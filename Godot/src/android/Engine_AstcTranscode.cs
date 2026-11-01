using System;
using System.Buffers.Binary;

namespace Refii.Textures;

/// <summary>Rewrites BC1 and BC3 blocks as ASTC 4x4 blocks, one for one, on the CPU.</summary>
/// <remarks>
/// FOR GPUS WITHOUT BC. Adreno 6xx (the Galaxy S10e's 640) samples no BC format, so Godot's
/// fallback, and <see cref="AndroidTextureUpload"/> before this, expanded every BC1
/// texture to RGBA8 (8x its size) and every BC3 to RGBA8 (4x). ASTC 4x4 is 16 bytes a block
/// like BC3, so BC1 doubles and BC3 keeps its size. No search: the BC endpoints become the
/// ASTC endpoints and each BC index its weight.
///
/// Measured against Godot's own BC decode on the game's cached textures (the pixels the
/// fallback uploads today): BC1 within 1-2 of 255 per channel (PSNR &gt; 49 dB); BC3 colour
/// within 4 (&gt; 42 dB). BC3 alpha is the lossy part: a block whose alpha varies needs ASTC's
/// dual plane, which at 4x4 leaves 2-bit weights against BC3's 3-bit indices (alpha PSNR
/// 33-42 dB, a hard edge exact). Opaque or uniform-alpha blocks stay single-plane.
///
/// The bit layout (block modes, trit packing, the range-48 endpoint table) was checked
/// against astcenc's decoder in the Godot editor; the unit tests decode with
/// <see cref="DecodeForTests"/>, which covers only the modes written here.
/// </remarks>
public static class Engine_AstcTranscode
{
    // Block modes for a 4x4 weight grid: single plane with 2-bit weights, the same dual-plane,
    // and single plane with trit (3-level) weights.
    private const int ModeQuant4 = 0x042, ModeQuant4Dual = 0x442, ModeQuant3 = 0x051;
    private const int CemRgb = 8, CemRgba = 12;
    /// <summary>Levels searched either side of the least-squares alpha endpoints: two steps cost
    /// 1.7x the time of one for 0.1 dB on the game's textures.</summary>
    private const int SearchSteps = 1;

    /// <summary>ASTC's unquantized 2-bit weights, out of 64.</summary>
    private static readonly int[] Weight4 = { 0, 21, 43, 64 };

    /// <summary>Decoded value of each range-48 endpoint (one trit and four bits), by ISE value.</summary>
    private static readonly byte[] Unq48 =
    {
        0, 255, 16, 239, 32, 223, 48, 207, 65, 190, 81, 174, 97, 158, 113, 142,
        5, 250, 21, 234, 38, 217, 54, 201, 70, 185, 86, 169, 103, 152, 119, 136,
        11, 244, 27, 228, 43, 212, 59, 196, 76, 179, 92, 163, 108, 147, 124, 131,
    };

    private static readonly byte[] Nearest48 = new byte[256];
    private static readonly byte[] Sorted48 = new byte[48];
    private static readonly byte[] Rank48 = new byte[48];
    private static readonly byte[] TritPack = new byte[243];

    static Engine_AstcTranscode()
    {
        for (int v = 0; v < 256; v++)
        {
            int best = 0;
            for (int q = 1; q < 48; q++)
                if (Math.Abs(Unq48[q] - v) < Math.Abs(Unq48[best] - v)) best = q;
            Nearest48[v] = (byte)best;
        }
        for (int q = 0; q < 48; q++) Sorted48[q] = (byte)q;
        Array.Sort(Sorted48, (a, b) => Unq48[a].CompareTo(Unq48[b]));
        for (int r = 0; r < 48; r++) Rank48[Sorted48[r]] = (byte)r;
        // The packed form of each five-trit group, from the specification's decode; the
        // first of the duplicate encodings wins.
        var seen = new bool[243];
        Span<int> trits = stackalloc int[5];
        for (int t = 0; t < 256; t++)
        {
            DecodeTrits(t, trits);
            int key = trits[0] + 3 * trits[1] + 9 * trits[2] + 27 * trits[3] + 81 * trits[4];
            if (!seen[key]) { seen[key] = true; TritPack[key] = (byte)t; }
        }
    }

