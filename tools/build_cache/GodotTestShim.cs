namespace Refii.BuildCache;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

/// <summary>
/// Fast pass-through shim for <c>GODOT_BIN</c> during <c>GdUnit4</c> test execution.
/// Skips <c>GdUnit4</c>'s unconditional 45-second headless editor warmup
/// (<c>--path . -e --headless --quit-after 100</c>) when the test project's <c>.godot/uid_cache.bin</c>
/// and <c>GdUnit4TestRunnerScene.cs.uid</c> are already populated, while forwarding <c>--help</c>,
/// <c>-s GdUnit4TestRunnerScene.cs</c>, and cold first-time editor warmups directly to the real Godot binary.
/// </summary>
public static class GodotTestShim
{
    /// <summary>
    /// Environment variable pointing to the underlying Godot Mono executable when <c>GODOT_BIN</c>
    /// points to <c>Refii.BuildCache</c>.
    /// </summary>
    public const string RealGodotEnvVar = "REFII_REAL_GODOT_BIN";

    private static readonly HashSet<string> KnownBuildCacheSubcommands = new(StringComparer.Ordinal)
    {
        "test-gate",
        "godot-export",
        "stage-package",
        "pack-zip",
        "package-release",
        "help",
    };

    private static readonly HashSet<string> ExcludedScanDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".godot",
        "bin",
        "obj",
        "build",
        "TestResults",
    };

    /// <summary>
    /// Determines whether the current process invocation came from <c>GdUnit4</c> invoking <c>GODOT_BIN</c>
    /// rather than a direct <c>Refii.BuildCache</c> CLI subcommand.
    /// </summary>
    /// <param name="args">Command-line arguments passed to <c>Main</c>.</param>
    /// <param name="realGodotBin">Receives the resolved path to the real Godot binary when returning <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when this invocation should be handled by <see cref="Execute"/>.</returns>
    public static bool IsGodotShimInvocation(string[] args, out string realGodotBin)
    {
        realGodotBin = Environment.GetEnvironmentVariable(RealGodotEnvVar) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(realGodotBin) || args.Length == 0)
        {
            return false;
        }

        return !KnownBuildCacheSubcommands.Contains(args[0]);
    }

    /// <summary>
    /// Evaluates whether <c>GdUnit4</c>'s <c>ReCompileGodotProject</c> editor invocation
    /// (<c>-e --headless --quit-after 100</c>) can be safely short-circuited because the Godot
    /// UID cache and test-runner scene metadata are already present on disk.
    /// </summary>
    /// <param name="args">Arguments passed by <c>GdUnit4</c> to <c>GODOT_BIN</c>.</param>
    /// <param name="workingDirectory">Working directory in which <c>GODOT_BIN</c> was spawned.</param>
    /// <param name="cacheDisabled">Whether build/test caching has been disabled via environment flags.</param>
    /// <returns><see langword="true"/> if the redundant editor warmup can be skipped.</returns>
    public static bool CanSkipEditorWarmup(
        ReadOnlySpan<string> args,
        string workingDirectory,
        bool cacheDisabled = false)
    {
        if (cacheDisabled)
        {
            return false;
        }

        bool hasEditorFlag = false;
        bool hasHeadlessFlag = false;
        bool hasQuitAfterFlag = false;
        string? explicitPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "-e", StringComparison.Ordinal) ||
                string.Equals(arg, "--editor", StringComparison.Ordinal))
            {
                hasEditorFlag = true;
            }
            else if (string.Equals(arg, "--headless", StringComparison.Ordinal))
            {
                hasHeadlessFlag = true;
            }
            else if (string.Equals(arg, "--quit-after", StringComparison.Ordinal))
            {
                hasQuitAfterFlag = true;
            }
            else if (string.Equals(arg, "-s", StringComparison.Ordinal) ||
                     string.Equals(arg, "--script", StringComparison.Ordinal) ||
                     arg.StartsWith("--export", StringComparison.Ordinal))
            {
                return false;
            }
            else if (string.Equals(arg, "--path", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                explicitPath = args[++i];
            }
        }

        if (!hasEditorFlag || !hasHeadlessFlag || !hasQuitAfterFlag)
        {
            return false;
        }

        string projectDir = string.IsNullOrWhiteSpace(explicitPath)
            ? workingDirectory
            : Path.GetFullPath(explicitPath, workingDirectory);

        var uidCache = new FileInfo(Path.Combine(projectDir, ".godot", "uid_cache.bin"));
        if (!uidCache.Exists || uidCache.Length == 0)
        {
            return false;
        }

        string runnerUid = Path.Combine(projectDir, "gdunit4_testadapter_v5", "GdUnit4TestRunnerScene.cs.uid");
        if (!File.Exists(runnerUid))
        {
            return false;
        }

        return !HasUnimportedSceneOrScriptAssets(projectDir);
    }

    /// <summary>
    /// Executes the shim, either short-circuiting a warm <c>-e --quit-after</c> editor pass or
    /// forwarding standard handles and arguments directly to <paramref name="realGodotBin"/>.
    /// </summary>
    /// <param name="realGodotBin">Path to the underlying Godot Mono executable.</param>
    /// <param name="args">Command-line arguments from <c>GdUnit4</c>.</param>
    /// <returns>Exit code (<c>0</c> on skipped warmup or underlying Godot process exit code).</returns>
    public static int Execute(string realGodotBin, string[] args)
    {
        if (!File.Exists(realGodotBin))
        {
            Console.Error.WriteLine($"[godot-test-shim] Underlying Godot executable not found at '{realGodotBin}'.");
            return 1;
        }

        if (CanSkipEditorWarmup(args, Environment.CurrentDirectory, TestGateCommand.IsCacheDisabled()))
        {
            Console.WriteLine("[godot-test-shim] .godot UID cache warm; skipping redundant headless editor warmup.");
            return 0;
        }

        var psi = new ProcessStartInfo(realGodotBin)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // Prevent recursive shim invocation inside child Godot processes.
        psi.Environment[RealGodotEnvVar] = string.Empty;
        psi.Environment["GODOT_BIN"] = realGodotBin;

        using Process? proc = Process.Start(psi);
        if (proc is null)
        {
            Console.Error.WriteLine($"[godot-test-shim] Failed to launch '{realGodotBin}'.");
            return 1;
        }

        proc.WaitForExit();
        return proc.ExitCode;
    }

    /// <summary>
    /// Resolves the native apphost executable (<c>Refii.BuildCache.exe</c> or <c>Refii.BuildCache</c>)
    /// located alongside <c>Refii.BuildCache.dll</c>, if present.
    /// </summary>
    /// <returns>Absolute apphost path, or <see langword="null"/> if not found.</returns>
    public static string? TryFindSelfAppHostPath()
    {
        string exeName = OperatingSystem.IsWindows() ? "Refii.BuildCache.exe" : "Refii.BuildCache";
        string candidate = Path.Combine(AppContext.BaseDirectory, exeName);
        return File.Exists(candidate) ? candidate : null;
    }

    private static bool HasUnimportedSceneOrScriptAssets(string projectDir)
    {
        if (!Directory.Exists(projectDir))
        {
            return false;
        }

        var stack = new Stack<string>();
        stack.Push(projectDir);
        while (stack.Count > 0)
        {
            string current = stack.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(current).GetFileSystemInfos();
            }
            catch
            {
                continue;
            }

            foreach (FileSystemInfo entry in entries)
            {
                if (entry.LinkTarget is not null)
                {
                    continue;
                }

                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    if (!ExcludedScanDirs.Contains(entry.Name))
                    {
                        stack.Push(entry.FullName);
                    }

                    continue;
                }

                string ext = entry.Extension;
                if (ext.Equals(".gd", StringComparison.OrdinalIgnoreCase) &&
                    !File.Exists(entry.FullName + ".uid"))
                {
                    return true;
                }

                if ((ext.Equals(".tscn", StringComparison.OrdinalIgnoreCase) ||
                     ext.Equals(".tres", StringComparison.OrdinalIgnoreCase)) &&
                    !File.Exists(entry.FullName + ".uid") &&
                    !File.Exists(entry.FullName + ".import"))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
