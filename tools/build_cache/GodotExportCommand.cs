namespace Refii.BuildCache;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

/// <summary>
/// Options controlling cached Godot headless import and release export.
/// </summary>
/// <param name="ProjectDir">Absolute path to the <c>Godot/</c> directory.</param>
/// <param name="GodotBin">Absolute path to the Godot Mono editor executable.</param>
/// <param name="GodotDir">Directory holding the pinned Godot editor and <c>editor_data/export_templates</c>.</param>
/// <param name="Preset">Export preset name in <c>Godot/export_presets.cfg</c>.</param>
/// <param name="OutBinary">File name of the exported binary (e.g. <c>Fable Return to Albion.exe</c>).</param>
/// <param name="AppDir">Destination packaged application directory (<c>&lt;workDir&gt;/&lt;appName&gt;</c>).</param>
/// <param name="WorkDir">Platform packaging work directory (<c>out/package/&lt;platform&gt;-&lt;arch&gt;</c>).</param>
/// <param name="ToolPlatform">Target platform identifier (<c>windows</c> or <c>linux</c>).</param>
/// <param name="ToolArch">Target architecture identifier (<c>x86_64</c> or <c>arm64</c>).</param>
/// <param name="CacheDir">Shared build cache root (<c>&lt;repo&gt;/out/cache</c>).</param>
/// <param name="ForceExport">When <see langword="true"/>, bypasses any cached export payload.</param>
public sealed record GodotExportOptions(
    string ProjectDir,
    string GodotBin,
    string GodotDir,
    string Preset,
    string OutBinary,
    string AppDir,
    string WorkDir,
    string ToolPlatform,
    string ToolArch,
    string CacheDir,
    bool ForceExport);

/// <summary>
/// Content-addressed cache and runner for <c>godot --import</c> and <c>godot --export-release</c>.
/// </summary>
public static class GodotExportCommand
{
    private const string SchemaVersion = "Refii.GodotExport.v1";
    internal const string ExportManifestFileName = ".export-manifest.txt";

    private static readonly Regex EmbeddedLauncherRegex = new(
        @"\.godot/mono/publish/[A-Za-z0-9_]+/refii-launcher\.dll",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ExcludedExportDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".godot",
        "build",
        "levels",
        "creator",
        "tests",
        "bin",
        "obj",
        "TestResults",
        "gdunit4_testadapter_v5",
    };

