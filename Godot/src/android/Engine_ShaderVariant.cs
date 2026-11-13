using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Refii.Models;

/// <summary>
/// `shader_variant`: which build of the surface shaders a material is given — "full" (the
/// default) or the mobile "lite" set, the same shader bodies compiled with defines that REMOVE
/// code at material build time.
/// </summary>
/// <remarks>
/// WHY COMPILE TIME. Skipping work behind a runtime uniform (the bake behind a global, parallax
/// behind a per-material bool) measured 0% on an Adreno 640: the compiler allocates registers for
/// the worst path through the shader whatever a uniform says, so the waves in flight stay where
/// they were. Code that is not in the shader costs no registers.
///
/// WHAT LITE DROPS — the defines and the lines they guard are in standard_material_body.gdshaderinc
/// and terrain_splat_body.gdshaderinc. In EITHER lighting model (lite/, FABLE_LITE), only what is
/// dead or exactly zero there:
///   - the dynamic sky's surface weather and cloud shadow (refused while they are on); the
///     lighting debug views; the shader-cost diagnostic and the zero-work A/B switches (their
///     defaults kept); the near-fog fallback for an unsupported curve; parallax (nearly invisible);
///   - per material: the environment cube where none is bound, the whole specular path where the
///     specular colour is provably zero; per terrain chunk: the cliff triplanar where no normal
///     leaves the ground band, the normal-map work where none is bound; the vista pages' loop.
/// Under the MODERN model only (lite/modern/, FABLE_LITE_MODERN), also every bake `fable_ambient`
/// multiplies by zero at `fable_unified` = 1 (PRT, probe, vertex colours, the terrain lightmap,
/// their darkening and tether; amb3 keeps its 0.35 share). Under the fable model — which Android
/// forces (AndroidGameHost, REFII_LIGHTING_MODEL=fable) — those are the visible ambient and stay.
/// With `android_modern_ambient_fold` the modern builds also compile Godot's own ambient out
/// (`ambient_light_disabled`) and add it in the body instead (AndroidModernAmbientFold).
///
/// REFUSED, falling back to "full": off Android (unless REFII_SHADER_VARIANT forces it, for the
/// tools), or with the dynamic sky's surface weather or cloud shadows on. READ ONCE, with the
/// lighting model, at the first material build: a variant belongs to the shader a material was
/// built with, so changing the key — or any of those settings — takes a relaunch, which is also
/// how the two sets are A/B'd.
///
/// NOT FILES. A lite build is its full shader's own text with the defines in front of the body's
/// #include (<see cref="Compose"/>), made in code (Engine_ShaderSpecialisation.LoadFile). The
/// paths below name the builds, so everything that picks one by path keeps working, and no copy of
/// a full shader's render mode can drift from it.
///
/// "lite_flat" is "lite" with the terrain's layer normal maps and cavity also compiled out: a
/// LOOK change, offered separately for that reason. Plain "lite" already compiles them out
/// where they are not bound (`terrain_normal_maps = false`), where removing them changes nothing.
/// </remarks>
public static class Engine_ShaderVariant
{
    public const string Key = "shader_variant";

    public enum Mode
    {
        Full,
        Lite,
        LiteFlat,
    }

    /// <summary>The variant in force for this process.</summary>
    public static Mode Current => Chosen.Value.Mode;

    /// <summary>Lite under the modern model: the lite/modern/ builds, which also drop the bakes.</summary>
    public static bool ModernBuilds => Chosen.Value.Modern;

    /// <summary>
    /// Desktop full surface shaders compiled with <c>FABLE_MODERN_DIRECT</c> when running the Modern
    /// lighting model without lighting debug overrides: strips dead Classic PRT/vertex/lightmap bakes
    /// at compile time while keeping full weather, cloud shadows, ORM, and HDR active.
    /// </summary>
    public static bool ModernDirect => Chosen.Value.ModernDirect;

    public static bool Lite => Current != Mode.Full;

    /// <summary>"lite_flat": the terrain draws without its layer normal maps and cavity.</summary>
    public static bool FlatTerrain => Current == Mode.LiteFlat;

    private static readonly Lazy<(Mode Mode, bool Modern, bool ModernDirect)> Chosen = new(Choose, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The key's value, as written; unknown spellings are "full".</summary>
    public static Mode Parse(string value) => value.Trim().Trim('"').ToLowerInvariant() switch
    {
        "lite" => Mode.Lite,
        "lite_flat" => Mode.LiteFlat,
        _ => Mode.Full,
    };

    /// <summary>
    /// The variant for a wanted mode under the process's conditions, and why "full" was kept
    /// instead (empty when it was not). Pure, so the policy can be read without the engine.
    /// </summary>
    public static Mode Decide(Mode wanted, bool platform, bool surfaceWeather, bool cloudShadows, out string refusal)
    {
        refusal = wanted == Mode.Full ? string.Empty
            : !platform ? "not Android (REFII_SHADER_VARIANT forces it)"
            : surfaceWeather ? "dynamic surface weather is on"
            : cloudShadows ? "dynamic cloud shadows are on"
            : string.Empty;
        return refusal.Length == 0 ? wanted : Mode.Full;
    }

    /// <summary>
    /// Pure policy for Desktop <c>FABLE_MODERN_DIRECT</c> specialization: enabled when running the
    /// full shader set under the Modern lighting model with no lighting debug view override.
    /// </summary>
    public static bool DecideModernDirect(bool runtime, bool modernLighting, int debugView) =>
        runtime && modernLighting && debugView == 0;

    private static int ConfiguredDebugView()
    {
        if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("REFII_LEVEL_DEBUG")))
        {
            return 1;
        }

