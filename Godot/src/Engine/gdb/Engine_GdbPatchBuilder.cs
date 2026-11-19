using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;

namespace Refii.Gdb;

/// <summary>
/// Handles schema flattening, leaf record promotion, and binary Title Update (.gdb.tu)
/// entry reconstruction without prototype bleed or base database mutations.
/// </summary>
public static class GdbPatchBuilder
{
    /// <summary>
    /// Loads an existing Title Update (.gdb.tu) database file if present, or creates a new empty one.
    /// </summary>
    public static GdbPatchDatabase LoadOrCreatePatchDatabase(string tuPath)
    {
        if (string.IsNullOrWhiteSpace(tuPath) || !File.Exists(tuPath))
        {
            return new GdbPatchDatabase();
        }

        try
        {
            return LoadOrCreatePatchDatabase(File.ReadAllBytes(tuPath));
        }
        catch (Exception ex)
        {
            LogWarning($"[gdb_patch] Failed to read TU patch at {tuPath}: {ex.Message}");
            return new GdbPatchDatabase();
        }
    }

    /// <summary>
    /// Loads an existing Title Update (.gdb.tu) database from raw binary bytes.
    /// </summary>
    public static GdbPatchDatabase LoadOrCreatePatchDatabase(byte[]? bytes)
    {
        var db = new GdbPatchDatabase();
        if (bytes is null || bytes.Length == 0)
        {
            return db;
        }

        try
        {
            if (!GdbView.TryParse(bytes, out GdbView view, out string error))
            {
                LogWarning($"[gdb_patch] Failed to parse TU patch bytes: {error}");
                return db;
            }

            for (int i = 0; i < view.RecordCount; i++)
            {
                uint id = view.RecordHash(i);
                byte flags = view.RecordSchemaFlags(i);
                var entry = new GdbPatchEntry(id, flags);

                foreach (var (hash, type, raw) in view.DeclaredFields(i))
                {
                    entry.Properties.Add(new GdbPatchProperty(hash, type, raw));
                }

                db.Entries.Add(entry);
            }

            foreach (var kvp in view.Names)
            {
                db.Names[kvp.Key] = kvp.Value;
            }

            foreach (var aliasPair in view.AliasPairs())
            {
                db.Aliases.Add(aliasPair);
            }
        }
        catch (Exception ex)
        {
            LogWarning($"[gdb_patch] Error reading TU patch bytes: {ex.Message}");
        }

        return db;
    }

