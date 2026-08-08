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

		_session.Stopped += OnSessionStopped;

		// Closing the window ends the session rather than orphaning a running guest. The
		// launcher comes back rather than the process going away: this is a CHILD window, so
		// closing it must not read as quitting the app.
		CloseRequested += () =>
		{
			RestoreLauncher();
			QueueFree();
		};
	}

	public override void _ExitTree()
	{
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
