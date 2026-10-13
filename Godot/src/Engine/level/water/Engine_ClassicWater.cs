using System;
using System.Collections.Generic;
using Godot;
using Refii.Models;

namespace Refii.Level;

/// <summary>
/// The classic renderer's water: a body's patch mesh drawn with the title's own OCEAN_WATER
/// pair, its constants from the day-set theme as the title fills them.
/// </summary>
/// <remarks>
/// THE TITLE'S DRAW (<c>sub_82B3C548</c>, <c>sub_82B3CDE8</c>): a WATERPATCH body draws with
/// VSHADER_OCEAN_WATER and PSHADER_OCEAN_WATER in two passes. The first, before the landscape
/// foreground, has no blend and writes depth: the body's core. The second, after the opaques, is
/// PSHADER_OCEAN_WATER_BLEND over SRCALPHA/INVSRCALPHA: the fringe. THE SHADERS PARTITION THE
/// SHORE THEMSELVES: the pair differ in one instruction, the core killing a pixel whose edge alpha
/// is below one (`sgt(1, a)`) and the fringe one whose alpha is exactly one (`seq(a, 1)`). Here
/// both are transparent materials (the refraction reads Godot's screen texture, which an opaque
/// material cannot): the core forces its alpha to one, the fringe blends at its own.
///
/// g_WaterConstants is filled per draw from the theme (<c>sub_821E4C88</c>, slots 104..132):
/// the body's own 37 parameters are dead at runtime. Phases accumulate speed x dt
/// (<c>sub_822B9D70</c>); the specular lanes are zero; the glitter bool follows its strength.
/// Textures: the body's mask as the vertex texture and g_WaterMaskSampler (1 = land, 0 = water,
/// as <c>sub_82B3B4F8</c> fills it), the normal map, the mirror as g_ReflectionSampler, the
/// screen as g_RefractionSampler, and a white g_ScreenShadowMap (no screen shadow mask here).
/// The theme's WATER_NORMAL_MAP texture slot is not carried by the host's theme yet, so the
/// body's stated normal map stands in; the vista ocean (SKY_OCEANWATER) keeps the modern shader.
/// </remarks>
public static class Engine_ClassicWater
{
    /// <summary>Marks a classic water material (the core pass; the fringe is its next pass).</summary>
    public static readonly StringName Meta = "xenos_water";

    private const string VertexEntry = "VSHADER_OCEAN_WATER";
    private const string CoreEntry = "PSHADER_OCEAN_WATER";
    private const string FringeEntry = "PSHADER_OCEAN_WATER_BLEND";
    private const float AlphaReference = 15f / 255f;

    /// <summary>The title's water tiles span 1.5 screens (constant 87 = 2/3); ours span one.</summary>
    private const float TileToScreen = 1.5f;

    /// <summary>
    /// The title's refraction is its own 256x256 render at c20.w = 1 (`sub_82B2FBA8` sets
    /// view+0x707, which `sub_82282320` answers with 1.0), exposed once by the water's
    /// `oC0 *= c20.w`. Ours is Godot's screen texture, the main scene already times E, so the
    /// read is divided by E. The mirror needs nothing: its camera already takes 1.0.
    /// </summary>
    private const string UnexposedRefraction =
        "#define XR_SCREEN_SCALE vec4(vec3(1.0 / max(texelFetch(fable_exposure_tex, ivec2(0, 0), 0).r, 1e-4)), 1.0)";

    private sealed class Surface
    {
        public ShaderMaterial Core = null!;
        /// <summary>The blended second pass; none on the vista ocean.</summary>
        public ShaderMaterial? Fringe;
        /// <summary>The vista ocean (SKY_OCEANWATER), which also takes the sky's overlay.</summary>
        public bool Vista;
        public WaterTheme Theme = WaterTheme.Default;
        public Vector2 Phase0;
        public Vector2 Phase1;
        /// <summary>The theme changed since the constants were last written in full.</summary>
        public bool Dirty = true;
        /// <summary>g_WaterConstants as registers c116..c141: the pixel stage's array whole, the
        /// vertex stage's its first <see cref="VertexRows"/> rows.</summary>
        public readonly Vector4[] Rows = new Vector4[PixelRows];
        public Vector4[] VertexRowsImage = new Vector4[VertexRows];
        /// <summary>The pixel stage's array as it declares it (all rows; the vista ocean's 23).</summary>
        public Vector4[] PixelRowsImage = new Vector4[PixelRows];