    /// <summary>
    /// Applies a Title Update (<c>.gdb.tu</c>) binary overlay onto a base GDB binary (such as <c>&lt;level&gt;.gdb</c>),
    /// returning a single merged GDB binary where modified records have their properties updated and newly added
    /// records are appended.
    /// </summary>
    public static byte[] ApplyOverlayToBaseGdb(byte[] baseGdbBytes, byte[]? tuGdbBytes)
    {
        if (tuGdbBytes is null || tuGdbBytes.Length == 0 || baseGdbBytes is null || baseGdbBytes.Length == 0)
        {
            return baseGdbBytes ?? Array.Empty<byte>();
        }

        GdbPatchDatabase tuDb = LoadOrCreatePatchDatabase(tuGdbBytes);
        if (tuDb.Entries.Count == 0 && tuDb.Aliases.Count == 0)
        {
            return baseGdbBytes;
        }

        GdbPatchDatabase baseDb = LoadOrCreatePatchDatabase(baseGdbBytes);
        if (baseDb.Entries.Count == 0)
        {
            return GdbWriter.Serialize(tuDb);
        }

        var entryById = new Dictionary<uint, GdbPatchEntry>(baseDb.Entries.Count);
        foreach (GdbPatchEntry entry in baseDb.Entries)
        {
            entryById[entry.Id] = entry;
        }

        foreach (GdbPatchEntry tuEntry in tuDb.Entries)
        {
            if (entryById.TryGetValue(tuEntry.Id, out GdbPatchEntry? existing) && existing is not null)
            {
                if (tuEntry.SchemaFlags != 0 && existing.SchemaFlags == 0)
                {
                    existing.SchemaFlags = tuEntry.SchemaFlags;
                }

                Dictionary<uint, int>? seen = tuEntry.Properties.Count > 1 ? new Dictionary<uint, int>() : null;
                foreach (GdbPatchProperty prop in tuEntry.Properties)
                {
                    int occ = 0;
                    if (seen is not null)
                    {
                        seen.TryGetValue(prop.NameHash, out occ);
                        seen[prop.NameHash] = occ + 1;
                    }

                    int propIdx = FindPropertyOccurrence(existing.Properties, prop.NameHash, occ);
                    if (propIdx >= 0)
                    {
                        existing.Properties[propIdx].Value = prop.Value;
                        existing.Properties[propIdx].Type = prop.Type;
                    }
                    else
                    {
                        existing.Properties.Add(new GdbPatchProperty(prop.NameHash, prop.Type, prop.Value));
                    }
                }
            }
            else
            {
                var cloned = new GdbPatchEntry(tuEntry.Id, tuEntry.SchemaFlags);
                foreach (GdbPatchProperty prop in tuEntry.Properties)
                {
                    cloned.Properties.Add(new GdbPatchProperty(prop.NameHash, prop.Type, prop.Value));
                }

                baseDb.Entries.Add(cloned);
                entryById[cloned.Id] = cloned;
            }
        }

        foreach (var kvp in tuDb.Names)
        {
            baseDb.Names[kvp.Key] = kvp.Value;
        }

        // By hash, the first of each: a FindIndex per alias was quadratic, 6-9 s on a phone for
        // New Game's 42,000 carry-name aliases.
        var aliasIndex = new Dictionary<uint, int>(baseDb.Aliases.Count + tuDb.Aliases.Count);
        for (int i = 0; i < baseDb.Aliases.Count; i++)
        {
            aliasIndex.TryAdd(baseDb.Aliases[i].AliasHash, i);
        }

        foreach (var (aliasHash, targetId) in tuDb.Aliases)
        {
            if (aliasIndex.TryGetValue(aliasHash, out int aliasIdx))
            {
                baseDb.Aliases[aliasIdx] = (aliasHash, targetId);
            }
            else
            {
                aliasIndex.Add(aliasHash, baseDb.Aliases.Count);
                baseDb.Aliases.Add((aliasHash, targetId));
            }
        }

        return GdbWriter.Serialize(baseDb);
    }

    /// <summary>
    /// Constructs a sparse patch entry containing ONLY the modified properties.
    /// Unmodified vanilla baseline fields are omitted so mod patch files remain sparse,
    /// preventing false collisions between mods in load order.
    /// </summary>
    public static GdbPatchEntry BuildPromotedEntry(
        GdbMulti? baseMulti,
        uint targetRecordHash,
        IEnumerable<(uint PropertyHash, GdbFieldType Type, string Value)> modifiedProps)
    {
        if (targetRecordHash == 0)
        {
            throw new ArgumentException("Target record hash cannot be 0", nameof(targetRecordHash));
        }

        byte schemaFlags = 0;
        if (baseMulti != null && baseMulti.TryLookup(targetRecordHash, out GdbCursor cursor))
        {
            schemaFlags = cursor.View.RecordSchemaFlags(cursor.Record);
        }
        else
        {
            schemaFlags = 0x01;
        }

        var entry = new GdbPatchEntry(targetRecordHash, schemaFlags);
        foreach (var (propHash, type, strVal) in modifiedProps)
        {
            uint rawValue = ParseRawValue(type, strVal);
            int existing = entry.Properties.FindIndex(p => p.NameHash == propHash);
            if (existing >= 0)
            {
                entry.Properties[existing].Value = rawValue;
                entry.Properties[existing].Type = type;
            }
            else
            {
                entry.Properties.Add(new GdbPatchProperty(propHash, type, rawValue));
            }
        }

        return entry;
    }

