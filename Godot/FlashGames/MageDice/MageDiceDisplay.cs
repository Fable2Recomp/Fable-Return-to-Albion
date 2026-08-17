using System;
using System.Collections.Generic;

using Refii.FlashGames.Framework;

using Action = Refii.FlashGames.Framework.Action;

namespace Refii.FlashGames.MageDice;

/// <summary>
/// The half of <see cref="MageDice"/> that talks to the stage rather than the rules.
/// </summary>
public sealed partial class MageDice
{
    public const int Legend1 = 1;
    public const int Legend2 = 2;
    public const int Legend3 = 3;
    public const int Legend4 = 4;
    public const int Legend5 = 5;

    public const string AButtonFrame = "a";
    public const string BButtonFrame = "b";
    public const string XButtonFrame = "x";
    public const string YButtonFrame = "y";
    public const string RightStickFrame = "rs";
    public const string LeftStickFrame = "ls";
    public const string RightTriggerFrame = "rt";
    public const string LeftTriggerFrame = "lt";

    public const double LegendFadeTime = 0.25;

    /// <summary>
    /// Builds the player's chip stack out of three columns and their shadows.
    /// </summary>
    /// <remarks>
    /// THE SHADOWS ARE ATTACHED, NOT AUTHORED, and they are placed to match each column exactly.
    /// They are siblings rather than children so that layering can put the whole stack in front of
    /// the board while the shadows stay on the felt.
    /// </remarks>
    private void InitChips()
    {
        Shadow("Stack0A", "Shadow0A");
        Shadow("Stack0B", "Shadow0B");
        Shadow("Stack0C", "Shadow0C");

        _chipStack = new ChipStackMulti(
            Child("Stack0A"), _root.GetChild("Shadow0A"),
            Child("Stack0B"), _root.GetChild("Shadow0B"),
            Child("Stack0C"), _root.GetChild("Shadow0C"), Random);

        _chipStack.SetValue(_player.Chips, null);
        UpdatePlayerChipText(_player.Chips);

        void Shadow(string stackName, string shadowName)
        {
            IPubClip? stack = _root.GetChild(stackName);
            if (stack is null)
            {
                return;
            }

            IPubClip? shadow = _root.AttachMovie(
                "chipStackShadow", shadowName, _root.GetNextHighestDepth());
            if (shadow is null)
            {
                return;
            }

            shadow.X = stack.X;
            shadow.Y = stack.Y;
            shadow.XScale = stack.XScale;
            shadow.YScale = stack.YScale;
        }
    }

    private IPubClip Child(string name) =>
        _root.GetChild(name) ?? _root.CreateEmptyMovieClip(name, _root.GetNextHighestDepth());

    /// <summary>
    /// Puts the stage's clips into the order they draw in.
    /// </summary>
    /// <remarks>
    /// ORDER OF THE CALLS IS THE ORDER OF THE LAYERS, back to front, because each one takes the
    /// next free depth. The board goes furthest back and the overlays in front of everything; the
    /// developer clips are removed outright when the table did not ask for them, so they cannot
    /// intercept anything.
    /// </remarks>
    private void AdjustLayers()
    {
        foreach (string name in new[]
        {
            "board", "button1", "button2", "button3", "button4", "button5",
            "Shadow0A", "Shadow0C", "Shadow0B", "Stack0A", "Stack0C", "Stack0B",
            "playerChips", "player0name", "freeSpins", "bonusMultiplier", "tourneyStats",
            "betStats", "overlays", "debugMenu", "callbackText", "debugText", "safeArea",
        })
        {
            _root.GetChild(name)?.SwapDepths(_root.GetNextHighestDepth());
        }

        if (AllowDebug)
        {
            return;
        }

        foreach (string name in new[] { "debugMenu", "callbackText", "debugText", "safeArea" })
        {
            _root.GetChild(name)?.RemoveMovieClip();
        }
    }

    private void PreFadeIn()
    {
        _spinLegendShowing = false;
        _freeSpinLegendShowing = false;
        _adjustBetLegendShowing = false;
        _buyChipsLegendShowing = false;
        _triggersLegendShowing = false;
        _endTourneyLegendShowing = false;
        _freeSpinsShowing = false;
        _bonusMultiplierShowing = false;

        InitLegend(Legend1);
        InitLegend(Legend2);
        InitLegend(Legend3);
        InitLegend(Legend4);
        InitLegend(Legend5);
        InitFreeSpins();
        InitBonusMultiplier();
        DebugInfo();

        Action.RunOnce(this, GoToDefaultState, "legend");
    }

