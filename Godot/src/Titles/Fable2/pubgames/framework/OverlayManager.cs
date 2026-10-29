using System;
using System.Collections.Generic;

namespace Refii.Titles.Fable2.PubGames.Framework;

/// <summary>
/// <c>OverlayManager</c>, the shared dialog layer: a <see cref="Callback"/> that
/// <c>PubGame::Initialize</c> makes callback 0 of the game's manager (0x00161b).
/// </summary>
/// <remarks>
/// Each dialog is a <c>*Showing</c> flag and a per-frame method that attaches its clip when the
/// clip is missing, fades it in, and takes input once the fade has finished. <see cref="Run"/>
/// dispatches to the highest-priority dialog that is up and sets <c>Blocking</c> while any modal
/// one is, which stops the game's callbacks behind it. Fades, delays and pulses run on this
/// manager's OWN callback manager, ticked from <see cref="Run"/>, so they keep moving while the
/// game is frozen. Tournament-only paths (standings results, tournament win) are not ported:
/// Fable II never names a tournament table.
/// </remarks>
public abstract class OverlayManager : Callback, IPubOverlayManager, IPubTutorialOverlays
{
    // Prototype constants, 0x00cae7-0x00cf0d.
    public const double DialogFadeTime = 200;
    public const double TutorialFadeTime = 350;
    public const double SavingFadeTime = 100;
    public const double BoardFadeTime = 750;
    public const double PulseTime = 200;
    public const string DialogGroup = "dg";
    public const string PulseGroup = "dg";

    public const int OverlaysDepth = 100;
    public const int TutorialDepth = 1;
    public const int Tutorial2Depth = 3;
    public const int QuickTipsDepth = 6;
    public const int StandingsDepth = 9;
    public const int StarDepth = 12;
    public const int NeedChipsDepth = 14;
    public const int BankDepth = 15;
    public const int BankSubDepth = 18;
    public const int CashOutDepth = 21;
    public const int QuitDepth = 27;
    public const int HelpDepth = 30;
    public const int FadeDepth = 35;
    public const int ShimPauseDepth = 36;
    public const int ShimControlsDepth = 38;
    public const int ShimSettingsDepth = 40;
    public const int SavingDepth = 49;

    private const string AButtonFrame = "a";
    private const string BButtonFrame = "b";
    private const string YButtonFrame = "y";

    public const double OptionShowTime = 150;
    public const double UpDownNavDelay1 = 500;
    public const double UpDownNavDelay2 = 100;
    public const double LeftRightNavDelay1 = 300;
    public const double MinLeftRightWaitTicks = AnimateBase.FrameTime60Fps;
    public const double GameHelpNavDelay = 250;

    private const int ShimPauseCancel = -1;
    private const int ShimPauseResume = 0;
    private const int ShimPauseHowToPlay = 1;
    private const int ShimPauseControls = 2;
    private const int ShimPauseSettings = 3;
    private const int ShimPauseExit = 4;
    private const int ShimPauseLast = 4;
    private const int ShimSettingsLast = 1;
    private const double ShimSliderMax = 10;

    protected readonly PubGame Game;

    private readonly CallbackManager _callbackManager;
    private IPubClip _overlays;
    private bool _cleanedUp;

    private bool _fadeShowing;
    private bool _quitting;
    private bool _bankShowing;
    private bool _bankSubShowing;
    private bool _cashOutShowing;
    private bool _helpShowing;
    private bool _needChipsShowing;
    private bool _quitShowing;
    private bool _starShowing;
    private bool _standingsShowing;
    private bool _quickTipsShowing;
    private bool _shimPauseShowing;
    private bool _shimControlsShowing;
    private bool _shimSettingsShowing;

    private IPubClip? _fade;
    private IPubClip? _saving;
    private IPubClip? _bank;
    private IPubClip? _bankSub;
    private IPubClip? _cashOut;
    private IPubClip? _help;
    private IPubClip? _needChips;
    private IPubClip? _quit;
    private IPubClip? _star;
    private IPubClip? _standings;
    private IPubClip? _quickTips;
    private IPubClip? _shimPause;
    private IPubClip? _shimControls;
    private IPubClip? _shimSettings;

    private double _forceQuickTipsTimer;

    private BankOption _bankOption = BankOption.QuickChips;
    private bool _bankInputEnabled;
    private double _bankWaitTicks;
    private double _bankMaxWaitTicks;
    private int _bankSliderDir;
    private int _bankSliderDelta;
    private int _bankSliderMax;
    private int _bankLeftRightIncrement = 1;
    private bool _bankSubCancelled;

    private bool _cashOutInputEnabled;
    private bool _quitInputEnabled;
    private bool _quitOption;
    private bool _needChipsInputEnabled;
    private bool _needChipsOption;
    private bool _starInputEnabled;

    private int _gameHelpSection = 1;
    private int _gameHelpSections;
    private double _gameHelpWaitTicks;

    private bool _newTableUnlocked;
    private bool _newConceptArtUnlocked;
    private string? _newTableGameName;
    private int _newTableMin;
    private int _newTableMax;

    private int _shimPauseOption = ShimPauseResume;
    private int _shimSettingsOption;
    private bool _shimInputEnabled;
    private bool _shimControlsInputEnabled;
    private bool _shimSettingsInputEnabled;
    private double _shimWaitTicks;
    private double _shimMaxWaitTicks;
    private int _shimSliderDir;
    private double _shimSliderDelta;
    private double _shimLeftRightIncrement = 0.5;
    private double _sound;
    private double _music;

    protected OverlayManager(PubGame game, IPubClip root)
    {
        Game = game;
        _callbackManager = new CallbackManager(new PubManualTimer());
        _overlays = root.GetChild("overlays")
            ?? root.CreateEmptyMovieClip("overlays", OverlaysDepth);
        _overlays.SwapDepths(root.GetNextHighestDepth());
        Bank = new Banking(game, GetChipsInPlay);
    }

    /// <summary>
    /// <c>OverlayManager::Init</c> (0x002d80-0x002dbd): the balance the session is measured
    /// against, then the board fade. <c>PubGame::Run</c> calls it after lending the buy-in.
    /// </summary>
    public void Init()
    {
        Bank.Snapshot();
        ActivateFade();
    }

    public Banking Bank { get; }

    /// <summary>Wizard's Tower holds this true until its black mask has faded (0x00ceaf).</summary>
    public bool BlockingOverride { get; set; }

    public bool AllowQuickTips { get; set; } = true;

    public bool FadeShowing => _fadeShowing;

    public bool BankShowing => _bankShowing;

    public bool BankSubShowing => _bankSubShowing;

    public bool QuickTipsShowing => _quickTipsShowing;

    public bool StandingsShowing => _standingsShowing;

    public bool QuitShowing => _quitShowing;

    public bool CashOutShowing => _cashOutShowing;

    public bool HelpShowing => _helpShowing;

    public bool NeedChipsShowing => _needChipsShowing;

    public bool StarShowing => _starShowing;

    public bool ShimPauseShowing => _shimPauseShowing;

    public bool ShimControlsShowing => _shimControlsShowing;

    public bool ShimSettingsShowing => _shimSettingsShowing;

    public bool TournamentResultShowing => _standingsShowing && Game.TournamentResultReported;

    public int HelpSection => _gameHelpSection;

    public int HelpSectionCount => _gameHelpSections;

    public BankOption SelectedOption => _bankOption;

    public int SliderValue => _bankSliderDelta;

    /// <summary>
    /// <c>OverlayManager::AbsorbedInput</c> (0x002f53-0x003015): every dialog that owns the pad.
    /// </summary>
    public bool AbsorbingInput =>
        _fadeShowing || _quitting || _bankShowing || _bankSubShowing || _cashOutShowing ||
        _helpShowing || _needChipsShowing || _quitShowing || _starShowing ||
        _shimPauseShowing || _shimControlsShowing || _shimSettingsShowing;

    public void ForceQuickTips(double ticks) => _forceQuickTipsTimer = ticks;

    public void ActivateQuitConfirmation() => _quitShowing = true;

    public void ActivateCashOut() => _cashOutShowing = true;

    public void ActivateGameHelp() => _helpShowing = true;

    public void ActivateNeedChipsAlert() => _needChipsShowing = true;

    public void ActivateShimPauseMenu() => _shimPauseShowing = true;

    public void ActivateShimControlsMenu() => _shimControlsShowing = true;

    public void ActivateShimSettingsMenu() => _shimSettingsShowing = true;

    /// <summary>What a Y press does when nothing else is up (0x003695), without the pulse.</summary>
    public void OpenBank() => _bankShowing = true;

    public void ActivateStarRatingDialog(
        bool table = false, bool concept = false,
        string? gameName = null, int min = 0, int max = 0)
    {
        _newTableUnlocked = table;
        _newConceptArtUnlocked = concept;
        _newTableGameName = gameName;
        _newTableMin = min;
        _newTableMax = max;
        _starShowing = true;
    }

