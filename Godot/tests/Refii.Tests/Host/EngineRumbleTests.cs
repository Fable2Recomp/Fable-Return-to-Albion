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
		AssertThat(System.Math.Abs(rumble.Weak - 0x8000 / 65535f) < 1e-6f).IsTrue();
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
		AssertThat(rumble.Left).IsEqual(500u);
		AssertThat(rumble.Right).IsEqual(2000u);
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