    private void UpdatePlayerChipText(int value)
    {
        IPubClip? clip = _root.GetChild("playerChips")?.GetChild("value");
        if (clip is not null)
        {
            Localizer.Localize(
                clip, "txtChipTotal",
                new Dictionary<string, string> { ["value"] = value.ToString() });
        }
    }

    /// <summary>
    /// Walks the displayed chip total towards the real one.
    /// </summary>
    /// <remarks>
    /// TWO SPEEDS, AND THE SLOW ONE IS THE POINT. After a win the count is deliberately paced —
    /// five audible dings a second, each worth a multiple of the last bet — so a big payout takes
    /// time to land and sounds like it. Everywhere else it catches up at the square root of the
    /// difference, which is fast for small corrections and still bounded for huge ones.
    /// </remarks>
    private void UpdatePlayerChipDisplay(double ticks)
    {
        int target = _chipStack.Value;
        int delta = target - _lastChips;

        if (delta != 0)
        {
            if (_slowChipCount)
            {
                if (!_slowChipCountSoundOn)
                {
                    _slowChipCountSoundOn = true;
                    PlaySound.Once(Audio, "S_getChipsLoop", null);
                    _chipCountTicks = ticks;
                }

                _chipCountTicks -= ticks;
                if (_chipCountTicks <= 0)
                {
                    _chipCountTicks += 1000 / _chipDingsPerSecond;
                    double magnitude = Math.Abs(delta);
                    double sign = magnitude / delta;
                    double bets = _lastBet != 0 ? magnitude / _lastBet : magnitude;

                    int step = bets switch
                    {
                        > 1000 => 13,
                        > 100 => 6,
                        > 50 => 2,
                        _ => 1,
                    };

                    _lastChips += (int)(sign * step * _lastBet);
                    UpdatePlayerChipText(_lastChips);
                }
            }
            else
            {
                double magnitude = Math.Abs(delta);
                double sign = magnitude / delta;
                var step = (int)Math.Floor(Math.Sqrt(magnitude));
                if (step < 1)
                {
                    step = 1;
                }
                else if (step > 15)
                {
                    step *= 4;
                }

                _lastChips += (int)(sign * step);
                UpdatePlayerChipText(_lastChips);
            }
        }
        else
        {
            _slowChipCount = false;
        }

        if (!_slowChipCount && _slowChipCountSoundOn)
        {
            _slowChipCountSoundOn = false;
            PlaySound.Once(Audio, "S_getChipsLoopEnd", null);
        }
    }

    /// <summary>
    /// Raises and lowers the button prompts as what the player can do changes.
    /// </summary>
    /// <remarks>
    /// EACH PROMPT REMEMBERS WHETHER IT IS UP, because raising it is a fade and re-raising an
    /// already-raised prompt every frame would restart that fade forever.
    /// </remarks>
    private void ShowButtons()
    {
        bool waiting = _activeState == StateWaitForPlayer;
        bool freeSpins = _board.FreeSpins > 0;
        bool tourneyOver = Tournament && _totalSpins >= TournamentRounds;

        Toggle(waiting && !freeSpins && !tourneyOver, ref _spinLegendShowing, ShowSpinText);
        Toggle(
            waiting && freeSpins && !AutoPlay && !_board.AutoPlayFreeSpins() && !tourneyOver,
            ref _freeSpinLegendShowing, ShowFreeSpinText);
        Toggle(waiting && tourneyOver, ref _endTourneyLegendShowing, ShowEndTourneyText);
        Toggle(
            waiting && !Tutorial && !freeSpins && MinBet != MaxBet,
            ref _adjustBetLegendShowing, ShowAdjustBetText);
        Toggle(!Tournament && !Tutorial, ref _buyChipsLegendShowing, ShowBuyChipsText);
        Toggle(!Tutorial, ref _triggersLegendShowing, ShowTriggersText);

        static void Toggle(bool wanted, ref bool showing, System.Action<bool> apply)
        {
            if (wanted != showing)
            {
                apply(wanted);
                showing = wanted;
            }
        }
    }

    private void ResetTickDelayForNewSpin()
    {
        _tickAudioTimer = 0;
        RequestAudioTick = false;
    }

