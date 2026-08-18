namespace Refii.Hero;

public static class Engine_HeroMorphState
{
    public const float StrengthAtWhichFatCapped = 0.5f;

    public const float FatCap = 0.5f;

    public const float SkillAtWhichStrengthCapped = 0.6f;

    public const float StrengthCap = 0.6f;

    public static float TallWeight(HeroMorphWeights weights)
    {
        float lower = weights.Tall >= 0.0f ? weights.Tall : 0.0f;
        return lower - 1.0f >= 0.0f ? 1.0f : lower;
    }

    public static float StrongWeight(HeroMorphWeights weights)
    {
        float value = weights.Strong;
        if (value <= StrengthCap)
        {
            return value;
        }

        return TallWeight(weights) <= SkillAtWhichStrengthCapped ? value : StrengthCap;
    }

    public static float FatWeight(HeroMorphWeights weights)
    {
        float value = weights.Fat;
        if (value <= FatCap)
        {
            return value;
        }

        return StrongWeight(weights) <= StrengthAtWhichFatCapped ? value : FatCap;
    }

    public static float VertexWeight(HeroMorphWeights weights, HeroMorphKind kind) => kind switch
    {
        HeroMorphKind.Strong => StrongWeight(weights),
        HeroMorphKind.Fat => FatWeight(weights),
        HeroMorphKind.Young => weights.Young,
        HeroMorphKind.Old => weights.Old,
        HeroMorphKind.GoodPure => weights.GoodPure,
        HeroMorphKind.Impure => weights.Impure,
        HeroMorphKind.EvilPure => weights.EvilPure,
        HeroMorphKind.EvilImpure => weights.EvilImpure,
        _ => 0.0f,
    };

    public static float SkeletalWeight(HeroMorphWeights weights, SkeletalMorphKind kind) =>
        kind switch
        {
            SkeletalMorphKind.Fat => FatWeight(weights),
            SkeletalMorphKind.Strong => StrongWeight(weights),
            SkeletalMorphKind.Tall => TallWeight(weights),
            _ => 0.0f,
        };

    public static float TextureWeight(HeroMorphWeights weights, uint morphType) => morphType switch
    {
        4 => FatWeight(weights),
        5 => StrongWeight(weights),
        6 => TallWeight(weights),
        7 => weights.Will,
        9 => weights.GoodPure,
        10 => weights.GoodPure,
        12 => weights.EvilPure,
        13 => weights.EvilPure,
        14 => weights.EvilImpure,
        15 => weights.GoodPure,
        16 => weights.Impure,
        17 => weights.Young,
        18 => weights.Old,
        _ => 0.0f,
    };
}
