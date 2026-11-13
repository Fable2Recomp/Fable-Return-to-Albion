using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;

namespace Refii.Entities;

/// <summary>
/// What a dressed character DRAWS: its mesh without the pieces its clothes hide, and with the
/// pieces of one material joined into one surface.
/// </summary>
/// <remarks>
/// A composite keeps a surface per region so a garment can hide a body cluster
/// (<see cref="Engine_HiddenClusters"/>), and a region costs a draw in the depth pre-pass and
/// another in the colour pass whether or not it is hidden — the collapse shader still submits
/// it. Once the hidden set is known, the regions it keeps that share a material are one surface
/// as far as the picture goes. The variant is built per (mesh, hidden set) and shared by every
/// body wearing the same outfit, as the shadow proxy's merge is.
///
/// Identical by construction: pieces join only when they share the Material OBJECT (finished
/// materials are shared by stated identity), carry the same vertex arrays, and switch LOD at the
/// same keys; and only under the opaque lit shaders, where the order of triangles inside one
/// surface cannot change a pixel.
///
/// The node shows the variant; everything that reads the body's surfaces by source index — the
/// rig, the hidden-cluster pass, the shadow proxy, the extent — asks <see cref="SourceMesh"/>.
/// </remarks>
public static class Engine_BodyVariants
{
	/// <summary>Debug lever: false shows every body's source mesh again.</summary>
	public static bool Enabled = true;

	/// <summary>Variants built, and source surfaces they removed (hidden or joined). Run totals.</summary>
	public static int Built;

	public static int SurfacesRemoved;

	private static readonly HashSet<string> MergeableShaders = new(StringComparer.Ordinal)
	{
		"res://src/Engine/material/standard_material.gdshader",
		"res://src/Engine/material/standard_material_culled.gdshader",
		"res://src/Engine/material/standard_material_wind.gdshader",
	};

	private static readonly ConditionalWeakTable<ArrayMesh, Dictionary<UInt128, ArrayMesh?>> Cache = new();

	/// <summary>Per node: the source it stands for and the variant it was given.</summary>
	private static readonly ConditionalWeakTable<MeshInstance3D, StrongBox<(ArrayMesh Source, ArrayMesh Variant)>> Shown = new();

	/// <summary>The source mesh a node stands for while it shows a variant; null otherwise.</summary>
	public static ArrayMesh? SourceOf(MeshInstance3D node) =>
		Shown.TryGetValue(node, out StrongBox<(ArrayMesh Source, ArrayMesh Variant)>? shown) &&
		ReferenceEquals(node.Mesh, shown.Value.Variant)
			? shown.Value.Source
			: null;

	/// <summary>The same, for a node whose mesh the caller has just read as <paramref name="mesh"/>.</summary>
	public static ArrayMesh? SourceOf(MeshInstance3D node, Mesh? mesh) =>
		Shown.TryGetValue(node, out StrongBox<(ArrayMesh Source, ArrayMesh Variant)>? shown) &&
		ReferenceEquals(mesh, shown.Value.Variant)
			? shown.Value.Source
			: null;

	/// <summary>The mesh whose surface order the body's bookkeeping uses: the source, whatever is shown.</summary>
	public static Mesh? SourceMesh(MeshInstance3D node) => SourceOf(node) ?? node.Mesh;

	/// <summary>
	/// The variant of <paramref name="source"/> with <paramref name="hidden"/> left out, or null
	/// when it would remove nothing or the lever is off; the node then shows the source.
	/// </summary>
	public static ArrayMesh? For(ArrayMesh source, UInt128 hidden)
	{
		if (!Enabled)
		{
			return null;
		}

		Dictionary<UInt128, ArrayMesh?> perMask = Cache.GetOrCreateValue(source);
		lock (perMask)
		{
			if (!perMask.TryGetValue(hidden, out ArrayMesh? variant))
			{
				variant = Build(source, hidden);
				perMask[hidden] = variant;
			}

			return variant;
		}
	}

