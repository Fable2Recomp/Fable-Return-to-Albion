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
    /// dithers out at its radius (18 m for a small clump under Low's 0.4), but the cut that
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

    /// <summary>`foliage_density`: the share of ground foliage copies drawn; 1 draws every one.</summary>
    public const string FoliageDensityKey = "foliage_density";

    /// <summary>
    /// The configured foliage density (a cvar-set override included, so it applies to the next region
    /// built), held to [0.25, 1]; 1 for an unset or invalid value.
    /// </summary>
    /// <remarks>
    /// WHY THIN IT. Undergrowth is drawn up close, where it covers the most screen, and as alpha-cut
    /// cards stacked several deep: at Bower Lake 1,023 nettle copies, and on the S10e the props' shading
    /// was 27% of the frame there. A thinned copy is the whole clump, chosen by its position, so the
    /// cover stays even, the same copies stay on every visit, and no clump pops as the camera moves.
    /// </remarks>
    public static float ReadFoliageDensity()
    {
        if (!Refii.Core.Engine_Platform.IsAndroid) return 1f;
        try
        {
            return ClampFoliageDensity(Refii.Core.Engine_RefiiConfig.Float(Refii.Core.Engine_RefiiConfig.Read(out _), FoliageDensityKey, 1f));
        }
        catch (Exception)
        {
            return 1f;
        }
    }

    /// <summary>1 (every copy) for an unset, zero or invalid value; otherwise [0.25, 1].</summary>
    internal static float ClampFoliageDensity(float share) =>
        float.IsFinite(share) && share > 0f ? Math.Clamp(share, 0.25f, 1f) : 1f;

    /// <summary>Whether a block's copy is drawn at <paramref name="density"/>: every copy of anything
    /// but ground foliage, and a share of the foliage's by a hash of where each stands.</summary>
    /// <remarks>Trees are kept whole: their models sit under the same foliage folders, and a thinned
    /// forest is a different landscape (tree_far_reach and tree_card_lod thin them instead).</remarks>
    private static bool KeepsFoliageCopy(PropBlockBuild block, int index, float density)
    {
        if (density >= 1f || block.ModelClass == PropModelClass.Tree ||
            !(block.Foliage || Refii.Models.Engine_MdlFoliage.IsFoliagePath(block.ModelPath)))
            return true;
        Godot.Vector3 at = block.Instances[index].Origin;
        uint hash = unchecked((uint)Godot.Mathf.FloorToInt(at.X * 8f) * 73856093u ^ (uint)Godot.Mathf.FloorToInt(at.Z * 8f) * 19349663u);
        return (hash * 0.6180339887498949) % 1.0 < density;
    }

    /// <summary>The cell a block's placements batch by: the foliage cell for ground foliage when set.</summary>
    private static float CellMetresFor(PropBlockBuild block, float foliageCells) =>
        foliageCells > 0f && (block.Foliage || Refii.Models.Engine_MdlFoliage.IsFoliagePath(block.ModelPath))
            ? foliageCells
            : BatchCellMetres;
}
