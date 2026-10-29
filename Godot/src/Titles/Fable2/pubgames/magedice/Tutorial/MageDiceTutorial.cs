using Refii.Titles.Fable2.PubGames.Framework;

using Action = Refii.Titles.Fable2.PubGames.Framework.Action;

namespace Refii.Titles.Fable2.PubGames.MageDice;

public sealed partial class MageDice
{
    private int _tutorialStage;
    private bool _allowTutorialInput;
    private IPubClip? _tutorialClip;

    private void StateTutorialNext(double ticks)
    {
        BlockTutorialInput();
        _activeState = StateTutorial;
        _tutorialStage++;
        EnterTutorialStage();
    }

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

                case 19:
                    break;
            }
        }
        else if (Pad is { BtnBPressed: true } && _tutorialStage == 21)
        {
            advance = true;
        }

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
            BlockTutorialInput();
            HideTutorial();
            return;
        }

        DoIdleTasks(ticks);
    }

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

    private void SwitchVariant(int index)
    {
        _activeVariant = index;
        Reset();
    }

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
        Action.SetGameSpeed(1);
        _allowTutorialInput = true;
    }

    private void BlockTutorialInput() => _allowTutorialInput = false;

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
