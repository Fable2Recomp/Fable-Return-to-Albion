using System.IO;

namespace Refii.Launcher.Core;

public static class Installer
{
    public static string GetInstallBasePath() =>
        LauncherPaths.ResolveFromLauncherDirectory(Ini.GetString("GamePath", "assets/", "Game"));

    public static string GetInstalledExecutablePath() =>
        Path.Combine(Platform.GetExecutableDirectory(), Platform.ExecutableName);

    public static string GetRuntimeConfigPath() =>
        LauncherPaths.ResolveFromLauncherDirectory("refii.toml");

    public static string GetGameDataDirectoryName() =>
        Ini.GetString("GameDataDirectory", "game", "Game");

    public static string GetGameMarkerRelativePath() =>
        Ini.GetString("GameMarkerFile", "game/default.xexp", "Game");

    public static string GetGameMarkerPath() =>
        Path.Combine(GetInstallBasePath(), GetGameMarkerRelativePath());

    public static string GetGameTitle() => "Fable 2";

    public static string GetGameDataDirectoryPath() =>
        Path.Combine(GetInstallBasePath(), GetGameDataDirectoryName());

    public static string GetLuaWorkspacePath() =>
        Path.Combine(GetGameDataDirectoryPath(), "data", "scripts");

    public static string GetGameContentDirectoryPath() =>
        Path.Combine(GetGameDataDirectoryPath(), "data");

    public static bool EnsureRuntimeConfigFile(out string error) =>
        RuntimeConfig.EnsureDefaults(GetRuntimeConfigPath(), out error);

    public static bool HasExtractedAssets(string gameDataDirectory, string markerPath) =>
        Directory.Exists(gameDataDirectory) && File.Exists(markerPath);

    public static bool AssetsExist() =>
        HasExtractedAssets(GetGameDataDirectoryPath(), GetGameMarkerPath());

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
