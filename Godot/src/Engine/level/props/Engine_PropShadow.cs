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
		float[] buffer = count > 0 ? source.Buffer : System.Array.Empty<float>();
		int stride = count > 0 ? buffer.Length / count : 0;
		if (stride < 12)
		{
			return;
		}

		// The transform is the first twelve floats of each slot; the colours behind it are the
		// lit batch's and the caster reads none.
		var transforms = new float[count * 12];
		for (int i = 0; i < count; i++)
		{
			System.Array.Copy(buffer, i * stride, transforms, i * 12, 12);
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

	/// <summary>Writes one copy's transform to the batch and to its cast-only twin.</summary>
	public static void SetInstanceTransform(MultiMesh batch, int instance, Transform3D transform)
	{
		batch.SetInstanceTransform(instance, transform);
		if (Twins.TryGetValue(batch, out MultiMesh? shadow) && instance < shadow.InstanceCount)
		{
			Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.ShadowTwin);
			shadow.SetInstanceTransform(instance, transform);
		}
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
}
