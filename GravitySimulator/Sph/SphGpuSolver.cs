using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using GravitySimulator.Planets;
using OpenTK.Graphics.OpenGL4;

namespace GravitySimulator.Sph
{
    /// <summary>GPU time per phase of the last step (ms, from GL timestamp queries) and CPU time of the tree.</summary>
    public struct SphPhaseTimes
    {
        public double Integrate, Grid, Density, Force, Gravity, Sinks;
        /// <summary>CPU: waiting for the drifted positions, building + flattening the octree, uploading it.</summary>
        public double TreeWait, TreeBuild, TreeUpload;
        public double GpuTotal => Integrate + Grid + Density + Force + Gravity + Sinks;
    }

    /// <summary>
    /// SPH on the GPU (OpenGL 4.3 compute shaders, state in SSBOs). Requires a current 4.3 context on
    /// the calling thread for its whole life. Per step: kick + drift + h → hashed-grid counting sort
    /// (cell = 2 h_max) → predict → density/EOS → forces → [CPU octree from read-back positions,
    /// overlapping the GPU density/force passes] → tree gravity + softening correction → kick + Δt
    /// → sink accretion and candidates. Mirrors <see cref="SphReference"/>.
    /// </summary>
    public sealed class SphGpuSolver : IDisposable
    {
        private const int WG = 128;
        private const int EventCapacity = 1 << 16;
        private const int CandidateCapacity = 256;
        private const int SinkCapacity = 4096;
        private const int Q = 9; // timestamp queries per step

        private sealed class Pair
        {
            public int Front, Back;
            public void Swap() => (Front, Back) = (Back, Front);
        }

        private readonly SphParameters _p;
        private readonly int _tableSize, _scanN, _scanBlocks;
        private readonly GlProgram _clear, _kickDrift, _hash, _scanLocal, _scanAdd, _permute, _predict,
            _density, _force, _gravity, _zeta, _kick, _accrete, _candidates, _inject, _energy, _signatures;
        private readonly Pair _posH, _vel, _acc, _massId;
        private readonly int _rhoP, _extra, _vpred, _xi, _key, _rank, _cellCount, _cellStart, _blockSums,
            _ctrl, _nodeA, _nodeB, _nodeC, _sinks, _events, _cands, _partials, _sig, _stagePos, _stageMass;
        private int _nodeCapacity;
        private int _alive;
        private int _nextId;
        private IntPtr _stageFence;

        // CPU tree.
        private readonly BarnesHutTree _tree;
        private readonly ParticleSystem _treePs;
        private float[] _soft;
        private readonly FlatTree _flat = new FlatTree();
        private float[] _stagePosData, _stageMassData;
        private float[] _nodeAData = Array.Empty<float>(), _nodeBData = Array.Empty<float>(), _nodeCData = Array.Empty<float>();
        private Vector3[] _sinkAcc = Array.Empty<Vector3>();
        private float[] _sinkPhi = Array.Empty<float>();
        private float[] _sinkData = new float[SinkCapacity * 8];
        private readonly float[] _eventData = new float[EventCapacity * 8];
        private readonly float[] _candData = new float[CandidateCapacity * 8];
        private readonly List<AccretionEvent> _eventList = new List<AccretionEvent>();
        private readonly List<SinkCandidate> _candList = new List<SinkCandidate>();
        private readonly uint[] _ctrlData = new uint[4];

        private readonly int[] _queries = new int[Q];
        private readonly Stopwatch _cpu = new Stopwatch();

