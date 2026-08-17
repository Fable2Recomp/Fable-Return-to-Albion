using System;

namespace Refii.FlashGames.Framework;

public interface IPubRandom
{
    double NextDouble();
}

public sealed class PubRandom : IPubRandom
{
    private ulong _state;

    public PubRandom(ulong seed)
    {
        _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
    }

    public double NextDouble()
    {
        _state ^= _state << 13;
        _state ^= _state >> 7;
        _state ^= _state << 17;
        return (_state >> 11) * (1.0 / 9007199254740992.0);
    }
}

public sealed class PubScriptedRandom : IPubRandom
{
    private readonly double[] _values;
    private int _index;

    public PubScriptedRandom(params double[] values)
    {
        _values = values.Length == 0 ? new double[] { 0 } : values;
    }

    public double NextDouble()
    {
        double value = _values[_index % _values.Length];
        _index++;
        return value;
    }
}

public static class PubRandomExtensions
{
    public static int Range(this IPubRandom random, int countExclusive) =>
        (int)Math.Floor(random.NextDouble() * countExclusive);
}
