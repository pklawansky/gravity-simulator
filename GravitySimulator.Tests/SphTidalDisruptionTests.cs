using System.Numerics;
using GravitySimulator.Planets;
using GravitySimulator.Sph;

namespace GravitySimulator.Tests;

/// <summary>Setup of the "Sun near a black hole" preset: a watchable, still recognisable tidal disruption.</summary>
public class SphTidalDisruptionTests
{
    [Fact]
    public void Setup_IsADeepEccentricDisruption_OfTheSun_JustAboveDirectCapture()
    {
        var sc = SphPresets.Create(SphPresetKind.TidalDisruption, 4_000, 1);
        float mbh = sc.Sinks.Mass[0];
        Assert.Equal(BodyClass.BlackHole, sc.Sinks.Class[0]);
        Assert.Equal(1f, SphPresets.StarMass);
        Assert.Equal(1f, SphPresets.StarRadius);
        float rStar = SphPresets.StarRadius * (float)sc.Units.SolarRadius;
        Assert.Equal(1f, rStar, 1e-6f); // M☉, R☉ units
        float rt = rStar * MathF.Cbrt(mbh / SphPresets.StarMass);

        // Osculating orbit of the launch point around the hole.
        Vector3 x = sc.LaunchOffset, v = sc.LaunchVelocity;
        float r = x.Length();
        float energy = 0.5f * v.LengthSquared() - mbh / r;
        float h = Vector3.Cross(x, v).Length();
        float e = MathF.Sqrt(1f + 2f * energy * h * h / (mbh * mbh));
        float rp = h * h / (mbh * (1f + e));
        Assert.True(energy < 0f, "bound orbit (the stream returns within the viewing time)");
        Assert.Equal(SphPresets.Eccentricity, e, 1e-3f);
        Assert.Equal(rt / SphPresets.Beta, rp, rp * 1e-3f);
        Assert.True(Vector3.Dot(x, v) < 0f, "starts inbound");
        Assert.Equal(SphPresets.StartTidalRadii, r / rt, 1e-3f);

        // Pseudo-Newtonian hole: r_s from the unit system, the sink radius is r_s (rendered horizon),
        // the absorbing inner boundary is the ISCO (3 r_s), and the pericentre sits just above the
        // ~4 r_s direct-capture threshold of the PW potential.
        float rs = (float)(2.0 * mbh / (sc.Units.SpeedOfLight * sc.Units.SpeedOfLight));
        Assert.Equal(rs, sc.Sinks.Radius[0], rs * 1e-5f);
        Assert.True(sc.Parameters.PseudoNewtonianBlackHoles);
        Assert.True(sc.PreciseBlackHoleAccretion);
        Assert.Equal(3f * rs, sc.BlackHoleAccretionRadius, rs * 1e-5f);
        Assert.InRange(rp / rs, 4f, 5.5f);
        Assert.InRange(rt / rs, 15f, 40f);
        // The camera frames the start point.
        Assert.True(sc.CameraDistance > 1.5f * r);
    }

    [Fact]
    public void OtherPresets_KeepNewtonianSinks()
    {
        Assert.False(SphPresets.Create(SphPresetKind.CollapsingCloud, 20_000, 1).Parameters.PseudoNewtonianBlackHoles);
        Assert.False(SphPresets.Create(SphPresetKind.CollidingClouds, 20_000, 1).Parameters.PseudoNewtonianBlackHoles);
        Assert.False(SphPresets.Create(SphPresetKind.CollapsingCloud, 20_000, 1).PreciseBlackHoleAccretion);
    }

    [Fact]
    public void SwallowedMass_IsBookedInDouble_BelowTheHolesFloatResolution()
    {
        var sinks = new SinkManager(new SphParameters());
        var ps = new ParticleSystem(4);
        ps.Add(Vector3.Zero, Vector3.Zero, 1000f, 0.004f, BodyClass.BlackHole);
        sinks.Reset(ps);
        const float m = 1.2e-5f; // one particle: 1.2e-8 of the hole, under float epsilon
        var events = new List<AccretionEvent>();
        for (int k = 0; k < 500; k++) events.Add(new AccretionEvent { Sink = 0, Mass = m, Position = Vector3.UnitX * 0.01f });
        sinks.Apply(events);
        Assert.Equal(500, sinks.AccretedParticles);
        Assert.Equal(500 * (double)m, sinks.BlackHoleAccretedMass, 1e-9);
    }
}
