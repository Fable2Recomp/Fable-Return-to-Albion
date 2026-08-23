using System;

namespace Refii.Level;

public enum ToneMapParam
{
    ExposureSpeed = 0,
    MiddleGray,
    Contrast,
    MinLumPct,
    AvgLumPct,
    MaxLumPct,
    ContrastEnhancement,
    ContrastEnhancementExponent,
    ContrastMaxDeviation,
    ContrastClamping,
    MinLumUpperBound,
    AvgLumLowerBound,
    MaxLumLowerBound,
    BloomThresholdAvgFactor,
    BloomFactor,
    BloomBlurSize,
    InverseBloomThreshold,
    InverseBloomDarkeningFactor,
    InverseBloomStrength,
    ColourSensitivityThreshold,
    OverlayScaleU,
    OverlayScaleV,
    OverlayStrength,
    Saturation,
    SaturationMasked,
    Brightness,
    BrightnessMasked,
    RadialBlurFactor,
    RadialBlurFalloff,
    RadialBlurMagnification,
    RadialBlurBlurSize,
    RadialBlurBlurIntensity,
    DofNearPlane,
    DofNearFocalPlane,
    DofFarFocalPlane,
    DofFarPlane,
    DofNearBlur,
    DofFarBlur,
    Count,
}

public sealed class ToneMapTheme
{
    public const int ParamCount = (int)ToneMapParam.Count;

    public static readonly float[] Defaults =
    {
        0.01f, 0.49783f, 0.0f, 0.01f, 0.5f, 0.99f,
        0.0f, 2.0f, 32.0f, 1.0f,
        0.1f, 0.3f, 0.6f,
        0.9f, 1.0f, 4.0f,
        0.2f, 0.01f, 0.0f,
        0.15f, 0.0f, 0.0f, 0.0f,
        0.0f, 0.0f, 0.0f, 0.0f,
        0.2f, 0.87f, 0.935f, 1.4f, 0.9f,
        1.0f, 5.0f, 6.0f, 60.0f, 0.0f, 0.0f,
    };

    public static readonly uint[] FieldHashes =
    {
        0x8CFC8E0B, 0x8CFF48DA, 0xF8759F93, 0xF8C7DC9F, 0x19E6701F, 0xE7ED3001,
        0x764DDEBF, 0x46F8F7BA, 0x4D7CF2C8, 0xD8307020,
        0x76DA55FF, 0xF93A84D6, 0x08DAFD90,
        0x0CCF2A5B, 0xAC1D43B5, 0x29190CAA,
        0xECA73689, 0xE7F3EABE, 0xF3ADF073,
        0xCC8CE6F7, 0x30042E1B, 0x30042E18, 0x48857310,
        0x5B748069, 0x6A5BB2AA, 0xC452018C, 0x6F2F9C27,
        0x1B39E8FA, 0x82D8E57A, 0xAC7AE46F, 0xAA18CDEB, 0x5EF04B5B,
        0x08B67B97, 0x458B0070, 0xA2A4EB86, 0x1EE3FFAE, 0xA2E867AC, 0x1D782F5F,
    };

    public bool HasAny { get; set; }

    public bool[] Present { get; } = new bool[ParamCount];

    public float[] V { get; } = (float[])Defaults.Clone();

    public float SourceTimeOfDay { get; set; } = -1.0f;

    public float this[ToneMapParam param]
    {
        get => V[(int)param];
        set => V[(int)param] = value;
    }

    public bool IsPresent(ToneMapParam param) => Present[(int)param];

    public static uint HashOf(ToneMapParam param) => FieldHashes[(int)param];

    public static float DefaultOf(ToneMapParam param) => Defaults[(int)param];

    public ToneMapTheme Clone()
    {
        var copy = new ToneMapTheme
        {
            HasAny = HasAny,
            SourceTimeOfDay = SourceTimeOfDay,
        };
        Array.Copy(Present, copy.Present, ParamCount);
        Array.Copy(V, copy.V, ParamCount);
        return copy;
    }

    public void CopyFrom(ToneMapTheme other)
    {
        HasAny = other.HasAny;
        SourceTimeOfDay = other.SourceTimeOfDay;
        Array.Copy(other.Present, Present, ParamCount);
        Array.Copy(other.V, V, ParamCount);
    }
}
