using System.Numerics;
using GravitySimulator.Sph;
using Xunit.Abstractions;

namespace GravitySimulator.Tests;

/// <summary>Radiative cooling (t_cool = β t_ff relaxation to a floor) of the ideal-gas SPH: CPU reference and GPU.</summary>
public class SphCoolingTests(ITestOutputHelper output)
{
    [Fact]
    public void Cool_DrivesUTowardFloor_AndNeverBelowIt()
    {
        var p = new SphParameters { CoolingBeta = 0.1f, CoolingFloor = 2f };
        const float rho = 50f;
        float tCool = SphReference.CoolingTime(rho, p);
        Assert.Equal(0.1f * MathF.Sqrt(3f * MathF.PI / (32f * rho)), tCool, 1e-6f);

        // Monotone decay toward the floor, exact exponential, unconditionally stable for any Δt.
        float u = 100f;
        for (int k = 0; k < 200; k++)
        {
            float next = SphReference.Cool(u, rho, 0.3f * tCool, p);
            Assert.True(next <= u, "cooling never heats");
            Assert.True(next >= p.CoolingFloor, $"u {next} fell below the floor");
            u = next;
        }
        Assert.Equal(p.CoolingFloor, u, 1e-3f);
        Assert.Equal(2f + 98f * MathF.Exp(-1f), SphReference.Cool(100f, rho, tCool, p), 1e-3f);
        Assert.Equal(p.CoolingFloor, SphReference.Cool(1e6f, rho, 1e6f * tCool, p));      // huge Δt: lands on the floor
        Assert.Equal(1.5f, SphReference.Cool(1.5f, rho, tCool, p));                        // below the floor: untouched
        Assert.Equal(100f, SphReference.Cool(100f, rho, tCool, new SphParameters()));      // β = 0: adiabatic
    }

    [Fact]
    public void Cooling_StaticHotGas_RadiatesThermalEnergy_WithSaneBookkeeping()
    {
        // Hot uniform periodic gas at rest: no work is done, so only cooling changes u, and the energy
        // the gas loses must equal the energy booked as radiated.
        const int n = 10;
        const float dx = 0.1f, rho0 = 1f, uHot = 4f;
        var s = SphTestHelpers.Lattice(n, n, n, dx, rho0, uHot);
        var p = new SphParameters
        {
            Period = new Vector3(1f), SelfGravity = false, Gamma = 5f / 3f,
            CoolingBeta = 0.05f, CoolingFloor = 0.5f,
        };
        var sph = new SphReference(p);
        sph.Initialise(s);
        var e0 = sph.Energies(s);
        double E0 = e0.Kinetic + e0.Thermal;
        float tCool = SphReference.CoolingTime(rho0, p);
        int steps = 0;
        while (sph.Time < 8f * tCool && steps < 5000) { sph.Step(s); steps++; }
        var e1 = sph.Energies(s);
        double E1 = e1.Kinetic + e1.Thermal;
        double floorEnergy = p.CoolingFloor * SphTestHelpers.TotalMass(s);
        output.WriteLine($"{steps} steps over {sph.Time / tCool:F1} t_cool: thermal {e0.Thermal:F4} -> {e1.Thermal:F4} " +
                         $"(floor {floorEnergy:F4}), radiated {sph.RadiatedEnergy:F4}, E+rad {E1 + sph.RadiatedEnergy:F4} vs {E0:F4}");
        for (int i = 0; i < s.Count; i++) Assert.True(s.U[i] >= p.CoolingFloor - 1e-6f, $"u[{i}] = {s.U[i]} below the floor");
        Assert.True(e1.Thermal < floorEnergy * 1.01, "thermal energy relaxed to the floor");
        Assert.True(sph.RadiatedEnergy > 0.9 * (e0.Thermal - floorEnergy), "the lost energy is booked as radiated");
        Assert.True(Math.Abs(E1 + sph.RadiatedEnergy - E0) < 1e-3 * E0, $"E + radiated drifted: {E1 + sph.RadiatedEnergy} vs {E0}");
    }

