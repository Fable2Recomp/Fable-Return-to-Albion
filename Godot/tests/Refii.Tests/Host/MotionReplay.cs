namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Refii.Core;

/// <summary>
/// One hero trace from the phone (`herotrace.py`, the debug channel's `camera` and `entity` trace
/// ops) as the statement stream the drawing saw: per drawn frame, its draw instant, the hero's
/// newest stated position and its stamp, the frame's draw point, and what was drawn.
/// </summary>
/// <remarks>
/// The hero rows are sampled at the end of the frame's entity work (`usec`, ±20 ms against the
/// frame start); the frame's instant is the camera row's `now`, the one every draw point read. A
/// camera row is missing on a frame whose camera read was torn: that frame is taken one 50 ms
/// period after the last. `stated` is the body timeline's newest statement and `newest` its stamp,
/// so a change of either is an observation; the stamp falls back to the row's sim `tick` in a trace
/// recorded before `newest` was.
/// </remarks>
internal sealed class HeroTrace
{
	/// <summary>One drawn frame.</summary>
	public readonly record struct Frame(ulong Now, Vector3 Stated, double Stamp, double DrawTick, Vector3 Recorded, bool New);

	public string Name { get; }

	public IReadOnlyList<Frame> Frames { get; }

	/// <summary>The pace the phone reported at the end of the trace.</summary>
	public double Pace { get; }

	private HeroTrace(string name, List<Frame> frames, double pace)
	{
		Pace = pace;
		Name = name;
		Frames = frames;
	}

	public static HeroTrace Load(string path)
	{
		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
		JsonElement root = document.RootElement;
		var nows = new List<ulong>();
		foreach (JsonElement row in root.GetProperty("cam").EnumerateArray())
		{
			nows.Add(row.GetProperty("now").GetUInt64());
		}

		var frames = new List<Frame>();
		int lastIndex = -2;
		ulong lastNow = 0UL;
		Frame? previous = null;
		foreach (JsonElement row in root.GetProperty("hero").EnumerateArray())
		{
			ulong usec = row.GetProperty("usec").GetUInt64();
			int index = UpperBound(nows, usec) - 1;
			ulong now = index >= 0 && index != lastIndex ? nows[index]
				: index >= 0 ? lastNow + 50_000UL
				: 0UL;
			lastIndex = index;
			lastNow = now;
			Vector3 stated = Vec(row.GetProperty("stated"));
			double stamp = row.TryGetProperty("newest", out JsonElement newest) && newest.ValueKind == JsonValueKind.Number
				? newest.GetDouble()
				: row.GetProperty("tick").GetDouble();
			bool isNew = previous is not { } p || p.Stated != stated || p.Stamp != stamp;
			var frame = new Frame(now, stated, stamp, row.GetProperty("drawTick").GetDouble(), Vec(row.GetProperty("drawn")), isNew);
			frames.Add(frame);
			previous = frame;
		}

		// Rows before the first camera row: a period apart, back from the first known instant.
		for (int i = frames.Count - 2; i >= 0; i--)
		{
			if (frames[i].Now == 0UL)
			{
				frames[i] = frames[i] with { Now = frames[i + 1].Now - 50_000UL };
			}
		}

		double pace = root.TryGetProperty("camera", out JsonElement camera) && camera.TryGetProperty("pace", out JsonElement reported) &&
			reported.ValueKind == JsonValueKind.Number ? reported.GetDouble() : 1.0;
		return new HeroTrace(Path.GetFileNameWithoutExtension(path).Replace("herotrace-", string.Empty, StringComparison.Ordinal),
			frames, pace);
	}

	private static int UpperBound(List<ulong> sorted, ulong value)
	{
		int lo = 0, hi = sorted.Count;
		while (lo < hi)
		{
			int mid = (lo + hi) / 2;
			if (sorted[mid] <= value)
			{
				lo = mid + 1;
			}
			else
			{
				hi = mid;
			}
		}

		return lo;
	}

