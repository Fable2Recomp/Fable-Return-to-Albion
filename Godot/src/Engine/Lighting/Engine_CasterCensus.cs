using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Refii.Lighting;

/// <summary>
/// What the shadow passes are made of: every shadow-casting instance under a root, grouped by the
/// engine node that owns it, with the draws it costs today and the draws it would cost if every
/// opaque, unanimated surface were cast through ONE material.
/// </summary>
/// <remarks>
/// The render thread is draw-call bound (measured 2026-09-21: 94 % on-CPU, ~2 us per call, 68 % of
/// the calls in shadow passes). Godot merges adjacent draws of one mesh surface only under one
/// material, and its own shared shadow material is refused to any shader that writes VERTEX,
/// discards, or is not back-face culled — which is every lit shader here. So today a shadow draw
/// is one per surface per instance. `sharedDraws` is the distinct (mesh, surface) count among the
/// surfaces that need nothing from their own material to cast: not alpha-tested, not wind-bent,
/// not skinned.
/// </remarks>
public static class Engine_CasterCensus
{
	private static readonly StringName AlphaScissor = "alpha_scissor_on";

	private static readonly StringName WindAmplitude = "wind_amplitude";

	private sealed class Group
	{
		public int Instances;
		public int Casting;
		public int ShadowsOnly;
		public int Skinned;
		public int MultiMeshes;
		public long MultiMeshInstances;
		public int CastingSurfaces;
		public int OwnMaterialSurfaces;
		public readonly HashSet<(ulong Mesh, int Surface)> Shareable = new();
		public readonly HashSet<ulong> Meshes = new();
		public readonly HashSet<ulong> Materials = new();
	}

	/// <summary>Walks <paramref name="root"/>. Debug use: it reads shader parameters, which cross to the server.</summary>
	public static Dictionary<string, object?> Describe(Node root)
	{
		var groups = new Dictionary<string, Group>();
		var ownMaterial = new Dictionary<ulong, bool>();
		Walk(root, "(root)", groups, ownMaterial);

		var rows = new List<Dictionary<string, object?>>();
		foreach ((string owner, Group g) in groups.OrderByDescending(p => p.Value.CastingSurfaces))
		{
			rows.Add(new Dictionary<string, object?>
			{
				["owner"] = owner,
				["instances"] = g.Instances,
				["casting"] = g.Casting,
				["shadowsOnly"] = g.ShadowsOnly,
				["skinned"] = g.Skinned,
				["multiMeshes"] = g.MultiMeshes,
				["multiMeshInstances"] = g.MultiMeshInstances,
				["castingSurfaces"] = g.CastingSurfaces,
				["ownMaterialSurfaces"] = g.OwnMaterialSurfaces,
				["sharedDraws"] = g.Shareable.Count + g.OwnMaterialSurfaces,
				["distinctMeshes"] = g.Meshes.Count,
				["distinctMaterials"] = g.Materials.Count,
			});
		}

		return new Dictionary<string, object?>
		{
			["castingSurfaces"] = groups.Values.Sum(g => g.CastingSurfaces),
			["sharedDraws"] = groups.Values.Sum(g => g.Shareable.Count + g.OwnMaterialSurfaces),
			["groups"] = rows,
		};
	}

	private static readonly Dictionary<ulong, (Transform3D At, Aabb Box)> Snapshot = new();

	private static ulong _snapshotFrame;

	/// <summary>
	/// Which instances MOVED or changed bounds since the previous call, by owner: each is one the
	/// renderer re-pairs with everything its box overlaps (`PairInstances::pair`), and a light
	/// among them re-pairs with every mesh in its range. The first call only takes the snapshot.
	/// Skeleton writes are not seen here; `skinned` in <see cref="Describe"/> counts those.
	/// </summary>
	public static Dictionary<string, object?> Moving(Node root)
	{
		var now = new Dictionary<ulong, (Transform3D At, Aabb Box)>();
		var moved = new Dictionary<string, (int Instances, int Moved, int Lights, int LightsMoved, float LargestMoved, string Example)>();
		Visit(root, "(root)", now, moved);

		ulong frame = Engine.GetProcessFrames();
		var rows = new List<Dictionary<string, object?>>();
		foreach ((string owner, var m) in moved.OrderByDescending(p => p.Value.Moved + (p.Value.LightsMoved * 100)))
		{
			rows.Add(new Dictionary<string, object?>
			{
				["owner"] = owner,
				["instances"] = m.Instances,
				["moved"] = m.Moved,
				["lights"] = m.Lights,
				["lightsMoved"] = m.LightsMoved,
				["largestMovedMetres"] = m.LargestMoved,
				["example"] = m.Example,
			});
		}

		var result = new Dictionary<string, object?>
		{
			["framesSincePrevious"] = _snapshotFrame == 0 ? null : frame - _snapshotFrame,
			["groups"] = rows,
		};

		Snapshot.Clear();
		foreach ((ulong id, var state) in now)
		{
			Snapshot[id] = state;
		}

		_snapshotFrame = frame;
		return result;
	}

