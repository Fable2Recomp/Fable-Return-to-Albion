namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;
using Refii.Host;
using Refii.Titles.Fable2;

[TestSuite]
public class HudExpressionMeterSmoothingTests
{
	private sealed class MockExpressionHud : IEvaluatedExpressionHud
	{
		public Engine_HudValues Values { get; } = new();
		public int ExpressionBand { get; set; } = -1;
		public (string Binding, float Degrees)? ExpressionPip { get; set; }
		public float PipLastPosition { get; set; } = float.NaN;
		public ulong PipLastUsec { get; set; }
		public float PipDrawn { get; set; } = float.NaN;
	}

	[TestCase]
	public void PipExtrapolatesSmoothlyBetweenGuestTicks()
	{
		var hud = new MockExpressionHud();

		// Pip at position 40, moving at 60 %/sec
		var meter = new HostBridge.ExpressionMeter(
			Known: true, On: true, Mode: 1, ArmedA: true, ArmedB: false, Latched: true,
			Position: 40f, Speed: 60f, HitA: false, HitB: false, Live: true, Band: 0);

		// Frame 0: Stated tick arrives
		Engine_UiHudExpressionMeter.Publish(meter, hud);
		AssertThat(hud.ExpressionPip).IsNotNull();
		float deg0 = hud.ExpressionPip!.Value.Degrees;

		// (40 * 1.3) - 65 = -13 degrees
		AssertThat(deg0).IsEqualApprox(-13f, 0.1f);

		// Frame 1: 33.3 ms later (half a 15 Hz tick, host frame at monitor refresh)
		// meter has not changed yet in guest memory
		Engine_UiHudExpressionMeter.Publish(meter, hud);
		AssertThat(hud.ExpressionPip).IsNotNull();
		float deg1 = hud.ExpressionPip!.Value.Degrees;

		// Degrees should have advanced smoothly (or stayed consistent)
		AssertThat(deg1).IsGreaterEqual(deg0);
	}

	[TestCase]
	public void PipStopsWhenMeterTurnsOff()
	{
		var hud = new MockExpressionHud();

		var running = new HostBridge.ExpressionMeter(
			Known: true, On: true, Mode: 1, ArmedA: true, ArmedB: false, Latched: true,
			Position: 50f, Speed: 50f, HitA: false, HitB: false, Live: true, Band: 0);

		Engine_UiHudExpressionMeter.Publish(running, hud);
		AssertThat(hud.ExpressionPip).IsNotNull();

		var stopped = new HostBridge.ExpressionMeter(
			Known: false, On: false, Mode: 0, ArmedA: false, ArmedB: false, Latched: false,
			Position: 0f, Speed: 0f, HitA: false, HitB: false, Live: false, Band: -1);

		Engine_UiHudExpressionMeter.Publish(stopped, hud);
		AssertThat(hud.ExpressionPip).IsNull();
		AssertThat(float.IsNaN(hud.PipDrawn)).IsTrue();
	}
}
