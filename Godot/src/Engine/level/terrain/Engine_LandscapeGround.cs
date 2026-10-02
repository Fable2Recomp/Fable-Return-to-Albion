using System;
using System.Collections.Generic;

namespace Refii.Level;

/// <summary>
/// The ground height under a point, from every landscape grid a level states (the .ghf grids
/// and the vistas' .ehf height textures), in region space before the root's mirror.
/// </summary>
/// <remarks>
/// A heightfield is single-valued and opaque, so anything below it at a point is hidden from
/// every eye above it there. That makes "water under the ground" a static fact rather than a
/// visibility guess. Where grids overlap (a vista's seam with the main grid) they agree to one
/// 16-bit step; the first grid covering a point answers.
/// </remarks>
public sealed class Engine_LandscapeGround
{
    private readonly List<GhfHeights> _grids = new();

    public int GridCount => _grids.Count;

    public void Add(GhfHeights grid)
    {
        if (grid.Width > 1 && grid.Height > 1 && grid.Spacing > 0f)
        {
            _grids.Add(grid);
        }
    }

    /// <summary>Bilinear ground height at (x, z); false where no grid covers the point.</summary>
    public bool TryHeight(float x, float z, out float height)
    {
        foreach (GhfHeights grid in _grids)
        {
            float u = (x - grid.OriginX) / grid.Spacing;
            float v = (z - grid.OriginZ) / grid.Spacing;
            if (u < 0f || v < 0f || u > grid.Width - 1 || v > grid.Height - 1)
            {
                continue;
            }

            int x0 = Math.Min((int)u, grid.Width - 2);
            int z0 = Math.Min((int)v, grid.Height - 2);
            float fx = u - x0;
            float fz = v - z0;
            float[] h = grid.Values;
            int row0 = z0 * grid.Width;
            int row1 = row0 + grid.Width;
            float near = h[row0 + x0] + ((h[row0 + x0 + 1] - h[row0 + x0]) * fx);
            float far = h[row1 + x0] + ((h[row1 + x0 + 1] - h[row1 + x0]) * fx);
            height = near + ((far - near) * fz);
            return true;
        }

        height = 0f;
        return false;
    }

    /// <summary>
    /// Water cells of a body that are not under ground — the ground at the cell's centre is
    /// below the surface, or no grid covers it. With <paramref name="firstOnly"/> it stops at one.
    /// </summary>
    public int OpenCells(WaterBody body, bool firstOnly, out int waterCells)
    {
        int open = 0;
        waterCells = 0;
        foreach (WaterTile tile in body.Tiles)
        {
            if (tile.CellsX <= 0 || tile.CellsZ <= 0 || tile.Mask.Length < tile.CellsX * tile.CellsZ)
            {
                continue;
            }

            float cellX = tile.ExtentX / tile.CellsX;
            float cellZ = tile.ExtentZ / tile.CellsZ;
            float minX = tile.CentreX - (tile.ExtentX * 0.5f);
            float minZ = tile.CentreZ - (tile.ExtentZ * 0.5f);
            for (int row = 0; row < tile.CellsZ; row++)
            {
                for (int col = 0; col < tile.CellsX; col++)
                {
                    if (tile.Mask[(row * tile.CellsX) + col] == 0)
                    {
                        continue;
                    }

                    waterCells++;
                    float x = minX + ((col + 0.5f) * cellX);
                    float z = minZ + ((row + 0.5f) * cellZ);
                    if (!TryHeight(x, z, out float ground) || ground < body.BaseHeight)
                    {
                        open++;
                        if (firstOnly)
                        {
                            return open;
                        }
                    }
                }
            }
        }

        return open;
    }
}
