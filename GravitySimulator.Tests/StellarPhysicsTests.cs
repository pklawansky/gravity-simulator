using System.Numerics;
using GravitySimulator.Planets;

namespace GravitySimulator.Tests;

public class StellarPhysicsTests
{
    [Fact]
    public void Classification_AtEachThreshold()
    {
        Assert.Equal(BodyClass.Planet, StellarPhysics.ClassifyNonDegenerate(0.079f));
        Assert.Equal(BodyClass.Star, StellarPhysics.ClassifyNonDegenerate(0.08f));

        Assert.Equal(BodyClass.WhiteDwarf, StellarPhysics.ClassifyRemnant(1.4f));
        Assert.Equal(BodyClass.NeutronStar, StellarPhysics.ClassifyRemnant(1.41f));
        Assert.Equal(BodyClass.NeutronStar, StellarPhysics.ClassifyRemnant(2.5f));
        Assert.Equal(BodyClass.BlackHole, StellarPhysics.ClassifyRemnant(2.51f));

        Assert.Equal(BodyClass.WhiteDwarf, StellarPhysics.EndOfLife(7.99f).Remnant);
        Assert.False(StellarPhysics.EndOfLife(7.99f).Supernova);
        Assert.Equal(BodyClass.NeutronStar, StellarPhysics.EndOfLife(8f).Remnant);
        Assert.True(StellarPhysics.EndOfLife(8f).Supernova);
        Assert.Equal(BodyClass.NeutronStar, StellarPhysics.EndOfLife(19.99f).Remnant);
        Assert.Equal(BodyClass.BlackHole, StellarPhysics.EndOfLife(20f).Remnant);
        Assert.True(StellarPhysics.EndOfLife(20f).Supernova);
    }

    [Fact]
    public void Lifetime_ScalesAsMassToTheMinus2Point5()
    {
        Assert.Equal(1e10, StellarPhysics.LifetimeYears(1f), 3);
        Assert.Equal(1e10 * Math.Pow(10, -2.5), StellarPhysics.LifetimeYears(10f), 3);
        Assert.Equal(Math.Pow(30, 2.5), StellarPhysics.LifetimeYears(1f) / StellarPhysics.LifetimeYears(30f), 6);
    }

    [Fact]
    public void BlackHole_NeverBounces()
    {
        var ps = new ParticleSystem(2);
        ps.Add(Vector3.Zero, new Vector3(500f, 0, 0), 5f, 0.1f, BodyClass.BlackHole);
        ps.Add(new Vector3(0.15f, 0, 0), new Vector3(-500f, 0, 0), 1f, 0.1f, BodyClass.Star);
        var resolver = new CollisionResolver { MergeThreshold = 1f, Restitution = 1f };

        resolver.Resolve(ps);

        Assert.Equal(0, resolver.LastBounces);
        Assert.Equal(1, ps.Count);
        Assert.Equal(BodyClass.BlackHole, ps.Class[0]);
        Assert.Equal(6f, ps.Mass[0]);
        Assert.Equal(1, resolver.LastBlackHoleMergers);
    }

    [Fact]
    public void Merges_FollowClassRules()
    {
        // star + star -> star with mass-weighted age
        var (c1, a1) = StellarPhysics.MergeClass(BodyClass.Star, 2f, 3f, BodyClass.Star, 6f, 1f);
        Assert.Equal(BodyClass.Star, c1);
        Assert.Equal((2f * 3f + 6f * 1f) / 4f, a1, 5);
        // anything into a compact object keeps the compact class
        Assert.Equal(BodyClass.NeutronStar, StellarPhysics.MergeClass(BodyClass.Star, 1f, 20f, BodyClass.NeutronStar, 0f, 1.4f).Class);
        Assert.Equal(BodyClass.BlackHole, StellarPhysics.MergeClass(BodyClass.BlackHole, 0f, 5f, BodyClass.NeutronStar, 0f, 1.4f).Class);
        Assert.Equal(BodyClass.WhiteDwarf, StellarPhysics.MergeClass(BodyClass.Gas, 0f, 3f, BodyClass.WhiteDwarf, 0f, 1f).Class);
        // gas + gas stays gas; planets (the stellar-model-off default) stay planets
        Assert.Equal(BodyClass.Gas, StellarPhysics.MergeClass(BodyClass.Gas, 0f, 1f, BodyClass.Gas, 0f, 1f).Class);
        Assert.Equal(BodyClass.Planet, StellarPhysics.MergeClass(BodyClass.Planet, 0f, 1f, BodyClass.Planet, 0f, 1f).Class);
        // star + gas -> star keeping the star's age
        var (c2, a2) = StellarPhysics.MergeClass(BodyClass.Gas, 0f, 1f, BodyClass.Star, 4f, 1f);
        Assert.Equal(BodyClass.Star, c2);
        Assert.Equal(4f, a2, 5);
    }

