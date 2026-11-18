using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using Refii.Textures;

namespace Refii.Models;

public static partial class Engine_MdlMaterial
{
    /// <summary>Set on a material that sways in the wind (`wind_amplitude` > 0): a local flag, unlike the parameter.</summary>
    public static StringName WindMetaName => WindMetaHolder.Name;

    private static class WindMetaHolder
    {
        public static readonly StringName Name = "sways_in_wind";
    }

    /// <summary>
    /// The wind's direction in world XZ, as a UNIT vector. Set by the level from its theme.
    /// </summary>
    /// <remarks>
    /// DIRECTION ONLY. The theme also states `WindStrengthMin/Max`, and the first version of this
    /// multiplied by it — which bent every tree in Bowerstone flat, because its units were never
    /// established and a census of the shipped themes could not recover a plausible range. An angle
    /// has a known scale; that value does not, so it is not used as a gain. If it is ever decoded,
    /// it belongs as a modest clamped multiplier here and nowhere else.
    ///
    /// APPLIED WHEN A MATERIAL IS BUILT, so a weather change mid-level does not retro-fit surfaces
    /// already standing. Stated rather than hidden; fixing it wants a global shader parameter.
    /// </remarks>
    public static Vector2 WindDirection { get; set; } = Vector2.Zero;

    /// <summary>
    /// Turns on the shader's wind sway for a mesh whose own header says it is vegetation.
    /// </summary>
    /// <remarks>
    /// EVERY NUMBER HERE COMES FROM THE FILE. `WindSkewMaxAmplitude` (0.05 on everything shipped)
    /// is a fraction of the surface's height, and `WindSkewMaxSpeed` (1.0) is the rate — so a
    /// four-metre tree's canopy travels about twenty centimetres, which is a breeze. Nothing is
    /// scaled by a constant chosen here: the previous version multiplied the amplitude by 20 and
    /// then by an unmeasured wind strength, and produced a hurricane.
    ///
    /// THE TWO MODES DIFFER ONLY SLIGHTLY. The guest draws low plants and trees through different
    /// shader families and never has to weigh one against the other; we have one surface shader, so
    /// the distinction has to be a number — but it is a modest one, because the file already states
    /// the amplitude and that is the authority.
    /// </remarks>
    public static void ApplyWind(Material material, Refii.Models.MdlInfo info, Shader? preloadedWind = null)
    {
        if (material is not ShaderMaterial shader || !info.SwaysInWind)
        {
            return;
        }

        float authored = Mathf.Clamp(info.WindAmplitude, 0.0f, 0.5f);
        if (authored <= 0.0f)
        {
            return;
        }

        // TREES DO NOT SWAY, and this is a correctness limit rather than a taste one. Mode 2 is a
        // tree and the guest draws it with `treematerial`, whose own vertex stage we have not
        // implemented. Our bend is anchored at the base and scales with height, which is the right
        // model for a frond growing out of the ground and the WRONG one for a trunk: it leans the
        // whole tree over like a sapling in a gale instead of moving only the canopy.
        //
        // So a tree stays still until `treematerial` is implemented. A still tree is merely the
        // shipped look; a leaning one is an animation we invented.
        if (info.MovementMode != 1)
        {
            return;
        }


        shader.SetShaderParameter("wind_amplitude", authored);
        if (authored > 0f)
        {
            // Read back by the prop streamer without a shader-parameter query: an unset
            // parameter's read is a round trip to the render thread.
            shader.SetMeta(WindMetaName, true);

            // The wind lives in its own shader; the parameters set above stay with the material.
            if ((preloadedWind ?? SharedWind()) is { } wind)
            {
                shader.Shader = wind;
            }
        }
        shader.SetShaderParameter("wind_speed", info.WindSpeed);
        Engine_SceneWind.NoteAmplitude(info.WindAmplitude);
    }

    public static void ApplyVegetation(
        Material material, Refii.Models.MdlInfo info, bool authoredNormals)
    {
        if (material is not ShaderMaterial shader || !IsVegetationDrawPath(info))
        {
            return;
        }

        shader.SetShaderParameter("premultiplied_alpha", true);
        SetFacts(shader, FactPremultiplied, true);

        // THE FAMILY BYTE PICKS THE PAIR (Engine_ShaderFamily.Resolve), not the wind byte: kind 1
        // draws `treematerial_trunk1` (Lambert, no specular), kind 4 the GRASS1 pair, which has
        // neither a specular sampler nor the ambient normal-map darkening.
        if (info.MeshKind == 1)
        {
            shader.SetShaderParameter("foliage", true);
            SetFacts(shader, FactFoliage, true);
        }
        else if (info.MeshKind == 4)
        {
            shader.SetShaderParameter("grass", true);
            SetFacts(shader, FactGrass, true);
            shader.SetShaderParameter("has_specular", false);

            // VS c45 as the classic builds it (`sub_82233570`): a ramp in model height from the
            // header's gradient at the bottom to the top over the file box's height range.
            if ((info.NormalGradientTop > 0f || info.NormalGradientBottom > 0f) &&
                info.FileBoundingBox.Length >= 6 && info.FileBoundingBox[5] != info.FileBoundingBox[2])
            {
                float z0 = info.FileBoundingBox[2], z1 = info.FileBoundingBox[5];
                float slope = (info.NormalGradientTop - info.NormalGradientBottom) / (z1 - z0);
                shader.SetShaderParameter("grass_normal_gradient",
                    new Vector3(1f, slope, info.NormalGradientBottom - (z0 * slope)));
                SetFacts(shader, FactGrassBend, true);
            }
        }
        if (authoredNormals)
        {
            shader.SetShaderParameter("foliage_normals", true);
            SetFacts(shader, FactFoliageNormals, true);
        }
    }

