namespace Refii.FlashGames.Framework;

public interface IPubGameSession
{
    /// <summary>The game says it is over: <c>FSCommand:GameOver</c> has gone to the guest.</summary>
    bool IsFinished { get; }

    /// <summary>
    /// The game asked to be reloaded: <c>LoadGameSettings</c> (the chosen table, after the
    /// tutorial) or <c>LoadTutorialSettings</c>. The guest reloads the movie in place
    /// (<c>sub_82EF2708</c>, <c>game+1160/+1161</c>) without ending the session.
    /// </summary>
    bool ReloadRequested { get; }

    bool TutorialRequested { get; }

    /// <summary>The game itself, for what the guest tells the movie: StartSave, EndSave, EndGame.</summary>
    PubGame? Game { get; }

    /// <summary>
    /// The board never came up, so there is nothing to settle and nobody to settle it.
    /// </summary>
    /// <remarks>
    /// A host that cannot load must still answer: the guest waits in state 3 indefinitely. The
    /// caller hands it GameOver with the profile untouched, which settles a delta of zero.
    /// </remarks>
    bool Failed { get; }
}
