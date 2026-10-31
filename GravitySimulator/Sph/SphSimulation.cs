using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using GravitySimulator.Planets;

namespace GravitySimulator.Sph
{
    /// <summary>Readout of the SPH mode (refreshed every step; energies every few steps).</summary>
    public sealed class SphStats
    {
        public int Gas { get; init; }
        public int Sinks { get; init; }
        public int[] ClassCounts { get; init; } = new int[6];
        public long Steps { get; init; }
        public double SimTime { get; init; }
        public double SimYears { get; init; }
        public float Dt { get; init; }
        public double StepsPerSecond { get; init; }
        public double Kinetic { get; init; }
        public double Thermal { get; init; }
        public double Potential { get; init; }
        public double TotalEnergy => Kinetic + Thermal + Potential;
        public long StarsFormed { get; init; }
        public long SinksCreated { get; init; }
        public long Accreted { get; init; }
        public long Supernovae { get; init; }
        public float HeaviestSink { get; init; }
        public SphPhaseTimes Times { get; init; }
        public static readonly SphStats Empty = new SphStats();
    }

    /// <summary>
    /// The Gas dynamics (SPH) mode's simulation: the GPU solver, the sinks, and the stellar model
    /// (<see cref="StellarEvolution"/>, shared with Planet formation) that makes sinks ignite, age and
    /// die. Stellar ejecta become SPH gas; supernova energy goes into the internal energy of nearby gas.
    /// All calls need the GL 4.3 context current (the UI thread).
    /// </summary>
    public sealed class SphSimulation : IDisposable
    {
        private SphGpuSolver _solver;
        private SinkManager _sinks;
        private StellarEvolution _stellar;
        private SphScenario _scenario;
        private long _lastSupernovae;
        private double _stepSecondsEma;
        private readonly Stopwatch _clock = new Stopwatch();
        private (double K, double T, double W) _energy;
        private readonly List<(Vector3, Vector3, float, float, float)> _ejecta = new List<(Vector3, Vector3, float, float, float)>();
        private readonly List<(Vector3, float, float)> _blasts = new List<(Vector3, float, float)>();

        public SphGpuSolver Solver => _solver;
        public SinkManager SinkManager => _sinks;
        public StellarEvolution Stellar => _stellar;
        public SphScenario Scenario => _scenario;
        public SphStats Stats { get; private set; } = SphStats.Empty;
        /// <summary>Energies are reduced on the GPU every this many steps.</summary>
        public int EnergyInterval { get; set; } = 10;

        public void Load(SphScenario scenario)
        {
            _solver?.Dispose();
            _scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
            var gas = scenario.Gas;
            _solver = new SphGpuSolver(scenario.Parameters, gas.Count + Math.Max(scenario.Headroom, 0));
            _sinks = new SinkManager(scenario.Parameters) { BlackHoleAccretionRadius = scenario.BlackHoleAccretionRadius };
            _sinks.Reset(CloneSinks(scenario.Sinks));
            _stellar = scenario.Stellar != null ? new StellarEvolution(scenario.Stellar) : null;
            _lastSupernovae = 0;
            _stepSecondsEma = 0;

            _solver.Upload(gas);
            if (scenario.RelaxSteps > 0) Relax(scenario);
            _solver.Initialise(sinks: _sinks);
            _energy = _solver.Energies(_sinks);
            PublishStats();
        }

        // Damped relaxation of the initial gas on its own (no sinks), then the launch offset and velocity.
        private void Relax(SphScenario sc)
        {
            _solver.Initialise();
            _solver.Damping = 0.85f;
            for (int k = 0; k < sc.RelaxSteps; k++) _solver.Step();
            _solver.Damping = 1f;
            var s = new SphState(_solver.Capacity);
            _solver.Download(s);
            for (int i = 0; i < s.Count; i++)
            {
                s.Position[i] += sc.LaunchOffset;
                s.Velocity[i] = sc.LaunchVelocity;
                s.Acceleration[i] = Vector3.Zero;
                s.DuDt[i] = 0f;
            }
            _solver.Upload(s);
        }

        public void Step()
        {
            if (_solver == null) return;
            _clock.Restart();
            float dt = _solver.Dt;
            _solver.Step(_sinks);
            if (_stellar != null) EvolveStars(dt);
            if (_solver.Steps % Math.Max(1, EnergyInterval) == 0) _energy = _solver.Energies(_sinks);
            double secs = _clock.Elapsed.TotalSeconds;
            _stepSecondsEma = _stepSecondsEma <= 0 ? secs : 0.9 * _stepSecondsEma + 0.1 * secs;
            PublishStats();
        }

        private void EvolveStars(float dt)
        {
            var ps = _sinks.Sinks;
            _stellar.Evolve(ps, dt);

            // Gas emitted by the stellar model (shells, supernova ejecta) joins the SPH gas.
            _ejecta.Clear();
            var sp = _scenario.Parameters;
            for (int i = 0; i < ps.Count; i++)
            {
                if (ps.Class[i] != BodyClass.Gas || ps.Mass[i] <= 0f) continue;
                float h = Math.Clamp(sp.HMin * 2f, sp.HMin, sp.HMax);
                _ejecta.Add((ps.Position[i], ps.Velocity[i], ps.Mass[i], _scenario.EjectaU, h));
                ps.Mass[i] = 0f;
            }
            if (_ejecta.Count > 0)
            {
                _solver.Append(_ejecta);
                ps.Compact();
            }

            // Supernova energy into the internal energy of nearby gas.
            long sn = _stellar.Supernovae;
            if (sn > _lastSupernovae && _scenario.SupernovaDu > 0f)
            {
                _blasts.Clear();
                int fresh = (int)Math.Min(sn - _lastSupernovae, _stellar.FlashCount);
                for (int k = 0; k < fresh; k++)
                    _blasts.Add((_stellar.FlashPosition(_stellar.FlashCount - 1 - k), _scenario.Stellar.BlastRadius, _scenario.SupernovaDu));
                _solver.InjectEnergy(_blasts);
            }
            _lastSupernovae = sn;
        }

        private void PublishStats()
        {
            var ps = _sinks.Sinks;
            var counts = new int[6];
            float heaviest = 0f;
            for (int i = 0; i < ps.Count; i++)
            {
                counts[(int)ps.Class[i]]++;
                heaviest = MathF.Max(heaviest, ps.Mass[i]);
            }
            counts[(int)BodyClass.Gas] = _solver.Alive;
            Stats = new SphStats
            {
                Gas = _solver.Alive,
                Sinks = ps.Count,
                ClassCounts = counts,
                Steps = _solver.Steps,
                SimTime = _solver.Time,
                SimYears = _scenario.Units.SimToYears(_solver.Time),
                Dt = _solver.Dt,
                StepsPerSecond = _stepSecondsEma > 0 ? 1.0 / _stepSecondsEma : 0,
                Kinetic = _energy.K,
                Thermal = _energy.T,
                Potential = _energy.W,
                StarsFormed = _stellar?.StarsFormed ?? 0,
                SinksCreated = _sinks.Created,
                Accreted = _sinks.AccretedParticles,
                Supernovae = _stellar?.Supernovae ?? 0,
                HeaviestSink = heaviest,
                Times = _solver.Times,
            };
        }

        private static ParticleSystem CloneSinks(ParticleSystem s) => s == null ? new ParticleSystem(64) : s.Clone();

        public void Dispose()
        {
            _solver?.Dispose();
            _solver = null;
        }
    }
}
