using System;
using System.Numerics;

namespace GravitySimulator.Planets
{
    /// <summary>
    /// Octree for Barnes-Hut gravity (G = 1). Rebuilt each step from a <see cref="ParticleSystem"/>;
    /// node storage is pooled and reused between builds, so steady-state builds do not allocate.
    /// After <see cref="Build"/>, <see cref="AccelerationAt"/> is read-only and safe to call from
    /// many threads at once.
    /// </summary>
    public sealed class BarnesHutTree
    {
        private const int MaxDepth = 24;
        private const int Empty = -1;
        private const int Internal = -2;

        /// <summary>Opening angle: a node of size s at distance d is used whole when s / d &lt; Theta.</summary>
        public float Theta { get; set; } = 0.6f;

        /// <summary>Plummer softening length epsilon.</summary>
        public float Softening { get; set; } = 0.01f;

        // Node pool (struct-of-arrays). Children of a node always have larger indices than the node.
        private int[] _child = Array.Empty<int>();      // 8 per node, -1 = none
        private int[] _parent = Array.Empty<int>();
        private int[] _body = Array.Empty<int>();       // Empty, Internal, or first body index of a leaf
        private Vector3[] _centre = Array.Empty<Vector3>();
        private float[] _half = Array.Empty<float>();
        private Vector3[] _com = Array.Empty<Vector3>();
        private float[] _mass = Array.Empty<float>();
        private int _nodeCount;

        private int[] _nextInLeaf = Array.Empty<int>(); // linked list for leaves holding several bodies (max depth)

        private Vector3[] _pos = Array.Empty<Vector3>();
        private float[] _bodyMass = Array.Empty<float>();

        public int NodeCount => _nodeCount;

        public BarnesHutTree(int expectedParticles)
        {
            EnsureNodeCapacity(Math.Max(64, expectedParticles * 2));
            _nextInLeaf = new int[Math.Max(1, expectedParticles)];
        }

        public void Build(ParticleSystem ps)
        {
            int n = ps.Count;
            _pos = ps.Position;
            _bodyMass = ps.Mass;
            if (_nextInLeaf.Length < n) _nextInLeaf = new int[ps.Capacity];

            // Bounding cube.
            Vector3 min = new Vector3(float.MaxValue), max = new Vector3(float.MinValue);
            for (int i = 0; i < n; i++)
            {
                min = Vector3.Min(min, _pos[i]);
                max = Vector3.Max(max, _pos[i]);
            }
            if (n == 0) { min = Vector3.Zero; max = Vector3.Zero; }
            Vector3 size = max - min;
            float half = MathF.Max(size.X, MathF.Max(size.Y, size.Z)) * 0.5f * 1.0001f + 1e-6f;

            _nodeCount = 0;
            NewNode((min + max) * 0.5f, half, -1);

            for (int i = 0; i < n; i++)
            {
                if (_bodyMass[i] <= 0f) continue;
                Insert(i);
            }

            ComputeMoments();
        }

        private void Insert(int b)
        {
            int node = 0;
            int depth = 0;
            Vector3 p = _pos[b];
            while (true)
            {
                int occupant = _body[node];
                if (occupant == Empty)
                {
                    _body[node] = b;
                    _nextInLeaf[b] = -1;
                    return;
                }
                if (occupant >= 0)
                {
                    if (depth >= MaxDepth)
                    {
                        // Coincident (or nearly) bodies: keep them together in this leaf.
                        _nextInLeaf[b] = occupant;
                        _body[node] = b;
                        return;
                    }
                    // Split the leaf: push its body (list) down one level, then keep descending.
                    _body[node] = Internal;
                    int o = occupant;
                    while (o >= 0)
                    {
                        int next = _nextInLeaf[o];
                        int c = ChildFor(node, _pos[o]);
                        _nextInLeaf[o] = _body[c] >= 0 ? _body[c] : -1;
                        _body[c] = o;
                        o = next;
                    }
                }
                node = ChildFor(node, p);
                depth++;
            }
        }

        /// <summary>Returns (creating if needed) the child of <paramref name="node"/> containing <paramref name="p"/>.</summary>
        private int ChildFor(int node, Vector3 p)
        {
            Vector3 c = _centre[node];
            int oct = (p.X >= c.X ? 1 : 0) | (p.Y >= c.Y ? 2 : 0) | (p.Z >= c.Z ? 4 : 0);
            int slot = node * 8 + oct;
            int child = _child[slot];
            if (child < 0)
            {
                float h = _half[node] * 0.5f;
                var cc = new Vector3(
                    c.X + ((oct & 1) != 0 ? h : -h),
                    c.Y + ((oct & 2) != 0 ? h : -h),
                    c.Z + ((oct & 4) != 0 ? h : -h));
                child = NewNode(cc, h, node);
                _child[slot] = child;
            }
            return child;
        }

