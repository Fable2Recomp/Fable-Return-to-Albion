using System;
using System.Collections.Generic;

using Refii.FlashGames.Framework;

using Action = Refii.FlashGames.Framework.Action;

namespace Refii.FlashGames.MageDice;

public sealed class AnimateWin : Action
{
    private readonly IPubClip _clip;
    private double _alpha;

    public AnimateWin(IPubClip clip, double duration, object? group = null)
    {
        InitActionData();
        _clip = clip;
        clip.Visible = false;
        Duration = duration * 1000;
        Group = group;
        _alpha = 100;
    }

    public static AnimateWin Timed(IPubClip clip, double duration, object? group = null) =>
        Add(new AnimateWin(clip, duration, group));

    public static AnimateWin Instant(IPubClip clip, object? group = null) =>
        Add(new AnimateWin(clip, 0, group));

    public override bool Run(double ticks, object? callbackData, bool groupActive)
    {
        if (!IncrementTime(ticks))
        {
            return false;
        }

        bool done = PercentDone >= 1;

        if (MageDice.KillWinAnims)
        {
            _alpha -= ticks * 0.5;
            if (_alpha < 0)
            {
                _alpha = 0;
            }
        }

        _clip.Alpha = (float)_alpha;

        double frame = (PercentDone * _clip.TotalFrames) + 1;
        if (frame > _clip.TotalFrames)
        {
            frame = _clip.TotalFrames;
        }

        _clip.GotoAndStop((int)frame);
        _clip.Visible = !done;
        return done;
    }
}

public sealed class FloatyText : Action
{
    private readonly IPubClip? _clip;
    private readonly Localizer _localizer;
    private readonly string _loc;
    private readonly Dictionary<string, string>? _vars;

    private double _alpha;

    public FloatyText(
        IPubClip parent, string loc, Dictionary<string, string>? vars, MageDiceWinVars clipVars,
        double duration, object? group, Localizer localizer)
    {
        InitActionData();
        _localizer = localizer;
        _loc = loc;
        _vars = vars;

        IPubClip container = parent.GetChild("winAnimations")
            ?? parent.CreateEmptyMovieClip("winAnimations", parent.GetNextHighestDepth());

        int depth = container.GetNextHighestDepth();
        _clip = container.AttachMovie("winAmount", "a" + depth, depth);
        if (_clip is not null)
        {
            _clip.X = clipVars.X;
            _clip.Y = clipVars.Y;
            _clip.XScale = clipVars.XScale;
            _clip.YScale = clipVars.YScale;
            _clip.Alpha = 0;
        }

        _alpha = 100;
        Duration = duration * 1000;
        Group = group;
    }

    public static FloatyText Timed(
        IPubClip parent, string loc, Dictionary<string, string>? vars, MageDiceWinVars clipVars,
        double duration, object? group, Localizer localizer) =>
        Add(new FloatyText(parent, loc, vars, clipVars, duration, group, localizer));

    public override bool Run(double ticks, object? callbackData, bool groupActive)
    {
        if (!IncrementTime(ticks))
        {
            return false;
        }

        bool done = PercentDone >= 1;
        if (done)
        {
            _clip?.RemoveMovieClip();
            return true;
        }

        if (MageDice.KillWinAnims || PercentDone > 0.7)
        {
            _alpha -= ticks * 0.33;
            if (_alpha < 0)
            {
                _alpha = 0;
            }
        }
        else if (PercentDone < 0.33)
        {
            _alpha = PercentDone * 100 / 0.33;
        }
        else
        {
            _alpha = 100;
        }

        if (_clip is null)
        {
            return false;
        }

        _clip.Alpha = (float)_alpha;

        IPubClip? blob = _clip.GetChild("blob");
        if (blob is not null)
        {
            var frame = (int)Math.Floor(PercentDone * blob.TotalFrames) + 1;
            if (frame > blob.TotalFrames)
            {
                frame = blob.TotalFrames;
            }

            blob.GotoAndStop(frame);
        }

        IPubClip? label = _clip.GetChild("label")?.GetChild("label");
        if (label is not null)
        {
            _localizer.Localize(label, _loc, _vars);
        }

        return false;
    }
}

public readonly record struct MageDiceWinVars(float X, float Y, float XScale, float YScale);
