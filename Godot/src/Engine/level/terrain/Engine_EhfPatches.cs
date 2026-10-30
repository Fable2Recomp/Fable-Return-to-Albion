using System;
using System.Collections.Generic;
using Refii.Core;

namespace Refii.Level;

public sealed class EhfVistaPatch
{
    public int CellsX { get; init; }

    public int CellsY { get; init; }

    public float[] AabbMin { get; init; } = new float[3];

    public float[] AabbMax { get; init; } = new float[3];

    public TerrainMesh Mesh { get; init; } = null!;
}

public static class Engine_EhfPatches
{
    private const string Magic = "HeightFieldGraphicsFile";
    private const int HeaderLength = 63;

    private const int CellStride = 160;

    private const int LatticeOffset = 64 + 12;

    private const int LatticeDiagonalOffset = 64 + 48;

    public static bool TryBuild(byte[] ehf, out List<EhfVistaPatch> patches, out string error) =>
        TryBuild(ehf, Engine_Title.Active, out patches, out error);

    public static bool TryBuild(
        byte[] ehf, Engine_Title title, out List<EhfVistaPatch> patches, out string error)
    {
        patches = new List<EhfVistaPatch>();
        error = string.Empty;

        if (ehf is null || ehf.Length < HeaderLength || !HasMagic(ehf))
        {
            error = "not a HeightFieldGraphicsFile";
            return false;
        }

        long bodyOffset = ReadU32(ehf, 55);
        long bodySize = ReadU32(ehf, 59);
        if (bodyOffset + bodySize > ehf.Length)
        {
            error = "body extent past end of file";
            return false;
        }

        int cursor = (int)bodyOffset;
        long bodyEnd = bodyOffset + bodySize;

        if (!TrySkipTexBlob(ehf, ref cursor, bodyEnd, title) ||
            !TrySkipTexBlob(ehf, ref cursor, bodyEnd, title))
        {
            error = "could not skip the leading texture blobs";
            return false;
        }

        if (cursor + 8 > bodyEnd)
        {
            error = "truncated before the patch block";
            return false;
        }

        cursor += 4;
        uint patchCount = ReadU32(ehf, cursor);
        cursor += 4;

        if (patchCount == 0 || patchCount > 4096)
        {
            error = $"implausible patch count {patchCount}";
            return false;
        }

        var headers = new List<(int CellsX, int CellsY, float[] Min, float[] Max)>((int)patchCount);
        var lattice = new Dictionary<long, float>();
        var latticeDiagonal = new Dictionary<long, float>();

        for (uint p = 0; p < patchCount; p++)
        {
            if (cursor + 16 > bodyEnd)
            {
                error = $"truncated reading patch {p} header";
                return false;
            }

            cursor += 8;
            uint cellsX = ReadU32(ehf, cursor);
            cursor += 4;
            uint cellsY = ReadU32(ehf, cursor);
            cursor += 4;

            if (cellsX == 0 || cellsY == 0 || cellsX > 1024 || cellsY > 1024)
            {
                error = $"patch {p} has implausible dimensions {cellsX}x{cellsY}";
                return false;
            }

            long cells = (long)cellsX * cellsY;
            long cellBase = cursor;
            long aabbAt = cellBase + (cells * CellStride);

            if (cells > 65536 || aabbAt + 24 > bodyEnd)
            {
                error = $"patch {p} overruns the body";
                return false;
            }

            var min = new float[3];
            var max = new float[3];
            for (int k = 0; k < 3; k++)
            {
                min[k] = ReadF32(ehf, (int)(aabbAt + (k * 4)));
                max[k] = ReadF32(ehf, (int)(aabbAt + 12 + (k * 4)));
            }

            for (long c = 0; c < cells; c++)
            {
                long record = cellBase + (c * CellStride);

                float bx = ReadF32(ehf, (int)(record + LatticeOffset));
                float by = ReadF32(ehf, (int)(record + LatticeOffset + 4));
                float bh = ReadF32(ehf, (int)(record + LatticeOffset + 8));
                if (IsSane(bx) && IsSane(by) && IsSane(bh))
                {
                    lattice[Key(bx, by)] = bh;
                }

                float dx = ReadF32(ehf, (int)(record + LatticeDiagonalOffset));
                float dy = ReadF32(ehf, (int)(record + LatticeDiagonalOffset + 4));
                float dh = ReadF32(ehf, (int)(record + LatticeDiagonalOffset + 8));
                if (IsSane(dx) && IsSane(dy) && IsSane(dh))
                {
                    latticeDiagonal.TryAdd(Key(dx, dy), dh);
                }
            }

            headers.Add(((int)cellsX, (int)cellsY, min, max));
            cursor = (int)(aabbAt + 24);
        }

        if (lattice.Count == 0)
        {
            error = "no usable lattice points";
            return false;
        }

        foreach ((int cellsX, int cellsY, float[] min, float[] max) in headers)
        {
            EhfVistaPatch? patch = BuildPatch(cellsX, cellsY, min, max, lattice, latticeDiagonal);
            if (patch is not null)
            {
                patches.Add(patch);
            }
        }

        if (patches.Count == 0)
        {
            error = "no patch produced geometry";
            return false;
        }

        return true;
    }

