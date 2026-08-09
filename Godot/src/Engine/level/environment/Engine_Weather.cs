using System;
using Godot;

namespace Refii.Level;

public static class Engine_Weather
{
    public const string ShaderPath = "res://src/Engine/level/environment/weather.gdshader";

    public const int MaxRain = 20000;

    public const int MaxSnow = 8000;

    private const float RainDensityToCount = 15000f;

    private const float SnowBaseCount = 4096f;

    public static MeshInstance3D? Build(WeatherTheme theme, float windAzimuth, float windStrength)
    {
        float rainDensity = Math.Clamp(
            theme.HasRain ? theme.RainDensity : 0f, 0f, 1f);
        float rainSize = Math.Clamp(theme.HasRain ? theme.RainSize : 0f, 0f, 4f);
        float snowSpeed = Math.Clamp(theme.HasSnow ? theme.SnowFallSpeed : 0f, 0f, 6f);
        float snowSize = Math.Clamp(theme.HasSnow ? theme.SnowSize : 0f, 0f, 4f);

        bool hasSnow = snowSpeed > 0.001f && snowSize > 0.001f;

        int rainCount = Math.Clamp((int)(rainDensity * RainDensityToCount), 0, MaxRain);
        if (rainSize <= 0.001f)
        {
            rainCount = 0;
        }

        int snowCount = Math.Clamp(hasSnow ? (int)SnowBaseCount : 0, 0, MaxSnow);

        if (rainCount == 0 && snowCount == 0)
        {
            return null;
        }

        var shader = GD.Load<Shader>(ShaderPath);
        if (shader is null)
        {
            GD.Print($"[weather] shader missing at {ShaderPath}");
            return null;
        }

        ArrayMesh mesh = BuildQuads(rainCount + snowCount);

        var material = new ShaderMaterial { Shader = shader };

        float strength = Math.Clamp(windStrength, 0f, 40f);
        material.SetShaderParameter("wind_vec", new Vector4(
            MathF.Cos(windAzimuth) * strength, 0f, MathF.Sin(windAzimuth) * strength, 0f));

        material.SetShaderParameter("rain_p", new Vector4(
            rainCount, MathF.Max(rainSize, 0.15f), 14.0f, 0.34f));

        material.SetShaderParameter("snow_p", new Vector4(
            snowCount, MathF.Max(snowSize, 0.15f), MathF.Max(snowSpeed, 0.15f), 1.0f));

        material.SetShaderParameter("area", new Vector4(30f, 24f, 22f, 18f));

        GD.Print($"[weather] {rainCount} rain, {snowCount} snow particles");

        return new MeshInstance3D
        {
            Name = "Weather",
            Mesh = mesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384f,
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
        mesh.SurfaceSetName(0, "Weather");
        mesh.CustomAabb = new Aabb(new Vector3(-4096f, -4096f, -4096f),
                                   new Vector3(8192f, 8192f, 8192f));
        return mesh;
    }
}
