using System;
using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.Keystone;

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
        Cheated = false;
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

    public bool Cheated { get; private set; }

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

    public IReadOnlyList<BoardInfo> BoardInfos => _boardInfos;

    public IReadOnlyList<bool> IsWinningSpace => _isWinningSpace;

    public IReadOnlyList<bool> ArchStonesIntact => _archStonesIntact;

    public IReadOnlyList<int> LastBets => _lastBets;

    public override Player CreatePlayerObject(
        int id, bool isAI, string name, int gold, int debt,
        int starRating, int points, int gamePoints) =>
        new KeystonePlayer(id, isAI, name, gold, debt, starRating, points, gamePoints);

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

    public KeystonePlayer? GetPlayerByID(int id) => PlayerInfo.GetPlayer(id) as KeystonePlayer;

    public KeystonePlayer? GetHumanPlayer() => PlayerInfo.GetPlayer(0) as KeystonePlayer;

    public GameBoard? Board { get; set; }

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

    public bool PlaceBet(bool arch, double ticks, GamePad? pad)
    {
        KeystonePlayer? player = GetHumanPlayer();
        if (player is null || pad is null)
        {
            return false;
        }

        int held = player.Bets[Current];
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
        else if (ShouldChangeBet(ticks, BetInputFromPad(pad)))
        {
            amount = BetMultiplier * _payoutBet[Current];
            if (BetInputFromPad(pad) < 0)
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

        if ((!Bloodstone && amount > player.Chips) ||
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

        int before = player.GetBetCount(arch);
        if (!ChangeBet(amount, player, Current))
        {
            return false;
        }

        if (pad.BtnA)
        {
            Board?.PulseLegend(2);
        }

        UpdateBetStatsRecount(arch);
        Board?.UpdateSpaceStatsBet(Current);
        return true;
    }

    public bool BetPhase(bool arch, double ticks, GamePad? pad)
    {
        DoAiBets(_boardInfos[0], ticks, arch);

        KeystonePlayer? human = GetHumanPlayer();
        if (human is null)
        {
            return false;
        }

        if (human.IsAI)
        {
            return human.BetsToMake <= 0;
        }

        if (pad is null || BlockGameInput)
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
            if (Bloodstone)
            {
                int exposure = BloodstoneExposure(human, true);
                if (!arch)
                {
                    exposure += BloodstoneExposure(human, false);
                }

                if (exposure > human.Chips)
                {
                    OverlayManager?.ActivateNeedChipsAlert();
                    return false;
                }
            }

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

        int betsBefore = human.GetBetCount(arch);
        PlaceBet(arch, ticks, pad);
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

    public void SetLastChips(int seat, int value)
    {
        while (_lastChips.Count <= seat)
        {
            _lastChips.Add(0);
        }

        _lastChips[seat] = value;
    }

    public void UpdatePlayerChipDisplays(double ticks)
    {
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
                    best += GetPayoutRate(space);
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
                rate[space] = GetPayoutRate(space);
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
                    Math.Min(_payoutBet[space], GetPayoutRate(space)));
            }
        }
    }

    public void ReportPointsEarned()
    {
        if (SavedPoints > 0)
        {
            PointsEarned(SavedPoints);
            SavedPoints = 0;
        }
    }

    public void PulseBankButton() => Board?.PulseLegend(4);

    public void PulseStandingsButton() => Board?.PulseLegend(5);

    public void PulseQuickTipsButton() => Board?.PulseLegend(1);

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

    public void RevealBoard()
    {
        ShowQuicktipsText(true);
        ShowStandingsText(true);
        if (!Tournament)
        {
            ShowBankText(true);
        }
    }

    public void StartArchBetting()
    {
        ArchBetsValue = 0;
        ArchWinsValue = 0;
        InsideBetsValue = 0;
        InsideWinsValue = 0;
        ArchPointsLockedIn = false;
        UpdateBetStats();

        Current = KeystoneSpace.Out10;
        Board?.ShowFocus(Current, true, true, null, false);
        StartAiBetting(true);
        _blockIncreaseBet = true;
        _blockDecreaseBet = true;
        ShowBetText(true);
        ShowArchBetNotice(true);
    }

    public void StartInsideBetting()
    {
        PlayDelayedSound(0, "K_startInsideBetting");
        InsideBetsValue = 0;
        InsideWinsValue = 0;
        InnerPointsLockedIn = false;
        UpdateBetStatsRecount(false);

        Current = KeystoneSpace.Keystone;
        Board?.ShowFocus(Current, true, true, null, false);
        StartAiBetting(false);
        _blockIncreaseBet = true;
        _blockDecreaseBet = true;
        ShowBetText(true);
        ShowRollText(true);
    }

    public void EndBettingPhase(bool arch)
    {
        SaveLastBets(arch);
        if (!arch)
        {
            SavePointsEarned();
        }

        ShowBetText(false);
        ShowRollText(false);
        if (arch)
        {
            ShowArchBetNotice(false);
        }

        Board?.ShowFocus(Current, false, true, null, false);
        FinishAiBets(_boardInfos[0], arch);
    }

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

    public bool RePlaceLastBets(bool arch)
    {
        KeystonePlayer? human = GetHumanPlayer();
        if (human is null)
        {
            return false;
        }

        int first = arch ? KeystoneSpace.Out3 : KeystoneSpace.FirstInnerSpace;
        int last = arch ? KeystoneSpace.Out18 : KeystoneSpace.LastInnerSpace;
        int cap = arch ? MaxBet : MaxInsideBet;

        int needed = 0;
        for (int space = first; space <= last; space++)
        {
            int wanted = _lastBets[space];
            int held = human.Bets[space];
            if (held + wanted > cap)
            {
                wanted = cap - held;
            }

            needed += wanted;
        }

        if (needed > human.Chips || needed <= 0)
        {
            PlayDelayedSound(0, "G_limitReached");
            return false;
        }

        for (int space = first; space <= last; space++)
        {
            ChangeBet(_lastBets[space], human, space);
        }

        UpdateBetStatsRecount(arch);
        Board?.UpdateSpaceStatsBet(Current);
        PlayDelayedSound(0, Bloodstone ? "BS_chips01Single" : "K_playerBetChanged");
        return true;
    }

    public void FinishAiBets(BoardInfo info, bool arch)
    {
        for (int seat = 0; seat < SeatsPerTable; seat++)
        {
            KeystonePlayer? player = info.GetPlayer(seat);
            if (player is null || !player.IsAI)
            {
                continue;
            }

            while (player.BetsToMake > 0)
            {
                int minimum = arch ? MinBet : MinInsideBet;
                if (player.Chips < minimum)
                {
                    break;
                }

                MakeAiBet(info, player, seat, arch, true, MaxAiFinishBetsTime);
                player.BetsToMake--;
            }

            player.BetsToMake = 0;
        }
    }

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

    public void PlaySound(string soundName) => Audio.PlaySound(soundName);

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

    public int GenerateStartingChipStackValue() =>
        BuyIn + ((int)Math.Floor(BuyIn * 0.1 * Random.NextDouble()) * 20);

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

    public void SeatAiPlayers()
    {
        BoardInfo main = _boardInfos[0];
        for (int seat = 1; seat < SeatsPerTable; seat++)
        {
            if (main.GetPlayer(seat) is not null)
            {
                continue;
            }

            var name = Localizer.LocalizeString("txtNPCName" + Random.Range(83), null);
            var ai = new KeystonePlayer(seat, true, name, 0, 0, 1, 0, 0);
            main.SetPlayer(seat, ai);
        }
    }

    public void InitBoardInfos()
    {
        _boardInfos.Clear();
        for (int i = 0; i < NumBoards; i++)
        {
            _boardInfos.Add(new BoardInfo(i));
        }
    }

    public bool IsMainBoard(BoardInfo info) => info.BoardIndex == 0;

    public void ResetArchStones()
    {
        for (int i = KeystoneSpace.Out3; i <= KeystoneSpace.Out18; i++)
        {
            _archStonesIntact[i] = true;
        }

        ArchStoneToRemove = KeystoneSpace.None;
    }

    public void RemoveArchStone()
    {
        if (ArchStoneToRemove != KeystoneSpace.None)
        {
            _archStonesIntact[ArchStoneToRemove] = false;
        }
    }

    public bool IsGameOver()
    {
        return !_archStonesIntact[KeystoneSpace.Out10] && !_archStonesIntact[KeystoneSpace.Out11];
    }

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

    public void PushScriptedRoll(int roll1, int roll2, int roll3)
    {
        _scriptedRolls.Enqueue(roll1);
        _scriptedRolls.Enqueue(roll2);
        _scriptedRolls.Enqueue(roll3);
    }

    public void GenerateRoll()
    {
        Roll1 = Random.Range(6) + 1;
        Roll2 = Random.Range(6) + 1;
        Roll3 = Random.Range(6) + 1;
        JackpotRolled = false;
        Cheated = false;

        if (AllowScriptedRolls && _scriptedRolls.Count >= 3)
        {
            Roll1 = _scriptedRolls.Dequeue();
            Roll2 = _scriptedRolls.Dequeue();
            Roll3 = _scriptedRolls.Dequeue();
        }

        RollTotal = Roll1 + Roll2 + Roll3;
        FindWinningSpaces(true);
    }

    public void SetRoll(int roll1, int roll2, int roll3, bool cheated = true)
    {
        Roll1 = roll1;
        Roll2 = roll2;
        Roll3 = roll3;
        Cheated = cheated;
        RollTotal = roll1 + roll2 + roll3;
        FindWinningSpaces(true);
    }

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

    public bool ChangeBet(int amount, KeystonePlayer player, int id)
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
            if (!Bloodstone)
            {
                player.Chips = chips - delta;
            }

            return true;
        }

        return false;
    }

    public bool IsArchSpace(int space) =>
        space >= KeystoneSpace.Out3 && space <= KeystoneSpace.Out18;

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
                        winnings = -GetPayoutRate(space);
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
                    winnings = bet * GetPayoutRate(space) / _payoutBet[space];
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
                if (space > KeystoneSpace.Out18 || space < KeystoneSpace.Out3)
                {
                    player.InsideWins += winnings + bet;
                }

                player.Chips += winnings + bet;
            }
        }
    }

    public AnimateWinNumbers? WinNumbers { get; set; }

    public int AnimatingCount { get; private set; }

    public int TotalWins { get; private set; }

    private void DecrementAnimatingCount(object? userData) => AnimatingCount--;

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
            int index = Random.Range(losers.Count);
            ChipStack stack = losers[index];

            double delay = (Random.NextDouble() * spread) + (Random.NextDouble() * spread) +
                (Random.NextDouble() * spread);

            Add(new AnimateAlpha(100, 0, stack.Stack, fade, delay, AnimateBase.StyleLinear), group);
            if (stack.Shadow is not null)
            {
                Add(new AnimateAlpha(
                    100, 0, stack.Shadow, fade, delay, AnimateBase.StyleLinear), group);
            }

            var clear = new AnimateValue(0, 0, stack, 1, delay + fade, AnimateBase.StyleLinear);
            clear.SetEndCallback(DecrementAnimatingCount, null);
            Add(clear, group);

            if (stack.Poof is not null)
            {
                var poof = new AnimateCallback(
                    AnimatePoof, null, stack.Poof,
                    stack.Poof.TotalFrames * AnimateBase.FrameTime60Fps, delay,
                    AnimateBase.StyleLinear);
                poof.SetEndCallback(DecrementAnimatingCount, null);
                Add(poof, group);
                animations++;
            }

            PlayDelayedSound(
                delay,
                stack.Owner != 0 ? "K_removeLosingBet" : "K_removeLosingPlayerBet",
                group);

            stack.Owner = -1;
            losers.RemoveAt(index);
        }

        return animations;
    }

    public int ShowPayoutStacks(object? group)
    {
        const double spread = 400;
        const double fade = 200;
        int shown = 0;

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

                double delay = (Random.NextDouble() * spread) + (Random.NextDouble() * spread) +
                    (Random.NextDouble() * spread);

                Add(new AnimateAlpha(0, 100, pay.Stack, fade, delay, AnimateBase.StyleLinear), group);

                var grow = new AnimateValue(
                    payout.Winnings, payout.Winnings, pay, 1, delay, AnimateBase.StyleLinear);
                grow.SetEndCallback(_ => PayoutOnBoard(payout), null);
                Add(grow, group);

                if (payout.Seat == 0)
                {
                    int total = payout.Winnings + payout.Bet;
                    IPubClip? label = total > 999 ? pay.WinBig : pay.Win;
                    if (label is not null)
                    {
                        LabelStack(label, total);
                        Add(new AnimateAlpha(
                            0, 100, label, fade, delay, AnimateBase.StyleLinear), group);
                        DelayCallback(delay, _ => label.Play(), null, group);
                    }
                }

                PlayDelayedSound(
                    delay,
                    payout.Seat != 0 ? "K_showPayoutStack" : "K_showPlayerPayoutStack",
                    group);
            }
            else if (stack.Loss is not null)
            {
                IPubClip loss = stack.Loss;
                LabelStack(loss, payout.Winnings);
                Add(new AnimateAlpha(0, 100, loss, fade, 0, AnimateBase.StyleLinear), group);
                DelayCallback(0, _ => loss.Play(), null, group);
                DelayCallback(0, _ => PayoutOnBoard(payout), null, group);
            }

            shown++;
        }

        return shown;
    }

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

        if (winners.Count == 0)
        {
            return 0;
        }

        int immediate = Random.Range(winners.Count);
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
                Add(new AnimateValue(0, 0, pay, 1, delay + fade, AnimateBase.StyleLinear), group);

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
                poof.SetEndCallback(DecrementAnimatingCount, null);
                Add(poof, group);
                animations++;
            }

            Add(new AnimateAlpha(100, 0, stack.Stack, fade, delay, AnimateBase.StyleLinear), group);
            if (stack.Shadow is not null)
            {
                Add(new AnimateAlpha(
                    100, 0, stack.Shadow, fade, delay, AnimateBase.StyleLinear), group);
            }

            Add(new AnimateValue(0, 0, stack, 1, delay + fade, AnimateBase.StyleLinear), group);

            PlayDelayedSound(
                delay,
                payout.Seat != 0 ? "K_collectWinningStack" : "K_collectWinningPlayerStack",
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

    public void AddAnimating(int count) => AnimatingCount += count;

    public void AdvanceRoll()
    {
        CurrentRoll++;
        TotalRolls++;
    }

    public void StartNextGame()
    {
        CurrentGame++;
        CurrentRoll = 1;
    }

    public bool HumanFinishedBetting { get; set; }

    public int GenerateAiBetCount(bool arch)
    {
        int[] table = arch ? NumberOfBetsArch : NumberOfBetsInside;
        return table[Random.Range(table.Length)];
    }

    public double GenerateAiBetWaitTime() =>
        Math.Floor(Random.NextDouble() * (MaxAiBetWaitTime - MinAiBetWaitTime + 1)) +
        MinAiBetWaitTime;

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

        int chosen = candidates[Random.Range(candidates.Count)];
        int amount = _payoutBet[chosen];

        if (!Bloodstone)
        {
            if (Tournament)
            {
                int[] table = arch ? NumberOfChipsBetArch : NumberOfChipsBetInside;
                amount = _payoutBet[chosen] * table[Random.Range(table.Length)];
                if (amount > _maxBets[chosen])
                {
                    amount = _maxBets[chosen];
                }
            }
            else
            {
                amount = _payoutBet[chosen] *
                    (1 + Random.Range(_maxBets[chosen] / _payoutBet[chosen]));
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

        if (!showAnims || !IsMainBoard(info) || Board is null)
        {
            return 0;
        }

        ChipStack? stack = Board.GetChipStack(seat, chosen);
        if (stack is null)
        {
            return 0;
        }

        double third = maxTime / 3;
        double delay = third <= 0
            ? 0
            : (Random.NextDouble() * third) + (Random.NextDouble() * third) +
              (Random.NextDouble() * third);

        var grow = new AnimateValue(
            stack.Value, amount, stack, 1, delay, AnimateBase.StyleLinear);
        CallbackManager.AddCallbackObject(grow);

        ChipStackMulti? seatStack = Board.GetPlayerStack(seat);
        int delta = Bloodstone ? 0 : -amount;
        DelayCallback(delay, _ =>
        {
            if (seatStack is not null)
            {
                seatStack.Value += delta;
            }
        });

        PlayDelayedSound(delay, "K_makeAiBet");
        return 1;
    }

    public void DoAiBets(BoardInfo info, double ticks, bool arch)
    {
        for (int seat = 0; seat < SeatsPerTable; seat++)
        {
            KeystonePlayer? player = info.GetPlayer(seat);
            if (player is null)
            {
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
                        Board?.UnseatPlayerOnMainBoard(seat);
                        player.JustUnseated = true;
                    }
                }

                continue;
            }

            MakeAiBet(info, player, seat, arch, true, 0);
            player.BetsToMake--;
            player.BetWaitTime = GenerateAiBetWaitTime();
        }
    }
}
