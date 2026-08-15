using System;

namespace Refii.FlashGames.PubGames;

/// <summary>
/// Lighting up what the roll won, and the arrows that point at the stone it takes.
/// </summary>
/// <remarks>
/// TWO PASSES, NOT ONE, and the split is the whole reason this reads oddly. Every winning space
/// lights immediately EXCEPT the arch spaces, which are dealt with second and in a deliberate
/// order: the sweep starts at the space the roll totalled and walks toward the stone being
/// removed, one space at a time with a shrinking delay, so the eye is led from "you rolled 11" to
/// "that stone comes out". Lighting them all at once loses the causation the animation exists to
/// show.
///
/// THE CUE COUNT IS A SOUND INDEX, not a decoration. Each hilight plays
/// <c>K_showWinHilight0{n}</c> with n rising per space and capped at 8, so a board full of wins
/// climbs a scale. It has to keep counting across both passes, which is why it is threaded through
/// rather than derived from the loop index.
///
/// Ported from <c>Keystone::HilightWinningSpaces</c> — read it with
/// <c>tools/as2dis.py &lt;game.swf&gt; --class Keystone --method HilightWinningSpaces --pseudo</c>.
/// </remarks>
public sealed partial class Keystone
{
    /// <summary>The delay between each step of the arch sweep, in milliseconds.</summary>
    public const double ArchSweepStep = 150;

    /// <summary>Where the arrow sweep starts, before it shortens by 20 ms per space crossed.</summary>
    public const double ArrowSweepBase = 250;

    /// <summary>The most distinct hilight cues there are.</summary>
    public const int MaxHilightCue = 8;

    public void HilightWinningSpaces(object? group)
    {
        int rolled = RollTotal - 3 + KeystoneSpace.Out3;
        int cue = 1;

        // FIRST PASS: everything that is not an arch space, plus the arch only in the one case
        // where the rolled space IS the stone being removed — there is no sweep to make then.
        for (int space = KeystoneSpace.FirstBetSpace; space <= KeystoneSpace.LastBetSpace; space++)
        {
            if (!_isWinningSpace[space])
            {
                continue;
            }

            bool arch = space >= KeystoneSpace.Out3 && space <= KeystoneSpace.Out18;
            if (arch && !(space == ArchStoneToRemove && ArchStoneToRemove == rolled))
            {
                continue;
            }

            Board?.ShowHilight(space, true, group, true, 0, cue);
            cue++;
        }

        if (JackpotRolled)
        {
            Board?.ShowJackpot(false, group);
            if (!Tutorial)
            {
                AchievementEarned("KS_Jackpot");
            }
        }

        // SECOND PASS: the arch, swept. Which way depends on where the rolled total sits — from
        // OUT3 the sweep can only run up, from OUT18 only down, and in between it runs toward the
        // stone. The last space in the sweep is the one that blocks, so the animation that
        // finishes the sequence is the one everything else waits on.
        if (rolled != ArchStoneToRemove)
        {
            SweepArch(rolled, group, ref cue);
            return;
        }

        SweepArrows(rolled, group, cue);
    }

    /// <summary>
    /// Lights the winning arch spaces in order, ending on the stone that comes out.
    /// </summary>
    private void SweepArch(int rolled, object? group, ref int cue)
    {
        // TAKEN VERBATIM FROM THE BRANCHES at 0xab22-0xac03, because they are not the symmetric
        // pair they look like: the sweep runs UP from OUT3 only when the roll totalled OUT3, and
        // DOWN from OUT18 in every other case. Deriving a direction from where the rolled space
        // sits would give a different answer for most of the board.
        bool fromLow = rolled == KeystoneSpace.Out3;
        int first = fromLow ? KeystoneSpace.Out3 : KeystoneSpace.Out18;
        int stop = fromLow ? KeystoneSpace.Out18 + 1 : KeystoneSpace.Out3 - 1;
        int last = fromLow ? KeystoneSpace.Out18 : KeystoneSpace.Out3;
        int step = fromLow ? 1 : -1;

        int shown = 0;
        for (int space = first; space != stop; space += step)
        {
            if (!_isWinningSpace[space] || space == rolled)
            {
                continue;
            }

            Board?.ShowHilight(
                space, true, group, space == last, ArchSweepStep * shown, cue);
            cue++;
            shown++;
        }
    }

    /// <summary>
    /// Walks arrows from the rolled total to the stone being removed, then lights the stone.
    /// </summary>
    /// <remarks>
    /// THE DELAY SHORTENS WITH DISTANCE. The whole sweep takes about the same time whether it
    /// crosses two spaces or ten, because the per-space delay is 250 ms less 20 per space between
    /// them — a long sweep hurries, a short one lingers.
    /// </remarks>
    private void SweepArrows(int rolled, object? group, int cue)
    {
        int step = rolled > KeystoneSpace.Out10 ? -1 : 1;
        double delay = ArrowSweepBase - (20 * Math.Abs(ArchStoneToRemove - rolled));

        for (int space = rolled; space != ArchStoneToRemove; space += step)
        {
            Board?.ShowArrow(space, true, delay, group, true);
        }

        Board?.ShowHilight(ArchStoneToRemove, true, group, true, delay, cue);
        Board?.ShowNumberAbove(rolled, false, group, false, 0);
    }

    public void UnHilightWinningSpaces(object? group)
    {
        for (int space = KeystoneSpace.FirstBetSpace; space <= KeystoneSpace.LastBetSpace; space++)
        {
            if (_isWinningSpace[space])
            {
                Board?.ShowHilight(space, false, group, false, 0, 0);
            }
        }

        Board?.ResetAllArrows();
    }
}