	private static Vector3 Vec(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
}

/// <summary>
/// Replays a trace's statement stream through a drawing method frame by frame, and scores what it
/// draws the way `stepstat.py` scores the phone: the step evenness, its lag behind the stated path,
/// and its drift against the legs' clock.
/// </summary>
internal static class MotionReplay
{
	/// <summary>A drawing method: told each statement as its frame reads it, asked for each frame's position.</summary>
	public interface IMethod
	{
		void Observe(ulong now, double stamp, Vector3 stated);

		Vector3 Advance(ulong now, double drawTick);
	}

	/// <summary>
	/// `stepstat.py`: the per-frame step against its local median (7 frames) while walking (median
	/// at least 8 cm): its sd and mean frame-to-frame change in % of the median, and the frames under
	/// half or over 1.6x of it (stalls and lurches).
	/// </summary>
	public readonly record struct Evenness(double StepSd, double MeanChange, int StallsLurches, int Frames);

	/// <summary>
	/// The lag behind the statements laid out on their arrival times, per walking frame: the
	/// nearest point of that path within the last 0.7 s, how long before the frame it was stated
	/// (ms, mean and p95), and how far off the path the drawn point is (cm, mean and p95 — a
	/// turn's corner cut or overshoot). <see cref="PastNewest"/>: frames whose arrival-fit draw
	/// point (the frame less 100 ms) is past the newest arrival, in %.
	/// </summary>
	public readonly record struct Lag(double MeanMs, double P95Ms, double OffMeanCm, double OffP95Cm, double PastNewest);

	public static List<Vector3> Run(HeroTrace trace, IMethod method)
	{
		var drawn = new List<Vector3>(trace.Frames.Count);
		foreach (HeroTrace.Frame frame in trace.Frames)
		{
			if (frame.New)
			{
				method.Observe(frame.Now, frame.Stamp, frame.Stated);
			}

			drawn.Add(method.Advance(frame.Now, frame.DrawTick));
		}

		return drawn;
	}

	public static List<double> Steps(IReadOnlyList<Vector3> drawn)
	{
		var steps = new List<double>();
		for (int i = 1; i < drawn.Count; i++)
		{
			steps.Add(drawn[i].DistanceTo(drawn[i - 1]) * 100.0);
		}

		return steps;
	}

	public static Evenness Score(IReadOnlyList<Vector3> drawn)
	{
		List<double> steps = Steps(drawn);
		var ok = new List<double>();
		var change = new List<double>();
		int bad = 0;
		for (int i = 3; i < steps.Count - 3; i++)
		{
			double median = Median(steps.Skip(i - 3).Take(7));
			if (median < 8.0)
			{
				continue;
			}

			ok.Add(steps[i] / median);
			change.Add(Math.Abs(steps[i] - steps[i - 1]) / median);
			if (steps[i] < 0.5 * median || steps[i] > 1.6 * median)
			{
				++bad;
			}
		}

		return new Evenness(100.0 * StdDev(ok), 100.0 * change.Average(), bad, ok.Count);
	}

