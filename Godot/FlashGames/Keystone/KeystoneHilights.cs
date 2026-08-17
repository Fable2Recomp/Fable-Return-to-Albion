using System;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.Keystone;

public sealed partial class Keystone
{
    public const double ArchSweepStep = 150;

    public const double ArrowSweepBase = 250;

    public const int MaxHilightCue = 8;

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

        if (rolled != ArchStoneToRemove)
        {
            SweepArch(rolled, group, ref cue);
            return;
        }

        SweepArrows(rolled, group, cue);
    }

    private void SweepArch(int rolled, object? group, ref int cue)
    {
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
