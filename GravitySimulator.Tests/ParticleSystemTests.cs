using System.Numerics;
using GravitySimulator.Planets;

namespace GravitySimulator.Tests;

public class ParticleSystemTests
{
    [Fact]
    public void RemoveAt_SwapsWithLast()
    {
        var ps = new ParticleSystem(3);
        ps.Add(new Vector3(0, 0, 0), Vector3.Zero, 1f, 0.1f);
        ps.Add(new Vector3(1, 0, 0), Vector3.Zero, 2f, 0.2f);
        ps.Add(new Vector3(2, 0, 0), Vector3.Zero, 3f, 0.3f);

        ps.RemoveAt(0);

        Assert.Equal(2, ps.Count);
        Assert.Equal(new Vector3(2, 0, 0), ps.Position[0]);
        Assert.Equal(3f, ps.Mass[0]);
        Assert.Equal(0.3f, ps.Radius[0]);
    }

    [Fact]
    public void Compact_RemovesMasslessEntries()
    {
        var ps = new ParticleSystem(4);
        for (int i = 0; i < 4; i++) ps.Add(new Vector3(i, 0, 0), Vector3.Zero, i % 2 == 0 ? 0f : 1f, 0.1f);

        int removed = ps.Compact();

        Assert.Equal(2, removed);
        Assert.Equal(2, ps.Count);
        Assert.All(Enumerable.Range(0, ps.Count), i => Assert.Equal(1f, ps.Mass[i]));
    }

    [Fact]
    public void Add_GrowsBeyondInitialCapacity()
    {
        var ps = new ParticleSystem(1);
        for (int i = 0; i < 10; i++) ps.Add(Vector3.One * i, Vector3.Zero, 1f, 0.1f);
        Assert.Equal(10, ps.Count);
        Assert.Equal(Vector3.One * 9, ps.Position[9]);
    }
}
