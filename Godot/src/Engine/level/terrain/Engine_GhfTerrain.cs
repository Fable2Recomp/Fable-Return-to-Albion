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

    /// <summary>World units between adjacent heightfield samples. Half a unit.</summary>
    /// <remarks>
    /// THIS SETS THE WORLD SCALE FOR EVERYTHING, so it is worth more than a bare literal — terrain
    /// vertices are placed at `origin + index * CellSize` and every placement that lands on the
    /// ground inherits it. It also decides how the atmosphere reads: the fog curve is
    /// DISTANCE-DRIVEN over a fixed 2000-unit domain against control points the theme states in the
    /// guest's units, so a scale that is off by a factor puts every control point at the wrong
    /// distance and produces a faithful curve evaluated in the wrong space.
    ///
    /// IT IS THE FILES' OWN, AND THE TILING PROVES IT. A census of all 83 shipped heightfields
    /// (`src/tools/GhfHeaderProbe.cs`) found every dimension is `2^n + 1` and every origin a
    /// multiple of 16, and the fields ABUT:
    ///
    ///     new_heightfield_10   769 x 769   origin (0, 0)
    ///     new_heightfield_1    769 x 641   origin (0, 384)
    ///
    /// `(769 - 1) * 0.5 = 384`, exactly where the neighbour begins. At 1.0 the two would overlap
    /// by half their width; at 0.25 a 384-unit gap would open between them. The `- 1` matters and
    /// is the same fencepost the lightmap binding uses — a 577-sample field bakes to 576 texels.
    ///
    /// NOT IN THE HEADER. The .ghf header is 0x14 bytes carrying originX, originZ, width and
    /// height, and the four bytes at +0x08 that look like the obvious home for a spacing are ZERO
    /// in all 83 files — checked, because an unread field beside the origin is exactly where one
    /// would live. The spacing is a constant of the format rather than a per-file value.
    /// </remarks>
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

        // The origin lands in every vertex of the largest mesh in the region, and a non-finite
        // vertex gives the ArrayMesh a non-finite AABB — which is a non-finite render depth,
        // which hangs Godot's render-list sort in an endless fault. Refused here rather than
        // sanitised: a header we cannot read is not a heightfield we should be guessing at.
        if (!IsSane(originX) || !IsSane(originZ))
        {
            error = $"implausible origin ({originX}, {originZ})";
            return false;
        }
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

        // NOTE THE MIN/MAX TRAP BELOW: `value < min` and `value > max` are both false for NaN,
        // so a NaN never updates either and the reported range looks perfectly clean while the
        // data is not. That is why the check is explicit rather than inferred from the range.
        for (long i = 0; i < cells; i++)
        {
            float value = ReadF32(raw, (int)(HeaderSize + (i * CellStride)));
            if (!IsSane(value))
            {
                error = $"cell {i:N0} of {cells:N0} is {value}";
                return false;
            }

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

                // Clockwise seen from above, under the region's mirrored root: Godot's front face.
                // Wound the other way the ground drew as back faces (facing view, 2026-09-22).
                indices[k++] = i00;
                indices[k++] = i01;
                indices[k++] = i10;

                indices[k++] = i10;
                indices[k++] = i01;
                indices[k++] = i11;
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

    /// <summary>
    /// Finite AND bounded, the same predicate the .ehf decoder already uses.
    /// </summary>
    /// <remarks>
    /// The bound is not decoration: a render depth is a squared distance, so a merely enormous
    /// height overflows to +inf when squared and becomes NaN one subtraction later, with every
    /// component still passing a plain finiteness test.
    /// </remarks>
    private static bool IsSane(float v) => float.IsFinite(v) && MathF.Abs(v) < 1_000_000f;

    private static float ReadF32(byte[] data, int at) =>
        BitConverter.Int32BitsToSingle(unchecked((int)ReadU32(data, at)));
}
