using System;
using Refii.Level;

namespace Refii.Lighting;

public static class Engine_PostFxModel
{
    public const int Bins = 512;
    public const float Range = 4.0f;
    public const int CurveSize = 512;
    public const float ExposureMin = 1.0f;
    public const float ExposureMax = 16.0f;
    public const float ExposureBlend = 0.05f;
    public const int GaussianTaps = 7;
    public const float SdrKneeY = 0.58f;
    public const float HdrKneeY = 0.80f;
    public const float MinWhiteX = 2.0f;
    public const float PeakToneBlend = 0.35f;

    private const float Pi = 3.14159265358979f;

    /// <summary>How the post-fx chain reaches the scene's colour layer.</summary>
    public enum ColourRoute
    {
        /// <summary>The chain cannot run on this colour layer; it is skipped for the frame.</summary>
        Unusable,

        /// <summary>The colour layer is a storage image and every pass binds it directly.</summary>
        Direct,

        /// <summary>
        /// The colour layer is not a storage image, so the chain runs on an owned rgba16f copy:
        /// the MSAA colour is resolved into it, and the result is copied back at the end.
        /// </summary>
        Staged,
    }

    /// <summary>
    /// Picks the route from the colour layer's actual format and usage bits rather than from
    /// what the renderer is expected to allocate.
    ///
    /// WHY THE USAGE BITS, NOT JUST THE FORMAT: with MSAA on, the colour layer Godot hands a
    /// compositor effect is the MSAA resolve target. On the Windows D3D12 build it arrived as
    /// rgba16f without STORAGE, so a format-only check passed, every <c>Image(colour, ...)</c>
    /// uniform set came back null, and the next dispatch took the process down.
    /// </summary>
    /// <param name="format">The colour layer's data format.</param>
    /// <param name="colourUsage">The colour layer's usage bits.</param>
    /// <param name="msaaUsage">The MSAA colour layer's usage bits, or null when MSAA is off.</param>
    /// <returns>The route the chain must take this frame.</returns>
    public static ColourRoute ChooseColourRoute(
        Godot.RenderingDevice.DataFormat format,
        Godot.RenderingDevice.TextureUsageBits colourUsage,
        Godot.RenderingDevice.TextureUsageBits? msaaUsage)
    {
        if (format != Godot.RenderingDevice.DataFormat.R16G16B16A16Sfloat)
        {
            return ColourRoute.Unusable;
        }

        if ((colourUsage & Godot.RenderingDevice.TextureUsageBits.StorageBit) != 0)
        {
            return ColourRoute.Direct;
        }

        // Staging needs the MSAA source to be resolvable (copy-from) and the scene layer to take
        // the result back (copy-to). Without MSAA there is nothing to resolve from, and a
        // non-MSAA layer without copy-from cannot be read into the staging texture either.
        bool canResolve = msaaUsage is { } msaa &&
            (msaa & Godot.RenderingDevice.TextureUsageBits.CanCopyFromBit) != 0;
        bool canWriteBack = (colourUsage & Godot.RenderingDevice.TextureUsageBits.CanCopyToBit) != 0;
        return canResolve && canWriteBack ? ColourRoute.Staged : ColourRoute.Unusable;
    }

    public sealed class Derived
    {
        public bool TonemapEnabled = true;
        public float[] SunUv = { 0.5f, 0.5f };
        public float ExposureSpeed = 0.01f;
        public float BlackMin = 0.1f;
        public float MidMin = 0.3f;
        public float WhiteMin = 0.6f;
        public float Knee = 0.36f;
        public float ColourSensitivity = 0.15f;
        public float[] Pct = { 0.01f, 0.5f, 0.99f };
        public float ContrastEnhancement;
        public float ContrastExponent = 2.0f;
        public float ContrastMaxDeviation = 32.0f;
        public float BloomFactor = 1.0f;
        public float BloomThresholdAvg = 0.9f;
        public float WhiteFactor = 0.9f;
        public float BloomBlurSize = 4.0f;
        public float[] InverseBloom = { 0.2f, 0.01f, 0.0f };
        public float RadialFactor = 0.2f;
        public float RadialFalloff = 0.87f;
        public float RadialMagnification = 0.935f;
        public float RadialBlurSize = 1.4f;
        public float RadialBlurIntensity = 0.9f;
        public float Saturation;
        public float SaturationMasked;
        public float Brightness;
        public float BrightnessMasked;
        public float Darkening;
        public float DarkeningParam;
        public float Gamma = 1.0f;
        public float DofNearPlane = 1.0f;
        public float DofNearFocal = 5.0f;
        public float DofFarFocal = 6.0f;
        public float DofFarPlane = 60.0f;
        public float DofNearBlur;
        public float DofFarBlur;

