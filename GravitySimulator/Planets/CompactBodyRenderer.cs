using System;
using System.Numerics;
using OpenTK.Graphics.OpenGL4;

namespace GravitySimulator.Planets
{
    /// <summary>
    /// Stars, white dwarfs, neutron stars and black holes as real 3D spheres at their world-space radius
    /// (the stellar model's radius rules: stars R☉ M^0.8, black holes r_s = 2GM/c², ...): ray-cast sphere
    /// impostors on camera-facing quads, perspective-correct, with depth written. Stars are emissive
    /// with limb darkening; a black hole is an opaque black disc (the event horizon) with a soft orange
    /// glow around its shadow (2.6 r_s), both scaling with the hole. When the true sphere is smaller
    /// than a few pixels a small "you are here" marker (the old class colours and sizes) is drawn
    /// instead; it fades out as the sphere's projected radius grows past it, so up close only the real
    /// sphere shows. Used by Planet formation's cosmic presets and the SPH mode (GLSL 3.3).
    /// </summary>
    public sealed class CompactBodyRenderer : IDisposable
    {
        internal const string VertexSource = @"#version 330 core
layout(location = 0) in vec4 iPosR;      // per instance: world xyz, world radius
layout(location = 1) in vec2 iMassClass; // per instance: mass, BodyClass
uniform mat4 uView;
uniform mat4 uProj;
uniform float uHalfHeightPx;             // 0.5 * viewport height * proj[1][1]
out vec3 vViewPos;
out vec2 vPx;                            // pixel offset from the centre on the quad
flat out vec3 vCenter;
flat out float vR;
flat out float vPr;                      // projected radius of the sphere, px
flat out float vMarker;                  // marker radius, px
flat out vec3 vColor;
flat out int vKind;
float markerPx(int k, float mass)
{
    if (k == 2) return 0.5 * (2.5 + 7.0 * clamp((log(max(mass, 0.01)) / log(10.0) + 1.1) / 2.6, 0.0, 1.0));
    if (k == 3) return 1.5;
    if (k == 4) return 1.75;
    return 9.0;
}
void main()
{
    vec2 c = vec2(gl_VertexID & 1, (gl_VertexID >> 1) & 1) * 2.0 - 1.0;
    int k = int(iMassClass.y + 0.5);
    float mass = iMassClass.x;
    vec3 center = (uView * vec4(iPosR.xyz, 1.0)).xyz;
    float R = max(iPosR.w, 0.0);
    float depth = -center.z;
    vKind = k;
    vCenter = center;
    vR = R;
    if (k == 2) {
        float lm = log(max(mass, 0.01)) / log(10.0);
        vec3 red = vec3(1.0, 0.28, 0.12), yellow = vec3(1.0, 0.78, 0.30), blue = vec3(0.50, 0.68, 1.0);
        vColor = lm < 0.0 ? mix(red, yellow, clamp(lm + 1.0, 0.0, 1.0)) : mix(yellow, blue, clamp(lm / 1.3, 0.0, 1.0));
    } else if (k == 3) vColor = vec3(1.0);
    else if (k == 4) vColor = vec3(0.35, 1.0, 1.0);
    else vColor = vec3(1.0, 0.45, 0.10);
    float d2 = dot(center, center);
    if (depth <= 0.0 || d2 <= R * R) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); vPr = 0.0; vMarker = 0.0; vViewPos = vec3(0.0); vPx = vec2(0.0); return; }
    float sil = R / sqrt(max(1.0 - R * R / d2, 1e-6));   // silhouette radius in the centre's plane
    vPr = uHalfHeightPx * sil / depth;
    vMarker = markerPx(k, mass);
    float halo = k == 5 ? 3.2 : 1.6;                      // glow extent in sphere radii
    float quadPx = max(vPr * halo, vMarker) + 1.5;
    float q = quadPx * depth / uHalfHeightPx;
    vec3 p = center + vec3(c * q, 0.0);
    vViewPos = p;
    vPx = c * quadPx;
    gl_Position = uProj * vec4(p, 1.0);
}";

        internal const string FragmentSource = @"#version 330 core
