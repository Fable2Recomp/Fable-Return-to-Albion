#if TOOLS
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Godot;
using Refii.Assets;
using Refii.Bnk;
using Refii.Core;
using Refii.Equipment;
using Refii.Gdb;
using Refii.Level;
using Refii.Models;
using Refii.Mods;
using Refii.Text;
using Refii.Textures;

namespace Refii.EditorTools;

/// <summary>
/// Classifies whether a GDB component field references an external VFS asset (.mdl, .tex, .skl, .phy).
/// </summary>
public enum AssetFieldKind
{
    None = 0,
    Model = 1,
    Texture = 2,
    Skeleton = 3,
    Physics = 4,
}

/// <summary>
/// Represents an inspectable and overridable surface texture slot inside a 3D model's (.mdl) material table.
/// </summary>
public sealed class ModModelMaterialSlot
{
    public int FlatMaterialIndex { get; set; }
    public string MeshName { get; set; } = string.Empty;
    public int SubMaterialIndex { get; set; }
    public int SlotIndex { get; set; }
    public string SlotName { get; set; } = string.Empty;
    public string CurrentTexturePath { get; set; } = string.Empty;
    public string VanillaTexturePath { get; set; } = string.Empty;

    public bool IsModified => !string.Equals(
        (CurrentTexturePath ?? string.Empty).Trim().Replace('/', '\\'),
        (VanillaTexturePath ?? string.Empty).Trim().Replace('/', '\\'),
        StringComparison.OrdinalIgnoreCase);
}

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
    public bool IsLocalizedTextTag { get; set; }
    public string BabelTagKey { get; set; } = string.Empty;
    public bool IsDefinedOnComponent { get; set; }
    public bool IsWalkedUpFromParent { get; set; }
    public string ParentComponentValue { get; set; } = string.Empty;
    public AssetFieldKind AssetKind { get; set; }
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
public static partial class EditorModValueService
{
    private const string GlobalsVirtualPath = "globals/globals.gdb";
    private const string GlobalsTuSubPath = "globals/globals.gdb.tu";
    private const string BabelCsvSubPath = "language/en-uk/text/book.babel.csv";

    /// <summary>
    /// Optional override for the mods root directory (used by isolated unit tests).
    /// </summary>
    public static string? ModsRootOverride { get; set; }

