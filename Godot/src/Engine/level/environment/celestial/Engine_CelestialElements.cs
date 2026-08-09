using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

public static class Engine_CelestialElements
{
    public const string NodeName = "Celestial";

    public const string MixShaderPath =
        "res://src/Engine/level/environment/celestial/celestial.gdshader";

    public const string AdditiveShaderPath =
        "res://src/Engine/level/environment/celestial/celestial_additive.gdshader";

    public const string StarsShaderPath =
        "res://src/Engine/level/environment/celestial/stars.gdshader";

    public const float MoonBillboardDistance = 4000.0f;

    public const float MoonSizeScale = 300.0f;

    public const float MoonPhaseUStep = 0.125f;

    public const float MoonGlareDistance = 1000.0f;

    public const float MoonGlareSizeScale = 300.0f;

    public const float SunDiscDistance = 1900.0f;

    public const float SunDiscSizeScale = 300.0f;

    public const float SunBeamsDistance = 1900.0f;

    public const float SunBeamsSizeScale = 600.0f;

    public const float SunGlareDistance = 1999.0f;

    public const float SunGlareSizeScale = 2000.0f;

    public const float IntensityGate = 0.000099999997f;

    public const int StarCount = 512;

    public const float StarRadius = 2500.0f;

    public const float StarPointSize = 1.5f;

    public const float StarPhaseRate = 2.5f;

    public const float Exposure = 10.0f;

    public const int DefaultMoonPhase = 4;

    public const float CullExtent = 100000.0f;

    public static float[] ElementParams(SkyTheme theme) => new[]
    {
        theme.MoonIntensity,
        theme.MoonSize,
        theme.MoonGlareIntensity,
        theme.MoonGlareSize,
        theme.MoonTransparency,
        theme.StarBrightness,
        theme.SunDiscSize,
        theme.SunDiscIntensity,
        theme.SunDiscColour[0],
        theme.SunDiscColour[1],
        theme.SunDiscColour[2],
        theme.SunBeamsWidth,
        theme.SunBeamsHeight,
        theme.SunBeamsIntensity,
        theme.SunGlareIntensity,
        theme.SunGlareSize,
    };

