using System.Collections.Generic;

using Refii.Titles.Fable2.PubGames.Framework;

namespace Refii.Titles.Fable2.PubGames.MageDice;

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

    public int Value { get; protected set; } = -1;

    public int LastValue { get; protected set; } = -1;

    public bool Visited { get; set; }

    public virtual void Dispose()
    {
        Neighbors.Clear();
    }

    public void AddNeighbor(Switch s1) => Neighbors.Add(s1);

    public virtual void NewSpin()
    {
    }

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
