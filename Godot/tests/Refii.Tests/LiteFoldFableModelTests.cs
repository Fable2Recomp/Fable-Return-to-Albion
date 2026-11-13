using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GdUnit4;
using Refii.Launcher.Core;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// `lite_fold_fable_model`: FABLE_LITE_FOLD_FABLE_MODEL composed into the lite builds, which turns the
/// fable model's fixed globals (fable_unified, the sky tether's weight, the interior and its bake
/// lift, the fill floor) into constants in the fable-model builds and leaves the modern ones alone.
/// </summary>
[TestSuite]
public sealed class LiteFoldFableModelTests
{
    private const string PropLite = "res://src/Engine/material/lite/standard_material_lite.gdshader";
    private const string PropModern = "res://src/Engine/material/lite/modern/standard_material_lite.gdshader";
    private static readonly string TerrainLite = Engine_ShaderVariant.TerrainLitePath;
    private const string Define = "FABLE_LITE_FOLD_FABLE_MODEL";

    /// <summary>A read of one of the folded globals, not its declaration.</summary>
    private static readonly Regex Read = new(
        @"(?<!uniform \w+ )\b(fable_unified|fable_interior|fable_interior_bake|fable_fill_floor)\b|fable_sky_ambient\.w",
        RegexOptions.Compiled);

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
                Engine_ShaderVariant.Compose(File(Engine_ShaderVariant.FullPathOf(litePath)), litePath, true, true, true), extra),
            litePath,
            Include);

    /// <summary>The code lines that read a folded global (comments and declarations aside).</summary>
    private static string[] Reads(string code) => code.Split('\n')
        .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.Contains("global uniform", StringComparison.Ordinal))
        .Where(l => Read.IsMatch(l))
        .ToArray();

    [TestCase]
    public void TheKeyIsRegisteredOffWithAPlayersName()
    {
        RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == Engine_ShaderVariant.FoldFableModelKey);
        AssertThat(row).IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("false");
        AssertThat(AndroidSettingNames.Keys.Contains(Engine_ShaderVariant.FoldFableModelKey)).IsTrue();
        AssertThat(Engine_ShaderVariant.FoldFableModel).IsEqual(Define);
    }

    [TestCase]
    public void UnsetIsOffAndTrueIsOn()
    {
        AssertThat(Engine_ShaderVariant.FoldFableModelWanted(new Dictionary<string, string>())).IsFalse();
        AssertThat(Engine_ShaderVariant.FoldFableModelWanted(new Dictionary<string, string> { [Engine_ShaderVariant.FoldFableModelKey] = "true" })).IsTrue();
        AssertThat(Engine_ShaderVariant.LeverDefines(true)).IsEqual(new[] { Define });
        AssertThat(Engine_ShaderVariant.LeverDefines(false)).IsEmpty();
    }

    /// <summary>Under the define a fable-model prop or ground build reads none of the folded globals.</summary>
    [TestCase]
    public void TheFableBuildsReadNoneOfThem()
    {
        AssertThat(Reads(Lite(PropLite))).IsNotEmpty();
        AssertThat(Reads(Lite(PropLite, Define))).IsEmpty();
        AssertThat(Reads(Lite(TerrainLite))).IsNotEmpty();
        AssertThat(Reads(Lite(TerrainLite, Define))).IsEmpty();
    }

    /// <summary>The modern builds keep the globals: only the define's own line differs.</summary>
    [TestCase]
    public void TheModernBuildsAreUnchanged()
    {
        static string Code(string text) => string.Join('\n', text.Split('\n').Where(l => l.Trim().Length > 0 && !l.Contains(Define, StringComparison.Ordinal)));
        AssertThat(Code(Lite(PropModern, Define))).IsEqual(Code(Lite(PropModern)));
    }
}
