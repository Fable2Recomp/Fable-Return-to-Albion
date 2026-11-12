namespace Refii.BuildCache;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>
/// Command-line dispatcher for <c>Refii.BuildCache</c>.
/// </summary>
public static class Program
{
    /// <summary>
    /// Entrypoint supporting <c>test-gate</c>, <c>godot-export</c>, <c>stage-package</c>, <c>pack-zip</c>,
    /// and single-process <c>package-release</c>.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Zero on success; non-zero exit code on failure.</returns>
    public static int Main(string[] args)
    {
        if (GodotTestShim.IsGodotShimInvocation(args, out string realGodotBin))
        {
            return GodotTestShim.Execute(realGodotBin, args);
        }

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        string subcommand = args[0];
        Dictionary<string, string> flags = ParseFlags(args, startIndex: 1);

        try
        {
            string repoRoot = RequireResolvedPath(flags, "repo-root", Directory.GetCurrentDirectory());
            string cacheDir = flags.TryGetValue("cache-dir", out string? explicitCache) && !string.IsNullOrWhiteSpace(explicitCache)
                ? Path.GetFullPath(explicitCache)
                : Path.Combine(repoRoot, "out", "cache");

            string hostKey = $"{(OperatingSystem.IsWindows() ? "win" : "unix")}-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
            string hashIndexPath = Path.Combine(cacheDir, $"file-hashes-{hostKey}.bin");
            FileHashIndex hashIndex = FileHashIndex.Load(hashIndexPath);

            try
            {
                return subcommand switch
                {
                    "test-gate" => RunTestGate(repoRoot, cacheDir, flags, hashIndex),
                    "godot-export" => RunGodotExport(repoRoot, cacheDir, flags, hashIndex),
                    "stage-package" => RunStagePackage(repoRoot, cacheDir, flags, hashIndex),
                    "pack-zip" => RunPackZip(flags, hashIndex),
                    "package-release" => RunFullPackageRelease(repoRoot, cacheDir, flags, hashIndex),
                    _ => UnknownCommand(subcommand),
                };
            }
            finally
            {
                hashIndex.SaveIfDirty(hashIndexPath);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[build-cache] Fatal error: {ex.Message}");
            return 1;
        }
    }

    private static int RunTestGate(
        string repoRoot,
        string cacheDir,
        Dictionary<string, string> flags,
        FileHashIndex hashIndex)
    {
        string projectDir = RequireResolvedPath(flags, "project-dir", Path.Combine(repoRoot, "Godot"));
        string testCsproj = RequireResolvedPath(
            flags, "test-csproj", Path.Combine(projectDir, "tests", "Refii.Tests", "Refii.Tests.csproj"));
        string config = GetOrDefault(flags, "config", "Debug");
        string godotBin = GetOrDefault(flags, "godot-bin", Environment.GetEnvironmentVariable("GODOT_BIN") ?? string.Empty);
        bool disableServers = HasBoolFlag(flags, "disable-build-servers");
        bool force = HasBoolFlag(flags, "force");

        var options = new TestGateOptions(
            RepoRoot: repoRoot,
            ProjectDir: projectDir,
            TestCsproj: testCsproj,
            Configuration: config,
            GodotBin: godotBin,
            CacheDir: cacheDir,
            DisableBuildServers: disableServers,
            ForceRun: force);
        return TestGateCommand.Execute(options, hashIndex);
    }

    private static int RunGodotExport(
        string repoRoot,
        string cacheDir,
        Dictionary<string, string> flags,
        FileHashIndex hashIndex)
    {
        string projectDir = RequireResolvedPath(flags, "project-dir", Path.Combine(repoRoot, "Godot"));
        string workDir = RequirePath(flags, "work-dir");
        string appName = RequireString(flags, "app-name");
        string appDir = Path.Combine(workDir, appName);

        var options = new GodotExportOptions(
            ProjectDir: projectDir,
            GodotBin: RequirePath(flags, "godot-bin"),
            GodotDir: GetOrDefault(flags, "godot-dir", Path.Combine(repoRoot, "tools", "godot")),
            Preset: RequireString(flags, "preset"),
            OutBinary: RequireString(flags, "out-binary"),
            AppDir: appDir,
            WorkDir: workDir,
            ToolPlatform: RequireString(flags, "tool-platform"),
            ToolArch: RequireString(flags, "tool-arch"),
            CacheDir: cacheDir,
            ForceExport: HasBoolFlag(flags, "force"));

        return GodotExportCommand.Execute(options, hashIndex, out _);
    }

    private static int RunStagePackage(
        string repoRoot,
        string cacheDir,
        Dictionary<string, string> flags,
        FileHashIndex hashIndex,
        IReadOnlySet<string>? preservedExportFiles = null)
    {
        string projectDir = RequireResolvedPath(flags, "project-dir", Path.Combine(repoRoot, "Godot"));
        string stagingRoot = RequirePath(flags, "staging-root");
        string workDir = RequirePath(flags, "work-dir");
        string appName = RequireString(flags, "app-name");
        string toolPlatform = RequireString(flags, "tool-platform");
        string toolArch = RequireString(flags, "tool-arch");
        string crtRaw = GetOrDefault(flags, "crt-dirs", string.Empty);

        LegacyPackageCleanup.CleanLegacyArtifacts(
            workDir: workDir,
            appName: appName,
            toolPlatform: toolPlatform,
            toolArch: toolArch,
            projectDir: projectDir);

        var options = new StagePackageOptions(
            RepoRoot: repoRoot,
            ProjectDir: projectDir,
            StagingRoot: stagingRoot,
            WorkDir: workDir,
            AppName: appName,
            OutBinary: GetOrDefault(flags, "out-binary", string.Empty),
            ToolPlatform: toolPlatform,
            ToolArch: toolArch,
            BundleZig: HasBoolFlag(flags, "bundle-zig"),
            ZigUnpackedDir: GetOrDefault(flags, "zig-unpacked-dir", string.Empty),
            CrtDirs: SplitSemicolonPaths(crtRaw),
            ObjdumpPath: GetOrDefault(flags, "objdump", string.Empty),
            StripPath: GetOrDefault(flags, "strip", string.Empty),
            GodotFloor: GetOrDefault(flags, "godot-floor", "4.7"),
            GodotFlavour: GetOrDefault(flags, "godot-flavour", "mono"),
            DotnetMajor: GetOrDefault(flags, "dotnet-major", "8"),
            CacheDir: cacheDir,
            PreservedExportFiles: preservedExportFiles);

        return StagePackageCommand.Execute(options, hashIndex, out _);
    }

    private static int RunPackZip(Dictionary<string, string> flags, FileHashIndex hashIndex)
    {
        string workDir = RequirePath(flags, "work-dir");
        string appName = RequireString(flags, "app-name");
        string zipPath = RequirePath(flags, "zip-path");
        bool force = HasBoolFlag(flags, "force");

        LegacyPackageCleanup.CleanLegacyArtifacts(
            workDir: workDir,
            appName: appName,
            activeZipPath: zipPath,
            toolPlatform: GetOrDefault(flags, "tool-platform", string.Empty),
            toolArch: GetOrDefault(flags, "tool-arch", string.Empty));

        IncrementalZipPacker.PackDirectory(
            appDir: Path.Combine(workDir, appName),
            archiveRootPrefix: appName,
            zipPath: zipPath,
            hashIndex: hashIndex,
            forceFullRebuild: force);
        return 0;
    }

    private static int RunFullPackageRelease(
        string repoRoot,
        string cacheDir,
        Dictionary<string, string> flags,
        FileHashIndex hashIndex)
    {
        var totalSw = Stopwatch.StartNew();
        string projectDir = RequireResolvedPath(flags, "project-dir", Path.Combine(repoRoot, "Godot"));
        string godotBin = RequirePath(flags, "godot-bin");
        string workDir = RequirePath(flags, "work-dir");
        string appName = RequireString(flags, "app-name");
        string outBinary = RequireString(flags, "out-binary");
        string zipPath = RequirePath(flags, "zip-path");
        string toolPlatform = RequireString(flags, "tool-platform");
        string toolArch = RequireString(flags, "tool-arch");
        bool force = HasBoolFlag(flags, "force");

        // Prune any legacy Fable 2 / refii-* folders, zips, sidecars, and stray exports upfront.
        LegacyPackageCleanup.CleanLegacyArtifacts(
            workDir: workDir,
            appName: appName,
            activeZipPath: zipPath,
            toolPlatform: toolPlatform,
            toolArch: toolArch,
            projectDir: projectDir);

        // Phase 1: Cached Unit & Feature Test Gate.
        string testCsproj = Path.Combine(projectDir, "tests", "Refii.Tests", "Refii.Tests.csproj");
        string? envGodotBin = Environment.GetEnvironmentVariable("GODOT_BIN");
        string effectiveGodotBin = string.IsNullOrWhiteSpace(envGodotBin) ? godotBin : envGodotBin;

        int testExit = TestGateCommand.Execute(
            new TestGateOptions(
                RepoRoot: repoRoot,
                ProjectDir: projectDir,
                TestCsproj: testCsproj,
                Configuration: "Debug",
                GodotBin: effectiveGodotBin,
                CacheDir: cacheDir,
                DisableBuildServers: true,
                ForceRun: force),
            hashIndex);
        if (testExit != 0)
        {
            Console.Error.WriteLine(
                $"The unit tests failed (exit {testExit}); nothing was exported. " +
                "Run `dotnet test Godot/tests/Refii.Tests` to see them on their own.");
            return testExit;
        }

        // Phase 2: Cached Godot Headless Import + Export.
        string appDir = Path.Combine(workDir, appName);
        int exportExit = GodotExportCommand.Execute(
            new GodotExportOptions(
                ProjectDir: projectDir,
                GodotBin: godotBin,
                GodotDir: GetOrDefault(flags, "godot-dir", Path.Combine(repoRoot, "tools", "godot")),
                Preset: RequireString(flags, "preset"),
                OutBinary: outBinary,
                AppDir: appDir,
                WorkDir: workDir,
                ToolPlatform: toolPlatform,
                ToolArch: toolArch,
                CacheDir: cacheDir,
                ForceExport: force),
            hashIndex,
            out HashSet<string> exportedPaths);
        if (exportExit != 0)
        {
            return exportExit;
        }

        // Phase 3: Incremental Parallel Staging + Orphan Pruning.
        int stageExit = RunStagePackage(repoRoot, cacheDir, flags, hashIndex, exportedPaths);
        if (stageExit != 0)
        {
            return stageExit;
        }

        // Phase 4: Multi-Core SIMD / Raw-Deflate-Reuse ZIP Packaging.
        ZipPackStats zipStats = IncrementalZipPacker.PackDirectory(
            appDir: appDir,
            archiveRootPrefix: appName,
            zipPath: zipPath,
            hashIndex: hashIndex,
            forceFullRebuild: force);

        totalSw.Stop();
        long zipMb = zipStats.ZipSizeBytes / (1024 * 1024);
        Console.WriteLine();
        Console.WriteLine($"-- Packaged: {zipPath} ({zipMb} MB) in {totalSw.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"--   folder:   {appDir}");
        Console.WriteLine($"--   contents: {outBinary}, bin/ (natives + recompile), sdk/ (modding editor)");
        return 0;
    }

    private static Dictionary<string, string> ParseFlags(string[] args, int startIndex)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = startIndex; i < args.Length; i++)
        {
            string token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string body = token[2..];
            int eq = body.IndexOf('=');
            if (eq >= 0)
            {
                map[body[..eq]] = body[(eq + 1)..];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                map[body] = args[++i];
            }
            else
            {
                map[body] = "1";
            }
        }

        return map;
    }

