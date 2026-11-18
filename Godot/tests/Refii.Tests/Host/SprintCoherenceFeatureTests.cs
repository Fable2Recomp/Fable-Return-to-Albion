namespace Refii.Tests;

using System;
using System.Collections.Generic;
using GdUnit4;
using Godot;
using Refii.Core;
using Refii.Entities;
using Refii.Game;
using Refii.Host;
using static GdUnit4.Assertions;

/// <summary>
/// End-to-end feature tests verifying hero sprint and camera coherence across mid-step
/// guest races (<c>game + 0xFC</c> early increment vs. late Havok placement writes) and
/// fluctuating Bowerstone Market frame rates.
/// </summary>
[TestSuite]
public sealed class SprintCoherenceFeatureTests
{
	private const float SprintSpeed = 8.5f;
	private const float MetresPerTick = SprintSpeed / (float)Engine_TickClock.GameTicksPerSecond;
	private static readonly Vector3 CameraOffset = new(0f, 1.8f, -3.5f);

	private sealed class SprintRunResult
	{
		public double MinHeroSpeed = double.MaxValue;
		public double MaxHeroSpeed;
		public double MeanHeroSpeed;
		public double MinCameraSpeed = double.MaxValue;
		public double MaxCameraSpeed;
		public float MaxCameraOffsetError;
		public long SnapsDelta;
	}

	private sealed class GuestRamSim
	{
		private const double EvenStepWorkSeconds = 0.018;
		private const double OddStepWorkSeconds = 0.002;
		private const double EarlyTickBumpOffsetSeconds = 0.004;
		private const double LatePlacementWriteOffsetSeconds = 0.010;

		public uint RamGuestFrame = 200u;
		public uint RamGameTick = 100u;
		public double RamSimSeconds = 100.0 / Engine_TickClock.GameTicksPerSecond;
		public float RamHeroX;
		public Vector3 RamCamOrigin = CameraOffset;

		public bool StepInFlight { get; private set; }
		public ulong PendingSteps { get; private set; }

		private bool _activeIsEven;
		private double _activeStartSec;
		private double _activeEndSec;
		private bool _bumpedTick;
		private bool _wrotePose;
		private double _guestReadySec;
		private ulong _startedSteps;

		public GuestRamSim(double startSec)
		{
			_guestReadySec = startSec;
		}

		public void EnqueueGrants(int granted, double atSec, bool holdInReadWindow)
		{
			if (granted <= 0)
			{
				return;
			}

			if (!StepInFlight && PendingSteps == 0)
			{
				_guestReadySec = atSec;
			}

			PendingSteps += (ulong)granted;
			AdvanceTo(atSec, holdInReadWindow);
		}

		public void EndReadWindowAndDrain(double atSec)
		{
			if (_guestReadySec < atSec)
			{
				_guestReadySec = atSec;
			}

			AdvanceTo(atSec, holdInReadWindow: false);
		}

		public void AdvanceTo(double tSec, bool holdInReadWindow)
		{
			while (true)
			{
				if (!StepInFlight)
				{
					if (PendingSteps == 0 || holdInReadWindow)
					{
						break;
					}

					--PendingSteps;
					StepInFlight = true;
					_activeIsEven = (_startedSteps++ & 1UL) == 0UL;
					_activeStartSec = _guestReadySec;
					_activeEndSec = _activeStartSec + (_activeIsEven ? EvenStepWorkSeconds : OddStepWorkSeconds);
					_bumpedTick = false;
					_wrotePose = false;
				}

				if (_activeIsEven)
				{
					if (!_bumpedTick && tSec >= _activeStartSec + EarlyTickBumpOffsetSeconds)
					{
						++RamGameTick;
						RamSimSeconds = RamGameTick / Engine_TickClock.GameTicksPerSecond;
						_bumpedTick = true;
					}

					if (!_wrotePose && tSec >= _activeStartSec + LatePlacementWriteOffsetSeconds)
					{
						RamHeroX = (RamGameTick - 100u) * MetresPerTick;
						RamCamOrigin = new Vector3(RamHeroX, 0f, 0f) + CameraOffset;
						_wrotePose = true;
					}
				}

				if (tSec >= _activeEndSec)
				{
					++RamGuestFrame;
					StepInFlight = false;
					_guestReadySec = _activeEndSec;
					continue;
				}

				break;
			}
		}
	}

