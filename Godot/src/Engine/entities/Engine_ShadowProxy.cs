using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;

namespace Refii.Entities;

/// <summary>
/// One draw per shadow pass for a character, instead of one per surface.
/// </summary>
/// <remarks>
/// /// A composed character is dozens of surfaces (the hero is 79), and every surface is a draw
/// /// call in EVERY shadow pass that reaches it: six cube faces for each fixture holding a map,
/// /// and each of the sun's cascades. A shadow pass reads no material, so the split buys nothing
/// /// there.
///
/// So a skinned body gets a child that casts and is never drawn — the SAME vertices on the SAME
/// skeleton and skin, every opaque surface merged into one — and the body itself stops casting.
/// The shadow is the same shape; the draws are not. What is kept apart:
///   - an ALPHA-TESTED surface (hair, lace) stays a surface of its own with its own material,
///     because the shadow pass must still run that material's discard;
///   - a surface <see cref="Engine_HiddenClusters"/> has collapsed is left out, as the body
///     does not draw or cast it.
/// A RIGID prop of two or more surfaces gets the same child without the skeleton. Its caster
/// flag is <c>Engine_ShadowReach</c>'s, so the proxy is what that policy is handed and the body
/// is reported through <see cref="IsProxied"/> as no longer its to write.
///
/// The merged surface carries LEVELS OF DETAIL: level k is every source surface's level k (its
/// full index list where it has none), keyed by the largest of their edge lengths, so a distant
/// caster costs the shadow passes what the body's own LODs did.
///
/// /// The proxy is also the shadow's CADENCE: Engine_ShadowSkeletons poses and places it on the
/// /// guest's frames while the body interpolates every displayed frame, so a lantern map holding a
/// /// character re-renders at the guest's rate, not the display's.
/// </remarks>
public static class Engine_ShadowProxy
{
	public const string NodeName = "ShadowProxy";

	/// <summary>Debug lever: false hands every body its own shadow back on its next <see cref="Sync"/>.</summary>
	public static bool Enabled = true;

	/// <summary>
	/// /// Every skinned body gets a proxy, whatever it saves in draws: the proxy is what lets a
	/// /// character's shadow follow at the guest's rate (Engine_ShadowSkeletons) while the body
	/// /// keeps interpolating.
	/// </summary>
	private const int MinSkinnedSurfaces = 1;

	/// <summary>Surfaces the hidden-cluster mask can express; a body past it is left alone.</summary>
	private const int MaxSurfaces = 128;

	private const int MinRigidSurfaces = 2;

	/// <summary>Bodies casting through a proxy, by instance id: not <c>Engine_ShadowReach</c>'s to write.</summary>
	private static readonly HashSet<ulong> Proxied = new();

	public static bool IsProxied(ulong body) => Proxied.Contains(body);

	/// <summary>For another owner of cast-only twins (the prop batches): whose caster flag the reach policy leaves alone.</summary>
	public static void MarkProxied(ulong instance, bool proxied)
	{
		if (proxied)
		{
			Proxied.Add(instance);
		}
		else
		{
			Proxied.Remove(instance);
		}
	}

	/// <summary>
	/// The cast-only mesh of a rigid model with nothing hidden, or null when merging saves
	/// nothing; <paramref name="merged"/> is how many surfaces became one. Cached per mesh.
	/// </summary>
	public static ArrayMesh? MergedCaster(ArrayMesh source, out int merged)
	{
		merged = 0;
		int surfaces = source.GetSurfaceCount();
		if (surfaces < MinRigidSurfaces || surfaces > 64 ||
			(source.SurfaceGetFormat(0) & Mesh.ArrayFormat.FormatBones) != 0)
		{
			return null;
		}

		ArrayMesh? caster = MergedFor(source, null, UInt128.Zero, skinned: false, always: false);
		if (caster is not null)
		{
			merged = surfaces - (caster.GetSurfaceCount() - 1);
		}

		return caster;
	}

