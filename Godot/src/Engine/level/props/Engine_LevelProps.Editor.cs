using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;

namespace Refii.Level;

public static partial class Engine_LevelProps
{
    public const string LayersContainerName = "Layers";
    public const string StaticEnvironmentContainerName = "StaticEnvironment";
    public const string UnassignedLayerName = "Unassigned";

    /// <summary>
    /// Builds the 1:1 unbatched GDB/Save hierarchy in the Godot Editor (<c>Engine.IsEditorHint()</c>)
    /// while keeping pre-baked <c>.engine_level</c> and <c>.hdb</c> foliage batches isolated under
    /// <see cref="StaticEnvironmentContainerName"/>.
    /// </summary>
    public sealed class EditorPropHierarchy
    {
        private readonly GdbSaveDocument _saveDoc;
        private readonly string _levelGdbPath;
        private readonly Dictionary<uint, GdbPlacement> _placementsByHash = new();
        private readonly HashSet<uint> _supplementalHashes = new();
        private readonly Dictionary<string, Node3D> _layerContainers = new(StringComparer.Ordinal);
        private readonly Dictionary<uint, Node3D> _primaryRecordNodes = new();
        private readonly Dictionary<string, int> _nameCounters = new(StringComparer.Ordinal);
        private Node3D? _layersRoot;
        private Node3D? _staticEnvRoot;

        public string GetUniqueName(string baseName)
        {
            if (_nameCounters.TryGetValue(baseName, out int count))
            {
                count++;
                _nameCounters[baseName] = count;
                return $"{baseName} ({count})";
            }

            _nameCounters[baseName] = 1;
            return baseName;
        }

        public string ResolveTarget(
            uint hash,
            string fallbackLabel,
            out string entityName,
            out string layerTag,
            out GdbSaveLoadState loadState)
        {
            if (hash == 0)
            {
                entityName = fallbackLabel;
                layerTag = string.Empty;
                loadState = GdbSaveLoadState.AlwaysOn;
                return StaticEnvironmentContainerName;
            }

            _placementsByHash.TryGetValue(hash, out GdbPlacement? placement);
            bool inSave = _saveDoc.TryGetEntity(hash, out GdbSaveEntityEntry saveEntry, out GdbSaveLayer? saveLayer);

            entityName = !string.IsNullOrWhiteSpace(placement?.EntityName)
                ? placement!.EntityName
                : inSave && !string.IsNullOrWhiteSpace(saveEntry.EntityName)
                    ? saveEntry.EntityName
                    : fallbackLabel;

            string layerName = inSave && saveLayer is not null ? saveLayer.LayerName : UnassignedLayerName;
            layerTag = inSave && saveLayer is not null ? saveLayer.LayerTag : "id_5A4CBED4";
            loadState = inSave && saveLayer is not null
                ? saveLayer.LoadState
                : GdbSaveLoadState.AlwaysOn;

            return layerName;
        }

        public EditorPropHierarchy(GdbSaveDocument saveDoc, string levelGdbPath)
        {
            _saveDoc = saveDoc;
            _levelGdbPath = levelGdbPath;
        }

        public GdbSaveDocument SaveDocument => _saveDoc;

        public IReadOnlyDictionary<uint, Node3D> PrimaryRecordNodes => _primaryRecordNodes;

        public void IndexPlacements(IEnumerable<GdbPlacement> placements)
        {
            foreach (GdbPlacement p in placements)
            {
                if (p.Hash != 0 && !_placementsByHash.ContainsKey(p.Hash))
                {
                    _placementsByHash[p.Hash] = p;
                }
            }
        }

        public void MarkSupplemental(uint recordHash)
        {
            if (recordHash != 0)
            {
                _supplementalHashes.Add(recordHash);
            }
        }

        public Node3D GetLayersRoot(Node3D container)
        {
            if (_layersRoot is not null && GodotObject.IsInstanceValid(_layersRoot))
            {
                return _layersRoot;
            }

            _layersRoot = new Node3D { Name = LayersContainerName };
            container.AddChild(_layersRoot);

            foreach (GdbSaveLayer layer in _saveDoc.Layers)
            {
                GetOrCreateLayerContainer(container, layer.LayerName, layer.LayerTag, layer.LoadState);
            }

            return _layersRoot;
        }

        public Node3D GetStaticEnvironmentRoot(Node3D container)
        {
            if (_staticEnvRoot is not null && GodotObject.IsInstanceValid(_staticEnvRoot))
            {
                return _staticEnvRoot;
            }

            _staticEnvRoot = new Node3D { Name = StaticEnvironmentContainerName };
            container.AddChild(_staticEnvRoot);
            return _staticEnvRoot;
        }

        public Node3D GetOrCreateLayerContainer(
            Node3D container,
            string layerName,
            string layerTag,
            GdbSaveLoadState loadState)
        {
            string effectiveName = string.IsNullOrWhiteSpace(layerName) ? UnassignedLayerName : layerName;
            if (_layerContainers.TryGetValue(effectiveName, out Node3D? existing) &&
                GodotObject.IsInstanceValid(existing))
            {
                return existing;
            }

            Node3D root = GetLayersRoot(container);
            var layerNode = new Node3D
            {
                Name = GetUniqueName(effectiveName),
            };
            LevelRecordMetadata.StampLayerContainer(layerNode, effectiveName, layerTag, loadState);
            root.AddChild(layerNode);
            _layerContainers[effectiveName] = layerNode;
            return layerNode;
        }

