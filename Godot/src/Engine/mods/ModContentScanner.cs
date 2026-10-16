using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Refii.Core;
using Refii.Equipment;
using Refii.Gdb;
using Refii.Text;

namespace Refii.Mods;

/// <summary>
/// Identifies the specific domain kind of an authored entry inside an active mod container.
/// </summary>
public enum ModAuthoredKind
{
    /// <summary>A sparse GDB record override or addition in a .gdb.tu Title Update.</summary>
    GdbRecord,

    /// <summary>A localized text override in book.babel.csv.</summary>
    BabelString,

    /// <summary>A custom Lua script file.</summary>
    Script,

    /// <summary>An uncompressed DCC work asset in assets/creator/ (e.g. .glb, .blend, .png).</summary>
    CreatorAsset
}

/// <summary>
/// Represents a discrete authored item, record, or file discovered in the active mod's workspace.
/// </summary>
public sealed class ModAuthoredEntry
{
    /// <summary>Gets or sets the kind of asset or record.</summary>
    public ModAuthoredKind Kind { get; set; }

    /// <summary>Gets or sets the display category (e.g. "Weapons", "Text", "Scripts").</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Gets or sets the virtual file path or string key identifier.</summary>
    public string PathOrKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the numerical hash if this entry targets a GDB record (or 0).</summary>
    public uint RecordHash { get; set; }

    /// <summary>Gets or sets the primary user-facing title or item name.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets secondary metadata (e.g. "Record 0x6F548E0E (Flintlock)").</summary>
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>Gets or sets detailed modification summary (e.g. "3 properties overridden").</summary>
    public string Details { get; set; } = string.Empty;

    /// <summary>Gets or sets the relative container subpath (e.g. "globals/globals.gdb.tu").</summary>
    public string ContainerSubPath { get; set; } = string.Empty;
}

/// <summary>
/// Aggregated summary of all content authored within an isolated active mod package.
/// </summary>
public sealed class ModContentSummary
{
    /// <summary>Gets or sets the folder/package name of the scanned mod.</summary>
    public string ModName { get; set; } = string.Empty;

    /// <summary>Gets or sets the count of discrete GDB records patched across all Title Updates.</summary>
    public int PatchedRecordCount { get; set; }

    /// <summary>Gets or sets the count of localized strings overridden in book.babel.csv.</summary>
    public int LocalizedStringCount { get; set; }

    /// <summary>Gets or sets the count of custom scripts discovered.</summary>
    public int ScriptCount { get; set; }

    /// <summary>Gets or sets the count of creator workbench files found in assets/creator/.</summary>
    public int CreatorAssetCount { get; set; }

    /// <summary>Gets the collection of discovered authored entries.</summary>
    public List<ModAuthoredEntry> Entries { get; } = new();

    /// <summary>Gets or sets human-readable conflict diagnostic text.</summary>
    public string ConflictStatusText { get; set; } = "✓ No Conflicts";

    /// <summary>Gets or sets a value indicating whether this mod has collisions with higher priority mods.</summary>
    public bool HasConflicts { get; set; }
}

/// <summary>
/// Headless service responsible for inspecting an active mod package (.f2mod), computing content
/// metrics, structuring traversal tree entries, and cleanly reverting specific overrides without GUI dependencies.
/// </summary>
/// <remarks>
/// Adheres strictly to active mod isolation: only records and files physically present within the
/// target mod package are queried. Data from other active or inactive mods is never merged into the summary.
/// </remarks>
public static class ModContentScanner
{
    private const string BabelSubPath = "language/en-uk/text/book.babel.csv";

    private static readonly string[] KnownTuFiles =
    {
        "globals/globals.gdb.tu",
        "speechaction/speechaction.gdb.tu",
        "interactivecutscenes/interactivecutscenes.gdb.tu",
        "entity/entity.gdb.tu",
        "environmentthemes/environmentthemes.gdb.tu"
    };

