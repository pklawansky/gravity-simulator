using System.Numerics;
using GravitySimulator.Planets;

namespace GravitySimulator.Tests;

/// <summary>Pure maths of the Leinhardt &amp; Stewart (2012) collision outcome model (no ParticleSystem).</summary>
public class CollisionOutcomesTests
{
    private static float RadiusAtDensity(float m, float rho = 1f) => MathF.Cbrt(3f * m / (4f * MathF.PI * rho));

    // ---- building blocks ---------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(0.5, 0.75)]
    [InlineData(1.0, 0.5)]   // catastrophic disruption: half the mass in the largest remnant
    [InlineData(1.6, 0.2)]
    [InlineData(1.8, 0.1)]   // universal law meets the super-catastrophic law (continuity)
    public void UniversalLaw_ReproducesLargestRemnantFraction(double qOverQ, double expected)
    {
        Assert.Equal(expected, CollisionOutcomes.LargestRemnantFraction((float)qOverQ), 1e-5);
    }

    [Theory]
    [InlineData(3.6, 0.0353553)]  // 0.1 / 1.8^-1.5 * 3.6^-1.5 = 0.1 * 2^-1.5
    [InlineData(18.0, 0.0031623)] // 0.1 * 10^-1.5
    public void SuperCatastrophicLaw_UsesEtaMinus1point5(double qOverQ, double expected)
    {
        Assert.Equal(expected, CollisionOutcomes.LargestRemnantFraction((float)qOverQ), 1e-5);
    }

    [Fact]
    public void LargestRemnantFraction_IsContinuousAt1point8()
    {
        float below = CollisionOutcomes.LargestRemnantFraction(1.8f - 1e-4f);
        float above = CollisionOutcomes.LargestRemnantFraction(1.8f + 1e-4f);
        Assert.True(MathF.Abs(below - above) < 1e-3f, $"{below} vs {above}");
    }

    [Fact]
    public void SpecificImpactEnergy_IsHalfReducedMassVSquaredOverTotalMass()
    {
        // Eq. 1: Q_R = 0.5 mu V^2 / M_tot with mu = 2*1/3.
        Assert.Equal(0.5 * (2.0 / 3.0) * 9.0 / 3.0, CollisionOutcomes.SpecificImpactEnergy(2f, 1f, 3f), 1e-5);
    }

