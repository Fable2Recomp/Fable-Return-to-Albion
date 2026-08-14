using System;
using System.Collections.Generic;
using System.Globalization;

namespace Refii.FlashGames.PubGames;

public static class PubGameConfig
{
    public const string DefaultsSection = "defaults";
    public const string GlobalSection = "globalsettings";

    public static List<string> Apply(
        Keystone game, PubGameSettings? shared, PubGameSettings? own)
    {
        var applied = new List<string>();

        if (shared is not null)
        {
            ApplyArray(shared, GlobalSection, "starPoints", game.StarPoints, applied);
            ApplyArray(shared, GlobalSection, "starCredit", game.StarCredit, applied);
            ApplyArray(shared, GlobalSection, "conceptArtPoints", game.ConceptArtPoints, applied);
        }

        if (own is null)
        {
            return applied;
        }

        Dictionary<string, PubGameSetting> defaults = own.Section(DefaultsSection);

        if (game.VariantName.Length != 0)
        {
            foreach ((string key, PubGameSetting value) in own.Section(game.VariantName))
            {
                defaults[key] = value;
            }

            applied.Add("variant:" + game.VariantName);
        }

        game.MinBet = Number(defaults, "minBet", game.MinBet, applied);
        game.MaxBet = Number(defaults, "maxBet", game.MaxBet, applied);
        game.MinInsideBet = Number(defaults, "minInsideBet", game.MinInsideBet, applied);
        game.MaxInsideBet = Number(defaults, "maxInsideBet", game.MaxInsideBet, applied);
        game.BuyIn = Number(defaults, "buyIn", game.BuyIn, applied);
        game.NumberOfAIPlayers =
            Number(defaults, "numberOfAIPlayers", game.NumberOfAIPlayers, applied);
        game.ApplauseThreshold3 =
            Number(defaults, "applauseThreshold3", game.ApplauseThreshold3, applied);
        game.ApplauseThreshold4 =
            Number(defaults, "applauseThreshold4", game.ApplauseThreshold4, applied);
        game.TournamentStartingChips =
            Number(defaults, "tournamentStartingChips", game.TournamentStartingChips, applied);
        game.MinRank = Number(defaults, "minRank", game.MinRank, applied);
        game.SkinIndex = Number(defaults, "skinIndex", game.SkinIndex, applied);

        game.AllowJackpot = Flag(defaults, "allowJackpot", game.AllowJackpot, applied);
        game.Tournament = Flag(defaults, "tournament", game.Tournament, applied);
        game.Tutorial = Flag(defaults, "tutorial", game.Tutorial, applied);
        game.Bloodstone = Flag(defaults, "bloodstone", game.Bloodstone, applied);
        game.RetailGame = Flag(defaults, "retailGame", game.RetailGame, applied);
        game.AllowScriptedRolls =
            Flag(defaults, "allowScriptedRolls", game.AllowScriptedRolls, applied);

        return applied;
    }

    private static int Number(
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

    private static bool Flag(
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

    private static void ApplyArray(
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
