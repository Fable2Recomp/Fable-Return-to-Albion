using System;
using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.Keystone;

/// <summary>
/// AS2 <c>Keystone</c>. The table runs as the AS2 does: <see cref="PostInitialize"/> adds
/// <see cref="GameLoop"/> as a callback behind the overlay manager, and every step of a roll is a
/// blocking <see cref="DelayCallback"/> that queues the next one. The human's input is the
/// current pump function (<c>BetOnStones</c>, <c>BetOnInside</c>, <c>WaitForContinue</c>).
/// </summary>
public sealed partial class Keystone : PubGame
{
    public const double MaxCursorWaitTime = 250;
    public const double MinCursorWaitTime = 62.5;
    public const double CursorWaitTimeFactor = 0.5;
    public const double MaxBetWaitTime = 500;
    public const double MinBetWaitTime = 15;
    public const double BetWaitTimeFactor = 0.75;
    public const int MaxPlayers = 4;
    public const int MaxChips = 10000;
    public const double MinAiBetWaitTime = 1000;
    public const double MaxAiBetWaitTime = 4000;
    public const double MaxAiFinishBetsTime = 2500;

    public const int FirstTutorialBlock = 1;
    public const int LastTutorialBlock = 12;
    public const int FirstTutorialStage = 1;
    public const int LastTutorialStage = 3;

    public static readonly int[] NumberOfBetsArch =
        { 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 5, 5, 5, 6, 6, 7 };

    public static readonly int[] NumberOfChipsBetArch =
        { 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 5, 5 };

    public static readonly int[] NumberOfBetsInside =
        { 0, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 5, 5, 6, 6, 8, 8, 10, 10, 12 };

    public static readonly int[] NumberOfChipsBetInside =
        { 1, 1, 2, 2, 2, 3, 3, 4, 4, 4, 5, 5, 5, 5, 6, 6, 8, 8, 10, 10 };

    private readonly int[] _payoutBet = new int[KeystoneSpace.SpaceCount];
    private readonly int[] _maxBets = new int[KeystoneSpace.SpaceCount];
    private readonly int[] _payoutRate = new int[KeystoneSpace.SpaceCount];
    private readonly bool[] _isWinningSpace = new bool[KeystoneSpace.SpaceCount];
    private readonly bool[] _archStonesIntact = new bool[KeystoneSpace.SpaceCount];
    private readonly int[] _lastBets = new int[KeystoneSpace.SpaceCount];
    private readonly List<int> _lastChips = new();
    private readonly List<BoardInfo> _boardInfos = new();
    private readonly Queue<int> _scriptedRolls = new();

    /// <summary><c>seatOpenTime</c> starts as <c>[]</c>: an unset seat reads NaN and never refills.</summary>
    private readonly Dictionary<int, double> _seatOpenTime = new();

    private Action<double>? _pump;

    public Keystone(IPubGameHost host, IPubTimer timer, IPubRandom random)
        : base(host, timer, random)
    {
        Current = KeystoneSpace.Out10;
        NotAvailableInXbla = false;
        CurrentGame = 1;
        CurrentRoll = 1;
        TotalRolls = 0;
        BlockGameInput = false;
        Bloodstone = false;
        AllowScriptedRolls = false;
        JackpotRolled = false;
        MinBet = 10;
        MaxBet = 100;
        MinInsideBet = 5;
        MaxInsideBet = 50;
        AllowJackpot = true;
        Roll1 = 1;
        Roll2 = 2;
        Roll3 = 3;
        RollTotal = 0;
        ArchStoneToRemove = KeystoneSpace.None;
        AnimatingCount = 0;
        ArchPointsLockedIn = false;
        InnerPointsLockedIn = false;
        BetLimitReached = false;
        SavedPoints = 0;
        SeatsPerTable = 1;
        NumBoards = 1;
        MinSeatOpenTicks = 5000;
        MaxSeatOpenTicks = 10000;
        ApplauseThreshold3 = 3;
        ApplauseThreshold4 = 6;
        ChipTravelTime = 500;
        InitPayoutRate();
    }

    /// <summary>The movie's <c>_root</c>; <see cref="PostInitialize"/> builds the board on it.</summary>
    public IPubClip? Root { get; set; }

    public GameBoard? Board { get; private set; }

    public AnimateWinNumbers? WinNumbers { get; private set; }

    /// <summary><c>pumpFunction</c>: the input handler <see cref="GameLoop"/> runs, or null.</summary>
    public Action<double>? PumpFunction => _pump;

    public int Current { get; set; }

    public bool NotAvailableInXbla { get; set; }

    public int CurrentGame { get; private set; }

    public int CurrentRoll { get; private set; }

    public int TotalRolls { get; private set; }

    public bool BlockGameInput { get; set; }

    public bool Bloodstone { get; set; }

    public bool AllowScriptedRolls { get; set; }

    public bool JackpotRolled { get; private set; }

    public int MinInsideBet { get; set; }

    public int MaxInsideBet { get; set; }

    public bool AllowJackpot { get; set; }

    public int Roll1 { get; private set; }

    public int Roll2 { get; private set; }

    public int Roll3 { get; private set; }

    public int RollTotal { get; private set; }

    public int ArchStoneToRemove { get; private set; }

    public bool ArchPointsLockedIn { get; set; }

    public bool InnerPointsLockedIn { get; set; }

    public bool BetLimitReached { get; set; }

    public int SavedPoints { get; set; }

    public int SeatsPerTable { get; set; }

    public int NumBoards { get; set; }

    public double MinSeatOpenTicks { get; set; }

    public double MaxSeatOpenTicks { get; set; }

    public int ApplauseThreshold3 { get; set; }

    public int ApplauseThreshold4 { get; set; }

    public double ChipTravelTime { get; set; }

    public int AnimatingCount { get; private set; }

    public int TotalWins { get; private set; }

    public bool HumanFinishedBetting { get; private set; }

    public IReadOnlyList<BoardInfo> BoardInfos => _boardInfos;

    public IReadOnlyList<bool> IsWinningSpace => _isWinningSpace;

    public IReadOnlyList<bool> ArchStonesIntact => _archStonesIntact;

    public IReadOnlyList<int> LastBets => _lastBets;

    public int ArchBetsValue { get; private set; }

    public int ArchWinsValue { get; set; }

    public int InsideBetsValue { get; private set; }

    public int InsideWinsValue { get; set; }

    public double MoveWaitTime { get; private set; } = MaxCursorWaitTime;

    public double BetWaitTime { get; private set; } = MaxBetWaitTime;

    public int BetMultiplier { get; private set; } = 1;

    private double _ticksTillNextMove;
    private double _ticksTillNextBet;
    private bool _blockIncreaseBet = true;
    private bool _blockDecreaseBet = true;

    public override Player CreatePlayerObject(
        int id, bool isAI, string name, int gold, int debt,
        int starRating, int points, int gamePoints) =>
        new KeystonePlayer(id, isAI, name, gold, debt, starRating, points, gamePoints);

    /// <summary><c>PostInitialize</c> (0x002a55-0x002e44).</summary>
    protected override void PostInitialize()
    {
        IPubClip root = Root ?? throw new InvalidOperationException("Keystone needs its _root");

        AdjustForScreenSize(root);

        IPubClip? mark1 = root.GetChild("player0chips1");
        IPubClip? mark2 = root.GetChild("player0chips2");
        if (mark1 is not null && mark2 is not null)
        {
            WinNumbers = new AnimateWinNumbers((mark1.X + mark2.X) / 2, mark2.Y, ChipTravelTime);
        }

        Board = new GameBoard(this, root, Random);
        Board.Init();

        // `allowDebugMenu` is false in every shipped settings.xml: no HUD, the tick field hidden.
        IPubClip? ticks = root.GetChild("ticks");
        if (ticks is not null)
        {
            ticks.Visible = false;
        }

        SeatsPerTable = Math.Min(1 + NumberOfAIPlayers, Board.MaxPlayersPerBoard);
        _ticksTillNextMove = 0;
        MoveWaitTime = MaxCursorWaitTime;
        _ticksTillNextBet = 0;
        BetWaitTime = MaxBetWaitTime;
        BetMultiplier = 1;
        _blockIncreaseBet = true;
        _blockDecreaseBet = true;
        HumanFinishedBetting = false;
        NumBoards = 1;

        InitBoardInfos();
        InitPlayers();
        Board.HideUnusedPlayerUI(SeatsPerTable);
        ClearBets();
        InitBetLimits();
        ArchBetsValue = 0;
        ArchWinsValue = 0;
        InsideBetsValue = 0;
        InsideWinsValue = 0;
        TotalWins = 0;
        Board.InitBetStats();
        UpdateBetStats();
        AdjustLayers(root);
        Board.InitTourneyStats();

        CallbackManager.AddCallbackFunction((ticks, _, _) => GameLoop(ticks), this);
        PreFadeIn();
    }

    /// <summary>
    /// <c>AdjustForScreenSize</c> (0x002e69-0x003967). retailGame is always true in Fable II
    /// (<c>sub_82EF0528</c>), so the SD/WD relayout under <c>!retailGame</c> (0x002e78) never
    /// runs; the layout markers are dropped.
    /// </summary>
    private static void AdjustForScreenSize(IPubClip root)
    {
        int depth = root.GetNextHighestDepth();
        string[] markers =
        {
            "betStatsWD", "spaceStatsWD", "player0chipsWD", "player1chipsWD", "player2chipsWD",
            "dieSD", "buttonSD", "betStatsSD", "tourneyStatsSD", "spaceStatsSD", "player0chipsSD",
        };

        foreach (string name in markers)
        {
            IPubClip? clip = root.GetChild(name);
            clip?.SwapDepths(depth);
            clip?.RemoveMovieClip();
        }
    }

