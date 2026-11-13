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
	/// Per node, the source surfaces a cloth body draws instead (<see cref="Refii.Physics.Engine_ClothBody"/>),
	/// paired with whether that cloth body casts its own shadow.
	/// They are hidden on the body exactly as a covered cluster is, so the variant leaves them
	/// out and the collapse override covers them when the source is shown.
	/// </summary>
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MeshInstance3D, System.Runtime.CompilerServices.StrongBox<(UInt128 Mask, bool CastsShadow)>> _clothSurfaces = new();

	/// <summary>Sets, or with zero clears, the cloth-drawn surfaces of <paramref name="node"/> and re-presents it.</summary>
	public static void SetClothSurfaces(MeshInstance3D node, UInt128 mask, bool castsShadow = false)
	{
		if (mask == UInt128.Zero)
		{
			_clothSurfaces.Remove(node);
		}
		else
		{
			_clothSurfaces.AddOrUpdate(node, new System.Runtime.CompilerServices.StrongBox<(UInt128 Mask, bool CastsShadow)>((mask, castsShadow)));
		}

		bool hadState = _collapsedSurfaces.TryGetValue(node, out SurfaceState? state);
		if (hadState)
		{
			state!.Cloth = mask;
			state.ClothCastsShadow = mask != UInt128.Zero && castsShadow;
		}

		if (Engine_BodyVariants.SourceMesh(node) is not ArrayMesh source)
		{
			return;
		}

		if (!hadState)
		{
			state = NewState(node, 0ul);
			_collapsedSurfaces.Add(node, state);
		}

		Present(node, source, state!);
	}

	/// <summary>
	/// Per node, the surfaces the classic renderer draws only as distortion (a composite's
	/// displacement parts, GDB DisplacementMeshes): another node draws them on the displacement
	/// layer, so the body hides them as it hides the cloth's.
	/// </summary>
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MeshInstance3D, System.Runtime.CompilerServices.StrongBox<UInt128>> _displacedSurfaces = new();

	/// <summary>Sets, or with zero clears, the displacement-drawn surfaces of <paramref name="node"/> and re-presents it.</summary>
	public static void SetDisplacedSurfaces(MeshInstance3D node, UInt128 mask)
	{
		if (mask == UInt128.Zero)
		{
			_displacedSurfaces.Remove(node);
		}
		else
		{
			_displacedSurfaces.AddOrUpdate(node, new System.Runtime.CompilerServices.StrongBox<UInt128>(mask));
		}
		bool hadState = _collapsedSurfaces.TryGetValue(node, out SurfaceState? state);
		if (hadState)
		{
			state!.Displaced = mask;
		}
		if (Engine_BodyVariants.SourceMesh(node) is not ArrayMesh source)
		{
			return;
		}
		if (!hadState)
		{
			state = NewState(node, 0ul);
			_collapsedSurfaces.Add(node, state);
		}
		Present(node, source, state!);
	}

	/// <summary>Surfaces another node draws for this one: the cloth body's and the displacement parts'.</summary>
	private static UInt128 ElsewhereMask(MeshInstance3D node) =>
		ClothMask(node) | DisplacedMask(node);

	/// <summary>The surfaces the displacement parts draw for <paramref name="node"/>; zero when none.</summary>
	private static UInt128 DisplacedMask(MeshInstance3D node) =>
		_displacedSurfaces.TryGetValue(node, out System.Runtime.CompilerServices.StrongBox<UInt128>? displaced)
			? displaced.Value
			: UInt128.Zero;

	/// <summary>
	/// A node's record, with what the cloth and displacement tables hold for it copied in: the node's
	/// one lookup answers the masks (<see cref="SetClothSurfaces"/> and <see cref="SetDisplacedSurfaces"/>,
	/// those tables' only writers, keep the copies).
	/// </summary>
	private static SurfaceState NewState(MeshInstance3D node, ulong meshId)
	{
		var state = new SurfaceState { MeshId = meshId, Displaced = DisplacedMask(node) };
		if (_clothSurfaces.TryGetValue(node, out System.Runtime.CompilerServices.StrongBox<(UInt128 Mask, bool CastsShadow)>? cloth))
		{
			state.Cloth = cloth.Value.Mask;
			state.ClothCastsShadow = cloth.Value.CastsShadow;
		}

		if (Engine_BodyVariants.TryGetShown(node, out ArrayMesh? source, out ArrayMesh? variant))
		{
			state.ShownSource = source;
			state.ShownVariant = variant;
		}

		return state;
	}

	/// <summary>The surfaces a cloth body draws for <paramref name="node"/>; zero when none.</summary>
	public static UInt128 ClothMask(MeshInstance3D node) =>
		_clothSurfaces.TryGetValue(node, out System.Runtime.CompilerServices.StrongBox<(UInt128 Mask, bool CastsShadow)>? entry)
			? entry.Value.Mask
			: UInt128.Zero;

	/// <summary>The surfaces the clusters alone hide on <paramref name="node"/>, cloth aside.</summary>
	public static UInt128 ClusterMask(MeshInstance3D node) =>
		_collapsedSurfaces.TryGetValue(node, out SurfaceState? state) ? MaskOf(state) : UInt128.Zero;

	private static ShaderMaterial Collapsed => _collapsed ??= new ShaderMaterial
	{
		Shader = new Shader
		{
			Code = "shader_type spatial;\nrender_mode unshaded, shadows_disabled;\n" +
				"void vertex() { POSITION = vec4(2.0, 2.0, 2.0, 1.0); }\n" +
				"void fragment() { ALBEDO = vec3(0.0); }\n",
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

		/// <summary>The mesh these bits describe: a new mesh (a co-op hero re-rigged) starts over.</summary>
		public ulong MeshId;

		/// <summary>The node's entry in the cloth table (zero, false when it has none), kept by its writer.</summary>
		public UInt128 Cloth;

		public bool ClothCastsShadow;

		/// <summary>The node's entry in the displacement table (zero when it has none), kept by its writer.</summary>
		public UInt128 Displaced;

		/// <summary>
		/// The node's entry in <see cref="Engine_BodyVariants"/>' shown table (both null when it has
		/// none): <see cref="Present"/>, that entry's only writer, keeps the copy.
		/// </summary>
		public ArrayMesh? ShownSource;

		public ArrayMesh? ShownVariant;

		/// <summary><see cref="Engine_BodyVariants.SourceOf(MeshInstance3D, Mesh)"/> from the copy.</summary>
		public ArrayMesh? SourceOf(Mesh? shown) =>
			ShownVariant is not null && ReferenceEquals(shown, ShownVariant) ? ShownSource : null;

		/// <summary>
		/// What the last <see cref="Present"/> was asked with and left the node showing: asked the
		/// same again, it would write nothing (the variant is cached per source and mask, and the
		/// overrides already stand as <see cref="Written"/> says).
		/// </summary>
		public bool Presented;

		public ArrayMesh? PresentedSource;

		public Mesh? PresentedShows;

		public UInt128 PresentedHidden;

		public bool PresentedVariants;

		private ulong _bits;
		private bool[] _more = Array.Empty<bool>();

		public bool Get(int surface) =>
			surface < 64 ? (_bits & (1ul << surface)) != 0ul
				: surface - 64 < _more.Length && _more[surface - 64];

		public ulong Bits => _bits;

		/// <summary>No surface past the first 64 has ever been stated.</summary>
		public bool OnlyFirst64 => _more.Length == 0;

		/// <summary>No surface hidden.</summary>
		public bool IsClear => _bits == 0ul && Array.IndexOf(_more, true) < 0;

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
	public static UInt128 CollapsedMask(MeshInstance3D node) =>
		_collapsedSurfaces.TryGetValue(node, out SurfaceState? state)
			? MaskOf(state) | Elsewhere(state)
			: ElsewhereMask(node);

	/// <summary><see cref="ElsewhereMask"/> from the node's record (its copies of both tables).</summary>
	private static UInt128 Elsewhere(SurfaceState state) => state.Cloth | state.Displaced;

	/// <summary>
	/// Pure managed mask composition for <see cref="ShadowCollapsedMask"/>: folds covered body clusters
	/// and displacement surfaces unconditionally, and includes <paramref name="clothMask"/> ONLY when
	/// <paramref name="clothCastsOwnShadow"/> is true.
	/// </summary>
	public static UInt128 ComposeShadowCollapsedMask(
		UInt128 clusterMask,
		UInt128 displacedMask,
		UInt128 clothMask,
		bool clothCastsOwnShadow) =>
		clusterMask | displacedMask | (clothCastsOwnShadow ? clothMask : UInt128.Zero);

	/// <summary>
	/// The surfaces hidden on <paramref name="node"/>'s shadow caster proxy: covered body clusters
	/// and displacement surfaces, plus cloth-driven surfaces ONLY when the cloth body casts its own
	/// shadow. Rigged character cloth bodies have <c>CastShadow = Off</c>, so keeping their GPU-skinned
	/// garment surfaces in the shadow proxy preserves coats, skirts, dresses, and capes in character
	/// shadows without any engine call on the reconcile path.
	/// </summary>
	public static UInt128 ShadowCollapsedMask(MeshInstance3D node)
	{
		if (_collapsedSurfaces.TryGetValue(node, out SurfaceState? state))
		{
			// The node's record answers all three: its copies are the tables' entries.
			return ComposeShadowCollapsedMask(MaskOf(state), state.Displaced, state.Cloth, state.ClothCastsShadow);
		}

		UInt128 displacedMask = DisplacedMask(node);
		UInt128 clusterMask = UInt128.Zero;
		bool hasCloth = _clothSurfaces.TryGetValue(node, out System.Runtime.CompilerServices.StrongBox<(UInt128 Mask, bool CastsShadow)>? cloth);
		return ComposeShadowCollapsedMask(
			clusterMask,
			displacedMask,
			hasCloth ? cloth!.Value.Mask : UInt128.Zero,
			hasCloth && cloth!.Value.CastsShadow);
	}

	/// <summary>
	/// The mesh's part tables, its surface regions (<see cref="Engine_MdlMesh.SurfaceRegions"/>, cached
	/// per mesh for good) and its instance id, which never change after it is built.
	/// </summary>
	private sealed record PartTables(int[] SurfaceParts, uint[] PartHashes, ulong MeshId, string[] Regions);

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, PartTables> _partTables = new();

	/// <summary>
	/// Reads the entity's built parts and folds their hides onto the node's surfaces. Every
	/// surface is stated each call: hidden ones collapse, the rest draw their own material
	/// again. Returns the number of surfaces hidden.
	/// </summary>
	public static int Apply(MeshInstance3D node, uint entity)
	{
		// The node's mesh, read once for the pass: nothing below sets it before Present compares it.
		// The node's record answers what it shows a variant of (its copy of the variants' entry).
		Mesh? shown = node.Mesh;
		bool known = _collapsedSurfaces.TryGetValue(node, out SurfaceState? state);
		ArrayMesh? showsVariantOf = known ? state!.SourceOf(shown) : Engine_BodyVariants.SourceOf(node, shown);
		if ((showsVariantOf ?? shown) is not Mesh mesh)
		{
			return 0;
		}

		// The tables are the mesh's and never change after it is built; a meta read is a
		// crossing plus a wrapper, and this runs for every entity on the reconcile cadence. They
		// carry the mesh's regions, so a mesh that has them is one lookup, not two.
		if (_partTables.TryGetValue(mesh, out PartTables? tables))
		{
			if (tables.Regions.Length == 0 && (known ? Elsewhere(state!) : ElsewhereMask(node)) == UInt128.Zero)
			{
				return 0;
			}
		}
		else
		{
			string[] meshRegions = Engine_MdlMesh.SurfaceRegions(mesh);
			if (meshRegions.Length == 0 && (known ? Elsewhere(state!) : ElsewhereMask(node)) == UInt128.Zero)
			{
				return 0;
			}

			tables = new PartTables(
				mesh.HasMeta(Engine_MdlComposite.SurfacePartsMetaName)
					? mesh.GetMeta(Engine_MdlComposite.SurfacePartsMetaName).AsInt32Array()
					: Array.Empty<int>(),
				mesh.HasMeta(Engine_MdlComposite.PartHashesMetaName)
					? Array.ConvertAll(mesh.GetMeta(Engine_MdlComposite.PartHashesMetaName).AsInt64Array(), v => unchecked((uint)v))
					: Array.Empty<uint>(),
				mesh.GetInstanceId(),
				meshRegions);
			_partTables.AddOrUpdate(mesh, tables);
		}

		string[] regions = tables.Regions;
		int[] surfaceParts = tables.SurfaceParts;
		uint[] partHashes = tables.PartHashes;

		if (state is null || state.MeshId != tables.MeshId)
		{
			state = NewState(node, tables.MeshId);
			_collapsedSurfaces.AddOrUpdate(node, state);
		}

		// A mesh with no part table hides no surface whatever the guest's hides say (every surface
		// below needs its part's hash), so they are not read; with nothing hidden and nothing
		// hidden before, every surface is stated as it already stands.
		Dictionary<uint, HashSet<string>>? hidden = surfaceParts.Length != 0 && partHashes.Length != 0
			? HiddenFor(entity)
			: null;

		int count = 0;
		int surfaces = hidden is null && state.IsClear ? 0 : Math.Min(mesh.GetSurfaceCount(), regions.Length);
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

		Present(node, mesh as ArrayMesh, state, shown);
		return count;
	}

	/// <summary>One entity's last part-hides text and what it gave; null when it gave no parts.</summary>
	private sealed class HiddenRead
	{
		public byte[] Text = Array.Empty<byte>();
		public Dictionary<uint, HashSet<string>>? Hidden;
	}

	private static readonly Dictionary<uint, HiddenRead> _hiddenReads = new();

	/// <summary>
	/// <see cref="HiddenPerPart"/> of the entity's built parts, parsed again only when the
	/// guest's text changed: it is the same every reconcile until the entity is dressed again,
	/// and the parse was a string, a split and a set per part, per entity, per pass.
	/// </summary>
	private static Dictionary<uint, HashSet<string>>? HiddenFor(uint entity)
	{
		ReadOnlySpan<byte> text = HostBridge.GetEntityPartHidesText(entity);
		if (text.IsEmpty)
		{
			// No parts gives no hides, whatever was read before (every single-model prop): the entry
			// is only a parse cache, and an older one is still compared before it answers again.
			return null;
		}

		if (_hiddenReads.TryGetValue(entity, out HiddenRead? read))
		{
			if (text.SequenceEqual(read.Text.AsSpan()))
			{
				return read.Hidden;
			}
		}
		else
		{
			// Entity addresses are reused as the world streams; the text is compared each
			// time, so a stale entry is only memory, dropped wholesale past a bound.
			if (_hiddenReads.Count >= 4096)
			{
				_hiddenReads.Clear();
			}

			read = new HiddenRead();
			_hiddenReads[entity] = read;
		}

		List<HostBridge.GuestPartHides> parts = HostBridge.ParsePartHides(text);
		read.Text = text.ToArray();
		read.Hidden = parts.Count == 0 ? null : HiddenPerPart(parts);
		return read.Hidden;
	}

	/// <summary>
	/// Draws the hidden set: the body's variant without those surfaces when one saves anything,
	/// else the source with the collapse override on each. Overrides are cleared BEFORE a swap to
	/// the variant — a node keeps its overrides by index across a mesh change — and written from
	/// our own record, not `GetSurfaceOverrideMaterial` (a wrapper per call).
	/// </summary>
	private static void Present(MeshInstance3D node, ArrayMesh? source, SurfaceState state) =>
		Present(node, source, state, node.Mesh);

	/// <summary>The same, for a node whose mesh the caller has just read as <paramref name="shown"/>.</summary>
	private static void Present(MeshInstance3D node, ArrayMesh? source, SurfaceState state, Mesh? shown)
	{
		UInt128 hidden = MaskOf(state) | Elsewhere(state);
		bool variants = Engine_BodyVariants.Enabled;

		// ASKED AS LAST TIME, it writes nothing: the variant for this source and mask is cached for
		// good, the node still shows what that pass left it showing, and the overrides stand as
		// `Written` says (only this pass writes either). The reconcile's common case, with no lookup.
		if (state.Presented && ReferenceEquals(source, state.PresentedSource) &&
			ReferenceEquals(shown, state.PresentedShows) && hidden == state.PresentedHidden &&
			variants == state.PresentedVariants)
		{
			return;
		}

		state.Presented = true;
		state.PresentedSource = source;
		state.PresentedHidden = hidden;
		state.PresentedVariants = variants;
		ArrayMesh? variant = source is null ? null : Engine_BodyVariants.For(source, hidden);
		if (variant is not null)
		{
			state.PresentedShows = variant;
			if (!ReferenceEquals(shown, variant))
			{
				WriteOverrides(node, state, UInt128.Zero);
				Refii.AndroidRuntime.AndroidCharacterLevers.BeforeMeshSwap(node);
				node.Mesh = variant;
				Engine_BodyVariants.NoteShown(node, source!, variant);
				state.ShownSource = source;
				state.ShownVariant = variant;
				// The classic transfer pair is per surface index: rebuilt for the new mesh.
				Refii.Models.Engine_ClassicTransfer.Apply(node);
			}

			return;
		}

		state.PresentedShows = source ?? shown;
		if (source is not null && !ReferenceEquals(shown, source))
		{
			Refii.AndroidRuntime.AndroidCharacterLevers.BeforeMeshSwap(node);
			node.Mesh = source;
			state.Written = UInt128.Zero;
			Refii.Models.Engine_ClassicTransfer.Apply(node);
		}

		WriteOverrides(node, state, hidden);
	}

	private static void WriteOverrides(MeshInstance3D node, SurfaceState state, UInt128 wanted)
	{
		UInt128 changed = state.Written ^ wanted;
		if (changed == UInt128.Zero)
		{
			// Every override already stands as wanted: no count to ask the engine for.
			return;
		}

		int surfaces = Math.Min(128, node.GetSurfaceOverrideMaterialCount());
		for (int s = 0; s < surfaces && changed != UInt128.Zero; s++)
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
		if (state.OnlyFirst64)
		{
			// Nothing stated past surface 63 (every prop): the rest of the mask is zero.
			return mask;
		}

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
