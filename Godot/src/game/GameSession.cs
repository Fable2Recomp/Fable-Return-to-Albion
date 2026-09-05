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
/// One running game: mounts the assets, boots the guest, and steps it from <see cref="_Process"/>.
/// The guest simulates and never draws; Godot owns the window and the frame loop. Opened by the
/// launcher on Play, never before.
/// </summary>
public partial class GameSession : Node
{
	/// <summary>Emitted once the guest is up and being driven.</summary>
	[Signal] public delegate void StartedEventHandler();

	/// <summary>Emitted when the guest stops, with the reason.</summary>
	[Signal] public delegate void StoppedEventHandler(string reason);

	/// <summary>Game directory override. Empty resolves by the shared convention.</summary>
	[Export(PropertyHint.GlobalDir)] public string GameDirectory { get; set; } = string.Empty;

	/// <summary>Boot the guest as well as mounting assets; off gives the world half alone.</summary>
	[Export] public bool StartGuest { get; set; } = true;

	private bool _driving;
	private long _steps;

	public bool IsDriving => _driving;

	/// <summary>
	/// The running session, so a host surface can reach the simulation clock (static: the UI is
	/// mounted under the window, not the session). Null before start and after leaving the tree.
	/// </summary>
	public static GameSession? Current { get; private set; }

	private bool _booted;
	private string _bootDirectory = string.Empty;

	public override void _Ready()
	{
		Current = this;

		// THE DEBUG CHANNEL COMES UP FIRST, BEFORE ANYTHING SLOW.
		//
		// It was attached after a successful guest boot, which is precisely when it is least
		// needed: everything before that point — the texture sweep, the shader warm-up, the
		// asset mount, the guest init itself — is a long silent phase with no diagnostic but a
		// CPU counter. On the run that proved it, the game spent fifteen minutes rebuilding an
		// invalidated texture cache while I measured thread CPU and reasoned about whether it
		// had hung; the screen said "Enhancing textures" the whole time and nothing could ask
		// it. A channel that only works once everything works is not a debug channel.
		//
		// It needs no guest: `shot` photographs the window whatever is on it, and `state`
		// reports the bridge as down rather than failing.
		if (GameDebugServer.Wanted)
		{
			AddChild(new GameDebugServer { Name = "GameDebugServer" });
		}

		string directory = Directory(GameDirectory);
		if (directory.Length == 0)
		{
			Fail("no game data found; install the game or set REFII_GAME_DATA.");
			return;
		}

		GD.Print($"[game] assets: {directory}");

		// Before the mount: a backend registered later does not enhance what already resolved.
		// Off unless REFII_TEX_UPSCALE says otherwise.
		Engine_TexEnhance.RegisterDefault();

		// Stale epochs and the size budget, on a worker; overlaps the bank walk.
		Engine_TexCache.Maintain();

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

		// THE GUEST WAITS FOR THE SHADER WARM-UP. Pipeline compilation happens on the main
		// thread the first time a material draws, and the step pump lives on that thread —
		// letting the storm land while the guest is mid-boot is how a shader edit became five
		// straight failed launches. The warm-up screen renders every shader first, overlapping
		// the asset mount's worker; _Process starts the boot once both are done.
		_bootDirectory = directory;
		Refii.Models.Engine_ShaderWarmup.Begin(this);
	}

	private void Boot()
	{
		string directory = _bootDirectory;
		Engine_GameAssets? assets = Engine_GameAssets.Instance;
		if (assets is null)
		{
			return;
		}

		// The mount must finish before the guest starts, or its first asset lookups miss.
		// `Mount`'s worker publishes under the mount lock, so blocking here without pumping a
		// frame is safe.
		if (assets.IsMounting)
		{
			GD.Print("[game] waiting for the asset mount before starting the guest...");
			var waited = System.Diagnostics.Stopwatch.StartNew();
			while (assets.IsMounting && waited.Elapsed < System.TimeSpan.FromSeconds(120))
			{
				System.Threading.Thread.Sleep(10);
			}

			GD.Print(assets.IsMounted
				? $"[game] assets mounted in {waited.ElapsedMilliseconds} ms — starting the guest"
				: $"[game] the mount did not finish in {waited.ElapsedMilliseconds} ms; " +
				  "starting the guest anyway, and its early asset lookups will miss");
		}

		int? abi = HostBridge.TryGetRawAbiVersion();
		if (abi != HostBridge.ExpectedAbiVersion)
		{
			Fail($"host bridge ABI {abi?.ToString() ?? "unavailable"}, " +
				$"expected {HostBridge.ExpectedAbiVersion} — rebuild refii_hostbridge.");
			return;
		}

		// Synchronous (loads the XEX, runs guest init) and on the main thread: SDL requires it.
		GD.Print("[game] initialising guest...");
		HostBridge.ProbeResult init = HostBridge.Initialize(directory);
		GD.Print($"[game] {init.Message}");

		if (!init.Success)
		{
			Fail(init.Message);
			return;
		}

		// Only after a successful boot: a failed init leaves the UI on the authored stand-in.
		Refii.Host.HostUiSource.Install();
		GD.Print("[game] the UI reads the live guest now (Engine_UiSource -> HostUiSource)");

		// Input starts only once the guest is running.
		AddChild(new GameInput { Name = "GameInput" });

		// The pub games watch the guest's published table state; they are not launched from here.
		_pubGames = new GamePubGames { Name = "GamePubGames" };
		AddChild(_pubGames);

		_driving = true;
		EmitSignal(SignalName.Started);
	}

