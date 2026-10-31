using System.Numerics;
using GravitySimulator.Planets;

namespace GravitySimulator.Tests;

/// <summary>CollisionResolver in <see cref="CollisionModel.Outcomes"/> mode: each regime applied to a ParticleSystem.</summary>
public class CollisionResolverOutcomesTests
{
    private const float M0 = 0.01f;

    private static float RadiusAtDensity(float m, float rho = 1f) => MathF.Cbrt(3f * m / (4f * MathF.PI * rho));

    // Target at rest in a moving frame; projectile approaching along -X with offset b (R_t + R_p) in Y.
    private static readonly Vector3 Origin = new(0.3f, -0.2f, 0.1f);
    private static readonly Vector3 Bulk = new(0.2f, 0.1f, -0.05f);

    private static ParticleSystem Contact(float vi, float b, float mt = 1f, float mp = 1f,
        BodyClass ct = BodyClass.Planet, BodyClass cp = BodyClass.Planet)
    {
        float rt = RadiusAtDensity(mt), rp = RadiusAtDensity(mp), s = rt + rp;
        float off = b * s, sep = 0.999f * s;
        var d = new Vector3(MathF.Sqrt(MathF.Max(0f, sep * sep - off * off)), off, 0f);
        var ps = new ParticleSystem(4);
        ps.Add(Origin, Bulk, mt, rt, ct);
        ps.Add(Origin + d, Bulk + new Vector3(-vi, 0f, 0f), mp, rp, cp);
        return ps;
    }

    private static CollisionResolver Outcomes(int maxBodies = 20, float m0 = M0) =>
        new() { Model = CollisionModel.Outcomes, MaxBodies = maxBodies, MinFragmentMass = m0 };

    private static (double Mass, Vector3 Momentum, Vector3 Com) Totals(ParticleSystem ps)
    {
        double m = 0, x = 0, y = 0, z = 0;
        for (int i = 0; i < ps.Count; i++)
        {
            m += ps.Mass[i];
            x += (double)ps.Mass[i] * ps.Position[i].X;
            y += (double)ps.Mass[i] * ps.Position[i].Y;
            z += (double)ps.Mass[i] * ps.Position[i].Z;
        }
        return (m, TestHelpers.TotalMomentum(ps), new Vector3((float)(x / m), (float)(y / m), (float)(z / m)));
    }

    private static void AssertConserved((double Mass, Vector3 Momentum, Vector3 Com) before, ParticleSystem ps, float vScale)
    {
        var after = Totals(ps);
        Assert.Equal(before.Mass, after.Mass, before.Mass * 1e-5);
        float dp = (after.Momentum - before.Momentum).Length();
        Assert.True(dp < 1e-5f * (float)before.Mass * vScale, $"momentum {before.Momentum} -> {after.Momentum}");
        float dc = (after.Com - before.Com).Length();
        Assert.True(dc < 1e-5f * (1f + before.Com.Length()), $"centre of mass {before.Com} -> {after.Com}");
    }

    private static int Events(CollisionResolver r) =>
        r.LastMerges + r.LastBounces + r.LastPartialAccretions + r.LastHitAndRuns + r.LastErosions + r.LastDisruptions;

    [Fact]
    public void Defaults_ResolverKeepsMergeOrBounce_SimulationUsesOutcomes()
    {
        Assert.Equal(CollisionModel.MergeOrBounce, new CollisionResolver().Model);
        Assert.Equal(CollisionModel.Outcomes, new SimulationParameters().CollisionModel);
    }

    [Fact]
    public void PerfectMerge_OneBody_ConservesMassMomentumCom_RadiusFromVolume()
    {
        var ps = Contact(vi: 2f, b: 0f);
        var before = Totals(ps);
        float r3 = ps.Radius[0] * ps.Radius[0] * ps.Radius[0] + ps.Radius[1] * ps.Radius[1] * ps.Radius[1];
        var res = Outcomes();

        res.Resolve(ps);

        Assert.Equal(1, ps.Count);
        Assert.Equal(1, res.LastMerges);
        Assert.Equal(1, Events(res));
        Assert.Equal(0, res.LastFragments);
        Assert.Equal(MathF.Cbrt(r3), ps.Radius[0], 5);
        AssertConserved(before, ps, 2f);
    }

