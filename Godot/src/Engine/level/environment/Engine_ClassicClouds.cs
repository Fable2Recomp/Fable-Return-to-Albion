using System.Collections.Generic;
using Godot;
using Refii.Models;

namespace Refii.Level;

/// <summary>
/// The cloud layers on the title's own CLOUDS pair, as `sub_82267858` -> `sub_821DB5E0` ->
/// `Render_MaterialQuadPass_INFERRED` (0x8223A210) draws them.
/// </summary>
/// <remarks>
/// Per layer, highest first: a quad (-SX..SX, -SY..SY) at the layer's HEIGHT, UV 0..1 (the
/// modern layer's own mesh), PS CLOUDS. Constants: CloudsScale = (TEXSCALE x, y, 0, 0),
/// AccumulatedUVOffset = the scrolled offset (velocity x 0.001 x dt, wrapped), Transparency,
/// NormalStrength = clamp(1 - NORMAL_STRENGTH, 0.1, 2), AmbientLight, Brightness; per frame
/// ViewerPosition = eye, ViewerDirection = the view's forward, LightColor = the last directional
/// light's +208, LightPosition = eye - (its +304) x 2000. States: cull none, alpha test GREATER
/// 5, SRCALPHA / INVSRCALPHA, depth write ON.
/// </remarks>
public static class Engine_ClassicClouds
{
    private const string RenderMode = "unshaded, blend_mix, depth_draw_always, cull_disabled";

    /// <summary>Marks a cloud layer material on the title's pair.</summary>
    public static readonly StringName ClassicCloudMeta = "xenos_cloud";

    private static readonly List<System.WeakReference<ShaderMaterial>> _materials = new();

    /// <summary>The layer's material on VSHADER_CLOUDS + PSHADER_CLOUDS, or null without the pair.</summary>
    /// <summary>The theme's layer (0..3) a material draws.</summary>
    public static readonly StringName LayerMeta = "xenos_cloud_layer";

    public static ShaderMaterial? Material(Texture2D density, CloudLayerTheme layer)
    {
        if (!Engine_ClassicMaterial.Active)
        {
            return null;
        }
        Shader? shader = Engine_ClassicShaders.Compose("VSHADER_CLOUDS", "PSHADER_CLOUDS", RenderMode);
        if (shader is null)
        {
            return null;
        }
        var material = new ShaderMaterial { Shader = shader };
        material.SetMeta(ClassicCloudMeta, true);
        material.SetShaderParameter("ps_g_CloudsSampler", density);
        material.SetShaderParameter("xr_spec_constants", 2u);
        material.SetShaderParameter("xr_alpha_threshold", 5.5f / 255f);
        SetLayer(material, layer);
        SetScroll(material, 0f, 0f);
        lock (_materials)
        {
            _materials.Add(new System.WeakReference<ShaderMaterial>(material));
        }
        return material;
    }

    /// <summary>The layer's theme values (they follow the time of day).</summary>
    public static void SetLayer(ShaderMaterial material, CloudLayerTheme layer)
    {
        // The guest's own blended block when it is up: CLOUDS_LAYERn_* at 133 + 14 n
        // (`sub_82267610` reads 133+14i .. 146+14i).
        if (Engine_ClassicTheme.Live && material.HasMeta(LayerMeta))
        {
            int at = 133 + 14 * material.GetMeta(LayerMeta).AsInt32();
            material.SetShaderParameter("ps_g_CloudsScale", new Vector4(Engine_ClassicTheme.Param(at + 10), Engine_ClassicTheme.Param(at + 11), 0f, 0f));
            material.SetShaderParameter("ps_g_Transparency", Splat(Engine_ClassicTheme.Param(at)));
            material.SetShaderParameter("ps_g_NormalStrength", Splat(Engine_Clouds.ShaderNormalStrength(Engine_ClassicTheme.Param(at + 2))));
            material.SetShaderParameter("ps_g_AmbientLight", Splat(Engine_ClassicTheme.Param(at + 4)));
            material.SetShaderParameter("ps_g_Brightness", Splat(Engine_ClassicTheme.Param(at + 5)));
            return;
        }
        material.SetShaderParameter("ps_g_CloudsScale", new Vector4(layer.TextureScaleX, layer.TextureScaleY, 0f, 0f));
        material.SetShaderParameter("ps_g_Transparency", Splat(layer.Transparency));
        material.SetShaderParameter("ps_g_NormalStrength", Splat(Engine_Clouds.ShaderNormalStrength(layer.NormalStrength)));
        material.SetShaderParameter("ps_g_AmbientLight", Splat(layer.AmbientLight));
        material.SetShaderParameter("ps_g_Brightness", Splat(layer.Brightness));
    }

    /// <summary>The scrolled UV offset (binding 0, `sub_82205918`).</summary>
    public static void SetScroll(ShaderMaterial material, float x, float y) =>
        material.SetShaderParameter("ps_g_AccumulatedUVOffset", new Vector4(x, y, 0f, 0f));

    /// <summary>The frame's viewer and main light, guest axes. Main thread, once a frame.</summary>
    public static void Pump(Vector3 eye, Vector3 viewerDirection, Vector3 lightColour, Vector3 lightDirection)
    {
        var position = new Vector4(eye.X, eye.Y, eye.Z, 1f);
        var direction = new Vector4(viewerDirection.X, viewerDirection.Y, viewerDirection.Z, 0f);
        var colour = new Vector4(lightColour.X, lightColour.Y, lightColour.Z, 1f);
        Vector3 at = eye - lightDirection * 2000f;
        var lightPosition = new Vector4(at.X, at.Y, at.Z, 1f);
        lock (_materials)
        {
            for (int i = _materials.Count - 1; i >= 0; i--)
            {
                if (!_materials[i].TryGetTarget(out ShaderMaterial? material) || !GodotObject.IsInstanceValid(material))
                {
                    _materials.RemoveAt(i);
                    continue;
                }
                material.SetShaderParameter("ps_g_ViewerPosition", position);
                material.SetShaderParameter("ps_g_ViewerDirection", direction);
                material.SetShaderParameter("ps_g_LightColor", colour);
                material.SetShaderParameter("ps_g_LightPosition", lightPosition);
            }
        }
    }

    private static Vector4 Splat(float v) => new(v, v, v, v);
}
