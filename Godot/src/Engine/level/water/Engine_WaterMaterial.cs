using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;
using Refii.Textures;

namespace Refii.Level;

public static class Engine_WaterMaterial
{
    private static readonly StringName BetaMieName = "beta_mie";
    private static readonly StringName BetaRayleighName = "beta_rayleigh";
    private static readonly StringName CoastalSurfName = "coastal_surf";
    private static readonly StringName GuestWaterName = "guest_water";
    private static readonly StringName HasDomeName = "has_dome";
    private static readonly StringName HasNormalMapBName = "has_normal_map_b";
    private static readonly StringName HasWaterMaskName = "has_water_mask";
    private static readonly StringName HorizonDistanceName = "horizon_distance";
    private static readonly StringName NormalMapBName = "normal_map_b";
    private static readonly StringName NormalMapBlendName = "normal_map_blend";
    private static readonly StringName NormalMapName = "normal_map";
    private static readonly StringName WaterQualityTierName = "water_quality_tier";
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
    private static readonly StringName FoamTexName = "foam_tex";
    private static readonly StringName WRipplesAName = "w_ripples_a";
    private static readonly StringName WRipplesBName = "w_ripples_b";
    private static readonly StringName WWader0Name = "w_wader0";
    private static readonly StringName WWader1Name = "w_wader1";
    private static readonly StringName WWader0VelName = "w_wader0_vel";
    private static readonly StringName WWader1VelName = "w_wader1_vel";
    private static readonly StringName WRippleClockName = "w_ripple_clock";
    private static readonly StringName WHasWakesName = "w_has_wakes";
    private static readonly StringName WCelestialName = "w_celestial";
    private static readonly StringName WCelestialDirName = "w_celestial_dir";
    private static readonly StringName VistaOceanName = Engine_LevelScene.VistaOceanNodeName;

    public const string ShaderPath = "res://src/Engine/level/water/water.gdshader";

    private static readonly object StateGate = new();
    private static readonly Dictionary<uint, Texture2D?> ThemeNormals = new();
    private static readonly List<WeakReference<ShaderMaterial>> TrackedMaterials = new();
    private static bool _initialized;
    private static int _checkedGeneration = int.MinValue;
    private static WaterQualityTier _currentQuality = WaterQualityTier.Balanced;

    public static WaterQualityTier CurrentQuality
    {
        get
        {
            EnsureInitialized();
            return _currentQuality;
        }
    }

    public static bool GuestMode =>
        OS.GetEnvironment("REFII_WATER").Equals("guest", StringComparison.OrdinalIgnoreCase);

    public delegate byte[]? ReadAsset(string virtualPath);

    public delegate string? FindByName(string fileName);

