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
/// `lite_half_precision_lighting`: a phone setting (default off) that composes FABLE_LITE_MEDIUMP into
/// the lite builds, and the rule that keeps that define's qualifiers parseable by Godot's Vulkan driver:
/// `mediump` on locals only. re-spirv, the pass that folds a pipeline's specialisation constants, fails
/// on RelaxedPrecision on an OpFunctionParameter ("SPIR-V Parsing error. Invalid instruction word
/// count") and the shader then goes without the pass.
/// </summary>
[TestSuite]
public sealed class LiteHalfPrecisionLightingTests
{
    private const string Key = "lite_half_precision_lighting";

    private static string File(string resPath, [CallerFilePath] string source = "") =>
        System.IO.File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(source)!, "../..", resPath["res://".Length..]))).Replace("\r\n", "\n");

    private static string? Include(string resPath, [CallerFilePath] string source = "")
    {
        string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", resPath["res://".Length..]));
        return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path).Replace("\r\n", "\n") : null;
    }

    /// <summary>Every lite build (both sets, terrain included) with <paramref name="extra"/>, preprocessed,
    /// FABLE_LITE_MP expanded as Godot's preprocessor will expand it.</summary>
    private static (string Path, string Active)[] Builds(params string[] extra) => Engine_ShaderVariant.AllVariantPaths
        .Select(p =>
        {
            string code = Engine_ShaderVariant.WithExtraDefines(
                Engine_ShaderVariant.Compose(File(Engine_ShaderVariant.FullPathOf(p)), p), extra);
            string text = Engine_ShaderCeilingSource.Preprocess(code, p, r => Include(r));
            Match value = Regex.Match(text, @"^#define\s+FABLE_LITE_MP\b(.*)$", RegexOptions.Multiline);
            return (p, Regex.Replace(text, @"\bFABLE_LITE_MP\b", value.Success ? value.Groups[1].Value.Trim() : string.Empty));
        })
        .ToArray();

    /// <summary>A function header, whitespace collapsed: return type, name, the parameter list.</summary>
    private static readonly Regex Header = new(
        @"\b([A-Za-z_]\w*)\s+([A-Za-z_]\w*)\s*\(([^(){};]*)\)\s*\{", RegexOptions.CultureInvariant);

    private static readonly Regex QualifiedReturn = new(
        @"\b(?:mediump|lowp)\s+\w+\s+[A-Za-z_]\w*\s*\([^(){};]*\)\s*\{", RegexOptions.CultureInvariant);

    [TestCase]
    public void TheKeyIsRegisteredOffWithAPlayersName()
    {
        RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == Key);
        AssertThat(row).IsNotNull();
        AssertThat(row!.Type).IsEqual(RuntimeCVarType.Bool);
        AssertThat(row.DefaultValue).IsEqual("false");
        AssertThat(Engine_ShaderVariant.HalfPrecisionLightingKey).IsEqual(Key);
        AssertThat(AndroidSettingNames.Keys.Contains(Key)).IsTrue();
        string? name;
        bool offered = GraphicsPresets.Offered;
        try
        {
            GraphicsPresets.Offered = true;
            name = AndroidSettingNames.Name(Key);
            AssertThat(AndroidSettingNames.About(Key)).IsNotNull();
        }
        finally { GraphicsPresets.Offered = offered; }

        AssertThat(name).IsNotNull();
        foreach (string word in new[] { "Lite", "Android", "mediump", "_" })
        {
            AssertThat(name!.Contains(word, StringComparison.Ordinal)).OverrideFailureMessage(name + " says " + word).IsFalse();
        }

        // Not a preset's: every preset leaves it off.
        AssertThat(GraphicsPresets.Members.Contains(Key)).IsFalse();
    }

    [TestCase]
    public void UnsetIsOffAndTrueIsOn()
    {
        AssertThat(Engine_ShaderVariant.HalfPrecisionLightingWanted(new Dictionary<string, string>())).IsFalse();
        AssertThat(Engine_ShaderVariant.HalfPrecisionLightingWanted(new Dictionary<string, string> { [Key] = "true" })).IsTrue();
        AssertThat(Engine_ShaderVariant.HalfPrecisionLightingWanted(new Dictionary<string, string> { [Key] = "false" })).IsFalse();
    }

    [TestCase]
    public void TheProcessDefinesCarryItAfterTheBlackLightsAheadOfTheExtras()
    {
        string[] extra = { "FABLE_LITE_VERTEX_FOG" };
        AssertThat(Engine_ShaderVariant.ProcessDefines(true, true, extra))
            .IsEqual(new[] { Engine_ShaderVariant.SkipBlackLights, Engine_ShaderVariant.Mediump, "FABLE_LITE_VERTEX_FOG" });
        AssertThat(Engine_ShaderVariant.ProcessDefines(false, false, extra)).IsEqual(extra);
        AssertThat(Engine_ShaderVariant.ProcessDefines(true, extra)).NotContains(Engine_ShaderVariant.Mediump);

        // The setting and `lite_extra_defines = FABLE_LITE_MEDIUMP` together define it once.
        string code = "#define FABLE_LITE\n#include \"body.gdshaderinc\"\n";
        string both = Engine_ShaderVariant.WithExtraDefines(code,
            Engine_ShaderVariant.ProcessDefines(false, true, new[] { Engine_ShaderVariant.Mediump }));
        AssertThat(Regex.Matches(both, @"#define\s+FABLE_LITE_MEDIUMP\b").Count).IsEqual(1);
    }

    /// <summary>No lite build puts a precision qualifier on a
    /// function's parameter or return type; and each one still qualifies its lighting maths.</summary>
    [TestCase]
    public void TheQualifiersLandOnLocalsOnly()
    {
        foreach (string[] extra in new[]
                 {
                     new[] { Engine_ShaderVariant.Mediump },
                     new[] { Engine_ShaderVariant.Mediump, "FABLE_LITE_VERTEX_FOG", "FABLE_LITE_NM_DERIV" },
                 })
        {
            (string Path, string Active)[] builds = Builds(extra);
            AssertThat(builds.Length).IsGreater(30);
            foreach ((string path, string active) in builds)
            {
                string label = $"{path} [{string.Join(',', extra)}]";
                string flat = Regex.Replace(active, @"\s+", " ");
                foreach (Match header in Header.Matches(flat))
                {
                    AssertThat(Regex.IsMatch(header.Groups[3].Value, @"\b(?:mediump|lowp|highp)\b"))
                        .OverrideFailureMessage($"{label}: a qualified parameter in {header.Groups[2].Value}({header.Groups[3].Value})")
                        .IsFalse();
                }

                AssertThat(QualifiedReturn.Match(flat).Value).OverrideFailureMessage($"{label}: a qualified return type").IsEmpty();
                AssertThat(Regex.Matches(flat, @"\bmediump\b").Count).OverrideFailureMessage($"{label}: no qualifier").IsGreater(3);
            }
        }
    }

    /// <summary>The body of the first <c>float <paramref name="name"/>(...) { ... }</c> in <paramref name="code"/>,
    /// braces matched; null when the build has no such function.</summary>
    private static string? Body(string code, string name)
    {
        Match head = Regex.Match(code, @"\bfloat\s+" + name + @"\s*\([^(){};]*\)\s*\{");
        if (!head.Success) return null;
        int depth = 1;
        for (int i = head.Index + head.Length; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[(head.Index + head.Length)..i];
        }

        return null;
    }

    /// <summary>The direct light's AO weight stays full precision in every build, both of its spellings
    /// (upstream's and `lite_lean_light_ao`'s): relaxed, an Adreno 640 drew every sun- or moon-facing lite
    /// surface pure white under the modern model (fable_lite_lighting.gdshaderinc, fable_direct_ao).</summary>
    [TestCase]
    public void TheDirectLightsAoWeightStaysFullPrecision()
    {
        foreach (string[] extra in new[]
                 {
                     new[] { Engine_ShaderVariant.Mediump },
                     new[] { Engine_ShaderVariant.Mediump, Refii.AndroidRuntime.AndroidLeanTerms.LightAoDefine },
                 })
        {
            int modernWithWeight = 0;
            foreach ((string path, string active) in Builds(extra))
            {
                string label = $"{path} [{string.Join(',', extra)}]";
                foreach (string function in new[] { "fable_direct_ao", "fable_direct_ao_for_light" })
                {
                    string? body = Body(active, function);
                    if (body is null) continue;
                    AssertThat(Regex.IsMatch(body, @"\b(?:mediump|lowp)\b"))
                        .OverrideFailureMessage($"{label}: {function} relaxes its weight").IsFalse();
                    if (function == "fable_direct_ao_for_light" && path.Contains("/lite/modern/", StringComparison.Ordinal)
                        && !Regex.IsMatch(body.Trim(), @"^return\s+1\.0\s*;$"))
                    {
                        modernWithWeight++;
                    }
                }

                // The rest of the lighting maths keeps its qualifiers: the fix is the weight alone.
                AssertThat(Regex.IsMatch(active, @"\bmediump\b")).OverrideFailureMessage($"{label}: no qualifier left").IsTrue();
            }

            // Not vacuous: the modern builds compile the weight (the fable model's may compile it out).
            AssertThat(modernWithWeight).OverrideFailureMessage(string.Join(',', extra)).IsGreater(0);
        }
    }

    /// <summary>Without the define (or the setting) no build carries a qualifier.</summary>
    [TestCase]
    public void OffByDefault()
    {
        foreach ((string path, string active) in Builds())
        {
            AssertThat(Regex.IsMatch(active, @"\bmediump\b")).OverrideFailureMessage(path).IsFalse();
        }
    }
}
