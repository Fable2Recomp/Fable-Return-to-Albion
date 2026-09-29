#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;
using Refii.Assets;
using Refii.Core;
using Refii.Equipment;
using Refii.Gdb;
using Refii.Mods;
using Refii.Text;

namespace Refii.EditorTools;

/// <summary>
/// Represents a single editable GDB scalar property on an item, discovered dynamically from its schema
/// and associated with its owning component category.
/// </summary>
public sealed class ModItemProperty
{
    public string PropertyName { get; set; } = string.Empty;
    public string DisplayLabel { get; set; } = string.Empty;
    public string CategoryId { get; set; } = string.Empty;
    public string CategoryDisplayName { get; set; } = string.Empty;
    public uint PropertyHash { get; set; }
    public uint DeclaringHash { get; set; }
    public GdbFieldType Type { get; set; }
    public string VanillaValue { get; set; } = string.Empty;
    public string CurrentValue { get; set; } = string.Empty;
    public bool IsModified { get; set; }
}

/// <summary>
/// Data container holding effective or overridden values for an item in the mod editor.
/// </summary>
public sealed class ItemModValues
{
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string VanillaDisplayName { get; set; } = string.Empty;
    public string VanillaDescription { get; set; } = string.Empty;
    public bool HasModOverrides { get; set; }

    public List<ModItemProperty> Properties { get; } = new();

    public int BasePrice
    {
        get => GetInt("BasePrice", -1);
        set => SetProp("BasePrice", value.ToString());
    }

    public int Rating
    {
        get => GetInt("Rating", 0);
        set => SetProp("Rating", value.ToString());
    }

    public float DamageMultiplier
    {
        get => GetFloat("DamageMultiplier", 1.0f);
        set => SetProp("DamageMultiplier", value.ToString("0.#####", CultureInfo.InvariantCulture));
    }

    public float AttackSpeedMultiplier
    {
        get => GetFloat("AttackSpeedMultiplier", 1.0f);
        set => SetProp("AttackSpeedMultiplier", value.ToString("0.#####", CultureInfo.InvariantCulture));
    }

    public int AugmentSlots
    {
        get => GetInt("NumberOfSlots", 0);
        set => SetProp("NumberOfSlots", value.ToString());
    }

    public bool IsWeapon => HasProperty("DamageMultiplier") || HasProperty("AttackSpeedMultiplier") || HasProperty("WeaponType");

    public bool HasProperty(string name) =>
        Properties.Exists(p => p.PropertyName.Equals(name, StringComparison.OrdinalIgnoreCase));

