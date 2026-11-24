using System;
using System.Collections.Generic;

namespace Refii.Level;

public static class Engine_WaterMesh
{
    private const long MaxVerticesPerTile = 1200000;

    /// <summary>
    /// Border of inactive cells kept around active water cells in Modern mode so the 3-pass
    /// Gaussian shoreline mask (`Engine_WaterMask`) fades cleanly to zero instead of clipping on grid squares.
    /// </summary>
    public const int ApronCells = 3;

    /// <summary>
    /// Ring of cells kept at full 1x1 cell resolution near shorelines and tile edges so curves,
    /// coastal surf vertex displacement, and vertex shore probes preserve native resolution.
    /// </summary>
    public const int FineShoreCells = 6;

    /// <summary>Maximum world span (metres) and cell count for a greedily merged flat interior quad.</summary>
    public const float MaxMergedSpanMetres = 4.5f;
    public const int MaxMergedCells = 8;

    /// <summary>
    /// One mesh for a body's tiles. <paramref name="fullPatches"/> (the classic renderer) meshes
    /// each tile as the title does (`sub_82B3A960`): its whole rectangle on a grid of
    /// max(2, min(cells, extent * 0.2)) vertices per axis, every quad kept, the shore left to the
    /// mask's fade; otherwise Modern mode keeps a 3-cell shoreline apron, merges flat deep interior
    /// quads greedily, and emits only referenced vertices.
    /// </summary>
    public static TerrainMesh? BuildBody(WaterBody body, bool fullPatches = false)
    {
        var positions = new List<float>();
        var normals = new List<float>();
        var uvs = new List<float>();
        var indices = new List<int>();

        float height = float.IsFinite(body.BaseHeight) ? body.BaseHeight : 0f;

        foreach (WaterTile tile in body.Tiles)
        {
            int cellsX = Math.Max(tile.CellsX, 1);
            int cellsZ = Math.Max(tile.CellsZ, 1);
            if (fullPatches)
            {
                cellsX = Math.Max(2, Math.Min(cellsX, (int)(Math.Abs(tile.ExtentX) * 0.2f))) - 1;
                cellsZ = Math.Max(2, Math.Min(cellsZ, (int)(Math.Abs(tile.ExtentZ) * 0.2f))) - 1;
            }
            long vertexCount = (long)(cellsX + 1) * (cellsZ + 1);
            if (vertexCount > MaxVerticesPerTile)
            {
                continue;
            }

            float halfX = Math.Abs(tile.ExtentX) * 0.5f;
            float halfZ = Math.Abs(tile.ExtentZ) * 0.5f;
            float x0 = tile.CentreX - halfX;
            float x1 = tile.CentreX + halfX;
            float z0 = tile.CentreZ - halfZ;
            float z1 = tile.CentreZ + halfZ;

            if (fullPatches)
            {
                BuildFullPatchesTile(
                    cellsX, cellsZ, height, x0, x1, z0, z1,
                    positions, normals, uvs, indices);
            }
            else
            {
                BuildModernMergedTile(
                    tile, cellsX, cellsZ, height, x0, x1, z0, z1,
                    positions, normals, uvs, indices);
            }
        }

        if (indices.Count == 0)
        {
            return null;
        }

        return new TerrainMesh
        {
            Width = 0,
            Height = 0,
            Positions = positions.ToArray(),
            Normals = normals.ToArray(),
            Uvs = uvs.ToArray(),
            Indices = indices.ToArray(),
            MinHeight = height,
            MaxHeight = height,
        };
    }

    private static void BuildFullPatchesTile(
        int cellsX, int cellsZ, float height,
        float x0, float x1, float z0, float z1,
        List<float> positions, List<float> normals, List<float> uvs, List<int> indices)
    {
        int baseVertex = positions.Count / 3;

        for (int mz = 0; mz <= cellsZ; mz++)
        {
            float vz = (float)mz / cellsZ;
            float pz = z0 + ((z1 - z0) * vz);

            for (int mx = 0; mx <= cellsX; mx++)
            {
                float vx = (float)mx / cellsX;
                float px = x0 + ((x1 - x0) * vx);

                positions.Add(px);
                positions.Add(height);
                positions.Add(pz);

                normals.Add(0f);
                normals.Add(1f);
                normals.Add(0f);

                uvs.Add(px);
                uvs.Add(pz);
            }
        }

        int rowStride = cellsX + 1;
        for (int mz = 0; mz < cellsZ; mz++)
        {
            for (int mx = 0; mx < cellsX; mx++)
            {
                int v00 = baseVertex + (mz * rowStride) + mx;
                int v10 = v00 + 1;
                int v01 = baseVertex + ((mz + 1) * rowStride) + mx;
                int v11 = v01 + 1;

                indices.Add(v00);
                indices.Add(v11);
                indices.Add(v10);

                indices.Add(v00);
                indices.Add(v01);
                indices.Add(v11);
            }
        }
    }

