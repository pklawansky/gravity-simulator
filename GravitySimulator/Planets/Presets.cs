using System;
using System.Numerics;

namespace GravitySimulator.Planets
{
    public enum PresetKind
    {
        DiscAroundStar,
        SpinningCloud,
        /// <summary>Cosmic scale (M☉, pc): turbulent gas sphere that collapses and forms stars.</summary>
        StarFormingCloud,
        /// <summary>Cosmic scale (M☉, pc): Plummer sphere of Kroupa-IMF stars.</summary>
        DenseStarCluster,
    }

    /// <summary>
    /// Deterministic (per seed) starting states. The planet-scale presets use normalised units
    /// (G = 1) with the stellar model off; the cosmic presets use solar masses and parsecs (G = 1)
    /// with the stellar model on (see <see cref="Configure"/>).
    /// </summary>
    public static class Presets
    {
        public const float StarMass = 1f;
        public const float StarRadius = 0.04f;
        public const float DiscInner = 0.4f;
        public const float DiscOuter = 2.0f;
        public const float DiscThickness = 0.01f;   // Gaussian sigma in z
        public const float DiscDispersion = 0.003f; // velocity dispersion as a fraction of circular speed
        public const float DustRadius = 0.004f;
        /// <summary>Total dust mass as a fraction of the star's mass, used when no fraction is given.</summary>
        public const float DefaultDiscMassFraction = 0.001f;

        public static ParticleSystem Create(PresetKind kind, int count, int seed, float discMassFraction = DefaultDiscMassFraction) => kind switch
        {
            PresetKind.SpinningCloud => SpinningCloud(count, seed),
            PresetKind.StarFormingCloud => StarFormingCloud(count, seed),
            PresetKind.DenseStarCluster => DenseStarCluster(count, seed),
            _ => DiscAroundStar(count, seed, discMassFraction),
        };

        public static bool IsCosmic(PresetKind kind) => kind == PresetKind.StarFormingCloud || kind == PresetKind.DenseStarCluster;

        /// <summary>Default particle count for the preset (interactive on a desktop CPU).</summary>
        public static int DefaultCount(PresetKind kind) => kind switch
        {
            PresetKind.StarFormingCloud => CloudDefaultCount,
            PresetKind.DenseStarCluster => ClusterDefaultCount,
            _ => 50_000,
        };

        /// <summary>Default merge threshold k (x escape speed) for the preset.</summary>
        public static float DefaultMergeThreshold(PresetKind kind) => kind == PresetKind.StarFormingCloud ? CloudMergeThreshold : 1f;

        /// <summary>Default restitution e for the preset.</summary>
        public static float DefaultRestitution(PresetKind kind) => kind == PresetKind.StarFormingCloud ? CloudRestitution : 0.5f;

        /// <summary>Default stellar time acceleration for the cosmic presets.</summary>
        public static float DefaultTimeAcceleration(PresetKind kind) => kind == PresetKind.DenseStarCluster ? ClusterTimeAcceleration : CloudTimeAcceleration;

