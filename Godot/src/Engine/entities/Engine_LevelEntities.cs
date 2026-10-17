using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;
using Refii.Level;

namespace Refii.Entities;

public sealed class EntityPlaceStats
{
    public int SaveEntities { get; set; }

    public int Creatures { get; set; }

    public int Placed { get; set; }

    public int NoTransform { get; set; }

    /// <summary>The creature's record is not in the level view at all — wrong database.</summary>
    public int CreatureMissingRecord { get; set; }

    /// <summary>The record is here but carries no transform shape we read.</summary>
    public int CreatureMissingTransform { get; set; }

    public int Animated { get; set; }

    public override string ToString() =>
        $"save={SaveEntities} creatures={Creatures} placed={Placed} " +
        $"noTransform={NoTransform} animated={Animated} " +
        $"(noRecord={CreatureMissingRecord} noShape={CreatureMissingTransform})";
}

public static class Engine_LevelEntities
{
    public const string NodeName = "Entities";

    public static Node3D? Build(
        LevelEntry level, Func<string, byte[]?> read, out EntityPlaceStats stats) =>
        Build(level, read, null, null, null, out stats);

    public static Node3D? Build(
        LevelEntry level,
        Func<string, byte[]?> read,
        Refii.Lighting.LightProbeField? probes,
        Refii.Lighting.LightingFrame? lighting,
        EntityInspectData? inspect,
        out EntityPlaceStats stats)
    {
        stats = new EntityPlaceStats();

        byte[]? levelGdb = Engine_LevelGdb.TryRead(level, read, out string gdbPath);
        if (levelGdb is null || !GdbView.TryParse(levelGdb, out GdbView levelView, out _))
        {
            return null;
        }

        string savePath = Engine_LevelGdb.SiblingWithExtension(gdbPath, ".save");
        byte[]? saveBytes = read(savePath);
        bool isEditor = Engine.IsEditorHint() || Engine_LevelScene.ForceEditorMode;
        GdbSaveDocument? editorSaveDoc = null;
        List<KeyValuePair<uint, string>> saveMap;

        if (isEditor)
        {
            editorSaveDoc = Engine_GdbSaveMap.ParseDocument(saveBytes, out _);
            IReadOnlyList<GdbSaveEntityEntry> allEntries = editorSaveDoc.GetAllEntities(includeInitiallyOff: true);
            saveMap = new List<KeyValuePair<uint, string>>(allEntries.Count);
            foreach (GdbSaveEntityEntry e in allEntries)
            {
                saveMap.Add(new KeyValuePair<uint, string>(e.RecordHash, e.EntityName));
            }
        }
        else
        {
            saveMap = Engine_GdbSaveMap.Parse(saveBytes);
        }

        stats.SaveEntities = saveMap.Count;
        if (saveMap.Count == 0)
        {
            return null;
        }

        var blobs = new List<byte[]?> { levelGdb };
        var supplementalViews = new List<(string Path, GdbView View)>();
        foreach (string path in Engine_LevelProps.SupplementalGdbPaths)
        {
            byte[]? suppBytes = read(path) ?? read("data/" + path);
            blobs.Add(suppBytes);
            if (suppBytes is not null && GdbView.TryParse(suppBytes, out GdbView suppView, out _))
            {
                supplementalViews.Add(("data/" + path, suppView));
            }
        }

        GdbMulti multi = GdbMulti.Build(blobs);
        var container = new Node3D { Name = NodeName };
        var used = new HashSet<string>();

        Node3D? editorLayersRoot = null;
        var editorLayerContainers = new Dictionary<string, Node3D>(StringComparer.Ordinal);

        Node3D GetOrCreateEditorLayerContainer(string layerName, string layerTag, GdbSaveLoadState loadState)
        {
            string effectiveName = string.IsNullOrWhiteSpace(layerName)
                ? Engine_LevelProps.UnassignedLayerName
                : layerName;
            if (editorLayerContainers.TryGetValue(effectiveName, out Node3D? existing) &&
                GodotObject.IsInstanceValid(existing))
            {
                return existing;
            }

            if (editorLayersRoot is null || !GodotObject.IsInstanceValid(editorLayersRoot))
            {
                editorLayersRoot = new Node3D { Name = Engine_LevelProps.LayersContainerName };
                container.AddChild(editorLayersRoot);
            }

            var layerNode = new Node3D { Name = UniqueName(used, effectiveName) };
            LevelRecordMetadata.StampLayerContainer(layerNode, effectiveName, layerTag, loadState);
            editorLayersRoot.AddChild(layerNode);
            editorLayerContainers[effectiveName] = layerNode;
            return layerNode;
        }

        foreach (KeyValuePair<uint, string> saved in saveMap)
        {
            if (saved.Key == 0 ||
                !Engine_EntityCatalog.TryDescribe(multi, saved.Key, out EntityEntry entry))
            {
                continue;
            }

            stats.Creatures++;

            int beforeRecord = Refii.Gdb.Engine_GdbPlacements.MissingRecord;
            string sourceGdbPath = gdbPath;
            bool foundPlacement = Engine_GdbPlacements.TryLookupPlacement(
                levelView, saved.Key, saved.Value, true, out GdbPlacement placement);

            if (!foundPlacement)
            {
                // Global NPCs (e.g., QO135_BardEntity, QO170_SaltyJack, QC180_Reaver) are referenced
                // in <level>.save but defined in Globals/globals.gdb rather than <level>.gdb.
                foreach ((string suppPath, GdbView suppView) in supplementalViews)
                {
                    if (Engine_GdbPlacements.TryLookupPlacement(
                            suppView, saved.Key, saved.Value, true, out placement))
                    {
                        foundPlacement = true;
                        sourceGdbPath = suppPath;
                        break;
                    }
                }
            }

            if (!foundPlacement)
            {
                stats.NoTransform++;
                if (Refii.Gdb.Engine_GdbPlacements.MissingRecord != beforeRecord)
                {
                    stats.CreatureMissingRecord++;
                }
                else
                {
                    stats.CreatureMissingTransform++;

                    // WHAT IS ACTUALLY IN THERE. A count says the shape is unrecognised; only
                    // the field list says which shape it is. Three is enough to see a pattern
                    // and few enough not to bury the log.
                    if (stats.CreatureMissingTransform <= 3 &&
                        levelView.TryLookup(saved.Key, out int failing))
                    {
                        var names = new System.Text.StringBuilder();
                        foreach (Refii.Gdb.GdbField field in levelView.Fields(failing))
                        {
                            names.Append($" {field.Hash:X8}:t{(byte)field.Type}");
                        }

                        GD.Print($"[entities] no transform shape for '{entry.Name}' " +
                            $"0x{saved.Key:X8}:{names}");
                    }
                }

                continue;
            }

            if (saved.Value.Length != 0)
            {
                entry.Name = saved.Value;
            }

            string displayName = saved.Value.Length != 0 ? saved.Value : entry.Name;
            var node = new Engine_EntityInstance
            {
                Name = UniqueName(
                    used,
                    isEditor ? $"{displayName} [0x{saved.Key:X8}]" : displayName),
                Transform = Engine_LevelProps.BuildTransform(placement),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
            };

            node.SetMeta(Engine_EntityInstance.NameMeta, entry.Name);
            node.SetMeta(Engine_EntityInstance.HashMeta, $"0x{entry.EntityHash:X8}");

            var models = new Godot.Collections.Array<string>();
            var modelHashes = new Godot.Collections.Array<long>();
            foreach (uint modelHash in entry.ModelHashes)
            {
                modelHashes.Add(modelHash);

                if (Engine_PropModelIndex.Resolve(
                        modelHash, Array.Empty<string>()) is string modelPath)
                {
                    models.Add(modelPath);
                }
            }

            node.SetMeta(Engine_EntityInstance.ModelsMeta, models);
            node.SetMeta(Engine_EntityInstance.ModelHashesMeta, modelHashes);

            if (models.Count != 0)
            {
                node.AddChild(new Refii.Animations.Engine_EntityAnimator
                {
                    Name = Refii.Animations.Engine_EntityAnimator.NodeName,
                    ClipLimit = isEditor ? 1 : Refii.Animations.Engine_EntityAnimator.DefaultClipLimit,
                });

                stats.Animated++;
            }

            EntityGameplay? gameplay = Engine_EntityGameplay.Extract(multi, saved.Key);
            if (gameplay is not null)
            {
                node.SetMeta(Engine_EntityInstance.FactionMeta, gameplay.FactionName);
                node.SetMeta(Engine_EntityInstance.ProfileMeta, gameplay.CombatProfileName);
                node.SetMeta(Engine_EntityInstance.StatsMeta, ToDictionary(gameplay.CoreFields));
                node.SetMeta(
                    Engine_EntityInstance.CombatMeta, ToDictionary(gameplay.CombatFields));
            }

            Refii.Lighting.Engine_LevelBakedLighting.StoreProbeSample(
                node, node.Transform.Origin, probes);
            if (inspect is not null)
            {
                Engine_EntityInspect.Annotate(node, saved.Key, inspect);
            }

            if (isEditor && editorSaveDoc is not null)
            {
                bool inSave = editorSaveDoc.TryGetEntity(
                    saved.Key, out _, out GdbSaveLayer? saveLayer);
                string layerName = inSave && saveLayer is not null
                    ? saveLayer.LayerName
                    : Engine_LevelProps.UnassignedLayerName;
                string layerTag = inSave && saveLayer is not null ? saveLayer.LayerTag : "id_5A4CBED4";
                GdbSaveLoadState loadState = inSave && saveLayer is not null
                    ? saveLayer.LoadState
                    : GdbSaveLoadState.AlwaysOn;

                Node3D layerContainer = GetOrCreateEditorLayerContainer(layerName, layerTag, loadState);
                layerContainer.AddChild(node);

                LevelRecordMetadata.StampRecordNode(
                    node,
                    saved.Key,
                    placement.ParentHash,
                    displayName,
                    layerName,
                    layerTag,
                    loadState,
                    sourceGdbPath,
                    node.Transform,
                    LevelNodeCategory.CreatureOrNpc);

                Engine_LevelProps.ApplyEditorLoadStateVisuals(node, loadState, ghostInitiallyOff: true);
            }
            else
            {
                container.AddChild(node);
            }

            stats.Placed++;
        }

        if (stats.Placed == 0)
        {
            container.QueueFree();
            return null;
        }

        return container;
    }

    private static Godot.Collections.Dictionary ToDictionary(
        List<KeyValuePair<string, string>> pairs)
    {
        var map = new Godot.Collections.Dictionary();
        foreach (KeyValuePair<string, string> pair in pairs)
        {
            map[pair.Key] = pair.Value;
        }

        return map;
    }

    private static string UniqueName(HashSet<string> used, string raw)
    {
        var text = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            text.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or ' ' ? c : '_');
        }

        string name = text.ToString().Trim();
        if (name.Length == 0)
        {
            name = "Entity";
        }

        if (used.Add(name))
        {
            return name;
        }

        for (int i = 2; ; i++)
        {
            string candidate = $"{name} ({i})";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