	/// <summary>The source and variant <see cref="NoteShown"/> last recorded for <paramref name="node"/>; false when none.</summary>
	public static bool TryGetShown(MeshInstance3D node, out ArrayMesh? source, out ArrayMesh? variant)
	{
		if (Shown.TryGetValue(node, out StrongBox<(ArrayMesh Source, ArrayMesh Variant)>? shown))
		{
			(source, variant) = shown.Value;
			return true;
		}

		source = variant = null;
		return false;
	}

	/// <summary>
	/// Records that <paramref name="node"/> now shows <paramref name="variant"/> for <paramref name="source"/>.
	/// Engine_HiddenClusters, its one caller, keeps a copy of the entry in the node's record.
	/// </summary>
	public static void NoteShown(MeshInstance3D node, ArrayMesh source, ArrayMesh variant) =>
		Shown.AddOrUpdate(node, new StrongBox<(ArrayMesh Source, ArrayMesh Variant)>((source, variant)));

	private static ArrayMesh? Build(ArrayMesh source, UInt128 hidden)
	{
		int count = source.GetSurfaceCount();
		if (count > 128)
		{
			return null;
		}

		// Surfaces grouped in first-seen order; a surface that cannot join keeps a group of its own.
		var groups = new List<List<int>>();
		var byKey = new Dictionary<(Material Material, string Layout, string Lods), int>();
		int kept = 0;
		for (int s = 0; s < count; s++)
		{
			if ((hidden & (UInt128.One << s)) != UInt128.Zero)
			{
				continue;
			}

			kept++;
			Material? material = source.SurfaceGetMaterial(s);
			if (material is ShaderMaterial { Shader: { } shader } &&
				MergeableShaders.Contains(Refii.Models.Engine_ShaderVariant.FullPathOf(Refii.Models.Engine_ShaderSpecialisation.PathOf(shader))) &&
				source.SurfaceGetPrimitiveType(s) == Mesh.PrimitiveType.Triangles)
			{
				string layout = LayoutOf(source, s);
				if (!layout.Contains($"i{(int)Variant.Type.PackedInt32Array};", StringComparison.Ordinal) ||
					layout.Contains(":-1;", StringComparison.Ordinal))
				{
					groups.Add(new List<int> { s });
					continue;
				}

				var key = (material, layout, Refii.AndroidRuntime.AndroidBodyMerge.AnyLods ? string.Empty : LodKeysOf(source, s));
				if (byKey.TryGetValue(key, out int group))
				{
					groups[group].Add(s);
					continue;
				}

				byKey[key] = groups.Count;
			}

			groups.Add(new List<int> { s });
		}

		if (groups.Count == count)
		{
			return null;
		}

		if (kept == 0)
		{
			return new ArrayMesh { ResourceName = source.ResourceName };
		}

		var variant = new ArrayMesh { ResourceName = source.ResourceName };
		// The classic transfer data rides per surface, so it follows the kept surfaces (a joined
		// group is only ever a mergeable modern shader, which reads none).
		Godot.Collections.Array? prtDescriptors = source.HasMeta(Refii.Models.Engine_MdlTransfer.SurfaceDescriptorsMeta)
			? source.GetMeta(Refii.Models.Engine_MdlTransfer.SurfaceDescriptorsMeta).AsGodotArray() : null;
		Godot.Collections.Array? prtTables = source.HasMeta(Refii.Models.Engine_MdlTransfer.SurfaceTablesMeta)
			? source.GetMeta(Refii.Models.Engine_MdlTransfer.SurfaceTablesMeta).AsGodotArray() : null;
		var keptDescriptors = new Godot.Collections.Array();
		var keptTables = new Godot.Collections.Array();
		var pending = new List<(
			Mesh.PrimitiveType Primitive,
			Godot.Collections.Array Arrays,
			Godot.Collections.Dictionary? Lods,
			string Name,
			Material? Material)>(groups.Count);
		long totalBytes = 0;
		foreach (List<int> group in groups)
		{
			int first = group[0];
			Godot.Collections.Array arrays = group.Count == 1
				? Refii.Models.Engine_MdlSurfaceArrays.Of(source, first)
				: Joined(source, group, out _);
			Godot.Collections.Dictionary? lods = group.Count == 1
				? Refii.Models.Engine_MdlMesh.SurfaceLods(source, first)
				: Refii.AndroidRuntime.AndroidBodyMerge.AnyLods ? Refii.AndroidRuntime.AndroidBodyMerge.JoinedLods(source, group) : JoinedLods(source, group);
			totalBytes += Refii.Core.Engine_ArrayMeshes.EstimateSurfaceBytes(arrays);
			pending.Add((
				source.SurfaceGetPrimitiveType(first),
				arrays,
				lods,
				source.SurfaceGetName(first),
				source.SurfaceGetMaterial(first)));
			bool single = group.Count == 1;
			keptDescriptors.Add(single && prtDescriptors is not null && first < prtDescriptors.Count ? prtDescriptors[first] : System.Array.Empty<int>());
			keptTables.Add(single && prtTables is not null && first < prtTables.Count ? prtTables[first] : System.Array.Empty<float>());
		}
		Aabb sourceAabb = source.GetAabb();
		Refii.Core.Engine_ServerSyncs.RunGpuUpload(Math.Max(4096L, totalBytes), () =>
		{
			foreach (var item in pending)
			{
				int added = Refii.Models.Engine_MdlSurfaceArrays.AddDirect(
					variant, item.Primitive, item.Arrays, item.Lods);
				variant.SurfaceSetName(added, item.Name);
				variant.SurfaceSetMaterial(added, item.Material);
			}
			if (prtDescriptors is not null && prtTables is not null)
			{
				variant.SetMeta(Refii.Models.Engine_MdlTransfer.SurfaceDescriptorsMeta, keptDescriptors);
				variant.SetMeta(Refii.Models.Engine_MdlTransfer.SurfaceTablesMeta, keptTables);
				GC.KeepAlive(keptDescriptors);
				GC.KeepAlive(keptTables);
			}
			foreach (Godot.StringName whole in new[] { Refii.Models.Engine_MdlTransfer.DescriptorMeta, Refii.Models.Engine_MdlTransfer.TableMeta })
			{
				if (source.HasMeta(whole))
				{
					variant.SetMeta(whole, source.GetMeta(whole));
				}
			}

			// The rig and the regions ride on the source; the variant is only ever drawn.
			variant.CustomAabb = sourceAabb;
		});
		Built++;
		SurfacesRemoved += count - groups.Count;
		return variant;
	}

