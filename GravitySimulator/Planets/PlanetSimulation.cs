using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace GravitySimulator.Planets
{
    /// <summary>Tunables for <see cref="PlanetSimulation"/>. Normalised units (G = 1).</summary>
    public sealed class SimulationParameters
    {
        public float TimeStep { get; set; } = 0.01f; // ~160 steps per orbit at the disc inner edge
        public float TimeScale { get; set; } = 1f;
        public float Theta { get; set; } = 0.6f;
        public float Softening { get; set; } = 0.005f;
        public float EscapeRadius { get; set; } = 20f;
        public float MergeThreshold { get; set; } = 1f;
        public float Restitution { get; set; } = 0.5f;
        /// <summary>Rule for rocky (Planet-Planet) collisions; other pairs always merge or bounce.</summary>
        public CollisionModel CollisionModel { get; set; } = CollisionModel.Outcomes;
        /// <summary>
        /// Outcomes: collision fragments may raise the live particle count up to this ceiling (or to the count at
        /// Load, if that is larger); at the ceiling, further fragments are folded into the largest fragment or the
        /// remnant. Chosen from measured step cost on a 32-core machine with an RTX 4070 Laptop GPU ("GPU tree build and
        /// broad phase" amendment, 2026-10-31, of the collision outcomes spec): the largest round count that still sustains about
        /// 5 steps/s under both held-at-ceiling loads measured (debris cloud, and the 0.1% disc whose live count stays
        /// pinned at the ceiling). <see cref="DefaultMaxLiveParticles"/> assumes GPU gravity; the UI uses
        /// <see cref="CpuMaxLiveParticles"/> when gravity falls back to the CPU tree.
        /// </summary>
        public int MaxLiveParticles { get; set; } = DefaultMaxLiveParticles;
        /// <summary>Ceiling with GPU gravity (held there: 5.4 steps/s for the 0.1% disc, 12.2 for the debris cloud).</summary>
        public const int DefaultMaxLiveParticles = 700_000;
        /// <summary>Ceiling with CPU gravity (held there: 4.1 steps/s for the 0.1% disc, 4.4-4.9 for the debris cloud).</summary>
        public const int CpuMaxLiveParticles = 150_000;
        /// <summary>Upper bound on background-thread steps per second (0 = unlimited), so small systems stay watchable.</summary>
        public float MaxStepsPerSecond { get; set; } = 120f;
        /// <summary>
        /// Sub-grid stellar model (and its unit system) for the cosmic presets; null = off, which is how
        /// the planet-scale presets run. Read when a state is loaded.
        /// </summary>
        public StellarParameters Stellar { get; set; }
    }

    /// <summary>Immutable per-step statistics.</summary>
    public sealed class SimulationStats
    {
        public long StepCount { get; init; }
        public double SimTime { get; init; }
        public int Count { get; init; }
        public long Merges { get; init; }
        public long Bounces { get; init; }
        /// <summary>Collision outcomes, cumulative (perfect merges are counted in <see cref="Merges"/>).</summary>
        public long PartialAccretions { get; init; }
        public long HitAndRuns { get; init; }
        public long Erosions { get; init; }
        public long Disruptions { get; init; }
        public long FragmentsCreated { get; init; }
        /// <summary>Fragments not created because the live count was at the ceiling (their mass was folded).</summary>
        public long FragmentsFoldedAtCeiling { get; init; }
        public long Escaped { get; init; }
        public double StepsPerSecond { get; init; }
        /// <summary>Mass of the heaviest body if it holds over half the total (the star); otherwise 0.</summary>
        public float StarMass { get; init; }
        /// <summary>Largest non-star masses, descending.</summary>
        public float[] LargestMasses { get; init; } = Array.Empty<float>();
        /// <summary>Smallest particle mass at load time (one dust grain).</summary>
        public float GrainMass { get; init; }

        /// <summary>True when the stellar model is on (cosmic presets); the fields below are only filled then.</summary>
        public bool StellarModel { get; init; }
        /// <summary>Body counts indexed by <see cref="BodyClass"/>.</summary>
        public int[] ClassCounts { get; init; } = Array.Empty<int>();
        public long StarsFormed { get; init; }
        public long Supernovae { get; init; }
        public long WhiteDwarfsFormed { get; init; }
        public long NeutronStarsFormed { get; init; }
        public long BlackHolesFormed { get; init; }
        /// <summary>Black holes merging with a star or compact object (contact or sink capture).</summary>
        public long BlackHoleMergers { get; init; }
        /// <summary>Simulation time in years (dynamical clock, not the accelerated stellar clock).</summary>
        public double SimYears { get; init; }
        /// <summary>Mass of the heaviest black hole, or 0.</summary>
        public float HeaviestBlackHole { get; init; }
        /// <summary>Mass of the heaviest star, or 0.</summary>
        public float HeaviestStar { get; init; }

        public static readonly SimulationStats Empty = new SimulationStats();
    }

    /// <summary>Wall time of the phases of the last step, in milliseconds.</summary>
    public readonly struct PlanetPhaseTimes
    {
        /// <summary>Opening kick + drift.</summary>
        public double KickDrift { get; init; }
        /// <summary>Octree build on the CPU (CPU backend; the GPU backend builds its tree on the GPU, see <see cref="GpuTreeBuild"/>).</summary>
        public double TreeBuild { get; init; }
        /// <summary>
        /// Gravity (CPU walk, or GPU flatten + upload + walk + read-back), force contributors and the closing kick;
        /// on the GPU path also the reorder of the bodies into tree order.
        /// </summary>
        public double Gravity { get; init; }
        /// <summary>Of <see cref="Gravity"/>, GPU path only: bounding cube and body packing on the CPU.</summary>
        public double GpuFlatten { get; init; }
        /// <summary>Of <see cref="Gravity"/>, GPU path only: upload, tree build, walk and read-back on the GL thread.</summary>
        public double GpuWalk { get; init; }
        /// <summary>Of <see cref="GpuWalk"/>: upload (to glFinish), octree build on the GPU, walk kernel (to glFinish), read-back and thread hand-off.</summary>
        public double GpuUpload { get; init; }
        public double GpuTreeBuild { get; init; }
        public double GpuKernel { get; init; }
        public double GpuReadback { get; init; }
        /// <summary>Of <see cref="Gravity"/>, GPU path only: scattering the accelerations, and the reorder into tree order.</summary>
        public double GpuScatter { get; init; }
        public double Reorder { get; init; }
        public double Collisions { get; init; }
        /// <summary>Of <see cref="Collisions"/>: broad phase (finding the touching pairs), the sequential resolve, compact + append.</summary>
        public double CollisionDetect { get; init; }
        public double CollisionResolve { get; init; }
        public double CollisionCompact { get; init; }
        /// <summary>
        /// GPU path with GPU contacts: the broad phase on the GPU (inside <see cref="GpuWalk"/>); <see cref="CollisionDetect"/>
        /// is then only the CPU re-test, sort and de-duplication of the candidates.
        /// </summary>
        public double GpuBroadPhase { get; init; }
        /// <summary>Body evolution, escape removal and the snapshot publish.</summary>
        public double Rest { get; init; }
        public double Total => KickDrift + TreeBuild + Gravity + Collisions + Rest;
    }

    /// <summary>
    /// A published copy of the particle state for the renderer. Buffers are recycled
    /// (triple buffering), but a snapshot is never written while the reader holds it.
    /// </summary>
    public sealed class SimulationSnapshot
    {
        public float[] Positions = Array.Empty<float>(); // xyz interleaved
        public float[] Radii = Array.Empty<float>();
        public float[] Masses = Array.Empty<float>();
        /// <summary>Per-body <see cref="BodyClass"/> as a float (for the GL attribute).</summary>
        public float[] Classes = Array.Empty<float>();
        public int Count;
        /// <summary>Supernova flashes appended after <see cref="Count"/> in the arrays (radius = brightness 0..1, class = <see cref="FlashClass"/>).</summary>
        public int FlashCount;
        public const float FlashClass = 6f;
        public SimulationStats Stats = SimulationStats.Empty;
        /// <summary>Increments with every publish; lets the reader skip re-uploading the same data.</summary>
        public long Version;
    }

    /// <summary>
    /// Owns the particle state and steps it with leapfrog kick-drift-kick, Barnes-Hut gravity
    /// (forces in parallel), collisions, and escape removal. Can run on its own background thread;
    /// after each step a snapshot is published for a single reader (the renderer).
    /// </summary>
    public sealed class PlanetSimulation : IDisposable
    {
        private const int TopN = 5;
        private const int Fresh = 4;

        private readonly object _gate = new object();
        private readonly SimulationParameters _p;
        private readonly CollisionResolver _collisions = new CollisionResolver();
        private BarnesHutTree _tree = new BarnesHutTree(1024);
        private ParticleSystem _ps = new ParticleSystem(1);

        private long _steps, _merges, _bounces, _escaped, _version, _bhContactMergers;
        private long _partialAccretions, _hitAndRuns, _erosions, _disruptions, _fragments, _folded;
        private int _loadCount;
        private double _simTime, _stepSecondsEma;
        private long _lastStepStart;
        private float _grainMass;
        private volatile SimulationStats _stats = SimulationStats.Empty;

        // Triple buffer: writer owns _writeIdx, reader owns _readIdx, _shared is the hand-off slot.
        private readonly SimulationSnapshot[] _snap = { new SimulationSnapshot(), new SimulationSnapshot(), new SimulationSnapshot() };
        private int _writeIdx = 0, _shared = 1, _readIdx = 2;
        private volatile bool _published;

        // Background thread.
        private Thread _thread;
        private readonly ManualResetEventSlim _running = new ManualResetEventSlim(false);
        private volatile bool _stop;
        private readonly ManualResetEventSlim _wake = new ManualResetEventSlim(false); // set on Dispose to cut a throttle wait short

        // Cached delegates so the parallel loops do not allocate closures each step.
        private readonly Action<int> _kickDriftChunk;
        private readonly Action<int> _forceKickChunk;
        private readonly Action<int> _forceChunk;
        private readonly Action<int> _kickChunk;
        private int _chunkSize, _n;
        private float _halfDt, _dt;

        private StellarEvolution _stellar;
        private RadiationPressure _radiation;

        private GpuGravity _gpu;
        private ParticleSystem _spare; // Reorder scratch (GPU path)
        private volatile string _gravityBackend = CpuBackendName;
        private const string CpuBackendName = "CPU Barnes-Hut";

        /// <summary>Extra forces added after gravity each step (see <see cref="IForceContributor"/>).</summary>
        public List<IForceContributor> ForceContributors { get; } = new List<IForceContributor>();
        /// <summary>Per-step body evolution after collisions (see <see cref="IBodyEvolution"/>).</summary>
        public List<IBodyEvolution> Evolutions { get; } = new List<IBodyEvolution>();
        /// <summary>The active stellar model, or null when it is off.</summary>
        public StellarEvolution Stellar => _stellar;

        public PlanetSimulation(SimulationParameters parameters)
        {
            _p = parameters ?? new SimulationParameters();
            _kickDriftChunk = KickDriftChunk;
            _forceKickChunk = ForceKickChunk;
            _forceChunk = ForceChunk;
            _kickChunk = KickChunk;
        }

        public SimulationParameters Parameters => _p;

        /// <summary>The live state. Only touch it from the simulation thread (or when not running).</summary>
        public ParticleSystem Particles => _ps;

        public SimulationStats Stats => _stats;

        /// <summary>Phase timings of the last step.</summary>
        public PlanetPhaseTimes LastPhaseTimes { get; private set; }

        /// <summary>Which gravity backend is active, for the readout (e.g. "GPU (NVIDIA ...)" or "CPU Barnes-Hut").</summary>
        public string GravityBackend => _gravityBackend;
        /// <summary>Touching pairs the last step resolved (or tried: a pair an earlier one moved apart is skipped).</summary>
        public int LastContactPairs => _collisions.LastContacts;
        /// <summary>Octree nodes of the last GPU gravity build (0 on the CPU path).</summary>
        public int LastGpuNodeCount => _gpu?.LastNodeCount ?? 0;
        /// <summary>GPU backend only: bodies per octree leaf at most (see <see cref="GpuGravity.LeafCapacity"/>).</summary>
        public int GpuLeafCapacity { get => _gpu?.LeafCapacity ?? 0; set { if (_gpu != null) _gpu.LeafCapacity = value; } }
        /// <summary>True while gravity runs on the GPU.</summary>
        public bool GpuGravityActive => _gpu != null;

        /// <summary>
        /// Moves gravity onto the GPU (OpenGL 4.3 compute, <see cref="GpuGravity"/>). Returns false and stays on
        /// the CPU tree, with the reason in <see cref="GravityBackend"/>, when that is unavailable. Off by
        /// default: the CPU path is deterministic (the golden-hash tests rely on it). If the GPU fails later,
        /// the simulation falls back to the CPU tree on its own.
        /// </summary>
        public bool TryEnableGpuGravity()
        {
            lock (_gate)
            {
                if (_gpu != null) return true;
                var g = GpuGravity.TryCreate(out string error);
                if (g == null)
                {
                    _gravityBackend = CpuBackendName + " (GPU unavailable: " + Shorten(error) + ")";
                    return false;
                }
                _gpu = g;
                _gravityBackend = "GPU (" + g.Renderer + ")";
                return true;
            }
        }

        /// <summary>Returns gravity to the CPU tree.</summary>
        public void DisableGpuGravity()
        {
            lock (_gate)
            {
                _gpu?.Dispose();
                _gpu = null;
                _gravityBackend = CpuBackendName;
            }
        }

        private static string Shorten(string s)
        {
            s = (s ?? "unknown").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > 60 ? s.Substring(0, 57) + "..." : s;
        }

        /// <summary>Live-count ceiling for fragments: max(<see cref="SimulationParameters.MaxLiveParticles"/>, count at Load).</summary>
        public int LiveParticleCeiling => Math.Max(_p.MaxLiveParticles, _loadCount);
        public bool IsRunning => _running.IsSet && IsThreadAlive;
        public bool IsThreadAlive => _thread != null && _thread.IsAlive;

        /// <summary>Replaces the state (reset) and publishes it immediately.</summary>
        public void Load(ParticleSystem initial)
        {
            lock (_gate)
            {
                _ps = initial;
                _steps = _merges = _bounces = _escaped = _bhContactMergers = 0;
                _partialAccretions = _hitAndRuns = _erosions = _disruptions = _fragments = _folded = 0;
                _loadCount = _ps.Count;
                ConfigureStellarModel();
                _simTime = 0;
                _grainMass = float.MaxValue;
                for (int i = 0; i < _ps.Count; i++) if (_ps.Mass[i] > 0f && _ps.Mass[i] < _grainMass) _grainMass = _ps.Mass[i];
                if (_grainMass == float.MaxValue) _grainMass = 0f;
                _tree = new BarnesHutTree(_ps.Capacity);
                ComputeAccelerations();
                Publish();
            }
        }

        // Installs (or removes) the stellar model's evolution and radiation pressure per the parameters.
        private void ConfigureStellarModel()
        {
            if (_stellar != null) Evolutions.Remove(_stellar);
            if (_radiation != null) ForceContributors.Remove(_radiation);
            _stellar = null;
            _radiation = null;
            var sp = _p.Stellar;
            if (sp == null) return;
            _stellar = new StellarEvolution(sp);
            _radiation = new RadiationPressure(sp);
            Evolutions.Add(_stellar);
            ForceContributors.Add(_radiation);
        }

        /// <summary>Advances one step synchronously.</summary>
        public void Step()
        {
            lock (_gate) StepCore();
        }

        private void StepCore()
        {
            long t0 = Stopwatch.GetTimestamp();
            _dt = _p.TimeStep * _p.TimeScale;
            _halfDt = _dt * 0.5f;

            RunChunks(_kickDriftChunk);       // v += a dt/2 ; x += v dt
            long t1 = Stopwatch.GetTimestamp();
            if (_gpu == null) BuildTree();    // the GPU backend builds its own tree
            long t2 = Stopwatch.GetTimestamp();
            _wantContacts = GpuContacts;
            ForcesAndKick();                  // a = F(x) ; v += a dt/2 (GPU: also the contact candidates)
            _wantContacts = false;
            long t3 = Stopwatch.GetTimestamp();

            _collisions.MergeThreshold = _p.MergeThreshold;
            _collisions.Restitution = _p.Restitution;
            _collisions.Model = _p.CollisionModel;
            _collisions.MaxBodies = LiveParticleCeiling;
            _collisions.MinFragmentMass = _grainMass * CollisionOutcomes.MinFragmentGrainFraction;
            // GPU path: the broad phase ran on the GPU with gravity, on these positions in this (reordered) order.
            if (_lastGpu && _lastGpuContacts) _collisions.Resolve(_ps, _gpu.LastContactPairs, _gpu.LastContactCount);
            else _collisions.Resolve(_ps);
            _partialAccretions += _collisions.LastPartialAccretions;
            _hitAndRuns += _collisions.LastHitAndRuns;
            _erosions += _collisions.LastErosions;
            _disruptions += _collisions.LastDisruptions;
            _fragments += _collisions.LastFragments;
            _folded += _collisions.LastFoldedAtCeiling;
            _merges += _collisions.LastMerges;
            _bounces += _collisions.LastBounces;
            _bhContactMergers += _collisions.LastBlackHoleMergers;
            long t4 = Stopwatch.GetTimestamp();

            foreach (var evolution in Evolutions) evolution.Evolve(_ps, _dt);

            float esc2 = _p.EscapeRadius * _p.EscapeRadius;
            int escaped = 0;
            for (int i = 0; i < _ps.Count; i++)
            {
                if (_ps.Position[i].LengthSquared() > esc2) { _ps.Mass[i] = 0f; escaped++; }
            }
            if (escaped > 0) { _ps.Compact(); _escaped += escaped; }

            _steps++;
            _simTime += _dt;
            // Steps/s is measured start-to-start (so throttling shows), falling back to the compute time
            // after a pause or for the first step.
            long now = Stopwatch.GetTimestamp();
            double secs = (now - t0) / (double)Stopwatch.Frequency;
            if (_lastStepStart != 0)
            {
                double interval = (t0 - _lastStepStart) / (double)Stopwatch.Frequency;
                if (interval < 1.0) secs = Math.Max(secs, interval);
            }
            _lastStepStart = t0;
            _stepSecondsEma = _stepSecondsEma == 0 ? secs : _stepSecondsEma * 0.9 + secs * 0.1;
            Publish();
            long t5 = Stopwatch.GetTimestamp();
            LastPhaseTimes = new PlanetPhaseTimes
            {
                KickDrift = Ms(t0, t1),
                TreeBuild = Ms(t1, t2),
                Gravity = Ms(t2, t3),
                GpuFlatten = _lastGpu ? _gpu?.LastFlattenMs ?? 0 : 0,
                GpuWalk = _lastGpu ? _gpu?.LastGpuMs ?? 0 : 0,
                GpuTreeBuild = _lastGpu ? _gpu?.LastBuildMs ?? 0 : 0,
                GpuUpload = _lastGpu ? _gpu?.LastUploadMs ?? 0 : 0,
                GpuKernel = _lastGpu ? _gpu?.LastKernelMs ?? 0 : 0,
                GpuReadback = _lastGpu ? _gpu?.LastReadbackMs ?? 0 : 0,
                GpuScatter = _lastGpu ? _gpu?.LastScatterMs ?? 0 : 0,
                Reorder = _lastGpu ? _lastReorderMs : 0,
                Collisions = Ms(t3, t4),
                CollisionDetect = _collisions.LastDetectMs,
                GpuBroadPhase = _lastGpu && _lastGpuContacts ? _gpu?.LastBroadPhaseMs ?? 0 : 0,
                CollisionResolve = _collisions.LastResolveMs,
                CollisionCompact = _collisions.LastCompactMs,
                Rest = Ms(t4, t5),
            };
        }

        private static double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
        private bool _lastGpu;
        private double _lastReorderMs;

        private bool _treeStale; // the CPU tree does not match the state (the GPU backend skipped it)
        private bool _wantContacts, _lastGpuContacts;

        /// <summary>
        /// GPU backend: run the collision broad phase on the GPU with gravity (on by default); the CPU still re-tests,
        /// orders and resolves the pairs. Off: the CPU broad phase, as on the CPU backend.
        /// </summary>
        public bool GpuContacts { get; set; } = true;

        private void BuildTree()
        {
            _treeStale = false;
            _tree.Theta = _p.Theta;
            _tree.Softening = _p.Softening;
            _tree.Build(_ps);
        }

        private void ComputeAccelerations()
        {
            if (_gpu == null) BuildTree();
            _halfDt = 0f;
            ForcesAndKick();
        }

        // Gravity and the half kick fused in one pass when there are no extra forces (planet presets);
        // otherwise gravity, then the contributors, then the kick.
        // With GPU gravity: the GPU writes the accelerations, then the contributors (CPU), then the kick.
        private void ForcesAndKick()
        {
            _lastGpu = _gpu != null && TryGpuGravity();
            if (!_lastGpu && _gpu == null && _treeStale) BuildTree(); // the GPU just failed: the CPU tree was not built
            if (_lastGpu)
            {
                foreach (var f in ForceContributors) f.AddAccelerations(_ps);
                RunChunks(_kickChunk);
                // Keep the bodies in tree order so the next tree build, flatten and collision sweep walk
                // memory coherently (2-3x faster builds at 400k). GPU path only: the CPU path keeps its order,
                // which the golden-hash tests pin.
                long r0 = Stopwatch.GetTimestamp();
                _ps.Reorder(_gpu.LastOrder, _spare ??= new ParticleSystem(_ps.Capacity));
                _lastReorderMs = Ms(r0, Stopwatch.GetTimestamp());
                return;
            }
            if (ForceContributors.Count == 0) { RunChunks(_forceKickChunk); return; }
            RunChunks(_forceChunk);
            foreach (var f in ForceContributors) f.AddAccelerations(_ps);
            RunChunks(_kickChunk);
        }

        // Gravity on the GPU into _ps.Acceleration; on failure, drops to the CPU tree for good (returns false).
        private bool TryGpuGravity()
        {
            try
            {
                _gpu.Compute(_ps, _p.Theta, _p.Softening, _wantContacts);
                _lastGpuContacts = _wantContacts;
                _treeStale = true;
                return true;
            }
            catch (Exception ex)
            {
                _gpu.Dispose();
                _gpu = null;
                _gravityBackend = CpuBackendName + " (GPU failed: " + Shorten(ex.Message) + ")";
                _treeStale = true;
                return false;
            }
        }

        private void ForceChunk(int c)
        {
            int start = c * _chunkSize, end = Math.Min(_n, start + _chunkSize);
            Vector3[] x = _ps.Position, a = _ps.Acceleration;
            for (int i = start; i < end; i++) a[i] = _tree.AccelerationAt(x[i], i);
        }

        private void KickChunk(int c)
        {
            int start = c * _chunkSize, end = Math.Min(_n, start + _chunkSize);
            Vector3[] v = _ps.Velocity, a = _ps.Acceleration;
            float h = _halfDt;
            for (int i = start; i < end; i++) v[i] += a[i] * h;
        }

        private void RunChunks(Action<int> body)
        {
            _n = _ps.Count;
            if (_n == 0) return;
            int chunks = Math.Min(_n, Environment.ProcessorCount * 4);
            _chunkSize = (_n + chunks - 1) / chunks;
            if (_n < 256) { for (int c = 0; c < chunks; c++) body(c); return; }
            Parallel.For(0, chunks, body);
        }

        private void KickDriftChunk(int c)
        {
            int start = c * _chunkSize, end = Math.Min(_n, start + _chunkSize);
            Vector3[] x = _ps.Position, v = _ps.Velocity, a = _ps.Acceleration;
            float h = _halfDt, dt = _dt;
            for (int i = start; i < end; i++)
            {
                v[i] += a[i] * h;
                x[i] += v[i] * dt;
            }
        }

        private void ForceKickChunk(int c)
        {
            int start = c * _chunkSize, end = Math.Min(_n, start + _chunkSize);
            Vector3[] x = _ps.Position, v = _ps.Velocity, a = _ps.Acceleration;
            float h = _halfDt;
            for (int i = start; i < end; i++)
            {
                Vector3 acc = _tree.AccelerationAt(x[i], i);
                a[i] = acc;
                v[i] += acc * h;
            }
        }

        // ---- snapshots -------------------------------------------------------------------------

        private void Publish()
        {
            int n = _ps.Count;
            int flashes = _stellar?.FlashCount ?? 0;
            var s = _snap[_writeIdx];
            if (s.Radii.Length < n + flashes)
            {
                int cap = Math.Max(n, _ps.Capacity) + StellarEvolution.MaxFlashes;
                s.Positions = new float[cap * 3];
                s.Radii = new float[cap];
                s.Masses = new float[cap];
                s.Classes = new float[cap];
            }
            Vector3[] x = _ps.Position;
            float[] pos = s.Positions;
            for (int i = 0, k = 0; i < n; i++, k += 3)
            {
                pos[k] = x[i].X; pos[k + 1] = x[i].Y; pos[k + 2] = x[i].Z;
            }
            Array.Copy(_ps.Radius, s.Radii, n);
            Array.Copy(_ps.Mass, s.Masses, n);
            BodyClass[] cls = _ps.Class;
            float[] fc = s.Classes;
            for (int i = 0; i < n; i++) fc[i] = (float)cls[i];
            for (int k = 0; k < flashes; k++)
            {
                int i = n + k;
                Vector3 fp = _stellar.FlashPosition(k);
                pos[i * 3] = fp.X; pos[i * 3 + 1] = fp.Y; pos[i * 3 + 2] = fp.Z;
                s.Radii[i] = _stellar.FlashBrightness(k);
                s.Masses[i] = 0f;
                fc[i] = SimulationSnapshot.FlashClass;
            }
            s.FlashCount = flashes;
            s.Count = n;
            s.Version = ++_version;
            s.Stats = _stats = BuildStats();

            _writeIdx = Interlocked.Exchange(ref _shared, _writeIdx | Fresh) & 3;
            _published = true;
        }

        /// <summary>
        /// Returns the newest published snapshot (or the one already held if nothing newer), or null
        /// before the first publish. Single reader only; the returned object stays valid and unchanged
        /// until the next call.
        /// </summary>
        public SimulationSnapshot AcquireLatest()
        {
            if (!_published) return null;
            if ((Volatile.Read(ref _shared) & Fresh) != 0)
                _readIdx = Interlocked.Exchange(ref _shared, _readIdx) & 3;
            return _snap[_readIdx];
        }

        private SimulationStats BuildStats()
        {
            int n = _ps.Count;
            float[] m = _ps.Mass;
            double total = 0;
            int heaviest = -1;
            for (int i = 0; i < n; i++)
            {
                total += m[i];
                if (heaviest < 0 || m[i] > m[heaviest]) heaviest = i;
            }
            int star = heaviest >= 0 && m[heaviest] > 0.5 * total ? heaviest : -1;

            Span<float> top = stackalloc float[TopN];
            int topCount = 0;
            for (int i = 0; i < n; i++)
            {
                if (i == star) continue;
                float v = m[i];
                if (topCount < TopN) { top[topCount++] = v; SiftUp(top, topCount); }
                else if (v > top[TopN - 1]) { top[TopN - 1] = v; SiftUp(top, TopN); }
            }
            var largest = top.Slice(0, topCount).ToArray();

            var st = _stellar;
            int[] classCounts = Array.Empty<int>();
            float heaviestBh = 0f, heaviestStar = 0f;
            if (st != null)
            {
                classCounts = new int[6];
                BodyClass[] cls = _ps.Class;
                for (int i = 0; i < n; i++)
                {
                    BodyClass c = cls[i];
                    classCounts[(int)c]++;
                    if (c == BodyClass.BlackHole && m[i] > heaviestBh) heaviestBh = m[i];
                    else if (c == BodyClass.Star && m[i] > heaviestStar) heaviestStar = m[i];
                }
            }

            return new SimulationStats
            {
                StellarModel = st != null,
                ClassCounts = classCounts,
                StarsFormed = st?.StarsFormed ?? 0,
                Supernovae = st?.Supernovae ?? 0,
                WhiteDwarfsFormed = st?.WhiteDwarfsFormed ?? 0,
                NeutronStarsFormed = st?.NeutronStarsFormed ?? 0,
                BlackHolesFormed = st?.BlackHolesFormed ?? 0,
                BlackHoleMergers = (st?.BlackHoleMergers ?? 0) + _bhContactMergers,
                SimYears = st != null ? st.Parameters.Units.SimToYears(_simTime) : 0,
                HeaviestBlackHole = heaviestBh,
                HeaviestStar = heaviestStar,
                StepCount = _steps,
                SimTime = _simTime,
                Count = n,
                Merges = _merges,
                Bounces = _bounces,
                PartialAccretions = _partialAccretions,
                HitAndRuns = _hitAndRuns,
                Erosions = _erosions,
                Disruptions = _disruptions,
                FragmentsCreated = _fragments,
                FragmentsFoldedAtCeiling = _folded,
                Escaped = _escaped,
                StepsPerSecond = _stepSecondsEma > 0 ? 1.0 / _stepSecondsEma : 0,
                StarMass = star >= 0 ? m[star] : 0f,
                LargestMasses = largest,
                GrainMass = _grainMass,
            };
        }

        // Keeps a small array sorted descending after its last element changed.
        private static void SiftUp(Span<float> a, int count)
        {
            for (int i = count - 1; i > 0 && a[i] > a[i - 1]; i--)
                (a[i], a[i - 1]) = (a[i - 1], a[i]);
        }

        // ---- background thread -----------------------------------------------------------------

        /// <summary>Starts (or resumes) stepping on the background thread.</summary>
        public void Start()
        {
            if (_stop) throw new ObjectDisposedException(nameof(PlanetSimulation));
            if (_thread == null)
            {
                _thread = new Thread(RunLoop) { IsBackground = true, Name = "PlanetSimulation" };
                _thread.Start();
            }
            _running.Set();
        }

        public void Pause() => _running.Reset();

        private void RunLoop()
        {
            while (!_stop)
            {
                _running.Wait();
                if (_stop) break;
                long t0 = Stopwatch.GetTimestamp();
                lock (_gate)
                {
                    if (!_stop) StepCore();
                }
                float max = _p.MaxStepsPerSecond;
                if (max > 0f)
                {
                    double spare = 1.0 / max - (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
                    if (spare > 0.001) _wake.Wait(TimeSpan.FromSeconds(spare));
                }
            }
        }

        /// <summary>Stops the background thread and waits for it to finish its current step.</summary>
        public void Dispose()
        {
            _stop = true;
            _running.Set();
            _wake.Set();
            _thread?.Join(TimeSpan.FromSeconds(10));
            _gpu?.Dispose();
            _gpu = null;
        }
    }
}
