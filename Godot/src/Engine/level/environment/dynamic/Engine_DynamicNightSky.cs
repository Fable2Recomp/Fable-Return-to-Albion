using System;
using System.Collections.Generic;
using System.Globalization;

namespace Refii.Level;

public enum DynamicAuroraMode { Auto, On, Off }

public sealed class Engine_DynamicNightSky
{
    public const string TimeKey = "dynamic_sky_time_of_day";
    public const string AuroraKey = "dynamic_sky_aurora";
    public static Engine_DynamicNightSky Session { get; } = new();

    private readonly Func<double> _random;
    private readonly Dictionary<int, bool> _nights = new();

    public Engine_DynamicNightSky(Func<double>? random = null) => _random = random ?? Random.Shared.NextDouble;

    public static float ResolveTime(float guestFraction, bool enabled, string setting)
    {
        if (enabled && float.TryParse(setting.Trim().Trim('"'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out float hour) && float.IsFinite(hour) && hour >= 0f && hour <= 24f)
            return (hour % 24f) / 24f;
        return guestFraction;
    }

    public static DynamicAuroraMode ParseMode(string setting) => setting.Trim().Trim('"').ToLowerInvariant() switch
    {
        "on" or "true" or "1" => DynamicAuroraMode.On,
        "off" or "false" or "0" => DynamicAuroraMode.Off,
        _ => DynamicAuroraMode.Auto,
    };

    public bool Visible(float hour, int day, DynamicAuroraMode mode)
    {
        if (!float.IsFinite(hour)) return false;
        hour = ((hour % 24f) + 24f) % 24f;
        if (hour >= 6f && hour < 18f) return false;
        int night = day - (hour < 6f ? 1 : 0);
        if (!_nights.TryGetValue(night, out bool chosen))
        {
            chosen = _random() < 0.1;
            _nights[night] = chosen;
        }
        return mode == DynamicAuroraMode.On || (mode == DynamicAuroraMode.Auto && chosen);
    }
}
