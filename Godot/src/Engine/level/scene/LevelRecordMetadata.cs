using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;

namespace Refii.Level;

/// <summary>
/// Categories of level objects in the 3D Editor for filtering and visual gizmo overlays.
/// </summary>
public enum LevelNodeCategory : byte
{
    Prop = 0,
    CreatureOrNpc = 1,
    VillageOrNpcMarker = 2,
    CombatSpawner = 3,
    PatrolOrSpline = 4,
    TriggerOrExit = 5,
    StaticEnvironment = 6,
    AiNavGraph = 7,
}

/// <summary>
/// Centralizes reading and stamping GDB record lineage and <c>&lt;level&gt;.save</c> layer/state
/// metadata on Godot <see cref="Node"/> instances so all level builders, inspectors, and diff
/// compilers share a single contract.
/// </summary>
public static class LevelRecordMetadata
{
    public const string RecordHashMeta = "record_hash";
    public const string ParentHashMeta = "gdb_parent_hash";
    public const string EntityNameMeta = "gdb_entity_name";
    public const string LayerNameMeta = "gdb_layer_name";
    public const string LayerTagMeta = "gdb_layer_tag";
    public const string LayerLoadStateMeta = "gdb_layer_load";
    public const string SourceDbMeta = "gdb_source_db";
    public const string BaseTransformMeta = "gdb_base_transform";
    public const string BaseLayerNameMeta = "gdb_base_layer_name";
    public const string BaseLoadStateMeta = "gdb_base_layer_load";
    public const string IsNewPlacementMeta = "gdb_is_new_placement";
    public const string NodeCategoryMeta = "gdb_node_category";
    public const string LevelGdbPathMeta = "gdb_level_path";
    public const string LevelSavePathMeta = "gdb_save_path";
    public const string LevelNameMeta = "gdb_level_name";
    public const string ScenarioNameMeta = "gdb_scenario_name";
    public const string AvailableScenariosMeta = "gdb_available_scenarios";
    public const string SpawnedRecordHashesMeta = "gdb_spawned_record_hashes";
    public const string IsLayerContainerMeta = "gdb_is_layer_container";
    public const string OwnerRecordHashMeta = "gdb_owner_record_hash";
    public const string SummaryTextMeta = "gdb_summary_text";
    public const string ModelPathMeta = "model_path";

    public const string SourceDbLevel = "level";
    public const string SourceDbGlobals = "globals";
    public const string SourceDbEntity = "entity";

    public static void StampRecordNode(
        Node3D node,
        uint recordHash,
        uint parentHash,
        string entityName,
        string layerName,
        string layerTag,
        GdbSaveLoadState loadState,
        string sourceDb,
        Transform3D baseRegionTransform,
        LevelNodeCategory category,
        bool isNewPlacement = false)
    {
        node.SetMeta(RecordHashMeta, (long)recordHash);
        node.SetMeta(ParentHashMeta, (long)parentHash);
        node.SetMeta(EntityNameMeta, entityName ?? string.Empty);
        node.SetMeta(LayerNameMeta, string.IsNullOrEmpty(layerName) ? "DefaultLayer" : layerName);
        node.SetMeta(LayerTagMeta, string.IsNullOrEmpty(layerTag) ? "id_5A4CBED4" : layerTag);
        node.SetMeta(LayerLoadStateMeta, loadState.ToXmlAttribute());
        node.SetMeta(SourceDbMeta, string.IsNullOrEmpty(sourceDb) ? SourceDbLevel : sourceDb);
        node.SetMeta(BaseTransformMeta, baseRegionTransform);
        node.SetMeta(BaseLayerNameMeta, string.IsNullOrEmpty(layerName) ? "DefaultLayer" : layerName);
        node.SetMeta(BaseLoadStateMeta, loadState.ToXmlAttribute());
        node.SetMeta(IsNewPlacementMeta, isNewPlacement);
        node.SetMeta(NodeCategoryMeta, (int)category);
    }