    /// <summary>Transcodes a whole BC1 or BC3 payload, every mip level, block for block.</summary>
    /// <param name="bc">The blocks, 8 bytes each for BC1 and 16 for BC3.</param>
    /// <param name="bc3">BC3 when true, BC1 otherwise.</param>
    /// <param name="astc">16 bytes for every source block.</param>
    /// <returns>False when the lengths do not match a whole number of blocks.</returns>
    public static bool Transcode(ReadOnlySpan<byte> bc, bool bc3, Span<byte> astc)
    {
        int size = bc3 ? 16 : 8;
        if (bc.Length % size != 0 || astc.Length != bc.Length / size * 16) return false;
        for (int i = 0, o = 0; i < bc.Length; i += size, o += 16)
        {
            if (bc3) Bc3Block(bc.Slice(i, 16), astc.Slice(o, 16));
            else Bc1Block(bc.Slice(i, 8), astc.Slice(o, 16));
        }
        return true;
    }

    /// <summary>One BC1 block (read as BC1 RGB, the format desktop samples it as).</summary>
    public static void Bc1Block(ReadOnlySpan<byte> bc1, Span<byte> astc)
    {
        int c0 = BinaryPrimitives.ReadUInt16LittleEndian(bc1), c1 = BinaryPrimitives.ReadUInt16LittleEndian(bc1[2..]);
        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(bc1[4..]);
        Span<int> e0 = stackalloc int[3], e1 = stackalloc int[3];
        Expand565(c0, e0);
        Expand565(c1, e1);
        Span<int> weights = stackalloc int[16];
        bool fourColour = c0 > c1;
        int max = fourColour ? 3 : 2;
        // Four-colour BC1 is c0, c1, 1/3, 2/3; three-colour is c0, c1, 1/2 and opaque black,
        // which is off the line: the nearest of the three points stands in for it.
        int black = 0;
        if (!fourColour)
        {
            int d0 = Square(e0), d2 = Square(e1), d1 = 0;
            for (int c = 0; c < 3; c++) { int m = (e0[c] + e1[c]) / 2; d1 += m * m; }
            black = d0 <= d1 && d0 <= d2 ? 0 : d1 <= d2 ? 1 : 2;
        }
        for (int t = 0; t < 16; t++)
        {
            int index = (int)(indices >> (2 * t)) & 3;
            weights[t] = fourColour
                ? index switch { 0 => 0, 1 => 3, 2 => 1, _ => 2 }
                : index switch { 0 => 0, 1 => 2, 2 => 1, _ => black };
        }
        OrderEndpoints(e0, e1, weights, max);

        var bits = new Bits();
        bits.Put(0, 11, fourColour ? ModeQuant4 : ModeQuant3);
        bits.Put(13, 4, CemRgb);
        int at = 17;
        for (int c = 0; c < 3; c++) { bits.Put(at, 8, e0[c]); bits.Put(at + 8, 8, e1[c]); at += 16; }
        if (fourColour) PutWeights2(ref bits, weights);
        else PutTritWeights(ref bits, weights);
        bits.Write(astc);
    }

