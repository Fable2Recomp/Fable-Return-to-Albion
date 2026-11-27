using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;
using Refii.Models;

namespace Refii.Level;

public sealed class PropBuildStats
{
    public int SaveEntities { get; set; }

    /// <summary>Level-file blocks whose model is a TreeMesh (`.mdl` byte 77 == 2).</summary>
    public int TreeBlocks { get; set; }

    /// <summary>Fine cells with at most SparseCellMembers members folded into coarse cells, and the batches that saved.</summary>
    public int SparseCellsFolded { get; set; }

    public int SparseBatchesSaved { get; set; }

    /// <summary>`.save` placements of TreeMesh models given the definition's L, F and D.</summary>
    public int SaveTreesRanged { get; set; }

    /// <summary>`.save` placements of TreeMesh models whose definition states no appearance.</summary>
    public int SaveTreesUnstated { get; set; }

    /// <summary>Batches beyond one per cell because placements in the cell state different ranges.</summary>
    public int RangeSplitBatches { get; set; }

    public int Placements { get; set; }

    public int Skeletal { get; set; }

    public int Unnamed { get; set; }

    public int NoModelHash { get; set; }

    public int UnresolvedModel { get; set; }

    public int RejectedCharacter { get; set; }

    public int DuplicateRecord { get; set; }

    public int DuplicateTransform { get; set; }

    public int SupplementalHits { get; set; }

    public int SupplementalModelFills { get; set; }

    public int ShellBadPosition { get; set; }

    public int Instances { get; set; }

    public int Blocks { get; set; }

    /// <summary>Props standing as their own node, and props folded into a MultiMesh.</summary>
    /// <remarks>
    /// Sizes what further batching could save: each single node costs per-instance render-thread
    /// work in the visible pass, the shadow pass and every reflection viewport. Blocks at or
    /// under <see cref="Engine_LevelProps.SelectableInstanceLimit"/> stay as singles.
    /// </remarks>
    public int SingleInstances { get; set; }

    public int BatchedInstances { get; set; }

    /// <summary>Blocks left unbatched for being at or under the limit, and their instances.</summary>
    public int SmallBlocks { get; set; }

    public int SmallBlockInstances { get; set; }

    /// <summary>Instances pulled out of a batch because something needs them addressable.</summary>
    public int ForcedSingles { get; set; }

    public int EngineLevelBlocks { get; set; }

    public int EngineLevelInstances { get; set; }

    public int EngineLevelUnresolved { get; set; }

    public int EngineLevelType21Blocks { get; set; }

    public int Companions { get; set; }

    public int FoliageBlocks { get; set; }

    public int FoliageInstances { get; set; }

    public int FoliageUnresolved { get; set; }

    public string EngineLevelError { get; set; } = string.Empty;

    /// <summary>
    /// Placements with no model hash that DO declare a mesh component — the work list.
    ///
    /// A subset of <see cref="NoModelHash"/>, and the only part of it that is a defect. The rest
    /// are triggers, markers, sound emitters and region volumes, which state no appearance and
    /// were never going to draw. Reported as one number, 462 of them in Ravenscar said nothing
    /// about how many models were actually lost.
    /// </summary>
    public int NoModelHashWithAppearance { get; set; }

    /// <summary>engine_level instances dropped because another placement already occupies the
    /// same transform with the same model. Silent until now, and it is subtracted from the
    /// instance count the level reports.</summary>
    public int EngineLevelDuplicate { get; set; }

    public override string ToString() =>
        $"save={SaveEntities} placements={Placements} instances={Instances} " +
        $"blocks={Blocks} supplemental={SupplementalHits} " +
        $"modelfills={SupplementalModelFills} " +
        $"skipped(skeletal={Skeletal} unnamed={Unnamed} " +
        $"nomodel={NoModelHash}(of which {NoModelHashWithAppearance} DECLARE A MESH) " +
        $"unresolved={UnresolvedModel} character={RejectedCharacter} " +
        $"shellpos={ShellBadPosition}) " +
        $"companions={Companions} (" +
        $"duprecord={DuplicateRecord} duptransform={DuplicateTransform}) " +
        $"engine_level(blocks={EngineLevelBlocks} inst={EngineLevelInstances} " +
        $"unresolved={EngineLevelUnresolved} duptransform={EngineLevelDuplicate} " +
        $"type21={EngineLevelType21Blocks} trees={TreeBlocks} rangesplits={RangeSplitBatches}) " +
        $"placedtrees(ranged={SaveTreesRanged} unstated={SaveTreesUnstated}) " +
        $"sparsecells(folded={SparseCellsFolded} batchessaved={SparseBatchesSaved}) " +
        $"foliage(models={FoliageBlocks} inst={FoliageInstances} " +
        $"unresolved={FoliageUnresolved})" +
        (EngineLevelError.Length == 0 ? string.Empty : $" [{EngineLevelError}]");
}

public static partial class Engine_LevelProps
{
    public const string NodeName = "Props";

    public const float ModelReach = 64.0f;

    /// <summary>Edge of the cube a model's placements are batched by.</summary>
    /// <remarks>
    /// 32 m re-measured 2026-09-22 in the level viewer (Bowerstone Market, one camera): 48 / 64 / 96 m
    /// cut the frame's draws from 2,442 to 2,357 / 2,176 / 1,868, and the viewport's CPU render
    /// time ROSE with each, 2.16 -> 2.57 / 2.82 / 2.87 ms (GPU 3.2 -> 5.6 ms): a larger cell is
    /// culled less often and drawn by more passes. The draws were not the cost.
    /// </remarks>
    public const float BatchCellMetres = 32.0f;

    /// <summary>
    /// A cell with this many members or fewer is folded into the <see cref="SparseCellMetres"/>
    /// cell around it. A MultiMesh is one draw in every pass that sees it and never merges
    /// with another draw (`cant_repeat` for MULTIMESH in `_fill_render_list`), so a model
    /// scattered one or two to a cell is a draw per cell in every pass. Dense cells keep the
    /// 32 m grain; only the sparse ones coarsen, and their few copies cost the extra passes little.
    /// </summary>
    public const int SparseCellMembers = 2;

    /// <summary>The coarse cell sparse members fold into: three fine cells a side.</summary>
    public const float SparseCellMetres = BatchCellMetres * 3f;

    /// <summary>
    /// Folds every cell of <paramref name="cells"/> holding at most <see cref="SparseCellMembers"/>
    /// members into the coarse cell around it, keyed so it never collides with a fine cell.
    /// Trees keep their fine cells: their LOD and shadow ranges widen by the batch spread.
    /// </summary>
    private static void MergeSparseCells(
        PropBlockBuild block,
        Dictionary<(Vector3I Cell, StatedRange? Range, byte Casting), List<int>> cells,
        PropBuildStats stats)
    {
        if (block.ModelClass == PropModelClass.Tree || FoliageKeepsOwnCells(block))
        {
            return;
        }

        var sparse = new List<(Vector3I Cell, StatedRange? Range, byte Casting)>();
        foreach (KeyValuePair<(Vector3I Cell, StatedRange? Range, byte Casting), List<int>> cell in cells)
        {
            if (cell.Value.Count <= SparseCellMembers)
            {
                sparse.Add(cell.Key);
            }
        }

        if (sparse.Count < 2)
        {
            return;
        }

        int before = cells.Count;
        foreach ((Vector3I Cell, StatedRange? Range, byte Casting) key in sparse)
        {
            List<int> members = cells[key];
            cells.Remove(key);
            // The coarse key lives far outside any fine cell's index range.
            var coarse = (new Vector3I(
                Mathf.FloorToInt(key.Cell.X / 3f) + SparseCellKeyOffset,
                Mathf.FloorToInt(key.Cell.Y / 3f) + SparseCellKeyOffset,
                Mathf.FloorToInt(key.Cell.Z / 3f) + SparseCellKeyOffset), key.Range, key.Casting);
            if (!cells.TryGetValue(coarse, out List<int>? merged))
            {
                cells[coarse] = merged = new List<int>();
            }

            merged.AddRange(members);
        }

        stats.SparseCellsFolded += sparse.Count;
        stats.SparseBatchesSaved += before - cells.Count;
    }

    /// <summary>Added to a coarse cell's indices; fine cells never reach it (levels span ~1 km).</summary>
    private const int SparseCellKeyOffset = 1 << 20;

    /// <summary>
    /// Widens a batch's bounds to hold one instance that was written after the streamer
    /// measured them. Godot culls the batch by that one box, so a twin driven out of its cell
    /// would otherwise vanish with the cell. A batch with no mesh yet is measured whole when
    /// the mesh arrives.
    /// </summary>
    /// <summary>A reflection in X, its own inverse: what a reflected batch's node carries for its copies.</summary>
    public static readonly Transform3D BatchMirror = new(
        new Basis(new Vector3(-1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f)), Vector3.Zero);

    /// <summary>Floats per batched copy: a Transform3D's twelve and the probe colour's four.</summary>
    private const int BatchStride = 16;

