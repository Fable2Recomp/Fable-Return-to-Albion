namespace Refii.Core;

/// <summary>
/// Where a frame falls between the two values the guest last stated, as a fraction — the one
/// piece of arithmetic every interpolating feed in the engine needs, written once.
/// </summary>
/// <remarks>
/// The fraction is anchored to the swap, not taken as `frac` of a free-running host position,
/// which drifts against the guest's counter and swaps at an arbitrary alpha. The guest's ticks
/// are unevenly spaced, so a carrying playhead lets alpha run past 1 (and below 0) rather than
/// holding. The anchor advances by whole periods and restarts only on a real discontinuity:
/// subtracting exactly one period subtracts exactly 1 from alpha, so the value drawn either
/// side of a swap is the same value, whether the next pair is early or late.
/// </remarks>
public sealed class Engine_PairPlayhead
{
	/// <summary>
	/// How far past the stated pair the draw point may run before it holds: half an interval,
	/// never a whole interval ahead of anything stated. Past this the guest has stopped.
	/// </summary>
	public const double Ceiling = 1.5;

	/// <summary>
	/// How far before the pair the draw point may sit. The anchor advances by exactly one
	/// period, so a swap at alpha `a` leaves alpha at `a - 1` — below 0 when the guest swaps
	/// early — and that value draws the same position as before: on the new pair `(B, C)`,
	/// `B - (1-a)*d` is `A + a*d`. Continuity for any `a` in [0, 1) needs the floor at -1.
	/// </summary>
	public const double Floor = -1.0;

	/// <summary>Weight given to each new interval measurement. A hiccup must not rewrite it.</summary>
	private const double PeriodTrust = 0.10;

	/// <summary>How fast the anchor re-centres when the guest's real rate differs from the estimate.</summary>
	private const double AnchorTrust = 0.05;

	/// <summary>
	/// The guest's tick period, measured once for the world.
	///
	/// The period is a property of the guest's clock, the same for every subject; per-subject
	/// estimates drift apart in rate. The anchor is a property of each subject's data — an
	/// entity latches only when its transform changes — so it is never shared. An instance
	/// built with `sharedPeriod` reads this and never writes it; the world's own playhead
	/// (`periodOwner`) measures it.
	/// </summary>
	public static double SharedPeriodUsec { get; private set; }

	private readonly double _nominalUsec;
	private readonly bool _sharedPeriod;
	private readonly bool _periodOwner;
	private double _anchorUsec;
	private double _observedUsec;
	private double _ownPeriodUsec;

	/// <remarks>
	/// Exactly one instance (the period owner) writes the shared period. Clip-channel and pose
	/// playheads are neither readers nor the owner: their intervals (a clip span, a half-tick
	/// pose interval) are not the game tick and stay private.
	/// </remarks>
	private double _periodUsec
	{
		get => _sharedPeriod ? (SharedPeriodUsec > 0.0 ? SharedPeriodUsec : _nominalUsec) : _ownPeriodUsec;
		set
		{
			if (_sharedPeriod)
			{
				return;
			}

			_ownPeriodUsec = value;
			if (_periodOwner)
			{
				SharedPeriodUsec = value;
			}
		}
	}

	private readonly double _ceiling;
	private readonly double _floor;

	/// <param name="carry">
	/// Whether the draw point may run outside the stated pair when the next one is late or early.
	/// True only for a transform: a position or orientation extrapolates plausibly, and carrying
	/// stops the world freezing on a late pair. Never for a blend weight (past 1 over-weights its
	/// channel) or a bone pose (extrapolation invents limb positions).
	/// </param>
	/// <param name="sharedPeriod">
	/// Take the interval from <see cref="SharedPeriodUsec"/> rather than measuring one. True for
	/// every per-subject playhead: each holds its own anchor on the world's period.
	/// </param>
	/// <param name="periodOwner">
	/// This instance measures the world's tick period for every reader. True for exactly one:
	/// the world playhead, which sees every guest tick. A playhead that is neither this nor a
	/// reader keeps its interval entirely to itself.
	/// </param>
	public Engine_PairPlayhead(double nominalUsec, bool carry = false, bool sharedPeriod = false,
		bool periodOwner = false)
	{
		_nominalUsec = nominalUsec;
		_sharedPeriod = sharedPeriod;
		_periodOwner = periodOwner;
		_ownPeriodUsec = nominalUsec;
		_ceiling = carry ? Ceiling : 1.0;
		_floor = carry ? Floor : 0.0;
	}

	/// <summary>
	/// The largest alpha this instance can return — 1 without carry, the ceiling with it. The
	/// value a caller must compare against to ask "has the blend stopped moving?"; comparing
	/// against the constant instead leaves a clamped playhead permanently unsettled.
	/// </summary>
	public double TopAlpha => _ceiling;

	/// <summary>The interval between swaps as currently measured, in the nominal's units.</summary>
	public double PeriodRatio => _periodUsec / _nominalUsec;

