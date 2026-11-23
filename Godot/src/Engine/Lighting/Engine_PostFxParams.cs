using System;
using System.Collections.Generic;
using Godot;
using Refii.Level;

namespace Refii.Lighting;

public static class Engine_PostFxParams
{
    private static readonly object Gate = new();

    private static readonly float[] SunUvScratch = { 0.5f, 0.5f };

    public static bool Enabled { get; set; } = true;

    public static bool Tonemap { get; set; } = true;

    public static bool Bloom { get; set; } = true;

    /// <summary>
    /// How far above reference white the display can show, as a linear multiple
    /// (<see cref="Window.GetOutputMaxLinearValue"/>): 1 in SDR, peak / reference nits with HDR
    /// output on. The composite's ceiling. Set on the main thread, read by the render thread.
    /// </summary>
    public static float OutputHeadroom { get; set; } = 1.0f;

    /// <summary>
    /// Radiance boost factor for emissive and light-emitting elements in HDR mode (1.0 in SDR).
    /// </summary>
    public static float EmissionBoost { get; set; } = 1.0f;

    /// <summary>
    /// ON, against the real source at last. The guest's rays are a radial blur of its SUN
    /// LAYER — off_8331A9A8, the depth-tested sun/moon disc draws in a half-res [0,1] UNORM
    /// target — DOWNSCALE2X'd then RADIALBLUR'd twice (decoded from sub_821CB750's frame
    /// segment; the glow-sprite layer at off_8331A9CC is a SEPARATE buffer and feeds bloom,
    /// not the rays). Engine_PostFxEffect.SunLayer synthesizes that source with the guest's
    /// own per-pixel Z test. The guest's constants (4/7 pass gain, 6.0 x RadialBlurFactor)
    /// are stated against this layer.
    /// </summary>
    public static bool Rays { get; set; } = true;

    public static bool Dof { get; set; } = true;

    private static int _checkedGen = -1;
    private static bool? _modernDof;
    private static string? _toneCurve;
    private static float? _darkLift;
    /// <summary>What `bloom_scale` is when the config does not state it (RuntimeCVarSet agrees):
    /// 0.25, calibrated to complement 3D volumetric scattering without overwhelming the scene.
    /// The dial scales the bright pass alone; the glow layer (sun, moon, fixture halos) joins
    /// the bloom buffer unscaled.</summary>
    public const float BloomGainDefault = 0.25f;

    /// <summary>What `volumetric_shaft_scale` is when the config does not state it: 1.0.</summary>
    public const float VolumetricShaftScaleDefault = 1.0f;

    /// <summary>
    /// Calibrated baseline multiplier on volumetric light-shaft in-scattering radiance in sky-less caves and interior levels
    /// when <c>interior_volumetric_shaft_scale = 1.0</c> (50% reduction by default).
    /// </summary>
    public const float InteriorVolumetricShaftBaseline = 0.50f;

    /// <summary>
    /// Calibrated baseline multiplier on volumetric light-shaft in-scattering radiance in sky-lit outdoor levels
    /// when <c>outdoor_volumetric_shaft_scale = 1.0</c> (20% reduction by default).
    /// </summary>
    public const float OutdoorVolumetricShaftBaseline = 0.80f;

    private static float? _bloomGain;
    private static bool? _haloDistanceFade;
    private static float? _interiorVolumetricShaftScale;
    private static float? _outdoorVolumetricShaftScale;

    private static void CheckGeneration()
    {
        Refii.Core.Engine_RefiiConfig.PollReload();
        int gen = Refii.Core.Engine_Switches.Generation;
        if (gen != _checkedGen)
        {
            _checkedGen = gen;
            _modernDof = null;
            _toneCurve = null;
            _darkLift = null;
            _bloomGain = null;
            _haloDistanceFade = null;
            _interiorVolumetricShaftScale = null;
            _outdoorVolumetricShaftScale = null;
        }
    }

