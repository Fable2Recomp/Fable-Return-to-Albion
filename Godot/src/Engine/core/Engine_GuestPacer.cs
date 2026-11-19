namespace Refii.Core;

/// <summary>
/// Paces host-to-guest simulation step grants at an exact 1.0x real-time fixed-step cadence
/// (<see cref="StepsPerSecond"/> = 30.0 HF steps/s, 15.0 game ticks/s), independent of host FPS.
/// </summary>
/// <remarks>
/// Uses an exact fixed-step accumulator (<see cref="Period"/> = 1/30 s) across all playable display
/// and frame rates (8–240+ FPS) while preventing post-hitch speed bursts:
/// <list type="number">
/// <item><b>Exact fractional carry</b>: normal frames carry <c>_accumulated -= steps * Period</c>
/// with zero artificial inter-grant gap or remainder clamp, guaranteeing exact 30.00 HF steps/s across
/// 15, 20, 24, 30 (+/- VSync jitter), 40, 45, 50, 59, 60, 75, 120, 144, and 240+ FPS.</item>
/// <item><b>True hitch discard</b>: a stall of <see cref="HitchThresholdSeconds"/> (200 ms = 6 steps)
/// or longer advances a single step and resets <c>_accumulated = 0</c>, so recovery frames never
/// fast-forward the character.</item>
/// </list>
/// </remarks>
public struct Engine_GuestPacer
{
	/// <summary>The guest's high-frequency step rate (bone poses at 30 Hz, game ticks at 15 Hz).</summary>
	public const double StepsPerSecond = 30.0;

	/// <summary>Seconds per high-frequency guest step (1/30 s = 33.333 ms).</summary>
	public const double Period = 1.0 / StepsPerSecond;

	/// <summary>
	/// Frame delta threshold (200 ms = 6 steps) above which a frame is treated as an OS/GPU stall
	/// rather than low-FPS play, granting a single step and zeroing the accumulator so no speed burst follows.
	/// </summary>
	public const double HitchThresholdSeconds = 6.0 * Period;

	/// <summary>Backwards-compatible alias for <see cref="HitchThresholdSeconds"/>.</summary>
	public const double HitchDeltaSeconds = HitchThresholdSeconds;

	/// <summary>
	/// Maximum steps granted in a single normal host frame (4 HF steps = 133.3 ms, maintaining 1.0x
	/// simulation speed down to 7.5 FPS while bounding worst-case per-frame work).
	/// </summary>
	public const int MaxStepsPerFrame = 4;

	/// <summary>
	/// Maximum unconsumed 30 Hz high-frequency steps queued or in flight natively (4 HF steps = 2.0
	/// game ticks, matching native <c>kMaxLeadSteps = 4</c> in <c>rex_bridge_host.cpp</c>) before
	/// closed-loop backpressure withholds further grants.
	/// </summary>
	public const ulong MaxUnconsumedHfSteps = 4UL;

	private double _accumulated;

	/// <summary>Normalized sub-step draw phase in <c>[0, 1)</c> for <see cref="Engine_TickClock.PublishHostTicks"/>.</summary>
	public readonly double Phase => System.Math.Clamp(_accumulated / Period, 0.0, 0.999999);

	/// <summary>Current accumulated seconds waiting toward the next step.</summary>
	public readonly double AccumulatedSeconds => _accumulated;

	/// <summary>Clears all accumulated time.</summary>
	public void Reset()
	{
		_accumulated = 0.0;
	}

	/// <summary>Clears buffered delta (e.g. during pause/load holds).</summary>
	public void ClearAccumulated()
	{
		_accumulated = 0.0;
	}

	/// <summary>
	/// Evaluates closed-loop grant backpressure directly from the native driven time-base's
	/// authoritative unconsumed step count (<c>pending + in_flight</c>), preventing cumulative
	/// counter drift or stale start-of-frame frame-counter latches from throttling sub-30 FPS play.
	/// </summary>
	/// <param name="unconsumedHfSteps">Current unconsumed HF steps queued or in flight in native runtime.</param>
	/// <param name="leadGameTicks">Unconsumed lead expressed in 15 Hz game ticks for diagnostics.</param>
	/// <param name="withholdGrant">True when unconsumed steps reach <see cref="MaxUnconsumedHfSteps"/>.</param>
	public static void EvaluateBackpressure(ulong unconsumedHfSteps, out double leadGameTicks, out bool withholdGrant)
	{
		leadGameTicks = unconsumedHfSteps * 0.5;
		withholdGrant = unconsumedHfSteps >= MaxUnconsumedHfSteps;
	}

	/// <summary>
	/// Advances the pacer by one host frame of <paramref name="delta"/> seconds using the live
	/// native unconsumed HF step depth to evaluate closed-loop backpressure cleanly.
	/// </summary>
	/// <param name="delta">Host frame delta in seconds.</param>
	/// <param name="unconsumedHfSteps">Live native unconsumed HF step depth (<c>pending + in_flight</c>).</param>
	/// <param name="leadGameTicks">Resulting lead in 15 Hz game ticks for diagnostics.</param>
	/// <param name="withholdGrant">Whether backpressure withheld grants on this frame.</param>
	/// <returns>Number of 30 Hz HF steps (<c>0..MaxStepsPerFrame</c>) to grant this frame.</returns>
	public int Advance(double delta, ulong unconsumedHfSteps, out double leadGameTicks, out bool withholdGrant)
	{
		EvaluateBackpressure(unconsumedHfSteps, out leadGameTicks, out withholdGrant);
		return Advance(delta, 0.0, withholdGrant);
	}

	/// <summary>
	/// Advances the pacer by one host frame of <paramref name="delta"/> seconds, returning the number
	/// of 30 Hz high-frequency guest steps (<c>0..MaxStepsPerFrame</c>) to grant this frame.
	/// </summary>
	/// <param name="delta">Host frame delta in seconds.</param>
	/// <param name="wallNowSeconds">Unused compatibility parameter (retained for call-site stability).</param>
	/// <param name="withholdGrant">True when closed-loop guest lead backpressure withholds grants this frame.</param>
	public int Advance(double delta, double wallNowSeconds = 0.0, bool withholdGrant = false)
	{
		_ = wallNowSeconds;
		if (!(delta > 0.0) || !double.IsFinite(delta))
		{
			return 0;
		}

		if (delta >= HitchThresholdSeconds)
		{
			if (withholdGrant)
			{
				_accumulated = System.Math.Min(_accumulated + Period, Period * 0.999);
				return 0;
			}

			_accumulated = 0.0;
			return 1;
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

		int steps = (int)(_accumulated / Period);
		if (steps > MaxStepsPerFrame)
		{
			_accumulated = 0.0;
			return MaxStepsPerFrame;
		}

		_accumulated -= steps * Period;
		return steps;
	}
}
