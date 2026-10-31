using System.Numerics;
using GravitySimulator.Planets;
using GravitySimulator.Sph;
using Xunit.Abstractions;

namespace GravitySimulator.Tests;

/// <summary>Paczyński-Wiita black holes (Φ = -GM/(r - r_s)) and exact black-hole accretion bookkeeping.</summary>
public class PseudoNewtonianTests
{
    [Fact]
    public void PaczynskiWiita_IsNewtonianFarAway_AndStrongerNearTheHorizon()
    {
        const float m = 1e5f, rs = 0.42f;
        // r >> r_s: Newtonian to O(r_s / r).
        var far = new Vector3(4000f, 0f, 0f);
        Vector3 a = SphReference.PaczynskiWiitaAcceleration(-far, m, rs, out float phi);
        float newton = m / far.LengthSquared();
        Assert.Equal(newton, a.Length(), newton * 3e-4f);
        Assert.Equal(-m / far.Length(), phi, MathF.Abs(phi) * 3e-4f);
        Assert.True(a.X < 0f, "attractive, toward the hole");
        // r = 3 r_s: (r / (r - r_s))^2 = 2.25 times Newtonian, Φ = -GM / (2 r_s).
        var near = new Vector3(0f, 3f * rs, 0f);
        Vector3 b = SphReference.PaczynskiWiitaAcceleration(-near, m, rs, out float phiNear);
        Assert.Equal(2.25f * m / near.LengthSquared(), b.Length(), b.Length() * 1e-4f);
        Assert.Equal(-m / (2f * rs), phiNear, MathF.Abs(phiNear) * 1e-4f);
        Assert.True(b.Y < 0f);
        // r_s = 0 is exactly Newtonian.
        Vector3 c = SphReference.PaczynskiWiitaAcceleration(-near, m, 0f, out _);
        Assert.Equal(m / near.LengthSquared(), c.Length(), c.Length() * 1e-6f);
    }

    [Fact]
    public void PaczynskiWiita_GivesApsidalPrecession()
    {
        // Test particle on an e = 0.8 orbit with r_p = 20 r_s: the pericentre advances. To leading order the
        // PW term -GM r_s / r² gives 2π r_s / p per orbit, 2/3 of the Schwarzschild 3π r_s / p (Wegg 2012);
        // the higher-order terms add a little at r_p = 20 r_s.
        const double m = 1.0, rs = 0.05, rp = 20 * rs, e = 0.8;
        double p = rp * (1 + e), h = Math.Sqrt(m * p);
        var x = new Vector3((float)rp, 0f, 0f);
        var v = new Vector3(0f, (float)(h / rp), 0f);
        double dt = 2e-5, t = 0, period = 2 * Math.PI * Math.Pow(p / (1 - e * e), 1.5);
        double xd = rp, yd = 0, vxd = 0, vyd = h / rp;
        double prevR = rp, prevPrevR = rp, angle = double.NaN;
        while (t < 1.6 * period)
        {
            // Leapfrog in double with the PW force (the float helper is checked above).
            Acc(xd, yd, out double ax, out double ay);
            vxd += 0.5 * dt * ax; vyd += 0.5 * dt * ay;
            xd += dt * vxd; yd += dt * vyd;
            Acc(xd, yd, out ax, out ay);
            vxd += 0.5 * dt * ax; vyd += 0.5 * dt * ay;
            t += dt;
            double r = Math.Sqrt(xd * xd + yd * yd);
            if (t > 0.5 * period && prevR < prevPrevR && prevR < r) { angle = Math.Atan2(yd, xd); break; }
            prevPrevR = prevR; prevR = r;
        }
        double expected = 2 * Math.PI * rs / p;
        Assert.False(double.IsNaN(angle), "found the next pericentre");
        Assert.InRange(angle, 0.95 * expected, 1.5 * expected);

        void Acc(double px, double py, out double ax, out double ay)
        {
            double r = Math.Sqrt(px * px + py * py), k = m / ((r - rs) * (r - rs) * r);
            ax = -k * px; ay = -k * py;
        }
    }

