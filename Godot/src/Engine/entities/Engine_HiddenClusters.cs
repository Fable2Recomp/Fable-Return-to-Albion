using System;
using System.Collections.Generic;
using Godot;
using Refii.Equipment;
using Refii.Gdb;
using Refii.Host;
using Refii.Models;

namespace Refii.Entities;

/// <summary>
/// What a dressed character's clothes hide. The guest's composite builder (`sub_8220DE88`)
/// gives every built part a cluster set (its GDB `ClustersCovered`, gender and open state
/// already chosen) and a `ClusterSortLayer`, and hides on each part the union of the sets of
/// every OTHER part whose layer is strictly greater — a coat over a shirt, never a coat over
/// itself. Its draw-list builder (`sub_82274CE0`) then skips every submesh whose region is in
/// that union. The host reads the parts' sets and layers as the guest built them, joins each
/// surface to its part through the composite's metadata, and collapses the covered surfaces
/// with a vertex shader that puts every vertex outside the clip volume: no fragments, no
/// shadow, no material swap to undo. A region named with several `;`-joined clusters hides
/// when any of them is covered.
/// </summary>
public static class Engine_HiddenClusters
{
	private static ShaderMaterial? _collapsed;

	/// <summary>Whether <paramref name="material"/> is the override that takes a surface away.</summary>
	public static bool IsCollapsed(Material? material) =>
		material is not null && ReferenceEquals(material, _collapsed);

	/// <summary>The override that takes a surface away, for the cloth body to hide its own copies.</summary>
	public static ShaderMaterial CollapsedMaterial => Collapsed;

	/// <summary>
	/// Per node, the source surfaces a cloth body draws instead (<see cref="Refii.Physics.Engine_ClothBody"/>).
	/// They are hidden on the body exactly as a covered cluster is, so the variant leaves them
	/// out and the collapse override covers them when the source is shown.
	/// </summary>
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MeshInstance3D, System.Runtime.CompilerServices.StrongBox<UInt128>> _clothSurfaces = new();

	/// <summary>Sets, or with zero clears, the cloth-drawn surfaces of <paramref name="node"/> and re-presents it.</summary>
	public static void SetClothSurfaces(MeshInstance3D node, UInt128 mask)
	{
		if (mask == UInt128.Zero)
		{
			_clothSurfaces.Remove(node);
		}
		else
		{
			_clothSurfaces.AddOrUpdate(node, new System.Runtime.CompilerServices.StrongBox<UInt128>(mask));
		}

		if (Engine_BodyVariants.SourceMesh(node) is not ArrayMesh source)
		{
			return;
		}

		if (!_collapsedSurfaces.TryGetValue(node, out SurfaceState? state))
		{
			state = new SurfaceState();
			_collapsedSurfaces.Add(node, state);
		}

		Present(node, source, state);
	}

	/// <summary>The surfaces a cloth body draws for <paramref name="node"/>; zero when none.</summary>
	public static UInt128 ClothMask(MeshInstance3D node) =>
		_clothSurfaces.TryGetValue(node, out System.Runtime.CompilerServices.StrongBox<UInt128>? mask)
			? mask.Value
			: UInt128.Zero;

	/// <summary>The surfaces the clusters alone hide on <paramref name="node"/>, cloth aside.</summary>
	public static UInt128 ClusterMask(MeshInstance3D node) =>
		_collapsedSurfaces.TryGetValue(node, out SurfaceState? state) ? MaskOf(state) : UInt128.Zero;

	private static ShaderMaterial Collapsed => _collapsed ??= new ShaderMaterial
	{
		Shader = new Shader
		{
			Code = "shader_type spatial;\nrender_mode unshaded, shadows_disabled;\n" +
				"void vertex() { POSITION = vec4(2.0, 2.0, 2.0, 1.0); }\n",
		},
	};

	private static readonly Dictionary<uint, ItemDetail?> Details = new();

