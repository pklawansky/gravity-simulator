using System;

namespace GravitySimulator.Planets
{
    /// <summary>
    /// What a body is, for the sub-grid stellar model. <see cref="Planet"/> is the zero default, so
    /// with the stellar model off every body is a Planet and nothing class-dependent happens.
    /// </summary>
    public enum BodyClass : byte
    {
        /// <summary>Non-degenerate body below the fusion limit (dust, rock, planet, brown dwarf).</summary>
        Planet = 0,
        /// <summary>Gas particle or clump (cosmic presets).</summary>
        Gas = 1,
        Star = 2,
        WhiteDwarf = 3,
        NeutronStar = 4,
        BlackHole = 5,
    }

    /// <summary>Outcome of a star reaching the end of its main-sequence life.</summary>
    public readonly struct EndOfLifeResult
    {
        public EndOfLifeResult(BodyClass remnant, float remnantMass, bool supernova)
        {
            Remnant = remnant;
            RemnantMass = remnantMass;
            Supernova = supernova;
        }

        public BodyClass Remnant { get; }
        public float RemnantMass { get; }
        public bool Supernova { get; }
    }

    /// <summary>
    /// The sub-grid stellar rules (masses in M☉): classification thresholds, lifetimes,
    /// luminosities, end states, and how classes combine in a merge. Pure functions, no units.
    /// </summary>
    public static class StellarPhysics
    {
        /// <summary>Hydrogen fusion ignites at or above this mass.</summary>
        public const float FusionMass = 0.08f;
        /// <summary>Chandrasekhar limit: white dwarfs above it collapse to neutron stars.</summary>
        public const float ChandrasekharMass = 1.4f;
        /// <summary>Tolman-Oppenheimer-Volkoff limit: neutron stars above it collapse to black holes.</summary>
        public const float TovMass = 2.5f;
        /// <summary>Stars at or above this mass end in a core-collapse supernova.</summary>
        public const float SupernovaMass = 8f;
        /// <summary>Stars at or above this mass leave a black hole.</summary>
        public const float BlackHoleProgenitorMass = 20f;
        public const float NeutronStarBirthMass = 1.4f;
        public const float BlackHoleMassFraction = 0.4f;
        public const double SolarLifetimeYears = 1e10;

        public static BodyClass ClassifyNonDegenerate(float mass) => mass >= FusionMass ? BodyClass.Star : BodyClass.Planet;

        public static BodyClass ClassifyRemnant(float mass) =>
            mass <= ChandrasekharMass ? BodyClass.WhiteDwarf : mass <= TovMass ? BodyClass.NeutronStar : BodyClass.BlackHole;

        /// <summary>Main-sequence lifetime t = 10 Gyr * M^-2.5.</summary>
        public static double LifetimeYears(float mass) => SolarLifetimeYears * Math.Pow(Math.Max(mass, 1e-6f), -2.5);

        /// <summary>L = L☉ * M^3.5, in solar luminosities.</summary>
        public static double LuminositySolar(float mass) => Math.Pow(Math.Max(mass, 0f), 3.5);

        public static EndOfLifeResult EndOfLife(float mass)
        {
            if (mass < SupernovaMass)
            {
                float wd = MathF.Min(mass, MathF.Min(0.6f + 0.1f * (mass - 1f), ChandrasekharMass));
                return new EndOfLifeResult(BodyClass.WhiteDwarf, wd, false);
            }
            if (mass < BlackHoleProgenitorMass)
                return new EndOfLifeResult(BodyClass.NeutronStar, NeutronStarBirthMass, true);
            return new EndOfLifeResult(BodyClass.BlackHole, mass * BlackHoleMassFraction, true);
        }

        // Which class wins a merge: compact objects swallow everything, stars swallow planets and gas.
        private static int Rank(BodyClass c) => c switch
        {
            BodyClass.Gas => 0,
            BodyClass.Planet => 1,
            BodyClass.Star => 2,
            BodyClass.WhiteDwarf => 3,
            BodyClass.NeutronStar => 4,
            _ => 5,
        };

        /// <summary>
        /// Class and age of a merge product (re-classification by mass happens afterwards in the
        /// stellar evolution step). Star + star keeps the mass-weighted age; anything merging into a
        /// compact object keeps the compact object's class.
        /// </summary>
        public static (BodyClass Class, float Age) MergeClass(BodyClass a, float ageA, float massA, BodyClass b, float ageB, float massB)
        {
            BodyClass c = Rank(a) >= Rank(b) ? a : b;
            if (c != BodyClass.Star) return (c, 0f);
            if (a == BodyClass.Star && b == BodyClass.Star)
            {
                float m = massA + massB;
                return (c, m > 0f ? (ageA * massA + ageB * massB) / m : 0f);
            }
            return (c, a == BodyClass.Star ? ageA : ageB);
        }
    }
}
