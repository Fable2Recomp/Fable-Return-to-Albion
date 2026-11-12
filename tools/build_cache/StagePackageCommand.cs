namespace Refii.BuildCache;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Options controlling incremental parallel staging of the release package directory.
/// </summary>
/// <param name="RepoRoot">Absolute path to the repository root.</param>
/// <param name="ProjectDir">Absolute path to the <c>Godot/</c> directory.</param>
/// <param name="StagingRoot">Absolute path to <c>Godot/build/&lt;arch&gt;</c>.</param>
/// <param name="WorkDir">Platform packaging work directory (<c>out/package/&lt;platform&gt;-&lt;arch&gt;</c>).</param>
/// <param name="AppName">Application folder name inside <paramref name="WorkDir"/>.</param>
/// <param name="OutBinary">Exported launcher binary name.</param>
/// <param name="ToolPlatform">Target platform (<c>windows</c> or <c>linux</c>).</param>
/// <param name="ToolArch">Target architecture (<c>x86_64</c> or <c>arm64</c>).</param>
/// <param name="BundleZig">Whether to bundle the target Zig toolchain into <c>bin/recompile/zig</c>.</param>
/// <param name="ZigUnpackedDir">Path to the cached unpacked Zig directory containing <c>.refii-zig-stamp</c>.</param>
/// <param name="CrtDirs">Semicolon-separated or pre-split MSVC redistributable search directories on Windows.</param>
/// <param name="ObjdumpPath">Optional path to <c>llvm-objdump</c> / <c>objdump</c> for Windows PE import scanning.</param>
/// <param name="StripPath">Optional path to <c>llvm-strip</c> / <c>strip</c> on Linux.</param>
/// <param name="GodotFloor">Pinned minimum Godot version written to <c>sdk/pins.json</c>.</param>
/// <param name="GodotFlavour">Godot flavour written to <c>sdk/pins.json</c> (default <c>mono</c>).</param>
/// <param name="DotnetMajor">.NET major version written to <c>sdk/pins.json</c> (default <c>8</c>).</param>
/// <param name="CacheDir">Shared build cache root (<c>&lt;repo&gt;/out/cache</c>).</param>
/// <param name="PreservedExportFiles">Optional set of relative paths already synced by <see cref="GodotExportCommand"/>.</param>
public sealed record StagePackageOptions(
    string RepoRoot,
    string ProjectDir,
    string StagingRoot,
    string WorkDir,
    string AppName,
    string OutBinary,
    string ToolPlatform,
    string ToolArch,
    bool BundleZig,
    string ZigUnpackedDir,
    IReadOnlyList<string> CrtDirs,
    string ObjdumpPath,
    string StripPath,
    string GodotFloor,
    string GodotFlavour,
    string DotnetMajor,
    string CacheDir,
    IReadOnlySet<string>? PreservedExportFiles = null);

/// <summary>
/// Telemetry returned by <see cref="StagePackageCommand.Execute"/> for diagnostics and automated tests.
/// </summary>
/// <param name="TotalTrackedFiles">Authoritative file count outside fast-pathed Zig subtree.</param>
/// <param name="CopiedFiles">Number of missing or modified files copied during this run.</param>
/// <param name="SkippedUnchangedFiles">Number of staged files skipped because metadata already matched.</param>
/// <param name="DeletedOrphanFiles">Number of stale/orphan files removed from <c>_app</c>.</param>
/// <param name="ZigFastPathHit">Whether the 19,500+ file Zig toolchain copy was skipped via stamp match.</param>
public readonly record struct StagePackageStats(
    int TotalTrackedFiles,
    int CopiedFiles,
    int SkippedUnchangedFiles,
    int DeletedOrphanFiles,
    bool ZigFastPathHit);

/// <summary>
/// Incremental parallel mirror stager replacing destructive <c>REMOVE_RECURSE</c> + single-threaded <c>file(COPY)</c>.
/// </summary>
public static class StagePackageCommand
{
    private const string ZigStageStampFile = ".zig-stage-stamp.txt";
    private const string CrtCacheFile = ".crt-imports-cache.json";

    private static readonly Regex WindowsCrtRegex = new(
        @"^(msvcp140|vcruntime140|concrt140|vccorlib140|ucrtbase)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ObjdumpDllNameRegex = new(
        @"^\s*DLL Name:\s*(.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ExcludedSdkDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".godot",
        "build",
        "levels",
        "bin",
        "obj",
    };

