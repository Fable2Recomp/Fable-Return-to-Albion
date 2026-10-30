using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Godot;
using Refii.Bnk;
using Refii.Core;
using Refii.Mods;

namespace Refii.Gdb;

/// <summary>
/// Represents a sparse delta applied to a single layer inside a <c>&lt;level&gt;.save.tu</c> patch.
/// </summary>
public sealed class SaveMapLayerPatch
{
    public string LayerTag { get; set; } = string.Empty;
    public string LayerName { get; set; } = string.Empty;
    public GdbSaveLoadState? LoadOverride { get; set; }
    public List<(uint RecordHash, string EntityName)> UpsertedEntities { get; } = new();

    public SaveMapLayerPatch() { }

    public SaveMapLayerPatch(string layerTag, string layerName, GdbSaveLoadState? loadOverride = null)
    {
        LayerTag = layerTag;
        LayerName = layerName;
        LoadOverride = loadOverride;
    }
}

/// <summary>
/// Represents a sparse delta patch (<c>&lt;level&gt;.save.tu</c>) for a Fable 2 <c>&lt;level&gt;.save</c> XML file.
/// Captures layer load-state overrides, newly placed or moved entities per layer, and deleted entity hashes.
/// </summary>
public sealed class SaveMapPatch
{
    public List<SaveMapLayerPatch> Layers { get; } = new();
    public HashSet<uint> RemovedEntities { get; } = new();

    public bool IsEmpty =>
        RemovedEntities.Count == 0 &&
        Layers.All(l => l.LoadOverride is null && l.UpsertedEntities.Count == 0);

    public SaveMapLayerPatch GetOrCreateLayer(string layerName, string? layerTag = null)
    {
        if (string.IsNullOrWhiteSpace(layerName))
        {
            layerName = "DefaultLayer";
        }

        SaveMapLayerPatch? existing = Layers.FirstOrDefault(
            l => string.Equals(l.LayerName, layerName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (string.IsNullOrEmpty(existing.LayerTag) && !string.IsNullOrEmpty(layerTag))
            {
                existing.LayerTag = layerTag;
            }
            return existing;
        }

        string resolvedTag = !string.IsNullOrWhiteSpace(layerTag)
            ? layerTag!
            : layerName.Equals("DefaultLayer", StringComparison.OrdinalIgnoreCase)
                ? "id_5A4CBED4"
                : $"id_{Engine_GameHash.Fnv1(layerName):X8}";

        var created = new SaveMapLayerPatch(resolvedTag, layerName);
        Layers.Add(created);
        return created;
    }

    public void UpsertEntity(string layerName, uint recordHash, string entityName, string? layerTag = null)
    {
        if (recordHash == 0)
        {
            return;
        }

        RemovedEntities.Remove(recordHash);

        // Remove from any other layer in this patch first:
        foreach (SaveMapLayerPatch layer in Layers)
        {
            layer.UpsertedEntities.RemoveAll(e => e.RecordHash == recordHash);
        }

        SaveMapLayerPatch target = GetOrCreateLayer(layerName, layerTag);
        target.UpsertedEntities.Add((recordHash, entityName ?? string.Empty));
    }

    public void RemoveEntity(uint recordHash)
    {
        if (recordHash == 0)
        {
            return;
        }

        foreach (SaveMapLayerPatch layer in Layers)
        {
            layer.UpsertedEntities.RemoveAll(e => e.RecordHash == recordHash);
        }

        RemovedEntities.Add(recordHash);
    }

    public string SerializeToXml()
    {
        var root = new XElement("SavePatch", new XAttribute("version", "1"));

        foreach (SaveMapLayerPatch layer in Layers)
        {
            if (layer.LoadOverride is null && layer.UpsertedEntities.Count == 0)
            {
                continue;
            }

            string tag = !string.IsNullOrWhiteSpace(layer.LayerTag)
                ? layer.LayerTag
                : layer.LayerName.Equals("DefaultLayer", StringComparison.OrdinalIgnoreCase)
                    ? "id_5A4CBED4"
                    : $"id_{Engine_GameHash.Fnv1(layer.LayerName):X8}";

            var layerEl = new XElement("Layer",
                new XAttribute("tag", tag),
                new XAttribute("name", layer.LayerName));

            if (layer.LoadOverride.HasValue && layer.LoadOverride.Value != GdbSaveLoadState.Unknown)
            {
                layerEl.Add(new XAttribute("load", layer.LoadOverride.Value.ToXmlAttribute()));
            }

            foreach (var (hash, entityName) in layer.UpsertedEntities)
            {
                layerEl.Add(new XElement(
                    "Entity",
                    new XAttribute("name", entityName ?? string.Empty),
                    $"0x{hash:X8}"));
            }

            root.Add(layerEl);
        }

        foreach (uint removed in RemovedEntities.OrderBy(h => h))
        {
            root.Add(new XElement("Removed", $"0x{removed:X8}"));
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        using var sw = new Utf8StringWriter();
        doc.Save(sw);
        return sw.ToString();
    }

    public byte[] SerializeToUtf8() => Encoding.UTF8.GetBytes(SerializeToXml());

    public static SaveMapPatch Parse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return new SaveMapPatch();
        }

        return Parse(Encoding.UTF8.GetString(bytes));
    }

