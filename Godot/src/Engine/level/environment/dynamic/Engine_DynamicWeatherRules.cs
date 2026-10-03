using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

public enum DynamicWeatherMode
{
    Fixed,
    Theme,
    Random,
    None,
}

public readonly record struct DynamicWeatherRule(
    DynamicWeatherMode Mode, DynamicWeatherPreset Preset, string Reason)
{
    public static DynamicWeatherRule Fixed(DynamicWeatherPreset preset, string reason) =>
        new(DynamicWeatherMode.Fixed, preset, reason);

    public static readonly DynamicWeatherRule Random =
        new(DynamicWeatherMode.Random, DynamicWeatherPreset.Theme, "random");

    public static readonly DynamicWeatherRule Theme =
        new(DynamicWeatherMode.Theme, DynamicWeatherPreset.Theme, "theme");

    public static readonly DynamicWeatherRule None =
        new(DynamicWeatherMode.None, DynamicWeatherPreset.Clear, "interior");
}

public static class Engine_DynamicWeatherRules
{
    public static readonly float ChangeMinutes =
        Refii.Core.Engine_Switches.Float("dynamic_sky_weather_change_minutes", 20f);

    private static readonly (DynamicWeatherPreset Preset, float Weight)[] OutdoorTable =
    {
        (DynamicWeatherPreset.Clear, 30f),
        (DynamicWeatherPreset.PartlyCloudy, 30f),
        (DynamicWeatherPreset.Cloudy, 15f),
        (DynamicWeatherPreset.Overcast, 9f),
        (DynamicWeatherPreset.Fog, 5f),
        (DynamicWeatherPreset.Rain, 8f),
        (DynamicWeatherPreset.Storm, 3f),
    };

    private static readonly Dictionary<DynamicWeatherPreset, DynamicWeatherPreset[]> Neighbours =
        new()
        {
            [DynamicWeatherPreset.Clear] =
                new[] { DynamicWeatherPreset.Clear, DynamicWeatherPreset.PartlyCloudy },
            [DynamicWeatherPreset.PartlyCloudy] =
                new[] { DynamicWeatherPreset.Clear, DynamicWeatherPreset.PartlyCloudy, DynamicWeatherPreset.Cloudy },
            [DynamicWeatherPreset.Cloudy] =
                new[] { DynamicWeatherPreset.PartlyCloudy, DynamicWeatherPreset.Cloudy, DynamicWeatherPreset.Overcast, DynamicWeatherPreset.Fog },
            [DynamicWeatherPreset.Overcast] =
                new[] { DynamicWeatherPreset.Cloudy, DynamicWeatherPreset.Overcast, DynamicWeatherPreset.Rain, DynamicWeatherPreset.Fog },
            [DynamicWeatherPreset.Fog] =
                new[] { DynamicWeatherPreset.Cloudy, DynamicWeatherPreset.Overcast, DynamicWeatherPreset.PartlyCloudy },
            [DynamicWeatherPreset.Rain] =
                new[] { DynamicWeatherPreset.Overcast, DynamicWeatherPreset.Rain, DynamicWeatherPreset.Storm, DynamicWeatherPreset.Cloudy },
            [DynamicWeatherPreset.Storm] =
                new[] { DynamicWeatherPreset.Rain, DynamicWeatherPreset.Storm, DynamicWeatherPreset.Overcast },
        };

    public static DynamicWeatherRule Resolve(LevelEntry level)
    {
        string name = level.Name.ToLowerInvariant();
        string group = level.Group.ToLowerInvariant();
        string scenario = level.Scenario.ToLowerInvariant();

        if (name == "bwsslums" && (scenario.Length == 0 || scenario == "defaultscenario"))
        {
            return DynamicWeatherRule.Fixed(DynamicWeatherPreset.Snow, "childhood old town");
        }

        if (name == "fairfaxcastlegardens" && scenario == "ff_chapter1")
        {
            return DynamicWeatherRule.Fixed(DynamicWeatherPreset.Snow, "childhood castle");
        }

        if (name == "dunecrestnew")
        {
            return DynamicWeatherRule.Fixed(DynamicWeatherPreset.Rain, "rookridge");
        }

        if (name == "mysteryisland")
        {
            return scenario switch
            {
                "winter" => DynamicWeatherRule.Fixed(DynamicWeatherPreset.Snow, "knothole winter"),
                "summer" => DynamicWeatherRule.Fixed(DynamicWeatherPreset.Clear, "knothole summer"),
                _ => DynamicWeatherRule.Theme with { Reason = "knothole theme" },
            };
        }

        if (group is "caves" or "tombs")
        {
            return DynamicWeatherRule.None;
        }

        if (group == "demondoors")
        {
            return DynamicWeatherRule.Fixed(DynamicWeatherPreset.PartlyCloudy, "demon door realm");
        }

        return DynamicWeatherRule.Random;
    }

    public static DynamicWeatherPreset Roll(Random random)
    {
        float total = 0f;
        foreach ((_, float weight) in OutdoorTable)
        {
            total += weight;
        }

        float pick = (float)random.NextDouble() * total;
        foreach ((DynamicWeatherPreset preset, float weight) in OutdoorTable)
        {
            pick -= weight;
            if (pick <= 0f)
            {
                return preset;
            }
        }

        return DynamicWeatherPreset.PartlyCloudy;
    }

    public static DynamicWeatherPreset Drift(DynamicWeatherPreset current, Random random)
    {
        if (!Neighbours.TryGetValue(current, out DynamicWeatherPreset[]? options) || options.Length == 0)
        {
            return Roll(random);
        }

        return options[random.Next(options.Length)];
    }
}
