using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Refii.Textures;

namespace Refii.Level;

/// <summary>
/// Synthesizes and caches a shared, toroidally seamless 512x512 RGBA8 channel-packed water foam
/// texture with coverage-preserving mipmaps:
///   R = Billowy aerated macro-froth (dual-domain-warped periodic Perlin FBM),
///   G = Clustered spherical medium bubbles (multi-radius toroidal bubble domes &amp; rims, zero Voronoi spiderwebs),
///   B = Fine fizzy micro-bubbles &amp; popping wake specks,
///   A = Histogram-balanced 6-octave curl-warped turbulent erosion field.
/// </summary>
public static class Engine_WaterFoamTexture
{
    public const int DefaultSize = 512;
    private const string CacheFileName = "water_foam_v2_512.bin";

    private static readonly object Sync = new();
    private static ImageTexture? _shared;

    /// <summary>Computes the exact total byte length of an RGBA8 mipmap chain from <paramref name="baseSize"/> down to 1x1.</summary>
    public static int ComputeTotalMipBytes(int baseSize)
    {
        if (baseSize <= 0 || (baseSize & (baseSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseSize), "Foam texture baseSize must be a positive power of two.");
        }

        int total = 0;
        for (int s = baseSize; s >= 1; s >>= 1)
        {
            total += s * s * 4;
        }

        return total;
    }

    /// <summary>Returns the shared GPU <see cref="ImageTexture"/> (safe to call from main or water-build worker threads).</summary>
    public static ImageTexture? Get()
    {
        if (Refii.Core.Engine_RdHealth.IsTestHost || Refii.Core.Engine_RdHealth.IsDeviceLost)
        {
            return null;
        }

        lock (Sync)
        {
            if (_shared is not null && GodotObject.IsInstanceValid(_shared))
            {
                return _shared;
            }

            int expectedBytes = ComputeTotalMipBytes(DefaultSize);
            byte[]? mipBytes = TryReadCache(expectedBytes);
            if (mipBytes is null)
            {
                mipBytes = BuildMipmappedRgba8(DefaultSize);
                TryWriteCache(mipBytes);
            }

            var image = Image.CreateFromData(DefaultSize, DefaultSize, true, Image.Format.Rgba8, mipBytes);
            if (image is null || image.IsEmpty())
            {
                return null;
            }

            _shared = Engine_TextureUpload.CreateTexture(image);
            return _shared;
        }
    }