    /// <summary><c>GetChipsInPlay</c> (0x002bb8): a game overrides it to add chips it has staked.</summary>
    public virtual int GetChipsInPlay() => Game.PlayerInfo.GetPlayer(0)?.Chips ?? 0;

    /// <summary><c>GetChipsInPlayButNotLockedIn</c> (0x002beb): stakes the player can still take back.</summary>
    public virtual int GetChipsInPlayButNotLockedIn() => 0;

    protected virtual int GetPlays() => 0;

    protected virtual void InitQuickTips(IPubClip clip)
    {
    }

    protected virtual void UpdateQuickTips(IPubClip clip)
    {
    }

    protected virtual void InitCashGameOverlay(IPubClip clip) =>
        Localize(clip.GetChild("title"), "txtCashGameTitle", null);

    protected virtual void UpdateCashGameOverlay(IPubClip clip)
    {
    }

    protected virtual void InitTournamentOverlay(IPubClip clip)
    {
    }

    protected virtual void UpdateTournamentOverlay(IPubClip clip)
    {
        IPubClip? subtitle = clip.GetChild("subtitle");
        if (subtitle is not null)
        {
            subtitle.Text = string.Empty;
        }
    }

    protected virtual IPubClip? InitNeedChipsAlternate(string instanceName, int depth) => null;

    protected virtual bool NeedChipsAllowBank() => !Game.Tournament;

    protected virtual void PulseBankButton()
    {
    }

    protected virtual void PulseStandingsButton()
    {
    }

    protected virtual void PulseQuickTipsButton()
    {
    }

    /// <summary><c>PulseClip</c> (0x003135-0x00316b), on this manager in the pulse group.</summary>
    public AnimatePulse PulseClip(IPubClip clip, double delay)
    {
        var pulse = new AnimatePulse(clip, PulseTime, delay) { Group = PulseGroup };
        _callbackManager.AddCallbackObject(pulse);
        return pulse;
    }

    /// <summary><c>CleanUp</c> (0x003196-0x0031f6).</summary>
    public void CleanUp()
    {
        _cleanedUp = true;
        _callbackManager.RemoveAllCallbacks();
        _overlays.RemoveMovieClip();
        Dispose();
    }

    /// <summary><c>OverlayManager::Run</c> (0x00323f-0x003d52).</summary>
    public override bool Run(double ticks, object? callbackData, bool blocked)
    {
        if (_cleanedUp)
        {
            return true;
        }

        ticks /= Game.CallbackManager.Speed;

        bool blocking = AbsorbingInput;
        if (BlockingOverride)
        {
            blocking = false;
        }

        if (blocking != Blocking)
        {
            Blocking = blocking;
            Game.Audio.PauseLoopingSounds(blocking);
        }

        Blocking |= Game.GameIsExiting;
        _quitting = false;
        _callbackManager.DoCallbacks(ticks);
        if (_cleanedUp)
        {
            return true;
        }

        GamePad pad = Game.Pad ?? Game.Input.GetKeyboardPadState();

        if (!_fadeShowing && !Game.GameIsExiting)
        {
            if (_shimSettingsShowing)
            {
                ShimSettingsMenu(ticks, pad);
            }
            else if (_shimControlsShowing)
            {
                ShimControlsMenu(pad);
            }
            else if (_shimPauseShowing)
            {
                ShimPauseMenu(ticks, pad);
            }
            else if (_helpShowing)
            {
                GameHelp(ticks, pad);
            }
            else if (_quitShowing)
            {
                QuitConfirmation(pad);
            }
            else if (_cashOutShowing)
            {
                CashOut(pad);
            }
            else if (_bankSubShowing)
            {
                BankingSubMenu(ticks, pad);
            }
            else if (_bankShowing)
            {
                BankingMenu(ticks, pad);
            }
            else if (_needChipsShowing)
            {
                NeedChips(pad);
            }
            else if (_starShowing)
            {
                Star(pad);
            }
            else if (pad.BtnYPressed &&
                     !_bankSubShowing && (!Game.Tournament || !Game.TournamentResultReported))
            {
                if (!_bankShowing)
                {
                    PulseBankButton();
                }

                _bankShowing = true;
            }

            UpdateStandingsState(pad);
            UpdateQuickTipsState(ticks, pad);
        }
        else
        {
            // 0x003c5d: during a fade or once exiting, only a forced quick-tips card shows.
            bool show = _forceQuickTipsTimer > 0;
            if (_quickTipsShowing != show)
            {
                _quickTipsShowing = show;
                if (show && _quickTips is null)
                {
                    _quickTips = CreateQuickTips();
                }

                ShowAnim(show, _quickTips, 1);
            }

            if (_quickTipsShowing && _quickTips is not null)
            {
                UpdateQuickTips(_quickTips);
            }
        }

        if (_forceQuickTipsTimer > 0)
        {
            Blocking = true;
        }

        return false;
    }

    /// <summary>
    /// Standings follow the right trigger, level-triggered (0x00369f-0x003898). Cash tables have
    /// no B close: that path needs a reported tournament result.
    /// </summary>
    private void UpdateStandingsState(GamePad pad)
    {
        bool show = !(_forceQuickTipsTimer > 0) &&
            ((Game.Tournament && Game.TournamentResultReported) ||
             (!Game.Tutorial && !_quickTipsShowing && !_bankShowing && !Blocking &&
              !(pad.RightTrigger < GamePad.TriggerThreshold)));

        if (_standingsShowing != show)
        {
            _standingsShowing = show;
            if (show && (!Game.Tournament || !Game.TournamentResultReported))
            {
                PulseStandingsButton();
            }

            if (show && _standings is null)
            {
                _standings = CreateStandings();
            }

            ShowAnim(show, _standings, DialogFadeTime);
            Game.Audio.PlaySound(show ? "G_fullscreenMenuOpens" : "G_fullscreenMenuCloses");
        }

        if (_standingsShowing && _standings is not null)
        {
            UpdateStandings(_standings);
        }
    }

    /// <summary>
    /// Quick tips follow the left trigger (0x0039ae-0x003c58); a forced card shows until its
    /// timer runs out or A, B or X dismisses it, and the bank dismisses it outright.
    /// </summary>
    private void UpdateQuickTipsState(double ticks, GamePad pad)
    {
        bool show = AllowQuickTips &&
            (_forceQuickTipsTimer > 0 ||
             (!_standingsShowing && !_bankShowing && !Blocking &&
              !(pad.LeftTrigger < GamePad.TriggerThreshold)));

        if (_quickTipsShowing != show)
        {
            _quickTipsShowing = show;
            if (show && !(_forceQuickTipsTimer > 0))
            {
                PulseQuickTipsButton();
            }

            if (show && _quickTips is null)
            {
                _quickTips = CreateQuickTips();
            }

            ShowAnim(show, _quickTips, DialogFadeTime);
            Game.Audio.PlaySound(show ? "G_fullscreenMenuOpens" : "G_fullscreenMenuCloses");
        }

        if (!_quickTipsShowing)
        {
            return;
        }

        if (_forceQuickTipsTimer > 0 && !Blocking)
        {
            _forceQuickTipsTimer -= ticks;
        }

        if (_quickTips is not null)
        {
            UpdateQuickTips(_quickTips);
        }

        if (_bankShowing ||
            (!Blocking && _forceQuickTipsTimer > 0 &&
             (pad.BtnAPressed || pad.BtnBPressed || pad.BtnXPressed)))
        {
            _forceQuickTipsTimer = 0;
        }
    }

    /// <summary><c>DelayCallback</c> (0x003de3-0x003e4a), in the dialog group.</summary>
    private void DelayCallback(double delay, System.Action endCallback, bool blocking)
    {
        _callbackManager.AddCallbackObject(
            new Delay(delay, _ => endCallback(), null, DialogGroup, blocking));
    }

    public void ActivateFade()
    {
        _fadeShowing = true;
        _fade ??= Attach("overlay_fade", "overlay_fade", FadeDepth);
        DelayCallback(1, StartFade, true);
    }

    private void StartFade()
    {
        AnimateAlpha anim = ShowAnim(false, _fade, BoardFadeTime);
        anim.Blocking = true;
        anim.SetEndCallback(_ => FadeCompleted(), null);
    }

    private void FadeCompleted()
    {
        _fadeShowing = false;
        _fade?.RemoveMovieClip();
        _fade = null;
    }

    public void ActivateFadeOut()
    {
        _fadeShowing = true;
        if (_fade is null)
        {
            _fade = Attach("overlay_fade", "overlay_fade", FadeDepth);
            _fade.Alpha = 0;
        }

        DelayCallback(1, StartFadeOut, true);
    }

