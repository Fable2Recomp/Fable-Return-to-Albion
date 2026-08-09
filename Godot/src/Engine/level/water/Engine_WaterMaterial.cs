using System;
using Godot;
using Refii.Textures;

namespace Refii.Level;

public static class Engine_WaterMaterial
{
    public const string ShaderPath = "res://src/Engine/level/water/water.gdshader";

    public delegate byte[]? ReadAsset(string virtualPath);

    public delegate string? FindByName(string fileName);

    public static ShaderMaterial? Build(
        WaterBody body, ReadAsset read, FindByName? findByName, out string error)
    {
        error = string.Empty;

        var shader = GD.Load<Shader>(ShaderPath);
        if (shader is null)
        {
            error = $"water shader missing at {ShaderPath}";
            return null;
        }

        var material = new ShaderMaterial { Shader = shader };

        Texture2D? normalMap = LoadTexture(body.NormalMapPath, read, findByName);
        if (normalMap is not null)
        {
            material.SetShaderParameter("normal_map", normalMap);
        }

        material.SetShaderParameter("w_bias", new Vector4(
            body.Param(WaterParam.FresnelBias, 0.2f),
            body.Param(WaterParam.ReflectionBias, 0f),
            body.Param(WaterParam.NormalScale, 0.05f),
            0.78f));

        material.SetShaderParameter("w_speed", new Vector4(
            body.Param(WaterParam.NormalSpeed0X, 0.052f),
            body.Param(WaterParam.NormalSpeed0Y, 0.011f),
            body.Param(WaterParam.NormalSpeed1X, -0.019f),
            body.Param(WaterParam.NormalSpeed1Y, 0.019f)));

        material.SetShaderParameter("w_scale", new Vector4(
            body.Param(WaterParam.NormalScale0X, 0.188f),
            body.Param(WaterParam.NormalScale0Y, 0.188f),
            body.Param(WaterParam.NormalScale1X, 0.220f),
            body.Param(WaterParam.NormalScale1Y, 0.220f)));

        float surfaceR = body.Param(WaterParam.SurfaceR, 0f);
        float surfaceG = body.Param(WaterParam.SurfaceG, 0f);
        float surfaceB = body.Param(WaterParam.SurfaceB, 0f);
        float deepR = body.Param(WaterParam.DeepR, 0f);
        float deepG = body.Param(WaterParam.DeepG, 0f);
        float deepB = body.Param(WaterParam.DeepB, 0f);

        if (surfaceR + surfaceG + surfaceB + deepR + deepG + deepB <= 0.0005f)
        {
            surfaceR = 0.28f;
            surfaceG = 0.42f;
            surfaceB = 0.44f;
            deepR = 0.04f;
            deepG = 0.11f;
            deepB = 0.15f;
        }

        material.SetShaderParameter("w_surface", new Vector4(
            surfaceR, surfaceG, surfaceB,
            body.Param(WaterParam.DiffuseAbsorption, 0.1f)));

        material.SetShaderParameter("w_deep", new Vector4(
            deepR, deepG, deepB,
            body.Param(WaterParam.ReflectionStrength, 0.75f)));

        material.SetShaderParameter("w_reflp", new Vector4(
            body.Param(WaterParam.ReflectionScaleX, 2f),
            body.Param(WaterParam.RefractionScaleX, 2f),
            body.Param(WaterParam.GlitterBend, 0.3f),
            body.Param(WaterParam.GlitterStrength, 5f)));

        material.SetShaderParameter("w_glit", new Vector4(
            body.Param(WaterParam.GlitterPower, 128f), 0f, 0f, 0f));

        var sun = new Vector3(0.3f, 0.7f, 0.5f).Normalized();
        float sunUp = Math.Clamp(sun.Y * 3f, 0f, 1f);

        material.SetShaderParameter("w_sun", new Vector4(
            1f, 0.55f + (0.42f * sunUp), 0.30f + (0.60f * sunUp), sun.Y));

        material.SetShaderParameter("w_light", new Vector4(sun.X, sun.Y, sun.Z, 0f));

        return material;
    }

    private static Texture2D? LoadTexture(string path, ReadAsset read, FindByName? findByName)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string normalized = path.Replace('\\', '/').Trim('/');
        byte[]? bytes = read(normalized) ?? read("data/" + normalized);

        if (bytes is null && findByName is not null)
        {
            int slash = normalized.LastIndexOf('/');
            string name = slash < 0 ? normalized : normalized[(slash + 1)..];
            string? found = findByName(name);
            if (found is not null)
            {
                bytes = read(found);
            }
        }

        if (bytes is null)
        {
            GD.Print($"[water] normal map not found: {normalized}");
            return null;
        }

        ImageTexture? texture = Engine_TexImage.TryCreateTexture(bytes, out string why);
        if (texture is null)
        {
            GD.Print($"[water] {normalized}: {why}");
        }

        return texture;
    }
}
