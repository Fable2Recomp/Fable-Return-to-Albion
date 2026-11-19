namespace Refii.Tests.Core;

using System;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Core;

[TestSuite]
public class EngineCameraProjectorTests
{
	[TestCase]
	public void TestTryProjectMatchesPerspectiveAndRejectsBehindNear()
	{
		var projector = new Engine_CameraProjector();
		var camPos = new Vector3(3f, 2f, 5f);
		var camBasis = new Basis(Vector3.Up, Mathf.DegToRad(25f));
		Transform3D camWorld = new Transform3D(camBasis, camPos);
		Transform3D view = camWorld.AffineInverse();

		foreach ((float w, float h) in new[] { (1920f, 1080f), (2560f, 1080f), (1280f, 960f), (960f, 1080f) })
		{
			var projection = Projection.CreatePerspective(55f, w / h, 0.1f, 500f, false);
			projector.Set(view, projection, new Vector2(w, h), 0.1f);

			Vector3 forward = -camBasis.Z;
			Vector3 right = camBasis.X;
			Vector3 up = camBasis.Y;

			// Point straight ahead at 10m projects to exact screen center.
			AssertThat(projector.TryProject(camPos + (forward * 10f), out Vector2 center, out float dNear)).IsTrue();
			AssertThat(MathF.Abs(center.X - (w * 0.5f))).IsLess(0.5f);
			AssertThat(MathF.Abs(center.Y - (h * 0.5f))).IsLess(0.5f);

			// Further ahead decreases reversed-Z depth (1 at near, 0 at far).
			AssertThat(projector.TryProject(camPos + (forward * 50f), out _, out float dFar)).IsTrue();
			AssertThat(dNear).IsGreater(dFar);
			AssertThat(dNear).IsGreater(0f);
			AssertThat(dFar).IsGreaterEqual(0f);

			// Point up and right lands in top-right quadrant (larger X, smaller Y in Y-down viewport pixels).
			Vector3 topRightWorld = camPos + (forward * 10f) + (right * 2f) + (up * 1.5f);
			AssertThat(projector.TryProject(topRightWorld, out Vector2 topRightPx, out _)).IsTrue();
			AssertThat(topRightPx.X).IsGreater(w * 0.5f);
			AssertThat(topRightPx.Y).IsLess(h * 0.5f);

			// Point behind camera or inside near plane is rejected.
			AssertThat(projector.TryProject(camPos - (forward * 2f), out _, out _)).IsFalse();
			AssertThat(projector.TryProject(camPos + (forward * 0.05f), out _, out _)).IsFalse();
		}
	}
}
