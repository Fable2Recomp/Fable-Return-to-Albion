using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;

namespace Refii.Level;

/// <summary>
/// One draw per shadow pass for a batch of props, instead of one per surface.
/// </summary>
/// <remarks>
/// A shadow draw is one per surface per instance: no lit shader here qualifies for Godot's shared
/// shadow material, so nothing merges. Measured 2026-09-21 in Bowerstone Market, the streamer owns
/// 4,458 shadow-casting surfaces over 1,801 casters — 2.5 draws each, in every cascade that
/// reaches them — and the render thread is draw-call bound.
///
/// So a batch that casts gets a cast-only twin: a second MultiMesh holding the SAME transforms
/// over <see cref="Refii.Entities.Engine_ShadowProxy.MergedCaster"/>'s mesh — every opaque
/// surface as one, alpha-tested surfaces kept with their material, LODs carried — and the batch
/// itself stops casting. A single-node prop goes through <c>Engine_ShadowProxy.Sync</c> as an
/// entity does. The batch is what <c>Engine_ShadowReach</c> stops writing; the twin is what it
/// is handed.
///
/// A static twin collapses and restores single copies of a batch; those writes go through
/// <see cref="SetInstanceTransform"/> and <see cref="CoverBounds"/> so the shadow keeps step.
/// </remarks>
public static class Engine_PropShadow
{
	public const string NodeName = "ShadowProxy";

	/// <summary>Batches casting through a twin, and the surfaces those twins stand for. Run totals.</summary>
	public static int Batches;

	public static int SurfacesMerged;

	private static readonly ConditionalWeakTable<MultiMesh, MultiMesh> Twins = new();

	private static readonly List<(MultiMeshInstance3D Batch, MultiMeshInstance3D Twin)> Pairs = new();

	private static bool _enabled = true;

	/// <summary>Debug lever: false hands every batch its own shadow back, for reading the cost as a difference.</summary>
	public static bool Enabled
	{
		get => _enabled;
		set
		{
			if (_enabled == value)
			{
				return;
			}

			_enabled = value;
			for (int i = Pairs.Count - 1; i >= 0; i--)
			{
				(MultiMeshInstance3D batch, MultiMeshInstance3D twin) = Pairs[i];
				if (!GodotObject.IsInstanceValid(batch) || !GodotObject.IsInstanceValid(twin))
				{
					Pairs.RemoveAt(i);
					continue;
				}

				Hand(batch, twin, toTwin: value);
			}
		}
	}

	/// <summary>For a level change, where the batches go away.</summary>
	public static void Forget() => Pairs.Clear();

	/// <summary>
	/// Each level batch's instance transforms, as the region build wrote them and every later
	/// write through <see cref="SetInstanceTransform"/> changed them.
	/// </summary>
	/// <remarks>
	/// Reading `MultiMesh.Buffer` back is a render-thread sync: with Godot's separate render
	/// thread the getter waits until that thread has drained its whole queue, which while a
	/// region pages in holds the workers' texture uploads. The build already has the transforms,
	/// so it records them here and nothing reads the buffer back.
	/// </remarks>
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MultiMesh, Transform3D[]> Stated = new();

	/// <summary>Records a batch's instance transforms as built. Any thread.</summary>
	public static void RecordBuilt(MultiMesh batch, Transform3D[] transforms) =>
		Stated.AddOrUpdate(batch, transforms);

	/// <summary>The batch's instance transforms without a readback, or null for a batch built elsewhere.</summary>
	public static Transform3D[]? StatedTransforms(MultiMesh batch) =>
		Stated.TryGetValue(batch, out Transform3D[]? transforms) &&
		transforms.Length == batch.InstanceCount
			? transforms
			: null;

	/// <summary>Gives a batch whose mesh has just arrived its cast-only twin. Main thread.</summary>
	public static void Attach(MultiMeshInstance3D batch, ArrayMesh mesh)
	{
		if (batch.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off ||
			batch.Multimesh is not { } source || batch.HasNode(NodeName) ||
			Refii.Entities.Engine_ShadowProxy.MergedCaster(mesh, out int merged) is not { } caster)
		{
			return;
		}

		int count = source.InstanceCount;
		float[] transforms;
		if (StatedTransforms(source) is { } stated)
		{
			transforms = new float[count * 12];
			for (int i = 0; i < count; i++)
			{
				WriteSlot(transforms, i * 12, stated[i]);
			}
		}
		else
		{
			float[] buffer = count > 0 ? source.Buffer : System.Array.Empty<float>();
			int stride = count > 0 ? buffer.Length / count : 0;
			if (stride < 12)
			{
				return;
			}

			// The transform is the first twelve floats of each slot; the colours behind it are
			// the lit batch's and the caster reads none.
			transforms = new float[count * 12];
			for (int i = 0; i < count; i++)
			{
				System.Array.Copy(buffer, i * stride, transforms, i * 12, 12);
			}
		}

		// The bounds before the buffer, as the batch itself is built: a transform write marks the
		// bounds dirty and they are recomputed from the mesh unless a custom box stands.
		var shadow = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
			InstanceCount = count,
			CustomAabb = source.CustomAabb,
			Mesh = caster,
		};
		shadow.Buffer = transforms;

		var twin = new MultiMeshInstance3D
		{
			Name = NodeName,
			Multimesh = shadow,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly,
			GIMode = GeometryInstance3D.GIModeEnum.Disabled,
			Layers = batch.Layers,
			VisibilityRangeEnd = batch.VisibilityRangeEnd,
			VisibilityRangeEndMargin = batch.VisibilityRangeEndMargin,
			VisibilityRangeFadeMode = batch.VisibilityRangeFadeMode,
		};
		batch.AddChild(twin);

