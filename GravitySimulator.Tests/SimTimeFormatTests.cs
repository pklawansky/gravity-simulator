using GravitySimulator.Planets;
using GravitySimulator.Sph;

namespace GravitySimulator.Tests;

/// <summary>The simulated elapsed-time counter: sim time -> physical units, auto-scaled.</summary>
public class SimTimeFormatTests
{
    [Theory]
    [InlineData(0.0, "0.00 s")]
    [InlineData(4.2, "4.20 s")]
    [InlineData(45.0, "45.0 s")]
    [InlineData(150.0, "2.50 min")]
    [InlineData(5400.0, "1.50 hours")]
    [InlineData(3.2 * 86400, "3.20 days")]
    [InlineData(400.0 * 86400, "1.10 yr")]
    [InlineData(12_345.0 * 3.15576e7, "12.3 kyr")]
    [InlineData(1.45e6 * 3.15576e7, "1.45 Myr")]
    [InlineData(2.5e9 * 3.15576e7, "2.50 Gyr")]
    public void FormatSeconds_AutoScales(double seconds, string expected) =>
        Assert.Equal(expected, SimTimeFormat.FormatSeconds(seconds));

    [Fact]
    public void Rounding_DoesNotOverflowTheDigits()
    {
        Assert.Equal("10.0 s", SimTimeFormat.FormatSeconds(9.999));
        Assert.Equal("100 yr", SimTimeFormat.FormatSeconds(99.97 * 3.15576e7));
        Assert.Equal("0.00 s", SimTimeFormat.FormatSeconds(double.NaN));
    }

    [Fact]
    public void SolarAu_TimeUnitIsAYearOverTwoPi()
    {
        // G = 1 with M = M☉ and L = AU: one orbit of 1 AU (2π sim units) is one year.
        Assert.Equal("1.00 yr", SimTimeFormat.Format(2 * Math.PI, UnitSystem.SolarAu));
        Assert.Equal("3.20 days", SimTimeFormat.Format(2 * Math.PI * 3.2 / 365.25, UnitSystem.SolarAu));
    }

    [Fact]
    public void SolarParsec_TimeUnitIsAbout14_9Myr()
    {
        double unitMyr = UnitSystem.SolarParsec.TimeS / 3.15576e13;
        Assert.InRange(unitMyr, 14.8, 15.0);
        Assert.Equal("1.49 Myr", SimTimeFormat.Format(0.1, UnitSystem.SolarParsec));
        Assert.Equal("298 kyr", SimTimeFormat.Format(0.02, UnitSystem.SolarParsec));
    }

    [Fact]
    public void SolarRadius_TimeUnitIsAbout26_5Minutes()
    {
        // The tidal-disruption units: sqrt(R☉³ / (G M☉)) ≈ 1593 s.
        Assert.Equal("26.5 min", SimTimeFormat.Format(1.0, SphPresets.SolarRadiusUnits));
        Assert.Equal("15.9 s", SimTimeFormat.Format(0.01, SphPresets.SolarRadiusUnits));
    }
}
