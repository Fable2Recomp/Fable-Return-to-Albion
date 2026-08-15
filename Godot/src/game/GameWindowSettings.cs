using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

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

	public static GameWindowSettings Load()
	{
		string path = Engine_RefiiConfig.ResolvePath();
		if (path.Length == 0)
		{
			return new GameWindowSettings
			{
				Size = DefaultSize,
				Vsync = true,
				Source = "defaults (no refii.toml found)",
			};
		}

		IReadOnlyDictionary<string, string> values = Engine_RefiiConfig.Read(out _);

		// window_width/height is the WINDOW; video_mode_width/height is the guest's video
		// mode. They are separate keys and both are honoured, in that order: a player who
		// set only the video mode still gets a window that matches it, which is what the
		// other renderers do rather than opening a 1280x720 window around a 1080p mode.
		int width = Engine_RefiiConfig.Int(values, "window_width", 0);
		int height = Engine_RefiiConfig.Int(values, "window_height", 0);

		if (width <= 0 || height <= 0)
		{
			width = Engine_RefiiConfig.Int(values, "video_mode_width", 0);
			height = Engine_RefiiConfig.Int(values, "video_mode_height", 0);
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
			Fullscreen = Engine_RefiiConfig.Bool(values, "fullscreen", false),
			Monitor = Math.Max(0, Engine_RefiiConfig.Int(values, "monitor", 0)),
			Vsync = Engine_RefiiConfig.Bool(values, "vsync", true),
			Source = source,
		};
	}

	public override string ToString() =>
		$"{Size.X}x{Size.Y} fullscreen={Fullscreen} monitor={Monitor} vsync={Vsync} ({Source})";
}
