using System;

namespace Refii.Level;

public static class Engine_DynamicWeatherProfile
{
    public static DynamicWeatherState FromUnreal(float clouds, float rain = 0, float snow = 0,
        float lightning = 0, float wind = 2, float fog = 0)
    {
        float c = Unit(clouds), r = Unit(rain), s = Unit(snow), storm = Unit(lightning);
        float overcast = Math.Clamp((c - 0.45f) / 0.35f, 0, 1);
        return new DynamicWeatherState
        {
            Coverage = c,
            Density = 0.55f + 0.45f * c,
            Overcast = overcast,
            Rain = r,
            SnowFall = s,
            Snow = s,
            Storm = storm,
            WindSpeed = Unit(wind) * 3f,
            FogHaze = Unit(fog),
            Cirrus = (1f - overcast) * c * 0.6f,
        };
    }

    private static float Unit(float value) => float.IsFinite(value) ? Math.Clamp(value / 10f, 0, 1) : 0;
}
