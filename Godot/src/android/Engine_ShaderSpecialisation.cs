using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Godot;

namespace Refii.Models;

/// <summary>
/// Lite shaders SPECIALISED at material build: a lite build (Engine_ShaderVariant's paths, made from
/// the full shader by <see cref="LoadFile"/>) recompiled with extra defines or render modes, as a
/// shader built in code. One shader per (build, defines); every material with the same facts
/// shares it.
/// </summary>
/// <remarks>
/// WHY NOT FILES. The specialisations multiply: a file per combination would be hundreds of copies
/// of the same few lines. The lite build's own text is the source (its `Code`), the defines go in
/// before its `#include`, and Godot's preprocessor does the rest, so a specialised build is the
/// body with code removed and nothing else.
///
/// WHAT IS NOT A FILE. A built shader has no ResourcePath; <see cref="PathOf"/> answers with the
/// lite build's path it was made from, and every caller that picks a build by path asks it.
///
/// Each specialisation has its own refii.toml key, read once at the first lite material build, so
/// the phone can A/B each by relaunch. All of them apply only under `shader_variant = "lite"`.
/// </remarks>
public static class Engine_ShaderSpecialisation
{
    /// <summary>`lite_shadowless`: the lite shaders compiled with `shadows_disabled` while nothing
    /// renders a shadow map (see <see cref="Shadowless"/>).</summary>
    public const string ShadowlessKey = "lite_shadowless";

    /// <summary>`lite_bake_variants`: a static prop's lite surfaces drawn by a build specialised to
    /// the bake mode its instance states (see <see cref="ApplyBakeMode"/>).</summary>
    public const string BakeVariantsKey = "lite_bake_variants";

    /// <summary>`lite_material_folds`: a model material's set-once flags folded into its build (see
    /// <see cref="FoldDefines"/>).</summary>
    public const string MaterialFoldsKey = "lite_material_folds";

    /// <summary>`lite_terrain_single_fetch`: the lite terrain's no-weight fallback drawn by its
    /// splat loop's own fetch instead of a second copy of it (see <see cref="TerrainDefines"/>).</summary>
    public const string TerrainSingleFetchKey = "lite_terrain_single_fetch";

    private readonly record struct Keys(bool Shadowless, bool BakeVariants, bool MaterialFolds, bool TerrainSingleFetch);

