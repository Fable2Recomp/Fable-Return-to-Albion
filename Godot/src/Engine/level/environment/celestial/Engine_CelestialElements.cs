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

    public const string OverlayShaderPath =
        "res://src/Engine/level/environment/celestial/celestial_overlay.gdshader";

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

    public const float Exposure = 10.0f / 3.0f;

    public const float MoonGlareFixedScale = 0.025f;

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

        AtmosphereState state = Engine_SkyAtmosphere.Compute(
            Engine_SkyAtmosphere.FromFrame(frame, theme));
        float[] sunTint = state.SunElementColour;

        Vector3 sun = ToGodot(frame.SunDirection);
        Vector3 moon = ToGodot(frame.MoonDirection);

        var container = new Node3D { Name = NodeName };
        EnsureElements(container, theme, read, moonPhase);
        Update(container, theme, frame, read, moonPhase);

        if (container.GetChildCount() == 0)
        {
            container.QueueFree();
            return null;
        }

        var names = new List<string>();
        foreach (Node child in container.GetChildren())
        {
            names.Add(child.Name.ToString());
        }

        GD.Print(
            $"[celestial] {string.Join(", ", names)}; " +
            $"sun=({sun.X:0.00},{sun.Y:0.00},{sun.Z:0.00}) " +
            $"moon=({moon.X:0.00},{moon.Y:0.00},{moon.Z:0.00}) " +
            $"tint=({sunTint[0]:0.00},{sunTint[1]:0.00},{sunTint[2]:0.00}) " +
            $"phase={Math.Clamp(moonPhase, 0, 7)}");

        return container;
    }

    public static void EnsureElements(
        Node3D container,
        SkyTheme theme,
        Func<string, byte[]?> read,
        int moonPhase = DefaultMoonPhase)
    {
        Shader? mixShader = GD.Load<Shader>(MixShaderPath);
        Shader? additiveShader = GD.Load<Shader>(AdditiveShaderPath);
        if (mixShader is null || additiveShader is null)
        {
            return;
        }

        float[] element = ElementParams(theme);
        int priority = container.GetChildCount();

        if (theme.HasSunBeamsTexture &&
            container.GetNodeOrNull<MeshInstance3D>("SunBeams") is null)
        {
            Texture2D? beams = Load(theme.SunBeamsTextureHash, read, "sun beams");
            if (beams is not null)
            {
                container.AddChild(Billboard(
                    "SunBeams", additiveShader, beams, Vector3.Up, SunBeamsDistance,
                    element[11] * SunBeamsSizeScale, element[12] * SunBeamsSizeScale,
                    Vector3.Zero, 1f, 0f, 0f, 1f, priority++));
            }
        }

        if (theme.HasSunDiscTexture &&
            container.GetNodeOrNull<MeshInstance3D>("SunDisc") is null)
        {
            Texture2D? disc = Load(theme.SunDiscTextureHash, read, "sun disc");
            if (disc is not null)
            {
                container.AddChild(Billboard(
                    "SunDisc", mixShader, disc, Vector3.Up, SunDiscDistance,
                    element[6] * SunDiscSizeScale, element[6] * SunDiscSizeScale,
                    Vector3.Zero, 1f, 0f, 0f, 1f, priority++, 1));
            }
        }

        Shader overlayShader = GD.Load<Shader>(OverlayShaderPath) ?? additiveShader;

        if (theme.HasSunGlareTexture &&
            container.GetNodeOrNull<MeshInstance3D>("SunGlare") is null)
        {
            Texture2D? glare = Load(theme.SunGlareTextureHash, read, "sun glare");
            if (glare is not null)
            {
                container.AddChild(Billboard(
                    "SunGlare", overlayShader, glare, Vector3.Up, SunGlareDistance,
                    element[15] * SunGlareSizeScale, element[15] * SunGlareSizeScale,
                    Vector3.Zero, 1f, 2f, 0f, 1f, priority++));
            }
        }

        int phase = Math.Clamp(moonPhase, 0, 7);
        float u0 = phase * MoonPhaseUStep;
        float u1 = (phase + 1) * MoonPhaseUStep;

        if (theme.HasMoonTexture &&
            container.GetNodeOrNull<MeshInstance3D>("Moon") is null)
        {
            Texture2D? moonDisc = Load(theme.MoonTextureHash, read, "moon");
            if (moonDisc is not null)
            {
                container.AddChild(Billboard(
                    "Moon", mixShader, moonDisc, Vector3.Up, MoonBillboardDistance,
                    element[1] * MoonSizeScale, element[1] * MoonSizeScale,
                    Vector3.Zero, element[4], 0f, u0, u1, priority++, 2));
            }
        }

        if (theme.HasMoonGlareTexture &&
            container.GetNodeOrNull<MeshInstance3D>("MoonGlare") is null)
        {
            Texture2D? moonGlare = Load(theme.MoonGlareTextureHash, read, "moon glare");
            if (moonGlare is not null)
            {
                container.AddChild(Billboard(
                    "MoonGlare", overlayShader, moonGlare, Vector3.Up, MoonGlareDistance,
                    element[3] * MoonGlareSizeScale, element[3] * MoonGlareSizeScale,
                    Vector3.Zero, 1f, 0f, 0f, 1f, priority++));
            }
        }

        if (container.GetNodeOrNull<MeshInstance3D>("Stars") is null)
        {
            MeshInstance3D? stars = Stars(element[5], priority++);
            if (stars is not null)
            {
                container.AddChild(stars);
            }
        }
    }

    public static void Update(
        Node3D container,
        SkyTheme theme,
        SkyFrame frame,
        Func<string, byte[]?>? read = null,
        int moonPhase = DefaultMoonPhase)
    {
        EnsureElements(
            container, theme,
            read ?? Refii.Models.Engine_BnkMeshLoader.ReadBytes, moonPhase);

        float[] element = ElementParams(theme);
        AtmosphereState state = Engine_SkyAtmosphere.Compute(
            Engine_SkyAtmosphere.FromFrame(frame, theme));
        float[] sunTint = state.SunElementColour;

        Vector3 sun = ToGodot(frame.SunDirection);
        Vector3 moon = ToGodot(frame.MoonDirection);
        bool sunActive = frame.SkyParams[0] >= IntensityGate;

        if (OS.GetEnvironment("REFII_CELESTIAL_DEBUG").Length != 0)
        {
            GD.Print(
                $"[celestial-dbg] sun=({sun.X:0.00},{sun.Y:0.00},{sun.Z:0.00}) " +
                $"moon=({moon.X:0.00},{moon.Y:0.00},{moon.Z:0.00}) " +
                $"tint=({sunTint[0]:0.00},{sunTint[1]:0.00},{sunTint[2]:0.00}) " +
                $"discCol=({element[8]:0.00},{element[9]:0.00},{element[10]:0.00}) " +
                $"discInt={element[7]:0.000} moonInt={element[0]:0.000} " +
                $"moonTrans={element[4]:0.000} moonGlareInt={element[2]:0.000} " +
                $"sunActive={sunActive}");
        }

        Shader? overlay = null;

        foreach (Node child in container.GetChildren())
        {
            if (child is not MeshInstance3D mesh ||
                mesh.MaterialOverride is not ShaderMaterial material)
            {
                continue;
            }

            string elementName = child.Name.ToString();
            if (elementName is "SunGlare" or "MoonGlare" &&
                material.Shader?.ResourcePath != OverlayShaderPath)
            {
                overlay ??= GD.Load<Shader>(OverlayShaderPath);
                if (overlay is not null)
                {
                    material.Shader = overlay;
                }
            }

            switch (elementName)
            {
                case "SunBeams":
                    material.SetShaderParameter("element_dir", sun);
                    material.SetShaderParameter("element_half", new Vector2(
                        element[11] * SunBeamsSizeScale,
                        element[12] * SunBeamsSizeScale));
                    material.SetShaderParameter("element_colour", new Vector4(
                        sunTint[0] * element[13],
                        sunTint[1] * element[13],
                        sunTint[2] * element[13], 1f));
                    mesh.Visible = sunActive && element[13] >= IntensityGate &&
                        !Refii.Lighting.Engine_PostFxParams.ChainActive;
                    break;
                case "SunDisc":
                    material.SetShaderParameter("element_dir", sun);
                    material.SetShaderParameter("element_half", new Vector2(
                        element[6] * SunDiscSizeScale,
                        element[6] * SunDiscSizeScale));
                    material.SetShaderParameter("element_colour", new Vector4(
                        element[8] * element[7],
                        element[9] * element[7],
                        element[10] * element[7], 1f));
                    mesh.Visible = sunActive && element[7] >= IntensityGate;
                    break;
                case "SunGlare":
                    material.SetShaderParameter("element_dir", sun);
                    material.SetShaderParameter("element_half", new Vector2(
                        element[15] * SunGlareSizeScale,
                        element[15] * SunGlareSizeScale));
                    material.SetShaderParameter("element_colour", new Vector4(
                        sunTint[0] * element[14],
                        sunTint[1] * element[14],
                        sunTint[2] * element[14], 1f));
                    material.SetShaderParameter("occlusion_half",
                        element[6] * SunDiscSizeScale);
                    material.SetShaderParameter("occlusion_distance", SunDiscDistance);
                    mesh.Visible = sunActive && element[14] >= IntensityGate;
                    break;
                case "Moon":
                    material.SetShaderParameter("element_dir", moon);
                    material.SetShaderParameter("element_half", new Vector2(
                        element[1] * MoonSizeScale,
                        element[1] * MoonSizeScale));
                    material.SetShaderParameter("element_colour", new Vector4(
                        0.5f * element[0],
                        0.5f * element[0],
                        0.5f * element[0],
                        element[4]));
                    mesh.Visible = element[0] >= IntensityGate;
                    break;
                case "MoonGlare":
                    material.SetShaderParameter("element_dir", moon);
                    material.SetShaderParameter("element_half", new Vector2(
                        element[3] * MoonGlareSizeScale,
                        element[3] * MoonGlareSizeScale));
                    float glareScale = MoonGlareFixedScale * element[2];
                    material.SetShaderParameter("element_colour", new Vector4(
                        glareScale, glareScale, glareScale, 1f));
                    material.SetShaderParameter("occlusion_half",
                        element[1] * MoonSizeScale);
                    material.SetShaderParameter("occlusion_distance",
                        MoonBillboardDistance);
                    mesh.Visible =
                        element[0] >= IntensityGate && element[2] >= IntensityGate;
                    break;
                case "Stars":
                    material.SetShaderParameter("star_brightness", element[5]);
                    mesh.Visible = element[5] >= IntensityGate;
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
        int priority,
        int mode = 0)
    {
        var material = new ShaderMaterial { Shader = shader, RenderPriority = priority };
        material.SetShaderParameter("element_tex", texture);
        material.SetShaderParameter("element_dir", direction);
        material.SetShaderParameter("element_distance", distance);
        material.SetShaderParameter("element_half", new Vector2(halfWidth, halfHeight));
        material.SetShaderParameter("element_colour",
            new Vector4(colour.X, colour.Y, colour.Z, alpha));
        material.SetShaderParameter("element_facing_power", facingPower);
        material.SetShaderParameter("element_mode", mode);
        material.SetShaderParameter("element_exposure", Exposure);

        return new MeshInstance3D
        {
            Name = name,
            Mesh = QuadMesh(name, u0, u1),
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384f,
        };
    }

    public static ArrayMesh QuadMesh(string name, float u0, float u1)
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
