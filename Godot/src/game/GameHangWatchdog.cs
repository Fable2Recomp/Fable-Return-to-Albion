using Godot;
using Refii.Core;

namespace Refii.Game;

/// <summary>
/// Wires the bridge's hang watchdog (hang_watchdog.cpp) to the frame: the frame stamp and the
/// phase index through <see cref="Engine_HangWatchdogFeed"/>, and the path of Godot's own log
/// file stated once, so a report for a main thread that stopped mid-frame says which phase it
/// was in and where the last printed lines are. Installed before the guest boots, because
/// loads are exactly where it has frozen.
/// </summary>
/// <remarks>
/// NOT a <see cref="Logger"/>. Godot dispatches a managed Logger through the C# glue, which
/// looks StringNames up; a print made under the StringName mutex would deadlock there, and the
/// watchdog must never be a hang vector itself. The report's reader tails godot.log instead.
/// </remarks>
public static class GameHangWatchdog
{
	private static bool _installed;

	/// <summary>Installs the feed once for the process.</summary>
	public static void Install()
	{
		if (_installed)
		{
			return;
		}

		_installed = true;
		Engine_HangWatchdogFeed.Install(
			Refii.Host.HostBridge.RegisterFramePhase,
			Refii.Host.HostBridge.SetFramePhase,
			Refii.Host.HostBridge.FrameAlive);

		Refii.Host.HostBridge.SetHostLogPath(GameLogPaths.GodotLog);
	}
}