    /// <summary>The depth of field as the frame may apply it: the fable model's always, the
    /// modern model's only when `modern_depth_of_field` asks for it.</summary>
    public static bool DofAllowed
    {
        get
        {
            if (!Dof || !Engine_LightingFrame.ModernModel)
            {
                return Dof;
            }

            CheckGeneration();
            _modernDof ??= ReadBool("modern_depth_of_field", false);
            return _modernDof.Value;
        }
    }

    /// <summary>
    /// Whether the modern model's filmic curve replaces the original's Reinhard for THIS frame.
    /// `modern_tone_curve`: "fable" (the default) uses the Extended Reinhard + smoothed CDF local
    /// contrast + HDR/SDR shoulder + shadow toe curve everywhere; "filmic" applies the ACES curve
    /// where there is no sky (caves and rooms); "all" applies the ACES curve outdoors too.
    /// </summary>
    public static bool FilmicCurve
    {
        get
        {
            if (!Engine_LightingFrame.ModernModel)
            {
                return false;
            }

            CheckGeneration();
            _toneCurve ??= ReadString("modern_tone_curve", "fable").ToLowerInvariant();
            return _toneCurve switch
            {
                "all" => true,
                "filmic" => Engine_LightZones.IsViewerIndoors,
                _ => false,
            };
        }
    }

    /// <summary>
    /// True when the modern scene-referred pipeline uses the Fable tonemapper
    /// (Extended Reinhard + smoothed CDF local contrast + HDR/SDR shoulder + shadow black toe).
    /// </summary>
    public static bool FableStyleTonemap => Engine_LightingFrame.ModernModel && !FilmicCurve;

    private static bool ReadBool(string name, bool fallback)
    {
        try
        {
            return Refii.Core.Engine_RefiiConfig.Read(out _).TryGetValue(name, out string? v)
                ? v.Trim().Equals("true", System.StringComparison.OrdinalIgnoreCase)
                : fallback;
        }
        catch (System.Exception)
        {
            return fallback;
        }
    }

    private static string ReadString(string name, string fallback)
    {
        try
        {
            return Refii.Core.Engine_RefiiConfig.Read(out _).TryGetValue(name, out string? v)
                ? v.Trim().Trim('"')
                : fallback;
        }
        catch (System.Exception)
        {
            return fallback;
        }
    }

    public static bool Saturation { get; set; } = true;

    public static bool ExposureLock { get; set; }

    public static float ExposureLockValue { get; set; } = 4.0f;

    public static float Darkening { get; set; }

    public static float DarkeningParam { get; set; }

    public static float UserGamma { get; set; } = 1.0f;

    public static float RayGain { get; set; } = 1.0f;

    /// <summary>
    /// Multiplier on bloom intensity in the modern composite (`bloom_scale` in config, default
    /// <see cref="BloomGainDefault"/>).
    /// </summary>
    public static float BloomGain
    {
        get
        {
            CheckGeneration();
            if (_bloomGain is null)
            {
                float value = BloomGainDefault;
                try
                {
                    value = Refii.Core.Engine_RefiiConfig.Float(
                        Refii.Core.Engine_RefiiConfig.Read(out _), "bloom_scale", BloomGainDefault);
                }
                catch (System.Exception)
                {
                }

                _bloomGain = System.Math.Clamp(value, 0.0f, 4.0f);
            }

            return _bloomGain.Value;
        }
        set
        {
            CheckGeneration();
            _bloomGain = System.Math.Clamp(value, 0.0f, 4.0f);
        }
    }

