using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;

namespace GravitySimulator.Planets
{
    /// <summary>
    /// Finds touching pairs with a hierarchical spatial hash (in parallel; see <see cref="FindContacts"/>) and
    /// resolves each one, in a fixed order:
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
        /// <summary>Distance tests the broad phase made this step (a measure of its work).</summary>
        public long LastPairTests { get; private set; }
        /// <summary>Wall time this step: finding the touching pairs, resolving them, compacting and appending fragments.</summary>
        public double LastDetectMs { get; private set; }
        public double LastResolveMs { get; private set; }
        public double LastCompactMs { get; private set; }

        // Reused buffers (grown on demand, never per step in steady state).
        private int[] _cellOf = Array.Empty<int>();
        private int[] _bucketStart = Array.Empty<int>(); // buckets + 1 entries: bucket b is [start[b], start[b + 1])
        private int[] _sorted = Array.Empty<int>();
        private int[] _level = Array.Empty<int>();
        private int[] _byLevel = Array.Empty<int>();
        private readonly int[] _levelCount = new int[MaxLevels], _levelStart = new int[MaxLevels], _levelFill = new int[MaxLevels];
        private readonly float[] _invCell = new float[MaxLevels], _levelMaxR = new float[MaxLevels];
        private int _levels, _live;
        private double _c0;
        private long[] _pairs = new long[256];
        private long[][] _chunkPairs = Array.Empty<long[]>();
        private int[] _chunkPairCount = Array.Empty<int>();
        private long[] _chunkTests = Array.Empty<long>();

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
            LastPairTests = 0;
            LastDetectMs = LastResolveMs = LastCompactMs = 0;
            _fragCount = _removed = 0;
            int n = ps.Count;
            if (n < 2) return;
            _startCount = n;
            _ceiling = MaxBodies > 0 ? MaxBodies : n;

            long t0 = Stopwatch.GetTimestamp();
            int pairs = Detect(ps);
            long t1 = Stopwatch.GetTimestamp();

            // Sequential, in (lower index, higher index) order: resolution mutates bodies and draws random numbers.
            // TryResolve re-tests each pair against the current state, so a pair that an earlier one moved apart
            // is skipped; contacts that only arise during the sweep wait for the next step.
            for (int k = 0; k < pairs; k++)
            {
                long key = _pairs[k];
                int i = (int)(key >> 32), j = (int)(key & 0xFFFFFFFF);
                if (ps.Mass[i] <= 0f || ps.Mass[j] <= 0f) continue;
                TryResolve(ps, i, j);
            }
            long t2 = Stopwatch.GetTimestamp();