    /// <summary>
    /// <c>AdjustLayers</c> (0x003b8f-0x003f94): restacks from <c>getNextHighestDepth() + 1</c>;
    /// the tutorial highlight takes the slot above <c>stones</c> (0x003c63-0x003c88).
    /// </summary>
    private void AdjustLayers(IPubClip root)
    {
        int depth = root.GetNextHighestDepth();
        foreach (string name in new[]
                 { "tableText", "fade", "playerShadows", "playerChips", "stonesGone", "stones" })
        {
            root.GetChild(name)?.SwapDepths(++depth);
        }

        if (Tutorial && Board is not null)
        {
            Board.TutorialHilightDepth = ++depth;
        }

        foreach (string name in new[]
                 {
                     "overs", "hilights", "shadows", "diceShadows", "chips", "dice", "numbersAbove",
                     "playerNames", "betStats", "spaceStats", "tourneyStats",
                     "button5", "button4", "button3", "button2", "button1",
                     "notice", "overlays", "debug", "safeArea", "debugHUD",
                 })
        {
            root.GetChild(name)?.SwapDepths(++depth);
        }

        foreach (string name in new[] { "debug", "safeArea", "debugHUD" })
        {
            root.GetChild(name)?.RemoveMovieClip();
        }
    }

    /// <summary><c>GetEarlyExitPayment</c> (0x003987-0x003a33): Bloodstone owes each live arch bet's rate.</summary>
    public int GetEarlyExitPayment()
    {
        int payment = 0;
        KeystonePlayer? human = GetHumanPlayer();
        if (Bloodstone && human is not null)
        {
            for (int space = KeystoneSpace.Out3; space <= KeystoneSpace.Out18; space++)
            {
                if (human.Bets[space] > 0)
                {
                    payment += _payoutRate[space];
                }
            }
        }

        return payment;
    }

    /// <summary><c>GameExiting</c> (0x003a58-0x003aad).</summary>
    protected override void GameExiting()
    {
        KeystonePlayer? human = GetHumanPlayer();
        if (human is null)
        {
            return;
        }

        if (Bloodstone)
        {
            human.Chips -= GetEarlyExitPayment();
        }
        else
        {
            human.Chips += GetChipsNotLockedIn();
        }
    }

    /// <summary><c>GetChipsNotLockedIn</c> (0x003acd-0x003b6a): stakes whose points are not yet locked.</summary>
    public int GetChipsNotLockedIn()
    {
        int chips = 0;
        KeystonePlayer? human = GetHumanPlayer();
        if (!Tutorial && !Tournament && !Bloodstone && human is not null)
        {
            if (!ArchPointsLockedIn)
            {
                chips += human.GetBetAmount(true);
            }

            if (!InnerPointsLockedIn)
            {
                chips += human.GetBetAmount(false);
            }
        }

        return chips;
    }

    /// <summary><c>CleanUp</c> (0x0041a8).</summary>
    protected override void CleanUp() => Board = null;

    /// <summary><c>BankingMenuDispelled</c> (0x00420a-0x0042a6).</summary>
    public override void BankingMenuDispelled()
    {
        ChipStackMulti? stack = Board?.GetPlayerStack(0);
        KeystonePlayer? human = GetHumanPlayer();
        if (stack is null || human is null || stack.Value == human.Chips)
        {
            return;
        }

        PlayChipStackSound(human.Chips - stack.Value);
        stack.Value = human.Chips;
    }

    /// <summary><c>PlayChipStackSound</c> (0x0042d5-0x0043ad).</summary>
    public void PlayChipStackSound(int delta)
    {
        int size = Math.Abs(delta);
        if (size > 5000)
        {
            PlayDelayedSound(0, "chips04BigWin");
        }
        else if (size > 500)
        {
            PlayDelayedSound(0, "chips03Medium");
        }
        else if (size > 50)
        {
            PlayDelayedSound(0, "chips02Several");
        }
        else if (size != 0)
        {
            PlayDelayedSound(0, "chips01Single");
        }
    }

    public override bool EndGameWarning()
    {
        KeystonePlayer? human = GetHumanPlayer();
        if (human is null)
        {
            return false;
        }

        return (ArchPointsLockedIn && human.GetBetAmount(true) > 0) ||
            (InnerPointsLockedIn && human.GetBetAmount(false) > 0);
    }

    /// <summary><c>GetChipsInPlay</c> (0x004470-0x0044b8): the stack plus what is staked, outside Bloodstone.</summary>
    public int GetChipsInPlay(int playerIndex)
    {
        KeystonePlayer? player = GetPlayerByID(playerIndex);
        if (player is null)
        {
            return 0;
        }

        return Bloodstone ? player.Chips : player.Chips + player.GetTotalBetAmount();
    }

    public KeystonePlayer? GetPlayerByID(int id) => PlayerInfo.GetPlayer(id) as KeystonePlayer;

    public KeystonePlayer? GetHumanPlayer() => PlayerInfo.GetPlayer(0) as KeystonePlayer;

    /// <summary><c>InitPlayers</c> (0x004632-0x004753).</summary>
    public void InitPlayers()
    {
        foreach (BoardInfo info in _boardInfos)
        {
            for (int seat = 0; seat < SeatsPerTable; seat++)
            {
                KeystonePlayer? player = info.GetPlayer(seat);
                if (player is null)
                {
                    continue;
                }

                if (!Tournament && !Tutorial && player.IsAI)
                {
                    player.Chips = GenerateStartingChipStackValue();
                }

                if (IsMainBoard(info))
                {
                    Board?.SeatPlayerOnMainBoard(seat, player);
                }

                player.Busted = false;
            }
        }
    }

    /// <summary><c>UnSeatPlayerOnMainBoard</c> (0x004837-0x0048e7).</summary>
    private void UnSeatPlayerOnMainBoard(int seat)
    {
        Board?.UnseatPlayerOnMainBoard(seat);
        _seatOpenTime[seat] =
            (Random.NextDouble() * (MaxSeatOpenTicks - MinSeatOpenTicks)) + MinSeatOpenTicks;
    }

    public int GenerateStartingChipStackValue() =>
        BuyIn + ((int)Math.Floor(BuyIn * 0.1 * Random.NextDouble()) * 20);

    public void SetLastChips(int seat, int value)
    {
        while (_lastChips.Count <= seat)
        {
            _lastChips.Add(0);
        }

        _lastChips[seat] = value;
    }

    /// <summary><c>UpdatePlayerChipDisplays</c> (0x004ae3-0x004c68).</summary>
    public void UpdatePlayerChipDisplays(double ticks)
    {
        if (_boardInfos.Count == 0)
        {
            return;
        }

        BoardInfo info = _boardInfos[0];
        for (int seat = 0; seat < SeatsPerTable; seat++)
        {
            int target = 0;
            if (info.GetPlayer(seat) is not null)
            {
                target = Board?.GetPlayerStack(seat)?.Value ?? 0;
            }

            while (_lastChips.Count <= seat)
            {
                _lastChips.Add(0);
            }

            int delta = target - _lastChips[seat];
            if (delta == 0)
            {
                continue;
            }

            int magnitude = Math.Abs(delta);
            int sign = magnitude / delta;
            int step = (int)Math.Floor(Math.Sqrt(magnitude));
            if (step < 1)
            {
                step = 1;
            }
            else if (step > 15)
            {
                step *= 4;
            }

            _lastChips[seat] += sign * step;
            Board?.UpdatePlayerChipDisplay(seat, _lastChips[seat]);
        }
    }

    /// <summary>
    /// <c>InitBoardInfos</c> (0x004c8c-0x004d89): board 0 seat 0 is the human, every other seat
    /// an unseated NPC from the 83 <c>Run</c> added.
    /// </summary>
    public void InitBoardInfos()
    {
        _boardInfos.Clear();
        for (int board = 0; board < NumBoards; board++)
        {
            var info = new BoardInfo(board);
            _boardInfos.Add(info);
            for (int seat = 0; seat < SeatsPerTable; seat++)
            {
                KeystonePlayer? player = board == 0 && seat == 0
                    ? GetHumanPlayer()
                    : PlayerInfo.GetNewUnseatedPlayer(Random) as KeystonePlayer;
                info.SetPlayer(seat, player);
            }
        }
    }

    public bool IsMainBoard(BoardInfo info) => info.BoardIndex == 0;

    public void PulseBankButton() => Board?.PulseLegend(4);

    public void PulseStandingsButton() => Board?.PulseLegend(5);

    public void PulseQuickTipsButton() => Board?.PulseLegend(1);

    public const double LegendFadeTime = 250;

    public void ShowBetText(bool show) =>
        Board?.ShowLegend(2, "a", "txtIncreaseBet", null, show, LegendFadeTime, false);

    public void ShowContinueGameText(bool show) =>
        Board?.ShowLegend(2, "a", "txtContinueGame", null, show, LegendFadeTime, false);

