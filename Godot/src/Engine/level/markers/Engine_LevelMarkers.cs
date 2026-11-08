using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;

namespace Refii.Level;

public sealed class MarkerBuildStats
{
    public int NpcMarkers { get; set; }
    public int Spawners { get; set; }
    public int Splines { get; set; }
    public int VaultLines { get; set; }
    public int Triggers { get; set; }

    public int Total => NpcMarkers + Spawners + Splines + VaultLines + Triggers;

    public override string ToString() =>
        $"total={Total} (npcMarkers={NpcMarkers} spawners={Spawners} splines={Splines} vaultLines={VaultLines} triggers={Triggers})";
}

/// <summary>
/// Builds 1:1 selectable Editor gizmos for non-mesh GDB records referenced in <c>&lt;level&gt;.save</c>:
/// Village/NPC markers, CreatureGenerators, Patrol/Camera/Navigation Splines, VaultLines, and Triggers.
/// </summary>
public static class Engine_LevelMarkers
{
    public const string NodeName = "Markers";
    public const string MarkerCategoryMeta = "refii_marker_category";

    public const string CategoryNpcMarker = "NPCMarker";
    public const string CategorySpawner = "Spawner";
    public const string CategorySpline = "Spline";
    public const string CategoryVaultLine = "VaultLine";
    public const string CategoryTrigger = "Trigger";

    // Verified FNV-1 component property hashes in <level>.gdb / globals.gdb
    public const uint HashVillageComponent = 0x16B8DBBD;
    public const uint HashMarkerLocationTypeComponent = 0x3D6288AD;
    public const uint HashCreatureGeneratorComponent = 0x6DBFD6F4;
    public const uint HashSplineComponent = 0x2CE0E8A2;
    public const uint HashVaultLineComponent = 0x360C6730;

