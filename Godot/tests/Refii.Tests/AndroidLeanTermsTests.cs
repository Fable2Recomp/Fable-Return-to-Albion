using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Core;
using Refii.Launcher.Core;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// `lite_lean_light_ao` (FABLE_LITE_LEAN_LIGHT_AO: light()'s two AO helpers from
/// src/android/fable_lite_lean_light.gdshaderinc) and `lite_lean_frame_sign` (FABLE_LITE_LEAN_FRAME_SIGN:
/// the fast normal maps' frame sign from the frame's own cross product).
/// </summary>
[TestSuite]
public sealed class AndroidLeanTermsTests
{
    private const string PropFable = "res://src/Engine/material/lite/standard_material_culled_lite.gdshader";
    private const string PropModern = "res://src/Engine/material/lite/modern/standard_material_culled_lite.gdshader";
    private static readonly string TerrainModern = Engine_ShaderVariant.TerrainFlatPath.Replace("/lite/", "/lite/modern/", StringComparison.Ordinal);
    private const string LightAo = "FABLE_LITE_LEAN_LIGHT_AO";
    private const string FrameSign = "FABLE_LITE_LEAN_FRAME_SIGN";
    private const string Deriv = "FABLE_LITE_NM_DERIV";
    private const string UpstreamClamp = "max(0.0, min(1.0, combined_ao))";
    private const string LeanSpecular = "spec_occ = min(1.0, ao_c * ao_c + 0.15 * ao_c)";

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

    private static string Lite(string litePath, bool noBakedAo, params string[] extra) =>
        Engine_ShaderCeilingSource.Preprocess(
            Engine_ShaderVariant.WithExtraDefines(
                Engine_ShaderVariant.Compose(File(Engine_ShaderVariant.FullPathOf(litePath)), litePath, noBakedAo, true, true), extra),
            litePath,
            Include);

    /// <summary>The code lines (comments aside) that contain <paramref name="text"/>.</summary>
    private static string[] Lines(string code, string text) => code.Split('\n')
        .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
        .Where(l => l.Contains(text, StringComparison.Ordinal))
        .ToArray();

    [TestCase]
    public void TheKeysAreRegisteredWithPlayersNames()
    {
        RuntimeCVarDefinition? lightAo = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == AndroidLeanTerms.LightAoKey);
        AssertThat(lightAo).IsNotNull();
        AssertThat(lightAo!.DefaultValue).IsEqual("true");
        RuntimeCVarDefinition? frameSign = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == AndroidLeanTerms.FrameSignKey);
        AssertThat(frameSign).IsNotNull();
        AssertThat(frameSign!.DefaultValue).IsEqual("false");
        AssertThat(AndroidSettingNames.Keys.Contains(AndroidLeanTerms.LightAoKey)).IsTrue();
        AssertThat(AndroidSettingNames.Keys.Contains(AndroidLeanTerms.FrameSignKey)).IsTrue();
    }

    [TestCase]
    public void TheDefinesFollowTheKeys()
    {
        var none = new Dictionary<string, string>();
        AssertThat(AndroidLeanTerms.LightAoWanted(none)).IsTrue();
        AssertThat(AndroidLeanTerms.FrameSignWanted(none)).IsFalse();
        AssertThat(AndroidLeanTerms.LightAoWanted(new Dictionary<string, string> { [AndroidLeanTerms.LightAoKey] = "false" })).IsFalse();
        AssertThat(AndroidLeanTerms.FrameSignWanted(new Dictionary<string, string> { [AndroidLeanTerms.FrameSignKey] = "true" })).IsTrue();
        AssertThat(AndroidLeanTerms.DefinesFor(false, false)).IsEmpty();
        AssertThat(AndroidLeanTerms.DefinesFor(true, false)).IsEqual(new[] { LightAo });
        AssertThat(AndroidLeanTerms.DefinesFor(false, true)).IsEqual(new[] { FrameSign });
        AssertThat(AndroidLeanTerms.DefinesFor(true, true)).IsEqual(new[] { LightAo, FrameSign });
        AssertThat(AndroidLeanTerms.LightAoDefine).IsEqual(LightAo);
        AssertThat(AndroidLeanTerms.FrameSignDefine).IsEqual(FrameSign);
    }

    /// <summary>With the define, the modern builds (props and ground) take the two helpers once each, without upstream's clamps.</summary>
    [TestCase]
    public void TheLeanLightAoReplacesUpstreamsTwo()
    {
        foreach (string path in new[] { PropModern, TerrainModern })
        {
            string plain = Lite(path, false);
            AssertThat(Lines(plain, UpstreamClamp).Length).OverrideFailureMessage(path).IsEqual(2);
            AssertThat(Lines(plain, LeanSpecular)).OverrideFailureMessage(path).IsEmpty();

            string lean = Lite(path, false, LightAo);
            AssertThat(Lines(lean, UpstreamClamp)).OverrideFailureMessage(path).IsEmpty();
            AssertThat(Lines(lean, LeanSpecular).Length).OverrideFailureMessage(path).IsEqual(1);
            AssertThat(Lines(lean, "float fable_direct_ao_for_light(").Length).OverrideFailureMessage(path).IsEqual(1);
            AssertThat(Lines(lean, "float fable_specular_ao(").Length).OverrideFailureMessage(path).IsEqual(1);
            AssertThat(Lines(lean, "fable_direct_ao_for_light(v_combined_ao, LIGHT_IS_DIRECTIONAL)")).OverrideFailureMessage(path).IsNotEmpty();
        }
    }

    /// <summary>Where baked AO is compiled out upstream's helpers stay (they return 1); the full shaders never see the include.</summary>
    [TestCase]
    public void WithoutBakedAoOrTheDefineUpstreamsStay()
    {
        foreach (string path in new[] { PropFable, PropModern })
        {
            string noAo = Lite(path, true, LightAo);
            AssertThat(Lines(noAo, LeanSpecular)).OverrideFailureMessage(path).IsEmpty();
            AssertThat(Lines(noAo, "float fable_specular_ao(").Length).OverrideFailureMessage(path).IsEqual(1);
        }

        foreach (string full in new[] { Engine_ShaderFamily.StandardCulledPath, Engine_ShaderVariant.TerrainFullPath })
        {
            string code = Engine_ShaderCeilingSource.Preprocess(File(full), full, Include);
            AssertThat(code.Contains(LeanSpecular, StringComparison.Ordinal)).OverrideFailureMessage(full).IsFalse();
            AssertThat(Lines(code, UpstreamClamp).Length).OverrideFailureMessage(full).IsEqual(2);
        }
    }

    /// <summary>The fast normal maps' frame sign: upstream's triple product by default, the frame's cross product with the define.</summary>
    [TestCase]
    public void TheFrameSignTakesTheFramesCrossProduct()
    {
        const string upstream = "dot(NORMAL, cross(dp1, dp2))";
        const string lean = "dot(dp1perp, dp2)";
        string plain = Lite(PropModern, false, Deriv);
        AssertThat(Lines(plain, upstream).Length).IsEqual(1);
        AssertThat(Lines(plain, lean)).IsEmpty();
        string signed = Lite(PropModern, false, Deriv, FrameSign);
        AssertThat(Lines(signed, upstream)).IsEmpty();
        AssertThat(Lines(signed, lean).Length).IsEqual(1);
    }
}