    public void ShowRollText(bool show) =>
        Board?.ShowLegend(3, "x", "txtRoll", null, show, LegendFadeTime, false);

    public void ShowBankText(bool show) =>
        Board?.ShowLegend(4, "y", "txtBanking", null, show, LegendFadeTime, false);

    public void ShowQuicktipsText(bool show) =>
        Board?.ShowLegend(1, "lt", "txtKeystoneOdds", null, show, LegendFadeTime, false);

    public void ShowStandingsText(bool show) =>
        Board?.ShowLegend(
            5, "rt", Tournament ? "txtStandings" : "txtStats", null, show, LegendFadeTime, false);

    public void ShowInnerBetOption(bool show) =>
        Board?.SwapNoticeText(show, LegendFadeTime, false);

    public void ShowArchBetNotice(bool show)
    {
        if (show)
        {
            Board?.SetNoticeText(
                "txtPlaceArchBet", null,
                "txtPlaceArchBetMore", null,
                "txtPlaceArchBetMove", null,
                "x");
        }

        Board?.ShowNotice(show, 250, false);
    }

    public void PlaySound(string soundName) => Audio.PlaySound(soundName);

    /// <summary><c>PlayDelayedSound</c> (0x004052-0x0040b7): a negative delay plays now.</summary>
    public void PlayDelayedSound(
        double delay, string soundName, object? group = null, bool blocking = false)
    {
        if (delay < 0)
        {
            PlaySound(soundName);
            return;
        }

        DelayCallback(delay, _ => PlaySound(soundName), null, group, blocking);
    }

    public void DelayCallback(
        double delay, PubEndCallback? endCallback, object? userData = null,
        object? group = null, bool blocking = false)
    {
        CallbackManager.AddCallbackObject(
            new Delay(delay, endCallback, userData, group, blocking));
    }

    /// <summary><c>PreFadeIn</c> (0x0052a5-0x0053e8).</summary>
    public void PreFadeIn()
    {
        Board?.InitCenter();
        Board?.InitNotice();
        for (int legend = 1; legend <= 5; legend++)
        {
            Board?.InitLegend(legend);
        }

        Board?.InitSpaceStats();
        if (Tutorial)
        {
            if (Root is not null)
            {
                Root.Y -= 60;
            }

            (OverlayManager as OverlayManager)?.SetOffsets(0, 60);
            TutorialPreFadeIn();
        }

        DelayCallback(1, _ => RevealBoard(), null, null, true);
    }

    /// <summary><c>RevealBoard</c> (0x005406-0x005491).</summary>
    public void RevealBoard()
    {
        if (Tutorial)
        {
            DelayCallback(1, _ => EnterTutorialStage(), null, null, true);
            return;
        }

        ShowQuicktipsText(true);
        ShowStandingsText(true);
        if (!Tournament)
        {
            ShowBankText(true);
        }

        DelayCallback(1, _ => RevealStones(), null, null, true);
    }

    /// <summary><c>RevealStones</c> (0x0054b8-0x00551c).</summary>
    private void RevealStones()
    {
        ResetArchStones();
        Board?.ShowCenter(false, 250, true);
        Board?.RevealStones(null);
        DelayCallback(3000, _ => TransitionToArchBetting(), null, null, true);
    }

    /// <summary><c>ResetArchStones</c> (0x00553d-0x0055a7).</summary>
    public void ResetArchStones()
    {
        CurrentRoll = 1;
        for (int i = KeystoneSpace.Out3; i <= KeystoneSpace.Out18; i++)
        {
            _archStonesIntact[i] = true;
        }
    }

    /// <summary><c>TransitionToArchBetting</c> (0x0055d2-0x00561b): the opening dice leave on game 1 only.</summary>
    private void TransitionToArchBetting()
    {
        if (CurrentGame == 1)
        {
            Board?.RemoveDice(400, null, true);
        }

        DelayCallback(1, _ => StartArchBetting(), null, null, true);
    }

    /// <summary><c>StartArchBetting</c> (0x005640-0x005754).</summary>
    private void StartArchBetting()
    {
        ArchBetsValue = 0;
        ArchWinsValue = 0;
        InsideBetsValue = 0;
        InsideWinsValue = 0;
        ArchPointsLockedIn = false;
        UpdateBetStats();
        ShowBetText(true);
        ShowArchBetNotice(true);
        Current = KeystoneSpace.Out10;
        Board?.ShowFocus(Current, true, true, null, false);
        Board?.ShowSpaceStats(true, null, false);
        StartAiBetting(true);
        _pump = BetOnStones;
        ResetAnimationSpeed();
        _blockIncreaseBet = true;
        _blockDecreaseBet = true;
    }

    /// <summary><c>ShouldChangeBet</c> (0x00577f-0x00595a).</summary>
    public bool ShouldChangeBet(double ticks, int betInput)
    {
        if (betInput == 0)
        {
            _ticksTillNextBet = 0;
            BetMultiplier = 1;
            BetWaitTime = MaxBetWaitTime;
            BetLimitReached = false;
            _blockIncreaseBet = false;
            _blockDecreaseBet = false;
            return false;
        }

        if (betInput > 0)
        {
            _blockDecreaseBet = true;
            if (_blockIncreaseBet)
            {
                return false;
            }
        }
        else if (_blockDecreaseBet)
        {
            return false;
        }

        if (_ticksTillNextBet > 0)
        {
            _ticksTillNextBet -= ticks;
            return false;
        }

        _ticksTillNextBet = BetWaitTime;
        BetWaitTime *= BetWaitTimeFactor;
        if (BetWaitTime < MinBetWaitTime)
        {
            BetWaitTime = MinBetWaitTime;
            BetMultiplier++;
        }

        return true;
    }

    /// <summary>Right stick up/right (angle &lt;= 135 or &gt; 315) +1, else -1; A +1, B -1 (0x005c48-0x005e3f).</summary>
    public static int BetInputFromPad(GamePad? pad)
    {
        if (pad is null)
        {
            return 0;
        }

        double x = pad.RightThumbLeft || pad.RightThumbRight ? pad.ThumbRX : 0;
        double y = pad.RightThumbUp || pad.RightThumbDown ? pad.ThumbRY : 0;

        int input = 0;
        if (Math.Abs(x) > 0 || Math.Abs(y) > 0)
        {
            double angle = KeystoneRules.AtanDegrees(y, x);
            if (angle < 0)
            {
                angle += 360;
            }

            input = angle <= 135 || angle > 315 ? 1 : -1;
        }

        if (pad.BtnA)
        {
            input += 1;
        }

        if (pad.BtnB)
        {
            input -= 1;
        }

        return input;
    }

    /// <summary>
    /// <c>Bet</c> (0x00598d-0x006330): the human's betting frame. True when the human is done —
    /// X with a bet on the arch, or X at any time inside.
    /// </summary>
    public bool Bet(bool arch, double ticks)
    {
        KeystonePlayer? human = GetHumanPlayer();
        if (_boardInfos.Count > 0)
        {
            DoAiBets(_boardInfos[0], ticks, arch);
        }

        if (human is null)
        {
            return false;
        }

        if (human.IsAI)
        {
            return human.BetsToMake <= 0;
        }

        GamePad? pad = Pad;
        if (BlockGameInput || pad is null)
        {
            return false;
        }

        int before = Current;
        Navigate(pad, ticks, null);
        if (before != Current)
        {
            _blockIncreaseBet = true;
            _blockDecreaseBet = true;
            BetLimitReached = false;
        }

        if (pad.BtnXPressed && (!arch || human.GetBetCount(arch) > 0))
        {
            if (arch)
            {
                Board?.PulseNoticeButton();
            }
            else
            {
                Board?.PulseLegend(3);
            }

            return true;
        }

        if (pad.RightShoulderPressed)
        {
            int had = human.GetBetCount(arch);
            RePlaceLastBets(arch);
            if (arch && had <= 0 && human.GetBetCount(arch) > 0)
            {
                ShowInnerBetOption(true);
            }

            return false;
        }

        int betInput = BetInputFromPad(pad);
        int held = human.Bets[Current];
        int amount = 0;

        if (pad.LeftShoulderPressed)
        {
            if (held < _maxBets[Current])
            {
                amount = _maxBets[Current];
            }
            else
            {
                PlayDelayedSound(0, "G_limitReached");
            }
        }
        else if (ShouldChangeBet(ticks, betInput))
        {
            amount = BetMultiplier * _payoutBet[Current];
            if (betInput < 0)
            {
                amount = -amount;
                if (held <= 0 && !BetLimitReached)
                {
                    PlayDelayedSound(0, "G_limitReached");
                    BetLimitReached = true;
                    amount = 0;
                    if (pad.BtnA)
                    {
                        Board?.PulseLegend(2);
                    }
                }
            }
            else if (held >= _maxBets[Current] && !BetLimitReached)
            {
                PlayDelayedSound(0, "G_limitReached");
                BetLimitReached = true;
                amount = 0;
                if (pad.BtnA)
                {
                    Board?.PulseLegend(2);
                }
            }
        }

        if (amount == 0)
        {
            return false;
        }

        if (amount > _maxBets[Current] - held)
        {
            amount = _maxBets[Current] - held;
        }

        if ((!Bloodstone && amount > human.Chips) ||
            (Bloodstone && amount > 0 && IsBloodstoneBetTooHigh(Current, arch)))
        {
            if (pad.BtnA)
            {
                Board?.PulseLegend(2);
            }

            OverlayManager?.ActivateNeedChipsAlert();
            return false;
        }

        if (amount >= 0 && amount < _payoutBet[Current])
        {
            return false;
        }

        int betsBefore = human.GetBetCount(arch);
        if (!ChangeBet(amount, human, Current, true, null))
        {
            return false;
        }

        if (pad.BtnA)
        {
            Board?.PulseLegend(2);
        }

        UpdateBetStatsRecount(arch);
        Board?.UpdateSpaceStatsBet(Current);
        if (arch)
        {
            int betsAfter = human.GetBetCount(arch);
            if (betsBefore > 0 && betsAfter <= 0)
            {
                ShowInnerBetOption(false);
            }
            else if (betsBefore <= 0 && betsAfter > 0)
            {
                ShowInnerBetOption(true);
            }
        }

        return false;
    }

