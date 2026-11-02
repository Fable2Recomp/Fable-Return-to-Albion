using System;
using System.Collections.Generic;
using System.Linq;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Hero;

/// <summary>
/// The Mirror's rows: the look (the automatic switch, then the stat sliders while manual), then one
/// row per mod morph (<see cref="Engine_ModMorphs"/>), which is always live — mods' morphs answer in
/// both modes — and is written as it moves (<c>write</c>), not on Apply; Cancel and Reset put the
/// weights the Mirror opened with back.
/// </summary>
public sealed class Engine_MorphMirrorSettings
{
    private readonly Engine_UiCharacterOptions _page;
    private readonly int[] _original;
    private readonly int[] _values;
    private readonly Action<int>? _moved;
    private readonly IReadOnlyList<ModMorph> _morphs;
    private readonly float[] _morphOriginal;
    private readonly float[] _morphValues;
    private readonly Action<ModMorph, float>? _write;
    public int Selected { get; private set; }
    public int Count => _page.Count + _morphs.Count;
    /// <summary>The look's own rows: 1 (the switch) while automatic.</summary>
    public int LookRows => _page.Count;
    public bool Automatic => _page.Count == 1;
    public float Rotation { get; private set; }
    public float Zoom { get; private set; }

    /// <param name="moved">Told the direction (-1 up, 1 down) each time the selection or a value moves.</param>
    /// <param name="morphs">The mod morphs, each with the player's weight now.</param>
    /// <param name="write">Told each mod morph weight as it moves.</param>
    public Engine_MorphMirrorSettings(Func<string, string> read, Action<int>? moved = null,
        IReadOnlyList<(ModMorph Morph, float Weight)>? morphs = null, Action<ModMorph, float>? write = null)
    {
        _moved = moved;
        _page = new Engine_UiCharacterOptions(read, _ => false);
        _values = _page.Read();
        _original = (int[])_values.Clone();
        _morphs = morphs?.Select(m => m.Morph).ToList() ?? new List<ModMorph>();
        _morphValues = morphs?.Select(m => m.Morph.Value(m.Morph.Index(m.Weight))).ToArray() ?? Array.Empty<float>();
        _morphOriginal = morphs?.Select(m => m.Morph.Clamp(m.Weight)).ToArray() ?? Array.Empty<float>();
        _write = write;
    }

    private int MorphRow(int row) => row - _page.Count;

    public string Title(int row) => row == 0 ? "Automatic morphs" : row < _page.Count ? _page.RowTitle(row) : _morphs[MorphRow(row)].Label;

    public string Display(int row)
    {
        if (row < _page.Count) return _page.Caption(row, _values[row]);
        ModMorph morph = _morphs[MorphRow(row)];
        float value = _morphValues[MorphRow(row)];
        return morph.Minimum == 0 && morph.Maximum == 1
            ? (value * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%"
            : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The weight a mod morph row shows, or NaN for a look row.</summary>
    public float MorphWeight(int row) => row >= _page.Count && row < Count ? _morphValues[MorphRow(row)] : float.NaN;
    public void Select(int direction)
    {
        int next = Math.Clamp(Selected + direction, 0, Count - 1);
        if (next == Selected) return;
        Selected = next;
        _moved?.Invoke(Math.Sign(direction));
    }
    public bool Adjust(int row, int direction)
    {
        if (row < 0 || row >= Count) return false;
        if (row >= _page.Count)
        {
            int at = MorphRow(row);
            ModMorph morph = _morphs[at];
            float value = morph.Value(morph.Index(_morphValues[at]) + direction);
            if (value == _morphValues[at]) return false;
            _morphValues[at] = value;
            _write?.Invoke(morph, value);
            Selected = row;
            _moved?.Invoke(Math.Sign(direction));
            return true;
        }

        int next = Math.Clamp(_values[row] + direction, 0, _page.Choices(row) - 1);
        if (next == _values[row]) return false;
        _values[row] = next;
        _page.Changed(row, next);
        Selected = Math.Min(row, Count - 1);
        _moved?.Invoke(Math.Sign(direction));
        return true;
    }

    public void Reset()
    {
        Array.Copy(_original, _values, _values.Length);
        _page.Changed(0, _values[0]);
        RestoreMorphs();
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

    /// <summary>Puts back each mod morph weight the Mirror opened with (Cancel, Reset), writing the ones that moved.</summary>
    public void RestoreMorphs()
    {
        for (int i = 0; i < _morphs.Count; i++)
        {
            if (_morphValues[i] == _morphOriginal[i]) continue;
            _morphValues[i] = _morphOriginal[i];
            _write?.Invoke(_morphs[i], _morphOriginal[i]);
        }
    }
}
