using GravitySimulator.Planets;

namespace GravitySimulator.Tests;

/// <summary>World radius -> on-screen size of the star / remnant / black-hole spheres, and the marker floor.</summary>
public class CompactBodyRendererTests
{
    private const float Height = 800f;
    private static readonly float M22 = 1f / MathF.Tan(MathF.PI / 8f); // 45° vertical field of view

    [Fact]
    public void ProjectedRadius_DoublesWhenDistanceHalves()
    {
        const float r = 0.00425f; // r_s of the TDE hole in R☉
        float far = CompactBodyRenderer.ProjectedRadiusPx(r, 0.5f, Height, M22);
        float near = CompactBodyRenderer.ProjectedRadiusPx(r, 0.25f, Height, M22);
        Assert.Equal(2f, near / far, 1e-3f);
        // ... and scales with the world radius.
        Assert.Equal(2f, CompactBodyRenderer.ProjectedRadiusPx(2f * r, 0.5f, Height, M22) / far, 1e-3f);
        // Close up it is the exact silhouette, growing faster than 1/d.
        float d = 2f * r;
        Assert.Equal(0.5f * Height * M22 * r / MathF.Sqrt(d * d - r * r), CompactBodyRenderer.ProjectedRadiusPx(r, d, Height, M22), 1e-2f);
    }

    [Fact]
    public void Marker_AppliesOnlyBelowTheThreshold_AndFadesWithoutAPop()
    {
        float m = CompactBodyRenderer.MarkerPx(BodyClass.BlackHole, 1000f);
        Assert.Equal(1f, CompactBodyRenderer.MarkerOpacity(0.01f, m));
        Assert.Equal(1f, CompactBodyRenderer.MarkerOpacity(0.5f * m, m));
        Assert.Equal(0f, CompactBodyRenderer.MarkerOpacity(m, m));
        Assert.Equal(0f, CompactBodyRenderer.MarkerOpacity(10f * m, m));
        // Continuous and monotone through the fade.
        float prev = 1f;
        for (float pr = 0.5f * m; pr <= m; pr += m / 200f)
        {
            float a = CompactBodyRenderer.MarkerOpacity(pr, m);
            Assert.True(a <= prev + 1e-6f && prev - a < 0.02f, $"jump at {pr}: {prev} -> {a}");
            prev = a;
        }
        // Sub-pixel sphere: drawn at the marker size; resolved sphere: its true size.
        Assert.Equal(m, CompactBodyRenderer.DrawnRadiusPx(0.001f, m));
        Assert.Equal(3f * m, CompactBodyRenderer.DrawnRadiusPx(3f * m, m));
    }

    [Fact]
    public void ParsecStar_IsSubPixel_TdeHole_FillsTheViewAtMinimumZoom()
    {
        // A 1 M☉ star in pc units at the closest SPH zoom of the cloud presets (1% of 3.2 pc).
        float rStar = (float)UnitSystem.SolarParsec.SolarRadius;
        Assert.True(CompactBodyRenderer.ProjectedRadiusPx(rStar, 0.032f, Height, M22) < 0.01f);
        // The TDE hole at the closest zoom (1% of the 4.5 r_t framing distance).
        var sc = GravitySimulator.Sph.SphPresets.Create(GravitySimulator.Sph.SphPresetKind.TidalDisruption, 2_000, 1);
        float pr = CompactBodyRenderer.ProjectedRadiusPx(sc.Sinks.Radius[0], 0.01f * sc.CameraDistance, Height, M22);
        Assert.True(pr > 0.5f * Height, $"hole radius {pr} px");
    }
}
