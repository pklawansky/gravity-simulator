using System.Numerics;
using GravitySimulator.Sph;

namespace GravitySimulator.Tests;

/// <summary>Initial conditions and analytic solutions for the SPH tests.</summary>
internal static class SphTestHelpers
{
    /// <summary>Cubic lattice of nx*ny*nz particles of spacing dx starting at origin + dx/2, uniform density rho.</summary>
    public static SphState Lattice(int nx, int ny, int nz, float dx, float rho, float u, Vector3 origin = default, float eta = SphKernel.DefaultEta)
    {
        var s = new SphState(nx * ny * nz);
        float m = rho * dx * dx * dx;
        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                    s.Add(origin + new Vector3((i + 0.5f) * dx, (j + 0.5f) * dx, (k + 0.5f) * dx), Vector3.Zero, m, u, eta * dx, eta);
        return s;
    }

    /// <summary>Random cloud in a unit-ish sphere with random velocities, internal energies and masses.</summary>
    public static SphState RandomCloud(int count, int seed, float radius = 1f)
    {
        var rng = new Random(seed);
        var s = new SphState(count);
        float meanMass = 1f / count;
        for (int i = 0; i < count; i++)
        {
            Vector3 p;
            do p = new Vector3(Next(rng), Next(rng), Next(rng)); while (p.LengthSquared() > 1f);
            p *= radius;
            var v = new Vector3(Next(rng), Next(rng), Next(rng)) * 0.3f;
            float m = meanMass * (0.5f + (float)rng.NextDouble());
            float u = 0.05f + 0.1f * (float)rng.NextDouble();
            // h scaled to the local density of a uniform sphere, with scatter so h_i != h_j.
            float h = SphKernel.DefaultEta * MathF.Cbrt(4.18879f * radius * radius * radius / count) * (0.8f + 0.4f * (float)rng.NextDouble());
            s.Add(p, v, m, u, h);
        }
        return s;
        static float Next(Random r) => (float)(r.NextDouble() * 2 - 1);
    }

    /// <summary>
    /// Evrard (1988) collapse: M = R = G = 1, ρ ∝ 1/r, u = 0.05, at rest. A cubic lattice inside the
    /// unit sphere is stretched radially (r → r^{3/2}) so that M(r) ∝ r².
    /// </summary>
    public static SphState Evrard(int approxCount, float eta = SphKernel.DefaultEta)
    {
        int n = (int)MathF.Ceiling(MathF.Cbrt(approxCount * 6f / MathF.PI));
        var pts = new List<Vector3>();
        float dx = 2f / n;
        for (int k = 0; k < n; k++)
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    var p = new Vector3(-1f + (i + 0.5f) * dx, -1f + (j + 0.5f) * dx, -1f + (k + 0.5f) * dx);
                    float r0 = p.Length();
                    if (r0 >= 1f || r0 == 0f) continue;
                    pts.Add(p / r0 * MathF.Pow(r0, 1.5f));
                }
        var s = new SphState(pts.Count);
        float m = 1f / pts.Count;
        foreach (var p in pts)
        {
            float r = MathF.Max(p.Length(), 1e-3f);
            float rho = 1f / (2f * MathF.PI * r);
            s.Add(p, Vector3.Zero, m, 0.05f, eta * MathF.Cbrt(m / rho), eta);
        }
        return s;
    }

    /// <summary>
    /// Exact solution of the Riemann problem (Toro, ch. 4) for an ideal gas, sampled at x/t.
    /// Returns (rho, u, p).
    /// </summary>
    public static (double Rho, double U, double P) ExactRiemann(double rhoL, double uL, double pL, double rhoR, double uR, double pR, double gamma, double s)
    {
        double cL = Math.Sqrt(gamma * pL / rhoL), cR = Math.Sqrt(gamma * pR / rhoR);
        double g1 = (gamma - 1) / (2 * gamma), g2 = (gamma + 1) / (2 * gamma), g3 = 2 * gamma / (gamma - 1);
        double g4 = 2 / (gamma - 1), g5 = 2 / (gamma + 1), g6 = (gamma - 1) / (gamma + 1), g7 = (gamma - 1) / 2;

        (double f, double df) F(double p, double rhoK, double pK, double cK)
        {
            if (p <= pK)
            {
                double pr = p / pK;
                return (g4 * cK * (Math.Pow(pr, g1) - 1), 1 / (rhoK * cK) * Math.Pow(pr, -g2));
            }
            double aK = g5 / rhoK, bK = g6 * pK;
            double q = Math.Sqrt(aK / (bK + p));
            return ((p - pK) * q, (1 - (p - pK) / (2 * (bK + p))) * q);
        }

        double pStar = Math.Max(1e-8, 0.5 * (pL + pR));
        for (int it = 0; it < 100; it++)
        {
            var (fL, dL) = F(pStar, rhoL, pL, cL);
            var (fR, dR) = F(pStar, rhoR, pR, cR);
            double pNew = Math.Max(1e-10, pStar - (fL + fR + uR - uL) / (dL + dR));
            if (Math.Abs(pNew - pStar) < 1e-12 * pStar) { pStar = pNew; break; }
            pStar = pNew;
        }
        double uStar = 0.5 * (uL + uR) + 0.5 * (F(pStar, rhoR, pR, cR).f - F(pStar, rhoL, pL, cL).f);

        if (s <= uStar)
        {
            if (pStar > pL)
            {
                double sL = uL - cL * Math.Sqrt(g2 * pStar / pL + g1);
                if (s <= sL) return (rhoL, uL, pL);
                return (rhoL * (pStar / pL + g6) / (g6 * pStar / pL + 1), uStar, pStar);
            }
            double shL = uL - cL, cStarL = cL * Math.Pow(pStar / pL, g1), stL = uStar - cStarL;
            if (s <= shL) return (rhoL, uL, pL);
            if (s > stL) return (rhoL * Math.Pow(pStar / pL, 1 / gamma), uStar, pStar);
            double c = g5 * (cL + g7 * (uL - s));
            double rho = rhoL * Math.Pow(c / cL, g4);
            return (rho, g5 * (cL + g7 * uL + s), pL * Math.Pow(c / cL, g3));
        }
        else
        {
            if (pStar > pR)
            {
                double sR = uR + cR * Math.Sqrt(g2 * pStar / pR + g1);
                if (s >= sR) return (rhoR, uR, pR);
                return (rhoR * (pStar / pR + g6) / (g6 * pStar / pR + 1), uStar, pStar);
            }
            double shR = uR + cR, cStarR = cR * Math.Pow(pStar / pR, g1), stR = uStar + cStarR;
            if (s >= shR) return (rhoR, uR, pR);
            if (s < stR) return (rhoR * Math.Pow(pStar / pR, 1 / gamma), uStar, pStar);
            double c = g5 * (cR - g7 * (uR - s));
            double rho = rhoR * Math.Pow(c / cR, g4);
            return (rho, g5 * (-cR + g7 * uR + s), pR * Math.Pow(c / cR, g3));
        }
    }

    public static double TotalMass(SphState s)
    {
        double m = 0;
        for (int i = 0; i < s.Count; i++) m += s.Mass[i];
        return m;
    }

    public static Vector3 TotalMomentum(SphState s)
    {
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < s.Count; i++)
        {
            x += (double)s.Mass[i] * s.Velocity[i].X;
            y += (double)s.Mass[i] * s.Velocity[i].Y;
            z += (double)s.Mass[i] * s.Velocity[i].Z;
        }
        return new Vector3((float)x, (float)y, (float)z);
    }
}
