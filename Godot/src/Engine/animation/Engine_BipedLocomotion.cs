using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Animations;

/// <summary>Which clip fills the blend's slow slot this frame.</summary>
public enum BipedPoseSlot
{
	Pose,
	CombatPose,
	CombatPoseGoofy,
	CombatPoseToNeutral,
	CombatPoseGoofyToNeutral,
}

/// <summary>
/// What the guest states about a character this frame. Everything here is READ from the
/// simulation, never inferred host-side.
///
/// <see cref="Velocity"/> and <see cref="Facing"/> are in GUEST space, where Z is up — the
/// space the bridge hands them over in, before `GameWorld.ToGodot` permutes them to Godot's
/// Y-up. They are taken unconverted on purpose: the script flattens them with `SetZ(0)`, and
/// converting first would make this class flatten the wrong axis and read every character's
/// vertical motion as forward speed.
/// </summary>
public readonly record struct BipedLocomotionInput(
	Vector3 Velocity,
	Vector3 Facing,
	float MaxSpeedInDirection,
	bool StanceAvailable,
	bool UseCombatPose,
	bool Goofy,
	bool Blocking,
	bool MovingViaAction);

/// <summary>
/// The NPC biped locomotion blend, translated from `EntityModeBipedMovement.lua`.
///
/// This is the whole of what the shipped script does, and it is a WEIGHTED BLEND OF THREE
/// CLIPS rather than a state machine: Pose, Walk and Run are mixed by continuous functions of
/// one scalar — the character's speed along its own facing, as a fraction of the speed its
/// mode currently permits. There are no discrete transitions to get wrong.
///
/// EVERY CONSTANT BELOW WAS READ OFF THE BYTECODE, not the decompiler's output. That mattered
/// once: the decompiler renders the run mix as `(P+W)/(P+W) + R`, which is `1 + R` and cannot
/// be what shipped. The instructions at pc 288-296 build `P+W`, then `P+W+R`, then divide —
/// `(P+W)/(P+W+R)`. A dropped parenthesis in a listing is invisible; in the blend it pins the
/// run weight on permanently.
///
/// THE TWO CLOCKS ARE NOT THE FRAME CLOCK. Walk and Run advance by `dt * tanh(3f)`, so their
/// cycles slow down as the character slows and stop dead when it does — that is what keeps the
/// feet from sliding. Walk is additionally scaled by `walkDuration / runDuration` so the two
/// stay in step through the blend. Pose alone advances in real time. Both cycle clocks start
/// at their clip's LEFT-FOOT-DOWN event so a character that begins moving is already in phase.
/// </summary>
public sealed class Engine_BipedLocomotion
{
	/// <summary>
	/// The event both cycle clips are phased from.
	///
	/// The anim table stores this as ONE string, verified against `fable2_anims.animation_toc`:
	/// there is no standalone "FOOT_PLANT" or "LEFT_FOOT_DOWN" entry in the table, only the
	/// combined form. So the match is on the whole string, which is also exactly what the
	/// shipped script passes to `GetEventTime`.
	/// </summary>
	public const string LeftFootDownEvent = "FOOT_PLANT LEFT_FOOT_DOWN";

	/// <summary>The script's `blend_val`: each update keeps 3 parts old weight to 1 part new.</summary>
	private const float WeightSmoothing = 3.0f;

	/// <summary>Nudge past the foot-plant event, so frame one is not exactly on it.</summary>
	private const float PhaseOffset = 0.005f;

	/// <summary>Below this the velocity direction is meaningless, so facing is assumed.</summary>
	private const float MinSpeedForDirection = 0.005f;

	private const float MinUsableMaxSpeed = 0.001f;

	private const float TransitionEndEpsilon = 0.001f;

	private readonly float _walkDuration;

	private readonly float _runDuration;

	private readonly float _combatPoseToNeutralDuration;

	private readonly float _combatPoseGoofyToNeutralDuration;

	private bool _useCombatPose;

	private bool _transitionToNeutral;

	private bool _transitionGoofyToNeutral;

	private float _transitionTime;

	public Engine_BipedLocomotion(
		float walkDuration,
		float runDuration,
		float walkLeftFootDown,
		float runLeftFootDown,
		float combatPoseToNeutralDuration = 0,
		float combatPoseGoofyToNeutralDuration = 0)
	{
		_walkDuration = walkDuration;
		_runDuration = runDuration;
		_combatPoseToNeutralDuration = combatPoseToNeutralDuration;
		_combatPoseGoofyToNeutralDuration = combatPoseGoofyToNeutralDuration;

		// A clip with no such event reports a negative time; the script floors it at zero
		// rather than treating it as "no phase", so a clip without foot events simply starts
		// at its beginning.
		WalkTime = Math.Max(walkLeftFootDown, 0) + PhaseOffset;
		RunTime = Math.Max(runLeftFootDown, 0) + PhaseOffset;

		PoseWeight = 1;
		WalkWeight = 0;
		RunWeight = 0;
	}

	/// <summary>Smoothed mix weights. Pose is provably positive, so the ratios never divide by zero.</summary>
	public float PoseWeight { get; private set; }

	public float WalkWeight { get; private set; }

	public float RunWeight { get; private set; }

	public float PoseTime { get; private set; }

	public float WalkTime { get; private set; }

	public float RunTime { get; private set; }

	/// <summary>Pose-against-Walk mix: `SlowBlend`.</summary>
	public float SlowAlphaA { get; private set; } = 1;

	public float SlowAlphaB { get; private set; }

	/// <summary>The slow pair against Run: `TotalBlend`.</summary>
	public float TotalAlphaA { get; private set; } = 1;