	public static Lag LagOf(HeroTrace trace, IReadOnlyList<Vector3> drawn)
	{
		var points = trace.Frames.Where(f => f.New).Select(f => (f.Now, f.Stated)).ToList();
		var steps = new List<double> { 0.0 };
		steps.AddRange(Steps(drawn));
		var lags = new List<double>();
		var offs = new List<double>();
		int past = 0, counted = 0;
		for (int k = 5; k + 4 <= trace.Frames.Count; k++)
		{
			if (Median(steps.Skip(k - 3).Take(7)) < 8.0)
			{
				continue;
			}

			ulong now = trace.Frames[k].Now;
			double bestDistance = double.MaxValue, bestAt = double.NaN;
			ulong newest = points.Where(q => q.Now <= now).Select(q => q.Now).DefaultIfEmpty(0UL).Max();
			for (int i = 0; i + 1 < points.Count; i++)
			{
				(ulong t0, Vector3 p0) = points[i];
				(ulong t1, Vector3 p1) = points[i + 1];
				if (t0 > now)
				{
					break;
				}

				if (t1 + 700_000UL < now)
				{
					continue;
				}

				Vector3 segment = p1 - p0;
				float length = segment.LengthSquared();
				float u = length < 1e-12f ? 0f : Math.Clamp((drawn[k] - p0).Dot(segment) / length, 0f, 1f);
				double distance = drawn[k].DistanceTo(p0 + (segment * u));
				if (distance < bestDistance)
				{
					bestDistance = distance;
					bestAt = t0 + (u * (double)(t1 - t0));
				}
			}

			if (double.IsNaN(bestAt))
			{
				continue;
			}

			++counted;
			if (now - Engine_ArrivalFit.DelayUsec > newest)
			{
				++past;
			}

			offs.Add(bestDistance * 100.0);
			lags.Add((now - bestAt) / 1000.0);
		}

		lags.Sort();
		offs.Sort();
		return new Lag(lags.Average(), lags[(int)(0.95 * lags.Count)], offs.Average(), offs[(int)(0.95 * offs.Count)],
			100.0 * past / Math.Max(1, counted));
	}

	/// <summary>
	/// The body's draw point against the legs': how far (ms) the instant the body is drawn at
	/// (<see cref="LagOf"/>'s nearest point on the stated path) leads the instant the legs' clip is
	/// at — an Engine_ClipPlayhead on a one-tick-a-tick clip, stated by the same stamps and drawn at
	/// the trace's DrawTick, its tick mapped to host time by a 2 s line through (stamp, arrival).
	/// A constant offset is a fixed phase between stride and root; its change from frame to frame,
	/// times the walk's speed, is the feet sliding. Answers the mean, sd and mean |frame change|.
	/// </summary>
	public static (double Mean, double Sd, double FrameChange) BodyAgainstLegs(HeroTrace trace, IReadOnlyList<Vector3> drawn)
	{
		var legs = new Refii.Animations.Engine_ClipPlayhead();
		var legTicks = new List<double>();
		ulong last = 0UL;
		try
		{
			foreach (HeroTrace.Frame frame in trace.Frames)
			{
				// The clip advances by the frame's instant as the game's does (Engine_DrawnFrame.NowUsec).
				Refii.Entities.Engine_DrawnFrame.BeginFrame(frame.Now);
				if (frame.New)
				{
					legs.Observe((frame.Stamp - 1.0) / Engine_TickClock.GameTicksPerSecond,
						frame.Stamp / Engine_TickClock.GameTicksPerSecond, (uint)frame.Stamp, 0.0, false, false, frame.DrawTick);
				}

				double seconds = last == 0UL || frame.Now <= last ? 0.0 : Math.Min((frame.Now - last) * 1e-6, 0.25);
				last = frame.Now;
				legTicks.Add(legs.Advance(seconds, frame.DrawTick) * Engine_TickClock.GameTicksPerSecond);
			}
		}
		finally
		{
			Refii.Entities.Engine_DrawnFrame.BeginFrame(0);
		}

		var statements = trace.Frames.Where(f => f.New).ToList();
		var points = statements.Select(f => (f.Now, f.Stated)).ToList();
		var steps = new List<double> { 0.0 };
		steps.AddRange(Steps(drawn));
		var offsets = new List<(int Frame, double Ms)>();
		for (int k = 5; k + 4 <= trace.Frames.Count; k++)
		{
			if (Median(steps.Skip(k - 3).Take(7)) < 8.0)
			{
				continue;
			}

			ulong now = trace.Frames[k].Now;
			double bestDistance = double.MaxValue, bodyAt = double.NaN;
			for (int i = 0; i + 1 < points.Count && points[i].Now <= now; i++)
			{
				if (points[i + 1].Now + 700_000UL < now)
				{
					continue;
				}

				Vector3 segment = points[i + 1].Stated - points[i].Stated;
				float length = segment.LengthSquared();
				float u = length < 1e-12f ? 0f : Math.Clamp((drawn[k] - points[i].Stated).Dot(segment) / length, 0f, 1f);
				double distance = drawn[k].DistanceTo(points[i].Stated + (segment * u));
				if (distance < bestDistance)
				{
					bestDistance = distance;
					bodyAt = points[i].Now + (u * (double)(points[i + 1].Now - points[i].Now));
				}
			}

			// The stamps' host time, fitted over the 2 s around the frame: their ±0.5-tick errors average out.
			var near = statements.Where(s => Math.Abs((double)s.Now - now) < 1_000_000.0).ToList();
			double xb = near.Average(s => s.Stamp), yb = near.Average(s => (double)s.Now);
			double sxx = near.Sum(s => (s.Stamp - xb) * (s.Stamp - xb));
			if (double.IsNaN(bodyAt) || sxx <= 0.0)
			{
				continue;
			}

			double slope = near.Sum(s => (s.Stamp - xb) * (s.Now - yb)) / sxx;
			double legsAt = yb + (slope * (legTicks[k] - xb));
			offsets.Add((k, (bodyAt - legsAt) / 1000.0));
		}

		var values = offsets.Select(o => o.Ms).ToList();
		var changes = new List<double>();
		for (int i = 1; i < offsets.Count; i++)
		{
			if (offsets[i].Frame == offsets[i - 1].Frame + 1)
			{
				changes.Add(Math.Abs(offsets[i].Ms - offsets[i - 1].Ms));
			}
		}

		return (values.Average(), StdDev(values), changes.Average());
	}

