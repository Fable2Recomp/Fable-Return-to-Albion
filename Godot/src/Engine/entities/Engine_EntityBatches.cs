using System.Collections.Generic;
using Godot;

namespace Refii.Entities;

/// <summary>
/// The guest's props that STAND STILL, drawn from batches instead of a node apiece.
/// </summary>
/// <remarks>
/// The guest places crates, stall parts, bottles and railings as entities, and the feed gives each
/// a MeshInstance3D: its own draws in the frame and in every shadow pass, its own entry in the
/// renderer's spatial index — which each fixture's shadow cull walks — and its own light pairing.
/// Measured 2026-09-22 in Bowerstone Market: hiding the entities takes the frame from 23.8 to
/// 12.6 ms, about 500 of the ~720 entity bodies are rigid props over at most 145 meshes, and 34 of
/// 4,437 entities change on a walk.
///
/// So a prop the guest has left unchanged for <see cref="StillWalks"/> walks is PROMOTED: its node
/// is hidden — which takes it, and its cast-only child, out of the index — and a slot in the batch
/// for its (mesh, layers, cell) draws it where the node stood. The node stays: every other part of
/// the feed keeps its handle, and the moment the guest states a change the prop is DEMOTED — slot
/// released, node shown — before anything places it. A batch casts through a merged twin exactly
/// as the streamer's do. Only under the unified lighting model, where instance lighting is the
/// theme's and not a per-prop probe sample.
/// </remarks>
public partial class Engine_EntityBatches : Node3D
{
	public const string NodeName = "EntityBatches";

	/// <summary>Walks a prop must go unchanged before it is batched: two seconds of guest frames.</summary>
	public const int StillWalks = 60;

	/// <summary>Debug lever: false demotes everything and promotes nothing.</summary>
	public static bool Enabled = true;

	/// <summary>Props drawn from a batch now, batches standing, and promotions and demotions as run totals.</summary>
	public static int Batched;

	public static int Batches;

	public static long Promotions;

	public static long Demotions;

	private sealed class Batch
	{
		public MultiMeshInstance3D Node = null!;
		public MultiMesh Mesh = null!;
		public MultiMesh? Caster;
		public readonly List<uint> Owners = new();
		public readonly List<Transform3D> Transforms = new();
		public Aabb Bounds;
		public Aabb Local;

		/// <summary>The range its entities state (all alike: it is in the key), or null.</summary>
		public float[]? Range;
	}

	/// <summary>
	/// Keyed by the stated range end too, in whole metres: a batch draws its entities to one range,
	/// so two entities of one mesh that state different distances never share one.
	/// </summary>
	private readonly Dictionary<(Mesh Mesh, uint Layers, Vector3I Cell, int RangeEnd), Batch> _batches = new();

	private readonly Dictionary<uint, (Batch Batch, int Slot)> _slots = new();

	private long _litGeneration = long.MinValue;

	public bool Contains(uint entity) => _slots.ContainsKey(entity);

	/// <summary>
	/// Draws <paramref name="entity"/> from a batch at <paramref name="local"/>, this node's space,
	/// to <paramref name="range"/> when the entity states one. Main thread.
	/// </summary>
	public bool Promote(uint entity, ArrayMesh mesh, uint layers, Transform3D local, float[]? range)
	{
		if (!Enabled || _slots.ContainsKey(entity))
		{
			return false;
		}

		float cellMetres = Refii.Level.Engine_LevelProps.BatchCellMetres;
		var cell = new Vector3I(Mathf.FloorToInt(local.Origin.X / cellMetres),
			Mathf.FloorToInt(local.Origin.Y / cellMetres), Mathf.FloorToInt(local.Origin.Z / cellMetres));
		var key = (mesh, layers, cell, range is null ? -1 : Mathf.RoundToInt(range[2]));
		if (!_batches.TryGetValue(key, out Batch? batch))
		{
			batch = Create(mesh, layers);
			batch.Range = range;
			_batches[key] = batch;
			++Batches;
		}

		int slot = batch.Owners.Count;
		if (slot >= batch.Mesh.InstanceCount)
		{
			Grow(batch);
		}

		batch.Owners.Add(entity);
		batch.Transforms.Add(local);
		Aabb box = local * batch.Local;
		batch.Bounds = slot == 0 ? box : batch.Bounds.Merge(box);
		Write(batch, slot, local);
		Cover(batch);
		batch.Mesh.VisibleInstanceCount = batch.Owners.Count;
		if (batch.Caster is not null)
		{
			batch.Caster.VisibleInstanceCount = batch.Owners.Count;
		}

		_slots[entity] = (batch, slot);
		++Batched;
		++Promotions;
		return true;
	}

	/// <summary>Releases <paramref name="entity"/>'s slot; true when it held one. The caller shows the node.</summary>
	public bool Demote(uint entity)
	{
		if (!_slots.Remove(entity, out (Batch Batch, int Slot) held))
		{
			return false;
		}

		// The last slot moves into the freed one: a batch has no holes, only a visible prefix.
		Batch batch = held.Batch;
		int last = batch.Owners.Count - 1;
		if (held.Slot != last)
		{
			uint moved = batch.Owners[last];
			batch.Owners[held.Slot] = moved;
			batch.Transforms[held.Slot] = batch.Transforms[last];
			_slots[moved] = (batch, held.Slot);
			Write(batch, held.Slot, batch.Transforms[last]);
		}

		batch.Owners.RemoveAt(last);
		batch.Transforms.RemoveAt(last);
		batch.Mesh.VisibleInstanceCount = batch.Owners.Count;
		if (batch.Caster is not null)
		{
			batch.Caster.VisibleInstanceCount = batch.Owners.Count;
		}

		--Batched;
		++Demotions;
		return true;
	}