    private static bool IsVegetationDrawPath(Refii.Models.MdlInfo info)
    {
        if (info.MeshKind is 1 or 4)
        {
            return true;
        }

        foreach (Refii.Models.MdlMeshBuffer mb in info.MeshBuffers)
        {
            if (mb.IsFoliagePath || mb.IsEngineTree)
            {
                return true;
            }
        }

        return false;
    }

    public const string ShaderPath =
        "res://src/Engine/material/standard_material.gdshader";



    // NO TEXTURE CACHE HERE ANY MORE. This class used to own a private Dictionary of decoded
    // textures that lived and died with the process, while the UI reached the same files through
    // the game:// loader — so one .tex arrived by two routes and only one of them could ever be
    // given a persistent cache. Both now go through Engine_TexLibrary, and clearing is its call
    // to offer (Engine_TexLibrary.Clear); the forwarder that used to sit here had no callers.

    /// <summary>
    /// A material for a surface that names no textures at all.
    ///
    /// It has to be OUR shader. A StandardMaterial3D here is lit by Godot's own PBR against the
    /// environment ambient, which for this region is a saturated blue — so an untextured surface
    /// comes out pale blue-white while everything beside it is lit by the guest's bake, and the
    /// two do not look like the same world. The albedo sampler defaults to white, so binding
    /// nothing is a neutral surface rather than a black one.
    /// </summary>
    public static Material? Fallback(MdlMaterial? record = null)
    {
        // No textures: no specular colour, so the lite set's no-specular build draws it exactly.
        // BACK FACES CULLED, as every opaque textured surface is (TryCreateShared takes
        // SharedCulled: the guest's view-wide CCW cull stands for a standard material). An
        // untextured surface drawn two-sided shows its back face where the title shows nothing —
        // the occlusion walls Fable 3 places inside its real walls (occlusionwall.mdl, a single
        // untextured 'Material') stood in the castle rooms as white walls for exactly that.
        string family = Engine_ShaderFamily.CullBackFaces ? Engine_ShaderFamily.StandardCulledPath : Engine_ShaderFamily.StandardPath;
        Shader? shader = LiteFallbackShader(family) ?? SharedCulled();
        if (shader is null)
        {
            return null;
        }
        ImageTexture? albedo = null;
        // Fable 3 draws an empty texture set as its 16x16 white default (sub_82CBA610: sub_82182B70
        // at 0x82CBA6D0) times the material's DiffuseColour (material+160, (1,1,1,1) from the
        // constructor sub_82CEE200 0x82CEE298), so a stated colour is the surface's colour: bound
        // here as a one-texel albedo, the white default already being the sampler's own.
        if (record?.StatedProperties is { } stated && stated.TryGetValue("DiffuseColour", out float[]? colour) &&
            colour.Length >= 3)
        {
            albedo = StatedColourTexture(colour);
        }
        var material = Refii.Core.Engine_ServerSyncs.RunGpuUpload(4096L, () =>
        {
            var mat = new ShaderMaterial { Shader = shader };
            if (albedo is not null)
            {
                mat.SetShaderParameter("albedo_tex", albedo);
            }
            return mat;
        });
        return material;
    }

    private static readonly Dictionary<(float, float, float, float), ImageTexture> _statedColours = new();

    /// <summary>A one-texel texture of a stated colour, shared per colour; null for white.</summary>
    private static ImageTexture? StatedColourTexture(float[] colour)
    {
        float a = colour.Length > 3 ? colour[3] : 1f;
        var key = (colour[0], colour[1], colour[2], a);
        if (key == (1f, 1f, 1f, 1f))
        {
            return null;
        }
        lock (_statedColours)
        {
            if (_statedColours.TryGetValue(key, out ImageTexture? known))
            {
                return known;
            }
        }
        var image = Image.CreateEmpty(1, 1, false, Image.Format.Rgbaf);
        image.SetPixel(0, 0, new Color(key.Item1, key.Item2, key.Item3, a));
        ImageTexture created = Refii.Textures.Engine_TextureUpload.CreateTexture(image);
        lock (_statedColours)
        {
            if (_statedColours.TryGetValue(key, out ImageTexture? known))
            {
                return known;
            }
            _statedColours[key] = created;
            return created;
        }
    }

