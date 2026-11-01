using Godot;

namespace Refii.Entities;

/// <summary>
/// A sphere against a view's frustum planes, for the shell's pose gate: in view unless the
/// centre lies more than radius plus margin outside one plane. Planes face outward, as
/// <see cref="Projection.GetProjectionPlane"/> builds them.
/// </summary>
public static class Engine_ViewSphere
{
	/// <summary>Whether the sphere at <paramref name="centre"/> reaches inside every plane.</summary>
	public static bool InsideAll(Plane[] planes, Vector3 centre, float radius, float margin)
	{
		float reach = radius + margin;
		foreach (Plane plane in planes)
		{
			if (plane.DistanceTo(centre) > reach)
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// A sphere in a rig's mesh space (the guest's model space through
	/// <c>Engine_EntitySkeleton.Swizzle</c>), placed by the rig node's world transform: the centre
	/// transformed, the radius scaled by the basis's largest axis scale.
	/// </summary>
	public static (Vector3 Centre, float Radius) ToWorld(Transform3D rigWorld, Vector3 centre, float radius)
	{
		Vector3 scale = rigWorld.Basis.Scale.Abs();
		return (rigWorld * centre, radius * Mathf.Max(scale.X, Mathf.Max(scale.Y, scale.Z)));
	}
}
