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

    public override string ToString() =>
        $"save={SaveEntities} placements={Placements} instances={Instances} " +
        $"blocks={Blocks} supplemental={SupplementalHits} " +
        $"modelfills={SupplementalModelFills} " +
        $"skipped(skeletal={Skeletal} unnamed={Unnamed} nomodel={NoModelHash} " +
        $"unresolved={UnresolvedModel} character={RejectedCharacter} " +
        $"bridge={RejectedBridge} shellpos={ShellBadPosition}) " +
        $"namematched={NameMatched} curated={CuratedMatched} " +
        $"companions={Companions} shells={Shells} " +
        $"gmd(sidecars={GmdSidecarsLoaded}/{GmdSidecarsLoaded + GmdSidecarsMissing} " +
        $"children={GmdChildren} unresolved={GmdChildrenMissing}) (" +
        $"duprecord={DuplicateRecord} duptransform={DuplicateTransform}) " +
        $"engine_level(blocks={EngineLevelBlocks} inst={EngineLevelInstances} " +
        $"unresolved={EngineLevelUnresolved} type21={EngineLevelType21Blocks}) " +
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
        Build(level, read, null, null, out stats);

    public static Node3D? Build(
        LevelEntry level,
        Func<string, byte[]?> read,
        Refii.Lighting.LightProbeField? probes,
        Refii.Lighting.LightingFrame? lighting,
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
        List<KeyValuePair<uint, string>> saveMap = Engine_GdbSaveMap.Parse(read(savePath));
        stats.SaveEntities = saveMap.Count;

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

        AppendEngineLevelBlocks(
            read, Engine_LevelGdb.SiblingWithExtension(gdbPath, ".engine_level"),
            blocks, order, transformKeys, stats);

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

            companionBlock.Instances.Add(transform);
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

            string? modelPath = placement.ModelPathHash == 0
                ? null
                : Engine_PropModelIndex.Resolve(
                    placement.ModelPathHash, Array.Empty<string>());

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
                    if (modelPath is not null)
                    {
                        stats.CuratedMatched++;
                    }
                }
            }

            if (modelPath is null && placement.EntityName.Length != 0)
            {
                modelPath = Engine_PropModelIndex.ResolveByEntityName(placement.EntityName)
                    ?? Engine_ModelScoring.ResolveByEntityScore(placement.EntityName);

                if (modelPath is not null)
                {
                    stats.NameMatched++;
                }
            }

            if (modelPath is null)
            {
                if (placement.ModelPathHash == 0)
                {
                    stats.NoModelHash++;

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

            string key = "prop|" + modelPath;
            if (!blocks.TryGetValue(key, out PropBlockBuild? block))
            {
                block = new PropBlockBuild(modelPath, $"prop {NameOf(modelPath)}");
                blocks[key] = block;
                order.Add(key);
            }

            block.Instances.Add(transform);
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

        var container = new Engine_PropStreamer { Name = NodeName };

        foreach (string key in order)
        {
            PropBlockBuild block = blocks[key];

            if (block.Instances.Count <= SelectableInstanceLimit)
            {
                for (int i = 0; i < block.Instances.Count; i++)
                {
                    var single = new MeshInstance3D
                    {
                        Name = UniqueName(container, block.Label),
                        Transform = block.Instances[i],
                    };

                    single.SetMeta(Engine_PropStreamer.ModelPathMeta, block.ModelPath);
                    Refii.Lighting.Engine_LevelBakedLighting.ApplyInstanceLighting(
                        single, block.Instances[i].Origin, probes, lighting);
                    container.AddChild(single);
                }

                stats.Blocks++;
                continue;
            }

            var multiMesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                InstanceCount = block.Instances.Count,
            };

            var bounds = new Aabb(block.Instances[0].Origin, Vector3.Zero);
            for (int i = 0; i < block.Instances.Count; i++)
            {
                multiMesh.SetInstanceTransform(i, block.Instances[i]);
                bounds = bounds.Expand(block.Instances[i].Origin);
            }

            multiMesh.CustomAabb = bounds.Grow(ModelReach);

            var node = new MultiMeshInstance3D
            {
                Name = UniqueName(container, block.Label),
                Multimesh = multiMesh,
            };

            node.SetMeta(Engine_PropStreamer.ModelPathMeta, block.ModelPath);
            Refii.Lighting.Engine_LevelBakedLighting.ApplyInstanceLighting(
                node, bounds.GetCenter(), probes, lighting);
            container.AddChild(node);

            stats.Blocks++;
        }

        if (container.GetChildCount() == 0)
        {
            container.QueueFree();
            return null;
        }

        return container;
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
                    build.Instances.Add(transform);
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
                        build.Instances.Add(world);
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

                build.Instances.Add(transform);
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

    private sealed class PropBlockBuild
    {
        public PropBlockBuild(string modelPath, string label)
        {
            ModelPath = modelPath;
            Label = label;
        }

        public string ModelPath { get; }

        public string Label { get; }

        public List<Transform3D> Instances { get; } = new();
    }

    private static void AppendEngineLevelBlocks(
        Func<string, byte[]?> read,
        string enginePath,
        Dictionary<string, PropBlockBuild> blocks,
        List<string> order,
        HashSet<string> transformKeys,
        PropBuildStats stats)
    {
        byte[]? bytes = read(enginePath);
        if (bytes is null)
        {
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
                Transform3D transform = BuildEngineLevelTransform(instance.Values);
                if (!transformKeys.Add(TransformKey(transform, modelPath)))
                {
                    continue;
                }

                build.Instances.Add(transform);
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

        AppendFoliageBlocks(file, blocks, order, transformKeys, stats);
    }

    private static void AppendFoliageBlocks(
        EngineLevelFile file,
        Dictionary<string, PropBlockBuild> blocks,
        List<string> order,
        HashSet<string> transformKeys,
        PropBuildStats stats)
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
                build = new PropBlockBuild(modelPath, $"foliage {NameOf(modelPath)}");
                merged[modelPath] = build;
                mergedOrder.Add(modelPath);
            }

            foreach (EngineLevelInstance instance in block.Instances)
            {
                Transform3D transform = BuildEngineLevelTransform(instance.Values);
                build.Instances.Add(transform);
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
    }

    public static Transform3D BuildEngineLevelTransform(float[] values)
    {
        float sx = values[9] == 0f ? 1f : values[9];
        float sy = values[10] == 0f ? sx : values[10];
        float sz = values[11] == 0f ? sx : values[11];

        return new Transform3D(
            YawScaleBasis(values[6], values[7], sx, sy, sz),
            new Vector3(values[0], values[2], values[1]));
    }

    private static List<GdbView> LoadSupplementalViews(Func<string, byte[]?> read)
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

    private static void AugmentPlacementModelHashes(
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
