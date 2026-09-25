using System.Collections.Generic;
using System.Diagnostics;
using Godot;

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

	/// <summary>
	/// What the renderer and the GC did during one frame: sampled at every frame start, so the
	/// difference between two samples is exactly the hitch frame's share.
	/// </summary>
	/// <remarks>
	/// The worst-frames list above says WHICH phase a hitch sat in but not WHY. A 1.5 s wait in
	/// `draw` is the main thread waiting for the render thread, and the render thread's work is
	/// invisible to every `Measure`: pipeline compilations, texture and buffer uploads, and
	/// the GC's stop-the-world pauses are the candidates, and each has a counter.
	/// </remarks>
	private struct FrameCounters
	{
		public double PipelinesCanvas, PipelinesMesh, PipelinesSurface, PipelinesDraw, PipelinesSpecialization;
		public double VideoMemMB, TextureMemMB, BufferMemMB;
		public double ObjectsDrawn, Primitives;
		public int Gen0, Gen1, Gen2;
		public double GcPauseMs;
		public double Resources, Nodes;

		public static FrameCounters Sample()
		{
			return new FrameCounters
			{
				PipelinesCanvas = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsCanvas),
				PipelinesMesh = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsMesh),
				PipelinesSurface = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface),
				PipelinesDraw = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw),
				PipelinesSpecialization = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSpecialization),
				VideoMemMB = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0,
				TextureMemMB = Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / 1048576.0,
				BufferMemMB = Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed) / 1048576.0,
				ObjectsDrawn = Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame),
				Primitives = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame),
				Gen0 = System.GC.CollectionCount(0),
				Gen1 = System.GC.CollectionCount(1),
				Gen2 = System.GC.CollectionCount(2),
				GcPauseMs = System.GC.GetTotalPauseDuration().TotalMilliseconds,
				Resources = Performance.GetMonitor(Performance.Monitor.ObjectResourceCount),
				Nodes = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
			};
		}
	}

	private static FrameCounters _previousCounters;
	private static bool _haveCounters;

	/// <summary>A frame over this is logged with its counters, not only kept in the list.</summary>
	public static double HitchLogThresholdMs = 200.0;

	/// <summary>
	/// Logs a frame over <see cref="HitchLogThresholdMs"/> with what moved during it. Called
	/// once per frame start with the finished frame's cost; the sample it takes is kept as the
	/// baseline for the next frame either way.
	/// </summary>
	private static void LogHitch(double totalMs)
	{
		FrameCounters now = FrameCounters.Sample();
		if (_haveCounters && totalMs >= HitchLogThresholdMs)
		{
			FrameCounters was = _previousCounters;
			string phases = string.Empty;
			string gap = string.Empty;
			foreach (Hitch h in Hitches)
			{
				if (System.Math.Abs(h.TotalMs - totalMs) < 0.001)
				{
					phases = h.Phases;
					gap = h.Gap;
					break;
				}
			}

			GD.Print($"[hitch] {System.DateTime.Now:HH:mm:ss.fff} frame {totalMs:0} ms" +
				$" | {phases}" +
				$" | gap: {gap}" +
				$" | pipelines +{now.PipelinesSurface - was.PipelinesSurface:0} surface" +
				$" +{now.PipelinesSpecialization - was.PipelinesSpecialization:0} spec" +
				$" +{now.PipelinesDraw - was.PipelinesDraw:0} draw" +
				$" +{now.PipelinesMesh - was.PipelinesMesh:0} mesh" +
				$" +{now.PipelinesCanvas - was.PipelinesCanvas:0} canvas" +
				$" | gc gen0 +{now.Gen0 - was.Gen0} gen1 +{now.Gen1 - was.Gen1} gen2 +{now.Gen2 - was.Gen2}" +
				$" pause +{now.GcPauseMs - was.GcPauseMs:0.0} ms" +
				$" | vram {now.VideoMemMB:0} MB ({now.VideoMemMB - was.VideoMemMB:+0;-0} MB)" +
				$" tex {now.TextureMemMB - was.TextureMemMB:+0;-0} MB buf {now.BufferMemMB - was.BufferMemMB:+0;-0} MB" +
				$" | objects {now.ObjectsDrawn:0} ({now.ObjectsDrawn - was.ObjectsDrawn:+0;-0})" +
				$" prims {now.Primitives / 1e6:0.0}M" +
				$" | resources {now.Resources:0} ({now.Resources - was.Resources:+0;-0}) nodes {now.Nodes:0} ({now.Nodes - was.Nodes:+0;-0})" +
				$" | updates: {Engine_InstanceUpdates.LastFrameSummary()}");
		}

		_previousCounters = now;
		_haveCounters = true;
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
				// The instance-update window closes with the frame, so the hitch line below can
				// name what this frame wrote into the scene.
				Engine_InstanceUpdates.EndFrame();
				CatchHitch(totalMs);
				LogHitch(totalMs);
			}
		}
		else
		{
			lock (Gate)
			{
				Engine_InstanceUpdates.EndFrame();
				LogHitch(0.0);
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


	/// <summary>One phase's rolling average and decaying peak, or (0, 0) before it has run.</summary>
	public static (double AverageMs, double PeakMs) Get(string name)
	{
		lock (Gate)
		{
			return Phases.TryGetValue(name, out Phase? phase)
				? (phase.AverageMs, phase.PeakMs)
				: (0.0, 0.0);
		}
	}

	/// <summary>One phase's most recent duration, or 0 before it has run.</summary>
	public static double Last(string name)
	{
		lock (Gate)
		{
			return Phases.TryGetValue(name, out Phase? phase) ? phase.LastMs : 0.0;
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

		// The finished frame's GPU pass table, when the engine captures one (`--gpu-profile`).
		Engine_GpuPasses.Sample();

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