    private sealed class CrtCacheEntry
    {
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string[] Imports { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Incrementally mirrors all native closures, Zig toolchain, metadata, bundled mods, CRT/stripped binaries,
    /// modding SDK, and license notices into <c>&lt;WorkDir&gt;/&lt;AppName&gt;</c>, deleting any orphan files
    /// and enforcing all release safety checks.
    /// </summary>
    /// <param name="options">Staging configuration.</param>
    /// <param name="hashIndex">Shared persistent file hash index.</param>
    /// <param name="stats">Receives staging execution statistics.</param>
    /// <returns><c>0</c> on success; non-zero on validation or I/O failure.</returns>
    public static int Execute(StagePackageOptions options, FileHashIndex hashIndex, out StagePackageStats stats)
    {
        stats = default;
        var sw = Stopwatch.StartNew();

        string stagingBin = Path.Combine(options.StagingRoot, "bin");
        if (!Directory.Exists(stagingBin))
        {
            Console.Error.WriteLine(
                $"No staged natives at {stagingBin}.\n" +
                "Build refii for this architecture first: the package needs the host bridge, the " +
                "guest runtime and the player's build inputs beside the executable.");
            return 1;
        }

        string licensePath = Path.Combine(options.RepoRoot, "LICENSE");
        string noticesPath = Path.Combine(options.RepoRoot, "THIRD_PARTY_NOTICES.txt");
        foreach (string requiredNotice in new[] { licensePath, noticesPath })
        {
            if (!File.Exists(requiredNotice))
            {
                Console.Error.WriteLine(
                    $"{Path.GetFileName(requiredNotice)} is missing from {options.RepoRoot}; a release must carry it. " +
                    "Run tools/licenses/gen_third_party_notices.py.");
                return 1;
            }
        }

        string appDir = Path.Combine(options.WorkDir, options.AppName);
        Directory.CreateDirectory(appDir);

        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var expectedPaths = new HashSet<string>(pathComparer);
        var copyPlan = new Dictionary<string, ScannedEntry>(pathComparer);

        // 1. Preserve exported binary & .NET data_* files placed by GodotExportCommand.
        foreach (string exportedRel in ResolvePreservedExportPaths(options, appDir, pathComparer))
        {
            expectedPaths.Add(exportedRel);
        }

        // 2. Native closure from STAGING_ROOT (excluding generated payloads, dev state, stale dirs, and zig/).
        List<ScannedEntry> stagedNatives = FileHashIndex.ScanDirectory(
            options.StagingRoot,
            shouldIncludeDirectory: ShouldIncludeStagingDirectory,
            shouldIncludeFile: static (_, name) => !IsExcludedStagingFile(name),
            ignoreAbsoluteSymlinks: true);

        foreach (ScannedEntry entry in stagedNatives)
        {
            expectedPaths.Add(entry.RelativePath);
            copyPlan[entry.RelativePath] = entry;
        }

        // 3. Bundled Zig toolchain (fast-path stamp check vs 8-core parallel copy).
        bool zigFastPath = false;
        string? pendingZigStamp = null;
        string zigStampStatePath = Path.Combine(options.WorkDir, ZigStageStampFile);
        if (options.BundleZig)
        {
            if (string.IsNullOrWhiteSpace(options.ZigUnpackedDir) || !Directory.Exists(options.ZigUnpackedDir))
            {
                Console.Error.WriteLine($"Bundled Zig unpacked cache not found at '{options.ZigUnpackedDir}'.");
                return 1;
            }

            string sourceStampFile = Path.Combine(options.ZigUnpackedDir, ".refii-zig-stamp");
            string sourceStamp = File.Exists(sourceStampFile) ? File.ReadAllText(sourceStampFile).Trim() : string.Empty;
            string destZigExe = Path.Combine(
                appDir, "bin", "recompile", "zig",
                string.Equals(options.ToolPlatform, "windows", StringComparison.OrdinalIgnoreCase) ? "zig.exe" : "zig");

            bool bypassCache = string.Equals(
                Environment.GetEnvironmentVariable("REFII_NO_BUILD_CACHE"), "1", StringComparison.Ordinal);

            if (!bypassCache &&
                !string.IsNullOrEmpty(sourceStamp) &&
                File.Exists(zigStampStatePath) &&
                string.Equals(File.ReadAllText(zigStampStatePath).Trim(), sourceStamp, StringComparison.Ordinal) &&
                File.Exists(destZigExe))
            {
                zigFastPath = true;
                EnsureExecutableModeIfUnix(destZigExe, options.ToolPlatform);
            }
            else
            {
                List<ScannedEntry> zigEntries = FileHashIndex.ScanDirectory(
                    options.ZigUnpackedDir,
                    shouldIncludeFile: static (_, name) => !name.Equals(".refii-zig-stamp", StringComparison.Ordinal));
                foreach (ScannedEntry entry in zigEntries)
                {
                    string destRel = $"bin/recompile/zig/{entry.RelativePath}";
                    expectedPaths.Add(destRel);
                    copyPlan[destRel] = entry with { RelativePath = destRel };
                }

                pendingZigStamp = sourceStamp;
            }
        }
        else if (File.Exists(zigStampStatePath))
        {
            File.Delete(zigStampStatePath);
        }

        // 4. Per-title metadata (Fable2, Fable3).
        foreach (string title in new[] { "Fable2", "Fable3" })
        {
            string metaDir = Path.Combine(options.RepoRoot, title, "metadata");
            if (Directory.Exists(metaDir))
            {
                foreach (ScannedEntry entry in FileHashIndex.ScanDirectory(metaDir))
                {
                    string destRel = $"assets/{title}/metadata/{entry.RelativePath}";
                    expectedPaths.Add(destRel);
                    copyPlan[destRel] = entry with { RelativePath = destRel };
                }
            }
        }

        // 5. Bundled mods (Fable2/mods-src -> bundled-mods/Fable2 + manifest.txt).
        string? bundledManifestContent = null;
        string modsSrcDir = Path.Combine(options.RepoRoot, "Fable2", "mods-src");
        if (Directory.Exists(modsSrcDir))
        {
            List<ScannedEntry> modEntries = FileHashIndex.ScanDirectory(modsSrcDir);
            var modRelNames = new List<string>(modEntries.Count);
            foreach (ScannedEntry entry in modEntries)
            {
                modRelNames.Add(entry.RelativePath);
                string destRel = $"bundled-mods/Fable2/{entry.RelativePath}";
                expectedPaths.Add(destRel);
                copyPlan[destRel] = entry with { RelativePath = destRel };
            }

            modRelNames.Sort(StringComparer.Ordinal);
            bundledManifestContent = modRelNames.Count == 0
                ? "\n"
                : string.Join("\n", modRelNames) + "\n";
            expectedPaths.Add("bundled-mods/Fable2/manifest.txt");
        }

        // 6. Modding SDK (Godot/ -> sdk/ + sdk/pins.json).
        List<ScannedEntry> sdkEntries = FileHashIndex.ScanDirectory(
            options.ProjectDir,
            shouldIncludeDirectory: static (_, name) => !ExcludedSdkDirs.Contains(name),
            shouldIncludeFile: static (_, name) =>
                !name.EndsWith(".x86_64", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".pck", StringComparison.OrdinalIgnoreCase));
        foreach (ScannedEntry entry in sdkEntries)
        {
            string destRel = $"sdk/{entry.RelativePath}";
            expectedPaths.Add(destRel);
            copyPlan[destRel] = entry with { RelativePath = destRel };
        }

        expectedPaths.Add("sdk/pins.json");

        // 7. Root license files.
        AddSingleFileToPlan(licensePath, "LICENSE", expectedPaths, copyPlan);
        AddSingleFileToPlan(noticesPath, "THIRD_PARTY_NOTICES.txt", expectedPaths, copyPlan);

        // Ensure all destination parent directories exist before parallel file copies.
        var uniqueParentDirs = new HashSet<string>(pathComparer);
        foreach (string relPath in copyPlan.Keys)
        {
            string fullDest = Path.Combine(appDir, relPath.Replace('/', Path.DirectorySeparatorChar));
            string? parent = Path.GetDirectoryName(fullDest);
            if (!string.IsNullOrEmpty(parent))
            {
                uniqueParentDirs.Add(parent);
            }
        }

        foreach (string dir in uniqueParentDirs)
        {
            Directory.CreateDirectory(dir);
        }

        // 8. Execute parallel delta file & relative-symlink sync.
        int copiedCount = 0;
        int skippedCount = 0;
        bool isLinuxTarget = string.Equals(options.ToolPlatform, "linux", StringComparison.OrdinalIgnoreCase);

        Parallel.ForEach(copyPlan.Values, entry =>
        {
            // On Linux targets, bin/*.so and bin/<Title>/*.so are stripped below; handle them in the strip pass
            // so we don't copy 444 MB unstripped librexruntime.so onto a stripped file on every build!
            if (isLinuxTarget && entry.SymlinkTarget is null && IsLinuxStrippableRelPath(entry.RelativePath))
            {
                return;
            }

            string fullDest = Path.Combine(appDir, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (SyncSingleEntry(entry, fullDest))
            {
                Interlocked.Increment(ref copiedCount);
            }
            else
            {
                Interlocked.Increment(ref skippedCount);
            }
        });

        if (options.BundleZig)
        {
            string destZigExe = Path.Combine(
                appDir, "bin", "recompile", "zig",
                string.Equals(options.ToolPlatform, "windows", StringComparison.OrdinalIgnoreCase) ? "zig.exe" : "zig");
            if (!File.Exists(destZigExe))
            {
                Console.Error.WriteLine($"Zig staged but {destZigExe} is missing.");
                return 1;
            }

            EnsureExecutableModeIfUnix(destZigExe, options.ToolPlatform);
            if (pendingZigStamp is not null)
            {
                File.WriteAllText(zigStampStatePath, $"{pendingZigStamp}\n");
            }
        }

        // Write generated text manifests only when bytes actually changed.
        if (bundledManifestContent is not null)
        {
            if (WriteUtf8IfChanged(Path.Combine(appDir, "bundled-mods", "Fable2", "manifest.txt"), bundledManifestContent))
            {
                copiedCount++;
            }
            else
            {
                skippedCount++;
            }
        }

        string floor = string.IsNullOrEmpty(options.GodotFloor) ? "4.7" : options.GodotFloor;
        string flavour = string.IsNullOrEmpty(options.GodotFlavour) ? "mono" : options.GodotFlavour;
        string dotnetMajor = string.IsNullOrEmpty(options.DotnetMajor) ? "8" : options.DotnetMajor;
        string pinsContent =
            "{\n" +
            $"  \"godotFloor\": \"{floor}\",\n" +
            $"  \"flavour\": \"{flavour}\",\n" +
            $"  \"dotnetMajor\": \"{dotnetMajor}\",\n" +
            "  \"checksumAsset\": \"SHA512-SUMS.txt\"\n" +
            "}\n";
        if (WriteUtf8IfChanged(Path.Combine(appDir, "sdk", "pins.json"), pinsContent))
        {
            copiedCount++;
        }
        else
        {
            skippedCount++;
        }

        // 9. Platform post-processing: Windows CRT DLL staging or Linux cached debug stripping.
        if (string.Equals(options.ToolPlatform, "windows", StringComparison.OrdinalIgnoreCase))
        {
            int crtResult = StageWindowsRuntimeDlls(
                options, appDir, expectedPaths, ref copiedCount, ref skippedCount);
            if (crtResult != 0)
            {
                return crtResult;
            }
        }
        else if (isLinuxTarget)
        {
            StageAndStripLinuxLibraries(
                options, appDir, copyPlan, hashIndex, ref copiedCount, ref skippedCount);
        }

        // 10. Sweep any orphan files, absolute symlinks, or stale directories from _app.
        int deletedOrphans = PruneOrphans(appDir, expectedPaths, protectZigSubtree: zigFastPath);

        // 11. Enforce hard release safety invariants.
        if (!ValidateStagedApp(appDir, out string validationError))
        {
            Console.Error.WriteLine(validationError);
            return 1;
        }

        stats = new StagePackageStats(
            TotalTrackedFiles: expectedPaths.Count,
            CopiedFiles: copiedCount,
            SkippedUnchangedFiles: skippedCount,
            DeletedOrphanFiles: deletedOrphans,
            ZigFastPathHit: zigFastPath);

        sw.Stop();
        Console.WriteLine(
            $"[stage-cache] Synced {appDir} in {sw.Elapsed.TotalSeconds:F2}s " +
            $"(copied: {copiedCount}, unchanged: {skippedCount}, pruned: {deletedOrphans}, " +
            $"zig-fast-path: {(zigFastPath ? "HIT" : (options.BundleZig ? "COPIED" : "OFF"))}).");
        return 0;
    }

    private static HashSet<string> ResolvePreservedExportPaths(
        StagePackageOptions options,
        string appDir,
        StringComparer comparer)
    {
        var result = new HashSet<string>(comparer);
        if (options.PreservedExportFiles is not null && options.PreservedExportFiles.Count > 0)
        {
            foreach (string p in options.PreservedExportFiles)
            {
                result.Add(p);
            }

            return result;
        }

        string manifestPath = Path.Combine(options.WorkDir, GodotExportCommand.ExportManifestFileName);
        if (File.Exists(manifestPath))
        {
            foreach (string line in File.ReadAllLines(manifestPath))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    result.Add(trimmed);
                }
            }

            if (result.Count > 0)
            {
                return result;
            }
        }

        if (!string.IsNullOrEmpty(options.OutBinary) && File.Exists(Path.Combine(appDir, options.OutBinary)))
        {
            result.Add(options.OutBinary);
            string consoleExe = Path.GetFileNameWithoutExtension(options.OutBinary) + ".console.exe";
            if (File.Exists(Path.Combine(appDir, consoleExe)))
            {
                result.Add(consoleExe);
            }
        }

        if (Directory.Exists(appDir))
        {
            foreach (string dataDir in Directory.EnumerateDirectories(appDir, "data_*", SearchOption.TopDirectoryOnly))
            {
                string dirName = Path.GetFileName(dataDir);
                foreach (ScannedEntry e in FileHashIndex.ScanDirectory(dataDir))
                {
                    result.Add($"{dirName}/{e.RelativePath}");
                }
            }
        }

        return result;
    }

