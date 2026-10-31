using System.Numerics;
using GravitySimulator.Planets;

namespace GravitySimulator.Tests;

public class CosmicPresetsTests
{
    [Theory]
    [InlineData(PresetKind.StarFormingCloud)]
    [InlineData(PresetKind.DenseStarCluster)]
    public void CosmicPresets_AreDeterministicPerSeed_AndHaveRequestedCount(PresetKind kind)
    {
        var a = Presets.Create(kind, 3000, 11);
        var b = Presets.Create(kind, 3000, 11);
        var c = Presets.Create(kind, 3000, 12);
        Assert.Equal(3000, a.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a.Position[i], b.Position[i]);
            Assert.Equal(a.Velocity[i], b.Velocity[i]);
            Assert.Equal(a.Mass[i], b.Mass[i]);
            Assert.Equal(a.Class[i], b.Class[i]);
        }
        Assert.NotEqual(a.Position[5], c.Position[5]);
        Assert.True(a.Capacity >= a.Count + 64, "cosmic presets preallocate ejecta headroom");
        Assert.True(TestHelpers.TotalMomentum(a).Length() < 1e-2f * (float)TestHelpers.TotalMass(a));
    }

    [Fact]
    public void DenseCluster_IsStarsDrawnFromKroupa()
    {
        var ps = Presets.Create(PresetKind.DenseStarCluster, 20_000, 3);
        Assert.All(Enumerable.Range(0, ps.Count), i => Assert.Equal(BodyClass.Star, ps.Class[i]));
        float min = Enumerable.Range(0, ps.Count).Min(i => ps.Mass[i]);
        float max = Enumerable.Range(0, ps.Count).Max(i => ps.Mass[i]);
        double mean = TestHelpers.TotalMass(ps) / ps.Count;
        Assert.True(min >= 0.08f && max <= 100f, $"mass range {min}..{max}");
        Assert.True(max >= 20f, "a 20k-star Kroupa sample should contain black-hole progenitors");
        Assert.InRange(mean, 0.3, 0.9);
    }

    [Fact]
    public void Configure_TurnsStellarModelOnForCosmicPresets_AndOffForPlanetPresets()
    {
        var p = new SimulationParameters();
        Presets.Configure(PresetKind.StarFormingCloud, p);
        Assert.NotNull(p.Stellar);
        Presets.Configure(PresetKind.DiscAroundStar, p);
        Assert.Null(p.Stellar);
        var defaults = new SimulationParameters();
        Assert.Equal(defaults.TimeStep, p.TimeStep);
        Assert.Equal(defaults.Softening, p.Softening);
        Assert.Equal(defaults.EscapeRadius, p.EscapeRadius);
    }

    [Fact]
    public void Simulation_WithStellarModel_InstallsSeams_AndPublishesClasses()
    {
        var p = new SimulationParameters();
        Presets.Configure(PresetKind.DenseStarCluster, p);
        var sim = new PlanetSimulation(p);
        sim.Load(Presets.Create(PresetKind.DenseStarCluster, 500, 1));
        Assert.Contains(sim.ForceContributors, f => f is RadiationPressure);
        Assert.Contains(sim.Evolutions, e => e is StellarEvolution);

        sim.Step();
        var snap = sim.AcquireLatest()!;
        for (int i = 0; i < snap.Count; i++) Assert.Equal((float)sim.Particles.Class[i], snap.Classes[i]);
        Assert.True(snap.Stats.StellarModel);

        Presets.Configure(PresetKind.DiscAroundStar, p);
        sim.Load(Presets.Create(PresetKind.DiscAroundStar, 500, 1));
        Assert.Empty(sim.ForceContributors);
        Assert.Empty(sim.Evolutions);
    }
}