    public static Node3D? Build(
        SkyTheme theme,
        SkyFrame frame,
        Func<string, byte[]?> read,
        int moonPhase = DefaultMoonPhase)
    {
        if (!theme.HasAny)
        {
            return null;
        }

        Shader? mixShader = GD.Load<Shader>(MixShaderPath);
        Shader? additiveShader = GD.Load<Shader>(AdditiveShaderPath);
        if (mixShader is null || additiveShader is null)
        {
            GD.Print($"[celestial] shader missing at {MixShaderPath} / {AdditiveShaderPath}");
            return null;
        }

        float[] element = ElementParams(theme);
        AtmosphereState state = Engine_SkyAtmosphere.Compute(
            Engine_SkyAtmosphere.FromFrame(frame, theme));
        float[] sunTint = state.SunElementColour;

        Vector3 sun = ToGodot(frame.SunDirection);
        Vector3 moon = ToGodot(frame.MoonDirection);

        var container = new Node3D { Name = NodeName };
        var names = new List<string>();
        int priority = 0;

        if (frame.SkyParams[0] >= IntensityGate)
        {
            if (element[13] >= IntensityGate && theme.HasSunBeamsTexture)
            {
                Texture2D? beams = Load(theme.SunBeamsTextureHash, read, "sun beams");
                if (beams is not null)
                {
                    container.AddChild(Billboard(
                        "SunBeams", additiveShader, beams, sun, SunBeamsDistance,
                        element[11] * SunBeamsSizeScale, element[12] * SunBeamsSizeScale,
                        Scaled(sunTint, element[13] * Exposure), 1f, 0f, 0f, 1f, priority++));
                    names.Add("beams");
                }
            }

            if (theme.HasSunDiscTexture)
            {
                Texture2D? disc = Load(theme.SunDiscTextureHash, read, "sun disc");
                if (disc is not null)
                {
                    float discScale = element[7] * Exposure;
                    container.AddChild(Billboard(
                        "SunDisc", mixShader, disc, sun, SunDiscDistance,
                        element[6] * SunDiscSizeScale, element[6] * SunDiscSizeScale,
                        new Vector3(
                            element[8] * discScale,
                            element[9] * discScale,
                            element[10] * discScale),
                        1f, 0f, 0f, 1f, priority++));
                    names.Add("disc");
                }
            }

            if (element[14] >= IntensityGate && theme.HasSunGlareTexture)
            {
                Texture2D? glare = Load(theme.SunGlareTextureHash, read, "sun glare");
                if (glare is not null)
                {
                    container.AddChild(Billboard(
                        "SunGlare", additiveShader, glare, sun, SunGlareDistance,
                        element[15] * SunGlareSizeScale, element[15] * SunGlareSizeScale,
                        Scaled(sunTint, element[14] * Exposure), 1f, 2f, 0f, 1f, priority++));
                    names.Add("glare");
                }
            }
        }

        if (element[0] >= IntensityGate)
        {
            int phase = Math.Clamp(moonPhase, 0, 7);
            float u0 = phase * MoonPhaseUStep;
            float u1 = (phase + 1) * MoonPhaseUStep;

            if (theme.HasMoonTexture)
            {
                Texture2D? disc = Load(theme.MoonTextureHash, read, "moon");
                if (disc is not null)
                {
                    container.AddChild(Billboard(
                        "Moon", mixShader, disc, moon, MoonBillboardDistance,
                        element[1] * MoonSizeScale, element[1] * MoonSizeScale,
                        new Vector3(
                            0.5f * element[0] * Exposure,
                            0.5f * element[0] * Exposure,
                            element[4] * Exposure),
                        element[4], 0f, u0, u1, priority++));
                    names.Add("moon");
                }
            }

            if (element[2] >= IntensityGate && theme.HasMoonGlareTexture)
            {
                Texture2D? glare = Load(theme.MoonGlareTextureHash, read, "moon glare");
                if (glare is not null)
                {
                    float scale = element[2] * Exposure;
                    container.AddChild(Billboard(
                        "MoonGlare", additiveShader, glare, moon, MoonGlareDistance,
                        element[3] * MoonGlareSizeScale, element[3] * MoonGlareSizeScale,
                        new Vector3(scale, scale, scale), 1f, 0f, 0f, 1f, priority++));
                    names.Add("moon glare");
                }
            }
        }

        if (element[5] >= IntensityGate)
        {
            MeshInstance3D? stars = Stars(element[5], priority++);
            if (stars is not null)
            {
                container.AddChild(stars);
                names.Add("stars");
            }
        }

        if (container.GetChildCount() == 0)
        {
            container.QueueFree();
            return null;
        }

        GD.Print(
            $"[celestial] {string.Join(", ", names)}; " +
            $"sun=({sun.X:0.00},{sun.Y:0.00},{sun.Z:0.00}) " +
            $"moon=({moon.X:0.00},{moon.Y:0.00},{moon.Z:0.00}) " +
            $"tint=({sunTint[0]:0.00},{sunTint[1]:0.00},{sunTint[2]:0.00}) " +
            $"phase={Math.Clamp(moonPhase, 0, 7)}");

        return container;
    }

    public static void Update(Node3D container, SkyTheme theme, SkyFrame frame)
    {
        float[] element = ElementParams(theme);
        AtmosphereState state = Engine_SkyAtmosphere.Compute(
            Engine_SkyAtmosphere.FromFrame(frame, theme));
        float[] sunTint = state.SunElementColour;

        Vector3 sun = ToGodot(frame.SunDirection);
        Vector3 moon = ToGodot(frame.MoonDirection);
        bool day = frame.SunDirection[1] > -0.05f;
        bool night = frame.SunDirection[1] < 0.05f;

        foreach (Node child in container.GetChildren())
        {
            if (child is not MeshInstance3D mesh ||
                mesh.MaterialOverride is not ShaderMaterial material)
            {
                continue;
            }

            switch (child.Name.ToString())
            {
                case "SunBeams":
                    material.SetShaderParameter("element_dir", sun);
                    material.SetShaderParameter("element_colour", new Vector4(
                        sunTint[0] * element[13] * Exposure,
                        sunTint[1] * element[13] * Exposure,
                        sunTint[2] * element[13] * Exposure, 1f));
                    mesh.Visible = day;
                    break;
                case "SunDisc":
                    material.SetShaderParameter("element_dir", sun);
                    mesh.Visible = day;
                    break;
                case "SunGlare":
                    material.SetShaderParameter("element_dir", sun);
                    material.SetShaderParameter("element_colour", new Vector4(
                        sunTint[0] * element[14] * Exposure,
                        sunTint[1] * element[14] * Exposure,
                        sunTint[2] * element[14] * Exposure, 1f));
                    mesh.Visible = day;
                    break;
                case "Moon":
                case "MoonGlare":
                    material.SetShaderParameter("element_dir", moon);
                    break;
                case "Stars":
                    mesh.Visible = night;
                    break;
            }
        }
    }

