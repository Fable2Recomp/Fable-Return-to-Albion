#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    public uint TargetRecordHash { get; set; }
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
    private const string GlobalsTuSubPath = "globals/globals.gdb.tu";
    private const string BabelCsvSubPath = "language/en-uk/text/book.babel.csv";

    /// <summary>
    /// The GDB database paths that the game mounts, mirroring
    /// <see cref="Refii.Level.Engine_LevelProps.SupplementalGdbPaths"/>.
    /// Each has a corresponding <c>.tu</c> patch file.
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
    /// Determines which <c>.gdb.tu</c> sub-path a property's target leaf record should be written to,
    /// based on which GDB file actually contains that record.
    /// Falls back to <c>globals/globals.gdb.tu</c> when the source file cannot be determined.
    /// </summary>
    private static string ResolveGdbTuSubPath(GdbMulti? baseMulti, uint targetHash)
    {
        baseMulti ??= Engine_GameGdb.BaseDatabase;
        if (baseMulti is not null)
        {
            int viewIndex = baseMulti.ViewIndexOf(targetHash);
            if (viewIndex >= 0 && viewIndex < KnownGdbPaths.Length)
            {
                return KnownGdbPaths[viewIndex] + ".tu";
            }
        }

        return GlobalsTuSubPath;
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

    public static bool AreValuesEqual(GdbFieldType type, string? valA, string? valB)
    {
        if (string.Equals(valA, valB, StringComparison.Ordinal))
        {
            return true;
        }
        if (valA is null || valB is null)
        {
            return false;
        }

        switch (type)
        {
            case GdbFieldType.Bool:
            {
                bool bA = valA.Equals("true", StringComparison.OrdinalIgnoreCase) || valA == "1";
                bool bB = valB.Equals("true", StringComparison.OrdinalIgnoreCase) || valB == "1";
                return bA == bB;
            }
            case GdbFieldType.Float:
            {
                if (float.TryParse(valA, NumberStyles.Float, CultureInfo.InvariantCulture, out float fA) &&
                    float.TryParse(valB, NumberStyles.Float, CultureInfo.InvariantCulture, out float fB))
                {
                    return Math.Abs(fA - fB) < 0.0001f;
                }
                break;
            }
            case GdbFieldType.Int:
            case GdbFieldType.Enum:
            {
                if (int.TryParse(valA, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iA) &&
                    int.TryParse(valB, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iB))
                {
                    return iA == iB;
                }
                break;
            }
        }

        return false;
    }

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
            uint leafHash = startCursor.View.RecordHash(startCursor.Record);
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

                    GdbFieldType effectiveType = type;
                    string currentFormatted = FormatRawValue(type, raw);
                    string vanillaFormatted = currentFormatted;

                    // Resolve the true vanilla value by walking up the prototype hierarchy in baseMulti starting from leafHash:
                    if (baseMulti.TryLookup(leafHash, out GdbCursor baseCursor) &&
                        baseMulti.TryFindInherited(baseCursor, hash, -1, out _, out uint baseRaw, out GdbFieldType baseType))
                    {
                        effectiveType = baseType;
                        vanillaFormatted = FormatRawValue(baseType, baseRaw);
                    }
                    else if (baseMulti.TryLookup(currentEntryHash, out GdbCursor declCursor) &&
                             baseMulti.TryFindInherited(declCursor, hash, -1, out _, out uint declRaw, out GdbFieldType declType))
                    {
                        effectiveType = declType;
                        vanillaFormatted = FormatRawValue(declType, declRaw);
                    }

                    currentFormatted = FormatRawValue(effectiveType, raw);
                    bool isMod = !AreValuesEqual(effectiveType, currentFormatted, vanillaFormatted);

                    props.Add(new ModItemProperty
                    {
                        PropertyName = name,
                        DisplayLabel = PrettifyPropertyName(name),
                        CategoryId = catId,
                        CategoryDisplayName = catName,
                        PropertyHash = hash,
                        TargetRecordHash = leafHash,
                        DeclaringHash = currentEntryHash,
                        Type = effectiveType,
                        VanillaValue = vanillaFormatted,
                        CurrentValue = currentFormatted,
                        IsModified = isMod,
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

    private static byte[]? LoadPackageEntry(string pkgPath, string subPath)
    {
        if (File.Exists(pkgPath) && F2ModPackage.TryOpen(pkgPath, out F2ModPackage? pkg, out _) && pkg is not null)
        {
            using (pkg)
            {
                if (pkg.TryGetEntry(subPath, out F2ModEntry? entry) && entry is not null)
                {
                    return pkg.ReadEntryBytes(entry);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Retrieves current effective values for an item, checking if any overrides exist in the active mod (.f2mod package).
    /// </summary>
    public static ItemModValues GetEffectiveValues(ItemDetail detail, string? activeMod = null)
    {
        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        string modsRoot = Engine_GameAssets.ModsRoot;
        if (string.IsNullOrEmpty(modsRoot))
        {
            modsRoot = ModWorkspace.GetDefaultModsRoot(Refii.Core.Engine_AppRoot.AssetsDirectory);
        }

        string pkgPath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);

        GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;
        var values = new ItemModValues();
        if (baseMulti is null)
        {
            return values;
        }

        // 1. Text values (start with vanilla defaults):
        ItemDetail vanillaDetail = Engine_ItemDetails.BuildOne(baseMulti, detail.RecordHash) ?? detail;

        values.VanillaDisplayName = Engine_ItemNames.VanillaDisplayNameOf(vanillaDetail);
        values.VanillaDescription = Engine_ItemNames.VanillaDescriptionOf(vanillaDetail);
        values.DisplayName = values.VanillaDisplayName;
        values.Description = values.VanillaDescription;

        // 2. Discover all genuine GDB properties from vanilla baseMulti:
        List<ModItemProperty> discovered = DiscoverProperties(vanillaDetail, baseMulti);
        values.Properties.AddRange(discovered);

        // 3. Check for active mod GDB overrides in .f2mod package (or fallback loose files):
        var tuEntryMap = new Dictionary<uint, Dictionary<uint, uint>>();

        if (File.Exists(pkgPath) && F2ModPackage.TryOpen(pkgPath, out F2ModPackage? pkg, out _) && pkg is not null)
        {
            using (pkg)
            {
                foreach (string gdbPath in KnownGdbPaths)
                {
                    string tuSub = gdbPath + ".tu";
                    if (pkg.TryGetEntry(tuSub, out F2ModEntry? entry) && entry is not null)
                    {
                        byte[] bytes = pkg.ReadEntryBytes(entry);
                        if (GdbView.TryParse(bytes, out GdbView tuView, out _))
                        {
                            for (int i = 0; i < tuView.RecordCount; i++)
                            {
                                uint entryHash = tuView.RecordHash(i);
                                if (!tuEntryMap.TryGetValue(entryHash, out var propMap))
                                {
                                    propMap = new Dictionary<uint, uint>();
                                    tuEntryMap[entryHash] = propMap;
                                }

                                foreach (var (fHash, _, fRaw) in tuView.RawFields(i))
                                {
                                    propMap[fHash] = fRaw;
                                }
                            }
                        }
                    }
                }

                // Check for active mod text overrides in book.babel.csv inside package:
                string relBabel = "language/en-uk/text/book.babel.csv";
                if (pkg.TryGetEntry(relBabel, out F2ModEntry? babelEntry) && babelEntry is not null)
                {
                    string csvText = Encoding.UTF8.GetString(pkg.ReadEntryBytes(babelEntry));
                    List<BabelOverlayRow> rows = BabelOverlay.ParseCsv(csvText, new List<string>());
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
            }
        }
        else
        {
            string filesRoot = Path.Combine(modsRoot, modName, "files");
            if (!Directory.Exists(filesRoot))
            {
                filesRoot = Path.Combine(Refii.Core.Engine_AppRoot.AssetsDirectory, "creators", modName);
            }
            if (Directory.Exists(filesRoot))
            {
                foreach (string gdbPath in KnownGdbPaths)
                {
                    string tuPath = Path.Combine(filesRoot, (gdbPath + ".tu").Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(tuPath)) continue;

                    try
                    {
                        byte[] bytes = File.ReadAllBytes(tuPath);
                        if (GdbView.TryParse(bytes, out GdbView tuView, out _))
                        {
                            for (int i = 0; i < tuView.RecordCount; i++)
                            {
                                uint entryHash = tuView.RecordHash(i);
                                if (!tuEntryMap.TryGetValue(entryHash, out var propMap))
                                {
                                    propMap = new Dictionary<uint, uint>();
                                    tuEntryMap[entryHash] = propMap;
                                }

                                foreach (var (fHash, _, fRaw) in tuView.RawFields(i))
                                {
                                    propMap[fHash] = fRaw;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        GD.PushWarning($"[mod_values] Could not read TU patch {tuPath}: {ex.Message}");
                    }
                }

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
            }
        }

        foreach (ModItemProperty prop in values.Properties)
        {
            // Reset to vanilla baseline:
            prop.CurrentValue = prop.VanillaValue;
            prop.IsModified = false;

            // Priority 1: Check TargetRecordHash in active mod's TU database (promoted leaf record):
            if (tuEntryMap.TryGetValue(prop.TargetRecordHash, out var targetProps) &&
                targetProps.TryGetValue(prop.PropertyHash, out uint rawVal))
            {
                string val = FormatRawValue(prop.Type, rawVal);
                prop.CurrentValue = val;
                prop.IsModified = !AreValuesEqual(prop.Type, val, prop.VanillaValue);
                if (prop.IsModified)
                {
                    values.HasModOverrides = true;
                }
                continue;
            }

            // Priority 2: Check DeclaringHash in active mod's TU database:
            if (tuEntryMap.TryGetValue(prop.DeclaringHash, out var declProps) &&
                declProps.TryGetValue(prop.PropertyHash, out uint declRawVal))
            {
                string val = FormatRawValue(prop.Type, declRawVal);
                prop.CurrentValue = val;
                prop.IsModified = !AreValuesEqual(prop.Type, val, prop.VanillaValue);
                if (prop.IsModified)
                {
                    values.HasModOverrides = true;
                }
                continue;
            }
        }

        return values;
    }

    /// <summary>
    /// Writes or updates property and text overlays for the specified item into the active mod (.f2mod package).
    /// </summary>
    public static void SaveItemOverrides(ItemDetail detail, ItemModValues values, string? activeMod = null)
    {
        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        string modsRoot = Engine_GameAssets.ModsRoot;
        if (string.IsNullOrEmpty(modsRoot))
        {
            modsRoot = ModWorkspace.GetDefaultModsRoot(Refii.Core.Engine_AppRoot.AssetsDirectory);
        }

        string pkgPath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
        if (!File.Exists(pkgPath))
        {
            Directory.CreateDirectory(modsRoot);
            ModWorkspace.CreateModPackage(modsRoot, modName, out _, out _);
        }

        GdbMulti? multi = Engine_GameGdb.Database;
        if (multi is null)
        {
            GD.PushWarning("[mod_values] Database not loaded; cannot resolve declaring schema slots.");
            return;
        }

        // 1. UPDATE GDB PROPERTIES VIA TITLE UPDATE (.gdb.tu)
        GdbMulti baseMulti = Engine_GameGdb.BaseDatabase ?? multi;

        var propsByTuFile = new Dictionary<string, Dictionary<uint, List<ModItemProperty>>>(StringComparer.OrdinalIgnoreCase);

        foreach (ModItemProperty prop in values.Properties)
        {
            string tuSubPath = ResolveGdbTuSubPath(baseMulti, prop.TargetRecordHash);
            if (!propsByTuFile.TryGetValue(tuSubPath, out var byRecord))
            {
                byRecord = new Dictionary<uint, List<ModItemProperty>>();
                propsByTuFile[tuSubPath] = byRecord;
            }

            if (!byRecord.TryGetValue(prop.TargetRecordHash, out var propList))
            {
                propList = new List<ModItemProperty>();
                byRecord[prop.TargetRecordHash] = propList;
            }

            propList.Add(prop);
        }

        var updates = new List<(string Path, byte[] Bytes)>();
        var deletes = new List<string>();

        foreach (var (tuSubPath, records) in propsByTuFile)
        {
            byte[]? existingBytes = LoadPackageEntry(pkgPath, tuSubPath);
            GdbPatchDatabase db = GdbPatchBuilder.LoadOrCreatePatchDatabase(existingBytes);

            foreach (var (targetHash, propList) in records)
            {
                var modifiedList = propList
                    .Where(p => !AreValuesEqual(p.Type, p.CurrentValue, p.VanillaValue))
                    .ToList();

                // Clean up stale names associated with this target record:
                db.Names.Remove(targetHash);
                foreach (var p in propList)
                {
                    db.Names.Remove(p.PropertyHash);
                }

                if (modifiedList.Count > 0)
                {
                    var modifiedFields = modifiedList
                        .Select(p => (p.PropertyHash, p.Type, p.CurrentValue));

                    GdbPatchEntry entry = GdbPatchBuilder.BuildPromotedEntry(baseMulti, targetHash, modifiedFields);
                    GdbPatchBuilder.UpsertEntry(db, entry);

                    string recName = multi.NameOf(targetHash);
                    if (string.IsNullOrWhiteSpace(recName))
                    {
                        string baseLabel = !string.IsNullOrWhiteSpace(values.DisplayName)
                            ? values.DisplayName
                            : (!string.IsNullOrWhiteSpace(detail.DisplayName) ? detail.DisplayName : detail.Label);

                        if (targetHash == detail.DefinitionHash || targetHash == detail.RecordHash)
                        {
                            recName = baseLabel;
                        }
                        else if (targetHash == detail.WeaponComponent)
                        {
                            recName = $"{baseLabel} (WeaponComponent)";
                        }
                        else if (targetHash == detail.InventoryComponentHash)
                        {
                            recName = $"{baseLabel} (InventoryItemComponent)";
                        }
                        else if (!string.IsNullOrWhiteSpace(detail.InternalName))
                        {
                            recName = detail.InternalName;
                        }
                        else if (!string.IsNullOrWhiteSpace(baseLabel))
                        {
                            recName = baseLabel;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(recName))
                    {
                        db.Names[targetHash] = recName;
                    }
                    foreach (var p in modifiedList)
                    {
                        if (!string.IsNullOrWhiteSpace(p.PropertyName))
                        {
                            db.Names[p.PropertyHash] = p.PropertyName;
                        }
                    }
                }
                else
                {
                    GdbPatchBuilder.RemoveEntry(db, targetHash);
                }
            }

            if (db.Entries.Count > 0)
            {
                updates.Add((tuSubPath, GdbWriter.Serialize(db)));
            }
            else
            {
                deletes.Add(tuSubPath);
            }
        }

        // 2. UPDATE BABEL TEXT (language/en-uk/text/book.babel.csv)
        string relBabel = "language/en-uk/text/book.babel.csv";
        byte[]? existingBabelBytes = LoadPackageEntry(pkgPath, relBabel);
        var existingBabelRows = new List<BabelOverlayRow>();
        if (existingBabelBytes != null && existingBabelBytes.Length > 0)
        {
            existingBabelRows = BabelOverlay.ParseCsv(Encoding.UTF8.GetString(existingBabelBytes), new List<string>());
        }

        string nameKey = detail.NameTagName.Length > 0
            ? detail.NameTagName
            : (detail.NameTag != 0 ? $"0x{detail.NameTag:X8}" : string.Empty);
        string descKey = detail.DescTagName.Length > 0
            ? detail.DescTagName
            : (detail.DescTag != 0 ? $"0x{detail.DescTag:X8}" : string.Empty);

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
            deletes.Add(relBabel);
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
            updates.Add((relBabel, Encoding.UTF8.GetBytes(babelSb.ToString())));
        }

        try
        {
            F2ModPackage.IncrementalSync(pkgPath, updates, deletes);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[mod_values] Failed to auto-sync .f2mod package for '{modName}': {ex.Message}");
        }

        GD.Print($"[mod_values] Saved overrides for '{detail.Label}' [0x{detail.RecordHash:X8}] to mod '{modName}' (.f2mod)");

        // 3. HOT-RELOAD IN-MEMORY OVERLAYS AND REFRESH SCENE NODES
        Engine_GameGdb.Invalidate();
        Refii.Equipment.Engine_WeaponIndex.Invalidate();
        Refii.Equipment.Engine_ItemIndex.Invalidate();
        Engine_BnkTextBankLoader.Invalidate();
        BnkMountPlugin.Instance?.ReloadModOverlays();
        RefreshSceneEquipment();
    }

    /// <summary>
    /// Removes all overrides targeting this item from the active mod's .f2mod package.
    /// </summary>
    public static void RevertItemOverrides(ItemDetail detail, string? activeMod = null)
    {
        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        string modsRoot = Engine_GameAssets.ModsRoot;
        if (string.IsNullOrEmpty(modsRoot))
        {
            modsRoot = ModWorkspace.GetDefaultModsRoot(Refii.Core.Engine_AppRoot.AssetsDirectory);
        }

        string pkgPath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
        if (!File.Exists(pkgPath))
        {
            return;
        }

        GdbMulti? multi = Engine_GameGdb.Database ?? Engine_GameGdb.BaseDatabase;
        var targetHashes = CollectTargetHashes(multi, detail);

        var updates = new List<(string Path, byte[] Bytes)>();
        var deletes = new List<string>();

        // Revert from all known .gdb.tu entries in the package:
        foreach (string gdbPath in KnownGdbPaths)
        {
            string tuSub = gdbPath + ".tu";
            byte[]? existingBytes = LoadPackageEntry(pkgPath, tuSub);
            if (existingBytes == null || existingBytes.Length == 0)
            {
                continue;
            }

            GdbPatchDatabase db = GdbPatchBuilder.LoadOrCreatePatchDatabase(existingBytes);
            bool changed = false;
            foreach (uint h in targetHashes)
            {
                if (GdbPatchBuilder.RemoveEntry(db, h))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                if (db.Entries.Count > 0)
                {
                    updates.Add((tuSub, GdbWriter.Serialize(db)));
                }
                else
                {
                    deletes.Add(tuSub);
                }
            }
        }

        // Filter book.babel.csv inside package:
        string relBabel = "language/en-uk/text/book.babel.csv";
        byte[]? existingBabelBytes = LoadPackageEntry(pkgPath, relBabel);
        if (existingBabelBytes != null && existingBabelBytes.Length > 0)
        {
            List<BabelOverlayRow> rows = BabelOverlay.ParseCsv(Encoding.UTF8.GetString(existingBabelBytes), new List<string>());
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
                deletes.Add(relBabel);
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
                updates.Add((relBabel, Encoding.UTF8.GetBytes(babelSb.ToString())));
            }
        }

        try
        {
            F2ModPackage.IncrementalSync(pkgPath, updates, deletes);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[mod_values] Failed to auto-sync .f2mod package on revert for '{modName}': {ex.Message}");
        }

        GD.Print($"[mod_values] Reverted overrides for '{detail.Label}' [0x{detail.RecordHash:X8}] in mod '{modName}' (.f2mod)");

        // Hot-reload in-memory overlays and refresh scene nodes:
        Engine_GameGdb.Invalidate();
        Refii.Equipment.Engine_WeaponIndex.Invalidate();
        Refii.Equipment.Engine_ItemIndex.Invalidate();
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
    /// Gathers all entry hashes associated with this item that could receive GDB overrides in .gdb.tu files.
    /// </summary>
    private static HashSet<uint> CollectTargetHashes(GdbMulti? multi, ItemDetail detail)
    {
        var set = new HashSet<uint>();

        void AddHash(uint h)
        {
            if (h != 0 && h != GdbHash.Null)
            {
                set.Add(h);
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
                AddHash(prop.TargetRecordHash);
                AddHash(prop.DeclaringHash);
            }
        }

        GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase;
        if (baseMulti is not null && !ReferenceEquals(baseMulti, multi))
        {
            foreach (var prop in DiscoverProperties(detail, baseMulti))
            {
                AddHash(prop.TargetRecordHash);
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
