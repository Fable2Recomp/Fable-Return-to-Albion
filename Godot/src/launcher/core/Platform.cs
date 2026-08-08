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
	/// <summary>
	/// The guest runtime's file name on THIS platform.
	///
	/// It was a hardcoded <c>refii.exe</c>, so every "is the runtime installed" check
	/// failed on Linux no matter what was present — the launcher reported "Missing
	/// Runtime Files" while happily mounting 123,710 game files beside it, which is the
	/// contradiction that gave this away.
	/// </summary>
	public static string ExecutableName =>
		OperatingSystem.IsWindows() ? "refii.exe" : "refii";

	/// <summary>
	/// Where a dev build leaves the runtime, relative to the repository root. The build
	/// config is part of the path and differs per platform, so this cannot be one
	/// constant either.
	/// </summary>
	public static string DevelopmentExecutableRelativePath =>
		OperatingSystem.IsWindows()
			? "out/build/win-amd64-relwithdebinfo/refii.exe"
			: "out/build/linux-amd64-debug/bin/refii";

	public static string? RuntimeDirectoryOverride { get; set; }

	public static string GetExecutableDirectory() =>
		string.IsNullOrEmpty(RuntimeDirectoryOverride)
			? LauncherPaths.GetLauncherDirectory()
			: RuntimeDirectoryOverride!;

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
