namespace Refii.BuildCache;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

/// <summary>
/// Options controlling the content-addressed unit and feature test gate.
/// </summary>
/// <param name="RepoRoot">Absolute path to the repository root.</param>
/// <param name="ProjectDir">Absolute path to the <c>Godot/</c> project directory.</param>
/// <param name="TestCsproj">Absolute path to <c>Refii.Tests.csproj</c>.</param>
/// <param name="Configuration">MSBuild configuration name (e.g. <c>Debug</c> or <c>Release</c>).</param>
/// <param name="GodotBin">Optional path to the Godot Mono binary used for <c>[RequireGodotRuntime]</c> suites.</param>
/// <param name="CacheDir">Root build-cache directory (typically <c>&lt;repo&gt;/out/cache</c>).</param>
/// <param name="DisableBuildServers">When <see langword="true"/>, suppresses persistent MSBuild/Roslyn daemons for headless CMake runs.</param>
/// <param name="ForceRun">When <see langword="true"/>, executes <c>dotnet test</c> even if a matching pass stamp exists.</param>
public sealed record TestGateOptions(
    string RepoRoot,
    string ProjectDir,
    string TestCsproj,
    string Configuration,
    string GodotBin,
    string CacheDir,
    bool DisableBuildServers,
    bool ForceRun);

/// <summary>
/// Turborepo-style input-hashed test gate shared by <c>dotnet build Godot/refii-launcher.csproj</c>
/// and <c>cmake/refii_run_godot_export.cmake</c>.
/// </summary>
public static class TestGateCommand
{
    private const string SchemaVersion = "Refii.TestGate.v1";

