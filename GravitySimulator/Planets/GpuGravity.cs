using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GravitySimulator.Sph;
using OpenTK.Graphics.OpenGL4;

namespace GravitySimulator.Planets
{
    /// <summary>
    /// Planet formation's Barnes-Hut gravity on the GPU (OpenGL 4.3 compute), with the octree built on the GPU
    /// too, so it never round-trips through the CPU. Per call: the CPU finds the bounding cube (the CPU tree's
    /// formula) and uploads the bodies (position, mass); the GPU gives each body a 60-bit Morton key
    /// (planet_keys.comp, the CPU tree's own octant test), radix-sorts the keys (LSD, 4-bit digits,
    /// radix_count/radix_scatter + the shared scan), builds the octree top-down one level per dispatch from
    /// the sorted keys (planet_tree_split.comp; Karras 2012, Bédorf et al. 2012 "Bonsai"), sums the moments
    /// bottom-up (planet_tree_moments.comp), lays the tree out in stackless pre-order
    /// (planet_tree_count/emit.comp) and walks it per body (planet_gravity.comp) with the CPU tree's physics
    /// (same theta test, same Plummer softening, self skipped). The accelerations and the key order are read back.
    /// <para>
    /// Leaves hold up to <see cref="LeafCapacity"/> bodies; a leaf the walk does not accept whole is summed body
    /// by body, so the walk is never less accurate than the CPU tree's (every node it accepts is a node of the
    /// CPU tree with the same cube; where the CPU tree would open further, the GPU sums exactly). At
    /// <see cref="LeafCapacity"/> = 1 it is the CPU tree (to depth 20).
    /// </para>
    /// <para>
    /// All GL work runs on a private thread that owns a hidden GL 4.3 context (<see cref="HeadlessGlContext"/>),
    /// so <see cref="Compute"/> may be called from any thread (the simulation's background thread, a test, or
    /// the UI thread on Load); calls are serialised. Not deterministic across drivers: tests that pin
    /// golden hashes use the CPU path.
    /// </para>
    /// </summary>
    public sealed class GpuGravity : IDisposable
    {
        private const int WG = 128;
        private const int TimeoutMs = 30_000;
        private const int ParallelThreshold = 16_384;
        private const int MaxLevel = 20;
        private const int ScanGroup = 2048;

        private readonly Thread _thread;
        private readonly ManualResetEventSlim _go = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _done = new ManualResetEventSlim(false);
        private readonly object _invokeLock = new object();
        private Action _job;
        private Exception _jobError;
        private volatile bool _stop, _broken;

        // GL-thread state.
        private HeadlessGlContext _ctx;
        private GlProgram _walk, _keys, _radixCount, _radixScatter, _gather, _split, _advance, _moments, _count, _emit,
            _scanLocal, _scanAdd, _clear;
        private int _bodyIn, _bodySorted, _accBuf, _hiA, _loA, _valA, _hiB, _loB, _valB, _hist, _histScan, _blockSums,
            _cnt, _base, _lmin, _nInfo, _nGeom, _nMom, _nodes, _ctrl, _args, _scanTotal;
        private int _particleCapacity, _nodeCapacity;

        // CPU staging.
        private int[] _order = Array.Empty<int>();
        private float[] _pos = Array.Empty<float>(), _acc = Array.Empty<float>();
        private readonly uint[] _ctrlOut = new uint[2];
        private readonly Stopwatch _clock = new Stopwatch();