    public static void CoverInstance(MultiMesh batch, Transform3D instance)
    {
        if (batch.Mesh is not { } mesh)
        {
            return;
        }

        Aabb needed = instance * mesh.GetAabb();
        Aabb held = batch.CustomAabb;
        if (!held.Encloses(needed))
        {
            // Slack, so a prop in motion does not re-pair the batch on every frame.
            Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.PropBounds);
            batch.CustomAabb = held.Merge(needed.Grow(4f));
            Engine_PropShadow.CoverBounds(batch);
        }
    }

    /// <summary>
    /// Blocks with this many placements or fewer stay as individual MeshInstance3D nodes instead of
    /// being partitioned into 32m cell MultiMeshInstance3D batches. Set to 1 so any model with 2 or
    /// more instances is partitioned by spatial locality cells and batched.
    /// </summary>
    public const int SelectableInstanceLimit = 1;

    public static readonly string[] SupplementalGdbPaths =
    {
        "globals/globals.gdb",
        "speechaction/speechaction.gdb",
        "interactivecutscenes/interactivecutscenes.gdb",
        "entity/entity.gdb",
        "environmentthemes/environmentthemes.gdb",
    };

    public readonly record struct StaticShadowPropBlock(
        string ModelPath,
        string ShadowModelPath,
        PropModelClass ModelClass,
        bool Foliage,
        IReadOnlyList<Transform3D> Instances,
        IReadOnlyList<uint> InstanceHashes,
        IReadOnlyList<byte> InstanceCasting);

    /// <summary>
    /// Thread-safe snapshot of already-parsed static prop blocks, placed lights, light-bearing fixture
    /// hashes/models, and decoded playable heightfields captured during <see cref="Build(LevelEntry,Func{string,byte[]?},Refii.Lighting.LightProbeField?,Refii.Lighting.LightingFrame?,Refii.Lighting.LightmapContainer?,Refii.Entities.EntityInspectData?,IReadOnlyList{GdbView}?,bool,out StaticLightingBakeSnapshot?,out PropBuildStats)"/>
    /// so during-load GPU shadow and AO baking never has to re-parse GDBs, GMDs, or heightfields from scratch.
    /// </summary>
    public sealed class StaticLightingBakeSnapshot
    {
        /// <summary>Resolved virtual path of the level's primary GDB.</summary>
        public string GdbPath { get; init; } = string.Empty;

        /// <summary>Non-foliage static prop blocks and their world transforms/casting flags.</summary>
        public List<StaticShadowPropBlock> PropBlocks { get; init; } = new();

        /// <summary>Placement record hashes that host placed lights (excluded from self-shadowing).</summary>
        public HashSet<uint> LightHosts { get; init; } = new();

        /// <summary>Model virtual paths of light-hosting fixtures.</summary>
        public HashSet<string> LightHostModels { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Resolved placed lights spawned for the scenario.</summary>
        public List<Refii.Lighting.Engine_LevelLights.ResolvedLightInstance> ResolvedLights { get; init; } = new();

        /// <summary>Decoded playable terrain heightfields for ground/contact AO and BVH assembly.</summary>
        public List<GhfHeights> TerrainHeights { get; init; } = new();
    }

    /// <summary>
    /// Collects all static shadow-casting prop blocks, light-bearing host placement hashes, and
    /// resolved placed light instances for <paramref name="level"/> using the exact same GDB +
    /// engine_level + companion pipeline as <see cref="Build(LevelEntry,Func{string,byte[]?},Refii.Lighting.LightProbeField?,Refii.Lighting.LightingFrame?,Refii.Lighting.LightmapContainer?,Refii.Entities.EntityInspectData?,out PropBuildStats)"/>.
    /// </summary>
    public static bool TryCollectStaticGeometryAndLights(
        LevelEntry level,
        Func<string, byte[]?> read,
        out List<StaticShadowPropBlock> propBlocks,
        out HashSet<uint> lightHosts,
        out HashSet<string> lightHostModels,
        out List<Refii.Lighting.Engine_LevelLights.ResolvedLightInstance> lights,
        out string gdbPath)
    {
        propBlocks = new List<StaticShadowPropBlock>();
        lightHosts = new HashSet<uint>();
        lightHostModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lights = new List<Refii.Lighting.Engine_LevelLights.ResolvedLightInstance>();

        if (!TryCollectRegionBlocks(
            level, read, null, null, createSilverKeyNodes: false, preloadedSupplemental: null,
            out gdbPath, out GdbView view, out _, out _,
            out List<GdbPlacement> placements, out List<GdbView> supplementalViews,
            out lightHosts, out Dictionary<string, PropBlockBuild> blocks,
            out List<string> order, out _, out Dictionary<uint, string> fxModelPaths,
            out _, out _))
        {
            return false;
        }

        foreach (string key in order)
        {
            PropBlockBuild b = blocks[key];
            if (b.Foliage || b.Instances.Count == 0)
            {
                continue;
            }

            propBlocks.Add(new StaticShadowPropBlock(
                b.ModelPath,
                b.ShadowModelPath,
                b.ModelClass,
                b.Foliage,
                b.Instances,
                b.InstanceHashes,
                b.InstanceCasting));
        }

        lights = Refii.Lighting.Engine_LevelLights.ResolvePlacements(
            view, supplementalViews, placements, fxModelPaths, lightHosts, out _, out _);

        foreach (uint h in lightHosts)
        {
            if (fxModelPaths.TryGetValue(h, out string? mp) && !string.IsNullOrEmpty(mp))
            {
                lightHostModels.Add(mp);
            }
        }

        return true;
    }

    private static bool TryCollectRegionBlocks(
        LevelEntry level,
        Func<string, byte[]?> read,
        Refii.Lighting.LightmapContainer? bakes,
        Refii.Entities.EntityInspectData? inspect,
        bool createSilverKeyNodes,
        IReadOnlyList<GdbView>? preloadedSupplemental,
        out string gdbPath,
        out GdbView view,
        out List<SaveEntity> saveEntities,
        out EditorPropHierarchy? editorHierarchy,
        out List<GdbPlacement> placements,
        out List<GdbView> supplementalViews,
        out HashSet<uint> lightHosts,
        out Dictionary<string, PropBlockBuild> blocks,
        out List<string> order,
        out List<SilverKeyNode> silverKeys,
        out Dictionary<uint, string> fxModelPaths,
        out Dictionary<uint, Node3D> fxAttachTo,
        out PropBuildStats stats)
    {
        stats = new PropBuildStats();
        view = null!;
        saveEntities = new List<SaveEntity>();
        editorHierarchy = null;
        placements = new List<GdbPlacement>();
        supplementalViews = new List<GdbView>();
        lightHosts = new HashSet<uint>();
        blocks = new Dictionary<string, PropBlockBuild>();
        order = new List<string>();
        silverKeys = new List<SilverKeyNode>();
        fxModelPaths = new Dictionary<uint, string>();
        fxAttachTo = new Dictionary<uint, Node3D>();

        byte[]? gdbBytes = Engine_LevelGdb.TryRead(level, read, out gdbPath);
        if (gdbBytes is null)
        {
            return false;
        }

        if (!GdbView.TryParseShared(gdbBytes, out view, out string why))
        {
            LogLine($"[props] {level}: level gdb unreadable ({why})");
            return false;
        }

        string savePath = Engine_LevelGdb.SiblingWithExtension(gdbPath, ".save");
        bool isEditor = createSilverKeyNodes && (Engine.IsEditorHint() || Engine_LevelScene.ForceEditorMode);
        byte[]? saveBytes = read(savePath);
        SaveLayerStats layers;

        if (isEditor)
        {
            GdbSaveDocument saveDoc = Engine_GdbSaveMap.ParseDocument(saveBytes, out layers);
            editorHierarchy = new EditorPropHierarchy(saveDoc, gdbPath);
            IReadOnlyList<GdbSaveEntityEntry> allSaveEntries = saveDoc.GetAllEntities(includeInitiallyOff: true);
            saveEntities = new List<SaveEntity>(allSaveEntries.Count);
            foreach (GdbSaveEntityEntry entry in allSaveEntries)
            {
                saveEntities.Add(entry.ToRuntimeSaveEntity());
            }
        }
        else
        {
            saveEntities = Engine_GdbSaveMap.ParseEntities(saveBytes, out layers);
        }

        var saveMap = new List<KeyValuePair<uint, string>>(saveEntities.Count);
        foreach (SaveEntity entity in saveEntities)
        {
            if (Refii.Core.Engine_TitleGame.Active.IsTitleRecord(entity.Record)) continue;
            saveMap.Add(new KeyValuePair<uint, string>(entity.Record, entity.Name));
        }

        stats.SaveEntities = saveMap.Count;

        // THE LAYER GATE, out loud. Everything downstream is built from this list, so if it is
        // ever wrong the whole region is wrong, and the way that failure looks — a street with
        // holes in it — is indistinguishable by eye from a dozen other defects.
        LogLine($"[layers] {level}: {layers}");

        placements = Engine_GdbPlacements.Parse(view, saveMap);
        supplementalViews = preloadedSupplemental is { Count: > 0 }
            ? new List<GdbView>(preloadedSupplemental)
            : LoadSupplementalViews(read);
        AugmentPlacementModelHashes(placements, supplementalViews, stats);
        AppendSupplementalPlacements(supplementalViews, saveMap, placements, stats, editorHierarchy);
        editorHierarchy?.IndexPlacements(placements);
        stats.Placements = placements.Count;

        // A placement that carries a light keeps a node of its own: Engine_LevelLights removes its
        // model from the casters of the light inside it, which a MultiMesh could only do for
        // every copy at once.
        lightHosts =
            Refii.Lighting.Engine_LevelLights.LightBearing(view, supplementalViews, placements);

        var recordKeys = new HashSet<string>();
        var transformKeys = new HashSet<string>();

        AppendEngineLevelBlocks(
            read, Engine_LevelGdb.EngineLevelPath(level, read, gdbPath),
            blocks, order, transformKeys, stats, bakes);

        // The title's own buildings: a stand-in that is really another model, the parts a
        // building brings, the shells it names by entity alone (Fable 2's Bowerstone market).
        Engine_TitleProps props = Refii.Core.Engine_TitleGame.Active.Props;
        bool levelPlacesBase = props.PlacesSubstitutedBase(placements);

        PropBuildStats buildStats = stats;
        Dictionary<string, PropBlockBuild> blockMap = blocks;
        List<string> blockOrder = order;
        void AppendCompanionInstance(string keyPrefix, string path, Transform3D transform, uint ownerHash = 0)
        {
            if (path.Length == 0 ||
                Engine_PropModelIndex.ResolveByPath(path) is not string resolved ||
                IsCharacterModel(resolved) ||
                !transformKeys.Add(TransformKey(transform, resolved)))
            {
                return;
            }

            string companionKey = keyPrefix + "|" + resolved;
            if (!blockMap.TryGetValue(companionKey, out PropBlockBuild? companionBlock))
            {
                companionBlock = new PropBlockBuild(resolved, $"prop {NameOf(resolved)}");
                blockMap[companionKey] = companionBlock;
                blockOrder.Add(companionKey);
            }

            companionBlock.AddInstance(transform, ownerHash);
            buildStats.Instances++;
        }

        foreach (GdbPlacement placement in placements)
        {
            string entityName = placement.EntityName ?? string.Empty;

            if (placement.SkeletonFileHash != 0 || placement.RetargetSkeletonFileHash != 0)
            {
                stats.Skeletal++;
                continue;
            }

            if (placement.ModelPathHash == 0 && entityName.Length == 0)
            {
                stats.Unnamed++;
                continue;
            }

            string? modelPath = placement.ModelPathHash != 0
                ? Engine_PropModelIndex.Resolve(
                    placement.ModelPathHash, Array.Empty<string>())
                : null;

            if (modelPath is null)
            {
                if (placement.ModelPathHash == 0)
                {
                    stats.NoModelHash++;

                    if (placement.StatesAppearance)
                    {
                        stats.NoModelHashWithAppearance++;
                    }
                }
                else
                {
                    stats.UnresolvedModel++;
                }

                continue;
            }

            {
                string want = GetEnv("REFII_PROPS_DUMP");
                if (want.Length != 0 && entityName.Length != 0 &&
                    entityName.Contains(want, StringComparison.OrdinalIgnoreCase))
                {
                    LogLine(
                        $"[props-dump] {entityName} hash={placement.Hash:X8} " +
                        $"modelHash={placement.ModelPathHash:X8} " +
                        $"parent={placement.ParentHash:X8} at=({placement.X:F2},{placement.Y:F2},{placement.Z:F2}) -> {modelPath}");
                }
            }

            if (IsCharacterModel(modelPath))
            {
                stats.RejectedCharacter++;
                continue;
            }

            fxModelPaths[placement.Hash] = modelPath;

            if (Engine_LevelFx.IsSilverKeyPlacement(placement) &&
                !NormalisePath(modelPath).Contains("silverkey", StringComparison.Ordinal))
            {
                continue;
            }

            string? substituteCompanion = null;
            if (props.SubstituteModel(placement, entityName, modelPath, levelPlacesBase, out string? companion) is { } substitute)
            {
                substituteCompanion = companion;
                modelPath = substitute;
            }

            if (IsUniqueEntityShellModel(modelPath) && !HasWorldishPosition(placement))
            {
                stats.ShellBadPosition++;
                continue;
            }

            if (!recordKeys.Add(RecordKey(placement, modelPath)))
            {
                stats.DuplicateRecord++;
                continue;
            }

            Transform3D transform = BuildTransform(placement);
            if (!transformKeys.Add(TransformKey(transform, modelPath)))
            {
                stats.DuplicateTransform++;
                continue;
            }

            if (Engine_LevelFx.IsSilverKeyPlacement(placement))
            {
                if (createSilverKeyNodes)
                {
                    var keyNode = new SilverKeyNode
                    {
                        Transform = transform,
                        BaseTransform = transform,
                    };
                    keyNode.SetMeta(Engine_PropStreamer.ModelPathMeta, modelPath);
                    keyNode.SetMeta(Engine_PropStreamer.RecordMeta, (long)placement.Hash);
                    fxAttachTo[placement.Hash] = keyNode;
                    silverKeys.Add(keyNode);
                }
                stats.Instances++;
                continue;
            }

            string key = "prop|" + modelPath;
            if (!blocks.TryGetValue(key, out PropBlockBuild? block))
            {
                block = new PropBlockBuild(modelPath, $"prop {NameOf(modelPath)}")
                {
                    ModelClass = Engine_PropDrawRange.ClassOf(modelPath),
                };
                blocks[key] = block;
                order.Add(key);
            }

            StatedRange? placedRange = block.ModelClass == PropModelClass.Tree
                ? StatePlacedTree(block, placement, view, supplementalViews, blocks, stats)
                : null;
            block.AddInstance(
                transform,
                placement.Hash,
                ((ulong)placement.ModelPathHash << 32) | placement.Hash,
                range: placedRange);
            stats.Instances++;
            if (createSilverKeyNodes && Engine_GdbPlacements.UsesDisplacement(view, placement.Hash))
            {
                Refii.Models.Engine_ClassicDisplacement.NoteRecord(placement.Hash);
            }

            if (props.CompanionParts(modelPath, out string partsTag) is { Count: > 0 } parts)
            {
                if (substituteCompanion is not null)
                {
                    AppendCompanionInstance(partsTag, substituteCompanion, transform, placement.Hash);
                }

                foreach (string partPath in parts)
                {
                    AppendCompanionInstance(partsTag, partPath, transform, placement.Hash);
                }
            }
        }

        AppendCompanionBlocks(blocks, order, transformKeys, stats);

        return stats.Instances != 0;
    }

    public static Node3D? Build(
        LevelEntry level, Func<string, byte[]?> read, out PropBuildStats stats) =>
        Build(level, read, null, null, null, null, out stats);

    public static Node3D? Build(
        LevelEntry level,
        Func<string, byte[]?> read,
        Refii.Lighting.LightProbeField? probes,
        Refii.Lighting.LightingFrame? lighting,
        out PropBuildStats stats) =>
        Build(level, read, probes, lighting, null, null, out stats);

    public static Node3D? Build(
        LevelEntry level,
        Func<string, byte[]?> read,
        Refii.Lighting.LightProbeField? probes,
        Refii.Lighting.LightingFrame? lighting,
        Refii.Lighting.LightmapContainer? bakes,
        Refii.Entities.EntityInspectData? inspect,
        out PropBuildStats stats) =>
        Build(level, read, probes, lighting, bakes, inspect, null, out stats);

    public static Node3D? Build(
        LevelEntry level,
        Func<string, byte[]?> read,
        Refii.Lighting.LightProbeField? probes,
        Refii.Lighting.LightingFrame? lighting,
        Refii.Lighting.LightmapContainer? bakes,
        Refii.Entities.EntityInspectData? inspect,
        IReadOnlyList<GdbView>? preloadedSupplemental,
        out PropBuildStats stats) =>
        Build(level, read, probes, lighting, bakes, inspect, preloadedSupplemental, captureLightingBakeSnapshot: false, out _, out stats);

    /// <summary>
    /// Builds the region's detached prop hierarchy and placed lights, optionally capturing a thread-safe
    /// <see cref="StaticLightingBakeSnapshot"/> of all resolved static geometry and lights when
    /// <paramref name="captureLightingBakeSnapshot"/> is true so uncached lighting bakes can start immediately
    /// without re-parsing GDBs or GMDs.
    /// </summary>
    public static Node3D? Build(
        LevelEntry level,
        Func<string, byte[]?> read,
        Refii.Lighting.LightProbeField? probes,
        Refii.Lighting.LightingFrame? lighting,
        Refii.Lighting.LightmapContainer? bakes,
        Refii.Entities.EntityInspectData? inspect,
        IReadOnlyList<GdbView>? preloadedSupplemental,
        bool captureLightingBakeSnapshot,
        out StaticLightingBakeSnapshot? lightingBakeSnapshot,
        out PropBuildStats stats)
    {
        lightingBakeSnapshot = null;
        if (!TryCollectRegionBlocks(
            level, read, bakes, inspect, createSilverKeyNodes: true, preloadedSupplemental,
            out string gdbPath, out GdbView view, out List<SaveEntity> saveEntities,
            out EditorPropHierarchy? editorHierarchy, out List<GdbPlacement> placements,
            out List<GdbView> supplementalViews, out HashSet<uint> lightHosts,
            out Dictionary<string, PropBlockBuild> blocks, out List<string> order,
            out List<SilverKeyNode> silverKeys, out Dictionary<uint, string> fxModelPaths,
            out Dictionary<uint, Node3D> fxAttachTo, out stats))
        {
            return null;
        }

        if (OS.GetEnvironment("REFII_VERIFY_BAKE_FIT") == "1")
        {
            ReportVertexBakeFit(bakes, blocks, order);
        }

        var bakeKeys = new HashSet<ulong>();
        if (bakes is not null)
        {
            foreach (KeyValuePair<ulong, uint[]> bake in bakes.VertexBakes)
            {
                bakeKeys.Add(bake.Key);
            }

            // Texture-baked placements (the amb3 interiors) carry the same composed key in the
            // object-lightmap map; without this they never got a BakeKeyMeta and the blobs sat
            // parsed but unconsumed.
            foreach (KeyValuePair<ulong, byte[]> bake in bakes.ObjectLightmapBlobs)
            {
                bakeKeys.Add(bake.Key);
            }

            // The 48-byte blocks are NOT bake keys: the title reads them for skinned models
            // only (`sub_82219178`); a static with no stream and no texture is flat.
        }

        // THE TITLE DRAWS EVERY BAKED PLACEMENT ON ITS OWN. A vertex bake is that placement's
        // colours and a lightmap its texture (`sub_82219978`, Amb 1/3); only the bake-less
        // repeated family is instanced (`sub_82223C68`). The classic path can only bind a bake
        // to a MeshInstance3D (Engine_PropStreamer.FinishVertexBake), so under it a baked
        // placement leaves its batch — the hall's tiles were batches drawing flat ambient.
        bool classicBakedSingles = Refii.Models.Engine_ClassicMaterial.Active;

        // FABLE 3'S ONLY PER-PLACEMENT BAKE IS THE TEXTURE, and it leaves the batch under every
        // lighting model. The .cflmp has no vertex bakes and a v47 rigid part no colour stream;
        // a draw takes Amb 3 when its mesh's GlobalLightingMode (header byte 88, mesh+132) is 2
        // and the table holds its key (`sub_82C88C88` 0x82c88d14..0x82c88e24), else the probe
        // or nothing. A MultiMesh instance cannot carry its own lightmap rect.
        bool lightmappedSingles = bakes is { SharedKeyTable: true };
        if (lightmappedSingles)
        {
            Refii.Lighting.Engine_LevelBakedLighting.AdoptContainer(gdbPath, bakes!);
        }

        // Diagnostics only. A composed key is (modelPathHash << 32 | placementHash); when the
        // whole key misses, the half that still matches something in the .lmp says which half
        // is wrong. Matching neither means the placement is not in the file.
        var bakeKeyLowHalves = new HashSet<uint>();
        var bakeKeyHighHalves = new HashSet<uint>();
        foreach (ulong k in bakeKeys)
        {
            bakeKeyLowHalves.Add((uint)(k & 0xFFFFFFFFul));
            bakeKeyHighHalves.Add((uint)(k >> 32));
        }

        int missPlacementOnly = 0, missModelOnly = 0, missBoth = 0, missZero = 0, keyed = 0;
        var missModels = new Dictionary<string, int>(StringComparer.Ordinal);
        var missKeys = new Dictionary<ulong, int>();

        var container = new Engine_PropStreamer { Name = NodeName };
        var names = new ChildNames(container);

        // The streamer reads the .lmp back out of the bank when it needs a bake, so the saved
        // scene carries the path rather than a million baked colours.
        container.SetMeta(Refii.Lighting.Engine_LevelBakedLighting.LevelGdbMeta, gdbPath);

        foreach (SilverKeyNode keyNode in silverKeys)
        {
            // Same route every other prop takes: the probe sample is STORED on the node and the
            // streamer turns it into shader state once the mesh is standing. A key carries its
            // mesh from the start, so it is lit here too — but through the stored sample, not a
            // second lighting path of its own.
            Refii.Lighting.Engine_LevelBakedLighting.StoreProbeSample(
                keyNode, keyNode.BaseTransform.Origin, probes);
            Refii.Lighting.Engine_LevelBakedLighting.ApplyInstanceLighting(
                keyNode, lighting, staticProp: true);
            if (editorHierarchy is not null)
            {
                editorHierarchy.AttachSilverKey(container, keyNode);
            }
            else
            {
                keyNode.Name = names.Take("SilverKey");
                container.AddChild(keyNode);
                names.Placed(keyNode);
            }
        }

        int singleInstances = 0;

        void AddSingle(PropBlockBuild block, int index)
        {
            StatedRange? range = index < block.Ranges.Count ? block.Ranges[index] : null;
            if (editorHierarchy is not null)
            {
                // In Editor mode, a 1:1 selectable tree only needs its primary mesh (PropRole.Main).
                // Redundant distant-LOD and shadow proxy twins are omitted so the scene tree stays lean.
                AddSingleAs(block, index, PropRole.Main, block.ModelPath, range);
                return;
            }

            foreach ((PropRole role, string path) in block.Roles(range))
            {
                AddSingleAs(block, index, role, path, range);
            }
        }

        void AddSingleAs(PropBlockBuild block, int index, PropRole role, string modelPath, StatedRange? range)
        {
            // A local, not `stats`: a local function cannot capture an `out` parameter.
            if (role == PropRole.Main)
            {
                ++singleInstances;
            }

            var single = new MeshInstance3D
            {
                Layers = block.LayersFor(role),
                Name = editorHierarchy is not null ? block.Label : names.Take(role == PropRole.Main ? block.Label : $"{block.Label} {role}"),
                Transform = block.Instances[index],
            };

            if (block.Foliage)
            {
                single.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            }

            block.Configure(single, role, range);
            block.MarkCasting(single, role, block.CastingAt(index), range);
            single.SetMeta(ModelPathMetaName, modelPath);

            uint hash = index < block.InstanceHashes.Count ? block.InstanceHashes[index] : 0;

            // WHICH `.save` RECORD THIS IS, carried onto the node so the running game can tell
            // that the guest is about to place the same thing itself. The editor ignores it.
            if (hash != 0)
            {
                single.SetMeta(RecordMetaName, (long)hash);
            }

            // The guest's order: the `.lmp`'s baked vertex-colour buffer (map manager+4), then the
            // `.lmp`'s object lightmap texture (manager+16), then SH light probes. There is no
            // separate mesh-owned per-vertex stage. `sub_82222F70` sets the resolve state to 1
            // BEFORE either lookup, so a miss falls through to probes (`sub_82219978` picks
            // packet mode 2 when the level has a probe array, else 0).
            //
            // The key is one u64, matched whole with `cmpld` at 0x82A57338:
            // `(model resource id << 32) | GDB record name hash`, assembled at
            // 0x82237250..0x8223726C, both halves FNV-1. The high half is shared by every
            // placement of a model, so never match on a half; the halves below are diagnostics.
            //
            // A key that matches nothing is not automatically a defect: `0`, a zero high half
            // (`sub_82209480`'s four sites) and the `{1, 0}` default (`sub_82456D58`
            // 0x82456E48) all mean "no baked lighting", and falling through to probes is the
            // designed outcome.
            //
            // Only the key is stored; the streamer resolves it once the mesh is standing and
            // can check that it fits.
            // A bake is keyed to the MAIN model's vertices; the LOD and shadow models take the
            // probe field, and are not counted as placements of their own.
            ulong bakeKey = role == PropRole.Main && index < block.InstanceBakeKeys.Count
                ? block.InstanceBakeKeys[index]
                : 0;

            if (role == PropRole.Main && bakeKey != 0 && bakeKeys.Contains(bakeKey))
            {
                ++keyed;
                single.SetMeta(
                    Refii.Lighting.Engine_LevelBakedLighting.BakeKeyMeta, $"0x{bakeKey:X16}");

                // Bound when the mesh stands, under either model (Engine_LevelBakedLighting
                // .ApplyInstanceLighting): the level's path rides along so the node can find it.
                if (lightmappedSingles && bakes!.ObjectLightmapBlobs.ContainsKey(bakeKey))
                {
                    single.SetMeta(
                        Refii.Lighting.Engine_LevelBakedLighting.ObjectLightmapLevelMeta, gdbPath);
                }
            }
            else if (role == PropRole.Main)
            {
                if (bakeKey == 0)
                {
                    ++missZero;
                }
                else
                {
                    bool lowSeen = bakeKeyLowHalves.Contains((uint)(bakeKey & 0xFFFFFFFFul));
                    bool highSeen = bakeKeyHighHalves.Contains((uint)(bakeKey >> 32));
                    if (lowSeen && !highSeen)
                    {
                        ++missPlacementOnly;
                    }
                    else if (highSeen && !lowSeen)
                    {
                        ++missModelOnly;
                    }
                    else
                    {
                        ++missBoth;

                        // Which keys miss, not just how many: `{1,0}` and a zero high half are
                        // legal no-bake shapes, not defects.
                        missKeys.TryGetValue(bakeKey, out int keySeen);
                        missKeys[bakeKey] = keySeen + 1;
                    }
                }

                string name = NameOf(block.ModelPath);
                missModels.TryGetValue(name, out int seen);
                missModels[name] = seen + 1;
            }

            // An authored per-instance colour outranks the probe field — same precedence the
            // guest gives amb1 over PRT. It rides the probe channel as a flat sample: L0 is
            // scaled so the shader's basis multiply recovers pow(c, 2.2), and its 1.3 then
            // completes the vertex-bake compose.
            uint bakeColour = index < block.InstanceBakeColours.Count
                ? block.InstanceBakeColours[index]
                : 0;
            if (bakeColour != 0)
            {
                Refii.Lighting.Engine_LevelBakedLighting.StoreFlatBakeColour(
                    single, bakeColour);
            }
            else
            {
                Refii.Lighting.Engine_LevelBakedLighting.StoreProbeSample(
                    single, block.Instances[index].Origin, probes);
            }

            if (role == PropRole.Main &&
                Refii.Lighting.Engine_LevelAmbientOcclusion.TryGetPlacementAoFactor(
                    block.ModelPath, block.Instances[index], out float placementAo))
            {
                single.SetMeta(Refii.Lighting.Engine_LevelBakedLighting.InstanceBakedAoMetaName, placementAo);
            }

            if (inspect is not null && hash != 0 && role == PropRole.Main)
            {
                Refii.Entities.Engine_EntityInspect.Annotate(single, hash, inspect);
            }

            if (editorHierarchy is not null)
            {
                editorHierarchy.AttachSingleProp(container, single, block.Label, role, hash);
            }
            else
            {
                container.AddChild(single);
                names.Placed(single);
            }
        }

        float foliageCells = editorHierarchy is null ? ReadFoliageCellMetres() : 0f;
        float propCells = editorHierarchy is null ? ReadPropCellMetres() : 0f;
        float foliageDensity = editorHierarchy is null ? ReadFoliageDensity() : 1f;
        foreach (string key in order)
        {
            PropBlockBuild block = blocks[key];

            if (editorHierarchy is null && block.Instances.Count <= SelectableInstanceLimit)
            {
                stats.SmallBlocks++;
                stats.SmallBlockInstances += block.Instances.Count;
                for (int i = 0; i < block.Instances.Count; i++)
                {
                    AddSingle(block, i);
                }

                stats.Blocks++;
                continue;
            }

            var batched = new List<int>(block.Instances.Count);
            for (int i = 0; i < block.Instances.Count; i++)
            {
                bool forcedSingle = i < block.InstanceHashes.Count &&
                    ((editorHierarchy is not null && block.InstanceHashes[i] != 0) ||
                        (inspect is not null && inspect.HasFor(block.InstanceHashes[i])) ||
                        lightHosts.Contains(block.InstanceHashes[i]));
                forcedSingle |= classicBakedSingles && i < block.InstanceBakeKeys.Count &&
                    bakeKeys.Contains(block.InstanceBakeKeys[i]);
                forcedSingle |= lightmappedSingles && i < block.InstanceBakeKeys.Count &&
                    bakes!.ObjectLightmapBlobs.ContainsKey(block.InstanceBakeKeys[i]);
                if (forcedSingle)
                {
                    stats.ForcedSingles++;
                    AddSingle(block, i);
                }
                else
                {
                    batched.Add(i);
                }
            }

            if (OS.GetEnvironment("REFII_DUMP_BATCH").Length != 0 &&
                block.ModelPath.Contains("MarketStall", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < batched.Count; i++)
                {
                    int idx = batched[i];
                    uint h = idx < block.InstanceHashes.Count ? block.InstanceHashes[idx] : 0;
                    Vector3 o = block.Instances[idx].Origin;
                    GD.Print($"[batch] {NameOf(block.ModelPath)} elem={i} hash={h:x8} " +
                        $"at=({o.X:0.##},{o.Y:0.##},{o.Z:0.##})");
                }
            }

            stats.BatchedInstances += batched.Count;
            if (batched.Count == 0)
            {
                stats.Blocks++;
                continue;
            }

            // ONE BATCH PER MODEL PER CELL. Godot culls and shadow-pairs a MultiMesh as one
            // box: a region-wide batch is inside every light's range and every cascade, so each
            // of them drew every placement of the model. The lists keep BLOCK indices — bake
            // keys, colours and records are all looked up by them.
            //
            // AND ONE PER STATED RANGE: a batch switches as one node, so placements the file gives
            // different distances cannot share one.
            var cells = new Dictionary<(Vector3I Cell, StatedRange? Range, byte Casting), List<int>>();
            float cellMetres = CellMetresFor(block, foliageCells, propCells);
            foreach (int index in batched)
            {
                if (!KeepsFoliageCopy(block, index, foliageDensity)) continue;
                Vector3 at = block.Instances[index].Origin;
                var cell = (new Vector3I(Mathf.FloorToInt(at.X / cellMetres),
                    Mathf.FloorToInt(at.Y / cellMetres),
                    Mathf.FloorToInt(at.Z / cellMetres)),
                    index < block.Ranges.Count ? block.Ranges[index] : null,
                    index < block.InstanceCasting.Count ? block.InstanceCasting[index]
                        : Refii.Models.Engine_ClassicShadows.CastsDefault);
                if (!cells.TryGetValue(cell, out List<int>? indices))
                {
                    cells[cell] = indices = new List<int>();
                }
                indices.Add(index);
            }

            var distinctCells = new HashSet<Vector3I>();
            foreach ((Vector3I cellOnly, StatedRange? _, byte _) in cells.Keys)
            {
                distinctCells.Add(cellOnly);
            }

            stats.RangeSplitBatches += cells.Count - distinctCells.Count;
            MergeSparseCells(block, cells, stats);

            foreach (KeyValuePair<(Vector3I Cell, StatedRange? Range, byte Casting), List<int>> celled in cells)
            foreach ((PropRole role, string rolePath) in block.Roles(celled.Key.Range))
            {
                List<int> spatialBatch = celled.Value;

                // UseColors BEFORE InstanceCount — Godot fixes the buffer format when the count is
                // set, and turning colours on afterwards silently does nothing.
                var multiMesh = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    UseColors = true,
                    InstanceCount = spatialBatch.Count,
                };

                // THE AABB IS SET BEFORE THE FIRST TRANSFORM, and that ordering is the whole point.
                //
                // The mesh arrives later, from Engine_PropStreamer. Writing an instance transform
                // marks the multimesh's bounds dirty, and on the next frame Godot recomputes them
                // from the mesh unless a custom AABB was set — `_multimesh_re_create_aabb`
                // (mesh_storage.cpp:1836), which opens `ERR_FAIL_COND(multimesh->mesh.is_null())`.
                // Setting the bounds afterwards is too late: the dirty flag is already up. Bounds
                // are therefore measured in their own pass first, which costs one extra walk of a
                // list already in memory.
                // THE PLACEMENTS' MIRROR GOES ON THE NODE, NOT IN THE COPIES. Every placement in
                // the level data carries a reflection (the region root carries the other one), and
                // Godot decides which face to cull from the INSTANCE's transform: it sees a node's
                // reflection and cannot see one inside a batch's buffer. Left in the copies, every
                // batched prop culled its near side and showed its far side's inside — measured
                // 2026-09-22, all 19,045 of the Market's copies reflected, a house without its
                // front wall. So a reflected batch's node is `BatchMirror` and each copy is
                // `BatchMirror * placement`: the same place, the same shape, and a handedness the
                // renderer can read. A batch mixing both kinds keeps the copies as stated.
                int reflected = 0;
                for (int i = 0; i < spatialBatch.Count; i++)
                {
                    if (block.Instances[spatialBatch[i]].Basis.Determinant() < 0f)
                    {
                        ++reflected;
                    }
                }

                bool mirrorOnNode = reflected == spatialBatch.Count;
                Transform3D toBatch = mirrorOnNode ? BatchMirror : Transform3D.Identity;

                var bounds = new Aabb(toBatch * block.Instances[spatialBatch[0]].Origin, Vector3.Zero);
                for (int i = 0; i < spatialBatch.Count; i++)
                {
                    bounds = bounds.Expand(toBatch * block.Instances[spatialBatch[i]].Origin);
                }
                multiMesh.CustomAabb = bounds.Grow(ModelReach);

                // THE WHOLE BATCH IS WRITTEN IN C# AND UPLOADED ONCE, below, after the colours: a
                // `SetInstanceTransform` and a `SetInstanceColor` per copy were two native calls,
                // each a queued server command under the separate render thread, 38k of them for
                // a region's 19k batched copies. Slot layout: the transform's twelve floats
                // (Engine_PropShadow.WriteSlot), then the probe colour's four.
                var built = new Transform3D[spatialBatch.Count];
                var slots = new float[spatialBatch.Count * BatchStride];
                for (int i = 0; i < spatialBatch.Count; i++)
                {
                    built[i] = toBatch * block.Instances[spatialBatch[i]];
                    Refii.Level.Engine_PropShadow.WriteSlot(slots, i * BatchStride, built[i]);
                    slots[i * BatchStride + 15] = 1.0f;
                }

                // Kept beside the batch, so nothing later has to read the buffer back through
                // the render thread (Engine_PropShadow.Stated).
                Refii.Level.Engine_PropShadow.RecordBuilt(multiMesh, built);

                // ONE PROBE SAMPLE PER INSTANCE, not one per block.
                //
                // A block is every placement of the same model in the region — this level's foliage
                // is 10,836 instances across 21 models — so a single sample taken at the centre of
                // their combined bounds lit every tree in the level identically, from a point that
                // is as likely to be inside a building as under open sky. Godot's per-instance
                // colour is the native place for this and it survives the pack, which the node's
                // instance uniforms do not.
                //
                // The L1 bands are dropped here and the flat L0 kept: a MultiMesh instance carries
                // one colour, and L0 is the dominant term by a wide margin (0.60 average against
                // ±0.27 for L1 across this region).
                // THE BATCHED SUBSET, not block.Instances. The inspector pulls some placements out
                // of the batch and into their own nodes, so the MultiMesh holds fewer instances than
                // the block does — sampling the full list would pair instance N's buffer slot with
                // placement N's position and slide every colour along by however many were pulled.
                var batchedTransforms = new List<Transform3D>(spatialBatch.Count);
                foreach (int index in spatialBatch)
                {
                    batchedTransforms.Add(block.Instances[index]);
                }

                var batchedBakeKeys = new List<ulong>(spatialBatch.Count);
                var batchedBakeColours = new List<uint>(spatialBatch.Count);
                foreach (int index in spatialBatch)
                {
                    batchedBakeKeys.Add(index < block.InstanceBakeKeys.Count
                        ? block.InstanceBakeKeys[index]
                        : 0);
                    batchedBakeColours.Add(index < block.InstanceBakeColours.Count
                        ? block.InstanceBakeColours[index]
                        : 0);
                }

                float[]? batchedAoFactors = null;
                if (role == PropRole.Main && Refii.Lighting.Engine_LevelAmbientOcclusion.Current is not null)
                {
                    batchedAoFactors = new float[spatialBatch.Count];
                    for (int i = 0; i < spatialBatch.Count; i++)
                    {
                        int index = spatialBatch[i];
                        batchedAoFactors[i] = Refii.Lighting.Engine_LevelAmbientOcclusion.TryGetPlacementAoFactor(
                            block.ModelPath, block.Instances[index], out float aoFactor)
                            ? aoFactor
                            : 1.0f;
                    }
                }

                bool coloured = Refii.Lighting.Engine_LevelBakedLighting.ApplyMultiMeshProbes(
                    slots, BatchStride, batchedTransforms, probes, bakes, batchedBakeKeys,
                    batchedBakeColours, batchedAoFactors);

                // ONE SLOT WRITE FIRST, THEN THE BULK BUFFER, and the order is what keeps the
                // server off the GPU. A bulk set on a multimesh with no CPU copy leaves it with
                // none (mesh_storage.cpp `_multimesh_set_buffer`), and then the first single-slot
                // write (StaticTwins, Engine_PropShadow.SetInstanceTransform), slot read, or the
                // streamer's later `multimesh_set_mesh` pulls the whole buffer back off the GPU
                // (`buffer_get_data`, a stall). The single write on a fresh multimesh builds that
                // copy from zeros, no read; the bulk set then fills it and the GPU buffer
                // together. The server ends holding the same CPU copy the per-slot fill left it.
                multiMesh.SetInstanceTransform(0, built[0]);
                RenderingServer.MultimeshSetBuffer(multiMesh.GetRid(), (ReadOnlySpan<float>)slots);

                // The cell rides in the name: `UniqueName` probes the container once per taken
                // name, and a model with hundreds of cells under one label is that squared.
                Vector3I at = celled.Key.Cell;
                string label = role == PropRole.Main ? block.Label : $"{block.Label} {role}";
                string batchName = cells.Count == 1 ? label : $"{label} [{at.X} {at.Y} {at.Z}]";
                var node = new MultiMeshInstance3D
                {
                    Layers = block.LayersFor(role),
                    Name = editorHierarchy is not null ? batchName : names.Take(batchName),
                    Multimesh = multiMesh,
                    Transform = toBatch,
                };

                if (block.Foliage)
                {
                    node.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                }

                // The placements' spread about the batch's centre: Godot measures the range there.
                block.Configure(node, role, celled.Key.Range, bounds.Size.Length() * 0.5f);
                block.MarkCasting(node, role, celled.Key.Casting, celled.Key.Range);
                node.SetMeta(Engine_PropStreamer.ModelPathMeta, rolePath);
                node.SetMeta(Engine_PropStreamer.BatchBoundsMeta, bounds);
                node.SetMeta(Engine_PropStreamer.BatchSpreadMeta, bounds.Size.Length() * 0.5f);

                // THE BATCHED SUBSET AGAIN, for the same reason the probe colours use it: the
                // inspector pulls placements out of the batch into nodes of their own, so instance
                // N of the MultiMesh is not placement N of the block. A column indexed off the full
                // list would name the wrong record for every instance after the first pulled one,
                // and retiring by it would delete a prop that is still the guest's only copy.
                var records = new long[spatialBatch.Count];
                bool anyRecord = false;
                for (int i = 0; i < spatialBatch.Count; i++)
                {
                    int index = spatialBatch[i];
                    records[i] = index < block.InstanceHashes.Count
                        ? block.InstanceHashes[index]
                        : 0L;
                    anyRecord |= records[i] != 0L;
                }

                if (anyRecord)
                {
                    node.SetMeta(Engine_PropStreamer.RecordsMeta, records);
                }

                if (coloured)
                {
                    node.SetMeta(Refii.Lighting.Engine_LevelBakedLighting.BakeModeMeta, 2);
                }
                else
                {
                    Refii.Lighting.Engine_LevelBakedLighting.StoreProbeSample(
                        node, bounds.GetCenter(), probes);
                }

                if (editorHierarchy is not null)
                {
                    editorHierarchy.AttachBatchedNode(container, node, batchName);
                }
                else
                {
                    container.AddChild(node);
                    names.Placed(node);
                }
            }

            stats.Blocks++;
        }

        ReportFoliageThinning(level);
        if (editorHierarchy is not null)
        {
            foreach (KeyValuePair<uint, Node3D> kv in editorHierarchy.PrimaryRecordNodes)
            {
                fxAttachTo.TryAdd(kv.Key, kv.Value);
            }
        }

        // In a game whose guest states its level's effects, those statements draw them; spawning
        // authored effects from the level data as well drew each one twice. Only host-synthesized
        // fallbacks for base-game GDB omissions (`NewLamp_on_stick`) are spawned here.
        bool guestOwnsLevelFx = Refii.Fx.Engine_LiveFx.GuestOwnsLevelEffects(Refii.Core.Engine_RefiiConfig.Read(out _));
        int levelFx = Engine_LevelFx.Spawn(
            container, view, supplementalViews, placements, fxModelPaths, fxAttachTo, probes,
            syntheticOnly: guestOwnsLevelFx);
        if (guestOwnsLevelFx)
        {
            GD.Print(levelFx != 0
                ? $"[props] {level}: level FX left to the guest ({levelFx} unauthored lantern FX synthesized)"
                : $"[props] {level}: level FX left to the guest, which states them");
        }
        else if (levelFx != 0)
        {
            GD.Print($"[props] {level}: {levelFx} level FX spawned from bank");
        }

        // THE REGION'S OWN LIGHTS, beside the effects and off the same placements — a torch's
        // flame and the light that flame casts are two statements about one fixture, and until
        // this ran we built the first and not the second.
        int lights = Refii.Lighting.Engine_LevelLights.Spawn(
            container, view, supplementalViews, placements, fxModelPaths,
            out List<Refii.Lighting.Engine_LevelLights.ResolvedLightInstance> resolvedLights,
            out HashSet<uint> resolvedLightHosts);
        if (lights != 0)
        {
            GD.Print($"[props] {level}: {lights} placed lights");
        }

        if (captureLightingBakeSnapshot)
        {
            var snapshotBlocks = new List<StaticShadowPropBlock>(order.Count);
            foreach (string key in order)
            {
                PropBlockBuild b = blocks[key];
                if (b.Foliage || b.Instances.Count == 0)
                {
                    continue;
                }

                snapshotBlocks.Add(new StaticShadowPropBlock(
                    b.ModelPath,
                    b.ShadowModelPath,
                    b.ModelClass,
                    b.Foliage,
                    b.Instances.ToArray(),
                    b.InstanceHashes.ToArray(),
                    b.InstanceCasting.ToArray()));
            }

            var mergedHosts = new HashSet<uint>(lightHosts);
            mergedHosts.UnionWith(resolvedLightHosts);
            var hostModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (uint h in mergedHosts)
            {
                if (fxModelPaths.TryGetValue(h, out string? mp) && !string.IsNullOrEmpty(mp))
                {
                    hostModels.Add(mp);
                }
            }

            lightingBakeSnapshot = new StaticLightingBakeSnapshot
            {
                GdbPath = gdbPath,
                PropBlocks = snapshotBlocks,
                LightHosts = mergedHosts,
                LightHostModels = hostModels,
                ResolvedLights = new List<Refii.Lighting.Engine_LevelLights.ResolvedLightInstance>(resolvedLights),
            };
        }

        if (container.GetChildCount() == 0)
        {
            container.QueueFree();
            return null;
        }

        StoreLayerMap(container, saveEntities);

        if (OS.GetThreadCallerId() != OS.GetMainThreadId())
        {
            container.PrefetchAhead();
        }

        if (keyed + missZero + missPlacementOnly + missModelOnly + missBoth != 0)
        {
            var worst = new List<KeyValuePair<string, int>>(missModels);
            worst.Sort((l, r) => r.Value.CompareTo(l.Value));
            var named = new System.Text.StringBuilder();
            for (int i = 0; i < worst.Count && i < 10; i++)
            {
                named.Append(' ').Append(worst[i].Key).Append('x').Append(worst[i].Value);
            }

            GD.Print($"[props] bake keys: keyed={keyed} miss(noKey={missZero} " +
                $"placementHalfMatched={missPlacementOnly} modelHalfMatched={missModelOnly} " +
                $"neither={missBoth}) of {bakeKeys.Count} key(s) in the .lmp —{named}");

            // Printed as {high,low}: high = the model's FNV-1 resource id, low = the GDB record
            // name hash. `{1,0}` (`sub_82456D58` 0x82456E48) and a zero high half
            // (`sub_82209480`) are legal no-bake keys.
            if (missKeys.Count != 0)
            {
                var worstKeys = new List<KeyValuePair<ulong, int>>(missKeys);
                worstKeys.Sort((a, b) => b.Value.CompareTo(a.Value));
                var keyLine = new System.Text.StringBuilder();
                for (int i = 0; i < worstKeys.Count && i < 12; ++i)
                {
                    ulong k = worstKeys[i].Key;
                    keyLine.Append($" {{{(uint)(k >> 32):X8},{(uint)k:X8}}}x{worstKeys[i].Value}");
                }

                GD.Print($"[props] unmatched keys ({missKeys.Count} distinct):{keyLine}" +
                    (missKeys.Count > 12 ? $" (+{missKeys.Count - 12} more)" : string.Empty));

                // Layout check: would the misses match with the halves swapped, or the whole key
                // byte-swapped?
                int swappedHalves = 0, swappedBytes = 0;
                foreach (ulong k in missKeys.Keys)
                {
                    if (bakeKeys.Contains((k >> 32) | (k << 32)))
                    {
                        ++swappedHalves;
                    }

                    if (bakeKeys.Contains(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(k)))
                    {
                        ++swappedBytes;
                    }
                }

                GD.Print($"[props] of {missKeys.Count} unmatched: {swappedHalves} match with the " +
                    $"HALVES SWAPPED, {swappedBytes} with the whole key BYTE-SWAPPED");
            }
        }

        stats.SingleInstances = singleInstances;

        // Every single is a draw call in the visible, shadow and each reflection pass; a batch
        // is one apiece however many instances it holds.
        GD.Print($"[props] instances: {stats.SingleInstances:N0} single, " +
            $"{stats.BatchedInstances:N0} batched | " +
            $"{stats.SmallBlocks:N0} blocks at or under {SelectableInstanceLimit} hold " +
            $"{stats.SmallBlockInstances:N0} of the singles; {stats.ForcedSingles:N0} more are " +
            $"forced out of a batch (light hosts, inspectables)");

        return container;
    }

    /// <summary>Record hashes, parallel to <see cref="LayerKeysMeta"/>.</summary>
    public const string LayerRecordsMeta = "layer_records";

    /// <summary>The layer each record sits in — FNV-1 of the layer `name` attribute.</summary>
    public const string LayerKeysMeta = "layer_keys";

    /// <summary>
    /// The layer keys whose `load` is `AlwaysOn`, as a set.
    ///
    /// Held separately because it is a property of the LAYER, and there are 81 of them against
    /// Ravenscar's 5749 entities. Nothing may ever hide an entity in one of these: the guest's
    /// `AlwaysOn` branch at 0x823664D0 spawns without consulting either runtime list.
    /// </summary>
    public const string AlwaysOnLayersMeta = "layer_alwayson";

    /// <summary>
    /// Writes the record → layer map onto the props container.
    ///
    /// IT RIDES ON THE NODE because the consumer is downstream of the build and has nothing but
    /// the region. `Engine_StaticTwins` decides, every half second, whether the region's copy of
    /// a record should be visible; without the layer it was left inferring that from whether the
    /// entity turned up in a walk of guest memory, and absence from a walk is not a statement.
    ///
    /// The whole `.save` set is stored, not just the placements that drew something — a record
    /// with no prop costs two array slots and its absence would read as "no layer known", which
    /// is the ambiguity this exists to remove.
    /// </summary>
    private static void StoreLayerMap(Node3D container, List<SaveEntity> saveEntities)
    {
        var records = new long[saveEntities.Count];
        var keys = new long[saveEntities.Count];
        var alwaysOn = new HashSet<uint>();

        for (int i = 0; i < saveEntities.Count; i++)
        {
            SaveEntity entity = saveEntities[i];
            records[i] = entity.Record;
            keys[i] = entity.LayerHash;
            if (entity.LayerAlwaysOn && entity.LayerHash != 0)
            {
                alwaysOn.Add(entity.LayerHash);
            }
        }

        var alwaysOnKeys = new long[alwaysOn.Count];
        int at = 0;
        foreach (uint key in alwaysOn)
        {
            alwaysOnKeys[at++] = key;
        }

        container.SetMeta(LayerRecordsMeta, records);
        container.SetMeta(LayerKeysMeta, keys);
        container.SetMeta(AlwaysOnLayersMeta, alwaysOnKeys);
    }

    private static void AppendCompanionBlocks(
        Dictionary<string, PropBlockBuild> blocks,
        List<string> order,
        HashSet<string> transformKeys,
        PropBuildStats stats)
    {
        var additions = new List<KeyValuePair<string, PropBlockBuild>>();

        foreach (string key in order)
        {
            PropBlockBuild source = blocks[key];

            string companion = CompanionPath(source.ModelPath);
            if (companion.Length == 0)
            {
                continue;
            }

            string? resolved = Engine_PropModelIndex.ResolveByPath(companion);
            if (resolved is null)
            {
                continue;
            }

            var build = new PropBlockBuild(resolved, $"prop {NameOf(resolved)}");
            for (int i = 0; i < source.Instances.Count; i++)
            {
                Transform3D transform = source.Instances[i];
                uint ownerHash = i < source.InstanceHashes.Count
                    ? source.InstanceHashes[i]
                    : 0u;
                if (transformKeys.Add(TransformKey(transform, resolved)))
                {
                    build.AddInstance(transform, ownerHash);
                }
            }

            if (build.Instances.Count == 0)
            {
                continue;
            }

            additions.Add(new KeyValuePair<string, PropBlockBuild>(
                "companion|" + resolved, build));
        }

        foreach (KeyValuePair<string, PropBlockBuild> addition in additions)
        {
            if (blocks.ContainsKey(addition.Key))
            {
                continue;
            }

            blocks[addition.Key] = addition.Value;
            order.Add(addition.Key);
            stats.Companions++;
            stats.Instances += addition.Value.Instances.Count;
        }
    }

    public static string NormalisePath(string path) =>
        path.Replace('\\', '/').ToLowerInvariant();

    public static bool IsUniqueEntityShellModel(string modelPath)
    {
        string p = NormalisePath(modelPath);
        if (!p.Contains("/buildings/", StringComparison.Ordinal) &&
            !p.Contains("/structures/", StringComparison.Ordinal))
        {
            return false;
        }

        return p.Contains("/exterior.mdl", StringComparison.Ordinal) ||
               p.Contains("/interior.mdl", StringComparison.Ordinal) ||
               Refii.Core.Engine_TitleGame.Active.Props.IsUniqueShellModel(p);
    }

    public static bool HasWorldishPosition(GdbPlacement p)
    {
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
        {
            return false;
        }

        if (p.X < -64f || p.X > 512f || p.Y < -64f || p.Y > 512f ||
            p.Z < -64f || p.Z > 256f)
        {
            return false;
        }

        const float pi = 3.14159265358979323846f;
        bool looksLikeRotation =
            MathF.Abs(p.X) < 10f &&
            ((MathF.Abs(p.Y) < 0.02f && MathF.Abs(p.Z) < 0.02f) ||
             (MathF.Abs(p.Y + pi) < 0.02f && MathF.Abs(p.Z + pi) < 0.02f));

        return !looksLikeRotation;
    }

    /// <summary>
    /// Does a shipped per-vertex bake actually FIT the mesh we parsed for that placement?
    ///
    /// The key mapping is settled — every one of this region's 586 bakes resolves to a
    /// placement — but a matching key only proves we found the right bake, not that our .mdl
    /// reader produced the same vertices in the same order the bake was authored against. The
    /// guest asserts on size alone (`4 * totalVertices == bufferSize`), and so can we; a
    /// mismatch here kills the whole approach before any of it is built, and an exact match
    /// across hundreds of models is strong evidence the ORDER agrees too.
    ///
    /// A sample, not a sweep: resolving every model at build time is what streaming exists to
    /// avoid.
    /// </summary>
    private static void ReportVertexBakeFit(
        Refii.Lighting.LightmapContainer? bakes,
        Dictionary<string, PropBlockBuild> blocks,
        List<string> order)
    {
        if (bakes is null || bakes.VertexBakes.Count == 0)
        {
            return;
        }

        var byKey = new Dictionary<ulong, int>();
        foreach (KeyValuePair<ulong, uint[]> bake in bakes.VertexBakes)
        {
            byKey[bake.Key] = bake.Value.Length;
        }

        int matched = 0, fits = 0, misfits = 0, unresolved = 0;
        string sample = string.Empty;

        foreach (string key in order)
        {
            if (matched >= 24)
            {
                break;
            }

            PropBlockBuild block = blocks[key];
            for (int i = 0; i < block.Instances.Count && matched < 24; i++)
            {
                if (i >= block.InstanceBakeKeys.Count ||
                    !byKey.TryGetValue(block.InstanceBakeKeys[i], out int bakeVerts))
                {
                    continue;
                }

                ++matched;

                ArrayMesh? mesh = Engine_PropStreamer.Resolve(block.ModelPath);
                if (mesh is null || mesh.GetSurfaceCount() == 0)
                {
                    ++unresolved;
                    continue;
                }

                // The MODEL's vertex count, which is what the bake was built against — not the
                // sum over our surfaces, which double-counts every vertex two submeshes share.
                int meshVerts = mesh.GetMeta(
                    Refii.Models.Engine_MdlMesh.SourceVertexTotalMeta, default) is
                    { VariantType: Variant.Type.Int } totalMeta
                    ? totalMeta.AsInt32()
                    : 0;

                if (meshVerts == bakeVerts)
                {
                    ++fits;
                }
                else
                {
                    ++misfits;
                    if (sample.Length == 0)
                    {
                        // Per surface, still, but no longer to hunt a constant delta — that
                        // question is closed. The old sum over surfaces double-counted every
                        // vertex two submeshes shared, which is why an eight-vertex gap kept
                        // turning up; it now compares the model's own count. A surface breakdown
                        // is still what tells a genuine LOD or build mismatch apart from a
                        // model whose bake simply belongs to a different revision.
                        var perSurface = new System.Text.StringBuilder();
                        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                        {
                            perSurface.Append(
                                $" [{mesh.SurfaceGetName(s)}]=" +
                                Refii.Models.Engine_MdlSurfaceArrays.VertexCount(mesh, s));
                        }

                        sample = $" first misfit: {NameOf(block.ModelPath)} " +
                            $"bake={bakeVerts} mesh={meshVerts} surfaces:{perSurface}";
                    }
                }
            }
        }

        GD.Print($"[bakefit] sampled={matched} fit={fits} misfit={misfits} " +
            $"unresolved={unresolved}{sample}");
    }

    private sealed class PropBlockBuild
    {
        public PropBlockBuild(string modelPath, string label)
        {
            ModelPath = modelPath;
            Label = label;
        }

        public string ModelPath { get; }

        public string Label { get; }

        public bool Foliage { get; set; }

        /// <summary>The guest's render class for the model (`.mdl` header), deciding which rule the ranges follow.</summary>
        public PropModelClass ModelClass { get; set; }

        /// <summary>A TreeMesh's separate `_LOD1` model, resolved, or empty.</summary>
        public string LodModelPath { get; set; } = string.Empty;

        /// <summary>A TreeMesh's cast-only `_shadow` model, resolved, or empty.</summary>
        public string ShadowModelPath { get; set; } = string.Empty;

        /// <summary>Set once the first `.save` placement has stated this tree block's contract.</summary>
        public bool PlacedTreeLogged { get; set; }

        /// <summary>The LOD model came from the level's tree block for the same model, not the definition.</summary>
        public bool PlacedTreeBorrowsLod { get; set; }

        public List<Transform3D> Instances { get; } = new();

        /// <summary>The distances the level file states for each instance; null where the source states none.</summary>
        public List<StatedRange?> Ranges { get; } = new();

        /// <summary>
        /// The placement id behind each instance, or 0 where the source did not carry one.
        ///
        /// Parallel to <see cref="Instances"/>, and load-bearing twice over: the entity
        /// inspector annotates by it, and the .lmp binds a baked vertex buffer to a PLACEMENT
        /// rather than to a model — grouping by model is what lets us share a mesh, and it is
        /// exactly what throws away the identity the bake is keyed on.
        /// </summary>
        public List<uint> InstanceHashes { get; } = new();

        /// <summary>
        /// The full 64-bit key the .lmp files this placement's bake under, or 0 for none.
        /// </summary>
        /// <remarks>
        /// The placement hash alone is NOT the key, and the difference is not academic. The
        /// guest composes the two halves at 0x8223725C —
        ///
        ///     key = ((u64)modelFileHash &lt;&lt; 32) | (u32)placementHash
        ///
        /// — where the high half is the entity's `ModelFile` GDB property and the low half is
        /// its own GDB entry hash. Matching on the low half alone very nearly works, which is
        /// what makes it dangerous: this region ships 572 bakes with only 555 distinct low
        /// halves, so seventeen placements are resolved by whichever entry the walk reaches
        /// first, and a bake built for a different model is then applied vertex-for-vertex.
        /// </remarks>
        public List<ulong> InstanceBakeKeys { get; } = new();

        /// <summary>
        /// The instance's authored bake as a D3DCOLOR (the .lmp u32-array entry for a type-21
        /// foliage placement), or 0 for none. Gamma-encoded like a per-vertex bake — the
        /// consumer decodes by 2.2 and the shader's 1.3 completes the guest's 2.86 compose.
        /// </summary>
        public List<uint> InstanceBakeColours { get; } = new();

        /// <summary>
        /// Which sun cascades each instance casts into under classic (Engine_ClassicShadows.Casts*);
        /// parallel to <see cref="Instances"/>. The guest's defaults where the source states none.
        /// </summary>
        public List<byte> InstanceCasting { get; } = new();

        /// <summary>
        /// The layers a node of this block takes for a role: the main view's and the mirrors'.
        /// Under classic a tree's ShadowModel is drawn by no camera in this world; it only feeds
        /// its caster in the cascades' own scene (Engine_ClassicCasters).
        /// </summary>
        public uint LayersFor(PropRole role) =>
            Refii.Models.Engine_ClassicMaterial.Active && ModelClass == PropModelClass.Tree &&
            role == PropRole.Shadow
                ? 0u
                : Refii.Level.Engine_ReflectionLayer.ReflectedLayers | Refii.Fx.Engine_ModernDecals.StaticDecalReceiverLayerMask;

        /// <summary>
        /// Marks a node with how it casts under classic, for its caster once the mesh is in. A
        /// tree casts through its ShadowModel alone, within S (`sub_822DAB20` draws result[156]
        /// at LOD 0 in the shadow pass, nothing when there is none); anything else through its
        /// main model, cut hard at D (`sub_8221E5F0`, twin+292).
        /// </summary>
        public void MarkCasting(Node node, PropRole role, byte casting, StatedRange? range)
        {
            if (!Refii.Models.Engine_ClassicMaterial.Active)
            {
                // Modern reads only "casts nothing" (Engine_PropStreamer's hand-off), on the
                // node that would cast; how trees cast stays its own.
                if (role == PropRole.Main && ModelClass != PropModelClass.Tree)
                {
                    Refii.Models.Engine_ClassicCasters.Mark(node, casting, 0f);
                }
                return;
            }

            bool tree = ModelClass == PropModelClass.Tree;
            bool caster = tree ? role == PropRole.Shadow : role == PropRole.Main;
            float reach = range is { } stated ? (tree ? stated.Shadow : stated.Draw) : 0f;
            Refii.Models.Engine_ClassicCasters.Mark(node, caster ? casting : (byte)0, reach);
        }

        /// <summary>The casting bits of instance <paramref name="index"/>.</summary>
        public byte CastingAt(int index) => index < InstanceCasting.Count
            ? InstanceCasting[index]
            : Refii.Models.Engine_ClassicShadows.CastsDefault;

        /// <summary>
        /// Records one instance, refusing any transform that is not finite.
        /// </summary>
        /// <remarks>
        /// THE CHOKEPOINT, guarded here because six callers reach it and the builders they come
        /// from harden the basis and the scale while leaving the ORIGIN bare — the .lev type-2
        /// and type-21 loop1 readers take positions as raw f32 with no test at all.
        ///
        /// These origins seed the block's MultiMesh CustomAabb through Expand(), so ONE bad
        /// instance in a ten-thousand-instance foliage block poisons the whole box. A
        /// non-finite box is a non-finite render depth, and Godot sorts its render list with
        /// std::sort, whose final pass has no bounds check: every comparison against NaN is
        /// false, the sort walks off the front of the array and faults, and the fault
        /// re-executes forever. Load-time data, so unlike a torn guest read this one is
        /// PERMANENT — the level freezes the same way every time it is entered.
        /// </remarks>
        public void AddInstance(
            Transform3D transform, uint hash = 0, ulong bakeKey = 0, uint bakeColour = 0,
            StatedRange? range = null,
            byte casting = Refii.Models.Engine_ClassicShadows.CastsDefault)
        {
            if (!transform.Origin.IsFinite() ||
                transform.Origin.Length() > MaxInstanceReach ||
                !transform.Basis.X.IsFinite() ||
                !transform.Basis.Y.IsFinite() ||
                !transform.Basis.Z.IsFinite())
            {
                ++NonFiniteInstances;
                return;
            }

            Instances.Add(transform);
            InstanceHashes.Add(hash);
            InstanceBakeKeys.Add(bakeKey);
            InstanceBakeColours.Add(bakeColour);
            Ranges.Add(range);
            InstanceCasting.Add(casting);
        }

        /// <summary>
        /// The models one placement draws through: the main model always, and for a TreeMesh its
        /// `_LOD1` and `_shadow` models when the block names them and the instance states ranges.
        /// </summary>
        public IEnumerable<(PropRole Role, string Path)> Roles(StatedRange? range)
        {
            if (ModelClass != PropModelClass.Tree || range is null)
            {
                yield return (PropRole.Main, ModelPath);
                yield break;
            }

            // L <= 0 with a LOD model: `d < 0.75·L` and `d < L` never hold, so the guest never
            // draws the main model (`sub_82208310`).
            if (LodModelPath.Length == 0 || range.Value.Lod > 0f)
            {
                yield return (PropRole.Main, ModelPath);
            }

            if (LodModelPath.Length != 0)
            {
                yield return (PropRole.Lod, LodModelPath);
            }

            if (ShadowModelPath.Length != 0)
            {
                yield return (PropRole.Shadow, ShadowModelPath);
            }
        }

        /// <summary>
        /// Gives one node its role's draw range and shadow part. Foliage casts nothing (set by the
        /// caller). A tree's main and LOD models never cast when it has a `_shadow` model; without
        /// one the guest casts nothing, and "full" reach leaves the main and LOD models casting.
        /// </summary>
        public void Configure(GeometryInstance3D node, PropRole role, StatedRange? range, float spread = 0f)
        {
            if (range is not { } stated)
            {
                return;
            }

            // One copy feeds the GI, the main one. SDFGI voxelises static geometry without
            // consulting visibility ranges, so a static LOD or shadow twin would be voxelised
            // on top of the main model at every distance.
            if (role != PropRole.Main)
            {
                node.GIMode = GeometryInstance3D.GIModeEnum.Disabled;
            }

            if (ModelClass == PropModelClass.Tree)
            {
                if (role == PropRole.Shadow)
                {
                    // Under classic the cascade cameras DRAW it (its layers are theirs alone);
                    // ShadowsOnly would hide it from every camera.
                    node.CastShadow = Refii.Models.Engine_ClassicMaterial.Active
                        ? GeometryInstance3D.ShadowCastingSetting.Off
                        : GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
                }
                else if (ShadowModelPath.Length != 0 || Engine_PropDrawRange.TreeShadowStated)
                {
                    node.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                }
            }

            Engine_PropDrawRange.Stamp(node, stated, ModelClass, role, LodModelPath.Length != 0, spread);
        }
    }

    /// <summary>
    /// The draw contract of a `.save` placement whose model is a TreeMesh. The guest picks the
    /// tree class from the model file alone (`sub_82A443A0`: model+104 == 2), so an entity-placed
    /// oak is drawn exactly as a level-block one — main model under L, LOD model to F, culled at
    /// max(D, F) (`sub_82208310`) — with L = LodDistance and F = BillboardDistance off the
    /// definition's appearance component (`sub_82A708E0` ← `sub_82572438`), D = MaxDrawDistance,
    /// and the LOD and shadow models the StaticMeshComponent names; the shadow model casts within
    /// F (`sub_822DAB20`). D is an entity's distance and takes the entity scale, as the live
    /// entity's node does.
    /// Null when no view carries the placement's appearance; the placement then keeps no range.
    ///
    /// A DEFINITION THAT NAMES NO LOD MODEL (the shipped trees state D 1024, F 64, L 0 and no
    /// LodModel or ShadowModel) is, to the guest, the main model out to 0.75 F and a billboard
    /// impostor from there to D (`sub_82208310` tiers 4 and 5, the distant-tree tables). There is
    /// no impostor here; the level's own engine-level tree block for the same model names its
    /// `_LOD1` and `_shadow` models, and those stand in: the main model to F, the LOD model from
    /// 0.75 F to the cull. A definition that names its own LodModel keeps the guest's L exactly.
    /// </summary>
    private static StatedRange? StatePlacedTree(
        PropBlockBuild block,
        GdbPlacement placement,
        GdbView view,
        List<GdbView> supplementalViews,
        Dictionary<string, PropBlockBuild> blocks,
        PropBuildStats stats)
    {
        GdbDrawSettings? found = null;
        foreach (uint record in new[] { placement.Hash, placement.ParentHash })
        {
            if (Engine_GdbDrawSettings.TryRead(view, record, out GdbDrawSettings stated))
            {
                found = stated;
                break;
            }
            foreach (GdbView supplemental in supplementalViews)
            {
                if (Engine_GdbDrawSettings.TryRead(supplemental, record, out stated))
                {
                    found = stated;
                    break;
                }
            }
            if (found is not null)
            {
                break;
            }
        }
        if (found is not { } d)
        {
            stats.SaveTreesUnstated++;
            return null;
        }
        if (!block.PlacedTreeLogged)
        {
            block.PlacedTreeLogged = true;
            string borrowed = string.Empty;
            if (d.LodModelHash != 0)
            {
                block.LodModelPath = Engine_PropModelIndex.Resolve(d.LodModelHash, Array.Empty<string>())
                    ?? string.Empty;
            }
            if (d.ShadowModelHash != 0)
            {
                block.ShadowModelPath = Engine_PropModelIndex.Resolve(d.ShadowModelHash, Array.Empty<string>())
                    ?? string.Empty;
            }
            // Under classic the impostor follows the full model, as the title's does: no stand-in.
            if (d.LodModelHash == 0 && Engine_TreeReach.PlacedTreeBorrowsFarModel)
            {
                foreach (PropBlockBuild other in blocks.Values)
                {
                    if (other.ModelClass == PropModelClass.Tree && other.LodModelPath.Length != 0 &&
                        !ReferenceEquals(other, block) &&
                        string.Equals(other.ModelPath, block.ModelPath, StringComparison.OrdinalIgnoreCase))
                    {
                        block.LodModelPath = other.LodModelPath;
                        if (block.ShadowModelPath.Length == 0)
                        {
                            block.ShadowModelPath = other.ShadowModelPath;
                        }
                        block.PlacedTreeBorrowsLod = true;
                        borrowed = $" (from {other.Label})";
                        break;
                    }
                }
            }
            GD.Print($"[props] placed tree {NameOf(block.ModelPath)} ({placement.EntityName}): " +
                $"D {d.MaxDrawDistance:0.#} F {d.BillboardDistance:0.#} L {d.LodDistance:0.#} " +
                $"fade {d.FadeRangeFactor:0.###} | lod " +
                (block.LodModelPath.Length != 0 ? NameOf(block.LodModelPath) + borrowed
                    : d.LodModelHash != 0 ? $"UNRESOLVED {d.LodModelHash:X8}" : "none") +
                " | shadow " +
                (block.ShadowModelPath.Length != 0 ? NameOf(block.ShadowModelPath)
                    : d.ShadowModelHash != 0 ? $"UNRESOLVED {d.ShadowModelHash:X8}" : "none"));
        }
        stats.SaveTreesRanged++;
        // A borrowed LOD model stands in for the impostor: the main model to F, the LOD beyond.
        float lod = block.PlacedTreeBorrowsLod && d.LodDistance <= 0f ? d.BillboardDistance : d.LodDistance;
        return new StatedRange(
            Draw: d.MaxDrawDistance * Engine_PropDrawRange.EntityDistanceScale,
            Fade: d.FadeRangeFactor,
            NearDraw: d.FadeInDistance,
            NearFade: d.FadeInRangeFactor,
            Shadow: d.BillboardDistance,
            Lod: lod);
    }

    private static void AppendEngineLevelBlocks(
        Func<string, byte[]?> read,
        string enginePath,
        Dictionary<string, PropBlockBuild> blocks,
        List<string> order,
        HashSet<string> transformKeys,
        PropBuildStats stats,
        Refii.Lighting.LightmapContainer? bakes)
    {
        // THE WHOLE FILE, INCLUDING ALL THE FOLIAGE, rides on this read — so a miss is said.
        //
        // `AppendFoliageBlocks` is called from the bottom of this function, so a bare return here
        // drops the level's engine_level blocks AND every instanced plant in one go. Ravenscar
        // draws 80,530 foliage instances from this path and Dunecrest 29,285; a level that
        // silently returned zero of them looked like a level with no undergrowth authored, and
        // there was nothing in the log to say otherwise.
        byte[]? bytes = read(enginePath);
        if (bytes is null)
        {
            stats.EngineLevelError = $"absent from every bank: {enginePath}";
            return;
        }

        EngineLevelFile file = Engine_EngineLevelFile.Parse(bytes);
        stats.EngineLevelType21Blocks = file.Type21Blocks;

        if (!file.Ok)
        {
            stats.EngineLevelError = file.Error;
            return;
        }

        if (file.StoppedAtType != 0)
        {
            stats.EngineLevelError = $"stopped at unknown type {file.StoppedAtType}";
        }

        foreach (EngineLevelBlock block in file.PropBlocks)
        {
            string? modelPath = Engine_PropModelIndex.ResolveByPath(block.ModelPath);
            if (modelPath is null)
            {
                stats.EngineLevelUnresolved += block.Instances.Count;
                continue;
            }

            string key = $"engine|{stats.EngineLevelBlocks}|{modelPath}";
            var build = new PropBlockBuild(
                modelPath, $"engine_level {NameOf(modelPath)}")
            {
                ModelClass = Engine_PropDrawRange.ClassOf(modelPath),
            };

            // The guest reads the shadow and LOD models only for a TreeMesh (`sub_82A708E0`).
            if (build.ModelClass == PropModelClass.Tree)
            {
                build.LodModelPath = block.LodModelPath.Length == 0 ? string.Empty
                    : Engine_PropModelIndex.ResolveByPath(block.LodModelPath) ?? string.Empty;
                build.ShadowModelPath = block.ShadowModelPath.Length == 0 ? string.Empty
                    : Engine_PropModelIndex.ResolveByPath(block.ShadowModelPath) ?? string.Empty;
                stats.TreeBlocks++;
            }

            int emitted = 0;
            foreach (EngineLevelInstance instance in block.Instances)
            {
                Transform3D transform = BuildStaticPlacementTransform(instance.Values);
                if (!transformKeys.Add(TransformKey(transform, modelPath)))
                {
                    // COUNTED, because the gap between `block.Instances.Count` and `emitted` was
                    // otherwise invisible and reads as the file having stated fewer instances.
                    stats.EngineLevelDuplicate++;
                    continue;
                }

                // The instance's own 64-bit id IS the bake key: chapter2posh's 90 type-2
                // instances all resolve in the .lmp with it verbatim — 28 in the vertex-bake
                // map (the Bowerstone structures), 62 in the 48-byte ambient map. No composed
                // model-hash key exists for this population; the file states the join directly.
                build.AddInstance(transform, bakeKey: instance.Hash,
                    range: StatedRange.FromStatic(instance.Values), casting: instance.Casting);
                emitted++;
            }

            if (emitted == 0)
            {
                continue;
            }

            if (build.ModelClass == PropModelClass.Tree)
            {
                float minD = float.MaxValue, maxD = 0f, minS = float.MaxValue, maxS = 0f;
                float minL = float.MaxValue, maxL = 0f;
                foreach (StatedRange? r in build.Ranges)
                {
                    if (r is not { } v)
                    {
                        continue;
                    }

                    minD = Math.Min(minD, v.Draw); maxD = Math.Max(maxD, v.Draw);
                    minS = Math.Min(minS, v.Shadow); maxS = Math.Max(maxS, v.Shadow);
                    minL = Math.Min(minL, v.Lod); maxL = Math.Max(maxL, v.Lod);
                }

                GD.Print($"[props] tree {NameOf(modelPath)} x{emitted}: D {minD:0.#}-{maxD:0.#} " +
                    $"S {minS:0.#}-{maxS:0.#} L {minL:0.#}-{maxL:0.#} | lod " +
                    $"{(build.LodModelPath.Length != 0 ? NameOf(build.LodModelPath) : block.LodModelPath.Length != 0 ? "UNRESOLVED " + block.LodModelPath : "none")}" +
                    $" | shadow {(build.ShadowModelPath.Length != 0 ? NameOf(build.ShadowModelPath) : block.ShadowModelPath.Length != 0 ? "UNRESOLVED " + block.ShadowModelPath : "none")}");
            }

            blocks[key] = build;
            order.Add(key);

            stats.EngineLevelInstances += emitted;
            stats.Instances += emitted;
            stats.EngineLevelBlocks++;
        }

        AppendFoliageBlocks(file, blocks, order, transformKeys, stats, bakes);
    }

    private static void AppendFoliageBlocks(
        EngineLevelFile file,
        Dictionary<string, PropBlockBuild> blocks,
        List<string> order,
        HashSet<string> transformKeys,
        PropBuildStats stats,
        Refii.Lighting.LightmapContainer? bakes)
    {
        var merged = new Dictionary<string, PropBlockBuild>();
        var mergedOrder = new List<string>();

        foreach (EngineLevelBlock block in file.FoliageBlocks)
        {
            string? modelPath = Engine_PropModelIndex.ResolveByPath(block.ModelPath);
            if (modelPath is null)
            {
                stats.FoliageUnresolved += block.Instances.Count;
                continue;
            }

            if (!merged.TryGetValue(modelPath, out PropBlockBuild? build))
            {
                // Every type-21 block is a RepeatedMesh to the guest (`sub_82A722A8`); its LOD
                // string has no consumer.
                build = new PropBlockBuild(modelPath, $"foliage {NameOf(modelPath)}")
                {
                    Foliage = true,
                    ModelClass = PropModelClass.Repeated,
                };
                merged[modelPath] = build;
                mergedOrder.Add(modelPath);
            }

            // THE BLOCK'S U32 ARRAY IS ITS BAKE — one D3DCOLOR per loop1 instance, keyed by
            // the block's own hash. All 158 of chapter2posh's type-21 blocks join its 158
            // arrays with the array length equal to the loop1 count exactly.
            uint[]? authored = null;
            bakes?.U32Arrays.TryGetValue(block.Hash, out authored);

            for (int i = 0; i < block.Instances.Count; i++)
            {
                EngineLevelInstance instance = block.Instances[i];
                Transform3D transform =
                    BuildFoliageTransform(instance.Values, instance.Rotation);
                build.AddInstance(transform, bakeColour:
                    authored is not null && i < authored.Length ? authored[i] : 0,
                    range: StatedRange.FromFoliage(block.DrawDistance, block.FadeFraction),
                    casting: block.Casting);
                transformKeys.Add(TransformKey(transform, modelPath));
            }
        }

        foreach (string modelPath in mergedOrder)
        {
            PropBlockBuild build = merged[modelPath];
            blocks["foliage|" + modelPath] = build;
            order.Add("foliage|" + modelPath);

            stats.FoliageBlocks++;
            stats.FoliageInstances += build.Instances.Count;
            stats.Instances += build.Instances.Count;
        }

        if (OS.GetEnvironment("REFII_DUMP_FOLIAGE").Length != 0)
        {
            foreach (string modelPath in mergedOrder)
            {
                PropBlockBuild build = merged[modelPath];
                if (build.Instances.Count == 0)
                {
                    continue;
                }

                Vector3 o = build.Instances[0].Origin;
                GD.Print($"[foliage] {NameOf(modelPath)} x{build.Instances.Count} " +
                    $"first=({o.X:0.#},{o.Y:0.#},{o.Z:0.#})");
            }
        }
    }

    /// <summary>
    /// A FOLIAGE (type-21) instance's transform.
    ///
    /// Type 21 ships a quaternion and a half-float scale, which
    /// <see cref="Engine_EngineLevelFile"/> reduces to a yaw sin/cos pair in
    /// <c>values[6]/[7]</c> and a uniform scale broadcast into <c>values[9..11]</c>. That
    /// synthesis is why this reads the slots it does — they are ours, not the file's.
    ///
    /// Static props do NOT share this shape; see
    /// <see cref="BuildStaticPlacementTransform"/>. They used to, and it silently threw away
    /// every prop's real orientation.
    /// </summary>
    /// <summary>Furthest a prop instance may sit from the level origin.</summary>
    private const float MaxInstanceReach = 1_000_000f;

    /// <summary>Prop/foliage instances refused as not finite. Run total.</summary>
    public static int NonFiniteInstances;

    public static Transform3D BuildFoliageTransform(float[] values) =>
        BuildFoliageTransform(values, null);

    /// <summary>
    /// A foliage instance's transform, from its full authored rotation when the record has
    /// one and from the yaw pair when it does not.
    ///
    /// Type-21 v12 ships a quaternion; the parser also reduces it to a yaw sin/cos pair, and
    /// using only that stands every plant bolt upright. Real foliage is authored leaning —
    /// on banks, against walls, out of a wall's cracks — and that lean is pitch and roll,
    /// exactly what the yaw reduction discards. v11 records carry no quaternion, so they keep
    /// the old path.
    /// </summary>
    public static Transform3D BuildFoliageTransform(
        float[] values, System.Numerics.Quaternion? rotation)
    {
        float sx = values[9] == 0f ? 1f : values[9];
        float sy = values[10] == 0f ? sx : values[10];
        float sz = values[11] == 0f ? sx : values[11];

        var origin = new Vector3(values[0], values[2], values[1]);

        // X AND W ARE NOT THE WHOLE QUATERNION. Y and Z come from the same four half-float
        // reads and are just as able to be NaN; the lengthSquared test below cannot catch them
        // because every comparison against NaN is false.
        if (rotation is not { } q || !float.IsFinite(q.X) || !float.IsFinite(q.Y) ||
            !float.IsFinite(q.Z) || !float.IsFinite(q.W))
        {
            return new Transform3D(YawScaleBasis(values[6], values[7], sx, sy, sz), origin);
        }

        float lengthSquared = (q.X * q.X) + (q.Y * q.Y) + (q.Z * q.Z) + (q.W * q.W);
        if (lengthSquared < 1e-8f)
        {
            return new Transform3D(YawScaleBasis(values[6], values[7], sx, sy, sz), origin);
        }

        // The guest's own quaternion-to-matrix, sub_8220C8F0: s = 2/|q|^2, and it produces the
        // ROW-vector form whose rows are the local axes in guest world space.
        float s = 2f / lengthSquared;
        var localX = new Vector3(
            1f - (s * ((q.Y * q.Y) + (q.Z * q.Z))),
            s * ((q.X * q.Y) + (q.Z * q.W)),
            s * ((q.X * q.Z) - (q.Y * q.W)));
        var localY = new Vector3(
            s * ((q.X * q.Y) - (q.Z * q.W)),
            1f - (s * ((q.X * q.X) + (q.Z * q.Z))),
            s * ((q.Y * q.Z) + (q.X * q.W)));
        var localZ = new Vector3(
            s * ((q.X * q.Z) + (q.Y * q.W)),
            s * ((q.Y * q.Z) - (q.X * q.W)),
            1f - (s * ((q.X * q.X) + (q.Y * q.Y))));

        // Same column order as BuildStaticPlacementTransform, and for the same reason: the mesh
        // is already mapped by M(x,y,z) = (x, z, -y) while placements use P(x,y,z) = (x, z, y),
        // so the basis is P * R * M^-1 and M^-1 sends e0,e1,e2 to localX, localZ and -localY.
        return new Transform3D(
            new Basis(
                GuestToGodot(localX) * sx,
                GuestToGodot(localZ) * sz,
                -GuestToGodot(localY) * sy),
            origin);
    }

    /// <summary>
    /// A STATIC PLACEMENT's (type-2) world transform, built the way the guest builds it.
    ///
    /// The file states the basis AXES directly — there is no yaw scalar, no Euler triple and
    /// no quaternion on disk for this record type. sub_82AB9CF8 reads four vec3s in file
    /// order through sub_82A21910 (0x82ABA040, 0x82ABA04C, 0x82ABA058, 0x82ABA064) into four
    /// separate stack slots, then at 0x82ABA5A0 copies vec#1 to pair+0 and vec#2 to pair+16
    /// and hands the pair to sub_821B0068 (0x82ABA694).
    ///
    /// sub_821B0068 takes A = pair+0 and B = pair+16 and lays down rows
    /// [cross(B,A), B, A, (0,0,0,1)] — the cross is B.yzx*A.zxy - B.zxy*A.yzx, which is
    /// cross(B, A). Against the world basis being X = cross(fwd,up), Y = fwd, Z = up, that
    /// fixes A = UP and B = FORWARD. So:
    ///
    ///   values[0..2]  position        values[3..5]  UP        values[6..8]  FORWARD
    ///   values[9..11] READ AND DISCARDED by the guest (no reader on this path)
    ///   values[12]    uniform scale
    ///
    /// The guest then stores quaternion at record+0x10, position at +0x20 and ONE scale
    /// float at +0x30 (0x82ABA6A8-0x82ABA6D4), and sub_82AA2F38 multiplies all three
    /// rotation rows by splats of that single float — the scale is uniform, not per-axis.
    ///
    /// NOT THE FOLIAGE SHAPE, which reads values[6] and values[7] as a yaw sin/cos pair and
    /// values[9..11] as a per-axis scale. On a static placement those are the first two
    /// components of the forward vector and the vector the guest throws away, so that reading
    /// orients a prop from two thirds of its forward axis and ignores the up vector, the third
    /// forward component and the real scale.
    ///
    /// The guest launders the basis through matrix -> quaternion -> matrix, and the quat to
    /// matrix step divides by |q|^2, so the round trip orthonormalises. Building the axes
    /// directly is exact for well-formed data and the normalisation here matches what that
    /// round trip would have done to anything else.
    /// </summary>
    public static Transform3D BuildStaticPlacementTransform(float[] values)
    {
        // Guest space throughout, because the cross product has to happen BEFORE the change
        // of basis: the guest-to-Godot map is a reflection, and cross does not commute with
        // one — S(a x b) = -(S a x S b). Crossing after converting silently flips every
        // prop's right axis.
        var up = new Vector3(values[3], values[4], values[5]);
        var forward = new Vector3(values[6], values[7], values[8]);

        float scale = float.IsFinite(values[12]) && values[12] > 0.0001f ? values[12] : 1f;

        Vector3 right = forward.Cross(up);
        if (!up.IsFinite() || !forward.IsFinite() || right.LengthSquared() < 1e-12f)
        {
            // Degenerate or parallel axes: the orientation is unrecoverable, so place it
            // upright rather than emitting a zero basis that collapses the mesh to nothing.
            return new Transform3D(
                Basis.Identity.Scaled(new Vector3(scale, scale, scale)),
                GuestToGodot(new Vector3(values[0], values[1], values[2])));
        }

        right = right.Normalized();
        forward = forward.Normalized();
        up = right.Cross(forward).Normalized();

        // THE MESH IS NOT IN GUEST SPACE. Engine_MdlGeometry.ConvertZUpToYUp has already
        // mapped every vertex by M(x,y,z) = (x, z, -y) — a determinant +1 rotation — while
        // placements use P(x,y,z) = (x, z, y), a reflection. The two differ, so the basis has
        // to absorb the difference as well as carry the rotation:
        //
        //     Basis * M(v) + origin  ==  P(R*v + t)     for every model vertex v
        //  => Basis = P * R * M^-1,  origin = P(t)
        //
        // and M^-1 sends e0,e1,e2 to (1,0,0), (0,0,1), (0,-1,0), so R maps those to right,
        // up and -forward. Hence the columns below — NOT right/forward/up in order, which is
        // what it looks like it should be and is wrong by a swap and a sign.
        //
        // Equivalently this is (P R P) with column 2 negated, since M^-1 = P * diag(1,1,-1) —
        // the same shape RotationBasis and ToGodotBasis already use.
        return new Transform3D(
            new Basis(
                GuestToGodot(right) * scale,
                GuestToGodot(up) * scale,
                -GuestToGodot(forward) * scale),
            GuestToGodot(new Vector3(values[0], values[1], values[2])));
    }

    /// <summary>
    /// Guest space to the level scene's INTERNAL space — the half of the conversion that
    /// lives on the placement. <c>Engine_LevelScene</c> gives the level root
    /// <c>Scale = (-1, 1, 1)</c>, and the two compose to a determinant +1 rotation; see
    /// <c>GameWorld.ToGodot</c>, which carries the net map because the camera is a sibling
    /// of the region rather than a child.
    /// </summary>
    private static Vector3 GuestToGodot(Vector3 guest) => new(guest.X, guest.Z, guest.Y);

    public static List<GdbView> LoadSupplementalViews(Func<string, byte[]?> read)
    {
        if (Refii.Gdb.Engine_GameGdb.MatchesActiveResolver(read))
        {
            return Refii.Gdb.Engine_GameGdb.GetSupplementalViews();
        }

        var views = new List<GdbView>();
        foreach (string path in SupplementalGdbPaths)
        {
            byte[]? bytes = read(path) ?? read("data/" + path);
            if (bytes is not null && GdbView.TryParseShared(bytes, out GdbView view, out _))
            {
                views.Add(view);
            }
        }

        return views;
    }

    public static void AugmentPlacementModelHashes(
        List<GdbPlacement> placements,
        List<GdbView> views,
        PropBuildStats stats)
    {
        if (views.Count == 0 || placements.Count == 0)
        {
            return;
        }

        foreach (GdbPlacement p in placements)
        {
            if (p.Hash == 0 || p.ModelPathHash != 0)
            {
                continue;
            }

            List<uint>? found = null;
            uint foundParent = 0;

            foreach (GdbView view in views)
            {
                if (Engine_GdbPlacements.TryLookupModelPathHashes(
                        view, p.Hash, out List<uint> byEntity, out uint parentHash))
                {
                    found = byEntity;
                    foundParent = parentHash;
                    break;
                }

                if (p.ParentHash != 0 &&
                    Engine_GdbPlacements.TryLookupModelPathHashes(
                        view, p.ParentHash, out List<uint> byParent, out _))
                {
                    found = byParent;
                    break;
                }
            }

            if (found is null)
            {
                continue;
            }

            p.ModelPathHashes = found;
            p.ModelPathHash = found[0];
            stats.SupplementalModelFills++;

            if (p.ParentHash == 0 && foundParent != 0)
            {
                p.ParentHash = foundParent;
            }
        }

        var parts = new List<GdbPlacement>();
        foreach (GdbPlacement p in placements)
        {
            if (p.ModelPathHashes.Count <= 1)
            {
                continue;
            }

            List<uint> hashes = p.ModelPathHashes;
            p.ModelPathHash = hashes[0];
            p.ModelPathHashes = new List<uint> { hashes[0] };

            for (int i = 1; i < hashes.Count; i++)
            {
                GdbPlacement part = p.Clone();
                part.ModelPathHash = hashes[i];
                part.ModelPathHashes = new List<uint> { hashes[i] };
                parts.Add(part);
            }
        }

        placements.AddRange(parts);
    }

    private static void AppendSupplementalPlacements(
        List<GdbView> views,
        List<KeyValuePair<uint, string>> saveMap,
        List<GdbPlacement> placements,
        PropBuildStats stats,
        EditorPropHierarchy? editorHierarchy = null)
    {
        if (saveMap.Count == 0 || views.Count == 0)
        {
            return;
        }

        var placed = new HashSet<uint>();
        foreach (GdbPlacement placement in placements)
        {
            if (placement.Hash != 0)
            {
                placed.Add(placement.Hash);
            }
        }

        var appended = new List<GdbPlacement>();

        foreach (KeyValuePair<uint, string> entity in saveMap)
        {
            if (entity.Key == 0 || placed.Contains(entity.Key))
            {
                continue;
            }

            foreach (GdbView view in views)
            {
                if (!Engine_GdbPlacements.TryLookupPlacement(
                        view, entity.Key, entity.Value, true, out GdbPlacement placement) ||
                    placement.ModelPathHashes.Count == 0)
                {
                    continue;
                }

                placed.Add(entity.Key);
                editorHierarchy?.MarkSupplemental(entity.Key);
                stats.SupplementalHits++;

                if (placement.ModelPathHashes.Count > 1)
                {
                    foreach (uint hash in placement.ModelPathHashes)
                    {
                        GdbPlacement part = placement.Clone();
                        part.ModelPathHash = hash;
                        part.ModelPathHashes = new List<uint> { hash };
                        appended.Add(part);
                    }
                }
                else
                {
                    appended.Add(placement);
                }

                break;
            }
        }

        placements.AddRange(appended);
    }

    public static Transform3D BuildTransform(GdbPlacement placement) =>
        GdbTransformMath.BuildTransform(placement);

    public static Basis RotationBasis(float rx, float ry, float rz, float scale) =>
        GdbTransformMath.RotationBasis(rx, ry, rz, scale);

    public static Basis YawBasis(float yaw, float scale) =>
        GdbTransformMath.YawBasis(yaw, scale);

    public static Basis YawScaleBasis(float s, float c, float sx, float sy, float sz) =>
        GdbTransformMath.YawScaleBasis(s, c, sx, sy, sz);

    public static string CompanionPath(string modelPath)
    {
        string path = modelPath.Replace('\\', '/');

        if (path.EndsWith("/exterior.mdl", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(path.AsSpan(0, path.Length - 13), "/interior.mdl");
        }

        if (path.EndsWith("/interior.mdl", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(path.AsSpan(0, path.Length - 13), "/exterior.mdl");
        }

        return string.Empty;
    }

    public static bool IsCharacterModel(string modelPath)
    {
        string path = modelPath.Replace('\\', '/').ToLowerInvariant();
        return path.StartsWith("art/characters/", StringComparison.Ordinal) ||
               path.Contains("/art/characters/", StringComparison.Ordinal) ||
               path.Contains("/characters/heros/", StringComparison.Ordinal) ||
               path.StartsWith("art/creatures/", StringComparison.Ordinal) ||
               path.Contains("/art/creatures/", StringComparison.Ordinal);
    }

    private static void LogLine(string message)
    {
        if (Refii.Core.Engine_AppRoot.IsTestHost)
        {
            return;
        }

        GD.Print(message);
    }

    private static string GetEnv(string name) =>
        Refii.Core.Engine_AppRoot.IsTestHost
            ? System.Environment.GetEnvironmentVariable(name) ?? string.Empty
            : OS.GetEnvironment(name);

    public static string CompactText(string value)
    {
        var text = new System.Text.StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                text.Append(char.ToLowerInvariant(c));
            }
        }

        return text.ToString();
    }

    public static string LeafOf(string path)
    {
        int slash = path.LastIndexOfAny(new[] { '/', '\\' });
        return slash < 0 ? path : path[(slash + 1)..];
    }

    public static string NameOf(string path)
    {
        string leaf = LeafOf(path);
        int dot = leaf.LastIndexOf('.');
        string name = dot <= 0 ? leaf : leaf[..dot];

        if (!name.Equals("exterior", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("interior", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        string normalised = path.Replace('\\', '/');
        int leafSlash = normalised.LastIndexOf('/');
        if (leafSlash <= 0)
        {
            return name;
        }

        string parent = normalised[..leafSlash];
        int parentSlash = parent.LastIndexOf('/');
        string folder = parentSlash < 0 ? parent : parent[(parentSlash + 1)..];

        return folder.Length == 0 ? name : $"{folder} {name}";
    }

    private static string RecordKey(GdbPlacement placement, string modelPath) =>
        $"{modelPath.ToLowerInvariant()}|{placement.Hash:x}|{placement.ParentHash:x}|" +
        $"{placement.EntityName}|{Quantise(placement.X)},{Quantise(placement.Y)}," +
        $"{Quantise(placement.Z)}";

    private static string TransformKey(Transform3D transform, string modelPath)
    {
        Basis basis = transform.Basis;
        Vector3 origin = transform.Origin;

        return $"{modelPath.ToLowerInvariant()}|" +
               $"{Quantise(origin.X)}|{Quantise(origin.Y)}|{Quantise(origin.Z)}|" +
               $"{Quantise(basis.X.X)}|{Quantise(basis.X.Y)}|{Quantise(basis.X.Z)}|" +
               $"{Quantise(basis.Y.X)}|{Quantise(basis.Y.Y)}|{Quantise(basis.Y.Z)}|" +
               $"{Quantise(basis.Z.X)}|{Quantise(basis.Z.Y)}|{Quantise(basis.Z.Z)}";
    }

    private static long Quantise(float value) =>
        float.IsFinite(value) ? (long)MathF.Round(value * 100f) : 0L;

    // The per-single meta names as StringNames made once: a `const string` passed to SetMeta is
    // a new native StringName (a lookup in the global table under its lock) on every call. Made
    // on first use, not in the static initialiser: the tests touch this type with no engine.
    private static StringName? _modelPathMetaName, _recordMetaName, _bakeKeyMetaName;
    private static StringName ModelPathMetaName =>
        _modelPathMetaName ??= Engine_PropStreamer.ModelPathMeta;
    private static StringName RecordMetaName => _recordMetaName ??= Engine_PropStreamer.RecordMeta;
    private static StringName BakeKeyMetaName =>
        _bakeKeyMetaName ??= Refii.Lighting.Engine_LevelBakedLighting.BakeKeyMeta;

    /// <summary>
    /// The names taken under the build's container, kept in C#. <see cref="UniqueName"/> asks the
    /// container once per taken candidate, a NodePath built and a native lookup each time, so a
    /// label with n singles cost n(n+1)/2 of them; this answers the same question from a set and
    /// starts each label's count where it left off. The same names come out: `name`, then the
    /// first free `name (i)` from 2, and names are never freed during the build.
    /// </summary>
    /// <remarks>
    /// A name Godot would rewrite (one of `. : @ / " %`, or empty) is not looked up as itself, so
    /// those still go through <see cref="UniqueName"/>, and the name the node actually ends with
    /// is read back after it is added.
    /// </remarks>
    private sealed class ChildNames(Node parent)
    {
        private readonly HashSet<string> _taken = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _next = new(StringComparer.Ordinal);
        private bool _readBack;

        public string Take(string name)
        {
            _readBack = !Plain(name);
            if (_readBack)
            {
                return UniqueName(parent, name);
            }

            if (_taken.Add(name))
            {
                return name;
            }

            int i = _next.TryGetValue(name, out int from) ? from : 2;
            for (; ; i++)
            {
                string candidate = $"{name} ({i})";
                if (_taken.Add(candidate))
                {
                    _next[name] = i + 1;
                    return candidate;
                }
            }
        }

        /// <summary>Call after adding the child named by the last <see cref="Take"/>.</summary>
        public void Placed(Node child)
        {
            if (_readBack)
            {
                _taken.Add(child.Name);
                _readBack = false;
            }
        }

        private static bool Plain(string name) =>
            name.Length != 0 && name.AsSpan().IndexOfAny(".:@/\"%") < 0;
    }

    private static string UniqueName(Node parent, string name)
    {
        if (parent.GetNodeOrNull(name) is null)
        {
            return name;
        }

        for (int i = 2; ; i++)
        {
            string candidate = $"{name} ({i})";
            if (parent.GetNodeOrNull(candidate) is null)
            {
                return candidate;
            }
        }
    }
}