    public bool IsBloodstoneBetTooHigh(int id, bool arch)
    {
        KeystonePlayer? human = GetHumanPlayer();
        if (human is null)
        {
            return false;
        }

        human.Bets[id] += _payoutBet[id];
        int exposure = BloodstoneExposure(human, true);
        if (!arch)
        {
            exposure += BloodstoneExposure(human, false);
        }

        human.Bets[id] -= _payoutBet[id];
        return exposure > human.Chips;
    }

    /// <summary><c>SavePointsEarned</c> (0x006424-0x006585).</summary>
    public void SavePointsEarned()
    {
        if (Tutorial || Tournament)
        {
            return;
        }

        KeystonePlayer? human = GetHumanPlayer();
        if (human is null)
        {
            return;
        }

        int first = KeystoneSpace.FirstInnerSpace;
        int last = KeystoneSpace.LastInnerSpace;
        if (!ArchPointsLockedIn)
        {
            first = KeystoneSpace.FirstBetSpace;
            last = KeystoneSpace.LastBetSpace;
            ArchPointsLockedIn = true;
        }

        InnerPointsLockedIn = true;
        SavedPoints = 0;
        for (int space = first; space <= last; space++)
        {
            int bet = human.Bets[space];
            if (bet > 0)
            {
                SavedPoints += (int)Math.Floor(
                    (double)bet / _payoutBet[space] *
                    Math.Min(_payoutBet[space], _payoutRate[space]));
            }
        }
    }

    /// <summary><c>ReportPointsEarned</c> (0x0065b4-0x0065e0).</summary>
    public void ReportPointsEarned()
    {
        if (SavedPoints > 0)
        {
            PointsEarned(SavedPoints);
            SavedPoints = 0;
        }
    }

    /// <summary><c>BetOnStones</c> (0x006615-0x0067a7).</summary>
    private void BetOnStones(double ticks)
    {
        if (!Bet(true, ticks))
        {
            return;
        }

        KeystonePlayer human = GetHumanPlayer()!;
        if (Bloodstone && BloodstoneExposure(human, true) > human.Chips)
        {
            OverlayManager?.ActivateNeedChipsAlert();
            return;
        }

        _pump = null;
        SaveLastBets(true);
        ShowBetText(false);
        ShowRollText(false);
        ShowArchBetNotice(false);
        Board?.ShowFocus(Current, false, true, null, false);
        Board?.ShowSpaceStats(false, null, false);
        FinishAiBets(_boardInfos[0], true, true);
        AddAnimationWait(null);
        DelayCallback(1, _ => RevealInsideBets(), null, null, true);
    }

    /// <summary><c>RevealInsideBets</c> (0x0067e1-0x006801).</summary>
    private void RevealInsideBets()
    {
        Board?.ShowCenter(true, 250, true);
        DelayCallback(200, _ => CompleteAiArchBets(), null, null, true);
    }

    /// <summary><c>CompleteAiArchBets</c> (0x00682b-0x00684b); <c>SimulateBets</c> only reaches tournament tables.</summary>
    private void CompleteAiArchBets() =>
        DelayCallback(1, _ => StartInsideBetting(), null, null, true);

    /// <summary><c>StartInsideBetting</c> (0x00687f-0x0069a3).</summary>
    private void StartInsideBetting()
    {
        PlayDelayedSound(0, "K_startInsideBetting");
        InsideBetsValue = 0;
        InsideWinsValue = 0;
        InnerPointsLockedIn = false;
        if (Bloodstone)
        {
            UpdateBetStatsRecount(true);
        }

        UpdateBetStatsRecount(false);
        ShowBetText(true);
        ShowRollText(true);
        Current = KeystoneSpace.Keystone;
        Board?.ShowFocus(Current, true, true, null, false);
        Board?.ShowSpaceStats(true, null, false);
        StartAiBetting(false);
        _pump = BetOnInside;
        _blockIncreaseBet = true;
        _blockDecreaseBet = true;
        ResetAnimationSpeed();
    }

    /// <summary><c>BetOnInside</c> (0x0069d8-0x006b7f).</summary>
    private void BetOnInside(double ticks)
    {
        if (!Bet(false, ticks))
        {
            return;
        }

        KeystonePlayer human = GetHumanPlayer()!;
        if (Bloodstone &&
            BloodstoneExposure(human, false) + BloodstoneExposure(human, true) > human.Chips)
        {
            OverlayManager?.ActivateNeedChipsAlert();
            return;
        }

        _pump = null;
        SaveLastBets(false);
        SavePointsEarned();
        ShowBetText(false);
        ShowRollText(false);
        Board?.ShowFocus(Current, false, true, null, false);
        Board?.ShowSpaceStats(false, null, false);
        FinishAiBets(_boardInfos[0], false, true);
        AddAnimationWait(null);
        DelayCallback(1, _ => CompleteInnerBets(), null, null, true);
    }

    /// <summary><c>CompleteInnerBets</c> (0x006ba9-0x006c1d).</summary>
    private void CompleteInnerBets()
    {
        DelayCallback(1, _ => GenerateRoll(), null, null, true);
        DelayCallback(1, info => GenerateAndResolvePayouts((BoardInfo)info!), _boardInfos[0], null, true);
        DelayCallback(1, _ => AnimateRoll(), null, null, true);
    }

    public void PushScriptedRoll(int roll1, int roll2, int roll3)
    {
        _scriptedRolls.Enqueue(roll1);
        _scriptedRolls.Enqueue(roll2);
        _scriptedRolls.Enqueue(roll3);
    }

    /// <summary><c>GenerateRoll</c> (0x006c43-0x006e69); the debug HUD's forced roll is not shipped.</summary>
    public void GenerateRoll()
    {
        Roll1 = Random.Range(6) + 1;
        Roll2 = Random.Range(6) + 1;
        Roll3 = Random.Range(6) + 1;
        JackpotRolled = false;

        if (AllowScriptedRolls && _scriptedRolls.Count >= 3)
        {
            Roll1 = _scriptedRolls.Dequeue();
            Roll2 = _scriptedRolls.Dequeue();
            Roll3 = _scriptedRolls.Dequeue();
        }

        RollTotal = Roll1 + Roll2 + Roll3;
        FindWinningSpaces(true);
    }

    /// <summary>The roll <c>TutorialStartRoll</c> states (0x00cffb-0x00d038); the caller finds the winners.</summary>
    public void SetRoll(int roll1, int roll2, int roll3)
    {
        Roll1 = roll1;
        Roll2 = roll2;
        Roll3 = roll3;
        RollTotal = roll1 + roll2 + roll3;
    }

    /// <summary><c>AnimateRoll</c> (0x006ebb-0x006f34).</summary>
    private void AnimateRoll()
    {
        Board?.RollDice(Roll1, Roll2, Roll3, false, null);
        DelayCallback(3500, _ => PresentDice(), null, null, true);
    }

    /// <summary><c>PresentDice</c> (0x007009-0x007056).</summary>
    private void PresentDice()
    {
        Board?.ShowNumberAbove(RollTotal - 3 + KeystoneSpace.Out3, true, null, true, 0);
        double time = Board?.PresentDice(500, null) ?? 0;
        DelayCallback(1000 + time, _ => RollResults(), null, null, true);
    }

    /// <summary><c>RollResults</c> (0x00707a-0x0070cd).</summary>
    private void RollResults()
    {
        HilightWinningSpaces(null);
        if (Bloodstone)
        {
            DelayCallback(1, _ => StartPayouts(), null, null, true);
        }
        else
        {
            DelayCallback(1, _ => StartSweep(), null, null, true);
        }
    }

    /// <summary><c>StartSweep</c> (0x0070f3-0x007131): inside losers only; arch losers go at game over.</summary>
    private void StartSweep()
    {
        AnimatingCount = AnimateLostBetRemoval(false, null);
        AddAnimationWait(null);
        DelayCallback(1, _ => StartPayouts(), null, null, true);
    }

    /// <summary><c>StartPayouts</c> (0x00714a-0x0072ac).</summary>
    private void StartPayouts()
    {
        AnimatingCount = ShowPayoutStacks(null);
        AddAnimationWait(null);
        DelayCallback(1, _ => UpdateBetStats(), null, null, false);
        if (!Bloodstone)
        {
            KeystonePlayer human = GetHumanPlayer()!;
            if (human.InsideWins > InsideBetsValue * ApplauseThreshold4)
            {
                PlayDelayedSound(1, "applause04_large");
            }
            else if (human.InsideWins > InsideBetsValue * ApplauseThreshold3)
            {
                PlayDelayedSound(1, "applause03_medium");
            }
        }

        ShowContinueGameText(true);
        DelayCallback(1, _ => WaitForEndOfPayout(), null, null, true);
    }

