using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Refii.Host;

/// <summary>
/// The game's own Tracy capture, for the dev menu: start, stop, save, and the queries over the
/// held capture. Contracts are in refii_tracy_capture.h; these are typed wrappers over the host
/// bridge's RefiiHostTracy* exports (host_tracy.cpp).
/// </summary>
/// <remarks>
/// Every query reads the in-process analysis Worker under its lock, so a call costs what it
/// returns; the buffers here are reused between calls because the timeline asks on every redraw
/// it scrolls. Times are Tracy's clock in nanoseconds. Main thread only.
/// </remarks>
public static class HostTracy
{
	private const string Library = "refii_hostbridge";

	/// <summary>Every other process's threads, as one id (see the shadowed TracySysTrace.cpp).</summary>
	public const ulong OtherProcessThread = 0xFFFFFFFE;

	public enum CaptureState
	{
		None = 0,
		Connecting = 1,
		Live = 2,
		Stopped = 3,
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct Status
	{
		public int State;
		public int Failure;
		public ulong Frames;
		public ulong Zones;
		public long FirstNs;
		public long LastNs;
		public long MemoryBytes;
		public long MemoryLimit;
		public ulong Samples;
		public int ContextSwitches;
		public int SystemTracing;

		public readonly CaptureState Capture => (CaptureState)State;
	}

	public readonly record struct ThreadInfo(ulong Id, uint Flags, string Name)
	{
		public bool HasZones => (Flags & 1) != 0;
		public bool HasSamples => (Flags & 2) != 0;
		public bool HasContextSwitches => (Flags & 4) != 0;
	}

	public readonly record struct SourceLocation(string Name, string Function, string File, uint Line, uint Color);

	public readonly record struct ZoneStat(
		short SrcLoc, ulong Count, long Total, long Min, long Max, long Median, long P95, long SelfTotal);

	public readonly record struct Instance(long Start, long End, ulong Thread);

	/// <summary>One source location's instances in a range: histogram, summary and the slowest.</summary>
	public sealed class FindZoneResult
	{
		public ulong[] Histogram = Array.Empty<ulong>();
		public bool LogScale;
		public long Min, Max, Total, Median, P95;
		public ulong Count;
		public List<Instance> Slowest = new();
	}

	/// <summary>A sample call tree node; nodes come depth first, <see cref="Parent"/> -1 for a root.</summary>
	public readonly record struct TreeNode(int Parent, int Depth, ulong Address, uint Total, uint Self, string Name);

	public readonly record struct LockInfo(string Name, uint Threads, ulong Acquired, ulong Contended,
		long WaitTotal, long WaitMax, long HoldTotal, long HoldMax, long WorstWaitAt);

	public readonly record struct MemoryPool(string Name, ulong Allocations, ulong Live, ulong Bytes, ulong Peak);

	public readonly record struct Allocation(ulong Id, ulong Size, long Time);

	public readonly record struct PlotInfo(int Index, string Name, byte Type, byte Format, ulong Count, double Min, double Max);

	public readonly record struct Message(long Time, ulong Thread, uint Color, string Text);

	public readonly record struct SampledSymbol(ulong Address, uint Inclusive, uint Exclusive, string Name, string File, uint Line);

	public readonly record struct Instruction(ulong Address, uint Samples, string Text);

	/// <summary>A reusable result buffer for zone spans: start, end, source location, depth.</summary>
	public sealed class Spans
	{
		public long[] Start = new long[1024];
		public long[] End = new long[1024];
		public short[] SrcLoc = new short[1024];
		public byte[] Depth = new byte[1024];
		public int Count;

		public void Reserve(int capacity)
		{
			if (Start.Length >= capacity)
			{
				return;
			}

			Start = new long[capacity];
			End = new long[capacity];
			SrcLoc = new short[capacity];
			Depth = new byte[capacity];
		}
	}

	/// <summary>A reusable buffer for intervals: start, end and a per-interval value (core or thread).</summary>
	public sealed class Intervals
	{
		public long[] Start = new long[1024];
		public long[] End = new long[1024];
		public ulong[] Value = new ulong[1024];
		public byte[] Cpu = new byte[1024];
		public int Count;