	private static void ResetTimingSingletons()
	{
		Engine_StatedPause.BeginFrame(false);
		Engine_TickClock.ResetForTests();
		Engine_DrawnFrame.ResetForTests();
		Engine_DrawAdvance.ResetForTests();
		Engine_GuestFrame.Publish(0u);
	}

	private static SprintRunResult RunSprintSimulation(
		IReadOnlyList<double> frameDurationsSeconds,
		bool quiescentGate,
		bool holdReadWindow,
		bool gateNoteRestated,
		bool monotonicStamp,
		double warmupSeconds = 2.0)
	{
		ResetTimingSingletons();
		long snapsBefore = Engine_TickTimeline.Snaps;

		try
		{
			var pacer = new Engine_GuestPacer();
			var body = new Engine_TickTimeline();
			var camera = new Engine_TickTimeline();

			double nowSec = 10.0;
			var guest = new GuestRamSim(nowSec);
			var result = new SprintRunResult();

			uint sampledGuestFrame = guest.RamGuestFrame;
			uint sampledTick = guest.RamGameTick;
			double sampledSimSeconds = guest.RamSimSeconds;
			ulong lastQuiescentUsec = 0UL;

			double simSeconds = 0.0;
			double measuredStartSec = 0.0;
			double heroStartX = 0.0;
			bool measuredStarted = false;
			bool havePrevDrawn = false;
			double prevHeroX = 0.0;
			double prevCamX = 0.0;

			for (int frame = 0; frame < frameDurationsSeconds.Count; ++frame)
			{
				double dt = frameDurationsSeconds[frame];
				nowSec += dt;
				double frameStartSec = nowSec;
				double frameWalkSec = frameStartSec + 0.0095;
				double frameEndSec = frameStartSec + 0.011;
				ulong nowUsec = (ulong)Math.Round(frameStartSec * 1_000_000.0);

				// Between the previous frame's EndTimeBaseReadWindow and this frame's SampleGuestFrame,
				// the host read window is closed and the guest thread runs queued HF steps freely.
				guest.AdvanceTo(frameStartSec, holdInReadWindow: false);

				// 1. FrameStart: enter host read window and evaluate SampleGuestFrame().
				bool defer = quiescentGate && HostBridge.ShouldDeferInFlightGuestSample(
					isInGame: true,
					guest.StepInFlight,
					Engine_GuestFrame.Frame,
					nowUsec,
					lastQuiescentUsec);

				if (defer)
				{
					Engine_GuestFrame.Publish(Engine_GuestFrame.Frame);
				}
				else
				{
					sampledGuestFrame = guest.RamGuestFrame;
					sampledTick = guest.RamGameTick;
					sampledSimSeconds = guest.RamSimSeconds;
					if (!guest.StepInFlight)
					{
						lastQuiescentUsec = nowUsec;
					}

					Engine_GuestFrame.Publish(sampledGuestFrame);
				}

				Engine_TickClock.PublishGuestGameTick(sampledTick);
				Engine_StatedPause.BeginFrame(false);
				if (Engine_TickClock.UseStatedClock)
				{
					Engine_TickClock.ObserveStatedClock(sampledSimSeconds, (long)nowUsec);
				}

				Engine_DrawnFrame.BeginFrame(nowUsec, sampledSimSeconds);
				if (!gateNoteRestated || Engine_GuestFrame.Changed || Engine_GuestFrame.Frame == 0u)
				{
					Engine_DrawnFrame.NoteRestated();
				}

				Engine_TickClock.BeginFrame((long)nowUsec);
				double drawTick = Engine_DrawnFrame.DrawTick;

				// 2. HostEntityWalk + GameEntities.Drawn + GameWorld camera (~4 ms into frame).
				guest.AdvanceTo(frameWalkSec, holdReadWindow);
				uint tick = Engine_TickClock.GuestGameTick;
				bool stepComplete = Engine_GuestFrame.Changed;

				if (stepComplete || body.Empty)
				{
					var statedHero = new Transform3D(Basis.Identity, new Vector3(guest.RamHeroX, 0f, 0f));
					bool moved = !statedHero.Origin.IsEqualApprox(body.Newest.Origin) ||
						!statedHero.Basis.IsEqualApprox(body.Newest.Basis);

					if (body.Empty)
					{
						body.Reset(tick, statedHero);
					}
					else if (moved)
					{
						double stamp = monotonicStamp
							? GameEntities.ResolvePlacementStamp(tick, body.NewestTick, body.Empty, moved)
							: tick;
						body.Observe(stamp, statedHero);
						Engine_DrawnFrame.NoteRestated();
					}
				}

				Transform3D drawnHero = body.Advance(nowUsec, drawTick);

				Transform3D drawnCam;
				if (!stepComplete && !camera.Empty)
				{
					drawnCam = camera.Advance(nowUsec, drawTick);
				}
				else
				{
					var statedCam = new Transform3D(Basis.Identity, guest.RamCamOrigin);
					if (camera.Empty)
					{
						camera.Reset(tick, statedCam);
						drawnCam = statedCam;
					}
					else
					{
						camera.Observe(tick, statedCam);
						drawnCam = camera.Advance(nowUsec, drawTick);
					}
				}

				simSeconds += dt;
				if (measuredStarted && havePrevDrawn)
				{
					double heroSpeed = (drawnHero.Origin.X - prevHeroX) / dt;
					double camSpeed = (drawnCam.Origin.X - prevCamX) / dt;
					result.MinHeroSpeed = Math.Min(result.MinHeroSpeed, heroSpeed);
					result.MaxHeroSpeed = Math.Max(result.MaxHeroSpeed, heroSpeed);
					result.MinCameraSpeed = Math.Min(result.MinCameraSpeed, camSpeed);
					result.MaxCameraSpeed = Math.Max(result.MaxCameraSpeed, camSpeed);

					float offsetError = (drawnCam.Origin - (drawnHero.Origin + CameraOffset)).Length();
					if (offsetError > result.MaxCameraOffsetError)
					{
						result.MaxCameraOffsetError = offsetError;
					}
				}
				else if (!measuredStarted && simSeconds >= warmupSeconds && !body.Empty && !camera.Empty)
				{
					measuredStarted = true;
					measuredStartSec = simSeconds;
					heroStartX = drawnHero.Origin.X;
				}

				prevHeroX = drawnHero.Origin.X;
				prevCamX = drawnCam.Origin.X;
				havePrevDrawn = true;

				if (measuredStarted && frame == frameDurationsSeconds.Count - 1)
				{
					result.MeanHeroSpeed = (drawnHero.Origin.X - heroStartX) / (simSeconds - measuredStartSec);
				}

				// 3. End-of-frame GameSession grant & EndTimeBaseReadWindow().
				guest.AdvanceTo(frameEndSec, holdReadWindow);
				if (holdReadWindow)
				{
					guest.EndReadWindowAndDrain(frameEndSec);
				}

				ulong unconsumed = guest.PendingSteps + (guest.StepInFlight ? 1UL : 0UL);
				int granted = pacer.Advance(dt, unconsumed, out double lead, out bool withheld);
				Engine_TickClock.HostGrantLead = lead;
				Engine_TickClock.HostGrantWithheld = withheld;
				guest.EnqueueGrants(granted, frameEndSec, holdInReadWindow: false);
			}

			result.SnapsDelta = Engine_TickTimeline.Snaps - snapsBefore;
			return result;
		}
		finally
		{
			ResetTimingSingletons();
		}
	}

