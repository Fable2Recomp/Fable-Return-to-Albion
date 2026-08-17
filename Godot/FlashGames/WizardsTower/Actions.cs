using Refii.FlashGames.Framework;

namespace Refii.FlashGames.WizardsTower;

public sealed class Animate : Action
{
    public double? StartFrame { get; set; }

    public double FinalFrame { get; set; }

    public string FinalLabel { get; set; } = string.Empty;

    public string ClipName { get; set; } = string.Empty;

    public bool Looping { get; set; }

    public static void Preallocate(int size) => ActionPool<Animate>.Preallocate(size);

    public static Animate Once(
        IPubClip thingToAnimate, double finalFrame, double? duration, object? group = null)
    {
        Animate animate = ActionPool<Animate>.GetFromArray();
        animate.Init(thingToAnimate, finalFrame, duration, group);
        return Add(animate);
    }

    public static Animate OnceByName(
        IPubClip thingToAnimate, string clipName, double finalFrame, double? duration,
        object? group = null)
    {
        Animate animate = Once(thingToAnimate, finalFrame, duration, group);
        animate.ClipName = clipName;
        return animate;
    }

    public static Animate LoopOnce(
        IPubClip thingToAnimate, double? duration, object? group = null)
    {
        Animate animate = Once(thingToAnimate, thingToAnimate.TotalFrames, duration, group);
        animate.StartFrame = 1;
        return animate;
    }

    public static Animate Loop(
        IPubClip thingToAnimate, double startFrame, double finalFrame, double? duration,
        object? group = null)
    {
        Animate animate = Once(thingToAnimate, finalFrame, duration, group);
        animate.StartFrame = startFrame;
        animate.Looping = true;
        return animate;
    }

    public static Animate Synched(
        IPubClip thingToAnimate, double finalFrame, Action synch, object? group = null)
    {
        Animate animate = ActionPool<Animate>.GetFromArray();
        animate.Init(thingToAnimate, finalFrame, null, group);
        animate.SynchedAction = synch;
        return Add(animate);
    }

    public static Animate Delayed(
        IPubClip thingToAnimate, double finalFrame, double delay, object? group = null)
    {
        Animate animate = Once(thingToAnimate, finalFrame, 0, group);
        animate.Time = -delay * 1000;
        return animate;
    }

    public static Animate Instant(
        IPubClip thingToAnimate, double finalFrame, object? group = null) =>
        Once(thingToAnimate, finalFrame, 0, group);

    public static Animate InstantByLabel(
        IPubClip thingToAnimate, string finalLabel, object? group = null)
    {
        Animate animate = Once(thingToAnimate, 0, 0, group);
        animate.FinalLabel = finalLabel;
        return animate;
    }

    public void Init(IPubClip thingToAnimate, double finalFrame, double? duration, object? group)
    {
        InitActionData();
        DebugString = "Animate";
        CallbackData = thingToAnimate;
        StartFrame = null;
        FinalFrame = finalFrame;
        FinalLabel = string.Empty;
        Duration = (duration ?? 0) * 1000;
        Looping = false;
        ClipName = string.Empty;
        Group = group;
    }

    private IPubClip? Target(object? callbackData)
    {
        if (callbackData is not IPubClip clip)
        {
            return null;
        }

        return ClipName.Length == 0 ? clip : clip.GetChild(ClipName);
    }

    public override bool Run(double ticks, object? callbackData, bool groupActive)
    {
        if (!IncrementTime(ticks))
        {
            return false;
        }

        IPubClip? target = Target(callbackData);
        if (target is null)
        {
            return true;
        }

        if (StartFrame is null)
        {
            StartFrame = target.CurrentFrame;

            if (FinalFrame == 0)
            {
                FinalFrame = target.TotalFrames;
            }
        }

        if (FinalLabel.Length == 0)
        {
            target.GotoAndStop(
                (int)(StartFrame.Value + (PercentDone * (FinalFrame - StartFrame.Value))));
        }

        if (PercentDone != 1)
        {
            return false;
        }

        if (FinalLabel.Length != 0)
        {
            target.GotoAndStop(FinalLabel);
        }

        if (!Looping)
        {
            return true;
        }

        PercentDone = 0;
        Time = 0;
        return false;
    }
}

public sealed class Fade : Action
{
    public double? StartAlpha { get; set; }

    public double FinalAlpha { get; set; }

    public static void Preallocate(int size) => ActionPool<Fade>.Preallocate(size);

