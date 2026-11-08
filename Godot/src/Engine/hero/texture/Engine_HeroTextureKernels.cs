using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Refii.Hero;

/// <summary>The per-pixel loops of a character composite, four pixels at a time.</summary>
/// <remarks>
/// The same single-precision sums in the same order as the one-pixel form each loop ends in, and
/// IEEE division, addition and multiplication are exact to the same bit in a vector as out of
/// one, so the bytes do not change with the width. Nothing here is fused or reordered. The one
/// place a vector would differ is NaN through a clamp, so a weight that could make one takes the
/// one-pixel form for the whole layer.
/// </remarks>
[SkipLocalsInit]
internal static class Engine_HeroTextureKernels
{
    /// <summary>Past this a weight could overflow to infinity and NaN; NaN and infinity fail it too.</summary>
    private const float VectorWeightLimit = 1e6f;

    /// <summary>
    /// One layer over the target: colour from one image, coverage from another's alpha. Mode 0 is
    /// opaque, 2 adds, 3 takes the weight alone, and anything else mixes by coverage times weight.
    /// </summary>
    public static void Blend(byte[] output, byte[] colour, byte[] coverage, int pixels, int mode, float weight)
    {
        int end = pixels * 4;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, output.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, colour.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, coverage.Length);

        int at = 0;
        if (Math.Abs(weight) < VectorWeightLimit)
        {
            ref byte toRef = ref MemoryMarshal.GetArrayDataReference(output);
            ref byte fromRef = ref MemoryMarshal.GetArrayDataReference(colour);
            ref byte coverRef = ref MemoryMarshal.GetArrayDataReference(coverage);
            Vector128<uint> mask = Vector128.Create(0xFFu);
            Vector128<float> half = Vector128.Create(0.5f);
            Vector128<float> top = Vector128.Create(255.0f);
            Vector128<float> one = Vector128.Create(1.0f);
            Vector128<float> stated = Vector128.Create(weight);
            Vector128<float> flat = mode == 0 ? one : stated;
            bool flatAlpha = mode is 0 or 3;
            bool add = mode == 2;
            for (; at + 16 <= end; at += 16)
            {
                Vector128<uint> to = Vector128.LoadUnsafe(ref toRef, (nuint)at).AsUInt32();
                Vector128<uint> from = Vector128.LoadUnsafe(ref fromRef, (nuint)at).AsUInt32();
                Vector128<float> alpha = flatAlpha
                    ? flat
                    : (Vector128.ConvertToSingle((Vector128.LoadUnsafe(ref coverRef, (nuint)at).AsUInt32() >>> 24).AsInt32()) / top) * stated;
                Vector128<float> keep = add ? one : one - alpha;

                Vector128<uint> mixed =
                    Mix(to & mask, from & mask, alpha, keep, half, top) |
                    (Mix((to >>> 8) & mask, (from >>> 8) & mask, alpha, keep, half, top) << 8) |
                    (Mix((to >>> 16) & mask, (from >>> 16) & mask, alpha, keep, half, top) << 16) |
                    (to & Vector128.Create(0xFF000000u));

                Vector128<uint> skipped = Vector128.LessThanOrEqual(alpha, Vector128<float>.Zero).AsUInt32();
                Vector128.ConditionalSelect(skipped, to, mixed).AsByte().StoreUnsafe(ref toRef, (nuint)at);
            }
        }