    private void StartFadeOut()
    {
        AnimateAlpha anim = ShowAnim(true, _fade, BoardFadeTime / 2);
        anim.Blocking = true;
        anim.SetEndCallback(_ => FadeOutCompleted(), null);
    }

    private void FadeOutCompleted()
    {
        _fadeShowing = false;
        Game.GameReallyOver();
    }

    /// <summary><c>ShowSaveIcon</c> (0x0042a8-0x00433c).</summary>
    public void ShowSaveIcon(bool show)
    {
        if (_saving is null)
        {
            _saving = Attach("overlay_saving", "overlay_saving", SavingDepth);
            _saving.Alpha = 0;
        }

        if (show)
        {
            _saving.Play();
        }
        else
        {
            _saving.Stop();
        }

        ShowAnim(show, _saving, SavingFadeTime);
    }

    /// <summary><c>SetOffsets</c> (0x004374-0x004393).</summary>
    public void SetOffsets(float x, float y)
    {
        _overlays.X += x;
        _overlays.Y += y;
    }

    /// <summary><c>SelectAnim</c> (0x0043cb-0x004492): 1.25x the clip's first-seen scale, or back.</summary>
    private void SelectAnim(bool select, IPubClip? clip, double time)
    {
        if (clip is null)
        {
            return;
        }

        float baseX = ClipMembers.Remember(clip, "baseXScale", clip.XScale);
        float baseY = ClipMembers.Remember(clip, "baseYScale", clip.YScale);
        double scale = select ? 1.25 : 1;
        _callbackManager.AddCallbackObject(new AnimateScale(
            clip.XScale, clip.YScale, scale * baseX, scale * baseY, clip, time, 0,
            AnimateBase.StyleLinear)
        {
            Group = DialogGroup,
        });
    }

    /// <summary><c>ShowAnim</c> (0x0044bf-0x004546): alpha to 100 or 0 in the dialog group.</summary>
    private AnimateAlpha ShowAnim(bool show, IPubClip? clip, double time)
    {
        var anim = new AnimateAlpha(
            clip?.Alpha ?? 0, show ? 100 : 0, clip, time, 0, AnimateBase.StyleLinear)
        {
            Group = DialogGroup,
        };
        _callbackManager.AddCallbackObject(anim);
        return anim;
    }

    /// <summary><c>GreyOutAnim</c> (0x004581-0x004601): alpha to 100, or to the grey level.</summary>
    private void GreyOutAnim(bool enabled, IPubClip? clip, double time, double greyAlpha)
    {
        _callbackManager.AddCallbackObject(new AnimateAlpha(
            clip?.Alpha ?? 0, enabled ? 100 : greyAlpha, clip, time, 0, AnimateBase.StyleLinear)
        {
            Group = DialogGroup,
        });
    }

    /// <summary><c>SelectBankingOption</c> (0x004ff7-0x00504d), shared by every menu list.</summary>
    private void SelectBankingOption(bool select, int option, bool enabled, double time, IPubClip clip)
    {
        IPubClip? line = clip.GetChild("line" + option);
        SelectAnim(select, line, time);
        GreyOutAnim(enabled, line, time, 50);
        ShowAnim(select, clip.GetChild("select" + option), time);
    }

    /// <summary>Opens a dialog clip at alpha 0 and enables its input when the fade-in ends.</summary>
    private void FadeIn(IPubClip clip, System.Action enable)
    {
        clip.Alpha = 0;
        ShowAnim(true, clip, DialogFadeTime).SetEndCallback(_ => enable(), null);
    }

    /// <summary>Fades a dialog out and hands its clip to <paramref name="kill"/> at the end.</summary>
    private void FadeOut(IPubClip clip, System.Action<IPubClip> kill)
    {
        ShowAnim(false, clip, DialogFadeTime).SetEndCallback(_ => kill(clip), null);
        Game.Audio.PlaySound("G_fullscreenMenuCloses");
    }

    private bool[] BankOptionsEnabled(out int quickChips)
    {
        Player human = Human;
        bool casual = !Game.Tutorial && !Game.Tournament;
        int credit = Bank.GetCredit();
        quickChips = casual ? Math.Min(credit, Game.BuyIn) : 0;
        return new[]
        {
            casual && quickChips > 0,
            casual && credit > 0,
            casual && human.Gold > 0 && human.Debt > 0,
            true,
        };
    }

    private static bool Enabled(bool[] enabled, int option) =>
        option >= 0 && option < enabled.Length && enabled[option];

    /// <summary><c>BankingMenu</c> (0x004743-0x004fb3).</summary>
    private void BankingMenu(double ticks, GamePad pad)
    {
        Player human = Human;
        bool[] enabled = BankOptionsEnabled(out int quickChips);

        if (_bank is null)
        {
            _bank = Attach("overlay_bank", "overlay_bank", BankDepth);
            _bankWaitTicks = 0;
            _bankMaxWaitTicks = UpDownNavDelay1;
            _bankInputEnabled = false;
            if (Game.Tutorial || Game.Tournament)
            {
                _bankOption = BankOption.CashOut;
            }
            else if (_bankOption < Banking.FirstOption || _bankOption > Banking.LastOption)
            {
                _bankOption = BankOption.QuickChips;
            }

            Localize(_bank.GetChild("title"), "txtBankTitle", null);
            Localize(
                _bank.GetChild("line0")?.GetChild("itemtext"), "txtBankQuickChips",
                new Dictionary<string, string> { ["chips"] = quickChips.ToString() });
            Localize(_bank.GetChild("line1")?.GetChild("itemtext"), "txtBankGetChips", null);
            Localize(_bank.GetChild("line2")?.GetChild("itemtext"), "txtBankPayDebtWithGold", null);
            Localize(_bank.GetChild("line3")?.GetChild("itemtext"), "txtBankCashOut", null);
            Buttons(_bank, "txtBankSelect", "txtBankCancel");

            for (int i = (int)Banking.FirstOption; i <= (int)Banking.LastOption; i++)
            {
                SelectBankingOption(i == (int)_bankOption, i, enabled[i], 1, _bank);
            }

            FadeIn(_bank, () => _bankInputEnabled = true);
            Game.Audio.PlaySound("G_fullscreenMenuOpens");
        }

        int option = (int)_bankOption;
        if (_bankInputEnabled)
        {
            bool close = false;
            if (pad.BtnAPressed || Game.AutoPlay)
            {
                if (Enabled(enabled, option))
                {
                    close = true;
                    if (option == (int)BankOption.QuickChips)
                    {
                        human.Chips += quickChips;
                        human.Debt += quickChips;
                        Game.UpdatePlayerHoldings();
                    }
                }
                else
                {
                    Game.Audio.PlaySound("G_error");
                }
            }
            else if (pad.BtnBPressed)
            {
                option = (int)BankOption.Cancel;
                close = true;
            }
            else
            {
                int direction = Vertical(pad);
                if (direction == 0)
                {
                    _bankWaitTicks = 0;
                    _bankMaxWaitTicks = UpDownNavDelay1;
                }
                else if (!(_bankWaitTicks > 0))
                {
                    _bankWaitTicks = _bankMaxWaitTicks;
                    _bankMaxWaitTicks = UpDownNavDelay2;
                    option = Wrap(option + direction, (int)Banking.LastOption);
                    Game.Audio.PlaySound("G_moveSelector");
                }
                else
                {
                    _bankWaitTicks -= ticks;
                }
            }

            if (close)
            {
                _bankInputEnabled = false;
                FadeOut(_bank, KillBankingMenu);
            }
        }

        if (option != (int)_bankOption)
        {
            SelectBankingOption(true, option, Enabled(enabled, option), OptionShowTime, _bank);
            SelectBankingOption(
                false, (int)_bankOption, Enabled(enabled, (int)_bankOption), OptionShowTime, _bank);
            _bankOption = (BankOption)option;
        }
    }

    /// <summary>
    /// <c>KillBankingMenu</c> (0x00508e-0x005246). Every exit resets the option to Quick Chips
    /// and tells the game with <c>BankingMenuDispelled</c>; Cash Out ends the game; a cancelled
    /// sub-menu returns to the bank.
    /// </summary>
    private void KillBankingMenu(IPubClip clip)
    {
        Remove(clip);
        if (_bankShowing)
        {
            _bankShowing = false;
            switch (_bankOption)
            {
                case BankOption.Cancel:
                    _bankOption = BankOption.QuickChips;
                    DelayCallback(1, Game.BankingMenuDispelled, false);
                    break;
                case BankOption.GetChips:
                case BankOption.PayDebtWithGold:
                    _bankSubShowing = true;
                    _bankSubCancelled = false;
                    break;
                case BankOption.CashOut:
                    _quitting = true;
                    _bankOption = BankOption.QuickChips;
                    DelayCallback(1, Game.EndGame, false);
                    break;
                default:
                    DelayCallback(1, Game.BankingMenuDispelled, false);
                    break;
            }

            return;
        }

        _bankSubShowing = false;
        if (_bankSubCancelled)
        {
            _bankShowing = true;
            return;
        }

        _bankOption = BankOption.QuickChips;
        DelayCallback(1, Game.BankingMenuDispelled, false);
    }