    /// <summary>
    /// The reel ticking, rate limited.
    /// </summary>
    /// <remarks>
    /// SIX REELS ASKING AT ONCE WOULD BE A BUZZ, so the reels raise a flag and this fires at most
    /// one tick every 70 ms, picked from six samples so the sound does not repeat audibly.
    /// </remarks>
    private void PlayTickSound(double ticks)
    {
        _tickAudioTimer -= ticks;
        if (_tickAudioTimer > 0 || !RequestAudioTick)
        {
            return;
        }

        PlaySound.Once(
            Audio, "M_flitSpinImage0" + ((int)Math.Floor(Random.NextDouble() * 6) + 1), null);
        RequestAudioTick = false;
        _tickAudioTimer = 70;
    }

    /// <summary>The chip sound scales with the size of the change, not the total.</summary>
    private void PlayChipStackSound(int delta)
    {
        int size = Math.Abs(delta);
        if (size > 5000)
        {
            Audio.PlaySound("chips04BigWin");
        }
        else if (size > 500)
        {
            Audio.PlaySound("chips03Medium");
        }
        else if (size > 50)
        {
            Audio.PlaySound("chips02Several");
        }
        else if (size != 0)
        {
            Audio.PlaySound("chips01Single");
        }
    }

    /// <summary>
    /// Refreshes the free-spin counter and the multiplier badge.
    /// </summary>
    /// <remarks>
    /// THE COUNTER RUNS BEHIND ON PURPOSE. <c>freeSpinCounter</c> is worked down one ding at a time
    /// by <see cref="DoIdleTasks"/> and subtracted here, so newly won spins are counted onto the
    /// display audibly rather than appearing all at once.
    ///
    /// THE NEXT MULTIPLIER IS HIDDEN WHILE THE REELS TURN — it would spoil the result it is about
    /// to be part of — so it shows a placeholder until the spin settles.
    /// </remarks>
    private void UpdateDisplay()
    {
        bool free = _board.FreeSpins > 0 || _thisSpinIsFree;
        if (free)
        {
            IPubClip? value = _root.GetChild("freeSpins")?.GetChild("value");
            if (value is not null)
            {
                Localizer.Localize(value, "txtBonusSpinsValue", new Dictionary<string, string>
                {
                    ["value"] = (_board.FreeSpins - _freeSpinCounter).ToString(),
                    ["total"] = (_board.TotalFreeSpins - _freeSpinCounter).ToString(),
                });
            }
        }

        if (free != _freeSpinsShowing)
        {
            ShowFreeSpins(free, LegendFadeTime);
            _freeSpinsShowing = free;
        }

        bool bonus = _board.BonusMultiplier > 1 || _board.NextBonusMultiplier > 1;
        if (bonus)
        {
            IPubClip? badge = _root.GetChild("bonusMultiplier");
            IPubClip? thisValue = badge?.GetChild("thisValue");
            IPubClip? nextValue = badge?.GetChild("nextValue");

            if (thisValue is not null)
            {
                Localizer.Localize(
                    thisValue, "txtBonusMultiplierValue",
                    new Dictionary<string, string>
                    {
                        ["value"] = _board.BonusMultiplier.ToString(),
                    });
            }

            if (nextValue is not null)
            {
                Localizer.Localize(
                    nextValue,
                    _activeState != StateSpinSwitches
                        ? "txtBonusMultiplierValue"
                        : "txtBonusSpinsUnknown",
                    new Dictionary<string, string>
                    {
                        ["value"] = _board.NextBonusMultiplier.ToString(),
                    });
            }
        }

        if (bonus != _bonusMultiplierShowing)
        {
            ShowBonusMultiplier(bonus, LegendFadeTime);
            _bonusMultiplierShowing = bonus;
        }
    }

    private void UpdateTourneyRank()
    {
        if (!Tournament)
        {
            return;
        }

        UpdateTournamentStandings();
        IPubClip? rank = _root.GetChild("tourneyStats")?.GetChild("rank");
        if (rank is not null)
        {
            Localizer.Localize(
                rank, "txtTourneyStatsRank",
                new Dictionary<string, string> { ["rank"] = _player.Rank.ToString() });
        }
    }

