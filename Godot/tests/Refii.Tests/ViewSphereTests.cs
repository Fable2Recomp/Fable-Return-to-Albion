using GdUnit4;
using Godot;
using Refii.Entities;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Engine_ViewSphere: the shell's pose gate tests a sphere, not a point, against the planes.</summary>
[TestSuite]
public sealed class ViewSphereTests
{
	/// <summary>A unit box's six outward planes, as a frustum stand-in.</summary>
	private static Plane[] Box() => new[]
	{
		new Plane(Vector3.Right, 1f), new Plane(Vector3.Left, 1f),
		new Plane(Vector3.Up, 1f), new Plane(Vector3.Down, 1f),
		new Plane(Vector3.Back, 1f), new Plane(Vector3.Forward, 1f),
	};

	/// <summary>An origin outside the planes still counts when its sphere reaches back in.</summary>
	[TestCase]
	public void SphereReachingInsidePasses()
	{
		AssertBool(Engine_ViewSphere.InsideAll(Box(), new Vector3(4f, 0f, 0f), 0f, 0f)).IsFalse();
		AssertBool(Engine_ViewSphere.InsideAll(Box(), new Vector3(4f, 0f, 0f), 3.5f, 0f)).IsTrue();
		AssertBool(Engine_ViewSphere.InsideAll(Box(), new Vector3(4f, 0f, 0f), 2.5f, 0f)).IsFalse();
		AssertBool(Engine_ViewSphere.InsideAll(Box(), new Vector3(4f, 0f, 0f), 2.5f, 1f)).IsTrue();
	}

	/// <summary>A point inside every plane passes with no radius; past one plane it fails.</summary>
	[TestCase]
	public void PointUsesTheMarginOnly()
	{
		AssertBool(Engine_ViewSphere.InsideAll(Box(), Vector3.Zero, 0f, 0f)).IsTrue();
		AssertBool(Engine_ViewSphere.InsideAll(Box(), new Vector3(0f, 0f, -3f), 0f, 1.5f)).IsFalse();
		AssertBool(Engine_ViewSphere.InsideAll(Box(), new Vector3(0f, 0f, -3f), 0f, 2.5f)).IsTrue();
	}

	/// <summary>The centre follows the rig's transform and the radius its largest scale.</summary>
	[TestCase]
	public void ToWorldPlacesAndScales()
	{
		var rig = new Transform3D(Basis.Identity.Scaled(new Vector3(1f, 2f, -3f)), new Vector3(10f, 0f, 0f));
		(Vector3 centre, float radius) = Engine_ViewSphere.ToWorld(rig, new Vector3(1f, 1f, 1f), 2f);
		AssertThat(centre.X).IsEqualApprox(11f, 1e-4f);
		AssertThat(centre.Y).IsEqualApprox(2f, 1e-4f);
		AssertThat(centre.Z).IsEqualApprox(-3f, 1e-4f);
		AssertThat(radius).IsEqualApprox(6f, 1e-4f);
	}
}
