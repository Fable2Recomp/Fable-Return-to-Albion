using System;

namespace Refii.Textures;

/// <summary>Rewrites BC5 normal maps as EAC RG11 blocks, one for one, on the CPU.</summary>
/// <remarks>
/// FOR GPUS WITHOUT BC. Adreno 6xx samples no BC format, so a BC5 normal map is expanded to RG8,
/// twice its size. EAC RG11 stores the same two channels in the same 16 bytes a block, so the
/// map keeps BC5's size and every level of its chain maps block for block.
///
/// A BC4 channel is decoded to its 16 values and refitted: EAC's modifier tables are a fixed
/// set, so each is tried at the multiplier that spans the block's range and the closest kept.
/// </remarks>
[System.Runtime.CompilerServices.SkipLocalsInit]
public static class Engine_EacTranscode
{
    /// <summary>Bump when the same input gives different blocks; cached forms depend on it.</summary>
    public const int Version = 1;

    private const int Tables = 16;

    /// <summary>The ETC2 specification's 16 modifier tables, eight values each.</summary>
    private static ReadOnlySpan<sbyte> Modifiers => new sbyte[Tables * 8]
    {
        -3, -6, -9, -15, 2, 5, 8, 14,
        -3, -7, -10, -13, 2, 6, 9, 12,
        -2, -5, -8, -13, 1, 4, 7, 12,
        -2, -4, -6, -13, 1, 3, 5, 12,
        -3, -6, -8, -12, 2, 5, 7, 11,
        -3, -7, -9, -11, 2, 6, 8, 10,
        -4, -7, -8, -11, 3, 6, 7, 10,
        -3, -5, -8, -11, 2, 4, 7, 10,
        -2, -6, -8, -10, 1, 5, 7, 9,
        -2, -5, -8, -10, 1, 4, 7, 9,
        -2, -4, -8, -10, 1, 3, 7, 9,
        -2, -5, -7, -10, 1, 4, 6, 9,
        -3, -4, -7, -10, 2, 3, 6, 9,
        -1, -2, -3, -10, 0, 1, 2, 9,
        -4, -6, -8, -9, 3, 5, 7, 8,
        -3, -5, -7, -9, 2, 4, 6, 8,
    };

    /// <summary>Rewrites a whole BC5 payload.</summary>
    /// <param name="bc5">Whole 16-byte BC5 blocks.</param>
    /// <param name="eac">Receives as many EAC RG11 blocks, the same length.</param>
    /// <returns>False if the payload is not whole blocks or the output is short.</returns>
    public static bool Transcode(ReadOnlySpan<byte> bc5, Span<byte> eac)
    {
        if (bc5.Length % 16 != 0 || eac.Length < bc5.Length) return false;
        for (int i = 0; i < bc5.Length; i += 16)
        {
            Channel(bc5.Slice(i, 8), eac.Slice(i, 8));
            Channel(bc5.Slice(i + 8, 8), eac.Slice(i + 8, 8));
        }
        return true;
    }

    /// <summary>Rewrites one BC4 channel as one EAC R11 channel.</summary>
    /// <param name="bc4">The 8-byte BC4 block.</param>
    /// <param name="eac">The 8-byte EAC block.</param>
    public static void Channel(ReadOnlySpan<byte> bc4, Span<byte> eac)
    {
        Span<byte> values = stackalloc byte[16];
        DecodeBc4(bc4, values);
        int min = 255, max = 0;
        foreach (byte v in values)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }

        Span<int> targets = stackalloc int[16];
        for (int i = 0; i < 16; i++) targets[i] = values[i] * 2047 / 255;

        ReadOnlySpan<sbyte> modifiers = Modifiers;
        Span<byte> trial = stackalloc byte[16];
        Span<byte> best = stackalloc byte[16];
        long bestError = long.MaxValue;
        int bestBase = min, bestMultiplier = 1, bestTable = 13;
        int range = max - min;
        for (int table = 0; table < Tables; table++)
        {
            ReadOnlySpan<sbyte> row = modifiers.Slice(table * 8, 8);
            int low = 0, high = 0;
            foreach (sbyte m in row)
            {
                if (m < low) low = m;
                if (m > high) high = m;
            }

            int spread = high - low;
            int multiplier = Math.Clamp((range + spread / 2) / spread, 1, 15);
            int centre = (min + max + 1) / 2 - (low + high) * multiplier / 2;
            int codeword = Math.Clamp(centre, 0, 255);
            long error = 0;
            for (int i = 0; i < 16 && error < bestError; i++)
            {
                int closest = int.MaxValue;
                for (int k = 0; k < 8; k++)
                {
                    int value = Math.Clamp(codeword * 8 + 4 + row[k] * multiplier * 8, 0, 2047);
                    int difference = Math.Abs(value - targets[i]);
                    if (difference < closest)
                    {
                        closest = difference;
                        trial[i] = (byte)k;
                    }
                }
                error += (long)closest * closest;
            }

            if (error < bestError)
            {
                bestError = error;
                bestBase = codeword;
                bestMultiplier = multiplier;
                bestTable = table;
                trial.CopyTo(best);
            }
        }

        // Column-major: the index of (x, y) is the (4x + y)th, most significant first.
        ulong indices = 0;
        for (int x = 0; x < 4; x++)
        {
            for (int y = 0; y < 4; y++) indices = (indices << 3) | best[y * 4 + x];
        }

        eac[0] = (byte)bestBase;
        eac[1] = (byte)((bestMultiplier << 4) | bestTable);
        for (int i = 0; i < 6; i++) eac[2 + i] = (byte)(indices >> (40 - 8 * i));
    }

    /// <summary>A BC4 block's sixteen 8-bit values, row-major.</summary>
    private static void DecodeBc4(ReadOnlySpan<byte> bc4, Span<byte> values)
    {
        int a = bc4[0], b = bc4[1];
        Span<int> palette = stackalloc int[8];
        palette[0] = a;
        palette[1] = b;
        if (a > b)
        {
            for (int i = 1; i <= 6; i++) palette[i + 1] = ((7 - i) * a + i * b + 3) / 7;
        }
        else
        {
            for (int i = 1; i <= 4; i++) palette[i + 1] = ((5 - i) * a + i * b + 2) / 5;
            palette[6] = 0;
            palette[7] = 255;
        }

        ulong bits = 0;
        for (int i = 5; i >= 0; i--) bits = (bits << 8) | bc4[2 + i];
        for (int i = 0; i < 16; i++) values[i] = (byte)palette[(int)((bits >> (3 * i)) & 7)];
    }

    /// <summary>A BC4 block's values, for the tests.</summary>
    public static void DecodeBc4ForTests(ReadOnlySpan<byte> bc4, Span<byte> values) => DecodeBc4(bc4, values);

    /// <summary>An EAC R11 block's sixteen 11-bit values, row-major, for the tests.</summary>
    public static void DecodeForTests(ReadOnlySpan<byte> eac, Span<int> values)
    {
        int codeword = eac[0], multiplier = eac[1] >> 4, table = eac[1] & 15;
        ulong indices = 0;
        for (int i = 0; i < 6; i++) indices = (indices << 8) | eac[2 + i];
        ReadOnlySpan<sbyte> modifiers = Modifiers;
        for (int x = 0; x < 4; x++)
        {
            for (int y = 0; y < 4; y++)
            {
                int selector = (int)((indices >> (45 - 3 * (4 * x + y))) & 7);
                int modifier = modifiers[table * 8 + selector];
                values[y * 4 + x] = Math.Clamp(codeword * 8 + 4 + modifier * multiplier * 8, 0, 2047);
            }
        }
    }
}
