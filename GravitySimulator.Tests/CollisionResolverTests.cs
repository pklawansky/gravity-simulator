using System.Numerics;
using GravitySimulator.Planets;

namespace GravitySimulator.Tests;

public class CollisionResolverTests
{
    // Two touching bodies on the X axis approaching head-on with the given closing speed.
    private static ParticleSystem HeadOn(float closingSpeed, float m1 = 2f, float m2 = 1f, float r1 = 0.1f, float r2 = 0.05f)
    {
        var ps = new ParticleSystem(2);
        float gap = (r1 + r2) * 0.9f; // overlapping
        // Velocities chosen so that total momentum is non-zero (catches sign errors).
        ps.Add(new Vector3(0, 0, 0), new Vector3(closingSpeed * 0.5f, 0.3f, 0), m1, r1);
        ps.Add(new Vector3(gap, 0, 0), new Vector3(-closingSpeed * 0.5f, 0.3f, 0), m2, r2);
        return ps;
    }

    private static float EscapeSpeed(float m1, float m2, float r1, float r2) =>
        MathF.Sqrt(2f * (m1 + m2) / (r1 + r2));

    [Fact]
    public void Merge_ConservesMassAndMomentum_AndRadiusFollowsVolume()
    {
        var ps = HeadOn(closingSpeed: 0.1f * EscapeSpeed(2f, 1f, 0.1f, 0.05f));
        double mass0 = TestHelpers.TotalMass(ps);
        Vector3 p0 = TestHelpers.TotalMomentum(ps);
        Vector3 com0 = (ps.Position[0] * ps.Mass[0] + ps.Position[1] * ps.Mass[1]) / (ps.Mass[0] + ps.Mass[1]);
        var resolver = new CollisionResolver { MergeThreshold = 1f, Restitution = 0.5f };

        resolver.Resolve(ps);

        Assert.Equal(1, ps.Count);
        Assert.Equal(1, resolver.LastMerges);
        Assert.Equal(mass0, ps.Mass[0], 5);
        Vector3 p1 = TestHelpers.TotalMomentum(ps);
        Assert.True((p1 - p0).Length() < 1e-5f, $"momentum {p0} -> {p1}");
        Assert.Equal(MathF.Cbrt(0.1f * 0.1f * 0.1f + 0.05f * 0.05f * 0.05f), ps.Radius[0], 5);
        Assert.True((ps.Position[0] - com0).Length() < 1e-6f);
    }

    [Fact]
    public void BelowThreshold_Merges()
    {
        float vEsc = EscapeSpeed(2f, 1f, 0.1f, 0.05f);
        var ps = HeadOn(closingSpeed: 0.9f * vEsc);
        var resolver = new CollisionResolver { MergeThreshold = 1f, Restitution = 0.5f };

        resolver.Resolve(ps);

        Assert.Equal(1, ps.Count);
        Assert.Equal(1, resolver.LastMerges);
        Assert.Equal(0, resolver.LastBounces);
    }

    [Fact]
    public void AboveThreshold_Bounces_ConservingMomentum_AndScalingNormalSpeedByRestitution()
    {
        float vEsc = EscapeSpeed(2f, 1f, 0.1f, 0.05f);
        float closing = 1.5f * vEsc;
        var ps = HeadOn(closingSpeed: closing);
        Vector3 p0 = TestHelpers.TotalMomentum(ps);
        const float e = 0.4f;
        var resolver = new CollisionResolver { MergeThreshold = 1f, Restitution = e };

        resolver.Resolve(ps);

        Assert.Equal(2, ps.Count);
        Assert.Equal(0, resolver.LastMerges);
        Assert.Equal(1, resolver.LastBounces);
        Vector3 p1 = TestHelpers.TotalMomentum(ps);
        Assert.True((p1 - p0).Length() < 1e-5f, $"momentum {p0} -> {p1}");

        Vector3 n = Vector3.Normalize(ps.Position[1] - ps.Position[0]);
        float separatingNormalSpeed = Vector3.Dot(ps.Velocity[1] - ps.Velocity[0], n);
        Assert.Equal(e * closing, separatingNormalSpeed, 4);
        // Tangential (Y) velocities untouched.
        Assert.Equal(0.3f, ps.Velocity[0].Y, 6);
        Assert.Equal(0.3f, ps.Velocity[1].Y, 6);
        // Separated so they no longer overlap.
        float d = Vector3.Distance(ps.Position[0], ps.Position[1]);
        Assert.True(d >= (ps.Radius[0] + ps.Radius[1]) * 0.9999f, $"still overlapping: d={d}");
    }

    [Fact]
    public void NonTouchingBodies_AreLeftAlone()
    {
        var ps = new ParticleSystem(2);
        ps.Add(new Vector3(0, 0, 0), new Vector3(1, 0, 0), 1f, 0.01f);
        ps.Add(new Vector3(1, 0, 0), new Vector3(-1, 0, 0), 1f, 0.01f);
        var resolver = new CollisionResolver();

        resolver.Resolve(ps);

        Assert.Equal(2, ps.Count);
        Assert.Equal(0, resolver.LastMerges + resolver.LastBounces);
    }

    [Fact]
    public void OversizeBody_CollidesWithSmallBodies()
    {
        // A "star" much larger than the dust grid cell still catches dust that touches it.
        var ps = new ParticleSystem(200);
        ps.Add(Vector3.Zero, Vector3.Zero, 1000f, 1f);
        for (int i = 0; i < 100; i++) // evenly spaced on a far ring, so no dust-dust contacts
        {
            float a = i * MathF.PI * 2f / 100f;
            ps.Add(new Vector3(MathF.Cos(a) * 5f, MathF.Sin(a) * 5f, 0), Vector3.Zero, 0.001f, 0.01f);
        }
        ps.Add(new Vector3(0.995f, 0, 0), Vector3.Zero, 0.001f, 0.01f); // touching the star surface
        double mass0 = TestHelpers.TotalMass(ps);
        var resolver = new CollisionResolver { MergeThreshold = 1f };

        resolver.Resolve(ps);

        Assert.Equal(101, ps.Count);
        Assert.Equal(1, resolver.LastMerges);
        Assert.Equal(mass0, TestHelpers.TotalMass(ps), 3);
    }
}