    /// <summary>
    /// Preloads and caches all shared shader resources on the main thread.
    /// This prevents background prefetch worker threads from invoking GD.Load and waiting
    /// on the main thread's MessageQueue while the main thread is busy with scene opening.
    /// </summary>
    public static void WarmShaders()
    {
        LoadShared(Engine_ShaderFamily.StandardPath);
        LoadShared(Engine_ShaderFamily.StandardCulledPath);
        LoadShared(Engine_ShaderFamily.StandardWindPath);
        LoadShared(Engine_ShaderFamily.StandardTreePath);
        LoadShared(Engine_ShaderFamily.StandardTranslucentPath);
        LoadShared(Engine_ShaderFamily.TreeCanopyPath);
        WarmLiteShaders();
    }

    /// <summary>
    /// The one shader every surface shares, loaded once.
    ///
    /// LOCKED because a level change has two threads building materials at once, and `??=` is
    /// a read, a branch and a write rather than one operation. Losing the race would not throw
    /// — it would leave two Shader instances alive with materials bound to each, and "the same
    /// shader" would stop being true of the scene without anything reporting a fault.
    /// </summary>
    private static Shader? Shared() => Shared(ShaderFamily.Standard);

    /// <summary>
    /// The two-sided shader that bends in the wind, and the ONLY shader that reads TIME: Godot marks
    /// a light's shadow dirty after every render while an "animated" caster (TIME in a vertex
    /// function that writes VERTEX) is in its pass, so only a surface that sways may carry it.
    /// </summary>
    private static Shader? SharedWind()
    {
        return LoadShared(Engine_ShaderFamily.StandardWindPath);
    }

    /// <summary>The two-sided shader moved by the title's tree wind (fable_tree_sway.gdshaderinc).</summary>
    private static Shader? SharedTree()
    {
        return LoadShared(Engine_ShaderFamily.StandardTreePath);
    }

    /// <summary>The standard shader with back faces culled, or the two-sided one when `cull_back_faces` is off.</summary>
    private static Shader? SharedCulled()
    {
        if (!Engine_ShaderFamily.CullBackFaces)
        {
            return Shared();
        }

        return LoadShared(Engine_ShaderFamily.StandardCulledPath);
    }

    /// <summary>
    /// The shader for one FAMILY, loaded once each.
    /// </summary>
    /// <remarks>
    /// ONE INSTANCE PER FAMILY, not one per material — the same reason the single shader was
    /// cached before. The dictionary replaces the old single field because the family is now
    /// chosen by the draw path, and a level change builds several families concurrently on two
    /// worker threads.
    /// </remarks>
    private static Shader? Shared(ShaderFamily family)
    {
        return LoadShared(Engine_ShaderFamily.PathFor(family));
    }

    /// <summary>
    /// One shared shader per path. THE LOAD RUNS OUTSIDE THE LOCK: a shader resource load on a
    /// worker can wait on the main thread, and the main thread building an entity reaches this
    /// lock too — holding it across `GD.Load` deadlocked every run that built a level and its
    /// entities at once (the level viewer hung after the build).
    ///
    /// BUT TWO WORKERS MAY NOT RACE ONE FIRST LOAD: both creating the managed wrapper of the
    /// same new resource is a FATAL in the interop (`gchandle ... != p_old_gchandle`), which killed
    /// the level viewer as the prefetch workers met the first translucent material together.
    /// Workers serialise on <see cref="WorkerLoadLock"/>; the main thread never takes it, so the
    /// deadlock above cannot come back through it.
    /// </summary>
    private static Shader? LoadShared(string path)
    {
        lock (ShaderLock)
        {
            if (_shaders.TryGetValue(path, out Shader? cached))
            {
                return cached;
            }
        }

        Shader? loaded;
        if (Godot.OS.GetThreadCallerId() == Godot.OS.GetMainThreadId())
        {
            loaded = Engine_ShaderSpecialisation.LoadFile(path);
        }
        else
        {
            lock (WorkerLoadLock)
            {
                lock (ShaderLock)
                {
                    if (_shaders.TryGetValue(path, out Shader? first))
                    {
                        return first;
                    }
                }
                loaded = Engine_ShaderSpecialisation.LoadFile(path);
                lock (ShaderLock)
                {
                    _shaders.TryAdd(path, loaded);
                    return _shaders[path];
                }
            }
        }
        lock (ShaderLock)
        {
            if (_shaders.TryGetValue(path, out Shader? raced))
            {
                return raced;
            }

            _shaders[path] = loaded;
            return loaded;
        }
    }

    private static readonly Dictionary<string, Shader?> _shaders = new();

    private static readonly object ShaderLock = new();

    private static readonly object WorkerLoadLock = new();


    private static int _built;
    private static int _withNormal;
    private static int _withSpecular;
    private static int _withEmission;
    private static int _withEnvmap;
    private static int _withOrm;
    private static int _lastReported = -1;

