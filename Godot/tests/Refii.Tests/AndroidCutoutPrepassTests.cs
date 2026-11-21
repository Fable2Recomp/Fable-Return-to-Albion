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
/// `android_cutout_depth_prepass` (2026-11-17): the cut-outs drawn depth first by godot-patches/0017, marked by
/// `depth_prepass_alpha` in their lite builds. Off by default and in no preset.
/// </summary>
[TestSuite]
public sealed class AndroidCutoutPrepassTests
{
    private const string Prop = "res://src/Engine/material/lite/standard_material_lite.gdshader";
    private const string Tree = "res://src/Engine/material/lite/standard_material_tree_lite.gdshader";
    private const string Wind = "res://src/Engine/material/lite/standard_material_wind_lite.gdshader";
    private const string Culled = "res://src/Engine/material/lite/standard_material_culled_lite.gdshader";
    private const string Translucent = "res://src/Engine/material/lite/standard_material_translucent_lite.gdshader";

    private const string PropWrapper =
        "shader_type spatial;\nrender_mode blend_mix, depth_draw_opaque, cull_disabled;\n" +
        "#define FABLE_LITE\n#include \"res://src/Engine/material/standard_material_body.gdshaderinc\"\n";

    private static string Repo(string relative, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../..", relative));

    private static readonly string[] Presets = { GraphicsPresets.Low, GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra };

    /// <summary>A display row, off by default, named for a phone with the restart line, on in Medium only (revision 27);
    /// the EQUAL switch a developer row, on (EQUAL is the default compare), in no preset.</summary>
    [TestCase]
    public void KeysAreRestartOnlyAndOnInMediumOnly()
    {
        RuntimeCVarDefinition? row = RuntimeCVarsAndroid.EffectsAndCanvas.SingleOrDefault(d => d.Name == AndroidCutoutPrepass.Key);
        AssertThat(row).IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("false");
        AssertThat(row.Category).IsEqual(RuntimeCVarsGodot.DisplayCategory);
        AssertThat(AndroidRestartNotes.DescriptionNeedsRestart(row.Description)).IsTrue();
        AssertThat(AndroidRestartNotes.NeedsRestart(AndroidCutoutPrepass.Key)).IsTrue();

        RuntimeCVarDefinition? equal = RuntimeCVarsAndroid.EffectsAndCanvas.SingleOrDefault(d => d.Name == AndroidCutoutPrepass.EqualKey);
        AssertThat(equal).IsNotNull();
        AssertThat(equal!.DefaultValue).IsEqual("true");
        AssertThat(equal.Category).IsEqual(RuntimeCVarsGodot.DeveloperCategory);
        AssertThat(AndroidRestartNotes.DescriptionNeedsRestart(equal.Description)).IsTrue();

        AssertThat(RuntimeCVarsAndroid.AllKeys.Contains(AndroidCutoutPrepass.Key)).IsTrue();
        AssertThat(GraphicsPresets.MediumValues[AndroidCutoutPrepass.Key]).IsEqual("true");
        foreach (var values in new[] { GraphicsPresets.LowValues, GraphicsPresets.HighValues, GraphicsPresets.UltraValues })
        {
            AssertThat(values[AndroidCutoutPrepass.Key]).IsEqual("false");
        }

        AssertThat(RuntimeCVarsAndroid.AllKeys.Contains(AndroidCutoutPrepass.EqualKey)).IsTrue();
        foreach (string preset in Presets)
        {
            AssertThat(GraphicsPresets.Values(preset)!.ContainsKey(AndroidCutoutPrepass.EqualKey)).OverrideFailureMessage(preset).IsFalse();
        }

        bool offered = GraphicsPresets.Offered;
        try
        {
            GraphicsPresets.Offered = true;
            AssertThat(AndroidSettingNames.Name(AndroidCutoutPrepass.Key)).IsNotNull();
            AssertThat(AndroidSettingNames.About(AndroidCutoutPrepass.Key)).EndsWith(AndroidRestartNotes.SettingNote);
        }
        finally
        {
            GraphicsPresets.Offered = offered;
        }

        AssertThat(AndroidCutoutPrepass.Wanted(new Dictionary<string, string>())).IsFalse();
        AssertThat(AndroidCutoutPrepass.Wanted(new Dictionary<string, string> { [AndroidCutoutPrepass.Key] = "true" })).IsTrue();
    }

    /// <summary>The engine value: 0 off (whatever the EQUAL switch says), 1 GREATER_OR_EQUAL, 2 EQUAL.</summary>
    [TestCase]
    public void TheEngineValueFollowsBothKeys()
    {
        AssertThat(AndroidCutoutPrepass.EngineValue(false, false)).IsEqual(0);
        AssertThat(AndroidCutoutPrepass.EngineValue(false, true)).IsEqual(0);
        AssertThat(AndroidCutoutPrepass.EngineValue(true, false)).IsEqual(1);
        AssertThat(AndroidCutoutPrepass.EngineValue(true, true)).IsEqual(2);
        AssertThat(AndroidCutoutPrepass.EngineSetting).StartsWith("rendering/renderer/");
        AssertThat(AndroidCutoutPrepass.EngineSetting).EndsWith("_mobile");
    }

    /// <summary>Cut-out and grass passes on the builds that discard; never solid builds, the blended build or a character's, nor when off.</summary>
    [TestCase]
    public void OnlyCutoutPassesOnDiscardingBuildsAreMarked()
    {
        foreach (string build in new[] { Prop, Tree, Wind })
        {
            AssertThat(AndroidCutoutPrepass.Marks(build, cutout: true, grass: false, blended: false, character: false, on: true)).OverrideFailureMessage(build).IsTrue();
            AssertThat(AndroidCutoutPrepass.Marks(build, cutout: false, grass: true, blended: false, character: false, on: true)).OverrideFailureMessage(build).IsTrue();
            // A trunk or a solid two-sided prop: no holes to gain from.
            AssertThat(AndroidCutoutPrepass.Marks(build, cutout: false, grass: false, blended: false, character: false, on: true)).OverrideFailureMessage(build).IsFalse();
            AssertThat(AndroidCutoutPrepass.Marks(build, cutout: true, grass: false, blended: false, character: true, on: true)).OverrideFailureMessage(build).IsFalse();
            AssertThat(AndroidCutoutPrepass.Marks(build, cutout: true, grass: false, blended: false, character: false, on: false)).OverrideFailureMessage(build).IsFalse();
        }

        AssertThat(AndroidCutoutPrepass.Marks(Culled, cutout: true, grass: false, blended: false, character: false, on: true)).IsFalse();
        // A translucent material's blended second pass: its own build, no discard.
        AssertThat(AndroidCutoutPrepass.Marks(Translucent, cutout: true, grass: false, blended: true, character: false, on: true)).IsFalse();
    }

    /// <summary>The census rows: the build a marked pass is on, a translucent material's opaque pass, and why one is not marked.</summary>
    [TestCase]
    public void TheCensusNamesEachRow()
    {
        AssertThat(AndroidCutoutPrepass.Row(Prop, cutout: true, grass: false, blended: false, character: false)).IsEqual(AndroidCutoutPrepass.Census.TwoSided);
        AssertThat(AndroidCutoutPrepass.Row(Tree, cutout: true, grass: false, blended: false, character: false)).IsEqual(AndroidCutoutPrepass.Census.Tree);
        AssertThat(AndroidCutoutPrepass.Row(Wind, cutout: false, grass: true, blended: false, character: false)).IsEqual(AndroidCutoutPrepass.Census.Wind);
        AssertThat(AndroidCutoutPrepass.Row(Prop, cutout: true, grass: false, blended: true, character: false)).IsEqual(AndroidCutoutPrepass.Census.TranslucentOpaque);
        AssertThat(AndroidCutoutPrepass.Row(Prop, cutout: true, grass: false, blended: false, character: true)).IsEqual(AndroidCutoutPrepass.Census.Character);
        AssertThat(AndroidCutoutPrepass.Row(Translucent, cutout: true, grass: false, blended: true, character: false)).IsEqual(AndroidCutoutPrepass.Census.OtherBuild);
        AssertThat(AndroidCutoutPrepass.Row(Culled, cutout: true, grass: false, blended: false, character: false)).IsEqual(AndroidCutoutPrepass.Census.OtherBuild);
        AssertThat(AndroidCutoutPrepass.Row(Prop, cutout: false, grass: false, blended: false, character: false)).IsNull();
    }

    /// <summary>A pass's facts reach the marker (Engine_MdlMaterial.SpecialisationDefines), beside the flat-lit hedges' own.</summary>
    [TestCase]
    public void APassesFactsReachTheMarker()
    {
        AssertThat(Defines(Engine_MdlMaterial.FactAlphaCut, prepass: true)).Contains(AndroidCutoutPrepass.Define);
        AssertThat(Defines(Engine_MdlMaterial.FactFoliage, prepass: true)).Contains(AndroidCutoutPrepass.Define);
        AssertThat(Defines(Engine_MdlMaterial.FactGrass, prepass: true)).Contains(AndroidCutoutPrepass.Define);
        AssertThat(Defines(0, prepass: true)).NotContains(AndroidCutoutPrepass.Define);
        // A translucent material's opaque alpha-tested pass carries the blended fact too and is marked on its two-sided build.
        AssertThat(Defines(Engine_MdlMaterial.FactAlphaCut | Engine_MdlMaterial.FactBlended, prepass: true)).Contains(AndroidCutoutPrepass.Define);
        AssertThat(Defines(Engine_MdlMaterial.FactAlphaCut | Engine_MdlMaterial.FactSkinned, prepass: true)).NotContains(AndroidCutoutPrepass.Define);
        AssertThat(Defines(Engine_MdlMaterial.FactAlphaCut | Engine_MdlMaterial.FactCharacter, prepass: true)).NotContains(AndroidCutoutPrepass.Define);
        AssertThat(Defines(Engine_MdlMaterial.FactAlphaCut)).NotContains(AndroidCutoutPrepass.Define);

        // Medium's flat-lit hedges keep their own marker and take this one too.
        string[] both = Defines(Engine_MdlMaterial.FactAlphaCut, prepass: true, flat: true);
        AssertThat(both).Contains(AndroidCutoutPrepass.Define);
        AssertThat(both).Contains(AndroidFlatCutouts.Define);
    }

    /// <summary>The marker adds `depth_prepass_alpha` to the build's render mode and its define before the body; nothing else changes.</summary>
    [TestCase]
    public void AMarkedBuildDeclaresDepthPrepassAlpha()
    {
        string marked = Engine_ShaderSpecialisation.Compose(PropWrapper, new[] { AndroidCutoutPrepass.Define }, false);
        AssertThat(marked).Contains("render_mode blend_mix, depth_draw_opaque, cull_disabled, depth_prepass_alpha;");
        AssertThat(marked.IndexOf("#define " + AndroidCutoutPrepass.Define + "\n", System.StringComparison.Ordinal))
            .IsLess(marked.IndexOf("#include", System.StringComparison.Ordinal));

        // With the flat-lit hedges' marker as well: both render modes, each once.
        string flat = Engine_ShaderSpecialisation.Compose(PropWrapper, new[] { AndroidCutoutPrepass.Define, AndroidFlatCutouts.Define }, false);
        AssertThat(Regex.Matches(flat, "depth_prepass_alpha").Count).IsEqual(1);
        AssertThat(Regex.Matches(flat, @"\bunshaded\b").Count).IsEqual(1);

        AssertThat(AndroidCutoutPrepass.Compose(PropWrapper, new[] { "OTHER" })).IsEqual(PropWrapper);
        AssertThat(Engine_ShaderSpecialisation.Compose(PropWrapper, new[] { "OTHER" }, false)).NotContains("depth_prepass_alpha");
    }

    /// <summary>
    /// The pre-pass draws a marked build's SHADOW version, where Mobile defines IN_SHADOW_PASS true. The cut-out's
    /// coverage (alpha scissor, fade dither) must not read it, or the depth draw and the colour draw would discard
    /// different pixels. Nothing the lite props' body includes reads it.
    /// </summary>
    [TestCase]
    public void TheCutoutBodyNeverReadsInShadowPass()
    {
        var sources = new List<string>
        {
            Repo("Godot/src/Engine/material/standard_material_body.gdshaderinc"),
            Repo("Godot/src/Engine/material/fable_dither.gdshaderinc"),
            Repo("Godot/src/Engine/material/fable_tree_sway.gdshaderinc"),
        };
        sources.AddRange(Directory.GetFiles(Repo("Godot/src/android"), "standard_lite_*.gdshaderinc"));
        sources.AddRange(Directory.GetFiles(Repo("Godot/src/android"), "fable_lite_*.gdshaderinc"));
        foreach (string file in sources)
        {
            AssertThat(File.Exists(file)).OverrideFailureMessage(file).IsTrue();
            foreach (string line in File.ReadAllLines(file))
            {
                string code = line.Split("//")[0];
                AssertThat(code.Contains("IN_SHADOW_PASS", System.StringComparison.Ordinal)).OverrideFailureMessage(file + ": " + line).IsFalse();
                AssertThat(code.Contains("Z_CLIP_SCALE", System.StringComparison.Ordinal)).OverrideFailureMessage(file + ": " + line).IsFalse();
            }
        }
    }

    /// <summary>The engine patch reads the setting the game writes, off by default, and takes `depth_prepass_alpha` with a discard as the request.</summary>
    [TestCase]
    public void TheEnginePatchMatchesTheGame()
    {
        string path = Repo("tools/android/godot-patches/0017-mobile-cutout-depth-prepass.diff");
        AssertThat(File.Exists(path)).IsTrue();
        string patch = File.ReadAllText(path);
        AssertThat(patch).Contains("\"" + AndroidCutoutPrepass.EngineSetting + "\"");
        AssertThat(patch).Contains("PROPERTY_HINT_ENUM, \"Off,Greater or Equal,Equal\"), 0);");
        AssertThat(patch).Contains("sd->uses_depth_prepass_alpha && sd->uses_discard && !sd->uses_alpha");
        AssertThat(patch).Contains("enable_depth_write = false;");
        AssertThat(patch).Contains("SHADER_VERSION_SHADOW_PASS");
        // The depth draws reach the viewport's draw calls (the game's VisibleCalls) and a log line.
        AssertThat(patch).Contains("VIEWPORT_RENDER_INFO_DRAW_CALLS_IN_FRAME] += depth_draws;");
        AssertThat(patch).Contains("[cutout-prepass]");
    }

    /// <summary>`debug_cutout_prepass_scope`: a developer row at "two_sided" (the default scope), in no preset; each scope marks only its builds and the census names what it left out.</summary>
    [TestCase]
    public void TheScopeLimitsTheMarking()
    {
        RuntimeCVarDefinition? row = RuntimeCVarsAndroid.EffectsAndCanvas.SingleOrDefault(d => d.Name == AndroidCutoutPrepass.ScopeKey);
        AssertThat(row).IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("two_sided");
        AssertThat(row.Category).IsEqual(RuntimeCVarsGodot.DeveloperCategory);
        AssertThat(AndroidRestartNotes.DescriptionNeedsRestart(row.Description)).IsTrue();
        foreach (string preset in Presets)
        {
            AssertThat(GraphicsPresets.Values(preset)!.ContainsKey(AndroidCutoutPrepass.ScopeKey)).IsFalse();
        }

        // Unset or naming no build: the two-sided builds, with translucent materials' opaque passes, as measured.
        AssertThat(AndroidCutoutPrepass.DefaultScope).IsEqual(AndroidCutoutPrepass.Scope.TwoSided | AndroidCutoutPrepass.Scope.Translucent);
        AssertThat(AndroidCutoutPrepass.ParseScope(null)).IsEqual(AndroidCutoutPrepass.DefaultScope);
        AssertThat(AndroidCutoutPrepass.ParseScope("nonsense")).IsEqual(AndroidCutoutPrepass.DefaultScope);
        AssertThat(AndroidCutoutPrepass.ParseScope(row.DefaultValue)).IsEqual(AndroidCutoutPrepass.DefaultScope);
        AssertThat(AndroidCutoutPrepass.ParseScope("all")).IsEqual(AndroidCutoutPrepass.Scope.All);
        AssertThat(AndroidCutoutPrepass.ParseScope("\"Trees\"")).IsEqual(AndroidCutoutPrepass.Scope.Trees | AndroidCutoutPrepass.Scope.Translucent);
        AssertThat(AndroidCutoutPrepass.ParseScope("two_sided, wind")).IsEqual(AndroidCutoutPrepass.Scope.TwoSided | AndroidCutoutPrepass.Scope.Wind | AndroidCutoutPrepass.Scope.Translucent);
        AssertThat(AndroidCutoutPrepass.ParseScope("all,no_translucent")).IsEqual(AndroidCutoutPrepass.Scope.TwoSided | AndroidCutoutPrepass.Scope.Wind | AndroidCutoutPrepass.Scope.Trees);
        AssertThat(AndroidCutoutPrepass.ParseScope("no_translucent")).IsEqual(AndroidCutoutPrepass.Scope.TwoSided);
        foreach (string text in new[] { "all", "two_sided", "trees", "two_sided,wind", "all,no_translucent", "trees,no_translucent" })
        {
            AssertThat(AndroidCutoutPrepass.ScopeText(AndroidCutoutPrepass.ParseScope(text))).IsEqual(text);
        }

        AndroidCutoutPrepass.Scope twoSided = AndroidCutoutPrepass.ParseScope("two_sided");
        AndroidCutoutPrepass.Scope trees = AndroidCutoutPrepass.ParseScope("trees");
        AssertThat(AndroidCutoutPrepass.Marks(Prop, cutout: true, grass: false, blended: false, character: false, on: true, scope: twoSided)).IsTrue();
        AssertThat(AndroidCutoutPrepass.Marks(Tree, cutout: true, grass: false, blended: false, character: false, on: true, scope: twoSided)).IsFalse();
        AssertThat(AndroidCutoutPrepass.Marks(Wind, cutout: false, grass: true, blended: false, character: false, on: true, scope: twoSided)).IsFalse();
        AssertThat(AndroidCutoutPrepass.Marks(Tree, cutout: true, grass: false, blended: false, character: false, on: true, scope: trees)).IsTrue();
        AssertThat(AndroidCutoutPrepass.Marks(Prop, cutout: true, grass: false, blended: false, character: false, on: true, scope: trees)).IsFalse();
        // A translucent tree leaf's opaque pass follows the trees, unless no_translucent.
        AssertThat(AndroidCutoutPrepass.Marks(Tree, cutout: true, grass: false, blended: true, character: false, on: true, scope: trees)).IsTrue();
        AssertThat(AndroidCutoutPrepass.Marks(Tree, cutout: true, grass: false, blended: true, character: false, on: true,
            scope: AndroidCutoutPrepass.ParseScope("trees,no_translucent"))).IsFalse();
        AssertThat(AndroidCutoutPrepass.Row(Tree, cutout: true, grass: false, blended: false, character: false, scope: twoSided))
            .IsEqual(AndroidCutoutPrepass.Census.OutOfScope);

        // The facts path takes the scope too.
        AssertThat(Engine_MdlMaterial.SpecialisationDefines(Engine_MdlMaterial.FactAlphaCut, Tree, false, false, true, twoSided).ToArray())
            .NotContains(AndroidCutoutPrepass.Define);
        AssertThat(Engine_MdlMaterial.SpecialisationDefines(Engine_MdlMaterial.FactAlphaCut, Tree, false, false, true, trees).ToArray())
            .Contains(AndroidCutoutPrepass.Define);

        string line = AndroidCutoutPrepass.CensusLine(new[] { 1, 2, 3, 4, 5, 6, 7 }, twoSided);
        AssertThat(line).Contains("scope two_sided:");
        AssertThat(line).Contains("10 of 28");
        AssertThat(line).Contains("out of scope 5");
    }

    private static string[] Defines(int facts, bool prepass = false, bool flat = false) =>
        Engine_MdlMaterial.SpecialisationDefines(facts, Prop, vertexLitOn: false, flatCutoutsOn: flat, cutoutPrepassOn: prepass).ToArray();

    /// <summary>Shared material keys stay split between characters and world props whenever cutout pre-pass or flat swaying plants are active.</summary>
    [TestCase]
    public void TranslucentPassKeySplitsCharactersFromWorldForCutoutPrepassAndFlatSwaying()
    {
        AssertThat(AndroidTranslucentPass.KeyPart(true, 1)).IsNotEqual(AndroidTranslucentPass.KeyPart(true, 0));
        string source = System.IO.File.ReadAllText(Repo("Godot/src/android/AndroidTranslucentPass.cs"));
        AssertThat(source).Contains("AndroidFlatCutouts.SwayingOn");
        AssertThat(source).Contains("AndroidCutoutPrepass.On");
    }
}
