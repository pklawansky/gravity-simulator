using System.Diagnostics;
using System.Numerics;
using GravitySimulator.Planets;
using GravitySimulator.Sph;
using Xunit.Abstractions;

namespace GravitySimulator.Tests;

/// <summary>
/// Planet formation's GPU gravity (<see cref="GpuGravity"/>) against the exact O(n²) sum and the CPU tree.
/// Skipped (with the reason) when no OpenGL 4.3 context can be created.
/// </summary>
[Trait("Category", "GPU")]
[Collection("GPU")]
public class PlanetGpuGravityTests(ITestOutputHelper output)
{
    private const float Softening = 0.01f;

    private GpuGravity Gpu()
    {
        using (var probe = HeadlessGlContext.TryCreate(out string why))
            Skip.If(probe == null, "No OpenGL 4.3 core context: " + why);
        var g = GpuGravity.TryCreate(out string error);
        Assert.True(g != null, "GL 4.3 context exists but GPU gravity failed to start: " + error);
        output.WriteLine("GL: " + g!.Renderer);
        return g;
    }

    private static (double Mean, double P99, double Max) Stats(List<double> rel)
    {
        rel.Sort();
        return (rel.Average(), rel[(int)(rel.Count * 0.99)], rel[^1]);
    }

    /// <summary>
    /// Same bound as the CPU tree's own test (theta 0.6, 3000-particle uniform cloud: mean relative error
    /// &lt; 1.5%, p99 &lt; 5% against the direct sum), and the GPU walk must agree with the CPU walk to float
    /// rounding: it is the same tree, the same acceptance test and the same softening, summed in another order.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Theta06_MatchesCpuTreeAndIsWithinStatedErrorOfDirectSum(bool disc)
    {
        using var gpu = Gpu();
        var ps = disc ? Presets.DiscAroundStar(4000, seed: 3, discMassFraction: 0.1f) : TestHelpers.RandomCloud(3000, seed: 1);
        float soft = disc ? 0.005f : Softening;
        var tree = new BarnesHutTree(ps.Count) { Theta = 0.6f, Softening = soft };
        tree.Build(ps);
        var cpu = new Vector3[ps.Count];
        for (int i = 0; i < ps.Count; i++) cpu[i] = tree.AccelerationAt(ps.Position[i], i);

        gpu.Compute(tree, ps, 0.6f, soft);

        var relCpu = new List<double>(); var relGpu = new List<double>(); var relPair = new List<double>();
        for (int i = 0; i < ps.Count; i++)
        {
            Vector3 exact = BarnesHutTree.DirectAcceleration(ps, i, soft);
            Vector3 g = ps.Acceleration[i];
            relCpu.Add((cpu[i] - exact).Length() / exact.Length());
            relGpu.Add((g - exact).Length() / exact.Length());
            relPair.Add((g - cpu[i]).Length() / cpu[i].Length());
        }
        var c = Stats(relCpu); var gs = Stats(relGpu); var pr = Stats(relPair);
        output.WriteLine($"{(disc ? "disc 4000" : "cloud 3000")}: CPU tree vs direct mean {c.Mean:E2} p99 {c.P99:E2} max {c.Max:E2}; " +
                         $"GPU vs direct mean {gs.Mean:E2} p99 {gs.P99:E2} max {gs.Max:E2}; GPU vs CPU tree mean {pr.Mean:E2} p99 {pr.P99:E2} max {pr.Max:E2}");
        Assert.True(gs.Mean < 0.015, $"GPU mean relative error {gs.Mean:E3}");
        Assert.True(gs.P99 < 0.05, $"GPU p99 relative error {gs.P99:E3}");
        Assert.True(pr.Max < 1e-4, $"GPU differs from the CPU tree by up to {pr.Max:E3} (relative)");
    }

    [SkippableFact]
    public void ThetaZero_MatchesDirectSumToFloatTolerance()
    {
        using var gpu = Gpu();
        var ps = TestHelpers.RandomCloud(500, seed: 2);
        var tree = new BarnesHutTree(ps.Count) { Theta = 0f, Softening = Softening };
        tree.Build(ps);
        gpu.Compute(tree, ps, 0f, Softening);
        for (int i = 0; i < ps.Count; i++)
        {
            Vector3 exact = BarnesHutTree.DirectAcceleration(ps, i, Softening);
            double rel = (ps.Acceleration[i] - exact).Length() / exact.Length();
            Assert.True(rel < 1e-4, $"particle {i}: relative error {rel:E3}");
        }
    }

    /// <summary>Coincident bodies (a max-depth leaf holding several) and massless bodies outside the tree.</summary>
    [SkippableFact]
    public void CoincidentAndMasslessBodies_GetCpuAccelerations()
    {
        using var gpu = Gpu();
        var ps = new ParticleSystem(110);
        for (int i = 0; i < 100; i++) ps.Add(new Vector3(0.5f, 0.5f, 0.5f), Vector3.Zero, 1f, 0.01f);
        ps.Add(new Vector3(-1f, 0, 0), Vector3.Zero, 1f, 0.01f);
        ps.Add(new Vector3(0f, 1f, 0), Vector3.Zero, 0f, 0.01f); // massless: not in the tree, still accelerated
        var tree = new BarnesHutTree(ps.Count) { Theta = 0.6f, Softening = Softening };
        tree.Build(ps);
        gpu.Compute(tree, ps, 0.6f, Softening);
        foreach (int i in new[] { 100, 101 })
        {
            Vector3 cpu = tree.AccelerationAt(ps.Position[i], i);
            Assert.True((ps.Acceleration[i] - cpu).Length() / cpu.Length() < 1e-4, $"body {i}: GPU {ps.Acceleration[i]} vs CPU {cpu}");
        }
        // Each of the 100 coincident bodies feels only the lone body (and none of its coincident neighbours).
        Vector3 c0 = tree.AccelerationAt(ps.Position[0], 0);
        Assert.True((ps.Acceleration[0] - c0).Length() / c0.Length() < 1e-3, $"coincident body: GPU {ps.Acceleration[0]} vs CPU {c0}");
    }

