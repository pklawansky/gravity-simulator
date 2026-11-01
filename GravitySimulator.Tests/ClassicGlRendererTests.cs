using Xunit.Abstractions;
using System.Numerics;
using GravitySimulator.Classic;
using GravitySimulator.Engines;
using GravitySimulator.Sph;
using OpenTK.Graphics.OpenGL4;
using GdiVector = GravitySimulator.Models.Vector;
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
            GL.ReadPixels(0, 0, _w, _h, PixelFormat.Rgba, PixelType.UnsignedByte, Pixels);
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
}
