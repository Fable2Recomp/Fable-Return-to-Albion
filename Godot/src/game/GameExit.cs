using Godot;

namespace Refii.Game;

/// <summary>
/// Ending the game process.
/// </summary>
/// <remarks>
/// The guest's own quit rows leave the world and return to its title; the console owned
/// everything past that, so nothing shipped ends the app.
///
/// The game runs as a child of the launcher (see <see cref="GameChildProcess"/>). Quitting ends
/// that child only: the launcher un-minimises whenever its child exits, however it exited.
///
/// Nothing here shuts the guest down: the process is ending and the kernel reclaims its
/// reservations. The save is sequenced BEFORE the quit, by the guest — see GameUi's quit latch.
/// </remarks>
public static class GameExit
{
	/// <summary>
	/// Ends the game. The launcher that started it comes back.
	/// </summary>
	public static void ToDesktop(SceneTree tree)
	{
		GD.Print("[game-exit] quitting the game; the launcher comes back");
		tree.Quit();
	}

	/// <summary>
	/// Ends the game child the moment its scene root is being deleted.
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
	/// core on the root partition.
	///
	/// Windows dies in the same place: the release template's render thread faults inside
	/// <c>~RendererSceneCull</c> under <c>RenderingServer</c> finish (player crash records,
	/// 2026-10-16, a heap read in its hash-table teardown).
	///
	/// <c>_exit</c> / <c>TerminateProcess</c>, not <c>Environment.Exit</c> or
	/// <c>ExitProcess</c>: those run the C runtime's exit path and DLL detach, whose destructors
	/// are the very code that faults. The runtime does the same for its standalone app
	/// (<c>ReXApp::RequestShutdown</c>: "hard-exit rather than run subsystem teardown").
	/// </remarks>
	public static void EndChildProcess()
	{
		if (!GameChildProcess.IsChild ||
			!(System.OperatingSystem.IsLinux() || System.OperatingSystem.IsWindows()))
		{
			return;
		}

		// An error-level line, because that is what flushes Godot's file logger.
		GD.PrintErr("[game-exit] ending the process here; the engine's display teardown is " +
			"skipped (see GameExit.EndChildProcess)");
		Flush();
		if (System.OperatingSystem.IsWindows())
		{
			TerminateProcess(GetCurrentProcess(), 0);
		}
		else
		{
			HardExit(0);
		}
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

	[System.Runtime.InteropServices.DllImport("kernel32.dll")]
	private static extern System.IntPtr GetCurrentProcess();

	[System.Runtime.InteropServices.DllImport("kernel32.dll")]
	private static extern bool TerminateProcess(System.IntPtr process, uint exitCode);
}
