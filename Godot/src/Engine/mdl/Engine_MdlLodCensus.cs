using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Refii.Models;

/// <summary>
/// What each view's triangles are made of: every geometry instance the camera would draw,
/// the level Godot picks for each surface, and the triangles that costs — by owning node and by
/// mesh — beside what the guest's own per-object rule would have drawn.
/// </summary>
/// <remarks>
/// GODOT'S PICK, REPRODUCED. Godot 4.7.1 Mobile and Forward+ take the distance from the camera to
/// the NEAREST POINT of the instance's world box, times the projection's `get_lod_multiplier()`
/// (2 / P[0][0]) divided by the global rendering/scaling_3d/scale; `mesh_surface_get_lod`
/// walks the levels in ascending key and keeps each whose
/// `key · model scale / that` is under the viewport threshold (`mesh_lod_threshold / width`).
/// Width is the actual Window or SubViewport size, not a logical canvas override, the
/// stretch-adjusted Window texture accessor or a scaled 3D buffer. The
/// model scale is the transform's largest axis times `lod_bias`. Shadow passes pick with the same
/// main-camera distance, so this is their level too.
///
/// THE GUEST'S PICK is per object, from the object's own ORIGIN (`sub_82218560`, obj+320). Two
/// places differ: a MultiMesh is one instance to Godot, so every copy in a batch takes the level
/// of the batch box's nearest corner (`perObjectTriangles` re-picks each copy from its stated
/// transform); and a single mesh is measured to its box, not its origin (`originTriangles`).
/// Debug use: it walks the whole tree on the main thread.
/// </remarks>
public static class Engine_MdlLodCensus
{
	private sealed class Tally
	{
		public int Instances;
		public long Copies;
		public long Triangles;
		public long PerObjectTriangles;
		public long OriginTriangles;
		public long FullTriangles;
		public long NoLodTriangles;
		public double NearestMetres = double.MaxValue;

		/// <summary>Nearest box centre (what Godot measures a range to), the largest range end + margin, and how many have no range.</summary>
		public double NearestCentreMetres = double.MaxValue;
		public double RangeReachMax;
		public int Unranged;
	}

	private sealed class View
	{
		public required Vector3 Eye;
		public required Plane[] Frustum;
		public required float Multiplier;
		public required float Threshold;
		public required uint CullMask;
	}

	/// <summary>
	/// Walks <paramref name="root"/> against the viewport's current camera; on split screen against
	/// each player's view, since Godot picks every view's levels from its own camera and width.
	/// Player one's census is the top level and player two's is under <c>secondView</c>.
	/// </summary>
	public static Dictionary<string, object?> Describe(Node root, Viewport viewport)
	{
		if (!Refii.Core.Engine_Views.Split ||
			Refii.Core.Engine_Views.FirstViewport is not { } first || !GodotObject.IsInstanceValid(first) ||
			Refii.Core.Engine_Views.SecondViewport is not { } second || !GodotObject.IsInstanceValid(second))
		{
			return DescribeView(root, viewport);
		}

		Dictionary<string, object?> census = DescribeView(root, first);
		census["split"] = true;
		census["secondView"] = DescribeView(root, second);
		return census;
	}

	/// <summary>Walks <paramref name="root"/> against one viewport's current camera.</summary>
	private static Dictionary<string, object?> DescribeView(Node root, Viewport viewport)
	{
		if (viewport.GetCamera3D() is not { } camera)
		{
			return new Dictionary<string, object?> { ["error"] = "no 3D camera" };
		}

		Projection projection = camera.GetCameraProjection();
		Vector2 physicalSize = viewport is Window window ? window.Size
			: viewport is SubViewport sub ? sub.Size : viewport.GetVisibleRect().Size;
		Vector2 textureAccessorSize = viewport.GetTexture().GetSize();
		Vector2 logicalSize = viewport.GetVisibleRect().Size;
		float width = physicalSize.X;
		float projectScale = ProjectSettings.GetSetting("rendering/scaling_3d/scale", 1.0f).AsSingle();
		var view = new View
		{
			Eye = camera.GlobalPosition,
			Frustum = camera.GetFrustum().ToArray(),
			Multiplier = Engine_MeshLodPolicy.DistanceMultiplier(projection.X.X, projectScale),
			Threshold = Engine_MeshLodPolicy.NormalizedThreshold(viewport.MeshLodThreshold, width),
			CullMask = camera.CullMask,
		};

		var owners = new Dictionary<string, Tally>();
		var meshes = new Dictionary<string, Tally>();
		Walk(root, "(root)", view, owners, meshes);

		Tally all = new();
		foreach (Tally t in owners.Values)
		{
			all.Instances += t.Instances;
			all.Copies += t.Copies;
			all.Triangles += t.Triangles;
			all.PerObjectTriangles += t.PerObjectTriangles;
			all.OriginTriangles += t.OriginTriangles;
			all.FullTriangles += t.FullTriangles;
			all.NoLodTriangles += t.NoLodTriangles;
		}

		return new Dictionary<string, object?>
		{
			["meshLodThreshold"] = viewport.MeshLodThreshold,
			["viewportWidth"] = width,
			["viewportHeight"] = physicalSize.Y,
			["logicalWidth"] = logicalSize.X,
			["logicalHeight"] = logicalSize.Y,
			["viewportExtentScope"] = "Actual CanvasItems Window.Size or SubViewport.Size; not an internal 3D buffer readback",
			["viewportTextureAccessorWidth"] = textureAccessorSize.X,
			["viewportTextureAccessorHeight"] = textureAccessorSize.Y,
			["project3DScale"] = projectScale,
			["lodDistanceMultiplier"] = view.Multiplier,
			["normalizedThreshold"] = view.Threshold,
			["total"] = Row("all", all),
			["owners"] = owners.OrderByDescending(p => p.Value.Triangles).Select(p => Row(p.Key, p.Value)).ToList(),
			["meshes"] = meshes.OrderByDescending(p => p.Value.Triangles).Take(40).Select(p => Row(p.Key, p.Value)).ToList(),
		};
	}