    /// <summary><c>WaitForEndOfPayout</c> (0x0072d3-0x0072f4).</summary>
    private void WaitForEndOfPayout()
    {
        ReportPointsEarned();
        _pump = WaitForContinue;
        ResetAnimationSpeed();
    }

    /// <summary><c>WaitForContinue</c> (0x007321-0x0073b7).</summary>
    private void WaitForContinue(double ticks)
    {
        KeystonePlayer? human = GetHumanPlayer();
        if (BlockGameInput || !(human?.IsAI == true || Pad?.BtnAPressed == true))
        {
            return;
        }

        Board?.PulseLegend(2);
        ShowContinueGameText(false);
        _pump = null;
        EndPayouts();
    }

    /// <summary><c>EndPayouts</c> (0x0073db-0x007439).</summary>
    private void EndPayouts()
    {
        AnimatingCount = MoveWinsToPlayers(null);
        AddAnimationWait(null);
        DelayCallback(1, _ => UnHilightWinningSpaces(null), null, null, true);
        DelayCallback(1, _ => ResolveOtherTables(), null, null, true);
    }

    /// <summary><c>ResolveOtherTables</c> (0x007525).</summary>
    private void ResolveOtherTables() =>
        DelayCallback(1, _ => RemoveStone(), null, null, true);

    public void RemoveArchStone()
    {
        if (ArchStoneToRemove != KeystoneSpace.None)
        {
            _archStonesIntact[ArchStoneToRemove] = false;
        }
    }

    /// <summary><c>RemoveStone</c> (0x007589-0x0075bc).</summary>
    private void RemoveStone()
    {
        Board?.RemoveStone(ArchStoneToRemove, null);
        RemoveArchStone();
        DelayCallback(1000, _ => EndBettingRound(), null, null, true);
    }

    /// <summary>
    /// <c>IsGameOver</c> (0x0075ed-0x007654): either end stone, or both keystones.
    /// </summary>
    public bool IsGameOver() =>
        !_archStonesIntact[KeystoneSpace.Out3] ||
        !_archStonesIntact[KeystoneSpace.Out18] ||
        (!_archStonesIntact[KeystoneSpace.Out10] && !_archStonesIntact[KeystoneSpace.Out11]);

    /// <summary><c>EndBettingRound</c> (0x007973-0x0079cb); the other-table resolve is tournament-only.</summary>
    private void EndBettingRound() =>
        DelayCallback(1, _ => CheckForGameOver(), null, null, true);

    /// <summary>
    /// <c>CheckForGameOver</c> (0x007b82-0x007c8d): a collapse sweeps the arch losers and starts
    /// a new game; any other roll goes back to INSIDE betting with the arch bets still riding.
    /// </summary>
    public void CheckForGameOver()
    {
        if (IsGameOver())
        {
            AnimatingCount = AnimateLostBetRemoval(true, null);
            AddAnimationWait(null);
            DelayCallback(1, _ => FinishGameOtherTables(), null, null, true);
            return;
        }

        CurrentRoll++;
        TotalRolls++;
        DelayCallback(1, _ => TransitionBackToInsideBetting(), null, null, true);
    }

    /// <summary><c>FinishGameOtherTables</c> (0x007cab-0x007ce2).</summary>
    private void FinishGameOtherTables() =>
        DelayCallback(1, _ => CollapseArch(), null, null, true);

    /// <summary><c>TransitionBackToInsideBetting</c> (0x007d1b-0x007d3b).</summary>
    private void TransitionBackToInsideBetting()
    {
        Board?.RemoveDice(400, null, true);
        DelayCallback(1, _ => StartInsideBetting(), null, null, true);
    }

    /// <summary><c>CollapseArch</c> (0x007d74-0x007db2).</summary>
    private void CollapseArch()
    {
        Board?.RemoveDice(400, null, true);
        Board?.CollapseArch(null);
        DelayCallback(3000, _ => StartNextGame(), null, null, true);
    }

    /// <summary><c>StartNextGame</c> (0x007ea8-0x007ed6), the cash-table branch.</summary>
    private void StartNextGame()
    {
        CurrentGame++;
        DelayCallback(1, _ => RevealStones(), null, null, true);
    }

    public int GenerateAiBetCount(bool arch)
    {
        int[] table = arch ? NumberOfBetsArch : NumberOfBetsInside;
        return table[Random.Range(table.Length)];
    }

    public double GenerateAiBetWaitTime() =>
        Math.Floor(Random.NextDouble() * (MaxAiBetWaitTime - MinAiBetWaitTime + 1)) +
        MinAiBetWaitTime;

    /// <summary><c>StartAiBetting</c> (0x007efa-0x008022).</summary>
    public void StartAiBetting(bool arch)
    {
        HumanFinishedBetting = false;
        foreach (BoardInfo info in _boardInfos)
        {
            for (int seat = 0; seat < SeatsPerTable; seat++)
            {
                KeystonePlayer? player = info.GetPlayer(seat);
                if (player is null)
                {
                    continue;
                }

                player.BetsToMake = player.Busted ? 0 : GenerateAiBetCount(arch);
                player.BetWaitTime = player.Busted ? 0 : GenerateAiBetWaitTime();
            }
        }
    }

    public static AnimateValue SetChipStackValue(
        double startValue, double endValue, double time, double delay,
        PubEndCallback? endCallback, object? userData, IAnimValue stack, object? group)
    {
        var animation = new AnimateValue(
            startValue, endValue, stack, time, delay, AnimateBase.StyleLinear);
        animation.SetEndCallback(endCallback, userData);
        animation.Group = group;
        return animation;
    }

    /// <summary><c>MakeAiBet</c> (0x0082b7-0x00882f); returns the animations it started.</summary>
    public int MakeAiBet(
        BoardInfo info, KeystonePlayer player, int seat, bool arch, bool showAnims, double maxTime)
    {
        int[] bets = player.Bets;
        int chips = player.Chips;
        int first = arch ? KeystoneSpace.Out3 : KeystoneSpace.FirstInnerSpace;
        int last = arch ? KeystoneSpace.Out18 : KeystoneSpace.LastInnerSpace;

        var candidates = new List<int>();
        for (int space = first; space <= last; space++)
        {
            if ((Bloodstone || chips >= _payoutBet[space]) && bets[space] == 0 &&
                IsRoomForAiBet(info, space, seat))
            {
                candidates.Add(space);
            }
        }

        if (candidates.Count == 0)
        {
            return 0;
        }

        int chosen = candidates[(int)Math.Floor(Random.NextDouble() * candidates.Count)];
        int amount = _payoutBet[chosen];

        if (!Bloodstone)
        {
            if (Tournament)
            {
                int[] table = arch ? NumberOfChipsBetArch : NumberOfChipsBetInside;
                amount = _payoutBet[chosen] *
                    table[(int)Math.Floor(Random.NextDouble() * table.Length)];
                if (amount > _maxBets[chosen])
                {
                    amount = _maxBets[chosen];
                }
            }
            else
            {
                amount = _payoutBet[chosen] * (1 + (int)Math.Floor(
                    Random.NextDouble() * ((double)_maxBets[chosen] / _payoutBet[chosen])));
            }

            if (amount > chips)
            {
                amount = chips / _payoutBet[chosen] * _payoutBet[chosen];
            }
        }

        if (amount <= 0)
        {
            return 0;
        }

        bets[chosen] = amount;
        if (!Bloodstone)
        {
            player.Chips -= amount;
        }

        info.AiBettorsOnSpace[chosen]++;

        ChipStack? stack = showAnims && IsMainBoard(info) ? Board?.GetChipStack(seat, chosen) : null;
        if (stack is null)
        {
            return 0;
        }

        double third = maxTime / 3;
        double delay = third > 0
            ? (Random.NextDouble() * third) + (Random.NextDouble() * third) +
              (Random.NextDouble() * third)
            : 0;

        CallbackManager.AddCallbackObject(
            SetChipStackValue(stack.Value, amount, 1, delay, null, null, stack, null));

        ChipStackMulti? seatStack = Board?.GetPlayerStack(seat);
        int change = Bloodstone ? 0 : -amount;
        DelayCallback(delay, _ => AddToPlayerStack(seatStack, change));
        PlayDelayedSound(delay, "K_makeAiBet");
        return 1;
    }

    /// <summary><c>AddToPlayerStack</c> (0x008865-0x0088a2).</summary>
    private void AddToPlayerStack(ChipStackMulti? stack, int value)
    {
        if (value != 0 && stack is not null)
        {
            stack.Value += value;
        }

        DecrementAnimatingCount();
    }

    /// <summary><c>FinishAiBets</c> (0x008988-0x008a6c): the human is done, so seats may share its space.</summary>
    public void FinishAiBets(BoardInfo info, bool arch, bool showAnims)
    {
        HumanFinishedBetting = true;
        for (int seat = 0; seat < SeatsPerTable; seat++)
        {
            KeystonePlayer? player = info.GetPlayer(seat);
            if (player is null || !player.IsAI)
            {
                continue;
            }

            while (player.BetsToMake > 0)
            {
                player.BetsToMake--;
                AnimatingCount += MakeAiBet(info, player, seat, arch, showAnims, MaxAiFinishBetsTime);
            }
        }
    }

