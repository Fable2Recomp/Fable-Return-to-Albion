#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Refii.Assets;
using Refii.Assets.UI;
using Refii.Core;
using Refii.Equipment;
using Refii.Gdb;
using Refii.Level;
using Refii.Mods;

namespace Refii.EditorTools;

public static partial class EditorModValueService
{
    private static string _activeLevelGdbSubPath = string.Empty;
    private static string _cachedLevelGdbSubPath = string.Empty;
    private static GdbMulti? _cachedBaseWithLevelMulti;

    /// <summary>
    /// Gets or sets the currently active level GDB sub-path (e.g. <c>worlds/albion/bloodstone/defaultscenario/defaultscenario.gdb</c>)
    /// so that Mod Studio and <see cref="PolymorphicItemGraphView"/> can inspect and edit level instance records alongside global archetypes.
    /// </summary>
    public static string ActiveLevelGdbSubPath
    {
        get
        {
            if (!string.IsNullOrEmpty(_activeLevelGdbSubPath))
            {
                return _activeLevelGdbSubPath;
            }

            if (TryDetectEditedSceneLevelGdbPath(out string detected))
            {
                return detected;
            }

            return string.Empty;
        }
        set
        {
            string normalized = NormalizeLevelSubPath(value);
            if (!string.Equals(_activeLevelGdbSubPath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                _activeLevelGdbSubPath = normalized;
                _cachedLevelGdbSubPath = string.Empty;
                _cachedBaseWithLevelMulti = null;
            }
        }
    }

    /// <summary>
    /// Gets or sets the target <c>.save</c> layer name in the active 3D level scene where newly placed objects
    /// should be inserted (defaults to <c>"DefaultLayer"</c>).
    /// </summary>
    public static string ActiveTargetSaveLayer { get; set; } = "DefaultLayer";

    /// <summary>
    /// Returns the base <see cref="GdbMulti"/> database combined with the currently active level's vanilla
    /// <c>&lt;level&gt;.gdb</c> view when a level scene is open or selected.
    /// </summary>
    public static GdbMulti? GetEffectiveBaseDatabase(GdbMulti? fallbackBaseMulti = null)
    {
        if (fallbackBaseMulti is not null)
        {
            return fallbackBaseMulti;
        }

        GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;
        if (baseMulti is null)
        {
            return null;
        }

        string levelSubPath = ActiveLevelGdbSubPath;
        if (string.IsNullOrEmpty(levelSubPath))
        {
            return baseMulti;
        }

        if (_cachedBaseWithLevelMulti is not null &&
            string.Equals(_cachedLevelGdbSubPath, levelSubPath, StringComparison.OrdinalIgnoreCase))
        {
            return _cachedBaseWithLevelMulti;
        }

        byte[]? levelBaseBytes = ReadVanillaFileBytes(levelSubPath);
        if (levelBaseBytes is null || levelBaseBytes.Length == 0)
        {
            return baseMulti;
        }

        _cachedLevelGdbSubPath = levelSubPath;
        _cachedBaseWithLevelMulti = GdbMulti.BuildWithOverlays(baseMulti, new[] { levelBaseBytes });
        return _cachedBaseWithLevelMulti;
    }

    private static IEnumerable<string> EnumerateGdbPathsWithActiveLevel()
    {
        foreach (string path in KnownGdbPaths)
        {
            yield return path;
        }

        string levelSubPath = ActiveLevelGdbSubPath;
        if (!string.IsNullOrEmpty(levelSubPath) &&
            !KnownGdbPaths.Any(k => string.Equals(k, levelSubPath, StringComparison.OrdinalIgnoreCase)))
        {
            yield return levelSubPath;
        }
    }

    private static bool TryResolveLevelTuSubPath(out string levelTuSubPath)
    {
        string levelSubPath = ActiveLevelGdbSubPath;
        if (!string.IsNullOrEmpty(levelSubPath))
        {
            levelTuSubPath = levelSubPath.EndsWith(".tu", StringComparison.OrdinalIgnoreCase)
                ? levelSubPath
                : levelSubPath + ".tu";
            return true;
        }

        levelTuSubPath = string.Empty;
        return false;
    }

    /// <summary>
    /// Switches the Godot Editor to the Mod Studio main screen and selects <paramref name="recordHash"/>
    /// in the GDB Hierarchy &amp; Components inspector.
    /// </summary>
    public static void OpenRecordInModStudio(uint recordHash, string? levelGdbPath = null)
    {
        if (recordHash == 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(levelGdbPath))
        {
            ActiveLevelGdbSubPath = levelGdbPath!;
        }

        try
        {
            EditorInterface.Singleton.SetMainScreenEditor("Mod Studio");
        }
        catch
        {
        }

        ModdingHubPanel.Instance?.OpenGdbNode(recordHash);
    }

    /// <summary>
    /// Reverse-matches a 3D model path (<c>.mdl</c>) to all GDB archetype records in <see cref="Engine_GameGdb.Database"/>
    /// that reference that model via <c>ModelFile</c> or <c>StaticMultipleMeshComponent</c>.
    /// Used by the 3D Inspector when clicking pre-baked <c>StaticEnvironment</c> (<c>.engine_level</c>) meshes.
    /// </summary>
    public static List<(uint RecordHash, string DisplayName)> ReverseLookupArchetypesByModelPath(string? modelPath)
    {
        var matches = new List<(uint RecordHash, string DisplayName)>();
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return matches;
        }

        uint modelHash = Engine_PropModelIndex.PathHash(modelPath!);
        if (modelHash == 0)
        {
            return matches;
        }

        GdbMulti? multi = Engine_GameGdb.Database ?? Engine_GameGdb.BaseDatabase;
        if (multi is null)
        {
            return matches;
        }

        var matchingComponentHashes = new HashSet<uint>();
        var directRecordHashes = new HashSet<uint>();

        foreach (GdbView view in multi.Views)
        {
            for (int i = 0; i < view.RecordCount; i++)
            {
                uint recHash = view.RecordHash(i);
                foreach (var (fHash, _, fRaw) in view.RawFields(i))
                {
                    if (fRaw == modelHash &&
                        (fHash == GdbHash.ModelFile ||
                         fHash == GdbHash.ModelFile1 ||
                         fHash == GdbHash.ModelFile2 ||
                         fHash == GdbHash.StaticMultipleModelFile))
                    {
                        matchingComponentHashes.Add(recHash);
                    }
                }
            }
        }

        if (matchingComponentHashes.Count == 0)
        {
            return matches;
        }

        // Walk up component -> slot -> entity records:
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (GdbView view in multi.Views)
            {
                for (int i = 0; i < view.RecordCount; i++)
                {
                    uint recHash = view.RecordHash(i);
                    foreach (var (_, fType, fRaw) in view.RawFields(i))
                    {
                        if (fType == GdbFieldType.Reference && matchingComponentHashes.Contains(fRaw))
                        {
                            matchingComponentHashes.Add(recHash);
                            if (view.TryFindLocalRaw(i, GdbView.HashParent, 6, out _, out _))
                            {
                                directRecordHashes.Add(recHash);
                            }
                        }
                    }
                }
            }
        }

