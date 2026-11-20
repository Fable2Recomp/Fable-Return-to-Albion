using Godot;
using Refii.Textures;

namespace Refii.Level;

public static class Engine_WaterMaterial
{
    private static readonly StringName GuestWaterName = "guest_water";
    private static readonly StringName HasWaterMaskName = "has_water_mask";
    private static readonly StringName NormalMapName = "normal_map";
    private static readonly StringName WBiasName = "w_bias";
    private static readonly StringName WDeepName = "w_deep";
    private static readonly StringName WEdgeName = "w_edge";
    private static readonly StringName WGlitName = "w_glit";
    private static readonly StringName WMaskBoundsName = "w_mask_bounds";
    private static readonly StringName WPhaseName = "w_phase";
    private static readonly StringName WReflpName = "w_reflp";
    private static readonly StringName WScaleName = "w_scale";
    private static readonly StringName WSpeedName = "w_speed";
    private static readonly StringName WSurfaceName = "w_surface";
    private static readonly StringName WaterMaskName = "water_mask";

    public const string ShaderPath = "res://src/Engine/level/water/water.gdshader";

    public static bool GuestMode =>
        OS.GetEnvironment("REFII_WATER").Equals("guest", System.StringComparison.OrdinalIgnoreCase);

    public delegate byte[]? ReadAsset(string virtualPath);

    public delegate string? FindByName(string fileName);

    public static ShaderMaterial? Build(
        WaterBody body, ReadAsset read, FindByName? findByName, out string error)
    {
        error = string.Empty;

        var shader = Refii.AndroidRuntime.AndroidWaterZenith.Load(ShaderPath);
        if (shader is null)
        {
            error = $"water shader missing at {ShaderPath}";
            return null;
        }

        var material = new ShaderMaterial { Shader = shader };

        if (GuestMode)
        {
            material.SetShaderParameter(GuestWaterName, true);
        }

        Texture2D? normalMap = LoadTexture(body.NormalMapPath, read, findByName);
        if (normalMap is not null)
        {
            material.SetShaderParameter(NormalMapName, normalMap);
        }
        else
        {
            // SAY SO. Without the wave normals this shader has nothing left that varies across the
            // surface — the reflection is sampled through an unperturbed normal, the glint's
            // half-vector never moves, and the body renders as a flat sheet of sky colour. That
            // reads as "the water shader is wrong" rather than as one missing texture.
            //
            // The path being EMPTY is the case that was invisible: `LoadTexture` returns null
            // before its own "not found" print when there is nothing to look for, so a body that
            // states no normal map at all produced silence and a flat surface.
            GD.Print(
                body.NormalMapPath is { Length: > 0 } stated
                    ? $"[water] no normal map bound; stated path was '{stated}'"
                    : "[water] no normal map bound; the body states no NormalMapPath at all");
        }

        Apply(material, WaterTheme.Default);

        // THE SHORELINE. Built once here rather than per frame: the coverage is authored geometry
        // and never moves, so only the ramp that reads it moves with time of day.
        //
        // A body with no usable tiles keeps `has_water_mask` false and stays opaque, which is the
        // guest's own empty-mask behaviour — water everywhere — rather than a fade invented from
        // whatever geometry happens to be nearby.
        Engine_WaterMask.Mask? mask = Engine_WaterMask.TryBuild(body);
        if (mask is { } built)
        {
            material.SetShaderParameter(WaterMaskName, built.Texture);
            material.SetShaderParameter(WMaskBoundsName, built.Bounds);
            material.SetShaderParameter(HasWaterMaskName, true);
        }

        // NO SUN HERE. It used to be a hardcoded (0.3, 0.7, 0.5) with a colour ramped off its own
        // Y, which fixed the glint in one corner of every lake for the whole day. The main light
        // is stated per frame and arrives as an instance parameter from
        // Engine_LevelBakedLighting.ApplyInstanceLighting, which is the same path the props' bake
        // takes and the same light Engine_LevelLighting.UpdateSun aims — see the shader for the
        // upload that proves the water draw reads that light and no other.
        return material;
    }

    private sealed class Phase
    {
        public Vector4 Speed;
        public Vector4 Value;
        public bool HasTheme;
        public Vector4 Bias;
        public Vector4 Scale;
        public Vector4 Surface;
        public Vector4 Deep;
        public Vector4 Reflp;
        public Vector4 Glit;
        public Vector4 Edge;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ShaderMaterial, Phase> Phases = new();

