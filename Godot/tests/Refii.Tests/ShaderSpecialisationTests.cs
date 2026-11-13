using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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

    /// <summary>A define the variant already states (lite_extra_defines) is not stated twice.</summary>
    [TestCase]
    public void ADefineAlreadyStatedIsSkipped()
    {
        string code = Engine_ShaderVariant.WithExtraDefines(
            Source("res://src/Engine/material/lite/standard_material_lite.gdshader"), new[] { "FABLE_A" });
        string built = Engine_ShaderSpecialisation.Compose(code, new[] { "FABLE_B", "FABLE_A" }, false);
        AssertThat(built.Split("#define FABLE_A\n").Length - 1).IsEqual(1);
        AssertThat(built).Contains("#define FABLE_B\n");
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

    /// <summary>
    /// The builds each low-memory scope can make, from every mix of flags the material code can
    /// reach on a stock phone (no ORM, no authored PBR): vegetation only on the two-sided, wind and
    /// tree builds, a grass bend only on vegetation's no-specular ones.
    /// </summary>
    [TestCase]
    public void LowMemoryScopesBoundTheBuilds()
    {
        string[] models = Engine_ShaderVariant.AllVariantPaths
            .Where(p => !Engine_ShaderVariant.IsTerrainSplat(p) && !p.Contains("/lite/modern/", StringComparison.Ordinal))
            .ToArray();
        AssertThat(models.Length).IsEqual(13);

        int Builds(Engine_ShaderSpecialisation.FoldScope scope)
        {
            var builds = new HashSet<string>();
            foreach (string path in models)
            {
                foreach (bool emission in new[] { false, true })
                foreach (bool normal in new[] { false, true })
                foreach (bool vegetation in Engine_ShaderVariant.DrawsVegetation(path) ? new[] { false, true } : new[] { false })
                foreach (bool bend in vegetation && Engine_ShaderVariant.DrawsGrass(path) ? new[] { false, true } : new[] { false })
                {
                    string[] folds = Engine_ShaderSpecialisation.FoldDefines(false, false, emission, vegetation, normal, bend);
                    builds.Add(path + ":" + string.Join(",", Engine_ShaderSpecialisation.ScopeFolds(scope, path, folds)));
                }
            }

            return builds.Count;
        }

        AssertThat(Builds(Engine_ShaderSpecialisation.FoldScope.Off)).IsEqual(13);
        AssertThat(Builds(Engine_ShaderSpecialisation.FoldScope.Const)).IsEqual(13);
        AssertThat(Builds(Engine_ShaderSpecialisation.FoldScope.Normal)).IsEqual(26);
        AssertThat(Builds(Engine_ShaderSpecialisation.FoldScope.All)).IsEqual(92);
    }

    /// <summary>A scope only ever narrows the folds, so a flag that is on stays a uniform in every one of them.</summary>
    [TestCase]
    public void ScopesKeepTheFlagsThatAreOn()
    {
        string lite = Engine_ShaderVariant.AllVariantPaths.First(p => p.EndsWith("/lite/standard_material_lite.gdshader", StringComparison.Ordinal));
        foreach (Engine_ShaderSpecialisation.FoldScope scope in Enum.GetValues<Engine_ShaderSpecialisation.FoldScope>())
        {
            AssertThat(Engine_ShaderSpecialisation.ScopeFolds(scope, lite, Engine_ShaderSpecialisation.FoldDefines(true, true, true, true, true, true))).IsEmpty();
        }

        string[] folds = Engine_ShaderSpecialisation.FoldDefines(false, false, false, false, false, false);
        AssertThat(Engine_ShaderSpecialisation.ScopeFolds(Engine_ShaderSpecialisation.FoldScope.Off, lite, folds)).IsEmpty();
        AssertThat(Engine_ShaderSpecialisation.ScopeFolds(Engine_ShaderSpecialisation.FoldScope.All, lite, folds)).IsEqual(folds);
        AssertThat(Engine_ShaderSpecialisation.ScopeFolds(Engine_ShaderSpecialisation.FoldScope.Normal, lite, folds))
            .ContainsExactly("FABLE_SPEC_NOT_PBR", "FABLE_SPEC_NO_ORM", "FABLE_SPEC_NO_NORMAL_MAP", "FABLE_SPEC_NO_GRASS_BEND");
    }

    /// <summary>The key's spellings, and the default for anything else.</summary>
    [TestCase]
    public void FoldScopeKeyParses()
    {
        AssertThat(Engine_ShaderSpecialisation.ParseFoldScope("off")).IsEqual(Engine_ShaderSpecialisation.FoldScope.Off);
        AssertThat(Engine_ShaderSpecialisation.ParseFoldScope("\"CONST\" ")).IsEqual(Engine_ShaderSpecialisation.FoldScope.Const);
        AssertThat(Engine_ShaderSpecialisation.ParseFoldScope("all")).IsEqual(Engine_ShaderSpecialisation.FoldScope.All);
        AssertThat(Engine_ShaderSpecialisation.ParseFoldScope("normal")).IsEqual(Engine_ShaderSpecialisation.FoldScope.Normal);
        AssertThat(Engine_ShaderSpecialisation.ParseFoldScope("what")).IsEqual(Engine_ShaderSpecialisation.FoldScope.Normal);
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

    /// <summary>An include's text, or null when there is no such file.</summary>
    private static string? Include(string resPath, [CallerFilePath] string source = "")
    {
        string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", resPath["res://".Length..]));
        return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path).Replace("\r\n", "\n") : null;
    }

    /// <summary>The code Godot compiles for <paramref name="code"/>: includes inlined, conditionals decided, comments gone.</summary>
    private static string Active(string code, string path) => Engine_ShaderCeilingSource.Preprocess(code, path, p => Include(p));

    private static readonly Regex TangentFrame = new(@"\b(TANGENT|BINORMAL|NORMAL_MAP|NORMAL_MAP_DEPTH)\b", RegexOptions.CultureInvariant);

    /// <summary>What of the tangent frame <paramref name="code"/> still names, for the message.</summary>
    private static string Named(string path, string code) =>
        string.Join(", ", TangentFrame.Matches(code).Select(m => m.Value).Distinct().Select(n => path + ": " + n));

    /// <summary>
    /// A model build without a normal map names no TANGENT, BINORMAL or NORMAL_MAP once preprocessed:
    /// Godot marks a built-in used wherever it is named, a folded `if (false)` included, and a used
    /// one keeps the tangent attribute, its varyings and Godot's normal-map maths. The far build and
    /// the fold sets with it the same; without the define every lite build still names them.
    /// The flat terrain builds and the vista page, which have no layer normal maps, likewise.
    /// </summary>
    [TestCase]
    public void NoNormalMapBuildsNameNoTangentFrame()
    {
        string[] models = Engine_ShaderVariant.AllVariantPaths.Where(p => !Engine_ShaderVariant.IsTerrainSplat(p)).ToArray();
        AssertThat(models.Length).IsEqual(26);
        string[] allFolds = Engine_ShaderSpecialisation.FoldDefines(false, false, false, false, false, false);
        string[][] sets =
        {
            Engine_ShaderSpecialisation.FarDefines,
            allFolds,
            allFolds.Concat(Engine_ShaderSpecialisation.VegetationDefines(true, false, true, tree: true)).ToArray(),
            allFolds.Concat(Engine_ShaderSpecialisation.VegetationDefines(false, true, false, tree: false)).ToArray(),
            Engine_ShaderSpecialisation.FarDefines.Concat(Engine_ShaderSpecialisation.BakeDefines(0, false)).ToArray(),
        };
        foreach (string lite in models)
        {
            string code = Source(lite);
            AssertThat(TangentFrame.IsMatch(Active(code, lite))).IsTrue();
            foreach (string[] defines in sets)
            {
                AssertThat(defines).Contains("FABLE_SPEC_NO_NORMAL_MAP");
                AssertThat(Named(lite, Active(Engine_ShaderSpecialisation.Compose(code, defines, false), lite))).IsEmpty();
            }
        }

        int flatBuilds = 0;
        foreach (string terrain in Engine_ShaderVariant.AllVariantPaths.Where(p => Engine_ShaderVariant.IsTerrainSplat(p)))
        {
            string code = Source(terrain);
            bool flat = code.Contains("#define FABLE_LITE_FLAT_TERRAIN", StringComparison.Ordinal) ||
                code.Contains("#define FABLE_TERRAIN_PAGE", StringComparison.Ordinal);
            string active = Active(code, terrain);
            if (flat)
            {
                AssertThat(Named(terrain, active)).IsEmpty();
                flatBuilds++;
            }
            else
            {
                AssertThat(TangentFrame.IsMatch(active)).IsTrue();
            }
        }

        AssertThat(flatBuilds).IsEqual(6);

        // The full shaders keep all of it: parallax, the weather ripple, the normal map itself.
        foreach (string full in new[] { Engine_ShaderFamily.StandardPath, Engine_ShaderVariant.TerrainFullPath })
        {
            AssertThat(TangentFrame.Matches(Active(File(full), full)).Select(m => m.Value).Distinct().Count()).IsEqual(3);
        }
    }

    /// <summary>The single-fetch splat has one layer fetch, inside the lite branch only.</summary>
    [TestCase]
    public void TheSingleFetchSplatSamplesOnce()
    {
        string body = Source("res://src/Engine/level/terrain/terrain_splat_body.gdshaderinc");
        const string open = "#elif defined(FABLE_LITE) && defined(FABLE_SPEC_TERRAIN_ONE_SAMPLE)\n";
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

    /// <summary>Each vegetation flag folds one way or the other; only a tree build drops the transfer, and the body knows every define.</summary>
    [TestCase]
    public void VegetationFoldsBothWays()
    {
        string[] plain = Engine_ShaderSpecialisation.VegetationDefines(false, false, false, tree: false);
        AssertThat(plain).ContainsExactly("FABLE_SPEC_NOT_FOLIAGE", "FABLE_SPEC_NOT_GRASS", "FABLE_SPEC_NO_FOLIAGE_NORMALS");
        string[] leaf = Engine_ShaderSpecialisation.VegetationDefines(true, false, true, tree: true);
        AssertThat(leaf).ContainsExactly("FABLE_SPEC_FOLIAGE", "FABLE_SPEC_NOT_GRASS", "FABLE_SPEC_FOLIAGE_NORMALS", "FABLE_SPEC_TREE_NO_TRANSFER");
        AssertThat(Engine_ShaderSpecialisation.VegetationDefines(false, true, false, tree: false)).Contains("FABLE_SPEC_GRASS");

        string body = File("res://src/Engine/material/standard_material_body.gdshaderinc");
        foreach (string define in plain.Concat(leaf).Append("FABLE_SPEC_GRASS"))
        {
            AssertThat(body.Contains("defined(" + define + ")") || body.Contains("#ifdef " + define)).IsTrue();
        }
    }
}
