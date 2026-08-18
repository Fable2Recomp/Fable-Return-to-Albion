using System;
using System.IO;

namespace Refii.Launcher.Core;

public static class LauncherPaths
{
    public static string? OverrideLauncherDirectory { get; set; }

    /// <summary>
    /// The app root — see <see cref="Refii.Core.Engine_AppRoot"/>.
    /// </summary>
    /// <remarks>
    /// NOT <c>AppContext.BaseDirectory</c>, which is where the managed assemblies live: in
    /// the editor that is <c>.godot/mono/temp/bin/Debug</c>, so the launcher wrote the
    /// runtime config, the install and the save data into a build-intermediate directory
    /// that the guest never reads and a clean rebuild deletes.
    /// </remarks>
    public static string GetLauncherDirectory()
    {
        if (!string.IsNullOrEmpty(OverrideLauncherDirectory))
        {
            return OverrideLauncherDirectory!;
        }

        return Refii.Core.Engine_AppRoot.Path;
    }

    public static string GetBundledLauncherDataDirectory() =>
        Path.Combine(GetLauncherDirectory(), "assets", "launcher");

    public static bool UseLinuxUserDataRoot()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        foreach (string variable in new[] { "APPIMAGE", "APPDIR", "FLATPAK_ID" })
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(value))
            {
                return true;
            }
        }

        return false;
    }

    public static string GetRuntimeDataRootDirectory()
    {
        if (OperatingSystem.IsLinux() && UseLinuxUserDataRoot())
        {
            string? xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrEmpty(xdgDataHome))
            {
                return Normalize(Path.Combine(xdgDataHome!, "refii"));
            }

            string? home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                return Normalize(Path.Combine(home!, ".local", "share", "refii"));
            }
        }

        return GetLauncherDirectory();
    }

    public static string GetLauncherDataDirectory() =>
        Path.Combine(GetRuntimeDataRootDirectory(), "assets", "launcher");

    public static string ResolveFromLauncherDirectory(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return GetRuntimeDataRootDirectory();
        }

        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Normalize(Path.Combine(GetRuntimeDataRootDirectory(), path));
    }

    public static string GetLauncherAssetDirectory()
    {
        string runtimeAssets = Normalize(Path.Combine(GetLauncherDataDirectory(), "assets"));
        string bundledAssets = Normalize(Path.Combine(GetBundledLauncherDataDirectory(), "assets"));

        if (runtimeAssets != bundledAssets && Directory.Exists(runtimeAssets))
        {
            return runtimeAssets;
        }

        return bundledAssets;
    }

    public static string GetLauncherAssetPath(string relativePath) =>
        Normalize(Path.Combine(GetLauncherAssetDirectory(), relativePath));

    public static string GetConfigPath() =>
        Path.Combine(GetLauncherDataDirectory(), "config.cfg");

    public static string GetLauncherLogPath() =>
        Path.Combine(GetRuntimeDataRootDirectory(), "logs", "refii-launcher.log");

    internal static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
}
