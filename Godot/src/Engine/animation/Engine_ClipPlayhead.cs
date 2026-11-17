namespace Refii.Animations;

/// <summary>
/// One clip's playhead, run on Godot's clock: advanced every drawn frame by the frame's time at the
/// guest's own pace (Engine_DrawAdvance) and the step per tick the guest states, and steered toward
/// the guest's own position by a bounded change of speed. The guest says WHAT plays and how fast;
/// Godot says WHEN.
/// </summary>
/// <remarks>
/// THE GUEST'S POSITION IS A REFERENCE, NOT A SAMPLE. Each game tick the guest states the record's
/// pair — previous time (latched at the tick's start) and current — and draws
/// `lerp(prev, cur, alpha)` (the type-0 leaf of `sub_822DC980`). Evaluated at the world's draw
/// point that is where the clip should be. Sampling it directly put every read defect on screen:
/// a tick the walk missed, a read that caught the next step under this tick's stamp, a network
/// packet's jitter — each one a freeze for a tick and then a jump (measured on the hero
/// 2026-10-14: four to seven frames with no bone motion, then 0.15-0.2 m of it in one frame).
/// Here such a defect is an error the playhead closes over a few hundred milliseconds at a speed
/// within <see cref="MaxCorrection"/> of the stated rate.
///
/// Only a re-time snaps: an error past <see cref="RestartSeconds"/> is a new position the guest
/// put the clip at (`sub_82200F60` SetTime), not drift; so is a stated cut (`sub_822B4970` writes
/// previous and current the same) and the first sight of a clip.
/// </remarks>
public sealed class Engine_ClipPlayhead
{
	/// <summary>Game ticks per second (`dbl_83319660`): the stated step is per tick.</summary>
	private const double TicksPerSecond = Refii.Core.Engine_TickClock.GameTicksPerSecond;

	/// <summary>An error this large is a re-time, taken at once.</summary>
	public const double RestartSeconds = 4.0 / 15.0;

	/// <summary>How fast an error is closed: the fraction left after one second is e^-Gain.</summary>
	private const double Gain = 4.0;

	/// <summary>
	/// The most the drawn speed may differ from the stated rate while closing an error, as a
	/// fraction of the rate (and of one clip second per second when the rate is near zero, so a
	/// paused clip still settles onto the guest's frame).
	/// </summary>
	public const double MaxCorrection = 0.08;

	/// <summary>How far past its stated pair the reference runs on before it holds, in ticks.</summary>
	private const double MaxLeadTicks = 2.5;

	/// <summary>The drawn clip time, unwrapped: it only ever moves by the frame's advance or a re-time.</summary>
	public double Time { get; private set; }

	/// <summary>The stated rate, clip seconds per real second.</summary>
	public double Rate { get; private set; }

	/// <summary>The last error, reference minus drawn, in clip seconds; for the debug channel.</summary>
	public double Error { get; private set; }

	public bool Started { get; private set; }

	/// <summary>The game tick the newest pair is stamped with, from its own continuity; for the debug channel.</summary>
	public double Stamp => _stamp;

	private double _previous;
	private double _statedPrevious;
	private double _current;
	private double _stamp;
	private double _length;
	private bool _loop;

	/// <summary>Re-times taken (first sights and cuts excluded); run total, for the debug channel.</summary>
	public static long Restarts;

	/// <summary>Frames on which the correction hit <see cref="MaxCorrection"/>; run total.</summary>
	public static long Saturated;

