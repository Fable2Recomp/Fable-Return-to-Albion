using System;
using System.Collections.Generic;

using Refii.Titles.Fable2.PubGames.Framework;

using Action = Refii.Titles.Fable2.PubGames.Framework.Action;

namespace Refii.Titles.Fable2.PubGames.MageDice;

public delegate void MageDiceState(double ticks);

public sealed partial class MageDice : PubGame
{
    public const double SpinTimeMs = 2000;
    public const double WagerInterval = 250;
    public const double SwitchRevealStagger = 0.6;

    public const int SpinNotBegun = 0;
    public const int SpinBegun = 1;
    public const int SpinBonus1 = 2;
    public const int SpinBonus2 = 3;
    public const int SpinFinished = 4;

    public const int AnimIdle = 0;
    public const int AnimPlaying = 1;
    public const int AnimFinished = 2;

    public const double GlintDelay = 15000;
    public const double QuickTipDelay = 3500;

    public static MageDice? Game { get; private set; }

    public static bool RequestAudioTick { get; set; }

    public static bool SpeedAnimation { get; set; }

    public static bool KillWinAnims { get; set; }

    private readonly IPubClip _root;
    private readonly List<GameVariant> _variants = new();
    private readonly List<GameBoard> _boards = new();
    private readonly List<Player> _players = new();

    private MageDiceState? _activeState;
    private MageDiceState? _defaultState;

    private AnimatingGameBoard _board = null!;
    private Player _player = null!;
    private ChipStackMulti _chipStack = null!;
    private MageDiceWinVars _winVars;

    private int _activeVariant;
    private int _spinState;
    private int _animState;
    private int _totalSpins;
    private bool _thisSpinIsFree;

    private double _glintCounter;
    private double _wagerTimeout;
    private int _lastBetInput;
    private int _lastBet;
    private int _lastChips;
    private int _savedPoints;
    private double _winAnimDelay;

    private int _freeSpinCounter;
    private double _freeSpinSoundDelay;
    private double _ticksBetweenFreeSpinSounds = 200;

    private bool _slowChipCount;
    private bool _slowChipCountSoundOn;
    private double _chipCountTicks;
    private readonly double _chipDingsPerSecond = 5;

    private double _tickAudioTimer;

    private bool _allowWagerAdjustment = true;
    private bool _showDebug;
    private bool _useDebugOverride;
    private int _debugCursor;
    private double _debugUITimer;
    private readonly int[] _debugOverrides = { 0, 0, 0, 0, 0, 0 };

    private bool _spinLegendShowing;
    private bool _freeSpinLegendShowing;
    private bool _adjustBetLegendShowing;
    private bool _buyChipsLegendShowing;
    private bool _triggersLegendShowing;
    private bool _endTourneyLegendShowing;
    private bool _freeSpinsShowing;
    private bool _bonusMultiplierShowing;

    public MageDice(IPubGameHost host, IPubTimer timer, IPubRandom random, IPubClip root)
        : base(host, timer, random)
    {
        _root = root;
    }

    public bool AllowDebug { get; set; }

    public double ReelSpeedBoost { get; set; } = 2.5;

    public List<double> SpinTimes { get; } = new()
    {
        0.8, 1.3, 0.8, 1.3, 0.8, 1.3, 0.8, 1.3, 0.8, 1.3, 0.8, 1.3,
    };

    public double SpinTimeExtension1 { get; set; } = 0.7;

    public double SpinTimeExtension2 { get; set; } = 0.7;

    public int WinThreshold3 { get; set; } = 10;

    public int WinThreshold4 { get; set; } = 30;

    public int WinThreshold5 { get; set; } = 55;

    public double TourneyWinThresholdMultiplier { get; set; } = 1;

    public MageDiceTables TablesA { get; } = new(3);

    public MageDiceTables TablesB { get; } = new(4);

    public MageDiceTables TablesC { get; } = new(6);

    public GameVariant Variant => _variants[_activeVariant];

    public int ActiveVariantIndex => _activeVariant;

    public int TotalSpins => _totalSpins;

    public AnimatingGameBoard Board => _board;

    public GameBoard GetBoard(int index) => _boards[index];

    public Player GetPlayer(int index) => _players[index];

    public IPubClip Root => _root;

    public void SetPad(GamePad pad) => Pad = pad;

    public void ForceReels(IReadOnlyList<int> values)
    {
        for (var i = 0; i < _debugOverrides.Length && i < values.Count; i++)
        {
            _debugOverrides[i] = values[i];
        }

        _useDebugOverride = true;
    }