    /// <summary><c>BankingSubMenu</c> (0x005298-0x005c42).</summary>
    private void BankingSubMenu(double ticks, GamePad pad)
    {
        Player human = Human;
        bool borrowing = _bankOption == BankOption.GetChips;

        if (_bankSub is null)
        {
            _bankSub = Attach("overlay_banksub", "overlay_banksub", BankSubDepth);
            _bankSliderDir = 0;
            _bankSliderDelta = 0;
            _bankWaitTicks = 0;
            _bankMaxWaitTicks = LeftRightNavDelay1;
            _bankLeftRightIncrement = 1;
            _bankInputEnabled = false;
            SetVisible(_bankSub.GetChild("thumb0"), false);
            SetVisible(_bankSub.GetChild("thumb1"), false);
            Buttons(_bankSub, "txtBankConfirm", "txtBankCancel");
            Localize(_bankSub.GetChild("line1label"), "txtBankDebtLabel", null);
            Localize(
                _bankSub.GetChild("sliderlabel"),
                borrowing ? "txtBorrowSliderLabel" : "txtDebtSliderLabel", null);
            Localize(
                _bankSub.GetChild("line0label"),
                borrowing ? "txtBankChipsLabel" : "txtBankGoldLabel", null);
            Localize(
                _bankSub.GetChild("title"),
                borrowing ? "txtBankGetChipsTitle" : "txtBankPayDebtWithGoldTitle", null);
            Localize(
                _bankSub.GetChild("instructions"),
                borrowing ? "txtBankGetChipsInstructions" : "txtBankPayDebtWithGoldInstructions",
                null);
            Localize(
                _bankSub.GetChild("tip"),
                borrowing ? "txtBankGetChipsTip" : "txtBankPayDebtWithGoldTip", null);
            _bankSliderMax = Bank.SliderMax(_bankOption);
            FadeIn(_bankSub, () => _bankInputEnabled = true);
            Game.Audio.PlaySound("G_fullscreenMenuOpens");
        }

        Localize(
            _bankSub.GetChild("line0"), borrowing ? "txtBankChipsValue" : "txtBankGoldValue",
            Vars("value", borrowing ? human.Chips + _bankSliderDelta : human.Gold - _bankSliderDelta));
        Localize(
            _bankSub.GetChild("line1"), "txtBankDebtValue",
            Vars("value", borrowing ? human.Debt + _bankSliderDelta : human.Debt - _bankSliderDelta));
        Localize(_bankSub.GetChild("slidervalue"), "txtSliderValue", Vars("value", _bankSliderDelta));
        PlaceThumb(_bankSub, "thumb", "thumb0", "thumb1", _bankSliderDelta, _bankSliderMax);

        if (!_bankInputEnabled)
        {
            return;
        }

        bool close = false;
        if (pad.BtnAPressed)
        {
            if (_bankSliderDelta > 0)
            {
                if (borrowing)
                {
                    human.Chips += _bankSliderDelta;
                    human.Debt += _bankSliderDelta;
                }
                else
                {
                    human.Gold -= _bankSliderDelta;
                    human.Debt -= _bankSliderDelta;
                }

                Game.UpdatePlayerHoldings();
            }

            close = true;
        }
        else if (pad.BtnBPressed)
        {
            _bankSubCancelled = true;
            close = true;
        }
        else
        {
            int direction = Horizontal(pad);
            if (direction == 0 || _bankSliderDir == -direction)
            {
                _bankWaitTicks = 0;
                _bankMaxWaitTicks = LeftRightNavDelay1;
                _bankLeftRightIncrement = 1;
            }
            else if (!(_bankWaitTicks > 0))
            {
                _bankSliderDelta += direction * _bankLeftRightIncrement;
                if (_bankSliderDelta < 0)
                {
                    _bankSliderDelta = 0;
                }
                else if (_bankSliderDelta > _bankSliderMax)
                {
                    _bankSliderDelta = _bankSliderMax;
                }
                else
                {
                    Game.Audio.PlaySound("G_sliderUp");
                }

                _bankWaitTicks = _bankMaxWaitTicks;
                _bankMaxWaitTicks /= 2;
                if (_bankMaxWaitTicks < MinLeftRightWaitTicks)
                {
                    _bankLeftRightIncrement +=
                        (int)Math.Floor(Math.Max(_bankLeftRightIncrement * 0.25, 1));
                }
            }
            else
            {
                _bankWaitTicks -= ticks;
            }

            _bankSliderDir = direction;
        }

        if (close)
        {
            _bankInputEnabled = false;
            FadeOut(_bankSub, KillBankingMenu);
        }
    }

    /// <summary><c>ShimPauseMenu</c> (0x00740e-0x007ac8).</summary>
    private void ShimPauseMenu(double ticks, GamePad pad)
    {
        int option = _shimPauseOption;
        if (_shimPause is null)
        {
            _shimPause = Attach("overlay_shim_pause", "overlay_shim_pause", ShimPauseDepth);
            _shimWaitTicks = 0;
            _shimMaxWaitTicks = UpDownNavDelay1;
            _shimInputEnabled = false;
            if (_shimPauseOption < ShimPauseResume || _shimPauseOption > ShimPauseLast)
            {
                _shimPauseOption = ShimPauseResume;
            }

            Localize(_shimPause.GetChild("title"), "txtShimPaused", null);
            Localize(_shimPause.GetChild("line0")?.GetChild("itemtext"), "txtShimPauseResume", null);
            Localize(_shimPause.GetChild("line1")?.GetChild("itemtext"), "txtShimPauseHelp", null);
            Localize(
                _shimPause.GetChild("line2")?.GetChild("itemtext"), "txtShimPauseControls", null);
            Localize(
                _shimPause.GetChild("line3")?.GetChild("itemtext"), "txtShimPauseSettings", null);
            Localize(
                _shimPause.GetChild("line4")?.GetChild("itemtext"),
                Game.Tutorial ? "txtShimPauseTutorialExit" : "txtShimPauseExit", null);
            Buttons(_shimPause, "txtShimConfirm", "txtShimCancel");

            for (int i = ShimPauseResume; i <= ShimPauseLast; i++)
            {
                SelectBankingOption(i == _shimPauseOption, i, true, 1, _shimPause);
            }

            FadeIn(_shimPause, () => _shimInputEnabled = true);
            Game.Audio.PlaySound("G_pauseUnpause");
        }

        if (_shimInputEnabled)
        {
            bool close = false;
            if (pad.BtnAPressed)
            {
                close = option is >= ShimPauseResume and <= ShimPauseExit;
            }
            else if (pad.BtnBPressed)
            {
                option = ShimPauseCancel;
                close = true;
            }
            else
            {
                int direction = Vertical(pad);
                if (direction == 0)
                {
                    _shimWaitTicks = 0;
                    _shimMaxWaitTicks = UpDownNavDelay1;
                }
                else if (!(_shimWaitTicks > 0))
                {
                    _shimWaitTicks = _shimMaxWaitTicks;
                    _shimMaxWaitTicks = UpDownNavDelay2;
                    option = Wrap(option + direction, ShimPauseLast);
                    Game.Audio.PlaySound("G_moveSelector");
                }
                else
                {
                    _shimWaitTicks -= ticks;
                }
            }

            if (close)
            {
                _shimInputEnabled = false;
                ShowAnim(false, _shimPause, DialogFadeTime)
                    .SetEndCallback(_ => KillShimPauseMenu(), null);
                Game.Audio.PlaySound("G_pauseUnpause");
            }
        }

        if (option != _shimPauseOption)
        {
            SelectBankingOption(true, option, true, OptionShowTime, _shimPause);
            SelectBankingOption(false, _shimPauseOption, true, OptionShowTime, _shimPause);
            _shimPauseOption = option;
        }
    }

    /// <summary><c>KillShimPauseMenu</c> (0x007aec-0x007c41).</summary>
    private void KillShimPauseMenu()
    {
        Remove(_shimPause);
        if (!_shimPauseShowing)
        {
            return;
        }

        _shimPauseShowing = false;
        switch (_shimPauseOption)
        {
            case ShimPauseCancel:
                _shimPauseOption = ShimPauseResume;
                break;
            case ShimPauseHowToPlay:
                Game.ActivatedGameHelp();
                _shimPauseOption = ShimPauseHowToPlay;
                break;
            case ShimPauseControls:
                _shimControlsShowing = true;
                break;
            case ShimPauseSettings:
                _shimSettingsShowing = true;
                break;
            case ShimPauseExit:
                _quitting = true;
                _shimPauseOption = ShimPauseResume;
                DelayCallback(1, Game.EndGame, false);
                break;
        }
    }

