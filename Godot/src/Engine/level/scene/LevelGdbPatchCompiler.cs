using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Refii.Core;
using Refii.Gdb;
using Refii.Mods;
using Refii.Host;

namespace Refii.Level;

/// <summary>
/// Pure-C# snapshot of a 1:1 GDB record node in a 3D level scene, decoupled from <see cref="Node3D"/>
/// so <see cref="LevelGdbPatchCompiler"/> can be unit-tested in headless CLI without unmanaged Godot objects.
/// </summary>
public sealed class LevelSceneRecordSnapshot
{
    public uint RecordHash { get; init; }
    public uint ParentHash { get; init; }
    public string EntityName { get; init; } = string.Empty;
    public string LayerName { get; init; } = "DefaultLayer";
    public string LayerTag { get; init; } = "id_5A4CBED4";
    public GdbSaveLoadState LoadState { get; init; } = GdbSaveLoadState.AlwaysOn;
    public string SourceDb { get; init; } = string.Empty;
    public string ModelPath { get; init; } = string.Empty;
    public Transform3D CurrentRegionTransform { get; init; } = Transform3D.Identity;
    public Transform3D BaseRegionTransform { get; init; } = Transform3D.Identity;
    public LevelNodeCategory Category { get; init; } = LevelNodeCategory.Prop;
    public bool HasBaseTransform { get; init; } = false;
    public bool IsNewPlacement { get; set; }
}

/// <summary>
/// Pure-C# snapshot of a <c>.save</c> layer container in a 3D level scene.
/// </summary>
public sealed class LevelSceneLayerSnapshot
{
    public string LayerName { get; init; } = string.Empty;
    public string LayerTag { get; init; } = string.Empty;
    public GdbSaveLoadState LoadState { get; init; } = GdbSaveLoadState.AlwaysOn;
}

/// <summary>
/// Result of compiling a 3D <c>LevelScene</c>'s edits into sparse <c>&lt;level&gt;.gdb.tu</c>,
/// optional <c>globals/globals.gdb.tu</c>, and <c>&lt;level&gt;.save.tu</c> patches.
/// </summary>
public sealed class LevelPatchCompileResult
{
    public string LevelGdbSubPath { get; set; } = string.Empty;
    public string LevelGdbTuSubPath => string.IsNullOrEmpty(LevelGdbSubPath) ? string.Empty : LevelGdbSubPath + ".tu";
    public string LevelSaveSubPath { get; set; } = string.Empty;
    public string LevelSaveTuSubPath => string.IsNullOrEmpty(LevelSaveSubPath) ? string.Empty : LevelSaveSubPath + ".tu";
    public GdbPatchDatabase LevelGdbPatch { get; set; } = new();
    public GdbPatchDatabase GlobalsGdbPatch { get; set; } = new();
    public SaveMapPatch SavePatch { get; set; } = new();

    public int ModifiedTransformCount { get; set; }
    public int NewPlacementCount { get; set; }
    public int DeletedRecordCount { get; set; }
    public int LayerMoveCount { get; set; }
    public int LayerStateOverrideCount { get; set; }

    public bool HasAnyChanges =>
        ModifiedTransformCount > 0 ||
        NewPlacementCount > 0 ||
        DeletedRecordCount > 0 ||
        LayerMoveCount > 0 ||
        LayerStateOverrideCount > 0 ||
        LevelGdbPatch.Entries.Count > 0 ||
        !SavePatch.IsEmpty;
}

/// <summary>
/// Compiles 3D viewport level edits (transform changes, new placements, deletions, and <c>.save</c> layer
/// or load-state changes) into sparse <c>&lt;level&gt;.gdb.tu</c> and <c>&lt;level&gt;.save.tu</c> overlays
/// with zero false diffs from editor hierarchy grouping.
/// </summary>
public static class LevelGdbPatchCompiler
{
    public const uint HashScale = 0xEC462524;

    private static readonly uint[] TransformComponentFieldHashes =
    {
        GdbHash.TransformComponent,
        GdbHash.SimpleTransformComponent,
        GdbHash.PhysicsSimulationKeyframedComponent,
        GdbHash.PhysicsSimulationStaticComponent,
        GdbHash.PhysicsSimulationDynamicComponent,
        0xFB32DB6F, // PhysicsSimulationCharacterNavigatorComponent
        0x05BD8E7B, // PhysicsSimpleComponent
    };

    /// <summary>
    /// Scans an edited <paramref name="levelRoot"/> scene tree, compares all 1:1 record nodes against
    /// their baseline transforms and vanilla <c>.save</c> layer assignments, and produces sparse
    /// <c>.gdb.tu</c> and <c>.save.tu</c> patches.
    /// </summary>
    public static LevelPatchCompileResult Compile(
        Node3D levelRoot,
        GdbView? baseLevelView,
        GdbSaveDocument? baseSaveDoc,
        GdbMulti? supplementalMulti = null,
        GdbPatchDatabase? existingLevelPatch = null,
        GdbPatchDatabase? existingGlobalsPatch = null)
    {
        if (levelRoot is null)
        {
            throw new ArgumentNullException(nameof(levelRoot));
        }

        string gdbSubPath = levelRoot.HasMeta(LevelRecordMetadata.LevelGdbPathMeta)
            ? levelRoot.GetMeta(LevelRecordMetadata.LevelGdbPathMeta).AsString()
            : string.Empty;
        string saveSubPath = levelRoot.HasMeta(LevelRecordMetadata.LevelSavePathMeta)
            ? levelRoot.GetMeta(LevelRecordMetadata.LevelSavePathMeta).AsString()
            : Engine_LevelGdb.SiblingWithExtension(gdbSubPath, ".save");

        TakeSceneSnapshot(
            levelRoot,
            baseSaveDoc,
            out List<LevelSceneRecordSnapshot> recordSnapshots,
            out List<LevelSceneLayerSnapshot> layerSnapshots,
            out HashSet<uint> baselineSpawned);

        return CompileSnapshots(
            gdbSubPath,
            saveSubPath,
            recordSnapshots,
            layerSnapshots,
            baselineSpawned,
            baseLevelView,
            baseSaveDoc,
            supplementalMulti,
            existingLevelPatch,
            existingGlobalsPatch);
    }

