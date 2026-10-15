using System;
using System.Collections.Generic;
using Godot;
using Refii.Lighting;
using Refii.Models;

namespace Refii.Level;

/// <summary>
/// The classic sky's elements as the title draws them: the sun and moon glares (PS 0117) and the
/// stars (0126 / 0127) in the main scene, and the sun beams, moon bloom and sun disc stated to the
/// post chain's layers (<see cref="Engine_ClassicSkyLayers"/>).
/// </summary>
/// <remarks>
/// From the sky owner's slots 10 / 11 (vtable 0x820052E4, main colour pass only) and the
/// layer passes (`sub_821CB750`, `sub_82277380`); constants as the title builds them, theme
/// parameter p = scene+16+16p:
/// - SUN GLARE (`0x8227DEA0`): gate p7 and facing = dot(camera forward, sun) >= 0; c244 = sun
///   colour x p100 x facing^2 x visibility^2; at sun x 1999, half size p101 x 2000; depth off,
///   ONE / ONE; theme slot 5.
/// - MOON GLARE (`0x821D57E0`): gate p67; c244 = 0.025 x p67 x visibility^2; at moon x 1000, half
///   size p68 x 300; depth off, ONE / ONE; slot 7.
/// - STARS (`0x821C4F48`): 512 points the vertex stage places from its index; g_StarParams =
///   (clock, p70, 1.5, 2.5), g_CamPos the eye; gate p70; ONE / ONE, depth test, point size 0.1..64.
/// - SUN BEAMS (layer): at sun x 1900, half (p95 x 600, p96 x 600), c244 = sun colour x p97, slot 2.
/// - MOON BLOOM (layer): at moon x 4000, half p66 x 400, phase u = [p/8, (p+1)/8], slot 6.
/// - SUN DISC (layer): at sun x 1900, half p92 x 300, c245 = sun colour x p93.rgb x p94, slot 3.
/// The visibility^2 terms are the layer draws' occlusion counts, which the post chain turns into
/// <see cref="Engine_ClassicSkyLayers.VisibilityName"/>; the glares take them in their output.
/// </remarks>
public static class Engine_ClassicSkyElements
{
    private const string GlareVertex = "res://src/Engine/level/environment/celestial/classic_celestial_vertex.gdshaderinc";
    private const string GlarePixel = "PSHADER_SUNGLARE";
    private const string StarsVertex = "VSHADER_SKY_STARS";
    private const string StarsPixel = "PSHADER_SKY_STARS";
    private const float Gate = 0.000099999997f;
    private const int StarCount = 512;

    private static readonly NodePath SunGlarePath = "ClassicSunGlare";
    private static readonly NodePath MoonGlarePath = "ClassicMoonGlare";
    private static readonly NodePath StarsPath = "ClassicStars";
    private static readonly StringName ElementDir = "element_dir";
    private static readonly StringName ElementDistance = "element_distance";
    private static readonly StringName ElementHalf = "element_half";
    private static readonly StringName SunColour = "ps_g_SunColour";
    private static readonly StringName GlareTexture = "ps_g_SunGlareTexture";

    private static readonly Dictionary<uint, Texture2D?> Textures = new();

    /// <summary>The classic sky is on and the guest's sky is stated.</summary>
    public static bool Active => Engine_ClassicMaterial.Active && Engine_ClassicTheme.Live;

