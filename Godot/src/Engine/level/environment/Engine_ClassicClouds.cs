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
///
/// MIST (`Render_MaterialQuadPass_INFERRED` 0x8223A3C4..E8): on the main view the pixel shader is
/// CLOUDS + 1 = CLOUDS_MIST while the eye (view+1680) is inside a mist volume (`sub_82296290`),
/// with that volume's five registers and samplers bound as for every MIST1 draw. The layers
/// are materials, not instances, so the misted composition reads its row from a material
/// uniform of its own (<c>XR_ROW_UNIFORM</c>).
/// </remarks>
public static class Engine_ClassicClouds
{
    private const string RenderMode = "unshaded, blend_mix, depth_draw_always, cull_disabled";

    /// <summary>Marks a cloud layer material on the title's pair.</summary>
    public static readonly StringName ClassicCloudMeta = "xenos_cloud";

    private static readonly List<System.WeakReference<ShaderMaterial>> _materials = new();
    private const string MistedDefines = "uniform int xr_cloud_row = 0;\n#define XR_ROW_UNIFORM xr_cloud_row";
    private static Shader? _plain;
    private static Shader? _misted;
    private static bool _mistedTried;
    private static int _row;
    private static Engine_ClassicMist.Volume? _mist;

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
        _plain = shader;
        var material = new ShaderMaterial { Shader = _mist is not null && Misted() is { } misted ? misted : shader };
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
            int index = material.GetMeta(LayerMeta).AsInt32();
            FollowDensity(material, index);
            int at = 133 + 14 * index;
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
        // The eye in a mist volume (guest (x, y, z) is Godot (-x, z, y)).
        Engine_ClassicMist.Volume? mist = Misted() is null
            ? null
            : Engine_ClassicMist.VolumeFor(new Aabb(new Vector3(-eye.X, eye.Z, eye.Y), Vector3.Zero));
        if (mist is not null && _row == 0)
        {
            _row = Engine_ClassicConstants.AcquireRow();
        }
        if (mist is not null && _row != 0)
        {
            Engine_ClassicMist.WriteRegisters(mist, _row);
        }
        else
        {
            mist = null;
        }
        bool changed = !ReferenceEquals(mist, _mist);
        _mist = mist;
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
                if (changed)
                {
                    Shader? want = mist is not null ? _misted : _plain;
                    if (want is not null && material.Shader != want)
                    {
                        material.Shader = want;
                    }
                    if (mist is not null)
                    {
                        material.SetShaderParameter("xr_cloud_row", _row);
                        material.SetShaderParameter("ps_g_MistDensitySampler", mist.Density64);
                        material.SetShaderParameter("ps_g_MistHeightSampler", Engine_ClassicMist.FlatHeight);
                    }
                }
                material.SetShaderParameter("ps_g_ViewerPosition", position);
                material.SetShaderParameter("ps_g_ViewerDirection", direction);
                material.SetShaderParameter("ps_g_LightColor", colour);
                material.SetShaderParameter("ps_g_LightPosition", lightPosition);
            }
        }
        if (mist is null && _row != 0)
        {
            Engine_ClassicConstants.ReleaseRow(_row);
            _row = 0;
        }
    }

    /// <summary>The CLOUDS_MIST composition, or null when the bank has none.</summary>
    private static Shader? Misted()
    {
        if (_misted is null && !_mistedTried)
        {
            _mistedTried = true;
            _misted = Engine_ClassicShaders.HasEntry("PSHADER_CLOUDS_MIST")
                ? Engine_ClassicShaders.Compose("VSHADER_CLOUDS", "PSHADER_CLOUDS_MIST", RenderMode, 0, MistedDefines)
                : null;
        }
        return _misted;
    }

    private static Vector4 Splat(float v) => new(v, v, v, v);

    private static readonly StringName DensityHashMeta = "xenos_density_hash";

    /// <summary>
    /// The layer's density map from the theme's live CLOUDS_LAYERn_DENSITY_MAP slot (8 + n, the
    /// heaviest texture, `sub_82267610` -> layer+60), rebound when the hash moves.
    /// </summary>
    private static void FollowDensity(ShaderMaterial material, int index)
    {
        if (!Engine_ClassicTheme.TexturesLive || index < 0 || index > 3)
        {
            return;
        }
        uint hash = Engine_ClassicTheme.TopHash(Engine_ClassicTheme.SlotCloudDensity0 + index);
        if (hash == 0u || (material.HasMeta(DensityHashMeta) && (uint)material.GetMeta(DensityHashMeta).AsInt64() == hash))
        {
            return;
        }
        material.SetMeta(DensityHashMeta, (long)hash);
        if (Engine_EnvironmentTextures.Load(hash, Engine_BnkMeshLoader.ReadBytes, out _) is { } density)
        {
            material.SetShaderParameter("ps_g_CloudsSampler", density);
        }
    }
}
