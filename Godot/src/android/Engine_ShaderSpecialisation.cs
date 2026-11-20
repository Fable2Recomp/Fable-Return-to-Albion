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

    /// <summary>`lite_material_folds_low_ram`: which of those flags the low-memory tier folds (see
    /// <see cref="FoldScope"/>): off, const, normal or all.</summary>
    public const string MaterialFoldsLowRamKey = "lite_material_folds_low_ram";

    /// <summary>`lite_terrain_single_fetch`: the lite terrain's no-weight fallback drawn by its
    /// splat loop's own fetch instead of a second copy of it (see <see cref="TerrainDefines"/>).</summary>
    public const string TerrainSingleFetchKey = "lite_terrain_single_fetch";

    /// <summary>`lite_vegetation_folds`: the vegetation flags (foliage, grass, authored foliage
    /// normals) folded into a model material's build, and a tree build's constant-zero transfer
    /// varying dropped (see <see cref="VegetationDefines"/>).</summary>
    public const string VegetationFoldsKey = "lite_vegetation_folds";

    private readonly record struct Keys(bool Shadowless, bool BakeVariants, FoldScope Folds, bool TerrainSingleFetch, bool VegetationFolds = false,
        TerrainLevers Terrain = default);

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
                // Each set of flags is a shader build of its own, and a build's bookkeeping is native
                // memory: the low-memory tier has its own scope, every fold by default since 2026-11-16 (on a
                // OnePlus 7 Pro at Low, 2461 against 2456 MB PSS with none, and 3-4% less GPU time).
                Folds: !Refii.Core.Engine_RefiiConfig.Bool(values, MaterialFoldsKey, false) ? FoldScope.Off
                    : Refii.Game.AndroidMemoryRelease.LowRamMode ? ParseFoldScope(Refii.Core.Engine_RefiiConfig.String(values, MaterialFoldsLowRamKey, "all"))
                    : FoldScope.All,
                TerrainSingleFetch: Refii.Core.Engine_RefiiConfig.Bool(values, TerrainSingleFetchKey, false),
                VegetationFolds: Refii.Core.Engine_RefiiConfig.Bool(values, VegetationFoldsKey, false),
                Terrain: ReadTerrainLevers(values));
            GD.Print($"[shader] specialisations: {ShadowlessKey}={keys.Shadowless}, " +
                $"{BakeVariantsKey}={keys.BakeVariants}, {MaterialFoldsKey}={keys.Folds != FoldScope.Off} ({keys.Folds}), " +
                $"{TerrainSingleFetchKey}={keys.TerrainSingleFetch}, {VegetationFoldsKey}={keys.VegetationFolds}, " +
                $"ground {keys.Terrain} (relaunch to change)");
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
        bool isLiteBuild = Engine_ShaderVariant.IsBuild(path);
        if (!isLiteBuild && !Engine_ShaderVariant.ShouldComposeModernDirect(path))
        {
            return GD.Load<Shader>(path);
        }

        lock (Gate)
        {
            if (LiteBuilds.TryGetValue(path, out Shader? hit))
            {
                return hit;
            }
        }

        // NOT UNDER THE GATE: Godot's loader can wait for the main thread, and the main thread takes
        // the gate when it runs a worker's material upload (ServerSyncs.GpuUploadPacer). Holding it
        // here left a level build and the main thread waiting on each other.
        if (GD.Load<Shader>(Engine_ShaderVariant.FullPathOf(path)) is not { } full)
        {
            return null;
        }

        string code = isLiteBuild
            ? Engine_ShaderVariant.Compose(full.Code, path)
            : Engine_ShaderVariant.ComposeModernDirect(full.Code);
        lock (Gate)
        {
            if (LiteBuilds.TryGetValue(path, out Shader? raced))
            {
                return raced;
            }

            var shader = new Shader { Code = code };
            LiteBuilds[path] = shader;
            ByShader.Add(shader, new Built { Path = path, Source = code, Defines = Array.Empty<string>(), Shader = shader });
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
    public static bool Any => Read.Value.Shadowless || MaterialFolds || Read.Value.TerrainSingleFetch || Read.Value.VegetationFolds ||
        Refii.AndroidRuntime.AndroidCharacterCull.On || Refii.AndroidRuntime.AndroidCharacterShading.On.Any ||
        Refii.AndroidRuntime.AndroidVertexLitProps.On || Refii.AndroidRuntime.AndroidFlatCutouts.On || Refii.AndroidRuntime.AndroidFlatCutouts.SwayingOn ||
        Refii.AndroidRuntime.AndroidCutoutPrepass.On;

    /// <summary>Whether `lite_vegetation_folds` is on (and the lite set drawn).</summary>
    public static bool VegetationFolds => Read.Value.VegetationFolds;

    /// <summary>
    /// The defines for a model material's vegetation flags, each folded to the value it holds
    /// (standard_material_body.gdshaderinc), plus FABLE_SPEC_TREE_NO_TRANSFER on a tree build,
    /// whose transfer varying is the constant zero. Pure.
    /// </summary>
    /// <remarks>
    /// Exact: ApplyVegetation sets the three uniforms once, at build, with the facts. Both sides
    /// fold, so a build splits by the mixes its materials hold: (none), foliage, grass, each with
    /// or without authored foliage normals. Off vegetation every build already holds (none) and
    /// splits nothing.
    /// </remarks>
    public static string[] VegetationDefines(bool foliage, bool grass, bool foliageNormals, bool tree)
    {
        var defines = new List<string>(4)
        {
            foliage ? "FABLE_SPEC_FOLIAGE" : "FABLE_SPEC_NOT_FOLIAGE",
            grass ? "FABLE_SPEC_GRASS" : "FABLE_SPEC_NOT_GRASS",
            foliageNormals ? "FABLE_SPEC_FOLIAGE_NORMALS" : "FABLE_SPEC_NO_FOLIAGE_NORMALS",
        };
        if (tree) defines.Add("FABLE_SPEC_TREE_NO_TRANSFER");
        return defines.ToArray();
    }

    /// <summary>
    /// The defines for the lite terrain splat (not the vista page, which has no loop):
    /// FABLE_SPEC_TERRAIN_ONE_SAMPLE under `lite_terrain_single_fetch`, then the ground levers
    /// (<see cref="TerrainLeverDefines"/>).
    /// </summary>
    public static string[] TerrainDefines => TerrainLeverDefines(Read.Value.TerrainSingleFetch, Read.Value.Terrain);

    /// <summary>`lite_terrain_bit_loop`: the splat loop visits only the layers that can weigh a pixel (the same picture).</summary>
    public const string TerrainBitLoopKey = "lite_terrain_bit_loop";

    /// <summary>`lite_terrain_max_layers`: at most this many of a pixel's layers sampled, the strongest; 0 samples all.</summary>
    public const string TerrainMaxLayersKey = "lite_terrain_max_layers";

    /// <summary>`lite_terrain_min_weight`: a layer weighing less than this is not sampled; 0 samples every one.</summary>
    public const string TerrainMinWeightKey = "lite_terrain_min_weight";

    /// <summary>`lite_terrain_cliff_sharpness`: how much of a cliff's two-plane blend is given to one plane; 0 blends as the title.</summary>
    public const string TerrainCliffSharpnessKey = "lite_terrain_cliff_sharpness";

    /// <summary>`lite_terrain_trilinear`: the ground's layer textures filtered without anisotropy.</summary>
    public const string TerrainTrilinearKey = "lite_terrain_trilinear";

    /// <summary>`lite_terrain_vertex_fog`: the ground's fog worked out per vertex instead of per pixel.</summary>
    public const string TerrainVertexFogKey = "lite_terrain_vertex_fog";

    /// <summary>The largest <see cref="TerrainMaxLayersKey"/> that cuts anything: more is all layers.</summary>
    public const int TerrainMaxLayersCap = 4;

    /// <summary>
    /// The ground levers, as read: <see cref="TerrainBitLoopKey"/> and the rest. Max layers 0 and the
    /// two weights 0 are off.
    /// </summary>
    public readonly record struct TerrainLevers(bool BitLoop, int MaxLayers, double MinWeight, double CliffSharpness, bool Trilinear, bool VertexFog);

    /// <summary>The levers in <paramref name="values"/>, each clamped to what it can mean; unset keys are their defaults (only the bit loop on). Pure.</summary>
    public static TerrainLevers ReadTerrainLevers(IReadOnlyDictionary<string, string> values)
    {
        int maxLayers = Refii.Core.Engine_RefiiConfig.Int(values, TerrainMaxLayersKey, 0);
        double minWeight = Refii.Core.Engine_RefiiConfig.Float(values, TerrainMinWeightKey, 0f);
        double sharpness = Refii.Core.Engine_RefiiConfig.Float(values, TerrainCliffSharpnessKey, 0f);
        return new TerrainLevers(
            BitLoop: Refii.Core.Engine_RefiiConfig.Bool(values, TerrainBitLoopKey, true),
            MaxLayers: maxLayers >= 1 && maxLayers < TerrainMaxLayersCap + 1 ? maxLayers : 0,
            MinWeight: minWeight > 0.0 ? Math.Min(minWeight, 0.5) : 0.0,
            CliffSharpness: sharpness > 0.0 ? Math.Min(sharpness, 0.45) : 0.0,
            Trilinear: Refii.Core.Engine_RefiiConfig.Bool(values, TerrainTrilinearKey, false),
            VertexFog: Refii.Core.Engine_RefiiConfig.Bool(values, TerrainVertexFogKey, false));
    }

    /// <summary>
    /// The lite terrain's defines for the settings given (terrain_splat_body.gdshaderinc and
    /// android/terrain_lite_layers.gdshaderinc), in order: FABLE_SPEC_TERRAIN_ONE_SAMPLE, the bit loop,
    /// the layer cap and the weight floor (each with its value), the cliff sharpening (with its
    /// band), the trilinear filter, the vertex fog. A value is written as `NAME VALUE`, which the
    /// composer states as `#define NAME VALUE`. Pure.
    /// </summary>
    /// <remarks>
    /// The bit loop is the same picture bit for bit (the same layers, order and arithmetic). The cap
    /// and the floor rescale the sampled layers to every layer's weight and change the picture only
    /// where layers meet; the vertex fog differs by a level of 255 or two on metre-sized cells; the
    /// sharpening and the trilinear filter change the cliffs and the far ground visibly.
    /// </remarks>
    public static string[] TerrainLeverDefines(bool singleFetch, TerrainLevers levers)
    {
        var defines = new List<string>(10);
        if (singleFetch) defines.Add("FABLE_SPEC_TERRAIN_ONE_SAMPLE");
        if (levers.BitLoop) defines.Add("FABLE_LITE_TERRAIN_BIT_LOOP");
        if (levers.MaxLayers > 0)
        {
            defines.Add("FABLE_LITE_TERRAIN_MAX_LAYERS");
            defines.Add("FABLE_LITE_TERRAIN_MAX_LAYERS_VALUE " + levers.MaxLayers.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (levers.MinWeight > 0.0)
        {
            defines.Add("FABLE_LITE_TERRAIN_MIN_WEIGHT");
            defines.Add("FABLE_LITE_TERRAIN_MIN_WEIGHT_VALUE " + ShaderFloat(levers.MinWeight));
        }

        if (levers.CliffSharpness > 0.0)
        {
            defines.Add("FABLE_LITE_TERRAIN_CLIFF_SHARP");
            defines.Add("FABLE_LITE_TERRAIN_CLIFF_SHARP_VALUE " + ShaderFloat(levers.CliffSharpness));
        }

        if (levers.Trilinear) defines.Add("FABLE_LITE_TERRAIN_TRILINEAR");
        if (levers.VertexFog) defines.Add("FABLE_LITE_TERRAIN_VERTEX_FOG");
        return defines.ToArray();
    }

    /// <summary><paramref name="value"/> as a shader float literal: invariant, always with a point.</summary>
    private static string ShaderFloat(double value) =>
        value.ToString("0.0#####", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Whether `lite_material_folds` is on (and the lite set drawn).</summary>
    public static bool MaterialFolds => Read.Value.Folds != FoldScope.Off;

    /// <summary>Which of a material's folds this process applies (<see cref="ScopeFolds"/>).</summary>
    public static FoldScope MaterialFoldScope => Read.Value.Folds;

    /// <summary>
    /// The defines for a model material's flags that are off: each folds a uniform the material
    /// sets once, at build, to the false it holds (standard_material_body.gdshaderinc). Pure.
    /// </summary>
    /// <remarks>
    /// Not authored PBR, no ORM, no self-illumination map, not premultiplied, no grass normal
    /// gradient: exact, the uniform is false for the material's life. NO NORMAL MAP also removes
    /// every line naming TANGENT, BINORMAL or NORMAL_MAP from the build (the body's
    /// `#ifndef FABLE_SPEC_NO_NORMAL_MAP` guards; the folded `if (false)` alone would keep them, as
    /// Godot marks a built-in used wherever it is named), so Godot stops carrying the tangent
    /// attribute and varyings and skips its own normal-map maths: the normal is the interpolated one
    /// either way, to float rounding (Godot's flat-map path only renormalised it).
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

    /// <summary>Which folds apply: all of them, except in the low-memory tier, which has its own key.</summary>
    /// <remarks>
    /// A build is compiled per (lite build, defines), so a flag folded where some materials have it
    /// and some do not doubles that build. There are 13 lite model builds.
    /// Const folds what splits nothing, so each stays one build: on a phone no stock material has an
    /// ORM or authored PBR, a vegetation surface never draws a culled or translucent build, and
    /// grass, which alone has a bend, draws only a no-specular one.
    /// Normal adds the normal map, the fold worth having (DIAG_NO_NORMALMAP -5% on an Adreno 640), so
    /// at most two per build, 26.
    /// All is every fold, at most 92 by the flags alone.
    /// A flag that is on is never folded, whatever the scope, so a mod's PBR map or a phone with the
    /// ORM turned on gets a build more and the right picture.
    /// </remarks>
    public enum FoldScope
    {
        Off,
        Const,
        Normal,
        All,
    }

    /// <summary>The key's value, as written; anything unknown is the default, <see cref="FoldScope.Normal"/>.</summary>
    public static FoldScope ParseFoldScope(string value) => value.Trim().Trim('"').ToLowerInvariant() switch
    {
        "off" => FoldScope.Off,
        "const" => FoldScope.Const,
        "all" => FoldScope.All,
        _ => FoldScope.Normal,
    };

    /// <summary>The folds of <see cref="FoldDefines"/> this scope applies to the lite build at <paramref name="path"/>. Pure.</summary>
    public static string[] ScopeFolds(FoldScope scope, string path, string[] folds)
    {
        if (scope == FoldScope.All)
        {
            return folds;
        }

        var kept = new List<string>(folds.Length);
        foreach (string fold in folds)
        {
            bool keep = scope != FoldScope.Off && fold switch
            {
                "FABLE_SPEC_NOT_PBR" or "FABLE_SPEC_NO_ORM" => true,
                "FABLE_SPEC_NO_NORMAL_MAP" => scope == FoldScope.Normal,
                "FABLE_SPEC_NOT_PREMULTIPLIED" => !Engine_ShaderVariant.DrawsVegetation(path),
                "FABLE_SPEC_NO_GRASS_BEND" => !Engine_ShaderVariant.DrawsGrass(path),
                _ => false,
            };
            if (keep)
            {
                kept.Add(fold);
            }
        }

        return kept.ToArray();
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
        // A define every lite build states already (`lite_normal_maps = "off"`) would only split a build.
        string[] sorted = Array.FindAll(Normalise(defines), d => !Engine_ShaderVariant.ProcessWide(d));
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
        }

        // Loaded outside the gate, as in LoadFile.
        file ??= LoadFile(path);
        if (file is null)
        {
            return null;
        }

        string source = file.Code;
        lock (Gate)
        {
            if (ByKey.TryGetValue(key, out Built? raced))
            {
                return raced.Shader;
            }

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
            code = WithRenderMode(code, "shadows_disabled");
        }

        // `lite_vertex_lit_props`: its marker compiles the build with `vertex_lighting`.
        code = Refii.AndroidRuntime.AndroidVertexLitProps.Compose(code, defines);

        // `lite_flat_cutouts`: its marker compiles the build `unshaded` with flat leaves' define.
        code = Refii.AndroidRuntime.AndroidFlatCutouts.Compose(code, defines);

        // `android_cutout_depth_prepass`: its marker adds `depth_prepass_alpha`, godot-patches/0017's request.
        code = Refii.AndroidRuntime.AndroidCutoutPrepass.Compose(code, defines);

        // `android_character_cull_back`: its marker turns the build's `cull_disabled` into `cull_back`.
        if (System.Linq.Enumerable.Contains(defines, Refii.AndroidRuntime.AndroidCharacterCull.Define))
        {
            code = Refii.AndroidRuntime.AndroidCharacterCull.CullBack(code);
        }

        if (defines.Count != 0)
        {
            var block = new StringBuilder();
            foreach (string define in Normalise(defines))
            {
                // A name the code states already (lite_extra_defines) would fail the compile twice stated.
                if (Engine_ShaderVariant.Defines(code, define)) continue;
                block.Append("#define ").Append(define).Append('\n');
            }

            Match include = Include.Match(code);
            code = include.Success ? code.Insert(include.Index, block.ToString()) : code + "\n" + block;
        }

        return code;
    }

    /// <summary>
    /// <paramref name="code"/> with <paramref name="word"/> added to the end of its render mode
    /// (one made after `shader_type spatial;` when it has none); unchanged when the mode names it
    /// already, as Godot refuses a word given twice. Pure.
    /// </summary>
    public static string WithRenderMode(string code, string word)
    {
        Match mode = RenderMode.Match(code);
        if (!mode.Success)
        {
            return code.Replace("shader_type spatial;", "shader_type spatial;\nrender_mode " + word + ";", StringComparison.Ordinal);
        }

        if (Regex.IsMatch(mode.Groups[1].Value, @"[\s,]" + Regex.Escape(word) + @"\s*(,|$)", RegexOptions.CultureInvariant))
        {
            return code;
        }

        return code.Remove(mode.Index, mode.Length).Insert(mode.Index, mode.Groups[1].Value + ", " + word + ";");
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
        !Refii.Lighting.Engine_LightingFrame.ModernModel &&
        (mesh is not ArrayMesh array || (array.SurfaceGetFormat(surface) & Mesh.ArrayFormat.FormatCustom0) != 0);

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

    /// <summary>What a far build compiles out: the normal map (fetch, tangent frame, NORMAL_MAP), every
    /// TANGENT/BINORMAL/NORMAL_MAP line with it, as in <see cref="FoldDefines"/>.</summary>
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