		public void Reserve(int capacity)
		{
			if (Start.Length >= capacity)
			{
				return;
			}

			Start = new long[capacity];
			End = new long[capacity];
			Value = new ulong[capacity];
			Cpu = new byte[capacity];
		}
	}

	private static bool Up => HostBridge.TryGetAbiVersion() is not null;

	// ---- capture ---------------------------------------------------------------------------

	/// <summary>Guest stack sampling: how often, and how deep, while a capture streams.</summary>
	public const uint GuestSamplesPerSecond = 250;

	public const uint GuestSampleDepth = 48;

	/// <summary>
	/// Starts a capture. With <paramref name="guestStacks"/>, the guest threads' call stacks are
	/// sampled into it as their own timelines; and the Godot-side memory already held is reported
	/// first, so the capture opens with what is actually in use.
	/// </summary>
	public static bool Start(long memoryLimitBytes, bool systemTracing, bool guestStacks)
	{
		if (!Up)
		{
			return false;
		}

		bool started = RefiiHostTracyStart(memoryLimitBytes, systemTracing ? 1 : 0) != 0;
		if (started)
		{
			SourceLocations.Clear();
			Refii.Core.Engine_TracedMemory.Replay();
			if (guestStacks)
			{
				RefiiHostGuestSamplerStart(GuestSamplesPerSecond, GuestSampleDepth);
			}
		}

		return started;
	}

	public static void Stop()
	{
		if (Up)
		{
			RefiiHostGuestSamplerStop();
			RefiiHostTracyStop();
		}
	}

	public static void Discard()
	{
		if (Up)
		{
			RefiiHostGuestSamplerStop();
			RefiiHostTracyDiscard();
			SourceLocations.Clear();
		}
	}

	/// <summary>True while a capture streams: one atomic read natively, for per-frame feeds.</summary>
	public static bool Live => Up && RefiiHostTracyLive() != 0;

	public static void Alloc(string pool, ulong id, ulong size)
	{
		if (Up)
		{
			RefiiHostTraceAlloc(pool, id, size);
		}
	}

	public static void Free(string pool, ulong id)
	{
		if (Up)
		{
			RefiiHostTraceFree(pool, id);
		}
	}

	public static ulong LockAnnounce(string name) => Up ? RefiiHostTraceLockAnnounce(name) : 0;

	public static bool LockBefore(ulong handle) => RefiiHostTraceLockBefore(handle) != 0;

	public static void LockAfter(ulong handle) => RefiiHostTraceLockAfter(handle);

	public static void LockAfterUnlock(ulong handle) => RefiiHostTraceLockAfterUnlock(handle);

	public static Status GetStatus()
	{
		var status = default(Status);
		if (Up)
		{
			RefiiHostTracyGetStatus(ref status, (uint)Marshal.SizeOf<Status>());
		}

		return status;
	}

	/// <summary>Writes the stopped capture. Blocks while it compresses; call off the main thread.</summary>
	public static bool Save(string path) => Up && RefiiHostTracySave(path) != 0;

	public static void SetCrashDirectory(string directory)
	{
		if (Up)
		{
			RefiiHostTracySetCrashDirectory(directory);
		}
	}

	// ---- queries ---------------------------------------------------------------------------

	private static long[] _frameStart = new long[4096];
	private static long[] _frameEnd = new long[4096];

	/// <summary>Frame spans from <paramref name="first"/>; the arrays are reused, valid until the next call.</summary>
	public static (long[] Start, long[] End, int Count, ulong Total) Frames(ulong first, int capacity)
	{
		if (_frameStart.Length < capacity)
		{
			_frameStart = new long[capacity];
			_frameEnd = new long[capacity];
		}

		ulong total = 0;
		int count = Up ? (int)RefiiHostTracyGetFrames(first, (uint)capacity, _frameStart, _frameEnd, out total) : 0;
		return (_frameStart, _frameEnd, count, total);
	}

	private const uint NameStride = 96;
	private const uint FileStride = 192;
	private const uint TextStride = 160;

