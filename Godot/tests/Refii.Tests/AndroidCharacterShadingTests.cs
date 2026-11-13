using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The characters' own lite builds (<see cref="AndroidCharacterShading"/>: `android_character_lite`,
/// `android_character_normal_maps`, `android_character_fragment_world`, `android_character_ground_ao`) and the
/// far bodies' single pass (`android_character_far_single_pass_metres`, <see cref="AndroidCharacterLevers"/>).
/// </summary>
[TestSuite]
public sealed class AndroidCharacterShadingTests
{
    private const string Culled = "res://src/Engine/material/lite/standard_material_culled_lite.gdshader";
    private const string CulledModern = "res://src/Engine/material/lite/modern/standard_material_culled_lite.gdshader";
    private const string TwoSidedModern = "res://src/Engine/material/lite/modern/standard_material_lite.gdshader";

    private const string Trim = AndroidLeanVaryings.TrimDefine;
    private const string Pack = AndroidLeanVaryings.PackDefine;
    private const string Near = AndroidGroundShading.NearDefine;

    private static readonly AndroidCharacterShading.Process Plain = new(false, false, true, true);

    private static string Lite(string path, params string[] extra) => LiteAoDefinesTests.Lite(path, extra);

    private static string Stage(string code, string stage) => LiteAoDefinesTests.Stage(code, stage);

    private static bool Declares(string code, string name) =>
        Regex.IsMatch(code, @"(?m)^\s*varying\s+(?:\w+\s+)*" + Regex.Escape(name) + @"\s*;");

    private static bool Defines(string code, string line) => code.Split('\n').Any(l => l.Trim() == line);

    private static string Source(string resPath, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", resPath["res://".Length..]))).Replace("\r\n", "\n");

    /// <summary>Every key is a launcher row with its default and a player's name.</summary>
    [TestCase]
    public void TheKeysAreRegisteredWithPlayersNames()
    {
        foreach ((string key, string value, RuntimeCVarType type) in new[]
        {
            (AndroidCharacterShading.LiteKey, "false", RuntimeCVarType.Bool),
            (AndroidCharacterShading.NormalMapsKey, "full", RuntimeCVarType.String),
            (AndroidCharacterShading.FragmentWorldKey, "false", RuntimeCVarType.Bool),
            (AndroidCharacterShading.GroundAoKey, "near", RuntimeCVarType.String),
            (AndroidCharacterLevers.FarSinglePassKey, "0", RuntimeCVarType.Double),
        })
        {
            RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == key);
            AssertThat(row).OverrideFailureMessage(key).IsNotNull();
            AssertThat(row!.DefaultValue).OverrideFailureMessage(key).IsEqual(value);
            AssertThat(row.Type).OverrideFailureMessage(key).IsEqual(type);
            AssertThat(AndroidSettingNames.Keys.Contains(key)).OverrideFailureMessage(key).IsTrue();
        }
    }

    /// <summary>Unset, nothing changes; each key reads as written, an unknown value as its default.</summary>
    [TestCase]
    public void TheSettingsReadAsWritten()
    {
        AssertThat(AndroidCharacterShading.Wanted(new Dictionary<string, string>())).IsEqual(AndroidCharacterShading.Defaults);
        AssertThat(AndroidCharacterShading.Defaults.Any).IsFalse();
        AndroidCharacterShading.Levers all = AndroidCharacterShading.Wanted(new Dictionary<string, string>
        {
            [AndroidCharacterShading.LiteKey] = "true",
            [AndroidCharacterShading.NormalMapsKey] = "\"fast\"",
            [AndroidCharacterShading.FragmentWorldKey] = "true",
            [AndroidCharacterShading.GroundAoKey] = "origin",
        });
        AssertThat(all).IsEqual(new AndroidCharacterShading.Levers(true, true, true, AndroidCharacterShading.GroundAo.Origin));
        AssertThat(AndroidCharacterShading.ParseGround("off") == AndroidCharacterShading.GroundAo.Off).IsTrue();
        AssertThat(AndroidCharacterShading.ParseGround("sideways") == AndroidCharacterShading.GroundAo.Near).IsTrue();
        AssertThat(AndroidCharacterShading.Wanted(new Dictionary<string, string> { [AndroidCharacterShading.NormalMapsKey] = "direct" }).FastNormalMaps).IsFalse();
        AssertThat(AndroidCharacterLevers.WantedPassMetres(new Dictionary<string, string>())).IsEqual(0f);
        AssertThat(AndroidCharacterLevers.WantedPassMetres(new Dictionary<string, string> { [AndroidCharacterLevers.FarSinglePassKey] = "15" })).IsEqual(15f);
        AssertThat(AndroidCharacterLevers.WantedPassMetres(new Dictionary<string, string> { [AndroidCharacterLevers.FarSinglePassKey] = "-2" })).IsEqual(0f);
    }

