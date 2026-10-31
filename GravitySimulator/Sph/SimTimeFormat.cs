using System;
using System.Globalization;
using GravitySimulator.Planets;

namespace GravitySimulator.Sph
{
    /// <summary>
    /// Simulated (world) time in physical units, auto-scaled: s, min, hours, days, yr, kyr, Myr, Gyr,
    /// three significant figures ("3.20 days", "1.45 Myr"). Sim time converts through the preset's
    /// <see cref="UnitSystem.TimeS"/> (the time unit that makes G = 1).
    /// </summary>
    public static class SimTimeFormat
    {
        private const double Minute = 60, Hour = 3600, Day = 86400, Year = UnitSystem.YearSeconds;

        /// <summary>Seconds of world time for <paramref name="simTime"/> in <paramref name="units"/>.</summary>
        public static double ToSeconds(double simTime, UnitSystem units) => simTime * units.TimeS;

        /// <summary>"3.20 days" for <paramref name="simTime"/> in <paramref name="units"/>.</summary>
        public static string Format(double simTime, UnitSystem units) => FormatSeconds(ToSeconds(simTime, units));

        public static string FormatSeconds(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
            if (seconds < Minute) return Number(seconds) + " s";
            if (seconds < Hour) return Number(seconds / Minute) + " min";
            if (seconds < Day) return Number(seconds / Hour) + " hours";
            if (seconds < Year) return Number(seconds / Day) + " days";
            double years = seconds / Year;
            if (years < 1e3) return Number(years) + " yr";
            if (years < 1e6) return Number(years / 1e3) + " kyr";
            if (years < 1e9) return Number(years / 1e6) + " Myr";
            return Number(years / 1e9) + " Gyr";
        }

        // Three significant figures without exponent (values here are 0 .. 999.5).
        private static string Number(double v)
        {
            string format = v < 9.995 ? "0.00" : v < 99.95 ? "0.0" : "0";
            return v.ToString(format, CultureInfo.InvariantCulture);
        }
    }
}
