namespace Refii.Game;

using Godot;

/// <summary>
/// Where this process's Godot log is. A game child is spawned with <c>--log-file</c> set to the
/// running title's <c>logs/&lt;Title&gt;/godot.log</c> (GameChildProcess.Spawn); Godot consumes that
/// flag, so it never reaches <c>OS.GetCmdlineArgs</c> and the path is taken from the title instead.
/// Anything else logs to the project's own file-logging path. Every reader of the live log asks here.
/// </summary>
public static class GameLogPaths
{
	private static string? _godotLog;

	public static string GodotLog => _godotLog ??= Resolve();

	private static string Resolve()
	{
		if (GameChildProcess.IsChild)
		{
			return Refii.Core.Engine_Title.Active.GodotLogPath;
		}

		string stated = ProjectSettings.GetSetting("debug/file_logging/log_path").AsString();
		return ProjectSettings.GlobalizePath(stated.Length != 0 ? stated : "user://logs/godot.log");
	}
}