        /// <summary>
        /// Sets the integration parameters and the stellar model for <paramref name="kind"/>: the
        /// planet presets get the <see cref="SimulationParameters"/> defaults with the stellar model off;
        /// the cosmic presets get their own time step, softening, escape radius, and stellar model.
        /// Merge threshold, restitution and time scale are left to the caller (UI controls).
        /// </summary>
        public static void Configure(PresetKind kind, SimulationParameters p)
        {
            var d = new SimulationParameters();
            switch (kind)
            {
                case PresetKind.StarFormingCloud:
                    p.TimeStep = 1e-4f;
                    p.Softening = 0.01f;
                    p.EscapeRadius = 12f;
                    p.MaxStepsPerSecond = d.MaxStepsPerSecond;
                    p.Stellar = new StellarParameters
                    {
                        Units = UnitSystem.SolarParsec,
                        TimeAcceleration = CloudTimeAcceleration,
                        AccretionRadius = 0.04f,
                        CaptureRadius = 0.003f,
                        StarFormationMass = CloudStarFormationMass,
                        GasParticleMass = CloudMass / CloudDefaultCount,
                        GasRadius = CloudGasRadius,
                        ShellSpeed = 20f,
                        SupernovaEjectaSpeed = 150f,
                        BlastRadius = 0.4f,
                        BlastSpeed = 60f,
                        RadiationSoftening = 0.02f,
                    };
                    break;
                case PresetKind.DenseStarCluster:
                    p.TimeStep = 2e-5f;
                    p.Softening = 0.002f;
                    p.EscapeRadius = 5f;
                    p.MaxStepsPerSecond = d.MaxStepsPerSecond;
                    p.Stellar = new StellarParameters
                    {
                        Units = UnitSystem.SolarParsec,
                        TimeAcceleration = ClusterTimeAcceleration,
                        AccretionRadius = 0.01f,
                        CaptureRadius = 0.002f,
                        StarFormationMass = 1f,
                        GasParticleMass = 0.5f,
                        GasRadius = 0.004f,
                        ShellSpeed = 30f,
                        SupernovaEjectaSpeed = 300f,
                        BlastRadius = 0.1f,
                        BlastSpeed = 50f,
                        RadiationSoftening = 0.005f,
                    };
                    break;
                default:
                    p.TimeStep = d.TimeStep;
                    p.Softening = d.Softening;
                    p.EscapeRadius = d.EscapeRadius;
                    p.MaxStepsPerSecond = d.MaxStepsPerSecond;
                    p.Stellar = null;
                    break;
            }
        }

        // ---- cosmic presets (M☉, pc, G = 1) ----------------------------------------------------

        public const int CloudDefaultCount = 10_000;
        public const float CloudMass = 2000f;          // M☉
        public const float CloudRadius = 2f;           // pc
        public const float CloudGasRadius = 0.015f;    // pc, per gas particle
        public const float CloudVirialRatio = 0.5f;    // 2T/|W| < 1, so it collapses
        public const float CloudStarFormationMass = 1.5f;
        public const float CloudMergeThreshold = 1f;
        public const float CloudRestitution = 0.3f;
        public const float CloudTimeAcceleration = 3f;

        public const int ClusterDefaultCount = 5_000;
        public const float ClusterPlummerRadius = 0.2f; // pc
        public const float ClusterTimeAcceleration = 20f;

        public const float KroupaMin = 0.08f, KroupaBreak = 0.5f, KroupaMax = 100f;

        /// <summary>Spare capacity for ejecta so spawning does not reallocate mid-run.</summary>
        public static int EjectaHeadroom(int count) => Math.Max(1024, count / 4);

