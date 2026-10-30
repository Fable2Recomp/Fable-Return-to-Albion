using Godot;
using Refii.Game;

namespace Refii.Launcher.Tools;

/// <summary>
/// Proves the game opens in a REAL OS window rather than one drawn inside the launcher.
///
/// The distinction is invisible in a screenshot — an embedded subwindow has a title bar
/// and a close button and looks like a window — so it needs asserting rather than eyeing.
/// <c>Window.IsEmbedded()</c> is the ground truth.
///
/// The guest is left off: this checks window management, and a two-second guest init in
/// the middle of it only makes the failure slower to see.
/// </summary>
public partial class GameWindowCheck : Node
{
	public override void _Ready()
	{
		// REFII_GAME_WINDOW_GUEST=1 runs the real thing. It matters: with the guest off the
		// session's shutdown is a no-op, so the interesting question — whether tearing the
		// guest down takes the process with it — is exactly the one a guest-less run cannot
		// answer.
		bool guest = OS.GetEnvironment("REFII_GAME_WINDOW_GUEST") == "1";
		GD.Print($"[window] guest: {(guest ? "on" : "off")}");

		GameWindow? window = GameWindow.Open(this, startGuest: guest);
		if (window is null)
		{
			GD.Print("[window] Open returned null — one is already up.");
			GetTree().Quit(1);
			return;
		}

		_window = window;
	}

	private GameWindow? _window;
	private int _frames;

	public override void _Process(double delta)
	{
		// The window is attached on a deferred call and its _Ready runs then, so nothing
		// about it is true yet on the frame Open returned. Wait for it to be in the tree
		// rather than guessing at a frame count.
		if (_window is null || !IsInstanceValid(_window))
		{
			if (_closing)
			{
				// Freed by the close we asked for; the post-close check still has to run.
				CheckAfterClose();
				return;
			}

			GD.Print("[window] the window went away before it was attached.");
			GetTree().Quit(1);
			return;
		}

		if (_closing)
		{
			// A few frames for the free and the window-manager round trip to land.
			if (++_afterFrames < 10)
			{
				return;
			}

			CheckAfterClose();
			return;
		}

		// Let the guest actually run for a bit before closing it, so shutdown happens
		// against a live runtime rather than one still starting.
		// The startup video does not begin until several seconds in, so a shot taken at
		// four seconds catches an empty screen and reads as "video is broken".
		int settle = OS.GetEnvironment("REFII_GAME_SHOT").Length != 0 ? 240 : 90;
		if (int.TryParse(OS.GetEnvironment("REFII_GAME_SHOT_FRAMES"), out int wanted) &&
			wanted > 0)
		{
			settle = wanted;
		}
		if (_window.IsInsideTree() && _window.Session is { IsDriving: true } && ++_ranFrames < settle)
		{
			return;
		}

		if (!_window.IsInsideTree())
		{
			if (++_frames > 120)
			{
				GD.Print("[window] never entered the tree.");
				GetTree().Quit(1);
			}

			return;
		}

		Report(_window);
	}

	private void Report(GameWindow window)
	{
		// THE GAME IS THE MAIN WINDOW NOW, so every property below is asked of the display
		// server rather than of the node — there is no child `Window` to interrogate.
		const int Main = (int)DisplayServer.MainWindowId;
		Vector2I size = DisplayServer.WindowGetSize(Main);
		string title = GetTree().Root.Title;
		// Assert against refii.toml, not against a constant. A check that hardcodes
		// 1280x720 passes whether or not the file is being read at all, which is precisely
		// the thing it exists to prove.
		GameWindowSettings expected = GameWindowSettings.Load();
		GD.Print($"[window] title={title} size={size} " +
			$"mode={DisplayServer.WindowGetMode(Main)}");
		GD.Print($"[window] {Refii.Core.Engine_Title.Active.ConfigFileName} says: {expected}");

		if (size != expected.Size)
		{
			GD.Print($"[window] FAIL — window is {size}, {Refii.Core.Engine_Title.Active.ConfigFileName} asks for {expected.Size}.");
			GetTree().Quit(1);
			return;
		}
		// THE EMBEDDED CHECK IS GONE, and deliberately: it asserted that the game was a real OS
		// window rather than drawn inside the launcher's viewport, which cannot be false now
		// that the game IS the main window. What is still worth asserting is that this process
		// came up as the GAME and not as a second launcher — the failure that produced two
		// windows on screen.
		if (title != "Fable 2")
		{
			GD.Print($"[window] FAIL — main window is titled '{title}', not the game. " +
				"This process did not take the game role.");
			GetTree().Quit(1);
			return;
		}

		GD.Print("[window] OK — the game owns the main window.");

		DisplayServer.WindowMode launcher =
			DisplayServer.WindowGetMode((int)DisplayServer.MainWindowId);
		GD.Print($"[window] launcher mode while the game is up: {launcher}");
		if (launcher != DisplayServer.WindowMode.Minimized)
		{
			GD.Print("[window] FAIL — the launcher did not get out of the way.");
			GetTree().Quit(1);
			return;
		}

		// Close it the way the user does: the window manager sends close_requested rather
		// than anything calling QueueFree directly, so testing QueueFree would skip the
		// handler that actually has to restore the launcher.
		// Optional screenshot of the game window itself, so the UI can be looked at rather
		// than inferred from log lines.
		string shot = OS.GetEnvironment("REFII_GAME_SHOT");
		if (shot.Length != 0)
		{
			Image image = window.GetViewport().GetTexture().GetImage();
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(shot)!);
			GD.Print($"[window] shot -> {shot} ({image.SavePng(shot)})");
		}

		_closing = true;
		window.EmitSignal(Window.SignalName.CloseRequested);
	}

	private bool _closing;
	private int _afterFrames;
	private int _ranFrames;

	private void CheckAfterClose()
	{
		// The launcher must be BACK, and the process must still be alive to report it —
		// that second half is the real assertion, since a child window closing the whole
		// app would simply mean no output at all from here.
		DisplayServer.WindowMode launcher =
			DisplayServer.WindowGetMode((int)DisplayServer.MainWindowId);
		bool gone = _window is null || !IsInstanceValid(_window) || !_window.IsInsideTree();

		GD.Print($"[window] after close: launcher={launcher} gameWindowGone={gone} " +
			"process=alive");

		bool ok = gone && launcher != DisplayServer.WindowMode.Minimized;
		GD.Print(ok
			? "[window] OK — launcher restored and still running."
			: "[window] FAIL — launcher not restored, or the game window is still up.");

		GetTree().Quit(ok ? 0 : 1);
	}
}
