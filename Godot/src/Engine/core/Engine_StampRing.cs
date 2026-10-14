namespace Refii.Core;

/// <summary>
/// The stamps of the last few values a series stated on the guest's tick timeline, and where a
/// draw point falls among them. Holds no payload: a caller keeps its values in its own array,
/// indexed by the slot <see cref="Push"/> returns.
/// </summary>
/// <remarks>
/// LOOKED UP BY STAMP, NEVER BY ARRIVAL. When the walk noticed a value does not enter, so a
/// value noticed early or late draws the same; that is the whole point of the type. A series
/// anchored to its arrivals restarts at each one: early is a forward jump, late is a hold.
/// </remarks>
public sealed class Engine_StampRing
{
	private readonly double[] _stamp;
	private int _head;
	private uint _stampSim = uint.MaxValue;
	private int _stampPhase;

	public Engine_StampRing(int capacity)
	{
		_stamp = new double[capacity];
	}

	public int Capacity => _stamp.Length;

	public int Count { get; private set; }

	/// <summary>The slot <paramref name="back"/> pushes before the newest; 0 is the newest.</summary>
	public int Slot(int back) => (_head - back + _stamp.Length) % _stamp.Length;

	public double Stamp(int back) => _stamp[Slot(back)];

	public double NewestStamp => Count == 0 ? double.NaN : _stamp[_head];

	public void Clear() => Count = 0;

	/// <summary>Adds a stamp and answers the slot it occupies; the oldest goes when full.</summary>
	public int Push(double stamp)
	{
		_head = Count == 0 ? 0 : (_head + 1) % _stamp.Length;
		_stamp[_head] = stamp;
		Count = System.Math.Min(Count + 1, _stamp.Length);
		return _head;
	}

	/// <summary>
	/// The stamp for a sample published on an HF step. The guest evaluates every HF step (30 Hz)
	/// at alpha 0.5 on the iteration the game tick ran (the counter is already T; the sample is
	/// halfway between T−1 and T) and at alpha 1.0 on the next (`(HFindex % 2 + 1) / 2`, see the
	/// anim timing contract). So the first sample under a new counter is T − 0.5, the next T. A
	/// stamp never sits at or before the newest.
	/// </summary>
	public double NextHalfTick(uint sim)
	{
		if (sim != _stampSim)
		{
			_stampSim = sim;
			_stampPhase = 0;
		}

		double stamp = sim - 0.5 + (0.5 * System.Math.Min(_stampPhase, 1));
		++_stampPhase;
		if (Count != 0 && stamp <= _stamp[_head])
		{
			stamp = _stamp[_head] + 0.25;
		}

		return stamp;
	}

	/// <summary>Where a draw point lies against the stamps.</summary>
	public enum Where
	{
		/// <summary>Nothing stated.</summary>
		Empty,

		/// <summary>At or past the newest, or one stamp only: <c>later</c> is the newest.</summary>
		After,

		/// <summary>Before the oldest: <c>later</c> is the oldest.</summary>
		Before,

		/// <summary>Between <c>earlier</c> and <c>later</c> at <c>t</c>.</summary>
		Between,

		/// <summary>Between two stamps further apart than the stale span: <c>later</c> stands.</summary>
		Stale,
	}

	/// <summary>
	/// The two slots around <paramref name="at"/> and how far between them it lies. A NaN draw
	/// point answers the newest.
	/// </summary>
	public Where Locate(double at, double staleSpan, out int earlier, out int later, out double t)
	{
		t = 1.0;
		earlier = later = _head;
		if (Count == 0)
		{
			return Where.Empty;
		}

		if (Count < 2 || double.IsNaN(at) || at >= _stamp[_head])
		{
			earlier = Count < 2 ? _head : Slot(1);
			return Where.After;
		}

		for (int back = 1; back < Count; ++back)
		{
			int newer = Slot(back - 1);
			int older = Slot(back);
			if (at < _stamp[older])
			{
				continue;
			}

			later = newer;
			earlier = older;
			double span = _stamp[newer] - _stamp[older];
			if (span > staleSpan)
			{
				return Where.Stale;
			}

			t = (at - _stamp[older]) / span;
			return Where.Between;
		}

		earlier = later = Slot(Count - 1);
		t = 0.0;
		return Where.Before;
	}
}