    [Fact]
    public void PartialAccretion_RemnantAtLeastTarget_PlusFragments_Conserved()
    {
        var ps = Contact(vi: 3f, b: 0f);
        var before = Totals(ps);
        var res = Outcomes();

        res.Resolve(ps);

        Assert.Equal(1, res.LastPartialAccretions);
        Assert.Equal(1, Events(res));
        Assert.True(res.LastFragments > 0);
        Assert.Equal(1 + res.LastFragments, ps.Count);
        Assert.True(ps.Mass[0] >= 1f && ps.Mass[0] < 2f, $"remnant {ps.Mass[0]}");
        AssertConserved(before, ps, 3f);
    }

    [Fact]
    public void Erosion_RemnantBelowTarget_Conserved()
    {
        var ps = Contact(vi: 6f, b: 0f);
        var before = Totals(ps);
        var res = Outcomes();

        res.Resolve(ps);

        Assert.Equal(1, res.LastErosions);
        Assert.Equal(1, Events(res));
        Assert.True(ps.Mass[0] < 1f && ps.Mass[0] >= 0.2f, $"remnant {ps.Mass[0]}");
        Assert.Equal(1 + res.LastFragments, ps.Count);
        AssertConserved(before, ps, 6f);
    }

    [Fact]
    public void Disruption_AtMost8Fragments_NoneBelowM0_Conserved()
    {
        var ps = Contact(vi: 10f, b: 0f);
        var before = Totals(ps);
        var res = Outcomes(m0: 0.001f);

        res.Resolve(ps);

        Assert.Equal(1, res.LastDisruptions);
        Assert.Equal(8, res.LastFragments);
        Assert.Equal(9, ps.Count);
        Assert.True(ps.Mass[0] < 0.2f);
        for (int i = 1; i < ps.Count; i++) Assert.True(ps.Mass[i] >= 0.001f, $"fragment {i} mass {ps.Mass[i]}");
        AssertConserved(before, ps, 10f);
    }

    [Fact]
    public void HitAndRun_TargetKeepsMassAndTangentialVelocity_BodiesSeparated_Conserved()
    {
        var ps = Contact(vi: 3f, b: 0.8f);
        var before = Totals(ps);
        Vector3 n = Vector3.Normalize(ps.Position[1] - ps.Position[0]);
        Vector3 vt0 = ps.Velocity[0];
        var res = Outcomes();

        res.Resolve(ps);

        Assert.Equal(1, res.LastHitAndRuns);
        Assert.Equal(1, Events(res));
        Assert.Equal(2 + res.LastFragments, ps.Count);
        Assert.Equal(1f, ps.Mass[0], 6);
        Assert.True(ps.Mass[1] > 0f && ps.Mass[1] <= 1f);
        Vector3 tan0 = vt0 - Vector3.Dot(vt0, n) * n, tan1 = ps.Velocity[0] - Vector3.Dot(ps.Velocity[0], n) * n;
        Assert.True((tan1 - tan0).Length() < 1e-5f, $"target tangential {tan0} -> {tan1}");
        float d = Vector3.Distance(ps.Position[0], ps.Position[1]);
        Assert.True(d >= (ps.Radius[0] + ps.Radius[1]) * 0.9999f, $"still overlapping: d={d}");
        AssertConserved(before, ps, 3f);
    }

    [Fact]
    public void HitAndRun_OnlyAboveBCrit_TheSameSpeedHeadOnAccretesOrErodes()
    {
        foreach (float b in new[] { 0f, 0.2f, 0.45f })
        {
            var res = Outcomes();
            res.Resolve(Contact(vi: 3f, b: b));
            Assert.Equal(0, res.LastHitAndRuns);
            Assert.Equal(1, res.LastPartialAccretions + res.LastErosions);
        }
        foreach (float b in new[] { 0.6f, 0.8f, 0.95f })
        {
            var res = Outcomes();
            res.Resolve(Contact(vi: 3f, b: b));
            Assert.Equal(1, res.LastHitAndRuns);
        }
    }