	public static double Median(IEnumerable<double> values)
	{
		var sorted = values.OrderBy(v => v).ToList();
		int n = sorted.Count;
		return n % 2 == 1 ? sorted[n / 2] : 0.5 * (sorted[(n / 2) - 1] + sorted[n / 2]);
	}

	public static double StdDev(IReadOnlyCollection<double> values)
	{
		double mean = values.Average();
		return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
	}

	/// <summary>
	/// Runs <paramref name="body"/> with `motion_arrival_fit` set as given and the shared pace put
	/// back to a healthy guest before and after, so a replay neither inherits nor leaves state; a
	/// <paramref name="pace"/> other than 1 is played for thirty seconds first, so a replay starts
	/// where the phone's pace already was.
	/// </summary>
	public static T WithRules<T>(bool arrivalFit, Func<T> body, double pace = 1.0)
	{
		bool fitBefore = Engine_TickTimeline.ArrivalFit;
		Engine_StatedPause.BeginFrame(false);
		WarmPace(1.0);
		if (pace != 1.0)
		{
			WarmPace(pace);
		}

		Engine_TickTimeline.ArrivalFit = arrivalFit;
		try
		{
			return body();
		}
		finally
		{
			Engine_TickTimeline.ArrivalFit = fitBefore;
			WarmPace(1.0);
		}
	}

	/// <summary>Thirty seconds of a draw point advancing at <paramref name="pace"/>: Engine_DrawAdvance.Pace settles on it.</summary>
	private static void WarmPace(double pace)
	{
		_warmed += 100_000_000UL;
		ulong now = 800_000_000UL + _warmed;
		double drawTick = 5_000.0;
		for (int i = 0; i < 600; i++)
		{
			now += 50_000UL;
			drawTick += 0.05 * Engine_TickClock.GameTicksPerSecond * pace;
			Engine_DrawAdvance.Ticks(0.05, now, drawTick);
		}
	}

	private static ulong _warmed;

