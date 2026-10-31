using System.Diagnostics;
using GravitySimulator.Planets;
using Xunit.Abstractions;

namespace GravitySimulator.Tests;

/// <summary>Spec check for collision outcomes: the default disc still forms clumps (about 10 s per model).</summary>
public class DiscClumpingTests(ITestOutputHelper output)
{
    /// <summary>
    /// Clumping under each collision model: UI-default disc (50,000 bodies, 10% of the star), 1,500 steps.
    /// A clump is a non-star body of at least 10 grains (m0 = the load-time grain mass).
    /// Spec bar: Outcomes still gives at least 50 clumps.
    /// </summary>
    [Theory]
    [InlineData(CollisionModel.Outcomes)]
    [InlineData(CollisionModel.MergeOrBounce)]
    public void Disc10Percent_Clumping(CollisionModel model)
    {
        var sim = new PlanetSimulation(new SimulationParameters { CollisionModel = model });
        sim.Load(Presets.DiscAroundStar(50_000, seed: 1, discMassFraction: 0.1f));
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1500; i++) sim.Step();
        var s = sim.Stats;
        var ps = sim.Particles;
        int clumps = 0, clumps100 = 0;
        float largest = 0f;
        for (int i = 0; i < ps.Count; i++)
        {
            float grains = ps.Mass[i] / s.GrainMass;
            if (ps.Mass[i] >= 0.5f) continue; // the star
            if (grains >= 10f) clumps++;
            if (grains >= 100f) clumps100++;
            largest = MathF.Max(largest, grains);
        }
        output.WriteLine($"clumping {model}: {clumps} clumps >=10 grains ({clumps100} >=100), largest {largest:F0} grains, " +
                         $"bodies {s.Count}, merges {s.Merges}, bounces {s.Bounces}, partial accretions {s.PartialAccretions}, " +
                         $"hit-and-runs {s.HitAndRuns}, erosions {s.Erosions}, disruptions {s.Disruptions}, " +
                         $"fragments {s.FragmentsCreated}, escaped {s.Escaped}, {sw.Elapsed.TotalSeconds:F0} s");
        if (model == CollisionModel.Outcomes) Assert.True(clumps >= 50, $"only {clumps} clumps of 10+ grains");
    }
}
