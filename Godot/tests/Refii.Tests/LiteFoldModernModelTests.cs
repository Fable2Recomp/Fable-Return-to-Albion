using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.Launcher.Core;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// `lite_fold_modern_model`: FABLE_LITE_FOLD_MODERN_MODEL (fable_unified 1) and, when the AO dials start
/// at their defaults, FABLE_LITE_FOLD_AO_DEFAULTS (strength 1, light-affect 0.25) compiled into the
/// modern lite builds; the fable builds are left alone.
/// </summary>
[TestSuite]
public sealed class LiteFoldModernModelTests
{
    private const string PropFable = "res://src/Engine/material/lite/standard_material_lite.gdshader";
    private const string PropModern = "res://src/Engine/material/lite/modern/standard_material_lite.gdshader";
    private static readonly string TerrainModern = Engine_ShaderVariant.TerrainLitePath.Replace("/lite/", "/lite/modern/", StringComparison.Ordinal);
    private const string Fold = "FABLE_LITE_FOLD_MODERN_MODEL";
    private const string Ao = "FABLE_LITE_FOLD_AO_DEFAULTS";

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

    // Baked AO on, as the modern builds compose it by default (NoBakedAoFor is false under modern).
    private static string Lite(string litePath, params string[] extra) =>
        Engine_ShaderCeilingSource.Preprocess(
            Engine_ShaderVariant.WithExtraDefines(
                Engine_ShaderVariant.Compose(File(Engine_ShaderVariant.FullPathOf(litePath)), litePath, false, true, true), extra),
            litePath,
            Include);

    /// <summary>The code lines that read one of the globals the defines fold (comments and declarations aside).</summary>
    private static string[] Reads(string code, string global) => code.Split('\n')
        .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.Contains("global uniform", StringComparison.Ordinal))
        .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"\b" + global + @"\b"))
        .ToArray();

    private static string Code(string text, params string[] drop) => string.Join('\n', text.Split('\n')
        .Where(l => l.Trim().Length > 0 && !drop.Any(d => l.Contains(d, StringComparison.Ordinal))));

    [TestCase]
    public void TheKeyIsRegisteredOffWithAPlayersName()
    {
        RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == Engine_ShaderVariant.FoldModernModelKey);
        AssertThat(row).IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("false");
        AssertThat(AndroidSettingNames.Keys.Contains(Engine_ShaderVariant.FoldModernModelKey)).IsTrue();
        AssertThat(Engine_ShaderVariant.FoldModernModel).IsEqual(Fold);
        AssertThat(Engine_ShaderVariant.FoldAoDefaults).IsEqual(Ao);
    }

    [TestCase]
    public void TheDefinesFollowTheKeyAndTheDefaults()
    {
        AssertThat(Engine_ShaderVariant.FoldModernModelWanted(new Dictionary<string, string>())).IsFalse();
        AssertThat(Engine_ShaderVariant.FoldModernModelWanted(new Dictionary<string, string> { [Engine_ShaderVariant.FoldModernModelKey] = "true" })).IsTrue();
        AssertThat(Engine_ShaderVariant.LeverDefines(false, true, true)).IsEqual(new[] { Fold, Ao });
        AssertThat(Engine_ShaderVariant.LeverDefines(false, true, false)).IsEqual(new[] { Fold });
        // The AO constants only ever come with the key.
        AssertThat(Engine_ShaderVariant.LeverDefines(false, false, true)).IsEmpty();
        AssertThat(Engine_ShaderVariant.LeverDefines(true, true, true)).IsEqual(new[] { "FABLE_LITE_FOLD_FABLE_MODEL", Fold, Ao });
        AssertThat(Engine_ShaderVariant.AoAtDefaults(true, 1.0f, 0.25f)).IsTrue();
        AssertThat(Engine_ShaderVariant.AoAtDefaults(false, 1.0f, 0.25f)).IsFalse();
        AssertThat(Engine_ShaderVariant.AoAtDefaults(true, 0.8f, 0.25f)).IsFalse();
        AssertThat(Engine_ShaderVariant.AoAtDefaults(true, 1.0f, 0.5f)).IsFalse();
    }

    /// <summary>Under both defines a modern prop or ground build reads neither fable_unified nor the light-affect, and the strength's pow is gone.</summary>
    [TestCase]
    public void TheModernBuildsFoldThem()
    {
        foreach (string path in new[] { PropModern, TerrainModern })
        {
            string plain = Lite(path);
            AssertThat(Reads(plain, "fable_unified")).IsNotEmpty();
            AssertThat(Reads(plain, "fable_ao_light_affect")).IsNotEmpty();
            string folded = Lite(path, Fold, Ao);
            AssertThat(Reads(folded, "fable_unified")).IsEmpty();
            AssertThat(Reads(folded, "fable_ao_light_affect")).IsEmpty();
            AssertThat(Reads(folded, "fable_ao_strength")).IsEmpty();
            AssertThat(Reads(Lite(path, Fold), "fable_ao_strength")).IsNotEmpty();
        }
    }

    /// <summary>The fable builds ignore both: only the defines' own lines differ.</summary>
    [TestCase]
    public void TheFableBuildsAreUnchanged()
    {
        AssertThat(Code(Lite(PropFable, Fold, Ao), Fold, Ao)).IsEqual(Code(Lite(PropFable)));
    }
}
