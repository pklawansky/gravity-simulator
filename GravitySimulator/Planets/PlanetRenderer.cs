using System;
using System.Drawing;
using System.Drawing.Imaging;
using ImagingFormat = System.Drawing.Imaging.PixelFormat;
using System.Numerics;
using OpenTK.Graphics.OpenGL4;

namespace GravitySimulator.Planets
{
    /// <summary>
    /// OpenGL 3.3 core renderer: particle snapshots streamed into VBOs each frame and drawn as
    /// perspective-sized round point sprites, coloured by mass (dust dim, rock warm, planets bright,
    /// star emissive). With the stellar model on, bodies are coloured by class instead: gas dim violet,
    /// stars by mass (red, yellow, blue-white), white dwarfs small white, neutron stars small cyan,
    /// black holes a black disc (the event horizon at a minimum on-screen size) with an orange ring,
    /// and supernova flashes. All methods must be called with the GL context current (UI thread).
    /// </summary>
    public sealed class PlanetRenderer : IDisposable
    {
        private const string VertexSource = @"#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in float aRadius;
layout(location = 2) in float aMass;
layout(location = 3) in float aClass;
uniform mat4 uView;
uniform mat4 uProj;
uniform float uPointScale;   // viewport height * proj[1][1]
uniform float uGrainMass;
uniform float uStarMass;     // bodies at or above this mass are drawn as the star
uniform int uStellar;        // 1 = colour by stellar class
out vec3 vColor;
out float vStar;
flat out int vKind;          // 0 normal, 1 star glow, 2 black hole, 3 flash, 4 gas
void main()
{
    vec4 vp = uView * vec4(aPos, 1.0);
    gl_Position = uProj * vp;
    float depth = max(-vp.z, 1e-3);
    float px = aRadius * uPointScale / depth;
    if (uStellar == 1)
    {
        int k = int(aClass + 0.5);
        vKind = 0;
        vStar = 0.0;
        if (k == 1) {            // gas
            vColor = vec3(0.32, 0.30, 0.62);
            gl_PointSize = clamp(px, 1.5, 48.0);
            vKind = 4;
        } else if (k == 2) {     // star: red -> yellow -> blue-white by mass
            float lm = log(max(aMass, 0.01)) / log(10.0);     // log10 M
            vec3 red = vec3(1.0, 0.28, 0.12), yellow = vec3(1.0, 0.78, 0.30), blue = vec3(0.50, 0.68, 1.0);
            float t = clamp((lm + 1.0) / 1.0, 0.0, 1.0);        // 0.1 -> 1 M_sun
            float u = clamp(lm / 1.3, 0.0, 1.0);                // 1 -> 20 M_sun
            vColor = lm < 0.0 ? mix(red, yellow, t) : mix(yellow, blue, u);
            gl_PointSize = 2.5 + 7.0 * clamp((lm + 1.1) / 2.6, 0.0, 1.0);
            vKind = 1;
        } else if (k == 3) {     // white dwarf
            vColor = vec3(1.0, 1.0, 1.0);
            gl_PointSize = 3.0;
        } else if (k == 4) {     // neutron star
            vColor = vec3(0.35, 1.0, 1.0);
            gl_PointSize = 3.5;
        } else if (k == 5) {     // black hole: event horizon at a minimum on-screen size, plus ring
            vColor = vec3(1.0, 0.45, 0.10);
            gl_PointSize = clamp(px * 3.0, 18.0, 96.0);
            vKind = 2;
        } else if (k == 6) {     // supernova flash; radius carries brightness 0..1
            vColor = vec3(1.0, 0.95, 0.80) * (0.4 + 0.8 * aRadius);
            gl_PointSize = 40.0 + 120.0 * aRadius;
            vKind = 3;
        } else {                 // planet / brown dwarf
            vColor = vec3(0.75, 0.52, 0.35);
            gl_PointSize = clamp(px, 2.0, 48.0);
        }
        return;
    }
    vKind = 0;
    float star = (uStarMass > 0.0 && aMass >= uStarMass) ? 1.0 : 0.0;
    float grains = max(aMass / max(uGrainMass, 1e-30), 1.0);
    float t = clamp(log(grains) / log(500.0), 0.0, 1.0);     // 1 grain -> 0, 500 grains -> 1
    vec3 dust   = vec3(0.30, 0.38, 0.62);
    vec3 rock   = vec3(1.00, 0.55, 0.25);
    vec3 planet = vec3(1.00, 0.97, 0.85);
    vec3 c = t < 0.5 ? mix(dust, rock, t * 2.0) : mix(rock, planet, t * 2.0 - 1.0);
    float minPx = 1.5 + 4.0 * t;                               // keep small bodies visible
    gl_PointSize = clamp(aRadius * uPointScale / depth, minPx, 96.0);
    if (star > 0.5) { c = vec3(1.0, 0.92, 0.6); gl_PointSize = clamp(aRadius * uPointScale / depth, 10.0, 160.0); }
    vColor = c;
    vStar = star;
}";

