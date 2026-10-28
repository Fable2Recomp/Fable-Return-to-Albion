using System.Collections.Generic;
using System.Threading;

namespace Refii.Core;

/// <summary>
/// What each frame allocated and which frames held a collection: one row per frame in a ring,
/// and one record per collection, read back by the debug channel (`gcframes`, `metrics.gc`).
/// </summary>
/// <remarks>
/// <para>WHY PER FRAME. The phase table's `allocKB` is a decaying average per scope, and the GC
/// counters are run totals: neither says how many bytes a frame cost or whether the slow frame
/// of a walk was the one a collection stopped. A row per frame answers both, and a caller reads
/// the rows of its own window with one ask at the end instead of polling (a poll allocates on
/// the main thread and would be counted in what it measures).</para>
/// <para>CHEAP. A frame costs two allocation-counter reads, three collection counts and a pause
/// total, all plain reads with no allocation. A collection costs one <see cref="System.GC.GetGCMemoryInfo()"/>
/// (a small object) and only on the frame a count moved.</para>
/// <para>`main` is the main thread's own bytes between two frame starts; `total` every thread's.
/// The difference is the workers (entity walk, texture decode, cloth, pose batches, the guest's
/// asset reads), which feed the same gen0 budget.</para>
/// </remarks>
public static class Engine_GcFrames
{
	/// <summary>One finished frame.</summary>
	public struct Row
	{
		public long Frame;
		public float FrameMs;
		public long MainBytes;
		public long TotalBytes;

		/// <summary>Collections that finished inside the frame, by generation (gen0 counts every one).</summary>
		public byte Gen0, Gen1, Gen2;

		/// <summary>Stop-the-world pause the GC reported inside the frame.</summary>
		public float PauseMs;
	}

	/// <summary>One collection, as the GC reports it, with the frame it fell in.</summary>
	public struct Collection
	{
		public long Index;
		public long Frame;
		public int Generation;
		public bool Compacted;
		public bool Concurrent;
		public double PauseMs;
		public double PromotedKB;
		public double Gen0BeforeKB;
		public double HeapMB;
		public double FragmentedMB;

		/// <summary>The memory load the GC saw at this collection, in percent of physical memory.</summary>
		public double MemoryLoadPercent;

		/// <summary>Every thread's bytes since the previous recorded collection: the budget it used.</summary>
		public double AllocatedSincePreviousKB;
	}

	/// <summary>About 70 s of frames at the phone's 30 FPS cap.</summary>
	public const int Capacity = 2048;

	private const int CollectionCapacity = 128;

	private static readonly Row[] Rows = new Row[Capacity];
	private static readonly Collection[] Collections = new Collection[CollectionCapacity];
	private static readonly object Gate = new();

	private static long _rowCount;
	private static long _collectionCount;

	private static bool _started;
	private static long _mainAtStart;
	private static long _totalAtStart;
	private static int _gen0AtStart, _gen1AtStart, _gen2AtStart;
	private static double _pauseAtStart;
	private static long _lastGcIndex;
	private static long _totalAtLastGc;

	/// <summary>Bytes the named worker loops allocated, summed by the workers themselves.</summary>
	/// <remarks>Each adds its own thread's allocation-counter difference around one unit of work;
	/// <see cref="Note"/> is a single interlocked add.</remarks>
	public static long EntityWalkBytes, TextureWorkerBytes, GuestAssetReadBytes, PoseBatchBytes, ClothBytes;

	/// <summary>Adds one worker's bytes to its counter.</summary>
	public static void Note(ref long counter, long bytes)
	{
		if (bytes > 0)
		{
			Interlocked.Add(ref counter, bytes);
		}
	}

	/// <summary>
	/// Closes the frame that just ended and opens the next. Main thread, once per frame start;
	/// <paramref name="frameMs"/> is the finished frame's length, or 0 for the first.
	/// </summary>
	public static void FrameStart(long frame, double frameMs)
	{
		long main = System.GC.GetAllocatedBytesForCurrentThread();
		long total = System.GC.GetTotalAllocatedBytes(false);
		int gen0 = System.GC.CollectionCount(0);
		int gen1 = System.GC.CollectionCount(1);
		int gen2 = System.GC.CollectionCount(2);
		double pause = System.GC.GetTotalPauseDuration().TotalMilliseconds;

		if (_started)
		{
			var row = new Row
			{
				Frame = frame - 1,
				FrameMs = (float)frameMs,
				MainBytes = main - _mainAtStart,
				TotalBytes = total - _totalAtStart,
				Gen0 = (byte)System.Math.Min(255, gen0 - _gen0AtStart),
				Gen1 = (byte)System.Math.Min(255, gen1 - _gen1AtStart),
				Gen2 = (byte)System.Math.Min(255, gen2 - _gen2AtStart),
				PauseMs = (float)(pause - _pauseAtStart),
			};

			lock (Gate)
			{
				Rows[_rowCount % Capacity] = row;
				++_rowCount;
			}

			if (gen0 != _gen0AtStart)
			{
				RecordCollection(frame - 1, total);
			}
		}
		else
		{
			_totalAtLastGc = total;
			_lastGcIndex = System.GC.GetGCMemoryInfo().Index;
		}

		_started = true;
		_mainAtStart = main;
		_totalAtStart = total;
		_gen0AtStart = gen0;
		_gen1AtStart = gen1;
		_gen2AtStart = gen2;
		_pauseAtStart = pause;
	}

