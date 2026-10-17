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

	/// <summary>High-frequency steps granted to the guest — the host's own simulation position.</summary>
	private long _granted;

	/// <summary>The guest's counter and our granted count when the loop was closed, so the two
	/// are compared as DIFFERENCES: they start from unrelated origins and the guest's own resets
	/// on world teardown.</summary>
	private uint _guestTickBase;
	private long _grantedBase;

	/// <summary>
	/// How far the host may lead the guest, in game ticks, before it stops granting.
	/// </summary>
	/// <remarks>
	/// Half a tick, i.e. one HF step: the guest re-bases the moment it is owed more than one
	/// step (`82276fa4 cmpwi cr6, r30, 1`) and a game tick is two steps. Any steady lead is time
	/// granted and not run. With the cap the world advances at the rate the guest can actually
	/// run, rather than at real time with a lurch to make up the difference.
	/// </remarks>
	private const double MaxLeadGameTicks = 0.5;

	/// <summary>
	/// A lead beyond this, in either direction, is a broken base rather than a real lead.
	/// </summary>
	/// <remarks>
	/// Above any genuine stall (a second of the guest doing nothing is 15 ticks), tight enough
	/// that a base taken against the wrong world is caught on the first frame.
	/// </remarks>
	private const double ReBaseGameTicks = 64.0;

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

		// Frame lifecycle boundaries: FrameStart (priority int.MinValue) samples GPU passes,
		// clocks, and advances FramePhases.FrameIndex; FrameEnd (priority int.MaxValue) marks frame end.
		// These must run every frame unconditionally in all configurations so guest liveness caching,
		// entity bone rigging, animation playheads, and UI carousels operate properly.
		AddChild(new Refii.Core.Engine_FrameStartNode { Name = "FrameStart" });
		AddChild(new Refii.Core.Engine_FrameEndNode { Name = "FrameEnd" });

		// THE GRANT GOES LAST IN THE FRAME, after every reader. A granted step starts on the
		// guest's own thread at once, so granted at the top of the frame it ran under the camera
		// read, the walk and the rig reads, and a read could take the next step's state under
		// this frame's stamp (Engine_TickClock.FrameStamp, read at frame start). The guest
		// finishes a step with the HF pass `sub_821A61B0` and then only publishes the render
		// (`sub_82278DD0`) and spins in SwitchToThread until the next grant (sub_82276D10,
		// 0x822772a4 onward), so a frame that saw a new guest frame now reads a parked guest,
		// and the step computes while Godot renders. Before Engine_FrameEndNode, which closes
		// the frame's measurements.
		ProcessPriority = int.MaxValue - 1;

		// The classic renderer's shadow cascades, up before any level builds its materials
		// so every classic material can bind the maps as it is made.
		Refii.Models.Engine_ClassicPath.Ensure(this);

		// THE GAME'S OWN PROFILER, when `tracy` is on (restart only; see GameTracy). Tracy runs
		// whole inside the process — client and analysis connected in memory, no socket — and a
		// capture is started from the dev menu. Until then the client collects nothing
		// (`TRACY_ON_DEMAND`) and sampling is off.
		//
		// EVERY FRAME PHASE BECOMES A TRACY ZONE, installed once here: the profiler carries the
		// same 28 phases the engine measures for its own report, so a zone cannot drift from the
		// phase it names. Godot's pass timestamps go onto the same timeline as GPU zones while
		// the engine captures them (`gpu_profile = true` passes `--gpu-profile`).
		GameTracy.Start(Refii.Core.Engine_RefiiConfig.Read(out _));
		if (GameTracy.Enabled)
		{
			GD.Print("[game] Tracy is up — capture from the dev menu" +
				(GameTracy.Advanced ? " (advanced data on)" : string.Empty));
			Refii.Core.Engine_FramePhases.ZoneBegin = Refii.Host.HostBridge.TraceZoneBegin;
			Refii.Core.Engine_FramePhases.ZoneEnd = Refii.Host.HostBridge.TraceZoneEnd;
			Refii.Core.Engine_FramePhases.Plot = Refii.Host.HostBridge.TracePlot;
			Refii.Core.Engine_GpuPasses.Sink = Refii.Host.HostBridge.TraceGpuPasses;
			AddChild(new GameTracyFeed { Name = "GameTracyFeed" });
		}

		// RenderDoc, when the launcher armed its Vulkan layer for this run (`renderdoc = true`):
		// takes the in-application API so the debug channel can trigger captures into user://captures.
		if (Refii.Core.Engine_RenderDoc.Install(
				ProjectSettings.GlobalizePath("user://captures"), out string renderDocNote))
		{
			GD.Print("[renderdoc] " + renderDocNote);
		}
		else if (renderDocNote.Length != 0)
		{
			GD.Print("[renderdoc] " + renderDocNote);
		}

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
		AddChild(new GameGoldenCock { Name = "GoldenCockInteraction" });
		AddChild(new GameGoldenCockCollision { Name = "GoldenCockCollision" });

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
	/// This bounds what the accumulator may bank, not what one frame may grant.
	/// </summary>
	private const int MaxCatchUpSteps = 4;

	/// <summary>
	/// Steps granted in a single frame. One, because two makes the guest skip time.
	/// </summary>
	/// <remarks>
	/// `sub_82276D10` compares what it owes against 1 (`82276fa4 cmpwi cr6, r30, 1`); at or
	/// below, it advances one step and sets its clock to `base + stepsDone/30`. Above, it takes
	/// the branch at `82276fb4` — advance one step and set the clock to now
	/// (`stfd f0, 0x110(r31)`), abandoning the time in between.
	///
	/// The bank above still absorbs a long frame and drains a step per frame, which above
	/// 30 fps covers the guest's 30 steps a second. Below 30 fps the world runs slow rather
	/// than lurching, deliberately: a sim clock advancing exactly 1/15 s a tick is what the
	/// original did.
	/// </remarks>
	private const int MaxStepsPerFrame = 1;

	/// <summary>How long the pace is averaged over before it is judged.</summary>
	private const double PaceWindowSeconds = 5.0;

	private double _paceSeconds;

	private int _paceSteps;

	private int _paceFrames;

	/// <summary>
	/// Halts the simulation without stopping it: the UI still draws and the bridge still reads.
	/// </summary>
	public bool Paused { get; set; }

	private double _accumulated;

	/// <summary>Seconds since the display's refresh rate was last read; the first frame reads it.</summary>
	private double _refreshRateAge = double.MaxValue;

	private const double RefreshRateSeconds = 1.0;

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

	private double _classicStatusTimer;

	private void session_processBody(double delta)
	{
		// The mods' settings.json values reach the game once per guest.
		Refii.Mods.ModSettings.Pump(Refii.Host.HostBridge.IsGuestUp);

		// The modern renderer's decals: the guest's stated ring as Godot decals.
		if (!Refii.Models.Engine_ClassicMaterial.Active && Refii.Host.HostBridge.IsGuestUp)
		{
			Refii.Fx.Engine_ModernDecals.Ensure(this).Pump();
			Refii.Level.Engine_ModernMist.Ensure(this).Pump();
			Refii.Lighting.Engine_LightZones.Pump();
			// The guest's live theme block: its texture slots give the modern dome its overlays.
			Refii.Models.Engine_ClassicTheme.Pump();
			Refii.Level.Engine_SkyOverlay.Pump();
			if (GetViewport()?.GetCamera3D() is { } modernEye)
			{
				Refii.Level.Engine_RoofCut.Pump(modernEye.GlobalPosition);
				Refii.Level.Engine_SnowPushes.Pump();
				// The guest's FX mesh twins, drawn on the modern materials; FableFx's simulated copies stand down.
				if (Refii.Fx.Engine_FxMeshTwins.Instance is null)
				{
					AddChild(new Refii.Fx.Engine_FxMeshTwins());
				}
				Refii.Fx.Engine_FxMeshTwins.Instance?.Pump(modernEye);
				Refii.Fx.FableFx.GuestMeshTwins = Refii.Fx.Engine_FxMeshTwins.FeedLive;
				// The guest's particle groups and trails on our own shaders; GameFx stands down only
				// the effects whose groups are drawn here (FableFx.GuestDrawn), every other keeps its own.
				if (Refii.Models.Engine_ClassicParticles.Instance is null)
				{
					AddChild(new Refii.Models.Engine_ClassicParticles { Name = "GuestParticles" });
				}
				Refii.Models.Engine_ClassicParticles.Instance?.Pump(modernEye);
				Refii.Models.Engine_ModernFur.Pump(modernEye);
				Refii.Fx.Engine_ModernDrunk.Pump(this);
				Refii.Fx.Engine_DisplacementMeshes.Pump();
			}
		}

		// The classic renderer's frame constants: registered once, pushed when they changed.
		if (Refii.Models.Engine_ClassicMaterial.Active)
		{
			Camera3D? camera = GetViewport()?.GetCamera3D();
			Refii.Models.Engine_ClassicPath.Pump(this, camera, GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920f, 1080f));

			// The path's liveness, every ten seconds: which feeds ran and what they drew.
			_classicStatusTimer += delta;
			if (_classicStatusTimer >= 10.0)
			{
				_classicStatusTimer = 0.0;
				GD.Print(Refii.Models.Engine_ClassicPath.Status());
			}
		}

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

		long lap = System.Diagnostics.Stopwatch.GetTimestamp();
		_accumulated += delta;

		double period = 1.0 / StepsPerSecond;
		int steps = 0;
		while (_accumulated >= period && steps < MaxStepsPerFrame)
		{
			_accumulated -= period;
			++steps;
		}

		// Godot's pacing is the guest's clock: the guest's loop takes exactly one step per
		// granted step and yields. Starved, it advances one step and re-bases, discarding the
		// backlog (0x82276FA4).
		//
		// Closed loop: grant on completion, not on wall time. If the host already leads by more
		// than `MaxLeadGameTicks` the grant is withheld and the time is dropped rather than
		// banked (banking guarantees a burst when the guest recovers); the whole game slows
		// together.
		//
		// The counter must count executed ticks. `SimTick` (`dword_83319650`) self-gates on the
		// front GameFlow command matching (0x821D46E8), is reset to 1 on world teardown
		// (0x8235075C) and is restored from saves. `game+0xFC` is incremented once per game tick
		// at 0x821C9044 with no gate.
		uint guestTick = HostBridge.SimClock() is { } clock ? clock.Tick : 0u;

		// `dword_83319650` is gated (`821d46e8` branches past its own increment) and has a
		// second increment path (`823d9904`); `guestTick` is the ungated once-per-game-tick
		// counter. Both are read in the same instant here, so their drift is observed exactly.
		if (guestTick != 0u)
		{
			Refii.Core.Engine_TickClock.ObserveCounterPair(HostBridge.SimTick, guestTick);

			// The draw point's terms are the display's: `sub_82388380` sizes its draw time from
			// the refresh rate and present interval (`XGetVideoMode`), never a measured frame
			// time. Re-read once a second, because the window can move to another screen — not
			// per frame: on X11 the query is XRRGetScreenResourcesCurrent plus a CRTC query per
			// output, round trips to the X server on the main thread.
			_refreshRateAge += delta;
			if (_refreshRateAge >= RefreshRateSeconds)
			{
				_refreshRateAge = 0.0;
				float hz = Godot.DisplayServer.ScreenGetRefreshRate(
					Godot.DisplayServer.WindowGetCurrentScreen((int)Godot.DisplayServer.MainWindowId));
				if (hz > 1.0f)
				{
					Refii.Core.Engine_TickClock.RefreshHz = hz;
				}
			}

			if (_guestTickBase == 0u)
			{
				_guestTickBase = guestTick;
				_grantedBase = _granted;
			}

			double hostGameTicks = (_granted - _grantedBase) * 0.5;
			double guestGameTicks = (double)guestTick - _guestTickBase;
			double lead = hostGameTicks - guestGameTicks;

			// Re-base on an implausible lead. The counts are compared as differences from a
			// captured base, and the guest's counter is not stable across a world coming up or
			// going down. A hugely negative lead would silently disable the cap below; re-basing
			// costs one frame of pacing.
			if (lead > ReBaseGameTicks || lead < -ReBaseGameTicks)
			{
				_guestTickBase = guestTick;
				_grantedBase = _granted;
				lead = 0.0;
			}

			Refii.Core.Engine_TickClock.HostGrantLead = lead;
			Refii.Core.Engine_TickClock.HostGrantWithheld = lead > MaxLeadGameTicks;
			if (lead > MaxLeadGameTicks)
			{
				// Hold at the boundary, do not rewind: the accumulator's remainder is also the
				// draw phase, so zeroing it would send the playhead backwards a step. Parking
				// just short of the period drops the surplus without moving the phase.
				_accumulated = System.Math.Min(_accumulated, period * 0.999);
				steps = 0;
			}
		}

		Refii.Core.Engine_FramePhases.Lap("session.clock", ref lap);
		if (steps > 0)
		{
			HostBridge.AdvanceTimeBase((uint)steps);
			_granted += steps;
		}

		// The same accumulator is the draw clock: `_accumulated / period` is how far this frame
		// lies between two guest steps, so the playback rate is 1.0 by construction.
		Refii.Core.Engine_TickClock.PublishHostTicks(_granted, _accumulated / period);

		if (_accumulated > period * MaxCatchUpSteps)
		{
			_accumulated = 0.0;
		}

		// One Tracy frame boundary and perf-counter flip per host frame. The renderer that
		// used to own Profiler::Flip() is compiled out, so without this the timeline has no
		// frames and the per-frame counter snapshots never rotate.
		Refii.Host.HostBridge.FrameMark();

		// TWO INSTRUMENTS ON THE SAME LOOP, and they answer different questions. The Tracy zone
		// is for a profiler attached to the native side, where the guest's own zones say what a
		// step is doing; the frame phase is ours and rides the debug channel, so `refii_perf`
		// answers "what does a step COST" with nothing attached. The pace line above says
		// whether we are keeping up; this says why not.
		using (Refii.Core.Engine_FramePhases.Measure("guest-step"))
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

		// SWAP IN ENHANCED TEXTURES BEFORE THE STEP GATE, NOT AFTER IT. Each queued completion
		// captures a whole mip chain — megabytes of native Image data sitting in the anonymous
		// heap until it is applied — and the queue is bounded only by the number of jobs. Behind
		// the gate it drained only while the guest was stepping, which is precisely NOT the case
		// during a loading screen or a level transition: the moment four workers are producing
		// fastest was the moment nothing consumed them. Budgeted still, so a warm cache cannot
		// trade a background cost for a visible hitch.
		Refii.Core.Engine_FramePhases.Lap("session.steps", ref lap);
		Engine_TexLibrary.Pump();
		Refii.Core.Engine_FramePhases.Lap("session.tex", ref lap);

		// THE PACE IT ACTUALLY GOT. The accumulator ASKS for StepsPerSecond; whether it gets it
		// is a fact about how long a step costs, and nothing above can assert it. A guest that
		// advances slower than real time moves the hero and his animation in slow motion while
		// the camera still runs at frame rate — which reads as the model detaching and then
		// snapping back, not as a frame-rate problem.
		//
		// Printed ONLY when it is behind, so a healthy run stays silent.
		_paceSeconds += delta;
		_paceSteps += steps;
		++_paceFrames;
		if (_paceSeconds >= PaceWindowSeconds)
		{
			double perSecond = _paceSteps / _paceSeconds;
			if (perSecond < StepsPerSecond * 0.9)
			{
				GD.Print($"[game-pace] the guest advanced {perSecond:0.0} step(s)/s against a " +
					$"{StepsPerSecond:0} Hz target while Godot drew " +
					$"{_paceFrames / _paceSeconds:0.0} frame(s)/s — the world is running at " +
					$"{perSecond / StepsPerSecond:P0} of real time");
			}

			_paceSeconds = 0.0;
			_paceSteps = 0;
			_paceFrames = 0;
		}

		if (steps == 0)
		{
			return;
		}

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
			_guestTickBase = 0u;
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
