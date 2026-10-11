using System;
using Godot;

namespace Refii.Level;

[Tool]
public partial class Engine_DynamicCloudShadows : MeshInstance3D
{
    public const string NodeName = "DynamicCloudShadows";
    private const float CloudBase = 1400f;
    private SubViewport? _densityViewport;
    private ShaderMaterial? _densityMaterial;
    private DirectionalLight3D? _sun;
    private bool _originalShadow;
    private float _originalAngularDistance;
    private float _originalPancakeSize;
    private float _mapExtent;
    private static readonly StringName[] Parameters =
    {
        "volumetric_clouds", "cloud_volume_shape", "cloud_volume_detail", "cloud_shape", "cloud_detail",
        "cloud_coverage", "cloud_density", "cloud_softness", "cloud_scale", "cloud_height",
        "cloud_speed", "cloud_time", "cloud_motion_multiplier", "wind_dir", "overcast", "storm", "sun_engine",
    };

    public static void Sync(Node owner, ref Engine_DynamicCloudShadows? caster, ShaderMaterial? sky,
        bool enabled, Camera3D? camera, DirectionalLight3D? sun = null, bool? normalSunShadows = null)
    {
        Vector3 sunEngine = sky is not null ? sky.GetShaderParameter("sun_engine").AsVector3() : Vector3.Zero;
        bool visible = enabled && sky is not null && sunEngine.Z > 0.03f;
        if (!visible)
        {
            if (caster is not null)
            {
                if (normalSunShadows.HasValue) caster._originalShadow = normalSunShadows.Value;
                caster.Visible = false;
                caster.RestoreSun();
                if (caster._densityViewport is not null) caster._densityViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            }
            return;
        }
        if (caster is null)
        {
            caster = new Engine_DynamicCloudShadows
            {
                Name = NodeName,
                Mesh = new PlaneMesh { Size = new Vector2(4000f, 4000f), SubdivideWidth = 127, SubdivideDepth = 127 },
                MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://src/Engine/level/environment/dynamic/dynamic_cloud_shadow.gdshader") },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly,
                GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            };
            owner.AddChild(caster);
            caster.TopLevel = true;
            caster.BuildDensityMap();
        }
        caster.Visible = true;
        Vector3 origin = camera?.GlobalPosition ?? Vector3.Zero;
        Vector3 towardSun = new Vector3(sunEngine.X, sunEngine.Z, sunEngine.Y).Normalized();
        Vector3 centre = origin + towardSun * ((CloudBase - origin.Y) / towardSun.Y);
        float extent = MathF.Max(4000f, (sun?.DirectionalShadowMaxDistance ?? 600f) * 3f);
        float casterDepth = MathF.Abs(CloudBase - origin.Y) / towardSun.Y + extent;
        caster.ConfigureSun(sun, normalSunShadows, casterDepth);
        float texel = extent / 256f;
        var mapCentre = new Vector2(SnapMapCoordinate(centre.X, texel), SnapMapCoordinate(centre.Z, texel));
        caster.GlobalPosition = new Vector3(mapCentre.X, CloudBase, mapCentre.Y);
        if (extent != caster._mapExtent)
        {
            caster._mapExtent = extent;
            ((PlaneMesh)caster.Mesh).Size = new Vector2(extent, extent);
        }
        foreach (StringName parameter in Parameters)
            caster._densityMaterial!.SetShaderParameter(parameter, sky!.GetShaderParameter(parameter));
        caster._densityMaterial.SetShaderParameter("shadow_map_center", mapCentre);
        caster._densityMaterial.SetShaderParameter("shadow_map_extent", extent);
        var material = (ShaderMaterial)caster.MaterialOverride;
        material.SetShaderParameter("shadow_map_center", mapCentre);
        material.SetShaderParameter("shadow_map_extent", extent);
        caster._densityViewport!.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
    }

    public static float SnapMapCoordinate(float coordinate, float texelSize)
    {
        if (!float.IsFinite(texelSize) || texelSize <= 0f) throw new ArgumentOutOfRangeException(nameof(texelSize));
        if (!float.IsFinite(coordinate)) throw new ArgumentOutOfRangeException(nameof(coordinate));
        return MathF.Floor(coordinate / texelSize) * texelSize;
    }

    private void BuildDensityMap()
    {
        _densityMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://src/Engine/level/environment/dynamic/dynamic_cloud_shadow_density.gdshader") };
        _densityViewport = new SubViewport
        {
            Name = "CloudShadowDensity",
            Size = new Vector2I(256, 256),
            Disable3D = true,
            World2D = new World2D(),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        AddChild(_densityViewport);
        _densityViewport.AddChild(new ColorRect { Size = new Vector2(256f, 256f), Material = _densityMaterial });
        ((ShaderMaterial)MaterialOverride).SetShaderParameter("cloud_shadow_map", _densityViewport.GetTexture());
    }

    private void ConfigureSun(DirectionalLight3D? sun, bool? normalShadows, float casterDepth)
    {
        if (_sun != sun)
        {
            RestoreSun();
            _sun = sun;
            if (sun is not null)
            {
                _originalShadow = sun.ShadowEnabled;
                _originalAngularDistance = sun.LightAngularDistance;
                _originalPancakeSize = sun.DirectionalShadowPancakeSize;
            }
        }
        if (_sun is null) return;
        if (normalShadows.HasValue) _originalShadow = normalShadows.Value;
        _sun.ShadowEnabled = true;
        _sun.DirectionalShadowPancakeSize = MathF.Max(_originalPancakeSize, casterDepth);
        _sun.LightAngularDistance = MathF.Max(_originalAngularDistance, 0.53f);
    }

    private void RestoreSun()
    {
        if (_sun is not null && GodotObject.IsInstanceValid(_sun))
        {
            _sun.ShadowEnabled = _originalShadow;
            _sun.LightAngularDistance = _originalAngularDistance;
            _sun.DirectionalShadowPancakeSize = _originalPancakeSize;
        }
        _sun = null;
    }

    public override void _ExitTree() => RestoreSun();
}
