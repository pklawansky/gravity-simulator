using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GravitySimulator.Sph;
using OpenTK.Graphics.OpenGL4;

namespace GravitySimulator.Planets
{
    /// <summary>
    /// Planet formation's Barnes-Hut gravity on the GPU (OpenGL 4.3 compute). The octree is still built on
    /// the CPU (<see cref="BarnesHutTree.Build"/>), flattened (<see cref="BarnesHutTree.Flatten"/>) and
    /// uploaded with the positions; planet_gravity.comp walks it per particle with the CPU tree's physics
    /// (same theta test, same Plummer softening) and the accelerations are read back.
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

        private readonly Thread _thread;
        private readonly ManualResetEventSlim _go = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _done = new ManualResetEventSlim(false);
        private readonly object _invokeLock = new object();
        private Action _job;
        private Exception _jobError;
        private volatile bool _stop, _broken;

        // GL-thread state.
        private HeadlessGlContext _ctx;
        private GlProgram _program;
        private GpuFlatTree _gpuTree;
        private int _posBuf, _accBuf, _particleCapacity;

        // CPU staging.
        private readonly FlatTree _flat = new FlatTree();
        private int[] _order = Array.Empty<int>();
        private float[] _pos = Array.Empty<float>(), _acc = Array.Empty<float>();
        private readonly Stopwatch _clock = new Stopwatch();

        /// <summary>The GL renderer string of the context (e.g. the GPU's name).</summary>
        public string Renderer { get; private set; } = "";
        /// <summary>CPU time of the last <see cref="Compute"/> spent flattening, packing and ordering (ms).</summary>
        public double LastFlattenMs { get; private set; }
        /// <summary>Wall time of the last <see cref="Compute"/> on the GL thread: upload, walk, read-back (ms).</summary>
        public double LastGpuMs { get; private set; }
        /// <summary>
        /// The particle order of the last <see cref="Compute"/> (first <c>ps.Count</c> entries): the tree's walk
        /// order, a permutation of the particle indices that is spatially coherent. Overwritten by the next call.
        /// </summary>
        public int[] LastOrder => _order;
        /// <summary>CPU time of the last <see cref="Compute"/> scattering the accelerations back (ms).</summary>
        public double LastScatterMs { get; private set; }
        /// <summary>Of <see cref="LastFlattenMs"/>: <see cref="BarnesHutTree.Flatten"/> alone (ms); the rest is packing.</summary>
        public double LastTreeFlattenMs { get; private set; }
        /// <summary>Of <see cref="LastGpuMs"/>: positions + tree upload (to glFinish), the walk kernel, and the read-back (ms).</summary>
        public double LastUploadMs { get; private set; }
        public double LastKernelMs { get; private set; }
        public double LastReadbackMs { get; private set; }

        private GpuGravity()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "PlanetGpuGravity" };
        }

        /// <summary>
        /// Starts the GL thread and builds the context and shader; returns null (with the reason in
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
                _program = GlProgram.Compute("planet_gravity.comp");
                _gpuTree = new GpuFlatTree();
                _posBuf = GL.GenBuffer();
                _accBuf = GL.GenBuffer();
                EnsureParticleCapacity(1024);
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
                _program?.Dispose();
                _gpuTree?.Dispose();
                if (_posBuf != 0) GL.DeleteBuffer(_posBuf);
                if (_accBuf != 0) GL.DeleteBuffer(_accBuf);
            }
            catch { /* context already gone */ }
            _program = null; _gpuTree = null; _posBuf = _accBuf = 0;
            _ctx?.Dispose();
            _ctx = null;
        }

        private void EnsureParticleCapacity(int n)
        {
            if (n <= _particleCapacity) return;
            _particleCapacity = Math.Max(n, _particleCapacity * 3 / 2);
            foreach (int b in new[] { _posBuf, _accBuf })
            {
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, b);
                GL.BufferData(BufferTarget.ShaderStorageBuffer, _particleCapacity * 16, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            }
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
        /// Writes the gravitational acceleration of every particle of <paramref name="ps"/> (indices below
        /// <c>ps.Count</c>) into <c>ps.Acceleration</c>, from <paramref name="tree"/>, which must have been
        /// built from <paramref name="ps"/> with the given theta and softening. Throws on a GL failure
        /// (the caller falls back to the CPU tree).
        /// </summary>
        public void Compute(BarnesHutTree tree, ParticleSystem ps, float theta, float softening)
        {
            int n = ps.Count;
            if (n == 0) return;
            _clock.Restart();
            if (_order.Length < n) _order = new int[ps.Capacity];
            int ordered = tree.Flatten(_flat, null, _order);
            LastTreeFlattenMs = _clock.Elapsed.TotalMilliseconds;
            // Bodies left out of the tree (mass <= 0) still get an acceleration, as on the CPU path.
            if (ordered < n)
            {
                float[] mass = ps.Mass;
                for (int i = 0; i < n; i++) if (mass[i] <= 0f) _order[ordered++] = i;
            }
            if (ordered != n) throw new InvalidOperationException($"tree order covers {ordered} of {n} bodies");
            if (_pos.Length < n * 4) { _pos = new float[ps.Capacity * 4]; _acc = new float[ps.Capacity * 4]; }
            var x = ps.Position;
            int[] order = _order;
            float[] pos = _pos;
            For(n, (s, e) =>
            {
                for (int k = s; k < e; k++)
                {
                    var p = x[order[k]];
                    int o = k * 4;
                    pos[o] = p.X; pos[o + 1] = p.Y; pos[o + 2] = p.Z; pos[o + 3] = 0f;
                }
            });
            var gpuTree = _gpuTree;
            gpuTree.Pack(_flat);
            LastFlattenMs = _clock.Elapsed.TotalMilliseconds;

            _clock.Restart();
            int nodes = _flat.Count;
            double upload = 0, kernel = 0;
            Invoke(() =>
            {
                long g0 = Stopwatch.GetTimestamp();
                EnsureParticleCapacity(n);
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _posBuf);
                GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, n * 16, _pos);
                gpuTree.Upload();
                GL.Finish();
                long g1 = Stopwatch.GetTimestamp();
                var g = _program;
                g.Set("uCount", (uint)n);
                g.Set("uNodeCount", (uint)nodes);
                g.Set("uTheta2", theta * theta);
                g.Set("uEps2", softening * softening);
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, _posBuf);
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _accBuf);
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, gpuTree.NodeA);
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, gpuTree.NodeB);
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, gpuTree.NodeC);
                g.Dispatch(n, WG);
                GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
                GL.Finish();
                long g2 = Stopwatch.GetTimestamp();
                upload = (g1 - g0) * 1000.0 / Stopwatch.Frequency;
                kernel = (g2 - g1) * 1000.0 / Stopwatch.Frequency;
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _accBuf);
                GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, n * 16, _acc);
                var err = GL.GetError();
                if (err != ErrorCode.NoError) throw new InvalidOperationException("GL error " + err);
            });
            LastGpuMs = _clock.Elapsed.TotalMilliseconds;
            LastUploadMs = upload;
            LastKernelMs = kernel;
            LastReadbackMs = Math.Max(0, LastGpuMs - upload - kernel);

            _clock.Restart();
            var a = ps.Acceleration;
            float[] acc = _acc;
            For(n, (s, e) =>
            {
                for (int k = s; k < e; k++)
                {
                    int o = k * 4;
                    a[order[k]] = new System.Numerics.Vector3(acc[o], acc[o + 1], acc[o + 2]);
                }
            });
            LastScatterMs = _clock.Elapsed.TotalMilliseconds;
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