    private static Vector3 ToGodot(float[] direction) =>
        new(-direction[0], direction[1], direction[2]);

    private static Vector3 Scaled(float[] rgb, float scale) =>
        new(rgb[0] * scale, rgb[1] * scale, rgb[2] * scale);

    private static Texture2D? Load(uint hash, Func<string, byte[]?> read, string label)
    {
        Texture2D? texture = Engine_EnvironmentTextures.Load(hash, read, out string path);
        if (texture is null)
        {
            GD.Print(path.Length == 0
                ? $"[celestial] {label} texture 0x{hash:X8} unresolved"
                : $"[celestial] {label} texture '{path}' did not decode");
        }

        return texture;
    }

    private static MeshInstance3D Billboard(
        string name,
        Shader shader,
        Texture2D texture,
        Vector3 direction,
        float distance,
        float halfWidth,
        float halfHeight,
        Vector3 colour,
        float alpha,
        float facingPower,
        float u0,
        float u1,
        int priority)
    {
        var material = new ShaderMaterial { Shader = shader, RenderPriority = priority };
        material.SetShaderParameter("element_tex", texture);
        material.SetShaderParameter("element_dir", direction);
        material.SetShaderParameter("element_distance", distance);
        material.SetShaderParameter("element_half", new Vector2(halfWidth, halfHeight));
        material.SetShaderParameter("element_colour",
            new Vector4(colour.X, colour.Y, colour.Z, alpha));
        material.SetShaderParameter("element_facing_power", facingPower);

        return new MeshInstance3D
        {
            Name = name,
            Mesh = QuadMesh(name, u0, u1),
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384f,
        };
    }

    private static ArrayMesh QuadMesh(string name, float u0, float u1)
    {
        var corners = new Vector3[]
        {
            new(-1f, 1f, 0f),
            new(1f, 1f, 0f),
            new(-1f, -1f, 0f),
            new(1f, -1f, 0f),
        };

        var quadUvs = new Vector2[]
        {
            new(u0, 0f), new(u1, 0f), new(u0, 1f), new(u1, 1f),
        };

        var order = new[] { 0, 1, 2, 2, 1, 3 };

        var vertices = new Vector3[6];
        var uvs = new Vector2[6];
        for (int v = 0; v < 6; v++)
        {
            vertices[v] = corners[order[v]];
            uvs[v] = quadUvs[order[v]];
        }

        return BuildMesh(name, vertices, uvs);
    }

    private static MeshInstance3D? Stars(float brightness, int priority)
    {
        Shader? shader = GD.Load<Shader>(StarsShaderPath);
        if (shader is null)
        {
            GD.Print($"[celestial] shader missing at {StarsShaderPath}");
            return null;
        }

        var offsets = new Vector2[]
        {
            new(-1f, 1f), new(1f, 1f), new(-1f, -1f),
            new(-1f, -1f), new(1f, 1f), new(1f, -1f),
        };

        var vertices = new Vector3[StarCount * 6];
        var uvs = new Vector2[StarCount * 6];
        for (int star = 0; star < StarCount; star++)
        {
            for (int corner = 0; corner < 6; corner++)
            {
                int v = (star * 6) + corner;
                vertices[v] = new Vector3(star, 0f, 0f);
                uvs[v] = offsets[corner];
            }
        }

        var material = new ShaderMaterial { Shader = shader, RenderPriority = priority };
        material.SetShaderParameter("star_brightness", brightness);
        material.SetShaderParameter("star_radius", StarRadius);
        material.SetShaderParameter("star_point_size", StarPointSize);
        material.SetShaderParameter("star_phase_rate", StarPhaseRate);

        return new MeshInstance3D
        {
            Name = "Stars",
            Mesh = BuildMesh("Stars", vertices, uvs),
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384f,
        };
    }

    private static ArrayMesh BuildMesh(string name, Vector3[] vertices, Vector2[] uvs)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetName(0, name);
        mesh.CustomAabb = new Aabb(
            new Vector3(-CullExtent, -CullExtent, -CullExtent),
            new Vector3(CullExtent * 2f, CullExtent * 2f, CullExtent * 2f));
        return mesh;
    }
}
