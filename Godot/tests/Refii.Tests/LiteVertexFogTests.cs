using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// FABLE_LITE_VERTEX_FOG (composed through <c>lite_extra_defines</c>): the props' atmosphere terms
/// are computed in vertex() and only applied in fragment(); without the define nothing changes.
/// </summary>
[TestSuite]
public sealed class LiteVertexFogTests
{
    private const string PropLite = "res://src/Engine/material/lite/standard_material_lite.gdshader";
    private static readonly string TerrainLite = Engine_ShaderVariant.TerrainLitePath;
    private const string Define = "FABLE_LITE_VERTEX_FOG";
    private const string Terms = "fable_atmosphere_terms_for(";

    private static string File(string resPath, [CallerFilePath] string source = "") =>
        System.IO.File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(source)!, "../..", resPath["res://".Length..]))).Replace("\r\n", "\n");

    private static string? Include(string resPath)
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

    private static string Lite(string litePath, params string[] extra) =>
        Engine_ShaderCeilingSource.Preprocess(
            Engine_ShaderVariant.WithExtraDefines(
                Engine_ShaderVariant.Compose(File(Engine_ShaderVariant.FullPathOf(litePath)), litePath, false, false, false), extra),
            litePath,
            Include);

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

    /// <summary>Under the define the fog's LUT fetch and view maths are in vertex() alone; fragment() only applies the varyings.</summary>
    [TestCase]
    public void FogTermsLeaveTheFragmentStage()
    {
        string code = Lite(PropLite, Define);
        string fragment = Stage(code, "void fragment()");
        string vertex = Stage(code, "void vertex()");

        AssertThat(vertex.Contains(Terms, StringComparison.Ordinal)).IsTrue();
        AssertThat(fragment.Contains(Terms, StringComparison.Ordinal)).IsFalse();
        AssertThat(fragment.Contains("atmos_inscatter", StringComparison.Ordinal)).IsFalse();
        AssertThat(fragment.Contains("fable_atmosphere_apply(v_fog_t, v_fog_c", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>Without the define the props fog per pixel exactly as before, and no fog varyings exist.</summary>
    [TestCase]
    public void WithoutTheDefineTheFogIsPerPixel()
    {
        string code = Lite(PropLite);
        AssertThat(Stage(code, "void fragment()").Contains(Terms, StringComparison.Ordinal)).IsTrue();
        AssertThat(Stage(code, "void vertex()").Contains(Terms, StringComparison.Ordinal)).IsFalse();
        AssertThat(code.Contains("v_fog_t", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>The terrain keeps its per-pixel fog under the define.</summary>
    [TestCase]
    public void TheTerrainIsUnchanged()
    {
        // Only the define's own line may differ; blank lines are what the preprocessor leaves of a directive.
        static string Code(string text) => string.Join('\n', text.Split('\n').Where(l => l.Trim().Length > 0 && !l.Contains(Define, StringComparison.Ordinal)));
        AssertThat(Code(Lite(TerrainLite, Define))).IsEqual(Code(Lite(TerrainLite)));
    }
}
