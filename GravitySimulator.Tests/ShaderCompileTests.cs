using GravitySimulator.Planets;
using GravitySimulator.Sph;

namespace GravitySimulator.Tests;

/// <summary>
/// Every GL program of both 3D modes compiles and links (skipped without an OpenGL 4.3 context): the
/// renderers of Planet formation and of the SPH mode, the shared compact-body sphere shader, the
/// SPH compute shaders and Planet formation's GPU gravity.
/// </summary>
[Trait("Category", "GPU")]
[Collection("GPU")]
public class ShaderCompileTests
{
    private static HeadlessGlContext Context()
    {
        var gl = HeadlessGlContext.TryCreate(out string error);
        Skip.If(gl == null, "No OpenGL 4.3 core context: " + error);
        return gl!;
    }

    [SkippableFact]
    public void PlanetRenderer_AllProgramsBuild()
    {
        using var gl = Context();
        using var r = new PlanetRenderer();
        r.Initialize();
    }

    [SkippableFact]
    public void SphRenderer_AllProgramsBuild()
    {
        using var gl = Context();
        using var r = new SphRenderer();
        r.Initialize();
    }

    [SkippableFact]
    public void CompactBodyRenderer_Builds()
    {
        using var gl = Context();
        using var r = new CompactBodyRenderer();
        r.Initialize();
    }

    [SkippableFact]
    public void SphComputeShaders_AllBuild()
    {
        using var gl = Context();
        using var solver = new SphGpuSolver(new SphParameters(), 64);
    }

    /// <summary>Planet formation's GPU gravity (planet_gravity.comp) builds on its own GL thread.</summary>
    [SkippableFact]
    public void PlanetGravityShader_Builds()
    {
        using (Context()) { }
        using var g = GpuGravity.TryCreate(out string error);
        Assert.True(g != null, "planet_gravity.comp: " + error);
    }
}