        public SphGpuSolver(SphParameters parameters, int capacity)
        {
            _p = parameters ?? throw new ArgumentNullException(nameof(parameters));
            Capacity = Math.Max(capacity, 1);
            _tableSize = 2048;
            while (_tableSize < 2 * Capacity) _tableSize <<= 1;
            _scanN = _tableSize + 2;
            _scanBlocks = (_scanN + 2047) / 2048;
            if (_scanBlocks > 2048) throw new ArgumentOutOfRangeException(nameof(capacity), "Too many particles for the two-level scan.");

            Renderer = GL.GetString(StringName.Renderer) ?? "";
            _clear = GlProgram.Compute("clear.comp");
            _kickDrift = GlProgram.Compute("kick_drift.comp");
            _hash = GlProgram.Compute("hash.comp");
            _scanLocal = GlProgram.Compute("scan_local.comp");
            _scanAdd = GlProgram.Compute("scan_add.comp");
            _permute = GlProgram.Compute("permute.comp");
            _predict = GlProgram.Compute("predict.comp");
            _density = GlProgram.Compute("density.comp");
            _force = GlProgram.Compute("force.comp");
            _gravity = GlProgram.Compute("gravity.comp");
            _zeta = GlProgram.Compute("zeta.comp");
            _kick = GlProgram.Compute("kick.comp");
            _accrete = GlProgram.Compute("accrete.comp");
            _candidates = GlProgram.Compute("sink_candidates.comp");
            _inject = GlProgram.Compute("inject.comp");
            _energy = GlProgram.Compute("energy.comp");
            _signatures = GlProgram.Compute("signatures.comp");

            int v4 = Capacity * 16;
            _posH = NewPair(v4);
            _vel = NewPair(v4);
            _acc = NewPair(v4);
            _massId = NewPair(v4);
            _rhoP = NewBuffer(v4);
            _extra = NewBuffer(v4);
            _vpred = NewBuffer(v4);
            _xi = NewBuffer(Capacity * 4);
            _key = NewBuffer(Capacity * 4);
            _rank = NewBuffer(Capacity * 4);
            _cellCount = NewBuffer(_scanN * 4);
            _cellStart = NewBuffer(_scanN * 4);
            _blockSums = NewBuffer(2048 * 4);
            _ctrl = NewBuffer(64);
            _nodeA = NewBuffer(16);
            _nodeB = NewBuffer(16);
            _nodeC = NewBuffer(16);
            _sinks = NewBuffer(SinkCapacity * 32);
            _events = NewBuffer(EventCapacity * 32);
            _cands = NewBuffer(CandidateCapacity * 32);
            _partials = NewBuffer(((Capacity + 255) / 256) * 16);
            _sig = NewBuffer(v4);
            _stagePos = NewBuffer(v4, BufferUsageHint.StreamRead);
            _stageMass = NewBuffer(v4, BufferUsageHint.StreamRead);
            GL.GenQueries(Q, _queries);

            _tree = new BarnesHutTree(Capacity + 64) { Theta = _p.Theta };
            _treePs = new ParticleSystem(Capacity + 64);
            _soft = new float[Capacity + 64];
            _stagePosData = new float[Capacity * 4];
            _stageMassData = new float[Capacity * 4];
        }

        public SphParameters Parameters => _p;
        public int Capacity { get; }
        /// <summary>Particles in the GPU arrays (live, plus any absorbed this step until the next sort).</summary>
        public int Count { get; private set; }
        /// <summary>Live gas particles.</summary>
        public int Alive => _alive;
        public float Dt { get; set; }
        public double Time { get; private set; }
        public long Steps { get; private set; }
        public string Renderer { get; }
        public SphPhaseTimes Times { get; private set; }
        /// <summary>Enables the GL timestamp queries (small cost: they are read at the end of the step).</summary>
        public bool MeasurePhases { get; set; } = true;
        /// <summary>Velocity factor applied after each closing kick (1 = none; below 1 to relax initial conditions).</summary>
        public float Damping { get; set; } = 1f;

        // Buffers for rendering (current order): xyz + h, (rho, P, c, balsara), (mass, id).
        public int PositionBuffer => _posH.Front;
        public int DensityBuffer => _rhoP;
        public int MassBuffer => _massId.Front;
        public int VelocityBuffer => _vel.Front;

        // ---- state transfer -----------------------------------------------------------------------

        public void Upload(SphState s)
        {
            if (s.Count > Capacity) throw new ArgumentException("State exceeds the solver capacity.");
            int n = s.Count;
            var pos = new float[n * 4];
            var vel = new float[n * 4];
            var acc = new float[n * 4];
            var mid = new float[n * 4];
            var rho = new float[n * 4];
            _nextId = 0;
            for (int i = 0; i < n; i++)
            {
                int k = i * 4;
                pos[k] = s.Position[i].X; pos[k + 1] = s.Position[i].Y; pos[k + 2] = s.Position[i].Z; pos[k + 3] = s.H[i];
                vel[k] = s.Velocity[i].X; vel[k + 1] = s.Velocity[i].Y; vel[k + 2] = s.Velocity[i].Z; vel[k + 3] = s.U[i];
                acc[k] = s.Acceleration[i].X; acc[k + 1] = s.Acceleration[i].Y; acc[k + 2] = s.Acceleration[i].Z; acc[k + 3] = s.DuDt[i];
                mid[k] = s.Mass[i]; mid[k + 1] = BitConverter.Int32BitsToSingle(s.Id[i]);
                rho[k] = s.Density[i];
                _nextId = Math.Max(_nextId, s.Id[i] + 1);
            }
            Write(_posH.Front, pos, n * 4);
            Write(_vel.Front, vel, n * 4);
            Write(_acc.Front, acc, n * 4);
            Write(_massId.Front, mid, n * 4);
            Write(_rhoP, rho, n * 4);
            Write(_extra, new float[n * 4], n * 4);
            Count = n;
            _alive = 0;
            for (int i = 0; i < n; i++) if (s.Mass[i] > 0f) _alive++;
            Time = 0;
            Steps = 0;
            Dt = 0f;
        }

