using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Refii.Level;

public sealed class SplatMaterial
{
    public byte[] BaseRgba { get; init; } = Array.Empty<byte>();

    public int BaseWidth { get; init; }

    public int BaseHeight { get; init; }

    public byte[] DetailRgba { get; init; } = Array.Empty<byte>();

    public int DetailWidth { get; init; }

    public int DetailHeight { get; init; }

    /// <summary>
    /// The layer's normal maps, one per colour map.
    /// </summary>
    /// <remarks>
    /// A landscape material is six texture names and four floats, and the second and fifth
    /// names are normal maps — the .ehf record has always carried them and
    /// <see cref="EhfLodEntry.BaseNormal"/> has always parsed them; nothing loaded them. The
    /// guest binds them at samplers s2 and s14 beside their colour partners at s0 and s13, and
    /// samples both with the SAME UV, so a layer's normal needs no addressing of its own.
    /// </remarks>
    public byte[] BaseNormalRgba { get; init; } = Array.Empty<byte>();

    public int BaseNormalWidth { get; init; }

    public int BaseNormalHeight { get; init; }

    public byte[] DetailNormalRgba { get; init; } = Array.Empty<byte>();

    public int DetailNormalWidth { get; init; }

    public int DetailNormalHeight { get; init; }

    public float BaseTileScale { get; init; }

    public float DetailTileScale { get; init; }

    public float DetailIntensity { get; init; } = 1f;

    public bool HasBase => BaseRgba.Length != 0 && BaseWidth > 0 && BaseHeight > 0;

    public bool HasDetail => DetailRgba.Length != 0 && DetailWidth > 0 && DetailHeight > 0;
}

public sealed class TerrainSplatData
{
    public int ChunkWidth { get; init; }

    public int ChunkHeight { get; init; }

    public float WorldOriginX { get; init; }

    public float WorldOriginZ { get; init; }

    public float ChunkExtentX { get; init; }

    public float ChunkExtentZ { get; init; }

    public int MaterialCount { get; init; }

    public float TileScale { get; init; }

    public float[] MaterialParams { get; init; } = Array.Empty<float>();

    public int WeightWidth { get; init; }

    public int WeightHeight { get; init; }

    public float[] Weights { get; init; } = Array.Empty<float>();

    public int SliceSize { get; init; }

    public byte[][] BaseSlices { get; init; } = Array.Empty<byte[]>();

    public byte[][] DetailSlices { get; init; } = Array.Empty<byte[]>();

    public byte[][] BaseNormalSlices { get; init; } = Array.Empty<byte[]>();

    public byte[][] DetailNormalSlices { get; init; } = Array.Empty<byte[]>();

    public int SeededBase { get; init; }

    public int SeededDetail { get; init; }

    public long PaintedTexels { get; init; }

    public long FallbackTexels { get; init; }

    public long Contributions { get; init; }
}

public static class Engine_EhfSplat
{
    public const int CommonLodSize = 1024;

    /// <summary>
    /// One normal-map slice per material, flat where a layer names none.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT sharing <c>BuildSlices</c>' fallback behaviour. A colour layer with no
    /// texture borrows another layer's so the ground is not black; a normal layer with none is
    /// FLAT, and borrowing a neighbour's would emboss one material's bumps onto a different
    /// material's paint. Flat is also the value that makes the guest's darkening term collapse
    /// to exactly 1, which is what it should do for a layer that has no normal to deviate by.
    /// </remarks>
    private static byte[][] BuildNormalSlices(IReadOnlyList<SplatMaterial> materials, bool detail)
    {
        var slices = new byte[Math.Min(materials.Count, MaxMaterials)][];
        byte[]? flat = null;

        for (int s = 0; s < slices.Length; s++)
        {
            SplatMaterial material = materials[s];
            byte[] source = detail ? material.DetailNormalRgba : material.BaseNormalRgba;
            int width = detail ? material.DetailNormalWidth : material.BaseNormalWidth;
            int height = detail ? material.DetailNormalHeight : material.BaseNormalHeight;

            if (source.Length != 0 && width > 0 && height > 0)
            {
                slices[s] = ResizeRgba8(source, width, height, CommonLodSize, CommonLodSize);
                continue;
            }

            flat ??= FlatNormal();
            slices[s] = flat;
        }

        return slices;
    }