    /// <summary>
    /// Multiplier on volumetric light-shaft in-scattering radiance across placed and dynamic lights in sky-less cave and interior levels
    /// (<c>interior_volumetric_shaft_scale</c> in config, default <see cref="VolumetricShaftScaleDefault"/>).
    /// </summary>
    public static float InteriorVolumetricShaftScale
    {
        get
        {
            CheckGeneration();
            if (_interiorVolumetricShaftScale is null)
            {
                float value = VolumetricShaftScaleDefault;
                try
                {
                    IReadOnlyDictionary<string, string> config = Refii.Core.Engine_RefiiConfig.Read(out _);
                    if (config.TryGetValue("interior_volumetric_shaft_scale", out string? s) ||
                        config.TryGetValue("volumetric_shaft_scale", out s) ||
                        config.TryGetValue("light_shaft_scale", out s))
                    {
                        if (float.TryParse(s, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float parsed))
                        {
                            value = parsed;
                        }
                    }
                }
                catch (System.Exception)
                {
                }

                _interiorVolumetricShaftScale = System.Math.Clamp(value, 0.0f, 16.0f);
            }

            return _interiorVolumetricShaftScale.Value;
        }
        set
        {
            CheckGeneration();
            _interiorVolumetricShaftScale = System.Math.Clamp(value, 0.0f, 16.0f);
        }
    }

    /// <summary>
    /// Multiplier on volumetric light-shaft in-scattering radiance across placed and dynamic lights in sky-lit outdoor levels
    /// (<c>outdoor_volumetric_shaft_scale</c> in config, default <see cref="VolumetricShaftScaleDefault"/>).
    /// </summary>
    public static float OutdoorVolumetricShaftScale
    {
        get
        {
            CheckGeneration();
            if (_outdoorVolumetricShaftScale is null)
            {
                float value = VolumetricShaftScaleDefault;
                try
                {
                    IReadOnlyDictionary<string, string> config = Refii.Core.Engine_RefiiConfig.Read(out _);
                    if (config.TryGetValue("outdoor_volumetric_shaft_scale", out string? s) ||
                        config.TryGetValue("volumetric_shaft_scale", out s) ||
                        config.TryGetValue("light_shaft_scale", out s))
                    {
                        if (float.TryParse(s, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float parsed))
                        {
                            value = parsed;
                        }
                    }
                }
                catch (System.Exception)
                {
                }

                _outdoorVolumetricShaftScale = System.Math.Clamp(value, 0.0f, 16.0f);
            }

            return _outdoorVolumetricShaftScale.Value;
        }
        set
        {
            CheckGeneration();
            _outdoorVolumetricShaftScale = System.Math.Clamp(value, 0.0f, 16.0f);
        }
    }

    /// <summary>
    /// Effective multiplier on volumetric light-shaft in-scattering radiance across placed and dynamic lights for the current frame/view,
    /// selecting indoor vs outdoor scale with calibrated baselines (<see cref="InteriorVolumetricShaftBaseline"/> and <see cref="OutdoorVolumetricShaftBaseline"/>).
    /// Does not affect celestial sun/moon rays.
    /// </summary>
    public static float VolumetricShaftScale
    {
        get
        {
            float baseScale = Engine_LightingFrame.CurrentInterior
                ? InteriorVolumetricShaftScale * InteriorVolumetricShaftBaseline
                : OutdoorVolumetricShaftScale * OutdoorVolumetricShaftBaseline;
            return System.Math.Clamp(baseScale, 0.0f, 16.0f);
        }
        set
        {
            CheckGeneration();
            _interiorVolumetricShaftScale = System.Math.Clamp(value, 0.0f, 16.0f);
            _outdoorVolumetricShaftScale = System.Math.Clamp(value, 0.0f, 16.0f);
        }
    }

    /// <summary>
    /// Whether bloom halos from distant point light fixtures are smoothly attenuated with distance
    /// (`halo_distance_fade` in config, default true).
    /// </summary>
    public static bool HaloDistanceFade
    {
        get
        {
            CheckGeneration();
            _haloDistanceFade ??= ReadBool("halo_distance_fade", true);
            return _haloDistanceFade.Value;
        }
        set
        {
            CheckGeneration();
            _haloDistanceFade = value;
        }
    }

    /// <summary>
    /// Where bloom cuts in, as a multiple of white in EXPOSED space. 1.0 is white — what the
    /// exposed image clips — which is where the guest's bright pass cuts. Below 1 blooms more
    /// of the frame, above 1 only the brightest highlights.
    /// </summary>
    public static float BloomThresholdScale { get; set; } = 1.0f;