	/// <summary>The latest collection, once per frame that held any. Several in one frame keep only the last.</summary>
	private static void RecordCollection(long frame, long totalNow)
	{
		System.GCMemoryInfo info = System.GC.GetGCMemoryInfo();
		if (info.Index == _lastGcIndex)
		{
			return;
		}

		System.ReadOnlySpan<System.TimeSpan> pauses = info.PauseDurations;
		double pauseMs = 0.0;
		for (int i = 0; i < pauses.Length; ++i)
		{
			pauseMs += pauses[i].TotalMilliseconds;
		}

		long collectionsSince = System.Math.Max(1L, info.Index - _lastGcIndex);
		var record = new Collection
		{
			Index = info.Index,
			Frame = frame,
			Generation = info.Generation,
			Compacted = info.Compacted,
			Concurrent = info.Concurrent,
			PauseMs = pauseMs,
			PromotedKB = info.PromotedBytes / 1024.0,
			Gen0BeforeKB = info.GenerationInfo.Length > 0 ? info.GenerationInfo[0].SizeBeforeBytes / 1024.0 : 0.0,
			HeapMB = info.HeapSizeBytes / 1048576.0,
			FragmentedMB = info.FragmentedBytes / 1048576.0,
			MemoryLoadPercent = info.TotalAvailableMemoryBytes > 0
				? 100.0 * info.MemoryLoadBytes / info.TotalAvailableMemoryBytes : 0.0,
			AllocatedSincePreviousKB = (totalNow - _totalAtLastGc) / 1024.0 / collectionsSince,
		};

		_lastGcIndex = info.Index;
		_totalAtLastGc = totalNow;
		lock (Gate)
		{
			Collections[_collectionCount % CollectionCapacity] = record;
			++_collectionCount;
		}
	}

	/// <summary>The newest frame with a row, or -1 before the first.</summary>
	public static long LatestFrame
	{
		get
		{
			lock (Gate)
			{
				return _rowCount == 0 ? -1 : Rows[(_rowCount - 1) % Capacity].Frame;
			}
		}
	}

	/// <summary>The rows after <paramref name="sinceFrame"/> still in the ring, oldest first.</summary>
	public static List<Row> RowsSince(long sinceFrame)
	{
		var rows = new List<Row>();
		lock (Gate)
		{
			long first = System.Math.Max(0L, _rowCount - Capacity);
			for (long i = first; i < _rowCount; ++i)
			{
				Row row = Rows[i % Capacity];
				if (row.Frame > sinceFrame)
				{
					rows.Add(row);
				}
			}
		}

		return rows;
	}

	/// <summary>The collections recorded after <paramref name="sinceFrame"/> still in the ring, oldest first.</summary>
	public static List<Collection> CollectionsSince(long sinceFrame)
	{
		var list = new List<Collection>();
		lock (Gate)
		{
			long first = System.Math.Max(0L, _collectionCount - CollectionCapacity);
			for (long i = first; i < _collectionCount; ++i)
			{
				Collection c = Collections[i % CollectionCapacity];
				if (c.Frame > sinceFrame)
				{
					list.Add(c);
				}
			}
		}

		return list;
	}

