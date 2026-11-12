namespace Refii.BuildCache;

using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Deterministically removes legacy packaged directories, binaries, and zip/index artefacts left behind
/// by historical application/package names (<c>Fable 2</c>, <c>Fable 2 Recompiled</c>,
/// <c>Fable 2 Return to Albion</c>, and <c>refii-&lt;platform&gt;-&lt;arch&gt;.zip</c>).
/// </summary>
public static class LegacyPackageCleanup
{
    private static readonly string[] LegacyAppNames =
    {
        "Fable 2",
        "Fable 2 Recompiled",
        "Fable 2 Return to Albion",
        "Fable2",
    };

    private static readonly string[] LegacyZipPrefixes =
    {
        "refii",
        "Fable 2",
        "Fable 2 Recompiled",
        "Fable 2 Return to Albion",
    };

    private static readonly string[] LegacyBinaryExtensions =
    {
        ".exe",
        ".console.exe",
        ".pck",
        ".pdb",
        ".x86_64",
        ".arm64",
    };

    /// <summary>
    /// Removes stale sibling package folders inside <paramref name="workDir"/>, stale zip/sidecar files inside
    /// <paramref name="releasePackageDir"/>, and stray manual-export legacy binaries inside <paramref name="projectDir"/>.
    /// Never removes the active <paramref name="appName"/> folder or active <paramref name="activeZipPath"/>.
    /// </summary>
    /// <param name="workDir">Platform packaging directory (e.g. <c>out/package/windows-x86_64</c>).</param>
    /// <param name="appName">Active application folder name (e.g. <c>Fable Return to Albion</c>).</param>
    /// <param name="releasePackageDir">Directory where release zips are emitted (e.g. <c>out/package</c>).</param>
    /// <param name="activeZipPath">Optional active output zip path to preserve.</param>
    /// <param name="toolPlatform">Optional target platform (<c>windows</c> or <c>linux</c>).</param>
    /// <param name="toolArch">Optional target architecture (<c>x86_64</c> or <c>arm64</c>).</param>
    /// <param name="projectDir">Optional Godot project directory to sweep for stray legacy manual exports.</param>
    /// <returns>List of full paths that were removed.</returns>
    public static IReadOnlyList<string> CleanLegacyArtifacts(
        string workDir,
        string appName,
        string? releasePackageDir = null,
        string? activeZipPath = null,
        string? toolPlatform = null,
        string? toolArch = null,
        string? projectDir = null)
    {
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var removed = new List<string>();
        string normalizedActiveAppDir = !string.IsNullOrWhiteSpace(workDir) && !string.IsNullOrWhiteSpace(appName)
            ? Path.GetFullPath(Path.Combine(workDir, appName))
            : string.Empty;
        string normalizedActiveZip = !string.IsNullOrWhiteSpace(activeZipPath)
            ? Path.GetFullPath(activeZipPath)
            : string.Empty;
        string normalizedActiveIdx = normalizedActiveZip.Length != 0
            ? normalizedActiveZip + ".idx"
            : string.Empty;

        // 1. Sibling legacy app folders inside workDir (e.g. out/package/windows-x86_64/Fable 2 Return to Albion).
        if (!string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir))
        {
            foreach (string legacyName in LegacyAppNames)
            {
                if (comparer.Equals(legacyName, appName))
                {
                    continue;
                }

                string candidateDir = Path.GetFullPath(Path.Combine(workDir, legacyName));
                if (comparer.Equals(candidateDir, normalizedActiveAppDir) || !Directory.Exists(candidateDir))
                {
                    continue;
                }

                if (TryDeleteDirectory(candidateDir))
                {
                    removed.Add(candidateDir);
                }
            }
        }

        // 2. Legacy .zip and .zip.idx sidecars in releasePackageDir (e.g. out/package/refii-windows-x86_64.zip*).
        string? effectiveReleaseDir = !string.IsNullOrWhiteSpace(releasePackageDir)
            ? releasePackageDir
            : (!string.IsNullOrWhiteSpace(activeZipPath)
                ? Path.GetDirectoryName(Path.GetFullPath(activeZipPath))
                : (!string.IsNullOrWhiteSpace(workDir) ? Directory.GetParent(Path.GetFullPath(workDir))?.FullName : null));

        if (!string.IsNullOrWhiteSpace(effectiveReleaseDir) && Directory.Exists(effectiveReleaseDir))
        {
            var candidateStems = new HashSet<string>(comparer);
            foreach (string prefix in LegacyZipPrefixes)
            {
                candidateStems.Add(prefix);
                if (!string.IsNullOrWhiteSpace(toolPlatform) && !string.IsNullOrWhiteSpace(toolArch))
                {
                    candidateStems.Add($"{prefix}-{toolPlatform}-{toolArch}");
                }
            }

            foreach (string stem in candidateStems)
            {
                foreach (string suffix in new[] { ".zip", ".zip.idx" })
                {
                    string candidateFile = Path.GetFullPath(Path.Combine(effectiveReleaseDir, stem + suffix));
                    if (comparer.Equals(candidateFile, normalizedActiveZip) ||
                        comparer.Equals(candidateFile, normalizedActiveIdx) ||
                        !File.Exists(candidateFile))
                    {
                        continue;
                    }

                    if (TryDeleteFile(candidateFile))
                    {
                        removed.Add(candidateFile);
                    }
                }
            }
        }

        // 3. Stray manual-export legacy binaries and data_Fable 2* directories in Godot/ project root.
        if (!string.IsNullOrWhiteSpace(projectDir) && Directory.Exists(projectDir))
        {
            foreach (string legacyName in LegacyAppNames)
            {
                if (comparer.Equals(legacyName, appName))
                {
                    continue;
                }

                foreach (string ext in LegacyBinaryExtensions)
                {
                    string strayFile = Path.GetFullPath(Path.Combine(projectDir, legacyName + ext));
                    if (File.Exists(strayFile) && TryDeleteFile(strayFile))
                    {
                        removed.Add(strayFile);
                    }
                }

                string dataPrefix = "data_" + legacyName + "_";
                foreach (string subDir in Directory.GetDirectories(projectDir, "data_*", SearchOption.TopDirectoryOnly))
                {
                    string dirName = Path.GetFileName(subDir);
                    if (dirName.StartsWith(dataPrefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                        TryDeleteDirectory(subDir))
                    {
                        removed.Add(Path.GetFullPath(subDir));
                    }
                }
            }
        }

        if (removed.Count > 0)
        {
            Console.WriteLine($"[cleanup] Removed {removed.Count} legacy package artefact(s) superseded by '{appName}'.");
        }

        return removed;
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cleanup] Warning: could not remove legacy file '{path}': {ex.Message}");
            return false;
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                    // Best effort before recursive delete.
                }
            }

            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cleanup] Warning: could not remove legacy folder '{path}': {ex.Message}");
            return false;
        }
    }
}