    /// <summary>
    /// <c>DoAiBets</c> (0x008aa4-0x008ecf). A seat an NPC left is refilled during arch betting
    /// once its <c>seatOpenTime</c> runs out (0x008cd8-0x008ebf).
    /// </summary>
    public void DoAiBets(BoardInfo info, double ticks, bool arch)
    {
        for (int seat = 0; seat < SeatsPerTable; seat++)
        {
            KeystonePlayer? player = info.GetPlayer(seat);
            if (player is null)
            {
                if (!Tournament && arch)
                {
                    RefillSeat(info, seat, ticks, arch);
                }

                continue;
            }

            if (!player.IsAI || player.BetsToMake <= 0)
            {
                continue;
            }

            player.BetWaitTime -= ticks;
            if (player.BetWaitTime >= 0)
            {
                continue;
            }

            int minimum = arch ? MinBet : MinInsideBet;
            if (player.Chips < minimum)
            {
                player.BetsToMake = 0;
                if (player.ID != 0 && player.GetTotalBetAmount() <= 0 && !Tournament)
                {
                    info.SetPlayer(seat, null);
                    if (IsMainBoard(info))
                    {
                        UnSeatPlayerOnMainBoard(seat);
                        player.JustUnseated = true;
                    }
                }

                continue;
            }

            AnimatingCount += MakeAiBet(info, player, seat, arch, true, 0);
            player.BetsToMake--;
            player.BetWaitTime = GenerateAiBetWaitTime();
        }
    }

    private void RefillSeat(BoardInfo info, int seat, double ticks, bool arch)
    {
        double open = (_seatOpenTime.TryGetValue(seat, out double left) ? left : double.NaN) - ticks;
        _seatOpenTime[seat] = open;
        if (!(open < 0))
        {
            return;
        }

        int count = PlayerInfo.NumPlayers;
        int id = (int)Math.Floor(Random.NextDouble() * count);
        int start = id;
        do
        {
            KeystonePlayer? candidate = GetPlayerByID(id);
            if (candidate is not null && !candidate.IsSeated)
            {
                if (candidate.JustUnseated)
                {
                    candidate.JustUnseated = false;
                }
                else
                {
                    candidate.Chips = GenerateStartingChipStackValue();
                    info.SetPlayer(seat, candidate);
                    candidate.BetsToMake = GenerateAiBetCount(arch);
                    candidate.BetWaitTime = GenerateAiBetWaitTime();
                    candidate.ClearBets();
                    if (IsMainBoard(info))
                    {
                        Board?.SeatPlayerOnMainBoard(seat, candidate);
                    }

                    return;
                }
            }

            id++;
            if (id >= count)
            {
                id = 0;
            }
        }
        while (id != start);
    }

    /// <summary><c>AddAnimationWait</c> (0x008eff-0x008f73): blocks until <see cref="AnimatingCount"/> is spent.</summary>
    private void AddAnimationWait(object? group)
    {
        CallbackManager.AddCallbackObject(new Callback
        {
            CallbackMethod = (_, _, _) => AnimatingCount <= 0,
            Blocking = true,
            Group = group,
        });
    }

    /// <summary><c>ResetAnimationSpeed</c> (0x008fd8).</summary>
    public void ResetAnimationSpeed() => CallbackManager.Speed = 1;

    private void DecrementAnimatingCount() => AnimatingCount--;

    private bool AnimatePoof(double dt, object? target, object? animData, bool done)
    {
        if (target is not IPubClip clip)
        {
            return done;
        }

        if (done)
        {
            clip.Visible = false;
            clip.GotoAndStop(1);
            return true;
        }

        clip.Visible = true;
        int frame = (int)Math.Floor(dt * clip.TotalFrames);
        if (frame >= clip.TotalFrames)
        {
            frame--;
        }

        clip.GotoAndStop(frame + 1);
        return false;
    }

    /// <summary><c>AnimateLostBetRemoval</c> (0x009051-0x00951e).</summary>
    public int AnimateLostBetRemoval(bool arch, object? group)
    {
        var losers = new List<ChipStack>();
        foreach (Payout payout in _boardInfos[0].Payouts)
        {
            if (payout.Winnings == 0 && payout.Arch == arch && payout.Stack is not null)
            {
                losers.Add(payout.Stack);
            }
        }

        int animations = losers.Count;
        const double spread = 400;
        const double fade = 200;

        while (losers.Count > 0)
        {
            int index = (int)Math.Floor(Random.NextDouble() * losers.Count);
            ChipStack stack = losers[index];

            double delay = (Random.NextDouble() * spread) + (Random.NextDouble() * spread) +
                (Random.NextDouble() * spread);

            Add(new AnimateAlpha(100, 0, stack.Stack, fade, delay, AnimateBase.StyleLinear), group);
            if (stack.Shadow is not null)
            {
                Add(new AnimateAlpha(
                    100, 0, stack.Shadow, fade, delay, AnimateBase.StyleLinear), group);
            }

            Add(SetChipStackValue(
                0, 0, 1, delay + fade, _ => DecrementAnimatingCount(), null, stack, null), group);

            if (stack.Poof is not null)
            {
                var poof = new AnimateCallback(
                    AnimatePoof, null, stack.Poof,
                    stack.Poof.TotalFrames * AnimateBase.FrameTime60Fps, delay,
                    AnimateBase.StyleLinear);
                poof.SetEndCallback(_ => DecrementAnimatingCount(), null);
                Add(poof, group);
                animations++;
            }

            PlayDelayedSound(
                delay,
                stack.Owner == 0 ? "K_removeLosingPlayerBet" : "K_removeLosingBet",
                group);

            stack.Owner = -1;
            losers.RemoveAt(index);
        }

        return animations;
    }

    /// <summary><c>GenerateAndResolvePayouts</c> (0x009634-0x009adb).</summary>
    public void GenerateAndResolvePayouts(BoardInfo info)
    {
        info.Payouts.Clear();

        bool archCollapsed =
            ArchStoneToRemove == KeystoneSpace.Out3 ||
            ArchStoneToRemove == KeystoneSpace.Out18 ||
            (ArchStoneToRemove == KeystoneSpace.Out10 && !_archStonesIntact[KeystoneSpace.Out11]) ||
            (ArchStoneToRemove == KeystoneSpace.Out11 && !_archStonesIntact[KeystoneSpace.Out10]);

        for (int seat = 0; seat < SeatsPerTable; seat++)
        {
            KeystonePlayer? player = info.GetPlayer(seat);
            if (player is null)
            {
                continue;
            }

            int[] bets = player.Bets;
            player.Wins = 0;
            player.InsideWins = 0;

            for (int space = KeystoneSpace.FirstBetSpace; space <= KeystoneSpace.LastBetSpace; space++)
            {
                int bet = bets[space];
                if (bet <= 0)
                {
                    continue;
                }

                int winnings = 0;
                bool lost = false;
                bool arch = IsArchSpace(space);

                if (Bloodstone)
                {
                    if (_isWinningSpace[space])
                    {
                        winnings = -_payoutRate[space];
                        bet = 0;
                    }
                    else if (!arch || archCollapsed)
                    {
                        winnings = MinBet;
                        bet = 0;
                    }
                }
                else if (_isWinningSpace[space])
                {
                    winnings = bet * _payoutRate[space] / _payoutBet[space];
                }
                else if (!arch || archCollapsed)
                {
                    lost = true;
                    bet = 0;
                }

                if (winnings == 0 && !lost)
                {
                    continue;
                }

                bets[space] = 0;
                if (player.IsAI)
                {
                    info.AiBettorsOnSpace[space]--;
                }

                var payout = new Payout(player.ID, space, winnings, bet, arch, seat);
                if (IsMainBoard(info) && Board is not null)
                {
                    payout.Stack = Board.GetChipStack(seat, space);
                    payout.PayStack = Board.GetPayoutStack(seat, space);
                }

                info.Payouts.Add(payout);
                player.Wins += winnings + bet;
                if (!arch)
                {
                    player.InsideWins += winnings + bet;
                }

                player.Chips += winnings + bet;
            }
        }
    }

    public static bool IsArchSpace(int space) =>
        space >= KeystoneSpace.Out3 && space <= KeystoneSpace.Out18;