    private static EhfVistaPatch? BuildPatch(
        int cellsX, int cellsY, float[] min, float[] max,
        Dictionary<long, float> lattice, Dictionary<long, float> latticeDiagonal)
    {
        float originX = min[0];
        float originY = min[1];
        float stepX = (max[0] - min[0]) / cellsX;
        float stepY = (max[1] - min[1]) / cellsY;

        if (!(stepX > 0f) || !(stepY > 0f) ||
            float.IsNaN(stepX) || float.IsInfinity(stepX) ||
            float.IsNaN(stepY) || float.IsInfinity(stepY))
        {
            return null;
        }

        int vw = cellsX + 1;
        int vh = cellsY + 1;
        var heights = new float[vw * vh];
        var known = new bool[vw * vh];

        for (int j = 0; j < vh; j++)
        {
            for (int i = 0; i < vw; i++)
            {
                long key = Key(originX + (stepX * i), originY + (stepY * j));
                if (lattice.TryGetValue(key, out float h) ||
                    latticeDiagonal.TryGetValue(key, out h))
                {
                    heights[(j * vw) + i] = h;
                    known[(j * vw) + i] = true;
                }
            }
        }

        var sourceIndices = new List<int>();
        for (int i = 0; i < known.Length; i++)
        {
            if (known[i])
            {
                sourceIndices.Add(i);
            }
        }

        if (sourceIndices.Count == 0)
        {
            return null;
        }

        for (int j = 0; j < vh; j++)
        {
            for (int i = 0; i < vw; i++)
            {
                int target = (j * vw) + i;
                if (known[target])
                {
                    continue;
                }

                float best = min[2];
                int bestDistance = int.MaxValue;
                foreach (int source in sourceIndices)
                {
                    int si = source % vw;
                    int sj = source / vw;
                    int distance = Math.Abs(si - i) + Math.Abs(sj - j);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = heights[source];
                    }
                }

                heights[target] = best;
            }
        }

        var positions = new float[vw * vh * 3];
        var normals = new float[vw * vh * 3];
        var uvs = new float[vw * vh * 2];
        var indices = new int[cellsX * cellsY * 6];

        float width = max[0] - min[0];
        float depth = max[1] - min[1];
        float invWidth = width > 0f ? 1f / width : 0f;
        float invDepth = depth > 0f ? 1f / depth : 0f;

        float lowest = float.PositiveInfinity;
        float highest = float.NegativeInfinity;

