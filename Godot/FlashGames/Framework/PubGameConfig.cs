using System;
using System.Collections.Generic;
using System.Globalization;

namespace Refii.FlashGames.Framework;

public static class PubGameConfig
{
    public const string DefaultsSection = "defaults";
    public const string GlobalSection = "globalsettings";

    /// <summary>
    /// Marks an applied-settings entry whose variation the file did not hold.
    /// </summary>
    /// <remarks>
    /// WORTH A WARNING RATHER THAN A LINE OF TRACE, and the guest is why. The variation the guest
    /// hands over is the string the village's GDB holds, and unlike the game name the guest does
    /// NOT lowercase it — <c>sub_824679F8</c> stores arg 2 verbatim while running arg 1 through
    /// <c>GetAsLowerCase</c>. A variation that does not resolve is not a cosmetic miss either: it
    /// falls through to the file's &lt;defaults&gt;, and Mage Dice's defaults state no
    /// <c>buyIn</c> at all, so the player sits down at a table with no stakes.
    ///
    /// The match here is deliberately case-INSENSITIVE — more forgiving than the guest — so a hit
    /// on this marker means the name genuinely is not in the file rather than merely mis-cased.
    /// </remarks>
    public const string VariantMissing = "(NOT IN FILE)";

    public static Dictionary<string, PubGameSetting> Apply(
        PubGame game, PubGameSettings? shared, PubGameSettings? own, List<string> applied)
    {
        if (shared is not null)
        {
            ApplyArray(shared, GlobalSection, "starPoints", game.StarPoints, applied);
            ApplyArray(shared, GlobalSection, "starCredit", game.StarCredit, applied);
            ApplyArray(shared, GlobalSection, "conceptArtPoints", game.ConceptArtPoints, applied);
        }

        if (own is null)
        {
            return new Dictionary<string, PubGameSetting>(StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, PubGameSetting> defaults = own.Section(DefaultsSection);

        if (game.VariantName.Length != 0)
        {
            Dictionary<string, PubGameSetting> variation = own.Section(game.VariantName);
            foreach ((string key, PubGameSetting value) in variation)
            {
                defaults[key] = value;
            }

            applied.Add(variation.Count == 0
                ? $"variant:{game.VariantName}{VariantMissing}"
                : $"variant:{game.VariantName}({variation.Count})");
        }

        game.MinBet = Number(defaults, "minBet", game.MinBet, applied);
        game.MaxBet = Number(defaults, "maxBet", game.MaxBet, applied);
        game.BuyIn = Number(defaults, "buyIn", game.BuyIn, applied);
        game.NumberOfAIPlayers =
            Number(defaults, "numberOfAIPlayers", game.NumberOfAIPlayers, applied);
        game.TournamentStartingChips =
            Number(defaults, "tournamentStartingChips", game.TournamentStartingChips, applied);
        game.MinRank = Number(defaults, "minRank", game.MinRank, applied);
        game.MinGamePoints = Number(defaults, "minGamePoints", game.MinGamePoints, applied);
        game.SkinIndex = Number(defaults, "skinIndex", game.SkinIndex, applied);
        game.TournamentRounds =
            Number(defaults, "tournamentRounds", game.TournamentRounds, applied);
        game.UnlockedItemIndex =
            Number(defaults, "unlockedItemIndex", game.UnlockedItemIndex, applied);

        game.Tournament = Flag(defaults, "tournament", game.Tournament, applied);
        game.Tutorial = Flag(defaults, "tutorial", game.Tutorial, applied);
        game.RetailGame = Flag(defaults, "retailGame", game.RetailGame, applied);

        Fill(defaults, "tournamentPayouts", game.TournamentPayouts, applied);
        Fill(defaults, "gameUnlockPoints", game.GameUnlockPoints, applied);
        Fill(defaults, "gameUnlockMin", game.GameUnlockMin, applied);
        Fill(defaults, "gameUnlockMax", game.GameUnlockMax, applied);
        FillText(defaults, "gameUnlockName", game.GameUnlockName, applied);

        return defaults;
    }

    public static List<int> Numbers(
        Dictionary<string, PubGameSetting> settings, string name)
    {
        var values = new List<int>();
        if (!settings.TryGetValue(name, out PubGameSetting? setting))
        {
            return values;
        }

        foreach (string item in setting.Items)
        {
            if (double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double value))
            {
                values.Add((int)Math.Round(value));
            }
        }

        return values;
    }

    public static List<double> Doubles(
        Dictionary<string, PubGameSetting> settings, string name)
    {
        var values = new List<double>();
        if (!settings.TryGetValue(name, out PubGameSetting? setting))
        {
            return values;
        }

        foreach (string item in setting.Items)
        {
            if (double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    public static void FillDoubles(
        Dictionary<string, PubGameSetting> settings, string name, List<double> target,
        List<string> applied)
    {
        List<double> values = Doubles(settings, name);
        if (values.Count == 0)
        {
            return;
        }

        target.Clear();
        target.AddRange(values);
        applied.Add($"{name}[{values.Count}]");
    }

    public static double Real(
        Dictionary<string, PubGameSetting> settings, string name, double fallback,
        List<string> applied)
    {
        if (!settings.TryGetValue(name, out PubGameSetting? setting) ||
            !double.TryParse(setting.Value, NumberStyles.Float, CultureInfo.InvariantCulture,
                out double value))
        {
            return fallback;
        }

        applied.Add($"{name}={value}");
        return value;
    }

    public static void Fill(
        Dictionary<string, PubGameSetting> settings, string name, List<int> target,
        List<string> applied)
    {
        List<int> values = Numbers(settings, name);
        if (values.Count == 0)
        {
            return;
        }

        target.Clear();
        target.AddRange(values);
        applied.Add($"{name}[{values.Count}]");
    }

    public static void FillText(
        Dictionary<string, PubGameSetting> settings, string name, List<string> target,
        List<string> applied)
    {
        if (!settings.TryGetValue(name, out PubGameSetting? setting) || setting.Items.Count == 0)
        {
            return;
        }

        target.Clear();
        target.AddRange(setting.Items);
        applied.Add($"{name}[{setting.Items.Count}]");
    }

    public static int Number(
        Dictionary<string, PubGameSetting> settings, string name, int fallback,
        List<string> applied)
    {
        if (!settings.TryGetValue(name, out PubGameSetting? setting) ||
            !int.TryParse(setting.Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int value))
        {
            return fallback;
        }

        applied.Add($"{name}={value}");
        return value;
    }

    public static bool Flag(
        Dictionary<string, PubGameSetting> settings, string name, bool fallback,
        List<string> applied)
    {
        if (!settings.TryGetValue(name, out PubGameSetting? setting))
        {
            return fallback;
        }

        bool value = string.Equals(setting.Value, "true", StringComparison.OrdinalIgnoreCase);
        applied.Add($"{name}={value}");
        return value;
    }

    public static void ApplyArray(
        PubGameSettings settings, string section, string name, List<int> target,
        List<string> applied)
    {
        foreach (PubGameSetting setting in settings.All)
        {
            if (!string.Equals(setting.Name, name, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(setting.Section, section, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            target.Clear();
            foreach (string item in setting.Items)
            {
                if (int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int value))
                {
                    target.Add(value);
                }
            }

            applied.Add($"{name}[{target.Count}]");
            return;
        }
    }
}
