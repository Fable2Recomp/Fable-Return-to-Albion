using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using Refii.Level;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The same-picture pixel and pass trims (2026-11-16): `lite_fog_trims`, `lite_fable_ambient_off`,
/// `android_water_zenith_once`, `sky_dome_globals` and `foliage_own_cells`, and the picture-changing
/// `lite_flat_swaying_plants`, each off by default and in no preset, but `lite_fog_trims`, which every preset
/// takes since revision 22, and `lite_fable_ambient_off`, since 24.
/// </summary>
[TestSuite]
public sealed class AndroidFogWaterSkyTests
{
    private const string PropLite = "res://src/Engine/material/lite/standard_material_lite.gdshader";
    private const string PropModern = "res://src/Engine/material/lite/modern/standard_material_lite.gdshader";
    private const string WaterPath = "res://src/Engine/level/water/water.gdshader";

    private static readonly string[] Keys =
    {
        AndroidFogTrims.Key, AndroidFableAmbientOff.Key, AndroidWaterZenith.Key, AndroidSkyGlobals.Key,
        Engine_LevelProps.FoliageOwnCellsKey, AndroidFlatCutouts.SwayingKey,
    };

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

    private static string Pre(string code, string path) => Engine_ShaderCeilingSource.Preprocess(code, path, Include);

    /// <summary>A lite prop build with lean fog and the given extra defines, preprocessed.</summary>
    private static string Lite(string litePath, params string[] extra) =>
        Pre(Engine_ShaderVariant.WithExtraDefines(
            Engine_ShaderVariant.Compose(File(Engine_ShaderVariant.FullPathOf(litePath)), litePath, true, true, true),
            new[] { AndroidLeanShading.FogLeanDefine }.Concat(extra)), litePath);

    [TestCase]
    public void EachKeyIsOffWithAPlayersNameAndInNoPreset()
    {
        foreach (string key in Keys)
        {
            RuntimeCVarDefinition? row = RuntimeCVarsGodot.Definitions.SingleOrDefault(d => d.Name == key);
            AssertThat(row).OverrideFailureMessage(key + " not registered").IsNotNull();
            AssertThat(row!.DefaultValue).IsEqual("false");
            AssertThat(AndroidSettingNames.Keys.Contains(key)).OverrideFailureMessage(key + " has no name").IsTrue();
            foreach (string preset in new[] { GraphicsPresets.Low, GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra })
            {
                // The fog trims are in every preset (revision 22), the fable model's ambient left out too (24); the
                // rest in none yet.
                if (key == AndroidFogTrims.Key || key == AndroidFableAmbientOff.Key)
                {
                    AssertThat(GraphicsPresets.Values(preset)![key]).OverrideFailureMessage(key + " in " + preset).IsEqual("true");
                }
                else
                {
                    AssertThat(GraphicsPresets.Values(preset)!.ContainsKey(key)).OverrideFailureMessage(key + " in " + preset).IsFalse();
                }
            }
        }

        // The start-only ones say so in the agreed words; the undergrowth one applies at a loading screen.
        bool offered = GraphicsPresets.Offered;
        try
        {
            GraphicsPresets.Offered = true;
            foreach (string key in Keys.Where(k => k != Engine_LevelProps.FoliageOwnCellsKey))
            {
                AssertThat(AndroidSettingNames.About(key)).EndsWith("Applies the next time you start the game.");
            }

            AssertThat(AndroidSettingNames.About(Engine_LevelProps.FoliageOwnCellsKey)).EndsWith("Applies at the next loading screen.");
        }
        finally
        {
            GraphicsPresets.Offered = offered;
        }
    }

    [TestCase]
    public void FogTrimDefinesFollowWhatIsFixed()
    {
        AssertThat(AndroidFogTrims.DefinesFor(false, false, true, false, 1f)).IsEmpty();
        // Fable model, outdoor scale 1: all three.
        AssertThat(AndroidFogTrims.DefinesFor(true, false, true, false, 1f)).IsEqual(new[]
            { AndroidFogTrims.SunViewDefine, AndroidFogTrims.NoVolumetricCancelDefine, AndroidFogTrims.UnitScaleDefine });
        // Another scale keeps the scale read.
        AssertThat(AndroidFogTrims.DefinesFor(true, false, true, false, 0.5f)).IsEqual(new[]
            { AndroidFogTrims.SunViewDefine, AndroidFogTrims.NoVolumetricCancelDefine });
        // Modern model: never the scale (interiors scale their fog); the cancel only while near fog is kept on Mobile.
        AssertThat(AndroidFogTrims.DefinesFor(true, true, true, false, 1f)).IsEqual(new[]
            { AndroidFogTrims.SunViewDefine, AndroidFogTrims.NoVolumetricCancelDefine });
        AssertThat(AndroidFogTrims.DefinesFor(true, true, false, false, 1f)).IsEqual(new[] { AndroidFogTrims.SunViewDefine });
        AssertThat(AndroidFogTrims.DefinesFor(true, true, true, true, 1f)).IsEqual(new[] { AndroidFogTrims.SunViewDefine });
    }

