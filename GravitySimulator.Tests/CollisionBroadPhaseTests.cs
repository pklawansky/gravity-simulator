using System.Numerics;
using GravitySimulator.Planets;

namespace GravitySimulator.Tests;

/// <summary>
/// The collision broad phase: it must find exactly the touching pairs an O(n^2) check finds, in a fixed
/// order, whatever the spread of radii, and its work must not blow up when there are many large bodies.
/// </summary>
public class CollisionBroadPhaseTests
{
    // Mixed sizes: grains, clumps over four decades of radius (well over 64 of them far above the mean),
    // one star-sized body, and tight clusters so that many pairs overlap.
    internal static ParticleSystem MixedState(int seed, int count)
    {
        var rng = new Random(seed);
        var ps = new ParticleSystem(count + 1);
        ps.Add(new Vector3(0.1f, -0.2f, 0f), Vector3.Zero, 1f, 0.3f); // the "star"
        for (int i = 0; i < count; i++)
        {
            Vector3 p;
            if (i % 5 == 0 && ps.Count > 1)
            {
                // Next to an earlier body, so clusters form.
                int k = rng.Next(ps.Count);
                var dir = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f);
                p = ps.Position[k] + dir * (4f * ps.Radius[k]);
            }
            else
            {
                p = new Vector3((float)rng.NextDouble() * 4f - 2f, (float)rng.NextDouble() * 4f - 2f, ((float)rng.NextDouble() - 0.5f) * 0.4f);
            }
            double u = rng.NextDouble();
            float r = u < 0.9 ? 0.002f * (float)(1 + rng.NextDouble()) : 0.002f * MathF.Pow(10f, (float)(rng.NextDouble() * 2.0 + 0.5));
            ps.Add(p, Vector3.Zero, r * r * r, r);
        }
        return ps;
    }

    internal static List<(int, int)> BruteForce(ParticleSystem ps)
    {
        var pairs = new List<(int, int)>();
        for (int i = 0; i < ps.Count; i++)
        for (int j = i + 1; j < ps.Count; j++)
        {
            if (ps.Mass[i] <= 0f || ps.Mass[j] <= 0f) continue;
            Vector3 d = ps.Position[j] - ps.Position[i];
            float rs = ps.Radius[i] + ps.Radius[j];
            if (d.LengthSquared() < rs * rs) pairs.Add((i, j));
        }
        return pairs;
    }

    [Theory]
    [InlineData(1, 3000)]
    [InlineData(2, 5000)]
    [InlineData(3, 8000)]
    [InlineData(4, 300)]
    public void FindContacts_MatchesBruteForce_OnRandomMixedSizeStates(int seed, int count)
    {
        var ps = MixedState(seed, count);
        if (seed == 3) for (int i = 7; i < ps.Count; i += 97) ps.Mass[i] = 0f; // dead bodies never pair
        double sumR = 0;
        for (int i = 0; i < ps.Count; i++) sumR += ps.Radius[i];
        int large = 0;
        for (int i = 0; i < ps.Count; i++) if (ps.Radius[i] > 4f * sumR / ps.Count) large++;
        if (count >= 3000) Assert.True(large > 64, $"only {large} large bodies");

        var expected = BruteForce(ps);
        var found = new CollisionResolver().FindContacts(ps);

        Assert.True(expected.Count > 50, $"only {expected.Count} contacts");
        Assert.Equal(expected, found); // same set, both in (lower index, higher index) order
    }

    [Fact]
    public void FindContacts_IsRepeatable()
    {
        var ps = MixedState(5, 6000);
        var resolver = new CollisionResolver();
        var a = resolver.FindContacts(ps);
        var b = new CollisionResolver().FindContacts(ps);
        var c = resolver.FindContacts(ps);
        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public void ManyLargeBodies_DoNotBlowUpThePairTests()
    {
        // 20,000 grains in a slab plus 300 bodies ten times larger: the old sweep sized its grid by the largest
        // body past the 64th "oversize" one, so every grain was tested against hundreds of neighbours.
        var rng = new Random(9);
        var ps = new ParticleSystem(20_400);
        for (int i = 0; i < 20_000; i++)
            ps.Add(new Vector3((float)rng.NextDouble() * 2f, (float)rng.NextDouble() * 2f, (float)rng.NextDouble() * 0.1f), Vector3.Zero, 1e-6f, 0.002f);
        for (int i = 0; i < 300; i++)
            ps.Add(new Vector3((float)rng.NextDouble() * 2f, (float)rng.NextDouble() * 2f, (float)rng.NextDouble() * 0.1f), Vector3.Zero, 1e-3f, 0.02f);
        var resolver = new CollisionResolver();

        var found = resolver.FindContacts(ps);

        Assert.Equal(BruteForce(ps), found);
        Assert.True(resolver.LastPairTests < 20L * ps.Count, $"{resolver.LastPairTests} pair tests for {ps.Count} bodies");
    }

    private static ulong RunHash(CollisionModel model)
    {
        var p = new SimulationParameters { CollisionModel = model };
        Presets.Configure(PresetKind.DiscAroundStar, p);
        var sim = new PlanetSimulation(p);
        sim.Load(Presets.Create(PresetKind.DiscAroundStar, 6000, seed: 3, discMassFraction: 0.1f));
        for (int i = 0; i < 120; i++) sim.Step();
        var ps = sim.Particles;
        ulong h = 1469598103934665603UL;
        void Mix(uint v) { h ^= v; h *= 1099511628211UL; }
        Mix((uint)ps.Count);
        for (int i = 0; i < ps.Count; i++)
        {
            Mix(BitConverter.SingleToUInt32Bits(ps.Position[i].X));
            Mix(BitConverter.SingleToUInt32Bits(ps.Velocity[i].Y));
            Mix(BitConverter.SingleToUInt32Bits(ps.Mass[i]));
            Mix(BitConverter.SingleToUInt32Bits(ps.Radius[i]));
        }
        return h;
    }

    [Theory]
    [InlineData(CollisionModel.MergeOrBounce)]
    [InlineData(CollisionModel.Outcomes)]
    public void Simulation_IsDeterministicPerSeed(CollisionModel model)
    {
        Assert.Equal(RunHash(model), RunHash(model));
    }
}
