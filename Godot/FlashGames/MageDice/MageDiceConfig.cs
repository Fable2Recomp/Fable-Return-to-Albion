using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.MageDice;

/// <summary>
/// The keys in <c>magedice/settings.xml</c> that only Mage Dice understands.
/// </summary>
/// <remarks>
/// FORTY-EIGHT ARRAYS ACROSS FORTY-ONE VARIATIONS, in a 503 KB file. This is the most dangerous
/// code in the port, because every way of getting it wrong is silent: bind <c>ReelPayoutsx3B</c>
/// to the wrong board type and the machine draws one paytable and settles by another, with nothing
/// on screen to say so. So the binding is written out flat, one line per setting, next to the
/// board type it belongs to — no loops over suffixes, nothing computed. It should be readable
/// against the XML by eye.
///
/// EVERY ARRAY THAT LANDS IS LOGGED WITH ITS LENGTH, for the same reason: a table that quietly
/// fell back to the defaults looks exactly like one that applied.
/// </remarks>
public static class MageDiceConfig
{
    public static List<string> Apply(
        MageDice game, PubGameSettings? shared, PubGameSettings? own)
    {
        var applied = new List<string>();
        Dictionary<string, PubGameSetting> defaults =
            PubGameConfig.Apply(game, shared, own, applied);

        game.AllowDebug = PubGameConfig.Flag(defaults, "allowDebug", game.AllowDebug, applied);

        game.WinThreshold3 =
            PubGameConfig.Number(defaults, "winThreshold3", game.WinThreshold3, applied);
        game.WinThreshold4 =
            PubGameConfig.Number(defaults, "winThreshold4", game.WinThreshold4, applied);
        game.WinThreshold5 =
            PubGameConfig.Number(defaults, "winThreshold5", game.WinThreshold5, applied);

        // FRACTIONAL, SO NOT PubGameConfig.Number. reelSpeedBoost is 2.5 and the spin extensions
        // are sub-second; rounding them to integers would either stop the reels dead or double the
        // hold on a bonus tease.
        game.ReelSpeedBoost =
            PubGameConfig.Real(defaults, "reelSpeedBoost", game.ReelSpeedBoost, applied);
        game.SpinTimeExtension1 =
            PubGameConfig.Real(defaults, "spinTimeExtension1", game.SpinTimeExtension1, applied);
        game.SpinTimeExtension2 =
            PubGameConfig.Real(defaults, "spinTimeExtension2", game.SpinTimeExtension2, applied);
        game.TourneyWinThresholdMultiplier = PubGameConfig.Real(
            defaults, "tourneyWinThresholdMultiplier", game.TourneyWinThresholdMultiplier, applied);

        // TWELVE NUMBERS READ AS SIX MIN/MAX PAIRS OF SECONDS. Rounding these to integers would
        // make every reel land on the same beat, or not spin at all.
        PubGameConfig.FillDoubles(defaults, "spinTimes", game.SpinTimes, applied);

        // The three board types. Suffix "" is the table the player sits at; B and C are the extra
        // machines the tutorial walks through.
        Tables(defaults, game.TablesA, string.Empty, applied);
        Tables(defaults, game.TablesB, "B", applied);
        Tables(defaults, game.TablesC, "C", applied);

        return applied;
    }

    /// <summary>
    /// Reads one board type's fifteen arrays and its reel count.
    /// </summary>
    /// <remarks>
    /// The suffix is the ONLY thing that varies between the three calls, and it is passed rather
    /// than derived so the setting names below read exactly as they appear in the file.
    /// </remarks>
    private static void Tables(
        Dictionary<string, PubGameSetting> defaults, MageDiceTables tables, string suffix,
        List<string> applied)
    {
        tables.BoardType =
            PubGameConfig.Number(defaults, "BoardType" + suffix, tables.BoardType, applied);

        PubGameConfig.FillText(defaults, "ReelImages" + suffix, tables.ReelImages, applied);
        PubGameConfig.Fill(defaults, "ReelTypes" + suffix, tables.ReelTypes, applied);

        PubGameConfig.Fill(defaults, "Reel1Stops" + suffix, tables.Stops[0], applied);
        PubGameConfig.Fill(defaults, "Reel2Stops" + suffix, tables.Stops[1], applied);
        PubGameConfig.Fill(defaults, "Reel3Stops" + suffix, tables.Stops[2], applied);
        PubGameConfig.Fill(defaults, "Reel4Stops" + suffix, tables.Stops[3], applied);
        PubGameConfig.Fill(defaults, "Reel5Stops" + suffix, tables.Stops[4], applied);
        PubGameConfig.Fill(defaults, "Reel6Stops" + suffix, tables.Stops[5], applied);

        PubGameConfig.Fill(defaults, "ReelPayoutsx1" + suffix, tables.Payouts[0], applied);
        PubGameConfig.Fill(defaults, "ReelPayoutsx2" + suffix, tables.Payouts[1], applied);
        PubGameConfig.Fill(defaults, "ReelPayoutsx3" + suffix, tables.Payouts[2], applied);
        PubGameConfig.Fill(defaults, "ReelPayoutsx4" + suffix, tables.Payouts[3], applied);
        PubGameConfig.Fill(defaults, "ReelPayoutsx5" + suffix, tables.Payouts[4], applied);
        PubGameConfig.Fill(defaults, "ReelPayoutsx6" + suffix, tables.Payouts[5], applied);
    }
}
