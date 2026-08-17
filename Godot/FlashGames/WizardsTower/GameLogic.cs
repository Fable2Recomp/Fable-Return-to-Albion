using System;
using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.WizardsTower;

public interface IWizardsTowerView
{
    PlayerInfo PlayerInfo { get; }

    bool Tournament { get; }

    bool Tutorial { get; }

    bool TournamentShootout { get; }

    bool TournamentFinalTable { get; }

    bool TournamentOver { get; }

    int TournamentStartingChips { get; }

    int MaxBet { get; }

    int EffectiveBuyIn { get; }

    int LowAIBuyInMultiple { get; }

    int HighAIBuyInMultiple { get; }

    bool NoSinglePlayerAIs { get; }

    bool AutoJackpot { get; }

    bool AutoBonus { get; }

    bool AutoUltimate { get; }

    string YButtonText { get; }

    IReadOnlyList<int> PresetAIList { get; }

    IReadOnlyList<int> StackedDeck { get; }

    bool NoPlayerBets { get; set; }

    bool HandOver { get; set; }

    void BuyChipsForPlayer(TowerPlayer towerplayer, double chips, int? dontAnimatePlayerPosition = null);

    void UpdateChipsForPlayer(TowerPlayer towerplayer, int? dontAnimatePlayerPosition = null);

    void SetChipsForPlayer(TowerPlayer towerplayer, double chips, int? dontAnimatePlayerPosition = null);

    void CashoutChipsForPlayer(TowerPlayer towerplayer, double chips, int? dontAnimatePlayerPosition = null);

    void WinChipsForPlayer(TowerPlayer towerplayer, double chips);

    void BetChipsForPlayer(TowerPlayer towerplayer, double chips);

    void UpdateBetAmountColor();

    void AchievementEarnedWT(GameLogic? game, string achievementname);

    void IncrementSkillRating(int goldrisked);

    void UpdateBonusState(GameLogic? game);

    void UpdatePlayerInfo(GameLogic? game, int player, bool statusonly = false);

    void UpdateAllPlayerStatusInfo(GameLogic? game);

    void PlayerEnters(GameLogic? game, int player);

    void PlayerExits(GameLogic? game, int player);

    void UpdateCashoutMarkers(GameLogic? game, int row, int count, bool human = false);

    void UpdateTextGlow(GameLogic? game, int row);

    void AnimateTheBurnCard(GameLogic? game);

    void AnimateTheCheatCard(GameLogic? game);

    void DealCards(GameLogic? game, int startcard, int? endcard = null);

    void SweepCards(GameLogic? game);

    void SetButtonsAfterSweep(GameLogic? game);

    void BustTheRow(GameLogic? game);

    void HitTheJackpot(GameLogic? game);

    void SetPushOnAndCashOutButtons(GameLogic? game);

    void SetBetAmount(GameLogic? game, int amount);

    void SetWinAmount(GameLogic? game, double amount);

    void SetAllButtons(GameLogic? game, string? buttonA, string? buttonB, string? buttonY, string? buttonX = null);

    void AddTutorialCallback(bool dontblock = false);

    void InsertTutorialCallback(bool dontblock = false);

    void PlayAIDecisionSound(GameLogic? game, int position);
}

public sealed class GameLogic
{
    private const int Seats = 5;
    private const int StakeUnit = 15;

    private readonly IPubRandom _random;

