namespace Refii.Tests.Host;

using System;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Core;
using Refii.Game;

[TestSuite]
public class EngineHostAimCameraTests
{
	[TestCase]
	public void TestDecomposeArmIdentityAndPitchInvariance()
	{
		var headPos = new Vector3(12f, 3.4f, -8f);
		const float expectedBehind = 1.65f;
		const float expectedSide = 0.42f;
		const float expectedHeight = 0.14f;

		foreach (float yawDeg in new[] { -145f, -35f, 0f, 47f, 128f })
		{
			foreach (float pitchDeg in new[] { -28f, -12f, 0f, 15f, 29f })
			{
				Basis basis = Engine_HostAimCamera.BasisFromAngles(
					Mathf.DegToRad(yawDeg), Mathf.DegToRad(pitchDeg));
				Vector3 fwd = -basis.Z;
				Vector3 levelRight = new Vector3(fwd.X, 0f, fwd.Z).Normalized().Cross(Vector3.Up);
				Vector3 camPos = headPos - (fwd * expectedBehind) + (levelRight * expectedSide) +
					(Vector3.Up * expectedHeight);

				bool ok = Engine_HostAimCamera.DecomposeArm(
					camPos, fwd, headPos, out float behind, out float side, out float height);
				AssertThat(ok).IsTrue();
				AssertThat(MathF.Abs(behind - expectedBehind)).IsLess(1e-4f);
				AssertThat(MathF.Abs(side - expectedSide)).IsLess(1e-4f);
				AssertThat(MathF.Abs(height - expectedHeight)).IsLess(1e-4f);
			}
		}
	}

	[TestCase]
	public void TestBeginSeedsZeroJumpFromDrawnCamera()
	{
		var aimCam = new Engine_HostAimCamera();
		var headPos = new Vector3(4f, 1.8f, 2f);
		Basis seedBasis = Engine_HostAimCamera.BasisFromAngles(Mathf.DegToRad(42f), Mathf.DegToRad(-11f));
		Vector3 seedFwd = -seedBasis.Z;
		Vector3 levelRight = new Vector3(seedFwd.X, 0f, seedFwd.Z).Normalized().Cross(Vector3.Up);
		var seedDrawn = new Transform3D(
			seedBasis, headPos - (seedFwd * 1.4f) + (levelRight * 0.35f) + (Vector3.Up * 0.12f));

		aimCam.Begin(
			seedDrawn, seedDrawn, headPos, fov: 12.5f,
			pitchLimitDeg: 35f, factor: 1f, invertY: false, tick: 100.0);

		Transform3D frame0 = aimCam.Evaluate(1_000_000UL, 100.0, 0.0, out float fov, out Vector3 guestFwd);
		AssertThat((-frame0.Basis.Z).DistanceTo(seedFwd)).IsLess(1e-4f);
		AssertThat(frame0.Origin.DistanceTo(seedDrawn.Origin)).IsLess(1e-4f);
		AssertThat(MathF.Abs(fov - 12.5f)).IsLess(1e-4f);
		AssertThat(guestFwd.IsEqualApprox(Engine_HostAimCamera.GodotToGuest(seedFwd))).IsTrue();
	}

	[TestCase]
	public void TestDisplayRateIntegrationAt120HzAgainst15HzGuest()
	{
		var aimCam = new Engine_HostAimCamera();
		var headPos = new Vector3(0f, 1.6f, 0f);
		Basis startBasis = Engine_HostAimCamera.BasisFromAngles(0.0, 0.0);
		var startCam = new Transform3D(startBasis, headPos + new Vector3(0.35f, 0.1f, 1.5f));

		aimCam.Begin(startCam, startCam, headPos, 11.28f, 30f, 1f, invertY: false, tick: 100.0);

		double prevYaw = aimCam.YawRad;
		ulong usec = 1_000_000UL;
		const double dt = 1.0 / 120.0;
		const double deltaDegPerFrame = 0.25;

		for (int i = 1; i <= 24; i++)
		{
			usec += 8_333UL;
			double drawTick = 100.0 + (i / 8.0);
			aimCam.ApplyMouseDelta(deltaDegPerFrame, 0.0);

			if (i % 8 == 0)
			{
				// Guest responds at 15 Hz with slightly moved head & stale/eased guest camera.
				double tick = 100.0 + (i / 8);
				Vector3 movedHead = headPos + new Vector3(0.05f * (i / 8), 0f, 0f);
				aimCam.ObserveGuest(tick, startCam, movedHead, 11.28f, 30f, 1f, invertY: false, snap: false);
			}

			Transform3D drawn = aimCam.Evaluate(usec, drawTick, dt, out _, out _);
			double stepYaw = aimCam.YawRad - prevYaw;
			AssertThat(stepYaw).IsGreater(0.0);
			AssertThat(Math.Abs(Mathf.RadToDeg((float)stepYaw) - deltaDegPerFrame)).IsLess(1e-3);
			// No roll on host camera basis.
			AssertThat(MathF.Abs(drawn.Basis.X.Y)).IsLess(1e-5f);
			prevYaw = aimCam.YawRad;
		}
	}

