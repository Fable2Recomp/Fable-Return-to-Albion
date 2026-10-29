using System.Collections.Generic;

using Refii.Titles.Fable2.PubGames.Framework;

namespace Refii.Titles.Fable2.PubGames.Framework;

public sealed class PubGameCommandSink : IPubGameHost
{
    private readonly List<string> _commands = new();

    public IReadOnlyList<string> Commands => _commands;

    /// <summary>
    /// Where the commands go after they are recorded: the guest, when a session is live.
    /// </summary>
    /// <remarks>
    /// Every fscommand the game issues is the movie talking to the guest — sounds, holdings,
    /// points, rank, volumes, GameOver. Null in the harnesses, which have no guest to tell.
    /// </remarks>
    public IPubGameHost? Forward { get; set; }

    public bool GameOverIssued { get; private set; }

    public bool ReloadRequested { get; private set; }

    public bool TutorialRequested { get; private set; }

    public void Command(string command, string? argument)
    {
        // Bounded: the list is a harness trace, and a session plays thousands of sounds.
        if (_commands.Count < 4096)
        {
            _commands.Add(argument is null ? command : $"{command}:{argument}");
        }

        switch (command)
        {
            case PubCommand.GameOver:
                GameOverIssued = true;
                break;
            case PubCommand.LoadGameSettings:
                ReloadRequested = true;
                break;
            case PubCommand.LoadTutorialSettings:
                TutorialRequested = true;
                break;
        }

        Forward?.Command(command, argument);
    }
}