        public void CopyFrom(Derived o)
        {
            TonemapEnabled = o.TonemapEnabled;
            SunUv[0] = o.SunUv[0];
            SunUv[1] = o.SunUv[1];
            ExposureSpeed = o.ExposureSpeed;
            BlackMin = o.BlackMin;
            MidMin = o.MidMin;
            WhiteMin = o.WhiteMin;
            Knee = o.Knee;
            ColourSensitivity = o.ColourSensitivity;
            Pct[0] = o.Pct[0];
            Pct[1] = o.Pct[1];
            Pct[2] = o.Pct[2];
            ContrastEnhancement = o.ContrastEnhancement;
            ContrastExponent = o.ContrastExponent;
            ContrastMaxDeviation = o.ContrastMaxDeviation;
            BloomFactor = o.BloomFactor;
            BloomThresholdAvg = o.BloomThresholdAvg;
            WhiteFactor = o.WhiteFactor;
            BloomBlurSize = o.BloomBlurSize;
            InverseBloom[0] = o.InverseBloom[0];
            InverseBloom[1] = o.InverseBloom[1];
            InverseBloom[2] = o.InverseBloom[2];
            RadialFactor = o.RadialFactor;
            RadialFalloff = o.RadialFalloff;
            RadialMagnification = o.RadialMagnification;
            RadialBlurSize = o.RadialBlurSize;
            RadialBlurIntensity = o.RadialBlurIntensity;
            Saturation = o.Saturation;
            SaturationMasked = o.SaturationMasked;
            Brightness = o.Brightness;
            BrightnessMasked = o.BrightnessMasked;
            Darkening = o.Darkening;
            DarkeningParam = o.DarkeningParam;
            Gamma = o.Gamma;
            DofNearPlane = o.DofNearPlane;
            DofNearFocal = o.DofNearFocal;
            DofFarFocal = o.DofFarFocal;
            DofFarPlane = o.DofFarPlane;
            DofNearBlur = o.DofNearBlur;
            DofFarBlur = o.DofFarBlur;
        }
    }

    private static float Clampf(float v, float lo, float hi) => MathF.Min(MathF.Max(v, lo), hi);

