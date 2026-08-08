using System;
using System.IO;

namespace Refii.Launcher.Core;

public static class Installer
{
    public static string GetInstallBasePath() =>
        LauncherPaths.ResolveFromLauncherDirectory(Ini.GetString("GamePath", "assets/", "Game"));

    public static string GetInstalledExecutablePath() =>
        Path.Combine(Platform.GetExecutableDirectory(), Platform.ExecutableName);

    public static string GetRuntimeConfigPath() =>
        Path.Combine(Platform.GetExecutableDirectory(), "refii.toml");

    public static string GetGameDataDirectoryName() =>
        Ini.GetString("GameDataDirectory", "game", "Game");

    public static string GetGameMarkerRelativePath() =>
        Ini.GetString("GameMarkerFile", "game/default.xexp", "Game");

    private const int WindowsMaxPath = 260;
    private const int DeepestAssetRelativeLength = 16 + 178;

    private static bool? _longPathsUsable;

    public static bool LongPathsUsable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        if (_longPathsUsable.HasValue)
        {
            return _longPathsUsable.Value;
        }

        bool usable = false;
        string probe = Path.Combine(Path.GetTempPath(), new string('p', 250));
        try
        {
            if (probe.Length > WindowsMaxPath)
            {
                Directory.CreateDirectory(probe);
                usable = Directory.Exists(probe);
            }
        }
        catch (Exception)
        {
            usable = false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(probe))
                {
                    Directory.Delete(probe);
                }
            }
            catch (Exception)
            {
            }
        }

        _longPathsUsable = usable;
        return usable;
    }

    public static bool InstallBasePathLeavesWindowsPathRoom(string basePath, out string error)
    {
        if (!OperatingSystem.IsWindows() || LongPathsUsable())
        {
            error = string.Empty;
            return true;
        }

        int baseLength = Path.GetFullPath(basePath).Length;
        if (baseLength + 1 + DeepestAssetRelativeLength >= WindowsMaxPath)
        {
            error = "The install folder path is too long for Windows: game " +
                    "files would exceed the 260 character path limit. Move " +
                    @"the launcher to a shorter path (for example C:\Games\" +
                    "Refii) and try again.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool CopyGameExecutable(string sourcePath, string destinationPath, out string error)
    {
        if (!SafePath.IsSafeDestinationUnderBase(destinationPath, GetInstallBasePath(), out error))
        {
            return false;
        }

        try
        {
            if (!File.Exists(sourcePath))
            {
                error = "The selected XEXP file does not exist.";
                return false;
            }

            string? parent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent!);
            }

            File.Copy(sourcePath, destinationPath, true);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static string GetGameMarkerPath() =>
        Path.Combine(GetInstallBasePath(), GetGameMarkerRelativePath());

    public static string GetGameTitle() => "Fable 2";

    public static string GetGameDataDirectoryPath() =>
        Path.Combine(GetInstallBasePath(), GetGameDataDirectoryName());

    private static string GetWorkspaceBasePath(string gameInstallRoot)
    {
        string basePath = Path.TrimEndingDirectorySeparator(gameInstallRoot);
        if (string.Equals(Path.GetFileName(basePath), "data", StringComparison.OrdinalIgnoreCase))
        {
            basePath = Path.GetDirectoryName(basePath) ?? basePath;
        }

        if (string.Equals(Path.GetFileName(basePath), "game", StringComparison.OrdinalIgnoreCase))
        {
            basePath = Path.GetDirectoryName(basePath) ?? basePath;
        }

        return basePath;
    }

    public static string GetLuaWorkspacePath() =>
        Path.Combine(GetWorkspaceBasePath(GetGameDataDirectoryPath()), "unpacked");

    public readonly record struct ReinstallStepStatus(bool Banks, bool Lua, bool Shaders);

    private const long StepStatusTtlMs = 1000;
    private static ReinstallStepStatus _stepStatus;
    private static bool _stepStatusValid;
    private static long _stepStatusStamp;

    public static void InvalidateReinstallStepStatus() => _stepStatusValid = false;

    public static ReinstallStepStatus GetReinstallStepStatus()
    {
        long now = Environment.TickCount64;
        if (_stepStatusValid && now - _stepStatusStamp < StepStatusTtlMs)
        {
            return _stepStatus;
        }

        string workspace = GetLuaWorkspacePath();
        _stepStatus = new ReinstallStepStatus(
            File.Exists(Path.Combine(workspace, "dir.manifest")),
            File.Exists(Path.Combine(workspace, "_manifest.live.tsv")),
            File.Exists(Path.Combine(workspace, "shaders", "compile_report.tsv")));
        _stepStatusValid = true;
        _stepStatusStamp = now;
        return _stepStatus;
    }

    public static string GetGameContentDirectoryPath() =>
        Path.Combine(GetGameDataDirectoryPath(), "data");

    public static bool EnsureRuntimeConfigFile(out string error) =>
        RuntimeConfig.EnsureDefaults(GetRuntimeConfigPath(), out error);

    /// <summary>
    /// True when a directory really holds an extracted install.
    ///
    /// The marker is necessary but NOT sufficient. A source checkout ships
    /// <c>assets/game/default.xexp</c> with no banks beside it, so a marker-only test
    /// reports "installed" for a tree that has no game in it — and everything
    /// downstream then tries to mount nothing and reports 0 files instead of saying the
    /// game is missing. The banks are the payload, so the banks are the test.
    /// </summary>
    public static bool HasExtractedAssets(string gameDataDirectory, string markerPath) =>
        Directory.Exists(gameDataDirectory) && File.Exists(markerPath) &&
        HasBanks(gameDataDirectory);

    /// <summary>
    /// True when a tree carries at least one <c>.bnk</c>.
    ///
    /// Two levels, not recursive: an unpacked asset tree is large enough that walking
    /// all of it to conclude "no banks" stalls startup for tens of seconds, and the
    /// banks always sit at the top or one level down.
    /// </summary>
    public static bool HasBanks(string root)
    {
        if (!Directory.Exists(root))
        {
            return false;
        }

        try
        {
            foreach (string _ in Directory.EnumerateFiles(root, "*.bnk", SearchOption.TopDirectoryOnly))
                return true;

            foreach (string child in Directory.EnumerateDirectories(root))
                foreach (string _ in Directory.EnumerateFiles(child, "*.bnk", SearchOption.TopDirectoryOnly))
                    return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Where the game data actually is, or empty when it is not installed anywhere.
    ///
    /// The configured install location wins — that is what a player sets and where an
    /// install writes. A DEV tree is the exception: nothing has been installed under the
    /// launcher directory and the data sits staged in a build output instead, so the
    /// shared convention is consulted before giving up. Both candidates must carry banks
    /// to be accepted; a marker on its own is not an install.
    /// </summary>
    public static string ResolveGameDataDirectory()
    {
        string configured = GetGameDataDirectoryPath();
        if (HasExtractedAssets(configured, GetGameMarkerPath()))
        {
            return configured;
        }

        string resolved = Refii.Launcher.Formats.UiGameData.Resolve();
        return HasBanks(resolved) ? resolved : string.Empty;
    }

    public static bool AssetsExist() => ResolveGameDataDirectory().Length > 0;

    public static bool InstalledBuildExists() => File.Exists(GetInstalledExecutablePath());

    public static bool UninstallManagedFiles(out string log)
    {
        var removed = new System.Text.StringBuilder();
        bool hasRemovedFiles = false;

        foreach (string directory in new[] { GetGameDataDirectoryPath(), GetUpdateDirectoryPath() })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                removed.AppendLine("Removed " + directory);
                hasRemovedFiles = true;
            }
            catch (System.Exception exception)
            {
                removed.AppendLine("Failed to remove " + directory + ": " + exception.Message);
            }
        }

        log = hasRemovedFiles
            ? removed.ToString().TrimEnd() + "\nFable 2 install removed successfully."
            : string.Empty;
        return hasRemovedFiles;
    }

    public static string GetUpdateDirectoryName() =>
        Ini.GetString("UpdateDirectory", "update", "Game");

    public static string GetUpdateDirectoryPath() =>
        Path.Combine(GetInstallBasePath(), GetUpdateDirectoryName());
}
