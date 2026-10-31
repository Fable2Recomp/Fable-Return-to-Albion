namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Core;
using Refii.Launcher.Core;

/// <summary>
/// `motion_arrival_fit` (<see cref="Engine_ArrivalFit"/> through <see cref="Engine_TickTimeline"/>):
/// a walk stated late and irregularly is drawn at an even speed, a stop ends exactly on the stated
/// transform, a teleport snaps, rotation is drawn along with position, the key switches live without
/// a jump, a turn of the history (the mouse) turns the drawn transform with it, and a moving body
/// allocates nothing per frame.
/// </summary>
[TestSuite]
public class EngineArrivalFitTests
{
	private const ulong FrameUsec = 50_000UL;

	/// <summary>Metres a statement in the allocation test.</summary>
	private const float Step = 0.15f;

	/// <summary>The walk's speed, m/s: a jog.</summary>
	private const double Speed = 3.0;

	private static T Fitting<T>(Func<T> body) => MotionReplay.WithRules(true, body);

	/// <summary>
	/// A steady walk along +X stopping at frame <paramref name="walkFrames"/>, stated like the
	/// phone's: a statement every 1-3 frames at random, each the place 0-40 ms before its frame,
	/// stamped with a tick that is a tick out a third of the time. Past the stop the unchanged place
	/// is stated every 4 frames (the bodies' rule). <paramref name="basis"/> turns it with time.
	/// Answers the drawn transform per frame.
	/// </summary>
	private static List<Transform3D> Walk(Engine_TickTimeline line, int frames, int walkFrames, int seed,
		Func<double, Basis>? basis = null)
	{
		var random = new Random(seed);
		var drawn = new List<Transform3D>();
		double stop = walkFrames * 0.05;
		int tick = 0;
		int next = 1;
		ulong origin = 70_000_000UL;
		line.Reset(0.0, new Transform3D(basis?.Invoke(0.0) ?? Basis.Identity, Vector3.Zero));
		for (int frame = 1; frame <= frames; frame++)
		{
			if (frame >= next && (frame <= walkFrames || frame % 4 == 0))
			{
				double at = Math.Min((frame * 0.05) - (0.04 * random.NextDouble()), stop);
				tick = Math.Max(tick + 1, (int)(at * 15.0 * 0.75) + (random.Next(3) == 0 ? 1 : 0));
				line.Observe(tick, new Transform3D(basis?.Invoke(at) ?? Basis.Identity, new Vector3((float)(Speed * at), 0f, 0f)));
				next = frame + 1 + random.Next(3);
			}

			drawn.Add(line.Advance(origin + ((ulong)frame * FrameUsec), double.NaN));
		}

		return drawn;
	}

	[TestCase]
	public void ALateIrregularWalkIsDrawnAtAnEvenSpeed()
	{
		List<Transform3D> drawn = Fitting(() => Walk(new Engine_TickTimeline(), 120, 120, seed: 3));
		var steps = Enumerable.Range(30, 80).Select(i => (double)(drawn[i].Origin.X - drawn[i - 1].Origin.X)).ToList();
		double mean = steps.Average();
		AssertThat(Math.Abs(mean - (Speed * 0.05))).IsLess(0.01);
		AssertThat(steps.Min()).IsGreater(0.6 * mean);
		AssertThat(steps.Max()).IsLess(1.4 * mean);
	}

	[TestCase]
	public void AStopEndsExactlyOnTheStatedTransformAndHandsTheFitBack()
	{
		int liveBefore = Engine_ArrivalFit.Live;
		var line = new Engine_TickTimeline();
		List<Transform3D> drawn = Fitting(() => Walk(line, 140, 40, seed: 5, basis: t => new Basis(Vector3.Up, (float)t)));
		AssertThat(line.Settled).IsTrue();
		AssertThat(line.FittingArrivals).IsFalse();
		AssertThat(Engine_ArrivalFit.Live).IsEqual(liveBefore);
		AssertThat(drawn[^1].Origin).IsEqual(line.Newest.Origin);
		AssertThat(drawn[^1].Basis.GetRotationQuaternion().AngleTo(line.Newest.Basis.GetRotationQuaternion())).IsLess(1e-6f);

		// Never walked back further than the coast and its fade overrun.
		float stand = line.Newest.Origin.X;
		AssertThat(drawn.Max(t => t.Origin.X) - stand).IsLess(0.3f);
	}

	[TestCase]
	public void AStandingBodyCarriesNoFit()
	{
		int liveBefore = Engine_ArrivalFit.Live;
		var line = new Engine_TickTimeline();
		var place = new Transform3D(Basis.Identity, new Vector3(3f, 0f, 1f));
		Fitting(() =>
		{
			line.Reset(10.0, place);
			for (int frame = 1; frame < 40; frame++)
			{
				if (frame % 4 == 0)
				{
					line.Observe(10.0 + frame, place);
				}

				AssertThat(line.Advance(5_000_000UL + ((ulong)frame * FrameUsec), double.NaN).Origin).IsEqual(place.Origin);
			}

			return 0;
		});
		AssertThat(line.FittingArrivals).IsFalse();
		AssertThat(Engine_ArrivalFit.Live).IsEqual(liveBefore);
		AssertThat(line.Settled).IsTrue();
	}

