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
	private static bool? _stripSurfaceLods;

	/// <summary>
	/// Returns true when runtime <see cref="ArrayMesh"/> surface LOD index dictionaries must be
	/// omitted to avoid upstream Godot 4.7 / Intel Arc D3D12 GPU TDR (<c>0x887a0005</c>,
	/// <c>godotengine/godot#120857</c>) when rendering alpha-scissor surfaces with runtime LODs.
	/// Base-level geometry (LOD 0) is preserved in full; override with <c>REFII_D3D12_KEEP_SURFACE_LODS=1</c>.
	/// </summary>
	public static bool ShouldStripSurfaceLodsForD3d12(bool isD3d12, string? keepLodsEnv = null) =>
		isD3d12 && !string.Equals(keepLodsEnv?.Trim(), "1", StringComparison.Ordinal);

	private static bool StripSurfaceLods => _stripSurfaceLods ??= ShouldStripSurfaceLodsForD3d12(
		Refii.Textures.Engine_TextureUpload.IsD3D12Driver,
		System.Environment.GetEnvironmentVariable("REFII_D3D12_KEEP_SURFACE_LODS"));

	/// <summary>Adds a surface as <see cref="ArrayMesh.AddSurfaceFromArrays"/> does; see the class remarks.</summary>
	public static void AddSurface(
		ArrayMesh mesh, Mesh.PrimitiveType primitive, Godot.Collections.Array arrays,
		Godot.Collections.Array<Godot.Collections.Array>? blendShapes = null,
		Godot.Collections.Dictionary? lods = null, Mesh.ArrayFormat flags = 0)
	{
		if (Engine_RdHealth.IsDeviceLost)
		{
			Engine_RdHealth.NoteSuppressed();
			return;
		}

		Godot.Collections.Array<Godot.Collections.Array> shapes = blendShapes ?? NoBlendShapes;
		Godot.Collections.Dictionary levels = StripSurfaceLods || lods is null || lods.Count == 0 ? NoLods : lods;
		long estimatedBytes = EstimateSurfaceBytes(arrays);
		Engine_ServerSyncs.RunGpuUpload(estimatedBytes, () =>
		{
			if (Engine_RdHealth.IsDeviceLost)
			{
				Engine_RdHealth.NoteSuppressed();
				return;
			}

			int countBefore = mesh.GetSurfaceCount();
			mesh.AddSurfaceFromArrays(primitive, arrays, shapes, levels, flags);
			_ = Engine_RdHealth.CheckArrayMeshSurfaceAdded(mesh, countBefore, "Engine_ArrayMeshes.AddSurface");
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
		if (Engine_RdHealth.IsDeviceLost)
		{
			Engine_RdHealth.NoteSuppressed();
			return;
		}

		Godot.Collections.Array<Godot.Collections.Array> shapes = blendShapes ?? NoBlendShapes;
		Godot.Collections.Dictionary levels = StripSurfaceLods || lods is null || lods.Count == 0 ? NoLods : lods;
		int countBefore = mesh.GetSurfaceCount();
		mesh.AddSurfaceFromArrays(primitive, arrays, shapes, levels, flags);
		_ = Engine_RdHealth.CheckArrayMeshSurfaceAdded(mesh, countBefore, "Engine_ArrayMeshes.AddSurfaceDirect");
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