	/// <summary>Once a walk: the theme's lighting reaches the batches when it moves, as it reaches the nodes.</summary>
	public void Refresh()
	{
		long generation = Refii.Lighting.Engine_LevelBakedLighting.LightingGeneration;
		if (generation == _litGeneration)
		{
			return;
		}

		_litGeneration = generation;
		foreach (Batch batch in _batches.Values)
		{
			Light(batch);
		}
	}

	/// <summary>The feed is per level and takes its batches with it; the totals are the process's.</summary>
	public override void _ExitTree()
	{
		Batched -= _slots.Count;
		Batches -= _batches.Count;
		_slots.Clear();
		_batches.Clear();
	}

	private Batch Create(ArrayMesh mesh, uint layers)
	{
		var batch = new Batch { Local = mesh.GetAabb() };
		batch.Mesh = NewMultiMesh(mesh, 8);
		batch.Node = new MultiMeshInstance3D
		{
			Name = $"Batch_{_batches.Count}",
			Multimesh = batch.Mesh,
			Layers = layers,
			// As the nodes it stands for: lit by the GI's probes, never voxelised into it.
			GIMode = GeometryInstance3D.GIModeEnum.Dynamic,
		};
		AddChild(batch.Node);

		// The same cast-only twin the streamer's batches have; a mesh that merges nothing casts itself.
		if (Engine_ShadowProxy.MergedCaster(mesh, out _) is { } caster)
		{
			batch.Caster = NewMultiMesh(caster, 8);
			var twin = new MultiMeshInstance3D
			{
				Name = Engine_ShadowProxy.NodeName,
				Multimesh = batch.Caster,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly,
				GIMode = GeometryInstance3D.GIModeEnum.Disabled,
				Layers = layers,
			};
			batch.Node.AddChild(twin);
			batch.Node.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
			Engine_ShadowProxy.MarkProxied(batch.Node.GetInstanceId(), true);
			Refii.Lighting.Engine_ShadowReach.Adopt(twin);
		}
		else
		{
			Refii.Lighting.Engine_ShadowReach.Adopt(batch.Node);
		}

		Light(batch);
		return batch;
	}

	private static MultiMesh NewMultiMesh(Mesh mesh, int capacity) => new()
	{
		TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
		InstanceCount = capacity,
		VisibleInstanceCount = 0,
		// Before any transform: a write marks the bounds dirty and they are recomputed from the
		// mesh unless a custom box stands.
		CustomAabb = new Aabb(Vector3.Zero, Vector3.One),
		Mesh = mesh,
	};

	/// <summary>Doubles the capacity. Setting the count clears the buffer, so every slot is written again.</summary>
	private static void Grow(Batch batch)
	{
		int capacity = batch.Mesh.InstanceCount * 2;
		batch.Mesh.InstanceCount = capacity;
		if (batch.Caster is not null)
		{
			batch.Caster.InstanceCount = capacity;
		}

		for (int i = 0; i < batch.Transforms.Count; i++)
		{
			Write(batch, i, batch.Transforms[i]);
		}
	}

	private static void Write(Batch batch, int slot, Transform3D transform)
	{
		Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.BatchSlot);
		batch.Mesh.SetInstanceTransform(slot, transform);
		if (batch.Caster is not null)
		{
			Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.ShadowTwin);
			batch.Caster.SetInstanceTransform(slot, transform);
		}
	}

	private static void Cover(Batch batch)
	{
		Aabb bounds = batch.Bounds.Grow(0.5f);
		if (batch.Mesh.CustomAabb.Encloses(bounds))
		{
			return;
		}

		Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.PropBounds);
		batch.Mesh.CustomAabb = bounds;
		if (batch.Caster is not null)
		{
			Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.ShadowTwin);
			batch.Caster.CustomAabb = bounds;
		}

		// The range its entities state, widened by how far the batch spreads from the centre Godot
		// measures to; else the props' screen-size cull, which a batch takes only while it is
		// compact and which reads the box just written.
		if (batch.Range is { } range)
		{
			float[] widened = (float[])range.Clone();
			widened[2] += bounds.Size.Length() * 0.5f;
			Refii.Level.Engine_PropDrawRange.ApplyStated(batch.Node, widened);
		}
		else
		{
			Refii.Level.Engine_PropStreamer.ApplyVisibilityRange(batch.Node, batch.Mesh.Mesh);
		}

		if (batch.Node.GetNodeOrNull<MultiMeshInstance3D>(Engine_ShadowProxy.NodeName) is { } twin)
		{
			twin.VisibilityRangeEnd = batch.Node.VisibilityRangeEnd;
			twin.VisibilityRangeEndMargin = batch.Node.VisibilityRangeEndMargin;
			twin.VisibilityRangeFadeMode = batch.Node.VisibilityRangeFadeMode;
		}
	}

	private void Light(Batch batch)
	{
		Refii.Level.Engine_SkyEnvironmentNode? sky = Refii.Level.Engine_SkyEnvironmentNode.Current;
		if (sky is null || !GodotObject.IsInstanceValid(batch.Node))
		{
			return;
		}

		Refii.Lighting.Engine_LevelBakedLighting.ApplyInstanceLighting(batch.Node, sky.Lighting);
	}
}
