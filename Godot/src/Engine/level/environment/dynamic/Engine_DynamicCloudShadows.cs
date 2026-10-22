using System;
using Godot;

namespace Refii.Level;

/// <summary>Shader and global parameter names held once: a string literal is a fresh finalizable StringName per call.</summary>
internal static class CloudShadowNames
{
    public static readonly StringName dynamic_cloud_mask = "dynamic_cloud_mask";
    public static readonly StringName dynamic_cloud_projection = "dynamic_cloud_projection";
    public static readonly StringName shadow_map_center = "shadow_map_center";
    public static readonly StringName shadow_map_extent = "shadow_map_extent";
    public static readonly StringName sun_engine = "sun_engine";
}

[Tool]
public partial class Engine_DynamicCloudShadows : Node
{
    public const string NodeName = "DynamicCloudShadows";
    private const float CloudBase = 1400f;
    private const int MapSize = 256;
    private const ulong RefreshIntervalUsec = 66_667;
    private SubViewport? _densityViewport;
    private ShaderMaterial? _densityMaterial;
    private ShaderMaterial? _source;
    private ulong? _lastRefresh;
    private Vector4 _projection;
    private bool _published;
    private bool _resourcesChanged;
    public int DensityUpdateCount { get; private set; }
    private readonly Variant[] _values = new Variant[Parameters.Length];
    private readonly Resource?[] _resources = new Resource?[Parameters.Length];
    private static readonly string[] Parameters =
    {
        "volumetric_clouds", "cloud_volume_shape", "cloud_volume_detail", "cloud_shape", "cloud_detail",
        "cloud_coverage", "cloud_density", "cloud_softness", "cloud_scale", "cloud_height",
        "cloud_speed", "cloud_time", "cloud_offset", "cloud_motion_multiplier", "wind_dir", "overcast", "storm", "sun_engine",
    };

