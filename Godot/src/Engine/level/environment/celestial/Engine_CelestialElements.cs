using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

public static class Engine_CelestialElements
{
    // Shader parameter names as StringNames: a string literal converts to a fresh
    // finalizable StringName on every call.
    private static readonly StringName Param_element_colour = "element_colour";
    private static readonly StringName Param_element_dir = "element_dir";
    private static readonly StringName Param_element_distance = "element_distance";
    private static readonly StringName Param_element_facing_power = "element_facing_power";
    private static readonly StringName Param_element_half = "element_half";
    private static readonly StringName Param_element_mode = "element_mode";
    private static readonly StringName Param_element_premultiply = "element_premultiply";
    private static readonly StringName Param_element_tex = "element_tex";
    private static readonly StringName Param_occlusion_distance = "occlusion_distance";
    private static readonly StringName Param_occlusion_half = "occlusion_half";
    private static readonly StringName Param_star_brightness = "star_brightness";
    private static readonly StringName Param_star_phase_rate = "star_phase_rate";
    private static readonly StringName Param_star_point_size = "star_point_size";
    private static readonly StringName Param_star_radius = "star_radius";

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

    /// <summary>Blob 0121 sunbeams: `texel.rgb * (1/6) * g_SunColour.rgb` — the attenuation is
    /// the shader's own literal, carried here because our shader does not have it.</summary>
    public const float SunBeamsColourScale = 1.0f / 6.0f;

    public const float SunBeamsDistance = 1900.0f;

    public const float SunBeamsSizeScale = 600.0f;

    public const float SunGlareDistance = 1999.0f;

    public const float SunGlareSizeScale = 2000.0f;

    public const float IntensityGate = 0.000099999997f;

    public const int StarCount = 512;

    public const float StarRadius = 2500.0f;

    public const float StarPointSize = 1.5f;

    public const float StarPhaseRate = 2.5f;


    /// <summary>
    /// The moon bloom's CPU-SIDE scale: `flt_833210CC = 0x3CCCCCCD = 0.025`, read out of the
    /// image. `Render_LitDecalDraw_INFERRED` @0x821D57E0 builds pixel-shader constant 244 as
    /// `0.025 * MOON_GLARE_INTENSITY * fade^2` (0x821d5e3c loads the literal, 0x821d5e78/7c do
    /// the two multiplies, 0x821d5e40 selects index 244), alpha 0.
    ///
    /// NOT 0.2. That value came from blob 0120 "moonbloom"'s own internal literal
    /// (`texel.rgb * 0.2 * texel.a`), but the binding table points the moon glare at pixel
    /// shader 0x8C — the SHARED glare shader the sun uses — so 0120 may not be the one that
    /// runs. What is certain is the constant the CPU writes, and that is this.
    /// </summary>
    public const float MoonGlareCpuScale = 0.025f;

    public const int DefaultMoonPhase = 4;

    public const float CullExtent = 100000.0f;

    private static readonly AtmosphereState _frameAtmosphere = new();

    private static float[]? _elementCache;
    private static SkyTheme? _elementCacheFor;

    /// <summary>
    /// The theme's sixteen element parameters, cached against the theme they came from.
    /// </summary>
    /// <remarks>
    /// Called two or three times a frame — the celestial update, EnsureElements, and the sun
    /// sources. A theme is a class and changes only when the region re-evaluates, so reference
    /// identity is the key.
    /// </remarks>
    public static float[] ElementParams(SkyTheme theme)
    {
        if (_elementCache is { } cached && ReferenceEquals(_elementCacheFor, theme))
        {
            return cached;
        }

        float[] built = BuildElementParams(theme);
        _elementCache = built;
        _elementCacheFor = theme;
        return built;
    }

    private static float[] BuildElementParams(SkyTheme theme) => new[]
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

