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
/// camera, in place for first person (<see cref="Turn"/>). When an update takes a turn the guest
/// states, in the same read, both the camera that carries it and the turn it absorbed, and the
/// drawn history is restated by that turn (<see cref="Engine_TickTimeline.TurnAbout"/>): the turn
/// moves from "ahead" to "stated" with the drawn view unchanged, and the timeline keeps blending
/// only the camera's own motion.</para>
/// </remarks>
public sealed class Engine_MouseLook
{
	/// <summary>
	/// Degrees of turn per mouse count at sensitivity 1 and the profile's middle camera sensitivity:
	/// the common shooter default (about 2400 counts — 3 inches at 800 DPI — per turn).
	/// </summary>
	public const float DegreesPerCount = 0.15f;

	/// <summary>The camera takes no look input now (a cut, a canned action, a lock, the ranged aim).</summary>
	public const int ModeNone = 0;

	/// <summary>The camera turns about the vertical through its focus (SACCamera.ApplyRotateVector).</summary>
	public const int ModeOrbit = 1;

	/// <summary>The camera turns about its own position (FirstPersonCamera.UpdateRotations).</summary>
	public const int ModeInPlace = 2;

	/// <summary>
	/// The mouse's turn for <paramref name="counts"/> (Godot's relative motion: right and down
	/// positive) as (right, up) degrees at the profile's middle camera sensitivity.
	/// </summary>
	public static Vector2 Degrees(Vector2 counts, float sensitivity, bool invertX)
	{
		float scale = DegreesPerCount * sensitivity;
		return new Vector2(counts.X * scale * (invertX ? -1f : 1f), -counts.Y * scale);
	}

	/// <summary>
	/// The point <paramref name="metres"/> out along <paramref name="camera"/>'s level line of
	/// sight: where the vertical the guest's camera turns about meets the ground plane of the eye.
	/// SACCamera looks from its placed position at RollingFocalPos, so its focus lies on that line.
	/// </summary>
	public static Vector3 Pivot(Transform3D camera, float metres)
	{
		Vector3 forward = -camera.Basis.Z;
		var level = new Vector3(forward.X, 0f, forward.Z);
		if (metres <= 0f || !float.IsFinite(metres) || level.LengthSquared() < 1e-8f)
		{
			return camera.Origin;
		}

		return camera.Origin + (level.Normalized() * metres);
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
	/// The point a camera drawn in <paramref name="mode"/> turns about: on its level line of sight,
	/// <paramref name="pivotMetres"/> out for an orbit, its own position in place.
	/// </summary>
	public static Vector3 PivotFor(Transform3D camera, int mode, float pivotMetres) =>
		Pivot(camera, mode == ModeOrbit ? pivotMetres : 0f);

	/// <summary><paramref name="camera"/> turned rightward by <paramref name="rightDegrees"/> the way a camera in <paramref name="mode"/> turns.</summary>
	public static Transform3D Turn(Transform3D camera, int mode, float pivotMetres, double rightDegrees) =>
		mode == ModeNone ? camera : Orbit(camera, PivotFor(camera, mode, pivotMetres), rightDegrees);

	private bool _started;
	private double _absorbed;
	private double _posted;
	private double _taken;
	private double _factor = 1.0;

	/// <summary>How the camera stated in the last whole read turns (a <c>Mode*</c> constant).</summary>
	public int Mode { get; private set; } = ModeNone;

	/// <summary>Its focus's distance along the level line of sight, for <see cref="ModeOrbit"/>.</summary>
	public float PivotMetres { get; private set; }

	/// <summary>
	/// The turn posted and not yet taken by any camera update, in the camera's degrees rightward:
	/// what the drawn camera is turned by. Zero while the camera takes no look input.
	/// </summary>
	public double Outstanding => Mode == ModeNone ? 0.0 : (_posted - _taken) * _factor;

	/// <summary>Everything the guest's camera has turned for the mouse, in its degrees, as last read.</summary>
	public double Absorbed => _absorbed;

	/// <summary>
	/// A whole read of the guest's account (its sequence even and unchanged across the camera read
	/// it was taken with). Returns the turn the camera absorbed since the last whole read, in its
	/// degrees rightward — the turn the read's camera carries that the drawn history does not.
	/// </summary>
	public double Observe(double posted, double taken, double absorbed, float factor, float pivotMetres, int mode)
	{
		if (!double.IsFinite(posted) || !double.IsFinite(taken) || !double.IsFinite(absorbed))
		{
			return 0.0;
		}

		double turned = _started ? absorbed - _absorbed : 0.0;
		_started = true;
		_absorbed = absorbed;
		_posted = posted;
		_taken = taken;
		if (factor > 0f && float.IsFinite(factor))
		{
			_factor = factor;
		}

		Mode = mode is ModeOrbit or ModeInPlace ? mode : ModeNone;
		PivotMetres = float.IsFinite(pivotMetres) && pivotMetres > 0f ? pivotMetres : 0f;
		return turned;
	}

	/// <summary>
	/// A read that straddled a camera update: only what was posted is new. The camera and the rest
	/// of the account stay as last read whole, and the turn posted since is drawn against them.
	/// </summary>
	public void ObservePosted(double posted)
	{
		if (_started && double.IsFinite(posted))
		{
			_posted = posted;
		}
	}
}
