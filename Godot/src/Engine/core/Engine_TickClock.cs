namespace Refii.Core;

/// <summary>
/// The guest's simulation clock, fitted once for the whole world.
/// </summary>
/// <remarks>
/// ONE CLOCK, OR THINGS DRAWN FROM IT DRIFT APART. `dword_83319650` is a single global counter
/// incremented at the tail of the world update, so every stated thing — a body's placement, the
/// camera, a character's bone pose — belongs to the same tick. Fitting a separate origin per
/// entity gives each its own approximation of that one clock: they converge, but they converge
/// at different rates from different sample counts, and a rig fitted from its own handful of
/// samples is drawn microseconds away from the body it is attached to. Sharing the fit removes
/// the disagreement by construction, and a fit fed by every entity in the world converges far
/// harder than one fed by a single villager.
///
/// The fit itself: a sample for tick T seen at `now` cannot have been stated later than `now`,
/// so `now - T*period` is an upper bound on the origin and the smallest one seen is the best
/// estimate. It creeps back at 5% when every candidate is larger, so a guest running slower
/// than 1/15 s a tick does not leave the draw point permanently starved.
/// </remarks>
public static class Engine_TickClock
{
	/// <summary>The guest's tick period: a fixed 1/15 s.</summary>
	public const double TickUsec = 1e6 / 15.0;

	/// <summary>
	/// How far behind the newest sample the world is drawn, in ticks. A sample lands on the
	/// host up to a frame after its tick, so under one tick plus a frame starves and holds.
	/// </summary>
	public const double DrawLatencyTicks = 1.5;

	private static double _originUsec = double.NaN;
	private static uint _highestTick;

	/// <summary>True once a sample has been seen and the draw point can be asked for.</summary>
	public static bool Ready => !double.IsNaN(_originUsec);

	/// <summary>How many samples have refined the fit — reported, not used.</summary>
	public static long Samples { get; private set; }

	/// <summary>How many times the guest's counter went backwards and the fit restarted.</summary>
	public static int Restarts { get; private set; }

	/// <summary>Folds one stated sample into the fit.</summary>
	public static void Observe(uint tick, double nowUsec)
	{
		// BACKWARDS MEANS A NEW COUNTER, NOT A LATE SAMPLE. The guest's own contract is that
		// this value need not only grow — a level load may restart it — and the minimum rule
		// cannot recover from a poisoned origin on its own, because nothing later is smaller.
		if (tick + 64u < _highestTick)
		{
			_originUsec = double.NaN;
			_highestTick = 0u;
			++Restarts;
		}

		if (tick > _highestTick)
		{
			_highestTick = tick;
		}

		++Samples;
		double candidate = nowUsec - (tick * TickUsec);
		_originUsec = double.IsNaN(_originUsec) || candidate < _originUsec
			? candidate
			: _originUsec + ((candidate - _originUsec) * 0.05);
	}

	/// <summary>
	/// The point on the guest's timeline a frame at <paramref name="nowUsec"/> draws, in ticks
	/// and fractions of one. NaN until the fit has a sample.
	/// </summary>
	public static double DrawTick(double nowUsec) =>
		double.IsNaN(_originUsec)
			? double.NaN
			: ((nowUsec - _originUsec) / TickUsec) - DrawLatencyTicks;
}
