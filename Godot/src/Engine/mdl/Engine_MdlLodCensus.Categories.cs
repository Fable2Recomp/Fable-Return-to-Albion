using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Refii.Models;

/// <summary>
/// The lodcensus's `categories`: the main view's triangles by what draws them (terrain, trees,
/// plants, props single and batched, characters, effects, water, sky), per surface, and what
/// Godot's own counter reports for them.
/// </summary>
/// <remarks>
/// GODOT'S COUNTER UNDERCOUNTS BATCHES WITH LEVELS. `VIEWPORT_RENDER_INFO_PRIMITIVES_IN_FRAME` (the
/// performance monitor's and meas.py's visible primitives) adds a surface's drawn indices once per
/// instance, and multiplies by a MultiMesh's copies only on the path without levels
/// (render_forward_mobile.cpp `_fill_render_list`, 4.7.1; Forward+ the same): a batch of 200 props
/// with generated levels counts as one prop. `godotCounted` reproduces that, so the counter can be
/// read against `triangles`, which counts every copy.
/// </remarks>
public static partial class Engine_MdlLodCensus
{
	private sealed class CategoryTally
	{
		public int Surfaces;
		public long Copies;
		public long Triangles;
		public long FullTriangles;
		public long NoLodTriangles;
		public long GodotCounted;
	}

	/// <summary>The census in progress (main thread, debug only); null between walks.</summary>
	private static Dictionary<string, CategoryTally>? _categories;

	/// <summary>
	/// A surface's category from its build's path and what draws it. Pure.
	/// </summary>
	/// <param name="shaderPath">The material's shader path (a lite build's name), or empty.</param>
	/// <param name="skinned">The node is skinned: a character or creature.</param>
	/// <param name="plant">The material states `foliage` or `grass`.</param>
	/// <param name="batch">The node is a MultiMesh.</param>
	/// <param name="owner">The nearest Refii node type above it.</param>
	public static string Category(string shaderPath, bool skinned, bool plant, bool batch, string owner)
	{
		string path = shaderPath.ToLowerInvariant();
		string kind =
			skinned ? "characters"
			: path.Contains("/terrain/", StringComparison.Ordinal) ? "terrain"
			: path.Contains("water", StringComparison.Ordinal) ? "water"
			: path.Contains("sky", StringComparison.Ordinal) || path.Contains("atmosphere", StringComparison.Ordinal) ? "sky"
			: owner.Contains("Fx", StringComparison.Ordinal) || path.Contains("/fx/", StringComparison.Ordinal) ? "fx"
			: path.Contains("standard_material_tree", StringComparison.Ordinal) || path.Contains("tree_canopy", StringComparison.Ordinal) ? "trees"
			: path.Contains("standard_material_wind", StringComparison.Ordinal) || plant ? "plants"
			: path.Contains("standard_material", StringComparison.Ordinal) ? "props"
			: path.Length == 0 ? "other"
			: "other " + System.IO.Path.GetFileNameWithoutExtension(path);
		return batch ? kind + " batch" : kind;
	}

	/// <summary>Adds one instance's surfaces to <see cref="_categories"/>, at the levels Godot picks.</summary>
	private static void TallyCategories(GeometryInstance3D geometry, Mesh mesh, bool batch, string owner,
		(float Edge, int Triangles)[]?[] levels, int[] full, long copies, float scale, float distance, View view)
	{
		if (_categories is null)
		{
			return;
		}

		bool skinned = geometry is MeshInstance3D single && (single.Skin is not null || !single.Skeleton.IsEmpty);
		for (int s = 0; s < full.Length; s++)
		{
			Material? material = geometry.MaterialOverride
				?? (geometry is MeshInstance3D mi ? mi.GetSurfaceOverrideMaterial(s) : null)
				?? mesh.SurfaceGetMaterial(s);
			string path = string.Empty;
			bool plant = false;
			if (material is ShaderMaterial shaded)
			{
				path = Engine_ShaderSpecialisation.PathOf(shaded.Shader);
				plant = shaded.GetShaderParameter("foliage").AsBool() || shaded.GetShaderParameter("grass").AsBool();
			}

			string key = Category(path, skinned, plant, batch, owner);
			if (!_categories.TryGetValue(key, out CategoryTally? t))
			{
				_categories[key] = t = new CategoryTally();
			}

			bool hasLevels = levels[s] is not null && view.Threshold > 0f;
			long one = Pick(new[] { levels[s] }, new[] { full[s] }, scale, distance, view);
			++t.Surfaces;
			t.Copies += copies;
			t.Triangles += copies * one;
			t.FullTriangles += copies * full[s];
			t.NoLodTriangles += levels[s] is null ? copies * full[s] : 0;
			t.GodotCounted += hasLevels ? one : copies * full[s];
		}
	}

	/// <summary>The finished categories, largest first, with their total; clears the census.</summary>
	private static Dictionary<string, object?> TakeCategories()
	{
		Dictionary<string, CategoryTally> taken = _categories ?? new Dictionary<string, CategoryTally>();
		_categories = null;
		long triangles = taken.Values.Sum(t => t.Triangles);
		return new Dictionary<string, object?>
		{
			["triangles"] = triangles,
			["godotCounted"] = taken.Values.Sum(t => t.GodotCounted),
			["scope"] = "Per surface at Godot's level pick; godotCounted is what VIEWPORT_RENDER_INFO_PRIMITIVES_IN_FRAME adds (one copy for a MultiMesh surface with levels)",
			["rows"] = taken.OrderByDescending(p => p.Value.Triangles).Select(p => new Dictionary<string, object?>
			{
				["category"] = p.Key,
				["triangles"] = p.Value.Triangles,
				["share"] = triangles > 0 ? Math.Round((double)p.Value.Triangles / triangles, 3) : 0.0,
				["surfaces"] = p.Value.Surfaces,
				["copies"] = p.Value.Copies,
				["fullDetailTriangles"] = p.Value.FullTriangles,
				["noLodTriangles"] = p.Value.NoLodTriangles,
				["godotCounted"] = p.Value.GodotCounted,
			}).ToList(),
		};
	}
}
