using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Refii.Core;

using Refii.Ui;

namespace Refii.Textures;

internal static class Engine_BcEncoder
{
    internal static byte[]? Encode(
        byte[] rgba, int width, int height, TexSurfaceFormat format)
    {
        int size = EncodedBytes(width, height, format);
        if (size == 0)
        {
            return null;
        }

        var output = new byte[size];
        return EncodeInto(rgba, width, height, format, output, 0) ? output : null;
    }

    /// <summary>Block bytes for one level, or 0 for a format this encoder does not write.</summary>
    internal static int EncodedBytes(int width, int height, TexSurfaceFormat format)
    {
        int blockBytes = BlockBytes(format);
        return blockBytes * Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4);
    }

    private static int BlockBytes(TexSurfaceFormat format) => format switch
    {
        TexSurfaceFormat.Bc1 => 8,
        TexSurfaceFormat.Bc4 => 8,
        TexSurfaceFormat.Bc3 => 16,
        TexSurfaceFormat.Bc5 => 16,
        _ => 0,
    };

    /// <summary>Encodes one level into <paramref name="output"/> at <paramref name="offset"/>.</summary>
    /// <remarks>Reads a span so a mip chain is encoded level by level without copying each level out.</remarks>
    internal static bool EncodeInto(
        ReadOnlySpan<byte> rgba, int width, int height, TexSurfaceFormat format, byte[] output, int offset)
    {
        int blocksW = Math.Max(1, (width + 3) / 4);
        int blocksH = Math.Max(1, (height + 3) / 4);
        int blockBytes = BlockBytes(format);

        if (blockBytes == 0 || rgba.Length < width * height * 4 ||
            output.Length - offset < blocksW * blocksH * blockBytes)
        {
            return false;
        }

        Span<byte> texels = stackalloc byte[16 * 4];
        Span<byte> target = output.AsSpan(offset);
        int cursor = 0;

        for (int by = 0; by < blocksH; ++by)
        {
            for (int bx = 0; bx < blocksW; ++bx)
            {
                GatherBlock(rgba, width, height, bx * 4, by * 4, texels);

                switch (format)
                {
                    case TexSurfaceFormat.Bc1:
                        WriteBlock(target, cursor, Bc1Block(texels));
                        break;

                    case TexSurfaceFormat.Bc4:
                        WriteBlock(target, cursor, Bc4Block(texels, 4));
                        break;

                    case TexSurfaceFormat.Bc3:
                        WriteBlock(target, cursor, Bc4Block(texels[3..], 4));
                        WriteBlock(target, cursor + 8, Bc1Block(texels));
                        break;

                    case TexSurfaceFormat.Bc5:
                        WriteBlock(target, cursor, Bc4Block(texels, 4));
                        WriteBlock(target, cursor + 8, Bc4Block(texels[1..], 4));
                        break;
                }

                cursor += blockBytes;
            }
        }

        return true;
    }

    private static void WriteBlock(Span<byte> output, int at, ulong block) =>
        MemoryMarshal.Write(output.Slice(at, 8), in block);

    /// <summary>A block's 16 texels, four bytes each, the image's edge repeated past its end.</summary>
    internal static void GatherBlock(
        ReadOnlySpan<byte> rgba, int width, int height, int originX, int originY, Span<byte> texels)
    {
        if (originX + 4 <= width && originY + 4 <= height)
        {
            for (int y = 0; y < 4; ++y)
            {
                rgba.Slice(((originY + y) * width + originX) * 4, 16).CopyTo(texels.Slice(y * 16, 16));
            }

            return;
        }

        for (int y = 0; y < 4; ++y)
        {
            int sy = Math.Min(height - 1, originY + y);
            for (int x = 0; x < 4; ++x)
            {
                int sx = Math.Min(width - 1, originX + x);
                rgba.Slice(((sy * width) + sx) * 4, 4).CopyTo(texels.Slice((y * 16) + (x * 4), 4));
            }
        }
    }

    private static ulong Bc1Block(ReadOnlySpan<byte> texels)
    {
        int minR = 255, minG = 255, minB = 255;
        int maxR = 0, maxG = 0, maxB = 0;

        for (int i = 0; i < 16; ++i)
        {
            int r = texels[(i * 4) + 0];
            int g = texels[(i * 4) + 1];
            int b = texels[(i * 4) + 2];
            minR = Math.Min(minR, r); maxR = Math.Max(maxR, r);
            minG = Math.Min(minG, g); maxG = Math.Max(maxG, g);
            minB = Math.Min(minB, b); maxB = Math.Max(maxB, b);
        }

        ushort high = Pack565(maxR, maxG, maxB);
        ushort low = Pack565(minR, minG, minB);

        if (high < low)
        {
            (high, low) = (low, high);
        }

        uint indices = 0;
        if (high != low)
        {
            Unpack565(high, out int r0, out int g0, out int b0);
            Unpack565(low, out int r1, out int g1, out int b1);
            indices = Bc1Indices(
                texels,
                Vector128.Create(r0, r1, ((2 * r0) + r1) / 3, (r0 + (2 * r1)) / 3),
                Vector128.Create(g0, g1, ((2 * g0) + g1) / 3, (g0 + (2 * g1)) / 3),
                Vector128.Create(b0, b1, ((2 * b0) + b1) / 3, (b0 + (2 * b1)) / 3));
        }

        return high | ((ulong)low << 16) | ((ulong)indices << 32);
    }

    /// <summary>
    /// For each of 16 RGBA texels the palette entry nearest by squared distance, the lowest
    /// index on a tie, packed two bits apiece.
    /// </summary>
    internal static uint Bc1Indices(
        ReadOnlySpan<byte> texels, Vector128<int> paletteR, Vector128<int> paletteG, Vector128<int> paletteB)
    {
        uint indices = 0;
        for (int i = 0; i < 16; ++i)
        {
            Vector128<int> dr = Vector128.Create((int)texels[(i * 4) + 0]) - paletteR;
            Vector128<int> dg = Vector128.Create((int)texels[(i * 4) + 1]) - paletteG;
            Vector128<int> db = Vector128.Create((int)texels[(i * 4) + 2]) - paletteB;
            Vector128<int> distance = (dr * dr) + (dg * dg) + (db * db);

            int best = 0;
            int bestDistance = distance.GetElement(0);
            int d1 = distance.GetElement(1);
            if (d1 < bestDistance) { bestDistance = d1; best = 1; }
            int d2 = distance.GetElement(2);
            if (d2 < bestDistance) { bestDistance = d2; best = 2; }
            if (distance.GetElement(3) < bestDistance) { best = 3; }

            indices |= (uint)best << (i * 2);
        }

        return indices;
    }

    /// <summary>
    /// A BC4 block over 16 values <paramref name="step"/> bytes apart: the extremes, then each
    /// value's 3-bit index.
    /// </summary>
    internal static ulong Bc4Block(ReadOnlySpan<byte> values, int step)
    {
        int min = 255;
        int max = 0;
        for (int i = 0; i < 16; ++i)
        {
            int v = values[i * step];
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        ulong block = (uint)max | ((ulong)(uint)min << 8);
        if (max == min)
        {
            return block;
        }

        int range = max - min;
        ReadOnlySpan<byte> nearest = Bc4Nearest.AsSpan(range * (range + 1) / 2, range + 1);
        ulong indices = 0;
        for (int i = 0; i < 16; ++i)
        {
            indices |= (ulong)nearest[values[i * step] - min] << (i * 3);
        }

        return block | (indices << 16);
    }

    /// <summary>
    /// The BC4 index of each value in each range: the palette entry nearest by absolute distance,
    /// the lowest index on a tie. Entry <c>range * (range + 1) / 2 + value - min</c>. The palette's
    /// offsets from its minimum depend on nothing but the range, so one table serves every block.
    /// </summary>
    private static readonly byte[] Bc4Nearest = BuildBc4Nearest();

    private static byte[] BuildBc4Nearest()
    {
        var table = new byte[256 * 257 / 2];
        Span<int> palette = stackalloc int[8];
        for (int range = 1; range < 256; ++range)
        {
            palette[0] = range;
            palette[1] = 0;
            for (int p = 1; p <= 6; ++p)
            {
                palette[p + 1] = (7 - p) * range / 7;
            }

            for (int value = 0; value <= range; ++value)
            {
                int best = 0;
                int bestDistance = int.MaxValue;
                for (int p = 0; p < 8; ++p)
                {
                    int distance = Math.Abs(value - palette[p]);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = p;
                    }
                }

                table[(range * (range + 1) / 2) + value] = (byte)best;
            }
        }

        return table;
    }

    private static ushort Pack565(int r, int g, int b) =>
        (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));

    private static void Unpack565(ushort value, out int r, out int g, out int b)
    {
        int r5 = (value >> 11) & 0x1F;
        int g6 = (value >> 5) & 0x3F;
        int b5 = value & 0x1F;

        r = (r5 << 3) | (r5 >> 2);
        g = (g6 << 2) | (g6 >> 4);
        b = (b5 << 3) | (b5 >> 2);
    }
}
