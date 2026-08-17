using System;

namespace Refii.FlashGames.Framework;

public sealed class Animate : Action
{
    private readonly IPubClip _clip;

    public Animate(IPubClip thingToAnimate, double finalFrame, double duration, object? group = null)
    {
        InitActionData();
        _clip = thingToAnimate;
        CallbackData = thingToAnimate;
        StartFrame = double.NaN;
        FinalFrame = finalFrame;
        Duration = duration * 1000;
        Looping = false;
        LoopsRemaining = 1;
        Group = group;
    }

    public double StartFrame { get; set; }

    public double FinalFrame { get; set; }

    public bool Looping { get; set; }

    public double LoopsRemaining { get; set; }

    public static Animate StartToEnd(IPubClip thingToAnimate, double duration, object? group = null)
    {
        var animate = new Animate(thingToAnimate, thingToAnimate.TotalFrames, duration, group)
        {
            StartFrame = 1,
        };
        Add(animate);
        return animate;
    }

    public static Animate Once(
        IPubClip thingToAnimate, double finalFrame, double duration, object? group = null) =>
        Add(new Animate(thingToAnimate, finalFrame, duration, group));

    public static Animate Loop(
        IPubClip thingToAnimate, double startFrame, double finalFrame, double duration,
        object? group = null)
    {
        double loop = (finalFrame - startFrame) / 60;
        var animate = new Animate(thingToAnimate, finalFrame, loop, group)
        {
            StartFrame = startFrame,
            Looping = true,
            LoopsRemaining = duration / loop,
        };
        Add(animate);
        return animate;
    }

    public static Animate Synched(
        IPubClip thingToAnimate, double finalFrame, Action synch, object? group = null)
    {
        var animate = new Animate(thingToAnimate, finalFrame, double.NaN, group)
        {
            SynchedAction = synch,
        };
        return Add(animate);
    }

    public static Animate Delayed(
        IPubClip thingToAnimate, double finalFrame, double delay, object? group = null)
    {
        Animate animate = Add(new Animate(thingToAnimate, finalFrame, 0, group));
        animate.Time = -delay * 1000;
        return animate;
    }

    public static Animate DelayedLoop(
        IPubClip thingToAnimate, double startFrame, double finalFrame, double delay,
        double duration, object? group = null)
    {
        double loop = (finalFrame - startFrame) / 60;
        var animate = new Animate(thingToAnimate, finalFrame, loop, group)
        {
            Time = -delay * 1000,
            StartFrame = startFrame,
            Looping = true,
            LoopsRemaining = duration / loop,
        };
        Add(animate);
        return animate;
    }

    public static Animate Instant(
        IPubClip thingToAnimate, double finalFrame, object? group = null) =>
        Add(new Animate(thingToAnimate, finalFrame, 0, group));

    public override bool Run(double ticks, object? callbackData, bool groupActive)
    {
        if (!IncrementTime(ticks))
        {
            return false;
        }

        if (double.IsNaN(StartFrame))
        {
            StartFrame = _clip.CurrentFrame;
        }

        _clip.GotoAndStop((int)(StartFrame + (PercentDone * (FinalFrame - StartFrame))));

        if (PercentDone == 1)
        {
            LoopsRemaining--;
            if (!Looping || LoopsRemaining < 1)
            {
                return true;
            }

            PercentDone = 0;
            Time = 0;
        }

        return false;
    }
}

public sealed class Wait : Action
{
    public Wait(double duration, object? group, bool runOnlyWhileActive)
    {
        InitActionData();
        Duration = duration * 1000;
        Group = group;
        Blocking = true;
        RunOnlyWhileActive = runOnlyWhileActive;
    }

    public bool RunOnlyWhileActive { get; set; }

    public static Wait Seconds(double duration, object? group = null) =>
        Add(new Wait(duration, group, false));

    public static Wait UntilFinished(object? group = null) =>
        Add(new Wait(1, group, true));

