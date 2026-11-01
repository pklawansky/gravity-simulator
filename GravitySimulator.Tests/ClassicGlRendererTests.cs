using Xunit.Abstractions;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using GravitySimulator.Classic;
using GravitySimulator.Engines;
using GravitySimulator.Sph;
using OpenTK.Graphics.OpenGL4;
using GdiVector = GravitySimulator.Models.Vector;
using PointLight = GravitySimulator.Models.PointLight;
using SphereModel = GravitySimulator.Models.SphereModel;
using Plane = GravitySimulator.Models.Plane;

namespace GravitySimulator.Tests;

/// <summary>
/// The classic OpenGL renderer (its programs' build test is in ShaderCompileTests): a sphere rendered on
/// the GPU is centred on the pixel where the GDI renderer's Convert3DTo2DFixedPerspective puts it, for
/// points across the box and focus values 0..1, and all five walls are drawn, the back one included
/// (skipped without an OpenGL 4.3 context).
/// </summary>
[Trait("Category", "GPU")]
[Collection("GPU")]
public class ClassicGlRendererTests(ITestOutputHelper output)
{
    private static HeadlessGlContext Context()
    {
        var gl = HeadlessGlContext.TryCreate(out string error);
        Skip.If(gl == null, "No OpenGL 4.3 core context: " + error);
        return gl!;
    }

    /// <summary>A w x h colour + depth framebuffer, bound while alive; Read() returns RGBA rows bottom-up.</summary>
    private sealed class Offscreen : IDisposable
    {
        private readonly int _fbo, _color, _depth, _w, _h;
        public readonly byte[] Pixels;

