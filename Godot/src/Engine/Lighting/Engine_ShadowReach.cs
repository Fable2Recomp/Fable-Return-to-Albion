using System.Collections.Generic;
using Godot;

namespace Refii.Lighting;

/// <summary>
/// How far each piece of a region casts a sun shadow, by how big it is.
/// </summary>
/// <remarks>
/// THE FAR CASCADE IS FOR THE LANDSCAPE, NOT FOR CRATES. The sun's shadow reaches 300 m because
/// anything outside the cascades stops existing to the light, and a horizon-low sun then shines
/// straight through the hills. But every barrel, sign and flowerpot in that 300 m is drawn into
/// the cascades too: measured 2026-09-04 in Bowerstone Market, the sun's four cascades were
/// 10,466 draw calls and 105M primitives of a 130M-primitive frame, and turning them off took
/// the frame from 15 to 23 fps. What a player can see of a hand-cart's shadow at 80 m is
/// nothing.
///
/// So the reach is per object and keyed on its own size: small dressing stops casting a long way
/// before the buildings do, and the terrain and the big geometry that motivated the 300 m keep
/// casting all the way out. Nothing is hidden — only its shadow stops being drawn — and the
/// cascades near the camera, which is all anyone inspects, are untouched.
///
/// The distance is to the NEAREST view's eye (<see cref="Refii.Core.Engine_Views"/>): on split
/// screen the cascades are drawn for both players, so a prop near either keeps its shadow.
/// </remarks>
public partial class Engine_ShadowReach : Node
{
	public const string NodeName = "ShadowReach";

	/// <summary>Debug lever: false leaves every instance casting, the way Godot built it.</summary>
	public static bool Enabled = true;

	/// <summary>Instances whose shadow is currently switched off.</summary>
	public static int Suppressed;

	/// <summary>Instances considered on the last pass.</summary>
	public static int Considered;

	/// <summary>Buffer distance to prevent boundary flickering when camera moves near cutoff.</summary>
	private const float HysteresisMetres = 8.0f;

	/// <summary>
	/// Computes the tiered sun shadow reach distance based on the object's longest extent.
	/// &lt;0.6m: 18m * scale (clutter / tableware / small debris)
	/// &lt;1.5m: 30m * scale (crates, barrels, stools, gravestones)
	/// &lt;3.5m: 55m * scale (carts, market stalls, tables, lamp posts, fences, bushes)
	/// &lt;8.0m: 95m * scale (modular building facades, canopies, sheds)
	/// &gt;=8.0m: PositiveInfinity (landscape, cliffs, hills, large trees, clocktowers, bridges)
	/// Returns PositiveInfinity when scale &lt;= 0 (culling disabled).
	/// </summary>
	public static float CalculateShadowReach(float longest, float reachScale)
	{
		if (reachScale <= 0f)
		{
			return float.PositiveInfinity;
		}

		return longest switch
		{
			< 0.6f => 18f * reachScale,
			< 1.5f => 30f * reachScale,
			< 3.5f => 55f * reachScale,
			< 8.0f => 95f * reachScale,
			_ => float.PositiveInfinity
		};
	}

	/// <summary>
	/// Resolves the Godot <see cref="GeometryInstance3D.CastShadow"/> and optional
	/// <see cref="Node3D.Visible"/> writes for an adopted instance. Cast-only proxies
	/// (<see cref="GeometryInstance3D.ShadowCastingSetting.ShadowsOnly"/>) MUST keep
	/// <c>ShadowsOnly</c> and toggle <c>Visible</c> instead of switching to <c>Off</c>,
	/// because in Godot 4 changing <c>ShadowsOnly</c> to <c>Off</c> enables main-camera
	/// color rendering and causes unlit/black coplanar Z-fighting against the real mesh.
	/// </summary>
	public static (GeometryInstance3D.ShadowCastingSetting CastShadow, bool? Visible) ResolveReachApplication(
		GeometryInstance3D.ShadowCastingSetting granted, bool withinReach)
	{
		if (granted == GeometryInstance3D.ShadowCastingSetting.ShadowsOnly)
		{
			return (GeometryInstance3D.ShadowCastingSetting.ShadowsOnly, withinReach);
		}

		return (withinReach ? granted : GeometryInstance3D.ShadowCastingSetting.Off, null);
	}

