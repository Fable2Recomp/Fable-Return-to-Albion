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
	public const double TickUsec = 1e6 / 15.0;

	/// <summary>
	/// How far behind the newest sample the world is drawn, in ticks. A sample lands on the
	/// host up to a frame after its tick, so under one tick plus a frame starves and holds.
	/// </summary>
	public const double DrawLatencyTicks = 1.5;

	private readonly uint[] _tick = new uint[Capacity];
	private readonly Transform3D[] _sample = new Transform3D[Capacity];
	private int _count;
	private int _head;
	private double _originUsec = double.NaN;

	/// <summary>The newest stated transform.</summary>
	public Transform3D Newest { get; private set; }

	public bool Empty => _count == 0;

	public uint NewestTick => _count == 0 ? 0u : _tick[_head];

	private int At(int back) => (_head - back + Capacity) % Capacity;

	/// <summary>Starts over on a sample — a teleport, a load, the first sight of a thing.</summary>
	public void Reset(uint tick, Transform3D sample)
	{
		_count = 0;
		_originUsec = double.NaN;
		Push(tick, sample, Godot.Time.GetTicksUsec());
	}

	/// <summary>
	/// A stated transform that differs from the newest, on the tick the guest states now.
	/// A tick at or before the newest sample's is read skew and becomes the next tick.
	/// </summary>
	public void Push(uint tick, Transform3D sample, double nowUsec)
	{
		if (_count != 0 && tick <= _tick[_head])
		{
			tick = _tick[_head] + 1;
		}

		_head = _count == 0 ? 0 : (_head + 1) % Capacity;
		_tick[_head] = tick;
		_sample[_head] = sample;
		_count = Mathf.Min(_count + 1, Capacity);
		Newest = sample;

		// The clock fit: this sample cannot have been stated later than now, so the origin is
		// at most `now - tick*period`; take it when earlier, creep toward it otherwise so a
		// guest that slows down does not leave the draw point starving.
		double candidate = nowUsec - (tick * TickUsec);
		_originUsec = double.IsNaN(_originUsec) || candidate < _originUsec
			? candidate
			: _originUsec + ((candidate - _originUsec) * 0.05);
	}

	/// <summary>The transform to draw now: the timeline's value 1.5 ticks ago.</summary>
	public Transform3D Sample(double nowUsec)
	{
		if (_count < 2 || double.IsNaN(_originUsec))
		{
			return Newest;
		}

		double drawTick = ((nowUsec - _originUsec) / TickUsec) - DrawLatencyTicks;
		if (drawTick >= _tick[_head])
		{
			return Newest;  // starved or stopped: hold the newest
		}

		for (int back = 1; back < _count; ++back)
		{
			int older = At(back);
			int younger = At(back - 1);
			double olderTick = _tick[older];
			double youngerTick = _tick[younger];
			if (drawTick >= olderTick)
			{
				float alpha = (float)Mathf.Clamp((drawTick - olderTick) / Mathf.Max(youngerTick - olderTick, 1e-6), 0.0, 1.0);
				return _sample[older].InterpolateWith(_sample[younger], alpha);
			}
		}

		return _sample[At(_count - 1)];
	}
}
