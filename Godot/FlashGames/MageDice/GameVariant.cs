using System.Collections.Generic;

namespace Refii.FlashGames.MageDice;

/// <summary>
/// One Mage Dice table: its reels, its symbols and what they pay.
/// </summary>
/// <remarks>
/// A VARIATION IS A WHOLE MACHINE, not a few tuning numbers. Each of the 41 variations in
/// <c>magedice/settings.xml</c> carries its own symbol list (<c>ReelImages</c>), its own symbol
/// types (<c>ReelTypes</c>), six weighted stop tables (<c>Reel1Stops</c>..<c>Reel6Stops</c>) and
/// six payout tables indexed by how many matched (<c>ReelPayoutsx1</c>..<c>x6</c>). A table with
/// three reels and one with six are different games sharing a screen.
///
/// A 1:1 port of <c>GameVariant.as</c>.
/// </remarks>
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
                // A TABLE WHERE TWO IN A ROW ALREADY PAY. The board reads this to decide whether a
                // pair is worth animating as a win at all.
                ChainPaysOnTwo = true;
            }
        }
    }

    public string Name { get; }

    /// <summary>How many reels this table has: 3, 4 or 6.</summary>
    public int GameType { get; }

    /// <summary>The stake for one spin at bet multiple 1.</summary>
    public int Cost { get; }

    /// <summary>The symbol artwork names, indexed by token.</summary>
    public List<string> ReelImages { get; }

    /// <summary>Each token's <see cref="Token"/> type, indexed by token.</summary>
    public List<int> Types { get; }

    /// <summary>Payout tables by match count: <c>Payouts[n - 1][token]</c> pays for n matched.</summary>
    public List<List<int>> Payouts { get; }

    public bool HasBonusReel { get; }

    public bool HasBonusSpin { get; }

    public bool HasBonusMult { get; }

    public bool ChainPaysOnTwo { get; }

    /// <summary>The weighted stop table a reel draws from.</summary>
    public List<int> Reel(int index) => _reels[index];

    public int TokenType(int idx) => Types[idx];

    public string TokenImage(int idx) => ReelImages[idx];

    /// <summary>
    /// What <paramref name="quantity"/> of <paramref name="token"/> pays.
    /// </summary>
    /// <remarks>
    /// THE BOUNDS TEST READS ONE TABLE PAST THE VALUE, and that is not a typo here — it is what
    /// the bytecode does: <c>if (quantity &gt; payouts[quantity].length) skip</c>, then read
    /// <c>payouts[quantity - 1][token]</c>. For a six-reel table and six matched, <c>payouts[6]</c>
    /// is off the end of a six-entry list; ActionScript yields undefined, <c>6 &gt; undefined</c>
    /// is FALSE, and the read goes ahead. So the top tier pays.
    ///
    /// THE DECOMPILER INVERTS THIS. It renders the guard as
    /// <c>quantity &lt;= payouts[quantity].length</c>, which is not the same thing once undefined
    /// is involved — <c>6 &lt;= undefined</c> is false, so following the decompiled source makes a
    /// six-of-a-kind, the biggest hand a six-reel table can produce, pay nothing at all.
    /// </remarks>
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

    /// <summary>The first token of a given type, or -1.</summary>
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
