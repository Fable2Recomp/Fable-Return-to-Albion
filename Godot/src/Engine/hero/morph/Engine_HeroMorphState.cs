namespace Refii.Hero;

/// <summary>
/// The guest's one weight function, <c>sub_822DA028(CECMorph, MorphType)</c>, which serves
/// vertex, skeletal, texture and particle morphs alike.
/// </summary>
public static class Engine_HeroMorphState
{
    /// <summary><c>flt_83319584</c>, script name SetMorphStrengthAtWhichFatCapped.</summary>
    public const float StrengthAtWhichFatCapped = 0.5f;

    /// <summary><c>flt_83319588</c>, SetMorphFatCap.</summary>
    public const float FatCap = 0.5f;

    /// <summary><c>flt_8331958C</c>, SetMorphSkillAtWhichStrengthCapped.</summary>
    public const float SkillAtWhichStrengthCapped = 0.6f;

    /// <summary><c>flt_83319590</c>, SetMorphStrengthCap.</summary>
    public const float StrengthCap = 0.6f;

    /// <summary>Below this age every type weighs 0 (<c>sub_821C2A18</c> stage 0 or 1).</summary>
    public const float AdultAge = 18.0f;

    public const uint TypeAlways = 1;
    public const uint TypeFat = 4;
    public const uint TypeStrength = 5;
    public const uint TypeSkill = 6;
    public const uint TypeScar = 19;

    public static float Weight(HeroMorphStats stats, uint type)
    {
        if (stats.Age < AdultAge)
        {
            return 0.0f;
        }

        float m = stats.Morality;
        float p = stats.Purity;

        switch (type)
        {
            case 1:
                return 1.0f;
            case 2:
                return UpperHalf(stats.Tan);
            case 3:
                return stats.Tan < 0.5f ? (0.5f - stats.Tan) * 2.0f : 0.0f;
            case 4:
                return stats.Fat > FatCap && Weight(stats, 5) > StrengthAtWhichFatCapped
                    ? FatCap
                    : stats.Fat;
            case 5:
                return stats.Strength > StrengthCap && Weight(stats, 6) > SkillAtWhichStrengthCapped
                    ? StrengthCap
                    : stats.Strength;
            case 6:
                return System.Math.Clamp(stats.Skill + stats.Height, 0.0f, 1.0f);
            case 7:
                return stats.Will;
            case 8:
                return stats.Height;
            case 9:
                return UpperHalf(m);
            case 10:
                return m > 0.5f && p > 0.5f ? (p + m) - 1.0f : 0.0f;
            case 12:
                return m < 0.5f && p >= 0.5f ? (0.5f - m) * 2.0f : 0.0f;
            case 14:
                return m < 0.5f && p < 0.5f ? 1.0f - (p + m) : 0.0f;
            case 15:
                return p > 0.5f && m >= 0.5f ? (p - 0.5f) * 2.0f : 0.0f;
            case 16:
                return p < 0.5f && m >= 0.5f ? (0.5f - p) * 2.0f : 0.0f;
            case 17:
            {
                int age = (int)stats.Age;
                return age < 18 || age > 30 ? 0.0f : 1.0f - ((age - 18) * 0.083333336f);
            }
            case 18:
            {
                int age = (int)stats.Age;
                return age < 30 ? 0.0f : age > 60 ? 1.0f : (age - 30) * 0.033333335f;
            }
            default:
                return 0.0f;
        }
    }

    private static float UpperHalf(float value) =>
        value > 0.5f ? (value - 0.5f) * 2.0f : 0.0f;
}