    public static bool MaterialExposureFollowsApplied { get; set; } = true;

    /// <summary>How many times darker than its floor a frame's mean must be before the lift
    /// begins; the lift then grows until it reaches <see cref="DarkLift"/>.</summary>
    public const float DarkLiftOnset = 10.0f;

    /// <summary>
    /// Calibrated baseline dark-lift divisor in sky-less caves when <c>interior_dark_lift = 1.0</c>
    /// under the Fable tone curve (<c>1.25x</c>), compensating for the Xbox 360's raw
    /// <c>1 / (smWhite - smBlack)^2</c> Reinhard boost alongside interior SDFGI bounce.
    /// </summary>
    public const float FableInteriorLiftBaseline = 1.25f;

    /// <summary>
    /// Calibrated baseline dark-lift divisor in sky-less caves when <c>interior_dark_lift = 1.0</c>
    /// under the Filmic (ACES) tone curve (<c>1.75x</c>), which evaluates ACES directly without the
    /// cumulative histogram (<c>fable_cdf_pw</c>) midtone lift alongside interior SDFGI bounce.
    /// </summary>
    public const float FilmicInteriorLiftBaseline = 1.75f;

    /// <summary>
    /// THE REMASTER'S ONE EXPOSURE LEVER. The guest's tone curve normalises a frame by its
    /// mean raw luminance, but pins that mean at the theme's AVG_LUMINANCE_LOWER_BOUND (0.3 by
    /// day, 0.15 at night) — so a frame darker than its floor renders at raw × knee / floor,
    /// and Thag's cave, raw mean 0.008, sits at 7% grey while a forest at noon (0.17) is pinned
    /// to the same floor and calibrated there. This lowers the floor only for frames far below
    /// it: effective floor = clamp(mean × 10, floor / lift, floor). Noon and Brightwood's night
    /// (0.024 against 0.15) are untouched; the cave is lifted up to `lift` times. 1 is the
    /// guest; forced to 1 in the fable model. Config `modern_dark_lift`, env REFII_DARK_LIFT.
    /// </summary>
    public static float DarkLift
    {
        get
        {
            if (!Engine_LightingFrame.ModernModel)
            {
                return 1.0f;
            }

            // No sky: the cave interior lift, normalized so 1.0 applies the calibrated baseline for the active curve.
            if (Engine_LightingFrame.CurrentInterior)
            {
                float baseline = FilmicCurve ? FilmicInteriorLiftBaseline : FableInteriorLiftBaseline;
                return System.Math.Clamp(Engine_LightingFrame.InteriorDarkLift * baseline, 1.0f, 16.0f);
            }

            CheckGeneration();
            if (_darkLift is null)
            {
                float value = 1.0f;
                try
                {
                    value = Refii.Core.Engine_RefiiConfig.Float(
                        Refii.Core.Engine_RefiiConfig.Read(out _), "modern_dark_lift", 1.0f);
                }
                catch (System.Exception)
                {
                }

                if (float.TryParse(System.Environment.GetEnvironmentVariable("REFII_DARK_LIFT") ?? string.Empty,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float forced))
                {
                    value = forced;
                }

                _darkLift = System.Math.Clamp(value, 1.0f, 16.0f);
            }

            return _darkLift.Value;
        }
    }

    /// <summary>
    /// The onset in force: outdoors the lift only begins a decade under the floor (night stays
    /// night); in caves it begins AT the floor, so a room's key follows its real mean down
    /// to floor / lift.
    /// </summary>
    public static float DarkLiftOnsetNow =>
        Engine_LightingFrame.CurrentInterior ? 1.0f : DarkLiftOnset;

    public const string ModeEnvironment = "REFII_POSTFX";

    public static string Mode => (System.Environment.GetEnvironmentVariable(ModeEnvironment) ?? string.Empty).ToLowerInvariant();

    public static bool ChainRequested => Mode != "off";

    public static bool ChainActive => ChainRequested && Enabled;

