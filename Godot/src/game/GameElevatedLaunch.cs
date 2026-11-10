using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace Refii.Game;

/// <summary>
/// The game as Administrator, for Tracy's advanced data on Windows.
/// </summary>
/// <remarks>
/// Sampling with call stacks and context switches come from Windows' kernel event tracing, which
/// only an elevated process may start. The launcher itself stays unelevated; Play asks first.
///
/// AN ELEVATED PROCESS DOES NOT INHERIT ITS PARENT'S ENVIRONMENT, and the game child is defined by
/// its environment: the child marker, the .NET GC budget, RenderDoc's layer. So elevation goes
/// through a relay — this executable, headless, started with `runas` and a file naming the real
/// launch. The relay is elevated; it starts the game child with the launcher's environment (the
/// child inherits the elevation), waits for it and exits with its code, so the launcher watches the
/// relay exactly as it would the game.
/// </remarks>
public static class GameElevatedLaunch
{
	private const string RelayArgument = "--refii-elevated-relay";

	/// <summary>ERROR_CANCELLED: the permission prompt was declined.</summary>
	private const int ErrorCancelled = 1223;

	private sealed record Request(
		string Executable,
		string WorkingDirectory,
		List<string> Arguments,
		Dictionary<string, string> Environment);

	/// <summary>
	/// Play has to ask: Windows, Tracy's advanced data wanted, and the launcher not already
	/// elevated (an elevated launcher's child is elevated anyway).
	/// </summary>
	public static bool Wanted(IReadOnlyDictionary<string, string> config) =>
		OperatingSystem.IsWindows()
		&& Refii.Core.Engine_RefiiConfig.Bool(config, "tracy", false)
		&& Refii.Core.Engine_RefiiConfig.Bool(config, "tracy_advanced", false)
		&& !IsAdministrator();

	/// <summary>True in the relay process: started by <see cref="Launch"/>, not by a player.</summary>
	public static bool IsRelay => RelayFile is not null;

	private static readonly string? RelayFile = FindRelayFile();

	private static string? FindRelayFile()
	{
		string[] args = OS.GetCmdlineUserArgs();
		int at = Array.IndexOf(args, RelayArgument);
		return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
	}

	public static bool IsAdministrator()
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
		return new System.Security.Principal.WindowsPrincipal(identity)
			.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
	}

	/// <summary>
	/// Starts the relay elevated, which starts the game as <paramref name="executable"/> with
	/// <paramref name="arguments"/> and the launcher's environment plus
	/// <paramref name="environment"/>. The handle is the relay's; it exits with the game.
	/// </summary>
	public static bool Launch(
		string executable,
		IReadOnlyList<string> arguments,
		IReadOnlyDictionary<string, string> environment,
		out Refii.Launcher.Core.ProcessHandle handle,
		out string error)
	{
		handle = new Refii.Launcher.Core.ProcessHandle();
		error = string.Empty;

		var inherited = new Dictionary<string, string>();
		foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
		{
			inherited[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
		}

		foreach ((string key, string value) in environment)
		{
			inherited[key] = value;
		}

		string workingDirectory = Directory.GetCurrentDirectory();
		var request = new Request(executable, workingDirectory, arguments.ToList(), inherited);
		string file = ProjectSettings.GlobalizePath("user://elevated-launch.json");

		try
		{
			File.WriteAllText(file, JsonSerializer.Serialize(request));

			var startInfo = new ProcessStartInfo(executable)
			{
				UseShellExecute = true,
				Verb = "runas",
				WorkingDirectory = workingDirectory,
			};
			startInfo.ArgumentList.Add("--headless");
			if (OS.HasFeature("editor"))
			{
				startInfo.ArgumentList.Add("--path");
				startInfo.ArgumentList.Add(ProjectSettings.GlobalizePath("res://"));
			}

			startInfo.ArgumentList.Add("--");
			startInfo.ArgumentList.Add(RelayArgument);
			startInfo.ArgumentList.Add(file);

			Process? relay = Process.Start(startInfo);
			if (relay is null)
			{
				File.Delete(file);
				error = "the Administrator launch could not be started.";
				return false;
			}

			handle = new Refii.Launcher.Core.ProcessHandle { Process = relay, ProcessId = relay.Id };
			return true;
		}
		catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
		{
			TryDelete(file);
			error = "Windows was not given Administrator permission, so the game was not started. " +
				"Press Play again and choose to run without the advanced data, or turn tracy_advanced off.";
			return false;
		}
		catch (Exception exception)
		{
			TryDelete(file);
			error = "the Administrator launch failed: " + exception.Message;
			return false;
		}
	}

	/// <summary>The relay process's whole job; quits the tree with the game's exit code.</summary>
	public static void RunRelay(Node host)
	{
		SceneTree tree = host.GetTree();
		Request? request = null;
		try
		{
			request = JsonSerializer.Deserialize<Request>(File.ReadAllText(RelayFile!));
		}
		catch (Exception exception)
		{
			GD.PrintErr($"[launch] elevated relay: cannot read {RelayFile}: {exception.Message}");
		}
		finally
		{
			TryDelete(RelayFile!);
		}

		if (request is null)
		{
			tree.Quit(1);
			return;
		}

		if (!Refii.Launcher.Core.Platform.LaunchProcess(
				request.WorkingDirectory,
				request.Executable,
				request.Arguments,
				request.Environment,
				out Refii.Launcher.Core.ProcessHandle game,
				out string error))
		{
			GD.PrintErr("[launch] elevated relay: " + error);
			tree.Quit(1);
			return;
		}

		GD.Print($"[launch] elevated relay: started the game as Administrator (pid {game.ProcessId})");
		Engine.MaxFps = 2;
		TrimProcessWorkingSet();
		Process process = game.Process!;
		process.WaitForExitAsync().ContinueWith(_ =>
			Callable.From(() => tree.Quit(process.ExitCode)).CallDeferred());
	}

	[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool K32EmptyWorkingSet(IntPtr hProcess);

	public static void TrimProcessWorkingSet()
	{
		try
		{
			System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
				System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
			GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
			if (OperatingSystem.IsWindows())
			{
				using var current = Process.GetCurrentProcess();
				K32EmptyWorkingSet(current.Handle);
			}
		}
		catch (Exception)
		{
		}
	}

	private static void TryDelete(string file)
	{
		try
		{
			File.Delete(file);
		}
		catch (Exception)
		{
		}
	}
}