    /// <summary>
    /// Reconstructs full native records for all entries in a sparse patch database by filling in
    /// baseline local fields (including 'parent' and other local vanilla properties) from the base database.
    /// This produces a complete native GDB container suitable for consumption by the native Fable 2 game engine.
    /// </summary>
    public static GdbPatchDatabase SynthesizeCompleteDatabase(GdbPatchDatabase sparseDb, GdbMulti? baseMulti)
    {
        if (baseMulti == null)
        {
            return sparseDb;
        }

        var fullDb = new GdbPatchDatabase();
        foreach (var kvp in sparseDb.Names) fullDb.Names[kvp.Key] = kvp.Value;
        fullDb.Aliases.AddRange(sparseDb.Aliases);

        foreach (GdbPatchEntry sparseEntry in sparseDb.Entries)
        {
            byte schemaFlags = sparseEntry.SchemaFlags;
            var fullProperties = new List<GdbPatchProperty>();
            var propMap = new Dictionary<(uint Hash, int Occurrence), int>();
            var localHashes = new HashSet<uint>();
            var localOcc = new Dictionary<uint, int>();

            if (baseMulti.TryLookup(sparseEntry.Id, out GdbCursor cursor))
            {
                if (schemaFlags == 0)
                {
                    schemaFlags = cursor.View.RecordSchemaFlags(cursor.Record);
                }

                // The authoring form: a record's name keyed by its id. The writer files it as the
                // container holds it (GdbPatchDatabase.FileName) when the patch is written.
                string entryName = baseMulti.NameOf(sparseEntry.Id);
                if (!string.IsNullOrEmpty(entryName) && !fullDb.Names.ContainsKey(sparseEntry.Id))
                {
                    fullDb.Names[sparseEntry.Id] = entryName;
                }

                // Populate local fields FIRST in exact declared order (keeping `parent` at index 0
                // and preserving repeated-hash array elements at their original positions):
                foreach (var (hash, type, raw) in cursor.View.DeclaredFields(cursor.Record))
                {
                    localHashes.Add(hash);
                    localOcc.TryGetValue(hash, out int occ);
                    localOcc[hash] = occ + 1;
                    propMap[(hash, occ)] = fullProperties.Count;
                    fullProperties.Add(new GdbPatchProperty(hash, type, raw));
                    RecordFieldNames(fullDb, baseMulti, hash, type, raw);
                }

                // Append ancestor-only inherited properties from parent hierarchy:
                foreach (var (hash, type, raw) in baseMulti.InheritedFields(cursor))
                {
                    if (!localHashes.Add(hash))
                    {
                        continue;
                    }

                    propMap[(hash, 0)] = fullProperties.Count;
                    fullProperties.Add(new GdbPatchProperty(hash, type, raw));
                    RecordFieldNames(fullDb, baseMulti, hash, type, raw);
                }
            }

            // Apply sparse properties on top of base fields:
            Dictionary<uint, int>? sparseOcc = sparseEntry.Properties.Count > 1 ? new Dictionary<uint, int>() : null;
            foreach (GdbPatchProperty sparseProp in sparseEntry.Properties)
            {
                int occ = 0;
                if (sparseOcc is not null)
                {
                    sparseOcc.TryGetValue(sparseProp.NameHash, out occ);
                    sparseOcc[sparseProp.NameHash] = occ + 1;
                }

                if (propMap.TryGetValue((sparseProp.NameHash, occ), out int idx))
                {
                    fullProperties[idx].Value = sparseProp.Value;
                    fullProperties[idx].Type = sparseProp.Type;
                }
                else
                {
                    propMap[(sparseProp.NameHash, occ)] = fullProperties.Count;
                    fullProperties.Add(new GdbPatchProperty(sparseProp.NameHash, sparseProp.Type, sparseProp.Value));
                }

                RecordFieldNames(fullDb, baseMulti, sparseProp.NameHash, sparseProp.Type, sparseProp.Value);
            }

            var entry = new GdbPatchEntry(sparseEntry.Id, schemaFlags);
            entry.Properties.AddRange(fullProperties);
            fullDb.Entries.Add(entry);
        }

        return fullDb;
    }

    private static int FindPropertyOccurrence(List<GdbPatchProperty> properties, uint nameHash, int occurrence)
    {
        int seen = 0;
        for (int i = 0; i < properties.Count; i++)
        {
            if (properties[i].NameHash == nameHash)
            {
                if (seen == occurrence)
                {
                    return i;
                }

                seen++;
            }
        }

        return -1;
    }

    private static void RecordFieldNames(
        GdbPatchDatabase fullDb, GdbMulti baseMulti, uint hash, GdbFieldType type, uint raw)
    {
        string propName = baseMulti.NameOf(hash);
        if (!string.IsNullOrEmpty(propName) && !fullDb.Names.ContainsKey(hash))
        {
            fullDb.Names[hash] = propName;
        }

        if (type == GdbFieldType.String)
        {
            string strVal = baseMulti.NameOf(raw);
            if (!string.IsNullOrEmpty(strVal) && !fullDb.Names.ContainsKey(raw))
            {
                fullDb.Names[raw] = strVal;
            }
        }
    }