	private static void Visit(Node node, string owner, Dictionary<ulong, (Transform3D At, Aabb Box)> now,
		Dictionary<string, (int Instances, int Moved, int Lights, int LightsMoved, float LargestMoved, string Example)> moved)
	{
		string? ns = node.GetType().Namespace;
		if (ns is not null && ns.StartsWith("Refii", System.StringComparison.Ordinal))
		{
			owner = node.GetType().Name;
		}

		if (node is VisualInstance3D visual && visual.IsVisibleInTree())
		{
			ulong id = visual.GetInstanceId();
			// A batch's bounds are its custom AABB; `GetAabb()` on one walks every copy.
			Aabb box = visual is MultiMeshInstance3D { Multimesh: { } batch } ? batch.CustomAabb : visual.GetAabb();
			(Transform3D At, Aabb Box) state = (visual.GlobalTransform, box);
			now[id] = state;
			bool light = visual is Light3D;
			bool changed = Snapshot.TryGetValue(id, out var before) && (before.At != state.At || before.Box != state.Box);
			moved.TryGetValue(owner, out var m);
			m.Instances += light ? 0 : 1;
			m.Lights += light ? 1 : 0;
			if (changed)
			{
				m.Moved += light ? 0 : 1;
				m.LightsMoved += light ? 1 : 0;
				float size = state.Box.Size.Length();
				if (size >= m.LargestMoved)
				{
					m.LargestMoved = size;
					m.Example = $"{visual.GetClass()} {visual.Name}";
				}
			}

			moved[owner] = m;
		}

		foreach (Node child in node.GetChildren())
		{
			Visit(child, owner, now, moved);
		}
	}

	private static readonly string[] TextureSlots = { "albedo_tex", "normal_tex", "orm_tex", "specular_tex", "emission_tex" };

	private static readonly string[] SwitchNames =
		{ "alpha_scissor_on", "premultiplied_alpha", "foliage", "foliage_normals", "has_envmap" };