    /// <summary>
    /// <c>ShowPayoutStacks</c> (0x009b5f-0x00a042). A Bloodstone loss reuses the delay register of
    /// the last win this call drew (0x009f97); before any win it is undefined, which AS2's
    /// <c>Delay</c> and <c>AnimateBase</c> finish on their first frame.
    /// </summary>
    public int ShowPayoutStacks(object? group)
    {
        const double spread = 400;
        const double fade = 200;
        int shown = 0;
        double? delay = null;

        foreach (Payout payout in _boardInfos[0].Payouts)
        {
            if (payout.Winnings == 0 || payout.Stack is null)
            {
                continue;
            }

            ChipStack stack = payout.Stack;

            if (payout.Winnings > 0 && payout.PayStack is not null)
            {
                ChipStack pay = payout.PayStack;
                pay.BaseX = stack.TopChipX;
                pay.BaseY = stack.TopChipY;

                double at = (Random.NextDouble() * spread) + (Random.NextDouble() * spread) +
                    (Random.NextDouble() * spread);
                delay = at;

                Add(new AnimateAlpha(0, 100, pay.Stack, fade, at, AnimateBase.StyleLinear), group);
                Add(SetChipStackValue(
                    payout.Winnings, payout.Winnings, 1, at,
                    _ => PayoutOnBoard(payout), null, pay, null), group);

                if (payout.Seat == 0)
                {
                    int total = payout.Winnings + payout.Bet;
                    IPubClip? label = total > 999 ? pay.WinBig : pay.Win;
                    if (label is not null)
                    {
                        LabelStack(label, total);
                        Add(new AnimateAlpha(0, 100, label, fade, at, AnimateBase.StyleLinear), group);
                        DelayCallback(at, _ => label.Play(), null, group);
                    }
                }

                PlayDelayedSound(
                    at,
                    payout.Seat == 0 ? "K_showPlayerPayoutStack" : "K_showPayoutStack",
                    group);
            }
            else if (stack.Loss is not null)
            {
                IPubClip loss = stack.Loss;
                LabelStack(loss, payout.Winnings);
                double at = delay ?? 0;
                double time = delay is null ? 1 : fade;
                Add(new AnimateAlpha(0, 100, loss, time, at, AnimateBase.StyleLinear), group);
                DelayCallback(at, _ => loss.Play(), null, group);
                DelayCallback(at, _ => PayoutOnBoard(payout), null, group);
            }

            shown++;
        }

        return shown;
    }

    /// <summary><c>MoveWinsToPlayers</c> (0x00a293-0x00a991).</summary>
    public int MoveWinsToPlayers(object? group)
    {
        var winners = new List<Payout>();
        foreach (Payout payout in _boardInfos[0].Payouts)
        {
            if (payout.Winnings != 0)
            {
                winners.Add(payout);
            }
        }

        int immediate = (int)Math.Floor(Random.NextDouble() * winners.Count);
        const double spread = 300;
        const double fade = 200;
        int animations = 0;

        for (int i = 0; i < winners.Count; i++)
        {
            Payout payout = winners[i];
            ChipStack? stack = payout.Stack;
            ChipStack? pay = payout.PayStack;
            if (stack is null)
            {
                continue;
            }

            double delay = i != immediate
                ? (Random.NextDouble() * spread) + (Random.NextDouble() * spread) +
                  (Random.NextDouble() * spread)
                : 0;

            if (payout.Winnings > 0 && pay is not null)
            {
                Add(new AnimateAlpha(100, 0, pay.Stack, fade, delay, AnimateBase.StyleLinear), group);
                Add(SetChipStackValue(0, 0, 1, delay + fade, null, null, pay, null), group);

                if (payout.Seat == 0)
                {
                    int total = payout.Winnings + payout.Bet;
                    IPubClip? label = total > 999 ? pay.WinBig : pay.Win;
                    if (label is not null)
                    {
                        WinNumbers?.AddWin(label, delay);
                    }
                }
            }
            else if (stack.Loss is not null)
            {
                WinNumbers?.AddWin(stack.Loss, delay);
            }

            if (stack.Poof is not null)
            {
                var poof = new AnimateCallback(
                    AnimatePoof, null, stack.Poof,
                    stack.Poof.TotalFrames * AnimateBase.FrameTime60Fps, delay,
                    AnimateBase.StyleLinear);
                poof.SetEndCallback(_ => DecrementAnimatingCount(), null);
                Add(poof, group);
                animations++;
            }

            Add(new AnimateAlpha(100, 0, stack.Stack, fade, delay, AnimateBase.StyleLinear), group);
            if (stack.Shadow is not null)
            {
                Add(new AnimateAlpha(
                    100, 0, stack.Shadow, fade, delay, AnimateBase.StyleLinear), group);
            }

            Add(SetChipStackValue(0, 0, 1, delay + fade, null, null, stack, null), group);

            PlayDelayedSound(
                delay,
                payout.Seat == 0 ? "K_collectWinningPlayerStack" : "K_collectWinningStack",
                group);

            DelayCallback(delay + ChipTravelTime, _ => PayoutAtPlayer(payout), null, group);

            stack.Owner = -1;
            if (pay is not null)
            {
                pay.Owner = -1;
            }

            animations++;
        }

        return animations;
    }

    /// <summary><c>PayoutOnBoard</c> (0x00a0d0-0x00a19a).</summary>
    private void PayoutOnBoard(Payout payout)
    {
        if (payout.Seat == 0)
        {
            int total = payout.Winnings + payout.Bet;
            if (Bloodstone || !IsArchSpace(payout.Space))
            {
                InsideWinsValue += total;
            }
            else
            {
                ArchWinsValue += total;
            }

            TotalWins += total;
        }

        AnimatingCount--;
    }

    /// <summary><c>PayoutAtPlayer</c> (0x00a1cc-0x00a250).</summary>
    private void PayoutAtPlayer(Payout payout)
    {
        ChipStackMulti? stack = Board?.GetPlayerStack(payout.Seat);
        if (stack is not null)
        {
            stack.Value += payout.Bet + payout.Winnings;
        }

        AnimatingCount--;
    }

    private void LabelStack(IPubClip clip, int value)
    {
        IPubClip? label = clip.GetChild("label");
        if (label is not null)
        {
            label.Text = Localizer.LocalizeString(
                "txtStackValue", new Dictionary<string, string> { ["value"] = value.ToString() });
        }
    }

    private void Add(Callback callback, object? group)
    {
        callback.Group = group;
        CallbackManager.AddCallbackObject(callback);
    }

    /// <summary>
    /// <c>RePlaceLastBets</c> (0x00ae29-0x00b219): the need-chips alert when the repeat costs more
    /// than the stack, the limit sound when there is nothing to repeat.
    /// </summary>
    public void RePlaceLastBets(bool arch)
    {
        KeystonePlayer? human = GetHumanPlayer();
        if (human is null)
        {
            return;
        }

        int[] bets = human.Bets;
        int first = arch ? KeystoneSpace.Out3 : KeystoneSpace.FirstInnerSpace;
        int last = arch ? KeystoneSpace.Out18 : KeystoneSpace.LastInnerSpace;
        int needed = 0;
        int newSpaces = 0;

        if (Bloodstone)
        {
            for (int space = first; space <= last; space++)
            {
                if (_lastBets[space] > 0)
                {
                    if (bets[space] <= 0)
                    {
                        newSpaces++;
                    }

                    bets[space] += _payoutBet[space];
                }
            }

            needed += BloodstoneExposure(human, true);
            if (!arch)
            {
                needed += BloodstoneExposure(human, false);
            }

            for (int space = first; space <= last; space++)
            {
                if (_lastBets[space] > 0)
                {
                    bets[space] -= _payoutBet[space];
                }
            }
        }
        else
        {
            int cap = arch ? MaxBet : MaxInsideBet;
            for (int space = first; space <= last; space++)
            {
                int wanted = _lastBets[space];
                int held = bets[space];
                if (held + wanted > cap)
                {
                    wanted = cap - held;
                }

                needed += wanted;
            }
        }

        if (needed > human.Chips)
        {
            OverlayManager?.ActivateNeedChipsAlert();
            return;
        }

        if ((!Bloodstone && needed > 0) || (Bloodstone && newSpaces > 0))
        {
            for (int space = first; space <= last; space++)
            {
                ChangeBet(_lastBets[space], human, space, false, null);
            }

            UpdateBetStatsRecount(arch);
            Board?.UpdateSpaceStatsBet(Current);
            PlayDelayedSound(0, Bloodstone ? "BS_chips01Single" : "K_playerBetChanged");
            return;
        }

        PlayDelayedSound(0, "G_limitReached");
    }

    /// <summary><c>SaveLastBets</c> (0x00b245-0x00b311).</summary>
    public void SaveLastBets(bool arch)
    {
        KeystonePlayer? human = GetHumanPlayer();
        if (human is null || human.GetBetAmount(arch) <= 0)
        {
            return;
        }

        int first = arch ? KeystoneSpace.Out3 : KeystoneSpace.FirstInnerSpace;
        int last = arch ? KeystoneSpace.Out18 : KeystoneSpace.LastInnerSpace;
        for (int space = first; space <= last; space++)
        {
            _lastBets[space] = human.Bets[space];
        }
    }

    public int BloodstoneExposure(KeystonePlayer player, bool arch)
    {
        int[] bets = player.Bets;
        int best = 0;

        if (arch)
        {
            for (int space = KeystoneSpace.Out3; space <= KeystoneSpace.Out18; space++)
            {
                if (bets[space] > 0)
                {
                    best += _payoutRate[space];
                }
            }

            return best;
        }

        int first = KeystoneSpace.FirstInnerSpace;
        int last = KeystoneSpace.LastInnerSpace;
        var rate = new int[last + 1];
        var stake = new int[last + 1];
        for (int space = first; space <= last; space++)
        {
            if (bets[space] > 0)
            {
                rate[space] = _payoutRate[space];
                stake[space] = _payoutBet[space];
            }
        }

        for (int d1 = 1; d1 <= 6; d1++)
        {
            for (int d2 = d1; d2 <= 6; d2++)
            {
                for (int d3 = d2; d3 <= 6; d3++)
                {
                    IReadOnlyList<bool> winners = KeystoneRules.GetWinners(d1, d2, d3);
                    int total = 0;
                    for (int space = first; space <= last; space++)
                    {
                        total += winners[space] ? rate[space] : -stake[space];
                    }

                    if (total > best)
                    {
                        best = total;
                    }
                }
            }
        }

        return best;
    }

