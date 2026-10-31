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
