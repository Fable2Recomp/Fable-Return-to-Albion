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

    public const string RangeHint = "0,1,0.01";

    public static readonly HeroMorphSlider[] All =
    {
        new(HeroMorphKind.Strong, "Strong"),
        new(HeroMorphKind.Fat, "Fat"),
        new(HeroMorphKind.Tall, "Tall"),
        new(HeroMorphKind.Will, "Will"),
        new(HeroMorphKind.Young, "Young"),
        new(HeroMorphKind.Old, "Old"),
        new(HeroMorphKind.GoodPure, "Good Pure"),
        new(HeroMorphKind.Impure, "Impure"),
        new(HeroMorphKind.EvilPure, "Evil Pure"),
        new(HeroMorphKind.EvilImpure, "Evil Corrupt"),
    };

    public static IEnumerable<HeroMorphSlider> Available(HeroMorphSet? set) => All;

    public static IEnumerable<HeroMorphSlider> Available(HeroMorphSet? set, bool female) => All;

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
            case HeroMorphKind.Tall: weights.Tall = clamped; break;
            case HeroMorphKind.Will: weights.Will = clamped; break;
            case HeroMorphKind.Young: weights.Young = clamped; break;
            case HeroMorphKind.Old: weights.Old = clamped; break;
            case HeroMorphKind.GoodPure: weights.GoodPure = clamped; break;
            case HeroMorphKind.Impure: weights.Impure = clamped; break;
            case HeroMorphKind.EvilPure: weights.EvilPure = clamped; break;
            case HeroMorphKind.EvilImpure: weights.EvilImpure = clamped; break;
        }
    }
}