    public static void Derive(
        ToneMapTheme t, bool tonemapEnabled, float[]? sunUv,
        float darkening, float darkeningParam, float gamma, Derived o)
    {
        float[] v = t.V;
        o.TonemapEnabled = tonemapEnabled;
        o.SunUv[0] = sunUv is not null ? sunUv[0] : 0.5f;
        o.SunUv[1] = sunUv is not null ? sunUv[1] : 0.5f;
        o.ExposureSpeed = v[(int)ToneMapParam.ExposureSpeed];

        float cc = v[(int)ToneMapParam.ContrastClamping];
        float minUb = v[(int)ToneMapParam.MinLumUpperBound];
        o.BlackMin = ((1.0f - minUb) * (1.0f - cc)) + minUb;
        o.MidMin = v[(int)ToneMapParam.AvgLumLowerBound] * cc;
        o.WhiteMin = v[(int)ToneMapParam.MaxLumLowerBound] * cc;

        o.Knee = MathF.Exp((v[(int)ToneMapParam.MiddleGray] * 10.0f) - 6.0f);
        o.ColourSensitivity = v[(int)ToneMapParam.ColourSensitivityThreshold];

        float k = v[(int)ToneMapParam.Contrast];
        float pmin = v[(int)ToneMapParam.MinLumPct];
        float pavg = v[(int)ToneMapParam.AvgLumPct];
        float pmax = v[(int)ToneMapParam.MaxLumPct];
        o.Pct[0] = pmin + ((pavg - pmin) * k);
        o.Pct[1] = pavg;
        o.Pct[2] = pavg + ((pmax - pavg) * (1.0f - k));

        o.ContrastEnhancement = v[(int)ToneMapParam.ContrastEnhancement];
        o.ContrastExponent = v[(int)ToneMapParam.ContrastEnhancementExponent];
        o.ContrastMaxDeviation = v[(int)ToneMapParam.ContrastMaxDeviation];
        o.BloomFactor = v[(int)ToneMapParam.BloomFactor];
        o.BloomThresholdAvg = v[(int)ToneMapParam.BloomThresholdAvgFactor];
        o.WhiteFactor = 0.9f;
        o.BloomBlurSize = Clampf(v[(int)ToneMapParam.BloomBlurSize], 1.0f, 4.0f);
        o.InverseBloom[0] = v[(int)ToneMapParam.InverseBloomThreshold];
        o.InverseBloom[1] = v[(int)ToneMapParam.InverseBloomDarkeningFactor];
        o.InverseBloom[2] = v[(int)ToneMapParam.InverseBloomStrength];
        o.RadialFactor = v[(int)ToneMapParam.RadialBlurFactor];
        o.RadialFalloff = v[(int)ToneMapParam.RadialBlurFalloff];
        o.RadialMagnification = v[(int)ToneMapParam.RadialBlurMagnification];
        o.RadialBlurSize = v[(int)ToneMapParam.RadialBlurBlurSize];
        o.RadialBlurIntensity = v[(int)ToneMapParam.RadialBlurBlurIntensity];
        o.Saturation = v[(int)ToneMapParam.Saturation];
        o.SaturationMasked = v[(int)ToneMapParam.SaturationMasked];
        o.Brightness = v[(int)ToneMapParam.Brightness];
        o.BrightnessMasked = v[(int)ToneMapParam.BrightnessMasked];
        o.Darkening = darkening;
        o.DarkeningParam = darkeningParam;
        o.Gamma = gamma;
        o.DofNearPlane = v[(int)ToneMapParam.DofNearPlane];
        o.DofNearFocal = v[(int)ToneMapParam.DofNearFocalPlane];
        o.DofFarFocal = v[(int)ToneMapParam.DofFarFocalPlane];
        o.DofFarPlane = v[(int)ToneMapParam.DofFarPlane];
        o.DofNearBlur = v[(int)ToneMapParam.DofNearBlur];
        o.DofFarBlur = v[(int)ToneMapParam.DofFarBlur];
    }

    public static void FixedGaussianWeights(float sigma, float[] outWeights)
    {
        float sg = MathF.Max(sigma, 1e-4f);
        Span<float> g = stackalloc float[GaussianTaps];
        for (int i = 0; i < GaussianTaps; i++)
        {
            float x = i;
            g[i] = MathF.Exp(-(x * x) / (2.0f * sg * sg)) / (MathF.Sqrt(2.0f * Pi) * sg);
        }

        float norm = g[0];
        for (int i = 1; i < GaussianTaps; i++)
        {
            norm += 2.0f * g[i];
        }

        for (int i = 0; i < GaussianTaps; i++)
        {
            outWeights[i] = g[i] / MathF.Max(norm, 1e-8f);
        }
    }

    public static float Luminance(float r, float g, float b) =>
        (0.2125f * r) + (0.7154f * g) + (0.0721f * b);

    /// <summary>
    /// Computes <c>1 / W_x^2</c> in curve space (<c>x = l_exp * knee_n / prev_exp</c>) where
    /// <c>W_x = max((smWhite - smBlack) * knee_n, 2.0)</c>, preventing Extended Reinhard from
    /// becoming super-linear when <c>smWhite - smBlack &lt; 1.0</c>.
    /// </summary>
    public static float ComputeInvWhiteSq(float smBlack, float smMid, float smWhite, float knee)
    {
        float kneeN = knee / MathF.Max(smMid - smBlack, 1e-4f);
        float wb = MathF.Max(smWhite - smBlack, 1e-4f);
        float whiteX = MathF.Max(wb * kneeN, MinWhiteX);
        return 1.0f / (whiteX * whiteX);
    }

    /// <summary>
    /// Unified C1 exponential highlight shoulder applied in curve output space (<c>curved &gt; knee</c>)
    /// for both SDR (<c>headroom == 1.0</c>, <c>knee = 0.58</c>) and HDR (<c>headroom &gt; 1.0</c>, <c>knee = 0.80</c>).
    /// </summary>
    public static float FableShoulder(float curved, float headroom)
    {
        float knee = headroom > 1.001f ? HdrKneeY : SdrKneeY;
        if (curved <= knee)
        {
            return curved;
        }

        float room = MathF.Max(headroom - knee, 1e-4f);
        return knee + (room * (1.0f - MathF.Exp(-(curved - knee) / room)));
    }
}
