using System.Collections.Generic;
using System.Diagnostics;

namespace Refii.Core;

/// <summary>
/// Where the main thread's frame goes, by named phase: a stopwatch around each per-frame
/// body, kept as an exponential average and a peak. Read back by the debug channel's
/// `metrics` reply. The render thread is measured by the viewport itself; this is the rest.
/// </summary>
public static class Engine_FramePhases
{
	private sealed class Phase
	{
		public double AverageMs;
		public double PeakMs;
		public double LastMs;

		/// <summary>The frame this phase last ran in, so a hitch snapshot only names live phases.</summary>
		public long LastFrame;

		/// <summary>Managed bytes this thread allocated inside the scope, averaged; laps and records have none.</summary>
		public double AverageAllocBytes;
	}

	private static readonly Dictionary<string, Phase> Phases = new();
	private static readonly object Gate = new();

	/// <summary>
	/// Opens a profiler zone for a phase, or 0 when no profiler is installed.
	/// </summary>
	/// <remarks>
	/// A HOOK RATHER THAN A CALL. This file is core and the profiler lives behind the game's
	/// host bridge, so calling it from here would point the dependency the wrong way. The game
	/// installs the pair once at startup and EVERY existing `Measure` site becomes a profiler
	/// zone — one annotation per phase instead of two to keep in step, which is the only way a
	/// codebase stays instrumented.
	///
	/// A ulong handle rather than an IDisposable: a phase opens on every frame of every screen,
	/// and boxing one per scope would make the instrument a cost of its own.
	/// </remarks>
	public static System.Func<string, ulong>? ZoneBegin;

	/// <summary>Closes what <see cref="ZoneBegin"/> opened.</summary>
	public static System.Action<ulong>? ZoneEnd;

