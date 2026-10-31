using System.Diagnostics;
using GravitySimulator.Planets;
using GravitySimulator.Sph;
using OpenTK.Graphics.OpenGL4;
using Xunit.Abstractions;

namespace GravitySimulator.Tests;

/// <summary>
/// How many live particles Planet formation can afford: whole-step cost (Barnes-Hut, collisions,
/// snapshot) and render cost (upload + draw into an offscreen framebuffer the size of the app's GL view)
/// against the live count. The state is the UI-default 10% disc a few steps after Load: that disc merges
/// fast (400k bodies fall to ~25k in 100 steps), so the window is kept short and early to time the step at
/// the stated live count, with the contact sweep at its busiest. Opt-in (Category=Perf), run with:
///   dotnet test GravitySimulator.Tests -c Release --settings GravitySimulator.Tests/perf.runsettings --filter "FullyQualifiedName~Affordability" --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Perf")]
[Collection("GPU")]
public class AffordabilityBenchmarks(ITestOutputHelper output)
{
    [Theory]
    [InlineData(50_000)]
    [InlineData(100_000)]
    [InlineData(150_000)]
    [InlineData(200_000)]
    [InlineData(300_000)]
    [InlineData(400_000)]
    public void StepAndRenderCost_VsLiveCount(int count)
    {
        var sim = new PlanetSimulation(new SimulationParameters());
        sim.Load(Presets.DiscAroundStar(count, seed: 1, discMassFraction: 0.1f));
        for (int i = 0; i < 3; i++) sim.Step(); // warm-up (JIT, pools, first contacts)

        const int steps = 10;
        int liveStart = sim.Particles.Count;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < steps; i++) sim.Step();
        sw.Stop();
        double msStep = sw.Elapsed.TotalMilliseconds / steps;

