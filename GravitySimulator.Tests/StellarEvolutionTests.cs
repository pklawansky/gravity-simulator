using System.Numerics;
using GravitySimulator.Planets;

namespace GravitySimulator.Tests;

public class StellarEvolutionTests
{
    private static StellarParameters Params(float accretionRadius = 0.05f) => new StellarParameters
    {
        Units = UnitSystem.SolarParsec,
        TimeAcceleration = 1f,
        AccretionRadius = accretionRadius,
        GasParticleMass = 0.1f,
        GasRadius = 0.01f,
        StarFormationMass = 10f, // test gas (0.1 M☉) stays gas unless a test lowers this
    };

    [Theory]
    [InlineData(1f, BodyClass.WhiteDwarf, 0.6f)]
    [InlineData(10f, BodyClass.NeutronStar, 1.4f)]
    [InlineData(30f, BodyClass.BlackHole, 12f)]
    public void EndOfLife_ProducesRemnant_AndEjectaPlusRemnantEqualsProgenitor(float mass, BodyClass remnant, float remnantMass)
    {
        var p = Params();
        var evo = new StellarEvolution(p);
        var ps = new ParticleSystem(4);
        ps.EnsureCapacity(ps.Count + 200);
        Vector3 v0 = new Vector3(4f, -5f, 6f);
        ps.Add(new Vector3(1f, 2f, 3f), v0, mass, 1e-6f, BodyClass.Star, (float)(evo.LifetimeSim(mass) * 1.001));
        int capacity = ps.Capacity;

        evo.Evolve(ps, 0f);

        int remnantIndex = Enumerable.Range(0, ps.Count).Single(i => ps.Class[i] != BodyClass.Gas);
        Assert.Equal(remnant, ps.Class[remnantIndex]);
        Assert.Equal(remnantMass, ps.Mass[remnantIndex], 4);
        int ejecta = ps.Count - 1;
        Assert.InRange(ejecta, 2, 64);
        Assert.Equal(mass, TestHelpers.TotalMass(ps), 4);
        Vector3 p1 = TestHelpers.TotalMomentum(ps);
        Assert.True((p1 - v0 * mass).Length() < 1e-3f * mass * v0.Length(), $"momentum {v0 * mass} -> {p1}");
        Assert.Equal(capacity, ps.Capacity); // headroom was enough: no reallocation in the step
        if (remnant != BodyClass.WhiteDwarf) Assert.Equal(1, evo.Supernovae);
        if (remnant == BodyClass.BlackHole) Assert.Equal(1, evo.BlackHolesFormed);
    }

    [Fact]
    public void WhiteDwarfOverChandrasekhar_BecomesNeutronStar_AndNeutronStarOverTov_BecomesBlackHole()
    {
        var evo = new StellarEvolution(Params());
        var ps = new ParticleSystem(2);
        ps.Add(new Vector3(0, 0, 0), Vector3.Zero, 1.45f, 1e-8f, BodyClass.WhiteDwarf);
        ps.Add(new Vector3(10, 0, 0), Vector3.Zero, 2.6f, 1e-12f, BodyClass.NeutronStar);

        evo.Evolve(ps, 0f);

        Assert.Equal(BodyClass.NeutronStar, ps.Class[0]);
        Assert.Equal(BodyClass.BlackHole, ps.Class[1]);
        // Black hole radius is the event horizon r_s = 2GM/c^2 (G = 1).
        float c = (float)UnitSystem.SolarParsec.SpeedOfLight;
        Assert.Equal(2f * 2.6f / (c * c), ps.Radius[1], 6);
    }

    [Fact]
    public void GasClump_AtStarFormationMass_IgnitesAsStar()
    {
        var p = Params();
        p.StarFormationMass = 0.5f;
        var evo = new StellarEvolution(p);
        var ps = new ParticleSystem(2);
        ps.Add(Vector3.Zero, Vector3.Zero, 0.49f, 0.02f, BodyClass.Gas);
        ps.Add(new Vector3(5, 0, 0), Vector3.Zero, 0.5f, 0.02f, BodyClass.Gas);

        evo.Evolve(ps, 0f);

        Assert.Equal(BodyClass.Gas, ps.Class[0]);
        Assert.Equal(BodyClass.Star, ps.Class[1]);
        Assert.Equal(0f, ps.Age[1]);
        Assert.Equal(1, evo.StarsFormed);
    }