    public override bool Run(double ticks, object? callbackData, bool groupActive)
    {
        if (!IncrementTime(ticks))
        {
            return false;
        }

        if (RunOnlyWhileActive)
        {
            if (groupActive)
            {
                return false;
            }

            Blocking = false;
            return true;
        }

        return PercentDone == 1;
    }
}

public sealed class Fade : Action
{
    private readonly IPubClip _clip;

    public Fade(IPubClip thingToFade, double finalAlpha, double duration, object? group = null)
    {
        InitActionData();
        _clip = thingToFade;
        CallbackData = thingToFade;
        StartAlpha = double.NaN;
        FinalAlpha = finalAlpha;
        Duration = duration * 1000;
        Group = group;
    }

    public double StartAlpha { get; set; }

    public double FinalAlpha { get; set; }

    public static Fade Exact(
        IPubClip thingToFade, double finalAlpha, double duration, object? group = null) =>
        Add(new Fade(thingToFade, finalAlpha, duration, group));

    public static Fade OutSynched(IPubClip thingToFade, Action synch, object? group = null) =>
        Add(new Fade(thingToFade, 0, double.NaN, group) { SynchedAction = synch });

    public static Fade InSynched(IPubClip thingToFade, Action synch, object? group = null) =>
        Add(new Fade(thingToFade, 100, double.NaN, group) { SynchedAction = synch });

    public static Fade Out(IPubClip thingToFade, double duration, object? group = null) =>
        Add(new Fade(thingToFade, 0, duration, group));

    public static Fade InstantOut(IPubClip thingToFade, object? group = null) =>
        Add(new Fade(thingToFade, 0, 0, group));

    public static Fade In(IPubClip thingToFade, double duration, object? group = null) =>
        Add(new Fade(thingToFade, 100, duration, group));

    public static Fade InstantIn(IPubClip thingToFade, object? group = null) =>
        Add(new Fade(thingToFade, 100, 0, group));

    public override bool Run(double ticks, object? callbackData, bool groupActive)
    {
        if (!IncrementTime(ticks))
        {
            return false;
        }

        if (double.IsNaN(StartAlpha))
        {
            StartAlpha = _clip.Alpha;
        }

        _clip.Alpha = (float)(StartAlpha + (PercentDone * (FinalAlpha - StartAlpha)));
        return PercentDone == 1;
    }
}

public sealed class FadeClips : AnimateBase
{
    private readonly IPubClip[] _clips;
    private readonly double _a0;
    private readonly double _a;

    public FadeClips(bool fadeIn, IPubClip[] clips, double time, double delay, int animStyle)
        : base(null, time, delay, animStyle)
    {
        _clips = clips;
        _a0 = fadeIn ? 0 : 100;
        _a = 100 - _a0 - _a0;
    }

    protected override bool Update(double dt, object? callbackData, bool done)
    {
        var alpha = (float)((dt * _a) + _a0);
        foreach (IPubClip clip in _clips)
        {
            clip.Alpha = alpha;
        }

        return done;
    }
}

public sealed class PlaySound : Action
{
    private readonly Audio _audio;

    public PlaySound(Audio audio, string cue, object? group = null)
    {
        InitActionData();
        _audio = audio;
        Cue = cue;
        Group = group;
    }

    public string Cue { get; set; }

    public static PlaySound Once(Audio audio, string cue, object? group = null) =>
        Add(new PlaySound(audio, cue, group));

    public static PlaySound Delayed(
        Audio audio, string cue, double delay, object? group = null)
    {
        PlaySound sound = Add(new PlaySound(audio, cue, group));
        sound.Time = -delay * 1000;
        return sound;
    }

    public override bool Run(double ticks, object? callbackData, bool groupActive)
    {
        if (!IncrementTime(ticks))
        {
            return false;
        }

        _audio.PlaySound(Cue);
        return PercentDone == 1;
    }
}
