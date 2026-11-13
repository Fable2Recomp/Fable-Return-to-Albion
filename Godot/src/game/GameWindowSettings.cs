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
public readonly partial struct GameWindowSettings
{
	/// <summary>Fallback when the file says nothing. Matches the guest's own default mode.</summary>
	public static readonly Vector2I DefaultSize = new(1280, 720);

	public Vector2I Size { get; init; }

	public bool Fullscreen { get; init; }

	public int Monitor { get; init; }

	public bool Vsync { get; init; }

	/// <summary>`vsync_mode`: mailbox, on or adaptive; only read when <see cref="Vsync"/> is true.</summary>
	public string VsyncMode { get; init; }

	/// <summary>Frame cap; 0 is uncapped. The sim's fixed step is unaffected.</summary>
	public int MaxFps { get; init; }

	/// <summary>`draw_latency_ticks`: a pinned draw latency in game ticks; 0 follows the frame time.</summary>
	public float DrawLatencyTicks { get; init; }

	/// <summary>`mesh_lod_pixels`: the viewport's mesh LOD threshold in pixels.</summary>
	public float MeshLodPixels { get; init; }

	/// <summary>`sun_shadow_map`: the directional shadow atlas size in pixels.</summary>
	public int SunShadowMap { get; init; }

	/// <summary>3D render scale as a percentage of the window; the UI stays native.</summary>
	public int RenderScalePercent { get; init; }

	/// <summary>MSAA samples on the 3D world: 0, 2, 4 or 8.</summary>
	public int Msaa { get; init; }

	/// <summary>`render_scaler`: bilinear, fsr, fsr2 or nearest — how the 3D world is scaled to the window.</summary>
	public string Scaler { get; init; }

	/// <summary>`fsr_quality`: an AMD quality mode, or custom for <see cref="RenderScalePercent"/>.</summary>
	public string FsrQuality { get; init; }

	/// <summary>`fsr_sharpness`: 0 is the sharpest, up to 2.</summary>
	public float FsrSharpness { get; init; }

	/// <summary>`texture_mipmap_bias`: added to Godot's own log2(scale) bias.</summary>
	public float MipmapBias { get; init; }

	/// <summary>`screen_space_aa`: off, fxaa or smaa.</summary>
	public string ScreenSpaceAa { get; init; }

	/// <summary>`taa`: temporal anti-aliasing; never with FSR 2.2, which is its own.</summary>
	public bool Taa { get; init; }

	/// <summary>`anisotropic_filtering`: 0, 2, 4, 8 or 16 samples.</summary>
	public int Anisotropic { get; init; }

	/// <summary>`debanding`: noise over smooth gradients.</summary>
	public bool Debanding { get; init; }

	/// <summary>`hdr_output`: request HDR output; SDR where the display has none.</summary>
	public bool Hdr { get; init; }

	/// <summary>`hdr_reference_nits`: white in HDR, in nits; 0 follows the OS.</summary>
	public float HdrReferenceNits { get; init; }

	/// <summary>`hdr_peak_nits`: the display's peak in nits; 0 takes what it reports.</summary>
	public float HdrPeakNits { get; init; }

	/// <summary>Whether the window drops its title bar and border.</summary>
	public bool Borderless { get; init; }

	/// <summary>
	/// True when <c>display_resolution</c> is set to <c>"native"</c>, allowing 3D internal resolution
	/// to grow with maximized or resized OS windows instead of anchoring to <see cref="Size"/>.
	/// </summary>
	public bool MatchWindowResolution { get; init; }

	/// <summary>Where the values came from, for the log line that explains the window.</summary>
	public string Source { get; init; }

	/// <summary>Loads the conventional runtime configuration using the shared path and read cache.</summary>
	/// <returns>The configured settings, or the existing no-file defaults.</returns>
	public static GameWindowSettings Load()
	{
		string path = Engine_RefiiConfig.ResolvePath();
		IReadOnlyDictionary<string, string> values = path.Length == 0
			? new Dictionary<string, string>()
			: Engine_RefiiConfig.Read(out _);
		return FromValues(values, path);
	}

	/// <summary>Parses the shared runtime settings without locating files or calling native Godot APIs.</summary>
	/// <param name="values">Values read by the runtime TOML parser.</param>
	/// <param name="path">The source configuration path; empty preserves the historical no-file defaults.</param>
	/// <returns>The same normalized settings used by the desktop window and Android render-scale policy.</returns>
	public static GameWindowSettings FromValues(IReadOnlyDictionary<string, string> values, string path)
	{
		ArgumentNullException.ThrowIfNull(values);
		ArgumentNullException.ThrowIfNull(path);
		if (path.Length == 0)
		{
			return new GameWindowSettings
			{
				Size = DefaultSize,
				Vsync = true,
				RenderScalePercent = 100,
				Scaler = "bilinear",
				FsrQuality = "custom",
				FsrSharpness = 0.2f,
				ScreenSpaceAa = "off",
				Anisotropic = 4,
				Source = "defaults (no " + Refii.Core.Engine_Title.Active.ConfigFileName + " found)",
			};
		}

		// `display_resolution` IS THE SIZE — one "WIDTHxHEIGHT" string chosen from the launcher's
		// aspect and resolution dropdowns, rather than a width and a height typed separately and
		// free to disagree.
		//
		// The old keys are still read underneath it, in order, so a `refii.toml` written before
		// the dropdowns existed still opens the window it asks for: `window_width`/`_height`
		// were the window, `video_mode_*` the guest's video mode.
		int width = 0;
		int height = 0;
		string chosen = Engine_RefiiConfig.String(values, "display_resolution", string.Empty).Trim();
		bool matchWindow = chosen.Equals("native", StringComparison.OrdinalIgnoreCase);
		int cross = chosen.IndexOf('x');
		if (!matchWindow && cross > 0 &&
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
			// One switch in the panel: vsync_mode = "off". The runtime's `vsync` bool is no
			// longer offered, but a file that already says false is still honoured.
			Vsync = Engine_RefiiConfig.Bool(values, "vsync", true)
				&& !Engine_RefiiConfig.String(values, "vsync_mode", "mailbox").Trim()
					.Equals("off", StringComparison.OrdinalIgnoreCase),
			VsyncMode = Engine_RefiiConfig.String(values, "vsync_mode", "mailbox").Trim().ToLowerInvariant(),
			MaxFps = Math.Max(0, Engine_RefiiConfig.Int(values, "max_fps", 0)),
			DrawLatencyTicks = Engine_RefiiConfig.Float(values, "draw_latency_ticks", 0f),
			MeshLodPixels = Math.Clamp(Engine_RefiiConfig.Float(values, "mesh_lod_pixels", 1f), 0f, 64f),
			SunShadowMap = Math.Clamp(Engine_RefiiConfig.Int(values, "sun_shadow_map", 4096), 512, 16384),

			// Clamped to what Godot's 3D scaling accepts. A zero here would render nothing at
			// all, and the file is user-editable.
			RenderScalePercent = Math.Clamp(
				Engine_RefiiConfig.Int(values, "render_scale_percent", 100), 25, 200),
			Msaa = Engine_RefiiConfig.Int(values, "msaa", 0),
			Scaler = Engine_RefiiConfig.String(values, "render_scaler", "bilinear").Trim().ToLowerInvariant(),
			FsrQuality = Engine_RefiiConfig.String(values, "fsr_quality", "custom").Trim().ToLowerInvariant(),
			FsrSharpness = Math.Clamp(Engine_RefiiConfig.Float(values, "fsr_sharpness", 0.2f), 0f, 2f),
			MipmapBias = Math.Clamp(Engine_RefiiConfig.Float(values, "texture_mipmap_bias", 0f), -2f, 2f),
			ScreenSpaceAa = Engine_RefiiConfig.String(values, "screen_space_aa", "off").Trim().ToLowerInvariant(),
			Taa = Engine_RefiiConfig.Bool(values, "taa", false),
			Anisotropic = Engine_RefiiConfig.Int(values, "anisotropic_filtering", 4),
			Debanding = Engine_RefiiConfig.Bool(values, "debanding", false),
			Hdr = Engine_RefiiConfig.Bool(values, "hdr_output", false),
			HdrReferenceNits = Math.Max(0f, Engine_RefiiConfig.Float(values, "hdr_reference_nits", 0f)),
			HdrPeakNits = Math.Max(0f, Engine_RefiiConfig.Float(values, "hdr_peak_nits", 0f)),
			Borderless = Engine_RefiiConfig.Bool(values, "borderless", false),
			MatchWindowResolution = matchWindow,
			Source = source,
		}.WithAndroidHostValues(values); // hdr_2d and canvas_lean: GameWindowSettings.Android.cs
	}

	/// <summary>
	/// Computes the 3D internal resolution scale and mesh LOD threshold for the live OS window size,
	/// keeping 3D pixel count anchored to <see cref="Size"/> when maximized, resized larger, or fullscreen
	/// unless <see cref="MatchWindowResolution"/> is enabled.
	/// </summary>
	/// <param name="actualWindowSize">Live root viewport / OS window dimensions in pixels.</param>
	/// <returns>Clamped <c>Scaling3DScale</c> and height-proportional <c>MeshLodThreshold</c>.</returns>
	public (float Scaling3D, float MeshLodThreshold) ComputeViewport3DParameters(Vector2I actualWindowSize)
	{
		float windowScale = !MatchWindowResolution
			&& actualWindowSize.Y > 0
			&& Size.Y > 0
			&& actualWindowSize.Y > Size.Y
			? (float)Size.Y / actualWindowSize.Y
			: 1f;
		float scaling3D = Mathf.Clamp(RenderScale * windowScale, 0.25f, 2.0f);
		float hostHeight = actualWindowSize.Y > 0 ? actualWindowSize.Y : Size.Y;
		float effective3DHeight = !MatchWindowResolution && Size.Y > 0 && hostHeight > Size.Y
			? Size.Y
			: hostHeight;
		float lodThreshold = MeshLodPixels * (effective3DHeight / Refii.Models.Engine_MdlLods.ReferenceHeight);
		return (scaling3D, lodThreshold);
	}

	/// <summary>
	/// Effective 3D quality configuration for the root viewport given whether a 3D world is active.
	/// When no 3D world is mounted (startup videos, title, main menu), root 3D scalers/MSAA/SSAA/TAA
	/// stay disabled so the root viewport does not allocate or run extra full-screen passes.
	/// Pure logic safe for headless unit tests.
	/// </summary>
	public (
		Viewport.Scaling3DModeEnum ScalingMode,
		float ScalingScale,
		float MeshLodThreshold,
		Viewport.Msaa MsaaMode,
		Viewport.ScreenSpaceAAEnum SsaaMode,
		bool UseTaa) ResolveRoot3DQuality(Vector2I actualWindowSize, bool activeWorld)
	{
		if (!activeWorld)
		{
			return (
				Viewport.Scaling3DModeEnum.Bilinear,
				1.0f,
				MeshLodPixels,
				Viewport.Msaa.Disabled,
				Viewport.ScreenSpaceAAEnum.Disabled,
				false);
		}

		Viewport.Msaa msaaMode = Msaa switch
		{
			>= 8 => Viewport.Msaa.Msaa8X,
			>= 4 => Viewport.Msaa.Msaa4X,
			>= 2 => Viewport.Msaa.Msaa2X,
			_ => Viewport.Msaa.Disabled,
		};

		Viewport.Scaling3DModeEnum scalingMode = Scaler switch
		{
			"fsr" => Viewport.Scaling3DModeEnum.Fsr,
			"fsr2" => Viewport.Scaling3DModeEnum.Fsr2,
			"nearest" => Viewport.Scaling3DModeEnum.Nearest,
			_ => Viewport.Scaling3DModeEnum.Bilinear,
		};

		(float scaling3D, float lodThreshold) = ComputeViewport3DParameters(actualWindowSize);

		Viewport.ScreenSpaceAAEnum ssaaMode = ScreenSpaceAa switch
		{
			"fxaa" => Viewport.ScreenSpaceAAEnum.Fxaa,
			"smaa" => Viewport.ScreenSpaceAAEnum.Smaa,
			_ => Viewport.ScreenSpaceAAEnum.Disabled,
		};

		bool useTaa = Taa && scalingMode != Viewport.Scaling3DModeEnum.Fsr2;
		return (scalingMode, scaling3D, lodThreshold, msaaMode, ssaaMode, useTaa);
	}

	/// <summary>
	/// The 3D render scale: AMD's ratio for the chosen FSR quality mode (1.3x Ultra Quality,
	/// 1.5x Quality, 1.7x Balanced, 2x Performance, 3x Ultra Performance), else
	/// <see cref="RenderScalePercent"/>.
	/// </summary>
	public float RenderScale =>
		Scaler is "fsr" or "fsr2" ? FsrQuality switch
		{
			"native" => 1.0f,
			"ultra_quality" => 1.0f / 1.3f,
			"quality" => 1.0f / 1.5f,
			"balanced" => 1.0f / 1.7f,
			"performance" => 0.5f,
			"ultra_performance" => 1.0f / 3.0f,
			_ => RenderScalePercent / 100.0f,
		}
		: RenderScalePercent / 100.0f;

	public override string ToString() =>
		$"{Size.X}x{Size.Y} fullscreen={Fullscreen} borderless={Borderless} monitor={Monitor} " +
		$"vsync={Vsync}/{VsyncMode} maxFps={MaxFps} renderScale={RenderScale * 100f:0}% scaler={Scaler}/{FsrQuality} msaa={Msaa}x " +
		$"ssaa={ScreenSpaceAa} taa={Taa} aniso={Anisotropic}x hdr={Hdr} ({Source})";
}