    private static byte[]? TryReadCache(int expectedBytes)
    {
        try
        {
            string dir = Engine_TexCache.CacheRoot;
            if (string.IsNullOrEmpty(dir))
            {
                return null;
            }

            string path = Path.Combine(dir, CacheFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            return bytes.Length == expectedBytes ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryWriteCache(byte[] bytes)
    {
        try
        {
            string dir = Engine_TexCache.CacheRoot;
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, CacheFileName);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Pure-managed deterministic generator building the full RGBA8 mipmap chain (<paramref name="baseSize"/> down to 1x1).
    /// Safe to execute in headless unit tests without initializing unmanaged Godot classes.
    /// </summary>
    public static byte[] BuildMipmappedRgba8(int baseSize = DefaultSize)
    {
        int totalBytes = ComputeTotalMipBytes(baseSize);
        int pixelCount = baseSize * baseSize;

        var rawMeso = new float[pixelCount];
        var rawFizz = new float[pixelCount];
        StampSphericalBubbles(rawMeso, baseSize, isFizz: false, seedBase: 0x3A8F19C5u);
        StampSphericalBubbles(rawFizz, baseSize, isFizz: true, seedBase: 0x71E2B49Du);

        var rBuf = new float[pixelCount];
        var gBuf = new float[pixelCount];
        var bBuf = new float[pixelCount];
        var aRaw = new float[pixelCount];

        float invSize = 1f / baseSize;
        Parallel.For(0, baseSize, y =>
        {
            float v = (y + 0.5f) * invSize;
            int row = y * baseSize;
            for (int x = 0; x < baseSize; x++)
            {
                float u = (x + 0.5f) * invSize;
                int idx = row + x;

                // Divergence-free periodic 2D curl warp so all channels swirl seamlessly across tile borders.
                PeriodicCurl(u, v, 3, 0x11457791u, out float cx0, out float cy0);
                PeriodicCurl(u, v, 7, 0x592C81E3u, out float cx1, out float cy1);

                float wu = Wrap01(u + (cx0 * 0.048f) + (cx1 * 0.019f));
                float wv = Wrap01(v + (cy0 * 0.048f) + (cy1 * 0.019f));

                // Second-stage domain warp for billowy aerated macro-froth (R) and turbulent erosion (A).
                float fbmWarpX = PeriodicFbm(wu, wv, basePeriod: 4, octaves: 3, seed: 0x9E3779B9u) - 0.5f;
                float fbmWarpY = PeriodicFbm(wu, wv, basePeriod: 4, octaves: 3, seed: 0x85EBCA6Bu) - 0.5f;
                float wu2 = Wrap01(wu + (fbmWarpX * 0.095f));
                float wv2 = Wrap01(wv + (fbmWarpY * 0.095f));

                float macroFbm = PeriodicFbm(wu2, wv2, basePeriod: 4, octaves: 5, seed: 0xC2B2AE35u);
                float fineFbm = PeriodicFbm(wu, wv, basePeriod: 12, octaves: 3, seed: 0x27D4EB2Fu);
                rBuf[idx] = (macroFbm * 0.78f) + (fineFbm * 0.22f);

                // Sample curl-advected spherical medium bubbles (G) and fine fizz (B) with bilinear toroidal wrap.
                float bu = Wrap01(u + (cx0 * 0.014f) + (cx1 * 0.008f));
                float bv = Wrap01(v + (cy0 * 0.014f) + (cy1 * 0.008f));
                gBuf[idx] = Math.Clamp(SampleToroidalBilinear(rawMeso, baseSize, bu, bv) * 1.18f, 0f, 1f);
                bBuf[idx] = Math.Clamp(SampleToroidalBilinear(rawFizz, baseSize, Wrap01(u + (cx1 * 0.010f)), Wrap01(v + (cy1 * 0.010f))) * 1.18f, 0f, 1f);

                // 6-octave curl-warped erosion heightmap (histogram-equalized below).
                aRaw[idx] = PeriodicFbm(wu2, wv2, basePeriod: 3, octaves: 6, seed: 0x165667B1u);
            }
        });

        // Normalize R across its full [min..max] FBM range before shaping billowy froth cushions,
        // and histogram-balance A into [0, 1] using a 1024-bin CDF so erosion thresholds respond linearly.
        const int HistBins = 1024;
        var hist = new int[HistBins];
        float rMin = float.PositiveInfinity;
        float rMax = float.NegativeInfinity;
        float aMin = float.PositiveInfinity;
        float aMax = float.NegativeInfinity;
        for (int i = 0; i < pixelCount; i++)
        {
            float rv = rBuf[i];
            if (rv < rMin) rMin = rv;
            if (rv > rMax) rMax = rv;

            float v = aRaw[i];
            if (v < aMin) aMin = v;
            if (v > aMax) aMax = v;
        }

        float rSpan = MathF.Max(rMax - rMin, 1e-6f);
        for (int i = 0; i < pixelCount; i++)
        {
            float rNorm = (rBuf[i] - rMin) / rSpan;
            rBuf[i] = SmoothStep(0.32f, 0.88f, rNorm);
        }

        float aSpan = MathF.Max(aMax - aMin, 1e-6f);
        for (int i = 0; i < pixelCount; i++)
        {
            int bin = Math.Clamp((int)((aRaw[i] - aMin) / aSpan * (HistBins - 1)), 0, HistBins - 1);
            hist[bin]++;
        }

        var cdf = new float[HistBins];
        int running = 0;
        float invPixels = 1f / pixelCount;
        for (int b = 0; b < HistBins; b++)
        {
            running += hist[b];
            cdf[b] = running * invPixels;
        }

        var output = new byte[totalBytes];
        for (int i = 0; i < pixelCount; i++)
        {
            float norm = ((aRaw[i] - aMin) / aSpan) * (HistBins - 1);
            int b0 = Math.Clamp((int)norm, 0, HistBins - 1);
            int b1 = Math.Min(b0 + 1, HistBins - 1);
            float frac = norm - b0;
            float eqA = cdf[b0] + ((cdf[b1] - cdf[b0]) * frac);

            int dst = i * 4;
            output[dst] = ToByte(rBuf[i]);
            output[dst + 1] = ToByte(gBuf[i]);
            output[dst + 2] = ToByte(bBuf[i]);
            output[dst + 3] = ToByte(eqA);
        }

        // Build coverage-preserving mip chain (0.72 * avg + 0.28 * max on G/B bubbles; exact box avg on R/A).
        int srcOffset = 0;
        int srcSize = baseSize;
        while (srcSize > 1)
        {
            int dstSize = srcSize >> 1;
            int dstOffset = srcOffset + (srcSize * srcSize * 4);
            for (int y = 0; y < dstSize; y++)
            {
                int sy0 = y * 2;
                int sy1 = sy0 + 1;
                for (int x = 0; x < dstSize; x++)
                {
                    int sx0 = x * 2;
                    int sx1 = sx0 + 1;

                    int p00 = srcOffset + (((sy0 * srcSize) + sx0) * 4);
                    int p10 = srcOffset + (((sy0 * srcSize) + sx1) * 4);
                    int p01 = srcOffset + (((sy1 * srcSize) + sx0) * 4);
                    int p11 = srcOffset + (((sy1 * srcSize) + sx1) * 4);

                    int dst = dstOffset + (((y * dstSize) + x) * 4);
                    output[dst] = (byte)((output[p00] + output[p10] + output[p01] + output[p11] + 2) >> 2);
                    output[dst + 1] = CoverageMipByte(output[p00 + 1], output[p10 + 1], output[p01 + 1], output[p11 + 1]);
                    output[dst + 2] = CoverageMipByte(output[p00 + 2], output[p10 + 2], output[p01 + 2], output[p11 + 2]);
                    output[dst + 3] = (byte)((output[p00 + 3] + output[p10 + 3] + output[p01 + 3] + output[p11 + 3] + 2) >> 2);
                }
            }

            srcOffset = dstOffset;
            srcSize = dstSize;
        }

        return output;
    }

    private static byte CoverageMipByte(byte a, byte b, byte c, byte d)
    {
        float avg = (a + b + c + d) * 0.25f;
        int m = Math.Max(Math.Max(a, b), Math.Max(c, d));
        return (byte)Math.Clamp((int)MathF.Round((avg * 0.72f) + (m * 0.28f)), 0, 255);
    }

    private static void StampSphericalBubbles(float[] grid, int size, bool isFizz, uint seedBase)
    {
        int count = isFizz
            ? Math.Max(160, (size * size) / 48)
            : Math.Max(64, (size * size) / 210);

        for (int i = 0; i < count; i++)
        {
            uint s = HashU32((uint)i * 0x9E3779B9u ^ seedBase);
            float cu = Hash01(s ^ 0xA341316Cu);
            float cv = Hash01(s ^ 0xC8013EA4u);

            // Concentrate bubbles into organic drifting foam rafts so clusters have natural dark-water gaps.
            float raft = PeriodicFbm(cu, cv, basePeriod: isFizz ? 6 : 4, octaves: 3, seed: seedBase ^ 0x55AA55AAu);
            float accept = SmoothStep(isFizz ? 0.36f : 0.40f, 0.60f, raft);
            if (Hash01(s ^ 0xAD90777Du) > (0.18f + (0.82f * accept)))
            {
                continue;
            }

            float tPow = MathF.Pow(Hash01(s ^ 0x7E95761Eu), isFizz ? 1.8f : 2.1f);
            float radiusUv = isFizz
                ? (0.0022f + (0.0052f * tPow))
                : (0.0046f + (0.0152f * tPow));
            float radiusPx = MathF.Max(radiusUv * size, 0.90f);
            float strength = (0.64f + (0.36f * Hash01(s ^ 0x1F83D9ABu))) * (0.52f + (0.48f * accept));

            float pxCenter = cu * size;
            float pyCenter = cv * size;
            int rInt = (int)MathF.Ceiling(radiusPx + 1.0f);
            int cx = (int)MathF.Floor(pxCenter);
            int cy = (int)MathF.Floor(pyCenter);
            float invRadius = 1f / radiusPx;

            for (int dy = -rInt; dy <= rInt; dy++)
            {
                int yy = ((cy + dy) % size + size) % size;
                float offY = (cy + dy + 0.5f) - pyCenter;
                int row = yy * size;
                for (int dx = -rInt; dx <= rInt; dx++)
                {
                    float offX = (cx + dx + 0.5f) - pxCenter;
                    float t = MathF.Sqrt((offX * offX) + (offY * offY)) * invRadius;
                    if (t >= 1.06f)
                    {
                        continue;
                    }

                    float profile;
                    if (isFizz)
                    {
                        // Crisp round micro-bubble speck with soft anti-aliased skirt.
                        profile = (1f - SmoothStep(0.35f, 1.04f, t)) * strength;
                    }
                    else
                    {
                        // Spherical thin-film bubble: bright diffuse meniscus rim + translucent pillowy dome interior.
                        float rim = SmoothStep(0.36f, 0.84f, t) * (1f - SmoothStep(0.85f, 1.05f, t));
                        float dome = MathF.Max(1f - (t * t), 0f) * 0.42f;
                        profile = Math.Clamp((rim * 0.92f) + dome, 0f, 1f) * strength;
                    }

                    int xx = ((cx + dx) % size + size) % size;
                    int idx = row + xx;
                    if (profile > grid[idx])
                    {
                        grid[idx] = profile;
                    }
                }
            }
        }
    }

    private static float SampleToroidalBilinear(float[] grid, int size, float u, float v)
    {
        float px = (u * size) - 0.5f;
        float py = (v * size) - 0.5f;
        int x0 = (int)MathF.Floor(px);
        int y0 = (int)MathF.Floor(py);
        float fx = px - x0;
        float fy = py - y0;

        int wx0 = ((x0 % size) + size) % size;
        int wx1 = (wx0 + 1) % size;
        int wy0 = ((y0 % size) + size) % size;
        int wy1 = (wy0 + 1) % size;

        float v00 = grid[(wy0 * size) + wx0];
        float v10 = grid[(wy0 * size) + wx1];
        float v01 = grid[(wy1 * size) + wx0];
        float v11 = grid[(wy1 * size) + wx1];

        float top = v00 + ((v10 - v00) * fx);
        float bot = v01 + ((v11 - v01) * fx);
        return top + ((bot - top) * fy);
    }

    private static void PeriodicCurl(float u, float v, int period, uint seed, out float curlX, out float curlY)
    {
        const float eps = 1f / 256f;
        float nPy = PeriodicFbm(u, Wrap01(v + eps), period, 2, seed);
        float nNy = PeriodicFbm(u, Wrap01(v - eps), period, 2, seed);
        float nPx = PeriodicFbm(Wrap01(u + eps), v, period, 2, seed);
        float nNx = PeriodicFbm(Wrap01(u - eps), v, period, 2, seed);
        curlX = (nPy - nNy) / (2f * eps * period);
        curlY = -(nPx - nNx) / (2f * eps * period);
    }

    private static float PeriodicFbm(float u, float v, int basePeriod, int octaves, uint seed)
    {
        float sum = 0f;
        float amp = 0.5f;
        float weightSum = 0f;
        int p = Math.Max(basePeriod, 1);
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * (0.5f + (0.5f * PeriodicPerlin(u, v, p, seed + ((uint)o * 0x45D9F3Bu))));
            weightSum += amp;
            amp *= 0.5f;
            p <<= 1;
        }

        return weightSum > 0f ? Math.Clamp(sum / weightSum, 0f, 1f) : 0.5f;
    }

    private static float PeriodicPerlin(float u, float v, int period, uint seed)
    {
        float px = u * period;
        float py = v * period;
        int xi = (int)MathF.Floor(px);
        int yi = (int)MathF.Floor(py);
        float fx = px - xi;
        float fy = py - yi;

        int x0 = ((xi % period) + period) % period;
        int x1 = (x0 + 1) % period;
        int y0 = ((yi % period) + period) % period;
        int y1 = (y0 + 1) % period;

        float sx = fx * fx * fx * ((fx * ((fx * 6f) - 15f)) + 10f);
        float sy = fy * fy * fy * ((fy * ((fy * 6f) - 15f)) + 10f);

        float d00 = GradDot(x0, y0, seed, fx, fy);
        float d10 = GradDot(x1, y0, seed, fx - 1f, fy);
        float d01 = GradDot(x0, y1, seed, fx, fy - 1f);
        float d11 = GradDot(x1, y1, seed, fx - 1f, fy - 1f);

        float nx0 = d00 + ((d10 - d00) * sx);
        float nx1 = d01 + ((d11 - d01) * sx);
        return Math.Clamp((nx0 + ((nx1 - nx0) * sy)) * 1.41421356f, -1f, 1f);
    }

    private static float GradDot(int ix, int iy, uint seed, float dx, float dy)
    {
        uint h = HashU32(((uint)ix * 0x27D4EB2Du) ^ ((uint)iy * 0x165667B1u) ^ seed);
        float angle = Hash01(h) * MathF.Tau;
        return (MathF.Cos(angle) * dx) + (MathF.Sin(angle) * dy);
    }

    private static uint HashU32(uint x)
    {
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x;
    }

    private static float Hash01(uint h) => (h & 0x00FFFFFFu) * (1f / 16777216f);

    private static float Wrap01(float x) => x - MathF.Floor(x);

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / MathF.Max(edge1 - edge0, 1e-6f), 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
