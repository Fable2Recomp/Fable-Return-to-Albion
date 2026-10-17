using System;
using Godot;

namespace Refii.Level;

public static class Engine_DynamicSkyLighting
{
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