        foreach (uint hash in directRecordHashes)
        {
            string name = multi.NameOf(hash);
            if (string.IsNullOrWhiteSpace(name))
            {
                Engine_RecordNameCatalog.TryGetCuratedNodeName(hash, out name);
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"Archetype 0x{hash:X8}";
            }

            matches.Add((hash, name));
        }

        matches.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return matches;
    }

    /// <summary>
    /// Spawns a new 1:1 GDB placement node into the currently edited 3D <c>LevelScene</c> under the active target
    /// <c>.save</c> layer, stamps full <see cref="LevelRecordMetadata"/>, and selects it in the 3D viewport.
    /// </summary>
    public static Node3D? PlaceRecordInActive3DScene(
        uint parentArchetypeHash,
        string? entityName,
        string? targetLayerName,
        Vector3? worldPosition,
        string? explicitModelPath,
        out string error)
    {
        error = string.Empty;
        Node? editedRoot = null;
        try
        {
            editedRoot = EditorInterface.Singleton.GetEditedSceneRoot();
        }
        catch
        {
        }

        Node3D? levelRoot = LevelRecordMetadata.FindLevelRootNode(editedRoot);
        if (levelRoot is null)
        {
            error = "Open a level scene from Game:// Levels in the 3D Editor first.";
            return null;
        }

        return PlaceRecordInLevelRoot(
            levelRoot,
            editedRoot ?? levelRoot,
            parentArchetypeHash,
            entityName,
            targetLayerName,
            worldPosition,
            explicitModelPath,
            out error);
    }

    /// <summary>
    /// Headless-compatible core implementation for spawning a new 1:1 GDB record node inside a <paramref name="levelRoot"/>.
    /// </summary>
    public static Node3D? PlaceRecordInLevelRoot(
        Node3D levelRoot,
        Node sceneOwner,
        uint parentArchetypeHash,
        string? entityName,
        string? targetLayerName,
        Vector3? worldPosition,
        string? explicitModelPath,
        out string error)
    {
        error = string.Empty;
        if (levelRoot is null)
        {
            error = "Level root is null.";
            return null;
        }

        GdbMulti? multi = GetEffectiveBaseDatabase();
        string resolvedName = entityName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(resolvedName) && parentArchetypeHash != 0)
        {
            resolvedName = multi?.NameOf(parentArchetypeHash) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(resolvedName))
            {
                Engine_RecordNameCatalog.TryGetCuratedNodeName(parentArchetypeHash, out resolvedName);
            }
        }
        if (string.IsNullOrWhiteSpace(resolvedName) && !string.IsNullOrWhiteSpace(explicitModelPath))
        {
            resolvedName = Engine_LevelProps.NameOf(explicitModelPath!);
        }
        if (string.IsNullOrWhiteSpace(resolvedName))
        {
            resolvedName = parentArchetypeHash != 0 ? $"PlacedObject_{parentArchetypeHash:X8}" : "PlacedObject";
        }

        string? modelPath = explicitModelPath;
        if (string.IsNullOrWhiteSpace(modelPath) && parentArchetypeHash != 0 && multi is not null)
        {
            foreach (GdbView view in multi.Views)
            {
                if (Engine_GdbPlacements.TryLookupModelPathHashes(view, parentArchetypeHash, out List<uint> modelHashes, out _) &&
                    modelHashes.Count > 0)
                {
                    modelPath = Engine_PropModelIndex.Resolve(modelHashes[0], Array.Empty<string>());
                    if (!string.IsNullOrWhiteSpace(modelPath))
                    {
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(modelPath))
            {
                string? curated = Refii.Core.Engine_TitleGame.Active.CuratedModelForParent(parentArchetypeHash);
                if (!string.IsNullOrWhiteSpace(curated))
                {
                    modelPath = Engine_PropModelIndex.ResolveByPath(curated!);
                }
            }
        }

        string layerName = !string.IsNullOrWhiteSpace(targetLayerName)
            ? targetLayerName!
            : !string.IsNullOrWhiteSpace(ActiveTargetSaveLayer)
                ? ActiveTargetSaveLayer
                : "DefaultLayer";

        // Mint a unique deterministic-friendly instance hash:
        uint instanceHash = Engine_GameHash.Fnv1($"{resolvedName}_{Guid.NewGuid():N}");
        if (instanceHash == 0)
        {
            instanceHash = 0x90000001u;
        }

        // Find or create Props/Layers/<layerName> container:
        Node3D propsRoot = levelRoot.GetNodeOrNull<Node3D>(Engine_LevelProps.NodeName) ?? levelRoot;
        Node3D? layersRoot = propsRoot.GetNodeOrNull<Node3D>("Layers");
        if (layersRoot is null)
        {
            layersRoot = new Node3D { Name = "Layers" };
            propsRoot.AddChild(layersRoot);
            layersRoot.Owner = sceneOwner;
        }

        string safeLayerNodeName = layerName.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        Node3D? layerContainer = layersRoot.GetNodeOrNull<Node3D>(safeLayerNodeName);
        GdbSaveLoadState layerLoadState = GdbSaveLoadState.AlwaysOn;
        string layerTag = layerName.Equals("DefaultLayer", StringComparison.OrdinalIgnoreCase)
            ? "id_5A4CBED4"
            : $"id_{Engine_GameHash.Fnv1(layerName):X8}";

        if (layerContainer is null)
        {
            layerContainer = new Node3D { Name = safeLayerNodeName };
            LevelRecordMetadata.StampLayerContainer(layerContainer, layerName, layerTag, layerLoadState);
            layersRoot.AddChild(layerContainer);
            layerContainer.Owner = sceneOwner;
        }
        else if (layerContainer.HasMeta(LevelRecordMetadata.LayerLoadStateMeta))
        {
            layerLoadState = GdbSaveLoadStateExtensions.ParseLoadState(
                layerContainer.GetMeta(LevelRecordMetadata.LayerLoadStateMeta).AsString());
            if (layerContainer.HasMeta(LevelRecordMetadata.LayerTagMeta))
            {
                layerTag = layerContainer.GetMeta(LevelRecordMetadata.LayerTagMeta).AsString();
            }
        }

        Node3D spawnedNode;
        if (!string.IsNullOrWhiteSpace(modelPath))
        {
            var meshInst = new MeshInstance3D();
            string cleanModelPath = modelPath!.Replace('\\', '/').TrimStart('/');
            if (cleanModelPath.StartsWith("game://", StringComparison.OrdinalIgnoreCase))
            {
                cleanModelPath = cleanModelPath["game://".Length..];
            }

            try
            {
                string loadUri = "game://" + cleanModelPath;
                if (ResourceLoader.Exists(loadUri))
                {
                    meshInst.Mesh = ResourceLoader.Load<Mesh>(loadUri);
                }
            }
            catch
            {
            }

            meshInst.SetMeta(LevelRecordMetadata.ModelPathMeta, cleanModelPath);
            spawnedNode = meshInst;
        }
        else
        {
            spawnedNode = new Node3D();
        }

        spawnedNode.Name = $"{resolvedName} [0x{instanceHash:X8}]";
        layerContainer.AddChild(spawnedNode);
        spawnedNode.Owner = sceneOwner;

        Vector3 targetWorldPos = worldPosition ?? ResolveDefaultCameraSpawnPosition(levelRoot);
        if (levelRoot.IsInsideTree())
        {
            spawnedNode.GlobalPosition = targetWorldPos;
        }
        else
        {
            spawnedNode.Position = targetWorldPos;
        }

        Transform3D regionTransform = LevelRecordMetadata.ComputeRegionTransform(spawnedNode, levelRoot);
        string levelGdbPath = levelRoot.HasMeta(LevelRecordMetadata.LevelGdbPathMeta)
            ? levelRoot.GetMeta(LevelRecordMetadata.LevelGdbPathMeta).AsString()
            : ActiveLevelGdbSubPath;

        LevelRecordMetadata.StampRecordNode(
            spawnedNode,
            instanceHash,
            parentArchetypeHash,
            resolvedName,
            layerName,
            layerTag,
            layerLoadState,
            levelGdbPath,
            regionTransform,
            LevelNodeCategory.Prop,
            isNewPlacement: true);

        Engine_LevelProps.ApplyEditorLoadStateVisuals(
            spawnedNode,
            layerLoadState,
            Engine_LevelProps.EditorGhostInitiallyOff);

        try
        {
            if (Engine.IsEditorHint())
            {
                EditorInterface.Singleton.SetMainScreenEditor("3D");
                var selection = EditorInterface.Singleton.GetSelection();
                selection.Clear();
                selection.AddNode(spawnedNode);
            }
        }
        catch
        {
        }

        return spawnedNode;
    }

    /// <summary>
    /// Compiles and saves all edits in the currently active 3D <c>LevelScene</c> into the active mod's
    /// <c>.f2mod</c> package (<c>&lt;level&gt;.gdb.tu</c> and <c>&lt;level&gt;.save.tu</c>), then triggers
    /// a hot-reload notification.
    /// </summary>
    public static bool SaveActiveLevelSceneToMod(
        Node3D? levelRoot,
        string? activeMod,
        out LevelPatchCompileResult result,
        out string error)
    {
        result = new LevelPatchCompileResult();
        error = string.Empty;

        if (levelRoot is null)
        {
            try
            {
                levelRoot = LevelRecordMetadata.FindLevelRootNode(EditorInterface.Singleton.GetEditedSceneRoot());
            }
            catch
            {
            }
        }

        if (levelRoot is null)
        {
            error = "No active Fable 2 LevelScene found in the 3D Editor.";
            return false;
        }

        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod! : EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(modName))
        {
            error = "No active mod selected. Create or select a mod in the Mod Studio first.";
            return false;
        }

        string modsRoot = ResolveModsRoot();
        Directory.CreateDirectory(modsRoot);
        string pkgPath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);

        string gdbSubPath = levelRoot.HasMeta(LevelRecordMetadata.LevelGdbPathMeta)
            ? NormalizeLevelSubPath(levelRoot.GetMeta(LevelRecordMetadata.LevelGdbPathMeta).AsString())
            : ActiveLevelGdbSubPath;
        string saveSubPath = levelRoot.HasMeta(LevelRecordMetadata.LevelSavePathMeta)
            ? NormalizeLevelSubPath(levelRoot.GetMeta(LevelRecordMetadata.LevelSavePathMeta).AsString())
            : Engine_LevelGdb.SiblingWithExtension(gdbSubPath, ".save");

        GdbView? baseLevelView = null;
        byte[]? baseGdbBytes = ReadVanillaFileBytes(gdbSubPath);
        if (baseGdbBytes is { Length: > 0 } && GdbView.TryParse(baseGdbBytes, out GdbView parsedView, out _))
        {
            baseLevelView = parsedView;
        }

        byte[]? baseSaveBytes = ReadVanillaFileBytes(saveSubPath);
        GdbSaveDocument baseSaveDoc = Engine_GdbSaveMap.ParseDocument(baseSaveBytes, out _);

        GdbMulti? supplementalMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;

        bool ok = LevelGdbPatchCompiler.SaveToModPackage(
            levelRoot,
            pkgPath,
            baseLevelView,
            baseSaveDoc,
            supplementalMulti,
            out result,
            out error);

        if (ok)
        {
            // Mount the newly saved .gdb.tu and composite .save into the live editor VFS immediately:
            ApplySavedLevelOverlaysToEditorVfs(result, baseSaveBytes, modName);
            EditorModExporter.NotifyModContentChanged(modName);
        }

        return ok;
    }

    private static void ApplySavedLevelOverlaysToEditorVfs(
        LevelPatchCompileResult result,
        byte[]? baseSaveBytes,
        string modName)
    {
        var fs = BnkMountPlugin.Instance?.FileSystem;
        if (fs is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(result.LevelGdbTuSubPath) && result.LevelGdbPatch.Entries.Count > 0)
        {
            byte[] tuBytes = GdbWriter.Serialize(result.LevelGdbPatch);
            fs.AddVirtualFile(
                result.LevelGdbTuSubPath,
                $"<level_tu:{modName}>",
                tuBytes.Length,
                () => tuBytes,
                modName);
        }

        if (!string.IsNullOrEmpty(result.LevelSaveSubPath) && !result.SavePatch.IsEmpty)
        {
            byte[] compositeSave = SaveMapCompositor.ApplyPatchesToSaveBytes(
                baseSaveBytes,
                new[] { result.SavePatch });
            fs.AddVirtualFile(
                result.LevelSaveSubPath,
                $"<save_tu:{modName}>",
                compositeSave.Length,
                () => compositeSave,
                modName);
        }
    }

    private static Vector3 ResolveDefaultCameraSpawnPosition(Node3D levelRoot)
    {
        try
        {
            SubViewport? vp = EditorInterface.Singleton.GetEditorViewport3D(0);
            Camera3D? cam = vp?.GetCamera3D();
            if (cam is not null && GodotObject.IsInstanceValid(cam))
            {
                return cam.GlobalPosition - cam.GlobalTransform.Basis.Z * 5.0f;
            }
        }
        catch
        {
        }

        return levelRoot.IsInsideTree() ? levelRoot.GlobalPosition : Vector3.Zero;
    }

    private static byte[]? ReadVanillaFileBytes(string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
        {
            return null;
        }

        string norm = NormalizeLevelSubPath(virtualPath);
        byte[]? bytes = BnkMountPlugin.Instance?.ResolveBaseBytes(norm)
            ?? BnkMountPlugin.Instance?.ResolveBaseBytes("data/" + norm);
        if (bytes is not null && bytes.Length > 0)
        {
            return bytes;
        }

        return Engine_BnkGdbLoader.ReadBytes(norm) ?? Engine_BnkGdbLoader.ReadBytes("data/" + norm);
    }

    private static bool TryDetectEditedSceneLevelGdbPath(out string levelGdbPath)
    {
        levelGdbPath = string.Empty;
        if (BnkMountPlugin.Instance is null ||
            AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
            System.Environment.CommandLine.Contains("testhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (!Engine.IsEditorHint())
            {
                return false;
            }

            Node? editedRoot = EditorInterface.Singleton.GetEditedSceneRoot();
            Node3D? levelRoot = LevelRecordMetadata.FindLevelRootNode(editedRoot);
            if (levelRoot is not null && levelRoot.HasMeta(LevelRecordMetadata.LevelGdbPathMeta))
            {
                levelGdbPath = NormalizeLevelSubPath(
                    levelRoot.GetMeta(LevelRecordMetadata.LevelGdbPathMeta).AsString());
                return !string.IsNullOrEmpty(levelGdbPath);
            }
        }
        catch
        {
        }

        return false;
    }

    private static string NormalizeLevelSubPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        string norm = rawPath!.Trim().Replace('\\', '/').TrimStart('/');
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

    /// <summary>
    /// Returns the list of vanilla entities in the level's .save document that are currently missing
    /// from the active scene tree (i.e. deleted by the user).
    /// </summary>
    public static List<GdbSaveEntityEntry> GetDeletedEntities(Node3D levelRoot, GdbSaveDocument? fallbackSaveDoc = null)
    {
        var deleted = new List<GdbSaveEntityEntry>();
        if (!GodotObject.IsInstanceValid(levelRoot))
        {
            return deleted;
        }

        GdbSaveDocument? baseSaveDoc = fallbackSaveDoc;
        if (baseSaveDoc is null)
        {
            string saveSubPath = levelRoot.HasMeta(LevelRecordMetadata.LevelSavePathMeta)
                ? NormalizeLevelSubPath(levelRoot.GetMeta(LevelRecordMetadata.LevelSavePathMeta).AsString())
                : (levelRoot.HasMeta(LevelRecordMetadata.LevelGdbPathMeta)
                    ? Engine_LevelGdb.SiblingWithExtension(NormalizeLevelSubPath(levelRoot.GetMeta(LevelRecordMetadata.LevelGdbPathMeta).AsString()), ".save")
                    : string.Empty);

            if (string.IsNullOrEmpty(saveSubPath))
            {
                return deleted;
            }

            byte[]? baseSaveBytes = ReadVanillaFileBytes(saveSubPath);
            if (baseSaveBytes is null || baseSaveBytes.Length == 0)
            {
                return deleted;
            }

            baseSaveDoc = Engine_GdbSaveMap.ParseDocument(baseSaveBytes, out _);
        }
        var liveHashes = new HashSet<uint>();
        LevelRecordMetadata.CollectRecordHashes(levelRoot, liveHashes);

        HashSet<uint>? baselineSpawned = null;
        if (levelRoot.HasMeta(LevelRecordMetadata.SpawnedRecordHashesMeta))
        {
            baselineSpawned = new HashSet<uint>();
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

        return ResolveDeletedEntities(liveHashes, baselineSpawned, baseSaveDoc);
    }

    /// <summary>
    /// Pure computation helper: resolves which baseline-spawned entities are missing from the live
    /// scene record hashes and sorts them deterministically by layer and entity name.
    /// </summary>
    public static List<GdbSaveEntityEntry> ResolveDeletedEntities(
        IReadOnlyCollection<uint> liveRecordHashes,
        IReadOnlyCollection<uint>? baselineSpawnedHashes,
        GdbSaveDocument baseSaveDoc)
    {
        var deleted = new List<GdbSaveEntityEntry>();
        if (baselineSpawnedHashes is null)
        {
            return deleted;
        }

        foreach (uint spawnedHash in baselineSpawnedHashes)
        {
            if (spawnedHash != 0 && !liveRecordHashes.Contains(spawnedHash))
            {
                if (baseSaveDoc.TryGetEntity(spawnedHash, out GdbSaveEntityEntry entry, out _))
                {
                    deleted.Add(entry);
                }
                else
                {
                    deleted.Add(new GdbSaveEntityEntry
                    {
                        RecordHash = spawnedHash,
                        EntityName = $"Entity_0x{spawnedHash:X8}",
                        LayerName = "DefaultLayer",
                        LayerTag = "id_5A4CBED4",
                        LoadState = GdbSaveLoadState.AlwaysOn,
                    });
                }
            }
        }

        deleted.Sort((a, b) =>
        {
            int cmp = string.Compare(a.LayerName, b.LayerName, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : string.Compare(a.EntityName, b.EntityName, StringComparison.OrdinalIgnoreCase);
        });

        return deleted;
    }

    /// <summary>
    /// Restores a previously deleted vanilla entity back into the 3D level scene at its original
    /// vanilla transform and layer container, clearing its deletion.
    /// </summary>
    public static Node3D? RestoreDeletedEntity(Node3D levelRoot, uint entityHash, out string error)
    {
        error = string.Empty;
        if (!GodotObject.IsInstanceValid(levelRoot))
        {
            error = "Invalid level root node.";
            return null;
        }

        string gdbSubPath = levelRoot.HasMeta(LevelRecordMetadata.LevelGdbPathMeta)
            ? NormalizeLevelSubPath(levelRoot.GetMeta(LevelRecordMetadata.LevelGdbPathMeta).AsString())
            : ActiveLevelGdbSubPath;
        string saveSubPath = levelRoot.HasMeta(LevelRecordMetadata.LevelSavePathMeta)
            ? NormalizeLevelSubPath(levelRoot.GetMeta(LevelRecordMetadata.LevelSavePathMeta).AsString())
            : Engine_LevelGdb.SiblingWithExtension(gdbSubPath, ".save");

        byte[]? baseSaveBytes = ReadVanillaFileBytes(saveSubPath);
        GdbSaveDocument baseSaveDoc = Engine_GdbSaveMap.ParseDocument(baseSaveBytes, out _);

        if (!baseSaveDoc.TryGetEntity(entityHash, out GdbSaveEntityEntry entityEntry, out _))
        {
            error = $"Entity 0x{entityHash:X8} not found in base .save document.";
            return null;
        }

        GdbView? baseLevelView = null;
        byte[]? baseGdbBytes = ReadVanillaFileBytes(gdbSubPath);
        if (baseGdbBytes is { Length: > 0 } && GdbView.TryParse(baseGdbBytes, out GdbView parsedView, out _))
        {
            baseLevelView = parsedView;
        }

        GdbMulti? supplementalMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;

        GdbPlacement? placement = null;
        if (baseLevelView is not null && Engine_GdbPlacements.TryLookupPlacement(
                baseLevelView, entityHash, entityEntry.EntityName, true, out GdbPlacement p1))
        {
            placement = p1;
        }
        else if (supplementalMulti is not null)
        {
            foreach (GdbView v in supplementalMulti.Views)
            {
                if (Engine_GdbPlacements.TryLookupPlacement(v, entityHash, entityEntry.EntityName, true, out GdbPlacement p2))
                {
                    placement = p2;
                    break;
                }
            }
        }

        Node3D? existingCulledNode = null;
        void FindCulledNode(Node parent)
        {
            if (parent is Node3D n3d && LevelRecordMetadata.GetRecordHash(n3d) == entityHash)
            {
                existingCulledNode = n3d;
                return;
            }
            int cc = parent.GetChildCount();
            for (int i = 0; i < cc; i++)
            {
                FindCulledNode(parent.GetChild(i));
                if (existingCulledNode is not null) return;
            }
        }
        FindCulledNode(levelRoot);

        Transform3D regionTransform = placement is not null
            ? GdbTransformMath.BuildTransform(placement)
            : Transform3D.Identity;

        if (existingCulledNode is not null)
        {
            if (LevelRecordMetadata.TryGetBaseTransform(existingCulledNode, out Transform3D bt))
            {
                regionTransform = bt;
            }
            LevelRecordMetadata.ApplyRegionTransform(existingCulledNode, regionTransform, levelRoot);
            existingCulledNode.Visible = true;
            RemoveRecordTuOverride(levelRoot, entityHash);

            try
            {
                if (Engine.IsEditorHint())
                {
                    var selection = EditorInterface.Singleton.GetSelection();
                    selection.Clear();
                    selection.AddNode(existingCulledNode);
                    EditorInterface.Singleton.InspectObject(existingCulledNode);
                }
            }
            catch
            {
            }

            GD.Print($"[level_edit] Restored culled vanilla entity '{entityEntry.EntityName}' [0x{entityHash:X8}].");
            return existingCulledNode;
        }

        string modelPath = string.Empty;
        if (placement is not null && placement.ModelPathHash != 0)
        {
            modelPath = Engine_PropModelIndex.Resolve(placement.ModelPathHash, Array.Empty<string>()) ?? string.Empty;
        }

        Node sceneOwner = levelRoot.Owner ?? levelRoot;
        string layerName = string.IsNullOrEmpty(entityEntry.LayerName) ? "DefaultLayer" : entityEntry.LayerName;
        string layerTag = string.IsNullOrEmpty(entityEntry.LayerTag) ? "id_5A4CBED4" : entityEntry.LayerTag;
        GdbSaveLoadState layerLoadState = entityEntry.LoadState;

        Node3D propsRoot = levelRoot.GetNodeOrNull<Node3D>(Engine_LevelProps.NodeName) ?? levelRoot;
        Node3D? layersRoot = propsRoot.GetNodeOrNull<Node3D>("Layers");
        if (layersRoot is null)
        {
            layersRoot = new Node3D { Name = "Layers" };
            propsRoot.AddChild(layersRoot);
            layersRoot.Owner = sceneOwner;
        }

        string safeLayerNodeName = layerName.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        Node3D? layerContainer = layersRoot.GetNodeOrNull<Node3D>(safeLayerNodeName);
        if (layerContainer is null)
        {
            layerContainer = new Node3D { Name = safeLayerNodeName };
            LevelRecordMetadata.StampLayerContainer(layerContainer, layerName, layerTag, layerLoadState);
            layersRoot.AddChild(layerContainer);
            layerContainer.Owner = sceneOwner;
        }

        Node3D restoredNode;
        if (!string.IsNullOrWhiteSpace(modelPath))
        {
            var meshInst = new MeshInstance3D();
            string cleanModel = modelPath.Replace('\\', '/').TrimStart('/');
            if (cleanModel.StartsWith("game://", StringComparison.OrdinalIgnoreCase))
            {
                cleanModel = cleanModel["game://".Length..];
            }

            try
            {
                string loadUri = "game://" + cleanModel;
                if (ResourceLoader.Exists(loadUri))
                {
                    meshInst.Mesh = ResourceLoader.Load<Mesh>(loadUri);
                }
            }
            catch
            {
            }

            meshInst.SetMeta(LevelRecordMetadata.ModelPathMeta, cleanModel);
            restoredNode = meshInst;
        }
        else
        {
            restoredNode = new Node3D();
        }

        restoredNode.Name = $"{entityEntry.EntityName} [0x{entityHash:X8}]";
        layerContainer.AddChild(restoredNode);
        restoredNode.Owner = sceneOwner;

        if (levelRoot.IsInsideTree())
        {
            restoredNode.GlobalTransform = levelRoot.GlobalTransform * regionTransform;
        }
        else
        {
            restoredNode.Transform = regionTransform;
        }

        LevelRecordMetadata.StampRecordNode(
            restoredNode,
            entityHash,
            placement?.ParentHash ?? 0,
            entityEntry.EntityName,
            layerName,
            layerTag,
            layerLoadState,
            gdbSubPath,
            regionTransform,
            LevelNodeCategory.Prop,
            isNewPlacement: false);

        Engine_LevelProps.ApplyEditorLoadStateVisuals(
            restoredNode,
            layerLoadState,
            Engine_LevelProps.EditorGhostInitiallyOff);

        if (levelRoot.HasMeta(LevelRecordMetadata.SpawnedRecordHashesMeta))
        {
            long[] oldArr = levelRoot.GetMeta(LevelRecordMetadata.SpawnedRecordHashesMeta).AsInt64Array();
            var newArr = new List<long>(oldArr.Length + 1);
            foreach (long h in oldArr)
            {
                newArr.Add(h);
            }
            if (!newArr.Contains((long)entityHash))
            {
                newArr.Add((long)entityHash);
            }
            levelRoot.SetMeta(LevelRecordMetadata.SpawnedRecordHashesMeta, newArr.ToArray());
        }

        try
        {
            if (Engine.IsEditorHint())
            {
                var selection = EditorInterface.Singleton.GetSelection();
                selection.Clear();
                selection.AddNode(restoredNode);
                EditorInterface.Singleton.InspectObject(restoredNode);
            }
        }
        catch
        {
        }

        RemoveRecordTuOverride(levelRoot, entityHash);

        GD.Print($"[level_edit] Restored vanilla entity '{entityEntry.EntityName}' [0x{entityHash:X8}] to layer '{layerName}'.");
        return restoredNode;
    }

    private static string ResolveActiveModPackagePath(string? activeMod = null)
    {
        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod! : EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(modName))
        {
            return string.Empty;
        }

        string modsRoot = ResolveModsRoot();
        return Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
    }

    /// <summary>
    /// Removes any transform override entries for <paramref name="recordHash"/> from the active
    /// mod package's <c>&lt;level&gt;.gdb.tu</c>.
    /// </summary>
    public static bool RemoveRecordTuOverride(Node3D levelRoot, uint recordHash)
    {
        if (!GodotObject.IsInstanceValid(levelRoot) || recordHash == 0)
        {
            return false;
        }

        string modPath = ResolveActiveModPackagePath();
        if (string.IsNullOrEmpty(modPath) || !File.Exists(modPath))
        {
            return true;
        }

        string levelGdbSubPath = levelRoot.HasMeta(LevelRecordMetadata.LevelGdbPathMeta)
            ? NormalizeLevelSubPath(levelRoot.GetMeta(LevelRecordMetadata.LevelGdbPathMeta).AsString())
            : string.Empty;

        if (string.IsNullOrEmpty(levelGdbSubPath))
        {
            return false;
        }

        string levelTuSubPath = levelGdbSubPath.EndsWith(".tu", StringComparison.OrdinalIgnoreCase)
            ? levelGdbSubPath
            : levelGdbSubPath + ".tu";

        try
        {
            if (!F2ModPackage.TryOpen(modPath, out F2ModPackage? pkg, out _) || pkg is null)
            {
                return false;
            }

            var upserts = new List<(string VirtualPath, byte[] Data)>();
            var removals = new List<string>();

            using (pkg)
            {
                if (pkg.TryGetEntry(levelTuSubPath, out F2ModEntry? lvlTuEntry) && lvlTuEntry is not null)
                {
                    byte[] bytes = pkg.ReadEntryBytes(lvlTuEntry);
                    GdbPatchDatabase patch = GdbPatchBuilder.LoadOrCreatePatchDatabase(bytes);
                    GdbMulti? baseMulti = GetEffectiveBaseDatabase();
                    GdbView? ownerView = null;
                    if (baseMulti is not null)
                    {
                        foreach (GdbView view in baseMulti.Views)
                        {
                            if (view.TryLookup(recordHash, out _))
                            {
                                ownerView = view;
                                break;
                            }
                        }
                    }

                    LevelGdbPatchCompiler.ClearRecordTransformEntries(patch, ownerView, recordHash);
                    if (patch.Entries.Count == 0)
                    {
                        removals.Add(levelTuSubPath);
                    }
                    else
                    {
                        upserts.Add((levelTuSubPath, GdbWriter.Serialize(patch)));
                    }
                }
            }

            if (upserts.Count > 0 || removals.Count > 0)
            {
                F2ModPackage.IncrementalSync(modPath, upserts, removals);
                GD.Print($"[level_edit] Purged .tu override for 0x{recordHash:X8} in '{modPath}'.");
            }

            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[level_edit] Failed to purge .tu override for 0x{recordHash:X8}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reverts all live scene nodes under <paramref name="levelRoot"/> back to their authoritative GDB transforms
    /// (restoring moved objects to baseline, restoring deleted vanilla items, and removing unsaved new placements).
    /// </summary>
    public static void RevertLevelSceneToGdbTransforms(Node3D levelRoot)
    {
        if (!GodotObject.IsInstanceValid(levelRoot))
        {
            return;
        }

        var liveNodes = new List<Node3D>();
        void CollectRecordNodes(Node parent)
        {
            if (parent is Node3D n3d && LevelRecordMetadata.GetRecordHash(n3d) != 0)
            {
                liveNodes.Add(n3d);
            }
            int childCount = parent.GetChildCount();
            for (int i = 0; i < childCount; i++)
            {
                CollectRecordNodes(parent.GetChild(i));
            }
        }
        CollectRecordNodes(levelRoot);

        HashSet<uint>? baselineSpawned = null;
        if (levelRoot.HasMeta(LevelRecordMetadata.SpawnedRecordHashesMeta))
        {
            baselineSpawned = new HashSet<uint>();
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

        foreach (Node3D node in liveNodes)
        {
            uint hash = LevelRecordMetadata.GetRecordHash(node);
            bool isNew = node.HasMeta(LevelRecordMetadata.IsNewPlacementMeta) &&
                         node.GetMeta(LevelRecordMetadata.IsNewPlacementMeta).AsBool();

            if (isNew || (baselineSpawned is not null && !baselineSpawned.Contains(hash)))
            {
                node.GetParent()?.RemoveChild(node);
                node.QueueFree();
            }
            else if (LevelRecordMetadata.TryGetBaseTransform(node, out Transform3D baseTransform))
            {
                LevelRecordMetadata.ApplyRegionTransform(node, baseTransform, levelRoot);
            }
        }

        List<GdbSaveEntityEntry> deleted = GetDeletedEntities(levelRoot);
        foreach (GdbSaveEntityEntry del in deleted)
        {
            RestoreDeletedEntity(levelRoot, del.RecordHash, out _);
        }

        try
        {
            if (Engine.IsEditorHint())
            {
                EditorInterface.Singleton.GetSelection().Clear();
            }
        }
        catch
        {
        }

        GD.Print($"[level_edit] Reverted level scene to GDB transforms ({liveNodes.Count} checked, {deleted.Count} restored).");
    }
}
#endif