    private static string ResolveModsRoot()
    {
        if (!string.IsNullOrEmpty(ModsRootOverride))
        {
            return ModsRootOverride;
        }

        string modsRoot = Engine_GameAssets.ModsRoot;
        if (string.IsNullOrEmpty(modsRoot))
        {
            modsRoot = ModWorkspace.GetDefaultModsRoot(Refii.Core.Engine_AppRoot.AssetsDirectory);
        }
        return modsRoot;
    }

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
    /// based on existing <c>.gdb.tu</c> entries in the active mod package, which base GDB file contains
    /// the record or its parent chain, or an owning node fallback.
    /// Falls back to <c>globals/globals.gdb.tu</c> when the source file cannot be determined.
    /// </summary>
    private static string ResolveGdbTuSubPath(
        GdbMulti? baseMulti,
        uint targetHash,
        string? pkgPath = null,
        GdbMulti? activeMulti = null,
        uint fallbackNodeHash = 0)
    {
        baseMulti ??= GetEffectiveBaseDatabase();

        if (targetHash != 0 && baseMulti is not null)
        {
            int viewIndex = baseMulti.ViewIndexOf(targetHash);
            if (viewIndex >= 0 && viewIndex < KnownGdbPaths.Length)
            {
                return KnownGdbPaths[viewIndex] + ".tu";
            }
            if (viewIndex >= KnownGdbPaths.Length && TryResolveLevelTuSubPath(out string levelTuSub))
            {
                return levelTuSub;
            }
        }

        // 1. Walk up parent chain in activeMulti until we hit a record in baseMulti:
        if (targetHash != 0 && activeMulti is not null && baseMulti is not null &&
            activeMulti.TryLookup(targetHash, out GdbCursor walkCursor))
        {
            var visited = new HashSet<uint>();
            while (walkCursor.Valid && visited.Add(walkCursor.View.RecordHash(walkCursor.Record)))
            {
                uint curHash = walkCursor.View.RecordHash(walkCursor.Record);
                int viewIdx = baseMulti.ViewIndexOf(curHash);
                if (viewIdx >= 0 && viewIdx < KnownGdbPaths.Length)
                {
                    return KnownGdbPaths[viewIdx] + ".tu";
                }

                if (!walkCursor.View.TryFindLocalRaw(walkCursor.Record, GdbView.HashParent, 6, out uint parentHash, out _) ||
                    parentHash == 0 || parentHash == GdbHash.Null ||
                    !activeMulti.TryLookup(parentHash, out walkCursor))
                {
                    break;
                }
            }
        }

        // 2. Check if targetHash (or a node referencing it) already lives in one of the active mod's .gdb.tu files
        //    (scan in reverse order so higher-priority views like entity/entity.gdb.tu win over globals/globals.gdb.tu):
        if (targetHash != 0 && !string.IsNullOrWhiteSpace(pkgPath) && File.Exists(pkgPath) &&
            F2ModPackage.TryOpen(pkgPath, out F2ModPackage? pkg, out _) && pkg is not null)
        {
            using (pkg)
            {
                for (int k = KnownGdbPaths.Length - 1; k >= 0; k--)
                {
                    string tuSub = KnownGdbPaths[k] + ".tu";
                    if (pkg.TryGetEntry(tuSub, out F2ModEntry? entry) && entry is not null)
                    {
                        byte[] bytes = pkg.ReadEntryBytes(entry);
                        if (GdbView.TryParse(bytes, out GdbView tuView, out _))
                        {
                            if (tuView.TryLookup(targetHash, out _))
                            {
                                return tuSub;
                            }

                            for (int i = 0; i < tuView.RecordCount; i++)
                            {
                                foreach (var (_, fType, fRaw) in tuView.RawFields(i))
                                {
                                    if (fType == GdbFieldType.Reference && fRaw == targetHash)
                                    {
                                        return tuSub;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        if (fallbackNodeHash != 0 && fallbackNodeHash != targetHash)
        {
            return ResolveGdbTuSubPath(baseMulti, fallbackNodeHash, pkgPath, activeMulti, 0);
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

    private const uint EmptyFnv1Hash = 0x811C9DC5;

    public static string PrettifyPropertyName(string name)
    {
        if (name.Equals("NameTag", StringComparison.OrdinalIgnoreCase)) return "Display Name (NameTag)";
        if (name.Equals("DescriptionTag", StringComparison.OrdinalIgnoreCase)) return "Description (DescriptionTag)";
        if (name.Equals("TextTag", StringComparison.OrdinalIgnoreCase)) return "Localized Text (TextTag)";
        if (name.Equals("BuildingName", StringComparison.OrdinalIgnoreCase)) return "Building Name";
        if (name.Equals("BuildingAnecdotes", StringComparison.OrdinalIgnoreCase)) return "Building Description";
        if (name.Equals("IconGraphic", StringComparison.OrdinalIgnoreCase)) return "Icon Texture Path";
        if (name.Equals("ModelFile", StringComparison.OrdinalIgnoreCase)) return "3D Model Path";
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
        "NameTag" => -3,
        "BuildingName" => -3,
        "DescriptionTag" => -2,
        "BuildingAnecdotes" => -2,
        "TextTag" => -2,
        "IconGraphic" => -1,
        "ModelFile" => 0,
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

    /// <summary>
    /// Synchronously builds an isolated <see cref="GdbMulti"/> that layers the active mod's <c>.gdb.tu</c>
    /// patches from <c>assets/mods/&lt;activeMod&gt;.f2mod</c> directly on top of <see cref="Engine_GameGdb.BaseDatabase"/>,
    /// and extracts its <c>book.babel.csv</c> text overrides and custom/modified record hashes.
    /// Guarantees immediate UI consistency without waiting for background VFS remount tasks.
    /// </summary>
    public static GdbMulti? BuildActiveModGdbMulti(
        string? activeMod,
        out Dictionary<string, string> activeBabelOverrides,
        out HashSet<uint> customRecordHashes,
        out HashSet<uint> modifiedRecordHashes,
        GdbMulti? fallbackBaseMulti = null)
    {
        return BuildActiveModGdbMulti(
            activeMod,
            out activeBabelOverrides,
            out customRecordHashes,
            out modifiedRecordHashes,
            out _,
            fallbackBaseMulti);
    }

    /// <summary>
    /// Synchronously builds an isolated <see cref="GdbMulti"/> for <paramref name="activeMod"/> and outputs
    /// the sparse per-record property map (<paramref name="activeTuEntryMap"/>) containing only genuinely
    /// custom or modified fields relative to <paramref name="fallbackBaseMulti"/>.
    /// </summary>
    public static GdbMulti? BuildActiveModGdbMulti(
        string? activeMod,
        out Dictionary<string, string> activeBabelOverrides,
        out HashSet<uint> customRecordHashes,
        out HashSet<uint> modifiedRecordHashes,
        out Dictionary<uint, Dictionary<uint, uint>> activeTuEntryMap,
        GdbMulti? fallbackBaseMulti = null)
    {
        activeBabelOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        customRecordHashes = new HashSet<uint>();
        modifiedRecordHashes = new HashSet<uint>();
        activeTuEntryMap = new Dictionary<uint, Dictionary<uint, uint>>();

        GdbMulti? baseMulti = GetEffectiveBaseDatabase(fallbackBaseMulti);
        if (baseMulti is null)
        {
            return null;
        }

        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(modName))
        {
            return baseMulti;
        }

        string modsRoot = ResolveModsRoot();

        string pkgPath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
        var overlayBlobs = new List<byte[]?>();

        if (File.Exists(pkgPath) && F2ModPackage.TryOpen(pkgPath, out F2ModPackage? pkg, out _) && pkg is not null)
        {
            using (pkg)
            {
                foreach (string gdbPath in EnumerateGdbPathsWithActiveLevel())
                {
                    string tuSub = gdbPath + ".tu";
                    if (pkg.TryGetEntry(tuSub, out F2ModEntry? entry) && entry is not null)
                    {
                        byte[] rawTu = pkg.ReadEntryBytes(entry);
                        if (rawTu.Length > 0)
                        {
                            GdbPatchDatabase patchDb = GdbPatchBuilder.LoadOrCreatePatchDatabase(rawTu);
                            foreach (GdbPatchEntry pe in patchDb.Entries)
                            {
                                bool isBaseRec = baseMulti.TryLookup(pe.Id, out GdbCursor basePeCursor);
                                if (!isBaseRec)
                                {
                                    customRecordHashes.Add(pe.Id);
                                    modifiedRecordHashes.Add(pe.Id);
                                    if (!activeTuEntryMap.TryGetValue(pe.Id, out var customProps))
                                    {
                                        customProps = new Dictionary<uint, uint>();
                                        activeTuEntryMap[pe.Id] = customProps;
                                    }
                                    foreach (GdbPatchProperty p in pe.Properties)
                                    {
                                        customProps[p.NameHash] = p.Value;
                                    }
                                }
                                else
                                {
                                    foreach (GdbPatchProperty p in pe.Properties)
                                    {
                                        if (p.NameHash == GdbView.HashParent)
                                        {
                                            if (basePeCursor.View.TryFindLocalRaw(basePeCursor.Record, GdbView.HashParent, 6, out uint bParent, out _) &&
                                                bParent == p.Value)
                                            {
                                                continue;
                                            }
                                        }
                                        else if (basePeCursor.View.TryFindLocalRaw(basePeCursor.Record, p.NameHash, -1, out uint bLocalRaw, out _))
                                        {
                                            if (bLocalRaw == p.Value)
                                            {
                                                continue;
                                            }
                                        }
                                        else if (baseMulti.TryFindInherited(basePeCursor, p.NameHash, -1, out _, out uint bInhRaw, out _))
                                        {
                                            if (bInhRaw == p.Value)
                                            {
                                                continue;
                                            }
                                        }

                                        if (!activeTuEntryMap.TryGetValue(pe.Id, out var modProps))
                                        {
                                            modProps = new Dictionary<uint, uint>();
                                            activeTuEntryMap[pe.Id] = modProps;
                                        }
                                        modProps[p.NameHash] = p.Value;
                                        modifiedRecordHashes.Add(pe.Id);
                                    }
                                }
                            }

                            GdbPatchDatabase completeDb = GdbPatchBuilder.SynthesizeCompleteDatabase(patchDb, baseMulti);
                            overlayBlobs.Add(GdbWriter.Serialize(completeDb));
                        }
                    }
                }

                if (pkg.TryGetEntry(BabelCsvSubPath, out F2ModEntry? babelEntry) && babelEntry is not null)
                {
                    string csvText = Encoding.UTF8.GetString(pkg.ReadEntryBytes(babelEntry));
                    foreach (BabelOverlayRow row in BabelOverlay.ParseCsv(csvText, new List<string>()))
                    {
                        if (!string.IsNullOrWhiteSpace(row.Key))
                        {
                            string k = row.Key.Trim();
                            activeBabelOverrides[k] = row.Value;
                            uint kh = Engine_GameHash.Fnv1(k);
                            activeBabelOverrides[$"0x{kh:X8}"] = row.Value;
                        }
                    }
                }
            }
        }

        if (overlayBlobs.Count == 0)
        {
            return baseMulti;
        }

        return GdbMulti.BuildWithOverlays(baseMulti, overlayBlobs);
    }

    public static GdbMulti BuildActiveModGdbMulti(GdbMulti baseMulti, string? activeMod = null)
    {
        return BuildActiveModGdbMulti(activeMod, out _, out _, out _, out _, baseMulti) ?? baseMulti;
    }

    public static Dictionary<string, string> LoadActiveModBabelOverrides(string? activeMod = null)
    {
        BuildActiveModGdbMulti(activeMod, out Dictionary<string, string> activeBabelOverrides, out _, out _, out _);
        return activeBabelOverrides;
    }

    public static Dictionary<uint, Dictionary<uint, uint>> GetActiveModTuEntryMap(string? activeMod = null, GdbMulti? fallbackBaseMulti = null)
    {
        BuildActiveModGdbMulti(activeMod, out _, out _, out _, out Dictionary<uint, Dictionary<uint, uint>> map, fallbackBaseMulti);
        return map;
    }

    public static bool TryLookupActiveBabelOverride(
        IReadOnlyDictionary<string, string>? activeBabelOverrides,
        string tagName,
        uint tagHash,
        out string text)
    {
        text = string.Empty;
        if (activeBabelOverrides is null || activeBabelOverrides.Count == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(tagName) && activeBabelOverrides.TryGetValue(tagName.Trim(), out string? byName) && byName is not null)
        {
            text = byName;
            return true;
        }

        if (tagHash != 0 && activeBabelOverrides.TryGetValue($"0x{tagHash:X8}", out string? byHash) && byHash is not null)
        {
            text = byHash;
            return true;
        }

        foreach (var kv in activeBabelOverrides)
        {
            if (MatchesBabelKey(kv.Key, tagName, tagHash))
            {
                text = kv.Value;
                return true;
            }
        }

        return false;
    }

    private static string FormatRawValue(GdbFieldType type, uint raw) => type switch
    {
        GdbFieldType.Bool => raw != 0 ? "true" : "false",
        GdbFieldType.Float => BitConverter.Int32BitsToSingle(unchecked((int)raw)).ToString("0.#####", CultureInfo.InvariantCulture),
        GdbFieldType.Int => unchecked((int)raw).ToString(CultureInfo.InvariantCulture),
        GdbFieldType.Enum => raw.ToString(CultureInfo.InvariantCulture),
        _ => raw.ToString(CultureInfo.InvariantCulture),
    };

    private static string FormatComponentFieldValue(
        GdbFieldType type,
        uint raw,
        string fieldName,
        GdbMulti multi,
        IReadOnlyDictionary<string, string>? activeBabelOverrides,
        bool isVanillaLookup,
        out bool isLocalizedTag,
        out string resolvedTagKey)
    {
        isLocalizedTag = false;
        resolvedTagKey = string.Empty;

        if (type is not (GdbFieldType.String or GdbFieldType.ResourceReference))
        {
            return FormatRawValue(type, raw);
        }

        if (raw == 0 || raw == GdbHash.Null || raw == EmptyFnv1Hash)
        {
            isLocalizedTag = Engine_RecordNameCatalog.IsLocalizedTextTagField(fieldName);
            return string.Empty;
        }

        string symName = multi.NameOf(raw);
        if (Engine_RecordNameCatalog.IsLocalizedTextTagField(fieldName))
        {
            isLocalizedTag = true;
            resolvedTagKey = !string.IsNullOrWhiteSpace(symName) ? symName : $"0x{raw:X8}";

            if (!isVanillaLookup &&
                TryLookupActiveBabelOverride(activeBabelOverrides, symName, raw, out string modText))
            {
                return modText;
            }

            if (Engine_BnkTextBankLoader.TryLookupVanilla(raw, out string vanText) &&
                !Engine_RecordNameCatalog.IsGenericPlaceholderName(vanText))
            {
                return vanText;
            }

            if (!string.IsNullOrWhiteSpace(symName) && !Engine_RecordNameCatalog.IsGenericPlaceholderName(symName))
            {
                return symName;
            }

            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(symName))
        {
            return symName;
        }

        string? resolvedModel = Engine_PropModelIndex.Resolve(raw, Array.Empty<string>());
        if (!string.IsNullOrWhiteSpace(resolvedModel))
        {
            return resolvedModel;
        }

        string? resolvedTex = Engine_TexPathIndex.Resolve(raw);
        if (!string.IsNullOrWhiteSpace(resolvedTex))
        {
            return resolvedTex;
        }

        return $"0x{raw:X8}";
    }

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
            case GdbFieldType.String:
            case GdbFieldType.ResourceReference:
            {
                string trimA = valA.Trim();
                string trimB = valB.Trim();
                if (string.Equals(trimA, trimB, StringComparison.Ordinal))
                {
                    return true;
                }

                if (GdbPatchBuilder.IsVirtualAssetPath(trimA) || GdbPatchBuilder.IsVirtualAssetPath(trimB))
                {
                    return GdbPatchBuilder.ParseRawValue(type, trimA) == GdbPatchBuilder.ParseRawValue(type, trimB);
                }

                return false;
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
            uint vanillaLeafHash = leafHash;
            if (!baseMulti.TryLookup(vanillaLeafHash, out _))
            {
                GdbCursor pWalk = startCursor;
                var pGuard = new HashSet<uint>();
                while (pWalk.Valid && pWalk.View.TryFindLocalRaw(pWalk.Record, GdbView.HashParent, 6, out uint pComp, out _) &&
                       pComp != 0 && pComp != GdbHash.Null && pGuard.Add(pComp))
                {
                    if (baseMulti.TryLookup(pComp, out _))
                    {
                        vanillaLeafHash = pComp;
                        break;
                    }
                    if (!multi.TryLookup(pComp, out pWalk))
                    {
                        break;
                    }
                }
            }

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

                    // Resolve the true vanilla value by walking up the prototype hierarchy in baseMulti starting from vanillaLeafHash:
                    if (baseMulti.TryLookup(vanillaLeafHash, out GdbCursor baseCursor) &&
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
    /// Discovers all editable scalar and string/localized-tag GDB properties for a specific component record
    /// attached to or inherited by a node, walking up the component record's own parent inheritance chain.
    /// Properties have <c>TargetRecordHash</c> set to <paramref name="componentRecordHash"/> so edits save to this component instance.
    /// </summary>
    public static List<ModItemProperty> DiscoverComponentProperties(
        uint componentRecordHash,
        string slotName,
        string slotDisplayName,
        GdbMulti multi,
        IReadOnlyDictionary<string, string>? activeBabelOverrides = null,
        IReadOnlyDictionary<uint, Dictionary<uint, uint>>? activeTuEntryMap = null)
    {
        var result = new List<ModItemProperty>();
        if (componentRecordHash == 0 || !multi.TryLookup(componentRecordHash, out GdbCursor startCursor) || !startCursor.Valid)
        {
            return result;
        }

        GdbMulti baseMulti = Engine_GameGdb.BaseDatabase ?? multi;
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        GdbCursor parentCompCursor = default;
        bool hasParentComp = startCursor.View.TryFindLocalRaw(startCursor.Record, GdbView.HashParent, 6, out uint directParentCompHash, out _) &&
                             directParentCompHash != 0 &&
                             directParentCompHash != GdbHash.Null &&
                             multi.TryLookup(directParentCompHash, out parentCompCursor) &&
                             parentCompCursor.Valid;

        GdbCursor walk = startCursor;
        var visited = new HashSet<uint>();

        for (int depth = 0; depth < 32 && walk.Valid; depth++)
        {
            uint currentEntryHash = walk.View.RecordHash(walk.Record);
            if (!visited.Add(currentEntryHash))
            {
                break;
            }

            bool currentEntryInBase = baseMulti.TryLookup(currentEntryHash, out GdbCursor baseCurrentCursor) && baseCurrentCursor.Valid;

            foreach ((uint hash, GdbFieldType type, uint raw) in walk.View.RawFields(walk.Record))
            {
                if (hash == GdbView.HashParent ||
                    type is not (GdbFieldType.Bool or GdbFieldType.Int or GdbFieldType.Float or GdbFieldType.Enum or GdbFieldType.String or GdbFieldType.ResourceReference))
                {
                    continue;
                }

                bool isLocalInBase = currentEntryInBase &&
                    baseCurrentCursor.View.TryFindLocalRaw(baseCurrentCursor.Record, hash, -1, out _, out _);
                bool isLocalInTu = activeTuEntryMap is not null
                    ? (activeTuEntryMap.TryGetValue(currentEntryHash, out var entryTuProps) && entryTuProps.ContainsKey(hash))
                    : !currentEntryInBase;

                if (currentEntryInBase && !isLocalInBase && !isLocalInTu)
                {
                    // Skip fields that were only flattened onto currentEntryHash by SynthesizeCompleteDatabase
                    // so they are discovered at their true declaring ancestor record at depth > 0.
                    continue;
                }

                string name = multi.NameOf(hash);
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = $"0x{hash:X8}";
                }

                if (!seenNames.Add(name))
                {
                    continue;
                }

                GdbFieldType effectiveType = type;
                string currentFormatted = FormatComponentFieldValue(
                    type, raw, name, multi, activeBabelOverrides, isVanillaLookup: false,
                    out bool isLocTag, out string resolvedTagKey);

                if (currentEntryHash != componentRecordHash)
                {
                    // Property is inherited from an ancestor component (not overridden on componentRecordHash):
                    result.Add(new ModItemProperty
                    {
                        PropertyName = name,
                        DisplayLabel = PrettifyPropertyName(name),
                        CategoryId = slotName,
                        CategoryDisplayName = slotDisplayName,
                        PropertyHash = hash,
                        TargetRecordHash = componentRecordHash,
                        DeclaringHash = currentEntryHash,
                        Type = effectiveType,
                        VanillaValue = currentFormatted,
                        CurrentValue = currentFormatted,
                        IsModified = false,
                        IsLocalizedTextTag = isLocTag,
                        BabelTagKey = resolvedTagKey,
                        IsDefinedOnComponent = false,
                        IsWalkedUpFromParent = true,
                        ParentComponentValue = currentFormatted,
                        AssetKind = ClassifyAssetField(slotName, name, currentFormatted),
                    });
                    continue;
                }

                // Property is defined locally on componentRecordHash (depth == 0):
                string vanillaFormatted = currentFormatted;
                string parentCompFormatted = string.Empty;
                if (hasParentComp &&
                    multi.TryFindInherited(parentCompCursor, hash, -1, out _, out uint pCompRaw, out GdbFieldType pCompType))
                {
                    parentCompFormatted = FormatComponentFieldValue(
                        pCompType, pCompRaw, name, multi, activeBabelOverrides, isVanillaLookup: false,
                        out _, out _);
                }

                bool isMod;

                if (currentEntryInBase)
                {
                    if (baseMulti.TryFindInherited(baseCurrentCursor, hash, -1, out _, out uint baseRaw, out GdbFieldType baseType))
                    {
                        effectiveType = baseType;
                        vanillaFormatted = FormatComponentFieldValue(
                            baseType, baseRaw, name, baseMulti, null, isVanillaLookup: true,
                            out _, out _);
                    }
                    else if (isLocTag)
                    {
                        vanillaFormatted = string.Empty;
                    }

                    currentFormatted = FormatComponentFieldValue(
                        effectiveType, raw, name, multi, activeBabelOverrides, isVanillaLookup: false,
                        out isLocTag, out resolvedTagKey);

                    bool hasActiveBabel = isLocTag && TryLookupActiveBabelOverride(activeBabelOverrides, resolvedTagKey, raw, out _);
                    if (activeTuEntryMap is not null)
                    {
                        isMod = (isLocalInTu || hasActiveBabel) && !AreValuesEqual(effectiveType, currentFormatted, vanillaFormatted);
                    }
                    else
                    {
                        isMod = !AreValuesEqual(effectiveType, currentFormatted, vanillaFormatted);
                    }
                }
                else
                {
                    // Custom or overridden component record created in the active mod:
                    if (hasParentComp &&
                        multi.TryFindInherited(parentCompCursor, hash, -1, out _, out uint parentRaw, out GdbFieldType parentType))
                    {
                        effectiveType = parentType;
                        vanillaFormatted = FormatComponentFieldValue(
                            parentType, parentRaw, name, multi, activeBabelOverrides, isVanillaLookup: false,
                            out _, out _);
                    }
                    else if (Engine_RecordNameCatalog.TryGetComponentSchema(slotName, out ComponentSlotSchema? compSchema) &&
                             compSchema is not null &&
                             compSchema.DefaultFields.FirstOrDefault(f => f.FieldHash == hash) is { } defField)
                    {
                        vanillaFormatted = defField.DefaultValue;
                    }
                    else
                    {
                        vanillaFormatted = isLocTag ? string.Empty : FormatRawValue(effectiveType, 0);
                    }

                    currentFormatted = FormatComponentFieldValue(
                        effectiveType, raw, name, multi, activeBabelOverrides, isVanillaLookup: false,
                        out isLocTag, out resolvedTagKey);

                    isMod = !AreValuesEqual(effectiveType, currentFormatted, vanillaFormatted) || !hasParentComp;
                }

                result.Add(new ModItemProperty
                {
                    PropertyName = name,
                    DisplayLabel = PrettifyPropertyName(name),
                    CategoryId = slotName,
                    CategoryDisplayName = slotDisplayName,
                    PropertyHash = hash,
                    TargetRecordHash = componentRecordHash,
                    DeclaringHash = currentEntryHash,
                    Type = effectiveType,
                    VanillaValue = vanillaFormatted,
                    CurrentValue = currentFormatted,
                    IsModified = isMod,
                    IsLocalizedTextTag = isLocTag,
                    BabelTagKey = resolvedTagKey,
                    IsDefinedOnComponent = true,
                    IsWalkedUpFromParent = false,
                    ParentComponentValue = parentCompFormatted,
                    AssetKind = ClassifyAssetField(slotName, name, !string.IsNullOrWhiteSpace(currentFormatted) ? currentFormatted : vanillaFormatted),
                });
            }

            if (!walk.View.TryFindLocalRaw(walk.Record, GdbView.HashParent, 6, out uint parent, out _) ||
                parent == 0 || parent == GdbHash.Null || !multi.TryLookup(parent, out walk))
            {
                break;
            }
        }

        // If this component has a known schema in Engine_RecordNameCatalog, expose any default schema fields
        // that were not yet explicitly authored in the component chain so the user can edit them directly:
        if (Engine_RecordNameCatalog.TryGetComponentSchema(slotName, out ComponentSlotSchema? schema) && schema is not null)
        {
            foreach (ComponentFieldSchema fieldSchema in schema.DefaultFields)
            {
                if (!seenNames.Add(fieldSchema.FieldName))
                {
                    continue;
                }

                bool isLocTag = Engine_RecordNameCatalog.IsLocalizedTextTagField(fieldSchema.FieldName);
                result.Add(new ModItemProperty
                {
                    PropertyName = fieldSchema.FieldName,
                    DisplayLabel = PrettifyPropertyName(fieldSchema.FieldName),
                    CategoryId = slotName,
                    CategoryDisplayName = slotDisplayName,
                    PropertyHash = fieldSchema.FieldHash,
                    TargetRecordHash = componentRecordHash,
                    DeclaringHash = 0,
                    Type = fieldSchema.FieldType,
                    VanillaValue = fieldSchema.DefaultValue,
                    CurrentValue = fieldSchema.DefaultValue,
                    IsModified = false,
                    IsLocalizedTextTag = isLocTag,
                    IsDefinedOnComponent = false,
                    IsWalkedUpFromParent = true,
                    AssetKind = ClassifyAssetField(slotName, fieldSchema.FieldName, fieldSchema.DefaultValue),
                });
            }
        }

        result.Sort((a, b) =>
        {
            if (a.IsDefinedOnComponent != b.IsDefinedOnComponent)
            {
                return a.IsDefinedOnComponent ? -1 : 1;
            }
            int pa = GetPropertyPriority(a.PropertyName);
            int pb = GetPropertyPriority(b.PropertyName);
            if (pa != pb) return pa.CompareTo(pb);
            return string.Compare(a.DisplayLabel, b.DisplayLabel, StringComparison.OrdinalIgnoreCase);
        });

        return result;
    }

    /// <summary>
    /// Discovers scalar GDB properties authored directly on the node record itself (excluding component references and RemoveComponent).
    /// </summary>
    public static List<ModItemProperty> DiscoverDirectNodeScalarProperties(
        uint recordHash,
        GdbMulti multi,
        IReadOnlyDictionary<uint, Dictionary<uint, uint>>? activeTuEntryMap = null)
    {
        var result = new List<ModItemProperty>();
        if (recordHash == 0 || !multi.TryLookup(recordHash, out GdbCursor cursor) || !cursor.Valid)
        {
            return result;
        }

        GdbMulti baseMulti = Engine_GameGdb.BaseDatabase ?? multi;
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ScanCursorFields(cursor, recordHash, "NodeRecord", "Direct Node Fields", result, seenNames, multi, baseMulti, activeTuEntryMap);

        result.Sort((a, b) =>
        {
            int pa = GetPropertyPriority(a.PropertyName);
            int pb = GetPropertyPriority(b.PropertyName);
            if (pa != pb) return pa.CompareTo(pb);
            return string.Compare(a.DisplayLabel, b.DisplayLabel, StringComparison.OrdinalIgnoreCase);
        });

        return result;
    }

    private static void ScanCursorFields(
        GdbCursor targetCursor,
        uint targetRecordHash,
        string categoryId,
        string categoryDisplayName,
        List<ModItemProperty> result,
        HashSet<string> seenNames,
        GdbMulti multi,
        GdbMulti baseMulti,
        IReadOnlyDictionary<uint, Dictionary<uint, uint>>? activeTuEntryMap = null)
    {
        bool existsInBase = baseMulti.TryLookup(targetRecordHash, out GdbCursor baseCursor) && baseCursor.Valid;

        foreach ((uint hash, GdbFieldType type, uint raw) in targetCursor.View.RawFields(targetCursor.Record))
        {
            if (hash == GdbView.HashParent ||
                hash == Engine_RecordNameCatalog.RemoveComponentFieldHash ||
                type is not (GdbFieldType.Bool or GdbFieldType.Int or GdbFieldType.Float or GdbFieldType.Enum))
            {
                continue;
            }

            bool isLocalInBase = existsInBase &&
                baseCursor.View.TryFindLocalRaw(baseCursor.Record, hash, -1, out _, out _);
            bool isLocalInTu = activeTuEntryMap is not null
                ? (activeTuEntryMap.TryGetValue(targetRecordHash, out var tuProps) && tuProps.ContainsKey(hash))
                : !existsInBase;

            if (existsInBase && !isLocalInBase && !isLocalInTu)
            {
                continue;
            }

            string name = multi.NameOf(hash);
            if (!IsEditableStatField(name) || !seenNames.Add(name))
            {
                continue;
            }

            GdbFieldType effectiveType = type;
            string currentFormatted = FormatRawValue(type, raw);
            string vanillaFormatted = currentFormatted;

            if (existsInBase &&
                baseCursor.View.TryFindLocalRaw(baseCursor.Record, hash, -1, out uint baseRaw, out GdbFieldType baseType))
            {
                effectiveType = baseType;
                vanillaFormatted = FormatRawValue(baseType, baseRaw);
            }
            else if (!existsInBase)
            {
                vanillaFormatted = FormatRawValue(effectiveType, 0);
            }

            bool isMod = activeTuEntryMap is not null
                ? (isLocalInTu && !AreValuesEqual(effectiveType, currentFormatted, vanillaFormatted))
                : !AreValuesEqual(effectiveType, currentFormatted, vanillaFormatted);

            result.Add(new ModItemProperty
            {
                PropertyName = name,
                DisplayLabel = PrettifyPropertyName(name),
                CategoryId = categoryId,
                CategoryDisplayName = categoryDisplayName,
                PropertyHash = hash,
                TargetRecordHash = targetRecordHash,
                DeclaringHash = targetRecordHash,
                Type = effectiveType,
                VanillaValue = vanillaFormatted,
                CurrentValue = currentFormatted,
                IsModified = isMod,
            });
        }
    }

    private static uint FindFirstBaseAncestorHash(GdbMulti activeMulti, GdbMulti baseMulti, uint startRecordHash)
    {
        if (startRecordHash == 0) return 0;
        if (baseMulti.TryLookup(startRecordHash, out _)) return startRecordHash;

        if (!activeMulti.TryLookup(startRecordHash, out GdbCursor walk))
        {
            return 0;
        }

        var visited = new HashSet<uint>();
        while (walk.Valid && visited.Add(walk.View.RecordHash(walk.Record)))
        {
            if (!walk.View.TryFindLocalRaw(walk.Record, GdbView.HashParent, 6, out uint parentHash, out _) ||
                parentHash == 0 || parentHash == GdbHash.Null)
            {
                break;
            }

            if (baseMulti.TryLookup(parentHash, out _))
            {
                return parentHash;
            }

            if (!activeMulti.TryLookup(parentHash, out walk))
            {
                break;
            }
        }

        return 0;
    }

    /// <summary>
    /// Retrieves true unpatched vanilla defaults for an item directly from the base game database
    /// (or from its closest base game ancestor if the item is a custom child node).
    /// </summary>
    public static ItemModValues GetVanillaValues(ItemDetail detail)
    {
        GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;
        var values = new ItemModValues();
        if (baseMulti is null)
        {
            return values;
        }

        bool existsInBase = baseMulti.TryLookup(detail.RecordHash, out _);
        if (existsInBase)
        {
            ItemDetail vanillaDetail = Engine_ItemDetails.BuildOne(baseMulti, detail.RecordHash) ?? detail;
            values.VanillaDisplayName = Engine_ItemNames.VanillaDisplayNameOf(vanillaDetail);
            values.VanillaDescription = Engine_ItemNames.VanillaDescriptionOf(vanillaDetail);
            values.DisplayName = values.VanillaDisplayName;
            values.Description = values.VanillaDescription;

            List<ModItemProperty> discovered = DiscoverProperties(vanillaDetail, baseMulti);
            foreach (var p in discovered)
            {
                p.CurrentValue = p.VanillaValue;
                p.IsModified = false;
            }
            values.Properties.AddRange(discovered);
            values.HasModOverrides = false;
            return values;
        }

        GdbMulti activeMulti = BuildActiveModGdbMulti(baseMulti, EditorModExporter.ActiveModName);
        uint baseAncestorHash = FindFirstBaseAncestorHash(activeMulti, baseMulti, detail.RecordHash);
        ItemDetail effectiveDetail = Engine_ItemDetails.BuildOne(activeMulti, detail.RecordHash) ?? detail;
        ItemDetail ancestorDetail = (baseAncestorHash != 0 ? Engine_ItemDetails.BuildOne(baseMulti, baseAncestorHash) : null) ?? effectiveDetail;

        values.VanillaDisplayName = Engine_ItemNames.VanillaDisplayNameOf(ancestorDetail);
        values.VanillaDescription = Engine_ItemNames.VanillaDescriptionOf(ancestorDetail);
        values.DisplayName = values.VanillaDisplayName;
        values.Description = values.VanillaDescription;

        List<ModItemProperty> customDiscovered = DiscoverProperties(effectiveDetail, activeMulti);
        foreach (var p in customDiscovered)
        {
            p.CurrentValue = p.VanillaValue;
            p.IsModified = false;
        }
        values.Properties.AddRange(customDiscovered);
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
    /// Supports both base game items and custom mod-created child nodes.
    /// </summary>
    public static ItemModValues GetEffectiveValues(ItemDetail detail, string? activeMod = null)
    {
        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        string modsRoot = ResolveModsRoot();

        string pkgPath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);

        GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;
        var values = new ItemModValues();
        if (baseMulti is null)
        {
            return values;
        }

        bool existsInBase = baseMulti.TryLookup(detail.RecordHash, out _);
        GdbMulti activeMulti = existsInBase ? baseMulti : BuildActiveModGdbMulti(baseMulti, modName);

        ItemDetail effectiveDetail = existsInBase
            ? (Engine_ItemDetails.BuildOne(baseMulti, detail.RecordHash) ?? detail)
            : (Engine_ItemDetails.BuildOne(activeMulti, detail.RecordHash) ?? detail);

        ItemDetail vanillaDetail = effectiveDetail;
        if (!existsInBase)
        {
            uint baseAncestorHash = FindFirstBaseAncestorHash(activeMulti, baseMulti, detail.RecordHash);
            if (baseAncestorHash != 0)
            {
                vanillaDetail = Engine_ItemDetails.BuildOne(baseMulti, baseAncestorHash) ?? effectiveDetail;
            }
            values.HasModOverrides = true;
        }

        // 1. Text values (start with vanilla defaults):
        values.VanillaDisplayName = Engine_ItemNames.VanillaDisplayNameOf(vanillaDetail);
        values.VanillaDescription = Engine_ItemNames.VanillaDescriptionOf(vanillaDetail);
        values.DisplayName = values.VanillaDisplayName;
        values.Description = values.VanillaDescription;

        // 2. Discover all genuine GDB properties (from baseMulti for base items, or activeMulti for custom child items):
        List<ModItemProperty> discovered = existsInBase
            ? DiscoverProperties(vanillaDetail, baseMulti)
            : DiscoverProperties(effectiveDetail, activeMulti);
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
                        if (MatchesBabelKey(row.Key, effectiveDetail.NameTagName, effectiveDetail.NameTag) ||
                            MatchesBabelKey(row.Key, detail.NameTagName, detail.NameTag))
                        {
                            values.DisplayName = row.Value;
                            values.HasModOverrides = true;
                        }
                        else if (MatchesBabelKey(row.Key, effectiveDetail.DescTagName, effectiveDetail.DescTag) ||
                                 MatchesBabelKey(row.Key, detail.DescTagName, detail.DescTag))
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
                        if (MatchesBabelKey(row.Key, effectiveDetail.NameTagName, effectiveDetail.NameTag) ||
                            MatchesBabelKey(row.Key, detail.NameTagName, detail.NameTag))
                        {
                            values.DisplayName = row.Value;
                            values.HasModOverrides = true;
                        }
                        else if (MatchesBabelKey(row.Key, effectiveDetail.DescTagName, effectiveDetail.DescTag) ||
                                 MatchesBabelKey(row.Key, detail.DescTagName, detail.DescTag))
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

            // Priority 2: Check DeclaringHash in active mod's TU database (only when DeclaringHash == TargetRecordHash):
            if (prop.DeclaringHash == prop.TargetRecordHash &&
                tuEntryMap.TryGetValue(prop.DeclaringHash, out var declProps) &&
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
        string modsRoot = ResolveModsRoot();

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
        GdbMulti activeMulti = BuildActiveModGdbMulti(
            modName,
            out _,
            out _,
            out _,
            out _,
            baseMulti) ?? baseMulti;

        var propsByTuFile = new Dictionary<string, Dictionary<uint, List<ModItemProperty>>>(StringComparer.OrdinalIgnoreCase);

        foreach (ModItemProperty prop in values.Properties)
        {
            string tuSubPath = ResolveGdbTuSubPath(baseMulti, prop.TargetRecordHash, pkgPath, activeMulti, detail.RecordHash);
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

                bool existsInBase = baseMulti.TryLookup(targetHash, out _);
                GdbPatchEntry? existingCustomEntry = !existsInBase
                    ? db.Entries.FirstOrDefault(e => e.Id == targetHash)
                    : null;

                if (modifiedList.Count > 0 || existingCustomEntry is not null)
                {
                    GdbPatchEntry entry;
                    if (existsInBase)
                    {
                        var modifiedFields = modifiedList
                            .Select(p => (p.PropertyHash, p.Type, p.CurrentValue));
                        entry = GdbPatchBuilder.BuildPromotedEntry(baseMulti, targetHash, modifiedFields);
                    }
                    else
                    {
                        var sourceMulti = activeMulti.TryLookup(targetHash, out _) ? activeMulti : baseMulti;
                        var modifiedFields = modifiedList
                            .Select(p => (p.PropertyHash, p.Type, p.CurrentValue));
                        entry = GdbPatchBuilder.BuildPromotedEntry(sourceMulti, targetHash, modifiedFields);

                        if (existingCustomEntry is not null)
                        {
                            uint parentHash = 0;
                            foreach (var ep in existingCustomEntry.Properties)
                            {
                                if (ep.NameHash == GdbView.HashParent)
                                {
                                    parentHash = ep.Value;
                                    break;
                                }
                            }
                            GdbCursor parentCursor = default;
                            bool hasParent = parentHash != 0 && parentHash != GdbHash.Null &&
                                             activeMulti.TryLookup(parentHash, out parentCursor);

                            foreach (var existingProp in existingCustomEntry.Properties)
                            {
                                if (entry.Properties.Any(ep => ep.NameHash == existingProp.NameHash))
                                {
                                    continue;
                                }

                                if (existingProp.NameHash == GdbView.HashParent)
                                {
                                    entry.Properties.Add(existingProp);
                                    continue;
                                }

                                bool wasInPropList = propList.Any(p => p.PropertyHash == existingProp.NameHash);
                                if (!wasInPropList)
                                {
                                    entry.Properties.Add(existingProp);
                                }
                                else if (!hasParent || !activeMulti.TryFindInherited(parentCursor, existingProp.NameHash, -1, out _, out _, out _))
                                {
                                    entry.Properties.Add(existingProp);
                                }
                            }
                        }
                    }

                    GdbPatchBuilder.UpsertEntry(db, entry);

                    string baseLabel = !string.IsNullOrWhiteSpace(values.DisplayName)
                        ? values.DisplayName
                        : (!string.IsNullOrWhiteSpace(detail.DisplayName) ? detail.DisplayName : detail.Label);

                    string recName = activeMulti.NameOf(targetHash);
                    if (string.IsNullOrWhiteSpace(recName))
                    {
                        recName = !string.IsNullOrWhiteSpace(detail.InternalName) ? detail.InternalName : baseLabel;
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
                    db.Names.Remove(targetHash);
                    foreach (var p in propList)
                    {
                        db.Names.Remove(p.PropertyHash);
                    }
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

        bool isCustomNode = detail.RecordHash != 0 && !baseMulti.TryLookup(detail.RecordHash, out _);
        bool usesInheritedBaseInvComp = isCustomNode &&
            (detail.InventoryComponentHash == 0 || baseMulti.TryLookup(detail.InventoryComponentHash, out _));

        string customNodeSym = isCustomNode
            ? (!string.IsNullOrWhiteSpace(detail.InternalName) ? detail.InternalName : activeMulti.NameOf(detail.RecordHash))
            : string.Empty;
        if (isCustomNode && string.IsNullOrWhiteSpace(customNodeSym))
        {
            customNodeSym = $"Node_{detail.RecordHash:X8}";
        }

        string nameKey = usesInheritedBaseInvComp
            ? $"INV_ITEM_{customNodeSym.ToUpperInvariant()}_NAME"
            : (detail.NameTagName.Length > 0
                ? detail.NameTagName
                : (detail.NameTag != 0 ? $"0x{detail.NameTag:X8}" : string.Empty));
        string descKey = usesInheritedBaseInvComp
            ? $"INV_ITEM_{customNodeSym.ToUpperInvariant()}_DESC"
            : (detail.DescTagName.Length > 0
                ? detail.DescTagName
                : (detail.DescTag != 0 ? $"0x{detail.DescTag:X8}" : string.Empty));

        uint targetNameTagHash = usesInheritedBaseInvComp ? Engine_GameHash.Fnv1(nameKey) : detail.NameTag;
        uint targetDescTagHash = usesInheritedBaseInvComp ? Engine_GameHash.Fnv1(descKey) : detail.DescTag;

        SafeLog($"[mod_values] Babel keys — name: '{nameKey}' (tag=0x{targetNameTagHash:X8}, tagName='{detail.NameTagName}'), " +
                 $"desc: '{descKey}' (tag=0x{targetDescTagHash:X8}, tagName='{detail.DescTagName}')");

        string? existingNameVal = null;
        string? existingDescVal = null;
        var mergedBabelRows = new List<(string Key, string Value)>();
        foreach (BabelOverlayRow row in existingBabelRows)
        {
            if (nameKey.Length > 0 && MatchesBabelKey(row.Key, nameKey, targetNameTagHash))
            {
                existingNameVal = row.Value;
                continue;
            }

            if (descKey.Length > 0 && MatchesBabelKey(row.Key, descKey, targetDescTagHash))
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

        // If a custom child node is saving its own Name or Description for the first time while still pointing
        // at an ancestor's InventoryItemComponent, promote a dedicated InventoryItemComponent record for the child node:
        if (usesInheritedBaseInvComp && (shouldSaveName || shouldSaveDesc))
        {
            string nodeTuSubPath = ResolveGdbTuSubPath(baseMulti, detail.RecordHash, pkgPath, activeMulti, detail.RecordHash);
            int existingUpdateIdx = updates.FindIndex(u => u.Path.Equals(nodeTuSubPath, StringComparison.OrdinalIgnoreCase));
            byte[]? tuBytes = existingUpdateIdx >= 0
                ? updates[existingUpdateIdx].Bytes
                : LoadPackageEntry(pkgPath, nodeTuSubPath);
            GdbPatchDatabase nodeDb = GdbPatchBuilder.LoadOrCreatePatchDatabase(tuBytes);

            string invCompName = $"{customNodeSym}_InventoryItemComponent";
            uint invCompHash = Engine_GameHash.Fnv1(invCompName);
            GdbPatchEntry invEntry = nodeDb.Entries.FirstOrDefault(e => e.Id == invCompHash) ?? new GdbPatchEntry(invCompHash, 0x01);

            if (detail.InventoryComponentHash != 0 &&
                !invEntry.Properties.Any(p => p.NameHash == GdbView.HashParent))
            {
                invEntry.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, detail.InventoryComponentHash));
                nodeDb.Names[GdbView.HashParent] = "parent";
            }

            if (shouldSaveName)
            {
                int nIdx = invEntry.Properties.FindIndex(p => p.NameHash == Engine_ItemDetails.NameTag);
                var nProp = new GdbPatchProperty(Engine_ItemDetails.NameTag, GdbFieldType.String, targetNameTagHash);
                if (nIdx >= 0) invEntry.Properties[nIdx] = nProp;
                else invEntry.Properties.Add(nProp);
                nodeDb.Names[Engine_ItemDetails.NameTag] = "NameTag";
                nodeDb.Names[targetNameTagHash] = nameKey;
            }

            if (shouldSaveDesc)
            {
                int dIdx = invEntry.Properties.FindIndex(p => p.NameHash == Engine_ItemDetails.DescTag);
                var dProp = new GdbPatchProperty(Engine_ItemDetails.DescTag, GdbFieldType.String, targetDescTagHash);
                if (dIdx >= 0) invEntry.Properties[dIdx] = dProp;
                else invEntry.Properties.Add(dProp);
                nodeDb.Names[Engine_ItemDetails.DescTag] = "DescriptionTag";
                nodeDb.Names[targetDescTagHash] = descKey;
            }

            GdbPatchBuilder.UpsertEntry(nodeDb, invEntry);
            nodeDb.Names[invCompHash] = invCompName;

            GdbPatchEntry? childNodeEntry = nodeDb.Entries.FirstOrDefault(e => e.Id == detail.RecordHash);
            if (childNodeEntry is not null)
            {
                int slotIdx = childNodeEntry.Properties.FindIndex(p => p.NameHash == Engine_ItemDetails.InventoryItemComponent);
                var slotProp = new GdbPatchProperty(Engine_ItemDetails.InventoryItemComponent, GdbFieldType.Reference, invCompHash);
                if (slotIdx >= 0) childNodeEntry.Properties[slotIdx] = slotProp;
                else childNodeEntry.Properties.Add(slotProp);
                nodeDb.Names[Engine_ItemDetails.InventoryItemComponent] = "InventoryItemComponent";
                GdbPatchBuilder.UpsertEntry(nodeDb, childNodeEntry);
            }

            byte[] serializedNodeDb = GdbWriter.Serialize(nodeDb);
            if (existingUpdateIdx >= 0)
            {
                updates[existingUpdateIdx] = (nodeTuSubPath, serializedNodeDb);
            }
            else
            {
                updates.Add((nodeTuSubPath, serializedNodeDb));
                deletes.Remove(nodeTuSubPath);
            }
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
            PruneUnreferencedModAssets(pkgPath);
            F2ModPackage.Defragment(pkgPath, out _);
        }
        catch (Exception ex)
        {
            SafeWarn($"[mod_values] Failed to auto-sync {Refii.Core.Engine_Title.Active.ModExtension} package for '{modName}': {ex.Message}");
        }

        SafeLog($"[mod_values] Saved overrides for '{detail.Label}' [0x{detail.RecordHash:X8}] to mod '{modName}' ({Refii.Core.Engine_Title.Active.ModExtension})");

        // 3. HOT-RELOAD IN-MEMORY OVERLAYS AND REFRESH SCENE NODES
        Engine_GameGdb.Invalidate();
        Refii.Equipment.Engine_WeaponIndex.Invalidate();
        Refii.Equipment.Engine_ItemIndex.Invalidate();
        Engine_BnkTextBankLoader.Invalidate();
        BnkMountPlugin.Instance?.ReloadModOverlays();
        RefreshSceneEquipment();
        EditorModExporter.NotifyModContentChanged(modName);
    }

    /// <summary>
    /// Saves modified properties across a node's attached component records (and/or direct node fields)
    /// into the active mod's Title Update (.gdb.tu) and localized text table (book.babel.csv).
    /// Preserves existing fields on custom mod-created records and promotes base game records cleanly.
    /// </summary>
    public static void SaveNodeComponentOverrides(
        uint targetNodeHash,
        IEnumerable<ModItemProperty> properties,
        string? activeMod = null,
        string? nodeLabel = null)
    {
        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(modName))
        {
            throw new InvalidOperationException("No active mod selected.");
        }

        string modsRoot = ResolveModsRoot();

        string pkgPath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
        if (!File.Exists(pkgPath))
        {
            Directory.CreateDirectory(modsRoot);
            ModWorkspace.CreateModPackage(modsRoot, modName, out _, out _);
        }

        GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;
        if (baseMulti is null)
        {
            throw new InvalidOperationException("Database not loaded; cannot resolve schema slots.");
        }

        GdbMulti activeMulti = BuildActiveModGdbMulti(baseMulti, modName);

        // Load existing book.babel.csv so localized text tag edits (NameTag, DescriptionTag, TextTag, etc.) can be saved:
        string relBabel = "language/en-uk/text/book.babel.csv";
        byte[]? existingBabelBytes = LoadPackageEntry(pkgPath, relBabel);
        var babelDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (existingBabelBytes != null && existingBabelBytes.Length > 0)
        {
            foreach (var row in BabelOverlay.ParseCsv(Encoding.UTF8.GetString(existingBabelBytes), new List<string>()))
            {
                if (!string.IsNullOrWhiteSpace(row.Key))
                {
                    babelDict[row.Key] = row.Value;
                }
            }
        }
        bool babelModified = false;

        var propsByTuFile = new Dictionary<string, Dictionary<uint, List<ModItemProperty>>>(StringComparer.OrdinalIgnoreCase);

        foreach (ModItemProperty prop in properties)
        {
            uint recHash = prop.TargetRecordHash != 0 ? prop.TargetRecordHash : targetNodeHash;
            string tuSubPath = ResolveGdbTuSubPath(baseMulti, recHash, pkgPath, activeMulti, targetNodeHash);
            if (!propsByTuFile.TryGetValue(tuSubPath, out var byRecord))
            {
                byRecord = new Dictionary<uint, List<ModItemProperty>>();
                propsByTuFile[tuSubPath] = byRecord;
            }

            if (!byRecord.TryGetValue(recHash, out var propList))
            {
                propList = new List<ModItemProperty>();
                byRecord[recHash] = propList;
            }

            propList.Add(prop);
        }

        var updates = new List<(string Path, byte[] Bytes)>();
        var deletes = new List<string>();
        string nodeSym = activeMulti.NameOf(targetNodeHash);

        foreach (var (tuSubPath, records) in propsByTuFile)
        {
            byte[]? existingBytes = LoadPackageEntry(pkgPath, tuSubPath);
            GdbPatchDatabase db = GdbPatchBuilder.LoadOrCreatePatchDatabase(existingBytes);

            foreach (var (recHash, propList) in records)
            {
                bool existsInBase = baseMulti.TryLookup(recHash, out GdbCursor baseRecCursor);
                var gdbFieldEdits = new List<(uint PropertyHash, GdbFieldType Type, string EncodedValue, string PropertyName, string? RawStringSymbol)>();

                foreach (ModItemProperty p in propList)
                {
                    bool isModified = !AreValuesEqual(p.Type, p.CurrentValue, p.VanillaValue);

                    if (p.IsLocalizedTextTag)
                    {
                        // Determine whether this component record owns a dedicated Babel tag key or needs one minted:
                        string tagKey = p.BabelTagKey?.Trim() ?? string.Empty;
                        bool needsDedicatedTag = string.IsNullOrWhiteSpace(tagKey) ||
                                                 tagKey.Equals("GUI_NEEDS_TEXT", StringComparison.OrdinalIgnoreCase) ||
                                                 tagKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                                                 p.DeclaringHash != recHash;

                        string legacyMintedKey = $"MOD_NODE_{targetNodeHash:X8}_{p.PropertyName.ToUpperInvariant()}";
                        string mintedKey;
                        if (!string.IsNullOrWhiteSpace(nodeSym))
                        {
                            string suffix = p.PropertyHash == Engine_ItemDetails.NameTag
                                ? "NAME"
                                : (p.PropertyHash == Engine_ItemDetails.DescTag ? "DESC" : p.PropertyName.ToUpperInvariant());
                            mintedKey = $"INV_ITEM_{nodeSym.ToUpperInvariant()}_{suffix}";
                        }
                        else
                        {
                            mintedKey = legacyMintedKey;
                        }

                        if (isModified)
                        {
                            string effectiveTagKey = needsDedicatedTag ? mintedKey : tagKey;
                            uint effectiveTagHash = Engine_GameHash.Fnv1(effectiveTagKey);
                            babelDict[effectiveTagKey] = p.CurrentValue?.Trim() ?? string.Empty;
                            if (!effectiveTagKey.Equals(legacyMintedKey, StringComparison.OrdinalIgnoreCase))
                            {
                                babelDict.Remove(legacyMintedKey);
                            }
                            babelModified = true;

                            // Only write the tag hash to .gdb.tu if this record does not already own this exact tag in baseMulti:
                            bool baseAlreadyHasTag = existsInBase &&
                                                     baseRecCursor.View.TryFindLocalRaw(baseRecCursor.Record, p.PropertyHash, -1, out uint baseTagRaw, out _) &&
                                                     baseTagRaw == effectiveTagHash;
                            if (!baseAlreadyHasTag)
                            {
                                gdbFieldEdits.Add((
                                    p.PropertyHash,
                                    GdbFieldType.String,
                                    $"0x{effectiveTagHash:X8}",
                                    p.PropertyName,
                                    effectiveTagKey));
                            }
                        }
                        else
                        {
                            // Reverted to vanilla: remove any mod Babel entry owned by this record or minted for this node:
                            if (p.DeclaringHash == recHash && !string.IsNullOrWhiteSpace(tagKey) && babelDict.Remove(tagKey))
                            {
                                babelModified = true;
                            }
                            if (babelDict.Remove(mintedKey))
                            {
                                babelModified = true;
                            }
                            if (babelDict.Remove(legacyMintedKey))
                            {
                                babelModified = true;
                            }
                        }
                    }
                    else
                    {
                        if (isModified)
                        {
                            string? rawSym = (p.Type is (GdbFieldType.String or GdbFieldType.ResourceReference) && !string.IsNullOrWhiteSpace(p.CurrentValue) && !p.CurrentValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                                ? p.CurrentValue.Trim()
                                : null;
                            gdbFieldEdits.Add((p.PropertyHash, p.Type, p.CurrentValue ?? string.Empty, p.PropertyName, rawSym));
                        }
                    }
                }

                GdbPatchEntry? existingCustomEntry = !existsInBase
                    ? db.Entries.FirstOrDefault(e => e.Id == recHash)
                    : null;

                if (gdbFieldEdits.Count > 0 || existingCustomEntry is not null || !existsInBase)
                {
                    GdbPatchEntry entry;
                    if (existsInBase)
                    {
                        var modifiedFields = gdbFieldEdits.Select(f => (f.PropertyHash, f.Type, f.EncodedValue));
                        entry = GdbPatchBuilder.BuildPromotedEntry(baseMulti, recHash, modifiedFields);
                    }
                    else
                    {
                        var sourceMulti = activeMulti.TryLookup(recHash, out _) ? activeMulti : baseMulti;
                        var allEditedFields = gdbFieldEdits.Select(f => (f.PropertyHash, f.Type, f.EncodedValue));
                        entry = GdbPatchBuilder.BuildPromotedEntry(sourceMulti, recHash, allEditedFields);

                        if (existingCustomEntry is not null)
                        {
                            uint parentCompHash = 0;
                            foreach (var ep in existingCustomEntry.Properties)
                            {
                                if (ep.NameHash == GdbView.HashParent)
                                {
                                    parentCompHash = ep.Value;
                                    break;
                                }
                            }
                            GdbCursor parentCompCursor = default;
                            bool hasParentComp = parentCompHash != 0 && parentCompHash != GdbHash.Null &&
                                                 activeMulti.TryLookup(parentCompHash, out parentCompCursor);

                            foreach (var existingProp in existingCustomEntry.Properties)
                            {
                                if (entry.Properties.Any(ep => ep.NameHash == existingProp.NameHash))
                                {
                                    continue;
                                }

                                if (existingProp.NameHash == GdbView.HashParent)
                                {
                                    entry.Properties.Add(existingProp);
                                    continue;
                                }

                                bool wasInPropList = propList.Any(p => p.PropertyHash == existingProp.NameHash);
                                if (!wasInPropList)
                                {
                                    entry.Properties.Add(existingProp);
                                }
                                else if (!hasParentComp || !activeMulti.TryFindInherited(parentCompCursor, existingProp.NameHash, -1, out _, out _, out _))
                                {
                                    entry.Properties.Add(existingProp);
                                }
                            }
                        }
                    }

                    GdbPatchBuilder.UpsertEntry(db, entry);

                    string recName = activeMulti.NameOf(recHash);
                    if (string.IsNullOrWhiteSpace(recName) && recHash == targetNodeHash && !string.IsNullOrWhiteSpace(nodeLabel))
                    {
                        recName = nodeLabel;
                    }
                    if (!string.IsNullOrWhiteSpace(recName))
                    {
                        db.Names[recHash] = recName;
                    }

                    foreach (var f in gdbFieldEdits)
                    {
                        if (!string.IsNullOrWhiteSpace(f.PropertyName))
                        {
                            db.Names[f.PropertyHash] = f.PropertyName;
                        }
                        if (!string.IsNullOrWhiteSpace(f.RawStringSymbol))
                        {
                            db.Names[GdbPatchBuilder.HashStringOrAssetPath(f.RawStringSymbol)] = f.RawStringSymbol;
                        }
                    }
                }
                else
                {
                    db.Names.Remove(recHash);
                    foreach (var p in propList)
                    {
                        db.Names.Remove(p.PropertyHash);
                    }
                    GdbPatchBuilder.RemoveEntry(db, recHash);
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

        if (babelModified)
        {
            if (babelDict.Count == 0)
            {
                deletes.Add(relBabel);
            }
            else
            {
                var babelSb = new StringBuilder();
                babelSb.AppendLine("# Refii Babel Text Overlays");
                babelSb.AppendLine("# tag,text");
                foreach (var kvp in babelDict)
                {
                    babelSb.Append(kvp.Key).Append(',').AppendLine(EscapeCsv(kvp.Value));
                }
                updates.Add((relBabel, Encoding.UTF8.GetBytes(babelSb.ToString())));
            }
        }

        try
        {
            F2ModPackage.IncrementalSync(pkgPath, updates, deletes);
            PruneUnreferencedModAssets(pkgPath);
            F2ModPackage.Defragment(pkgPath, out _);
        }
        catch (Exception ex)
        {
            SafeWarn($"[mod_values] Failed to auto-sync {Refii.Core.Engine_Title.Active.ModExtension} package for '{modName}': {ex.Message}");
        }

        SafeLog($"[mod_values] Saved node/component overrides for [0x{targetNodeHash:X8}] to mod '{modName}' ({Refii.Core.Engine_Title.Active.ModExtension})");

        Engine_GameGdb.Invalidate();
        Refii.Equipment.Engine_WeaponIndex.Invalidate();
        Refii.Equipment.Engine_ItemIndex.Invalidate();
        Engine_BnkTextBankLoader.Invalidate();
        if (!IsTestHost())
        {
            Engine_TexLibrary.Clear();
            Engine_MdlMaterial.ClearSharedCache();
        }
        BnkMountPlugin.Instance?.ReloadModOverlays();
        RefreshSceneEquipment();
        EditorModExporter.NotifyModContentChanged(modName);
    }

    /// <summary>
    /// Creates a new child node branching off any parent node in the GDB hierarchy.
    /// The child node automatically inherits all of its parent's components via walk-up resolution.
    /// If <paramref name="displayName"/> is provided, an <c>InventoryItemComponent</c> record (inheriting from
    /// the parent's <c>InventoryItemComponent</c> if present) is also created and linked, with its localized
    /// <c>NameTag</c> and <c>DescriptionTag</c> written to <c>book.babel.csv</c>.
    /// </summary>
    public static bool CreateChildNode(
        uint parentNodeHash,
        string? recordName,
        string? displayName,
        string? description,
        string? activeMod,
        out uint newRecordHash,
        out string error)
    {
        newRecordHash = 0;
        error = string.Empty;

        if (parentNodeHash == 0)
        {
            error = "Invalid parent node hash.";
            return false;
        }

        string trimmedRecord = recordName?.Trim() ?? string.Empty;
        string trimmedDisplay = displayName?.Trim() ?? string.Empty;
        string trimmedDesc = description?.Trim() ?? string.Empty;

        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(modName))
        {
            error = "No active mod selected.";
            return false;
        }

        string modsRoot = ResolveModsRoot();

        string pkgPath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
        if (!File.Exists(pkgPath))
        {
            Directory.CreateDirectory(modsRoot);
            ModWorkspace.CreateModPackage(modsRoot, modName, out _, out _);
        }

        GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;
        if (baseMulti is null)
        {
            error = "Game GDB databases not loaded.";
            return false;
        }

        GdbMulti multi = BuildActiveModGdbMulti(baseMulti, modName);

        string effectiveRecordName;
        if (!string.IsNullOrWhiteSpace(trimmedRecord))
        {
            effectiveRecordName = trimmedRecord;
        }
        else if (!string.IsNullOrWhiteSpace(trimmedDisplay))
        {
            effectiveRecordName = "ObjectInventory_" + trimmedDisplay.Replace(" ", "").Replace("'", "").Replace("-", "");
        }
        else
        {
            string parentSym = multi.NameOf(parentNodeHash);
            if (string.IsNullOrWhiteSpace(parentSym))
            {
                parentSym = $"Node_{parentNodeHash:X8}";
            }
            int suffix = 1;
            do
            {
                effectiveRecordName = $"{parentSym}_Child_{suffix}";
                suffix++;
            } while (multi.TryLookup(Engine_GameHash.Fnv1(effectiveRecordName), out _));
        }

        newRecordHash = Engine_GameHash.Fnv1(effectiveRecordName);

        string tuSubPath = ResolveGdbTuSubPath(baseMulti, parentNodeHash, pkgPath, multi, parentNodeHash);
        byte[]? existingBytes = LoadPackageEntry(pkgPath, tuSubPath);
        GdbPatchDatabase db = GdbPatchBuilder.LoadOrCreatePatchDatabase(existingBytes);

        var nodeEntry = new GdbPatchEntry(newRecordHash, 0x01);
        nodeEntry.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, parentNodeHash));
        db.Names[newRecordHash] = effectiveRecordName;
        db.Names[GdbView.HashParent] = "parent";

        var updates = new List<(string Path, byte[] Bytes)>();

        if (!string.IsNullOrWhiteSpace(trimmedDisplay) || !string.IsNullOrWhiteSpace(trimmedDesc))
        {
            string invCompName = effectiveRecordName + "_InventoryItemComponent";
            uint invCompHash = Engine_GameHash.Fnv1(invCompName);

            var invEntry = new GdbPatchEntry(invCompHash, 0x01);

            // Link to parent's InventoryItemComponent if the parent has or inherits one:
            if (multi.TryLookup(parentNodeHash, out GdbCursor parentCursor) &&
                multi.TryFindInherited(parentCursor, Engine_ItemDetails.InventoryItemComponent, 6, out _, out uint parentInvComp, out _) &&
                parentInvComp != 0 && parentInvComp != GdbHash.Null)
            {
                invEntry.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, parentInvComp));
            }
            else
            {
                invEntry.Properties.Add(new GdbPatchProperty(RatingField, GdbFieldType.Int, 1));
                db.Names[RatingField] = "Rating";
            }

            string nameTagName = string.Empty;
            if (!string.IsNullOrWhiteSpace(trimmedDisplay))
            {
                nameTagName = "INV_ITEM_" + effectiveRecordName.ToUpperInvariant() + "_NAME";
                uint nameTagHash = Engine_GameHash.Fnv1(nameTagName);
                invEntry.Properties.Add(new GdbPatchProperty(Engine_ItemDetails.NameTag, GdbFieldType.String, nameTagHash));
                db.Names[Engine_ItemDetails.NameTag] = "NameTag";
                db.Names[nameTagHash] = nameTagName;
            }

            string descTagName = string.Empty;
            if (!string.IsNullOrWhiteSpace(trimmedDesc))
            {
                descTagName = "INV_ITEM_" + effectiveRecordName.ToUpperInvariant() + "_DESC";
                uint descTagHash = Engine_GameHash.Fnv1(descTagName);
                invEntry.Properties.Add(new GdbPatchProperty(Engine_ItemDetails.DescTag, GdbFieldType.String, descTagHash));
                db.Names[Engine_ItemDetails.DescTag] = "DescriptionTag";
                db.Names[descTagHash] = descTagName;
            }

            GdbPatchBuilder.UpsertEntry(db, invEntry);
            db.Names[invCompHash] = invCompName;

            nodeEntry.Properties.Add(new GdbPatchProperty(Engine_ItemDetails.InventoryItemComponent, GdbFieldType.Reference, invCompHash));
            db.Names[Engine_ItemDetails.InventoryItemComponent] = "InventoryItemComponent";

            // Write localized text to book.babel.csv:
            string relBabel = "language/en-uk/text/book.babel.csv";
            var existingBabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            byte[]? babelBytes = LoadPackageEntry(pkgPath, relBabel);
            if (babelBytes != null && babelBytes.Length > 0)
            {
                string csv = Encoding.UTF8.GetString(babelBytes);
                foreach (var row in BabelOverlay.ParseCsv(csv, new List<string>()))
                {
                    existingBabel[row.Key] = row.Value;
                }
            }

            if (!string.IsNullOrWhiteSpace(nameTagName))
            {
                existingBabel[nameTagName] = trimmedDisplay;
            }
            if (!string.IsNullOrWhiteSpace(descTagName))
            {
                existingBabel[descTagName] = trimmedDesc;
            }

            var babelSb = new StringBuilder();
            babelSb.AppendLine("# Refii Babel Text Overlays");
            babelSb.AppendLine("# tag,text");
            foreach (var r in existingBabel)
            {
                babelSb.Append(r.Key).Append(',').AppendLine(EscapeCsv(r.Value));
            }
            updates.Add((relBabel, Encoding.UTF8.GetBytes(babelSb.ToString())));
        }

        GdbPatchBuilder.UpsertEntry(db, nodeEntry);
        updates.Add((tuSubPath, GdbWriter.Serialize(db)));

        try
        {
            F2ModPackage.IncrementalSync(pkgPath, updates, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        Engine_GameGdb.Invalidate();
        Refii.Equipment.Engine_WeaponIndex.Invalidate();
        Refii.Equipment.Engine_ItemIndex.Invalidate();
        Engine_BnkTextBankLoader.Invalidate();
        BnkMountPlugin.Instance?.ReloadModOverlays();
        RefreshSceneEquipment();
        EditorModExporter.NotifyModContentChanged(modName);

        return true;
    }

    /// <summary>
    /// Attaches a new component slot or overrides an inherited component slot directly on <paramref name="nodeRecordHash"/>.
    /// Creates a new component record in <c>.gdb.tu</c> (inheriting from <paramref name="parentComponentRecordHash"/> if provided)
    /// and binds <c>[ Hash(slotName), newComponentRecordHash ]</c> on the node record.
    /// </summary>
    public static bool AttachOrOverrideComponent(
        uint nodeRecordHash,
        string slotName,
        uint? parentComponentRecordHash,
        string? activeMod,
        out uint newComponentRecordHash,
        out string error)
    {
        newComponentRecordHash = 0;
        error = string.Empty;

        if (nodeRecordHash == 0)
        {
            error = "Invalid node record hash.";
            return false;
        }

        string trimmedSlot = slotName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedSlot))
        {
            error = "Component slot name cannot be empty.";
            return false;
        }

        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(modName))
        {
            error = "No active mod selected.";
            return false;
        }

        string modsRoot = ResolveModsRoot();

        string pkgPath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
        if (!File.Exists(pkgPath))
        {
            Directory.CreateDirectory(modsRoot);
            ModWorkspace.CreateModPackage(modsRoot, modName, out _, out _);
        }

        GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;
        if (baseMulti is null)
        {
            error = "Game GDB databases not loaded.";
            return false;
        }

        GdbMulti multi = BuildActiveModGdbMulti(baseMulti, modName);
        string tuSubPath = ResolveGdbTuSubPath(baseMulti, nodeRecordHash, pkgPath, multi, nodeRecordHash);
        byte[]? existingBytes = LoadPackageEntry(pkgPath, tuSubPath);
        GdbPatchDatabase db = GdbPatchBuilder.LoadOrCreatePatchDatabase(existingBytes);

        string nodeName = multi.NameOf(nodeRecordHash);
        if (string.IsNullOrWhiteSpace(nodeName))
        {
            nodeName = $"Node_{nodeRecordHash:X8}";
        }

        string compRecordName = $"{nodeName}_{trimmedSlot}";
        newComponentRecordHash = Engine_GameHash.Fnv1(compRecordName);
        uint slotHash = Engine_GameHash.Fnv1(trimmedSlot);

        // 1. Create the component record entry:
        var compEntry = new GdbPatchEntry(newComponentRecordHash, 0x01);
        if (parentComponentRecordHash.HasValue && parentComponentRecordHash.Value != 0 && parentComponentRecordHash.Value != GdbHash.Null)
        {
            compEntry.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, parentComponentRecordHash.Value));
            db.Names[GdbView.HashParent] = "parent";
        }
        else if (Engine_RecordNameCatalog.TryGetComponentSchema(trimmedSlot, out ComponentSlotSchema? schema) && schema is not null)
        {
            foreach (ComponentFieldSchema field in schema.DefaultFields)
            {
                uint rawVal = EncodeScalarValue(field.FieldType, field.DefaultValue);
                compEntry.Properties.Add(new GdbPatchProperty(field.FieldHash, field.FieldType, rawVal));
                db.Names[field.FieldHash] = field.FieldName;
            }
        }

        GdbPatchBuilder.UpsertEntry(db, compEntry);
        db.Names[newComponentRecordHash] = compRecordName;
        db.Names[slotHash] = trimmedSlot;

        // 2. Promote/update the node record entry to attach [slotHash -> newComponentRecordHash]:
        GdbPatchEntry? existingNodeEntry = db.Entries.FirstOrDefault(e => e.Id == nodeRecordHash);
        GdbPatchEntry nodeEntry;
        if (existingNodeEntry is not null)
        {
            nodeEntry = existingNodeEntry;
        }
        else if (multi.TryLookup(nodeRecordHash, out _))
        {
            nodeEntry = GdbPatchBuilder.BuildPromotedEntry(multi, nodeRecordHash, Array.Empty<(uint, GdbFieldType, string)>());
        }
        else
        {
            nodeEntry = new GdbPatchEntry(nodeRecordHash, 0x01);
        }

        int existingSlotIdx = nodeEntry.Properties.FindIndex(p => p.NameHash == slotHash);
        var slotProp = new GdbPatchProperty(slotHash, GdbFieldType.Reference, newComponentRecordHash);
        if (existingSlotIdx >= 0)
        {
            nodeEntry.Properties[existingSlotIdx] = slotProp;
        }
        else
        {
            nodeEntry.Properties.Add(slotProp);
        }

        GdbPatchBuilder.UpsertEntry(db, nodeEntry);
        db.Names[nodeRecordHash] = nodeName;

        try
        {
            F2ModPackage.IncrementalSync(pkgPath, new[] { (tuSubPath, GdbWriter.Serialize(db)) }, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        Engine_GameGdb.Invalidate();
        Refii.Equipment.Engine_WeaponIndex.Invalidate();
        Refii.Equipment.Engine_ItemIndex.Invalidate();
        Engine_BnkTextBankLoader.Invalidate();
        BnkMountPlugin.Instance?.ReloadModOverlays();
        RefreshSceneEquipment();
        EditorModExporter.NotifyModContentChanged(modName);

        return true;
    }

    /// <summary>
    /// Removes a mod-attached or mod-overridden direct component from <paramref name="nodeRecordHash"/> so the node
    /// falls back to its inherited component (or base game component if it was a base node).
    /// Also removes any mod overrides on <paramref name="componentRecordHash"/>.
    /// </summary>
    public static bool RemoveOrRevertDirectComponent(
        uint nodeRecordHash,
        uint slotHash,
        uint componentRecordHash,
        string? activeMod,
        out string error)
    {
        error = string.Empty;
        if (nodeRecordHash == 0 || slotHash == 0)
        {
            error = "Invalid node or component slot hash.";
            return false;
        }

        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(modName))
        {
            error = "No active mod selected.";
            return false;
        }

        string modsRoot = ResolveModsRoot();

        string pkgPath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
        if (!File.Exists(pkgPath))
        {
            error = "Active mod package does not exist.";
            return false;
        }

        GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;
        if (baseMulti is null)
        {
            error = "Game GDB databases not loaded.";
            return false;
        }

        string tuSubPath = ResolveGdbTuSubPath(baseMulti, nodeRecordHash, pkgPath, null, nodeRecordHash);
        byte[]? existingBytes = LoadPackageEntry(pkgPath, tuSubPath);
        if (existingBytes == null || existingBytes.Length == 0)
        {
            return true;
        }

        GdbPatchDatabase db = GdbPatchBuilder.LoadOrCreatePatchDatabase(existingBytes);

        // Remove any custom or promoted component record entry in .gdb.tu:
        if (componentRecordHash != 0)
        {
            GdbPatchBuilder.RemoveEntry(db, componentRecordHash);
            db.Names.Remove(componentRecordHash);
        }

        // Update the node record entry in .gdb.tu:
        GdbPatchEntry? nodeEntry = db.Entries.FirstOrDefault(e => e.Id == nodeRecordHash);
        if (nodeEntry is not null)
        {
            if (baseMulti.TryLookup(nodeRecordHash, out GdbCursor baseNode))
            {
                // Base node: restore original slot if it existed in base, otherwise remove the mod-added slot:
                if (baseNode.View.TryFindLocalRaw(baseNode.Record, slotHash, 6, out uint baseCompHash, out _))
                {
                    int idx = nodeEntry.Properties.FindIndex(p => p.NameHash == slotHash);
                    var restoredProp = new GdbPatchProperty(slotHash, GdbFieldType.Reference, baseCompHash);
                    if (idx >= 0)
                    {
                        nodeEntry.Properties[idx] = restoredProp;
                    }
                    else
                    {
                        nodeEntry.Properties.Add(restoredProp);
                    }
                }
                else
                {
                    nodeEntry.Properties.RemoveAll(p => p.NameHash == slotHash);
                }

                // Check if nodeEntry now matches baseNode completely; if so, remove nodeEntry from .gdb.tu:
                bool matchesBase = true;
                var baseFields = baseNode.View.RawFields(baseNode.Record).ToList();
                if (baseFields.Count != nodeEntry.Properties.Count)
                {
                    matchesBase = false;
                }
                else
                {
                    foreach (var (fHash, fType, fRaw) in baseFields)
                    {
                        if (!nodeEntry.Properties.Any(p => p.NameHash == fHash && p.Type == fType && p.Value == fRaw))
                        {
                            matchesBase = false;
                            break;
                        }
                    }
                }

                if (matchesBase)
                {
                    GdbPatchBuilder.RemoveEntry(db, nodeRecordHash);
                    db.Names.Remove(nodeRecordHash);
                }
            }
            else
            {
                // Custom mod node: simply remove the component slot from the custom node:
                nodeEntry.Properties.RemoveAll(p => p.NameHash == slotHash);
            }
        }

        var updates = new List<(string Path, byte[] Bytes)>();
        var deletes = new List<string>();
        if (db.Entries.Count > 0)
        {
            updates.Add((tuSubPath, GdbWriter.Serialize(db)));
        }
        else
        {
            deletes.Add(tuSubPath);
        }

        try
        {
            F2ModPackage.IncrementalSync(pkgPath, updates, deletes);
            PruneUnreferencedModAssets(pkgPath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        Engine_GameGdb.Invalidate();
        Refii.Equipment.Engine_WeaponIndex.Invalidate();
        Refii.Equipment.Engine_ItemIndex.Invalidate();
        Engine_BnkTextBankLoader.Invalidate();
        BnkMountPlugin.Instance?.ReloadModOverlays();
        RefreshSceneEquipment();
        EditorModExporter.NotifyModContentChanged(modName);

        return true;
    }

    /// <summary>
    /// Strips or restores an inherited component on <paramref name="nodeRecordHash"/> using the native Fable II
    /// <c>RemoveComponent</c> (<c>0x9B41D00A</c>) field.
    /// </summary>
    public static bool SetInheritedComponentStripped(
        uint nodeRecordHash,
        string slotName,
        bool stripped,
        string? activeMod,
        out string error)
    {
        error = string.Empty;
        if (nodeRecordHash == 0 || string.IsNullOrWhiteSpace(slotName))
        {
            error = "Invalid node hash or component slot name.";
            return false;
        }

        string trimmedSlot = slotName.Trim();
        uint slotHash = Engine_GameHash.Fnv1(trimmedSlot);

        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(modName))
        {
            error = "No active mod selected.";
            return false;
        }

        string modsRoot = ResolveModsRoot();

        string pkgPath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
        if (!File.Exists(pkgPath))
        {
            Directory.CreateDirectory(modsRoot);
            ModWorkspace.CreateModPackage(modsRoot, modName, out _, out _);
        }

        GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;
        if (baseMulti is null)
        {
            error = "Game GDB databases not loaded.";
            return false;
        }

        GdbMulti multi = BuildActiveModGdbMulti(baseMulti, modName);
        string tuSubPath = ResolveGdbTuSubPath(baseMulti, nodeRecordHash, pkgPath, multi, nodeRecordHash);
        byte[]? existingBytes = LoadPackageEntry(pkgPath, tuSubPath);
        GdbPatchDatabase db = GdbPatchBuilder.LoadOrCreatePatchDatabase(existingBytes);

        GdbPatchEntry? existingNodeEntry = db.Entries.FirstOrDefault(e => e.Id == nodeRecordHash);
        GdbPatchEntry nodeEntry;
        if (existingNodeEntry is not null)
        {
            nodeEntry = existingNodeEntry;
        }
        else if (multi.TryLookup(nodeRecordHash, out _))
        {
            nodeEntry = GdbPatchBuilder.BuildPromotedEntry(multi, nodeRecordHash, Array.Empty<(uint, GdbFieldType, string)>());
        }
        else
        {
            nodeEntry = new GdbPatchEntry(nodeRecordHash, 0x01);
        }

        uint removeFieldHash = Engine_RecordNameCatalog.RemoveComponentFieldHash;
        if (stripped)
        {
            bool alreadyPresent = nodeEntry.Properties.Any(p => p.NameHash == removeFieldHash && p.Value == slotHash);
            if (!alreadyPresent)
            {
                nodeEntry.Properties.Add(new GdbPatchProperty(removeFieldHash, GdbFieldType.String, slotHash));
            }
            db.Names[removeFieldHash] = "RemoveComponent";
            db.Names[slotHash] = trimmedSlot;
            GdbPatchBuilder.UpsertEntry(db, nodeEntry);
        }
        else
        {
            nodeEntry.Properties.RemoveAll(p => p.NameHash == removeFieldHash && p.Value == slotHash);
            if (baseMulti.TryLookup(nodeRecordHash, out GdbCursor baseNode))
            {
                bool matchesBase = true;
                var baseFields = baseNode.View.RawFields(baseNode.Record).ToList();
                if (baseFields.Count != nodeEntry.Properties.Count)
                {
                    matchesBase = false;
                }
                else
                {
                    foreach (var (fHash, fType, fRaw) in baseFields)
                    {
                        if (!nodeEntry.Properties.Any(p => p.NameHash == fHash && p.Type == fType && p.Value == fRaw))
                        {
                            matchesBase = false;
                            break;
                        }
                    }
                }

                if (matchesBase)
                {
                    GdbPatchBuilder.RemoveEntry(db, nodeRecordHash);
                    db.Names.Remove(nodeRecordHash);
                }
                else
                {
                    GdbPatchBuilder.UpsertEntry(db, nodeEntry);
                }
            }
            else
            {
                GdbPatchBuilder.UpsertEntry(db, nodeEntry);
            }
        }

        var updates = new List<(string Path, byte[] Bytes)>();
        var deletes = new List<string>();
        if (db.Entries.Count > 0)
        {
            updates.Add((tuSubPath, GdbWriter.Serialize(db)));
        }
        else
        {
            deletes.Add(tuSubPath);
        }

        try
        {
            F2ModPackage.IncrementalSync(pkgPath, updates, deletes);
            PruneUnreferencedModAssets(pkgPath);
            F2ModPackage.Defragment(pkgPath, out _);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        Engine_GameGdb.Invalidate();
        Refii.Equipment.Engine_WeaponIndex.Invalidate();
        Refii.Equipment.Engine_ItemIndex.Invalidate();
        Engine_BnkTextBankLoader.Invalidate();
        BnkMountPlugin.Instance?.ReloadModOverlays();
        RefreshSceneEquipment();
        EditorModExporter.NotifyModContentChanged(modName);

        return true;
    }

    /// <summary>
    /// Deletes a mod-created custom child node and all of its custom component records from the active mod's <c>.gdb.tu</c>
    /// and cleans up its localized Babel text entries.
    /// </summary>
    public static bool DeleteCustomNode(
        uint nodeRecordHash,
        string? activeMod,
        out string error)
    {
        error = string.Empty;
        if (nodeRecordHash == 0)
        {
            error = "Invalid node hash.";
            return false;
        }

        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(modName))
        {
            error = "No active mod selected.";
            return false;
        }

        GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;
        if (baseMulti is null)
        {
            error = "Game GDB databases not loaded.";
            return false;
        }

        if (baseMulti.TryLookup(nodeRecordHash, out _))
        {
            error = "Cannot delete a base game GDB record.";
            return false;
        }

        string modsRoot = ResolveModsRoot();

        string pkgPath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
        if (!File.Exists(pkgPath))
        {
            error = "Active mod package does not exist.";
            return false;
        }

        var updates = new List<(string Path, byte[] Bytes)>();
        var deletes = new List<string>();
        var removedStringHashes = new HashSet<uint>();
        string? removedRecordName = null;

        foreach (string gdbPath in KnownGdbPaths)
        {
            string tuSubPath = gdbPath + ".tu";
            byte[]? existingBytes = LoadPackageEntry(pkgPath, tuSubPath);
            if (existingBytes == null || existingBytes.Length == 0)
            {
                continue;
            }

            GdbPatchDatabase db = GdbPatchBuilder.LoadOrCreatePatchDatabase(existingBytes);
            GdbPatchEntry? nodeEntry = db.Entries.FirstOrDefault(e => e.Id == nodeRecordHash);
            if (nodeEntry is null)
            {
                continue;
            }

            if (db.Names.TryGetValue(nodeRecordHash, out string? rName))
            {
                removedRecordName = rName;
            }

            // Collect custom component records referenced by this custom node:
            foreach (GdbPatchProperty prop in nodeEntry.Properties)
            {
                if (prop.NameHash != GdbView.HashParent &&
                    prop.Type == GdbFieldType.Reference &&
                    prop.Value != 0 &&
                    !baseMulti.TryLookup(prop.Value, out _))
                {
                    GdbPatchEntry? compEntry = db.Entries.FirstOrDefault(e => e.Id == prop.Value);
                    if (compEntry is not null)
                    {
                        foreach (var cp in compEntry.Properties)
                        {
                            if (cp.Type == GdbFieldType.String && cp.Value != 0)
                            {
                                removedStringHashes.Add(cp.Value);
                            }
                        }
                    }
                    GdbPatchBuilder.RemoveEntry(db, prop.Value);
                    db.Names.Remove(prop.Value);
                }
            }

            GdbPatchBuilder.RemoveEntry(db, nodeRecordHash);
            db.Names.Remove(nodeRecordHash);

            if (db.Entries.Count > 0)
            {
                updates.Add((tuSubPath, GdbWriter.Serialize(db)));
            }
            else
            {
                deletes.Add(tuSubPath);
            }
        }

        // Clean up Babel rows associated with this custom node:
        string relBabel = "language/en-uk/text/book.babel.csv";
        byte[]? existingBabelBytes = LoadPackageEntry(pkgPath, relBabel);
        if (existingBabelBytes != null && existingBabelBytes.Length > 0)
        {
            string nodePrefix = $"MOD_NODE_{nodeRecordHash:X8}_";
            string invPrefix = !string.IsNullOrWhiteSpace(removedRecordName)
                ? $"INV_ITEM_{removedRecordName.ToUpperInvariant()}_"
                : string.Empty;

            List<BabelOverlayRow> rows = BabelOverlay.ParseCsv(Encoding.UTF8.GetString(existingBabelBytes), new List<string>());
            var remaining = new List<BabelOverlayRow>();
            foreach (BabelOverlayRow row in rows)
            {
                uint keyHash = Engine_GameHash.Fnv1(row.Key);
                bool match = removedStringHashes.Contains(keyHash) ||
                             row.Key.StartsWith(nodePrefix, StringComparison.OrdinalIgnoreCase) ||
                             (!string.IsNullOrEmpty(invPrefix) && row.Key.StartsWith(invPrefix, StringComparison.OrdinalIgnoreCase));
                if (!match)
                {
                    remaining.Add(row);
                }
            }

            if (remaining.Count != rows.Count)
            {
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
        }

        try
        {
            F2ModPackage.IncrementalSync(pkgPath, updates, deletes);
            PruneUnreferencedModAssets(pkgPath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        Engine_GameGdb.Invalidate();
        Refii.Equipment.Engine_WeaponIndex.Invalidate();
        Refii.Equipment.Engine_ItemIndex.Invalidate();
        Engine_BnkTextBankLoader.Invalidate();
        BnkMountPlugin.Instance?.ReloadModOverlays();
        RefreshSceneEquipment();
        EditorModExporter.NotifyModContentChanged(modName);

        return true;
    }

    /// <summary>
    /// Returns the set of all record hashes present in the active mod's <c>.gdb.tu</c> files.
    /// Used by the GDB hierarchy view to badge custom and modified nodes and components.
    /// </summary>
    public static HashSet<uint> GetActiveModTuRecordHashes(string? activeMod = null)
    {
        BuildActiveModGdbMulti(activeMod, out _, out _, out HashSet<uint> modifiedRecordHashes, out _);
        return modifiedRecordHashes;
    }

    /// <summary>
    /// Adds or updates a custom scalar property on a component record in the active mod's <c>.gdb.tu</c>.
    /// Enables authoring custom component properties for Lua scripts or adding fields to existing components.
    /// </summary>
    public static bool AddOrUpdateComponentProperty(
        uint componentRecordHash,
        string propertyName,
        GdbFieldType fieldType,
        string value,
        string? activeMod,
        out string error)
    {
        error = string.Empty;
        if (componentRecordHash == 0)
        {
            error = "Invalid component record hash.";
            return false;
        }

        string trimmedProp = propertyName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedProp))
        {
            error = "Property name cannot be empty.";
            return false;
        }

        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(modName))
        {
            error = "No active mod selected.";
            return false;
        }

        string modsRoot = ResolveModsRoot();

        string pkgPath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
        if (!File.Exists(pkgPath))
        {
            Directory.CreateDirectory(modsRoot);
            ModWorkspace.CreateModPackage(modsRoot, modName, out _, out _);
        }

        GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;
        if (baseMulti is null)
        {
            error = "Game GDB databases not loaded.";
            return false;
        }

        GdbMulti multi = BuildActiveModGdbMulti(baseMulti, modName);
        string tuSubPath = ResolveGdbTuSubPath(baseMulti, componentRecordHash, pkgPath, multi, 0);
        byte[]? existingBytes = LoadPackageEntry(pkgPath, tuSubPath);
        GdbPatchDatabase db = GdbPatchBuilder.LoadOrCreatePatchDatabase(existingBytes);

        uint propHash = Engine_GameHash.Fnv1(trimmedProp);
        uint rawVal = EncodeScalarValue(fieldType, value);

        GdbPatchEntry? existingCompEntry = db.Entries.FirstOrDefault(e => e.Id == componentRecordHash);
        GdbPatchEntry compEntry;
        if (existingCompEntry is not null)
        {
            compEntry = existingCompEntry;
        }
        else if (multi.TryLookup(componentRecordHash, out _))
        {
            compEntry = GdbPatchBuilder.BuildPromotedEntry(multi, componentRecordHash, Array.Empty<(uint, GdbFieldType, string)>());
        }
        else
        {
            compEntry = new GdbPatchEntry(componentRecordHash, 0x01);
        }

        int idx = compEntry.Properties.FindIndex(p => p.NameHash == propHash);
        var patchProp = new GdbPatchProperty(propHash, fieldType, rawVal);
        if (idx >= 0)
        {
            compEntry.Properties[idx] = patchProp;
        }
        else
        {
            compEntry.Properties.Add(patchProp);
        }

        GdbPatchBuilder.UpsertEntry(db, compEntry);
        db.Names[propHash] = trimmedProp;
        if (fieldType is (GdbFieldType.String or GdbFieldType.ResourceReference) &&
            !string.IsNullOrWhiteSpace(value) &&
            !value.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            db.Names[rawVal] = value.Trim();
        }

        try
        {
            F2ModPackage.IncrementalSync(pkgPath, new[] { (tuSubPath, GdbWriter.Serialize(db)) }, Array.Empty<string>());
            PruneUnreferencedModAssets(pkgPath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        Engine_GameGdb.Invalidate();
        Refii.Equipment.Engine_WeaponIndex.Invalidate();
        Refii.Equipment.Engine_ItemIndex.Invalidate();
        Engine_BnkTextBankLoader.Invalidate();
        BnkMountPlugin.Instance?.ReloadModOverlays();
        RefreshSceneEquipment();
        EditorModExporter.NotifyModContentChanged(modName);

        return true;
    }

    private static uint EncodeScalarValue(GdbFieldType type, string value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        switch (type)
        {
            case GdbFieldType.Bool:
                return (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) || trimmed == "1") ? 1u : 0u;
            case GdbFieldType.Float:
                return float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
                    ? BitConverter.SingleToUInt32Bits(f)
                    : 0u;
            case GdbFieldType.Int:
            case GdbFieldType.Enum:
                return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)
                    ? unchecked((uint)i)
                    : 0u;
            default:
                if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                    uint.TryParse(trimmed.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
                {
                    return hex;
                }
                return GdbPatchBuilder.HashStringOrAssetPath(trimmed);
        }
    }

    /// <summary>
    /// Removes all overrides targeting this item from the active mod's .f2mod package.
    /// </summary>
    public static void RevertItemOverrides(ItemDetail detail, string? activeMod = null)
    {
        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        string modsRoot = ResolveModsRoot();

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
            PruneUnreferencedModAssets(pkgPath);
            F2ModPackage.Defragment(pkgPath, out _);
        }
        catch (Exception ex)
        {
            SafeWarn($"[mod_values] Failed to auto-sync {Refii.Core.Engine_Title.Active.ModExtension} package on revert for '{modName}': {ex.Message}");
        }

        SafeLog($"[mod_values] Reverted overrides for '{detail.Label}' [0x{detail.RecordHash:X8}] in mod '{modName}' ({Refii.Core.Engine_Title.Active.ModExtension})");

        // Hot-reload in-memory overlays and refresh scene nodes:
        Engine_GameGdb.Invalidate();
        Refii.Equipment.Engine_WeaponIndex.Invalidate();
        Refii.Equipment.Engine_ItemIndex.Invalidate();
        Engine_BnkTextBankLoader.Invalidate();
        BnkMountPlugin.Instance?.ReloadModOverlays();
        RefreshSceneEquipment();
        EditorModExporter.NotifyModContentChanged(modName);
    }

    // =========================================================================
    // GDB Asset Classification, Conversion, Linking & Orphan GC Pipeline
    // =========================================================================

    private static readonly HashSet<string> KnownModelFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ModelFile",
        "GUIScreenMesh",
        "OverrideFemaleModel",
        "MeshFile",
        "StaticMesh",
        "AnimatedMesh",
        "BrokenModelFile",
        "DestroyedModelFile",
    };

    private static readonly HashSet<string> KnownTextureFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "IconGraphic",
        "TextureFile",
        "DiffuseTexture",
        "NormalTexture",
        "SpecularTexture",
        "HudIcon",
        "MapIcon",
    };

    public static AssetFieldKind ClassifyAssetField(
        string? categoryOrSlotName,
        string? propertyName,
        string? currentValue = null)
    {
        string prop = propertyName?.Trim() ?? string.Empty;
        string val = currentValue?.Trim() ?? string.Empty;

        if (KnownModelFieldNames.Contains(prop) ||
            val.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
        {
            return AssetFieldKind.Model;
        }

        if (KnownTextureFieldNames.Contains(prop) ||
            val.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
            val.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            return AssetFieldKind.Texture;
        }

        if (prop.Equals("SkeletonFile", StringComparison.OrdinalIgnoreCase) ||
            val.EndsWith(".skl", StringComparison.OrdinalIgnoreCase))
        {
            return AssetFieldKind.Skeleton;
        }

        if (prop.Equals("PhysicsFile", StringComparison.OrdinalIgnoreCase) ||
            val.EndsWith(".phy", StringComparison.OrdinalIgnoreCase))
        {
            return AssetFieldKind.Physics;
        }

        // Fallback: if this is a GraphicAppearance*MeshComponent and the field is unnamed (0xA0B3B05C == ModelFile):
        if (!string.IsNullOrEmpty(categoryOrSlotName) &&
            categoryOrSlotName.Contains("MeshComponent", StringComparison.OrdinalIgnoreCase) &&
            prop.Equals("0xA0B3B05C", StringComparison.OrdinalIgnoreCase))
        {
            return AssetFieldKind.Model;
        }

        return AssetFieldKind.None;
    }

    public static string[] GetAllowedExtensionsForAssetKind(AssetFieldKind kind) => kind switch
    {
        AssetFieldKind.Model => new[] { ".glb", ".gltf", ".mdl" },
        AssetFieldKind.Texture => new[] { ".png", ".dds", ".tex", ".orm" },
        AssetFieldKind.Skeleton => new[] { ".skl" },
        AssetFieldKind.Physics => new[] { ".phy" },
        _ => Array.Empty<string>(),
    };

    public static bool IsAllowedAssetExtension(AssetFieldKind kind, string? filePath)
    {
        if (kind == AssetFieldKind.None || string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string ext = Path.GetExtension(filePath.Trim());
        if (string.IsNullOrEmpty(ext))
        {
            return false;
        }

        foreach (string allowed in GetAllowedExtensionsForAssetKind(kind))
        {
            if (ext.Equals(allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads an entry directly from the active mod's <c>.f2mod</c> package without invoking any engine resource resolvers.
    /// Safe to call from inside <c>BnkMountPlugin.ResolveBytes</c> without recursion.
    /// </summary>
    public static byte[]? TryReadFromActiveModPackage(string? virtualPath, string? activeMod = null)
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
        {
            return null;
        }

        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(modName))
        {
            return null;
        }

        string pkgPath = Path.Combine(ResolveModsRoot(), modName + ModWorkspace.PackageExtension);
        if (!File.Exists(pkgPath))
        {
            return null;
        }

        byte[]? pkgBytes = LoadPackageEntry(pkgPath, virtualPath.Trim().Replace('\\', '/').TrimStart('/'));
        return pkgBytes is not null && pkgBytes.Length > 0 ? pkgBytes : null;
    }

    public static byte[]? ReadVirtualAssetBytes(
        string? virtualPathOrHex,
        BnkFileSystem? filesystem)
        => ReadVirtualAssetBytes(virtualPathOrHex, null, includeActiveModPackage: true, filesystem);

    public static byte[]? ReadVirtualAssetBytes(
        string? virtualPathOrHex,
        string? activeMod = null,
        bool includeActiveModPackage = true,
        BnkFileSystem? filesystem = null)
    {
        if (string.IsNullOrWhiteSpace(virtualPathOrHex))
        {
            return null;
        }

        string resolvedPath = virtualPathOrHex.Trim();
        if (resolvedPath.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            string suffix = string.Empty;
            ReadOnlySpan<char> hexSpan = resolvedPath.AsSpan(2);
            int dotIdx = hexSpan.IndexOf('.');
            if (dotIdx > 0)
            {
                suffix = hexSpan[dotIdx..].ToString();
                hexSpan = hexSpan[..dotIdx];
            }

            if (uint.TryParse(hexSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rawHash))
            {
                string baseResolved = Engine_PropModelIndex.Resolve(rawHash, Array.Empty<string>())
                                      ?? Engine_TexPathIndex.Resolve(rawHash)
                                      ?? string.Empty;
                resolvedPath = !string.IsNullOrEmpty(baseResolved) ? baseResolved + suffix : string.Empty;
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return null;
        }

        if (includeActiveModPackage)
        {
            byte[]? pkgBytes = TryReadFromActiveModPackage(resolvedPath, activeMod);
            if (pkgBytes is not null && pkgBytes.Length > 0)
            {
                return pkgBytes;
            }
        }

        if (filesystem is not null)
        {
            string normVfs = resolvedPath.Replace('\\', '/').TrimStart('/');
            if (filesystem.TryGetFile(normVfs, out BnkFile vfsFile))
            {
                try
                {
                    byte[] vfsBytes = vfsFile.ReadAllBytes();
                    if (vfsBytes is not null && vfsBytes.Length > 0)
                    {
                        return vfsBytes;
                    }
                }
                catch
                {
                }
            }
        }

        byte[]? meshBytes = Engine_BnkMeshLoader.ReadBytes(resolvedPath);
        if (meshBytes is not null && meshBytes.Length > 0)
        {
            return meshBytes;
        }

        byte[]? texBytes = Engine_BnkTextureLoader.ReadBytes(resolvedPath);
        if (texBytes is not null && texBytes.Length > 0)
        {
            return texBytes;
        }

        return null;
    }

    /// <summary>
    /// Probes for an authored companion PBR ORM texture associated with the given diffuse texture path.
    /// Returns the virtual or disk path of the ORM texture if found, or string.Empty if none exists.
    /// </summary>
    public static string ResolveCompanionOrmPath(
        string? diffusePath,
        string? activeMod = null,
        BnkFileSystem? filesystem = null)
    {
        if (string.IsNullOrWhiteSpace(diffusePath))
        {
            return string.Empty;
        }

        string normalized = diffusePath.Trim().Replace('\\', '/');
        int extIdx = normalized.LastIndexOf('.');
        string stem = extIdx > 0 ? normalized[..extIdx] : normalized;

        string[] suffixes = { "_orm.png", ".orm.png", "_orm.tex.png", "_orm.tex", ".orm.tex", "_orm", ".tex.orm", ".orm" };
        string[] diffSuffixes = { "_diffuse", "_diff", "_d", "_albedo", "_color", "_basecolor", "diffuse" };

        var candidates = new List<string>(suffixes.Length * 2);
        foreach (string s in suffixes)
        {
            candidates.Add(stem + s);
        }

        foreach (string diffSuffix in diffSuffixes)
        {
            if (stem.EndsWith(diffSuffix, StringComparison.OrdinalIgnoreCase))
            {
                string baseStem = stem[..^diffSuffix.Length];
                foreach (string s in suffixes)
                {
                    candidates.Add(baseStem + s);
                }
                break;
            }
        }

        foreach (string candidate in candidates)
        {
            byte[]? b = ReadVirtualAssetBytes(candidate, activeMod, includeActiveModPackage: true, filesystem);
            if (b is not null && b.Length > 0)
            {
                return candidate.Replace('/', '\\');
            }

            string disk = ResolveDiskFilePath(candidate);
            if (File.Exists(disk))
            {
                return disk;
            }
        }

        return string.Empty;
    }

    public static List<ModModelMaterialSlot> GetModelMaterialSlots(
        string? currentModelPath,
        string? vanillaModelPath,
        BnkFileSystem? filesystem)
        => GetModelMaterialSlots(currentModelPath, vanillaModelPath, null, filesystem);

    /// <summary>
    /// Inspects the <c>.mdl</c> file referenced by <paramref name="currentModelPath"/> (and compares against
    /// <paramref name="vanillaModelPath"/>) to return all editable surface material texture slots.
    /// </summary>
    public static List<ModModelMaterialSlot> GetModelMaterialSlots(
        string? currentModelPath,
        string? vanillaModelPath = null,
        string? activeMod = null,
        BnkFileSystem? filesystem = null)
    {
        var result = new List<ModModelMaterialSlot>();
        byte[]? currentMdlBytes = ReadVirtualAssetBytes(currentModelPath, activeMod, includeActiveModPackage: true, filesystem);
        if (currentMdlBytes is null || currentMdlBytes.Length == 0)
        {
            return result;
        }

        IReadOnlyList<MdlMaterialSlotEntry> currentMats = Engine_MdlWriter.ExtractMaterials(currentMdlBytes);
        if (currentMats.Count == 0)
        {
            return result;
        }

        byte[]? vanillaMdlBytes = !string.IsNullOrWhiteSpace(vanillaModelPath)
            ? ReadVirtualAssetBytes(vanillaModelPath, activeMod, includeActiveModPackage: false, filesystem)
            : null;
        IReadOnlyList<MdlMaterialSlotEntry> vanillaMats = vanillaMdlBytes is not null && vanillaMdlBytes.Length > 0
            ? Engine_MdlWriter.ExtractMaterials(vanillaMdlBytes)
            : currentMats;

        (int Index, string Name, Func<MdlMaterial, string> Getter)[] slotDefs =
        {
            (0, "Diffuse", m => m.Diffuse),
            (2, "Normal", m => m.Normal),
            (1, "Specular", m => m.Specular),
            (4, "Emission", m => m.SelfIllumination),
            (3, "EnvironmentMap", m => m.EnvironmentMap),
            (5, "PBR ORM", m => ResolveCompanionOrmPath(m.Diffuse, activeMod, filesystem)),
        };

        for (int i = 0; i < currentMats.Count; i++)
        {
            MdlMaterialSlotEntry curEntry = currentMats[i];
            MdlMaterial vanMat = i < vanillaMats.Count ? vanillaMats[i].Material : curEntry.Material;

            foreach (var (slotIdx, slotName, getter) in slotDefs)
            {
                string curTex = getter(curEntry.Material) ?? string.Empty;
                string vanTex = slotIdx == 5 ? string.Empty : (getter(vanMat) ?? string.Empty);

                // Always expose Diffuse, Normal, Specular, Emission, and PBR ORM so modders can add or replace them;
                // expose EnvironmentMap when non-empty in current or vanilla.
                if (string.IsNullOrWhiteSpace(curTex) && string.IsNullOrWhiteSpace(vanTex) && slotIdx is 3)
                {
                    continue;
                }

                result.Add(new ModModelMaterialSlot
                {
                    FlatMaterialIndex = curEntry.FlatIndex,
                    MeshName = string.IsNullOrWhiteSpace(curEntry.MeshName) ? $"Mesh_{curEntry.MeshIndex}" : curEntry.MeshName,
                    SubMaterialIndex = curEntry.MaterialIndex,
                    SlotIndex = slotIdx,
                    SlotName = slotName,
                    CurrentTexturePath = curTex,
                    VanillaTexturePath = vanTex,
                });
            }
        }

        return result;
    }

    public static string SanitizeAssetSlug(string? raw, string fallback = "item")
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (c is ' ' or '-' or '.')
            {
                sb.Append('_');
            }
        }

        string cleaned = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(cleaned) ? fallback : cleaned;
    }

    private static string ResolveDiskFilePath(string resOrDiskPath)
    {
        string trimmed = resOrDiskPath.Trim();
        if (trimmed.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("user://", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsTestHost())
            {
                try
                {
                    return ProjectSettings.GlobalizePath(trimmed);
                }
                catch
                {
                }
            }

            string rel = trimmed.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
                ? trimmed["res://".Length..]
                : trimmed["user://".Length..];
            return Path.Combine(Engine_AppRoot.SafeGetProjectDir(), rel.Replace('/', Path.DirectorySeparatorChar));
        }

        return trimmed;
    }

    private static bool TryConvertImageFileToTex(
        string diskPath,
        out byte[] compiledTex,
        out byte[]? ormBytes,
        out string error,
        bool isNormalMap = false)
    {
        compiledTex = Array.Empty<byte>();
        ormBytes = null;
        error = string.Empty;



        if (IsTestHost())
        {
            byte[] testRgba = new byte[4 * 4 * 4];
            for (int i = 0; i < testRgba.Length; i++)
            {
                testRgba[i] = 255;
            }
            return Engine_TexWriter.TryWrite(testRgba, 4, 4, isNormalMap ? TexSurfaceFormat.Bc5 : TexSurfaceFormat.Rgba8, out compiledTex, out error);
        }

        string ext = Path.GetExtension(diskPath).ToLowerInvariant();
        if (ext == ".png" && !isNormalMap)
        {
            byte[] pngBytes = File.ReadAllBytes(diskPath);
            var conv = new ImageConversion { Target = diskPath };
            if (!ModImageOverlays.TryConvert(pngBytes, out compiledTex, conv))
            {
                error = conv.Error;
                return false;
            }
            return true;
        }

        Image? img = Image.LoadFromFile(diskPath);
        if (img is null)
        {
            error = $"Could not load image file '{Path.GetFileName(diskPath)}'.";
            return false;
        }

        if (img.IsCompressed())
        {
            img.Decompress();
        }
        if (img.GetFormat() != Image.Format.Rgba8)
        {
            img.Convert(Image.Format.Rgba8);
        }

        byte[] rgba = img.GetData();
        int w = img.GetWidth();
        int h = img.GetHeight();
        TexSurfaceFormat fmt = isNormalMap ? TexSurfaceFormat.Bc5 : Engine_TexWriter.ChooseFormat(rgba, w, h);
        return Engine_TexWriter.TryWrite(rgba, w, h, fmt, out compiledTex, out error);
    }

    /// <summary>
    /// Converts raw image bytes (PNG, JPEG, or WebP) extracted from a <c>.glb</c>/<c>.gltf</c> container into a compiled <c>.tex</c> binary.
    /// </summary>
    public static bool TryConvertImageBytesToTex(
        byte[] imageBytes,
        bool isNormalMap,
        out byte[] compiledTex,
        out string error)
    {
        compiledTex = Array.Empty<byte>();
        error = string.Empty;

        if (imageBytes is null || imageBytes.Length == 0)
        {
            error = "Image payload is empty.";
            return false;
        }

        if (IsTestHost())
        {
            byte[] testRgba = new byte[4 * 4 * 4];
            for (int i = 0; i < testRgba.Length; i++)
            {
                testRgba[i] = 255;
            }
            return Engine_TexWriter.TryWrite(
                testRgba, 4, 4,
                isNormalMap ? TexSurfaceFormat.Bc5 : TexSurfaceFormat.Rgba8,
                out compiledTex, out error);
        }

        bool isPng = imageBytes.Length >= 8 &&
                     imageBytes[0] == 0x89 && imageBytes[1] == 0x50 &&
                     imageBytes[2] == 0x4E && imageBytes[3] == 0x47;
        if (isPng && !isNormalMap)
        {
            var conv = new ImageConversion { Target = "embedded.png" };
            if (ModImageOverlays.TryConvert(imageBytes, out compiledTex, conv))
            {
                return true;
            }
        }

        var img = new Image();
        Error loadErr;
        if (isPng)
        {
            loadErr = img.LoadPngFromBuffer(imageBytes);
        }
        else if (imageBytes.Length >= 3 && imageBytes[0] == 0xFF && imageBytes[1] == 0xD8 && imageBytes[2] == 0xFF)
        {
            loadErr = img.LoadJpgFromBuffer(imageBytes);
        }
        else if (imageBytes.Length >= 12 &&
                 imageBytes[0] == 'R' && imageBytes[1] == 'I' && imageBytes[2] == 'F' && imageBytes[3] == 'F' &&
                 imageBytes[8] == 'W' && imageBytes[9] == 'E' && imageBytes[10] == 'B' && imageBytes[11] == 'P')
        {
            loadErr = img.LoadWebpFromBuffer(imageBytes);
        }
        else
        {
            loadErr = img.LoadPngFromBuffer(imageBytes);
            if (loadErr != Error.Ok)
            {
                loadErr = img.LoadJpgFromBuffer(imageBytes);
            }
            if (loadErr != Error.Ok)
            {
                loadErr = img.LoadWebpFromBuffer(imageBytes);
            }
        }

        if (loadErr != Error.Ok)
        {
            error = $"Could not decode embedded glTF image ({loadErr}).";
            return false;
        }

        if (img.IsCompressed())
        {
            img.Decompress();
        }
        if (img.GetFormat() != Image.Format.Rgba8)
        {
            img.Convert(Image.Format.Rgba8);
        }

        byte[] rgba = img.GetData();
        int w = img.GetWidth();
        int h = img.GetHeight();
        TexSurfaceFormat fmt = isNormalMap ? TexSurfaceFormat.Bc5 : Engine_TexWriter.ChooseFormat(rgba, w, h);
        return Engine_TexWriter.TryWrite(rgba, w, h, fmt, out compiledTex, out error);
    }

    private sealed class GltfExtractedMaterial
    {
        public byte[]? BaseColorBytes { get; set; }
        public Color BaseColorFactor { get; set; } = Colors.White;
        public byte[]? NormalBytes { get; set; }
        public float NormalScale { get; set; } = 1.0f;
        public byte[]? EmissiveBytes { get; set; }
        public Color EmissiveFactor { get; set; } = Colors.Black;
        public float EmissiveStrength { get; set; } = 1.0f;

        // PBR maps:
        public byte[]? MetallicRoughnessBytes { get; set; }
        public float MetallicFactor { get; set; } = 1.0f;
        public float RoughnessFactor { get; set; } = 1.0f;
        public byte[]? OcclusionBytes { get; set; }
        public float OcclusionStrength { get; set; } = 1.0f;

        // Final synthesized ORM PNG bytes (R=AO, G=Roughness, B=Metallic, A=Height/255)
        public byte[]? OrmBytes { get; set; }
        public bool HasPbr { get; set; }
    }

    private static uint CalculateCrc32(byte[] typeBytes, byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in typeBytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : (crc >> 1);
            }
        }
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : (crc >> 1);
            }
        }
        return ~crc;
    }

    private static void WritePngChunk(Stream stream, string type, byte[] data)
    {
        byte[] lenBytes = BitConverter.GetBytes((uint)data.Length);
        if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
        stream.Write(lenBytes);

        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        if (data.Length > 0)
        {
            stream.Write(data);
        }

        uint crc = CalculateCrc32(typeBytes, data);
        byte[] crcBytes = BitConverter.GetBytes(crc);
        if (BitConverter.IsLittleEndian) Array.Reverse(crcBytes);
        stream.Write(crcBytes);
    }

    public static byte[] CreateMinimalPng(byte r, byte g, byte b, byte a)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        byte[] ihdrData = { 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0 };
        WritePngChunk(ms, "IHDR", ihdrData);

        byte[] scanline = { 0, r, g, b, a };
        using (var idatMs = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(idatMs, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(scanline);
            }
            WritePngChunk(ms, "IDAT", idatMs.ToArray());
        }

        WritePngChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static Image? TryLoadImageFromBytes(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        if (IsTestHost())
        {
            var testImg = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
            testImg.Fill(Colors.White);
            return testImg;
        }

        var img = new Image();
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            if (img.LoadPngFromBuffer(bytes) == Error.Ok)
            {
                if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
                return img;
            }
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            if (img.LoadJpgFromBuffer(bytes) == Error.Ok)
            {
                if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
                return img;
            }
        }

        if (img.LoadWebpFromBuffer(bytes) == Error.Ok)
        {
            if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
            return img;
        }

        return null;
    }

    private static void SynthesizeOrmIfNeeded(GltfExtractedMaterial mat)
    {
        bool hasMrTex = mat.MetallicRoughnessBytes is not null && mat.MetallicRoughnessBytes.Length > 0;
        bool hasOccTex = mat.OcclusionBytes is not null && mat.OcclusionBytes.Length > 0;

        if (!hasMrTex && !hasOccTex)
        {
            mat.HasPbr = false;
            return;
        }

        mat.HasPbr = true;

        if (IsTestHost())
        {
            byte r = (byte)Math.Clamp((int)Math.Round((hasOccTex ? 255 : 255) * mat.OcclusionStrength), 0, 255);
            byte g = (byte)Math.Clamp((int)Math.Round(mat.RoughnessFactor * 255), 0, 255);
            byte b = (byte)Math.Clamp((int)Math.Round(mat.MetallicFactor * 255), 0, 255);
            mat.OrmBytes = CreateMinimalPng(r, g, b, 255);
            return;
        }

        Image? mrImg = hasMrTex ? TryLoadImageFromBytes(mat.MetallicRoughnessBytes!) : null;
        Image? occImg = hasOccTex ? TryLoadImageFromBytes(mat.OcclusionBytes!) : null;

        int width = mrImg?.GetWidth() ?? occImg?.GetWidth() ?? 16;
        int height = mrImg?.GetHeight() ?? occImg?.GetHeight() ?? 16;

        if (mrImg is not null)
        {
            if (mrImg.IsCompressed()) mrImg.Decompress();
            if (mrImg.GetFormat() != Image.Format.Rgba8) mrImg.Convert(Image.Format.Rgba8);
            if (mrImg.GetWidth() != width || mrImg.GetHeight() != height) mrImg.Resize(width, height);
        }

        if (occImg is not null)
        {
            if (occImg.IsCompressed()) occImg.Decompress();
            if (occImg.GetFormat() != Image.Format.Rgba8) occImg.Convert(Image.Format.Rgba8);
            if (occImg.GetWidth() != width || occImg.GetHeight() != height) occImg.Resize(width, height);
        }

        byte[]? mrData = mrImg?.GetData();
        byte[]? occData = occImg?.GetData();
        byte[] outData = new byte[width * height * 4];

        float occStr = Math.Clamp(mat.OcclusionStrength, 0.0f, 1.0f);
        float roughFac = Math.Clamp(mat.RoughnessFactor, 0.0f, 1.0f);
        float metalFac = Math.Clamp(mat.MetallicFactor, 0.0f, 1.0f);

        for (int p = 0; p < outData.Length; p += 4)
        {
            byte aoVal = 255;
            if (occData is not null && p < occData.Length)
            {
                aoVal = (byte)Math.Clamp((int)Math.Round(occData[p] * occStr + 255 * (1.0f - occStr)), 0, 255);
            }
            else if (mrData is not null && mat.MetallicRoughnessBytes == mat.OcclusionBytes && p < mrData.Length)
            {
                aoVal = (byte)Math.Clamp((int)Math.Round(mrData[p] * occStr + 255 * (1.0f - occStr)), 0, 255);
            }

            byte roughVal = (byte)Math.Clamp((int)Math.Round(roughFac * 255), 0, 255);
            if (mrData is not null && p + 1 < mrData.Length)
            {
                roughVal = (byte)Math.Clamp((int)Math.Round(mrData[p + 1] * roughFac), 0, 255);
            }

            byte metalVal = (byte)Math.Clamp((int)Math.Round(metalFac * 255), 0, 255);
            if (mrData is not null && p + 2 < mrData.Length)
            {
                metalVal = (byte)Math.Clamp((int)Math.Round(mrData[p + 2] * metalFac), 0, 255);
            }

            byte aVal = (mrData is not null && p + 3 < mrData.Length) ? mrData[p + 3] : (byte)255;

            outData[p] = aoVal;
            outData[p + 1] = roughVal;
            outData[p + 2] = metalVal;
            outData[p + 3] = aVal;
        }

        var finalImg = Image.CreateFromData(width, height, false, Image.Format.Rgba8, outData);
        mat.OrmBytes = finalImg.SavePngToBuffer();
    }

    private static bool TryExtractGltfMaterials(
        byte[] gltfBytes,
        string? baseDir,
        out List<GltfExtractedMaterial> extractedMaterials)
    {
        extractedMaterials = new List<GltfExtractedMaterial>();
        if (gltfBytes is null || gltfBytes.Length < 12)
        {
            return false;
        }

        ReadOnlyMemory<byte> jsonSlice = default;
        ReadOnlyMemory<byte> binSlice = default;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(gltfBytes.AsSpan(0, 4));
        if (magic == 0x46546C67u) // "glTF" binary container (.glb)
        {
            if (gltfBytes.Length < 20)
            {
                return false;
            }

            int pos = 12;
            while (pos + 8 <= gltfBytes.Length)
            {
                uint chunkLen = BinaryPrimitives.ReadUInt32LittleEndian(gltfBytes.AsSpan(pos, 4));
                uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(gltfBytes.AsSpan(pos + 4, 4));
                pos += 8;

                if (chunkLen > (uint)(gltfBytes.Length - pos))
                {
                    break;
                }

                if (chunkType == 0x4E4F534Au && jsonSlice.IsEmpty) // "JSON"
                {
                    jsonSlice = gltfBytes.AsMemory(pos, (int)chunkLen);
                }
                else if (chunkType == 0x004E4942u && binSlice.IsEmpty) // "BIN\0"
                {
                    binSlice = gltfBytes.AsMemory(pos, (int)chunkLen);
                }

                pos += (int)chunkLen;
            }
        }
        else
        {
            jsonSlice = gltfBytes.AsMemory();
        }

        if (jsonSlice.IsEmpty)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonSlice);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("materials", out JsonElement materialsEl) ||
                materialsEl.ValueKind != JsonValueKind.Array ||
                materialsEl.GetArrayLength() == 0)
            {
                return false;
            }

            JsonElement texturesEl = root.TryGetProperty("textures", out JsonElement tEl) && tEl.ValueKind == JsonValueKind.Array ? tEl : default;
            JsonElement imagesEl = root.TryGetProperty("images", out JsonElement iEl) && iEl.ValueKind == JsonValueKind.Array ? iEl : default;
            JsonElement bufferViewsEl = root.TryGetProperty("bufferViews", out JsonElement bvEl) && bvEl.ValueKind == JsonValueKind.Array ? bvEl : default;

            byte[]? ResolveTextureImageBytes(int texIndex)
            {
                if (texturesEl.ValueKind != JsonValueKind.Array ||
                    texIndex < 0 || texIndex >= texturesEl.GetArrayLength())
                {
                    return null;
                }

                JsonElement texObj = texturesEl[texIndex];
                int imgIndex = -1;
                if (texObj.TryGetProperty("source", out JsonElement srcEl) && srcEl.TryGetInt32(out int sIdx))
                {
                    imgIndex = sIdx;
                }
                else if (texObj.TryGetProperty("extensions", out JsonElement extEl) &&
                         extEl.ValueKind == JsonValueKind.Object &&
                         extEl.TryGetProperty("EXT_texture_webp", out JsonElement webpEl) &&
                         webpEl.ValueKind == JsonValueKind.Object &&
                         webpEl.TryGetProperty("source", out JsonElement webpSrc) &&
                         webpSrc.TryGetInt32(out int wIdx))
                {
                    imgIndex = wIdx;
                }

                if (imagesEl.ValueKind != JsonValueKind.Array ||
                    imgIndex < 0 || imgIndex >= imagesEl.GetArrayLength())
                {
                    return null;
                }

                JsonElement imgObj = imagesEl[imgIndex];
                if (imgObj.TryGetProperty("bufferView", out JsonElement bvIdxEl) &&
                    bvIdxEl.TryGetInt32(out int bvIdx) &&
                    bufferViewsEl.ValueKind == JsonValueKind.Array &&
                    bvIdx >= 0 && bvIdx < bufferViewsEl.GetArrayLength() &&
                    !binSlice.IsEmpty)
                {
                    JsonElement bvObj = bufferViewsEl[bvIdx];
                    int byteOffset = bvObj.TryGetProperty("byteOffset", out JsonElement boEl) && boEl.TryGetInt32(out int bo) ? bo : 0;
                    int byteLength = bvObj.TryGetProperty("byteLength", out JsonElement blEl) && blEl.TryGetInt32(out int bl) ? bl : 0;
                    if (byteOffset >= 0 && byteLength > 0 && byteOffset + byteLength <= binSlice.Length)
                    {
                        return binSlice.Slice(byteOffset, byteLength).ToArray();
                    }
                }

                if (imgObj.TryGetProperty("uri", out JsonElement uriEl) && uriEl.ValueKind == JsonValueKind.String)
                {
                    string? uri = uriEl.GetString();
                    if (!string.IsNullOrWhiteSpace(uri))
                    {
                        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        {
                            int commaIdx = uri.IndexOf(',');
                            if (commaIdx > 0 && commaIdx + 1 < uri.Length)
                            {
                                try
                                {
                                    return Convert.FromBase64String(uri[(commaIdx + 1)..]);
                                }
                                catch
                                {
                                }
                            }
                        }
                        else if (!string.IsNullOrEmpty(baseDir))
                        {
                            string candidate = Path.Combine(baseDir, Uri.UnescapeDataString(uri));
                            if (File.Exists(candidate))
                            {
                                try
                                {
                                    return File.ReadAllBytes(candidate);
                                }
                                catch
                                {
                                }
                            }
                        }
                    }
                }

                return null;
            }

            var allMaterials = new List<GltfExtractedMaterial>(materialsEl.GetArrayLength());
            foreach (JsonElement matEl in materialsEl.EnumerateArray())
            {
                var extracted = new GltfExtractedMaterial();

                if (matEl.TryGetProperty("pbrMetallicRoughness", out JsonElement pbrEl) &&
                    pbrEl.ValueKind == JsonValueKind.Object)
                {
                    if (pbrEl.TryGetProperty("baseColorTexture", out JsonElement bctEl) &&
                        bctEl.ValueKind == JsonValueKind.Object &&
                        bctEl.TryGetProperty("index", out JsonElement bctIdxEl) &&
                        bctIdxEl.TryGetInt32(out int bctIdx))
                    {
                        extracted.BaseColorBytes = ResolveTextureImageBytes(bctIdx);
                    }

                    if (pbrEl.TryGetProperty("baseColorFactor", out JsonElement bcfEl) &&
                        bcfEl.ValueKind == JsonValueKind.Array && bcfEl.GetArrayLength() >= 3)
                    {
                        float r = bcfEl[0].GetSingle();
                        float g = bcfEl[1].GetSingle();
                        float b = bcfEl[2].GetSingle();
                        float a = bcfEl.GetArrayLength() > 3 ? bcfEl[3].GetSingle() : 1.0f;
                        extracted.BaseColorFactor = new Color(r, g, b, a);
                    }

                    if (pbrEl.TryGetProperty("metallicRoughnessTexture", out JsonElement mrEl) &&
                        mrEl.ValueKind == JsonValueKind.Object &&
                        mrEl.TryGetProperty("index", out JsonElement mrIdxEl) &&
                        mrIdxEl.TryGetInt32(out int mrIdx))
                    {
                        extracted.MetallicRoughnessBytes = ResolveTextureImageBytes(mrIdx);
                    }

                    if (pbrEl.TryGetProperty("metallicFactor", out JsonElement mfEl) && mfEl.TryGetSingle(out float mf))
                    {
                        extracted.MetallicFactor = mf;
                    }

                    if (pbrEl.TryGetProperty("roughnessFactor", out JsonElement rfEl) && rfEl.TryGetSingle(out float rf))
                    {
                        extracted.RoughnessFactor = rf;
                    }
                }

                if (matEl.TryGetProperty("occlusionTexture", out JsonElement occEl) &&
                    occEl.ValueKind == JsonValueKind.Object &&
                    occEl.TryGetProperty("index", out JsonElement occIdxEl) &&
                    occIdxEl.TryGetInt32(out int occIdx))
                {
                    extracted.OcclusionBytes = ResolveTextureImageBytes(occIdx);
                    if (occEl.TryGetProperty("strength", out JsonElement strEl) && strEl.TryGetSingle(out float occStr))
                    {
                        extracted.OcclusionStrength = occStr;
                    }
                }

                if (extracted.BaseColorBytes is null &&
                    matEl.TryGetProperty("extensions", out JsonElement matExtEl) &&
                    matExtEl.ValueKind == JsonValueKind.Object)
                {
                    if (matExtEl.TryGetProperty("KHR_materials_pbrSpecularGlossiness", out JsonElement specGlossEl) &&
                        specGlossEl.ValueKind == JsonValueKind.Object)
                    {
                        if (specGlossEl.TryGetProperty("diffuseTexture", out JsonElement diffTexEl) &&
                            diffTexEl.ValueKind == JsonValueKind.Object &&
                            diffTexEl.TryGetProperty("index", out JsonElement diffIdxEl) &&
                            diffIdxEl.TryGetInt32(out int diffIdx))
                        {
                            extracted.BaseColorBytes = ResolveTextureImageBytes(diffIdx);
                        }

                        if (specGlossEl.TryGetProperty("specularGlossinessTexture", out JsonElement sgTexEl) &&
                            sgTexEl.ValueKind == JsonValueKind.Object &&
                            sgTexEl.TryGetProperty("index", out JsonElement sgIdxEl) &&
                            sgIdxEl.TryGetInt32(out int sgIdx))
                        {
                            extracted.MetallicRoughnessBytes = ResolveTextureImageBytes(sgIdx);
                        }
                    }
                }

                if (matEl.TryGetProperty("normalTexture", out JsonElement normEl) &&
                    normEl.ValueKind == JsonValueKind.Object &&
                    normEl.TryGetProperty("index", out JsonElement normIdxEl) &&
                    normIdxEl.TryGetInt32(out int normIdx))
                {
                    extracted.NormalBytes = ResolveTextureImageBytes(normIdx);
                    if (normEl.TryGetProperty("scale", out JsonElement nScaleEl) && nScaleEl.TryGetSingle(out float nScale))
                    {
                        extracted.NormalScale = nScale;
                    }
                }

                if (matEl.TryGetProperty("emissiveTexture", out JsonElement emisEl) &&
                    emisEl.ValueKind == JsonValueKind.Object &&
                    emisEl.TryGetProperty("index", out JsonElement emisIdxEl) &&
                    emisIdxEl.TryGetInt32(out int emisIdx))
                {
                    extracted.EmissiveBytes = ResolveTextureImageBytes(emisIdx);
                }

                if (matEl.TryGetProperty("emissiveFactor", out JsonElement efEl) &&
                    efEl.ValueKind == JsonValueKind.Array && efEl.GetArrayLength() >= 3)
                {
                    extracted.EmissiveFactor = new Color(efEl[0].GetSingle(), efEl[1].GetSingle(), efEl[2].GetSingle());
                }

                if (matEl.TryGetProperty("extensions", out JsonElement extProps) &&
                    extProps.ValueKind == JsonValueKind.Object &&
                    extProps.TryGetProperty("KHR_materials_emissive_strength", out JsonElement emStrEl) &&
                    emStrEl.ValueKind == JsonValueKind.Object &&
                    emStrEl.TryGetProperty("emissiveStrength", out JsonElement esVal) &&
                    esVal.TryGetSingle(out float es))
                {
                    extracted.EmissiveStrength = es;
                }

                SynthesizeOrmIfNeeded(extracted);
                allMaterials.Add(extracted);
            }

            if (root.TryGetProperty("meshes", out JsonElement meshesEl) &&
                meshesEl.ValueKind == JsonValueKind.Array &&
                meshesEl.GetArrayLength() > 0)
            {
                foreach (JsonElement meshEl in meshesEl.EnumerateArray())
                {
                    if (meshEl.TryGetProperty("primitives", out JsonElement primsEl) &&
                        primsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement primEl in primsEl.EnumerateArray())
                        {
                            if (primEl.TryGetProperty("material", out JsonElement mIdxEl) &&
                                mIdxEl.TryGetInt32(out int mIdx) &&
                                mIdx >= 0 && mIdx < allMaterials.Count)
                            {
                                extractedMaterials.Add(allMaterials[mIdx]);
                            }
                        }
                    }
                }
            }

            if (extractedMaterials.Count == 0)
            {
                extractedMaterials.AddRange(allMaterials);
            }

            return extractedMaterials.Any(m =>
                m.BaseColorBytes is not null ||
                m.NormalBytes is not null ||
                m.EmissiveBytes is not null ||
                m.OrmBytes is not null);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts any embedded or referenced surface textures from <paramref name="gltfBytes"/>, converts them to <c>.tex</c>,
    /// appends them to <paramref name="packageAdditions"/>, registers their paths in <see cref="Engine_TexPathIndex"/>,
    /// and rewrites <paramref name="compiledMdl"/>'s material table so dropping a <c>.glb</c>/<c>.gltf</c> updates both mesh and textures.
    /// </summary>
    public static bool TryExtractAndApplyGltfEmbeddedTextures(
        byte[] gltfBytes,
        string? baseDir,
        byte[] compiledMdl,
        string modSlug,
        string nodeSlug,
        List<(string Path, byte[] Bytes)> packageAdditions,
        out byte[] patchedMdl)
    {
        patchedMdl = compiledMdl;
        if (!TryExtractGltfMaterials(gltfBytes, baseDir, out List<GltfExtractedMaterial> gltfMaterials) ||
            gltfMaterials.Count == 0)
        {
            return false;
        }

        IReadOnlyList<MdlMaterialSlotEntry> mdlMaterials = Engine_MdlWriter.ExtractMaterials(compiledMdl);
        if (mdlMaterials.Count == 0)
        {
            return false;
        }

        if (mdlMaterials.Count < gltfMaterials.Count)
        {
            if (Engine_MdlWriter.TryEnsureMaterialSlots(compiledMdl, gltfMaterials.Count, out byte[] expMdl, out _))
            {
                compiledMdl = expMdl;
                mdlMaterials = Engine_MdlWriter.ExtractMaterials(compiledMdl);
            }
        }

        IReadOnlyList<MdlMaterialSlotEntry> targetMdlSlots = mdlMaterials;
        if (mdlMaterials.Count > gltfMaterials.Count)
        {
            var nonEmpty = mdlMaterials
                .Where(m => !string.IsNullOrWhiteSpace(m.Material.Diffuse) ||
                            !string.IsNullOrWhiteSpace(m.Material.Normal) ||
                            !string.IsNullOrWhiteSpace(m.Material.Specular) ||
                            !string.IsNullOrWhiteSpace(m.Material.SelfIllumination))
                .ToList();
            if (nonEmpty.Count == gltfMaterials.Count)
            {
                targetMdlSlots = nonEmpty;
            }
        }

        var patches = new Dictionary<int, MdlMaterialTexturePatch>();
        int pairCount = Math.Min(targetMdlSlots.Count, gltfMaterials.Count);

        for (int i = 0; i < pairCount; i++)
        {
            MdlMaterialSlotEntry mdlSlot = targetMdlSlots[i];
            int flatIdx = mdlSlot.FlatIndex;
            GltfExtractedMaterial gltfMat = gltfMaterials[i];
            string? newDiffusePath = null;
            string? newNormalPath = null;
            string? newEmissivePath = null;

            byte[]? diffuseImg = gltfMat.BaseColorBytes;
            byte[]? emissiveImg = gltfMat.EmissiveBytes;

            if (emissiveImg is null && (gltfMat.EmissiveFactor.R > 0f || gltfMat.EmissiveFactor.G > 0f || gltfMat.EmissiveFactor.B > 0f))
            {
                // Emissive color/factor without dedicated image map (e.g. glowing runes/lights defined solely by color factor):
                float strength = gltfMat.EmissiveStrength > 0f ? gltfMat.EmissiveStrength : 1.0f;
                byte er = (byte)Math.Clamp((int)Math.Round(gltfMat.EmissiveFactor.R * strength * 255f), 0, 255);
                byte eg = (byte)Math.Clamp((int)Math.Round(gltfMat.EmissiveFactor.G * strength * 255f), 0, 255);
                byte eb = (byte)Math.Clamp((int)Math.Round(gltfMat.EmissiveFactor.B * strength * 255f), 0, 255);
                emissiveImg = CreateMinimalPng(er, eg, eb, 255);
            }

            if (diffuseImg is null && (gltfMat.OrmBytes is not null || emissiveImg is not null))
            {
                // Untextured PBR or emissive material: synthesize a solid diffuse texture from BaseColorFactor so companion ORM and emission bind cleanly
                byte r = (byte)Math.Clamp((int)Math.Round(gltfMat.BaseColorFactor.R * 255), 0, 255);
                byte g = (byte)Math.Clamp((int)Math.Round(gltfMat.BaseColorFactor.G * 255), 0, 255);
                byte b = (byte)Math.Clamp((int)Math.Round(gltfMat.BaseColorFactor.B * 255), 0, 255);
                byte a = (byte)Math.Clamp((int)Math.Round(gltfMat.BaseColorFactor.A * 255), 0, 255);
                byte[] solidPng = CreateMinimalPng(r, g, b, a);
                if (TryConvertImageBytesToTex(solidPng, isNormalMap: false, out byte[] diffuseTex, out _))
                {
                    string relTexPath = $"mods/{modSlug}/textures/{nodeSlug}/mat{flatIdx}_diffuse.tex";
                    newDiffusePath = relTexPath.Replace('/', '\\');
                    packageAdditions.Add((relTexPath, diffuseTex));
                    Engine_TexPathIndex.RegisterPath(newDiffusePath);
                }
            }

            if (gltfMat.OrmBytes is not null && gltfMat.OrmBytes.Length > 0)
            {
                string relOrmPath = $"mods/{modSlug}/textures/{nodeSlug}/mat{flatIdx}_diffuse_orm.png";
                string relOrmAlias = $"mods/{modSlug}/textures/{nodeSlug}/mat{flatIdx}_orm.png";
                packageAdditions.Add((relOrmPath, gltfMat.OrmBytes));
                packageAdditions.Add((relOrmAlias, gltfMat.OrmBytes));
                Engine_TexPathIndex.RegisterPath(relOrmPath.Replace('/', '\\'));
                AssetHashIndex.RegisterPath(relOrmPath.Replace('/', '\\'));
                Engine_TexPathIndex.RegisterPath(relOrmAlias.Replace('/', '\\'));
                AssetHashIndex.RegisterPath(relOrmAlias.Replace('/', '\\'));
            }

            if (diffuseImg is not null &&
                TryConvertImageBytesToTex(diffuseImg, isNormalMap: false, out byte[] diffuseTexParsed, out _))
            {
                string relTexPath = $"mods/{modSlug}/textures/{nodeSlug}/mat{flatIdx}_diffuse.tex";
                newDiffusePath = relTexPath.Replace('/', '\\');
                packageAdditions.Add((relTexPath, diffuseTexParsed));
                Engine_TexPathIndex.RegisterPath(newDiffusePath);
            }

            if (gltfMat.NormalBytes is not null &&
                TryConvertImageBytesToTex(gltfMat.NormalBytes, isNormalMap: true, out byte[] normalTex, out _))
            {
                string relTexPath = $"mods/{modSlug}/textures/{nodeSlug}/mat{flatIdx}_normal.tex";
                newNormalPath = relTexPath.Replace('/', '\\');
                packageAdditions.Add((relTexPath, normalTex));
                Engine_TexPathIndex.RegisterPath(newNormalPath);
            }

            if (emissiveImg is not null &&
                TryConvertImageBytesToTex(emissiveImg, isNormalMap: false, out byte[] emissiveTex, out _))
            {
                string relTexPath = $"mods/{modSlug}/textures/{nodeSlug}/mat{flatIdx}_emission.tex";
                string relLegacyPath = $"mods/{modSlug}/textures/{nodeSlug}/mat{flatIdx}_selfillumination.tex";
                newEmissivePath = relTexPath.Replace('/', '\\');
                packageAdditions.Add((relTexPath, emissiveTex));
                packageAdditions.Add((relLegacyPath, emissiveTex));
                Engine_TexPathIndex.RegisterPath(newEmissivePath);
                AssetHashIndex.RegisterPath(newEmissivePath);
                Engine_TexPathIndex.RegisterPath(relLegacyPath.Replace('/', '\\'));
                AssetHashIndex.RegisterPath(relLegacyPath.Replace('/', '\\'));
            }

            if (newDiffusePath is not null || newNormalPath is not null || newEmissivePath is not null || gltfMat.OrmBytes is not null)
            {
                patches[flatIdx] = new MdlMaterialTexturePatch
                {
                    Diffuse = newDiffusePath,
                    Normal = newNormalPath,
                    SelfIllumination = newEmissivePath,
                };
            }
        }

        if (patches.Count == 0 && packageAdditions.Count == 0)
        {
            return false;
        }

        if (patches.Count > 0 && Engine_MdlWriter.TryRewriteMaterialTextures(compiledMdl, patches, out byte[] rewritten, out _))
        {
            patchedMdl = rewritten;
            return true;
        }

        return packageAdditions.Count > 0;
    }

    /// <summary>
    /// Converts a creator asset file (<c>.glb</c>/<c>.gltf</c>/<c>.mdl</c> or <c>.png</c>/<c>.dds</c>/<c>.tex</c>),
    /// packages it into <c>&lt;ActiveMod&gt;.f2mod</c> under a record-scoped virtual path, registers its <c>PathHash</c>,
    /// and links it onto <paramref name="property"/> in the active mod's <c>.gdb.tu</c>.
    /// </summary>
    public static bool LinkAssetToComponentProperty(
        uint targetNodeHash,
        string? nodeLabel,
        ModItemProperty property,
        IReadOnlyList<ModItemProperty>? allComponentProperties,
        string sourceFilePath,
        string? activeMod,
        out string linkedVirtualPath,
        out string error,
        BnkFileSystem? filesystem = null)
    {
        linkedVirtualPath = string.Empty;
        error = string.Empty;

        if (property is null)
        {
            error = "Property is null.";
            return false;
        }

        AssetFieldKind kind = property.AssetKind != AssetFieldKind.None
            ? property.AssetKind
            : ClassifyAssetField(property.CategoryId, property.PropertyName, property.CurrentValue);

        if (kind == AssetFieldKind.None)
        {
            error = $"Property '{property.PropertyName}' is not an asset-bearing field.";
            return false;
        }

        if (!IsAllowedAssetExtension(kind, sourceFilePath))
        {
            string ext = Path.GetExtension(sourceFilePath ?? string.Empty);
            string allowed = string.Join(", ", GetAllowedExtensionsForAssetKind(kind));
            error = $"Unsupported file format '{ext}' for {kind} field '{property.PropertyName}'. Allowed formats: {allowed}";
            return false;
        }

        string diskPath = ResolveDiskFilePath(sourceFilePath);
        if (!File.Exists(diskPath))
        {
            error = $"Source file does not exist on disk: {diskPath}";
            return false;
        }

        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(modName))
        {
            error = "No active mod selected.";
            return false;
        }

        string modsRoot = ResolveModsRoot();
        string pkgPath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
        if (!File.Exists(pkgPath))
        {
            Directory.CreateDirectory(modsRoot);
            ModWorkspace.CreateModPackage(modsRoot, modName, out _, out _);
        }

        GdbMulti? multi = Engine_GameGdb.Database ?? Engine_GameGdb.BaseDatabase;
        string nodeSym = multi?.NameOf(targetNodeHash) ?? string.Empty;
        string nodeSlug = SanitizeAssetSlug(
            !string.IsNullOrWhiteSpace(nodeSym) ? nodeSym : (!string.IsNullOrWhiteSpace(nodeLabel) ? nodeLabel : $"node_{targetNodeHash:X8}"),
            $"node_{targetNodeHash:X8}");
        string modSlug = SanitizeAssetSlug(modName, "mod");
        string propSlug = SanitizeAssetSlug(property.PropertyName, "asset");

        string extLower = Path.GetExtension(diskPath).ToLowerInvariant();
        var packageAdditions = new List<(string Path, byte[] Bytes)>();

        if (kind == AssetFieldKind.Model)
        {
            string relPkgPath = $"mods/{modSlug}/models/{nodeSlug}/{propSlug}.mdl";
            byte[] compiledMdl;
            byte[]? gmdBytes = ResolveTemplateGmdBytes(property, targetNodeHash, multi, modName, filesystem);

            if (extLower == ".mdl")
            {
                compiledMdl = File.ReadAllBytes(diskPath);
                if (File.Exists(diskPath + ".gmd"))
                {
                    try
                    {
                        gmdBytes = File.ReadAllBytes(diskPath + ".gmd");
                    }
                    catch
                    {
                    }
                }
            }
            else
            {
                byte[]? templateMdl = ReadVirtualAssetBytes(property.CurrentValue, modName, includeActiveModPackage: true, filesystem)
                                      ?? ReadVirtualAssetBytes(property.VanillaValue, modName, includeActiveModPackage: false, filesystem)
                                      ?? ReadVirtualAssetBytes(property.ParentComponentValue, modName, includeActiveModPackage: true, filesystem);

                if (templateMdl is null || templateMdl.Length == 0)
                {
                    string? fallbackModelPath = ResolveNodeEffectiveModelPath(targetNodeHash, multi, modName);
                    if (!string.IsNullOrWhiteSpace(fallbackModelPath))
                    {
                        templateMdl = ReadVirtualAssetBytes(fallbackModelPath, modName, includeActiveModPackage: true, filesystem);
                    }
                }

                if (templateMdl is null || templateMdl.Length == 0)
                {
                    error = $"Could not locate a base .mdl template to compile '{Path.GetFileName(diskPath)}'.";
                    return false;
                }

                byte[] gltfBytes = File.ReadAllBytes(diskPath);
                if (!ModMeshOverlays.TryConvert(gltfBytes, templateMdl, out compiledMdl, out string convErr))
                {
                    error = $"Failed to convert '{Path.GetFileName(diskPath)}' to .mdl: {convErr}";
                    return false;
                }

                if (TryExtractAndApplyGltfEmbeddedTextures(
                        gltfBytes, Path.GetDirectoryName(diskPath), compiledMdl,
                        modSlug, nodeSlug, packageAdditions, out byte[] mdlWithTextures))
                {
                    compiledMdl = mdlWithTextures;
                }

                var templateGmd = gmdBytes is not null && gmdBytes.Length > 0
                    ? Engine_GmdSkeleton.TryParse(gmdBytes)
                    : null;
                if (Engine_GmdWriter.TryExtractGmdFromGltf(gltfBytes, templateGmd, out byte[] rebuiltGmdBytes, out _) &&
                    rebuiltGmdBytes.Length > 0)
                {
                    gmdBytes = rebuiltGmdBytes;
                }
            }

            packageAdditions.Add((relPkgPath, compiledMdl));
            if (gmdBytes is not null && gmdBytes.Length > 0)
            {
                packageAdditions.Add((relPkgPath + ".gmd", gmdBytes));
            }
            linkedVirtualPath = relPkgPath.Replace('/', '\\');
            Engine_PropModelIndex.RegisterPath(linkedVirtualPath, pkgPath);
            AssetHashIndex.RegisterPath(linkedVirtualPath);
            if (gmdBytes is not null && gmdBytes.Length > 0)
            {
                AssetHashIndex.RegisterPath(linkedVirtualPath + ".gmd");
            }
        }
        else if (kind == AssetFieldKind.Texture)
        {
            string relPkgPath = $"mods/{modSlug}/textures/{nodeSlug}/{propSlug}.tex";
            byte[] compiledTex;
            byte[]? ormBytes = null;

            if (extLower == ".tex")
            {
                compiledTex = File.ReadAllBytes(diskPath);
            }
            else
            {
                if (!TryConvertImageFileToTex(diskPath, out compiledTex, out ormBytes, out string convErr))
                {
                    error = $"Failed to convert '{Path.GetFileName(diskPath)}' to .tex: {convErr}";
                    return false;
                }
            }

            packageAdditions.Add((relPkgPath, compiledTex));
            if (ormBytes is not null && ormBytes.Length > 0)
            {
                packageAdditions.Add((relPkgPath + ".orm", ormBytes));
            }

            linkedVirtualPath = relPkgPath.Replace('/', '\\');
            Engine_TexPathIndex.RegisterPath(linkedVirtualPath);
            AssetHashIndex.RegisterPath(linkedVirtualPath);
        }
        else
        {
            string subFolder = kind == AssetFieldKind.Skeleton ? "skeletons" : "physics";
            string targetExt = kind == AssetFieldKind.Skeleton ? ".skl" : ".phy";
            string relPkgPath = $"mods/{modSlug}/{subFolder}/{nodeSlug}/{propSlug}{targetExt}";
            packageAdditions.Add((relPkgPath, File.ReadAllBytes(diskPath)));
            linkedVirtualPath = relPkgPath.Replace('/', '\\');
            Engine_PropModelIndex.RegisterPath(linkedVirtualPath, pkgPath);
            AssetHashIndex.RegisterPath(linkedVirtualPath);
        }

        try
        {
            F2ModPackage.IncrementalSync(pkgPath, packageAdditions, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            error = $"Failed to write converted asset into '{Path.GetFileName(pkgPath)}': {ex.Message}";
            return false;
        }

        property.CurrentValue = linkedVirtualPath;
        property.IsModified = !AreValuesEqual(property.Type, property.CurrentValue, property.VanillaValue);

        var propsToSave = allComponentProperties is not null && allComponentProperties.Count > 0
            ? allComponentProperties.ToList()
            : new List<ModItemProperty> { property };
        if (!propsToSave.Contains(property))
        {
            propsToSave.Add(property);
        }

        SaveNodeComponentOverrides(targetNodeHash, propsToSave, modName, nodeLabel ?? nodeSlug);
        return true;
    }

    private static byte[]? ResolveTemplateGmdBytes(
        ModItemProperty modelProperty,
        uint targetNodeHash,
        GdbMulti? multi,
        string modName,
        BnkFileSystem? filesystem)
    {
        (string? Path, bool IncludeActiveMod)[] candidates =
        {
            (modelProperty.CurrentValue, true),
            (modelProperty.VanillaValue, false),
            (modelProperty.ParentComponentValue, true),
        };

        foreach (var (candidate, includeActiveMod) in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            byte[]? gmd = ReadVirtualAssetBytes(candidate.Trim() + ".gmd", modName, includeActiveMod, filesystem);
            if (gmd is not null && gmd.Length > 0)
            {
                return gmd;
            }
        }

        string? fallbackModelPath = ResolveNodeEffectiveModelPath(targetNodeHash, multi, modName);
        if (!string.IsNullOrWhiteSpace(fallbackModelPath))
        {
            byte[]? gmd = ReadVirtualAssetBytes(fallbackModelPath.Trim() + ".gmd", modName, includeActiveModPackage: true, filesystem);
            if (gmd is not null && gmd.Length > 0)
            {
                return gmd;
            }
        }

        return null;
    }

    /// <summary>
    /// Converts a surface texture (<c>.png</c>, <c>.dds</c>, or <c>.tex</c>), stores it in <c>&lt;ActiveMod&gt;.f2mod</c>,
    /// patches the <c>.mdl</c> referenced by <paramref name="modelProperty"/> via <see cref="Engine_MdlWriter.TryRewriteMaterialTextures"/>
    /// into a record-scoped <c>mods/&lt;mod&gt;/models/&lt;node&gt;/&lt;prop&gt;.mdl</c>, and updates the GDB record.
    /// </summary>
    public static bool LinkSurfaceTextureToModelSlot(
        uint targetNodeHash,
        string? nodeLabel,
        ModItemProperty modelProperty,
        IReadOnlyList<ModItemProperty>? allComponentProperties,
        int flatMaterialIndex,
        int slotIndex,
        string slotName,
        string sourceFilePath,
        string? activeMod,
        out string linkedTextureVirtualPath,
        out string linkedModelVirtualPath,
        out string error,
        BnkFileSystem? filesystem = null)
    {
        linkedTextureVirtualPath = string.Empty;
        linkedModelVirtualPath = string.Empty;
        error = string.Empty;

        if (modelProperty is null)
        {
            error = "Model property is null.";
            return false;
        }

        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(modName))
        {
            error = "No active mod selected.";
            return false;
        }

        string modsRoot = ResolveModsRoot();
        string pkgPath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
        if (!File.Exists(pkgPath))
        {
            Directory.CreateDirectory(modsRoot);
            ModWorkspace.CreateModPackage(modsRoot, modName, out _, out _);
        }

        byte[]? baseMdlBytes = ReadVirtualAssetBytes(modelProperty.CurrentValue, modName, includeActiveModPackage: true, filesystem)
                               ?? ReadVirtualAssetBytes(modelProperty.VanillaValue, modName, includeActiveModPackage: false, filesystem)
                               ?? ReadVirtualAssetBytes(modelProperty.ParentComponentValue, modName, includeActiveModPackage: true, filesystem);

        if (baseMdlBytes is null || baseMdlBytes.Length == 0)
        {
            error = "Could not load the model's .mdl binary to patch its material table.";
            return false;
        }

        if (Engine_MdlWriter.ExtractMaterials(baseMdlBytes).Count <= flatMaterialIndex)
        {
            if (Engine_MdlWriter.TryEnsureMaterialSlots(baseMdlBytes, flatMaterialIndex + 1, out byte[] expMdl, out _))
            {
                baseMdlBytes = expMdl;
            }
        }

        GdbMulti? multi = Engine_GameGdb.Database ?? Engine_GameGdb.BaseDatabase;
        string nodeSym = multi?.NameOf(targetNodeHash) ?? string.Empty;
        string nodeSlug = SanitizeAssetSlug(
            !string.IsNullOrWhiteSpace(nodeSym) ? nodeSym : (!string.IsNullOrWhiteSpace(nodeLabel) ? nodeLabel : $"node_{targetNodeHash:X8}"),
            $"node_{targetNodeHash:X8}");
        string modSlug = SanitizeAssetSlug(modName, "mod");
        string propSlug = SanitizeAssetSlug(modelProperty.PropertyName, "modelfile");
        string slotSlug = SanitizeAssetSlug(slotName, $"slot{slotIndex}");

        if (slotIndex == 5)
        {
            string relOrmPkgPath = $"mods/{modSlug}/textures/{nodeSlug}/mat{flatMaterialIndex}_diffuse_orm.png";
            string relOrmAliasPath = $"mods/{modSlug}/textures/{nodeSlug}/mat{flatMaterialIndex}_orm.png";
            string relOrmTexPath = $"mods/{modSlug}/textures/{nodeSlug}/mat{flatMaterialIndex}_diffuse.tex.orm";

            if (string.IsNullOrWhiteSpace(sourceFilePath))
            {
                // Revert / remove authored ORM
                var removals = new[] { relOrmPkgPath, relOrmAliasPath, relOrmTexPath };
                try
                {
                    F2ModPackage.IncrementalSync(pkgPath, Array.Empty<(string, byte[])>(), removals);
                }
                catch (Exception ex)
                {
                    error = $"Failed to remove ORM textures from {Refii.Core.Engine_Title.Active.ModExtension}: {ex.Message}";
                    return false;
                }

                Engine_TexLibrary.Clear();
                linkedTextureVirtualPath = string.Empty;
                linkedModelVirtualPath = modelProperty.CurrentValue ?? string.Empty;
                return true;
            }

            string diskPathOrm = ResolveDiskFilePath(sourceFilePath);
            if (!File.Exists(diskPathOrm))
            {
                error = $"ORM texture file does not exist on disk: {diskPathOrm}";
                return false;
            }

            if (!IsAllowedAssetExtension(AssetFieldKind.Texture, sourceFilePath))
            {
                string ext = Path.GetExtension(sourceFilePath ?? string.Empty);
                error = $"Unsupported ORM texture format '{ext}'. Allowed formats: .png, .dds, .tex, .orm";
                return false;
            }

            byte[] ormBytes = File.ReadAllBytes(diskPathOrm);
            var ormAdditions = new List<(string Path, byte[] Bytes)>();
            ormAdditions.Add((relOrmPkgPath, ormBytes));
            ormAdditions.Add((relOrmAliasPath, ormBytes));

            linkedTextureVirtualPath = relOrmPkgPath.Replace('/', '\\');
            Engine_TexPathIndex.RegisterPath(linkedTextureVirtualPath);
            AssetHashIndex.RegisterPath(linkedTextureVirtualPath);

            IReadOnlyList<MdlMaterialSlotEntry> currentMats = Engine_MdlWriter.ExtractMaterials(baseMdlBytes);
            string curDiffuse = flatMaterialIndex < currentMats.Count ? (currentMats[flatMaterialIndex].Material.Diffuse ?? string.Empty) : string.Empty;
            string scopedDiffusePath = $"mods/{modSlug}/textures/{nodeSlug}/mat{flatMaterialIndex}_diffuse.tex";
            string scopedDiffuseVirtual = scopedDiffusePath.Replace('/', '\\');

            byte[] ormRewrittenMdl = baseMdlBytes;
            if (!string.Equals(curDiffuse.Trim().Replace('/', '\\'), scopedDiffuseVirtual, StringComparison.OrdinalIgnoreCase))
            {
                var ormPatch = new MdlMaterialTexturePatch { Diffuse = scopedDiffuseVirtual };
                var ormPatches = new Dictionary<int, MdlMaterialTexturePatch> { [flatMaterialIndex] = ormPatch };
                if (Engine_MdlWriter.TryRewriteMaterialTextures(baseMdlBytes, ormPatches, out byte[] newMdl, out _))
                {
                    ormRewrittenMdl = newMdl;
                    byte[]? existingDiffuseBytes = ReadVirtualAssetBytes(curDiffuse, modName, includeActiveModPackage: true, filesystem);
                    if (existingDiffuseBytes is not null && existingDiffuseBytes.Length > 0)
                    {
                        ormAdditions.Add((scopedDiffusePath, existingDiffuseBytes));
                    }
                    else
                    {
                        byte[] neutralRgba = new byte[4 * 4 * 4];
                        for (int i = 0; i < neutralRgba.Length; i++) neutralRgba[i] = 200;
                        if (Engine_TexWriter.TryWrite(neutralRgba, 4, 4, TexSurfaceFormat.Rgba8, out byte[] neutralTex, out _))
                        {
                            ormAdditions.Add((scopedDiffusePath, neutralTex));
                        }
                    }
                    Engine_TexPathIndex.RegisterPath(scopedDiffuseVirtual);
                    AssetHashIndex.RegisterPath(scopedDiffuseVirtual);
                }
            }

            string ormRelMdlPkgPath = $"mods/{modSlug}/models/{nodeSlug}/{propSlug}.mdl";
            linkedModelVirtualPath = ormRelMdlPkgPath.Replace('/', '\\');
            ormAdditions.Add((ormRelMdlPkgPath, ormRewrittenMdl));
            byte[]? ormGmdBytes = ResolveTemplateGmdBytes(modelProperty, targetNodeHash, multi, modName, filesystem);
            if (ormGmdBytes is not null && ormGmdBytes.Length > 0)
            {
                ormAdditions.Add((ormRelMdlPkgPath + ".gmd", ormGmdBytes));
            }

            try
            {
                F2ModPackage.IncrementalSync(pkgPath, ormAdditions, Array.Empty<string>());
            }
            catch (Exception ex)
            {
                error = $"Failed to write ORM texture and patched model to {Refii.Core.Engine_Title.Active.ModExtension}: {ex.Message}";
                return false;
            }

            Engine_PropModelIndex.RegisterPath(linkedModelVirtualPath, pkgPath);
            AssetHashIndex.RegisterPath(linkedModelVirtualPath);
            if (ormGmdBytes is not null && ormGmdBytes.Length > 0)
            {
                AssetHashIndex.RegisterPath(linkedModelVirtualPath + ".gmd");
            }

            Engine_TexLibrary.Clear();

            modelProperty.CurrentValue = linkedModelVirtualPath;
            modelProperty.IsModified = !AreValuesEqual(modelProperty.Type, modelProperty.CurrentValue, modelProperty.VanillaValue);

            var ormPropsToSave = allComponentProperties is not null && allComponentProperties.Count > 0
                ? allComponentProperties.ToList()
                : new List<ModItemProperty> { modelProperty };
            if (!ormPropsToSave.Contains(modelProperty))
            {
                ormPropsToSave.Add(modelProperty);
            }

            SaveNodeComponentOverrides(targetNodeHash, ormPropsToSave, modName, nodeLabel ?? nodeSlug);
            return true;
        }

        var additions = new List<(string Path, byte[] Bytes)>();
        string diskPath = ResolveDiskFilePath(sourceFilePath);

        if (File.Exists(diskPath))
        {
            if (!IsAllowedAssetExtension(AssetFieldKind.Texture, sourceFilePath))
            {
                string ext = Path.GetExtension(sourceFilePath ?? string.Empty);
                error = $"Unsupported texture format '{ext}'. Allowed formats: .png, .dds, .tex";
                return false;
            }

            string relTexPkgPath = $"mods/{modSlug}/textures/{nodeSlug}/mat{flatMaterialIndex}_{slotSlug}.tex";
            string extLower = Path.GetExtension(diskPath).ToLowerInvariant();

            byte[] compiledTex;
            byte[]? ormBytes = null;

            if (extLower == ".tex")
            {
                compiledTex = File.ReadAllBytes(diskPath);
            }
            else
            {
                if (!TryConvertImageFileToTex(diskPath, out compiledTex, out ormBytes, out string convErr, isNormalMap: slotIndex == 2))
                {
                    error = $"Failed to convert '{Path.GetFileName(diskPath)}' to .tex: {convErr}";
                    return false;
                }
            }

            linkedTextureVirtualPath = relTexPkgPath.Replace('/', '\\');
            additions.Add((relTexPkgPath, compiledTex));
            if (ormBytes is not null && ormBytes.Length > 0)
            {
                additions.Add((relTexPkgPath + ".orm", ormBytes));
            }
            Engine_TexPathIndex.RegisterPath(linkedTextureVirtualPath);
            AssetHashIndex.RegisterPath(linkedTextureVirtualPath);
        }
        else if (!string.IsNullOrWhiteSpace(sourceFilePath) &&
                 sourceFilePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
        {
            // Reverting or binding to an existing virtual .tex path (such as VanillaTexturePath):
            linkedTextureVirtualPath = sourceFilePath.Trim().Replace('/', '\\');
        }
        else if (string.IsNullOrWhiteSpace(sourceFilePath))
        {
            // Clearing / unbinding texture slot (e.g. removing an authored emission texture when vanilla had none):
            linkedTextureVirtualPath = string.Empty;
        }
        else
        {
            error = $"Texture file does not exist on disk: {diskPath}";
            return false;
        }

        var patch = slotIndex switch
        {
            0 => new MdlMaterialTexturePatch { Diffuse = linkedTextureVirtualPath },
            1 => new MdlMaterialTexturePatch { Specular = linkedTextureVirtualPath },
            2 => new MdlMaterialTexturePatch { Normal = linkedTextureVirtualPath },
            3 => new MdlMaterialTexturePatch { EnvironmentMap = linkedTextureVirtualPath },
            4 => new MdlMaterialTexturePatch { SelfIllumination = linkedTextureVirtualPath },
            _ => default,
        };

        var patches = new Dictionary<int, MdlMaterialTexturePatch>
        {
            [flatMaterialIndex] = patch,
        };

        if (!Engine_MdlWriter.TryRewriteMaterialTextures(baseMdlBytes, patches, out byte[] rewrittenMdl, out string mdlErr))
        {
            error = $"Failed to patch .mdl material table: {mdlErr}";
            return false;
        }

        string relMdlPkgPath = $"mods/{modSlug}/models/{nodeSlug}/{propSlug}.mdl";
        linkedModelVirtualPath = relMdlPkgPath.Replace('/', '\\');
        additions.Add((relMdlPkgPath, rewrittenMdl));
        byte[]? gmdBytes = ResolveTemplateGmdBytes(modelProperty, targetNodeHash, multi, modName, filesystem);
        if (gmdBytes is not null && gmdBytes.Length > 0)
        {
            additions.Add((relMdlPkgPath + ".gmd", gmdBytes));
        }

        try
        {
            F2ModPackage.IncrementalSync(pkgPath, additions, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            error = $"Failed to write patched model and texture to {Refii.Core.Engine_Title.Active.ModExtension}: {ex.Message}";
            return false;
        }

        Engine_PropModelIndex.RegisterPath(linkedModelVirtualPath, pkgPath);
        AssetHashIndex.RegisterPath(linkedModelVirtualPath);
        if (gmdBytes is not null && gmdBytes.Length > 0)
        {
            AssetHashIndex.RegisterPath(linkedModelVirtualPath + ".gmd");
        }

        modelProperty.CurrentValue = linkedModelVirtualPath;
        modelProperty.IsModified = !AreValuesEqual(modelProperty.Type, modelProperty.CurrentValue, modelProperty.VanillaValue);

        var propsToSave = allComponentProperties is not null && allComponentProperties.Count > 0
            ? allComponentProperties.ToList()
            : new List<ModItemProperty> { modelProperty };
        if (!propsToSave.Contains(modelProperty))
        {
            propsToSave.Add(modelProperty);
        }

        SaveNodeComponentOverrides(targetNodeHash, propsToSave, modName, nodeLabel ?? nodeSlug);
        return true;
    }

    /// <summary>
    /// Sweeps <c>mods/...</c> record-scoped assets in <paramref name="pkgPathOrModName"/> that are no longer referenced by
    /// any <c>*.gdb.tu</c> record or any live <c>mods/.../*.mdl</c> material table.
    /// </summary>
    public static int PruneUnreferencedModAssets(string? pkgPathOrModName = null)
    {
        string target = !string.IsNullOrWhiteSpace(pkgPathOrModName)
            ? pkgPathOrModName.Trim()
            : EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(target))
        {
            return 0;
        }

        string pkgPath = target.EndsWith(ModWorkspace.PackageExtension, StringComparison.OrdinalIgnoreCase) || File.Exists(target)
            ? target
            : Path.Combine(ResolveModsRoot(), target + ModWorkspace.PackageExtension);

        if (!File.Exists(pkgPath))
        {
            return 0;
        }

        List<F2ModEntry> entries;
        if (!F2ModPackage.TryOpen(pkgPath, out F2ModPackage? pkg, out _) || pkg is null)
        {
            return 0;
        }

        using (pkg)
        {
            entries = pkg.Entries.ToList();
        }

        var modScopedEntries = entries
            .Where(e => e.Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (modScopedEntries.Count == 0)
        {
            return 0;
        }

        var activeGdbHashes = new HashSet<uint>();
        foreach (F2ModEntry entry in entries)
        {
            if (!entry.Path.EndsWith(".gdb.tu", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[]? tuBytes = LoadPackageEntry(pkgPath, entry.Path);
            if (tuBytes is null || tuBytes.Length == 0)
            {
                continue;
            }

            GdbPatchDatabase db = GdbPatchBuilder.LoadOrCreatePatchDatabase(tuBytes);
            foreach (GdbPatchEntry rec in db.Entries)
            {
                foreach (GdbPatchProperty prop in rec.Properties)
                {
                    if (prop.Value != 0 && prop.Value != GdbHash.Null)
                    {
                        activeGdbHashes.Add(prop.Value);
                    }
                }
            }
        }

        var liveModPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (F2ModEntry entry in modScopedEntries)
        {
            if (entry.Path.EndsWith(".orm", StringComparison.OrdinalIgnoreCase) ||
                entry.Path.EndsWith(".gmd", StringComparison.OrdinalIgnoreCase) ||
                entry.Path.EndsWith("_orm.png", StringComparison.OrdinalIgnoreCase) ||
                entry.Path.EndsWith(".orm.png", StringComparison.OrdinalIgnoreCase) ||
                entry.Path.EndsWith(".tex.orm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            uint pathHash = Engine_PropModelIndex.PathHash(entry.Path);
            if (activeGdbHashes.Contains(pathHash))
            {
                liveModPaths.Add(entry.Path.Replace('\\', '/'));
            }
        }

        // Any texture referenced inside a live mods/.../*.mdl material table is also live:
        foreach (string livePath in liveModPaths.ToList())
        {
            if (!livePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Keep .mdl.gmd sidecars for any live .mdl:
            liveModPaths.Add(livePath + ".gmd");

            byte[]? mdlBytes = LoadPackageEntry(pkgPath, livePath);
            if (mdlBytes is null || mdlBytes.Length == 0)
            {
                continue;
            }

            foreach (MdlMaterialSlotEntry matEntry in Engine_MdlWriter.ExtractMaterials(mdlBytes))
            {
                MdlMaterial m = matEntry.Material;
                foreach (string? tex in new[] { m.Diffuse, m.Specular, m.Normal, m.EnvironmentMap, m.SelfIllumination })
                {
                    if (!string.IsNullOrWhiteSpace(tex))
                    {
                        liveModPaths.Add(tex.Trim().Replace('\\', '/'));
                    }
                }
            }
        }

        // Keep companion .orm, _orm.png, and alias sidecars for any live .tex:
        foreach (string livePath in liveModPaths.ToList())
        {
            if (livePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            {
                liveModPaths.Add(livePath + ".orm");
                liveModPaths.Add(livePath + ".orm.png");
                liveModPaths.Add(livePath + ".tex.orm");
                string stem = livePath[..^4];
                liveModPaths.Add(stem + "_orm.png");
                liveModPaths.Add(stem + ".orm.png");
                liveModPaths.Add(stem + ".orm");

                foreach (string diffSuffix in new[] { "_diffuse", "_diff", "_d", "_albedo", "_color" })
                {
                    if (stem.EndsWith(diffSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        string baseStem = stem[..^diffSuffix.Length];
                        liveModPaths.Add(baseStem + "_orm.png");
                        liveModPaths.Add(baseStem + ".orm.png");
                        liveModPaths.Add(baseStem + ".orm");
                        break;
                    }
                }
            }
        }

        var orphans = new List<string>();
        foreach (F2ModEntry entry in modScopedEntries)
        {
            string normalized = entry.Path.Replace('\\', '/');
            if (!liveModPaths.Contains(normalized))
            {
                orphans.Add(entry.Path);
            }
        }

        if (orphans.Count > 0)
        {
            F2ModPackage.IncrementalSync(pkgPath, Array.Empty<(string, byte[])>(), orphans);
            F2ModPackage.Defragment(pkgPath, out _);
        }

        return orphans.Count;
    }

    /// <summary>
    /// Dynamically expands the specified model's material table in the active mod package by 1 slot.
    /// Returns the linked mod virtual path (e.g. <c>mods/.../model.mdl</c>) on success.
    /// </summary>
    public static bool AddMaterialSlotToModel(
        uint targetNodeHash,
        string? nodeLabel,
        ModItemProperty modelProperty,
        IReadOnlyList<ModItemProperty>? allComponentProperties,
        out string linkedModelPath,
        out string error,
        string? activeMod = null,
        BnkFileSystem? filesystem = null)
    {
        linkedModelPath = string.Empty;
        error = string.Empty;

        if (modelProperty is null)
        {
            error = "Model property is null.";
            return false;
        }

        string modName = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(modName))
        {
            error = "No active mod selected.";
            return false;
        }

        string modsRoot = ResolveModsRoot();
        string pkgPath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
        if (!File.Exists(pkgPath))
        {
            Directory.CreateDirectory(modsRoot);
            ModWorkspace.CreateModPackage(modsRoot, modName, out _, out _);
        }

        byte[]? baseMdlBytes = ReadVirtualAssetBytes(modelProperty.CurrentValue, modName, includeActiveModPackage: true, filesystem)
                               ?? ReadVirtualAssetBytes(modelProperty.VanillaValue, modName, includeActiveModPackage: false, filesystem)
                               ?? ReadVirtualAssetBytes(modelProperty.ParentComponentValue, modName, includeActiveModPackage: true, filesystem);

        if (baseMdlBytes is null || baseMdlBytes.Length == 0)
        {
            error = $"Could not load .mdl bytes for '{modelProperty.CurrentValue}'.";
            return false;
        }

        var currentMats = Engine_MdlWriter.ExtractMaterials(baseMdlBytes);
        int targetCount = Math.Max(2, currentMats.Count + 1);

        if (!Engine_MdlWriter.TryEnsureMaterialSlots(baseMdlBytes, targetCount, out byte[] expandedMdl, out string expandErr))
        {
            error = $"Failed to expand material slots: {expandErr}";
            return false;
        }

        GdbMulti? multi = Engine_GameGdb.Database ?? Engine_GameGdb.BaseDatabase;
        string nodeSym = multi?.NameOf(targetNodeHash) ?? string.Empty;
        string nodeSlug = SanitizeAssetSlug(
            !string.IsNullOrWhiteSpace(nodeSym) ? nodeSym : (!string.IsNullOrWhiteSpace(nodeLabel) ? nodeLabel : $"node_{targetNodeHash:X8}"),
            $"node_{targetNodeHash:X8}");
        string modSlug = SanitizeAssetSlug(modName, "mod");
        string propSlug = SanitizeAssetSlug(modelProperty.PropertyName, "modelfile");

        string relPkgPath = $"mods/{modSlug}/models/{nodeSlug}/{propSlug}.mdl";
        var additions = new List<(string Path, byte[] Bytes)>
        {
            (relPkgPath, expandedMdl)
        };

        byte[]? gmdBytes = ResolveTemplateGmdBytes(modelProperty, targetNodeHash, multi, modName, filesystem);
        if (gmdBytes is not null && gmdBytes.Length > 0)
        {
            additions.Add((relPkgPath + ".gmd", gmdBytes));
        }

        try
        {
            F2ModPackage.IncrementalSync(pkgPath, additions, Array.Empty<string>());
            linkedModelPath = relPkgPath.Replace('/', '\\');
            Engine_PropModelIndex.RegisterPath(linkedModelPath, pkgPath);
            AssetHashIndex.RegisterPath(linkedModelPath);
            if (gmdBytes is not null && gmdBytes.Length > 0)
            {
                AssetHashIndex.RegisterPath(linkedModelPath + ".gmd");
            }

            modelProperty.CurrentValue = linkedModelPath;
            modelProperty.IsModified = !AreValuesEqual(modelProperty.Type, modelProperty.CurrentValue, modelProperty.VanillaValue);

            var propsToSave = allComponentProperties is not null && allComponentProperties.Count > 0
                ? allComponentProperties.ToList()
                : new List<ModItemProperty> { modelProperty };
            if (!propsToSave.Contains(modelProperty))
            {
                propsToSave.Add(modelProperty);
            }

            SaveNodeComponentOverrides(targetNodeHash, propsToSave, modName, nodeLabel ?? nodeSlug);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to write expanded .mdl to mod package: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Resolves the effective <c>.mdl</c> virtual path for any GDB record (weapons, clothing, furniture, props,
    /// buildings, creatures, or custom child nodes) by inspecting its appearance components and inheritance chain.
    /// </summary>
    public static string? ResolveNodeEffectiveModelPath(
        uint nodeHash,
        GdbMulti? multi = null,
        string? activeMod = null)
    {
        if (nodeHash == 0 || nodeHash == GdbHash.Null)
        {
            return null;
        }

        GdbMulti? effectiveMulti = multi ?? Engine_GameGdb.Database ?? Engine_GameGdb.BaseDatabase;
        if (effectiveMulti is null)
        {
            return null;
        }

        uint[] appearanceSlots =
        {
            Engine_GameHash.Fnv1("GraphicAppearanceStaticMeshComponent"),
            Engine_GameHash.Fnv1("GraphicAppearanceAnimatedMeshComponent"),
            Engine_GameHash.Fnv1("InventoryItemComponent"),
            Engine_GameHash.Fnv1("AppearanceModifierComponent"),
        };

        uint[] modelFields =
        {
            Engine_GameHash.Fnv1("ModelFile"),
            Engine_GameHash.Fnv1("GUIScreenMesh"),
            Engine_GameHash.Fnv1("OverrideFemaleModel"),
        };

        if (effectiveMulti.TryLookup(nodeHash, out GdbCursor nodeCursor) && nodeCursor.Valid)
        {
            // 1. Check direct model fields on the record itself (in case nodeHash IS a component record):
            foreach (uint mf in modelFields)
            {
                if (effectiveMulti.TryFindInherited(nodeCursor, mf, -1, out _, out uint directRaw, out _) &&
                    directRaw != 0 && directRaw != GdbHash.Null)
                {
                    string? resolved = TryResolveModelHashOrSymbol(directRaw, effectiveMulti);
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        return resolved;
                    }
                }
            }

            // 2. Check attached or inherited appearance components:
            foreach (uint slotHash in appearanceSlots)
            {
                if (!effectiveMulti.TryFindInherited(nodeCursor, slotHash, 6, out _, out uint compHash, out _) ||
                    compHash == 0 || compHash == GdbHash.Null ||
                    !effectiveMulti.TryLookup(compHash, out GdbCursor compCursor) || !compCursor.Valid)
                {
                    continue;
                }

                foreach (uint mf in modelFields)
                {
                    if (effectiveMulti.TryFindInherited(compCursor, mf, -1, out _, out uint modelRaw, out _) &&
                        modelRaw != 0 && modelRaw != GdbHash.Null)
                    {
                        string? resolved = TryResolveModelHashOrSymbol(modelRaw, effectiveMulti);
                        if (!string.IsNullOrWhiteSpace(resolved))
                        {
                            return resolved;
                        }
                    }
                }
            }
        }

        // 3. Fallback to weapon / item / clothing indexes if available:
        if (Engine_WeaponIndex.TryGetByHash(nodeHash, out WeaponEntry? wep) && wep is not null && !string.IsNullOrWhiteSpace(wep.ModelPath))
        {
            return wep.ModelPath;
        }
        if (Engine_ItemIndex.TryGetByHash(nodeHash, out ItemEntry? itm) && itm is not null && !string.IsNullOrWhiteSpace(itm.ModelPath))
        {
            return itm.ModelPath;
        }
        if (Engine_ClothingIndex.TryGetByHash(nodeHash, out ClothingEntry? cloth) && cloth is not null)
        {
            if (!string.IsNullOrWhiteSpace(cloth.MaleModelPath)) return cloth.MaleModelPath;
            if (!string.IsNullOrWhiteSpace(cloth.FemaleModelPath)) return cloth.FemaleModelPath;
        }

        return null;
    }

    private static string? TryResolveModelHashOrSymbol(uint rawHash, GdbMulti multi)
    {
        if (rawHash == 0 || rawHash == GdbHash.Null)
        {
            return null;
        }

        string sym = multi.NameOf(rawHash);
        if (!string.IsNullOrWhiteSpace(sym) && sym.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
        {
            return sym;
        }

        string? resolved = Engine_PropModelIndex.Resolve(rawHash, Array.Empty<string>());
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        return null;
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

    private static bool IsTestHost() =>
        AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
        System.Environment.CommandLine.Contains("testhost", StringComparison.OrdinalIgnoreCase);

    private static void SafeLog(string message)
    {
        if (IsTestHost()) return;
        try { GD.Print(message); } catch { }
    }

    private static void SafeWarn(string message)
    {
        if (IsTestHost()) return;
        try { GD.PushWarning(message); } catch { }
    }

    /// <summary>
    /// Refreshes all active Engine_EquipmentInstance nodes in the currently edited scene in the editor.
    /// </summary>
    public static void RefreshSceneEquipment()
    {
        if (IsTestHost())
        {
            return;
        }

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
            SafeWarn($"[mod_values] Could not refresh scene equipment: {ex.Message}");
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
