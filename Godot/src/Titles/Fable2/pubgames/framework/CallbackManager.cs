using System.Collections.Generic;

namespace Refii.Titles.Fable2.PubGames.Framework;

public sealed class CallbackManager
{
    private readonly List<Callback> _callbacks = new();
    private readonly IPubTimer _timer;
    private double _lastTick;
    private double _speed;
    private int _lastCallbackThisFrame;

    public CallbackManager(IPubTimer timer)
    {
        _timer = timer;
        ResetTimer();
        _speed = 1;
        _lastCallbackThisFrame = 0;
    }

    public double Speed
    {
        get => _speed;
        set => _speed = value;
    }

    public int Count => _callbacks.Count;

    public IReadOnlyList<Callback> Callbacks => _callbacks;

    public void AddCallbackObject(Callback callback)
    {
        _callbacks.Add(callback);
    }

    /// <summary>
    /// Puts <paramref name="callback"/> ahead of everything already queued. The one caller is
    /// <see cref="PubGame.RegisterOverlayManager"/>: <c>PubGame::Initialize</c> empties the list
    /// and adds the overlay manager first (0x0015ff-0x00161b), so it runs, and blocks, before any
    /// game callback.
    /// </summary>
    public void AddCallbackObjectFirst(Callback callback)
    {
        _callbacks.Remove(callback);
        _callbacks.Insert(0, callback);
    }

    public Callback AddCallbackFunction(
        PubCallbackMethod? callbackMethod,
        object? callbackData,
        object? group = null,
        PubPredicateMethod? blockingMethod = null,
        PubPredicateMethod? abortMethod = null)
    {
        var callback = new Callback();
        if (callbackMethod is not null)
        {
            callback.CallbackMethod = callbackMethod;
        }

        callback.CallbackData = callbackData;
        callback.Group = group;
        if (blockingMethod is not null)
        {
            callback.BlockingMethod = blockingMethod;
        }

        if (abortMethod is not null)
        {
            callback.AbortMethod = abortMethod;
        }

        _callbacks.Add(callback);
        return callback;
    }

    public void RemoveAllCallbacks(object? group = null)
    {
        if (group is null)
        {
            for (int i = _callbacks.Count - 1; i >= 0; i--)
            {
                _lastCallbackThisFrame--;
                _callbacks[i].Dispose();
            }

            _callbacks.Clear();
            return;
        }

        for (int i = _callbacks.Count - 1; i >= 0; i--)
        {
            if (Equals(_callbacks[i].Group, group))
            {
                _lastCallbackThisFrame--;
                _callbacks[i].Dispose();
                _callbacks.RemoveAt(i);
            }
        }
    }

    public void ResetTimer()
    {
        _lastTick = _timer.Milliseconds;
    }

    public bool IsGroupInUse(object? group)
    {
        foreach (Callback callback in _callbacks)
        {
            if (Equals(callback.Group, group))
            {
                return true;
            }
        }

        return false;
    }

    public bool DoCallbacks(double? ticks = null, object? callbackData = null)
    {
        var blockedGroups = new HashSet<object>();
        var ranGroups = new HashSet<object>();
        bool ranUngrouped = false;

        double elapsed;
        if (ticks is { } given)
        {
            elapsed = given;
        }
        else
        {
            double now = _timer.Milliseconds;
            elapsed = now - _lastTick;
            _lastTick = now;
        }

        elapsed *= _speed;

        _lastCallbackThisFrame = _callbacks.Count;
        for (int i = 0; i < _lastCallbackThisFrame && i < _callbacks.Count; i++)
        {
            Callback callback = _callbacks[i];
            callbackData = callback.CallbackData;
            object? group = callback.Group;
            bool blocked = ranUngrouped;
            bool removed = false;

            if (group is not null)
            {
                if (blockedGroups.Contains(group))
                {
                    continue;
                }

                blocked = ranGroups.Contains(group);
            }

            if (callback.CallbackMethod is not null &&
                callback.CallbackMethod(elapsed, callbackData, blocked))
            {
                // `callbacks.splice(i, 1)` (0x0007bc): a callback that emptied the list while it
                // ran (GameReallyOver -> CleanUpGame -> RemoveAllCallbacks) leaves nothing there,
                // and AS2's splice past the end is a no-op.
                if (i < _callbacks.Count)
                {
                    _callbacks.RemoveAt(i);
                }

                removed = true;
                i--;
                _lastCallbackThisFrame--;
            }
            else if (group is not null)
            {
                ranGroups.Add(group);
            }
            else
            {
                ranUngrouped = true;
            }

            if (callback.AbortMethod is not null && callback.AbortMethod(callbackData))
            {
                RemoveAllCallbacks(group);
            }
            else if ((callback.BlockingMethod is not null && callback.BlockingMethod(callbackData)) ||
                     (callback.BlockingMethod is null && callback.Blocking))
            {
                if (group is null)
                {
                    if (removed)
                    {
                        callback.Dispose();
                    }

                    break;
                }

                blockedGroups.Add(group);
            }

            if (removed)
            {
                callback.Dispose();
            }
        }

        return _callbacks.Count == 0;
    }
}