    public static bool SunSourcesRequested => Mode is not ("off" or "nosun");

    public static Engine_PostFxModel.Derived Derived { get; } = new();

    public static ToneMapTheme Theme { get; } = new();

    public static bool HasTheme { get; private set; }

    public static SkyTheme? Sky { get; private set; }

    public static SkyFrame? Frame { get; private set; }

    private static volatile bool _themeHasSaturationMask;

    /// <summary>
    /// True when the active tone-map theme states a non-trivial masked saturation/brightness delta
    /// (<c>0x821e5e60</c>: <c>|SaturationMasked| &gt; 0.01 || |BrightnessMasked| &gt; 0.01</c>) and PostFX is active.
    /// </summary>
    public static bool RequiresSaturationMask(
        float saturationMasked, float brightnessMasked, bool chainActive = true, bool coverHeld = false) =>
        chainActive && !coverHeld && (MathF.Abs(saturationMasked) > 0.01f || MathF.Abs(brightnessMasked) > 0.01f);

    /// <summary>
    /// True when entities carrying <c>SetSaturationMask</c> should attach the stencil-mask overlay
    /// for the active theme and viewport state.
    /// </summary>
    public static bool IsSaturationMaskActive =>
        RequiresSaturationMask(
            _themeHasSaturationMask ? 1.0f : 0.0f,
            0.0f,
            ChainActive,
            Refii.Game.GameWorld.CoverHeld);

    /// <summary>
    /// The sun on the PRIMARY view's screen — <c>Publish</c>'s camera, the window's — for readers on
    /// the main thread. The chain does not read it: each camera's own Engine_PostFxEffect projects
    /// the sun through the view it is rendering (<c>RenderSceneData</c>), so on split screen each
    /// view's rays and sun layer follow its own camera. The theme and sky frame are world state
    /// and serve every view.
    /// </summary>
    public static Vector2 SunUv { get; private set; } = new(0.5f, 0.5f);

    public static bool SunOnScreen { get; private set; }

    public static Vector3 SunDirection { get; private set; } = Vector3.Up;

    public static float SunFacing { get; private set; }

    private static bool _vistaOceanActive;
    private static float _vistaOceanHeight;

    /// <summary>Records whether a coastal region currently shows an active <c>VistaOcean</c> at <paramref name="height"/>.</summary>
    public static void SetVistaOcean(bool active, float height)
    {
        lock (Gate)
        {
            _vistaOceanActive = active;
            _vistaOceanHeight = height;
        }
    }

    /// <summary>Snapshots the standing region's <c>VistaOcean</c> state for the render thread.</summary>
    public static void GetVistaOcean(out bool active, out float height)
    {
        lock (Gate)
        {
            active = _vistaOceanActive;
            height = _vistaOceanHeight;
        }
    }

    /// <summary>
    /// Builds the per-view analytical ocean-plane ray intersection parameter
    /// <c>(camOrigin.Y - oceanY, forward.Y, right.Y / px, up.Y / py)</c> when <paramref name="oceanActive"/>
    /// is true and the camera sits above <paramref name="oceanY"/>; otherwise <see cref="Vector4.Zero"/>.
    /// </summary>
    public static Vector4 BuildWaterPlanePush(
        bool oceanActive, float oceanY, Vector3 camOrigin, Basis camBasis, float px, float py)
    {
        if (!oceanActive || !(camOrigin.Y > oceanY + 0.02f) || !(px > 1e-8f) || !(py > 1e-8f))
        {
            return Vector4.Zero;
        }

        Vector3 forward = (-camBasis.Z).Normalized();
        Vector3 right = camBasis.X.Normalized();
        Vector3 up = camBasis.Y.Normalized();
        return new Vector4(camOrigin.Y - oceanY, forward.Y, right.Y / px, up.Y / py);
    }

