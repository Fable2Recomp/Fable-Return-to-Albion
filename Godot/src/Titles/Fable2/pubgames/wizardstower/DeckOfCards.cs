using System;
using System.Collections.Generic;

using Refii.Titles.Fable2.PubGames.Framework;

namespace Refii.Titles.Fable2.PubGames.WizardsTower;

public sealed class DeckOfCards
{
    private readonly IPubRandom _random;

    public DeckOfCards(
        int rangeOfCards, int numberOfCards, int numberOfWarriors, int numberOfRows,
        IPubRandom random)
    {
        _random = random;
        RangeOfCards = rangeOfCards;
        NumberOfCards = numberOfCards;
        NumberOfWarriors = numberOfWarriors;
        NumberOfRows = numberOfRows;

        Cards = new List<int>(numberOfWarriors + (rangeOfCards * numberOfCards));
        for (var i = 0; i < NumberOfWarriors; i++)
        {
            Cards.Add(0);
        }

        for (var value = 1; value <= RangeOfCards; value++)
        {
            for (var copy = 0; copy < NumberOfCards; copy++)
            {
                Cards.Add(value);
            }
        }

        TotalCards = Cards.Count;
    }

    public List<int> Cards { get; }

    public int TotalCards { get; }

    public int RangeOfCards { get; }

    public int NumberOfCards { get; }

    public int NumberOfWarriors { get; }

    public int NumberOfRows { get; }

    public int BurnedCard { get; private set; }

    public int CheatCard { get; private set; }

    public void Shuffle()
    {
        for (var pass = 0; pass < 7; pass++)
        {
            for (int remaining = TotalCards; remaining > 0; remaining--)
            {
                var at = (int)Math.Floor(_random.NextDouble() * remaining);
                int card = Cards[at];
                Cards.RemoveAt(at);
                Cards.Add(card);
            }

            BurnedCard = 0;
        }
    }

    public int FindLastHero()
    {
        for (int i = TotalCards - 1; i >= 0; i--)
        {
            if (Cards[i] == 0)
            {
                return i;
            }
        }

        return -1;
    }

    public bool ExecuteCheat(int replace)
    {
        int hero = FindLastHero();
        if (hero <= replace)
        {
            return false;
        }

        Cards[hero] = Cards[TotalCards - 1];
        Cards[TotalCards - 1] = Cards[replace];
        Cards[replace] = 0;
        CheatCard = replace;
        return true;
    }

    public bool UseBurnCard(int row)
    {
        if (!RowIsBusted(row))
        {
            return false;
        }

        int above = row - 1;
        int start = RowIndex(above);
        int end = start + above - 1;
        var firstOffender = 0;

        for (int i = start; i <= end; i++)
        {
            if (Cards[i] == Cards[i + above] && firstOffender == 0)
            {
                firstOffender = i + above;
            }

            if (Cards[i] == Cards[i + above + 1] && firstOffender == 0)
            {
                firstOffender = i + above + 1;
            }
        }

        if (firstOffender <= 0)
        {
            return false;
        }

        (Cards[firstOffender], Cards[0]) = (Cards[0], Cards[firstOffender]);
        BurnedCard = firstOffender;
        return true;
    }

    public static int RowIndex(int row)
    {
        var index = 0;
        for (var i = 1; i < row; i++)
        {
            index += i;
        }

        return index;
    }

    public bool RowHasWarrior(int row)
    {
        int start = RowIndex(row);
        int end = start + row - 1;
        for (int i = start; i <= end; i++)
        {
            if (Cards[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    public bool StackedPair(int row)
    {
        if (row <= 2)
        {
            return false;
        }

        int above = row - 1;
        int start = RowIndex(above);
        int end = start + above - 1;

        for (int i = start; i <= end; i++)
        {
            if (Cards[i] == Cards[i + above] || Cards[i] == Cards[i + above + 1])
            {
                return true;
            }
        }

        return false;
    }

    public bool IsCardStacked(int card)
    {
        if (Cards[card] == 0)
        {
            return false;
        }

        var row = 1;
        var rowStart = 0;
        var rowEnd = 0;
        while (rowEnd < card)
        {
            rowStart += row;
            rowEnd += row + 1;
            row++;
        }

        if (row <= 2)
        {
            return false;
        }

        if (card != rowStart && Cards[card] == Cards[card - row])
        {
            return true;
        }

        return card != rowEnd && Cards[card] == Cards[card - row + 1];
    }

    public bool RowIsBusted(int row) =>
        !RowHasWarrior(row) && StackedPair(row) && RowBonus(row) == 1;

    public int RowValue(int row)
    {
        int start = RowIndex(row);
        int end = start + row - 1;
        var total = 0;
        for (int i = start; i <= end; i++)
        {
            total += Cards[i];
        }

        return total;
    }

    public int JackpotValue()
    {
        var total = 0;
        for (var row = 2; row <= NumberOfRows; row++)
        {
            total += RowValue(row);
        }

        return total;
    }

    public int RowBonus(int row)
    {
        int start = RowIndex(row);
        int end = start + row - 1;
        int first = Cards[start];

        for (int i = start; i <= end; i++)
        {
            if (first != Cards[i])
            {
                return 1;
            }
        }

        return row;
    }

    public int TotalBonus(int row)
    {
        var best = 0;
        for (var i = 1; i <= row; i++)
        {
            int bonus = RowBonus(i);
            if (bonus > best)
            {
                best = bonus;
            }
        }

        return best < 1 ? 1 : best;
    }
}
