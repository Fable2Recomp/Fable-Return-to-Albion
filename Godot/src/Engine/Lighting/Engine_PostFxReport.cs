using System;
using System.Text;

namespace Refii.Lighting;

public static class Engine_PostFxReport
{
    public const int Bands = 16;

    private const float BucketWidth = Engine_PostFxModel.Range / Engine_PostFxModel.Bins;

    public static string Format(
        ReadOnlySpan<uint> bins,
        ReadOnlySpan<float> state,
        int width,
        int height,
        float deltaSeconds,
        long frame)
    {
        var text = new StringBuilder();
        text.Append($"[postfx] frame {frame} {width}x{height} dt={deltaSeconds * 1000.0f:0.0}ms")
            .Append('\n');

        if (state.Length < Engine_PostFxState.Floats)
        {
            return text.Append($"[postfx]   state buffer is {state.Length} floats, expected " +
                $"{Engine_PostFxState.Floats}").ToString();
        }

        text.Append(
            $"[postfx]   exposure={state[Engine_PostFxState.Exposure]:0.000} " +
            $"applied={state[Engine_PostFxState.Applied]:0.000} " +
            $"prev={state[Engine_PostFxState.Prev]:0.000} " +
            $"frame=(exposure {state[Engine_PostFxState.ExposureFrame]:0.000} " +
            $"applied {state[Engine_PostFxState.AppliedFrame]:0.000}) " +
            $"material={state[Engine_PostFxState.MaterialExposure]:0.000}").Append('\n');
        text.Append(
            $"[postfx]   smoothed black={state[Engine_PostFxState.SmBlack]:0.0000} " +
            $"mid={state[Engine_PostFxState.SmMid]:0.0000} " +
            $"white={state[Engine_PostFxState.SmWhite]:0.0000} " +
            $"p99={state[Engine_PostFxState.SmP99]:0.0000}   " +
            $"threshold={state[Engine_PostFxState.Threshold]:0.0000} " +
            $"fade_k={state[Engine_PostFxState.FadeK]:0.0000} " +
            $"history={state[Engine_PostFxState.HasHistory]:0} " +
            $"curve={state[Engine_PostFxState.CurveValid]:0}").Append('\n');
        float[] tv = Engine_PostFxParams.Theme.V;
        text.Append(
            $"[postfx]   theme exposureSpeed={tv[(int)Refii.Level.ToneMapParam.ExposureSpeed]:0.000} " +
            $"middleGray={tv[(int)Refii.Level.ToneMapParam.MiddleGray]:0.000} " +
            $"minUB={tv[(int)Refii.Level.ToneMapParam.MinLumUpperBound]:0.000} " +
            $"avgLB={tv[(int)Refii.Level.ToneMapParam.AvgLumLowerBound]:0.000} " +
            $"maxLB={tv[(int)Refii.Level.ToneMapParam.MaxLumLowerBound]:0.000} " +
            $"contrastClamp={tv[(int)Refii.Level.ToneMapParam.ContrastClamping]:0.000} " +
            $"brightness={tv[(int)Refii.Level.ToneMapParam.Brightness]:0.000} " +
            $"darkLift={Engine_PostFxParams.DarkLift:0.00} modern={Engine_LightingFrame.ModernModel}").Append('\n');
        text.Append(
            $"[postfx]   raw black={state[Engine_PostFxState.RawBlack]:0.0000} " +
            $"mean={state[Engine_PostFxState.RawMean]:0.0000} " +
            $"white={state[Engine_PostFxState.RawWhite]:0.0000} " +
            $"exp={state[Engine_PostFxState.RawExp]:0.000} gain={state[Engine_PostFxState.RawGain]:0.00}   " +
            $"hist idx=(min {state[Engine_PostFxState.HistIdxMin]:0} " +
            $"avg {state[Engine_PostFxState.HistIdxAvg]:0} " +
            $"max {state[Engine_PostFxState.HistIdxMax]:0} " +
            $"last {state[Engine_PostFxState.HistIdxLast]:0}) " +
            $"total={state[Engine_PostFxState.HistTotal]:0}").Append('\n');

        // WHAT THE BLOOM ACTUALLY COVERS. The bright pass cuts at
        // `clamp(l/exposure - threshold, 0, 1)`, so a bin blooms when its luminance clears
        // `threshold * exposure`. Coverage is the fraction of the frame feeding the screen
        // blend, and mean_cut is how hard: together they say whether the wash is a broad, weak
        // pedestal or a few genuinely bright sources, which the bounds alone cannot.
        int scanned = Math.Min(bins.Length, Engine_PostFxModel.Bins);
        float cutExposure = Math.Max(state[Engine_PostFxState.ExposureFrame], 1e-4f);
        float cutThreshold = state[Engine_PostFxState.Threshold];
        long cutTotal = 0;
        long cutAbove = 0;
        double cutWeighted = 0.0;
        for (int i = 0; i < scanned; i++)
        {
            long count = bins[i];
            cutTotal += count;
            float cut = Math.Clamp((i * BucketWidth) / cutExposure - cutThreshold, 0.0f, 1.0f);
            if (cut > 0.0f)
            {
                cutAbove += count;
                cutWeighted += cut * count;
            }
        }

        text.Append(
            $"[postfx]   bloom cut>{cutThreshold * cutExposure:0.0000} " +
            $"coverage={(cutTotal > 0 ? 100.0 * cutAbove / cutTotal : 0.0):0.00}% " +
            $"mean_cut={(cutTotal > 0 ? cutWeighted / cutTotal : 0.0):0.0000} " +
            $"peak_cut={(cutAbove > 0 ? cutWeighted / cutAbove : 0.0):0.0000}").Append('\n');

        long total = 0;
        double weighted = 0.0;
        int counted = Math.Min(bins.Length, Engine_PostFxModel.Bins);
        for (int i = 0; i < counted; i++)
        {
            total += bins[i];
            weighted += (double)bins[i] * i;
        }

        uint overRange = bins.Length > Engine_PostFxModel.Bins ? bins[Engine_PostFxModel.Bins] : 0u;

        if (total == 0)
        {
            return text.Append("[postfx]   histogram is EMPTY; the state above is stale").ToString();
        }

        float mean = (float)(weighted / total) * BucketWidth;
        float p1 = Percentile(bins, total, 0.01f) * BucketWidth;
        float p50 = Percentile(bins, total, 0.50f) * BucketWidth;
        float p99 = Percentile(bins, total, 0.99f) * BucketWidth;

        text.Append(
            $"[postfx]   exposed scene pixels={total:N0} mean={mean:0.0000} " +
            $"p1={p1:0.0000} p50={p50:0.0000} p99={p99:0.0000}   " +
            $"at 4.0 (bin 512): {Pct(overRange, total + overRange):0.00}%").Append('\n');

        foreach (float luminance in new[] { p50, p99, 0.05f, 0.25f, 1.0f, 3.0f })
        {
            float gain = Gain(state, luminance);
            text.Append(
                $"[postfx]   lut L={luminance:0.0000} gain={gain:0.0000} " +
                $"mapped={luminance * gain:0.0000}").Append('\n');
        }

        Bars(text, bins, total);
        return text.ToString();
    }