        private const string FragmentSource = @"#version 330 core
in vec3 vColor;
in float vStar;
flat in int vKind;
uniform int uPass;           // 0 additive glow, 1 alpha-blended black-hole cores
out vec4 fragColor;
void main()
{
    vec2 d = gl_PointCoord * 2.0 - 1.0;
    float r2 = dot(d, d);
    if (r2 > 1.0) discard;
    if (uPass == 1)
    {
        if (vKind != 2 || r2 > 0.30) discard;
        fragColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }
    if (vKind == 2)
    {
        float ring = smoothstep(0.25, 0.45, r2) * (1.0 - smoothstep(0.55, 1.0, r2));
        fragColor = vec4(vColor * 1.6, ring);
        return;
    }
    if (vKind == 3)
    {
        float glow = exp(-4.0 * r2);
        fragColor = vec4(vColor, glow);
        return;
    }
    if (vKind == 4)
    {
        fragColor = vec4(vColor, (1.0 - smoothstep(0.2, 1.0, r2)) * 0.5);
        return;
    }
    float edge = 1.0 - smoothstep(0.6, 1.0, r2);
    float glow = vStar > 0.5 ? 1.4 : 1.0;
    fragColor = vec4(vColor * glow, edge * (vStar > 0.5 ? 1.0 : 0.85));
}";

        private int _program, _vao, _vboPos, _vboRad, _vboMass, _vboClass;
        private int _uView, _uProj, _uPointScale, _uGrainMass, _uStarMass, _uStellar, _uPass;
        private int _count, _drawCount;
        private bool _stellar;
        private long _uploadedVersion = -1;
        private float _grainMass = 1f, _starMass;
        private readonly float[] _mat = new float[16];

        public string GlVersion { get; private set; } = "";

        public void Initialize()
        {
            GlVersion = GL.GetString(StringName.Version) ?? "";
            _program = BuildProgram();
            _uView = GL.GetUniformLocation(_program, "uView");
            _uProj = GL.GetUniformLocation(_program, "uProj");
            _uPointScale = GL.GetUniformLocation(_program, "uPointScale");
            _uGrainMass = GL.GetUniformLocation(_program, "uGrainMass");
            _uStarMass = GL.GetUniformLocation(_program, "uStarMass");
            _uStellar = GL.GetUniformLocation(_program, "uStellar");
            _uPass = GL.GetUniformLocation(_program, "uPass");

            _vao = GL.GenVertexArray();
            GL.BindVertexArray(_vao);
            _vboPos = MakeAttribute(0, 3);
            _vboRad = MakeAttribute(1, 1);
            _vboMass = MakeAttribute(2, 1);
            _vboClass = MakeAttribute(3, 1);
            GL.BindVertexArray(0);

            GL.Enable(EnableCap.ProgramPointSize);
            GL.Disable(EnableCap.DepthTest);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); // additive glow
            GL.ClearColor(0.01f, 0.01f, 0.03f, 1f);
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

        /// <summary>Uploads the snapshot if it is newer than what is already on the GPU.</summary>
        public void Upload(SimulationSnapshot snap)
        {
            if (snap == null || snap.Version == _uploadedVersion) return;
            _count = snap.Count;
            _stellar = snap.Stats.StellarModel;
            _drawCount = _stellar ? _count + snap.FlashCount : _count;
            _grainMass = snap.Stats.GrainMass > 0f ? snap.Stats.GrainMass : 1f;
            _starMass = snap.Stats.StarMass;
            Stream(_vboPos, snap.Positions, _drawCount * 3);
            Stream(_vboRad, snap.Radii, _drawCount);
            Stream(_vboMass, snap.Masses, _drawCount);
            Stream(_vboClass, snap.Classes, _drawCount);
            _uploadedVersion = snap.Version;
        }