    public static void StampLayerContainer(
        Node3D layerNode,
        string layerName,
        string layerTag,
        GdbSaveLoadState loadState)
    {
        layerNode.SetMeta(IsLayerContainerMeta, true);
        layerNode.SetMeta(LayerNameMeta, layerName);
        layerNode.SetMeta(LayerTagMeta, layerTag);
        layerNode.SetMeta(LayerLoadStateMeta, loadState.ToXmlAttribute());
        layerNode.SetMeta(BaseLoadStateMeta, loadState.ToXmlAttribute());
    }

    public static bool IsLayerContainer(Node? node) =>
        node is not null &&
        node.HasMeta(IsLayerContainerMeta) &&
        node.GetMeta(IsLayerContainerMeta).AsBool();

    public static uint GetRecordHash(Node? node)
    {
        if (node is null || !node.HasMeta(RecordHashMeta))
        {
            return 0u;
        }

        return unchecked((uint)node.GetMeta(RecordHashMeta).AsInt64());
    }

    public static uint GetParentHash(Node? node)
    {
        if (node is null || !node.HasMeta(ParentHashMeta))
        {
            return 0u;
        }

        return unchecked((uint)node.GetMeta(ParentHashMeta).AsInt64());
    }

    public static string GetEntityName(Node? node) =>
        node is not null && node.HasMeta(EntityNameMeta)
            ? node.GetMeta(EntityNameMeta).AsString()
            : string.Empty;

    public static string GetLayerName(Node? node)
    {
        if (node is null)
        {
            return "DefaultLayer";
        }

        if (node.HasMeta(LayerNameMeta))
        {
            string val = node.GetMeta(LayerNameMeta).AsString();
            if (!string.IsNullOrEmpty(val))
            {
                return val;
            }
        }

        // Walk up to check if parented under a Layer container node:
        Node? current = node.GetParent();
        while (current is not null)
        {
            if (IsLayerContainer(current) && current.HasMeta(LayerNameMeta))
            {
                return current.GetMeta(LayerNameMeta).AsString();
            }

            current = current.GetParent();
        }

        return "DefaultLayer";
    }

    public static GdbSaveLoadState GetLoadState(Node? node)
    {
        if (node is null)
        {
            return GdbSaveLoadState.AlwaysOn;
        }

        if (node.HasMeta(LayerLoadStateMeta))
        {
            return GdbSaveLoadStateExtensions.ParseLoadState(
                node.GetMeta(LayerLoadStateMeta).AsString());
        }

        Node? current = node.GetParent();
        while (current is not null)
        {
            if (IsLayerContainer(current) && current.HasMeta(LayerLoadStateMeta))
            {
                return GdbSaveLoadStateExtensions.ParseLoadState(
                    current.GetMeta(LayerLoadStateMeta).AsString());
            }

            current = current.GetParent();
        }

        return GdbSaveLoadState.AlwaysOn;
    }

    public static bool TryGetBaseTransform(Node? node, out Transform3D baseTransform)
    {
        if (node is not null && node.HasMeta(BaseTransformMeta))
        {
            Variant v = node.GetMeta(BaseTransformMeta);
            if (v.VariantType == Variant.Type.Transform3D)
            {
                baseTransform = v.AsTransform3D();
                return true;
            }
        }

        baseTransform = Transform3D.Identity;
        return false;
    }

    /// <summary>
    /// Walks up from a clicked child mesh (e.g. GMD door/window, companion mesh, or secondary mesh)
    /// to find the owning 1:1 GDB record node.
    /// </summary>
    public static Node3D? FindOwningRecordNode(Node? startNode)
    {
        Node? current = startNode;
        while (current is not null)
        {
            if (current is Node3D n3d && GetRecordHash(n3d) != 0)
            {
                return n3d;
            }

            current = current.GetParent();
        }

        return null;
    }

    /// <summary>
    /// Walks up from any node inside a level scene to find the root <c>LevelScene</c> node
    /// carrying <see cref="LevelGdbPathMeta"/>.
    /// </summary>
    public static Node3D? FindLevelRootNode(Node? startNode)
    {
        Node? current = startNode;
        while (current is not null)
        {
            if (current is Node3D n3d && n3d.HasMeta(LevelGdbPathMeta))
            {
                return n3d;
            }

            current = current.GetParent();
        }

        return null;
    }

