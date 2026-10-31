using System.Numerics;
using GravitySimulator.Planets;
using GravitySimulator.Sph;
using Xunit.Abstractions;

namespace GravitySimulator.Tests;

/// <summary>CPU reference SPH: kernel, neighbour search, tree layout and physics validation (always run).</summary>
public class SphReferenceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0.05f)]
    [InlineData(0.7f)]
    [InlineData(3f)]
    public void Kernel_IsNormalised_AndCompact(float h)
    {
        // ∫ W dV = ∫_0^{2h} 4π r² W(r) dr = 1 (midpoint rule).
        const int steps = 20000;
        double sum = 0, dr = 2.0 * h / steps;
        for (int k = 0; k < steps; k++)
        {
            double r = (k + 0.5) * dr;
            sum += 4 * Math.PI * r * r * SphKernel.W((float)r, h) * dr;
        }
        Assert.Equal(1.0, sum, 4);
        Assert.Equal(0f, SphKernel.W(2f * h, h));
        Assert.Equal(0f, SphKernel.W(2.5f * h, h));
        Assert.Equal(0f, SphKernel.DWdr(2.1f * h, h));
    }

    [Fact]
    public void Kernel_Gradient_IsAntisymmetric_AndMatchesFiniteDifference()
    {
        var rng = new Random(3);
        for (int t = 0; t < 200; t++)
        {
            float h = 0.1f + (float)rng.NextDouble();
            var xij = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 3f * h;
            float r = xij.Length();
            Vector3 g = SphKernel.GradW(xij, r, h);
            Vector3 gBack = SphKernel.GradW(-xij, r, h);
            Assert.True((g + gBack).Length() <= 1e-6f * (g.Length() + 1e-12f), "∇W(x_ij) = -∇W(x_ji)");
            // dW/dr by central difference (double precision on the analytic form via float calls).
            float e = 1e-3f * h;
            float fd = (SphKernel.W(r + e, h) - SphKernel.W(r - e, h)) / (2 * e);
            if (r > e && r < 2 * h - e)
                Assert.Equal(fd, SphKernel.DWdr(r, h), MathF.Max(1e-2f * MathF.Abs(fd), 1e-3f / (h * h * h * h)));
            // Gradient points along x_ij with magnitude |dW/dr| (W decreases outward).
            if (r > 0 && r < 2 * h)
                Assert.True(Vector3.Dot(g, xij) <= 0f);
        }
    }

    [Fact]
    public void UniformLattice_DensityWithinOnePercent_Periodic()
    {
        const int n = 12;
        const float dx = 1f / n, rho0 = 2.5f;
        var s = SphTestHelpers.Lattice(n, n, n, dx, rho0, 1f);
        var p = new SphParameters { Period = new Vector3(1f), SelfGravity = false };
        var sph = new SphReference(p);
        sph.Predict(s, 0f);
        sph.ComputeDensity(s);
        for (int i = 0; i < s.Count; i++)
            Assert.InRange(s.Density[i], rho0 * 0.99f, rho0 * 1.01f);
        output.WriteLine($"lattice density / rho0 = {s.Density[0] / rho0:F5}");
    }

    [Fact]
    public void GridNeighbours_EqualBruteForce()
    {
        var s = SphTestHelpers.RandomCloud(3000, seed: 11);
        var sph = new SphReference(new SphParameters { SelfGravity = false });
        int[][] lists = sph.NeighbourLists(s);
        var b = new List<int>();
        for (int i = 0; i < s.Count; i++)
        {
            var a = lists[i].ToList();
            SphReference.NeighboursBruteForce(s, i, b);
            a.Sort();
            b.Sort();
            Assert.Equal(b, a);
        }
    }

    [Fact]
    public void FlatTree_WalkMatchesBarnesHutTree()
    {
        var ps = TestHelpers.RandomCloud(3000, seed: 5);
        var tree = new BarnesHutTree(ps.Count) { Theta = 0.6f, Softening = 0.02f };
        tree.Build(ps);
        var flat = new FlatTree();
        tree.Flatten(flat);
        Assert.Equal(tree.NodeCount, flat.Count);
        Assert.Equal(flat.Count, flat.Skip[0]);
        for (int i = 0; i < ps.Count; i++)
        {
            Vector3 expected = tree.AccelerationAt(ps.Position[i], i);
            Vector3 actual = flat.Acceleration(ps.Position[i], 0.02f, 0.6f, out _);
            Assert.True((expected - actual).Length() <= 1e-4f * expected.Length() + 1e-6f,
                $"body {i}: tree {expected} flat {actual}");
        }
    }

    [Fact]
    public void FlatTree_PotentialMatchesDirectSum()
    {
        var ps = TestHelpers.RandomCloud(2000, seed: 9);
        var tree = new BarnesHutTree(ps.Count) { Softening = 0.05f };
        tree.Build(ps);
        var flat = new FlatTree();
        tree.Flatten(flat);
        double maxRel = 0;
        for (int i = 0; i < ps.Count; i += 7)
        {
            double phi = 0;
            for (int j = 0; j < ps.Count; j++)
            {
                if (j == i) continue;
                float d2 = (ps.Position[j] - ps.Position[i]).LengthSquared() + 0.05f * 0.05f;
                phi -= ps.Mass[j] / Math.Sqrt(d2);
            }
            flat.Acceleration(ps.Position[i], 0.05f, 0f, out float pot); // θ = 0: every leaf, exact
            maxRel = Math.Max(maxRel, Math.Abs(pot - phi) / Math.Abs(phi));
        }
        Assert.True(maxRel < 1e-5, $"max relative potential error {maxRel}");
    }

    [Fact]
    public void SodShockTube_ProfilesMatchExactSolution()
    {
        const float gamma = 1.4f, dxL = 0.0125f, dxR = 0.025f, width = 0.2f;
        var pts = new List<(Vector3 Pos, float Rho, float P)>();
        int nyL = (int)MathF.Round(width / dxL), nyR = (int)MathF.Round(width / dxR);
        for (int i = 0; i < 60; i++)
            for (int j = 0; j < nyL; j++)
                for (int k = 0; k < nyL; k++)
                    pts.Add((new Vector3(-0.75f + (i + 0.5f) * dxL, (j + 0.5f) * dxL, (k + 0.5f) * dxL), 1f, 1f));
        for (int i = 0; i < 30; i++)
            for (int j = 0; j < nyR; j++)
                for (int k = 0; k < nyR; k++)
                    pts.Add((new Vector3((i + 0.5f) * dxR, (j + 0.5f) * dxR, (k + 0.5f) * dxR), 0.125f, 0.1f));
        var s = new SphState(pts.Count);
        float m = dxL * dxL * dxL;
        foreach (var (pos, rho, pr) in pts)
            s.Add(pos, Vector3.Zero, m, pr / ((gamma - 1) * rho), SphKernel.DefaultEta * MathF.Cbrt(m / rho));

        var p = new SphParameters { Gamma = gamma, SelfGravity = false, HMax = width / 4f, Period = new Vector3(0f, width, width) };
        var sph = new SphReference(p);
        sph.Initialise(s);
        while (sph.Time < 0.2)
        {
            sph.Dt = MathF.Min(sph.Dt, (float)(0.2 - sph.Time) + 1e-7f);
            sph.Step(s);
        }

        // Bin the particles in x and compare with the exact solution at the bin centres. The waves
        // from the free ends (at ±0.75, moving inward at the sound speed) stay outside [-0.4, 0.45].
        const double x0 = -0.4, x1 = 0.45, bw = 0.025;
        int bins = (int)Math.Round((x1 - x0) / bw);
        var sumRho = new double[bins]; var sumP = new double[bins]; var sumV = new double[bins]; var cnt = new int[bins];
        for (int i = 0; i < s.Count; i++)
        {
            int b = (int)Math.Floor((s.Position[i].X - x0) / bw);
            if (b < 0 || b >= bins) continue;
            sumRho[b] += s.Density[i]; sumP[b] += s.Pressure[i]; sumV[b] += s.Velocity[i].X; cnt[b]++;
        }
        double eRho = 0, eP = 0, eV = 0;
        int used = 0;
        for (int b = 0; b < bins; b++)
        {
            if (cnt[b] == 0) continue;
            double xc = x0 + (b + 0.5) * bw;
            var ex = SphTestHelpers.ExactRiemann(1, 0, 1, 0.125, 0, 0.1, gamma, xc / sph.Time);
            eRho += Math.Abs(sumRho[b] / cnt[b] - ex.Rho);
            eP += Math.Abs(sumP[b] / cnt[b] - ex.P);
            eV += Math.Abs(sumV[b] / cnt[b] - ex.U);
            used++;
        }
        // Mean absolute error as a fraction of each quantity's range across the tube.
        eRho /= used * 0.875; eP /= used * 0.9; eV /= used * 0.927;
        output.WriteLine($"Sod t={sph.Time:F3} N={s.Count}: L1 error / range  rho {eRho:P2}  P {eP:P2}  v {eV:P2}");
        Assert.True(eRho < 0.03, $"density L1 {eRho:P2}");
        Assert.True(eP < 0.035, $"pressure L1 {eP:P2}");
        Assert.True(eV < 0.07, $"velocity L1 {eV:P2}");
    }

    [Fact]
    public void SoundWave_PropagatesAtSoundSpeed()
    {
        // Standing adiabatic wave, λ = 1, c_s = 1 (γ = 5/3, ρ = 1, P = 3/5): period T = 1.
        const int nx = 32, ny = 8;
        const float dx = 1f / nx, gamma = 5f / 3f, eps = 2e-3f;
        float k = 2f * MathF.PI;
        float u0 = 0.6f / (gamma - 1f);
        var s = SphTestHelpers.Lattice(nx, ny, ny, dx, 1f, u0);
        var x0 = new float[s.Count];
        for (int i = 0; i < s.Count; i++)
        {
            float x = s.Position[i].X;
            x0[i] = x;
            s.Position[i].X = x + eps / k * MathF.Sin(k * x);
            s.U[i] = u0 * (1f + (gamma - 1f) * (-eps * MathF.Cos(k * x)));
        }
        var p = new SphParameters { Gamma = gamma, SelfGravity = false, Period = new Vector3(1f, ny * dx, ny * dx) };
        var sph = new SphReference(p);
        sph.Initialise(s);

        double Mode()
        {
            double d = 0;
            for (int i = 0; i < s.Count; i++) d += (s.Position[i].X - x0[i]) * Math.Sin(k * x0[i]);
            return d;
        }
        var crossings = new List<double>();
        double prev = Mode(), prevT = 0;
        while (sph.Time < 1.0 && crossings.Count < 2)
        {
            sph.Step(s);
            double cur = Mode();
            if (Math.Sign(cur) != Math.Sign(prev))
                crossings.Add(prevT + (sph.Time - prevT) * prev / (prev - cur));
            prev = cur;
            prevT = sph.Time;
        }
        Assert.Equal(2, crossings.Count);
        double period = 2 * (crossings[1] - crossings[0]);
        output.WriteLine($"sound wave: crossings {crossings[0]:F4}, {crossings[1]:F4} → c_measured/c_s = {1.0 / period:F4}");
        Assert.InRange(1.0 / period, 0.95, 1.05);
    }

    [Fact]
    public void EvrardCollapse_ConservesTotalEnergy_Cpu()
    {
        var s = SphTestHelpers.Evrard(2000);
        var p = new SphParameters { Gamma = 5f / 3f, HMin = 1e-4f };
        var sph = new SphReference(p);
        sph.Initialise(s);
        var e0 = sph.Energies(s);
        double E0 = e0.Kinetic + e0.Thermal + e0.Potential;
        double maxDrift = 0, maxThermal = 0, tPeak = 0;
        int steps = 0;
        while (sph.Time < 1.3)
        {
            sph.Step(s);
            steps++;
            if (steps % 5 != 0) continue;
            var e = sph.Energies(s);
            double E = e.Kinetic + e.Thermal + e.Potential;
            maxDrift = Math.Max(maxDrift, Math.Abs(E - E0) / Math.Abs(E0));
            if (e.Thermal > maxThermal) { maxThermal = e.Thermal; tPeak = sph.Time; }
        }
        output.WriteLine($"Evrard CPU N={s.Count}: E0={E0:F4}, steps={steps}, peak thermal {maxThermal:F3} at t={tPeak:F2}, max |ΔE|/|E0| = {maxDrift:P2}");
        Assert.True(maxThermal > 0.5, "the cloud bounces (thermal energy rises well above its initial 0.05)");
        Assert.True(maxDrift < 0.02, $"energy drift {maxDrift:P2}");
    }

    [Fact]
    public void SinkFormationAndAccretion_ConserveMassAndMomentum_Cpu()
    {
        // A cold, dense, converging clump with a bulk velocity: forms a sink, which then accretes.
        var s = SphTestHelpers.Lattice(16, 16, 16, 0.02f, 100f, 1e-4f, new Vector3(-0.16f));
        for (int i = 0; i < s.Count; i++)
            s.Velocity[i] = -0.5f * s.Position[i] + new Vector3(0.3f, -0.2f, 0.1f);
        var p = new SphParameters
        {
            Eos = EquationOfState.Barotropic, SoundSpeed = 0.01f, SelfGravity = true,
            SinkDensity = 50f, SinkRadius = 0.06f,
        };
        var sph = new SphReference(p);
        sph.Initialise(s);
        var sinks = new SinkManager(p);
        double m0 = SphTestHelpers.TotalMass(s);
        Vector3 p0 = SphTestHelpers.TotalMomentum(s);

        var candidates = new List<SinkCandidate>();
        var events = new List<AccretionEvent>();
        sph.FindSinkCandidates(s, candidates);
        Assert.NotEmpty(candidates);
        Assert.True(sinks.SelectNewSinks(candidates) > 0);
        sph.FindAccretions(s, sinks, events);
        int created = sinks.Apply(events);
        Assert.True(created > 0, "a sink formed");
        // Second pass: the existing sink(s) accrete bound gas.
        sph.FindAccretions(s, sinks, events);
        sinks.Apply(events);

        double m1 = SphTestHelpers.TotalMass(s);
        Vector3 p1 = SphTestHelpers.TotalMomentum(s);
        for (int k = 0; k < sinks.Sinks.Count; k++)
        {
            m1 += sinks.Sinks.Mass[k];
            p1 += sinks.Sinks.Mass[k] * sinks.Sinks.Velocity[k];
        }
        output.WriteLine($"sinks {sinks.Sinks.Count}, sink mass {m1 - SphTestHelpers.TotalMass(s):F4} of {m0:F4}");
        Assert.Equal(m0, m1, 4);
        Assert.True((p1 - p0).Length() < 1e-4f * (float)m0, $"momentum {p0} → {p1}");
    }
}