    /// <summary>A tangent-space normal of (0, 0, 1), encoded.</summary>
    private static byte[] FlatNormal()
    {
        var flat = new byte[CommonLodSize * CommonLodSize * 4];
        for (int i = 0; i < flat.Length; i += 4)
        {
            flat[i] = 128;
            flat[i + 1] = 128;
            flat[i + 2] = 255;
            flat[i + 3] = 255;
        }

        return flat;
    }

    public const int MaxMaterials = 32;

    public const int MaxLayers = 16;

    private const int WeightPerChunk = 32;

    private const float DefaultTileScale = 0.125f;

    public static bool TryBuild(
        EhfParsedBody parsed,
        IReadOnlyList<SplatMaterial> materials,
        out TerrainSplatData data,
        out string error)
    {
        data = null!;
        error = string.Empty;

        if (parsed.Chunks.Count == 0 || materials.Count == 0)
        {
            error = "no chunks or no materials";
            return false;
        }

        int materialCount = materials.Count;
        var materialParams = new float[MaxMaterials * 4];
        float tileScale = DefaultTileScale;

        for (int i = 0; i < MaxMaterials; i++)
        {
            materialParams[(i * 4) + 0] = DefaultTileScale;
            materialParams[(i * 4) + 1] = DefaultTileScale;
            materialParams[(i * 4) + 2] = 0f;
            materialParams[(i * 4) + 3] = 1f;
        }

        for (int i = 0; i < materialCount && i < MaxMaterials; i++)
        {
            SplatMaterial material = materials[i];

            float baseScale = material.BaseTileScale is > 0f and < 16f
                ? material.BaseTileScale
                : DefaultTileScale;
            float detailScale = material.DetailTileScale is > 0f and < 16f
                ? material.DetailTileScale
                : baseScale;

            materialParams[(i * 4) + 0] = baseScale;
            materialParams[(i * 4) + 1] = detailScale;
            materialParams[(i * 4) + 2] = material.HasDetail ? 1f : 0f;
            materialParams[(i * 4) + 3] = material.DetailIntensity is > 0f and < 16f
                ? material.DetailIntensity
                : 1f;

            if (i == 0)
            {
                tileScale = baseScale;
            }
        }

        float worldMinX = parsed.Chunks[0].Origin[0];
        float worldMinZ = parsed.Chunks[0].Origin[1];
        float worldMaxX = parsed.Chunks[0].Extent[0];
        float worldMaxZ = parsed.Chunks[0].Extent[1];

        foreach (EhfChunk chunk in parsed.Chunks)
        {
            worldMinX = MathF.Min(worldMinX, chunk.Origin[0]);
            worldMinZ = MathF.Min(worldMinZ, chunk.Origin[1]);
            worldMaxX = MathF.Max(worldMaxX, chunk.Extent[0]);
            worldMaxZ = MathF.Max(worldMaxZ, chunk.Extent[1]);
        }

        byte[][] baseSlices = BuildSlices(materials, detail: false, out int seededBase);
        if (seededBase == 0)
        {
            error = "no base diffuse slices seeded";
            return false;
        }

        byte[][] detailSlices = BuildSlices(materials, detail: true, out int seededDetail);
        if (seededDetail == 0)
        {
            error = "no detail diffuse slices seeded";
            return false;
        }

        if (parsed.ChunkWidth == 0 || parsed.ChunkHeight == 0)
        {
            error = "empty EHF chunk grid";
            return false;
        }

        int chunkWidth = (int)parsed.ChunkWidth;
        int chunkHeight = (int)parsed.ChunkHeight;

        bool hasSplat = parsed.SplatIndices.Length != 0 &&
                        parsed.SplatWidth > 0 && parsed.SplatHeight > 0 &&
                        parsed.SplatIndices.Length ==
                            (long)parsed.SplatWidth * parsed.SplatHeight;
        if (!hasSplat)
        {
            error = "cannot build material weights without a pf99 mask";
            return false;
        }

        int planes = Math.Min(Math.Max(0, materialCount), MaxMaterials);
        if (planes == 0)
        {
            error = "no material planes";
            return false;
        }

        int weightWidth = (chunkWidth * WeightPerChunk) + 1;
        int weightHeight = (chunkHeight * WeightPerChunk) + 1;
        float[] weights = BakeWeights(
            parsed, chunkWidth, chunkHeight, planes, weightWidth, weightHeight,
            out long painted, out long fallback, out long contributions);

        data = new TerrainSplatData
        {
            ChunkWidth = chunkWidth,
            ChunkHeight = chunkHeight,
            WorldOriginX = worldMinX,
            WorldOriginZ = worldMinZ,
            ChunkExtentX = (worldMaxX - worldMinX) / Math.Max(1, chunkWidth),
            ChunkExtentZ = (worldMaxZ - worldMinZ) / Math.Max(1, chunkHeight),
            MaterialCount = materialCount,
            TileScale = tileScale,
            MaterialParams = materialParams,
            WeightWidth = weightWidth,
            WeightHeight = weightHeight,
            Weights = weights,
            SliceSize = CommonLodSize,
            BaseSlices = baseSlices,
            DetailSlices = detailSlices,
            BaseNormalSlices = BuildNormalSlices(materials, detail: false),
            DetailNormalSlices = BuildNormalSlices(materials, detail: true),
            SeededBase = seededBase,
            SeededDetail = seededDetail,
            PaintedTexels = painted,
            FallbackTexels = fallback,
            Contributions = contributions,
        };

        return true;
    }

