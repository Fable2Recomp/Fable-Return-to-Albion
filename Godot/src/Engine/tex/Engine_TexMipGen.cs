using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Refii.Core;

using Refii.Ui;

namespace Refii.Textures;

public static class Engine_TexMipGen
{
    public static byte[] DecodeBc1(byte[] blocks, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        DecodeBc1Into(blocks, 8, 0, width, height, rgba);
        return rgba;
    }

    /// <summary>
    /// RGBA8 from BC1 blocks <paramref name="stride"/> bytes apart, the first at <paramref name="at"/>.
    /// A block past the end of <paramref name="blocks"/> leaves its texels as they were.
    /// </summary>
    internal static void DecodeBc1Into(
        ReadOnlySpan<byte> blocks, int stride, int at, int width, int height, Span<byte> rgba)
    {
        int bw = Math.Max(1, (width + 3) / 4);
        int bh = Math.Max(1, (height + 3) / 4);
        Span<uint> texels = MemoryMarshal.Cast<byte, uint>(rgba);
        Span<uint> palette = stackalloc uint[4];

        for (int by = 0; by < bh; ++by)
        for (int bx = 0; bx < bw; ++bx)
        {
            int o = ((by * bw + bx) * stride) + at;
            if (o + 8 > blocks.Length)
            {
                continue;
            }

            ulong block = MemoryMarshal.Read<ulong>(blocks.Slice(o, 8));
            var c0 = (ushort)block;
            var c1 = (ushort)(block >> 16);
            Expand565(c0, out int r0, out int g0, out int b0);
            Expand565(c1, out int r1, out int g1, out int b1);
            palette[0] = PackOpaque(r0, g0, b0);
            palette[1] = PackOpaque(r1, g1, b1);

            if (c0 > c1)
            {
                palette[2] = PackOpaque((2 * r0 + r1) / 3, (2 * g0 + g1) / 3, (2 * b0 + b1) / 3);
                palette[3] = PackOpaque((r0 + 2 * r1) / 3, (g0 + 2 * g1) / 3, (b0 + 2 * b1) / 3);
            }
            else
            {
                palette[2] = PackOpaque((r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2);
                palette[3] = PackOpaque(0, 0, 0);
            }

            var bits = (uint)(block >> 32);
            for (int py = 0; py < 4; ++py)
            {
                int y = by * 4 + py;
                if (y >= height)
                {
                    break;
                }

                for (int px = 0; px < 4; ++px, bits >>= 2)
                {
                    int x = bx * 4 + px;
                    if (x < width)
                    {
                        texels[y * width + x] = palette[(int)(bits & 3)];
                    }
                }
            }
        }
    }

    private static uint PackOpaque(int r, int g, int b) =>
        (uint)r | ((uint)g << 8) | ((uint)b << 16) | 0xFF000000u;

    public static byte[] EncodeBc1(byte[] rgba, int width, int height)
    {
        var blocks = new byte[Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8];
        EncodeBc1Into(rgba, width, height, blocks, 8, 0);
        return blocks;
    }

    /// <summary>Writes BC1 blocks <paramref name="stride"/> bytes apart, the first at <paramref name="at"/>.</summary>
    internal static void EncodeBc1Into(
        ReadOnlySpan<byte> rgba, int width, int height, Span<byte> output, int stride, int at)
    {
        int bw = Math.Max(1, (width + 3) / 4);
        int bh = Math.Max(1, (height + 3) / 4);
        Span<byte> texels = stackalloc byte[16 * 4];

        for (int by = 0; by < bh; ++by)
        for (int bx = 0; bx < bw; ++bx)
        {
            Engine_BcEncoder.GatherBlock(rgba, width, height, bx * 4, by * 4, texels);

            int minLum = int.MaxValue;
            int maxLum = int.MinValue;
            int minR = 0, minG = 0, minB = 0, maxR = 0, maxG = 0, maxB = 0;

            for (int i = 0; i < 16; ++i)
            {
                int cr = texels[i * 4], cg = texels[(i * 4) + 1], cb = texels[(i * 4) + 2];
                int lum = cr * 77 + cg * 150 + cb * 29;
                if (lum < minLum) { minLum = lum; minR = cr; minG = cg; minB = cb; }
                if (lum > maxLum) { maxLum = lum; maxR = cr; maxG = cg; maxB = cb; }
            }

            ushort c0 = Pack565(maxR, maxG, maxB);
            ushort c1 = Pack565(minR, minG, minB);
            if (c0 < c1)
            {
                (c0, c1) = (c1, c0);
            }

            uint bits = 0;
            if (c0 != c1)
            {
                Expand565(c0, out int r0, out int g0, out int b0);
                Expand565(c1, out int r1, out int g1, out int b1);
                bits = Engine_BcEncoder.Bc1Indices(
                    texels,
                    Vector128.Create(r0, r1, (2 * r0 + r1) / 3, (r0 + 2 * r1) / 3),
                    Vector128.Create(g0, g1, (2 * g0 + g1) / 3, (g0 + 2 * g1) / 3),
                    Vector128.Create(b0, b1, (2 * b0 + b1) / 3, (b0 + 2 * b1) / 3));
            }

            ulong block = c0 | ((ulong)c1 << 16) | ((ulong)bits << 32);
            MemoryMarshal.Write(output.Slice(((by * bw + bx) * stride) + at, 8), in block);
        }
    }

    public static byte[] DecodeBc4(byte[] blocks, int width, int height)
    {
        var gray = new byte[width * height];
        DecodeBc4Into(blocks, 8, 0, width, height, gray);
        return gray;
    }

    /// <summary>
    /// One channel from BC4 blocks <paramref name="stride"/> bytes apart, the first at
    /// <paramref name="at"/>. A block past the end of <paramref name="blocks"/> leaves its texels as they were.
    /// </summary>
    internal static void DecodeBc4Into(
        ReadOnlySpan<byte> blocks, int stride, int at, int width, int height, Span<byte> gray)
    {
        int bw = Math.Max(1, (width + 3) / 4);
        int bh = Math.Max(1, (height + 3) / 4);
        Span<byte> palette = stackalloc byte[8];

        for (int by = 0; by < bh; ++by)
        for (int bx = 0; bx < bw; ++bx)
        {
            int o = ((by * bw + bx) * stride) + at;
            if (o + 8 > blocks.Length)
            {
                continue;
            }

            ulong block = MemoryMarshal.Read<ulong>(blocks.Slice(o, 8));
            int a0 = (byte)block;
            int a1 = (byte)(block >> 8);
            palette[0] = (byte)a0;
            palette[1] = (byte)a1;
            if (a0 > a1)
            {
                for (int i = 1; i < 7; ++i) palette[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
            }
            else
            {
                for (int i = 1; i < 5; ++i) palette[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                palette[6] = 0;
                palette[7] = 255;
            }

            ulong bits = block >> 16;
            for (int py = 0; py < 4; ++py)
            {
                int y = by * 4 + py;
                if (y >= height)
                {
                    break;
                }

                for (int px = 0; px < 4; ++px, bits >>= 3)
                {
                    int x = bx * 4 + px;
                    if (x < width)
                    {
                        gray[y * width + x] = palette[(int)(bits & 7)];
                    }
                }
            }
        }
    }

    public static byte[] EncodeBc4(byte[] gray, int width, int height)
    {
        var blocks = new byte[Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8];
        EncodeBc4Into(gray, width, height, blocks, 8, 0);
        return blocks;
    }

    /// <summary>Writes BC4 blocks <paramref name="stride"/> bytes apart, the first at <paramref name="at"/>.</summary>
    internal static void EncodeBc4Into(
        ReadOnlySpan<byte> gray, int width, int height, Span<byte> output, int stride, int at)
    {
        int bw = Math.Max(1, (width + 3) / 4);
        int bh = Math.Max(1, (height + 3) / 4);
        Span<byte> texels = stackalloc byte[16];

        for (int by = 0; by < bh; ++by)
        for (int bx = 0; bx < bw; ++bx)
        {
            for (int py = 0; py < 4; ++py)
            {
                int row = Math.Min(height - 1, by * 4 + py) * width;
                for (int px = 0; px < 4; ++px)
                {
                    texels[py * 4 + px] = gray[row + Math.Min(width - 1, bx * 4 + px)];
                }
            }

            ulong block = Engine_BcEncoder.Bc4Block(texels, 1);
            MemoryMarshal.Write(output.Slice(((by * bw + bx) * stride) + at, 8), in block);
        }
    }

    public static byte[] Downscale(byte[] src, int width, int height, int channels)
    {
        var dst = new byte[Math.Max(1, width / 2) * Math.Max(1, height / 2) * channels];
        Downscale(src, width, height, channels, dst);
        return dst;
    }

    /// <summary>
    /// A 2x2 box. <paramref name="dst"/> may be the start of <paramref name="src"/>: nothing is
    /// written before the texels that land on it have been read.
    /// </summary>
    internal static void Downscale(ReadOnlySpan<byte> src, int width, int height, int channels, Span<byte> dst)
    {
        int dw = Math.Max(1, width / 2);
        int dh = Math.Max(1, height / 2);

        for (int y = 0; y < dh; ++y)
        {
            int row0 = Math.Min(height - 1, y * 2) * width * channels;
            int row1 = Math.Min(height - 1, y * 2 + 1) * width * channels;
            int d = y * dw * channels;

            for (int x = 0; x < dw; ++x)
            {
                int x0 = Math.Min(width - 1, x * 2) * channels;
                int x1 = Math.Min(width - 1, x * 2 + 1) * channels;
                for (int c = 0; c < channels; ++c, ++d)
                {
                    int sum = src[row0 + x0 + c] + src[row0 + x1 + c] + src[row1 + x0 + c] + src[row1 + x1 + c];
                    dst[d] = (byte)(sum / 4);
                }
            }
        }
    }

    private static void Expand565(ushort c, out int r, out int g, out int b)
    {
        r = ((c >> 11) & 0x1F) * 255 / 31;
        g = ((c >> 5) & 0x3F) * 255 / 63;
        b = (c & 0x1F) * 255 / 31;
    }

    private static ushort Pack565(int r, int g, int b) =>
        (ushort)(((r * 31 / 255) << 11) | ((g * 63 / 255) << 5) | (b * 31 / 255));
}
