using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Refii.Launcher.Core;

namespace Refii.Game;

/// <summary>
/// The game window's size and display settings, read from <c>refii.toml</c>.
///
/// Godot renders the game now, but the settings did not move: <c>refii.toml</c> is still
/// what the launcher writes and what a player edits, and the other renderers already
/// honour these keys. A window sized by a constant in C# would mean the resolution
/// controls silently stopped working the moment Godot took over drawing — the settings
/// panel would still offer them and nothing would happen.
/// </summary>
public readonly struct GameWindowSettings
{
	/// <summary>Fallback when the file says nothing. Matches the guest's own default mode.</summary>
	public static readonly Vector2I DefaultSize = new(1280, 720);

	public Vector2I Size { get; init; }

	public bool Fullscreen { get; init; }

	public int Monitor { get; init; }

	public bool Vsync { get; init; }

	/// <summary>Where the values came from, for the log line that explains the window.</summary>
	public string Source { get; init; }

	/// <summary>
	/// Finds <c>refii.toml</c> by CONVENTION rather than by asking the launcher.
	///
	/// <see cref="Installer.GetRuntimeConfigPath"/> resolves against a directory the
	/// launcher sets at startup, so anything that opens a game window without the launcher
	/// having run — a debug scene, the editor — silently gets defaults and no indication
	/// that the file was never read. The staged build directory and the repository root
	/// are where the file actually lives, so look there too.
	/// </summary>
	private static string ResolvePath()
	{
		var candidates = new List<string>();

		try
		{
			candidates.Add(Installer.GetRuntimeConfigPath());
		}
		catch (Exception)
		{
			// The launcher's path config is not available; the conventional ones still are.
		}

		string project = ProjectSettings.GlobalizePath("res://");
		candidates.Add(System.IO.Path.Combine(project, "build", "refii.toml"));
		candidates.Add(System.IO.Path.Combine(project, "refii.toml"));
		candidates.Add(System.IO.Path.GetFullPath(
			System.IO.Path.Combine(project, "..", "refii.toml")));

		foreach (string candidate in candidates)
		{
			if (!string.IsNullOrEmpty(candidate) && System.IO.File.Exists(candidate))
			{
				return candidate;
			}
		}

		return string.Empty;
	}

	public static GameWindowSettings Load()
	{
		string path = ResolvePath();
		if (path.Length == 0)
		{
			return new GameWindowSettings
			{
				Size = DefaultSize,
				Vsync = true,
				Source = "defaults (no refii.toml found)",
			};
		}

		SortedDictionary<string, string> values;
		try
		{
			values = RuntimeConfig.ReadValues(path);
		}
		catch (Exception exception)
		{
			GD.PushWarning($"[game] could not read {path}: {exception.Message}");
			values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		}

		// window_width/height is the WINDOW; video_mode_width/height is the guest's video
		// mode. They are separate keys and both are honoured, in that order: a player who
		// set only the video mode still gets a window that matches it, which is what the
		// other renderers do rather than opening a 1280x720 window around a 1080p mode.
		int width = ReadInt(values, "window_width", 0);
		int height = ReadInt(values, "window_height", 0);

		if (width <= 0 || height <= 0)
		{
			width = ReadInt(values, "video_mode_width", 0);
			height = ReadInt(values, "video_mode_height", 0);
		}

		string source = path;
		if (width <= 0 || height <= 0)
		{
			width = DefaultSize.X;
			height = DefaultSize.Y;
			source = "defaults (unset in " + System.IO.Path.GetFileName(path) + ")";
		}

		return new GameWindowSettings
		{
			// Clamped, not trusted. A zero or negative size makes a window that cannot be
			// found on screen, and the file is user-editable.
			Size = new Vector2I(Math.Max(320, width), Math.Max(240, height)),
			Fullscreen = ReadBool(values, "fullscreen", false),
			Monitor = Math.Max(0, ReadInt(values, "monitor", 0)),
			Vsync = ReadBool(values, "vsync", true),
			Source = source,
		};
	}

	private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback) =>
		values.TryGetValue(key, out string? raw) &&
		int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
			? parsed
			: fallback;

	private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
	{
		if (!values.TryGetValue(key, out string? raw))
		{
			return fallback;
		}

		string text = raw.Trim().Trim('"');
		return text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
			text == "1" ||
			(!text.Equals("false", StringComparison.OrdinalIgnoreCase) && text != "0" && fallback);
	}

	public override string ToString() =>
		$"{Size.X}x{Size.Y} fullscreen={Fullscreen} monitor={Monitor} vsync={Vsync} ({Source})";
}