    private static string RequireString(Dictionary<string, string> flags, string key)
    {
        if (!flags.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing required option --{key}");
        }

        return value;
    }

    private static string RequirePath(Dictionary<string, string> flags, string key)
    {
        return Path.GetFullPath(RequireString(flags, key));
    }

    private static string RequireResolvedPath(Dictionary<string, string> flags, string key, string fallback)
    {
        return flags.TryGetValue(key, out string? val) && !string.IsNullOrWhiteSpace(val)
            ? Path.GetFullPath(val)
            : Path.GetFullPath(fallback);
    }

    private static string GetOrDefault(Dictionary<string, string> flags, string key, string defaultValue)
    {
        return flags.TryGetValue(key, out string? val) ? val : defaultValue;
    }

    private static bool HasBoolFlag(Dictionary<string, string> flags, string key)
    {
        if (!flags.TryGetValue(key, out string? val))
        {
            return false;
        }

        return val.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               val.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               val.Equals("on", StringComparison.OrdinalIgnoreCase) ||
               val.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> SplitSemicolonPaths(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        string[] parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<string>(parts.Length);
        foreach (string p in parts)
        {
            list.Add(Path.GetFullPath(p));
        }

        return list;
    }

    private static int UnknownCommand(string cmd)
    {
        Console.Error.WriteLine($"Unknown Refii.BuildCache command '{cmd}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: Refii.BuildCache <test-gate | godot-export | stage-package | pack-zip | package-release> [--options]");
    }
}