    public GameLogic(
        IWizardsTowerView gameview, int tableNum, int buyin,
        int rangeOfCards, int numberOfCards, int numberOfWarriors, int numberOfRows,
        IReadOnlyList<double> playTable, bool aiOnly, IPubRandom random)
    {
        _random = random;
        GameView = gameview;
        AIOnlyGame = aiOnly;
        AutoPlay = aiOnly;
        MinimumChipBuyIn = buyin;
        Players = new List<TowerPlayer>(Seats);

        for (var i = 0; i < Seats; i++)
        {
            Player? info;
            double chips = 0;

            if (GameView.TournamentFinalTable && i > 0)
            {
                info = i < GameView.PresetAIList.Count
                    ? GameView.PlayerInfo.GetPlayer(GameView.PresetAIList[i])
                    : null;
            }
            else if (AIOnlyGame || i > 0)
            {
                info = GetNewAIPlayer();
            }
            else
            {
                info = GameView.PlayerInfo.GetPlayer(0);
            }

            Players.Add(info is not null
                ? new TowerPlayer(info.ID, info.Name, 0, buyin, i)
                : new TowerPlayer(i, "PLAYER" + i, 0, buyin, i));

            if (info is not null)
            {
                if (GameView.Tournament)
                {
                    info.Chips = 0;
                }

                chips = info.Chips;
            }

            if (GameView.Tournament)
            {
                chips = GameView.TournamentStartingChips;
            }
            else if (chips < GameView.EffectiveBuyIn)
            {
                chips = GameView.EffectiveBuyIn - chips;
            }

            if (!GameView.Tournament)
            {
                if (i != 0)
                {
                    GameView.BuyChipsForPlayer(
                        Players[i],
                        chips * (Math.Floor(_random.NextDouble() * GameView.HighAIBuyInMultiple)
                            + GameView.LowAIBuyInMultiple),
                        i);
                }
                else if (!GameView.Tutorial)
                {
                    GameView.UpdateChipsForPlayer(Players[i], i);
                }
                else
                {
                    GameView.SetChipsForPlayer(Players[i], chips, i);
                }
            }
            else
            {
                GameView.SetChipsForPlayer(Players[i], chips, i);
            }

            if (i != 0 || AIOnlyGame)
            {
                SetAIStakes(i);
            }

            if (i == 0 && !AIOnlyGame)
            {
                GameView.UpdateBetAmountColor();
            }

            if (GameView.TournamentShootout && info is not null)
            {
                info.Table = tableNum;
            }

            if ((GameView.Tutorial || GameView.NoSinglePlayerAIs) && i > 0)
            {
                Players[i].SeatEmpty = true;
                Players[i].Chips = 0;
                Players[i].Out = true;
            }
        }

        Deck = new DeckOfCards(rangeOfCards, numberOfCards, numberOfWarriors, numberOfRows, random);
        PlayTable = playTable;
        Shuffle();
    }

    public DeckOfCards Deck { get; }

    public IWizardsTowerView GameView { get; }

    public int MinimumChipBuyIn { get; }

    public IReadOnlyList<double> PlayTable { get; }

    public List<TowerPlayer> Players { get; }

    public bool AIOnlyGame { get; }

    public bool AutoPlay { get; set; }

    public int CurrentCard { get; set; }

    public int CurrentRow { get; set; } = 1;

    public int CashOutValue { get; set; }

    public int CashOutCount { get; set; }

    public int PlayerCashOutRow { get; set; } = 100;

    public int HandsPlayed { get; set; }

    public bool BurnCardUsed { get; set; }

    public bool CheatUsed { get; set; }

    public bool Jackpot { get; set; }

    public bool ReadyToContinue { get; set; } = true;

    public int CheatTarget { get; set; }

    public int CheatRange { get; set; } = 1;

    public void SetReadyToTrue()
    {
        if (CurrentRow == 1 && HandsPlayed > 0)
        {
            GameView.AddTutorialCallback(HandsPlayed == 2);
        }

        ReadyToContinue = true;
        if (!AIOnlyGame)
        {
            if (Players[0].In || Players[0].Out)
            {
                Action.RunDelayed(this, AIUpdate, (0.5 + _random.NextDouble()) / 2);
            }
        }
        else
        {
            AIUpdate();
        }
    }

    public void PlayHand()
    {
        ReadyToContinue = false;
        if (CheatTarget < 0)
        {
            CheatTarget = 0;
        }

        GameView.SetAllButtons(this, string.Empty, string.Empty, string.Empty);
        for (var i = 0; i < Seats; i++)
        {
            if (!Players[i].In && !Players[i].Out)
            {
                PlayHandPosition(i);
            }
        }

        GameView.DealCards(this, 0);
        if (HandsPlayed == 0)
        {
            GameView.AddTutorialCallback();
        }

        if (!AIOnlyGame)
        {
            Action.RunOnce(this, DealSecondRow);
        }
        else
        {
            DealSecondRow();
        }
    }