	public static List<ThreadInfo> Threads()
	{
		var threads = new List<ThreadInfo>();
		if (!Up)
		{
			return threads;
		}

		const uint Capacity = 256;
		var ids = new ulong[Capacity];
		var flags = new uint[Capacity];
		var names = new byte[Capacity * NameStride];
		uint count = RefiiHostTracyGetThreads(Capacity, ids, flags, names, NameStride);
		for (int i = 0; i < count; ++i)
		{
			threads.Add(new ThreadInfo(ids[i], flags[i], Slot(names, i, NameStride)));
		}

		return threads;
	}

	public static void Zones(ulong thread, long t0, long t1, long minNs, uint maxDepth, int capacity, Spans into)
	{
		into.Reserve(capacity);
		into.Count = Up
			? (int)RefiiHostTracyGetZones(thread, t0, t1, minNs, maxDepth, (uint)capacity,
				into.Start, into.End, into.SrcLoc, into.Depth)
			: 0;
	}

	public static void GpuZones(int context, long t0, long t1, long minNs, uint maxDepth, int capacity, Spans into)
	{
		into.Reserve(capacity);
		into.Count = Up
			? (int)RefiiHostTracyGetGpuZones((uint)context, t0, t1, minNs, maxDepth, (uint)capacity,
				into.Start, into.End, into.SrcLoc, into.Depth)
			: 0;
	}

	/// <summary>Per capture: a source location's strings never change once the capture has it.</summary>
	private static readonly Dictionary<short, SourceLocation> SourceLocations = new();

	public static SourceLocation Location(short srcloc)
	{
		if (SourceLocations.TryGetValue(srcloc, out SourceLocation known))
		{
			return known;
		}

		var location = new SourceLocation("?", string.Empty, string.Empty, 0, 0);
		if (Up)
		{
			var name = new byte[NameStride];
			var function = new byte[NameStride];
			var file = new byte[FileStride];
			if (RefiiHostTracyGetSourceLocation(srcloc, name, NameStride, function, NameStride, file, FileStride,
					out uint line, out uint color) != 0)
			{
				location = new SourceLocation(Slot(name, 0, NameStride), Slot(function, 0, NameStride),
					Slot(file, 0, FileStride), line, color);
				SourceLocations[srcloc] = location;
			}
		}

		return location;
	}

	public static List<ZoneStat> ZoneStats()
	{
		var stats = new List<ZoneStat>();
		if (!Up)
		{
			return stats;
		}

		const uint Capacity = 4096;
		var srcloc = new short[Capacity];
		var count = new ulong[Capacity];
		var total = new long[Capacity];
		var min = new long[Capacity];
		var max = new long[Capacity];
		var median = new long[Capacity];
		var p95 = new long[Capacity];
		var self = new long[Capacity];
		uint n = RefiiHostTracyGetZoneStats(Capacity, srcloc, count, total, min, max, median, p95, self);
		for (int i = 0; i < n; ++i)
		{
			stats.Add(new ZoneStat(srcloc[i], count[i], total[i], min[i], max[i], median[i], p95[i], self[i]));
		}

		return stats;
	}

	/// <summary>Zone statistics for zones starting in [t0, t1], on one thread or all (0).</summary>
	public static List<ZoneStat> ZoneStats(ulong thread, long t0, long t1)
	{
		var stats = new List<ZoneStat>();
		if (!Up)
		{
			return stats;
		}

		const uint Capacity = 4096;
		var srcloc = new short[Capacity];
		var count = new ulong[Capacity];
		var total = new long[Capacity];
		var min = new long[Capacity];
		var max = new long[Capacity];
		var median = new long[Capacity];
		var p95 = new long[Capacity];
		var self = new long[Capacity];
		uint n = RefiiHostTracyGetZoneStatsRange(thread, t0, t1, Capacity, srcloc, count, total, min, max, median, p95, self);
		for (int i = 0; i < n; ++i)
		{
			stats.Add(new ZoneStat(srcloc[i], count[i], total[i], min[i], max[i], median[i], p95[i], self[i]));
		}

		return stats;
	}

