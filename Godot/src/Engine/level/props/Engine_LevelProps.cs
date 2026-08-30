using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;
using Refii.Models;

namespace Refii.Level;

public sealed class PropBuildStats
{
    public int SaveEntities { get; set; }

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

    public int EngineLevelBlocks { get; set; }

    public int EngineLevelInstances { get; set; }

    public int EngineLevelUnresolved { get; set; }

    public int EngineLevelType21Blocks { get; set; }

    public int NameMatched { get; set; }

    public int CuratedMatched { get; set; }

    public int RejectedBridge { get; set; }

    public int Companions { get; set; }

    public int Shells { get; set; }

    public int GmdSidecarsLoaded { get; set; }

    public int GmdSidecarsMissing { get; set; }

    public int GmdChildren { get; set; }

    public int GmdChildrenMissing { get; set; }

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
        $"bridge={RejectedBridge} shellpos={ShellBadPosition}) " +
        $"namematched={NameMatched} curated={CuratedMatched} " +
        $"companions={Companions} shells={Shells} " +
        $"gmd(sidecars={GmdSidecarsLoaded}/{GmdSidecarsLoaded + GmdSidecarsMissing} " +
        $"children={GmdChildren} unresolved={GmdChildrenMissing}) (" +
        $"duprecord={DuplicateRecord} duptransform={DuplicateTransform}) " +
        $"engine_level(blocks={EngineLevelBlocks} inst={EngineLevelInstances} " +
        $"unresolved={EngineLevelUnresolved} duptransform={EngineLevelDuplicate} " +
        $"type21={EngineLevelType21Blocks}) " +
        $"foliage(models={FoliageBlocks} inst={FoliageInstances} " +
        $"unresolved={FoliageUnresolved})" +
        (EngineLevelError.Length == 0 ? string.Empty : $" [{EngineLevelError}]");
}

public static class Engine_LevelProps
{
    public const string NodeName = "Props";

    public const float ModelReach = 64.0f;

    public const int SelectableInstanceLimit = 8;