	/// <summary>
	/// The body's own path through Engine_TickTimeline (GameEntities.Drawn): reset on first sight
	/// and on a teleport, observed on each statement, advanced every frame at the frame's draw
	/// point. Under `motion_arrival_fit` this is the engine's arrival fit.
	/// </summary>
	public sealed class Timeline : IMethod
	{
		private readonly Engine_TickTimeline _line = new();

		public Engine_TickTimeline Line => _line;

		public void Observe(ulong now, double stamp, Vector3 stated)
		{
			var sample = new Transform3D(Basis.Identity, stated);
			if (_line.Empty || _line.Newest.Origin.DistanceSquaredTo(stated) >
				Engine_TickTimeline.TeleportMetres * Engine_TickTimeline.TeleportMetres)
			{
				_line.Reset(stamp, sample);
				return;
			}

			_line.Observe(stamp, sample);
		}

		public Vector3 Advance(ulong now, double drawTick) => _line.Advance(now, drawTick).Origin;
	}

	/// <summary>The newest statement drawn as it is: the raw stream.</summary>
	public sealed class Stated : IMethod
	{
		private Vector3 _newest;

		public void Observe(ulong now, double stamp, Vector3 stated) => _newest = stated;

		public Vector3 Advance(ulong now, double drawTick) => _newest;
	}

	/// <summary>
	/// The arrival-time fit drawn straight off its line: the least-squares line through the last
	/// <c>n</c> statements against arrival time, at the frame less <c>delay</c>, held
	/// <c>coast</c> past the newest arrival. With <c>omega</c> &gt; 0 a critically damped spring
	/// follows the line's position and slope instead.
	/// </summary>
	public sealed class RawFit(int n, double delayMs, double coastMs = 50.0, double omega = 0.0) : IMethod
	{
		private readonly List<(ulong Usec, Vector3 P)> _ring = new();
		private Vector3 _x;
		private Vector3 _u;
		private ulong _last;
		private bool _started;

		public void Observe(ulong now, double stamp, Vector3 stated)
		{
			if (_ring.Count > 0 && _ring[^1].Usec == now)
			{
				_ring[^1] = (now, stated);
				return;
			}

			_ring.Add((now, stated));
			if (_ring.Count > n)
			{
				_ring.RemoveAt(0);
			}
		}

		public Vector3 Advance(ulong now, double drawTick)
		{
			(ulong tn, Vector3 pn) = _ring[^1];
			double tb = _ring.Average(r => ((double)r.Usec - tn) * 1e-6);
			Vector3 pb = Vector3.Zero;
			foreach ((ulong _, Vector3 p) in _ring)
			{
				pb += (p - pn) / _ring.Count;
			}

			double variance = _ring.Sum(r => Math.Pow((((double)r.Usec - tn) * 1e-6) - tb, 2));
			Vector3 v = Vector3.Zero;
			if (variance > 1e-9)
			{
				foreach ((ulong t, Vector3 p) in _ring)
				{
					v += (p - pn - pb) * (float)(((((double)t - tn) * 1e-6) - tb) / variance);
				}
			}

			double at = ((double)now - (delayMs * 1000.0) - tn) * 1e-6;
			bool held = at > coastMs * 1e-3;
			at = Math.Min(at, coastMs * 1e-3);
			Vector3 target = pn + pb + (v * (float)(at - tb));
			Vector3 velocity = held ? Vector3.Zero : v;
			if (omega <= 0.0 || !_started)
			{
				_started = true;
				_last = now;
				_x = target;
				_u = velocity;
				return _x;
			}

			double h = now > _last ? Math.Min((now - _last) * 1e-6, 0.25) : 0.0;
			_last = now;
			Vector3 e = _x - (target - (velocity * (float)h));
			Vector3 de = _u - velocity;
			double ex = Math.Exp(-omega * h);
			Vector3 e2 = (e + ((de + (e * (float)omega)) * (float)h)) * (float)ex;
			Vector3 de2 = (de - ((de + (e * (float)omega)) * (float)(omega * h))) * (float)ex;
			_x = target + e2;
			_u = velocity + de2;
			return _x;
		}
	}

