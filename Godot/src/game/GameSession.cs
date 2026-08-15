using System;
using Godot;
using Refii.Assets;
using Refii.Host;
using Refii.Core;
using Refii.Text;
using Refii.Textures;
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

		// BEFORE the mount, because the first texture request follows it immediately and a
		// backend registered after that point does not retroactively enhance what already
		// resolved. Off unless REFII_TEX_UPSCALE says otherwise.
		Engine_TexEnhance.RegisterDefault();

		// Stale epochs and the size budget, on a worker. Started here rather than after the
		// mount so it overlaps the bank walk instead of adding to it.
		Engine_TexCache.Maintain();

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

		int? abi = HostBridge.TryGetRawAbiVersion();
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

		// THE PUB GAMES WATCH THE GUEST, they are not launched from here. It publishes a state when
		// the player sits at a table and then waits for whoever draws the game; this node is that
		// somebody. Added alongside the input for the same reason — a failed boot must not leave
		// something polling a runtime that never came up.
		_pubGames = new GamePubGames { Name = "GamePubGames" };
		AddChild(_pubGames);

		_driving = true;
		EmitSignal(SignalName.Started);
	}

	private GamePubGames? _pubGames;

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

		// Enhanced textures that finished on a background worker get swapped in HERE, on the main
		// thread, and budgeted so a warm cache completing hundreds of entries at once does not
		// trade a background cost for a visible hitch. It is a no-op with no upscaler registered.
		Engine_TexLibrary.Pump();

		// Evidence on a good run, not only a bad one, and often enough that a kill or a crash
		// still leaves some. It prints only when the counters have moved.
		if (_steps % 600 == 0)
		{
			Engine_TexLibrary.ReportIfChanged();
			Refii.Models.Engine_MdlMaterial.ReportIfChanged();
		}

		// AFTER the step, so the state read is the one this frame's guest tick produced rather than
		// the previous frame's. The pub-game manager is written by that tick.
		_pubGames?.Poll();

		TraceCamera();
	}

	/// <summary>
	/// Logs the guest camera when it MOVES.
	///
	/// The question this was added for — whether the snapshot at 0x8331A230 still updates
	/// when the guest never draws — is SETTLED, and the answer is yes. It is a pull cache
	/// refreshed by sub_82211598 only when the sim generation counter dword_83319650 has
	/// moved, and all ten callers are Lua natives ("IsPositionInCameraFrustrum",
	/// "IsSphereInCameraFrustrum", "IsEntityInCameraFrustrumWithMultiplier"), AI path gates
	/// and effect ticks. Not one of them contains a device call, so none of them needs the
	/// render path to run.
	///
	/// It is still a copy of a copy, and a laggy one: sub_82211598 reads it through
	/// sub_821DD3B0 -> sub_82265F90, whose own memo at cameraManager+0xD0 is stamped with
	/// KeTickCount and so refreshes at most once a millisecond. The camera manager's PUSHED
	/// block is the better source — cameraManager+0xE0 position, +0xF0 up, +0x100 forward,
	/// +0x110 fov, +0x114 bool, written by sub_821B8FD8 (the stvx128 at 0x821B917C, then
	/// sub_822BDD10 for the two basis vectors) and driven with force=1 once per sim tick
	/// from sub_8233B6B0. Reaching it needs the manager pointer, which is a pure memory
	/// walk: Game = *(0x83496AB8+12), then a binary search of the 12-byte entries between
	/// *(Game+0x9C)+8 and +12 for key 1 (key 2 when *(Game+0x8C)+0x34 and +0x36 say split
	/// screen), taking *(entry+4) and then *(view+4).
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
		Engine_TexLibrary.Report();
		Refii.Models.Engine_MdlMaterial.ReportIfChanged();

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
