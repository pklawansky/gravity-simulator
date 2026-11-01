using System;

namespace GravitySimulator.Planets
{
    /// <summary>
    /// Physical unit system for the cosmic presets: a mass unit (solar masses) and a length unit
    /// (parsec or AU), with the time unit derived so that G = 1. Converts the speed of light, the
    /// solar radius and luminosity, and years into simulation units.
    /// </summary>
    public sealed class UnitSystem
    {
        public const double GravitationalConstant = 6.6743e-11; // m^3 kg^-1 s^-2
        public const double SolarMassKg = 1.98847e30;
        public const double ParsecMetres = 3.0856775814913673e16;
        public const double AuMetres = 1.495978707e11;
        public const double YearSeconds = 3.15576e7; // Julian year
        public const double SpeedOfLightMps = 299792458.0;
        public const double SolarRadiusMetres = 6.957e8;
        public const double SolarLuminosityWatts = 3.828e26;

        /// <summary>Mass in solar masses, length in parsecs (time unit ~14.9 Myr).</summary>
        public static readonly UnitSystem SolarParsec = new UnitSystem("M☉, pc", 1.0, ParsecMetres);

        /// <summary>Mass in solar masses, length in AU (time unit = 1/(2 pi) year).</summary>
        public static readonly UnitSystem SolarAu = new UnitSystem("M☉, AU", 1.0, AuMetres);

        public UnitSystem(string name, double massUnitSolarMasses, double lengthUnitMetres)
        {
            if (!(massUnitSolarMasses > 0) || !(lengthUnitMetres > 0)) throw new ArgumentOutOfRangeException();
            Name = name;
            MassKg = massUnitSolarMasses * SolarMassKg;
            LengthM = lengthUnitMetres;
            TimeS = Math.Sqrt(LengthM * LengthM * LengthM / (GravitationalConstant * MassKg));
            VelocityMps = LengthM / TimeS;
            SpeedOfLight = SpeedOfLightMps / VelocityMps;
            SolarRadius = SolarRadiusMetres / LengthM;
            double powerUnit = MassKg * LengthM * LengthM / (TimeS * TimeS * TimeS);
            SolarLuminosity = SolarLuminosityWatts / powerUnit;
            SolarMass = SolarMassKg / MassKg;
        }

        public string Name { get; }
        public double MassKg { get; }
        public double LengthM { get; }
        public double TimeS { get; }
        public double VelocityMps { get; }

        /// <summary>c in simulation velocity units.</summary>
        public double SpeedOfLight { get; }
        /// <summary>R☉ in simulation length units.</summary>
        public double SolarRadius { get; }
        /// <summary>L☉ in simulation power units (mass length^2 / time^3).</summary>
        public double SolarLuminosity { get; }
        /// <summary>M☉ in simulation mass units.</summary>
        public double SolarMass { get; }

        public double YearsToSim(double years) => years * YearSeconds / TimeS;
        public double SimToYears(double t) => t * TimeS / YearSeconds;
        public double MetresToSim(double metres) => metres / LengthM;
        public double SimToMetres(double length) => length * LengthM;
        public double KmsToSim(double kms) => kms * 1000.0 / VelocityMps;
        public double SimToKms(double v) => v * VelocityMps / 1000.0;

        public override string ToString() => Name;
    }
}