    /// <summary>Only models with skinned parts, and only with a setting on.</summary>
    [TestCase]
    public void OnlyCharacters()
    {
        var lite = new AndroidCharacterShading.Levers(true, false, false, AndroidCharacterShading.GroundAo.Near);
        AssertThat(AndroidCharacterShading.Decide(lite, 3)).IsTrue();
        AssertThat(AndroidCharacterShading.Decide(lite, 0)).IsFalse();
        AssertThat(AndroidCharacterShading.Decide(AndroidCharacterShading.Defaults, 3)).IsFalse();
        AssertThat(AndroidCharacterShading.Decide(lite with { Lite = false, Ground = AndroidCharacterShading.GroundAo.Off }, 1)).IsTrue();
    }

    /// <summary>The exact set: the marker, and the bake fold only on a modern build; the others only where the process differs.</summary>
    [TestCase]
    public void TheDefinesFollowTheSettingsAndTheProcess()
    {
        var lite = new AndroidCharacterShading.Levers(true, false, false, AndroidCharacterShading.GroundAo.Near);
        AssertThat(AndroidCharacterShading.DefinesFor(lite, true, Plain))
            .IsEqual(new[] { AndroidCharacterShading.Define, AndroidCharacterShading.BakeNoneDefine });
        // Under the fable model the probe and no bake differ: the mode stays the instance's.
        AssertThat(AndroidCharacterShading.DefinesFor(lite, false, Plain)).IsEqual(new[] { AndroidCharacterShading.Define });

        var all = new AndroidCharacterShading.Levers(true, true, true, AndroidCharacterShading.GroundAo.Origin);
        AssertThat(AndroidCharacterShading.DefinesFor(all, true, Plain)).IsEqual(new[]
        {
            AndroidCharacterShading.Define, AndroidCharacterShading.BakeNoneDefine, "FABLE_LITE_NM_DERIV",
            AndroidLeanVaryings.PropFragmentWorldDefine, AndroidCharacterShading.GroundFromOriginDefine,
        });

        // What the process states already splits no build.
        var lean = new AndroidCharacterShading.Process(NoTangentFrame: true, PropFragmentWorld: true, NearGroundAo: false, GroundAoLive: true);
        AssertThat(AndroidCharacterShading.DefinesFor(all with { Lite = false }, true, lean)).IsEmpty();
        var off = all with { Lite = false, FastNormalMaps = false, FragmentWorld = false, Ground = AndroidCharacterShading.GroundAo.Off };
        AssertThat(AndroidCharacterShading.DefinesFor(off, true, Plain)).IsEqual(new[] { AndroidGroundShading.NoPropDefine });
        AssertThat(AndroidCharacterShading.DefinesFor(off, true, Plain with { GroundAoLive = false })).IsEmpty();
    }

    /// <summary>The exact set takes the folds that split no build where the process folds none; otherwise the process's.</summary>
    [TestCase]
    public void TheExactSetFoldsTheConstantFlags()
    {
        var lite = new AndroidCharacterShading.Levers(true, false, false, AndroidCharacterShading.GroundAo.Near);
        AssertThat(AndroidCharacterShading.FoldScopeFor(Engine_ShaderSpecialisation.FoldScope.Off, lite) == Engine_ShaderSpecialisation.FoldScope.Const).IsTrue();
        AssertThat(AndroidCharacterShading.FoldScopeFor(Engine_ShaderSpecialisation.FoldScope.All, lite) == Engine_ShaderSpecialisation.FoldScope.All).IsTrue();
        AssertThat(AndroidCharacterShading.FoldScopeFor(Engine_ShaderSpecialisation.FoldScope.Off, lite with { Lite = false, FastNormalMaps = true })
            == Engine_ShaderSpecialisation.FoldScope.Off).IsTrue();
    }

    /// <summary>
    /// A character build hands on no UV2 and no fade, and drops the fade's discard, under either model; the
    /// modern one also folds the bake mode, so the amb3 fetch goes.
    /// </summary>
    [TestCase]
    public void TheExactBuildCarriesNoLightmapCoordinateOrFade()
    {
        foreach (string path in new[] { Culled, CulledModern, TwoSidedModern })
        {
            string plain = Lite(path, Trim, Pack, Near);
            string character = Lite(path, Trim, Pack, Near, AndroidCharacterShading.Define);
            AssertThat(Defines(character, "#define FABLE_LIGHTMAP_UV vec2(0.0)")).OverrideFailureMessage(path).IsTrue();
            // A solid build under the trim has no fade already; a two-sided one reads the constant.
            AssertThat(Defines(character, "#define v_tree_fade 1.0")).OverrideFailureMessage(path).IsEqual(path == TwoSidedModern);
            AssertThat(Declares(character, "v_tree_fade")).OverrideFailureMessage(path).IsFalse();
            AssertThat(Stage(character, "vertex").Contains("v_tree_fade = tree_fade;", StringComparison.Ordinal)).OverrideFailureMessage(path).IsFalse();
            AssertThat(Defines(plain, "#define FABLE_LIGHTMAP_UV vec2(0.0)")).OverrideFailureMessage(path).IsFalse();
        }

        // Without the trim too (the varyings' own switch), the coordinate is still the constant.
        AssertThat(Defines(Lite(CulledModern, AndroidCharacterShading.Define), "#define FABLE_LIGHTMAP_UV vec2(0.0)")).IsTrue();
        // The two-sided plain build keeps its fade.
        AssertThat(Defines(Lite(TwoSidedModern, Trim, Pack, Near), "#define v_tree_fade COLOR.g")).IsTrue();
        // The modern fold: no bake mode but none, so no amb3 lookup.
        string folded = Lite(CulledModern, Trim, Pack, Near, AndroidCharacterShading.Define, AndroidCharacterShading.BakeNoneDefine);
        AssertThat(Defines(folded, "#define FABLE_MODE 0.0")).IsTrue();
    }

