namespace Refii.Tests;

using System;
using System.Collections.Generic;
using GdUnit4;
using Godot;
using Refii.Animations;
using Refii.Core;
using Refii.Entities;
using static GdUnit4.Assertions;

/// <summary>
/// End-to-end feature tests driving the full host timing pipeline
/// (<see cref="Engine_GuestPacer"/> -&gt; <see cref="Engine_TickClock"/> -&gt;
/// <see cref="Engine_DrawnFrame"/> -&gt; <see cref="Engine_DrawAdvance"/> -&gt;
/// <see cref="Engine_TickTimeline"/> &amp; <see cref="Engine_ClipPlayhead"/>) across steady sub-30 FPS,
/// fluctuating Bowerstone-style FPS transitions with 12 ms guest step latency, and pause holds.
/// </summary>
[TestSuite]
public sealed class Sub30FpsTimingFeatureTests
{
	private const float MetresPerSecond = 6.0f;
	private const float MetresPerTick = MetresPerSecond / (float)Engine_TickClock.GameTicksPerSecond;

	private sealed class PipelineResult
	{
		public double FinalBodyX;
		public double FinalClipTime;
		public double ElapsedSeconds;
		public double FinalPace;
		public double MinPace = double.MaxValue;
		public double MaxPace;
		public double FinalPlaybackRate;
		public double MinPlaybackRate = double.MaxValue;
		public double MaxPlaybackRate;
		public double PeriodRatio;
		public long HostOffsetSnaps;
		public int WithheldFrames;
	}

	private static void ResetTimingSingletons()
	{
		Engine_StatedPause.BeginFrame(false);
		Engine_TickClock.ResetForTests();
		Engine_DrawnFrame.ResetForTests();
		Engine_DrawAdvance.ResetForTests();
	}

