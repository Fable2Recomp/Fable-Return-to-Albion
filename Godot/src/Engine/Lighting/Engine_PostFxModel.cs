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

    public sealed class Histogram
    {
        public uint[] Counts = new uint[Bins + 1];
        public float[] Cdf = new float[Bins];
        public uint Total;
        public int IdxMin;
        public int IdxAvg;
        public int IdxMax;
        public int IdxLast;
        public int IdxMean;
        public float MinPct;
        public float Mean = 0.5f;
        public float AvgPct;
        public float MaxPct;
        public float Last;
        public float ExposureUsed = 1.0f;

        public void CopyFrom(Histogram o)
        {
            Array.Copy(o.Counts, Counts, Bins + 1);
            Array.Copy(o.Cdf, Cdf, Bins);
            Total = o.Total;
            IdxMin = o.IdxMin;
            IdxAvg = o.IdxAvg;
            IdxMax = o.IdxMax;
            IdxLast = o.IdxLast;
            IdxMean = o.IdxMean;
            MinPct = o.MinPct;
            Mean = o.Mean;
            AvgPct = o.AvgPct;
            MaxPct = o.MaxPct;
            Last = o.Last;
            ExposureUsed = o.ExposureUsed;
        }
    }

    public sealed class State
    {
        public float Exposure = 4.0f;
        public float Applied = 4.0f;
        public float Prev = 4.0f;
        public float SmBlack;
        public float SmMid = 0.25f;
        public float SmWhite = 1.0f;
        public float SmP99 = 1.0f;
        public float RawBlack;
        public float RawMean;
        public float RawWhite;
        public float RawExp = 1.0f;
        public float RawGain = 1.0f;
        public float FadeK = 1.0f;
        public bool HasHistory;
        public double LastTime;
        public float[] Curve = new float[CurveSize];
        public bool CurveValid;
        public float Threshold;
        public Histogram Hist = new();

        public void CopyFrom(State o)
        {
            Exposure = o.Exposure;
            Applied = o.Applied;
            Prev = o.Prev;
            SmBlack = o.SmBlack;
            SmMid = o.SmMid;
            SmWhite = o.SmWhite;
            SmP99 = o.SmP99;
            RawBlack = o.RawBlack;
            RawMean = o.RawMean;
            RawWhite = o.RawWhite;
            RawExp = o.RawExp;
            FadeK = o.FadeK;
            HasHistory = o.HasHistory;
            LastTime = o.LastTime;
            Array.Copy(o.Curve, Curve, CurveSize);
            CurveValid = o.CurveValid;
            Threshold = o.Threshold;
            Hist.CopyFrom(o.Hist);
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

    public static void Analyze(
        ReadOnlySpan<float> luminance, float exposureUsed, float[] pct, Histogram h)
    {
        Array.Clear(h.Counts, 0, h.Counts.Length);
        float binScale = Bins / Range;
        for (int i = 0; i < luminance.Length; i++)
        {
            float l = luminance[i];
            if (!(l > 0.0f))
            {
                l = 0.0f;
            }

            if (l > Range)
            {
                l = Range;
            }

            int b = (int)(l * binScale);
            if (b > Bins)
            {
                b = Bins;
            }

            ++h.Counts[b];
        }

        ulong total = 0;
        for (int i = 0; i < Bins; i++)
        {
            total += h.Counts[i];
        }

        h.Total = (uint)total;

        double thrMin = (double)pct[0] * total;
        double thrAvg = (double)pct[1] * total;
        double thrMax = (double)pct[2] * total;
        ulong cum = 0;
        ulong weighted = 0;
        h.IdxMin = h.IdxAvg = h.IdxMax = h.IdxLast = 0;
        for (int i = 0; i < Bins; i++)
        {
            uint c = h.Counts[i];
            if ((double)cum < thrMin)
            {
                h.IdxMin = i;
            }

            if ((double)cum < thrAvg)
            {
                h.IdxAvg = i;
            }

            if ((double)cum < thrMax)
            {
                h.IdxMax = i;
            }

            if (c != 0)
            {
                h.IdxLast = i;
            }

            cum += c;
            weighted += (ulong)i * c;
            h.Cdf[i] = total > 0 ? (float)((double)cum / total) : 0.0f;
        }

        float lumPerBin = Range / Bins;
        float invExp = 1.0f / MathF.Max(exposureUsed, 1e-6f);
        h.MinPct = h.IdxMin * lumPerBin;
        h.AvgPct = h.IdxAvg * lumPerBin;
        h.MaxPct = h.IdxMax * lumPerBin;
        h.Last = h.IdxLast * lumPerBin;
        h.Mean = total > 0 ? (float)((double)weighted * lumPerBin / total) : 0.5f;
        h.ExposureUsed = exposureUsed;
        h.MinPct *= invExp;
        h.AvgPct *= invExp;
        h.MaxPct *= invExp;
        h.Last *= invExp;
        h.Mean *= invExp;
        h.IdxMean = (int)(h.Mean * binScale);
    }

    public static float FadeWeight(State s, Derived d, double now)
    {
        if (!s.HasHistory)
        {
            return 1.0f;
        }

        float speed = Clampf(d.ExposureSpeed, 0.0f, 1.0f);
        double dt = Math.Max(now - s.LastTime, 0.0);
        return 1.0f - MathF.Pow(1.0f - speed, (float)(dt * 30.0));
    }

    public static void UpdateBounds(State s, Derived d, Histogram h)
    {
        // The remaster's dark lift is a RAW GAIN (Engine_PostFxParams.RawGain): the statistics
        // and the curve's input scale together, the guest's floors and curve stay as they are.
        float gain = Engine_PostFxParams.RawGain(h.Mean, d.MidMin);
        float black = MathF.Min(h.MinPct * gain, d.BlackMin);
        float mid = MathF.Max(h.Mean * gain, d.MidMin);
        float white = MathF.Max(h.MaxPct * gain, d.WhiteMin);
        s.RawGain = gain;
        float k = s.FadeK;
        s.SmBlack += (black - s.SmBlack) * k;
        s.SmMid += (mid - s.SmMid) * k;
        s.SmWhite += (white - s.SmWhite) * k;
        s.SmP99 += ((h.MaxPct * gain) - s.SmP99) * k;
        s.RawBlack = h.MinPct;
        s.RawMean = h.Mean;
        s.RawWhite = h.MaxPct;
        s.RawExp = h.ExposureUsed;
        if (!ReferenceEquals(s.Hist, h))
        {
            s.Hist.CopyFrom(h);
        }
    }


    public static void BuildToneCurve(State s, Derived d, float[] lut)
    {
        float expo = d.ContrastExponent;
        float k1 = d.ContrastEnhancement;
        float toe = d.ContrastMaxDeviation;
        float knee = d.Knee;
        float prevExp = MathF.Max(s.Prev, 1e-4f);
        float maxS = s.SmP99;
        float blackS = s.SmBlack;
        float midS = s.SmMid;
        float whiteS = s.SmWhite;

        float binToLum = MathF.Max(s.RawGain, 1f) * Range / (prevExp * CurveSize);
        float shoulder = MathF.Max(maxS - 0.2f, 0.0f) * toe;
        float denom = MathF.Max((CurveSize / Range) * maxS * prevExp, 256.0f);
        float wb = MathF.Max(whiteS - blackS, 1e-4f);
        float invW2 = 1.0f / (wb * wb);
        float kneeN = knee / MathF.Max(midS - blackS, 1e-4f);
        bool isSquare = MathF.Abs(expo - 2.0f) < 0.003f;
        float k = s.CurveValid ? s.FadeK : 1.0f;

        Span<float> cur = stackalloc float[CurveSize];
        for (int i = 0; i < CurveSize; i++)
        {
            float x = i * binToLum * kneeN;
            float reinhard = Engine_PostFxParams.FilmicCurve
                ? Filmic(x * 0.6f)
                : x * (1.0f + (x * invW2)) / (1.0f + x);
            float value = reinhard;
            if (k1 > 0.0f)
            {
                float t = MathF.Min(i / denom, 1.0f);
                float cdf = s.Hist.Cdf[i];
                float eq = Clampf(cdf, t - shoulder, t + shoulder);
                float pw = isSquare ? eq * eq : MathF.Pow(MathF.Max(eq, 0.0f), expo);
                value = reinhard + ((pw - reinhard) * k1);
            }

            cur[i] = value;
        }

        for (int i = 0; i < CurveSize; i++)
        {
            s.Curve[i] = s.CurveValid ? s.Curve[i] + ((cur[i] - s.Curve[i]) * k) : cur[i];
        }

        s.CurveValid = true;

        for (int x = 0; x < CurveSize; x++)
        {
            int j = Math.Min(x, CurveSize - 2);
            float c = 0.5f * (s.Curve[j] + s.Curve[j + 1]);
            float lum = ((x + 0.5f) / CurveSize) * Range;
            lut[x] = c / MathF.Max(lum, 1e-6f);
        }
    }

    /// <summary>The ACES fitted curve the modern model tones with: a toe and a shoulder.</summary>
    public static float Filmic(float x) =>
        Clampf((x * ((2.51f * x) + 0.03f)) / ((x * ((2.43f * x) + 0.59f)) + 0.14f), 0f, 1f);

    /// <summary>
    /// The guest's auto-exposure step. Must stay identical to the compute copy in
    /// <c>fable_postfx_reduce.glsl</c>, which is the one that actually runs.
    /// </summary>
    /// <remarks>
    /// Every constant is `sub_82191C18`'s own: 16.0 (`flt_82099410`), 0.1 (`flt_8209B650`),
    /// 0.75 (`flt_8209949C`), 10.0 (`flt_82000A54`), 0.05 (`flt_820992D8`), and the [1, 16]
    /// bound applied twice, once to the target and once to the blended result.
    /// <see cref="Range"/> is the histogram's full scale, the guest's `+0x10C` = 4.0. It reads the
    /// RAW per-frame statistics; the smoothed ones feed the curve and the bright pass.
    ///
    /// <paramref name="dt"/> is the only departure: the guest applies 0.05 once per render frame
    /// at about 30 fps with no dt term, so the step is referred to 30 Hz with the guest's own
    /// compensation form (`1 - (1 - speed)^(dt * 30)`, 0x82276810..0x82276890), exactly 0.05 at
    /// 30 fps.
    ///
    /// This is the CPU reference, not the live path; keep it in step with the shader.
    /// </remarks>
    public static void UpdateExposure(State s, Derived d, float dt = 1.0f / 30.0f)
    {
        Histogram h = s.Hist;
        float m = MathF.Min(h.MaxPct, 16.0f * h.Mean) * h.ExposureUsed;
        m = MathF.Max(m, 0.1f);
        float r = Clampf((0.75f * Range) / m, 0.1f, 10.0f);
        float prev = s.Exposure;
        float target = Clampf(prev * r, ExposureMin, ExposureMax);
        float step = 1.0f - MathF.Pow(1.0f - ExposureBlend, MathF.Max(dt, 0.0f) * 30.0f);
        float e = Clampf(prev + (step * (target - prev)), ExposureMin, ExposureMax);
        s.Prev = prev;
        s.Exposure = e;
        s.Applied = d.TonemapEnabled ? e : 1.0f;
    }

    public static void LockExposure(State s, Derived d, float lockValue)
    {
        s.Prev = s.Exposure;
        s.Exposure = Clampf(lockValue, ExposureMin, ExposureMax);
        s.Applied = d.TonemapEnabled ? s.Exposure : 1.0f;
    }

    public static void EndFrame(State s, double now)
    {
        s.LastTime = now;
        s.HasHistory = true;
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

    public static float SampleLut(float[] lut, float luminance)
    {
        int index = (int)(luminance * (1.0f / Range) * CurveSize);
        return lut[Math.Clamp(index, 0, CurveSize - 1)];
    }
}
