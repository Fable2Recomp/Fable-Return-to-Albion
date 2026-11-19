using System;
using Godot;

namespace Refii.Core;

/// <summary>
/// Display-rate host camera controller for mouse-driven ranged aiming (<see cref="Engine_MouseLook.ModeAim"/>)
/// shared across Fable 2 and Fable 3.
/// </summary>
/// <remarks>
/// <para>The host owns aim yaw/pitch at full display rate (60–240 Hz) with zero 15 Hz guest easing
/// or cone lag, while the guest supplies only high-level gameplay framing facts: hero collision head
/// position (tracked on an internal <see cref="Engine_TickTimeline"/>), zoomed FOV, elevation limit,
/// sensitivity factor, invert-Y state, and obstacle-adjusted camera arm.</para>
/// <para>The camera offset from the hero's head is decomposed along
/// <c>(-forward, levelRight, Vector3.Up)</c> matching <c>RangedCombatCamera:GetDesiredCameraPos</c>,
/// so pitching between 15 Hz guest ticks introduces zero vertical camera bob.</para>
/// </remarks>
public sealed class Engine_HostAimCamera
{
	/// <summary>Default pitch clamp when the guest reports 0 or uninitialized AngleLimit.</summary>
	public const float DefaultPitchLimitDegrees = 30f;

	private const double SmoothRate = 22.0;
	private const double PullInRate = 40.0;

	private readonly Engine_TickTimeline _headLine = new(stillWhenUnchanged: true);

	private float _targetBehind = 1.5f;
	private float _targetSide = 0.35f;
	private float _targetHeight = 0.1f;
	private float _drawnBehind = 1.5f;
	private float _drawnSide = 0.35f;
	private float _drawnHeight = 0.1f;
	private float _targetFov = 55f;

	/// <summary>True while the host aim controller is driving the camera.</summary>
	public bool Active { get; private set; }

	/// <summary>Current heading in radians (clockwise/rightward positive about +Y from -Z).</summary>
	public double YawRad { get; private set; }

	/// <summary>Current elevation in radians (upward positive), clamped to ±<see cref="PitchLimitDegrees"/>.</summary>
	public double PitchRad { get; private set; }

	/// <summary>Elevation bound in degrees reported by the active RangedCombatCamera.</summary>
	public float PitchLimitDegrees { get; private set; } = DefaultPitchLimitDegrees;

	/// <summary>Profile camera sensitivity multiplier (<c>0.5 + Player.GetCameraSensitivity</c>).</summary>
	public float SensitivityFactor { get; private set; } = 1f;

	/// <summary>True when the guest player profile has CameraLookInverted enabled.</summary>
	public bool InvertY { get; private set; }

	/// <summary>Current smoothed vertical FOV in degrees.</summary>
	public float DrawnFov { get; private set; } = 55f;

	/// <summary>
	/// Decomposes <c>camPos - headPos</c> into pitch-invariant <c>(behind, side, height)</c>
	/// coordinates along <c>(-camFwd, levelRight, Vector3.Up)</c>, matching
	/// <c>RangedCombatCamera:GetDesiredCameraPos</c>.
	/// </summary>
	public static bool DecomposeArm(
		Vector3 camPos, Vector3 camFwd, Vector3 headPos,
		out float behind, out float side, out float height)
	{
		behind = 0f;
		side = 0f;
		height = 0f;
		if (!camPos.IsFinite() || !camFwd.IsFinite() || !headPos.IsFinite() ||
			camFwd.LengthSquared() < 1e-8f || camPos.DistanceSquaredTo(headPos) > 25f * 25f)
		{
			return false;
		}

		Vector3 fwd = camFwd.Normalized();
		var levelFwd = new Vector3(fwd.X, 0f, fwd.Z);
		float levelLen = levelFwd.Length();
		if (levelLen < 1e-4f)
		{
			return false;
		}

		levelFwd /= levelLen;
		Vector3 levelRight = levelFwd.Cross(Vector3.Up);
		Vector3 offset = camPos - headPos;
		side = offset.Dot(levelRight);
		float cosPitch = MathF.Max(fwd.Dot(levelFwd), 0.1f);
		behind = -offset.Dot(levelFwd) / cosPitch;
		height = offset.Y + (fwd.Y * behind);
		return float.IsFinite(behind) && float.IsFinite(side) && float.IsFinite(height);
	}

