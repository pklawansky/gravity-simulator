using System.Diagnostics;
using System.Numerics;
using GravitySimulator.Sph;
using Xunit.Abstractions;

namespace GravitySimulator.Tests;

[CollectionDefinition("GPU", DisableParallelization = true)]
public class GpuCollection { }

/// <summary>
/// GPU solver tests against the CPU reference. Each test creates a hidden OpenGL 4.3 core context and
/// is skipped (with the reason) when none can be created.
/// </summary>
[Trait("Category", "GPU")]
[Collection("GPU")]
public class SphGpuTests(ITestOutputHelper output)
{
    private HeadlessGlContext Context()
    {
        var gl = HeadlessGlContext.TryCreate(out string error);
        Skip.If(gl == null, "No OpenGL 4.3 core context: " + error);
        output.WriteLine($"GL: {gl!.Renderer} / {gl.Version}");
        return gl;
    }

    private static SphState ById(SphState gpu)
    {
        var s = new SphState(gpu.Count) { Count = gpu.Count };
        for (int k = 0; k < gpu.Count; k++)
        {
            int i = gpu.Id[k];
            s.Id[i] = i;
            s.Position[i] = gpu.Position[k];
            s.Velocity[i] = gpu.Velocity[k];
            s.Acceleration[i] = gpu.Acceleration[k];
            s.H[i] = gpu.H[k];
            s.Mass[i] = gpu.Mass[k];
            s.U[i] = gpu.U[k];
            s.DuDt[i] = gpu.DuDt[k];
            s.Density[i] = gpu.Density[k];
            s.Pressure[i] = gpu.Pressure[k];
            s.SoundSpeed[i] = gpu.SoundSpeed[k];
            s.Potential[i] = gpu.Potential[k];
        }
        return s;
    }

    [SkippableFact]
    public void Gpu_UniformLattice_DensityWithinOnePercent()
    {
        using var gl = Context();
        const int n = 20;
        const float dx = 0.05f, rho0 = 3f;
        var s = SphTestHelpers.Lattice(n, n, n, dx, rho0, 1f);
        var p = new SphParameters { SelfGravity = false };
        using var gpu = new SphGpuSolver(p, s.Count);
        gpu.Upload(s);
        gpu.ComputeDerivatives(0f);
        var d = ById(Download(gpu));
        float margin = 2f * SphKernel.DefaultEta * dx + 0.5f * dx, size = n * dx;
        int inner = 0;
        for (int i = 0; i < d.Count; i++)
        {
            Vector3 x = d.Position[i];
            if (x.X < margin || x.Y < margin || x.Z < margin || x.X > size - margin || x.Y > size - margin || x.Z > size - margin) continue;
            Assert.InRange(d.Density[i], rho0 * 0.99f, rho0 * 1.01f);
            inner++;
        }
        Assert.True(inner > 1000);
    }

    [SkippableFact]
    public void Gpu_GridNeighbours_EqualBruteForce()
    {
        using var gl = Context();
        var s = SphTestHelpers.RandomCloud(4000, seed: 21);
        using var gpu = new SphGpuSolver(new SphParameters { SelfGravity = false }, s.Count);
        gpu.Upload(s);
        gpu.ComputeDerivatives(0f);
        var count = new uint[s.Count];
        var idSum = new uint[s.Count];
        var idSq = new uint[s.Count];
        var ids = gpu.NeighbourSignatures(count, idSum, idSq);
        var list = new List<int>();
        for (int k = 0; k < s.Count; k++)
        {
            int i = ids[k];
            SphReference.NeighboursBruteForce(s, i, list);
            uint c = 0, sum = 0, sq = 0;
            foreach (int j in list) { c++; sum += (uint)j; sq += (uint)j * (uint)j; }
            Assert.Equal(c, count[k]);
            Assert.Equal(sum, idSum[k]);
            Assert.Equal(sq, idSq[k]);
        }
    }

    [SkippableFact]
    public void Gpu_DensityAndAcceleration_MatchCpuReference()
    {
        using var gl = Context();
        var s = SphTestHelpers.RandomCloud(4096, seed: 7);
        var p = new SphParameters { SelfGravity = true };
        var cpu = s.Clone();
        var reference = new SphReference(p);
        reference.Predict(cpu, 0f);
        reference.ComputeDensity(cpu);
        reference.ComputeForces(cpu);
        reference.AddGravity(cpu);

        using var gpu = new SphGpuSolver(p, s.Count);
        gpu.Upload(s);
        gpu.ComputeDerivatives(0f);
        var g = ById(Download(gpu));

        double aRms = 0, duRms = 0;
        for (int i = 0; i < s.Count; i++) { aRms += cpu.Acceleration[i].LengthSquared(); duRms += cpu.DuDt[i] * (double)cpu.DuDt[i]; }
        aRms = Math.Sqrt(aRms / s.Count);
        duRms = Math.Sqrt(duRms / s.Count);
        double maxRho = 0, maxA = 0, maxDu = 0, maxPhi = 0;
        for (int i = 0; i < s.Count; i++)
        {
            maxRho = Math.Max(maxRho, Math.Abs(g.Density[i] - cpu.Density[i]) / cpu.Density[i]);
            maxA = Math.Max(maxA, (g.Acceleration[i] - cpu.Acceleration[i]).Length() / aRms);
            maxDu = Math.Max(maxDu, Math.Abs(g.DuDt[i] - cpu.DuDt[i]) / duRms);
            maxPhi = Math.Max(maxPhi, Math.Abs(g.Potential[i] - cpu.Potential[i]) / Math.Abs(cpu.Potential[i]));
        }
        output.WriteLine($"max rel density {maxRho:E2}, accel {maxA:E2} (of rms), du/dt {maxDu:E2} (of rms), potential {maxPhi:E2}");
        Assert.True(maxRho < 1e-4, $"density {maxRho}");
        Assert.True(maxA < 2e-3, $"acceleration {maxA}");
        Assert.True(maxDu < 2e-3, $"du/dt {maxDu}");
        Assert.True(maxPhi < 1e-4, $"potential {maxPhi}");
    }

