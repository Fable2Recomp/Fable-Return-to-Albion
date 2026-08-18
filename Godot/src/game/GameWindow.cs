using Refii.Host;
using Godot;

namespace Refii.Game;

/// <summary>
/// The game's own OS window, opened by the launcher.
///
/// A separate <see cref="Window"/> rather than a scene change, because the launcher
/// stays up: it is where the console output, settings and the Play button live, and
/// replacing it with the game would take them away for the whole session.
///
/// This is also the window the guest does NOT create. It runs headless precisely so
/// that this one is the only one — see the host bridge's REX_EMBED_HEADLESS.
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
	/// Boot the guest with the window. Off opens the window and mounts assets only, which
	/// is how the window itself is tested without a two-second guest init in the way.
	/// </summary>
	public bool StartGuest { get; set; } = true;

	/// <summary>The live session, once <see cref="_Ready"/> has run.</summary>
	public GameSession? Session => _session;

	public override void _Ready()
	{
		Title = "Fable 2";

		// refii.toml owns the window, exactly as it does for the other renderers. Godot
		// draws the game now, but the settings did not move.
		GameWindowSettings settings = GameWindowSettings.Load();
		GD.Print($"[game] window: {settings}");

		Size = settings.Size;

		// Centre on the configured monitor, so the game does not open half off-screen or
		// stacked exactly over the launcher. A monitor index out of range falls back to the
		// primary rather than putting the window somewhere that does not exist.
		int monitor = settings.Monitor < DisplayServer.GetScreenCount() ? settings.Monitor : 0;
		Vector2I screen = DisplayServer.ScreenGetSize(monitor);
		Position = ((screen - Size) / 2) + DisplayServer.ScreenGetPosition(monitor);

		CurrentScreen = monitor;
		Visible = true;

		if (settings.Fullscreen)
		{
			Mode = Window.ModeEnum.Fullscreen;
		}

		// Vsync is a DISPLAY-server setting, not a per-window one, so this is the whole
		// application's. That is right here: the launcher is minimised behind the game and
		// the game is the only thing drawing.
		DisplayServer.WindowSetVsyncMode(
			settings.Vsync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled,
			(int)DisplayServer.MainWindowId);

		// NOT transient. A transient window is owned by the launcher, and window managers
		// minimise an owned window along with its owner — which would minimise the game the
		// instant the launcher gets out of its way, immediately below.
		Transient = false;

		MinimiseLauncher();

		// Godot renders this window; the guest never draws into it.
		_session = new GameSession { Name = "GameSession", StartGuest = StartGuest };
		AddChild(_session);

		// Audio, played by Godot and — because the guest is credited for exactly what this
		// node takes — paced by Godot. Added beside the session rather than inside it: the
		// guest's audio worker stalls if nothing drains its output, so this wants to be up
		// for the whole life of the session, not only once the world exists.
		AddChild(new GameAudio { Name = "GameAudio" });

		// The game's own UI, rendered by Godot from the title's presentation data. Added
		// alongside the session rather than inside it: the UI stands up from the shipped
		// assets and does not need a running guest, so a failed boot still shows a screen
		// instead of a blank window.
		_ui = new GameUi { Name = "GameUi" };
		AddChild(_ui);

		// The world, drawn through the guest's camera. Added before the UI so the UI and
		// the videos compose over it.
		AddChild(new GameWorld { Name = "GameWorld" });

		// Above the UI: the startup logos play over everything.
		_video = new GameVideo { Name = "GameVideo" };
		AddChild(_video);

		// The boot order the player sees: logos first, then the front end.
		//
		// The UI is built during the logos but stays off screen until the movies are done,
		// so the front end never flashes up ahead of them. After that it is simply "video
		// over UI" — a movie the guest opens later covers the front end and hands it back
		// when it ends. That needs no knowledge of how many movies the startup sequence
		// contains, which is the guest's business, not ours.
		//
		// Concealing is immediate. Revealing waits for the GUEST to say it has reached its
		// front end — see _Process — rather than for a movie to end, which was the host
		// guessing and put the menu on screen in the seam between two logos.
		// NOT FOR AN EMBEDDED MOVIE — an ability preview plays inside the abilities card, and
		// concealing would hide the screen it belongs to.
		_video.MovieStarted += _ =>
		{
			if (!_ui.HasEmbeddedMovie)
			{
				_ui.Conceal();
			}
		};

		// So the UI can draw an embedded movie into the element hosting it: the presentation
		// and its ordinal map live in GameUi, the decoded frame lives here.
		_ui.Video = _video;

		_session.Stopped += OnSessionStopped;

		// Closing the window ends the session rather than orphaning a running guest. The
		// launcher comes back rather than the process going away: this is a CHILD window, so
		// closing it must not read as quitting the app.
		CloseRequested += () =>
		{
			// A close here means the window manager asked for it; a teardown without this
			// line came from somewhere else.
			GD.Print("[window] close_requested from the window manager");
			RestoreLauncher();
			QueueFree();
		};
	}

	/// <summary>
	/// Shows the front end exactly when the guest has one.
	///
	/// The guest builds its title/main-menu state only after every startup logo has played
	/// AND its bank mounter has finished, so this is the title itself saying the boot
	/// sequence is over. A movie opened later still covers the UI, and hands it back when
	/// it ends.
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

		// THE UI LAYER STAYS UP IN GAME.
		//
		// Concealing it the moment the front end goes away keeps the menu off the running game,
		// but it also makes "the front end is gone" mean "there is no UI at all" — and the guest
		// has a HUD standing up the whole time it is in game, plus a pause menu and a menu
		// cycler, none of which would ever get a chance to draw.
		//
		// So the layer follows whether the guest has ANY live presentation worth drawing, and
		// which one is drawn is GameUi's business.
		//
		// REVEALING AND CONCEALING ARE TWO DIFFERENT CONDITIONS, and collapsing them into one
		// is a bug. Revealing waits for the front end to reach a non-zero state, because that
		// is the guest saying it has something up. Concealing must NOT be the negation of that:
		// the title screen sits at state 0 while it is perfectly visible, so hiding on
		// `state == 0` blanks the front end in the middle of itself.
		//
		// What ends the UI is the guest no longer HAVING one — the front-end GUI object gone
		// and no in-game presentation standing up.
		bool guestHasUi = HostBridge.FrontEndGuiIsUp || _ui.HasInGameUi;

		if (_ui.IsRevealed)
		{
			if (!guestHasUi)
			{
				GD.Print("[game-ui] the guest has no live UI; hiding the layer");
				_ui.Conceal();
			}

			return;
		}

		if (HostBridge.FrontEndState() != 0 || _ui.HasInGameUi)
		{
			_ui.Reveal();
		}
	}

	/// <summary>
	/// Writes periodic screenshots of the REAL window, when <c>REFII_SHOT_DIR</c> asks for it.
	///
	/// The two UI tools stand the front end up with no guest, which answers "does the screen
	/// build" and cannot answer "is it right": with no guest every element sits on its default
	/// slide and every label falls back to the .bgf's own DEFAULT_TEXT_ID, so a dialog the game
	/// would never show is drawn over the logo and reads as a layout bug. Only a live run puts
	/// the guest's own per-element state on the screen, and only a shot of the window shows the
	/// front end composed over the world the way the game composes it.
	///
	/// Interval comes from <c>REFII_SHOT_EVERY</c> in frames (default 600, ~10s at 60fps), so
	/// one unattended run yields the boot logos, the title, the menu and the world in sequence
	/// without anyone at the keyboard.
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

		// Never during a movie. A Bink frame is the guest's own pre-rendered video drawn over
		// everything, so a shot taken then says nothing about what Godot rendered — and the
		// boot logos plus the opening cinematic run for minutes, which is long enough that
		// three consecutive runs produced nothing but movie frames while the world was
		// standing behind them the whole time.
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
	/// Reports the guest's front end as it moves, so a run says what the title is doing
	/// rather than only what we drew. Logged on change only — this is polled every frame.
	/// </summary>
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

		// CHANGE-GATED: the load's resting state is static and the previous behaviour printed
		// the same four lines once a second for the whole session. The signature quantises the
		// values that legitimately jitter (the fence's submitted counter) so only a real state
		// change re-reports.
		string loadingKey = HostBridge.GetLoadingScreen() is { } ls
			? $"{ls.Manager:X8}/{ls.Screen:X8}/{ls.Fade:F1}/{ls.Requested}/{ls.Done}"
			: "none";
		string ringKey = string.Empty;
		foreach (HostBridge.SceneStream s in HostBridge.GetSceneStreams())
		{
			ringKey += $"{s.WriteCursor:X}:{s.ReaderConsumed:X};";
		}
		HostBridge.InitMarkers markers = HostBridge.GetInitMarkers();
		string signature = $"{fence.Busy}/{fence.BlocksLevelLoad}/{fence.Submitted:F1}|{loadingKey}|{ringKey}|" +
			$"{markers.GdbManager:X8}/{markers.InGame}/{markers.GameMode:X8}/{markers.FrontEndMode:X8}";
		if (signature == _lastLoadTrace)
		{
			return;
		}
		_lastLoadTrace = signature;

		// "deadline", not "retired". +176 is a render DEADLINE BARRIER with two writers — the
		// ctor and sub_82388B48 — and it standing still while submitted climbs past it is the
		// normal resting state, not a stall.
		//
		// The holder is the part worth reading: the busy byte carries no state of its own, so
		// "busy=True" on its own names nothing.
		GD.Print($"[guest-fence] mgr=0x{fence.Manager:X8} preloading={fence.Busy} " +
			$"holder={fence.Holder} " +
			$"(submitted={fence.Submitted:F2} deadline={fence.Deadline:F2}) " +
			$"{(fence.BlocksLevelLoad ? "BLOCKS the level load" : "clear")}");

		// The loading screen's take-down handshake, alongside it — the game thread's one
		// UNBOUNDED wait sits on this, and only the render thread's fade tick releases it.
		if (HostBridge.GetLoadingScreen() is { } loading)
		{
			// The ART the guest picked, alongside the handshake. `obj != 0` already means a
			// loading screen is WANTED — that has been true throughout every level load in the
			// logs while nothing was drawn — so the missing half was never "is one up" but
			// "which one", and that is a texture name rather than a presentation.
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

		// How far into starting a game the guest has got. Game_InitDatabases says nothing
		// until its last statement, so without these it is indistinguishable from a guest
		// with nothing to do.
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
			!HostBridge.TryGetFrontEnd(out HostBridge.FrontEndScreen screen,
				out uint state, out bool canPressA, out uint cursor,
				out int sceneSlide, out int slideCount))
		{
			return;
		}

		// FollowGuest compares the live slide table itself and is a no-op when nothing
		// moved, so it is called every frame — a widget changing state does not show up
		// in the screen-level fields below.
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
		if (screen == HostBridge.FrontEndScreen.None &&
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
			(screen == HostBridge.FrontEndScreen.Title ? $" canPressA={canPressA}" : $" cursor={cursor}") +
			where);
	}

	private (HostBridge.FrontEndScreen, uint, bool, uint, int) _lastFrontEnd;

	public override void _ExitTree()
	{
		// Fires however the window went away; the line above says whether the WM asked.
		GD.Print($"[window] leaving the tree after {Engine.GetProcessFrames()} frames " +
			$"(visible={Visible} inTree={IsInsideTree()})");

		// Also here, not only on CloseRequested: the window can go away without the user
		// clicking its close button — the launcher freeing it, a failed session tearing it
		// down — and a launcher left minimised with nothing on screen looks like a crash.
		RestoreLauncher();

		if (_current == this)
		{
			_current = null;
		}
	}

	/// <summary>
	/// Drops the launcher out of the way for the duration of the game.
	///
	/// The launcher stays RUNNING — it owns the process, the console output and the host
	/// bridge — it just stops competing for the screen. Its mode is remembered so a
	/// maximised launcher comes back maximised.
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
	/// Opens a game window as a child of <paramref name="parent"/>.
	///
	/// One at a time: the guest runtime holds process-global state (the memory map, the
	/// kernel, the MMIO handler), so a second session in the same process cannot work.
	/// </summary>
	public static GameWindow? Open(Node parent, bool startGuest = true)
	{
		// Tracked in a field rather than by scanning the root's children, because the add
		// below is DEFERRED: a window that has been asked for but not yet attached is
		// invisible to a scan, so two quick presses of Play would each start a session.
		if (_current is not null && IsInstanceValid(_current))
		{
			_current.GrabFocus();
			return null;
		}

		// A Window node is EMBEDDED in the parent viewport by default, which draws the game
		// as a fake window floating inside the launcher — same pixels, same viewport, no
		// separate entry in the task bar and no independent resize. Turning embedding off on
		// the root viewport is what makes child Windows real OS windows.
		//
		// This is a property of the root viewport rather than of the window, so it applies
		// to the launcher's own dialogs too. That is the better behaviour for them anyway,
		// and it is why it is set here rather than in project.godot: the launcher runs in the
		// editor as a [Tool] script, and forcing native subwindows project-wide changes how
		// it behaves inside the editor as well.
		Viewport root = parent.GetTree().Root;
		root.GuiEmbedSubwindows = false;

		var window = new GameWindow { Name = "GameWindow", StartGuest = startGuest };
		_current = window;

		// Deferred, because add_child fails outright while the tree is "busy setting up
		// children" — which is exactly the state during a _Ready, and the failure is a
		// printed error rather than an exception, so the caller gets back a window that
		// was never attached and looks fine.
		root.CallDeferred(Node.MethodName.AddChild, window);
		return window;
	}
}