        public IEnumerable<ShaderMaterial> Materials()
        {
            yield return Core;
            if (Fringe is not null)
            {
                yield return Fringe;
            }
        }
    }

    // The translated stages declare the block as an array (`ps_g_WaterConstants[26]`,
    // `vs_g_WaterConstants[10]`), so it is written by row, not by member name.
    private const int PixelRows = 26;
    private const int VertexRows = 10;

    private static readonly List<Surface> _surfaces = new();
    private static ImageTexture? _noMask;
    private static float _lastSeconds = float.NaN;

    /// <summary>The classic renderer is on and the bank has the water pair.</summary>
    public static bool Available =>
        Engine_ClassicMaterial.Active &&
        Engine_ClassicShaders.HasEntry(VertexEntry) &&
        Engine_ClassicShaders.HasEntry(CoreEntry) &&
        Engine_ClassicShaders.HasEntry(FringeEntry);

    /// <summary>Live classic water surfaces.</summary>
    public static int Count => _surfaces.Count;

    /// <summary>
    /// The core material (with the fringe as its next pass) for a body whose mesh is
    /// <paramref name="mesh"/>; the mesh's texture coordinates are rewritten to the body mask's
    /// space, which is what the shaders sample. Null when the pair is missing.
    /// </summary>
    public static ShaderMaterial? Build(
        WaterBody body, TerrainMesh mesh, Engine_WaterMaterial.ReadAsset read,
        Engine_WaterMaterial.FindByName? findByName, out string error)
    {
        error = string.Empty;
        Shader? core = Engine_ClassicShaders.Compose(
            VertexEntry, CoreEntry, "unshaded, cull_disabled, blend_mix, depth_draw_always, fog_disabled", 0,
            "#define XR_ALPHA_OUT(a) 1.0\n" + UnexposedRefraction);
        Shader? fringe = Engine_ClassicShaders.Compose(
            VertexEntry, FringeEntry, "unshaded, cull_disabled, blend_mix, depth_draw_never, fog_disabled", 0,
            UnexposedRefraction);
        if (core is null || fringe is null)
        {
            error = "classic water pair did not compose";
            return null;
        }

        // The mask in the body's own grid; its texture coordinates replace the mesh's world ones.
        Engine_WaterMask.Mask? mask = Engine_WaterMask.TryBuild(body);
        float[] uvs = mesh.Uvs;
        if (mask is { } m)
        {
            for (int i = 0; i + 1 < uvs.Length; i += 2)
            {
                uvs[i] = (uvs[i] - m.Bounds.X) * m.Bounds.Z;
                uvs[i + 1] = (uvs[i + 1] - m.Bounds.Y) * m.Bounds.W;
            }
        }
        else
        {
            Array.Clear(uvs);
        }
        Texture2D maskTexture = mask?.Texture ?? NoMask();

        var coreMaterial = new ShaderMaterial { Shader = core, RenderPriority = -2 };
        var fringeMaterial = new ShaderMaterial { Shader = fringe, RenderPriority = -1 };
        coreMaterial.NextPass = fringeMaterial;
        coreMaterial.SetMeta(Meta, true);
        coreMaterial.SetMeta(Engine_ClassicMaterial.ClassicMeta, true);

        Texture2D? normal = Engine_WaterMaterial.LoadTexture(body.NormalMapPath, read, findByName);
        foreach (ShaderMaterial material in new[] { coreMaterial, fringeMaterial })
        {
            material.SetShaderParameter("ps_g_WaterMaskSampler", maskTexture);
            material.SetShaderParameter("vs_s16", maskTexture);
            material.SetShaderParameter("vs_g_MaskSampler", maskTexture);
            if (normal is not null)
            {
                material.SetShaderParameter("ps_g_NormalTextureSampler", normal);
            }
            material.SetShaderParameter("ps_g_ScreenShadowMap", Refii.Models.Engine_ClassicScreenShadow.Mask);
            material.SetShaderParameter("ps_g_Extinction", Engine_ClassicAtmosphere.ExtinctionTexture);
            material.SetShaderParameter("ps_g_InScattering", Engine_ClassicAtmosphere.InScatteringTexture);
            // `sub_82B3CDE8` sets constant 87 = (2/3, 2/3, ., 1): the title's reflection and
            // refraction tiles are rendered 1.5x wider than the screen on both axes, so the shader
            // pulls the screen position in by 2/3, then adds the bump offsets (rows 13, 14) in
            // tile units. Ours both cover the screen exactly (the mirror at the view's FOV and
            // aspect, the refraction Godot's own screen), so the adjustment is 1 and the two
            // offset rows take the 1.5 instead (Write) — the same sample, measured on the screen.
            material.SetShaderParameter("ps_g_FOVAdjustment", new Vector4(1f, 1f, 1f, 1f));
        }
        coreMaterial.SetShaderParameter("xr_spec_constants", 2u);
        coreMaterial.SetShaderParameter("xr_alpha_threshold", AlphaReference);
        fringeMaterial.SetShaderParameter("xr_spec_constants", 0u);

        // ONE WRITER. The level builds on a worker thread; once a surface is published here the
        // main thread's Pump writes its constants every frame, and ShaderMaterial's parameter
        // cache is not safe under two writers (it faulted inside set_shader_parameter). So the
        // builder writes everything first and publishes last, and Apply only stores the theme.
        var surface = new Surface { Core = coreMaterial, Fringe = fringeMaterial };
        Write(surface, 0f);
        lock (_surfaces)
        {
            _surfaces.Add(surface);
        }
        return coreMaterial;
    }

