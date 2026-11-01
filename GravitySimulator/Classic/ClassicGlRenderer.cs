using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using GravitySimulator.Models;
using GravitySimulator.Sph;
using OpenTK.Graphics.OpenGL4;
using Plane = GravitySimulator.Models.Plane;

namespace GravitySimulator.Classic
{
    /// <summary>One sphere of a classic frame. <see cref="Flash"/> draws the red collision dot over it.</summary>
    public struct ClassicSphere
    {
        public Vector3 Position;
        public float Radius;
        public bool Flash;
    }

    /// <summary>A point light of the classic scene, with the GDI renderer's lighting parameters.</summary>
    public struct ClassicLight
    {
        public Vector3 Position;
        public float Intensity;
        public float Ambience;
    }

    /// <summary>Everything one classic frame draws, captured on the simulation tick.</summary>
    public sealed class ClassicFrame
    {
        public Vector3 Bounds;
        public float Focus = 0.5f;
        public float LightMarkerRadius;
        public readonly List<ClassicLight> Lights = new List<ClassicLight>();
        public readonly List<ClassicSphere> Spheres = new List<ClassicSphere>();
    }

    /// <summary>
    /// OpenGL renderer of the classic simulator (GLSL 3.3): the box walls as lit triangles (the same
    /// facets as the GDI renderer), the spheres as per-pixel lit ray-cast impostors with exact depth,
    /// and the lights as small translucent white markers. Lighting is the GDI facet formula evaluated
    /// per pixel: for each light, base * ((pi/2 * ambience) - angle) / (pi/2 * ambience) * intensity / d^2,
    /// clamped per light and summed. The projection is <see cref="ClassicProjection"/>'s, so the
    /// focus control behaves as before; the depth buffer replaces painter's sorting.
    /// </summary>
    public sealed class ClassicGlRenderer : IDisposable
    {
        public const int MaxLights = 8;

        /// <summary>Radius (px) of the red collision dot: the GDI renderer drew it at the sphere radius in metres taken as pixels, i.e. a dot of a pixel or two.</summary>
        public const float FlashRadiusPx = 2f;

        public static readonly Vector3 WallColour = new Vector3(173, 216, 230) / 255f;   // Color.LightBlue
        public static readonly Vector3 SphereColour = new Vector3(144, 238, 144) / 255f; // Color.LightGreen
        public static readonly Vector4 MarkerColour = new Vector4(1f, 1f, 1f, 0.83f);     // two layers of alpha 150 white facets

        // Shared by both vertex shaders: ClassicProjection's matrix and scale.
        private const string ProjectionGlsl = @"
uniform mat4 uScene;
uniform float uFocus;
vec4 sceneQ(vec3 p) { return uScene * vec4(p, 1.0); }
float sceneScale(float yRatio) { return max(1.0 - (1.0 - uFocus) * yRatio, 1e-4); }
";

        // Shared by both fragment shaders: the GDI facet lighting, per pixel.
        private const string LightingGlsl = @"
uniform int uLightCount;
uniform vec4 uLights[8];      // xyz position, w intensity
uniform float uAmbience[8];
vec3 shade(vec3 base, vec3 n, vec3 p)
{
    vec3 total = vec3(0.0);
    for (int i = 0; i < uLightCount; i++)
    {
        vec3 l = uLights[i].xyz - p;
        float d = max(length(l), 1e-4);
        float theta = acos(clamp(dot(n, l) / (length(n) * d), -1.0, 1.0));
        float a = 1.5707963 * uAmbience[i];
        total += clamp(base * ((a - theta) / a) * (uLights[i].w / (d * d)), 0.0, 1.0);
    }
    return clamp(total, 0.0, 1.0);
}
";

        internal const string WallVertexSource = "#version 330 core\n" + ProjectionGlsl + @"
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
out vec3 vWorld;
flat out vec3 vNormal;
void main()
{
    vec4 q = sceneQ(aPos);
    float s = sceneScale(q.w);
    vWorld = aPos;
    vNormal = aNormal;
    gl_Position = vec4(q.x, q.y, q.z / s, 1.0 / s);   // after the divide: (q.xy * s, q.z)
}";

        internal const string WallFragmentSource = "#version 330 core\n" + LightingGlsl + @"
uniform vec3 uBase;
in vec3 vWorld;
flat in vec3 vNormal;
out vec4 fragColor;
void main() { fragColor = vec4(shade(uBase, vNormal, vWorld), 1.0); }";

        internal const string BallVertexSource = "#version 330 core\n" + ProjectionGlsl + @"
layout(location = 0) in vec4 iPosR;   // per instance: world xyz, world radius
layout(location = 1) in float iKind;  // 0 sphere, 1 sphere with collision flash, 2 light marker
uniform vec2 uViewportPx;
uniform float uPxPerMetre;
out vec2 vOff;                        // pixel offset from the projected centre
flat out vec3 vCenter;
flat out float vR;
flat out float vRpx;
flat out float vKind;
void main()
{
    vec2 c = vec2(gl_VertexID & 1, (gl_VertexID >> 1) & 1) * 2.0 - 1.0;
    vec4 q = sceneQ(iPosR.xyz);
    float s = sceneScale(q.w);
    vRpx = max(iPosR.w * uPxPerMetre * s, 1e-3);
    vCenter = iPosR.xyz;
    vR = iPosR.w;
    vKind = iKind;
    float quad = vRpx + 1.0;
    vOff = c * quad;
    gl_Position = vec4(q.xy * s + vOff * 2.0 / uViewportPx, q.z, 1.0);
}";

