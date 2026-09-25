using System;

namespace Refii.FlashGames.Keystone;

public sealed partial class Keystone
{
    public const double ArchSweepStep = 150;

    public const double ArrowSweepBase = 250;

    /// <summary>
    /// <c>HilightWinningSpaces</c> (0x00a9c2-0x00adfd). Every winning inside space lights; an arch
    /// space only when it is the stone the dice named. A jackpot sweeps the arch from the rolled
    /// end; a roll whose stone is already gone walks the seeker ghosts outward to the stone that
    /// falls instead. The seeker number above the arch always fades.
    /// </summary>
    public void HilightWinningSpaces(object? group)
    {
        int rolled = RollTotal - 3 + KeystoneSpace.Out3;
        int cue = 1;

        for (int space = KeystoneSpace.FirstBetSpace; space <= KeystoneSpace.LastBetSpace; space++)
        {
            if (!_isWinningSpace[space])
            {
                continue;
            }

            if (!IsArchSpace(space) || (space == ArchStoneToRemove && ArchStoneToRemove == rolled))
            {
                Board?.ShowHilight(space, true, group, true, 0, cue);
                cue++;
            }
        }

        if (JackpotRolled)
        {
            Board?.ShowJackpot(false, group);
            if (!Tutorial)
            {
                AchievementEarned("KS_Jackpot");
            }

            bool fromLow = rolled == KeystoneSpace.Out3;
            int first = fromLow ? KeystoneSpace.Out3 : KeystoneSpace.Out18;
            int stop = fromLow ? KeystoneSpace.Out18 + 1 : KeystoneSpace.Out3 - 1;
            int last = fromLow ? KeystoneSpace.Out18 : KeystoneSpace.Out3;
            int step = fromLow ? 1 : -1;

            int shown = 0;
            for (int space = first; space != stop; space += step)
            {
                if (_isWinningSpace[space] && space != rolled)
                {
                    Board?.ShowHilight(
                        space, true, group, space == last, ArchSweepStep * shown, cue);
                    cue++;
                    shown++;
                }
            }
        }
        else if (rolled != ArchStoneToRemove)
        {
            int step = rolled > KeystoneSpace.Out10 ? 1 : -1;
            double delay = ArrowSweepBase - (20 * Math.Abs(ArchStoneToRemove - rolled));
            for (int space = rolled; space != ArchStoneToRemove; space += step)
            {
                Board?.ShowArrow(space, true, delay, group, true);
            }

            Board?.ShowHilight(ArchStoneToRemove, true, group, true, delay, cue);
        }

        Board?.ShowNumberAbove(rolled, false, group, false, 0);
    }

    /// <summary><c>UnHilightWinningSpaces</c> (0x007462-0x0074f1).</summary>
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