		Twins.AddOrUpdate(source, shadow);
		Pairs.Add((batch, twin));
		++Batches;
		SurfacesMerged += merged;
		Hand(batch, twin, toTwin: _enabled);
	}

	/// <summary>Who casts: the twin, with the batch out of the reach policy's hands, or the reverse.</summary>
	private static void Hand(MultiMeshInstance3D batch, MultiMeshInstance3D twin, bool toTwin)
	{
		Refii.Entities.Engine_ShadowProxy.MarkProxied(batch.GetInstanceId(), toTwin);
		Refii.Entities.Engine_ShadowProxy.MarkProxied(twin.GetInstanceId(), !toTwin);
		batch.CastShadow = toTwin
			? GeometryInstance3D.ShadowCastingSetting.Off
			: GeometryInstance3D.ShadowCastingSetting.On;
		twin.CastShadow = toTwin
			? GeometryInstance3D.ShadowCastingSetting.ShadowsOnly
			: GeometryInstance3D.ShadowCastingSetting.Off;
		twin.Visible = toTwin;
		Refii.Lighting.Engine_ShadowReach.Adopt(toTwin ? twin : batch);
	}

	/// <summary>
	/// Writes one copy's transform to the batch, and to its cast-only twin at the guest's
	/// half rate.
	/// </summary>
	/// <remarks>
	/// A MultiMesh slot write has no equality check in the server: it marks the buffer dirty,
	/// the instance updates, and every lamp paired with a CASTING instance redraws its map
	/// (`_update_instance`: `can_cast_shadows` → `make_shadow_dirty`, count 2). The lit batch
	/// does not cast once its twin does, so its slot can follow the guest every display frame
	/// for free; the twin is the caster, and it takes the slot on the same frames the
	/// villagers' shadow skeletons move (`Engine_GuestFrame.HalfRate`, 15 Hz), so a bobbing
	/// barge or a swinging sign under a lamp costs the map two redraws in eight frames, not
	/// eight. An unchanged transform is not written at all.
	/// </remarks>
	public static void SetInstanceTransform(MultiMesh batch, int instance, Transform3D transform)
	{
		Transform3D[]? stated = Stated.TryGetValue(batch, out Transform3D[]? recorded) &&
			instance >= 0 && instance < recorded.Length
			? recorded
			: null;
		if (stated is not null && stated[instance].IsEqualApprox(transform))
		{
			return;
		}

		batch.SetInstanceTransform(instance, transform);
		if (stated is not null)
		{
			stated[instance] = transform;
		}

		if (!Twins.TryGetValue(batch, out MultiMesh? shadow) || instance >= shadow.InstanceCount)
		{
			return;
		}

		if (stated is null || Refii.Core.Engine_GuestFrame.HalfRate)
		{
			Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.ShadowTwin);
			shadow.SetInstanceTransform(instance, transform);
			Deferred.Remove((batch, instance));
		}
		else
		{
			Deferred.Add((batch, instance));
		}
	}

	/// <summary>Twin slots whose batch moved on a frame the twin does not take.</summary>
	private static readonly HashSet<(MultiMesh Batch, int Instance)> Deferred = new();

	/// <summary>
	/// Once per frame after the entities are placed: on a half-rate frame, the twins take every
	/// slot that moved since the last one. A twin never reads its slot back from the server.
	/// </summary>
	public static void FlushDeferred()
	{
		if (Deferred.Count == 0 || !Refii.Core.Engine_GuestFrame.HalfRate)
		{
			return;
		}

		foreach ((MultiMesh batch, int instance) in Deferred)
		{
			if (Twins.TryGetValue(batch, out MultiMesh? shadow) && instance < shadow.InstanceCount &&
				Stated.TryGetValue(batch, out Transform3D[]? stated) && instance < stated.Length)
			{
				Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.ShadowTwin);
				shadow.SetInstanceTransform(instance, stated[instance]);
			}
		}

		Deferred.Clear();
	}

	/// <summary>The batch's bounds grew; the twin is culled by the same box.</summary>
	public static void CoverBounds(MultiMesh batch)
	{
		if (Twins.TryGetValue(batch, out MultiMesh? shadow))
		{
			Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.ShadowTwin);
			shadow.CustomAabb = batch.CustomAabb;
		}
	}

	/// <summary>
	/// One Transform3D in MultiMesh buffer order: three rows of the basis, each followed by that
	/// row's origin component — the layout `Engine_PropStreamer` reads the buffer back in.
	/// </summary>
	public static void WriteSlot(float[] buffer, int at, Transform3D t)
	{
		buffer[at] = t.Basis.Column0.X;
		buffer[at + 1] = t.Basis.Column1.X;
		buffer[at + 2] = t.Basis.Column2.X;
		buffer[at + 3] = t.Origin.X;
		buffer[at + 4] = t.Basis.Column0.Y;
		buffer[at + 5] = t.Basis.Column1.Y;
		buffer[at + 6] = t.Basis.Column2.Y;
		buffer[at + 7] = t.Origin.Y;
		buffer[at + 8] = t.Basis.Column0.Z;
		buffer[at + 9] = t.Basis.Column1.Z;
		buffer[at + 10] = t.Basis.Column2.Z;
		buffer[at + 11] = t.Origin.Z;
	}
}