	/// <summary>Unit forward direction in Godot world space for <paramref name="yawRad"/> and <paramref name="pitchRad"/>.</summary>
	public static Vector3 ForwardFromAngles(double yawRad, double pitchRad)
	{
		double cosPitch = Math.Cos(pitchRad);
		return new Vector3(
			(float)(Math.Sin(yawRad) * cosPitch),
			(float)Math.Sin(pitchRad),
			(float)(-Math.Cos(yawRad) * cosPitch));
	}

	/// <summary>Roll-free camera <see cref="Basis"/> in Godot world space for <paramref name="yawRad"/> and <paramref name="pitchRad"/>.</summary>
	public static Basis BasisFromAngles(double yawRad, double pitchRad)
	{
		Vector3 forward = ForwardFromAngles(yawRad, pitchRad);
		var levelRight = new Vector3((float)Math.Cos(yawRad), 0f, (float)Math.Sin(yawRad));
		Vector3 basisZ = -forward;
		Vector3 basisY = basisZ.Cross(levelRight);
		return new Basis(levelRight, basisY, basisZ);
	}

	/// <summary>Converts a Godot world vector/position back into guest world axes (inverse of <c>GuestToGodot</c>).</summary>
	public static Vector3 GodotToGuest(Vector3 godot) => new(-godot.X, godot.Z, godot.Y);

	/// <summary>
	/// Enters host-authoritative aiming seeded seamlessly from <paramref name="seedDrawn"/> and the
	/// latest guest camera/head framing.
	/// </summary>
	public void Begin(
		Transform3D seedDrawn, Transform3D guestStated, Vector3 headGodot, float fov,
		float pitchLimitDeg, float factor, bool invertY, double tick)
	{
		Vector3 rawFwd = -seedDrawn.Basis.Z;
		Vector3 seedFwd = rawFwd.IsFinite() && rawFwd.LengthSquared() > 1e-8f
			? rawFwd.Normalized()
			: Vector3.Forward;

		PitchLimitDegrees = NormalizePitchLimit(pitchLimitDeg);
		double limitRad = PitchLimitDegrees * (Math.PI / 180.0);
		YawRad = WrapAngle(Math.Atan2(seedFwd.X, -seedFwd.Z));
		PitchRad = Math.Clamp(Math.Asin(Math.Clamp(seedFwd.Y, -1.0, 1.0)), -limitRad, limitRad);
		SensitivityFactor = factor > 0f && float.IsFinite(factor) ? factor : 1f;
		InvertY = invertY;

		if (!DecomposeArm(guestStated.Origin, -guestStated.Basis.Z, headGodot, out float b, out float s, out float h) &&
			!DecomposeArm(seedDrawn.Origin, seedFwd, headGodot, out b, out s, out h))
		{
			b = 1.5f;
			s = 0.35f;
			h = 0.1f;
		}

		_targetBehind = _drawnBehind = b;
		_targetSide = _drawnSide = s;
		_targetHeight = _drawnHeight = h;

		_headLine.Reset(tick, new Transform3D(Basis.Identity, headGodot));
		float validFov = float.IsFinite(fov) && fov >= 1f && fov <= 179f ? fov : 55f;
		_targetFov = DrawnFov = validFov;
		Active = true;
	}

	/// <summary>Exits host-authoritative aiming and clears internal timeline state.</summary>
	public void End()
	{
		Active = false;
		_headLine.Clear();
	}

	/// <summary>Integrates display-rate mouse turn degrees (rightward and upward positive before invert).</summary>
	public void ApplyMouseDelta(double rightDeg, double upDeg)
	{
		if (!Active || !double.IsFinite(rightDeg) || !double.IsFinite(upDeg))
		{
			return;
		}

		double scale = SensitivityFactor * (Math.PI / 180.0);
		YawRad = WrapAngle(YawRad + (rightDeg * scale));
		double signedUp = InvertY ? -upDeg : upDeg;
		double limitRad = PitchLimitDegrees * (Math.PI / 180.0);
		PitchRad = Math.Clamp(PitchRad + (signedUp * scale), -limitRad, limitRad);
	}