    private static readonly HashSet<string> ExcludedGodotDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".godot",
        "build",
        "levels",
        "bin",
        "obj",
        "TestResults",
        "gdunit4_testadapter_v5",
    };

    private static readonly HashSet<string> ExcludedBuildToolDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
    };

    /// <summary>
    /// Computes the composite SHA-256 input fingerprint for <c>Refii.Tests</c>.
    /// </summary>
    /// <param name="options">Test gate configuration.</param>
    /// <param name="hashIndex">Shared persistent file hash index.</param>
    /// <returns>Lowercase 64-character hexadecimal SHA-256 digest.</returns>
    public static string ComputeInputDigest(TestGateOptions options, FileHashIndex hashIndex)
    {
        using var manifest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        FileHashIndex.AppendString(manifest, SchemaVersion);
        FileHashIndex.AppendString(manifest, options.Configuration);

        AppendBinaryMetadata(manifest, "GODOT_BIN", options.GodotBin);

        List<ScannedEntry> godotEntries = FileHashIndex.ScanDirectory(
            options.ProjectDir,
            shouldIncludeDirectory: static (_, name) => !ExcludedGodotDirs.Contains(name),
            shouldIncludeFile: static (_, name) => !IsExcludedGodotTestFile(name));
        hashIndex.AppendEntriesToManifestHash("Godot", godotEntries, manifest);

        string buildToolDir = Path.Combine(options.RepoRoot, "tools", "build_cache");
        List<ScannedEntry> toolEntries = FileHashIndex.ScanDirectory(
            buildToolDir,
            shouldIncludeDirectory: static (_, name) => !ExcludedBuildToolDirs.Contains(name),
            shouldIncludeFile: static (_, name) =>
                !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".user", StringComparison.OrdinalIgnoreCase));
        hashIndex.AppendEntriesToManifestHash("tools/build_cache", toolEntries, manifest);

        // External repository assets read directly by tests via [CallerFilePath].
        string pchTemplate = Path.Combine(
            options.RepoRoot,
            "rexglue-net", "shadow", "rexglue-sdk", "resources", "templates", "codegen", "pch_h.inja");
        if (File.Exists(pchTemplate))
        {
            FileHashIndex.AppendString(manifest, "FILE:pch_h.inja");
            manifest.AppendData(hashIndex.GetOrComputeFileSha256(pchTemplate));
        }

        AppendOptionalDirectory(options.RepoRoot, "Fable2/mods-src", hashIndex, manifest);
        AppendOptionalDirectory(options.RepoRoot, "Fable2/versions", hashIndex, manifest);
        AppendOptionalDirectory(options.RepoRoot, "Fable3/versions", hashIndex, manifest);

        return Convert.ToHexString(manifest.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// Evaluates the test gate, skipping <c>dotnet test</c> when all inputs match a prior passing stamp.
    /// </summary>
    /// <param name="options">Test gate configuration.</param>
    /// <param name="hashIndex">Shared persistent file hash index.</param>
    /// <returns>Process exit code (<c>0</c> on cache hit or passing test run).</returns>
    public static int Execute(TestGateOptions options, FileHashIndex hashIndex)
    {
        if (!File.Exists(options.TestCsproj))
        {
            Console.WriteLine($"[test-cache] No test project at {options.TestCsproj}; skipping.");
            return 0;
        }

        var sw = Stopwatch.StartNew();
        string digest = ComputeInputDigest(options, hashIndex);
        string shortDigest = digest[..12];
        string stampsDir = Path.Combine(options.CacheDir, "test-stamps");
        string stampFile = Path.Combine(stampsDir, $"{digest}.pass");

        bool bypass = options.ForceRun || IsCacheDisabled();
        if (!bypass && File.Exists(stampFile))
        {
            sw.Stop();
            Console.WriteLine(
                $"[test-cache] HIT ({shortDigest}, {sw.Elapsed.TotalMilliseconds:F0} ms) - " +
                "inputs unchanged, skipping unit & feature tests.");
            return 0;
        }

        long hashMs = sw.ElapsedMilliseconds;
        Console.WriteLine(
            $"[test-cache] {(bypass ? "FORCED" : "MISS")} ({shortDigest}, hashed in {hashMs} ms) - " +
            $"running unit & feature tests (GODOT_BIN={options.GodotBin})...");

        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            WorkingDirectory = options.RepoRoot,
        };
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add(options.TestCsproj);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(options.Configuration);
        psi.ArgumentList.Add("-p:IsTesting=true");
        psi.ArgumentList.Add("-p:REFII_NO_TESTS=1");
        if (options.DisableBuildServers)
        {
            psi.ArgumentList.Add("-p:UseSharedCompilation=false");
            psi.ArgumentList.Add("-p:NodeReuse=false");
            psi.ArgumentList.Add("--disable-build-servers");
            psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
            psi.Environment["UseSharedCompilation"] = "false";
        }

        psi.ArgumentList.Add("--logger");
        psi.ArgumentList.Add("console;verbosity=minimal");

        psi.Environment["REFII_NO_TESTS"] = "1";
        if (!string.IsNullOrWhiteSpace(options.GodotBin))
        {
            string? shimExe = !bypass ? GodotTestShim.TryFindSelfAppHostPath() : null;
            if (shimExe is not null)
            {
                psi.Environment[GodotTestShim.RealGodotEnvVar] = options.GodotBin;
                psi.Environment["GODOT_BIN"] = shimExe;
            }
            else
            {
                psi.Environment["GODOT_BIN"] = options.GodotBin;
            }
        }

        using Process? proc = Process.Start(psi);
        if (proc is null)
        {
            Console.Error.WriteLine("[test-cache] Failed to start `dotnet test`.");
            return 1;
        }

        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            return proc.ExitCode;
        }

        Directory.CreateDirectory(stampsDir);
        File.WriteAllText(
            stampFile,
            $"ok {DateTime.UtcNow:O} config={options.Configuration}\n");
        PruneOldStamps(stampsDir, keepCount: 32);

        sw.Stop();
        Console.WriteLine($"[test-cache] Recorded pass stamp {shortDigest} ({sw.Elapsed.TotalSeconds:F1}s total).");
        return 0;
    }

    internal static bool IsCacheDisabled()
    {
        return string.Equals(Environment.GetEnvironmentVariable("REFII_NO_BUILD_CACHE"), "1", StringComparison.Ordinal) ||
               string.Equals(Environment.GetEnvironmentVariable("REFII_FORCE_TESTS"), "1", StringComparison.Ordinal);
    }

    private static bool IsExcludedGodotTestFile(string fileName)
    {
        return fileName.EndsWith(".uid", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".x86_64", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".pck", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("refii.toml", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("refiii.toml", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals(".DS_Store", StringComparison.Ordinal);
    }

    private static void AppendOptionalDirectory(
        string repoRoot,
        string relativeSubdir,
        FileHashIndex hashIndex,
        IncrementalHash manifest)
    {
        string fullDir = Path.Combine(repoRoot, relativeSubdir.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(fullDir))
        {
            return;
        }

        List<ScannedEntry> entries = FileHashIndex.ScanDirectory(fullDir);
        hashIndex.AppendEntriesToManifestHash(relativeSubdir, entries, manifest);
    }

    private static void AppendBinaryMetadata(IncrementalHash manifest, string label, string path)
    {
        FileHashIndex.AppendString(manifest, $"BIN:{label}:{path}");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var info = new FileInfo(path);
            Span<byte> meta = stackalloc byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(meta[..8], info.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(meta[8..], info.LastWriteTimeUtc.Ticks);
            manifest.AppendData(meta);
        }
    }

    private static void PruneOldStamps(string stampsDir, int keepCount)
    {
        try
        {
            FileInfo[] files = new DirectoryInfo(stampsDir).GetFiles("*.pass");
            if (files.Length <= keepCount)
            {
                return;
            }

            Array.Sort(files, static (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = keepCount; i < files.Length; i++)
            {
                files[i].Delete();
            }
        }
        catch
        {
            // Non-fatal cache housekeeping.
        }
    }
}