	/// <summary>For a level change, where the bodies go away.</summary>
	public static void Forget() => Proxied.Clear();

	/// <summary>Bodies casting through a proxy NOW; then merged meshes built and the surfaces they stand for, as run totals.</summary>
	public static int Active;

	public static int Built;

	public static int SurfacesMerged;

	private static readonly StringName StateMeta = "shadow_proxy_state";

	private static readonly StringName AlphaScissor = "alpha_scissor_on";

	/// <summary>Per source mesh, per hidden-surface mask. Weak: a crowd shares meshes and they retire.</summary>
	private static readonly ConditionalWeakTable<Mesh, Dictionary<(UInt128 Hidden, bool Always), ArrayMesh?>> Cache = new();

	private static StandardMaterial3D? _caster;

	/// <summary>What the merged surface is drawn with: two-sided, as the bodies' own materials are.</summary>
	private static StandardMaterial3D Caster => _caster ??= new StandardMaterial3D
	{
		ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		CullMode = BaseMaterial3D.CullModeEnum.Disabled,
	};

	/// <summary>The hidden set each body's proxy was last built for.</summary>
	private static readonly ConditionalWeakTable<MeshInstance3D, StrongBox<(UInt128 Hidden, bool Enabled)>> BuiltFor = new();

	/// <summary>
	/// /// Each body's proxy. A character's proxy stands BESIDE its body (same parent) so its placement
	/// /// is the shadow skeletons' to write at the guest's rate; a rigid prop's stays a child.
	/// </summary>
	private static readonly ConditionalWeakTable<MeshInstance3D, MeshInstance3D> Proxies = new();

	/// <summary>The body's proxy, if it has one.</summary>
	public static MeshInstance3D? ProxyOf(MeshInstance3D body) =>
		Proxies.TryGetValue(body, out MeshInstance3D? proxy) && GodotObject.IsInstanceValid(proxy) ? proxy : null;


	/// <summary>
	/// The reconcile cadence's call: <see cref="Sync"/> only when the hidden set moved since the
	/// proxy was built. No engine call otherwise — this runs for every entity, every second.
	/// </summary>
	public static void SyncHidden(MeshInstance3D body)
	{
		if (!BuiltFor.TryGetValue(body, out StrongBox<(UInt128 Hidden, bool Enabled)>? built) ||
			built.Value != (Engine_HiddenClusters.CollapsedMask(body), Enabled))
		{
			Sync(body);
		}
	}