    public static Node3D? Build(
        LevelEntry level,
        Func<string, byte[]?> read,
        ISet<uint>? alreadySpawnedHashes,
        out MarkerBuildStats stats)
    {
        stats = new MarkerBuildStats();

        byte[]? gdbBytes = Engine_LevelGdb.TryRead(level, read, out string gdbPath);
        if (gdbBytes is null || !GdbView.TryParseShared(gdbBytes, out GdbView levelView, out _))
        {
            return null;
        }

        string savePath = Engine_LevelGdb.SiblingWithExtension(gdbPath, ".save");
        GdbSaveDocument saveDoc = Engine_GdbSaveMap.ParseDocument(read(savePath), out _);
        IReadOnlyList<GdbSaveEntityEntry> allSaveEntries = saveDoc.GetAllEntities(includeInitiallyOff: true);
        if (allSaveEntries.Count == 0)
        {
            return null;
        }

        var supplementalViews = new List<(string Path, GdbView View)>();
        foreach (string path in Engine_LevelProps.SupplementalGdbPaths)
        {
            byte[]? suppBytes = read(path) ?? read("data/" + path);
            if (suppBytes is not null && GdbView.TryParseShared(suppBytes, out GdbView suppView, out _))
            {
                supplementalViews.Add(("data/" + path, suppView));
            }
        }

        var allViews = new List<GdbView>(supplementalViews.Count + 1) { levelView };
        foreach ((string _, GdbView v) in supplementalViews)
        {
            allViews.Add(v);
        }

        var container = new Node3D { Name = NodeName };
        var layersRoot = new Node3D { Name = Engine_LevelProps.LayersContainerName };
        container.AddChild(layersRoot);

        var nameCounters = new Dictionary<string, int>(StringComparer.Ordinal);
        var layerContainers = new Dictionary<string, Node3D>(StringComparer.Ordinal);
        Node3D GetOrCreateLayerContainer(string layerName, string layerTag, GdbSaveLoadState loadState)
        {
            string effectiveName = string.IsNullOrWhiteSpace(layerName)
                ? Engine_LevelProps.UnassignedLayerName
                : layerName;
            if (layerContainers.TryGetValue(effectiveName, out Node3D? existing) &&
                GodotObject.IsInstanceValid(existing))
            {
                return existing;
            }

            var layerNode = new Node3D { Name = effectiveName };
            LevelRecordMetadata.StampLayerContainer(layerNode, effectiveName, layerTag, loadState);
            layersRoot.AddChild(layerNode);
            layerContainers[effectiveName] = layerNode;
            return layerNode;
        }

        ArrayMesh diamondMesh = BuildOctahedronMesh(0.42f, 0.75f);
        ArrayMesh spawnerMesh = BuildOctahedronMesh(0.58f, 1.05f);
        ArrayMesh triggerMesh = BuildOctahedronMesh(0.50f, 0.50f);

        StandardMaterial3D npcMarkerMat = CreateMarkerMaterial(new Color(0.20f, 0.85f, 1.00f, 0.85f));
        StandardMaterial3D spawnerMat = CreateMarkerMaterial(new Color(1.00f, 0.28f, 0.32f, 0.88f));
        StandardMaterial3D triggerMat = CreateMarkerMaterial(new Color(1.00f, 0.82f, 0.22f, 0.78f));
        StandardMaterial3D splineMat = CreateLineMaterial(new Color(1.00f, 0.65f, 0.15f, 0.95f));
        StandardMaterial3D vaultMat = CreateLineMaterial(new Color(0.25f, 0.95f, 0.45f, 0.95f));

        foreach (GdbSaveEntityEntry entry in allSaveEntries)
        {
            uint hash = entry.RecordHash;
            if (hash == 0)
            {
                continue;
            }

            bool alreadyVisual = alreadySpawnedHashes is not null && alreadySpawnedHashes.Contains(hash);

            string sourceGdbPath = gdbPath;
            GdbView? ownerView = null;
            int recordIndex = -1;

            if (levelView.TryLookup(hash, out recordIndex))
            {
                ownerView = levelView;
            }
            else
            {
                foreach ((string suppPath, GdbView suppView) in supplementalViews)
                {
                    if (suppView.TryLookup(hash, out recordIndex))
                    {
                        ownerView = suppView;
                        sourceGdbPath = suppPath;
                        break;
                    }
                }
            }

            if (ownerView is null)
            {
                continue;
            }

            string? category = ClassifyMarker(ownerView, recordIndex, allViews, entry.EntityName, alreadyVisual);
            if (category is null)
            {
                continue;
            }

            bool hasPlacement = Engine_GdbPlacements.TryLookupPlacement(
                ownerView, hash, entry.EntityName, true, out GdbPlacement placement);

            List<Vector3>? curvePoints = null;
            if (category is CategorySpline or CategoryVaultLine)
            {
                uint compHash = category == CategorySpline ? HashSplineComponent : HashVaultLineComponent;
                curvePoints = ExtractComponentPoints(allViews, hash, compHash);
            }

            if (!hasPlacement && (curvePoints is null || curvePoints.Count == 0))
            {
                continue;
            }

            Transform3D nodeTransform;
            if (hasPlacement)
            {
                nodeTransform = GdbTransformMath.BuildTransform(placement);
            }
            else
            {
                nodeTransform = new Transform3D(Basis.Identity, curvePoints![0]);
            }

            string entityName = !string.IsNullOrWhiteSpace(entry.EntityName)
                ? entry.EntityName
                : !string.IsNullOrWhiteSpace(placement?.EntityName)
                    ? placement!.EntityName
                    : $"{category}_{hash:X8}";

            Node3D markerNode;
            if (category is CategorySpline or CategoryVaultLine && curvePoints is { Count: >= 2 })
            {
                StandardMaterial3D lineMat = category == CategorySpline ? splineMat : vaultMat;
                Color labelColor = category == CategorySpline
                    ? new Color(1.0f, 0.75f, 0.25f)
                    : new Color(0.35f, 1.0f, 0.55f);
                markerNode = BuildPolylineMarkerNode(
                    curvePoints,
                    nodeTransform,
                    lineMat,
                    entityName,
                    labelColor);
            }
            else
            {
                (ArrayMesh mesh, StandardMaterial3D mat, Color labelColor) = category switch
                {
                    CategorySpawner => (spawnerMesh, spawnerMat, new Color(1.0f, 0.45f, 0.45f)),
                    CategoryTrigger => (triggerMesh, triggerMat, new Color(1.0f, 0.88f, 0.35f)),
                    _ => (diamondMesh, npcMarkerMat, new Color(0.35f, 0.90f, 1.0f)),
                };

                markerNode = BuildGizmoMarkerNode(mesh, mat, nodeTransform, entityName, labelColor);
            }

            Node3D layerContainer = GetOrCreateLayerContainer(
                entry.LayerName, entry.LayerTag, entry.LoadState);
            markerNode.Name = UniqueChildName(nameCounters, $"{entityName} [0x{hash:X8}]");
            markerNode.SetMeta(MarkerCategoryMeta, category);
            layerContainer.AddChild(markerNode);

            LevelNodeCategory nodeCat = category switch
            {
                CategorySpawner => LevelNodeCategory.CombatSpawner,
                CategorySpline or CategoryVaultLine => LevelNodeCategory.PatrolOrSpline,
                CategoryTrigger => LevelNodeCategory.TriggerOrExit,
                _ => LevelNodeCategory.VillageOrNpcMarker,
            };

            uint parentHash = hasPlacement ? placement!.ParentHash : ReadParentHash(ownerView, recordIndex);
            LevelRecordMetadata.StampRecordNode(
                markerNode,
                hash,
                parentHash,
                entityName,
                entry.LayerName,
                entry.LayerTag,
                entry.LoadState,
                sourceGdbPath,
                markerNode.Transform,
                nodeCat);

            Engine_LevelProps.ApplyEditorLoadStateVisuals(
                markerNode, entry.LoadState, ghostInitiallyOff: true);

            switch (category)
            {
                case CategoryNpcMarker:
                    stats.NpcMarkers++;
                    break;
                case CategorySpawner:
                    stats.Spawners++;
                    break;
                case CategorySpline:
                    stats.Splines++;
                    break;
                case CategoryVaultLine:
                    stats.VaultLines++;
                    break;
                case CategoryTrigger:
                    stats.Triggers++;
                    break;
            }
        }

        if (stats.Total == 0)
        {
            container.QueueFree();
            return null;
        }

        return container;
    }

