using System.Numerics;
using GravitySimulator.Planets;

namespace GravitySimulator.Tests;

public class PlanetSimulationTests
{
    [Fact]
    public void TwoBodyCircularOrbit_ConservesEnergy_Over10kLeapfrogSteps()
    {
        // Heavy primary + light secondary on a circular orbit of radius 1 (period 2*pi).
        var ps = new ParticleSystem(2);
        const float M = 1f, m = 1e-3f, r = 1f;
        float v = MathF.Sqrt((M + m) / r);
        // Velocities split so the centre of mass is at rest.
        ps.Add(new Vector3(-r * m / (M + m), 0, 0), new Vector3(0, -v * m / (M + m), 0), M, 0.001f);
        ps.Add(new Vector3(r * M / (M + m), 0, 0), new Vector3(0, v * M / (M + m), 0), m, 0.001f);
        var sim = new PlanetSimulation(new SimulationParameters
        {
            TimeStep = 0.005f, Theta = 0f, Softening = 0f, EscapeRadius = 100f,
        });
        sim.Load(ps);
        double e0 = TestHelpers.TotalEnergy(sim.Particles);

        for (int i = 0; i < 10_000; i++) sim.Step();

        double e1 = TestHelpers.TotalEnergy(sim.Particles);
        double rel = Math.Abs((e1 - e0) / e0);
        Assert.Equal(2, sim.Particles.Count);
        Assert.True(rel < 1e-3, $"relative energy drift {rel:E3}");
        // Still roughly on the orbit (radius 1).
        float sep = Vector3.Distance(sim.Particles.Position[0], sim.Particles.Position[1]);
        Assert.InRange(sep, 0.99f, 1.01f);
    }

    [Fact]
    public void Star_IsPulledByTheDisc_AndTotalMomentumIsConserved()
    {
        // Disc preset with every grain shifted to +x: the star must accelerate toward the dust.
        var ps = Presets.DiscAroundStar(2_000, seed: 7);
        for (int i = 1; i < ps.Count; i++) ps.Position[i] += new Vector3(3f, 0, 0);
        Vector3 v0 = ps.Velocity[0];
        var sim = new PlanetSimulation(new SimulationParameters { MergeThreshold = 0f, Restitution = 1f });
        sim.Load(ps);
        Vector3 p0 = TestHelpers.TotalMomentum(sim.Particles);

        for (int i = 0; i < 50; i++) sim.Step();

        Assert.Equal(Presets.StarMass, sim.Particles.Mass[0]);
        Assert.True(sim.Particles.Velocity[0].X - v0.X > 0f, "star did not move toward the disc");
        Vector3 p1 = TestHelpers.TotalMomentum(sim.Particles);
        Assert.True(Vector3.Distance(p0, p1) < 1e-5f, $"momentum drift {Vector3.Distance(p0, p1):E2}");
    }

    [Fact]
    public void ParticlesBeyondEscapeRadius_AreRemoved()
    {
        var ps = new ParticleSystem(4);
        ps.Add(new Vector3(0, 0, 0), Vector3.Zero, 1f, 0.01f);
        ps.Add(new Vector3(1, 0, 0), Vector3.Zero, 1e-6f, 0.001f);
        ps.Add(new Vector3(0, 60, 0), Vector3.Zero, 1e-6f, 0.001f);   // beyond
        ps.Add(new Vector3(0, 0, -49), new Vector3(0, 0, -200), 1e-6f, 0.001f); // flies out this step
        var sim = new PlanetSimulation(new SimulationParameters { TimeStep = 0.01f, EscapeRadius = 50f });
        sim.Load(ps);

        sim.Step();

        Assert.Equal(2, sim.Particles.Count);
        for (int i = 0; i < sim.Particles.Count; i++)
            Assert.True(sim.Particles.Position[i].Length() < 50f);
        Assert.Equal(2, sim.Stats.Escaped);
    }

    [Fact]
    public void Step_PublishesSnapshotMatchingState()
    {
        var sim = new PlanetSimulation(new SimulationParameters());
        sim.Load(Presets.DiscAroundStar(1000, seed: 5));

        sim.Step();
        var snap = sim.AcquireLatest();

        Assert.NotNull(snap);
        Assert.Equal(sim.Particles.Count, snap!.Count);
        Assert.Equal(1, snap.Stats.StepCount);
        Assert.Equal(sim.Particles.Position[3].X, snap.Positions[9]);
        Assert.Equal(sim.Particles.Radius[3], snap.Radii[3]);
    }

    [Fact]
    public void BackgroundThread_StartsAdvancesAndStopsCleanly()
    {
        var sim = new PlanetSimulation(new SimulationParameters());
        sim.Load(Presets.DiscAroundStar(2000, seed: 6));

        sim.Start();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (sim.Stats.StepCount < 3 && DateTime.UtcNow < deadline) Thread.Sleep(10);
        sim.Dispose();

        Assert.True(sim.Stats.StepCount >= 3);
        Assert.False(sim.IsThreadAlive);
    }
}
