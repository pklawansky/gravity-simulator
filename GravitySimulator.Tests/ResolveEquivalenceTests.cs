using System.Numerics;
using GravitySimulator.Planets;

namespace GravitySimulator.Tests;

/// <summary>
/// The collision resolve's parallel speed-ups must not change a single bit: speculative parallel classification
/// against classifying every pair in the sweep, and the parallel candidate re-test/sort against the CPU broad phase.
/// </summary>
public class ResolveEquivalenceTests
{
    // A dense, hot box of grains (m0/4 to m0, about one neighbour each, speeds of several escape speeds):
    // most contacts are hit-and-run, erosion or disruption, and many bodies are in more than one contact
    // (so some classifications go stale during the sweep).
    private static readonly Lazy<ParticleSystem> Busy = new(() =>
    {
        var rng = new Random(2);
        const int n = 60_000;
        const float m0 = 1e-7f, r0 = 0.004f;
        var ps = new ParticleSystem(n);
        float side = MathF.Cbrt(n * 4f / 3f * MathF.PI * MathF.Pow(2f * r0, 3)) * 0.8f;
        for (int k = 0; k < n; k++)
        {
            var p = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble()) * side;
            var v = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 0.1f;
            float m = m0 * (0.25f + 0.75f * (float)rng.NextDouble());
            ps.Add(p, v, m, r0 * MathF.Cbrt(m / m0));
        }
        return ps;
    });

    private static void AssertSameState(ParticleSystem a, ParticleSystem b)
    {
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a.Position[i], b.Position[i]);
            Assert.Equal(a.Velocity[i], b.Velocity[i]);
            Assert.Equal(a.Acceleration[i], b.Acceleration[i]);
            Assert.Equal(a.Mass[i], b.Mass[i]);
            Assert.Equal(a.Radius[i], b.Radius[i]);
            Assert.Equal(a.Class[i], b.Class[i]);
        }
    }

    [Theory]
    [InlineData(60_000)]   // room for fragments
    [InlineData(0)]        // at the ceiling: fragments folded
    public void SpeculativeClassify_IsBitIdenticalToSequential(int extraRoom)
    {
        var state = Busy.Value;
        var a = state.Clone();
        var b = state.Clone();
        var on = new CollisionResolver { Model = CollisionModel.Outcomes, MaxBodies = state.Count + extraRoom, MinFragmentMass = 2.5e-8f };
        var off = new CollisionResolver { Model = CollisionModel.Outcomes, MaxBodies = state.Count + extraRoom, MinFragmentMass = 2.5e-8f, SpeculativeClassify = false };
        int contacts = 0;
        for (int sweep = 0; sweep < 3; sweep++)
        {
            on.Resolve(a);
            off.Resolve(b);
            contacts += on.LastContacts;
            Assert.Equal(off.LastContacts, on.LastContacts);
            Assert.Equal(off.LastMerges, on.LastMerges);
            Assert.Equal(off.LastHitAndRuns, on.LastHitAndRuns);
            Assert.Equal(off.LastDisruptions, on.LastDisruptions);
            Assert.Equal(off.LastFragments, on.LastFragments);
            AssertSameState(a, b);
            // Nudge everyone a little so the next sweep has fresh contacts.
            for (int i = 0; i < a.Count; i++) { a.Position[i] += a.Velocity[i] * 0.002f; b.Position[i] += b.Velocity[i] * 0.002f; }
        }
        Assert.True(contacts > 3 * 2048, $"only {contacts} contacts: the speculative path did not run");
    }

    [Fact]
    public void ParallelCandidateFilter_GivesTheCpuBroadPhasePairs()
    {
        var ps = Busy.Value.Clone();
        var cpu = new CollisionResolver().FindContacts(ps);
        // Candidates: every contact (shuffled, both orientations, some twice) plus near misses that must be dropped.
        var rng = new Random(3);
        var cand = new List<uint>();
        foreach (var (i, j) in cpu)
        {
            if (rng.Next(2) == 0) { cand.Add((uint)i); cand.Add((uint)j); } else { cand.Add((uint)j); cand.Add((uint)i); }
            if (rng.Next(4) == 0) { cand.Add((uint)i); cand.Add((uint)j); }
        }
        for (int k = 0; k < 40_000; k++) { int i = rng.Next(ps.Count), j = rng.Next(ps.Count); cand.Add((uint)i); cand.Add((uint)j); }
        var pairs = new List<(uint, uint)>();
        for (int k = 0; k < cand.Count; k += 2) pairs.Add((cand[k], cand[k + 1]));
        var shuffled = pairs.OrderBy(_ => rng.Next()).SelectMany(p => new[] { p.Item1, p.Item2 }).ToArray();
        Assert.True(shuffled.Length / 2 >= 32_768, "the parallel filter must run");
        var found = new CollisionResolver().FindContacts(ps, shuffled, shuffled.Length / 2);
        Assert.True(cpu.Count > 1000, $"only {cpu.Count} contacts");
        Assert.Equal(cpu, found);
    }
}