    /// <summary>
    /// Computes a node's transform relative to the level root so parenting under <c>Layers/&lt;LayerName&gt;</c>
    /// or user organizational folders never alters its region-space coordinates.
    /// </summary>
    public static Transform3D ComputeRegionTransform(Node3D node, Node3D? levelRoot = null)
    {
        levelRoot ??= FindLevelRootNode(node);
        if (levelRoot is null || ReferenceEquals(node, levelRoot))
        {
            return node.Transform;
        }

        Transform3D accum = node.Transform;
        Node? current = node.GetParent();
        while (current is not null && !ReferenceEquals(current, levelRoot))
        {
            if (current is Node3D parent3D)
            {
                accum = parent3D.Transform * accum;
            }

            current = current.GetParent();
        }

        return accum;
    }

    /// <summary>
    /// Sets a node's local transform so its region-space transform relative to <paramref name="levelRoot"/>
    /// matches <paramref name="desiredRegionTransform"/>.
    /// </summary>
    public static void ApplyRegionTransform(Node3D node, Transform3D desiredRegionTransform, Node3D? levelRoot = null)
    {
        levelRoot ??= FindLevelRootNode(node);
        if (levelRoot is null || ReferenceEquals(node, levelRoot))
        {
            node.Transform = desiredRegionTransform;
            return;
        }

        Node? parent = node.GetParent();
        if (parent is Node3D parent3D && !ReferenceEquals(parent3D, levelRoot))
        {
            Transform3D parentRegion = ComputeRegionTransform(parent3D, levelRoot);
            node.Transform = parentRegion.AffineInverse() * desiredRegionTransform;
        }
        else
        {
            node.Transform = desiredRegionTransform;
        }
    }

    public static bool IsRecordNode(Node? node) =>
        GetRecordHash(node) != 0u;

    public static bool IsRecordNode(Node? node, out uint recordHash)
    {
        recordHash = GetRecordHash(node);
        return recordHash != 0u;
    }

    /// <summary>
    /// Checks whether the given transform represents an abyss-culled (logically deleted) record,
    /// having been placed deep underground (Z or Y &lt;= -9000) or scaled to zero by a .tu deletion override.
    /// </summary>
    public static bool IsAbyssCulled(Transform3D transform)
    {
        if (transform.Origin.Y <= -9000f || transform.Origin.Z <= -9000f)
        {
            return true;
        }
        float det = MathF.Abs(transform.Basis.Determinant());
        if (det <= 1e-4f)
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Checks whether the given node represents an abyss-culled (logically deleted) record,
    /// having been placed deep underground (Z or Y &lt;= -9000) or scaled to zero by a .tu deletion override.
    /// </summary>
    public static bool IsAbyssCulled(Node? node)
    {
        if (node is Node3D n3d)
        {
            return IsAbyssCulled(n3d.Transform);
        }
        return false;
    }

    public static void CollectRecordHashes(Node? root, ISet<uint> into, bool includeCulled = false)
    {
        if (root is null)
        {
            return;
        }

        uint hash = GetRecordHash(root);
        if (hash != 0u && (includeCulled || !IsAbyssCulled(root)))
        {
            into.Add(hash);
        }

        int count = root.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            CollectRecordHashes(root.GetChild(i), into, includeCulled);
        }
    }

    /// <summary>
    /// Searches <paramref name="node"/> and its direct children for a stamped model path metadata.
    /// </summary>
    public static string FindModelPath(Node? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node.HasMeta(ModelPathMeta))
        {
            string p = node.GetMeta(ModelPathMeta).AsString();
            if (!string.IsNullOrEmpty(p))
            {
                return p;
            }
        }

        int count = node.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            Node child = node.GetChild(i);
            if (child.HasMeta(ModelPathMeta))
            {
                string p = child.GetMeta(ModelPathMeta).AsString();
                if (!string.IsNullOrEmpty(p))
                {
                    return p;
                }
            }
        }

        return string.Empty;
    }
}
