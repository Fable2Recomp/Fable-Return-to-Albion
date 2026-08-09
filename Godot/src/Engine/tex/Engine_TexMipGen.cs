using System;

using Refii.Core;

using Refii.Ui;

namespace Refii.Textures;

public static class Engine_TexMipGen
{
    public static byte[] DecodeBc1(byte[] blocks, int width, int height)
    {
        int bw = Math.Max(1, (width + 3) / 4);
        int bh = Math.Max(1, (height + 3) / 4);
        var rgba = new byte[width * height * 4];
        var r = new int[4];
        var g = new int[4];
        var b = new int[4];

        for (int by = 0; by < bh; ++by)
        for (int bx = 0; bx < bw; ++bx)
        {
            int o = (by * bw + bx) * 8;
            if (o + 8 > blocks.Length)
            {
                continue;
            }

            ushort c0 = (ushort)(blocks[o] | (blocks[o + 1] << 8));
            ushort c1 = (ushort)(blocks[o + 2] | (blocks[o + 3] << 8));
            Expand565(c0, out r[0], out g[0], out b[0]);
            Expand565(c1, out r[1], out g[1], out b[1]);

            if (c0 > c1)
            {
                r[2] = (2 * r[0] + r[1]) / 3; g[2] = (2 * g[0] + g[1]) / 3; b[2] = (2 * b[0] + b[1]) / 3;
                r[3] = (r[0] + 2 * r[1]) / 3; g[3] = (g[0] + 2 * g[1]) / 3; b[3] = (b[0] + 2 * b[1]) / 3;
            }
            else
            {
                r[2] = (r[0] + r[1]) / 2; g[2] = (g[0] + g[1]) / 2; b[2] = (b[0] + b[1]) / 2;
                r[3] = 0; g[3] = 0; b[3] = 0;
            }

            uint bits = (uint)(blocks[o + 4] | (blocks[o + 5] << 8) |
                               (blocks[o + 6] << 16) | (blocks[o + 7] << 24));

            for (int py = 0; py < 4; ++py)
            for (int px = 0; px < 4; ++px)
            {
                int x = bx * 4 + px;
                int y = by * 4 + py;
                if (x >= width || y >= height)
                {
                    continue;
                }

                int idx = (int)((bits >> (2 * (py * 4 + px))) & 3);
                int d = (y * width + x) * 4;
                rgba[d] = (byte)r[idx];
                rgba[d + 1] = (byte)g[idx];
                rgba[d + 2] = (byte)b[idx];
                rgba[d + 3] = 255;
            }
        }

        return rgba;
    }

