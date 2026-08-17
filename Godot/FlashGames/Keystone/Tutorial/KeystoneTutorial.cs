using System;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.Keystone;

/// <summary>
/// The scripted tutorial table.
/// </summary>
/// <remarks>
/// A GUEST-FACING MODE, NOT A DEMO. <c>Tutorial</c> is one of the 28 variations in
/// <c>keystone/settings.xml</c>, so the guest can seat the player at this table and, until this
/// existed, doing so produced an ordinary game that ignored every instruction card.
///
/// TWELVE BLOCKS OF THREE STAGES, and the shape is the whole design. A block is a lesson; its
/// three stages are "here is the idea" / "now you try" / "here is what happened". Each stage may
/// name a card in <see cref="TutorialCards"/> and may name a method
/// <c>EnterBlock{block}Stage{stage}</c> that sets the board up for it; a stage with neither is
/// simply skipped. <see cref="EnterTutorialStage"/> runs the setup, raises the card, and then
/// waits — the player advances with <see cref="TutorialContinue"/> and nothing else moves the
/// script on.
///
/// TWO CALLBACK GROUPS, AND THEY ARE NOT INTERCHANGEABLE. Everything the script queues goes in
/// <see cref="TutorialScript"/>; everything the board animates goes in <see cref="TutorialAnim"/>.
/// The script blocks on the animation group being empty (<see cref="AddTutorialAnimWait"/>), which
/// is what makes a stage wait for its own dice to land rather than for a fixed delay. Putting an
/// animation in the script group deadlocks it against itself.
///
/// Ported from the SWF, which is the specification — read it with
/// <c>tools/as2dis.py &lt;game.swf&gt; --class Keystone --method … --pseudo</c>.
/// </remarks>
public sealed partial class Keystone
{
    /// <summary>The callback group the board's tutorial animations run in. The SWF's -10.</summary>
    public const int TutorialAnim = -10;

    /// <summary>The callback group the tutorial script itself runs in. The SWF's -11.</summary>
    public const int TutorialScript = -11;

    /// <summary>How much the callback clock is sped up while the player holds A.</summary>
    /// <remarks>
    /// A SKIP, NOT A SEEK. Holding A during a stage the player cannot act in multiplies the
    /// callback clock, so the animation still runs and still ends where it would have — it just
    /// gets there sooner. <see cref="AllowTutorialInput"/> puts it back to 1.
    /// </remarks>
    public const double TutorialFastForward = 3;

    /// <summary>The block being taught, 1..12. Zero once the tutorial has run out.</summary>
    public int TutorialBlock { get; private set; }

    /// <summary>The stage within the block, 1..3.</summary>
    public int TutorialStage { get; private set; }

    /// <summary>Whether the player's input reaches the script rather than the fast-forward.</summary>
    public bool AllowTutorialInputFlag { get; private set; }

    /// <summary>
    /// One instruction card: what it says and which button prompts it shows.
    /// </summary>
    /// <remarks>
    /// Every field is a LOCALISATION KEY, not text — the card is filled through
    /// <see cref="Localizer"/> like the rest of the board. A null <see cref="Body"/> means the
    /// stage shows no card at all, which is how block 1 stage 2 and the last two stages of block 12
    /// are marked: they are pure script with nothing to read.
    /// </remarks>
    public readonly record struct TutorialCard(
        string? Title, string? Body,
        string? Button1, string? Label1,
        string? Button2, string? Label2)
    {
        public bool Any => Body is not null || Title is not null;
    }

