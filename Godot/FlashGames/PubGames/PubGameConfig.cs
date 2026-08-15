using System;
using System.Collections.Generic;
using System.Globalization;

namespace Refii.FlashGames.PubGames;

/// <summary>
/// Applies a pub game's shipped <c>settings.xml</c> to the game object.
/// </summary>
/// <remarks>
/// THE VARIATION IS THE TABLE. The guest names one of the 28 variations in
/// <c>keystone/settings.xml</c> when it starts a game — Standard1 through Standard5, the four
/// Jackpot tables, three Bloodstone tables, five Tournament tables, the KS tables and Tutorial —
/// and that name is the entire difference between one pub table and another. A variation is a
/// patch over <c>&lt;defaults&gt;</c>, so it is merged on top rather than read instead.
///
/// THE ARRAYS ARE NOT DECORATION. Every one of the 28 variations carries its own 46-entry
/// <c>payoutRate</c> array, and they genuinely differ: Standard1 pays 18:1 on OUT3 where the
/// game's built-in default is 35:1. Reading only the scalars and leaving the arrays on the floor
/// means every table in the game quietly pays the wrong odds — the board draws one number and the
/// settlement uses another.
/// </remarks>
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
            Dictionary<string, PubGameSetting> variation = own.Section(game.VariantName);
            foreach ((string key, PubGameSetting value) in variation)
            {
                defaults[key] = value;
            }

            // SAID EITHER WAY. A variation the file does not have is not an error the guest can
            // see — it just plays the defaults — so an unknown name has to be visible here or a
            // table that silently reverted to house odds looks exactly like one that applied.
            applied.Add(variation.Count == 0
                ? $"variant:{game.VariantName}(NOT IN FILE)"
                : $"variant:{game.VariantName}({variation.Count})");
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
        game.MinGamePoints = Number(defaults, "minGamePoints", game.MinGamePoints, applied);
        game.SkinIndex = Number(defaults, "skinIndex", game.SkinIndex, applied);
        game.TournamentRounds =
            Number(defaults, "tournamentRounds", game.TournamentRounds, applied);
        game.UnlockedItemIndex =
            Number(defaults, "unlockedItemIndex", game.UnlockedItemIndex, applied);

        game.AllowJackpot = Flag(defaults, "allowJackpot", game.AllowJackpot, applied);
        game.Tournament = Flag(defaults, "tournament", game.Tournament, applied);
        game.Tutorial = Flag(defaults, "tutorial", game.Tutorial, applied);
        game.Bloodstone = Flag(defaults, "bloodstone", game.Bloodstone, applied);
        game.RetailGame = Flag(defaults, "retailGame", game.RetailGame, applied);
        game.AllowScriptedRolls =
            Flag(defaults, "allowScriptedRolls", game.AllowScriptedRolls, applied);

        // THE ODDS THE TABLE ACTUALLY PAYS. Positional, in KeystoneSpace order — see
        // Keystone.SetPayoutRates for why a short array is merged rather than assigned.
        if (Numbers(defaults, "payoutRate") is { Count: > 0 } rates)
        {
            game.SetPayoutRates(rates);
            applied.Add($"payoutRate[{rates.Count}]");
        }

        Fill(defaults, "tournamentPayouts", game.TournamentPayouts, applied);
        Fill(defaults, "gameUnlockPoints", game.GameUnlockPoints, applied);
        Fill(defaults, "gameUnlockMin", game.GameUnlockMin, applied);
        Fill(defaults, "gameUnlockMax", game.GameUnlockMax, applied);
        FillText(defaults, "gameUnlockName", game.GameUnlockName, applied);

        // ONLY WHEN THE TABLE ASKED FOR THEM. `scriptedRolls` is the authored dice sequence the
        // tutorial table plays against; loading it on a live table would replace the RNG with a
        // fixed script, so the flag gates the queue rather than the queue implying the flag.
        if (game.AllowScriptedRolls && Numbers(defaults, "scriptedRolls") is { Count: >= 3 } script)
        {
            for (int i = 0; i + 2 < script.Count; i += 3)
            {
                game.PushScriptedRoll(script[i], script[i + 1], script[i + 2]);
            }

            applied.Add($"scriptedRolls[{script.Count / 3}]");
        }

        return applied;
    }

    /// <summary>The items of an array setting, as numbers; empty when it is absent.</summary>
    private static List<int> Numbers(
        Dictionary<string, PubGameSetting> settings, string name)
    {
        var values = new List<int>();
        if (!settings.TryGetValue(name, out PubGameSetting? setting))
        {
            return values;
        }

        foreach (string item in setting.Items)
        {
            // ROUNDED, NOT TRUNCATED. The file declares these `type="number"`, which in
            // ActionScript 2 is a double, and a few are authored as "2.0".
            if (double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double value))
            {
                values.Add((int)Math.Round(value));
            }
        }

        return values;
    }

    private static void Fill(
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

    private static void FillText(
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