        /// <summary>Copies the GPU state into <paramref name="s"/> in the current GPU order (see <see cref="SphState.Id"/>).</summary>
        public void Download(SphState s)
        {
            int n = Count;
            if (s.Capacity < n) throw new ArgumentException("State too small.");
            GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
            float[] pos = Read(_posH.Front, n * 4), vel = Read(_vel.Front, n * 4), acc = Read(_acc.Front, n * 4),
                mid = Read(_massId.Front, n * 4), rho = Read(_rhoP, n * 4), ext = Read(_extra, n * 4);
            bool ideal = _p.Eos == EquationOfState.IdealGas;
            for (int i = 0; i < n; i++)
            {
                int k = i * 4;
                s.Position[i] = new Vector3(pos[k], pos[k + 1], pos[k + 2]);
                s.H[i] = pos[k + 3];
                s.Velocity[i] = new Vector3(vel[k], vel[k + 1], vel[k + 2]);
                s.Acceleration[i] = new Vector3(acc[k], acc[k + 1], acc[k + 2]);
                s.DuDt[i] = acc[k + 3];
                s.Mass[i] = mid[k];
                s.Id[i] = BitConverter.SingleToInt32Bits(mid[k + 1]);
                s.Density[i] = rho[k];
                s.Pressure[i] = rho[k + 1];
                s.SoundSpeed[i] = rho[k + 2];
                s.Balsara[i] = rho[k + 3];
                s.U[i] = ideal || rho[k] <= 0f ? vel[k + 3] : 1.5f * rho[k + 1] / rho[k];
                s.DivV[i] = ext[k];
                s.CurlV[i] = ext[k + 1];
                s.MuMax[i] = ext[k + 2];
                s.Potential[i] = ext[k + 3];
            }
            s.Count = n;
        }

        /// <summary>
        /// Appends gas particles (e.g. stellar ejecta) after the current ones. Their density starts
        /// from h. Returns how many fitted.
        /// </summary>
        public int Append(IReadOnlyList<(Vector3 Position, Vector3 Velocity, float Mass, float U, float H)> particles)
        {
            int k = Math.Min(particles.Count, Capacity - Count);
            if (k <= 0) return 0;
            var pos = new float[k * 4];
            var vel = new float[k * 4];
            var mid = new float[k * 4];
            var rho = new float[k * 4];
            for (int i = 0; i < k; i++)
            {
                var q = particles[i];
                int o = i * 4;
                pos[o] = q.Position.X; pos[o + 1] = q.Position.Y; pos[o + 2] = q.Position.Z; pos[o + 3] = q.H;
                vel[o] = q.Velocity.X; vel[o + 1] = q.Velocity.Y; vel[o + 2] = q.Velocity.Z; vel[o + 3] = q.U;
                mid[o] = q.Mass; mid[o + 1] = BitConverter.Int32BitsToSingle(_nextId++);
                float d = q.H / _p.Eta;
                rho[o] = q.Mass / (d * d * d);
            }
            IntPtr offset = (IntPtr)(Count * 16);
            WriteAt(_posH.Front, offset, pos, k * 4);
            WriteAt(_vel.Front, offset, vel, k * 4);
            WriteAt(_acc.Front, offset, new float[k * 4], k * 4);
            WriteAt(_massId.Front, offset, mid, k * 4);
            WriteAt(_rhoP, offset, rho, k * 4);
            Count += k;
            _alive += k;
            return k;
        }

        /// <summary>Adds Δu (1 - r/R) to gas inside each blast (centre, radius R, Δu), at most 16 per call.</summary>
        public void InjectEnergy(IReadOnlyList<(Vector3 Centre, float Radius, float Du)> blasts)
        {
            int n = Math.Min(16, blasts.Count);
            if (n == 0 || Count == 0) return;
            var c = new float[n * 4];
            var du = new float[n];
            for (int k = 0; k < n; k++)
            {
                c[k * 4] = blasts[k].Centre.X; c[k * 4 + 1] = blasts[k].Centre.Y; c[k * 4 + 2] = blasts[k].Centre.Z; c[k * 4 + 3] = blasts[k].Radius;
                du[k] = blasts[k].Du;
            }
            _inject.Set("uCount", (uint)Count);
            _inject.Set("uBlastCount", n);
            _inject.SetArray4("uBlast", c, n);
            _inject.SetArray1("uBlastDu", du, n);
            Bind(_posH.Front, _vel.Front, _massId.Front);
            _inject.Dispatch(Count, WG);
            Barrier();
        }

        // ---- the step -----------------------------------------------------------------------------

        /// <summary>Iterates h and density, then forces and the first Δt (as <see cref="SphReference.Initialise"/>).</summary>
        public void Initialise(int iterations = 3, SinkManager sinks = null)
        {
            DensityPhase(0f, kick: false, updateH: false, stage: false);
            for (int it = 0; it < iterations; it++) DensityPhase(0f, kick: false, updateH: true, stage: false);
            Force();
            StageAndFence();
            BuildTree(sinks);
            GravityPhase();
            _acc.Swap();
            float dtGas = KickAndTimeStep(0f, kick: false, accFront: true);
            ReadCtrl();
            Dt = CombineTimeStep(BitConverter.UInt32BitsToSingle(_ctrlData[1]), sinks);
            _ = dtGas;
        }

        /// <summary>Test hook: density, forces and gravity of the uploaded state (no integration).</summary>
        public void ComputeDerivatives(float dtPredict)
        {
            DensityPhase(dtPredict, kick: false, updateH: false, stage: false);
            Force();
            StageAndFence();
            BuildTree(null);
            GravityPhase();
            _acc.Swap();
            GL.Finish();
        }