    /// <summary>The day-set theme for a surface built here; the next Pump writes it.</summary>
    public static void Apply(ShaderMaterial core, WaterTheme theme)
    {
        lock (_surfaces)
        {
            foreach (Surface surface in _surfaces)
            {
                if (ReferenceEquals(surface.Core, core))
                {
                    surface.Theme = theme;
                    surface.Dirty = true;
                    return;
                }
            }
        }
    }

    /// <summary>Advances the phases and the per-frame constants. Main thread, once a frame, after the frame feed.</summary>
    public static void Pump(Vector2 viewport)
    {
        float seconds = Engine_ClassicFeed.Seconds;
        float dt = float.IsNaN(_lastSeconds) ? 0f : Math.Clamp(seconds - _lastSeconds, 0f, 1f);
        _lastSeconds = seconds;
        var tile = new Vector4(0f, 0f, 1f / Math.Max(viewport.X, 1f), 1f / Math.Max(viewport.Y, 1f));
        bool sun = Engine_ClassicFeed.TrySun(out Vector3 colour, out Vector3 direction, out _);
        lock (_surfaces)
        {
            for (int i = _surfaces.Count - 1; i >= 0; i--)
            {
                Surface surface = _surfaces[i];
                if (!GodotObject.IsInstanceValid(surface.Core))
                {
                    _surfaces.RemoveAt(i);
                    continue;
                }
                WaterTheme theme = surface.Theme;
                surface.Phase0 += new Vector2(theme[WaterThemeParam.NormalMapSpeed0X], theme[WaterThemeParam.NormalMapSpeed0Y]) * dt;
                surface.Phase1 += new Vector2(theme[WaterThemeParam.NormalMapSpeed1X], theme[WaterThemeParam.NormalMapSpeed1Y]) * dt;
                if (surface.Dirty)
                {
                    surface.Dirty = false;
                    Write(surface, seconds);
                }
                else
                {
                    WritePhases(surface, seconds);
                }
                foreach (ShaderMaterial material in surface.Materials())
                {
                    if (sun)
                    {
                        var dir = new Vector4(direction.X, direction.Y, direction.Z, 0f);
                        var col = new Vector4(colour.X, colour.Y, colour.Z, 1f);
                        material.SetShaderParameter("ps_g_LightDirection", dir);
                        material.SetShaderParameter("ps_g_LightColor", col);
                    }
                }
            }
        }
    }

    /// <summary>The constants that move every frame: the two bump phases and the detail phase.</summary>
    private static void WritePhases(Surface surface, float seconds)
    {
        WaterTheme t = surface.Theme;
        surface.Rows[2] = new Vector4(surface.Phase0.X, surface.Phase0.Y, 0f, 0f);
        surface.Rows[3] = new Vector4(surface.Phase1.X, surface.Phase1.Y, 0f, 0f);
        surface.Rows[6] = new Vector4(t[WaterThemeParam.DetailMapSpeedX] * seconds, t[WaterThemeParam.DetailMapSpeedY] * seconds, 0f, 0f);
        Push(surface);
    }