    [Fact]
    public void QStarRdEqualMass_IsGravityRegimePrincipalCurve()
    {
        // Eq. 28 with G = 1: Q* = c* (4/5) pi rho R_C1^2, c* = 1.9, R_C1 from M_tot at rho.
        float mtot = 2f, rho = 1.5f;
        double rc1 = Math.Cbrt(3.0 * mtot / (4.0 * Math.PI * rho));
        double expected = 1.9 * 0.8 * Math.PI * rho * rc1 * rc1;
        Assert.Equal(expected, CollisionOutcomes.QStarRdEqualMass(mtot, rho), expected * 1e-5);
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.1, 2.567479)] // ((1.1)^2 / 0.4)^(2/(3*0.36) - 1)
    [InlineData(0.5, 1.105540)] // (2.25/2)^(0.851852)
    public void MassRatioCorrection_FollowsEq23WithMuBar0point36(double gamma, double expected)
    {
        Assert.Equal(expected, CollisionOutcomes.MassRatioFactor((float)gamma), 1e-4);
    }

    [Fact]
    public void CriticalImpactParameter_IsTargetRadiusOverRadiusSum()
    {
        Assert.Equal(0.75f, CollisionOutcomes.CriticalImpactParameter(3f, 1f), 6);
    }

    [Fact]
    public void InteractingFraction_FollowsEq11()
    {
        float rt = 1f, rp = 0.5f; // R + r = 1.5
        Assert.Equal(1f, CollisionOutcomes.InteractingFraction(rt, rp, 0f), 6);
        // l = (R + r)(1 - b) = r  -> alpha = (3 r^3 - r^3) / (4 r^3) = 0.5
        Assert.Equal(0.5f, CollisionOutcomes.InteractingFraction(rt, rp, 1f - rp / 1.5f), 5);
        Assert.Equal(0f, CollisionOutcomes.InteractingFraction(rt, rp, 1f), 6);
        // l = 0.25: (3*0.5*0.0625 - 0.015625) / (4*0.125) = 0.15625
        Assert.Equal(0.15625f, CollisionOutcomes.InteractingFraction(rt, rp, 1f - 0.25f / 1.5f), 5);
    }

    [Fact]
    public void AngleCorrection_IsOneHeadOn_AndRaisesTheThresholdWhenGrazing()
    {
        float mt = 1f, mp = 0.5f, rt = RadiusAtDensity(mt), rp = RadiusAtDensity(mp);
        float rho = (mt + mp) / (4f / 3f * MathF.PI * (rt * rt * rt + rp * rp * rp));
        float headOn = CollisionOutcomes.QPrimeStarRd(mt, mp, rt, rp, 0f);
        double expectedHeadOn = CollisionOutcomes.QStarRdEqualMass(mt + mp, rho) * CollisionOutcomes.MassRatioFactor(0.5f);
        Assert.Equal(expectedHeadOn, headOn, expectedHeadOn * 1e-5);

        // Eq. 15: Q' = (mu / mu_alpha)^(2 - 3 mu_bar / 2) Q*.
        float b = 0.8f;
        float alpha = CollisionOutcomes.InteractingFraction(rt, rp, b);
        double mu = mt * mp / (mt + mp), muA = alpha * mp * mt / (alpha * mp + mt);
        double expected = Math.Pow(mu / muA, 2 - 1.5 * 0.36) * expectedHeadOn;
        float grazing = CollisionOutcomes.QPrimeStarRd(mt, mp, rt, rp, b);
        Assert.Equal(expected, grazing, expected * 1e-4);
        Assert.True(grazing > headOn);
    }

    [Fact]
    public void MergeEscapeSpeed_UsesTargetPlusInteractingMass_Eq53()
    {
        float mt = 1f, mp = 1f, rt = RadiusAtDensity(mt), rp = RadiusAtDensity(mp);
        // Head-on: M' = M_tot and R' = radius of the combined volume.
        double expected = Math.Sqrt(2.0 * 2.0 / Math.Cbrt(rt * rt * rt + rp * rp * rp));
        Assert.Equal(expected, CollisionOutcomes.MergeEscapeSpeed(mt, mp, rt, rp, 0f), expected * 1e-5);
        // Grazing: only part of the projectile counts, so V'_esc is lower.
        Assert.True(CollisionOutcomes.MergeEscapeSpeed(mt, mp, rt, rp, 0.9f) < (float)expected);
    }

    [Fact]
    public void ImpactParameter_IsPerpendicularOffsetOverRadiusSum()
    {
        var d = new Vector3(0.3f, 0.4f, 0f);
        var vRel = new Vector3(-2f, 0f, 0f);
        Assert.Equal(0.4f / 0.5f, CollisionOutcomes.ImpactParameter(d, vRel, 0.5f), 6);
        Assert.Equal(0f, CollisionOutcomes.ImpactParameter(new Vector3(0.4f, 0, 0), vRel, 0.5f), 6);
        Assert.Equal(0f, CollisionOutcomes.ImpactParameter(d, Vector3.Zero, 0.5f), 6);
    }

    // ---- classification ----------------------------------------------------------------------

    // Equal masses (1 + 1) at density 1: head-on Q'* = 2.918, Q_R = V^2 / 8, V'_esc = 2.262.
    private static CollisionResult Classify(float vi, float b, float mt = 1f, float mp = 1f)
    {
        float rt = RadiusAtDensity(mt), rp = RadiusAtDensity(mp), s = rt + rp;
        float off = b * s;
        var d = new Vector3(MathF.Sqrt(MathF.Max(0f, (0.999f * s) * (0.999f * s) - off * off)), off, 0f);
        var vRel = new Vector3(-vi, 0f, 0f);
        return CollisionOutcomes.Classify(mt, mp, rt, rp, d, vRel);
    }

    [Fact]
    public void BelowMergeEscapeSpeed_IsPerfectMerge()
    {
        var r = Classify(vi: 2.0f, b: 0f);
        Assert.Equal(CollisionOutcome.PerfectMerge, r.Outcome);
        Assert.Equal(2f, r.LargestRemnant, 5);
    }

    [Fact]
    public void HeadOn_ModerateSpeed_IsPartialAccretion()
    {
        var r = Classify(vi: 3.0f, b: 0f);
        Assert.Equal(CollisionOutcome.PartialAccretion, r.Outcome);
        Assert.True(r.LargestRemnant >= 1f && r.LargestRemnant < 2f, $"M_lr {r.LargestRemnant}");
        Assert.Equal(2f * (1f - r.QR / (2f * r.QPrimeStar)), r.LargestRemnant, 4);
    }

    [Fact]
    public void HeadOn_FastSpeed_IsErosion()
    {
        var r = Classify(vi: 6.0f, b: 0f);
        Assert.Equal(CollisionOutcome.Erosion, r.Outcome);
        Assert.True(r.LargestRemnant < 1f && r.LargestRemnant >= 0.2f, $"M_lr {r.LargestRemnant}");
    }

    [Fact]
    public void HeadOn_VeryFastSpeed_IsCatastrophicDisruption_OnTheSuperCatastrophicLaw()
    {
        var r = Classify(vi: 10.0f, b: 0f);
        Assert.Equal(CollisionOutcome.Disruption, r.Outcome);
        float ratio = r.QR / r.QPrimeStar;
        Assert.True(ratio > 1.8f);
        Assert.Equal(2f * 0.1f / MathF.Pow(1.8f, -1.5f) * MathF.Pow(ratio, -1.5f), r.LargestRemnant, 4);
        Assert.True(r.LargestRemnant < 0.2f);
    }

    [Fact]
    public void Grazing_AtTheSameSpeed_IsHitAndRun_OnlyAboveBCrit()
    {
        float bCrit = 0.5f; // equal radii
        Assert.Equal(CollisionOutcome.PartialAccretion, Classify(vi: 3.0f, b: 0f).Outcome);
        Assert.Equal(CollisionOutcome.PartialAccretion, Classify(vi: 3.0f, b: bCrit - 0.05f).Outcome);
        var hnr = Classify(vi: 3.0f, b: 0.8f);
        Assert.Equal(CollisionOutcome.HitAndRun, hnr.Outcome);
        Assert.Equal(1f, hnr.LargestRemnant, 5);       // the target keeps its mass
        Assert.True(hnr.ProjectileRemnant <= 1f && hnr.ProjectileRemnant > 0f);
    }

    [Fact]
    public void Grazing_PastTheErosionThreshold_ErodesTheTarget()
    {
        var r = Classify(vi: 30f, b: 0.8f);
        Assert.True(r.Outcome == CollisionOutcome.Erosion || r.Outcome == CollisionOutcome.Disruption, r.Outcome.ToString());
        Assert.True(r.LargestRemnant < 1f);
    }

    [Fact]
    public void HitAndRun_ReverseImpact_SparesAProjectileThatBarelyTouches_AndDamagesOneThatDigsIn()
    {
        float mt = 1f, mp = 0.1f;
        var glancing = Classify(vi: 4f, b: 0.98f, mt: mt, mp: mp);
        var deep = Classify(vi: 4f, b: 0.75f, mt: mt, mp: mp);
        Assert.Equal(CollisionOutcome.HitAndRun, glancing.Outcome);
        Assert.Equal(CollisionOutcome.HitAndRun, deep.Outcome);
        Assert.True(glancing.ProjectileRemnant > deep.ProjectileRemnant,
            $"glancing {glancing.ProjectileRemnant} vs deep {deep.ProjectileRemnant}");
        Assert.True(deep.ProjectileRemnant < mp);
    }

    [Fact]
    public void ReverseInteractingMass_FollowsEqs46to48()
    {
        float rt = 1f, rp = 0.5f, rhoT = 2f;
        float b = 1f - 0.25f / 1.5f; // l = 0.25
        double l = 0.25, phi = 2 * Math.Acos((l - rp) / rp);
        double area = rp * rp * (Math.PI - (phi - Math.Sin(phi)) / 2);
        double len = 2 * Math.Sqrt(rt * rt - (rt - l / 2) * (rt - l / 2));
        Assert.Equal(rhoT * area * len, CollisionOutcomes.ReverseInteractingMass(rt, rp, b, rhoT), 1e-4 * rhoT * area * len);
    }

    // ---- fragment split ----------------------------------------------------------------------

    [Fact]
    public void Split_AtMost8_NoneBelowM0_DescendingAndSumsToDebris()
    {
        Span<float> m = stackalloc float[CollisionOutcomes.MaxFragments];
        int n = CollisionOutcomes.SplitFragments(1.0f, 0.01f, 100, m);
        Assert.Equal(8, n);
        float sum = 0f;
        for (int k = 0; k < n; k++)
        {
            Assert.True(m[k] >= 0.01f);
            if (k > 0) Assert.True(m[k] <= m[k - 1]);
            sum += m[k];
        }
        Assert.Equal(1.0f, sum, 5);
    }

    [Fact]
    public void Split_DebrisBelowM0_MakesNoFragments()
    {
        Span<float> m = stackalloc float[CollisionOutcomes.MaxFragments];
        Assert.Equal(0, CollisionOutcomes.SplitFragments(0.009f, 0.01f, 100, m));
    }

    [Fact]
    public void Split_FewGrainsOfDebris_UsesFewerFragments_AllAtLeastM0()
    {
        Span<float> m = stackalloc float[CollisionOutcomes.MaxFragments];
        int n = CollisionOutcomes.SplitFragments(0.035f, 0.01f, 100, m);
        Assert.InRange(n, 1, 3);
        float sum = 0f;
        for (int k = 0; k < n; k++) { Assert.True(m[k] >= 0.01f); sum += m[k]; }
        Assert.Equal(0.035f, sum, 6);
    }

    [Fact]
    public void Split_CeilingReducesCount_ExcessGoesToTheLargestFragment()
    {
        Span<float> full = stackalloc float[CollisionOutcomes.MaxFragments];
        Span<float> m = stackalloc float[CollisionOutcomes.MaxFragments];
        int nFull = CollisionOutcomes.SplitFragments(1f, 0.01f, 100, full);
        int n = CollisionOutcomes.SplitFragments(1f, 0.01f, 3, m);
        Assert.Equal(3, n);
        Assert.Equal(full[1], m[1], 6);
        Assert.Equal(full[2], m[2], 6);
        Assert.Equal(1f - full[1] - full[2], m[0], 5);
        Assert.Equal(0, CollisionOutcomes.SplitFragments(1f, 0.01f, 0, m));
    }
}
