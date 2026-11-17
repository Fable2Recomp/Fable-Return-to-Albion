using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;

namespace Refii.Models;

/// <summary>
/// The arrays each model surface was built from, kept beside the mesh so nothing has to read
/// them back out of Godot.
/// </summary>
/// <remarks>
/// `ArrayMesh.SurfaceGetArrays` IS A GPU READBACK. Godot keeps no CPU copy of a surface: the
/// RD mesh storage answers it with one `buffer_get_data` per buffer — vertex, attribute, skin,
/// index, and every LOD's index list — and each of those calls
/// `_flush_and_stall_for_all_frames()` (`renderer_rd/storage_rd/mesh_storage.cpp`
/// `mesh_get_surface`, `rendering_device.cpp` `buffer_get_data`). Asked from the main thread
/// under a separate render thread it is also a synchronous round trip to that thread; asked
/// from a worker it stalls the render thread instead.
///
/// So every builder of a surface something later reads goes through <see cref="Add"/>, and
/// every reader goes through <see cref="Of"/>. A surface that was never recorded is read back
/// once, counted in <see cref="Readbacks"/> with the caller named in the log, and kept.
///
/// What is kept is the Godot array the builder already made, shallow-copied: its packed arrays
/// are copy-on-write, so a baked or morphed copy that shares a source's positions shares their
/// storage. <see cref="Of"/> hands out a shallow copy too, so a reader that replaces an element
/// (a composite remapping bones, a morph moving positions) cannot reach the record.
///
/// KEPT ON THE MESH, as metadata, not in a table keyed by the C# wrapper. A mesh loaded through
/// `ResourceLoader` on a worker can outlive the wrapper it was built through, and the next
/// access makes a new one, which a wrapper-keyed table would miss and read back from the GPU.
/// Metadata lives exactly as long as the mesh does.
/// </remarks>
public static class Engine_MdlSurfaceArrays
{
	private static readonly StringName ArraysMeta = "_refii_surface_arrays";
	private static readonly StringName LodsMeta = "_refii_surface_lod_levels";

	/// <summary>Serialises the metadata writes and reads; a mesh can be read on several workers at once.</summary>
	private static readonly object Gate = new();

	private static long _recordedBytes;
	private static int _readbacks;

	/// <summary>Surfaces read back from the GPU because no builder recorded them. Run total.</summary>
	public static int Readbacks => _readbacks;

	/// <summary>Estimated bytes of surface arrays recorded this run, including meshes since freed.</summary>
	public static long RecordedBytes => System.Threading.Interlocked.Read(ref _recordedBytes);

	/// <summary>
	/// Adds a surface to <paramref name="mesh"/> and records its arrays. The format carries the
	/// transfer channels (<see cref="Engine_MdlTransfer.FormatOf"/>) as every model surface must.
	/// Returns the new surface's index.
	/// </summary>
	public static int Add(
		ArrayMesh mesh, Mesh.PrimitiveType primitive, Godot.Collections.Array arrays,
		Godot.Collections.Dictionary? lods) =>
		Add(mesh, primitive, arrays, lods, null);

	/// <summary>
	/// <see cref="Add(ArrayMesh, Mesh.PrimitiveType, Godot.Collections.Array, Godot.Collections.Dictionary?)"/>
	/// for a builder that already has the level table <see cref="LodsOf"/> reads (<see cref="Levels"/>,
	/// or <see cref="LevelTable"/> of the surface it copies): finding a level's size in
	/// <paramref name="lods"/> would marshal the whole index array across just to count it.
	/// </summary>
	public static int Add(
		ArrayMesh mesh, Mesh.PrimitiveType primitive, Godot.Collections.Array arrays,
		Godot.Collections.Dictionary? lods, float[]? levelTable)
	{
		Godot.Collections.Array built = Refii.AndroidRuntime.AndroidLeanTreeStreams.ForGodot(arrays);
		int countBefore = mesh.GetSurfaceCount();
		Refii.Core.Engine_ArrayMeshes.AddSurface(
			mesh,
			primitive,
			built,
			null,
			lods,
			Engine_MdlTransfer.FormatOf(built) | Refii.AndroidRuntime.AndroidMeshCompression.FlagFor(primitive, built));
		int countAfter = mesh.GetSurfaceCount();
		if (countAfter <= countBefore)
		{
			return -1;
		}

		int added = countAfter - 1;
		Record(mesh, added, arrays.Duplicate(false), lods is null ? null : levelTable ?? LevelsOf(lods));
		return added;
	}