    public static void SetCategoryVisibility(Node levelRoot, string category, bool visible)
    {
        if (levelRoot.GetNodeOrNull<Node3D>(NodeName) is not { } markersRoot)
        {
            return;
        }

        SetCategoryVisibilityRecursive(markersRoot, category, visible);
    }

    private static void SetCategoryVisibilityRecursive(Node current, string category, bool visible)
    {
        if (current is Node3D n3 &&
            n3.HasMeta(MarkerCategoryMeta) &&
            string.Equals(n3.GetMeta(MarkerCategoryMeta).AsString(), category, StringComparison.Ordinal))
        {
            n3.Visible = visible;
            return;
        }

        int count = current.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            SetCategoryVisibilityRecursive(current.GetChild(i), category, visible);
        }
    }

    private static string? ClassifyMarker(
        GdbView ownerView,
        int recordIndex,
        List<GdbView> allViews,
        string entityName,
        bool alreadyVisual)
    {
        uint recordHash = ownerView.RecordHash(recordIndex);

        if (alreadyVisual)
        {
            // Placed props and entities are already visual. Only check if this record declared a spline,
            // vault line, or creature spawner that requires a marker overlay.
            if (HasLocalComponent(ownerView, recordIndex, HashSplineComponent))
            {
                return CategorySpline;
            }
            if (HasLocalComponent(ownerView, recordIndex, HashVaultLineComponent))
            {
                return CategoryVaultLine;
            }
            if (HasLocalComponent(ownerView, recordIndex, HashCreatureGeneratorComponent))
            {
                return CategorySpawner;
            }
            return null;
        }

        if (HasComponent(ownerView, recordIndex, allViews, recordHash, HashSplineComponent))
        {
            return CategorySpline;
        }

        if (HasComponent(ownerView, recordIndex, allViews, recordHash, HashVaultLineComponent))
        {
            return CategoryVaultLine;
        }

        if (HasComponent(ownerView, recordIndex, allViews, recordHash, HashCreatureGeneratorComponent) ||
            entityName.Contains("CreatureGenerator", StringComparison.OrdinalIgnoreCase) ||
            entityName.StartsWith("CreatureGen", StringComparison.OrdinalIgnoreCase))
        {
            return CategorySpawner;
        }

        if (HasComponent(ownerView, recordIndex, allViews, recordHash, HashVillageComponent) ||
            HasComponent(ownerView, recordIndex, allViews, recordHash, HashMarkerLocationTypeComponent))
        {
            return CategoryNpcMarker;
        }

        if (entityName.StartsWith("Marker", StringComparison.OrdinalIgnoreCase))
        {
            if (entityName.Contains("Trigger", StringComparison.OrdinalIgnoreCase) ||
                entityName.Contains("LevelExit", StringComparison.OrdinalIgnoreCase))
            {
                return CategoryTrigger;
            }

            return CategoryNpcMarker;
        }

        if (entityName.Contains("Trigger", StringComparison.OrdinalIgnoreCase) ||
            entityName.Contains("PlayerStart", StringComparison.OrdinalIgnoreCase) ||
            entityName.Contains("LevelExit", StringComparison.OrdinalIgnoreCase))
        {
            return CategoryTrigger;
        }

        if (entityName.Contains("NavLine", StringComparison.OrdinalIgnoreCase) ||
            entityName.Contains("Patrol", StringComparison.OrdinalIgnoreCase) ||
            entityName.Contains("Spline", StringComparison.OrdinalIgnoreCase))
        {
            return CategorySpline;
        }

        return null;
    }

    private static bool HasLocalComponent(GdbView ownerView, int recordIndex, uint componentFieldHash) =>
        ownerView.TryFindLocal(recordIndex, componentFieldHash, (int)GdbFieldType.Reference, out GdbField f) &&
        f.Raw != 0 && f.Raw != Engine_LevelFx.Null;

    private static bool HasComponent(
        GdbView ownerView,
        int recordIndex,
        List<GdbView> allViews,
        uint recordHash,
        uint componentFieldHash)
    {
        if (HasLocalComponent(ownerView, recordIndex, componentFieldHash))
        {
            return true;
        }

        return Engine_LevelFx.Inherited(allViews, recordHash, componentFieldHash, GdbFieldType.Reference) is
            { Raw: not 0 and not Engine_LevelFx.Null };
    }

    private static uint ReadParentHash(GdbView view, int recordIndex) =>
        view.TryFindLocal(recordIndex, GdbView.HashParent, (int)GdbFieldType.Reference, out GdbField parent)
            ? parent.Raw
            : 0u;

    private static List<Vector3> ExtractComponentPoints(
        List<GdbView> views,
        uint entityHash,
        uint componentFieldHash)
    {
        var points = new List<Vector3>();
        if (Engine_LevelFx.Inherited(views, entityHash, componentFieldHash, GdbFieldType.Reference) is not
            { Raw: not 0 and not Engine_LevelFx.Null } compRef)
        {
            return points;
        }

        var visited = new HashSet<uint>();
        CollectVec3RecordsRecursive(views, compRef.Raw, depth: 0, visited, points);
        return points;
    }

    private static void CollectVec3RecordsRecursive(
        List<GdbView> views,
        uint currentHash,
        int depth,
        HashSet<uint> visited,
        List<Vector3> points)
    {
        if (currentHash == 0 || currentHash == Engine_LevelFx.Null || depth > 4 || !visited.Add(currentHash))
        {
            return;
        }

        foreach (GdbView view in views)
        {
            if (!view.TryLookup(currentHash, out int recordIndex))
            {
                continue;
            }

            if (view.TryReadVec3Record(recordIndex, out float vx, out float vy, out float vz) &&
                float.IsFinite(vx) && float.IsFinite(vy) && float.IsFinite(vz))
            {
                // Convert game Z-up (X, Y, Z) to region-local Godot (X, Z, Y)
                points.Add(new Vector3(vx, vz, vy));
                return;
            }

            foreach (GdbField field in view.Fields(recordIndex))
            {
                if (!field.IsReference || field.Hash == GdbView.HashParent ||
                    field.Raw == 0 || field.Raw == Engine_LevelFx.Null)
                {
                    continue;
                }

                CollectVec3RecordsRecursive(views, field.Raw, depth + 1, visited, points);
            }

            return;
        }
    }

    private static MeshInstance3D BuildGizmoMarkerNode(
        ArrayMesh mesh,
        StandardMaterial3D material,
        Transform3D transform,
        string labelText,
        Color labelColor)
    {
        var node = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = material,
            Transform = transform,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
        };

        var label = new Label3D
        {
            Name = "Label",
            Text = labelText,
            Position = new Vector3(0f, 0.95f, 0f),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Modulate = labelColor,
            OutlineModulate = new Color(0f, 0f, 0f, 0.85f),
            OutlineSize = 6,
            FontSize = 24,
            PixelSize = 0.0045f,
            VisibilityRangeEnd = 90f,
            VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
        };
        node.AddChild(label);
        return node;
    }

    private static MeshInstance3D BuildPolylineMarkerNode(
        List<Vector3> worldPoints,
        Transform3D nodeTransform,
        StandardMaterial3D material,
        string labelText,
        Color labelColor)
    {
        Transform3D inv = nodeTransform.AffineInverse();
        var vertices = new Vector3[worldPoints.Count];
        for (int i = 0; i < worldPoints.Count; i++)
        {
            vertices[i] = inv * worldPoints[i];
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;

        var mesh = new ArrayMesh();
        Refii.Core.Engine_ArrayMeshes.AddSurface(mesh, Mesh.PrimitiveType.LineStrip, arrays);

        var node = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = material,
            Transform = nodeTransform,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
        };

        var label = new Label3D
        {
            Name = "Label",
            Text = $"{labelText} ({worldPoints.Count} pts)",
            Position = vertices[0] + new Vector3(0f, 0.8f, 0f),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Modulate = labelColor,
            OutlineModulate = new Color(0f, 0f, 0f, 0.85f),
            OutlineSize = 6,
            FontSize = 22,
            PixelSize = 0.0045f,
            VisibilityRangeEnd = 110f,
            VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
        };
        node.AddChild(label);
        return node;
    }

    private static ArrayMesh BuildOctahedronMesh(float radius, float halfHeight)
    {
        Vector3 top = new(0f, halfHeight, 0f);
        Vector3 bottom = new(0f, -halfHeight, 0f);
        Vector3 pX = new(radius, 0f, 0f);
        Vector3 nX = new(-radius, 0f, 0f);
        Vector3 pZ = new(0f, 0f, radius);
        Vector3 nZ = new(0f, 0f, -radius);

        Vector3[] verts =
        {
            top, pX, pZ,
            top, pZ, nX,
            top, nX, nZ,
            top, nZ, pX,
            bottom, pZ, pX,
            bottom, nX, pZ,
            bottom, nZ, nX,
            bottom, pX, nZ,
        };

        var normals = new Vector3[verts.Length];
        for (int i = 0; i < verts.Length; i += 3)
        {
            Vector3 n = (verts[i + 1] - verts[i]).Cross(verts[i + 2] - verts[i]).Normalized();
            normals[i] = n;
            normals[i + 1] = n;
            normals[i + 2] = n;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;

        var mesh = new ArrayMesh();
        Refii.Core.Engine_ArrayMeshes.AddSurface(mesh, Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static StandardMaterial3D CreateMarkerMaterial(Color color) =>
        new()
        {
            AlbedoColor = color,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

    private static StandardMaterial3D CreateLineMaterial(Color color) =>
        new()
        {
            AlbedoColor = color,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };

    private static string UniqueChildName(Dictionary<string, int> counters, string baseName)
    {
        var sanitized = new System.Text.StringBuilder(baseName.Length);
        foreach (char c in baseName)
        {
            sanitized.Append(c is '/' or ':' or '@' or '"' ? '_' : c);
        }

        string clean = sanitized.ToString();
        if (counters.TryGetValue(clean, out int count))
        {
            count++;
            counters[clean] = count;
            return $"{clean} ({count})";
        }

        counters[clean] = 1;
        return clean;
    }
}