    [Fact]
    public void Fragments_AreBornAfterTheSweep_AndDoNotOverlapEachOtherOrTheRemnant()
    {
        var ps = Contact(vi: 10f, b: 0f);
        var res = Outcomes(m0: 0.001f);

        res.Resolve(ps);

        Assert.Equal(1, Events(res)); // the fragments took part in no contact this step
        for (int i = 0; i < ps.Count; i++)
        for (int j = i + 1; j < ps.Count; j++)
        {
            float d = Vector3.Distance(ps.Position[i], ps.Position[j]);
            Assert.True(d >= ps.Radius[i] + ps.Radius[j], $"bodies {i} and {j} overlap: d={d}");
        }
        // So the next sweep (no motion in between) finds nothing to resolve.
        res.Resolve(ps);
        Assert.Equal(0, Events(res));
    }

    [Fact]
    public void GlobalCeiling_HoldsUnderAForcedShowerOfDisruptions()
    {
        // 30 disrupting pairs far apart; ceiling = the starting count, so fragments only fill freed slots.
        const int pairs = 30;
        var ps = new ParticleSystem(2 * pairs);
        for (int k = 0; k < pairs; k++)
        {
            var one = Contact(vi: 10f, b: 0f);
            Vector3 shift = new(k * 10f, 0f, 0f);
            ps.Add(one.Position[0] + shift, one.Velocity[0], one.Mass[0], one.Radius[0]);
            ps.Add(one.Position[1] + shift, one.Velocity[1], one.Mass[1], one.Radius[1]);
        }
        int ceiling = ps.Count;
        var before = Totals(ps);
        var res = Outcomes(maxBodies: ceiling, m0: 0.001f);
        int capacity = ps.Capacity;

        res.Resolve(ps);

        Assert.Equal(pairs, res.LastDisruptions);
        Assert.True(ps.Count <= ceiling, $"count {ps.Count} > ceiling {ceiling}");
        Assert.Equal(ceiling, ps.Count); // every freed slot was refilled by a fragment
        Assert.Equal(capacity, ps.Capacity); // no growth
        for (int i = 0; i < ps.Count; i++) Assert.True(ps.Mass[i] >= 0.001f, $"body {i} mass {ps.Mass[i]}");
        AssertConserved(before, ps, 10f);
    }

    [Fact]
    public void NoFragmentLighterThanM0_EvenWhenDebrisIsSmall()
    {
        // Head-on just above V'_esc: tiny debris; with a large m0 it all goes back into the remnant.
        var ps = Contact(vi: 2.4f, b: 0f);
        var res = Outcomes(m0: 0.5f);

        res.Resolve(ps);

        Assert.Equal(1, res.LastPartialAccretions);
        for (int i = 0; i < ps.Count; i++) Assert.True(ps.Mass[i] >= 0.5f, $"body {i} mass {ps.Mass[i]}");
    }

    [Theory]
    [InlineData(BodyClass.Star, BodyClass.Planet)]
    [InlineData(BodyClass.Gas, BodyClass.Gas)]
    [InlineData(BodyClass.Planet, BodyClass.WhiteDwarf)]
    public void NonRockyPairs_KeepMergeOrBounceRules(BodyClass a, BodyClass b)
    {
        var ps = Contact(vi: 6f, b: 0f, ct: a, cp: b);
        var res = Outcomes();
        res.MergeThreshold = 1f;
        res.Restitution = 0.5f;

        res.Resolve(ps);

        Assert.Equal(1, res.LastBounces);
        Assert.Equal(2, ps.Count);
        Assert.Equal(0, res.LastFragments + res.LastPartialAccretions + res.LastErosions + res.LastDisruptions + res.LastHitAndRuns);
    }

    [Fact]
    public void BlackHole_StillAlwaysAbsorbs_UnderOutcomes()
    {
        var ps = Contact(vi: 50f, b: 0f, ct: BodyClass.BlackHole, cp: BodyClass.Planet);
        var res = Outcomes();

        res.Resolve(ps);

        Assert.Equal(1, ps.Count);
        Assert.Equal(1, res.LastMerges);
        Assert.Equal(BodyClass.BlackHole, ps.Class[0]);
    }
}