    /// <summary>
    /// Computes the SHA-256 input fingerprint of all project files, export presets, Godot binary,
    /// and export templates affecting <paramref name="options"/>.
    /// </summary>
    /// <param name="options">Export configuration.</param>
    /// <param name="hashIndex">Shared persistent file hash index.</param>
    /// <returns>Lowercase 64-character hexadecimal SHA-256 digest.</returns>
    public static string ComputeExportDigest(GodotExportOptions options, FileHashIndex hashIndex)
    {
        using var manifest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        FileHashIndex.AppendString(manifest, SchemaVersion);
        FileHashIndex.AppendString(manifest, options.Preset);
        FileHashIndex.AppendString(manifest, options.OutBinary);
        FileHashIndex.AppendString(manifest, options.ToolPlatform);
        FileHashIndex.AppendString(manifest, options.ToolArch);

        AppendFileStat(manifest, "GODOT_BIN", options.GodotBin);

        if (!string.IsNullOrWhiteSpace(options.GodotDir))
        {
            string templatesDir = Path.Combine(options.GodotDir, "editor_data", "export_templates");
            if (Directory.Exists(templatesDir))
            {
                List<ScannedEntry> templates = FileHashIndex.ScanDirectory(templatesDir);
                FileHashIndex.AppendString(manifest, $"TEMPLATES:{templates.Count}");
                Span<byte> statBytes = stackalloc byte[16];
                foreach (ScannedEntry t in templates)
                {
                    FileHashIndex.AppendString(manifest, t.RelativePath);
                    BinaryPrimitives.WriteInt64LittleEndian(statBytes[..8], t.Length);
                    BinaryPrimitives.WriteInt64LittleEndian(statBytes[8..], t.LastWriteUtcTicks);
                    manifest.AppendData(statBytes);
                }
            }
        }

        List<ScannedEntry> projectEntries = FileHashIndex.ScanDirectory(
            options.ProjectDir,
            shouldIncludeDirectory: static (_, name) => !ExcludedExportDirs.Contains(name),
            shouldIncludeFile: static (_, name) => !IsExcludedExportFile(name));
        hashIndex.AppendEntriesToManifestHash("GodotExportInputs", projectEntries, manifest);

        return Convert.ToHexString(manifest.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// Restores a cached Godot export into <see cref="GodotExportOptions.AppDir"/> on a cache hit,
    /// or runs <c>godot --import</c> + <c>godot --export-release</c>, verifies <c>.NET</c> assemblies,
    /// updates the cache, and syncs the exported payload into <see cref="GodotExportOptions.AppDir"/>.
    /// </summary>
    /// <param name="options">Export configuration.</param>
    /// <param name="hashIndex">Shared persistent file hash index.</param>
    /// <param name="exportedRelativePaths">Receives the set of relative paths inside <c>AppDir</c> owned by the export.</param>
    /// <returns><c>0</c> on success; non-zero on export or validation failure.</returns>
    public static int Execute(
        GodotExportOptions options,
        FileHashIndex hashIndex,
        out HashSet<string> exportedRelativePaths)
    {
        exportedRelativePaths = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(options.GodotBin) || !File.Exists(options.GodotBin))
        {
            Console.Error.WriteLine($"Godot binary not found at '{options.GodotBin}'.");
            return 1;
        }

        var sw = Stopwatch.StartNew();
        string digest = ComputeExportDigest(options, hashIndex);
        string shortDigest = digest[..12];

        string cacheRoot = Path.Combine(options.CacheDir, "godot-export", $"{options.ToolPlatform}-{options.ToolArch}");
        string stampPath = Path.Combine(cacheRoot, "stamp.txt");
        string payloadDir = Path.Combine(cacheRoot, "payload");

        bool bypass = options.ForceExport ||
                      string.Equals(Environment.GetEnvironmentVariable("REFII_NO_BUILD_CACHE"), "1", StringComparison.Ordinal);

        if (!bypass &&
            File.Exists(stampPath) &&
            string.Equals(File.ReadAllText(stampPath).Trim(), digest, StringComparison.OrdinalIgnoreCase) &&
            VerifyExportedPayload(payloadDir, options.OutBinary, out _))
        {
            SyncPayloadToApp(payloadDir, options.AppDir, options.WorkDir, exportedRelativePaths);
            sw.Stop();
            Console.WriteLine(
                $"[export-cache] HIT ({shortDigest}, {sw.Elapsed.TotalMilliseconds:F0} ms) - " +
                $"reused cached '{options.Preset}' export ({exportedRelativePaths.Count} files).");
            return 0;
        }

        Console.WriteLine(
            $"[export-cache] {(bypass ? "FORCED" : "MISS")} ({shortDigest}) - " +
            $"importing & exporting '{options.Preset}' -> {options.OutBinary}...");

        RunGodotImport(options);

        string tempPayloadDir = Path.Combine(cacheRoot, $"payload.{Environment.ProcessId}.tmp");
        if (Directory.Exists(tempPayloadDir))
        {
            Directory.Delete(tempPayloadDir, recursive: true);
        }

        Directory.CreateDirectory(tempPayloadDir);

        string tempBinaryPath = Path.Combine(tempPayloadDir, options.OutBinary);
        int exportExit = RunGodotExportWithTeardownGuard(options, tempPayloadDir, tempBinaryPath);
        if (exportExit != 0)
        {
            CleanupTempDir(tempPayloadDir);
            Console.Error.WriteLine(
                $"Godot export failed (exit {exportExit}).\n" +
                "If it reported a missing preset, the names in Godot/export_presets.cfg must match " +
                "the ones in cmake/refii_godot_export.cmake - Godot selects a preset BY NAME.");
            return exportExit;
        }

        if (!VerifyExportedPayload(tempPayloadDir, options.OutBinary, out string validationError))
        {
            CleanupTempDir(tempPayloadDir);
            Console.Error.WriteLine(validationError);
            return 1;
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(
                    tempBinaryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch
            {
                // Best effort on POSIX hosts.
            }
        }

        Directory.CreateDirectory(cacheRoot);
        if (Directory.Exists(payloadDir))
        {
            Directory.Delete(payloadDir, recursive: true);
        }

        Directory.Move(tempPayloadDir, payloadDir);
        File.WriteAllText(stampPath, $"{digest}\n");

        SyncPayloadToApp(payloadDir, options.AppDir, options.WorkDir, exportedRelativePaths);
        sw.Stop();
        Console.WriteLine(
            $"[export-cache] Exported & cached '{options.Preset}' ({exportedRelativePaths.Count} files) in {sw.Elapsed.TotalSeconds:F1}s.");
        return 0;
    }

    /// <summary>
    /// Verifies that <paramref name="payloadDir"/> contains <paramref name="outBinary"/> AND either
    /// a loose <c>data_*/refii-launcher.dll</c> assembly or an embedded <c>.godot/mono/publish/*/refii-launcher.dll</c> payload.
    /// </summary>
    /// <param name="payloadDir">Directory containing the Godot export artefacts.</param>
    /// <param name="outBinary">Expected binary file name.</param>
    /// <param name="errorMessage">Detailed failure reason when returning <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when both the binary and <c>.NET</c> assemblies are verified present.</returns>
    public static bool VerifyExportedPayload(string payloadDir, string outBinary, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!Directory.Exists(payloadDir))
        {
            errorMessage = $"Export directory '{payloadDir}' does not exist.";
            return false;
        }

        string binaryPath = Path.Combine(payloadDir, outBinary);
        if (!File.Exists(binaryPath))
        {
            errorMessage =
                $"Godot reported success but wrote no binary to {binaryPath}. " +
                "Usually the export templates for this Godot version are not installed.";
            return false;
        }

        try
        {
            var info = new FileInfo(binaryPath);
            if (info.Length <= 0)
            {
                errorMessage = $"Exported binary {binaryPath} is empty (0 bytes).";
                return false;
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"Unable to inspect exported binary {binaryPath}: {ex.Message}";
            return false;
        }

        foreach (string dataDir in Directory.EnumerateDirectories(payloadDir, "data_*", SearchOption.TopDirectoryOnly))
        {
            string[] found = Directory.GetFiles(dataDir, "refii-launcher.dll", SearchOption.AllDirectories);
            if (found.Length > 0)
            {
                return true;
            }
        }

        if (ContainsEmbeddedLauncherAssembly(binaryPath))
        {
            return true;
        }

        errorMessage =
            $"The export wrote {binaryPath} without the .NET assemblies: neither a " +
            "data_* folder beside it nor refii-launcher.dll embedded in it. The game would " +
            "stop at \".NET assemblies not found\". Run the package again; if it repeats, " +
            "delete Godot/.godot/mono/temp and export once more.";
        return false;
    }

    private static bool ContainsEmbeddedLauncherAssembly(string binaryPath)
    {
        const int chunkSize = 1024 * 1024;
        const int overlap = 128;
        byte[] buffer = new byte[chunkSize + overlap];
        int carry = 0;

        using var stream = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
        while (true)
        {
            int read = stream.Read(buffer, carry, chunkSize);
            if (read <= 0)
            {
                return false;
            }

            int total = carry + read;
            string ascii = Encoding.Latin1.GetString(buffer, 0, total);
            if (EmbeddedLauncherRegex.IsMatch(ascii))
            {
                return true;
            }

            if (read < chunkSize)
            {
                return false;
            }

            carry = Math.Min(overlap, total);
            Buffer.BlockCopy(buffer, total - carry, buffer, 0, carry);
        }
    }

    private static void SyncPayloadToApp(
        string payloadDir,
        string appDir,
        string workDir,
        HashSet<string> exportedRelativePaths)
    {
        Directory.CreateDirectory(appDir);
        List<ScannedEntry> entries = FileHashIndex.ScanDirectory(payloadDir);
        var manifestLines = new List<string>(entries.Count);

        foreach (ScannedEntry entry in entries)
        {
            exportedRelativePaths.Add(entry.RelativePath);
            manifestLines.Add(entry.RelativePath);

            string destPath = Path.Combine(appDir, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            string? parent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            bool needsCopy = true;
            if (File.Exists(destPath))
            {
                var destInfo = new FileInfo(destPath);
                if (destInfo.Length == entry.Length && destInfo.LastWriteTimeUtc.Ticks == entry.LastWriteUtcTicks)
                {
                    needsCopy = false;
                }
            }

            if (needsCopy)
            {
                File.Copy(entry.FullPath, destPath, overwrite: true);
                File.SetLastWriteTimeUtc(destPath, new DateTime(entry.LastWriteUtcTicks, DateTimeKind.Utc));
                if (!OperatingSystem.IsWindows() && (entry.UnixMode & 0x49u) != 0u)
                {
                    try
                    {
                        File.SetUnixFileMode(
                            destPath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    }
                    catch
                    {
                        // Best effort.
                    }
                }
            }
        }

        manifestLines.Sort(StringComparer.Ordinal);
        Directory.CreateDirectory(workDir);
        File.WriteAllText(
            Path.Combine(workDir, ExportManifestFileName),
            manifestLines.Count == 0 ? string.Empty : string.Join("\n", manifestLines) + "\n");
    }

    private static void RunGodotImport(GodotExportOptions options)
    {
        var psi = CreateGodotStartInfo(options.GodotBin, options.ProjectDir);
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add("--path");
        psi.ArgumentList.Add(options.ProjectDir);
        psi.ArgumentList.Add("--import");

        using Process? proc = Process.Start(psi);
        if (proc is null)
        {
            Console.Error.WriteLine("[export-cache] Warning: could not start Godot --import.");
            return;
        }

        if (!proc.WaitForExit(180_000))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore kill race.
            }

            Console.WriteLine("[export-cache] Warning: Godot --import timed out; continuing to --export-release.");
            return;
        }

        if (proc.ExitCode != 0)
        {
            Console.WriteLine(
                $"[export-cache] Warning: Godot asset import returned {proc.ExitCode}; continuing to export.");
        }
    }

    private static int RunGodotExportWithTeardownGuard(
        GodotExportOptions options,
        string tempPayloadDir,
        string tempBinaryPath)
    {
        var psi = CreateGodotStartInfo(options.GodotBin, options.ProjectDir);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add("--path");
        psi.ArgumentList.Add(options.ProjectDir);
        psi.ArgumentList.Add("--export-release");
        psi.ArgumentList.Add(options.Preset);
        psi.ArgumentList.Add(tempBinaryPath);

        using Process? proc = Process.Start(psi);
        if (proc is null)
        {
            return 1;
        }

        long savepackDoneStopwatchTicks = 0;

        void InspectLine(string? line, TextWriter sink)
        {
            if (line is null)
            {
                return;
            }

            sink.WriteLine(line);
            if (line.Contains("[ DONE ] savepack", StringComparison.Ordinal) ||
                line.Contains("savepack: end", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.CompareExchange(ref savepackDoneStopwatchTicks, Stopwatch.GetTimestamp(), 0);
            }
        }

        proc.OutputDataReceived += (_, e) => InspectLine(e.Data, Console.Out);
        proc.ErrorDataReceived += (_, e) => InspectLine(e.Data, Console.Error);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var totalTimer = Stopwatch.StartNew();
        const int totalTimeoutMs = 180_000;
        const double postSavepackGraceSeconds = 5.0;

        while (!proc.WaitForExit(250))
        {
            long doneAt = Interlocked.Read(ref savepackDoneStopwatchTicks);
            if (doneAt != 0)
            {
                double sinceDoneSec = Stopwatch.GetElapsedTime(doneAt).TotalSeconds;
                if (sinceDoneSec >= postSavepackGraceSeconds &&
                    CanOpenExclusively(tempBinaryPath) &&
                    VerifyExportedPayload(tempPayloadDir, options.OutBinary, out _))
                {
                    Console.WriteLine(
                        "[export-cache] Godot completed savepack and wrote verified artefacts, " +
                        "but stalled in post-export teardown; terminating hung editor process cleanly.");
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(5_000);
                    }
                    catch
                    {
                        // Ignore teardown race.
                    }

                    return 0;
                }
            }

            if (totalTimer.ElapsedMilliseconds >= totalTimeoutMs)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5_000);
                }
                catch
                {
                    // Ignore kill race.
                }

                if (VerifyExportedPayload(tempPayloadDir, options.OutBinary, out _))
                {
                    Console.WriteLine(
                        $"[export-cache] Warning: Godot export timed out in teardown after writing {tempBinaryPath}; artefacts verified.");
                    return 0;
                }

                return 124;
            }
        }

        proc.WaitForExit();
        return proc.ExitCode;
    }

    private static bool CanOpenExclusively(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return fs.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessStartInfo CreateGodotStartInfo(string godotBin, string projectDir)
    {
        var psi = new ProcessStartInfo(godotBin)
        {
            UseShellExecute = false,
            WorkingDirectory = projectDir,
        };
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
        psi.Environment["UseSharedCompilation"] = "false";
        psi.Environment["REFII_NO_SHARED_COMPILATION"] = "1";
        return psi;
    }

    private static bool IsExcludedExportFile(string fileName)
    {
        return fileName.EndsWith(".x86_64", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".pck", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("refii.toml", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("refiii.toml", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("android-runtime.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals(".DS_Store", StringComparison.Ordinal);
    }

    private static void AppendFileStat(IncrementalHash manifest, string label, string path)
    {
        FileHashIndex.AppendString(manifest, $"STAT:{label}:{path}");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var info = new FileInfo(path);
            Span<byte> buf = stackalloc byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(buf[..8], info.Length);
            BinaryPrimitives.WriteInt64LittleEndian(buf[8..], info.LastWriteTimeUtc.Ticks);
            manifest.AppendData(buf);
        }
    }

    private static void CleanupTempDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