    /// <summary>Draws the frame's classic sky elements under <paramref name="container"/>. Main thread.</summary>
    public static void Update(Node3D container, Camera3D? camera)
    {
        if (!Active || camera is null)
        {
            return;
        }
        Engine_ClassicSkyLayers.EnsureRegistered();

        Vector3 sun = ToGodot(Engine_ClassicTheme.SunDirection);
        Vector3 moon = ToGodot(Engine_ClassicTheme.MoonDirection);
        Vector3 sunColour = Engine_ClassicTheme.SunColour;
        float sunGate = Engine_ClassicTheme.Param(7);
        int phase = Math.Clamp(Engine_ClassicTheme.MoonPhase, 0, 7);
        var phaseUv = new Vector4(phase * 0.125f, 0f, (phase + 1) * 0.125f, 1f);

        // The glares, main scene.
        float facing = (-camera.GlobalBasis.Z).Dot(sun);
        float sunGlare = Engine_ClassicTheme.Param(100);
        Glare(container, SunGlarePath, 0, sun, 1999f, Engine_ClassicTheme.Param(101) * 2000f,
            sunColour * sunGlare * facing * facing, Engine_ClassicTheme.SlotSunGlare,
            sunGate >= Gate && facing >= 0f);
        float moonGlare = Engine_ClassicTheme.Param(67);
        Glare(container, MoonGlarePath, 1, moon, 1000f, Engine_ClassicTheme.Param(68) * 300f,
            Vector3.One * (0.025f * moonGlare), Engine_ClassicTheme.SlotMoonGlare, moonGlare >= Gate);

        Stars(container, camera);

        // The layers, drawn by the post chain.
        Rid beamsTexture = SlotTexture(Engine_ClassicTheme.SlotSunBeams);
        Rid moonTexture = SlotTexture(Engine_ClassicTheme.SlotMoon);
        Rid discTexture = SlotTexture(Engine_ClassicTheme.SlotSunDisc);
        Vector3 discColour = sunColour * new Vector3(Engine_ClassicTheme.Param(93, 0), Engine_ClassicTheme.Param(93, 1),
            Engine_ClassicTheme.Param(93, 2)) * Engine_ClassicTheme.Param(94);
        Vector3 beamsColour = sunColour * Engine_ClassicTheme.Param(97);
        var beams = new Engine_ClassicSkyLayers.Sprite
        {
            Active = sunGate >= Gate && beamsTexture.IsValid,
            Direction = sun,
            Distance = 1900f,
            Half = new Vector2(Engine_ClassicTheme.Param(95) * 600f, Engine_ClassicTheme.Param(96) * 600f),
            Colour = new Vector4(beamsColour.X, beamsColour.Y, beamsColour.Z, 0f),
            Texture = beamsTexture,
            Uv = new Vector4(0f, 0f, 1f, 1f),
        };
        var bloom = new Engine_ClassicSkyLayers.Sprite
        {
            Active = Engine_ClassicTheme.Param(65) >= Gate && moonTexture.IsValid,
            Direction = moon,
            Distance = 4000f,
            Half = Vector2.One * (Engine_ClassicTheme.Param(66) * 400f),
            Texture = moonTexture,
            Uv = phaseUv,
        };
        var disc = new Engine_ClassicSkyLayers.Sprite
        {
            Active = sunGate >= Gate && discTexture.IsValid,
            Direction = sun,
            Distance = 1900f,
            Half = Vector2.One * (Engine_ClassicTheme.Param(92) * 300f),
            Colour = new Vector4(discColour.X, discColour.Y, discColour.Z, 0f),
            Texture = discTexture,
            Uv = new Vector4(0f, 0f, 1f, 1f),
        };
        Engine_ClassicSkyLayers.Publish(beams, bloom, disc, Engine_ClassicTheme.Param(92), Engine_ClassicTheme.Param(66));
    }

    private static void Glare(Node3D container, NodePath path, int visibility, Vector3 direction, float distance,
        float half, Vector3 colour, int slot, bool on)
    {
        MeshInstance3D? node = container.GetNodeOrNull<MeshInstance3D>(path);
        if (node is null)
        {
            string defines =
                $"global uniform sampler2D {Engine_ClassicSkyLayers.VisibilityName};\n" +
                // c244's visibility^2 term, taken from the layer draw's occlusion count.
                $"#define XR_BEFORE_OUTPUT(c) {{ float xr_v = texelFetch({Engine_ClassicSkyLayers.VisibilityName}, ivec2({visibility}, 0), 0).r; (c).rgb *= xr_v * xr_v; }}\n" +
                "#define XR_ALPHA_OUT(a) 0.0";
            Shader? shader = Engine_ClassicShaders.Compose(GlareVertex, GlarePixel,
                "unshaded, blend_premul_alpha, depth_draw_never, depth_test_disabled, cull_disabled, shadows_disabled, fog_disabled",
                0, defines);
            if (shader is null)
            {
                return;
            }
            var material = new ShaderMaterial { Shader = shader, RenderPriority = 20 };
            node = new MeshInstance3D
            {
                Name = path.ToString(),
                Mesh = Engine_CelestialElements.QuadMesh(path.ToString(), 0f, 1f),
                MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Layers = Engine_ClassicParticles.Layer,
                ExtraCullMargin = 16384f,
            };
            container.AddChild(node);
        }
        var m = (ShaderMaterial)node.MaterialOverride;
        node.Visible = on;
        if (!on)
        {
            return;
        }
        m.SetShaderParameter(ElementDir, direction);
        m.SetShaderParameter(ElementDistance, distance);
        m.SetShaderParameter(ElementHalf, new Vector2(half, half));
        m.SetShaderParameter(SunColour, new Vector4(colour.X, colour.Y, colour.Z, 0f));
        uint hash = Engine_ClassicTheme.TopHash(slot);
        if (hash != 0u && (!node.HasMeta("slot_hash") || (uint)node.GetMeta("slot_hash").AsInt64() != hash) &&
            Load(hash) is { } texture)
        {
            node.SetMeta("slot_hash", (long)hash);
            m.SetShaderParameter(GlareTexture, texture);
        }
    }