    public static SaveMapPatch Parse(string? xml)
    {
        var patch = new SaveMapPatch();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return patch;
        }

        try
        {
            XDocument doc = XDocument.Parse(xml);
            XElement? root = doc.Root;
            if (root is null)
            {
                return patch;
            }

            foreach (XElement layerEl in root.Elements("Layer"))
            {
                string tag = (string?)layerEl.Attribute("tag") ?? string.Empty;
                string name = (string?)layerEl.Attribute("name") ?? "DefaultLayer";
                string? loadAttr = (string?)layerEl.Attribute("load");

                GdbSaveLoadState? loadOverride = null;
                if (!string.IsNullOrEmpty(loadAttr))
                {
                    GdbSaveLoadState parsedLoad = GdbSaveLoadStateExtensions.ParseLoadState(loadAttr);
                    if (parsedLoad != GdbSaveLoadState.Unknown)
                    {
                        loadOverride = parsedLoad;
                    }
                }

                SaveMapLayerPatch layerPatch = patch.GetOrCreateLayer(name, tag);
                if (loadOverride.HasValue)
                {
                    layerPatch.LoadOverride = loadOverride;
                }

                foreach (XElement entityEl in layerEl.Elements("Entity"))
                {
                    string entityName = (string?)entityEl.Attribute("name") ?? string.Empty;
                    if (TryParseHexHash(entityEl.Value, out uint hash) && hash != 0)
                    {
                        layerPatch.UpsertedEntities.RemoveAll(e => e.RecordHash == hash);
                        layerPatch.UpsertedEntities.Add((hash, entityName));
                    }
                }
            }

            foreach (XElement removedEl in root.Elements("Removed"))
            {
                if (TryParseHexHash(removedEl.Value, out uint hash) && hash != 0)
                {
                    patch.RemovedEntities.Add(hash);
                }
            }
        }
        catch
        {
        }

        return patch;
    }

    private static bool TryParseHexHash(string? raw, out uint hash)
    {
        hash = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        ReadOnlySpan<char> span = raw.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        return uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash);
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}

