using GravitySimulator.Planets;
using Xunit.Abstractions;

namespace GravitySimulator.Tests;

/// <summary>
/// Pins the merge-or-bounce collision path bit-for-bit. The hashes were first recorded from the code
/// before collision outcomes existed (planet-formation @ fde847c) and re-pinned when the collision sweep
/// moved to a hierarchical hash (planet-formation @ 817530b): pairs are now found up front and resolved
/// in (lower, higher index) order, so a contact that only arises during the sweep waits one step
/// (disc 1,450 -> 1,451 bodies, 2,550 -> 2,549 merges; cloud 153 -> 182 bodies, 2,847 -> 2,818 merges,
/// 3,809 -> 3,894 bounces). Any other change to the MergeOrBounce path (or to integration) changes them.
/// </summary>
public class MergeOrBounceGoldenTests(ITestOutputHelper output)
{
    private static ulong Hash(ParticleSystem ps, SimulationStats s)
    {
        ulong h = 1469598103934665603UL;
        void Mix(uint v) { h ^= v; h *= 1099511628211UL; }
        Mix((uint)ps.Count);
        Mix((uint)s.Merges);
        Mix((uint)s.Bounces);
        for (int i = 0; i < ps.Count; i++)
        {
            Mix(BitConverter.SingleToUInt32Bits(ps.Position[i].X));
            Mix(BitConverter.SingleToUInt32Bits(ps.Position[i].Y));
            Mix(BitConverter.SingleToUInt32Bits(ps.Position[i].Z));
            Mix(BitConverter.SingleToUInt32Bits(ps.Velocity[i].X));
            Mix(BitConverter.SingleToUInt32Bits(ps.Velocity[i].Y));
            Mix(BitConverter.SingleToUInt32Bits(ps.Velocity[i].Z));
            Mix(BitConverter.SingleToUInt32Bits(ps.Mass[i]));
            Mix(BitConverter.SingleToUInt32Bits(ps.Radius[i]));
        }
        return h;
    }

    private (ulong Hash, SimulationStats Stats) Run(PresetKind kind, int count, int steps)
    {
        var p = new SimulationParameters();
        SetLegacyModel(p);
        Presets.Configure(kind, p);
        var sim = new PlanetSimulation(p);
        sim.Load(Presets.Create(kind, count, seed: 11, discMassFraction: 0.1f));
        for (int i = 0; i < steps; i++) sim.Step();
        var s = sim.Stats;
        ulong h = Hash(sim.Particles, s);
        output.WriteLine($"{kind}: count {s.Count}, merges {s.Merges}, bounces {s.Bounces}, hash 0x{h:X16}");
        return (h, s);
    }

    private static void SetLegacyModel(SimulationParameters p) => p.CollisionModel = CollisionModel.MergeOrBounce;

    [Fact]
    public void Disc_MergeOrBounce_MatchesPreOutcomesCode()
    {
        var (h, s) = Run(PresetKind.DiscAroundStar, 4000, 300);
        Assert.True(s.Merges > 0);
        Assert.Equal(0x15803126073B45F5UL, h);
    }

    [Fact]
    public void SpinningCloud_MergeOrBounce_MatchesPreOutcomesCode()
    {
        var (h, s) = Run(PresetKind.SpinningCloud, 3000, 300);
        Assert.True(s.Merges > 0);
        Assert.True(s.Bounces > 0);
        Assert.Equal(0x69EDF157C502CEEAUL, h);
    }
}