	[TestCase]
	public void AResetMidWalkSnapsAtOnce()
	{
		var line = new Engine_TickTimeline();
		var there = new Transform3D(new Basis(Vector3.Up, 1.0f), new Vector3(40f, 2f, -7f));
		Transform3D after = Fitting(() =>
		{
			Walk(line, 30, 30, seed: 9);
			AssertThat(line.FittingArrivals).IsTrue();
			line.Reset(100.0, there);
			return line.Advance(90_000_000UL, double.NaN);
		});
		AssertThat(line.FittingArrivals).IsFalse();
		AssertThat(after.Origin).IsEqual(there.Origin);
		AssertThat(after.Basis.GetRotationQuaternion().AngleTo(there.Basis.GetRotationQuaternion())).IsLess(1e-6f);
	}

	[TestCase]
	public void ATurningBodyTurnsEvenlyWithItsWalk()
	{
		// 1.6 rad/s: 0.08 rad a frame.
		const float rate = 0.08f;
		List<Transform3D> drawn = Fitting(() => Walk(new Engine_TickTimeline(), 120, 120, seed: 4,
			basis: t => new Basis(Vector3.Up, (float)(rate / 0.05 * t))));
		var turns = Enumerable.Range(30, 80).Select(i =>
			(double)drawn[i - 1].Basis.GetRotationQuaternion().AngleTo(drawn[i].Basis.GetRotationQuaternion())).ToList();
		double mean = turns.Average();

		// The stated yaw advances rate a frame; the drawn one as evenly as the walk does.
		AssertThat(Math.Abs(mean - rate)).IsLess(0.1 * rate);
		AssertThat(turns.Min()).IsGreater(0.6 * mean);
		AssertThat(turns.Max()).IsLess(1.4 * mean);
	}

	[TestCase]
	public void TheKeySwitchesLiveWithoutAJump()
	{
		var line = new Engine_TickTimeline();
		double lastX = 0.0;
		double worst = 0.0;
		MotionReplay.WithRules(false, () =>
		{
			line.Reset(0.0, Transform3D.Identity);
			for (int frame = 1; frame < 200; frame++)
			{
				Engine_TickTimeline.ArrivalFit = frame is >= 60 and < 130;

				// One statement a tick, on time, drawn a tick behind: both rules walk 0.2 m a tick,
				// 15 cm a frame at 20 fps.
				double tick = frame * 0.75;
				if (Math.Floor(tick) > Math.Floor((frame - 1) * 0.75))
				{
					line.Observe(Math.Floor(tick), new Transform3D(Basis.Identity, new Vector3(0.2f * (float)Math.Floor(tick), 0f, 0f)));
				}

				double x = line.Advance(30_000_000UL + ((ulong)frame * FrameUsec), tick - 1.0).Origin.X;
				if (frame > 30)
				{
					worst = Math.Max(worst, Math.Abs(x - lastX));
				}

				lastX = x;
			}

			return 0;
		});

		// Within 1.5x of the walk's own 15 cm a frame, through both switches.
		AssertThat(worst).IsLess(1.5 * 0.15);
	}

	[TestCase]
	public void ATurnOfTheHistoryTurnsTheDrawnTransformWithIt()
	{
		var line = new Engine_TickTimeline();
		var pivot = new Vector3(1f, 0f, 2f);
		var turn = new Basis(Vector3.Up, 0.4f);
		(Transform3D before, Transform3D after) = Fitting(() =>
		{
			Walk(line, 30, 30, seed: 6);
			Transform3D drawnBefore = line.Advance(70_000_000UL + (30UL * FrameUsec), double.NaN);
			line.TurnAbout(pivot, turn);
			return (drawnBefore, line.Advance(70_000_000UL + (31UL * FrameUsec), double.NaN));
		});

		// One frame on, the turned walk: the drawn point is the turned one moved by one frame of the
		// turned motion, within a frame's step.
		Vector3 turned = pivot + (turn * (before.Origin - pivot));
		AssertThat(after.Origin.DistanceTo(turned)).IsLess(0.3f);
		AssertThat(after.Origin.DistanceTo(before.Origin)).IsGreater(0.3f);
		AssertThat(after.Basis.GetRotationQuaternion().AngleTo(turn.GetRotationQuaternion())).IsLess(0.01f);
	}