	/// <summary>
	/// A window's rows folded into what a walk report needs: bytes per frame (main and every
	/// thread), how many frames held a collection, and those frames' length against the rest.
	/// </summary>
	public static Dictionary<string, object?> Summarise(List<Row> rows)
	{
		int n = rows.Count;
		var main = new double[n];
		var total = new double[n];
		double frameGc = 0.0, frameClean = 0.0;
		int withGc = 0, withGen1 = 0, withGen2 = 0, gen0 = 0;
		double pause = 0.0, worstPause = 0.0;
		for (int i = 0; i < n; ++i)
		{
			Row r = rows[i];
			main[i] = r.MainBytes / 1024.0;
			total[i] = r.TotalBytes / 1024.0;
			gen0 += r.Gen0;
			pause += r.PauseMs;
			worstPause = System.Math.Max(worstPause, r.PauseMs);
			if (r.Gen0 > 0)
			{
				++withGc;
				frameGc += r.FrameMs;
				withGen1 += r.Gen1 > 0 ? 1 : 0;
				withGen2 += r.Gen2 > 0 ? 1 : 0;
			}
			else
			{
				frameClean += r.FrameMs;
			}
		}

		double seconds = 0.0;
		for (int i = 0; i < n; ++i)
		{
			seconds += rows[i].FrameMs / 1000.0;
		}

		System.Array.Sort(main);
		System.Array.Sort(total);
		double Pct(double[] v, double q) => v.Length == 0 ? 0.0 : v[System.Math.Min(v.Length - 1, (int)(q * v.Length))];
		double Mean(double[] v)
		{
			double s = 0.0;
			foreach (double x in v)
			{
				s += x;
			}

			return v.Length == 0 ? 0.0 : s / v.Length;
		}

		return new Dictionary<string, object?>
		{
			["frames"] = n,
			["seconds"] = System.Math.Round(seconds, 2),
			["mainKBPerFrame"] = System.Math.Round(Mean(main), 1),
			["mainKBp50"] = System.Math.Round(Pct(main, 0.5), 1),
			["mainKBp95"] = System.Math.Round(Pct(main, 0.95), 1),
			["mainKBmax"] = System.Math.Round(n == 0 ? 0.0 : main[n - 1], 1),
			["totalKBPerFrame"] = System.Math.Round(Mean(total), 1),
			["totalKBp95"] = System.Math.Round(Pct(total, 0.95), 1),
			["totalMBPerSec"] = seconds > 0.0 ? System.Math.Round(Mean(total) * n / 1024.0 / seconds, 2) : 0.0,
			["gcs"] = gen0,
			["framesWithGc"] = withGc,
			["framesWithGen1"] = withGen1,
			["framesWithGen2"] = withGen2,
			["pauseMs"] = System.Math.Round(pause, 1),
			["worstFramePauseMs"] = System.Math.Round(worstPause, 1),
			["frameMsWithGc"] = withGc == 0 ? 0.0 : System.Math.Round(frameGc / withGc, 1),
			["frameMsWithout"] = n - withGc == 0 ? 0.0 : System.Math.Round(frameClean / (n - withGc), 1),
		};
	}

	/// <summary>One collection as a reply row.</summary>
	public static Dictionary<string, object?> Describe(Collection c) => new()
	{
		["index"] = c.Index,
		["frame"] = c.Frame,
		["gen"] = c.Generation,
		["compacted"] = c.Compacted,
		["concurrent"] = c.Concurrent,
		["pauseMs"] = System.Math.Round(c.PauseMs, 2),
		["promotedKB"] = System.Math.Round(c.PromotedKB, 1),
		["gen0BeforeKB"] = System.Math.Round(c.Gen0BeforeKB, 1),
		["allocatedSincePreviousKB"] = System.Math.Round(c.AllocatedSincePreviousKB, 1),
		["heapMB"] = System.Math.Round(c.HeapMB, 1),
		["fragmentedMB"] = System.Math.Round(c.FragmentedMB, 1),
		["memoryLoadPercent"] = System.Math.Round(c.MemoryLoadPercent, 1),
	};

	/// <summary>The worker counters, in KB since launch.</summary>
	public static Dictionary<string, object?> Workers() => new()
	{
		["entityWalkKB"] = Interlocked.Read(ref EntityWalkBytes) / 1024,
		["textureWorkersKB"] = Interlocked.Read(ref TextureWorkerBytes) / 1024,
		["guestAssetReadsKB"] = Interlocked.Read(ref GuestAssetReadBytes) / 1024,
		["poseBatchesKB"] = Interlocked.Read(ref PoseBatchBytes) / 1024,
		["clothKB"] = Interlocked.Read(ref ClothBytes) / 1024,
	};

	private static Dictionary<string, object?>? _configuration;

	/// <summary>
	/// The GC's own view of its settings (gen0 size, concurrent, server, conserve memory, hard
	/// limit), and what the Android library set before the runtime started (`android_dotnet_gc`,
	/// refii_gc_preinit.c). Read once: none of it changes after start.
	/// </summary>
	public static Dictionary<string, object?> Configuration()
	{
		if (_configuration is { } cached)
		{
			return cached;
		}

		var reply = new Dictionary<string, object?>();
		try
		{
			foreach (KeyValuePair<string, object> pair in System.GC.GetConfigurationVariables())
			{
				reply[pair.Key] = pair.Value;
			}
		}
		catch (System.Exception exception)
		{
			reply["error"] = exception.GetType().Name;
		}

		reply["isServerGC"] = System.Runtime.GCSettings.IsServerGC;
		reply["latencyMode"] = System.Runtime.GCSettings.LatencyMode.ToString();
		reply["preinitApplied"] = System.Environment.GetEnvironmentVariable("REFII_DOTNET_GC_APPLIED") ?? string.Empty;
		_configuration = reply;
		return reply;
	}
}