	/// <summary>
	/// Brings <paramref name="body"/>'s proxy in line with its mesh, skeleton, layers and hidden
	/// surfaces. Cheap when nothing changed. Call after any of those change. Main thread.
	/// </summary>
	public static void Sync(MeshInstance3D body)
	{
		if (!GodotObject.IsInstanceValid(body) || !body.IsInsideTree())
		{
			return;
		}

		UInt128 collapsed = Engine_HiddenClusters.CollapsedMask(body);
		BuiltFor.GetOrCreateValue(body).Value = (collapsed, Enabled);

		var source = body.Mesh as ArrayMesh;
		int surfaces = source?.GetSurfaceCount() ?? 0;
		UInt128 hidden = UInt128.Zero;
		bool skinned = surfaces != 0 && (source!.SurfaceGetFormat(0) & Mesh.ArrayFormat.FormatBones) != 0;
		// // Only a character casts through a cadence proxy. An attached child (a held weapon)
		// // keeps its own shadow: see Engine_ShadowSkeletons for what the proxy is for.
		bool follows = skinned;
		bool eligible = Enabled && source is not null && surfaces <= MaxSurfaces &&
			surfaces >= (follows ? MinSkinnedSurfaces : MinRigidSurfaces);
		if (eligible)
		{
			// Engine_HiddenClusters is the override slots' only writer and keeps what it wrote.
			hidden = surfaces >= MaxSurfaces ? collapsed : collapsed & ((UInt128.One << surfaces) - UInt128.One);
		}

		string state = eligible
			? $"{source!.GetInstanceId()}:{hidden:X}:{body.Skeleton}:{body.Layers:X}:{body.VisibilityRangeEnd:0.#}:{(follows ? 'f' : 'c')}"
			: string.Empty;
		if (body.HasMeta(StateMeta) && body.GetMeta(StateMeta).AsString() == state)
		{
			// Engine_ShadowReach.Release hands a body its shadow back when a prop turns out to be a
			// character; with a proxy standing that is BOTH casting, which is dearer than neither.
			if (state.Length != 0 && body.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off &&
				ProxyOf(body) is not null)
			{
				body.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
			}

			return;
		}

		body.SetMeta(StateMeta, state);
		MeshInstance3D? proxy = ProxyOf(body);
		ArrayMesh? merged = eligible ? MergedFor(source!, body, hidden, skinned, always: follows) : null;
		ulong bodyId = body.GetInstanceId();
		if (merged is null)
		{
			if (proxy is not null)
			{
				Engine_ShadowSkeletons.Forget(body);
				Proxies.Remove(body);
				proxy.QueueFree();
				body.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
				if (Proxied.Remove(bodyId) && !skinned)
				{
					Refii.Lighting.Engine_ShadowReach.Adopt(body);
				}
			}

			return;
		}

		// Beside the body, or under it: a proxy standing on the wrong side is rebuilt.
		bool beside = follows && body.GetParent() is Node3D && body.GetParent() is not BoneAttachment3D;
		if (proxy is not null && ReferenceEquals(proxy.GetParent(), body) == beside)
		{
			Engine_ShadowSkeletons.Forget(body);
			Proxies.Remove(body);
			proxy.QueueFree();
			proxy = null;
		}

		if (proxy is null)
		{
			proxy = new MeshInstance3D
			{
				Name = NodeName + "_" + body.Name,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly,
				GIMode = GeometryInstance3D.GIModeEnum.Disabled,
			};

			// A character's proxy is a SIBLING: under the body it would move at display rate
			// with its parent and re-dirty every lantern map each frame whatever its skeleton did.
			// Beside it, the shadow skeletons place it on guest frames and mirror its visibility.
			// A rigid prop's proxy stays a child, as does one whose body rides a bone attachment
			// (the attachment moves at display rate whichever side of it the proxy is on).
			if (beside)
			{
				var parent = (Node3D)body.GetParent();
				parent.AddChild(proxy);
				proxy.GlobalTransform = body.GlobalTransform;
				proxy.Visible = body.IsVisibleInTree();

				// Freed with its body: a sibling is not.
				MeshInstance3D standing = proxy;
				body.TreeExiting += () =>
				{
					if (GodotObject.IsInstanceValid(standing) && standing.IsInsideTree())
					{
						standing.QueueFree();
					}
				};
			}
			else
			{
				body.AddChild(proxy);
			}

			Proxies.AddOrUpdate(body, proxy);
			Active++;

			// Leaving the tree is the one event every ending shares: freed here, or with its body.
			// A proxy rebuilt on the other side of its body leaves AFTER its successor stands,
			// so it only unregisters itself.
			MeshInstance3D me = proxy;
			proxy.TreeExiting += () =>
			{
				Active--;
				if (GodotObject.IsInstanceValid(body) && ReferenceEquals(ProxyOf(body), me))
				{
					Proxied.Remove(bodyId);
					Engine_ShadowSkeletons.Forget(body);
					Proxies.Remove(body);
				}
				else if (!GodotObject.IsInstanceValid(body))
				{
					Proxied.Remove(bodyId);
				}
			};
		}

		proxy.Mesh = merged;
		proxy.Layers = body.Layers;

		// The body's screen-size cull is the caster's too: a range is per instance, not inherited.
		proxy.VisibilityRangeEnd = body.VisibilityRangeEnd;
		proxy.VisibilityRangeEndMargin = body.VisibilityRangeEndMargin;
		proxy.VisibilityRangeFadeMode = body.VisibilityRangeFadeMode;
		if (!body.Skeleton.IsEmpty && body.GetNodeOrNull(body.Skeleton) is Skeleton3D bones)
		{
			// Not the body's skeleton: the proxy's own, under the proxy, posed at the guest's rate
			// (every frame for a principal) by the same writers — see Engine_ShadowSkeletons.
			Skeleton3D shadowBones = Engine_ShadowSkeletons.Ensure(body, proxy, bones);
			proxy.Skeleton = proxy.GetPathTo(shadowBones);
			proxy.Skin = body.Skin;
		}
		else
		{
			Engine_ShadowSkeletons.Forget(body);
			proxy.Skeleton = new NodePath();
			proxy.Skin = null;
		}

		body.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		Proxied.Add(bodyId);

		// A rigid prop's shadow is withdrawn by size and distance; a character keeps its own. A
		// prop that rigs later takes its proxy back, cast-only as it was.
		if (!skinned)
		{
			Refii.Lighting.Engine_ShadowReach.Adopt(proxy);
		}
		else
		{
			Refii.Lighting.Engine_ShadowReach.Release(proxy);
			proxy.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
		}
	}