	public readonly struct Scope : System.IDisposable
	{
		private readonly string _name;
		private readonly long _start;
		private readonly long _allocStart;
		private readonly ulong _zone;

		public Scope(string name)
		{
			_name = name;
			_start = Stopwatch.GetTimestamp();
			_allocStart = System.GC.GetAllocatedBytesForCurrentThread();
			_zone = ZoneBegin is { } begin ? begin(name) : 0UL;
		}

		public void Dispose()
		{
			if (_zone != 0UL)
			{
				ZoneEnd?.Invoke(_zone);
			}

			double ms = (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
			long alloc = System.GC.GetAllocatedBytesForCurrentThread() - _allocStart;
			lock (Gate)
			{
				if (!Phases.TryGetValue(_name, out Phase? phase))
				{
					phase = new Phase { AverageMs = ms, AverageAllocBytes = alloc };
					Phases[_name] = phase;
				}

				phase.LastMs = ms;
				phase.AverageMs += (ms - phase.AverageMs) * 0.05;
				phase.PeakMs = System.Math.Max(phase.PeakMs * 0.98, ms);
				phase.AverageAllocBytes += (alloc - phase.AverageAllocBytes) * 0.05;
			}
		}
	}

	public static Scope Measure(string name) => new(name);

	/// <summary>Records the time since <paramref name="since"/> under a name and restarts it.</summary>
	/// <remarks>
	/// PLOTTED, NOT ZONED. A lap is a duration recorded after the fact, and a profiler zone has
	/// to be opened and closed around the work as it happens — there is no retroactive zone. A
	/// plot is the honest shape for it: the profiler draws the millisecond value per frame
	/// beside the zones, which is what a lap actually is.
	/// </remarks>
	public static void Lap(string name, ref long since)
	{
		long now = Stopwatch.GetTimestamp();
		double ms = (now - since) * 1000.0 / Stopwatch.Frequency;
		Record(name, ms);
		Plot?.Invoke(name, ms);
		since = now;
	}

	/// <summary>Draws a named value on the profiler's timeline; installed with the zone hooks.</summary>
	public static System.Action<string, double>? Plot;

	private static long _frameStart;

	private static long _frameIndex;

	/// <summary>Each phase completion this frame, in order, so a hitch can be localised.</summary>
	/// <remarks>
	/// A frame also contains what Godot does between our nodes (scene-tree walk, signal
	/// dispatch, Skeleton3D processing), which no `Measure` covers, and phases nest so they
	/// cannot be summed. Recording when each phase finished lets the largest gap between two
	/// consecutive finishes bracket the unaccounted time between two named points.
	/// </remarks>
	private static readonly (string Name, long At)[] _marks = new (string, long)[96];

	private static int _markCount;

	/// <summary>One caught hitch: what the frame cost, and which phases were in it.</summary>
	public sealed class Hitch
	{
		public double TotalMs;
		public string Phases = string.Empty;

		/// <summary>The widest stretch no phase accounts for, and the two phases around it.</summary>
		public string Gap = string.Empty;
	}

	/// <summary>
	/// The worst frames seen, with the phases that were running in them.
	/// </summary>
	/// <remarks>
	/// Per-phase peaks decay independently and may come from different frames, so a frame over
	/// the threshold is snapshotted whole at the next frame's start, where its cost is known and
	/// the phases still hold its values. Only phases that ran in that frame are named — `LastMs`
	/// on one that did not is stale. Kept sorted worst-first and capped.
	/// </remarks>
	public static readonly List<Hitch> Hitches = new();

	/// <summary>A frame over this many milliseconds is worth explaining.</summary>
	public static double HitchThresholdMs = 25.0;

	private const int MaxHitches = 12;

	private static void CatchHitch(double totalMs)
	{
		if (totalMs < HitchThresholdMs)
		{
			return;
		}

		// Cheapest first: if it cannot displace the weakest entry there is nothing to build.
		if (Hitches.Count >= MaxHitches && totalMs <= Hitches[Hitches.Count - 1].TotalMs)
		{
			return;
		}

		var parts = new List<(string Name, double Ms)>();
		foreach ((string name, Phase phase) in Phases)
		{
			if (phase.LastFrame == _frameIndex && name != "frame_total" && phase.LastMs >= 1.0)
			{
				parts.Add((name, phase.LastMs));
			}
		}

		parts.Sort((a, b) => b.Ms.CompareTo(a.Ms));
		var text = new System.Text.StringBuilder();
		for (int i = 0; i < parts.Count && i < 5; i++)
		{
			text.Append(i == 0 ? string.Empty : " ").Append(parts[i].Name)
				.Append(' ').Append(parts[i].Ms.ToString("0.0"));
		}

		// The widest stretch of the frame that no phase accounts for, and what bracketed it.
		string gap = string.Empty;
		double worstGapMs = 0.0;
		long previousAt = _frameStart;
		string previousName = "frame start";
		for (int i = 0; i < _markCount; i++)
		{
			double sinceMs = (_marks[i].At - previousAt) * 1000.0 / Stopwatch.Frequency;
			if (sinceMs > worstGapMs)
			{
				worstGapMs = sinceMs;
				gap = $"{sinceMs:0.0} ms between '{previousName}' and '{_marks[i].Name}'";
			}

			previousAt = _marks[i].At;
			previousName = _marks[i].Name;
		}

		Hitches.Add(new Hitch { TotalMs = totalMs, Phases = text.ToString(), Gap = gap });
		Hitches.Sort((a, b) => b.TotalMs.CompareTo(a.TotalMs));
		if (Hitches.Count > MaxHitches)
		{
			Hitches.RemoveAt(Hitches.Count - 1);
		}
	}

	/// <summary>The first process callback of the frame (lowest priority node).</summary>
	public static void MarkFrameStart()
	{
		long now = Stopwatch.GetTimestamp();
		if (_frameStart != 0)
		{
			double totalMs = (now - _frameStart) * 1000.0 / Stopwatch.Frequency;
			Record("frame_total", totalMs);

			// Before the index moves on: the phases still hold the finished frame's values.
			lock (Gate)
			{
				CatchHitch(totalMs);
			}
		}

		_markCount = 0;
		++_frameIndex;

		// Everything between the renderer finishing and the next frame's first callback:
		// swap/present wait, OS events, physics, deferred calls.
		if (_drawEnd != 0)
		{
			Record("after_draw", (now - _drawEnd) * 1000.0 / Stopwatch.Frequency);
		}

		_frameStart = now;
	}

	/// <summary>The last process callback of the frame (highest priority node).</summary>
	private static long _processEnd;
	private static long _drawEnd;

	public static void MarkFrameEnd()
	{
		_processEnd = Stopwatch.GetTimestamp();
		if (_frameStart != 0)
		{
			Record("process_total", (_processEnd - _frameStart) * 1000.0 / Stopwatch.Frequency);
		}
	}

	/// <summary>RenderingServer.FramePostDraw: the renderer has submitted the frame.</summary>
	public static void MarkDrawEnd()
	{
		_drawEnd = Stopwatch.GetTimestamp();
		if (_processEnd != 0)
		{
			Record("draw", (_drawEnd - _processEnd) * 1000.0 / Stopwatch.Frequency);
		}
	}

	/// <summary>
	/// Records a duration a caller measured itself, and plots it.
	/// </summary>
	/// <remarks>
	/// For a cost ACCUMULATED across a loop, where neither a scope nor a lap fits: a scope would
	/// open once per iteration and a lap would record one value per iteration, and the question
	/// is what the whole walk spent.
	/// </remarks>
	public static void Record(string name, double ms, bool plot)
	{
		Record(name, ms);
		if (plot)
		{
			Plot?.Invoke(name, ms);
		}
	}

	private static void Record(string name, double ms)
	{
		lock (Gate)
		{
			if (!Phases.TryGetValue(name, out Phase? phase))
			{
				phase = new Phase { AverageMs = ms };
				Phases[name] = phase;
			}

			phase.LastMs = ms;
			phase.LastFrame = _frameIndex;
			if (_markCount < _marks.Length)
			{
				_marks[_markCount++] = (name, Stopwatch.GetTimestamp());
			}
			phase.AverageMs += (ms - phase.AverageMs) * 0.05;
			phase.PeakMs = System.Math.Max(phase.PeakMs * 0.98, ms);
		}
	}


	/// <summary>Every phase as {name: {avgMs, peakMs, lastMs}}, for the metrics reply.</summary>
	public static Dictionary<string, object?> Report()
	{
		var report = new Dictionary<string, object?>();
		lock (Gate)
		{
			foreach ((string name, Phase phase) in Phases)
			{
				report[name] = new Dictionary<string, object?>
				{
					["avgMs"] = System.Math.Round(phase.AverageMs, 2),
					["peakMs"] = System.Math.Round(phase.PeakMs, 2),
					["lastMs"] = System.Math.Round(phase.LastMs, 2),
					["allocKB"] = System.Math.Round(phase.AverageAllocBytes / 1024.0, 1),
				};
			}
		}

		return report;
	}
}

/// <summary>First process callback of the frame (lowest priority node).</summary>
public sealed partial class Engine_FrameStartNode : Godot.Node
{
	public override void _Ready()
	{
		ProcessPriority = int.MinValue;
		Godot.RenderingServer.Singleton.FramePostDraw += Engine_FramePhases.MarkDrawEnd;
	}