	public float TotalAlphaB { get; private set; }

	/// <summary>Which clip the slow slot should be playing.</summary>
	public BipedPoseSlot PoseSlot { get; private set; } = BipedPoseSlot.Pose;

	/// <summary>How far along the current pose-to-neutral transition is.</summary>
	public float TransitionTime => _transitionTime;

	/// <summary>The speed fraction the last update resolved. Diagnostic.</summary>
	public float SpeedFraction { get; private set; }

	public void Update(in BipedLocomotionInput input, float delta)
	{
		// Locomotion is a ground-plane question: both vectors are flattened before the
		// projection, exactly as the script does with SetZ(0).
		Vector3 velocity = Flatten(input.Velocity);
		Vector3 facing = Flatten(input.Facing);

		float speed = velocity.Length();
		float cosAngle = 1;
		if (speed > MinSpeedForDirection)
		{
			cosAngle = velocity.Dot(facing) / speed;
		}

		float forwardSpeed = speed * cosAngle;

		if (input.MaxSpeedInDirection <= MinUsableMaxSpeed)
		{
			// The mode permits no movement, so there is no fraction to form. The script skips
			// its whole body here, leaving the weights and clocks exactly as they were.
			return;
		}

		float fraction = Math.Clamp(forwardSpeed / input.MaxSpeedInDirection, 0, 1);
		SpeedFraction = fraction;

		bool useCombatPose = false;
		bool goofy = false;

		if (input.StanceAvailable)
		{
			useCombatPose = input.UseCombatPose;
			goofy = input.Goofy;

			// Leaving the combat pose plays a dedicated transition; entering it does not.
			if (!useCombatPose && _useCombatPose)
			{
				if (goofy)
				{
					_transitionGoofyToNeutral = true;
				}
				else
				{
					_transitionToNeutral = true;
				}

				_transitionTime = 0;
			}
		}

		float poseTarget = 1.0f / (1.0f + (fraction * 50.0f));
		float walkTarget = 3.5f * fraction *
			MathF.Exp(-10.0f * ((fraction - 0.2f) * (fraction - 0.2f)));
		float runTarget = fraction * fraction;

		if (input.MovingViaAction)
		{
			// An action is driving the character, so locomotion holds still underneath it.
			poseTarget = 1;
			walkTarget = 0;
			runTarget = 0;
		}

		PoseWeight = Smooth(PoseWeight, poseTarget);
		WalkWeight = Smooth(WalkWeight, walkTarget);
		RunWeight = Smooth(RunWeight, runTarget);

		float cycleRate = MathF.Tanh(3.0f * fraction);
		float walkScale = _runDuration > 0 ? _walkDuration / _runDuration : 1;

		PoseTime += delta;
		WalkTime += delta * cycleRate * walkScale;
		RunTime += delta * cycleRate;

		AdvanceTransition(delta);

		float slow = PoseWeight + WalkWeight;
		SlowAlphaA = PoseWeight / slow;
		SlowAlphaB = 1 - SlowAlphaA;

		TotalAlphaA = slow / (slow + RunWeight);
		TotalAlphaB = 1 - TotalAlphaA;

		// Blocking forces the combat pose even when stance did not ask for it, and it is
		// tested AFTER the weights are formed — it changes which clip fills the slot, not how
		// much of it is mixed in.
		if (!useCombatPose && input.Blocking)
		{
			useCombatPose = true;
		}

		PoseSlot = ResolveSlot(useCombatPose, goofy);
		_useCombatPose = useCombatPose;
	}

	private void AdvanceTransition(float delta)
	{
		if (!_transitionGoofyToNeutral && !_transitionToNeutral)
		{
			return;
		}

		_transitionTime += delta;

		if (_transitionGoofyToNeutral)
		{
			if (_transitionTime > _combatPoseGoofyToNeutralDuration - TransitionEndEpsilon)
			{
				_transitionGoofyToNeutral = false;
			}

			return;
		}

		if (_transitionTime > _combatPoseToNeutralDuration - TransitionEndEpsilon)
		{
			_transitionToNeutral = false;
		}
	}

	private BipedPoseSlot ResolveSlot(bool useCombatPose, bool goofy)
	{
		if (_transitionGoofyToNeutral)
		{
			return BipedPoseSlot.CombatPoseGoofyToNeutral;
		}

		if (_transitionToNeutral)
		{
			return BipedPoseSlot.CombatPoseToNeutral;
		}

		if (useCombatPose)
		{
			return goofy ? BipedPoseSlot.CombatPoseGoofy : BipedPoseSlot.CombatPose;
		}

		return BipedPoseSlot.Pose;
	}

	private static float Smooth(float current, float target) =>
		((current * WeightSmoothing) + target) / (1.0f + WeightSmoothing);

	/// <summary>Drops the GUEST vertical, which is Z. See <see cref="BipedLocomotionInput"/>.</summary>
	private static Vector3 Flatten(Vector3 value) => new(value.X, value.Y, 0);

	/// <summary>
	/// The time of a clip's left-foot-down event, or -1 when it has none.
	///
	/// Negative rather than zero on purpose: zero is a legitimate event time, and the caller
	/// floors it anyway.
	/// </summary>
	public static float LeftFootDownTime(AnimClip? clip)
	{
		if (clip is null)
		{
			return -1;
		}

		foreach (AnimEvent entry in clip.Events)
		{
			if (string.Equals(entry.Name, LeftFootDownEvent, StringComparison.Ordinal))
			{
				return entry.Time;
			}
		}

		return -1;
	}
}