	private static double[] BuildBowerstoneCadenceFrames(int count)
	{
		double[] pattern = { 1.0 / 20.0, 1.0 / 68.0, 1.0 / 24.0, 1.0 / 45.0 };
		var frames = new double[count];
		for (int i = 0; i < count; ++i)
		{
			frames[i] = pattern[i % pattern.Length];
		}

		return frames;
	}

	private static double[] BuildSteadyFrames(int count, double fps)
	{
		double dt = 1.0 / fps;
		var frames = new double[count];
		for (int i = 0; i < count; ++i)
		{
			frames[i] = dt;
		}

		return frames;
	}

	/// <summary>
	/// Reproduces the Bowerstone Market mid-step race without the Round 2 guards: reading guest
	/// state mid-step and releasing queued steps inside the host read window creates velocity surges
	/// well above the stated 8.5 m/s sprint speed followed by stalls.
	/// </summary>
	[TestCase]
	public void LegacyMidStepRace_ReproducesTwoTimesSprintSurgeAndStall()
	{
		double[] frames = BuildBowerstoneCadenceFrames(320);
		SprintRunResult legacy = RunSprintSimulation(
			frames,
			quiescentGate: false,
			holdReadWindow: false,
			gateNoteRestated: false,
			monotonicStamp: false);

		AssertThat(legacy.MaxHeroSpeed)
			.OverrideFailureMessage($"LegacyMaxHero={legacy.MaxHeroSpeed:F3}, LegacyMaxCam={legacy.MaxCameraSpeed:F3}, LegacyMinHero={legacy.MinHeroSpeed:F3}, LegacyMinCam={legacy.MinCameraSpeed:F3}")
			.IsGreater(13.0);
		AssertThat(legacy.MinHeroSpeed)
			.OverrideFailureMessage($"LegacyMinHero={legacy.MinHeroSpeed:F3}, LegacyMinCam={legacy.MinCameraSpeed:F3}")
			.IsLess(5.0);
		AssertThat(legacy.MinCameraSpeed)
			.OverrideFailureMessage($"LegacyMinCam={legacy.MinCameraSpeed:F3}")
			.IsLess(1.0);
	}