	public static float ReadShadowReachScale()
	{
		try
		{
			return Mathf.Clamp(
				Refii.Core.Engine_RefiiConfig.Float(
					Refii.Core.Engine_RefiiConfig.Read(out _), "sun_shadow_reach_scale", 1.0f),
				0.1f, 5.0f);
		}
		catch (System.Exception)
		{
			return 1.0f;
		}
	}

	/// <summary>Seconds a full pass over the region takes: the work is a slice per frame.</summary>
	private const double PassSeconds = 0.5;

	/// <summary>Seconds between re-walks of the region tree for instances the streamer added.</summary>
	private const double RecollectSeconds = 10.0;

	private sealed class Entry
	{
		public GeometryInstance3D Node = null!;

		/// <summary>
		/// Taken WHEN THE NODE WAS ALIVE and never asked for again. A freed Godot object leaves a
		/// DISPOSED C# wrapper behind, and every member on it — `GetInstanceId` included — throws
		/// `ObjectDisposedException`; guest-drawn props retire constantly.
		/// </summary>
		public ulong Id;
		public bool Measured;
		public Aabb Bounds;
		public float ItemExtent;
		public GeometryInstance3D.ShadowCastingSetting Applied = (GeometryInstance3D.ShadowCastingSetting)(-1);

		/// <summary>What the instance cast with when taken — On, or ShadowsOnly for a cast-only proxy.</summary>
		public GeometryInstance3D.ShadowCastingSetting Granted = GeometryInstance3D.ShadowCastingSetting.On;
	}

	/// <summary>
	/// Instances outside the region the feed hands over: the props the GUEST draws, which hang
	/// beside the region where the walk below cannot reach them. Only entities with no skeleton
	/// are offered: a character is what the player is looking at and keeps its shadow.
	/// </summary>
	private static readonly List<GeometryInstance3D> _offered = new();

	/// <summary>Instances taken back, by id: a prop that turned out to be a character.</summary>
	private static readonly HashSet<ulong> _withdrawn = new();

	/// <summary>Offers this policy a guest-drawn prop. Main thread only.</summary>
	public static void Adopt(GeometryInstance3D instance)
	{
		// Not mounted under classic (GameWorld): nothing would drain the list.
		if (!Refii.Models.Engine_ClassicMaterial.Active && GodotObject.IsInstanceValid(instance))
		{
			_offered.Add(instance);
		}
	}

	/// <summary>Takes one back and restores its shadow. Main thread only.</summary>
	public static void Release(GeometryInstance3D instance)
	{
		// A caller may hand over something already gone. Touching a freed node's wrapper throws
		// rather than returning; the pass drops what it no longer holds by itself.
		if (!GodotObject.IsInstanceValid(instance))
		{
			return;
		}

		if (instance.CastShadow == GeometryInstance3D.ShadowCastingSetting.ShadowsOnly)
		{
			instance.Visible = true;
		}
		else
		{
			instance.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
		}
		_withdrawn.Add(instance.GetInstanceId());
	}

	/// <summary>Instances whose OWNER switched the caster off, by id: never written again.</summary>
	private static readonly HashSet<ulong> _excluded = new();

	/// <summary>
	/// The owner's "this never casts", for an instance this policy may already hold — the
	/// streamer learns a plant sways only when its mesh arrives. Main thread only.
	/// </summary>
	public static void Exclude(GeometryInstance3D instance)
	{
		if (!GodotObject.IsInstanceValid(instance))
		{
			return;
		}

		instance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		_excluded.Add(instance.GetInstanceId());
	}

