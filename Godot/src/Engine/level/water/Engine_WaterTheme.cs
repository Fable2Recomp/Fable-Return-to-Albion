using System;
using System.Collections.Generic;
using Godot;
using Refii.Lighting;

namespace Refii.Level;

public static class WaterThemeParam
{
    public const int ShallowR = 0;
    public const int ShallowG = 1;
    public const int ShallowB = 2;
    public const int DeepR = 3;
    public const int DeepG = 4;
    public const int DeepB = 5;
    public const int NormalScale = 6;
    public const int FresnelBias = 7;
    public const int ReflectionBias = 8;
    public const int ReflectionScale = 9;
    public const int RefractionScale = 10;
    public const int ReflectionStrength = 11;
    public const int GlitterBend = 12;
    public const int GlitterStrength = 13;
    public const int GlitterPower = 14;
    public const int ShadowScaleBias = 15;
    public const int MaxRefractionDistance = 16;
    public const int EdgeBlendMax = 17;
    public const int EdgeBlendMin = 18;
    public const int EdgeBlendBias = 19;
    public const int NormalMapSpeed0X = 20;
    public const int NormalMapSpeed0Y = 21;
    public const int NormalMapSpeed1X = 22;
    public const int NormalMapSpeed1Y = 23;
    public const int NormalMapScale0 = 24;
    public const int NormalMapScale1 = 25;
    public const int DetailMapSpeedX = 26;
    public const int DetailMapSpeedY = 27;
    public const int DetailMapScale = 28;

    /// <summary>Theme param 102 `WATER_VISTA_DISABLED`: the vista ocean draws only while this is
    /// exactly 0 (`sub_821D5068`, theme+1648).</summary>
    public const int DisableVistaWater = 29;

    public const int Count = 30;
}

public sealed class WaterTheme
{
    public float[] Values { get; } = (float[])Defaults.Clone();

    public bool[] Stated { get; } = new bool[WaterThemeParam.Count];

    public bool HasAny { get; set; }

    public bool HasNormalMap { get; set; }

    public uint NormalMapHash { get; set; }

    public float SourceTimeOfDay { get; set; } = -1.0f;

    public float this[int index] => Values[index];

    public static readonly float[] Defaults =
    {
        0.37f, 0.47f, 0.75f,
        0.0f, 0.1275f, 0.1913f,
        0.05f,
        0.2f,
        0.0f,
        2.0f,
        2.0f,
        0.75f,
        0.3f,
        5.0f,
        128.0f,
        0.95f,
        75.0f,
        1.0f,
        0.5f,
        1.0f,
        0.052f, 0.011f, -0.019f, 0.019f,
        0.047f, 0.055f,
        0.0f, 0.0f, 0.0f,
        0.0f,
    };

    public static WaterTheme Default => new();
}

public static class Engine_WaterTheme
{
    public const uint WaterRecord = 0x1E6889DA;

    private const uint ShallowWaterColourRedHash = 0xB2ECBF11;
    private const uint ShallowWaterColourGreenHash = 0x25F0E705;
    private const uint ShallowWaterColourBlueHash = 0x8BF17608;
    private const uint DeepWaterColourRedHash = 0x1750476D;
    private const uint DeepWaterColourGreenHash = 0x61671F01;
    private const uint DeepWaterColourBlueHash = 0x632A3D74;

    private static readonly uint[] NormalMapFieldHashes =
    {
        0xFA7F9D8Au, // NormalMap
        0x1B3CDEBBu, // NormalMapTexture
        0xE278DAD3u, // WaterNormalMap
        0x3F437E51u, // NormalTexture
    };

    private static readonly (int Index, uint Hash)[] FloatFields =
    {
        (WaterThemeParam.NormalScale, 0xA027B7EE),
        (WaterThemeParam.FresnelBias, 0x73C59519),
        (WaterThemeParam.ReflectionBias, 0x4838AA55),
        (WaterThemeParam.ReflectionScale, 0x9BA6BDE0),
        (WaterThemeParam.RefractionScale, 0x623C0662),
        (WaterThemeParam.ReflectionStrength, 0xAF315449),
        (WaterThemeParam.GlitterBend, 0xEAFD017D),
        (WaterThemeParam.GlitterStrength, 0x304221FF),
        (WaterThemeParam.GlitterPower, 0x704F875F),
        (WaterThemeParam.ShadowScaleBias, 0x9E07424A),
        (WaterThemeParam.MaxRefractionDistance, 0x671D3125),
        (WaterThemeParam.EdgeBlendMax, 0x52233307),
        (WaterThemeParam.EdgeBlendMin, 0x5A234079),
        (WaterThemeParam.EdgeBlendBias, 0x79F85F10),
        (WaterThemeParam.NormalMapSpeed0X, 0x61396EBB),
        (WaterThemeParam.NormalMapSpeed0Y, 0x61396EBA),
        (WaterThemeParam.NormalMapSpeed1X, 0x60396D08),
        (WaterThemeParam.NormalMapSpeed1Y, 0x60396D09),
        (WaterThemeParam.NormalMapScale0, 0xD6E03A0C),
        (WaterThemeParam.NormalMapScale1, 0xD6E03A0D),
        (WaterThemeParam.DetailMapSpeedX, 0xB2CE584B),
        (WaterThemeParam.DetailMapSpeedY, 0xB2CE584A),
        (WaterThemeParam.DetailMapScale, 0x0BF8E61E),
        (WaterThemeParam.DisableVistaWater, 0xE5E4C38B),
    };