    /// <summary><c>FindWinningSpaces</c> (0x00bc41-0x00be97).</summary>
    public void FindWinningSpaces(bool allowInner)
    {
        JackpotRolled = false;
        IReadOnlyList<bool> winners = KeystoneRules.GetWinners(Roll1, Roll2, Roll3);

        for (int i = KeystoneSpace.FirstInnerSpace; i <= KeystoneSpace.LastInnerSpace; i++)
        {
            _isWinningSpace[i] = allowInner && winners[i];
        }

        if (AllowJackpot && CurrentRoll == 1 && (RollTotal == 3 || RollTotal == 18))
        {
            JackpotRolled = true;
            for (int i = KeystoneSpace.Out3; i <= KeystoneSpace.Out18; i++)
            {
                _isWinningSpace[i] = true;
            }

            ArchStoneToRemove = RollTotal - 3 + KeystoneSpace.Out3;
            return;
        }

        for (int i = KeystoneSpace.Out3; i <= KeystoneSpace.Out18; i++)
        {
            _isWinningSpace[i] = false;
        }

        int space = RollTotal - 3 + KeystoneSpace.Out3;
        while (space >= KeystoneSpace.Out3 && space <= KeystoneSpace.Out18)
        {
            if (_archStonesIntact[space])
            {
                _isWinningSpace[space] = true;
                ArchStoneToRemove = space;
                return;
            }

            space = space <= KeystoneSpace.Out10 ? space - 1 : space + 1;
        }
    }

    /// <summary>
    /// <c>HandleGlobalInput</c> (0x00beb7-0x00bf8b): the tutorial's input, or A with no pump
    /// running triples the animation speed until the next pump resets it.
    /// </summary>
    private void HandleGlobalInput(double ticks)
    {
        GamePad? pad = Pad;
        if (pad is null)
        {
            return;
        }

        if (Tutorial)
        {
            HandleTutorialInput(pad, ticks);
            return;
        }

        if (!BlockGameInput && _pump is null && pad.BtnAPressed)
        {
            CallbackManager.Speed = 3;
        }
    }

    /// <summary><c>GameLoop</c> (0x00bfb9-0x00c083): never finishes.</summary>
    private bool GameLoop(double ticks)
    {
        BlockGameInput = OverlayManager is OverlayManager overlays &&
            (overlays.QuickTipsShowing || overlays.StandingsShowing);
        HandleGlobalInput(ticks);
        _pump?.Invoke(ticks);
        UpdatePlayerChipDisplays(ticks);
        WinNumbers?.Update(ticks);
        return false;
    }

    public void JumpToNewLocation(int id, bool fade, object? group)
    {
        if (Current != id)
        {
            Board?.ShowFocus(Current, false, true, group, false);
            Current = id;
        }

        Board?.ShowFocus(Current, true, fade, group, false);
    }

    public void Navigate(GamePad? pad, double ticks, object? group)
    {
        double x = 0;
        double y = 0;

        if (pad is not null)
        {
            x = pad.DPadRight ? 1 : pad.DPadLeft ? -1 : pad.ThumbLX;
            y = pad.DPadUp ? 1 : pad.DPadDown ? -1 : pad.ThumbLY;
        }

        if (Math.Abs(x) <= 0 && Math.Abs(y) <= 0)
        {
            _ticksTillNextMove = 0;
            MoveWaitTime = MaxCursorWaitTime;
            return;
        }

        if (_ticksTillNextMove > 0)
        {
            _ticksTillNextMove -= ticks;
            return;
        }

        _ticksTillNextMove = MoveWaitTime;
        MoveWaitTime *= CursorWaitTimeFactor;
        if (MoveWaitTime < MinCursorWaitTime)
        {
            MoveWaitTime = MinCursorWaitTime;
        }

        double angle = KeystoneRules.AtanDegrees(y, x);
        if (angle < 0)
        {
            angle += 360;
        }

        int next = KeystoneRules.NextBetLocation(Current, angle);
        if (next != Current)
        {
            JumpToNewLocation(next, false, group);
        }
    }

    /// <summary><c>ClearBets</c> (0x00c539-0x00c683).</summary>
    public void ClearBets()
    {
        for (int i = KeystoneSpace.FirstBetSpace; i <= KeystoneSpace.LastBetSpace; i++)
        {
            _lastBets[i] = 0;
        }

        foreach (BoardInfo info in _boardInfos)
        {
            for (int i = KeystoneSpace.FirstBetSpace; i <= KeystoneSpace.LastBetSpace; i++)
            {
                info.AiBettorsOnSpace[i] = 0;
            }

            for (int seat = 0; seat < SeatsPerTable; seat++)
            {
                info.GetPlayer(seat)?.ClearBets();
            }
        }
    }

    /// <summary>
    /// <c>IsRoomForAiBet</c> (0x00c6ac-0x00c76f): an NPC leaves the human's slot free until the
    /// human has finished betting, and for good once the human bets that space.
    /// </summary>
    public bool IsRoomForAiBet(BoardInfo info, int id, int seat)
    {
        int limit = Board?.MaxBettorsOnSpace(id) ?? 0;
        KeystonePlayer? human = GetHumanPlayer();
        if (seat != 0 && IsMainBoard(info) &&
            (!HumanFinishedBetting || (human is not null && human.Bets[id] > 0)))
        {
            limit--;
        }

        return info.AiBettorsOnSpace[id] < limit;
    }

    /// <summary>
    /// <c>ChangeBet</c> (0x00c7bb-0x00c9cd): sets the human's space stack and player stack, and
    /// with <paramref name="playSound"/> the chip sound. True when the clamped bet differs, even if
    /// the stack could not cover it.
    /// </summary>
    public bool ChangeBet(int amount, KeystonePlayer player, int id, bool playSound, object? group)
    {
        int[] bets = player.Bets;
        int before = bets[id];
        int after = before + amount;
        if (after < 0)
        {
            after = 0;
        }
        else if (after > _maxBets[id])
        {
            after = _maxBets[id];
        }

        if (after == before)
        {
            return false;
        }

        int delta = after - before;
        int chips = player.Chips;
        if (delta < 0 || (delta > 0 && delta <= chips))
        {
            bets[id] = after;
            ChipStack? stack = Board?.GetChipStack(0, id);
            if (stack is not null)
            {
                stack.Value = after;
                if (after == 0)
                {
                    stack.Owner = -1;
                }
            }

            if (!Bloodstone)
            {
                player.Chips = chips - delta;
                ChipStackMulti? playerStack = Board?.GetPlayerStack(0);
                if (playerStack is not null)
                {
                    playerStack.Value = player.Chips;
                }
            }

            if (playSound)
            {
                PlayDelayedSound(0, Bloodstone ? "BS_chips01Single" : "K_playerBetChanged", group);
            }
        }

        return true;
    }

    /// <summary><c>UpdateBetStatsRecount</c> (0x00c9f9-0x00ca96).</summary>
    public void UpdateBetStatsRecount(bool arch)
    {
        KeystonePlayer? player = GetHumanPlayer();
        if (player is null)
        {
            return;
        }

        if (Bloodstone)
        {
            if (arch)
            {
                ArchBetsValue = BloodstoneExposure(player, true);
            }
            else
            {
                InsideBetsValue = BloodstoneExposure(player, false);
            }
        }
        else if (arch)
        {
            ArchBetsValue = player.GetBetAmount(true);
        }
        else
        {
            InsideBetsValue = player.GetBetAmount(false);
        }

        UpdateBetStats();
    }

    /// <summary><c>UpdateBetStats</c> (0x00cab9-0x00cb40).</summary>
    public void UpdateBetStats()
    {
        if (Bloodstone)
        {
            Board?.UpdateBetStats(
                ArchBetsValue, InsideBetsValue,
                ArchBetsValue + InsideBetsValue, ArchWinsValue + InsideWinsValue);
            return;
        }

        Board?.UpdateBetStats(ArchBetsValue, ArchWinsValue, InsideBetsValue, InsideWinsValue);
    }

    public void InitPayoutRate()
    {
        for (int id = 0; id < _payoutRate.Length; id++)
        {
            _payoutRate[id] = KeystoneRules.DefaultPayoutRate(id);
        }
    }

    public int GetPayoutRate(int id) =>
        id >= 0 && id < _payoutRate.Length ? _payoutRate[id] : 0;

    public void SetPayoutRates(IReadOnlyList<int> rates)
    {
        for (int id = 0; id < rates.Count && id < _payoutRate.Length; id++)
        {
            _payoutRate[id] = rates[id];
        }
    }

    public void InitBetLimits()
    {
        for (int i = KeystoneSpace.FirstInnerSpace; i <= KeystoneSpace.LastInnerSpace; i++)
        {
            _payoutBet[i] = MinInsideBet;
            _maxBets[i] = MaxInsideBet;
        }

        for (int i = KeystoneSpace.Out3; i <= KeystoneSpace.Out18; i++)
        {
            _payoutBet[i] = MinBet;
            _maxBets[i] = MaxBet;
        }
    }

    public int GetPayoutBet(int id) => _payoutBet[id];

    public int GetMaxBet(int id) => _maxBets[id];
}