	private static Dictionary<string, object?> Row(string name, Tally t) => new()
	{
		["name"] = name,
		["instances"] = t.Instances,
		["copies"] = t.Copies,
		["triangles"] = t.Triangles,
		["perObjectTriangles"] = t.PerObjectTriangles,
		["originTriangles"] = t.OriginTriangles,
		["fullDetailTriangles"] = t.FullTriangles,
		["noLodTriangles"] = t.NoLodTriangles,
		["nearestMetres"] = t.NearestMetres == double.MaxValue ? null : System.Math.Round(t.NearestMetres, 1),
		["nearestCentreMetres"] = t.NearestCentreMetres == double.MaxValue ? null : System.Math.Round(t.NearestCentreMetres, 1),
		["rangeReachMax"] = System.Math.Round(t.RangeReachMax, 1),
		["unranged"] = t.Unranged,
	};

	private static void Walk(Node node, string owner, View view, Dictionary<string, Tally> owners, Dictionary<string, Tally> meshes)
	{
		string? ns = node.GetType().Namespace;
		if (ns is not null && ns.StartsWith("Refii", System.StringComparison.Ordinal))
		{
			owner = node.GetType().Name;
		}

		if (node is GeometryInstance3D geometry && geometry.IsVisibleInTree() &&
			geometry.CastShadow != GeometryInstance3D.ShadowCastingSetting.ShadowsOnly &&
			(geometry.Layers & view.CullMask) != 0)
		{
			Count(geometry, owner, view, owners, meshes);
		}

		foreach (Node child in node.GetChildren())
		{
			Walk(child, owner, view, owners, meshes);
		}
	}

