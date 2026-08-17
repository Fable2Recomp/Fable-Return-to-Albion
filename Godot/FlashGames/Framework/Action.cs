using System;

namespace Refii.FlashGames.Framework;

/// <summary>
/// A timed unit of work on the callback manager: run for a duration, then finish.
/// </summary>
/// <remarks>
/// A SIBLING OF <see cref="AnimateBase"/>, NOT THE SAME CLASS. Both derive from
/// <see cref="Callback"/> and both interpolate over a duration, but they are separate classes in
/// the shipped code with separate designs, and Mage Dice uses both. Where <c>AnimateBase</c> is
/// handed a target and tweens one property of it, an <c>Action</c> is a scheduled job: it owns a
/// group, it can block the rest of its group, it can synchronise its duration to another action's
/// remaining time, and it can carry a one-shot function to fire when its delay expires. Keystone
/// never needed any of that, which is why this is arriving only now.
///
/// TIMES ARE SECONDS IN, MILLISECONDS INSIDE. Every factory below takes seconds because that is
/// what the game's call sites are written in — <c>Fade.Out(clip, 0.5, "SwitchAnim")</c> — and
/// <see cref="Time"/> and <see cref="Duration"/> are milliseconds because that is what the
/// callback manager ticks in.
///
/// A NEGATIVE <see cref="Time"/> IS A DELAY, not an error: the action accumulates ticks while it
/// is negative and does nothing until it reaches zero.
/// </remarks>
public class Action : Callback
{
    public const int StyleLinear = 0;
    public const int StyleEaseIn = 1;
    public const int StyleEaseOut = 2;

    /// <summary>
    /// Where <see cref="Add"/> puts new actions.
    /// </summary>
    /// <remarks>
    /// STATIC BECAUSE THE SHIPPED CODE IS: <c>Action.DefaultCallbackManager</c>, set once in
    /// <c>MageDice.PreInitialize</c>, so that every call site can read
    /// <c>Fade.Out(clip, 0.5, group)</c> rather than thread a manager through the board, the
    /// switches and the reels. A game sets it before it builds anything.
    /// </remarks>
    public static CallbackManager? DefaultCallbackManager { get; set; }

    public Action(object? group = null, double delay = 0)
    {
        InitActionData();
        Group = group;
        Time = -delay * 1000;
    }

    /// <summary>How far through, 0 to 1, after the easing curve has been applied.</summary>
    public double PercentDone { get; set; }

    /// <summary>Milliseconds elapsed. Negative while the action is still waiting out its delay.</summary>
    public double Time { get; set; }

    /// <summary>Milliseconds the action runs for. NaN until a synched action supplies it.</summary>
    public double Duration { get; set; }

    public int AnimStyle { get; set; }

    /// <summary>Fired once, when the delay expires.</summary>
    public System.Action? FunctionToRunOnce { get; set; }

    /// <summary>
    /// An action to take the remaining duration from, the first time this one runs.
    /// </summary>
    /// <remarks>
    /// This is how the shipped code lines an animation up with one already in flight — the shutter
    /// closing over exactly as long as the reel has left — without either having to know the other's
    /// start time.
    /// </remarks>
    public Action? SynchedAction { get; set; }

    public static void SetGameSpeed(double speed)
    {
        if (DefaultCallbackManager is not null)
        {
            DefaultCallbackManager.Speed = speed;
        }
    }

    /// <summary>Runs <paramref name="method"/> every tick until it returns true.</summary>
    public static Action RunUntilTrue(
        object? caller, PubCallbackMethod method, object? group = null) =>
        AddFunction(caller, method, 0, group);

    /// <summary>Runs <paramref name="method"/> once, next tick.</summary>
    public static Action RunOnce(object? caller, System.Action method, object? group = null)
    {
        Action action = AddFunction(caller, null, 0, group);
        action.FunctionToRunOnce = method;
        return action;
    }

    /// <summary>Runs <paramref name="method"/> once, after <paramref name="delay"/> seconds.</summary>
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

    /// <summary>Schedules an action on <see cref="DefaultCallbackManager"/> and returns it.</summary>
    /// <remarks>
    /// RETURNS WHAT IT WAS GIVEN, so the shipped code's decorator style works —
    /// <c>Action.EaseOut(Action.BlockOn(Animate.Once(...)))</c> reads outside in and each layer
    /// hands the same action back.
    /// </remarks>
    public static T Add<T>(T action) where T : Action
    {
        DefaultCallbackManager?.AddCallbackObject(action);
        return action;
    }

    /// <summary>Makes an action hold up the rest of its group until it finishes.</summary>
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

    /// <summary>
    /// Advances the clock, and says whether the action is awake enough to do anything.
    /// </summary>
    /// <remarks>
    /// FALSE MEANS "NOT YET", NOT "FINISHED". Every subclass opens its <c>Run</c> with
    /// <c>if (!IncrementTime(ticks)) return false;</c> — returning false to the callback manager
    /// keeps the action alive for another tick.
    /// </remarks>
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