    /// <summary>
    /// Evaluates the view-Z (<c>facing</c>) distance along screen <paramref name="uv"/> where the camera ray
    /// strikes the infinite ocean plane, or <see cref="float.PositiveInfinity"/> if the ray points at/above the horizon
    /// or no ocean plane is active. Pure helper mirroring <c>fable_postfx_sunlayer.glsl</c> / <c>fable_postfx_halolayer.glsl</c>.
    /// </summary>
    public static float EvaluateWaterPlaneFacing(Vector4 waterPlane, Vector2 uv)
    {
        if (waterPlane.X <= 0f)
        {
            return float.PositiveInfinity;
        }

        float ndcX = (uv.X * 2f) - 1f;
        float ndcY = 1f - (uv.Y * 2f);
        float rayY = waterPlane.Y + (ndcX * waterPlane.Z) + (ndcY * waterPlane.W);
        return rayY < -1e-5f ? (waterPlane.X / (-rayY)) : float.PositiveInfinity;
    }

    public static void Publish(ToneMapTheme? theme, SkyTheme? sky, SkyFrame? frame, Camera3D? camera)
    {
        lock (Gate)
        {
            if (theme is not null)
            {
                Theme.CopyFrom(theme);
                HasTheme = theme.HasAny;
            }
            else
            {
                Theme.CopyFrom(new ToneMapTheme());
                HasTheme = false;
            }

            Sky = sky;
            Frame = frame;

            Vector3 sunDirection = frame is not null
                ? Refii.Level.Engine_SkyFrame.ToGodot(frame.SunDirection)
                : Vector3.Up;
            if (sunDirection.LengthSquared() > 1e-12f)
            {
                sunDirection = sunDirection.Normalized();
            }

            SunDirection = sunDirection;

            Vector2 uv = new(0.5f, 0.5f);
            bool onScreen = false;
            float facing = 0f;
            if (camera is not null && frame is not null)
            {
                onScreen = ProjectSun(sunDirection, camera, out uv, out facing);
            }

            SunUv = uv;
            SunOnScreen = onScreen;
            SunFacing = facing;
            SunUvScratch[0] = uv.X;
            SunUvScratch[1] = uv.Y;

            Engine_PostFxModel.Derive(
                Theme, Tonemap, SunUvScratch, Darkening, DarkeningParam, UserGamma, Derived);
            _themeHasSaturationMask = HasTheme &&
                (MathF.Abs(Derived.SaturationMasked) > 0.01f || MathF.Abs(Derived.BrightnessMasked) > 0.01f);
        }
    }

    public static void Snapshot(Engine_PostFxModel.Derived into)
    {
        lock (Gate)
        {
            into.CopyFrom(Derived);
        }
    }

    public static bool ProjectSun(Vector3 direction, Camera3D camera, out Vector2 uv, out float facing)
    {
        Basis basis = camera.GlobalTransform.Basis;
        Projection projection = camera.GetCameraProjection();
        float px = projection.X.X;
        float py = projection.Y.Y;
        float tanHalfX = MathF.Abs(px) > 1e-8f ? 1.0f / MathF.Abs(px) : 0f;
        float tanHalfY = MathF.Abs(py) > 1e-8f ? 1.0f / MathF.Abs(py) : 0f;
        return ProjectSun(
            direction, -basis.Z, basis.X, basis.Y, tanHalfX, tanHalfY, out uv, out facing);
    }

    public static bool ProjectSun(
        Vector3 direction, Vector3 forward, Vector3 right, Vector3 up,
        float tanHalfX, float tanHalfY, out Vector2 uv, out float facing)
    {
        uv = new Vector2(0.5f, 0.5f);
        facing = direction.Dot(forward);
        if (!(facing > 0f) || !(tanHalfX > 0f) || !(tanHalfY > 0f))
        {
            return false;
        }

        float rx = direction.Dot(right);
        float uy = direction.Dot(up);
        float x = ((rx / (facing * tanHalfX)) + 1.0f) * 0.5f;
        float y = (1.0f - (uy / (facing * tanHalfY))) * 0.5f;
        if (!float.IsFinite(x) || !float.IsFinite(y))
        {
            return false;
        }

        uv = new Vector2(x, y);
        return true;
    }
}
