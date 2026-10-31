using System;
using System.Numerics;

namespace GravitySimulator.Planets
{
    public enum PresetKind
    {
        DiscAroundStar,
        SpinningCloud,
    }

    /// <summary>Deterministic (per seed) starting states. Normalised units, G = 1.</summary>
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
            _ => DiscAroundStar(count, seed, discMassFraction),
        };

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