	/// <summary>
	/// What a shared-texture-array material would have to hold: per texture slot, how many distinct
	/// textures the drawn surfaces bind and how they fall into (size, format) buckets — an array's
	/// layers must agree on both, and resizing one to fit is a change of look — and how many
	/// distinct settings of the non-texture switches there are, since each is a separate shader
	/// path. `draws` is surfaces drawn today; `meshDraws` is what one surface per mesh would be.
	/// </summary>
	public static Dictionary<string, object?> Materials(Node root)
	{
		var slots = new Dictionary<string, Dictionary<ulong, string>>();
		foreach (string slot in TextureSlots)
		{
			slots[slot] = new Dictionary<ulong, string>();
		}

		var kinds = new Dictionary<string, int>();
		var materials = new HashSet<ulong>();
		int draws = 0;
		int meshDraws = 0;
		int wind = 0;

		void Visit(Node node)
		{
			foreach (Node child in node.GetChildren())
			{
				Visit(child);
			}

			if (node is not GeometryInstance3D geometry || !geometry.IsVisibleInTree() ||
				geometry.CastShadow == GeometryInstance3D.ShadowCastingSetting.ShadowsOnly)
			{
				return;
			}

			Mesh? mesh = geometry is MeshInstance3D single ? single.Mesh
				: geometry is MultiMeshInstance3D { Multimesh: { } batch } ? batch.Mesh : null;
			if (mesh is null)
			{
				return;
			}

			++meshDraws;
			for (int s = 0; s < mesh.GetSurfaceCount(); s++)
			{
				++draws;
				if (mesh.SurfaceGetMaterial(s) is not ShaderMaterial shaded || !materials.Add(shaded.GetInstanceId()))
				{
					continue;
				}

				foreach (string slot in TextureSlots)
				{
					if (shaded.GetShaderParameter(slot).AsGodotObject() is Texture2D texture)
					{
						string format = texture is ImageTexture image ? image.GetFormat().ToString() : texture.GetClass();
						slots[slot][texture.GetInstanceId()] = $"{texture.GetWidth()}x{texture.GetHeight()} {format}";
					}
				}

				var kind = new System.Text.StringBuilder();
				foreach (string name in SwitchNames)
				{
					Variant value = shaded.GetShaderParameter(name);
					kind.Append(value.VariantType == Variant.Type.Bool && value.AsBool() ? '1' : '0');
				}

				Variant amplitude = shaded.GetShaderParameter("wind_amplitude");
				bool sways = amplitude.VariantType == Variant.Type.Float && amplitude.AsDouble() > 0.0;
				wind += sways ? 1 : 0;
				kind.Append(sways ? 'w' : '-');
				string key = kind.ToString();
				kinds[key] = kinds.GetValueOrDefault(key) + 1;
			}
		}

		Visit(root);

		var perSlot = new Dictionary<string, object?>();
		foreach ((string slot, Dictionary<ulong, string> textures) in slots)
		{
			var buckets = new Dictionary<string, int>();
			foreach (string bucket in textures.Values)
			{
				buckets[bucket] = buckets.GetValueOrDefault(bucket) + 1;
			}

			perSlot[slot] = new Dictionary<string, object?>
			{
				["textures"] = textures.Count,
				["buckets"] = buckets.OrderByDescending(b => b.Value).ToDictionary(b => b.Key, b => (object?)b.Value),
			};
		}

		return new Dictionary<string, object?>
		{
			["draws"] = draws,
			["meshDraws"] = meshDraws,
			["materials"] = materials.Count,
			["swaying"] = wind,
			["switchKinds (scissor,premult,foliage,foliageNormals,envmap,wind)"] =
				kinds.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => (object?)k.Value),
			["slots"] = perSlot,
		};
	}

	/// <summary>
	/// Placements whose transform MIRRORS the mesh (negative determinant), which turns its front
	/// faces into back faces. Godot flips the cull for a mirrored NODE, never for one copy inside a
	/// batch. Counts both, relative to the parent chain's own handedness, with examples.
	/// </summary>
	public static Dictionary<string, object?> Mirrored(Node root)
	{
		int nodes = 0, mirroredNodes = 0, batches = 0, copies = 0, mirroredCopies = 0, mixedBatches = 0;
		var examples = new List<string>();
		void Visit(Node node)
		{
			foreach (Node child in node.GetChildren())
			{
				Visit(child);
			}

			if (node is MeshInstance3D single && single.Mesh is not null)
			{
				++nodes;
				if (single.GlobalTransform.Basis.Determinant() * RootSign(root) < 0f)
				{
					++mirroredNodes;
				}
			}
			else if (node is MultiMeshInstance3D { Multimesh: { } batch } host &&
				host.CastShadow != GeometryInstance3D.ShadowCastingSetting.ShadowsOnly)
			{
				++batches;
				int count = batch.InstanceCount;
				float[] buffer = count > 0 ? batch.Buffer : System.Array.Empty<float>();
				int stride = count > 0 ? buffer.Length / count : 0;
				int flipped = 0;
				for (int i = 0; i < count && stride >= 12; i++)
				{
					int o = i * stride;
					var basis = new Basis(
						new Vector3(buffer[o], buffer[o + 4], buffer[o + 8]),
						new Vector3(buffer[o + 1], buffer[o + 5], buffer[o + 9]),
						new Vector3(buffer[o + 2], buffer[o + 6], buffer[o + 10]));
					float determinant = basis.Determinant();
					if (determinant < 0f)
					{
						++flipped;
					}
				}

				copies += count;
				mirroredCopies += flipped;
				if (flipped != 0 && flipped != count)
				{
					++mixedBatches;
				}

				if (flipped != 0 && examples.Count < 12)
				{
					examples.Add($"{host.Name} {flipped}/{count} at {host.GlobalTransform * batch.CustomAabb.GetCenter()}");
				}
			}
		}

		Visit(root);
		return new Dictionary<string, object?>
		{
			["nodes"] = nodes, ["mirroredNodes"] = mirroredNodes, ["batches"] = batches,
			["copies"] = copies, ["mirroredCopies"] = mirroredCopies, ["batchesMixingBoth"] = mixedBatches,
			["examples"] = examples,
		};
	}

	/// <summary>The handedness the world itself is drawn in: the region root mirrors X.</summary>
	private static float RootSign(Node root) => -1f;

	private static void Walk(Node node, string owner, Dictionary<string, Group> groups, Dictionary<ulong, bool> ownMaterial)
	{
		// The owner is the nearest ancestor that is one of OUR node types: the streamer, the twins,
		// the entity feed — the thing a fix would be made in.
		string? ns = node.GetType().Namespace;
		if (ns is not null && ns.StartsWith("Refii", System.StringComparison.Ordinal))
		{
			owner = node.GetType().Name;
		}

		if (node is GeometryInstance3D geometry && geometry.IsVisibleInTree())
		{
			Count(geometry, owner, groups, ownMaterial);
		}

		foreach (Node child in node.GetChildren())
		{
			Walk(child, owner, groups, ownMaterial);
		}
	}

	private static void Count(GeometryInstance3D geometry, string owner, Dictionary<string, Group> groups, Dictionary<ulong, bool> ownMaterial)
	{
		Mesh? mesh = null;
		bool skinned = false;
		long copies = 1;
		if (geometry is MeshInstance3D single)
		{
			mesh = single.Mesh;
			skinned = !single.Skeleton.IsEmpty && single.Skin is not null;
		}
		else if (geometry is MultiMeshInstance3D multi && multi.Multimesh is { } batch)
		{
			mesh = batch.Mesh;
			copies = batch.VisibleInstanceCount >= 0 ? batch.VisibleInstanceCount : batch.InstanceCount;
		}

		if (mesh is null)
		{
			return;
		}

		if (!groups.TryGetValue(owner, out Group? g))
		{
			groups[owner] = g = new Group();
		}

		++g.Instances;
		if (geometry is MultiMeshInstance3D)
		{
			++g.MultiMeshes;
			g.MultiMeshInstances += copies;
		}

		if (geometry.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off)
		{
			return;
		}

		++g.Casting;
		if (geometry.CastShadow == GeometryInstance3D.ShadowCastingSetting.ShadowsOnly)
		{
			++g.ShadowsOnly;
		}

		if (skinned)
		{
			++g.Skinned;
		}

		ulong meshId = mesh.GetInstanceId();
		g.Meshes.Add(meshId);
		int surfaces = mesh.GetSurfaceCount();
		for (int s = 0; s < surfaces; s++)
		{
			Material? material = geometry.MaterialOverride
				?? (geometry as MeshInstance3D)?.GetSurfaceOverrideMaterial(s)
				?? mesh.SurfaceGetMaterial(s);
			++g.CastingSurfaces;
			if (material is not null)
			{
				g.Materials.Add(material.GetInstanceId());
			}

			if (skinned || NeedsOwnMaterial(material, ownMaterial))
			{
				++g.OwnMaterialSurfaces;
			}
			else
			{
				g.Shareable.Add((meshId, s));
			}
		}
	}

	/// <summary>Whether the shadow pass needs this material itself: it discards by alpha or bends by wind.</summary>
	private static bool NeedsOwnMaterial(Material? material, Dictionary<ulong, bool> cache)
	{
		if (material is not ShaderMaterial shaded)
		{
			return material is BaseMaterial3D { Transparency: not BaseMaterial3D.TransparencyEnum.Disabled };
		}

		ulong id = shaded.GetInstanceId();
		if (cache.TryGetValue(id, out bool known))
		{
			return known;
		}

		Variant scissor = shaded.GetShaderParameter(AlphaScissor);
		Variant wind = shaded.GetShaderParameter(WindAmplitude);
		bool needs = (scissor.VariantType == Variant.Type.Bool && scissor.AsBool()) ||
			(wind.VariantType == Variant.Type.Float && wind.AsDouble() > 0.0);
		cache[id] = needs;
		return needs;
	}
}