    protected override void PreInitialize()
    {
        Game = this;
        Action.DefaultCallbackManager = CallbackManager;

        AutoPlay = false;
        _activeVariant = 0;
        _lastBetInput = 0;
        _wagerTimeout = 0;
        _debugCursor = 0;
        _debugUITimer = 0;
        _showDebug = false;
        _useDebugOverride = false;
        _lastChips = 0;
        _glintCounter = 0;
        _allowWagerAdjustment = true;
        _freeSpinCounter = 0;
        _freeSpinSoundDelay = 0;
        _savedPoints = 0;
        _slowChipCount = false;
        _slowChipCountSoundOn = false;
        _chipCountTicks = 0;
        _lastBet = 0;
        _winAnimDelay = 0;

        IPubClip? winAmount = _root.GetChild("winAmount");
        if (winAmount is not null)
        {
            _winVars = new MageDiceWinVars(
                winAmount.X, winAmount.Y, winAmount.XScale, winAmount.YScale);
            winAmount.SwapDepths(_root.GetNextHighestDepth());
            winAmount.RemoveMovieClip();
        }
    }

    protected override void PostInitialize()
    {
        _variants.Add(TablesA.ToVariant(MinBet));
        if (Tutorial)
        {
            _variants.Add(TablesC.ToVariant(MinBet));
            _variants.Add(TablesB.ToVariant(MinBet));
        }

        InitPlayers();

        Localize("betStats", "betLabel", "txtBetLabel");
        Localize("betStats", "winLabel", "txtWinLabel");
        Localize("freeSpins", "label", "txtFreeSpinsLabel");
        Localize("bonusMultiplier", "label", "txtBonusLabel");
        Localize("bonusMultiplier", "thisLabel", "txtBonusLabelThis");
        Localize("bonusMultiplier", "nextLabel", "txtBetLabelNext");

        IPubClip? nameLabel = _root.GetChild("player0name")?.GetChild("label");
        if (nameLabel is not null)
        {
            Localizer.Localize(
                nameLabel, "txtPlayerName",
                new Dictionary<string, string> { ["value"] = _player.Name });
        }

        InitChips();
        AdjustLayers();
        Reset();

        Action.RunUntilTrue(this, GameLoop);

        if (Tutorial)
        {
            _useDebugOverride = true;
            _tutorialStage = -1;
            if (OverlayManager is OverlayManager tutorialOverlays)
            {
                tutorialOverlays.AllowQuickTips = false;
            }

            _defaultState = StateTutorialNext;
        }
        else
        {
            _defaultState = StateWaitForPlayer;
            if (OverlayManager is OverlayManager overlays)
            {
                overlays.ForceQuickTips(QuickTipDelay);
            }
        }

        if (Tournament)
        {
            _player.Rank = 1;
            UpdateTourneySpin();
            UpdateTourneyRank();
            WinThreshold3 = (int)(WinThreshold3 * TourneyWinThresholdMultiplier);
            WinThreshold4 = (int)(WinThreshold4 * TourneyWinThresholdMultiplier);
            WinThreshold5 = (int)(WinThreshold5 * TourneyWinThresholdMultiplier);
        }
        else
        {
            _root.GetChild("tourneyStats")?.RemoveMovieClip();
        }

        _activeState = StateWaiting;
        PreFadeIn();
    }

    private void Localize(string parent, string child, string key)
    {
        IPubClip? clip = _root.GetChild(parent)?.GetChild(child);
        if (clip is not null)
        {
            Localizer.Localize(clip, key, null);
        }
    }

    private void InitPlayers()
    {
        Player? human = PlayerInfo.GetPlayer(0);
        if (human is null)
        {
            AddHumanPlayer(0, "Player_0", 0, 0, 1, 0, 0);
            human = PlayerInfo.GetPlayer(0);
        }

        _player = human!;
        _players.Clear();
        _players.Add(_player);

        if (Tournament)
        {
            for (var i = 0; i < NumberOfAIPlayers; i++)
            {
                Player? ai = PlayerInfo.GetNewUnseatedPlayer(Random);
                if (ai is null)
                {
                    continue;
                }

                ai.IsSeated = true;
                _players.Add(ai);
            }
        }
        else if (Tutorial)
        {
            _player.Chips = 100;
        }

        _lastChips = _player.Chips;
    }