        /// <summary>One leapfrog step of Δt = <see cref="Dt"/>, including the sinks when given.</summary>
        public void Step(SinkManager sinks = null)
        {
            float dt = Dt, half = 0.5f * dt;
            bool timing = MeasurePhases;
            var times = new SphPhaseTimes();

            ParticleSystem sp = sinks?.Sinks;
            if (sp != null)
                for (int k = 0; k < sp.Count; k++)
                {
                    sp.Velocity[k] += sp.Acceleration[k] * half;
                    sp.Position[k] += sp.Velocity[k] * dt;
                }

            if (timing) GL.QueryCounter(_queries[0], QueryCounterTarget.Timestamp);
            ResetCtrl();
            KickDrift(dt, kick: true, updateH: true);
            if (timing) GL.QueryCounter(_queries[1], QueryCounterTarget.Timestamp);
            Sort();
            StageAndFence();
            if (timing) GL.QueryCounter(_queries[2], QueryCounterTarget.Timestamp);
            Predict(half);
            Density();
            if (timing) GL.QueryCounter(_queries[3], QueryCounterTarget.Timestamp);
            Force();
            if (timing) GL.QueryCounter(_queries[4], QueryCounterTarget.Timestamp);
            GL.Flush();

            BuildTree(sinks, ref times); // CPU, while the GPU runs density and forces

            if (timing) GL.QueryCounter(_queries[5], QueryCounterTarget.Timestamp);
            GravityPhase();
            if (timing) GL.QueryCounter(_queries[6], QueryCounterTarget.Timestamp);
            KickAndTimeStep(dt, kick: true, accFront: false);
            _acc.Swap();
            if (timing) GL.QueryCounter(_queries[7], QueryCounterTarget.Timestamp);

            if (sp != null)
            {
                for (int k = 0; k < sp.Count; k++)
                {
                    sp.Acceleration[k] = _sinkAcc[k];
                    sp.Velocity[k] += sp.Acceleration[k] * half;
                }
                AccreteAndFindCandidates(sinks);
            }
            if (timing) GL.QueryCounter(_queries[8], QueryCounterTarget.Timestamp);

            ReadCtrl();
            if (sinks != null) ApplySinkResults(sinks);
            Time += dt;
            Steps++;
            Dt = CombineTimeStep(BitConverter.UInt32BitsToSingle(_ctrlData[1]), sinks);

            if (timing)
            {
                long[] t = new long[Q];
                for (int k = 0; k < Q; k++) GL.GetQueryObject(_queries[k], GetQueryObjectParam.QueryResult, out t[k]);
                double Ms(int a, int b) => (t[b] - t[a]) * 1e-6;
                times.Integrate = Ms(0, 1) + Ms(6, 7);
                times.Grid = Ms(1, 2);
                times.Density = Ms(2, 3);
                times.Force = Ms(3, 4);
                times.Gravity = Ms(5, 6);
                times.Sinks = Ms(7, 8);
            }
            Times = times;
        }

        private float CombineTimeStep(float dtGas, SinkManager sinks)
        {
            float dt = MathF.Min(dtGas, _p.MaxTimeStep);
            var sp = sinks?.Sinks;
            if (sp != null)
            {
                float c = _p.Courant * _p.TimeScale;
                for (int k = 0; k < sp.Count; k++)
                {
                    float a = sp.Acceleration[k].Length();
                    if (a > 0f) dt = MathF.Min(dt, c * MathF.Sqrt(MathF.Max(sinks.AccretionRadius(k), 1e-12f) / a));
                }
            }
            return dt;
        }

        // ---- phases -------------------------------------------------------------------------------

        private void DensityPhase(float dtPredict, bool kick, bool updateH, bool stage)
        {
            ResetCtrl();
            KickDrift(0f, kick, updateH);
            Sort();
            if (stage) StageAndFence();
            Predict(dtPredict);
            Density();
        }

