using System;
using System.Collections.Generic;
using System.Globalization;

namespace Refii.FlashGames.Framework;

/// <summary>
/// Applies the part of a pub game's shipped <c>settings.xml</c> that every pub game has.
/// </summary>
/// <remarks>
/// THE VARIATION IS THE TABLE. The guest names one variation when it starts a game — 28 of them in
/// <c>keystone/settings.xml</c>, 41 in <c>magedice/settings.xml</c> — and that name is the entire
/// difference between one pub table and another. A variation is a patch over
/// <c>&lt;defaults&gt;</c>, so it is merged on top rather than read instead.
///
/// THE ARRAYS ARE NOT DECORATION. Every Keystone variation carries its own 46-entry
/// <c>payoutRate</c>, and every Mage Dice variation its own reel stop and payout tables. Reading
/// only the scalars and leaving the arrays on the floor means every table in the game quietly pays
/// the wrong odds — the board draws one number and the settlement uses another.
///
/// WHAT IS HERE IS WHAT <see cref="PubGame"/> OWNS. The keys a particular game understands are
/// that game's business, so <see cref="Apply"/> hands back the merged section for its caller to
/// keep reading — see <c>KeystoneConfig</c>. The parsing helpers are public for the same reason.
/// </remarks>
public static class PubGameConfig
{
    public const string DefaultsSection = "defaults";
    public const string GlobalSection = "globalsettings";

    /// <summary>
    /// Applies the shared settings and returns the merged <c>defaults</c> + variation section.
    /// </summary>
    /// <param name="game">The game to configure.</param>
    /// <param name="shared"><c>common/settings.xml</c>, which carries the star tables.</param>
    /// <param name="own">The game's own <c>settings.xml</c>.</param>
    /// <param name="applied">Every setting that landed, for the log.</param>
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

            // SAID EITHER WAY. A variation the file does not have is not an error the guest can
            // see — it just plays the defaults — so an unknown name has to be visible here or a
            // table that silently reverted to house odds looks exactly like one that applied.
            applied.Add(variation.Count == 0
                ? $"variant:{game.VariantName}(NOT IN FILE)"
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

    /// <summary>The items of an array setting, as numbers; empty when it is absent.</summary>
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

    /// <summary>
    /// The items of an array setting, as they were written.
    /// </summary>
    /// <remarks>
    /// NOT ROUNDED, UNLIKE <see cref="Numbers"/>, and both are needed. Keystone's arrays are
    /// counts and payout ratios, so rounding them is right. Mage Dice's <c>spinTimes</c> is twelve
    /// reel stop times in SECONDS — 1.5, 2.5, 0.4, 1.0, 0.4 … — and rounding those turns a
    /// staggered reel stop into every reel landing at once, or not spinning at all.
    /// </remarks>
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

    /// <summary>A single fractional setting — <c>reelSpeedBoost</c>, <c>spinTimeExtension1</c>.</summary>
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
