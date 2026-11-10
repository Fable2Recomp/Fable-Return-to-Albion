using System;
using Godot;

namespace Refii.Core;

/// <summary>
/// <see cref="ArrayMesh.AddSurfaceFromArrays"/> with every argument held alive until Godot has
/// read it. Every surface the game adds goes through <see cref="AddSurface"/>.
/// </summary>
/// <remarks>
/// WHY. The binding hands Godot only each collection's native pointer. A default
/// (<c>null</c>) blend-shape list or LOD dictionary becomes a fresh <c>new()</c> whose sole
/// reference is the binding's own temporary, and a caller's arrays are often dead after the
/// call too. <see cref="Godot.Collections.Array"/> and <see cref="Godot.Collections.Dictionary"/>
/// have finalizers that free the native collection. Mono scans stacks conservatively and kept
/// such temporaries alive; the NativeAOT runtime (<c>export-apk.py --nativeaot</c>) tracks
/// liveness precisely, so a collection on another thread could finalize one while Godot was
/// still reading it. The phones crashed there inside libgodot during world loads (the
/// prefetch workers' <see cref="Refii.Models.Engine_MdlSurfaceArrays.Add"/> and the shadow
/// proxies' merges), about one load in six.
/// </remarks>
public static class Engine_ArrayMeshes
{
	/// <summary>Shared empty defaults: Godot only reads them, and their refcount is atomic.</summary>
	private static readonly Godot.Collections.Array<Godot.Collections.Array> NoBlendShapes = new();
	private static readonly Godot.Collections.Dictionary NoLods = new();

	/// <summary>Adds a surface as <see cref="ArrayMesh.AddSurfaceFromArrays"/> does; see the class remarks.</summary>
	public static void AddSurface(
		ArrayMesh mesh, Mesh.PrimitiveType primitive, Godot.Collections.Array arrays,
		Godot.Collections.Array<Godot.Collections.Array>? blendShapes = null,
		Godot.Collections.Dictionary? lods = null, Mesh.ArrayFormat flags = 0)
	{
		Godot.Collections.Array<Godot.Collections.Array> shapes = blendShapes ?? NoBlendShapes;
		Godot.Collections.Dictionary levels = lods ?? NoLods;
		long estimatedBytes = EstimateSurfaceBytes(arrays);
		Engine_ServerSyncs.RunGpuUpload(estimatedBytes, () =>
		{
			mesh.AddSurfaceFromArrays(primitive, arrays, shapes, levels, flags);
		});
		GC.KeepAlive(mesh);
		GC.KeepAlive(arrays);
		GC.KeepAlive(shapes);
		GC.KeepAlive(levels);
	}

	/// <summary>
	/// Adds a surface directly when the caller is already inside <see cref="Engine_ServerSyncs.RunGpuUpload"/>,
	/// keeping every argument alive across the interop call.
	/// </summary>
	public static void AddSurfaceDirect(
		ArrayMesh mesh, Mesh.PrimitiveType primitive, Godot.Collections.Array arrays,
		Godot.Collections.Array<Godot.Collections.Array>? blendShapes = null,
		Godot.Collections.Dictionary? lods = null, Mesh.ArrayFormat flags = 0)
	{
		Godot.Collections.Array<Godot.Collections.Array> shapes = blendShapes ?? NoBlendShapes;
		Godot.Collections.Dictionary levels = lods ?? NoLods;
		mesh.AddSurfaceFromArrays(primitive, arrays, shapes, levels, flags);
		GC.KeepAlive(mesh);
		GC.KeepAlive(arrays);
		GC.KeepAlive(shapes);
		GC.KeepAlive(levels);
	}

	public static long EstimateSurfaceBytes(Godot.Collections.Array arrays)
	{
		if (arrays.Count <= (int)Mesh.ArrayType.Vertex)
		{
			return 16384L;
		}

		long vertexCount = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
		long indexCount = arrays.Count > (int)Mesh.ArrayType.Index
			? arrays[(int)Mesh.ArrayType.Index].AsInt32Array().Length
			: 0L;
		return System.Math.Max(4096L, vertexCount * 48L + indexCount * 4L);
	}
}
