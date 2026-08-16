using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using Refii.Textures;

namespace Refii.Models;

public static class Engine_MdlMaterial
{
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
        shader.SetShaderParameter("wind_speed", Mathf.Clamp(info.WindSpeed, 0.1f, 8.0f));
        shader.SetShaderParameter("wind_direction", WindDirection);
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
            $"{Volatile.Read(ref _withOrm):N0} orm");
    }

    public static Material? TryCreate(MdlMaterial? material)
    {
        if (material is null)
        {
            return null;
        }

        Texture2D? albedo = TryLoad(material.Diffuse, TexSemantic.Albedo, out bool albedoHasAlpha);
        Texture2D? normal = TryLoad(material.Normal, TexSemantic.Normal, out _);
        Texture2D? emissive =
            TryLoad(material.SelfIllumination, TexSemantic.SelfIllumination, out _);

        Texture2D? specular = TryLoad(material.Specular, TexSemantic.Specular, out _);

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

        var result = new ShaderMaterial { Shader = shader };
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

        // The packed ORM, when the player has asked for it. Derived from the SPECULAR map and the
        // material's own exponent range, so it is only meaningful where there is a specular map to
        // derive it from.
        if (specular is not null)
        {
            ImageTexture? orm = Engine_TexLibrary.GetOrm(
                material.Specular,
                material.Normal,
                material.SpecularPowerMin,
                material.SpecularPowerMax,
                Engine_BnkMeshLoader.ReadBytes);
            if (orm is not null)
            {
                result.SetShaderParameter("orm_tex", orm);
                result.SetShaderParameter("has_orm", true);
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

    private static Texture2D? TryLoad(string name, TexSemantic semantic, out bool hasAlpha) =>
        Engine_TexLibrary.Get(name, semantic, Engine_BnkMeshLoader.ReadBytes, out hasAlpha);

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
