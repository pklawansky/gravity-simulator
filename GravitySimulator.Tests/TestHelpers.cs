using System.Numerics;
using GravitySimulator.Planets;

namespace GravitySimulator.Tests;

internal static class TestHelpers
{
    public static ParticleSystem RandomCloud(int count, int seed, float radius = 1f)
    {
        var rng = new Random(seed);
        var ps = new ParticleSystem(count);
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
            ps.Add(p * radius, Vector3.Zero, (float)(0.5 + rng.NextDouble()), 0.001f);
        }
        return ps;
    }

    public static Vector3 TotalMomentum(ParticleSystem ps)
    {
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < ps.Count; i++)
        {
            x += (double)ps.Mass[i] * ps.Velocity[i].X;
            y += (double)ps.Mass[i] * ps.Velocity[i].Y;
            z += (double)ps.Mass[i] * ps.Velocity[i].Z;
        }
        return new Vector3((float)x, (float)y, (float)z);
    }

    public static double TotalMass(ParticleSystem ps)
    {
        double m = 0;
        for (int i = 0; i < ps.Count; i++) m += ps.Mass[i];
        return m;
    }

    /// <summary>Total energy (kinetic + pairwise potential, G = 1, unsoftened) in double precision.</summary>
    public static double TotalEnergy(ParticleSystem ps)
    {
        double e = 0;
        for (int i = 0; i < ps.Count; i++)
        {
            e += 0.5 * ps.Mass[i] * (double)ps.Velocity[i].LengthSquared();
            for (int j = i + 1; j < ps.Count; j++)
            {
                double dx = (double)ps.Position[i].X - ps.Position[j].X;
                double dy = (double)ps.Position[i].Y - ps.Position[j].Y;
                double dz = (double)ps.Position[i].Z - ps.Position[j].Z;
                e -= (double)ps.Mass[i] * ps.Mass[j] / Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
        }
        return e;
    }
}