    public static bool TryExtract(
        IReadOnlyList<byte[]> gdbs, float targetTime, out WaterTheme theme)
    {
        theme = new WaterTheme();

        Chain? chain = Chain.Create(gdbs);
        if (chain is null)
        {
            return false;
        }

        float time = float.IsFinite(targetTime) && targetTime >= 0f ? targetTime : 0.5f;
        if (!chain.SelectThemeFromDaySet(time, out RecordRef dayTheme, out float dayTime))
        {
            return false;
        }

        RecordRef water = chain.ResolveRecordField(dayTheme, WaterRecord);
        if (!water.Valid)
        {
            return false;
        }

        theme.SourceTimeOfDay = dayTime;

        var colour = new float[3];
        if (chain.ReadFlatColour(
                water, ShallowWaterColourRedHash, ShallowWaterColourGreenHash,
                ShallowWaterColourBlueHash, 0, colour))
        {
            Set(theme, WaterThemeParam.ShallowR, colour[0]);
            Set(theme, WaterThemeParam.ShallowG, colour[1]);
            Set(theme, WaterThemeParam.ShallowB, colour[2]);
        }

        if (chain.ReadFlatColour(
                water, DeepWaterColourRedHash, DeepWaterColourGreenHash,
                DeepWaterColourBlueHash, 0, colour))
        {
            Set(theme, WaterThemeParam.DeepR, colour[0]);
            Set(theme, WaterThemeParam.DeepG, colour[1]);
            Set(theme, WaterThemeParam.DeepB, colour[2]);
        }

        foreach ((int index, uint hash) in FloatFields)
        {
            if (chain.ReadFloat(water, hash, out float value))
            {
                Set(theme, index, value);
            }
        }

        foreach (uint fieldHash in NormalMapFieldHashes)
        {
            if (chain.ReadHash(water, fieldHash, out uint normalHash))
            {
                theme.NormalMapHash = normalHash;
                theme.HasNormalMap = true;
                theme.HasAny = true;
                break;
            }
        }

        return theme.HasAny;
    }

    private static void Set(WaterTheme theme, int index, float value)
    {
        if (!float.IsFinite(value))
        {
            return;
        }

        theme.Values[index] = value;
        theme.Stated[index] = true;
        theme.HasAny = true;
    }

    private static readonly object DegenerateLogGate = new();
    private static readonly HashSet<(int MinBits, int MaxBits, int BiasBits)> LoggedDegenerateEdgeBlends = new();

    internal static void ResetLoggedDegenerateEdgeBlendsForTests()
    {
        lock (DegenerateLogGate)
        {
            LoggedDegenerateEdgeBlends.Clear();
        }
    }

    public static bool ShouldLogDegenerateEdgeBlend(float min, float max, float bias)
    {
        var key = (
            BitConverter.SingleToInt32Bits(min),
            BitConverter.SingleToInt32Bits(max),
            BitConverter.SingleToInt32Bits(bias));
        lock (DegenerateLogGate)
        {
            return LoggedDegenerateEdgeBlends.Add(key);
        }
    }

    public static Vector4 EdgeBlend(WaterTheme theme)
    {
        float max = theme[WaterThemeParam.EdgeBlendMax];
        float min = theme[WaterThemeParam.EdgeBlendMin];
        float bias = theme[WaterThemeParam.EdgeBlendBias];

        if (!float.IsFinite(min) || !float.IsFinite(max) || !float.IsFinite(bias) ||
            max - min <= 1e-4f)
        {
            if (ShouldLogDegenerateEdgeBlend(min, max, bias) &&
                !AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase))
            {
                GD.Print(
                    $"[water] theme edge blend is degenerate (min={min}, max={max}, bias={bias}) — " +
                    "using the neutral ramp");
            }
            return new Vector4(0f, 1f, 0f, 0f);
        }

        return new Vector4(min, max, bias, 0f);
    }
}
