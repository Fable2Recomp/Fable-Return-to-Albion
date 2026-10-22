using System;
using Godot;

namespace Refii.Core;

/// <summary>
/// The mouse's camera turn on the host: counts to degrees, and the part of the turn no guest
/// camera update has taken yet, drawn the frame the mouse makes it.
/// </summary>
/// <remarks>
/// <para>THE GUEST IS THE CAMERA'S AUTHORITY; THE MOUSE IS ITS INPUT. Every turn the mouse makes
/// is posted to the guest, which hands all of it to the local player's active camera before that
/// camera's next update, and the camera turns it whole in that update — outside the stick's
/// easing (src/vfs/src/hooks/mouse_look.cpp). Position, framing, collision, lock-on and cuts stay
/// the camera's.</para>
/// <para>SO THERE IS NOTHING TO PREDICT. At any frame the turn is in exactly one of two places:
/// taken by an update, and then carried by the camera the guest states after it; or posted and
/// not yet taken, <c>(posted - taken) * factor</c> of the camera's degrees, which the host turns
/// the drawn camera by — about the vertical through the camera's focus for the third-person
/// camera, in place for first person, rigidly about the hero's head for the ranged aim, its
/// reticule turned with it (<see cref="Turn"/>, <see cref="AimTurn"/>). When an update takes a
/// turn the guest states, in the same read, both the camera that carries it and the turn it
/// absorbed, and the drawn history is restated by that turn
/// (<see cref="Engine_TickTimeline.TurnAbout(Vector3, Basis)"/>): the turn moves from "ahead" to
/// "stated" with the drawn view unchanged, and the timeline keeps blending only the camera's own
/// motion.</para>
/// </remarks>
public sealed class Engine_MouseLook
{
	/// <summary>
	/// Degrees of turn per mouse count at sensitivity 1 and the profile's middle camera sensitivity:
	/// the common shooter default (about 2400 counts — 3 inches at 800 DPI — per turn).
	/// </summary>
	public const float DegreesPerCount = 0.15f;

	/// <summary>The camera takes no look input now (a cut, a canned action, a lock, a snapped aim).</summary>
	public const int ModeNone = 0;

	/// <summary>The camera turns about the vertical through its focus (SACCamera.ApplyRotateVector).</summary>
	public const int ModeOrbit = 1;

	/// <summary>The camera turns about its own position (FirstPersonCamera.UpdateRotations).</summary>
	public const int ModeInPlace = 2;

	/// <summary>
	/// The ranged aim: the camera and its aim point turn rigidly about the hero's head
	/// (RangedCombatCamera hangs from it) — the yaw about the vertical through it, then the pitch
	/// about the level right axis through it — the elevation bounded by the camera's AngleLimit.
	/// </summary>
	public const int ModeAim = 3;

	/// <summary>
	/// The mouse's turn for <paramref name="counts"/> (Godot's relative motion: right and down
	/// positive) as (right, up) degrees at the profile's middle camera sensitivity.
	/// </summary>
	public static Vector2 Degrees(Vector2 counts, float sensitivity, bool invertX)
	{
		float scale = DegreesPerCount * sensitivity;
		return new Vector2(counts.X * scale * (invertX ? -1f : 1f), -counts.Y * scale);
	}

	/// <summary><paramref name="camera"/>'s forward with the vertical removed, normalised; zero looking straight up or down.</summary>
	private static Vector3 LevelForward(Transform3D camera)
	{
		Vector3 forward = -camera.Basis.Z;
		var level = new Vector3(forward.X, 0f, forward.Z);
		return level.LengthSquared() < 1e-8f ? Vector3.Zero : level.Normalized();
	}