	private GamePubGames? _pubGames;

	/// <summary>
	/// The guest's simulation rate in steps per second: bone poses at 30 Hz, game tick at 15.
	/// Independent of the display rate.
	/// </summary>
	public const double StepsPerSecond = 30.0;

	/// <summary>
	/// Catch-up ceiling: a stall loses time instead of replaying every missed step at once.
	/// </summary>
	private const int MaxCatchUpSteps = 4;

	/// <summary>
	/// Halts the simulation without stopping it: the UI still draws and the bridge still reads.
	/// </summary>
	public bool Paused { get; set; }

	private double _accumulated;

	/// <summary>Set once the ORM sweep's worker has been started.</summary>
	private bool _ormStarted;

	/// <summary>Written by the sweep's worker thread when it finishes; read from _Process.</summary>
	private volatile bool _ormDone;

	/// <summary>The sweep's latest status line. Reference assignment, so no lock.</summary>
	private volatile string _ormStatus = "Preparing materials...";

	public override void _Process(double delta)
	{
		using (Refii.Core.Engine_FramePhases.Measure("session_process"))
		{
			session_processBody(delta);
		}
	}

	private void session_processBody(double delta)
	{
		if (!_booted)
		{
			if (_bootDirectory.Length == 0 || !Refii.Models.Engine_ShaderWarmup.Done)
			{
				return;
			}

			// THE ORM SWEEP RUNS BEHIND THE WARM-UP SCREEN, NOT DURING PLAY. Built on demand,
			// an ORM is a Poisson solve on a streaming worker, and a cache-version bump makes a
			// region's first load pay hundreds of them in one burst — which looks like the game
			// freezing. Behind this screen the same cost is a progress line; on a warm cache the
			// sweep is enumerate-and-hit and adds seconds.
			if (!_ormStarted)
			{
				_ormStarted = true;
				string directory = _bootDirectory;
				var worker = new System.Threading.Thread(() =>
				{
					try
					{
						Refii.Textures.Engine_TexPrebuild.Progress result =
							Refii.Textures.Engine_TexPrebuild.RunOrm(directory, progress =>
								_ormStatus = progress.Total == 0
									? "Preparing materials..."
									: $"Preparing materials... {progress.Done:N0} of " +
									  $"{progress.Total:N0}");
						GD.Print($"[game] orm sweep: {result.Built} built, " +
							$"{result.Failed} failed of {result.Total}");

						// THE UPSCALE SWEEPS, when `texture_upscaling_when` says boot: every
						// material texture, then every terrain layer, into the shared cache
						// behind this same screen — so play never pays an enhancement and the
						// terrain's synchronous build is enumerate-and-hit. "play" defers both
						// to their live paths; "off" makes both no-ops.
						if (Refii.Textures.Engine_TexSettings.UpscaleWhen == "boot")
						{
							Refii.Textures.Engine_TexPrebuild.Progress sharp =
								Refii.Textures.Engine_TexPrebuild.RunTextures(directory, progress =>
									_ormStatus = progress.Total == 0
										? "Enhancing textures..."
										: $"Enhancing textures... {progress.Done:N0} of " +
										  $"{progress.Total:N0}");
							GD.Print($"[game] texture sweep: {sharp.Built} prepared, " +
								$"{sharp.Failed} failed of {sharp.Total}");

							Refii.Level.Engine_TerrainPrebuild.Progress ground =
								Refii.Level.Engine_TerrainPrebuild.Run(directory, progress =>
									_ormStatus = progress.Total == 0
										? "Preparing terrain..."
										: $"Preparing terrain... {progress.Done:N0} of " +
										  $"{progress.Total:N0}");
							GD.Print($"[game] terrain sweep: {ground.Built} enhanced, " +
								$"{ground.Failed} unreadable of {ground.Total}");
						}
					}
					catch (Exception e)
					{
						// The sweep is an optimisation; a failure means the live path pays
						// per-texture instead, never that the boot stops.
						GD.Print($"[game] orm sweep failed: {e.Message}");
					}
					finally
					{
						_ormDone = true;
					}
				})
				{
					IsBackground = true,
					Name = "orm-prebuild",
				};
				worker.Start();
				return;
			}

			if (!_ormDone)
			{
				Refii.Models.Engine_ShaderWarmup.SetStatus(_ormStatus);
				return;
			}

			_booted = true;
			Refii.Models.Engine_ShaderWarmup.SetStatus("Preparing Albion...");
			Boot();
			// Only now: the guest is initialised (or Fail said why), so the videos that follow
			// have something real behind them instead of a grey gap.
			Refii.Models.Engine_ShaderWarmup.ReleaseScreen();
			return;
		}

		if (!_driving)
			return;

		if (Paused)
		{
			// The accumulator does not advance while paused; resuming replays nothing.
			return;
		}

		_accumulated += delta;

		double period = 1.0 / StepsPerSecond;
		int steps = 0;
		while (_accumulated >= period && steps < MaxCatchUpSteps)
		{
			_accumulated -= period;
			++steps;
		}

		if (_accumulated > period * MaxCatchUpSteps)
		{
			_accumulated = 0.0;
		}

		// One Tracy frame boundary and perf-counter flip per host frame. The renderer that
		// used to own Profiler::Flip() is compiled out, so without this the timeline has no
		// frames and the per-frame counter snapshots never rotate.
		Refii.Host.HostBridge.FrameMark();

		using (new Refii.Host.HostBridge.TraceZone("guest-steps"))
		{
			for (int i = 0; i < steps; ++i)
			{
				if (!StepGuest())
				{
					return;
				}
			}
		}

		if (steps == 0)
		{
			return;
		}

		// Swap in enhanced textures on the main thread, budgeted; no-op without an upscaler.
		Engine_TexLibrary.Pump();

		// Periodic report; prints only when the counters have moved.
		if (_steps % 600 == 0)
		{
			Engine_TexLibrary.ReportIfChanged();
			Refii.Models.Engine_MdlMaterial.ReportIfChanged();
		}

		// After the step: the pub-game manager is written by this frame's guest tick.
		_pubGames?.Poll();

		TraceCamera();
	}

