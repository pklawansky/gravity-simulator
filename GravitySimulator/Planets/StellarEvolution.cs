using System;
using System.Numerics;
using System.Threading.Tasks;

namespace GravitySimulator.Planets
{
    /// <summary>
    /// The sub-grid stellar model as an <see cref="IBodyEvolution"/>. Each step:
    /// <list type="number">
    /// <item>Sink accretion (Bate, Bonnell &amp; Price 1995): stars and remnants absorb gas inside the
    /// accretion radius that is bound to them; black holes also capture any bound body inside the
    /// capture radius (a heavier sink absorbs a lighter one). Mass, momentum and centre of mass are conserved.</item>
    /// <item>Classification and ageing: gas clumps collapse at the star-formation mass, bodies over
    /// 0.08 M☉ ignite, white dwarfs over 1.4 M☉ become neutron stars, neutron stars over 2.5 M☉ black
    /// holes; stars age by dt x time acceleration and die at t = 10 Gyr M^-2.5.</item>
    /// <item>End of life: a shell (white dwarf) or supernova ejecta are emitted as gas particles in
    /// opposed pairs, so ejecta + remnant conserve the progenitor's mass and momentum; supernovae kick
    /// nearby bodies outward.</item>
    /// </list>
    /// Radii follow the class: stars R☉ M^0.8, white dwarfs 0.01 R☉ (M/0.6)^-1/3, neutron stars 12 km,
    /// black holes the event horizon 2GM/c². Gas and planets keep their volume radius.
    /// </summary>
    public sealed class StellarEvolution : IBodyEvolution
    {
        /// <summary>How many recent supernova flashes are kept for the renderer.</summary>
        public const int MaxFlashes = 16;
        /// <summary>Steps a flash stays visible.</summary>
        public const int FlashSteps = 90;

        private readonly StellarParameters _p;
        private readonly double _lifetimeSimPerSolar; // 10 Gyr in sim time
        private readonly float _rsun, _c2, _nsRadius;
        private readonly Random _rng;

        private int[] _claim = Array.Empty<int>();
        private int[] _sinks = Array.Empty<int>();
        private int[] _bhSinks = Array.Empty<int>();
        private int _sinkCount, _bhCount, _n, _chunkSize;
        private float _racc2, _rcap2;
        private ParticleSystem _ps;
        private readonly Action<int> _claimChunk;
        private Vector3[] _dirs = Array.Empty<Vector3>();

        private readonly Vector3[] _flashPos = new Vector3[MaxFlashes];
        private readonly int[] _flashAge = new int[MaxFlashes];
        private int _flashCount;

        public StellarEvolution(StellarParameters parameters)
        {
            _p = parameters ?? throw new ArgumentNullException(nameof(parameters));
            var u = _p.Units;
            _lifetimeSimPerSolar = u.YearsToSim(StellarPhysics.SolarLifetimeYears);
            _rsun = (float)u.SolarRadius;
            _c2 = (float)(u.SpeedOfLight * u.SpeedOfLight);
            _nsRadius = (float)u.MetresToSim(12_000);
            _rng = new Random(_p.Seed);
            _claimChunk = ClaimChunk;
        }

        public StellarParameters Parameters => _p;

        public long StarsFormed { get; private set; }
        public long Supernovae { get; private set; }
        public long WhiteDwarfsFormed { get; private set; }
        public long NeutronStarsFormed { get; private set; }
        public long BlackHolesFormed { get; private set; }
        /// <summary>Black holes absorbing a star or a compact object by sink capture.</summary>
        public long BlackHoleMergers { get; private set; }
        public long Accreted { get; private set; }

        /// <summary>Recent supernova flashes (position, 0..1 brightness), newest last.</summary>
        public int FlashCount => _flashCount;
        public Vector3 FlashPosition(int k) => _flashPos[k];
        public float FlashBrightness(int k) => 1f - _flashAge[k] / (float)FlashSteps;

