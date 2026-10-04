using System;
using System.IO;

namespace Refii.Launcher.Core;

public static class Installer
{
    public static string GetInstallBasePath() =>
        LauncherPaths.ResolveFromLauncherDirectory(Ini.GetString("GamePath", "assets/", "Game"));

    public static string GetInstalledExecutablePath() =>
        Path.Combine(Platform.GetExecutableDirectory(), Platform.ExecutableName);

    /// <summary>
    /// The runtime config, at the app root.
    /// </summary>
    /// <remarks>
    /// The guest resolves this same file, so it cannot hang off the standalone
    /// executable's directory: under the Godot host there is no standalone executable, and
    /// in a dev tree that search picked whichever build output it found first — writing
    /// settings into a config the running guest never reads.
    /// </remarks>
    public static string GetRuntimeConfigPath() =>
        Refii.Core.Engine_AppRoot.ConfigPath;

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

    /// <summary>
    /// Writes the title update beside the installed executable, from a .xexp or the package
    /// it came in, after checking that it patches that executable.
    /// </summary>
    /// <remarks>
    /// The check runs against the executable the ISO extraction just wrote, not the one the
    /// dialog looked at, so a path pasted by hand or an unattended run gets the same answer.
    /// </remarks>
    public static bool InstallTitleUpdate(string sourcePath, string destinationPath, out string error)
    {
        if (!SafePath.IsSafeDestinationUnderBase(destinationPath, GetInstallBasePath(), out error))
        {
            return false;
        }

        if (!TitleUpdate.TryLoad(sourcePath, out TitleUpdate update, out error))
        {
            return false;
        }

        string executablePath = Path.Combine(Path.GetDirectoryName(destinationPath) ?? string.Empty, "default.xex");
        if (!TitleUpdate.TryReadExecutable(executablePath, out XexIdentity executable, out string xexError))
        {
            error = "The extracted game executable could not be identified: " + xexError;
            return false;
        }

        if (!SupportedGame.IsExecutable(executable, out error) ||
            !SupportedGame.IsTitleUpdate(update.Identity, out error) ||
            !update.AppliesTo(executable, out error))
        {
            return false;
        }

        try
        {
            string? parent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent!);
            }

            File.WriteAllBytes(destinationPath, update.Patch);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Whether the installed executable and title update belong together, with a line to show.
    /// Cached briefly: the panels ask every refresh and the answer only changes on an install.
    /// </summary>
    public static bool CheckInstalledTitleUpdate(out string summary)
    {
        long now = Environment.TickCount64;
        if (_titleUpdateValid && now - _titleUpdateStamp < StepStatusTtlMs)
        {
            summary = _titleUpdateSummary;
            return _titleUpdateOk;
        }

        _titleUpdateOk = CheckInstalledTitleUpdateNow(out _titleUpdateSummary);
        _titleUpdateValid = true;
        _titleUpdateStamp = now;
        summary = _titleUpdateSummary;
        return _titleUpdateOk;
    }

    private static string _titleUpdateSummary = string.Empty;
    private static bool _titleUpdateOk;
    private static bool _titleUpdateValid;
    private static long _titleUpdateStamp;