    private static readonly Lazy<Keys> Read = new(ReadKeys, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private static Keys ReadKeys()
    {
        if (!Engine_ShaderVariant.Lite)
        {
            return default;
        }

        try
        {
            IReadOnlyDictionary<string, string> values = Refii.Core.Engine_RefiiConfig.Read(out _);
            var keys = new Keys(
                Shadowless: Refii.Core.Engine_RefiiConfig.Bool(values, ShadowlessKey, false),
                BakeVariants: Refii.Core.Engine_RefiiConfig.Bool(values, BakeVariantsKey, false),
                MaterialFolds: Refii.Core.Engine_RefiiConfig.Bool(values, MaterialFoldsKey, false),
                TerrainSingleFetch: Refii.Core.Engine_RefiiConfig.Bool(values, TerrainSingleFetchKey, false));
            GD.Print($"[shader] specialisations: {ShadowlessKey}={keys.Shadowless}, " +
                $"{BakeVariantsKey}={keys.BakeVariants}, {MaterialFoldsKey}={keys.MaterialFolds}, " +
                $"{TerrainSingleFetchKey}={keys.TerrainSingleFetch} (relaunch to change)");
            return keys;
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static readonly object Gate = new();

    private sealed class Built
    {
        public required string Path;
        public required string Source;
        public required string[] Defines;
        public required Shader Shader;
    }

    private static readonly Dictionary<string, Built> ByKey = new(StringComparer.Ordinal);

    private static readonly ConditionalWeakTable<Shader, Built> ByShader = new();

    private static readonly Dictionary<string, Shader> LiteBuilds = new(StringComparer.Ordinal);

    /// <summary>
    /// The shader <paramref name="path"/> names: the file, or for a lite build
    /// (<see cref="Engine_ShaderVariant.IsBuild"/>) its full shader's text with the lite defines,
    /// made once and named by that path (<see cref="PathOf"/>). Null when the file does not load.
    /// </summary>
    public static Shader? LoadFile(string path)
    {
        if (!Engine_ShaderVariant.IsBuild(path))
        {
            return GD.Load<Shader>(path);
        }

        lock (Gate)
        {
            if (LiteBuilds.TryGetValue(path, out Shader? hit))
            {
                return hit;
            }

            if (GD.Load<Shader>(Engine_ShaderVariant.FullPathOf(path)) is not { } full)
            {
                return null;
            }

            var shader = new Shader { Code = Engine_ShaderVariant.Compose(full.Code, path) };
            LiteBuilds[path] = shader;
            ByShader.Add(shader, new Built { Path = path, Source = shader.Code, Defines = Array.Empty<string>(), Shader = shader });
            return shader;
        }
    }

    /// <summary>The wrapper file a shader stands for: its own path, or the one it was built from.</summary>
    public static string PathOf(Shader? shader)
    {
        if (shader is null)
        {
            return string.Empty;
        }

        return ByShader.TryGetValue(shader, out Built? built) ? built.Path : shader.ResourcePath;
    }

    /// <summary>The defines a built shader carries (none for a file).</summary>
    public static IReadOnlyList<string> DefinesOf(Shader? shader) =>
        shader is not null && ByShader.TryGetValue(shader, out Built? built) ? built.Defines : Array.Empty<string>();

    /// <summary>Whether any specialisation can apply in this process.</summary>
    public static bool Any => Read.Value.Shadowless || Read.Value.MaterialFolds || Read.Value.TerrainSingleFetch;

    /// <summary>
    /// The defines for the lite terrain splat (not the vista page, which has no loop):
    /// FABLE_SPEC_TERRAIN_ONE_SAMPLE under `lite_terrain_single_fetch`.
    /// </summary>
    public static string[] TerrainDefines => Read.Value.TerrainSingleFetch
        ? new[] { "FABLE_SPEC_TERRAIN_ONE_SAMPLE" }
        : Array.Empty<string>();

    /// <summary>Whether `lite_material_folds` is on (and the lite set drawn).</summary>
    public static bool MaterialFolds => Read.Value.MaterialFolds;

    /// <summary>
    /// The defines for a model material's flags that are off: each folds a uniform the material
    /// sets once, at build, to the false it holds (standard_material_body.gdshaderinc). Pure.
    /// </summary>
    /// <remarks>
    /// Not authored PBR, no ORM, no self-illumination map, not premultiplied, no grass normal
    /// gradient: exact, the uniform is false for the material's life. NO NORMAL MAP also removes
    /// NORMAL_MAP from the build, so Godot stops carrying the tangent and binormal: the normal is
    /// the interpolated one either way, to float rounding (Godot's flat-map path renormalises it).
    /// </remarks>
    public static string[] FoldDefines(bool pbr, bool orm, bool emission, bool premultiplied, bool normalMap, bool grassBend)
    {
        var defines = new List<string>(6);
        if (!pbr) defines.Add("FABLE_SPEC_NOT_PBR");
        if (!orm) defines.Add("FABLE_SPEC_NO_ORM");
        if (!emission) defines.Add("FABLE_SPEC_NO_EMISSION");
        if (!premultiplied) defines.Add("FABLE_SPEC_NOT_PREMULTIPLIED");
        if (!normalMap) defines.Add("FABLE_SPEC_NO_NORMAL_MAP");
        if (!grassBend) defines.Add("FABLE_SPEC_NO_GRASS_BEND");
        return defines.ToArray();
    }

    /// <summary>Whether `lite_bake_variants` is on (and the lite set drawn).</summary>
    public static bool BakeVariants => Read.Value.BakeVariants;

    /// <summary>
    /// The shader for a lite wrapper with <paramref name="defines"/>: the file itself when nothing
    /// applies, otherwise the shared built one. Null when the file does not load.
    /// <paramref name="file"/> is the wrapper already loaded, for a caller on a worker thread that
    /// loads through its own lock (Engine_MdlMaterial.LoadShared).
    /// </summary>
    public static Shader? Load(string path, IEnumerable<string>? defines = null, Shader? file = null)
    {
        string[] sorted = Normalise(defines);
        bool shadowless = Shadowless;
        if (sorted.Length == 0 && !shadowless)
        {
            return file ?? LoadFile(path);
        }

        string key = path + "\n" + string.Join("\n", sorted);
        lock (Gate)
        {
            if (ByKey.TryGetValue(key, out Built? hit))
            {
                return hit.Shader;
            }

            file ??= LoadFile(path);
            if (file is null)
            {
                return null;
            }

            string source = file.Code;
            var shader = new Shader { Code = Compose(source, sorted, _shadowless == true) };
            var built = new Built { Path = path, Source = source, Defines = sorted, Shader = shader };
            ByKey[key] = built;
            ByShader.Add(shader, built);
            return shader;
        }
    }

    /// <summary>The same build with <paramref name="extra"/> defines added.</summary>
    public static Shader? With(Shader current, IEnumerable<string> extra)
    {
        var defines = new List<string>(DefinesOf(current));
        defines.AddRange(extra);
        return Load(PathOf(current), defines, string.IsNullOrEmpty(current.ResourcePath) ? null : current);
    }

    private static string[] Normalise(IEnumerable<string>? defines)
    {
        if (defines is null)
        {
            return Array.Empty<string>();
        }

        var set = new SortedSet<string>(defines, StringComparer.Ordinal);
        set.Remove(string.Empty);
        var result = new string[set.Count];
        set.CopyTo(result);
        return result;
    }

    private static readonly Regex RenderMode = new(@"(?m)^(\s*render_mode\s+[^;]*);", RegexOptions.CultureInvariant);

    private static readonly Regex Include = new(@"(?m)^\s*#include\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// A wrapper's text with <paramref name="defines"/> placed before its first `#include`, and,
    /// when <paramref name="shadowless"/>, `shadows_disabled` added to its render mode. Pure.
    /// </summary>
    public static string Compose(string wrapper, IReadOnlyList<string> defines, bool shadowless)
    {
        string code = wrapper.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (shadowless)
        {
            Match mode = RenderMode.Match(code);
            code = mode.Success
                ? code.Remove(mode.Index, mode.Length).Insert(mode.Index, mode.Groups[1].Value + ", shadows_disabled;")
                : code.Replace("shader_type spatial;", "shader_type spatial;\nrender_mode shadows_disabled;", StringComparison.Ordinal);
        }

        if (defines.Count != 0)
        {
            var block = new StringBuilder();
            foreach (string define in Normalise(defines))
            {
                block.Append("#define ").Append(define).Append('\n');
            }

            Match include = Include.Match(code);
            code = include.Success ? code.Insert(include.Index, block.ToString()) : code + "\n" + block;
        }

        return code;
    }

    // ---------------------------------------------------------------- shadows_disabled

    private static bool? _shadowless;

    private static string _shadowedBecause = string.Empty;

    /// <summary>Why the shadowed builds must come back, while they wait for a loading screen; null when nothing waits.</summary>
    private static string? _waiting;

    /// <summary>
    /// Whether the lite shaders are being built without Godot's shadow sampling: `lite_shadowless`
    /// is on and the settings render no shadow map — the sun has no cascades
    /// (`sun_shadow_distance = 0`, no debug override forcing it) and `placed_light_shadows` is off.
    /// </summary>
    /// <remarks>
    /// EXACT WHILE IT HOLDS. Godot's receiver code reads a light's map only behind
    /// `shadow_opacity > 0.001`, which is zero for a light that casts nothing, so the shadow term is
    /// 1 either way; compiled out, the cascade select and the PCF taps stop costing registers.
    ///
    /// AND IT IS DROPPED ONCE A SETTING CALLS FOR SHADOWS, for the rest of the session: an Options
    /// change, a preset switch or a cvar that would render a shadow map puts every built shader back
    /// on its wrapper's own code. The materials keep their shader object, so they all recompile
    /// shadowed in one frame, and that frame is long: with cold caches the 29 shaders' compile and
    /// their 232 pipelines took 20.8 s on the S25+ (12.6 s with the shader cache warm) and 38.6 s on
    /// the S10e, a game that looks hung; under 1 s with both caches warm.
    ///
    /// SO THE REBUILD WAITS FOR A LEVEL LOAD'S SCREEN (<see cref="LoadingScreen"/>), where a long
    /// frame is not seen, and the lights hold their shadows off until it has run (<see cref="ShadowCast"/>
    /// answers false). Not a save's "Saving content", which is up for about two seconds: the Thor
    /// tester's first save after a spot came near froze on it for 26 s (28 shaders, 163 pipelines).
    /// The Options panel offers the rebuild at once instead (<see cref="RebuildNow"/>), saying first
    /// that the game will pause.
    ///
    /// A LIGHT ALONE NEVER DROPS IT. The shipped spots that cast even with placed shadows off
    /// (Engine_LightShadowBudget) cast nothing while it holds, as the preset's other lights do. Each
    /// used to queue the rebuild the first time one came near, the long frame included; on the S10e
    /// Oakfield also held 0.1 GB more PSS (0.37 GB more native heap) after it.
    /// </remarks>
    public static bool Shadowless
    {
        get
        {
            if (_shadowless is { } known)
            {
                return known;
            }

            lock (Gate)
            {
                _shadowless ??= Read.Value.Shadowless && !ShadowsPossible(out _);
                if (_shadowless == true)
                {
                    GD.Print("[shader] lite_shadowless: lite shaders built with shadows_disabled while no shadow map renders");
                }

                return _shadowless.Value;
            }
        }
    }

    /// <summary>Whether the settings could render a shadow map, and which one.</summary>
    public static bool ShadowsPossible(out string why)
    {
        why = Decide(
            Refii.Lighting.Engine_LevelLighting.SunShadowOverride,
            Refii.Lighting.Engine_LevelLighting.SunFarDepth,
            Refii.Lighting.Engine_LevelLights.PlacedShadowsEnabled);
        return why.Length != 0;
    }

    /// <summary>Why shadows could render (empty when they cannot). Pure.</summary>
    public static string Decide(bool? sunOverride, float sunFarDepth, bool placedShadows) =>
        sunOverride == true ? "the sun's shadow is forced on"
        : sunOverride != false && sunFarDepth > 0f ? "sun_shadow_distance is above 0"
        : placedShadows ? "placed_light_shadows is on"
        : string.Empty;

    /// <summary>
    /// A light is about to cast. With the shaders built without shadow sampling it may not: when the
    /// settings call for shadows the rebuild that brings them back waits for a level load, and when
    /// they do not (a shipped spot with placed shadows off) the light casts nothing this session.
    /// </summary>
    /// <returns>Whether the light may cast now.</returns>
    public static bool ShadowCast(string light)
    {
        if (_shadowless != true) return true;
        if (ShadowsPossible(out string why))
        {
            Wait(why);
        }
        else if (!_heldReported)
        {
            _heldReported = true;
            GD.Print($"[shader] lite_shadowless: {light} casts no shadow while the settings render none");
        }

        return false;
    }

    private static bool _heldReported;

    /// <summary>The settings changed: if they could now render shadows, the shadowed builds wait for a loading screen.</summary>
    public static void SettingsChanged()
    {
        if (_shadowless == true && ShadowsPossible(out string why))
        {
            Wait(why);
        }
    }

    /// <summary>Whether the shadowed builds are waiting for a loading screen, the lights holding their shadows off.</summary>
    public static bool RebuildWaiting => _waiting is not null;

    /// <summary>A level load's loading screen is up: a waiting rebuild runs now, where its long frame is not seen.</summary>
    public static void LoadingScreen()
    {
        if (_waiting is { } why) Restore(why);
    }

    /// <summary>Runs a waiting rebuild at once, for a player who chose to pause for it (the Options panel).</summary>
    public static void RebuildNow()
    {
        if (_waiting is { } why) Restore(why + ", rebuilt now from Options");
    }

    /// <summary>Sets whether the shaders are built without shadow sampling, nothing waiting: for the tests.</summary>
    internal static void SetShadowlessForTests(bool? shadowless)
    {
        _shadowless = shadowless;
        _waiting = null;
    }

    private static void Wait(string why)
    {
        if (_waiting is not null) return;
        _waiting = why;
        GD.Print($"[shader] lite_shadowless: the shadowed builds wait for a loading screen ({why}); shadows held off until then");
    }

    private static void Restore(string why)
    {
        var shaders = new List<Built>();
        lock (Gate)
        {
            if (_shadowless != true)
            {
                return;
            }

            _shadowless = false;
            _shadowedBecause = why;
            _waiting = null;
            shaders.AddRange(ByKey.Values);
        }

        foreach (Built built in shaders)
        {
            built.Shader.Code = Compose(built.Source, built.Defines, false);
        }

        GD.Print($"[shader] lite_shadowless off for this session ({_shadowedBecause}): {shaders.Count} shaders rebuilt with shadows");
    }

    // ---------------------------------------------------------------- bake-mode builds

    /// <summary>
    /// The defines for a build drawn only by instances in one bake mode (`lightmap_colour.w`, see
    /// fable_lighting.gdshaderinc), or none where ir3 shows no gain. Pure.
    /// </summary>
    /// <remarks>
    /// Modes 0, 2, 3, 4 fix the mode (FABLE_SPEC_BAKE_*) and drop the PRT transfer, which only mode
    /// 1 reads. Mode 1, the probe, keeps its mode a uniform — fixed, ir3 puts the no-specular build
    /// at 32 registers (8 to 6 waves) — and drops the transfer only on a mesh without CUSTOM0, where
    /// the transfer is zero and its `present` flag 0.
    /// </remarks>
    public static string[] BakeDefines(int mode, bool meshHasTransfer) => mode switch
    {
        0 => new[] { "FABLE_SPEC_BAKE_NONE", "FABLE_SPEC_NO_PRT" },
        1 => meshHasTransfer ? Array.Empty<string>() : new[] { "FABLE_SPEC_NO_PRT" },
        2 => new[] { "FABLE_SPEC_BAKE_COLOR_SINGLE", "FABLE_SPEC_NO_PRT" },
        3 => new[] { "FABLE_SPEC_BAKE_COLOR_DOUBLE", "FABLE_SPEC_NO_PRT" },
        4 => new[] { "FABLE_SPEC_BAKE_AMB3", "FABLE_SPEC_NO_PRT" },
        _ => Array.Empty<string>(),
    };

    // Lazy: a StringName needs the engine, and the pure helpers above are tested without one.
    private static StringName BakeStampMeta => _bakeStampMeta ??= "lite_bake_stamp";

    private static StringName? _bakeStampMeta;

    /// <summary>A bake-mode copy back to the shared material it was made from.</summary>
    private static readonly ConditionalWeakTable<ShaderMaterial, ShaderMaterial> CopySource = new();

    /// <summary>Per shared material, its copies by (mode, transfer).</summary>
    private static readonly ConditionalWeakTable<ShaderMaterial, Dictionary<int, ShaderMaterial>> Copies = new();

    /// <summary>
    /// Puts a static prop's lite surfaces on the build for the bake mode its instance now states,
    /// as surface overrides (a single-surface MultiMesh: its material override). Called by
    /// Engine_LevelBakedLighting.ApplyInstanceLighting with the mode it is about to set.
    /// </summary>
    /// <remarks>
    /// EXACT: every instance drawing a copy states the copy's mode, and the instance's mode is
    /// rewritten only here, so the constant and the uniform cannot disagree. One copy per
    /// (shared material, mode, transfer), the same object for every instance in that mode, so
    /// Godot still merges their draws. A copy is a shallow duplicate: the same textures and
    /// parameters, and only its shader differs. A surface that already carries someone else's
    /// override is left alone.
    /// </remarks>
    public static void ApplyBakeMode(GeometryInstance3D node, int mode)
    {
        if (!Read.Value.BakeVariants)
        {
            return;
        }

        if (node is MeshInstance3D single && single.Mesh is { } mesh)
        {
            long stamp = unchecked(((long)mesh.GetInstanceId() * 8) + mode);
            if (single.HasMeta(BakeStampMeta) && single.GetMeta(BakeStampMeta).AsInt64() == stamp)
            {
                return;
            }

            int count = mesh.GetSurfaceCount();
            for (int i = 0; i < count; i++)
            {
                Material? current = single.GetSurfaceOverrideMaterial(i);
                if (BakeCopyFor(current, mesh.SurfaceGetMaterial(i), mode, HasTransfer(mesh, i), out ShaderMaterial? copy) &&
                    !ReferenceEquals(current, copy))
                {
                    single.SetSurfaceOverrideMaterial(i, copy);
                }
            }

            single.SetMeta(BakeStampMeta, stamp);
        }
        else if (node is MultiMeshInstance3D multi && multi.Multimesh?.Mesh is { } shared && shared.GetSurfaceCount() == 1)
        {
            Material? current = multi.MaterialOverride;
            if (BakeCopyFor(current, shared.SurfaceGetMaterial(0), mode, HasTransfer(shared, 0), out ShaderMaterial? copy) &&
                !ReferenceEquals(current, copy))
            {
                multi.MaterialOverride = copy;
            }
        }
    }

    private static bool HasTransfer(Mesh mesh, int surface) =>
        mesh is not ArrayMesh array || (array.SurfaceGetFormat(surface) & Mesh.ArrayFormat.FormatCustom0) != 0;

    /// <summary>The override a surface should carry (null: none), or false to leave it as it is.</summary>
    private static bool BakeCopyFor(Material? current, Material? meshMaterial, int mode, bool transfer, out ShaderMaterial? copy)
    {
        copy = null;
        ShaderMaterial? source;
        lock (Gate)
        {
            if (current is null)
            {
                source = meshMaterial as ShaderMaterial;
            }
            else if (current is ShaderMaterial ours && CopySource.TryGetValue(ours, out ShaderMaterial? from))
            {
                // Our copy; the mesh under it may have changed since.
                source = ReferenceEquals(from, meshMaterial) ? from : meshMaterial as ShaderMaterial;
            }
            else
            {
                return false;
            }
        }

        if (source is null || !IsLiteModel(source))
        {
            // Not ours to specialise: drop a stale copy of ours, else leave the surface alone.
            return current is not null;
        }

        string[] defines = BakeDefines(mode, transfer);
        if (defines.Length == 0)
        {
            return true;
        }

        copy = BakeCopy(source, mode * 2 + (transfer ? 1 : 0), defines);
        return true;
    }

    // ---------------------------------------------------------------- far builds

    /// <summary>What a far build compiles out: the normal map (fetch, tangent frame, NORMAL_MAP).</summary>
    public static readonly string[] FarDefines = { "FABLE_SPEC_NO_NORMAL_MAP" };

    /// <summary>Per shared material, its far copy (itself when the far build is the same shader).</summary>
    private static readonly ConditionalWeakTable<ShaderMaterial, ShaderMaterial> FarCopies = new();

    /// <summary>A far copy back to the shared material it was made from.</summary>
    private static readonly ConditionalWeakTable<ShaderMaterial, ShaderMaterial> FarSource = new();

    /// <summary>The shared material a far copy stands for; any other material unchanged.</summary>
    public static Material? NearOf(Material? material) =>
        material is ShaderMaterial shaded && FarSource.TryGetValue(shaded, out ShaderMaterial? source) ? source : material;

    /// <summary>Whether this material is a far copy made by <see cref="FarCopy"/>.</summary>
    public static bool IsFarCopy(Material? material) =>
        material is ShaderMaterial shaded && FarSource.TryGetValue(shaded, out _);

    /// <summary>
    /// A lite model material's FAR copy (`prop_far_shading_metres`, Engine_PropFarShading): the same
    /// textures and parameters on its build with <see cref="FarDefines"/> added, one per shared
    /// material. Null when the material is not a lite model surface, or when its build already has
    /// no normal map (`lite_material_folds` folded it), where the copy would draw the same shader.
    /// Main thread.
    /// </summary>
    public static ShaderMaterial? FarCopy(Material? material)
    {
        if (material is not ShaderMaterial source || IsFarCopy(source) || !IsLiteModel(source))
        {
            return null;
        }

        if (FarCopies.TryGetValue(source, out ShaderMaterial? hit))
        {
            return ReferenceEquals(hit, source) ? null : hit;
        }

        Shader? special = source.Shader is { } shader ? With(shader, FarDefines) : null;
        if (special is null || ReferenceEquals(special, source.Shader))
        {
            FarCopies.AddOrUpdate(source, source);
            return null;
        }

        var copy = Refii.Models.Engine_MaterialCopy.Shallow(source);
        copy.Shader = special;
        // The blended second pass of a translucent surface draws the same instance.
        if (source.NextPass is ShaderMaterial next)
        {
            copy.NextPass = FarCopy(next) ?? next;
        }

        FarCopies.AddOrUpdate(source, copy);
        FarSource.AddOrUpdate(copy, source);
        return copy;
    }

    private static bool IsLiteModel(ShaderMaterial material)
    {
        string path = PathOf(material.Shader);
        return path.Length != 0 && Engine_ShaderVariant.FullPathOf(path) != path && !Engine_ShaderVariant.IsTerrainSplat(path);
    }

    private static ShaderMaterial BakeCopy(ShaderMaterial source, int key, string[] defines)
    {
        Dictionary<int, ShaderMaterial> copies;
        lock (Gate)
        {
            copies = Copies.GetValue(source, _ => new Dictionary<int, ShaderMaterial>());
            if (copies.TryGetValue(key, out ShaderMaterial? hit))
            {
                return hit;
            }
        }

        var copy = Refii.Models.Engine_MaterialCopy.Shallow(source);
        if (source.Shader is { } shader && With(shader, defines) is { } special)
        {
            copy.Shader = special;
        }

        // The blended second pass of a translucent surface draws the same instance.
        if (source.NextPass is ShaderMaterial next && IsLiteModel(next))
        {
            copy.NextPass = BakeCopy(next, key, defines);
        }

        lock (Gate)
        {
            if (copies.TryGetValue(key, out ShaderMaterial? raced))
            {
                return raced;
            }

            copies[key] = copy;
            CopySource.AddOrUpdate(copy, source);
        }

        return copy;
    }
}