    private static Vector3 RadiationOnGasAt(float starMass, Vector3 gasPos, float gasMass = 0.1f, float gasRadius = 0.01f)
    {
        var p = Params();
        var rad = new RadiationPressure(p);
        var ps = new ParticleSystem(2);
        ps.Add(Vector3.Zero, Vector3.Zero, starMass, 1e-7f, BodyClass.Star);
        ps.Add(gasPos, Vector3.Zero, gasMass, gasRadius, BodyClass.Gas);
        rad.AddAccelerations(ps);
        Assert.Equal(Vector3.Zero, ps.Acceleration[0]); // stars are not pushed
        return ps.Acceleration[1];
    }

    [Fact]
    public void RadiationPressure_PointsAwayFromStar_AndFollowsInverseSquareAndLuminosity()
    {
        var gasPos = new Vector3(0.3f, -0.4f, 0f); // r = 0.5
        Vector3 a1 = RadiationOnGasAt(10f, gasPos);
        Assert.True(Vector3.Dot(Vector3.Normalize(a1), Vector3.Normalize(gasPos)) > 0.9999f, $"a = {a1}");

        // a = L * pi R^2 / (4 pi r^2 c m)
        var u = UnitSystem.SolarParsec;
        double L = u.SolarLuminosity * Math.Pow(10, 3.5);
        double expected = L * 0.01 * 0.01 / (4 * 0.25 * u.SpeedOfLight * 0.1);
        Assert.Equal(1.0, a1.Length() / expected, 3);

        Vector3 a2 = RadiationOnGasAt(10f, gasPos * 2f);
        Assert.Equal(0.25, a2.Length() / a1.Length(), 4);

        Vector3 a3 = RadiationOnGasAt(20f, gasPos);
        Assert.Equal(Math.Pow(2, 3.5), a3.Length() / a1.Length(), 3);
    }

    [Fact]
    public void SinkAccretion_AbsorbsBoundGasOnly_ConservingMassAndMomentum()
    {
        var evo = new StellarEvolution(Params(accretionRadius: 0.05f));
        var ps = new ParticleSystem(4);
        ps.Add(Vector3.Zero, new Vector3(1f, 0, 0), 2f, 1e-7f, BodyClass.Star);
        ps.Add(new Vector3(0.02f, 0, 0), new Vector3(1f, 0.5f, 0), 0.1f, 0.01f, BodyClass.Gas);     // bound: absorbed
        ps.Add(new Vector3(0, 0.03f, 0), new Vector3(1f, 500f, 0), 0.1f, 0.01f, BodyClass.Gas);     // fast: unbound
        ps.Add(new Vector3(0, 0, 0.2f), new Vector3(1f, 0, 0), 0.1f, 0.01f, BodyClass.Gas);         // outside r_acc
        double m0 = TestHelpers.TotalMass(ps);
        Vector3 p0 = TestHelpers.TotalMomentum(ps);

        evo.Evolve(ps, 0f);

        Assert.Equal(3, ps.Count);
        int star = Enumerable.Range(0, ps.Count).Single(i => ps.Class[i] == BodyClass.Star);
        Assert.Equal(2.1f, ps.Mass[star], 5);
        Assert.Equal(m0, TestHelpers.TotalMass(ps), 5);
        Assert.True((TestHelpers.TotalMomentum(ps) - p0).Length() < 1e-4f);
    }

    [Fact]
    public void BlackHole_CapturesBoundStar_AndCountsTheMerger()
    {
        var evo = new StellarEvolution(Params(accretionRadius: 0.05f));
        var ps = new ParticleSystem(2);
        ps.Add(Vector3.Zero, Vector3.Zero, 10f, 1e-12f, BodyClass.BlackHole);
        ps.Add(new Vector3(0.01f, 0, 0), new Vector3(0, 1f, 0), 1f, 1e-7f, BodyClass.Star, 5f);
        Vector3 p0 = TestHelpers.TotalMomentum(ps);

        evo.Evolve(ps, 0f);

        Assert.Equal(1, ps.Count);
        Assert.Equal(BodyClass.BlackHole, ps.Class[0]);
        Assert.Equal(11f, ps.Mass[0], 5);
        Assert.Equal(1, evo.BlackHoleMergers);
        Assert.True((TestHelpers.TotalMomentum(ps) - p0).Length() < 1e-5f);
    }
}
