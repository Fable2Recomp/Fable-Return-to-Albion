using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;
using Refii.Core;
using Refii.Entities;

namespace Refii.Launcher.Tools;

/// <summary>
/// Does a limb move as evenly as the body carrying it? The animation timing path, outside the
/// game, against a simulated guest whose arrival timing is the real one's.
///
/// Simulated: Godot's frames (rate, jitter, hitches), the session's 30 Hz fixed-step grant (one
/// step a frame, `GameSession.MaxStepsPerFrame`), the guest computing each granted step on its
/// own thread for a variable time, the session reading the game tick near the frame's start
/// and the walk reading the guest's state later in the frame, and only on frames where a new
/// guest frame landed. The guest moves everything at exactly one second per second, so every
/// series should advance by exactly the frame's duration.
///
/// Real: <see cref="Engine_DrawnFrame"/> (frame instant, restate, draw point),
/// <see cref="Engine_TickClock.PublishGuestGameTick"/>, <see cref="Refii.Animations.Engine_ClipPlayhead"/>
/// (clip time on Godot's clock), <see cref="Engine_StampRing"/> (the pose ring's lookup and half-tick stamps) and
/// <see cref="Engine_PairPlayhead"/> (the body's carrying pair, and the limbs' old
/// arrival-anchored pair, run exactly as the code before this lab ran them).
///
///   godot --headless --path Godot res://debug/anim_timing_lab.tscn
///
/// Prints one `[anim-lab]` table per scenario: per series, the frame-to-frame rate (1.000 is
/// perfect), the share of frames that HOLD (under 0.25x) or JUMP (over 1.75x), the RMS rate
/// error, and the jitter of the drawn value around a constant lag in milliseconds.
/// REFII_LAB_CSV=/abs/file.csv writes every frame of REFII_LAB_SCENARIO (default the first).
/// </summary>
public partial class AnimTimingLab : Node
{
	private sealed record Scenario(
		string Name, double Hz, double JitterFraction, int HitchEvery, double HitchMs,
		double LatencyMinMs, double LatencyMaxMs, double WalkMinMs, double WalkMaxMs);

	private static readonly Scenario[] Scenarios =
	{
		new("60hz", 60, 0.02, 0, 0, 3, 12, 1, 4),
		new("75hz", 75, 0.02, 0, 0, 3, 12, 1, 4),
		new("120hz", 120, 0.02, 0, 0, 3, 12, 1, 4),
		new("144hz", 144, 0.02, 0, 0, 3, 12, 1, 4),
		new("165hz", 165, 0.02, 0, 0, 3, 12, 1, 4),
		new("uncapped-90-jitter", 90, 0.20, 300, 40, 3, 12, 1, 4),
		new("uncapped-110-loaded", 110, 0.25, 200, 60, 5, 28, 1, 6),
		new("45fps", 45, 0.05, 0, 0, 3, 12, 1, 4),

		// The run measured live 2026-10-14: 49 fps, the rig reads well into the frame, short
		// guest steps — a read there often sees the step granted at the top of the same frame.
		new("live-49fps-late-reads", 49, 0.25, 0, 0, 2, 8, 6, 14),

		// The guest behind real time, as `[game-pace]` logged it: ~19.6 steps a second (65%).
		new("slow-guest-65pct", 100, 0.10, 0, 0, 45, 57, 4, 12),
	};

	private const double StepUsec = 1e6 / 30.0;
	private const double Seconds = 30.0;
	private const double WarmupSeconds = 2.0;
	private const int PoseSlots = 6;

	/// <summary>The clip's loop length: the hero's RunStrong (0.833 s), unwrapped times as the guest states them.</summary>
	private static readonly double LoopLength =
		double.TryParse(OS.GetEnvironment("REFII_LAB_LOOP"), System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out double loop) ? loop : 0.0;

	/// <summary>One frame's drawn values, in seconds of guest motion.</summary>
	private readonly record struct Frame(double Usec, double Body, double ClipNew, double PoseNew,
		double ClipOld, double PoseOld, double PoseIdeal, double BodyNew);

