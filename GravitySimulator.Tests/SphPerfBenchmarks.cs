using System.Diagnostics;
using GravitySimulator.Sph;
using Xunit.Abstractions;

namespace GravitySimulator.Tests;

/// <summary>
/// Steps/s of the Gas dynamics (SPH) presets on the GPU, with the time split between GPU phases (GL
/// timestamp queries) and the CPU tree. Opt-in (Category=Perf), run with:
///   dotnet test GravitySimulator.Tests -c Release --settings GravitySimulator.Tests/perf.runsettings --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Perf")]
[Collection("GPU")]
public class SphPerfBenchmarks(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData(SphPresetKind.CollapsingCloud, 100_000)]
    [InlineData(SphPresetKind.CollapsingCloud, 200_000)]
    [InlineData(SphPresetKind.CollapsingCloud, 400_000)]
    [InlineData(SphPresetKind.CollidingClouds, 200_000)]
    [InlineData(SphPresetKind.TidalDisruption, 50_000)]
    public void Preset_StepsPerSecond(SphPresetKind kind, int count)
    {
        using var gl = HeadlessGlContext.TryCreate(out string error);
        Skip.If(gl == null, "No OpenGL 4.3 core context: " + error);
        using var sim = new SphSimulation();
        var load = Stopwatch.StartNew();
        sim.Load(SphPresets.Create(kind, count, seed: 1));
        double loadSecs = load.Elapsed.TotalSeconds;
        for (int i = 0; i < 10; i++) sim.Step(); // warm-up

        const int steps = 60;
        var sum = new SphPhaseTimes();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < steps; i++)
        {
            sim.Step();
            var t = sim.Solver.Times;
            sum.Integrate += t.Integrate; sum.Grid += t.Grid; sum.Density += t.Density; sum.Force += t.Force;
            sum.Gravity += t.Gravity; sum.Sinks += t.Sinks; sum.TreeWait += t.TreeWait; sum.TreeBuild += t.TreeBuild; sum.TreeUpload += t.TreeUpload;
        }
        double rate = steps / sw.Elapsed.TotalSeconds;
        double ms = 1000.0 / rate;
        var s = sim.Stats;
        output.WriteLine($"{kind} {count:N0} on {gl!.Renderer}: {rate:F1} steps/s ({ms:F1} ms/step), load {loadSecs:F1} s, " +
                         $"t={s.SimTime:G3}, sinks {s.Sinks}, gas {s.Gas:N0}");
        output.WriteLine($"  GPU ms/step: integrate {sum.Integrate / steps:F2}, grid {sum.Grid / steps:F2}, density {sum.Density / steps:F2}, " +
                         $"force {sum.Force / steps:F2}, gravity {sum.Gravity / steps:F2}, sinks {sum.Sinks / steps:F2} (total {sum.GpuTotal / steps:F2})");
        output.WriteLine($"  CPU ms/step: wait for positions {sum.TreeWait / steps:F2}, tree build+flatten {sum.TreeBuild / steps:F2}, tree upload {sum.TreeUpload / steps:F2}");
        Assert.True(rate > 0);
    }
}
