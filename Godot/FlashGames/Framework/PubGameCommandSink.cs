using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.Framework;

/// <summary>
/// Receives the commands the game issues to whoever is hosting it.
/// </summary>
/// <remarks>
/// THESE ARE THE GAME'S OWN WORDS. In the shipped title the pub games are a Flash movie and every
/// one of these is an <c>FSCommand</c> the movie sends out to the C++ side — "GameOver",
/// "PlaySound", "AchievementEarned", "PointsEarned", "UpdatePlayerHoldings". They are the stated
/// interface between the game and its host, so a host follows them rather than inferring the same
/// facts from the game's internal state.
///
/// <c>GameOver</c> IS THE END OF THE SESSION, and nothing else is. It is issued from
/// <c>GameReallyOver</c>, which the fade-out's completion callback calls, which
/// <c>PubGame::GameOver</c> starts — and <c>PubGame::GameOver</c> is where the player's chips are
/// netted against their debt and the remainder becomes gold. So a host that watches for this
/// command cannot read the balance before the settlement has run, which a host watching an
/// overlay's own "I closed" flag can and did.
/// </remarks>
public sealed class PubGameCommandSink : IPubGameHost
{
    private readonly List<string> _commands = new();

    /// <summary>Every command issued this session, in order, for a log or a probe.</summary>
    public IReadOnlyList<string> Commands => _commands;

    /// <summary>The game has settled and is done.</summary>
    public bool GameOverIssued { get; private set; }

    /// <summary>
    /// The retail tutorial's ending, which is a request to reload rather than an ending.
    /// </summary>
    /// <remarks>
    /// <c>GameReallyOver</c> sends <c>LoadGameSettings</c> instead of <c>GameOver</c> when the
    /// retail build finishes the tutorial, because the title's next move is to start a real game
    /// at the same table. There is no tutorial here yet; the distinction is kept because treating
    /// it as an ordinary <c>GameOver</c> would settle a session that has not happened.
    /// </remarks>
    public bool ReloadRequested { get; private set; }

    public void Command(string command, string? argument)
    {
        _commands.Add(argument is null ? command : $"{command}:{argument}");

        switch (command)
        {
            case PubCommand.GameOver:
                GameOverIssued = true;
                break;
            case PubCommand.LoadGameSettings:
                ReloadRequested = true;
                break;
        }
    }
}
