using System;

namespace Refii.Titles.Fable2.PubGames.Framework;

public class Action : Callback
{
    public const int StyleLinear = 0;
    public const int StyleEaseIn = 1;
    public const int StyleEaseOut = 2;

    public static CallbackManager? DefaultCallbackManager { get; set; }

    public Action(object? group = null, double delay = 0)
    {
        InitActionData();
        Group = group;
        Time = -delay * 1000;
    }

    public double PercentDone { get; set; }

    public double Time { get; set; }

    public double Duration { get; set; }

    public int AnimStyle { get; set; }

    public System.Action? FunctionToRunOnce { get; set; }

    public Action? SynchedAction { get; set; }

    public static void SetGameSpeed(double speed)
    {
        if (DefaultCallbackManager is not null)
        {
            DefaultCallbackManager.Speed = speed;
        }
    }

    public static Action RunUntilTrue(
        object? caller, PubCallbackMethod method, object? group = null) =>
        AddFunction(caller, method, 0, group);

    public static Action RunOnce(object? caller, System.Action method, object? group = null)
    {
        Action action = AddFunction(caller, null, 0, group);
        action.FunctionToRunOnce = method;
        return action;
    }

    public static Action RunDelayed(
        object? caller, System.Action method, double delay, object? group = null)
    {
        Action action = AddFunction(caller, null, delay, group);
        action.FunctionToRunOnce = method;
        return action;
    }

    public static Action AddFunction(
        object? caller, PubCallbackMethod? method, double delay, object? group = null)
    {
        var action = new Action(group, delay);
        if (method is not null)
        {
            action.CallbackMethod = method;
        }

        action.CallbackData = caller;
        return Add(action);
    }

    public static T Add<T>(T action) where T : Action
    {
        DefaultCallbackManager?.AddCallbackObject(action);
        return action;
    }

    public static T BlockOn<T>(T action) where T : Action
    {
        action.Blocking = true;
        return action;
    }

    public static T EaseOut<T>(T action) where T : Action
    {
        action.AnimStyle = StyleEaseOut;
        return action;
    }

    public static T EaseIn<T>(T action) where T : Action
    {
        action.AnimStyle = StyleEaseIn;
        return action;
    }

    protected void InitActionData()
    {
        Group = null;
        Blocking = false;
        FunctionToRunOnce = null;
        SynchedAction = null;
        Time = 0;
        Duration = 0;
        PercentDone = 0;
        AnimStyle = StyleLinear;
    }

    public double TimeLeft() => Duration - Time;

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
        if (Time < 0 || double.IsNaN(Duration))
        {
            return;
        }

        if (Time > Duration)
        {
            Time = Duration;
        }

        if (Time >= Duration)
        {
            PercentDone = 1;
        }
        else if (AnimStyle == StyleEaseIn)
        {
            PercentDone = Math.Sin(Time * Math.PI * 0.5 / Duration);
        }
        else if (AnimStyle == StyleEaseOut)
        {
            PercentDone = 1 - Math.Cos(Time * Math.PI * 0.5 / Duration);
        }
        else
        {
            PercentDone = Time / Duration;
        }
    }

    public bool SynchCheck(double ticks)
    {
        if (SynchedAction is null)
        {
            return true;
        }

        Duration = SynchedAction.TimeLeft();
        if (double.IsNaN(Duration))
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

        FunctionToRunOnce?.Invoke();
        return true;
    }

    public override void Dispose()
    {
        base.Dispose();
        SynchedAction = null;
    }
}