    public ModItemProperty? GetProperty(string name) =>
        Properties.Find(p => p.PropertyName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private int GetInt(string name, int fallback)
    {
        var p = GetProperty(name);
        return p is not null && int.TryParse(p.CurrentValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
    }

    private float GetFloat(string name, float fallback)
    {
        var p = GetProperty(name);
        return p is not null && float.TryParse(p.CurrentValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
    }

    private void SetProp(string name, string val)
    {
        var p = GetProperty(name);
        if (p is not null)
        {
            p.CurrentValue = val;
            p.IsModified = !string.Equals(p.CurrentValue, p.VanillaValue, StringComparison.Ordinal);
        }
    }
}

/// <summary>
/// Service responsible for querying, writing, and reverting mod value overlays (.gdb.csv and .babel.csv)
/// directly from the Godot editor without requiring manual text editing.
/// </summary>
public static class EditorModValueService
{
    private const string GlobalsVirtualPath = "globals/globals.gdb";
    private const string GlobalsCsvSubPath = "globals/globals.gdb.csv";
    private const string BabelCsvSubPath = "language/en-uk/text/book.babel.csv";

    /// <summary>
    /// The GDB database paths that the game mounts, mirroring
    /// <see cref="Refii.Level.Engine_LevelProps.SupplementalGdbPaths"/>.
    /// Each has a corresponding <c>.csv</c> overlay file.
    /// </summary>
    private static readonly string[] KnownGdbPaths =
    {
        "globals/globals.gdb",
        "speechaction/speechaction.gdb",
        "interactivecutscenes/interactivecutscenes.gdb",
        "entity/entity.gdb",
        "environmentthemes/environmentthemes.gdb",
    };

    /// <summary>
    /// Determines which <c>.gdb.csv</c> sub-path a property's declaring record should be written to,
    /// based on which GDB file actually contains that record.
    /// Falls back to <c>globals/globals.gdb.csv</c> when the source file cannot be determined.
    /// </summary>
    /// <param name="multi">The merged multi-database to look up the record in.</param>
    /// <param name="declaringHash">The hash of the record that locally declares the property.</param>
    /// <returns>The relative CSV sub-path (e.g. <c>entity/entity.gdb.csv</c>).</returns>
    private static string ResolveGdbCsvSubPath(GdbMulti multi, uint declaringHash)
    {
        int viewIndex = multi.ViewIndexOf(declaringHash);
        if (viewIndex >= 0 && viewIndex < KnownGdbPaths.Length)
        {
            return KnownGdbPaths[viewIndex] + ".csv";
        }

        // Default to globals when the source file cannot be determined:
        return GlobalsCsvSubPath;
    }

    /// <summary>
    /// Resolves the absolute path to the active mod's <c>book.babel.csv</c> overlay file.
    /// Checks for both <c>language/en-uk/text/book.babel.csv</c> and <c>data/language/en-uk/text/book.babel.csv</c>,
    /// defaulting to the canonical relative path without <c>data/</c>.
    /// </summary>
    /// <param name="filesRoot">The active mod's <c>files/</c> directory root.</param>
    public static string ResolveBabelCsvPath(string filesRoot)
    {
        string primary = Path.Combine(filesRoot, BabelCsvSubPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(primary))
        {
            return primary;
        }

        string fallback = Path.Combine(filesRoot, ("data/" + BabelCsvSubPath).Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fallback))
        {
            return fallback;
        }

        return primary;
    }

    /// <summary>
    /// Checks whether a CSV row's key matches the target tag name or hash.
    /// Supports exact tag names, optional <c>TEXT_</c> prefixes, hexadecimal hashes (<c>0x...</c>),
    /// and raw FNV-1 hash comparisons.
    /// </summary>
    /// <param name="rowKey">The key authored in the CSV row.</param>
    /// <param name="tagName">The item's name/description tag string (e.g. <c>INV_ITEM_WEAPON_PISTOL_CHILD_NAME</c>).</param>
    /// <param name="tagHash">The FNV-1 hash or ID of the tag.</param>
    /// <returns>True if the row key corresponds to the given tag or hash.</returns>
    public static bool MatchesBabelKey(string rowKey, string tagName, uint tagHash)
    {
        if (string.IsNullOrWhiteSpace(rowKey))
        {
            return false;
        }

        string trimmedRow = rowKey.Trim();

        // 1. Direct string tag matching (with or without 'TEXT_' prefix):
        if (!string.IsNullOrEmpty(tagName))
        {
            if (trimmedRow.Equals(tagName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (tagName.StartsWith("TEXT_", StringComparison.OrdinalIgnoreCase) &&
                trimmedRow.Equals(tagName[5..], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!tagName.StartsWith("TEXT_", StringComparison.OrdinalIgnoreCase) &&
                trimmedRow.Equals("TEXT_" + tagName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 2. Numerical / Hash matching (0x12345678 or FNV-1 hash of row string):
        if (tagHash != 0)
        {
            if (trimmedRow.Equals($"0x{tagHash:X8}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (trimmedRow.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(trimmedRow.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed) &&
                parsed == tagHash)
            {
                return true;
            }

            if (Engine_GameHash.Fnv1(trimmedRow) == tagHash)
            {
                return true;
            }
        }

        return false;
    }

    // Standard component and property hashes:
    private static readonly uint BasePriceField = Engine_GameHash.Fnv1("BasePrice");
    private static readonly uint RatingField = Engine_GameHash.Fnv1("Rating");
    private static readonly uint DamageMultiplierField = Engine_GameHash.Fnv1("DamageMultiplier");
    private static readonly uint AttackSpeedMultiplierField = Engine_GameHash.Fnv1("AttackSpeedMultiplier");
    private static readonly uint NumberOfSlotsField = Engine_GameHash.Fnv1("NumberOfSlots");
    private static readonly uint ShopItemComponentField = Engine_ItemDetails.ShopItemComponentField;
    private static readonly uint MoneyComponent = Engine_ItemDetails.MoneyComponent;
    private static readonly uint InventoryItemComponent = Engine_ItemDetails.InventoryItemComponent;
    private static readonly uint WeaponComponent = Engine_ItemDetails.WeaponComponent;
    private static readonly uint FirearmComponentField = 0x0A644D42;
    private static readonly uint MeleeCombatComponentField = 0x6D04D9A2;
    private static readonly uint Augmentable = Engine_ItemDetails.Augmentable;
    private static readonly uint AppearanceModifierComponent = Engine_ItemDetails.AppearanceModifierComponent;

    public static string PrettifyPropertyName(string name)
    {
        if (name.Equals("BasePrice", StringComparison.OrdinalIgnoreCase)) return "Base Price (Gold)";
        if (name.Equals("NumberOfSlots", StringComparison.OrdinalIgnoreCase)) return "Augment Slots";
        if (name.Equals("Rating", StringComparison.OrdinalIgnoreCase)) return "Tier / Rating";
        if (name.Equals("DamageMultiplier", StringComparison.OrdinalIgnoreCase)) return "Damage Multiplier";
        if (name.Equals("AttackSpeedMultiplier", StringComparison.OrdinalIgnoreCase)) return "Attack Speed Multiplier";
        if (name.Equals("CombatRating", StringComparison.OrdinalIgnoreCase)) return "Combat Rating";

        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                sb.Append(' ');
            }
            sb.Append(name[i]);
        }
        return sb.ToString();
    }

    private static int GetPropertyPriority(string name) => name switch
    {
        "BasePrice" => 1,
        "Rating" => 2,
        "NumberOfSlots" => 3,
        "DamageMultiplier" => 4,
        "AttackSpeedMultiplier" => 5,
        "CombatRating" => 6,
        "WeaponType" => 7,
        "ReloadTime" => 8,
        "BulletCapacity" => 9,
        "Range" => 10,
        _ => 100,
    };

    private static string FormatRawValue(GdbFieldType type, uint raw) => type switch
    {
        GdbFieldType.Bool => raw != 0 ? "true" : "false",
        GdbFieldType.Float => BitConverter.Int32BitsToSingle(unchecked((int)raw)).ToString("0.#####", CultureInfo.InvariantCulture),
        GdbFieldType.Int => unchecked((int)raw).ToString(CultureInfo.InvariantCulture),
        GdbFieldType.Enum => raw.ToString(CultureInfo.InvariantCulture),
        _ => raw.ToString(CultureInfo.InvariantCulture),
    };

    private static readonly string[] InternalSkipNames =
    {
        "GUIScreen", "Icon", "parent", "Component", "Tag",
        "Offset", "Rotation", "Translation", "Mesh", "Model",
        "Texture", "ScriptName", "SoundEvent", "Expression",
        "Category", "InternalName",
    };

    private static bool IsEditableStatField(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Equals("BasePrice", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string skip in InternalSkipNames)
        {
            if (name.Contains(skip, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Discovers all editable scalar GDB properties across the item's definition and attached components,
    /// organizing them into clean component categories (Shop, Inventory, Combat, Augmentations, Appearance, Definition).
    /// </summary>
    public static List<ModItemProperty> DiscoverProperties(ItemDetail detail, GdbMulti multi)
    {
        var props = new List<ModItemProperty>();
        uint def = detail.DefinitionHash != 0 ? detail.DefinitionHash : detail.RecordHash;
        if (def == 0 || !multi.TryLookup(def, out GdbCursor owner))
        {
            return props;
        }

        // Component registration: (FieldHash, CategoryId, CategoryDisplayName, FallbackHash)
        var componentSpecs = new (uint FieldHash, string CatId, string CatName, uint DirectHash)[]
        {
            (ShopItemComponentField, "Shop", "Shop & Pricing", 0),
            (MoneyComponent, "Shop", "Shop & Pricing", 0),
            (InventoryItemComponent, "Inventory", "Inventory & Rules", detail.InventoryComponentHash),
            (WeaponComponent, "Combat", "Combat & Weapon", detail.WeaponComponent),
            (FirearmComponentField, "Combat", "Combat & Weapon", 0),
            (MeleeCombatComponentField, "Combat", "Combat & Weapon", 0),
            (Augmentable, "Augments", "Augmentations", 0),
            (AppearanceModifierComponent, "Appearance", "Appearance", 0),
        };

        var startingComponents = new List<(GdbCursor Cursor, string CatId, string CatName)>();
        var addedComponentHashes = new HashSet<uint>();

        foreach (var spec in componentSpecs)
        {
            uint compRef = 0;
            if (multi.TryFindInherited(owner, spec.FieldHash, 6, out _, out uint foundRef, out _) &&
                foundRef != 0 && foundRef != GdbHash.Null)
            {
                compRef = foundRef;
            }
            else if (detail.RecordHash != def &&
                     multi.TryLookup(detail.RecordHash, out GdbCursor recordCursor) &&
                     multi.TryFindInherited(recordCursor, spec.FieldHash, 6, out _, out uint recordRef, out _) &&
                     recordRef != 0 && recordRef != GdbHash.Null)
            {
                compRef = recordRef;
            }
            else if (spec.DirectHash != 0 && spec.DirectHash != GdbHash.Null)
            {
                compRef = spec.DirectHash;
            }

            if (compRef != 0 && addedComponentHashes.Add(compRef) && multi.TryLookup(compRef, out GdbCursor scoped))
            {
                startingComponents.Add((scoped, spec.CatId, spec.CatName));
            }
        }

        // Add the base item definition record itself:
        startingComponents.Add((owner, "Definition", "Item Definition"));

        GdbMulti baseMulti = Engine_GameGdb.BaseDatabase ?? multi;
        var seenPropertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (startCursor, catId, catName) in startingComponents)
        {
            GdbCursor walk = startCursor;
            var visited = new HashSet<uint>();

            for (int depth = 0; depth < 32 && walk.Valid; depth++)
            {
                uint currentEntryHash = walk.View.RecordHash(walk.Record);
                if (!visited.Add(currentEntryHash))
                {
                    break;
                }

                foreach ((uint hash, GdbFieldType type, uint raw) in walk.View.RawFields(walk.Record))
                {
                    if (hash == GdbView.HashParent ||
                        type is not (GdbFieldType.Bool or GdbFieldType.Int or GdbFieldType.Float or GdbFieldType.Enum))
                    {
                        continue;
                    }

                    string name = multi.NameOf(hash);
                    if (!IsEditableStatField(name))
                    {
                        continue;
                    }

                    if (!seenPropertyNames.Add(name))
                    {
                        continue;
                    }

                    string currentFormatted = FormatRawValue(type, raw);
                    string vanillaFormatted = currentFormatted;

                    if (baseMulti.TryLookup(currentEntryHash, out GdbCursor baseCursor) &&
                        baseCursor.View.TryFindLocalRaw(baseCursor.Record, hash, -1, out uint baseRaw, out GdbFieldType baseType))
                    {
                        vanillaFormatted = FormatRawValue(baseType, baseRaw);
                    }

                    props.Add(new ModItemProperty
                    {
                        PropertyName = name,
                        DisplayLabel = PrettifyPropertyName(name),
                        CategoryId = catId,
                        CategoryDisplayName = catName,
                        PropertyHash = hash,
                        DeclaringHash = currentEntryHash,
                        Type = type,
                        VanillaValue = vanillaFormatted,
                        CurrentValue = currentFormatted,
                        IsModified = false,
                    });
                }

                if (!walk.View.TryFindLocalRaw(walk.Record, GdbView.HashParent, 6, out uint parent, out _) ||
                    parent == 0 || parent == GdbHash.Null || !multi.TryLookup(parent, out walk))
                {
                    break;
                }
            }
        }

        props.Sort((a, b) =>
        {
            int pa = GetPropertyPriority(a.PropertyName);
            int pb = GetPropertyPriority(b.PropertyName);
            if (pa != pb) return pa.CompareTo(pb);
            return string.Compare(a.DisplayLabel, b.DisplayLabel, StringComparison.OrdinalIgnoreCase);
        });

        return props;
    }

    /// <summary>
    /// Retrieves true unpatched vanilla defaults for an item directly from the base game database.
    /// </summary>
    public static ItemModValues GetVanillaValues(ItemDetail detail)
    {
        GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;
        var values = new ItemModValues();
        if (baseMulti is null)
        {
            return values;
        }

        ItemDetail vanillaDetail = Engine_ItemDetails.BuildOne(baseMulti, detail.RecordHash) ?? detail;
        values.VanillaDisplayName = Engine_ItemNames.VanillaDisplayNameOf(vanillaDetail);
        values.VanillaDescription = Engine_ItemNames.VanillaDescriptionOf(vanillaDetail);
        values.DisplayName = values.VanillaDisplayName;
        values.Description = values.VanillaDescription;

        List<ModItemProperty> discovered = DiscoverProperties(detail, baseMulti);
        foreach (var p in discovered)
        {
            p.CurrentValue = p.VanillaValue;
            p.IsModified = false;
        }
        values.Properties.AddRange(discovered);
        values.HasModOverrides = false;
        return values;
    }

    /// <summary>
    /// Retrieves current effective values for an item, checking if any overrides exist in the active mod.
    /// </summary>
    public static ItemModValues GetEffectiveValues(ItemDetail detail, string? activeMod = null)
    {
        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        string filesRoot = Path.Combine(Engine_GameAssets.ModsRoot, modName, "files");

        GdbMulti? multi = Engine_GameGdb.Database ?? Engine_GameGdb.BaseDatabase;
        var values = new ItemModValues();
        if (multi is null)
        {
            return values;
        }

        // 1. Text values:
        ItemDetail vanillaDetail = Engine_GameGdb.BaseDatabase is not null
            ? Engine_ItemDetails.BuildOne(Engine_GameGdb.BaseDatabase, detail.RecordHash) ?? detail
            : detail;

        values.VanillaDisplayName = Engine_ItemNames.VanillaDisplayNameOf(vanillaDetail);
        values.VanillaDescription = Engine_ItemNames.VanillaDescriptionOf(vanillaDetail);
        values.DisplayName = Engine_ItemNames.DisplayNameOf(detail);
        values.Description = Engine_ItemNames.DescriptionOf(detail);

        // 2. Discover all genuine GDB properties:
        List<ModItemProperty> discovered = DiscoverProperties(detail, multi);
        values.Properties.AddRange(discovered);

        // 3. Check for active mod GDB overrides across ALL known .gdb.csv files:
        //    Properties may be spread across globals.gdb, entity.gdb, etc.
        var rowMap = new Dictionary<(string Entry, string Property), string>();
        foreach (string gdbPath in KnownGdbPaths)
        {
            string csvPath = Path.Combine(filesRoot, (gdbPath + ".csv").Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(csvPath))
            {
                continue;
            }

            List<GdbOverlayRow> rows = GdbOverlay.ParseCsv(File.ReadAllText(csvPath), new List<string>());
            foreach (GdbOverlayRow r in rows)
            {
                rowMap[(r.Entry.Trim(), r.Property.Trim())] = r.Value.Trim();
            }
        }

        foreach (ModItemProperty prop in values.Properties)
        {
            string hexUpper = $"0x{prop.DeclaringHash:X8}";
            string hexLower = $"0x{prop.DeclaringHash:x8}";

            if (rowMap.TryGetValue((hexUpper, prop.PropertyName), out string? val) ||
                rowMap.TryGetValue((hexLower, prop.PropertyName), out val))
            {
                prop.CurrentValue = val;
                prop.IsModified = !string.Equals(val, prop.VanillaValue, StringComparison.Ordinal);
                if (prop.IsModified)
                {
                    values.HasModOverrides = true;
                }
            }
        }

        // 4. Check for active mod text overrides in book.babel.csv:
        string babelCsvPath = ResolveBabelCsvPath(filesRoot);
        if (File.Exists(babelCsvPath))
        {
            List<BabelOverlayRow> rows = BabelOverlay.ParseCsv(File.ReadAllText(babelCsvPath), new List<string>());
            foreach (BabelOverlayRow row in rows)
            {
                if (MatchesBabelKey(row.Key, detail.NameTagName, detail.NameTag))
                {
                    values.DisplayName = row.Value;
                    values.HasModOverrides = true;
                }
                else if (MatchesBabelKey(row.Key, detail.DescTagName, detail.DescTag))
                {
                    values.Description = row.Value;
                    values.HasModOverrides = true;
                }
            }
        }

        return values;
    }

    /// <summary>
    /// Writes or updates property and text overlays for the specified item into the active mod.
    /// </summary>
    public static void SaveItemOverrides(ItemDetail detail, ItemModValues values, string? activeMod = null)
    {
        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        string filesRoot = EditorModExporter.EnsureActiveModWorkspace();

        GdbMulti? multi = Engine_GameGdb.Database;
        if (multi is null)
        {
            GD.PushWarning("[mod_values] Database not loaded; cannot resolve declaring schema slots.");
            return;
        }

        // 1. UPDATE GDB PROPERTIES — routed to the correct per-database .gdb.csv file.
        //    Properties are grouped by the GDB file that owns their declaring record, so
        //    weapon stats in entity.gdb go to entity/entity.gdb.csv, pricing in globals.gdb
        //    goes to globals/globals.gdb.csv, etc.
        var propsByFile = new Dictionary<string, List<ModItemProperty>>(StringComparer.OrdinalIgnoreCase);

        foreach (ModItemProperty prop in values.Properties)
        {
            string csvSubPath = ResolveGdbCsvSubPath(multi, prop.DeclaringHash);
            if (!propsByFile.TryGetValue(csvSubPath, out var list))
            {
                list = new List<ModItemProperty>();
                propsByFile[csvSubPath] = list;
            }

            list.Add(prop);
        }

        // Process each target GDB CSV file independently:
        foreach (var (csvSubPath, props) in propsByFile)
        {
            string gdbCsvPath = Path.Combine(filesRoot, csvSubPath.Replace('/', Path.DirectorySeparatorChar));
            string? gdbDir = Path.GetDirectoryName(gdbCsvPath);
            if (!string.IsNullOrEmpty(gdbDir))
            {
                Directory.CreateDirectory(gdbDir);
            }

            var existingGdbRows = new List<GdbOverlayRow>();
            if (File.Exists(gdbCsvPath))
            {
                existingGdbRows = GdbOverlay.ParseCsv(File.ReadAllText(gdbCsvPath), new List<string>());
            }

            var updates = new Dictionary<(string Entry, string Property), string>();
            var removals = new HashSet<(string Entry, string Property)>();

            foreach (ModItemProperty prop in props)
            {
                string hex = $"0x{prop.DeclaringHash:X8}";
                var key = (hex, prop.PropertyName);

                if (!string.Equals(prop.CurrentValue, prop.VanillaValue, StringComparison.Ordinal))
                {
                    updates[key] = prop.CurrentValue;
                }
                else
                {
                    removals.Add(key);
                }
            }

            var mergedGdbRows = new List<(string Entry, string Property, string Value)>();
            var updatedKeys = new HashSet<(string Entry, string Property)>();

            foreach (GdbOverlayRow row in existingGdbRows)
            {
                var key = (row.Entry, row.Property);
                if (removals.Contains(key))
                {
                    continue;
                }

                if (updates.TryGetValue(key, out string? newVal))
                {
                    mergedGdbRows.Add((row.Entry, row.Property, newVal));
                    updatedKeys.Add(key);
                }
                else
                {
                    mergedGdbRows.Add((row.Entry, row.Property, row.Value));
                }
            }

            foreach (var kvp in updates)
            {
                if (!updatedKeys.Contains(kvp.Key))
                {
                    mergedGdbRows.Add((kvp.Key.Entry, kvp.Key.Property, kvp.Value));
                }
            }

            if (mergedGdbRows.Count == 0)
            {
                if (File.Exists(gdbCsvPath))
                {
                    try { File.Delete(gdbCsvPath); } catch { }
                }
            }
            else
            {
                var gdbSb = new StringBuilder();
                gdbSb.AppendLine("# Refii GDB Property Overlays");
                gdbSb.AppendLine("# entry,property,value");
                foreach (var r in mergedGdbRows)
                {
                    gdbSb.Append(r.Entry).Append(',').Append(r.Property).Append(',').AppendLine(EscapeCsv(r.Value));
                }

                File.WriteAllText(gdbCsvPath, gdbSb.ToString(), Encoding.UTF8);
            }

            GD.Print($"[mod_values] Wrote {mergedGdbRows.Count} row(s) to {csvSubPath}");
        }

        // 2. UPDATE BABEL TEXT (language/en-uk/text/book.babel.csv)
        string babelCsvPath = ResolveBabelCsvPath(filesRoot);
        string? babelDir = Path.GetDirectoryName(babelCsvPath);
        if (!string.IsNullOrEmpty(babelDir))
        {
            Directory.CreateDirectory(babelDir);
        }

        var existingBabelRows = new List<BabelOverlayRow>();
        if (File.Exists(babelCsvPath))
        {
            existingBabelRows = BabelOverlay.ParseCsv(File.ReadAllText(babelCsvPath), new List<string>());
        }

        string nameKey = detail.NameTagName.Length > 0
            ? detail.NameTagName
            : (detail.NameTag != 0 ? $"0x{detail.NameTag:X8}" : string.Empty);
        string descKey = detail.DescTagName.Length > 0
            ? detail.DescTagName
            : (detail.DescTag != 0 ? $"0x{detail.DescTag:X8}" : string.Empty);

        // Diagnostic: log the babel keys being used so key mismatches are visible:
        GD.Print($"[mod_values] Babel keys — name: '{nameKey}' (tag=0x{detail.NameTag:X8}, tagName='{detail.NameTagName}'), " +
                 $"desc: '{descKey}' (tag=0x{detail.DescTag:X8}, tagName='{detail.DescTagName}')");

        string? existingNameVal = null;
        string? existingDescVal = null;
        var mergedBabelRows = new List<(string Key, string Value)>();
        foreach (BabelOverlayRow row in existingBabelRows)
        {
            if (nameKey.Length > 0 && MatchesBabelKey(row.Key, detail.NameTagName, detail.NameTag))
            {
                existingNameVal = row.Value;
                continue;
            }

            if (descKey.Length > 0 && MatchesBabelKey(row.Key, detail.DescTagName, detail.DescTag))
            {
                existingDescVal = row.Value;
                continue;
            }

            mergedBabelRows.Add((row.Key, row.Value));
        }

        bool shouldSaveName = nameKey.Length > 0 && !string.IsNullOrWhiteSpace(values.DisplayName) &&
            (values.DisplayName != values.VanillaDisplayName || existingNameVal != null);
        if (shouldSaveName)
        {
            mergedBabelRows.Add((nameKey, values.DisplayName.Trim()));
        }

        bool shouldSaveDesc = descKey.Length > 0 && !string.IsNullOrWhiteSpace(values.Description) &&
            (values.Description != values.VanillaDescription || existingDescVal != null);
        if (shouldSaveDesc)
        {
            mergedBabelRows.Add((descKey, values.Description.Trim()));
        }

        if (mergedBabelRows.Count == 0)
        {
            if (File.Exists(babelCsvPath))
            {
                try { File.Delete(babelCsvPath); } catch { }
            }
        }
        else
        {
            var babelSb = new StringBuilder();
            babelSb.AppendLine("# Refii Babel Text Overlays");
            babelSb.AppendLine("# tag,text");
            foreach (var r in mergedBabelRows)
            {
                babelSb.Append(r.Key).Append(',').AppendLine(EscapeCsv(r.Value));
            }

            File.WriteAllText(babelCsvPath, babelSb.ToString(), Encoding.UTF8);
        }

        GD.Print($"[mod_values] Saved overrides for '{detail.Label}' [0x{detail.RecordHash:X8}] to mod '{modName}'");

        // 3. HOT-RELOAD IN-MEMORY OVERLAYS AND REFRESH SCENE NODES
        Engine_BnkTextBankLoader.Invalidate();
        BnkMountPlugin.Instance?.ReloadModOverlays();
        RefreshSceneEquipment();
    }

    /// <summary>
    /// Removes all overrides targeting this item from the active mod's overlay CSV files.
    /// </summary>
    public static void RevertItemOverrides(ItemDetail detail, string? activeMod = null)
    {
        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        string filesRoot = Path.Combine(Engine_GameAssets.ModsRoot, modName, "files");

        GdbMulti? multi = Engine_GameGdb.Database ?? Engine_GameGdb.BaseDatabase;
        var targets = CollectTargetEntries(multi, detail);

        // Filter ALL known .gdb.csv files — properties may be spread across multiple databases:
        foreach (string gdbPath in KnownGdbPaths)
        {
            string gdbCsvPath = Path.Combine(filesRoot, (gdbPath + ".csv").Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(gdbCsvPath))
            {
                continue;
            }

            List<GdbOverlayRow> rows = GdbOverlay.ParseCsv(File.ReadAllText(gdbCsvPath), new List<string>());
            var remaining = new List<GdbOverlayRow>();
            foreach (GdbOverlayRow row in rows)
            {
                if (!targets.Contains(row.Entry.Trim()))
                {
                    remaining.Add(row);
                }
            }

            if (remaining.Count == 0)
            {
                try
                {
                    File.Delete(gdbCsvPath);
                }
                catch (Exception ex)
                {
                    GD.PushWarning($"[mod_values] Could not delete empty {gdbPath}.csv: {ex.Message}");
                }
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Refii GDB Property Overlays");
                sb.AppendLine("# entry,property,value");
                foreach (GdbOverlayRow r in remaining)
                {
                    sb.Append(r.Entry).Append(',').Append(r.Property).Append(',').AppendLine(EscapeCsv(r.Value));
                }

                File.WriteAllText(gdbCsvPath, sb.ToString(), Encoding.UTF8);
            }
        }

        // Filter book.babel.csv:
        string babelCsvPath = ResolveBabelCsvPath(filesRoot);
        if (File.Exists(babelCsvPath))
        {
            List<BabelOverlayRow> rows = BabelOverlay.ParseCsv(File.ReadAllText(babelCsvPath), new List<string>());
            var remaining = new List<BabelOverlayRow>();
            foreach (BabelOverlayRow row in rows)
            {
                if (!MatchesBabelKey(row.Key, detail.NameTagName, detail.NameTag) &&
                    !MatchesBabelKey(row.Key, detail.DescTagName, detail.DescTag))
                {
                    remaining.Add(row);
                }
            }

            if (remaining.Count == 0)
            {
                try
                {
                    File.Delete(babelCsvPath);
                }
                catch (Exception ex)
                {
                    GD.PushWarning($"[mod_values] Could not delete empty book.babel.csv: {ex.Message}");
                }
            }
            else
            {
                var babelSb = new StringBuilder();
                babelSb.AppendLine("# Refii Babel Text Overlays");
                babelSb.AppendLine("# tag,text");
                foreach (BabelOverlayRow r in remaining)
                {
                    babelSb.Append(r.Key).Append(',').AppendLine(EscapeCsv(r.Value));
                }

                File.WriteAllText(babelCsvPath, babelSb.ToString(), Encoding.UTF8);
            }
        }

        GD.Print($"[mod_values] Reverted overrides for '{detail.Label}' [0x{detail.RecordHash:X8}] in mod '{modName}'");

        // Hot-reload in-memory overlays and refresh scene nodes:
        Engine_BnkTextBankLoader.Invalidate();
        BnkMountPlugin.Instance?.ReloadModOverlays();
        RefreshSceneEquipment();
    }

    /// <summary>
    /// Checks whether the item associated with the given record hash or detail currently has any mod overrides in the active mod.
    /// </summary>
    public static bool HasModOverridesFor(ItemDetail detail, string? activeMod = null)
    {
        ItemModValues values = GetEffectiveValues(detail, activeMod);
        return values.HasModOverrides;
    }

    /// <summary>
    /// Checks whether the item associated with the given record hash has any mod overrides in the active mod.
    /// </summary>
    public static bool HasModOverridesForHash(uint hash, string? activeMod = null)
    {
        if (hash == 0) return false;
        GdbMulti? multi = Engine_GameGdb.Database ?? Engine_GameGdb.BaseDatabase;
        if (multi is null) return false;

        ItemDetail? detail = Engine_ItemDetails.BuildOne(multi, hash);
        return detail is not null && HasModOverridesFor(detail, activeMod);
    }

    /// <summary>
    /// Refreshes all active Engine_EquipmentInstance nodes in the currently edited scene in the editor.
    /// </summary>
    public static void RefreshSceneEquipment()
    {
        try
        {
            Node? root = EditorInterface.Singleton?.GetEditedSceneRoot();
            if (root is null || !GodotObject.IsInstanceValid(root))
            {
                return;
            }

            RefreshEquipmentRecursive(root);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[mod_values] Could not refresh scene equipment: {ex.Message}");
        }
    }

    private static void RefreshEquipmentRecursive(Node node)
    {
        if (node is Engine_EquipmentInstance equip && GodotObject.IsInstanceValid(equip))
        {
            equip.RefreshDetails();
        }

        foreach (Node child in node.GetChildren())
        {
            RefreshEquipmentRecursive(child);
        }
    }

    /// <summary>
    /// Gathers all entry identifiers (record hexes) associated with this item that could receive GDB overrides.
    /// </summary>
    private static HashSet<string> CollectTargetEntries(GdbMulti? multi, ItemDetail detail)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddHash(uint h)
        {
            if (h != 0 && h != GdbHash.Null)
            {
                set.Add($"0x{h:X8}");
                set.Add($"0x{h:x8}");
            }
        }

        AddHash(detail.RecordHash);
        AddHash(detail.DefinitionHash);
        AddHash(detail.InventoryComponentHash);
        AddHash(detail.WeaponComponent);

        if (multi is not null)
        {
            foreach (var prop in DiscoverProperties(detail, multi))
            {
                AddHash(prop.DeclaringHash);
            }
        }

        GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase;
        if (baseMulti is not null && !ReferenceEquals(baseMulti, multi))
        {
            foreach (var prop in DiscoverProperties(detail, baseMulti))
            {
                AddHash(prop.DeclaringHash);
            }
        }

        return set;
    }

    /// <summary>
    /// Walks up the parent inheritance chain from <paramref name="startHash"/> until an entry that locally
    /// declares <paramref name="fieldHash"/> in its schema is found.
    /// </summary>
    public static bool TryFindDeclaringEntry(
        GdbMulti multi, uint startHash, uint fieldHash, out uint declaringHash, out GdbFieldType type)
    {
        declaringHash = 0;
        type = default;

        if (startHash == 0 || startHash == GdbHash.Null || !multi.TryLookup(startHash, out GdbCursor cursor))
        {
            return false;
        }

        var visited = new HashSet<uint>();
        GdbCursor walk = cursor;

        while (true)
        {
            uint currentHash = walk.View.RecordHash(walk.Record);
            if (!visited.Add(currentHash))
            {
                break;
            }

            if (walk.View.TryFindValueSlot(walk.Record, fieldHash, -1, out _, out type))
            {
                declaringHash = currentHash;
                return true;
            }

            if (!walk.View.TryFindLocalRaw(walk.Record, GdbView.HashParent, 6, out uint parent, out _) ||
                parent == 0 || parent == GdbHash.Null ||
                !multi.TryLookup(parent, out GdbCursor next))
            {
                break;
            }

            walk = next;
        }

        return false;
    }

    private static string EscapeCsv(string text)
    {
        if (text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
        {
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        return text;
    }
}
#endif
