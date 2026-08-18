using System;
using System.Collections.Generic;

namespace Refii.Hero;

public readonly struct HeroMorphSlider
{
    public HeroMorphSlider(HeroMorphKind kind, string label)
    {
        Kind = kind;
        Label = label;
    }

    public HeroMorphKind Kind { get; }

    public string Label { get; }
}

public static class Engine_HeroMorphSliders
{
    public const float Minimum = 0.0f;

    public const float Maximum = 1.0f;

    public const float Step = 0.01f;

    public const string RangeHint = "0,1,0.01";

    public static readonly HeroMorphSlider[] All =
    {
        new(HeroMorphKind.Strong, "Strong"),
        new(HeroMorphKind.Fat, "Fat"),
        new(HeroMorphKind.Young, "Young"),
        new(HeroMorphKind.Old, "Old"),
        new(HeroMorphKind.Impure, "Impure"),
        new(HeroMorphKind.EvilPure, "Evil Pure"),
        new(HeroMorphKind.EvilImpure, "Evil Impure"),
    };

    public static IEnumerable<HeroMorphSlider> Available(HeroMorphSet? set)
    {
        foreach (HeroMorphSlider slider in All)
        {
            if (set is not null && set.Targets.ContainsKey(slider.Kind))
            {
                yield return slider;
            }
        }
    }

    public static bool TryFind(string label, out HeroMorphKind kind)
    {
        foreach (HeroMorphSlider slider in All)
        {
            if (slider.Label.Equals(label, StringComparison.Ordinal))
            {
                kind = slider.Kind;
                return true;
            }
        }

        kind = default;
        return false;
    }

    public static string LabelOf(HeroMorphKind kind)
    {
        foreach (HeroMorphSlider slider in All)
        {
            if (slider.Kind == kind)
            {
                return slider.Label;
            }
        }

        return kind.ToString();
    }

    public static void Set(HeroMorphWeights weights, HeroMorphKind kind, float value)
    {
        float clamped = value < Minimum ? Minimum : value > Maximum ? Maximum : value;

        switch (kind)
        {
            case HeroMorphKind.Strong: weights.Strong = clamped; break;
            case HeroMorphKind.Fat: weights.Fat = clamped; break;
            case HeroMorphKind.Young: weights.Young = clamped; break;
            case HeroMorphKind.Old: weights.Old = clamped; break;
            case HeroMorphKind.Impure: weights.Impure = clamped; break;
            case HeroMorphKind.EvilPure: weights.EvilPure = clamped; break;
            case HeroMorphKind.EvilImpure: weights.EvilImpure = clamped; break;
        }
    }
}
