using System;

namespace Refii.Level;

public sealed class SkyFrame
{
    public float TimeOfDay { get; set; }

    public float[] SunDirection { get; } = new float[3];

    public float[] MoonDirection { get; } = new float[3];

    public float[] SkyTop { get; } = { 0.42f, 0.56f, 0.76f };

    public float[] SkyBottom { get; } = { 0.55f, 0.60f, 0.65f };

    public float[] SkySunset { get; } = { 1.0f, 0.47f, 0.22f };

    public float[] SkyParams { get; } = new float[4];

    public float[] MainLightColour { get; } = { 1f, 1f, 1f };
}

public static class Engine_SkyFrame
{
    public static SkyFrame Evaluate(SkyTheme theme, float timeOfDay)
    {
        var frame = new SkyFrame();

        float time = float.IsFinite(timeOfDay) ? timeOfDay : 0.5f;
        time -= MathF.Floor(time);
        frame.TimeOfDay = time;

        CopyClamped(theme.SkyColour, frame.SkyTop, 0.42f, 0.56f, 0.76f);
        CopyClamped(theme.ComplementaryColour, frame.SkyBottom, 0.55f, 0.60f, 0.65f);
        CopyClamped(theme.SunsetColour, frame.SkySunset, 1.0f, 0.47f, 0.22f);
        Array.Copy(theme.MainLightColour, frame.MainLightColour, 3);

        frame.SkyParams[0] = MathF.Max(theme.SunIntensity, 0f);
        frame.SkyParams[1] = Math.Clamp(theme.ComplementaryBias, 0f, 1f);
        frame.SkyParams[2] = MathF.Max(theme.Rayleigh, 0.05f);
        frame.SkyParams[3] = MathF.Max(theme.Mie, 0.05f);

        const float toRadians = MathF.PI / 180f;
        const float twoPi = MathF.PI * 2f;

        float timeFactor =
            theme.HasSunAxis && float.IsFinite(theme.MainLightTimeFactor) &&
            theme.MainLightTimeFactor > 0f
                ? theme.MainLightTimeFactor
                : 1f;

        float thetaAngle = ((time * timeFactor) - 0.5f) * twoPi;
        float elevation = (theme.HasSunAxis ? theme.SunAxisElevation : 26f) * toRadians;
        float phi = (theme.HasSunAxis
            ? theme.SunAxisZOffset + theme.SunAxisXyRotation
            : 0f) * toRadians;

        float cosTheta = MathF.Cos(thetaAngle);
        float sinTheta = MathF.Sin(thetaAngle);
        float groundX = cosTheta * MathF.Cos(elevation);
        float up = cosTheta * MathF.Sin(elevation);
        float groundZ = sinTheta;
        float cosPhi = MathF.Cos(phi);
        float sinPhi = MathF.Sin(phi);

        Normalise(
            (groundX * cosPhi) - (groundZ * sinPhi),
            up,
            (groundX * sinPhi) + (groundZ * cosPhi),
            frame.SunDirection);

        if (theme.HasMoonAxis)
        {
            float moonElevation = theme.MoonAxisElevation * toRadians;
            float moonPhi = (theme.MoonAxisZOffset + theme.MoonAxisXyRotation) * toRadians;
            float moonGroundX = cosTheta * MathF.Cos(moonElevation);
            float moonUp = cosTheta * MathF.Sin(moonElevation);
            float cosMoonPhi = MathF.Cos(moonPhi);
            float sinMoonPhi = MathF.Sin(moonPhi);

            Normalise(
                -((moonGroundX * cosMoonPhi) - (groundZ * sinMoonPhi)),
                -moonUp,
                -((moonGroundX * sinMoonPhi) + (groundZ * cosMoonPhi)),
                frame.MoonDirection);
        }
        else
        {
            frame.MoonDirection[0] = -frame.SunDirection[0];
            frame.MoonDirection[1] = -frame.SunDirection[1];
            frame.MoonDirection[2] = -frame.SunDirection[2];
        }

        return frame;
    }

    private static void CopyClamped(float[] source, float[] target, float r, float g, float b)
    {
        target[0] = Finite(source[0], r);
        target[1] = Finite(source[1], g);
        target[2] = Finite(source[2], b);
    }

    private static float Finite(float value, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 4f) : fallback;

    private static void Normalise(float x, float y, float z, float[] target)
    {
        float length = MathF.Sqrt((x * x) + (y * y) + (z * z));
        if (length > 1e-6f)
        {
            x /= length;
            y /= length;
            z /= length;
        }

        target[0] = x;
        target[1] = y;
        target[2] = z;
    }
}