    public void DealSecondRow()
    {
        CurrentRow = 2;
        CurrentCard += CurrentRow;
        GameView.DealCards(this, 1, 2);
        CashOutValue = Deck.RowValue(CurrentRow) * Deck.TotalBonus(CurrentRow);
        GameView.SetWinAmount(this, 0);
        GameView.SetAllButtons(this, "txtDealRow", "txtTakeWin", GameView.YButtonText);
        GameView.UpdateBonusState(this);
        ClearAllPlayersInStatus();
        GameView.UpdateAllPlayerStatusInfo(this);

        if (!AIOnlyGame)
        {
            Action.RunOnce(this, SetReadyToTrue);
        }
        else
        {
            ReadyToContinue = true;
        }

        if (HandsPlayed == 0)
        {
            GameView.InsertTutorialCallback();
            GameView.InsertTutorialCallback();
            GameView.InsertTutorialCallback();
        }
    }

    public void PushOn()
    {
        ReadyToContinue = false;
        for (var i = 0; i < Seats; i++)
        {
            if (!Players[i].In && !Players[i].Out)
            {
                if ((i == 0 && !AutoPlay) || DoesAIPlayerPush(i))
                {
                    PushOnPosition(i);
                }
                else
                {
                    CashOutPosition(i);
                }
            }
        }

        if (!Players[0].Out)
        {
            GameView.SetAllButtons(this, string.Empty, string.Empty, string.Empty);
        }

        if (GameView.Tutorial && HandsPlayed == 2 && CurrentRow == 5)
        {
            return;
        }

        CurrentRow++;
        CurrentCard += CurrentRow;
        GameView.DealCards(this, CurrentCard + 1 - CurrentRow, CurrentCard);

        if (Deck.RowIsBusted(CurrentRow))
        {
            if (AttemptToCheat())
            {
                CheatUsed = Deck.ExecuteCheat(CurrentCard);
                GameView.AnimateTheCheatCard(this);
            }
            else if (!BurnCardUsed)
            {
                if (HandsPlayed == 0)
                {
                    GameView.InsertTutorialCallback();
                    GameView.InsertTutorialCallback();
                }

                BurnCardUsed = Deck.UseBurnCard(CurrentRow);
                GameView.AnimateTheBurnCard(this);
            }

            if (Deck.RowIsBusted(CurrentRow))
            {
                GameView.BustTheRow(this);
                if (!AIOnlyGame)
                {
                    Action.RunOnce(this, SetReadyToTrue);
                }
                else
                {
                    ReadyToContinue = true;
                }

                return;
            }
        }

        CashOutValue = Deck.RowValue(CurrentRow) * Deck.TotalBonus(CurrentRow);
        if (Players[0].In)
        {
            GameView.SetPushOnAndCashOutButtons(this);
        }

        GameView.UpdateBonusState(this);

        if (CurrentRow == Deck.NumberOfRows && !BurnCardUsed)
        {
            WizardsTower.DebugToScreen("Player0.In = " + Players[0].In);
            CashOutValue = Deck.JackpotValue() * Deck.TotalBonus(CurrentRow);
            if (HandsPlayed == 1)
            {
                GameView.AddTutorialCallback();
            }

            GameView.HitTheJackpot(this);
            WizardsTower.DebugToScreen("Player0.In = " + Players[0].In);
            if (Players[0].In)
            {
                Jackpot = true;
            }
        }

        ClearAllPlayersInStatus();
        GameView.UpdateAllPlayerStatusInfo(this);

        if (Jackpot)
        {
            WizardsTower.DebugToScreen("Jackpot = true");
            GameView.SetWinAmount(this, CashOutValue * (double)Players[0].SafetyStakes / StakeUnit);
            Action.RunOnce(this, JackpotAchievement);
            Jackpot = false;
            if (HandsPlayed == 1)
            {
                GameView.AddTutorialCallback();
            }
        }

        if (!AIOnlyGame)
        {
            Action.RunOnce(this, SetReadyToTrue);
        }
        else
        {
            ReadyToContinue = true;
        }

        if (HandsPlayed == 0 && CurrentRow == 2)
        {
            GameView.InsertTutorialCallback();
            GameView.InsertTutorialCallback();
            GameView.InsertTutorialCallback();
        }

        if (HandsPlayed == 0 && CurrentRow == 3)
        {
            GameView.InsertTutorialCallback();
            GameView.InsertTutorialCallback();
        }

        if (HandsPlayed == 0 && CurrentRow == 4)
        {
            GameView.InsertTutorialCallback();
            GameView.InsertTutorialCallback();
            GameView.InsertTutorialCallback();
        }

        if (HandsPlayed == 1 && CurrentRow == 3)
        {
            GameView.InsertTutorialCallback();
            GameView.InsertTutorialCallback();
        }

        if (HandsPlayed == 1 && CurrentRow == 8)
        {
            GameView.InsertTutorialCallback();
        }

        if (HandsPlayed == 2 && CurrentRow == 5)
        {
            GameView.InsertTutorialCallback();
            GameView.InsertTutorialCallback();
        }
    }