    [TestCase]
    public void FogTrimsRemoveTheirTermsAndNothingElse()
    {
        string plain = Lite(PropLite);
        AssertThat(plain).Contains("vec3 world_ray = mat3(inv_view) * view_vertex;");
        AssertThat(plain).Contains("fable_volumetric_fog_end * vol_dist");
        AssertThat(plain).Contains("float fog_scale = fable_interior_fog;");

        string trimmed = Lite(PropLite, AndroidFogTrims.SunViewDefine, AndroidFogTrims.NoVolumetricCancelDefine, AndroidFogTrims.UnitScaleDefine);
        AssertThat(trimmed).NotContains("world_ray");
        AssertThat(trimmed).Contains("* mat3(inv_view);");
        AssertThat(trimmed).NotContains("fable_volumetric_fog_end * vol_dist");
        AssertThat(trimmed).NotContains("float fog_scale = fable_interior_fog;");
        AssertThat(trimmed).Contains("const float fog_scale = 1.0;");

        // Without lean fog the include is not compiled, so the trims change nothing.
        string full = File(Engine_ShaderVariant.FullPathOf(PropLite));
        string fullBuild = Engine_ShaderVariant.Compose(full, PropLite, true, true, true);
        static string Code(string text) => string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith("#define FABLE_LITE_FOG_", StringComparison.Ordinal)));
        AssertThat(Code(Pre(Engine_ShaderVariant.WithExtraDefines(fullBuild, new[] { AndroidFogTrims.SunViewDefine, AndroidFogTrims.UnitScaleDefine }), PropLite)))
            .IsEqual(Code(Pre(fullBuild, PropLite)));
    }

    [TestCase]
    public void AmbientWordOnlyOnFableBuildsUnderTheFableModel()
    {
        AssertThat(AndroidFableAmbientOff.Applies(true, false, false)).IsTrue();
        AssertThat(AndroidFableAmbientOff.Applies(false, false, false)).IsFalse();
        AssertThat(AndroidFableAmbientOff.Applies(true, true, false)).IsFalse();
        AssertThat(AndroidFableAmbientOff.Applies(true, false, true)).IsFalse();

        string fable = Engine_ShaderVariant.Compose(File(Engine_ShaderVariant.FullPathOf(PropLite)), PropLite, true, true, true);
        string worded = AndroidFableAmbientOff.Compose(fable, true, false, Engine_ShaderVariant.FoldsAmbient(PropLite));
        AssertThat(worded.Replace(", ambient_light_disabled;", ";", StringComparison.Ordinal)).IsEqual(fable);
        AssertThat(worded).Contains(", ambient_light_disabled;");

        string modern = Engine_ShaderVariant.Compose(File(Engine_ShaderVariant.FullPathOf(PropModern)), PropModern, true, true, true);
        AssertThat(AndroidFableAmbientOff.Compose(modern, true, false, Engine_ShaderVariant.FoldsAmbient(PropModern))).IsEqual(modern);
    }

    [TestCase]
    public void AmbientWordOnlyOnAPhoneUnderTheFableModelInFable2()
    {
        AssertThat(AndroidFableAmbientOff.ChosenFor(true, true, false, true)).IsTrue();
        AssertThat(AndroidFableAmbientOff.ChosenFor(false, true, false, true)).IsFalse();
        AssertThat(AndroidFableAmbientOff.ChosenFor(true, false, false, true)).IsFalse();
        AssertThat(AndroidFableAmbientOff.ChosenFor(true, true, true, true)).IsFalse();
        AssertThat(AndroidFableAmbientOff.ChosenFor(true, true, false, false)).IsFalse();
    }

    [TestCase]
    public void AmbientWatchKnowsWhichEnvironmentsAddAmbient()
    {
        var grey = new Godot.Color(0.3f, 0.3f, 0.3f);
        var black = new Godot.Color(0f, 0f, 0f, 0f);
        AssertThat(AndroidFableAmbientOff.AddsAmbient(Godot.Environment.AmbientSource.Disabled, grey, 1f, grey)).IsFalse();
        AssertThat(AndroidFableAmbientOff.AddsAmbient(Godot.Environment.AmbientSource.Color, grey, 1f, black)).IsTrue();
        AssertThat(AndroidFableAmbientOff.AddsAmbient(Godot.Environment.AmbientSource.Color, grey, 0f, black)).IsFalse();
        AssertThat(AndroidFableAmbientOff.AddsAmbient(Godot.Environment.AmbientSource.Color, black, 1f, black)).IsFalse();
        AssertThat(AndroidFableAmbientOff.AddsAmbient(Godot.Environment.AmbientSource.Sky, black, 1f, black)).IsTrue();
        AssertThat(AndroidFableAmbientOff.AddsAmbient(Godot.Environment.AmbientSource.Bg, grey, 1f, black)).IsFalse();
        AssertThat(AndroidFableAmbientOff.AddsAmbient(Godot.Environment.AmbientSource.Bg, black, 1f, grey)).IsTrue();
    }

    [TestCase]
    public void FigureStageDrawsNoLiteBuild()
    {
        // The stage's 0.3 grey ambient reaches only its own StandardMaterial3D overrides, set on every surface.
        string stage = File("res://src/Engine/ui/Engine_UiStageMaterial.cs");
        AssertThat(stage).Contains("var material = new StandardMaterial3D");
        AssertThat(stage).Contains("target.SetSurfaceOverrideMaterial(surface, material);");
        AssertThat(stage).Contains("for (int surface = 0; surface < mesh.GetSurfaceCount(); ++surface)");
    }

    [TestCase]
    public void WaterZenithMovesToTheVertexOnlyWithTheDefine()
    {
        string file = File(WaterPath);
        string plain = Pre(file, WaterPath);
        AssertThat(plain).Contains("dome_reflect(vec3(0.0, 1.0, 0.0))");
        AssertThat(plain).NotContains("v_zenith");

        string composed = AndroidWaterZenith.Compose(file);
        AssertThat(composed.Replace("#define " + AndroidWaterZenith.Define + "\n", string.Empty, StringComparison.Ordinal)).IsEqual(file);
        string lean = Pre(composed, WaterPath);
        AssertThat(lean).NotContains("dome_reflect(vec3(0.0, 1.0, 0.0))");
        AssertThat(lean).Contains("varying flat vec3 v_zenith;");
        AssertThat(lean).Contains("vec3 zen = v_zenith;");
    }

    [TestCase]
    public void SkyReadsGlobalsOnlyWithTheDefine()
    {
        string file = File(Engine_SkyEnvironment.ShaderPath);
        string plain = Pre(file, Engine_SkyEnvironment.ShaderPath);
        AssertThat(plain).Contains("uniform vec3 sun_engine");
        AssertThat(plain).NotContains(AndroidSkyGlobals.BetaRayleighGlobal);

        string globals = Pre(AndroidSkyGlobals.Compose(file), Engine_SkyEnvironment.ShaderPath);
        AssertThat(globals).NotContains("uniform vec3 sun_engine");
        AssertThat(globals).NotContains("uniform vec3 beta_rayleigh");
        AssertThat(globals).Contains("global uniform vec3 " + AndroidSkyGlobals.BetaRayleighGlobal + ";");
        AssertThat(globals).Contains("global uniform vec3 " + AndroidSkyGlobals.BetaMieGlobal + ";");
        AssertThat(globals).Contains("global uniform vec3 atmos_sun;");
        AssertThat(globals).Contains("uniform float horizon_distance");

        AssertThat(AndroidSkyGlobals.KeepsRefreshing(radianceKnownUnread: true)).IsFalse();
        AssertThat(AndroidSkyGlobals.KeepsRefreshing(radianceKnownUnread: false)).IsTrue();
    }

    [TestCase]
    public void UndergrowthKeepsItsCellsOnlyWhenAsked()
    {
        AssertThat(Engine_LevelProps.KeepsOwnCells(on: true, foliage: true)).IsTrue();
        AssertThat(Engine_LevelProps.KeepsOwnCells(on: false, foliage: true)).IsFalse();
        AssertThat(Engine_LevelProps.KeepsOwnCells(on: true, foliage: false)).IsFalse();
    }

    [TestCase]
    public void SwayingPlantsGoFlatOnlyOnTheWindBuilds()
    {
        const string wind = "res://src/Engine/material/lite/standard_material_wind_lite.gdshader";
        const string windModern = "res://src/Engine/material/lite/modern/standard_material_wind_lite_nospec.gdshader";
        AssertThat(AndroidFlatCutouts.IsWindBuild(wind)).IsTrue();
        AssertThat(AndroidFlatCutouts.IsWindBuild(windModern)).IsTrue();
        AssertThat(AndroidFlatCutouts.IsWindBuild(PropLite)).IsFalse();
        AssertThat(AndroidFlatCutouts.MarksSwaying(windModern, cutout: true, blended: false, grass: false, character: false, on: true)).IsTrue();
        AssertThat(AndroidFlatCutouts.MarksSwaying(windModern, cutout: false, blended: false, grass: true, character: false, on: true)).IsTrue();
        AssertThat(AndroidFlatCutouts.MarksSwaying(windModern, cutout: true, blended: false, grass: false, character: false, on: false)).IsFalse();
        AssertThat(AndroidFlatCutouts.MarksSwaying(windModern, cutout: true, blended: true, grass: false, character: false, on: true)).IsFalse();
        AssertThat(AndroidFlatCutouts.MarksSwaying(windModern, cutout: true, blended: false, grass: false, character: true, on: true)).IsFalse();
        AssertThat(AndroidFlatCutouts.MarksSwaying(windModern, cutout: false, blended: false, grass: false, character: false, on: true)).IsFalse();
        AssertThat(AndroidFlatCutouts.MarksSwaying(PropLite, cutout: true, blended: false, grass: false, character: false, on: true)).IsFalse();
        // lite_flat_cutouts alone still leaves the wind builds per pixel.
        AssertThat(AndroidFlatCutouts.Marks(windModern, cutout: true, blended: false, grass: false, character: false, on: true)).IsFalse();
    }
}
