using Godot;

namespace Refii.Game;

/// <summary>
/// Ending the app, and telling the launcher whether to come back.
/// </summary>
/// <remarks>
/// The guest's own quit rows leave the world and return to its title; the console owned
/// everything past that, so nothing shipped ends the app.
///
/// THE EXIT STATUS IS THE CHANNEL. The game runs as a child of the launcher (see
/// <see cref="GameChildProcess"/>), which un-minimises when that child exits — right for a quit
/// to title, wrong for a quit to desktop, which has to take the launcher with it.
/// `ProcessHandle.ExitCode` is already read there, so one bit of intent needs no second channel.
///
/// Nothing here shuts the guest down: the process is ending and the kernel reclaims its
/// reservations. The save is sequenced BEFORE the quit, by the guest — see GameUi's quit latch.
/// </remarks>
public static class GameExit
{
	/// <summary>
	/// The child's exit status meaning "the player asked to reach the desktop".
	/// </summary>
	/// <remarks>
	/// Any other status brings the launcher back, which is what a normal close, a crash and a
	/// kill should all do. Clear of the codes Godot returns and of the 128+signal crash range.
	/// </remarks>
	public const int ToDesktopStatus = 42;

	/// <summary>
	/// Ends the app and asks the launcher to close too.
	/// </summary>
	public static void ToDesktop(SceneTree tree)
	{
		GD.Print("[game-exit] quitting to the desktop — the launcher is asked to close as well " +
			$"(status {ToDesktopStatus})");
		tree.Quit(ToDesktopStatus);
	}
}
