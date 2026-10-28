namespace Refii.Tests;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;

[TestSuite]
public class EngineHandheldRumbleTests
{
	[TestCase]
	public void TestLevelIsTheStrongerMotorOnAStep()
	{
		AssertThat(Engine_HandheldRumble.Level(0xFFFF, 0)).IsEqual(1f);
		AssertThat(Engine_HandheldRumble.Level(0, 0xFFFF)).IsEqual(1f);
		AssertThat(Engine_HandheldRumble.Level(0x8000, 0x2000)).IsEqual(Engine_HandheldRumble.Level(0x2000, 0x8000));
		// The faintest stated rumble is still the least pulse; under one pad step is silence.
		AssertThat(Engine_HandheldRumble.Level(0x100, 0)).IsEqual(1f / Engine_HandheldRumble.Steps);
		AssertThat(Engine_HandheldRumble.Level(0xFF, 0xFF)).IsEqual(0f);
		AssertThat(Engine_HandheldRumble.Level(0, 0)).IsEqual(0f);
		// A half-speed motor is half strength, on a step.
		AssertThat(Engine_HandheldRumble.Level(0x8000, 0)).IsEqual(0.5f);
	}

	[TestCase]
	public void TestStartPulsesAtOnceAndSilenceStopsOnce()
	{
		var rumble = new Engine_HandheldRumble();
		AssertThat(rumble.Observe(0, 0, 1000)).IsEqual(Engine_HandheldRumble.Verdict.Unchanged);
		AssertThat(rumble.Observe(0xFFFF, 0x4000, 1000)).IsEqual(Engine_HandheldRumble.Verdict.Pulse);
		AssertThat(rumble.Running).IsTrue();
		AssertThat(rumble.Amplitude).IsEqual(1f);

		// Silence is told the same frame, once, and then nothing while it stays silent.
		AssertThat(rumble.Observe(0, 0, 1010)).IsEqual(Engine_HandheldRumble.Verdict.Stop);
		AssertThat(rumble.Running).IsFalse();
		AssertThat(rumble.Amplitude).IsEqual(0f);
		AssertThat(rumble.Observe(0, 0, 1020)).IsEqual(Engine_HandheldRumble.Verdict.Unchanged);
		AssertThat(rumble.Silence()).IsEqual(Engine_HandheldRumble.Verdict.Unchanged);

		// A new hit straight after a stop pulses at once: no gap applies to a start.
		AssertThat(rumble.Observe(0x8000, 0, 1030)).IsEqual(Engine_HandheldRumble.Verdict.Pulse);
	}

	[TestCase]
	public void TestSteadyLevelRenewsBeforeThePulseEnds()
	{
		var rumble = new Engine_HandheldRumble();
		AssertThat(rumble.Observe(0x8000, 0, 0)).IsEqual(Engine_HandheldRumble.Verdict.Pulse);
		int pulses = 1;
		// Two seconds of a steady pair at 30 frames a second.
		for (ulong now = 33; now <= 2000; now += 33)
		{
			if (rumble.Observe(0x8000, 0, now) == Engine_HandheldRumble.Verdict.Pulse)
			{
				++pulses;
			}
		}

		// Renewed every RenewMs plus at most a frame, and never left to lapse.
		AssertThat(pulses >= 2000 / (Engine_HandheldRumble.RenewMs + 33)).IsTrue();
		AssertThat(pulses <= 2000 / Engine_HandheldRumble.RenewMs + 1).IsTrue();
		AssertThat(Engine_HandheldRumble.RenewMs + 33 < Engine_HandheldRumble.PulseMs).IsTrue();
	}

	[TestCase]
	public void TestDecayIsToldAtMostEveryGap()
	{
		var rumble = new Engine_HandheldRumble();
		AssertThat(rumble.Observe(0xFFFF, 0, 0)).IsEqual(Engine_HandheldRumble.Verdict.Pulse);
		// A level change inside the gap waits; the joypad path would have restarted every frame.
		AssertThat(rumble.Observe(0x4000, 0, 16)).IsEqual(Engine_HandheldRumble.Verdict.Unchanged);
		AssertThat(rumble.Amplitude).IsEqual(1f);
		AssertThat(rumble.Observe(0x4000, 0, Engine_HandheldRumble.MinGapMs)).IsEqual(Engine_HandheldRumble.Verdict.Pulse);
		AssertThat(rumble.Amplitude).IsEqual(Engine_HandheldRumble.Level(0x4000, 0));

		// A frame-by-frame decay from full to nothing over a second at 60 FPS.
		rumble = new Engine_HandheldRumble();
		int calls = 0;
		for (ulong frame = 0; frame <= 60; ++frame)
		{
			uint speed = (uint)(0xFFFF * (60 - frame) / 60);
			if (rumble.Observe(speed, 0, frame * 1000 / 60) != Engine_HandheldRumble.Verdict.Unchanged)
			{
				++calls;
			}
		}

		AssertThat(calls <= 1000 / Engine_HandheldRumble.MinGapMs + 2).IsTrue();
		AssertThat(rumble.Running).IsFalse();
	}

	[TestCase]
	public void TestChangeWithinAStepIsUnchanged()
	{
		var rumble = new Engine_HandheldRumble();
		rumble.Observe(0x8000, 0x1000, 0);
		// Both still round to the same strength: nothing to tell, however long past the gap.
		AssertThat(rumble.Observe(0x8100, 0x2000, Engine_HandheldRumble.MinGapMs + 10)).IsEqual(Engine_HandheldRumble.Verdict.Unchanged);
	}

	[TestCase]
	public void TestStopAndPulseFitAndroidsVibrator()
	{
		// Godot drops a zero duration and Android takes amplitudes 1..255: the stop is the least of both.
		AssertThat(Engine_HandheldRumble.StopMs).IsEqual(1);
		AssertThat((int)(Engine_HandheldRumble.StopAmplitude * 255 + 0.5f)).IsEqual(1);
		AssertThat(Engine_HandheldRumble.MinGapMs < Engine_HandheldRumble.RenewMs).IsTrue();
	}
}
