using System;
using System.Numerics;

namespace GravitySimulator.Planets
{
    /// <summary>Which collision rule <see cref="CollisionResolver"/> applies to rocky (Planet-Planet) pairs.</summary>
    public enum CollisionModel
    {
        /// <summary>Leinhardt &amp; Stewart (2012) outcome regimes with real fragments (see <see cref="CollisionOutcomes"/>).</summary>
        Outcomes,
        /// <summary>The original rule: merge below k x escape speed, otherwise bounce with restitution e.</summary>
        MergeOrBounce,
    }

    /// <summary>Collision outcome regimes, in the order they are tested.</summary>
    public enum CollisionOutcome : byte
    {
        PerfectMerge,
        PartialAccretion,
        HitAndRun,
        Erosion,
        /// <summary>Catastrophic disruption (largest remnant below 10% of the total; super-catastrophic law).</summary>
        Disruption,
    }

    /// <summary>The verdict for one contact. Masses are absolute (same units as the inputs).</summary>
    public readonly struct CollisionResult
    {
        public CollisionResult(CollisionOutcome outcome, float largestRemnant, float projectileRemnant, float b, bool grazing, float qr, float qPrimeStar, float vEscPrime)
        {
            Outcome = outcome;
            LargestRemnant = largestRemnant;
            ProjectileRemnant = projectileRemnant;
            B = b;
            Grazing = grazing;
            QR = qr;
            QPrimeStar = qPrimeStar;
            VEscPrime = vEscPrime;
        }

        public CollisionOutcome Outcome { get; }
        /// <summary>Largest remnant M_lr (the target's mass in a hit-and-run).</summary>
        public float LargestRemnant { get; }
        /// <summary>Hit-and-run only: mass the projectile keeps (second largest remnant). Otherwise 0.</summary>
        public float ProjectileRemnant { get; }
        /// <summary>Impact parameter b = sin(theta).</summary>
        public float B { get; }
        public bool Grazing { get; }
        /// <summary>Specific impact energy Q_R (LS12 eq. 1).</summary>
        public float QR { get; }
        /// <summary>Angle- and mass-ratio-corrected catastrophic disruption threshold Q'*_RD (LS12 eq. 15).</summary>
        public float QPrimeStar { get; }
        /// <summary>Merging escape speed V'_esc (LS12 eq. 53).</summary>
        public float VEscPrime { get; }
    }

    /// <summary>
    /// Pure maths of the gravity-regime collision outcome model of Leinhardt &amp; Stewart 2012,
    /// ApJ 745:79 ("LS12"), with G = 1 and no material strength. Equation numbers refer to LS12.
    /// Target = heavier body (mass M_t, radius R_t), projectile = lighter (M_p, R_p).
    /// <para>
    /// Deviation forced by the unitless model: LS12 evaluates R_C1 at a fixed reference density
    /// rho_1 = 1000 kg/m^3; there are no physical units here, so R_C1 and R' use the pair's mean
    /// bulk density instead (R_C1 = radius of the combined volume).
    /// </para>
    /// </summary>
    public static class CollisionOutcomes
    {
        /// <summary>c* for hydrodynamic planet-size bodies (LS12 §3.1, eq. 28 fit: 1.9 ± 0.3).</summary>
        public const float CStar = 1.9f;
        /// <summary>Velocity exponent mu-bar for planet-size bodies (LS12: 0.36 ± 0.01).</summary>
        public const float MuBar = 0.36f;
        /// <summary>Super-catastrophic power-law slope (LS12 eq. 44: eta ~ -1.5).</summary>
        public const float Eta = -1.5f;
        /// <summary>Q_R / Q'*_RD above which the universal law gives M_lr &lt; 0.1 M_tot (LS12 §4.1).</summary>
        public const float SuperCatastrophicRatio = 1.8f;
        /// <summary>At most this many fragments per collision.</summary>
        public const int MaxFragments = 8;
        /// <summary>Cumulative fragment size-distribution slope beta (LS12 eq. 31; appendix value 2.85).</summary>
        public const float Beta = 2.85f;