    /// <summary>
    /// Traverses <paramref name="levelRoot"/> to capture pure snapshots of all record nodes,
    /// layer containers, and baseline spawned hashes.
    /// </summary>
    public static void TakeSceneSnapshot(
        Node3D levelRoot,
        GdbSaveDocument? baseSaveDoc,
        out List<LevelSceneRecordSnapshot> recordSnapshots,
        out List<LevelSceneLayerSnapshot> layerSnapshots,
        out HashSet<uint> baselineSpawned)
    {
        baseSaveDoc ??= new GdbSaveDocument();

        var liveNodesByHash = new Dictionary<uint, Node3D>();
        var layerContainers = new Dictionary<string, Node3D>(StringComparer.OrdinalIgnoreCase);
        CollectSceneNodesRecursive(levelRoot, liveNodesByHash, layerContainers, baseSaveDoc);

        layerSnapshots = new List<LevelSceneLayerSnapshot>(layerContainers.Count);
        foreach (var (layerName, containerNode) in layerContainers)
        {
            string rawState = containerNode.HasMeta(LevelRecordMetadata.LayerLoadStateMeta)
                ? containerNode.GetMeta(LevelRecordMetadata.LayerLoadStateMeta).AsString()
                : string.Empty;
            string layerTag = containerNode.HasMeta(LevelRecordMetadata.LayerTagMeta)
                ? containerNode.GetMeta(LevelRecordMetadata.LayerTagMeta).AsString()
                : string.Empty;
            GdbSaveLoadState state = GdbSaveLoadStateExtensions.ParseLoadState(rawState);
            if (state != GdbSaveLoadState.Unknown)
            {
                layerSnapshots.Add(new LevelSceneLayerSnapshot
                {
                    LayerName = layerName,
                    LayerTag = layerTag,
                    LoadState = state,
                });
            }
        }

        recordSnapshots = new List<LevelSceneRecordSnapshot>(liveNodesByHash.Count);
        foreach (var (recordHash, node3D) in liveNodesByHash)
        {
            ResolveEffectiveNodeLayerAndState(
                node3D,
                baseSaveDoc,
                out string effectiveLayerName,
                out string effectiveLayerTag,
                out GdbSaveLoadState effectiveLoadState);

            string entityName = node3D.HasMeta(LevelRecordMetadata.EntityNameMeta)
                ? node3D.GetMeta(LevelRecordMetadata.EntityNameMeta).AsString()
                : string.Empty;
            if (string.IsNullOrWhiteSpace(entityName))
            {
                entityName = StripHashSuffix(node3D.Name.ToString());
            }

            bool hasBaseTransform = LevelRecordMetadata.TryGetBaseTransform(node3D, out Transform3D baseTransform);
            Transform3D currentRegionTransform = LevelRecordMetadata.ComputeRegionTransform(node3D, levelRoot);
            bool isNew = node3D.HasMeta(LevelRecordMetadata.IsNewPlacementMeta) &&
                         node3D.GetMeta(LevelRecordMetadata.IsNewPlacementMeta).AsBool();

            string modelPath = LevelRecordMetadata.FindModelPath(node3D);

            LevelNodeCategory category = node3D.HasMeta(LevelRecordMetadata.NodeCategoryMeta)
                ? (LevelNodeCategory)node3D.GetMeta(LevelRecordMetadata.NodeCategoryMeta).AsByte()
                : LevelNodeCategory.Prop;

            recordSnapshots.Add(new LevelSceneRecordSnapshot
            {
                RecordHash = recordHash,
                ParentHash = LevelRecordMetadata.GetParentHash(node3D),
                EntityName = entityName,
                Category = category,
                LayerName = effectiveLayerName,
                LayerTag = effectiveLayerTag,
                LoadState = effectiveLoadState,
                SourceDb = node3D.HasMeta(LevelRecordMetadata.SourceDbMeta)
                    ? node3D.GetMeta(LevelRecordMetadata.SourceDbMeta).AsString()
                    : string.Empty,
                ModelPath = modelPath,
                CurrentRegionTransform = currentRegionTransform,
                BaseRegionTransform = baseTransform,
                HasBaseTransform = hasBaseTransform,
                IsNewPlacement = isNew,
            });
        }

        baselineSpawned = new HashSet<uint>();
        if (levelRoot.HasMeta(LevelRecordMetadata.SpawnedRecordHashesMeta))
        {
            long[] rawArr = levelRoot.GetMeta(LevelRecordMetadata.SpawnedRecordHashesMeta).AsInt64Array();
            foreach (long raw in rawArr)
            {
                uint h = unchecked((uint)raw);
                if (h != 0)
                {
                    baselineSpawned.Add(h);
                }
            }
        }
    }