    [Fact]
    public void PreciseBlackHoleAccretion_ConservesMassAndMomentum()
    {
        var sinks = new SinkManager(new SphParameters()) { PreciseBlackHoleAccretion = true };
        var ps = new ParticleSystem(4);
        ps.Add(Vector3.Zero, new Vector3(0.1f, 0f, 0f), 1e5f, 0.42f, BodyClass.BlackHole);
        sinks.Reset(ps);
        const float m = 2e-5f; // 2e-10 of the hole: far below float resolution of its mass
        var vGas = new Vector3(-300f, 400f, 0f);
        double M0 = 1e5;
        Vector3 p0 = new Vector3(0.1f, 0f, 0f) * (float)M0;
        var events = new List<AccretionEvent>();
        const int n = 20_000;
        for (int k = 0; k < n; k++) events.Add(new AccretionEvent { Sink = 0, Mass = m, Position = new Vector3(5f, 0f, 0f), Velocity = vGas });
        for (int step = 0; step < 10; step++) sinks.Apply(events.GetRange(step * (n / 10), n / 10));
        double mGas = (double)n * m;
        Assert.Equal(M0 + mGas, sinks.BlackHoleMassExact(0), 1e-6);
        Assert.Equal((float)(M0 + mGas), ps.Mass[0], 0.01f);
        Assert.True(ps.Mass[0] > 1e5f, "the hole's float mass grew");
        // Total momentum M v + Σ m v_gas is conserved.
        double px = M0 * 0.1 + mGas * vGas.X, py = mGas * vGas.Y;
        Assert.Equal(px / (M0 + mGas), ps.Velocity[0].X, 1e-7);
        Assert.Equal(py / (M0 + mGas), ps.Velocity[0].Y, 1e-7);
        Assert.True(ps.Velocity[0].Y > 0f, "the hole picked up the gas momentum");
        Assert.Equal(5.0 * mGas / (M0 + mGas), ps.Position[0].X, 1e-7);
    }
}

/// <summary>The GPU gravity pass applies the Paczyński-Wiita force of black-hole sinks (skipped without GL 4.3).</summary>
[Trait("Category", "GPU")]
[Collection("GPU")]
public class PseudoNewtonianGpuTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Gpu_GasFeelsPaczynskiWiitaForce_OfTheBlackHole()
    {
        using var gl = HeadlessGlContext.TryCreate(out string error);
        Skip.If(gl == null, "No OpenGL 4.3 core context: " + error);
        const float mbh = 1e5f, rs = 0.42f;
        // A few very light, well separated gas particles at 3..60 r_s (no SPH neighbours, negligible self-gravity).
        var s = new SphState(8);
        float[] radii = { 3f, 5f, 10f, 20f, 60f };
        foreach (float k in radii) s.Add(new Vector3(k * rs, 0.3f * k * rs, 0f), Vector3.Zero, 1e-12f, 1e-12f, 0.01f);
        var p = new SphParameters { PseudoNewtonianBlackHoles = true, HMin = 1e-4f, HMax = 0.01f };
        var sinks = new SinkManager(p);
        var ps = new ParticleSystem(4);
        ps.Add(Vector3.Zero, Vector3.Zero, mbh, rs, BodyClass.BlackHole);
        sinks.Reset(ps);
        using var gpu = new SphGpuSolver(p, s.Count);
        gpu.Upload(s);
        gpu.Initialise(iterations: 1, sinks: sinks);
        var g = new SphState(s.Count);
        gpu.Download(g);
        for (int k = 0; k < g.Count; k++)
        {
            Vector3 x = g.Position[k];
            Vector3 expected = SphReference.PaczynskiWiitaAcceleration(-x, mbh, rs, out _);
            double rel = (g.Acceleration[k] - expected).Length() / expected.Length();
            output.WriteLine($"r = {x.Length() / rs:F1} r_s: |a| gpu {g.Acceleration[k].Length():G5} pw {expected.Length():G5} newton {mbh / x.LengthSquared():G5} (rel err {rel:E1})");
            Assert.True(rel < 1e-4, $"r = {x.Length() / rs} r_s: rel error {rel}");
        }
    }
}