        // m_k ~ k^(-3/beta): the k-th largest fragment of a N(>D) ~ D^-beta distribution.
        private static readonly float[] SplitWeights = BuildSplitWeights();

        private static float[] BuildSplitWeights()
        {
            var w = new float[MaxFragments];
            for (int k = 0; k < MaxFragments; k++) w[k] = MathF.Pow(k + 1, -3f / Beta);
            return w;
        }

        /// <summary>LS12 eq. 1: Q_R = 0.5 mu V_i^2 / M_tot, mu = M_t M_p / M_tot.</summary>
        public static float SpecificImpactEnergy(float mt, float mp, float vi)
        {
            double mtot = (double)mt + mp, mu = (double)mt * mp / mtot;
            return (float)(0.5 * mu * vi * vi / mtot);
        }

        /// <summary>b = sin(theta) = |d x v_hat| / (R_t + R_p), clamped to [0, 1]; 0 when there is no relative motion.</summary>
        public static float ImpactParameter(Vector3 d, Vector3 vRel, float radiusSum)
        {
            float v = vRel.Length();
            if (v <= 0f || radiusSum <= 0f) return 0f;
            float b = Vector3.Cross(d, vRel / v).Length() / radiusSum;
            return Math.Clamp(b, 0f, 1f);
        }

        /// <summary>LS12 eq. 6: b_crit = R_t / (R_t + R_p). Grazing when b &gt; b_crit.</summary>
        public static float CriticalImpactParameter(float rt, float rp) => rt / (rt + rp);

        /// <summary>LS12 eqs. 7 and 11: l = (R_t + R_p)(1 - b); alpha = (3 r l^2 - l^3) / (4 r^3), 1 when l &gt;= 2r.</summary>
        public static float InteractingFraction(float rt, float rp, float b)
        {
            double r = rp, l = ((double)rt + rp) * (1.0 - b);
            if (l >= 2.0 * r) return 1f;
            if (l <= 0.0) return 0f;
            return (float)Math.Clamp((3.0 * r * l * l - l * l * l) / (4.0 * r * r * r), 0.0, 1.0);
        }

        /// <summary>LS12 eq. 28 (G = 1): Q*_RD,gamma=1 = c* (4/5) pi rho R_C1^2, with R_C1 the radius of M_tot at density rho.</summary>
        public static float QStarRdEqualMass(float mtot, float rho)
        {
            double rc1 = Math.Cbrt(3.0 * mtot / (4.0 * Math.PI * rho));
            return (float)(CStar * 0.8 * Math.PI * rho * rc1 * rc1);
        }

        /// <summary>LS12 eq. 23: Q*_RD / Q*_RD,gamma=1 = [(gamma + 1)^2 / (4 gamma)]^(2/(3 mu-bar) - 1).</summary>
        public static float MassRatioFactor(float gamma)
        {
            double g = gamma;
            return (float)Math.Pow((g + 1.0) * (g + 1.0) / (4.0 * g), 2.0 / (3.0 * MuBar) - 1.0);
        }

        /// <summary>Mean bulk density of the pair: M_tot / (4/3 pi (R_t^3 + R_p^3)).</summary>
        public static float MeanDensity(float mt, float mp, float rt, float rp) =>
            (float)(((double)mt + mp) / (4.0 / 3.0 * Math.PI * ((double)rt * rt * rt + (double)rp * rp * rp)));

        /// <summary>
        /// Q'*_RD: eq. 28 at the mean density, corrected for mass ratio (eq. 23) and for impact angle via
        /// the interacting mass (eq. 12, mu_alpha = alpha M_p M_t / (alpha M_p + M_t); eq. 15,
        /// Q'*_RD = (mu / mu_alpha)^(2 - 3 mu-bar / 2) Q*_RD).
        /// </summary>
        public static float QPrimeStarRd(float mt, float mp, float rt, float rp, float b)
        {
            float rho = MeanDensity(mt, mp, rt, rp);
            double q = (double)QStarRdEqualMass(mt + mp, rho) * MassRatioFactor(mp / mt);
            double alpha = InteractingFraction(rt, rp, b);
            if (alpha >= 1.0) return (float)q;
            if (alpha <= 0.0) return float.PositiveInfinity; // nothing of the projectile hits the target
            double mu = (double)mt * mp / ((double)mt + mp);
            double muA = alpha * mp * mt / (alpha * mp + mt);
            return (float)(Math.Pow(mu / muA, 2.0 - 1.5 * MuBar) * q);
        }