            if (LastMerges > 0 || _removed > 0) ps.Compact();
            if (ps.Count + _fragCount > ps.Capacity) // grow geometrically, but never past the ceiling
                ps.EnsureCapacity(Math.Max(ps.Count + _fragCount, Math.Min(_ceiling, 2 * ps.Capacity)));
            for (int k = 0; k < _fragCount; k++)
            {
                int f = ps.Add(_fragPos[k], _fragVel[k], _fragMass[k], _fragRadius[k]);
                ps.Acceleration[f] = _fragAcc[k];
            }
            long t3 = Stopwatch.GetTimestamp();
            double ms = 1000.0 / Stopwatch.Frequency;
            LastDetectMs = (t1 - t0) * ms;
            LastResolveMs = (t2 - t1) * ms;
            LastCompactMs = (t3 - t2) * ms;
        }

        /// <summary>
        /// The touching pairs of the current state (live bodies whose centres are closer than the sum of their
        /// radii), each as (lower index, higher index), in the order <see cref="Resolve"/> handles them.
        /// Does not change the state.
        /// </summary>
        public List<(int A, int B)> FindContacts(ParticleSystem ps)
        {
            int count = ps.Count < 2 ? 0 : Detect(ps);
            var list = new List<(int, int)>(count);
            for (int k = 0; k < count; k++) list.Add(((int)(_pairs[k] >> 32), (int)(_pairs[k] & 0xFFFFFFFF)));
            return list;
        }

        // ---- broad phase: hierarchical spatial hash -------------------------------------------------
        //
        // Level L has cell size c0 * 2^L, with c0 just over twice the smallest radius; each body sits in the lowest
        // level whose cell is (with a 0.1% margin) at least its diameter. Two bodies of one level can only touch
        // from neighbouring cells, so each body scans its own cell and the 13 "forward" neighbours there. In every
        // coarser occupied level it scans the cells within its radius plus that level's largest radius (2 or 3 per
        // axis), or, for a level holding only a few bodies, the level's whole list. So every touching pair is met,
        // and the work per body does not depend on how large the largest bodies are (the old single grid was sized
        // by them). Bodies are scanned in parallel; the pairs are then sorted and de-duplicated (hash collisions
        // can report a pair twice), so the result does not depend on threading.

        private const int MaxLevels = 48;
        private const int DirectListMax = 16;
        private const int ParallelMinBodies = 4096;

        private int Detect(ParticleSystem ps)
        {
            int n = ps.Count;
            EnsureCapacity(ps.Capacity);

            float rMin = float.MaxValue;
            for (int i = 0; i < n; i++)
                if (ps.Mass[i] > 0f && ps.Radius[i] < rMin) rMin = ps.Radius[i];
            if (rMin == float.MaxValue) return 0;
            _c0 = Math.Max(2.002 * rMin, 1e-6);
            for (int l = 0; l < MaxLevels; l++) _invCell[l] = (float)(1.0 / (_c0 * Math.Pow(2.0, l)));

            // Level and hash bucket of every live body (independent per body, so in parallel).
            int[] start = _bucketStart;
            int buckets = start.Length - 1;
            int chunks = n < ParallelMinBodies ? 1 : Math.Min(n / 1024, Environment.ProcessorCount * 4);
            int chunkSize = (n + chunks - 1) / chunks;
            if (chunks == 1) ClassifyChunk(ps, 0, n);
            else Parallel.For(0, chunks, c => ClassifyChunk(ps, c * chunkSize, Math.Min(n, (c + 1) * chunkSize)));

            // Counting sorts (index order within a level and within a bucket).
            Array.Clear(_levelCount, 0, MaxLevels);
            Array.Clear(_levelMaxR, 0, MaxLevels);
            Array.Clear(start, 0, start.Length);
            int top = 0;
            for (int i = 0; i < n; i++)
            {
                int l = _level[i];
                if (l < 0) continue;
                _levelCount[l]++;
                if (ps.Radius[i] > _levelMaxR[l]) _levelMaxR[l] = ps.Radius[i];
                if (l > top) top = l;
                start[_cellOf[i] + 1]++;
            }
            _levels = top + 1;
            int run = 0;
            for (int l = 0; l < _levels; l++)
            {
                _levelStart[l] = run;
                run += _levelCount[l];
                _levelFill[l] = 0;
            }
            _live = run;
            for (int b = 0; b < buckets; b++) start[b + 1] += start[b]; // start[b] = first slot of bucket b
            for (int i = 0; i < n; i++)
            {
                int l = _level[i];
                if (l < 0) continue;
                _byLevel[_levelStart[l] + _levelFill[l]++] = i;
                _sorted[start[_cellOf[i]]++] = i; // afterwards start[b] = end of bucket b
            }
            for (int b = buckets; b > 0; b--) start[b] = start[b - 1];
            start[0] = 0;

            if (_chunkPairs.Length < chunks)
            {
                Array.Resize(ref _chunkPairs, chunks);
                Array.Resize(ref _chunkPairCount, chunks);
                Array.Resize(ref _chunkTests, chunks);
            }
            int live = _live, scanSize = (live + chunks - 1) / chunks;
            if (chunks == 1) DetectChunk(ps, 0, 0, live);
            else Parallel.For(0, chunks, c => DetectChunk(ps, c, c * scanSize, Math.Min(live, (c + 1) * scanSize)));

            int total = 0;
            long tests = 0;
            for (int c = 0; c < chunks; c++) { total += _chunkPairCount[c]; tests += _chunkTests[c]; }
            LastPairTests = tests;
            if (_pairs.Length < total) _pairs = new long[Math.Max(total, 2 * _pairs.Length)];
            int at = 0;
            for (int c = 0; c < chunks; c++)
            {
                Array.Copy(_chunkPairs[c], 0, _pairs, at, _chunkPairCount[c]);
                at += _chunkPairCount[c];
            }
            Array.Sort(_pairs, 0, total);
            int unique = 0;
            for (int k = 0; k < total; k++)
                if (unique == 0 || _pairs[k] != _pairs[unique - 1]) _pairs[unique++] = _pairs[k];
            return unique;
        }

        private void ClassifyChunk(ParticleSystem ps, int first, int end)
        {
            int mask = _bucketStart.Length - 2;
            for (int i = first; i < end; i++)
            {
                if (ps.Mass[i] <= 0f) { _level[i] = -1; continue; }
                double d = 2.002 * ps.Radius[i], c = _c0; // a 0.1% margin keeps "touching" within one cell despite rounding
                int l = 0;
                while (c < d && l < MaxLevels - 1) { c *= 2.0; l++; }
                _level[i] = l;
                Vector3 p = ps.Position[i];
                float inv = _invCell[l];
                _cellOf[i] = Hash((int)MathF.Floor(p.X * inv), (int)MathF.Floor(p.Y * inv), (int)MathF.Floor(p.Z * inv), l) & mask;
            }
        }

        // Scans the live bodies _sorted[first..end) (bucket order, so neighbours share cache lines).
        private void DetectChunk(ParticleSystem ps, int chunk, int first, int end)
        {
            long[] buf = _chunkPairs[chunk] ??= new long[256];
            int count = 0;
            long tests = 0;
            int[] bucket = _bucketStart;
            int mask = bucket.Length - 2;
            Vector3[] pos = ps.Position;
            float[] rad = ps.Radius;
            for (int q = first; q < end; q++)
            {
                int i = _sorted[q];
                int l = _level[i];
                Vector3 p = pos[i];
                float r = rad[i];

                // Own level: own cell (j > i) and the 13 forward neighbours.
                float inv = _invCell[l];
                int cx = (int)MathF.Floor(p.X * inv), cy = (int)MathF.Floor(p.Y * inv), cz = (int)MathF.Floor(p.Z * inv);
                for (int s = 0; s < 14; s++)
                {
                    int h = Hash(cx + Stencil[s * 3], cy + Stencil[s * 3 + 1], cz + Stencil[s * 3 + 2], l) & mask;
                    for (int k = bucket[h], e = bucket[h + 1]; k < e; k++)
                    {
                        int j = _sorted[k];
                        if (j == i || (s == 0 && j < i) || _level[j] != l) continue;
                        tests++;
                        if (Touching(p, r, pos[j], rad[j])) Push(ref buf, ref count, i, j);
                    }
                }

                // Coarser levels: the cells (at most 3 per axis) within reach of this body, or the whole level when it is small.
                for (int m = l + 1; m < _levels; m++)
                {
                    int lc = _levelCount[m];
                    if (lc == 0) continue;
                    if (lc <= DirectListMax)
                    {
                        for (int k = _levelStart[m], e = k + lc; k < e; k++)
                        {
                            int j = _byLevel[k];
                            tests++;
                            if (Touching(p, r, pos[j], rad[j])) Push(ref buf, ref count, i, j);
                        }
                        continue;
                    }
                    float im = _invCell[m], reach = (r + _levelMaxR[m]) * 1.001f;
                    int x0 = (int)MathF.Floor((p.X - reach) * im), x1 = (int)MathF.Floor((p.X + reach) * im);
                    int y0 = (int)MathF.Floor((p.Y - reach) * im), y1 = (int)MathF.Floor((p.Y + reach) * im);
                    int z0 = (int)MathF.Floor((p.Z - reach) * im), z1 = (int)MathF.Floor((p.Z + reach) * im);
                    for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int h = Hash(x, y, z, m) & mask;
                        for (int k = bucket[h], e = bucket[h + 1]; k < e; k++)
                        {
                            int j = _sorted[k];
                            if (_level[j] != m) continue;
                            tests++;
                            if (Touching(p, r, pos[j], rad[j])) Push(ref buf, ref count, i, j);
                        }
                    }
                }
            }
            _chunkPairs[chunk] = buf;
            _chunkPairCount[chunk] = count;
            _chunkTests[chunk] = tests;
        }

        // The same test TryResolve starts with, so every pair found here is one it resolves.
        private static bool Touching(Vector3 pi, float ri, Vector3 pj, float rj)
        {
            Vector3 d = pj - pi;
            float rs = ri + rj;
            return d.LengthSquared() < rs * rs;
        }

        private static void Push(ref long[] buf, ref int count, int i, int j)
        {
            if (count == buf.Length) Array.Resize(ref buf, buf.Length * 2);
            int a = Math.Min(i, j), b = Math.Max(i, j);
            buf[count++] = ((long)a << 32) | (uint)b;
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
            float overlap = rs * 1.0001f - dist; // a hair of clearance so rounding does not leave them touching
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

        private static int Hash(int x, int y, int z, int level) => (x * 73856093) ^ (y * 19349663) ^ (z * 83492791) ^ (level * 1640531527);

        private void EnsureCapacity(int capacity)
        {
            if (_cellOf.Length >= capacity) return;
            _cellOf = new int[capacity];
            _sorted = new int[capacity];
            _level = new int[capacity];
            _byLevel = new int[capacity];
            int buckets = 1;
            while (buckets < capacity) buckets <<= 1;
            _bucketStart = new int[buckets + 1];
        }
    }
}
