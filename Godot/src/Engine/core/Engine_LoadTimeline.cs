using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Refii.Core;

/// <summary>
/// Where a load's wall time goes, from leaving the front end to the world settling: the frames,
/// the main thread's share of each and the engine's, the time and count of each kind of streamed
/// item, and the server syncs (Engine_ServerSyncs) made meanwhile. Read by the debug channel's
/// `metrics` reply as `load`.
/// </summary>
/// <remarks>
/// ONE WINDOW AT A TIME. <see cref="Open"/> starts a window when none is open or the last one has
/// been released, so the front end's exit and the region build (which follows it, or comes alone
/// on a level change) land in the same window. It stays open after the hold releases — the props
/// and entities that page in during play are part of the load the player sees — until the next
/// one opens, and the release keeps its own copy of every total.
///
/// Totals, not averages: the question is what the whole load spent, which a frame phase's
/// rolling average (Engine_FramePhases) cannot answer. A frame is split at our first and last
/// process callbacks: "process" is ours, "engine" is everything between our last callback and the
/// next frame's first — physics, `RenderingServer::sync` (under the separate render thread, the
/// wait for that thread), the draw itself under Safe.
/// </remarks>
public static class Engine_LoadTimeline
{
	/// <summary>The kinds of item a load streams, fixed so the totals are plain arrays.</summary>
	public enum Category
	{
		/// <summary>The region build on its worker (Engine_GameAssets.BuildLevelNode), wall time.</summary>
		RegionBuild,
		/// <summary>The static-twin index at mount (Engine_StaticTwins.Build).</summary>
		StaticTwins,
		/// <summary>One prop given its mesh (Engine_PropStreamer.Step).</summary>
		PropStep,
		/// <summary>A prop decoded on the main thread after the pool missed it.</summary>
		PropSyncLoad,
		/// <summary>One finished vertex bake applied (Engine_PropStreamer.DrainVertexBakes).</summary>
		PropBake,
		/// <summary>One live entity's node created (GameEntities).</summary>
		EntityCreate,
	}

	private static readonly int CategoryCount = System.Enum.GetValues<Category>().Length;

	private static readonly long[] CategoryTicks = new long[CategoryCount];
	private static readonly long[] CategoryItems = new long[CategoryCount];
	private static readonly long[] CategoryPeak = new long[CategoryCount];

	private static readonly object Gate = new();
	private static bool _open;
	private static long _openedAt;
	private static long _releasedAt;
	private static string _what = string.Empty;
	private static readonly List<(string Name, long At)> Marks = new();
	private static Engine_ServerSyncs.Snapshot _syncsAtOpen;
	private static Engine_ServerSyncs.Snapshot _syncsAtRelease;
	private static Dictionary<string, object?>? _atRelease;

	private static long _frames;
	private static long _processTicks;
	private static long _engineTicks;
	private static long _longestFrameTicks;
	private static long _lastFrameStart;
	private static long _lastFrameEnd;
	private static long _allocatedAtOpen;
	private static int _gen0AtOpen, _gen1AtOpen, _gen2AtOpen;
	private static double _pauseAtOpen;

	/// <summary>Whether a window is open (its totals are being kept).</summary>
	public static bool IsOpen => Volatile.Read(ref _open);

	/// <summary>
	/// Starts a window unless one is open and not yet released; names the moment either way.
	/// Main thread.
	/// </summary>
	public static void Open(string what)
	{
		long now = Stopwatch.GetTimestamp();
		lock (Gate)
		{
			if (!_open || _releasedAt != 0)
			{
				_open = true;
				_openedAt = now;
				_releasedAt = 0;
				_what = what;
				Marks.Clear();
				_atRelease = null;
				_syncsAtOpen = Engine_ServerSyncs.Take();
				_syncsAtRelease = default;
				for (int i = 0; i < CategoryCount; i++)
				{
					Interlocked.Exchange(ref CategoryTicks[i], 0);
					Interlocked.Exchange(ref CategoryItems[i], 0);
					Interlocked.Exchange(ref CategoryPeak[i], 0);
				}
				_frames = 0;
				_processTicks = 0;
				_engineTicks = 0;
				_longestFrameTicks = 0;
				_lastFrameStart = 0;
				_lastFrameEnd = 0;
				_allocatedAtOpen = System.GC.GetTotalAllocatedBytes();
				_gen0AtOpen = System.GC.CollectionCount(0);
				_gen1AtOpen = System.GC.CollectionCount(1);
				_gen2AtOpen = System.GC.CollectionCount(2);
				_pauseAtOpen = System.GC.GetTotalPauseDuration().TotalMilliseconds;
			}

			MarkLocked(what, now);
		}
	}

	/// <summary>Names a moment inside the open window (the first time only). Main thread.</summary>
	public static void Mark(string name)
	{
		long now = Stopwatch.GetTimestamp();
		lock (Gate)
		{
			if (_open)
			{
				MarkLocked(name, now);
			}
		}
	}

	private static void MarkLocked(string name, long at)
	{
		foreach ((string seen, long _) in Marks)
		{
			if (seen == name)
			{
				return;
			}
		}

		Marks.Add((name, at));
	}