	private static void Count(GeometryInstance3D geometry, string owner, View view,
		Dictionary<string, Tally> owners, Dictionary<string, Tally> meshes)
	{
		Mesh? mesh;
		Aabb local;
		MultiMesh? batch = null;
		if (geometry is MeshInstance3D single && single.Mesh is { } m)
		{
			mesh = m;
			local = m.GetAabb();
		}
		else if (geometry is MultiMeshInstance3D multi && multi.Multimesh is { Mesh: { } bm } mm)
		{
			mesh = bm;
			batch = mm;
			local = mm.GetAabb();
		}
		else
		{
			return;
		}

		Transform3D at = geometry.GlobalTransform;
		Aabb box = at * local;
		if (!Inside(box, view.Frustum))
		{
			return;
		}

		float boxDistance = NearestDistance(box, view.Eye);
		// Godot's own test: camera to the box centre, against end + margin and begin - margin.
		float centre = view.Eye.DistanceTo(box.GetCenter());
		float end = geometry.VisibilityRangeEnd;
		float begin = geometry.VisibilityRangeBegin;
		if ((end > 0f && centre > end + geometry.VisibilityRangeEndMargin) ||
			(begin > 0f && centre < begin - geometry.VisibilityRangeBeginMargin))
		{
			return;
		}

		float scale = MaxScale(at.Basis) * geometry.LodBias;
		int surfaces = mesh.GetSurfaceCount();
		var levels = new (float Edge, int Triangles)[]?[surfaces];
		var full = new int[surfaces];
		for (int s = 0; s < surfaces; s++)
		{
			full[s] = mesh is ArrayMesh array ? array.SurfaceGetArrayIndexLen(s) / 3 : 0;
			levels[s] = Engine_MdlSurfaceArrays.LodsOf(mesh, s) ?? FromMeta(mesh, s);
		}

		long copies = 1;
		long triangles;
		long perObject;
		long origin;
		if (batch is null)
		{
			triangles = Pick(levels, full, scale, boxDistance, view);
			perObject = triangles;
			origin = Pick(levels, full, scale, view.Eye.DistanceTo(at.Origin), view);
		}
		else
		{
			copies = batch.VisibleInstanceCount >= 0 ? batch.VisibleInstanceCount : batch.InstanceCount;
			triangles = copies * Pick(levels, full, scale, boxDistance, view);
			perObject = 0;
			origin = 0;
			Transform3D[]? stated = Refii.Level.Engine_PropShadow.StatedTransforms(batch);
			Aabb meshBox = mesh.GetAabb();
			for (int i = 0; i < copies; i++)
			{
				Transform3D each = stated is not null && i < stated.Length ? at * stated[i] : at;
				float eachScale = MaxScale(each.Basis) * geometry.LodBias;
				perObject += Pick(levels, full, eachScale, NearestDistance(each * meshBox, view.Eye), view);
				origin += Pick(levels, full, eachScale, view.Eye.DistanceTo(each.Origin), view);
			}
		}

		long fullTriangles = copies * full.Sum();
		long noLod = 0;
		for (int s = 0; s < surfaces; s++)
		{
			if (levels[s] is null)
			{
				noLod += copies * full[s];
			}
		}

		string meshName = mesh.ResourceName.Length > 0 ? mesh.ResourceName
			: mesh.ResourcePath.Length > 0 ? mesh.ResourcePath
			: $"mesh#{mesh.GetInstanceId()}";
		foreach (Tally t in new[] { Get(owners, owner), Get(meshes, $"{meshName} [{owner}]") })
		{
			++t.Instances;
			t.Copies += copies;
			t.Triangles += triangles;
			t.PerObjectTriangles += perObject;
			t.OriginTriangles += origin;
			t.FullTriangles += fullTriangles;
			t.NoLodTriangles += noLod;
			t.NearestMetres = System.Math.Min(t.NearestMetres, boxDistance);
			t.NearestCentreMetres = System.Math.Min(t.NearestCentreMetres, centre);
			if (end > 0f)
			{
				t.RangeReachMax = System.Math.Max(t.RangeReachMax, end + geometry.VisibilityRangeEndMargin);
			}
			else
			{
				++t.Unranged;
			}
		}
	}

	/// <summary>`mesh_surface_get_lod`, summed over the surfaces: the triangles one copy draws at <paramref name="distance"/>.</summary>
	private static long Pick((float Edge, int Triangles)[]?[] levels, int[] full, float scale, float distance, View view)
	{
		float lodDistance = distance * view.Multiplier;
		long total = 0;
		for (int s = 0; s < full.Length; s++)
		{
			int drawn = full[s];
			if (levels[s] is { } table && view.Threshold > 0f && lodDistance > 0f)
			{
				foreach ((float edge, int tris) in table)
				{
					if (edge * scale / lodDistance > view.Threshold)
					{
						break;
					}

					drawn = tris;
				}
			}

			total += drawn;
		}

		return total;
	}

	private static (float Edge, int Triangles)[]? FromMeta(Mesh mesh, int surface)
	{
		if (Engine_MdlMesh.SurfaceLods(mesh, surface) is not { } lods)
		{
			return null;
		}

		var levels = new List<(float Edge, int Triangles)>();
		foreach (KeyValuePair<Variant, Variant> level in lods)
		{
			levels.Add((level.Key.AsSingle(), level.Value.AsInt32Array().Length / 3));
		}

		levels.Sort((a, b) => a.Edge.CompareTo(b.Edge));
		return levels.ToArray();
	}

	private static Tally Get(Dictionary<string, Tally> table, string key)
	{
		if (!table.TryGetValue(key, out Tally? t))
		{
			table[key] = t = new Tally();
		}

		return t;
	}

	private static float MaxScale(Basis basis) =>
		Mathf.Max(basis.X.Length(), Mathf.Max(basis.Y.Length(), basis.Z.Length()));

	/// <summary>Distance from the eye to the box's nearest point; zero inside it. Godot's `surface_distance`.</summary>
	private static float NearestDistance(Aabb box, Vector3 eye)
	{
		Vector3 min = box.Position;
		Vector3 max = box.Position + box.Size;
		Vector3 d = Vector3.Zero.Max(min - eye).Max(eye - max);
		return d.Length();
	}

	/// <summary>Box against Godot's frustum planes, whose normals point out.</summary>
	private static bool Inside(Aabb box, Plane[] frustum)
	{
		Vector3 min = box.Position;
		Vector3 max = box.Position + box.Size;
		foreach (Plane plane in frustum)
		{
			var nearest = new Vector3(
				plane.Normal.X > 0f ? min.X : max.X,
				plane.Normal.Y > 0f ? min.Y : max.Y,
				plane.Normal.Z > 0f ? min.Z : max.Z);
			if (plane.DistanceTo(nearest) > 0f)
			{
				return false;
			}
		}

		return true;
	}
}
