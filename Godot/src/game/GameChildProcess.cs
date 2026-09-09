using System.Collections.Generic;
using Godot;

namespace Refii.Game;

/// <summary>
/// The game runs in a CHILD PROCESS of the launcher, not inside it.
/// </summary>
/// <remarks>
/// THE GUEST CAN ONLY START ONCE PER PROCESS. `RefiiHostStartup` refuses a second attempt —
/// "guest already started in this process; the runtime's global state" — because the recompiled
/// binary takes fixed memory reservations and sets up global state at init. So while the old
/// in-process game WINDOW did leave the launcher standing when it closed, the launcher was then
/// permanently unable to start another game: Play worked exactly once per run of the app.
///
/// A child process gives every Play a clean address space, so that constraint stops being a
/// limit on the UI. It also means a guest crash takes the game down and not the launcher.
///
/// SAME BINARY, TWO ROLES. The child is this very executable, so it needs some way to know which
/// role to take. That marker is an ENVIRONMENT VARIABLE rather than a command-line flag: nothing
/// a user types or sees, and it cannot collide with the arguments Godot itself parses.
/// </remarks>
public static class GameChildProcess
{
	/// <summary>Set by the launcher on the child it spawns; never set by hand.</summary>
	private const string Marker = "REFII_GAME_CHILD";

	/// <summary>True when this process was spawned by the launcher to BE the game.</summary>
	public static bool IsChild => OS.GetEnvironment(Marker) == "1";

	/// <summary>
	/// Starts the game as a child of this process.
	///
	/// EDITOR AND EXPORT TAKE THE SAME PATH. `OS.GetExecutablePath()` is the packaged game in an
	/// export and the Godot binary in the editor; the only difference is that the editor build
	/// needs telling which project to open, so `--path` is added there. The behaviour either
	/// side is identical — spawn, minimise, restore on exit — rather than the editor keeping a
	/// second, in-process code path that would then be the one nobody tests.
	/// </summary>
	public static bool Spawn(out Refii.Launcher.Core.ProcessHandle handle, out string error)
	{
		var arguments = new List<string>();
		if (OS.HasFeature("editor"))
		{
			arguments.Add("--path");
			arguments.Add(ProjectSettings.GlobalizePath("res://"));
		}

		return Refii.Launcher.Core.Platform.LaunchProcess(
			string.Empty,
			OS.GetExecutablePath(),
			arguments,
			new Dictionary<string, string> { [Marker] = "1" },
			out handle,
			out error);
	}
}