/// <summary>
/// Merges <c>.save.tu</c> XML patches across multiple enabled mods in load order onto vanilla
/// <c>&lt;level&gt;.save</c> files and mounts the composite <c>.save</c> virtual files into <see cref="BnkFileSystem"/>.
/// </summary>
public static class SaveMapCompositor
{
    /// <summary>
    /// Applies a sequence of <see cref="SaveMapPatch"/> deltas (in lowest-to-highest priority order)
    /// onto a baseline <see cref="GdbSaveDocument"/>, returning a new composited <see cref="GdbSaveDocument"/>.
    /// </summary>
    public static GdbSaveDocument ApplyPatches(GdbSaveDocument baseDoc, IEnumerable<SaveMapPatch> patches)
    {
        var working = CloneDocument(baseDoc);

        foreach (SaveMapPatch patch in patches)
        {
            if (patch is null || patch.IsEmpty)
            {
                continue;
            }

            // 1. Apply explicit removals across all layers:
            if (patch.RemovedEntities.Count > 0)
            {
                foreach (GdbSaveLayer layer in working.Layers)
                {
                    layer.Entities.RemoveAll(e => patch.RemovedEntities.Contains(e.RecordHash));
                }
            }

            // 2. Apply layer load overrides and upserted/moved entities:
            foreach (SaveMapLayerPatch layerPatch in patch.Layers)
            {
                if (string.IsNullOrWhiteSpace(layerPatch.LayerName))
                {
                    continue;
                }

                GdbSaveLayer targetLayer = working.GetOrCreateLayer(
                    layerPatch.LayerName,
                    layerPatch.LoadOverride ?? GdbSaveLoadState.AlwaysOn);

                if (!string.IsNullOrWhiteSpace(layerPatch.LayerTag))
                {
                    targetLayer.LayerTag = layerPatch.LayerTag;
                }

                if (layerPatch.LoadOverride.HasValue &&
                    layerPatch.LoadOverride.Value != GdbSaveLoadState.Unknown)
                {
                    targetLayer.LoadState = layerPatch.LoadOverride.Value;
                    for (int i = 0; i < targetLayer.Entities.Count; i++)
                    {
                        GdbSaveEntityEntry existing = targetLayer.Entities[i];
                        targetLayer.Entities[i] = existing with
                        {
                            LayerTag = targetLayer.LayerTag,
                            LoadState = targetLayer.LoadState,
                        };
                    }
                }

                foreach (var (recordHash, entityName) in layerPatch.UpsertedEntities)
                {
                    if (recordHash == 0)
                    {
                        continue;
                    }

                    // Preserve existing name if the patch did not specify one:
                    string resolvedName = entityName;
                    if (string.IsNullOrEmpty(resolvedName))
                    {
                        foreach (GdbSaveLayer l in working.Layers)
                        {
                            int foundIdx = l.Entities.FindIndex(e => e.RecordHash == recordHash);
                            if (foundIdx >= 0 && !string.IsNullOrEmpty(l.Entities[foundIdx].EntityName))
                            {
                                resolvedName = l.Entities[foundIdx].EntityName;
                                break;
                            }
                        }
                    }

                    // Remove from all layers before inserting into targetLayer (handles layer moves):
                    foreach (GdbSaveLayer l in working.Layers)
                    {
                        l.Entities.RemoveAll(e => e.RecordHash == recordHash);
                    }

                    targetLayer.Entities.Add(new GdbSaveEntityEntry(
                        recordHash,
                        resolvedName,
                        targetLayer.LayerTag,
                        targetLayer.LayerName,
                        targetLayer.LayerHash,
                        targetLayer.LoadState));
                }
            }
        }

        working.RebuildIndices();
        return working;
    }

    /// <summary>
    /// Applies a sequence of <see cref="SaveMapPatch"/> deltas onto raw vanilla <c>&lt;level&gt;.save</c> bytes
    /// and returns the composited Fable 2 <c>&lt;level&gt;.save</c> UTF-8 XML bytes.
    /// </summary>
    public static byte[] ApplyPatchesToSaveBytes(byte[]? baseSaveBytes, IEnumerable<SaveMapPatch> patches)
    {
        GdbSaveDocument baseDoc = Engine_GdbSaveMap.ParseDocument(baseSaveBytes, out _);
        GdbSaveDocument mergedDoc = ApplyPatches(baseDoc, patches);
        return SerializeSaveDocumentBytes(mergedDoc);
    }

    /// <summary>
    /// Serializes a <see cref="GdbSaveDocument"/> into Fable 2's native <c>&lt;Save&gt;</c> XML format.
    /// </summary>
    public static string SerializeSaveDocument(GdbSaveDocument doc)
    {
        var sb = new StringBuilder(16384);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        string saveVer = string.IsNullOrEmpty(doc.SaveVersion) ? "2" : doc.SaveVersion;
        string levelName = doc.LevelName ?? string.Empty;
        string levelVer = string.IsNullOrEmpty(doc.LevelVersion) ? "4" : doc.LevelVersion;

        sb.Append($"<Save version=\"{EscapeXmlAttr(saveVer)}\">\n");
        sb.Append($"\t<Level name=\"{EscapeXmlAttr(levelName)}\" version=\"{EscapeXmlAttr(levelVer)}\">\n");

        foreach (GdbSaveLayer layer in doc.Layers)
        {
            string tag = !string.IsNullOrWhiteSpace(layer.LayerTag)
                ? layer.LayerTag
                : layer.LayerName.Equals("DefaultLayer", StringComparison.OrdinalIgnoreCase)
                    ? "id_5A4CBED4"
                    : $"id_{Engine_GameHash.Fnv1(layer.LayerName):X8}";
            string loadStr = layer.LoadState.ToXmlAttribute();

            if (layer.Entities.Count == 0)
            {
                sb.Append($"\t\t<{tag} name=\"{EscapeXmlAttr(layer.LayerName)}\" load=\"{loadStr}\"/>\n");
                continue;
            }

            sb.Append($"\t\t<{tag} name=\"{EscapeXmlAttr(layer.LayerName)}\" load=\"{loadStr}\">\n");
            foreach (GdbSaveEntityEntry entity in layer.Entities)
            {
                sb.Append($"\t\t\t<Entity name=\"{EscapeXmlAttr(entity.EntityName)}\">0x{entity.RecordHash:X8}</Entity>\n");
            }
            sb.Append($"\t\t</{tag}>\n");
        }

        sb.Append("\t</Level>\n");
        sb.Append("</Save>\n");
        return sb.ToString();
    }