    /// <summary><c>ShimControlsMenu</c> (0x007c95-0x008155).</summary>
    private void ShimControlsMenu(GamePad pad)
    {
        if (_shimControls is null)
        {
            _shimControls = Attach("overlay_controller", "overlay_controller", ShimControlsDepth);
            _shimControlsInputEnabled = false;
            string game = Game.GameIndex switch
            {
                0 => "FortunesTower",
                1 => "Keystone",
                2 => "SpinnerBox",
                _ => string.Empty,
            };

            string prefix = "txtShimControls" + game;
            Localize(_shimControls.GetChild("title"), "txtShimControlsTitle", null);
            foreach ((string child, string suffix) in new[]
            {
                ("labelA", "BtnA"), ("labelB", "BtnB"), ("labelX", "BtnX"), ("labelY", "BtnY"),
            })
            {
                Localize(_shimControls.GetChild(child), prefix + suffix, null);
            }

            Localize(_shimControls.GetChild("labelStart"), "txtPause", null);
            foreach ((string child, string suffix) in new[]
            {
                ("labelLT", "LeftTrigger"), ("labelRT", "RightTrigger"),
                ("labelLB", "LeftButton"), ("labelRB", "RightButton"),
                ("labelLS", "LeftStick"), ("labelRS", "RightStick"),
            })
            {
                Localize(_shimControls.GetChild(child), prefix + suffix, null);
            }

            Localize(_shimControls.GetChild("gameName"), "txtGameName", null);
            Localize(_shimControls.GetChild("button1")?.GetChild("label"), "txtShimCancel", null);
            _shimControls.GetChild("button1")?.GetChild("button")?.GotoAndStop(BButtonFrame);
            FadeIn(_shimControls, () => _shimControlsInputEnabled = true);
            Game.Audio.PlaySound("G_fullscreenMenuOpens");
        }

        if (_shimControlsInputEnabled && (pad.BtnAPressed || pad.BtnBPressed))
        {
            _shimControlsInputEnabled = false;
            FadeOut(_shimControls, _ => KillShimControlsMenu());
        }
    }

    /// <summary><c>KillShimControlsMenu</c> (0x008179-0x0081d8): back to the pause menu.</summary>
    private void KillShimControlsMenu()
    {
        Remove(_shimControls);
        if (_shimControlsShowing)
        {
            _shimPauseOption = ShimPauseControls;
        }

        _shimControlsShowing = false;
        _shimPauseShowing = true;
    }

    /// <summary><c>ShimSettingsMenu</c> (0x00822d-0x008fc2): sound and music, 0-10 by halves.</summary>
    private void ShimSettingsMenu(double ticks, GamePad pad)
    {
        int option = _shimSettingsOption;
        if (_shimSettings is null)
        {
            _sound = Game.SoundVolume;
            _music = Game.MusicVolume;
            _shimSettings = Attach("overlay_shim_settings", "overlay_shim_settings", ShimSettingsDepth);
            _shimSliderDir = 0;
            _shimSliderDelta = 0;
            _shimWaitTicks = 0;
            _shimMaxWaitTicks = LeftRightNavDelay1;
            _shimLeftRightIncrement = 0.5;
            _shimSettingsInputEnabled = false;
            SetVisible(_shimSettings.GetChild("thumb0Sound"), false);
            SetVisible(_shimSettings.GetChild("thumb1Sound"), false);
            SetVisible(_shimSettings.GetChild("thumb0Music"), false);
            SetVisible(_shimSettings.GetChild("thumb1Music"), false);
            SelectBankingOption(true, 0, true, 1, _shimSettings);
            SelectBankingOption(false, 1, true, 1, _shimSettings);
            Buttons(_shimSettings, "txtBankConfirm", "txtBankCancel");
            Localize(_shimSettings.GetChild("button3")?.GetChild("label"), "txtRestoreDefaults", null);
            _shimSettings.GetChild("button3")?.GetChild("button")?.GotoAndStop(YButtonFrame);
            Localize(_shimSettings.GetChild("title"), "txtShimPauseSettings", null);
            Localize(_shimSettings.GetChild("sliderlabelSound"), "txtSounds", null);
            Localize(_shimSettings.GetChild("sliderlabelMusic"), "txtMusic", null);
            SetText(_shimSettings.GetChild("slidervalueSound"), string.Empty);
            SetText(_shimSettings.GetChild("slidervalueMusic"), string.Empty);
            _shimSliderDelta = _sound;
            PlaceSettingsThumbs();
            FadeIn(_shimSettings, () => _shimSettingsInputEnabled = true);
            Game.Audio.PlaySound("G_fullscreenMenuOpens");
        }

        if (_shimSettingsOption == 0)
        {
            _sound = _shimSliderDelta;
            SetText(_shimSettings.GetChild("slidervalueSound"), string.Empty);
        }
        else
        {
            _music = _shimSliderDelta;
            SetText(_shimSettings.GetChild("slidervalueMusic"), string.Empty);
        }

        PlaceSettingsThumbs();

        if (_shimSettingsInputEnabled)
        {
            bool close = false;
            if (pad.BtnAPressed)
            {
                Game.SoundVolume = _sound;
                Game.MusicVolume = _music;
                Game.UpdateVolumeSettings();
                close = true;
            }
            else if (pad.BtnBPressed)
            {
                close = true;
            }
            else if (pad.BtnYPressed)
            {
                _sound = 5;
                _music = 5;
                PlaceSettingsThumbs();
                SetText(_shimSettings.GetChild("slidervalueSound"), string.Empty);
                SetText(_shimSettings.GetChild("slidervalueMusic"), string.Empty);
                _shimSliderDelta = _shimSettingsOption == 0 ? _sound : _music;
            }
            else if (pad.LeftThumbLeft || pad.DPadLeft || pad.LeftThumbRight || pad.DPadRight)
            {
                int direction = Horizontal(pad);
                if (direction == 0 || _shimSliderDir == -direction)
                {
                    _shimWaitTicks = 0;
                    _shimMaxWaitTicks = LeftRightNavDelay1;
                    _shimLeftRightIncrement = 0.5;
                }
                else if (!(_shimWaitTicks > 0))
                {
                    _shimSliderDelta += direction * _shimLeftRightIncrement;
                    if (_shimSliderDelta < 0)
                    {
                        _shimSliderDelta = 0;
                    }
                    else if (_shimSliderDelta > ShimSliderMax)
                    {
                        _shimSliderDelta = ShimSliderMax;
                    }
                    else
                    {
                        Game.Audio.PlaySound("G_sliderUp");
                    }

                    _shimWaitTicks = _shimMaxWaitTicks;
                    _shimMaxWaitTicks /= 2;
                    if (_shimMaxWaitTicks < MinLeftRightWaitTicks)
                    {
                        _shimLeftRightIncrement +=
                            Math.Floor(Math.Max(_shimLeftRightIncrement * 0.5, 1)) * 0.5;
                    }
                }
                else
                {
                    _shimWaitTicks -= ticks;
                }

                _shimSliderDir = direction;
            }
            else if (pad.LeftThumbUp || pad.DPadUp || pad.LeftThumbDown || pad.DPadDown)
            {
                int direction = Vertical(pad);
                if (direction == 0)
                {
                    _shimWaitTicks = 0;
                    _shimMaxWaitTicks = UpDownNavDelay1;
                }
                else if (!(_shimWaitTicks > 0))
                {
                    _shimWaitTicks = _shimMaxWaitTicks;
                    _shimMaxWaitTicks = UpDownNavDelay2 * 2;
                    option = Wrap(option + direction, ShimSettingsLast);
                    _shimSliderDelta = option == 0 ? _sound : _music;
                    Game.Audio.PlaySound("G_moveSelector");
                }
                else
                {
                    _shimWaitTicks -= ticks;
                }
            }
            else
            {
                _shimWaitTicks = 0;
                _shimMaxWaitTicks = LeftRightNavDelay1;
                _shimLeftRightIncrement = 0.5;
            }

            if (close)
            {
                _shimSettingsInputEnabled = false;
                FadeOut(_shimSettings, _ => KillShimSettingsMenu());
            }
        }

        if (option != _shimSettingsOption)
        {
            SelectBankingOption(true, option, true, OptionShowTime, _shimSettings);
            SelectBankingOption(false, _shimSettingsOption, true, OptionShowTime, _shimSettings);
            _shimSettingsOption = option;
        }
    }