        for (; at < end; at += 4)
        {
            float alpha = mode switch
            {
                0 => 1.0f,
                3 => weight,
                _ => (coverage[at + 3] / 255.0f) * weight,
            };

            if (alpha <= 0.0f)
            {
                continue;
            }

            for (int k = 0; k < 3; k++)
            {
                float value = mode == 2
                    ? output[at + k] + (colour[at + k] * alpha)
                    : (output[at + k] * (1.0f - alpha)) + (colour[at + k] * alpha);

                output[at + k] = (byte)Math.Clamp(value + 0.5f, 0.0f, 255.0f);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Mix(
        Vector128<uint> to, Vector128<uint> from, Vector128<float> alpha, Vector128<float> keep,
        Vector128<float> half, Vector128<float> top)
    {
        Vector128<float> value =
            (Vector128.ConvertToSingle(to.AsInt32()) * keep) + (Vector128.ConvertToSingle(from.AsInt32()) * alpha) + half;
        return Vector128.ConvertToInt32(Vector128.Min(Vector128.Max(value, Vector128<float>.Zero), top)).AsUInt32();
    }

    /// <summary>
    /// The dye over every pixel: <paramref name="to"/> may be <paramref name="from"/>. The sums are
    /// those of the shipped <c>texture_morph_colourise</c> pixel shader, as
    /// <c>Engine_HeroTextureMorph.Colourise</c> derives their constants.
    /// </summary>
    public static void Colourise(
        byte[] from, byte[] to, int pixels, float[] colour, float colourAlpha, float[] toIntensity, float pivot, float[] curve)
    {
        int end = pixels * 4;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, from.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, to.Length);

        bool lowered = pivot > 1e-5f;
        bool raised = pivot < 1.0f - 1e-5f;

        ref byte fromRef = ref MemoryMarshal.GetArrayDataReference(from);
        ref byte toRef = ref MemoryMarshal.GetArrayDataReference(to);
        Vector128<uint> mask = Vector128.Create(0xFFu);
        Vector128<float> unit = Vector128.Create(255.0f);
        Vector128<float> one = Vector128.Create(1.0f);
        Vector128<float> half = Vector128.Create(0.5f);
        Vector128<float> toR = Vector128.Create(toIntensity[0]), toG = Vector128.Create(toIntensity[1]), toB = Vector128.Create(toIntensity[2]);
        Vector128<float> colourR = Vector128.Create(colour[0]), colourG = Vector128.Create(colour[1]), colourB = Vector128.Create(colour[2]);
        Vector128<float> dyed = Vector128.Create(colourAlpha);
        Vector128<float> pivots = Vector128.Create(pivot);
        Vector128<float> aboveMax = Vector128.Create(1.0f - pivot);
        Vector128<float> four = Vector128.Create(4.0f);
        Vector128<int> three = Vector128.Create(3);
        Vector128<float> base0 = Vector128.Create(curve[0]), base1 = Vector128.Create(curve[1]), base2 = Vector128.Create(curve[2]), base3 = Vector128.Create(curve[3]);
        Vector128<float> rise0 = Vector128.Create(curve[1] - curve[0]), rise1 = Vector128.Create(curve[2] - curve[1]);
        Vector128<float> rise2 = Vector128.Create(curve[3] - curve[2]), rise3 = Vector128.Create(curve[4] - curve[3]);

        int at = 0;
        for (; at + 16 <= end; at += 16)
        {
            Vector128<uint> texels = Vector128.LoadUnsafe(ref fromRef, (nuint)at).AsUInt32();
            Vector128<float> tr = Vector128.ConvertToSingle((texels & mask).AsInt32()) / unit;
            Vector128<float> tg = Vector128.ConvertToSingle(((texels >>> 8) & mask).AsInt32()) / unit;
            Vector128<float> tb = Vector128.ConvertToSingle(((texels >>> 16) & mask).AsInt32()) / unit;

            Vector128<float> t = four * ((tr * toR) + (tg * toG) + (tb * toB));
            Vector128<int> i = Vector128.Min(Vector128.Max(Vector128.ConvertToInt32(t), Vector128<int>.Zero), three);
            Vector128<float> f = Vector128.Min(Vector128.Max(t - Vector128.ConvertToSingle(i), Vector128<float>.Zero), one);

            Vector128<float> is1 = Vector128.Equals(i, Vector128<int>.One).AsSingle();
            Vector128<float> is2 = Vector128.Equals(i, Vector128.Create(2)).AsSingle();
            Vector128<float> is3 = Vector128.Equals(i, three).AsSingle();
            Vector128<float> start = Vector128.ConditionalSelect(is3, base3, Vector128.ConditionalSelect(is2, base2, Vector128.ConditionalSelect(is1, base1, base0)));
            Vector128<float> rise = Vector128.ConditionalSelect(is3, rise3, Vector128.ConditionalSelect(is2, rise2, Vector128.ConditionalSelect(is1, rise1, rise0)));
            Vector128<float> shaped = start + (rise * f);

            Vector128<float> lo = lowered ? Vector128.Min(shaped / pivots, one) : one;
            Vector128<float> hi = raised
                ? Vector128.Max(shaped - pivots, Vector128<float>.Zero) / aboveMax
                : Vector128<float>.Zero;

            Vector128<uint> dyedTexels =
                DyeChannel(lo, hi, colourR, tr, dyed, one, half, unit) |
                (DyeChannel(lo, hi, colourG, tg, dyed, one, half, unit) << 8) |
                (DyeChannel(lo, hi, colourB, tb, dyed, one, half, unit) << 16) |
                (texels & Vector128.Create(0xFF000000u));
            dyedTexels.AsByte().StoreUnsafe(ref toRef, (nuint)at);
        }

        for (; at < end; at += 4)
        {
            float tr = from[at] / 255.0f;
            float tg = from[at + 1] / 255.0f;
            float tb = from[at + 2] / 255.0f;

            float t = 4.0f * ((tr * toIntensity[0]) + (tg * toIntensity[1]) + (tb * toIntensity[2]));
            int i = Math.Clamp((int)t, 0, 3);
            float f = Math.Clamp(t - i, 0.0f, 1.0f);
            float shaped = curve[i] + ((curve[i + 1] - curve[i]) * f);

            float lo = lowered ? MathF.Min(shaped / pivot, 1.0f) : 1.0f;
            float hi = raised ? MathF.Max(shaped - pivot, 0.0f) / (1.0f - pivot) : 0.0f;

            byte alpha = from[at + 3];
            to[at] = DyeChannel(lo, hi, colour[0], tr, colourAlpha);
            to[at + 1] = DyeChannel(lo, hi, colour[1], tg, colourAlpha);
            to[at + 2] = DyeChannel(lo, hi, colour[2], tb, colourAlpha);
            to[at + 3] = alpha;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> DyeChannel(
        Vector128<float> lo, Vector128<float> hi, Vector128<float> colour, Vector128<float> texel, Vector128<float> dyed,
        Vector128<float> one, Vector128<float> half, Vector128<float> top)
    {
        Vector128<float> col = lo * colour;
        col += hi * (one - col);
        Vector128<float> value = texel + ((col - texel) * dyed);
        return Vector128.ConvertToInt32(Vector128.Min(Vector128.Max((value * top) + half, Vector128<float>.Zero), top)).AsUInt32();
    }

    private static byte DyeChannel(float lo, float hi, float colour, float texel, float dyed)
    {
        float col = lo * colour;
        col += hi * (1.0f - col);
        float value = texel + ((col - texel) * dyed);
        return (byte)Math.Clamp((value * 255.0f) + 0.5f, 0.0f, 255.0f);
    }
}