        public Offscreen(int w, int h)
        {
            _w = w; _h = h;
            Pixels = new byte[w * h * 4];
            _fbo = GL.GenFramebuffer(); _color = GL.GenRenderbuffer(); _depth = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _color);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, w, h);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, w, h);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _color);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _depth);
            Assert.Equal(FramebufferErrorCode.FramebufferComplete, GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
        }

        public byte[] Read()
        {
            GL.ReadPixels(0, 0, _w, _h, OpenTK.Graphics.OpenGL4.PixelFormat.Rgba, PixelType.UnsignedByte, Pixels);
            return Pixels;
        }

        public void Dispose()
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(_fbo);
            GL.DeleteRenderbuffer(_color);
            GL.DeleteRenderbuffer(_depth);
        }
    }

    [SkippableTheory]
    [InlineData(600, 600)]
    [InlineData(800, 500)]
    public void SphereCentres_LandWhereGdiPutsThem(int w, int h)
    {
        using var gl = Context();
        using var r = new ClassicGlRenderer();
        r.Initialize();
        using var target = new Offscreen(w, h);
        var bounds = new Vector3(w / 100f, w / 100f, h / 100f);
        var gdiBounds = new GdiVector(bounds.X, bounds.Y, bounds.Z);

        // One very bright light in front so every sphere pixel is lit; no light markers, no walls.
        var frame = new ClassicFrame { Bounds = bounds, LightMarkerRadius = 0f };
        frame.Lights.Add(new ClassicLight { Position = new Vector3(bounds.X / 2f, -bounds.Y, bounds.Z / 2f), Intensity = 1e6f, Ambience = 2f });
        float worst = 0f;
        string worstAt = "";
        int cases = 0;
        const float radius = 0.3f;

        foreach (float focus in new[] { 0f, 0.27f, 0.5f, 0.73f, 1f })
            foreach (float fx in new[] { 0.13f, 0.52f, 0.87f })
                foreach (float fy in new[] { 0.13f, 0.52f, 0.87f })
                    foreach (float fz in new[] { 0.13f, 0.52f, 0.87f })
                    {
                        var p = new Vector3(fx * bounds.X, fy * bounds.Y, fz * bounds.Z);
                        frame.Focus = focus;
                        frame.Spheres.Clear();
                        frame.Spheres.Add(new ClassicSphere { Position = p, Radius = radius });
                        r.Render(frame, w, h);
                        byte[] pixels = target.Read();

                        double sx = 0, sy = 0; int n = 0;
                        for (int row = 0; row < h; row++)
                            for (int col = 0; col < w; col++)
                                if (pixels[(row * w + col) * 4 + 1] > 16) // green channel: the sphere
                                {
                                    sx += col + 0.5; sy += h - (row + 0.5); n++;  // continuous top-left coordinates
                                }
                        Assert.True(n > 0, $"sphere not drawn at {p} focus {focus}");
                        var gdi = GraphicsEngine.Convert3DTo2DFixedPerspective(new GdiVector(p.X, p.Y, p.Z), radius, w, h, gdiBounds, focus);
                        float err = MathF.Max(MathF.Abs((float)(sx / n) - gdi.x), MathF.Abs((float)(sy / n) - gdi.y));
                        if (err > worst) { worst = err; worstAt = $"{p} focus {focus} ({n} px)"; }
                        cases++;
                    }

        output.WriteLine($"{w}x{h}: {cases} cases, worst centre difference {worst:0.###} px at {worstAt}");
        Assert.Equal(135, cases);
        Assert.True(worst <= 1f, $"worst centre difference {worst:0.###} px at {worstAt}");
    }

    /// <summary>The five walls as Form1 builds them all show, the back wall (y = bounds.y, at the far depth) included.</summary>
    [SkippableTheory]
    [InlineData(0.2f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void AllWalls_AreDrawn(float focus)
    {
        using var gl = Context();
        using var r = new ClassicGlRenderer();
        r.Initialize();
        const int w = 600, h = 600;
        using var target = new Offscreen(w, h);
        var b = new GdiVector(6f, 6f, 6f);
        r.SetWalls(new[]
        {
            new Plane(1, new GdiVector(0, 0, 0), new GdiVector(b.x, b.y, 0), Plane.PlaneAxis.xy, 0.1f, true),
            new Plane(2, new GdiVector(0, 0, 0), new GdiVector(0, b.y, b.z), Plane.PlaneAxis.yz, 0.1f, true),
            new Plane(3, new GdiVector(b.x, 0, 0), new GdiVector(b.x, b.y, b.z), Plane.PlaneAxis.yz, 0.1f, false),
            new Plane(4, new GdiVector(0, 0, b.z), new GdiVector(b.x, b.y, b.z), Plane.PlaneAxis.xy, 0.1f, false),
            new Plane(1, new GdiVector(0, b.y, 0), new GdiVector(b.x, b.y, b.z), Plane.PlaneAxis.xz, 0.1f, true),
        });
        var frame = new ClassicFrame { Bounds = new Vector3(b.x, b.y, b.z), Focus = focus };
        frame.Lights.Add(new ClassicLight { Position = new Vector3(3f, 3f, 3f), Intensity = 1e6f, Ambience = 2f });
        r.Render(frame, w, h);
        byte[] px = target.Read();

        // Sample just inside each wall's on-screen region (top-left pixel coordinates).
        float back = 300f * focus; // half-size of the back wall on screen
        var samples = new (string wall, int x, int y)[]
        {
            ("back", 300, 300),
            ("floor", 300, 300 + (int)(back + (300 - back) / 2)),
            ("ceiling", 300, 300 - (int)(back + (300 - back) / 2)),
            ("left", 300 - (int)(back + (300 - back) / 2), 300),
            ("right", 300 + (int)(back + (300 - back) / 2), 300),
        };
        foreach (var (wall, x, y) in samples)
        {
            if (wall != "back" && focus >= 1f) continue; // at focus 1 the side walls are edge-on
            int row = h - 1 - y, i = (row * w + x) * 4;
            Assert.True(px[i] + px[i + 1] + px[i + 2] > 30, $"{wall} wall missing at ({x}, {y}), focus {focus}");
        }
    }

    // ---- Lighting parity with the GDI facets (Form1's scene: 600 x 600, bounds 6 m, five lights) ----

    private const int W = 600, H = 600;
    private static readonly Vector3 SceneBounds = new Vector3(6f, 6f, 6f);
    private const float SceneScale = 100f; // Form1.sceneScale: SphereModel's facet count depends on it

    /// <summary>Form1's five point lights (intensity 1, ambience 2), 1 m in from the box edges, one 1 m above the floor centre.</summary>
    private static List<PointLight> FormLights()
    {
        var b = SceneBounds; float m = 100f / SceneScale;
        return new List<PointLight>
        {
            new PointLight(1, new GdiVector(b.X - m, b.Y - m, b.Z - m), 1, 2),
            new PointLight(2, new GdiVector(m, b.Y - m, b.Z - m), 1, 2),
            new PointLight(3, new GdiVector(b.X - m, m, b.Z - m), 1, 2),
            new PointLight(4, new GdiVector(m, m, b.Z - m), 1, 2),
            new PointLight(5, new GdiVector(b.X / 2f, b.Y / 2f, m), 1, 2),
        };
    }

    private static ClassicFrame FormFrame(List<PointLight> lights)
    {
        var frame = new ClassicFrame { Bounds = SceneBounds, Focus = 0.5f, LightMarkerRadius = 0f };
        foreach (var l in lights)
            frame.Lights.Add(new ClassicLight { Position = new Vector3(l.Center.x, l.Center.y, l.Center.z), Intensity = (float)l.LightIntensity, Ambience = (float)l.Ambience });
        return frame;
    }

    private static Plane[] FormWalls()
    {
        var b = new GdiVector(SceneBounds.X, SceneBounds.Y, SceneBounds.Z);
        return new[]
        {
            new Plane(1, new GdiVector(0, 0, 0), new GdiVector(b.x, b.y, 0), Plane.PlaneAxis.xy, 0.1f, true),
            new Plane(2, new GdiVector(0, 0, 0), new GdiVector(0, b.y, b.z), Plane.PlaneAxis.yz, 0.1f, true),
            new Plane(3, new GdiVector(b.x, 0, 0), new GdiVector(b.x, b.y, b.z), Plane.PlaneAxis.yz, 0.1f, false),
            new Plane(4, new GdiVector(0, 0, b.z), new GdiVector(b.x, b.y, b.z), Plane.PlaneAxis.xy, 0.1f, false),
            new Plane(1, new GdiVector(0, b.y, 0), new GdiVector(b.x, b.y, b.z), Plane.PlaneAxis.xz, 0.1f, true),
        };
    }

    /// <summary>RGB of a GDI bitmap, top-left origin.</summary>
    private static byte[] GdiPixels(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var rgb = new byte[bmp.Width * bmp.Height * 3];
        var row = new byte[data.Stride];
        for (int y = 0; y < bmp.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
            for (int x = 0; x < bmp.Width; x++)
            {
                int o = (y * bmp.Width + x) * 3;
                rgb[o] = row[x * 3 + 2]; rgb[o + 1] = row[x * 3 + 1]; rgb[o + 2] = row[x * 3];   // BGR in memory
            }
        }
        bmp.UnlockBits(data);
        return rgb;
    }

    /// <summary>RGB of the offscreen GL target, top-left origin.</summary>
    private static byte[] GlPixels(byte[] rgbaBottomUp, int w, int h)
    {
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = ((h - 1 - y) * w + x) * 4, o = (y * w + x) * 3;
                rgb[o] = rgbaBottomUp[i]; rgb[o + 1] = rgbaBottomUp[i + 1]; rgb[o + 2] = rgbaBottomUp[i + 2];
            }
        return rgb;
    }

    /// <summary>Mean and max of one channel over the pixels the predicate selects.</summary>
    private static (double mean, int max, int n) Stats(byte[] rgb, int channel, Func<int, int, bool> take)
    {
        double sum = 0; int max = 0, n = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (take(x, y))
                {
                    int v = rgb[(y * W + x) * 3 + channel];
                    sum += v; n++; if (v > max) max = v;
                }
        return (n == 0 ? 0 : sum / n, max, n);
    }

    /// <summary>
    /// Tolerances of the GL shading against the GDI facets (channel values 0..255). One sphere's mean
    /// may differ by up to 16 because GDI lights each facet (about 22 degrees across) at its centre and
    /// the impostor lights every pixel; across a whole scene those differences average out to a few levels.
    /// </summary>
    public const double SphereMeanTolerance = 16, SphereMaxTolerance = 20, SceneMeanTolerance = 4, SceneBrightShareTolerance = 0.2,
        WallMeanTolerance = 4, WallMaxTolerance = 8;

    private static Func<int, int, bool> Disc(Vector3 p, float radius, float focus)
    {
        var c = GraphicsEngine.Convert3DTo2DFixedPerspective(new GdiVector(p.X, p.Y, p.Z), radius * W / SceneBounds.X, W, H,
            new GdiVector(SceneBounds.X, SceneBounds.Y, SceneBounds.Z), focus);
        float inner = c.z - 1.5f;   // the disc, away from the rim pixels the two rasterise differently
        return (x, y) => (x + 0.5f - c.x) * (x + 0.5f - c.x) + (y + 0.5f - c.y) * (y + 0.5f - c.y) <= inner * inner;
    }

    /// <summary>The GDI renderer's spheres, as Form1 draws them (far to near, facet meshes at sceneScale, transparent pen), on black.</summary>
    private static byte[] GdiSpheres(IEnumerable<(Vector3 p, float radius)> spheres, List<PointLight> lights, float focus)
    {
        using var bmp = GraphicsEngine.GetBitmap(Brushes.Black, W, H);
        foreach (var (p, radius) in spheres.OrderByDescending(s => s.p.Y))
            GraphicsEngine.DrawModelFacets3DFixedPerspective(bmp, new SphereModel(0, new GdiVector(p.X, p.Y, p.Z), radius, SceneScale),
                new Pen(Color.Transparent, 0f), Color.LightGreen, W, H, new GdiVector(SceneBounds.X, SceneBounds.Y, SceneBounds.Z), lights, focus);
        return GdiPixels(bmp);
    }

    /// <summary>
    /// A sphere lit by Form1's five lights has the GDI facets' brightness on the GPU: the mean and the
    /// max of the green channel over its disc (the facet mesh, SphereModel(.., sceneScale), drawn by
    /// GraphicsEngine into a Bitmap, against the impostor) agree within the tolerances above, at
    /// positions from right next to the floor light to the far corners.
    /// </summary>
    [SkippableFact]
    public void SphereBrightness_MatchesGdiFacets()
    {
        using var gl = Context();
        using var r = new ClassicGlRenderer();
        r.Initialize();
        using var target = new Offscreen(W, H);
        var lights = FormLights();
        var frame = FormFrame(lights);
        var cases = new (Vector3 p, float radius)[]
        {
            (new Vector3(3.0f, 2.3f, 1.3f), 0.3f),   // in front of and above the floor light
            (new Vector3(3.4f, 3.0f, 1.0f), 0.25f),  // beside the floor light
            (new Vector3(3.0f, 3.0f, 3.0f), 0.3f),   // box centre
            (new Vector3(1.5f, 2.0f, 4.5f), 0.2f),   // upper left, near the front lights
            (new Vector3(4.6f, 1.2f, 4.4f), 0.3f),   // near light 3
            (new Vector3(4.5f, 4.5f, 2.0f), 0.15f),  // far right, low
        };
        bool allWithin = true;
        foreach (var (p, radius) in cases)
        {
            var disc = Disc(p, radius, frame.Focus);
            var gdi = Stats(GdiSpheres(new[] { (p, radius) }, lights, frame.Focus), 1, disc);

            frame.Spheres.Clear();
            frame.Spheres.Add(new ClassicSphere { Position = p, Radius = radius });
            r.Render(frame, W, H);
            var ogl = Stats(GlPixels(target.Read(), W, H), 1, disc);

            bool ok = Math.Abs(ogl.mean - gdi.mean) <= SphereMeanTolerance && Math.Abs(ogl.max - gdi.max) <= SphereMaxTolerance;
            allWithin &= ok;
            output.WriteLine($"sphere {p} r {radius} ({gdi.n} px): GDI mean {gdi.mean:0.0} max {gdi.max} | GL mean {ogl.mean:0.0} max {ogl.max}{(ok ? "" : "  <-- outside tolerance")}");
        }
        Assert.True(allWithin, "GL sphere brightness differs from the GDI facets beyond tolerance (see output)");
    }

    /// <summary>
    /// A whole scene of 20 spheres (seeded positions and Form1's radii, 0.1 to 0.3 m) is as bright on the
    /// GPU as with the GDI facets: the mean green over all sphere pixels within SceneMeanTolerance, and the
    /// share of bright pixels (green 128 or more, the lit faces and highlights) within 20% of GDI's.
    /// </summary>
    [SkippableFact]
    public void SceneSphereBrightness_MatchesGdiFacets()
    {
        using var gl = Context();
        using var r = new ClassicGlRenderer();
        r.Initialize();
        using var target = new Offscreen(W, H);
        var lights = FormLights();
        var frame = FormFrame(lights);
        var rnd = new Random(3);
        var spheres = new List<(Vector3 p, float radius)>();
        for (int i = 0; i < 20; i++)
            spheres.Add((new Vector3(0.5f + 5f * (float)rnd.NextDouble(), 0.5f + 5f * (float)rnd.NextDouble(), 0.5f + 5f * (float)rnd.NextDouble()),
                0.1f + 0.2f * (float)rnd.NextDouble()));
        var discs = spheres.Select(s => Disc(s.p, s.radius, frame.Focus)).ToArray();
        Func<int, int, bool> anyDisc = (x, y) => discs.Any(d => d(x, y));

        var gdiRgb = GdiSpheres(spheres, lights, frame.Focus);
        foreach (var (p, radius) in spheres) frame.Spheres.Add(new ClassicSphere { Position = p, Radius = radius });
        r.Render(frame, W, H);
        var glRgb = GlPixels(target.Read(), W, H);

        var gdi = Stats(gdiRgb, 1, anyDisc);
        var ogl = Stats(glRgb, 1, anyDisc);
        var gdiBright = Stats(gdiRgb, 1, (x, y) => anyDisc(x, y) && gdiRgb[(y * W + x) * 3 + 1] >= 128).n;
        var glBright = Stats(glRgb, 1, (x, y) => anyDisc(x, y) && glRgb[(y * W + x) * 3 + 1] >= 128).n;
        output.WriteLine($"scene ({gdi.n} px): GDI mean {gdi.mean:0.0}, {gdiBright} px >= 128 | GL mean {ogl.mean:0.0}, {glBright} px >= 128");
        Assert.True(Math.Abs(ogl.mean - gdi.mean) <= SceneMeanTolerance, $"scene mean: GDI {gdi.mean:0.0}, GL {ogl.mean:0.0}");
        Assert.True(Math.Abs(glBright - gdiBright) <= SceneBrightShareTolerance * gdiBright, $"bright pixels: GDI {gdiBright}, GL {glBright}");
    }

    /// <summary>The walls have the GDI facets' brightness too: mean and max of each channel over the whole frame.</summary>
    [SkippableFact]
    public void WallBrightness_MatchesGdiFacets()
    {
        using var gl = Context();
        using var r = new ClassicGlRenderer();
        r.Initialize();
        using var target = new Offscreen(W, H);
        var lights = FormLights();
        var walls = FormWalls();
        r.SetWalls(walls);
        var frame = FormFrame(lights);
        r.Render(frame, W, H);
        var glRgb = GlPixels(target.Read(), W, H);

        var gdiBounds = new GdiVector(SceneBounds.X, SceneBounds.Y, SceneBounds.Z);
        using var bmp = GraphicsEngine.GetBitmap(Brushes.Black, W, H);
        foreach (var wall in walls)
            GraphicsEngine.DrawModelFacets3DFixedPerspective(bmp, wall, new Pen(Color.Transparent, 0f), Color.LightBlue, W, H, gdiBounds, lights, frame.Focus);
        var gdiRgb = GdiPixels(bmp);

        bool allWithin = true;
        foreach (var (name, ch) in new[] { ("red", 0), ("green", 1), ("blue", 2) })
        {
            var gdi = Stats(gdiRgb, ch, (x, y) => true);
            var ogl = Stats(glRgb, ch, (x, y) => true);
            bool ok = Math.Abs(ogl.mean - gdi.mean) <= WallMeanTolerance && Math.Abs(ogl.max - gdi.max) <= WallMaxTolerance;
            allWithin &= ok;
            output.WriteLine($"walls {name}: GDI mean {gdi.mean:0.0} max {gdi.max} | GL mean {ogl.mean:0.0} max {ogl.max}{(ok ? "" : "  <-- outside tolerance")}");
        }
        Assert.True(allWithin, "GL wall brightness differs from the GDI facets beyond tolerance (see output)");
    }
}