        for (int j = 0; j < vh; j++)
        {
            for (int i = 0; i < vw; i++)
            {
                int v = (j * vw) + i;
                float wx = originX + (stepX * i);
                float wz = originY + (stepY * j);
                float wh = heights[v];

                positions[(v * 3) + 0] = wx;
                positions[(v * 3) + 1] = wh;
                positions[(v * 3) + 2] = wz;

                uvs[(v * 2) + 0] = (wx - originX) * invWidth;
                uvs[(v * 2) + 1] = (wz - originY) * invDepth;

                if (wh < lowest) lowest = wh;
                if (wh > highest) highest = wh;

                float left = HeightAt(heights, vw, vh, i - 1, j);
                float right = HeightAt(heights, vw, vh, i + 1, j);
                float down = HeightAt(heights, vw, vh, i, j - 1);
                float up = HeightAt(heights, vw, vh, i, j + 1);

                float nx = (left - right) * stepY;
                float ny = 2f * stepX * stepY;
                float nz = (down - up) * stepX;
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

                normals[(v * 3) + 0] = nx;
                normals[(v * 3) + 1] = ny;
                normals[(v * 3) + 2] = nz;
            }
        }

        int k2 = 0;
        for (int j = 0; j + 1 < vh; j++)
        {
            for (int i = 0; i + 1 < vw; i++)
            {
                int i00 = (j * vw) + i;
                int i10 = i00 + 1;
                int i01 = ((j + 1) * vw) + i;
                int i11 = i01 + 1;

                indices[k2++] = i00;
                indices[k2++] = i10;
                indices[k2++] = i01;

                indices[k2++] = i10;
                indices[k2++] = i11;
                indices[k2++] = i01;
            }
        }

        return new EhfVistaPatch
        {
            CellsX = cellsX,
            CellsY = cellsY,
            AabbMin = min,
            AabbMax = max,
            Mesh = new TerrainMesh
            {
                Width = vw,
                Height = vh,
                Positions = positions,
                Normals = normals,
                Uvs = uvs,
                Indices = indices,
                MinHeight = float.IsFinite(lowest) ? lowest : 0f,
                MaxHeight = float.IsFinite(highest) ? highest : 0f,
            },
        };
    }

    private static float HeightAt(float[] heights, int vw, int vh, int i, int j)
    {
        if (i < 0) i = 0;
        else if (i >= vw) i = vw - 1;
        if (j < 0) j = 0;
        else if (j >= vh) j = vh - 1;
        return heights[(j * vw) + i];
    }

    private static long Key(float x, float y)
    {
        int qx = (int)Math.Round(x * 4.0);
        int qy = (int)Math.Round(y * 4.0);
        return ((long)(uint)qx << 32) | (uint)qy;
    }

    private static bool IsSane(float v) => float.IsFinite(v) && MathF.Abs(v) < 1_000_000f;

    private static bool TrySkipTexBlob(byte[] data, ref int cursor, long bodyEnd, Engine_Title title)
    {
        if (title == Engine_Title.Fable3)
        {
            if (!Engine_EhfBody.TryReadFable3Blob(data, cursor, bodyEnd, out EhfTexBlob blob, out _))
            {
                return false;
            }

            cursor = (int)blob.End;
            return true;
        }

        long start = cursor;
        if (start + 0x60 > bodyEnd || ReadU32(data, (int)start) != 0xFFFFFFFEu)
        {
            return false;
        }

        uint pixelFormat = ReadU32(data, (int)start + 0x18);
        uint metaSize = ReadU32(data, (int)start + 0x20);
        if (metaSize > 0x100)
        {
            return false;
        }

        long next;
        if (pixelFormat == 98u)
        {
            uint w = ReadU32(data, (int)start + 0x10);
            uint h = ReadU32(data, (int)start + 0x14);
            next = start + metaSize + ((long)w * h * 2);
        }
        else
        {
            if (start + metaSize + 8 > bodyEnd)
            {
                return false;
            }

            uint compressed = ReadU32(data, (int)(start + metaSize + 4));
            next = start + metaSize + 8 + compressed;
        }

        if (next > bodyEnd || next <= start)
        {
            return false;
        }

        cursor = (int)next;
        return true;
    }

    private static bool HasMagic(byte[] data)
    {
        for (int i = 0; i < Magic.Length; i++)
        {
            if (data[i] != Magic[i])
            {
                return false;
            }
        }

        return true;
    }

    private static uint ReadU32(byte[] data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) |
        ((uint)data[at + 2] << 8) | data[at + 3];

    private static float ReadF32(byte[] data, int at) =>
        BitConverter.Int32BitsToSingle(unchecked((int)ReadU32(data, at)));
}
