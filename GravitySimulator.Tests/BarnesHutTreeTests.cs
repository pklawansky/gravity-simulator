using System.Numerics;
using GravitySimulator.Planets;

namespace GravitySimulator.Tests;

public class BarnesHutTreeTests
{
    private const float Softening = 0.01f;

    [Fact]
    public void Theta06_IsWithinStatedErrorOfDirectSum()
    {
        // Stated bound (monopole Barnes-Hut, theta = 0.6, 3000-particle uniform cloud): per-particle error
        // relative to the exact |a| has mean < 1.5% and 99th percentile < 5%. (The worst ~1% are particles
        // near the centre whose net force nearly cancels, so their relative error is inflated.)
        var ps = TestHelpers.RandomCloud(3000, seed: 1);
        var tree = new BarnesHutTree(ps.Count) { Theta = 0.6f, Softening = Softening };
        tree.Build(ps);

        double sumRel = 0, maxRel = 0;
        var rels = new List<double>(ps.Count);
        for (int i = 0; i < ps.Count; i++)
        {
            Vector3 exact = BarnesHutTree.DirectAcceleration(ps, i, Softening);
            Vector3 approx = tree.AccelerationAt(ps.Position[i], i);
            double rel = (approx - exact).Length() / exact.Length();
            sumRel += rel;
            rels.Add(rel);
            maxRel = Math.Max(maxRel, rel);
        }
        double meanRel = sumRel / ps.Count;

        double p99 = Percentile99(rels);

        Assert.True(meanRel < 0.015, $"mean relative error {meanRel:E3} (p99 {p99:E3}, max {maxRel:E3})");
        Assert.True(p99 < 0.05, $"99th percentile relative error {p99:E3} (max {maxRel:E3})");
    }

    private static double Percentile99(List<double> values)
    {
        values.Sort();
        return values[(int)(values.Count * 0.99)];
    }

    [Fact]
    public void ThetaZero_MatchesDirectSumToFloatTolerance()
    {
        var ps = TestHelpers.RandomCloud(500, seed: 2);
        var tree = new BarnesHutTree(ps.Count) { Theta = 0f, Softening = Softening };
        tree.Build(ps);

        for (int i = 0; i < ps.Count; i++)
        {
            Vector3 exact = BarnesHutTree.DirectAcceleration(ps, i, Softening);
            Vector3 approx = tree.AccelerationAt(ps.Position[i], i);
            double rel = (approx - exact).Length() / exact.Length();
            Assert.True(rel < 1e-4, $"particle {i}: relative error {rel:E3}");
        }
    }

    [Fact]
    public void CoincidentParticles_DoNotBreakTheBuild()
    {
        var ps = new ParticleSystem(100);
        for (int i = 0; i < 100; i++) ps.Add(new Vector3(0.5f, 0.5f, 0.5f), Vector3.Zero, 1f, 0.01f);
        ps.Add(new Vector3(-1f, 0, 0), Vector3.Zero, 1f, 0.01f);
        var tree = new BarnesHutTree(ps.Count) { Theta = 0.6f, Softening = Softening };

        tree.Build(ps);
        Vector3 a = tree.AccelerationAt(ps.Position[100], 100);

        Vector3 exact = BarnesHutTree.DirectAcceleration(ps, 100, Softening);
        Assert.True((a - exact).Length() / exact.Length() < 1e-3);
    }
}
