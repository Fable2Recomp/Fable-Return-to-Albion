using System;
using System.Collections.Generic;
using System.Linq;

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

    /// <summary>`foliage_own_cells`: undergrowth keeps its own cells instead of folding sparse ones into 3x cells (Android, default off).</summary>
    public const string FoliageOwnCellsKey = "foliage_own_cells";

    /// <summary>
    /// Whether <see cref="MergeSparseCells"/> leaves a block's cells as they are: the key is on and
    /// the block batches by the foliage cell (<see cref="CellMetresFor"/>'s test). Pure.
    /// </summary>
    /// <remarks>
    /// A folded 3x cell spreads about three times as far, so its node reaches further past the
    /// copies' own radius and every copy out there is rasterised and discarded by the fade dither.
    /// Kept fine, the node stops near the copies' radius. The same picture wherever the size tier
    /// sets the node's end; where a block's stated range is the shorter end, a copy's fade radius is
    /// that end less the spread (Engine_PropStreamer.ApplyRange), so fine cells draw those copies
    /// further out than today.
    /// </remarks>
    internal static bool KeepsOwnCells(bool on, bool foliage) => on && foliage;

    private static int _ownCellsGeneration = int.MinValue;
    private static bool _ownCells;

    /// <summary>The key, re-read when the configuration changes (so it applies to the next region built); Android only.</summary>
    private static bool ReadFoliageOwnCells()
    {
        if (!Refii.Core.Engine_Platform.IsAndroid) return false;
        int generation = Refii.Core.Engine_Switches.Generation;
        if (generation == _ownCellsGeneration) return _ownCells;
        try
        {
            _ownCells = Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), FoliageOwnCellsKey, false);
        }
        catch (Exception)
        {
            _ownCells = false;
        }

        _ownCellsGeneration = generation;
        return _ownCells;
    }

    /// <summary><see cref="KeepsOwnCells"/> for this block under this process's setting.</summary>
    private static bool FoliageKeepsOwnCells(PropBlockBuild block) =>
        KeepsOwnCells(ReadFoliageOwnCells(), block.Foliage || Refii.Models.Engine_MdlFoliage.IsFoliagePath(block.ModelPath));

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
        _thinTally = null;
        if (!Refii.Core.Engine_Platform.IsAndroid) return 1f;
        try
        {
            IReadOnlyDictionary<string, string> values = Refii.Core.Engine_RefiiConfig.Read(out _);
            _thinFix = Refii.Core.Engine_RefiiConfig.Bool(values, FoliageThinFixKey, true);
            float density = ClampFoliageDensity(Refii.Core.Engine_RefiiConfig.Float(values, FoliageDensityKey, 1f));
            if (density < 1f) _thinTally = new ThinTally();
            return density;
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
    private static bool KeepsFoliageCopy(PropBlockBuild block, int index, float density) =>
        KeepsFoliageCopy(block, index, density, _thinFix);

    /// <summary><see cref="KeepsFoliageCopy(PropBlockBuild, int, float)"/> with the fix stated, tallied for the build's log line.</summary>
    private static bool KeepsFoliageCopy(PropBlockBuild block, int index, float density, bool fix)
    {
        if (density >= 1f || block.ModelClass == PropModelClass.Tree ||
            !(block.Foliage || Refii.Models.Engine_MdlFoliage.IsFoliagePath(block.ModelPath)))
            return true;
        Godot.Transform3D placed = block.Instances[index];
        bool record = index < block.InstanceHashes.Count && block.InstanceHashes[index] != 0u;
        Verdict verdict = Thinning(FoliageRadius(block.ModelPath), placed.Basis.Scale, record, ThinHashShare(placed.Origin), density, fix);
        _thinTally?.Add(block.ModelPath, verdict);
        return verdict != Verdict.Thinned;
    }

    /// <summary>`foliage_thin_fix`: the plant thinning's two fixes (<see cref="Thinning"/>); on unless set off.</summary>
    public const string FoliageThinFixKey = "foliage_thin_fix";

    /// <summary>Read with the density, once per region build; on off the phone too (the density is 1 there).</summary>
    private static volatile bool _thinFix = true;

    /// <summary>What the thinning decided for one copy, and why it was kept.</summary>
    internal enum Verdict
    {
        /// <summary>Drawn by the share.</summary>
        Kept,
        /// <summary>Kept whole for its size.</summary>
        KeptLarge,
        /// <summary>Kept for its size only by the fix: a mirrored placement the signed scale let through.</summary>
        KeptLargeByFix,
        /// <summary>Kept by the fix: a `.save` record the guest also places, which marries this copy.</summary>
        KeptRecordByFix,
        Thinned,
    }

    /// <summary>
    /// The thinning's decision for one ground-foliage copy. Pure.
    /// </summary>
    /// <remarks>
    /// WHY THE FIX. Without it, the size test took the radius times the SIGNED x scale, and most
    /// placements are mirrored (negative): whole hedges, dead trees, large brambles and wall ivy were
    /// thinned (Wraithmarsh lost about 580 dead trees at Low). And a copy that is a `.save` record is
    /// the guest's own entity: thinned, the entity found no copy to marry and was drawn on its own node
    /// at full entity cost (Oakfield: 242 records thinned at Low, 75 fewer marriages than Medium).
    /// With the fix the size is the largest scale, signs ignored, and records are never thinned.
    /// </remarks>
    internal static Verdict Thinning(float radius, Godot.Vector3 scale, bool record, double share, float density, bool fix)
    {
        float signedX = radius * scale.X;
        if (signedX > ThinnedRadiusMax) return Verdict.KeptLarge;
        if (fix)
        {
            Godot.Vector3 size = scale.Abs();
            if (radius * MathF.Max(size.X, MathF.Max(size.Y, size.Z)) > ThinnedRadiusMax) return Verdict.KeptLargeByFix;
            if (record) return Verdict.KeptRecordByFix;
        }

        return share < density ? Verdict.Kept : Verdict.Thinned;
    }

    /// <summary>Where a copy falls in [0, 1) by its position: the share below the density is drawn. Pure.</summary>
    internal static double ThinHashShare(Godot.Vector3 at)
    {
        uint hash = unchecked((uint)Godot.Mathf.FloorToInt(at.X * 8f) * 73856093u ^ (uint)Godot.Mathf.FloorToInt(at.Z * 8f) * 19349663u);
        return (hash * 0.6180339887498949) % 1.0;
    }

    /// <summary>One region build's thinning, for its `[foliage-thin]` line; per build thread.</summary>
    [ThreadStatic]
    private static ThinTally? _thinTally;

    internal sealed class ThinTally
    {
        public int Kept, KeptLarge, KeptLargeByFix, KeptRecordByFix, Thinned;
        public readonly Dictionary<string, int> ThinnedBy = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> FixedBy = new(StringComparer.Ordinal);

        public void Add(string modelPath, Verdict verdict)
        {
            switch (verdict)
            {
                case Verdict.Kept: Kept++; return;
                case Verdict.KeptLarge: KeptLarge++; return;
                case Verdict.KeptLargeByFix: KeptLargeByFix++; break;
                case Verdict.KeptRecordByFix: KeptRecordByFix++; break;
                default: Thinned++; break;
            }

            Dictionary<string, int> by = verdict == Verdict.Thinned ? ThinnedBy : FixedBy;
            string name = NameOf(modelPath);
            by[name] = by.GetValueOrDefault(name) + 1;
        }

        /// <summary>The log line's body. Pure.</summary>
        public string Describe(bool fix) =>
            $"kept {Kept + KeptLarge + KeptLargeByFix + KeptRecordByFix}, thinned {Thinned}; the fix ({(fix ? "on" : "off")}) " +
            $"kept {KeptLargeByFix} large mirrored and {KeptRecordByFix} record copies ({Top(FixedBy)}); thinned most: {Top(ThinnedBy)}";

        private static string Top(Dictionary<string, int> counts) =>
            counts.Count == 0 ? "none" : string.Join(", ", counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
                .Take(6).Select(p => $"{p.Key} x{p.Value}"));
    }

    /// <summary>The `[foliage-thin]` line at the end of a region's props build, when anything is thinned.</summary>
    private static void ReportFoliageThinning(LevelEntry level)
    {
        if (_thinTally is not { } tally) return;
        _thinTally = null;
        Godot.GD.Print($"[foliage-thin] {level}: {tally.Describe(_thinFix)}");
    }

    /// <summary>The largest model radius, in metres, that `foliage_density` thins: nettles, bracken, grass, flowers
    /// and small bushes are 0.2-2 m; large brambles, hedges and every tree-sized plant are more.</summary>
    internal const float ThinnedRadiusMax = 2f;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> FoliageRadii = new();

    /// <summary>A model's bounding radius from its file header (version 33-36: the sphere at +16, radius at +28;
    /// version 47: at +28, radius at +40), cached per path; 0 when unread.</summary>
    internal static float FoliageRadius(string modelPath) =>
        FoliageRadii.GetOrAdd(modelPath, static path => RadiusFromHeader(Refii.Models.Engine_BnkMeshLoader.ReadHead(path, 48)));

    /// <summary>The bounding radius in a model header's bytes; 0 for anything unreadable. Pure.</summary>
    internal static float RadiusFromHeader(byte[]? head)
    {
        if (head is null || head.Length < 44) return 0f;
        uint version = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8, 4));
        int at = version is >= 33 and <= 36 ? 28 : version == 47 ? 40 : -1;
        if (at < 0) return 0f;
        float radius = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(head.AsSpan(at, 4));
        return float.IsFinite(radius) && radius > 0f ? radius : 0f;
    }

    /// <summary>The cell a block's placements batch by: the foliage cell for ground foliage when set,
    /// the prop cell (<see cref="PropCellKey"/>) for everything else when set.</summary>
    private static float CellMetresFor(PropBlockBuild block, float foliageCells, float propCells = 0f) =>
        block.Foliage || Refii.Models.Engine_MdlFoliage.IsFoliagePath(block.ModelPath)
            ? (foliageCells > 0f ? foliageCells : BatchCellMetres)
            : (propCells > 0f ? propCells : BatchCellMetres);

    /// <summary>`prop_cell_metres`: the cube edge every other batched model (props, buildings, trees) is batched by; 0 keeps <see cref="BatchCellMetres"/>.</summary>
    public const string PropCellKey = "prop_cell_metres";

    /// <summary>
    /// The configured prop cell (a cvar-set override included, so it applies to the next region
    /// built), 0 for the shared one. Held to [8, 32] m. Android only.
    /// </summary>
    /// <remarks>
    /// WHY. Godot picks a mesh level once per MultiMesh, from the camera to the nearest point of the
    /// batch's box (render_forward_mobile.cpp `_fill_render_list`), so every copy in a 32 m cell
    /// draws at the level of the cell's nearest corner: the cells around the camera draw every copy
    /// at full detail, whatever `mesh_lod_pixels` says. A finer cell lets the far copies of a near
    /// cell take their coarser levels, and culls by a tighter box. The cost is more, smaller draws.
    /// The lodcensus's `perObjectTriangles` against `triangles` is what a cell of one copy would save.
    /// </remarks>
    public static float ReadPropCellMetres()
    {
        if (!Refii.Core.Engine_Platform.IsAndroid) return 0f;
        try
        {
            return ClampPropCell(Refii.Core.Engine_RefiiConfig.Float(Refii.Core.Engine_RefiiConfig.Read(out _), PropCellKey, 0f));
        }
        catch (Exception)
        {
            return 0f;
        }
    }

    /// <summary>0 (the shared cell) for an unset, zero or invalid value; otherwise [8, 32] m.</summary>
    internal static float ClampPropCell(float metres) =>
        float.IsFinite(metres) && metres > 0f ? Math.Clamp(metres, 8f, BatchCellMetres) : 0f;
}