	/// <summary>Whether a pair has ever been stated; before that there is nothing to blend.</summary>
	public bool Ready => _anchorUsec > 0.0;

	/// <summary>Draw points taken past the stated pair, and those that hit the ceiling and held.</summary>
	public long Extrapolated { get; private set; }

	public long Held { get; private set; }

	/// <summary>
	/// Swaps that took the discontinuity path and restarted the anchor at the arrival instead of
	/// advancing it by whole periods. Each snaps alpha to 0, so the drawn value jumps by
	/// `(1 - alpha) * |pair|`.
	/// </summary>
	public long Restarts { get; private set; }

	/// <summary>The alpha each swap happened at. At 1.0 a swap is exactly continuous.</summary>
	public double LastSwapAlpha { get; private set; }

	/// <summary>
	/// Draw points clamped at the floor — the swap landed the anchor further ahead than the time
	/// that had passed, so the draw point holds until the clock catches up. The mirror image of
	/// <see cref="Held"/>.
	/// </summary>
	public long Floored { get; private set; }

	/// <summary>The furthest below the pair a draw point has been asked to sit, before clamping.</summary>
	public double WorstEarly { get; private set; }

	/// <summary>
	/// The guest has stated a new value and how much time the pair spans. Use this wherever the
	/// width is known rather than inferred.
	/// </summary>
	/// <remarks>
	/// A smoothed estimate cannot represent a bimodal interval: poses publish every HF step but
	/// are noticed on the walk, so a pair spans half a tick or a whole tick, alternating. The
	/// stamps carry the width; stating it is exact and needs no warm-up.
	/// </remarks>
	public void SwapStated(double nowUsec, double intervalUsec)
	{
		if (intervalUsec > _nominalUsec * 0.25 && intervalUsec < _nominalUsec * 8.0)
		{
			_periodUsec = intervalUsec;
		}

		// Anchored at the arrival, not crept forward. Only a carrying playhead needs the
		// creeping anchor; a clamped one at alpha 1 draws `newest`, which after the swap is
		// `previous` at alpha 0, so restarting is exactly continuous and tracks a width that
		// changes per pair.
		_observedUsec = nowUsec;
		_anchorUsec = nowUsec;
	}

	/// <summary>
	/// The guest has stated a new value. <paramref name="steps"/> is how many intervals it
	/// covers — more than one when the host did not observe every one of them; advancing the
	/// anchor by one period there would leave it permanently behind.
	/// </summary>
	public void Swap(double nowUsec, int steps = 1)
	{
		if (steps < 1)
		{
			steps = 1;
		}

		// Measured from the real arrival, not from the anchor: measuring the anchor against
		// itself feeds the estimate its own output.
		if (_observedUsec > 0.0 && nowUsec > _observedUsec)
		{
			double measured = (nowUsec - _observedUsec) / steps;
			if (measured > _nominalUsec * 0.25 && measured < _nominalUsec * 4.0)
			{
				_periodUsec += (measured - _periodUsec) * PeriodTrust;
			}
		}

		_observedUsec = nowUsec;

		if (_anchorUsec > 0.0 && _periodUsec > 0.0)
		{
			LastSwapAlpha = (nowUsec - _anchorUsec) / _periodUsec;
		}

		double anchored = _anchorUsec + (_periodUsec * steps);
		double drift = nowUsec - anchored;
		if (_anchorUsec <= 0.0 || drift < -_periodUsec || drift > _periodUsec * 2.0)
		{
			if (_anchorUsec > 0.0)
			{
				++Restarts;
			}

			// First pair, or a real discontinuity — a load, a teleport, the world coming up.
			// Unwinding that one period per swap would take a minute of visibly wrong motion.
			_anchorUsec = nowUsec;
			return;
		}

		_anchorUsec = anchored + (drift * AnchorTrust);
	}

	/// <summary>Nothing has been stated for a long time: the next pair starts clean.</summary>
	public void Reset()
	{
		_anchorUsec = 0.0;
		_observedUsec = 0.0;
		_ownPeriodUsec = _nominalUsec;
	}

	/// <summary>How far through the current pair this frame falls; 0 to <see cref="Ceiling"/>.</summary>
	public double Alpha(double nowUsec)
	{
		if (_anchorUsec <= 0.0 || _periodUsec <= 0.0)
		{
			return 1.0;  // nothing stated yet: the live value stands on its own
		}

		double alpha = (nowUsec - _anchorUsec) / _periodUsec;
		if (alpha <= 1.0)
		{
			if (alpha < 0.0 && alpha < WorstEarly)
			{
				WorstEarly = alpha;
			}

			if (alpha < _floor)
			{
				++Floored;
				return _floor;
			}

			return alpha;
		}

		++Extrapolated;
		if (alpha >= _ceiling)
		{
			++Held;
			return _ceiling;
		}

		return alpha;
	}
}