    [Fact]
    public void ResolverMerge_OfTwoStars_KeepsMassWeightedAge()
    {
        var ps = new ParticleSystem(2);
        ps.Add(Vector3.Zero, Vector3.Zero, 3f, 0.1f, BodyClass.Star, 2f);
        ps.Add(new Vector3(0.15f, 0, 0), Vector3.Zero, 1f, 0.1f, BodyClass.Star, 6f);
        var resolver = new CollisionResolver();

        resolver.Resolve(ps);

        Assert.Equal(1, ps.Count);
        Assert.Equal(BodyClass.Star, ps.Class[0]);
        Assert.Equal(3f, ps.Age[0], 5);
    }

    [Fact]
    public void UnitConversions_RoundTrip_AndGIsOne()
    {
        foreach (var u in new[] { UnitSystem.SolarParsec, UnitSystem.SolarAu })
        {
            Assert.Equal(1.0, u.SimToYears(u.YearsToSim(1.0)), 9);
            Assert.Equal(3.0e5, u.SimToMetres(u.MetresToSim(3.0e5)), 3);
            Assert.Equal(12.5, u.SimToKms(u.KmsToSim(12.5)), 9);
            // G = 1  <=>  T^2 = L^3 / (G M)
            double g = UnitSystem.GravitationalConstant * u.MassKg * u.TimeS * u.TimeS / Math.Pow(u.LengthM, 3);
            Assert.Equal(1.0, g, 9);
        }
        // c = 299792.458 km/s, R_sun = 6.957e8 m
        Assert.Equal(299792.458, UnitSystem.SolarParsec.SimToKms(UnitSystem.SolarParsec.SpeedOfLight), 3);
        Assert.Equal(6.957e8, UnitSystem.SolarAu.SimToMetres(UnitSystem.SolarAu.SolarRadius), 0);
    }

    [Fact]
    public void ClassAndAge_SurviveRemoveCompactAndClone()
    {
        var ps = new ParticleSystem(4);
        ps.Add(Vector3.Zero, Vector3.Zero, 1f, 0.1f, BodyClass.Gas, 0f);
        ps.Add(Vector3.One, Vector3.Zero, 0f, 0.1f, BodyClass.Star, 1f); // dead
        ps.Add(Vector3.One, Vector3.Zero, 2f, 0.1f, BodyClass.NeutronStar, 2f);
        ps.Add(Vector3.One, Vector3.Zero, 3f, 0.1f, BodyClass.Star, 3f);

        ps.RemoveAt(0); // last (Star, 3) moves into slot 0
        Assert.Equal(BodyClass.Star, ps.Class[0]);
        Assert.Equal(3f, ps.Age[0]);

        ps.Compact(); // removes the dead star
        Assert.Equal(2, ps.Count);
        Assert.Contains(BodyClass.NeutronStar, ps.Class.Take(ps.Count));

        var copy = ps.Clone();
        for (int i = 0; i < ps.Count; i++)
        {
            Assert.Equal(ps.Class[i], copy.Class[i]);
            Assert.Equal(ps.Age[i], copy.Age[i]);
        }
    }
}
