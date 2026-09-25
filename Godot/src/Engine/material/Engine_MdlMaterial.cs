using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using Refii.Textures;

namespace Refii.Models;

public static class Engine_MdlMaterial
{
    /// <summary>Set on a material that sways in the wind (`wind_amplitude` > 0): a local flag, unlike the parameter.</summary>
    public static readonly StringName WindMetaName = "sways_in_wind";

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
    public static void ApplyWind(Material material, Refii.Models.MdlInfo info)
    {
        if (material is not ShaderMaterial shader || !info.SwaysInWind)
        {
            return;
        }

        // A TREE IS LIT AS A CANOPY, whether or not it moves. `treematerial`'s canopy permutation
        // takes no `dot` against a normal at all; drawing it as a solid is what flattens it.
        if (info.MovementMode == 2)
        {
            shader.SetShaderParameter("foliage", true);
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
            if (SharedWind() is { } wind)
            {
                shader.Shader = wind;
            }
        }
        shader.SetShaderParameter("wind_speed", Mathf.Clamp(info.WindSpeed, 0.1f, 8.0f));
        shader.SetShaderParameter("wind_direction", WindDirection);
    }

    public static void ApplyVegetation(
        Material material, Refii.Models.MdlInfo info, bool authoredNormals)
    {
        if (material is not ShaderMaterial shader || !IsVegetationDrawPath(info))
        {
            return;
        }

        shader.SetShaderParameter("premultiplied_alpha", true);
        if (authoredNormals)
        {
            shader.SetShaderParameter("foliage_normals", true);
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
    public static Material? Fallback()
    {
        Shader? shader = Shared();
        return shader is null ? null : new ShaderMaterial { Shader = shader };
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
        lock (ShaderLock)
        {
            if (_shaders.TryGetValue(Engine_ShaderFamily.StandardWindPath, out Shader? cached))
            {
                return cached;
            }

            Shader? loaded = GD.Load<Shader>(Engine_ShaderFamily.StandardWindPath);
            _shaders[Engine_ShaderFamily.StandardWindPath] = loaded;
            return loaded;
        }
    }

    /// <summary>The standard shader with back faces culled, or the two-sided one when `cull_back_faces` is off.</summary>
    private static Shader? SharedCulled()
    {
        if (!Engine_ShaderFamily.CullBackFaces)
        {
            return Shared();
        }

        lock (ShaderLock)
        {
            if (_shaders.TryGetValue(Engine_ShaderFamily.StandardCulledPath, out Shader? cached))
            {
                return cached;
            }

            Shader? loaded = GD.Load<Shader>(Engine_ShaderFamily.StandardCulledPath);
            _shaders[Engine_ShaderFamily.StandardCulledPath] = loaded;
            return loaded;
        }
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
        string path = Engine_ShaderFamily.PathFor(family);

        lock (ShaderLock)
        {
            if (_shaders.TryGetValue(path, out Shader? cached))
            {
                return cached;
            }

            Shader? loaded = GD.Load<Shader>(path);
            _shaders[path] = loaded;
            return loaded;
        }
    }

    private static readonly Dictionary<string, Shader?> _shaders = new();

    private static readonly object ShaderLock = new();

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
        string key = string.Concat(
            material.Diffuse, "|", material.Specular, "|", material.Normal, "|",
            material.EnvironmentMap, "|", material.SelfIllumination, "|",
            material.SpecularPowerMin.ToString("R"), "|",
            material.SpecularPowerMax.ToString("R"), "|",
            material.SelfIlluminationScale.ToString("R"), "|",
            info.MovementMode.ToString(), "|",
            info.WindAmplitude.ToString("R"), "|",
            IsVegetationDrawPath(info) ? "v" : "-",
            authoredNormals ? "n" : "-",
            tangentBasis ? "t" : "-");

        if (_shared.TryGetValue(key, out Material? hit))
        {
            Interlocked.Increment(ref SharedHits);
            return hit;
        }

        Material? built = TryCreate(material, out bool overridden);
        if (built is null)
        {
            return null;
        }

        ApplyWind(built, info);
        ApplyVegetation(built, info, authoredNormals);

        // Vegetation keeps both sides whatever its alpha: leaves and blades are single cards. A
        // surface ApplyWind gave an amplitude keeps the wind shader; it is the one that sways.
        if (built is ShaderMaterial plant && (info.SwaysInWind || info.MovementMode != 0 || IsVegetationDrawPath(info)) &&
            Shared() is { } twoSided)
        {
            plant.Shader = plant.HasMeta(WindMetaName) ? SharedWind() ?? twoSided : twoSided;
        }

        Interlocked.Increment(ref SharedBuilt);

        // A SURFACE WHOSE TANGENTS ARE ALL ZERO TAKES NO NORMAL MAP. 1,687 shipped rigid buffers
        // bind one over a zero TEXCOORD1 (`VertexAudit`). The guest's basis is then T = B = 0 and
        // `normalize(T*nx + B*ny + N*nz)` is N exactly, so the map does nothing there; Godot
        // instead decodes a zero tangent to an arbitrary axis and bends the normal along it.
        if (!tangentBasis && built is ShaderMaterial flat)
        {
            flat.SetShaderParameter("has_normal", false);
        }

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
    public static Material? TryCreate(MdlMaterial? material, out bool overridden)
    {
        overridden = false;
        if (material is null)
        {
            return null;
        }

        Texture2D? albedo = TryLoad(
            material.Diffuse, TexSemantic.Albedo, out bool albedoHasAlpha, out bool albedoOverridden);
        Texture2D? normal = TryLoad(
            material.Normal, TexSemantic.Normal, out _, out bool normalOverridden);
        Texture2D? emissive = TryLoad(
            material.SelfIllumination, TexSemantic.SelfIllumination, out _, out bool emissiveOverridden);

        Texture2D? specular = TryLoad(
            material.Specular, TexSemantic.Specular, out _, out bool specularOverridden);

        overridden =
            albedoOverridden || normalOverridden || emissiveOverridden || specularOverridden;

        // A named albedo that did not load makes an incomplete material, and it is not shared:
        // shared, every surface of that record would keep the shader's default albedo (white)
        // for the session. Unshared, the next surface that asks builds it again and gets the
        // texture once it loads.
        if (albedo is null && material.Diffuse.Length != 0)
        {
            Interlocked.Increment(ref MaterialsMissingAlbedo);
            overridden = true;
        }

        // The environment cube. 526 of the 8,931 shipped material records name one and none of
        // them were rendering it, because the decoder could not read a cube .tex at all until
        // Engine_TexCube landed.
        Cubemap? envmap = Engine_TexLibrary.GetCubemap(
            material.EnvironmentMap, Engine_BnkMeshLoader.ReadBytes);

        if (albedo is null && normal is null && emissive is null && specular is null &&
            envmap is null)
        {
            return null;
        }

        Shader? shader = Shared();
        if (shader is null)
        {
            GD.Print($"[material] retail shader missing at {ShaderPath}");
            return null;
        }

        // Counted from a WORKER. The world build runs off the main thread and a level change runs
        // two of them at once, so these are interlocked rather than incremented.
        Interlocked.Increment(ref _built);

        // A solid surface shows one side; a card cut out by its alpha is seen from both.
        var result = new ShaderMaterial { Shader = albedoHasAlpha ? shader : SharedCulled() ?? shader };
        if (albedo is not null)
        {
            result.SetShaderParameter("albedo_tex", albedo);
            if (albedoHasAlpha)
            {
                result.SetShaderParameter("alpha_scissor_on", true);
            }
        }

        if (normal is not null)
        {
            result.SetShaderParameter("normal_tex", normal);
            result.SetShaderParameter("has_normal", true);
            Interlocked.Increment(ref _withNormal);
        }

        if (emissive is not null)
        {
            result.SetShaderParameter("emission_tex", emissive);
            result.SetShaderParameter("has_emission", true);
            Interlocked.Increment(ref _withEmission);
        }

        if (specular is not null)
        {
            result.SetShaderParameter("specular_tex", specular);
            result.SetShaderParameter("has_specular", true);
            Interlocked.Increment(ref _withSpecular);
        }

        if (envmap is not null)
        {
            result.SetShaderParameter("envmap_tex", envmap);
            result.SetShaderParameter("has_envmap", true);
            Interlocked.Increment(ref _withEnvmap);
        }

        // The packed ORM, when the player has asked for it. The specular map feeds roughness and
        // metallic; the NORMAL map feeds AO and height — so either is enough, and a material
        // naming only a normal (a third of the asset set) still gets its occlusion and parallax.
        if (specular is not null || normal is not null)
        {
            ImageTexture? orm;
            bool ormHeight;
            if (specularOverridden || normalOverridden)
            {
                // A morphed source (the hero) — the path-keyed cache would pack the UNMORPHED
                // banks' bytes and hand back roughness and AO that disagree with the maps bound
                // beside them, so the ORM is built from the very textures this material carries.
                orm = Engine_TexLibrary.BuildOrmFromTextures(
                    specular, normal,
                    material.SpecularPowerMin, material.SpecularPowerMax, out ormHeight);
            }
            else
            {
                orm = Engine_TexLibrary.GetOrm(
                    material.Specular,
                    material.Normal,
                    material.SpecularPowerMin,
                    material.SpecularPowerMax,
                    Engine_BnkMeshLoader.ReadBytes,
                    out ormHeight);
            }

            if (orm is not null)
            {
                result.SetShaderParameter("orm_tex", orm);
                result.SetShaderParameter("has_orm", true);

                // Whether a height was actually SOLVED into the alpha. The heightless sentinel
                // is opaque white — half a texel of permanent positive relief if the shader ever
                // read it — so parallax stays off unless this says the channel is real.
                result.SetShaderParameter("has_height", ormHeight);
                Interlocked.Increment(ref _withOrm);
            }
        }

        // The exponent range the material states, not a constant. It is per material rather than
        // per texture set: the guest reads one scalar block and then N texture-set records under
        // it, so every record shares the pair, and Engine_MdlEngine copies it down.
        result.SetShaderParameter("spec_power_min", material.SpecularPowerMin);
        result.SetShaderParameter("spec_power_max", material.SpecularPowerMax);
        result.SetShaderParameter("self_illum_scale", material.SelfIlluminationScale);

        // The §6 second pass, and it touches nothing built above.
        //
        // NO CALLBACK AND NO REFERENCE TO THIS MATERIAL. An enhancement replaces the image inside
        // the Texture2D the library already handed over, so this material — and every other one
        // naming the same texture — updates because it is holding that same object. The earlier
        // version of this registered four weak references to the material per surface just to be
        // able to re-assign the samplers, which a region's worth of materials made into thousands
        // of live callbacks that a level change then had to outlive.
        RequestEnhanced(material.Diffuse, TexSemantic.Albedo, albedo);
        RequestEnhanced(material.Normal, TexSemantic.Normal, normal);
        RequestEnhanced(material.SelfIllumination, TexSemantic.SelfIllumination, emissive);
        RequestEnhanced(material.Specular, TexSemantic.Specular, specular);

        return result;
    }

    public static Func<string, TexSemantic, Texture2D?>? TextureOverride { get; set; }

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