    /// <summary>
    /// Scans the target mod package in isolation and compiles an aggregated content summary.
    /// </summary>
    /// <param name="modsRoot">Directory containing .f2mod packages (e.g. assets/mods/).</param>
    /// <param name="modName">Name of the mod to scan (without .f2mod extension).</param>
    /// <param name="appAssetsRoot">Optional root directory containing assets/ (for resolving creator/ folder).</param>
    /// <returns>A populated summary object.</returns>
    public static ModContentSummary ScanMod(string modsRoot, string modName, string? appAssetsRoot = null)
    {
        var summary = new ModContentSummary
        {
            ModName = modName
        };

        if (string.IsNullOrWhiteSpace(modName))
        {
            return summary;
        }

        string packagePath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
        if (File.Exists(packagePath) && F2ModPackage.TryOpen(packagePath, out F2ModPackage? pkg, out _) && pkg is not null)
        {
            using (pkg)
            {
                // 1. Scan GDB Title Updates (.gdb.tu)
                ScanTitleUpdates(pkg, summary);

                // 2. Scan Localized Text (book.babel.csv)
                ScanBabelText(pkg, summary);

                // 3. Scan Scripts (scripts/*)
                ScanScripts(pkg, summary);
            }
        }

        // 4. Scan Creator Working Assets (res://creator/<modName>/)
        ScanCreatorAssets(modName, appAssetsRoot, summary);

        // 5. Evaluate Conflicts against loadorder.toml
        EvaluateConflicts(modsRoot, modName, summary);

        return summary;
    }