    private void DisposeBoards()
    {
        foreach (GameBoard board in _boards)
        {
            board.Dispose();
        }

        _boards.Clear();
    }

    public void Reset()
    {
        DisposeBoards();

        _board = new AnimatingGameBoard(
            Variant, SkinIndex, SpinTimes, SpinTimeExtension1, SpinTimeExtension2, _winVars,
            _root, Random, Audio, Localizer);
        _board.Init();
        _boards.Add(_board);

        for (var i = 1; i < _players.Count; i++)
        {
            var opponent = new GameBoard(Variant, Random, Audio);
            opponent.Init();
            _boards.Add(opponent);
        }

        _totalSpins = 0;
        _thisSpinIsFree = false;
        _slowChipCount = false;
        UpdateDisplay();
        UpdateChipstack();
        UpdateBetAmount();
        UpdateWinAmount();
        UpdateDebug();
    }

    public bool GameLoop(double ticks, object? callbackData, bool blocked)
    {
        HandleGlobalInput(ticks);
        DebugInfo();
        ShowButtons();
        UpdatePlayerChipDisplay(ticks);
        _activeState?.Invoke(ticks);
        return false;
    }

    private void StateWaiting(double ticks)
    {
    }

    private void GoToDefaultState()
    {
        PlaySound.Once(Audio, "M_introFlourish", "legend");
        _activeState = _defaultState;
    }

    private void StateWaitForPlayer(double ticks)
    {
        bool overlayUp = OverlayManager is OverlayManager o &&
            (o.StandingsShowing || o.QuickTipsShowing);

        bool wants = Pad is { BtnAPressed: true } ||
            ((AutoPlay || _board.AutoPlayFreeSpins()) && !_board.FirstPayoutSequence);

        if (!overlayUp && wants)
        {
            PulseLegend(Legend2);
            if (Tournament && _totalSpins >= TournamentRounds)
            {
                TournamentCompleted();
            }
            else if (!ChargePlayerForSpin())
            {
                AutoPlay = false;
            }
            else
            {
                StartNewSpin();
                RollTournamentBoards();
            }

            return;
        }

        DoIdleTasks(ticks);
    }

    private void ResetGlint() => _glintCounter = GlintDelay;

    private void DoIdleTasks(double ticks)
    {
        var standings = false;
        var quickTips = false;
        var tournamentResult = false;
        if (OverlayManager is OverlayManager o)
        {
            standings = o.StandingsShowing;
            quickTips = o.QuickTipsShowing;
            tournamentResult = o.TournamentResultShowing;
        }

        if (!standings)
        {
            if (!_board.Paid && !quickTips)
            {
                _glintCounter -= ticks;
                if (_glintCounter < 0)
                {
                    _board.AnimateGlint();
                    ResetGlint();
                }
            }

            if (_allowWagerAdjustment && Pad is { } pad)
            {
                int max = MinBet != 0 ? MaxBet / MinBet : 1;

                if (pad.LeftShoulderPressed)
                {
                    _board.ChangeBetMultiple(max, max, true);
                    UpdateBetAmount();
                }

                double x = 0;
                double y = 0;
                if (pad.LeftThumbLeft || pad.LeftThumbRight)
                {
                    x = pad.ThumbLX;
                }

                if (pad.RightThumbLeft || pad.RightThumbRight)
                {
                    x += pad.ThumbRX;
                }

                if (pad.DPadRight)
                {
                    x += 1;
                }
                else if (pad.DPadLeft)
                {
                    x -= 1;
                }

                x = Math.Clamp(x, -1, 1);

                if (pad.LeftThumbUp || pad.LeftThumbDown)
                {
                    y = pad.ThumbLY;
                }

                if (pad.RightThumbUp || pad.RightThumbDown)
                {
                    y += pad.ThumbRY;
                }

                if (pad.DPadUp)
                {
                    y += 1;
                }
                else if (pad.DPadDown)
                {
                    y -= 1;
                }

                y = Math.Clamp(y, -1, 1);

                var direction = 0;
                if (Math.Abs(x) > 0 || Math.Abs(y) > 0)
                {
                    double angle = 180 * Math.Atan2(y, x) / Math.PI;
                    if (angle < 0)
                    {
                        angle += 360;
                    }

                    direction = angle <= 135 || angle > 315 ? 1 : -1;
                }

                if (_wagerTimeout <= 0)
                {
                    if (direction != 0)
                    {
                        if (_board.ChangeBetMultiple(
                                direction <= 0 ? -1 : 1, max, _lastBetInput != direction))
                        {
                            PulseLegend(Legend1);
                        }

                        _wagerTimeout = WagerInterval;
                        UpdateBetAmount();
                    }

                    _lastBetInput = direction;
                }
                else
                {
                    _wagerTimeout -= ticks;
                    if (direction != _lastBetInput)
                    {
                        _wagerTimeout = 0;
                    }
                }
            }
        }

        if (tournamentResult)
        {
            return;
        }

        if (_board.Paid && _animState != AnimPlaying && !KillWinAnims)
        {
            _winAnimDelay -= ticks;
            if (_winAnimDelay < 0)
            {
                _winAnimDelay = 66;
                _board.PlayNextWinAnimation();
                _animState = AnimPlaying;
                Wait.UntilFinished("Payout");
                Action.RunOnce(this, AnimFinishedCallback, "Payout");
            }
        }

        if (_freeSpinCounter > 0)
        {
            _freeSpinSoundDelay -= ticks;
            if (_freeSpinSoundDelay <= 0)
            {
                _freeSpinSoundDelay += _ticksBetweenFreeSpinSounds;
                PlaySound.Once(Audio, "G_countUpDing", null);
                _freeSpinCounter--;
                UpdateDisplay();
            }
        }
    }

