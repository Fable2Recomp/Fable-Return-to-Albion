using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.WizardsTower;

public static class WizardsTowerConfig
{
    public static List<string> Apply(
        WizardsTower game, PubGameSettings? shared, PubGameSettings? own)
    {
        var applied = new List<string>();
        Dictionary<string, PubGameSetting> defaults =
            PubGameConfig.Apply(game, shared, own, applied);

        game.RangeOfCards =
            PubGameConfig.Number(defaults, "RangeOfCards", game.RangeOfCards, applied);
        game.NumberOfCards =
            PubGameConfig.Number(defaults, "NumberOfCards", game.NumberOfCards, applied);
        game.NumberOfHeroes =
            PubGameConfig.Number(defaults, "NumberOfHeroes", game.NumberOfHeroes, applied);
        game.NumberOfRows =
            PubGameConfig.Number(defaults, "NumberOfRows", game.NumberOfRows, applied);

        game.LowAIBuyInMultiple =
            PubGameConfig.Number(defaults, "LowAIBuyInMultiple", game.LowAIBuyInMultiple, applied);
        game.HighAIBuyInMultiple =
            PubGameConfig.Number(defaults, "HighAIBuyInMultiple", game.HighAIBuyInMultiple, applied);
        game.NoSinglePlayerAIs =
            PubGameConfig.Flag(defaults, "NoSinglePlayerAIs", game.NoSinglePlayerAIs, applied);

        game.RedTint = PubGameConfig.Real(defaults, "RedTint", game.RedTint, applied);
        game.GreenTint = PubGameConfig.Real(defaults, "GreenTint", game.GreenTint, applied);
        game.BlueTint = PubGameConfig.Real(defaults, "BlueTint", game.BlueTint, applied);

        game.Cheats = PubGameConfig.Number(defaults, "Cheats", game.Cheats, applied);

        PubGameConfig.FillDoubles(defaults, "AIPlayTable", game.AIPlayTable, applied);

        PubGameConfig.Fill(defaults, "StackedDeck", game.StackedDeck, applied);

        return applied;
    }
}
