using System;

namespace Refii.FlashGames.PubGames;

public abstract class AnimateBase : Callback
{
    public const int StyleLinear = 0;
    public const int StyleEaseIn = 1;
    public const int StyleEaseOut = 2;
    public const double FrameTime60Fps = 16.666666666666668;
    public const double FrameTime30Fps = 33.333333333333336;

    private readonly int _animStyle;
    private double _t;
    private readonly double _t1;
    private PubEndCallback? _callback;
    private object? _userData;

    protected AnimateBase(object? target, double time, double delay, int animStyle)
    {
        Object = target;
        _t = -delay;
        _t1 = time;
        _animStyle = animStyle;
        _callback = null;
        _userData = null;
    }

    protected object? Object { get; set; }

    public void SetEndCallback(PubEndCallback? endCallback, object? endUserData)
    {
        _callback = endCallback;
        _userData = endUserData;
    }

    protected virtual bool Update(double dt, object? callbackData, bool done) => done;

    public override bool Run(double ticks, object? callbackData, bool blocked)
    {
        bool done = false;
        _t += ticks;
        double u = _t;
        if (u < 0)
        {
            return false;
        }

        if (u >= _t1 || _t1 == 1)
        {
            u = _t1;
            done = true;
        }

        u = _animStyle switch
        {
            StyleEaseIn => Math.Sin(u * Math.PI * 0.5 / _t1),
            StyleEaseOut => 1 - Math.Cos(u * Math.PI * 0.5 / _t1),
            _ => u / _t1,
        };

        done = Update(u, callbackData, done);
        if (done)
        {
            _callback?.Invoke(_userData);
        }

        return done;
    }

    protected void ApplyX(double value)
    {
        if (Object is IAnimPos pos)
        {
            pos.SetX((float)value);
        }
        else if (Object is IPubClip clip)
        {
            clip.X = (float)value;
        }
    }

    protected void ApplyY(double value)
    {
        if (Object is IAnimPos pos)
        {
            pos.SetY((float)value);
        }
        else if (Object is IPubClip clip)
        {
            clip.Y = (float)value;
        }
    }

    protected void ApplyXScale(double value)
    {
        if (Object is IAnimScale scale)
        {
            scale.SetXScale((float)value);
        }
        else if (Object is IPubClip clip)
        {
            clip.XScale = (float)value;
        }
    }

    protected void ApplyYScale(double value)
    {
        if (Object is IAnimScale scale)
        {
            scale.SetYScale((float)value);
        }
        else if (Object is IPubClip clip)
        {
            clip.YScale = (float)value;
        }
    }

    protected void ApplyAlpha(double value)
    {
        if (Object is IAnimAlpha alpha)
        {
            alpha.SetAlpha((float)value);
        }
        else if (Object is IPubClip clip)
        {
            clip.Alpha = (float)value;
        }
    }

    protected void ApplyFrame(int frame)
    {
        if (Object is IAnimFrame target)
        {
            target.SetFrame(frame);
        }
        else if (Object is IPubClip clip)
        {
            clip.GotoAndStop(frame);
        }
    }
}