	/// <summary>A surface's `lods` dictionary as it is built, with the level table that goes beside it.</summary>
	public sealed class Levels
	{
		private readonly List<(float Edge, int Triangles)> _table = new();

		public Godot.Collections.Dictionary Lods { get; } = new();

		/// <summary>Adds one level's index array, replacing any level at the same key.</summary>
		public void Add(float key, System.ReadOnlySpan<int> indices)
		{
			using (Variant level = Variant.CreateFrom(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
				ref System.Runtime.InteropServices.MemoryMarshal.GetReference(indices), indices.Length)))
			{
				Lods[key] = level;
			}

			_table.RemoveAll(known => known.Edge == key);
			_table.Add((key, indices.Length / 3));
		}

		/// <summary>The table <see cref="Add(ArrayMesh, Mesh.PrimitiveType, Godot.Collections.Array, Godot.Collections.Dictionary?, float[]?)"/> takes.</summary>
		public float[] Packed() => Pack(_table);
	}

	/// <summary>
	/// Adds a surface directly when the caller is already inside <see cref="Refii.Core.Engine_ServerSyncs.RunGpuUpload"/>,
	/// and records its arrays.
	/// </summary>
	public static int AddDirect(
		ArrayMesh mesh, Mesh.PrimitiveType primitive, Godot.Collections.Array arrays,
		Godot.Collections.Dictionary? lods, float[]? levelTable = null)
	{
		Godot.Collections.Array built = Refii.AndroidRuntime.AndroidLeanTreeStreams.ForGodot(arrays);
		int countBefore = mesh.GetSurfaceCount();
		Refii.Core.Engine_ArrayMeshes.AddSurfaceDirect(
			mesh,
			primitive,
			built,
			null,
			lods,
			Engine_MdlTransfer.FormatOf(built) | Refii.AndroidRuntime.AndroidMeshCompression.FlagFor(primitive, built));
		int countAfter = mesh.GetSurfaceCount();
		if (countAfter <= countBefore)
		{
			return -1;
		}

		int added = countAfter - 1;
		Record(mesh, added, arrays.Duplicate(false), lods is null ? null : levelTable ?? LevelsOf(lods));
		return added;
	}

	/// <summary>
	/// A surface's coarser levels as Godot holds them — edge-length key and triangle count,
	/// ascending by key — or null when it was built with none or not through <see cref="Add"/>.
	/// </summary>
	public static (float Edge, int Triangles)[]? LodsOf(Mesh mesh, int surface)
	{
		if (LevelTable(mesh, surface) is not { } packed)
		{
			return null;
		}

		var levels = new (float Edge, int Triangles)[packed.Length / 2];
		for (int i = 0; i < levels.Length; i++)
		{
			levels[i] = (packed[i * 2], (int)packed[i * 2 + 1]);
		}

		return levels.Length == 0 ? null : levels;
	}

	/// <summary>A surface's recorded level table, edge key and triangle count in pairs; null when it has none.</summary>
	public static float[]? LevelTable(Mesh mesh, int surface)
	{
		if (surface < 0)
		{
			return null;
		}

		lock (Gate)
		{
			if (!mesh.HasMeta(LodsMeta) ||
				mesh.GetMeta(LodsMeta).AsGodotArray() is not { } all ||
				surface >= all.Count || all[surface].VariantType != Variant.Type.PackedFloat32Array)
			{
				return null;
			}

			return all[surface].AsFloat32Array();
		}
	}

	/// <summary>
	/// Records only a surface's level table (key and triangle count), for a builder that adds its
	/// surface itself and keeps no arrays: the ground's chunks (`terrain_lod`). Without it the
	/// lodcensus counts such a surface at full detail at any distance.
	/// </summary>
	public static void RecordLevels(Mesh mesh, int surface, Godot.Collections.Dictionary? lods)
	{
		if (surface < 0 || LevelsOf(lods) is not { } packed)
		{
			return;
		}

		lock (Gate)
		{
			Godot.Collections.Array levels = mesh.HasMeta(LodsMeta)
				? mesh.GetMeta(LodsMeta).AsGodotArray()
				: new Godot.Collections.Array();
			while (levels.Count <= surface)
			{
				levels.Add(default(Variant));
			}

			levels[surface] = packed;
			mesh.SetMeta(LodsMeta, levels);
		}
	}

	/// <summary>Edge key and triangle count per level, ascending, packed in pairs.</summary>
	private static float[]? LevelsOf(Godot.Collections.Dictionary? lods)
	{
		if (lods is null || lods.Count == 0)
		{
			return null;
		}

		var levels = new List<(float Edge, int Triangles)>(lods.Count);
		foreach (KeyValuePair<Variant, Variant> level in lods)
		{
			levels.Add((level.Key.AsSingle(), level.Value.AsInt32Array().Length / 3));
		}

		return Pack(levels);
	}

	private static float[] Pack(List<(float Edge, int Triangles)> levels)
	{
		levels.Sort((a, b) => a.Edge.CompareTo(b.Edge));
		var packed = new float[levels.Count * 2];
		for (int i = 0; i < levels.Count; i++)
		{
			packed[i * 2] = levels[i].Edge;
			packed[i * 2 + 1] = levels[i].Triangles;
		}

		return packed;
	}

	/// <summary>
	/// A surface's arrays, as a copy the caller may modify. From the record when a builder made
	/// one; otherwise read back from the GPU once and recorded.
	/// </summary>
	public static Godot.Collections.Array Of(Mesh mesh, int surface, [CallerMemberName] string caller = "")
	{
		if (surface < 0)
		{
			return new Godot.Collections.Array();
		}

		lock (Gate)
		{
			if (mesh.HasMeta(ArraysMeta) &&
				mesh.GetMeta(ArraysMeta).AsGodotArray() is { } all &&
				surface < all.Count && all[surface].VariantType == Variant.Type.Array)
			{
				return all[surface].AsGodotArray().Duplicate(false);
			}
		}

		if (Refii.Core.Engine_RdHealth.IsDeviceLost)
		{
			Refii.Core.Engine_RdHealth.NoteSuppressed();
			return new Godot.Collections.Array();
		}

		int count = System.Threading.Interlocked.Increment(ref _readbacks);
		if (count <= 8 || (count & (count - 1)) == 0)
		{
			GD.Print($"[surface-arrays] readback #{count}: '{mesh.ResourceName}' surface {surface} " +
				$"was never recorded; read back from the GPU for {caller}");
		}

		Godot.Collections.Array read = Refii.Core.Engine_ServerSyncs.SurfaceArrays(mesh, surface);
		Record(mesh, surface, read.Duplicate(false), null);
		return read;
	}

	/// <summary>Drops a mesh's recorded arrays and keeps its level table; a later <see cref="Of"/> reads back from the GPU.</summary>
	public static void Forget(Mesh mesh)
	{
		lock (Gate)
		{
			if (mesh.HasMeta(ArraysMeta))
			{
				mesh.RemoveMeta(ArraysMeta);
			}
		}
	}

	/// <summary>A surface's vertex count, from the mesh's own bookkeeping. No readback.</summary>
	public static int VertexCount(Mesh mesh, int surface)
	{
		if (surface < 0)
		{
			return 0;
		}

		if (mesh is ArrayMesh array)
		{
			return Refii.Core.Engine_RdHealth.ShouldAcceptSurfaceIndex(surface, array.GetSurfaceCount())
				? array.SurfaceGetArrayLen(surface)
				: 0;
		}

		Godot.Collections.Array arrays = Of(mesh, surface);
		return arrays.Count > (int)Mesh.ArrayType.Vertex
			? arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length
			: 0;
	}

	/// <summary>True when the surface carries bone indices and weights. No readback.</summary>
	public static bool Skinned(Mesh mesh, int surface)
	{
		if (surface < 0)
		{
			return false;
		}

		if (mesh is ArrayMesh array)
		{
			if (!Refii.Core.Engine_RdHealth.ShouldAcceptSurfaceIndex(surface, array.GetSurfaceCount()))
			{
				return false;
			}
			Mesh.ArrayFormat format = array.SurfaceGetFormat(surface);
			return (format & Mesh.ArrayFormat.FormatBones) != 0 &&
				(format & Mesh.ArrayFormat.FormatWeights) != 0;
		}

		Godot.Collections.Array arrays = Of(mesh, surface);
		return arrays.Count > (int)Mesh.ArrayType.Weights &&
			arrays[(int)Mesh.ArrayType.Bones].VariantType != Variant.Type.Nil &&
			arrays[(int)Mesh.ArrayType.Weights].VariantType != Variant.Type.Nil;
	}

	private static void Record(Mesh mesh, int surface, Godot.Collections.Array arrays, float[]? lods)
	{
		if (surface < 0)
		{
			return;
		}

		long bytes = mesh is ArrayMesh array ? EstimatedBytes(array, surface) : 0;
		lock (Gate)
		{
			Godot.Collections.Array all = mesh.HasMeta(ArraysMeta)
				? mesh.GetMeta(ArraysMeta).AsGodotArray()
				: new Godot.Collections.Array();
			while (all.Count <= surface)
			{
				all.Add(default(Variant));
			}

			all[surface] = arrays;
			mesh.SetMeta(ArraysMeta, all);

			if (lods is not null)
			{
				Godot.Collections.Array levels = mesh.HasMeta(LodsMeta)
					? mesh.GetMeta(LodsMeta).AsGodotArray()
					: new Godot.Collections.Array();
				while (levels.Count <= surface)
				{
					levels.Add(default(Variant));
				}

				levels[surface] = lods;
				mesh.SetMeta(LodsMeta, levels);
			}
		}

		System.Threading.Interlocked.Add(ref _recordedBytes, bytes);
	}

	/// <summary>The surface's arrays at their managed widths, from its format and lengths.</summary>
	private static long EstimatedBytes(ArrayMesh mesh, int surface)
	{
		if (!Refii.Core.Engine_RdHealth.ShouldAcceptSurfaceIndex(surface, mesh.GetSurfaceCount()))
		{
			return 0;
		}

		Mesh.ArrayFormat format = mesh.SurfaceGetFormat(surface);
		long perVertex = 12;
		perVertex += (format & Mesh.ArrayFormat.FormatNormal) != 0 ? 12 : 0;
		perVertex += (format & Mesh.ArrayFormat.FormatTangent) != 0 ? 16 : 0;
		perVertex += (format & Mesh.ArrayFormat.FormatColor) != 0 ? 16 : 0;
		perVertex += (format & Mesh.ArrayFormat.FormatTexUV) != 0 ? 8 : 0;
		perVertex += (format & Mesh.ArrayFormat.FormatTexUV2) != 0 ? 8 : 0;
		perVertex += (format & Mesh.ArrayFormat.FormatBones) != 0 ? 16 : 0;
		perVertex += (format & Mesh.ArrayFormat.FormatWeights) != 0 ? 16 : 0;
		perVertex += (format & Mesh.ArrayFormat.FormatCustom0) != 0 ? 16 : 0;
		perVertex += (format & Mesh.ArrayFormat.FormatCustom1) != 0 ? 16 : 0;
		perVertex += (format & Mesh.ArrayFormat.FormatCustom2) != 0 ? 16 : 0;
		return perVertex * mesh.SurfaceGetArrayLen(surface) + 4L * mesh.SurfaceGetArrayIndexLen(surface);
	}
}