	public override void _Ready()
	{
		string csv = OS.GetEnvironment("REFII_LAB_CSV");
		string pick = OS.GetEnvironment("REFII_LAB_SCENARIO");
		GD.Print("[anim-lab] rate p5/p50/p95 = drawn motion per real second (1.000 = even); " +
			"hold = frames under 0.25x, jump = over 1.75x; rms = RMS rate error; jitter = ms off a constant lag");
		for (int i = 0; i < Scenarios.Length; ++i)
		{
			Scenario scenario = Scenarios[i];
			List<Frame> early = Run(scenario, 100_000u * (uint)((2 * i) + 1), seed: 1234 + i, grantLast: false);
			List<Frame> late = Run(scenario, 100_000u * (uint)((2 * i) + 2), seed: 1234 + i, grantLast: true);
			Report(scenario, early, late);
			bool wanted = pick.Length == 0 ? i == 0 : pick == scenario.Name;
			if (csv.Length != 0 && wanted)
			{
				WriteCsv(csv, late);
			}
		}

		GetTree().Quit();
	}

	/// <param name="grantLast">
	/// False: the session grants the next step at the top of the frame, before the camera, the
	/// walk and the rig reads (GameSession processing first). True: after them, so a frame that
	/// saw a new guest frame reads a guest parked between steps.
	/// </param>
	private static List<Frame> Run(Scenario s, uint tickBase, int seed, bool grantLast)
	{
		var rng = new Random(seed);
		var frames = new List<Frame>();

		// The guest: step g is granted during a frame and finishes after its compute time, never
		// before the step ahead of it (one game thread). Its HF pass counts the step (the frame
		// counter, offset so the stamp's parity has to be learned) and the game tick moves on
		// every second step.
		var done = new List<double>();
		const uint frameOffset = 7u;
		int lastFrameSeen = -1;

		// NEW path state.
		var clip = new Refii.Animations.Engine_ClipPlayhead();
		uint clipStamp = uint.MaxValue;
		var poseRing = new Engine_StampRing(PoseSlots);
		var poseValue = new double[PoseSlots];
		var idealRing = new Engine_StampRing(PoseSlots);
		var idealValue = new double[PoseSlots];

		// OLD path state: the per-series pair anchored at the walk's wall clock, carry off.
		var oldClip = new Engine_PairPlayhead(Engine_TickClock.TickUsec);
		double oldClipPrev = 0, oldClipCur = 0;
		uint oldClipTick = 0;
		var oldPose = new Engine_PairPlayhead(StepUsec);
		var oldPoseStamps = new Engine_StampRing(2);
		double oldPosePrev = 0, oldPoseCur = 0;
		int oldPoses = 0;

		// The body: GameEntities.Drawn — a carrying pair per entity, swapped at the frame instant
		// over the width the guest's clock states.
		var body = new Engine_PairPlayhead(Engine_TickClock.TickUsec, carry: true);
		double bodyPrev = 0, bodyCur = 0, bodyGuestSeconds = double.NaN;
		uint bodyTick = 0;
		var bodyLine = new Engine_TickTimeline();

		double accumulated = 0;
		double now = 1e6;
		double last = now;
		double lastDraw = now;
		int frameIndex = 0;
		while (now < 1e6 + (Seconds * 1e6))
		{
			++frameIndex;
			double frame = 1e6 / s.Hz * (1.0 + (Gaussian(rng) * s.JitterFraction));
			if (s.HitchEvery > 0 && frameIndex % s.HitchEvery == 0)
			{
				frame += s.HitchMs * 1000.0;
			}

			now += Math.Max(frame, 1000.0);
			double delta = now - last;
			last = now;
			Engine_DrawnFrame.BeginFrame((ulong)now);

			// Frame start (GameWindow): the guest frame and the tick, read together.
			int atStart = Completed(done, now);
			uint tick = tickBase + (uint)(atStart / 2);
			Engine_TickClock.PublishGuestGameTick(tick);
			Engine_TickClock.PublishGuestFrame((uint)atStart + frameOffset, tick);
			bool changed = atStart != lastFrameSeen;
			lastFrameSeen = atStart;

			double walkAt = now + ((s.WalkMinMs + (rng.NextDouble() * (s.WalkMaxMs - s.WalkMinMs))) * 1000.0);
			double grantAt = grantLast ? walkAt + ((1.0 + (rng.NextDouble() * 2.0)) * 1000.0) : now + 300.0;
			accumulated += delta;
			if (accumulated >= StepUsec)
			{
				accumulated -= StepUsec;
				double compute = (s.LatencyMinMs + (rng.NextDouble() * (s.LatencyMaxMs - s.LatencyMinMs))) * 1000.0;
				double start = done.Count == 0 ? grantAt : Math.Max(grantAt, done[^1]);
				done.Add(start + compute);
			}

			accumulated = Math.Min(accumulated, StepUsec * 4);

			// The camera restates every tick (GameWorld); deduped on the tick inside.
			Engine_DrawnFrame.NoteRestated();

			// The walk: only on a new guest frame, reading the guest later in the frame — which,
			// with the grant first, may already be the NEXT step.
			int seen = Completed(done, walkAt);

			// Placements come from the entity walk, kicked on its worker at the top of the frame
			// (HostEntityWalk.Kick in GameWindow), not from the late rig reads.
			int placed = Completed(done, now + ((0.2 + (rng.NextDouble() * 1.3)) * 1000.0));
			if (changed && seen >= 2)
			{
				double readTick = tickBase + (seen / 2);
				double stated = readTick / 15.0;                          // clip time and body: T/15
				double pose = (tickBase / 15.0) + ((seen - 1) / 30.0);   // the HF step's pose

				// Clip time, new: the pair read now, under the frame's tick, on every read
				// (the playhead tells a new tick by the pair's own continuity).
				clipStamp = tick;
				clip.Observe((readTick - 1.0) / 15.0, stated, tick, LoopLength, LoopLength > 0.0, false,
					Engine_DrawnFrame.DrawTick);

				// Clip time, old: the arrival-anchored pair the clip time used to be drawn on.
				if (tick != oldClipTick)
				{
					double span = oldClipTick == 0 ? 1.0 : Math.Max(1.0, (double)tick - oldClipTick);
					oldClip.SwapStated(walkAt, span * Engine_TickClock.TickUsec);
					oldClipPrev = oldClipTick == 0 ? stated : oldClipCur;
					oldClipCur = stated;
					oldClipTick = tick;
				}

				// Pose, new: the ring, stamped as Engine_EntitySkeleton stamps it.
				double stamp = Engine_TickClock.FrameStamp;
				if (double.IsNaN(stamp))
				{
					stamp = poseRing.NextHalfTick(tick);
				}

				if (poseRing.Count == 0 || stamp > poseRing.NewestStamp)
				{
					poseValue[poseRing.Push(stamp)] = pose;
				}
				idealValue[idealRing.Push(tickBase + ((seen - 1) / 2.0))] = pose;

				// Pose, old: the pair anchored at the walk, width from its order stamps.
				double previousStamp = oldPoseStamps.NewestStamp;
				double newStamp = oldPoseStamps.NextHalfTick(tick);
				oldPoseStamps.Push(newStamp);
				double width = oldPoses > 0 && newStamp > previousStamp ? newStamp - previousStamp : 0.5;
				oldPose.SwapStated(walkAt, width * Engine_TickClock.TickUsec);
				oldPosePrev = oldPoses == 0 ? pose : oldPoseCur;
				oldPoseCur = pose;
				++oldPoses;

				// Body, new: every read stated (GameEntities.Drawn), on the steered timeline.
				// In metres near the origin, as a body's are: a transform is single precision.
				double placedAt = (placed / 2) / 15.0;
				bodyLine.Observe(tick, new Transform3D(Basis.Identity, new Vector3((float)placedAt, 0f, 0f)));

				// Body, old: the per-entity pair anchored at the frame it was noticed on.
				if (tick != bodyTick)
				{
					double guestSeconds = seen / 30.0;
					double statedUsec = double.IsNaN(bodyGuestSeconds) ? 0.0 : (guestSeconds - bodyGuestSeconds) * 1e6;
					if (statedUsec > Engine_TickClock.TickUsec * 0.25 && statedUsec < Engine_TickClock.TickUsec * 8.0)
					{
						body.SwapStated(now, statedUsec);
					}
					else
					{
						int steps = bodyTick == 0 ? 1 : (int)Math.Clamp((long)tick - bodyTick, 1L, 8L);
						body.Swap(now, steps);
					}

					bodyPrev = bodyTick == 0 ? stated : bodyCur;
					bodyCur = stated;
					bodyTick = tick;
					bodyGuestSeconds = guestSeconds;
				}
			}

			if (!body.Ready || poseRing.Count < 2)
			{
				lastDraw = now;
				continue;
			}

			// Draw.
			double drawTick = Engine_DrawnFrame.DrawTick;
			double bodyX = bodyLine.Advance((ulong)now, drawTick).Origin.X + (tickBase / 15.0);
			_ = placed;
			double clipNow = clip.Advance((now - lastDraw) / 1e6, drawTick);
			lastDraw = now;
			frames.Add(new Frame(now,
				bodyPrev + ((bodyCur - bodyPrev) * body.Alpha(now)),
				clipNow,
				PoseAt(poseRing, poseValue, drawTick),
				oldClipPrev + ((oldClipCur - oldClipPrev) * oldClip.Alpha(now)),
				oldPosePrev + ((oldPoseCur - oldPosePrev) * oldPose.Alpha(now)),
				PoseAt(idealRing, idealValue, drawTick),
				bodyX));
		}

		return frames;
	}