        /// <summary>LS12 eq. 53: V'_esc = sqrt(2 M' / R'), M' = M_t + alpha M_p, R' = radius of M' at the mean density.</summary>
        public static float MergeEscapeSpeed(float mt, float mp, float rt, float rp, float b)
        {
            double rho = MeanDensity(mt, mp, rt, rp);
            double m = mt + (double)InteractingFraction(rt, rp, b) * mp;
            double r = Math.Cbrt(3.0 * m / (4.0 * Math.PI * rho));
            return (float)Math.Sqrt(2.0 * m / r);
        }

        /// <summary>
        /// LS12 eq. 5 (universal law) below Q_R/Q'*_RD = 1.8: M_lr/M_tot = 1 - Q_R / (2 Q'*_RD);
        /// eq. 44 above it: M_lr/M_tot = (0.1 / 1.8^eta) (Q_R / Q'*_RD)^eta, eta = -1.5.
        /// </summary>
        public static float LargestRemnantFraction(float qOverQStar)
        {
            double x = Math.Max(0f, qOverQStar);
            if (x <= SuperCatastrophicRatio) return (float)(1.0 - 0.5 * x);
            return (float)(0.1 / Math.Pow(SuperCatastrophicRatio, Eta) * Math.Pow(x, Eta));
        }

        /// <summary>
        /// LS12 eqs. 46-48: mass of the target that the projectile ploughs through in a grazing hit,
        /// used as the "projectile" of the reverse impact. phi = 2 acos((l - r)/r),
        /// A = r^2 (pi - (phi - sin phi)/2), L = 2 sqrt(R^2 - (R - l/2)^2), M = rho_t A L
        /// (LS12 writes M = A L "of order"; the target density turns the volume into a mass).
        /// </summary>
        public static float ReverseInteractingMass(float rt, float rp, float b, float rhoTarget)
        {
            double r = rp, big = rt;
            double l = Math.Clamp((big + r) * (1.0 - b), 0.0, Math.Min(2.0 * r, 2.0 * big));
            if (l <= 0.0) return 0f;
            double phi = 2.0 * Math.Acos(Math.Clamp((l - r) / r, -1.0, 1.0));
            double area = r * r * (Math.PI - (phi - Math.Sin(phi)) / 2.0);
            double h = big - l / 2.0;
            double len = 2.0 * Math.Sqrt(Math.Max(0.0, big * big - h * h));
            return (float)(rhoTarget * area * len);
        }

        /// <summary>
        /// Hit-and-run fate of the projectile via the reverse impact (LS12 §4.2, eqs. 49-52, appendix step 12):
        /// M†_p = M_interact (capped at M_t), M†_targ = M_p, mu† and gamma† (eqs. 49-50), Q†*_RD from
        /// eq. 28 at R†_C1 and the mass-ratio correction (eq. 52), Q†_R = 0.5 mu† V_i^2 / M†_tot, then
        /// eq. 5 or 44 for the largest remnant of M†_tot. The projectile keeps min(M_p, that remnant):
        /// in this model a hit-and-run never adds target material to the projectile.
        /// </summary>
        public static float HitAndRunProjectileRemnant(float mt, float mp, float rt, float rp, float b, float vi)
        {
            float rho = MeanDensity(mt, mp, rt, rp);
            float rhoT = (float)(mt / (4.0 / 3.0 * Math.PI * (double)rt * rt * rt));
            double mi = Math.Min(ReverseInteractingMass(rt, rp, b, rhoT), mt);
            if (mi <= 0.0) return mp;
            double mtot = mi + mp, mu = mi * mp / mtot;
            double gamma = mi / mp;
            double qStar = (double)QStarRdEqualMass((float)mtot, rho) * MassRatioFactor((float)gamma);
            double qr = 0.5 * mu * vi * vi / mtot;
            double lr = LargestRemnantFraction((float)(qr / qStar)) * mtot;
            return (float)Math.Min(mp, lr);
        }

