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
    /// <para>
    /// With <see cref="Model"/> = <see cref="CollisionModel.Outcomes"/>, Planet-Planet pairs instead follow
    /// the Leinhardt &amp; Stewart (2012) regimes (<see cref="CollisionOutcomes"/>): perfect merge, partial
    /// accretion, hit-and-run, erosion, catastrophic disruption. Fragments are real particles, appended
    /// after the sweep (so they never collide in the step they are born), never lighter than
    /// <see cref="MinFragmentMass"/>, and never pushing the live count above <see cref="MaxBodies"/>.
    /// Every other pair keeps the rules above.
    /// </para>
    /// </summary>
    public sealed class CollisionResolver
    {
        /// <summary>Bodies larger than this multiple of the mean radius are "oversize" and checked against everyone directly.</summary>
        private const float OversizeFactor = 4f;
        private const int MaxOversize = 64;

        public float MergeThreshold { get; set; } = 1f;
        public float Restitution { get; set; } = 0.5f;
        /// <summary>Rule for Planet-Planet pairs. The resolver defaults to the original merge-or-bounce.</summary>
        public CollisionModel Model { get; set; } = CollisionModel.MergeOrBounce;
        /// <summary>Outcomes: live-count ceiling for fragments (0 = the count at the start of each Resolve).</summary>
        public int MaxBodies { get; set; }
        /// <summary>Outcomes: lightest allowed fragment, m0 (0 = the lighter body of each pair).</summary>
        public float MinFragmentMass { get; set; }

        public int LastMerges { get; private set; }
        public int LastBounces { get; private set; }
        /// <summary>Merges this step where a black hole absorbed a star or another compact object.</summary>
        public int LastBlackHoleMergers { get; private set; }
        /// <summary>Outcomes regimes this step (perfect merges count in <see cref="LastMerges"/>).</summary>
        public int LastPartialAccretions { get; private set; }
        public int LastHitAndRuns { get; private set; }
        public int LastErosions { get; private set; }
        public int LastDisruptions { get; private set; }
        /// <summary>Fragments created this step.</summary>
        public int LastFragments { get; private set; }
        /// <summary>Fragments not created this step because the live count was at <see cref="MaxBodies"/> (their mass was folded).</summary>
        public int LastFoldedAtCeiling { get; private set; }

        // Reused buffers (grown on demand, never per step in steady state).
        private int[] _cellOf = Array.Empty<int>();
        private int[] _bucketStart = Array.Empty<int>();
        private int[] _bucketCount = Array.Empty<int>();
        private int[] _sorted = Array.Empty<int>();
        private readonly int[] _oversize = new int[MaxOversize];
        private bool[] _isOversize = Array.Empty<bool>();

        // Outcomes: fragments waiting to be appended after the sweep, and bodies removed by
        // non-merge outcomes (merges are counted in LastMerges). Grown on demand only.
        private Vector3[] _fragPos = new Vector3[64], _fragVel = new Vector3[64], _fragAcc = new Vector3[64];
        private float[] _fragMass = new float[64], _fragRadius = new float[64];
        private int _fragCount, _removed, _startCount, _ceiling;
        private uint _rng = 0x9E3779B9u;

        /// <summary>Resolves all current contacts, then compacts away merged-out bodies.</summary>
        public void Resolve(ParticleSystem ps)
        {
            LastMerges = 0;
            LastBounces = 0;
            LastBlackHoleMergers = 0;
            LastPartialAccretions = LastHitAndRuns = LastErosions = LastDisruptions = LastFragments = LastFoldedAtCeiling = 0;
            _fragCount = _removed = 0;
            int n = ps.Count;
            if (n < 2) return;
            _startCount = n;
            _ceiling = MaxBodies > 0 ? MaxBodies : n;
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

            if (LastMerges > 0 || _removed > 0) ps.Compact();
            if (ps.Count + _fragCount > ps.Capacity) // grow geometrically, but never past the ceiling
                ps.EnsureCapacity(Math.Max(ps.Count + _fragCount, Math.Min(_ceiling, 2 * ps.Capacity)));
            for (int k = 0; k < _fragCount; k++)
            {
                int f = ps.Add(_fragPos[k], _fragVel[k], _fragMass[k], _fragRadius[k]);
                ps.Acceleration[f] = _fragAcc[k];
            }
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
            if (Model == CollisionModel.Outcomes && ci == BodyClass.Planet && cj == BodyClass.Planet)
            {
                ResolveOutcome(ps, i, j, dist2, rs);
                return;
            }
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

        // ---- collision outcomes (Planet-Planet pairs) ---------------------------------------------

        private void ResolveOutcome(ParticleSystem ps, int i, int j, float dist2, float rs)
        {
            int t = ps.Mass[i] >= ps.Mass[j] ? i : j, p = t == i ? j : i; // target = heavier (merge keeps its slot)
            float mt = ps.Mass[t], mp = ps.Mass[p], rt = ps.Radius[t], rp = ps.Radius[p], mtot = mt + mp;
            Vector3 d = ps.Position[p] - ps.Position[t];
            Vector3 vRel = ps.Velocity[p] - ps.Velocity[t];
            var r = CollisionOutcomes.Classify(mt, mp, rt, rp, d, vRel);
            float m0 = MinFragmentMass > 0f ? MinFragmentMass : mp;

            if (r.Outcome == CollisionOutcome.HitAndRun)
            {
                ResolveHitAndRun(ps, t, p, r, dist2, rs, m0);
                return;
            }

            // Every other outcome replaces the pair by one remnant (in the target's slot) at the centre of mass.
            Vector3 xcm = (ps.Position[t] * mt + ps.Position[p] * mp) / mtot;
            Vector3 vcm = (ps.Velocity[t] * mt + ps.Velocity[p] * mp) / mtot;
            Vector3 acm = (ps.Acceleration[t] * mt + ps.Acceleration[p] * mp) / mtot;
            float rho = CollisionOutcomes.MeanDensity(mt, mp, rt, rp);
            ps.Mass[p] = 0f;
            ps.Class[t] = BodyClass.Planet;
            ps.Age[t] = 0f;
            if (r.Outcome == CollisionOutcome.PerfectMerge)
            {
                // Same as a merge-or-bounce merge: radius from the summed volume.
                ps.Position[t] = xcm;
                ps.Velocity[t] = vcm;
                ps.Acceleration[t] = acm;
                ps.Radius[t] = MathF.Cbrt(rt * rt * rt + rp * rp * rp);
                ps.Mass[t] = mtot;
                LastMerges++;
                return;
            }
            _removed++;
            switch (r.Outcome)
            {
                case CollisionOutcome.PartialAccretion: LastPartialAccretions++; break;
                case CollisionOutcome.Erosion: LastErosions++; break;
                default: LastDisruptions++; break;
            }
            Vector3 axis = vRel.LengthSquared() > 0f ? Vector3.Normalize(vRel) : Vector3.Normalize(d);
            EmitFragments(ps, t, mtot, mtot - r.LargestRemnant, rho, m0, xcm, vcm, acm, axis, -1);
        }

        // Target keeps its mass; normal velocities exchanged as a perfectly inelastic hit, tangential kept;
        // pushed apart; the projectile keeps its reverse-impact remnant and sheds the rest as fragments.
        private void ResolveHitAndRun(ParticleSystem ps, int t, int p, in CollisionResult r, float dist2, float rs, float m0)
        {
            LastHitAndRuns++;
            float mt = ps.Mass[t], mp = ps.Mass[p], mtot = mt + mp;
            float dist = MathF.Sqrt(dist2);
            Vector3 d = ps.Position[p] - ps.Position[t];
            Vector3 nrm = dist > 1e-12f ? d / dist : Vector3.UnitX;
            float vtn = Vector3.Dot(ps.Velocity[t], nrm), vpn = Vector3.Dot(ps.Velocity[p], nrm);
            float vcmn = (mt * vtn + mp * vpn) / mtot;
            ps.Velocity[t] += nrm * (vcmn - vtn);
            ps.Velocity[p] += nrm * (vcmn - vpn);
            float overlap = rs * 1.0001f - dist; // a hair of clearance so a duplicate hash hit does not re-collide them
            if (overlap > 0f)
            {
                ps.Position[t] -= nrm * (overlap * mp / mtot);
                ps.Position[p] += nrm * (overlap * mt / mtot);
            }

            float rp = ps.Radius[p];
            float rhoP = mp / (4f / 3f * MathF.PI * rp * rp * rp);
            Vector3 tangential = ps.Velocity[p] - ps.Velocity[t];
            Vector3 axis = tangential.LengthSquared() > 0f ? Vector3.Normalize(tangential) : nrm;
            EmitFragments(ps, p, mp, mp - r.ProjectileRemnant, rhoP, m0, ps.Position[p], ps.Velocity[p], ps.Acceleration[p], axis, t);
        }

        /// <summary>
        /// Turns body <paramref name="rem"/> into the remnant of a system of <paramref name="systemMass"/>, with
        /// <paramref name="debris"/> split into fragments (none below the minimum fragment mass, at most
        /// <see cref="CollisionOutcomes.MaxFragmentsPerEvent"/>; only at the ceiling is the excess folded into the
        /// largest fragment or the remnant). Fragments leave in a 45-degree double cone around the impact axis,
        /// sides chosen to balance momentum, at <see cref="FragmentSpeedFactor"/> x the remnant's escape speed
        /// (smaller fragments faster), placed just outside the remnant and pushed outward along their own
        /// direction until they clear every fragment already placed (and the obstacle). The whole set is then
        /// shifted (position and velocity) so its centre of mass and momentum equal the inputs exactly: mass, momentum and centre of mass are
        /// conserved and the set's net momentum relative to the centre of mass is zero.
        /// </summary>
        private void EmitFragments(ParticleSystem ps, int rem, float systemMass, float debris, float rho, float m0,
            Vector3 x, Vector3 v, Vector3 a, Vector3 axis, int obstacle)
        {
            int budget = Math.Max(0, _ceiling - (_startCount - LastMerges - _removed) - _fragCount);
            Span<float> masses = stackalloc float[CollisionOutcomes.MaxFragmentsPerEvent];
            int n = CollisionOutcomes.SplitFragments(debris, m0, budget, masses, out int folded);
            LastFoldedAtCeiling += folded;
            float fragMass = 0f;
            for (int k = 0; k < n; k++) fragMass += masses[k];
            float mr = systemMass - fragMass;
            float rr = RadiusOf(mr, rho);
            ps.Mass[rem] = mr;
            ps.Radius[rem] = rr;
            ps.Acceleration[rem] = a;
            if (n == 0)
            {
                ps.Position[rem] = x;
                ps.Velocity[rem] = v;
                return;
            }
            EnsureFragmentCapacity(_fragCount + n);

            float vEsc = MathF.Sqrt(2f * mr / rr);
            Vector3 e1 = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
            Vector3 e2 = Vector3.Cross(axis, e1);
            const float CosHalfAngle = 0.70710678f; // 45-degree cone
            float plusMass = 0f, minusMass = 0f;
            int first = _fragCount;
            Vector3 sumOffset = Vector3.Zero, sumMomentum = Vector3.Zero;
            for (int k = 0; k < n; k++)
            {
                float m = masses[k], r = RadiusOf(m, rho);
                bool plus = plusMass <= minusMass;
                float cosT = 1f - NextFloat() * (1f - CosHalfAngle), sinT = MathF.Sqrt(MathF.Max(0f, 1f - cosT * cosT));
                float phi = NextFloat() * MathF.Tau;
                Vector3 dir = (plus ? axis : -axis) * cosT + (e1 * MathF.Cos(phi) + e2 * MathF.Sin(phi)) * sinT;
                float dist = ClearPlaced(ps, obstacle, x, dir, (rr + r) * 1.001f, r, first);
                if (plus) plusMass += m;
                else minusMass += m;
                Vector3 offset = dir * dist;
                Vector3 u = dir * (vEsc * FragmentSpeedFactor(masses[0], m, NextFloat()));
                int f = _fragCount++;
                _fragPos[f] = offset; // relative to x until the shift below
                _fragVel[f] = u;      // relative to v
                _fragAcc[f] = a;
                _fragMass[f] = m;
                _fragRadius[f] = r;
                sumOffset += offset * m;
                sumMomentum += u * m;
            }
            Vector3 dx = sumOffset / systemMass, dv = sumMomentum / systemMass;
            ps.Position[rem] = x - dx;
            ps.Velocity[rem] = v - dv;
            for (int f = _fragCount - n; f < _fragCount; f++)
            {
                _fragPos[f] = x + (_fragPos[f] - dx);
                _fragVel[f] = v + (_fragVel[f] - dv);
            }
            LastFragments += n;
        }

        /// <summary>
        /// Fragment launch speed in units of the remnant's escape speed: 1.05 (m_k / m_1)^(-1/6) plus a
        /// 0-0.2 random spread, capped at 2. Just above escape for the largest fragment, smaller fragments
        /// faster, following the weak v ~ m^(-1/6) mass dependence of laboratory disruption experiments
        /// (Nakamura &amp; Fujiwara 1991, Icarus 92, 132). The 64-fragment split spans m_1/m_64 ~ 80, so the
        /// smallest leave at the cap of 2 V_esc.
        /// </summary>
        public static float FragmentSpeedFactor(float largestMass, float mass, float random01)
        {
            float ratio = mass > 0f && largestMass > mass ? largestMass / mass : 1f;
            return MathF.Min(MaxFragmentSpeed, MinFragmentSpeed * MathF.Pow(ratio, 1f / 6f) + 0.2f * random01);
        }

        /// <summary>Fragment speed range, in units of the remnant's escape speed.</summary>
        public const float MinFragmentSpeed = 1.05f, MaxFragmentSpeed = 2f;

        // Pushes a fragment further out along its direction until it clears the obstacle (the hit-and-run
        // target) and every fragment of this event placed so far (offsets relative to x until the final shift).
        // dist only grows and each sphere's interval is passed at most once, so this ends.
        private float ClearPlaced(ParticleSystem ps, int obstacle, Vector3 x, Vector3 dir, float dist, float r, int first)
        {
            bool moved = true;
            while (moved)
            {
                moved = false;
                if (obstacle >= 0) moved |= Clear(ps.Position[obstacle] - x, ps.Radius[obstacle] + r, dir, ref dist);
                for (int f = first; f < _fragCount; f++) moved |= Clear(_fragPos[f], _fragRadius[f] + r, dir, ref dist);
            }
            return dist;
        }

        // |dir s - c| < clear for s in (s1, s2): roots of s^2 - 2 s (dir.c) + |c|^2 - clear^2. Moves dist past s2.
        private static bool Clear(Vector3 c, float clear, Vector3 dir, ref float dist)
        {
            float bc = Vector3.Dot(dir, c), disc = bc * bc - (c.LengthSquared() - clear * clear);
            if (disc <= 0f) return false;
            float root = MathF.Sqrt(disc);
            if (!(dist > bc - root && dist < bc + root)) return false;
            dist = (bc + root) * 1.001f;
            return true;
        }

        private static float RadiusOf(float mass, float rho) => MathF.Cbrt(3f * mass / (4f * MathF.PI * rho));

        // xorshift32 in [0, 1): deterministic and allocation-free.
        private float NextFloat()
        {
            uint s = _rng;
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            _rng = s;
            return (s >> 8) * (1f / 16777216f);
        }

        private void EnsureFragmentCapacity(int needed)
        {
            if (needed <= _fragMass.Length) return;
            int size = Math.Max(needed, _fragMass.Length * 2);
            Array.Resize(ref _fragPos, size);
            Array.Resize(ref _fragVel, size);
            Array.Resize(ref _fragAcc, size);
            Array.Resize(ref _fragMass, size);
            Array.Resize(ref _fragRadius, size);
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