    /// <summary>
    /// Pure-C# snapshot compiler: compares <paramref name="recordSnapshots"/> and <paramref name="layerSnapshots"/>
    /// against <paramref name="baseLevelView"/> and <paramref name="baseSaveDoc"/> and produces sparse
    /// <c>&lt;level&gt;.gdb.tu</c>, <c>globals/globals.gdb.tu</c>, and <c>&lt;level&gt;.save.tu</c> patches.
    /// </summary>
    public static LevelPatchCompileResult CompileSnapshots(
        string gdbSubPath,
        string? saveSubPath,
        IReadOnlyList<LevelSceneRecordSnapshot> recordSnapshots,
        IReadOnlyList<LevelSceneLayerSnapshot>? layerSnapshots,
        IReadOnlyCollection<uint>? baselineSpawnedHashes,
        GdbView? baseLevelView,
        GdbSaveDocument? baseSaveDoc,
        GdbMulti? supplementalMulti = null,
        GdbPatchDatabase? existingLevelPatch = null,
        GdbPatchDatabase? existingGlobalsPatch = null)
    {
        string resolvedSaveSubPath = !string.IsNullOrWhiteSpace(saveSubPath)
            ? saveSubPath!
            : Engine_LevelGdb.SiblingWithExtension(gdbSubPath, ".save");

        var result = new LevelPatchCompileResult
        {
            LevelGdbSubPath = NormalizeSubPath(gdbSubPath),
            LevelSaveSubPath = NormalizeSubPath(resolvedSaveSubPath),
            LevelGdbPatch = ClonePatchDatabase(existingLevelPatch),
            GlobalsGdbPatch = ClonePatchDatabase(existingGlobalsPatch),
            SavePatch = new SaveMapPatch(),
        };

        baseSaveDoc ??= new GdbSaveDocument();

        var layerMap = new Dictionary<string, LevelSceneLayerSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (layerSnapshots is not null)
        {
            foreach (LevelSceneLayerSnapshot ls in layerSnapshots)
            {
                if (string.IsNullOrEmpty(ls.LayerName) || ls.LoadState == GdbSaveLoadState.Unknown)
                {
                    continue;
                }

                layerMap[ls.LayerName] = ls;
                GdbSaveLayer? baseLayer = baseSaveDoc.FindLayer(ls.LayerName);
                if (baseLayer is null || baseLayer.LoadState != ls.LoadState)
                {
                    SaveMapLayerPatch lp = result.SavePatch.GetOrCreateLayer(ls.LayerName, ls.LayerTag);
                    lp.LoadOverride = ls.LoadState;
                    result.LayerStateOverrideCount++;
                }
            }
        }

        var liveHashes = new HashSet<uint>();
        foreach (LevelSceneRecordSnapshot snap in recordSnapshots)
        {
            if (snap.RecordHash != 0 && !LevelRecordMetadata.IsAbyssCulled(snap.CurrentRegionTransform))
            {
                liveHashes.Add(snap.RecordHash);
            }
        }

        if (baselineSpawnedHashes is not null)
        {
            foreach (uint spawnedHash in baselineSpawnedHashes)
            {
                if (spawnedHash != 0 && !liveHashes.Contains(spawnedHash))
                {
                    result.SavePatch.RemoveEntity(spawnedHash);
                    result.DeletedRecordCount++;

                    // Abyss-cull deletion: emit Position = (0, -9999, 0), Scale = 0.0 in <level>.gdb.tu
                    // so the native Xbox 360 guest engine culls the entity out of the playable world:
                    GdbPatchDatabase targetDb = result.LevelGdbPatch;
                    GdbView? ownerView = ResolveRecordView(spawnedHash, baseLevelView, supplementalMulti);
                    EmitRecordTransformEntries(
                        targetDb,
                        ownerView,
                        spawnedHash,
                        0,
                        string.Empty,
                        string.Empty,
                        new Transform3D(Basis.FromScale(Vector3.Zero), new Vector3(0f, -9999f, 0f)),
                        isNewPlacement: false);
                    ClearRecordTransformEntries(result.GlobalsGdbPatch, ownerView, spawnedHash);
                }
            }
        }

        foreach (LevelSceneRecordSnapshot snap in recordSnapshots)
        {
            if (snap.RecordHash == 0 || LevelRecordMetadata.IsAbyssCulled(snap.CurrentRegionTransform))
            {
                continue;
            }

            GdbView? ownerView = null;
            bool isNewPlacement = snap.IsNewPlacement && !baseSaveDoc.TryGetEntity(snap.RecordHash, out _, out _);
            if (baseLevelView is not null)
            {
                ownerView = ResolveRecordView(snap.RecordHash, baseLevelView, supplementalMulti);
            }
            snap.IsNewPlacement = isNewPlacement;

            string effectiveLayerName = string.IsNullOrWhiteSpace(snap.LayerName) ? "DefaultLayer" : snap.LayerName;
            string effectiveLayerTag = string.IsNullOrWhiteSpace(snap.LayerTag)
                ? (effectiveLayerName.Equals("DefaultLayer", StringComparison.OrdinalIgnoreCase)
                    ? "id_5A4CBED4"
                    : $"id_{Engine_GameHash.Fnv1(effectiveLayerName):X8}")
                : snap.LayerTag;
            GdbSaveLoadState effectiveLoadState = snap.LoadState;

            bool hasBaseSaveEntry = baseSaveDoc.TryGetEntity(snap.RecordHash, out GdbSaveEntityEntry baseSaveEntry, out _);

            GdbSaveLoadState layerLoadState = DetermineLayerLoadState(effectiveLayerName, layerMap, baseSaveDoc, result.SavePatch);
            bool loadStateOverridden = false;
            if (effectiveLoadState != GdbSaveLoadState.Unknown && effectiveLoadState != layerLoadState)
            {
                string stateSuffix = "_" + effectiveLoadState.ToXmlAttribute();
                if (!effectiveLayerName.EndsWith(stateSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    effectiveLayerName += stateSuffix;
                    effectiveLayerTag = $"id_{Engine_GameHash.Fnv1(effectiveLayerName):X8}";
                }

                SaveMapLayerPatch subLayerPatch = result.SavePatch.GetOrCreateLayer(effectiveLayerName, effectiveLayerTag);
                if (subLayerPatch.LoadOverride != effectiveLoadState)
                {
                    subLayerPatch.LoadOverride = effectiveLoadState;
                    result.LayerStateOverrideCount++;
                }
                loadStateOverridden = true;
            }

            if (isNewPlacement)
            {
                result.SavePatch.UpsertEntity(effectiveLayerName, snap.RecordHash, snap.EntityName, effectiveLayerTag);
                result.NewPlacementCount++;
            }
            else if (hasBaseSaveEntry)
            {
                if (!string.Equals(baseSaveEntry.LayerName, effectiveLayerName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(baseSaveEntry.EntityName, snap.EntityName, StringComparison.Ordinal))
                {
                    result.SavePatch.UpsertEntity(effectiveLayerName, snap.RecordHash, snap.EntityName, effectiveLayerTag);
                    result.LayerMoveCount++;
                }
            }
            else if (loadStateOverridden)
            {
                result.SavePatch.UpsertEntity(effectiveLayerName, snap.RecordHash, snap.EntityName, effectiveLayerTag);
                result.LayerMoveCount++;
            }

            bool transformChanged = isNewPlacement;
            if (!isNewPlacement && snap.HasBaseTransform)
            {
                transformChanged = !GdbTransformMath.AreTransformsEquivalent(
                    snap.CurrentRegionTransform,
                    snap.BaseRegionTransform);
            }

            if (!transformChanged)
            {
                if (!isNewPlacement)
                {
                    GdbView? cleanView = ownerView ?? ResolveRecordView(snap.RecordHash, baseLevelView, supplementalMulti);
                    ClearRecordTransformEntries(result.LevelGdbPatch, cleanView, snap.RecordHash);
                    ClearRecordTransformEntries(result.GlobalsGdbPatch, cleanView, snap.RecordHash);
                }
                continue;
            }

            if (!isNewPlacement)
            {
                result.ModifiedTransformCount++;
            }

            // In the 3D Level Editor, all entity transform edits belong to this level's GDB Title Update (<level>.gdb.tu).
            // They should NEVER pollute globals/globals.gdb.tu, even if the entity's base archetype originated from globals.gdb.
            GdbPatchDatabase targetDb = result.LevelGdbPatch;
            ownerView ??= ResolveRecordView(snap.RecordHash, baseLevelView, supplementalMulti);

            EmitRecordTransformEntries(
                targetDb,
                ownerView,
                snap.RecordHash,
                snap.ParentHash,
                snap.EntityName,
                snap.ModelPath,
                snap.CurrentRegionTransform,
                isNewPlacement);

            // Clean up any stale record entries in GlobalsGdbPatch that might have been saved by earlier buggy versions:
            ClearRecordTransformEntries(result.GlobalsGdbPatch, ownerView, snap.RecordHash);
        }

        return result;
    }

    /// <summary>
    /// Compiles and persists a <see cref="LevelScene"/>'s edits directly into the active <c>.f2mod</c> package
    /// (writing <c>&lt;level&gt;.gdb.tu</c>, optional <c>globals/globals.gdb.tu</c>, and <c>&lt;level&gt;.save.tu</c>),
    /// and updates the baseline transforms on the scene nodes so subsequent saves only diff new changes.
    /// </summary>
    public static bool SaveToModPackage(
        Node3D levelRoot,
        string modPackagePath,
        GdbView? baseLevelView,
        GdbSaveDocument? baseSaveDoc,
        GdbMulti? supplementalMulti,
        out LevelPatchCompileResult result,
        out string error)
    {
        error = string.Empty;
        string gdbSubPath = levelRoot.HasMeta(LevelRecordMetadata.LevelGdbPathMeta)
            ? NormalizeSubPath(levelRoot.GetMeta(LevelRecordMetadata.LevelGdbPathMeta).AsString())
            : string.Empty;

        ReadExistingModTuDatabases(
            modPackagePath,
            gdbSubPath,
            out GdbPatchDatabase? existingLevelPatch,
            out GdbPatchDatabase? existingGlobalsPatch);

        result = Compile(
            levelRoot,
            baseLevelView,
            baseSaveDoc,
            supplementalMulti,
            existingLevelPatch,
            existingGlobalsPatch);

        if (!WriteCompileResultToModPackage(modPackagePath, result, out error))
        {
            return false;
        }

        // Commit the new baseline state to the scene nodes:
        CommitBaselineStateToScene(levelRoot);
        return true;
    }

    /// <summary>
    /// Persists a <see cref="LevelPatchCompileResult"/> into <paramref name="modPackagePath"/> via
    /// <see cref="F2ModPackage.IncrementalSync"/>.
    /// </summary>
    public static bool WriteCompileResultToModPackage(
        string modPackagePath,
        LevelPatchCompileResult result,
        out string error)
    {
        error = string.Empty;
        if (string.IsNullOrEmpty(result.LevelGdbTuSubPath))
        {
            error = "Level root is missing gdb_level_path metadata.";
            return false;
        }

        const string globalsTuSubPath = "globals/globals.gdb.tu";
        var upserts = new List<(string VirtualPath, byte[] Data)>();
        var removals = new List<string>();

        if (result.LevelGdbPatch.Entries.Count > 0)
        {
            upserts.Add((result.LevelGdbTuSubPath, GdbWriter.Serialize(result.LevelGdbPatch)));
        }
        else
        {
            removals.Add(result.LevelGdbTuSubPath);
        }

        if (result.GlobalsGdbPatch.Entries.Count > 0)
        {
            upserts.Add((globalsTuSubPath, GdbWriter.Serialize(result.GlobalsGdbPatch)));
        }

        if (!result.SavePatch.IsEmpty && !string.IsNullOrEmpty(result.LevelSaveTuSubPath))
        {
            upserts.Add((result.LevelSaveTuSubPath, result.SavePatch.SerializeToUtf8()));
        }
        else if (!string.IsNullOrEmpty(result.LevelSaveTuSubPath))
        {
            removals.Add(result.LevelSaveTuSubPath);
        }

        try
        {
            string? dir = Path.GetDirectoryName(modPackagePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (!File.Exists(modPackagePath))
            {
                string modName = Path.GetFileNameWithoutExtension(modPackagePath);
                ModWorkspace.CreateModPackage(dir ?? string.Empty, modName, out _, out _);
            }

            F2ModPackage.IncrementalSync(modPackagePath, upserts, removals);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void ReadExistingModTuDatabases(
        string modPackagePath,
        string gdbSubPath,
        out GdbPatchDatabase? existingLevelPatch,
        out GdbPatchDatabase? existingGlobalsPatch)
    {
        existingLevelPatch = null;
        existingGlobalsPatch = null;

        string levelTuSubPath = string.IsNullOrEmpty(gdbSubPath) ? string.Empty : gdbSubPath + ".tu";
        const string globalsTuSubPath = "globals/globals.gdb.tu";

        if (!File.Exists(modPackagePath) ||
            !F2ModPackage.TryOpen(modPackagePath, out F2ModPackage? existingPkg, out _) ||
            existingPkg is null)
        {
            return;
        }

        using (existingPkg)
        {
            if (!string.IsNullOrEmpty(levelTuSubPath) &&
                existingPkg.TryGetEntry(levelTuSubPath, out F2ModEntry? lvlTuEntry) &&
                lvlTuEntry is not null)
            {
                existingLevelPatch = GdbPatchBuilder.LoadOrCreatePatchDatabase(existingPkg.ReadEntryBytes(lvlTuEntry));
            }

            if (existingPkg.TryGetEntry(globalsTuSubPath, out F2ModEntry? globTuEntry) &&
                globTuEntry is not null)
            {
                existingGlobalsPatch = GdbPatchBuilder.LoadOrCreatePatchDatabase(existingPkg.ReadEntryBytes(globTuEntry));
            }
        }
    }

    /// <summary>
    /// Refreshes <c>gdb_spawned_record_hashes</c> on <paramref name="levelRoot"/> after a successful save
    /// without mutating vanilla baselines or clearing new-placement flags, so subsequent saves continue
    /// to diff correctly against vanilla and preserve all mod records.
    /// </summary>
    public static void CommitBaselineStateToScene(Node3D levelRoot)
    {
        var liveNodesByHash = new Dictionary<uint, Node3D>();
        var layerContainers = new Dictionary<string, Node3D>(StringComparer.OrdinalIgnoreCase);
        CollectSceneNodesRecursive(levelRoot, liveNodesByHash, layerContainers, null);

        var spawned = new List<long>(liveNodesByHash.Count);
        foreach (var (hash, _) in liveNodesByHash)
        {
            spawned.Add(hash);
        }

        levelRoot.SetMeta(LevelRecordMetadata.SpawnedRecordHashesMeta, spawned.ToArray());
    }

    public static void ClearRecordTransformEntries(
        GdbPatchDatabase targetDb,
        GdbView? ownerView,
        uint recordHash)
    {
        if (ownerView is not null &&
            ownerView.TryLookup(recordHash, out int recordIndex) &&
            TryFindTransformHierarchy(
                ownerView,
                recordIndex,
                out uint transformOwnerHash,
                out _,
                out uint posVecHash,
                out uint rotVecHash,
                out _))
        {
            GdbPatchBuilder.RemoveEntry(targetDb, posVecHash);
            GdbPatchBuilder.RemoveEntry(targetDb, rotVecHash);
            GdbPatchBuilder.RemoveEntry(targetDb, transformOwnerHash);
        }

        uint transformHash = Engine_GameHash.Fnv1($"0x{recordHash:X8}_TransformComponent");
        uint fallbackPosVecHash = Engine_GameHash.Fnv1($"0x{recordHash:X8}_Position");
        uint fallbackRotVecHash = Engine_GameHash.Fnv1($"0x{recordHash:X8}_Rotation");
        uint meshCompHash = Engine_GameHash.Fnv1($"0x{recordHash:X8}_StaticMeshComponent");

        GdbPatchBuilder.RemoveEntry(targetDb, fallbackPosVecHash);
        GdbPatchBuilder.RemoveEntry(targetDb, fallbackRotVecHash);
        GdbPatchBuilder.RemoveEntry(targetDb, transformHash);
        GdbPatchBuilder.RemoveEntry(targetDb, meshCompHash);
        GdbPatchBuilder.RemoveEntry(targetDb, recordHash);
    }

    private static void EmitRecordTransformEntries(
        GdbPatchDatabase targetDb,
        GdbView? ownerView,
        uint recordHash,
        uint parentHash,
        string entityName,
        string modelPath,
        Transform3D regionTransform,
        bool isNewPlacement)
    {
        GdbTransformMath.ExtractGdbTransform(
            regionTransform,
            out float gx,
            out float gy,
            out float gz,
            out float rx,
            out float ry,
            out float rz,
            out float scale);
        float yaw = rx;

        uint rawX = BitConverter.SingleToUInt32Bits(gx);
        uint rawY = BitConverter.SingleToUInt32Bits(gy);
        uint rawZ = BitConverter.SingleToUInt32Bits(gz);
        uint rawRx = BitConverter.SingleToUInt32Bits(rx);
        uint rawRy = BitConverter.SingleToUInt32Bits(ry);
        uint rawRz = BitConverter.SingleToUInt32Bits(rz);
        uint rawYaw = BitConverter.SingleToUInt32Bits(yaw);
        uint rawScale = BitConverter.SingleToUInt32Bits(scale);

        targetDb.Names[GdbHash.Position] = "Position";
        targetDb.Names[GdbHash.Rotation] = "Rotation";
        targetDb.Names[GdbView.HashVecX] = "X";
        targetDb.Names[GdbView.HashVecY] = "Y";
        targetDb.Names[GdbView.HashVecZ] = "Z";
        if (!string.IsNullOrEmpty(entityName))
        {
            targetDb.Names[recordHash] = entityName;
        }

        // Case 1: Existing record in ownerView with a discoverable Transform component + Position Vec3:
        if (!isNewPlacement &&
            ownerView is not null &&
            ownerView.TryLookup(recordHash, out int recordIndex) &&
            TryFindTransformHierarchy(
                ownerView,
                recordIndex,
                out uint transformOwnerHash,
                out int transformOwnerIndex,
                out uint posVecHash,
                out uint rotVecHash,
                out bool rotationIsScalarFloat))
        {
            byte posFlags = ownerView.TryLookup(posVecHash, out int posIdx)
                ? ownerView.RecordSchemaFlags(posIdx)
                : (byte)0x01;
            var posEntry = GetOrCreateEntry(targetDb, posVecHash, posFlags);
            SetProperty(posEntry, GdbView.HashVecX, GdbFieldType.Float, rawX);
            SetProperty(posEntry, GdbView.HashVecY, GdbFieldType.Float, rawY);
            SetProperty(posEntry, GdbView.HashVecZ, GdbFieldType.Float, rawZ);

            byte ownerFlags = ownerView.RecordSchemaFlags(transformOwnerIndex);
            var ownerEntry = GetOrCreateEntry(targetDb, transformOwnerHash, ownerFlags);
            SetProperty(ownerEntry, GdbHash.Position, GdbFieldType.Reference, posVecHash);

            bool isPureYaw = MathF.Abs(ry) <= 1e-4f && MathF.Abs(rz) <= 1e-4f;
            if (rotationIsScalarFloat && isPureYaw)
            {
                SetProperty(ownerEntry, GdbHash.Rotation, GdbFieldType.Float, rawYaw);
            }
            else
            {
                uint effectiveRotHash = rotVecHash != 0
                    ? rotVecHash
                    : Engine_GameHash.Fnv1($"0x{recordHash:X8}_Rotation");
                byte rotFlags = rotVecHash != 0 && ownerView.TryLookup(rotVecHash, out int rotIdx)
                    ? ownerView.RecordSchemaFlags(rotIdx)
                    : posFlags;

                var rotEntry = GetOrCreateEntry(targetDb, effectiveRotHash, rotFlags);
                SetProperty(rotEntry, GdbView.HashVecX, GdbFieldType.Float, rawRx);
                SetProperty(rotEntry, GdbView.HashVecY, GdbFieldType.Float, rawRy);
                SetProperty(rotEntry, GdbView.HashVecZ, GdbFieldType.Float, rawRz);

                SetProperty(ownerEntry, GdbHash.Rotation, GdbFieldType.Reference, effectiveRotHash);
            }

            bool hadScale = ownerView.TryFindLocalRaw(transformOwnerIndex, HashScale, (int)GdbFieldType.Float, out _, out _);
            if (hadScale || MathF.Abs(scale - 1f) > 1e-3f)
            {
                targetDb.Names[HashScale] = "Scale";
                SetProperty(ownerEntry, HashScale, GdbFieldType.Float, rawScale);
            }

            return;
        }

        // Case 2: Newly placed record (or record without an existing transform component in ownerView):
        uint newTransformHash = Engine_GameHash.Fnv1($"0x{recordHash:X8}_TransformComponent");
        uint newPosVecHash = Engine_GameHash.Fnv1($"0x{recordHash:X8}_Position");
        uint newRotVecHash = Engine_GameHash.Fnv1($"0x{recordHash:X8}_Rotation");

        var newPosEntry = GetOrCreateEntry(targetDb, newPosVecHash, 0x01);
        SetProperty(newPosEntry, GdbView.HashVecX, GdbFieldType.Float, rawX);
        SetProperty(newPosEntry, GdbView.HashVecY, GdbFieldType.Float, rawY);
        SetProperty(newPosEntry, GdbView.HashVecZ, GdbFieldType.Float, rawZ);

        var newRotEntry = GetOrCreateEntry(targetDb, newRotVecHash, 0x01);
        SetProperty(newRotEntry, GdbView.HashVecX, GdbFieldType.Float, rawRx);
        SetProperty(newRotEntry, GdbView.HashVecY, GdbFieldType.Float, rawRy);
        SetProperty(newRotEntry, GdbView.HashVecZ, GdbFieldType.Float, rawRz);

        var newTransformEntry = GetOrCreateEntry(targetDb, newTransformHash, 0x01);
        SetProperty(newTransformEntry, GdbView.HashParent, GdbFieldType.Reference, GdbHash.SimpleTransformComponent);
        SetProperty(newTransformEntry, GdbHash.Position, GdbFieldType.Reference, newPosVecHash);
        SetProperty(newTransformEntry, GdbHash.Rotation, GdbFieldType.Reference, newRotVecHash);
        if (MathF.Abs(scale - 1f) > 1e-3f)
        {
            targetDb.Names[HashScale] = "Scale";
            SetProperty(newTransformEntry, HashScale, GdbFieldType.Float, rawScale);
        }

        var recordEntry = GetOrCreateEntry(targetDb, recordHash, 0x01);
        if (parentHash != 0)
        {
            SetProperty(recordEntry, GdbView.HashParent, GdbFieldType.Reference, parentHash);
        }
        SetProperty(recordEntry, GdbHash.SimpleTransformComponent, GdbFieldType.Reference, newTransformHash);
        targetDb.Names[GdbHash.SimpleTransformComponent] = "SimpleTransformComponent";

        if (!string.IsNullOrWhiteSpace(modelPath))
        {
            string cleanModel = modelPath.Replace('\\', '/').TrimStart('/');
            if (cleanModel.StartsWith("game://", StringComparison.OrdinalIgnoreCase))
            {
                cleanModel = cleanModel["game://".Length..];
            }

            uint meshCompHash = Engine_GameHash.Fnv1($"0x{recordHash:X8}_StaticMeshComponent");
            uint modelFileHash = Engine_PropModelIndex.PathHash(cleanModel);
            var meshCompEntry = GetOrCreateEntry(targetDb, meshCompHash, 0x01);
            SetProperty(meshCompEntry, GdbView.HashParent, GdbFieldType.Reference, GdbHash.StaticMeshComponent);
            SetProperty(meshCompEntry, GdbHash.ModelFile, GdbFieldType.ResourceReference, modelFileHash);
            targetDb.Names[GdbHash.ModelFile] = "ModelFile";
            targetDb.Names[modelFileHash] = cleanModel;

            SetProperty(recordEntry, GdbHash.StaticMeshComponent, GdbFieldType.Reference, meshCompHash);
            targetDb.Names[GdbHash.StaticMeshComponent] = "StaticMeshComponent";

        }
    }

    private static bool TryFindTransformHierarchy(
        GdbView view,
        int recordIndex,
        out uint transformOwnerHash,
        out int transformOwnerIndex,
        out uint posVecHash,
        out uint rotVecHash,
        out bool rotationIsScalarFloat)
    {
        transformOwnerHash = 0;
        transformOwnerIndex = -1;
        posVecHash = 0;
        rotVecHash = 0;
        rotationIsScalarFloat = false;

        foreach (uint compFieldHash in TransformComponentFieldHashes)
        {
            if (view.TryFindLocalRaw(recordIndex, compFieldHash, (int)GdbFieldType.Reference, out uint compHash, out _) &&
                view.TryLookup(compHash, out int compIndex) &&
                TryInspectTransformOwner(view, compIndex, out posVecHash, out rotVecHash, out rotationIsScalarFloat))
            {
                transformOwnerHash = compHash;
                transformOwnerIndex = compIndex;
                return true;
            }
        }

        if (TryInspectTransformOwner(view, recordIndex, out posVecHash, out rotVecHash, out rotationIsScalarFloat))
        {
            transformOwnerHash = view.RecordHash(recordIndex);
            transformOwnerIndex = recordIndex;
            return true;
        }

        foreach (var (_, fType, fRaw) in view.RawFields(recordIndex))
        {
            if (fType != GdbFieldType.Reference || fRaw == 0 ||
                !view.TryLookup(fRaw, out int compIndex) || compIndex == recordIndex)
            {
                continue;
            }

            if (TryInspectTransformOwner(view, compIndex, out posVecHash, out rotVecHash, out rotationIsScalarFloat))
            {
                transformOwnerHash = fRaw;
                transformOwnerIndex = compIndex;
                return true;
            }
        }

        return false;
    }

    private static bool TryInspectTransformOwner(
        GdbView view,
        int candidateIndex,
        out uint posVecHash,
        out uint rotVecHash,
        out bool rotationIsScalarFloat)
    {
        posVecHash = 0;
        rotVecHash = 0;
        rotationIsScalarFloat = false;

        if (!view.TryFindFieldOwner(candidateIndex, GdbHash.Position, (int)GdbFieldType.Reference, out GdbField posField, out int ownerIdx) ||
            posField.Raw == 0 ||
            !view.TryReadVec3Ref(posField.Raw, out _, out _, out _))
        {
            return false;
        }

        posVecHash = posField.Raw;
        if (view.TryFindLocalRaw(ownerIdx, GdbHash.Rotation, (int)GdbFieldType.Reference, out uint rHash, out _) &&
            rHash != 0 &&
            view.TryReadVec3Ref(rHash, out _, out _, out _))
        {
            rotVecHash = rHash;
        }
        else if (view.TryFindLocalRaw(ownerIdx, GdbHash.Rotation, (int)GdbFieldType.Float, out _, out _))
        {
            rotationIsScalarFloat = true;
        }

        return true;
    }

    private static GdbView? ResolveRecordView(uint recordHash, GdbView? baseLevelView, GdbMulti? supplementalMulti)
    {
        if (baseLevelView is not null && baseLevelView.TryLookup(recordHash, out _))
        {
            return baseLevelView;
        }

        if (supplementalMulti is not null && supplementalMulti.TryLookup(recordHash, out GdbCursor cursor) && cursor.Valid)
        {
            return cursor.View;
        }

        return baseLevelView;
    }

    private static GdbPatchEntry GetOrCreateEntry(GdbPatchDatabase db, uint id, byte schemaFlags)
    {
        GdbPatchEntry? existing = db.Entries.FirstOrDefault(e => e.Id == id);
        if (existing is not null)
        {
            if (existing.SchemaFlags == 0 && schemaFlags != 0)
            {
                existing.SchemaFlags = schemaFlags;
            }
            return existing;
        }

        var created = new GdbPatchEntry(id, schemaFlags != 0 ? schemaFlags : (byte)0x01);
        db.Entries.Add(created);
        return created;
    }

    private static void SetProperty(GdbPatchEntry entry, uint nameHash, GdbFieldType type, uint rawValue)
    {
        int idx = entry.Properties.FindIndex(p => p.NameHash == nameHash);
        if (idx >= 0)
        {
            entry.Properties[idx].Type = type;
            entry.Properties[idx].Value = rawValue;
        }
        else
        {
            entry.Properties.Add(new GdbPatchProperty(nameHash, type, rawValue));
        }
    }

    public static uint GenerateUniqueRecordHash(
        uint originalHash,
        string nodeName,
        IEnumerable<uint>? liveHashes = null,
        GdbSaveDocument? baseSaveDoc = null)
    {
        var existing = new HashSet<uint>();
        if (liveHashes is not null)
        {
            foreach (uint h in liveHashes)
            {
                existing.Add(h);
            }
        }
        if (baseSaveDoc is not null)
        {
            foreach (uint h in baseSaveDoc.EntriesByHash.Keys)
            {
                existing.Add(h);
            }
        }

        uint candidate;
        int attempts = 0;
        do
        {
            attempts++;
            candidate = Engine_GameHash.Fnv1($"{nodeName}_{originalHash:X8}_{Guid.NewGuid():N}");
            if (candidate == 0)
            {
                candidate = unchecked((uint)(0x90000000 + attempts));
            }
        }
        while (existing.Contains(candidate));

        return candidate;
    }

    private static void CollectSceneNodesRecursive(
        Node current,
        Dictionary<uint, Node3D> liveNodesByHash,
        Dictionary<string, Node3D> layerContainers,
        GdbSaveDocument? baseSaveDoc = null)
    {
        if (current is Node3D current3D)
        {
            if (current3D.HasMeta(LevelRecordMetadata.IsLayerContainerMeta) &&
                current3D.GetMeta(LevelRecordMetadata.IsLayerContainerMeta).AsBool() &&
                current3D.HasMeta(LevelRecordMetadata.LayerNameMeta))
            {
                string layerName = current3D.GetMeta(LevelRecordMetadata.LayerNameMeta).AsString();
                if (!string.IsNullOrEmpty(layerName) && !layerContainers.ContainsKey(layerName))
                {
                    layerContainers[layerName] = current3D;
                }
            }

            if (LevelRecordMetadata.IsRecordNode(current3D, out uint recordHash) && recordHash != 0)
            {
                if (liveNodesByHash.TryGetValue(recordHash, out Node3D? originalNode) && !ReferenceEquals(originalNode, current3D))
                {
                    // Duplicated node (Ctrl+D) detected via hash collision with an existing live node:
                    uint newHash = GenerateUniqueRecordHash(recordHash, current3D.Name.ToString(), liveNodesByHash.Keys, baseSaveDoc);
                    current3D.SetMeta(LevelRecordMetadata.RecordHashMeta, (long)newHash);
                    current3D.SetMeta(LevelRecordMetadata.ParentHashMeta, (long)recordHash);
                    current3D.SetMeta(LevelRecordMetadata.IsNewPlacementMeta, true);
                    current3D.RemoveMeta(LevelRecordMetadata.BaseTransformMeta);

                    string modelPath = LevelRecordMetadata.FindModelPath(current3D);
                    if (string.IsNullOrEmpty(modelPath) && originalNode is not null)
                    {
                        modelPath = LevelRecordMetadata.FindModelPath(originalNode);
                    }
                    if (!string.IsNullOrEmpty(modelPath))
                    {
                        current3D.SetMeta(LevelRecordMetadata.ModelPathMeta, modelPath);
                    }

                    string rawName = StripHashSuffix(current3D.Name.ToString());
                    current3D.Name = $"{rawName} [0x{newHash:X8}]";

                    liveNodesByHash[newHash] = current3D;
                }
                else
                {
                    liveNodesByHash[recordHash] = current3D;
                }
            }
        }

        int childCount = current.GetChildCount();
        for (int i = 0; i < childCount; i++)
        {
            CollectSceneNodesRecursive(current.GetChild(i), liveNodesByHash, layerContainers, baseSaveDoc);
        }
    }

    private static void ResolveEffectiveNodeLayerAndState(
        Node3D recordNode,
        GdbSaveDocument baseSaveDoc,
        out string layerName,
        out string layerTag,
        out GdbSaveLoadState loadState)
    {
        layerName = recordNode.HasMeta(LevelRecordMetadata.LayerNameMeta)
            ? recordNode.GetMeta(LevelRecordMetadata.LayerNameMeta).AsString()
            : string.Empty;
        layerTag = recordNode.HasMeta(LevelRecordMetadata.LayerTagMeta)
            ? recordNode.GetMeta(LevelRecordMetadata.LayerTagMeta).AsString()
            : string.Empty;
        string rawLoad = recordNode.HasMeta(LevelRecordMetadata.LayerLoadStateMeta)
            ? recordNode.GetMeta(LevelRecordMetadata.LayerLoadStateMeta).AsString()
            : string.Empty;

        Node? walk = recordNode.GetParent();
        while (walk is not null)
        {
            if (walk.HasMeta(LevelRecordMetadata.IsLayerContainerMeta) &&
                walk.GetMeta(LevelRecordMetadata.IsLayerContainerMeta).AsBool() &&
                walk.HasMeta(LevelRecordMetadata.LayerNameMeta))
            {
                string parentLayerName = walk.GetMeta(LevelRecordMetadata.LayerNameMeta).AsString();
                if (!string.IsNullOrEmpty(parentLayerName) &&
                    !string.Equals(parentLayerName, layerName, StringComparison.OrdinalIgnoreCase))
                {
                    layerName = parentLayerName;
                    layerTag = walk.HasMeta(LevelRecordMetadata.LayerTagMeta)
                        ? walk.GetMeta(LevelRecordMetadata.LayerTagMeta).AsString()
                        : string.Empty;
                    rawLoad = walk.HasMeta(LevelRecordMetadata.LayerLoadStateMeta)
                        ? walk.GetMeta(LevelRecordMetadata.LayerLoadStateMeta).AsString()
                        : rawLoad;
                }
                break;
            }
            walk = walk.GetParent();
        }

        if (string.IsNullOrWhiteSpace(layerName))
        {
            layerName = "DefaultLayer";
        }

        if (string.IsNullOrWhiteSpace(layerTag))
        {
            GdbSaveLayer? existing = baseSaveDoc.FindLayer(layerName);
            layerTag = existing is not null && !string.IsNullOrEmpty(existing.LayerTag)
                ? existing.LayerTag
                : layerName.Equals("DefaultLayer", StringComparison.OrdinalIgnoreCase)
                    ? "id_5A4CBED4"
                    : $"id_{Engine_GameHash.Fnv1(layerName):X8}";
        }

        loadState = GdbSaveLoadStateExtensions.ParseLoadState(rawLoad);
    }

    private static GdbSaveLoadState DetermineLayerLoadState(
        string layerName,
        IReadOnlyDictionary<string, LevelSceneLayerSnapshot> layerSnapshots,
        GdbSaveDocument baseSaveDoc,
        SaveMapPatch savePatch)
    {
        SaveMapLayerPatch? patchLayer = savePatch.Layers.FirstOrDefault(
            l => string.Equals(l.LayerName, layerName, StringComparison.OrdinalIgnoreCase));
        if (patchLayer?.LoadOverride is { } overrideState && overrideState != GdbSaveLoadState.Unknown)
        {
            return overrideState;
        }

        if (layerSnapshots.TryGetValue(layerName, out LevelSceneLayerSnapshot? ls) &&
            ls.LoadState != GdbSaveLoadState.Unknown)
        {
            return ls.LoadState;
        }

        GdbSaveLayer? baseLayer = baseSaveDoc.FindLayer(layerName);
        return baseLayer?.LoadState ?? GdbSaveLoadState.AlwaysOn;
    }

    private static string StripHashSuffix(string nodeName)
    {
        int bracket = nodeName.LastIndexOf(" [0x", StringComparison.OrdinalIgnoreCase);
        return bracket > 0 ? nodeName[..bracket].Trim() : nodeName.Trim();
    }

    private static string NormalizeSubPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string norm = path.Replace('\\', '/').TrimStart('/');
        if (norm.StartsWith("game://", StringComparison.OrdinalIgnoreCase))
        {
            norm = norm["game://".Length..];
        }
        if (norm.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
        {
            norm = norm["data/".Length..];
        }
        return norm;
    }

    private static GdbPatchDatabase ClonePatchDatabase(GdbPatchDatabase? source)
    {
        var clone = new GdbPatchDatabase();
        if (source is null)
        {
            return clone;
        }

        foreach (var kvp in source.Names)
        {
            clone.Names[kvp.Key] = kvp.Value;
        }
        clone.Aliases.AddRange(source.Aliases);

        foreach (GdbPatchEntry e in source.Entries)
        {
            var entry = new GdbPatchEntry(e.Id, e.SchemaFlags);
            foreach (GdbPatchProperty p in e.Properties)
            {
                entry.Properties.Add(new GdbPatchProperty(p.NameHash, p.Type, p.Value));
            }
            clone.Entries.Add(entry);
        }

        return clone;
    }
}
