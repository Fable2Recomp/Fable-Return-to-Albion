using System;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.Keystone;

public sealed partial class Keystone
{
    public const int TutorialAnim = -10;

    public const int TutorialScript = -11;

    public const double TutorialFastForward = 3;

    public int TutorialBlock { get; private set; }

    public int TutorialStage { get; private set; }

    public bool AllowTutorialInputFlag { get; private set; }

    public readonly record struct TutorialCard(
        string? Title, string? Body,
        string? Button1, string? Label1,
        string? Button2, string? Label2)
    {
        public bool Any => Body is not null || Title is not null;
    }

    public static readonly TutorialCard[] TutorialCards =
    {
        new("txtTutorialH1", "txtTutorialA1", "a", "txtTutorialContinue", null, null),
        default,
        new(null, "txtTutorialC1", "a", "txtTutorialContinue", null, null),

        new("txtTutorialH2", "txtTutorialA2", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialB2", "x", "txtTutorialRollDice", null, null),
        new(null, "txtTutorialC2", "a", "txtTutorialContinue", null, null),

        new("txtTutorialH3", "txtTutorialA3", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialB3", "x", "txtTutorialRollDice", null, null),
        new(null, "txtTutorialC3", "a", "txtTutorialContinue", null, null),

        new("txtTutorialH4", "txtTutorialA4", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialB4", "x", "txtTutorialRollDice", null, null),
        new(null, "txtTutorialC4", "a", "txtTutorialContinue", null, null),

        new("txtTutorialH5", "txtTutorialA5", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialB5", "ls", "txtTutorialMoveCursor", "a", "txtTutorialPlaceBets"),
        new(null, "txtTutorialC5", "a", "txtTutorialContinue", null, null),

        new("txtTutorialH6", "txtTutorialA6", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialB6", "lt", "txtTutorialOddsOverlay", null, null),
        new(null, "txtTutorialC6", "lt", "txtTutorialOddsOverlay", "a", "txtTutorialContinue"),

        new("txtTutorialH7", "txtTutorialA7", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialB7", "ls", "txtTutorialMoveCursor", "a", "txtTutorialPlaceBets"),
        new(null, "txtTutorialC7", "a", "txtTutorialContinue", null, null),

        new("txtTutorialH8", "txtTutorialA8", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialB8", "x", "txtTutorialRollDice", null, null),
        new(null, "txtTutorialC8", "a", "txtTutorialContinue", null, null),

        new("txtTutorialH9", "txtTutorialA9", "a", "txtTutorialContinue", null, null),
        default,
        new(null, "txtTutorialC9", "a", "txtTutorialContinue", null, null),

        new("txtTutorialH10", "txtTutorialA10", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialB10", "x", "txtTutorialRollDice", null, null),
        new(null, "txtTutorialC10", "a", "txtTutorialContinue", null, null),

        new("txtTutorialH11", "txtTutorialA11", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialB11", "x", "txtTutorialRollDice", null, null),
        new(null, "txtTutorialC11", "a", "txtTutorialContinue", null, null),

        new("txtTutorialH12", "txtTutorialA12", "b", "txtTutorialEndTutorial", null, null),
        default,
        default,
    };

    private static int CardIndex(int block, int stage) =>
        ((block - FirstTutorialBlock) * (LastTutorialStage - FirstTutorialStage + 1)) +
        (stage - FirstTutorialStage);

    public void TutorialPreFadeIn()
    {
        TutorialBlock = FirstTutorialBlock;
        TutorialStage = FirstTutorialStage;
        BlockTutorialInput();

        if (OverlayManager is IPubTutorialOverlays overlays)
        {
            overlays.AllowQuickTips = false;
        }

        Board?.SetDiceValues(4, 3, 6);

        ChipStackMulti? stack = Board?.GetPlayerStack(0);
        if (stack is not null)
        {
            stack.Value = 0;
        }

        KeystonePlayer? human = GetHumanPlayer();
        if (human is not null)
        {
            human.Chips = 0;
        }

        SetLastChips(0, 0);
        Board?.HidePlayerName(0);
        Board?.SetBetStatsAlpha(0);
        Board?.SetChipTotalAlpha(0, 0);
    }

    public void EnterTutorialStage()
    {
        if (TutorialBlock < FirstTutorialBlock || TutorialBlock > LastTutorialBlock)
        {
            DelayCallback(1000, _ => EndGame(), null, TutorialScript, true);
            return;
        }

        int block = TutorialBlock;
        int stage = TutorialStage;

        DelayCallback(1, _ => EnterBlock(block, stage), null, TutorialScript, true);

        bool fade = !(block == 6 && stage == 3);
        DelayCallback(1, _ => ShowTutorialCard(fade), null, TutorialScript, true);
    }

    public void ShowTutorialCard(bool fade)
    {
        AddTutorialAnimWait();

        if (TutorialBlock >= FirstTutorialBlock && TutorialBlock <= LastTutorialBlock &&
            TutorialStage >= FirstTutorialStage && TutorialStage <= LastTutorialStage &&
            OverlayManager is IPubTutorialOverlays overlays)
        {
            TutorialCard card = TutorialCards[CardIndex(TutorialBlock, TutorialStage)];
            overlays.ShowTutorial(
                card.Title, card.Body, card.Button1, card.Label1,
                card.Button2, card.Label2, fade, TutorialScript);
        }

        DelayCallback(1, _ => AllowTutorialInput(), null, TutorialScript, false);
    }

    public void TutorialContinue()
    {
        BlockTutorialInput();

        bool fade = !(TutorialBlock == 6 && TutorialStage == 2);
        if (OverlayManager is IPubTutorialOverlays overlays)
        {
            overlays.HideTutorial(fade, TutorialScript);
        }

        TutorialStage++;

        if (TutorialStage > LastTutorialStage)
        {
            TutorialBlock++;
            TutorialStage = FirstTutorialStage;
        }
        else if (TutorialStage == 2)
        {
            if (TutorialBlock == LastTutorialBlock)
            {
                TutorialBlock = 0;
                TutorialStage = 0;
            }
            else if (TutorialBlock is 1 or 9)
            {
                TutorialStage++;
            }
        }

        EnterTutorialStage();
    }

    public void AllowTutorialInput()
    {
        CallbackManager.Speed = 1;
        AllowTutorialInputFlag = true;
    }

    public void BlockTutorialInput() => AllowTutorialInputFlag = false;

    public void HandleTutorialInput(GamePad? pad, double ticks)
    {
        if (pad is null)
        {
            return;
        }

        if (!AllowTutorialInputFlag)
        {
            if (!BlockGameInput && pad.BtnAPressed)
            {
                CallbackManager.Speed = TutorialFastForward;
            }

            return;
        }

        KeystonePlayer? human = GetHumanPlayer();
        if (human is null)
        {
            return;
        }

        if (!BlockGameInput && TutorialBlock == 5 && TutorialStage == 2)
        {
            PlaceTutorialBets(
                pad, ticks, human, true,
                KeystoneSpace.Out8, KeystoneSpace.Out9, KeystoneSpace.Out10);
            return;
        }

        if (!BlockGameInput && TutorialBlock == 7 && TutorialStage == 2)
        {
            PlaceTutorialBets(
                pad, ticks, human, false,
                KeystoneSpace.In8, KeystoneSpace.Black, KeystoneSpace.Pair);
            return;
        }

        if (TutorialBlock == 6 && TutorialStage == 2)
        {
            if (OverlayManager is IPubTutorialOverlays overlays &&
                overlays.QuickTipsShowing)
            {
                TutorialContinue();
            }

            return;
        }

        bool rollStage = TutorialStage == 2 &&
            TutorialBlock is 2 or 3 or 4 or 8 or 10 or 11;

        if (rollStage)
        {
            if (!BlockGameInput && pad.BtnXPressed)
            {
                TutorialContinue();
            }

            return;
        }

        if (TutorialBlock == 12)
        {
            if (!BlockGameInput && pad.BtnBPressed)
            {
                TutorialContinue();
            }

            return;
        }

        if (!BlockGameInput && pad.BtnAPressed)
        {
            TutorialContinue();
        }
    }

    private void PlaceTutorialBets(
        GamePad pad, double ticks, KeystonePlayer human, bool arch, int a, int b, int c)
    {
        Navigate(pad, ticks, null);

        if (!pad.BtnAPressed)
        {
            return;
        }

        int[] bets = human.Bets;
        bool wanted = Current == a || Current == b || Current == c;

        if (!wanted || bets[Current] > 0)
        {
            PlayDelayedSound(0, "G_limitReached", TutorialAnim);
            return;
        }

        ChangeBet(GetPayoutBet(Current), human, Current);
        UpdateBetStatsRecount(arch);

        if (bets[a] > 0 && bets[b] > 0 && bets[c] > 0)
        {
            TutorialContinue();
        }
    }

    public void AddTutorialAnimWait()
    {
        var wait = new Callback
        {
            CallbackMethod = (_, _, _) => WaitForTutorialAnimationsToEnd(),
            Blocking = true,
            Group = TutorialScript,
        };
        CallbackManager.AddCallbackObject(wait);
    }

    private bool WaitForTutorialAnimationsToEnd() =>
        !CallbackManager.IsGroupInUse(TutorialAnim);

    public void InsertTutorialDelay(double delay) => InsertDelay(delay, TutorialAnim);

    public void InsertDelay(double delay, object? group) =>
        DelayCallback(delay, _ => DoneBlocking(), null, group, true);

    public void DoneBlocking()
    {
    }

    public void TutorialShowFocus(int id) =>
        Board?.ShowFocus(id, true, true, TutorialAnim, false);

    public void TutorialRevealArch()
    {
        ResetArchStones();
        Board?.RevealStones(TutorialAnim);
        InsertTutorialDelay(3000);
    }

    public void TutorialCollapseArch()
    {
        Board?.RemoveDice(400, TutorialAnim, true);
        Board?.CollapseArch(TutorialAnim);
        InsertTutorialDelay(3000);
    }

    public void TutorialRemoveBets() => AnimateLostBetRemoval(false, TutorialAnim);

    public void TutorialShowPayouts() => ShowPayoutStacks(TutorialAnim);

    public void TutorialPresentDice()
    {
        double time = Board?.PresentDice(500, TutorialAnim) ?? 0;
        InsertTutorialDelay(1000 + time);
        HilightWinningSpaces(TutorialAnim);
    }

    public void TutorialRoll()
    {
        Board?.RollDice(Roll1, Roll2, Roll3, false, TutorialAnim);
        InsertTutorialDelay(3500);
        Board?.ShowNumberAbove(
            RollTotal - 3 + KeystoneSpace.Out3, true, TutorialAnim, true, 0);
    }

    public void TutorialStartRoll(
        int roll1, int roll2, int roll3, bool allowInner, bool removeDice)
    {
        SetRoll(roll1, roll2, roll3, false);
        FindWinningSpaces(allowInner);
        GenerateAndResolvePayouts(BoardInfos[0]);
        InsertTutorialDelay(1);

        if (removeDice)
        {
            Board?.RemoveDice(400, TutorialAnim, true);
        }

        AddTutorialAnimWait();
        DelayCallback(1, _ => TutorialRoll(), null, TutorialScript, true);
        AddTutorialAnimWait();
        DelayCallback(1, _ => TutorialPresentDice(), null, TutorialScript, true);
        AddTutorialAnimWait();
    }

    public void TutorialFinishRoll()
    {
        UnHilightWinningSpaces(TutorialAnim);
        Board?.RemoveStone(ArchStoneToRemove, TutorialAnim);
        RemoveArchStone();
        InsertTutorialDelay(1000);
    }

    public static AnimateValue SetChipStackMultiValue(
        double startValue, double endValue, double time, double delay,
        PubEndCallback? endCallback, object? userData, IAnimValue stack, object? group)
    {
        var animation = new AnimateValue(
            startValue, endValue, stack, time, delay, AnimateBase.StyleLinear);
        animation.SetEndCallback(endCallback, userData);
        animation.Group = group;
        return animation;
    }
}