    private static float[] BakeWeights(
        EhfParsedBody parsed,
        int chunkWidth,
        int chunkHeight,
        int planes,
        int weightWidth,
        int weightHeight,
        out long painted,
        out long fallback,
        out long contributions)
    {
        painted = 0;
        fallback = 0;
        contributions = 0;

        long area = (long)weightWidth * weightHeight;
        var weights = new float[planes * area];

        long expected = (long)chunkWidth * chunkHeight;
        int chunkCount = (int)Math.Min(parsed.Chunks.Count, expected);

        for (int ci = 0; ci < chunkCount; ci++)
        {
            int cx = ci / chunkHeight;
            int cy = ci % chunkHeight;
            EhfChunk chunk = parsed.Chunks[ci];

            int layerCount = Math.Min(chunk.Layers.Count, MaxLayers);
            for (int li = 0; li < layerCount; li++)
            {
                EhfChunkLayer layer = chunk.Layers[li];
                int material = Math.Clamp((int)layer.MaterialIndex, 0, planes - 1);

                for (int py = 0; py <= WeightPerChunk; py++)
                {
                    for (int px = 0; px <= WeightPerChunk; px++)
                    {
                        float coverage = SampleMask(parsed, layer, px, py);
                        if (coverage <= 0.0001f)
                        {
                            continue;
                        }

                        int gx = (cx * WeightPerChunk) + px;
                        int gy = (cy * WeightPerChunk) + py;
                        if (gx < 0 || gy < 0 || gx >= weightWidth || gy >= weightHeight)
                        {
                            continue;
                        }

                        weights[(material * area) + (gy * (long)weightWidth) + gx] += coverage;
                        contributions++;
                    }
                }
            }
        }

        for (long p = 0; p < area; p++)
        {
            float sum = 0f;
            for (int m = 0; m < planes; m++)
            {
                sum += weights[(m * area) + p];
            }

            if (sum > 0.00001f)
            {
                float inverse = 1f / sum;
                for (int m = 0; m < planes; m++)
                {
                    weights[(m * area) + p] *= inverse;
                }

                painted++;
            }
            else
            {
                weights[p] = 1f;
                fallback++;
            }
        }

        return weights;
    }

    private static float SampleMask(EhfParsedBody parsed, EhfChunkLayer layer, int px, int py)
    {
        byte[] map = parsed.SplatIndices;
        int width = (int)parsed.SplatWidth;
        int height = (int)parsed.SplatHeight;

        if (layer.NameIndex < parsed.PaintResources.Count)
        {
            EhfPaintResource resource = parsed.PaintResources[(int)layer.NameIndex];
            if (resource.Data.Length != 0 && resource.Width > 0 && resource.Height > 0)
            {
                map = resource.Data;
                width = (int)resource.Width;
                height = (int)resource.Height;
            }
        }

        if (map.Length == 0 || width <= 0 || height <= 0 ||
            map.Length != (long)width * height)
        {
            return 1f;
        }

        int originX = Math.Clamp((int)MathF.Floor(layer.TileU * width), 0, width - 1);
        int originY = Math.Clamp((int)MathF.Floor(layer.TileV * height), 0, height - 1);
        int sx = Math.Clamp(originX + px, 0, width - 1);
        int sy = Math.Clamp(originY + py, 0, height - 1);

        return map[(sy * (long)width) + sx] / 255f;
    }

