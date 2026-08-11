using System;
using System.Collections.Generic;
using Godot;
using Refii.Textures;

namespace Refii.Models;

public static class Engine_MdlMaterial
{
    public const string ShaderPath =
        "res://src/Engine/material/standard_material.gdshader";

    private static readonly Dictionary<string, (Texture2D? Texture, bool HasAlpha)>
        TextureCache = new();

    private static Shader? _shader;

    public static void ClearCache()
    {
        lock (TextureCache)
        {
            TextureCache.Clear();
        }
    }


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
    private static Shader? Shared()
    {
        lock (ShaderLock)
        {
            return _shader ??= GD.Load<Shader>(ShaderPath);
        }
    }

    private static readonly object ShaderLock = new();

    public static Material? TryCreate(MdlMaterial? material)
    {
        if (material is null)
        {
            return null;
        }

        Texture2D? albedo = TryLoad(material.Diffuse, out bool albedoHasAlpha);
        Texture2D? normal = TryLoad(material.Normal, out _);
        Texture2D? emissive = TryLoad(material.Extra, out _);

        if (albedo is null && normal is null && emissive is null)
        {
            return null;
        }

        Shader? shader = Shared();
        if (shader is null)
        {
            GD.Print($"[material] retail shader missing at {ShaderPath}");
            return null;
        }

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
        }

        if (emissive is not null)
        {
            result.SetShaderParameter("emission_tex", emissive);
            result.SetShaderParameter("has_emission", true);
        }

        return result;
    }

    private static Texture2D? TryLoad(string name, out bool hasAlpha)
    {
        hasAlpha = false;

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // LOCKED FOR THE LEVEL CHANGE. The region build runs on a worker while the region we
        // are still standing in pages its props in on the main thread, so both threads reach
        // this cache. A `Dictionary` torn by a concurrent resize does not lose an entry, it
        // corrupts its buckets and the next lookup never returns.
        //
        // Decoding stays outside the lock — it is the expensive half, and two threads racing
        // to decode the same texture costs one redundant decode and nothing else.
        string virtualPath = name.Replace('\\', '/').TrimStart('/');
        lock (TextureCache)
        {
            if (TextureCache.TryGetValue(
                    virtualPath, out (Texture2D? Texture, bool HasAlpha) cached))
            {
                hasAlpha = cached.HasAlpha;
                return cached.Texture;
            }
        }

        Texture2D? texture = Decode(virtualPath, out hasAlpha);
        lock (TextureCache)
        {
            TextureCache[virtualPath] = (texture, hasAlpha);
        }

        return texture;
    }

    private static Texture2D? Decode(string virtualPath, out bool hasAlpha)
    {
        hasAlpha = false;

        byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(virtualPath);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return Engine_TexImage.TryCreateTexture(bytes, out hasAlpha, out _);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
