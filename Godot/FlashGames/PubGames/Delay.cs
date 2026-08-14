namespace Refii.FlashGames.PubGames;

public sealed class Delay : Callback
{
    private double _t;
    private readonly PubEndCallback? _callback;
    private readonly object? _userData;

    public Delay(
        double delay,
        PubEndCallback? endCallback,
        object? endUserData,
        object? group,
        bool blocking)
    {
        _t = delay;
        _callback = endCallback;
        _userData = endUserData;
        Group = group;
        Blocking = blocking;
    }

    public override bool Run(double ticks, object? callbackData, bool blocked)
    {
        _t -= ticks;
        bool done = _t <= 0;
        if (done)
        {
            _callback?.Invoke(_userData);
        }

        return done;
    }
}