    public static Fade Exact(
        IPubClip thingToFade, double finalAlpha, double? duration, object? group = null)
    {
        Fade fade = ActionPool<Fade>.GetFromArray();
        fade.Init(thingToFade, finalAlpha, duration, group);
        return Add(fade);
    }

    public static Fade OutSynched(IPubClip thingToFade, Action synch, object? group = null)
    {
        Fade fade = ActionPool<Fade>.GetFromArray();
        fade.Init(thingToFade, 0, null, group);
        fade.SynchedAction = synch;
        return Add(fade);
    }

    public static Fade InSynched(IPubClip thingToFade, Action synch, object? group = null)
    {
        Fade fade = ActionPool<Fade>.GetFromArray();
        fade.Init(thingToFade, 100, null, group);
        fade.SynchedAction = synch;
        return Add(fade);
    }

    public static Fade Out(IPubClip thingToFade, double? duration, object? group = null) =>
        Exact(thingToFade, 0, duration, group);

    public static Fade InstantOut(IPubClip thingToFade, object? group = null) =>
        Exact(thingToFade, 0, 0, group);

    public static Fade In(IPubClip thingToFade, double? duration, object? group = null) =>
        Exact(thingToFade, 100, duration, group);

    public static Fade InstantIn(IPubClip thingToFade, object? group = null) =>
        Exact(thingToFade, 100, 0, group);

    public void Init(IPubClip thingToFade, double finalAlpha, double? duration, object? group)
    {
        InitActionData();
        DebugString = "Fade";
        CallbackData = thingToFade;
        StartAlpha = null;
        FinalAlpha = finalAlpha;
        Duration = (duration ?? 0) * 1000;
        Group = group;
    }

    public override void Destroy()
    {
        base.Destroy();
        StartAlpha = null;
    }

    public override bool Run(double ticks, object? callbackData, bool groupActive)
    {
        if (!IncrementTime(ticks))
        {
            return false;
        }

        if (callbackData is not IPubClip clip)
        {
            return true;
        }

        StartAlpha ??= clip.Alpha;
        clip.Alpha = (float)(StartAlpha.Value + (PercentDone * (FinalAlpha - StartAlpha.Value)));
        return PercentDone == 1;
    }
}

public sealed class Wait : Action
{
    public bool RunOnlyWhileActive { get; set; }

    public static void Preallocate(int size) => ActionPool<Wait>.Preallocate(size);

    public static Wait Seconds(double duration, object? group = null)
    {
        Wait wait = ActionPool<Wait>.GetFromArray();
        wait.Init(duration, group, false);
        return Add(wait);
    }

    public static Wait UntilFinished(object? group = null)
    {
        Wait wait = ActionPool<Wait>.GetFromArray();
        wait.Init(0, group, true);
        return Add(wait);
    }

    public void Init(double duration, object? group, bool runOnlyWhileActive)
    {
        InitActionData();
        DebugString = "Wait";
        Duration = duration * 1000;
        Group = group;
        Blocking = true;
        RunOnlyWhileActive = runOnlyWhileActive;
    }

    public override bool Run(double ticks, object? callbackData, bool groupActive)
    {
        if (!IncrementTime(ticks))
        {
            return false;
        }

        if (!RunOnlyWhileActive)
        {
            return PercentDone == 1;
        }

        if (groupActive)
        {
            return false;
        }

        Blocking = false;
        return true;
    }
}

public sealed class PlaySound : Action
{
    public string? Cue { get; set; }

    public static void Preallocate(int size) => ActionPool<PlaySound>.Preallocate(size);

    public static PlaySound Once(string cue, object? group = null)
    {
        PlaySound sound = ActionPool<PlaySound>.GetFromArray();
        sound.Init(cue, group);
        return Add(sound);
    }

    public static PlaySound Delayed(string cue, double delay, object? group = null)
    {
        PlaySound sound = Once(cue, group);
        sound.Time = -delay * 1000;
        return sound;
    }

    public void Init(string cue, object? group)
    {
        InitActionData();
        DebugString = "PlaySound";
        Cue = cue;
        Group = group;
    }

    public override void Destroy()
    {
        base.Destroy();
        Cue = null;
    }

    public override bool Run(double ticks, object? callbackData, bool groupActive)
    {
        if (!IncrementTime(ticks))
        {
            return false;
        }

        WizardsTower.DebugToScreen(Cue);
        if (Cue is not null)
        {
            WizardsTower.Sound?.PlaySound(Cue);
        }

        return PercentDone == 1;
    }
}