    public static void Sync(Node owner, ref Engine_DynamicCloudShadows? caster, ShaderMaterial? sky,
        bool enabled, Camera3D? camera, DirectionalLight3D? sun = null)
    {
        Vector3 sunEngine = sky is not null ? sky.GetShaderParameter(CloudShadowNames.sun_engine).AsVector3() : Vector3.Zero;
        if (!enabled || sky is null || sunEngine.Z <= 0.03f)
        {
            caster?.Disable();
            return;
        }
        if (caster is null)
        {
            caster = new Engine_DynamicCloudShadows { Name = NodeName };
            owner.AddChild(caster);
            caster.BuildDensityMap();
        }
        Vector3 towardSun = new Vector3(sunEngine.X, sunEngine.Z, sunEngine.Y).Normalized();
        float extent = MathF.Max(4000f, (sun?.DirectionalShadowMaxDistance ?? 600f) * 3f);

        // ONE MAP FOR EVERY VIEW. The mask is world-space (dynamic_cloud_receiver), so both split
        // views read the same texture; it is centred between the views' cloud-base points and only
        // grows, in quarter-extent steps, once they are too far apart for each to keep a quarter
        // of the map around it. One view is the camera's own point at the base extent.
        Vector3 low = Vector3.Zero;
        Vector3 high = Vector3.Zero;
        int views = 0;
        foreach (Camera3D view in Refii.Core.Engine_Views.Cameras(camera))
        {
            Vector3 eye = view.GlobalPosition;
            Vector3 at = eye + towardSun * ((CloudBase - eye.Y) / towardSun.Y);
            low = views == 0 ? at : low.Min(at);
            high = views == 0 ? at : high.Max(at);
            ++views;
        }

        Vector3 centre = views switch
        {
            0 => towardSun * (CloudBase / towardSun.Y),
            1 => low,
            _ => (low + high) * 0.5f,
        };
        if (views > 1)
        {
            Vector3 span = high - low;
            float needed = MathF.Max(span.X, span.Z) + (extent * 0.5f);
            if (needed > extent)
            {
                float step = extent * 0.25f;
                extent = MathF.Ceiling(needed / step) * step;
            }
        }

        float texel = extent / MapSize;
        var mapCentre = new Vector2(SnapMapCoordinate(centre.X, texel), SnapMapCoordinate(centre.Z, texel));
        var projection = new Vector4(mapCentre.X, mapCentre.Y, extent, 1f);
        bool moved = projection != caster._projection;
        bool first = !caster._published || caster._source != sky;
        ulong now = Time.GetTicksUsec();
        if (!first && !moved && !caster._resourcesChanged && !RefreshDue(now, caster._lastRefresh)) return;
        caster._lastRefresh = now;
        bool dirty = first || moved || caster._resourcesChanged;
        for (int i = 0; i < Parameters.Length; i++)
        {
            Variant value = sky.GetShaderParameter(Parameters[i]);
            if (!first && SameParameter(value, caster._values[i])) { value.Dispose(); continue; }
            caster._values[i].Dispose();
            caster._values[i] = value;
            Resource? resource = value.VariantType == Variant.Type.Object ? value.AsGodotObject() as Resource : null;
            if (caster._resources[i] != resource)
            {
                if (caster._resources[i] is { } previous) previous.Changed -= caster.InvalidateDensity;
                caster._resources[i] = resource;
                if (resource is not null) resource.Changed += caster.InvalidateDensity;
            }
            caster._densityMaterial!.SetShaderParameter(Parameters[i], value);
            dirty = true;
        }
        caster._source = sky;
        caster._resourcesChanged = false;
        if (!dirty) return;
        caster._densityMaterial!.SetShaderParameter(CloudShadowNames.shadow_map_center, mapCentre);
        caster._densityMaterial.SetShaderParameter(CloudShadowNames.shadow_map_extent, extent);
        caster._densityViewport!.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        caster.DensityUpdateCount++;
        if (!caster._published)
            RenderingServer.GlobalShaderParameterSet(CloudShadowNames.dynamic_cloud_mask, caster._densityViewport.GetTexture());
        if (first || moved)
            RenderingServer.GlobalShaderParameterSet(CloudShadowNames.dynamic_cloud_projection, projection);
        caster._projection = projection;
        caster._published = true;
    }

    public static bool RefreshDue(ulong nowUsec, ulong? lastUsec) =>
        lastUsec is not { } last || nowUsec < last || nowUsec - last >= RefreshIntervalUsec;

    public static bool SameParameter(in Variant left, in Variant right)
    {
        if (left.VariantType != right.VariantType) return false;
        return left.VariantType switch
        {
            Variant.Type.Nil => true,
            Variant.Type.Bool => left.AsBool() == right.AsBool(),
            Variant.Type.Int => left.AsInt64() == right.AsInt64(),
            Variant.Type.Float => left.AsDouble() == right.AsDouble(),
            Variant.Type.Vector2 => left.AsVector2() == right.AsVector2(),
            Variant.Type.Vector3 => left.AsVector3() == right.AsVector3(),
            Variant.Type.Object => left.AsGodotObject() == right.AsGodotObject(),
            _ => false,
        };
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
            Size = new Vector2I(MapSize, MapSize),
            Disable3D = true,
            World2D = new World2D(),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        AddChild(_densityViewport);
        _densityViewport.AddChild(new ColorRect { Size = new Vector2(MapSize, MapSize), Material = _densityMaterial });
    }

    private void Disable()
    {
        if (_published) RenderingServer.GlobalShaderParameterSet(CloudShadowNames.dynamic_cloud_projection, Vector4.Zero);
        _published = false;
        _lastRefresh = null;
        if (_densityViewport is not null) _densityViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
    }

    private void InvalidateDensity() => _resourcesChanged = true;

    public override void _ExitTree()
    {
        Disable();
        for (int i = 0; i < _values.Length; i++)
        {
            if (_resources[i] is { } resource) resource.Changed -= InvalidateDensity;
            _resources[i] = null;
            _values[i].Dispose();
        }
    }
}
