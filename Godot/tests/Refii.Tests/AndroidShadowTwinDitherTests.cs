using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// `prop_shadow_twin_whole` (2026-11-16): a prop batch's cast-only twin draws without the fade dither
/// while the dither could discard nothing. Off by default and in no preset.
/// </summary>
[TestSuite]
public sealed class AndroidShadowTwinDitherTests
{
    private static string File(string resPath, [CallerFilePath] string source = "") =>
        System.IO.File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(source)!, "../..", resPath["res://".Length..]))).Replace("\r\n", "\n");

    [TestCase]
    public void KeyIsOffNamedRestartOnlyAndInNoPreset()
    {
        RuntimeCVarDefinition? row = RuntimeCVarsGodot.Definitions.SingleOrDefault(d => d.Name == AndroidShadowTwinDither.Key);
        AssertThat(row).IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("false");
        AssertThat(AndroidSettingNames.Keys.Contains(AndroidShadowTwinDither.Key)).IsTrue();
        foreach (string preset in new[] { GraphicsPresets.Low, GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra })
        {
            AssertThat(GraphicsPresets.Values(preset)!.ContainsKey(AndroidShadowTwinDither.Key)).IsFalse();
        }

        bool offered = GraphicsPresets.Offered;
        try
        {
            GraphicsPresets.Offered = true;
            AssertThat(AndroidSettingNames.About(AndroidShadowTwinDither.Key)).EndsWith("Applies the next time you start the game.");
        }
        finally
        {
            GraphicsPresets.Offered = offered;
        }
    }

    [TestCase]
    public void WholeOnlyWithoutABandAndOnceArrived()
    {
        AssertThat(AndroidShadowTwinDither.Whole(0f, 1f)).IsTrue();
        AssertThat(AndroidShadowTwinDither.Whole(-1f, 1f)).IsTrue();
        AssertThat(AndroidShadowTwinDither.Whole(30f, 1f)).IsFalse();
        AssertThat(AndroidShadowTwinDither.Whole(0f, 0f)).IsFalse();
        AssertThat(AndroidShadowTwinDither.Whole(0f, 0.9995f)).IsFalse();
        AssertThat(AndroidShadowTwinDither.Whole(float.NaN, 1f)).IsTrue();
    }

    [TestCase]
    public void OnlyASingleCasterSurfaceIsEligible()
    {
        const string caster = "res://src/Engine/entities/shadow_caster.gdshader";
        AssertThat(AndroidShadowTwinDither.Eligible(1, caster)).IsTrue();
        AssertThat(AndroidShadowTwinDither.Eligible(2, caster)).IsFalse();
        AssertThat(AndroidShadowTwinDither.Eligible(1, "res://src/Engine/material/standard_material.gdshader")).IsFalse();
        AssertThat(AndroidShadowTwinDither.Eligible(1, null)).IsFalse();
    }

    [TestCase]
    public void WholeCasterIsTheCasterWithoutTheDither()
    {
        string caster = File("res://src/Engine/entities/shadow_caster.gdshader");
        string whole = File(AndroidShadowTwinDither.ShaderPath);

        // The same render mode and pinned instance slots; the same body once the dither is gone.
        foreach (string line in caster.Split('\n').Where(l => l.StartsWith("render_mode") || l.StartsWith("instance uniform")))
        {
            AssertThat(whole).Contains(line);
        }

        // Upstream's layout since #34: `should_discard` from the pass first, the dither folded into it,
        // and ONE tail discard. On Mobile (the phones) the whole caster is that without the dither.
        const string pass = "\tbool should_discard = !IN_SHADOW_PASS;\n";
        const string tail = "\tALBEDO = vec3(0.0);\n\tif (should_discard) {\n\t\tdiscard;\n\t}\n";
        AssertThat(caster).Contains(pass);
        AssertThat(caster).Contains(tail + "}");
        string mobile = Refii.Models.Engine_ShaderCeilingSource.Preprocess(
            whole, AndroidShadowTwinDither.ShaderPath, _ => null, Refii.Models.Engine_ShaderCeilingSource.RendererMobile);
        AssertThat(mobile).Contains("void fragment() {\n" + pass + "\n" + tail + "}");
        AssertThat(mobile.Split('\n').Count(l => !l.TrimStart().StartsWith("//") && l.Contains("discard;"))).IsEqual(1);

        // Forward+ allows a discard only in upstream's own casters (shader-dxil-safety.md): none here.
        string forwardPlus = Refii.Models.Engine_ShaderCeilingSource.Preprocess(
            whole, AndroidShadowTwinDither.ShaderPath, _ => null, Refii.Models.Engine_ShaderCeilingSource.RendererForwardPlus);
        AssertThat(System.Text.RegularExpressions.Regex.IsMatch(forwardPlus, @"\bdiscard\s*;")).IsFalse();
        AssertThat(forwardPlus).Contains("void fragment() {\n\tALBEDO = vec3(0.0);\n}");

        AssertThat(whole).NotContains("void vertex()");
        AssertThat(whole).NotContains("VERTEX");
        AssertThat(whole).NotContains("fable_bayer_threshold");

        // The dither discards only below 0.999 of fade, which Whole never lets through.
        AssertThat(caster).Contains("if (!should_discard && v_fade < 0.999) {");
        AssertThat(caster).Contains("v_fade = xr_fade_alpha;\n\tif (xr_tree_fade_out.x > 0.0) {");
    }

    [TestCase]
    public void HooksReportEveryFadeWriteAndEachTwin()
    {
        string fade = File("res://src/Engine/level/props/Engine_PropFade.cs");
        AssertThat(fade).Contains("Refii.AndroidRuntime.AndroidShadowTwinDither.Band(node, fadeOut.X);");
        AssertThat(fade).Contains("Refii.AndroidRuntime.AndroidShadowTwinDither.Alpha(node, alpha);");
        AssertThat(fade.Split('\n').Count(l => l.Contains("SetInstanceShaderParameter(FadeOut") || l.Contains("SetInstanceShaderParameter(Alpha"))).IsEqual(2);

        string shadow = File("res://src/Engine/level/props/Engine_PropShadow.cs");
        int register = shadow.IndexOf("AndroidShadowTwinDither.Register(twin);", System.StringComparison.Ordinal);
        int copy = shadow.IndexOf("Engine_PropFade.Copy(batch, twin);", System.StringComparison.Ordinal);
        AssertThat(register > 0 && register < copy).IsTrue();
    }
}