        private static void Stream(int vbo, float[] data, int floats)
        {
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            int bytes = floats * sizeof(float);
            GL.BufferData(BufferTarget.ArrayBuffer, bytes, IntPtr.Zero, BufferUsageHint.StreamDraw); // orphan
            if (bytes > 0) GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, bytes, data);
        }

        public void Render(OrbitCamera camera, int width, int height)
        {
            GL.Viewport(0, 0, Math.Max(1, width), Math.Max(1, height));
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            if (_drawCount == 0) return;

            Matrix4x4 proj = camera.Projection(width / (float)Math.Max(1, height));
            GL.UseProgram(_program);
            SetMatrix(_uView, camera.View);
            SetMatrix(_uProj, proj);
            GL.Uniform1(_uPointScale, height * proj.M22);
            GL.Uniform1(_uGrainMass, _grainMass);
            GL.Uniform1(_uStarMass, _starMass);
            GL.Uniform1(_uStellar, _stellar ? 1 : 0);
            GL.Uniform1(_uPass, 0);
            GL.BindVertexArray(_vao);
            GL.DrawArrays(PrimitiveType.Points, 0, _drawCount);
            if (_stellar)
            {
                // Black-hole cores drawn opaque black over the additive glow.
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                GL.Uniform1(_uPass, 1);
                GL.DrawArrays(PrimitiveType.Points, 0, _count);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
            }
            GL.BindVertexArray(0);
        }

        private void SetMatrix(int location, Matrix4x4 m)
        {
            _mat[0] = m.M11; _mat[1] = m.M12; _mat[2] = m.M13; _mat[3] = m.M14;
            _mat[4] = m.M21; _mat[5] = m.M22; _mat[6] = m.M23; _mat[7] = m.M24;
            _mat[8] = m.M31; _mat[9] = m.M32; _mat[10] = m.M33; _mat[11] = m.M34;
            _mat[12] = m.M41; _mat[13] = m.M42; _mat[14] = m.M43; _mat[15] = m.M44;
            GL.UniformMatrix4(location, 1, false, _mat);
        }

        /// <summary>Reads the current back buffer (call after Render, before SwapBuffers).</summary>
        public Bitmap ReadBackBuffer(int width, int height)
        {
            var bmp = new Bitmap(Math.Max(1, width), Math.Max(1, height), ImagingFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, ImagingFormat.Format32bppArgb);
            try
            {
                GL.ReadBuffer(ReadBufferMode.Back);
                GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
                GL.ReadPixels(0, 0, bmp.Width, bmp.Height, OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            bmp.RotateFlip(RotateFlipType.RotateNoneFlipY); // GL origin is bottom-left
            // Make it opaque (alpha from blending is not meaningful in a screenshot).
            return MakeOpaque(bmp);
        }

        private static Bitmap MakeOpaque(Bitmap src)
        {
            var dst = new Bitmap(src.Width, src.Height, ImagingFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(dst)) g.DrawImage(src, 0, 0, src.Width, src.Height);
            src.Dispose();
            return dst;
        }

        private static int BuildProgram()
        {
            int vs = Compile(ShaderType.VertexShader, VertexSource);
            int fs = Compile(ShaderType.FragmentShader, FragmentSource);
            int program = GL.CreateProgram();
            GL.AttachShader(program, vs);
            GL.AttachShader(program, fs);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int ok);
            GL.DeleteShader(vs);
            GL.DeleteShader(fs);
            if (ok == 0) throw new InvalidOperationException("Shader link failed: " + GL.GetProgramInfoLog(program));
            return program;
        }

        private static int Compile(ShaderType type, string source)
        {
            int s = GL.CreateShader(type);
            GL.ShaderSource(s, source);
            GL.CompileShader(s);
            GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException(type + " compile failed: " + GL.GetShaderInfoLog(s));
            return s;
        }

        public void Dispose()
        {
            if (_program == 0) return;
            GL.DeleteBuffer(_vboPos);
            GL.DeleteBuffer(_vboRad);
            GL.DeleteBuffer(_vboMass);
            GL.DeleteBuffer(_vboClass);
            GL.DeleteVertexArray(_vao);
            GL.DeleteProgram(_program);
            _program = 0;
        }
    }
}
