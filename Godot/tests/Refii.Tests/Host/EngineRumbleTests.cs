namespace Refii.Tests;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;

[TestSuite]
public class EngineRumbleTests
{
	[TestCase]
	public void TestLeftMotorIsStrongAndRightIsWeak()
	{
		var rumble = new Engine_Rumble();
		AssertThat(rumble.Observe(0xFFFF, 0)).IsEqual(Engine_Rumble.Verdict.Start);
		AssertThat(rumble.Strong).IsEqual(1f);
		AssertThat(rumble.Weak).IsEqual(0f);

		AssertThat(rumble.Observe(0, 0x8000)).IsEqual(Engine_Rumble.Verdict.Start);
		AssertThat(rumble.Strong).IsEqual(0f);
		AssertThat(System.Math.Abs(rumble.Weak - Engine_Rumble.Magnitude(Engine_Rumble.Quantise(0x8000))) < 1e-6f).IsTrue();
	}

	[TestCase]
	public void TestQuantumIsThePadsMotorByte()
	{
		// The 360 pad's report carries one byte per motor: 0x100 speeds a step, full scale stays full.
		AssertThat(Engine_Rumble.Quantise(0xFFFF)).IsEqual(0xFFFFu);
		AssertThat(Engine_Rumble.Quantise(0xFF00)).IsEqual(0xFFFFu);
		AssertThat(Engine_Rumble.Quantise(0x100)).IsEqual(0x101u);
		AssertThat(Engine_Rumble.Quantise(0x1FF)).IsEqual(0x101u);
		AssertThat(Engine_Rumble.Quantise(0xFF)).IsEqual(0u);
		AssertThat(Engine_Rumble.Quantise(0x1FFFF)).IsEqual(0xFFFFu);
	}

	[TestCase]
	public void TestChangeWithinOneStepIsUnchanged()
	{
		var rumble = new Engine_Rumble();
		AssertThat(rumble.Observe(0x1000, 0x2000)).IsEqual(Engine_Rumble.Verdict.Start);
		// A decay that has not yet moved the motor's byte restarts nothing.
		AssertThat(rumble.Observe(0x10FF, 0x20FF)).IsEqual(Engine_Rumble.Verdict.Unchanged);
		AssertThat(rumble.Observe(0x1001, 0x2001)).IsEqual(Engine_Rumble.Verdict.Unchanged);
		AssertThat(rumble.Left).IsEqual(Engine_Rumble.Quantise(0x1000));
	}

	[TestCase]
	public void TestCrossingAStepRestarts()
	{
		var rumble = new Engine_Rumble();
		rumble.Observe(0x1000, 0x2000);
		AssertThat(rumble.Observe(0x0FFF, 0x2000)).IsEqual(Engine_Rumble.Verdict.Start);
		AssertThat(rumble.Left).IsEqual(Engine_Rumble.Quantise(0x0FFF));
	}

	[TestCase]
	public void TestDecayUnderOneStepIsTheFinalStop()
	{
		var rumble = new Engine_Rumble();
		rumble.Observe(0x300, 0);
		AssertThat(rumble.Observe(0x200, 0)).IsEqual(Engine_Rumble.Verdict.Start);
		// Under a step the console's motor was off: the stop is never dropped.
		AssertThat(rumble.Observe(0x80, 0)).IsEqual(Engine_Rumble.Verdict.Stop);
		AssertThat(rumble.Running).IsFalse();
		AssertThat(rumble.Observe(0x7F, 0)).IsEqual(Engine_Rumble.Verdict.Unchanged);
	}

	[TestCase]
	public void TestMagnitudeClampsAboveFullScale()
	{
		AssertThat(Engine_Rumble.Magnitude(0x1FFFF)).IsEqual(1f);
		AssertThat(Engine_Rumble.Magnitude(0)).IsEqual(0f);
	}

	[TestCase]
	public void TestSilenceWhileSilentIsUnchanged()
	{
		var rumble = new Engine_Rumble();
		AssertThat(rumble.Observe(0, 0)).IsEqual(Engine_Rumble.Verdict.Unchanged);
		AssertThat(rumble.Silence()).IsEqual(Engine_Rumble.Verdict.Unchanged);
		AssertThat(rumble.Running).IsFalse();
	}

	[TestCase]
	public void TestSteadyPairIsToldOnce()
	{
		var rumble = new Engine_Rumble();
		AssertThat(rumble.Observe(1000, 2000)).IsEqual(Engine_Rumble.Verdict.Start);
		AssertThat(rumble.Observe(1000, 2000)).IsEqual(Engine_Rumble.Verdict.Unchanged);
		AssertThat(rumble.Observe(1000, 2000)).IsEqual(Engine_Rumble.Verdict.Unchanged);
		AssertThat(rumble.Running).IsTrue();
	}

	[TestCase]
	public void TestChangedPairRestarts()
	{
		var rumble = new Engine_Rumble();
		rumble.Observe(1000, 2000);
		AssertThat(rumble.Observe(500, 2000)).IsEqual(Engine_Rumble.Verdict.Start);
		AssertThat(rumble.Left).IsEqual(Engine_Rumble.Quantise(500));
		AssertThat(rumble.Right).IsEqual(Engine_Rumble.Quantise(2000));
	}

	[TestCase]
	public void TestFirstSilentFrameStopsThenStaysQuiet()
	{
		var rumble = new Engine_Rumble();
		rumble.Observe(1000, 2000);
		AssertThat(rumble.Observe(0, 0)).IsEqual(Engine_Rumble.Verdict.Stop);
		AssertThat(rumble.Running).IsFalse();
		AssertThat(rumble.Observe(0, 0)).IsEqual(Engine_Rumble.Verdict.Unchanged);
		AssertThat(rumble.Left).IsEqual(0u);
		AssertThat(rumble.Right).IsEqual(0u);
	}

	[TestCase]
	public void TestSilenceStopsARunOnceAndRestartsAfter()
	{
		var rumble = new Engine_Rumble();
		rumble.Observe(1000, 2000);
		AssertThat(rumble.Silence()).IsEqual(Engine_Rumble.Verdict.Stop);
		AssertThat(rumble.Silence()).IsEqual(Engine_Rumble.Verdict.Unchanged);
		// Focus back with the guest still stating the same pair: the joypad must be told again.
		AssertThat(rumble.Observe(1000, 2000)).IsEqual(Engine_Rumble.Verdict.Start);
	}
}