        private void ResetCtrl()
        {
            _ctrlData[0] = 0u;
            _ctrlData[1] = 0x7f7fffffu; // FLT_MAX
            _ctrlData[2] = 0u;
            _ctrlData[3] = 0u;
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ctrl);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, 16, _ctrlData);
        }

        private void KickDrift(float dt, bool kick, bool updateH)
        {
            var p = _kickDrift;
            p.Set("uCount", (uint)Count);
            p.Set("uDt", dt);
            p.Set("uKick", kick);
            p.Set("uUpdateH", updateH);
            p.Set("uIdeal", _p.Eos == EquationOfState.IdealGas);
            p.Set("uUMin", _p.MinInternalEnergy);
            p.Set("uEta", _p.Eta);
            p.Set("uHMin", _p.HMin);
            p.Set("uHMax", _p.HMax);
            p.Set("uCoolBeta", _p.CoolingBeta);
            p.Set("uCoolFloor", _p.CoolingFloor);
            Bind(_posH.Front, _vel.Front, _acc.Front, _massId.Front, _rhoP, _ctrl);
            p.Dispatch(Count, WG);
            Barrier();
        }

        private void Sort()
        {
            _clear.Set("uN", (uint)_scanN);
            _clear.Set("uValue", 0u);
            Bind(_cellCount);
            _clear.Dispatch(_scanN, 256);
            Barrier();

            _hash.Set("uCount", (uint)Count);
            _hash.Set("uTableMask", (uint)(_tableSize - 1));
            _hash.Set("uTableSize", (uint)_tableSize);
            Bind(_posH.Front, _massId.Front, _key, _rank, _cellCount, _ctrl);
            _hash.Dispatch(Count, WG);
            Barrier();

            _scanLocal.Set("uN", (uint)_scanN);
            Bind(_cellCount, _cellStart, _blockSums);
            GL.UseProgram(_scanLocal.Handle);
            GL.DispatchCompute(_scanBlocks, 1, 1);
            Barrier();
            if (_scanBlocks > 1)
            {
                _scanLocal.Set("uN", (uint)_scanBlocks);
                Bind(_blockSums, _blockSums, _cellCount); // in place; the (single) total is not needed
                GL.UseProgram(_scanLocal.Handle);
                GL.DispatchCompute(1, 1, 1);
                Barrier();
                _scanAdd.Set("uN", (uint)_scanN);
                Bind(_cellStart, _blockSums);
                _scanAdd.Dispatch(_scanN, 256);
                Barrier();
            }

            _permute.Set("uCount", (uint)Count);
            Bind(_key, _rank, _cellStart, _posH.Front, _posH.Back, _vel.Front, _vel.Back);
            _permute.Dispatch(Count, WG);
            Bind(_key, _rank, _cellStart, _acc.Front, _acc.Back, _massId.Front, _massId.Back);
            _permute.Dispatch(Count, WG);
            Barrier();
            _posH.Swap();
            _vel.Swap();
            _acc.Swap();
            _massId.Swap();
            Count = _alive; // absorbed particles were sorted to the end
        }

        private void StageAndFence()
        {
            GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
            int bytes = Count * 16;
            if (bytes > 0)
            {
                Copy(_posH.Front, _stagePos, bytes);
                Copy(_massId.Front, _stageMass, bytes);
            }
            if (_stageFence != IntPtr.Zero) GL.DeleteSync(_stageFence);
            _stageFence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
        }

        private void Predict(float dtPredict)
        {
            _predict.Set("uCount", (uint)Count);
            _predict.Set("uDtPredict", dtPredict);
            _predict.Set("uIdeal", _p.Eos == EquationOfState.IdealGas);
            _predict.Set("uUMin", _p.MinInternalEnergy);
            Bind(_vel.Front, _acc.Front, _vpred);
            _predict.Dispatch(Count, WG);
            Barrier();
        }

        private void Density()
        {
            var p = _density;
            SetGrid(p);
            p.Set("uIdeal", _p.Eos == EquationOfState.IdealGas);
            p.Set("uGamma", _p.Gamma);
            p.Set("uCs2", _p.SoundSpeed * _p.SoundSpeed);
            p.Set("uRhoCrit", _p.CriticalDensity);
            p.Set("uSelfCorrection", SphKernel.DensitySelfCorrection(_p.Eta));
            Bind(_posH.Front, _vpred, _massId.Front, _cellStart, _rhoP, _extra, _ctrl);
            p.Dispatch(Count, WG);
            Barrier();
        }

        private void Force()
        {
            var p = _force;
            SetGrid(p);
            p.Set("uIdeal", _p.Eos == EquationOfState.IdealGas);
            p.Set("uAlpha", _p.Alpha);
            p.Set("uBeta", _p.Beta);
            Bind(_posH.Front, _vpred, _massId.Front, _cellStart, _rhoP, _extra, _acc.Back, _ctrl);
            p.Dispatch(Count, WG);
            Barrier();
        }

        private void SetGrid(GlProgram p)
        {
            p.Set("uCount", (uint)Count);
            p.Set("uTableMask", (uint)(_tableSize - 1));
            p.Set("uTableSize", (uint)_tableSize);
        }

        private void BuildTree(SinkManager sinks)
        {
            var t = new SphPhaseTimes();
            BuildTree(sinks, ref t);
        }

        // Reads back the drifted positions, builds the octree on the CPU (gas + sinks), uploads it, and
        // computes the sinks' accelerations and potentials from it.
        private void BuildTree(SinkManager sinks, ref SphPhaseTimes times)
        {
            var sp = sinks?.Sinks;
            int s = sp?.Count ?? 0;
            if (_sinkAcc.Length < s) { _sinkAcc = new Vector3[Math.Max(s, 16)]; _sinkPhi = new float[Math.Max(s, 16)]; }
            if (!_p.SelfGravity)
            {
                for (int k = 0; k < s; k++) { _sinkAcc[k] = Vector3.Zero; _sinkPhi[k] = 0f; }
                return;
            }
            int n = Count;
            _cpu.Restart();
            if (_stageFence != IntPtr.Zero)
            {
                while (true)
                {
                    var r = GL.ClientWaitSync(_stageFence, ClientWaitSyncFlags.SyncFlushCommandsBit, 1_000_000_000L);
                    if (r == WaitSyncStatus.AlreadySignaled || r == WaitSyncStatus.ConditionSatisfied) break;
                    if (r == WaitSyncStatus.WaitFailed) throw new InvalidOperationException("glClientWaitSync failed.");
                }
                GL.DeleteSync(_stageFence);
                _stageFence = IntPtr.Zero;
            }
            if (n > 0)
            {
                GL.BindBuffer(BufferTarget.CopyReadBuffer, _stagePos);
                GL.GetBufferSubData(BufferTarget.CopyReadBuffer, IntPtr.Zero, n * 16, _stagePosData);
                GL.BindBuffer(BufferTarget.CopyReadBuffer, _stageMass);
                GL.GetBufferSubData(BufferTarget.CopyReadBuffer, IntPtr.Zero, n * 16, _stageMassData);
            }
            times.TreeWait = _cpu.Elapsed.TotalMilliseconds;
            _cpu.Restart();

            var ps = _treePs;
            while (ps.Count > 0) ps.RemoveAt(ps.Count - 1);
            ps.EnsureCapacity(n + s);
            if (_soft.Length < n + s) _soft = new float[ps.Capacity];
            for (int i = 0; i < n; i++)
            {
                int k = i * 4;
                ps.Add(new Vector3(_stagePosData[k], _stagePosData[k + 1], _stagePosData[k + 2]), Vector3.Zero, _stageMassData[k], 0f);
                _soft[i] = _stagePosData[k + 3];
            }
            for (int k = 0; k < s; k++)
            {
                ps.Add(sp.Position[k], Vector3.Zero, sp.Mass[k], 0f);
                _soft[n + k] = sinks.AccretionRadius(k);
            }
            _tree.Theta = _p.Theta;
            _tree.Build(ps);
            _tree.Flatten(_flat, _soft);
            for (int k = 0; k < s; k++)
                _sinkAcc[k] = _flat.Acceleration(sp.Position[k], _soft[n + k], _p.Theta, out _sinkPhi[k]);
            int nodes = _flat.Count;
            if (_nodeAData.Length < nodes * 4)
            {
                int cap = nodes * 4 * 5 / 4 + 64;
                _nodeAData = new float[cap]; _nodeBData = new float[cap]; _nodeCData = new float[cap];
            }
            for (int i = 0; i < nodes; i++)
            {
                int k = i * 4;
                Vector4 a = _flat.ComMass[i], b = _flat.CentreHalf[i];
                _nodeAData[k] = a.X; _nodeAData[k + 1] = a.Y; _nodeAData[k + 2] = a.Z; _nodeAData[k + 3] = a.W;
                _nodeBData[k] = b.X; _nodeBData[k + 1] = b.Y; _nodeBData[k + 2] = b.Z; _nodeBData[k + 3] = b.W;
                _nodeCData[k] = BitConverter.Int32BitsToSingle(_flat.Skip[i]);
                _nodeCData[k + 1] = BitConverter.Int32BitsToSingle(_flat.Body[i]);
                _nodeCData[k + 2] = _flat.Soft2[i];
                _nodeCData[k + 3] = 0f;
            }
            times.TreeBuild = _cpu.Elapsed.TotalMilliseconds;
            _cpu.Restart();
            if (nodes > _nodeCapacity)
            {
                _nodeCapacity = nodes * 5 / 4 + 64;
                foreach (int buf in new[] { _nodeA, _nodeB, _nodeC })
                {
                    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, buf);
                    GL.BufferData(BufferTarget.ShaderStorageBuffer, _nodeCapacity * 16, IntPtr.Zero, BufferUsageHint.StreamDraw);
                }
            }
            WriteAt(_nodeA, IntPtr.Zero, _nodeAData, nodes * 4);
            WriteAt(_nodeB, IntPtr.Zero, _nodeBData, nodes * 4);
            WriteAt(_nodeC, IntPtr.Zero, _nodeCData, nodes * 4);
            times.TreeUpload = _cpu.Elapsed.TotalMilliseconds;
        }

        private void GravityPhase()
        {
            if (!_p.SelfGravity) return;
            var g = _gravity;
            g.Set("uCount", (uint)Count);
            g.Set("uNodeCount", (uint)_flat.Count);
            g.Set("uTheta2", _p.Theta * _p.Theta);
            g.Set("uHMin", _p.HMin);
            g.Set("uHMax", _p.HMax);
            Bind(_posH.Front, _rhoP, _acc.Back, _extra, _xi, _nodeA, _nodeB, _nodeC);
            g.Dispatch(Count, WG);
            Barrier();

            SetGrid(_zeta);
            Bind(_posH.Front, _massId.Front, _cellStart, _xi, _acc.Back, _ctrl);
            _zeta.Dispatch(Count, WG);
            Barrier();
        }

        private float KickAndTimeStep(float dt, bool kick, bool accFront)
        {
            var p = _kick;
            p.Set("uCount", (uint)Count);
            p.Set("uDt", dt);
            p.Set("uKick", kick);
            p.Set("uIdeal", _p.Eos == EquationOfState.IdealGas);
            p.Set("uUMin", _p.MinInternalEnergy);
            p.Set("uCourant", _p.Courant * _p.TimeScale);
            p.Set("uAlpha", _p.Alpha);
            p.Set("uBeta", _p.Beta);
            p.Set("uDamping", Damping);
            p.Set("uCoolBeta", _p.CoolingBeta);
            p.Set("uCoolFloor", _p.CoolingFloor);
            Bind(_vel.Front, accFront ? _acc.Front : _acc.Back, _posH.Front, _rhoP, _extra, _ctrl, _massId.Front);
            p.Dispatch(Count, WG);
            Barrier();
            return 0f;
        }

        private void AccreteAndFindCandidates(SinkManager sinks)
        {
            var sp = sinks.Sinks;
            int existing = Math.Min(sp.Count, SinkCapacity);
            int total = Math.Min(existing + sinks.Forming.Count, SinkCapacity);
            if (total > 0)
            {
                for (int k = 0; k < total; k++)
                {
                    int o = k * 8;
                    if (k < existing)
                    {
                        Vector3 x = sp.Position[k], v = sp.Velocity[k];
                        _sinkData[o] = x.X; _sinkData[o + 1] = x.Y; _sinkData[o + 2] = x.Z; _sinkData[o + 3] = sinks.AccretionRadius(k);
                        _sinkData[o + 4] = v.X; _sinkData[o + 5] = v.Y; _sinkData[o + 6] = v.Z; _sinkData[o + 7] = sp.Mass[k];
                    }
                    else
                    {
                        Vector3 x = sinks.Forming[k - existing];
                        _sinkData[o] = x.X; _sinkData[o + 1] = x.Y; _sinkData[o + 2] = x.Z; _sinkData[o + 3] = _p.SinkRadius;
                        _sinkData[o + 4] = 0f; _sinkData[o + 5] = 0f; _sinkData[o + 6] = 0f; _sinkData[o + 7] = 0f;
                    }
                }
                WriteAt(_sinks, IntPtr.Zero, _sinkData, total * 8);
                var a = _accrete;
                a.Set("uCount", (uint)Count);
                a.Set("uExisting", (uint)existing);
                a.Set("uSinkCount", (uint)total);
                a.Set("uEventCapacity", (uint)EventCapacity);
                a.Set("uSinkRadius", _p.SinkRadius);
                Bind(_posH.Front, _vel.Front, _massId.Front, _sinks, _events, _ctrl);
                a.Dispatch(Count, WG);
                Barrier();
            }
            _lastSinkTotal = total;
            _lastExisting = existing;
            if (!float.IsPositiveInfinity(_p.SinkDensity) && _p.SinkRadius > 0f)
            {
                var c = _candidates;
                SetGrid(c);
                c.Set("uRhoSink", _p.SinkDensity);
                c.Set("uSinkRadius", _p.SinkRadius);
                c.Set("uCandidateCapacity", (uint)CandidateCapacity);
                c.Set("uIdeal", _p.Eos == EquationOfState.IdealGas);
                Bind(_posH.Front, _vel.Front, _massId.Front, _cellStart, _rhoP, _extra, _cands, _ctrl);
                c.Dispatch(Count, WG);
                Barrier();
            }
        }

        private int _lastSinkTotal, _lastExisting;

        private void ApplySinkResults(SinkManager sinks)
        {
            int events = (int)Math.Min(_ctrlData[3], (uint)EventCapacity);
            _eventList.Clear();
            if (events > 0 && _lastSinkTotal > 0)
            {
                GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _events);
                GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, events * 32, _eventData);
                for (int e = 0; e < events; e++)
                {
                    int o = e * 8;
                    _eventList.Add(new AccretionEvent
                    {
                        Position = new Vector3(_eventData[o], _eventData[o + 1], _eventData[o + 2]),
                        Mass = _eventData[o + 3],
                        Velocity = new Vector3(_eventData[o + 4], _eventData[o + 5], _eventData[o + 6]),
                        Sink = BitConverter.SingleToInt32Bits(_eventData[o + 7]),
                    });
                }
                _alive -= events;
            }
            // Forming sinks beyond the uploaded list (capacity) are dropped.
            while (sinks.Forming.Count > _lastSinkTotal - _lastExisting) sinks.Forming.RemoveAt(sinks.Forming.Count - 1);
            sinks.Apply(_eventList);

            int cands = (int)Math.Min(_ctrlData[2], (uint)CandidateCapacity);
            _candList.Clear();
            if (cands > 0)
            {
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _cands);
                GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, cands * 32, _candData);
                for (int c = 0; c < cands; c++)
                {
                    int o = c * 8;
                    _candList.Add(new SinkCandidate
                    {
                        Position = new Vector3(_candData[o], _candData[o + 1], _candData[o + 2]),
                        Density = _candData[o + 3],
                        Index = BitConverter.SingleToInt32Bits(_candData[o + 4]),
                    });
                }
                sinks.SelectNewSinks(_candList);
            }
        }

        private void ReadCtrl()
        {
            GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ctrl);
            GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, 16, _ctrlData);
        }

        // ---- diagnostics ----------------------------------------------------------------------------

        /// <summary>Test hook (after <see cref="ComputeDerivatives"/>): grid-neighbour signatures; returns the particle ids in GPU order.</summary>
        public int[] NeighbourSignatures(uint[] count, uint[] idSum, uint[] idSq)
        {
            int n = Count;
            SetGrid(_signatures);
            Bind(_posH.Front, _massId.Front, _cellStart, _ctrl, _sig);
            _signatures.Dispatch(n, WG);
            GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
            var sig = new uint[n * 4];
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _sig);
            GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, n * 16, sig);
            float[] mid = Read(_massId.Front, n * 4);
            var ids = new int[n];
            for (int i = 0; i < n; i++)
            {
                count[i] = sig[i * 4];
                idSum[i] = sig[i * 4 + 1];
                idSq[i] = sig[i * 4 + 2];
                ids[i] = BitConverter.SingleToInt32Bits(mid[i * 4 + 1]);
            }
            return ids;
        }

        /// <summary>
        /// Kinetic, thermal and potential energy of gas and sinks (potential from the last gravity pass:
        /// ½ Σ m φ). GPU reduction; reads back one vec4 per 256 particles.
        /// </summary>
        public (double Kinetic, double Thermal, double Potential) Energies(SinkManager sinks = null)
        {
            int n = Count, groups = (n + 255) / 256;
            double k = 0, t = 0, w = 0;
            if (n > 0)
            {
                _energy.Set("uCount", (uint)n);
                _energy.Set("uIdeal", _p.Eos == EquationOfState.IdealGas);
                Bind(_vel.Front, _massId.Front, _extra, _rhoP, _partials);
                _energy.Dispatch(n, 256);
                GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
                float[] part = Read(_partials, groups * 4);
                for (int g = 0; g < groups; g++) { k += part[g * 4]; t += part[g * 4 + 1]; w += part[g * 4 + 2]; }
            }
            var sp = sinks?.Sinks;
            if (sp != null)
                for (int s = 0; s < sp.Count && s < _sinkPhi.Length; s++)
                {
                    k += 0.5 * sp.Mass[s] * sp.Velocity[s].LengthSquared();
                    w += 0.5 * sp.Mass[s] * _sinkPhi[s];
                }
            return (k, t, w);
        }

        // ---- GL helpers -----------------------------------------------------------------------------

        private static void Barrier() => GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

        private static void Bind(params int[] buffers)
        {
            for (int b = 0; b < buffers.Length; b++)
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, b, buffers[b]);
        }

        private static int NewBuffer(int bytes, BufferUsageHint hint = BufferUsageHint.DynamicCopy)
        {
            int b = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, b);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, Math.Max(bytes, 16), IntPtr.Zero, hint);
            return b;
        }

        private static Pair NewPair(int bytes) => new Pair { Front = NewBuffer(bytes), Back = NewBuffer(bytes) };

        private static void Write(int buffer, float[] data, int floats) => WriteAt(buffer, IntPtr.Zero, data, floats);

        private static void WriteAt(int buffer, IntPtr offset, float[] data, int floats)
        {
            if (floats <= 0) return;
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, buffer);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, offset, floats * 4, data);
        }

        private static float[] Read(int buffer, int floats)
        {
            var data = new float[Math.Max(floats, 0)];
            if (floats <= 0) return data;
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, buffer);
            GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, floats * 4, data);
            return data;
        }

        private static void Copy(int from, int to, int bytes)
        {
            GL.BindBuffer(BufferTarget.CopyReadBuffer, from);
            GL.BindBuffer(BufferTarget.CopyWriteBuffer, to);
            GL.CopyBufferSubData(BufferTarget.CopyReadBuffer, BufferTarget.CopyWriteBuffer, IntPtr.Zero, IntPtr.Zero, bytes);
        }

        public void Dispose()
        {
            foreach (var p in new[] { _clear, _kickDrift, _hash, _scanLocal, _scanAdd, _permute, _predict, _density, _force,
                         _gravity, _zeta, _kick, _accrete, _candidates, _inject, _energy, _signatures })
                p?.Dispose();
            var buffers = new List<int> { _rhoP, _extra, _vpred, _xi, _key, _rank, _cellCount, _cellStart, _blockSums, _ctrl,
                _nodeA, _nodeB, _nodeC, _sinks, _events, _cands, _partials, _sig, _stagePos, _stageMass };
            foreach (var pr in new[] { _posH, _vel, _acc, _massId }) { buffers.Add(pr.Front); buffers.Add(pr.Back); }
            foreach (int b in buffers) if (b != 0) GL.DeleteBuffer(b);
            GL.DeleteQueries(Q, _queries);
            if (_stageFence != IntPtr.Zero) GL.DeleteSync(_stageFence);
            _stageFence = IntPtr.Zero;
        }
    }
}