in vec3 vViewPos;
in vec2 vPx;
flat in vec3 vCenter;
flat in float vR;
flat in float vPr;
flat in float vMarker;
flat in vec3 vColor;
flat in int vKind;
uniform mat4 uProj;
out vec4 fragColor;                      // premultiplied alpha (blend One, OneMinusSrcAlpha)
void main()
{
    float rpx = length(vPx);
    vec3 dir = normalize(vViewPos);
    float b = dot(dir, vCenter);
    float disc = b * b - (dot(vCenter, vCenter) - vR * vR);
    // The marker fades out while the true sphere grows from half the marker radius to the marker radius.
    float fade = 1.0 - smoothstep(0.5 * vMarker, vMarker, vPr);
    float inMarker = 1.0 - smoothstep(vMarker - 0.75, vMarker + 0.75, rpx);
    if (disc >= 0.0 && b > 0.0)
    {
        vec3 hit = dir * (b - sqrt(disc));
        vec3 n = (hit - vCenter) / vR;
        float mu = clamp(dot(n, -dir), 0.0, 1.0);
        vec4 clip = uProj * vec4(hit, 1.0);
        gl_FragDepth = clamp(clip.z / clip.w * 0.5 + 0.5, 0.0, 1.0);
        if (vKind == 5) { fragColor = vec4(0.0, 0.0, 0.0, 1.0); return; }  // event horizon
        float limb = 1.0 - 0.6 * (1.0 - mu);                                   // linear limb darkening
        vec3 c = vColor * limb * (vKind == 2 ? 1.25 : 1.0);
        // Blend toward the marker colour while the sphere is still about marker-sized (no pop).
        fragColor = vec4(mix(c, vColor, fade * 0.5), 1.0);
        return;
    }
    gl_FragDepth = 0.99999;                    // glow and markers: behind any sphere, over the gas
    float s = rpx / max(vPr, 1e-6);            // distance from the centre in sphere radii (on screen)
    vec3 glow;
    // Glows fall to zero before the quad edge (3.2 R for holes, 1.6 R for stars) so no square shows.
    if (vKind == 5) glow = vColor * 0.7 * exp(-pow((s - 2.6) / 0.25, 2.0)) * (1.0 - smoothstep(2.9, 3.15, s));
    else glow = vColor * 0.6 * exp(-6.0 * (s - 1.0)) * step(1.0, s) * (1.0 - smoothstep(1.35, 1.58, s));
    glow *= 1.0 - fade;                        // world-scaled glow only once the sphere is resolved
    vec4 marker;
    if (vKind == 5) {                          // small black disc with an orange ring, as before
        float r = rpx / max(vMarker, 1e-3);
        float ring = smoothstep(0.50, 0.67, r) * (1.0 - smoothstep(0.74, 1.0, r));
        float core = 1.0 - smoothstep(0.50, 0.55, r);
        marker = vec4(vColor * 1.6 * ring, core) * fade;
    } else {
        float e = 1.0 - smoothstep(0.6, 1.0, rpx / max(vMarker, 1e-3));
        marker = vec4(vColor * (vKind == 2 ? 1.4 : 1.0) * e, 0.0) * fade;
    }
    vec4 col = vec4(glow, 0.0) + marker * (vKind == 5 ? 1.0 : inMarker);
    if (col.a <= 0.0 && dot(col.rgb, vec3(1.0)) < 1e-3) discard;
    fragColor = col;
}";

        private int _program, _vao, _vbo;
        private int _uView, _uProj, _uHalfHeight;
        private float[] _data = new float[8 * 64];
        private int _count;
        private readonly float[] _mat = new float[16];

        /// <summary>Marker radius (px) of a sub-pixel body, as the shader computes it.</summary>
        public static float MarkerPx(BodyClass cls, float mass) => cls switch
        {
            BodyClass.Star => 0.5f * (2.5f + 7f * Math.Clamp((MathF.Log10(MathF.Max(mass, 0.01f)) + 1.1f) / 2.6f, 0f, 1f)),
            BodyClass.WhiteDwarf => 1.5f,
            BodyClass.NeutronStar => 1.75f,
            _ => 9f,
        };

        /// <summary>
        /// On-screen radius (px) of a sphere of world radius <paramref name="radius"/> whose centre is
        /// <paramref name="distance"/> from the eye, for a viewport <paramref name="heightPx"/> high and a
        /// projection with proj[1][1] = <paramref name="projM22"/> (the shader's silhouette maths, with the
        /// centre on the view axis).
        /// </summary>
        public static float ProjectedRadiusPx(float radius, float distance, float heightPx, float projM22)
        {
            if (!(distance > radius)) return float.PositiveInfinity;
            float sil = radius / MathF.Sqrt(1f - radius * radius / (distance * distance));
            return 0.5f * heightPx * projM22 * sil / distance;
        }

        /// <summary>Opacity of the marker for a sphere of projected radius <paramref name="projectedPx"/>: 1 when sub-marker, 0 once larger.</summary>
        public static float MarkerOpacity(float projectedPx, float markerPx)
        {
            float t = Math.Clamp((projectedPx - 0.5f * markerPx) / (0.5f * markerPx), 0f, 1f);
            return 1f - t * t * (3f - 2f * t);
        }

        /// <summary>On-screen radius actually covered: the true sphere, or the marker floor below it.</summary>
        public static float DrawnRadiusPx(float projectedPx, float markerPx) =>
            MarkerOpacity(projectedPx, markerPx) > 0f ? MathF.Max(projectedPx, markerPx) : projectedPx;

        public static bool IsCompact(float cls) => cls >= 1.5f && cls <= 5.5f;

        public void Initialize()
        {
            int vs = Compile(ShaderType.VertexShader, VertexSource);
            int fs = Compile(ShaderType.FragmentShader, FragmentSource);
            _program = GL.CreateProgram();
            GL.AttachShader(_program, vs);
            GL.AttachShader(_program, fs);
            GL.LinkProgram(_program);
            GL.GetProgram(_program, GetProgramParameterName.LinkStatus, out int ok);
            GL.DeleteShader(vs);
            GL.DeleteShader(fs);
            if (ok == 0) throw new InvalidOperationException("Compact-body shader link failed: " + GL.GetProgramInfoLog(_program));
            _uView = GL.GetUniformLocation(_program, "uView");
            _uProj = GL.GetUniformLocation(_program, "uProj");
            _uHalfHeight = GL.GetUniformLocation(_program, "uHalfHeightPx");
            _vao = GL.GenVertexArray();
            _vbo = GL.GenBuffer();
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, IntPtr.Zero, IntPtr.Zero, BufferUsageHint.StreamDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 4, VertexAttribPointerType.Float, false, 8 * sizeof(float), 0);
            GL.VertexAttribDivisor(0, 1);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 8 * sizeof(float), 4 * sizeof(float));
            GL.VertexAttribDivisor(1, 1);
            GL.BindVertexArray(0);
        }

        /// <summary>Takes the compact bodies (classes Star..BlackHole) out of the given body arrays.</summary>
        public void Upload(float[] positions, float[] radii, float[] masses, float[] classes, int count)
        {
            int n = 0;
            for (int i = 0; i < count; i++) if (IsCompact(classes[i])) n++;
            if (_data.Length < n * 8) _data = new float[n * 8 * 2];
            int o = 0;
            for (int i = 0; i < count; i++)
            {
                if (!IsCompact(classes[i])) continue;
                _data[o] = positions[i * 3]; _data[o + 1] = positions[i * 3 + 1]; _data[o + 2] = positions[i * 3 + 2];
                _data[o + 3] = radii[i]; _data[o + 4] = masses[i]; _data[o + 5] = classes[i];
                o += 8;
            }
            _count = n;
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, n * 8 * sizeof(float), IntPtr.Zero, BufferUsageHint.StreamDraw);
            if (n > 0) GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, n * 8 * sizeof(float), _data);
        }

        /// <summary>Draws the uploaded bodies with depth test and write (clears the depth buffer first).</summary>
        public void Draw(Matrix4x4 view, Matrix4x4 proj, int heightPx)
        {
            if (_count == 0) return;
            GL.Clear(ClearBufferMask.DepthBufferBit);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.DepthMask(true);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
            GL.UseProgram(_program);
            SetMatrix(_uView, view);
            SetMatrix(_uProj, proj);
            GL.Uniform1(_uHalfHeight, 0.5f * heightPx * proj.M22);
            GL.BindVertexArray(_vao);
            GL.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, _count);
            GL.BindVertexArray(0);
            GL.Disable(EnableCap.DepthTest);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        }

        private void SetMatrix(int location, Matrix4x4 m)
        {
            _mat[0] = m.M11; _mat[1] = m.M12; _mat[2] = m.M13; _mat[3] = m.M14;
            _mat[4] = m.M21; _mat[5] = m.M22; _mat[6] = m.M23; _mat[7] = m.M24;
            _mat[8] = m.M31; _mat[9] = m.M32; _mat[10] = m.M33; _mat[11] = m.M34;
            _mat[12] = m.M41; _mat[13] = m.M42; _mat[14] = m.M43; _mat[15] = m.M44;
            GL.UniformMatrix4(location, 1, false, _mat);
        }

        private static int Compile(ShaderType type, string source)
        {
            int s = GL.CreateShader(type);
            GL.ShaderSource(s, source);
            GL.CompileShader(s);
            GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException("Compact-body " + type + " compile failed: " + GL.GetShaderInfoLog(s));
            return s;
        }

        public void Dispose()
        {
            if (_program == 0) return;
            GL.DeleteBuffer(_vbo);
            GL.DeleteVertexArray(_vao);
            GL.DeleteProgram(_program);
            _program = 0;
        }
    }
}
