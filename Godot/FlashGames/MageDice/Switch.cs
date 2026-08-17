using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.MageDice;

/// <summary>
/// One reel: the symbol it is showing, and which other reels it is adjacent to.
/// </summary>
/// <remarks>
/// "SWITCH" IS THE SHIPPED CODE'S WORD for a reel position, and it is kept. The board wires them
/// into a graph — <see cref="AddNeighbor"/> — and a payout is a connected run of reels showing the
/// same token, so adjacency is the whole geometry of the game. A three-reel board is fully
/// connected; a six-reel board is not, which is why matching six is not the same as matching three
/// twice.
///
/// A 1:1 port of <c>Switch.as</c>, with one substitution: the shipped <see cref="Roll"/> calls
/// <c>Math.random()</c>, and here the source is an <see cref="IPubRandom"/> so a seeded session
/// replays — the same swap the Keystone port makes.
/// </remarks>
public class Switch
{
    private readonly List<int> _reel;
    private readonly IPubRandom _random;

    public Switch(GameVariant variant, int id, int reeltype, int value, IPubRandom random)
    {
        Neighbors = new List<Switch>();
        ID = id;
        _reel = variant.Reel(reeltype);
        _random = random;
        Value = value;
        LastValue = value;
    }

    public int ID { get; }

    public List<Switch> Neighbors { get; }

    /// <summary>The token currently showing.</summary>
    public int Value { get; protected set; } = -1;

    /// <summary>What it was showing before the last roll, so the reel can spin from it.</summary>
    public int LastValue { get; protected set; } = -1;

    /// <summary>
    /// Scratch space for the board's flood fill.
    /// </summary>
    /// <remarks>
    /// A DYNAMIC PROPERTY IN THE SHIPPED CODE — <c>GameBoard.Update</c> writes
    /// <c>switch.visited</c> onto a Switch that never declares it. A field here says the same thing
    /// and lets the compiler check it.
    /// </remarks>
    public bool Visited { get; set; }

    public virtual void Dispose()
    {
        Neighbors.Clear();
    }

    public void AddNeighbor(Switch s1) => Neighbors.Add(s1);

    public virtual void NewSpin()
    {
    }

    /// <summary>
    /// Draws a new symbol.
    /// </summary>
    /// <remarks>
    /// THE STOP TABLE IS THE ODDS. A reel's list is not the symbol set — it is a weighted bag, with
    /// each token repeated as often as it should come up: <c>Reel1Stops</c> on CowAndCorset1 is 23
    /// entries over 9 symbols, with token 8 appearing seven times and token 4 once. Drawing
    /// uniformly from THIS list is what makes the table's odds its own.
    /// </remarks>
    public void Roll()
    {
        LastValue = Value;
        Value = _reel[(int)System.Math.Floor(_random.NextDouble() * _reel.Count)];
    }

    public void DebugForceValue(int x)
    {
        LastValue = Value;
        Value = x;
    }
}