    private static void EnsureInitialized()
    {
        lock (StateGate)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            _currentQuality = Engine_WaterQuality.ResolveFromValues(
                Engine_RefiiConfig.Read(out _),
                Refii.Launcher.Core.GraphicsPresets.Offered);
            _checkedGeneration = Engine_Switches.Generation;

            Engine_RefiiConfig.OnConfigChanged += static (name, _) =>
            {
                if (string.Equals(name, Engine_WaterQuality.CVarName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, Refii.Launcher.Core.DesktopGraphicsPresets.Key, StringComparison.OrdinalIgnoreCase))
                {
                    lock (StateGate)
                    {
                        _checkedGeneration = int.MinValue;
                    }

                    if (Engine_ServerSyncs.OnMainThread)
                    {
                        SyncConfigIfNeeded();
                    }
                }
            };
        }
    }

    /// <summary>
    /// Refreshes <see cref="CurrentQuality"/> when <c>refii.toml</c> or a live CVar changes, pushing
    /// the updated <c>water_quality_tier</c> to tracked materials and notifying <see cref="Engine_WaterReflection"/>.
    /// </summary>
    public static void SyncConfigIfNeeded()
    {
        EnsureInitialized();
        int gen = Engine_Switches.Generation;
        if (gen == System.Threading.Volatile.Read(ref _checkedGeneration))
        {
            return;
        }

        WaterQualityTier next = Engine_WaterQuality.ResolveFromValues(
            Engine_RefiiConfig.Read(out _),
            Refii.Launcher.Core.GraphicsPresets.Offered);

        bool changed;
        lock (StateGate)
        {
            _checkedGeneration = gen;
            changed = next != _currentQuality;
            _currentQuality = next;
        }

        if (!changed)
        {
            return;
        }

        int tierValue = (int)next;
        lock (StateGate)
        {
            for (int i = TrackedMaterials.Count - 1; i >= 0; i--)
            {
                if (!TrackedMaterials[i].TryGetTarget(out ShaderMaterial? mat) ||
                    !GodotObject.IsInstanceValid(mat))
                {
                    TrackedMaterials.RemoveAt(i);
                    continue;
                }

                Phase phase = Phases.GetOrCreateValue(mat);
                if (phase.QualityTier != tierValue)
                {
                    phase.QualityTier = tierValue;
                    mat.SetShaderParameter(WaterQualityTierName, tierValue);
                }
            }
        }

        Engine_WaterReflection.SyncAllQuality();
    }

    public static ShaderMaterial? Build(
        WaterBody body, ReadAsset read, FindByName? findByName, out string error, bool coastalSurf = false)
    {
        error = string.Empty;
        EnsureInitialized();

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

        Phase phase = Phases.GetOrCreateValue(material);
        phase.FallbackNormal = normalMap;
        phase.ReadFallback = read;

        int tierValue = (int)CurrentQuality;
        phase.QualityTier = tierValue;
        material.SetShaderParameter(WaterQualityTierName, tierValue);
        material.SetShaderParameter(CoastalSurfName, coastalSurf);
        if (!GuestMode && Engine_WaterFoamTexture.Get() is { } foamTexture)
        {
            material.SetShaderParameter(FoamTexName, foamTexture);
        }

        Apply(material, WaterTheme.Default);

        // THE SHORELINE. Built once here rather than per frame: the coverage is authored geometry
        // and never moves, so only the ramp that reads it moves with time of day.
        //
        // A body with no usable tiles keeps `has_water_mask` false and stays opaque, which is the
        // guest's own empty-mask behaviour — water everywhere — rather than a fade invented from
        // whatever geometry happens to be nearby.
        Engine_WaterMask.Mask? mask = Engine_WaterMask.TryBuild(body, perTile: true);
        if (mask is { } built)
        {
            material.SetShaderParameter(WaterMaskName, built.Texture);
            material.SetShaderParameter(WMaskBoundsName, built.Bounds);
            material.SetShaderParameter(HasWaterMaskName, true);
        }

        lock (StateGate)
        {
            TrackedMaterials.Add(new WeakReference<ShaderMaterial>(material));
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
        public Texture2D? FallbackNormal;
        public ReadAsset? ReadFallback;
        public uint ThemeNormalHash;
        public uint BoundHash0 = uint.MaxValue;
        public uint BoundHash1 = uint.MaxValue;
        public float BoundBlend = -1f;
        public bool BoundHasB;
        public Vector3 BetaRayleigh = new(float.NaN, 0f, 0f);
        public Vector3 BetaMie = new(float.NaN, 0f, 0f);
        public float HorizonDistance = float.NaN;
        public bool HasDome;
        public int QualityTier = -1;
        public readonly WaterWakeRingBuffer Wakes = new();
        public bool HadActiveWakes;
        public Vector4 Celestial = new(float.NaN, 0f, 0f, 0f);
        public Vector3 CelestialDir = new(float.NaN, 0f, 0f);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ShaderMaterial, Phase> Phases = new();

    /// <summary>
    /// Advances every water surface's normal-map scroll by its current speed, syncs live theme
    /// normal maps, and checks live quality switches. Main thread, once a frame.
    /// </summary>
    public static void AdvancePhases(Node3D? water, float delta)
    {
        SyncConfigIfNeeded();
        if (water is null)
        {
            return;
        }

        int tierValue = (int)CurrentQuality;
        bool advance = delta > 0f;
        bool sampleWakes = tierValue >= (int)WaterQualityTier.Balanced && !GuestMode;
        Span<WaderSample> waderCandidates = stackalloc WaderSample[8];
        int waderCandidateCount = 0;
        if (sampleWakes)
        {
            Vector3 eye = water.GetViewport()?.GetCamera3D()?.GlobalPosition
                ?? Refii.Game.GameEntities.HeroPosition
                ?? Vector3.Zero;
            waderCandidateCount = Refii.Game.GameEntities.CopyNearbyWaders(eye, 36f, waderCandidates);
        }

        Span<WaderSample> bodyWaders = stackalloc WaderSample[8];

        // By index: GetChildren() is a Godot Array, a finalizable handle per frame.
        for (int i = 0, count = water.GetChildCount(); i < count; i++)
        {
            Node child = water.GetChild(i);
            if (child is MeshInstance3D mesh && mesh.MaterialOverride is ShaderMaterial material &&
                Phases.TryGetValue(material, out Phase? phase))
            {
                if (phase.QualityTier != tierValue)
                {
                    phase.QualityTier = tierValue;
                    material.SetShaderParameter(WaterQualityTierName, tierValue);
                }

                FollowThemeNormal(material, phase);

                if (advance && phase.Speed != Vector4.Zero)
                {
                    Vector4 v = phase.Value + (phase.Speed * delta);
                    phase.Value = new Vector4(
                        v.X - Mathf.Floor(v.X),
                        v.Y - Mathf.Floor(v.Y),
                        v.Z - Mathf.Floor(v.Z),
                        v.W - Mathf.Floor(v.W));
                    material.SetShaderParameter(WPhaseName, phase.Value);
                }

                if (sampleWakes && mesh.Name != VistaOceanName)
                {
                    int bodyWaderCount = 0;
                    float waterY = 0f;
                    if (waderCandidateCount > 0)
                    {
                        Aabb bounds = mesh.GetAabb();
                        Transform3D xform = mesh.GlobalTransform;
                        Vector3 p0 = xform * bounds.Position;
                        Vector3 p1 = xform * bounds.End;
                        waterY = 0.5f * (p0.Y + p1.Y);
                        float minX = MathF.Min(p0.X, p1.X) - 1f;
                        float maxX = MathF.Max(p0.X, p1.X) + 1f;
                        float minZ = MathF.Min(p0.Z, p1.Z) - 1f;
                        float maxZ = MathF.Max(p0.Z, p1.Z) + 1f;
                        for (int w = 0; w < waderCandidateCount; w++)
                        {
                            WaderSample sample = waderCandidates[w];
                            Vector3 pos = sample.Position;
                            if (pos.X >= minX && pos.X <= maxX &&
                                pos.Z >= minZ && pos.Z <= maxZ &&
                                pos.Y >= (waterY - 2.1f) && pos.Y <= (waterY + 0.12f))
                            {
                                bodyWaders[bodyWaderCount++] = sample;
                            }
                        }
                    }

                    bool active = phase.Wakes.Step(
                        delta, bodyWaders[..bodyWaderCount], waterY,
                        out Projection ripplesA, out Projection ripplesB,
                        out Vector4 wader0, out Vector4 wader1,
                        out Vector4 wader0Vel, out Vector4 wader1Vel);
                    if (active || phase.HadActiveWakes)
                    {
                        if (phase.HadActiveWakes != active)
                        {
                            phase.HadActiveWakes = active;
                            material.SetShaderParameter(WHasWakesName, active);
                        }
                        material.SetShaderParameter(WRipplesAName, ripplesA);
                        material.SetShaderParameter(WRipplesBName, ripplesB);
                        material.SetShaderParameter(WWader0Name, wader0);
                        material.SetShaderParameter(WWader1Name, wader1);
                        material.SetShaderParameter(WWader0VelName, wader0Vel);
                        material.SetShaderParameter(WWader1VelName, wader1Vel);
                        material.SetShaderParameter(WRippleClockName, phase.Wakes.Clock);
                    }
                }
                else if (phase.HadActiveWakes)
                {
                    phase.HadActiveWakes = false;
                    material.SetShaderParameter(WHasWakesName, false);
                    material.SetShaderParameter(WRipplesAName, default(Projection));
                    material.SetShaderParameter(WRipplesBName, default(Projection));
                    material.SetShaderParameter(WWader0Name, Vector4.Zero);
                    material.SetShaderParameter(WWader1Name, Vector4.Zero);
                    material.SetShaderParameter(WWader0VelName, Vector4.Zero);
                    material.SetShaderParameter(WWader1VelName, Vector4.Zero);
                }
            }
        }
    }

    /// <summary>
    /// Updates active celestial disc angular sizing and world direction on every surface under <paramref name="water"/>,
    /// writing shader parameters only when values change beyond a small epsilon.
    /// </summary>
    public static void ApplyCelestialToWater(Node3D? water, Vector4 celestial, Vector3 celestialDir)
    {
        if (water is null)
        {
            return;
        }

        for (int i = 0, count = water.GetChildCount(); i < count; i++)
        {
            if (water.GetChild(i) is MeshInstance3D mesh && mesh.MaterialOverride is ShaderMaterial material)
            {
                ApplyCelestial(material, celestial, celestialDir);
            }
        }
    }

    /// <summary>Updates active celestial disc parameters on <paramref name="material"/> only when changed.</summary>
    public static void ApplyCelestial(ShaderMaterial material, Vector4 celestial, Vector3 celestialDir)
    {
        Phase phase = Phases.GetOrCreateValue(material);
        if (float.IsNaN(phase.Celestial.X) || (phase.Celestial - celestial).LengthSquared() > 1e-8f)
        {
            phase.Celestial = celestial;
            material.SetShaderParameter(WCelestialName, celestial);
        }

        if (float.IsNaN(phase.CelestialDir.X) || (phase.CelestialDir - celestialDir).LengthSquared() > 1e-8f)
        {
            phase.CelestialDir = celestialDir;
            material.SetShaderParameter(WCelestialDirName, celestialDir);
        }
    }

    /// <summary>
    /// Updates atmosphere scattering uniforms on <paramref name="material"/> only when values changed.
    /// </summary>
    public static void ApplyAtmosphere(ShaderMaterial material, AtmosphereState state)
    {
        Phase phase = Phases.GetOrCreateValue(material);

        if (!phase.HasDome)
        {
            phase.HasDome = true;
            material.SetShaderParameter(HasDomeName, true);
        }

        var rayleigh = new Vector3(state.BetaRayleigh[0], state.BetaRayleigh[1], state.BetaRayleigh[2]);
        if (phase.BetaRayleigh != rayleigh)
        {
            phase.BetaRayleigh = rayleigh;
            material.SetShaderParameter(BetaRayleighName, rayleigh);
        }

        var mie = new Vector3(state.BetaMie[0], state.BetaMie[1], state.BetaMie[2]);
        if (phase.BetaMie != mie)
        {
            phase.BetaMie = mie;
            material.SetShaderParameter(BetaMieName, mie);
        }

        float horizon = Engine_SkyAtmosphere.HorizonDistance;
        if (phase.HorizonDistance != horizon)
        {
            phase.HorizonDistance = horizon;
            material.SetShaderParameter(HorizonDistanceName, horizon);
        }
    }

    public static void Apply(ShaderMaterial material, WaterTheme theme)
    {
        EnsureInitialized();
        Phase phase = Phases.GetOrCreateValue(material);
        bool first = !phase.HasTheme;

        phase.ThemeNormalHash = theme.HasNormalMap ? theme.NormalMapHash : 0u;
        int tierValue = (int)CurrentQuality;
        if (phase.QualityTier != tierValue)
        {
            phase.QualityTier = tierValue;
            material.SetShaderParameter(WaterQualityTierName, tierValue);
        }

        if (Engine_ServerSyncs.OnMainThread)
        {
            FollowThemeNormal(material, phase);
        }

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

    private static void FollowThemeNormal(ShaderMaterial material, Phase phase)
    {
        uint hash0 = 0u;
        uint hash1 = 0u;
        float blend = 0f;

        if (Refii.Models.Engine_ClassicTheme.TexturesLive)
        {
            Span<(float Weight, uint Hash)> pair = stackalloc (float, uint)[2];
            Refii.Models.Engine_ClassicTheme.Pick(Refii.Models.Engine_ClassicTheme.SlotWaterNormal, pair);
            hash0 = pair[0].Hash;
            hash1 = pair[1].Hash;
            blend = pair[1].Weight;
        }
        else if (phase.ThemeNormalHash != 0u)
        {
            hash0 = phase.ThemeNormalHash;
        }

        if (hash0 != phase.BoundHash0)
        {
            phase.BoundHash0 = hash0;
            Texture2D? tex0 = LoadThemeNormal(hash0, phase.ReadFallback) ?? phase.FallbackNormal;
            if (tex0 is not null)
            {
                material.SetShaderParameter(NormalMapName, tex0);
            }
        }

        bool wantB = hash1 != 0u && hash1 != hash0 && blend > 0.005f;
        Texture2D? tex1 = wantB ? LoadThemeNormal(hash1, phase.ReadFallback) : null;
        bool hasB = tex1 is not null;

        if (hasB && hash1 != phase.BoundHash1)
        {
            phase.BoundHash1 = hash1;
            material.SetShaderParameter(NormalMapBName, tex1!);
        }
        else if (!hasB && phase.BoundHash1 != 0u)
        {
            phase.BoundHash1 = 0u;
        }

        if (hasB != phase.BoundHasB)
        {
            phase.BoundHasB = hasB;
            material.SetShaderParameter(HasNormalMapBName, hasB);
        }

        if (hasB && MathF.Abs(blend - phase.BoundBlend) > 0.002f)
        {
            phase.BoundBlend = blend;
            material.SetShaderParameter(NormalMapBlendName, blend);
        }
    }

    private static Texture2D? LoadThemeNormal(uint hash, ReadAsset? fallbackRead)
    {
        if (hash == 0u)
        {
            return null;
        }

        lock (StateGate)
        {
            if (ThemeNormals.TryGetValue(hash, out Texture2D? cached))
            {
                return cached;
            }
        }

        Texture2D? loaded = null;
        try
        {
            loaded = Engine_EnvironmentTextures.Load(hash, Refii.Models.Engine_BnkMeshLoader.ReadBytes, out string path);
            if (loaded is null && fallbackRead is not null)
            {
                loaded = Engine_EnvironmentTextures.Load(hash, p => fallbackRead(p), out path);
            }

            if (loaded is null && path.Length != 0)
            {
                GD.Print($"[water] theme normal 0x{hash:X8} ('{path}') did not decode");
            }
        }
        catch (Exception exception)
        {
            GD.Print($"[water] theme normal 0x{hash:X8} failed: {exception.Message}");
        }

        lock (StateGate)
        {
            ThemeNormals[hash] = loaded;
        }

        return loaded;
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