        /// <summary>Main-sequence lifetime in simulation time (before the time acceleration).</summary>
        public double LifetimeSim(float mass) => _lifetimeSimPerSolar * Math.Pow(Math.Max(mass, 1e-6f), -2.5);

        public void Evolve(ParticleSystem ps, float dt)
        {
            AgeFlashes();
            Accrete(ps);
            ClassifyAndAge(ps, dt);
        }

        // ---- sink accretion ----------------------------------------------------------------------

        private static bool IsSink(BodyClass c) => c >= BodyClass.Star;

        private void Accrete(ParticleSystem ps)
        {
            int n = ps.Count;
            float racc = _p.AccretionRadius;
            if (n < 2 || !(racc > 0f)) return;
            if (_claim.Length < ps.Capacity)
            {
                _claim = new int[ps.Capacity];
                _sinks = new int[ps.Capacity];
                _bhSinks = new int[ps.Capacity];
            }
            _sinkCount = _bhCount = 0;
            for (int i = 0; i < n; i++)
            {
                BodyClass c = ps.Class[i];
                if (!IsSink(c) || ps.Mass[i] <= 0f) continue;
                _sinks[_sinkCount++] = i;
                if (c == BodyClass.BlackHole) _bhSinks[_bhCount++] = i;
            }
            if (_sinkCount == 0) return;

            _ps = ps;
            _n = n;
            _racc2 = racc * racc;
            float rcap = _p.CaptureRadius > 0f ? _p.CaptureRadius : racc;
            _rcap2 = rcap * rcap;
            int chunks = Math.Min(n, Environment.ProcessorCount * 4);
            _chunkSize = (n + chunks - 1) / chunks;
            if (n < 256) { for (int c = 0; c < chunks; c++) _claimChunk(c); }
            else Parallel.For(0, chunks, _claimChunk);
            _ps = null;

            int absorbed = 0;
            for (int i = 0; i < n; i++)
            {
                int s = _claim[i];
                if (s < 0 || ps.Mass[i] <= 0f || ps.Mass[s] <= 0f) continue;
                Absorb(ps, s, i);
                absorbed++;
            }
            if (absorbed > 0)
            {
                Accreted += absorbed;
                ps.Compact();
            }
        }

        // For each body, the most tightly bound eligible sink inside the accretion radius (or -1).
        private void ClaimChunk(int c)
        {
            int start = c * _chunkSize, end = Math.Min(_n, start + _chunkSize);
            var ps = _ps;
            for (int i = start; i < end; i++)
            {
                _claim[i] = -1;
                float mi = ps.Mass[i];
                if (mi <= 0f) continue;
                BodyClass ci = ps.Class[i];
                // Gas can fall into any sink; anything else only into a black hole.
                int[] list = ci == BodyClass.Gas ? _sinks : _bhSinks;
                int count = ci == BodyClass.Gas ? _sinkCount : _bhCount;
                float r2 = ci == BodyClass.Gas ? _racc2 : _rcap2;
                Vector3 pi = ps.Position[i], vi = ps.Velocity[i];
                float best = 0f;
                for (int k = 0; k < count; k++)
                {
                    int s = list[k];
                    if (s == i) continue;
                    float ms = ps.Mass[s];
                    // A sink is only absorbed by a heavier one (ties: lower index wins), never mutually.
                    if (IsSink(ci) && (ms < mi || (ms == mi && s > i))) continue;
                    Vector3 d = ps.Position[s] - pi;
                    float d2 = d.LengthSquared();
                    if (d2 >= r2) continue;
                    float dist = MathF.Max(MathF.Sqrt(d2), 1e-12f);
                    float energy = 0.5f * (ps.Velocity[s] - vi).LengthSquared() - (ms + mi) / dist; // specific orbital energy
                    if (energy < best) { best = energy; _claim[i] = s; }
                }
            }
        }

