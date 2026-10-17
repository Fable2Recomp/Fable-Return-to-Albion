using System;

namespace Refii.Level;

public sealed class Engine_DynamicWeatherTransition
{
    private DynamicWeatherState _from;
    private DynamicWeatherState _target;
    private double _elapsed;
    private double _duration;

    public DynamicWeatherState Value { get; private set; }

    public Engine_DynamicWeatherTransition(DynamicWeatherState initial)
    {
        Value = _from = _target = initial.Sanitized();
    }

    public void Begin(DynamicWeatherState target, float seconds)
    {
        target = target.Sanitized();
        if (_target.Equals(target) && float.IsFinite(seconds) && seconds > 0) return;
        _from = Value;
        _target = target;
        _elapsed = 0;
        _duration = float.IsFinite(seconds) ? Math.Max(seconds, 0) : 0;
        if (_duration == 0) Value = _target;
    }

    public void Advance(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds <= 0 || _elapsed >= _duration) return;
        _elapsed = Math.Min(_elapsed + seconds, _duration);
        float t = (float)(_elapsed / _duration);
        Value = DynamicWeatherState.Lerp(_from, _target, t * t * (3f - 2f * t));
    }
}