	/// <summary>
	/// Simulates the complete host-to-guest closed loop for a given sequence of frame durations,
	/// including realistic native step queue release/completion latency and mid-frame guest steps.
	/// </summary>
	private static PipelineResult RunPipeline(
		IReadOnlyList<double> frameDurationsSeconds,
		double guestStepWorkSeconds = 0.012,
		Func<int, bool>? isPausedAtFrame = null)
	{
		ResetTimingSingletons();
		try
		{
			var pacer = new Engine_GuestPacer();
			var body = new Engine_TickTimeline();
			var camera = new Engine_TickTimeline();
			var clip = new Engine_ClipPlayhead();

			var pendingCompletions = new Queue<double>();
			uint executedHfSteps = 0;
			uint visibleGuestTick = 100;
			long totalGranted = 0;
			int withheldFrames = 0;

			double nowSec = 10.0;
			double simSeconds = 0.0;
			double activeUntil = 0.0;
			double warmupSec = 2.0;
			double measuredStartSec = 0.0;
			double bodyStartX = 0.0;
			double clipStartTime = 0.0;
			bool measuredStarted = false;

			double finalBodyX = 0.0;
			double finalClipTime = 0.0;
			double minPace = double.MaxValue;
			double maxPace = 0.0;
			double minPlaybackRate = double.MaxValue;
			double maxPlaybackRate = 0.0;
			uint lastObservedTick = 0;

			for (int frame = 0; frame < frameDurationsSeconds.Count; ++frame)
			{
				double dt = frameDurationsSeconds[frame];
				nowSec += dt;
				ulong nowUsec = (ulong)Math.Round(nowSec * 1_000_000.0);
				bool paused = isPausedAtFrame?.Invoke(frame) ?? false;

				// Complete any queued HF guest steps that finished before FrameStart.
				// During an in-game pause menu, GUI steps continue draining while the world tick holds.
				while (pendingCompletions.Count > 0 && pendingCompletions.Peek() <= nowSec)
				{
					_ = pendingCompletions.Dequeue();
					++executedHfSteps;
					if (!paused && (executedHfSteps & 1u) == 0u)
					{
						++visibleGuestTick;
					}
				}

				// 1. FrameStart phase order (mirrors HostBridge.SampleGuestFrame + Engine_FrameStartNode).
				Engine_TickClock.PublishGuestGameTick(visibleGuestTick);
				Engine_StatedPause.BeginFrame(paused);
				Engine_DrawnFrame.BeginFrame(nowUsec);
				Engine_DrawnFrame.NoteRestated();
				Engine_TickClock.BeginFrame(nowUsec);

				double drawTick = Engine_DrawnFrame.DrawTick;

				// 2. Presentation observation on new game ticks (mirrors GameWorld + GameEntities).
				if (!paused && visibleGuestTick != lastObservedTick)
				{
					lastObservedTick = visibleGuestTick;
					float guestX = (visibleGuestTick - 100u) * MetresPerTick;
					var bodyPose = new Transform3D(Basis.Identity, new Vector3(guestX, 0f, 0f));
					var camPose = new Transform3D(Basis.Identity, new Vector3(guestX, 2f, -4f));
					if (body.Empty)
					{
						body.Reset(visibleGuestTick, bodyPose);
						camera.Reset(visibleGuestTick, camPose);
						clip.Observe(0.0, 0.0, visibleGuestTick, 1000.0, loop: false, cut: true, drawTick);
					}
					else
					{
						body.Observe(visibleGuestTick, bodyPose);
						camera.Observe(visibleGuestTick, camPose);
						double curClip = (visibleGuestTick - 100u) / Engine_TickClock.GameTicksPerSecond;
						double prevClip = curClip - (1.0 / Engine_TickClock.GameTicksPerSecond);
						clip.Observe(prevClip, curClip, visibleGuestTick, 1000.0, loop: false, cut: false, drawTick);
					}
				}

				Transform3D drawnBody = body.Advance(nowUsec, drawTick);
				_ = camera.Advance(nowUsec, drawTick);
				double drawnClip = clip.Advance(dt, drawTick);

				if (!paused)
				{
					simSeconds += dt;
					if (measuredStarted)
					{
						minPace = Math.Min(minPace, Engine_DrawAdvance.Pace);
						maxPace = Math.Max(maxPace, Engine_DrawAdvance.Pace);
						minPlaybackRate = Math.Min(minPlaybackRate, Engine_TickClock.PlaybackRate);
						maxPlaybackRate = Math.Max(maxPlaybackRate, Engine_TickClock.PlaybackRate);
					}
					else if (simSeconds >= warmupSec && !body.Empty && clip.Started)
					{
						measuredStarted = true;
						measuredStartSec = simSeconds;
						bodyStartX = drawnBody.Origin.X;
						clipStartTime = drawnClip;
					}
				}

				finalBodyX = drawnBody.Origin.X;
				finalClipTime = drawnClip;

				// 3. End-of-frame GameSession grant (queries live unconsumed steps = pending + in_flight).
				ulong unconsumed = (ulong)pendingCompletions.Count;
				int granted = pacer.Advance(dt, unconsumed, out double lead, out bool withheld);
				Engine_TickClock.HostGrantLead = lead;
				Engine_TickClock.HostGrantWithheld = withheld;
				if (withheld)
				{
					++withheldFrames;
				}

				for (int s = 0; s < granted; ++s)
				{
					double start = Math.Max(nowSec, activeUntil);
					activeUntil = start + guestStepWorkSeconds;
					pendingCompletions.Enqueue(activeUntil);
				}

				totalGranted += granted;
				Engine_TickClock.PublishHostTicks(totalGranted, pacer.Phase);
			}

			return new PipelineResult
			{
				FinalBodyX = finalBodyX - bodyStartX,
				FinalClipTime = finalClipTime - clipStartTime,
				ElapsedSeconds = simSeconds - measuredStartSec,
				FinalPace = Engine_DrawAdvance.Pace,
				MinPace = minPace,
				MaxPace = maxPace,
				FinalPlaybackRate = Engine_TickClock.PlaybackRate,
				MinPlaybackRate = minPlaybackRate,
				MaxPlaybackRate = maxPlaybackRate,
				PeriodRatio = Engine_DrawnFrame.Playhead.PeriodRatio,
				HostOffsetSnaps = Engine_TickClock.HostOffsetSnaps,
				WithheldFrames = withheldFrames,
			};
		}
		finally
		{
			ResetTimingSingletons();
		}
	}

	private static List<double> BuildSteadyFrames(double fps, double durationSeconds)
	{
		int count = (int)Math.Round(fps * durationSeconds);
		double dt = 1.0 / fps;
		var frames = new List<double>(count);
		for (int i = 0; i < count; ++i)
		{
			frames.Add(dt);
		}
		return frames;
	}

	/// <summary>
	/// Verifies steady sub-30 and sub-15 FPS host framerates (24, 20, 15, and 12 FPS) preserve
	/// exact 1.0x real-time character movement, clip advance, and unbiased pair period ratio.
	/// </summary>
	[TestCase]
	public void SteadySub30AndSub15Fps_MaintainRealTimeCharacterAndClipSpeed()
	{
		double[] rates = { 24.0, 20.0, 15.0, 12.0 };
		foreach (double fps in rates)
		{
			PipelineResult res = RunPipeline(BuildSteadyFrames(fps, durationSeconds: 14.0));
			double bodySpeed = res.FinalBodyX / res.ElapsedSeconds;
			double clipRate = res.FinalClipTime / res.ElapsedSeconds;

			AssertThat(res.WithheldFrames).OverrideFailureMessage($"fps={fps} unexpectedly withheld").IsEqual(0);
			AssertThat(res.HostOffsetSnaps).OverrideFailureMessage($"fps={fps} snapped host offset").IsEqual(0L);
			AssertThat(Math.Abs((bodySpeed / MetresPerSecond) - 1.0))
				.OverrideFailureMessage($"fps={fps} body speed ratio {bodySpeed / MetresPerSecond:F4}").IsLess(0.02);
			AssertThat(Math.Abs(clipRate - 1.0))
				.OverrideFailureMessage($"fps={fps} clip rate {clipRate:F4}").IsLess(0.02);
			AssertThat(Math.Abs(res.FinalPace - 1.0))
				.OverrideFailureMessage($"fps={fps} DrawAdvance.Pace={res.FinalPace:F4}").IsLess(0.02);
			AssertThat(Math.Abs(res.FinalPlaybackRate - 1.0))
				.OverrideFailureMessage($"fps={fps} PlaybackRate={res.FinalPlaybackRate:F4}").IsLess(0.02);
			AssertThat(Math.Abs(res.PeriodRatio - 1.0))
				.OverrideFailureMessage($"fps={fps} PeriodRatio={res.PeriodRatio:F4}").IsLess(0.03);
		}
	}

