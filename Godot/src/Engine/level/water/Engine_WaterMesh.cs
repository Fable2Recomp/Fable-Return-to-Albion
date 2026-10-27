using System;
using System.Collections.Generic;

namespace Refii.Level;

public static class Engine_WaterMesh
{
    private const long MaxVerticesPerTile = 1200000;

    /// <summary>
    /// One mesh for a body's tiles. <paramref name="fullPatches"/> (the classic renderer) meshes
    /// each tile as the title does (`sub_82B3A960`): its whole rectangle on a grid of
    /// max(2, min(cells, extent * 0.2)) vertices per axis, every quad kept, the shore left to the
    /// mask's fade; otherwise one quad per water cell.
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
                    if (!fullPatches && !IsActive(tile, cellsX, mx, mz))
                    {
                        continue;
                    }

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
