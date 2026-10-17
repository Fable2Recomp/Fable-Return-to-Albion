using System;
using System.Collections.Generic;
using Refii.Core;

namespace Refii.Gdb;

/// <summary>
/// Represents the <c>load="..."</c> attribute on a layer element in a Fable 2 <c>&lt;level&gt;.save</c> XML file.
/// Verified against <c>sub_823662C8</c> (<c>0x823664B4..0x82366548</c>).
/// </summary>
public enum GdbSaveLoadState : byte
{
    AlwaysOn = 0,
    InitiallyOn = 1,
    InitiallyOff = 2,
    Unknown = 3,
}

public static class GdbSaveLoadStateExtensions
{
    public static GdbSaveLoadState ParseLoadState(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return GdbSaveLoadState.Unknown;
        }

        if (raw.Equals("AlwaysOn", StringComparison.OrdinalIgnoreCase))
        {
            return GdbSaveLoadState.AlwaysOn;
        }

        if (raw.Equals("InitiallyOn", StringComparison.OrdinalIgnoreCase))
        {
            return GdbSaveLoadState.InitiallyOn;
        }

        if (raw.Equals("InitiallyOff", StringComparison.OrdinalIgnoreCase))
        {
            return GdbSaveLoadState.InitiallyOff;
        }

        return GdbSaveLoadState.Unknown;
    }

    public static string ToXmlAttribute(this GdbSaveLoadState state) => state switch
    {
        GdbSaveLoadState.AlwaysOn => "AlwaysOn",
        GdbSaveLoadState.InitiallyOn => "InitiallyOn",
        GdbSaveLoadState.InitiallyOff => "InitiallyOff",
        _ => "InitiallyOff",
    };

    /// <summary>
    /// True if the layer spawns on a fresh new-game load (<c>AlwaysOn</c> or <c>InitiallyOn</c>).
    /// </summary>
    public static bool SpawnsByDefault(this GdbSaveLoadState state) =>
        state is GdbSaveLoadState.AlwaysOn or GdbSaveLoadState.InitiallyOn;
}

/// <summary>
/// A single <c>&lt;Entity name="..."&gt;0xHASH&lt;/Entity&gt;</c> placement inside a <c>.save</c> XML layer,
/// preserving full layer identity and load state for the 3D Editor and SaveMapCompositor.
/// </summary>
public readonly record struct GdbSaveEntityEntry(
    uint RecordHash,
    string EntityName,
    string LayerTag,
    string LayerName,
    uint LayerHash,
    GdbSaveLoadState LoadState)
{
    public bool SpawnsByDefault => LoadState.SpawnsByDefault();
    public bool LayerAlwaysOn => LoadState == GdbSaveLoadState.AlwaysOn;

    public SaveEntity ToRuntimeSaveEntity() =>
        new(RecordHash, EntityName, LayerHash, LayerAlwaysOn);
}

/// <summary>
/// Represents a single layer section (<c>&lt;id_XXXXXXXX name="..." load="..."&gt;</c>) in a <c>.save</c> file.
/// </summary>
public sealed class GdbSaveLayer
{
    public string LayerTag { get; set; } = string.Empty;
    public string LayerName { get; set; } = string.Empty;
    public uint LayerHash { get; set; }
    public GdbSaveLoadState LoadState { get; set; } = GdbSaveLoadState.AlwaysOn;
    public List<GdbSaveEntityEntry> Entities { get; } = new();

    public GdbSaveLayer() { }

    public GdbSaveLayer(string layerTag, string layerName, GdbSaveLoadState loadState)
    {
        LayerTag = layerTag;
        LayerName = layerName;
        LayerHash = string.IsNullOrEmpty(layerName) ? 0u : Engine_GameHash.Fnv1(layerName);
        LoadState = loadState;
    }

    public bool SpawnsByDefault => LoadState.SpawnsByDefault();
}

/// <summary>
/// Full structured representation of a <c>&lt;level&gt;.save</c> XML file across all layers
/// (<c>AlwaysOn</c>, <c>InitiallyOn</c>, and <c>InitiallyOff</c>).
/// </summary>
public sealed class GdbSaveDocument
{
    public string LevelName { get; set; } = string.Empty;
    public string SaveVersion { get; set; } = "2";
    public string LevelVersion { get; set; } = "4";
    public List<GdbSaveLayer> Layers { get; } = new();

    private readonly Dictionary<uint, GdbSaveEntityEntry> _entriesByHash = new();
    private readonly Dictionary<string, GdbSaveLayer> _layersByName = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<uint, GdbSaveEntityEntry> EntriesByHash => _entriesByHash;
    public IReadOnlyDictionary<string, GdbSaveLayer> LayersByName => _layersByName;

    public void RebuildIndices()
    {
        _entriesByHash.Clear();
        _layersByName.Clear();

        foreach (GdbSaveLayer layer in Layers)
        {
            if (!string.IsNullOrEmpty(layer.LayerName))
            {
                _layersByName[layer.LayerName] = layer;
            }

            foreach (GdbSaveEntityEntry entry in layer.Entities)
            {
                if (entry.RecordHash != 0)
                {
                    _entriesByHash[entry.RecordHash] = entry;
                }
            }
        }
    }

    public bool TryGetEntity(
        uint recordHash,
        out GdbSaveEntityEntry entry,
        out GdbSaveLayer? layer)
    {
        if (recordHash != 0 && _entriesByHash.TryGetValue(recordHash, out entry))
        {
            layer = FindLayer(entry.LayerName);
            return true;
        }

        entry = default;
        layer = null;
        return false;
    }

    public GdbSaveLayer? FindLayer(string layerName)
    {
        if (string.IsNullOrEmpty(layerName))
        {
            return null;
        }

        return _layersByName.TryGetValue(layerName, out GdbSaveLayer? layer) ? layer : null;
    }

    public GdbSaveLayer GetOrCreateLayer(string layerName, GdbSaveLoadState defaultState = GdbSaveLoadState.AlwaysOn)
    {
        GdbSaveLayer? existing = FindLayer(layerName);
        if (existing is not null)
        {
            return existing;
        }

        uint hash = Engine_GameHash.Fnv1(layerName);
        string tag = layerName.Equals("DefaultLayer", StringComparison.OrdinalIgnoreCase)
            ? "id_5A4CBED4"
            : $"id_{hash:X8}";

        var created = new GdbSaveLayer(tag, layerName, defaultState);
        Layers.Add(created);
        _layersByName[layerName] = created;
        return created;
    }

    public List<GdbSaveEntityEntry> GetAllEntities(bool includeInitiallyOff = true)
    {
        var list = new List<GdbSaveEntityEntry>();
        foreach (GdbSaveLayer layer in Layers)
        {
            if (!includeInitiallyOff && !layer.SpawnsByDefault)
            {
                continue;
            }

            list.AddRange(layer.Entities);
        }

        return list;
    }
}
