using System.Collections.Generic;

namespace Refii.FlashGames.Framework;

public interface IPubGameHost
{
    void Command(string command, string? argument);
}

public sealed class PubGameHostLog : IPubGameHost
{
    public List<string> Commands { get; } = new();

    public void Command(string command, string? argument)
    {
        Commands.Add(argument is null ? command : $"{command}:{argument}");
    }
}

public static class PubCommand
{
    public const string AchievementEarned = "AchievementEarned";
    public const string Assert = "Assert";
    public const string GameOver = "GameOver";
    public const string LoadGameSettings = "LoadGameSettings";
    public const string LoadTutorialSettings = "LoadTutorialSettings";
    public const string MergeWithCharacter = "MergeWithCharacter";
    public const string NewRank = "NewRank";
    public const string PauseSound = "PauseSound";
    public const string PlaySound = "PlaySound";
    public const string PointsEarned = "PointsEarned";
    public const string Print = "Print";
    public const string Profile = "Profile";
    public const string Running = "Running";
    public const string TournamentBuyIn = "TournamentBuyIn";
    public const string TournamentCompleted = "TournamentCompleted";
    public const string UpdateMusicVolume = "UpdateMusicVolume";
    public const string UpdatePlayerHoldings = "UpdatePlayerHoldings";
    public const string UpdateSoundVolume = "UpdateSoundVolume";
}

public sealed class Audio
{
    private readonly IPubGameHost _host;

    public Audio(IPubGameHost host)
    {
        _host = host;
    }

    public void PlaySound(string soundName)
    {
        _host.Command(PubCommand.PlaySound, soundName);
    }

    public void PauseLoopingSounds(bool pause)
    {
        _host.Command(PubCommand.PauseSound, pause ? "true" : "false");
    }
}