        /// <summary>The GL renderer string of the context (e.g. the GPU's name).</summary>
        public string Renderer { get; private set; } = "";
        /// <summary>Bodies per octree leaf at most (except at the deepest level). Measured best: see the spec.</summary>
        public int LeafCapacity { get; set; } = 8;
        /// <summary>CPU time of the last <see cref="Compute"/> finding the bounds and packing the bodies (ms).</summary>
        public double LastFlattenMs { get; private set; }
        /// <summary>Wall time of the last <see cref="Compute"/> on the GL thread: upload, build, walk, read-back (ms).</summary>
        public double LastGpuMs { get; private set; }
        /// <summary>Of <see cref="LastGpuMs"/>: upload (to glFinish), tree build, walk kernel, and the read-back (ms).</summary>
        public double LastUploadMs { get; private set; }
        public double LastBuildMs { get; private set; }
        public double LastKernelMs { get; private set; }
        public double LastReadbackMs { get; private set; }
        /// <summary>Octree nodes of the last build.</summary>
        public int LastNodeCount { get; private set; }
        /// <summary>
        /// The particle order of the last <see cref="Compute"/> (first <c>ps.Count</c> entries): Morton (tree)
        /// order, a permutation of the particle indices that is spatially coherent. Overwritten by the next call.
        /// </summary>
        public int[] LastOrder => _order;
        /// <summary>CPU time of the last <see cref="Compute"/> scattering the accelerations back (ms).</summary>
        public double LastScatterMs { get; private set; }

