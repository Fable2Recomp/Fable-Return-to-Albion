using System;

namespace Refii.FlashGames.Framework;

// THE "== NaN" TESTS IN THE SHIPPED CODE ARE "isNaN" TESTS, and this file is where that matters.
//
// Fade and Animate both open their Run with what decompiles as `if (this.StartAlpha == NaN)`, and
// the bytecode agrees: Push StartAlpha, Push <double NaN>, Equals2, Not, If. Under ECMA equality
// NaN == NaN is false, so a literal transcription never captures the starting value, leaves it NaN,
// and assigns NaN to the clip's alpha or frame on every tick.
//
// That reading cannot be what the game does. InitLegend sets `_root["button" + i]._alpha = 0` and
// the ONLY thing that ever raises it is Fade.In; same for InitFreeSpins and InitBonusMultiplier.
// Under the literal reading the button prompts and the free-spin counter would never once become
// visible, and they plainly do. So the intent is isNaN — a first-run initialiser — and that is what
// is written here.
//
// Transcribing the defect instead would be faithful to the bytes and wrong about the game, and in
// C# it is worse than in Flash: Godot has no forgiving coercion for a NaN modulate, so a NaN alpha
// propagates into the render rather than being quietly ignored.
//
// (Found while reading this: tools/as2dis.py decoded AVM1 doubles with the two 32-bit halves
// swapped, printing NaN as 1.06e-314 — fixed there, or none of the above would have been legible.)

/// <summary>Plays a clip's timeline from one frame to another over a duration.</summary>
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

    /// <summary>NaN until the first tick reads the clip's current frame.</summary>
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

    /// <summary>
    /// Loops a stretch of timeline for a total duration.
    /// </summary>
    /// <remarks>
    /// ONE LOOP IS THE FRAME SPAN AT 60 FPS — <c>(finalFrame - startFrame) / 60</c> — so the loop
    /// runs at the rate the artwork was authored at, and <paramref name="duration"/> only decides
    /// how many times round it goes.
    /// </remarks>
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

    /// <summary>Runs for whatever <paramref name="synch"/> has left when this first ticks.</summary>
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

        // TRUNCATED, WHICH IS WHAT gotoAndStop DOES with a fractional frame.
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

/// <summary>Holds a group up, either for a fixed time or until the rest of it is done.</summary>
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

    /// <summary>
    /// Blocks until nothing else in the group ran this tick.
    /// </summary>
    /// <remarks>
    /// The one-second duration is a floor, not the wait: <c>groupActive</c> is what actually ends
    /// it, and it stops blocking the moment its group falls quiet.
    /// </remarks>
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

/// <summary>Tweens a clip's alpha to a target over a duration.</summary>
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

    /// <summary>NaN until the first tick reads the clip's current alpha.</summary>
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

/// <summary>
/// Fades a set of clips together.
/// </summary>
/// <remarks>
/// AN <see cref="AnimateBase"/>, NOT AN <see cref="Action"/> — the odd one out in this file, and
/// that is how it is in the shipped code. Nothing calls it: it is in Mage Dice's SWF with no
/// callers at all. Ported for completeness rather than because anything needs it.
/// </remarks>
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

/// <summary>Fires a sound cue, optionally after a delay.</summary>
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
