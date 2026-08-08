using System;
using Godot;
using Refii.Assets;
using Refii.Host;
using Refii.Launcher.Formats;

namespace Refii.Game;

/// <summary>
/// One running game: the mounted assets, the hosted guest, and the world Godot builds.
///
/// This is where the plan's split lives. The guest simulates and never draws; Godot owns
/// the window, the frame loop and everything visual. So this node holds both halves —
/// it mounts the banks the world is built from, initialises the guest, and advances it
/// once per frame from <see cref="_Process"/>.
///
/// It is deliberately NOT the launcher. The launcher opens one of these when the user
/// presses Play; until then nothing here has run and no asset has been read.
/// </summary>
public partial class GameSession : Node
{
	/// <summary>Emitted once the guest is up and being driven.</summary>
	[Signal] public delegate void StartedEventHandler();

	/// <summary>Emitted when the guest stops, with the reason.</summary>
	[Signal] public delegate void StoppedEventHandler(string reason);

	/// <summary>Game directory override. Empty resolves by the shared convention.</summary>
	[Export(PropertyHint.GlobalDir)] public string GameDirectory { get; set; } = string.Empty;

	/// <summary>
	/// Boot the guest as well as mounting assets. Off gives the world-building half on
	/// its own, which is how the importer and the editor are worked on without a guest.
	/// </summary>
	[Export] public bool StartGuest { get; set; } = true;

	private bool _driving;
	private long _steps;

	public bool IsDriving => _driving;

	public override void _Ready()
	{
		string directory = Directory(GameDirectory);
		if (directory.Length == 0)
		{
			Fail("no game data found; install the game or set REFII_GAME_DATA.");
			return;
		}

		GD.Print($"[game] assets: {directory}");

		// Mount HERE, not in the launcher. The world is built from these, so the session
		// is the thing that needs them.
		Engine_GameAssets? assets = Engine_GameAssets.Instance;
		if (assets is null)
		{
			Fail("Engine_GameAssets autoload is not present.");
			return;
		}

		if (!assets.IsMounted && !assets.IsMounting)
		{
			assets.Mount(directory);
		}

		if (!StartGuest)
		{
			GD.Print("[game] StartGuest is off — assets only, no guest.");
			return;
		}

		int? abi = HostBridge.TryGetAbiVersion();
		if (abi != HostBridge.ExpectedAbiVersion)
		{
			Fail($"host bridge ABI {abi?.ToString() ?? "unavailable"}, " +
				$"expected {HostBridge.ExpectedAbiVersion} — rebuild refii_hostbridge.");
			return;
		}

		// Synchronous and stalls this frame by a second or two: it loads the XEX and runs
		// the guest's init. On the MAIN thread deliberately — SDL wants it, and running
		// the guest on a worker left that unresolved.
		GD.Print("[game] initialising guest...");
		HostBridge.ProbeResult init = HostBridge.Initialize(directory);
		GD.Print($"[game] {init.Message}");

		if (!init.Success)
		{
			Fail(init.Message);
			return;
		}

		_driving = true;
		EmitSignal(SignalName.Started);
	}

	public override void _Process(double delta)
	{
		if (!_driving)
			return;

		// One slice of the guest's loop per Godot frame. This is what "the guest is a
		// simulation server" means in practice.
		if (!HostBridge.Step())
		{
			_driving = false;
			string reason = HostBridge.HostedStatus;
			GD.Print($"[game] guest stopped after {_steps:N0} steps: {reason}");
			EmitSignal(SignalName.Stopped, reason);
			return;
		}

		_steps++;
	}

	public override void _ExitTree()
	{
		// Always. Leaving the guest's threads live at process exit aborts the process.
		if (HostBridge.TryGetAbiVersion() is not null)
		{
			GD.Print($"[game] shutdown: {HostBridge.Shutdown().Message}");
		}
	}

	/// <summary>The export wins; otherwise the one shared convention.</summary>
	private static string Directory(string configured) =>
		System.IO.Directory.Exists(configured) ? configured : UiGameData.Resolve();

	private void Fail(string reason)
	{
		GD.PrintErr($"[game] {reason}");
		EmitSignal(SignalName.Stopped, reason);
	}
}
