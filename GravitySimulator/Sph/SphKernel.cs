using System;
using System.Numerics;

namespace GravitySimulator.Sph
{
    /// <summary>
    /// Wendland C2 kernel in 3D with support radius 2h (stable against the pairing instability):
    /// W(r, h) = 21/(16π h³) (1 - q/2)^4 (2q + 1), q = r/h &lt; 2. Mirrored in Shaders/common.glsl.
    /// </summary>
    public static class SphKernel
    {
        public const float Sigma = 21f / (16f * MathF.PI);

        /// <summary>Default η in h = η (m/ρ)^(1/3) (see <see cref="SphParameters.Eta"/>).</summary>
        public const float DefaultEta = 1.5f;

        /// <summary>
        /// Fraction ε of the self-contribution m_i W(0, h_i) removed from the density estimate. Wendland
        /// kernels overestimate density by a roughly constant fraction of the self term (Dehnen &amp; Aly
        /// 2012, MNRAS 425, 1068, eq. 18: ε = 0.0294 (N_H/100)^-0.977 for C2, N_H = 4π/3 (2η)³
        /// neighbours); at η = 1.2 this is ε ≈ 0.050.
        /// </summary>
        public static float DensitySelfCorrection(float eta)
        {
            float support = 2f * eta;
            float nh = 4f / 3f * MathF.PI * support * support * support;
            return 0.0294f * MathF.Pow(nh / 100f, -0.977f);
        }

        public static float W(float r, float h)
        {
            float q = r / h;
            if (q >= 2f) return 0f;
            float t = 1f - 0.5f * q;
            float t2 = t * t;
            return Sigma / (h * h * h) * t2 * t2 * (2f * q + 1f);
        }

        /// <summary>dW/dr = -5 σ q (1 - q/2)³ / h⁴.</summary>
        public static float DWdr(float r, float h)
        {
            float q = r / h;
            if (q >= 2f) return 0f;
            float t = 1f - 0.5f * q;
            float h2 = h * h;
            return -5f * Sigma * q * t * t * t / (h2 * h2);
        }

        /// <summary>∇_i W(|x_ij|, h) for x_ij = x_i - x_j with |x_ij| = r (zero at r = 0 and beyond 2h).</summary>
        public static Vector3 GradW(Vector3 xij, float r, float h)
        {
            float q = r / h;
            if (q >= 2f) return Vector3.Zero;
            float t = 1f - 0.5f * q;
            float h2 = h * h;
            // dW/dr / r, written without the division by r.
            return xij * (-5f * Sigma * t * t * t / (h2 * h2 * h));
        }
    }
}
