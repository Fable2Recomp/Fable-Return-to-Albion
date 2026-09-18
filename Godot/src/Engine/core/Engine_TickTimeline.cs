using Godot;

namespace Refii.Core;

/// <summary>
/// Draws a transform the guest states on its 15 Hz sim tick as a smooth path on the host clock.
/// </summary>
/// <remarks>
/// On the guest's clock, not on arrival. The guest states placements and its camera on its game
/// tick (`dbl_83319660 = 15`), and every stated change lands on a tick change. The counter is
/// `Engine_TickClock.GuestGameTick`, not `dword_83319650`: the latter is gated (`sub_821D4628`
/// branches past its own increment at `821d46e8`) and skips game ticks. Drawing each sample as
/// it lands re-times every segment by when the host happened to see it.
///
/// So each sample is stamped with the tick it was stated on and a frame draws between the two
/// the guest last stated, at the fraction <see cref="Engine_PairPlayhead"/> gives — anchored to
/// the swap, carried past a late one, never restarted. Everything the guest states on the tick
/// — bodies, the camera — goes through the same arithmetic, so nothing drifts against anything
/// else.
/// </remarks>
public sealed class Engine_TickTimeline
{
	private const int Capacity = 8;

	/// <summary>The guest's tick period: a fixed 1/15 s.</summary>
	public const double TickUsec = Engine_TickClock.TickUsec;

	/// <summary>
	/// How far behind the newest sample the world is drawn, in ticks. A sample lands on the
	/// host up to a frame after its tick, so under one tick plus a frame starves and holds.
	/// </summary>
	public static double DrawLatencyTicks => Engine_TickClock.DrawLatencyTicks;

	/// <summary>
	/// The step, in metres, past which a sample is a teleport rather than a movement — the
	/// timeline restarts on it instead of gliding across.
	/// </summary>
	/// <remarks>
	/// ONE THRESHOLD, BECAUSE THINGS THAT MOVE TOGETHER MUST RESTART TOGETHER. A body used 4 m
	/// and the camera following it used 8. A hero moved 5 m in a tick — a door, a cut, a
	/// scripted placement — and the two rules disagreed about what had just happened: the body
	/// restarted and was drawn at its new position at once, while the camera treated the same
	/// jump as travel and glided across it over the next tick and a half. For that tenth of a
	/// second the hero was somewhere the camera had not reached, which is the camera letting go
	/// of the player. 4 m in a 15 Hz tick is already 60 m/s and nothing runs there.
	/// </remarks>
	public const float TeleportMetres = 4f;

	private readonly double[] _tick = new double[Capacity];
	private readonly Transform3D[] _sample = new Transform3D[Capacity];

	/// <summary>
	/// A value the guest states on the SAME tick as the transform — the camera's field of
	/// view. It rides this timeline rather than one of its own so it cannot be drawn on a
	/// different clock from the transform it belongs to: a zoom applied raw while the dolly
	/// lagged 1.5 ticks behind is two motions for one movement.
	/// </summary>
	private readonly float[] _scalar = new float[Capacity];
	private readonly Engine_PairPlayhead _playhead = new(TickUsec, carry: true);
	private int _count;
	private int _head;

	/// <summary>The newest stated transform.</summary>
	public Transform3D Newest { get; private set; }

	/// <summary>The newest stated companion value.</summary>
	public float NewestScalar { get; private set; }

	public bool Empty => _count == 0;

	public double NewestTick => _count == 0 ? 0.0 : _tick[_head];

	private int At(int back) => (_head - back + Capacity) % Capacity;

	/// <summary>Starts over on a sample — a teleport, a load, the first sight of a thing.</summary>
	public void Reset(double tick, Transform3D sample, float scalar = 0f)
	{
		_count = 0;
		_playhead.Reset();
		Push(tick, sample, Godot.Time.GetTicksUsec(), scalar);
	}

	/// <summary>
	/// A stated transform that differs from the newest, on the tick the guest states now.
	/// A tick at or before the newest sample's is read skew and becomes the next tick.
	/// </summary>
	public void Push(double tick, Transform3D sample, double nowUsec, float scalar = 0f)
	{
		// THE CLOCK IS TOLD WHAT THE GUEST SAID, NOT WHAT THIS TIMELINE NEEDED IT TO BE. The
		// bump below is a local ordering repair — two samples read either side of one tick
		// must not collide in the ring — and the tick it invents is one the guest never
		// reached. Fed to the fit, that over-large tick makes `now - tick*period` too small,
		// the minimum rule keeps it, and the origin is dragged permanently early for every
		// entity sharing it. Only the stated value carries information about the clock.
		double stated = tick;
		if (_count != 0 && tick <= _tick[_head])
		{
			tick = _tick[_head] + 1.0;
		}

		// As Engine_TickScalar: advance by every interval this sample covers, not by one.
		int steps = _count == 0
			? 1
			: (int)System.Math.Max(1.0, System.Math.Round(stated - _tick[_head]));
		_playhead.Swap(nowUsec, steps);

		_head = _count == 0 ? 0 : (_head + 1) % Capacity;
		_tick[_head] = tick;
		_sample[_head] = sample;
		_scalar[_head] = scalar;
		_count = Mathf.Min(_count + 1, Capacity);
		Newest = sample;
		NewestScalar = scalar;

		// THE FIT IS SHARED. Every stated thing rides one guest counter, so it is fitted once
		// for the world — see Engine_TickClock. A per-entity origin made each thing its own
		// approximation of the same clock, and a body and the bones inside it drawn from two
		// approximations do not agree.
		// A half-tick stamp (a camera or pose from the guest's alpha-0.5 HF iteration) belongs
		// to the game tick whose iteration wrote it: the tick above it.
		Engine_TickClock.Observe((uint)System.Math.Ceiling(stated), nowUsec);
	}

	/// <summary>The transform to draw now.</summary>
	public Transform3D Sample(double nowUsec)
	{
		if (!Locate(nowUsec, out int older, out int younger, out float alpha))
		{
			return Newest;
		}

		return _sample[older].InterpolateWith(_sample[younger], alpha);
	}

	/// <summary>
	/// The companion value to draw now, at the same point on the timeline the transform is
	/// drawn at — one clock for both.
	/// </summary>
	public float SampleScalar(double nowUsec)
	{
		if (!Locate(nowUsec, out int older, out int younger, out float alpha))
		{
			return NewestScalar;
		}

		return Mathf.Lerp(_scalar[older], _scalar[younger], alpha);
	}

	/// <summary>
	/// The two samples the guest last stated and how far between them this frame falls. False
	/// when the timeline cannot answer yet — fewer than two samples, or nothing stated — and the
	/// caller holds the newest value.
	/// </summary>
	private bool Locate(double nowUsec, out int older, out int younger, out float alpha)
	{
		older = _head;
		younger = _head;
		alpha = 0f;
		if (_count < 2 || !_playhead.Ready)
		{
			return false;
		}

		// See Engine_PairPlayhead: the fraction is anchored to the swap, not to a host draw
		// tick, which drifts against the guest's counter.
		older = At(1);
		younger = _head;
		alpha = (float)_playhead.Alpha(nowUsec);
		return true;
	}
}
