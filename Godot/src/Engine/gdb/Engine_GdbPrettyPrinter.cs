using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;

namespace Refii.Gdb;

/// <summary>
/// Utility tool to decode, inspect, and pretty-print binary Title Update (.gdb.tu) patch files.
/// Displays decoded record hashes, property names, types, raw/formatted values, and vanilla baseline comparisons.
/// </summary>
public static class GdbPrettyPrinter
{
    public static string Print(string filePath, GdbMulti? baseMulti = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return $"[GdbPrettyPrinter] File not found: '{filePath}'";
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            GdbPatchDatabase db = GdbPatchBuilder.LoadOrCreatePatchDatabase(filePath);
            return Print(db, filePath, baseMulti, bytes.Length);
        }
        catch (Exception ex)
        {
            return $"[GdbPrettyPrinter] Error reading '{filePath}': {ex.Message}";
        }
    }

    public static string Print(GdbPatchDatabase db, string title = "", GdbMulti? baseMulti = null, long fileSize = -1)
    {
        if (db == null)
        {
            return "[GdbPrettyPrinter] Null database";
        }

        baseMulti ??= Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;

        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine($" GDB Title Update Patch: {(string.IsNullOrEmpty(title) ? "(in-memory)" : title)}");
        if (fileSize >= 0)
        {
            sb.AppendLine($" Size: {fileSize:N0} bytes");
        }
        sb.AppendLine($" Records: {db.Entries.Count}  |  Aliases: {db.Aliases.Count}  |  Authored Names: {db.Names.Count}");
        sb.AppendLine("================================================================================");

        if (db.Names.Count > 0)
        {
            sb.AppendLine(" Authored String Dictionary:");
            foreach (var (hash, name) in db.Names)
            {
                sb.AppendLine($"   • 0x{hash:X8} => \"{name}\"");
            }
            sb.AppendLine("--------------------------------------------------------------------------------");
        }

        if (db.Entries.Count == 0)
        {
            sb.AppendLine(" (No records in patch)");
            sb.AppendLine("================================================================================");
            return sb.ToString();
        }

        for (int i = 0; i < db.Entries.Count; i++)
        {
            GdbPatchEntry entry = db.Entries[i];
            string recName = db.Names.TryGetValue(entry.Id, out string? nameInDb) && !string.IsNullOrEmpty(nameInDb)
                ? nameInDb
                : (baseMulti?.NameOf(entry.Id) ?? string.Empty);

            if (string.IsNullOrEmpty(recName))
            {
                if (Refii.Equipment.Engine_WeaponIndex.TryGetByHash(entry.Id, out var weaponEntry))
                {
                    recName = weaponEntry.Name;
                    if (weaponEntry.Detail.WeaponComponent == entry.Id)
                    {
                        recName += " (WeaponComponent)";
                    }
                }
                else if (Refii.Equipment.Engine_ItemIndex.TryGetByHash(entry.Id, out var itemEntry))
                {
                    recName = itemEntry.Name;
                    if (itemEntry.Detail.InventoryComponentHash == entry.Id)
                    {
                        recName += " (InventoryItemComponent)";
                    }
                }
            }

            string recLabel = !string.IsNullOrEmpty(recName) ? $"\"{recName}\"" : "(unnamed)";
            sb.AppendLine($" [Record {i + 1}/{db.Entries.Count}] Hash: 0x{entry.Id:X8} {recLabel} (Flags: 0x{entry.SchemaFlags:X2}, Fields: {entry.Properties.Count})");

            GdbCursor baseCursor = default;
            bool hasCursor = baseMulti != null && baseMulti.TryLookup(entry.Id, out baseCursor);

            foreach (GdbPatchProperty prop in entry.Properties)
            {
                string propName = db.Names.TryGetValue(prop.NameHash, out string? nameFromDb) && !string.IsNullOrEmpty(nameFromDb)
                    ? nameFromDb
                    : (baseMulti?.NameOf(prop.NameHash) ?? string.Empty);

                if (string.IsNullOrEmpty(propName))
                {
                    propName = prop.NameHash == GdbView.HashParent ? "parent" : $"0x{prop.NameHash:X8}";
                }

                string formattedVal = FormatValue(prop.Type, prop.Value, baseMulti);
                bool isAuthored = db.Names.ContainsKey(prop.NameHash);

                string diffInfo = string.Empty;
                if (hasCursor && baseMulti != null)
                {
                    if (baseMulti.TryFindInherited(baseCursor, prop.NameHash, -1, out _, out uint baseRaw, out GdbFieldType baseType))
                    {
                        string vanillaVal = FormatValue(baseType, baseRaw, baseMulti);
                        if (prop.Value != baseRaw)
                        {
                            diffInfo = $" [MODIFIED] (Vanilla: {vanillaVal})";
                        }
                        else
                        {
                            diffInfo = $" [Baseline: {vanillaVal}]";
                        }
                    }
                    else
                    {
                        diffInfo = " [PROMOTED / NEW FIELD]";
                    }
                }
                else if (isAuthored)
                {
                    diffInfo = " [AUTHORED]";
                }

                sb.AppendLine($"   • {propName,-24} (0x{prop.NameHash:X8}) [{prop.Type}] = {formattedVal}{diffInfo}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("================================================================================");
        return sb.ToString();
    }

    public static void Log(string filePath)
    {
        string text = Print(filePath);
        GD.Print(text);
    }

    private static string FormatValue(GdbFieldType type, uint raw, GdbMulti? multi)
    {
        return type switch
        {
            GdbFieldType.Bool => raw != 0 ? "true" : "false",
            GdbFieldType.Int => unchecked((int)raw).ToString(CultureInfo.InvariantCulture),
            GdbFieldType.Float => BitConverter.Int32BitsToSingle(unchecked((int)raw)).ToString("0.######", CultureInfo.InvariantCulture),
            GdbFieldType.String => $"<string 0x{raw:X8}>",
            GdbFieldType.Enum => multi?.NameOf(raw) is { Length: > 0 } enumName ? $"{enumName} ({raw})" : raw.ToString(),
            GdbFieldType.Reference or GdbFieldType.ResourceReference => multi?.NameOf(raw) is { Length: > 0 } refName ? $"→ {refName} (0x{raw:X8})" : $"→ 0x{raw:X8}",
            _ => $"0x{raw:X8}",
        };
    }
}
