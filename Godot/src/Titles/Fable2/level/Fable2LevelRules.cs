namespace Refii.Titles.Fable2;

using Refii.Level;

/// <summary>
/// Fable 2's own level rules, by its level names: which vistas show an open ocean, and the
/// weather its story fixes in place (the childhood snow, Rookridge's rain, Knothole's seasons,
/// the demon doors' realms).
/// </summary>
public static class Fable2LevelRules
{
    /// <summary>
    /// Whether a level features an open coastal ocean horizon that should receive the vista ocean sheet.
    /// Inland cities (e.g. Bowerstone Market/Slums), inland landscapes (Brightwood, Bower Lake, Fairfax,
    /// Oakfield), and enclosed dungeons/caves do not feature an ocean horizon and must not receive the 5.7 km sheet,
    /// which would otherwise slice through topography and flood low-lying streets and valleys.
    /// </summary>
    public static bool AllowsVistaOcean(LevelEntry level)
    {
        string path = (level.LevelPath ?? string.Empty).ToLowerInvariant().Replace('\\', '/');
        string name = (level.Name ?? string.Empty).ToLowerInvariant();
        string group = (level.Group ?? string.Empty).ToLowerInvariant();

        // Strip any leading world prefix ("albion/", etc.) for clean prefix matching
        string localPath = path.StartsWith("albion/") ? path["albion/".Length..] : path;

        // Enclosed caves, tombs, and demon doors never feature an open ocean horizon,
        // with the exception of coastal exterior transitional paths.
        bool isEnclosed = localPath.StartsWith("caves/") || localPath.StartsWith("tombs/") || localPath.StartsWith("demondoors/") ||
                          path.Contains("/caves/") || path.Contains("/tombs/") || path.Contains("/demondoors/");
        if (isEnclosed && !path.Contains("westcliffexterior") && !path.Contains("bloodstone_assault"))
        {
            return false;
        }

        // Inland landlocked cities, estates, and regions
        if (path.Contains("bwsmarket") || path.Contains("bwsslums") || path.Contains("bwscemetary") ||
            path.Contains("bowerstone") || path.Contains("cemetery") ||
            path.Contains("fairfax") || path.Contains("brightwood") || path.Contains("bowerlake") ||
            path.Contains("oakfield") || path.Contains("crucible") || path.Contains("dreamworld") ||
            path.Contains("chamberofseasons") || path.Contains("shrine") || path.Contains("dlc2") ||
            name.Contains("bwsmarket") || name.Contains("bwsslums") || name.Contains("bowerstone") ||
            name.Contains("fairfax") || name.Contains("brightwood") || name.Contains("bowerlake") ||
            group.Contains("bwsmarket") || group.Contains("bwsslums") || group.Contains("fairfax"))
        {
            return false;
        }

        // Coastal maritime regions featuring an open ocean horizon
        return path.Contains("bloodstone") ||
               path.Contains("bloodtsone") ||
               path.Contains("reaver beach") ||
               path.Contains("ravenscar") ||
               path.Contains("spire") ||
               path.Contains("mysteryisland") ||
               path.Contains("dunecrest") ||
               path.Contains("westcliff") ||
               path.Contains("wraithmarsh") ||
               path.Contains("rookridge") ||
               name.Contains("bloodstone") ||
               name.Contains("bloodtsone") ||
               name.Contains("reaver beach") ||
               name.Contains("ravenscar") ||
               name.Contains("spire") ||
               name.Contains("westcliff") ||
               name.Contains("wraithmarsh");
    }

    /// <summary>The weather Fable 2 fixes for a level, or null to leave it to the shell's rules.</summary>
    public static DynamicWeatherRule? WeatherRule(LevelEntry level)
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

        if (group == "demondoors")
        {
            return DynamicWeatherRule.Fixed(DynamicWeatherPreset.PartlyCloudy, "demon door realm");
        }

        return null;
    }
}
