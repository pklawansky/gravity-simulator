using System;
using System.Drawing;
using System.Numerics;

namespace GravitySimulator.Classic
{
    /// <summary>
    /// The classic simulator's projection for the OpenGL renderer: the same mapping as
    /// <see cref="Engines.GraphicsEngine.Convert3DTo2DFixedPerspective"/>, split into a matrix and one
    /// scale. The GDI mapping shrinks a point towards the image centre by
    /// s = 1 - (1 - focus) * y / bounds.y, which is linear in depth y, so no 4x4 projection matrix can
    /// reproduce it (a matrix divides by something linear in y). Instead the scene matrix takes a world
    /// point (x right, y into the box, z up) to q = (2x/bx - 1, 2z/bz - 1, ~2y/by - 1, y/by): the
    /// unscaled position in normalised device coordinates, the depth, and the depth ratio. The vertex
    /// shader then emits (q.x, q.y, q.z / s, 1 / s), so after the divide by w the point lands at
    /// (q.x * s, q.y * s) about the viewport centre, exactly as GDI, with a depth linear in y.
    /// The GLSL in <see cref="ClassicGlRenderer"/> mirrors these methods.
    /// </summary>
    public static class ClassicProjection
    {
        /// <summary>Smallest scale used, so w = 1/s stays finite at focus 0 on the back wall.</summary>
        public const float MinScale = 1e-4f;

        /// <summary>
        /// The box's depth range y in [0, bounds.y] maps to NDC depth [-DepthSpan, DepthSpan], just inside
        /// the clip volume, so the back wall (y = bounds.y) is neither clipped nor lost to the cleared depth.
        /// </summary>
        public const float DepthSpan = 0.999f;

        /// <summary>World to (unscaled NDC x, unscaled NDC y, NDC depth, depth ratio), row-vector convention (v * M).</summary>
        public static Matrix4x4 SceneMatrix(Vector3 bounds) => new Matrix4x4(
            2f / bounds.X, 0f, 0f, 0f,
            0f, 0f, 2f * DepthSpan / bounds.Y, 1f / bounds.Y,
            0f, 2f / bounds.Z, 0f, 0f,
            -1f, -1f, -DepthSpan, 0f);

        /// <summary>The GDI scaling factor towards the image centre at depth ratio <paramref name="yRatio"/>.</summary>
        public static float Scale(float yRatio, float focus) => MathF.Max(1f - (1f - focus) * yRatio, MinScale);

        /// <summary>Normalised device coordinates of a world point (x, y in [-1, 1] over the viewport; z the depth).</summary>
        public static Vector3 ToNdc(Vector3 world, Vector3 bounds, float focus)
        {
            Vector4 q = Vector4.Transform(new Vector4(world, 1f), SceneMatrix(bounds));
            float s = Scale(q.W, focus);
            return new Vector3(q.X * s, q.Y * s, q.Z);
        }

        /// <summary>
        /// The largest rectangle of the scene's aspect (bounds.x : bounds.z) centred in a control of the
        /// given size, in top-left pixel coordinates. At the size the scene was built for it is the
        /// whole control, as the GDI renderer's bitmap was.
        /// </summary>
        public static Rectangle FitViewport(int controlWidth, int controlHeight, Vector3 bounds)
        {
            controlWidth = Math.Max(1, controlWidth);
            controlHeight = Math.Max(1, controlHeight);
            float pxPerMetre = MathF.Min(controlWidth / bounds.X, controlHeight / bounds.Z);
            int w = Math.Clamp((int)MathF.Round(bounds.X * pxPerMetre), 1, controlWidth);
            int h = Math.Clamp((int)MathF.Round(bounds.Z * pxPerMetre), 1, controlHeight);
            return new Rectangle((controlWidth - w) / 2, (controlHeight - h) / 2, w, h);
        }

        /// <summary>Where a world point lands, in continuous top-left pixel coordinates of the control.</summary>
        public static PointF ToPixel(Vector3 world, Vector3 bounds, float focus, Rectangle viewport)
        {
            Vector3 ndc = ToNdc(world, bounds, focus);
            return new PointF(
                viewport.X + (ndc.X + 1f) * 0.5f * viewport.Width,
                viewport.Y + (1f - ndc.Y) * 0.5f * viewport.Height);
        }

        /// <summary>On-screen radius (px) of a sphere: its world radius times pixels per metre times the scale at its centre.</summary>
        public static float RadiusPx(float radius, float y, Vector3 bounds, float focus, Rectangle viewport) =>
            radius * (viewport.Width / bounds.X) * Scale(y / bounds.Y, focus);
    }
}