    private void PlaceSettingsThumbs()
    {
        if (_shimSettings is null)
        {
            return;
        }

        PlaceThumb(_shimSettings, "thumbSound", "thumb0Sound", "thumb1Sound", _sound, ShimSliderMax);
        PlaceThumb(_shimSettings, "thumbMusic", "thumb0Music", "thumb1Music", _music, ShimSliderMax);
    }

    /// <summary><c>KillShimSettingsMenu</c> (0x008fe6-0x009069): back to the pause menu.</summary>
    private void KillShimSettingsMenu()
    {
        Remove(_shimSettings);
        _shimSettingsOption = 0;
        if (_shimSettingsShowing)
        {
            _shimPauseOption = ShimPauseSettings;
        }

        _shimSettingsShowing = false;
        _shimPauseShowing = true;
    }

    /// <summary><c>QuitConfirmation</c> (0x00909f-0x00933c).</summary>
    private void QuitConfirmation(GamePad pad)
    {
        if (_quit is null)
        {
            _quit = Attach("overlay_alert2", "overlay_quitgame", QuitDepth);
            _quitInputEnabled = false;
            _quitOption = false;
            Localize(
                _quit.GetChild("body"),
                Game.Tournament ? "txtQuitBodyTournament" : "txtQuitBodyCash", null);
            Buttons(_quit, "txtQuitConfirm", "txtQuitCancel");
            FadeIn(_quit, () => _quitInputEnabled = true);
            Game.Audio.PlaySound("G_fullscreenMenuOpens");
            return;
        }

        if (!_quitInputEnabled || !(pad.BtnAPressed || pad.BtnBPressed))
        {
            return;
        }

        _quitOption = pad.BtnAPressed;
        _quitInputEnabled = false;
        FadeOut(_quit, KillQuitMenu);
    }

    /// <summary><c>KillQuitMenu</c> (0x00937f-0x009454).</summary>
    private void KillQuitMenu(IPubClip clip)
    {
        _quitShowing = false;
        Remove(clip);
        if (!_quitOption)
        {
            return;
        }

        if (Game.Tournament || Game.Tutorial || !Game.BetsMade)
        {
            _quitting = true;
            DelayCallback(1, Game.GameOver, true);
        }
        else
        {
            _cashOutShowing = true;
        }
    }

    /// <summary>
    /// <c>GameHelp</c> (0x00948a-0x009843). B or Back closes it even while it is still fading in.
    /// </summary>
    private void GameHelp(double ticks, GamePad pad)
    {
        if (_help is null)
        {
            _help = Attach("overlay_help", "overlay_help", HelpDepth);
            _gameHelpWaitTicks = 0;
            _gameHelpSection = 1;
            _gameHelpSections = 0;
            while (Game.Localizer.Contains("txtGameHelpText" + (_gameHelpSections + 1)))
            {
                _gameHelpSections++;
            }

            Localize(_help.GetChild("title"), "txtGameHelpTitle", null);
            UpdateGameHelpSection();
            Localize(_help.GetChild("button1")?.GetChild("label"), "txtGameHelpDone", null);
            _help.GetChild("button1")?.GetChild("button")?.GotoAndStop(BButtonFrame);
            FadeIn(_help, () => { });
            Game.Audio.PlaySound("G_fullscreenMenuOpens");
            return;
        }

        if (pad.BtnBPressed || pad.BackPressed)
        {
            FadeOut(_help, KillGameHelp);
            return;
        }

        int direction = Horizontal(pad);
        if (direction == 0)
        {
            _gameHelpWaitTicks = 0;
        }
        else if (!(_gameHelpWaitTicks > 0))
        {
            _gameHelpWaitTicks = GameHelpNavDelay;
            ChangeGameHelpSection(direction > 0 ? 1 : -1);
            Game.Audio.PlaySound("G_moveSelector");
        }
        else
        {
            _gameHelpWaitTicks -= ticks;
        }
    }

    private void ChangeGameHelpSection(int delta)
    {
        int section = _gameHelpSection + delta;
        if (section < 1)
        {
            section = 1;
        }
        else if (section > _gameHelpSections)
        {
            section = _gameHelpSections;
        }

        if (section != _gameHelpSection)
        {
            _gameHelpSection = section;
            UpdateGameHelpSection();
            Game.Audio.PlaySound("G_fullscreenMenuOpens");
        }
    }

    private void UpdateGameHelpSection()
    {
        if (_help is null)
        {
            return;
        }

        Localize(_help.GetChild("body"), "txtGameHelpText" + _gameHelpSection, null);
        Localize(_help.GetChild("section"), "txtGameHelpSection" + _gameHelpSection, null);
        _help.GetChild("image")?.GotoAndStop(_gameHelpSection);
        SetAlpha(_help.GetChild("leftArrow"), _gameHelpSection > 1 ? 100 : 0);
        SetAlpha(_help.GetChild("rightArrow"), _gameHelpSection < _gameHelpSections ? 100 : 0);
    }

    /// <summary><c>KillGameHelp</c> (0x009a52-0x009aad): back to the pause menu it came from.</summary>
    private void KillGameHelp(IPubClip clip)
    {
        _helpShowing = false;
        Remove(clip);
        if (_shimPauseOption == ShimPauseHowToPlay)
        {
            _shimPauseShowing = true;
        }

        Game.GameHelpDispelled();
    }

    /// <summary><c>NeedChips</c> (0x009ae3-0x009e6a).</summary>
    private void NeedChips(GamePad pad)
    {
        if (_needChips is null)
        {
            _needChips = InitNeedChipsAlternate("overlay_needchips", NeedChipsDepth);
            if (_needChips is null)
            {
                _needChips = Attach(
                    Game.Tournament ? "overlay_alert1" : "overlay_alert2", "overlay_needchips",
                    NeedChipsDepth);
                SetText(_needChips.GetChild("body2"), string.Empty);
                if (Game.Tournament)
                {
                    Localize(_needChips.GetChild("body"), "txtNeedChipsBodyTournament", null);
                    Localize(_needChips.GetChild("button1")?.GetChild("label"), "txtNeedChipsOK", null);
                    _needChips.GetChild("button1")?.GetChild("button")?.GotoAndStop(AButtonFrame);
                }
                else
                {
                    Localize(_needChips.GetChild("body"), "txtNeedChipsBody", null);
                    Buttons(_needChips, "txtNeedChipsYes", "txtNeedChipsCancel");
                }
            }

            _needChipsOption = false;
            _needChipsInputEnabled = false;
            FadeIn(_needChips, () => _needChipsInputEnabled = true);
            Game.Audio.PlaySound("G_fullscreenMenuOpens");
            return;
        }

        if (!_needChipsInputEnabled)
        {
            return;
        }

        bool close = false;
        if (pad.BtnAPressed || Game.AutoPlay)
        {
            _needChipsOption = NeedChipsAllowBank();
            close = true;
        }
        else if (pad.BtnBPressed)
        {
            _needChipsOption = false;
            close = true;
        }

        if (close)
        {
            _needChipsInputEnabled = false;
            FadeOut(_needChips, KillNeedChips);
        }
    }

    /// <summary><c>KillNeedChips</c> (0x009ead-0x009edb): A takes the player to the bank.</summary>
    private void KillNeedChips(IPubClip clip)
    {
        _needChipsShowing = false;
        Remove(clip);
        _bankShowing = _needChipsOption;
    }

    private IPubClip CreateQuickTips()
    {
        IPubClip clip = Attach("overlay_quicktips", "overlay_quicktips", QuickTipsDepth);
        InitQuickTips(clip);
        return clip;
    }

    /// <summary><c>CreateStandings</c>, cash-game branch (0x00a63d-0x00a7f6).</summary>
    private IPubClip CreateStandings()
    {
        IPubClip clip = Attach("overlay_cashgame", "overlay_standings", StandingsDepth);
        Localize(
            clip.GetChild("subtitle"), "txtCashGameSubTitle",
            new Dictionary<string, string>
            {
                ["minBet"] = Game.MinBet.ToString(),
                ["maxBet"] = Game.MaxBet.ToString(),
            });
        Localize(clip.GetChild("playsLabel"), "txtCashGamePlaysLabel", null);
        Localize(clip.GetChild("winningsLabel"), "txtCashGameWinningsLabel", null);
        Localize(clip.GetChild("borrowedLabel"), "txtCashGameBorrowedLabel", null);
        Localize(clip.GetChild("pointsEarnedLabel"), "txtCashGamePointsEarnedLabel", null);
        Localize(
            clip.GetChild("playerName"), "txtCashGamePlayerName",
            new Dictionary<string, string> { ["name"] = Human.Name });
        foreach ((string child, string key) in new[]
        {
            ("starRatingLabel", "txtCashGameStarRatingLabel"),
            ("pointsLabel", "txtCashGamePointsLabel"),
            ("nextLevelLabel", "txtCashGameNextLevelLabel"),
            ("chipsLabel", "txtCashGameChipsLabel"),
            ("goldLabel", "txtCashGameGoldLabel"),
            ("debtLabel", "txtCashGameDebtLabel"),
            ("sessionStats", "txtCashGameSessionStats"),
        })
        {
            Localize(clip.GetChild(child), key, null);
        }

        InitCashGameOverlay(clip);
        return clip;
    }