        // The moon glare is not a scene draw. The guest renders MOONBLOOM (PS 144) into the
        // transient half-res target that `sub_82277380` DOWNSCALE2s into off_8331A9CC — the
        // glow layer the bright pass sums after its threshold — so the glare blooms but never
        // brightens the sky it sits on. It is published to the halo layer from Update; a
        // billboard left by an older build is freed.
        if (container.GetNodeOrNull<MeshInstance3D>("MoonGlare") is { } staleGlare)
        {
            staleGlare.QueueFree();
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

        // Reused: `AtmosphereState` holds thirteen arrays and this runs every frame. Every field
        // is overwritten by the compute, so nothing carries over.
        AtmosphereState state = _frameAtmosphere;
        Engine_SkyAtmosphere.Compute(Engine_SkyAtmosphere.FromFrame(frame, theme), state);
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
                    material.SetShaderParameter(Param_element_dir, sun);
                    material.SetShaderParameter(Param_element_half, new Vector2(
                        element[11] * SunBeamsSizeScale,
                        element[12] * SunBeamsSizeScale));
                    float beams = element[13] * SunBeamsColourScale;
                    material.SetShaderParameter(Param_element_colour, new Vector4(
                        sunTint[0] * beams,
                        sunTint[1] * beams,
                        sunTint[2] * beams, 1f));
                    // ALWAYS WHEN LIT — the billboard is the scene's sun, and the post-FX
                    // rays are a radial blur OF THE SCENE, so this is their source. Standing it
                    // down while the chain ran left the rays with nothing real to streak and
                    // the chain synthesized a source instead, which drew through cave walls.
                    mesh.Visible = sunActive && element[13] >= IntensityGate;
                    break;
                case "SunDisc":
                    material.SetShaderParameter(Param_element_dir, sun);
                    material.SetShaderParameter(Param_element_half, new Vector2(
                        element[6] * SunDiscSizeScale,
                        element[6] * SunDiscSizeScale));
                    // THE ATMOSPHERE IS A FACTOR OF THE DISC. The guest's pixel constant for
                    // the disc is `sky[144] * SUN_DISC_COLOUR * SUN_DISC_INTENSITY`
                    // (`Render_QueryGatedEffectDraw_INFERRED_2`, 0x821D8658), and sky[144] is the
                    // sun colour after `sub_82263570`'s Rayleigh/Mie attenuation by the sun's
                    // zenith angle — the same tint the beams and glare take here. Without it
                    // the disc is the authored colour at full intensity whenever it is above
                    // the horizon: the huge flat white ball in the night sky.
                    material.SetShaderParameter(Param_element_colour, new Vector4(
                        sunTint[0] * element[8] * element[7],
                        sunTint[1] * element[9] * element[7],
                        sunTint[2] * element[10] * element[7], 1f));
                    mesh.Visible = sunActive && element[7] >= IntensityGate;
                    break;
                case "SunGlare":
                    material.SetShaderParameter(Param_element_dir, sun);
                    material.SetShaderParameter(Param_element_half, new Vector2(
                        element[15] * SunGlareSizeScale,
                        element[15] * SunGlareSizeScale));
                    material.SetShaderParameter(Param_element_colour, new Vector4(
                        sunTint[0] * element[14],
                        sunTint[1] * element[14],
                        sunTint[2] * element[14], 1f));
                    material.SetShaderParameter(Param_occlusion_half,
                        element[6] * SunDiscSizeScale);
                    material.SetShaderParameter(Param_occlusion_distance, SunDiscDistance);
                    mesh.Visible = sunActive && element[14] >= IntensityGate;
                    break;
                case "Moon":
                    material.SetShaderParameter(Param_element_dir, moon);
                    material.SetShaderParameter(Param_element_half, new Vector2(
                        element[1] * MoonSizeScale,
                        element[1] * MoonSizeScale));
                    material.SetShaderParameter(Param_element_colour, new Vector4(
                        0.5f * element[0],
                        0.5f * element[0],
                        0.5f * element[0],
                        element[4]));
                    mesh.Visible = element[0] >= IntensityGate;
                    break;
                case "MoonGlare":
                    // Stale billboard from an older build; the glare is a halo now.
                    mesh.Visible = false;
                    break;

                case "MoonGlareUnused":
                    material.SetShaderParameter(Param_element_dir, moon);
                    material.SetShaderParameter(Param_element_half, new Vector2(
                        element[3] * MoonGlareSizeScale,
                        element[3] * MoonGlareSizeScale));
                    // THE SCALE WAS 8x TOO BIG, WHICH IS WHAT BLEW THE MOON OUT — and the
                    // intensity term is real, so do not drop it.
                    //
                    // The guest builds this as `0.025 * MOON_GLARE_INTENSITY * fade^2`
                    // (0x821D57E0, constant 244). We had `0.2 * MOON_GLARE_INTENSITY`, and the
                    // authored night values are TENS, not ~1 — from `environmentthemes.gdb`,
                    // MoonGlareIntensity is 20 at Bower Lake midnight and 30/40 at Bowerstone
                    // Market against 1 in daylight. So we emitted 4.0-8.0 where the guest emits
                    // 0.5, on a bloom that legitimately spans ~44 degrees (MoonGlareSize 1.3 x
                    // 300 at distance 1000): a white ball filling the screen.
                    //
                    // `fade^2` is the moon's own occlusion query, `clamp(pixels / (MoonSize^2 *
                    // 6000))` squared (0x82279a94..0x82279ac4, divisor flt_82000C28 = 6000).
                    // Our shader's `vis * vis` already IS that term, so it is not applied here.
                    material.SetShaderParameter(Param_element_premultiply, 1.0f);
                    float glareScale = MoonGlareCpuScale * element[2];
                    material.SetShaderParameter(Param_element_colour, new Vector4(
                        glareScale, glareScale, glareScale, 1f));
                    material.SetShaderParameter(Param_occlusion_half,
                        element[1] * MoonSizeScale);
                    material.SetShaderParameter(Param_occlusion_distance,
                        MoonBillboardDistance);
                    mesh.Visible =
                        element[0] >= IntensityGate && element[2] >= IntensityGate;
                    break;
                case "Stars":
                    material.SetShaderParameter(Param_star_brightness, element[5]);
                    mesh.Visible = element[5] >= IntensityGate;
                    break;
            }
        }

        PublishMoonGlare(container, theme, element, moon, read ?? Refii.Models.Engine_BnkMeshLoader.ReadBytes);
    }

    private static readonly Dictionary<uint, Texture2D?> GlareTextures = new();

    /// <summary>
    /// The moon's glare as a sky halo: direction, the guest's distance and size
    /// (MoonGlareSize × 300 at 1000), colour <c>0.025 × MoonGlareIntensity</c> (constant 244,
    /// `Render_LitDecalDraw_INFERRED` 0x821d5e3c..0x821d5e7c; the fade² term is the halo
    /// node's visibility), premultiplied by the sprite's alpha in the halo shader.
    /// </summary>
    private static void PublishMoonGlare(
        Node3D container, SkyTheme theme, float[] element, Vector3 moon, Func<string, byte[]?> read)
    {
        if (!theme.HasMoonGlareTexture)
        {
            Refii.Fx.Engine_PostFxHalos.PublishSky("MoonGlare", default);
            return;
        }

        if (!GlareTextures.TryGetValue(theme.MoonGlareTextureHash, out Texture2D? sprite))
        {
            sprite = Load(theme.MoonGlareTextureHash, read, "moon glare");
            GlareTextures[theme.MoonGlareTextureHash] = sprite;
        }

        float glareScale = MoonGlareCpuScale * element[2];
        bool active = sprite is not null &&
            element[0] >= IntensityGate && element[2] >= IntensityGate && moon.LengthSquared() > 1e-12f;
        Refii.Fx.Engine_PostFxHalos.PublishSky("MoonGlare", new Refii.Fx.Engine_PostFxHalos.SkyHalo(
            moon, MoonGlareDistance, element[3] * MoonGlareSizeScale,
            new Vector3(glareScale, glareScale, glareScale), sprite, active));
        if (active)
        {
            Refii.Fx.Engine_PostFxHalos.Ensure(container);
        }
    }

    /// <summary>
    /// The direction for a celestial element, in WORLD space.
    ///
    /// The elements are billboards placed by `celestial.gdshader`, whose vertex stage does
    /// `VIEW_MATRIX * vec4(element_dir, 0)` — world to view. It never touches MODEL_MATRIX, so
    /// the region root's Scale(-1, 1, 1) does NOT apply to this direction and it must not be
    /// pre-compensated for it. Feeding region-local here mirrored the disc away from the
    /// post-FX glow and drew two suns.
    /// </summary>
    private static Vector3 ToGodot(float[] direction) =>
        Refii.Level.Engine_SkyFrame.ToGodot(direction);

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
        material.SetShaderParameter(Param_element_tex, texture);
        material.SetShaderParameter(Param_element_dir, direction);
        material.SetShaderParameter(Param_element_distance, distance);
        material.SetShaderParameter(Param_element_half, new Vector2(halfWidth, halfHeight));
        material.SetShaderParameter(Param_element_colour,
            new Vector4(colour.X, colour.Y, colour.Z, alpha));
        material.SetShaderParameter(Param_element_facing_power, facingPower);
        material.SetShaderParameter(Param_element_mode, mode);

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
        material.SetShaderParameter(Param_star_brightness, brightness);
        material.SetShaderParameter(Param_star_radius, StarRadius);
        material.SetShaderParameter(Param_star_point_size, StarPointSize);
        material.SetShaderParameter(Param_star_phase_rate, StarPhaseRate);

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