	/// <summary>
	/// Verifies that the full Round 2 pipeline keeps hero sprint velocity tightly bounded around
	/// 8.5 m/s with zero timeline snaps and coherent camera tracking across fluctuating Bowerstone
	/// frame rates, steady 43 FPS, and sub-30 FPS (22 FPS).
	/// </summary>
	[TestCase]
	public void QuiescentGateAcross43Fps22FpsAndVariableCadence_EliminatesSurgesAndKeepsCameraCoherent()
	{
		(string Name, double[] Frames)[] scenarios =
		{
			("BowerstoneVariable", BuildBowerstoneCadenceFrames(320)),
			("Steady43Fps", BuildSteadyFrames(320, 43.0)),
			("Steady22Fps", BuildSteadyFrames(220, 22.0)),
		};

		foreach ((string name, double[] frames) in scenarios)
		{
			SprintRunResult run = RunSprintSimulation(
				frames,
				quiescentGate: true,
				holdReadWindow: true,
				gateNoteRestated: true,
				monotonicStamp: true);

			AssertThat(run.MinHeroSpeed)
				.OverrideFailureMessage($"{name}: MinHeroSpeed={run.MinHeroSpeed:F3}, MaxHeroSpeed={run.MaxHeroSpeed:F3}, Mean={run.MeanHeroSpeed:F3}, Snaps={run.SnapsDelta}, CamErr={run.MaxCameraOffsetError:F4}")
				.IsGreaterEqual(7.0);
			AssertThat(run.MaxHeroSpeed)
				.OverrideFailureMessage($"{name}: MaxHeroSpeed={run.MaxHeroSpeed:F3}")
				.IsLessEqual(10.0);
			AssertThat(Math.Abs(run.MeanHeroSpeed - SprintSpeed))
				.OverrideFailureMessage($"{name}: MeanHeroSpeed={run.MeanHeroSpeed:F3}")
				.IsLess(0.25);
			AssertThat(run.SnapsDelta).IsEqual(0L);
			AssertThat(run.MaxCameraOffsetError).IsLess(0.08f);
		}
	}