	private readonly List<GeometryInstance3D> _instances = new();
	private readonly List<Entry> _entries = new();
	private readonly Dictionary<ulong, Entry> _known = new();
	private int _cursor;
	private double _recollectSince = double.MaxValue;
	private int _suppressedThisPass;
	private readonly Vector3[] _eyes = new Vector3[2];

	/// <summary>
	/// A slice of the region per frame, never the whole of it. Bounds are measured once — the
	/// region's props do not move — the tree is re-walked every <see cref="RecollectSeconds"/>
	/// for what the streamer added, and the caster flag is written only when it changes.
	/// </summary>
	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("shadowreach");
		Camera3D? camera = Refii.Core.Engine_Views.Primary(GetViewport()?.GetCamera3D());
		if (camera is null || GetParent() is not Node region)
		{
			return;
		}

		_recollectSince += delta;
		if (_recollectSince >= RecollectSeconds)
		{
			_recollectSince = 0;
			Collect(region);
			foreach (GeometryInstance3D instance in _instances)
			{
				Take(instance);
			}
		}

		if (_offered.Count != 0)
		{
			foreach (GeometryInstance3D instance in _offered)
			{
				if (GodotObject.IsInstanceValid(instance))
				{
					Take(instance);
				}
			}

			_offered.Clear();
		}

		if (_entries.Count == 0)
		{
			return;
		}

		// THE NEAREST EYE. On split screen a prop casts while it is within reach of either view.
		int eyes = 0;
		foreach (Camera3D view in Refii.Core.Engine_Views.Cameras(camera))
		{
			if (eyes < _eyes.Length)
			{
				_eyes[eyes++] = view.GlobalPosition;
			}
		}

