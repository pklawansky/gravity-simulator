using GravitySimulator.Classic;

namespace GravitySimulator.Tests;

/// <summary>The classic simulator's real-time pace: steps scale with real time at the GDI pace, capped, never negative.</summary>
public class ClassicPaceTests
{
    [Fact]
    public void Step_IsRealTimeTimesPace()
    {
        Assert.Equal(2.2f, ClassicPace.SimSecondsPerRealSecond);
        Assert.Equal(0.1f / 3f * 2.2f, ClassicPace.Step(0.1 / 3, 0.4f), 5);   // a 30 ticks/s frame
        Assert.Equal(0.22f, ClassicPace.Step(0.1, 0.4f), 5);
    }

    [Fact]
    public void Step_AtGdiTickRate_EqualsTheOldFixedStep()
    {
        // GDI ran about 5.5 ticks/s at 0.4 s per tick; at that tick interval the paced step is the same 0.4 s.
        Assert.Equal(0.4f, ClassicPace.Step(0.4 / 2.2, 0.4f), 4);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(3.0)]
    [InlineData(1e9)]
    public void Step_StalledFrame_IsCapped(double realSeconds) => Assert.Equal(0.4f, ClassicPace.Step(realSeconds, 0.4f));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void Step_NoTimeOrBadTime_IsZero(double realSeconds) => Assert.Equal(0f, ClassicPace.Step(realSeconds, 0.4f));

    [Fact]
    public void ThirtyTwoTicks_AdvanceAboutOneSecondTimesPace()
    {
        double sim = 0;
        for (int i = 0; i < 32; i++) sim += ClassicPace.Step(1.0 / 32, 0.4f);
        Assert.Equal(2.2, sim, 3);
    }
}