	/// <summary>
	/// Simulates a worst-case CPU-saturated fallback where a placement from tick <c>T + 1</c>
	/// arrives while <c>GuestGameTick</c> is still <c>T</c>, followed by <c>T + 1</c> observing the
	/// same placement: <see cref="GameEntities.ResolvePlacementStamp"/> prevents the 2x spike and
	/// subsequent stall.
	/// </summary>
	[TestCase]
	public void LatePlacementUnderCurrentTickFallback_ResolvePlacementStampPreventsTwoTimesSurge()
	{
		SprintRunResult RunFallbackCase(bool useMonotonicStamp)
		{
			ResetTimingSingletons();
			long snapsBefore = Engine_TickTimeline.Snaps;

			try
			{
				var body = new Engine_TickTimeline();
				double dt = 1.0 / 45.0;
				double nowSec = 10.0;
				double simSec = 0.0;
				double minSpeed = double.MaxValue;
				double maxSpeed = 0.0;
				double prevX = 0.0;
				bool havePrev = false;
				uint guestTick = 100u;
				uint guestFrame = 200u;
				double nextTickAt = nowSec + (1.0 / Engine_TickClock.GameTicksPerSecond);

				for (int frame = 0; frame < 360; ++frame)
				{
					nowSec += dt;
					simSec += dt;
					ulong nowUsec = (ulong)Math.Round(nowSec * 1_000_000.0);

					bool tickAdvanced = false;
					while (nowSec >= nextTickAt)
					{
						++guestTick;
						++guestFrame;
						nextTickAt += 1.0 / Engine_TickClock.GameTicksPerSecond;
						tickAdvanced = true;
					}

					uint readTick = guestTick;
					bool earlyRace = (guestTick % 5u == 0u) && tickAdvanced;
					uint sampledTickForStamp = earlyRace ? guestTick - 1u : guestTick;
					uint publishedFrame = earlyRace ? guestFrame - 1u : guestFrame;
					bool runWalkThisFrame = tickAdvanced || Engine_GuestFrame.Frame != publishedFrame;
					float guestX = (readTick - 100u) * MetresPerTick;
					double simClockSec = sampledTickForStamp / Engine_TickClock.GameTicksPerSecond;

					Engine_GuestFrame.Publish(publishedFrame);
					Engine_TickClock.PublishGuestGameTick(sampledTickForStamp);
					Engine_StatedPause.BeginFrame(false);
					if (Engine_TickClock.UseStatedClock)
					{
						Engine_TickClock.ObserveStatedClock(simClockSec, (long)nowUsec);
					}

					Engine_DrawnFrame.BeginFrame(nowUsec, simClockSec);
					if (Engine_GuestFrame.Changed || Engine_GuestFrame.Frame == 0u)
					{
						Engine_DrawnFrame.NoteRestated();
					}

					Engine_TickClock.BeginFrame((long)nowUsec);

					var stated = new Transform3D(Basis.Identity, new Vector3(guestX, 0f, 0f));
					if (body.Empty)
					{
						body.Reset(sampledTickForStamp, stated);
					}
					else if (runWalkThisFrame)
					{
						bool moved = !stated.Origin.IsEqualApprox(body.Newest.Origin);
						if (moved)
						{
							double stamp = useMonotonicStamp
								? GameEntities.ResolvePlacementStamp(sampledTickForStamp, body.NewestTick, body.Empty, moved)
								: sampledTickForStamp;
							body.Observe(stamp, stated);
							Engine_DrawnFrame.NoteRestated();
						}
					}

					Transform3D drawn = body.Advance(nowUsec, Engine_DrawnFrame.DrawTick);
					if (simSec >= 2.0 && havePrev)
					{
						double speed = (drawn.Origin.X - prevX) / dt;
						minSpeed = Math.Min(minSpeed, speed);
						maxSpeed = Math.Max(maxSpeed, speed);
					}

					prevX = drawn.Origin.X;
					havePrev = true;
				}

				return new SprintRunResult
				{
					MinHeroSpeed = minSpeed,
					MaxHeroSpeed = maxSpeed,
					SnapsDelta = Engine_TickTimeline.Snaps - snapsBefore,
				};
			}
			finally
			{
				ResetTimingSingletons();
			}
		}

		SprintRunResult withoutFix = RunFallbackCase(useMonotonicStamp: false);
		AssertThat(withoutFix.MaxHeroSpeed).IsGreater(13.5);
		AssertThat(withoutFix.MinHeroSpeed).IsLess(3.5);

		SprintRunResult withFix = RunFallbackCase(useMonotonicStamp: true);
		AssertThat(withFix.MinHeroSpeed).IsGreaterEqual(7.0);
		AssertThat(withFix.MaxHeroSpeed).IsLessEqual(10.0);
		AssertThat(withFix.SnapsDelta).IsEqual(0L);
	}
}
