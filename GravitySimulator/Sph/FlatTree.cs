using System;
using System.Numerics;

namespace GravitySimulator.Sph
{
    /// <summary>
    /// Barnes-Hut octree flattened into depth-first pre-order for a stackless walk: the first child of
    /// an internal node n is n + 1, and <see cref="Skip"/>[n] is the node after n's subtree. Filled by
    /// <see cref="Planets.BarnesHutTree.Flatten"/>; uploaded as-is to the GPU (gravity.comp).
    /// </summary>
    public sealed class FlatTree
    {
        /// <summary>xyz = centre of mass, w = mass.</summary>
        public Vector4[] ComMass = Array.Empty<Vector4>();
        /// <summary>xyz = cube centre, w = half size.</summary>
        public Vector4[] CentreHalf = Array.Empty<Vector4>();
        /// <summary>Index of the node after this subtree (== Count at the end of the walk).</summary>
        public int[] Skip = Array.Empty<int>();
        /// <summary>Leaf: first body index (an empty leaf has mass 0); internal node: -1.</summary>
        public int[] Body = Array.Empty<int>();
        /// <summary>Squared softening length: a leaf's body h², a node's mass-weighted mean of its bodies' h².</summary>
        public float[] Soft2 = Array.Empty<float>();
        public int Count;

        public void EnsureCapacity(int nodes)
        {
            if (nodes <= Skip.Length) return;
            int cap = Math.Max(nodes, Skip.Length * 3 / 2);
            Array.Resize(ref ComMass, cap);
            Array.Resize(ref CentreHalf, cap);
            Array.Resize(ref Skip, cap);
            Array.Resize(ref Body, cap);
            Array.Resize(ref Soft2, cap);
        }

        /// <summary>
        /// Stackless walk, the CPU mirror of gravity.comp. Every interaction (leaf or accepted node) uses
        /// the symmetric softening ε² = (h² + Soft2)/2. Returns the acceleration and the potential; a leaf
        /// at zero distance (the particle itself) is skipped.
        /// </summary>
        public Vector3 Acceleration(Vector3 p, float h, float theta, out float potential) =>
            Acceleration(p, h, theta, out potential, out _);

        /// <summary>
        /// As the 4-argument overload, also returning S = sum of m_j (r^2 + eps^2)^(-3/2), from which
        /// dPhi/dh = h m S / 2 (the adaptive-softening correction).
        /// </summary>
        public Vector3 Acceleration(Vector3 p, float h, float theta, out float potential, out float softSum)
        {
            Vector3 acc = Vector3.Zero;
            float phi = 0f, sum = 0f, h2 = h * h, theta2 = theta * theta;
            int n = 0;
            while (n < Count)
            {
                Vector4 cm = ComMass[n];
                Vector4 ch = CentreHalf[n];
                Vector3 d = new Vector3(cm.X - p.X, cm.Y - p.Y, cm.Z - p.Z);
                float d2 = d.LengthSquared();
                if (Body[n] >= 0)
                {
                    if (d2 > 0f && cm.W > 0f)
                    {
                        float inv = 1f / MathF.Sqrt(d2 + 0.5f * (h2 + Soft2[n]));
                        float mi3 = cm.W * inv * inv * inv;
                        acc += d * mi3;
                        sum += mi3;
                        phi -= cm.W * inv;
                    }
                    n = Skip[n];
                    continue;
                }
                float size = 2f * ch.W;
                bool inside = MathF.Abs(p.X - ch.X) <= ch.W && MathF.Abs(p.Y - ch.Y) <= ch.W && MathF.Abs(p.Z - ch.Z) <= ch.W;
                if (size * size < theta2 * d2 && !inside)
                {
                    float inv = 1f / MathF.Sqrt(d2 + 0.5f * (h2 + Soft2[n]));
                    float mi3 = cm.W * inv * inv * inv;
                    acc += d * mi3;
                    sum += mi3;
                    phi -= cm.W * inv;
                    n = Skip[n];
                }
                else n++;
            }
            potential = phi;
            softSum = sum;
            return acc;
        }
    }
}
