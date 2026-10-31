namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;
using Refii.Core;

[TestSuite]
public class HudMeterInterpolationTests
{
	private sealed class TestUiSource : Engine_UiSource
	{
		public System.Collections.Generic.Dictionary<uint, float> Meters { get; } = new();

		public override System.Collections.Generic.Dictionary<uint, float> GetHudMeters() =>
			new(Meters);
	}

	[TestCase]
	public void PolledMeterGlidesAcross15HzIntervalWithoutFreezing()
	{
		var source = new TestUiSource();
		Engine_UiSource.Current = source;

		var values = new Engine_HudValues();
		var handle = Engine_HudValues.Handle.For("ExpressionFillPos");

		// Initial state: 0
		source.Meters[handle.Hash] = 0f;
		values.Read();

		AssertThat(values.Meter(handle)).IsEqual(0f);
		AssertBool(values.Seeking).IsFalse();

		// Tick 1: guest sets meter to 50 at t=0
		source.Meters[handle.Hash] = 50f;
		values.Read();

		// Right after read, Seeking is true, Current is at initial value 0
		AssertBool(values.Seeking).IsTrue();

		// Advance 33.3 ms (half of 15 Hz tick):
		// Previously it would have reached 50 and stopped Seeking here.
		// Now, with ~66.7 ms period, at 33.3 ms it should be around halfway (~25).
		values.Advance(1f / 30f);
		float atHalfway = values.Meter(handle) ?? 0f;
		AssertThat(atHalfway).IsGreater(15f);
		AssertThat(atHalfway).IsLess(35f);
		AssertBool(values.Seeking).IsTrue();

		// Advance remaining 33.3 ms (completing the 66.7 ms tick)
		values.Advance(1f / 30f);
		float atFull = values.Meter(handle) ?? 0f;
		AssertThat(atFull).IsGreaterEqual(48f);
		AssertThat(atFull).IsLessEqual(50f);
	}

	[TestCase]
	public void ContinuousUpdatesAdaptMeasuredPeriod()
	{
		var source = new TestUiSource();
		Engine_UiSource.Current = source;

		var values = new Engine_HudValues();
		var handle = Engine_HudValues.Handle.For("ExpressionFillPos");

		source.Meters[handle.Hash] = 10f;
		values.Read();

		// Simulate step 1
		source.Meters[handle.Hash] = 20f;
		values.Read();
		values.Advance(0.016f);
		AssertBool(values.Seeking).IsTrue();

		// Simulate step 2
		source.Meters[handle.Hash] = 30f;
		values.Read();
		values.Advance(0.016f);
		AssertBool(values.Seeking).IsTrue();

		float current = values.Meter(handle) ?? 0f;
		AssertThat(current).IsGreater(10f);
		AssertThat(current).IsLess(30f);
	}
}
