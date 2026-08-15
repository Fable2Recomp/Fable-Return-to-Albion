using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Animations;

/// <summary>
/// Everything the condition test reads, for one character on one frame.
///
/// ANGLES ARE RADIANS AND SIGNED, in [-pi, +pi], measured from the character's forward axis —
/// that is what `sub_827F27E8` produces, taking the sign from the vector's X. The state tables
/// store their bounds in DEGREES and the test converts them at comparison time, so nothing
/// here should be pre-converted.
///
/// THE TWO MAGNITUDES ARE NOT IN THE SAME UNITS, which is the easiest thing in this system to
/// get wrong. <see cref="ControlMagnitude"/> is compared RAW against `ControlArea`, while
/// <see cref="VelocityMagnitude"/> is divided by <see cref="MaxSpeed"/> before it meets
/// `VelocityArea`. That is why `Sprint` can hold a velocity band of 1.05..1.75 while every
/// control band stays inside 0..1.05.
///
/// The guest stores velocity, vertical velocity and max speed all pre-multiplied by the frame
/// delta. Every use of them is a RATIO of two of those, so the delta cancels and plain metres
/// per second may be passed here — the scaling is not part of the contract, only the ratio is.
/// </summary>
public readonly record struct LocomotionInput(
	float ControlMagnitude,
	float ControlAngle,
	float VelocityMagnitude,
	float VelocityAngle,
	float VerticalVelocity,
	float MaxSpeed,
	float Delta,
	float TargetFacingAngle,
	LocomotionStance Stance,
	LocomotionConfinement Confinement,
	LocomotionCombatState CombatState,
	bool IsChild,
	bool IsLooking);

/// <summary>Why a frame did or did not change state. Diagnostic only.</summary>
public enum LocomotionOutcome
{
	Held,
	Revalidated,
	Changed,
	NoCandidate,
}

/// <summary>
/// The hero locomotion state machine, run natively.
///
/// THE ONE RULE THAT ORGANISES EVERYTHING: a state is asked the same question by the same
/// function whether it is the current state or a candidate, and the ONLY difference is which
/// set of boxes it is asked against. `sub_822CC7A0` picks the `Termination*` fields when the
/// state is current and the entry fields when it is not — literally `def + (isCurrent ? 0xA0 :
/// 0x60)` and its siblings. So "may I stay?" and "may I be entered?" are one predicate over two
/// datasets, not two predicates, and that is why the reader defaults each `Termination*` field
/// to a copy of its entry counterpart.
///
/// SELECTION IS FIRST-MATCH-WINS, IN THE SCRIPT'S ORDER. There is no scoring and no tie-break
/// (`sub_822CCC80`). The successor list's order is therefore load-bearing data, not a set, and
/// it is preserved exactly as the script writes it. The only thing that can pass over a
/// candidate whose condition holds is `ValidTime`: the candidate must have held that condition
/// for longer than `ValidTime` seconds, tracked per candidate as a dwell timer.
///
/// WHAT THIS DOES NOT YET DO, stated rather than silently skipped: the footing-tolerance gate
/// and the foot-pose clip picker both need the candidate clip evaluated against the live
/// skeleton's heel bones, which is animation work this class does not own. Both are exposed as
/// hooks that default to "pass" / "first clip", so the machine runs without them and a caller
/// that can evaluate poses can supply them. A default that silently rejected would strand the
/// machine in its start state; one that silently accepted without saying so would be worse.
/// </summary>
public sealed class Engine_LocomotionMachine
{
	/// <summary>The state the guest's own machine starts in (`sub_827F7A78`).</summary>
	public const string StartState = "HeroLocomotionStateIdle";

	/// <summary>Below this a ValidTime counts as "no wait at all".</summary>
	private const float ImmediateValidTime = 1e-4f;

	/// <summary>How close to its end a footing transition is considered spent.</summary>
	private const float TransitionEndEpsilon = 0.0002f;

	private readonly Dictionary<string, float> _dwell = new(StringComparer.Ordinal);

	private readonly List<LocomotionState> _passing = new();

	public Engine_LocomotionMachine(LocomotionState? start = null)
	{
		Current = start ?? Engine_LocomotionStates.Find(StartState);
	}

	/// <summary>The state now held. Null only when the script failed to load.</summary>
	public LocomotionState? Current { get; private set; }