    public static int Percentile(ReadOnlySpan<uint> bins, long total, float fraction)
    {
        double threshold = fraction * total;
        long cumulative = 0;
        int index = 0;
        for (int i = 0; i < bins.Length && i < Engine_PostFxModel.Bins; i++)
        {
            if (cumulative < threshold)
            {
                index = i;
            }

            cumulative += bins[i];
        }

        return index;
    }

    public static float Gain(ReadOnlySpan<float> state, float luminance)
    {
        int index = (int)(luminance * (1.0f / Engine_PostFxModel.Range) * Engine_PostFxModel.CurveSize);
        index = Math.Clamp(index, 0, Engine_PostFxModel.CurveSize - 1);
        return state[Engine_PostFxState.Lut + index];
    }

    public static float Mapped(ReadOnlySpan<float> state, float luminance) =>
        luminance * Gain(state, luminance);

    private static void Bars(StringBuilder text, ReadOnlySpan<uint> bins, long total)
    {
        int perBand = Engine_PostFxModel.Bins / Bands;
        float widest = 0.0f;
        var share = new float[Bands];
        for (int band = 0; band < Bands; band++)
        {
            long count = 0;
            for (int i = band * perBand; i < (band + 1) * perBand && i < bins.Length; i++)
            {
                count += bins[i];
            }

            share[band] = (float)((double)count / total);
            widest = MathF.Max(widest, share[band]);
        }

        for (int band = 0; band < Bands; band++)
        {
            int length = widest > 0.0f ? (int)MathF.Round(share[band] / widest * 40.0f) : 0;
            float low = band * perBand * BucketWidth;
            text.Append($"[postfx]   {low,5:0.00}+ {new string('#', length),-40} " +
                $"{share[band] * 100.0f:0.00}%").Append('\n');
        }
    }

    private static float Pct(long part, long total) => total > 0 ? part * 100.0f / total : 0.0f;
}
