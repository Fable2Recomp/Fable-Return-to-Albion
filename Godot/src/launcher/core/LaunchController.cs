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

    /// <summary>
    /// Takes ownership of a game process started elsewhere — the child spawned by Play. Keeps
    /// `GameIsRunning` as the one answer to "is a game up", which the unattended driver and the
    /// launcher's own restore both key on.
    /// </summary>
    public void AdoptGameProcess(ProcessHandle handle) => GameProcess = handle;

    public LaunchResult Launch()
    {
        if (!Installer.EnsureRuntimeConfigFile(out string configError))
        {
            return new LaunchResult(false, "Failed to prepare runtime config. " + configError);
        }

        string executablePath = Installer.GetInstalledExecutablePath();
        List<string> arguments = _patches.GetPatchLaunchArguments();

        // The game's Godot log, per title and beside its guest logs (logs/godot_refii.log,
        // logs/godot_refiii.log): the engine flag goes first, ahead of any `--` the patch
        // arguments carry.
        string logs = System.IO.Path.Combine(Installer.GetInstallBasePath(), "logs");
        try { System.IO.Directory.CreateDirectory(logs); } catch (System.Exception) { }
        arguments.Insert(0, System.IO.Path.Combine(logs, $"godot_{Refii.Core.Engine_Title.Active.Key}.log"));
        arguments.Insert(0, "--log-file");

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