        /// <summary>
        /// Classifies one contact (LS12 appendix A.1 order): perfect merge if V_i &lt; V'_esc (eq. 53);
        /// otherwise M_lr from Q_R and Q'*_RD (eqs. 1, 15, 5/44); grazing (b &gt; b_crit, eq. 6) with
        /// M_lr &gt; M_t (Q_R below the target's erosion threshold) is hit-and-run; non-grazing with
        /// M_lr &gt;= M_t is partial accretion; M_lr &gt;= 0.1 M_tot erosion; below that, catastrophic
        /// disruption. <paramref name="d"/> is the separation and <paramref name="vRel"/> the relative
        /// velocity at contact (either sign convention: only magnitudes enter).
        /// </summary>
        public static CollisionResult Classify(float mt, float mp, float rt, float rp, Vector3 d, Vector3 vRel)
        {
            if (mp > mt) { (mt, mp) = (mp, mt); (rt, rp) = (rp, rt); }
            float mtot = mt + mp;
            float vi = vRel.Length();
            float b = ImpactParameter(d, vRel, rt + rp);
            float vEscPrime = MergeEscapeSpeed(mt, mp, rt, rp, b);
            bool grazing = b > CriticalImpactParameter(rt, rp);
            if (vi < vEscPrime)
                return new CollisionResult(CollisionOutcome.PerfectMerge, mtot, 0f, b, grazing, 0f, 0f, vEscPrime);

            float qr = SpecificImpactEnergy(mt, mp, vi);
            float qStar = QPrimeStarRd(mt, mp, rt, rp, b);
            float lr = LargestRemnantFraction(qr / qStar) * mtot;
            CollisionOutcome outcome;
            if (grazing && lr > mt)
            {
                float pr = HitAndRunProjectileRemnant(mt, mp, rt, rp, b, vi);
                return new CollisionResult(CollisionOutcome.HitAndRun, mt, pr, b, grazing, qr, qStar, vEscPrime);
            }
            if (!grazing && lr >= mt) outcome = CollisionOutcome.PartialAccretion;
            else if (lr >= 0.1f * mtot) outcome = CollisionOutcome.Erosion;
            else outcome = CollisionOutcome.Disruption;
            return new CollisionResult(outcome, MathF.Min(lr, mtot), 0f, b, grazing, qr, qStar, vEscPrime);
        }

        /// <summary>
        /// Splits <paramref name="debris"/> into at most <see cref="MaxFragments"/> fragments, largest first,
        /// with masses m_k ~ k^(-3/beta) (the k-th largest body of LS12's eq. 31 size distribution) and none
        /// lighter than <paramref name="minMass"/>. If <paramref name="maxCount"/> (the particle ceiling) is
        /// smaller, the smallest fragments are dropped and their mass goes into the largest. Returns the
        /// count (0 = the debris goes back into the remnant). Masses sum to <paramref name="debris"/>.
        /// </summary>
        public static int SplitFragments(float debris, float minMass, int maxCount, Span<float> masses)
        {
            if (maxCount <= 0 || !(debris > 0f) || debris < minMass) return 0;
            int n = minMass > 0f ? (int)Math.Min(MaxFragments, Math.Floor(debris / minMass)) : MaxFragments;
            n = Math.Max(1, n);
            float sum = 0f;
            for (int k = 0; k < n; k++) sum += SplitWeights[k];
            while (n > 1 && debris * SplitWeights[n - 1] / sum < minMass)
            {
                sum -= SplitWeights[n - 1];
                n--;
            }
            for (int k = 0; k < n; k++) masses[k] = debris * SplitWeights[k] / sum;
            if (n > maxCount) n = maxCount;
            float rest = 0f;
            for (int k = 1; k < n; k++) rest += masses[k];
            masses[0] = debris - rest; // largest takes any dropped mass and the rounding
            return n;
        }
    }
}