    /// <summary>One BC3 block: uniform alpha stays single-plane, varying alpha goes dual-plane.</summary>
    public static void Bc3Block(ReadOnlySpan<byte> bc3, Span<byte> astc)
    {
        Span<int> alpha = stackalloc int[16];
        Bc3Alpha(bc3, alpha);
        ReadOnlySpan<byte> colour = bc3[8..];
        int c0 = BinaryPrimitives.ReadUInt16LittleEndian(colour), c1 = BinaryPrimitives.ReadUInt16LittleEndian(colour[2..]);
        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(colour[4..]);
        Span<int> e0 = stackalloc int[3], e1 = stackalloc int[3];
        Expand565(c0, e0);
        Expand565(c1, e1);
        Span<int> weights = stackalloc int[16];
        Span<int> ids = stackalloc int[16];
        bool uniform = true;
        for (int t = 0; t < 16; t++)
        {
            // BC3's colour half is always four-colour, whatever the endpoint order.
            ids[t] = (int)(indices >> (2 * t)) & 3;
            weights[t] = ids[t] switch { 0 => 0, 1 => 3, 2 => 1, _ => 2 };
            uniform &= alpha[t] == alpha[0];
        }

        var bits = new Bits();
        bits.Put(13, 4, CemRgba);
        if (uniform)
        {
            OrderEndpoints(e0, e1, weights, 3);
            bits.Put(0, 11, ModeQuant4);
            int at = 17;
            for (int c = 0; c < 3; c++) { bits.Put(at, 8, e0[c]); bits.Put(at + 8, 8, e1[c]); at += 16; }
            bits.Put(at, 8, alpha[0]);
            bits.Put(at + 8, 8, alpha[0]);
            PutWeights2(ref bits, weights);
            bits.Write(astc);
            return;
        }

        // Dual plane: 32 two-bit weights leave 45 bits for eight endpoints, which is range 48.
        // The texels to match are BC3's decoded colours; the weights are refitted to the
        // quantized endpoints.
        Span<int> texels = stackalloc int[48];
        for (int t = 0; t < 16; t++)
            for (int c = 0; c < 3; c++)
                texels[t * 3 + c] = ids[t] switch
                {
                    0 => e0[c], 1 => e1[c],
                    2 => (2 * e0[c] + e1[c] + 1) / 3,
                    _ => (e0[c] + 2 * e1[c] + 1) / 3,
                };
        Span<int> q0 = stackalloc int[3], q1 = stackalloc int[3], u0 = stackalloc int[3], u1 = stackalloc int[3];
        for (int c = 0; c < 3; c++)
        {
            q0[c] = Nearest48[e0[c]]; q1[c] = Nearest48[e1[c]];
            u0[c] = Unq48[q0[c]]; u1[c] = Unq48[q1[c]];
        }
        Span<int> palette = stackalloc int[12];
        for (int w = 0; w < 4; w++)
            for (int c = 0; c < 3; c++) palette[w * 3 + c] = Lerp(u0[c], u1[c], Weight4[w]);
        for (int t = 0; t < 16; t++)
        {
            int best = 0, bestError = int.MaxValue;
            for (int w = 0; w < 4; w++)
            {
                int error = 0;
                for (int c = 0; c < 3; c++) { int d = palette[w * 3 + c] - texels[t * 3 + c]; error += d * d; }
                if (error < bestError) { bestError = error; best = w; }
            }
            weights[t] = best;
        }
        Span<int> alphaWeights = stackalloc int[16];
        (int qa0, int qa1) = FitAlpha(alpha, alphaWeights);
        int sum0 = u0[0] + u0[1] + u0[2], sum1 = u1[0] + u1[1] + u1[2];
        if (sum1 < sum0)
        {
            // Swapped as whole endpoints, alpha included, so the decoder's order rule holds.
            for (int c = 0; c < 3; c++) (q0[c], q1[c]) = (q1[c], q0[c]);
            (qa0, qa1) = (qa1, qa0);
            for (int t = 0; t < 16; t++) { weights[t] = 3 - weights[t]; alphaWeights[t] = 3 - alphaWeights[t]; }
        }

        bits.Put(0, 11, ModeQuant4Dual);
        Span<int> values = stackalloc int[8] { q0[0], q1[0], q0[1], q1[1], q0[2], q1[2], qa0, qa1 };
        PutTrits(ref bits, 17, values, 4);
        // Plane-interleaved: colour weight, alpha weight, for each texel; the component
        // selector (alpha = 3) sits just below them.
        for (int t = 0; t < 16; t++)
        {
            bits.PutReversed(4 * t, 2, weights[t]);
            bits.PutReversed(4 * t + 2, 2, alphaWeights[t]);
        }
        bits.Put(128 - 64 - 2, 2, 3);
        bits.Write(astc);
    }