	/// <summary>The cluster lists of one worn record, read once per hash from every database;
	/// the readout's view of what the GDB says, beside what the guest built.</summary>
	public static ItemDetail? DetailFor(uint hash)
	{
		if (Details.TryGetValue(hash, out ItemDetail? known))
		{
			return known;
		}

		ItemDetail? detail = Engine_GdbCatalog.All is { } all
			? Engine_ItemDetails.DetailForRecord(all, hash)
			: null;
		Details[hash] = detail;
		return detail;
	}

	/// <summary>
	/// Per part (by hash), the clusters hidden on it: the union over the other parts with a
	/// strictly greater layer. Parts the guest did not build (eyes) get nothing hidden.
	/// </summary>
	public static Dictionary<uint, HashSet<string>> HiddenPerPart(IReadOnlyList<HostBridge.GuestPartHides> parts)
	{
		var hidden = new Dictionary<uint, HashSet<string>>();
		foreach (HostBridge.GuestPartHides part in parts)
		{
			var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (HostBridge.GuestPartHides other in parts)
			{
				if (other.Hash == part.Hash || other.Layer <= part.Layer)
				{
					continue;
				}

				foreach (string name in other.Covers)
				{
					set.Add(name);
				}
			}

			hidden[part.Hash] = set;
		}

		return hidden;
	}

