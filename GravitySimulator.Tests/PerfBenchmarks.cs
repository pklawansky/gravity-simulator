using System.Diagnostics;
using GravitySimulator.Planets;
using Xunit.Abstractions;

namespace GravitySimulator.Tests;

/// <summary>
/// Headless steps/s benchmark for the disc preset. Excluded from the default run by
/// default.runsettings (Category!=Perf). Run with:
///   dotnet test GravitySimulator.Tests -c Release --settings GravitySimulator.Tests/perf.runsettings --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Perf")]
public class PerfBenchmarks(ITestOutputHelper output)
{
    [Theory]
    [InlineData(50_000, CollisionModel.Outcomes)]
    [InlineData(100_000, CollisionModel.Outcomes)]
    [InlineData(50_000, CollisionModel.MergeOrBounce)]
    [InlineData(100_000, CollisionModel.MergeOrBounce)]
    public void DiscPreset_StepsPerSecond(int count, CollisionModel model)
    {
        var sim = new PlanetSimulation(new SimulationParameters { CollisionModel = model });
        sim.Load(Presets.DiscAroundStar(count, seed: 1));
        for (int i = 0; i < 5; i++) sim.Step(); // warm-up (JIT, pools)

        const int steps = 40;
        long gen0 = GC.CollectionCount(0);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < steps; i++) sim.Step();
        sw.Stop();
        double rate = steps / sw.Elapsed.TotalSeconds;

        output.WriteLine($"disc {count} {model}: {rate:F1} steps/s over {steps} steps " +
                         $"({sw.Elapsed.TotalMilliseconds / steps:F1} ms/step), count now {sim.Particles.Count}, " +
                         $"merges {sim.Stats.Merges}, bounces {sim.Stats.Bounces}, gen0 GCs {GC.CollectionCount(0) - gen0}, " +
                         $"cores {Environment.ProcessorCount}");
        Assert.True(rate > 0);
    }

    /// <summary>
    /// The UI's default disc (10% of the star) after a warm-up, so the timing is steady state rather
    /// than the first-contact burst; reports both collision models.
    /// </summary>
    [Theory]
    [InlineData(100_000, CollisionModel.Outcomes)]
    [InlineData(100_000, CollisionModel.MergeOrBounce)]
    public void Disc10Percent_SteadyStateStepsPerSecond(int count, CollisionModel model)
    {
        var sim = new PlanetSimulation(new SimulationParameters { CollisionModel = model });
        sim.Load(Presets.DiscAroundStar(count, seed: 1, discMassFraction: 0.1f));
        for (int i = 0; i < 150; i++) sim.Step();

        const int steps = 60;
        long gen0 = GC.CollectionCount(0);
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < steps; i++) sim.Step();
        sw.Stop();
        double rate = steps / sw.Elapsed.TotalSeconds;
        var s = sim.Stats;
        output.WriteLine($"disc10% {count} {model}: {rate:F1} steps/s over {steps} steps after 150 warm-up " +
                         $"({sw.Elapsed.TotalMilliseconds / steps:F1} ms/step), count now {s.Count}, " +
                         $"alloc/step (this thread) {(GC.GetAllocatedBytesForCurrentThread() - bytes) / steps} B, " +
                         $"gen0 GCs {GC.CollectionCount(0) - gen0}, fragments {s.FragmentsCreated}");
        Assert.True(rate > 0);
    }

    /// <summary>
    /// Collision cost alone, like for like: the same 100k disc state (0.1% disc, one step in, so the first
    /// contacts exist) resolved under each model, fresh copy per repetition.
    /// </summary>
    [Fact]
    public void Resolver_SameState_CostByModel()
    {
        var sim = new PlanetSimulation(new SimulationParameters { CollisionModel = CollisionModel.MergeOrBounce });
        sim.Load(Presets.DiscAroundStar(100_000, seed: 1));
        sim.Step();
        var state = sim.Particles.Clone();
        foreach (var model in new[] { CollisionModel.MergeOrBounce, CollisionModel.Outcomes, CollisionModel.MergeOrBounce, CollisionModel.Outcomes })
        {
            var resolver = new CollisionResolver { Model = model, MaxBodies = state.Count, MinFragmentMass = state.Mass[1] };
            double total = 0;
            const int reps = 10;
            int events = 0;
            for (int r = 0; r < reps + 2; r++)
            {
                var ps = state.Clone();
                var sw = Stopwatch.StartNew();
                resolver.Resolve(ps);
                if (r >= 2) total += sw.Elapsed.TotalMilliseconds;
                events = resolver.LastMerges + resolver.LastBounces + resolver.LastPartialAccretions + resolver.LastHitAndRuns
                         + resolver.LastErosions + resolver.LastDisruptions;
            }
            output.WriteLine($"resolve 100k same state {model}: {total / reps:F2} ms, {events} contacts resolved");
        }
    }

    [Theory]
    [InlineData(PresetKind.StarFormingCloud)]
    [InlineData(PresetKind.DenseStarCluster)]
    public void CosmicPreset_StepsPerSecond(PresetKind kind)
    {
        var p = new SimulationParameters();
        Presets.Configure(kind, p);
        p.MergeThreshold = Presets.DefaultMergeThreshold(kind);
        p.Restitution = Presets.DefaultRestitution(kind);
        var sim = new PlanetSimulation(p);
        int count = Presets.DefaultCount(kind);
        sim.Load(Presets.Create(kind, count, seed: 1));
        for (int i = 0; i < 20; i++) sim.Step(); // warm-up

        const int steps = 200;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < steps; i++) sim.Step();
        double rate = steps / sw.Elapsed.TotalSeconds;
        var s = sim.Stats;
        output.WriteLine($"{kind} {count}: {rate:F1} steps/s over {steps} steps, count now {s.Count}, " +
                         $"stars formed {s.StarsFormed}, supernovae {s.Supernovae}, black holes formed {s.BlackHolesFormed}");
        Assert.True(rate > 0);
    }

    [Theory]
    [InlineData(50_000)]
    public void DiscPreset_PhaseBreakdown(int count)
    {
        var ps = Presets.DiscAroundStar(count, seed: 1);
        var tree = new BarnesHutTree(count) { Theta = 0.6f, Softening = 0.005f };
        var resolver = new CollisionResolver();
        double build = 0, force = 0, collide = 0;
        const int reps = 10;
        for (int r = 0; r < reps + 2; r++)
        {
            var sw = Stopwatch.StartNew();
            tree.Build(ps);
            double b = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            Parallel.For(0, ps.Count, i => ps.Acceleration[i] = tree.AccelerationAt(ps.Position[i], i));
            double f = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            resolver.Resolve(ps);
            double c = sw.Elapsed.TotalMilliseconds;
            if (r >= 2) { build += b; force += f; collide += c; }
        }
        output.WriteLine($"disc {count} per step: build {build / reps:F1} ms, forces {force / reps:F1} ms, " +
                         $"collisions {collide / reps:F1} ms, nodes {tree.NodeCount}");
    }
}
