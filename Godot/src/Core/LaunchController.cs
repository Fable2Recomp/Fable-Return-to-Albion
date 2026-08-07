using System.Collections.Generic;

namespace Refii.Launcher.Core;

public enum PrimaryAction
{
    Install,
    DownloadBuild,
    RuntimeMissing,
    Launch,
}

public sealed record LaunchResult(bool Success, string Message, ProcessHandle? Handle = null);

public sealed class LaunchController
{
    private readonly Patches _patches;

    public LaunchController(Patches patches) => _patches = patches;

    public ProcessHandle? GameProcess { get; private set; }
    public bool GameIsRunning => GameProcess is { IsRunning: true };

    public static PrimaryAction ResolveAction(
        bool assetsExist,
        bool canDownloadBuild,
        bool installedBuildExists,
        bool updateAvailable)
    {
        if (!assetsExist)
        {
            return PrimaryAction.Install;
        }

        if (canDownloadBuild && (!installedBuildExists || updateAvailable))
        {
            return PrimaryAction.DownloadBuild;
        }

        if (!installedBuildExists)
        {
            return PrimaryAction.RuntimeMissing;
        }

        return PrimaryAction.Launch;
    }

    public LaunchResult Launch()
    {
        if (!Installer.EnsureRuntimeConfigFile(out string configError))
        {
            return new LaunchResult(false, "Failed to prepare runtime config. " + configError);
        }

        string executablePath = Installer.GetInstalledExecutablePath();
        List<string> arguments = _patches.GetPatchLaunchArguments();

        if (!Platform.LaunchProcess(
                Installer.GetInstallBasePath(),
                executablePath,
                arguments,
                out ProcessHandle handle,
                out string launchError))
        {
            return new LaunchResult(false, "Failed to launch game. " + launchError);
        }

        GameProcess = handle;
        return new LaunchResult(true, "Game launched successfully.", handle);
    }
}
