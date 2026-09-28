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

    /// <summary>
    /// The value at or below which the guest draws no weather at all — `flt_820992D8`, read as
    /// `3D 4C CC CD`. Ours was 0.001, so a theme stating a trace of snow drew a full field of it.
    /// </summary>
    public const float WeatherGate = 0.05f;

    /// <summary>
    /// The level's wind as a world-space velocity — the direction the rain and the plants
    /// take, times the theme's strength — which is what the guest hands its cloth
    /// (<c>sub_821D1FD8</c>: level+0xD38 direction × level+0xD44 speed). Set with the level.
    /// </summary>
    public static Vector3 WindWorld { get; set; } = Vector3.Zero;

    /// <summary>The remaster's mapping of rain density onto streaks; zero under the guest's gate.</summary>
    public static int RainCount(float rainDensity, float rainSize)
    {
        if (rainSize <= WeatherGate || rainDensity <= WeatherGate)
        {
            return 0;
        }

        return Math.Clamp((int)(rainDensity * RainDensityToCount), 0, MaxRain);
    }

    /// <summary>A fixed field of flakes whenever the guest's gate passes; density is fall rate.</summary>
    public static int SnowCount(float snowFallSpeed, float snowSize)
    {
        // THE GUEST'S OWN GATE, AND ITS ONLY ONE. Both draws test the blended theme value
        // against flt_820992D8 — bytes `3D 4C CC CD`, exactly 0.05 — and skip the entire pass
        // below it: snow on SnowSize at 0x821bdf00 (`fcmpu cr6, f29, f0` / `bgt` / `li r10, 0`
        // / `beq` past the whole draw), rain on RainDensity at 0x82280450. There is NO ceiling
        // test, no raycast, no portal, no room or interior flag anywhere on that path; the
        // strings HasSky, IsInside and NoWeather are not in the image, and the engine's own
        // "am I indoors" query has no caller on it. An interior is dry because its BLENDED
        // theme states nothing — which is why the node follows the guest's blended record
        // (`Engine_WeatherNode`) rather than the level's day set. Do not add an interior test
        // here — carry the guest's threshold.
        bool hasSnow = snowFallSpeed > WeatherGate && snowSize > WeatherGate;
        return Math.Clamp(hasSnow ? (int)SnowBaseCount : 0, 0, MaxSnow);
    }

    /// <summary>
    /// Builds the precipitation node. With a guest hosted the mesh holds full capacity for both
    /// kinds and the node exists even when the level's own day set states no weather — a
    /// painted set can bring snow to a level whose day set has none, and the blended record
    /// the guest reads is what decides, per tick, in <see cref="Engine_WeatherNode"/>. In the
    /// editor the level's values are all there is: the mesh is sized to them, and null means
    /// none.
    /// </summary>
    public static MeshInstance3D? Build(WeatherTheme theme, float windAzimuth, float windStrength)
    {
        float rainDensity = Math.Clamp(theme.HasRain ? theme.RainDensity : 0f, 0f, 1f);
        float rainSize = Math.Clamp(theme.HasRain ? theme.RainSize : 0f, 0f, 4f);
        float snowSpeed = Math.Clamp(theme.HasSnow ? theme.SnowFallSpeed : 0f, 0f, 6f);
        float snowSize = Math.Clamp(theme.HasSnow ? theme.SnowSize : 0f, 0f, 4f);

        int rainCount = RainCount(rainDensity, rainSize);
        int snowCount = SnowCount(snowSpeed, snowSize);

        bool live = Refii.Host.HostBridge.TryGetAbiVersion() is not null;
        if (!live && rainCount == 0 && snowCount == 0)
        {
            return null;
        }

        var shader = GD.Load<Shader>(ShaderPath);
        if (shader is null)
        {
            GD.Print($"[weather] shader missing at {ShaderPath}");
            return null;
        }

        int rainCapacity = live ? MaxRain : rainCount;
        int snowCapacity = live ? MaxSnow : snowCount;
        ArrayMesh mesh = BuildQuads(rainCapacity, snowCapacity);

        var material = new ShaderMaterial { Shader = shader };

        float strength = Math.Clamp(windStrength, 0f, 40f);
        material.SetShaderParameter("wind_vec", new Vector4(
            MathF.Cos(windAzimuth) * strength, 0f, MathF.Sin(windAzimuth) * strength, 0f));
        material.SetShaderParameter("area", new Vector4(30f, 24f, 22f, 18f));

        var node = new Engine_WeatherNode
        {
            Name = "Weather",
            Mesh = mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384f,
            RainCapacity = rainCapacity,
            SnowCapacity = snowCapacity,
        };
        node.Attach(material);
        node.Apply(rainDensity, rainSize, snowSpeed, snowSize, "level day set");
        return node;
    }

    /// <summary>
    /// One quad per particle: rain first, then snow. UV2 carries (index within its kind,
    /// kind) so the shader can draw the first N of each independently and the snow's seeds
    /// do not shift as the rain count moves.
    /// </summary>
    private static ArrayMesh BuildQuads(int rain, int snow)
    {
        int particles = rain + snow;
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
            bool isRain = p < rain;
            var id = new Vector2(isRain ? p : p - rain, isRain ? 0f : 1f);
            for (int c = 0; c < 6; c++)
            {
                int v = (p * 6) + c;
                vertices[v] = Vector3.Zero;
                uvs[v] = corners[c];
                ids[v] = id;
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
