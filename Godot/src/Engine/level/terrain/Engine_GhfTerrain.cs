using System;
using System.IO;
using System.IO.Compression;

namespace Refii.Level;

public sealed class GhfHeights
{
    public int Width { get; init; }

    public int Height { get; init; }

    public float[] Values { get; init; } = Array.Empty<float>();

    public float MinHeight { get; init; }

    public float MaxHeight { get; init; }

    public float OriginX { get; init; }

    public float OriginZ { get; init; }
}

public sealed class TerrainMesh
{
    public int Width { get; init; }

    public int Height { get; init; }

    public float[] Positions { get; init; } = Array.Empty<float>();

    public float[] Normals { get; init; } = Array.Empty<float>();

    public float[] Uvs { get; init; } = Array.Empty<float>();

    public int[] Indices { get; init; } = Array.Empty<int>();

    public float MinHeight { get; init; }

    public float MaxHeight { get; init; }

    public int VertexCount => Positions.Length / 3;

    public int TriangleCount => Indices.Length / 3;
}

public static class Engine_GhfTerrain
{
    public const int HeaderSize = 0x14;

    public const int CellStride = 14;

    public const float CellSize = 0.5f;


    public static bool TryDecode(byte[] bankBytes, out GhfHeights heights, out string error)
    {
        heights = null!;
        error = string.Empty;

        byte[] raw;
        try
        {
            raw = Gunzip(bankBytes);
        }
        catch (Exception exception)
        {
            error = $"gunzip failed: {exception.Message}";
            return false;
        }

        if (raw.Length < HeaderSize)
        {
            error = "too small to hold a .ghf header";
            return false;
        }

        float originX = ReadF32(raw, 0x00);
        float originZ = ReadF32(raw, 0x04);
        uint width = ReadU32(raw, 0x0C);
        uint height = ReadU32(raw, 0x10);

        if (width < 2 || height < 2 || width > 8192 || height > 8192)
        {
            error = $"implausible dimensions {width}x{height}";
            return false;
        }

        long cells = (long)width * height;
        long need = HeaderSize + (cells * CellStride);
        if (raw.Length < need)
        {
            error = $"truncated: {width}x{height} needs {need:N0} bytes, body has {raw.Length:N0}";
            return false;
        }

        var values = new float[cells];
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;

        for (long i = 0; i < cells; i++)
        {
            float value = ReadF32(raw, (int)(HeaderSize + (i * CellStride)));
            values[i] = value;
            if (value < min) min = value;
            if (value > max) max = value;
        }

        heights = new GhfHeights
        {
            Width = (int)width,
            Height = (int)height,
            Values = values,
            MinHeight = min,
            MaxHeight = max,
            OriginX = originX,
            OriginZ = originZ,
        };

        return true;
    }

    public static TerrainMesh BuildMesh(GhfHeights heights)
    {
        int w = heights.Width;
        int h = heights.Height;
        long vertices = (long)w * h;
        long quads = (long)(w - 1) * (h - 1);

        var positions = new float[vertices * 3];
        var normals = new float[vertices * 3];
        var uvs = new float[vertices * 2];
        var indices = new int[quads * 6];

        float[] values = heights.Values;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                long i = ((long)y * w) + x;
                float wx = heights.OriginX + (x * CellSize);
                float wz = heights.OriginZ + (y * CellSize);

                positions[(i * 3) + 0] = wx;
                positions[(i * 3) + 1] = values[i];
                positions[(i * 3) + 2] = wz;

                uvs[(i * 2) + 0] = w > 1 ? (float)x / (w - 1) : 0f;
                uvs[(i * 2) + 1] = h > 1 ? (float)y / (h - 1) : 0f;
            }
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float left = At(values, w, h, x - 1, y);
                float right = At(values, w, h, x + 1, y);
                float down = At(values, w, h, x, y - 1);
                float up = At(values, w, h, x, y + 1);

                float nx = left - right;
                float ny = 2f * CellSize;
                float nz = down - up;

                float length = MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
                if (length > 1e-6f)
                {
                    nx /= length;
                    ny /= length;
                    nz /= length;
                }
                else
                {
                    nx = 0f;
                    ny = 1f;
                    nz = 0f;
                }

                long i = ((long)y * w) + x;
                normals[(i * 3) + 0] = nx;
                normals[(i * 3) + 1] = ny;
                normals[(i * 3) + 2] = nz;
            }
        }

        long k = 0;
        for (int y = 0; y + 1 < h; y++)
        {
            for (int x = 0; x + 1 < w; x++)
            {
                int i00 = (y * w) + x;
                int i10 = (y * w) + x + 1;
                int i01 = ((y + 1) * w) + x;
                int i11 = ((y + 1) * w) + x + 1;

                indices[k++] = i00;
                indices[k++] = i10;
                indices[k++] = i01;

                indices[k++] = i10;
                indices[k++] = i11;
                indices[k++] = i01;
            }
        }

        return new TerrainMesh
        {
            Width = w,
            Height = h,
            Positions = positions,
            Normals = normals,
            Uvs = uvs,
            Indices = indices,
            MinHeight = heights.MinHeight,
            MaxHeight = heights.MaxHeight,
        };
    }

    private static float At(float[] values, int w, int h, int x, int y)
    {
        if (x < 0) x = 0;
        else if (x >= w) x = w - 1;
        if (y < 0) y = 0;
        else if (y >= h) y = h - 1;
        return values[((long)y * w) + x];
    }

    private static byte[] Gunzip(byte[] input)
    {
        if (!Refii.Core.Engine_GameGzip.IsGzip(input))
        {
            return input;
        }

        using var source = new MemoryStream(input);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static uint ReadU32(byte[] data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) |
        ((uint)data[at + 2] << 8) | data[at + 3];

    private static float ReadF32(byte[] data, int at) =>
        BitConverter.Int32BitsToSingle(unchecked((int)ReadU32(data, at)));
}