    public static byte[] EncodeBc1(byte[] rgba, int width, int height)
    {
        int bw = Math.Max(1, (width + 3) / 4);
        int bh = Math.Max(1, (height + 3) / 4);
        var blocks = new byte[bw * bh * 8];
        Span<int> pr = stackalloc int[4];
        Span<int> pg = stackalloc int[4];
        Span<int> pb = stackalloc int[4];

        for (int by = 0; by < bh; ++by)
        for (int bx = 0; bx < bw; ++bx)
        {
            int minLum = int.MaxValue;
            int maxLum = int.MinValue;
            int minR = 0, minG = 0, minB = 0, maxR = 0, maxG = 0, maxB = 0;

            for (int py = 0; py < 4; ++py)
            for (int px = 0; px < 4; ++px)
            {
                int x = Math.Min(width - 1, bx * 4 + px);
                int y = Math.Min(height - 1, by * 4 + py);
                int s = (y * width + x) * 4;
                int cr = rgba[s], cg = rgba[s + 1], cb = rgba[s + 2];
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

            int o = (by * bw + bx) * 8;
            blocks[o] = (byte)(c0 & 0xFF);
            blocks[o + 1] = (byte)(c0 >> 8);
            blocks[o + 2] = (byte)(c1 & 0xFF);
            blocks[o + 3] = (byte)(c1 >> 8);

            if (c0 == c1)
            {
                continue;
            }

            Expand565(c0, out pr[0], out pg[0], out pb[0]);
            Expand565(c1, out pr[1], out pg[1], out pb[1]);
            pr[2] = (2 * pr[0] + pr[1]) / 3; pg[2] = (2 * pg[0] + pg[1]) / 3; pb[2] = (2 * pb[0] + pb[1]) / 3;
            pr[3] = (pr[0] + 2 * pr[1]) / 3; pg[3] = (pg[0] + 2 * pg[1]) / 3; pb[3] = (pb[0] + 2 * pb[1]) / 3;

            uint bits = 0;
            for (int py = 0; py < 4; ++py)
            for (int px = 0; px < 4; ++px)
            {
                int x = Math.Min(width - 1, bx * 4 + px);
                int y = Math.Min(height - 1, by * 4 + py);
                int s = (y * width + x) * 4;

                int best = 0;
                int bestErr = int.MaxValue;
                for (int i = 0; i < 4; ++i)
                {
                    int dr = rgba[s] - pr[i];
                    int dg = rgba[s + 1] - pg[i];
                    int db = rgba[s + 2] - pb[i];
                    int err = dr * dr + dg * dg + db * db;
                    if (err < bestErr) { bestErr = err; best = i; }
                }

                bits |= (uint)best << (2 * (py * 4 + px));
            }

            blocks[o + 4] = (byte)(bits & 0xFF);
            blocks[o + 5] = (byte)((bits >> 8) & 0xFF);
            blocks[o + 6] = (byte)((bits >> 16) & 0xFF);
            blocks[o + 7] = (byte)((bits >> 24) & 0xFF);
        }

        return blocks;
    }

    public static byte[] DecodeBc4(byte[] blocks, int width, int height)
    {
        int bw = Math.Max(1, (width + 3) / 4);
        int bh = Math.Max(1, (height + 3) / 4);
        var gray = new byte[width * height];
        Span<int> palette = stackalloc int[8];

        for (int by = 0; by < bh; ++by)
        for (int bx = 0; bx < bw; ++bx)
        {
            int o = (by * bw + bx) * 8;
            if (o + 8 > blocks.Length)
            {
                continue;
            }

            int a0 = blocks[o];
            int a1 = blocks[o + 1];
            palette[0] = a0;
            palette[1] = a1;
            if (a0 > a1)
            {
                for (int i = 1; i < 7; ++i) palette[i + 1] = ((7 - i) * a0 + i * a1) / 7;
            }
            else
            {
                for (int i = 1; i < 5; ++i) palette[i + 1] = ((5 - i) * a0 + i * a1) / 5;
                palette[6] = 0;
                palette[7] = 255;
            }

            ulong bits = 0;
            for (int i = 0; i < 6; ++i) bits |= (ulong)blocks[o + 2 + i] << (8 * i);

            for (int py = 0; py < 4; ++py)
            for (int px = 0; px < 4; ++px)
            {
                int x = bx * 4 + px;
                int y = by * 4 + py;
                if (x >= width || y >= height) continue;
                int idx = (int)((bits >> (3 * (py * 4 + px))) & 7);
                gray[y * width + x] = (byte)palette[idx];
            }
        }

        return gray;
    }

    public static byte[] EncodeBc4(byte[] gray, int width, int height)
    {
        int bw = Math.Max(1, (width + 3) / 4);
        int bh = Math.Max(1, (height + 3) / 4);
        var blocks = new byte[bw * bh * 8];
        Span<int> palette = stackalloc int[8];

        for (int by = 0; by < bh; ++by)
        for (int bx = 0; bx < bw; ++bx)
        {
            int lo = 255, hi = 0;
            for (int py = 0; py < 4; ++py)
            for (int px = 0; px < 4; ++px)
            {
                int x = Math.Min(width - 1, bx * 4 + px);
                int y = Math.Min(height - 1, by * 4 + py);
                int v = gray[y * width + x];
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }

            int o = (by * bw + bx) * 8;
            blocks[o] = (byte)hi;
            blocks[o + 1] = (byte)lo;
            if (hi == lo)
            {
                continue;
            }

            palette[0] = hi;
            palette[1] = lo;
            for (int i = 1; i < 7; ++i) palette[i + 1] = ((7 - i) * hi + i * lo) / 7;

            ulong bits = 0;
            for (int py = 0; py < 4; ++py)
            for (int px = 0; px < 4; ++px)
            {
                int x = Math.Min(width - 1, bx * 4 + px);
                int y = Math.Min(height - 1, by * 4 + py);
                int v = gray[y * width + x];

                int best = 0, bestErr = int.MaxValue;
                for (int i = 0; i < 8; ++i)
                {
                    int err = Math.Abs(v - palette[i]);
                    if (err < bestErr) { bestErr = err; best = i; }
                }

                bits |= (ulong)best << (3 * (py * 4 + px));
            }

            for (int i = 0; i < 6; ++i) blocks[o + 2 + i] = (byte)((bits >> (8 * i)) & 0xFF);
        }

        return blocks;
    }

    public static byte[] Downscale(byte[] src, int width, int height, int channels)
    {
        int dw = Math.Max(1, width / 2);
        int dh = Math.Max(1, height / 2);
        var dst = new byte[dw * dh * channels];

        for (int y = 0; y < dh; ++y)
        for (int x = 0; x < dw; ++x)
        for (int c = 0; c < channels; ++c)
        {
            int x0 = Math.Min(width - 1, x * 2);
            int y0 = Math.Min(height - 1, y * 2);
            int x1 = Math.Min(width - 1, x * 2 + 1);
            int y1 = Math.Min(height - 1, y * 2 + 1);

            int sum = src[(y0 * width + x0) * channels + c] +
                      src[(y0 * width + x1) * channels + c] +
                      src[(y1 * width + x0) * channels + c] +
                      src[(y1 * width + x1) * channels + c];
            dst[(y * dw + x) * channels + c] = (byte)(sum / 4);
        }

        return dst;
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
