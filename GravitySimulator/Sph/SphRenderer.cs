using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using GravitySimulator.Planets;
using OpenTK.Graphics.OpenGL4;
using ImagingFormat = System.Drawing.Imaging.PixelFormat;

namespace GravitySimulator.Sph
{
    /// <summary>
    /// Draws the SPH gas straight from the solver's buffers (no read-back) as additive Gaussian splats
    /// into a floating-point buffer, coloured by temperature (P/ρ) or density and brightened by density,
    /// tone-mapped to the screen; then the sinks, stars and black holes on top with Planet formation's
    /// point shader and colour rules (<see cref="PlanetRenderer"/>). Needs the GL 4.3 context current.
    /// </summary>
    public sealed class SphRenderer : IDisposable
    {
        private GlProgram _splat, _tonemap, _points;
        private int _gasVao, _emptyVao, _pointVao, _vboPos, _vboRad, _vboMass, _vboClass;
        private int _fbo, _hdrTex, _fbWidth, _fbHeight;
        private float[] _pos = new float[64 * 3], _rad = new float[64], _mass = new float[64], _cls = new float[64];
        private float _logTMin, _logTMax, _logRhoMin, _logRhoMax, _rhoRef = 1f, _refRadius = 1f, _refMass = 1f;

        /// <summary>0 = temperature, 1 = density.</summary>
        public int ColorMode { get; set; }
        /// <summary>Multiplies the automatic exposure.</summary>
        public float Brightness { get; set; } = 1f;

        /// <summary>Overlay drawn in the top-left corner of the view (large first line, smaller second); null = none.</summary>
        public (string Title, string Detail)? Overlay { get; set; }

        private GlProgram _text;
        private int _textTex, _textW, _textH;
        private (string, string)? _textDrawn;

        private const string TextVertexSource = @"#version 430 core
// Screen-space quad (triangle strip) over uRect = (x0, y0, x1, y1) in NDC; texture row 0 at the top.
uniform vec4 uRect;
out vec2 vUv;
void main()
{
    vec2 c = vec2(gl_VertexID & 1, (gl_VertexID >> 1) & 1);
    vUv = vec2(c.x, 1.0 - c.y);
    gl_Position = vec4(mix(uRect.xy, uRect.zw, c), 0.0, 1.0);
}";

        private const string TextFragmentSource = @"#version 430 core
in vec2 vUv;
uniform sampler2D uTex;
out vec4 fragColor;
void main() { fragColor = texture(uTex, vUv); }";

        public void Initialize()
        {
            _splat = GlProgram.Graphics("splat.vert", "splat.frag");
            _tonemap = GlProgram.Graphics("tonemap.vert", "tonemap.frag");
            _points = GlProgram.FromSource("planet points", PlanetRenderer.VertexSource, PlanetRenderer.FragmentSource);
            _gasVao = GL.GenVertexArray();
            _emptyVao = GL.GenVertexArray();
            _pointVao = GL.GenVertexArray();
            GL.BindVertexArray(_pointVao);
            _vboPos = MakeAttribute(0, 3);
            _vboRad = MakeAttribute(1, 1);
            _vboMass = MakeAttribute(2, 1);
            _vboClass = MakeAttribute(3, 1);
            GL.BindVertexArray(0);
            _fbo = GL.GenFramebuffer();
            _hdrTex = GL.GenTexture();
            _text = GlProgram.FromSource("overlay text", TextVertexSource, TextFragmentSource);
            _textTex = GL.GenTexture();
        }

        private static int MakeAttribute(int location, int components)
        {
            int vbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, IntPtr.Zero, IntPtr.Zero, BufferUsageHint.StreamDraw);
            GL.EnableVertexAttribArray(location);
            GL.VertexAttribPointer(location, components, VertexAttribPointerType.Float, false, components * sizeof(float), 0);
            return vbo;
        }

        /// <summary>Colour ranges and exposure reference from the scenario's initial gas.</summary>
        public void Configure(SphScenario sc)
        {
            var g = sc.Gas;
            var p = sc.Parameters;
            double sumRho = 0, sumT = 0, sumR2 = 0, mass = 0;
            Vector3 com = Vector3.Zero;
            for (int i = 0; i < g.Count; i++) { mass += g.Mass[i]; com += g.Position[i] * g.Mass[i]; }
            com /= (float)Math.Max(mass, 1e-30);
            for (int i = 0; i < g.Count; i++)
            {
                float rho = g.Density[i];
                float t = p.Eos == EquationOfState.IdealGas ? (p.Gamma - 1f) * g.U[i] : p.SoundSpeed * p.SoundSpeed;
                sumRho += Math.Log10(Math.Max(rho, 1e-30));
                sumT += Math.Log10(Math.Max(t, 1e-30));
                sumR2 += (g.Position[i] - com).LengthSquared();
            }
            int n = Math.Max(1, g.Count);
            float logRho = (float)(sumRho / n), logT = (float)(sumT / n);
            _logRhoMin = logRho - 1.0f;
            _logRhoMax = logRho + 2.5f;
            _logTMin = logT - 0.4f;
            _logTMax = logT + (p.Eos == EquationOfState.IdealGas ? 2.2f : 1.4f);
            _rhoRef = MathF.Pow(10f, logRho);
            _refRadius = (float)Math.Sqrt(sumR2 / n * 5.0 / 3.0); // radius of a uniform sphere with this rms
            _refMass = (float)mass;
            if (sc.ExposureRadius > 0f) _refRadius = sc.ExposureRadius;
        }

