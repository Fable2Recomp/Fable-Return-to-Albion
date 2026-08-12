using Godot;
using Refii.Textures;

namespace Refii.Level;

public static class Engine_WaterMaterial
{
    public const string ShaderPath = "res://src/Engine/level/water/water.gdshader";

    /// <summary>WATER_MAX_REFRACTION_DISTANCE's engine default, in world units.</summary>
    private const float MaxRefractionDistanceDefault = 75.0f;

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

        // w: WATER_MAX_REFRACTION_DISTANCE, the distance the refraction has faded out by.
        //
        // A THEME PARAMETER WITH NO PER-BODY SLOT — the body block below ends at
        // WATER_GLITTERING_POWER, and `sub_821E4C88` sources this one from the day-set theme only.
        // The guest uploads its RECIPROCAL (`821E5054  fdivs f5, f13, f6`, f13 = 1.0) and the
        // shader multiplies by camera distance; the reciprocal is taken in the shader here instead
        // so the stated distance stays in world units on this side.
        //
        // 75.0 is the engine's own default (`sub_82B2EDE0`) and is exactly where this shader's
        // former hardcoded `1.0 / 75.0` came from. It stays a constant until the theme's `Water`
        // category is read, at which point it moves with time of day like everything else here.
        material.SetShaderParameter("w_bias", new Vector4(
            body.Param(WaterParam.FresnelBias, 0.2f),
            body.Param(WaterParam.ReflectionBias, 0f),
            body.Param(WaterParam.NormalScale, 0.05f),
            MaxRefractionDistanceDefault));

        material.SetShaderParameter("w_speed", new Vector4(
            body.Param(WaterParam.NormalSpeed0X, 0.052f),
            body.Param(WaterParam.NormalSpeed0Y, 0.011f),
            body.Param(WaterParam.NormalSpeed1X, -0.019f),
            body.Param(WaterParam.NormalSpeed1Y, 0.019f)));

        // ONE SCALE PER LAYER, NOT AN X/Y PAIR. WATER_NORMAL_MAP_SCALE0 and _SCALE1 are single
        // scalars that the guest SPLATS into both components of g_WaterConstants(4) and (5), so
        // the two slots the body block carries always hold the same number. Reading both is
        // harmless and honours the file; the defaults were not.
        //
        // The engine's defaults are 0.047 and 0.055 (`sub_82B2EDE0`, reaching the block through
        // `sub_82B31B28`). The 0.188 / 0.220 that stood here are about four times finer, so a lake
        // wore roughly sixteen times as many wavelets as the game states and read as noise rather
        // than as swell — which is most of why the surface looked wrong before the sun even
        // reached it.
        float normalScale0 = body.Param(WaterParam.NormalScale0X, 0.047f);
        float normalScale1 = body.Param(WaterParam.NormalScale1X, 0.055f);
        material.SetShaderParameter("w_scale", new Vector4(
            normalScale0, normalScale0, normalScale1, normalScale1));

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

        // w: DiffuseAbsorption, and NOTHING READS IT — here or in the shipped game. It is one of
        // the four body parameters (indices 30..33, the absorption and specular trio) that land in
        // g_WaterConstants(16..19), and the per-frame theme upload FORCES ALL FOUR TO ZERO before
        // any draw sees them:
        //
        //     821E5128  stfs f0, 0x100(r11)      ; WC(16).x = 0
        //     821E512C  stfs f0, 0x110(r11)      ; WC(17).x = 0
        //     821E5130  stfs f0, 0x120(r11)      ; WC(18).x = 0
        //     821E5134  stfs f0, 0x130(r11)      ; WC(19).x = 0
        //
        // So their authored values are dead in the retail game and a shader that consumed them
        // would diverge from it. Kept in the vector only because the slot is free.
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