    private void UpdateTourneySpin()
    {
        if (!Tournament)
        {
            return;
        }

        IPubClip? spin = _root.GetChild("tourneyStats")?.GetChild("spin");
        if (spin is not null)
        {
            Localizer.Localize(spin, "txtTourneyStatsSpin", new Dictionary<string, string>
            {
                ["game"] = TotalSpins.ToString(),
                ["total"] = TournamentRounds.ToString(),
            });
        }
    }

    private void UpdateWinAmount()
    {
        IPubClip? clip = _root.GetChild("betStats")?.GetChild("winValue");
        if (clip is not null)
        {
            Localizer.Localize(
                clip, "txtWinValue",
                new Dictionary<string, string> { ["value"] = _board.RunningPayout.ToString() });
        }
    }

    private void UpdateBetAmount()
    {
        IPubClip? clip = _root.GetChild("betStats")?.GetChild("betValue");
        if (clip is not null)
        {
            Localizer.Localize(
                clip, "txtBetValue",
                new Dictionary<string, string> { ["value"] = _board.CostPerSpin().ToString() });
        }
    }

    private void UpdateDebug()
    {
        if (!AllowDebug)
        {
            return;
        }

        IPubClip? menu = _root.GetChild("debugMenu");
        if (menu is null)
        {
            return;
        }

        for (var i = 0; i < 9; i++)
        {
            IPubClip? cursor = menu.GetChild("cursor" + i);
            if (cursor is not null)
            {
                cursor.Visible = _debugCursor == i;
            }
        }

        for (var i = 0; i < 6; i++)
        {
            IPubClip? reel = menu.GetChild("reel" + i);
            if (reel is not null)
            {
                ReplaceClip(menu, reel, Variant.ReelImages[_debugOverrides[i]]);
            }
        }

        Text(menu, "apply", _useDebugOverride ? "Force Reels" : "Natural");
        Text(menu, "autoplay", AutoPlay ? "Autoplay: On" : "Autoplay: Off");
        Text(menu, "debugOutput", _showDebug ? "Debug Output: On" : "Debug Output: Off");

        static void Text(IPubClip parent, string child, string value)
        {
            IPubClip? clip = parent.GetChild(child);
            if (clip is not null)
            {
                clip.Text = value;
            }
        }
    }

    private void DebugInfo()
    {
        if (!AllowDebug)
        {
            return;
        }

        IPubClip? debugText = _root.GetChild("debugText");
        IPubClip? callbackText = _root.GetChild("callbackText");
        if (debugText is not null)
        {
            debugText.Visible = _showDebug;
        }

        if (callbackText is not null)
        {
            callbackText.Visible = _showDebug;
        }
    }

    private void UpdateChipstack()
    {
        if (_player.Chips == _chipStack.Value)
        {
            return;
        }

        PlayChipStackSound(_player.Chips - _chipStack.Value);
        _chipStack.SetValue(_player.Chips, CallbackManager);
    }

    private void InitFreeSpins()
    {
        IPubClip? clip = _root.GetChild("freeSpins");
        if (clip is null)
        {
            return;
        }

        clip.Alpha = 0;
        clip.GetChild("frameglow1")?.Stop();
        clip.GetChild("frameglow2")?.Stop();
    }

    private void ShowFreeSpins(bool show, double time)
    {
        IPubClip? clip = _root.GetChild("freeSpins");
        if (clip is null)
        {
            return;
        }

        if (show)
        {
            Fade.In(clip, time, "legend");
            clip.GetChild("frameglow1")?.GotoAndPlay(1);
            clip.GetChild("frameglow2")?.GotoAndPlay(1);
        }
        else
        {
            Fade.Out(clip, time, "legend");
            clip.GetChild("frameglow1")?.Stop();
            clip.GetChild("frameglow2")?.Stop();
        }
    }

    private void InitBonusMultiplier()
    {
        IPubClip? clip = _root.GetChild("bonusMultiplier");
        if (clip is not null)
        {
            clip.Alpha = 0;
        }
    }

    private void ShowBonusMultiplier(bool show, double time)
    {
        IPubClip? clip = _root.GetChild("bonusMultiplier");
        if (clip is null)
        {
            return;
        }

        if (show)
        {
            Fade.In(clip, time, "legend");
        }
        else
        {
            Fade.Out(clip, time, "legend");
        }
    }

    private void ShowSpinText(bool show) =>
        ShowLegend(
            Tournament ? Legend3 : Legend2, AButtonFrame, "txtSpin", null, show,
            LegendFadeTime, false);