        public void Render(SphSimulation sim, OrbitCamera camera, int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            EnsureFramebuffer(width, height);
            Matrix4x4 view = camera.View, proj = camera.Projection(width / (float)height);
            float pointScale = height * proj.M22;
            var solver = sim.Solver;

            // 1. Gas splats into the HDR buffer.
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            GL.Viewport(0, 0, width, height);
            GL.ClearColor(0f, 0f, 0f, 0f);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.Disable(EnableCap.DepthTest);
            GL.Enable(EnableCap.ProgramPointSize);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.One, BlendingFactor.One);
            if (solver != null && solver.Count > 0)
            {
                GL.MemoryBarrier(MemoryBarrierFlags.VertexAttribArrayBarrierBit);
                var s = _splat;
                s.SetMatrix("uView", view);
                s.SetMatrix("uProj", proj);
                s.Set("uPointScale", pointScale);
                s.Set("uColorMode", ColorMode);
                s.Set("uLogTMin", _logTMin);
                s.Set("uLogTMax", _logTMax);
                s.Set("uLogRhoMin", _logRhoMin);
                s.Set("uLogRhoMax", _logRhoMax);
                s.Set("uRhoRef", _rhoRef);
                // Exposure: the reference sphere's mean column density maps to about 0.8.
                float pxPerUnit = pointScale / MathF.Max(camera.Distance, 1e-6f);
                float meanColumnPx = _refMass / (MathF.PI * _refRadius * _refRadius * pxPerUnit * pxPerUnit);
                s.Set("uExposure", 0.8f * Brightness / MathF.Max(meanColumnPx, 1e-30f));
                GL.BindVertexArray(_gasVao);
                BindAttribute(0, solver.PositionBuffer);
                BindAttribute(1, solver.DensityBuffer);
                BindAttribute(2, solver.MassBuffer);
                GL.UseProgram(s.Handle);
                GL.DrawArrays(PrimitiveType.Points, 0, solver.Count);
            }