    /// <summary>Both stages' arrays onto both passes.</summary>
    private static void Push(Surface surface)
    {
        Array.Copy(surface.Rows, surface.VertexRowsImage, surface.VertexRowsImage.Length);
        Array.Copy(surface.Rows, surface.PixelRowsImage, surface.PixelRowsImage.Length);
        foreach (ShaderMaterial material in surface.Materials())
        {
            material.SetShaderParameter("ps_g_WaterConstants", surface.PixelRowsImage);
            material.SetShaderParameter("vs_g_WaterConstants", surface.VertexRowsImage);
        }
    }

    /// <summary>g_WaterConstants as `sub_821E4C88` fills them from the theme, on both passes and both stages.</summary>
    private static void Write(Surface surface, float seconds)
    {
        WaterTheme t = surface.Theme;
        float max = t[WaterThemeParam.EdgeBlendMax], min = t[WaterThemeParam.EdgeBlendMin], bias = t[WaterThemeParam.EdgeBlendBias];
        float range = max - min;
        Vector4 edge = MathF.Abs(range) > 1e-4f
            ? new Vector4((bias + max) / range, 1f / range, bias, 1f / 17f)
            : new Vector4(0f, 1f, 0f, 1f / 17f);
        float shadow = t[WaterThemeParam.ShadowScaleBias];
        float maxRefract = t[WaterThemeParam.MaxRefractionDistance];
        float glitter = t[WaterThemeParam.GlitterStrength];

        static Vector4 Splat(float v) => new(v, v, v, v);

        // `sub_821E4C88`, register c116 + row.
        Vector4[] r = surface.Rows;
        r[0] = Splat(t[WaterThemeParam.FresnelBias]);
        r[1] = Splat(t[WaterThemeParam.ReflectionBias]);
        r[2] = new Vector4(surface.Phase0.X, surface.Phase0.Y, 0f, 0f);
        r[3] = new Vector4(surface.Phase1.X, surface.Phase1.Y, 0f, 0f);
        r[4] = Splat(t[WaterThemeParam.NormalMapScale0]);
        r[5] = Splat(t[WaterThemeParam.NormalMapScale1]);
        r[6] = new Vector4(t[WaterThemeParam.DetailMapSpeedX] * seconds, t[WaterThemeParam.DetailMapSpeedY] * seconds, 0f, 0f);
        r[7] = Vector4.Zero;
        r[8] = Splat(t[WaterThemeParam.DetailMapScale]);
        r[9] = Vector4.Zero;
        r[10] = new Vector4(t[WaterThemeParam.ShallowR], t[WaterThemeParam.ShallowG], t[WaterThemeParam.ShallowB], 0f);
        r[11] = new Vector4(t[WaterThemeParam.DeepR], t[WaterThemeParam.DeepG], t[WaterThemeParam.DeepB], 0f);
        r[12] = Splat(t[WaterThemeParam.NormalScale]);
        r[13] = Splat(t[WaterThemeParam.ReflectionScale] * TileToScreen);
        r[14] = Splat(t[WaterThemeParam.RefractionScale] * TileToScreen);
        r[15] = Splat(t[WaterThemeParam.ReflectionStrength]);
        r[16] = r[17] = r[18] = r[19] = Vector4.Zero;
        r[20] = Splat(t[WaterThemeParam.GlitterBend]);
        r[21] = Splat(glitter);
        r[22] = Splat(t[WaterThemeParam.GlitterPower]);
        r[23] = new Vector4(shadow, 1f - shadow, 0f, 0f);
        r[24] = Splat(MathF.Abs(maxRefract) > 1e-6f ? 1f / maxRefract : 0f);
        r[25] = edge;
        Push(surface);
        uint booleans = glitter > 0f ? 1u << (16 + 8) : 0u;
        foreach (ShaderMaterial material in surface.Materials())
        {
            material.SetShaderParameter("xr_booleans", booleans);
        }
    }

    /// <summary>The sky's overlay onto every vista ocean (the dome calls this when it changes).</summary>
    public static void ApplyVistaOverlay(Texture2D overlay)
    {
        lock (_surfaces)
        {
            foreach (Surface surface in _surfaces)
            {
                if (surface.Vista && GodotObject.IsInstanceValid(surface.Core))
                {
                    surface.Core.SetShaderParameter("ps_g_SkyOverlayTexture0", overlay);
                    surface.Core.SetShaderParameter("ps_g_SkyOverlayTexture1", overlay);
                }
            }
        }
    }

    private const string VistaVertexEntry = "VSHADER_SKY_OCEANWATER";
    private const string VistaPixelEntry = "PSHADER_SKY_OCEANWATER";

