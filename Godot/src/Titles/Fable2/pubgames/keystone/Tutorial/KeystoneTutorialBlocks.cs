using Refii.Titles.Fable2.PubGames.Framework;

namespace Refii.Titles.Fable2.PubGames.Keystone;

public sealed partial class Keystone
{
    private void EnterBlock(int block, int stage)
    {
        switch (block, stage)
        {
            case (2, 2): EnterBlock2Stage2(); break;
            case (2, 3): EnterBlock2Stage3(); break;
            case (3, 3): EnterBlock3Stage3(); break;
            case (4, 3): EnterBlock4Stage3(); break;
            case (5, 2): EnterBlock5Stage2(); break;
            case (5, 3): EnterBlock5Stage3(); break;
            case (6, 2): EnterBlock6Stage2(); break;
            case (7, 2): EnterBlock7Stage2(); break;
            case (7, 3): EnterBlock7Stage3(); break;
            case (8, 3): EnterBlock8Stage3(); break;
            case (9, 3): EnterBlock9Stage3(); break;
            case (10, 2): EnterBlock10Stage2(); break;
            case (10, 3): EnterBlock10Stage3(); break;
            case (11, 1): EnterBlock11Stage1(); break;
            case (11, 2): EnterBlock11Stage2(); break;
            case (11, 3): EnterBlock11Stage3(); break;
        }
    }

    private void EnterBlock2Stage2() => TutorialRevealArch();

    private void EnterBlock2Stage3()
    {
        TutorialStartRoll(3, 6, 2, false, true);
        DelayCallback(1, _ => InsertTutorialDelay(1000), null, TutorialScript, true);
        DelayCallback(1, _ => TutorialFinishRoll(), null, TutorialScript, true);
        DelayCallback(1, _ => InsertTutorialDelay(1000), null, TutorialScript, true);
    }

    private void EnterBlock3Stage3()
    {
        TutorialStartRoll(5, 5, 1, false, true);
        DelayCallback(1, _ => InsertTutorialDelay(1000), null, TutorialScript, true);
        DelayCallback(1, _ => TutorialFinishRoll(), null, TutorialScript, true);
        DelayCallback(1, _ => InsertTutorialDelay(1000), null, TutorialScript, true);
    }

    private void EnterBlock4Stage3()
    {
        TutorialStartRoll(1, 4, 5, false, true);
        DelayCallback(1, _ => InsertTutorialDelay(1000), null, TutorialScript, true);
        DelayCallback(1, _ => TutorialFinishRoll(), null, TutorialScript, true);
        DelayCallback(1, _ => InsertTutorialDelay(1000), null, TutorialScript, true);
        DelayCallback(1, _ => TutorialCollapseArch(), null, TutorialScript, true);
    }

    private void EnterBlock5Stage2()
    {
        KeystonePlayer? human = GetHumanPlayer();
        if (human is not null)
        {
            human.Chips = 100;
        }

        ChipStackMulti? stack = Board?.GetPlayerStack(0);
        stack?.SetAlpha(0);

        TutorialRevealArch();

        if (stack is not null)
        {
            CallbackManager.AddCallbackObject(
                SetChipStackMultiValue(100, 100, 1, 0, null, null, stack, TutorialAnim));

            Board?.FadeInPlayerStack(0, 500, TutorialAnim);
        }

        Board?.FadeInChipTotal(0, 500, TutorialAnim, true);
        Board?.ShowTutorialHilight(true, false, TutorialAnim, false);

        Current = KeystoneSpace.Out8;
        Board?.ShowFocus(Current, true, true, TutorialAnim, false);
    }

    private void EnterBlock5Stage3()
    {
        Board?.ShowTutorialHilight(false, false, TutorialAnim, false);
        Board?.ShowFocus(Current, false, true, TutorialAnim, false);
    }

    private void EnterBlock6Stage2()
    {
        if (OverlayManager is IPubTutorialOverlays overlays)
        {
            overlays.AllowQuickTips = true;
        }
    }

    private void EnterBlock7Stage2()
    {
        Board?.ShowTutorialHilight(true, true, TutorialAnim, false);

        if (OverlayManager is IPubTutorialOverlays overlays)
        {
            overlays.AllowQuickTips = false;
        }

        PlayDelayedSound(0, "K_startInsideBetting", TutorialAnim);
        Current = KeystoneSpace.In8;
        Board?.ShowFocus(Current, true, true, TutorialAnim, false);
        UpdateBetStatsRecount(false);
    }

    private void EnterBlock7Stage3()
    {
        Board?.ShowTutorialHilight(false, true, TutorialAnim, false);
        Board?.ShowFocus(Current, false, true, TutorialAnim, false);
    }

    private void EnterBlock8Stage3()
    {
        TutorialStartRoll(2, 1, 5, true, false);
        AddTutorialAnimWait();
        DelayCallback(1, _ => TutorialRemoveBets(), null, TutorialScript, true);
        AddTutorialAnimWait();
        DelayCallback(1, _ => TutorialShowPayouts(), null, TutorialScript, true);
        AddTutorialAnimWait();
        DelayCallback(1, _ => UpdateBetStats(), null, TutorialScript, false);
    }