    private static void Stars(Node3D container, Camera3D camera)
    {
        float brightness = Engine_ClassicTheme.Param(70);
        MeshInstance3D? node = container.GetNodeOrNull<MeshInstance3D>(StarsPath);
        if (node is null)
        {
            const string defines =
                "#define XR_ALPHA_OUT(a) 0.0\n" +
                "uniform vec2 xr_point_range = vec2(0.1, 64.0);\n" +
                "#define XR_POINT_SIZE(s) clamp((s), xr_point_range.x, xr_point_range.y)";
            Shader? shader = Engine_ClassicShaders.Compose(StarsVertex, StarsPixel,
                "unshaded, blend_premul_alpha, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled", 0, defines);
            if (shader is null || !shader.Code.Contains("POINT_SIZE", StringComparison.Ordinal))
            {
                return;
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[StarCount];
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Points, arrays);
            arrays.Dispose();
            node = new MeshInstance3D
            {
                Name = "ClassicStars",
                Mesh = mesh,
                MaterialOverride = new ShaderMaterial { Shader = shader, RenderPriority = -126 },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Layers = Engine_ClassicParticles.Layer,
                ExtraCullMargin = 16384f,
                // The stage places the points in the guest's world and composes the object matrix
                // with XR_FILE_TO_MODEL: the node is the half-turn that maps that onto Godot's.
                TopLevel = true,
                Transform = new Transform3D(Basis.FromScale(new Vector3(-1f, 1f, -1f)), Vector3.Zero),
            };
            container.AddChild(node);
        }
        node.Visible = brightness >= Gate;
        if (!node.Visible)
        {
            return;
        }
        var m = (ShaderMaterial)node.MaterialOverride;
        float clock = Engine_ClassicFeed.Seconds;
        Vector3 eye = ToGuest(camera.GlobalPosition);
        m.SetShaderParameter(VsStarParamsName, new Vector4(clock, brightness, 1.5f, 2.5f));
        m.SetShaderParameter(VsCamPosName, new Vector4(eye.X, eye.Y, eye.Z, 0f));
        // POINTSIZE_MIN / MAX were pixels of the title's 1280-wide target.
        float scale = camera.GetViewport().GetVisibleRect().Size.X / 1280f;
        m.SetShaderParameter(XrPointRangeName, new Vector2(0.1f * scale, 64f * scale));
    }

    private static Rid SlotTexture(int slot)
    {
        uint hash = Engine_ClassicTheme.TopHash(slot);
        return hash != 0u && Load(hash) is { } texture ? texture.GetRid() : default;
    }

    /// <summary>
    /// A theme slot's texture, decoded off the main thread: a slot that changes with the time of day
    /// cost a 500 ms frame decoding in place. Null until it stands (the element waits a frame or two).
    /// </summary>
    private static Texture2D? Load(uint hash)
    {
        while (Loaded.TryDequeue(out (uint Hash, Texture2D? Texture) done))
        {
            Loading.Remove(done.Hash);
            Textures[done.Hash] = done.Texture;
        }
        if (Textures.TryGetValue(hash, out Texture2D? texture))
        {
            return texture;
        }
        if (Loading.Add(hash))
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                Texture2D? decoded = null;
                try
                {
                    decoded = Engine_EnvironmentTextures.Load(hash, Engine_BnkMeshLoader.ReadBytes, out _);
                }
                catch (Exception)
                {
                    decoded = null;
                }
                Loaded.Enqueue((hash, decoded));
            });
        }
        return null;
    }

    private static readonly HashSet<uint> Loading = new();
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(uint Hash, Texture2D? Texture)> Loaded = new();

    /// <summary>
    /// The sky object's directions are world vectors in the guest's Z-up axes, like every other
    /// guest point: `sub_82263570` builds the sun as the x axis through RotZ(2pi s) . RotY(T59) .
    /// RotZ(T61 + T57) with T60 added in z (Engine_ClassicEditorFrame), and that rule reproduces
    /// the live sky+0xA0 (Rookridge 12.5 h: computed (-0.43, -0.28, 0.86), stated (-0.45, -0.20,
    /// 0.87) — the sun 60 degrees up at noon). They map as world points do, (-x, z, y).
    /// </summary>
    private static Vector3 ToGodot(Vector3 sky) => new(-sky.X, sky.Z, sky.Y);

    private static Vector3 ToGuest(Vector3 godot) => new(-godot.X, godot.Z, godot.Y);

    // Made once: a string literal converts to a new StringName on every call.
    private static readonly StringName VsStarParamsName = "vs_g_StarParams";
    private static readonly StringName VsCamPosName = "vs_g_CamPos";
    private static readonly StringName XrPointRangeName = "xr_point_range";
}
