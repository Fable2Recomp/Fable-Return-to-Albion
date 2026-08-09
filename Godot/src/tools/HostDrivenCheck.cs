using Godot;
using Refii.Host;
using Refii.Core;
using Refii.Text;
using Refii.Ui;

namespace Refii.Launcher.Tools;

/// <summary>
/// Runs the guest with GODOT DRIVING IT — the plan's "Refii becomes a library".
///
/// The difference from <see cref="HostBootCheck"/> is who owns the thread. There, the
/// guest was handed one and ran its own blocking SDL loop inside it; Godot merely
/// watched. Here Godot initialises the guest once and then advances it a slice per
/// frame from <c>_Process</c>, which is what makes the guest a simulation server rather
/// than an app that happens to live in the same process.
///
/// Initialisation is SYNCHRONOUS and stalls the first frame by seconds — it loads the
/// XEX and runs the guest's init. That is deliberate: SDL wants the main thread, and
/// running the guest on a worker left that question open.
///
/// <b>Opt-in.</b> Without <c>REFII_HOST_BOOT=1</c> this reports and exits.
/// </summary>
public partial class HostDrivenCheck : Node
{
	/// <summary>Seconds to drive the guest before reporting.</summary>
	[Export] public double WatchSeconds { get; set; } = 30.0;

	private double _elapsed;
	private bool _driving;
	private long _steps;

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

		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0)
		{
			GD.Print("no game data found; set REFII_GAME_DATA or stage assets/game.");
			GetTree().Quit(1);
			return;
		}

		GD.Print($"host bridge ABI {abi}");
		GD.Print($"game directory: {dataRoot}");

		if (System.Environment.GetEnvironmentVariable("REFII_HOST_BOOT") != "1")
		{
			GD.Print("DRY RUN — set REFII_HOST_BOOT=1 to initialise and drive the guest.");
			GetTree().Quit(0);
			return;
		}

		GD.Print("initialising (this blocks for a few seconds)...");
		HostBridge.ProbeResult init = HostBridge.Initialize(dataRoot);
		GD.Print($"initialize: {init.Message}");
		if (!init.Success)
		{
			GetTree().Quit(1);
			return;
		}

		_driving = true;
	}

	public override void _Process(double delta)
	{
		if (!_driving)
			return;

		_elapsed += delta;

		// One slice of the guest's loop, from Godot's frame. This is the whole point.
		if (!HostBridge.Step())
		{
			GD.Print($"guest stopped after {_elapsed:F1}s and {_steps:N0} steps: " +
				HostBridge.HostedStatus);
			GD.Print("HOST DRIVEN FAILED — guest stopped on its own");
			_driving = false;
			GetTree().Quit(1);
			return;
		}

		_steps++;

		if (_elapsed >= WatchSeconds)
		{
			GD.Print($"drove the guest for {_elapsed:F1}s across {_steps:N0} steps " +
				$"({_steps / _elapsed:F0} steps/s): {HostBridge.HostedStatus}");
			GD.Print("HOST DRIVEN OK — Godot owns the loop and the guest is alive");
			_driving = false;
			GetTree().Quit(0);
		}
	}

	public override void _ExitTree()
	{
		// Always, even on the failure paths: leaving the guest's threads live at process
		// exit is what produced the abort on teardown.
		if (HostBridge.TryGetAbiVersion() is not null)
		{
			GD.Print($"shutdown: {HostBridge.Shutdown().Message}");
		}
	}
}