	/// <summary>Which array slots a surface fills and with what type: surfaces join only when these agree.</summary>
	private static string LayoutOf(ArrayMesh source, int surface)
	{
		Godot.Collections.Array arrays = Refii.Models.Engine_MdlSurfaceArrays.Of(source, surface);
		int vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
		var layout = new System.Text.StringBuilder();
		for (int slot = 0; slot < arrays.Count; slot++)
		{
			Variant value = arrays[slot];
			if (slot == (int)Mesh.ArrayType.Index)
			{
				// Only whether there is one: a surface drawn without indices cannot be offset into another.
				layout.Append("i").Append((int)value.VariantType).Append(';');
				continue;
			}

			layout.Append((int)value.VariantType).Append(':').Append(PerVertex(value, vertices)).Append(';');
		}

		return layout.ToString();
	}

	/// <summary>Elements per vertex of one array slot; -1 when it does not divide evenly.</summary>
	private static int PerVertex(Variant value, int vertices)
	{
		int length = value.VariantType switch
		{
			Variant.Type.PackedVector3Array => value.AsVector3Array().Length,
			Variant.Type.PackedVector2Array => value.AsVector2Array().Length,
			Variant.Type.PackedColorArray => value.AsColorArray().Length,
			Variant.Type.PackedFloat32Array => value.AsFloat32Array().Length,
			Variant.Type.PackedInt32Array => value.AsInt32Array().Length,
			Variant.Type.PackedByteArray => value.AsByteArray().Length,
			_ => 0,
		};
		return vertices == 0 || length % vertices != 0 ? -1 : length / vertices;
	}

