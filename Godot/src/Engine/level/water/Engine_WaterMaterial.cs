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
        else
        {
            // SAY SO. Without the wave normals this shader has nothing left that varies across the
            // surface — the reflection is sampled through an unperturbed normal, the glint's
            // half-vector never moves, and the body renders as a flat sheet of sky colour. That
            // reads as "the water shader is wrong" rather than as one missing texture.
            //
            // The path being EMPTY is the case that was invisible: `LoadTexture` returns null
            // before its own "not found" print when there is nothing to look for, so a body that
            // states no normal map at all produced silence and a flat surface.
            GD.Print(
                body.NormalMapPath is { Length: > 0 } stated
                    ? $"[water] no normal map bound; stated path was '{stated}'"
                    : "[water] no normal map bound; the body states no NormalMapPath at all");
        }

        Apply(material, WaterTheme.Default);

        // THE SHORELINE. Built once here rather than per frame: the coverage is authored geometry
        // and never moves, so only the ramp that reads it moves with time of day.
        //
        // A body with no usable tiles keeps `has_water_mask` false and stays opaque, which is the
        // guest's own empty-mask behaviour — water everywhere — rather than a fade invented from
        // whatever geometry happens to be nearby.
        Engine_WaterMask.Mask? mask = Engine_WaterMask.TryBuild(body);
        if (mask is { } built)
        {
            material.SetShaderParameter("water_mask", built.Texture);
            material.SetShaderParameter("w_mask_bounds", built.Bounds);
            material.SetShaderParameter("has_water_mask", true);
        }

        // NO SUN HERE. It used to be a hardcoded (0.3, 0.7, 0.5) with a colour ramped off its own
        // Y, which fixed the glint in one corner of every lake for the whole day. The main light
        // is stated per frame and arrives as an instance parameter from
        // Engine_LevelBakedLighting.ApplyInstanceLighting, which is the same path the props' bake
        // takes and the same light Engine_LevelLighting.UpdateSun aims — see the shader for the
        // upload that proves the water draw reads that light and no other.
        return material;
    }

    public static void Apply(ShaderMaterial material, WaterTheme theme)
    {
        material.SetShaderParameter("w_bias", new Vector4(
            theme[WaterThemeParam.FresnelBias],
            theme[WaterThemeParam.ReflectionBias],
            theme[WaterThemeParam.NormalScale],
            theme[WaterThemeParam.MaxRefractionDistance]));

        material.SetShaderParameter("w_speed", new Vector4(
            theme[WaterThemeParam.NormalMapSpeed0X],
            theme[WaterThemeParam.NormalMapSpeed0Y],
            theme[WaterThemeParam.NormalMapSpeed1X],
            theme[WaterThemeParam.NormalMapSpeed1Y]));

        float normalScale0 = theme[WaterThemeParam.NormalMapScale0];
        float normalScale1 = theme[WaterThemeParam.NormalMapScale1];
        material.SetShaderParameter("w_scale", new Vector4(
            normalScale0, normalScale0, normalScale1, normalScale1));

        material.SetShaderParameter("w_surface", new Vector4(
            theme[WaterThemeParam.ShallowR],
            theme[WaterThemeParam.ShallowG],
            theme[WaterThemeParam.ShallowB],
            0f));

        material.SetShaderParameter("w_deep", new Vector4(
            theme[WaterThemeParam.DeepR],
            theme[WaterThemeParam.DeepG],
            theme[WaterThemeParam.DeepB],
            theme[WaterThemeParam.ReflectionStrength]));

        material.SetShaderParameter("w_reflp", new Vector4(
            theme[WaterThemeParam.ReflectionScale],
            theme[WaterThemeParam.RefractionScale],
            theme[WaterThemeParam.GlitterBend],
            theme[WaterThemeParam.GlitterStrength]));

        material.SetShaderParameter("w_glit", new Vector4(
            theme[WaterThemeParam.GlitterPower],
            theme[WaterThemeParam.ShadowScaleBias],
            0f, 0f));

        material.SetShaderParameter("w_edge", Engine_WaterTheme.EdgeBlend(theme));
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