    private void ShowFreeSpinText(bool show) =>
        ShowLegend(
            Tournament ? Legend3 : Legend2, AButtonFrame, "txtFreeSpin", null, show,
            LegendFadeTime, false);

    private void ShowEndTourneyText(bool show) =>
        ShowLegend(
            Tournament ? Legend3 : Legend2, AButtonFrame, "txtContinue", null, show,
            LegendFadeTime, false);

    private void ShowAdjustBetText(bool show) =>
        ShowLegend(Legend1, LeftStickFrame, "txtAdjustBet", null, show, LegendFadeTime, false);

    private void ShowBuyChipsText(bool show) =>
        ShowLegend(Legend3, YButtonFrame, "txtBuyChips", null, show, LegendFadeTime, false);

    private void ShowTriggersText(bool show)
    {
        ShowLegend(
            Legend4, LeftTriggerFrame, "txtSpinnerboxOdds", null, show, LegendFadeTime, false);
        ShowLegend(
            Legend5, RightTriggerFrame, Tournament ? "txtStandings" : "txtStats", null, show,
            LegendFadeTime, false);
    }

    private void InitLegend(int index)
    {
        IPubClip? clip = _root.GetChild("button" + index);
        if (clip is null)
        {
            return;
        }

        // STARTS INVISIBLE BY ALPHA, and the ONLY thing that ever raises it is the fade in
        // ShowLegend. See the note at the top of Actions.cs about why that fade has to work.
        clip.Alpha = 0;
        clip.GetChild("button")?.GotoAndStop(1);
    }

    private Fade? ShowLegend(
        int index, string? frame, string? locId, Dictionary<string, string>? locVars, bool show,
        double time, bool blocking)
    {
        IPubClip? clip = _root.GetChild("button" + index);
        if (clip is null)
        {
            return null;
        }

        if (show)
        {
            if (frame is not null)
            {
                clip.GetChild("button")?.GotoAndStop(frame);
            }

            if (locId is not null && clip.GetChild("label") is { } label)
            {
                Localizer.Localize(label, locId, locVars);
            }
        }

        Fade fade = show ? Fade.In(clip, time, "legend") : Fade.Out(clip, time, "legend");
        fade.Blocking = blocking;
        return fade;
    }

    public void PulseLegend(int index)
    {
        IPubClip? button = _root.GetChild("button" + index)?.GetChild("button");
        if (button is not null && OverlayManager is OverlayManager overlays)
        {
            overlays.PulseClip(button, 0);
        }
    }

    public void PulseBankButton() => PulseLegend(Legend3);

    public void PulseStandingsButton() => PulseLegend(Legend5);

    public void PulseQuickTipsButton() => PulseLegend(Legend4);

    /// <summary>Fades the whole board in or out.</summary>
    public Fade? ShowBoard(bool show, double time, bool blocking, object? group)
    {
        IPubClip? clip = _root.GetChild("fadeAll");
        if (clip is null)
        {
            return null;
        }

        Fade fade = show ? Fade.In(clip, time, group) : Fade.Out(clip, time, group);
        fade.Blocking = blocking;
        return fade;
    }

    public override void BankingMenuDispelled()
    {
        // NOT SLOWLY. The bank changed the total by an amount the player chose, so counting it up
        // one ding at a time would be pacing a number they already know.
        _slowChipCount = false;
        UpdateChipstack();
    }

    /// <summary>
    /// Swaps one clip for a different library symbol, keeping its place and size.
    /// </summary>
    /// <remarks>
    /// A 1:1 port of <c>ReplaceClip</c>. Only the developer reel picker uses it.
    /// </remarks>
    internal static void ReplaceClip(IPubClip parent, IPubClip clip, string symbolName)
    {
        int depth = clip.GetDepth();
        if (depth < 0)
        {
            depth = parent.GetNextHighestDepth();
            clip.SwapDepths(depth);
        }

        string name = clip.Name;
        float x = clip.X, y = clip.Y, xs = clip.XScale, ys = clip.YScale;
        clip.RemoveMovieClip();

        IPubClip? replacement = parent.AttachMovie(symbolName, name, depth);
        if (replacement is null)
        {
            return;
        }

        replacement.X = x;
        replacement.Y = y;
        replacement.XScale = xs;
        replacement.YScale = ys;
    }
}