    /// <summary>Range-48 alpha endpoints and 2-bit weights: least-squares refit of a min/max
    /// start, then the best of the levels within <see cref="SearchSteps"/> of it.</summary>
    private static (int, int) FitAlpha(ReadOnlySpan<int> alpha, Span<int> weights)
    {
        int lo = 255, hi = 0;
        foreach (int a in alpha) { lo = Math.Min(lo, a); hi = Math.Max(hi, a); }
        int bestQ0 = Nearest48[lo], bestQ1 = Nearest48[hi];
        long bestError = AssignAlpha(alpha, Unq48[bestQ0], Unq48[bestQ1], weights);
        // Cut-out alpha (two values, both on the grid) is already exact.
        if (bestError == 0) return (bestQ0, bestQ1);

        double s00 = 0, s11 = 0, s01 = 0, b0 = 0, b1 = 0;
        for (int t = 0; t < 16; t++)
        {
            double f = Weight4[weights[t]] / 64.0;
            s00 += (1 - f) * (1 - f); s11 += f * f; s01 += (1 - f) * f;
            b0 += (1 - f) * alpha[t]; b1 += f * alpha[t];
        }
        double det = s00 * s11 - s01 * s01;
        double x0 = lo, x1 = hi;
        if (Math.Abs(det) > 1e-9) { x0 = (b0 * s11 - b1 * s01) / det; x1 = (b1 * s00 - b0 * s01) / det; }
        int r0 = Rank48[Nearest48[Math.Clamp((int)Math.Round(x0), 0, 255)]];
        int r1 = Rank48[Nearest48[Math.Clamp((int)Math.Round(x1), 0, 255)]];

        Span<int> trial = stackalloc int[16];
        for (int i = Math.Max(0, r0 - SearchSteps); i <= Math.Min(47, r0 + SearchSteps); i++)
            for (int j = Math.Max(0, r1 - SearchSteps); j <= Math.Min(47, r1 + SearchSteps); j++)
            {
                long error = AssignAlpha(alpha, Unq48[Sorted48[i]], Unq48[Sorted48[j]], trial);
                if (error < bestError) { bestError = error; bestQ0 = Sorted48[i]; bestQ1 = Sorted48[j]; }
            }
        AssignAlpha(alpha, Unq48[bestQ0], Unq48[bestQ1], weights);
        return (bestQ0, bestQ1);
    }

    private static long AssignAlpha(ReadOnlySpan<int> alpha, int a0, int a1, Span<int> weights)
    {
        // The four levels are monotonic, so the nearest is found by the midpoints between them.
        int l0 = Lerp(a0, a1, 0), l1 = Lerp(a0, a1, 21), l2 = Lerp(a0, a1, 43), l3 = Lerp(a0, a1, 64);
        bool rising = a1 >= a0;
        int m01 = l0 + l1, m12 = l1 + l2, m23 = l2 + l3;
        long total = 0;
        for (int t = 0; t < 16; t++)
        {
            int twice = 2 * alpha[t];
            int w = rising
                ? (twice > m01 ? 1 : 0) + (twice > m12 ? 1 : 0) + (twice > m23 ? 1 : 0)
                : (twice < m01 ? 1 : 0) + (twice < m12 ? 1 : 0) + (twice < m23 ? 1 : 0);
            int d = (w switch { 0 => l0, 1 => l1, 2 => l2, _ => l3 }) - alpha[t];
            weights[t] = w;
            total += d * d;
        }
        return total;
    }

    /// <summary>BC3's alpha half, decoded with the D3D rounding.</summary>
    private static void Bc3Alpha(ReadOnlySpan<byte> block, Span<int> alpha)
    {
        int a0 = block[0], a1 = block[1];
        Span<int> palette = stackalloc int[8];
        palette[0] = a0; palette[1] = a1;
        if (a0 > a1)
            for (int k = 1; k < 7; k++) palette[k + 1] = ((7 - k) * a0 + k * a1 + 3) / 7;
        else
        {
            for (int k = 1; k < 5; k++) palette[k + 1] = ((5 - k) * a0 + k * a1 + 2) / 5;
            palette[6] = 0; palette[7] = 255;
        }
        ulong indices = 0;
        for (int i = 0; i < 6; i++) indices |= (ulong)block[2 + i] << (8 * i);
        for (int t = 0; t < 16; t++) alpha[t] = palette[(int)(indices >> (3 * t)) & 7];
    }

    /// <summary>ASTC's decoder flips to blue contraction when e1's sum is below e0's: keep e1 the
    /// larger by swapping endpoints and mirroring weights.</summary>
    private static void OrderEndpoints(Span<int> e0, Span<int> e1, Span<int> weights, int max)
    {
        if (e1[0] + e1[1] + e1[2] >= e0[0] + e0[1] + e0[2]) return;
        for (int c = 0; c < 3; c++) (e0[c], e1[c]) = (e1[c], e0[c]);
        for (int t = 0; t < 16; t++) weights[t] = max - weights[t];
    }

    private static void PutWeights2(ref Bits bits, ReadOnlySpan<int> weights)
    {
        for (int t = 0; t < 16; t++) bits.PutReversed(2 * t, 2, weights[t]);
    }