    /// <summary>
    /// One line saying what the region's materials actually bound.
    /// </summary>
    /// <remarks>
    /// THIS EXISTS BECAUSE A RUN COULD NOT ANSWER "ARE THE REFLECTIONS WORKING?". Only 526 of the
    /// 8,931 shipped material records name an environment map — about six per cent — so walking
    /// around a region and seeing none is exactly what you would expect whether the feature works
    /// or is completely broken. Nothing was logged when one bound, so the log could not tell those
    /// two apart either, and the question came back as "look at it again".
    ///
    /// A count settles it: zero envmaps in a region means there were none to draw, and a non-zero
    /// count means the binding ran and the question is about appearance rather than plumbing.
    /// </remarks>
    public static void ReportIfChanged()
    {
        int built = Volatile.Read(ref _built);
        if (built == _lastReported)
        {
            return;
        }

        _lastReported = built;
        GD.Print(
            $"[material] {built:N0} built: {Volatile.Read(ref _withNormal):N0} normal, " +
            $"{Volatile.Read(ref _withSpecular):N0} specular, " +
            $"{Volatile.Read(ref _withEmission):N0} self-illum, " +
            $"{Volatile.Read(ref _withEnvmap):N0} envmap, " +
            $"{Volatile.Read(ref _withOrm):N0} orm; " +
            $"shared {Interlocked.Read(ref SharedHits):N0} hits / " +
            $"{Interlocked.Read(ref SharedBuilt):N0} distinct");
        // The classic path returns before the counters above, so under the classic model they
        // read zero; its own tally is the one that says what reached the screen.
        (int classic, int fellBack) = Engine_ClassicMaterial.Counts;
        if (classic != 0 || fellBack != 0)
        {
            GD.Print($"[classic] {classic:N0} materials on the title's shaders, {fellBack:N0} fell back to the modern material");
        }
    }

    /// <summary>Finished materials by stated identity; a region-scoped cache, never an owner.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Material>
        _shared = new();

    /// <summary>How many surfaces took a shared material, and how many had to build one.</summary>
    public static long SharedHits;

    public static long SharedBuilt;

    /// <summary>Drops the shared materials. Call from <c>Engine_LevelCaches.ReleaseRegion</c>.</summary>
    public static void ClearSharedCache()
    {
        _shared.Clear();
    }