	/// <summary>
	/// The guest's pair as read: previous and current clip time, on a frame whose game tick is
	/// <paramref name="tick"/>. Called on every read; the same pair again is not a new tick.
	/// <paramref name="cut"/> takes the position at once.
	/// </summary>
	/// <remarks>
	/// THE PAIR STATES ITS OWN TICK. `sub_82200F60` shifts the current time into the previous slot
	/// every tick, so a pair whose previous time is the last read's current time is exactly one
	/// tick on — whatever tick the frame read. The frame's counter is read at the top of the frame
	/// and the leaves later, and a guest behind real time is never idle, so a late read often
	/// carries the next step's pair under this frame's tick: a whole tick of reference error each
	/// time (measured in res://debug/anim_timing_lab, `slow-guest-65pct`). Counted from the pair's
	/// own continuity, the stamp is kept within [tick, tick + 1] — a read can run at most one
	/// step ahead of its frame, and a clip that paused re-anchors on the frame's tick.
	/// </remarks>
	public void Observe(double previous, double current, uint tick, double length, bool loop, bool cut,
		double drawTick)
	{
		if (Started && previous == _statedPrevious && current == _current)
		{
			return;
		}

		_length = length;
		_loop = loop && length > 0.0;
		double rawPrevious = previous;
		double stamp = tick;
		double span = 1.0;
		if (Started && cut && previous == current && tick > _stamp)
		{
			if (System.Math.Abs(Rate) > 1e-3)
			{
				double elapsed = System.Math.Clamp(tick - _stamp, 1.0, 4.0);
				double inferred = Gap(current - _current) / elapsed;
				if (inferred >= -RestartSeconds && inferred < RestartSeconds)
				{
					cut = false;
					previous = _current;
					span = elapsed;
				}
			}
		}
		else if (Started && !cut && System.Math.Abs(Gap(previous - _current)) < 1e-4)
		{
			if (tick > _stamp + 1.0)
			{
				span = System.Math.Clamp(tick - _stamp, 1.0, 4.0);
			}
			else
			{
				stamp = System.Math.Clamp(_stamp + 1.0, tick, tick + 1.0);
			}
		}

		double step;
		if ((!Started || System.Math.Abs(_current) > 1e-4) && !cut && rawPrevious == 0.0 && current > 1.5 / TicksPerSecond)
		{
			// A newly spawned locomotion footstep leaf (`WalkFootstep` / `WalkFootstepMirror`) or
			// phase-offset clip enters mid-stride (`current = 0.177 .. 0.256s` or higher) while its
			// constructor-initialized `previous` slot (`node + 28`) is still `0.0`. Treating
			// `current - 0.0` as the per-tick step sets `Rate = 3x..4x` and `_previous = 0.0`,
			// anchoring `Time` at `~0.0s` (`200ms` behind the guest) on `alpha ~ 0` and forcing
			// `+25%` saturated catch-up across the entire step. Seed a normal 1x tick step so
			// `Reference(drawTick)` starts in exact sub-tick phase lockstep from frame 1.
			step = 1.0 / TicksPerSecond;
			cut = true;
		}
		else
		{
			step = Gap(current - previous) / span;
			if (!(step >= -RestartSeconds) || step >= RestartSeconds)
			{
				step = 0.0;
				cut = true;
			}
		}

		_statedPrevious = rawPrevious;
		_previous = current - step;
		_current = current;
		_stamp = stamp;
		Rate = step * TicksPerSecond;
		if (!Started || cut)
		{
			Time = Reference(drawTick);
			Error = 0.0;
			Started = true;
		}
	}

	/// <summary>Moves the playhead by one drawn frame of <paramref name="seconds"/>.</summary>
	public double Advance(double seconds, double drawTick)
	{
		// A stated pause holds the clip where it is: no advance, no steering — the reference is
		// the pair the guest stopped on and the next pair re-anchors on release.
		if (!Started || Refii.Core.Engine_StatedPause.Latched)
		{
			return Time;
		}

		Time += Rate / TicksPerSecond * Refii.Core.Engine_DrawAdvance.Ticks(seconds, Refii.Entities.Engine_DrawnFrame.NowUsec, drawTick);
		double error = Gap(Reference(drawTick) - Time);
		Error = error;
		if (System.Math.Abs(error) > RestartSeconds)
		{
			++Restarts;
			Time += error;
			Error = 0.0;
			return Time;
		}

		double pull = error * (1.0 - System.Math.Exp(-Gain * seconds));
		double limit = MaxCorrection * System.Math.Max(System.Math.Abs(Rate), 1.0) * seconds;
		if (System.Math.Abs(pull) > limit)
		{
			pull = System.Math.Sign(pull) * limit;
			++Saturated;
		}

		Time += pull;
		return Time;
	}

	/// <summary>Moves the playhead by one drawn frame at the last stated rate, unsteered.</summary>
	public double Freewheel(double seconds, double drawTick)
	{
		if (Started && !Refii.Core.Engine_StatedPause.Latched)
		{
			Time += Rate / TicksPerSecond * Refii.Core.Engine_DrawAdvance.Ticks(seconds, Refii.Entities.Engine_DrawnFrame.NowUsec, drawTick);
			if (!_loop && _length > 0.0 && Time >= _length)
			{
				Time = _length;
				Rate = 0.0;
			}
		}

		return Time;
	}