        private int NewNode(Vector3 centre, float half, int parent)
        {
            if (_nodeCount == _half.Length) EnsureNodeCapacity(_half.Length * 2);
            int n = _nodeCount++;
            int s = n * 8;
            for (int k = 0; k < 8; k++) _child[s + k] = -1;
            _parent[n] = parent;
            _body[n] = Empty;
            _centre[n] = centre;
            _half[n] = half;
            _com[n] = Vector3.Zero;
            _mass[n] = 0f;
            return n;
        }

        private void EnsureNodeCapacity(int capacity)
        {
            if (capacity <= _half.Length) return;
            Array.Resize(ref _child, capacity * 8);
            Array.Resize(ref _parent, capacity);
            Array.Resize(ref _body, capacity);
            Array.Resize(ref _centre, capacity);
            Array.Resize(ref _half, capacity);
            Array.Resize(ref _com, capacity);
            Array.Resize(ref _mass, capacity);
        }

        private void ComputeMoments()
        {
            // Leaves: sum their bodies. Then children are folded into parents in reverse index order,
            // which is a valid post-order because every child index is larger than its parent's.
            // _com accumulates mass-weighted positions until normalised.
            for (int n = 0; n < _nodeCount; n++)
            {
                int b = _body[n];
                if (b < 0) continue;
                float m = 0f;
                Vector3 mp = Vector3.Zero;
                for (int o = b; o >= 0; o = _nextInLeaf[o])
                {
                    m += _bodyMass[o];
                    mp += _bodyMass[o] * _pos[o];
                }
                _mass[n] = m;
                _com[n] = mp;
            }
            for (int n = _nodeCount - 1; n >= 0; n--)
            {
                float m = _mass[n];
                Vector3 mp = _com[n];
                _com[n] = m > 0f ? mp / m : _centre[n];
                int parent = _parent[n];
                if (parent >= 0)
                {
                    _mass[parent] += m;
                    _com[parent] += mp;
                }
            }
        }

        /// <summary>Gravitational acceleration at <paramref name="p"/>, excluding body <paramref name="self"/> (pass -1 for none).</summary>
        public Vector3 AccelerationAt(Vector3 p, int self)
        {
            if (_nodeCount == 0 || _mass[0] <= 0f) return Vector3.Zero;
            float theta2 = Theta * Theta;
            float eps2 = Softening * Softening;
            Vector3 acc = Vector3.Zero;

            Span<int> stack = stackalloc int[8 * (MaxDepth + 2)];
            int sp = 0;
            stack[sp++] = 0;
            while (sp > 0)
            {
                int n = stack[--sp];
                int b = _body[n];
                if (b >= 0)
                {
                    for (int o = b; o >= 0; o = _nextInLeaf[o])
                    {
                        if (o == self) continue;
                        acc += PairAcceleration(_pos[o] - p, _bodyMass[o], eps2);
                    }
                    continue;
                }
                if (b == Empty) continue;

                Vector3 d = _com[n] - p;
                float dist2 = d.LengthSquared();
                float size = _half[n] * 2f;
                if (size * size < theta2 * dist2 && !Contains(n, p))
                {
                    acc += PairAcceleration(d, _mass[n], eps2);
                    continue;
                }
                int s = n * 8;
                for (int k = 0; k < 8; k++)
                {
                    int c = _child[s + k];
                    if (c >= 0) stack[sp++] = c;
                }
            }
            return acc;
        }

        private bool Contains(int n, Vector3 p)
        {
            Vector3 d = Vector3.Abs(p - _centre[n]);
            float h = _half[n];
            return d.X <= h && d.Y <= h && d.Z <= h;
        }

        private static Vector3 PairAcceleration(Vector3 d, float m, float eps2)
        {
            float r2 = d.LengthSquared() + eps2;
            if (r2 <= 0f) return Vector3.Zero;
            float inv = 1f / MathF.Sqrt(r2);
            return d * (m * inv * inv * inv);
        }

        /// <summary>Exact all-pairs acceleration on body <paramref name="i"/> (reference for tests; O(N)).</summary>
        public static Vector3 DirectAcceleration(ParticleSystem ps, int i, float softening)
        {
            float eps2 = softening * softening;
            Vector3 p = ps.Position[i];
            Vector3 acc = Vector3.Zero;
            for (int j = 0; j < ps.Count; j++)
            {
                if (j == i || ps.Mass[j] <= 0f) continue;
                acc += PairAcceleration(ps.Position[j] - p, ps.Mass[j], eps2);
            }
            return acc;
        }
    }
}
