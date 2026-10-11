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
		Status = ToDesktopStatus;
		tree.Quit(ToDesktopStatus);
	}

	/// <summary>The status this process will end with: 0 unless a quit to the desktop was asked.</summary>
	public static int Status { get; private set; }

	/// <summary>
	/// Ends the game child the moment its scene root is being deleted, with the status above.
	/// </summary>
	/// <remarks>
	/// Everything this process owns has been torn down by then: every node has left the tree,
	/// the guest is shut down (GameSession's exit) and the logs are flushed. What remains of
	/// Godot's own cleanup is the engine unloading the display driver, and on Linux that is
	/// where the process has died on every close since 2026-10-08: first in
	/// <c>~RendererSceneCull</c> over leaked instances (fixed at the source), then, once past
	/// that, inside the NVIDIA driver's own library destructor when the Vulkan loader
	/// <c>dlclose</c>d it under <c>vkDestroyInstance</c> with the driver's worker threads still
	/// alive (core of 2026-10-11 03:36, driver 595.91.07) — a known class of NVIDIA exit fault
	/// with no application-side ordering that avoids it. Each such death cost a multi-gigabyte
	/// core on the root partition and, through the exit status, brought the launcher back
	/// after a quit to the desktop.
	///
	/// <c>_exit</c>, not <c>Environment.Exit</c>: the latter runs the C runtime's exit path,
	/// whose library destructors are the very code that faults. The launcher reads the status
	/// either way. The runtime does the same for its standalone app
	/// (<c>ReXApp::RequestShutdown</c>: "hard-exit rather than run subsystem teardown").
	/// </remarks>
	public static void EndChildProcess()
	{
		if (!GameChildProcess.IsChild || !System.OperatingSystem.IsLinux())
		{
			return;
		}

		// An error-level line, because that is what flushes Godot's file logger.
		GD.PrintErr($"[game-exit] ending the process here with status {Status}; the engine's " +
			"display teardown is skipped (see GameExit.EndChildProcess)");
		Flush();
		HardExit(Status);
	}

	private static void Flush()
	{
		try
		{
			System.Console.Out.Flush();
			System.Console.Error.Flush();
		}
		catch (System.Exception)
		{
			// Nothing to flush is not a reason to keep the process alive.
		}
	}

	[System.Runtime.InteropServices.DllImport("libc", EntryPoint = "_exit")]
	private static extern void HardExit(int status);
}