    /// <summary>
    /// Inspects all Title Update databases inside the package and enumerates individual record modifications.
    /// </summary>
    private static void ScanTitleUpdates(F2ModPackage pkg, ModContentSummary summary)
    {
        GdbMulti? baseMulti = Engine_GameGdb.Database ?? Engine_GameGdb.BaseDatabase;

        var tuPaths = new List<string>(KnownTuFiles);
        foreach (F2ModEntry pkgEntry in pkg.Entries)
        {
            if (pkgEntry.Path.EndsWith(".gdb.tu", StringComparison.OrdinalIgnoreCase) &&
                !tuPaths.Any(k => string.Equals(k, pkgEntry.Path, StringComparison.OrdinalIgnoreCase)))
            {
                tuPaths.Add(pkgEntry.Path);
            }
        }

        foreach (string tuPath in tuPaths)
        {
            if (!pkg.TryGetEntry(tuPath, out F2ModEntry? entry) || entry is null)
            {
                continue;
            }

            byte[] bytes = pkg.ReadEntryBytes(entry);
            if (!GdbView.TryParse(bytes, out GdbView view, out _))
            {
                continue;
            }

            bool isLevelTu = !KnownTuFiles.Any(k => string.Equals(k, tuPath, StringComparison.OrdinalIgnoreCase));

            for (int i = 0; i < view.RecordCount; i++)
            {
                uint hash = view.RecordHash(i);
                int fieldCount = 0;
                var modifiedProps = new List<string>();

                foreach (var (fHash, fType, fRaw) in view.RawFields(i))
                {
                    if (fHash == GdbView.HashParent)
                    {
                        continue;
                    }

                    fieldCount++;
                    string fname = baseMulti is not null ? baseMulti.NameOf(fHash) : string.Empty;
                    if (string.IsNullOrEmpty(fname) && view.Names.TryGetValue(fHash, out string? viewFname))
                    {
                        fname = viewFname;
                    }

                    if (string.IsNullOrEmpty(fname))
                    {
                        fname = $"0x{fHash:X8}";
                    }
                    else
                    {
                        fname = PrettifyFieldName(fname);
                    }

                    string fval = FormatFieldValue(fType, fRaw);
                    if (modifiedProps.Count < 4)
                    {
                        modifiedProps.Add($"{fname}: {fval}");
                    }
                }

                string recName = ResolveRecordName(baseMulti, view, hash);

                string detailStr = fieldCount switch
                {
                    0 => "Promoted record (no local field overrides)",
                    _ => modifiedProps.Count > 0
                        ? string.Join(", ", modifiedProps) + (fieldCount > modifiedProps.Count ? $", ... (+{fieldCount - modifiedProps.Count} more)" : "")
                        : $"{fieldCount} propert{(fieldCount == 1 ? "y" : "ies")} modified"
                };

                string category = isLevelTu
                    ? "Levels & World"
                    : tuPath.StartsWith("entity", StringComparison.OrdinalIgnoreCase)
                        ? "Entities & NPCs"
                        : "Items & Game Records";

                string fileName = Path.GetFileName(tuPath);

                summary.Entries.Add(new ModAuthoredEntry
                {
                    Kind = ModAuthoredKind.GdbRecord,
                    Category = category,
                    PathOrKey = $"0x{hash:X8}",
                    RecordHash = hash,
                    Label = recName,
                    Subtitle = $"0x{hash:X8} ({fileName})",
                    Details = detailStr,
                    ContainerSubPath = tuPath
                });

                summary.PatchedRecordCount++;
            }
        }

        // Also scan any .save.tu level layer/placement patches:
        foreach (F2ModEntry pkgEntry in pkg.Entries)
        {
            if (!pkgEntry.Path.EndsWith(".save.tu", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SaveMapPatch savePatch = SaveMapPatch.Parse(pkg.ReadEntryBytes(pkgEntry));
            if (savePatch.IsEmpty)
            {
                continue;
            }

            int upsertedCount = savePatch.Layers.Sum(l => l.UpsertedEntities.Count);
            int layerOverrideCount = savePatch.Layers.Count(l => l.LoadOverride.HasValue);
            int removedCount = savePatch.RemovedEntities.Count;
            string fileName = Path.GetFileName(pkgEntry.Path);

            // Aggregate level patch entry:
            summary.Entries.Add(new ModAuthoredEntry
            {
                Kind = ModAuthoredKind.GdbRecord,
                Category = "Levels & World",
                PathOrKey = pkgEntry.Path,
                RecordHash = 0,
                Label = fileName,
                Subtitle = pkgEntry.Path,
                Details = $"{upsertedCount} placed/moved, {removedCount} removed, {layerOverrideCount} layer state override(s)",
                ContainerSubPath = pkgEntry.Path
            });

            // Discrete removed entities:
            foreach (uint removedHash in savePatch.RemovedEntities)
            {
                string remName = baseMulti is not null ? baseMulti.NameOf(removedHash) : string.Empty;
                if (string.IsNullOrEmpty(remName))
                {
                    remName = $"Record 0x{removedHash:X8}";
                }
                else
                {
                    remName = PrettifyFieldName(remName);
                }

                summary.Entries.Add(new ModAuthoredEntry
                {
                    Kind = ModAuthoredKind.GdbRecord,
                    Category = "Levels & World",
                    PathOrKey = $"0x{removedHash:X8}",
                    RecordHash = removedHash,
                    Label = $"🗑️ Removed: {remName}",
                    Subtitle = $"0x{removedHash:X8} ({fileName})",
                    Details = "Entity removed from level .save",
                    ContainerSubPath = pkgEntry.Path
                });
                summary.PatchedRecordCount++;
            }

            // Discrete added / placed entities:
            foreach (SaveMapLayerPatch layer in savePatch.Layers)
            {
                foreach ((uint RecordHash, string EntityName) upserted in layer.UpsertedEntities)
                {
                    string entName = !string.IsNullOrWhiteSpace(upserted.EntityName)
                        ? PrettifyFieldName(upserted.EntityName)
                        : (baseMulti is not null ? baseMulti.NameOf(upserted.RecordHash) : $"0x{upserted.RecordHash:X8}");

                    summary.Entries.Add(new ModAuthoredEntry
                    {
                        Kind = ModAuthoredKind.GdbRecord,
                        Category = "Levels & World",
                        PathOrKey = $"0x{upserted.RecordHash:X8}",
                        RecordHash = upserted.RecordHash,
                        Label = $"➕ Placement: {entName}",
                        Subtitle = $"0x{upserted.RecordHash:X8} ({fileName} -> {layer.LayerName})",
                        Details = $"Placed in layer '{layer.LayerName}'",
                        ContainerSubPath = pkgEntry.Path
                    });
                    summary.PatchedRecordCount++;
                }

                if (layer.LoadOverride.HasValue)
                {
                    summary.Entries.Add(new ModAuthoredEntry
                    {
                        Kind = ModAuthoredKind.GdbRecord,
                        Category = "Levels & World",
                        PathOrKey = $"{layer.LayerName}_LoadState",
                        RecordHash = 0,
                        Label = $"🗂️ Layer State: {layer.LayerName}",
                        Subtitle = fileName,
                        Details = $"Load state overridden to {layer.LoadOverride.Value}",
                        ContainerSubPath = pkgEntry.Path
                    });
                }
            }
        }
    }

    private static string ResolveRecordName(GdbMulti? multi, GdbView view, uint hash)
    {
        // 1. Author-specified record names from the TU view's string table:
        if (view.Names.TryGetValue(hash, out string? authored) &&
            !string.IsNullOrWhiteSpace(authored) &&
            !authored.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            !authored.StartsWith("Record 0x", StringComparison.OrdinalIgnoreCase))
        {
            return Engine_ItemLabels.Prettify(authored);
        }

        if (view.RecordNames.TryGetValue(hash, out string? recAuthored) &&
            !string.IsNullOrWhiteSpace(recAuthored) &&
            !recAuthored.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            !recAuthored.StartsWith("Record 0x", StringComparison.OrdinalIgnoreCase))
        {
            return Engine_ItemLabels.Prettify(recAuthored);
        }

        if (Engine_RecordNameCatalog.TryGetCuratedNodeName(hash, out string curatedName) &&
            !string.IsNullOrWhiteSpace(curatedName))
        {
            return curatedName;
        }

        if (multi is not null)
        {
            // 2. High-level Item lookup (handles item definitions, inventory components, and component leaves):
            try
            {
                ItemDetail? item = Engine_ItemDetails.BuildOne(multi, hash);
                if (item is not null)
                {
                    string friendly = Engine_ItemNames.DisplayNameOf(item);
                    if (!string.IsNullOrWhiteSpace(friendly) &&
                        !friendly.Equals(Engine_ItemNames.Placeholder, StringComparison.Ordinal) &&
                        !friendly.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        return friendly;
                    }

                    if (!string.IsNullOrWhiteSpace(item.Label) &&
                        !item.Label.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        return item.Label;
                    }
                }
            }
            catch
            {
            }

            // 3. Reverse component definition walk if BuildOne wasn't directly called on owner:
            try
            {
                if (Engine_ItemDetails.TryDefinitionOf(multi, hash, out GdbCursor defCursor) && defCursor.Valid)
                {
                    uint defHash = defCursor.View.RecordHash(defCursor.Record);
                    ItemDetail? defItem = Engine_ItemDetails.BuildOne(multi, defHash);
                    if (defItem is not null)
                    {
                        string friendly = Engine_ItemNames.DisplayNameOf(defItem);
                        if (!string.IsNullOrWhiteSpace(friendly) &&
                            !friendly.Equals(Engine_ItemNames.Placeholder, StringComparison.Ordinal) &&
                            !friendly.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        {
                            return friendly;
                        }

                        if (!string.IsNullOrWhiteSpace(defItem.Label) &&
                            !defItem.Label.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        {
                            return defItem.Label;
                        }
                    }
                }
            }
            catch
            {
            }

            // 4. Weapon and Item index lookups:
            try
            {
                if (Engine_WeaponIndex.TryGetByHash(hash, out var weaponEntry) &&
                    !string.IsNullOrWhiteSpace(weaponEntry.Name))
                {
                    return weaponEntry.Name;
                }

                if (Engine_ItemIndex.TryGetByHash(hash, out var itemEntry) &&
                    !string.IsNullOrWhiteSpace(itemEntry.Name))
                {
                    return itemEntry.Name;
                }
            }
            catch
            {
            }

            // 5. GDB string table lookup:
            string gdbName = multi.NameOf(hash);
            if (!string.IsNullOrWhiteSpace(gdbName) &&
                !gdbName.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return Engine_ItemLabels.Prettify(gdbName);
            }
        }

        return $"Record 0x{hash:X8}";
    }

    private static string FormatFieldValue(GdbFieldType type, uint raw) => type switch
    {
        GdbFieldType.Bool => raw != 0 ? "true" : "false",
        GdbFieldType.Float => BitConverter.Int32BitsToSingle(unchecked((int)raw)).ToString("0.#####", CultureInfo.InvariantCulture),
        GdbFieldType.Int => unchecked((int)raw).ToString(CultureInfo.InvariantCulture),
        GdbFieldType.Enum => raw.ToString(CultureInfo.InvariantCulture),
        _ => $"0x{raw:X8}"
    };

    private static string PrettifyFieldName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (name.Equals("BasePrice", StringComparison.OrdinalIgnoreCase)) return "Base Price";
        if (name.Equals("NumberOfSlots", StringComparison.OrdinalIgnoreCase)) return "Augment Slots";
        if (name.Equals("Rating", StringComparison.OrdinalIgnoreCase)) return "Tier / Rating";
        if (name.Equals("DamageMultiplier", StringComparison.OrdinalIgnoreCase)) return "Damage";
        if (name.Equals("AttackSpeedMultiplier", StringComparison.OrdinalIgnoreCase)) return "Attack Speed";
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

    /// <summary>
    /// Inspects book.babel.csv inside the package and enumerates individual localized text overrides.
    /// </summary>
    private static void ScanBabelText(F2ModPackage pkg, ModContentSummary summary)
    {
        if (!pkg.TryGetEntry(BabelSubPath, out F2ModEntry? entry) || entry is null)
        {
            return;
        }

        byte[] bytes = pkg.ReadEntryBytes(entry);
        string csvText = Encoding.UTF8.GetString(bytes);
        List<BabelOverlayRow> rows = BabelOverlay.ParseCsv(csvText, new List<string>());

        foreach (BabelOverlayRow row in rows)
        {
            summary.Entries.Add(new ModAuthoredEntry
            {
                Kind = ModAuthoredKind.BabelString,
                Category = "Localized Text",
                PathOrKey = row.Key,
                Label = row.Value,
                Subtitle = $"Key: {row.Key}",
                Details = "Localized string override in book.babel.csv",
                ContainerSubPath = BabelSubPath
            });

            summary.LocalizedStringCount++;
        }
    }

    /// <summary>
    /// Inspects the package entries for custom Lua scripts.
    /// </summary>
    private static void ScanScripts(F2ModPackage pkg, ModContentSummary summary)
    {
        foreach (F2ModEntry entry in pkg.Entries)
        {
            if (entry.Path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            {
                string stem = Path.GetFileName(entry.Path);
                summary.Entries.Add(new ModAuthoredEntry
                {
                    Kind = ModAuthoredKind.Script,
                    Category = "Lua Scripts",
                    PathOrKey = entry.Path,
                    Label = stem,
                    Subtitle = entry.Path,
                    Details = $"{entry.Size:N0} bytes",
                    ContainerSubPath = entry.Path
                });

                summary.ScriptCount++;
            }
        }
    }

    /// <summary>
    /// Discovers raw staging files located under res://creator/<modName>/ (or assets/creator/<modName>/) on the local filesystem.
    /// </summary>
    private static void ScanCreatorAssets(string modName, string? appAssetsRoot, ModContentSummary summary)
    {
        var candidateDirs = new List<(string Dir, string PrefixLabel)>();

        if (!string.IsNullOrEmpty(appAssetsRoot))
        {
            candidateDirs.Add((Path.Combine(appAssetsRoot, "creator", modName), $"assets/creator/{modName}"));
            candidateDirs.Add((Path.Combine(appAssetsRoot, "creators", modName), $"assets/creator/{modName}"));
        }
        else
        {
            string projectDir = Engine_AppRoot.SafeGetProjectDir();
            if (!string.IsNullOrEmpty(projectDir))
            {
                candidateDirs.Add((Path.Combine(projectDir, "creator", modName), $"res://creator/{modName}"));
            }

            string assetsDir = Engine_AppRoot.AssetsDirectory;
            if (!string.IsNullOrEmpty(assetsDir))
            {
                candidateDirs.Add((Path.Combine(assetsDir, "creator", modName), $"assets/creator/{modName}"));
                candidateDirs.Add((Path.Combine(assetsDir, "creators", modName), $"assets/creator/{modName}"));
            }
        }

        var seenRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (creatorDir, prefixLabel) in candidateDirs)
        {
            if (!Directory.Exists(creatorDir))
            {
                continue;
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(creatorDir, "*.*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is ".glb" or ".gltf" or ".png" or ".blend" or ".tga" or ".dds" or ".mdl" or ".tex")
                    {
                        string rel = Path.GetRelativePath(creatorDir, file).Replace('\\', '/');
                        if (!seenRelPaths.Add(rel))
                        {
                            continue;
                        }

                        summary.Entries.Add(new ModAuthoredEntry
                        {
                            Kind = ModAuthoredKind.CreatorAsset,
                            Category = "Creator Work Files",
                            PathOrKey = rel,
                            Label = Path.GetFileName(file),
                            Subtitle = $"{prefixLabel}/{rel}",
                            Details = $"{new FileInfo(file).Length:N0} bytes",
                            ContainerSubPath = rel
                        });

                        summary.CreatorAssetCount++;
                    }
                }
            }
            catch
            {
                // Directory access failure in restricted sandboxes
            }
        }
    }

    /// <summary>
    /// Evaluates if any of the active mod's patched GDB records or text collide with and are shadowed by
    /// higher-priority mods listed earlier in loadorder.toml, or if this mod wins over lower mods.
    /// Utilizes the composite patch resolution pipeline to accurately attribute which specific mod
    /// won each colliding property, record, or localized string.
    /// </summary>
    private static void EvaluateConflicts(string modsRoot, string modName, ModContentSummary summary)
    {
        try
        {
            var order = ModLoadOrder.Read(modsRoot);
            int activeIndex = order.Enabled.FindIndex(m => m.Equals(modName, StringComparison.OrdinalIgnoreCase));
            if (activeIndex < 0)
            {
                summary.ConflictStatusText = "Mod is disabled in load order";
                summary.HasConflicts = false;
                return;
            }

            if (order.Enabled.Count < 2)
            {
                summary.ConflictStatusText = "✓ No Conflicts (Only enabled mod in load order)";
                summary.HasConflicts = false;
                return;
            }

            var installs = ModWorkspace.DiscoverModInstalls(modsRoot);
            var recordConflicts = GdbPatchCompositor.DetectRecordConflicts(modsRoot, order, installs);
            var rowConflicts = ModRowOverlays.DetectRowConflicts(order, installs);

            var lostRecordHashes = new HashSet<uint>();
            var lostBabelKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lostWinners = new List<string>();

            // 1. Check each authored entry in the active mod's summary to attribute exact winners:
            foreach (var entry in summary.Entries)
            {
                if (entry.Kind == ModAuthoredKind.GdbRecord && entry.RecordHash != 0)
                {
                    string normSubPath = ModLoadOrder.Normalize(entry.ContainerSubPath);
                    var matching = recordConflicts.FindAll(c =>
                        c.RecordHash == entry.RecordHash &&
                        (string.IsNullOrEmpty(normSubPath) || string.IsNullOrEmpty(c.DatabaseSubPath) ||
                         string.Equals(ModLoadOrder.Normalize(c.DatabaseSubPath), normSubPath, StringComparison.OrdinalIgnoreCase)) &&
                        c.LoserMods.Contains(modName, StringComparer.OrdinalIgnoreCase));

                    if (matching.Count > 0)
                    {
                        lostRecordHashes.Add(entry.RecordHash);
                        var entryWinners = new List<string>();
                        foreach (var c in matching)
                        {
                            if (!string.IsNullOrEmpty(c.WinnerMod) && !entryWinners.Contains(c.WinnerMod, StringComparer.OrdinalIgnoreCase))
                            {
                                entryWinners.Add(c.WinnerMod);
                            }
                        }

                        // Order winners by load order priority:
                        entryWinners.Sort((a, b) =>
                        {
                            int ia = order.Enabled.FindIndex(m => m.Equals(a, StringComparison.OrdinalIgnoreCase));
                            int ib = order.Enabled.FindIndex(m => m.Equals(b, StringComparison.OrdinalIgnoreCase));
                            return (ia < 0 ? int.MaxValue : ia).CompareTo(ib < 0 ? int.MaxValue : ib);
                        });

                        foreach (string w in entryWinners)
                        {
                            if (!lostWinners.Contains(w, StringComparer.OrdinalIgnoreCase))
                            {
                                lostWinners.Add(w);
                            }
                        }

                        if (!entry.Details.Contains("Overridden by", StringComparison.OrdinalIgnoreCase))
                        {
                            string winnerStr = string.Join(", ", entryWinners.Select(w => $"'{w}'"));
                            entry.Details += $" (⚠ Overridden by {winnerStr})";
                        }
                    }
                }
                else if (entry.Kind == ModAuthoredKind.BabelString && !string.IsNullOrEmpty(entry.PathOrKey))
                {
                    var matching = rowConflicts.FindAll(rc =>
                        string.Equals(rc.Conflict.Key, entry.PathOrKey, StringComparison.OrdinalIgnoreCase) &&
                        rc.Conflict.Losers.Contains(modName, StringComparer.OrdinalIgnoreCase));

                    if (matching.Count > 0)
                    {
                        lostBabelKeys.Add(entry.PathOrKey);
                        var entryWinners = new List<string>();
                        foreach (var rc in matching)
                        {
                            if (!string.IsNullOrEmpty(rc.Conflict.Winner) && !entryWinners.Contains(rc.Conflict.Winner, StringComparer.OrdinalIgnoreCase))
                            {
                                entryWinners.Add(rc.Conflict.Winner);
                            }
                        }

                        entryWinners.Sort((a, b) =>
                        {
                            int ia = order.Enabled.FindIndex(m => m.Equals(a, StringComparison.OrdinalIgnoreCase));
                            int ib = order.Enabled.FindIndex(m => m.Equals(b, StringComparison.OrdinalIgnoreCase));
                            return (ia < 0 ? int.MaxValue : ia).CompareTo(ib < 0 ? int.MaxValue : ib);
                        });

                        foreach (string w in entryWinners)
                        {
                            if (!lostWinners.Contains(w, StringComparer.OrdinalIgnoreCase))
                            {
                                lostWinners.Add(w);
                            }
                        }

                        if (!entry.Details.Contains("Overridden by", StringComparison.OrdinalIgnoreCase))
                        {
                            string winnerStr = string.Join(", ", entryWinners.Select(w => $"'{w}'"));
                            entry.Details += $" (⚠ Overridden by {winnerStr})";
                        }
                    }
                }
            }

            // 2. Also ensure any other record/row conflicts where modName is a loser are accounted for:
            foreach (var c in recordConflicts)
            {
                if (c.LoserMods.Contains(modName, StringComparer.OrdinalIgnoreCase))
                {
                    lostRecordHashes.Add(c.RecordHash);
                    if (!string.IsNullOrEmpty(c.WinnerMod) && !lostWinners.Contains(c.WinnerMod, StringComparer.OrdinalIgnoreCase))
                    {
                        lostWinners.Add(c.WinnerMod);
                    }
                }
            }

            foreach (var (_, rc) in rowConflicts)
            {
                if (rc.Losers.Contains(modName, StringComparer.OrdinalIgnoreCase))
                {
                    lostBabelKeys.Add(rc.Key);
                    if (!string.IsNullOrEmpty(rc.Winner) && !lostWinners.Contains(rc.Winner, StringComparer.OrdinalIgnoreCase))
                    {
                        lostWinners.Add(rc.Winner);
                    }
                }
            }

            int totalLost = lostRecordHashes.Count + lostBabelKeys.Count;
            if (totalLost > 0)
            {
                summary.HasConflicts = true;
                lostWinners.Sort((a, b) =>
                {
                    int ia = order.Enabled.FindIndex(m => m.Equals(a, StringComparison.OrdinalIgnoreCase));
                    int ib = order.Enabled.FindIndex(m => m.Equals(b, StringComparison.OrdinalIgnoreCase));
                    return (ia < 0 ? int.MaxValue : ia).CompareTo(ib < 0 ? int.MaxValue : ib);
                });

                string pluralCollisions = totalLost == 1 ? "collision" : "collisions";
                if (lostWinners.Count == 1)
                {
                    summary.ConflictStatusText = $"⚠ {totalLost} {pluralCollisions} overridden by '{lostWinners[0]}' higher in load order";
                }
                else
                {
                    string winnerList = string.Join(", ", lostWinners.Select(w => $"'{w}'"));
                    summary.ConflictStatusText = $"⚠ {totalLost} {pluralCollisions} overridden by {lostWinners.Count} higher-priority mods ({winnerList})";
                }
                return;
            }

            // 3. If no losses, check if this mod won any collisions over lower-priority mods:
            var wonRecordHashes = new HashSet<uint>();
            var wonBabelKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var loserMods = new List<string>();

            foreach (var c in recordConflicts)
            {
                if (string.Equals(c.WinnerMod, modName, StringComparison.OrdinalIgnoreCase))
                {
                    wonRecordHashes.Add(c.RecordHash);
                    foreach (string loser in c.LoserMods)
                    {
                        if (!loserMods.Contains(loser, StringComparer.OrdinalIgnoreCase))
                        {
                            loserMods.Add(loser);
                        }
                    }
                }
            }

            foreach (var (_, rc) in rowConflicts)
            {
                if (string.Equals(rc.Winner, modName, StringComparison.OrdinalIgnoreCase))
                {
                    wonBabelKeys.Add(rc.Key);
                    foreach (string loser in rc.Losers)
                    {
                        if (!loserMods.Contains(loser, StringComparer.OrdinalIgnoreCase))
                        {
                            loserMods.Add(loser);
                        }
                    }
                }
            }

            int totalWon = wonRecordHashes.Count + wonBabelKeys.Count;
            summary.HasConflicts = false;

            if (totalWon > 0)
            {
                loserMods.Sort((a, b) =>
                {
                    int ia = order.Enabled.FindIndex(m => m.Equals(a, StringComparison.OrdinalIgnoreCase));
                    int ib = order.Enabled.FindIndex(m => m.Equals(b, StringComparison.OrdinalIgnoreCase));
                    return (ia < 0 ? int.MaxValue : ia).CompareTo(ib < 0 ? int.MaxValue : ib);
                });

                string pluralCollisions = totalWon == 1 ? "collision" : "collisions";
                if (loserMods.Count == 1)
                {
                    summary.ConflictStatusText = $"✓ Priority Mod: {totalWon} {pluralCollisions} resolved in favor of this mod (overrides '{loserMods[0]}')";
                }
                else
                {
                    string losersStr = string.Join(", ", loserMods.Select(l => $"'{l}'"));
                    summary.ConflictStatusText = $"✓ Priority Mod: {totalWon} {pluralCollisions} resolved in favor of this mod (overrides {losersStr})";
                }
            }
            else
            {
                if (activeIndex == 0)
                {
                    summary.ConflictStatusText = "✓ No Conflicts (Highest priority in load order)";
                }
                else
                {
                    summary.ConflictStatusText = $"✓ No Conflicts (Checked against {activeIndex} higher priority mod(s))";
                }
            }
        }
        catch (Exception ex)
        {
            summary.ConflictStatusText = $"Conflict check unavailable: {ex.Message}";
        }
    }

    /// <summary>
    /// Removes a single GDB record override from the target Title Update inside the active mod package,
    /// rebuilding the sparse database and synchronizing the package in < 5ms.
    /// </summary>
    /// <param name="modsRoot">Directory containing mods.</param>
    /// <param name="modName">Active mod package name.</param>
    /// <param name="tuSubPath">Relative path to the Title Update (e.g. "globals/globals.gdb.tu").</param>
    /// <param name="recordHash">FNV-1 hash of the record to remove.</param>
    /// <param name="error">Error description if the revert failed.</param>
    /// <returns>True on success; false otherwise.</returns>
    public static bool RevertGdbRecord(
        string modsRoot, string modName, string tuSubPath, uint recordHash, out string error)
    {
        error = string.Empty;
        string packagePath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
        if (!File.Exists(packagePath))
        {
            error = $"Package not found: {packagePath}";
            return false;
        }

        byte[]? existingBytes = null;
        if (F2ModPackage.TryOpen(packagePath, out F2ModPackage? pkg, out string openErr) && pkg is not null)
        {
            using (pkg)
            {
                if (pkg.TryGetEntry(tuSubPath, out F2ModEntry? entry) && entry is not null)
                {
                    existingBytes = pkg.ReadEntryBytes(entry);
                }
            }
        }
        else
        {
            error = openErr;
            return false;
        }

        if (existingBytes is null || existingBytes.Length == 0)
        {
            error = $"Title Update entry '{tuSubPath}' not found in package.";
            return false;
        }

        GdbPatchDatabase db = GdbPatchBuilder.LoadOrCreatePatchDatabase(existingBytes);
        bool removed = GdbPatchBuilder.RemoveEntry(db, recordHash);
        if (!removed)
        {
            error = $"Record 0x{recordHash:X8} was not found in patch database.";
            return false;
        }

        db.Names.Remove(recordHash);

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

        bool ok = F2ModPackage.IncrementalSync(packagePath, updates, deletes, null);
        if (!ok)
        {
            error = "IncrementalSync failed to update package.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Removes a localized string overlay row from book.babel.csv inside the active mod package.
    /// </summary>
    /// <param name="modsRoot">Directory containing mods.</param>
    /// <param name="modName">Active mod package name.</param>
    /// <param name="rowKey">String key or hex ID of the row to remove.</param>
    /// <param name="error">Error description if the revert failed.</param>
    /// <returns>True on success; false otherwise.</returns>
    public static bool RevertBabelString(
        string modsRoot, string modName, string rowKey, out string error)
    {
        error = string.Empty;
        string packagePath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
        if (!File.Exists(packagePath))
        {
            error = $"Package not found: {packagePath}";
            return false;
        }

        byte[]? existingBytes = null;
        if (F2ModPackage.TryOpen(packagePath, out F2ModPackage? pkg, out string openErr) && pkg is not null)
        {
            using (pkg)
            {
                if (pkg.TryGetEntry(BabelSubPath, out F2ModEntry? entry) && entry is not null)
                {
                    existingBytes = pkg.ReadEntryBytes(entry);
                }
            }
        }
        else
        {
            error = openErr;
            return false;
        }

        if (existingBytes is null || existingBytes.Length == 0)
        {
            error = "book.babel.csv not found in package.";
            return false;
        }

        string csvText = Encoding.UTF8.GetString(existingBytes);
        var rows = BabelOverlay.ParseCsv(csvText, new List<string>());
        int removed = rows.RemoveAll(r => r.Key.Equals(rowKey, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            error = $"Key '{rowKey}' was not found in book.babel.csv.";
            return false;
        }

        var updates = new List<(string Path, byte[] Bytes)>();
        var deletes = new List<string>();

        if (rows.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Refii Babel Text Overlays");
            foreach (var r in rows)
            {
                sb.AppendLine($"{r.Key},{r.Value}");
            }
            updates.Add((BabelSubPath, Encoding.UTF8.GetBytes(sb.ToString())));
        }
        else
        {
            deletes.Add(BabelSubPath);
        }

        bool ok = F2ModPackage.IncrementalSync(packagePath, updates, deletes, null);
        if (!ok)
        {
            error = "IncrementalSync failed to update book.babel.csv.";
            return false;
        }
        return true;
    }
}
