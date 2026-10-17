using System;
using Godot;

namespace Refii.Level;

public struct DynamicPrecipitationLook
{
    public float RainWidth;
    public float RainLength;
    public float RainOpacity;
    public float RainSpeed;
    public float RainDensity;
    public float SnowSize;
    public float SnowOpacity;
    public float SnowSpeed;
    public float SnowDensity;
    public float SnowSway;

    public static DynamicPrecipitationLook Default => new()
    {
        RainWidth = Refii.Core.Engine_Switches.Float("dynamic_sky_rain_width", 0.004f),
        RainLength = Refii.Core.Engine_Switches.Float("dynamic_sky_rain_length", 0.35f),
        RainOpacity = Refii.Core.Engine_Switches.Float("dynamic_sky_rain_opacity", 0.18f),
        RainSpeed = Refii.Core.Engine_Switches.Float("dynamic_sky_rain_speed", 16f),
        RainDensity = Refii.Core.Engine_Switches.Float("dynamic_sky_rain_density", 0.6f),
        SnowSize = Refii.Core.Engine_Switches.Float("dynamic_sky_snow_size", 0.018f),
        SnowOpacity = Refii.Core.Engine_Switches.Float("dynamic_sky_snow_opacity", 0.8f),
        SnowSpeed = Refii.Core.Engine_Switches.Float("dynamic_sky_snow_speed", 0.9f),
        SnowDensity = Refii.Core.Engine_Switches.Float("dynamic_sky_snow_density", 0.7f),
        SnowSway = Refii.Core.Engine_Switches.Float("dynamic_sky_snow_sway", 0.3f),
    };
}

public partial class Engine_DynamicPrecipitation : Node3D
{
    public const string NodeName = "DynamicPrecipitation";

    public const string RainShaderPath =
        "res://src/Engine/level/environment/dynamic/dynamic_rain.gdshader";

    public const string SnowShaderPath =
        "res://src/Engine/level/environment/dynamic/dynamic_snow.gdshader";

    public static readonly int RainSlots =
        Refii.Core.Engine_Switches.Int("dynamic_sky_rain_slots", 8000);

    public static readonly int SnowSlots =
        Refii.Core.Engine_Switches.Int("dynamic_sky_snow_slots", 12000);

    private static readonly StringName CountName = "particle_count";
    private static readonly StringName WindName = "wind";
    private static readonly StringName BrightnessName = "brightness";

    private MeshInstance3D? _rain;
    private MeshInstance3D? _snow;
    private int _rainCount = -1;
    private int _snowCount = -1;
    private float _brightness = -1f;
    private Vector3 _wind;
    private double _windX;
    private double _windZ;
    private double _snowWindX;
    private double _snowWindZ;
    private double _motionTime;
    private DynamicPrecipitationLook _look = DynamicPrecipitationLook.Default;

    public DynamicPrecipitationLook Look
    {
        get => _look;
        set
        {
            _look = value;
            ApplyLook();
            _rainCount = -1;
            _snowCount = -1;
        }
    }

    public static Engine_DynamicPrecipitation? Build()
    {
        if (RainSlots <= 0 && SnowSlots <= 0)
        {
            return null;
        }

        var node = new Engine_DynamicPrecipitation { Name = NodeName };
        node._rain = Layer("Rain", RainShaderPath, RainSlots);
        node._snow = Layer("Snow", SnowShaderPath, SnowSlots);
        if (node._rain is null && node._snow is null)
        {
            return null;
        }

        if (node._rain is not null)
        {
            node.AddChild(node._rain);
        }

        if (node._snow is not null)
        {
            node.AddChild(node._snow);
        }

        node.ApplyLook();
        return node;
    }

    private static MeshInstance3D? Layer(string name, string shaderPath, int slots)
    {
        if (slots <= 0)
        {
            return null;
        }

        var shader = GD.Load<Shader>(shaderPath);
        if (shader is null)
        {
            GD.Print($"[dynamic-sky] precipitation shader missing at {shaderPath}");
            return null;
        }

        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter(CountName, 0);
        material.SetShaderParameter(WindName, new Vector3(2f, 0f, 0.5f));
        material.SetShaderParameter(BrightnessName, 1f);

        return new MeshInstance3D
        {
            Name = name,
            Mesh = BuildQuads(slots),
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384f,
            Visible = false,
        };
    }