	public static FindZoneResult FindZone(short srcloc, ulong thread, long t0, long t1, int buckets, bool logScale, int slowest)
	{
		var result = new FindZoneResult { Histogram = new ulong[buckets], LogScale = logScale };
		if (!Up)
		{
			return result;
		}

		var start = new long[slowest];
		var end = new long[slowest];
		var threads = new ulong[slowest];
		uint n = RefiiHostTracyFindZone(srcloc, thread, t0, t1, (uint)buckets, logScale ? 1 : 0, result.Histogram,
			out result.Min, out result.Max, out result.Count, out result.Total, out result.Median, out result.P95,
			(uint)slowest, start, end, threads);
		for (int i = 0; i < n; ++i)
		{
			result.Slowest.Add(new Instance(start[i], end[i], threads[i]));
		}

		return result;
	}

	/// <summary>
	/// The sample call tree over [t0, t1], one thread or all (0): top-down, or bottom-up when
	/// <paramref name="inverted"/>. Branches under <paramref name="minSamples"/> are left out.
	/// </summary>
	public static (List<TreeNode> Nodes, ulong Samples) SampleTree(ulong thread, long t0, long t1, bool inverted,
		uint minSamples, int capacity)
	{
		var nodes = new List<TreeNode>();
		if (!Up)
		{
			return (nodes, 0);
		}

		var parent = new int[capacity];
		var depth = new byte[capacity];
		var address = new ulong[capacity];
		var total = new uint[capacity];
		var self = new uint[capacity];
		var names = new byte[capacity * NameStride];
		uint n = RefiiHostTracyGetSampleTree(thread, t0, t1, inverted ? 1 : 0, minSamples, (uint)capacity, parent,
			depth, address, total, self, names, NameStride, out ulong samples);
		for (int i = 0; i < n; ++i)
		{
			nodes.Add(new TreeNode(parent[i], depth[i], address[i], total[i], self[i], Slot(names, i, NameStride)));
		}

		return (nodes, samples);
	}

	/// <summary>Every lock acquired in [t0, t1], with its contention.</summary>
	public static List<LockInfo> Locks(long t0, long t1)
	{
		var locks = new List<LockInfo>();
		if (!Up)
		{
			return locks;
		}

		const uint Capacity = 256;
		var names = new byte[Capacity * NameStride];
		var threads = new uint[Capacity];
		var acquired = new ulong[Capacity];
		var contended = new ulong[Capacity];
		var waitTotal = new long[Capacity];
		var waitMax = new long[Capacity];
		var holdTotal = new long[Capacity];
		var holdMax = new long[Capacity];
		var worstAt = new long[Capacity];
		uint n = RefiiHostTracyGetLocks(t0, t1, Capacity, names, NameStride, threads, acquired, contended, waitTotal,
			waitMax, holdTotal, holdMax, worstAt);
		for (int i = 0; i < n; ++i)
		{
			locks.Add(new LockInfo(Slot(names, i, NameStride), threads[i], acquired[i], contended[i], waitTotal[i],
				waitMax[i], holdTotal[i], holdMax[i], worstAt[i]));
		}

		return locks;
	}

	public static List<MemoryPool> MemoryPools()
	{
		var pools = new List<MemoryPool>();
		if (!Up)
		{
			return pools;
		}

		const uint Capacity = 64;
		var names = new byte[Capacity * NameStride];
		var allocations = new ulong[Capacity];
		var live = new ulong[Capacity];
		var bytes = new ulong[Capacity];
		var peak = new ulong[Capacity];
		uint n = RefiiHostTracyGetMemoryPools(Capacity, names, NameStride, allocations, live, bytes, peak);
		for (int i = 0; i < n; ++i)
		{
			pools.Add(new MemoryPool(Slot(names, i, NameStride), allocations[i], live[i], bytes[i], peak[i]));
		}

		return pools;
	}

	public static List<Allocation> LargestAllocations(string pool, int capacity)
	{
		var allocations = new List<Allocation>();
		if (!Up)
		{
			return allocations;
		}

		var id = new ulong[capacity];
		var size = new ulong[capacity];
		var time = new long[capacity];
		uint n = RefiiHostTracyGetMemoryLargest(pool, (uint)capacity, id, size, time);
		for (int i = 0; i < n; ++i)
		{
			allocations.Add(new Allocation(id[i], size[i], time[i]));
		}

		return allocations;
	}

