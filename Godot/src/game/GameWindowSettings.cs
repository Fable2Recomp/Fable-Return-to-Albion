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

	/// <summary>Frame cap; 0 is uncapped. The sim's fixed step is unaffected.</summary>
	public int MaxFps { get; init; }

	/// <summary>3D render scale as a percentage of the window; the UI stays native.</summary>
	public int RenderScalePercent { get; init; }

	/// <summary>MSAA samples on the 3D world: 0, 2, 4 or 8.</summary>
	public int Msaa { get; init; }

	/// <summary>Whether the window drops its title bar and border.</summary>
	public bool Borderless { get; init; }

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
				RenderScalePercent = 100,
				Source = "defaults (no refii.toml found)",
			};
		}

		IReadOnlyDictionary<string, string> values = Engine_RefiiConfig.Read(out _);

		// `display_resolution` IS THE SIZE — one "WIDTHxHEIGHT" string chosen from the launcher's
		// aspect and resolution dropdowns, rather than a width and a height typed separately and
		// free to disagree.
		//
		// The old keys are still read underneath it, in order, so a `refii.toml` written before
		// the dropdowns existed still opens the window it asks for: `window_width`/`_height`
		// were the window, `video_mode_*` the guest's video mode.
		int width = 0;
		int height = 0;
		string chosen = Engine_RefiiConfig.String(values, "display_resolution", string.Empty);
		int cross = chosen.IndexOf('x');
		if (cross > 0 &&
			int.TryParse(chosen[..cross], out int pickedWidth) &&
			int.TryParse(chosen[(cross + 1)..], out int pickedHeight))
		{
			width = pickedWidth;
			height = pickedHeight;
		}

		if (width <= 0 || height <= 0)
		{
			width = Engine_RefiiConfig.Int(values, "window_width", 0);
			height = Engine_RefiiConfig.Int(values, "window_height", 0);
		}

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
			MaxFps = Math.Max(0, Engine_RefiiConfig.Int(values, "max_fps", 0)),

			// Clamped to what Godot's 3D scaling accepts. A zero here would render nothing at
			// all, and the file is user-editable.
			RenderScalePercent = Math.Clamp(
				Engine_RefiiConfig.Int(values, "render_scale_percent", 100), 25, 200),
			Msaa = Engine_RefiiConfig.Int(values, "msaa", 0),
			Borderless = Engine_RefiiConfig.Bool(values, "borderless", false),
			Source = source,
		};
	}

	public override string ToString() =>
		$"{Size.X}x{Size.Y} fullscreen={Fullscreen} borderless={Borderless} monitor={Monitor} " +
		$"vsync={Vsync} maxFps={MaxFps} renderScale={RenderScalePercent}% msaa={Msaa}x ({Source})";
}