	/// <summary>
	/// Observes a prop clip (doors, chests, clocks, signs, cogs) so the Host drives its playhead
	/// autonomously via <see cref="Freewheel"/> without 15 Hz phase-error micro-corrections.
	/// One-shot prop transitions (<c>!loop</c>) seed a <c>1.0x</c> rate as soon as triggered near
	/// the start of the clip and run smoothly to completion on the Host clock. A looping clip runs
	/// at the rate its stated times move, and a constant pair holds it.
	/// </summary>
	public void ObserveProp(double previous, double current, uint tick, double length, bool loop, double drawTick)
	{
		if (Started && previous == _statedPrevious && current == _current)
		{
			return;
		}

		_length = length;
		_loop = loop && length > 0.0;
		bool hardReset = !Started || (!loop && Started && current < Time - 0.15 && current < length * 0.5);
		double span = Started && tick > _stamp ? System.Math.Clamp(tick - _stamp, 1.0, 4.0) : 1.0;
		double delta = previous != current
			? Gap(current - previous)
			: (Started && tick > _stamp ? Gap(current - _current) / span : 0.0);

		_statedPrevious = previous;
		_current = current;
		_stamp = tick;

		if (!loop)
		{
			// A one-shot prop animation (e.g. door Open/Close, chest Open) runs at 1.0x on the Host
			// until it reaches the end of the clip; a door spawned already at the end of its clip
			// (current >= length - 0.05) stays settled at rest.
			bool spawnedAtEnd = !Started && length > 0.0 && current >= length - 0.05 && previous >= length - 0.05;
			if (hardReset)
			{
				Time = spawnedAtEnd ? current : System.Math.Min(previous, current);
				Rate = spawnedAtEnd ? 0.0 : 1.0;
				Error = 0.0;
				Started = true;
			}
			else if (length > 0.0 && Time >= length)
			{
				Rate = 0.0;
			}
			else
			{
				Rate = 1.0;
			}
			return;
		}

		// The loop byte says nothing about motion: `CAnimationControlSimple`'s base ctor sets it for
		// every control (`sub_82283BB8`: `li r8, 1` / `stb r8, 0x2C(r31)` at 0x82283BE8/0x82283C0C)
		// and no slot of the control advances its time. The time moves only when the owner calls
		// SetTime (vf5 `sub_82200F60`: cur -> prev, then cur = t, 0x82200F98/0x82200F9C) or cuts
		// (vf7 `sub_822B4970`: prev = cur = t, 0x822B499C/0x822B49A4). So the rate is the stated
		// step — the pair's own, else the step between cuts on successive ticks — and a cut with
		// no step is a hold.
		if (delta > -RestartSeconds && delta < RestartSeconds)
		{
			Rate = delta * TicksPerSecond;
		}
		else if (!Started)
		{
			Rate = 0.0;
		}

		if (!Started)
		{
			_previous = current - (Rate / TicksPerSecond);
			Time = Reference(drawTick);
			Error = 0.0;
			Started = true;
		}
	}

	/// <summary>
	/// Where the guest draws the clip at <paramref name="drawTick"/>: its pair interpolated as
	/// `lerp(prev, cur, alpha)` with alpha 0 at the tick before the stamp and 1 at the stamp, run
	/// on along the step past it and held after <see cref="MaxLeadTicks"/>.
	/// </summary>
	private double Reference(double drawTick)
	{
		if (double.IsNaN(drawTick))
		{
			return _current;
		}

		double alpha = System.Math.Clamp(drawTick - (_stamp - 1.0), -1.0, MaxLeadTicks);
		return _previous + ((_current - _previous) * alpha);
	}

	/// <summary>A difference of clip times, the short way round a loop.</summary>
	private double Gap(double gap)
	{
		if (!_loop)
		{
			return gap;
		}

		gap %= _length;
		if (gap > _length * 0.5)
		{
			gap -= _length;
		}
		else if (gap < -_length * 0.5)
		{
			gap += _length;
		}

		return gap;
	}
}
