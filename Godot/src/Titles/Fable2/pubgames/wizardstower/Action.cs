using System;
using System.Collections.Generic;

using Refii.Titles.Fable2.PubGames.Framework;

namespace Refii.Titles.Fable2.PubGames.WizardsTower;

public static class ActionPool<T>
    where T : Action, new()
{
    private static readonly List<T> Preallocated = new();
    private static int _lastUsed;

    static ActionPool() => Action.Register(Deallocate);

    public static void Preallocate(int size)
    {
        while (Preallocated.Count < size)
        {
            Preallocated.Add(new T { InUse = false });
        }
    }

    public static void Deallocate()
    {
        foreach (T action in Preallocated)
        {
            action.Destroy();
        }

        Preallocated.Clear();
        _lastUsed = 0;
    }

    public static T GetFromArray()
    {
        for (var offset = 0; offset < Preallocated.Count; offset++)
        {
            int index = (_lastUsed + offset) % Preallocated.Count;
            if (!Preallocated[index].InUse)
            {
                Preallocated[index].InUse = true;
                _lastUsed = index;
                return Preallocated[index];
            }
        }

        var grown = new T { InUse = true };
        Preallocated.Add(grown);
        _lastUsed = Preallocated.Count - 1;
        return grown;
    }
}

public class Action : Callback
{
    public const int StyleLinear = 0;
    public const int StyleEaseIn = 1;
    public const int StyleEaseOut = 2;

    private static readonly List<System.Action> Pools = new();

    public static CallbackManager? DefaultCallbackManager { get; set; }

    public Action() => Reset();

    public bool InUse { get; set; }

    public string? DebugString { get; set; }

    public double PercentDone { get; set; }

    public double Time { get; set; }

    public double? Duration { get; set; }

    public int AnimStyle { get; set; }

    public System.Action<object?>? FunctionToRunOnce { get; set; }

    public object? FunctionData { get; set; }

    public Action? SynchedAction { get; set; }

    public bool RunMethodOverride { get; set; }

    internal static void Register(System.Action deallocate) => Pools.Add(deallocate);

    public static void Deallocate()
    {
        foreach (System.Action pool in Pools)
        {
            pool();
        }

        DefaultCallbackManager = null;
    }

    public static void Preallocate(int size) => ActionPool<Action>.Preallocate(size);

    public static void SetGameSpeed(double speed)
    {
        if (DefaultCallbackManager is not null)
        {
            DefaultCallbackManager.Speed = speed;
        }
    }

    public static Action RunUntilTrue(object? caller, PubCallbackMethod method, object? group = null) =>
        AddFunction(caller, method, 0, group);

    public static Action RunOnce(object? caller, System.Action method, object? group = null)
    {
        Action action = AddFunction(caller, null, 0, group);
        action.FunctionToRunOnce = _ => method();
        return action;
    }

    public static Action RunOnceWithData(
        object? caller, System.Action<object?> method, object? functionData, object? group = null)
    {
        Action action = AddFunction(caller, null, 0, group);
        action.FunctionToRunOnce = method;
        action.FunctionData = functionData;
        return action;
    }

    public static Action RunDelayed(
        object? caller, System.Action method, double delay, object? group = null)
    {
        Action action = AddFunction(caller, null, delay, group);
        action.FunctionToRunOnce = _ => method();
        return action;
    }

    public static Action AddFunction(
        object? caller, PubCallbackMethod? method, double delay, object? group = null)
    {
        Action action = ActionPool<Action>.GetFromArray();
        action.Init(group, delay);
        if (method is not null)
        {
            action.CallbackMethod = method;
            action.RunMethodOverride = true;
        }

        action.CallbackData = caller;
        return Add(action);
    }

    public static T Add<T>(T action)
        where T : Action
    {
        DefaultCallbackManager?.AddCallbackObject(action);
        return action;
    }

    public static T BlockOn<T>(T action)
        where T : Action
    {
        action.Blocking = true;
        return action;
    }

    public static T Delay<T>(T action, double delay)
        where T : Action
    {
        action.Time = -delay * 1000;
        return action;
    }

    public void Init(object? group, double delay)
    {
        InitActionData();
        DebugString = "Action";
        Group = group;
        Time = -delay * 1000;
    }

    protected void InitActionData()
    {
        Group = null;
        Blocking = false;
        FunctionToRunOnce = null;
        FunctionData = null;
        SynchedAction = null;
        Time = 0;
        Duration = 0;
        PercentDone = 0;
        AnimStyle = StyleLinear;
        RunMethodOverride = false;
    }

    public override string ToString() => DebugString ?? nameof(Action);

    public double TimeLeft() => (Duration ?? 0) - Time;

    public bool IncrementTime(double ticks)
    {
        if (ticks <= 0)
        {
            return false;
        }

        Time += ticks;
        if (Time < 0)
        {
            return false;
        }

        if (!SynchCheck(ticks))
        {
            return false;
        }

        CalculatePercentDone();
        return true;
    }

    public void CalculatePercentDone()
    {
        if (Time < 0 || Duration is null)
        {
            return;
        }

        double duration = Duration.Value;
        if (Time > duration)
        {
            Time = duration;
        }

        if (Time >= duration)
        {
            PercentDone = 1;
        }
        else if (AnimStyle == StyleEaseIn)
        {
            PercentDone = Math.Sin(Time * Math.PI * 0.5 / duration);
        }
        else if (AnimStyle == StyleEaseOut)
        {
            PercentDone = 1 - Math.Cos(Time * Math.PI * 0.5 / duration);
        }
        else
        {
            PercentDone = Time / duration;
        }
    }

    public bool SynchCheck(double ticks)
    {
        if (SynchedAction is null)
        {
            return true;
        }

        Duration = SynchedAction.TimeLeft();
        if (Duration is null)
        {
            return false;
        }

        SynchedAction = null;
        return true;
    }

    public override bool Run(double ticks, object? callbackData, bool groupActive)
    {
        if (!IncrementTime(ticks))
        {
            return false;
        }

        FunctionToRunOnce?.Invoke(FunctionData);
        return true;
    }

    public override void Dispose()
    {
        SynchedAction = null;
        InUse = false;
        DebugString = null;

        if (RunMethodOverride)
        {
            CallbackMethod = Run;
            RunMethodOverride = false;
        }

        Reset();
    }

    private void Reset()
    {
        CallbackMethod = Run;
        CallbackData = this;
        BlockingMethod = null;
        AbortMethod = null;
        Group = null;
        Blocking = false;
    }

    public virtual void Destroy()
    {
        base.Dispose();
        SynchedAction = null;
        DebugString = null;
        FunctionToRunOnce = null;
        FunctionData = null;
    }
}
