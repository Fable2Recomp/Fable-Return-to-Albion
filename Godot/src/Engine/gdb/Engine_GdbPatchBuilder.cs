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
        var db = new GdbPatchDatabase();
        if (string.IsNullOrWhiteSpace(tuPath) || !File.Exists(tuPath))
        {
            return db;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(tuPath);
            if (!GdbView.TryParse(bytes, out GdbView view, out string error))
            {
                LogWarning($"[gdb_patch] Failed to parse existing TU patch at {tuPath}: {error}");
                return db;
            }

            for (int i = 0; i < view.RecordCount; i++)
            {
                uint id = view.RecordHash(i);
                byte flags = view.RecordSchemaFlags(i);
                var entry = new GdbPatchEntry(id, flags);

                foreach (var (hash, type, raw) in view.RawFields(i))
                {
                    entry.Properties.Add(new GdbPatchProperty(hash, type, raw));
                }

                db.Entries.Add(entry);
            }

            foreach (var kvp in view.Names)
            {
                db.Names[kvp.Key] = kvp.Value;
            }
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[gdb_patch] Error reading existing TU patch at {tuPath}: {ex.Message}");
        }

        return db;
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
            var propMap = new Dictionary<uint, int>();

            if (baseMulti.TryLookup(sparseEntry.Id, out GdbCursor cursor))
            {
                if (schemaFlags == 0)
                {
                    schemaFlags = cursor.View.RecordSchemaFlags(cursor.Record);
                }

                // Populate all inherited properties from parent hierarchy:
                foreach (var (hash, type, raw) in baseMulti.InheritedFields(cursor))
                {
                    propMap[hash] = fullProperties.Count;
                    fullProperties.Add(new GdbPatchProperty(hash, type, raw));
                }

                // Ensure local fields (such as parent reference) are also included:
                foreach (var (hash, type, raw) in cursor.View.RawFields(cursor.Record))
                {
                    if (propMap.TryGetValue(hash, out int idx))
                    {
                        fullProperties[idx].Value = raw;
                        fullProperties[idx].Type = type;
                    }
                    else
                    {
                        propMap[hash] = fullProperties.Count;
                        fullProperties.Add(new GdbPatchProperty(hash, type, raw));
                    }
                }
            }

            // Apply sparse properties on top of base fields:
            foreach (GdbPatchProperty sparseProp in sparseEntry.Properties)
            {
                if (propMap.TryGetValue(sparseProp.NameHash, out int idx))
                {
                    fullProperties[idx].Value = sparseProp.Value;
                    fullProperties[idx].Type = sparseProp.Type;
                }
                else
                {
                    propMap[sparseProp.NameHash] = fullProperties.Count;
                    fullProperties.Add(new GdbPatchProperty(sparseProp.NameHash, sparseProp.Type, sparseProp.Value));
                }
            }

            var entry = new GdbPatchEntry(sparseEntry.Id, schemaFlags);
            entry.Properties.AddRange(fullProperties);
            fullDb.Entries.Add(entry);
        }

        return fullDb;
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
                return 0u;

            case GdbFieldType.Int:
            case GdbFieldType.Enum:
                if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (uint.TryParse(trimmed.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hexVal))
                    {
                        return hexVal;
                    }
                }
                if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intVal))
                {
                    return unchecked((uint)intVal);
                }
                if (uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint uintVal))
                {
                    return uintVal;
                }
                return 0u;

            case GdbFieldType.Reference:
            case GdbFieldType.ResourceReference:
                if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (uint.TryParse(trimmed.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint refHex))
                    {
                        return refHex;
                    }
                }
                if (uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint refUint))
                {
                    return refUint;
                }
                return 0u;

            default:
                if (uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint defaultUint))
                {
                    return defaultUint;
                }
                return 0u;
        }
    }
}
