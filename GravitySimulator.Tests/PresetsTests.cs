using System.Numerics;
using GravitySimulator.Planets;

namespace GravitySimulator.Tests;

public class PresetsTests
{
    [Theory]
    [InlineData(10_000)]
    [InlineData(1_234)]
    public void Disc_ProducesRequestedCount(int count) =>
        Assert.Equal(count, Presets.DiscAroundStar(count, seed: 1).Count);

    [Theory]
    [InlineData(10_000)]
    [InlineData(1_234)]
    public void Cloud_ProducesRequestedCount(int count) =>
        Assert.Equal(count, Presets.SpinningCloud(count, seed: 1).Count);

    [Fact]
    public void Presets_AreDeterministicPerSeed()
    {
        AssertSame(Presets.DiscAroundStar(5000, 42), Presets.DiscAroundStar(5000, 42));
        AssertSame(Presets.SpinningCloud(5000, 42), Presets.SpinningCloud(5000, 42));
        Assert.NotEqual(Presets.DiscAroundStar(5000, 42).Position[10], Presets.DiscAroundStar(5000, 43).Position[10]);
    }

    [Fact]
    public void Disc_HasNetAngularMomentumAlongPlusZ_AndStarDominatesMass()
    {
        var ps = Presets.DiscAroundStar(20_000, seed: 7);
        Vector3 L = Vector3.Zero;
        double dust = 0;
        int star = 0;
        for (int i = 0; i < ps.Count; i++)
        {
            L += ps.Mass[i] * Vector3.Cross(ps.Position[i], ps.Velocity[i]);
            if (ps.Mass[i] > ps.Mass[star]) star = i;
        }
        for (int i = 0; i < ps.Count; i++) if (i != star) dust += ps.Mass[i];

        Assert.True(L.Z > 0);
        Assert.True(MathF.Abs(L.X) < 0.05f * L.Z && MathF.Abs(L.Y) < 0.05f * L.Z, $"L = {L}");
        double ratio = ps.Mass[star] / dust;
        Assert.InRange(ratio, 900, 1100);
    }

    [Fact]
    public void Cloud_RotatesAboutPlusZ()
    {
        var ps = Presets.SpinningCloud(10_000, seed: 8);
        Vector3 L = Vector3.Zero;
        for (int i = 0; i < ps.Count; i++) L += ps.Mass[i] * Vector3.Cross(ps.Position[i], ps.Velocity[i]);
        Assert.True(L.Z > 0);
    }

    private static void AssertSame(ParticleSystem a, ParticleSystem b)
    {
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a.Position[i], b.Position[i]);
            Assert.Equal(a.Velocity[i], b.Velocity[i]);
            Assert.Equal(a.Mass[i], b.Mass[i]);
            Assert.Equal(a.Radius[i], b.Radius[i]);
        }
    }
}
