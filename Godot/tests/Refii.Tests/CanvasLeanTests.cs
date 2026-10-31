using System;
using System.Collections.Generic;
using System.Text.Json;
using GdUnit4;
using Refii.Game;
using Refii.Launcher.Core;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`canvas_lean`: when the touch controls may share the UI layer's pass, and the fade's invisible alpha.</summary>
[TestSuite]
public sealed class CanvasLeanTests
{
	private static Engine_CanvasLean.OtherLayer Layer(string name, int index, bool shows) => new(name, index, shows);

	[TestCase]
	public void TestNothingBetweenLetsTheControlsShare()
	{
		var others = new List<Engine_CanvasLean.OtherLayer>
		{
			Layer("FadeLayer", 7, false), Layer("LoadingLayer", 8, false), Layer("Letterbox", 4, true),
			Layer("DiagnosticButtons", 150, true),
		};
		AssertThat(Engine_CanvasLean.FirstBetween(5, 140, others)).IsNull();
	}

	[TestCase]
	public void TestALayerShowingBetweenKeepsThemApart()
	{
		var others = new List<Engine_CanvasLean.OtherLayer> { Layer("FadeLayer", 7, false), Layer("PhotoFilter", 120, true) };
		AssertThat(Engine_CanvasLean.FirstBetween(5, 140, others)).IsEqual("PhotoFilter");
	}

	[TestCase]
	public void TestALayerOnEitherIndexBlocks()
	{
		AssertThat(Engine_CanvasLean.FirstBetween(5, 140, new[] { Layer("Same5", 5, true) })).IsEqual("Same5");
		AssertThat(Engine_CanvasLean.FirstBetween(5, 140, new[] { Layer("Same140", 140, true) })).IsEqual("Same140");
	}

	[TestCase]
	public void TestTheUpperLayerMustBeAbove()
	{
		AssertThat(Engine_CanvasLean.FirstBetween(140, 5, Array.Empty<Engine_CanvasLean.OtherLayer>())).IsEqual("order");
		AssertThat(Engine_CanvasLean.FirstBetween(5, 5, Array.Empty<Engine_CanvasLean.OtherLayer>())).IsEqual("order");
	}

	[TestCase]
	public void TestOnlyAlphaUnderHalfAStepIsInvisible()
	{
		AssertThat(Engine_CanvasLean.Invisible(0f)).IsTrue();
		AssertThat(Engine_CanvasLean.Invisible(0.4f / 255f)).IsTrue();
		AssertThat(Engine_CanvasLean.Invisible(0.5f / 255f)).IsFalse();
		AssertThat(Engine_CanvasLean.Invisible(1f / 255f)).IsFalse();
		// A blend at the threshold over the largest possible difference moves under half an 8-bit step.
		float a = Engine_CanvasLean.InvisibleAlpha * 0.999f;
		AssertThat(MathF.Round(255f * (1f - a))).IsEqual(255f);
	}

	[TestCase]
	public void TestAPassLoadsAndStoresTheWholeTarget()
	{
		AssertThat(Engine_CanvasLean.PassBytes(2280, 1080, 4)).IsEqual(2L * 2280 * 1080 * 4);
		AssertThat(Engine_CanvasLean.PassBytes(2280, 1080, 8)).IsEqual(2L * 2 * 2280 * 1080 * 4);
		AssertThat(Engine_CanvasLean.PassBytes(-1, 1080, 4)).IsEqual(0L);
	}

	[TestCase]
	public void TestTheLeverIsReadOnlyAsABoolean()
	{
		using JsonDocument on = JsonDocument.Parse("{\"canvasLean\":true}");
		using JsonDocument off = JsonDocument.Parse("{\"canvasLean\":false}");
		using JsonDocument absent = JsonDocument.Parse("{\"fxFastPaths\":true}");
		using JsonDocument wrong = JsonDocument.Parse("{\"canvasLean\":1}");
		AssertThat(Engine_CanvasLean.ReadRequest(on.RootElement)).IsEqual(true);
		AssertThat(Engine_CanvasLean.ReadRequest(off.RootElement)).IsEqual(false);
		AssertThat(Engine_CanvasLean.ReadRequest(absent.RootElement)).IsNull();
		AssertThrown(() => Engine_CanvasLean.ReadRequest(wrong.RootElement)).IsInstanceOf<ArgumentException>();
	}

	[TestCase]
	public void TestTheSettingIsOffByDefaultAndOnInBothPresets()
	{
		AssertThat(GameWindowSettings.FromValues(new Dictionary<string, string>(), "test").CanvasLean).IsFalse();
		AssertThat(GameWindowSettings.FromValues(new Dictionary<string, string> { [Engine_CanvasLean.Key] = "true" }, "test").CanvasLean).IsTrue();
		AssertThat(GraphicsPresets.BalancedValues[Engine_CanvasLean.Key]).IsEqual("true");
		AssertThat(GraphicsPresets.PerformanceValues[Engine_CanvasLean.Key]).IsEqual("true");
	}
}