    /// <summary>Sixteen trit weights (26 bits) from the top of the block, bit-reversed.</summary>
    private static void PutTritWeights(ref Bits bits, ReadOnlySpan<int> weights)
    {
        var stream = new Bits();
        int length = PutTrits(ref stream, 0, weights, 0);
        for (int k = 0; k < length; k++) bits.PutReversed(k, 1, stream.Get(k));
    }

    /// <summary>Integer-sequence-encodes values of one trit and <paramref name="low"/> bits each,
    /// five to a group, starting at bit <paramref name="at"/>.</summary>
    /// <returns>The encoded length in bits.</returns>
    private static int PutTrits(ref Bits bits, int at, ReadOnlySpan<int> values, int low)
    {
        int length = values.Length * low + (values.Length * 8 + 4) / 5;
        int start = at;
        Span<int> m = stackalloc int[5], t = stackalloc int[5];
        Span<(int Bits, int Value)> parts = stackalloc (int, int)[10];
        for (int g = 0; g < values.Length; g += 5)
        {
            for (int i = 0; i < 5; i++)
            {
                int v = g + i < values.Length ? values[g + i] : 0;
                m[i] = v & ((1 << low) - 1);
                t[i] = v >> low;
            }
            int packed = TritPack[t[0] + 3 * t[1] + 9 * t[2] + 27 * t[3] + 81 * t[4]];
            parts[0] = (low, m[0]); parts[1] = (2, packed & 3); parts[2] = (low, m[1]);
            parts[3] = (2, (packed >> 2) & 3); parts[4] = (low, m[2]); parts[5] = (1, (packed >> 4) & 1);
            parts[6] = (low, m[3]); parts[7] = (2, (packed >> 5) & 3); parts[8] = (low, m[4]);
            parts[9] = (1, packed >> 7);
            foreach ((int n, int value) in parts)
            {
                // The last group is cut to the sequence's length.
                int keep = Math.Min(n, start + length - at);
                if (keep > 0) bits.Put(at, keep, value);
                at += n;
            }
        }
        return length;
    }

    /// <summary>The specification's trit-block decode, for building <see cref="TritPack"/>.</summary>
    private static void DecodeTrits(int packed, Span<int> trits)
    {
        static int Bit(int x, int i) => (x >> i) & 1;
        int c;
        if (((packed >> 2) & 7) == 7)
        {
            c = (((packed >> 5) & 7) << 2) | (packed & 3);
            trits[4] = 2; trits[3] = 2;
        }
        else
        {
            c = packed & 31;
            if (((packed >> 5) & 3) == 3) { trits[4] = 2; trits[3] = Bit(packed, 7); }
            else { trits[4] = Bit(packed, 7); trits[3] = (packed >> 5) & 3; }
        }
        if ((c & 3) == 3) { trits[2] = 2; trits[1] = Bit(c, 4); trits[0] = (Bit(c, 3) << 1) | (Bit(c, 2) & ~Bit(c, 3) & 1); }
        else if (((c >> 2) & 3) == 3) { trits[2] = 2; trits[1] = 2; trits[0] = c & 3; }
        else { trits[2] = Bit(c, 4); trits[1] = (c >> 2) & 3; trits[0] = (Bit(c, 1) << 1) | (Bit(c, 0) & ~Bit(c, 1) & 1); }
    }

    private static void Expand565(int c, Span<int> rgb)
    {
        int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
        rgb[0] = (r << 3) | (r >> 2);
        rgb[1] = (g << 2) | (g >> 4);
        rgb[2] = (b << 3) | (b >> 2);
    }

    private static int Square(ReadOnlySpan<int> rgb) => rgb[0] * rgb[0] + rgb[1] * rgb[1] + rgb[2] * rgb[2];

    /// <summary>ASTC's LDR interpolation of two 8-bit endpoints, read back as UNORM8.</summary>
    private static int Lerp(int a, int b, int weight) => ((a * 257 * (64 - weight) + b * 257 * weight + 32) >> 6) >> 8;