	public override void _Process(double delta)
	{
		Engine_FramePhases.MarkFrameStart();
		double now = Godot.Time.GetTicksUsec();

		// The guest's own clock first: it states its simulation time every iteration at
		// `game+0x110` (0x82276F18). It cannot be fitted from sample arrivals because the guest
		// does not catch up — more than one step behind, it takes one step and re-bases.
		if (Engine_TickClock.UseStatedClock)
		{
			Engine_TickClock.ObserveStatedClock(
				Refii.Host.HostBridge.SimClock() is { } clock ? clock.Seconds : double.NaN, now);
		}

		Engine_TickClock.BeginFrame(now);

		// One instant for every draw point in this frame — camera, entity transforms, pose
		// blends, clip times (Engine_DrawnFrame.NowUsec). The stated clock is read every frame,
		// not only under UseStatedClock: the width of each pair comes from the guest (see
		// Engine_DrawnFrame.GuestSeconds).
		Refii.Entities.Engine_DrawnFrame.BeginFrame((ulong)now,
			Refii.Host.HostBridge.SimClock() is { } stated ? stated.Seconds : double.NaN);
	}
}

/// <summary>Last process callback of the frame (highest priority node).</summary>
public sealed partial class Engine_FrameEndNode : Godot.Node
{
	public override void _Ready() => ProcessPriority = int.MaxValue;
	public override void _Process(double delta) => Engine_FramePhases.MarkFrameEnd();
}