		float reachScale = ReadShadowReachScale();
		int slice = System.Math.Max(1, (int)System.Math.Ceiling(_entries.Count * delta / PassSeconds));
		for (int n = 0; n < slice && _entries.Count != 0; n++)
		{
			if (_cursor >= _entries.Count)
			{
				_cursor = 0;
				Suppressed = _suppressedThisPass;
				Considered = _entries.Count;
				_suppressedThisPass = 0;

				// Every entry has been offered a withdrawal by now; what is left names
				// something this policy does not hold, and would otherwise never be dropped.
				_withdrawn.Clear();
			}

			Entry entry = _entries[_cursor];
			// A body that casts through Engine_ShadowProxy is not this policy's: its proxy is.
			if (!GodotObject.IsInstanceValid(entry.Node) || _withdrawn.Contains(entry.Id) ||
				_excluded.Contains(entry.Id) || Refii.Entities.Engine_ShadowProxy.IsProxied(entry.Id))
			{
				_known.Remove(entry.Id);
				_withdrawn.Remove(entry.Id);
				_excluded.Remove(entry.Id);
				_entries.RemoveAt(_cursor);
				continue;
			}

			++_cursor;
			if (!entry.Measured)
			{
				if (!TryMeasure(entry.Node, out entry.Bounds, out entry.ItemExtent))
				{
					continue;
				}

				entry.Measured = true;
			}

			float baseReach = CalculateShadowReach(entry.ItemExtent, reachScale);
			float distance = float.PositiveInfinity;
			for (int e = 0; e < eyes; e++)
			{
				Vector3 eye = _eyes[e];
				distance = Mathf.Min(distance, eye.Clamp(entry.Bounds.Position, entry.Bounds.End).DistanceTo(eye));
			}

			float reachThreshold = entry.Applied != GeometryInstance3D.ShadowCastingSetting.Off &&
				entry.Applied != (GeometryInstance3D.ShadowCastingSetting)(-1)
				? baseReach + HysteresisMetres
				: baseReach;
			bool withinReach = !Enabled || distance <= reachThreshold;
			GeometryInstance3D.ShadowCastingSetting wanted =
				withinReach
					? entry.Granted
					: GeometryInstance3D.ShadowCastingSetting.Off;

			if (wanted == GeometryInstance3D.ShadowCastingSetting.Off)
			{
				++_suppressedThisPass;
			}

			if (entry.Applied != wanted)
			{
				var (castShadow, visible) = ResolveReachApplication(entry.Granted, withinReach);
				if (entry.Node.CastShadow != castShadow)
				{
					entry.Node.CastShadow = castShadow;
				}

				if (visible.HasValue && entry.Node.Visible != visible.Value)
				{
					entry.Node.Visible = visible.Value;
				}

				entry.Applied = wanted;
			}
		}
	}

	private static bool TryMeasure(GeometryInstance3D node, out Aabb worldBounds, out float longestItemAxis)
	{
		if (node is MultiMeshInstance3D mmi)
		{
			if (mmi.Multimesh is not { } mm || mm.Mesh is not { } batchMesh)
			{
				worldBounds = default;
				longestItemAxis = 0f;
				return false;
			}

			Aabb localBounds = mm.CustomAabb.Size != Vector3.Zero ? mm.CustomAabb : batchMesh.GetAabb();
			worldBounds = mmi.GlobalTransform * localBounds;
			Vector3 itemSize = batchMesh.GetAabb().Size;
			longestItemAxis = Mathf.Max(itemSize.X, Mathf.Max(itemSize.Y, itemSize.Z));
			return true;
		}

		if (node is MeshInstance3D mi)
		{
			if (mi.Mesh is not { } mesh)
			{
				worldBounds = default;
				longestItemAxis = 0f;
				return false;
			}

			Aabb meshAabb = mesh.GetAabb();
			Aabb local = mi.CustomAabb.Size != Vector3.Zero ? mi.CustomAabb : meshAabb;
			worldBounds = mi.GlobalTransform * local;
			Vector3 itemSize = meshAabb.Size * mi.GlobalTransform.Basis.Scale.Abs();
			longestItemAxis = Mathf.Max(itemSize.X, Mathf.Max(itemSize.Y, itemSize.Z));
			return true;
		}

		Aabb custom = node.CustomAabb;
		if (custom.Size == Vector3.Zero)
		{
			worldBounds = default;
			longestItemAxis = 0f;
			return false;
		}

		worldBounds = node.GlobalTransform * custom;
		Vector3 size = worldBounds.Size;
		longestItemAxis = Mathf.Max(size.X, Mathf.Max(size.Y, size.Z));
		return true;
	}

	private void Take(GeometryInstance3D instance)
	{
		if (!GodotObject.IsInstanceValid(instance))
		{
			return;
		}

		ulong id = instance.GetInstanceId();

		// Taking back cancels a withdrawal, and this runs before the pass that acts on one.
		// A prop that changes mesh releases and re-offers itself on the same frame; without
		// this the pass would find the id still withdrawn and drop the entry for good.
		if (_known.TryGetValue(id, out Entry? held))
		{
			_withdrawn.Remove(id);
			held.Measured = false;
			return;
		}

		// The policy withdraws a shadow; it never grants one. An instance its owner built
		// with the caster off (foliage, FX) is not this policy's to switch on.
		GeometryInstance3D.ShadowCastingSetting granted = instance.CastShadow;
		if (granted == GeometryInstance3D.ShadowCastingSetting.Off ||
			Refii.Entities.Engine_ShadowProxy.IsProxied(id))
		{
			return;
		}

		_withdrawn.Remove(id);
		var entry = new Entry { Node = instance, Id = id, Granted = granted };
		_known[id] = entry;
		_entries.Add(entry);
	}

	private void Collect(Node region)
	{
		_instances.Clear();
		Walk(region);
	}

	private void Walk(Node node)
	{
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = node.GetChild(i);
			// Characters and the entities the guest places are NOT dressing: they are what the
			// player is looking at, they move, and there are few of them. The feed hangs them
			// beside the region, so this walk does not reach them at all — but a rig inside a
			// region (a level's own animated piece) is left alone here as well.
			if (child is Skeleton3D)
			{
				continue;
			}

			if (child is GeometryInstance3D instance)
			{
				_instances.Add(instance);
			}

			Walk(child);
		}
	}
}
