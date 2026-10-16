namespace Refii.Tests.Modding;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GdUnit4;
using Godot;
using static GdUnit4.Assertions;
using Refii.Bnk;
using Refii.Core;
using Refii.EditorTools;
using Refii.Gdb;
using Refii.Level;
using Refii.Mods;
using Refii.Host;

/// <summary>
/// Unit and End-to-End Feature tests for the 3D Level Editing, <c>.save</c> Layer State Visualization,
/// Transform Round-Trip Math, Kynapse <c>.pdl</c> NavGraph parsing, and Zero-False-Diff
/// <c>&lt;level&gt;.gdb.tu</c> + <c>&lt;level&gt;.save.tu</c> Compilation pipeline.
/// </summary>
[TestSuite]
public class LevelEditingPipelineTests
{
    private const uint PropArchHash = 0x10000001;
    private const uint PropTableHash = 0x20000001;
    private const uint PropTableTransHash = 0x20000002;
    private const uint PropTablePosHash = 0x20000003;
    private const uint PropTableRotHash = 0x20000004;

    private const uint PropChairHash = 0x20000010;
    private const uint PropChairTransHash = 0x20000011;
    private const uint PropChairPosHash = 0x20000012;
    private const uint PropChairRotHash = 0x20000013;

    private const uint UnspawnedTriggerHash = 0x30000099;
    private const uint NewBarrelRecordHash = 0x40000055;

