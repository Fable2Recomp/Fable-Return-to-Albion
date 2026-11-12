namespace Refii.Tests.Core;

using System;
using GdUnit4;
using Refii.Core;
using static GdUnit4.Assertions;

/// <summary>
/// Verifies that <see cref="Engine_GuestPacer"/> maintains strict 1.0x real-time simulation
/// pacing (30.0 HF steps/s = 15.0 game ticks/s) across all host display rates and never produces
/// high-FPS fast-forward bursts after frame hitches.
/// </summary>
[TestSuite]
public sealed class GuestPacerTests
{
	[TestCase]
	public void SteadyFrameRatesFrom15To240Hz_MaintainExact30StepsPerSecond()
	{
		int[] refreshRates = { 15, 20, 24, 30, 45, 50, 59, 60, 75, 90, 120, 144, 165, 240 };
		const double durationSeconds = 10.0;
		const int expectedSteps = 300;

		foreach (int hz in refreshRates)
		{
			var pacer = new Engine_GuestPacer();
			double dt = 1.0 / hz;
			int frameCount = (int)Math.Round(durationSeconds * hz);
			double wallNow = 0.0;
			int totalSteps = 0;

			for (int i = 0; i < frameCount; ++i)
			{
				wallNow += dt;
				int granted = pacer.Advance(dt, wallNow);
				AssertThat(granted >= 0 && granted <= Engine_GuestPacer.MaxStepsPerFrame).IsTrue();
				totalSteps += granted;
			}

			// Across 10 seconds at any steady refresh rate (15..240 Hz), total steps must match 300 within +/-1 boundary step.
			AssertThat(Math.Abs(totalSteps - expectedSteps) <= 1)
				.OverrideFailureMessage($"hz={hz} produced {totalSteps} steps (expected {expectedSteps})")
				.IsTrue();
		}
	}

	[TestCase]
	public void ThirtyHzVsyncJitter_DoesNotDropHalfStepsOrBurst()
	{
		var pacer = new Engine_GuestPacer();
		// Alternate +/-6 ms around 33.333 ms (heavy 30 Hz VSync / compositor jitter).
		double[] pattern = { 0.0273333333, 0.0393333333, 0.0313333333, 0.0353333333 };
		double wallNow = 0.0;
		int totalSteps = 0;

		for (int i = 0; i < 300; ++i)
		{
			double dt = pattern[i % pattern.Length];
			wallNow += dt;
			totalSteps += pacer.Advance(dt, wallNow);
		}

		// 300 frames (10.000 s) at jittered 30 Hz stays exact at 299..301 steps (never halving or slowing down).
		AssertThat(totalSteps >= 299 && totalSteps <= 301).IsTrue();
	}

	[TestCase]
	public void FrameHitchFollowedBy240Fps_NeverBurstsCatchUpSteps()
	{
		var pacer = new Engine_GuestPacer();
		const double dt240 = 1.0 / 240.0; // 4.1667 ms
		double wallNow = 0.0;

		// Warm up for 1 second at 240 FPS.
		for (int i = 0; i < 240; ++i)
		{
			wallNow += dt240;
			pacer.Advance(dt240, wallNow);
		}

		// Inject a 500 ms hitch (>= HitchThresholdSeconds = 200 ms): grants 1 step and zeroes accumulator.
		wallNow += 0.500;
		int hitchGrant = pacer.Advance(0.500, wallNow);
		AssertThat(hitchGrant).IsEqual(1);
		AssertThat(pacer.AccumulatedSeconds).IsEqual(0.0);
		double hitchWallTime = wallNow;

		// Run the next 7 frames at 240 FPS (~29.17 ms < 33.33 ms): ZERO steps may be granted!
		int burstWindowSteps = 0;
		for (int i = 0; i < 7; ++i)
		{
			wallNow += dt240;
			burstWindowSteps += pacer.Advance(dt240, wallNow);
		}
		AssertThat(burstWindowSteps).IsEqual(0);

		// Continue for the remainder of 1 full second (233 more frames = 240 frames = 1.000 s post-hitch):
		// Total post-hitch grants across 1.000 s must be 30 (+/-1), NEVER banked catch-up steps!
		int postHitchOneSecondSteps = burstWindowSteps;
		double firstPostHitchGrantDelay = -1.0;
		for (int i = 7; i < 240; ++i)
		{
			wallNow += dt240;
			int step = pacer.Advance(dt240, wallNow);
			if (step > 0 && firstPostHitchGrantDelay < 0.0)
			{
				firstPostHitchGrantDelay = wallNow - hitchWallTime;
			}
			postHitchOneSecondSteps += step;
		}

		AssertThat(firstPostHitchGrantDelay >= Engine_GuestPacer.Period - 1e-6).IsTrue();
		AssertThat(postHitchOneSecondSteps >= 29 && postHitchOneSecondSteps <= 30).IsTrue();
	}

