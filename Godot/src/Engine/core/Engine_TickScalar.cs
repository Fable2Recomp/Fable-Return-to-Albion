namespace Refii.Core;

/// <summary>
/// A scalar the guest states once per game tick, drawn by linear interpolation between the two
/// it last stated. This is the guest's own render rule for a record's clip time and weight —
/// `lerp(prev, cur, alpha)` in `sub_82211660` / the type-0 leaf of `sub_822DC980`.
/// `Engine_ClipAnimator` reads it for clip time and blend weights.
///
/// Alpha is anchored to the push, not to <see cref="Engine_TickClock.DrawTick"/> (which drifts
/// against the guest's counter): 0 at the push and 1 at the next, over the pair's width as the
/// stamps state it, so the value drawn either side of a push is the same value. One interval
/// of latency, the same as the pose blend and <see cref="Engine_TickClock.PairAlpha"/>.
/// </summary>
public sealed class Engine_TickScalar
{
	private const int Capacity = 8;

	private readonly uint[] _tick = new uint[Capacity];
	private readonly double[] _value = new double[Capacity];
	private int _count;
	private int _head;
	private readonly Engine_PairPlayhead _playhead = new(Engine_TickClock.TickUsec);

	public bool Empty => _count == 0;

	public uint NewestTick => _count == 0 ? 0u : _tick[_head];

	public double Newest => _count == 0 ? 0.0 : _value[_head];

	/// <summary>Starts over on one sample: a restart, or the first sight of a record.</summary>
	public void Reset(uint tick, double value)
	{
		_count = 0;
		_playhead.Reset();
		Push(tick, value);
	}

	/// <summary>A value stated on a tick. A tick at or before the newest is read skew and becomes the next.</summary>
	public void Push(uint tick, double value)
	{
		if (_count != 0 && tick <= _tick[_head])
		{
			tick = _tick[_head] + 1;
		}

		// The stamps state the width; do not estimate it. The host does not observe every game
		// tick, so a pair spans one tick or two and a smoothed period is wrong for one case.
		// `tick - previous tick` is the span in game ticks; its wall time is that times the tick
		// period. Same rule as Engine_EntitySkeleton.
		double spanTicks = _count == 0
			? 1.0
			: System.Math.Max(1.0, (double)((long)tick - (long)_tick[_head]));
		_playhead.SwapStated(
			Godot.Time.GetTicksUsec(), spanTicks * Engine_TickClock.TickUsec);

		_head = _count == 0 ? 0 : (_head + 1) % Capacity;
		_tick[_head] = tick;
		_value[_head] = value;
		_count = System.Math.Min(_count + 1, Capacity);
	}

	/// <summary>The value to draw now: the last two stated, blended across the interval they last.</summary>
	public double Sample(double nowUsec)
	{
		if (_count < 2 || !_playhead.Ready)
		{
			return Newest;
		}

		double alpha = _playhead.Alpha(nowUsec);
		NoteAlpha(alpha, _playhead.PeriodRatio);
		double previous = _value[At(1)];
		return previous + ((_value[_head] - previous) * alpha);
	}

	/// <summary>
	/// The last alphas any scalar drew at — the clip playhead and the blend weights — as a
	/// sequence, since a histogram cannot tell a smooth ramp from a jump.
	/// </summary>
	private static readonly (ulong Usec, double Alpha, double Period)[] Ring = new (ulong, double, double)[256];

	private static int _at;

	/// <summary>
	/// Records at most one sample per millisecond (the first of each frame), so the ring shows
	/// how alpha moves between frames rather than across subjects within one.
	/// </summary>
	private static void NoteAlpha(double alpha, double period)
	{
		ulong now = Godot.Time.GetTicksUsec();
		if (now - _lastNoted < 1000UL)
		{
			return;
		}

		_lastNoted = now;
		int at = System.Threading.Interlocked.Increment(ref _at);
		Ring[at & 255] = (now, alpha, period);
	}

	private static ulong _lastNoted;

	/// <summary>The clip-path alpha sequence, oldest first; for the debug channel.</summary>
	public static System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>> AlphaTrace()
	{
		var rows = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>();
		int at = _at;
		for (int back = 255; back >= 0; --back)
		{
			(ulong usec, double alpha, double period) = Ring[(at - back) & 255];
			if (usec == 0UL)
			{
				continue;
			}

			rows.Add(new System.Collections.Generic.Dictionary<string, object?>
			{
				["usec"] = usec,
				["alpha"] = alpha,
				["period"] = period,
			});
		}

		return rows;
	}

	private int At(int back) => (_head - back + Capacity) % Capacity;
}
