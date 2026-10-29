using System.Collections.Generic;

using Refii.Titles.Fable2.PubGames.Framework;

namespace Refii.Titles.Fable2.PubGames.Keystone;

public static class KeystoneConfig
{
    public static List<string> Apply(
        Keystone game, PubGameSettings? shared, PubGameSettings? own)
    {
        var applied = new List<string>();
        Dictionary<string, PubGameSetting> defaults =
            PubGameConfig.Apply(game, shared, own, applied);

        game.MinInsideBet =
            PubGameConfig.Number(defaults, "minInsideBet", game.MinInsideBet, applied);
        game.MaxInsideBet =
            PubGameConfig.Number(defaults, "maxInsideBet", game.MaxInsideBet, applied);
        game.ApplauseThreshold3 =
            PubGameConfig.Number(defaults, "applauseThreshold3", game.ApplauseThreshold3, applied);
        game.ApplauseThreshold4 =
            PubGameConfig.Number(defaults, "applauseThreshold4", game.ApplauseThreshold4, applied);

        game.AllowJackpot = PubGameConfig.Flag(defaults, "allowJackpot", game.AllowJackpot, applied);
        game.Bloodstone = PubGameConfig.Flag(defaults, "bloodstone", game.Bloodstone, applied);
        game.AllowScriptedRolls =
            PubGameConfig.Flag(defaults, "allowScriptedRolls", game.AllowScriptedRolls, applied);

        if (PubGameConfig.Numbers(defaults, "payoutRate") is { Count: > 0 } rates)
        {
            game.SetPayoutRates(rates);
            applied.Add($"payoutRate[{rates.Count}]");
        }

        if (game.AllowScriptedRolls &&
            PubGameConfig.Numbers(defaults, "scriptedRolls") is { Count: >= 3 } script)
        {
            for (int i = 0; i + 2 < script.Count; i += 3)
            {
                game.PushScriptedRoll(script[i], script[i + 1], script[i + 2]);
            }

            applied.Add($"scriptedRolls[{script.Count / 3}]");
        }

        return applied;
    }
}
