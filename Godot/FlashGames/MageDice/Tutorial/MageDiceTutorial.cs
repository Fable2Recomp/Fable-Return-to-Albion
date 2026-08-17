using Refii.FlashGames.Framework;

using Action = Refii.FlashGames.Framework.Action;

namespace Refii.FlashGames.MageDice;

/// <summary>
/// The scripted tutorial table.
/// </summary>
/// <remarks>
/// A GUEST-FACING MODE, NOT A DEMO. <c>Tutorial</c> is one of the 41 variations in
/// <c>magedice/settings.xml</c>, so the guest can seat the player at this table and expects a
/// working lesson rather than an ordinary machine.
///
/// IT REPLACES THE STATE MACHINE, IT DOES NOT SIT BESIDE IT. <see cref="StateTutorial"/> becomes
/// the default state, and it drives everything: it takes the player's input, decides whether they
/// did what the card asked, pins the reels to a scripted result, charges them and starts the spin
/// itself. <see cref="StateWaitForPlayer"/> never runs on a tutorial table.
///
/// EVERY OUTCOME IS FIXED. Steps that spin write <c>debugOverrides</c> first, so the lesson about
/// three-in-a-row always lands three in a row. That is the same override the developer menu uses.
///
/// AND IT WALKS THREE MACHINES. The tutorial pushes three variants in
/// <see cref="PostInitialize"/> — the plain table, then C, then B — and steps 6, 9, 12, 15 and 18
/// switch <see cref="_activeVariant"/> and rebuild the board so the player is shown a three-reel,
/// a six-reel and a four-reel machine in turn.
///
/// A 1:1 port of the tutorial half of <c>MageDice.as</c>.
/// </remarks>
public sealed partial class MageDice
{
    private int _tutorialStage;
    private bool _allowTutorialInput;
    private IPubClip? _tutorialClip;

    /// <summary>
    /// Advances to the next card without the player having answered one.
    /// </summary>
    /// <remarks>
    /// THE ENTRY POINT, and it is the DEFAULT STATE rather than something called once: a tutorial
    /// session opens on it with <see cref="_tutorialStage"/> at -1, so the first thing that happens
    /// is stage 0 being entered.
    /// </remarks>
    private void StateTutorialNext(double ticks)
    {
        BlockTutorialInput();
        _activeState = StateTutorial;
        _tutorialStage++;
        EnterTutorialStage();
    }

    /// <summary>
    /// One tick of the lesson: read the card, read the player, decide whether to move on.
    /// </summary>
    /// <remarks>
    /// A STEP THAT SPINS DOES NOT ADVANCE ITSELF. Steps 1, 4, 7, 10, 13 and 16 start a spin and
    /// leave <c>advance</c> false; what moves the tutorial on is the spin finishing and the NEXT
    /// press of A on the card that follows. That is why the reel-pinning steps and the
    /// "press A to continue" steps alternate.
    ///
    /// THE BET IS A GATE, NOT A SUGGESTION. Steps 1, 4 and 7 do nothing at all until the player has
    /// actually dialled the stake the card asked for — 3, then 5, then 5 — which is what makes the
    /// lesson about betting a lesson rather than a caption.
    /// </remarks>
    private void StateTutorial(double ticks)
    {
        _allowWagerAdjustment = _tutorialStage is 1 or 4 or 7 or 19 or 20;

        if (!_allowTutorialInput)
        {
            return;
        }

        var advance = false;

        if (Pad is { BtnAPressed: true })
        {
            switch (_tutorialStage)
            {
                case 0:
                case 2:
                case 3:
                    advance = true;
                    break;

                case 1:
                    advance = ScriptedSpin(3, 3, 4, 1, 0, 0, 0);
                    break;

                case 4:
                    advance = ScriptedSpin(5, 6, 6, 7, 0, 0, 0);
                    break;

                case 5:
                    KillWinAnims = true;
                    advance = true;
                    break;

                case 6:
                    SwitchVariant(1);
                    advance = true;
                    break;

                case 7:
                    advance = ScriptedSpin(5, 4, 4, 4, 6, 4, 4);
                    break;

                case 8:
                    KillWinAnims = true;
                    advance = true;
                    break;

                case 9:
                    SwitchVariant(0);
                    advance = true;
                    break;

                case 10:
                    advance = ScriptedSpin(1, 8, 8, 8, 7, 0, 0);
                    break;

                case 11:
                    KillWinAnims = true;
                    advance = true;
                    break;

                case 12:
                    SwitchVariant(2);
                    advance = true;
                    break;

                case 13:
                    advance = ScriptedSpin(1, 3, 4, 8, 8, 0, 0);
                    break;

                case 14:
                    KillWinAnims = true;
                    advance = true;
                    break;

                case 15:
                    SwitchVariant(1);
                    advance = true;
                    break;

                case 16:
                    advance = ScriptedSpin(1, 8, 1, 1, 8, 1, 4);
                    break;

                case 17:
                    KillWinAnims = true;
                    advance = true;

                    // THE ODDS CARD IS UNLOCKED HERE and locked again at 20, because step 19 is the
                    // lesson that teaches it. Outside those two steps the player must not be able
                    // to open an overlay the script is not expecting.
                    if (OverlayManager is OverlayManager unlock)
                    {
                        unlock.AllowQuickTips = true;
                    }

                    break;

                case 18:
                    SwitchVariant(2);
                    advance = true;
                    break;

                case 20:
                    KillWinAnims = true;
                    if (OverlayManager is OverlayManager relock)
                    {
                        relock.AllowQuickTips = false;
                    }

                    advance = true;
                    break;

                // 19 IS DELIBERATELY EMPTY. A does not answer that card; the left trigger does,
                // below — it is the step that teaches the paytable.
                case 19:
                    break;
            }
        }
        else if (Pad is { BtnBPressed: true } && _tutorialStage == 21)
        {
            // THE LAST CARD ENDS ON B, not A, because it is the "leave the tutorial" prompt and
            // every other card has trained A as "yes, go on".
            advance = true;
        }

        // THE SPIN PROMPT APPEARS ONLY ONCE THE BET IS RIGHT, so the card shows the player they
        // have met the condition before it invites them to act on it.
        if (_tutorialStage is 1 or 4 or 7 && _tutorialClip?.GetChild("button2") is { } button2)
        {
            button2.Visible = _tutorialStage == 1
                ? _board.BetMultiple == 3
                : _board.BetMultiple == 5;
        }

        if (_tutorialStage == 19 && Pad is { LeftTriggerPressed: true })
        {
            advance = true;
        }

        if (advance)
        {
            BlockTutorialInput();
            HideTutorial();
            _tutorialStage++;
            EnterTutorialStage();
            return;
        }

        if (_activeState == StateSpinSwitches)
        {
            // THE CARD GETS OUT OF THE WAY while the reels turn — the player is meant to watch the
            // result the lesson just set up, not read over it.
            BlockTutorialInput();
            HideTutorial();
            return;
        }

        DoIdleTasks(ticks);
    }

