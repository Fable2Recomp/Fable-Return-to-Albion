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
	public void SteadyFrameRatesFrom30To240Hz_MaintainExact30StepsPerSecond()
	{
		int[] refreshRates = { 30, 45, 50, 59, 60, 75, 90, 120, 144, 165, 240 };
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
				AssertThat(granted >= 0 && granted <= 1).IsTrue();
				totalSteps += granted;
			}

			// Across 10 seconds at any steady refresh rate >= 30 Hz, total steps must match 300 within +/-1 boundary step.
			AssertThat(Math.Abs(totalSteps - expectedSteps) <= 1).IsTrue();
		}
	}

	[TestCase]
	public void ThirtyHzVsyncJitter_DoesNotDropHalfStepsOrBurst()
	{
		var pacer = new Engine_GuestPacer();
		// Alternate +/-1.2 ms around 33.333 ms (typical 30 Hz VSync presentation jitter).
		double[] pattern = { 0.0321, 0.0345666667, 0.0330, 0.0336666667 };
		double wallNow = 0.0;
		int totalSteps = 0;

		for (int i = 0; i < 300; ++i)
		{
			double dt = pattern[i % pattern.Length];
			wallNow += dt;
			totalSteps += pacer.Advance(dt, wallNow);
		}

		// 300 frames (~10.0 s) at jittered 30 Hz should stay within 298..301 steps (never halving to 150).
		AssertThat(totalSteps >= 298 && totalSteps <= 301).IsTrue();
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

		// Inject a 160 ms hitch (previously banked 4 catch-up steps that drained in 16 ms at ~8x speed!).
		wallNow += 0.160;
		int hitchGrant = pacer.Advance(0.160, wallNow);
		AssertThat(hitchGrant).IsEqual(1);
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
		// Total post-hitch grants across 1.000 s must be 30 (+/-1), NEVER 33-34 banked steps!
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

		for (int i = 0; i < 6; ++i)
		{
			wallNow += dt22;
			pacer.Advance(dt22, wallNow);
		}

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
	public void WallClockMinimumInterval_BlocksSubFrameInflatedDeltaBursts()
	{
		var pacer = new Engine_GuestPacer();

		// Grant initial step at wall = 1.000s.
		AssertThat(pacer.Advance(0.034, 1.000)).IsEqual(1);

		// Even if Godot reports an inflated 0.035s delta only 4 ms later in real wall time (1.004s),
		// the wall-clock guard withholds the grant so simulation cannot surge ahead of real time.
		AssertThat(pacer.Advance(0.035, 1.004)).IsEqual(0);

		// Once real wall time advances past MinGrantIntervalSeconds (e.g. 1.034s), the grant releases cleanly.
		AssertThat(pacer.Advance(0.004, 1.034)).IsEqual(1);
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
