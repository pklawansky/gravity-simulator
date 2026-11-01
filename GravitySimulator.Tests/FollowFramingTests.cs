using System.Numerics;
using GravitySimulator.Sph;

namespace GravitySimulator.Tests;

/// <summary>The follow camera's framing maths (gas positions + hole -> centre and distance) and damping.</summary>
public class FollowFramingTests
{
    private const float Fov = MathF.PI / 4f;
    private static readonly Vector3 FarHole = new Vector3(250f, 0f, 0f);

    private static float[] Cloud(int n, float radius, Vector3 centre, int seed, Vector3 stretch = default)
    {
        if (stretch == default) stretch = Vector3.One;
        var rng = new Random(seed);
        var p = new float[n * 4];
        for (int i = 0; i < n; i++)
        {
            Vector3 x;
            do x = new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1);
            while (x.LengthSquared() > 1f);
            x = centre + x * radius * stretch;
            p[i * 4] = x.X; p[i * 4 + 1] = x.Y; p[i * 4 + 2] = x.Z; p[i * 4 + 3] = 0.05f;
        }
        return p;
    }

    private static readonly FollowFraming.Settings S = new() { MinHalfView = 8f, FieldOfView = Fov };

    [Fact]
    public void StaticCloud_FarFromTheHole_IsFramedTightly_AndSteadily()
    {
        var p = Cloud(20_000, 1f, new Vector3(-200f, 30f, 0f), 1);
        var a = FollowFraming.Compute(p, 20_000, FarHole, S);
        var b = FollowFraming.Compute(p, 20_000, FarHole, S);
        Assert.Equal(a, b); // deterministic: a static cloud gives a steady frame
        Assert.True((a.Centre - new Vector3(-200f, 30f, 0f)).Length() < 0.1f, $"centre {a.Centre}");
        // A 1-radius star fills >= 10% of the view: half-view = MinHalfView = 8.
        float halfView = a.Distance * MathF.Tan(Fov / 2f);
        Assert.Equal(8f, halfView, 0.01f);
        Assert.True(2f / (2f * halfView) >= 0.1f);
    }

    [Fact]
    public void AFewFarOutliers_DoNotZoomOut()
    {
        var p = Cloud(20_000, 30f, Vector3.Zero, 2);
        var clean = FollowFraming.Compute(p, 20_000, FarHole * 10f, S);
        for (int k = 0; k < 50; k++) { p[k * 4] = 5000f + k; p[k * 4 + 1] = -4000f; } // 0.25% outliers
        var dirty = FollowFraming.Compute(p, 20_000, FarHole * 10f, S);
        Assert.InRange(dirty.Distance / clean.Distance, 0.97f, 1.03f);
        Assert.True((dirty.Centre - clean.Centre).Length() < 1f);
    }

    [Fact]
    public void Distance_GrowsMonotonically_AsTheCloudStretches()
    {
        float prev = 0f;
        foreach (float s in new[] { 1f, 2f, 4f, 8f, 16f, 32f, 64f })
        {
            var p = Cloud(20_000, 2f, Vector3.Zero, 3, new Vector3(s, 1f, 1f));
            var f = FollowFraming.Compute(p, 20_000, new Vector3(0f, 0f, 1e5f), S);
            Assert.True(f.Distance >= prev, $"stretch {s}: {f.Distance} < {prev}");
            prev = f.Distance;
        }
        Assert.True(prev > 5f * (8f / MathF.Tan(Fov / 2f)), "a long stream widens the view well beyond the minimum");
    }

    [Fact]
    public void HoleNearby_IsBroughtIntoView()
    {
        var p = Cloud(20_000, 1f, new Vector3(-20f, 0f, 0f), 4);
        var f = FollowFraming.Compute(p, 20_000, Vector3.Zero, S);
        float halfView = f.Distance * MathF.Tan(Fov / 2f);
        Assert.True((f.Centre - Vector3.Zero).Length() < halfView, "hole inside the view");
        Assert.True((f.Centre - new Vector3(-20f, 0f, 0f)).Length() < halfView, "star inside the view");
    }

    [Fact]
    public void Tracking_AMovingTarget_LeavesNoLag()
    {
        var v = new Vector3(3f, -1f, 0f); // world units per frame
        var prevTarget = new FollowFraming.Frame(Vector3.Zero, 100f);
        var cur = new FollowFraming.Frame(new Vector3(5f, 5f, 0f), 100f);
        for (int k = 1; k <= 300; k++)
        {
            var target = new FollowFraming.Frame(v * k, 100f);
            cur = FollowFraming.Track(cur, prevTarget, target, 1f / 60f);
            prevTarget = target;
        }
        // Float rounding at |x| ~ 950 leaves ~1e-3.
        Assert.True((cur.Centre - prevTarget.Centre).Length() < 1e-2f, $"lag {(cur.Centre - prevTarget.Centre).Length()}");
        // Plain damping of the same target lags by about v * tau / dt.
        var plain = new FollowFraming.Frame(Vector3.Zero, 100f);
        for (int k = 1; k <= 300; k++) plain = FollowFraming.Damp(plain, new FollowFraming.Frame(v * k, 100f), 1f / 60f);
        Assert.True((plain.Centre - v * 300).Length() > 10f);
    }

    [Fact]
    public void Damping_Converges_WithoutOvershoot_AndZoomsOutFaster()
    {
        var target = new FollowFraming.Frame(new Vector3(10f, -5f, 2f), 300f);
        var cur = new FollowFraming.Frame(Vector3.Zero, 100f);
        float prevD = cur.Distance;
        for (int k = 0; k < 600; k++)
        {
            cur = FollowFraming.Damp(cur, target, 1f / 60f);
            Assert.True(cur.Distance >= prevD && cur.Distance <= target.Distance + 1e-3f, "monotone, no overshoot");
            prevD = cur.Distance;
        }
        Assert.Equal(300f, cur.Distance, 0.01f);
        Assert.True((cur.Centre - target.Centre).Length() < 1e-3f);

        var outward = FollowFraming.Damp(new FollowFraming.Frame(Vector3.Zero, 100f), new FollowFraming.Frame(Vector3.Zero, 200f), 0.1f);
        var inward = FollowFraming.Damp(new FollowFraming.Frame(Vector3.Zero, 200f), new FollowFraming.Frame(Vector3.Zero, 100f), 0.1f);
        Assert.True(outward.Distance - 100f > 200f - inward.Distance, "zoom-out is quicker than zoom-in");
    }
}
