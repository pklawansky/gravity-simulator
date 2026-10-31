using System;
using System.Collections.Generic;
using System.Numerics;
using GravitySimulator.Planets;

namespace GravitySimulator.Sph
{
    /// <summary>A gas particle absorbed this step by sink <see cref="Sink"/> (index into the uploaded sink list).</summary>
    public struct AccretionEvent
    {
        public int Sink;
        public float Mass;
        public Vector3 Position;
        public Vector3 Velocity;
    }

    /// <summary>A dense, converging, bound gas particle that may become a sink.</summary>
    public struct SinkCandidate
    {
        public int Index;
        public Vector3 Position;
        public float Density;
    }

    /// <summary>
    /// CPU side of the sinks (Bate, Bonnell &amp; Price 1995). Sinks live in a <see cref="ParticleSystem"/>
    /// so the stellar model (<see cref="StellarEvolution"/>) can evolve them like Planet formation's
    /// bodies: a new sink starts as <see cref="BodyClass.Planet"/> (a protostar) and ignites once it has
    /// accreted 0.08 M☉. The GPU decides which gas is absorbed; this class books the absorbed mass,
    /// momentum and centre of mass onto the sinks, so both are conserved exactly.
    /// </summary>
    public sealed class SinkManager
    {
        private readonly SphParameters _p;
        private double[] _formM = Array.Empty<double>();
        private Vector3[] _formMx = Array.Empty<Vector3>(), _formMv = Array.Empty<Vector3>();
        private readonly List<SinkCandidate> _sorted = new List<SinkCandidate>();

        public SinkManager(SphParameters parameters, int capacity = 64)
        {
            _p = parameters ?? throw new ArgumentNullException(nameof(parameters));
            Sinks = new ParticleSystem(capacity);
        }

        /// <summary>The sinks (stars, protostars, remnants, black holes).</summary>
        public ParticleSystem Sinks { get; private set; }

        /// <summary>Centres of sinks accepted this step; they gather their gas in the next accretion pass.</summary>
        public List<Vector3> Forming { get; } = new List<Vector3>();

        /// <summary>Black holes use this accretion radius when it is positive (otherwise <see cref="SphParameters.SinkRadius"/>).</summary>
        public float BlackHoleAccretionRadius { get; set; }

        /// <summary>Total sinks created from gas.</summary>
        public long Created { get; private set; }
        /// <summary>Total gas particles absorbed by sinks (including those that formed sinks).</summary>
        public long AccretedParticles { get; private set; }
        /// <summary>
        /// Gas mass absorbed by black holes, summed in double precision: a TDE particle is ~10⁻⁸ of the
        /// hole's mass, below float resolution, so the hole's float mass alone does not show it.
        /// </summary>
        public double BlackHoleAccretedMass { get; private set; }

        public void Reset(ParticleSystem sinks)
        {
            Sinks = sinks ?? new ParticleSystem(64);
            Forming.Clear();
            Created = AccretedParticles = 0;
            BlackHoleAccretedMass = 0;
        }

        public float AccretionRadius(int sink) =>
            Sinks.Class[sink] == BodyClass.BlackHole && BlackHoleAccretionRadius > 0f ? BlackHoleAccretionRadius : _p.SinkRadius;

        /// <summary>
        /// Accepts candidates (densest first) that are at least two sink radii from every existing,
        /// forming or just-accepted sink. Returns how many were accepted into <see cref="Forming"/>.
        /// </summary>
        public int SelectNewSinks(IReadOnlyList<SinkCandidate> candidates)
        {
            if (candidates.Count == 0 || !(_p.SinkRadius > 0f)) return 0;
            _sorted.Clear();
            for (int k = 0; k < candidates.Count; k++) _sorted.Add(candidates[k]);
            _sorted.Sort((a, b) => b.Density.CompareTo(a.Density));
            float min2 = 4f * _p.SinkRadius * _p.SinkRadius;
            int accepted = 0;
            foreach (var c in _sorted)
            {
                bool clear = true;
                for (int k = 0; k < Sinks.Count && clear; k++)
                {
                    float r = MathF.Max(2f * AccretionRadius(k), 2f * _p.SinkRadius);
                    if ((Sinks.Position[k] - c.Position).LengthSquared() < r * r) clear = false;
                }
                for (int k = 0; k < Forming.Count && clear; k++)
                    if ((Forming[k] - c.Position).LengthSquared() < min2) clear = false;
                if (!clear) continue;
                Forming.Add(c.Position);
                accepted++;
            }
            return accepted;
        }

        /// <summary>
        /// Books this step's accretion events (sink indices: existing sinks first, then
        /// <see cref="Forming"/>), creates the new sinks from the gas they gathered, and clears
        /// <see cref="Forming"/>. Returns the number of sinks created.
        /// </summary>
        public int Apply(IReadOnlyList<AccretionEvent> events)
        {
            int existing = Sinks.Count, forming = Forming.Count;
            if (_formM.Length < forming)
            {
                _formM = new double[forming];
                _formMx = new Vector3[forming];
                _formMv = new Vector3[forming];
            }
            Array.Clear(_formM, 0, forming);
            Array.Clear(_formMx, 0, forming);
            Array.Clear(_formMv, 0, forming);

            var ps = Sinks;
            for (int e = 0; e < events.Count; e++)
            {
                var ev = events[e];
                if (ev.Sink < existing)
                {
                    int k = ev.Sink;
                    if (ps.Class[k] == BodyClass.BlackHole) BlackHoleAccretedMass += ev.Mass;
                    float ms = ps.Mass[k], mt = ms + ev.Mass;
                    ps.Position[k] = (ps.Position[k] * ms + ev.Position * ev.Mass) / mt;
                    ps.Velocity[k] = (ps.Velocity[k] * ms + ev.Velocity * ev.Mass) / mt;
                    ps.Mass[k] = mt;
                }
                else if (ev.Sink < existing + forming)
                {
                    int f = ev.Sink - existing;
                    _formM[f] += ev.Mass;
                    _formMx[f] += ev.Position * ev.Mass;
                    _formMv[f] += ev.Velocity * ev.Mass;
                }
                else continue;
                AccretedParticles++;
            }

            int created = 0;
            for (int f = 0; f < forming; f++)
            {
                if (_formM[f] <= 0) continue;
                float m = (float)_formM[f];
                ps.Add(_formMx[f] / m, _formMv[f] / m, m, 0.3f * _p.SinkRadius, BodyClass.Planet);
                created++;
            }
            Created += created;
            Forming.Clear();
            return created;
        }
    }
}