    /// <summary><c>UpdateStandings</c>, cash-game branch (0x00b3ee-0x00b68c).</summary>
    private void UpdateStandings(IPubClip clip)
    {
        Player human = Human;
        Value(clip, "borrowedValue", "txtCashGameBorrowedValue", "chips", Bank.TotalBorrowed());
        Value(clip, "playsValue", "txtCashGamePlaysValue", "plays", GetPlays());
        Value(clip, "winningsValue", "txtCashGameWinningsValue", "gold", Bank.TotalWinnings());
        Value(
            clip, "pointsEarnedValue", "txtCashGamePointsEarnedValue", "points",
            human.SessionPointsEarned);
        Value(clip, "pointsValue", "txtCashGamePointsValue", "points", human.Points);
        if (human.StarRating == 5)
        {
            SetText(clip.GetChild("nextLevelLabel"), string.Empty);
            SetText(clip.GetChild("nextLevelValue"), string.Empty);
        }
        else if (human.StarRating >= 0 && human.StarRating < Game.StarPoints.Count)
        {
            Value(
                clip, "nextLevelValue", "txtCashGameNextLevelValue", "points",
                Game.StarPoints[human.StarRating] - human.Points);
        }

        Value(clip, "chipsValue", "txtCashGameChipsValue", "chips", human.Chips);
        Value(clip, "goldValue", "txtCashGameGoldValue", "gold", human.Gold);
        Value(clip, "debtValue", "txtCashGameDebtValue", "gold", human.Debt);
        clip.GetChild("starValue")?.GotoAndStop(human.StarRating);
        UpdateCashGameOverlay(clip);
    }

    protected void Value(IPubClip clip, string child, string key, string variable, int amount) =>
        Localize(clip.GetChild(child), key, Vars(variable, amount));

    /// <summary><c>Star</c> (0x00b6c2-0x00bcd6).</summary>
    private void Star(GamePad pad)
    {
        if (_star is null)
        {
            bool table = _newTableUnlocked;
            bool concept = _newConceptArtUnlocked;
            _star = Attach(
                table || concept ? "overlay_alert1" : "overlay_starrating", "overlay_star",
                StarDepth);
            int rating = Human.StarRating;
            foreach (string field in new[] { "body", "body2", "gameName", "gameLimits" })
            {
                SetText(_star.GetChild(field), string.Empty);
            }

            if (table)
            {
                Localize(
                    _star.GetChild(concept ? "body2" : "body"),
                    concept ? "txtTableAndConceptBody" : "txtTableBody", null);
                if (_newTableGameName is not null)
                {
                    Localize(_star.GetChild("gameName"), _newTableGameName, null);
                }

                Localize(
                    _star.GetChild("gameLimits"),
                    _newTableMin == _newTableMax ? "txtGameGalleryCardBuyInText" : "txtGameGalleryCardBetText",
                    _newTableMin == _newTableMax
                        ? new Dictionary<string, string> { ["BUYIN"] = _newTableMin.ToString() }
                        : new Dictionary<string, string>
                        {
                            ["MINIMUM"] = _newTableMin.ToString(),
                            ["MAXIMUM"] = _newTableMax.ToString(),
                        });
            }
            else if (concept)
            {
                Localize(_star.GetChild("body"), "txtConceptArtBody", null);
            }
            else
            {
                Localize(_star.GetChild("body"), "txtStarBody", null);
                Game.Audio.PlaySound(
                    rating == 5 ? "applause05_jackpot"
                    : rating == 4 ? "applause04_large"
                    : "applause03_medium");
            }

            IPubClip? body = _star.GetChild("body");
            if (!table && concept && body is not null)
            {
                body.Y += (body.Height - (body.TextHeight * body.YScale / 100f)) / 2f;
            }

            if (!table && !concept)
            {
                SetAlpha(_star.GetChild("starValue"), 100);
                Localize(_star.GetChild("starRatingLabel"), "txtStarRatingLabel", null);
                _star.GetChild("starValue")?.GotoAndStop(rating);
            }

            Localize(_star.GetChild("button1")?.GetChild("label"), "txtStarConfirm", null);
            _star.GetChild("button1")?.GetChild("button")?.GotoAndStop(BButtonFrame);
            _starInputEnabled = false;
            FadeIn(_star, () => _starInputEnabled = true);
            Game.Audio.PlaySound("G_acheivementEarned");
            return;
        }

        if (_starInputEnabled && (pad.BtnBPressed || Game.AutoPlay))
        {
            _starInputEnabled = false;
            FadeOut(_star, clip =>
            {
                Remove(clip);
                _starShowing = false;
            });
        }
    }

    /// <summary><c>CashOut</c> (0x00bd6f-0x00c361).</summary>
    private void CashOut(GamePad pad)
    {
        if (_cashOut is null)
        {
            Player human = Human;
            _cashOut = Attach("overlay_cashout", "overlay_cashout", CashOutDepth);
            Localize(_cashOut.GetChild("title"), "txtCashOutTitle", null);

            int winnings = Bank.TotalWinnings() + GetChipsInPlayButNotLockedIn();
            if (!(winnings < 0))
            {
                Localize(
                    _cashOut.GetChild("body"),
                    winnings > 0 ? "txtCashOutUpGold" : "txtCashOutEvenGold",
                    Vars("value", winnings));
            }
            else
            {
                Localize(_cashOut.GetChild("body"), "txtCashOutDownGold", Vars("value", -winnings));
            }

            int gold = human.Gold;
            int debt = human.Debt - human.Chips - GetChipsInPlayButNotLockedIn();
            if (debt < 0)
            {
                gold -= debt;
                debt = 0;
            }

            Localize(_cashOut.GetChild("line0"), "txtCashOutGold", Vars("value", gold));
            Localize(_cashOut.GetChild("line1"), "txtCashOutDebt", Vars("value", debt));

            if (human.StarRating == 5)
            {
                SetText(_cashOut.GetChild("PointsText"), string.Empty);
            }
            else if (human.StarRating >= 0 && human.StarRating < Game.StarPoints.Count)
            {
                Localize(
                    _cashOut.GetChild("PointsText"), "txtCashOutPointsToNextLevel",
                    Vars("value", Game.StarPoints[human.StarRating] - human.Points));
            }

            _cashOut.GetChild("StarRating")?.GotoAndStop(human.StarRating);
            Localize(_cashOut.GetChild("button1")?.GetChild("label"), "txtCashOutConfirm", null);
            _cashOut.GetChild("button1")?.GetChild("button")?.GotoAndStop(AButtonFrame);

            int hint = Game.Random.Range(10);
            while (Game.RetailGame && (hint == 0 || hint == 3 || (Game.GameIndex == 0 && hint == 8)))
            {
                hint = Game.Random.Range(10);
            }

            Localize(_cashOut.GetChild("tip"), "txtCashOutHint" + hint, null);
            _cashOutInputEnabled = false;
            FadeIn(_cashOut, () => _cashOutInputEnabled = true);
            Game.Audio.PlaySound("G_fullscreenMenuOpens");
            if (!(human.Chips + GetChipsInPlayButNotLockedIn() < Bank.TotalBorrowed() * 2))
            {
                Game.Audio.PlaySound("applause04_large");
            }

            return;
        }

        if (_cashOutInputEnabled && (pad.BtnAPressed || pad.BtnBPressed))
        {
            _cashOutInputEnabled = false;
            FadeOut(_cashOut, KillCashOut);
        }
    }

    /// <summary><c>KillCashOut</c> (0x00c3a4-0x00c3fa).</summary>
    private void KillCashOut(IPubClip clip)
    {
        _cashOutShowing = false;
        Remove(clip);
        _quitting = true;
        DelayCallback(1, Game.GameOver, true);
    }

    private Player Human => Game.PlayerInfo.GetPlayer(0)
        ?? throw new InvalidOperationException("the overlays need player 0");

    private static int Vertical(GamePad pad)
    {
        int direction = 0;
        if (pad.LeftThumbUp || pad.DPadUp)
        {
            direction -= 1;
        }

        if (pad.LeftThumbDown || pad.DPadDown)
        {
            direction += 1;
        }

        return direction;
    }

    private static int Horizontal(GamePad pad)
    {
        int direction = 0;
        if (pad.LeftThumbLeft || pad.DPadLeft)
        {
            direction -= 1;
        }

        if (pad.LeftThumbRight || pad.DPadRight)
        {
            direction += 1;
        }

        return direction;
    }

