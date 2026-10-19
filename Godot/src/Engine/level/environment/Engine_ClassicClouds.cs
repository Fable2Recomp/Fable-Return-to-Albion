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
/// with that volume's five registers and samplers bound as for every MIST1 draw, and with them
/// pixel boolean b1, g_EnableMist (`Render_BindHighSamplersAndPixelBool_INFERRED`, as for the
/// fur shells' PS 128; the bit stands once any mist has existed, byte_8335CBD2, so it is set
/// whenever a volume is): CLOUDS_MIST applies its mist only under b1. The layers are materials,
/// not instances, so the misted composition reads its row from a material uniform of its own
/// (<c>XR_ROW_UNIFORM</c>).
///
/// The viewer and light positions belong to the view: on split screen the second view reads its
/// own from host registers (<see cref="PumpView"/>, Engine_ClassicViews). So does the mist: each
/// view's eye picks its volume, the layers take the misted composition while either eye is in
/// one, each view's copy of the row holds its own volume's registers, and the second view reads
/// its own density map and b1 (<see cref="Engine_ClassicShaders.ViewSampler"/>,
/// <see cref="Engine_ClassicShaders.ViewBooleans"/>); a view whose eye is in no volume, which the
/// title draws on PS CLOUDS, has b1 clear and so draws unmisted.
/// </remarks>
public static class Engine_ClassicClouds
{
    private const string RenderMode = "unshaded, blend_mix, depth_draw_always, cull_disabled";

    /// <summary>Marks a cloud layer material on the title's pair.</summary>
    public static readonly StringName ClassicCloudMeta = "xenos_cloud";

    private static readonly List<System.WeakReference<ShaderMaterial>> _materials = new();
    private static string MistedDefines => "uniform int xr_cloud_row = 0;\n#define XR_ROW_UNIFORM xr_cloud_row\n" + ViewDefines + "\n" +
        Engine_ClassicShaders.ViewSampler("ps_g_MistDensitySampler") + "\n" + Engine_ClassicShaders.ViewBooleans();

    /// <summary>Pixel boolean b1, g_EnableMist (booleans 16.. are the pixel stage's).</summary>
    private const uint EnableMist = 1u << (16 + 1);

    /// <summary>The cloud stage's view-space constants, read per view (Engine_ClassicShaders).</summary>
    private static string ViewDefines =>
        Engine_ClassicShaders.ViewParam("ps_g_ViewerPosition", true, Engine_ClassicRegisters.Ps.HostCloudViewer) + "\n" +
        Engine_ClassicShaders.ViewParam("ps_g_ViewerDirection", true, Engine_ClassicRegisters.Ps.HostCloudDirection) + "\n" +
        Engine_ClassicShaders.ViewParam("ps_g_LightPosition", true, Engine_ClassicRegisters.Ps.HostCloudLight);
    private static Shader? _plain;
    private static Shader? _misted;
    private static bool _mistedTried;
    private static int _row;
    private static Engine_ClassicMist.Volume? _mist;
    private static Engine_ClassicMist.Volume? _secondMist;

    /// <summary>The layer's material on VSHADER_CLOUDS + PSHADER_CLOUDS, or null without the pair.</summary>
    /// <summary>The theme's layer (0..3) a material draws.</summary>
    public static readonly StringName LayerMeta = "xenos_cloud_layer";