        internal const string BallFragmentSource = "#version 330 core\n" + LightingGlsl + @"
uniform vec3 uBase;
uniform vec4 uMarker;
uniform float uBoundsY;
uniform float uDepthSpan;
uniform float uFlashPx;
in vec2 vOff;
flat in vec3 vCenter;
flat in float vR;
flat in float vRpx;
flat in float vKind;
out vec4 fragColor;
void main()
{
    vec2 d = vOff / vRpx;
    float dd = dot(d, d);
    if (dd > 1.0) discard;
    // Screen right is world +x, screen up is world +z, towards the viewer is world -y.
    vec3 n = vec3(d.x, -sqrt(1.0 - dd), d.y);
    vec3 hit = vCenter + vR * n;
    gl_FragDepth = clamp(0.5 + 0.5 * uDepthSpan * (2.0 * hit.y / uBoundsY - 1.0), 0.0, 1.0);   // as the scene matrix
    if (vKind > 1.5) { fragColor = uMarker; return; }
    vec3 col = shade(uBase, n, hit);
    if (vKind > 0.5 && length(vOff) <= uFlashPx) col = vec3(1.0, 0.0, 0.0);
    fragColor = vec4(col, 1.0);
}";

        private GlProgram _walls, _balls;
        private int _wallVao, _wallVbo, _wallVertexCount;
        private int _ballVao, _ballVbo;
        private float[] _instances = new float[5 * 64];
        private readonly float[] _mat = new float[16];
        private readonly float[] _lights = new float[4 * MaxLights];
        private readonly float[] _ambience = new float[MaxLights];

        public string GlVersion { get; private set; } = "";

        /// <summary>Builds the programs and buffers. Throws InvalidOperationException with the driver log on a shader error.</summary>
        public void Initialize()
        {
            GlVersion = GL.GetString(StringName.Version) ?? "";
            _walls = GlProgram.FromSource("classic walls", WallVertexSource, WallFragmentSource);
            _balls = GlProgram.FromSource("classic spheres", BallVertexSource, BallFragmentSource);

            _wallVao = GL.GenVertexArray();
            _wallVbo = GL.GenBuffer();
            GL.BindVertexArray(_wallVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _wallVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, IntPtr.Zero, IntPtr.Zero, BufferUsageHint.StaticDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));

            _ballVao = GL.GenVertexArray();
            _ballVbo = GL.GenBuffer();
            GL.BindVertexArray(_ballVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _ballVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, IntPtr.Zero, IntPtr.Zero, BufferUsageHint.StreamDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribDivisor(0, 1);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribDivisor(1, 1);
            GL.BindVertexArray(0);
        }

        /// <summary>Uploads the wall facets once (the GDI renderer's boundary planes, normals facing into the box).</summary>
        public void SetWalls(IEnumerable<Plane> planes)
        {
            var data = new List<float>();
            foreach (var plane in planes)
            {
                foreach (var f in plane.Facets)
                {
                    var n = new Vector3(f.Normal.x, f.Normal.y, f.Normal.z);
                    if (f.InvertNormal) n = -n;
                    n = Vector3.Normalize(n);
                    foreach (var p in new[] { f.P1, f.P2, f.P3 })
                        data.AddRange(new[] { p.x, p.y, p.z, n.X, n.Y, n.Z });
                }
            }
            _wallVertexCount = data.Count / 6;
            GL.BindBuffer(BufferTarget.ArrayBuffer, _wallVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, data.Count * sizeof(float), data.ToArray(), BufferUsageHint.StaticDraw);
        }

        /// <summary>Draws a frame into the bound framebuffer, which is <paramref name="width"/> x <paramref name="height"/> pixels.</summary>
        public void Render(ClassicFrame frame, int width, int height)
        {
            GL.Viewport(0, 0, Math.Max(1, width), Math.Max(1, height));
            GL.ClearColor(0f, 0f, 0f, 1f);
            GL.ClearDepth(1.0);
            GL.DepthMask(true);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            Rectangle vp = ClassicProjection.FitViewport(width, height, frame.Bounds);
            GL.Viewport(vp.X, Math.Max(1, height) - vp.Y - vp.Height, vp.Width, vp.Height);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.Blend);

            int lightCount = Math.Min(frame.Lights.Count, MaxLights);
            for (int i = 0; i < lightCount; i++)
            {
                var l = frame.Lights[i];
                _lights[i * 4] = l.Position.X; _lights[i * 4 + 1] = l.Position.Y; _lights[i * 4 + 2] = l.Position.Z; _lights[i * 4 + 3] = l.Intensity;
                _ambience[i] = l.Ambience;
            }
            Matrix4x4 scene = ClassicProjection.SceneMatrix(frame.Bounds);

