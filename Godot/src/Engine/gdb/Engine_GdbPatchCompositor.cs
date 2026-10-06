using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using Refii.Bnk;
using Refii.Mods;

namespace Refii.Gdb;

/// <summary>
/// Represents a conflict where a higher-priority mod has overridden a record provided by one or more lower-priority mods.
/// </summary>
public sealed class GdbRecordConflict
{
    public uint RecordHash { get; set; }
    public string RecordName { get; set; } = string.Empty;
    public uint PropertyHash { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string DatabaseSubPath { get; set; } = string.Empty;
    public string WinnerMod { get; set; } = string.Empty;
    public List<string> LoserMods { get; } = new();

    public override string ToString()
    {
        string name = string.IsNullOrEmpty(RecordName) ? $"0x{RecordHash:X8}" : $"{RecordName} (0x{RecordHash:X8})";
        string prop = !string.IsNullOrEmpty(PropertyName) ? $"[{PropertyName}] " : string.Empty;
        return $"{prop}{name} in {DatabaseSubPath}: won by '{WinnerMod}', overridden in [{string.Join(", ", LoserMods)}]";
    }
}

/// <summary>
/// Summary of a composited Title Update database container.
/// </summary>
public sealed class CompositeGdbResult
{
    public string SubPath { get; set; } = string.Empty;
    public string CachePath { get; set; } = string.Empty;
    public byte[] Binary { get; set; } = Array.Empty<byte>();
    public string WinningMod { get; set; } = string.Empty;
    public List<string> ContributingMods { get; } = new();
    public List<GdbRecordConflict> Conflicts { get; } = new();
}

/// <summary>
/// Merges Title Update (.gdb.tu) databases across multiple enabled mods respecting load order precedence,
/// caches composite output, and mounts virtual Title Update containers into BnkFileSystem.
/// </summary>
public static class GdbPatchCompositor
{
    public const string CompositeCacheDirName = ".cache/gdb_composite";

    public static Action<string>? LogHandler;
    public static Action<string>? WarnHandler;