	/// <summary>
	/// The point <paramref name="metres"/> out along <paramref name="camera"/>'s level line of
	/// sight: where the vertical the guest's camera turns about meets the ground plane of the eye.
	/// SACCamera looks from its placed position at RollingFocalPos, so its focus lies on that line.
	/// </summary>
	public static Vector3 Pivot(Transform3D camera, float metres)
	{
		Vector3 level = LevelForward(camera);
		if (metres <= 0f || !float.IsFinite(metres) || level == Vector3.Zero)
		{
			return camera.Origin;
		}

		return camera.Origin + (level * metres);
	}

	/// <summary>
	/// The point at <paramref name="offsets"/> metres along <paramref name="camera"/>'s level
	/// forward (X), level right (Y) and up (Z) — how the guest states the ranged aim's pivot, the
	/// hero's head, against the camera it placed.
	/// </summary>
	public static Vector3 AimPivot(Transform3D camera, Vector3 offsets)
	{
		Vector3 level = LevelForward(camera);
		if (level == Vector3.Zero || !offsets.IsFinite())
		{
			return camera.Origin;
		}

		Vector3 right = level.Cross(Vector3.Up);
		return camera.Origin + (level * offsets.X) + (right * offsets.Y) + (Vector3.Up * offsets.Z);
	}

	/// <summary><paramref name="camera"/> orbited about the vertical through <paramref name="pivot"/> by <paramref name="rightDegrees"/>.</summary>
	public static Transform3D Orbit(Transform3D camera, Vector3 pivot, double rightDegrees)
	{
		if (rightDegrees == 0.0 || !double.IsFinite(rightDegrees))
		{
			return camera;
		}

		var turn = new Basis(Vector3.Up, -Mathf.DegToRad((float)rightDegrees));
		Vector3 arm = camera.Origin - pivot;
		arm.Y = 0f;
		Vector3 origin = pivot + (turn * arm);
		origin.Y = camera.Origin.Y;
		return new Transform3D(turn * camera.Basis, origin);
	}

	/// <summary>
	/// The pitch of a turn of <paramref name="camera"/> by <paramref name="upDegrees"/>, kept so its
	/// elevation stays inside ±<paramref name="limitDegrees"/> (the guest's bound, applied the same way:
	/// never pushed further outside it). A limit of 0 or less bounds nothing.
	/// </summary>
	public static double BoundPitch(Transform3D camera, double upDegrees, float limitDegrees)
	{
		if (!double.IsFinite(upDegrees))
		{
			return 0.0;
		}

		if (limitDegrees <= 0f || !float.IsFinite(limitDegrees))
		{
			return upDegrees;
		}

		Vector3 forward = (-camera.Basis.Z).Normalized();
		double elevation = Mathf.RadToDeg(Math.Asin(Math.Clamp(forward.Y, -1f, 1f)));
		double high = Math.Max(limitDegrees - elevation, 0.0);
		double low = Math.Min(-limitDegrees - elevation, 0.0);
		return Math.Clamp(upDegrees, low, high);
	}

	/// <summary>
	/// The ranged aim's turn of <paramref name="camera"/>: <paramref name="rightDegrees"/> about the
	/// vertical, then <paramref name="upDegrees"/> about the level right axis the yaw leaves — the
	/// arithmetic of the guest's RangedCombatCamera wrapper (Godot/src/host/CoopHeroLua.cs), whose
	/// right axis is the heading's right after the yaw.
	/// </summary>
	public static Basis AimRotation(Transform3D camera, double rightDegrees, double upDegrees)
	{
		var yaw = new Basis(Vector3.Up, -Mathf.DegToRad((float)(double.IsFinite(rightDegrees) ? rightDegrees : 0.0)));
		Vector3 level = LevelForward(camera);
		if (level == Vector3.Zero || upDegrees == 0.0 || !double.IsFinite(upDegrees))
		{
			return yaw;
		}

		Vector3 right = (yaw * level).Cross(Vector3.Up).Normalized();
		return new Basis(right, Mathf.DegToRad((float)upDegrees)) * yaw;
	}

