using Refii.Ui;
using Refii.Host;
using Godot;

namespace Refii.Game;

/// <summary>
/// The game's own OS window, opened by the launcher. A separate <see cref="Window"/> rather
/// than a scene change so the launcher (console output, settings, Play) stays up. The guest
/// creates no window of its own — see the host bridge's REX_EMBED_HEADLESS.
/// </summary>
public partial class GameWindow : Window
{
	private static GameWindow? _current;

	private GameSession? _session;
	private GameUi? _ui;
	private GameVideo? _video;

	private DisplayServer.WindowMode _launcherMode = DisplayServer.WindowMode.Windowed;
	private bool _launcherRestored;

	/// <summary>
	/// Boot the guest with the window. Off opens the window and mounts assets only.
	/// </summary>
	public bool StartGuest { get; set; } = true;

	/// <summary>The live session, once <see cref="_Ready"/> has run.</summary>
	public GameSession? Session => _session;

	public override void _Ready()
	{
		Refii.Launcher.Core.CrashReporting.Install();
		Title = "Fable 2";

		// refii.toml owns the window, as it does for the other renderers.
		GameWindowSettings settings = GameWindowSettings.Load();
		GD.Print($"[game] window: {settings}");

		Size = settings.Size;

		// Centre on the configured monitor; an out-of-range index falls back to the primary.
		int monitor = settings.Monitor < DisplayServer.GetScreenCount() ? settings.Monitor : 0;
		Vector2I screen = DisplayServer.ScreenGetSize(monitor);
		Position = ((screen - Size) / 2) + DisplayServer.ScreenGetPosition(monitor);

		CurrentScreen = monitor;
		Visible = true;

		if (settings.Fullscreen)
		{
			Mode = Window.ModeEnum.Fullscreen;
		}

		// Vsync is a display-server setting, not a per-window one, so this is the whole
		// application's.
		DisplayServer.WindowSetVsyncMode(
			settings.Vsync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled,
			(int)DisplayServer.MainWindowId);

		// Not transient: window managers minimise an owned window along with its owner, and
		// the launcher is minimised immediately below.
		Transient = false;

		MinimiseLauncher();

		// Godot renders this window; the guest never draws into it.
		_session = new GameSession { Name = "GameSession", StartGuest = StartGuest };
		AddChild(_session);

		// Beside the session, not inside it: the guest's audio worker stalls if nothing drains
		// its output, so this must be up for the whole life of the session.
		AddChild(new GameAudio { Name = "GameAudio" });

		// Alongside the session, not inside it: the UI stands up from the shipped assets and
		// needs no running guest, so a failed boot still shows a screen.
		_ui = new GameUi { Name = "GameUi" };
		AddChild(_ui);

		// The world, drawn through the guest's camera. Added before the UI so the UI and
		// the videos compose over it.
		AddChild(new GameWorld { Name = "GameWorld" });

		// Above the UI: the startup logos play over everything.
		_video = new GameVideo { Name = "GameVideo" };
		AddChild(_video);

		// Video over UI: a movie covers the front end and hands it back when it ends.
		// Concealing is immediate; revealing waits for the guest to say it has a front end
		// (see _Process), not for a movie to end. Never for an embedded movie — an ability
		// preview plays inside the card, and concealing would hide the screen it belongs to.
		_video.MovieStarted += _ =>
		{
			if (!_ui.HasEmbeddedMovie)
			{
				_ui.Conceal("a full-screen movie started over it");
			}
		};

		// So the UI can draw an embedded movie into the element hosting it: the presentation
		// and its ordinal map live in GameUi, the decoded frame lives here.
		_ui.Video = _video;

		_session.Stopped += OnSessionStopped;

		// Closing ends the session rather than orphaning a running guest, and brings the
		// launcher back: this is a child window, so closing it is not quitting the app.
		CloseRequested += () =>
		{
			// A teardown without this line came from somewhere other than the WM.
			GD.Print("[window] close_requested from the window manager");
			RestoreLauncher();
			QueueFree();
		};
	}

