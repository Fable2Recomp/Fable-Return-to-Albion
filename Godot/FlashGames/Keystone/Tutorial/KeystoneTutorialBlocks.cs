using Refii.FlashGames.Framework;

namespace Refii.FlashGames.Keystone;

/// <summary>
/// What each tutorial stage does to the board before its card is shown.
/// </summary>
/// <remarks>
/// MOST STAGES DO NOTHING, and that is not an omission. Of the 36 stages only 16 have any setup at
/// all; the rest simply raise a card over whatever the previous stage left on the table. The SWF
/// stores these as methods named <c>EnterBlock{block}Stage{stage}</c> and looks them up by name,
/// falling back to an empty <c>EnterNullBlock</c> — the switch here is that lookup.
///
/// THE NUMBERS ARE THE LESSON. Every roll, every space and every stake in this file is authored to
/// demonstrate something: 6+6+6 is the jackpot, 4+4+2 is a bloodstone pair, and the twelve bets
/// dealt onto the board in block 10 are there so the payout sweep has something to sweep. Nothing
/// here should be tidied into a loop or a formula, because none of it is derived.
/// </remarks>
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

    /// <summary>The player is given their bankroll and asked to bet the arch.</summary>
    /// <remarks>
    /// THE STACK FADES IN FROM NOTHING while its value counts up to 100, which is why the alpha is
    /// forced to zero first: <see cref="TutorialPreFadeIn"/> left the chips hidden rather than
    /// absent, and animating alpha from its current value would start at whatever it happened to be.
    /// </remarks>
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

    /// <summary>The inner board opens; the odds table is closed off again.</summary>
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

    /// <summary>
    /// A full board of bets, dealt rather than played, so the jackpot roll has something to pay.
    /// </summary>
    /// <remarks>
    /// THE STAKE IS SUBTRACTED UP FRONT. The player is set to 100 minus the twelve stakes and the
    /// bets are written straight into their array, because these were not placed through
    /// <c>ChangeBet</c> and nothing else will have taken the money.
    /// </remarks>
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

    /// <summary>
    /// The bloodstone table: the same board, staked in blood rather than chips.
    /// </summary>
    /// <remarks>
    /// EVERY STACK BECOMES A LAMMER, because a bloodstone bet is a marker and not an amount — see
    /// <c>GameBoard.SwitchToLammer</c>. The stakes are flattened to 10 across the board and two
    /// payout rates are overwritten by hand so the numbers on the card match what the roll pays.
    /// The exposure, not the stake, is what the bet-stats line shows here.
    /// </remarks>
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
