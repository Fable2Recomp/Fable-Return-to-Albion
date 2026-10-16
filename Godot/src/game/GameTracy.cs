using System.Collections.Generic;
using Godot;

namespace Refii.Game;

/// <summary>
/// The game's own Tracy profiler: whether it runs in this process, and whether its captures ask
/// for the advanced data (sampling with call stacks, context switches).
/// </summary>
/// <remarks>
/// RESTART ONLY. `tracy` and `tracy_advanced` are read once, when the session starts; nothing
/// changes them in a running game. And GAME ONLY: the launcher never starts Tracy. On the desktop
/// the game is the child process, so this does nothing anywhere else; Android has no child — its
/// game host runs in the launcher's process and calls this when the game starts. Off, the client
/// is never started and every zone, plot and capture call is inert.
///
/// Captures are written to user://captures: by the dev menu's Save, and by a crash — the native
/// crash handlers have the Tracy library save a live capture there (bounded), and a managed crash
/// does the same here.
/// </remarks>
public static class GameTracy
{
	public static bool Enabled { get; private set; }

	public static bool Advanced { get; private set; }

	public static string CapturesDirectory => ProjectSettings.GlobalizePath("user://captures");

	public static void Start(IReadOnlyDictionary<string, string> config)
	{
		bool gameProcess = GameChildProcess.IsChild || System.OperatingSystem.IsAndroid();
		if (Enabled || !gameProcess || !Refii.Core.Engine_RefiiConfig.Bool(config, "tracy", false))
		{
			return;
		}

		Enabled = Refii.Host.HostBridge.TraceStartup();
		Advanced = Enabled && Refii.Core.Engine_RefiiConfig.Bool(config, "tracy_advanced", false);
		if (!Enabled)
		{
			return;
		}

		System.IO.Directory.CreateDirectory(CapturesDirectory);
		Refii.Host.HostTracy.SetCrashDirectory(CapturesDirectory);
		System.AppDomain.CurrentDomain.UnhandledException += (_, _) => SaveCrashCapture();
	}

	/// <summary>A new capture file's path: `prefix-date-time.tracy` in the captures folder.</summary>
	public static string NewCapturePath(string prefix) =>
		System.IO.Path.Combine(CapturesDirectory, $"{prefix}-{System.DateTime.Now:yyyyMMdd-HHmmss}.tracy");

	/// <summary>A managed crash on its way out: stop and keep whatever was being captured.</summary>
	private static void SaveCrashCapture()
	{
		if (Refii.Host.HostTracy.GetStatus().Capture is not Refii.Host.HostTracy.CaptureState.Live)
		{
			return;
		}

		Refii.Host.HostTracy.Stop();
		string path = NewCapturePath("crash");
		GD.PrintErr(Refii.Host.HostTracy.Save(path)
			? $"[tracy] the live capture was saved for the crash: {path}"
			: "[tracy] the live capture could not be saved for the crash");
	}
}