    private static bool CheckInstalledTitleUpdateNow(out string summary)
    {
        string gameData = GetActiveGameDataDirectoryPath();
        string executablePath = Path.Combine(gameData, "default.xex");
        string patchPath = Path.Combine(gameData, "default.xexp");

        if (!File.Exists(executablePath))
        {
            summary = string.Empty;
            return true;
        }

        if (!TitleUpdate.TryReadExecutable(executablePath, out XexIdentity executable, out string xexError))
        {
            summary = "The installed executable could not be identified: " + xexError;
            return false;
        }

        if (!SupportedGame.IsExecutable(executable, out string unsupported))
        {
            summary = unsupported;
            return false;
        }

        if (!File.Exists(patchPath))
        {
            summary = "Game version " + executable.VersionText + ", media ID " +
                      executable.ShortMediaIdText + ", no title update installed.";
            return false;
        }

        if (!TitleUpdate.TryLoad(patchPath, out TitleUpdate update, out string patchError))
        {
            summary = "The installed title update is unusable: " + patchError;
            return false;
        }

        if (!SupportedGame.IsTitleUpdate(update.Identity, out string mismatch) ||
            !update.AppliesTo(executable, out mismatch))
        {
            summary = mismatch;
            return false;
        }

        summary = "Game version " + executable.VersionText + " with title update " +
                  update.Identity.VersionText + ", media ID " + executable.ShortMediaIdText + ".";
        return true;
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

    private const long ActiveGameDataTtlMs = 1000;
    private static string _activeGameData = string.Empty;
    private static bool _activeGameDataValid;
    private static long _activeGameDataStamp;

    public static string GetActiveGameDataDirectoryPath()
    {
        long now = Environment.TickCount64;
        if (_activeGameDataValid && now - _activeGameDataStamp < ActiveGameDataTtlMs)
        {
            return _activeGameData;
        }

        string resolved = ResolveGameDataDirectory();
        _activeGameData = resolved.Length > 0 ? resolved : GetGameDataDirectoryPath();
        _activeGameDataValid = true;
        _activeGameDataStamp = now;
        return _activeGameData;
    }

    public static string GetLuaWorkspacePath() =>
        Path.Combine(GetWorkspaceBasePath(GetActiveGameDataDirectoryPath()), "unpacked");

    /// <summary>
    /// The game executable the recompiler reads, inside the installed game data.
    /// </summary>
    public static string GetGameExecutablePath() =>
        Path.Combine(GetActiveGameDataDirectoryPath(), "default.xex");

    /// <summary>
    /// Where the generator writes the recompiled C++, beside the installed game data.
    /// </summary>
    /// <remarks>
    /// A SIBLING of <c>game</c> and <c>unpacked</c>, resolved the same way they are, because
    /// it is derived from the same install and has to follow it. Anchoring this on the
    /// native library directory instead put it under <c>build/&lt;arch&gt;/bin</c> while the
    /// installer wrote the game to <c>assets/game</c> — two different roots, so the build
    /// looked for an executable nothing had written. It also has to follow the install on a
    /// packaged Linux build, where the data root is under XDG rather than beside the app.
    ///
    /// Deleted once the module is built; nothing may be kept here.
    /// </remarks>
    public static string GetGeneratedCodeDirectoryPath() =>
        Path.Combine(GetWorkspaceBasePath(GetActiveGameDataDirectoryPath()), "generated");

    public readonly record struct ReinstallStepStatus(bool Banks, bool Shaders);

    private const long StepStatusTtlMs = 1000;
    private static ReinstallStepStatus _stepStatus;
    private static bool _stepStatusValid;
    private static long _stepStatusStamp;

    public static void InvalidateReinstallStepStatus()
    {
        _stepStatusValid = false;
        _activeGameDataValid = false;
        _titleUpdateValid = false;
    }

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
            File.Exists(Path.Combine(workspace, "shaders", "compile_report.tsv")));
        _stepStatusValid = true;
        _stepStatusStamp = now;
        return _stepStatus;
    }

    public static string GetGameContentDirectoryPath() =>
        Path.Combine(GetActiveGameDataDirectoryPath(), "data");

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

        string resolved = Refii.Core.Engine_GameData.Resolve();
        return HasBanks(resolved) ? resolved : string.Empty;
    }

    public static bool AssetsExist() => ResolveGameDataDirectory().Length > 0;

    /// <summary>
    /// Whether refii's own runtime is present — NOT whether the game is installed.
    /// </summary>
    /// <remarks>
    /// THE HOST BRIDGE IS THE RUNTIME. This used to test for a standalone <c>refii</c>
    /// executable beside the app, which no longer ships: Godot hosts the guest in its own
    /// process, so there is no second binary to find. An exported build therefore always
    /// failed the test and reported "Missing Runtime Files" while being perfectly intact.
    ///
    /// A false answer here means the APP is broken — a bad package, a missing native — and
    /// installing the game cannot fix it. Whether the player's game is installed is
    /// <see cref="AssetsExist"/>, and whether its code is built is
    /// <c>GameCodeBuild.NeedsBuild</c>.
    /// </remarks>
    public static bool InstalledBuildExists() =>
        File.Exists(Path.Combine(
            Refii.Core.Engine_AppRoot.BinDirectory,
            Refii.Core.Engine_NativeLibraries.FileNameFor("refii_hostbridge"))) ||
        File.Exists(GetInstalledExecutablePath());

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
