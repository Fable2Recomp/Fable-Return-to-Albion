using System;

namespace Refii.FlashGames.PubGames;

public sealed class AnimateAlpha : AnimateBase
{
    private readonly double _a0;
    private readonly double _a;

    public AnimateAlpha(
        double startAlpha, double endAlpha, object? target,
        double time, double delay, int animStyle)
        : base(target, time, delay, animStyle)
    {
        _a0 = startAlpha;
        _a = endAlpha - _a0;
    }

    protected override bool Update(double dt, object? callbackData, bool done)
    {
        ApplyAlpha((dt * _a) + _a0);
        return done;
    }
}

public sealed class AnimatePos : AnimateBase
{
    private readonly double _x0;
    private readonly double _y0;
    private readonly double _x;
    private readonly double _y;

    public AnimatePos(
        double startX, double startY, double endX, double endY, object? target,
        double time, double delay, int animStyle)
        : base(target, time, delay, animStyle)
    {
        _x0 = startX;
        _y0 = startY;
        _x = endX - _x0;
        _y = endY - _y0;
    }

    protected override bool Update(double dt, object? callbackData, bool done)
    {
        ApplyX((dt * _x) + _x0);
        ApplyY((dt * _y) + _y0);
        return done;
    }
}

public sealed class AnimateScale : AnimateBase
{
    private readonly double _x0;
    private readonly double _y0;
    private readonly double _x;
    private readonly double _y;

    public AnimateScale(
        double startX, double startY, double endX, double endY, object? target,
        double time, double delay, int animStyle)
        : base(target, time, delay, animStyle)
    {
        _x0 = startX;
        _y0 = startY;
        _x = endX - _x0;
        _y = endY - _y0;
    }

    protected override bool Update(double dt, object? callbackData, bool done)
    {
        ApplyXScale((dt * _x) + _x0);
        ApplyYScale((dt * _y) + _y0);
        return done;
    }
}

public sealed class AnimateValue : AnimateBase
{
    private readonly double _v0;
    private readonly double _v;

    public AnimateValue(
        double startValue, double endValue, object? target,
        double time, double delay, int animStyle)
        : base(target, time, delay, animStyle)
    {
        _v0 = startValue;
        _v = endValue - _v0;
    }

    protected override bool Update(double dt, object? callbackData, bool done)
    {
        if (Object is IAnimValue target)
        {
            target.Value = (dt * _v) + _v0;
        }

        return done;
    }
}

public sealed class AnimateFrame : AnimateBase
{
    private readonly int _f0;
    private readonly int _f;

    public AnimateFrame(
        int startFrame, int endFrame, object? target,
        double time, double delay, int animStyle)
        : base(target, time, delay, animStyle)
    {
        _f0 = startFrame;
        _f = endFrame - _f0 + 1;
    }

    protected override bool Update(double dt, object? callbackData, bool done)
    {
        int frame = (int)Math.Floor(dt * _f);
        if (frame >= _f)
        {
            frame--;
        }

        ApplyFrame(frame + _f0);
        return done;
    }
}

public sealed class AnimatePulse : AnimateBase
{
    private readonly float _xs;
    private readonly float _ys;

    public AnimatePulse(IPubClip clip, double time, double delay)
        : base(clip, time, delay, StyleLinear)
    {
        _xs = clip.XScale;
        _ys = clip.YScale;
    }

    protected override bool Update(double dt, object? callbackData, bool done)
    {
        dt = dt < 0.5
            ? Math.Sin(dt * Math.PI)
            : Math.Sin((1 - dt) * Math.PI);

        if (Object is IPubClip clip)
        {
            clip.XScale = (float)((dt * 20) + _xs);
            clip.YScale = (float)((dt * 20) + _ys);
        }

        return done;
    }
}

public delegate bool PubAnimFunction(double dt, object? target, object? animData, bool done);

public sealed class AnimateCallback : AnimateBase
{
    private readonly PubAnimFunction? _animFunction;
    private readonly object? _animData;

    public AnimateCallback(
        PubAnimFunction? animFunction, object? animData, object? target,
        double time, double delay, int animStyle)
        : base(target, time, delay, animStyle)
    {
        _animFunction = animFunction;
        _animData = animData;
    }

    protected override bool Update(double dt, object? callbackData, bool done)
    {
        if (_animFunction is not null)
        {
            done = _animFunction(dt, Object, _animData, done);
        }

        return done;
    }
}