	/// <summary>
	/// Shows the front end exactly when the guest has one: it builds its title/main-menu state
	/// only after every startup logo has played and its bank mounter has finished.
	/// </summary>
	public override void _Process(double delta)
	{
		TraceFrontEnd();
		StepShots();

		if (_ui is null || _video is null || _video.IsPlaying)
		{
			return;
		}

		if (HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		// The layer follows whether the guest has any live presentation (the HUD, pause menu
		// and menu cycler all stand up in game); which one is drawn is GameUi's business.
		// Reveal and conceal are two different conditions and must not be collapsed: reveal on
		// a non-zero front-end state, but the title sits at state 0 while perfectly visible,
		// so conceal only when the front-end GUI object is gone and no in-game presentation is
		// standing up.
		bool guestHasUi = HostBridge.FrontEndGuiIsUp || _ui.HasInGameUi;

		if (_ui.IsRevealed)
		{
			if (!guestHasUi)
			{
				GD.Print("[game-ui] the guest has no live UI; hiding the layer");
				_ui.Conceal("the guest has no live UI left");
			}

			return;
		}

		if (HostBridge.FrontEndState() != 0 || _ui.HasInGameUi)
		{
			_ui.Reveal();
		}
	}

	/// <summary>
	/// Writes periodic screenshots of the real window when <c>REFII_SHOT_DIR</c> asks for it.
	/// Interval is <c>REFII_SHOT_EVERY</c> in frames (default 600, ~10s at 60fps).
	/// </summary>
	private void StepShots()
	{
		if (_shotDirectory is null)
		{
			_shotDirectory = OS.GetEnvironment("REFII_SHOT_DIR");
			if (_shotDirectory.Length == 0)
			{
				return;
			}

			if (int.TryParse(OS.GetEnvironment("REFII_SHOT_EVERY"), out int every) && every > 0)
			{
				_shotEvery = every;
			}

			DirAccess.MakeDirRecursiveAbsolute(_shotDirectory);
			GD.Print($"[game-shot] writing a shot every {_shotEvery} frames to {_shotDirectory}");
		}

		if (_shotDirectory.Length == 0 || ++_shotFrames % _shotEvery != 0)
		{
			return;
		}

		// Never during a movie: the frame covers everything and says nothing about what Godot
		// rendered.
		if (_video is { IsPlaying: true })
		{
			// Try again next frame rather than waiting out another whole interval.
			--_shotFrames;
			return;
		}

		// The window's own texture, not the tree's root: this is a separate Window and its
		// viewport is what the player is looking at.
		Image image = GetTexture().GetImage();
		string path = $"{_shotDirectory}/shot_{_shotFrames:D6}.png";
		GD.Print(image.SavePng(path) == Error.Ok
			? $"[game-shot] {path} (frontEnd={HostBridge.FrontEndGuiIsUp} inGame={HostBridge.IsInGame})"
			: $"[game-shot] could not write {path}");
	}

	private string? _shotDirectory;

	private int _shotEvery = 600;

	private int _shotFrames;

	/// <summary>
	/// Reports the guest crossing into the game, and the fence its level load waits on —
	/// CmdLevelChange_Update's gates are silent, so a stalled load looks like an idle guest.
	/// </summary>
	private void TraceLevelLoad()
	{
		if (HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		bool inGame = HostBridge.IsInGame;
		if (inGame != _wasInGame)
		{
			_wasInGame = inGame;
			GD.Print($"[guest-game] byte_83496A5E={(inGame ? 1 : 0)} — the guest says it is " +
				$"{(inGame ? "IN GAME" : "out of the game")}");
		}

		// Not gated on the in-game flag: it is set at 0x82311648, after the level change is
		// queued at 0x823115D0, so gating on it would hide the fence during the wait.
		if (HostBridge.GetRenderFence() is not { } fence)
		{
			return;
		}

		// Every 60th tick: this is a poll, and the interesting thing is whether it MOVES.
		if (++_fenceTicks % 60 != 0)
		{
			return;
		}

		// Change-gated on a signature that quantises the values which legitimately jitter
		// (the fence's submitted counter), so only a real state change re-reports.
		string loadingKey = HostBridge.GetLoadingScreen() is { } ls
			? $"{ls.Manager:X8}/{ls.Screen:X8}/{ls.Fade:F1}/{ls.Requested}/{ls.Done}"
			: "none";
		string ringKey = string.Empty;
		foreach (HostBridge.SceneStream s in HostBridge.GetSceneStreams())
		{
			ringKey += $"{s.WriteCursor:X}:{s.ReaderConsumed:X};";
		}
		HostBridge.InitMarkers markers = HostBridge.GetInitMarkers();

		// Sim time is in the signature, quantised to a second: everything else here is resting
		// state, and a raw sim time would move every frame and defeat the gate.
		HostBridge.GameLoopPacer pacer = HostBridge.GetGameLoopPacer();
		string signature = $"{fence.Busy}/{fence.BlocksLevelLoad}/{fence.Submitted:F1}|{loadingKey}|{ringKey}|" +
			$"{markers.GdbManager:X8}/{markers.InGame}/{markers.GameMode:X8}/{markers.FrontEndMode:X8}|" +
			$"{pacer.Known}/{(int)pacer.SimTime}/{pacer.SimulationGate}/{pacer.WorldGate}";
		if (signature == _lastLoadTrace)
		{
			return;
		}
		_lastLoadTrace = signature;

		// +176 is a render deadline barrier (writers: the ctor and sub_82388B48); it standing
		// still while submitted climbs past it is the resting state, not a stall. The busy
		// byte carries no state of its own, so the holder is the part worth reading.
		GD.Print($"[guest-fence] mgr=0x{fence.Manager:X8} preloading={fence.Busy} " +
			$"holder={fence.Holder} " +
			$"(submitted={fence.Submitted:F2} deadline={fence.Deadline:F2}) " +
			$"{(fence.BlocksLevelLoad ? "BLOCKS the level load" : "clear")}");

		// The loading screen's take-down handshake: the game thread's one unbounded wait sits
		// on this, and only the render thread's fade tick releases it.
		if (HostBridge.GetLoadingScreen() is { } loading)
		{
			// `obj != 0` only means a loading screen is wanted; the art names which one.
			string art = HostBridge.GetLoadingScreenArt() is { } picked
				? $" type={picked.Type} art='{picked.Name}' body=0x{picked.Body:X8}"
				: " art=UNREADABLE";

			GD.Print($"[guest-loadscreen] mgr=0x{loading.Manager:X8} obj=0x{loading.Screen:X8} " +
				$"fade={loading.Fade} requested={loading.Requested} done={loading.Done}" +
				$"{art} worldReady={HostBridge.WorldIsReady()}");
		}

		// `WouldBlock` is sub_8222ABE0's own condition for the 4-byte reserve it makes twice
		// per message.
		foreach (HostBridge.SceneStream stream in HostBridge.GetSceneStreams())
		{
			GD.Print($"[guest-ring] obj=0x{stream.Object:X8} cap=0x{stream.Capacity:X} " +
				$"write=0x{stream.WriteCursor:X} consumed=0x{stream.WriteConsumed:X} " +
				$"reader=0x{stream.ReaderConsumed:X} lastRead=0x{stream.LastRead:X}" +
				$"{(stream.WouldBlock(4u) ? "  WOULD BLOCK" : string.Empty)}");
		}

		// Whether the guest is ticking at all. `sim` is `game+0x110`, rewritten every game-loop
		// iteration including the ones where no tick is due, so a sim time standing still is a
		// blocked tick thread, not a paced one. `inFlight` is presented minus completed, read
		// from the record the frame-sync index names; it is the term `sub_82B6FCE8` adds to the
		// render deadline, and while it grows the command-stream drain burns a full present
		// period per call holding the global GPU-access lock.
		if (pacer.Known)
		{
			GD.Print($"[guest-pacer] game=0x{pacer.Game:X8} sim={pacer.SimTime:F2}s " +
				$"gates sim={pacer.SimulationGate} world={pacer.WorldGate} quit={pacer.QuitByte} " +
				$"| presented={pacer.Presented} completed={pacer.Completed} " +
				$"inFlight={pacer.InFlight} slot={pacer.SyncSlot} lock={pacer.ReaderLock} " +
				$"| timebase epoch={pacer.TimebaseEpoch:F0} hz={pacer.TimebaseTicksPerSecond:F0} " +
				$"| gui players={pacer.GuiRegisteredPlayers} update={pacer.GuiUpdateEnabled} " +
				$"window={pacer.GuiDoStringWindow} hudT={pacer.HudPresentationTime:F0} " +
				$"screenT={pacer.ScreenPresentationTime:F0}");

			// No registered player means no Gameface update at all. Posted through the guest's
			// own routine (sub_82371860), once per sample while the count stays 0.
			if (pacer.WorldGate && pacer.GuiRegisteredPlayers == 0 && pacer.GuiUpdateEnabled != 0)
			{
				GD.Print("[guest-pacer] the in-game GUI reports 0 registered players — asking the " +
					"guest to register the first one (sub_82371860)");
				HostBridge.PostFrontEndAction("RegisterPlayer");
			}
		}

		// How far into starting a game the guest has got: Game_InitDatabases says nothing
		// until its last statement.
		GD.Print($"[guest-init] gdbManager=0x{markers.GdbManager:X8} inGame={markers.InGame} " +
			$"gameMode=0x{markers.GameMode:X8} frontEndMode=0x{markers.FrontEndMode:X8}");
	}

	private bool _wasInGame;
	private int _fenceTicks;
	private string _lastLoadTrace = string.Empty;

	private void TraceFrontEnd()
	{
		TraceLevelLoad();

		if (HostBridge.TryGetAbiVersion() is null ||
			!HostBridge.TryGetFrontEnd(out Engine_UiModel.FrontEndScreen screen,
				out uint state, out bool canPressA, out uint cursor,
				out int sceneSlide, out int slideCount))
		{
			return;
		}

		// Called every frame: a widget changing state does not show up in the screen-level
		// fields below, and FollowGuest is a no-op when the live slide table has not moved.
		_ui?.FollowGuest(screen, sceneSlide);

		var now = (screen, state, canPressA, cursor, sceneSlide);
		if (now == _lastFrontEnd)
		{
			return;
		}

		_lastFrontEnd = now;

		// dword_83496B08 is written by two constructions through the same base (0x8237132C):
		// the front-end GUI, and a base-only instance from Game_InitDatabases whose
		// +180/+184/+188 are not screen pointers. The vtable is what tells them apart.
		string where = string.Empty;
		if (screen == Engine_UiModel.FrontEndScreen.None &&
			HostBridge.GetFrontEndRaw() is { } words)
		{
			where = $" gui=0x{words[0]:X8} guiVtable=0x{words[9]:X8}" +
				$" active=0x{words[1]:X8} activeVtable=0x{words[8]:X8}";

			// Say which of the two it is, or every later reader has to know this trap.
			where += HostBridge.FrontEndGuiIsUp
				? " (front-end GUI, no screen active)"
				: " (NOT the front-end GUI — the game has taken it over)";
		}

		GD.Print($"[guest-ui] {screen} state={state} slide={sceneSlide}/{slideCount}" +
			(screen == Engine_UiModel.FrontEndScreen.Title ? $" canPressA={canPressA}" : $" cursor={cursor}") +
			where);

		ReportFrontEndInit();
	}

	/// <summary>
	/// Says which of the front end's failure modes is in play, on change: never activated,
	/// activated into a gated-off tick, or ticking with nothing to draw. `InitRan` and
	/// `TickGatePasses` separate them; see <see cref="HostBridge.FrontEndInit"/> for the bases.
	/// </summary>
	private void ReportFrontEndInit()
	{
		if (HostBridge.TryGetFrontEndInit() is not { } init)
		{
			return;
		}

		var now = (init.InitRan, init.TickGatePasses, init.Presentation != 0u, init.Stamped);
		if (now == _lastFrontEndInit)
		{
			return;
		}

		_lastFrontEndInit = now;
		GD.Print($"[guest-ui] front end: Init {(init.InitRan ? "RAN" : "NEVER RAN")}, " +
			$"tick gate {(init.TickGatePasses ? "passes" : "BLOCKS the whole front-end frame")} " +
			$"(logo {init.LogoReady}, button {init.ButtonReady}), " +
			$"presentation 0x{init.Presentation:X8}, " +
			$"input stamp {(init.Stamped ? "written" : "never — this is the CONSTRUCTED state")}");
	}

	private (bool, bool, bool, bool) _lastFrontEndInit = (true, true, true, true);

	private (Engine_UiModel.FrontEndScreen, uint, bool, uint, int) _lastFrontEnd;

	public override void _ExitTree()
	{
		// Fires however the window went away; the line above says whether the WM asked.
		GD.Print($"[window] leaving the tree after {Engine.GetProcessFrames()} frames " +
			$"(visible={Visible} inTree={IsInsideTree()})");

		// Also here, not only on CloseRequested: the window can go away without the close
		// button, and a launcher left minimised with nothing on screen looks like a crash.
		RestoreLauncher();

		if (_current == this)
		{
			_current = null;
		}
	}

	/// <summary>
	/// Drops the launcher out of the way for the duration of the game; it keeps running (it
	/// owns the process and the host bridge). Its mode is remembered for the restore.
	/// </summary>
	private void MinimiseLauncher()
	{
		_launcherMode = DisplayServer.WindowGetMode((int)DisplayServer.MainWindowId);

		// Never remember "minimised" as the state to return to, or closing the game leaves
		// the user with no window at all and no obvious way back.
		if (_launcherMode == DisplayServer.WindowMode.Minimized)
		{
			_launcherMode = DisplayServer.WindowMode.Windowed;
		}

		DisplayServer.WindowSetMode(
			DisplayServer.WindowMode.Minimized, (int)DisplayServer.MainWindowId);
	}

	private void RestoreLauncher()
	{
		if (_launcherRestored)
		{
			return;
		}

		_launcherRestored = true;
		DisplayServer.WindowSetMode(_launcherMode, (int)DisplayServer.MainWindowId);
		DisplayServer.WindowMoveToForeground((int)DisplayServer.MainWindowId);
	}

	private void OnSessionStopped(string reason)
	{
		GD.Print($"[game] session stopped: {reason}");

		// Leave the window up on failure so the reason is readable; the user closes it.
		Title = $"Fable 2 — stopped ({reason})";
	}

	/// <summary>
	/// Opens a game window as a child of <paramref name="parent"/>. One at a time: the guest
	/// runtime holds process-global state (memory map, kernel, MMIO handler).
	/// </summary>
	public static GameWindow? Open(Node parent, bool startGuest = true)
	{
		// A field rather than a scan of the root's children: the add below is deferred, so a
		// window asked for but not yet attached is invisible to a scan.
		if (_current is not null && IsInstanceValid(_current))
		{
			_current.GrabFocus();
			return null;
		}

		// A Window node is embedded in the parent viewport by default; clearing this on the
		// root viewport is what makes child Windows real OS windows. Set here rather than in
		// project.godot because the launcher runs in the editor as a [Tool] script and this
		// is a root-viewport property that would change its editor behaviour too.
		Viewport root = parent.GetTree().Root;
		root.GuiEmbedSubwindows = false;

		var window = new GameWindow { Name = "GameWindow", StartGuest = startGuest };
		_current = window;

		// Deferred: add_child fails while the tree is busy setting up children (the state
		// during a _Ready), and it fails by printing rather than throwing.
		root.CallDeferred(Node.MethodName.AddChild, window);
		return window;
	}
}