	/// <summary>
	/// Reproduces running repeatedly between a 90 FPS street and a 22 FPS -&gt; 18 FPS heavy market
	/// square with 12 ms guest step latency, proving zero FPS-coupled slowdown or speed-up.
	/// </summary>
	[TestCase]
	public void Fluctuating90To18FpsTransitions_With12MsStepWork_MaintainRealTimeSpeed()
	{
		var frames = new List<double>();
		for (int cycle = 0; cycle < 4; ++cycle)
		{
			frames.AddRange(BuildSteadyFrames(90.0, 1.5));
			frames.AddRange(BuildSteadyFrames(22.0, 1.5));
			frames.AddRange(BuildSteadyFrames(18.0, 1.5));
			frames.AddRange(BuildSteadyFrames(42.0, 1.5));
		}

		PipelineResult res = RunPipeline(frames, guestStepWorkSeconds: 0.012);
		double bodySpeed = res.FinalBodyX / res.ElapsedSeconds;
		double clipRate = res.FinalClipTime / res.ElapsedSeconds;

		AssertThat(res.WithheldFrames).IsEqual(0);
		AssertThat(res.HostOffsetSnaps).IsEqual(0L);
		AssertThat(Math.Abs((bodySpeed / MetresPerSecond) - 1.0))
			.OverrideFailureMessage($"bodySpeedRatio={bodySpeed / MetresPerSecond:F4}").IsLess(0.02);
		AssertThat(Math.Abs(clipRate - 1.0))
			.OverrideFailureMessage($"clipRate={clipRate:F4}").IsLess(0.02);
		AssertThat(res.MinPace >= 0.94 && res.MaxPace <= 1.06)
			.OverrideFailureMessage($"Pace band=[{res.MinPace:F4}, {res.MaxPace:F4}]").IsTrue();
		AssertThat(res.MinPlaybackRate >= 0.95 && res.MaxPlaybackRate <= 1.05)
			.OverrideFailureMessage($"PlaybackRate band=[{res.MinPlaybackRate:F4}, {res.MaxPlaybackRate:F4}]").IsTrue();

		// Returning to steady 60 FPS after fluctuating cycles settles PlaybackRate and Pace to 1.000.
		frames.AddRange(BuildSteadyFrames(60.0, 2.5));
		PipelineResult settled = RunPipeline(frames, guestStepWorkSeconds: 0.012);
		AssertThat(Math.Abs(settled.FinalPlaybackRate - 1.0))
			.OverrideFailureMessage($"Settled PlaybackRate={settled.FinalPlaybackRate:F4}").IsLess(0.02);
		AssertThat(Math.Abs(settled.FinalPace - 1.0))
			.OverrideFailureMessage($"Settled Pace={settled.FinalPace:F4}").IsLess(0.02);
	}

	/// <summary>
	/// Verifies pause-menu holds (3 s where host keeps granting GUI steps while GuestGameTick holds)
	/// incur zero HostOffsetSnaps and resume 1.0x world motion cleanly upon unpause.
	/// </summary>
	[TestCase]
	public void StatedPauseHold_IncursZeroClockOffsetSnapsAndResumesAtRealTime()
	{
		List<double> frames = BuildSteadyFrames(25.0, 14.0);
		// Frames 100..174 (3 seconds at 25 FPS) are held under Engine_StatedPause.
		PipelineResult res = RunPipeline(frames, isPausedAtFrame: frame => frame >= 100 && frame < 175);
		double bodySpeed = res.FinalBodyX / res.ElapsedSeconds;
		double clipRate = res.FinalClipTime / res.ElapsedSeconds;

		AssertThat(res.HostOffsetSnaps).IsEqual(0L);
		AssertThat(Math.Abs((bodySpeed / MetresPerSecond) - 1.0))
			.OverrideFailureMessage($"post-pause bodySpeedRatio={bodySpeed / MetresPerSecond:F4}").IsLess(0.025);
		AssertThat(Math.Abs(clipRate - 1.0))
			.OverrideFailureMessage($"post-pause clipRate={clipRate:F4}").IsLess(0.025);
		AssertThat(Math.Abs(res.FinalPace - 1.0))
			.OverrideFailureMessage($"post-pause Pace={res.FinalPace:F4}").IsLess(0.025);
	}
}
