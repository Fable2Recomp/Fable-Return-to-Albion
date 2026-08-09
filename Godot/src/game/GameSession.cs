using System;
using Godot;
using Refii.Assets;
using Refii.Host;
using Refii.Core;
using Refii.Text;
using Refii.Ui;

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

		// Input starts only once the guest is actually running, so a failed boot does not
		// leave a node publishing controller frames at a runtime that never came up.
		AddChild(new GameInput { Name = "GameInput" });

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
		TraceCamera();
	}

	/// <summary>
	/// Logs the guest camera when it MOVES, to answer the one thing IDA could not settle:
	/// whether the snapshot at 0x8331A230 still updates when the guest never draws.
	///
	/// It is a pull cache — refreshed only when guest code calls sub_82211598, and every
	/// caller is a culling or visibility query. If none of them run headless it freezes at
	/// its last value with no error and no timestamp, and the only way to tell is to watch
	/// it. If it never moves, the push-driven copy on the camera manager (+0xE0) is the
	/// fallback.
	/// </summary>
	private void TraceCamera()
	{
		if (HostBridge.GetCamera() is not { } camera)
		{
			return;
		}

		bool moved = _lastCamera is not { } previous ||
			previous.Position.DistanceTo(camera.Position) > 0.01f ||
			previous.Forward.DistanceTo(camera.Forward) > 0.001f;
		if (!moved)
		{
			return;
		}

		_lastCamera = camera;
		if (++_cameraReports <= 20)
		{
			GD.Print($"[guest-camera] pos={camera.Position} fwd={camera.Forward} " +
				$"up={camera.Up} vfov={camera.VerticalFovDegrees:F1}deg " +
				$"{(camera.IsDefault ? "DEFAULT (no live camera)" : "live")}");
		}
	}

	private HostBridge.GuestCamera? _lastCamera;
	private int _cameraReports;

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
		System.IO.Directory.Exists(configured) ? configured : Engine_GameData.Resolve();

	private void Fail(string reason)
	{
		GD.PrintErr($"[game] {reason}");
		EmitSignal(SignalName.Stopped, reason);
	}
}