        string render = "render n/a";
        using (var gl = HeadlessGlContext.TryCreate(out string error))
        {
            if (gl != null) render = RenderCost(sim);
            else render = "render skipped: " + error;
        }
        var s = sim.Stats;
        output.WriteLine($"affordability {count}: live {liveStart}->{s.Count}, step {msStep:F1} ms ({1000.0 / msStep:F1} steps/s), " +
                         $"{render}, fragments {s.FragmentsCreated}, cores {Environment.ProcessorCount}");
        Assert.True(msStep > 0);
    }

    /// <summary>
    /// The state the fragment ceiling is about: a warmed-up 50k disc (150 steps: clumps, fragments, about 15k
    /// live) topped up to <paramref name="count"/> with debris of m0/4 to m0 on near-circular orbits across the
    /// disc, puffed to sigma_z = 0.05 as sprays are. Times 10 steps (after 2 warm-up steps) and the render.
    /// </summary>
    [Theory]
    [InlineData(50_000)]
    [InlineData(100_000)]
    [InlineData(150_000)]
    [InlineData(200_000)]
    [InlineData(300_000)]
    [InlineData(400_000)]
    public void StepAndRenderCost_WarmDiscPlusDebrisCloud(int count)
    {
        var sim = new PlanetSimulation(new SimulationParameters { MaxLiveParticles = 1_000_000 });
        sim.Load(Presets.DiscAroundStar(50_000, seed: 1, discMassFraction: 0.1f));
        for (int i = 0; i < 150; i++) sim.Step();
        var ps = sim.Particles.Clone();
        float m0 = sim.Stats.GrainMass;
        var rng = new Random(7);
        float rho = m0 / (4f / 3f * MathF.PI * MathF.Pow(Presets.DustRadius, 3));
        while (ps.Count < count)
        {
            float r = Presets.DiscInner + (float)rng.NextDouble() * (Presets.DiscOuter - Presets.DiscInner);
            float a = (float)(rng.NextDouble() * Math.PI * 2);
            float z = (float)(rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 1.5) * 0.1f; // ~N(0, 0.05)
            var pos = new System.Numerics.Vector3(r * MathF.Cos(a), r * MathF.Sin(a), z);
            float vc = MathF.Sqrt(1.05f / r);
            var vel = new System.Numerics.Vector3(-MathF.Sin(a), MathF.Cos(a), 0f) * vc;
            float m = m0 * (0.25f + 0.75f * (float)rng.NextDouble());
            ps.Add(pos, vel, m, MathF.Cbrt(3f * m / (4f * MathF.PI * rho)));
        }
        sim.Load(ps);
        for (int i = 0; i < 2; i++) sim.Step();
        const int steps = 10;
        int liveStart = sim.Particles.Count;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < steps; i++) sim.Step();
        sw.Stop();
        double msStep = sw.Elapsed.TotalMilliseconds / steps;
        // Phase split on a copy of the final state: tree build + forces, then one collision sweep.
        var probe = sim.Particles.Clone();
        var tree = new BarnesHutTree(probe.Capacity) { Theta = 0.6f, Softening = 0.005f };
        var ph = Stopwatch.StartNew();
        tree.Build(probe);
        Parallel.For(0, probe.Count, i => probe.Acceleration[i] = tree.AccelerationAt(probe.Position[i], i));
        double msGravity = ph.Elapsed.TotalMilliseconds;
        var resolver = new CollisionResolver { Model = CollisionModel.Outcomes, MaxBodies = sim.LiveParticleCeiling, MinFragmentMass = m0 / 4f };
        ph.Restart();
        resolver.Resolve(probe);
        double msCollide = ph.Elapsed.TotalMilliseconds;
        string render;
        using (var gl = HeadlessGlContext.TryCreate(out string error))
            render = gl != null ? RenderCost(sim) : "render skipped: " + error;
        var s = sim.Stats;
        output.WriteLine($"debris-cloud {count}: live {liveStart}->{s.Count}, step {msStep:F1} ms ({1000.0 / msStep:F1} steps/s), " +
                         $"gravity {msGravity:F0} ms, collisions {msCollide:F1} ms (find pairs {resolver.LastDetectMs:F1}, resolve {resolver.LastResolveMs:F1}, " +
                         $"compact+append {resolver.LastCompactMs:F2}; {resolver.LastPairTests:N0} pair tests, {resolver.LastMerges} merges, {resolver.LastFragments} fragments), " +
                         $"{render}, merges {s.Merges}, fragments {s.FragmentsCreated}");
        Assert.True(msStep > 0);
    }

    /// <summary>
    /// The debris-cloud state of <see cref="StepAndRenderCost_WarmDiscPlusDebrisCloud"/> (same seeds), on either
    /// gravity backend, timing 10 steps after 2 warm-up steps with the simulation's own per-phase split
    /// (<see cref="PlanetSimulation.LastPhaseTimes"/>, averaged). The warmed 50k disc is built once (CPU).
    /// </summary>
    [SkippableTheory]
    [InlineData(50_000, false)]
    [InlineData(100_000, false)]
    [InlineData(150_000, false)]
    [InlineData(200_000, false)]
    [InlineData(300_000, false)]
    [InlineData(400_000, false)]
    [InlineData(50_000, true)]
    [InlineData(100_000, true)]
    [InlineData(150_000, true)]
    [InlineData(200_000, true)]
    [InlineData(300_000, true)]
    [InlineData(400_000, true)]
    [InlineData(500_000, true)]
    [InlineData(600_000, true)]
    [InlineData(800_000, true)]
    [InlineData(1_000_000, true)]
    public void DebrisCloud_StepCostAndPhaseSplit(int count, bool gpu)
    {
        using var sim = new PlanetSimulation(new SimulationParameters { MaxLiveParticles = 2_000_000 });
        if (gpu) Skip.IfNot(sim.TryEnableGpuGravity(), sim.GravityBackend);
        sim.Load(DebrisCloud(count));
        for (int i = 0; i < 2; i++) sim.Step();
        const int steps = 10;
        int liveStart = sim.Particles.Count;
        double kd = 0, tree = 0, grav = 0, flat = 0, walk = 0, coll = 0, rest = 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < steps; i++)
        {
            sim.Step();
            var t = sim.LastPhaseTimes;
            kd += t.KickDrift; tree += t.TreeBuild; grav += t.Gravity; flat += t.GpuFlatten; walk += t.GpuWalk; coll += t.Collisions; rest += t.Rest;
        }
        sw.Stop();
        Assert.True(!gpu || sim.GpuGravityActive, sim.GravityBackend);
        double msStep = sw.Elapsed.TotalMilliseconds / steps;
        var s = sim.Stats;
        string gpuSplit = gpu ? $" (flatten+pack {flat / steps:F1}, GL upload+walk+readback {walk / steps:F1})" : "";
        output.WriteLine($"phase-split {(gpu ? "GPU" : "CPU")} {count}: live {liveStart}->{s.Count}, step {msStep:F1} ms ({1000.0 / msStep:F1} steps/s); " +
                         $"kick+drift {kd / steps:F1}, tree build {tree / steps:F1}, gravity {grav / steps:F1}{gpuSplit}, collisions {coll / steps:F1}, " +
                         $"rest {rest / steps:F1} ms; fragments {s.FragmentsCreated}; {sim.GravityBackend}");
        Assert.True(msStep > 0);
    }

    /// <summary>
    /// The optimisation profile: the debris cloud on the GPU backend at 150k / 350k / 500k, 10 steps after 2
    /// warm-up steps, with every sub-phase of <see cref="PlanetSimulation.LastPhaseTimes"/> averaged.
    /// </summary>
    [SkippableTheory]
    [InlineData(150_000, 0)]
    [InlineData(350_000, 0)]
    [InlineData(500_000, 0)]
    [InlineData(1_000_000, 0)]
    [InlineData(350_000, 1)]
    [InlineData(350_000, 4)]
    [InlineData(350_000, 16)]
    [InlineData(350_000, 32)]
    public void DebrisCloud_GpuProfile(int count, int leafCapacity)
    {
        using var sim = new PlanetSimulation(new SimulationParameters { MaxLiveParticles = 2_000_000 });
        Skip.IfNot(sim.TryEnableGpuGravity(), sim.GravityBackend);
        if (leafCapacity > 0) sim.GpuLeafCapacity = leafCapacity;
        sim.Load(DebrisCloud(count));
        for (int i = 0; i < 2; i++) sim.Step();
        const int steps = 10;
        var sum = new double[16];
        long nodes = 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < steps; i++)
        {
            sim.Step();
            var t = sim.LastPhaseTimes;
            double[] v = { t.KickDrift, t.TreeBuild, t.GpuFlatten, t.GpuTreeBuild, t.GpuUpload, t.GpuKernel, t.GpuReadback,
                           t.GpuScatter, t.Reorder, t.Gravity - t.GpuFlatten - t.GpuWalk - t.GpuScatter - t.Reorder,
                           t.CollisionDetect, t.CollisionResolve, t.CollisionCompact, t.Collisions - t.CollisionDetect - t.CollisionResolve - t.CollisionCompact,
                           t.Rest, t.Total };
            for (int k = 0; k < v.Length; k++) sum[k] += v[k];
        }
        nodes = sim.LastGpuNodeCount;
        sw.Stop();
        Assert.True(sim.GpuGravityActive, sim.GravityBackend);
        string[] names = { "kick+drift", "CPU tree build", "CPU prep", "GPU tree build", "upload", "GPU walk", "readback", "scatter", "reorder", "gravity other",
                           "coll broad", "coll resolve", "coll compact", "coll other", "rest", "total" };
        output.WriteLine($"profile GPU {count} leaf {sim.GpuLeafCapacity}: live {sim.Particles.Count}, nodes {nodes}, wall {sw.Elapsed.TotalMilliseconds / steps:F1} ms/step; " +
                         string.Join(", ", names.Select((nm, k) => $"{nm} {sum[k] / steps:F1}")));
    }

    /// <summary>
    /// The ceiling check: the debris cloud loaded at <paramref name="ceiling"/> with MaxLiveParticles =
    /// <paramref name="ceiling"/>, so fragments keep it pinned there; 30 steps timed after 2 warm-up steps.
    /// </summary>
    [SkippableTheory]
    [InlineData(150_000, false)]
    [InlineData(150_000, true)]
    [InlineData(350_000, true)]
    [InlineData(400_000, true)]
    [InlineData(450_000, true)]
    [InlineData(500_000, true)]
    public void DebrisCloud_HeldAtCeiling(int ceiling, bool gpu)
    {
        using var sim = new PlanetSimulation(new SimulationParameters { MaxLiveParticles = ceiling });
        if (gpu) Skip.IfNot(sim.TryEnableGpuGravity(), sim.GravityBackend);
        sim.Load(DebrisCloud(ceiling));
        for (int i = 0; i < 2; i++) sim.Step();
        const int steps = 30;
        int liveStart = sim.Particles.Count;
        double tree = 0, grav = 0, coll = 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < steps; i++)
        {
            sim.Step();
            var t = sim.LastPhaseTimes;
            tree += t.TreeBuild; grav += t.Gravity; coll += t.Collisions;
        }
        sw.Stop();
        Assert.True(!gpu || sim.GpuGravityActive, sim.GravityBackend);
        double msStep = sw.Elapsed.TotalMilliseconds / steps;
        var s = sim.Stats;
        output.WriteLine($"held-at-ceiling {(gpu ? "GPU" : "CPU")} {ceiling}: live {liveStart}->{s.Count}, step {msStep:F1} ms ({1000.0 / msStep:F1} steps/s); " +
                         $"tree build {tree / steps:F1}, gravity {grav / steps:F1}, collisions {coll / steps:F1} ms; fragments {s.FragmentsCreated}, folded {s.FragmentsFoldedAtCeiling}");
        Assert.True(msStep > 0);
    }

    /// <summary>
    /// The heaviest held-at-ceiling load measured: the lightest disc (0.1% of the star) loaded at the ceiling.
    /// Its grains barely merge, so nearly every contact sprays fragments and the live count stays pinned at the
    /// ceiling (millions folded). 100 steps of warm-up, then 100 timed, with the per-phase split.
    /// </summary>
    [SkippableTheory]
    [InlineData(150_000, false)]
    [InlineData(150_000, true)]
    [InlineData(300_000, true)]
    [InlineData(350_000, true)]
    [InlineData(400_000, true)]
    [InlineData(450_000, true)]
    public void LightDisc_HeldAtCeiling(int ceiling, bool gpu)
    {
        using var sim = new PlanetSimulation(new SimulationParameters { MaxLiveParticles = ceiling });
        if (gpu) Skip.IfNot(sim.TryEnableGpuGravity(), sim.GravityBackend);
        sim.Load(Presets.DiscAroundStar(ceiling, seed: 1, discMassFraction: 0.001f));
        for (int i = 0; i < 100; i++) sim.Step();
        const int steps = 100;
        double tree = 0, grav = 0, coll = 0, total = 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < steps; i++)
        {
            sim.Step();
            var t = sim.LastPhaseTimes;
            tree += t.TreeBuild; grav += t.Gravity; coll += t.Collisions; total += t.Total;
        }
        sw.Stop();
        Assert.True(!gpu || sim.GpuGravityActive, sim.GravityBackend);
        double msStep = sw.Elapsed.TotalMilliseconds / steps;
        var s = sim.Stats;
        output.WriteLine($"light-disc {(gpu ? "GPU" : "CPU")} {ceiling}: live {s.Count}, step {msStep:F1} ms ({1000.0 / msStep:F1} steps/s); tree build {tree / steps:F1}, " +
                         $"gravity {grav / steps:F1}, collisions {coll / steps:F1}, other {(total - tree - grav - coll) / steps:F1} ms; fragments {s.FragmentsCreated}, folded {s.FragmentsFoldedAtCeiling}");
        Assert.True(msStep > 0);
    }

    private static readonly Lazy<(ParticleSystem State, float GrainMass)> WarmDisc = new(() =>
    {
        var sim = new PlanetSimulation(new SimulationParameters { MaxLiveParticles = 1_000_000 });
        sim.Load(Presets.DiscAroundStar(50_000, seed: 1, discMassFraction: 0.1f));
        for (int i = 0; i < 150; i++) sim.Step();
        return (sim.Particles.Clone(), sim.Stats.GrainMass);
    });

    // As in StepAndRenderCost_WarmDiscPlusDebrisCloud: the warmed disc topped up with debris (rng seed 7).
    private static ParticleSystem DebrisCloud(int count)
    {
        var (warm, m0) = WarmDisc.Value;
        var ps = warm.Clone();
        ps.EnsureCapacity(count);
        var rng = new Random(7);
        float rho = m0 / (4f / 3f * MathF.PI * MathF.Pow(Presets.DustRadius, 3));
        while (ps.Count < count)
        {
            float r = Presets.DiscInner + (float)rng.NextDouble() * (Presets.DiscOuter - Presets.DiscInner);
            float a = (float)(rng.NextDouble() * Math.PI * 2);
            float z = (float)(rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 1.5) * 0.1f; // ~N(0, 0.05)
            var pos = new System.Numerics.Vector3(r * MathF.Cos(a), r * MathF.Sin(a), z);
            float vc = MathF.Sqrt(1.05f / r);
            var vel = new System.Numerics.Vector3(-MathF.Sin(a), MathF.Cos(a), 0f) * vc;
            float m = m0 * (0.25f + 0.75f * (float)rng.NextDouble());
            ps.Add(pos, vel, m, MathF.Cbrt(3f * m / (4f * MathF.PI * rho)));
        }
        return ps;
    }

    /// <summary>
    /// Cost of the per-event fragment cap: a forced shower of head-on disruptions (equal bodies, debris far above
    /// 64 minimum fragments) resolved in one sweep, so every event emits the full 64 fragments.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(1_000)]
    public void DisruptionShower_ResolveCost(int pairs)
    {
        var state = new ParticleSystem(2 * pairs);
        float r = MathF.Cbrt(3f / (4f * MathF.PI));
        for (int k = 0; k < pairs; k++)
        {
            var o = new System.Numerics.Vector3(k * 20f, 0f, 0f);
            state.Add(o, System.Numerics.Vector3.Zero, 1f, r);
            state.Add(o + new System.Numerics.Vector3(1.998f * r, 0f, 0f), new System.Numerics.Vector3(-10f, 0f, 0f), 1f, r);
        }
        double best = double.MaxValue;
        int fragments = 0;
        for (int rep = 0; rep < 5; rep++)
        {
            var ps = state.Clone();
            var res = new CollisionResolver { Model = CollisionModel.Outcomes, MaxBodies = 1_000_000, MinFragmentMass = 0.001f };
            var sw = Stopwatch.StartNew();
            res.Resolve(ps);
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            fragments = res.LastFragments;
            Assert.Equal(pairs, res.LastDisruptions);
        }
        output.WriteLine($"shower {pairs} disruptions: {fragments} fragments ({fragments / pairs} per event), resolve {best:F2} ms " +
                         $"({1000.0 * best / pairs:F1} us per event)");
    }

    // Upload + draw + glFinish per frame at the app's GL view size (1280x800 window minus the 250 px side panel).
    private static string RenderCost(PlanetSimulation sim)
    {
        const int w = 1030, h = 800;
        int fbo = GL.GenFramebuffer(), rb = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, rb);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, w, h);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, rb);
        try
        {
            using var r = new PlanetRenderer();
            r.Initialize();
            var cam = new OrbitCamera();
            var snap = sim.AcquireLatest();
            const int frames = 60;
            double total = 0;
            for (int f = 0; f < frames + 5; f++)
            {
                snap.Version++; // force a fresh upload every frame, as when the simulation publishes
                var sw = Stopwatch.StartNew();
                r.Upload(snap);
                r.Render(cam, w, h);
                GL.Finish();
                if (f >= 5) total += sw.Elapsed.TotalMilliseconds;
            }
            double ms = total / frames;
            return $"render {ms:F2} ms/frame ({1000.0 / ms:F0} fps cap) on {GL.GetString(StringName.Renderer)}";
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(fbo);
            GL.DeleteRenderbuffer(rb);
        }
    }
}
