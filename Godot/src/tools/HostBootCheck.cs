using System;
using Godot;
using Refii.Host;
using Refii.Launcher.Formats;

namespace Refii.Launcher.Tools;

/// <summary>
/// Boots the recompiled guest INSIDE Godot's process and reports what happens.
///
/// The guest runs HEADLESS: the bridge sets <c>REX_HEADLESS=1</c> so it creates no
/// window, and <c>--renderer_backend=plume</c> so no graphics system is constructed at
/// all. Godot owns presentation; the guest simulates and never draws.
///
/// It still runs its own event loop on its own thread — Godot does not yet drive the
/// tick. That is the next step, and it is what "owned by Godot" finally means.
///
/// <b>Launching is opt-in.</b> Without <c>REFII_HOST_BOOT=1</c> this reports what it
/// WOULD do and exits. Booting the guest starts the game, and that is the user's call to
/// make, not a side effect of opening a scene.
/// </summary>
public partial class HostBootCheck : Node
{
	/// <summary>Seconds to watch the guest before reporting. It boots in the low seconds.</summary>
	[Export] public double WatchSeconds { get; set; } = 30.0;

	private double _elapsed;
	private bool _started;
	private string _dataRoot = string.Empty;

	public override void _Ready()
	{
		int? abi = HostBridge.TryGetAbiVersion();
		if (abi != HostBridge.ExpectedAbiVersion)
		{
			GD.Print($"host bridge ABI {abi?.ToString() ?? "unavailable"}, " +
				$"expected {HostBridge.ExpectedAbiVersion} — rebuild refii_hostbridge.");
			GetTree().Quit(1);
			return;
		}

		// The guest wants the directory holding `data`, which is what the UI tools
		// already resolve. One convention, not a second one for the host.
		_dataRoot = UiGameData.Resolve();
		if (_dataRoot.Length == 0)
		{
			GD.Print("no game data found; set REFII_GAME_DATA or stage assets/game.");
			GetTree().Quit(1);
			return;
		}

		GD.Print($"host bridge ABI {abi}");
		GD.Print($"game directory: {_dataRoot}");

		if (System.Environment.GetEnvironmentVariable("REFII_HOST_BOOT") != "1")
		{
			GD.Print("DRY RUN — set REFII_HOST_BOOT=1 to actually boot the guest in this process.");
			GD.Print("(booting starts the game; it runs headless — no window of its own)");
			GetTree().Quit(0);
			return;
		}

		HostBridge.ProbeResult start = HostBridge.Start(_dataRoot);
		GD.Print($"start: {start.Message}");
		if (!start.Success)
		{
			GetTree().Quit(1);
			return;
		}

		_started = true;
	}

	public override void _Process(double delta)
	{
		if (!_started)
			return;

		_elapsed += delta;

		if (!HostBridge.IsRunning)
		{
			HostBridge.ProbeResult joined = HostBridge.Join();
			GD.Print($"guest stopped after {_elapsed:F1}s: {joined.Message}");
			GD.Print($"exit code {HostBridge.ExitCode}");

			// The guest exiting on its own is a FAILURE for this check: we are testing
			// that it stays up inside Godot, so a clean early exit is still not what we
			// wanted to see.
			GD.Print("HOST BOOT FAILED — guest did not stay running");
			GetTree().Quit(1);
			return;
		}

		if (_elapsed >= WatchSeconds)
		{
			GD.Print($"guest still running after {_elapsed:F1}s: {HostBridge.Status}");
			GD.Print("HOST BOOT OK — the guest is alive inside Godot's process");

			// No clean external shutdown is wired through the ABI yet, so the guest goes
			// down with the process. Say so rather than implying a graceful stop.
			GD.Print("(no external shutdown yet — the guest exits with this process)");
			GetTree().Quit(0);
		}
	}
}