	/// <summary>A surface's LOD keys, exactly; surfaces join only when they switch at the same keys.</summary>
	private static string LodKeysOf(ArrayMesh source, int surface)
	{
		Godot.Collections.Dictionary? lods = Refii.Models.Engine_MdlMesh.SurfaceLods(source, surface);
		if (lods is null)
		{
			return string.Empty;
		}

		var keys = new List<float>(lods.Count);
		foreach (Variant key in lods.Keys)
		{
			keys.Add(key.AsSingle());
		}

		keys.Sort();
		return string.Join(',', keys.ConvertAll(k => BitConverter.SingleToInt32Bits(k).ToString("X8")));
	}

	/// <summary>The group's arrays end to end, indices offset; <paramref name="offsets"/> is each surface's first vertex.</summary>
	private static Godot.Collections.Array Joined(ArrayMesh source, List<int> group, out int[] offsets)
	{
		var parts = new List<Godot.Collections.Array>(group.Count);
		foreach (int s in group)
		{
			parts.Add(Refii.Models.Engine_MdlSurfaceArrays.Of(source, s));
		}

		offsets = new int[group.Count];
		int total = 0;
		for (int i = 0; i < parts.Count; i++)
		{
			offsets[i] = total;
			total += parts[i][(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
		}

		var joined = new Godot.Collections.Array();
		joined.Resize(parts[0].Count);
		for (int slot = 0; slot < parts[0].Count; slot++)
		{
			Variant first = parts[0][slot];
			if (slot == (int)Mesh.ArrayType.Index)
			{
				var indices = new List<int>();
				for (int i = 0; i < parts.Count; i++)
				{
					foreach (int index in parts[i][slot].AsInt32Array())
					{
						indices.Add(offsets[i] + index);
					}
				}

				joined[slot] = indices.ToArray();
				continue;
			}

			joined[slot] = first.VariantType switch
			{
				Variant.Type.PackedVector3Array => Concat(parts, slot, v => v.AsVector3Array()),
				Variant.Type.PackedVector2Array => Concat(parts, slot, v => v.AsVector2Array()),
				Variant.Type.PackedColorArray => Concat(parts, slot, v => v.AsColorArray()),
				Variant.Type.PackedFloat32Array => Concat(parts, slot, v => v.AsFloat32Array()),
				Variant.Type.PackedInt32Array => Concat(parts, slot, v => v.AsInt32Array()),
				Variant.Type.PackedByteArray => Concat(parts, slot, v => v.AsByteArray()),
				_ => first,
			};
		}

		return joined;
	}

	private static Variant Concat<T>(List<Godot.Collections.Array> parts, int slot, Func<Variant, T[]> read)
	{
		var all = new List<T>();
		foreach (Godot.Collections.Array part in parts)
		{
			all.AddRange(read(part[slot]));
		}

		return Variant.From(all.ToArray());
	}

	/// <summary>Each shared key's level, joined with the same vertex offsets as the surface.</summary>
	private static Godot.Collections.Dictionary? JoinedLods(ArrayMesh source, List<int> group)
	{
		Godot.Collections.Dictionary? firstLods = Refii.Models.Engine_MdlMesh.SurfaceLods(source, group[0]);
		if (firstLods is null)
		{
			return null;
		}

		var offsets = new int[group.Count];
		int total = 0;
		for (int i = 0; i < group.Count; i++)
		{
			offsets[i] = total;
			total += Refii.Models.Engine_MdlSurfaceArrays.VertexCount(source, group[i]);
		}

		var joined = new Godot.Collections.Dictionary();
		foreach (Variant key in firstLods.Keys)
		{
			var level = new List<int>();
			for (int i = 0; i < group.Count; i++)
			{
				Godot.Collections.Dictionary lods = Refii.Models.Engine_MdlMesh.SurfaceLods(source, group[i])!;
				foreach (int index in lods[key].AsInt32Array())
				{
					level.Add(offsets[i] + index);
				}
			}

			joined[key] = level.ToArray();
		}

		return joined;
	}
}