        private void Absorb(ParticleSystem ps, int sink, int body)
        {
            float ms = ps.Mass[sink], mb = ps.Mass[body], mt = ms + mb;
            BodyClass cs = ps.Class[sink], cb = ps.Class[body];
            var (cls, age) = StellarPhysics.MergeClass(cs, ps.Age[sink], ms, cb, ps.Age[body], mb);
            if (cls == BodyClass.BlackHole && CollisionResolver.IsBlackHoleMerger(cs, cb)) BlackHoleMergers++;
            ps.Position[sink] = (ps.Position[sink] * ms + ps.Position[body] * mb) / mt;
            ps.Velocity[sink] = (ps.Velocity[sink] * ms + ps.Velocity[body] * mb) / mt;
            ps.Acceleration[sink] = (ps.Acceleration[sink] * ms + ps.Acceleration[body] * mb) / mt;
            ps.Mass[sink] = mt;
            ps.Class[sink] = cls;
            ps.Age[sink] = age;
            ps.Mass[body] = 0f;
        }

        // ---- classification, ageing, end of life -------------------------------------------------

        private void ClassifyAndAge(ParticleSystem ps, float dt)
        {
            float ageStep = dt * _p.TimeAcceleration;
            int n = ps.Count; // bodies appended this step (ejecta) are not processed until the next one
            for (int i = 0; i < n; i++)
            {
                float m = ps.Mass[i];
                if (m <= 0f) continue;
                switch (ps.Class[i])
                {
                    case BodyClass.Gas:
                        if (m >= _p.StarFormationMass) Ignite(ps, i, StellarPhysics.ClassifyNonDegenerate(m));
                        break;
                    case BodyClass.Planet:
                        if (m >= StellarPhysics.FusionMass) Ignite(ps, i, BodyClass.Star);
                        break;
                    case BodyClass.Star:
                        ps.Age[i] += ageStep;
                        if (ps.Age[i] >= LifetimeSim(m)) EndOfLife(ps, i);
                        break;
                    case BodyClass.WhiteDwarf:
                    case BodyClass.NeutronStar:
                        BodyClass r = StellarPhysics.ClassifyRemnant(m);
                        if (r > ps.Class[i])
                        {
                            ps.Class[i] = r;
                            if (r == BodyClass.NeutronStar) NeutronStarsFormed++;
                            else BlackHolesFormed++;
                        }
                        break;
                }
                ps.Radius[i] = RadiusFor(ps.Class[i], ps.Mass[i], ps.Radius[i]);
            }
        }

        private void Ignite(ParticleSystem ps, int i, BodyClass cls)
        {
            ps.Class[i] = cls;
            ps.Age[i] = 0f;
            if (cls == BodyClass.Star) StarsFormed++;
        }

        /// <summary>Radius by class (simulation units); gas and planets keep <paramref name="current"/>.</summary>
        public float RadiusFor(BodyClass cls, float mass, float current) => cls switch
        {
            BodyClass.Star => _rsun * MathF.Pow(mass, 0.8f),
            BodyClass.WhiteDwarf => 0.01f * _rsun * MathF.Pow(MathF.Max(mass, 1e-3f) / 0.6f, -1f / 3f),
            BodyClass.NeutronStar => _nsRadius,
            BodyClass.BlackHole => 2f * mass / _c2,
            _ => current,
        };

        private void EndOfLife(ParticleSystem ps, int i)
        {
            float m = ps.Mass[i];
            EndOfLifeResult r = StellarPhysics.EndOfLife(m);
            ps.Class[i] = r.Remnant;
            ps.Mass[i] = r.RemnantMass;
            ps.Age[i] = 0f;
            switch (r.Remnant)
            {
                case BodyClass.WhiteDwarf: WhiteDwarfsFormed++; break;
                case BodyClass.NeutronStar: NeutronStarsFormed++; break;
                default: BlackHolesFormed++; break;
            }
            float ejecta = m - r.RemnantMass;
            if (r.Supernova)
            {
                Supernovae++;
                AddFlash(ps.Position[i]);
                Blast(ps, i);
            }
            if (ejecta > 0f) Emit(ps, i, ejecta, r.Supernova ? _p.SupernovaEjectaSpeed : _p.ShellSpeed);
        }

