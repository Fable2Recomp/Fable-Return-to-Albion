using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.Keystone;

/// <summary>
/// The keys in <c>keystone/settings.xml</c> that only Keystone understands.
/// </summary>
/// <remarks>
/// <see cref="PubGameConfig.Apply"/> does the merge and everything <see cref="PubGame"/> itself
/// owns, then hands back the merged section; this reads Keystone's own keys out of it. The split
/// is not cosmetic — the framework applying a key called <c>bloodstone</c> would mean every future
/// game inherits a setting it has no idea about.
/// </remarks>
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

        // THE ODDS THE TABLE ACTUALLY PAYS. Positional, in KeystoneSpace order — see
        // Keystone.SetPayoutRates for why a short array is merged rather than assigned.
        if (PubGameConfig.Numbers(defaults, "payoutRate") is { Count: > 0 } rates)
        {
            game.SetPayoutRates(rates);
            applied.Add($"payoutRate[{rates.Count}]");
        }

        // ONLY WHEN THE TABLE ASKED FOR THEM. `scriptedRolls` is the authored dice sequence the
        // tutorial table plays against; loading it on a live table would replace the RNG with a
        // fixed script, so the flag gates the queue rather than the queue implying the flag.
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
