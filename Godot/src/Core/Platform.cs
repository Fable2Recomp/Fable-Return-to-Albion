using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Refii.Launcher.Core;

public sealed class ProcessHandle
{
    public Process? Process { get; init; }
    public int ProcessId { get; init; }
    public bool IsRunning => Process is { HasExited: false };
    public int? ExitCode => Process is { HasExited: true } process ? process.ExitCode : null;
}

public static class Platform
{
    public const string ExecutableName = "refii.exe";
    public const string DevelopmentExecutableRelativePath =
        "out/build/win-amd64-relwithdebinfo/refii.exe";

    public static string GetExecutableDirectory() => LauncherPaths.GetLauncherDirectory();

    public static void TryOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }

    public static bool LaunchProcess(
        string workingDirectory,
        string executablePath,
        IReadOnlyList<string> arguments,
        out ProcessHandle handle,
        out string error)
    {
        handle = new ProcessHandle();
        error = string.Empty;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
            };

            if (!string.IsNullOrEmpty(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process? process = Process.Start(startInfo);
            if (process is null)
            {
                error = "CreateProcess failed: the process could not be started.";
                return false;
            }

            handle = new ProcessHandle { Process = process, ProcessId = process.Id };
            return true;
        }
        catch (Exception exception)
        {
            error = "CreateProcess failed: " + exception.Message;
            return false;
        }
    }
}
