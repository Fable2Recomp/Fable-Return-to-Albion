using System;

namespace Refii.Level;

public static partial class Engine_LevelProps
{
    /// <summary>`foliage_cell_metres`: the cube edge ground foliage is batched by; 0 keeps <see cref="BatchCellMetres"/>.</summary>
    public const string FoliageCellKey = "foliage_cell_metres";

    /// <summary>
    /// The configured foliage cell (a cvar-set override included, so it applies to the next region
    /// built), 0 for the shared one. Held to [4, 32] m.
    /// </summary>
    /// <remarks>
    /// WHY FOLIAGE BATCHES FINER. Undergrowth is tier-culled (prop_cull_distance_scale): each copy
    /// dithers out at its radius (18 m for a small clump under Performance's 0.4), but the cut that
    /// saves geometry is the batch node's, at the radius plus the batch's spread, and a 32 m cell
    /// spreads about 23 m. A copy past its radius is discarded pixel by pixel after its vertices
    /// and triangles have been processed. At Bower Lake the lodcensus counted 1,023 nettle copies
    /// (124k triangles, no level: 119 a copy is under mesh_auto_lod's floor) in batches reaching
    /// 45 m. An 8 m cell spreads about 6 m, so its node stops about 24 m out. The picture is the
    /// same: no copy inside its radius is ever outside its node's range. The cost is more, smaller
    /// draws, which the S10e has shown it does not mind.
    /// </remarks>
    public static float ReadFoliageCellMetres()
    {
        try
        {
            float metres = Refii.Core.Engine_RefiiConfig.Float(Refii.Core.Engine_RefiiConfig.Read(out _), FoliageCellKey, 0f);
            return ClampFoliageCell(metres);
        }
        catch (Exception)
        {
            return 0f;
        }
    }

    /// <summary>0 (the shared cell) for an unset, zero or invalid value; otherwise [4, 32] m.</summary>
    internal static float ClampFoliageCell(float metres) =>
        float.IsFinite(metres) && metres > 0f ? Math.Clamp(metres, 4f, BatchCellMetres) : 0f;

    /// <summary>The cell a block's placements batch by: the foliage cell for ground foliage when set.</summary>
    private static float CellMetresFor(PropBlockBuild block, float foliageCells) =>
        foliageCells > 0f && (block.Foliage || Refii.Models.Engine_MdlFoliage.IsFoliagePath(block.ModelPath))
            ? foliageCells
            : BatchCellMetres;
}
