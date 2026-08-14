namespace Refii.Animations;

public static class Engine_AnimGdbHashes
{
    public const uint Animation = 0x8F32748Du;

    public const uint AnimationList = 0x63565E85u;

    public const uint Animations = 0xF96D7984u;

    public const uint AnimationSet = 0xE227399Fu;

    public const uint AnimationManagerComponent = 0x7C63CDDFu;

    public const uint AnimationName = 0x78B1F79Cu;

    public const uint AnimName = 0x49BD6FC7u;

    public const uint SkeletonFile = 0xC3D06E3Au;

    public const uint RetargetSkeletonFile = 0x64234AF2u;

    public const uint GraphicAppearanceComponent = 0xA7B6EF56u;

    public const uint GraphicAppearanceAnimatedMeshComponent = 0x21D312CAu;

    public const uint GraphicAppearanceMorphComponent = 0x0D4ADA1Au;

    public const uint CompositeModelRecord = 0x7CFF5EE2u;

    public const uint Model = 0x90347E14u;

    public const uint ModelFile = 0x0C17DB4Eu;

    public const uint ModelFile1 = 0x578E3BFBu;

    public const uint ModelFile2 = 0x578E3BF8u;

    public const uint FnvOffsetBasis = 0x811C9DC5u;

    public const uint FnvPrime = 0x01000193u;

    private static readonly uint[] ModelFields =
    {
        0x90347E14u, 0x0C17DB4Eu, 0x578E3BFBu, 0x578E3BF8u, 0x1372D766u,
        0x05294B89u, 0x547DDA3Eu, 0xD622E5ADu, 0x139ADC04u, 0x8D09F54Fu,
        0x8D09F551u, 0x8D09F56Eu, 0xA0CFEC37u, 0x017CDC23u, 0x017CDC24u,
        0x017CDC25u, 0x017CDC26u, 0x017CDC27u,
    };

    private static readonly uint[] AppearanceLinks =
    {
        0xA7B6EF56u, 0x21D312CAu, 0x0D4ADA1Au, 0x7CFF5EE2u, 0x29CF50D1u,
        0xCE642A15u, 0x77679B84u, 0x3C06A4E4u, 0x31FF8FCFu, 0x515A75DAu,
    };

    public static bool IsModelField(uint hash)
    {
        foreach (uint candidate in ModelFields)
        {
            if (candidate == hash)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsAppearanceLink(uint hash)
    {
        if (IsModelField(hash))
        {
            return true;
        }

        foreach (uint candidate in AppearanceLinks)
        {
            if (candidate == hash)
            {
                return true;
            }
        }

        return false;
    }

    public static uint Fnv1(string text)
    {
        uint hash = FnvOffsetBasis;
        foreach (char c in text)
        {
            hash = unchecked(hash * FnvPrime) ^ (byte)c;
        }

        return hash;
    }

}
