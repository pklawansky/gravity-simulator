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
    /// The GPU-built octree against the exact sum, at theta 0.6 (3000-particle uniform cloud and a 4000-body
    /// disc): at the default leaf capacity its mean, p99 and max relative errors must be no worse than the CPU
    /// tree's (within 2% of them, for float summation order) and within the CPU tree's own bound (mean &lt; 1.5%,
    /// p99 &lt; 5%). At leaf capacity 1 it is the CPU tree, so it must agree with the CPU walk to float rounding.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Theta06_GpuTreeIsNoWorseThanCpuTreeAgainstDirectSum(bool disc)
    {
        using var gpu = Gpu();
        var ps = disc ? Presets.DiscAroundStar(4000, seed: 3, discMassFraction: 0.1f) : TestHelpers.RandomCloud(3000, seed: 1);
        float soft = disc ? 0.005f : Softening;
        var tree = new BarnesHutTree(ps.Count) { Theta = 0.6f, Softening = soft };
        tree.Build(ps);
        var cpu = new Vector3[ps.Count];
        var exact = new Vector3[ps.Count];
        for (int i = 0; i < ps.Count; i++)
        {
            cpu[i] = tree.AccelerationAt(ps.Position[i], i);
            exact[i] = BarnesHutTree.DirectAcceleration(ps, i, soft);
        }
        var relCpu = new List<double>();
        for (int i = 0; i < ps.Count; i++) relCpu.Add((cpu[i] - exact[i]).Length() / exact[i].Length());
        var c = Stats(relCpu);
        string name = disc ? "disc 4000" : "cloud 3000";
        output.WriteLine($"{name}: CPU tree vs direct mean {c.Mean:E2} p99 {c.P99:E2} max {c.Max:E2}; {tree.NodeCount} nodes");

        foreach (int leaf in new[] { 1, 4, 8, 16, gpu.LeafCapacity }.Distinct())
        {
            gpu.LeafCapacity = leaf;
            gpu.Compute(ps, 0.6f, soft);
            var relGpu = new List<double>(); var relPair = new List<double>();
            for (int i = 0; i < ps.Count; i++)
            {
                Vector3 g = ps.Acceleration[i];
                relGpu.Add((g - exact[i]).Length() / exact[i].Length());
                relPair.Add((g - cpu[i]).Length() / cpu[i].Length());
            }
            var gs = Stats(relGpu); var pr = Stats(relPair);
            output.WriteLine($"{name} leaf {leaf}: GPU vs direct mean {gs.Mean:E2} p99 {gs.P99:E2} max {gs.Max:E2}; " +
                             $"GPU vs CPU tree mean {pr.Mean:E2} p99 {pr.P99:E2} max {pr.Max:E2}; {gpu.LastNodeCount} nodes");
            Assert.True(gs.Mean < 0.015, $"leaf {leaf}: GPU mean relative error {gs.Mean:E3}");
            Assert.True(gs.P99 < 0.05, $"leaf {leaf}: GPU p99 relative error {gs.P99:E3}");
            Assert.True(gs.Mean <= c.Mean * 1.02, $"leaf {leaf}: GPU mean {gs.Mean:E3} worse than the CPU tree's {c.Mean:E3}");
            Assert.True(gs.P99 <= c.P99 * 1.02, $"leaf {leaf}: GPU p99 {gs.P99:E3} worse than the CPU tree's {c.P99:E3}");
            Assert.True(gs.Max <= c.Max * 1.02, $"leaf {leaf}: GPU max {gs.Max:E3} worse than the CPU tree's {c.Max:E3}");
            if (leaf == 1) Assert.True(pr.Max < 1e-4, $"leaf 1: GPU differs from the CPU tree by up to {pr.Max:E3} (relative)");
        }
    }

    [SkippableFact]
    public void ThetaZero_MatchesDirectSumToFloatTolerance()
    {
        using var gpu = Gpu();
        var ps = TestHelpers.RandomCloud(500, seed: 2);
        gpu.Compute(ps, 0f, Softening);
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
        gpu.Compute(ps, 0.6f, Softening);
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
    /// The GPU broad phase (uniform grid per size level) on the brute-force contact-set test of the CPU one
    /// (CollisionBroadPhaseTests: grains, clumps over four decades of radius, a star-sized body, clusters, dead
    /// bodies): after the CPU re-test, sort and de-duplication, its pairs, mapped back through the Morton order,
    /// must be exactly the O(n^2) set; and in the reordered state they must equal the CPU broad phase's pairs,
    /// in the same (lower, higher) order.
    /// </summary>
    [SkippableTheory]
    [InlineData(1, 3000)]
    [InlineData(2, 5000)]
    [InlineData(3, 8000)]
    [InlineData(4, 300)]
    public void GpuContacts_MatchBruteForce_OnRandomMixedSizeStates(int seed, int count)
    {
        using var gpu = Gpu();
        var ps = CollisionBroadPhaseTests.MixedState(seed, count);
        if (seed == 3) for (int i = 7; i < ps.Count; i += 97) ps.Mass[i] = 0f; // dead bodies never pair
        var expected = CollisionBroadPhaseTests.BruteForce(ps);
        Assert.True(expected.Count > 50, $"only {expected.Count} contacts");

        gpu.Compute(ps, 0.6f, Softening, contacts: true);
        int[] order = gpu.LastOrder.Take(ps.Count).ToArray();
        var reordered = ps.Clone();
        reordered.Reorder(order, new ParticleSystem(reordered.Capacity));
        var resolver = new CollisionResolver();
        var gpuPairs = resolver.FindContacts(reordered, gpu.LastContactPairs, gpu.LastContactCount);
        output.WriteLine($"seed {seed}: {expected.Count} contacts, {gpu.LastContactCount} GPU candidates, {gpuPairs.Count} after the re-test");

        // Same pairs as the CPU broad phase on the reordered state, in the same order.
        Assert.Equal(new CollisionResolver().FindContacts(reordered), gpuPairs);
        // And, mapped back to the original indices, exactly the brute-force set.
        var mapped = gpuPairs.Select(p => (Math.Min(order[p.A], order[p.B]), Math.Max(order[p.A], order[p.B]))).OrderBy(p => p).ToList();
        Assert.Equal(expected, mapped);
    }

    /// <summary>
    /// More candidate pairs than the first pair buffer holds (a 30^3 lattice of touching bodies, about 78,000
    /// pairs): the broad phase must grow its buffer and run again, giving the CPU broad phase's pairs.
    /// </summary>
    [SkippableFact]
    public void GpuContacts_PairBufferOverflow_GrowsAndReruns()
    {
        using var gpu = Gpu();
        var ps = new ParticleSystem(27_000);
        for (int z = 0; z < 30; z++)
        for (int y = 0; y < 30; y++)
        for (int x = 0; x < 30; x++)
            ps.Add(new Vector3(x, y, z) * 0.1f, Vector3.Zero, 1e-5f, 0.055f);
        gpu.Compute(ps, 0.6f, Softening, contacts: true);
        var reordered = ps.Clone();
        reordered.Reorder(gpu.LastOrder.Take(ps.Count).ToArray(), new ParticleSystem(reordered.Capacity));
        var gpuPairs = new CollisionResolver().FindContacts(reordered, gpu.LastContactPairs, gpu.LastContactCount);
        var cpuPairs = new CollisionResolver().FindContacts(reordered);
        output.WriteLine($"lattice: {cpuPairs.Count} contacts, {gpu.LastContactCount} GPU candidates");
        Assert.True(cpuPairs.Count > 1 << 16, $"only {cpuPairs.Count} contacts: the case must overflow the first pair buffer");
        Assert.Equal(cpuPairs, gpuPairs);
    }

    /// <summary>
    /// Near-coincident pairs at leaf capacity 1 give long single-child chains, far more nodes than the first
    /// guess at the node buffers: the build must flag the overflow, grow and rebuild, and still give the CPU
    /// tree's forces (same tree), with no corruption carried over into a later, smaller build.
    /// </summary>
    [SkippableFact]
    public void NodeBufferOverflow_GrowsAndRebuilds()
    {
        using var gpu = Gpu();
        gpu.LeafCapacity = 1;
        var rng = new Random(9);
        var ps = new ParticleSystem(6000);
        for (int k = 0; k < 3000; k++)
        {
            var p = new Vector3((float)rng.NextDouble() * 4f - 2f, (float)rng.NextDouble() * 4f - 2f, (float)rng.NextDouble() * 4f - 2f);
            ps.Add(p, Vector3.Zero, 1e-4f, 0.001f);
            ps.Add(p + new Vector3(3e-5f, 1e-5f, -2e-5f), Vector3.Zero, 1e-4f, 0.001f);
        }
        var tree = new BarnesHutTree(ps.Count) { Theta = 0.6f, Softening = Softening };
        tree.Build(ps);
        gpu.Compute(ps, 0.6f, Softening);
        output.WriteLine($"nodes: GPU {gpu.LastNodeCount}, CPU {tree.NodeCount}; first guess {2 * ps.Count + ps.Count / 2 + 4096}");
        Assert.True(gpu.LastNodeCount > 2 * ps.Count + ps.Count / 2 + 4096, "the case must overflow the first node buffer");
        double worst = 0;
        for (int i = 0; i < ps.Count; i++)
        {
            Vector3 c = tree.AccelerationAt(ps.Position[i], i);
            worst = Math.Max(worst, (ps.Acceleration[i] - c).Length() / c.Length());
        }
        output.WriteLine($"max relative difference to the CPU tree: {worst:E2}");
        Assert.True(worst < 1e-3, $"after the rebuild the GPU differs from the CPU tree by up to {worst:E3}");

        // A later, smaller build on the grown buffers.
        var small = TestHelpers.RandomCloud(500, seed: 4);
        gpu.Compute(small, 0f, Softening);
        for (int i = 0; i < small.Count; i++)
        {
            Vector3 exact = BarnesHutTree.DirectAcceleration(small, i, Softening);
            Assert.True((small.Acceleration[i] - exact).Length() / exact.Length() < 1e-4, $"small build, body {i}");
        }
    }

    /// <summary>
    /// Through <see cref="PlanetSimulation"/>: the accelerations at Load (GPU leaf capacity 1) match the CPU tree's, and stepping on
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
        int defaultLeaf = gpu.GpuLeafCapacity;
        gpu.GpuLeafCapacity = 1; // at leaf capacity 1 the GPU-built octree is the CPU tree: compare to float rounding
        cpu.Load(start.Clone());
        gpu.Load(start.Clone());
        // The GPU path keeps the bodies in tree order, so match them by position (distinct in this disc).
        var byPos = new Dictionary<Vector3, int>();
        for (int i = 0; i < cpu.Particles.Count; i++) byPos[cpu.Particles.Position[i]] = i;
        Assert.Equal(cpu.Particles.Count, gpu.Particles.Count);
        var rels = new List<double>();
        for (int k = 0; k < gpu.Particles.Count; k++)
        {
            int i = byPos[gpu.Particles.Position[k]];
            Vector3 c = cpu.Particles.Acceleration[i];
            rels.Add((c - gpu.Particles.Acceleration[k]).Length() / c.Length());
            Assert.Equal(cpu.Particles.Mass[i], gpu.Particles.Mass[k]);
            Assert.Equal(cpu.Particles.Velocity[i], gpu.Particles.Velocity[k]);
        }
        var cpuTree = new BarnesHutTree(start.Count) { Theta = 0.6f, Softening = 0.005f };
        cpuTree.Build(start);
        var r = Stats(rels);
        output.WriteLine($"relative acceleration difference at Load: mean {r.Mean:E2} p99 {r.P99:E2} max {r.Max:E2}; " +
                         $"nodes GPU {gpu.LastGpuNodeCount}, CPU {cpuTree.NodeCount}");
        // Same tree (same node count), so the same forces to float rounding; a centre of mass that differs in its
        // last bit (another summation order) can flip one opening decision, which moves that body by ~1e-4.
        Assert.Equal(cpuTree.NodeCount, gpu.LastGpuNodeCount);
        Assert.True(r.P99 < 1e-5, $"GPU and CPU accelerations differ by {r.P99:E3} at p99");
        Assert.True(r.Max < 1e-3, $"GPU and CPU accelerations differ by up to {r.Max:E3}");

        gpu.GpuLeafCapacity = defaultLeaf;
        for (int k = 0; k < 20; k++) gpu.Step();
        Assert.True(gpu.GpuGravityActive, gpu.GravityBackend);
        var ps = gpu.Particles;
        for (int i = 0; i < ps.Count; i++)
            Assert.True(float.IsFinite(ps.Position[i].X + ps.Position[i].Y + ps.Position[i].Z + ps.Velocity[i].X), $"body {i} not finite");
        var t = gpu.LastPhaseTimes;
        output.WriteLine($"GPU step: tree {t.TreeBuild:F1} ms, gravity {t.Gravity:F1} ms (CPU prep {t.GpuFlatten:F1}, GL {t.GpuWalk:F1}: GPU tree build {t.GpuTreeBuild:F1}, walk {t.GpuKernel:F1}), collisions {t.Collisions:F1} ms");
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
