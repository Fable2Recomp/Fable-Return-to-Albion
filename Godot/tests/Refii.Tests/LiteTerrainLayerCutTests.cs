using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The phone's ground levers in the lite splat (terrain_splat_body.gdshaderinc and
/// android/terrain_lite_layers.gdshaderinc, composed by Engine_ShaderSpecialisation.TerrainLeverDefines):
/// the bit loop, the layer cap and the weight floor replace the body's lite loop with one that reads
/// each weight once and samples through one `sample_layer`; the cliff sharpening, the trilinear filter
/// and the vertex fog change only their own lines. The desktop and the plain lite builds compile as before.
/// </summary>
[TestSuite]
public sealed class LiteTerrainLayerCutTests
{
    private const string BitLoop = "FABLE_LITE_TERRAIN_BIT_LOOP";
    private const string MinWeight = "FABLE_LITE_TERRAIN_MIN_WEIGHT";
    private const string MaxLayers = "FABLE_LITE_TERRAIN_MAX_LAYERS";
    private const string OneSample = "FABLE_SPEC_TERRAIN_ONE_SAMPLE";
    private const string Include = "res://src/android/terrain_lite_layers.gdshaderinc";

    private static string File(string resPath, [CallerFilePath] string source = "") =>
        System.IO.File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(source)!, "../..", resPath["res://".Length..]))).Replace("\r\n", "\n");

    private static string? ReadInclude(string resPath)
    {
        try
        {
            return File(resPath);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string LiteAt(string path, params string[] extra) =>
        Engine_ShaderCeilingSource.Preprocess(
            Engine_ShaderVariant.WithExtraDefines(
                Engine_ShaderVariant.Compose(File(Engine_ShaderVariant.FullPathOf(path)), path, false, false, false), extra),
            path,
            ReadInclude);

    private static string Lite(params string[] extra) => LiteAt(Engine_ShaderVariant.TerrainLitePath, extra);

    /// <summary>The text of the stage function starting at <paramref name="signature"/>, braces included.</summary>
    private static string Stage(string code, string signature)
    {
        int start = code.IndexOf(signature, StringComparison.Ordinal);
        AssertThat(start).IsGreater(-1);
        int depth = 0;
        for (int i = code.IndexOf('{', start); i < code.Length; i++)
        {
            depth += code[i] == '{' ? 1 : code[i] == '}' ? -1 : 0;
            if (depth == 0)
            {
                return code[start..(i + 1)];
            }
        }

        throw new FormatException("unbalanced " + signature);
    }

    private static int Count(string text, string what) => text.Split(what).Length - 1;

    private static string Fragment(string code) => Stage(code, "void fragment()");

    /// <summary>Nothing changes for a lite build without the defines, and the loop keeps the 0.001 skip.</summary>
    [TestCase]
    public void WithoutTheDefinesNothingIsAdded()
    {
        foreach (string plain in new[] { Lite(), Lite(OneSample) })
        {
            AssertThat(plain.Contains("tl_bits", StringComparison.Ordinal)).IsFalse();
            AssertThat(plain.Contains("findLSB", StringComparison.Ordinal)).IsFalse();
            AssertThat(plain.Contains("FABLE_LITE_TERRAIN", StringComparison.Ordinal)).IsFalse();
            AssertThat(plain.Contains("if (w <= 0.001)", StringComparison.Ordinal)).IsTrue();
            AssertThat(plain.Contains("filter_linear_mipmap_anisotropic", StringComparison.Ordinal)).IsTrue();
            AssertThat(plain.Contains("v_fog_t", StringComparison.Ordinal)).IsFalse();
        }

        string full = Engine_ShaderCeilingSource.Preprocess(
            File(Engine_ShaderVariant.TerrainFullPath), Engine_ShaderVariant.TerrainFullPath, ReadInclude);
        AssertThat(full.Contains("tl_bits", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    /// The bit loop: one pass per set bit of the folded mask, then the fallback pass, through one
    /// `sample_layer` and one weight fetch, with or without the single-fetch build.
    /// </summary>
    [TestCase]
    public void TheBitLoopVisitsTheSetBitsOnly()
    {
        foreach (string code in new[] { Lite(BitLoop), Lite(OneSample, BitLoop) })
        {
            string fragment = Fragment(code);
            AssertThat(fragment.Contains("int mat = int(findLSB(tl_bits));", StringComparison.Ordinal)).IsTrue();
            AssertThat(fragment.Contains("tl_bits &= tl_bits - 1u;", StringComparison.Ordinal)).IsTrue();
            AssertThat(fragment.Contains("uint(terrain_active_material_mask)", StringComparison.Ordinal)).IsTrue();
            AssertThat(Count(fragment, "sample_layer(")).IsEqual(1);
            AssertThat(Count(fragment, "textureLod(material_weight")).IsEqual(1);
            // The body's own loops are gone: no per-index pass, no second fallback copy.
            AssertThat(fragment.Contains("mat <= 32; mat++", StringComparison.Ordinal)).IsFalse();
            AssertThat(fragment.Contains("mat < 32; mat++", StringComparison.Ordinal)).IsFalse();
            // Nothing is cut, so nothing is rescaled and no list is kept.
            AssertThat(fragment.Contains("tl_w[", StringComparison.Ordinal)).IsFalse();
            AssertThat(fragment.Contains("tl_low_w", StringComparison.Ordinal)).IsFalse();
        }
    }

    /// <summary>The floor alone: the bit loop with the skip, the strongest light layer kept for the fallback pass, the rescale.</summary>
    [TestCase]
    public void MinWeightSkipsTheLightLayersAndRescales()
    {
        string fragment = Fragment(Lite(OneSample, MinWeight));
        AssertThat(fragment.Contains("if (w < FABLE_LITE_TERRAIN_MIN_WEIGHT_VALUE)", StringComparison.Ordinal)).IsTrue();
        AssertThat(fragment.Contains("slice = tl_low_i;", StringComparison.Ordinal)).IsTrue();
        AssertThat(fragment.Contains("float restore = tl_total / weight_sum;", StringComparison.Ordinal)).IsTrue();
        AssertThat(Count(fragment, "sample_layer(")).IsEqual(1);
        AssertThat(Count(fragment, "textureLod(material_weight")).IsEqual(1);
        AssertThat(fragment.Contains("tl_w[", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>The cap: a strongest-first list filled in one weight pass, then a sampling pass over it.</summary>
    [TestCase]
    public void MaxLayersKeepsTheStrongest()
    {
        string fragment = Fragment(Lite(OneSample, MaxLayers));
        AssertThat(fragment.Contains("float tl_w[FABLE_LITE_TERRAIN_MAX_LAYERS_VALUE];", StringComparison.Ordinal)).IsTrue();
        AssertThat(fragment.Contains("if (cw > tl_w[k])", StringComparison.Ordinal)).IsTrue();
        AssertThat(fragment.Contains("sample_layer(tl_i[k]", StringComparison.Ordinal)).IsTrue();
        AssertThat(Count(fragment, "sample_layer(")).IsEqual(1);
        AssertThat(Count(fragment, "textureLod(material_weight")).IsEqual(1);
        AssertThat(fragment.Contains("tl_dropped = tl_sampled < tl_weighed;", StringComparison.Ordinal)).IsTrue();
        AssertThat(fragment.Contains("< FABLE_LITE_TERRAIN_MIN_WEIGHT_VALUE", StringComparison.Ordinal)).IsFalse();

        string both = Fragment(Lite(OneSample, MaxLayers, MinWeight));
        AssertThat(both.Contains("if (k > 0 && tl_w[k] < FABLE_LITE_TERRAIN_MIN_WEIGHT_VALUE)", StringComparison.Ordinal)).IsTrue();
        AssertThat(Count(both, "sample_layer(")).IsEqual(1);
    }

    /// <summary>Every build that loops (lite, no-cliff, flat, flat no-cliff, both models) takes the include; the page has no loop.</summary>
    [TestCase]
    public void EveryLoopingBuildTakesTheLevers()
    {
        foreach (string path in Engine_ShaderVariant.AllVariantPaths.Where(Engine_ShaderVariant.IsTerrainSplat))
        {
            string code = LiteAt(path, OneSample, BitLoop, MaxLayers, MinWeight);
            bool page = path.EndsWith("_page.gdshader", StringComparison.Ordinal);
            AssertThat(code.Contains("tl_bits", StringComparison.Ordinal)).IsEqual(!page);
        }
    }

    /// <summary>The value macros' defaults are the include's, and the body defines none of the loop's.</summary>
    [TestCase]
    public void TheDefaultsLiveInTheAndroidInclude()
    {
        string include = File(Include);
        AssertThat(include.Contains("#define FABLE_LITE_TERRAIN_MAX_LAYERS_VALUE 3", StringComparison.Ordinal)).IsTrue();
        AssertThat(include.Contains("#define FABLE_LITE_TERRAIN_MIN_WEIGHT_VALUE 0.08", StringComparison.Ordinal)).IsTrue();
        string body = File("res://src/Engine/level/terrain/terrain_splat_body.gdshaderinc");
        AssertThat(body.Contains("#define FABLE_LITE_TERRAIN_MAX_LAYERS_VALUE", StringComparison.Ordinal)).IsFalse();
        AssertThat(Count(body, "#include \"" + Include + "\"")).IsEqual(1);

        // A full (non-lite) build never takes it, even with the define stated.
        string full = Engine_ShaderCeilingSource.Preprocess(
            "#define " + BitLoop + "\n" + File(Engine_ShaderVariant.TerrainFullPath), Engine_ShaderVariant.TerrainFullPath, ReadInclude);
        AssertThat(full.Contains("tl_bits", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>The cliff sharpening: the band narrowed and each plane fetched only where it weighs anything.</summary>
    [TestCase]
    public void CliffSharpeningFetchesALivePlaneOnly()
    {
        string plain = Lite(OneSample);
        string sharp = Lite(OneSample, "FABLE_LITE_TERRAIN_CLIFF_SHARP");
        // The body hooks the phone's sharpened blend in place of its own; the default band lives there.
        AssertThat(File("res://src/Engine/level/terrain/terrain_splat_body.gdshaderinc")
            .Contains("#ifdef FABLE_LITE_TERRAIN_CLIFF_SHARP\n#include \"res://src/android/terrain_lite_cliff_sharp.gdshaderinc\"\n#else", StringComparison.Ordinal)).IsTrue();
        AssertThat(File("res://src/android/terrain_lite_cliff_sharp.gdshaderinc")
            .Contains("#define FABLE_LITE_TERRAIN_CLIFF_SHARP_VALUE 0.35", StringComparison.Ordinal)).IsTrue();
        AssertThat(sharp.Contains("pw = clamp((pw - FABLE_LITE_TERRAIN_CLIFF_SHARP_VALUE)", StringComparison.Ordinal)).IsTrue();
        AssertThat(sharp.Contains("if (pw.x > 0.0) {", StringComparison.Ordinal)).IsTrue();
        AssertThat(sharp.Contains("if (pw.y > 0.0) {", StringComparison.Ordinal)).IsTrue();
        AssertThat(plain.Contains("if (pw.x > 0.0) {", StringComparison.Ordinal)).IsFalse();
        // Two cliff colour fetches either way; the no-cliff build has none to sharpen.
        AssertThat(Count(sharp, "textureGrad(lod_detail_array")).IsEqual(Count(plain, "textureGrad(lod_detail_array"));
        string noCliff = LiteAt(Engine_ShaderVariant.TerrainLiteNoCliffPath, OneSample, "FABLE_LITE_TERRAIN_CLIFF_SHARP");
        AssertThat(noCliff.Contains("if (pw.x > 0.0) {", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>The trilinear filter changes the two colour arrays' sampler hints and nothing else.</summary>
    [TestCase]
    public void TrilinearChangesOnlyTheColourSamplers()
    {
        string plain = Lite(OneSample);
        string tri = Lite(OneSample, "FABLE_LITE_TERRAIN_TRILINEAR");
        // The two colour arrays take their filter from FABLE_TERRAIN_LOD_FILTER (fable_lite_switches in a lite
        // build, upstream's anisotropic otherwise), and the define changes only that macro's value.
        foreach (string code in new[] { plain, tri })
        {
            AssertThat(code.Contains("uniform sampler2DArray lod_array : FABLE_TERRAIN_LOD_FILTER, repeat_enable;", StringComparison.Ordinal)).IsTrue();
            AssertThat(code.Contains("uniform sampler2DArray lod_detail_array : FABLE_TERRAIN_LOD_FILTER, repeat_enable;", StringComparison.Ordinal)).IsTrue();
            AssertThat(System.Text.RegularExpressions.Regex.Matches(code, @"\bFABLE_TERRAIN_LOD_FILTER\b").Count).IsEqual(3);
        }

        AssertThat(tri.Split('\n').Contains("#define FABLE_TERRAIN_LOD_FILTER filter_linear_mipmap")).IsTrue();
        AssertThat(plain.Split('\n').Contains("#define FABLE_TERRAIN_LOD_FILTER filter_linear_mipmap_anisotropic")).IsTrue();
        static string Rest(string code) => string.Join('\n', code.Split('\n')
            .Where(l => l.Trim().Length > 0 && !l.StartsWith("#define FABLE_TERRAIN_LOD_FILTER ", StringComparison.Ordinal) &&
                !l.Contains("FABLE_LITE_TERRAIN_TRILINEAR", StringComparison.Ordinal)));
        AssertThat(Rest(tri)).IsEqual(Rest(plain));
    }

    /// <summary>The ground's vertex fog: the terms in vertex(), only the apply in fragment(); the props' define leaves the ground alone.</summary>
    [TestCase]
    public void VertexFogMovesTheTermsToTheVertexStage()
    {
        string code = Lite(OneSample, "FABLE_LITE_TERRAIN_VERTEX_FOG");
        string vertex = Stage(code, "void vertex()");
        string fragment = Fragment(code);
        AssertThat(vertex.Contains("fable_atmosphere_terms(", StringComparison.Ordinal)).IsTrue();
        AssertThat(fragment.Contains("fable_atmosphere_terms(", StringComparison.Ordinal)).IsFalse();
        AssertThat(fragment.Contains("fable_atmosphere_apply(v_fog_t, v_fog_c, 1.0, ALBEDO, EMISSION);", StringComparison.Ordinal)).IsTrue();

        string plain = Lite(OneSample);
        AssertThat(Fragment(plain).Contains("fable_atmosphere_terms(", StringComparison.Ordinal)).IsTrue();
        AssertThat(Stage(plain, "void vertex()").Contains("fable_atmosphere_terms(", StringComparison.Ordinal)).IsFalse();
    }
}