	/// <param name="always">
	/// A caster even when merging saves nothing: the proxy exists for its cadence (a character, an
	/// attached child), not for its draw count. False keeps a rigid prop on its own shadow unless
	/// two or more surfaces become one.
	/// </param>
	private static ArrayMesh? MergedFor(ArrayMesh source, MeshInstance3D? body, UInt128 hidden, bool skinned, bool always)
	{
		Dictionary<(UInt128 Hidden, bool Always), ArrayMesh?> perMask = Cache.GetOrCreateValue(source);
		lock (perMask)
		{
			if (!perMask.TryGetValue((hidden, always), out ArrayMesh? merged))
			{
				merged = Build(source, body, hidden, skinned, always);
				perMask[(hidden, always)] = merged;
			}

			return merged;
		}
	}

	/// <summary>The cast-only mesh, or null when a surface cannot be merged and the body must keep casting.</summary>
	private static ArrayMesh? Build(ArrayMesh source, MeshInstance3D? body, UInt128 hidden, bool skinned, bool always)
	{
		var positions = new List<Vector3>();
		var bones = new List<int>();
		var weights = new List<float>();
		var indices = new List<int>();
		var kept = new List<(Godot.Collections.Array Arrays, Material Material, Godot.Collections.Dictionary? Lods)>();
		var levels = new List<(int At, int[] Full, List<(float Edge, int[] Indices)> Lods)>();
		int merged = 0;

		for (int s = 0; s < source.GetSurfaceCount(); s++)
		{
			if ((hidden & (UInt128.One << s)) != UInt128.Zero)
			{
				continue;
			}

			if (source.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Triangles)
			{
				return null;
			}

			Godot.Collections.Array arrays = source.SurfaceGetArrays(s);
			Vector3[] vertex = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
			int[] index = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
			int[] bone = skinned ? arrays[(int)Mesh.ArrayType.Bones].AsInt32Array() : System.Array.Empty<int>();
			float[] weight = skinned ? arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array() : System.Array.Empty<float>();
			if (vertex.Length == 0 || index.Length == 0 ||
				(skinned && (bone.Length != vertex.Length * 4 || weight.Length != vertex.Length * 4)))
			{
				return null;
			}

			Godot.Collections.Dictionary? lods = Refii.Models.Engine_MdlMesh.SurfaceLods(source, s);

			Material? material = body?.GetSurfaceOverrideMaterial(s) ?? source.SurfaceGetMaterial(s);
			if (material is ShaderMaterial shaded &&
				shaded.GetShaderParameter(AlphaScissor) is { VariantType: Variant.Type.Bool } scissor &&
				scissor.AsBool())
			{
				var own = new Godot.Collections.Array();
				own.Resize((int)Mesh.ArrayType.Max);
				own[(int)Mesh.ArrayType.Vertex] = vertex;
				own[(int)Mesh.ArrayType.TexUV] = arrays[(int)Mesh.ArrayType.TexUV];
				if (skinned)
				{
					own[(int)Mesh.ArrayType.Bones] = bone;
					own[(int)Mesh.ArrayType.Weights] = weight;
				}

				own[(int)Mesh.ArrayType.Index] = index;
				kept.Add((own, shaded, lods));
				continue;
			}

			int at = positions.Count;
			positions.AddRange(vertex);
			bones.AddRange(bone);
			weights.AddRange(weight);
			foreach (int i in index)
			{
				indices.Add(at + i);
			}

			var sorted = new List<(float Edge, int[] Indices)>();
			if (lods is not null)
			{
				foreach (Variant key in lods.Keys)
				{
					sorted.Add((key.AsSingle(), lods[key].AsInt32Array()));
				}

				sorted.Sort((a, b) => a.Edge.CompareTo(b.Edge));
			}

			levels.Add((at, index, sorted));
			merged++;
		}

		// // A cadence proxy is built from whatever is there: one merged surface, or the kept
		// // surfaces alone when the body's only surface is alpha-cut. A rigid prop's draw-saving
		// // twin needs two surfaces to become one, or the prop keeps its own shadow.
		if (merged < 2 && !always)
		{
			return null;
		}

		if (merged == 0 && kept.Count == 0)
		{
			return null;
		}

		var result = new ArrayMesh();
		if (merged != 0)
		{
			var one = new Godot.Collections.Array();
			one.Resize((int)Mesh.ArrayType.Max);
			one[(int)Mesh.ArrayType.Vertex] = positions.ToArray();
			if (skinned)
			{
				one[(int)Mesh.ArrayType.Bones] = bones.ToArray();
				one[(int)Mesh.ArrayType.Weights] = weights.ToArray();
			}

			one[(int)Mesh.ArrayType.Index] = indices.ToArray();
			result.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, one, null, MergedLods(levels));
			result.SurfaceSetMaterial(0, Caster);
		}

