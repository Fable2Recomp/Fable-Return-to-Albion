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
    /// Reconstructs a leaf record by copying its original local fields (including 'parent'),
    /// updating modified local properties, and promoting modified inherited properties into local fields.
    /// The ancestor prototype is never modified, isolating sibling records.
    /// </summary>
    public static GdbPatchEntry BuildPromotedEntry(
        GdbMulti baseMulti,
        uint targetRecordHash,
        IEnumerable<(uint PropertyHash, GdbFieldType Type, string Value)> modifiedProps)
    {
        if (targetRecordHash == 0)
        {
            throw new ArgumentException("Target record hash cannot be 0", nameof(targetRecordHash));
        }

        byte schemaFlags = 0;
        var properties = new List<GdbPatchProperty>();
        var localPropertyMap = new Dictionary<uint, int>();

        // 1. Copy local vanilla fields from base database:
        if (baseMulti != null && baseMulti.TryLookup(targetRecordHash, out GdbCursor cursor))
        {
            schemaFlags = cursor.View.RecordSchemaFlags(cursor.Record);

            foreach (var (hash, type, raw) in cursor.View.RawFields(cursor.Record))
            {
                localPropertyMap[hash] = properties.Count;
                properties.Add(new GdbPatchProperty(hash, type, raw));
            }
        }

        // 2. Apply modifications and promote inherited properties:
        foreach (var (propHash, type, strVal) in modifiedProps)
        {
            uint rawValue = ParseRawValue(type, strVal);

            if (localPropertyMap.TryGetValue(propHash, out int existingIndex))
            {
                // Already local on the leaf record: update in-place:
                properties[existingIndex].Value = rawValue;
                properties[existingIndex].Type = type;
            }
            else
            {
                // Inherited from an ancestor prototype: PROMOTE to local leaf property:
                localPropertyMap[propHash] = properties.Count;
                properties.Add(new GdbPatchProperty(propHash, type, rawValue));
            }
        }

        var entry = new GdbPatchEntry(targetRecordHash, schemaFlags);
        entry.Properties.AddRange(properties);
        return entry;
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
