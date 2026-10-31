using System;
using System.Numerics;

namespace GravitySimulator.Planets
{
    /// <summary>
    /// Finds touching pairs with a uniform spatial hash and resolves each one:
    /// relative speed below <see cref="MergeThreshold"/> x mutual escape speed merges the pair
    /// (mass, momentum and centre of mass conserved; radius from summed volume); otherwise the pair
    /// bounces along the contact normal with restitution <see cref="Restitution"/> and is pushed
    /// apart so it no longer overlaps. G = 1. A black hole never bounces: it always absorbs what
    /// touches it. Merged bodies take the class/age given by <see cref="StellarPhysics.MergeClass"/>
    /// (all bodies are Planet when the stellar model is off, so nothing changes there).
    /// </summary>
    public sealed class CollisionResolver
    {
        /// <summary>Bodies larger than this multiple of the mean radius are "oversize" and checked against everyone directly.</summary>
        private const float OversizeFactor = 4f;
        private const int MaxOversize = 64;

        public float MergeThreshold { get; set; } = 1f;
        public float Restitution { get; set; } = 0.5f;

        public int LastMerges { get; private set; }
        public int LastBounces { get; private set; }
        /// <summary>Merges this step where a black hole absorbed a star or another compact object.</summary>
        public int LastBlackHoleMergers { get; private set; }

        // Reused buffers (grown on demand, never per step in steady state).
        private int[] _cellOf = Array.Empty<int>();
        private int[] _bucketStart = Array.Empty<int>();
        private int[] _bucketCount = Array.Empty<int>();
        private int[] _sorted = Array.Empty<int>();
        private readonly int[] _oversize = new int[MaxOversize];
        private bool[] _isOversize = Array.Empty<bool>();

        /// <summary>Resolves all current contacts, then compacts away merged-out bodies.</summary>
        public void Resolve(ParticleSystem ps)
        {
            LastMerges = 0;
            LastBounces = 0;
            LastBlackHoleMergers = 0;
            int n = ps.Count;
            if (n < 2) return;
            EnsureCapacity(ps.Capacity);

            // Classify oversize bodies (e.g. the star) and size the grid for the rest.
            double sumR = 0;
            for (int i = 0; i < n; i++) sumR += ps.Radius[i];
            float meanR = (float)(sumR / n);
            float cut = meanR * OversizeFactor;
            int oversizeCount = 0;
            float maxR = 0f;
            for (int i = 0; i < n; i++)
            {
                float r = ps.Radius[i];
                bool big = r > cut && oversizeCount < MaxOversize;
                _isOversize[i] = big;
                if (big) _oversize[oversizeCount++] = i;
                else if (r > maxR) maxR = r;
            }
            float cell = MathF.Max(2f * maxR, 1e-6f);
            float invCell = 1f / cell;

            // Counting sort of non-oversize bodies into hash buckets.
            int buckets = _bucketStart.Length;
            int mask = buckets - 1;
            Array.Clear(_bucketCount, 0, buckets);
            for (int i = 0; i < n; i++)
            {
                if (_isOversize[i]) { _cellOf[i] = -1; continue; }
                Vector3 p = ps.Position[i];
                int h = Hash((int)MathF.Floor(p.X * invCell), (int)MathF.Floor(p.Y * invCell), (int)MathF.Floor(p.Z * invCell)) & mask;
                _cellOf[i] = h;
                _bucketCount[h]++;
            }
            int run = 0;
            for (int b = 0; b < buckets; b++)
            {
                _bucketStart[b] = run;
                run += _bucketCount[b];
                _bucketCount[b] = 0;
            }
            for (int i = 0; i < n; i++)
            {
                int h = _cellOf[i];
                if (h < 0) continue;
                _sorted[_bucketStart[h] + _bucketCount[h]++] = i;
            }

            // Small-vs-small: own cell (pairs with j > i) plus the 13 "forward" neighbour cells (all j),
            // so each neighbouring pair of cells is scanned once. Hash collisions can make a pair show
            // up twice; that is harmless (merged bodies are skipped, separated bodies no longer touch).
            for (int i = 0; i < n; i++)
            {
                if (_isOversize[i] || ps.Mass[i] <= 0f) continue;
                Vector3 p = ps.Position[i];
                int cx = (int)MathF.Floor(p.X * invCell), cy = (int)MathF.Floor(p.Y * invCell), cz = (int)MathF.Floor(p.Z * invCell);
                for (int s = 0; s < 14; s++)
                {
                    int h = Hash(cx + Stencil[s * 3], cy + Stencil[s * 3 + 1], cz + Stencil[s * 3 + 2]) & mask;
                    int start = _bucketStart[h], end = start + _bucketCount[h];
                    for (int k = start; k < end; k++)
                    {
                        int j = _sorted[k];
                        if (j == i || (s == 0 && j < i) || ps.Mass[j] <= 0f || _isOversize[j]) continue;
                        TryResolve(ps, i, j);
                        if (ps.Mass[i] <= 0f) goto nextI;
                    }
                }
            nextI:;
            }

            // Oversize vs everyone (including other oversize bodies).
            for (int a = 0; a < oversizeCount; a++)
            {
                int i = _oversize[a];
                for (int j = 0; j < n; j++)
                {
                    if (j == i || ps.Mass[i] <= 0f || ps.Mass[j] <= 0f) continue;
                    if (_isOversize[j] && j < i) continue; // each oversize pair once
                    TryResolve(ps, i, j);
                }
            }

            if (LastMerges > 0) ps.Compact();
        }