	/// <summary>A transform turned by <paramref name="turn"/> about <paramref name="pivot"/>.</summary>
	public static Transform3D Rigid(Transform3D t, Vector3 pivot, Basis turn) =>
		new(turn * t.Basis, pivot + (turn * (t.Origin - pivot)));

	/// <summary>
	/// The point a camera drawn in <paramref name="mode"/> turns about: for the aim the stated point
	/// in its level frame; on its level line of sight, <paramref name="pivot"/>.X out, for an orbit;
	/// its own position in place.
	/// </summary>
	public static Vector3 PivotFor(Transform3D camera, int mode, Vector3 pivot) => mode switch
	{
		ModeAim => AimPivot(camera, pivot),
		ModeOrbit => Pivot(camera, pivot.X),
		_ => camera.Origin,
	};

	/// <summary>
	/// The turn of <paramref name="camera"/> a camera in <paramref name="mode"/> makes for
	/// (<paramref name="rightDegrees"/>, <paramref name="upDegrees"/>): about the vertical alone but
	/// for the aim, whose pitch is <paramref name="upDegrees"/> as given (bound it first, <see cref="BoundPitch"/>).
	/// </summary>
	public static Basis Rotation(Transform3D camera, int mode, double rightDegrees, double upDegrees) => mode switch
	{
		ModeAim => AimRotation(camera, rightDegrees, upDegrees),
		ModeOrbit or ModeInPlace when rightDegrees != 0.0 && double.IsFinite(rightDegrees) =>
			new Basis(Vector3.Up, -Mathf.DegToRad((float)rightDegrees)),
		_ => Basis.Identity,
	};

	/// <summary>
	/// <paramref name="camera"/> turned by (<paramref name="rightDegrees"/>, <paramref name="upDegrees"/>)
	/// the way a camera in <paramref name="mode"/> turns. An orbit keeps the eye's height; the aim
	/// turns rigidly, its pitch bounded at ±<paramref name="pitchLimit"/> degrees of elevation.
	/// </summary>
	public static Transform3D Turn(Transform3D camera, int mode, Vector3 pivot, double rightDegrees,
		double upDegrees = 0.0, float pitchLimit = 0f) => mode switch
	{
		ModeOrbit => Orbit(camera, Pivot(camera, pivot.X), rightDegrees),
		ModeInPlace => Orbit(camera, camera.Origin, rightDegrees),
		ModeAim => Rigid(camera, AimPivot(camera, pivot),
			AimRotation(camera, rightDegrees, BoundPitch(camera, upDegrees, pitchLimit))),
		_ => camera,
	};

	private bool _started;
	private double _absorbed;
	private double _absorbedPitch;
	private double _posted;
	private double _taken;
	private double _postedPitch;
	private double _takenPitch;
	private double _factor = 1.0;

	/// <summary>How the camera stated in the last whole read turns (a <c>Mode*</c> constant).</summary>
	public int Mode { get; private set; } = ModeNone;

	/// <summary>
	/// The pivot as last stated: for <see cref="ModeOrbit"/> X is the focus's distance along the
	/// level line of sight; for <see cref="ModeAim"/> the point (level forward, level right, up).
	/// </summary>
	public Vector3 PivotOffsets { get; private set; }

	/// <summary>The focus's distance along the level line of sight, for <see cref="ModeOrbit"/>.</summary>
	public float PivotMetres => PivotOffsets.X;

	/// <summary>For <see cref="ModeAim"/>: the camera's elevation bound, degrees.</summary>
	public float PitchLimit { get; private set; }

	/// <summary>For <see cref="ModeAim"/>: the guest's ranged aim whose reticule turns with the view.</summary>
	public uint Aim { get; private set; }

	/// <summary>
	/// The turn posted and not yet taken by any camera update, in the camera's degrees rightward:
	/// what the drawn camera is turned by. Zero while the camera takes no look input.
	/// </summary>
	public double Outstanding => Mode == ModeNone ? 0.0 : (_posted - _taken) * _factor;

