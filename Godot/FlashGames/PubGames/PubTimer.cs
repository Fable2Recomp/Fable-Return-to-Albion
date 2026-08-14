using System.Diagnostics;

namespace Refii.FlashGames.PubGames;

public interface IPubTimer
{
    double Milliseconds { get; }
}

public sealed class PubStopwatchTimer : IPubTimer
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public double Milliseconds => _stopwatch.Elapsed.TotalMilliseconds;
}

public sealed class PubManualTimer : IPubTimer
{
    public double Milliseconds { get; private set; }

    public void Advance(double milliseconds)
    {
        Milliseconds += milliseconds;
    }
}
