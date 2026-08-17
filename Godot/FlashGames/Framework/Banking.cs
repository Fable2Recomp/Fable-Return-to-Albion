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
    public const double UpDownNavDelay1 = 500;
    public const double UpDownNavDelay2 = 100;
    public const double LeftRightNavDelay1 = 300;

    private readonly PubGame _game;

    public Banking(PubGame game)
    {
        _game = game;
        Player? human = game.PlayerInfo.GetPlayer(0);
        StartingChips = human?.Chips ?? 0;
        StartingGold = human?.Gold ?? 0;
        StartingDebt = human?.Debt ?? 0;
    }

    public int StartingChips { get; }

    public int StartingGold { get; }

    public int StartingDebt { get; }

    public int TotalWinnings()
    {
        Player? human = Human;
        if (human is null)
        {
            return 0;
        }

        return human.Chips - StartingChips + human.Gold - StartingGold -
            human.Debt + StartingDebt;
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

    public int ChipsInPlay => Human?.Chips ?? 0;

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

    public static BankOption Step(BankOption option, int direction)
    {
        int next = (int)option + direction;
        if (next < (int)FirstOption)
        {
            return LastOption;
        }

        if (next > (int)LastOption)
        {
            return FirstOption;
        }

        return (BankOption)next;
    }
}