    public static readonly string[] SupplementalGdbPaths =
    {
        "globals/globals.gdb",
        "speechaction/speechaction.gdb",
        "interactivecutscenes/interactivecutscenes.gdb",
        "entity/entity.gdb",
        "environmentthemes/environmentthemes.gdb",
    };

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
        out PropBuildStats stats)
    {
        stats = new PropBuildStats();

        byte[]? gdbBytes = Engine_LevelGdb.TryRead(level, read, out string gdbPath);
        if (gdbBytes is null)
        {
            return null;
        }

        if (!GdbView.TryParse(gdbBytes, out GdbView view, out string why))
        {
            GD.Print($"[props] {level}: level gdb unreadable ({why})");
            return null;
        }

        string savePath = Engine_LevelGdb.SiblingWithExtension(gdbPath, ".save");
        List<SaveEntity> saveEntities =
            Engine_GdbSaveMap.ParseEntities(read(savePath), out SaveLayerStats layers);
        var saveMap = new List<KeyValuePair<uint, string>>(saveEntities.Count);
        foreach (SaveEntity entity in saveEntities)
        {
            saveMap.Add(new KeyValuePair<uint, string>(entity.Record, entity.Name));
        }

        stats.SaveEntities = saveMap.Count;

        // THE LAYER GATE, out loud. Everything downstream is built from this list, so if it is
        // ever wrong the whole region is wrong, and the way that failure looks — a street with
        // holes in it — is indistinguishable by eye from a dozen other defects.
        GD.Print($"[layers] {level}: {layers}");

        List<GdbPlacement> placements = Engine_GdbPlacements.Parse(view, saveMap);
        List<GdbView> supplementalViews = LoadSupplementalViews(read);
        AugmentPlacementModelHashes(level, placements, supplementalViews, stats);
        AppendSupplementalPlacements(supplementalViews, saveMap, placements, stats);
        stats.Placements = placements.Count;

        var blocks = new Dictionary<string, PropBlockBuild>();
        var recordKeys = new HashSet<string>();
        var transformKeys = new HashSet<string>();
        var order = new List<string>();
        var shellCandidates = new List<ShellCandidate>();
        var silverKeys = new List<SilverKeyNode>();
        var fxModelPaths = new Dictionary<uint, string>();

        // Placements whose effect must hang off a LIVE node rather than stand in the region.
        // A silver key bobs and spins; its glow has to go with it.
        var fxAttachTo = new Dictionary<uint, Node3D>();

        AppendEngineLevelBlocks(
            read, Engine_LevelGdb.EngineLevelPath(level, read, gdbPath),
            blocks, order, transformKeys, stats, bakes);

        bool hasExplicitClocktowerBase = false;
        uint clocktowerBaseHash = Engine_PropModelIndex.PathHash(ClocktowerBasePath);
        foreach (GdbPlacement placement in placements)
        {
            if (placement.ModelPathHash == clocktowerBaseHash ||
                placement.ModelPathHashes.Contains(clocktowerBaseHash))
            {
                hasExplicitClocktowerBase = true;
                break;
            }
        }

        PropBuildStats buildStats = stats;
        void AppendCompanionInstance(string keyPrefix, string path, Transform3D transform)
        {
            if (path.Length == 0 ||
                Engine_PropModelIndex.ResolveByPath(path) is not string resolved ||
                IsCharacterModel(resolved) ||
                !transformKeys.Add(TransformKey(transform, resolved)))
            {
                return;
            }

            string companionKey = keyPrefix + "|" + resolved;
            if (!blocks.TryGetValue(companionKey, out PropBlockBuild? companionBlock))
            {
                companionBlock = new PropBlockBuild(resolved, $"prop {NameOf(resolved)}");
                blocks[companionKey] = companionBlock;
                order.Add(companionKey);
            }

            companionBlock.AddInstance(transform);
            buildStats.Instances++;
        }

        foreach (GdbPlacement placement in placements)
        {
            if (placement.SkeletonFileHash != 0 || placement.RetargetSkeletonFileHash != 0)
            {
                stats.Skeletal++;
                continue;
            }

            if (placement.ModelPathHash == 0 && placement.EntityName.Length == 0)
            {
                stats.Unnamed++;
                continue;
            }

            bool RejectSubstitute(string candidate, bool containerGuard)
            {
                if (IsBadMarketSubstitution(placement.EntityName, candidate))
                {
                    return true;
                }

                return containerGuard && inspect is not null &&
                    inspect.Contents.ContainsKey(placement.Hash) &&
                    IsImplausibleContainerShellModel(candidate);
            }

            bool authoredHash = placement.ModelPathHash != 0 && !placement.ModelPathFilled;
            string? modelPath = authoredHash
                ? Engine_PropModelIndex.Resolve(
                    placement.ModelPathHash, Array.Empty<string>())
                : null;

            bool matchedByHash = modelPath is not null;

            if (modelPath is null)
            {
                string entityKey = Engine_PropModelIndex.EntityKey(placement.EntityName);
                string? curated =
                    Engine_GdbModelHashlist.LookupParentHash(placement.ParentHash) ??
                    Engine_GdbModelHashlist.LookupEntityKey(entityKey);

                if (curated is not null)
                {
                    modelPath = Engine_PropModelIndex.ResolveByPath(curated);
                    if (modelPath is not null && RejectSubstitute(modelPath, true))
                    {
                        modelPath = null;
                    }

                    if (modelPath is not null)
                    {
                        stats.CuratedMatched++;
                    }
                }
            }

            if (modelPath is null && placement.ModelPathFilled &&
                placement.ModelPathHash != 0)
            {
                modelPath = Engine_PropModelIndex.Resolve(
                    placement.ModelPathHash, Array.Empty<string>());
                if (modelPath is not null && RejectSubstitute(modelPath, true))
                {
                    modelPath = null;
                }

                if (modelPath is not null)
                {
                    matchedByHash = true;
                }
            }

            // NO NAME-MATCH FALLBACK. An entity that does not state a model does not get one
            // guessed from its name.
            //
            // This resolved a placement's model by tokenising its ENTITY name and scoring
            // candidates against it — so an entity called something like a rug got whichever
            // indexed model scored best for "rug". It reported 162 hits in one region, and
            // every one of them was a model the level had not asked for. `RejectSubstitute`
            // existing at all is the tell: the code already knew it was substituting, and was
            // filtering the worst of its own guesses.
            //
            // If a placement's model cannot be resolved from what the level STATES, it is not
            // drawn and it is counted. See Engine_PropModelIndex.ResolveByPath.

            if (modelPath is null)
            {
                if (placement.ModelPathHash == 0)
                {
                    stats.NoModelHash++;

                    // AND WHETHER THAT WAS THE LEVEL'S ANSWER OR OURS. A placement declaring a
                    // mesh component that yielded no `ModelFile` is a hole in the walk, not a
                    // trigger volume, and it is the only half of this count worth chasing.
                    if (placement.StatesAppearance)
                    {
                        stats.NoModelHashWithAppearance++;
                    }

                    string shellKey = Engine_PropModelIndex.EntityKey(placement.EntityName);
                    if (IsNoHashShellKey(shellKey) && HasWorldishPosition(placement))
                    {
                        shellCandidates.Add(new ShellCandidate(shellKey, placement));
                    }
                }
                else
                {
                    stats.UnresolvedModel++;
                }

                continue;
            }

            {
                string want = OS.GetEnvironment("REFII_PROPS_DUMP");
                if (want.Length != 0 && placement.EntityName is not null &&
                    placement.EntityName.Contains(want, StringComparison.OrdinalIgnoreCase))
                {
                    GD.Print(
                        $"[props-dump] {placement.EntityName} hash={placement.Hash:X8} " +
                        $"modelHash={placement.ModelPathHash:X8} filled={placement.ModelPathFilled} " +
                        $"authored={authoredHash} byHash={matchedByHash} " +
                        $"parent={placement.ParentHash:X8} -> {modelPath}");
                }
            }

            if (!matchedByHash && IsBridgeModel(modelPath))
            {
                stats.RejectedBridge++;
                continue;
            }

            if (IsCharacterModel(modelPath))
            {
                stats.RejectedCharacter++;
                continue;
            }

            // WHICH MODEL THIS PLACEMENT GOT, remembered for the FX pass.
            //
            // An attached effect hangs off a bone of the prop's own model, so Engine_LevelFx has
            // to know which model that is. Resolving it a second time down there would mean a
            // second copy of the ladder above — hash, then curated parent/entity key, then the
            // filled hash, each with its own rejections — and the two copies would drift.
            fxModelPaths[placement.Hash] = modelPath;

            if (Engine_LevelFx.IsSilverKeyPlacement(placement) &&
                !NormalisePath(modelPath).Contains("silverkey", StringComparison.Ordinal))
            {
                continue;
            }

            string? clocktowerPlatformCompanion = null;
            bool clocktowerAudit = placement.ParentHash == ClocktowerParentHash ||
                Engine_PropModelIndex.EntityKey(placement.EntityName)
                    .Contains("clocktower", StringComparison.Ordinal);
            if (clocktowerAudit && !hasExplicitClocktowerBase &&
                NormalisePath(modelPath).Contains(
                    "bs_market_platform/bs_market_platform.mdl", StringComparison.Ordinal) &&
                Engine_PropModelIndex.ResolveByPath(ClocktowerBasePath) is string towerPath)
            {
                clocktowerPlatformCompanion = modelPath;
                modelPath = towerPath;
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
                var keyNode = new SilverKeyNode
                {
                    Transform = transform,
                    BaseTransform = transform,
                };
                keyNode.SetMeta(Engine_PropStreamer.ModelPathMeta, modelPath);

                // NO EFFECT IS BUILT HERE ANY MORE. This used to hang a hardcoded
                // `FX_Key_Silver_01` off the key, which is the effect the DATA states for it
                // anyway — the ordinary attachment walk resolves it, and it simulates. Building it
                // by hand meant the key alone missed the `OffsetFromDummy` its own data states,
                // and it was a second implementation of a resolver we already have.
                //
                // The node is handed to Engine_LevelFx instead, which spawns the effect the data
                // names AS A CHILD of it, so the glow still bobs and spins with the key.
                fxAttachTo[placement.Hash] = keyNode;
                silverKeys.Add(keyNode);
                stats.Instances++;
                continue;
            }

            string key = "prop|" + modelPath;
            if (!blocks.TryGetValue(key, out PropBlockBuild? block))
            {
                block = new PropBlockBuild(modelPath, $"prop {NameOf(modelPath)}");
                blocks[key] = block;
                order.Add(key);
            }

            block.AddInstance(
                transform,
                placement.Hash,
                ((ulong)placement.ModelPathHash << 32) | placement.Hash);
            stats.Instances++;

            bool clocktowerBasePrimary = NormalisePath(modelPath).Contains(
                "bs_market_clocktower/bs_market_clocktower.mdl", StringComparison.Ordinal);
            if (clocktowerBasePrimary)
            {
                if (clocktowerPlatformCompanion is not null)
                {
                    AppendCompanionInstance("clocktower", clocktowerPlatformCompanion, transform);
                }

                foreach (string partPath in ClocktowerPartPaths)
                {
                    AppendCompanionInstance("clocktower", partPath, transform);
                }
            }
        }

        AppendNoHashShells(shellCandidates, blocks, order, transformKeys, stats);
        AppendCompanionBlocks(blocks, order, transformKeys, stats);
        AppendGmdLayoutChildren(blocks, order, transformKeys, stats);

        if (stats.Instances == 0)
        {
            return null;
        }

        ReportVertexBakeFit(bakes, blocks, order);

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

            // The 48-byte ambient blocks cover essentially every placement — they are the
            // guest's per-object ambient, and the fallback for props the probe field has no
            // cell for. Without these keys 694 of one region's props carried no lighting
            // identity at all and rendered as flat global ambient.
            foreach (KeyValuePair<ulong, byte[]> ambient in bakes.Blocks48)
            {
                bakeKeys.Add(ambient.Key);
            }
        }

        var container = new Engine_PropStreamer { Name = NodeName };

        // The streamer reads the .lmp back out of the bank when it needs a bake, so the saved
        // scene carries the path rather than a million baked colours.
        container.SetMeta(Refii.Lighting.Engine_LevelBakedLighting.LevelGdbMeta, gdbPath);

        foreach (SilverKeyNode keyNode in silverKeys)
        {
            keyNode.Name = UniqueName(container, "SilverKey");

            // Same route every other prop takes: the probe sample is STORED on the node and the
            // streamer turns it into shader state once the mesh is standing. A key carries its
            // mesh from the start, so it is lit here too — but through the stored sample, not a
            // second lighting path of its own.
            Refii.Lighting.Engine_LevelBakedLighting.StoreProbeSample(
                keyNode, keyNode.BaseTransform.Origin, probes);
            Refii.Lighting.Engine_LevelBakedLighting.ApplyInstanceLighting(keyNode, lighting);
            container.AddChild(keyNode);
        }

        void AddSingle(PropBlockBuild block, int index)
        {
            var single = new MeshInstance3D
            {
                Name = UniqueName(container, block.Label),
                Transform = block.Instances[index],
            };

            if (block.Foliage)
            {
                single.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            }

            single.SetMeta(Engine_PropStreamer.ModelPathMeta, block.ModelPath);

            uint hash = index < block.InstanceHashes.Count ? block.InstanceHashes[index] : 0;

            // WHICH `.save` RECORD THIS IS, carried onto the node so the running game can tell
            // that the guest is about to place the same thing itself. The editor ignores it.
            if (hash != 0)
            {
                single.SetMeta(Engine_PropStreamer.RecordMeta, (long)hash);
            }

            // An authored per-vertex bake outranks the probe field — the guest's own order is
            // per-vertex D3DCOLOR, then object lightmap, then probes. Only the KEY is stored;
            // the streamer resolves it once the mesh is standing and can check that it fits.
            ulong bakeKey = index < block.InstanceBakeKeys.Count
                ? block.InstanceBakeKeys[index]
                : 0;

            if (bakeKey != 0 && bakeKeys.Contains(bakeKey))
            {
                single.SetMeta(
                    Refii.Lighting.Engine_LevelBakedLighting.BakeKeyMeta, (long)bakeKey);
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

            if (inspect is not null && hash != 0)
            {
                Refii.Entities.Engine_EntityInspect.Annotate(single, hash, inspect);
            }

            container.AddChild(single);
        }

        foreach (string key in order)
        {
            PropBlockBuild block = blocks[key];

            if (block.Instances.Count <= SelectableInstanceLimit)
            {
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
                bool forcedSingle = inspect is not null &&
                    i < block.InstanceHashes.Count &&
                    inspect.HasFor(block.InstanceHashes[i]);
                if (forcedSingle)
                {
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

            if (batched.Count == 0)
            {
                stats.Blocks++;
                continue;
            }

            // UseColors BEFORE InstanceCount — Godot fixes the buffer format when the count is
            // set, and turning colours on afterwards silently does nothing.
            var multiMesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                InstanceCount = batched.Count,
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
            var bounds = new Aabb(block.Instances[batched[0]].Origin, Vector3.Zero);
            for (int i = 0; i < batched.Count; i++)
            {
                bounds = bounds.Expand(block.Instances[batched[i]].Origin);
            }
            multiMesh.CustomAabb = bounds.Grow(ModelReach);

            for (int i = 0; i < batched.Count; i++)
            {
                multiMesh.SetInstanceTransform(i, block.Instances[batched[i]]);
            }

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
            var batchedTransforms = new List<Transform3D>(batched.Count);
            foreach (int index in batched)
            {
                batchedTransforms.Add(block.Instances[index]);
            }

            var batchedBakeKeys = new List<ulong>(batched.Count);
            var batchedBakeColours = new List<uint>(batched.Count);
            foreach (int index in batched)
            {
                batchedBakeKeys.Add(index < block.InstanceBakeKeys.Count
                    ? block.InstanceBakeKeys[index]
                    : 0);
                batchedBakeColours.Add(index < block.InstanceBakeColours.Count
                    ? block.InstanceBakeColours[index]
                    : 0);
            }

            bool coloured = Refii.Lighting.Engine_LevelBakedLighting.ApplyMultiMeshProbes(
                multiMesh, batchedTransforms, probes, bakes, batchedBakeKeys,
                batchedBakeColours);

            var node = new MultiMeshInstance3D
            {
                Name = UniqueName(container, block.Label),
                Multimesh = multiMesh,
            };

            if (block.Foliage)
            {
                node.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            }

            node.SetMeta(Engine_PropStreamer.ModelPathMeta, block.ModelPath);

            // THE BATCHED SUBSET AGAIN, for the same reason the probe colours use it: the
            // inspector pulls placements out of the batch into nodes of their own, so instance
            // N of the MultiMesh is not placement N of the block. A column indexed off the full
            // list would name the wrong record for every instance after the first pulled one,
            // and retiring by it would delete a prop that is still the guest's only copy.
            var records = new long[batched.Count];
            bool anyRecord = false;
            for (int i = 0; i < batched.Count; i++)
            {
                int index = batched[i];
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

            container.AddChild(node);

            stats.Blocks++;
        }

        int levelFx = Engine_LevelFx.Spawn(
            container, view, supplementalViews, placements, fxModelPaths, fxAttachTo, probes);
        if (levelFx != 0)
        {
            GD.Print($"[props] {level}: {levelFx} level FX spawned from bank");
        }

        // THE REGION'S OWN LIGHTS, beside the effects and off the same placements — a torch's
        // flame and the light that flame casts are two statements about one fixture, and until
        // this ran we built the first and not the second.
        int lights = Refii.Lighting.Engine_LevelLights.Spawn(
            container, view, supplementalViews, placements, fxModelPaths);
        if (lights != 0)
        {
            GD.Print($"[props] {level}: {lights} placed lights");
        }

        if (container.GetChildCount() == 0)
        {
            container.QueueFree();
            return null;
        }

        StoreLayerMap(container, saveEntities);

        if (OS.GetThreadCallerId() != OS.GetMainThreadId())
        {
            Engine_PropStreamer.PrefetchModels(Engine_PropStreamer.PendingModelPaths(container));
        }

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
            foreach (Transform3D transform in source.Instances)
            {
                if (transformKeys.Add(TransformKey(transform, resolved)))
                {
                    build.AddInstance(transform);
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

    private static void AppendGmdLayoutChildren(
        Dictionary<string, PropBlockBuild> blocks,
        List<string> order,
        HashSet<string> transformKeys,
        PropBuildStats stats)
    {
        var additions = new Dictionary<string, PropBlockBuild>();
        var additionOrder = new List<string>();
        var cache = new Dictionary<string, List<GmdLayoutChild>>();

        foreach (string key in new List<string>(order))
        {
            PropBlockBuild parent = blocks[key];
            if (!Engine_GmdLayout.IsShellParent(parent.ModelPath))
            {
                continue;
            }

            if (!cache.TryGetValue(parent.ModelPath, out List<GmdLayoutChild>? children))
            {
                byte[]? sidecar = Engine_BnkMeshLoader.ReadBytes(parent.ModelPath + ".gmd");
                children = Engine_GmdLayout.Parse(sidecar);
                cache[parent.ModelPath] = children;

                if (sidecar is null)
                {
                    stats.GmdSidecarsMissing++;
                }
                else
                {
                    stats.GmdSidecarsLoaded++;
                }
            }

            foreach (GmdLayoutChild child in children)
            {
                string? childPath = ResolveGmdChild(child.AssetKey);
                if (childPath is null)
                {
                    stats.GmdChildrenMissing++;
                    continue;
                }

                if (!Engine_GmdLayout.ShouldEmit(child.AssetKey, childPath))
                {
                    continue;
                }

                string childKey = "gmd|" + childPath;
                if (!additions.TryGetValue(childKey, out PropBlockBuild? build))
                {
                    build = new PropBlockBuild(childPath, $"gmd {NameOf(childPath)}");
                    additions[childKey] = build;
                    additionOrder.Add(childKey);
                }

                foreach (Transform3D instance in parent.Instances)
                {
                    Transform3D world = ComposeChild(instance, child.Local);
                    if (transformKeys.Add(TransformKey(world, childPath)))
                    {
                        build.AddInstance(world);
                        stats.GmdChildren++;
                        stats.Instances++;
                    }
                }
            }
        }

        foreach (string key in additionOrder)
        {
            if (additions[key].Instances.Count == 0)
            {
                continue;
            }

            blocks[key] = additions[key];
            order.Add(key);
        }
    }

    public static Transform3D ComposeChild(Transform3D parent, Transform3D childLocal)
    {
        Basis parentGame = FlipZColumn(parent.Basis);
        Basis worldGame = parentGame * childLocal.Basis;
        Vector3 origin = parent.Origin + (parentGame * childLocal.Origin);
        return new Transform3D(FlipZColumn(worldGame), origin);
    }

    private static Basis FlipZColumn(Basis basis) =>
        new(basis.X, basis.Y, -basis.Z);

    private static string? ResolveGmdChild(string assetKey)
    {
        if (assetKey.Length == 0)
        {
            return null;
        }

        string? curated = Engine_GdbModelHashlist.LookupEntityKey(assetKey);
        if (curated is not null &&
            Engine_PropModelIndex.ResolveByPath(curated) is string curatedHit)
        {
            return curatedHit;
        }

        IReadOnlyList<PropModelAsset> exact = Engine_PropModelIndex.ByCompactKey(assetKey);
        if (exact.Count != 0)
        {
            return ChooseGmdChild(exact);
        }

        var fuzzy = new List<PropModelAsset>();
        foreach (KeyValuePair<string, List<PropModelAsset>> entry in
                 Engine_PropModelIndex.CompactKeys())
        {
            if (entry.Key.Length < 5)
            {
                continue;
            }

            if (entry.Key.Contains(assetKey, StringComparison.Ordinal) ||
                assetKey.Contains(entry.Key, StringComparison.Ordinal))
            {
                fuzzy.AddRange(entry.Value);
            }
        }

        return fuzzy.Count == 0 ? null : ChooseGmdChild(fuzzy);
    }

    private static string? ChooseGmdChild(IReadOnlyList<PropModelAsset> candidates)
    {
        string? best = null;
        int bestScore = int.MinValue;

        foreach (PropModelAsset candidate in candidates)
        {
            string path = candidate.VirtualPath.Replace('\\', '/').ToLowerInvariant();
            int score = Engine_PropModelIndex.ArchivePreference(candidate.ArchivePath);

            if (path.Contains("/doors_windows/", StringComparison.Ordinal))
            {
                score += 400;
            }

            if (path.Contains("/props/", StringComparison.Ordinal))
            {
                score += 200;
            }

            if (path.Contains("/buildings/", StringComparison.Ordinal))
            {
                score += 100;
            }

            score -= Math.Min(candidate.VirtualPath.Length, 240);

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate.VirtualPath;
            }
        }

        return best;
    }

    private readonly record struct ShellCandidate(string Key, GdbPlacement Placement);

    public const uint ClocktowerParentHash = 0xD55304DB;

    public const string ClocktowerBasePath =
        "Art\\Environment\\Regions\\Bowerstone\\Structures\\dotXSI\\" +
        "BS_Market_ClockTower\\BS_Market_ClockTower.mdl";

    public static readonly string[] ClocktowerPartPaths =
    {
        "Art\\Environment\\Regions\\Bowerstone\\Structures\\dotXSI\\" +
        "BS_Market_ClockTower_Cogs\\BS_Market_ClockTower_Cogs.mdl",
        "Art\\Environment\\Regions\\Bowerstone\\Structures\\dotXSI\\" +
        "BS_Market_ClockTower_HourHand\\BS_Market_ClockTower_HourHand.mdl",
        "Art\\Environment\\Regions\\Bowerstone\\Structures\\dotXSI\\" +
        "BS_Market_ClockTower_MinuteHand\\BS_Market_ClockTower_MinuteHand.mdl",
    };

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
               p.Contains("bs_market_gatehouse", StringComparison.Ordinal) ||
               p.Contains("bs_market_clocktower", StringComparison.Ordinal) ||
               p.Contains("bs_market_platform", StringComparison.Ordinal) ||
               p.Contains("bs_market_largeshop", StringComparison.Ordinal) ||
               p.Contains("bs_market_smallshop", StringComparison.Ordinal) ||
               p.Contains("bs_market_generalshop", StringComparison.Ordinal) ||
               p.Contains("bs_market_tavern", StringComparison.Ordinal) ||
               p.Contains("bs_market_tarotstall", StringComparison.Ordinal) ||
               p.Contains("bs_townhouse", StringComparison.Ordinal);
    }

    public const string TavernExterior =
        "art/environment/regions/bowerstone/buildings/dotxsi/" +
        "bs_market_tavern/bs_market_tavern/exterior.mdl";

    public const string GeneralShopExterior =
        "art/environment/regions/bowerstone/buildings/dotxsi/" +
        "bs_market_generalshop/bs_market_generalshop/exterior.mdl";

    public static bool IsNoHashShellKey(string key) =>
        key is "bsmarkettavern" or "generalstore" or "generalstore1";

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

    private static void AppendNoHashShells(
        List<ShellCandidate> candidates,
        Dictionary<string, PropBlockBuild> blocks,
        List<string> order,
        HashSet<string> transformKeys,
        PropBuildStats stats)
    {
        foreach (ShellCandidate candidate in candidates)
        {
            string exterior = candidate.Key == "bsmarkettavern"
                ? TavernExterior
                : GeneralShopExterior;

            GdbPlacement placement = WithBorrowedRotation(candidate, candidates);
            Transform3D transform = BuildTransform(placement);

            foreach (string path in new[] { exterior, CompanionPath(exterior) })
            {
                if (path.Length == 0 ||
                    Engine_PropModelIndex.ResolveByPath(path) is not string resolved)
                {
                    continue;
                }

                if (!transformKeys.Add(TransformKey(transform, resolved)))
                {
                    continue;
                }

                string key = "shell|" + resolved;
                if (!blocks.TryGetValue(key, out PropBlockBuild? build))
                {
                    build = new PropBlockBuild(resolved, $"prop {NameOf(resolved)}");
                    blocks[key] = build;
                    order.Add(key);
                }

                build.AddInstance(transform);
                stats.Shells++;
                stats.Instances++;
            }
        }
    }

    private static GdbPlacement WithBorrowedRotation(
        ShellCandidate candidate, List<ShellCandidate> all)
    {
        GdbPlacement placement = candidate.Placement;
        if (placement.HasRotation)
        {
            return placement;
        }

        float bestDistance = 64f;
        GdbPlacement? best = null;

        foreach (ShellCandidate other in all)
        {
            if (other.Key != candidate.Key || !other.Placement.HasRotation)
            {
                continue;
            }

            float dx = other.Placement.X - placement.X;
            float dy = other.Placement.Y - placement.Y;
            float distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = other.Placement;
            }
        }

        if (best is null)
        {
            return placement;
        }

        GdbPlacement copy = placement.Clone();
        copy.RotX = best.RotX;
        copy.RotY = best.RotY;
        copy.RotZ = best.RotZ;
        copy.Yaw = best.Yaw;
        copy.HasRotation = true;
        return copy;
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
                                mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex]
                                    .AsVector3Array().Length);
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

        public List<Transform3D> Instances { get; } = new();

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
            Transform3D transform, uint hash = 0, ulong bakeKey = 0, uint bakeColour = 0)
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
        }
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
                modelPath, $"engine_level {NameOf(modelPath)}");

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
                build.AddInstance(transform, bakeKey: instance.Hash);
                emitted++;
            }

            if (emitted == 0)
            {
                continue;
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
                build = new PropBlockBuild(modelPath, $"foliage {NameOf(modelPath)}")
                {
                    Foliage = true,
                };
                merged[modelPath] = build;
                mergedOrder.Add(modelPath);
            }

            // THE BLOCK'S U32 ARRAY IS ITS BAKE — one D3DCOLOR per loop1 instance, keyed by
            // the block's own hash. All 158 of chapter2posh's type-21 blocks join its 158
            // arrays with the array length equal to the loop1 count exactly. Instances past
            // the array (the loop2 tail) carry no authored colour and keep the probe sample.
            uint[]? authored = null;
            bakes?.U32Arrays.TryGetValue(block.Hash, out authored);

            for (int i = 0; i < block.Instances.Count; i++)
            {
                EngineLevelInstance instance = block.Instances[i];
                Transform3D transform =
                    BuildFoliageTransform(instance.Values, instance.Rotation);
                build.AddInstance(transform, bakeColour:
                    authored is not null && i < authored.Length ? authored[i] : 0);
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
        var views = new List<GdbView>();
        foreach (string path in SupplementalGdbPaths)
        {
            byte[]? bytes = read(path) ?? read("data/" + path);
            if (bytes is not null && GdbView.TryParse(bytes, out GdbView view, out _))
            {
                views.Add(view);
            }
        }

        return views;
    }

    public static void AugmentPlacementModelHashes(
        LevelEntry level,
        List<GdbPlacement> placements,
        List<GdbView> views,
        PropBuildStats stats)
    {
        if (views.Count == 0 || placements.Count == 0)
        {
            return;
        }

        bool isSlumsLevel = level.LevelPath.Contains(
            "bwsslums", StringComparison.OrdinalIgnoreCase);

        foreach (GdbPlacement p in placements)
        {
            string entityKey = Engine_PropModelIndex.EntityKey(p.EntityName);
            bool allowSlumsShellAugment = isSlumsLevel && p.ModelPathHash != 0 &&
                (entityKey.Contains("slumstreethouse", StringComparison.Ordinal) ||
                 entityKey.Contains("townhouse", StringComparison.Ordinal));

            if (p.Hash == 0 || (p.ModelPathHash != 0 && !allowSlumsShellAugment))
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

            if (p.ModelPathHash != 0)
            {
                if (p.ModelPathHashes.Count == 0)
                {
                    p.ModelPathHashes.Add(p.ModelPathHash);
                }

                foreach (uint hash in found)
                {
                    if (!p.ModelPathHashes.Contains(hash))
                    {
                        p.ModelPathHashes.Add(hash);
                    }
                }
            }
            else
            {
                p.ModelPathHashes = found;
                p.ModelPathHash = found[0];
                p.ModelPathFilled = true;
            }

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
        PropBuildStats stats)
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

    public static Transform3D BuildTransform(GdbPlacement placement)
    {
        float scale = float.IsFinite(placement.Scale) &&
                      placement.Scale > 0.01f && placement.Scale < 100f
            ? placement.Scale
            : 1f;

        Basis basis = placement.HasRotation
            ? RotationBasis(placement.RotX, placement.RotY, placement.RotZ, scale)
            : YawBasis(placement.Yaw, scale);

        return new Transform3D(basis, new Vector3(placement.X, placement.Z, placement.Y));
    }

    public static Basis RotationBasis(float rx, float ry, float rz, float scale)
    {
        if (!float.IsFinite(rx))
        {
            rx = 0f;
        }

        if (!float.IsFinite(ry))
        {
            ry = 0f;
        }

        if (!float.IsFinite(rz))
        {
            rz = 0f;
        }

        float sx = MathF.Sin(rx);
        float cx = MathF.Cos(rx);
        float sy = MathF.Sin(ry);
        float cy = MathF.Cos(ry);
        float sz = MathF.Sin(rz);
        float cz = MathF.Cos(rz);

        var game = new float[9];
        game[0] = cy * cx;
        game[1] = sx;
        game[2] = -sy * cx;
        game[3] = (sy * sz) - (cy * cz * sx);
        game[4] = cz * cx;
        game[5] = (sy * cz * sx) + (cy * sz);
        game[6] = (cy * sz * sx) + (sy * cz);
        game[7] = -sz * cx;
        game[8] = (cy * cz) - (sy * sz * sx);

        var map = new[] { 0, 2, 1 };
        var m = new float[9];
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                m[(row * 3) + col] = game[(map[row] * 3) + map[col]] * scale;
            }
        }

        return ToGodotBasis(m);
    }

    public static Basis YawBasis(float yaw, float scale)
    {
        float s = MathF.Sin(yaw);
        float c = MathF.Cos(yaw);

        if (!float.IsFinite(s) || !float.IsFinite(c))
        {
            s = 0f;
            c = 1f;
        }

        return YawScaleBasis(s, c, scale, scale, scale);
    }

    public static Basis YawScaleBasis(float s, float c, float sx, float sy, float sz)
    {
        if (!float.IsFinite(s) || !float.IsFinite(c))
        {
            s = 0f;
            c = 1f;
        }

        return ToGodotBasis(new[]
        {
            c * sx, 0f, s * sy,
            0f, sz, 0f,
            -s * sx, 0f, c * sy,
        });
    }

    private static Basis ToGodotBasis(float[] m) => new(
        new Vector3(m[0], m[3], m[6]),
        new Vector3(m[1], m[4], m[7]),
        new Vector3(-m[2], -m[5], -m[8]));

    public static bool IsBridgeModel(string modelPath) =>
        modelPath.Replace('\\', '/').ToLowerInvariant()
            .Contains("bridge", StringComparison.Ordinal);

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
               path.Contains("/characters/heros/", StringComparison.Ordinal);
    }

    private static string CompactText(string value)
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

    private static bool KeyIsOrNumbered(string key, string baseKey)
    {
        if (key == baseKey)
        {
            return true;
        }

        if (key.Length <= baseKey.Length ||
            !key.StartsWith(baseKey, StringComparison.Ordinal))
        {
            return false;
        }

        for (int i = baseKey.Length; i < key.Length; i++)
        {
            if (!char.IsDigit(key[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool KeyIsOrVariant(string key, string baseKey)
    {
        if (KeyIsOrNumbered(key, baseKey))
        {
            return true;
        }

        if (key.Length <= baseKey.Length + 1 ||
            !key.StartsWith(baseKey, StringComparison.Ordinal) ||
            key[baseKey.Length] != 'v')
        {
            return false;
        }

        for (int i = baseKey.Length + 1; i < key.Length; i++)
        {
            if (!char.IsDigit(key[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsImplausibleContainerShellModel(string modelPath)
    {
        string key = CompactText(modelPath.Replace('\\', '/'));
        return key.Contains("bridge", StringComparison.Ordinal) ||
               key.Contains("facade", StringComparison.Ordinal) ||
               key.Contains("clocktower", StringComparison.Ordinal) ||
               modelPath.Replace('\\', '/').ToLowerInvariant()
                   .Contains("/buildings/", StringComparison.Ordinal) ||
               key.EndsWith("exteriormdl", StringComparison.Ordinal) ||
               key.EndsWith("interiormdl", StringComparison.Ordinal);
    }

    public static bool IsBadMarketSubstitution(string entityName, string modelPath)
    {
        string entityKey = Engine_PropModelIndex.EntityKey(entityName);
        string rawKey = CompactText(entityName);
        string modelKey = CompactText(NameOf(modelPath));
        string modelPathKey = CompactText(modelPath.Replace('\\', '/'));

        bool signEntity =
            entityKey.Contains("sign", StringComparison.Ordinal) ||
            rawKey.Contains("sign", StringComparison.Ordinal);
        bool doorEntity =
            entityKey.Contains("door", StringComparison.Ordinal) ||
            rawKey.Contains("door", StringComparison.Ordinal);

        bool generalStoreBuilding =
            KeyIsOrNumbered(entityKey, "generalstore") ||
            KeyIsOrNumbered(rawKey, "objectbuildinggeneralstore") ||
            KeyIsOrNumbered(rawKey, "newobjectbuildinggeneralstore");
        if (modelPathKey.Contains("bsmarketgeneralshop", StringComparison.Ordinal) &&
            (modelPathKey.Contains("exterior", StringComparison.Ordinal) ||
             modelPathKey.Contains("interior", StringComparison.Ordinal)) &&
            !generalStoreBuilding)
        {
            return true;
        }

        if (generalStoreBuilding && !signEntity &&
            modelKey.Contains("signgeneralstore", StringComparison.Ordinal))
        {
            return true;
        }

        bool tavernBuilding =
            entityKey.Contains("bsmarkettavern", StringComparison.Ordinal) ||
            entityKey.Contains("markettavern", StringComparison.Ordinal) ||
            rawKey.Contains("objectbuildingbsmarkettavern", StringComparison.Ordinal) ||
            rawKey.Contains("newobjectbuildingbsmarkettavern", StringComparison.Ordinal);
        if (modelPathKey.Contains("bsmarkettavern", StringComparison.Ordinal) &&
            !tavernBuilding)
        {
            return true;
        }

        if (tavernBuilding && !signEntity &&
            (modelKey.Contains("botavernsign", StringComparison.Ordinal) ||
             modelKey.Contains("tavernsign", StringComparison.Ordinal)))
        {
            return true;
        }

        bool tarotStallBuilding =
            entityKey.Contains("tarotstall", StringComparison.Ordinal) ||
            rawKey.Contains("tarotstall", StringComparison.Ordinal);
        if (tarotStallBuilding && !doorEntity &&
            modelKey.Contains("tarotstalldoors", StringComparison.Ordinal))
        {
            return true;
        }

        bool marketStallBuilding =
            KeyIsOrVariant(entityKey, "bsopenstall") ||
            KeyIsOrVariant(entityKey, "bsmarketopenstall") ||
            KeyIsOrVariant(entityKey, "bsmarketstall") ||
            rawKey.Contains("objectbuildingbsopenstall", StringComparison.Ordinal) ||
            rawKey.Contains("objectbuildingbsmarketstall", StringComparison.Ordinal) ||
            rawKey.Contains("newobjectbuildingbsmarketstall", StringComparison.Ordinal);
        bool marketStallShellModel =
            (modelKey.Contains("bsmarketmarketstall", StringComparison.Ordinal) ||
             modelKey.Contains("bsmarketopenstall", StringComparison.Ordinal) ||
             modelKey.Contains("bsopenstall", StringComparison.Ordinal)) &&
            !modelKey.Contains("esashopmarketstall", StringComparison.Ordinal);
        if (marketStallShellModel && !marketStallBuilding)
        {
            return true;
        }

        if ((generalStoreBuilding || tavernBuilding ||
             marketStallBuilding || tarotStallBuilding) &&
            (modelKey.Contains("bstownhouse", StringComparison.Ordinal) ||
             modelKey.Contains("townhouse", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
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
