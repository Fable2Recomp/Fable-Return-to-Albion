using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.Framework;

public sealed class PubGameCommandSink : IPubGameHost
{
    private readonly List<string> _commands = new();

    public IReadOnlyList<string> Commands => _commands;

    public bool GameOverIssued { get; private set; }

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
