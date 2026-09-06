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
	/// Reads the entity's built parts and folds their hides onto the node's surfaces. Every
	/// surface is stated each call: hidden ones collapse, the rest draw their own material
	/// again. Returns the number of surfaces hidden.
	/// </summary>
	public static int Apply(MeshInstance3D node, uint entity)
	{
		if (node.Mesh is not Mesh mesh)
		{
			return 0;
		}

		string[] regions = Engine_MdlMesh.SurfaceRegions(mesh);
		if (regions.Length == 0)
		{
			return 0;
		}

		int[] surfaceParts = mesh.HasMeta(Engine_MdlComposite.SurfacePartsMeta)
			? mesh.GetMeta(Engine_MdlComposite.SurfacePartsMeta).AsInt32Array()
			: Array.Empty<int>();
		uint[] partHashes = mesh.HasMeta(Engine_MdlComposite.PartHashesMeta)
			? Array.ConvertAll(mesh.GetMeta(Engine_MdlComposite.PartHashesMeta).AsInt64Array(), v => unchecked((uint)v))
			: Array.Empty<uint>();

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

			bool isHidden = node.GetSurfaceOverrideMaterial(s) == Collapsed;
			if (hide != isHidden)
			{
				node.SetSurfaceOverrideMaterial(s, hide ? Collapsed : null);
			}

			if (hide)
			{
				count++;
			}
		}

		return count;
	}
}
