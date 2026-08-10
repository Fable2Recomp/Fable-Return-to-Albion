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

    public static void ClearCache() => TextureCache.Clear();


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
        _shader ??= GD.Load<Shader>(ShaderPath);
        return _shader is null ? null : new ShaderMaterial { Shader = _shader };
    }

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

        _shader ??= GD.Load<Shader>(ShaderPath);
        if (_shader is null)
        {
            GD.Print($"[material] retail shader missing at {ShaderPath}");
            return null;
        }

        var result = new ShaderMaterial { Shader = _shader };
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

        string virtualPath = name.Replace('\\', '/').TrimStart('/');
        if (TextureCache.TryGetValue(
                virtualPath, out (Texture2D? Texture, bool HasAlpha) cached))
        {
            hasAlpha = cached.HasAlpha;
            return cached.Texture;
        }

        Texture2D? texture = Decode(virtualPath, out hasAlpha);
        TextureCache[virtualPath] = (texture, hasAlpha);
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
