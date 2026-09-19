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

	/// <summary>Longest AABB axis under which a thing is dressing rather than architecture.</summary>
	private const float SmallMetres = 3.0f;

	/// <summary>And over which it is landscape or a building, and always casts.</summary>
	private const float LargeMetres = 12.0f;

	/// <summary>How far dressing casts, and how far mid-sized geometry casts.</summary>
	private const float SmallReach = 60.0f;

	private const float MidReach = 150.0f;

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
		public float Reach;
		public GeometryInstance3D.ShadowCastingSetting Applied = (GeometryInstance3D.ShadowCastingSetting)(-1);
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
		if (GodotObject.IsInstanceValid(instance))
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

		instance.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
		_withdrawn.Add(instance.GetInstanceId());
	}

	private readonly List<GeometryInstance3D> _instances = new();
	private readonly List<Entry> _entries = new();
	private readonly Dictionary<ulong, Entry> _known = new();
	private int _cursor;
	private double _recollectSince = double.MaxValue;
	private int _suppressedThisPass;

	/// <summary>
	/// A slice of the region per frame, never the whole of it. Bounds are measured once — the
	/// region's props do not move — the tree is re-walked every <see cref="RecollectSeconds"/>
	/// for what the streamer added, and the caster flag is written only when it changes.
	/// </summary>
	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("shadowreach");
		Camera3D? camera = GetViewport()?.GetCamera3D();
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

		Vector3 eye = camera.GlobalPosition;
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
			if (!GodotObject.IsInstanceValid(entry.Node) || _withdrawn.Contains(entry.Id))
			{
				_known.Remove(entry.Id);
				_withdrawn.Remove(entry.Id);
				_entries.RemoveAt(_cursor);
				continue;
			}

			++_cursor;
			if (!entry.Measured)
			{
				entry.Bounds = entry.Node.GlobalTransform * entry.Node.GetAabb();
				Vector3 size = entry.Bounds.Size;
				float longest = Mathf.Max(size.X, Mathf.Max(size.Y, size.Z));
				entry.Reach = longest >= LargeMetres
					? float.PositiveInfinity
					: longest >= SmallMetres ? MidReach : SmallReach;
				entry.Measured = true;
			}

			float distance = eye.Clamp(entry.Bounds.Position, entry.Bounds.End).DistanceTo(eye);
			GeometryInstance3D.ShadowCastingSetting wanted =
				!Enabled || distance <= entry.Reach
					? GeometryInstance3D.ShadowCastingSetting.On
					: GeometryInstance3D.ShadowCastingSetting.Off;

			if (wanted == GeometryInstance3D.ShadowCastingSetting.Off)
			{
				++_suppressedThisPass;
			}

			if (entry.Applied != wanted)
			{
				entry.Node.CastShadow = wanted;
				entry.Applied = wanted;
			}
		}
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
			if (_withdrawn.Remove(id))
			{
				held.Measured = false;
			}

			return;
		}

		// Distance culling may suppress an existing caster, but must not enable shadows
		// explicitly disabled by its owner (for example, foliage batches).
		if (instance.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off)
		{
			return;
		}

		_withdrawn.Remove(id);
		var entry = new Entry { Node = instance, Id = id };
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
