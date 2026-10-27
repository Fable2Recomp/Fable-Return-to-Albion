using System;
using System.Collections.Generic;
using Refii.Ui;

namespace Refii.Hero;

public sealed class Engine_MorphMirrorSettings
{
    private readonly Engine_UiCharacterOptions _page;
    private readonly int[] _original;
    private readonly int[] _values;
    private readonly Action<string>? _playSound;
    public int Selected { get; private set; }
    public int Count => _page.Count;
    public float Rotation { get; private set; }
    public float Zoom { get; private set; }

    public Engine_MorphMirrorSettings(Func<string, string> read, Action<string>? playSound = null)
    {
        _playSound = playSound;
        _page = new Engine_UiCharacterOptions(read, _ => false);
        _values = _page.Read();
        _original = (int[])_values.Clone();
    }

    public string Title(int row) => row == 0 ? "Automatic morphs" : _page.RowTitle(row);
    public string Display(int row) => _page.Caption(row, _values[row]);
    public void Select(int direction)
    {
        int next = Math.Clamp(Selected + direction, 0, Count - 1);
        if (next == Selected) return;
        Selected = next;
        _playSound?.Invoke(direction < 0 ? "UiCue:game:slideup" : "UiCue:game:slidedown");
    }
    public bool Adjust(int row, int direction)
    {
        if (row < 0 || row >= Count) return false;
        int next = Math.Clamp(_values[row] + direction, 0, _page.Choices(row) - 1);
        if (next == _values[row]) return false;
        _values[row] = next;
        _page.Changed(row, next);
        Selected = Math.Min(row, Count - 1);
        _playSound?.Invoke(direction < 0 ? "UiCue:game:slideup" : "UiCue:game:slidedown");
        return true;
    }

    public void Reset()
    {
        Array.Copy(_original, _values, _values.Length);
        _page.Changed(0, _values[0]);
        Selected = Math.Min(Selected, Count - 1);
        Rotation = 0;
        Zoom = 0;
    }

    public void Rotate(float stick, float drag, float seconds)
    {
        if (!float.IsFinite(stick)) stick = 0;
        if (!float.IsFinite(drag)) drag = 0;
        if (!float.IsFinite(seconds)) seconds = 0;
        float axis = Math.Clamp((Math.Abs(stick) - 0.18f) / 0.82f, 0, 1) * Math.Sign(stick);
        float turn = axis * Math.Clamp(seconds, 0, 0.1f) * 2.2f + drag * 0.008f;
        Rotation = MathF.IEEERemainder(Rotation + turn, MathF.Tau);
    }

    public void ZoomBy(float stick, float wheel, float seconds)
    {
        if (!float.IsFinite(stick)) stick = 0;
        if (!float.IsFinite(wheel)) wheel = 0;
        if (!float.IsFinite(seconds)) seconds = 0;
        float axis = Math.Clamp((Math.Abs(stick) - 0.18f) / 0.82f, 0, 1) * Math.Sign(stick);
        Zoom = Math.Clamp(Zoom + axis * Math.Clamp(seconds, 0, 0.1f) * 0.65f +
            Math.Clamp(wheel, -20, 20) * 0.12f, 0, 1);
    }

    public Dictionary<string, string> Values() => Engine_CharacterMorphs.Values(row => _values[row]);
}