    /// <summary>The near ground AO's per-vertex gate: the floor map's four taps unless a character build says the origin.</summary>
    [TestCase]
    public void TheGroundGateComesFromTheOriginOnlyWhenAsked()
    {
        const string Taps = "v_prop_ground_height = fable_vertex_prop_ground_ao(COLOR.a, world_at).y;";
        const string Origin = "v_prop_ground_height = max(world_at.y - MODEL_MATRIX[3].y, 0.0);";
        string plain = Stage(Lite(CulledModern, Trim, Pack, Near), "vertex");
        AssertThat(plain.Contains(Taps, StringComparison.Ordinal)).IsTrue();
        AssertThat(plain.Contains(Origin, StringComparison.Ordinal)).IsFalse();
        string origin = Stage(Lite(CulledModern, Trim, Pack, Near, AndroidCharacterShading.Define, AndroidCharacterShading.GroundFromOriginDefine), "vertex");
        AssertThat(origin.Contains(Origin, StringComparison.Ordinal)).IsTrue();
        AssertThat(origin.Contains(Taps, StringComparison.Ordinal)).IsFalse();
        AssertThat(origin.Contains("texelFetch(fable_terrain_contact_ao", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>The hooks: the facts bit, the specialisation gate and the material cache's split.</summary>
    [TestCase]
    public void TheHooks()
    {
        string lite = Source("res://src/android/Engine_MdlMaterial.Lite.cs");
        AssertThat(lite).Contains("AndroidCharacterShading.For(info)");
        AssertThat(lite).Contains("AndroidCharacterShading.Defines(path)");
        AssertThat(Source("res://src/android/Engine_ShaderSpecialisation.cs")).Contains("AndroidCharacterShading.On.Any");
        AssertThat(Source("res://src/android/AndroidTranslucentPass.cs")).Contains("AndroidCharacterShading.On.Any");
    }

    /// <summary>Off a phone no character build is made and the shared material keys are as they were.</summary>
    [TestCase]
    public void TheDesktopKeepsTheWorldsBuilds()
    {
        if (Refii.Core.Engine_Platform.IsAndroid)
        {
            return;
        }

        AssertThat(AndroidCharacterShading.On).IsEqual(AndroidCharacterShading.Defaults);
        AssertThat(AndroidCharacterShading.For(new MdlInfo { SkinnedCount = 2 })).IsFalse();
        AssertThat(AndroidTranslucentPass.KeyPart(new MdlInfo { SkinnedCount = 2 })).IsEqual(string.Empty);
        AssertThat(AndroidCharacterLevers.FarSinglePassMetres).IsEqual(0f);
    }

    /// <summary>The single pass is the title's cutout reference, and only a translucent surface's pair takes it.</summary>
    [TestCase]
    public void TheFarSinglePassIsTheTranslucentPairsOpaquePass()
    {
        AssertThat(AndroidCharacterLevers.CutoutAlpha).IsEqual(128.5f / 255.0f);
        AssertThat(AndroidCharacterLevers.IsTranslucentPair(Engine_ShaderFamily.StandardPath, Engine_ShaderFamily.StandardTranslucentPath)).IsTrue();
        AssertThat(AndroidCharacterLevers.IsTranslucentPair(Engine_ShaderFamily.StandardCulledPath, Engine_ShaderFamily.StandardTranslucentPath)).IsFalse();
        AssertThat(AndroidCharacterLevers.IsTranslucentPair(Engine_ShaderFamily.StandardPath, Engine_ShaderFamily.StandardPath)).IsFalse();
        // Engine_MdlMaterial's own constant, which the single-pass copy repeats.
        AssertThat(Source("res://src/Engine/material/Engine_MdlMaterial.cs")).Contains("private const float CutoutAlpha = 128.5f / 255.0f;");
    }
}