	/// <summary>Seconds spent in <see cref="Current"/>.</summary>
	public float TimeInState { get; private set; }

	public LocomotionOutcome LastOutcome { get; private set; }

	/// <summary>
	/// Whether the current state's clip has run out.
	///
	/// The caller sets this; it is `state+41` in the guest. An IDLE state stays valid for as
	/// long as this is false and is FORCED to change the moment it turns true, so a caller that
	/// never sets it leaves every one-shot idle running forever.
	/// </summary>
	public bool CurrentAnimationFinished { get; set; }

	/// <summary>Playback position and length of the current state's clip, for the footing gate.</summary>
	public float CurrentClipTime { get; set; }

	public float CurrentClipLength { get; set; }

	/// <summary>
	/// The footing-tolerance gate: does this candidate's frame-0 foot pose sit within
	/// `FeetMatchPoseTolerance` of the character's live feet?
	///
	/// Only consulted for MATCH-FOOTING candidates. Defaults to accepting.
	/// </summary>
	public Func<LocomotionState, bool>? FootingGate { get; set; }

	/// <summary>Fires when the machine leaves one state for another.</summary>
	public event Action<LocomotionState?, LocomotionState>? Entered;

	public void Reset(LocomotionState? start = null)
	{
		Current = start ?? Engine_LocomotionStates.Find(StartState);
		TimeInState = 0;
		CurrentAnimationFinished = false;
		_dwell.Clear();
	}

	/// <summary>Advances the machine one frame and returns the state now held.</summary>
	public LocomotionState? Update(in LocomotionInput input)
	{
		if (Current is null)
		{
			Current = Engine_LocomotionStates.Find(StartState);
			if (Current is null)
			{
				LastOutcome = LocomotionOutcome.NoCandidate;
				return null;
			}
		}

		TimeInState += input.Delta;

		LocomotionState current = Current;
		bool currentHolds = Test(current, input, isCurrent: true);

		// Dwell is maintained over EVERY successor, including ones the scan below will refuse
		// to consider this frame — `sub_822CCE10` runs its accumulate loop before the scan and
		// does not consult the scan's gates. A candidate blocked by a live transition therefore
		// keeps ripening, which is what lets it fire the instant the transition releases.
		_passing.Clear();
		foreach (string name in current.States)
		{
			LocomotionState? candidate = Engine_LocomotionStates.Find(name);
			if (candidate is null)
			{
				continue;
			}

			// The predicate is pure over this frame's input, so one evaluation serves both the
			// dwell update and the scan.
			bool holds = Test(candidate, input, isCurrent: false);
			_dwell[name] = holds ? Dwell(name) + input.Delta : 0;

			if (holds)
			{
				_passing.Add(candidate);
			}
		}

		foreach (LocomotionState candidate in _passing)
		{
			// A steady state cannot interrupt a transition that is still valid. Transitions
			// chain into transitions and only release once the transition itself fails.
			if (candidate.TransitionType == LocomotionTransition.Null &&
				current.TransitionType != LocomotionTransition.Null &&
				currentHolds)
			{
				continue;
			}

			if (string.Equals(candidate.Name, current.Name, StringComparison.Ordinal))
			{
				// The current state named itself and still passes: it is re-validated in place,
				// and the scan STOPS. It does not fall through to later candidates.
				LastOutcome = LocomotionOutcome.Revalidated;
				return Current;
			}

			if (candidate.ValidTime >= ImmediateValidTime &&
				Dwell(candidate.Name) <= candidate.ValidTime)
			{
				continue;
			}

			Enter(candidate);
			LastOutcome = LocomotionOutcome.Changed;
			return Current;
		}

		LastOutcome = currentHolds ? LocomotionOutcome.Held : LocomotionOutcome.NoCandidate;
		return Current;
	}

	private void Enter(LocomotionState next)
	{
		LocomotionState? previous = Current;
		Current = next;
		TimeInState = 0;
		CurrentAnimationFinished = false;
		CurrentClipTime = 0;

		// Entry resets the state's own dwell (`sub_827F4D10`), so re-entering it later has to
		// earn its ValidTime again.
		_dwell[next.Name] = 0;

		Entered?.Invoke(previous, next);
	}

	private float Dwell(string name) => _dwell.TryGetValue(name, out float value) ? value : 0;