	[TestCase]
	public void TestPitchLimitClampAndInvertY()
	{
		var aimCam = new Engine_HostAimCamera();
		var head = new Vector3(0f, 1.6f, 0f);
		var start = new Transform3D(Basis.Identity, new Vector3(0.3f, 1.7f, 1.5f));

		aimCam.Begin(start, start, head, 40f, pitchLimitDeg: 30f, factor: 1.5f, invertY: false, tick: 10.0);
		aimCam.ApplyMouseDelta(0.0, 40.0); // 40 * 1.5 = +60 deg -> clamped to +30 deg
		AssertThat(MathF.Abs(Mathf.RadToDeg((float)aimCam.PitchRad) - 30f)).IsLess(1e-3f);

		aimCam.ObserveGuest(11.0, start, head, 40f, pitchLimitDeg: 30f, factor: 1.0f, invertY: true, snap: false);
		aimCam.ApplyMouseDelta(0.0, 20.0); // invertY negates +20 -> +10 deg
		AssertThat(MathF.Abs(Mathf.RadToDeg((float)aimCam.PitchRad) - 10f)).IsLess(1e-3f);

		aimCam.ApplyMouseDelta(0.0, 80.0); // invertY -> clamped to -30 deg
		AssertThat(MathF.Abs(Mathf.RadToDeg((float)aimCam.PitchRad) + 30f)).IsLess(1e-3f);
	}

	[TestCase]
	public void TestTeleportSnapAndSmoothArmTransition()
	{
		var aimCam = new Engine_HostAimCamera();
		var head0 = new Vector3(0f, 1.6f, 0f);
		var cam0 = new Transform3D(Basis.Identity, new Vector3(0.4f, 1.7f, 1.8f));

		aimCam.Begin(cam0, cam0, head0, 50f, 30f, 1f, invertY: false, tick: 10.0);

		// Smooth arm pull-in & FOV zoom (non-teleport)
		var zoomedCam = new Transform3D(Basis.Identity, new Vector3(0.3f, 1.7f, 0.9f));
		aimCam.ObserveGuest(11.0, zoomedCam, head0, 12f, 30f, 1f, invertY: false, snap: false);
		aimCam.Evaluate(1_016_666UL, 11.0, 0.016, out float midFov, out _);
		AssertThat(midFov).IsGreater(12f);
		AssertThat(midFov).IsLess(50f);

		// Teleport jump resets head & snaps FOV immediately
		var headFar = new Vector3(50f, 1.6f, 0f);
		var camFar = new Transform3D(Basis.Identity, new Vector3(50.3f, 1.7f, 1.2f));
		aimCam.ObserveGuest(12.0, camFar, headFar, 25f, 30f, 1f, invertY: false, snap: false);
		Transform3D snapped = aimCam.Evaluate(1_033_333UL, 12.0, 0.016, out float snapFov, out _);
		AssertThat(MathF.Abs(snapFov - 25f)).IsLess(1e-3f);
		AssertThat(snapped.Origin.X).IsGreater(49f);
	}

	[TestCase]
	public void TestZoomSensitivityScale()
	{
		AssertThat(GameInput.ZoomSensitivityScale(55f, enabled: true)).IsEqual(1f);
		AssertThat(GameInput.ZoomSensitivityScale(70f, enabled: true)).IsEqual(1f);
		AssertThat(GameInput.ZoomSensitivityScale(11.278f, enabled: false)).IsEqual(1f);

		float zoomScale = GameInput.ZoomSensitivityScale(11.278f, enabled: true);
		AssertThat(zoomScale).IsGreater(0.15f);
		AssertThat(zoomScale).IsLess(0.25f);

		// Very narrow FOV hits the 0.15x floor.
		AssertThat(GameInput.ZoomSensitivityScale(1f, enabled: true)).IsEqual(0.15f);
	}
}