        /// <summary>
        /// Uniform sphere (2 pc, 2000 M☉) of equal gas particles with a divergence-free random
        /// (turbulent) velocity field at virial ratio 0.5, so it collapses into filaments and clumps.
        /// Centre of mass at rest.
        /// </summary>
        public static ParticleSystem StarFormingCloud(int count, int seed)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            var rng = new Random(seed);
            var ps = new ParticleSystem(count + EjectaHeadroom(count));
            float m = CloudMass / count;
            // Particles keep the same gas radius whatever the count (it is the clump scale, not a grain).
            for (int i = 0; i < count; i++)
            {
                Vector3 p;
                do
                {
                    p = new Vector3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1));
                } while (p.LengthSquared() > 1f);
                ps.Add(p * CloudRadius, Vector3.Zero, m, CloudGasRadius, BodyClass.Gas);
            }

            // Turbulence: sum of random solenoidal Fourier modes, amplitude ~ k^-2 (Burgers-like, large scales dominate).
            const int Modes = 24;
            var kv = new Vector3[Modes];
            var amp = new Vector3[Modes];
            var phase = new float[Modes];
            for (int k = 0; k < Modes; k++)
            {
                float kmag = MathF.PI * (1f + 3f * (float)rng.NextDouble()) / CloudRadius;
                Vector3 dir = RandomUnit(rng);
                kv[k] = dir * kmag;
                Vector3 a = Vector3.Cross(dir, RandomUnit(rng)); // perpendicular to k: divergence-free
                amp[k] = a / (kmag * kmag);
                phase[k] = (float)(rng.NextDouble() * Math.PI * 2);
            }
            Vector3 mean = Vector3.Zero;
            for (int i = 0; i < count; i++)
            {
                Vector3 v = Vector3.Zero;
                for (int k = 0; k < Modes; k++) v += amp[k] * MathF.Sin(Vector3.Dot(kv[k], ps.Position[i]) + phase[k]);
                ps.Velocity[i] = v;
                mean += v;
            }
            mean /= count;
            double ke = 0;
            for (int i = 0; i < count; i++)
            {
                ps.Velocity[i] -= mean;
                ke += 0.5 * m * ps.Velocity[i].LengthSquared();
            }
            double w = 0.6 * CloudMass * CloudMass / CloudRadius; // |W| of a uniform sphere
            float scale = ke > 0 ? (float)Math.Sqrt(CloudVirialRatio * w / (2 * ke)) : 0f;
            Vector3 com = Vector3.Zero;
            for (int i = 0; i < count; i++)
            {
                ps.Velocity[i] *= scale;
                com += ps.Position[i];
            }
            com /= count;
            for (int i = 0; i < count; i++) ps.Position[i] -= com;
            return ps;
        }

        /// <summary>
        /// Plummer sphere (scale radius 0.2 pc) of stars with Kroupa (2001) masses in 0.08–100 M☉,
        /// in virial equilibrium, all born at age 0. Centre of mass at rest. Massive stars die first
        /// into black holes, which sink to the centre.
        /// </summary>
        public static ParticleSystem DenseStarCluster(int count, int seed)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            var rng = new Random(seed);
            var ps = new ParticleSystem(count + EjectaHeadroom(count));
            float rsun = (float)UnitSystem.SolarParsec.SolarRadius;
            var masses = new float[count];
            double total = 0;
            for (int i = 0; i < count; i++) { masses[i] = SampleKroupa(rng); total += masses[i]; }

            float a = ClusterPlummerRadius;
            Vector3 momentum = Vector3.Zero, com = Vector3.Zero;
            for (int i = 0; i < count; i++)
            {
                // Aarseth, Hénon & Wielen (1974): radius from the cumulative mass, speed by rejection.
                float r;
                do
                {
                    double x = Math.Max(rng.NextDouble(), 1e-10);
                    r = (float)(a / Math.Sqrt(Math.Pow(x, -2.0 / 3.0) - 1.0));
                } while (r > 10f * a);
                double q, g;
                do
                {
                    q = rng.NextDouble();
                    g = rng.NextDouble() * 0.1;
                } while (g > q * q * Math.Pow(1 - q * q, 3.5));
                float vesc = (float)(Math.Sqrt(2.0 * total) * Math.Pow(r * r + a * a, -0.25));
                Vector3 pos = RandomUnit(rng) * r;
                Vector3 vel = RandomUnit(rng) * (float)(q * vesc);
                float m = masses[i];
                ps.Add(pos, vel, m, rsun * MathF.Pow(m, 0.8f), BodyClass.Star);
                momentum += vel * m;
                com += pos * m;
            }
            Vector3 dv = momentum / (float)total, dp = com / (float)total;
            for (int i = 0; i < count; i++)
            {
                ps.Velocity[i] -= dv;
                ps.Position[i] -= dp;
            }
            return ps;
        }

        /// <summary>Kroupa (2001) IMF: dN/dm ~ m^-1.3 for 0.08–0.5 M☉, m^-2.3 for 0.5–100 M☉ (continuous).</summary>
        public static float SampleKroupa(Random rng)
        {
            // Segment integrals of k m^-alpha with k2 = k1 * KroupaBreak (continuity at the break).
            double i1 = (Math.Pow(KroupaMin, -0.3) - Math.Pow(KroupaBreak, -0.3)) / 0.3;
            double i2 = KroupaBreak * (Math.Pow(KroupaBreak, -1.3) - Math.Pow(KroupaMax, -1.3)) / 1.3;
            double u = rng.NextDouble() * (i1 + i2);
            double m;
            if (u < i1)
                m = Math.Pow(Math.Pow(KroupaMin, -0.3) - 0.3 * u, -1.0 / 0.3);
            else
                m = Math.Pow(Math.Pow(KroupaBreak, -1.3) - 1.3 * (u - i1) / KroupaBreak, -1.0 / 1.3);
            return (float)Math.Clamp(m, KroupaMin, KroupaMax);
        }

        private static Vector3 RandomUnit(Random rng)
        {
            Vector3 v;
            float l2;
            do
            {
                v = new Vector3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1));
                l2 = v.LengthSquared();
            } while (l2 > 1f || l2 < 1e-6f);
            return v / MathF.Sqrt(l2);
        }

        /// <summary>
        /// Index 0 is a central star of mass 1; the remaining count-1 particles share a total dust mass
        /// of <paramref name="discMassFraction"/> x the star's mass. They
        /// form a thin disc with near-circular prograde (about +Z) Keplerian
        /// velocities plus a small random dispersion. Total momentum is zero.
        /// </summary>
        public static ParticleSystem DiscAroundStar(int count, int seed, float discMassFraction = DefaultDiscMassFraction)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            if (!(discMassFraction > 0f)) throw new ArgumentOutOfRangeException(nameof(discMassFraction));
            var rng = new Random(seed);
            var ps = new ParticleSystem(count);
            ps.Add(Vector3.Zero, Vector3.Zero, StarMass, StarRadius);

            int dustCount = count - 1;
            if (dustCount == 0) return ps;
            float discMass = StarMass * discMassFraction;
            float dustMass = discMass / dustCount;
            Vector3 momentum = Vector3.Zero;
            for (int i = 0; i < dustCount; i++)
            {
                // Uniform in r => surface density ~ 1/r.
                float r = DiscInner + (float)rng.NextDouble() * (DiscOuter - DiscInner);
                float a = (float)(rng.NextDouble() * Math.PI * 2);
                float z = Gaussian(rng) * DiscThickness;
                var pos = new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), z);

                // Enclosed dust mass (uniform-in-r disc) adds slightly to the star's pull.
                float enclosed = StarMass + discMass * (r - DiscInner) / (DiscOuter - DiscInner);
                float vc = MathF.Sqrt(enclosed / r);
                var vel = new Vector3(-MathF.Sin(a), MathF.Cos(a), 0f) * vc;
                vel += new Vector3(Gaussian(rng), Gaussian(rng), Gaussian(rng) * 0.5f) * (DiscDispersion * vc);

                ps.Add(pos, vel, dustMass, DustRadius);
                momentum += vel * dustMass;
            }
            ps.Velocity[0] = -momentum / StarMass;
            return ps;
        }

        /// <summary>
        /// Uniform sphere (radius 1, total mass 1) of equal dust grains in solid-body rotation about +Z
        /// with rotational kinetic energy at 30% of |W| (below the virial 50%), so it collapses
        /// into a flattened, spinning system. Centre of mass at rest.
        /// </summary>
        public static ParticleSystem SpinningCloud(int count, int seed)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            var rng = new Random(seed);
            var ps = new ParticleSystem(count);
            const float R = 1f, M = 1f;
            float m = M / count;
            // KE_rot = 0.5 * (2/5 M R^2) w^2 = 0.3 * (3/5 M^2 / R)  =>  w^2 = 0.9 M / R^3
            float omega = MathF.Sqrt(0.9f * M / (R * R * R));
            float radius = 0.25f * R / MathF.Cbrt(count); // grains fill ~1.6% of the volume
            Vector3 momentum = Vector3.Zero;
            Vector3 com = Vector3.Zero;
            for (int i = 0; i < count; i++)
            {
                Vector3 p;
                do
                {
                    p = new Vector3(
                        (float)(rng.NextDouble() * 2 - 1),
                        (float)(rng.NextDouble() * 2 - 1),
                        (float)(rng.NextDouble() * 2 - 1));
                } while (p.LengthSquared() > 1f);
                p *= R;
                var v = new Vector3(-p.Y, p.X, 0f) * omega;
                ps.Add(p, v, m, radius);
                momentum += v * m;
                com += p * m;
            }
            Vector3 dv = momentum / M, dp = com / M;
            for (int i = 0; i < count; i++)
            {
                ps.Velocity[i] -= dv;
                ps.Position[i] -= dp;
            }
            return ps;
        }

        private static float Gaussian(Random rng)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }
    }
}
