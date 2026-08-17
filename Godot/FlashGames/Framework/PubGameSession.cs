namespace Refii.FlashGames.Framework;

/// <summary>
/// What a hosted pub game has to be able to tell the guest.
/// </summary>
/// <remarks>
/// THE GUEST SETTLES, NOT THE GAME. It seeded the game's bank from the hero's real gold before the
/// session and credits the DIFFERENCE afterwards, so these are ABSOLUTE balances rather than
/// winnings — handing back a delta would pay the player twice. See the block comment above
/// <c>RefiiHostGetPubGame</c> in <c>host_bridge.cpp</c>.
///
/// SMALL ON PURPOSE. <c>GamePubGames</c> polls the guest, decides a session has started and later
/// settles it; the only things it needs from whichever game is on screen are whether it is over and
/// what the player finished with. Everything else — the board, the overlays, the rules — is the
/// game's own business, and a wider interface would invite the dispatcher to reach into it.
/// </remarks>
public interface IPubGameSession
{
    /// <summary>
    /// True once the game has settled and issued its own <c>GameOver</c>.
    /// </summary>
    /// <remarks>
    /// GATED ON THE GAME'S COMMAND, not on an overlay closing. The settlement chain nets chips
    /// against debt and turns the remainder into gold before the command is issued, so a host that
    /// samples the balance any earlier reads it before the money has moved.
    /// </remarks>
    bool IsFinished { get; }

    long FinalGold { get; }

    long FinalDebt { get; }
}
