using System;
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

    public static bool Saturation { get; set; } = true;

    public static bool ExposureLock { get; set; }

    public static float ExposureLockValue { get; set; } = 4.0f;

    public static float Darkening { get; set; }

    public static float DarkeningParam { get; set; }

    public static float UserGamma { get; set; } = 1.0f;

    public static float RayGain { get; set; } = 1.0f;

    public static float BloomGain { get; set; } = 1.0f;

    /// <summary>
    /// Where bloom cuts in, as a multiple of white in EXPOSED space. 1.0 is white — what the
    /// exposed image clips — which is where the guest's bright pass cuts. Below 1 blooms more
    /// of the frame, above 1 only the brightest highlights.
    /// </summary>
    public static float BloomThresholdScale { get; set; } = 1.0f;

    public static bool MaterialExposureFollowsApplied { get; set; } = true;

    private static float? _darkLift;

    /// <summary>How many times darker than its floor a frame's mean must be before the lift
    /// begins; the lift then grows until it reaches <see cref="DarkLift"/>.</summary>
    public const float DarkLiftOnset = 10.0f;

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

            // No sky: the interior lift, live. Outdoors keeps the launcher's modern_dark_lift.
            if (Engine_LightingFrame.CurrentInterior)
            {
                return Engine_LightingFrame.InteriorDarkLift;
            }

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

                if (float.TryParse(OS.GetEnvironment("REFII_DARK_LIFT"),
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
    /// night); with no sky it begins AT the floor, so a room's key follows its real mean down
    /// to floor / lift. The guild hall at 0.018 raw sat between the two and got no lift at all
    /// under the outdoor onset.
    /// </summary>
    public static float DarkLiftOnsetNow =>
        Engine_LightingFrame.CurrentInterior ? 1.0f : DarkLiftOnset;

    /// <summary>
    /// How much brighter a frame of this mean is treated as having been lit: up to
    /// <see cref="DarkLift"/>, and only until its mean reaches floor / onset.
    /// </summary>
    public static float RawGain(float mean, float floor) =>
        System.Math.Clamp((floor / DarkLiftOnsetNow) / System.Math.Max(mean, 1e-6f), 1.0f, DarkLift);

    public const string ModeEnvironment = "REFII_POSTFX";

    public static string Mode => OS.GetEnvironment(ModeEnvironment).ToLowerInvariant();

    public static bool ChainRequested => Mode != "off";

    public static bool ChainActive => ChainRequested && Enabled;

    public static bool SunSourcesRequested => Mode is not ("off" or "nosun");

    public static Engine_PostFxModel.Derived Derived { get; } = new();

    public static ToneMapTheme Theme { get; } = new();

    public static bool HasTheme { get; private set; }

    public static SkyTheme? Sky { get; private set; }

    public static SkyFrame? Frame { get; private set; }

    public static Vector2 SunUv { get; private set; } = new(0.5f, 0.5f);

    public static bool SunOnScreen { get; private set; }

    public static Vector3 SunDirection { get; private set; } = Vector3.Up;

    public static float SunFacing { get; private set; }

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
