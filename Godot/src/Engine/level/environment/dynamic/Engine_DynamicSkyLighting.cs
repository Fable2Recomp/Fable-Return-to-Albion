using System;
using Godot;

namespace Refii.Level;

public static class Engine_DynamicSkyLighting
{
    public static bool OverridesSun { get; private set; }
    public static Vector3 SunToward { get; private set; } = Vector3.Up;
    public static Vector3 SunColour { get; private set; } = Vector3.One;
    public static float SunEnergy { get; private set; }
    public static float SunTransmission { get; private set; } = 1f;

    public static float Transmission(in DynamicWeatherState weather, float daylight, bool enabled)
    {
        if (!enabled) return 1f;
        float coverage = Unit((Unit(weather.Coverage) - 0.2f) / 0.8f);
        coverage *= coverage * (3f - 2f * coverage);
        float opticalDepth = coverage * Unit(weather.Density) *
            (1.2f + 2.2f * Unit(weather.Overcast) + Unit(weather.Storm));
        float transmission = MathF.Max(0.06f, MathF.Exp(-opticalDepth));
        return 1f + (transmission - 1f) * Unit(daylight);
    }

    public static void Update(in DynamicWeatherState weather, float daylight, bool enabled) =>
        SunTransmission = Transmission(weather, daylight, enabled);

    public static void Reset() { SunTransmission = 1f; OverridesSun = false; }

    public static float DynamicEnergy(float elevation, in DynamicWeatherState weather, bool spatialShadows)
    {
        if (!float.IsFinite(elevation)) return 0f;
        elevation = Math.Clamp(elevation, -1f, 1f);
        float day = Unit((elevation + 0.08f) / 0.18f);
        day *= day * (3f - 2f * day);
        float transmission = spatialShadows
            ? 1f - 0.65f * Unit(weather.Overcast) : Transmission(weather, day, true);
        return (0.8f + 1.8f * MathF.Sqrt(MathF.Max(elevation, 0f))) * day * transmission;
    }

    public static void ConfigureSun(SkyFrame frame, in DynamicWeatherState weather, bool enabled, bool spatialShadows, bool hasSky = true)
    {
        SunToward = Engine_SkyFrame.ToGodot(frame.SunDirection).Normalized();
        OverridesSun = enabled && hasSky && SunToward.Y > -0.08f;
        float warm = 1f - Unit(SunToward.Y / 0.35f);
        SunColour = new Vector3(1f, 0.96f, 0.88f).Lerp(new Vector3(1f, 0.48f, 0.22f), warm * warm);
        SunEnergy = OverridesSun ? DynamicEnergy(SunToward.Y, weather, spatialShadows) : 0f;
    }

    public static Vector3 ResolveRadiance(Vector3 authored) => OverridesSun
        ? SunColour * SunEnergy : authored * SunTransmission;

    public static Vector3 ResolveGuestTravel(Vector3 authored) => OverridesSun
        ? new Vector3(SunToward.X, -SunToward.Z, -SunToward.Y) : authored;

    private static float Unit(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;

    public static float FogDensity(bool usesFableAtmosphere, float haze, float maximum)
    {
        if (usesFableAtmosphere || !float.IsFinite(haze) || !float.IsFinite(maximum)) return 0f;
        haze = Math.Clamp(haze, 0f, 1f);
        return haze * haze * MathF.Max(maximum, 0f);
    }

    public static Vector3 LinearColour(float[] colour, Vector3 fallback) => new(
        Channel(colour[0], fallback.X), Channel(colour[1], fallback.Y), Channel(colour[2], fallback.Z));

    private static float Channel(float value, float fallback) =>
        MathF.Max(float.IsFinite(value) ? value : fallback, 0f);
}