        public void AttachSilverKey(Node3D container, SilverKeyNode keyNode)
        {
            uint hash = keyNode.HasMeta(Engine_PropStreamer.RecordMeta)
                ? (uint)keyNode.GetMeta(Engine_PropStreamer.RecordMeta).AsInt64()
                : 0;
            if (hash == 0)
            {
                Node3D staticRoot = GetStaticEnvironmentRoot(container);
                keyNode.Name = GetUniqueName("SilverKey");
                staticRoot.AddChild(keyNode);
                return;
            }

            AttachPrimaryRecordNode(container, keyNode, hash, "SilverKey");
        }

        public void AttachSingleProp(
            Node3D container,
            MeshInstance3D single,
            string blockLabel,
            PropRole role,
            uint hash)
        {
            if (hash == 0)
            {
                Node3D staticRoot = GetStaticEnvironmentRoot(container);
                single.Name = GetUniqueName(role == PropRole.Main ? blockLabel : $"{blockLabel} {role}");
                staticRoot.AddChild(single);
                return;
            }

            if (!_primaryRecordNodes.TryGetValue(hash, out Node3D? primaryNode) ||
                !GodotObject.IsInstanceValid(primaryNode))
            {
                AttachPrimaryRecordNode(container, single, hash, blockLabel);
                return;
            }

            // Secondary mesh for the same GDB record (e.g., 2nd/3rd mesh of StaticMultipleMeshComponent,
            // companion interior/exterior mesh, or TreeMesh LOD/Shadow).
            // Parent under the primary record node with a relative local transform so moving the
            // primary node in the 3D Editor moves all of its parts as one unit without false diffs.
            single.Transform = primaryNode.Transform.AffineInverse() * single.Transform;
            if (single.HasMeta(Engine_PropStreamer.RecordMeta))
            {
                single.RemoveMeta(Engine_PropStreamer.RecordMeta);
            }

            string partLabel = role == PropRole.Main ? blockLabel : $"{blockLabel} {role}";
            single.Name = GetUniqueName(partLabel);
            primaryNode.AddChild(single);
        }

        public void AttachBatchedNode(Node3D container, MultiMeshInstance3D node, string label)
        {
            Node3D staticRoot = GetStaticEnvironmentRoot(container);
            node.Name = GetUniqueName(label);
            staticRoot.AddChild(node);
        }

        private void AttachPrimaryRecordNode(
            Node3D container,
            Node3D node,
            uint hash,
            string fallbackLabel)
        {
            _placementsByHash.TryGetValue(hash, out GdbPlacement? placement);
            string layerName = ResolveTarget(hash, fallbackLabel, out string entityName, out string layerTag, out GdbSaveLoadState loadState);

            Node3D layerContainer = GetOrCreateLayerContainer(container, layerName, layerTag, loadState);
            node.Name = GetUniqueName($"{entityName} [0x{hash:X8}]");
            layerContainer.AddChild(node);

            string sourceDb = _supplementalHashes.Contains(hash)
                ? LevelRecordMetadata.SourceDbGlobals
                : _levelGdbPath;

            LevelRecordMetadata.StampRecordNode(
                node,
                hash,
                placement?.ParentHash ?? 0u,
                entityName,
                layerName,
                layerTag,
                loadState,
                sourceDb,
                node.Transform,
                LevelNodeCategory.Prop);

            ApplyEditorLoadStateVisuals(node, loadState, EditorGhostInitiallyOff);
            _primaryRecordNodes[hash] = node;
        }
    }

    /// <summary>
    /// Controls whether <see cref="GdbSaveLoadState.InitiallyOff"/> objects are rendered semi-transparent
    /// ("ghosted") in the 3D Editor viewport.
    /// </summary>
    public static bool EditorGhostInitiallyOff { get; set; } = true;

    /// <summary>
    /// Applies visual styling to a 1:1 record node in the Editor based on its <c>.save</c> load state.
    /// <see cref="GdbSaveLoadState.InitiallyOff"/> nodes can be shown ghosted (semi-transparent) so
    /// level designers can immediately distinguish inactive quest/progression layers from active ones.
    /// </summary>
    public static void ApplyEditorLoadStateVisuals(
        Node3D recordNode,
        GdbSaveLoadState loadState,
        bool ghostInitiallyOff)
    {
        float transparency = (loadState == GdbSaveLoadState.InitiallyOff && ghostInitiallyOff)
            ? 0.48f
            : 0.0f;

        ApplyTransparencyRecursive(recordNode, transparency);
    }

    private static void ApplyTransparencyRecursive(Node node, float transparency)
    {
        if (node is GeometryInstance3D geom)
        {
            geom.Transparency = transparency;
        }

        int count = node.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            Node child = node.GetChild(i);
            if (LevelRecordMetadata.IsRecordNode(child))
            {
                continue;
            }

            ApplyTransparencyRecursive(child, transparency);
        }
    }
}