    public static ShaderMaterial? Material(Texture2D density, CloudLayerTheme layer)
    {
        if (!Engine_ClassicMaterial.Active)
        {
            return null;
        }
        Shader? shader = Engine_ClassicShaders.Compose("VSHADER_CLOUDS", "PSHADER_CLOUDS", RenderMode, 0, ViewDefines);
        if (shader is null)
        {
            return null;
        }
        _plain = shader;
        var material = new ShaderMaterial { Shader = (_mist ?? _secondMist) is not null && Misted() is { } misted ? misted : shader };
        material.SetMeta(ClassicCloudMeta, true);
        material.SetShaderParameter(PsCloudsSamplerName, density);
        material.SetShaderParameter(XrSpecConstantsName, 2u);
        material.SetShaderParameter(XrAlphaThresholdName, 5.5f / 255f);
        SetLayer(material, layer);
        SetScroll(material, 0f, 0f);
        if ((_mist ?? _secondMist) is not null)
        {
            ApplyMist(material);
        }
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
            material.SetShaderParameter(PsCloudsScaleName, new Vector4(Engine_ClassicTheme.Param(at + 10), Engine_ClassicTheme.Param(at + 11), 0f, 0f));
            material.SetShaderParameter(PsTransparencyName, Splat(Engine_ClassicTheme.Param(at)));
            material.SetShaderParameter(PsNormalStrengthName, Splat(Engine_Clouds.ShaderNormalStrength(Engine_ClassicTheme.Param(at + 2))));
            material.SetShaderParameter(PsAmbientLightName, Splat(Engine_ClassicTheme.Param(at + 4)));
            material.SetShaderParameter(PsBrightnessName, Splat(Engine_ClassicTheme.Param(at + 5)));
            return;
        }
        material.SetShaderParameter(PsCloudsScaleName, new Vector4(layer.TextureScaleX, layer.TextureScaleY, 0f, 0f));
        material.SetShaderParameter(PsTransparencyName, Splat(layer.Transparency));
        material.SetShaderParameter(PsNormalStrengthName, Splat(Engine_Clouds.ShaderNormalStrength(layer.NormalStrength)));
        material.SetShaderParameter(PsAmbientLightName, Splat(layer.AmbientLight));
        material.SetShaderParameter(PsBrightnessName, Splat(layer.Brightness));
    }

    /// <summary>The scrolled UV offset (binding 0, `sub_82205918`).</summary>
    public static void SetScroll(ShaderMaterial material, float x, float y) =>
        material.SetShaderParameter(PsAccumulatedUVOffsetName, new Vector4(x, y, 0f, 0f));

    /// <summary>The frame's viewer and main light, guest axes. Main thread, once a frame.</summary>
    public static void Pump(Vector3 eye, Vector3 viewerDirection, Vector3 lightColour, Vector3 lightDirection)
    {
        // The eye in a mist volume (guest (x, y, z) is Godot (-x, z, y)); on split screen the
        // second view's eye too, each written into its view's copy of the row.
        Engine_ClassicMist.Volume? mist = Misted() is null
            ? null
            : Engine_ClassicMist.VolumeFor(new Aabb(new Vector3(-eye.X, eye.Z, eye.Y), Vector3.Zero));
        bool split = Engine_ClassicViews.Count > 1 && Engine_ClassicViews.Camera(1) is not null;
        Engine_ClassicMist.Volume? secondMist = split && Misted() is not null
            ? Engine_ClassicMist.VolumeFor(new Aabb(Engine_ClassicViews.Eye(1), Vector3.Zero))
            : null;
        bool any = mist is not null || secondMist is not null;
        if (any && _row == 0)
        {
            _row = Engine_ClassicConstants.AcquireRow();
        }
        if (any && _row != 0)
        {
            if (!split)
            {
                Engine_ClassicMist.WriteRegisters(mist!, _row);
            }
            else
            {
                if (mist is not null)
                {
                    Engine_ClassicMist.WriteRegisters(mist, _row, 0);
                }
                if (secondMist is not null)
                {
                    Engine_ClassicMist.WriteRegisters(secondMist, _row, 1);
                }
            }
        }
        else
        {
            mist = null;
            secondMist = null;
            any = false;
        }
        bool changed = !ReferenceEquals(mist, _mist) || !ReferenceEquals(secondMist, _secondMist);
        _mist = mist;
        _secondMist = secondMist;
        Engine_ClassicMist.Volume? bound = mist ?? secondMist;
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
                    Shader? want = bound is not null ? _misted : _plain;
                    if (want is not null && material.Shader != want)
                    {
                        material.Shader = want;
                    }
                    if (bound is not null)
                    {
                        ApplyMist(material);
                    }
                }
                material.SetShaderParameter(PsViewerPositionName, position);
                material.SetShaderParameter(PsViewerDirectionName, direction);
                material.SetShaderParameter(PsLightColorName, colour);
                material.SetShaderParameter(PsLightPositionName, lightPosition);
            }
        }
        if (!any && _row != 0)
        {
            Engine_ClassicConstants.ReleaseRow(_row);
            _row = 0;
        }
    }

    /// <summary>
    /// The misted composition's binding for the volumes the views' eyes stand in: the row, each
    /// view's density map and b1 set for exactly the views whose eye is in a volume.
    /// </summary>
    private static void ApplyMist(ShaderMaterial material)
    {
        Engine_ClassicMist.Volume? bound = _mist ?? _secondMist;
        if (bound is null)
        {
            return;
        }
        material.SetShaderParameter(XrCloudRowName, _row);
        material.SetShaderParameter(PsMistDensitySamplerName, bound.Density64);
        material.SetShaderParameter(PsMistHeightSamplerName, Engine_ClassicMist.FlatHeight);
        material.SetShaderParameter(XrBooleansName, _mist is not null ? EnableMist : 0u);
        if (_secondMist is not null)
        {
            material.SetShaderParameter(PsMistDensitySecondName, _secondMist.Density64);
        }
        material.SetShaderParameter(XrSecondBooleansName, _secondMist is not null ? EnableMist : 0u);
    }

    /// <summary>
    /// A view's viewer, direction and light position into its host registers (guest axes), for
    /// split screen's second view. Main thread, after <see cref="Pump"/>.
    /// </summary>
    public static void PumpView(int view, Vector3 eye, Vector3 viewerDirection, Vector3 lightDirection)
    {
        Vector3 at = eye - lightDirection * 2000f;
        Four(view, Engine_ClassicRegisters.Ps.HostCloudViewer, eye.X, eye.Y, eye.Z, 1f);
        Four(view, Engine_ClassicRegisters.Ps.HostCloudDirection, viewerDirection.X, viewerDirection.Y, viewerDirection.Z, 0f);
        Four(view, Engine_ClassicRegisters.Ps.HostCloudLight, at.X, at.Y, at.Z, 1f);
    }

    private static readonly float[] _four = new float[4];

    private static void Four(int view, int register, float x, float y, float z, float w)
    {
        _four[0] = x; _four[1] = y; _four[2] = z; _four[3] = w;
        Engine_ClassicConstants.SetFrameView(view, true, register, _four);
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
            material.SetShaderParameter(PsCloudsSamplerName, density);
        }
    }

    // Made once: a string literal converts to a new StringName on every call.
    private static readonly StringName PsCloudsSamplerName = "ps_g_CloudsSampler";
    private static readonly StringName XrSpecConstantsName = "xr_spec_constants";
    private static readonly StringName XrAlphaThresholdName = "xr_alpha_threshold";
    private static readonly StringName PsCloudsScaleName = "ps_g_CloudsScale";
    private static readonly StringName PsTransparencyName = "ps_g_Transparency";
    private static readonly StringName PsNormalStrengthName = "ps_g_NormalStrength";
    private static readonly StringName PsAmbientLightName = "ps_g_AmbientLight";
    private static readonly StringName PsBrightnessName = "ps_g_Brightness";
    private static readonly StringName PsAccumulatedUVOffsetName = "ps_g_AccumulatedUVOffset";
    private static readonly StringName XrCloudRowName = "xr_cloud_row";
    private static readonly StringName PsMistDensitySamplerName = "ps_g_MistDensitySampler";
    private static readonly StringName XrBooleansName = "xr_booleans";
    private static readonly StringName XrSecondBooleansName = Engine_ClassicShaders.SecondViewBooleans;
    private static readonly StringName PsMistDensitySecondName = Engine_ClassicShaders.SecondViewSampler("ps_g_MistDensitySampler");
    private static readonly StringName PsMistHeightSamplerName = "ps_g_MistHeightSampler";
    private static readonly StringName PsViewerPositionName = "ps_g_ViewerPosition";
    private static readonly StringName PsViewerDirectionName = "ps_g_ViewerDirection";
    private static readonly StringName PsLightColorName = "ps_g_LightColor";
    private static readonly StringName PsLightPositionName = "ps_g_LightPosition";
}