	/// <summary>Feeds a completed guest tick into the head timeline and arm/FOV targets.</summary>
	public void ObserveGuest(
		double tick, Transform3D guestStated, Vector3 headGodot, float fov,
		float pitchLimitDeg, float factor, bool invertY, bool snap)
	{
		if (!Active)
		{
			return;
		}

		PitchLimitDegrees = NormalizePitchLimit(pitchLimitDeg);
		double limitRad = PitchLimitDegrees * (Math.PI / 180.0);
		PitchRad = Math.Clamp(PitchRad, -limitRad, limitRad);
		if (factor > 0f && float.IsFinite(factor))
		{
			SensitivityFactor = factor;
		}
		InvertY = invertY;

		bool teleport = snap || _headLine.Empty || !headGodot.IsFinite() ||
			_headLine.Newest.Origin.DistanceSquaredTo(headGodot) >
				Engine_TickTimeline.TeleportMetres * Engine_TickTimeline.TeleportMetres;

		if (headGodot.IsFinite())
		{
			if (teleport)
			{
				_headLine.Reset(tick, new Transform3D(Basis.Identity, headGodot));
			}
			else
			{
				_headLine.Observe(tick, new Transform3D(Basis.Identity, headGodot));
			}
		}

		if (headGodot.IsFinite() &&
			DecomposeArm(guestStated.Origin, -guestStated.Basis.Z, headGodot, out float b, out float s, out float h))
		{
			_targetBehind = b;
			_targetSide = s;
			_targetHeight = h;
			if (teleport)
			{
				_drawnBehind = b;
				_drawnSide = s;
				_drawnHeight = h;
			}
		}

		if (float.IsFinite(fov) && fov >= 1f && fov <= 179f)
		{
			_targetFov = fov;
			if (teleport)
			{
				DrawnFov = fov;
			}
		}
	}

	/// <summary>Evaluates the host camera transform, smoothed FOV, and guest-space aim direction for this frame.</summary>
	public Transform3D Evaluate(
		ulong nowUsec, double drawTick, double deltaSeconds,
		out float fov, out Vector3 guestForward)
	{
		double dt = double.IsFinite(deltaSeconds) && deltaSeconds > 0.0
			? Math.Min(deltaSeconds, 0.25)
			: 0.0;
		float alpha = dt > 0.0 ? (float)(1.0 - Math.Exp(-SmoothRate * dt)) : 1f;
		float behindAlpha = (_targetBehind < _drawnBehind && dt > 0.0)
			? (float)(1.0 - Math.Exp(-PullInRate * dt))
			: alpha;

		_drawnBehind = Mathf.Lerp(_drawnBehind, _targetBehind, behindAlpha);
		_drawnSide = Mathf.Lerp(_drawnSide, _targetSide, alpha);
		_drawnHeight = Mathf.Lerp(_drawnHeight, _targetHeight, alpha);
		DrawnFov = Mathf.Lerp(DrawnFov, _targetFov, alpha);

		Vector3 head = _headLine.Advance(nowUsec, drawTick).Origin;
		Vector3 forward = ForwardFromAngles(YawRad, PitchRad);
		Basis basis = BasisFromAngles(YawRad, PitchRad);
		Vector3 levelRight = basis.X;
		Vector3 origin = head - (forward * _drawnBehind) + (levelRight * _drawnSide) + (Vector3.Up * _drawnHeight);

		fov = DrawnFov;
		guestForward = GodotToGuest(forward);
		return new Transform3D(basis, origin);
	}

	private static float NormalizePitchLimit(float degrees) =>
		degrees > 1f && float.IsFinite(degrees) ? MathF.Min(degrees, 89f) : DefaultPitchLimitDegrees;

	private static double WrapAngle(double rad) =>
		Math.Atan2(Math.Sin(rad), Math.Cos(rad));
}
