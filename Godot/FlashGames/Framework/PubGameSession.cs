namespace Refii.FlashGames.Framework;

public interface IPubGameSession
{
    bool IsFinished { get; }

    long FinalGold { get; }

    long FinalDebt { get; }

    /// <summary>
    /// The board never came up, so there is nothing to settle and nobody to settle it.
    /// </summary>
    /// <remarks>
    /// A HOST THAT CANNOT LOAD MUST STILL BE ANSWERED. The guest waits at state 2 for as long as
    /// it takes; a host whose <c>Load</c> failed never finishes, never reports a balance, and the
    /// player sits in front of a black screen forever. Keystone had a second chance at this — it
    /// falls back to building the stage from the SWF when the baked scene will not load — and Mage
    /// Dice and Wizard's Tower have only the one path, so the failure is theirs to hit first.
    ///
    /// The caller settles the balance UNCHANGED when this is set, which is the same answer an
    /// unrecognised game gets: a delta of zero, and the player back in the world having lost
    /// nothing.
    /// </remarks>
    bool Failed { get; }
}