    private const string SampleSaveXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Save version="2">
          <Level name="AlbionBowerstoneMarket" version="4">
            <id_5A4CBED4 name="DefaultLayer" load="AlwaysOn">
              <Entity name="MarketTable_01">0x20000001</Entity>
              <Entity name="UnspawnedLogicTrigger">0x30000099</Entity>
            </id_5A4CBED4>
            <id_11223344 name="QuestIntro_Layer" load="InitiallyOn">
              <Entity name="MarketChair_01">0x20000010</Entity>
            </id_11223344>
            <id_AABBCCDD name="PostQuest_ stalls" load="InitiallyOff">
              <Entity name="HiddenStall_01">0x50000001</Entity>
            </id_AABBCCDD>
          </Level>
        </Save>
        """;

    [TestCase]
    public void TestGdbSaveMap_ParseDocumentPreservesAllLayers_WhileParseEntitiesFiltersRuntime()
    {
        byte[] saveBytes = Encoding.UTF8.GetBytes(SampleSaveXml);

        // 1. Editor/Document parser must preserve ALL layers (AlwaysOn, InitiallyOn, InitiallyOff):
        GdbSaveDocument doc = Engine_GdbSaveMap.ParseDocument(saveBytes, out SaveLayerStats docStats);
        AssertThat(doc.LevelName).IsEqual("AlbionBowerstoneMarket");
        AssertThat(doc.Layers.Count).IsEqual(3);
        AssertThat(docStats.Layers).IsEqual(3);
        AssertThat(docStats.LayersOff).IsEqual(1);
        AssertThat(docStats.Gated).IsEqual(1);
        AssertThat(doc.EntriesByHash.Count).IsEqual(4);

        AssertThat(doc.TryGetEntity(PropTableHash, out GdbSaveEntityEntry tableEntry, out GdbSaveLayer? defaultLayer)).IsTrue();
        AssertThat(tableEntry.EntityName).IsEqual("MarketTable_01");
        AssertThat(tableEntry.LoadState).IsEqual(GdbSaveLoadState.AlwaysOn);
        AssertThat(defaultLayer!.LayerTag).IsEqual("id_5A4CBED4");

        AssertThat(doc.TryGetEntity(0x50000001u, out GdbSaveEntityEntry hiddenEntry, out GdbSaveLayer? offLayer)).IsTrue();
        AssertThat(hiddenEntry.EntityName).IsEqual("HiddenStall_01");
        AssertThat(hiddenEntry.LoadState).IsEqual(GdbSaveLoadState.InitiallyOff);
        AssertThat(offLayer!.SpawnsByDefault).IsFalse();

        // 2. Runtime parser must continue skipping InitiallyOff layers for zero runtime overhead/regression:
        List<SaveEntity> runtimeEntities = Engine_GdbSaveMap.ParseEntities(saveBytes, out _);
        AssertThat(runtimeEntities.Any(e => e.Record == PropTableHash)).IsTrue();
        AssertThat(runtimeEntities.Any(e => e.Record == PropChairHash)).IsTrue();
        AssertThat(runtimeEntities.Any(e => e.Record == 0x50000001u)).IsFalse();
    }

    [TestCase]
    public void TestGdbTransformMath_RoundTripAndParentGroupInvariance()
    {
        // 1. Full 3D Euler + uniform scale round-trip:
        const float gx = 142.5f;
        const float gy = -88.25f;
        const float gz = 19.75f;
        const float rx = 0.42f;
        const float ry = -0.18f;
        const float rz = 0.31f;
        const float scale = 1.35f;

        Transform3D built = GdbTransformMath.BuildTransform(new GdbPlacement
        {
            X = gx,
            Y = gy,
            Z = gz,
            RotX = rx,
            RotY = ry,
            RotZ = rz,
            HasRotation = true,
            Scale = scale,
        });

        GdbTransformMath.ExtractGdbTransform(
            built,
            out float outX,
            out float outY,
            out float outZ,
            out float outRx,
            out float outRy,
            out float outRz,
            out float outScale);

        AssertThat(MathF.Abs(outX - gx) < 1e-4f).IsTrue();
        AssertThat(MathF.Abs(outY - gy) < 1e-4f).IsTrue();
        AssertThat(MathF.Abs(outZ - gz) < 1e-4f).IsTrue();
        AssertThat(MathF.Abs(outScale - scale) < 1e-4f).IsTrue();

        Transform3D rebuilt = GdbTransformMath.BuildTransform(new GdbPlacement
        {
            X = outX,
            Y = outY,
            Z = outZ,
            RotX = outRx,
            RotY = outRy,
            RotZ = outRz,
            HasRotation = true,
            Scale = outScale,
        });

        AssertThat(GdbTransformMath.AreTransformsEquivalent(built, rebuilt)).IsTrue();

        // 2. Parent/Group transform composition invariance (grouping a node under an offset parent container):
        var groupParent = new Transform3D(Basis.FromEuler(new Vector3(0f, 0.5f, 0f)), new Vector3(50f, 10f, -30f));
        Transform3D localUnderGroup = groupParent.AffineInverse() * built;
        Transform3D composedRegion = groupParent * localUnderGroup;

        AssertThat(GdbTransformMath.AreTransformsEquivalent(built, composedRegion)).IsTrue();
    }

    [TestCase]
    public void TestSaveMapPatch_SerializationAndLayerMoveComposition()
    {
        byte[] baseSaveBytes = Encoding.UTF8.GetBytes(SampleSaveXml);
        GdbSaveDocument baseDoc = Engine_GdbSaveMap.ParseDocument(baseSaveBytes, out _);

        var patch = new SaveMapPatch();
        // Flip InitiallyOff layer to AlwaysOn:
        SaveMapLayerPatch offLayerPatch = patch.GetOrCreateLayer("PostQuest_ stalls", "id_AABBCCDD");
        offLayerPatch.LoadOverride = GdbSaveLoadState.AlwaysOn;

        // Move MarketChair_01 from QuestIntro_Layer to DefaultLayer:
        patch.UpsertEntity("DefaultLayer", PropChairHash, "MarketChair_01", "id_5A4CBED4");

        // Add a brand-new entity to DefaultLayer:
        patch.UpsertEntity("DefaultLayer", NewBarrelRecordHash, "CustomBarrel_01", "id_5A4CBED4");

        // Remove MarketTable_01:
        patch.RemoveEntity(PropTableHash);

        byte[] patchBytes = patch.SerializeToUtf8();
        SaveMapPatch roundTripped = SaveMapPatch.Parse(patchBytes);
        AssertThat(roundTripped.IsEmpty).IsFalse();
        AssertThat(roundTripped.RemovedEntities.Contains(PropTableHash)).IsTrue();

        GdbSaveDocument composited = SaveMapCompositor.ApplyPatches(baseDoc, new[] { roundTripped });

        // MarketTable_01 was removed:
        AssertThat(composited.TryGetEntity(PropTableHash, out _, out _)).IsFalse();

        // MarketChair_01 moved to DefaultLayer and does NOT exist in QuestIntro_Layer anymore:
        AssertThat(composited.TryGetEntity(PropChairHash, out GdbSaveEntityEntry chairMoved, out _)).IsTrue();
        AssertThat(chairMoved.LayerName).IsEqual("DefaultLayer");
        GdbSaveLayer? questIntro = composited.FindLayer("QuestIntro_Layer");
        AssertThat(questIntro!.Entities.Any(e => e.RecordHash == PropChairHash)).IsFalse();

        // PostQuest_ stalls is now AlwaysOn:
        GdbSaveLayer? postQuest = composited.FindLayer("PostQuest_ stalls");
        AssertThat(postQuest!.LoadState).IsEqual(GdbSaveLoadState.AlwaysOn);
        AssertThat(composited.TryGetEntity(0x50000001u, out GdbSaveEntityEntry stallEntry, out _)).IsTrue();
        AssertThat(stallEntry.LoadState).IsEqual(GdbSaveLoadState.AlwaysOn);

        // CustomBarrel_01 exists in DefaultLayer:
        AssertThat(composited.TryGetEntity(NewBarrelRecordHash, out GdbSaveEntityEntry barrelEntry, out _)).IsTrue();
        AssertThat(barrelEntry.EntityName).IsEqual("CustomBarrel_01");
    }

    [TestCase]
    public void TestLevelGdbPatchCompiler_ZeroFalseDiff_WhenSceneUneditedOrGrouped()
    {
        byte[] baseGdbBytes = BuildSampleLevelGdbBytes();
        AssertThat(GdbView.TryParse(baseGdbBytes, out GdbView baseView, out _)).IsTrue();

        byte[] baseSaveBytes = Encoding.UTF8.GetBytes(SampleSaveXml);
        GdbSaveDocument baseSaveDoc = Engine_GdbSaveMap.ParseDocument(baseSaveBytes, out _);

        Transform3D tableBaseTransform = GdbTransformMath.BuildTransform(new GdbPlacement
        {
            X = 10f,
            Y = 20f,
            Z = 5f,
            RotX = 0.1f,
            RotY = 0.2f,
            RotZ = 0.3f,
            HasRotation = true,
            Scale = 1f,
        });
        Transform3D chairBaseTransform = GdbTransformMath.BuildTransform(new GdbPlacement
        {
            X = -15f,
            Y = 40f,
            Z = 2f,
            HasRotation = false,
            Yaw = 0.75f,
            Scale = 1f,
        });

        // Simulate grouping under a translated parent container whose composed region transform equals BaseRegionTransform:
        var parentGroup = new Transform3D(Basis.Identity, new Vector3(100f, 0f, -50f));
        Transform3D tableGroupedRegion = parentGroup * (parentGroup.AffineInverse() * tableBaseTransform);

        // Notice UnspawnedTriggerHash (0x30000099) is in DefaultLayer in .save, but was NOT spawned as a 3D node.
        // Because baselineSpawnedHashes only contains [PropTableHash, PropChairHash], it must NOT be marked deleted!
        var recordSnapshots = new List<LevelSceneRecordSnapshot>
        {
            new()
            {
                RecordHash = PropTableHash,
                ParentHash = PropArchHash,
                EntityName = "MarketTable_01",
                LayerName = "DefaultLayer",
                LayerTag = "id_5A4CBED4",
                LoadState = GdbSaveLoadState.AlwaysOn,
                SourceDb = "level",
                CurrentRegionTransform = tableGroupedRegion,
                BaseRegionTransform = tableBaseTransform,
                HasBaseTransform = true,
                IsNewPlacement = false,
            },
            new()
            {
                RecordHash = PropChairHash,
                ParentHash = PropArchHash,
                EntityName = "MarketChair_01",
                LayerName = "QuestIntro_Layer",
                LayerTag = "id_11223344",
                LoadState = GdbSaveLoadState.InitiallyOn,
                SourceDb = "level",
                CurrentRegionTransform = chairBaseTransform,
                BaseRegionTransform = chairBaseTransform,
                HasBaseTransform = true,
                IsNewPlacement = false,
            },
        };

        var layerSnapshots = new List<LevelSceneLayerSnapshot>
        {
            new() { LayerName = "DefaultLayer", LayerTag = "id_5A4CBED4", LoadState = GdbSaveLoadState.AlwaysOn },
            new() { LayerName = "QuestIntro_Layer", LayerTag = "id_11223344", LoadState = GdbSaveLoadState.InitiallyOn },
            new() { LayerName = "PostQuest_ stalls", LayerTag = "id_AABBCCDD", LoadState = GdbSaveLoadState.InitiallyOff },
        };

        var baselineSpawned = new uint[] { PropTableHash, PropChairHash };

        LevelPatchCompileResult result = LevelGdbPatchCompiler.CompileSnapshots(
            "levels/albion/bowerstonemarket.gdb",
            "levels/albion/bowerstonemarket.save",
            recordSnapshots,
            layerSnapshots,
            baselineSpawned,
            baseView,
            baseSaveDoc);

        AssertThat(result.HasAnyChanges).IsFalse();
        AssertThat(result.ModifiedTransformCount).IsEqual(0);
        AssertThat(result.NewPlacementCount).IsEqual(0);
        AssertThat(result.DeletedRecordCount).IsEqual(0);
        AssertThat(result.LayerMoveCount).IsEqual(0);
        AssertThat(result.LayerStateOverrideCount).IsEqual(0);
        AssertThat(result.LevelGdbPatch.Entries.Count).IsEqual(0);
        AssertThat(result.SavePatch.IsEmpty).IsTrue();
    }

    [TestCase]
    public void TestLevelEditingPipeline_CompileSyncAndMultiModMount_E2E()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_level_edit_e2e_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            const string gdbSubPath = "levels/albion/bowerstonemarket.gdb";
            const string saveSubPath = "levels/albion/bowerstonemarket.save";

            byte[] baseGdbBytes = BuildSampleLevelGdbBytes();
            byte[] baseSaveBytes = Encoding.UTF8.GetBytes(SampleSaveXml);

            AssertThat(GdbView.TryParse(baseGdbBytes, out GdbView baseView, out _)).IsTrue();
            GdbSaveDocument baseSaveDoc = Engine_GdbSaveMap.ParseDocument(baseSaveBytes, out _);

            Transform3D tableBaseTransform = GdbTransformMath.BuildTransform(new GdbPlacement
            {
                X = 10f,
                Y = 20f,
                Z = 5f,
                RotX = 0.1f,
                RotY = 0.2f,
                RotZ = 0.3f,
                HasRotation = true,
                Scale = 1f,
            });

            // User moves MarketTable_01 to a new position (55, 65, 12) and scales it to 1.5x:
            Transform3D tableMovedTransform = GdbTransformMath.BuildTransform(new GdbPlacement
            {
                X = 55f,
                Y = 65f,
                Z = 12f,
                RotX = 0.1f,
                RotY = 0.2f,
                RotZ = 0.3f,
                HasRotation = true,
                Scale = 1.5f,
            });

            // User places a brand new barrel record at (-30, 15, 4):
            Transform3D newBarrelTransform = GdbTransformMath.BuildTransform(new GdbPlacement
            {
                X = -30f,
                Y = 15f,
                Z = 4f,
                RotX = 0f,
                RotY = 0.5f,
                RotZ = 0f,
                HasRotation = true,
                Scale = 1f,
            });

            // User deletes MarketChair_01 (PropChairHash was in baselineSpawned, omitted from live snapshots).
            // User also changes "PostQuest_ stalls" layer from InitiallyOff -> AlwaysOn.
            var recordSnapshots = new List<LevelSceneRecordSnapshot>
            {
                new()
                {
                    RecordHash = PropTableHash,
                    ParentHash = PropArchHash,
                    EntityName = "MarketTable_01",
                    LayerName = "DefaultLayer",
                    LayerTag = "id_5A4CBED4",
                    LoadState = GdbSaveLoadState.AlwaysOn,
                    SourceDb = "level",
                    CurrentRegionTransform = tableMovedTransform,
                    BaseRegionTransform = tableBaseTransform,
                    HasBaseTransform = true,
                    IsNewPlacement = false,
                },
                new()
                {
                    RecordHash = NewBarrelRecordHash,
                    ParentHash = PropArchHash,
                    EntityName = "CustomBarrel_01",
                    LayerName = "DefaultLayer",
                    LayerTag = "id_5A4CBED4",
                    LoadState = GdbSaveLoadState.AlwaysOn,
                    SourceDb = "level",
                    ModelPath = "data/art/objects/barrel.mdl",
                    CurrentRegionTransform = newBarrelTransform,
                    BaseRegionTransform = newBarrelTransform,
                    HasBaseTransform = true,
                    IsNewPlacement = true,
                },
            };

            var layerSnapshots = new List<LevelSceneLayerSnapshot>
            {
                new() { LayerName = "DefaultLayer", LayerTag = "id_5A4CBED4", LoadState = GdbSaveLoadState.AlwaysOn },
                new() { LayerName = "QuestIntro_Layer", LayerTag = "id_11223344", LoadState = GdbSaveLoadState.InitiallyOn },
                new() { LayerName = "PostQuest_ stalls", LayerTag = "id_AABBCCDD", LoadState = GdbSaveLoadState.AlwaysOn },
            };

            var baselineSpawned = new uint[] { PropTableHash, PropChairHash };

            LevelPatchCompileResult compileResult = LevelGdbPatchCompiler.CompileSnapshots(
                gdbSubPath,
                saveSubPath,
                recordSnapshots,
                layerSnapshots,
                baselineSpawned,
                baseView,
                baseSaveDoc);

            AssertThat(compileResult.HasAnyChanges).IsTrue();
            AssertThat(compileResult.ModifiedTransformCount).IsEqual(1);
            AssertThat(compileResult.NewPlacementCount).IsEqual(1);
            AssertThat(compileResult.DeletedRecordCount).IsEqual(1);
            AssertThat(compileResult.LayerStateOverrideCount).IsEqual(1);

            string modPath = Path.Combine(tempDir, "LevelOverhaul.f2mod");
            AssertThat(LevelGdbPatchCompiler.WriteCompileResultToModPackage(modPath, compileResult, out string writeErr)).IsTrue();
            AssertThat(writeErr).IsEmpty();
            AssertThat(File.Exists(modPath)).IsTrue();

            // Verify ModContentScanner discovers our level .gdb.tu and .save.tu entries:
            ModContentSummary scanSummary = ModContentScanner.ScanMod(tempDir, "LevelOverhaul");
            AssertThat(scanSummary.PatchedRecordCount > 0).IsTrue();
            AssertThat(scanSummary.Entries.Any(e => e.ContainerSubPath.Equals(gdbSubPath + ".tu", StringComparison.OrdinalIgnoreCase))).IsTrue();
            AssertThat(scanSummary.Entries.Any(e => e.ContainerSubPath.Equals(saveSubPath + ".tu", StringComparison.OrdinalIgnoreCase))).IsTrue();

            // Mount base files and apply mod overlays into BnkFileSystem:
            var fs = new BnkFileSystem();
            fs.AddVirtualFile("data/" + gdbSubPath, "base", baseGdbBytes.Length, () => baseGdbBytes);
            fs.AddVirtualFile(gdbSubPath, "base", baseGdbBytes.Length, () => baseGdbBytes);
            fs.AddVirtualFile("data/" + saveSubPath, "base", baseSaveBytes.Length, () => baseSaveBytes);
            fs.AddVirtualFile(saveSubPath, "base", baseSaveBytes.Length, () => baseSaveBytes);

            var order = new ModLoadOrder();
            order.Enabled.Add("LevelOverhaul");

            var installs = ModWorkspace.DiscoverModInstalls(tempDir);
            var overlays = ModIndex.ResolveOverlays(tempDir, order, installs);

            GdbPatchCompositor.Apply(fs, overlays, tempDir);
            SaveMapCompositor.Apply(fs, overlays);

            // Read back overlaid .save and overlaid .gdb from BnkFileSystem:
            AssertThat(fs.TryGetFile(saveSubPath, out BnkFile overlaidSaveFile)).IsTrue();
            byte[] overlaidSaveBytes = overlaidSaveFile.ReadAllBytes();

            var levelEntry = new LevelEntry
            {
                LevelPath = "bowerstonemarket",
                MainEhfPath = "levels/albion/bowerstonemarket/bowerstonemarket.ehf",
            };
            byte[]? overlaidGdbBytes = Engine_LevelGdb.TryRead(
                levelEntry,
                vpath => fs.TryGetFile(vpath, out BnkFile f) ? f.ReadAllBytes() : null,
                out string resolvedGdbPath);

            AssertThat(overlaidGdbBytes).IsNotNull();
            AssertThat(resolvedGdbPath).IsEqual(gdbSubPath);
            AssertThat(GdbView.TryParse(overlaidGdbBytes!, out GdbView overlaidLevelView, out _)).IsTrue();

            // Verify overlaid .save reflects our deletion, addition, and layer load override:
            GdbSaveDocument mountedSaveDoc = Engine_GdbSaveMap.ParseDocument(overlaidSaveBytes, out _);
            AssertThat(mountedSaveDoc.TryGetEntity(PropChairHash, out _, out _)).IsFalse();
            AssertThat(mountedSaveDoc.TryGetEntity(NewBarrelRecordHash, out GdbSaveEntityEntry barrelSave, out _)).IsTrue();
            AssertThat(barrelSave.EntityName).IsEqual("CustomBarrel_01");
            AssertThat(mountedSaveDoc.TryGetEntity(0x50000001u, out GdbSaveEntityEntry stallSave, out _)).IsTrue();
            AssertThat(stallSave.LoadState).IsEqual(GdbSaveLoadState.AlwaysOn);

            // Verify overlaid GdbView resolves the updated MarketTable_01 position (55, 65, 12) and scale (1.5):
            List<KeyValuePair<uint, string>> savePairs = Engine_GdbSaveMap.Parse(overlaidSaveBytes, out _);
            List<GdbPlacement> placements = Engine_GdbPlacements.Parse(overlaidLevelView, savePairs);

            GdbPlacement? tablePlacement = placements.FirstOrDefault(p => p.Hash == PropTableHash);
            AssertThat(tablePlacement).IsNotNull();
            AssertThat(MathF.Abs(tablePlacement!.X - 55f) < 1e-3f).IsTrue();
            AssertThat(MathF.Abs(tablePlacement.Y - 65f) < 1e-3f).IsTrue();
            AssertThat(MathF.Abs(tablePlacement.Z - 12f) < 1e-3f).IsTrue();
            AssertThat(MathF.Abs(tablePlacement.Scale - 1.5f) < 1e-3f).IsTrue();

            GdbPlacement? barrelPlacement = placements.FirstOrDefault(p => p.Hash == NewBarrelRecordHash);
            AssertThat(barrelPlacement).IsNotNull();
            AssertThat(MathF.Abs(barrelPlacement!.X - (-30f)) < 1e-3f).IsTrue();
            AssertThat(MathF.Abs(barrelPlacement.Y - 15f) < 1e-3f).IsTrue();
            AssertThat(MathF.Abs(barrelPlacement.Z - 4f) < 1e-3f).IsTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [TestCase]
    public void TestKynapseGraph_TryParseVersion3BinaryPayload()
    {
        byte[] pdlBytes = BuildSampleKynapsePdlVersion3();
        bool ok = Engine_KynapseGraph.TryParse(pdlBytes, out KynapseGraphData graph, out string error);

        AssertThat(ok).IsTrue();
        AssertThat(error).IsEmpty();
        AssertThat(graph.Version).IsEqual(3u);
        AssertThat(graph.Vertices.Length).IsEqual(2);
        // Game (10, 20, 30) -> Godot region (X=10, Y=Z=30, Z=Y=20):
        AssertThat(graph.Vertices[0]).IsEqual(new Vector3(10f, 30f, 20f));
        AssertThat(graph.Vertices[1]).IsEqual(new Vector3(40f, 60f, 50f));
        AssertThat(graph.Edges.Length).IsEqual(1);
        AssertThat(graph.Edges[0]).IsEqual((0u, 1u));
    }

    [TestCase]
    public void TestEditorPropHierarchy_FastNamingAndSeparationOfLayersAndStaticEnv()
    {
        byte[] saveBytes = Encoding.UTF8.GetBytes(SampleSaveXml);
        GdbSaveDocument saveDoc = Engine_GdbSaveMap.ParseDocument(saveBytes, out _);
        var hierarchy = new Engine_LevelProps.EditorPropHierarchy(saveDoc, "albion/bowerstone.gdb");

        var placements = new List<GdbPlacement>
        {
            new GdbPlacement { Hash = PropTableHash, EntityName = "MarketTable_01", X = 10f, Y = 20f, Z = 5f },
            new GdbPlacement { Hash = PropChairHash, EntityName = "MarketChair_01", X = 15f, Y = 25f, Z = 5f },
        };
        hierarchy.IndexPlacements(placements);

        // 1. Fast O(1) Unique Naming counter verification
        AssertThat(hierarchy.GetUniqueName("Tree")).IsEqual("Tree");
        AssertThat(hierarchy.GetUniqueName("Tree")).IsEqual("Tree (2)");
        AssertThat(hierarchy.GetUniqueName("Tree")).IsEqual("Tree (3)");
        AssertThat(hierarchy.GetUniqueName("Rock")).IsEqual("Rock");

        // 2. Authored GDB records (hash != 0) must resolve to their respective Layers
        string tableLayer = hierarchy.ResolveTarget(PropTableHash, "MarketTable_01", out string tableName, out _, out GdbSaveLoadState tableLoad);
        AssertThat(tableLayer).IsEqual("DefaultLayer");
        AssertThat(tableName).IsEqual("MarketTable_01");
        AssertThat(tableLoad).IsEqual(GdbSaveLoadState.AlwaysOn);

        string chairLayer = hierarchy.ResolveTarget(PropChairHash, "MarketChair_01", out string chairName, out _, out GdbSaveLoadState chairLoad);
        AssertThat(chairLayer).IsEqual("QuestIntro_Layer");
        AssertThat(chairName).IsEqual("MarketChair_01");
        AssertThat(chairLoad).IsEqual(GdbSaveLoadState.InitiallyOn);

        // 3. Unassigned authored records must resolve to Unassigned layer
        string unassignedLayer = hierarchy.ResolveTarget(0x99999999u, "MysteryProp", out string unassignedName, out _, out _);
        AssertThat(unassignedLayer).IsEqual(Engine_LevelProps.UnassignedLayerName);
        AssertThat(unassignedName).IsEqual("MysteryProp");

        // 4. Static clutter without GDB record (hash == 0) must land under StaticEnvironment
        string staticLayer = hierarchy.ResolveTarget(0, "TrimRock", out string staticName, out _, out _);
        AssertThat(staticLayer).IsEqual(Engine_LevelProps.StaticEnvironmentContainerName);
        AssertThat(staticName).IsEqual("TrimRock");
    }

    private static byte[] BuildSampleLevelGdbBytes()
    {
        var db = new GdbPatchDatabase();

        // Base prop archetype:
        var arch = new GdbPatchEntry(PropArchHash, 0x01);
        db.Entries.Add(arch);

        // Table Position Vec3 (10, 20, 5):
        var tablePos = new GdbPatchEntry(PropTablePosHash, 0x01);
        tablePos.Properties.Add(new GdbPatchProperty(GdbView.HashVecX, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(10f)));
        tablePos.Properties.Add(new GdbPatchProperty(GdbView.HashVecY, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(20f)));
        tablePos.Properties.Add(new GdbPatchProperty(GdbView.HashVecZ, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(5f)));
        db.Entries.Add(tablePos);

        // Table Rotation Vec3 (0.1, 0.2, 0.3):
        var tableRot = new GdbPatchEntry(PropTableRotHash, 0x01);
        tableRot.Properties.Add(new GdbPatchProperty(GdbView.HashVecX, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(0.1f)));
        tableRot.Properties.Add(new GdbPatchProperty(GdbView.HashVecY, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(0.2f)));
        tableRot.Properties.Add(new GdbPatchProperty(GdbView.HashVecZ, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(0.3f)));
        db.Entries.Add(tableRot);

        // Table TransformComponent:
        var tableTrans = new GdbPatchEntry(PropTableTransHash, 0x01);
        tableTrans.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, GdbHash.SimpleTransformComponent));
        tableTrans.Properties.Add(new GdbPatchProperty(GdbHash.Position, GdbFieldType.Reference, PropTablePosHash));
        tableTrans.Properties.Add(new GdbPatchProperty(GdbHash.Rotation, GdbFieldType.Reference, PropTableRotHash));
        db.Entries.Add(tableTrans);

        // Table Record:
        var tableRec = new GdbPatchEntry(PropTableHash, 0x01);
        tableRec.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, PropArchHash));
        tableRec.Properties.Add(new GdbPatchProperty(GdbHash.SimpleTransformComponent, GdbFieldType.Reference, PropTableTransHash));
        db.Entries.Add(tableRec);

        // Chair Position Vec3 (-15, 40, 2):
        var chairPos = new GdbPatchEntry(PropChairPosHash, 0x01);
        chairPos.Properties.Add(new GdbPatchProperty(GdbView.HashVecX, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(-15f)));
        chairPos.Properties.Add(new GdbPatchProperty(GdbView.HashVecY, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(40f)));
        chairPos.Properties.Add(new GdbPatchProperty(GdbView.HashVecZ, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(2f)));
        db.Entries.Add(chairPos);

        // Chair TransformComponent (uses scalar float Rotation):
        var chairTrans = new GdbPatchEntry(PropChairTransHash, 0x01);
        chairTrans.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, GdbHash.TransformComponent));
        chairTrans.Properties.Add(new GdbPatchProperty(GdbHash.Position, GdbFieldType.Reference, PropChairPosHash));
        chairTrans.Properties.Add(new GdbPatchProperty(GdbHash.Rotation, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(0.75f)));
        db.Entries.Add(chairTrans);

        // Chair Record:
        var chairRec = new GdbPatchEntry(PropChairHash, 0x01);
        chairRec.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, PropArchHash));
        chairRec.Properties.Add(new GdbPatchProperty(GdbHash.TransformComponent, GdbFieldType.Reference, PropChairTransHash));
        db.Entries.Add(chairRec);

        return GdbWriter.Serialize(db);
    }

    private static byte[] BuildSampleKynapsePdlVersion3()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        // 21-byte magic header:
        bw.Write(Encoding.ASCII.GetBytes(Engine_KynapseGraph.MagicHeader));
        // Version = 3:
        bw.Write(3u);
        // 11 bytes of 0xAA padding:
        for (int i = 0; i < 11; i++)
        {
            bw.Write((byte)0xAA);
        }

        // 20-byte header: flags=0, unk1=0, unk2=0, maxVertices=2, numVertices=2
        bw.Write(0u);
        bw.Write(0u);
        bw.Write(0u);
        bw.Write(2u);
        bw.Write(2u);

        // Vertex 0: vid=0, x=10, y=20, z=30, tag=0x11
        bw.Write(0u);
        bw.Write(10f);
        bw.Write(20f);
        bw.Write(30f);
        bw.Write(0x11u);

        // Vertex 1: vid=1, x=40, y=50, z=60, tag=0x22
        bw.Write(1u);
        bw.Write(40f);
        bw.Write(50f);
        bw.Write(60f);
        bw.Write(0x22u);

        // Edge section: unk=0, maxEdges=1, numEdges=1
        bw.Write(0u);
        bw.Write(1u);
        bw.Write(1u);

        // maxEdges * 8 bytes of edge headers:
        bw.Write(0UL);

        // vStarts[1] = { 0 }, vEnds[1] = { 1 }:
        bw.Write(0u);
        bw.Write(1u);

        bw.Flush();
        return ms.ToArray();
    }

    [TestCase]
    public void TestLevelEditingPipeline_GenerateUniqueRecordHash_AvoidsCollisions()
    {
        byte[] baseSaveBytes = Encoding.UTF8.GetBytes(SampleSaveXml);
        GdbSaveDocument baseSaveDoc = Engine_GdbSaveMap.ParseDocument(baseSaveBytes, out _);

        var liveHashes = new HashSet<uint> { PropTableHash, PropChairHash };
        uint minted = LevelGdbPatchCompiler.GenerateUniqueRecordHash(
            PropTableHash, "MarketTable_01", liveHashes, baseSaveDoc);

        AssertThat(minted != 0).IsTrue();
        AssertThat(minted != PropTableHash).IsTrue();
        AssertThat(minted != PropChairHash).IsTrue();
        AssertThat(baseSaveDoc.EntriesByHash.ContainsKey(minted)).IsFalse();
    }

    [TestCase]
    public void TestLevelEditingPipeline_NewPlacement_WithModelPath_EmitsStaticMeshComponentAndRoutesToLevelTu()
    {
        byte[] baseGdbBytes = BuildSampleLevelGdbBytes();
        byte[] baseSaveBytes = Encoding.UTF8.GetBytes(SampleSaveXml);
        AssertThat(GdbView.TryParse(baseGdbBytes, out GdbView baseView, out _)).IsTrue();
        GdbSaveDocument baseSaveDoc = Engine_GdbSaveMap.ParseDocument(baseSaveBytes, out _);

        uint newPropHash = 0x90001234;
        var recordSnapshots = new List<LevelSceneRecordSnapshot>
        {
            new()
            {
                RecordHash = PropTableHash,
                ParentHash = PropArchHash,
                EntityName = "MarketTable_01",
                LayerName = "DefaultLayer",
                LayerTag = "id_5A4CBED4",
                LoadState = GdbSaveLoadState.AlwaysOn,
                SourceDb = "level",
                CurrentRegionTransform = Transform3D.Identity,
                BaseRegionTransform = Transform3D.Identity,
                HasBaseTransform = true,
                IsNewPlacement = false,
            },
            new()
            {
                RecordHash = newPropHash,
                ParentHash = PropArchHash,
                EntityName = "NewDuplicatedTable",
                LayerName = "DefaultLayer",
                LayerTag = "id_5A4CBED4",
                LoadState = GdbSaveLoadState.AlwaysOn,
                SourceDb = "globals", // Even if source db says globals, a new placement MUST go to level patch!
                ModelPath = "art/props/table.mdl",
                CurrentRegionTransform = new Transform3D(Basis.Identity, new Vector3(5, 0, 5)),
                BaseRegionTransform = Transform3D.Identity,
                HasBaseTransform = false,
                IsNewPlacement = true,
            },
        };

        var baselineSpawned = new uint[] { PropTableHash, PropChairHash };

        LevelPatchCompileResult result = LevelGdbPatchCompiler.CompileSnapshots(
            "levels/albion/bowerstonemarket.gdb",
            "levels/albion/bowerstonemarket.save",
            recordSnapshots,
            null,
            baselineSpawned,
            baseView,
            baseSaveDoc);

        // 1. New placement is counted:
        AssertThat(result.NewPlacementCount).IsEqual(1);

        // 2. Target DB is LevelGdbPatch, not GlobalsGdbPatch:
        AssertThat(result.LevelGdbPatch.Entries.Any(e => e.Id == newPropHash)).IsTrue();
        AssertThat(result.GlobalsGdbPatch.Entries.Any(e => e.Id == newPropHash)).IsFalse();

        // 3. StaticMeshComponent is emitted even though ParentHash != 0:
        var newEntry = result.LevelGdbPatch.Entries.First(e => e.Id == newPropHash);
        AssertThat(newEntry.Properties.Any(p => p.NameHash == GdbHash.StaticMeshComponent)).IsTrue();

        // 4. Deleted Chair is recorded in SavePatch removals:
        AssertThat(result.DeletedRecordCount).IsEqual(1);
        AssertThat(result.SavePatch.RemovedEntities.Contains(PropChairHash)).IsTrue();

        // 5. New placement is upserted in SavePatch:
        AssertThat(result.SavePatch.Layers.Any(l => l.UpsertedEntities.Any(e => e.RecordHash == newPropHash))).IsTrue();
    }

    [TestCase]
    public void TestLevelGdbPatchCompiler_DeletionsOnlyFromSpawnedSet()
    {
        byte[] baseGdbBytes = BuildSampleLevelGdbBytes();
        byte[] baseSaveBytes = Encoding.UTF8.GetBytes(SampleSaveXml);
        AssertThat(GdbView.TryParse(baseGdbBytes, out GdbView baseView, out _)).IsTrue();
        GdbSaveDocument baseSaveDoc = Engine_GdbSaveMap.ParseDocument(baseSaveBytes, out _);

        // 4 total entities in SampleSaveXml:
        // Table (0x20000001), UnspawnedTrigger (0x30000099), Chair (0x20000010), HiddenStall (0x50000001).
        // Only Table & Chair were spawned in the 3D scene:
        var baselineSpawned = new uint[] { PropTableHash, PropChairHash };

        // Only Table remains in the live scene snapshots (Chair was deleted by user):
        var recordSnapshots = new List<LevelSceneRecordSnapshot>
        {
            new()
            {
                RecordHash = PropTableHash,
                ParentHash = PropArchHash,
                EntityName = "MarketTable_01",
                LayerName = "DefaultLayer",
                LayerTag = "id_5A4CBED4",
                LoadState = GdbSaveLoadState.AlwaysOn,
                SourceDb = "level",
                CurrentRegionTransform = Transform3D.Identity,
                BaseRegionTransform = Transform3D.Identity,
                HasBaseTransform = true,
                IsNewPlacement = false,
            },
        };

        LevelPatchCompileResult result = LevelGdbPatchCompiler.CompileSnapshots(
            "levels/albion/bowerstonemarket.gdb",
            "levels/albion/bowerstonemarket.save",
            recordSnapshots,
            null,
            baselineSpawned,
            baseView,
            baseSaveDoc);

        // Deleted count must be EXACTLY 1 (Chair only), not 3 (UnspawnedTrigger & HiddenStall must not be deleted):
        AssertThat(result.DeletedRecordCount).IsEqual(1);
        AssertThat(result.SavePatch.RemovedEntities.Contains(PropChairHash)).IsTrue();
        AssertThat(result.SavePatch.RemovedEntities.Contains(UnspawnedTriggerHash)).IsFalse();
        AssertThat(result.SavePatch.RemovedEntities.Contains(0x50000001u)).IsFalse();
    }

    [TestCase]
    public void TestLevelGdbPatchCompiler_DeletedEntityEmitsCulledGdbTransform()
    {
        byte[] baseGdbBytes = BuildSampleLevelGdbBytes();
        byte[] baseSaveBytes = Encoding.UTF8.GetBytes(SampleSaveXml);
        AssertThat(GdbView.TryParse(baseGdbBytes, out GdbView baseView, out _)).IsTrue();
        GdbSaveDocument baseSaveDoc = Engine_GdbSaveMap.ParseDocument(baseSaveBytes, out _);

        var baselineSpawned = new uint[] { PropTableHash, PropChairHash };
        var recordSnapshots = new List<LevelSceneRecordSnapshot>
        {
            new()
            {
                RecordHash = PropTableHash,
                ParentHash = PropArchHash,
                EntityName = "MarketTable_01",
                LayerName = "DefaultLayer",
                LayerTag = "id_5A4CBED4",
                LoadState = GdbSaveLoadState.AlwaysOn,
                SourceDb = "level",
                CurrentRegionTransform = Transform3D.Identity,
                BaseRegionTransform = Transform3D.Identity,
                HasBaseTransform = true,
                IsNewPlacement = false,
            },
        };

        LevelPatchCompileResult result = LevelGdbPatchCompiler.CompileSnapshots(
            "levels/albion/bowerstonemarket.gdb",
            "levels/albion/bowerstonemarket.save",
            recordSnapshots,
            null,
            baselineSpawned,
            baseView,
            baseSaveDoc);

        // Verify native abyss-culling transforms in LevelGdbPatch for the deleted chair:
        AssertThat(result.LevelGdbPatch.Entries.Any(e => e.Id == PropChairPosHash)).IsTrue();
        var posEntry = result.LevelGdbPatch.Entries.First(e => e.Id == PropChairPosHash);
        var zProp = posEntry.Properties.First(p => p.NameHash == GdbView.HashVecZ);
        float posZ = BitConverter.UInt32BitsToSingle(zProp.Value);
        AssertThat(posZ).IsEqual(-9999f);

        AssertThat(result.LevelGdbPatch.Entries.Any(e => e.Id == PropChairTransHash)).IsTrue();
        var transEntry = result.LevelGdbPatch.Entries.First(e => e.Id == PropChairTransHash);
        var scaleProp = transEntry.Properties.First(p => p.NameHash == LevelGdbPatchCompiler.HashScale);
        float scale = BitConverter.UInt32BitsToSingle(scaleProp.Value);
        AssertThat(scale).IsEqual(0.0f);
    }

    [TestCase]
    public void TestLevelGdbPatchCompiler_ResetToVanillaPurgesOverride()
    {
        byte[] baseGdbBytes = BuildSampleLevelGdbBytes();
        byte[] baseSaveBytes = Encoding.UTF8.GetBytes(SampleSaveXml);
        AssertThat(GdbView.TryParse(baseGdbBytes, out GdbView baseView, out _)).IsTrue();
        GdbSaveDocument baseSaveDoc = Engine_GdbSaveMap.ParseDocument(baseSaveBytes, out _);

        // Pre-existing patch where table was moved:
        var existingPatch = new GdbPatchDatabase();
        var tablePos = new GdbPatchEntry(PropTablePosHash, 0x01);
        tablePos.Properties.Add(new GdbPatchProperty(GdbView.HashVecY, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(999f)));
        existingPatch.Entries.Add(tablePos);

        var baselineSpawned = new uint[] { PropTableHash, PropChairHash };

        // Table is now back at vanilla transform (CurrentRegionTransform == BaseRegionTransform):
        var recordSnapshots = new List<LevelSceneRecordSnapshot>
        {
            new()
            {
                RecordHash = PropTableHash,
                ParentHash = PropArchHash,
                EntityName = "MarketTable_01",
                LayerName = "DefaultLayer",
                LayerTag = "id_5A4CBED4",
                LoadState = GdbSaveLoadState.AlwaysOn,
                SourceDb = "level",
                CurrentRegionTransform = Transform3D.Identity,
                BaseRegionTransform = Transform3D.Identity,
                HasBaseTransform = true,
                IsNewPlacement = false,
            },
            new()
            {
                RecordHash = PropChairHash,
                ParentHash = PropArchHash,
                EntityName = "MarketChair_01",
                LayerName = "QuestIntro_Layer",
                LayerTag = "id_11223344",
                LoadState = GdbSaveLoadState.InitiallyOn,
                SourceDb = "level",
                CurrentRegionTransform = Transform3D.Identity,
                BaseRegionTransform = Transform3D.Identity,
                HasBaseTransform = true,
                IsNewPlacement = false,
            },
        };

        LevelPatchCompileResult result = LevelGdbPatchCompiler.CompileSnapshots(
            "levels/albion/bowerstonemarket.gdb",
            "levels/albion/bowerstonemarket.save",
            recordSnapshots,
            null,
            baselineSpawned,
            baseView,
            baseSaveDoc,
            existingLevelPatch: existingPatch);

        // Previous transform override must be purged from patch:
        AssertThat(result.ModifiedTransformCount).IsEqual(0);
        AssertThat(result.LevelGdbPatch.Entries.Any(e => e.Id == PropTablePosHash)).IsFalse();
    }

    [TestCase]
    public void TestLevelGdbPatchCompiler_UnmodifiedFreshLevel_EmitsZeroDiffs()
    {
        // Construct a level GDB containing both a dynamic entity (PropTableHash, in base .save)
        // AND a vanilla static scenery entity (PropVanillaSceneryHash, absent from .save):
        const uint PropVanillaSceneryHash = 0x20000050;
        const uint PropVanillaSceneryTransHash = 0x20000051;
        const uint PropVanillaSceneryPosHash = 0x20000052;

        var db = new GdbPatchDatabase();
        var arch = new GdbPatchEntry(PropArchHash, 0x01);
        db.Entries.Add(arch);

        var tablePos = new GdbPatchEntry(PropTablePosHash, 0x01);
        tablePos.Properties.Add(new GdbPatchProperty(GdbView.HashVecX, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(10f)));
        tablePos.Properties.Add(new GdbPatchProperty(GdbView.HashVecY, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(20f)));
        tablePos.Properties.Add(new GdbPatchProperty(GdbView.HashVecZ, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(5f)));
        db.Entries.Add(tablePos);

        var tableTrans = new GdbPatchEntry(PropTableTransHash, 0x01);
        tableTrans.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, GdbHash.SimpleTransformComponent));
        tableTrans.Properties.Add(new GdbPatchProperty(GdbHash.Position, GdbFieldType.Reference, PropTablePosHash));
        db.Entries.Add(tableTrans);

        var tableRec = new GdbPatchEntry(PropTableHash, 0x01);
        tableRec.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, PropArchHash));
        tableRec.Properties.Add(new GdbPatchProperty(GdbHash.SimpleTransformComponent, GdbFieldType.Reference, PropTableTransHash));
        db.Entries.Add(tableRec);

        var sceneryPos = new GdbPatchEntry(PropVanillaSceneryPosHash, 0x01);
        sceneryPos.Properties.Add(new GdbPatchProperty(GdbView.HashVecX, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(30f)));
        sceneryPos.Properties.Add(new GdbPatchProperty(GdbView.HashVecY, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(40f)));
        sceneryPos.Properties.Add(new GdbPatchProperty(GdbView.HashVecZ, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(12f)));
        db.Entries.Add(sceneryPos);

        var sceneryTrans = new GdbPatchEntry(PropVanillaSceneryTransHash, 0x01);
        sceneryTrans.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, GdbHash.SimpleTransformComponent));
        sceneryTrans.Properties.Add(new GdbPatchProperty(GdbHash.Position, GdbFieldType.Reference, PropVanillaSceneryPosHash));
        db.Entries.Add(sceneryTrans);

        var sceneryRec = new GdbPatchEntry(PropVanillaSceneryHash, 0x01);
        sceneryRec.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, PropArchHash));
        sceneryRec.Properties.Add(new GdbPatchProperty(GdbHash.SimpleTransformComponent, GdbFieldType.Reference, PropVanillaSceneryTransHash));
        db.Entries.Add(sceneryRec);

        byte[] baseGdbBytes = GdbWriter.Serialize(db);
        byte[] baseSaveBytes = Encoding.UTF8.GetBytes(SampleSaveXml);
        AssertThat(GdbView.TryParse(baseGdbBytes, out GdbView baseView, out _)).IsTrue();
        GdbSaveDocument baseSaveDoc = Engine_GdbSaveMap.ParseDocument(baseSaveBytes, out _);

        // Notice: PropVanillaSceneryHash is in baseView (vanilla level GDB) but NOT in baseSaveDoc (.save)
        AssertThat(baseSaveDoc.EntriesByHash.ContainsKey(PropVanillaSceneryHash)).IsFalse();

        var baselineSpawned = new uint[] { PropTableHash, PropVanillaSceneryHash };
        var recordSnapshots = new List<LevelSceneRecordSnapshot>
        {
            new()
            {
                RecordHash = PropTableHash,
                ParentHash = PropArchHash,
                EntityName = "MarketTable_01",
                LayerName = "DefaultLayer",
                LayerTag = "id_5A4CBED4",
                LoadState = GdbSaveLoadState.AlwaysOn,
                SourceDb = "level",
                CurrentRegionTransform = Transform3D.Identity,
                BaseRegionTransform = Transform3D.Identity,
                HasBaseTransform = true,
                IsNewPlacement = false,
            },
            new()
            {
                RecordHash = PropVanillaSceneryHash,
                ParentHash = PropArchHash,
                EntityName = "VanillaFountain_01",
                LayerName = "DefaultLayer",
                LayerTag = "id_5A4CBED4",
                LoadState = GdbSaveLoadState.AlwaysOn,
                SourceDb = "level",
                CurrentRegionTransform = Transform3D.Identity,
                BaseRegionTransform = Transform3D.Identity,
                HasBaseTransform = true,
                IsNewPlacement = false,
            },
        };

        LevelPatchCompileResult result = LevelGdbPatchCompiler.CompileSnapshots(
            "levels/albion/bowerstonemarket.gdb",
            "levels/albion/bowerstonemarket.save",
            recordSnapshots,
            null,
            baselineSpawned,
            baseView,
            baseSaveDoc);

        // Verification: Fresh unmodified level save must produce exactly ZERO diffs and NO collision payload:
        AssertThat(result.HasAnyChanges).IsFalse();
        AssertThat(result.LevelGdbPatch.Entries.Count).IsEqual(0);
        AssertThat(result.GlobalsGdbPatch.Entries.Count).IsEqual(0);
        AssertThat(result.SavePatch.IsEmpty).IsTrue();
        AssertThat(result.NewPlacementCount).IsEqual(0);
        AssertThat(result.ModifiedTransformCount).IsEqual(0);
        AssertThat(result.DeletedRecordCount).IsEqual(0);
        AssertThat(result.LayerMoveCount).IsEqual(0);
        AssertThat(result.LayerStateOverrideCount).IsEqual(0);
    }

    [TestCase]
    public void TestEditorModValueService_GetDeletedEntities_MatchesActualDeletedCount()
    {
        byte[] baseSaveBytes = Encoding.UTF8.GetBytes(SampleSaveXml);
        GdbSaveDocument baseSaveDoc = Engine_GdbSaveMap.ParseDocument(baseSaveBytes, out _);

        var liveHashes = new HashSet<uint> { PropTableHash };
        var baselineSpawned = new uint[] { PropTableHash, PropChairHash };

        // Chair (0x20000010) is missing from scene, but UnspawnedTrigger (0x30000099) and HiddenStall (0x50000001) were never spawned:
        List<GdbSaveEntityEntry> deleted = EditorModValueService.ResolveDeletedEntities(liveHashes, baselineSpawned, baseSaveDoc);

        AssertThat(deleted.Count).IsEqual(1);
        AssertThat(deleted[0].RecordHash).IsEqual(PropChairHash);
        AssertThat(deleted[0].EntityName).IsEqual("MarketChair_01");
    }

    [TestCase]
    public void TestLevelRecordMetadata_AbyssCulledDetection()
    {
        // Pure math check for abyss culling transform values:
        // Position underground (Y or Z <= -9000) or determinant <= 1e-4
        var normalTransform = new Transform3D(Basis.Identity, new Vector3(10f, 5f, 20f));
        var culledYTransform = new Transform3D(Basis.Identity, new Vector3(0f, -9999f, 0f));
        var culledZTransform = new Transform3D(Basis.Identity, new Vector3(0f, 0f, -9999f));
        var scaledZeroTransform = new Transform3D(Basis.FromScale(Vector3.Zero), new Vector3(10f, 5f, 20f));

        AssertThat(LevelRecordMetadata.IsAbyssCulled(normalTransform)).IsFalse();
        AssertThat(LevelRecordMetadata.IsAbyssCulled(culledYTransform)).IsTrue();
        AssertThat(LevelRecordMetadata.IsAbyssCulled(culledZTransform)).IsTrue();
        AssertThat(LevelRecordMetadata.IsAbyssCulled(scaledZeroTransform)).IsTrue();
    }

    [TestCase]
    public void TestResolveDeletedEntities_AccuratelyReportsMissingOrCulledEntities()
    {
        byte[] saveBytes = Encoding.UTF8.GetBytes(SampleSaveXml);
        GdbSaveDocument doc = Engine_GdbSaveMap.ParseDocument(saveBytes, out _);

        // Baseline spawned: table (0x20000001) and chair (0x20000010)
        var baselineSpawned = new HashSet<uint> { PropTableHash, PropChairHash };

        // Live scene: only table is present (chair was deleted/culled)
        var liveHashes = new HashSet<uint> { PropTableHash };

        List<GdbSaveEntityEntry> deleted = EditorModValueService.ResolveDeletedEntities(liveHashes, baselineSpawned, doc);

        AssertThat(deleted.Count).IsEqual(1);
        AssertThat(deleted[0].RecordHash).IsEqual(PropChairHash);
        AssertThat(deleted[0].EntityName).IsEqual("MarketChair_01");
    }

    [TestCase]
    public void TestLevelGdbPatchCompiler_MovedCharacterModel_SavesToLevelPatch_NotGlobalsPatch()
    {
        const uint CharacterHash = 0x30000088u;
        var records = new List<LevelSceneRecordSnapshot>
        {
            new()
            {
                RecordHash = CharacterHash,
                EntityName = "QO135_BardEntity",
                SourceDb = "globals/globals.gdb", // Defined in globals in vanilla
                CurrentRegionTransform = new Transform3D(Basis.Identity, new Vector3(100f, 2f, 50f)),
                BaseRegionTransform = new Transform3D(Basis.Identity, new Vector3(0f, 0f, 0f)),
                HasBaseTransform = true,
                IsNewPlacement = false,
            }
        };

        var baselineSpawned = new HashSet<uint> { CharacterHash };
        var baseDoc = new GdbSaveDocument();

        LevelPatchCompileResult result = LevelGdbPatchCompiler.CompileSnapshots(
            "levels/albion/bowerstonemarket.gdb",
            "levels/albion/bowerstonemarket.save",
            records,
            null,
            baselineSpawned,
            null,
            baseDoc);

        // 1. Level patch MUST contain the moved character transform:
        AssertThat(result.LevelGdbPatch.Entries.Any(e => e.Id == CharacterHash)).IsTrue();

        // 2. Globals patch MUST NOT contain the character transform:
        AssertThat(result.GlobalsGdbPatch.Entries.Any(e => e.Id == CharacterHash)).IsFalse();
    }

    [TestCase]
    public void TestLevelGdbPatchCompiler_ConsecutiveSaves_PreservesMovedObjects()
    {
        // 1. Initial State:
        // Table moved to (10, 0, 20) with vanilla baseline at (0, 0, 0).
        // New barrel placed at (5, 0, 15) with no vanilla baseline.
        var tableRecord = new LevelSceneRecordSnapshot
        {
            RecordHash = PropTableHash,
            EntityName = "MarketTable_01",
            CurrentRegionTransform = new Transform3D(Basis.Identity, new Vector3(10f, 0f, 20f)),
            BaseRegionTransform = new Transform3D(Basis.Identity, new Vector3(0f, 0f, 0f)),
            HasBaseTransform = true,
            IsNewPlacement = false,
        };

        var barrelRecord = new LevelSceneRecordSnapshot
        {
            RecordHash = NewBarrelRecordHash,
            EntityName = "NewBarrel_01",
            CurrentRegionTransform = new Transform3D(Basis.Identity, new Vector3(5f, 0f, 15f)),
            HasBaseTransform = false,
            IsNewPlacement = true,
        };

        var records = new List<LevelSceneRecordSnapshot> { tableRecord, barrelRecord };
        var baselineSpawned = new HashSet<uint> { PropTableHash };
        byte[] saveBytes = Encoding.UTF8.GetBytes(SampleSaveXml);
        GdbSaveDocument doc = Engine_GdbSaveMap.ParseDocument(saveBytes, out _);

        // --- SAVE 1 ---
        LevelPatchCompileResult save1Result = LevelGdbPatchCompiler.CompileSnapshots(
            "levels/albion/market.gdb",
            "levels/albion/market.save",
            records,
            null,
            baselineSpawned,
            null,
            doc);

        AssertThat(save1Result.ModifiedTransformCount).IsEqual(1);
        AssertThat(save1Result.NewPlacementCount).IsEqual(1);
        AssertThat(save1Result.LevelGdbPatch.Entries.Any(e => e.Id == PropTableHash)).IsTrue();
        AssertThat(save1Result.LevelGdbPatch.Entries.Any(e => e.Id == NewBarrelRecordHash)).IsTrue();

        // Simulate subsequent scene snapshot compilation without mutating immutable vanilla baselines:
        // Notice: `tableRecord.BaseRegionTransform` remains (0, 0, 0), `tableRecord.CurrentRegionTransform` remains (10, 0, 20).
        // `barrelRecord.IsNewPlacement` remains true.
        // We pass existing patches into CompileSnapshots as done in SaveToModPackage.

        // --- SAVE 2 (No edits between saves, e.g. Ctrl+S pressed again) ---
        LevelPatchCompileResult save2Result = LevelGdbPatchCompiler.CompileSnapshots(
            "levels/albion/market.gdb",
            "levels/albion/market.save",
            records,
            null,
            baselineSpawned,
            null,
            doc,
            existingLevelPatch: save1Result.LevelGdbPatch,
            existingGlobalsPatch: save1Result.GlobalsGdbPatch);

        // CRITICAL ASSERTION: The second save MUST NOT wipe the patches!
        AssertThat(save2Result.LevelGdbPatch.Entries.Any(e => e.Id == PropTableHash)).IsTrue();
        AssertThat(save2Result.LevelGdbPatch.Entries.Any(e => e.Id == NewBarrelRecordHash)).IsTrue();
        AssertThat(save2Result.HasAnyChanges).IsTrue();

        // --- SAVE 3 (Table moved further to (30, 0, 40)) ---
        var tableRecord3 = new LevelSceneRecordSnapshot
        {
            RecordHash = PropTableHash,
            EntityName = "MarketTable_01",
            CurrentRegionTransform = new Transform3D(Basis.Identity, new Vector3(30f, 0f, 40f)),
            BaseRegionTransform = new Transform3D(Basis.Identity, new Vector3(0f, 0f, 0f)), // Immutable baseline!
            HasBaseTransform = true,
            IsNewPlacement = false,
        };
        var records3 = new List<LevelSceneRecordSnapshot> { tableRecord3, barrelRecord };

        LevelPatchCompileResult save3Result = LevelGdbPatchCompiler.CompileSnapshots(
            "levels/albion/market.gdb",
            "levels/albion/market.save",
            records3,
            null,
            baselineSpawned,
            null,
            doc,
            existingLevelPatch: save2Result.LevelGdbPatch,
            existingGlobalsPatch: save2Result.GlobalsGdbPatch);

        AssertThat(save3Result.LevelGdbPatch.Entries.Any(e => e.Id == PropTableHash)).IsTrue();
    }

    [TestCase]
    public void TestLevelEditing_SaveToModPackage_IncrementalMultiSaveLifecycle()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RefiiTests_SaveCycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string modPath = Path.Combine(tempDir, "TestLevelEditMod.f2mod");

        try
        {
            // Initial Save 1:
            var records1 = new List<LevelSceneRecordSnapshot>
            {
                new()
                {
                    RecordHash = PropTableHash,
                    EntityName = "MarketTable_01",
                    CurrentRegionTransform = new Transform3D(Basis.Identity, new Vector3(15f, 0f, 25f)),
                    BaseRegionTransform = new Transform3D(Basis.Identity, new Vector3(0f, 0f, 0f)),
                    HasBaseTransform = true,
                    IsNewPlacement = false,
                },
                new()
                {
                    RecordHash = NewBarrelRecordHash,
                    EntityName = "NewBarrel_01",
                    CurrentRegionTransform = new Transform3D(Basis.Identity, new Vector3(8f, 0f, 12f)),
                    HasBaseTransform = false,
                    IsNewPlacement = true,
                }
            };

            byte[] saveBytes = Encoding.UTF8.GetBytes(SampleSaveXml);
            GdbSaveDocument doc = Engine_GdbSaveMap.ParseDocument(saveBytes, out _);

            LevelPatchCompileResult result1 = LevelGdbPatchCompiler.CompileSnapshots(
                "levels/albion/market.gdb",
                "levels/albion/market.save",
                records1,
                null,
                new HashSet<uint> { PropTableHash },
                null,
                doc);

            bool ok1 = LevelGdbPatchCompiler.WriteCompileResultToModPackage(modPath, result1, out string err1);
            AssertThat(ok1).IsTrue();
            AssertThat(err1).IsEmpty();

            // Verify .f2mod contains all expected artifacts:
            AssertThat(F2ModPackage.TryOpen(modPath, out F2ModPackage? pkg1, out _)).IsTrue();
            using (pkg1)
            {
                AssertThat(pkg1!.TryGetEntry("levels/albion/market.gdb.tu", out _)).IsTrue();
                AssertThat(pkg1.TryGetEntry("levels/albion/market.save.tu", out _)).IsTrue();
            }

            // Save 2 (Consecutive save without edits - reading existing TU patches from the package):
            // Read back existing patch DBs:
            GdbPatchDatabase? existingLevelPatch = null;
            if (F2ModPackage.TryOpen(modPath, out F2ModPackage? readPkg, out _) && readPkg is not null)
            {
                using (readPkg)
                {
                    if (readPkg.TryGetEntry("levels/albion/market.gdb.tu", out F2ModEntry? tuEntry))
                    {
                        existingLevelPatch = GdbPatchBuilder.LoadOrCreatePatchDatabase(readPkg.ReadEntryBytes(tuEntry!));
                    }
                }
            }

            LevelPatchCompileResult result2 = LevelGdbPatchCompiler.CompileSnapshots(
                "levels/albion/market.gdb",
                "levels/albion/market.save",
                records1,
                null,
                new HashSet<uint> { PropTableHash },
                null,
                doc,
                existingLevelPatch: existingLevelPatch);

            bool ok2 = LevelGdbPatchCompiler.WriteCompileResultToModPackage(modPath, result2, out string err2);
            AssertThat(ok2).IsTrue();
            AssertThat(err2).IsEmpty();

            // CRITICAL TEST: On the 2nd save, market.gdb.tu and market.save.tu MUST still exist!
            AssertThat(F2ModPackage.TryOpen(modPath, out F2ModPackage? pkg2, out _)).IsTrue();
            using (pkg2)
            {
                AssertThat(pkg2!.TryGetEntry("levels/albion/market.gdb.tu", out _)).IsTrue();
                AssertThat(pkg2.TryGetEntry("levels/albion/market.save.tu", out _)).IsTrue();
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [TestCase]
    public void TestLevelGdbPatchCompiler_NewAndMovedProps_EmitsVisualMeshAndTransformWithoutSyntheticPhysics()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_physics_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string modPath = Path.Combine(tempDir, "PhysicsTestMod.f2mod");
            GdbSaveDocument doc = Engine_GdbSaveMap.ParseDocument(Encoding.UTF8.GetBytes(SampleSaveXml), out _);

            const uint newPropHash = 0x40001234;
            const string modelPath = "Art/Environment/BowerstoneMarket/Props/stall_apple_01.mdl";

            var records = new List<LevelSceneRecordSnapshot>
            {
                // 1. Newly placed prop:
                new()
                {
                    RecordHash = newPropHash,
                    EntityName = "AppleStall_Placed",
                    LayerName = "DefaultLayer",
                    LayerTag = "id_5A4CBED4",
                    LoadState = GdbSaveLoadState.AlwaysOn,
                    ModelPath = modelPath,
                    CurrentRegionTransform = new Transform3D(Basis.Identity, new Vector3(10f, 0f, 20f)),
                    IsNewPlacement = true,
                },
                // 2. Moved vanilla prop:
                new()
                {
                    RecordHash = PropTableHash,
                    EntityName = "MarketTable_01",
                    LayerName = "DefaultLayer",
                    LayerTag = "id_5A4CBED4",
                    LoadState = GdbSaveLoadState.AlwaysOn,
                    ModelPath = "Art/Environment/BowerstoneMarket/Props/table_wood_01.mdl",
                    CurrentRegionTransform = new Transform3D(Basis.Identity, new Vector3(50f, 0f, 60f)),
                    BaseRegionTransform = new Transform3D(Basis.Identity, new Vector3(0f, 0f, 0f)),
                    HasBaseTransform = true,
                    IsNewPlacement = false,
                }
            };

            LevelPatchCompileResult result = LevelGdbPatchCompiler.CompileSnapshots(
                "levels/albion/market.gdb",
                "levels/albion/market.save",
                records,
                null,
                new HashSet<uint> { PropTableHash },
                null,
                doc);

            // Verify newly placed prop has StaticMeshComponent with ModelFile:
            uint newMeshCompHash = Engine_GameHash.Fnv1($"0x{newPropHash:X8}_StaticMeshComponent");
            var newMeshEntry = result.LevelGdbPatch.Entries.FirstOrDefault(e => e.Id == newMeshCompHash);
            AssertThat(newMeshEntry).IsNotNull();

            var modelFileProp = newMeshEntry!.Properties.FirstOrDefault(p => p.NameHash == GdbHash.ModelFile);
            AssertThat(modelFileProp).IsNotNull();

            // Verify fake PhysicsSimulationStaticComponent is NOT emitted (native Havok assets are authored by modders when needed):
            uint fakePhysHash = Engine_GameHash.Fnv1($"0x{newPropHash:X8}_PhysicsSimulationStaticComponent");
            AssertThat(result.LevelGdbPatch.Entries.Any(e => e.Id == fakePhysHash)).IsFalse();

            // Verify moved vanilla prop has transform updated in the GDB patch:
            AssertThat(result.LevelGdbPatch.Entries.Any(e => e.Id == PropTableHash)).IsTrue();

            // Write to mod package and verify persistence:
            bool ok = LevelGdbPatchCompiler.WriteCompileResultToModPackage(modPath, result, out string err);
            AssertThat(ok).IsTrue();
            AssertThat(err).IsEmpty();

            AssertThat(F2ModPackage.TryOpen(modPath, out F2ModPackage? pkg, out _)).IsTrue();
            using (pkg)
            {
                AssertThat(pkg!.TryGetEntry("levels/albion/market.gdb.tu", out _)).IsTrue();
                AssertThat(pkg.TryGetEntry("levels/albion/market.save.tu", out _)).IsTrue();
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [TestCase]
    public void TestLevelGdbPatchCompiler_UntouchedFaunaChicken_ProducesZeroOverridesOnFreshSave()
    {
        const uint ChickenHash = 0x19C353E8u; // CreatureFaunaChicken
        var doc = new GdbSaveDocument();
        GdbSaveLayer layer = doc.GetOrCreateLayer("DefaultLayer", GdbSaveLoadState.AlwaysOn);
        layer.Entities.Add(new GdbSaveEntityEntry(
            ChickenHash,
            "CreatureFaunaChicken",
            "id_5A4CBED4",
            "DefaultLayer",
            Engine_GameHash.Fnv1("DefaultLayer"),
            GdbSaveLoadState.AlwaysOn));
        doc.RebuildIndices();

        var chickenTransform = new Transform3D(Basis.Identity, new Vector3(182.98f, 49.87f, 125.02f));
        var records = new List<LevelSceneRecordSnapshot>
        {
            new()
            {
                RecordHash = ChickenHash,
                EntityName = "CreatureFaunaChicken",
                Category = LevelNodeCategory.CreatureOrNpc,
                LayerName = "DefaultLayer",
                LayerTag = "id_5A4CBED4",
                LoadState = GdbSaveLoadState.AlwaysOn,
                CurrentRegionTransform = chickenTransform,
                BaseRegionTransform = chickenTransform,
                HasBaseTransform = true,
                IsNewPlacement = false,
            }
        };

        LevelPatchCompileResult result = LevelGdbPatchCompiler.CompileSnapshots(
            "worlds/albion/bwsslums/defaultscenario/defaultscenario.gdb",
            "worlds/albion/bwsslums/defaultscenario/defaultscenario.save",
            records,
            null,
            new HashSet<uint> { ChickenHash },
            null,
            doc);

        AssertThat(result.HasAnyChanges).IsFalse();
        AssertThat(result.ModifiedTransformCount).IsEqual(0);
        AssertThat(result.NewPlacementCount).IsEqual(0);
        AssertThat(result.DeletedRecordCount).IsEqual(0);
        AssertThat(result.LayerMoveCount).IsEqual(0);
        AssertThat(result.LevelGdbPatch.Entries.Count).IsEqual(0);
        AssertThat(result.SavePatch.Layers.Count).IsEqual(0);
        AssertThat(result.SavePatch.RemovedEntities.Count).IsEqual(0);
    }
}