    /// <summary>
    /// Replaces an existing entry with the same ID, or appends it.
    /// </summary>
    public static void UpsertEntry(GdbPatchDatabase db, GdbPatchEntry entry)
    {
        int index = db.Entries.FindIndex(e => e.Id == entry.Id);
        if (index >= 0)
        {
            db.Entries[index] = entry;
        }
        else
        {
            db.Entries.Add(entry);
        }
    }

    /// <summary>
    /// Removes an entry with the specified ID. Returns true if an entry was removed.
    /// </summary>
    public static bool RemoveEntry(GdbPatchDatabase db, uint entryId)
    {
        int removed = db.Entries.RemoveAll(e => e.Id == entryId);
        return removed > 0;
    }

    /// <summary>
    /// Serializes and saves the patch database to disk, or deletes the file if no entries remain.
    /// </summary>
    public static void SavePatchDatabase(GdbPatchDatabase db, string tuPath)
    {
        if (db.Entries.Count == 0)
        {
            if (File.Exists(tuPath))
            {
                try
                {
                    File.Delete(tuPath);
                    LogPrint($"[gdb_patch] Deleted empty TU patch container: {tuPath}");
                }
                catch (Exception ex)
                {
                    LogWarning($"[gdb_patch] Could not delete empty TU patch {tuPath}: {ex.Message}");
                }
            }
            return;
        }

        string? dir = Path.GetDirectoryName(tuPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        byte[] binary = GdbWriter.Serialize(db);
        File.WriteAllBytes(tuPath, binary);
        LogPrint($"[gdb_patch] Compiled and wrote {db.Entries.Count} record(s) to TU patch: {tuPath} ({binary.Length} bytes)");
    }

    public static Action<string>? LogHandler;
    public static Action<string>? WarnHandler;

    private static void LogPrint(string message)
    {
        if (LogHandler is not null)
        {
            LogHandler(message);
            return;
        }

        try { GD.Print(message); } catch { }
    }

    private static void LogWarning(string message)
    {
        if (WarnHandler is not null)
        {
            WarnHandler(message);
            return;
        }

        try { GD.PushWarning(message); } catch { }
    }

    /// <summary>
    /// Parses a string representation of a property value into its raw 32-bit integer/bit representation.
    /// </summary>
    public static uint ParseRawValue(GdbFieldType type, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0u;
        }

        string trimmed = value.Trim();
        bool isHex = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                     uint.TryParse(trimmed.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
        uint parsedHex = isHex
            ? uint.Parse(trimmed.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : 0u;

        switch (type)
        {
            case GdbFieldType.Bool:
                if (bool.TryParse(trimmed, out bool b)) return b ? 1u : 0u;
                if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bi)) return bi != 0 ? 1u : 0u;
                return 0u;

            case GdbFieldType.Float:
                if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                {
                    return BitConverter.SingleToUInt32Bits(f);
                }
                if (isHex)
                {
                    return parsedHex;
                }
                return 0u;

            case GdbFieldType.Int:
            case GdbFieldType.Enum:
                if (isHex)
                {
                    return parsedHex;
                }
                if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intVal))
                {
                    return unchecked((uint)intVal);
                }
                if (uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint uintVal))
                {
                    return uintVal;
                }
                if (type == GdbFieldType.Enum)
                {
                    return Refii.Core.Engine_GameHash.Fnv1(trimmed);
                }
                return 0u;

            case GdbFieldType.String:
                if (isHex)
                {
                    return parsedHex;
                }
                return HashStringOrAssetPath(trimmed);

            case GdbFieldType.Reference:
            case GdbFieldType.ResourceReference:
                if (isHex)
                {
                    return parsedHex;
                }
                if (uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint refUint))
                {
                    return refUint;
                }
                return HashStringOrAssetPath(trimmed);

            default:
                if (isHex)
                {
                    return parsedHex;
                }
                if (uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint defaultUint))
                {
                    return defaultUint;
                }
                return HashStringOrAssetPath(trimmed);
        }
    }

    public static bool IsVirtualAssetPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        return trimmed.Contains('\\') ||
               trimmed.Contains('/') ||
               trimmed.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith(".skl", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith(".phy", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
    }

    public static uint HashStringOrAssetPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0u;
        }

        string trimmed = value.Trim();
        return IsVirtualAssetPath(trimmed)
            ? Refii.Level.Engine_PropModelIndex.PathHash(trimmed)
            : Refii.Core.Engine_GameHash.Fnv1(trimmed);
    }
}