	/// <summary>One slice of the guest's loop. False once it has stopped.</summary>
	private bool StepGuest()
	{
		if (!HostBridge.Step())
		{
			_driving = false;
			string reason = HostBridge.HostedStatus;
			GD.Print($"[game] guest stopped after {_steps:N0} steps: {reason}");

			// Latch after the status is read: it makes the bridge refuse, which is what stops
			// every polling node other than `GameWindow`.
			HostBridge.MarkUnusable($"the guest stopped: {reason}");
			EmitSignal(SignalName.Stopped, reason);
			return false;
		}

		_steps++;
		return true;
	}

	/// <summary>
	/// Logs the guest camera when it moves. The snapshot at 0x8331A230 is a pull cache refreshed
	/// by sub_82211598 when dword_83319650 (sim generation) moves, via sub_821DD3B0 ->
	/// sub_82265F90 whose memo at cameraManager+0xD0 is KeTickCount-stamped (at most once per ms).
	/// Preferred source is the camera manager's pushed block:
	/// cameraManager+0xE0 position, +0xF0 up, +0x100 forward, +0x110 fov, +0x114 bool, written by
	/// sub_821B8FD8 once per sim tick from sub_8233B6B0. Manager pointer: Game = *(0x83496AB8+12),
	/// binary-search the 12-byte entries between *(Game+0x9C)+8 and +12 for key 1 (key 2 when
	/// *(Game+0x8C)+0x34 and +0x36 say split screen), then *(entry+4), then *(view+4).
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
		if (ReferenceEquals(Current, this))
		{
			Current = null;
		}

		Engine_TexLibrary.Report();
		Refii.Models.Engine_MdlMaterial.ReportIfChanged();

		// Guest threads left live at process exit abort the process. Raw version, not
		// `TryGetAbiVersion`: that one reports the refusal latch, set exactly when the guest
		// stopped. Only a missing or wrong-version library excuses skipping teardown.
		if (HostBridge.TryGetRawAbiVersion() == HostBridge.ExpectedAbiVersion)
		{
			GD.Print($"[game] shutdown: {HostBridge.Shutdown().Message}");
		}
	}

	/// <summary>The export wins; otherwise the one shared convention.</summary>
	private static string Directory(string configured) =>
		System.IO.Directory.Exists(configured) ? configured : Engine_GameData.Resolve();

	/// <summary>
	/// Abandons the boot. `Stopped` only reaches `GameWindow`; latching the bridge is what stops
	/// every other node polling the guest.
	/// </summary>
	private void Fail(string reason)
	{
		GD.PrintErr($"[game] {reason}");
		HostBridge.MarkUnusable(reason);
		EmitSignal(SignalName.Stopped, reason);
	}
}
