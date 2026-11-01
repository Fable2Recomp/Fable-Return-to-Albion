using System;
using System.Collections.Generic;
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

    public static bool Lite => Current != Mode.Full;

    /// <summary>"lite_flat": the terrain draws without its layer normal maps and cavity.</summary>
    public static bool FlatTerrain => Current == Mode.LiteFlat;

    private static readonly Lazy<(Mode Mode, bool Modern)> Chosen = new(Choose, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

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

    private static (Mode, bool) Choose()
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
            return (Mode.Full, false);
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
        GD.Print(refusal.Length == 0
            ? $"[shader] variant {value}: lite surface shaders, {(modern ? "modern" : "fable")} model builds (relaunch to change)"
            : $"[shader] variant {value} refused, drawing full: {refusal}");
        return (mode, modern);
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
    /// The text of the lite build <paramref name="path"/>: <paramref name="fullCode"/> (its full
    /// shader's) with the build's defines ahead of the body's #include, in order. Pure.
    /// </summary>
    public static string Compose(string fullCode, string path)
    {
        string code = fullCode;
        foreach (string define in Builds[path].Defines)
        {
            code = Refii.Fx.Engine_FxDepthReads.Compose(code, define);
        }

        return code;
    }

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