    [Fact]
    public void Cooling_Off_LeavesAdiabaticEvolutionUnchanged()
    {
        var a = SphTestHelpers.RandomCloud(800, seed: 5);
        var b = a.Clone();
        var pa = new SphParameters { SelfGravity = true };
        var pb = new SphParameters { SelfGravity = true, CoolingBeta = 0f, CoolingFloor = 10f };
        var ra = new SphReference(pa);
        var rb = new SphReference(pb);
        ra.Initialise(a);
        rb.Initialise(b);
        for (int k = 0; k < 3; k++) { ra.Step(a); rb.Step(b); }
        for (int i = 0; i < a.Count; i++) Assert.Equal(a.U[i], b.U[i]);
        Assert.Equal(0.0, rb.RadiatedEnergy);
    }

    [Fact]
    public void Presets_OnlyCollidingCloudsCool()
    {
        var collide = SphPresets.Create(SphPresetKind.CollidingClouds, 20_000, 1).Parameters;
        Assert.True(collide.CoolingBeta > 0f);
        Assert.True(collide.CoolingFloor > collide.MinInternalEnergy);
        Assert.Equal(0f, SphPresets.Create(SphPresetKind.CollapsingCloud, 20_000, 1).Parameters.CoolingBeta);
        Assert.Equal(0f, SphPresets.Create(SphPresetKind.TidalDisruption, 2_000, 1).Parameters.CoolingBeta);
    }
}

/// <summary>GPU cooling against the CPU reference (skipped without an OpenGL 4.3 context).</summary>
[Trait("Category", "GPU")]
[Collection("GPU")]
public class SphCoolingGpuTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Gpu_Cooling_TracksCpuReference()
    {
        using var gl = HeadlessGlContext.TryCreate(out string error);
        Skip.If(gl == null, "No OpenGL 4.3 core context: " + error);
        var s = SphTestHelpers.RandomCloud(3000, seed: 11);
        for (int i = 0; i < s.Count; i++) s.U[i] *= 20f; // hot: cooling dominates du/dt
        var p = new SphParameters { SelfGravity = true, CoolingBeta = 0.02f, CoolingFloor = 0.06f };
        var cpu = s.Clone();
        var reference = new SphReference(p);
        reference.Initialise(cpu, iterations: 2);
        using var gpu = new SphGpuSolver(p, s.Count);
        gpu.Upload(s);
        gpu.Initialise(iterations: 2);
        double uStart = MeanU(cpu);
        for (int k = 0; k < 5; k++)
        {
            gpu.Dt = reference.Dt;
            reference.Step(cpu);
            gpu.Step();
        }
        var g = new SphState(gpu.Count);
        gpu.Download(g);
        var byId = new float[s.Count];
        for (int k = 0; k < g.Count; k++) byId[g.Id[k]] = g.U[k];
        double maxRel = 0;
        for (int i = 0; i < s.Count; i++)
        {
            maxRel = Math.Max(maxRel, Math.Abs(byId[i] - cpu.U[i]) / cpu.U[i]);
            Assert.True(byId[i] >= p.CoolingFloor - 1e-6f, "GPU u below the floor");
        }
        double uEnd = MeanU(cpu);
        output.WriteLine($"mean u {uStart:F4} -> {uEnd:F4} after 5 steps (t = {reference.Time:G3}); max |u_gpu - u_cpu|/u = {maxRel:E2}");
        Assert.True(uEnd < 0.8 * uStart, "the gas cooled noticeably");
        Assert.True(maxRel < 1e-3, $"GPU u diverged from the CPU reference: {maxRel}");
    }

    private static double MeanU(SphState s)
    {
        double sum = 0;
        for (int i = 0; i < s.Count; i++) sum += s.U[i];
        return sum / s.Count;
    }
}
