using System;
using System.Numerics;

namespace GravitySimulator.Sph
{
    /// <summary>
    /// Framing maths of the follow camera (pure; no GL). From the gas positions and the black hole it
    /// picks a view centre and a camera distance:
    /// <list type="bullet">
    /// <item>centre = per-axis median of the gas (robust to outliers), shifted halfway toward the hole as
    /// the hole comes within a few view radii (w = 1 - smoothstep(4E, 12E, d)), so the hole enters the
    /// view as the star approaches instead of the star being a dot next to a distant hole;</item>
    /// <item>half-view E = max(1.3 x the 95th-percentile gas distance from the centre, the hole's distance
    /// from the centre (weighted by w) plus a margin, <see cref="Settings.MinHalfView"/>), so a compact
    /// star is shown as a ball (MinHalfView) and a stream is fully in view without a few far particles
    /// zooming it out;</item>
    /// <item>distance = E / tan(fov/2).</item>
    /// </list>
    /// At most <see cref="MaxSamples"/> particles are used (a fixed stride), so it is cheap per frame and
    /// deterministic. <see cref="Damp"/> smooths the result exponentially (zoom-out quicker than zoom-in).
    /// </summary>
    public static class FollowFraming
    {
        public const int MaxSamples = 8192;
        /// <summary>Half-view as a multiple of the 95th-percentile gas distance (room for the outer 5%).</summary>
        public const float Margin = 1.3f;

        public sealed class Settings
        {
            /// <summary>Smallest half-height of the view (world units): a compact star is framed at this size.</summary>
            public float MinHalfView { get; init; } = 1f;
            public float FieldOfView { get; init; } = MathF.PI / 4f;
            /// <summary>Margin added around the hole (world units), e.g. a few Schwarzschild radii.</summary>
            public float HoleMargin { get; init; }
            /// <summary>Damping time constants (s): centre, distance growing, distance shrinking.</summary>
            public float CentreTau { get; init; } = 0.5f;
            public float ZoomOutTau { get; init; } = 0.35f;
            public float ZoomInTau { get; init; } = 1.2f;
        }

        public readonly record struct Frame(Vector3 Centre, float Distance);

        [ThreadStatic] private static float[] _x, _y, _z, _d;

        /// <summary>Target frame for <paramref name="count"/> particles in <paramref name="posH"/> (x, y, z, h per particle).</summary>
        public static Frame Compute(ReadOnlySpan<float> posH, int count, Vector3? hole, Settings s)
        {
            count = Math.Min(count, posH.Length / 4);
            float tanHalf = MathF.Tan(0.5f * s.FieldOfView);
            if (count <= 0) return new Frame(hole ?? Vector3.Zero, s.MinHalfView / tanHalf);
            int stride = Math.Max(1, (count + MaxSamples - 1) / MaxSamples);
            int n = (count + stride - 1) / stride;
            if (_x == null || _x.Length < n) { _x = new float[n]; _y = new float[n]; _z = new float[n]; _d = new float[n]; }
            for (int k = 0, i = 0; k < n; k++, i += stride) { _x[k] = posH[i * 4]; _y[k] = posH[i * 4 + 1]; _z[k] = posH[i * 4 + 2]; }

            var gas = new Vector3(Median(_x, n), Median(_y, n), Median(_z, n));
            for (int k = 0, i = 0; k < n; k++, i += stride) { _x[k] = posH[i * 4]; _y[k] = posH[i * 4 + 1]; _z[k] = posH[i * 4 + 2]; }
            float eGas = MathF.Max(Margin * Percentile95(gas, n), s.MinHalfView);
            Vector3 h = hole ?? gas;
            float w = hole.HasValue ? 1f - SmoothStep(4f * eGas, 12f * eGas, (h - gas).Length()) : 0f;
            Vector3 centre = gas + 0.5f * w * (h - gas);
            float e = MathF.Max(MathF.Max(Margin * Percentile95(centre, n), s.MinHalfView),
                                w * (1.2f * (h - centre).Length() + s.HoleMargin));
            return new Frame(centre, e / tanHalf);
        }

        /// <summary>Exponential approach of <paramref name="current"/> to <paramref name="target"/> over <paramref name="dt"/> seconds.</summary>
        public static Frame Damp(Frame current, Frame target, float dt, Settings s = null)
        {
            s ??= DefaultSettings;
            float kc = 1f - MathF.Exp(-MathF.Max(dt, 0f) / s.CentreTau);
            float tau = target.Distance > current.Distance ? s.ZoomOutTau : s.ZoomInTau;
            float kd = 1f - MathF.Exp(-MathF.Max(dt, 0f) / tau);
            // Distance damped in log space: the same relative speed at every scale.
            float logD = MathF.Log(current.Distance) + kd * (MathF.Log(target.Distance) - MathF.Log(current.Distance));
            float dist = MathF.Exp(logD);
            if ((target.Distance - current.Distance) * (target.Distance - dist) < 0f) dist = target.Distance; // rounding guard
            return new Frame(current.Centre + kc * (target.Centre - current.Centre), dist);
        }

        /// <summary>
        /// <see cref="Damp"/> for a moving target: the frame first moves with the target (its change since
        /// <paramref name="previousTarget"/>), so a target moving at constant velocity is followed without
        /// the lag plain exponential damping leaves; only the remaining error is damped. A jump of more
        /// than half the view (a discontinuity) is not copied, just damped.
        /// </summary>
        public static Frame Track(Frame current, Frame previousTarget, Frame target, float dt, Settings s = null)
        {
            Vector3 move = target.Centre - previousTarget.Centre;
            float halfView = 0.5f * MathF.Tan(0.5f * (s ?? DefaultSettings).FieldOfView) * MathF.Max(current.Distance, 1e-30f);
            if (float.IsFinite(move.X + move.Y + move.Z) && move.Length() <= halfView)
                current = current with { Centre = current.Centre + move };
            return Damp(current, target, dt, s);
        }

        private static readonly Settings DefaultSettings = new Settings();

        private static float Percentile95(Vector3 c, int n)
        {
            for (int k = 0; k < n; k++)
            {
                float dx = _x[k] - c.X, dy = _y[k] - c.Y, dz = _z[k] - c.Z;
                _d[k] = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            return Select(_d, n, (int)(0.95f * (n - 1)));
        }

        private static float Median(float[] a, int n) => Select(a, n, (n - 1) / 2);

        // k-th smallest of a[0..n) (Hoare quickselect, in place; deterministic middle pivot).
        private static float Select(float[] a, int n, int k)
        {
            int lo = 0, hi = n - 1;
            while (lo < hi)
            {
                float pivot = a[(lo + hi) >> 1];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (a[i] < pivot) i++;
                    while (a[j] > pivot) j--;
                    if (i <= j) { (a[i], a[j]) = (a[j], a[i]); i++; j--; }
                }
                if (k <= j) hi = j;
                else if (k >= i) lo = i;
                else break;
            }
            return a[k];
        }

        private static float SmoothStep(float e0, float e1, float x)
        {
            float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }
}
