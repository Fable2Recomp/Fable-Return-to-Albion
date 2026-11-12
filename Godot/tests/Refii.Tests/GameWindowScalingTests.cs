using System.Collections.Generic;
using GdUnit4;
using Godot;
using Refii.Game;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// Verifies 3D viewport scaling across maximized high-DPI windows, explicit target resolutions,
/// <c>display_resolution = "native"</c> (Match Window), and FSR quality modes.
/// </summary>
[TestSuite]
public sealed class GameWindowScalingTests
{
	[TestCase]
	public void Windowed1280x720MaximizedTo2880x1800_Clamps3DBufferTo720pHeight()
	{
		var settings = GameWindowSettings.FromValues(new Dictionary<string, string>
		{
			["display_resolution"] = "1280x720",
			["fullscreen"] = "false",
			["borderless"] = "false",
			["render_scaler"] = "bilinear",
			["render_scale_percent"] = "100",
			["mesh_lod_pixels"] = "1.0",
		}, "refii.toml");

		AssertThat(settings.MatchWindowResolution).IsFalse();
		(float scale, float lodThreshold) = settings.ComputeViewport3DParameters(new Vector2I(2880, 1800));
		// 720 / 1800 = 0.40 -> 1152x720 (~0.83M px) instead of 2880x1800 (5.18M px)!
		AssertThat(Mathf.Abs(scale - 0.40f) < 1e-4f).IsTrue();
		AssertThat(lodThreshold > 0f).IsTrue();
	}

	[TestCase]
	public void MatchWindowResolution_RendersFullWindowTimesScalePercent()
	{
		var settings = GameWindowSettings.FromValues(new Dictionary<string, string>
		{
			["display_resolution"] = "native",
			["fullscreen"] = "false",
			["render_scaler"] = "bilinear",
			["render_scale_percent"] = "100",
		}, "refii.toml");

		AssertThat(settings.MatchWindowResolution).IsTrue();
		(float scale, _) = settings.ComputeViewport3DParameters(new Vector2I(2880, 1800));
		AssertThat(Mathf.Abs(scale - 1.0f) < 1e-4f).IsTrue();
	}

	[TestCase]
	public void MatchWindowWithFsrUltraQuality_UsesActualWindowResolutionTimesFsrRatio()
	{
		var settings = GameWindowSettings.FromValues(new Dictionary<string, string>
		{
			["display_resolution"] = "native",
			["fullscreen"] = "true",
			["render_scaler"] = "fsr",
			["fsr_quality"] = "ultra_quality",
		}, "refii.toml");

		AssertThat(settings.Scaler).IsEqual("fsr");
		(float scale, _) = settings.ComputeViewport3DParameters(new Vector2I(2880, 1800));
		AssertThat(Mathf.Abs(scale - (1.0f / 1.3f)) < 1e-4f).IsTrue();
	}

	[TestCase]
	public void WindowSmallerThanTargetResolution_DoesNotSupersampleAccidentally()
	{
		var settings = GameWindowSettings.FromValues(new Dictionary<string, string>
		{
			["display_resolution"] = "1920x1080",
			["fullscreen"] = "false",
			["render_scaler"] = "bilinear",
			["render_scale_percent"] = "100",
		}, "refii.toml");

		(float scale, _) = settings.ComputeViewport3DParameters(new Vector2I(1280, 720));
		AssertThat(Mathf.Abs(scale - 1.0f) < 1e-4f).IsTrue();
	}
}
