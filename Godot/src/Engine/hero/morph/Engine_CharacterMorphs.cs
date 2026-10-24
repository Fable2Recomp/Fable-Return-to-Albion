using System;
using System.Collections.Generic;
using System.Globalization;

namespace Refii.Hero;

public static class Engine_CharacterMorphs
{
    public const string AutomaticKey = "character_morphs";
    public const string BridgePrefix = "refii.character_morphs.";
    public static readonly HeroMorphSlider[] Sliders =
    {
        new(HeroMorphInput.Strength, "Strength", 0, 1, 0.05f),
        new(HeroMorphInput.Skill, "Skill", 0, 1, 0.05f),
        new(HeroMorphInput.Will, "Will", 0, 1, 0.05f),
        new(HeroMorphInput.Fat, "Weight", 0, 1, 0.05f),
        new(HeroMorphInput.Height, "Height", -1, 1, 0.05f),
        new(HeroMorphInput.Tan, "Tan", 0, 1, 0.05f),
        new(HeroMorphInput.Morality, "Morality appearance", 0, 1, 0.05f),
        new(HeroMorphInput.Purity, "Purity appearance", 0, 1, 0.05f),
        new(HeroMorphInput.Age, "Age appearance", 18, 60, 1),
    };

    public static string Key(HeroMorphInput input) => "character_morph_" + input.ToString().ToLowerInvariant();

    public static bool Automatic(Func<string, string> read) =>
        read(AutomaticKey).Trim().Trim('"').ToLowerInvariant() is not ("false" or "0" or "off");

    public static HeroMorphStats Read(Func<string, string> read)
    {
        var stats = new HeroMorphStats();
        foreach (var slider in Sliders)
        {
            if (float.TryParse(read(Key(slider.Input)).Trim().Trim('"'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out float value) && float.IsFinite(value))
                Engine_HeroMorphSliders.Set(stats, slider, value);
        }
        return stats;
    }

    public static int Steps(HeroMorphSlider slider) =>
        (int)MathF.Round((slider.Maximum - slider.Minimum) / slider.Step) + 1;

    public static int Index(HeroMorphSlider slider, float value) =>
        Math.Clamp((int)MathF.Round((value - slider.Minimum) / slider.Step), 0, Steps(slider) - 1);

    public static float Value(HeroMorphSlider slider, int index) =>
        Math.Clamp(slider.Minimum + Math.Clamp(index, 0, Steps(slider) - 1) * slider.Step,
            slider.Minimum, slider.Maximum);

    public static Dictionary<string, string> Values(Func<int, int> index)
    {
        var values = new Dictionary<string, string> { [AutomaticKey] = index(0) != 0 ? "true" : "false" };
        for (int i = 0; i < Sliders.Length; i++)
            values[Key(Sliders[i].Input)] = Value(Sliders[i], index(i + 1)).ToString("0.##", CultureInfo.InvariantCulture);
        return values;
    }

    public static void Publish(Func<string, string> read, Action<string, float> set, int generation)
    {
        var stats = Read(read);
        for (uint type = 2; type <= 18; type++)
            set(BridgePrefix + type.ToString(CultureInfo.InvariantCulture), Engine_HeroMorphState.Weight(stats, type));
        set(BridgePrefix + "automatic", Automatic(read) ? 1 : 0);
        set(BridgePrefix + "generation", generation);
    }
}