        try
        {
            return Refii.Core.Engine_RefiiConfig.Int(Refii.Core.Engine_RefiiConfig.Read(out _), "lighting_debug_view", 0);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static (Mode, bool, bool) Choose()
    {
        string forced = System.Environment.GetEnvironmentVariable("REFII_SHADER_VARIANT") ?? string.Empty;
        string value;
        try
        {
            value = forced.Length != 0
                ? forced
                : Refii.Core.Engine_RefiiConfig.String(Refii.Core.Engine_RefiiConfig.Read(out _), Key, "full");
        }
        catch (Exception)
        {
            value = "full";
        }

        Mode wanted = Parse(value);
        if (wanted == Mode.Full)
        {
            bool modernDirect = DecideModernDirect(
                !Refii.Core.Engine_AppRoot.IsTestHost,
                Refii.Lighting.Engine_LightingFrame.ModernModel,
                ConfiguredDebugView());
            return (Mode.Full, false, modernDirect);
        }

        bool surfaceWeather;
        bool cloudShadows;
        try
        {
            surfaceWeather = Refii.Level.Engine_DynamicSurfaceWeather.Enabled;
            cloudShadows = Refii.Level.Engine_DynamicSky.Enabled && Refii.Level.Engine_DynamicSky.CloudShadows;
        }
        catch (Exception)
        {
            surfaceWeather = cloudShadows = true;
        }

        Mode mode = Decide(
            wanted,
            Refii.Core.Engine_Platform.IsAndroid || forced.Length != 0,
            surfaceWeather,
            cloudShadows,
            out string refusal);
        bool modern = mode != Mode.Full && Refii.Lighting.Engine_LightingFrame.ModernModel;
        bool fallbackModernDirect = mode == Mode.Full && DecideModernDirect(
            !Refii.Core.Engine_AppRoot.IsTestHost,
            Refii.Lighting.Engine_LightingFrame.ModernModel,
            ConfiguredDebugView());
        GD.Print(refusal.Length == 0
            ? $"[shader] variant {value}: lite surface shaders, {(modern ? "modern" : "fable")} model builds (relaunch to change)"
            : $"[shader] variant {value} refused, drawing full: {refusal}");
        return (mode, modern, fallbackModernDirect);
    }

    /// <summary>A lite path in the build set this process uses: lite/ or lite/modern/.</summary>
    private static string Flavour(string litePath) =>
        ModernBuilds ? litePath.Replace("/lite/", "/lite/modern/", StringComparison.Ordinal) : litePath;

    /// <summary>The fable-model (lite/) spelling of either set's path.</summary>
    private static string Unflavour(string path) =>
        path.Replace("/lite/modern/", "/lite/", StringComparison.Ordinal);

    private const string MaterialLite = "res://src/Engine/material/lite/";
    private const string TerrainLite = "res://src/Engine/level/terrain/lite/";

    /// <summary>The lite builds of one full surface shader. A null Env means the full shader is
    /// kept for a material that needs its environment cube (tree and wind surfaces bind none).</summary>
    private readonly record struct ModelSet(string Lite, string? Env, string NoSpec);

    private static readonly Dictionary<string, ModelSet> Models = new(StringComparer.Ordinal)
    {
        [Engine_ShaderFamily.StandardPath] = new(
            MaterialLite + "standard_material_lite.gdshader",
            MaterialLite + "standard_material_lite_env.gdshader",
            MaterialLite + "standard_material_lite_nospec.gdshader"),
        [Engine_ShaderFamily.StandardCulledPath] = new(
            MaterialLite + "standard_material_culled_lite.gdshader",
            MaterialLite + "standard_material_culled_lite_env.gdshader",
            MaterialLite + "standard_material_culled_lite_nospec.gdshader"),
        [Engine_ShaderFamily.StandardTranslucentPath] = new(
            MaterialLite + "standard_material_translucent_lite.gdshader",
            MaterialLite + "standard_material_translucent_lite_env.gdshader",
            MaterialLite + "standard_material_translucent_lite_nospec.gdshader"),
        [Engine_ShaderFamily.StandardTreePath] = new(
            MaterialLite + "standard_material_tree_lite.gdshader",
            null,
            MaterialLite + "standard_material_tree_lite_nospec.gdshader"),
        [Engine_ShaderFamily.StandardWindPath] = new(
            MaterialLite + "standard_material_wind_lite.gdshader",
            null,
            MaterialLite + "standard_material_wind_lite_nospec.gdshader"),
    };

    public const string TerrainFullPath = "res://src/Engine/level/terrain/terrain_splat.gdshader";
    public const string TerrainLitePath = TerrainLite + "terrain_splat_lite.gdshader";
    public const string TerrainLiteNoCliffPath = TerrainLite + "terrain_splat_lite_nocliff.gdshader";
    public const string TerrainFlatPath = TerrainLite + "terrain_splat_lite_flat.gdshader";
    public const string TerrainFlatNoCliffPath = TerrainLite + "terrain_splat_lite_flat_nocliff.gdshader";
    public const string TerrainPagePath = TerrainLite + "terrain_splat_lite_page.gdshader";

    /// <summary>A lite build: the full shader it is made from and the defines it adds.</summary>
    private readonly record struct Build(string Full, string[] Defines);

    private static readonly Dictionary<string, Build> Builds = BuildBuilds();

    private static readonly Dictionary<string, string> FullOf = BuildFullOf();

    private static Dictionary<string, Build> BuildBuilds()
    {
        var map = new Dictionary<string, Build>(StringComparer.Ordinal);
        foreach ((string full, ModelSet set) in Models)
        {
            map[set.Lite] = new(full, new[] { "FABLE_LITE", "FABLE_LITE_NO_ENVMAP" });
            map[set.NoSpec] = new(full, new[] { "FABLE_LITE_NO_SPEC" });
            if (set.Env is { } env)
            {
                map[env] = new(full, new[] { "FABLE_LITE" });
            }
        }

        map[TerrainLitePath] = new(TerrainFullPath, new[] { "FABLE_LITE" });
        map[TerrainLiteNoCliffPath] = new(TerrainFullPath, new[] { "FABLE_LITE", "FABLE_LITE_NO_CLIFF" });
        map[TerrainFlatPath] = new(TerrainFullPath, new[] { "FABLE_LITE", "FABLE_LITE_FLAT_TERRAIN" });
        map[TerrainFlatNoCliffPath] = new(TerrainFullPath, new[] { "FABLE_LITE", "FABLE_LITE_FLAT_TERRAIN", "FABLE_LITE_NO_CLIFF" });
        map[TerrainPagePath] = new(TerrainFullPath, new[] { "FABLE_TERRAIN_PAGE" });

        // The modern-model builds: the same, with the bakes dropped too.
        foreach ((string lite, Build build) in new List<KeyValuePair<string, Build>>(map))
        {
            map[lite.Replace("/lite/", "/lite/modern/", StringComparison.Ordinal)] =
                new(build.Full, [.. build.Defines, "FABLE_LITE_MODERN"]);
        }

        return map;
    }

    private static Dictionary<string, string> BuildFullOf()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string lite, Build build) in Builds)
        {
            map[lite] = build.Full;
        }

        return map;
    }

    /// <summary>Whether <paramref name="path"/> names a lite build (made in code, not a file).</summary>
    public static bool IsBuild(string path) => Builds.ContainsKey(path);

    /// <summary>
    /// Whether <paramref name="path"/> is a full surface or terrain shader that should be compiled
    /// with <c>FABLE_MODERN_DIRECT</c> in this process.
    /// </summary>
    public static bool ShouldComposeModernDirect(string path) =>
        ModernDirect && (Models.ContainsKey(path) || path == TerrainFullPath);

    /// <summary>
    /// Prepends <c>#define FABLE_MODERN_DIRECT</c> ahead of <paramref name="fullCode"/>'s body <c>#include</c>. Pure.
    /// </summary>
    public static string ComposeModernDirect(string fullCode) =>
        Refii.Fx.Engine_FxDepthReads.Compose(fullCode, "FABLE_MODERN_DIRECT");

    /// <summary>
    /// The text of the lite build <paramref name="path"/>: <paramref name="fullCode"/> (its full
    /// shader's) with the build's defines ahead of the body's #include, in order, then the process's
    /// settings' defines and `lite_extra_defines`.
    /// </summary>
    public static string Compose(string fullCode, string path) => WithExtraDefines(Compose(
        fullCode,
        path,
        NoBakedAoFor(Refii.Lighting.Engine_LevelAmbientOcclusion.BakedAoEnabled, Refii.Lighting.Engine_LightingFrame.ModernModel, FableNoBakedAoOn),
        NoStaticShadows.Value,
        Refii.AndroidRuntime.AndroidLightTablePacking.On,
        Refii.AndroidRuntime.AndroidModernAmbientFold.On), ProcessExtraDefines.Value);

    /// <summary>
    /// Whether the baked-AO helpers compile out (<see cref="NoBakedAo"/>): with baked AO off, or under the
    /// fable model, where Engine_LevelAmbientOcclusion publishes strength and light-affect 0 and the
    /// helpers return exactly the define's constants. Pure.
    /// </summary>
    public static bool NoBakedAoFor(bool bakedAoEnabled, bool modernModel) => NoBakedAoFor(bakedAoEnabled, modernModel, fableFold: true);

    /// <summary>
    /// <see cref="NoBakedAoFor(bool, bool)"/> with the fable model's half as a choice
    /// (<see cref="FableNoBakedAoKey"/>): off, the fable builds keep the baked-AO helpers and read the zeros
    /// Engine_LevelAmbientOcclusion publishes, as before the fold. Pure.
    /// </summary>
    public static bool NoBakedAoFor(bool bakedAoEnabled, bool modernModel, bool fableFold) =>
        !bakedAoEnabled || (!modernModel && fableFold);

    /// <summary>`lite_fable_no_baked_ao` in refii.toml (Android, default on): the fable model's lite builds compile the baked-AO helpers out.</summary>
    public const string FableNoBakedAoKey = "lite_fable_no_baked_ao";

    private static readonly Lazy<bool> FableNoBakedAoChosen = new(() =>
    {
        try
        {
            return !Refii.Core.Engine_Platform.IsAndroid ||
                Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), FableNoBakedAoKey, true);
        }
        catch (Exception)
        {
            return true;
        }
    });

    /// <summary>This process's <see cref="FableNoBakedAoKey"/>; read once, as the variant is.</summary>
    public static bool FableNoBakedAoOn => FableNoBakedAoChosen.Value;

    /// <summary>`lite_skip_black_lights`'s define, `lite_half_precision_lighting`'s, `lite_world_vertex`'s, `lite_fold_fable_model`'s, `lite_fold_modern_model`'s, `lite_fog`'s and `lite_lean_darkening`'s (AndroidLeanShading), `lite_lean_light_ao`'s and `lite_lean_frame_sign`'s (AndroidLeanTerms), the varyings' (AndroidLeanVaryings: `lite_trim_varyings` and its six neighbours), `lite_normal_maps`', `lite_ground_ao`'s (AndroidGroundShading), then `lite_extra_defines`.</summary>
    private static readonly Lazy<string[]> ProcessExtraDefines = new(() =>
        ProcessDefines(SkipBlackLightsOn, HalfPrecisionLightingOn,
            LeverDefines(FoldFableModelOn, FoldModernModelOn, ModernAoAtDefaults()).Concat(Refii.AndroidRuntime.AndroidLeanShading.Defines()).Concat(Refii.AndroidRuntime.AndroidLeanTerms.Defines()).Concat(Refii.AndroidRuntime.AndroidLeanVaryings.Defines()).Concat(NormalMapDefines(NormalMapsOn)).Concat(Refii.AndroidRuntime.AndroidGroundShading.ProcessDefines()).Concat(ExtraDefines.Value), WorldVertexOn));

    /// <summary>
    /// The defines of the shader levers that are settings of their own, in order, for the values given:
    /// <see cref="FoldFableModel"/>, then <see cref="FoldModernModel"/> and, with it, <see cref="FoldAoDefaults"/>
    /// when the AO dials start at their defaults (<see cref="AoAtDefaults"/>). Pure.
    /// </summary>
    public static string[] LeverDefines(bool foldFableModel, bool foldModernModel = false, bool aoAtDefaults = false)
    {
        var defines = new List<string>(3);
        if (foldFableModel) defines.Add(FoldFableModel);
        if (foldModernModel)
        {
            defines.Add(FoldModernModel);
            if (aoAtDefaults) defines.Add(FoldAoDefaults);
        }

        return defines.ToArray();
    }

    /// <summary>`lite_fold_modern_model` in refii.toml (Android, default off).</summary>
    public const string FoldModernModelKey = "lite_fold_modern_model";

    /// <summary>
    /// The define that compiles the modern model's one fixed global into the modern lite builds:
    /// `fable_unified` 1, which Engine_LevelLighting publishes under the modern model (caves included)
    /// and which those builds already assume, having compiled out every bake it multiplies by zero.
    /// The fable builds ignore it. The same picture.
    /// </summary>
    public const string FoldModernModel = "FABLE_LITE_FOLD_MODERN_MODEL";

    /// <summary>
    /// With <see cref="FoldModernModel"/>, when the process starts with baked AO on at strength 1 and
    /// light-affect 0.25 (the defaults, which Engine_LevelAmbientOcclusion publishes as they are): the
    /// strength's pow and the light-affect compiled in. A change to either, or baked AO turned off,
    /// then takes effect at the next start.
    /// </summary>
    public const string FoldAoDefaults = "FABLE_LITE_FOLD_AO_DEFAULTS";

    /// <summary>Whether the AO dials are the defaults <see cref="FoldAoDefaults"/> compiles in. Pure.</summary>
    public static bool AoAtDefaults(bool bakedAoEnabled, float strength, float lightAffect) =>
        bakedAoEnabled && strength == 1.0f && lightAffect == 0.25f;

    private static bool ModernAoAtDefaults()
    {
        try
        {
            return AoAtDefaults(Refii.Lighting.Engine_LevelAmbientOcclusion.BakedAoEnabled,
                Refii.Lighting.Engine_LevelAmbientOcclusion.BakedAoStrength,
                Refii.Lighting.Engine_LevelAmbientOcclusion.AoLightAffect);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Whether the values ask for <see cref="FoldModernModel"/>; unset is off. Pure.</summary>
    public static bool FoldModernModelWanted(IReadOnlyDictionary<string, string> values) =>
        Refii.Core.Engine_RefiiConfig.Bool(values, FoldModernModelKey, false);

    private static readonly Lazy<bool> FoldModernModelChosen = new(() =>
    {
        try
        {
            return Refii.Core.Engine_Platform.IsAndroid && FoldModernModelWanted(Refii.Core.Engine_RefiiConfig.Read(out _));
        }
        catch (Exception)
        {
            return false;
        }
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether this process's lite builds take <see cref="FoldModernModel"/>; read once, as the variant is.</summary>
    public static bool FoldModernModelOn => FoldModernModelChosen.Value;

    /// <summary>`lite_fold_fable_model` in refii.toml (Android, default off).</summary>
    public const string FoldFableModelKey = "lite_fold_fable_model";

    /// <summary>
    /// The define that compiles the fable model's fixed globals into the fable-model lite builds as
    /// constants (fable_lighting.gdshaderinc): `fable_unified` 0, the sky tether's weight 0, no
    /// interior and its bake lift 1, no fill floor — what Engine_LevelLighting publishes for the
    /// life of a fable-model process, and the only model those builds are drawn under. The modern
    /// builds ignore it. The same picture: every mix, max and multiply by them folds to its value.
    /// </summary>
    public const string FoldFableModel = "FABLE_LITE_FOLD_FABLE_MODEL";

    /// <summary>Whether the values ask for <see cref="FoldFableModel"/>; unset is off. Pure.</summary>
    public static bool FoldFableModelWanted(IReadOnlyDictionary<string, string> values) =>
        Refii.Core.Engine_RefiiConfig.Bool(values, FoldFableModelKey, false);

    private static readonly Lazy<bool> FoldFableModelChosen = new(() =>
    {
        try
        {
            return Refii.Core.Engine_Platform.IsAndroid && FoldFableModelWanted(Refii.Core.Engine_RefiiConfig.Read(out _));
        }
        catch (Exception)
        {
            return false;
        }
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether this process's lite builds take <see cref="FoldFableModel"/>; read once, as the variant is.</summary>
    public static bool FoldFableModelOn => FoldFableModelChosen.Value;

    /// <summary><see cref="ProcessDefines(bool, bool, IEnumerable{string}, bool)"/> without the half-precision lighting. Pure.</summary>
    public static string[] ProcessDefines(bool skipBlackLights, IEnumerable<string> extra, bool worldVertex = false) =>
        ProcessDefines(skipBlackLights, false, extra, worldVertex);

    /// <summary>The process-level defines in order: the skipped black lights, the half-precision lighting, the world-space vertex stage, then the free-form extras. Pure.</summary>
    public static string[] ProcessDefines(bool skipBlackLights, bool halfPrecisionLighting, IEnumerable<string> extra, bool worldVertex = false)
    {
        var defines = new List<string>();
        if (skipBlackLights)
        {
            defines.Add(SkipBlackLights);
        }

        if (halfPrecisionLighting)
        {
            defines.Add(Mediump);
        }

        if (worldVertex)
        {
            defines.Add(WorldVertex);
        }

        defines.AddRange(extra);
        return defines.ToArray();
    }

    /// <summary><see cref="Compose(string, string)"/> with the settings given, without `lite_extra_defines`. Pure.</summary>
    public static string Compose(string fullCode, string path, bool noBakedAo, bool noStaticShadow, bool oneRowLight, bool ambientFold = false)
    {
        string code = fullCode;
        foreach (string define in Builds[path].Defines)
        {
            code = Refii.Fx.Engine_FxDepthReads.Compose(code, define);
        }

        // BAKED AO OFF COMPILES IT OUT. `baked_ao_enabled = false` only zeroes a strength; its
        // texture fetch and maths stay in every prop, character and terrain shader, and a
        // uniform frees no registers on a phone GPU (see above). Read once, as the variant is. The
        // fable model publishes the same zeros (NoBakedAoFor).
        if (noBakedAo)
        {
            code = Refii.Fx.Engine_FxDepthReads.Compose(code, NoBakedAo);
        }

        // BAKED LAMP SHADOWS OFF COMPILES THEIR LOOKUP OUT. With the option off no atlas is bound and
        // the lookup returns at once, but its 5x4-tap filter stayed in every lamp's light pass: about
        // 7% of an S10e's GPU time in Bowerstone Market.
        if (noStaticShadow)
        {
            code = Refii.Fx.Engine_FxDepthReads.Compose(code, NoStaticShadow);

            // ONE FETCH PER LIGHT. With the lookup gone only the stated range is left of rows 1-2,
            // and the table packs it beside the position (AndroidLightTablePacking). Only here, so a
            // shader never reads the packed row without the lookup compiled out.
            if (oneRowLight)
            {
                code = Refii.Fx.Engine_FxDepthReads.Compose(code, Refii.AndroidRuntime.AndroidLightTablePacking.Define);
            }
        }

        // GODOT'S AMBIENT CARRIED BY THE SHADER (`android_modern_ambient_fold`, AndroidModernAmbientFold):
        // only the modern builds, the ones Godot's ambient reaches, and the word with its define,
        // since a build with the word and no fold would lose the ambient.
        if (ambientFold && FoldsAmbient(path))
        {
            code = Refii.Fx.Engine_FxDepthReads.Compose(code, Refii.AndroidRuntime.AndroidModernAmbientFold.Define);
            code = Engine_ShaderSpecialisation.WithRenderMode(code, Refii.AndroidRuntime.AndroidModernAmbientFold.RenderModeWord);
        }

        return code;
    }

    /// <summary>Whether the lite build <paramref name="path"/> takes `android_modern_ambient_fold`: a lite/modern/ one.</summary>
    public static bool FoldsAmbient(string path) =>
        Builds.TryGetValue(path, out Build build) && Array.IndexOf(build.Defines, "FABLE_LITE_MODERN") >= 0;

    /// <summary>Whether <paramref name="code"/> has a line defining <paramref name="name"/>.</summary>
    public static bool Defines(string code, string name) => System.Text.RegularExpressions.Regex.IsMatch(
        code, @"^\s*#define\s+" + System.Text.RegularExpressions.Regex.Escape(name) + @"\b", System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary><paramref name="code"/> with each of <paramref name="defines"/> it does not already define. Pure.</summary>
    /// <remarks>A second #define of a name the build states already fails the compile.</remarks>
    public static string WithExtraDefines(string code, IEnumerable<string> defines)
    {
        foreach (string define in defines)
        {
            if (!Defines(code, define))
            {
                code = Refii.Fx.Engine_FxDepthReads.Compose(code, define);
            }
        }

        return code;
    }

    /// <summary>
    /// `lite_extra_defines` in refii.toml: shader defines added to every lite build, comma-separated,
    /// for pricing a cut on a phone with a relaunch instead of a rebuild. Names only; anything else
    /// is ignored. Read once, as the variant is.
    /// </summary>
    private static readonly Lazy<string[]> ExtraDefines = new(() =>
    {
        try
        {
            return ParseDefines(Refii.Core.Engine_RefiiConfig.String(Refii.Core.Engine_RefiiConfig.Read(out _), "lite_extra_defines", ""));
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    });

    /// <summary>The define names in <paramref name="value"/>, in order, without duplicates or invalid names.</summary>
    public static string[] ParseDefines(string value) => value.Trim().Trim('"')
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(name => System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Z][A-Z0-9_]*$"))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    /// <summary>The define that marks the lighting helpers' colour and weight maths mediump (fable_lighting.gdshaderinc);
    /// set by `lite_half_precision_lighting`, or through `lite_extra_defines`.</summary>
    public const string Mediump = "FABLE_LITE_MEDIUMP";

    /// <summary>`lite_half_precision_lighting` in refii.toml (Android, default off): composes <see cref="Mediump"/>.</summary>
    public const string HalfPrecisionLightingKey = "lite_half_precision_lighting";

    /// <summary>Whether the values ask for <see cref="Mediump"/>; unset is off. Pure.</summary>
    public static bool HalfPrecisionLightingWanted(IReadOnlyDictionary<string, string> values) =>
        Refii.Core.Engine_RefiiConfig.Bool(values, HalfPrecisionLightingKey, false);

    private static readonly Lazy<bool> HalfPrecisionLightingChosen = new(() =>
    {
        try
        {
            return Refii.Core.Engine_Platform.IsAndroid && HalfPrecisionLightingWanted(Refii.Core.Engine_RefiiConfig.Read(out _));
        }
        catch (Exception)
        {
            return false;
        }
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether this process's lite builds take the half-precision lighting; read once, as the variant is.</summary>
    public static bool HalfPrecisionLightingOn => HalfPrecisionLightingChosen.Value;

    /// <summary>`lite_skip_black_lights` in refii.toml (Android, default on).</summary>
    public const string SkipBlackLightsKey = "lite_skip_black_lights";

    /// <summary>The define that skips the lighting maths for lights that add nothing (Godot's empty first slot per light type).</summary>
    public const string SkipBlackLights = "FABLE_LITE_SKIP_BLACK_LIGHTS";

    /// <summary>Whether the values ask for <see cref="SkipBlackLights"/>; unset is on. Pure.</summary>
    public static bool SkipBlackLightsWanted(IReadOnlyDictionary<string, string> values) =>
        Refii.Core.Engine_RefiiConfig.Bool(values, SkipBlackLightsKey, true);

    private static readonly Lazy<bool> SkipBlackLightsChosen = new(() =>
    {
        try
        {
            return Refii.Core.Engine_Platform.IsAndroid && SkipBlackLightsWanted(Refii.Core.Engine_RefiiConfig.Read(out _));
        }
        catch (Exception)
        {
            return false;
        }
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether this process's lite builds skip the black lights; read once, as the variant is.</summary>
    public static bool SkipBlackLightsOn => SkipBlackLightsChosen.Value;

    /// <summary>`lite_normal_maps` in refii.toml (Android): how the lite model shaders apply a material's normal map.</summary>
    public const string NormalMapsKey = "lite_normal_maps";

    /// <summary>
    /// The values of <see cref="NormalMapsKey"/> (standard_material_body.gdshaderinc's `FABLE_LITE_NM_*`).
    /// Full: Godot applies the map through the mesh's tangent frame (NORMAL_MAP), as before. Direct: the
    /// body writes the normal it already maps, so Godot's second copy of that maths goes; the same picture.
    /// Fast: Direct with the tangent frame rebuilt per pixel from screen derivatives, so no tangent is
    /// decoded, transformed or interpolated; the same picture to a level or two of 255 (mirrored UV
    /// islands and two-sided back faces included), the frame flat across each triangle. Off: no normal
    /// map at all (FABLE_SPEC_NO_NORMAL_MAP, what a material without one draws), a flatter picture.
    /// Models only; the ground's maps are `terrain_normal_maps`.
    /// </summary>
    public enum NormalMapPath
    {
        Full,
        Direct,
        Fast,
        Off,
    }

    /// <summary>The key's value, as written; anything unknown is <see cref="NormalMapPath.Full"/>. Pure.</summary>
    public static NormalMapPath ParseNormalMaps(string value) => value.Trim().Trim('"').ToLowerInvariant() switch
    {
        "direct" => NormalMapPath.Direct,
        "fast" => NormalMapPath.Fast,
        "off" => NormalMapPath.Off,
        _ => NormalMapPath.Full,
    };

    /// <summary>The defines a lite build takes for <paramref name="path"/>. Pure.</summary>
    public static string[] NormalMapDefines(NormalMapPath path) => path switch
    {
        NormalMapPath.Direct => new[] { "FABLE_LITE_NM_DIRECT" },
        NormalMapPath.Fast => new[] { "FABLE_LITE_NM_DERIV" },
        NormalMapPath.Off => new[] { "FABLE_SPEC_NO_NORMAL_MAP" },
        _ => Array.Empty<string>(),
    };

    private static readonly Lazy<NormalMapPath> NormalMapsChosen = new(() =>
    {
        try
        {
            return Refii.Core.Engine_Platform.IsAndroid
                ? ParseNormalMaps(Refii.Core.Engine_RefiiConfig.String(Refii.Core.Engine_RefiiConfig.Read(out _), NormalMapsKey, "full"))
                : NormalMapPath.Full;
        }
        catch (Exception)
        {
            return NormalMapPath.Full;
        }
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>This process's <see cref="NormalMapsKey"/>; read once, as the variant is.</summary>
    public static NormalMapPath NormalMapsOn => NormalMapsChosen.Value;

    /// <summary>
    /// Whether every lite build already carries <paramref name="define"/> from the process
    /// (`lite_normal_maps = "off"` states FABLE_SPEC_NO_NORMAL_MAP everywhere), so a specialisation
    /// adding it would only be a second shader with the same code.
    /// </summary>
    public static bool ProcessWide(string define) => Array.IndexOf(NormalMapDefines(NormalMapsOn), define) >= 0;

    /// <summary>`lite_world_vertex` in refii.toml (Android, default off).</summary>
    /// <remarks>
    /// The model builds' vertex stage in world space (standard_material_body.gdshaderinc,
    /// `world_vertex_coords`): Godot's Mobile vertex shader otherwise multiplies the view matrix into
    /// the model matrix, and its normal matrix, for every vertex, and a MultiMesh copy's matrix is
    /// per instance, so a batched prop pays both products per vertex. Counted on the compiled Low
    /// builds (SPIR-V, matrix products as multiply-adds): a batched prop's vertex stage 499 -> 424,
    /// its position-only (binning) stage 215 -> 167; a single prop 383 -> 308 and 131 -> 83. The
    /// same picture: model height and up come from MODEL_MATRIX, exact for unsheared transforms.
    /// The render mode also makes Godot refuse a solid build its shared depth-only shadow material
    /// (scene_shader_forward_mobile.h, `uses_shared_shadow_material`), so it is meant for presets
    /// that render no shadow map.
    /// </remarks>
    public const string WorldVertexKey = "lite_world_vertex";

    /// <summary>The define that runs the model builds' vertex stage in world space.</summary>
    public const string WorldVertex = "FABLE_LITE_WORLD_VERTEX";

    /// <summary>Whether the values ask for <see cref="WorldVertex"/>; unset is off. Pure.</summary>
    public static bool WorldVertexWanted(IReadOnlyDictionary<string, string> values) =>
        Refii.Core.Engine_RefiiConfig.Bool(values, WorldVertexKey, false);

    private static readonly Lazy<bool> WorldVertexChosen = new(() =>
    {
        try
        {
            return Refii.Core.Engine_Platform.IsAndroid && WorldVertexWanted(Refii.Core.Engine_RefiiConfig.Read(out _));
        }
        catch (Exception)
        {
            return false;
        }
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether this process's lite builds run the vertex stage in world space; read once, as the variant is.</summary>
    public static bool WorldVertexOn => WorldVertexChosen.Value;

    /// <summary>The define that turns the baked-AO helpers in fable_lighting.gdshaderinc into constants.</summary>
    public const string NoBakedAo = "FABLE_LITE_NO_BAKED_AO";

    /// <summary>The define that drops the baked lamp-shadow lookup from fable_attenuation.</summary>
    public const string NoStaticShadow = "FABLE_LITE_NO_STATIC_SHADOW";

    /// <summary>Whether this process draws without baked lamp shadows; read once, as the variant is.</summary>
    private static readonly Lazy<bool> NoStaticShadows = new(() => Refii.AndroidRuntime.AndroidBakedShadows.LevelsIgnoreBakes);

    /// <summary>Whether this process's lite builds compile the lamp-shadow lookup out (<see cref="NoStaticShadow"/>).</summary>
    public static bool NoStaticShadowsOn => NoStaticShadows.Value;

    /// <summary>Every lite shader file, both sets, for the compile check.</summary>
    public static IEnumerable<string> AllVariantPaths => FullOf.Keys;

    /// <summary>The lite files this process can draw (one set), for the loads and the warm-up.</summary>
    public static IEnumerable<string> ActiveVariantPaths
    {
        get
        {
            foreach (string path in FullOf.Keys)
            {
                if (path.Contains("/lite/modern/", StringComparison.Ordinal) == ModernBuilds)
                {
                    yield return path;
                }
            }
        }
    }

    /// <summary>The full shader a lite one was compiled from; any other path unchanged.</summary>
    public static string FullPathOf(string path) => FullOf.TryGetValue(path, out string? full) ? full : path;

    /// <summary>
    /// Whether a vegetation surface can draw this lite build: Engine_MdlMaterial.TryCreateShared
    /// moves every one to the two-sided, wind or tree shader, and a translucent second pass is
    /// made before ApplyVegetation marks anything premultiplied.
    /// </summary>
    public static bool DrawsVegetation(string path) =>
        FullPathOf(path) is not (Engine_ShaderFamily.StandardCulledPath or Engine_ShaderFamily.StandardTranslucentPath);

    /// <summary>Whether grass can draw this lite build: it binds no specular, so short of authored PBR it is a no-specular one.</summary>
    public static bool DrawsGrass(string path) =>
        DrawsVegetation(path) && Builds.TryGetValue(path, out Build build) && Array.IndexOf(build.Defines, "FABLE_LITE_NO_SPEC") >= 0;

    /// <summary>Whether this path is the full terrain splat or one of its lite builds.</summary>
    public static bool IsTerrainSplat(string? path) => path is not null && FullPathOf(path) == TerrainFullPath;

    /// <summary>A file the warm-up should not draw: the set this process does not use.</summary>
    public static bool SkipWarm(string path) => Lite
        ? Models.ContainsKey(path) || path == TerrainFullPath ||
          (FullOf.ContainsKey(path) && path.Contains("/lite/modern/", StringComparison.Ordinal) != ModernBuilds)
        : FullOf.ContainsKey(path);

    /// <summary>
    /// The lite shader for a model surface drawn with <paramref name="fullPath"/>, or null to keep
    /// it. Exactness, not taste, picks the build:
    /// <list type="bullet">
    /// <item>NO SPECULAR when the specular colour is zero everywhere the body reads it: no map (or
    /// grass, which binds none) and not authored PBR — the ORM reconstruction and the cube both
    /// weight by it — or foliage with no cube, whose light() never takes the lobe.</item>
    /// <item>ENV when a cube is bound and a specular map weights it (the cube term is
    /// `spec_colour * env^2`, and only the map fills spec_colour — PBR included).</item>
    /// <item>otherwise the plain lite build, which has no cube code.</item>
    /// </list>
    /// </summary>
    public static string? ModelPath(string fullPath, bool specularMap, bool envmap, bool pbr, bool foliage)
    {
        if (!Lite || !Models.TryGetValue(fullPath, out ModelSet set))
        {
            return null;
        }

        if (!pbr && (!specularMap || (foliage && !envmap)))
        {
            return Flavour(set.NoSpec);
        }

        if (envmap && specularMap)
        {
            return set.Env is { } env ? Flavour(env) : null;
        }

        return Flavour(set.Lite);
    }

    /// <summary>The lite terrain splat for a landscape, or the full one.</summary>
    public static string TerrainPath(bool normalsBound) =>
        !Lite ? TerrainFullPath : Flavour(normalsBound && !FlatTerrain ? TerrainLitePath : TerrainFlatPath);

    /// <summary>The no-cliff build of a lite terrain shader (same set), or null when there is none.</summary>
    public static string? TerrainNoCliffPath(string? path)
    {
        if (path is null)
        {
            return null;
        }

        string? plain = Unflavour(path) switch
        {
            TerrainLitePath => TerrainLiteNoCliffPath,
            TerrainFlatPath => TerrainFlatNoCliffPath,
            _ => null,
        };
        return plain is null ? null
            : path.Contains("/lite/modern/", StringComparison.Ordinal)
                ? plain.Replace("/lite/", "/lite/modern/", StringComparison.Ordinal)
                : plain;
    }

    /// <summary>The vista page shader: the page-only lite build, or the full splat.</summary>
    public static string PageTerrainPath => Lite ? Flavour(TerrainPagePath) : TerrainFullPath;
}
