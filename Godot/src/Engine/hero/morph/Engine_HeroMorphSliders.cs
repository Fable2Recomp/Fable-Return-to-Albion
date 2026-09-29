using System;

namespace Refii.Hero;

public readonly struct HeroMorphSlider
{
    public HeroMorphSlider(
        HeroMorphInput input, string label, float minimum, float maximum, float step)
    {
        Input = input;
        Label = label;
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
    }

    public HeroMorphInput Input { get; }

    public string Label { get; }

    public float Minimum { get; }

    public float Maximum { get; }

    public float Step { get; }

    public string RangeHint => FormattableString.Invariant($"{Minimum},{Maximum},{Step}");
}

public static class Engine_HeroMorphSliders
{
    /// <summary>Ranges are the guest's clamps: percentages [0,1], Height [-1,1] (<c>sub_824D7A68</c>).</summary>
    public static readonly HeroMorphSlider[] All =
    {
        new(HeroMorphInput.Strength, "Strength", 0.0f, 1.0f, 0.01f),
        new(HeroMorphInput.Skill, "Skill", 0.0f, 1.0f, 0.01f),
        new(HeroMorphInput.Will, "Will", 0.0f, 1.0f, 0.01f),
        new(HeroMorphInput.Fat, "Fat", 0.0f, 1.0f, 0.01f),
        new(HeroMorphInput.Height, "Height", -1.0f, 1.0f, 0.01f),
        new(HeroMorphInput.Tan, "Tan", 0.0f, 1.0f, 0.01f),
        new(HeroMorphInput.Morality, "Morality", 0.0f, 1.0f, 0.01f),
        new(HeroMorphInput.Purity, "Purity", 0.0f, 1.0f, 0.01f),
        new(HeroMorphInput.Age, "Age", 0.0f, 100.0f, 1.0f),
    };

    public static bool TryFind(string label, out HeroMorphSlider slider)
    {
        foreach (HeroMorphSlider candidate in All)
        {
            if (candidate.Label.Equals(label, StringComparison.Ordinal))
            {
                slider = candidate;
                return true;
            }
        }

        slider = default;
        return false;
    }

    public static void Set(HeroMorphStats stats, HeroMorphSlider slider, float value) =>
        stats[slider.Input] = Math.Clamp(value, slider.Minimum, slider.Maximum);
}
