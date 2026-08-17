using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.MageDice;

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

        game.ReelSpeedBoost =
            PubGameConfig.Real(defaults, "reelSpeedBoost", game.ReelSpeedBoost, applied);
        game.SpinTimeExtension1 =
            PubGameConfig.Real(defaults, "spinTimeExtension1", game.SpinTimeExtension1, applied);
        game.SpinTimeExtension2 =
            PubGameConfig.Real(defaults, "spinTimeExtension2", game.SpinTimeExtension2, applied);
        game.TourneyWinThresholdMultiplier = PubGameConfig.Real(
            defaults, "tourneyWinThresholdMultiplier", game.TourneyWinThresholdMultiplier, applied);

        PubGameConfig.FillDoubles(defaults, "spinTimes", game.SpinTimes, applied);

        Tables(defaults, game.TablesA, string.Empty, applied);
        Tables(defaults, game.TablesB, "B", applied);
        Tables(defaults, game.TablesC, "C", applied);

        return applied;
    }

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