	/// <summary>
	/// A synthetic walk like the phone's: guest ticks 70-150 ms apart, 34 cm a tick along
	/// <c>+X</c>, changing at 3 s to <paramref name="after"/> per tick; each tick read on the first
	/// 50 ms frame at least 10 ms after it, only the newest of a frame's ticks seen. Answers the
	/// ticks (time, position) and the frames (instant, newest read, its tick).
	/// </summary>
	public static (List<(double T, Vector3 P)> Ticks, List<(ulong Now, Vector3 Stated, int Tick)> Frames)
		Synthetic(Vector3 after, int seed, double seconds = 6.0)
	{
		var random = new Random(seed);
		var ticks = new List<(double, Vector3)> { (0.0, Vector3.Zero) };
		double t = 0.0;
		Vector3 p = Vector3.Zero;
		while (t < seconds)
		{
			t += 0.07 + (0.08 * random.NextDouble());
			p += (t < 3.0 ? Vector3.Right : after) * 0.34f;
			ticks.Add((t, p));
		}

		var frames = new List<(ulong, Vector3, int)>();
		for (int k = 0; k < (int)(seconds / 0.05); k++)
		{
			double now = k * 0.05;
			int seen = 0;
			while (seen + 1 < ticks.Count && ticks[seen + 1].Item1 + 0.01 <= now)
			{
				++seen;
			}

			frames.Add((1_000_000UL + (ulong)(now * 1e6), ticks[seen].Item2, seen));
		}

		return (ticks, frames);
	}
	/// <summary>
	/// How a method answers a change of the walk (<see cref="Synthetic"/>, 20 seeds averaged):
	/// a reversal's overshoot past the turning point (cm) and how late the drawn turn is (ms); a 90°
	/// turn's widest cut off the stated path (cm) and when the drawn heading is within 10° of the
	/// new one (ms after the turn); when a halving of speed is drawn within 15% for 4 frames; a dead
	/// stop's overrun (cm), when it is drawn within 1 cm for good (ms), and whether it ends exactly
	/// on the stop. Statements follow the bodies' rule (GameEntities.IsBodyStatement): a move, or
	/// the unchanged place once 4 ticks old; stamps are the true ticks, and a timeline is drawn at
	/// the true tick 100 ms before the frame — an ideal shared draw point.
	/// </summary>
	public readonly record struct Response(double ReverseOvershootCm, double ReverseLateMs, double CornerCutCm,
		double HeadingMs, double HalfSpeedMs, double StopOverrunCm, double SettleMs, bool StopsExactly);