    private static void BuildModernMergedTile(
        WaterTile tile, int cellsX, int cellsZ, float height,
        float x0, float x1, float z0, float z1,
        List<float> positions, List<float> normals, List<float> uvs, List<int> indices)
    {
        int rowW = cellsX + 1;
        var prefix = new int[(cellsZ + 1) * rowW];
        int activeTotal = 0;

        for (int mz = 0; mz < cellsZ; mz++)
        {
            int rowSum = 0;
            int prevRow = mz * rowW;
            int curRow = (mz + 1) * rowW;
            for (int mx = 0; mx < cellsX; mx++)
            {
                if (IsActive(tile, cellsX, mx, mz))
                {
                    rowSum++;
                    activeTotal++;
                }

                prefix[curRow + mx + 1] = prefix[prevRow + mx + 1] + rowSum;
            }
        }

        if (activeTotal == 0)
        {
            return;
        }

        int RectSum(int rx0, int rz0, int rx1, int rz1) =>
            prefix[rz1 * rowW + rx1] - prefix[rz0 * rowW + rx1] -
            prefix[rz1 * rowW + rx0] + prefix[rz0 * rowW + rx0];

        int cellCount = cellsX * cellsZ;
        var emit = new bool[cellCount];
        var mergeable = new bool[cellCount];
        int fineSize = (2 * FineShoreCells) + 1;
        int fineArea = fineSize * fineSize;
        bool anyEmitted = false;

        for (int mz = 0; mz < cellsZ; mz++)
        {
            int az0 = Math.Max(0, mz - ApronCells);
            int az1 = Math.Min(cellsZ, mz + ApronCells + 1);
            int fz0 = mz - FineShoreCells;
            int fz1 = mz + FineShoreCells + 1;
            bool zInside = fz0 >= 0 && fz1 <= cellsZ;
            int rowBase = mz * cellsX;

            for (int mx = 0; mx < cellsX; mx++)
            {
                int ax0 = Math.Max(0, mx - ApronCells);
                int ax1 = Math.Min(cellsX, mx + ApronCells + 1);
                if (RectSum(ax0, az0, ax1, az1) <= 0)
                {
                    continue;
                }

                int idx = rowBase + mx;
                emit[idx] = true;
                anyEmitted = true;

                if (zInside)
                {
                    int fx0 = mx - FineShoreCells;
                    int fx1 = mx + FineShoreCells + 1;
                    if (fx0 >= 0 && fx1 <= cellsX && RectSum(fx0, fz0, fx1, fz1) == fineArea)
                    {
                        mergeable[idx] = true;
                    }
                }
            }
        }

        if (!anyEmitted)
        {
            return;
        }

        float cellSpanX = Math.Abs(x1 - x0) / cellsX;
        float cellSpanZ = Math.Abs(z1 - z0) / cellsZ;
        int maxMergeX = Math.Clamp((int)MathF.Floor(MaxMergedSpanMetres / Math.Max(cellSpanX, 1e-3f)), 1, MaxMergedCells);
        int maxMergeZ = Math.Clamp((int)MathF.Floor(MaxMergedSpanMetres / Math.Max(cellSpanZ, 1e-3f)), 1, MaxMergedCells);

        var consumed = new bool[cellCount];
        var remap = new int[(cellsX + 1) * (cellsZ + 1)];
        Array.Fill(remap, -1);

        int EmitVertex(int vxIdx, int vzIdx)
        {
            int gridIdx = (vzIdx * rowW) + vxIdx;
            int existing = remap[gridIdx];
            if (existing >= 0)
            {
                return existing;
            }

            float px = x0 + ((x1 - x0) * ((float)vxIdx / cellsX));
            float pz = z0 + ((z1 - z0) * ((float)vzIdx / cellsZ));
            int outIdx = positions.Count / 3;
            remap[gridIdx] = outIdx;

            positions.Add(px);
            positions.Add(height);
            positions.Add(pz);

            normals.Add(0f);
            normals.Add(1f);
            normals.Add(0f);

            uvs.Add(px);
            uvs.Add(pz);
            return outIdx;
        }

        for (int mz = 0; mz < cellsZ; mz++)
        {
            int rowBase = mz * cellsX;
            for (int mx = 0; mx < cellsX; mx++)
            {
                int startIdx = rowBase + mx;
                if (consumed[startIdx] || !emit[startIdx])
                {
                    continue;
                }

                int w = 1;
                int h = 1;
                if (mergeable[startIdx])
                {
                    int limitX = Math.Min(cellsX - mx, maxMergeX);
                    while (w < limitX && !consumed[rowBase + mx + w] && mergeable[rowBase + mx + w])
                    {
                        w++;
                    }

                    int limitZ = Math.Min(cellsZ - mz, maxMergeZ);
                    while (h < limitZ)
                    {
                        int nextRowBase = (mz + h) * cellsX;
                        bool rowOk = true;
                        for (int dx = 0; dx < w; dx++)
                        {
                            int cIdx = nextRowBase + mx + dx;
                            if (consumed[cIdx] || !mergeable[cIdx])
                            {
                                rowOk = false;
                                break;
                            }
                        }

                        if (!rowOk)
                        {
                            break;
                        }

                        h++;
                    }
                }

                for (int dz = 0; dz < h; dz++)
                {
                    int markRow = (mz + dz) * cellsX;
                    for (int dx = 0; dx < w; dx++)
                    {
                        consumed[markRow + mx + dx] = true;
                    }
                }

                int v00 = EmitVertex(mx, mz);
                int v10 = EmitVertex(mx + w, mz);
                int v01 = EmitVertex(mx, mz + h);
                int v11 = EmitVertex(mx + w, mz + h);

                indices.Add(v00);
                indices.Add(v11);
                indices.Add(v10);

                indices.Add(v00);
                indices.Add(v01);
                indices.Add(v11);
            }
        }
    }

    private static bool IsActive(WaterTile tile, int cellsX, int mx, int mz)
    {
        if (tile.Mask.Length == 0)
        {
            return true;
        }

        long index = ((long)mz * cellsX) + mx;
        return index < tile.Mask.Length && tile.Mask[index] != 0;
    }
}