    /// <summary>The bank has the vista ocean's pair.</summary>
    public static bool VistaAvailable =>
        Engine_ClassicMaterial.Active &&
        Engine_ClassicShaders.HasEntry(VistaVertexEntry) &&
        Engine_ClassicShaders.HasEntry(VistaPixelEntry);

    /// <summary>
    /// The vista ocean on the title's own SKY_OCEANWATER pair (`sub_821D1790`): a unit cube
    /// with no top face (`sub_821D7550(0x3B)`). The vertex shader places the bottom at
    /// (2·corner - 1)·2850 around the world origin at g_WaterHeight, and the side walls' top rim
    /// at the eye's height around the eye, so the sheet runs to the horizon. It writes depth
    /// (ZWriteEnable 1) and is drawn once, opaque. g_WaterConstants are the block `sub_821E4C88`
    /// fills from the theme, VS rows 0..5 and PS rows 0..22. The theme's WATER_NORMAL_MAP slot
    /// is not carried yet, so the lowest body's stated normal map stands in.
    /// </summary>
    public static MeshInstance3D? BuildVista(float guestHeight, Texture2D? normal, out string error)
    {
        error = string.Empty;
        Shader? shader = Engine_ClassicShaders.Compose(
            VistaVertexEntry, VistaPixelEntry, "unshaded, cull_disabled, depth_draw_opaque, fog_disabled");
        if (shader is null)
        {
            error = "SKY_OCEANWATER did not compose";
            return null;
        }
        var material = new ShaderMaterial { Shader = shader };
        material.SetMeta(Meta, true);
        material.SetMeta(Engine_ClassicMaterial.ClassicMeta, true);
        material.SetShaderParameter("vs_g_WaterHeight", new Vector4(guestHeight, guestHeight, guestHeight, guestHeight));
        if (normal is not null)
        {
            material.SetShaderParameter("ps_g_NormalTextureSampler", normal);
        }
        material.SetShaderParameter("ps_g_Extinction", Engine_ClassicAtmosphere.ExtinctionTexture);
        material.SetShaderParameter("ps_g_InScattering", Engine_ClassicAtmosphere.InScatteringTexture);
        // The sky's zenith depths and overlays, as the dome binds them (`Render_MeshMaterialSubmit_INFERRED_6`).
        material.SetShaderParameter("ps_g_TzenithR", new Vector4(150f, 150f, 150f, 150f));
        material.SetShaderParameter("ps_g_TzenithM", new Vector4(100f, 100f, 100f, 100f));
        material.SetShaderParameter("ps_g_SkyOverlayTextureBlend", Vector4.Zero);
        Texture2D overlay = Engine_ClassicSkyDome.CurrentOverlay;
        material.SetShaderParameter("ps_g_SkyOverlayTexture0", overlay);
        material.SetShaderParameter("ps_g_SkyOverlayTexture1", overlay);

        // The title's corners {0,1}^3, given to the world-space vertex input through
        // XR_GODOT_TO_GUEST_WORLD (its own inverse) on an identity node.
        var positions = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            float x = i & 1, y = (i >> 1) & 1, z = (i >> 2) & 1;
            positions[i] = new Vector3(-x, z, y);
        }
        int[] indices =
        {
            0, 2, 3, 0, 3, 1,
            0, 1, 5, 0, 5, 4,
            2, 6, 7, 2, 7, 3,
            0, 4, 6, 0, 6, 2,
            1, 3, 7, 1, 7, 5,
        };
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = positions;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.CustomAabb = new Aabb(new Vector3(-8192f, -8192f, -8192f), new Vector3(16384f, 16384f, 16384f));
        var node = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            TopLevel = true,
            ExtraCullMargin = 16384f,
        };

        var surface = new Surface
        {
            Core = material,
            Vista = true,
            VertexRowsImage = new Vector4[6],
            PixelRowsImage = new Vector4[23],
        };
        Write(surface, 0f);
        lock (_surfaces)
        {
            _surfaces.Add(surface);
        }
        return node;
    }

    /// <summary>An empty mask: water everywhere, as `sub_82B3B578` fills a body without one.</summary>
    private static ImageTexture NoMask()
    {
        if (_noMask is null)
        {
            var image = Image.CreateEmpty(4, 4, false, Image.Format.Rf);
            image.Fill(new Color(0f, 0f, 0f, 1f));
            _noMask = ImageTexture.CreateFromImage(image);
        }
        return _noMask;
    }
}
