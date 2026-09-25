namespace Refii.FlashGames.Framework;

/// <summary>
/// What the guest hands the movie before <c>pubGame.Run</c>, read from its own profile.
/// </summary>
/// <remarks>
/// <c>sub_82EF18A0</c>, in its order: <c>AddHumanPlayer(0, name, gold, debt, rank, total points,
/// this game's points)</c>, the settings, <c>TournamentItemWon(i)</c> per profile bit, the
/// localization, then <c>wideScreen</c>, <c>retailGame</c> (always 1 in Fable II —
/// <c>stb 1, 0x468</c> at 0x82EF0B4C), <c>trialVersion</c> (0) and the two volumes as
/// <c>(int)(v * 10)</c>. <see cref="Default"/> is the harness's stand-in for a player.
/// </remarks>
public sealed record PubGameSeed(
    string Name,
    int Gold,
    int Debt,
    int StarRating,
    int TotalPoints,
    long[] Points,
    uint Items,
    int SoundVolume,
    int MusicVolume,
    bool WideScreen,
    string Language)
{
    public static PubGameSeed Default(int gold) =>
        new("Hero", gold, 0, 1, 0, [0, 0, 0], 0, 5, 5, true, "en");

    /// <summary>The human player as <c>pubGame.AddHumanPlayer</c> would build it.</summary>
    /// <remarks>Needs the settings applied first: the game's own point slot is its gameIndex.</remarks>
    public Player CreateHuman(PubGame game)
    {
        long gamePoints = game.GameIndex >= 0 && game.GameIndex < Points.Length
            ? Points[game.GameIndex]
            : 0;
        Player human = game.CreatePlayerObject(
            0, false, Name, Gold, Debt, StarRating, TotalPoints, (int)gamePoints);
        game.PlayerInfo.AddPlayer(0, human);
        return human;
    }

    /// <summary>Everything the guest sets after the settings and before <c>Run</c>.</summary>
    public void ApplyAfterSettings(PubGame game)
    {
        for (int item = 0; item < 32; item++)
        {
            if ((Items & (1u << item)) != 0)
            {
                game.TournamentItemWon(item);
            }
        }

        game.WideScreen = WideScreen;
        game.RetailGame = true;
        game.TrialVersion = false;
        game.SoundVolume = SoundVolume;
        game.MusicVolume = MusicVolume;
    }
}
