using System;

namespace Refii.FlashGames.Framework;

public enum BankOption
{
    Cancel = -1,
    QuickChips = 0,
    GetChips = 1,
    PayDebtWithGold = 2,
    CashOut = 3,
}

public sealed class Banking
{
    public const BankOption FirstOption = BankOption.QuickChips;
    public const BankOption LastOption = BankOption.CashOut;

    private readonly PubGame _game;
    private readonly Func<int>? _chipsInPlay;

    public Banking(PubGame game)
        : this(game, null)
    {
    }

    /// <param name="chipsInPlay">
    /// <c>OverlayManager::GetChipsInPlay</c>, which a game overrides to count chips it has staked
    /// (Keystone: chips + bets). Null reads the player's chips, the base implementation (0x002bb8).
    /// </param>
    public Banking(PubGame game, Func<int>? chipsInPlay)
    {
        _game = game;
        _chipsInPlay = chipsInPlay;
        Snapshot();
    }

    /// <summary>
    /// Takes the balance the session is measured against.
    /// </summary>
    /// <remarks>
    /// TAKEN AFTER THE BUY-IN, NOT BEFORE, and the SWF is what says so: <c>OverlayManager::Init</c>
    /// reads <c>startingChips</c>, <c>startingGold</c> and <c>startingDebt</c> off player 0, and
    /// <c>PubGame::Run</c> calls it AFTER <c>Chips += buyIn</c> and <c>Debt += buyIn</c>. Snapping
    /// it a moment earlier leaves <c>TotalBorrowed</c> — the "you borrowed N" line on the cash-out
    /// card — overstated by exactly the buy-in, because that term already adds <c>BuyIn</c> back on
    /// its own and would then be counting it twice.
    ///
    /// <c>TotalWinnings</c> survives the early snapshot by luck rather than design: it subtracts
    /// the starting chips and adds back the starting debt, and the buy-in moves both by the same
    /// amount. That is not a reason to take it early.
    /// </remarks>
    public void Snapshot()
    {
        Player? human = _game.PlayerInfo.GetPlayer(0);
        StartingChips = human?.Chips ?? 0;
        StartingGold = human?.Gold ?? 0;
        StartingDebt = human?.Debt ?? 0;
    }

    public int StartingChips { get; private set; }

    public int StartingGold { get; private set; }

    public int StartingDebt { get; private set; }

    /// <summary><c>OverlayManager::TotalWinnings(addChipsInPlay)</c> (0x002e0c-0x002e81).</summary>
    public int TotalWinnings(bool addChipsInPlay = false)
    {
        Player? human = Human;
        if (human is null)
        {
            return 0;
        }

        return (addChipsInPlay ? ChipsInPlay : human.Chips) - StartingChips + human.Gold -
            StartingGold - human.Debt + StartingDebt;
    }

    public int TotalBorrowed()
    {
        Player? human = Human;
        if (human is null)
        {
            return 0;
        }

        return Math.Max(
            human.Debt - StartingDebt + _game.BuyIn + StartingGold - human.Gold, 0);
    }

    private Player? Human => _game.PlayerInfo.GetPlayer(0);

    public int ChipsInPlay => _chipsInPlay?.Invoke() ?? Human?.Chips ?? 0;

    public bool CasualGame => !_game.Tutorial && !_game.Tournament;

    public int GetCredit()
    {
        Player? human = Human;
        if (human is null)
        {
            return 0;
        }

        int chips = ChipsInPlay;
        int starIndex = human.StarRating - 1;
        int starCredit = starIndex >= 0 && starIndex < _game.StarCredit.Count
            ? _game.StarCredit[starIndex]
            : 0;

        return Math.Max(Math.Max(human.Gold + chips - human.Debt, starCredit) - chips, 0);
    }

    public int QuickChipsAmount() =>
        CasualGame ? Math.Min(GetCredit(), _game.BuyIn) : 0;

    public bool IsEnabled(BankOption option)
    {
        Player? human = Human;
        if (human is null)
        {
            return false;
        }

        return option switch
        {
            BankOption.QuickChips => CasualGame && QuickChipsAmount() > 0,
            BankOption.GetChips => CasualGame && GetCredit() > 0,
            BankOption.PayDebtWithGold => CasualGame && human.Gold > 0 && human.Debt > 0,
            BankOption.CashOut => true,
            _ => false,
        };
    }

    public int SliderMax(BankOption option)
    {
        Player? human = Human;
        if (human is null)
        {
            return 0;
        }

        if (option == BankOption.GetChips)
        {
            int ceiling = _game.StarCredit.Count >= 5 ? _game.StarCredit[4] * 10 : int.MaxValue;
            return Math.Min(GetCredit(), ceiling);
        }

        if (option == BankOption.PayDebtWithGold)
        {
            return Math.Min(human.Gold, human.Debt);
        }

        return 0;
    }

    public bool Commit(BankOption option, int amount)
    {
        Player? human = Human;
        if (human is null || !IsEnabled(option))
        {
            return false;
        }

        switch (option)
        {
            case BankOption.QuickChips:
            {
                int quick = QuickChipsAmount();
                if (quick <= 0)
                {
                    return false;
                }

                human.Chips += quick;
                human.Debt += quick;
                _game.UpdatePlayerHoldings();
                return true;
            }

            case BankOption.GetChips:
            {
                if (amount <= 0 || amount > SliderMax(option))
                {
                    return false;
                }

                human.Chips += amount;
                human.Debt += amount;
                _game.UpdatePlayerHoldings();
                return true;
            }

            case BankOption.PayDebtWithGold:
            {
                if (amount <= 0 || amount > SliderMax(option))
                {
                    return false;
                }

                human.Gold -= amount;
                human.Debt -= amount;
                _game.UpdatePlayerHoldings();
                return true;
            }

            default:
                return false;
        }
    }
}
