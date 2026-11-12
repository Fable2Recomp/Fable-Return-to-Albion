namespace Refii.Core;

/// <summary>
/// Paces host-to-guest simulation step grants at a strict 1.0x real-time ceiling
/// (<see cref="StepsPerSecond"/> = 30.0 HF steps/s, 15.0 game ticks/s), independent of host FPS.
/// </summary>
/// <remarks>
/// Two invariants prevent post-hitch high-FPS character movement speed bursts:
/// <list type="number">
/// <item><b>No multi-step catch-up banking</b>: matching Xbox 360 <c>sub_82276D10</c> (<c>0x82276FA4</c>),
/// a slow frame or hitch advances at most one step and discards unrun backlog instead of banking
/// multiple steps that would subsequently drain at one step per rendered frame (e.g. 120–240 Hz).</item>
/// <item><b>Monotonic wall-clock minimum interval</b>: consecutive step grants are separated by at
/// least <see cref="MinGrantIntervalSeconds"/> of monotonic wall time, preserving exact 30.0 Hz
/// cadence across all display refresh rates (30, 45, 50, 59, 60, 75, 120, 144, 165, 240+ Hz) while
/// making sub-frame multi-step bursts impossible.</item>
/// </list>
/// </remarks>
public struct Engine_GuestPacer
{
	/// <summary>The guest's high-frequency step rate (bone poses at 30 Hz, game ticks at 15 Hz).</summary>
	public const double StepsPerSecond = 30.0;

	/// <summary>Seconds per high-frequency guest step (1/30 s = 33.333 ms).</summary>
	public const double Period = 1.0 / StepsPerSecond;

	/// <summary>
	/// Frame delta threshold above which a frame is treated as a hitch rather than normal VSync jitter,
	/// discarding any post-step remainder so recovery resumes cleanly from phase 0.
	/// </summary>
	public const double HitchDeltaSeconds = 1.5 * Period;

	/// <summary>
	/// Maximum post-grant remainder preserved for 30 Hz VSync jitter (~38.3 ms = 1.15 * Period).
	/// At 30 Hz VSync, a single slightly-short frame (e.g. 32.1 ms) shifts steady-state post-grant
	/// remainder to ~1.0 * Period +/- VSync jitter; anything beyond 1.15 * Period indicates real
	/// sub-30-FPS lag whose backlog must be discarded rather than replayed at high FPS.
	/// </summary>
	public const double MaxJitterRemainderSeconds = 1.15 * Period;

	/// <summary>
	/// Minimum monotonic wall-clock separation between two consecutive step grants (16.0 ms = 0.48 * Period).
	/// Permits legitimate alternating-frame cadence at 31–59 FPS (16.9–32.2 ms) while blocking high-FPS
	/// (120–240+ FPS, 4–8 ms) back-to-back step bursts.
	/// </summary>
	public const double MinGrantIntervalSeconds = 0.48 * Period;

	private double _accumulated;
	private double _lastGrantWallSeconds;
	private bool _hasLastGrant;

	/// <summary>Normalized sub-step draw phase in <c>[0, 1)</c> for <see cref="Engine_TickClock.PublishHostTicks"/>.</summary>
	public readonly double Phase => System.Math.Clamp(_accumulated / Period, 0.0, 0.999999);

	/// <summary>Current accumulated seconds waiting toward the next step.</summary>
	public readonly double AccumulatedSeconds => _accumulated;

	/// <summary>Clears all state, including the monotonic wall-clock anchor.</summary>
	public void Reset()
	{
		_accumulated = 0.0;
		_lastGrantWallSeconds = 0.0;
		_hasLastGrant = false;
	}

	/// <summary>Clears buffered delta while preserving the last grant's monotonic wall timestamp (e.g. during pause/load holds).</summary>
	public void ClearAccumulated()
	{
		_accumulated = 0.0;
	}

	/// <summary>
	/// Advances the pacer by one host frame of <paramref name="delta"/> seconds at monotonic wall time
	/// <paramref name="wallNowSeconds"/>, returning <c>0</c> or <c>1</c> step to grant.
	/// </summary>
	/// <param name="delta">Host frame delta in seconds.</param>
	/// <param name="wallNowSeconds">Monotonic wall-clock time in seconds (e.g. from <see cref="System.Diagnostics.Stopwatch"/>).</param>
	/// <param name="withholdGrant">True when closed-loop guest lead backpressure withholds a grant this frame.</param>
	public int Advance(double delta, double wallNowSeconds, bool withholdGrant = false)
	{
		if (!(delta > 0.0))
		{
			return 0;
		}

		_accumulated += delta;

		if (withholdGrant)
		{
			// Hold just short of the period boundary rather than zeroing so the draw phase never jumps backwards.
			_accumulated = System.Math.Min(_accumulated, Period * 0.999);
			return 0;
		}

		if (_accumulated < Period)
		{
			return 0;
		}

		if (_hasLastGrant && wallNowSeconds >= _lastGrantWallSeconds &&
			(wallNowSeconds - _lastGrantWallSeconds) < MinGrantIntervalSeconds)
		{
			// Prevent back-to-back high-FPS frames from draining a near-boundary remainder faster than real time.
			_accumulated = System.Math.Min(_accumulated, Period * 0.999);
			return 0;
		}

		_accumulated -= Period;

		// Discard multi-step backlog or post-hitch remainder instead of banking catch-up bursts,
		// while preserving ~1.0 * Period of phase cushion for +/-5 ms 30 Hz VSync presentation jitter.
		if (delta >= HitchDeltaSeconds || _accumulated > MaxJitterRemainderSeconds)
		{
			_accumulated = 0.0;
		}

		_lastGrantWallSeconds = wallNowSeconds;
		_hasLastGrant = true;
		return 1;
	}
}
