using System.Collections.Generic;
using Refii.Core;

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

    /// <summary>The title owned by the currently running child game process, or null when no game is running.</summary>
    public Engine_Title? RunningTitle => GameIsRunning ? _runningTitle : null;
    private Engine_Title? _runningTitle;

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
    public void AdoptGameProcess(ProcessHandle handle, Engine_Title? title = null)
    {
        GameProcess = handle;
        _runningTitle = title ?? Engine_Title.Active;
    }

    public LaunchResult Launch() => Launch(Engine_Title.Active);

    /// <summary>Launches <paramref name="title"/> using the standalone runtime executable.</summary>
    public LaunchResult Launch(Engine_Title title)
    {
        if (!Installer.EnsureRuntimeConfigFile(title, out string configError))
        {
            return new LaunchResult(false, "Failed to prepare runtime config. " + configError);
        }

        string executablePath = Installer.GetInstalledExecutablePath();
        List<string> arguments = _patches.GetPatchLaunchArguments();

        // The game's Godot log, in the title's own logs folder beside its guest logs
        // (logs/Fable2/godot.log, logs/Fable3/godot.log): the engine flag goes first, ahead of any
        // `--` the patch arguments carry.
        string logs = title.LogsDirectory;
        try { System.IO.Directory.CreateDirectory(logs); } catch (System.Exception) { }
        arguments.Insert(0, System.IO.Path.Combine(logs, "godot.log"));
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
        _runningTitle = title;
        return new LaunchResult(true, "Game launched successfully.", handle);
    }
}
