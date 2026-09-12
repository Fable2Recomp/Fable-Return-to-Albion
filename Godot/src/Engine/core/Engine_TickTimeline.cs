using Godot;

namespace Refii.Core;

/// <summary>
/// Draws a transform the guest states on its 15 Hz sim tick as a smooth path on the host clock.
/// </summary>
/// <remarks>
/// ON THE GUEST'S CLOCK, NOT ON ARRIVAL. The guest states placements and its camera on the sim
/// tick (`SimTick`, 0x83319650; `dbl_83319660 = 15`), and every stated change lands on a tick
/// change — measured 2026-09-05 on a walking villager (14.89 ticks/s, 105 of 119 frames
/// agreeing exactly, the rest one-frame read skew) and on the camera (its zero-movement frames
/// are precisely the frames the tick did not advance). Drawing each sample as it lands, or
/// blending "from where the thing is to the newest sample over one tick", re-times every
/// segment by when the host happened to see it: the drawn step swung 1.5-6.8 cm a frame on a
/// body the guest was moving at a constant 8.2 cm a tick. That was the stutter.
///
/// So each sample is stamped with the tick it was stated on, the host clock is fitted to the
/// tick timeline (samples only ever arrive LATE, so the earliest arrival per tick is the best
/// estimate and the fit creeps to follow drift), and a frame draws the transform at
/// `now - 1.5 ticks` by interpolating between the two samples that bracket it. Any frame length
/// then lands exactly where the guest's timeline says. Everything the guest states on the tick
/// — bodies, the camera — goes through the same timeline at the same latency, so nothing drifts
/// against anything else.
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
	public const double DrawLatencyTicks = Engine_TickClock.DrawLatencyTicks;

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

	private readonly uint[] _tick = new uint[Capacity];
	private readonly Transform3D[] _sample = new Transform3D[Capacity];

	/// <summary>
	/// A value the guest states on the SAME tick as the transform — the camera's field of
	/// view. It rides this timeline rather than one of its own so it cannot be drawn on a
	/// different clock from the transform it belongs to: a zoom applied raw while the dolly
	/// lagged 1.5 ticks behind is two motions for one movement.
	/// </summary>
	private readonly float[] _scalar = new float[Capacity];
	private int _count;
	private int _head;

	/// <summary>The newest stated transform.</summary>
	public Transform3D Newest { get; private set; }

	/// <summary>The newest stated companion value.</summary>
	public float NewestScalar { get; private set; }

	public bool Empty => _count == 0;

	public uint NewestTick => _count == 0 ? 0u : _tick[_head];

	private int At(int back) => (_head - back + Capacity) % Capacity;

	/// <summary>Starts over on a sample — a teleport, a load, the first sight of a thing.</summary>
	public void Reset(uint tick, Transform3D sample, float scalar = 0f)
	{
		_count = 0;
		Push(tick, sample, Godot.Time.GetTicksUsec(), scalar);
	}

	/// <summary>
	/// A stated transform that differs from the newest, on the tick the guest states now.
	/// A tick at or before the newest sample's is read skew and becomes the next tick.
	/// </summary>
	public void Push(uint tick, Transform3D sample, double nowUsec, float scalar = 0f)
	{
		// THE CLOCK IS TOLD WHAT THE GUEST SAID, NOT WHAT THIS TIMELINE NEEDED IT TO BE. The
		// bump below is a local ordering repair — two samples read either side of one tick
		// must not collide in the ring — and the tick it invents is one the guest never
		// reached. Fed to the fit, that over-large tick makes `now - tick*period` too small,
		// the minimum rule keeps it, and the origin is dragged permanently early for every
		// entity sharing it. Only the stated value carries information about the clock.
		uint stated = tick;
		if (_count != 0 && tick <= _tick[_head])
		{
			tick = _tick[_head] + 1;
		}

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
		Engine_TickClock.Observe(stated, nowUsec);
	}

	/// <summary>The transform to draw now: the timeline's value 1.5 ticks ago.</summary>
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
	/// The two samples bracketing the draw point and how far between them it falls. False when
	/// the timeline cannot answer — too few samples, no clock fit yet, or starved past the
	/// newest sample — and the caller holds the newest value.
	/// </summary>
	private bool Locate(double nowUsec, out int older, out int younger, out float alpha)
	{
		older = _head;
		younger = _head;
		alpha = 0f;
		if (_count < 2 || !Engine_TickClock.Ready)
		{
			return false;
		}

		double drawTick = Engine_TickClock.DrawTick(nowUsec);
		if (drawTick >= _tick[_head])
		{
			return false;  // starved or stopped: hold the newest
		}

		for (int back = 1; back < _count; ++back)
		{
			int olderAt = At(back);
			int youngerAt = At(back - 1);
			double olderTick = _tick[olderAt];
			double youngerTick = _tick[youngerAt];
			if (drawTick >= olderTick)
			{
				older = olderAt;
				younger = youngerAt;
				alpha = (float)Mathf.Clamp(
					(drawTick - olderTick) / Mathf.Max(youngerTick - olderTick, 1e-6), 0.0, 1.0);
				return true;
			}
		}

		older = At(_count - 1);
		younger = older;
		alpha = 0f;
		return true;
	}
}