        private void TryResolve(ParticleSystem ps, int i, int j)
        {
            Vector3 d = ps.Position[j] - ps.Position[i];
            float rs = ps.Radius[i] + ps.Radius[j];
            float dist2 = d.LengthSquared();
            if (dist2 >= rs * rs) return;

            float m1 = ps.Mass[i], m2 = ps.Mass[j], mt = m1 + m2;
            Vector3 vRel = ps.Velocity[j] - ps.Velocity[i];
            float vEsc = MathF.Sqrt(2f * mt / rs);

            BodyClass ci = ps.Class[i], cj = ps.Class[j];
            if (vRel.Length() < MergeThreshold * vEsc || ci == BodyClass.BlackHole || cj == BodyClass.BlackHole)
            {
                // Merge into the heavier body's slot; the other is marked dead (mass 0).
                int keep = m1 >= m2 ? i : j, gone = keep == i ? j : i;
                float rk = ps.Radius[keep], rg = ps.Radius[gone];
                ps.Position[keep] = (ps.Position[i] * m1 + ps.Position[j] * m2) / mt;
                ps.Velocity[keep] = (ps.Velocity[i] * m1 + ps.Velocity[j] * m2) / mt;
                ps.Acceleration[keep] = (ps.Acceleration[i] * m1 + ps.Acceleration[j] * m2) / mt;
                ps.Radius[keep] = MathF.Cbrt(rk * rk * rk + rg * rg * rg);
                var (cls, age) = StellarPhysics.MergeClass(ci, ps.Age[i], m1, cj, ps.Age[j], m2);
                if (cls == BodyClass.BlackHole && IsBlackHoleMerger(ci, cj)) LastBlackHoleMergers++;
                ps.Class[keep] = cls;
                ps.Age[keep] = age;
                ps.Mass[keep] = mt;
                ps.Mass[gone] = 0f;
                LastMerges++;
                return;
            }

            float dist = MathF.Sqrt(dist2);
            Vector3 nrm = dist > 1e-12f ? d / dist : Vector3.UnitX;
            float vn = Vector3.Dot(vRel, nrm);
            if (vn < 0f)
            {
                float jImpulse = -(1f + Restitution) * vn / (1f / m1 + 1f / m2);
                ps.Velocity[i] -= nrm * (jImpulse / m1);
                ps.Velocity[j] += nrm * (jImpulse / m2);
            }
            // Push apart along the normal, keeping the centre of mass fixed.
            float overlap = rs - dist;
            ps.Position[i] -= nrm * (overlap * m2 / mt);
            ps.Position[j] += nrm * (overlap * m1 / mt);
            LastBounces++;
        }

        /// <summary>A black hole absorbing a star or a compact object (not gas or planets) counts as a merger.</summary>
        public static bool IsBlackHoleMerger(BodyClass a, BodyClass b)
        {
            BodyClass other = a == BodyClass.BlackHole ? b : b == BodyClass.BlackHole ? a : BodyClass.Planet;
            return other >= BodyClass.Star;
        }

        // Own cell first, then the 13 neighbours with (dz, dy, dx) lexicographically after (0, 0, 0).
        private static readonly int[] Stencil = BuildStencil();

        private static int[] BuildStencil()
        {
            var s = new int[14 * 3];
            int k = 3; // entry 0 is (0, 0, 0)
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dz < 0 || (dz == 0 && (dy < 0 || (dy == 0 && dx <= 0)))) continue;
                s[k++] = dx; s[k++] = dy; s[k++] = dz;
            }
            return s;
        }

        private static int Hash(int x, int y, int z) => (x * 73856093) ^ (y * 19349663) ^ (z * 83492791);

        private void EnsureCapacity(int capacity)
        {
            if (_cellOf.Length >= capacity) return;
            _cellOf = new int[capacity];
            _sorted = new int[capacity];
            _isOversize = new bool[capacity];
            int buckets = 1;
            while (buckets < capacity * 2) buckets <<= 1;
            _bucketStart = new int[buckets];
            _bucketCount = new int[buckets];
        }
    }
}
