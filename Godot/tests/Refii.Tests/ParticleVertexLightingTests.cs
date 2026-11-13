using System;
using System.IO;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.Fx;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// `android_particles_vertex_lit`: the guest particle and trail shaders' render mode gets
/// `vertex_lighting` only when the setting is on, and nothing else of the shader moves.
/// </summary>
[TestSuite]
public sealed class ParticleVertexLightingTests
{
    /// <summary>The shape Engine_ClassicParticles.ModernShader builds.</summary>
    private const string Modern =
        "shader_type spatial;\nrender_mode blend_premul_alpha, cull_disabled, depth_draw_never, fog_disabled, shadows_disabled, specular_disabled, skip_vertex_transform;\n" +
        "#define FX_TRAIL_DOUBLE\n#include \"res://src/Engine/fx/fx_guest_trail.gdshaderinc\"\n";

    private static string Source(string relative, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", relative))).Replace("\r\n", "\n");

    [TestCase]
    public void OffLeavesTheShaderAlone()
    {
        AssertThat(Engine_ParticleVertexLighting.Compose(Modern, false)).IsEqual(Modern);
    }

    [TestCase]
    public void OnAddsTheWordToTheRenderMode()
    {
        string code = Engine_ParticleVertexLighting.Compose(Modern, true);
        AssertThat(code).IsEqual(Modern.Replace("skip_vertex_transform;", "skip_vertex_transform, vertex_lighting;", StringComparison.Ordinal));
        // The defines and the include are untouched, and the word is in the render mode, not elsewhere.
        AssertThat(code.Split("vertex_lighting").Length).IsEqual(2);
        AssertThat(code).Contains("#define FX_TRAIL_DOUBLE\n#include \"res://src/Engine/fx/fx_guest_trail.gdshaderinc\"\n");
    }

    [TestCase]
    public void OnTwiceAddsTheWordOnce()
    {
        string once = Engine_ParticleVertexLighting.Compose(Modern, true);
        AssertThat(Engine_ParticleVertexLighting.Compose(once, true)).IsEqual(once);
    }

    [TestCase]
    public void AnUnshadedShaderHasNoLightToMove()
    {
        const string unshaded = "shader_type spatial;\nrender_mode blend_add, unshaded, cull_disabled;\nvoid fragment() {}\n";
        AssertThat(Engine_ParticleVertexLighting.Compose(unshaded, true)).IsEqual(unshaded);
    }

    [TestCase]
    public void ASpatialShaderWithoutARenderModeGetsOne()
    {
        const string bare = "shader_type spatial;\nvoid fragment() {}\n";
        AssertThat(Engine_ParticleVertexLighting.Compose(bare, true))
            .IsEqual("shader_type spatial;\nrender_mode vertex_lighting;\nvoid fragment() {}\n");
    }

    [TestCase]
    public void TheWordIsMatchedWholeNotAsAPrefix()
    {
        // `vertex_lighting_x` is not the word, and `unshaded_x` is not unshaded.
        const string code = "shader_type spatial;\nrender_mode unshaded_x,vertex_lighting_x;\n";
        AssertThat(Engine_ParticleVertexLighting.Compose(code, true))
            .IsEqual("shader_type spatial;\nrender_mode unshaded_x,vertex_lighting_x, vertex_lighting;\n");
    }

    [TestCase]
    public void TheModernParticleShadersAreComposedThroughIt()
    {
        // The one place the guest's particle and trail shaders are built (both includes go through it).
        string particles = Source("src/Engine/material/Engine_ClassicParticles.cs");
        int at = particles.IndexOf("private static Shader ModernShader(", StringComparison.Ordinal);
        AssertThat(at).IsGreater(0);
        int end = particles.IndexOf("return shader;", at, StringComparison.Ordinal);
        AssertThat(particles[at..end]).Contains("Engine_ParticleVertexLighting.Apply(");
        AssertThat(Source("src/Engine/material/Engine_ClassicParticles.Trails.cs")).Contains("ModernShader(ModernTrailInclude");
    }
}
