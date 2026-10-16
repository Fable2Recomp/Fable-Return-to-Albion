using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// The Tracy capture's locks and memory. Locks: each traced lock's acquisitions, how many had to
/// wait on another holder, and its wait and hold times — pick one to open its longest wait in the
/// timeline. Memory: each named pool's allocations, what is live, its bytes and peak — pick one for
/// its largest live allocations.
/// </summary>
/// <remarks>
/// Traced locks: the guest clock grant, the guest tick pump, the audio ring and voice table, the
/// guest I/O completion lock, and the gates Godot's main and render threads share (frame phases,
/// render stats, GPU passes, post-FX halos). Pools: the texture cache, render targets, movies.
/// </remarks>
public partial class GameDevTabTracyLocks : GameDevTab
{
	public override string Title => "Tracy locks & memory";

	public override double RefreshSeconds => 1.0;

	private const int LargestCapacity = 200;

	private readonly GameDevTracyScope _scope = new();
	private readonly Label _summary = Readout();
	private readonly GameDevTable _locks = new(new[]
	{
		"lock", "threads", "acquired", "contended", "contended %", "wait ms", "longest wait ms", "hold ms", "longest hold ms",
	}, sortColumn: 5);

	private readonly GameDevTable _pools = new(new[] { "pool", "allocations", "live", "live MB", "peak MB" }, sortColumn: 3);
	private readonly GameDevTable _largest = new(new[] { "#", "id", "size KB", "made at" }, sortColumn: -1);
	private readonly Dictionary<string, (long At, long Wait)> _worst = new();
	private string _pickedPool = string.Empty;
	private ulong _shown = ulong.MaxValue;
	private bool _stale = true;

	public GameDevTabTracyLocks()
	{
		AddChild(_scope);
		AddChild(_summary);
		var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		split.AddChild(_locks);
		var memory = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		memory.AddChild(_pools);
		_largest.CustomMinimumSize = new Vector2(380, 140);
		memory.AddChild(_largest);
		split.AddChild(memory);
		AddChild(split);

		_scope.Changed += () =>
		{
			_stale = true;
			Refresh();
		};
		_locks.Picked += row =>
		{
			if (_worst.TryGetValue(row[0], out (long At, long Wait) worst) && worst.Wait > 0)
			{
				GameDevTabTracy.Reveal(worst.At, worst.At + worst.Wait);
			}
		};
		_pools.Picked += row =>
		{
			_pickedPool = row[0];
			ShowLargest();
		};
	}

	public override void Refresh()
	{
		HostTracy.Status status = HostTracy.GetStatus();
		if (status.Capture == HostTracy.CaptureState.None)
		{
			_summary.Text = "no capture — start one on the Tracy tab";
			_locks.Show(new List<string[]>());
			_pools.Show(new List<string[]>());
			_largest.Show(new List<string[]>());
			_shown = ulong.MaxValue;
			return;
		}

		bool live = status.Capture != HostTracy.CaptureState.Stopped;
		ulong marker = status.Zones ^ (ulong)status.LastNs;
		if (!live && marker == _shown && !_stale)
		{
			return;
		}

		_shown = marker;
		_stale = false;
		(long t0, long t1) = _scope.Range;
		var lockRows = new List<string[]>();
		_worst.Clear();
		foreach (HostTracy.LockInfo info in HostTracy.Locks(t0, t1))
		{
			_worst[info.Name] = (info.WorstWaitAt, info.WaitMax);
			lockRows.Add(new[]
			{
				info.Name,
				info.Threads.ToString(),
				info.Acquired.ToString(),
				info.Contended.ToString(),
				$"{info.Contended * 100.0 / System.Math.Max(1UL, info.Acquired):0.0}",
				Ms(info.WaitTotal),
				Ms(info.WaitMax),
				Ms(info.HoldTotal),
				Ms(info.HoldMax),
			});
		}

		_locks.Show(lockRows);

		var poolRows = new List<string[]>();
		foreach (HostTracy.MemoryPool pool in HostTracy.MemoryPools())
		{
			poolRows.Add(new[]
			{
				pool.Name,
				pool.Allocations.ToString(),
				pool.Live.ToString(),
				$"{pool.Bytes / 1048576.0:0.0}",
				$"{pool.Peak / 1048576.0:0.0}",
			});
		}

		_pools.Show(poolRows);
		if (_pickedPool.Length != 0)
		{
			ShowLargest();
		}

		_summary.Text = $"{lockRows.Count} locks over {_scope.Describe()}   ·   {poolRows.Count} memory pools (whole capture)" +
			(live ? "   ·   live, updating each second" : string.Empty) +
			"   ·   pick a lock for its longest wait, a pool for its largest allocations";
	}

	private void ShowLargest()
	{
		HostTracy.Status status = HostTracy.GetStatus();
		var rows = new List<string[]>();
		List<HostTracy.Allocation> largest = HostTracy.LargestAllocations(_pickedPool, LargestCapacity);
		for (int i = 0; i < largest.Count; ++i)
		{
			rows.Add(new[]
			{
				(i + 1).ToString(),
				$"0x{largest[i].Id:X}",
				$"{largest[i].Size / 1024.0:0.0}",
				GameDevTracyTimeline.FormatNs(largest[i].Time - status.FirstNs),
			});
		}

		_largest.Show(rows);
	}

	private static string Ms(long ns) => $"{ns / 1e6:0.000}";
}
