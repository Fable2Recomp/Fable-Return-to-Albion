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

    /// <summary>Authored changes that reached the merged patch: values set, properties and entries added.</summary>
    public int Applied { get; set; }

    /// <summary>Rows a <c>.gdb.csv</c> stated that were not applied, each with its reason.</summary>
    public List<string> Refused { get; } = new();

    /// <summary>Entries the base database lacks, by name or hash.</summary>
    public List<string> AddedEntries { get; } = new();

    /// <summary>Properties no record of an entry's chain declared, as <c>Entry.Property</c>.</summary>
    public List<string> AddedProperties { get; } = new();

    /// <summary>Inherited properties now declared on the entry itself, as <c>Entry.Property</c>.</summary>
    public List<string> Materialised { get; } = new();
}

/// <summary>
/// One mod's contribution to one patch: a <c>.gdb.tu</c> or a <c>.gdb.csv</c>, loose on disk or
/// inside a package. <see cref="EntryPath"/> is the package entry (or the loose file) that holds it.
/// </summary>
public readonly record struct GdbPatchSource(string ModName, string DiskPath, string EntryPath)
{
    public bool IsCsv => EntryPath.EndsWith(GdbCsvOverlay.CsvSuffix, StringComparison.OrdinalIgnoreCase);
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
    /// The GDB record ids each mod ADDS — entries its patches carry that the base database lacks —
    /// by mod name. What a save's mod stamp carries so a save can be filtered for a mod that is
    /// no longer installed (<see cref="Refii.Mods.ModSaveStamp"/>). Read from the same sources the
    /// composite is merged from; a record two mods add is in both sets.
    /// </summary>
    public static Dictionary<string, IReadOnlyCollection<uint>> AddedRecordsByMod(
        BnkFileSystem? mounted,
        IReadOnlyList<ModOverlay> overlays)
    {
        var byMod = new Dictionary<string, IReadOnlyCollection<uint>>(StringComparer.OrdinalIgnoreCase);
        var installs = new List<ModInstall>(overlays.Count);
        foreach (ModOverlay overlay in overlays)
        {
            installs.Add(new ModInstall
            {
                Name = overlay.Name,
                PackagePath = !string.IsNullOrEmpty(overlay.PackagePath) ? overlay.PackagePath : overlay.AssetRoot,
            });
        }

        foreach (var (subPath, sources) in DiscoverTuFiles(installs))
        {
            GdbMulti? baseMulti = BaseFor(subPath, mounted);
            foreach (GdbPatchSource source in sources)
            {
                var refused = new List<string>();
                GdbPatchDatabase db;
                try
                {
                    db = LoadTuPatch(source, subPath, baseMulti, refused);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
                {
                    continue;
                }

                if (!byMod.TryGetValue(source.ModName, out IReadOnlyCollection<uint>? held) || held is not HashSet<uint> set)
                {
                    set = new HashSet<uint>();
                    byMod[source.ModName] = set;
                }

                foreach (GdbPatchEntry entry in db.Entries)
                {
                    if (baseMulti is null || !baseMulti.TryLookup(entry.Id, out GdbCursor at) || !at.Valid)
                    {
                        set.Add(entry.Id);
                    }
                }
            }
        }

        return byMod;
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

            var (_, subConflicts) = MergeSources(subPath, modFiles, BaseFor(subPath, null), new List<string>());
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

        // overlays is already LOWEST PRIORITY FIRST. A loose mod's root IS its "package path"
        // (ModWorkspace.GetModAssetRoot), which is how the discovery tells the two apart.
        var installs = new List<ModInstall>(overlays.Count);
        foreach (ModOverlay overlay in overlays)
        {
            installs.Add(new ModInstall
            {
                Name = overlay.Name,
                PackagePath = !string.IsNullOrEmpty(overlay.PackagePath) ? overlay.PackagePath : overlay.AssetRoot,
            });
        }

        var tuFilesBySubPath = DiscoverTuFiles(installs);

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
            var refused = new List<string>();
            string winningMod = modFiles[^1].ModName;
            GdbMulti? baseMulti = BaseFor(subPath, mounted);

            // THE MERGE IS ALWAYS RUN: it is what the report states (who set what, who lost, what
            // was refused, what was added), and it is cheap next to the synthesis and the write,
            // which the cache does skip.
            (GdbPatchDatabase mergedDb, List<GdbRecordConflict> conflicts) = MergeSources(subPath, modFiles, baseMulti, refused);

            // Check disk cache first:
            if (File.Exists(cachePath) && File.Exists(stampPath) &&
                string.Equals(File.ReadAllText(stampPath).Trim(), currentStamp, StringComparison.Ordinal))
            {
                try
                {
                    binary = File.ReadAllBytes(cachePath);
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
                // Synthesize complete native records for the game engine so omitted baseline fields are preserved:
                GdbPatchDatabase completeDb = GdbPatchBuilder.SynthesizeCompleteDatabase(mergedDb, baseMulti);

                try
                {
                    binary = GdbWriter.Serialize(completeDb);
                }
                catch (InvalidOperationException ex)
                {
                    // The writer's round-trip refused the container; the guest would have crashed
                    // on it at boot. Nothing is mounted for this database and the report says why.
                    refused.Add($"{subPath}: the merged patch could not be written — {ex.Message}");
                    var failed = new CompositeGdbResult
                    {
                        SubPath = subPath,
                        CachePath = cachePath,
                        WinningMod = winningMod,
                    };
                    failed.ContributingMods.AddRange(modFiles.Select(m => m.ModName));
                    failed.Conflicts.AddRange(conflicts);
                    failed.Refused.AddRange(refused);
                    results.Add(failed);
                    LogWarning($"[gdb_compositor] {refused[^1]}");
                    continue;
                }

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
            res.Refused.AddRange(refused);
            Classify(mergedDb, baseMulti, res);
            results.Add(res);
        }

        SynthesizeMissingGmdSidecars(mounted, results);
        return results;
    }

    /// <summary>
    /// Ensures any record-scoped <c>mods/.../*.mdl</c> referenced by a <c>.gdb.tu</c> record has a companion
    /// <c>.mdl.gmd</c> in <paramref name="mounted"/> (inheriting the record's vanilla <c>.mdl.gmd</c> dummy transforms
    /// if the <c>.f2mod</c> package did not already include one).
    /// </summary>
    private static void SynthesizeMissingGmdSidecars(
        BnkFileSystem? mounted,
        IReadOnlyList<CompositeGdbResult> results)
    {
        if (mounted is null || results.Count == 0)
        {
            return;
        }

        var missingMdlByHash = new Dictionary<uint, BnkFile>();
        foreach (BnkFile file in mounted.EnumerateAllFiles())
        {
            if (file.VirtualPath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) &&
                file.VirtualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) &&
                !mounted.TryGetFile(file.VirtualPath + ".gmd", out _))
            {
                uint hash = Refii.Level.Engine_PropModelIndex.PathHash(file.VirtualPath);
                if (hash != 0)
                {
                    missingMdlByHash[hash] = file;
                }
            }
        }

        if (missingMdlByHash.Count == 0)
        {
            return;
        }

        GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;
        if (baseMulti is null)
        {
            return;
        }

        foreach (CompositeGdbResult res in results)
        {
            if (res.Binary is null || res.Binary.Length == 0)
            {
                continue;
            }

            GdbPatchDatabase db = GdbPatchBuilder.LoadOrCreatePatchDatabase(res.Binary);
            foreach (GdbPatchEntry rec in db.Entries)
            {
                foreach (GdbPatchProperty prop in rec.Properties)
                {
                    if (!missingMdlByHash.TryGetValue(prop.Value, out BnkFile? mdlFile) || mdlFile is null)
                    {
                        continue;
                    }

                    if (!baseMulti.TryLookup(rec.Id, out GdbCursor baseCursor) || !baseCursor.Valid ||
                        !baseMulti.TryFindInherited(baseCursor, prop.NameHash, -1, out _, out uint vanillaModelHash, out _) ||
                        vanillaModelHash == 0 || vanillaModelHash == GdbHash.Null)
                    {
                        continue;
                    }

                    string? vanillaMdlPath = Refii.Level.Engine_PropModelIndex.Resolve(vanillaModelHash, Array.Empty<string>());
                    if (string.IsNullOrWhiteSpace(vanillaMdlPath))
                    {
                        string sym = baseMulti.NameOf(vanillaModelHash);
                        if (!string.IsNullOrWhiteSpace(sym) && sym.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                        {
                            vanillaMdlPath = sym;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(vanillaMdlPath))
                    {
                        continue;
                    }

                    string vanillaGmdPath = vanillaMdlPath.Replace('\\', '/').TrimStart('/') + ".gmd";
                    if (mounted.TryGetFile(vanillaGmdPath, out BnkFile vanillaGmdFile))
                    {
                        BnkFile capturedGmd = vanillaGmdFile;
                        mounted.AddVirtualFile(
                            mdlFile.VirtualPath + ".gmd",
                            capturedGmd.SourceArchivePath,
                            (int)capturedGmd.Size,
                            () => capturedGmd.ReadAllBytes(),
                            !string.IsNullOrEmpty(mdlFile.SourceMod) ? mdlFile.SourceMod : res.WinningMod);
                        missingMdlByHash.Remove(prop.Value);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The patch a path contributes to: a <c>.gdb.tu</c> is itself, a <c>.gdb.csv</c> compiles
    /// to the <c>.gdb.tu</c> beside the database it names. Empty for any other path.
    /// </summary>
    public static string PatchSubPathOf(string normalizedPath)
    {
        if (normalizedPath.EndsWith(".gdb.tu", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedPath;
        }

        if (normalizedPath.EndsWith(GdbCsvOverlay.CsvSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedPath[..^4] + ".tu";
        }

        return string.Empty;
    }

    /// <summary>
    /// Every mod's patch sources by the patch they contribute to, in mount order. A mod may ship
    /// both a <c>.gdb.tu</c> and a <c>.gdb.csv</c> for one database; both merge, the rows last.
    /// </summary>
    private static Dictionary<string, List<GdbPatchSource>> DiscoverTuFiles(
        IReadOnlyList<ModInstall> enabledModsInMountOrder)
    {
        var tuFilesBySubPath = new Dictionary<string, List<GdbPatchSource>>(StringComparer.OrdinalIgnoreCase);

        void Add(string subPath, GdbPatchSource source)
        {
            if (!tuFilesBySubPath.TryGetValue(subPath, out var list))
            {
                list = new List<GdbPatchSource>();
                tuFilesBySubPath[subPath] = list;
            }

            // Within one mod the binary patch comes first and the rows after, so a hand-written
            // row overrides what the Studio compiled.
            int insertAt = list.Count;
            if (!source.IsCsv)
            {
                while (insertAt > 0 && list[insertAt - 1].IsCsv &&
                    string.Equals(list[insertAt - 1].ModName, source.ModName, StringComparison.OrdinalIgnoreCase))
                {
                    insertAt--;
                }
            }

            list.Insert(insertAt, source);
        }

        foreach (ModInstall mod in enabledModsInMountOrder)
        {
            if (File.Exists(mod.PackagePath) && Refii.Core.Engine_Title.IsModPackagePath(mod.PackagePath))
            {
                if (F2ModPackage.TryOpen(mod.PackagePath, out F2ModPackage? pkg, out _) && pkg is not null)
                {
                    using (pkg)
                    {
                        foreach (F2ModEntry entry in pkg.Entries)
                        {
                            string normalized = ModLoadOrder.Normalize(entry.Path);
                            string subPath = PatchSubPathOf(normalized);
                            if (subPath.Length == 0)
                            {
                                continue;
                            }

                            Add(subPath, new GdbPatchSource(mod.Name, mod.PackagePath, entry.Path));
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
                string relative = Path.GetRelativePath(assetRoot, file);
                string normalized = ModLoadOrder.Normalize(relative);
                string subPath = PatchSubPathOf(normalized);
                if (subPath.Length == 0)
                {
                    continue;
                }

                Add(subPath, new GdbPatchSource(mod.Name, file, file));
            }
        }

        return tuFilesBySubPath;
    }

    /// <summary>
    /// The unpatched records a patch on <paramref name="subPath"/> is read against: the shared
    /// supplemental set, plus the database the patch names when it is not among them.
    /// </summary>
    private static GdbMulti? BaseFor(string subPath, BnkFileSystem? mounted)
    {
        GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;
        if (!subPath.EndsWith(".gdb.tu", StringComparison.OrdinalIgnoreCase))
        {
            return baseMulti;
        }

        string baseGdbSubPath = subPath[..^3];
        if (baseMulti is not null)
        {
            foreach (string supplemental in Refii.Level.Engine_LevelProps.SupplementalGdbPaths)
            {
                if (string.Equals(ModLoadOrder.Normalize(supplemental), baseGdbSubPath, StringComparison.OrdinalIgnoreCase))
                {
                    return baseMulti;
                }
            }
        }

        byte[]? baseGdbBytes = mounted is not null
            ? mounted.ReadBaseBytes(baseGdbSubPath) ?? mounted.ReadBaseBytes("data/" + baseGdbSubPath)
            : Engine_GameGdb.ReadBaseBytes(baseGdbSubPath);
        if (baseGdbBytes is not { Length: > 0 })
        {
            return baseMulti;
        }

        return baseMulti is not null
            ? GdbMulti.BuildWithOverlays(baseMulti, new[] { baseGdbBytes })
            : GdbMulti.Build(new[] { baseGdbBytes });
    }

    /// <summary>
    /// One source as a sparse patch database. A <c>.gdb.csv</c> is compiled here against
    /// <paramref name="baseMulti"/>; its refused rows go to <paramref name="refused"/>, prefixed
    /// with the mod and the file.
    /// </summary>
    private static GdbPatchDatabase LoadTuPatch(GdbPatchSource source, string subPath, GdbMulti? baseMulti, List<string> refused)
    {
        byte[]? bytes = null;
        if (Refii.Core.Engine_Title.IsModPackagePath(source.DiskPath))
        {
            if (F2ModPackage.TryOpen(source.DiskPath, out F2ModPackage? pkg, out _) && pkg is not null)
            {
                using (pkg)
                {
                    string entryPath = source.EntryPath.Length != 0 ? source.EntryPath : subPath;
                    if (pkg.TryGetEntry(entryPath, out F2ModEntry? entry) && entry is not null)
                    {
                        bytes = pkg.ReadEntryBytes(entry);
                    }
                }
            }
        }
        else
        {
            try
            {
                bytes = File.ReadAllBytes(source.DiskPath);
            }
            catch (Exception ex)
            {
                refused.Add($"{source.ModName}: {source.EntryPath}: {ex.Message}");
                return new GdbPatchDatabase();
            }
        }

        if (bytes is null)
        {
            return new GdbPatchDatabase();
        }

        if (!source.IsCsv)
        {
            return GdbPatchBuilder.LoadOrCreatePatchDatabase(bytes);
        }

        if (baseMulti is null)
        {
            refused.Add($"{source.ModName}: {source.EntryPath}: no database to read the rows against; the file is skipped");
            return new GdbPatchDatabase();
        }

        GdbCsvResult compiled = GdbCsvOverlay.Parse(Encoding.UTF8.GetString(bytes), baseMulti);
        foreach (string error in compiled.Errors)
        {
            refused.Add($"{source.ModName}: {source.EntryPath}: {error}");
        }

        return compiled.Database;
    }

    /// <summary>
    /// What the merged patch does to the base, for the report: an entry the base lacks is ADDED;
    /// a property no record of the entry's chain declares is ADDED; one an ancestor declares is
    /// MATERIALISED on the entry; the rest are values set. A property equal to the base's own
    /// value is not a change (a <c>.gdb.tu</c> carries flattened baseline fields).
    /// </summary>
    public static void Classify(GdbPatchDatabase merged, GdbMulti? baseMulti, CompositeGdbResult result)
    {
        string EntryName(uint id)
        {
            if (merged.Names.TryGetValue(id, out string? own) && own.Length != 0)
            {
                return own;
            }

            string known = baseMulti?.NameOf(id) ?? string.Empty;
            return known.Length != 0 ? known : $"0x{id:X8}";
        }

        string PropertyName(uint hash)
        {
            if (merged.Names.TryGetValue(hash, out string? own) && own.Length != 0)
            {
                return own;
            }

            string known = baseMulti?.NameOf(hash) ?? string.Empty;
            return known.Length != 0 ? known : $"0x{hash:X8}";
        }

        foreach (GdbPatchEntry entry in merged.Entries)
        {
            if (baseMulti is null || !baseMulti.TryLookup(entry.Id, out GdbCursor at) || !at.Valid)
            {
                result.AddedEntries.Add(EntryName(entry.Id));
                result.Applied += 1 + entry.Properties.Count(p => p.NameHash != GdbView.HashParent);
                continue;
            }

            foreach (GdbPatchProperty property in entry.Properties)
            {
                if (at.View.TryFindLocalRaw(at.Record, property.NameHash, -1, out uint local, out _))
                {
                    if (local != property.Value)
                    {
                        result.Applied++;
                    }

                    continue;
                }

                string key = $"{EntryName(entry.Id)}.{PropertyName(property.NameHash)}";
                if (baseMulti.TryFindInherited(at, property.NameHash, -1, out _, out uint inherited, out _))
                {
                    if (inherited != property.Value || merged.Names.ContainsKey(property.NameHash))
                    {
                        result.Materialised.Add(key);
                        result.Applied++;
                    }

                    continue;
                }

                result.AddedProperties.Add(key);
                result.Applied++;
            }
        }
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
        var sources = new List<GdbPatchSource>(modFiles.Count);
        foreach (var (modName, diskPath) in modFiles)
        {
            bool packaged = Refii.Core.Engine_Title.IsModPackagePath(diskPath);
            sources.Add(new GdbPatchSource(modName, diskPath, packaged ? subPath : diskPath));
        }

        return MergeSources(subPath, sources, BaseFor(subPath, null), new List<string>());
    }

    /// <summary><see cref="MergeDatabases"/> over sources of either form, collecting refused rows.</summary>
    public static (GdbPatchDatabase MergedDb, List<GdbRecordConflict> Conflicts) MergeSources(
        string subPath,
        IReadOnlyList<GdbPatchSource> modFiles,
        GdbMulti? baseMulti,
        List<string> refused)
    {
        var merged = new GdbPatchDatabase();
        var recordMap = new Dictionary<uint, GdbPatchEntry>();
        var propertyOwners = new Dictionary<(uint RecordId, uint PropHash), string>();
        var conflictsByRecordProp = new Dictionary<(uint RecordId, uint PropHash), GdbRecordConflict>();

        foreach (GdbPatchSource source in modFiles)
        {
            string modName = source.ModName;
            GdbPatchDatabase db = LoadTuPatch(source, subPath, baseMulti, refused);

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

    private static string ComputeCompositeStamp(IReadOnlyList<GdbPatchSource> modFiles)
    {
        var sb = new StringBuilder();
        foreach (var (modName, diskPath, entryPath) in modFiles)
        {
            sb.Append(modName).Append(':').Append(entryPath).Append(':');
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