	/// <summary>The pose ring's draw, as `Engine_EntitySkeleton.WriteBlended` does it, on a scalar pose.</summary>
	private static double PoseAt(Engine_StampRing ring, double[] values, double drawTick) =>
		ring.Locate(drawTick, 4.0, out int earlier, out int later, out double t) == Engine_StampRing.Where.Between
			? values[earlier] + ((values[later] - values[earlier]) * t)
			: values[later];

	private static int Completed(List<double> done, double at)
	{
		int n = 0;
		while (n < done.Count && done[n] <= at)
		{
			++n;
		}

		return n;
	}

	private static double Gaussian(Random rng)
	{
		double u = 1.0 - rng.NextDouble();
		double v = rng.NextDouble();
		return Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * v);
	}

	private static void Report(Scenario s, List<Frame> early, List<Frame> late)
	{
		GD.Print($"[anim-lab] --- {s.Name}: {s.Hz:0} Hz, jitter {s.JitterFraction:P0}, guest step {s.LatencyMinMs:0}-{s.LatencyMaxMs:0} ms" +
			(s.HitchEvery > 0 ? $", {s.HitchMs:0} ms hitch every {s.HitchEvery} frames" : string.Empty));
		GD.Print($"[anim-lab] {"series",-30} {"p5",7} {"p50",7} {"p95",7} {"hold",7} {"jump",7} {"rms",7} {"jitter",8}");
		Line("body, grant first", early, f => f.Body);
		Line("clip time, before", early, f => f.ClipOld);
		Line("pose copy, before", early, f => f.PoseOld);
		Line("clip time, after, grant first", early, f => f.ClipNew);
		Line("pose copy, after, grant first", early, f => f.PoseNew);
		Line("body, grant last", late, f => f.Body);
		Line("body steered, grant last", late, f => f.BodyNew);
		Skate("skate: old body vs clip", late, f => f.Body - f.ClipNew);
		Skate("skate: body steered vs clip", late, f => f.BodyNew - f.ClipNew);
		Line("clip time, after, grant last", late, f => f.ClipNew);
		GD.Print($"[anim-lab] playhead restarts {Refii.Animations.Engine_ClipPlayhead.Restarts}, saturated {Refii.Animations.Engine_ClipPlayhead.Saturated} (both runs)");
		Refii.Animations.Engine_ClipPlayhead.Restarts = 0;
		Refii.Animations.Engine_ClipPlayhead.Saturated = 0;
		Line("pose copy, after, grant last", late, f => f.PoseNew);
		Line("pose, ideal stamp, grant last", late, f => f.PoseIdeal);
	}

	private static void Line(string name, List<Frame> frames, Func<Frame, double> of)
	{
		var rates = new List<double>();
		var offsets = new List<double>();
		double warm = frames.Count == 0 ? 0 : frames[0].Usec + (WarmupSeconds * 1e6);
		for (int i = 1; i < frames.Count; ++i)
		{
			if (frames[i].Usec < warm)
			{
				continue;
			}

			double dt = (frames[i].Usec - frames[i - 1].Usec) / 1e6;
			rates.Add((of(frames[i]) - of(frames[i - 1])) / dt);
			offsets.Add((frames[i].Usec / 1e6) - of(frames[i]));
		}

		if (rates.Count == 0)
		{
			GD.Print($"[anim-lab] {name,-30} no frames");
			return;
		}

		double holds = 0, jumps = 0, square = 0;
		foreach (double r in rates)
		{
			holds += r < 0.25 ? 1 : 0;
			jumps += r > 1.75 ? 1 : 0;
			square += (r - 1.0) * (r - 1.0);
		}

		double mean = 0;
		foreach (double o in offsets)
		{
			mean += o;
		}

		mean /= offsets.Count;
		double variance = 0;
		foreach (double o in offsets)
		{
			variance += (o - mean) * (o - mean);
		}

		rates.Sort();
		GD.Print(string.Create(CultureInfo.InvariantCulture,
			$"[anim-lab] {name,-30} {Pct(rates, 0.05),7:0.000} {Pct(rates, 0.50),7:0.000} {Pct(rates, 0.95),7:0.000} " +
			$"{holds / rates.Count,7:P1} {jumps / rates.Count,7:P1} {Math.Sqrt(square / rates.Count),7:0.000} " +
			$"{Math.Sqrt(variance / offsets.Count) * 1000.0,6:0.00}ms"));
	}

	/// <summary>How much a body and the clip it plays wander against each other: the spread of their difference.</summary>
	private static void Skate(string name, List<Frame> frames, Func<Frame, double> of)
	{
		var d = new List<double>();
		double warm = frames.Count == 0 ? 0 : frames[0].Usec + (WarmupSeconds * 1e6);
		foreach (Frame f in frames)
		{
			if (f.Usec >= warm)
			{
				d.Add(of(f));
			}
		}

		if (d.Count == 0)
		{
			return;
		}

		double mean = 0;
		foreach (double x in d)
		{
			mean += x;
		}

		mean /= d.Count;
		double variance = 0;
		double worst = 0;
		foreach (double x in d)
		{
			variance += (x - mean) * (x - mean);
			worst = Math.Max(worst, Math.Abs(x - mean));
		}

		GD.Print(string.Create(CultureInfo.InvariantCulture,
			$"[anim-lab] {name,-36} sd {Math.Sqrt(variance / d.Count) * 1000.0:0.00} ms, worst {worst * 1000.0:0.00} ms"));
	}

	private static double Pct(List<double> sorted, double p) =>
		sorted[Math.Clamp((int)(p * (sorted.Count - 1)), 0, sorted.Count - 1)];

	private static void WriteCsv(string path, List<Frame> frames)
	{
		var text = new StringBuilder("usec,body,clip_before,clip_after,pose_before,pose_after,pose_ideal\n");
		foreach (Frame f in frames)
		{
			text.Append(string.Create(CultureInfo.InvariantCulture,
				$"{f.Usec:0},{f.Body:0.000000},{f.ClipOld:0.000000},{f.ClipNew:0.000000},{f.PoseOld:0.000000},{f.PoseNew:0.000000},{f.PoseIdeal:0.000000}\n"));
		}

		File.WriteAllText(path, text.ToString());
		GD.Print($"[anim-lab] wrote {frames.Count} frames to {path}");
	}
}