    /// <summary>
    /// Through <see cref="PlanetSimulation"/>: the accelerations at Load match the CPU tree's, and stepping on
    /// the GPU stays on the GPU with finite state. (Positions are not compared after steps: collisions are
    /// sensitive to the last bit of the force, so the two runs may resolve a contact differently.)
    /// </summary>
    [SkippableFact]
    public void PlanetSimulation_GpuAccelerationsMatchCpu_AndStepsStayFinite()
    {
        using (var probe = HeadlessGlContext.TryCreate(out string why))
            Skip.If(probe == null, "No OpenGL 4.3 core context: " + why);
        var start = Presets.DiscAroundStar(20_000, seed: 5, discMassFraction: 0.1f);
        using var cpu = new PlanetSimulation(new SimulationParameters());
        using var gpu = new PlanetSimulation(new SimulationParameters());
        Assert.False(gpu.GpuGravityActive); // the CPU tree is the default (the golden-hash tests rely on it)
        Assert.True(gpu.TryEnableGpuGravity(), gpu.GravityBackend);
        output.WriteLine("backend: " + gpu.GravityBackend);
        cpu.Load(start.Clone());
        gpu.Load(start.Clone());
        // The GPU path keeps the bodies in tree order, so match them by position (distinct in this disc).
        var byPos = new Dictionary<Vector3, int>();
        for (int i = 0; i < cpu.Particles.Count; i++) byPos[cpu.Particles.Position[i]] = i;
        Assert.Equal(cpu.Particles.Count, gpu.Particles.Count);
        double worst = 0;
        for (int k = 0; k < gpu.Particles.Count; k++)
        {
            int i = byPos[gpu.Particles.Position[k]];
            Vector3 c = cpu.Particles.Acceleration[i];
            worst = Math.Max(worst, (c - gpu.Particles.Acceleration[k]).Length() / c.Length());
            Assert.Equal(cpu.Particles.Mass[i], gpu.Particles.Mass[k]);
            Assert.Equal(cpu.Particles.Velocity[i], gpu.Particles.Velocity[k]);
        }
        output.WriteLine($"max relative acceleration difference at Load: {worst:E2}");
        Assert.True(worst < 1e-4, $"GPU and CPU accelerations differ by up to {worst:E3}");

        for (int k = 0; k < 20; k++) gpu.Step();
        Assert.True(gpu.GpuGravityActive, gpu.GravityBackend);
        var ps = gpu.Particles;
        for (int i = 0; i < ps.Count; i++)
            Assert.True(float.IsFinite(ps.Position[i].X + ps.Position[i].Y + ps.Position[i].Z + ps.Velocity[i].X), $"body {i} not finite");
        var t = gpu.LastPhaseTimes;
        output.WriteLine($"GPU step: tree {t.TreeBuild:F1} ms, gravity {t.Gravity:F1} ms (flatten {t.GpuFlatten:F1}, GL {t.GpuWalk:F1}), collisions {t.Collisions:F1} ms");
    }

    [Fact]
    public void CpuIsTheDefaultBackend()
    {
        using var sim = new PlanetSimulation(new SimulationParameters());
        Assert.False(sim.GpuGravityActive);
        Assert.StartsWith("CPU", sim.GravityBackend);
    }
}

/// <summary>
/// Clumping on the GPU vs the CPU (DiscClumpingTests set-up, seeds 1-4, Outcomes): opt-in, about 3 minutes.
///   dotnet test GravitySimulator.Tests -c Release --settings GravitySimulator.Tests/perf.runsettings --filter "FullyQualifiedName~GpuVsCpuClumping" --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Perf")]
[Collection("GPU")]
public class GpuVsCpuClumpingTests(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Disc10Percent_Clumping_GpuVsCpu(int seed)
    {
        var line = new List<string>();
        foreach (bool useGpu in new[] { false, true })
        {
            using var sim = new PlanetSimulation(new SimulationParameters { CollisionModel = CollisionModel.Outcomes });
            if (useGpu) Skip.IfNot(sim.TryEnableGpuGravity(), sim.GravityBackend);
            sim.Load(Presets.DiscAroundStar(50_000, seed, discMassFraction: 0.1f));
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 1500; i++) sim.Step();
            var s = sim.Stats;
            var ps = sim.Particles;
            int clumps = 0, clumps100 = 0;
            float largest = 0f;
            for (int i = 0; i < ps.Count; i++)
            {
                if (ps.Mass[i] >= 0.5f) continue; // the star
                float grains = ps.Mass[i] / s.GrainMass;
                if (grains >= 10f) clumps++;
                if (grains >= 100f) clumps100++;
                largest = MathF.Max(largest, grains);
            }
            Assert.True(!useGpu || sim.GpuGravityActive, sim.GravityBackend);
            line.Add($"{(useGpu ? "GPU" : "CPU")}: {clumps} clumps >=10 grains ({clumps100} >=100), largest {largest:F0}, bodies {s.Count}, " +
                     $"fragments {s.FragmentsCreated}, escaped {s.Escaped}, {sw.Elapsed.TotalSeconds:F0} s");
        }
        output.WriteLine($"clumping seed {seed}: " + string.Join(" | ", line));
    }
}