    private static bool ShouldIncludeStagingDirectory(string relDir, string name)
    {
        if (relDir.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
            relDir.Equals("assets", StringComparison.OrdinalIgnoreCase) ||
            relDir.Equals("cache", StringComparison.OrdinalIgnoreCase) ||
            relDir.Equals("bin/recompile/lib", StringComparison.OrdinalIgnoreCase) ||
            relDir.Equals("bin/recompile/bin", StringComparison.OrdinalIgnoreCase) ||
            relDir.Equals("bin/recompile/zig", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !name.Equals("logs", StringComparison.OrdinalIgnoreCase) &&
               !name.Equals("assets", StringComparison.OrdinalIgnoreCase) &&
               !name.Equals("cache", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcludedStagingFile(string name)
    {
        return name.Contains("refii_generated.", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("refiii_generated.", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("dev-skip-rebuild", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("build-info.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("refii.toml", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("refiii.toml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLinuxStrippableRelPath(string relPath)
    {
        if (!relPath.EndsWith(".so", StringComparison.Ordinal))
        {
            return false;
        }

        string? dir = Path.GetDirectoryName(relPath)?.Replace('\\', '/');
        return string.Equals(dir, "bin", StringComparison.Ordinal) ||
               string.Equals(dir, "bin/Fable2", StringComparison.Ordinal) ||
               string.Equals(dir, "bin/Fable3", StringComparison.Ordinal);
    }

    private static void AddSingleFileToPlan(
        string fullSourcePath,
        string destRelPath,
        HashSet<string> expectedPaths,
        Dictionary<string, ScannedEntry> copyPlan)
    {
        var info = new FileInfo(fullSourcePath);
        expectedPaths.Add(destRelPath);
        copyPlan[destRelPath] = new ScannedEntry(
            destRelPath,
            info.FullName,
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            FileHashIndex.GetPortableUnixMode(info),
            null);
    }

    private static bool SyncSingleEntry(ScannedEntry source, string fullDest)
    {
        if (source.SymlinkTarget is not null)
        {
            var existingLink = new FileInfo(fullDest);
            if (existingLink.LinkTarget is not null &&
                string.Equals(existingLink.LinkTarget, source.SymlinkTarget, StringComparison.Ordinal))
            {
                return false;
            }

            DeleteAnyAtPath(fullDest);
            File.CreateSymbolicLink(fullDest, source.SymlinkTarget);
            return true;
        }

        var destInfo = new FileInfo(fullDest);
        if (destInfo.Exists && destInfo.LinkTarget is null &&
            destInfo.Length == source.Length &&
            destInfo.LastWriteTimeUtc.Ticks == source.LastWriteUtcTicks)
        {
            return false;
        }

        if (destInfo.LinkTarget is not null || Directory.Exists(fullDest))
        {
            DeleteAnyAtPath(fullDest);
        }

        File.Copy(source.FullPath, fullDest, overwrite: true);
        File.SetLastWriteTimeUtc(fullDest, new DateTime(source.LastWriteUtcTicks, DateTimeKind.Utc));

        if (!OperatingSystem.IsWindows() && source.UnixMode != 0u)
        {
            ApplyUnixFileMode(fullDest, source.UnixMode);
        }

        return true;
    }

    private static bool WriteUtf8IfChanged(string path, string content)
    {
        byte[] desired = Encoding.UTF8.GetBytes(content);
        if (File.Exists(path))
        {
            byte[] existing = File.ReadAllBytes(path);
            if (existing.AsSpan().SequenceEqual(desired))
            {
                return false;
            }
        }

        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllBytes(path, desired);
        return true;
    }

    private static int StageWindowsRuntimeDlls(
        StagePackageOptions options,
        string appDir,
        HashSet<string> expectedPaths,
        ref int copiedCount,
        ref int skippedCount)
    {
        string binDir = Path.Combine(appDir, "bin");
        var rootBinaries = new List<string>();
        if (Directory.Exists(binDir))
        {
            rootBinaries.AddRange(Directory.GetFiles(binDir, "*.dll", SearchOption.TopDirectoryOnly));
            rootBinaries.AddRange(Directory.GetFiles(binDir, "*.exe", SearchOption.TopDirectoryOnly));
            foreach (string titleDir in new[] { "Fable2", "Fable3" })
            {
                string sub = Path.Combine(binDir, titleDir);
                if (Directory.Exists(sub))
                {
                    rootBinaries.AddRange(Directory.GetFiles(sub, "*.dll", SearchOption.TopDirectoryOnly));
                }
            }
        }

        if (rootBinaries.Count == 0)
        {
            Console.Error.WriteLine($"refii_stage_windows_runtime: no binaries under {binDir}; the package would not run.");
            return 1;
        }

        if (options.CrtDirs.Count == 0)
        {
            Console.WriteLine(
                "[stage-cache] Warning: No MSVC runtime redistributable directories configured; skipping CRT staging.");
            return 0;
        }

        string cachePath = Path.Combine(options.WorkDir, CrtCacheFile);
        Dictionary<string, CrtCacheEntry> importCache = LoadCrtCache(cachePath);
        bool cacheDirty = false;

        var wantedCrtNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var inspectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        // Ignore previously staged CRT DLLs in bin/ as roots so we only stage what our own binaries need.
        foreach (string binary in rootBinaries)
        {
            string fileName = Path.GetFileName(binary);
            if (!WindowsCrtRegex.IsMatch(fileName))
            {
                queue.Enqueue(binary);
            }
        }

        var missing = new List<string>();
        var resolvedCrtPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            var batchToInspect = new List<string>();
            var currentWave = new List<string>();
            while (queue.Count > 0)
            {
                string candidate = Path.GetFullPath(queue.Dequeue());
                if (!inspectedFiles.Add(candidate) || !File.Exists(candidate))
                {
                    continue;
                }

                currentWave.Add(candidate);
                var fi = new FileInfo(candidate);
                if (!importCache.TryGetValue(candidate, out CrtCacheEntry? cached) ||
                    cached.Length != fi.Length ||
                    cached.LastWriteUtcTicks != fi.LastWriteTimeUtc.Ticks)
                {
                    batchToInspect.Add(candidate);
                }
            }

            if (batchToInspect.Count > 0)
            {
                ReadPeImportsIntoCache(batchToInspect, options.ObjdumpPath, importCache);
                cacheDirty = true;
            }

            foreach (string file in currentWave)
            {
                if (!importCache.TryGetValue(file, out CrtCacheEntry? entry))
                {
                    continue;
                }

                foreach (string importedDll in entry.Imports)
                {
                    string lower = importedDll.ToLowerInvariant();
                    if (!WindowsCrtRegex.IsMatch(lower) ||
                        string.Equals(lower, "ucrtbase.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (wantedCrtNames.Add(lower))
                    {
                        string? foundCrt = FindInDirectories(lower, options.CrtDirs);
                        if (foundCrt is null)
                        {
                            missing.Add(lower);
                        }
                        else
                        {
                            resolvedCrtPaths[lower] = foundCrt;
                            queue.Enqueue(foundCrt);
                        }
                    }
                }
            }
        }

        if (cacheDirty)
        {
            SaveCrtCache(cachePath, importCache);
        }

        if (missing.Count > 0)
        {
            missing.Sort(StringComparer.OrdinalIgnoreCase);
            Console.Error.WriteLine(
                "The package needs these MSVC runtime DLLs and none could be found:\n    " +
                string.Join("\n    ", missing) +
                "\n\nSearched:\n    " +
                string.Join("\n    ", options.CrtDirs) +
                "\n\nA name ending in d is the DEBUG CRT, which Microsoft does not license for " +
                "redistribution and which exists only inside a Visual Studio install. Build " +
                "--config RelWithDebInfo for a package that runs on a machine without one.");
            return 1;
        }

        bool hasDebugCrt = false;
        foreach (KeyValuePair<string, string> kvp in resolvedCrtPaths)
        {
            var srcInfo = new FileInfo(kvp.Value);
            // Preserve canonical casing from the redistributable directory on disk.
            string actualFileName = srcInfo.Name;
            string relDest = $"bin/{actualFileName}";
            expectedPaths.Add(relDest);

            var entry = new ScannedEntry(
                relDest, srcInfo.FullName, srcInfo.Length, srcInfo.LastWriteTimeUtc.Ticks, 0u, null);
            if (SyncSingleEntry(entry, Path.Combine(binDir, actualFileName)))
            {
                copiedCount++;
            }
            else
            {
                skippedCount++;
            }

            if (actualFileName.EndsWith("d.dll", StringComparison.OrdinalIgnoreCase))
            {
                hasDebugCrt = true;
            }
        }

        if (hasDebugCrt)
        {
            Console.WriteLine(
                "[stage-cache] Warning: This package carries the DEBUG MSVC runtime and MUST NOT be distributed. " +
                "Build --config RelWithDebInfo for a redistributable package.");
        }

        return 0;
    }

    private static void ReadPeImportsIntoCache(
        IReadOnlyList<string> binaries,
        string objdumpPath,
        Dictionary<string, CrtCacheEntry> cache)
    {
        foreach (string binary in binaries)
        {
            var fi = new FileInfo(binary);
            if (TryReadPeImportsManaged(binary, out List<string> managedImports))
            {
                cache[binary] = new CrtCacheEntry
                {
                    Length = fi.Length,
                    LastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
                    Imports = managedImports.ToArray(),
                };
            }
            else if (!string.IsNullOrWhiteSpace(objdumpPath) && File.Exists(objdumpPath))
            {
                cache[binary] = new CrtCacheEntry
                {
                    Length = fi.Length,
                    LastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
                    Imports = ReadPeImportsWithObjdump(binary, objdumpPath),
                };
            }
        }
    }

    /// <summary>
    /// Fast in-process PE32 / PE32+ Import Directory Table reader (~0.05 ms/binary, zero subprocesses).
    /// </summary>
    internal static bool TryReadPeImportsManaged(string pePath, out List<string> importedDlls)
    {
        importedDlls = new List<string>();
        try
        {
            using var fs = new FileStream(pePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16384);
            using var reader = new BinaryReader(fs, Encoding.ASCII, leaveOpen: false);
            if (fs.Length < 128 || reader.ReadUInt16() != 0x5A4D) // "MZ"
            {
                return false;
            }

            fs.Position = 0x3C;
            uint peOffset = reader.ReadUInt32();
            if (peOffset + 24 > fs.Length)
            {
                return false;
            }

            fs.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550u) // "PE\0\0"
            {
                return false;
            }

            fs.Position += 2; // Machine
            ushort numSections = reader.ReadUInt16();
            fs.Position += 12; // TimeDateStamp + SymbolTable + NumSymbols
            ushort sizeOfOptionalHeader = reader.ReadUInt16();
            fs.Position += 2; // Characteristics

            long optionalHeaderStart = fs.Position;
            ushort magic = reader.ReadUInt16();
            int dataDirOffsetInOpt;
            if (magic == 0x10B) // PE32
            {
                dataDirOffsetInOpt = 96;
            }
            else if (magic == 0x20B) // PE32+
            {
                dataDirOffsetInOpt = 112;
            }
            else
            {
                return false;
            }

            if (sizeOfOptionalHeader < dataDirOffsetInOpt + 16)
            {
                return true; // Valid PE with no Import Data Directory.
            }

            fs.Position = optionalHeaderStart + dataDirOffsetInOpt - 4;
            uint numRvaAndSizes = reader.ReadUInt32();
            if (numRvaAndSizes < 2)
            {
                return true;
            }

            fs.Position += 8; // Skip Export Table (DataDirectory[0])
            uint importTableRva = reader.ReadUInt32();
            uint importTableSize = reader.ReadUInt32();
            if (importTableRva == 0 || importTableSize == 0)
            {
                return true;
            }

            long sectionTableStart = optionalHeaderStart + sizeOfOptionalHeader;
            var sections = new (uint VirtualAddress, uint VirtualSize, uint RawDataPtr, uint RawDataSize)[numSections];
            fs.Position = sectionTableStart;
            for (int i = 0; i < numSections; i++)
            {
                fs.Position += 8; // Name
                uint virtualSize = reader.ReadUInt32();
                uint virtualAddr = reader.ReadUInt32();
                uint rawSize = reader.ReadUInt32();
                uint rawPtr = reader.ReadUInt32();
                fs.Position += 16; // Relocs, Linenums, Counts, Characteristics
                sections[i] = (virtualAddr, Math.Max(virtualSize, rawSize), rawPtr, rawSize);
            }

            static long RvaToFileOffset(
                uint rva,
                ReadOnlySpan<(uint VirtualAddress, uint VirtualSize, uint RawDataPtr, uint RawDataSize)> secs)
            {
                foreach (var s in secs)
                {
                    if (rva >= s.VirtualAddress && rva < s.VirtualAddress + s.VirtualSize)
                    {
                        uint delta = rva - s.VirtualAddress;
                        return delta < s.RawDataSize ? s.RawDataPtr + delta : -1;
                    }
                }

                return -1;
            }

            long descOffset = RvaToFileOffset(importTableRva, sections);
            if (descOffset < 0)
            {
                return false;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int idx = 0; idx < 4096; idx++)
            {
                long pos = descOffset + (idx * 20L);
                if (pos + 20 > fs.Length)
                {
                    break;
                }

                fs.Position = pos;
                uint originalFirstThunk = reader.ReadUInt32();
                reader.ReadUInt32(); // TimeDateStamp
                reader.ReadUInt32(); // ForwarderChain
                uint nameRva = reader.ReadUInt32();
                uint firstThunk = reader.ReadUInt32();

                if (originalFirstThunk == 0 && nameRva == 0 && firstThunk == 0)
                {
                    break;
                }

                long nameOffset = RvaToFileOffset(nameRva, sections);
                if (nameOffset <= 0 || nameOffset >= fs.Length)
                {
                    continue;
                }

                fs.Position = nameOffset;
                var sb = new StringBuilder(32);
                int b;
                while (sb.Length < 260 && (b = fs.ReadByte()) > 0)
                {
                    sb.Append((char)b);
                }

                string dllName = sb.ToString();
                if (dllName.Length > 0 && seen.Add(dllName))
                {
                    importedDlls.Add(dllName);
                }
            }

            return true;
        }
        catch
        {
            importedDlls.Clear();
            return false;
        }
    }

    private static string[] ReadPeImportsWithObjdump(string binary, string objdumpPath)
    {
        var psi = new ProcessStartInfo(objdumpPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--private-headers");
        psi.ArgumentList.Add(binary);

        using Process? proc = Process.Start(psi);
        if (proc is null)
        {
            return Array.Empty<string>();
        }

        string stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        var names = new List<string>();
        using var sr = new StringReader(stdout);
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            Match m = ObjdumpDllNameRegex.Match(line);
            if (m.Success)
            {
                names.Add(m.Groups[1].Value);
            }
        }

        return names.ToArray();
    }

    private static void StageAndStripLinuxLibraries(
        StagePackageOptions options,
        string appDir,
        Dictionary<string, ScannedEntry> copyPlan,
        FileHashIndex hashIndex,
        ref int copiedCount,
        ref int skippedCount)
    {
        bool canStrip = !string.IsNullOrWhiteSpace(options.StripPath) && File.Exists(options.StripPath);
        string stripCacheDir = Path.Combine(options.CacheDir, "strip-cache");
        if (canStrip)
        {
            Directory.CreateDirectory(stripCacheDir);
        }

        foreach (ScannedEntry entry in copyPlan.Values)
        {
            if (entry.SymlinkTarget is not null || !IsLinuxStrippableRelPath(entry.RelativePath))
            {
                continue;
            }

            string fullDest = Path.Combine(appDir, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!canStrip)
            {
                if (SyncSingleEntry(entry, fullDest))
                {
                    copiedCount++;
                }
                else
                {
                    skippedCount++;
                }

                continue;
            }

            string sourceHex = Convert.ToHexString(
                hashIndex.GetOrComputeFileSha256(entry.FullPath, entry.Length, entry.LastWriteUtcTicks)).ToLowerInvariant();
            string cachedStripped = Path.Combine(stripCacheDir, $"{sourceHex}.so");

            if (!File.Exists(cachedStripped))
            {
                string tmp = $"{cachedStripped}.{Environment.ProcessId}.tmp";
                File.Copy(entry.FullPath, tmp, overwrite: true);
                var psi = new ProcessStartInfo(options.StripPath)
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("--strip-debug");
                psi.ArgumentList.Add(tmp);
                using Process? p = Process.Start(psi);
                p?.WaitForExit();
                if (p is not null && p.ExitCode == 0)
                {
                    File.Move(tmp, cachedStripped, overwrite: true);
                }
                else
                {
                    if (File.Exists(tmp))
                    {
                        File.Delete(tmp);
                    }

                    if (SyncSingleEntry(entry, fullDest))
                    {
                        copiedCount++;
                    }
                    else
                    {
                        skippedCount++;
                    }

                    continue;
                }
            }

            var cachedInfo = new FileInfo(cachedStripped);
            var strippedEntry = new ScannedEntry(
                entry.RelativePath,
                cachedInfo.FullName,
                cachedInfo.Length,
                cachedInfo.LastWriteTimeUtc.Ticks,
                entry.UnixMode,
                null);
            if (SyncSingleEntry(strippedEntry, fullDest))
            {
                copiedCount++;
            }
            else
            {
                skippedCount++;
            }
        }
    }

    private static int PruneOrphans(string appDir, HashSet<string> expectedPaths, bool protectZigSubtree)
    {
        int deleted = 0;
        string fullRoot = Path.GetFullPath(appDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dirsPostOrder = new List<string>();
        var stack = new Stack<(string FullDir, string RelPrefix)>();
        stack.Push((fullRoot, string.Empty));

        while (stack.Count > 0)
        {
            (string currentDir, string relPrefix) = stack.Pop();
            if (currentDir != fullRoot)
            {
                dirsPostOrder.Add(currentDir);
            }

            FileSystemInfo[] items;
            try
            {
                items = new DirectoryInfo(currentDir).GetFileSystemInfos();
            }
            catch
            {
                continue;
            }

            foreach (FileSystemInfo item in items)
            {
                string relPath = relPrefix.Length == 0 ? item.Name : $"{relPrefix}/{item.Name}";
                if (protectZigSubtree &&
                    relPath.Equals("bin/recompile/zig", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (item.LinkTarget is not null)
                {
                    bool isAbsolute = Path.IsPathRooted(item.LinkTarget);
                    if (isAbsolute || !expectedPaths.Contains(relPath))
                    {
                        DeleteAnyAtPath(item.FullName);
                        deleted++;
                    }

                    continue;
                }

                if ((item.Attributes & FileAttributes.Directory) != 0)
                {
                    stack.Push((item.FullName, relPath));
                }
                else if (!expectedPaths.Contains(relPath))
                {
                    File.Delete(item.FullName);
                    deleted++;
                }
            }
        }

        for (int i = dirsPostOrder.Count - 1; i >= 0; i--)
        {
            string dir = dirsPostOrder[i];
            try
            {
                if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                {
                    Directory.Delete(dir, recursive: false);
                }
            }
            catch
            {
                // Ignore non-empty or transient lock.
            }
        }

        return deleted;
    }

    private static bool ValidateStagedApp(string appDir, out string error)
    {
        error = string.Empty;
        string binDir = Path.Combine(appDir, "bin");
        if (!Directory.Exists(binDir))
        {
            error = $"Staging produced no bin/ in {appDir}; the package would not run.";
            return false;
        }

        string projectGodot = Path.Combine(appDir, "sdk", "project.godot");
        if (!File.Exists(projectGodot))
        {
            error = $"The modding SDK staged without a project.godot at {projectGodot}.";
            return false;
        }

        string devSkipMarker = Path.Combine(binDir, "recompile", "dev-skip-rebuild");
        if (File.Exists(devSkipMarker))
        {
            error = $"The dev-skip-rebuild marker reached the package ({devSkipMarker}).";
            return false;
        }

        foreach (string file in Directory.EnumerateFiles(binDir, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            if (name.Contains("refii_generated.", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("refiii_generated.", StringComparison.OrdinalIgnoreCase))
            {
                error = $"The recompiled game module reached the package ({file}). It is derived from a game image and must never ship.";
                return false;
            }
        }

        return true;
    }

    private static string? FindInDirectories(string fileName, IReadOnlyList<string> dirs)
    {
        foreach (string dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            // Case-insensitive match even on Linux cross-packaging hosts.
            string direct = Path.Combine(dir, fileName);
            if (File.Exists(direct))
            {
                return direct;
            }

            foreach (string candidate in Directory.EnumerateFiles(dir))
            {
                if (string.Equals(Path.GetFileName(candidate), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static Dictionary<string, CrtCacheEntry> LoadCrtCache(string path)
    {
        var result = new Dictionary<string, CrtCacheEntry>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            Dictionary<string, CrtCacheEntry>? parsed =
                JsonSerializer.Deserialize<Dictionary<string, CrtCacheEntry>>(File.ReadAllText(path));
            if (parsed is not null)
            {
                foreach (KeyValuePair<string, CrtCacheEntry> kvp in parsed)
                {
                    result[kvp.Key] = kvp.Value;
                }
            }
        }
        catch
        {
            result.Clear();
        }

        return result;
    }

    private static void SaveCrtCache(string path, Dictionary<string, CrtCacheEntry> cache)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(cache));
        }
        catch
        {
            // Non-fatal cache write failure.
        }
    }

    private static void EnsureExecutableModeIfUnix(string path, string toolPlatform)
    {
        if (!OperatingSystem.IsWindows() &&
            string.Equals(toolPlatform, "linux", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(path))
        {
            ApplyUnixFileMode(path, 0x81EDu);
        }
    }

    private static void ApplyUnixFileMode(string path, uint modeWord)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            bool exec = (modeWord & 0x49u) != 0u;
            File.SetUnixFileMode(
                path,
                exec
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                      UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                      UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite |
                      UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        catch
        {
            // Ignore filesystem permission unsupported cases (e.g. NTFS mount on WSL).
        }
    }

    private static void DeleteAnyAtPath(string path)
    {
        var fi = new FileInfo(path);
        if (fi.LinkTarget is not null)
        {
            if ((fi.Attributes & FileAttributes.Directory) != 0)
            {
                Directory.Delete(path, recursive: false);
            }
            else
            {
                File.Delete(path);
            }

            return;
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