	/// <summary>The pitch posted and not yet taken, the camera's degrees upward: drawn for the aim only.</summary>
	public double OutstandingPitch => Mode == ModeAim ? (_postedPitch - _takenPitch) * _factor : 0.0;

	/// <summary>Everything the guest's camera has turned for the mouse, in its degrees, as last read.</summary>
	public double Absorbed => _absorbed;

	/// <summary>Everything the guest's camera has pitched for the mouse, in its degrees, as last read.</summary>
	public double AbsorbedPitch => _absorbedPitch;

	/// <summary>
	/// A whole read of the guest's account (its sequence even and unchanged across the camera read
	/// it was taken with). Returns the turn the camera absorbed since the last whole read, in its
	/// degrees rightward and upward — the turn the read's camera carries that the drawn history does not.
	/// </summary>
	public (double Right, double Up) Observe(double posted, double taken, double absorbed,
		double postedPitch, double takenPitch, double absorbedPitch, float factor, Vector3 pivot,
		int mode, float pitchLimit = 0f, uint aim = 0u)
	{
		if (!double.IsFinite(posted) || !double.IsFinite(taken) || !double.IsFinite(absorbed) ||
			!double.IsFinite(postedPitch) || !double.IsFinite(takenPitch) || !double.IsFinite(absorbedPitch))
		{
			return (0.0, 0.0);
		}

		(double Right, double Up) turned = _started
			? (absorbed - _absorbed, absorbedPitch - _absorbedPitch)
			: (0.0, 0.0);
		_started = true;
		_absorbed = absorbed;
		_absorbedPitch = absorbedPitch;
		_posted = posted;
		_taken = taken;
		_postedPitch = postedPitch;
		_takenPitch = takenPitch;
		if (factor > 0f && float.IsFinite(factor))
		{
			_factor = factor;
		}

		Mode = mode is ModeOrbit or ModeInPlace or ModeAim ? mode : ModeNone;
		PivotOffsets = pivot.IsFinite() ? pivot : Vector3.Zero;
		if (Mode == ModeOrbit && PivotOffsets.X <= 0f)
		{
			PivotOffsets = Vector3.Zero;
		}

		PitchLimit = Mode == ModeAim && float.IsFinite(pitchLimit) && pitchLimit > 0f ? pitchLimit : 0f;
		Aim = Mode == ModeAim ? aim : 0u;
		return turned;
	}

	/// <summary>
	/// A read that straddled a camera update: only what was posted is new. The camera and the rest
	/// of the account stay as last read whole, and the turn posted since is drawn against them.
	/// </summary>
	public void ObservePosted(double posted, double postedPitch)
	{
		if (_started && double.IsFinite(posted) && double.IsFinite(postedPitch))
		{
			_posted = posted;
			_postedPitch = postedPitch;
		}
	}

	/// <summary>
	/// The turn drawn ahead of <paramref name="camera"/> this frame: the pivot and the rotation the
	/// outstanding turn makes about it (identity when nothing is outstanding). The same turn moves
	/// the aim's reticule anchor, so the two stay as the guest will state them.
	/// </summary>
	public (Vector3 Pivot, Basis Turn) Ahead(Transform3D camera)
	{
		Vector3 pivot = PivotFor(camera, Mode, PivotOffsets);
		double up = Mode == ModeAim ? BoundPitch(camera, OutstandingPitch, PitchLimit) : 0.0;
		return (pivot, Rotation(camera, Mode, Outstanding, up));
	}

	/// <summary><paramref name="camera"/> with the outstanding turn drawn ahead (<see cref="Ahead"/>).</summary>
	public Transform3D DrawAhead(Transform3D camera)
	{
		if (Mode == ModeOrbit)
		{
			// The orbit keeps the eye's height exactly (the string's camera turns level).
			return Orbit(camera, Pivot(camera, PivotOffsets.X), Outstanding);
		}

		(Vector3 pivot, Basis turn) = Ahead(camera);
		return Rigid(camera, pivot, turn);
	}
}
