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
