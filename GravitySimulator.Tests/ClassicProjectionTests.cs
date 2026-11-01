using Xunit.Abstractions;
using System.Numerics;
using GravitySimulator.Classic;
using GravitySimulator.Engines;
using GdiVector = GravitySimulator.Models.Vector;

namespace GravitySimulator.Tests;

/// <summary>
/// The OpenGL classic renderer's projection (scene matrix + depth scale) against the GDI renderer's
/// Convert3DTo2DFixedPerspective, in C#. The GPU side of the same check is in ClassicGlRendererTests.
/// </summary>
public class ClassicProjectionTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Sizes() => new[]
    {
        new object[] { 600, 600 },
        new object[] { 700, 700 },
        new object[] { 800, 500 },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void ToPixel_MatchesGdiProjection(int w, int h)
    {
        var bounds = new Vector3(w / 100f, w / 100f, h / 100f); // as Form1 builds the scene
        var gdiBounds = new GdiVector(bounds.X, bounds.Y, bounds.Z);
        var vp = ClassicProjection.FitViewport(w, h, bounds);
        Assert.Equal(new System.Drawing.Rectangle(0, 0, w, h), vp);

        float worst = 0f;
        foreach (float focus in new[] { 0f, 0.1f, 0.25f, 0.5f, 0.75f, 1f })
            for (float fx = 0f; fx <= 1f; fx += 0.125f)
                for (float fy = 0f; fy <= 1f; fy += 0.125f)
                    for (float fz = 0f; fz <= 1f; fz += 0.125f)
                    {
                        var p = new Vector3(fx * bounds.X, fy * bounds.Y, fz * bounds.Z);
                        var gdi = GraphicsEngine.Convert3DTo2DFixedPerspective(new GdiVector(p.X, p.Y, p.Z), 0.3f, w, h, gdiBounds, focus);
                        var gl = ClassicProjection.ToPixel(p, bounds, focus, vp);
                        worst = MathF.Max(worst, MathF.Max(MathF.Abs(gl.X - gdi.x), MathF.Abs(gl.Y - gdi.y)));
                    }
        output.WriteLine($"{w}x{h}: worst difference {worst:0.####} px"); // float rounding only
        Assert.True(worst < 0.1f, $"worst difference {worst} px");
    }

    [Fact]
    public void Focus_LeftKeyShrinksTheBackOfTheBox()
    {
        // Left lowers focus; the far wall must get smaller on screen, as with GDI.
        var bounds = new Vector3(6f, 6f, 6f);
        var vp = ClassicProjection.FitViewport(600, 600, bounds);
        var corner = new Vector3(0f, bounds.Y, 0f); // far bottom-left corner
        float At(float focus) => 300f - ClassicProjection.ToPixel(corner, bounds, focus, vp).X;
        Assert.True(At(0.4f) < At(0.5f));
        Assert.Equal(300f * 0.5f, At(0.5f), 3);
    }

    [Fact]
    public void FitViewport_KeepsTheSceneAspectWhenResized()
    {
        var bounds = new Vector3(6f, 6f, 6f);
        var vp = ClassicProjection.FitViewport(1000, 600, bounds);
        Assert.Equal(new System.Drawing.Rectangle(200, 0, 600, 600), vp);
    }
}