    private static byte[][] BuildSlices(
        IReadOnlyList<SplatMaterial> materials, bool detail, out int seeded)
    {
        seeded = 0;

        int count = materials.Count;
        var slices = new byte[count][];
        byte[]? neutral = null;
        byte[]? fallback = null;

        for (int s = 0; s < count; s++)
        {
            SplatMaterial material = materials[s];

            byte[] source;
            int sourceWidth;
            int sourceHeight;

            if (detail)
            {
                source = material.DetailRgba;
                sourceWidth = material.DetailWidth;
                sourceHeight = material.DetailHeight;
            }
            else if (material.HasBase)
            {
                source = material.BaseRgba;
                sourceWidth = material.BaseWidth;
                sourceHeight = material.BaseHeight;
            }
            else
            {
                source = material.DetailRgba;
                sourceWidth = material.DetailWidth;
                sourceHeight = material.DetailHeight;
            }

            if (source.Length != 0 && sourceWidth > 0 && sourceHeight > 0)
            {
                byte[] resized = ResizeRgba8(
                    source, sourceWidth, sourceHeight, CommonLodSize, CommonLodSize);
                slices[s] = resized;
                fallback ??= detail ? null : resized;
                seeded++;
                continue;
            }

            if (detail)
            {
                neutral ??= NeutralDetail();
                slices[s] = neutral;
                seeded++;
                continue;
            }

            if (fallback is not null)
            {
                slices[s] = fallback;
                seeded++;
            }
        }

        byte[]? empty = null;
        for (int s = 0; s < count; s++)
        {
            if (slices[s] is null)
            {
                empty ??= OpaqueBlack();
                slices[s] = empty;
            }
        }

        return slices;
    }

    private static byte[] NeutralDetail()
    {
        var neutral = new byte[CommonLodSize * CommonLodSize * 4];
        for (int i = 0; i < neutral.Length; i += 4)
        {
            neutral[i + 0] = 0x80;
            neutral[i + 1] = 0x80;
            neutral[i + 2] = 0x80;
            neutral[i + 3] = 0xFF;
        }

        return neutral;
    }

    private static byte[] OpaqueBlack()
    {
        var black = new byte[CommonLodSize * CommonLodSize * 4];
        for (int i = 3; i < black.Length; i += 4)
        {
            black[i] = 0xFF;
        }

        return black;
    }