    public void JackpotAchievement() => GameView.AchievementEarnedWT(this, "WT_Jackpot");

    public void CashOut()
    {
        ReadyToContinue = false;
        var allOut = true;
        for (var i = 0; i < Seats; i++)
        {
            if (Players[i].In)
            {
                allOut = false;
            }
            else if (!Players[i].Out)
            {
                if ((i == 0 && !AutoPlay) || !DoesAIPlayerPush(i))
                {
                    CashOutPosition(i);
                }
                else
                {
                    PushOnPosition(i);
                    allOut = false;
                }
            }
        }

        if (!allOut && !GameView.Tutorial)
        {
            PushOn();
            return;
        }

        GameView.SetAllButtons(this, "txtPlayAgain", string.Empty, GameView.YButtonText);
        if (!AIOnlyGame)
        {
            Action.RunOnce(this, SetReadyToTrue);
        }
        else
        {
            ReadyToContinue = true;
        }
    }

    public void HandOver()
    {
        if (!Players[0].Out && !AIOnlyGame && !GameView.Tournament)
        {
            GameView.IncrementSkillRating(Players[0].SafetyStakes);
        }

        ReadyToContinue = false;
        GameView.HandOver = true;
        HandsPlayed++;
        CashOutCount = 0;
        GameView.UpdateCashoutMarkers(this, 1, 0);
        GameView.UpdateTextGlow(this, 1);
        GameView.SetAllButtons(this, string.Empty, string.Empty, string.Empty);

        if (CurrentRow <= 1)
        {
            return;
        }

        for (var i = 0; i < Seats; i++)
        {
            if (i > 0 || AutoPlay || GameView.Tournament)
            {
                while (Players[i].Chips < Players[i].Stakes)
                {
                    Players[i].Stakes -= MinimumChipBuyIn;
                }
            }

            if (i > 0 || (AutoPlay && !GameView.Tutorial))
            {
                SetAIStakes(i);
            }

            if (Players[i].Chips >= MinimumChipBuyIn)
            {
                continue;
            }

            if (i == 0)
            {
                if (AutoPlay && !GameView.Tournament)
                {
                    GameView.BuyChipsForPlayer(Players[i], GameView.EffectiveBuyIn);
                    Players[i].Stakes = MinimumChipBuyIn;
                    GameView.UpdatePlayerInfo(this, i);
                }
            }
            else
            {
                if (Players[i].SeatEmpty && !GameView.Tournament && !GameView.Tutorial
                    && !GameView.NoSinglePlayerAIs)
                {
                    Players[i].SeatEmpty = false;
                    Player? info = GetNewAIPlayer();
                    if (info is not null)
                    {
                        GameView.PlayerInfo.UnseatPlayer(Players[i].ID);
                        Players[i].SetName(info.ID, info.Name);
                    }

                    GameView.PlayerEnters(this, i);
                    GameView.BuyChipsForPlayer(
                        Players[i],
                        GameView.EffectiveBuyIn
                            * (Math.Floor(_random.NextDouble() * GameView.HighAIBuyInMultiple)
                                + GameView.LowAIBuyInMultiple));
                    SetAIStakes(i);
                }
                else
                {
                    GameView.CashoutChipsForPlayer(Players[i], Players[i].Chips, i);
                    Players[i].SeatEmpty = true;
                    GameView.PlayerExits(this, i);
                }

                GameView.UpdatePlayerInfo(this, i);
            }
        }

        GameView.SetBetAmount(this, Players[0].Stakes);
        GameView.SetWinAmount(this, 0);
        GameView.SweepCards(this);
        ClearAllPlayersInStatus();
        ClearAllPlayersOutStatus();
        GameView.UpdateAllPlayerStatusInfo(this);

        if (!AIOnlyGame)
        {
            Action.RunOnce(this, Shuffle);
        }
        else
        {
            Shuffle();
        }

        GameView.SetButtonsAfterSweep(this);
        GameView.HandOver = false;
    }