    /// <summary>
    /// The tutorial script: 12 blocks of 3 stages, in order.
    /// </summary>
    /// <remarks>
    /// FLAT AND POSITIONAL, exactly as the SWF stores it —
    /// <c>[(block - 1) * 3 + (stage - 1)]</c>. The gaps are meaningful and load-bearing:
    /// <see cref="TutorialContinue"/> knows that blocks 1 and 9 have no stage 2 and steps over it,
    /// so filling those in with something plausible would change the script's shape.
    /// </remarks>
    public static readonly TutorialCard[] TutorialCards =
    {
        // Block 1 — what the game is.
        new("txtTutorialH1", "txtTutorialA1", "a", "txtTutorialContinue", null, null),
        default,
        new(null, "txtTutorialC1", "a", "txtTutorialContinue", null, null),

        // Block 2 — the arch and the roll.
        new("txtTutorialH2", "txtTutorialA2", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialB2", "x", "txtTutorialRollDice", null, null),
        new(null, "txtTutorialC2", "a", "txtTutorialContinue", null, null),

        // Block 3.
        new("txtTutorialH3", "txtTutorialA3", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialB3", "x", "txtTutorialRollDice", null, null),
        new(null, "txtTutorialC3", "a", "txtTutorialContinue", null, null),

        // Block 4 — the arch collapses.
        new("txtTutorialH4", "txtTutorialA4", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialB4", "x", "txtTutorialRollDice", null, null),
        new(null, "txtTutorialC4", "a", "txtTutorialContinue", null, null),

        // Block 5 — the player places their first arch bets.
        new("txtTutorialH5", "txtTutorialA5", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialB5", "ls", "txtTutorialMoveCursor", "a", "txtTutorialPlaceBets"),
        new(null, "txtTutorialC5", "a", "txtTutorialContinue", null, null),

        // Block 6 — the odds table.
        new("txtTutorialH6", "txtTutorialA6", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialB6", "lt", "txtTutorialOddsOverlay", null, null),
        new(null, "txtTutorialC6", "lt", "txtTutorialOddsOverlay", "a", "txtTutorialContinue"),

        // Block 7 — the inner board.
        new("txtTutorialH7", "txtTutorialA7", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialB7", "ls", "txtTutorialMoveCursor", "a", "txtTutorialPlaceBets"),
        new(null, "txtTutorialC7", "a", "txtTutorialContinue", null, null),

        // Block 8 — payouts.
        new("txtTutorialH8", "txtTutorialA8", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialB8", "x", "txtTutorialRollDice", null, null),
        new(null, "txtTutorialC8", "a", "txtTutorialContinue", null, null),

        // Block 9 — collecting.
        new("txtTutorialH9", "txtTutorialA9", "a", "txtTutorialContinue", null, null),
        default,
        new(null, "txtTutorialC9", "a", "txtTutorialContinue", null, null),

        // Block 10 — the jackpot.
        new("txtTutorialH10", "txtTutorialA10", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialB10", "x", "txtTutorialRollDice", null, null),
        new(null, "txtTutorialC10", "a", "txtTutorialContinue", null, null),

        // Block 11 — bloodstone.
        new("txtTutorialH11", "txtTutorialA11", "a", "txtTutorialContinue", null, null),
        new(null, "txtTutorialB11", "x", "txtTutorialRollDice", null, null),
        new(null, "txtTutorialC11", "a", "txtTutorialContinue", null, null),

        // Block 12 — done.
        new("txtTutorialH12", "txtTutorialA12", "b", "txtTutorialEndTutorial", null, null),
        default,
        default,
    };

    private static int CardIndex(int block, int stage) =>
        ((block - FirstTutorialBlock) * (LastTutorialStage - FirstTutorialStage + 1)) +
        (stage - FirstTutorialStage);

    /// <summary>
    /// Sets the table up for the tutorial and starts it at block 1.
    /// </summary>
    /// <remarks>
    /// THE PLAYER STARTS WITH NOTHING, deliberately. Chips, the stack, the running total and the
    /// name plate are all zeroed and hidden so the first blocks can talk about the board without a
    /// bankroll on screen; block 5 fades them in as it hands the player their first 100.
    /// </remarks>
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

    /// <summary>Runs the current stage: its setup, then its card.</summary>
    /// <remarks>
    /// THE CARD IS QUEUED, NOT SHOWN. Both halves go through <see cref="DelayCallback"/> into the
    /// script group so the setup runs to completion before the card appears — a stage that deals
    /// chips onto the board must not be described until the chips are there.
    ///
    /// A BLOCK OUT OF RANGE ENDS THE GAME. That is how the tutorial finishes: block 12 stage 2
    /// zeroes the block, the next entry finds it below <see cref="FirstTutorialBlock"/>, and a
    /// second later the game ends and settles like any other.
    /// </remarks>
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

        // Block 6 stage 3 is the one stage that must NOT fade its card in: the player is holding
        // the odds table open over it and a fade would flash behind the overlay.
        bool fade = !(block == 6 && stage == 3);
        DelayCallback(1, _ => ShowTutorialCard(fade), null, TutorialScript, true);
    }

    /// <summary>Raises the card for the current stage, then hands input back.</summary>
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

    /// <summary>
    /// Advances the script one stage.
    /// </summary>
    /// <remarks>
    /// THE SKIPS ARE THE SCRIPT. Blocks 1 and 9 have no stage 2, so reaching stage 2 in either
    /// steps straight past it; and reaching stage 2 of the LAST block is the end — the block and
    /// stage go to zero, which <see cref="EnterTutorialStage"/> reads as "out of range" and ends
    /// the game. These are not guards against bad state; they are how the script is shaped.
    /// </remarks>
    public void TutorialContinue()
    {
        BlockTutorialInput();

        // Block 6 stage 2 leaves its card up: the player opens the odds table over it and stage 3
        // talks about what they are looking at.
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

    /// <summary>
    /// The tutorial's whole input model.
    /// </summary>
    /// <remarks>
    /// MOST STAGES WANT ONE BUTTON, and the exceptions are the stages that teach something. Blocks
    /// 5 and 7 want the player to actually place bets on three named spaces and only advance once
    /// all three are down; block 6 advances when the odds table is open; the roll blocks want X;
    /// the last block wants B. Everything else is A.
    ///
    /// WHEN INPUT IS NOT ALLOWED, A FAST-FORWARDS. That is the only thing the button does during an
    /// animation, and it is why this runs even when <see cref="AllowTutorialInputFlag"/> is false.
    /// </remarks>
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

    /// <summary>
    /// The two stages where the player bets for themselves.
    /// </summary>
    /// <remarks>
    /// ONLY THE THREE NAMED SPACES TAKE A BET, and a bet anywhere else is refused with the
    /// limit-reached sound rather than silently ignored — the player has to learn that the cursor
    /// moves freely but the lesson does not. A space that already has a bet on it is refused the
    /// same way, so the three cannot be satisfied by stacking one of them.
    /// </remarks>
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

    /// <summary>Blocks the script until every board animation has finished.</summary>
    /// <remarks>
    /// A BLOCKING CALLBACK THAT NEVER COMPLETES UNTIL THE OTHER GROUP DRAINS. This is the join
    /// between the two groups and the reason the script does not need to know how long anything
    /// takes.
    /// </remarks>
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

    /// <summary>A pause in the animation group, so the script's next wait picks it up.</summary>
    public void InsertTutorialDelay(double delay) => InsertDelay(delay, TutorialAnim);

    public void InsertDelay(double delay, object? group) =>
        DelayCallback(delay, _ => DoneBlocking(), null, group, true);

    /// <summary>Deliberately empty — the delay itself was the point.</summary>
    public void DoneBlocking()
    {
    }

    // -- the scripted board steps -------------------------------------------

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

    /// <summary>
    /// Plays a fixed roll and resolves it, without touching the RNG.
    /// </summary>
    /// <remarks>
    /// THE DICE ARE AUTHORED. Every roll in the tutorial is stated by the block that wants it —
    /// 3+6+2, 5+5+1, 6+6+6 for the jackpot — because each one is chosen to demonstrate the thing
    /// the block is about. This is the tutorial's substitute for <c>GenerateRoll</c> and it sets
    /// <c>Cheated</c> false, because a stated roll is not a cheated one.
    /// </remarks>
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

    /// <summary>Builds the animation that counts a multi-stack from one value to another.</summary>
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
