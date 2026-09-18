using System;

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

        var texels = new byte[16 * 4];
        int cursor = offset;

        for (int by = 0; by < blocksH; ++by)
        {
            for (int bx = 0; bx < blocksW; ++bx)
            {
                GatherBlock(rgba, width, height, bx * 4, by * 4, texels);

                switch (format)
                {
                    case TexSurfaceFormat.Bc1:
                        EncodeBc1Block(texels, output, cursor);
                        break;

                    case TexSurfaceFormat.Bc4:
                        EncodeBc4Block(texels, 0, output, cursor);
                        break;

                    case TexSurfaceFormat.Bc3:
                        EncodeBc4Block(texels, 3, output, cursor);
                        EncodeBc1Block(texels, output, cursor + 8);
                        break;

                    case TexSurfaceFormat.Bc5:
                        EncodeBc4Block(texels, 0, output, cursor);
                        EncodeBc4Block(texels, 1, output, cursor + 8);
                        break;
                }

                cursor += blockBytes;
            }
        }

        return true;
    }

    private static void GatherBlock(
        ReadOnlySpan<byte> rgba, int width, int height, int originX, int originY, byte[] texels)
    {
        for (int y = 0; y < 4; ++y)
        {
            int sy = Math.Min(height - 1, originY + y);
            for (int x = 0; x < 4; ++x)
            {
                int sx = Math.Min(width - 1, originX + x);
                int src = ((sy * width) + sx) * 4;
                int dst = ((y * 4) + x) * 4;
                texels[dst] = rgba[src];
                texels[dst + 1] = rgba[src + 1];
                texels[dst + 2] = rgba[src + 2];
                texels[dst + 3] = rgba[src + 3];
            }
        }
    }

    private static void EncodeBc1Block(byte[] texels, byte[] output, int offset)
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

        output[offset + 0] = (byte)(high & 0xFF);
        output[offset + 1] = (byte)(high >> 8);
        output[offset + 2] = (byte)(low & 0xFF);
        output[offset + 3] = (byte)(low >> 8);

        if (high == low)
        {
            output[offset + 4] = 0;
            output[offset + 5] = 0;
            output[offset + 6] = 0;
            output[offset + 7] = 0;
            return;
        }

        Span<int> paletteR = stackalloc int[4];
        Span<int> paletteG = stackalloc int[4];
        Span<int> paletteB = stackalloc int[4];
        Unpack565(high, out paletteR[0], out paletteG[0], out paletteB[0]);
        Unpack565(low, out paletteR[1], out paletteG[1], out paletteB[1]);
        paletteR[2] = ((2 * paletteR[0]) + paletteR[1]) / 3;
        paletteG[2] = ((2 * paletteG[0]) + paletteG[1]) / 3;
        paletteB[2] = ((2 * paletteB[0]) + paletteB[1]) / 3;
        paletteR[3] = (paletteR[0] + (2 * paletteR[1])) / 3;
        paletteG[3] = (paletteG[0] + (2 * paletteG[1])) / 3;
        paletteB[3] = (paletteB[0] + (2 * paletteB[1])) / 3;

        uint indices = 0;
        for (int i = 0; i < 16; ++i)
        {
            int r = texels[(i * 4) + 0];
            int g = texels[(i * 4) + 1];
            int b = texels[(i * 4) + 2];

            int best = 0;
            int bestDistance = int.MaxValue;
            for (int p = 0; p < 4; ++p)
            {
                int dr = r - paletteR[p];
                int dg = g - paletteG[p];
                int db = b - paletteB[p];
                int distance = (dr * dr) + (dg * dg) + (db * db);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = p;
                }
            }

            indices |= (uint)best << (i * 2);
        }

        output[offset + 4] = (byte)(indices & 0xFF);
        output[offset + 5] = (byte)((indices >> 8) & 0xFF);
        output[offset + 6] = (byte)((indices >> 16) & 0xFF);
        output[offset + 7] = (byte)((indices >> 24) & 0xFF);
    }

    private static void EncodeBc4Block(byte[] texels, int channel, byte[] output, int offset)
    {
        int min = 255;
        int max = 0;
        for (int i = 0; i < 16; ++i)
        {
            int v = texels[(i * 4) + channel];
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        output[offset + 0] = (byte)max;
        output[offset + 1] = (byte)min;

        if (max == min)
        {
            for (int i = 2; i < 8; ++i)
            {
                output[offset + i] = 0;
            }

            return;
        }

        Span<int> palette = stackalloc int[8];
        palette[0] = max;
        palette[1] = min;
        for (int p = 1; p <= 6; ++p)
        {
            palette[p + 1] = (((7 - p) * max) + (p * min)) / 7;
        }

        ulong indices = 0;
        for (int i = 0; i < 16; ++i)
        {
            int v = texels[(i * 4) + channel];
            int best = 0;
            int bestDistance = int.MaxValue;
            for (int p = 0; p < 8; ++p)
            {
                int distance = Math.Abs(v - palette[p]);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = p;
                }
            }

            indices |= (ulong)best << (i * 3);
        }

        for (int i = 0; i < 6; ++i)
        {
            output[offset + 2 + i] = (byte)((indices >> (i * 8)) & 0xFF);
        }
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