    private static void LogPrint(string message)
    {
        if (LogHandler is not null)
        {
            LogHandler(message);
            return;
        }

        if (AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
            System.Environment.CommandLine.Contains("testhost", StringComparison.OrdinalIgnoreCase))
        {
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

        if (AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
            System.Environment.CommandLine.Contains("testhost", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try { GD.PushWarning(message); } catch { }
    }

    /// <summary>
    /// Deletes any cached composite .gdb.tu files and directories.
    /// </summary>
    public static void PurgeCache(string modsRoot)
    {
        if (string.IsNullOrEmpty(modsRoot))
        {
            return;
        }

        string cacheRoot = Path.Combine(modsRoot, CompositeCacheDirName.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(cacheRoot))
        {
            try
            {
                Directory.Delete(cacheRoot, recursive: true);
                LogPrint($"[mods] purged composite cache at {cacheRoot}");
            }
            catch (Exception ex)
            {
                LogWarning($"[mods] failed to purge composite cache at {cacheRoot}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Detects all record-level conflicts across enabled mods without mounting the full filesystem.
    /// Used by ModIndex and the launcher ModdingPanel to display granular warning indicators.
    /// </summary>
    public static List<GdbRecordConflict> DetectRecordConflicts(
        string modsRoot,
        ModLoadOrder order,
        IReadOnlyList<ModInstall> installs)
    {
        var conflicts = new List<GdbRecordConflict>();
        if (string.IsNullOrEmpty(modsRoot) || order.Enabled.Count < 2)
        {
            return conflicts;
        }

        // Gather enabled mods in mount order (lowest priority first -> highest priority last):
        var enabledInMountOrder = new List<ModInstall>();
        for (int i = order.Enabled.Count - 1; i >= 0; i--)
        {
            string name = order.Enabled[i];
            ModInstall? mod = installs.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (mod is not null)
            {
                enabledInMountOrder.Add(mod);
            }
        }

        // Discover all .gdb.tu files across enabled mods grouped by normalized subpath:
        var tuFilesBySubPath = DiscoverTuFiles(enabledInMountOrder);

        foreach (var (subPath, modFiles) in tuFilesBySubPath)
        {
            if (modFiles.Count < 2)
            {
                continue;
            }

            var (_, subConflicts) = MergeDatabases(subPath, modFiles);
            conflicts.AddRange(subConflicts);
        }

        return conflicts;
    }

    /// <summary>
    /// Applies the multi-mod Title Update compositor across all mounted overlays.
    /// Merges sparse .gdb.tu files, caches results, and registers composite virtual files into BnkFileSystem.
    /// </summary>
    public static List<CompositeGdbResult> Apply(
        BnkFileSystem? mounted,
        IReadOnlyList<ModOverlay> overlays,
        string modsRoot)
    {
        var results = new List<CompositeGdbResult>();
        if (overlays.Count == 0)
        {
            PurgeCache(modsRoot);
            return results;
        }

        // overlays is already LOWEST PRIORITY FIRST:
        var tuFilesBySubPath = new Dictionary<string, List<(string ModName, string DiskPath)>>(StringComparer.OrdinalIgnoreCase);

        foreach (ModOverlay overlay in overlays)
        {
            string sourcePath = !string.IsNullOrEmpty(overlay.PackagePath) ? overlay.PackagePath : overlay.AssetRoot;
            if (File.Exists(sourcePath) && sourcePath.EndsWith(".f2mod", StringComparison.OrdinalIgnoreCase))
            {
                if (F2ModPackage.TryOpen(sourcePath, out F2ModPackage? pkg, out _) && pkg is not null)
                {
                    using (pkg)
                    {
                        foreach (F2ModEntry entry in pkg.Entries)
                        {
                            if (!entry.Path.EndsWith(".gdb.tu", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            string normalized = ModLoadOrder.Normalize(entry.Path);
                            if (!tuFilesBySubPath.TryGetValue(normalized, out var list))
                            {
                                list = new List<(string, string)>();
                                tuFilesBySubPath[normalized] = list;
                            }

                            list.Add((overlay.Name, sourcePath));
                        }
                    }
                }
                continue;
            }

            if (!Directory.Exists(overlay.AssetRoot))
            {
                continue;
            }

            foreach (string file in ModManifest.EnumerateAssets(overlay.AssetRoot))
            {
                if (!file.EndsWith(".gdb.tu", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(overlay.AssetRoot, file);
                string normalized = ModLoadOrder.Normalize(relative);
                if (normalized.Length == 0)
                {
                    continue;
                }

                if (!tuFilesBySubPath.TryGetValue(normalized, out var list))
                {
                    list = new List<(string, string)>();
                    tuFilesBySubPath[normalized] = list;
                }

                list.Add((overlay.Name, file));
            }
        }

        if (tuFilesBySubPath.Count == 0)
        {
            PurgeCache(modsRoot);
            return results;
        }

        string cacheRoot = Path.Combine(modsRoot, CompositeCacheDirName.Replace('/', Path.DirectorySeparatorChar));

        // Clean up any stale cached composite files that are no longer contributed by active overlays:
        if (Directory.Exists(cacheRoot))
        {
            foreach (string file in Directory.GetFiles(cacheRoot, "*.gdb.tu", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(cacheRoot, file);
                string norm = ModLoadOrder.Normalize(rel);
                if (!tuFilesBySubPath.ContainsKey(norm))
                {
                    try
                    {
                        File.Delete(file);
                        string stamp = file + ".stamp";
                        if (File.Exists(stamp)) File.Delete(stamp);
                    }
                    catch { }
                }
            }
        }

        foreach (var (subPath, modFiles) in tuFilesBySubPath)
        {
            string cachePath = Path.Combine(cacheRoot, subPath.Replace('/', Path.DirectorySeparatorChar));
            string stampPath = cachePath + ".stamp";
            string currentStamp = ComputeCompositeStamp(modFiles);

            byte[] binary;
            List<GdbRecordConflict> conflicts = new();
            string winningMod = modFiles[^1].ModName;

            // Check disk cache first:
            if (File.Exists(cachePath) && File.Exists(stampPath) &&
                string.Equals(File.ReadAllText(stampPath).Trim(), currentStamp, StringComparison.Ordinal))
            {
                try
                {
                    binary = File.ReadAllBytes(cachePath);
                    if (modFiles.Count > 1)
                    {
                        // Re-discover conflict records for reporting:
                        (_, conflicts) = MergeDatabases(subPath, modFiles);
                    }
                }
                catch
                {
                    binary = Array.Empty<byte>();
                }
            }
            else
            {
                binary = Array.Empty<byte>();
            }

            if (binary.Length == 0)
            {
                GdbPatchDatabase mergedDb;
                if (modFiles.Count == 1)
                {
                    mergedDb = LoadTuPatch(modFiles[0].DiskPath, subPath);
                }
                else
                {
                    (mergedDb, conflicts) = MergeDatabases(subPath, modFiles);
                }

                // Synthesize complete native records for the game engine so omitted baseline fields are preserved:
                GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;
                GdbPatchDatabase completeDb = GdbPatchBuilder.SynthesizeCompleteDatabase(mergedDb, baseMulti);

                binary = GdbWriter.Serialize(completeDb);

                try
                {
                    string? dir = Path.GetDirectoryName(cachePath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.WriteAllBytes(cachePath, binary);
                    File.WriteAllText(stampPath, currentStamp);
                    LogPrint($"[gdb_compositor] Composited {completeDb.Entries.Count} record(s) across {modFiles.Count} mod(s) into {cachePath}");
                }
                catch (Exception ex)
                {
                    LogWarning($"[gdb_compositor] Failed to write composite cache {cachePath}: {ex.Message}");
                }
            }

            // Inject composite virtual file into BnkFileSystem if mounted:
            byte[] capturedBinary = binary;
            mounted?.AddVirtualFile(
                subPath,
                File.Exists(cachePath) ? cachePath : modFiles[^1].DiskPath,
                capturedBinary.Length,
                () => capturedBinary,
                winningMod);

            var res = new CompositeGdbResult
            {
                SubPath = subPath,
                CachePath = cachePath,
                Binary = capturedBinary,
                WinningMod = winningMod,
            };
            res.ContributingMods.AddRange(modFiles.Select(m => m.ModName));
            res.Conflicts.AddRange(conflicts);
            results.Add(res);
        }

        return results;
    }

    private static Dictionary<string, List<(string ModName, string DiskPath)>> DiscoverTuFiles(
        IReadOnlyList<ModInstall> enabledModsInMountOrder)
    {
        var tuFilesBySubPath = new Dictionary<string, List<(string ModName, string DiskPath)>>(StringComparer.OrdinalIgnoreCase);

        foreach (ModInstall mod in enabledModsInMountOrder)
        {
            if (File.Exists(mod.PackagePath) && mod.PackagePath.EndsWith(".f2mod", StringComparison.OrdinalIgnoreCase))
            {
                if (F2ModPackage.TryOpen(mod.PackagePath, out F2ModPackage? pkg, out _) && pkg is not null)
                {
                    using (pkg)
                    {
                        foreach (F2ModEntry entry in pkg.Entries)
                        {
                            if (!entry.Path.EndsWith(".gdb.tu", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            string normalized = ModLoadOrder.Normalize(entry.Path);
                            if (!tuFilesBySubPath.TryGetValue(normalized, out var list))
                            {
                                list = new List<(string, string)>();
                                tuFilesBySubPath[normalized] = list;
                            }

                            list.Add((mod.Name, mod.PackagePath));
                        }
                    }
                }
                continue;
            }

            string assetRoot = ModWorkspace.GetModAssetRoot(mod);
            if (!Directory.Exists(assetRoot))
            {
                continue;
            }

            foreach (string file in ModManifest.EnumerateAssets(assetRoot))
            {
                if (!file.EndsWith(".gdb.tu", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(assetRoot, file);
                string normalized = ModLoadOrder.Normalize(relative);
                if (normalized.Length == 0)
                {
                    continue;
                }

                if (!tuFilesBySubPath.TryGetValue(normalized, out var list))
                {
                    list = new List<(string, string)>();
                    tuFilesBySubPath[normalized] = list;
                }

                list.Add((mod.Name, file));
            }
        }

        return tuFilesBySubPath;
    }

    private static GdbPatchDatabase LoadTuPatch(string sourcePath, string subPath)
    {
        if (sourcePath.EndsWith(".f2mod", StringComparison.OrdinalIgnoreCase))
        {
            if (F2ModPackage.TryOpen(sourcePath, out F2ModPackage? pkg, out _) && pkg is not null)
            {
                using (pkg)
                {
                    if (pkg.TryGetEntry(subPath, out F2ModEntry? entry) && entry is not null)
                    {
                        return GdbPatchBuilder.LoadOrCreatePatchDatabase(pkg.ReadEntryBytes(entry));
                    }
                }
            }
            return new GdbPatchDatabase();
        }

        return GdbPatchBuilder.LoadOrCreatePatchDatabase(sourcePath);
    }

    /// <summary>
    /// Merges sparse GDB patch databases from multiple mods in mount order (lowest priority first).
    /// When multiple mods touch the same record, their modifications are composited at the property level.
    /// Higher priority mods overwrite colliding properties from lower priority mods.
    /// Non-colliding properties from lower priority mods are preserved.
    /// </summary>
    public static (GdbPatchDatabase MergedDb, List<GdbRecordConflict> Conflicts) MergeDatabases(
        string subPath,
        IReadOnlyList<(string ModName, string DiskPath)> modFiles)
    {
        var merged = new GdbPatchDatabase();
        var recordMap = new Dictionary<uint, GdbPatchEntry>();
        var propertyOwners = new Dictionary<(uint RecordId, uint PropHash), string>();
        var conflictsByRecordProp = new Dictionary<(uint RecordId, uint PropHash), GdbRecordConflict>();

        GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;

        foreach (var (modName, diskPath) in modFiles)
        {
            GdbPatchDatabase db = LoadTuPatch(diskPath, subPath);

            foreach (GdbPatchEntry entry in db.Entries)
            {
                // Identify which properties were actually authored/modified by this mod:
                var authoredHashes = new HashSet<uint>();
                foreach (GdbPatchProperty prop in entry.Properties)
                {
                    if (db.Names.ContainsKey(prop.NameHash))
                    {
                        authoredHashes.Add(prop.NameHash);
                    }
                    else if (baseMulti != null && baseMulti.TryLookup(entry.Id, out GdbCursor baseCursor))
                    {
                        if (baseMulti.TryFindInherited(baseCursor, prop.NameHash, -1, out _, out uint baseRaw, out _))
                        {
                            if (baseRaw != prop.Value)
                            {
                                authoredHashes.Add(prop.NameHash);
                            }
                        }
                        else
                        {
                            authoredHashes.Add(prop.NameHash);
                        }
                    }
                }

                if (authoredHashes.Count == 0 && entry.Properties.Count > 0)
                {
                    foreach (GdbPatchProperty prop in entry.Properties)
                    {
                        if (prop.NameHash != GdbView.HashParent)
                        {
                            authoredHashes.Add(prop.NameHash);
                        }
                    }
                }

                if (!recordMap.TryGetValue(entry.Id, out GdbPatchEntry? existingEntry))
                {
                    var newEntry = new GdbPatchEntry(entry.Id, entry.SchemaFlags);
                    foreach (GdbPatchProperty prop in entry.Properties)
                    {
                        newEntry.Properties.Add(new GdbPatchProperty(prop.NameHash, prop.Type, prop.Value));
                        if (authoredHashes.Contains(prop.NameHash))
                        {
                            propertyOwners[(entry.Id, prop.NameHash)] = modName;
                        }
                    }
                    merged.Entries.Add(newEntry);
                    recordMap[entry.Id] = newEntry;
                }
                else
                {
                    foreach (GdbPatchProperty prop in entry.Properties)
                    {
                        if (!authoredHashes.Contains(prop.NameHash))
                        {
                            continue;
                        }

                        var key = (entry.Id, prop.NameHash);
                        if (propertyOwners.TryGetValue(key, out string? previousOwner) &&
                            !string.Equals(previousOwner, modName, StringComparison.OrdinalIgnoreCase))
                        {
                            int propIdx = existingEntry.Properties.FindIndex(p => p.NameHash == prop.NameHash);
                            if (propIdx >= 0)
                            {
                                existingEntry.Properties[propIdx] = new GdbPatchProperty(prop.NameHash, prop.Type, prop.Value);
                            }
                            else
                            {
                                existingEntry.Properties.Add(new GdbPatchProperty(prop.NameHash, prop.Type, prop.Value));
                            }
                            propertyOwners[key] = modName;

                            string recName = db.Names.TryGetValue(entry.Id, out string? foundRecName) && !string.IsNullOrEmpty(foundRecName)
                                ? foundRecName
                                : (merged.Names.TryGetValue(entry.Id, out string? prevRecName) && !string.IsNullOrEmpty(prevRecName)
                                    ? prevRecName
                                    : ResolveAuthoredRecordName(entry.Id));

                            string propName = db.Names.TryGetValue(prop.NameHash, out string? foundPropName) && !string.IsNullOrEmpty(foundPropName)
                                ? foundPropName
                                : (merged.Names.TryGetValue(prop.NameHash, out string? prevPropName) && !string.IsNullOrEmpty(prevPropName)
                                    ? prevPropName
                                    : ResolveAuthoredPropertyName(prop.NameHash));

                            if (!conflictsByRecordProp.TryGetValue(key, out GdbRecordConflict? conflict))
                            {
                                conflict = new GdbRecordConflict
                                {
                                    RecordHash = entry.Id,
                                    RecordName = recName,
                                    PropertyHash = prop.NameHash,
                                    PropertyName = propName,
                                    DatabaseSubPath = subPath,
                                    WinnerMod = modName,
                                };
                                conflict.LoserMods.Add(previousOwner);
                                conflictsByRecordProp[key] = conflict;
                            }
                            else
                            {
                                conflict.WinnerMod = modName;
                                if (!conflict.LoserMods.Contains(previousOwner))
                                {
                                    conflict.LoserMods.Add(previousOwner);
                                }
                            }
                        }
                        else
                        {
                            int propIdx = existingEntry.Properties.FindIndex(p => p.NameHash == prop.NameHash);
                            if (propIdx >= 0)
                            {
                                existingEntry.Properties[propIdx] = new GdbPatchProperty(prop.NameHash, prop.Type, prop.Value);
                            }
                            else
                            {
                                existingEntry.Properties.Add(new GdbPatchProperty(prop.NameHash, prop.Type, prop.Value));
                            }
                            propertyOwners[key] = modName;
                        }
                    }
                }
            }

            foreach (var (hash, name) in db.Names)
            {
                if (!string.IsNullOrEmpty(name))
                {
                    merged.Names[hash] = name;
                }
            }
        }

        return (merged, conflictsByRecordProp.Values.ToList());
    }

    private static string ResolveAuthoredPropertyName(uint hash)
    {
        try
        {
            string name = Engine_GameGdb.Database?.NameOf(hash) ?? string.Empty;
            if (!string.IsNullOrEmpty(name)) return name;
            name = Engine_GameGdb.BaseDatabase?.NameOf(hash) ?? string.Empty;
            if (!string.IsNullOrEmpty(name)) return name;
        }
        catch
        {
        }
        return $"0x{hash:X8}";
    }

    private static string ResolveAuthoredRecordName(uint hash)
    {
        try
        {
            string name = Engine_GameGdb.Database?.NameOf(hash) ?? string.Empty;
            if (!string.IsNullOrEmpty(name)) return name;
            name = Engine_GameGdb.BaseDatabase?.NameOf(hash) ?? string.Empty;
            if (!string.IsNullOrEmpty(name)) return name;

            if (Refii.Equipment.Engine_WeaponIndex.TryGetByHash(hash, out var weaponEntry))
            {
                string label = weaponEntry.Name;
                if (weaponEntry.Detail.WeaponComponent == hash)
                {
                    label += " (WeaponComponent)";
                }
                return label;
            }

            if (Refii.Equipment.Engine_ItemIndex.TryGetByHash(hash, out var itemEntry))
            {
                string label = itemEntry.Name;
                if (itemEntry.Detail.InventoryComponentHash == hash)
                {
                    label += " (InventoryItemComponent)";
                }
                return label;
            }
        }
        catch
        {
        }
        return $"0x{hash:X8}";
    }

    private static string ComputeCompositeStamp(IReadOnlyList<(string ModName, string DiskPath)> modFiles)
    {
        var sb = new StringBuilder();
        foreach (var (modName, diskPath) in modFiles)
        {
            sb.Append(modName).Append(':');
            try
            {
                var fi = new FileInfo(diskPath);
                sb.Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append(';');
            }
            catch
            {
                sb.Append("missing;");
            }
        }
        return sb.ToString();
    }
}