        // Ejecta in opposed pairs (d, -d) on a randomly rotated Fibonacci sphere: the pairs cancel, so the
        // ejecta's total momentum is exactly (their mass) x (progenitor velocity) and the centre of mass stays put.
        private void Emit(ParticleSystem ps, int i, float ejectaMass, float speed)
        {
            int count = (int)MathF.Round(ejectaMass / MathF.Max(_p.GasParticleMass, 1e-12f));
            int max = Math.Max(2, _p.MaxEjecta & ~1);
            count = Math.Clamp(count, 2, max) & ~1;
            int pairs = count / 2;
            if (_dirs.Length < pairs) _dirs = new Vector3[Math.Max(pairs, max / 2)];
            var rot = Quaternion.Normalize(new Quaternion((float)(_rng.NextDouble() - 0.5), (float)(_rng.NextDouble() - 0.5),
                (float)(_rng.NextDouble() - 0.5), (float)(_rng.NextDouble() - 0.5) + 1e-3f));
            float golden = MathF.PI * (3f - MathF.Sqrt(5f));
            for (int k = 0; k < pairs; k++)
            {
                // Upper hemisphere only; the partner of each direction covers the lower one.
                float z = 1f - (k + 0.5f) / pairs;
                float rr = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
                float a = golden * k;
                _dirs[k] = Vector3.Transform(new Vector3(rr * MathF.Cos(a), rr * MathF.Sin(a), z), rot);
            }
            ps.EnsureCapacity(ps.Count + count); // presets preallocate headroom, so this rarely allocates
            float each = ejectaMass / count;
            float offset = MathF.Max(_p.GasRadius * 5f, ps.Radius[i] * 2f);
            Vector3 p0 = ps.Position[i], v0 = ps.Velocity[i];
            for (int k = 0; k < pairs; k++)
            {
                Vector3 d = _dirs[k];
                ps.Add(p0 + d * offset, v0 + d * speed, each, _p.GasRadius, BodyClass.Gas);
                ps.Add(p0 - d * offset, v0 - d * speed, each, _p.GasRadius, BodyClass.Gas);
            }
        }

        // Outward kick for nearby gas and planets (stars and remnants are too dense for the blast wave to
        // move), falling off linearly with distance and weakened for bodies heavier than a gas particle.
        private void Blast(ParticleSystem ps, int centre)
        {
            float rb = _p.BlastRadius, vb = _p.BlastSpeed;
            if (!(rb > 0f) || !(vb > 0f)) return;
            Vector3 c = ps.Position[centre];
            float rb2 = rb * rb;
            for (int j = 0; j < ps.Count; j++)
            {
                BodyClass cj = ps.Class[j];
                if (j == centre || ps.Mass[j] <= 0f || (cj != BodyClass.Gas && cj != BodyClass.Planet)) continue;
                Vector3 d = ps.Position[j] - c;
                float d2 = d.LengthSquared();
                if (d2 >= rb2 || d2 <= 0f) continue;
                float dist = MathF.Sqrt(d2);
                float weight = MathF.Min(1f, _p.GasParticleMass / ps.Mass[j]);
                ps.Velocity[j] += d / dist * (vb * (1f - dist / rb) * weight);
            }
        }

        private void AddFlash(Vector3 p)
        {
            if (_flashCount == MaxFlashes)
            {
                Array.Copy(_flashPos, 1, _flashPos, 0, MaxFlashes - 1);
                Array.Copy(_flashAge, 1, _flashAge, 0, MaxFlashes - 1);
                _flashCount--;
            }
            _flashPos[_flashCount] = p;
            _flashAge[_flashCount] = 0;
            _flashCount++;
        }

        private void AgeFlashes()
        {
            int w = 0;
            for (int k = 0; k < _flashCount; k++)
            {
                int age = _flashAge[k] + 1;
                if (age >= FlashSteps) continue;
                _flashPos[w] = _flashPos[k];
                _flashAge[w] = age;
                w++;
            }
            _flashCount = w;
        }
    }
}