    public static byte[] ResizeRgba8(
        byte[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        var destination = new byte[(long)width * height * 4];
        if (sourceWidth <= 0 || sourceHeight <= 0 || width <= 0 || height <= 0)
        {
            return destination;
        }

        if (source.LongLength < (long)sourceWidth * sourceHeight * 4)
        {
            return destination;
        }

        float scaleX = (float)sourceWidth / width;
        float scaleY = (float)sourceHeight / height;

        for (int y = 0; y < height; y++)
        {
            float sy = ((y + 0.5f) * scaleY) - 0.5f;
            int sy0 = Math.Clamp((int)MathF.Floor(sy), 0, sourceHeight - 1);
            int sy1 = Math.Min(sy0 + 1, sourceHeight - 1);
            float fy = Math.Clamp(sy - sy0, 0f, 1f);

            for (int x = 0; x < width; x++)
            {
                float sx = ((x + 0.5f) * scaleX) - 0.5f;
                int sx0 = Math.Clamp((int)MathF.Floor(sx), 0, sourceWidth - 1);
                int sx1 = Math.Min(sx0 + 1, sourceWidth - 1);
                float fx = Math.Clamp(sx - sx0, 0f, 1f);

                long p00 = (((long)sy0 * sourceWidth) + sx0) * 4;
                long p10 = (((long)sy0 * sourceWidth) + sx1) * 4;
                long p01 = (((long)sy1 * sourceWidth) + sx0) * 4;
                long p11 = (((long)sy1 * sourceWidth) + sx1) * 4;
                long at = (((long)y * width) + x) * 4;

                float w00 = (1f - fx) * (1f - fy);
                float w10 = fx * (1f - fy);
                float w01 = (1f - fx) * fy;
                float w11 = fx * fy;

                for (int c = 0; c < 4; c++)
                {
                    destination[at + c] = (byte)Math.Clamp(
                        (source[p00 + c] * w00) + (source[p10 + c] * w10) +
                        (source[p01 + c] * w01) + (source[p11 + c] * w11),
                        0f, 255f);
                }
            }
        }

        return destination;
    }

    public static bool TryDecodePf99SplatMap(
        byte[] data,
        int offset,
        int available,
        out byte[] indices,
        out int width,
        out int height,
        out string error)
    {
        indices = Array.Empty<byte>();
        width = 0;
        height = 0;
        error = string.Empty;

        if (offset < 0 || available < 0x60 || offset + 0x60 > data.Length)
        {
            error = "splat: blob too small";
            return false;
        }

        if (ReadU32(data, offset) != 0xFFFFFFFEu)
        {
            error = "splat: bad magic";
            return false;
        }

        uint w = ReadU32(data, offset + 0x10);
        uint h = ReadU32(data, offset + 0x14);
        uint pixelFormat = ReadU32(data, offset + 0x18);
        uint metaSize = ReadU32(data, offset + 0x20);

        if (pixelFormat != 99u)
        {
            error = $"splat: PixelFormat is {pixelFormat}, expected 99";
            return false;
        }

        if (w == 0 || h == 0 || w > 16384 || h > 16384)
        {
            error = "splat: bad dimensions";
            return false;
        }

        long tableAt = offset + metaSize;
        if (tableAt + 8 > data.Length)
        {
            error = "splat: mip table read failed";
            return false;
        }

        uint rawSize = ReadU32(data, (int)tableAt);
        long zlibAt = tableAt + 8;
        if (zlibAt + 2 > data.Length || data[zlibAt] != 0x78)
        {
            error = "splat: no zlib magic at mip data offset";
            return false;
        }

        uint compressedSize = ReadU32(data, (int)tableAt + 4);
        long availableCompressed = data.Length - zlibAt;
        int compressed = (int)Math.Min(compressedSize, availableCompressed);

        byte[] raw;
        try
        {
            using var source = new MemoryStream(data, (int)zlibAt, compressed);
            using var inflate = new ZLibStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream((int)rawSize);
            inflate.CopyTo(output);
            raw = output.ToArray();
        }
        catch (Exception exception)
        {
            error = $"splat: zlib inflate failed: {exception.Message}";
            return false;
        }

        uint blocksWide = (w + 3u) / 4u;
        uint blocksHigh = (h + 3u) / 4u;
        uint paddedWide = (blocksWide + 31u) & ~31u;
        uint paddedHigh = (blocksHigh + 31u) & ~31u;

        long expected = (long)paddedWide * paddedHigh * 8;
        if (raw.Length < expected)
        {
            error = $"splat: raw page too small: {raw.Length} < {expected}";
            return false;
        }

        var linear = new byte[(long)blocksWide * blocksHigh * 8];
        uint totalBlocks = paddedWide * paddedHigh;

        for (uint off = 0; off < totalBlocks; off++)
        {
            uint bx = Engine_EhfAtlas.TiledX(off, paddedWide, 8);
            uint by = Engine_EhfAtlas.TiledY(off, paddedWide, 8);
            if (bx >= blocksWide || by >= blocksHigh)
            {
                continue;
            }

            long from = (long)off * 8;
            if (from + 8 > raw.Length)
            {
                continue;
            }

            Array.Copy(raw, from, linear, ((by * (long)blocksWide) + bx) * 8, 8);
        }

        for (int i = 0; i + 2 <= linear.Length; i += 2)
        {
            (linear[i], linear[i + 1]) = (linear[i + 1], linear[i]);
        }

        indices = new byte[(long)w * h];
        for (uint by = 0; by < blocksHigh; by++)
        {
            for (uint bx = 0; bx < blocksWide; bx++)
            {
                long block = ((by * (long)blocksWide) + bx) * 8;

                for (int py = 0; py < 4; py++)
                {
                    uint yy = (by * 4u) + (uint)py;
                    if (yy >= h)
                    {
                        break;
                    }

                    ushort row = (ushort)(linear[block + (py * 2)] |
                                          (linear[block + (py * 2) + 1] << 8));

                    for (int px = 0; px < 4; px++)
                    {
                        uint xx = (bx * 4u) + (uint)px;
                        if (xx >= w)
                        {
                            continue;
                        }

                        indices[(yy * (long)w) + xx] = (byte)(((row >> (4 * px)) & 0xF) * 17);
                    }
                }
            }
        }

        width = (int)w;
        height = (int)h;
        return true;
    }

    private static uint ReadU32(byte[] data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) |
        ((uint)data[at + 2] << 8) | data[at + 3];
}