    public void Shuffle()
    {
        GameView.NoPlayerBets = false;
        CurrentCard = 0;
        CurrentRow = 1;
        CashOutValue = 0;
        PlayerCashOutRow = 100;
        BurnCardUsed = false;
        CheatUsed = false;
        Deck.Shuffle();

        var start = 0;
        var sentinels = 0;
        int at;

        if (HandsPlayed > 0)
        {
            for (at = 0; at < GameView.StackedDeck.Count; at++)
            {
                if (GameView.StackedDeck[at] < 0)
                {
                    start = at + 1;
                    sentinels++;
                    if (sentinels == HandsPlayed)
                    {
                        break;
                    }
                }
            }
        }

        for (at = start;
             at < GameView.StackedDeck.Count && at - start < Deck.Cards.Count;
             at++)
        {
            if (GameView.StackedDeck[at] < 0)
            {
                break;
            }

            Deck.Cards[at - start] = GameView.StackedDeck[at];
        }

        if (GameView.AutoJackpot)
        {
            var value = 0;
            for (at = 0; at < Deck.Cards.Count; at++)
            {
                Deck.Cards[at] = value % (Deck.RangeOfCards + 1);
                value++;
            }
        }

        if (GameView.AutoBonus)
        {
            var rowSize = 1;
            var inRow = 0;
            for (at = 0; at < Deck.Cards.Count; at++)
            {
                inRow++;
                if (rowSize == (HandsPlayed % Deck.NumberOfCards) + 2)
                {
                    Deck.Cards[at] = (HandsPlayed % Deck.RangeOfCards) + 1;
                }

                if (inRow >= rowSize)
                {
                    rowSize++;
                    inRow = 0;
                }
            }
        }

        if (GameView.AutoUltimate)
        {
            var value = 0;
            var inRow = 0;
            for (at = 0; at < Deck.Cards.Count; at++)
            {
                inRow++;
                Deck.Cards[at] = value;
                if (inRow > value)
                {
                    value++;
                    inRow = 0;
                }

                if (value > Deck.RangeOfCards)
                {
                    break;
                }
            }
        }

        GameView.UpdateBonusState(this);

        if (HandsPlayed >= 1)
        {
            GameView.InsertTutorialCallback();
            if (GameView.Tutorial && HandsPlayed == 2)
            {
                Players[0].Stakes = 60;
                GameView.SetBetAmount(this, Players[0].Stakes);
            }
        }

        if (!AIOnlyGame)
        {
            Action.RunOnce(this, SetReadyToTrue);
        }
        else
        {
            ReadyToContinue = true;
        }
    }