	/// <summary>
	/// The condition test, in the guest's order (`sub_822CC7A0`).
	///
	/// The order is preserved because the early exits are not equivalent to a conjunction: the
	/// idle branch RETURNS TRUE before any area is looked at, so an idle state whose animation
	/// is still running holds regardless of where the stick is.
	/// </summary>
	public bool Test(LocomotionState state, in LocomotionInput input, bool isCurrent)
	{
		// Looking gates idle states only, and only those that opt out of it.
		if (state.IdleState && !state.AllowLooking && input.IsLooking)
		{
			return false;
		}

		if (isCurrent)
		{
			if (state.IdleState && !CurrentAnimationFinished)
			{
				return true;
			}

			if (CurrentAnimationFinished)
			{
				return false;
			}
		}

		if (input.IsChild && !state.AvailableToChild)
		{
			return false;
		}

		if (state.TransitionType == LocomotionTransition.MatchFooting && isCurrent &&
			CurrentClipLength > 0 &&
			CurrentClipTime >= CurrentClipLength - TransitionEndEpsilon)
		{
			return false;
		}

		// Every magnitude below is a fraction of the speed the character is currently allowed.
		float inverseMax = input.MaxSpeed > 0 ? 1.0f / input.MaxSpeed : 0;

		LocomotionRange vertical = isCurrent
			? state.TerminationVerticalVelocityRange
			: state.VerticalVelocityRange;

		// The ONLY non-strict comparison in the whole test.
		if (!vertical.ContainsInclusive(input.VerticalVelocity * inverseMax))
		{
			return false;
		}

		if (state.Confinement != LocomotionConfinement.None)
		{
			bool confined = state.ExactConfinement
				? input.Confinement == state.Confinement
				: (input.Confinement & state.Confinement) != 0;

			if (!confined)
			{
				return false;
			}
		}

		// Zero is the wildcard for combat, one is the wildcard for stance.
		if (state.CombatState != LocomotionCombatState.Null &&
			state.CombatState != input.CombatState)
		{
			return false;
		}

		if (state.Stance != LocomotionStance.Neutral && input.Stance != state.Stance)
		{
			return false;
		}

		LocomotionRange facing = isCurrent
			? state.TerminationTargetFacingAngles
			: state.TargetFacingAngles;

		if (!InFacing(facing, input.TargetFacingAngle, state.Symmetrical))
		{
			return false;
		}

		LocomotionArea control = isCurrent ? state.TerminationControlArea : state.ControlArea;
		if (!InArea(control, input.ControlMagnitude, input.ControlAngle, state.Symmetrical))
		{
			return false;
		}

		LocomotionArea velocity = isCurrent ? state.TerminationVelocityArea : state.VelocityArea;
		if (!InArea(velocity, input.VelocityMagnitude * inverseMax, input.VelocityAngle,
			state.Symmetrical))
		{
			return false;
		}

		// The footing gate applies to CANDIDATES only — the current state is never re-tested
		// against a pose it is already holding.
		if (isCurrent || state.AnimBlendType != LocomotionAnimBlend.MatchFooting)
		{
			return true;
		}

		return FootingGate?.Invoke(state) ?? true;
	}

	/// <summary>
	/// An area test, with `Symmetrical` handled the way the guest handles it.
	///
	/// SYMMETRICAL DOES NOT WIDEN THE BOX. It re-runs the identical test with the sample's
	/// ANGLE NEGATED (`fneg` at `0x822CCB20`), which is how one `StrafeLeft` table serves both
	/// sides. Widening to `(-max, +max)` would additionally admit everything between the two
	/// lobes — for a 45-degree strafe wedge that is the entire forward arc.
	/// </summary>
	private static bool InArea(
		LocomotionArea area, float magnitude, float angle, bool symmetrical) =>
		area.Contains(magnitude, angle) ||
		(symmetrical && area.Contains(magnitude, -angle));

	/// <summary>
	/// The facing test. Its mirror reflects the INTERVAL rather than the sample, testing
	/// `(-max, -min)` — not the same thing as negating the sample once the interval is
	/// asymmetric, which every turn state's is.
	/// </summary>
	private static bool InFacing(LocomotionRange range, float angle, bool symmetrical)
	{
		float min = Mathf.DegToRad(range.Min);
		float max = Mathf.DegToRad(range.Max);

		if (angle > min && angle < max)
		{
			return true;
		}

		return symmetrical && angle < -min && angle > -max;
	}
}