	/// <summary>The hold released the guest: the window's totals so far are kept beside the running ones.</summary>
	public static void Released()
	{
		long now = Stopwatch.GetTimestamp();
		Dictionary<string, object?> snapshot;
		var stages = new System.Text.StringBuilder();
		lock (Gate)
		{
			if (!_open || _releasedAt != 0)
			{
				return;
			}

			MarkLocked("released", now);
			_syncsAtRelease = Engine_ServerSyncs.Take();
			snapshot = Totals(now, _syncsAtRelease);
			_releasedAt = now;
			_atRelease = snapshot;
			foreach ((string name, long at) in Marks)
			{
				stages.Append($" {name} {(at - _openedAt) / (double)Stopwatch.Frequency:0.00}");
			}
			for (int i = 0; i < CategoryCount; i++)
			{
				long items = Interlocked.Read(ref CategoryItems[i]);
				if (items != 0)
				{
					stages.Append($"; {(Category)i} {items}x {Interlocked.Read(ref CategoryTicks[i]) * 1000.0 / Stopwatch.Frequency:0} ms");
				}
			}
		}

		Godot.GD.Print($"[load] {_what} to release in {(now - _openedAt) / (double)Stopwatch.Frequency:0.00} s over " +
			$"{snapshot["frames"]} frame(s), process {snapshot["processMs"]} ms, engine {snapshot["engineMs"]} ms; " +
			Engine_ServerSyncs.Describe(_syncsAtOpen, _syncsAtRelease) +
			$"; allocated {(System.GC.GetTotalAllocatedBytes() - _allocatedAtOpen) >> 20} MB, gc gen0 +{System.GC.CollectionCount(0) - _gen0AtOpen} " +
			$"gen1 +{System.GC.CollectionCount(1) - _gen1AtOpen} gen2 +{System.GC.CollectionCount(2) - _gen2AtOpen}, " +
			$"pause {System.GC.GetTotalPauseDuration().TotalMilliseconds - _pauseAtOpen:0} ms; at{stages}");
	}

	/// <summary>One item of a kind, the time it took and how many it covered. Any thread.</summary>
	public static void Add(Category category, long ticks, int items = 1)
	{
		if (!Volatile.Read(ref _open))
		{
			return;
		}

		int i = (int)category;
		Interlocked.Add(ref CategoryTicks[i], ticks);
		Interlocked.Add(ref CategoryItems[i], items);
		long peak = Volatile.Read(ref CategoryPeak[i]);
		while (ticks > peak)
		{
			long seen = Interlocked.CompareExchange(ref CategoryPeak[i], ticks, peak);
			if (seen == peak)
			{
				break;
			}
			peak = seen;
		}
	}

	/// <summary>The frame's first process callback (Engine_FramePhases.MarkFrameStart).</summary>
	public static void FrameStart(long stamp)
	{
		if (!Volatile.Read(ref _open))
		{
			return;
		}

		if (_lastFrameEnd != 0)
		{
			_engineTicks += stamp - _lastFrameEnd;
		}
		if (_lastFrameStart != 0)
		{
			_longestFrameTicks = System.Math.Max(_longestFrameTicks, stamp - _lastFrameStart);
		}

		_lastFrameStart = stamp;
		++_frames;
	}

	/// <summary>The frame's last process callback (Engine_FramePhases.MarkFrameEnd).</summary>
	public static void FrameEnd(long stamp)
	{
		if (!Volatile.Read(ref _open) || _lastFrameStart == 0)
		{
			return;
		}

		_processTicks += stamp - _lastFrameStart;
		_lastFrameEnd = stamp;
	}

	/// <summary>The open window (null when none has opened): its marks, running totals and the release's copy.</summary>
	public static Dictionary<string, object?>? Report()
	{
		long now = Stopwatch.GetTimestamp();
		lock (Gate)
		{
			if (!_open)
			{
				return null;
			}

			Dictionary<string, object?> report = Totals(now, Engine_ServerSyncs.Take());
			report["what"] = _what;
			var marks = new Dictionary<string, object?>();
			foreach ((string name, long at) in Marks)
			{
				marks[name] = System.Math.Round((at - _openedAt) / (double)Stopwatch.Frequency, 2);
			}
			report["marksS"] = marks;
			report["atRelease"] = _atRelease;
			return report;
		}
	}

	private static Dictionary<string, object?> Totals(long now, Engine_ServerSyncs.Snapshot syncs)
	{
		double toMs = 1000.0 / Stopwatch.Frequency;
		var categories = new Dictionary<string, object?>();
		for (int i = 0; i < CategoryCount; i++)
		{
			long items = Interlocked.Read(ref CategoryItems[i]);
			if (items == 0)
			{
				continue;
			}

			long ticks = Interlocked.Read(ref CategoryTicks[i]);
			categories[((Category)i).ToString()] = new Dictionary<string, object?>
			{
				["items"] = items,
				["ms"] = System.Math.Round(ticks * toMs, 1),
				["meanMs"] = System.Math.Round(ticks * toMs / items, 3),
				["peakMs"] = System.Math.Round(Interlocked.Read(ref CategoryPeak[i]) * toMs, 2),
			};
		}

		return new Dictionary<string, object?>
		{
			["wallS"] = System.Math.Round((now - _openedAt) / (double)Stopwatch.Frequency, 2),
			["frames"] = _frames,
			["processMs"] = System.Math.Round(_processTicks * toMs, 0),
			["engineMs"] = System.Math.Round(_engineTicks * toMs, 0),
			["longestFrameMs"] = System.Math.Round(_longestFrameTicks * toMs, 1),
			["items"] = categories,
			["serverSyncs"] = Engine_ServerSyncs.Report(_syncsAtOpen, syncs),
		};
	}
}