    private void StateSpinSwitches(double ticks)
    {
        switch (_spinState)
        {
            case SpinNotBegun:
                if (_useDebugOverride)
                {
                    _board.DebugForceSwitches(_debugOverrides);
                }
                else
                {
                    _board.BeginSpin();
                }

                _board.AnimateSpin();
                Wait.UntilFinished("SwitchAnim");
                Action.RunOnce(this, FinishSpin, "SwitchAnim");
                ResetTickDelayForNewSpin();
                _spinState = SpinBegun;
                break;

            case SpinBegun:
            case SpinBonus1:
            case SpinBonus2:
                PlayTickSound(ticks);
                break;

            case SpinFinished:
                if (_board.BonusUnlocked == 0)
                {
                    KillWinAnims = false;
                    AwardPayout();
                }
                else if (_board.BonusUnlocked == 1)
                {
                    if (_useDebugOverride)
                    {
                        _board.DebugForceBonusSwitch(_debugOverrides[3]);
                    }
                    else
                    {
                        _board.BeginBonusSpin();
                    }

                    _board.AnimateBonusSpin();
                    Wait.UntilFinished("SwitchAnim");
                    Action.RunOnce(this, FinishSpin, "SwitchAnim");
                    ResetTickDelayForNewSpin();
                    _spinState = SpinBonus1;
                }
                else if (_board.BonusUnlocked == 2)
                {
                    if (_useDebugOverride)
                    {
                        _board.DebugForceBonus2Switch(_debugOverrides[4]);
                    }
                    else
                    {
                        _board.BeginBonus2Spin();
                    }

                    _board.AnimateBonus2Spin();
                    Wait.UntilFinished("SwitchAnim");
                    Action.RunOnce(this, FinishSpin, "SwitchAnim");
                    ResetTickDelayForNewSpin();
                    _spinState = SpinBonus2;
                }

                break;
        }
    }

    private void FinishSpin() => _spinState = SpinFinished;

    private void AnimFinishedCallback() => _animState = AnimFinished;

    private bool ChargePlayerForSpin()
    {
        _thisSpinIsFree = _board.FreeSpins > 0;
        if (_thisSpinIsFree)
        {
            return true;
        }

        if (_player.Chips < _board.CostPerSpin())
        {
            PlaySound.Once(Audio, "G_notEnoughMoney", null);
            OverlayManager?.ActivateNeedChipsAlert();
            return false;
        }

        _player.Chips -= _board.CostPerSpin();
        _savedPoints = _board.CostPerSpin();
        _lastBet = _board.CostPerSpin();
        UpdateChipstack();
        return true;
    }

    private void StartNewSpin()
    {
        _totalSpins++;
        _freeSpinCounter = 0;
        _slowChipCount = false;
        _winAnimDelay = 0;

        foreach (GameBoard board in _boards)
        {
            board.NewSpin();
        }

        SpeedAnimation = false;
        KillWinAnims = true;
        _spinState = SpinNotBegun;
        _activeState = StateSpinSwitches;
        UpdateDisplay();
        UpdateWinAmount();
        ResetGlint();
    }