		foreach ((Godot.Collections.Array arrays, Material material, Godot.Collections.Dictionary? lods) in kept)
		{
			result.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, lods);
			result.SurfaceSetMaterial(result.GetSurfaceCount() - 1, material);
		}

		Built++;
		SurfacesMerged += merged;
		return result;
	}

	/// <summary>
	/// Level k of the merged surface: each source's level k, its coarsest where it has fewer, its
	/// full index list where it has none; keyed by the largest edge length among them. Null when
	/// no source has any.
	/// </summary>
	private static Godot.Collections.Dictionary? MergedLods(List<(int At, int[] Full, List<(float Edge, int[] Indices)> Lods)> sources)
	{
		int depth = 0;
		foreach ((_, _, List<(float Edge, int[] Indices)> lods) in sources)
		{
			depth = System.Math.Max(depth, lods.Count);
		}

		if (depth == 0)
		{
			return null;
		}

		var result = new Godot.Collections.Dictionary();
		float lastEdge = 0f;
		for (int k = 0; k < depth; k++)
		{
			float edge = 0f;
			var level = new List<int>();
			foreach ((int at, int[] full, List<(float Edge, int[] Indices)> lods) in sources)
			{
				int[] take = full;
				if (lods.Count != 0)
				{
					(float Edge, int[] Indices) own = lods[System.Math.Min(k, lods.Count - 1)];
					take = own.Indices;
					edge = System.Math.Max(edge, own.Edge);
				}

				foreach (int i in take)
				{
					level.Add(at + i);
				}
			}

			// Keys must rise; two levels that tie would overwrite one another.
			if (edge <= lastEdge)
			{
				continue;
			}

			lastEdge = edge;
			result[edge] = level.ToArray();
		}

		return result.Count == 0 ? null : result;
	}
}
