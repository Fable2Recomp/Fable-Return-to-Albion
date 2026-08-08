using System;
using System.Collections.Generic;
using Godot;
using Refii.Textures;

namespace Refii.Models;

public static class Engine_MdlMaterial
{
    private static readonly Dictionary<string, Texture2D?> TextureCache = new();

    public static void ClearCache() => TextureCache.Clear();


    public static StandardMaterial3D? TryCreate(MdlMaterial? material)
    {
        if (material is null)
        {
            return null;
        }

        Texture2D? albedo = TryLoad(material.Diffuse);
        Texture2D? occlusion = TryLoad(material.Specular);
        Texture2D? normal = TryLoad(material.Normal);
        Texture2D? metallicRoughness = TryLoad(material.Metallic);
        Texture2D? emissive = TryLoad(material.Extra);

        if (albedo is null && occlusion is null && normal is null &&
            metallicRoughness is null && emissive is null)
        {
            return null;
        }

        var result = new StandardMaterial3D
        {
            AlbedoTexture = albedo,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        if (albedo is not null && DiffuseHasAlpha(material.Diffuse))
        {
            result.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            result.AlphaScissorThreshold = 0.5f;
        }

        if (normal is not null)
        {
            result.NormalEnabled = true;
            result.NormalTexture = normal;
        }

        if (occlusion is not null)
        {
            result.AOEnabled = true;
            result.AOTexture = occlusion;
            result.AOTextureChannel = BaseMaterial3D.TextureChannel.Red;
        }

        if (metallicRoughness is not null)
        {
            result.MetallicTexture = metallicRoughness;
            result.MetallicTextureChannel = BaseMaterial3D.TextureChannel.Blue;
            result.Metallic = 1.0f;
            result.RoughnessTexture = metallicRoughness;
            result.RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Green;
            result.Roughness = 1.0f;
        }
        else
        {
            result.Metallic = 0.0f;
            result.Roughness = 0.9f;
        }

        if (emissive is not null)
        {
            result.EmissionEnabled = true;
            result.EmissionTexture = emissive;
            result.Emission = Colors.White;
        }

        return result;
    }

    private static bool DiffuseHasAlpha(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(name.Replace('\\', '/').TrimStart('/'));
        if (bytes is null || !Engine_TexData.TryParseChain(bytes, out var mips, out _) || mips.Count == 0)
        {
            return false;
        }

        return mips[0].CompFlag is 1 or 11;
    }

    private static Texture2D? TryLoad(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string virtualPath = name.Replace('\\', '/').TrimStart('/');
        if (TextureCache.TryGetValue(virtualPath, out Texture2D? cached))
        {
            return cached;
        }

        Texture2D? texture = Decode(virtualPath);
        TextureCache[virtualPath] = texture;
        return texture;
    }

    private static Texture2D? Decode(string virtualPath)
    {
        byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(virtualPath);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return Engine_TexImage.TryCreateTexture(bytes, out _);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