            if (_wallVertexCount > 0)
            {
                Use(_walls, scene, frame.Focus, lightCount);
                GL.Uniform3(_walls.Location("uBase"), WallColour.X, WallColour.Y, WallColour.Z);
                GL.BindVertexArray(_wallVao);
                GL.DrawArrays(PrimitiveType.Triangles, 0, _wallVertexCount);
            }

            // Spheres (opaque, depth written), then the light markers (translucent, depth tested only).
            int spheres = frame.Spheres.Count;
            int markers = frame.LightMarkerRadius > 0f ? frame.Lights.Count : 0;
            int total = spheres + markers;
            if (total > 0)
            {
                if (_instances.Length < total * 5) _instances = new float[total * 10];
                int o = 0;
                foreach (var s in frame.Spheres)
                {
                    _instances[o] = s.Position.X; _instances[o + 1] = s.Position.Y; _instances[o + 2] = s.Position.Z;
                    _instances[o + 3] = s.Radius; _instances[o + 4] = s.Flash ? 1f : 0f;
                    o += 5;
                }
                for (int i = 0; i < markers; i++)
                {
                    var l = frame.Lights[i];
                    _instances[o] = l.Position.X; _instances[o + 1] = l.Position.Y; _instances[o + 2] = l.Position.Z;
                    _instances[o + 3] = frame.LightMarkerRadius; _instances[o + 4] = 2f;
                    o += 5;
                }
                GL.BindBuffer(BufferTarget.ArrayBuffer, _ballVbo);
                GL.BufferData(BufferTarget.ArrayBuffer, total * 5 * sizeof(float), _instances, BufferUsageHint.StreamDraw);

                Use(_balls, scene, frame.Focus, lightCount);
                GL.Uniform3(_balls.Location("uBase"), SphereColour.X, SphereColour.Y, SphereColour.Z);
                GL.Uniform4(_balls.Location("uMarker"), MarkerColour.X, MarkerColour.Y, MarkerColour.Z, MarkerColour.W);
                GL.Uniform1(_balls.Location("uBoundsY"), frame.Bounds.Y);
                GL.Uniform1(_balls.Location("uDepthSpan"), ClassicProjection.DepthSpan);
                GL.Uniform1(_balls.Location("uFlashPx"), FlashRadiusPx);
                GL.Uniform2(_balls.Location("uViewportPx"), (float)vp.Width, (float)vp.Height);
                GL.Uniform1(_balls.Location("uPxPerMetre"), vp.Width / frame.Bounds.X);
                GL.BindVertexArray(_ballVao);

                if (spheres > 0)
                {
                    PointInstances(0);
                    GL.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, spheres);
                }
                if (markers > 0)
                {
                    GL.Enable(EnableCap.Blend);
                    GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    GL.DepthMask(false);
                    PointInstances(spheres);
                    GL.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, markers);
                    GL.DepthMask(true);
                    GL.Disable(EnableCap.Blend);
                }
            }
            GL.BindVertexArray(0);
            GL.Disable(EnableCap.DepthTest);
        }

        private static void PointInstances(int first)
        {
            int stride = 5 * sizeof(float);
            GL.VertexAttribPointer(0, 4, VertexAttribPointerType.Float, false, stride, first * stride);
            GL.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, stride, first * stride + 4 * sizeof(float));
        }

        private void Use(GlProgram p, Matrix4x4 m, float focus, int lightCount)
        {
            GL.UseProgram(p.Handle);
            _mat[0] = m.M11; _mat[1] = m.M12; _mat[2] = m.M13; _mat[3] = m.M14;
            _mat[4] = m.M21; _mat[5] = m.M22; _mat[6] = m.M23; _mat[7] = m.M24;
            _mat[8] = m.M31; _mat[9] = m.M32; _mat[10] = m.M33; _mat[11] = m.M34;
            _mat[12] = m.M41; _mat[13] = m.M42; _mat[14] = m.M43; _mat[15] = m.M44;
            GL.UniformMatrix4(p.Location("uScene"), 1, false, _mat);
            GL.Uniform1(p.Location("uFocus"), focus);
            GL.Uniform1(p.Location("uLightCount"), lightCount);
            if (lightCount > 0)
            {
                GL.Uniform4(p.Location("uLights"), lightCount, _lights);
                GL.Uniform1(p.Location("uAmbience"), lightCount, _ambience);
            }
        }

        public void Dispose()
        {
            if (_walls == null) return;
            GL.DeleteBuffer(_wallVbo);
            GL.DeleteVertexArray(_wallVao);
            GL.DeleteBuffer(_ballVbo);
            GL.DeleteVertexArray(_ballVao);
            _walls.Dispose();
            _balls?.Dispose();
            _walls = null;
            _balls = null;
        }
    }
}