	public static List<PlotInfo> Plots()
	{
		var plots = new List<PlotInfo>();
		if (!Up)
		{
			return plots;
		}

		const uint Capacity = 128;
		var names = new byte[Capacity * NameStride];
		var type = new byte[Capacity];
		var format = new byte[Capacity];
		var count = new ulong[Capacity];
		var min = new double[Capacity];
		var max = new double[Capacity];
		uint n = RefiiHostTracyGetPlots(Capacity, names, NameStride, type, format, count, min, max);
		for (int i = 0; i < n; ++i)
		{
			plots.Add(new PlotInfo(i, Slot(names, i, NameStride), type[i], format[i], count[i], min[i], max[i]));
		}

		return plots;
	}

	public static int PlotData(int plot, long t0, long t1, long[] time, double[] value) =>
		Up ? (int)RefiiHostTracyGetPlotData((uint)plot, t0, t1, (uint)Math.Min(time.Length, value.Length), time, value) : 0;

	public static (List<Message> Messages, ulong Total) Messages(ulong first, int capacity)
	{
		var messages = new List<Message>();
		if (!Up)
		{
			return (messages, 0);
		}

		var time = new long[capacity];
		var thread = new ulong[capacity];
		var color = new uint[capacity];
		var text = new byte[capacity * TextStride];
		uint n = RefiiHostTracyGetMessages(first, (uint)capacity, time, thread, color, text, TextStride, out ulong total);
		for (int i = 0; i < n; ++i)
		{
			messages.Add(new Message(time[i], thread[i], color[i], Slot(text, i, TextStride)));
		}

		return (messages, total);
	}

	public static List<(string Name, ulong Zones)> GpuContexts()
	{
		var contexts = new List<(string, ulong)>();
		if (!Up)
		{
			return contexts;
		}

		const uint Capacity = 16;
		var names = new byte[Capacity * NameStride];
		var zones = new ulong[Capacity];
		uint n = RefiiHostTracyGetGpuContexts(Capacity, names, NameStride, zones);
		for (int i = 0; i < n; ++i)
		{
			contexts.Add((Slot(names, i, NameStride), zones[i]));
		}

		return contexts;
	}

	/// <summary>When <paramref name="thread"/> ran in [t0, t1]; <see cref="Intervals.Cpu"/> is the core.</summary>
	public static void Running(ulong thread, long t0, long t1, int capacity, Intervals into)
	{
		into.Reserve(capacity);
		into.Count = Up
			? (int)RefiiHostTracyGetRunning(thread, t0, t1, (uint)capacity, into.Start, into.End, into.Cpu)
			: 0;
	}

	public static int CpuCount() => Up ? (int)RefiiHostTracyGetCpuCount() : 0;

	/// <summary>What a core ran in [t0, t1]; <see cref="Intervals.Value"/> is the thread.</summary>
	public static void CpuSlices(int cpu, long t0, long t1, int capacity, Intervals into)
	{
		into.Reserve(capacity);
		into.Count = Up
			? (int)RefiiHostTracyGetCpuSlices((uint)cpu, t0, t1, (uint)capacity, into.Start, into.End, into.Value)
			: 0;
	}

	public static List<SampledSymbol> SampledSymbols(int capacity)
	{
		var symbols = new List<SampledSymbol>();
		if (!Up)
		{
			return symbols;
		}

		var address = new ulong[capacity];
		var inclusive = new uint[capacity];
		var exclusive = new uint[capacity];
		var names = new byte[capacity * NameStride];
		var files = new byte[capacity * FileStride];
		var line = new uint[capacity];
		uint n = RefiiHostTracyGetSampledSymbols((uint)capacity, address, inclusive, exclusive, names, NameStride,
			files, FileStride, line);
		for (int i = 0; i < n; ++i)
		{
			symbols.Add(new SampledSymbol(address[i], inclusive[i], exclusive[i], Slot(names, i, NameStride),
				Slot(files, i, FileStride), line[i]));
		}

		return symbols;
	}

