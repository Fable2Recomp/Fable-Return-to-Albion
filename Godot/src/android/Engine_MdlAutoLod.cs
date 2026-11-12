using System;
using Godot;

namespace Refii.Models;

/// <summary>
/// Generated levels for model surfaces the 360 shipped without any (`mesh_auto_lod`, default on
/// for Android only): coarser index lists over the same vertices from Godot's SurfaceTool
/// (meshoptimizer), the way the terrain gets its own (Engine_LevelMesh, `terrain_lod`).
/// </summary>
/// <remarks>
/// WHY. A phone's GPU is bound by geometry, not pixels: on the Galaxy S25+ variable rate
/// shading at 2x2 took 3% off the frame while the terrain's generated levels took 9.5%, and the
/// S10e's Adreno 640 pays ~12 cycles a triangle. In Bowerstone Market 1.29M of the props' 1.68M
/// drawn triangles were in meshes with no level at all, a stall's 3,000 triangles drawn the
/// same at 80 m as at 2.
///
/// The levels sit at 0.2%, 0.8% and 3% of the surface's largest extent, keyed by that absolute
/// error as Godot's importer keys them, so a level draws once its error projects under
/// `mesh_lod_pixels`. The borders are locked, so a UV or material seam, which is a border in
/// the index list, keeps its vertices. A level that saves under 30% of the one before is not
/// kept. Skinned surfaces are left alone: their LODs would have to follow the bones. So are
/// trees, whose leaf cards a simplifier would shrink: they thin by `tree_card_lod`.
///
/// MEASURED, relaunch-paired, uncapped: on the S25+ (High) visible triangles 1.90M -> 1.46M
/// and GPU cycles -1 to -2%; on the S10e (Low) 1.54M -> 1.11M and -3.5%. The triangles
/// it removes are cheap ones, small distant props. No visible change at either spot.
/// </remarks>
public static class Engine_MdlAutoLod
{
	private static readonly float[] Errors = { 0.002f, 0.008f, 0.03f };

	/// <summary>Surfaces given generated levels since start-up.</summary>
	public static int Generated => System.Threading.Volatile.Read(ref _generated);

	private static int _generated;
	private static bool? _enabled;

	/// <summary>The launch setting; read once.</summary>
	public static bool Enabled => _enabled ??= ReadEnabled();

	private static bool ReadEnabled()
	{
		try
		{
			return Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), "mesh_auto_lod",
				Refii.Core.Engine_Platform.IsAndroid);
		}
		catch (Exception)
		{
			return Refii.Core.Engine_Platform.IsAndroid;
		}
	}

	/// <summary>The surface's generated levels, or null when it is too small or none is worth keeping.</summary>
	/// <param name="vertices">The surface's positions.</param>
	/// <param name="indexCount">How many indices it has.</param>
	/// <param name="surface">Its arrays so far, of which the positions, normals and indices (in the
	/// winding the surface is added with) are read.</param>
	internal static Engine_MdlSurfaceArrays.Levels? Generate(ReadOnlySpan<Vector3> vertices, int indexCount, Godot.Collections.Array surface)
	{
		if (indexCount < 600 || vertices.Length == 0)
		{
			return null;
		}

		Vector3 min = vertices[0], max = vertices[0];
		foreach (Vector3 v in vertices)
		{
			min = min.Min(v);
			max = max.Max(v);
		}

		Vector3 size = max - min;
		float extent = Math.Max(size.X, Math.Max(size.Y, size.Z));
		if (!(extent > 0f))
		{
			return null;
		}

		// Positions and normals only: the levels index the surface's own vertices, so nothing
		// else needs to reach the simplifier. They are the surface's own packed arrays, not copies.
		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		foreach (Mesh.ArrayType kept in new[] { Mesh.ArrayType.Vertex, Mesh.ArrayType.Normal, Mesh.ArrayType.Index })
		{
			arrays[(int)kept] = surface[(int)kept];
		}

		using var tool = new SurfaceTool();
		tool.CreateFromArrays(arrays);
		GC.KeepAlive(arrays);

		// COARSEST FIRST, because each run simplifies the whole surface again. With a larger error
		// meshoptimizer goes on along the same collapses, so a finer level never has fewer indices
		// than a coarser one: when a coarse level does not save the 30%, no finer one will.
		// Left null, it is what the walk below would have refused. A level of every index says
		// nothing: it is also what `generate_lod` returns when a run collapses the surface to
		// nothing, and a finer level can still be a good one then.
		var generated = new int[Errors.Length][];
		for (int e = Errors.Length - 1; e >= 0; e--)
		{
			// SurfaceTool.GenerateLod is intentionally used here for direct index-array simplification
			// per error threshold without round-tripping through ImporterMesh.GenerateLods.
#pragma warning disable CS0618
			int[] level = tool.GenerateLod(Errors[e], 3);
#pragma warning restore CS0618
			generated[e] = level;
			if (level.Length > indexCount * 7 / 10 && level.Length < indexCount)
			{
				break;
			}
		}

		Engine_MdlSurfaceArrays.Levels? levels = null;
		int previous = indexCount;
		for (int e = 0; e < Errors.Length; e++)
		{
			int[]? level = generated[e];
			if (level is null || level.Length < 3 || level.Length > previous * 7 / 10)
			{
				continue;
			}

			levels ??= new Engine_MdlSurfaceArrays.Levels();
			levels.Add(Errors[e] * extent, level);
			previous = level.Length;
		}

		if (levels is not null)
		{
			System.Threading.Interlocked.Increment(ref _generated);
		}

		return levels;
	}
}
