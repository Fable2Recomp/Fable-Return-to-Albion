using System;
using System.Collections.Generic;
using System.Globalization;
using Refii.Hero;

namespace Refii.Titles.Fable2;

using Refii.Ui;

public sealed class Engine_UiCharacterOptions : Engine_UiHostOptionsPage
{
    public const string OpenCommand = "options:character:morphs";
    public static readonly Engine_UiCharacterOptions Page = new(
        key => Refii.Core.Engine_Switches.String(key, string.Empty),
        values => Engine_UiScreenActions.WriteSettings(values));

    private readonly Func<string, string> _read;
    private readonly Func<IReadOnlyDictionary<string, string>, bool> _write;
    private bool _automatic = true;

    public Engine_UiCharacterOptions(Func<string, string> read, Func<IReadOnlyDictionary<string, string>, bool> write)
    {
        _read = read;
        _write = write;
    }

    public override string Name => "CharacterMorphs";
    public override string Title => "Character - Morphs";
    public override int Count => _automatic ? 1 : Engine_CharacterMorphs.Sliders.Length + 1;
    public override string RowTitle(int row) => row == 0 ? "Morphs" : Engine_CharacterMorphs.Sliders[row - 1].Label;
    public override int Choices(int row) => row == 0 ? 2 : Engine_CharacterMorphs.Steps(Engine_CharacterMorphs.Sliders[row - 1]);

    public override string Caption(int row, int index)
    {
        if (row == 0) return index != 0 ? "On" : "Off";
        var slider = Engine_CharacterMorphs.Sliders[row - 1];
        float value = Engine_CharacterMorphs.Value(slider, index);
        if (slider.Input == HeroMorphInput.Age) return value.ToString("0", CultureInfo.InvariantCulture);
        if (slider.Input == HeroMorphInput.Morality)
            return value == 0 ? "Evil" : value == 1 ? "Good" : value == 0.5f ? "Neutral" : $"{value * 100:0}% good";
        if (slider.Input == HeroMorphInput.Purity)
            return value == 0 ? "Corrupt" : value == 1 ? "Pure" : value == 0.5f ? "Neutral" : $"{value * 100:0}% pure";
        return (value * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    public override int[] Read()
    {
        _automatic = Engine_CharacterMorphs.Automatic(_read);
        var stats = Engine_CharacterMorphs.Read(_read);
        var indices = new int[Engine_CharacterMorphs.Sliders.Length + 1];
        indices[0] = _automatic ? 1 : 0;
        for (int i = 0; i < Engine_CharacterMorphs.Sliders.Length; i++)
        {
            var slider = Engine_CharacterMorphs.Sliders[i];
            indices[i + 1] = Engine_CharacterMorphs.Index(slider, stats[slider.Input]);
        }
        return indices;
    }

    public override void Changed(int row, int index)
    {
        if (row == 0) _automatic = index != 0;
    }

    public override bool Commit(Func<int, int> index) => _write(Engine_CharacterMorphs.Values(index));
}