    private void EnterBlock9Stage3()
    {
        MoveWinsToPlayers(TutorialAnim);
        AddTutorialAnimWait();
        DelayCallback(1, _ => TutorialFinishRoll(), null, TutorialScript, true);
        DelayCallback(
            1, _ => TutorialShowFocus(KeystoneSpace.Keystone), null, TutorialScript, true);
        AddTutorialAnimWait();
    }

    private void EnterBlock10Stage2()
    {
        ResetArchStones();
        Board?.SetStoneOnArch(KeystoneSpace.Out8);

        KeystonePlayer? human = GetHumanPlayer();
        if (human is null)
        {
            return;
        }

        int[] spaces =
        {
            KeystoneSpace.Out4, KeystoneSpace.Out5, KeystoneSpace.Out8, KeystoneSpace.Out9,
            KeystoneSpace.Out10, KeystoneSpace.Out13, KeystoneSpace.Out18, KeystoneSpace.In8,
            KeystoneSpace.Black, KeystoneSpace.In18, KeystoneSpace.Trips, KeystoneSpace.Pair,
        };

        int chips = 100;
        foreach (int space in spaces)
        {
            chips -= GetPayoutBet(space);
        }

        human.Chips = chips;

        ChipStackMulti? stack = Board?.GetPlayerStack(0);
        if (stack is not null)
        {
            stack.Value = human.Chips;
        }

        foreach (int space in spaces)
        {
            ChipStack? chipStack = Board?.GetChipStack(0, space);
            if (chipStack is not null)
            {
                chipStack.Value = GetPayoutBet(space);
            }

            human.Bets[space] = GetPayoutBet(space);
        }

        ArchWinsValue = 0;
        InsideWinsValue = 0;
        UpdateBetStatsRecount(true);
        UpdateBetStatsRecount(false);
        Board?.ShowFocus(KeystoneSpace.Keystone, false, true, TutorialAnim, false);
    }

    private void EnterBlock10Stage3()
    {
        AllowJackpot = true;
        TutorialStartRoll(6, 6, 6, true, true);
        AddTutorialAnimWait();
        DelayCallback(1, _ => TutorialRemoveBets(), null, TutorialScript, true);
        AddTutorialAnimWait();
        DelayCallback(1, _ => TutorialShowPayouts(), null, TutorialScript, true);
        AddTutorialAnimWait();
        DelayCallback(1, _ => UpdateBetStats(), null, TutorialScript, false);
    }

    private void EnterBlock11Stage1()
    {
        MoveWinsToPlayers(TutorialAnim);
        AddTutorialAnimWait();
        DelayCallback(1, _ => TutorialFinishRoll(), null, TutorialScript, true);
        DelayCallback(1, _ => InsertTutorialDelay(1000), null, TutorialScript, true);
        DelayCallback(1, _ => TutorialCollapseArch(), null, TutorialScript, true);
        AddTutorialAnimWait();
        DelayCallback(1, _ => TutorialRevealArch(), null, TutorialScript, true);
    }

    private void EnterBlock11Stage2()
    {
        Bloodstone = true;
        Board?.ClearAllHilights();
        Board?.InitBetStats();

        MaxInsideBet = 10;
        MinInsideBet = 10;
        MinBet = 10;
        MaxBet = 10;
        InitBetLimits();

        _payoutRate[KeystoneSpace.In10] = 80;
        _payoutRate[KeystoneSpace.Pair] = 13;

        int[] spaces =
        {
            KeystoneSpace.Out7, KeystoneSpace.Out8, KeystoneSpace.Out9, KeystoneSpace.Out14,
            KeystoneSpace.In3, KeystoneSpace.In5, KeystoneSpace.In6, KeystoneSpace.In8,
            KeystoneSpace.In10, KeystoneSpace.In12, KeystoneSpace.In14, KeystoneSpace.In17,
            KeystoneSpace.In18, KeystoneSpace.Red, KeystoneSpace.Pair, KeystoneSpace.Straight,
        };

        foreach (int space in spaces)
        {
            Board?.SwitchToLammer(Board.GetChipStack(0, space));
        }

        KeystonePlayer? human = GetHumanPlayer();
        if (human is null)
        {
            return;
        }

        foreach (int space in spaces)
        {
            ChipStack? chipStack = Board?.GetChipStack(0, space);
            if (chipStack is not null)
            {
                chipStack.Value = GetPayoutBet(space);
            }

            human.Bets[space] = GetPayoutBet(space);
        }

        ArchBetsValue = BloodstoneExposure(human, false);
        ArchWinsValue = 0;
        InsideBetsValue = 0;
        InsideWinsValue = 0;
        UpdateBetStatsRecount(true);
    }

    private void EnterBlock11Stage3()
    {
        AllowJackpot = true;
        TutorialStartRoll(4, 4, 2, true, false);
        AddTutorialAnimWait();
        DelayCallback(1, _ => TutorialShowPayouts(), null, TutorialScript, true);
        AddTutorialAnimWait();
        DelayCallback(1, _ => UpdateBetStats(), null, TutorialScript, false);
    }
}