    [SkippableFact]
    public void Gpu_FewSteps_TrackCpuReference()
    {
        using var gl = Context();
        var s = SphTestHelpers.RandomCloud(3000, seed: 3);
        var p = new SphParameters { SelfGravity = true };
        var cpu = s.Clone();
        var reference = new SphReference(p);
        reference.Initialise(cpu, iterations: 2);
        using var gpu = new SphGpuSolver(p, s.Count);
        gpu.Upload(s);
        gpu.Initialise(iterations: 2);
        Assert.Equal(reference.Dt, gpu.Dt, reference.Dt * 1e-3f);
        for (int k = 0; k < 5; k++)
        {
            gpu.Dt = reference.Dt; // same step sequence
            reference.Step(cpu);
            gpu.Step();
        }
        var g = ById(Download(gpu));
        double maxDx = 0;
        for (int i = 0; i < s.Count; i++) maxDx = Math.Max(maxDx, (g.Position[i] - cpu.Position[i]).Length() / cpu.H[i]);
        output.WriteLine($"after 5 steps: max |x_gpu - x_cpu| / h = {maxDx:E2}");
        Assert.True(maxDx < 1e-3, $"positions diverged: {maxDx}");
    }

    [SkippableFact]
    public void Gpu_EvrardCollapse_ConservesTotalEnergy()
    {
        using var gl = Context();
        var s = SphTestHelpers.Evrard(5000);
        var p = new SphParameters { Gamma = 5f / 3f, HMin = 1e-4f };
        using var gpu = new SphGpuSolver(p, s.Count);
        gpu.Upload(s);
        gpu.Initialise();
        var e0 = gpu.Energies();
        double E0 = e0.Kinetic + e0.Thermal + e0.Potential;
        double maxDrift = 0, maxThermal = 0, tPeak = 0;
        int steps = 0;
        var sw = Stopwatch.StartNew();
        while (gpu.Time < 1.3)
        {
            gpu.Step();
            steps++;
            if (steps % 10 != 0) continue;
            var e = gpu.Energies();
            double E = e.Kinetic + e.Thermal + e.Potential;
            maxDrift = Math.Max(maxDrift, Math.Abs(E - E0) / Math.Abs(E0));
            if (e.Thermal > maxThermal) { maxThermal = e.Thermal; tPeak = gpu.Time; }
        }
        output.WriteLine($"Evrard GPU N={s.Count}: E0={E0:F4}, {steps} steps in {sw.Elapsed.TotalSeconds:F1} s, " +
                         $"peak thermal {maxThermal:F3} at t={tPeak:F2}, max |ΔE|/|E0| = {maxDrift:P2}");
        Assert.True(maxThermal > 0.5, "the cloud bounces");
        Assert.True(maxDrift < 0.02, $"energy drift {maxDrift:P2}");
    }

    [SkippableFact]
    public void Gpu_SinkFormationAndAccretion_ConserveMassAndMomentum()
    {
        using var gl = Context();
        var s = SphTestHelpers.Lattice(20, 20, 20, 0.016f, 100f, 1e-4f, new Vector3(-0.16f));
        for (int i = 0; i < s.Count; i++)
            s.Velocity[i] = -0.5f * s.Position[i] + new Vector3(0.3f, -0.2f, 0.1f);
        var p = new SphParameters
        {
            Eos = EquationOfState.Barotropic, SoundSpeed = 0.01f, SelfGravity = false,
            SinkDensity = 60f, SinkRadius = 0.05f,
        };
        var sinks = new SinkManager(p);
        using var gpu = new SphGpuSolver(p, s.Count);
        gpu.Upload(s);
        gpu.Initialise(sinks: sinks);
        double m0 = SphTestHelpers.TotalMass(s);
        Vector3 p0 = SphTestHelpers.TotalMomentum(s);
        for (int k = 0; k < 20; k++) gpu.Step(sinks);
        var g = Download(gpu);
        double m1 = SphTestHelpers.TotalMass(g);
        Vector3 p1 = SphTestHelpers.TotalMomentum(g);
        double sinkMass = 0;
        for (int k = 0; k < sinks.Sinks.Count; k++)
        {
            sinkMass += sinks.Sinks.Mass[k];
            p1 += sinks.Sinks.Mass[k] * sinks.Sinks.Velocity[k];
        }
        m1 += sinkMass;
        output.WriteLine($"sinks {sinks.Sinks.Count} holding {sinkMass:F4} of {m0:F4}; gas left {g.Count}");
        Assert.True(sinks.Sinks.Count > 0, "sinks formed");
        Assert.Equal(m0, m1, 3);
        Assert.True((p1 - p0).Length() < 1e-3f * (float)m0, $"momentum {p0} → {p1}");
    }

    private static SphState Download(SphGpuSolver gpu)
    {
        var s = new SphState(Math.Max(1, gpu.Count));
        gpu.Download(s);
        return s;
    }
}