	[TestCase]
	public void AMovingBodyAllocatesNothingPerFrame()
	{
		var line = new Engine_TickTimeline();
		long allocated = Fitting(() =>
		{
			Walk(line, 20, 20, seed: 1);
			ulong now = 80_000_000UL;
			long before = GC.GetAllocatedBytesForCurrentThread();
			for (int frame = 0; frame < 2000; frame++)
			{
				if (frame % 2 == 0)
				{
					line.Observe(100 + frame, new Transform3D(new Basis(Vector3.Up, 0.01f * frame), new Vector3(Step * frame, 0f, 0f)));
				}

				line.Advance(now + ((ulong)frame * FrameUsec), double.NaN);
			}

			return GC.GetAllocatedBytesForCurrentThread() - before;
		});
		AssertThat(line.FittingArrivals).IsTrue();
		AssertThat(allocated).IsEqual(0L);
	}

	[TestCase]
	public void TheFitAnswersAChangeOfWalkAndStopsExactly()
	{
		MotionReplay.Response fit = Fitting(() => MotionReplay.Respond(() => new MotionReplay.Timeline()));

		// Measured: reversal 4 cm short, 200 ms late; 90° corner cut 16 cm, heading in 250 ms;
		// half speed within 15% in 650 ms; a dead stop overrun 21 cm, within 1 cm by 0.8 s.
		AssertThat(fit.ReverseOvershootCm).IsLess(5.0);
		AssertThat(fit.ReverseLateMs).IsLess(260.0);
		AssertThat(fit.CornerCutCm).IsLess(22.0);
		AssertThat(fit.HeadingMs).IsLess(330.0);
		AssertThat(fit.StopOverrunCm).IsLess(26.0);
		AssertThat(fit.SettleMs).IsLess(1100.0);
		AssertThat(fit.StopsExactly).IsTrue();
	}

	[TestCase]
	public void TheKeyIsARegisteredLiveCVarDefaultOff()
	{
		RuntimeCVarDefinition? definition = RuntimeCVarsGodot.Definitions.FirstOrDefault(d => d.Name == Engine_TickTimeline.ArrivalFitKey);
		AssertThat(definition).IsNotNull();
		AssertThat(definition!.Type).IsEqual(RuntimeCVarType.Bool);
		AssertThat(definition.DefaultValue).IsEqual("false");

		bool before = Engine_TickTimeline.ArrivalFit;
		try
		{
			Engine_TickTimeline.Configure(new Dictionary<string, string> { [Engine_TickTimeline.ArrivalFitKey] = "true" });
			AssertThat(Engine_TickTimeline.ArrivalFit).IsTrue();
			Engine_TickTimeline.Configure(new Dictionary<string, string>());
			AssertThat(Engine_TickTimeline.ArrivalFit).IsFalse();
		}
		finally
		{
			Engine_TickTimeline.ArrivalFit = before;
		}

		// The debug channel's render op flips it live, next to the other motion keys.
		string server = File.ReadAllText(Source("../../../src/game/GameDebugServer.cs"));
		AssertThat(server).Contains("root.TryGetProperty(\"motionArrivalFit\"");
		AssertThat(server).Contains("Refii.Core.Engine_TickTimeline.ArrivalFit = maf.GetBoolean();");
	}

	[TestCase]
	public void ThePerformancePresetTurnsItOnAndADesktopKeepsItOff()
	{
		// Phones: Performance on (the S10e's paired walks), Quality off (the S25+ runs at full speed
		// and measured nothing for its latency); a phone still on Performance takes it on its next
		// start (revision 6).
		AssertThat(GraphicsPresets.PerformanceValues[Engine_TickTimeline.ArrivalFitKey]).IsEqual("true");
		AssertThat(GraphicsPresets.QualityValues[Engine_TickTimeline.ArrivalFitKey]).IsEqual("false");
		AssertThat(GraphicsPresets.Revision).IsGreaterEqual(6);
		var phone = new Dictionary<string, string> { [GraphicsPresets.Key] = GraphicsPresets.Performance, [GraphicsPresets.RevisionKey] = "5" };
		AssertThat(GraphicsPresets.Upgrade(phone)).IsTrue();
		AssertThat(phone[Engine_TickTimeline.ArrivalFitKey]).IsEqual("true");

		// A desktop: the presets are neither offered nor written, so the key keeps its default, off.
		bool offered = GraphicsPresets.Offered;
		try
		{
			GraphicsPresets.Offered = false;
			var model = new RuntimeSettingsModel();
			model.Values.Remove(GraphicsPresets.Key);
			AssertThat(model.RefreshGraphicsPreset(Engine_TickTimeline.ArrivalFitKey)).IsFalse();
			AssertThat(RuntimeCVarsGodot.Definitions.First(d => d.Name == Engine_TickTimeline.ArrivalFitKey).DefaultValue).IsEqual("false");
		}
		finally
		{
			GraphicsPresets.Offered = offered;
		}
	}

	private static string Source(string relative, [CallerFilePath] string source = "") =>
		Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, relative));
}