	private static bool Covered(string region, HashSet<string> set)
	{
		if (region.Length == 0 || set.Count == 0)
		{
			return false;
		}

		if (region.IndexOf(';') < 0)
		{
			return set.Contains(region);
		}

		foreach (string token in region.Split(';', StringSplitOptions.RemoveEmptyEntries))
		{
			if (set.Contains(token))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Which surfaces this pass hides, by SOURCE surface index. It is the only writer. The
	/// collapse overrides actually written on the node are <see cref="Written"/>: none while the
	/// node shows a variant (<see cref="Engine_BodyVariants"/>), which leaves those surfaces out.
	/// </summary>
	private sealed class SurfaceState
	{
		public UInt128 Written;

		private ulong _bits;
		private bool[] _more = Array.Empty<bool>();

		public bool Get(int surface) =>
			surface < 64 ? (_bits & (1ul << surface)) != 0ul
				: surface - 64 < _more.Length && _more[surface - 64];

		public ulong Bits => _bits;

		public void Set(int surface, bool hidden)
		{
			if (surface < 64)
			{
				if (hidden)
				{
					_bits |= 1ul << surface;
				}
				else
				{
					_bits &= ~(1ul << surface);
				}

				return;
			}

			int at = surface - 64;
			if (at >= _more.Length)
			{
				Array.Resize(ref _more, at + 1);
			}

			_more[at] = hidden;
		}
	}

	/// <summary>Per node, keyed weakly so a freed entity's record goes with it.</summary>
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MeshInstance3D, SurfaceState> _collapsedSurfaces = new();

	/// <summary>
	/// The surfaces <see cref="Apply"/> last collapsed on this node, as bits, the first 128 of
	/// them (the hero's body has 79). No engine call.
	/// </summary>
	public static UInt128 CollapsedMask(MeshInstance3D node)
	{
		UInt128 cloth = ClothMask(node);
		if (!_collapsedSurfaces.TryGetValue(node, out SurfaceState? state))
		{
			return cloth;
		}

		return MaskOf(state) | cloth;
	}

	private sealed record PartTables(int[] SurfaceParts, uint[] PartHashes);

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, PartTables> _partTables = new();

	/// <summary>
	/// Reads the entity's built parts and folds their hides onto the node's surfaces. Every
	/// surface is stated each call: hidden ones collapse, the rest draw their own material
	/// again. Returns the number of surfaces hidden.
	/// </summary>
	public static int Apply(MeshInstance3D node, uint entity)
	{
		if (Engine_BodyVariants.SourceMesh(node) is not Mesh mesh)
		{
			return 0;
		}

		string[] regions = Engine_MdlMesh.SurfaceRegions(mesh);
		if (regions.Length == 0 && ClothMask(node) == UInt128.Zero)
		{
			return 0;
		}

		// The tables are the mesh's and never change after it is built; a meta read is a
		// crossing plus a wrapper, and this runs for every entity on the reconcile cadence.
		if (!_partTables.TryGetValue(mesh, out PartTables? tables))
		{
			tables = new PartTables(
				mesh.HasMeta(Engine_MdlComposite.SurfacePartsMetaName)
					? mesh.GetMeta(Engine_MdlComposite.SurfacePartsMetaName).AsInt32Array()
					: Array.Empty<int>(),
				mesh.HasMeta(Engine_MdlComposite.PartHashesMetaName)
					? Array.ConvertAll(mesh.GetMeta(Engine_MdlComposite.PartHashesMetaName).AsInt64Array(), v => unchecked((uint)v))
					: Array.Empty<uint>());
			_partTables.AddOrUpdate(mesh, tables);
		}

		int[] surfaceParts = tables.SurfaceParts;
		uint[] partHashes = tables.PartHashes;

		if (!_collapsedSurfaces.TryGetValue(node, out SurfaceState? state))
		{
			state = new SurfaceState();
			_collapsedSurfaces.Add(node, state);
		}

		List<HostBridge.GuestPartHides> parts = HostBridge.GetEntityPartHides(entity);
		Dictionary<uint, HashSet<string>>? hidden = parts.Count == 0 ? null : HiddenPerPart(parts);

		int count = 0;
		int surfaces = Math.Min(mesh.GetSurfaceCount(), regions.Length);
		for (int s = 0; s < surfaces; s++)
		{
			bool hide = false;
			if (hidden is not null && s < surfaceParts.Length)
			{
				int part = surfaceParts[s];
				if (part >= 0 && part < partHashes.Length &&
					hidden.TryGetValue(partHashes[part], out HashSet<string>? set))
				{
					hide = Covered(regions[s], set);
				}
			}

			state.Set(s, hide);
			if (hide)
			{
				count++;
			}
		}

		Present(node, mesh as ArrayMesh, state);
		return count;
	}

	/// <summary>
	/// Draws the hidden set: the body's variant without those surfaces when one saves anything,
	/// else the source with the collapse override on each. Overrides are cleared BEFORE a swap to
	/// the variant — a node keeps its overrides by index across a mesh change — and written from
	/// our own record, not `GetSurfaceOverrideMaterial` (a wrapper per call).
	/// </summary>
	private static void Present(MeshInstance3D node, ArrayMesh? source, SurfaceState state)
	{
		UInt128 hidden = MaskOf(state) | ClothMask(node);
		ArrayMesh? variant = source is null ? null : Engine_BodyVariants.For(source, hidden);
		if (variant is not null)
		{
			if (!ReferenceEquals(node.Mesh, variant))
			{
				WriteOverrides(node, state, UInt128.Zero);
				node.Mesh = variant;
				Engine_BodyVariants.NoteShown(node, source!, variant);
				// The classic transfer pair is per surface index: rebuilt for the new mesh.
				Refii.Models.Engine_ClassicTransfer.Apply(node);
			}

			return;
		}

		if (source is not null && !ReferenceEquals(node.Mesh, source))
		{
			node.Mesh = source;
			state.Written = UInt128.Zero;
			Refii.Models.Engine_ClassicTransfer.Apply(node);
		}

		WriteOverrides(node, state, hidden);
	}

	private static void WriteOverrides(MeshInstance3D node, SurfaceState state, UInt128 wanted)
	{
		UInt128 changed = state.Written ^ wanted;
		for (int s = 0; s < 128 && changed != UInt128.Zero; s++)
		{
			UInt128 bit = UInt128.One << s;
			if ((changed & bit) != UInt128.Zero)
			{
				// Unhidden, a surface goes back to whatever lit it before: the classic transfer
				// pair when the classic renderer gave it one, else its own material.
				node.SetSurfaceOverrideMaterial(s, (wanted & bit) != UInt128.Zero
					? Collapsed
					: Refii.Models.Engine_ClassicTransfer.SurfaceOverride(node, s));
				changed &= ~bit;
			}
		}

		state.Written = wanted;
	}

	private static UInt128 MaskOf(SurfaceState state)
	{
		UInt128 mask = state.Bits;
		for (int surface = 64; surface < 128; surface++)
		{
			if (state.Get(surface))
			{
				mask |= UInt128.One << surface;
			}
		}

		return mask;
	}
}