	public static List<Instruction> Disassemble(ulong symbol, int capacity)
	{
		var lines = new List<Instruction>();
		if (!Up)
		{
			return lines;
		}

		var address = new ulong[capacity];
		var samples = new uint[capacity];
		var text = new byte[capacity * TextStride];
		uint n = RefiiHostTracyDisassemble(symbol, (uint)capacity, address, samples, text, TextStride);
		for (int i = 0; i < n; ++i)
		{
			lines.Add(new Instruction(address[i], samples[i], Slot(text, i, TextStride)));
		}

		return lines;
	}

	private static string Slot(byte[] buffer, int index, uint stride)
	{
		int start = index * (int)stride;
		int end = start;
		while (end < start + stride && buffer[end] != 0)
		{
			end++;
		}

		return Encoding.UTF8.GetString(buffer, start, end - start);
	}

	// ---- imports ---------------------------------------------------------------------------

	[DllImport(Library)]
	private static extern int RefiiHostTracyStart(long memoryLimitBytes, int systemTracing);

	[DllImport(Library)]
	private static extern void RefiiHostTracyStop();

	[DllImport(Library)]
	private static extern void RefiiHostTracyDiscard();

	[DllImport(Library)]
	private static extern void RefiiHostTracyGetStatus(ref Status status, uint size);