    /// <summary>Decodes a block written by this class to 16 RGBA texels, for the tests.</summary>
    /// <remarks>Only the three block modes and two endpoint modes above; anything else throws.</remarks>
    public static void DecodeForTests(ReadOnlySpan<byte> astc, Span<byte> rgba)
    {
        var bits = new Bits();
        bits.Read(astc);
        int mode = bits.Get(0, 11), cem = bits.Get(13, 4);
        bool dual = mode == ModeQuant4Dual;
        if (mode is not (ModeQuant4 or ModeQuant4Dual or ModeQuant3) || cem is not (CemRgb or CemRgba) || bits.Get(11, 2) != 0)
            throw new NotSupportedException($"block mode {mode:x3}, cem {cem}");
        int count = cem == CemRgb ? 6 : 8;
        Span<int> e = stackalloc int[8];
        if (dual)
        {
            Span<int> values = stackalloc int[8];
            ReadTrits(ref bits, 17, values, 4);
            for (int i = 0; i < 8; i++) e[i] = Unq48[values[i]];
        }
        else
            for (int i = 0; i < count; i++) e[i] = bits.Get(17 + 8 * i, 8);
        if (cem == CemRgb) { e[6] = 255; e[7] = 255; }
        if (e[1] + e[3] + e[5] < e[0] + e[2] + e[4]) throw new NotSupportedException("blue contraction");

        Span<int> w = stackalloc int[32];
        if (mode == ModeQuant3)
        {
            var stream = new Bits();
            for (int k = 0; k < 26; k++) stream.Put(k, 1, bits.GetReversed(k, 1));
            ReadTrits(ref stream, 0, w[..16], 0);
            for (int t = 0; t < 16; t++) w[t] *= 32;
        }
        else
            for (int i = 0; i < (dual ? 32 : 16); i++) w[i] = Weight4[bits.GetReversed(2 * i, 2)];
        if (dual && bits.Get(128 - 64 - 2, 2) != 3) throw new NotSupportedException("component selector");
        for (int t = 0; t < 16; t++)
        {
            int wc = dual ? w[2 * t] : w[t], wa = dual ? w[2 * t + 1] : w[t];
            for (int c = 0; c < 3; c++) rgba[t * 4 + c] = (byte)Lerp(e[2 * c], e[2 * c + 1], wc);
            rgba[t * 4 + 3] = (byte)Lerp(e[6], e[7], wa);
        }
    }

    private static void ReadTrits(ref Bits bits, int at, Span<int> values, int low)
    {
        int length = values.Length * low + (values.Length * 8 + 4) / 5, end = at + length;
        Span<int> trits = stackalloc int[5], m = stackalloc int[5];
        // Group layout in bits: m0, T1:0, m1, T3:2, m2, T4, m3, T6:5, m4, T7 (0 = a value's low bits).
        ReadOnlySpan<int> parts = stackalloc int[] { 0, 2, 0, 2, 0, 1, 0, 2, 0, 1 };
        for (int g = 0; g < values.Length; g += 5)
        {
            int packed = 0, shift = 0, mi = 0;
            foreach (int part in parts)
            {
                bool trit = part != 0;
                int n = trit ? part : low;
                int keep = Math.Clamp(end - at, 0, n);
                int value = keep > 0 ? bits.Get(at, keep) : 0;
                if (trit) { packed |= value << shift; shift += n; }
                else m[mi++] = value;
                at += n;
            }
            DecodeTrits(packed, trits);
            for (int i = 0; i < 5 && g + i < values.Length; i++) values[g + i] = (trits[i] << low) | m[i];
        }
    }

    /// <summary>A 128-bit ASTC block, bit 0 the lowest bit of byte 0.</summary>
    private struct Bits
    {
        private UInt128 _bits;

        public void Put(int at, int count, int value) =>
            _bits |= ((UInt128)(uint)value & (((UInt128)1 << count) - 1)) << at;

        /// <summary>Writes counting down from bit 127, as ASTC stores its weights.</summary>
        public void PutReversed(int at, int count, int value)
        {
            for (int i = 0; i < count; i++)
                if (((value >> i) & 1) != 0) Set(127 - (at + i));
        }

        public readonly int Get(int at, int count = 1) =>
            (int)(uint)((_bits >> at) & (((UInt128)1 << count) - 1));

        public readonly int GetReversed(int at, int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++) value |= Bit(127 - (at + i)) << i;
            return value;
        }

        public readonly void Write(Span<byte> block) => BinaryPrimitives.WriteUInt128LittleEndian(block, _bits);

        public void Read(ReadOnlySpan<byte> block) => _bits = BinaryPrimitives.ReadUInt128LittleEndian(block);

        private void Set(int i) => _bits |= (UInt128)1 << i;

        private readonly int Bit(int i) => (int)(uint)((_bits >> i) & 1);
    }
}
