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
		private readonly ulong _zone;

		public Scope(string name)
		{
			_name = name;
			_start = Stopwatch.GetTimestamp();
			_zone = ZoneBegin is { } begin ? begin(name) : 0UL;
		}

		public void Dispose()
		{
			if (_zone != 0UL)
			{
				ZoneEnd?.Invoke(_zone);
			}

			double ms = (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
			lock (Gate)
			{
				if (!Phases.TryGetValue(_name, out Phase? phase))
				{
					phase = new Phase { AverageMs = ms };
					Phases[_name] = phase;
				}

				phase.LastMs = ms;
				phase.AverageMs += (ms - phase.AverageMs) * 0.05;
				phase.PeakMs = System.Math.Max(phase.PeakMs * 0.98, ms);
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

	/// <summary>The first process callback of the frame (lowest priority node).</summary>
	public static void MarkFrameStart()
	{
		long now = Stopwatch.GetTimestamp();
		if (_frameStart != 0)
		{
			Record("frame_total", (now - _frameStart) * 1000.0 / Stopwatch.Frequency);
		}

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

	public override void _Process(double delta) => Engine_FramePhases.MarkFrameStart();
}

/// <summary>Last process callback of the frame (highest priority node).</summary>
public sealed partial class Engine_FrameEndNode : Godot.Node
{
	public override void _Ready() => ProcessPriority = int.MaxValue;
	public override void _Process(double delta) => Engine_FramePhases.MarkFrameEnd();
}