	[TestCase]
	public void SustainedSub30FpsFollowedBy240Fps_NeverBurstsCatchUpSteps()
	{
		var pacer = new Engine_GuestPacer();
		const double dt22 = 0.045; // ~22.2 FPS
		const double dt240 = 1.0 / 240.0;
		double wallNow = 0.0;

		int lowFpsSteps = 0;
		for (int i = 0; i < 20; ++i)
		{
			wallNow += dt22;
			lowFpsSteps += pacer.Advance(dt22, wallNow);
		}
		// 20 frames * 45 ms = 0.900 s -> 27 HF steps (26 granted + 0.999999999999996 * Period carried in IEEE-754).
		AssertThat(lowFpsSteps >= 26 && lowFpsSteps <= 27).IsTrue();
		double conservedSeconds = (lowFpsSteps * Engine_GuestPacer.Period) + pacer.AccumulatedSeconds;
		AssertThat(Math.Abs(conservedSeconds - 0.900) < 1e-9).IsTrue();

		// Recover immediately to 240 FPS for 1.0 second: must never exceed 30 (+1 boundary) steps.
		int recoverySteps = 0;
		for (int i = 0; i < 240; ++i)
		{
			wallNow += dt240;
			recoverySteps += pacer.Advance(dt240, wallNow);
		}

		AssertThat(recoverySteps >= 29 && recoverySteps <= 31).IsTrue();
	}

	[TestCase]
	public void ExtremeSubHitchSpike_CapsAtMaxStepsPerFrameAndClearsOverflow()
	{
		var pacer = new Engine_GuestPacer();

		// 180 ms is just under HitchThresholdSeconds (200 ms) and represents 5.4 steps:
		// caps at MaxStepsPerFrame (4) and clears overflow so the next frame starts clean.
		AssertThat(pacer.Advance(0.180, 0.180)).IsEqual(Engine_GuestPacer.MaxStepsPerFrame);
		AssertThat(pacer.AccumulatedSeconds).IsEqual(0.0);
		AssertThat(pacer.Advance(0.004, 0.184)).IsEqual(0);
	}

	[TestCase]
	public void WithholdGrant_HoldsPhaseNearOneWithoutRewindingOrBanking()
	{
		var pacer = new Engine_GuestPacer();
		AssertThat(pacer.Advance(0.020, 0.020)).IsEqual(0);
		double beforePhase = pacer.Phase;

		// Withhold across several frames totaling >100 ms: phase parks near 0.999 without rewinding,
		// and releasing withhold grants only 1 step rather than a banked burst.
		for (int i = 0; i < 5; ++i)
		{
			AssertThat(pacer.Advance(0.025, 0.045 + (i * 0.025), withholdGrant: true)).IsEqual(0);
			AssertThat(pacer.Phase >= beforePhase).IsTrue();
			AssertThat(pacer.Phase < 1.0).IsTrue();
		}

		AssertThat(pacer.Advance(0.004, 0.200, withholdGrant: false)).IsEqual(1);
		AssertThat(pacer.Advance(0.004, 0.204, withholdGrant: false)).IsEqual(0);
	}
}