        private GpuGravity()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "PlanetGpuGravity" };
        }

        /// <summary>
        /// Starts the GL thread and builds the context and shaders; returns null (with the reason in
        /// <paramref name="error"/>) when OpenGL 4.3 compute is not available. Never throws.
        /// </summary>
        public static GpuGravity TryCreate(out string error)
        {
            GpuGravity g = null;
            try
            {
                g = new GpuGravity();
                var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                g._thread.Start(ready);
                if (!ready.Task.Wait(TimeoutMs)) { g.Dispose(); error = "GL initialisation timed out"; return null; }
                error = ready.Task.Result;
                if (error == null) return g;
                g.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                g?.Dispose();
                error = ex.Message;
                return null;
            }
        }

        private void Run(object state)
        {
            var ready = (TaskCompletionSource<string>)state;
            try
            {
                _ctx = HeadlessGlContext.TryCreate(out string error);
                if (_ctx == null) { ready.TrySetResult(error ?? "no OpenGL 4.3 context"); return; }
                Renderer = _ctx.Renderer;
                _walk = GlProgram.Compute("planet_gravity.comp");
                _keys = GlProgram.Compute("planet_keys.comp");
                _radixCount = GlProgram.Compute("radix_count.comp");
                _radixScatter = GlProgram.Compute("radix_scatter.comp");
                _gather = GlProgram.Compute("planet_gather.comp");
                _split = GlProgram.Compute("planet_tree_split.comp");
                _advance = GlProgram.Compute("planet_tree_advance.comp");
                _moments = GlProgram.Compute("planet_tree_moments.comp");
                _count = GlProgram.Compute("planet_tree_count.comp");
                _emit = GlProgram.Compute("planet_tree_emit.comp");
                _scanLocal = GlProgram.Compute("scan_local.comp");
                _scanAdd = GlProgram.Compute("scan_add.comp");
                _clear = GlProgram.Compute("clear.comp");
                _ctrl = NewBuffer(64 * 4);
                _args = NewBuffer(22 * 3 * 4);
                _scanTotal = NewBuffer(16);
                EnsureParticleCapacity(1024);
                EnsureNodeCapacity(4096);
                var err = GL.GetError();
                if (err != ErrorCode.NoError) throw new InvalidOperationException("GL error during set-up: " + err);
            }
            catch (Exception ex)
            {
                ReleaseGl();
                ready.TrySetResult(ex.Message);
                return;
            }
            ready.TrySetResult(null);

            while (!_stop)
            {
                if (_go.Wait(50))
                {
                    _go.Reset();
                    if (_stop) break;
                    try { _job?.Invoke(); }
                    catch (Exception ex) { _jobError = ex; }
                    _job = null;
                    _done.Set();
                }
                PumpMessages(); // the hidden window lives on this thread; keep its message queue drained
            }
            ReleaseGl();
            _jobError = new ObjectDisposedException(nameof(GpuGravity));
            _done.Set(); // release a caller still waiting
        }

        private void ReleaseGl()
        {
            try
            {
                foreach (var p in new[] { _walk, _keys, _radixCount, _radixScatter, _gather, _split, _advance, _moments, _count, _emit, _scanLocal, _scanAdd, _clear })
                    p?.Dispose();
                foreach (int b in new[] { _bodyIn, _bodySorted, _accBuf, _hiA, _loA, _valA, _hiB, _loB, _valB, _hist, _histScan, _blockSums,
                                          _cnt, _base, _lmin, _nInfo, _nGeom, _nMom, _nodes, _ctrl, _args, _scanTotal })
                    if (b != 0) GL.DeleteBuffer(b);
            }
            catch { /* context already gone */ }
            _walk = _keys = _radixCount = _radixScatter = _gather = _split = _advance = _moments = _count = _emit = _scanLocal = _scanAdd = _clear = null;
            _bodyIn = _bodySorted = _accBuf = _hiA = _loA = _valA = _hiB = _loB = _valB = _hist = _histScan = _blockSums = 0;
            _cnt = _base = _lmin = _nInfo = _nGeom = _nMom = _nodes = _ctrl = _args = _scanTotal = 0;
            _ctx?.Dispose();
            _ctx = null;
        }

        private static int NewBuffer(int bytes)
        {
            int b = GL.GenBuffer();
            Resize(b, bytes);
            return b;
        }

        private static void Resize(int buffer, long bytes)
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, buffer);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)Math.Max(16, bytes), IntPtr.Zero, BufferUsageHint.DynamicDraw);
        }

        private static void Renew(ref int buffer, long bytes)
        {
            if (buffer == 0) buffer = GL.GenBuffer();
            Resize(buffer, bytes);
        }

        private void EnsureParticleCapacity(int n)
        {
            if (n <= _particleCapacity) return;
            int cap = Math.Max(n, _particleCapacity * 3 / 2);
            if ((cap + 1 + ScanGroup - 1) / ScanGroup > ScanGroup) throw new InvalidOperationException("too many bodies for the two-level scan");
            _particleCapacity = cap;
            Renew(ref _bodyIn, cap * 16L);
            Renew(ref _bodySorted, cap * 16L);
            Renew(ref _accBuf, cap * 16L);
            Renew(ref _hiA, cap * 4L); Renew(ref _loA, cap * 4L); Renew(ref _valA, cap * 4L);
            Renew(ref _hiB, cap * 4L); Renew(ref _loB, cap * 4L); Renew(ref _valB, cap * 4L);
            int tiles = (cap + 255) / 256;
            Renew(ref _hist, 16L * tiles * 4);
            Renew(ref _histScan, 16L * tiles * 4);
            Renew(ref _cnt, (cap + 1) * 4L);
            Renew(ref _base, (cap + 1) * 4L);
            Renew(ref _lmin, (cap + 1) * 4L);
            Renew(ref _blockSums, ScanGroup * 4L);
        }

        private void EnsureNodeCapacity(int nodes)
        {
            if (nodes <= _nodeCapacity) return;
            _nodeCapacity = nodes;
            Renew(ref _nInfo, nodes * 16L);
            Renew(ref _nGeom, nodes * 16L);
            Renew(ref _nMom, nodes * 16L);
            Renew(ref _nodes, nodes * 48L);
        }

        // Runs a job on the GL thread and waits for it; rethrows its failure.
        private void Invoke(Action job)
        {
            lock (_invokeLock)
            {
                if (_broken || _stop || !_thread.IsAlive) throw new InvalidOperationException("GPU gravity is not running.");
                _jobError = null;
                _done.Reset();
                _job = job;
                _go.Set();
                if (!_done.Wait(TimeoutMs)) { _broken = true; throw new TimeoutException("GPU gravity did not finish within 30 s."); }
                if (_jobError != null) { _broken = true; throw new InvalidOperationException("GPU gravity failed: " + _jobError.Message, _jobError); }
            }
        }

        /// <summary>
        /// The CPU tree's root cube for these bodies (<see cref="BarnesHutTree.Build"/>): centre = middle of the
        /// bounding box of every body, half = 1.0001 x half the longest side + 1e-6.
        /// </summary>
        public static Vector4 RootCube(ParticleSystem ps)
        {
            int n = ps.Count;
            if (n == 0) return new Vector4(0f, 0f, 0f, 1e-6f);
            Vector3[] x = ps.Position;
            Vector3 min = new Vector3(float.MaxValue), max = new Vector3(float.MinValue);
            object gate = new object();
            For(n, (s, e) =>
            {
                Vector3 lo = new Vector3(float.MaxValue), hi = new Vector3(float.MinValue);
                for (int i = s; i < e; i++) { lo = Vector3.Min(lo, x[i]); hi = Vector3.Max(hi, x[i]); }
                lock (gate) { min = Vector3.Min(min, lo); max = Vector3.Max(max, hi); }
            });
            Vector3 size = max - min;
            float half = MathF.Max(size.X, MathF.Max(size.Y, size.Z)) * 0.5f * 1.0001f + 1e-6f;
            return new Vector4((min + max) * 0.5f, half);
        }

        /// <summary>
        /// Writes the gravitational acceleration of every particle of <paramref name="ps"/> (indices below
        /// <c>ps.Count</c>) into <c>ps.Acceleration</c>, building the octree on the GPU, and leaves the Morton
        /// order in <see cref="LastOrder"/>. Massless bodies are in the tree with no mass (they feel gravity and
        /// exert none). Throws on a GL failure (the caller falls back to the CPU tree).
        /// </summary>
        public void Compute(ParticleSystem ps, float theta, float softening)
        {
            int n = ps.Count;
            if (n == 0) return;
            _clock.Restart();
            Vector4 root = RootCube(ps);
            if (_pos.Length < n * 4) { _pos = new float[ps.Capacity * 4]; _acc = new float[ps.Capacity * 4]; }
            if (_order.Length < n) _order = new int[ps.Capacity];
            var x = ps.Position;
            float[] mass = ps.Mass, pos = _pos;
            For(n, (s, e) =>
            {
                for (int k = s; k < e; k++)
                {
                    var p = x[k];
                    int o = k * 4;
                    pos[o] = p.X; pos[o + 1] = p.Y; pos[o + 2] = p.Z; pos[o + 3] = Math.Max(mass[k], 0f);
                }
            });
            LastFlattenMs = _clock.Elapsed.TotalMilliseconds;

            _clock.Restart();
            int leafCap = Math.Max(1, LeafCapacity);
            double upload = 0, build = 0, kernel = 0;
            for (int attempt = 0; ; attempt++)
            {
                Invoke(() =>
                {
                    long g0 = Stopwatch.GetTimestamp();
                    EnsureParticleCapacity(n);
                    EnsureNodeCapacity(2 * n / leafCap + n / 2 + 4096); // measured: 1.46 n nodes at leaf 1, 0.39 n at 8
                    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _bodyIn);
                    GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, n * 16, _pos);
                    GL.Finish();
                    long g1 = Stopwatch.GetTimestamp();
                    BuildTree(n, root, leafCap);
                    GL.Finish();
                    long g2 = Stopwatch.GetTimestamp();
                    var w = _walk;
                    w.Set("uCount", (uint)n);
                    w.Set("uNodeCap", (uint)_nodeCapacity);
                    w.Set("uTheta2", theta * theta);
                    w.Set("uEps2", softening * softening);
                    Bind(_bodySorted, _accBuf, _nodes, _ctrl);
                    w.Dispatch(n, WG);
                    GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
                    GL.Finish();
                    long g3 = Stopwatch.GetTimestamp();
                    double f = 1000.0 / Stopwatch.Frequency;
                    upload = (g1 - g0) * f; build = (g2 - g1) * f; kernel = (g3 - g2) * f;
                    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ctrl);
                    GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, 8, _ctrlOut);
                    if (_ctrlOut[1] != 0)
                    {
                        EnsureNodeCapacity(Math.Max(_nodeCapacity * 2, (int)Math.Min(int.MaxValue / 64, (long)_ctrlOut[0] * 5 / 4)));
                        return; // overflowed: the caller tries again with the larger node buffers
                    }
                    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _accBuf);
                    GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, n * 16, _acc);
                    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _valA);
                    GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, n * 4, _order);
                    var err = GL.GetError();
                    if (err != ErrorCode.NoError) throw new InvalidOperationException("GL error " + err);
                });
                if (_ctrlOut[1] == 0) break;
                if (attempt >= 8) throw new InvalidOperationException("octree node buffer kept overflowing");
            }
            LastNodeCount = (int)_ctrlOut[0];
            LastGpuMs = _clock.Elapsed.TotalMilliseconds;
            LastUploadMs = upload;
            LastBuildMs = build;
            LastKernelMs = kernel;
            LastReadbackMs = Math.Max(0, LastGpuMs - upload - build - kernel);

            _clock.Restart();
            var a = ps.Acceleration;
            float[] acc = _acc;
            int[] order = _order;
            For(n, (s, e) =>
            {
                for (int k = s; k < e; k++)
                {
                    int o = k * 4;
                    a[order[k]] = new Vector3(acc[o], acc[o + 1], acc[o + 2]);
                }
            });
            LastScatterMs = _clock.Elapsed.TotalMilliseconds;
        }

        // ---- GPU octree build (GL thread) ----------------------------------------------------------

        private static void Bind(params int[] buffers)
        {
            for (int k = 0; k < buffers.Length; k++) GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, k, buffers[k]);
        }

        private static void Barrier() =>
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.CommandBarrierBit);

        private void BuildTree(int n, Vector4 root, int leafCap)
        {
            // Keys (and the identity order), then the radix sort: 8 passes on the low word, 8 on the high word.
            _keys.Set("uCount", (uint)n);
            GL.ProgramUniform4(_keys.Handle, _keys.Location("uRoot"), root.X, root.Y, root.Z, root.W);
            Bind(_bodyIn, _hiA, _loA, _valA);
            _keys.Dispatch(n, 256);
            Barrier();
            int tiles = (n + 255) / 256;
            int hiS = _hiA, loS = _loA, valS = _valA, hiD = _hiB, loD = _loB, valD = _valB;
            for (int pass = 0; pass < 16; pass++)
            {
                uint word = pass < 8 ? 0u : 1u;
                uint shift = (uint)(4 * (pass & 7));
                _radixCount.Set("uCount", (uint)n);
                _radixCount.Set("uShift", shift);
                _radixCount.Set("uTiles", (uint)tiles);
                Bind(word == 0 ? loS : hiS, _hist);
                _radixCount.Dispatch(n, 256);
                Barrier();
                Scan(_hist, _histScan, 16 * tiles);
                _radixScatter.Set("uCount", (uint)n);
                _radixScatter.Set("uShift", shift);
                _radixScatter.Set("uWord", word);
                _radixScatter.Set("uTiles", (uint)tiles);
                Bind(hiS, loS, valS, hiD, loD, valD, _histScan);
                _radixScatter.Dispatch(n, 256);
                Barrier();
                (hiS, hiD) = (hiD, hiS); (loS, loD) = (loD, loS); (valS, valD) = (valD, valS);
            }
            // 16 passes: the sorted keys and order are back in the A buffers.
            _gather.Set("uCount", (uint)n);
            Bind(_valA, _bodyIn, _bodySorted);
            _gather.Dispatch(n, 256);

            // Root node and the level bookkeeping.
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ctrl);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, 16, new uint[] { 1, 0, 0, 1 });
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _args);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, 12, new uint[] { 1, 1, 1 });
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _nInfo);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, 16, new uint[] { 0, (uint)n, 0, 0 });
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _nGeom);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, 16, new[] { root.X, root.Y, root.Z, root.W });
            Barrier();

            // Top-down split, one level per (indirect) dispatch.
            GL.BindBuffer(BufferTarget.DispatchIndirectBuffer, _args);
            _split.Set("uLeafCap", (uint)leafCap);
            _split.Set("uNodeCap", (uint)_nodeCapacity);
            _advance.Set("uNodeCap", (uint)_nodeCapacity);
            for (int level = 0; level <= MaxLevel; level++)
            {
                _split.Set("uLevel", (uint)level);
                Bind(_hiA, _loA, _nInfo, _nGeom, _ctrl);
                GL.UseProgram(_split.Handle);
                GL.DispatchComputeIndirect((IntPtr)(level * 12));
                Barrier();
                if (level == MaxLevel) break;
                _advance.Set("uLevel", (uint)level);
                Bind(_ctrl, _args);
                GL.UseProgram(_advance.Handle);
                GL.DispatchCompute(1, 1, 1);
                Barrier();
            }

            // Bottom-up moments.
            _moments.Set("uCount", (uint)n);
            Bind(_bodySorted, _nInfo, _nGeom, _nMom, _ctrl);
            GL.UseProgram(_moments.Handle);
            for (int level = MaxLevel; level >= 0; level--)
            {
                _moments.Set("uLevel", (uint)level);
                GL.DispatchComputeIndirect((IntPtr)(level * 12));
                Barrier();
            }

            // Pre-order layout: nodes per first body, scan, emit.
            Clear(_cnt, n + 1, 0u);
            Clear(_lmin, n + 1, uint.MaxValue);
            Barrier();
            _count.Set("uNodeCap", (uint)_nodeCapacity);
            _count.Set("uCount", (uint)n);
            Bind(_nInfo, _cnt, _lmin, _ctrl);
            GL.UseProgram(_count.Handle);
            GL.DispatchComputeIndirect((IntPtr)(21 * 12));
            Barrier();
            Scan(_cnt, _base, n + 1);
            _emit.Set("uNodeCap", (uint)_nodeCapacity);
            _emit.Set("uCount", (uint)n);
            Bind(_nInfo, _nGeom, _nMom, _base, _lmin, _nodes, _ctrl);
            GL.UseProgram(_emit.Handle);
            GL.DispatchComputeIndirect((IntPtr)(21 * 12));
            Barrier();
        }

        private void Clear(int buffer, int count, uint value)
        {
            _clear.Set("uN", (uint)count);
            _clear.Set("uValue", value);
            Bind(buffer);
            _clear.Dispatch(count, 256);
        }

        // Exclusive prefix sum of count uints (count <= 2048 * 2048), the SPH solver's two-level scan.
        private void Scan(int input, int output, int count)
        {
            int blocks = (count + ScanGroup - 1) / ScanGroup;
            _scanLocal.Set("uN", (uint)count);
            Bind(input, output, _blockSums);
            GL.UseProgram(_scanLocal.Handle);
            GL.DispatchCompute(blocks, 1, 1);
            Barrier();
            if (blocks > 1)
            {
                _scanLocal.Set("uN", (uint)blocks);
                Bind(_blockSums, _blockSums, _scanTotal); // in place; the single total is not needed
                GL.UseProgram(_scanLocal.Handle);
                GL.DispatchCompute(1, 1, 1);
                Barrier();
                _scanAdd.Set("uN", (uint)count);
                Bind(output, _blockSums);
                _scanAdd.Dispatch(count, 256);
                Barrier();
            }
        }

        private static void For(int n, Action<int, int> range)
        {
            if (n < ParallelThreshold) { range(0, n); return; }
            int chunks = Environment.ProcessorCount * 2;
            int size = (n + chunks - 1) / chunks;
            Parallel.For(0, chunks, c => { int s = c * size; if (s < n) range(s, Math.Min(n, s + size)); });
        }

        public void Dispose()
        {
            if (_stop) return;
            _stop = true;
            _go.Set();
            if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(10));
        }

        // ---- message pump for the hidden window's thread ------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr Hwnd;
            public uint Message;
            public IntPtr WParam, LParam;
            public uint Time;
            public int X, Y;
            public uint Private;
        }

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);
        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Msg msg);
        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref Msg msg);

        private static void PumpMessages()
        {
            try
            {
                while (PeekMessage(out Msg m, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
                {
                    TranslateMessage(ref m);
                    DispatchMessage(ref m);
                }
            }
            catch { /* not on Windows: nothing to pump */ }
        }
    }
}
