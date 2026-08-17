using System.Collections.Generic;

namespace Refii.FlashGames.MageDice;

public sealed class GameVariant
{
    private readonly List<int>[] _reels = new List<int>[6];

    public GameVariant(
        string n, int gtype, int amt, List<string> images, List<int> types,
        List<List<int>> pay,
        List<int> r0, List<int> r1, List<int> r2, List<int> r3, List<int> r4, List<int> r5)
    {
        Name = n;
        GameType = gtype;
        Cost = amt;
        ReelImages = images;
        Types = types;
        Payouts = pay;
        _reels[0] = r0;
        _reels[1] = r1;
        _reels[2] = r2;
        _reels[3] = r3;
        _reels[4] = r4;
        _reels[5] = r5;

        for (var i = 0; i < types.Count; i++)
        {
            if (types[i] == Token.TypeBonusReel)
            {
                HasBonusReel = true;
            }
            else if (types[i] == Token.TypeFreeSpin)
            {
                HasBonusSpin = true;
            }
            else if (types[i] == Token.TypeBonusMult)
            {
                HasBonusMult = true;
            }
            else if (TokenPays(i, 2) != 0)
            {
                ChainPaysOnTwo = true;
            }
        }
    }

    public string Name { get; }

    public int GameType { get; }

    public int Cost { get; }

    public List<string> ReelImages { get; }

    public List<int> Types { get; }

    public List<List<int>> Payouts { get; }

    public bool HasBonusReel { get; }

    public bool HasBonusSpin { get; }

    public bool HasBonusMult { get; }

    public bool ChainPaysOnTwo { get; }

    public List<int> Reel(int index) => _reels[index];

    public int TokenType(int idx) => Types[idx];

    public string TokenImage(int idx) => ReelImages[idx];

    public int TokenPays(int token, int quantity)
    {
        var pays = 0;
        if (quantity <= 0)
        {
            return pays;
        }

        bool beyondTable =
            quantity < Payouts.Count && quantity > Payouts[quantity].Count;
        if (!beyondTable && quantity - 1 < Payouts.Count && token < Payouts[quantity - 1].Count)
        {
            pays = Payouts[quantity - 1][token];
        }

        return pays;
    }

    public int FindType(int t)
    {
        for (var i = 0; i < Types.Count; i++)
        {
            if (Types[i] == t)
            {
                return i;
            }
        }

        return -1;
    }
}