    private static int Wrap(int option, int last) =>
        option < 0 ? last : option > last ? 0 : option;

    private static Dictionary<string, string> Vars(string name, int value) =>
        new() { [name] = value.ToString() };

    private static void PlaceThumb(
        IPubClip clip, string thumb, string from, string to, double value, double max)
    {
        IPubClip? moving = clip.GetChild(thumb);
        IPubClip? start = clip.GetChild(from);
        IPubClip? end = clip.GetChild(to);
        if (moving is not null && start is not null && end is not null)
        {
            moving.X = (float)(((end.X - start.X) * value / Math.Max(max, 1)) + start.X);
        }
    }

    private void Buttons(IPubClip clip, string confirm, string cancel)
    {
        Localize(clip.GetChild("button1")?.GetChild("label"), confirm, null);
        Localize(clip.GetChild("button2")?.GetChild("label"), cancel, null);
        clip.GetChild("button1")?.GetChild("button")?.GotoAndStop(AButtonFrame);
        clip.GetChild("button2")?.GetChild("button")?.GotoAndStop(BButtonFrame);
    }

    private static void SetText(IPubClip? clip, string text)
    {
        if (clip is not null)
        {
            clip.Text = text;
        }
    }

    private static void SetAlpha(IPubClip? clip, float alpha)
    {
        if (clip is not null)
        {
            clip.Alpha = alpha;
        }
    }

    private static void SetVisible(IPubClip? clip, bool visible)
    {
        if (clip is not null)
        {
            clip.Visible = visible;
        }
    }

    /// <summary>
    /// <c>overlays.attachMovie</c>. A stage without the export (a harness) gets a clip that
    /// absorbs everything, so the dialog still runs its course instead of re-attaching each frame.
    /// </summary>
    private IPubClip Attach(string export, string name, int depth) =>
        _overlays.AttachMovie(export, name, depth) ?? new DetachedClip(name);

    /// <summary><c>clip.removeMovieClip()</c>, forgetting whichever dialog field held it.</summary>
    private void Remove(IPubClip? clip)
    {
        if (clip is null)
        {
            return;
        }

        clip.RemoveMovieClip();
        if (ReferenceEquals(clip, _bank))
        {
            _bank = null;
        }
        else if (ReferenceEquals(clip, _bankSub))
        {
            _bankSub = null;
        }
        else if (ReferenceEquals(clip, _cashOut))
        {
            _cashOut = null;
        }
        else if (ReferenceEquals(clip, _help))
        {
            _help = null;
        }
        else if (ReferenceEquals(clip, _needChips))
        {
            _needChips = null;
        }
        else if (ReferenceEquals(clip, _quit))
        {
            _quit = null;
        }
        else if (ReferenceEquals(clip, _star))
        {
            _star = null;
        }
        else if (ReferenceEquals(clip, _shimPause))
        {
            _shimPause = null;
        }
        else if (ReferenceEquals(clip, _shimControls))
        {
            _shimControls = null;
        }
        else if (ReferenceEquals(clip, _shimSettings))
        {
            _shimSettings = null;
        }
    }

    protected void Localize(
        IPubClip? clip, string key, IReadOnlyDictionary<string, string>? variables)
    {
        if (clip is not null)
        {
            Game.Localizer.Localize(clip, key, variables);
        }
    }

    private IPubClip? _tutorial;

    private IPubClip? _tutorial2;

    private IPubClip? _current;

    private float? _tutorialBodyY;

    private float _tutorialTitleY;

    public bool TutorialShowing => _current is not null;

    public IPubClip? TutorialClip => _current;

    public void ShowTutorial(
        string? title, string? body, string? button1Frame, string? button1Label,
        string? button2Frame, string? button2Label, bool fade, object? group)
    {
        string wanted = title is null ? "overlay_tutorial2" : "overlay_tutorial";
        IPubClip? card = title is null ? _tutorial2 : _tutorial;
        if (card is null)
        {
            card = _overlays.AttachMovie(
                wanted, wanted, title is null ? Tutorial2Depth : TutorialDepth);
            if (title is null)
            {
                _tutorial2 = card;
            }
            else
            {
                _tutorial = card;
            }
        }

        if (card is null)
        {
            return;
        }

        IPubClip? other = title is null ? _tutorial : _tutorial2;
        if (other is not null)
        {
            other.Visible = false;
        }

        card.Visible = true;
        _current = card;

        IPubClip? bodyClip = card.GetChild("body");
        IPubClip? titleClip = card.GetChild("title");

        if (_tutorialBodyY is null && bodyClip is not null)
        {
            _tutorialBodyY = bodyClip.Y;
            _tutorialTitleY = titleClip?.Y ?? 0;
        }

        if (bodyClip is not null)
        {
            bodyClip.Visible = body is not null;
            if (body is not null)
            {
                Localize(bodyClip, body, null);
                bodyClip.Y = _tutorialBodyY!.Value +
                    ((bodyClip.Height - (bodyClip.TextHeight * bodyClip.YScale / 100)) / 2);
            }
        }

        if (titleClip is not null)
        {
            titleClip.Visible = title is not null;
            if (title is not null)
            {
                Localize(titleClip, title, null);
                titleClip.Y = (bodyClip?.Y ?? 0) - (_tutorialBodyY!.Value - _tutorialTitleY);
            }
        }

        Prompt(card, button1Frame, button1Label, button2Label);
        Prompt2(card, "button2", button2Frame, button2Label);

        var alpha = new AnimateAlpha(
            card.Alpha, 100, card, TutorialFadeTime, 1, AnimateBase.StyleLinear)
        {
            Blocking = fade,
            Group = group,
        };
        Game.CallbackManager.AddCallbackObject(alpha);
    }

    private void Prompt(IPubClip card, string? frame, string? label, string? secondLabel)
    {
        IPubClip? button1 = card.GetChild("button1");
        IPubClip? button2 = card.GetChild("button2");
        IPubClip? button3 = card.GetChild("button3");
        if (button1 is null)
        {
            return;
        }

        if (label is null)
        {
            button1.Visible = false;
            if (button3 is not null)
            {
                button3.Visible = false;
            }

            return;
        }

        bool centred = button2 is not null && secondLabel is null;
        IPubClip? used = centred ? button3 : button1;
        IPubClip? spare = centred ? button1 : button3;

        if (used is not null)
        {
            used.Visible = true;
            if (frame is not null)
            {
                used.GetChild("button")?.GotoAndStop(frame);
            }

            Localize(used.GetChild("label"), label, null);
        }

        if (spare is not null)
        {
            spare.Visible = false;
            spare.GetChild("button")?.GotoAndStop(1);
        }
    }

    private void Prompt2(IPubClip card, string name, string? frame, string? label)
    {
        IPubClip? clip = card.GetChild(name);
        if (clip is null)
        {
            return;
        }

        clip.Visible = label is not null;
        if (label is null)
        {
            return;
        }

        if (frame is not null)
        {
            clip.GetChild("button")?.GotoAndStop(frame);
        }

        Localize(clip.GetChild("label"), label, null);
    }

    public void HideTutorial(bool fade, object? group)
    {
        if (!fade || _current is null || _current.Alpha <= 0)
        {
            return;
        }

        var alpha = new AnimateAlpha(
            _current.Alpha, 0, _current, TutorialFadeTime, 1, AnimateBase.StyleLinear)
        {
            Blocking = true,
            Group = group,
        };
        Game.CallbackManager.AddCallbackObject(alpha);
    }

    /// <summary>A clip for an export the stage does not have; every call is absorbed.</summary>
    private sealed class DetachedClip : IPubClip
    {
        public DetachedClip(string name)
        {
            Name = name;
        }

        public float X { get; set; }

        public float Y { get; set; }

        public float Alpha { get; set; } = 100;

        public float XScale { get; set; } = 100;

        public float YScale { get; set; } = 100;

        public float Rotation { get; set; }

        public bool Visible { get; set; } = true;

        public int CurrentFrame => 1;

        public int TotalFrames => 1;

        public string Text { get; set; } = string.Empty;

        public float Height => 0f;

        public float TextHeight => 0f;

        public string Name { get; }

        public void GotoAndStop(int frame)
        {
        }

        public void GotoAndStop(string label)
        {
        }

        public void GotoAndPlay(int frame)
        {
        }

        public void Play()
        {
        }

        public void Stop()
        {
        }

        public IPubClip? GetChild(string name) => null;

        public IPubClip? AttachMovie(string exportName, string instanceName, int depth) => null;

        public IPubClip CreateEmptyMovieClip(string name, int depth) => new DetachedClip(name);

        public int GetDepth() => 0;

        public void RemoveMovieClip()
        {
        }

        public void SwapDepths(int depth)
        {
        }

        public int GetNextHighestDepth() => 0;
    }
}