	public static Response Respond(Func<IMethod> make)
	{
		var reverse = new List<(double, double)>();
		var turn = new List<(double, double)>();
		var slow = new List<double>();
		var stop = new List<(double, double, bool)>();
		for (int seed = 0; seed < 20; seed++)
		{
			var (ticks, frames, drawn) = Drive(make(), Vector3.Left, seed);
			Vector3 peak = ticks.Where(t => t.T < 3.0).Last().P;
			double peakAt = ticks.Where(t => t.T < 3.0).Last().T;
			int i = Enumerable.Range(0, drawn.Count).MaxBy(k => drawn[k].X);
			reverse.Add(((drawn[i].X - peak.X) * 100.0, ((frames[i].Now - 1_000_000UL) * 1e-6 - peakAt) * 1000.0));

			(ticks, frames, drawn) = Drive(make(), Vector3.Back, seed);
			double cut = 0.0;
			double heading = double.NaN;
			for (int k = 1; k < drawn.Count; k++)
			{
				double off = double.MaxValue;
				for (int j = 0; j + 1 < ticks.Count; j++)
				{
					Vector3 a = ticks[j].P, segment = ticks[j + 1].P - a;
					float u = Math.Clamp((drawn[k] - a).Dot(segment) / segment.LengthSquared(), 0f, 1f);
					off = Math.Min(off, drawn[k].DistanceTo(a + (segment * u)));
				}

				cut = Math.Max(cut, off);
				double t = (frames[k].Now - 1_000_000UL) * 1e-6;
				Vector3 step = drawn[k] - drawn[k - 1];
				if (double.IsNaN(heading) && t > 3.0 && step.Z > 0f && step.Length() > 1e-4f &&
					Math.Atan2(Math.Abs(step.X), step.Z) < Mathf.DegToRad(10f))
				{
					heading = (t - 3.0) * 1000.0;
				}
			}

			turn.Add((cut * 100.0, heading));

			(ticks, frames, drawn) = Drive(make(), Vector3.Right * 0.5f, seed);
			var after = ticks.Where(t => t.T >= 3.0).ToList();
			double speed = (after[^1].P.X - after[0].P.X) / (after[^1].T - after[0].T);
			double reached = double.NaN;
			for (int k = 1; k + 4 < drawn.Count && double.IsNaN(reached); k++)
			{
				double t = (frames[k].Now - 1_000_000UL) * 1e-6;
				bool near = Enumerable.Range(k, 4).All(j =>
					Math.Abs(((drawn[j].X - drawn[j - 1].X) / ((frames[j].Now - frames[j - 1].Now) * 1e-6)) - speed) < 0.15 * speed);
				if (t > 3.0 && near)
				{
					reached = (t - 3.0) * 1000.0;
				}
			}

			slow.Add(reached);

			IMethod method = make();
			(ticks, frames, drawn) = Drive(method, Vector3.Zero, seed);
			Vector3 stand = ticks.Where(t => t.T < 3.0).Last().P;
			int settled = drawn.Count;
			while (settled > 0 && drawn[settled - 1].DistanceTo(stand) < 0.01f)
			{
				--settled;
			}

			bool exact = drawn[^1] == stand && (method is not Timeline line || line.Line.Settled);
			stop.Add(((drawn.Max(d => d.X) - stand.X) * 100.0,
				(((frames[Math.Min(settled, drawn.Count - 1)].Now - 1_000_000UL) * 1e-6) - 3.0) * 1000.0, exact));
		}

		return new Response(reverse.Average(r => r.Item1), reverse.Average(r => r.Item2), turn.Average(r => r.Item1),
			turn.Average(r => r.Item2), slow.Average(), stop.Average(r => r.Item1), stop.Average(r => r.Item2),
			stop.All(r => r.Item3));
	}

	private static (List<(double T, Vector3 P)> Ticks, List<(ulong Now, Vector3 Stated, int Tick)> Frames, List<Vector3> Drawn)
		Drive(IMethod method, Vector3 after, int seed)
	{
		var (ticks, frames) = Synthetic(after, seed);
		var drawn = new List<Vector3>();
		int observedTick = -1;
		Vector3 observed = new(float.NaN, 0f, 0f);
		foreach ((ulong now, Vector3 stated, int tick) in frames)
		{
			bool moved = stated != observed;
			if (tick != observedTick && (moved || tick - observedTick >= 4))
			{
				method.Observe(now, tick, stated);
				observedTick = tick;
				observed = stated;
			}

			// The ideal shared draw point: the true tick 100 ms before the frame.
			double at = ((now - 1_000_000UL) * 1e-6) - 0.1;
			int j = 0;
			while (j + 2 < ticks.Count && ticks[j + 1].T <= at)
			{
				++j;
			}

			double drawTick = j + Math.Clamp((at - ticks[j].T) / (ticks[j + 1].T - ticks[j].T), 0.0, 1.0);
			drawn.Add(method.Advance(now, drawTick));
		}

		return (ticks, frames, drawn);
	}
}