    /// <summary>
    /// Pins the reels and starts a spin, but only once the stake matches.
    /// </summary>
    /// <returns>Always false: a scripted spin never advances the card that started it.</returns>
    private bool ScriptedSpin(int requiredBet, params int[] reels)
    {
        if (_board.BetMultiple != requiredBet)
        {
            return false;
        }

        for (var i = 0; i < _debugOverrides.Length && i < reels.Length; i++)
        {
            _debugOverrides[i] = reels[i];
        }

        ChargePlayerForSpin();
        StartNewSpin();
        return false;
    }

    /// <summary>Moves the lesson to another machine and rebuilds the board for it.</summary>
    private void SwitchVariant(int index)
    {
        _activeVariant = index;
        Reset();
    }

    /// <summary>
    /// Raises the current card.
    /// </summary>
    /// <remarks>
    /// TWICE-WAITED ON PURPOSE. The first wait lets whatever the last step started finish, the card
    /// then fades in, and the second wait lets THAT finish before input is accepted a millisecond
    /// later — so a held button cannot answer a card the player has not seen yet.
    /// </remarks>
    private void ShowTutorial()
    {
        Wait.UntilFinished(TutorialScript);

        MageDiceTutorialStep step = TutorialLabels[_tutorialStage];
        if (OverlayManager is OverlayManager overlays)
        {
            overlays.ShowTutorial(
                step.Title, step.Body, step.Button1, step.Label1, step.Button2, step.Label2,
                true, TutorialScript);
            _tutorialClip = overlays.TutorialClip;
        }

        Wait.UntilFinished(TutorialScript);
        Action.RunDelayed(this, AllowTutorialInput, 0.001, TutorialScript);

        if (_tutorialStage is 1 or 4 or 7 && _tutorialClip?.GetChild("button2") is { } button2)
        {
            button2.Visible = false;
        }
    }

    private void AllowTutorialInput()
    {
        // BACK TO NORMAL SPEED. A step may have been fast-forwarded; the next card starts clean.
        Action.SetGameSpeed(1);
        _allowTutorialInput = true;
    }

    private void BlockTutorialInput() => _allowTutorialInput = false;

    /// <summary>
    /// Shows the next card, or ends the session when the script runs out.
    /// </summary>
    /// <remarks>
    /// ENDS THROUGH <see cref="PubGame.EndGame"/>, which is the ordinary way out — it settles the
    /// chips and issues the command the host is waiting on. A tutorial that simply stopped would
    /// leave the guest parked at state 2.
    /// </remarks>
    private void EnterTutorialStage()
    {
        if (_tutorialStage < TutorialLabels.Length)
        {
            Action.BlockOn(Action.RunDelayed(this, ShowTutorial, 0.001, TutorialScript));
            return;
        }

        Action.RunDelayed(this, EndGame, 1, TutorialScript);
    }

    private void HideTutorial()
    {
        if (OverlayManager is OverlayManager overlays)
        {
            overlays.HideTutorial(true, TutorialScript);
        }
    }
}
