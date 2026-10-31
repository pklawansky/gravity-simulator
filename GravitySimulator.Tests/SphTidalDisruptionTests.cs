using System.Numerics;
using GravitySimulator.Planets;
using GravitySimulator.Sph;

namespace GravitySimulator.Tests;

/// <summary>Setup of the "Star near a black hole" preset: a watchable, still recognisable tidal disruption.</summary>
public class SphTidalDisruptionTests
{
    [Fact]
    public void Setup_IsADeepEccentricDisruption_JustOutsideTheAccretionRadius()
    {
        var sc = SphPresets.Create(SphPresetKind.TidalDisruption, 4_000, 1);
        float mbh = sc.Sinks.Mass[0];
        Assert.Equal(BodyClass.BlackHole, sc.Sinks.Class[0]);
        float rStar = SphPresets.StarRadius * (float)sc.Units.SolarRadius;
        float rt = rStar * MathF.Cbrt(mbh / SphPresets.StarMass);

        // Osculating orbit of the launch point around the hole.
        Vector3 x = sc.LaunchOffset, v = sc.LaunchVelocity;
        float r = x.Length();
        float energy = 0.5f * v.LengthSquared() - mbh / r;
        float h = Vector3.Cross(x, v).Length();
        float e = MathF.Sqrt(1f + 2f * energy * h * h / (mbh * mbh));
        float rp = h * h / (mbh * (1f + e));
        Assert.True(energy < 0f, "bound orbit (debris returns quickly)");
        Assert.Equal(SphPresets.Eccentricity, e, 1e-3f);
        Assert.Equal(rt / SphPresets.Beta, rp, rp * 1e-3f);
        Assert.True(Vector3.Dot(x, v) < 0f, "starts inbound");
        Assert.InRange(r / rt, 1.2f, 2f);

        // Compact star: big next to its tidal radius, and r_t a few tens of Schwarzschild radii.
        Assert.InRange(rt / rStar, 8f, 20f);
        float rs = (float)(2.0 * mbh / (sc.Units.SpeedOfLight * sc.Units.SpeedOfLight));
        Assert.Equal(6f * rs, sc.BlackHoleAccretionRadius, 1e-6f);
        Assert.InRange(rp / sc.BlackHoleAccretionRadius, 1f, 1.5f); // just outside: the debris spread reaches in
        // The camera frames the region the debris orbits in (apocentre ~1.8 r_t).
        Assert.InRange(sc.CameraDistance / rt, 3f, 6f);
    }
}
