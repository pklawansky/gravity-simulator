using System;
using System.Numerics;

namespace GravitySimulator.Planets
{
    /// <summary>
    /// Orbiting camera around the origin with +Z up. Yaw/pitch in radians, distance in world units.
    /// Matrices use System.Numerics (row-vector) convention, which has the same memory layout GL
    /// expects for column-vector shaders, so they can be uploaded untransposed.
    /// </summary>
    public sealed class OrbitCamera
    {
        public float Yaw { get; set; } = -MathF.PI / 2f;
        public float Pitch { get; set; } = 0.9f; // radians above the XY plane
        public float Distance { get; set; } = 5f;
        public float FieldOfView { get; set; } = MathF.PI / 4f;
        public float MinDistance { get; set; } = 0.2f;
        public float MaxDistance { get; set; } = 200f;

        public Vector3 Eye
        {
            get
            {
                float cp = MathF.Cos(Pitch);
                return new Vector3(cp * MathF.Cos(Yaw), cp * MathF.Sin(Yaw), MathF.Sin(Pitch)) * Distance;
            }
        }

        /// <summary>Rotate by a mouse drag of (dx, dy) pixels.</summary>
        public void Rotate(float dxPixels, float dyPixels)
        {
            Yaw -= dxPixels * 0.01f;
            Pitch = Math.Clamp(Pitch + dyPixels * 0.01f, -1.55f, 1.55f);
        }

        /// <summary>Zoom by mouse wheel notches (positive = closer).</summary>
        public void Zoom(float notches)
        {
            Distance = Math.Clamp(Distance * MathF.Pow(0.9f, notches), MinDistance, MaxDistance);
        }

        public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, Vector3.Zero, Vector3.UnitZ);

        public Matrix4x4 Projection(float aspect) =>
            Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, MathF.Max(aspect, 1e-3f), 0.01f, 1000f);
    }
}