    /// <summary>
    /// Advances every water surface's normal-map scroll by its current speed, as the title does
    /// (`sub_822B9D70`: phase += speed x dt, rows 2 and 3). Main thread, once a frame.
    /// </summary>
    public static void AdvancePhases(Node3D? water, float delta)
    {
        if (water is null)
        {
            return;
        }
        // By index: GetChildren() is a Godot Array, a finalizable handle per frame.
        for (int i = 0, count = water.GetChildCount(); i < count; i++)
        {
            Node child = water.GetChild(i);
            if (child is MeshInstance3D mesh && mesh.MaterialOverride is ShaderMaterial material &&
                Phases.TryGetValue(material, out Phase? phase))
            {
                Vector4 v = phase.Value + (phase.Speed * delta);
                phase.Value = new Vector4(v.X - Mathf.Floor(v.X), v.Y - Mathf.Floor(v.Y), v.Z - Mathf.Floor(v.Z), v.W - Mathf.Floor(v.W));
                material.SetShaderParameter(WPhaseName, phase.Value);
            }
        }
    }

    public static void Apply(ShaderMaterial material, WaterTheme theme)
    {
        Phase phase = Phases.GetOrCreateValue(material);
        bool first = !phase.HasTheme;

        var bias = new Vector4(
            theme[WaterThemeParam.FresnelBias],
            theme[WaterThemeParam.ReflectionBias],
            theme[WaterThemeParam.NormalScale],
            theme[WaterThemeParam.MaxRefractionDistance]);
        if (first || phase.Bias != bias)
        {
            phase.Bias = bias;
            material.SetShaderParameter(WBiasName, bias);
        }

        var speed = new Vector4(
            theme[WaterThemeParam.NormalMapSpeed0X],
            theme[WaterThemeParam.NormalMapSpeed0Y],
            theme[WaterThemeParam.NormalMapSpeed1X],
            theme[WaterThemeParam.NormalMapSpeed1Y]);
        if (first || phase.Speed != speed)
        {
            phase.Speed = speed;
            material.SetShaderParameter(WSpeedName, speed);
        }

        float normalScale0 = theme[WaterThemeParam.NormalMapScale0];
        float normalScale1 = theme[WaterThemeParam.NormalMapScale1];
        var scale = new Vector4(normalScale0, normalScale0, normalScale1, normalScale1);
        if (first || phase.Scale != scale)
        {
            phase.Scale = scale;
            material.SetShaderParameter(WScaleName, scale);
        }

        var surface = new Vector4(
            theme[WaterThemeParam.ShallowR],
            theme[WaterThemeParam.ShallowG],
            theme[WaterThemeParam.ShallowB],
            0f);
        if (first || phase.Surface != surface)
        {
            phase.Surface = surface;
            material.SetShaderParameter(WSurfaceName, surface);
        }

        var deep = new Vector4(
            theme[WaterThemeParam.DeepR],
            theme[WaterThemeParam.DeepG],
            theme[WaterThemeParam.DeepB],
            theme[WaterThemeParam.ReflectionStrength]);
        if (first || phase.Deep != deep)
        {
            phase.Deep = deep;
            material.SetShaderParameter(WDeepName, deep);
        }

        var reflp = new Vector4(
            theme[WaterThemeParam.ReflectionScale],
            theme[WaterThemeParam.RefractionScale],
            theme[WaterThemeParam.GlitterBend],
            theme[WaterThemeParam.GlitterStrength]);
        if (first || phase.Reflp != reflp)
        {
            phase.Reflp = reflp;
            material.SetShaderParameter(WReflpName, reflp);
        }

        var glit = new Vector4(
            theme[WaterThemeParam.GlitterPower],
            theme[WaterThemeParam.ShadowScaleBias],
            0f, 0f);
        if (first || phase.Glit != glit)
        {
            phase.Glit = glit;
            material.SetShaderParameter(WGlitName, glit);
        }

        Vector4 edge = Engine_WaterTheme.EdgeBlend(theme);
        if (first || phase.Edge != edge)
        {
            phase.Edge = edge;
            material.SetShaderParameter(WEdgeName, edge);
        }

        phase.HasTheme = true;
    }

    public static Texture2D? LoadTexture(string path, ReadAsset read, FindByName? findByName)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string normalized = path.Replace('\\', '/').Trim('/');
        byte[]? bytes = read(normalized) ?? read("data/" + normalized);

        if (bytes is null && findByName is not null)
        {
            int slash = normalized.LastIndexOf('/');
            string name = slash < 0 ? normalized : normalized[(slash + 1)..];
            string? found = findByName(name);
            if (found is not null)
            {
                bytes = read(found);
            }
        }

        if (bytes is null)
        {
            GD.Print($"[water] normal map not found: {normalized}");
            return null;
        }

        ImageTexture? texture = Engine_TexImage.TryCreateTexture(bytes, out string why);
        if (texture is null)
        {
            GD.Print($"[water] {normalized}: {why}");
        }

        return texture;
    }
}
