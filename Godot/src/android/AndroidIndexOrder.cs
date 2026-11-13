using System;
using Godot;

namespace Refii.Models;

/// <summary>
/// `android_optimize_index_order` (off by default while measured): an opaque triangle-list
/// surface's indices, and each of its levels', reordered for the post-transform vertex cache
/// (meshoptimizer, through <see cref="SurfaceTool.OptimizeIndicesForCache"/>). A tile-based GPU
/// shades a vertex again in every bin its triangles touch, so triangles that reuse vertices
/// close together in the list save invocations.
/// </summary>
/// <remarks>
/// ONLY INDICES CHANGE. The vertex arrays stay exactly as built, since the surface's recorded
/// arrays (<see cref="Engine_MdlSurfaceArrays"/>) and the source-vertex map are in that order; the
/// same triangles come back, winding kept, in a new order. A level indexes the same vertices, so
/// it is reordered the same way and keeps its size and key (the level table needs nothing).
/// TRANSLUCENT SURFACES ARE LEFT ALONE: a blended surface's triangles draw in list order and
/// that order is what blends correctly. A list that is not whole triangles, or indexes outside
/// its vertices, is left as it is. Runs where the surface is built, on the prefetch worker.
/// </remarks>
public static class AndroidIndexOrder
{
	public const string Key = "android_optimize_index_order";

	private static bool? _enabled;
	private static int _surfaces;
	private static long _ticks;

	/// <summary>Index lists reordered this run (surfaces and levels).</summary>
	public static int Reordered => System.Threading.Volatile.Read(ref _surfaces);

	/// <summary>Milliseconds spent reordering this run, over all threads.</summary>
	public static double Milliseconds => System.Threading.Interlocked.Read(ref _ticks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

	/// <summary>The launch setting, on a phone only; read once there and never off a phone.</summary>
	public static bool Enabled => Refii.Core.Engine_Platform.IsAndroid && (_enabled ??= ReadEnabled());

	private static bool ReadEnabled()
	{
		try
		{
			return Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), Key, false);
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// The hook the mesh builder calls with a finished surface: reorders <paramref name="arrays"/>'
	/// index array and each of <paramref name="levels"/>' lists in place when the key is on and the
	/// surface's diffuse is not translucent.
	/// </summary>
	public static void Apply(MdlMaterial? material, Godot.Collections.Array arrays, Engine_MdlSurfaceArrays.Levels? levels)
	{
		if (!Enabled)
		{
			return;
		}

		bool translucent = material is not null && Refii.Textures.Engine_TexLibrary.IsTranslucent(
			material.Diffuse, Engine_BnkMeshLoader.ReadTextureHeader);
		Apply(translucent, arrays, levels);
	}

	/// <summary>Reorders unless <paramref name="translucent"/>, whatever the setting says.</summary>
	public static void Apply(bool translucent, Godot.Collections.Array arrays, Engine_MdlSurfaceArrays.Levels? levels)
	{
		if (translucent || arrays[(int)Mesh.ArrayType.Vertex].VariantType != Variant.Type.PackedVector3Array)
		{
			return;
		}

		long start = System.Diagnostics.Stopwatch.GetTimestamp();
		Variant vertices = arrays[(int)Mesh.ArrayType.Vertex];
		if (arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.PackedInt32Array &&
			Reorder(vertices, arrays[(int)Mesh.ArrayType.Index].AsInt32Array()) is { } own)
		{
			arrays[(int)Mesh.ArrayType.Index] = own;
			System.Threading.Interlocked.Increment(ref _surfaces);
		}

		if (levels is not null)
		{
			foreach (Variant key in levels.Lods.Keys)
			{
				if (levels.Lods[key].VariantType == Variant.Type.PackedInt32Array &&
					Reorder(vertices, levels.Lods[key].AsInt32Array()) is { } level)
				{
					levels.Lods[key] = level;
					System.Threading.Interlocked.Increment(ref _surfaces);
				}
			}
		}

		System.Threading.Interlocked.Add(ref _ticks, System.Diagnostics.Stopwatch.GetTimestamp() - start);
	}

	/// <summary>
	/// The same triangles in cache order, or null when the list is not whole triangles over
	/// <paramref name="positions"/> (a PackedVector3Array variant) or Godot returned another size.
	/// </summary>
	public static int[]? Reorder(Variant positions, int[] indices)
	{
		int count = positions.AsVector3Array().Length;
		if (indices.Length < 3 || indices.Length % 3 != 0)
		{
			return null;
		}

		foreach (int index in indices)
		{
			if ((uint)index >= (uint)count)
			{
				return null;
			}
		}

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = positions;
		arrays[(int)Mesh.ArrayType.Index] = indices;
		using var tool = new SurfaceTool();
		tool.CreateFromArrays(arrays);
		tool.OptimizeIndicesForCache();
		Variant result = tool.CommitToArrays()[(int)Mesh.ArrayType.Index];
		return result.VariantType == Variant.Type.PackedInt32Array && result.AsInt32Array() is { } ordered &&
			ordered.Length == indices.Length ? ordered : null;
	}
}