    public static byte[] SerializeSaveDocumentBytes(GdbSaveDocument doc) =>
        Encoding.UTF8.GetBytes(SerializeSaveDocument(doc));

    /// <summary>
    /// Discovers all <c>*.save.tu</c> files across mounted <paramref name="overlays"/> (lowest priority first),
    /// merges them onto the corresponding vanilla <c>&lt;level&gt;.save</c> files, and registers the composite
    /// <c>.save</c> files into <paramref name="mounted"/>.
    /// </summary>
    public static int Apply(BnkFileSystem? mounted, IReadOnlyList<ModOverlay> overlays)
    {
        if (mounted is null || overlays is null || overlays.Count == 0)
        {
            return 0;
        }

        var patchesBySaveSubPath = new Dictionary<string, List<(string ModName, SaveMapPatch Patch)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (ModOverlay overlay in overlays)
        {
            string sourcePath = !string.IsNullOrEmpty(overlay.PackagePath) ? overlay.PackagePath : overlay.AssetRoot;
            if (File.Exists(sourcePath) && Refii.Core.Engine_Title.IsModPackagePath(sourcePath))
            {
                if (F2ModPackage.TryOpen(sourcePath, out F2ModPackage? pkg, out _) && pkg is not null)
                {
                    using (pkg)
                    {
                        foreach (F2ModEntry entry in pkg.Entries)
                        {
                            if (!entry.Path.EndsWith(".save.tu", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            string normalizedTu = ModLoadOrder.Normalize(entry.Path);
                            string saveSubPath = normalizedTu[..^3]; // strip ".tu"
                            SaveMapPatch patch = SaveMapPatch.Parse(pkg.ReadEntryBytes(entry));
                            if (patch.IsEmpty)
                            {
                                continue;
                            }

                            if (!patchesBySaveSubPath.TryGetValue(saveSubPath, out var list))
                            {
                                list = new List<(string, SaveMapPatch)>();
                                patchesBySaveSubPath[saveSubPath] = list;
                            }

                            list.Add((overlay.Name, patch));
                        }
                    }
                }
                continue;
            }

            if (!Directory.Exists(overlay.AssetRoot))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(overlay.AssetRoot, "*.save.tu", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(overlay.AssetRoot, file);
                string normalizedTu = ModLoadOrder.Normalize(relative);
                if (normalizedTu.Length <= 3)
                {
                    continue;
                }

                string saveSubPath = normalizedTu[..^3];
                SaveMapPatch patch = SaveMapPatch.Parse(File.ReadAllBytes(file));
                if (patch.IsEmpty)
                {
                    continue;
                }

                if (!patchesBySaveSubPath.TryGetValue(saveSubPath, out var list))
                {
                    list = new List<(string, SaveMapPatch)>();
                    patchesBySaveSubPath[saveSubPath] = list;
                }

                list.Add((overlay.Name, patch));
            }
        }

        int appliedCount = 0;
        foreach (var (saveSubPath, modPatches) in patchesBySaveSubPath)
        {
            byte[]? baseBytes = mounted.ReadBaseBytes(saveSubPath) ?? mounted.ReadBaseBytes("data/" + saveSubPath);
            byte[] compositeBytes = ApplyPatchesToSaveBytes(baseBytes, modPatches.Select(m => m.Patch));
            string winningMod = modPatches[^1].ModName;

            mounted.AddVirtualFile(
                saveSubPath,
                $"<save_tu:{winningMod}>",
                compositeBytes.Length,
                () => compositeBytes,
                winningMod);
            appliedCount++;
        }

        return appliedCount;
    }

    private static GdbSaveDocument CloneDocument(GdbSaveDocument source)
    {
        var clone = new GdbSaveDocument
        {
            LevelName = source.LevelName,
            SaveVersion = source.SaveVersion,
            LevelVersion = source.LevelVersion,
        };

        foreach (GdbSaveLayer layer in source.Layers)
        {
            var layerClone = new GdbSaveLayer(layer.LayerTag, layer.LayerName, layer.LoadState);
            layerClone.Entities.AddRange(layer.Entities);
            clone.Layers.Add(layerClone);
        }

        clone.RebuildIndices();
        return clone;
    }

    private static string EscapeXmlAttr(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }
}