    public void PlayHandPosition(int position)
    {
        if (Players[position].In || Players[position].Out)
        {
            return;
        }

        if (position == 0)
        {
            GameView.NoPlayerBets = true;
        }

        Players[position].SafetyStakes = Players[position].Stakes;
        GameView.BetChipsForPlayer(Players[position], Players[position].SafetyStakes);
        Players[position].In = true;
        if (Players[position].SafetyStakes >= MinimumChipBuyIn)
        {
            Players[position].HandsPlayed++;
        }

        GameView.UpdatePlayerInfo(this, position);
    }

    public void PushOnPosition(int position)
    {
        if (Players[position].In || Players[position].Out)
        {
            return;
        }

        Players[position].In = true;
        GameView.UpdatePlayerInfo(this, position, true);
    }

    public void CashOutPosition(int position)
    {
        if (Players[position].In || Players[position].Out)
        {
            return;
        }

        if (!Players[position].SeatEmpty)
        {
            CashOutCount++;
        }

        GameView.UpdateCashoutMarkers(this, CurrentRow, CashOutCount, position == 0);
        if (position == 0)
        {
            GameView.UpdateTextGlow(this, CurrentRow);
            PlayerCashOutRow = CurrentRow;
        }

        GameView.WinChipsForPlayer(
            Players[position], CashOutValue * ((double)Players[position].SafetyStakes / StakeUnit));
        Players[position].Out = true;
        GameView.UpdatePlayerInfo(this, position);

        if (position == 0 && !AllPlayersHaveCashedOut())
        {
            GameView.SetAllButtons(this, "txtSkipAhead", string.Empty, GameView.YButtonText);
        }

        if (position == 0)
        {
            GameView.SetWinAmount(
                this, CashOutValue * (double)Players[position].SafetyStakes / StakeUnit);
        }

        if (!Deck.RowIsBusted(CurrentRow) && !AIOnlyGame && !GameView.Tournament && position == 0)
        {
            GameView.IncrementSkillRating(Players[position].SafetyStakes);
        }
    }

    public bool AllPlayersHaveCashedOut()
    {
        for (var i = 0; i < Seats; i++)
        {
            if (!Players[i].Out)
            {
                return false;
            }
        }

        return true;
    }

    public void ClearAllPlayersInStatus()
    {
        for (var i = 0; i < Seats; i++)
        {
            Players[i].In = false;
        }
    }

    public void ClearAllPlayersOutStatus()
    {
        for (var i = 0; i < Seats; i++)
        {
            if (!Players[i].SeatEmpty)
            {
                Players[i].Out = false;
            }

            Players[i].ChipsWon = 0;
        }
    }

    public void SetAIStakes(int p)
    {
        double roll = Math.Floor(_random.NextDouble() * 100) + 1;
        if (Players[p].Personality == TowerPlayer.PersonalityTimid)
        {
            roll -= 55;
        }

        int multiple;
        if (roll <= 25)
        {
            multiple = 1;
        }
        else if (roll <= 40)
        {
            multiple = 2;
        }
        else if (roll <= 50)
        {
            multiple = 3;
        }
        else if (roll <= 60)
        {
            multiple = 4;
        }
        else if (roll <= 70)
        {
            multiple = 5;
        }
        else if (roll <= 80)
        {
            multiple = 6;
        }
        else if (roll <= 85)
        {
            multiple = 7;
        }
        else if (roll <= 90)
        {
            multiple = 8;
        }
        else if (roll <= 95)
        {
            multiple = 9;
        }
        else
        {
            multiple = 10;
        }

        double stake = MinimumChipBuyIn * multiple;
        if (Players[p].Personality == TowerPlayer.PersonalitySmart && stake > Players[p].Chips * 0.5)
        {
            stake = Players[p].Chips * 0.5;
        }
        else if (Players[p].Personality == TowerPlayer.PersonalityCautious
            && stake > Players[p].Chips * 0.2)
        {
            stake = Players[p].Chips * 0.2;
        }
        else if (Players[p].Personality == TowerPlayer.PersonalityTimid
            && stake > Players[p].Chips * 0.2)
        {
            stake = Players[p].Chips * 0.2;
        }

        Players[p].Stakes = (int)Math.Floor(stake / MinimumChipBuyIn) * MinimumChipBuyIn;
        if (Players[p].Stakes > GameView.MaxBet)
        {
            Players[p].Stakes = GameView.MaxBet;
        }
        else if (Players[p].Stakes < MinimumChipBuyIn && Players[p].Chips >= MinimumChipBuyIn)
        {
            Players[p].Stakes = MinimumChipBuyIn;
        }
    }

