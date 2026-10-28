using System.Collections.Generic;
using System.Threading;

namespace Refii.Host;

/// <summary>
/// The entity walk (<see cref="HostBridge.GetEntities"/>) run on a worker thread so the main
/// thread does not pay it. Contract: <see cref="Kick"/> at the start of a frame starts one walk;
/// <see cref="TryTake"/> later in the same frame hands the result over, blocking only if the
/// walk is still running. One walk per kick; a kick while one is pending is ignored, so a frame
/// that never takes leaves the next kick a no-op until the result is consumed.
/// </summary>
/// <remarks>
/// The walk reads guest memory with no guest code and tolerates torn reads (one bad frame), so
/// it needs nothing from the main thread. Kicking at frame start and taking after the UI and
/// world have processed keeps the sample as fresh as a synchronous read: the tick and the time
/// the timelines are stamped with are still read on the main thread at the take.
/// </remarks>
public static class HostEntityWalk
{
	/// <summary>One walk's result.</summary>
	public readonly record struct Walk(
		List<HostBridge.GuestEntity> Entities,
		uint Unknown,
		HostBridge.UnknownPlacement[] UnknownVtables);

	/// <summary>Debug lever: false leaves the feed to walk synchronously.</summary>
	public static bool Enabled = true;

	/// <summary>Walks handed over, and how many of those the main thread had to wait for.</summary>
	public static long Taken;

	public static long Waited;

	/// <summary>
	/// Walks during which the guest advanced a game tick, and the worst number of ticks one
	/// walk spanned.
	/// </summary>
	/// <remarks>
	/// The walk races the guest's simulation thread, which writes the same memory. A tick
	/// completing mid-walk leaves entities from tick T and T+1 in one snapshot, all stamped with
	/// the single tick the feed reads afterwards. Nonzero means the walk needs to be taken against
	/// a tick boundary.
	/// </remarks>
	public static long TornWalks;

	/// <summary>The most game ticks a single walk spanned.</summary>
	public static long WalkTickSpanWorst;

	private static Thread? _thread;
	private static readonly AutoResetEvent _kick = new(false);
	private static readonly ManualResetEventSlim _done = new(true);
	private static Walk _result;

	/// <summary>Two lists, filled alternately: the one the main thread took last is never the one being filled.</summary>
	private static readonly List<HostBridge.GuestEntity>[] _lists = { new(), new() };
	private static int _fill;

	/// <summary>Main thread only: a walk was kicked and not yet taken.</summary>
	private static bool _pending;

	/// <summary>Starts a walk for this frame. Main thread only.</summary>
	public static void Kick()
	{
		if (!Enabled || _pending || HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		if (_thread is null)
		{
			_thread = new Thread(Loop)
			{
				Name = "refii entity walk",
				IsBackground = true,
			};
			_thread.Start();
		}

		_pending = true;
		_done.Reset();
		_kick.Set();
	}

	/// <summary>
	/// The walk kicked this frame, waiting for it if it has not finished. False when none was
	/// kicked, in which case the caller walks synchronously.
	/// </summary>
	public static bool TryTake(out Walk walk)
	{
		if (!_pending)
		{
			walk = default;
			return false;
		}

		if (!_done.IsSet)
		{
			++Waited;
			_done.Wait();
		}

		walk = _result;
		_pending = false;
		++Taken;
		return true;
	}

	private static void Loop()
	{
		while (true)
		{
			_kick.WaitOne();
			long allocFrom = System.GC.GetAllocatedBytesForCurrentThread();
			List<HostBridge.GuestEntity> into = _lists[_fill];
			_fill ^= 1;

			// Read live, not from the published stamp: both reads bracket the walk on this thread.
			uint before = HostBridge.SimClock() is { } a ? a.Tick : 0u;
			List<HostBridge.GuestEntity> entities =
				HostBridge.GetEntities(into, out uint unknown, out HostBridge.UnknownPlacement[] unknownVtables);
			uint after = HostBridge.SimClock() is { } b ? b.Tick : 0u;

			if (before != 0u && after != 0u && after > before)
			{
				++TornWalks;
				long span = after - before;
				if (span > WalkTickSpanWorst)
				{
					WalkTickSpanWorst = span;
				}
			}

			_result = new Walk(entities, unknown, unknownVtables);
			Refii.Core.Engine_GcFrames.Note(ref Refii.Core.Engine_GcFrames.EntityWalkBytes,
				System.GC.GetAllocatedBytesForCurrentThread() - allocFrom);
			_done.Set();
		}
	}
}