	[DllImport(Library)]
	private static extern int RefiiHostTracySave([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

	[DllImport(Library)]
	private static extern void RefiiHostTracySetCrashDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string directory);

	[DllImport(Library)]
	private static extern uint RefiiHostTracyGetFrames(ulong first, uint capacity, [Out] long[] start, [Out] long[] end,
		out ulong total);

	[DllImport(Library)]
	private static extern uint RefiiHostTracyGetThreads(uint capacity, [Out] ulong[] id, [Out] uint[] flags,
		[Out] byte[] names, uint nameStride);

	[DllImport(Library)]
	private static extern uint RefiiHostTracyGetZones(ulong thread, long t0, long t1, long minNs, uint maxDepth,
		uint capacity, [Out] long[] start, [Out] long[] end, [Out] short[] srcloc, [Out] byte[] depth);

	[DllImport(Library)]
	private static extern int RefiiHostTracyGetSourceLocation(short srcloc, [Out] byte[] name, uint nameSize,
		[Out] byte[] function, uint functionSize, [Out] byte[] file, uint fileSize, out uint line, out uint color);

	[DllImport(Library)]
	private static extern uint RefiiHostTracyGetZoneStats(uint capacity, [Out] short[] srcloc, [Out] ulong[] count,
		[Out] long[] total, [Out] long[] min, [Out] long[] max, [Out] long[] median, [Out] long[] p95,
		[Out] long[] selfTotal);

	[DllImport(Library)]
	private static extern int RefiiHostTracyLive();

	[DllImport(Library)]
	private static extern void RefiiHostTraceAlloc([MarshalAs(UnmanagedType.LPUTF8Str)] string pool, ulong id, ulong size);

	[DllImport(Library)]
	private static extern void RefiiHostTraceFree([MarshalAs(UnmanagedType.LPUTF8Str)] string pool, ulong id);

	[DllImport(Library)]
	private static extern ulong RefiiHostTraceLockAnnounce([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

	[DllImport(Library)]
	private static extern int RefiiHostTraceLockBefore(ulong handle);

	[DllImport(Library)]
	private static extern void RefiiHostTraceLockAfter(ulong handle);

	[DllImport(Library)]
	private static extern void RefiiHostTraceLockAfterUnlock(ulong handle);

	[DllImport(Library)]
	private static extern int RefiiHostGuestSamplerStart(uint hz, uint depth);

	[DllImport(Library)]
	private static extern void RefiiHostGuestSamplerStop();

	[DllImport(Library)]
	private static extern uint RefiiHostTracyGetLocks(long t0, long t1, uint capacity, [Out] byte[] names,
		uint nameStride, [Out] uint[] threads, [Out] ulong[] acquired, [Out] ulong[] contended, [Out] long[] waitTotal,
		[Out] long[] waitMax, [Out] long[] holdTotal, [Out] long[] holdMax, [Out] long[] worstWaitAt);

	[DllImport(Library)]
	private static extern uint RefiiHostTracyGetMemoryPools(uint capacity, [Out] byte[] names, uint nameStride,
		[Out] ulong[] allocations, [Out] ulong[] live, [Out] ulong[] bytes, [Out] ulong[] peak);

	[DllImport(Library)]
	private static extern uint RefiiHostTracyGetMemoryLargest([MarshalAs(UnmanagedType.LPUTF8Str)] string pool,
		uint capacity, [Out] ulong[] id, [Out] ulong[] size, [Out] long[] time);

	[DllImport(Library)]
	private static extern uint RefiiHostTracyGetZoneStatsRange(ulong thread, long t0, long t1, uint capacity,
		[Out] short[] srcloc, [Out] ulong[] count, [Out] long[] total, [Out] long[] min, [Out] long[] max,
		[Out] long[] median, [Out] long[] p95, [Out] long[] selfTotal);

	[DllImport(Library)]
	private static extern uint RefiiHostTracyFindZone(short srcloc, ulong thread, long t0, long t1, uint buckets,
		int logScale, [Out] ulong[] histogram, out long min, out long max, out ulong count, out long total,
		out long median, out long p95, uint slowestCapacity, [Out] long[] slowStart, [Out] long[] slowEnd,
		[Out] ulong[] slowThread);

	[DllImport(Library)]
	private static extern uint RefiiHostTracyGetSampleTree(ulong thread, long t0, long t1, int inverted,
		uint minSamples, uint capacity, [Out] int[] parent, [Out] byte[] depth, [Out] ulong[] address,
		[Out] uint[] total, [Out] uint[] self, [Out] byte[] names, uint nameStride, out ulong samples);

	[DllImport(Library)]
	private static extern uint RefiiHostTracyGetPlots(uint capacity, [Out] byte[] names, uint nameStride,
		[Out] byte[] type, [Out] byte[] format, [Out] ulong[] count, [Out] double[] min, [Out] double[] max);

	[DllImport(Library)]
	private static extern uint RefiiHostTracyGetPlotData(uint plot, long t0, long t1, uint capacity,
		[Out] long[] time, [Out] double[] value);

	[DllImport(Library)]
	private static extern uint RefiiHostTracyGetMessages(ulong first, uint capacity, [Out] long[] time,
		[Out] ulong[] thread, [Out] uint[] color, [Out] byte[] text, uint textStride, out ulong total);

	[DllImport(Library)]
	private static extern uint RefiiHostTracyGetGpuContexts(uint capacity, [Out] byte[] names, uint nameStride,
		[Out] ulong[] zones);

	[DllImport(Library)]
	private static extern uint RefiiHostTracyGetGpuZones(uint context, long t0, long t1, long minNs, uint maxDepth,
		uint capacity, [Out] long[] start, [Out] long[] end, [Out] short[] srcloc, [Out] byte[] depth);

	[DllImport(Library)]
	private static extern uint RefiiHostTracyGetRunning(ulong thread, long t0, long t1, uint capacity,
		[Out] long[] start, [Out] long[] end, [Out] byte[] cpu);

	[DllImport(Library)]
	private static extern uint RefiiHostTracyGetCpuCount();

	[DllImport(Library)]
	private static extern uint RefiiHostTracyGetCpuSlices(uint cpu, long t0, long t1, uint capacity,
		[Out] long[] start, [Out] long[] end, [Out] ulong[] thread);

	[DllImport(Library)]
	private static extern uint RefiiHostTracyGetSampledSymbols(uint capacity, [Out] ulong[] address,
		[Out] uint[] inclusive, [Out] uint[] exclusive, [Out] byte[] names, uint nameStride, [Out] byte[] files,
		uint fileStride, [Out] uint[] line);

	[DllImport(Library)]
	private static extern uint RefiiHostTracyDisassemble(ulong symbol, uint capacity, [Out] ulong[] address,
		[Out] uint[] samples, [Out] byte[] text, uint textStride);
}
