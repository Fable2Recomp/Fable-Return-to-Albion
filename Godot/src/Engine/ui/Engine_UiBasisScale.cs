using Godot;

namespace Refii.Ui;

/// <summary>
/// Sets one axis of a node's scale without going through <c>Node3D.Scale</c>.
///
/// `Basis::get_scale()` multiplies by <c>SIGN(determinant())</c>, and SIGN(0) is 0, so a
/// singular basis reports <c>(0, 0, 0)</c> for every axis. `hud.bgf` authors meter fills at
/// <c>scale.x = 0</c>, so <c>node.Scale = node.Scale with { X = value }</c> writes the artist's
/// thickness back as zero while reading back the value asked for. This is the one implementation.
///
/// Each column keeps its direction — where the element's rotation lives — and takes the new
/// length; a column with no direction left is rebuilt from the cross product of the other two
/// so the basis stays right-handed.
/// </summary>
public static class Engine_UiBasisScale
{
	/// <summary>Below this a column is treated as having no direction to preserve.</summary>
	private const float Degenerate = 0.000001f;

	/// <summary>The canvas axis to fall back on when a basis has no orientation left at all.</summary>
	private static Vector3 Fallback(int axis) => axis switch
	{
		0 => new Vector3(1f, 0f, 0f),
		1 => new Vector3(0f, 1f, 0f),
		_ => new Vector3(0f, 0f, 1f),
	};

	/// <summary>
	/// Returns <paramref name="basis"/> with one axis rescaled to <paramref name="length"/>.
	/// A non-finite length is refused, not clamped: `Mathf.Clamp` passes NaN through (every
	/// comparison against NaN is false), and a NaN basis gives a NaN AABB, a NaN render depth,
	/// and walks Godot's render-list sort off the array.
	/// </summary>
	public static Basis WithAxis(Basis basis, int axis, float length)
	{
		if (axis is < 0 or > 2 || !float.IsFinite(length))
		{
			return basis;
		}

		Vector3 column = basis[axis];
		if (column.LengthSquared() <= Degenerate)
		{
			column = basis[(axis + 1) % 3].Cross(basis[(axis + 2) % 3]);
		}

		basis[axis] = column.IsFinite() && column.LengthSquared() > Degenerate
			? column.Normalized() * length
			: Fallback(axis) * length;

		return basis;
	}

	/// <summary>Rescales one axis of a node in place, leaving its position and the other axes alone.</summary>
	public static void SetAxis(Node3D node, int axis, float length)
	{
		Transform3D transform = node.Transform;
		Basis after = WithAxis(transform.Basis, axis, length);
		if (after != transform.Basis)
		{
			node.Transform = new Transform3D(after, transform.Origin);
		}
	}

	/// <summary>The x axis — a bar's fill width, and the commonest case.</summary>
	public static void SetX(Node3D node, float length) => SetAxis(node, 0, length);

	/// <summary>The y axis — a bar's thickness, or a vertical meter's fill.</summary>
	public static void SetY(Node3D node, float length) => SetAxis(node, 1, length);
}