    private void AwardPayout()
    {
        _board.ResolveAndPay();

        int cost = _board.CostPerSpin();
        if (_board.FreeSpins == 0)
        {
            _player.Chips += _board.RunningPayout;
            if (_board.RunningPayout > 0)
            {
                _slowChipCount = true;
            }

            if (!Tutorial && !Tournament && _board.RunningPayout >= 100 * cost)
            {
                AchievementEarned("SB_BigSpinner");
            }

            Applaud(_board.RunningPayout, cost);
        }
        else
        {
            Applaud(_board.Payout, cost);
        }

        PayTournamentBoards();
        _activeState = _defaultState;

        if (_board.BonusMultiplier < _board.NextBonusMultiplier)
        {
            PlaySound.Once(Audio, "M_bonusMultiplierGrowth", null);
        }

        _freeSpinCounter = _board.NewFreeSpins;
        UpdateDisplay();
        UpdateWinAmount();
        UpdateChipstack();

        if (_savedPoints > 0 && !Tournament && !Tutorial)
        {
            PointsEarned(_savedPoints);
            _savedPoints = 0;
        }
    }

    private void Applaud(int amount, int cost)
    {
        if (amount >= WinThreshold5 * cost)
        {
            PlaySound.Once(Audio, "applause05_jackpot", null);
        }
        else if (amount >= WinThreshold4 * cost)
        {
            PlaySound.Once(Audio, "applause04_large", null);
        }
        else if (amount >= WinThreshold3 * cost)
        {
            PlaySound.Once(Audio, "applause03_medium", null);
        }
    }

    private void RollTournamentBoards()
    {
        for (var i = 1; i < _players.Count; i++)
        {
            _players[i].Chips -= MinBet;
            GameBoard board = _boards[i];
            board.BeginSpin();
            if (board.BonusUnlocked == 1)
            {
                board.BeginBonusSpin();
            }

            if (board.BonusUnlocked == 2)
            {
                board.BeginBonus2Spin();
            }
        }

        UpdateTourneySpin();
    }

    private void PayTournamentBoards()
    {
        for (var i = 1; i < _players.Count; i++)
        {
            GameBoard board = _boards[i];
            board.ResolveAndPay();
            _players[i].Chips += board.Payout;
        }

        UpdateTourneyRank();
    }

    private void HandleGlobalInput(double ticks)
    {
        if (Pad is not { } pad)
        {
            return;
        }

        if (pad.BtnAPressed && _activeState == StateSpinSwitches)
        {
            SpeedAnimation = true;
        }

        if (!AllowDebug)
        {
            return;
        }

        if (pad.RightShoulderPressed)
        {
            _showDebug = !_showDebug;
        }

        if (_debugUITimer > 0)
        {
            _debugUITimer -= ticks;
            return;
        }

        if (pad.DPad == 0)
        {
            return;
        }

        var delta = 0;
        if (pad.DPadUp)
        {
            _debugCursor = _debugCursor - 1 < 0 ? 8 : _debugCursor - 1;
        }
        else if (pad.DPadDown)
        {
            _debugCursor = _debugCursor + 1 > 8 ? 0 : _debugCursor + 1;
        }
        else if (pad.DPadLeft)
        {
            delta = -1;
        }
        else if (pad.DPadRight)
        {
            delta = 1;
        }

        if (delta != 0)
        {
            if (_debugCursor < 6)
            {
                int symbols = Variant.ReelImages.Count;
                _debugOverrides[_debugCursor] = (_debugOverrides[_debugCursor] + delta) % symbols;
                if (_debugOverrides[_debugCursor] < 0)
                {
                    _debugOverrides[_debugCursor] = symbols - 1;
                }
            }
            else if (_debugCursor == 6)
            {
                _useDebugOverride = !_useDebugOverride;
            }
            else if (_debugCursor == 7)
            {
                AutoPlay = !AutoPlay;
            }
            else if (_debugCursor == 8)
            {
                _showDebug = !_showDebug;
            }
        }

        UpdateDebug();
        _debugUITimer = 100;
    }

    // AS2 0x007002-0x007049: activeState == StateSpinSwitches || board.FreeSpins > 0. Before the
    // boards exist AS2 reads undefined.FreeSpins, which is not > 0.
    public override bool EndGameWarning() =>
        _activeState == (MageDiceState)StateSpinSwitches ||
        (_board is not null && _board.FreeSpins > 0);

    protected override void CleanUp()
    {
        DisposeBoards();
        Game = null;
        Action.DefaultCallbackManager = null;
    }
}