    public bool AnyUndecidedAIs()
    {
        for (var i = 1; i < Seats; i++)
        {
            if (!Players[i].In && !Players[i].Out)
            {
                return true;
            }
        }

        return false;
    }

    public bool DoesAIPlayerPush(int position)
    {
        if (CurrentRow == Deck.NumberOfRows)
        {
            return false;
        }

        int at = CurrentRow - 2;
        if (at < 0)
        {
            at = 0;
        }

        if (BurnCardUsed)
        {
            at += 8;
        }

        double threshold = at >= 0 && at < PlayTable.Count ? PlayTable[at] : double.NaN;
        double value;

        if (Players[position].Personality == TowerPlayer.PersonalityAggressive || GameView.Tutorial)
        {
            value = 0.75 - (_random.NextDouble() * 0.5);
            if (BurnCardUsed)
            {
                value = 1 - (_random.NextDouble() * 0.75);
            }
            else
            {
                value /= Deck.TotalBonus(CurrentRow);
            }

            value *= Deck.RowValue(CurrentRow);
            if (GameView.Tutorial)
            {
                value = 5;
            }

            if (value < threshold)
            {
                return true;
            }
        }
        else if (Players[position].Personality == TowerPlayer.PersonalityCautious)
        {
            value = 1 - (_random.NextDouble() * 0.5);
            if (BurnCardUsed)
            {
                value *= 1.5;
            }

            value *= Deck.RowValue(CurrentRow) * Deck.TotalBonus(CurrentRow);
            if (value < StakeUnit)
            {
                return true;
            }
        }
        else if (Players[position].Personality == TowerPlayer.PersonalityTimid)
        {
            value = 1 - (_random.NextDouble() * 0.25);
            if (BurnCardUsed)
            {
                value *= 1.5;
            }

            value *= Deck.RowValue(CurrentRow) * Deck.TotalBonus(CurrentRow);
            if (value < StakeUnit)
            {
                return true;
            }
        }
        else if (Players[position].Personality == TowerPlayer.PersonalitySmart)
        {
            value = 0.75 + (_random.NextDouble() * 0.5);
            value *= Deck.RowValue(CurrentRow);
            if (value < threshold)
            {
                return true;
            }
        }

        return false;
    }

    public void AIUpdate()
    {
        if (!ReadyToContinue || AllPlayersHaveCashedOut() || GameView.TournamentOver)
        {
            return;
        }

        int first = (int)Math.Floor(_random.NextDouble() * 4) + 1;
        for (int i = first; i < first + 4; i++)
        {
            int seat = (i % 4) + 1;
            if (!Players[seat].In && !Players[seat].Out)
            {
                if (CurrentRow == 1)
                {
                    PlayHandPosition(seat);
                }
                else if (DoesAIPlayerPush(seat))
                {
                    PushOnPosition(seat);
                }
                else
                {
                    CashOutPosition(seat);
                }

                GameView.PlayAIDecisionSound(this, seat);
                break;
            }
        }

        double delay = 0.5 + _random.NextDouble();
        if (Players[0].Out || CurrentRow == 1)
        {
            delay *= 0.5;
        }

        if (AnyUndecidedAIs())
        {
            if (!AIOnlyGame)
            {
                Action.RunDelayed(this, AIUpdate, delay);
            }
            else
            {
                AIUpdate();
            }
        }
        else if (AllPlayersHaveCashedOut())
        {
            CashOut();
        }
    }

    public Player? GetNewAIPlayer()
    {
        Player? player = GameView.PlayerInfo.GetNewUnseatedPlayer(_random);
        if (player is not null)
        {
            player.IsSeated = true;
        }

        return player;
    }

    public bool AttemptToCheat() => false;
}