            // 2. Tone map to the screen.
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.Viewport(0, 0, width, height);
            GL.Disable(EnableCap.Blend);
            GL.UseProgram(_tonemap.Handle);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _hdrTex);
            _tonemap.Set("uHdr", 0);
            GL.BindVertexArray(_emptyVao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

            // 3. Sinks, stars, black holes and supernova flashes (Planet formation's shader and rules).
            DrawSinks(sim, view, proj, pointScale);
            DrawOverlay(width, height);
            GL.BindVertexArray(0);
        }

        // The overlay text is rasterised with GDI+ into a texture (only when it changes) and drawn as an
        // alpha-blended quad, so it is part of the GL frame and of saved screenshots.
        private void DrawOverlay(int width, int height)
        {
            if (Overlay is not { } o) return;
            if (_textDrawn != o) UploadOverlay(o.Title, o.Detail);
            const int margin = 12;
            float x0 = -1f + 2f * margin / width, x1 = -1f + 2f * (margin + _textW) / width;
            float y1 = 1f - 2f * margin / height, y0 = 1f - 2f * (margin + _textH) / height;
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.UseProgram(_text.Handle);
            _text.Set("uTex", 0);
            int l = _text.Location("uRect");
            if (l >= 0) GL.ProgramUniform4(_text.Handle, l, x0, y0, x1, y1);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _textTex);
            GL.BindVertexArray(_emptyVao);
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        }

        private void UploadOverlay(string title, string detail)
        {
            using var big = new Font("Segoe UI", 26f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var small = new Font("Segoe UI", 17f, FontStyle.Regular, GraphicsUnit.Pixel);
            Size s1, s2;
            using (var probe = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(probe))
            {
                s1 = Size.Ceiling(g.MeasureString(title ?? "", big));
                s2 = Size.Ceiling(g.MeasureString(detail ?? "", small));
            }
            const int pad = 10;
            int w = Math.Max(s1.Width, s2.Width) + 2 * pad, h = s1.Height + s2.Height + 2 * pad;
            using var bmp = new Bitmap(w, h, ImagingFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(150, 0, 0, 0));
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(title ?? "", big, Brushes.White, pad, pad);
                using var grey = new SolidBrush(Color.FromArgb(255, 200, 200, 210));
                g.DrawString(detail ?? "", small, grey, pad + 2, pad + s1.Height);
            }
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, ImagingFormat.Format32bppArgb);
            try
            {
                GL.BindTexture(TextureTarget.Texture2D, _textTex);
                GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, w, h, 0, OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            _textW = w;
            _textH = h;
            _textDrawn = (title, detail);
        }

        private static void BindAttribute(int location, int buffer)
        {
            GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
            GL.EnableVertexAttribArray(location);
            GL.VertexAttribPointer(location, 4, VertexAttribPointerType.Float, false, 16, 0);
        }

        private void DrawSinks(SphSimulation sim, Matrix4x4 view, Matrix4x4 proj, float pointScale)
        {
            var sinks = sim.SinkManager?.Sinks;
            var stellar = sim.Stellar;
            int n = sinks?.Count ?? 0, flashes = stellar?.FlashCount ?? 0, total = n + flashes;
            if (total == 0) return;
            if (_rad.Length < total)
            {
                int cap = total * 2;
                _pos = new float[cap * 3]; _rad = new float[cap]; _mass = new float[cap]; _cls = new float[cap];
            }
            for (int i = 0; i < n; i++)
            {
                Vector3 x = sinks.Position[i];
                _pos[i * 3] = x.X; _pos[i * 3 + 1] = x.Y; _pos[i * 3 + 2] = x.Z;
                _rad[i] = sinks.Radius[i];
                _mass[i] = sinks.Mass[i];
                _cls[i] = (float)sinks.Class[i];
            }
            for (int k = 0; k < flashes; k++)
            {
                int i = n + k;
                Vector3 x = stellar.FlashPosition(k);
                _pos[i * 3] = x.X; _pos[i * 3 + 1] = x.Y; _pos[i * 3 + 2] = x.Z;
                _rad[i] = stellar.FlashBrightness(k);
                _mass[i] = 0f;
                _cls[i] = SimulationSnapshot.FlashClass;
            }
            Stream(_vboPos, _pos, total * 3);
            Stream(_vboRad, _rad, total);
            Stream(_vboMass, _mass, total);
            Stream(_vboClass, _cls, total);

            var p = _points;
            p.SetMatrix("uView", view);
            p.SetMatrix("uProj", proj);
            p.Set("uPointScale", pointScale);
            p.Set("uGrainMass", 1f);
            p.Set("uStarMass", 0f);
            p.Set("uStellar", 1);
            p.Set("uPass", 0);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
            GL.UseProgram(p.Handle);
            GL.BindVertexArray(_pointVao);
            GL.DrawArrays(PrimitiveType.Points, 0, total);
            // Black-hole cores opaque black over the glow.
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            p.Set("uPass", 1);
            GL.DrawArrays(PrimitiveType.Points, 0, n);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        }

        private static void Stream(int vbo, float[] data, int floats)
        {
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            int bytes = floats * sizeof(float);
            GL.BufferData(BufferTarget.ArrayBuffer, bytes, IntPtr.Zero, BufferUsageHint.StreamDraw);
            if (bytes > 0) GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, bytes, data);
        }

        private void EnsureFramebuffer(int width, int height)
        {
            if (width == _fbWidth && height == _fbHeight) return;
            _fbWidth = width;
            _fbHeight = height;
            GL.BindTexture(TextureTarget.Texture2D, _hdrTex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, width, height, 0, OpenTK.Graphics.OpenGL4.PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _hdrTex, 0);
            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            if (status != FramebufferErrorCode.FramebufferComplete)
                throw new InvalidOperationException("HDR framebuffer incomplete: " + status);
        }

        /// <summary>Reads the current back buffer (call after Render, before SwapBuffers) as an opaque bitmap.</summary>
        public Bitmap ReadBackBuffer(int width, int height)
        {
            var bmp = new Bitmap(Math.Max(1, width), Math.Max(1, height), ImagingFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, ImagingFormat.Format32bppArgb);
            try
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                GL.ReadBuffer(ReadBufferMode.Back);
                GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
                GL.ReadPixels(0, 0, bmp.Width, bmp.Height, OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            bmp.RotateFlip(RotateFlipType.RotateNoneFlipY);
            var opaque = new Bitmap(bmp.Width, bmp.Height, ImagingFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(opaque)) g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
            bmp.Dispose();
            return opaque;
        }

        public void Dispose()
        {
            _splat?.Dispose();
            _tonemap?.Dispose();
            _points?.Dispose();
            _text?.Dispose();
            if (_textTex != 0) GL.DeleteTexture(_textTex);
            foreach (int b in new[] { _vboPos, _vboRad, _vboMass, _vboClass }) if (b != 0) GL.DeleteBuffer(b);
            foreach (int v in new[] { _gasVao, _emptyVao, _pointVao }) if (v != 0) GL.DeleteVertexArray(v);
            if (_fbo != 0) GL.DeleteFramebuffer(_fbo);
            if (_hdrTex != 0) GL.DeleteTexture(_hdrTex);
            _splat = null;
        }
    }
}
