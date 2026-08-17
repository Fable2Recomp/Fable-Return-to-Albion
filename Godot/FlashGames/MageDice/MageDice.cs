using System;
using System.Collections.Generic;

using Refii.FlashGames.Framework;

using Action = Refii.FlashGames.Framework.Action;

namespace Refii.FlashGames.MageDice;

/// <summary>What the game is doing this tick.</summary>
public delegate void MageDiceState(double ticks);

/// <summary>
/// Mage Dice: a six-reel fruit machine with a bonus reel, free spins and a growing multiplier.
/// </summary>
/// <remarks>
/// IT DRIVES ITSELF. Unlike Keystone, whose host owns a phase enum and steps the game through it,
/// Mage Dice registers <see cref="GameLoop"/> with <c>Action.RunUntilTrue</c> in
/// <see cref="PostInitialize"/> and dispatches through <see cref="_activeState"/>, a function
/// pointer it reassigns as it goes. A host only has to poll input and tick the callback manager.
///
/// A 1:1 port of <c>MageDice.as</c>, split across this file and <c>MageDiceDisplay.cs</c> the way
/// <c>Keystone.cs</c> is split — the shipped class is one 1,560-line file.
/// </remarks>
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

    /// <summary>
    /// The live game, for the animation classes that reach for it.
    /// </summary>
    /// <remarks>
    /// STATIC BECAUSE THE SHIPPED CODE IS — <c>MageDice.Game</c>, set in
    /// <see cref="PreInitialize"/>, is how <c>AnimateReel</c> reaches <c>reelSpeedBoost</c> without
    /// being handed the game. Only one pub game runs at a time, which is what makes it safe.
    /// </remarks>
    public static MageDice? Game { get; private set; }

    /// <summary>A reel has moved far enough to be worth a tick of sound.</summary>
    public static bool RequestAudioTick { get; set; }

    /// <summary>The player is holding A to hurry the reels along.</summary>
    public static bool SpeedAnimation { get; set; }

    /// <summary>Win celebrations should fade out — the next spin has started.</summary>
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

    /// <summary>Whether the developer reel picker is reachable. False in every shipped table.</summary>
    public bool AllowDebug { get; set; }

    /// <summary>How much faster the reels run while the player is hurrying them.</summary>
    public double ReelSpeedBoost { get; set; } = 2.5;

    /// <summary>Six min/max pairs of reel stop times, in seconds.</summary>
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

    /// <summary>The three board-type templates a table can be built from.</summary>
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

    /// <summary>
    /// Hands the game this frame's pad.
    /// </summary>
    /// <remarks>
    /// MAGE DICE READS THE PAD ITSELF rather than being passed it per call the way Keystone is, so
    /// the host has to put it somewhere the game can see. <see cref="PubGame.OnEnterFrame"/> would
    /// do this — it is the shipped per-frame entry — but it also calls <c>Input.Update()</c>, and
    /// the host has already done that to read the pad for its own bindings; updating twice in a
    /// frame eats the just-pressed edges the game is waiting on.
    /// </remarks>
    public void SetPad(GamePad pad) => Pad = pad;

    /// <summary>
    /// Pins the reels to chosen symbols, as the shipped developer menu does.
    /// </summary>
    /// <remarks>
    /// THE ONLY WAY TO REACH THE BONUS PATHS DELIBERATELY. A bonus reel needs three of one symbol
    /// and a free-spin run needs a token that most tables do not even carry, so waiting for the RNG
    /// to produce one is not a test. This is <c>debugOverrides</c> and <c>useDebugOverride</c> from
    /// the shipped debug menu, reachable without it.
    /// </remarks>
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

        // THE TEMPLATE IS READ OFF THE STAGE AND THEN THE STAGE CLIP IS DELETED. Every floating
        // win amount is placed with these numbers; leaving the authored clip in place would show a
        // stray label over the board.
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
            // THE TUTORIAL WALKS THROUGH THREE MACHINES, and they are pushed in C-then-B order so
            // its script can step the player from the six-reel board back down.
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
            // EVERY OUTCOME IS SCRIPTED, so the override is on for the whole session and each step
            // writes the reels it wants. STAGE -1 because StateTutorialNext increments before it
            // enters, so the first card shown is 0.
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

    /// <summary>
    /// Builds the machine the player sees, plus one silent machine per tournament opponent.
    /// </summary>
    /// <remarks>
    /// THE OPPONENTS ARE REAL BOARDS, not simulated scores — a tournament rolls every seat against
    /// the same variation and pays each one, so the standings come out of the same rules the player
    /// is playing. They are plain <see cref="GameBoard"/>s because nobody watches them.
    /// </remarks>
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

    // ---------------------------------------------------------------------------------------
    // The state machine
    // ---------------------------------------------------------------------------------------

    /// <summary>One tick. Registered once and never removed.</summary>
    public bool GameLoop(double ticks, object? callbackData, bool blocked)
    {
        HandleGlobalInput(ticks);
        DebugInfo();
        ShowButtons();
        UpdatePlayerChipDisplay(ticks);
        _activeState?.Invoke(ticks);
        return false;
    }

    /// <summary>Does nothing: the board is still fading in.</summary>
    private void StateWaiting(double ticks)
    {
    }

    private void GoToDefaultState()
    {
        PlaySound.Once(Audio, "M_introFlourish", "legend");
        _activeState = _defaultState;
    }

    /// <summary>
    /// Idle at the table, waiting for the player to spin.
    /// </summary>
    /// <remarks>
    /// A FREE SPIN MID-RUN PLAYS ITSELF, but the last one does not — see
    /// <see cref="GameBoard.AutoPlayFreeSpins"/> — so the player is present for the result. Neither
    /// autoplay nor a free-spin run starts while the first payout pass is still lighting up the
    /// board, or the celebration would be cut off by its own reward.
    /// </remarks>
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

    /// <summary>
    /// Everything the game does while nobody is spinning: the shine, the bet, the celebration.
    /// </summary>
    /// <remarks>
    /// THE BET IS READ FROM BOTH STICKS AND THE D-PAD AT ONCE, summed and then reduced to a single
    /// left-or-right by the ANGLE of the result. That is why a diagonal still counts: anything in
    /// the right half circle raises the bet and anything in the left half lowers it.
    ///
    /// THE REPEAT DELAY RESETS ON A DIRECTION CHANGE, so flicking the other way responds at once
    /// rather than waiting out the interval from the previous press.
    /// </remarks>
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

    /// <summary>
    /// The reels are turning.
    /// </summary>
    /// <remarks>
    /// THE RESULT IS ALREADY DECIDED before the first frame — <c>BeginSpin</c> rolls and
    /// <c>Update</c> resolves, then <c>AnimateSpin</c> plays it back. The bonus tiers re-enter this
    /// state twice more, spinning the bonus reel and then everything.
    /// </remarks>
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

    // ---------------------------------------------------------------------------------------
    // Spending and paying
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Takes the stake, or sends the player to the bank.
    /// </summary>
    /// <remarks>
    /// A FREE SPIN COSTS NOTHING AND SETS NOTHING. It does not overwrite <c>lastBet</c>, which the
    /// chip counter uses to pace its ticking, and it earns no points — a spin the player did not
    /// pay for should not move their standing with the pub.
    /// </remarks>
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

    /// <summary>
    /// Settles the spin: pays the player, applauds, and hands the table back.
    /// </summary>
    /// <remarks>
    /// CHIPS ARE ONLY PAID OUT WHEN THE FREE SPINS RUN OUT, and then it is the whole run's
    /// <see cref="GameBoard.RunningPayout"/> at once. Mid-run the applause still fires off this
    /// spin's own payout, so a big hit inside a free run is celebrated when it happens rather than
    /// at the end.
    /// </remarks>
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

    // ---------------------------------------------------------------------------------------
    // Input outside the state machine
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Input the game answers whatever state it is in.
    /// </summary>
    /// <remarks>
    /// HOLDING A DURING A SPIN HURRIES IT. That is the only always-live binding; the rest of this
    /// is the developer menu, which <c>allowDebug</c> keeps off in every shipped table.
    /// </remarks>
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

    public override bool EndGameWarning() => false;

    protected override void CleanUp()
    {
        DisposeBoards();
        Game = null;
        Action.DefaultCallbackManager = null;
    }
}
