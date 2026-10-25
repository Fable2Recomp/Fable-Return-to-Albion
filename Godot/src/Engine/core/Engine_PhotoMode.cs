using System;

namespace Refii.Core;

public sealed class Engine_PhotoMode
{
    public static readonly string[] Filters = { "Original", "Vivid", "Warm", "Cool", "Sepia", "Monochrome", "Noir" };
    public static readonly string[] Settings = { "Filter", "Field of view", "Camera tilt", "Move speed", "Exposure", "Contrast", "Saturation", "Vignette" };
    private static readonly float[] Minimum = { 0, 15, -180, 0.25f, -3, 0.5f, 0, 0 };
    private static readonly float[] Maximum = { Filters.Length - 1, 110, 180, 20, 3, 2, 2, 1 };
    private static readonly float[] Steps = { 1, 2, 2, 0.25f, 0.1f, 0.05f, 0.1f, 0.05f };
    private readonly float[] _values = new float[Settings.Length];
    private bool _toggleHeld;
    public int Selected { get; private set; }
    public float this[int setting] => _values[setting];
    public int Filter => (int)_values[0];

    public Engine_PhotoMode() => Reset(60);

    public bool TogglePressed(bool keyboard, bool leftStick, bool rightStick)
    {
        bool held = keyboard || (leftStick && rightStick);
        bool pressed = held && !_toggleHeld;
        _toggleHeld = held;
        return pressed;
    }

    public void Reset(float fov)
    {
        _values[0] = 0;
        _values[1] = float.IsFinite(fov) ? Math.Clamp(fov, Minimum[1], Maximum[1]) : 60;
        _values[2] = 0;
        _values[3] = 3;
        _values[4] = 0;
        _values[5] = 1;
        _values[6] = 1;
        _values[7] = 0;
        Selected = 0;
    }

    public void Select(int direction) => Selected = (Selected + Math.Sign(direction) + Settings.Length) % Settings.Length;

    public void Adjust(int setting, int direction)
    {
        if ((uint)setting >= _values.Length || direction == 0) return;
        float next = _values[setting] + Steps[setting] * Math.Sign(direction);
        _values[setting] = setting == 0
            ? ((int)next + Filters.Length) % Filters.Length
            : Math.Clamp(next, Minimum[setting], Maximum[setting]);
    }

    public string Display(int setting) => setting switch
    {
        0 => Filters[Filter],
        1 or 2 => $"{_values[setting]:0}°",
        3 => $"{_values[setting]:0.##} m/s",
        4 => $"{_values[setting]:+0.0;-0.0;0.0} EV",
        _ => $"{_values[setting]:0.00}"
    };
}
