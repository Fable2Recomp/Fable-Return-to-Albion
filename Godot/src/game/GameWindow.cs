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
		_video.MovieStarted += _ => _ui.Conceal();

		_session.Stopped += OnSessionStopped;

		// Closing the window ends the session rather than orphaning a running guest. The
		// launcher comes back rather than the process going away: this is a CHILD window, so
		// closing it must not read as quitting the app.
		CloseRequested += () =>
		{
			// SAY SO. The game window disappearing seconds after boot is an intermittent
			// failure that has been read as a code regression more than once — including by
			// me, on two runs, which against a flaky start is not evidence at all. A close
			// arriving here means the WINDOW MANAGER asked for it; a teardown with no such
			// line came from somewhere else entirely, and that distinction is the whole
			// diagnosis.
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

		if (_ui is null || _video is null || _video.IsPlaying)
		{
			return;
		}

		// Take the front end back off screen once the guest no longer has one. Without this
		// the main menu stays drawn over the running game forever, because every field we
		// mirror it from belongs to an object the game has already replaced.
		if (_ui.IsRevealed)
		{
			if (HostBridge.TryGetAbiVersion() is not null && !HostBridge.FrontEndGuiIsUp)
			{
				GD.Print("[game-ui] the guest's front end is gone; hiding it");
				_ui.Conceal();
			}

			return;
		}

		if (HostBridge.TryGetAbiVersion() is not null && HostBridge.FrontEndState() != 0)
		{
			_ui.Reveal();
		}
	}

	/// <summary>
	/// Reports the guest's front end as it moves, so a run says what the title is doing
	/// rather than only what we drew. Logged on change only — this is polled every frame.
	/// </summary>
	/// <summary>
	/// Reports the guest crossing into the game, and then the fence its level load waits on.
	///
	/// CmdLevelChange_Update's gates are silent by construction — each one returns early and
	/// logs nothing, so a load that never starts looks exactly like a guest with nothing to
	/// do. This makes state 7's fence visible: `sub_82387B38` answers BUSY while
	/// submitted &lt;= retired or the busy byte is set, and with no GPU nothing retires frames.
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

		// NOT gated on the in-game flag. That flag is set late in Game_InitDatabases
		// (0x82311648), after the level-change command has already been queued at
		// 0x823115D0 — so gating the fence report on it hides the fence during exactly the
		// window the load is waiting in.
		if (HostBridge.GetRenderFence() is not { } fence)
		{
			return;
		}

		// Every 60th tick: this is a poll, and the interesting thing is whether it MOVES.
		if (++_fenceTicks % 60 != 0)
		{
			return;
		}

		GD.Print($"[guest-fence] mgr=0x{fence.Manager:X8} submitted={fence.Submitted} " +
			$"retired={fence.Retired} busy={fence.Busy} " +
			$"{(fence.BlocksLevelLoad ? "BLOCKS the level load" : "clear")}");

		// The loading screen's take-down handshake, alongside it — the game thread's one
		// UNBOUNDED wait sits on this, and only the render thread's fade tick releases it.
		if (HostBridge.GetLoadingScreen() is { } loading)
		{
			GD.Print($"[guest-loadscreen] mgr=0x{loading.Manager:X8} obj=0x{loading.Screen:X8} " +
				$"fade={loading.Fade} requested={loading.Requested} done={loading.Done}");
		}

		// The command-stream rings. `WouldBlock` is sub_8222ABE0's own condition, evaluated
		// for the 4-byte reserve it makes twice per message — so a run says whether the
		// producer is genuinely wedged rather than leaving it to be inferred.
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
		HostBridge.InitMarkers markers = HostBridge.GetInitMarkers();
		GD.Print($"[guest-init] gdbManager=0x{markers.GdbManager:X8} inGame={markers.InGame} " +
			$"gameMode=0x{markers.GameMode:X8} frontEndMode=0x{markers.FrontEndMode:X8}");
	}

	private bool _wasInGame;
	private int _fenceTicks;

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

		// "None" is the case that needs naming, and the GUI's OWN vtable is what names it.
		// dword_83496B08 is written by two different constructions through the same base
		// (0x8237132C): the front-end GUI, and a base-only instance built by
		// Game_InitDatabases whose +180/+184/+188 are not screen pointers at all. Reported
		// blind, the second one reads as "the front end went nowhere" when the truth is
		// that the game started.
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
		// The other half of the same question: this fires however the window went away, so a
		// run always records the moment, and the presence or absence of the close_requested
		// line above says which path took it.
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
