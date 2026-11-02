using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The lite shaders specialised at material build (Engine_ShaderSpecialisation).</summary>
[TestSuite]
public sealed class ShaderSpecialisationTests
{
    private static string File(string resPath, [CallerFilePath] string source = "") =>
        System.IO.File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(source)!, "../..", resPath["res://".Length..]))).Replace("\r\n", "\n");

    /// <summary>A shader's text: the file, or a lite build made from its full shader's file.</summary>
    private static string Source(string resPath) => Engine_ShaderVariant.IsBuild(resPath)
        ? Engine_ShaderVariant.Compose(File(Engine_ShaderVariant.FullPathOf(resPath)), resPath)
        : File(resPath);

    /// <summary>Nothing asked: the wrapper's own text, byte for byte.</summary>
    [TestCase]
    public void ComposesNothingIntoTheWrapperItself()
    {
        foreach (string lite in Engine_ShaderVariant.AllVariantPaths)
        {
            string code = Source(lite);
            AssertThat(Engine_ShaderSpecialisation.Compose(code, Array.Empty<string>(), false)).IsEqual(code);
        }
    }

    /// <summary>Shadowless adds `shadows_disabled` to the wrapper's one render mode and changes nothing else.</summary>
    [TestCase]
    public void ShadowlessExtendsTheRenderMode()
    {
        foreach (string lite in Engine_ShaderVariant.AllVariantPaths)
        {
            string code = Source(lite);
            string built = Engine_ShaderSpecialisation.Compose(code, Array.Empty<string>(), true);
            string[] modes = built.Split('\n').Where(l => l.StartsWith("render_mode", StringComparison.Ordinal)).ToArray();
            AssertThat(modes.Length).IsEqual(1);
            AssertThat(modes[0].EndsWith(", shadows_disabled;", StringComparison.Ordinal)).IsTrue();
            AssertThat(built.Replace(", shadows_disabled;", ";", StringComparison.Ordinal)).IsEqual(code);
        }
    }

    /// <summary>Defines go in sorted, before the body's #include, so the body sees them.</summary>
    [TestCase]
    public void DefinesPrecedeTheInclude()
    {
        string code = Source("res://src/Engine/material/lite/standard_material_lite.gdshader");
        string built = Engine_ShaderSpecialisation.Compose(code, new[] { "FABLE_B", "FABLE_A" }, false);
        int a = built.IndexOf("#define FABLE_A\n", StringComparison.Ordinal);
        int b = built.IndexOf("#define FABLE_B\n", StringComparison.Ordinal);
        int include = built.IndexOf("#include", StringComparison.Ordinal);
        AssertThat(a).IsGreater(-1);
        AssertThat(b).IsGreater(a);
        AssertThat(include).IsGreater(b);
    }

    /// <summary>Any setting that could render a shadow map keeps the shadowed builds.</summary>
    [TestCase]
    public void ShadowlessOnlyWhereNoMapRenders()
    {
        AssertThat(Engine_ShaderSpecialisation.Decide(null, 0f, false)).IsEmpty();
        AssertThat(Engine_ShaderSpecialisation.Decide(false, 200f, false)).IsEmpty();
        AssertThat(Engine_ShaderSpecialisation.Decide(null, 80f, false)).IsNotEmpty();
        AssertThat(Engine_ShaderSpecialisation.Decide(true, 0f, false)).IsNotEmpty();
        AssertThat(Engine_ShaderSpecialisation.Decide(null, 0f, true)).IsNotEmpty();
    }

    /// <summary>A bake-mode build fixes its mode only where ir3 shows a gain, and drops the PRT
    /// transfer only where nothing can read it; every mode it names exists in the shader.</summary>
    [TestCase]
    public void BakeBuildsMatchTheShader()
    {
        string lighting = Source("res://src/Engine/material/fable_lighting.gdshaderinc");
        string body = Source("res://src/Engine/material/standard_material_body.gdshaderinc");
        for (int mode = 0; mode <= 4; mode++)
        {
            foreach (bool transfer in new[] { false, true })
            {
                string[] defines = Engine_ShaderSpecialisation.BakeDefines(mode, transfer);
                foreach (string define in defines)
                {
                    AssertThat(lighting.Contains($"defined({define})", StringComparison.Ordinal) ||
                        body.Contains($"defined({define})", StringComparison.Ordinal)).IsTrue();
                }

                // Only the probe reads the transfer, so only a probe build on a mesh that has one keeps it.
                AssertThat(defines.Contains("FABLE_SPEC_NO_PRT")).IsEqual(!(mode == 1 && transfer));
                AssertThat(defines.Count(d => d.StartsWith("FABLE_SPEC_BAKE_", StringComparison.Ordinal))).IsEqual(mode == 1 ? 0 : 1);
            }
        }

        AssertThat(Engine_ShaderSpecialisation.BakeDefines(7, false)).IsEmpty();
    }

    /// <summary>The mode macro stands for the instance's own mode wherever the body branched on it.</summary>
    [TestCase]
    public void TheBodyReadsTheModeThroughTheMacro()
    {
        string body = Source("res://src/Engine/material/standard_material_body.gdshaderinc");
        string[] reads = body.Split('\n').Where(l => l.Contains("lightmap_colour.w", StringComparison.Ordinal)).ToArray();
        // The debug view's tag is the only direct read left (not in FABLE_LITE).
        AssertThat(reads.Length).IsEqual(1);
        AssertThat(reads[0].Contains("float m = lightmap_colour.w;", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>A fold for each flag that is off, none for one that is on; each one the body knows.</summary>
    [TestCase]
    public void FoldsOnlyTheFlagsThatAreOff()
    {
        string body = Source("res://src/Engine/material/standard_material_body.gdshaderinc");
        string[] all = Engine_ShaderSpecialisation.FoldDefines(false, false, false, false, false, false);
        AssertThat(all.Length).IsEqual(6);
        foreach (string define in all)
        {
            AssertThat(body.Contains($"#ifdef {define}\n", StringComparison.Ordinal)).IsTrue();
        }

        AssertThat(Engine_ShaderSpecialisation.FoldDefines(true, true, true, true, true, true)).IsEmpty();
        AssertThat(Engine_ShaderSpecialisation.FoldDefines(false, true, true, true, false, true))
            .ContainsExactly("FABLE_SPEC_NOT_PBR", "FABLE_SPEC_NO_NORMAL_MAP");

        // Folded through the macros only: no fragment code reads the raw uniform any more.
        foreach (string raw in new[] { "if (is_pbr)", "if (has_orm)", "if (has_emission)", "if (premultiplied_alpha)", "if (has_normal)" })
        {
            AssertThat(body.Contains(raw, StringComparison.Ordinal)).IsFalse();
        }
    }

    /// <summary>The single-fetch splat has one layer fetch, inside the lite branch only.</summary>
    [TestCase]
    public void TheSingleFetchSplatSamplesOnce()
    {
        string body = Source("res://src/Engine/level/terrain/terrain_splat_body.gdshaderinc");
        const string open = "#if defined(FABLE_LITE) && defined(FABLE_SPEC_TERRAIN_ONE_SAMPLE)\n";
        int start = body.IndexOf(open, StringComparison.Ordinal);
        AssertThat(start).IsGreater(-1);
        int split = body.IndexOf("\n#else\n", start, StringComparison.Ordinal);
        string branch = body[start..split];
        int calls = branch.Split("sample_layer(").Length - 1;
        AssertThat(calls).IsEqual(1);
        AssertThat(branch.Contains("mat <= 32", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// A light about to cast while the shaders are built without shadow sampling holds its shadow off,
    /// and the rebuild waits for a loading screen (or the Options panel's Rebuild now); after it, lights cast.
    /// In Godot's runtime: the wait and the rebuild log through GD.Print.
    /// </summary>
    [TestCase]
    [RequireGodotRuntime]
    public void TheShadowRebuildWaitsForALoadingScreen()
    {
        try
        {
            Engine_ShaderSpecialisation.SetShadowlessForTests(true);
            AssertThat(Engine_ShaderSpecialisation.ShadowCast("the sun")).IsFalse();
            AssertThat(Engine_ShaderSpecialisation.RebuildWaiting).IsTrue();
            AssertThat(Engine_ShaderSpecialisation.ShadowCast("a placed light")).IsFalse();
            Engine_ShaderSpecialisation.LoadingScreen();
            AssertThat(Engine_ShaderSpecialisation.RebuildWaiting).IsFalse();
            AssertThat(Engine_ShaderSpecialisation.ShadowCast("the sun")).IsTrue();

            Engine_ShaderSpecialisation.SetShadowlessForTests(true);
            Engine_ShaderSpecialisation.ShadowCast("a placed light");
            Engine_ShaderSpecialisation.RebuildNow();
            AssertThat(Engine_ShaderSpecialisation.RebuildWaiting).IsFalse();
            AssertThat(Engine_ShaderSpecialisation.ShadowCast("a placed light")).IsTrue();

            // Nothing waits, and nothing is held, while the shaders keep their shadow sampling.
            Engine_ShaderSpecialisation.SetShadowlessForTests(false);
            AssertThat(Engine_ShaderSpecialisation.ShadowCast("the sun")).IsTrue();
            AssertThat(Engine_ShaderSpecialisation.RebuildWaiting).IsFalse();
        }
        finally { Engine_ShaderSpecialisation.SetShadowlessForTests(null); }
    }
}