    private static ArrayMesh BuildQuads(int particles)
    {
        int vertexCount = particles * 6;
        var vertices = new Vector3[vertexCount];
        var uvs = new Vector2[vertexCount];
        var ids = new Vector2[vertexCount];
        var corners = new Vector2[6]
        {
            new(0f, 0f), new(1f, 0f), new(0f, 1f),
            new(1f, 0f), new(1f, 1f), new(0f, 1f),
        };

        for (int p = 0; p < particles; p++)
        {
            for (int c = 0; c < 6; c++)
            {
                int v = (p * 6) + c;
                vertices[v] = Vector3.Zero;
                uvs[v] = corners[c];
                ids[v] = new Vector2(p, 0f);
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.TexUV2] = ids;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.CustomAabb = new Aabb(new Vector3(-4096f, -4096f, -4096f),
                                   new Vector3(8192f, 8192f, 8192f));
        return mesh;
    }

    private void ApplyLook()
    {
        if (_rain?.MaterialOverride is ShaderMaterial rain)
        {
            rain.SetShaderParameter("particle_size", new Vector2(_look.RainWidth, _look.RainLength));
            rain.SetShaderParameter("min_pixels", new Vector2(1f, 6f));
            rain.SetShaderParameter("max_pixels", new Vector2(2f, 48f));
            rain.SetShaderParameter("particle_colour",
                new Color(0.78f, 0.82f, 0.90f, Math.Clamp(_look.RainOpacity, 0f, 1f)));
            rain.SetShaderParameter("fall_speed", MathF.Max(_look.RainSpeed, 0.1f));
            rain.SetShaderParameter("sway", 0f);
            rain.SetShaderParameter("wind_scale", 1f);
            rain.SetShaderParameter("size_jitter", 0.6f);
            rain.SetShaderParameter("speed_jitter", 0.3f);
            rain.SetShaderParameter("box", new Vector3(12f, 10f, 12f));
            rain.SetShaderParameter("box_far", new Vector3(32f, 16f, 32f));
            rain.SetShaderParameter("near_fraction", 0.45f);
            rain.SetShaderParameter("depth_fade", 0.05f);
            rain.SetShaderParameter("fog_tint", new Color(0.70f, 0.73f, 0.80f));
        }

        if (_snow?.MaterialOverride is ShaderMaterial snow)
        {
            snow.SetShaderParameter("particle_size", new Vector2(_look.SnowSize, _look.SnowSize));
            snow.SetShaderParameter("min_pixels", new Vector2(0.65f, 0.65f));
            snow.SetShaderParameter("max_pixels", new Vector2(5f, 5f));
            snow.SetShaderParameter("particle_colour",
                new Color(1f, 1f, 1f, Math.Clamp(_look.SnowOpacity, 0f, 1f)));
            snow.SetShaderParameter("fall_speed", MathF.Max(_look.SnowSpeed, 0.05f));
            snow.SetShaderParameter("sway", MathF.Max(_look.SnowSway, 0f));
            snow.SetShaderParameter("wind_scale", 0.8f);
            snow.SetShaderParameter("size_jitter", 0.8f);
            snow.SetShaderParameter("speed_jitter", 0.6f);
            snow.SetShaderParameter("box", new Vector3(16f, 12f, 16f));
            snow.SetShaderParameter("box_far", new Vector3(42f, 26f, 42f));
            snow.SetShaderParameter("near_fraction", 0.65f);
            snow.SetShaderParameter("depth_fade", 0.035f);
            snow.SetShaderParameter("fog_tint", new Color(0.74f, 0.76f, 0.80f));
        }
    }

    private int _shelterVersion = -1;

    public override void _Process(double delta)
    {
        if (_shelterVersion == Engine_WeatherShelter.Version)
        {
            return;
        }

        _shelterVersion = Engine_WeatherShelter.Version;
        foreach (MeshInstance3D? layer in new[] { _rain, _snow })
        {
            if (layer?.MaterialOverride is ShaderMaterial material)
            {
                material.SetShaderParameter("shelter_on", Engine_WeatherShelter.HasBox ? 1f : 0f);
                material.SetShaderParameter("shelter_box", Engine_WeatherShelter.ToBox);
            }
        }
    }

    public void AdvanceMotion(float delta, float lightning)
    {
        if (!float.IsFinite(delta) || delta < 0f) return;
        _windX += _wind.X * (double)delta;
        _windZ += _wind.Z * (double)delta;
        _motionTime += delta;
        double gust = 1.0 + 0.35 * Math.Sin(_motionTime * 0.43) + 0.15 * Math.Sin(_motionTime * 1.13);
        _snowWindX += _wind.X * delta * gust;
        _snowWindZ += _wind.Z * delta * gust;
        foreach (MeshInstance3D? layer in new[] { _rain, _snow })
        {
            if (layer?.MaterialOverride is not ShaderMaterial material) continue;
            material.SetShaderParameter("motion_time", (float)_motionTime);
            material.SetShaderParameter("wind_offset", layer == _snow
                ? new Vector2((float)_snowWindX, (float)_snowWindZ)
                : new Vector2((float)_windX, (float)_windZ));
            material.SetShaderParameter("lightning_flash", float.IsFinite(lightning) ? Math.Clamp(lightning, 0f, 2f) : 0f);
        }
    }

    public void Apply(float rain, float snowFall, Vector2 windDir, float windSpeed, float brightness)
    {
        int rainCount = (int)(Math.Clamp(rain * _look.RainDensity, 0f, 1f) * RainSlots);
        int snowCount = (int)(Math.Clamp(snowFall * _look.SnowDensity, 0f, 1f) * SnowSlots);

        if (rainCount != _rainCount && _rain?.MaterialOverride is ShaderMaterial rainMaterial)
        {
            _rainCount = rainCount;
            rainMaterial.SetShaderParameter(CountName, rainCount);
            _rain.Visible = rainCount > 0;
        }

        if (snowCount != _snowCount && _snow?.MaterialOverride is ShaderMaterial snowMaterial)
        {
            _snowCount = snowCount;
            snowMaterial.SetShaderParameter(CountName, snowCount);
            _snow.Visible = snowCount > 0;
        }

        if (windDir.LengthSquared() < 1e-6f)
        {
            windDir = new Vector2(1f, 0.25f);
        }

        windDir = windDir.Normalized();
        float strength = Math.Clamp(windSpeed, 0f, 4f) * 2.0f;
        var wind = new Vector3(windDir.X * strength, 0f, windDir.Y * strength);
        bool windChanged = !wind.IsEqualApprox(_wind);
        bool brightnessChanged = MathF.Abs(brightness - _brightness) > 0.01f;
        _wind = wind;
        _brightness = brightness;

        foreach (MeshInstance3D? layer in new[] { _rain, _snow })
        {
            if (layer?.MaterialOverride is not ShaderMaterial material)
            {
                continue;
            }

            if (windChanged)
            {
                material.SetShaderParameter(WindName, wind);
            }

            if (brightnessChanged)
            {
                material.SetShaderParameter(BrightnessName, brightness);
            }
        }

        Visible = rainCount > 0 || snowCount > 0;
    }
}