    /// <summary>
    /// Retains shared materials across desktop region swaps up to <paramref name="maxEntries"/>
    /// (falling back to a full clear under Classic or non-positive limits).
    /// </summary>
    public static void TrimSharedCache(int maxEntries = 2048)
    {
        if (Engine_ClassicMaterial.Active || maxEntries <= 0)
        {
            ClearSharedCache();
            return;
        }

        int excess = _shared.Count - maxEntries;
        if (excess <= 0)
        {
            return;
        }

        foreach (string key in _shared.Keys)
        {
            if (_shared.TryRemove(key, out _))
            {
                if (--excess <= 0)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Builds the cache key for <see cref="TryCreateShared"/>, including quantized
    /// <see cref="WindDirection"/> for materials that respond to regional wind.
    /// </summary>
    internal static string BuildSharedKey(
        MdlMaterial material, Refii.Models.MdlInfo info, bool authoredNormals, bool tangentBasis = true)
    {
        bool usesWind = info.SwaysInWind || info.MovementMode != 0;
        string windPart = usesWind
            ? string.Concat(
                "|",
                MathF.Round(WindDirection.X, 3).ToString("R"),
                ",",
                MathF.Round(WindDirection.Y, 3).ToString("R"))
            : string.Empty;

        return string.Concat(
            material.Diffuse, "|", material.Specular, "|", material.Normal, "|",
            material.EnvironmentMap, "|", material.SelfIllumination, "|",
            material.SpecularPowerMin.ToString("R"), "|",
            material.SpecularPowerMax.ToString("R"), "|",
            material.SelfIlluminationScale.ToString("R"), "|",
            info.MovementMode.ToString(), "|",
            info.WindAmplitude.ToString("R"), "|",
            IsVegetationDrawPath(info) ? "v" : "-",
            authoredNormals ? "n" : "-",
            tangentBasis ? "t" : "-",
            Refii.AndroidRuntime.AndroidTranslucentPass.KeyPart(info),
            windPart);
    }

    /// <summary>
    /// The finished material for one surface — the record's textures plus the wind and vegetation
    /// the MESH states — shared by every surface that states the same thing.
    /// </summary>
    /// <remarks>
    /// One material per surface is one weak handle per surface: every ShaderMaterial is a
    /// RefCounted registered in Godot's DisposablesTracker, and the GC processes every weak handle
    /// on every collection whatever the generation.
    ///
    /// The identity is the record plus what the mesh states. The record names the samplers; the
    /// mesh header states the movement mode, the wind amplitude and the vegetation draw path, and
    /// all three are written onto the material AFTER it is built (<see cref="ApplyWind"/>,
    /// <see cref="ApplyVegetation"/>). Two surfaces sharing a record but not a wind mode are not
    /// the same material, so the key carries both.
    ///
    /// Not shared when a sampler was overridden — the morphed hero maps belong to one body, and a
    /// path key would hand the next character the hero's skin.
    /// </remarks>
    public static Material? TryCreateShared(
        MdlMaterial? material, Refii.Models.MdlInfo info, bool authoredNormals,
        bool tangentBasis = true)
    {
        if (material is null)
        {
            return null;
        }

        // Built once per surface at BUILD time, not per frame; the string is the cheap part of a
        // call that otherwise decodes textures.
        string key = BuildSharedKey(material, info, authoredNormals, tangentBasis);

        if (_shared.TryGetValue(key, out Material? hit))
        {
            Interlocked.Increment(ref SharedHits);
            return hit;
        }

        Material? built = TryCreateCore(material, out bool overridden, info,
            applySharedPostSteps: true, authoredNormals: authoredNormals, tangentBasis: tangentBasis);
        if (built is null)
        {
            return null;
        }

        Interlocked.Increment(ref SharedBuilt);

        if (overridden)
        {
            return built;
        }

        // A race costs one duplicate, not a wrong material: two workers can miss the same key,
        // both build the same thing, and whichever lands first is the one everyone takes.
        return _shared.GetOrAdd(key, built);
    }

    public static Material? TryCreate(MdlMaterial? material) => TryCreate(material, out _);

    /// <summary>Materials built whose named albedo failed to load; run total.</summary>
    public static long MaterialsMissingAlbedo;

    /// <param name="overridden">
    /// A sampler resolved through <see cref="TextureOverride"/> — the hero's morphed maps, which
    /// belong to one body. Such a material is not shareable by record key.
    /// </param>
    /// <param name="info">The model the record belongs to, when known: the classic material
    /// chooses its ambient permutation from the model's lightmap and transfer data.</param>
    public static Material? TryCreate(MdlMaterial? material, out bool overridden, MdlInfo? info = null) =>
        TryCreateCore(material, out overridden, info, applySharedPostSteps: false, authoredNormals: false, tangentBasis: true);

    private static Material? TryCreateCore(
        MdlMaterial? material,
        out bool overridden,
        MdlInfo? info,
        bool applySharedPostSteps,
        bool authoredNormals,
        bool tangentBasis)
    {
        overridden = false;
        if (material is null || Refii.Core.Engine_RdHealth.IsDeviceLost)
        {
            return null;
        }

        PrepareSlot(material.Diffuse, TexSemantic.Albedo, out Texture2D? albedoOverride,
            out Engine_TexLibrary.PreparedTexture? albedoPrep, out bool albedoHasAlpha, out bool albedoOverridden);
        PrepareSlot(material.Normal, TexSemantic.Normal, out Texture2D? normalOverride,
            out Engine_TexLibrary.PreparedTexture? normalPrep, out _, out bool normalOverridden);
        PrepareSlot(material.SelfIllumination, TexSemantic.SelfIllumination, out Texture2D? emissiveOverride,
            out Engine_TexLibrary.PreparedTexture? emissivePrep, out _, out bool emissiveOverridden);
        PrepareSlot(material.Specular, TexSemantic.Specular, out Texture2D? specularOverride,
            out Engine_TexLibrary.PreparedTexture? specularPrep, out _, out bool specularOverridden);

        Engine_TexLibrary.PreparedOrm? ormPrep = null;
        try
        {
            overridden =
                albedoOverridden || normalOverridden || emissiveOverridden || specularOverridden;

            bool hasAlbedo = albedoOverride is not null || albedoPrep?.Ready is not null || (albedoPrep?.EstimatedBytes ?? 0) > 0;
            bool hasNormal = normalOverride is not null || normalPrep?.Ready is not null || (normalPrep?.EstimatedBytes ?? 0) > 0;
            bool hasEmissive = emissiveOverride is not null || emissivePrep?.Ready is not null || (emissivePrep?.EstimatedBytes ?? 0) > 0;
            bool hasSpecular = specularOverride is not null || specularPrep?.Ready is not null || (specularPrep?.EstimatedBytes ?? 0) > 0;

            // A named albedo that did not load makes an incomplete material, and it is not shared:
            // shared, every surface of that record would keep the shader's default albedo (white)
            // for the session. Unshared, the next surface that asks builds it again and gets the
            // texture once it loads.
            if (!hasAlbedo && material.Diffuse.Length != 0)
            {
                Interlocked.Increment(ref MaterialsMissingAlbedo);
                overridden = true;
            }

            // The environment cube. 526 of the 8,931 shipped material records name one and none of
            // them were rendering it, because the decoder could not read a cube .tex at all until
            // Engine_TexCube landed.
            Cubemap? envmap = Engine_TexLibrary.GetCubemap(
                material.EnvironmentMap, Engine_BnkMeshLoader.ReadBytes);

            // A record that names no texture at all is still drawn by the title, on its loader's
            // default textures (Engine_ClassicMaterial.DefaultDiffuse); only the modern look has
            // nothing to build from.
            if (!hasAlbedo && !hasNormal && !hasEmissive && !hasSpecular &&
                envmap is null && !Engine_ClassicMaterial.Active)
            {
                return null;
            }

            // THE CLASSIC RENDERER: commit prepared textures once on main thread, then build classic material.
            if (Engine_ClassicMaterial.Active)
            {
                long classicBytes =
                    (albedoPrep?.EstimatedBytes ?? 0) +
                    (normalPrep?.EstimatedBytes ?? 0) +
                    (emissivePrep?.EstimatedBytes ?? 0) +
                    (specularPrep?.EstimatedBytes ?? 0);
                Texture2D? cAlbedo = albedoOverride ?? albedoPrep?.Ready;
                Texture2D? cNormal = normalOverride ?? normalPrep?.Ready;
                Texture2D? cEmissive = emissiveOverride ?? emissivePrep?.Ready;
                Texture2D? cSpecular = specularOverride ?? specularPrep?.Ready;
                if (classicBytes > 0)
                {
                    Refii.Core.Engine_ServerSyncs.RunGpuUpload(classicBytes, () =>
                    {
                        cAlbedo ??= albedoPrep?.CommitOnMainThread();
                        cNormal ??= normalPrep?.CommitOnMainThread();
                        cEmissive ??= emissivePrep?.CommitOnMainThread();
                        cSpecular ??= specularPrep?.CommitOnMainThread();
                    });
                }

                Material? classic = Engine_ClassicMaterial.TryCreate(
                    material, info, albedoHasAlpha, cAlbedo, cNormal, cEmissive, cSpecular, envmap);
                if (classic is not null)
                {
                    Interlocked.Increment(ref _built);
                    return classic;
                }
            }

            Shader? shader = Shared();
            if (shader is null)
            {
                GD.Print($"[material] retail shader missing at {ShaderPath}");
                return null;
            }

            Interlocked.Increment(ref _built);

            bool translucent = hasAlbedo &&
                Refii.Textures.Engine_TexLibrary.IsTranslucent(material.Diffuse, Engine_BnkMeshLoader.ReadTextureHeader);
            bool onePass = Refii.AndroidRuntime.AndroidTranslucentPass.SinglePass(translucent, info);

            bool vegetation = info is not null && IsVegetationDrawPath(info);
            int diffuseFlags = hasAlbedo && !vegetation
                ? Refii.Textures.Engine_TexLibrary.HeaderFlags(material.Diffuse, Engine_BnkMeshLoader.ReadTextureHeader)
                : 0;
            bool cutout = vegetation ? albedoHasAlpha : (diffuseFlags & 0x20) != 0;
            Shader? chosen = cutout || translucent ? shader : SharedCulled();
            Shader? blended = translucent && !onePass ? LoadShared(Engine_ShaderFamily.StandardTranslucentPath) : null;

            bool needsOverriddenOrm = false;
            if (hasSpecular || hasNormal || !string.IsNullOrWhiteSpace(material.Diffuse))
            {
                if (specularOverridden || normalOverridden)
                {
                    needsOverriddenOrm = true;
                }
                else
                {
                    ormPrep = Engine_TexLibrary.PrepareOrm(
                        material.Diffuse,
                        material.Specular,
                        material.Normal,
                        material.SpecularPowerMin,
                        material.SpecularPowerMax,
                        Engine_BnkMeshLoader.ReadBytes);
                }
            }

            bool rangedExponent = !string.IsNullOrEmpty(material.Specular) &&
                (Refii.Textures.Engine_TexLibrary.HeaderFlags(material.Specular, Engine_BnkMeshLoader.ReadTextureHeader) & 0x80) != 0;

            Shader? twoSided = applySharedPostSteps && info is not null &&
                (info.SwaysInWind || info.MovementMode != 0 || IsVegetationDrawPath(info))
                    ? shader
                    : null;
            Shader? treeShader = applySharedPostSteps && info is { MovementMode: 2 } ? SharedTree() : null;
            Shader? windShader = applySharedPostSteps && info is { SwaysInWind: true, MovementMode: 1 } ? SharedWind() : null;
            if (Engine_ShaderVariant.Lite)
            {
                WarmLiteShaders();
            }

            long totalUploadBytes =
                4096L +
                (albedoPrep?.EstimatedBytes ?? 0) +
                (normalPrep?.EstimatedBytes ?? 0) +
                (emissivePrep?.EstimatedBytes ?? 0) +
                (specularPrep?.EstimatedBytes ?? 0) +
                (ormPrep?.EstimatedBytes ?? 0);

            Texture2D? albedo = null;
            Texture2D? normal = null;
            Texture2D? emissive = null;
            Texture2D? specular = null;

            ShaderMaterial result = Refii.Core.Engine_ServerSyncs.RunGpuUpload(totalUploadBytes, () =>
            {
                albedo = albedoOverride ?? albedoPrep?.CommitOnMainThread();
                normal = normalOverride ?? normalPrep?.CommitOnMainThread();
                emissive = emissiveOverride ?? emissivePrep?.CommitOnMainThread();
                specular = specularOverride ?? specularPrep?.CommitOnMainThread();

                ImageTexture? orm = null;
                bool ormHeight = false;
                bool isPbr = false;
                if (needsOverriddenOrm)
                {
                    orm = Engine_TexLibrary.BuildOrmFromTextures(
                        specular, normal,
                        material.SpecularPowerMin, material.SpecularPowerMax, out ormHeight);
                }
                else if (ormPrep is not null)
                {
                    orm = ormPrep.CommitOnMainThread();
                    ormHeight = ormPrep.HasHeight;
                    isPbr = ormPrep.IsAuthored;
                }

                var mat = new ShaderMaterial { Shader = chosen ?? shader };
                if (cutout || translucent || twoSided is not null)
                {
                    mat.RenderPriority = CutoutRenderPriority;
                }
                mat.SetMeta(TextureNamesMeta, new[]
                {
                    material.Diffuse, material.Normal, material.Specular, material.SelfIllumination,
                });
                float specMin = rangedExponent ? material.SpecularPowerMin : FixedSpecularPower;
                float specMax = rangedExponent ? material.SpecularPowerMax : FixedSpecularPower;
                mat.SetMeta(OrmParamsMeta, new Vector3(orm is not null ? 1f : 0f, specMin, specMax));
                if (albedo is not null)
                {
                    mat.SetShaderParameter("albedo_tex", albedo);
                    if (cutout || translucent)
                    {
                        mat.SetShaderParameter("alpha_scissor_on", true);
                    }
                    if (translucent)
                    {
                        mat.SetShaderParameter("alpha_scissor_threshold", onePass ? CutoutAlpha : OpaquePassAlpha);
                    }
                    else if (cutout)
                    {
                        mat.SetShaderParameter("alpha_scissor_threshold",
                            vegetation
                                ? VegetationCutoutAlpha
                                : (diffuseFlags & 0xA0) == 0x20 ? CutoutAlpha : OpaquePassAlpha);
                    }
                }

                if (normal is not null)
                {
                    mat.SetShaderParameter("normal_tex", normal);
                    mat.SetShaderParameter("has_normal", true);
                    Interlocked.Increment(ref _withNormal);
                }

                if (emissive is not null)
                {
                    mat.SetShaderParameter("emission_tex", emissive);
                    mat.SetShaderParameter("has_emission", true);
                    Interlocked.Increment(ref _withEmission);
                }

                if (specular is not null)
                {
                    mat.SetShaderParameter("specular_tex", specular);
                    mat.SetShaderParameter("has_specular", true);
                    Interlocked.Increment(ref _withSpecular);
                }

                if (envmap is not null)
                {
                    mat.SetShaderParameter("envmap_tex", envmap);
                    mat.SetShaderParameter("has_envmap", true);
                    Interlocked.Increment(ref _withEnvmap);
                }

                // The lite variant's facts, before the duplicate below so the blended pass carries them too.
                CaptureLiteFacts(mat, specular, envmap, normal, emissive);

                if (orm is not null)
                {
                    mat.SetShaderParameter("orm_tex", orm);
                    mat.SetShaderParameter("has_orm", true);
                    mat.SetShaderParameter("is_pbr", isPbr);
                    SetFacts(mat, isPbr ? FactOrm | FactPbr : FactOrm, true);

                    // Whether a height was actually SOLVED into the alpha. The heightless sentinel
                    // is opaque white — half a texel of permanent positive relief if the shader ever
                    // read it — so parallax stays off unless this says the channel is real.
                    mat.SetShaderParameter("has_height", ormHeight);
                    Interlocked.Increment(ref _withOrm);
                }
                else
                {
                    mat.SetShaderParameter("is_pbr", false);
                }

                mat.SetShaderParameter("spec_power_min", specMin);
                mat.SetShaderParameter("spec_power_max", specMax);
                mat.SetShaderParameter("self_illum_scale", material.SelfIlluminationScale);

                if (translucent && blended is not null)
                {
                    var second = Refii.Models.Engine_MaterialCopy.Shallow(mat);
                    second.Shader = blended;
                    second.RenderPriority = 0;
                    second.SetShaderParameter("alpha_scissor_on", false);
                    mat.NextPass = second;
                }

                if (applySharedPostSteps && info is not null)
                {
                    ApplyWind(mat, info, windShader);
                    ApplyVegetation(mat, info, authoredNormals);

                    if (twoSided is not null)
                    {
                        mat.Shader = info.MovementMode == 2
                            ? treeShader ?? twoSided
                            : mat.HasMeta(WindMetaName) ? windShader ?? twoSided : twoSided;
                    }

                    ApplyShaderVariant(mat, info);

                    if (!tangentBasis)
                    {
                        mat.SetShaderParameter("has_normal", false);
                        SetFacts(mat, FactNormal, false);
                    }

                    ApplySpecialisation(mat);
                }

                return mat;
            });

            RequestEnhanced(material.Diffuse, TexSemantic.Albedo, albedo);
            RequestEnhanced(material.Normal, TexSemantic.Normal, normal);
            RequestEnhanced(material.SelfIllumination, TexSemantic.SelfIllumination, emissive);
            RequestEnhanced(material.Specular, TexSemantic.Specular, specular);

            return result;
        }
        finally
        {
            albedoPrep?.Dispose();
            normalPrep?.Dispose();
            emissivePrep?.Dispose();
            specularPrep?.Dispose();
            ormPrep?.Dispose();
        }
    }

    /// <summary>
    /// Draws <c>depth_prepass_alpha</c> cutout surfaces ahead of semi-transparents at priority 0.
    /// </summary>
    public const int CutoutRenderPriority = -16;

    /// <summary>The translucent surface's opaque pass keeps alpha GREATER 254 (ref 254 of 255).</summary>
    private const float OpaquePassAlpha = 254.5f / 255.0f;

    /// <summary>A cutout-only texture's ALPHAREF 128, GREATER.</summary>
    private const float CutoutAlpha = 128.5f / 255.0f;

    /// <summary>Default vegetation alpha scissor threshold (matches the shader uniform default).</summary>
    private const float VegetationCutoutAlpha = 0.25f;

    /// <summary>The SP1 pair's exponent: saturate(V.R) squared twice.</summary>
    private const float FixedSpecularPower = 4.0f;

    /// <summary>
    /// The texture names a material was built from, in <see cref="TextureParameters"/> order;
    /// the guest replaces a texture by its name's hash in whichever slot holds it.
    /// </summary>
    public const string TextureNamesMeta = "mdl_texture_names";

    /// <summary>
    /// Cached ORM state (<c>(hasOrm ? 1 : 0, specPowerMin, specPowerMax)</c>) stamped on built
    /// materials so <c>Engine_HeroTextureMorph</c> can prepare morphed ORMs on worker threads
    /// without stalling on main-thread shader parameter reads.
    /// </summary>
    public const string OrmParamsMeta = "mdl_orm_params";

    public static readonly string[] TextureParameters =
    {
        "albedo_tex", "normal_tex", "specular_tex", "emission_tex",
    };

    /// <summary>
    /// Per thread: a hero's composite (Engine_HeroInstance.Rebuild) builds synchronously on the
    /// thread that sets it, and a worker's load meanwhile must not take the hero's textures.
    /// </summary>
    public static Func<string, TexSemantic, Texture2D?>? TextureOverride
    {
        get => _textureOverride;
        set => _textureOverride = value;
    }

    [ThreadStatic]
    private static Func<string, TexSemantic, Texture2D?>? _textureOverride;

    private static void PrepareSlot(
        string name,
        TexSemantic semantic,
        out Texture2D? overrideTex,
        out Engine_TexLibrary.PreparedTexture? prepared,
        out bool hasAlpha,
        out bool overridden)
    {
        overrideTex = null;
        prepared = null;
        hasAlpha = false;
        overridden = false;

        if (name.Length == 0)
        {
            return;
        }

        if (TextureOverride is not null &&
            TextureOverride(name, semantic) is Texture2D replacement)
        {
            overridden = true;
            overrideTex = replacement;
            return;
        }

        prepared = Engine_TexLibrary.PrepareGet(name, semantic, Engine_BnkMeshLoader.ReadBytes);
        hasAlpha = prepared.HasAlpha;
    }

    private static Texture2D? TryLoad(string name, TexSemantic semantic, out bool hasAlpha) =>
        TryLoad(name, semantic, out hasAlpha, out _);

    private static Texture2D? TryLoad(
        string name, TexSemantic semantic, out bool hasAlpha, out bool overridden)
    {
        hasAlpha = false;
        overridden = false;

        if (name.Length == 0)
        {
            return null;
        }

        if (TextureOverride is not null &&
            TextureOverride(name, semantic) is Texture2D replacement)
        {
            overridden = true;
            return replacement;
        }

        return Engine_TexLibrary.Get(
            name, semantic, Engine_BnkMeshLoader.ReadBytes, out hasAlpha);
    }

    /// <summary>
    /// Queues one sampler's enhancement.
    /// </summary>
    /// <remarks>
    /// Skipped when the slot resolved to nothing: a material that names no normal map has
    /// <c>has_normal</c> false, and filling that sampler later would leave the flag's meaning
    /// untrue without turning the flag on.
    /// </remarks>
    private static void RequestEnhanced(string name, TexSemantic semantic, Texture2D? current)
    {
        if (current is null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Engine_TexLibrary.RequestEnhanced(
            name, semantic, TexPriority.Scene, Engine_BnkMeshLoader.ReadBytes);
    }
}
