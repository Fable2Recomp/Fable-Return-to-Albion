using System;
using System.Collections.Generic;
using Godot;
using Refii.Models;

namespace Refii.Level;

/// <summary>
/// `prop_far_shading_metres`: a prop whose nearest point is past this distance draws its lite
/// surfaces on a FAR build, the same shader with the normal map compiled out
/// (<see cref="Engine_ShaderSpecialisation.FarCopy"/>). 0 is off.
/// </summary>
/// <remarks>
/// <para>WHY. On the Adreno 640 only compile-time removal of executed work paid; the normal map
/// compiled out of every model surface measured -5% GPU (DIAG_NO_NORMALMAP), and it cannot be
/// removed everywhere without flattening what is near. Past 25-40 m, at a 30% render scale, a
/// prop's normal map is sub-pixel: the far build keeps its albedo, specular, bake and fog.</para>
/// <para>HOW. A per-node material swap, not a second batch under visibility ranges: the ranges
/// already carry the props' draw distances and fades, and a twin would have to mirror every
/// instance uniform the lighting writes. A single-surface batch takes the far copy as its
/// MaterialOverride, a single prop as surface overrides; a node carrying anyone else's override
/// is left alone. A MULTI-SURFACE BATCH STAYS NEAR: Godot's MultiMesh draws ignore surface
/// overrides (render_forward_mobile.cpp, `_geometry_instance_update`), and a far copy of its mesh
/// would double its vertex memory on a phone near its limit. The load log counts both.</para>
/// <para>THE SWITCH is measured to the node's world box (its nearest point, so a long batch turns
/// near as soon as its near end does), with <see cref="HysteresisMetres"/> between the two edges,
/// over <see cref="NodesPerFrame"/> nodes a frame. It is an instant material change; at the
/// distance it happens the only difference is the lost relief shading.</para>
/// <para>PIPELINES. The far copies' builds are new shaders: Engine_PipelineWarmup draws every
/// registered prop under both materials at load (<see cref="WarmMaterials"/>), so a far switch
/// in play compiles nothing on a GPU without ubershaders.</para>
/// <para>Lite only (Android): the full shaders have no far build. Live: a `cvar_set` of the key
/// moves every node on the next sweep; 0 puts all of them back.</para>
/// </remarks>
public static class Engine_PropFarShading
{
	public const string Key = "prop_far_shading_metres";

	/// <summary>Metres between the near and far edges, so a prop at the edge does not flicker.</summary>
	public const float HysteresisMetres = 3f;

	/// <summary>Nodes measured a frame: a town's few thousand in well under a second.</summary>
	public const int NodesPerFrame = 256;

	private sealed class Entry
	{
		public required GeometryInstance3D Node;
		public Aabb World;
		public bool Measured;
		public bool Far;
	}

	private static readonly List<Entry> Entries = new();
	private static readonly HashSet<ulong> Registered = new();
	private static int _next;
	private static ulong _tickedFrame = ulong.MaxValue;
	private static float _metres = float.NaN;
	private static bool _hooked;
	private static int _skippedMultiSurface;
	private static int _swaps;
	private static int _far;
	private static bool _reported;

	/// <summary>The distance in force (0: off). Lite only.</summary>
	public static float Metres
	{
		get
		{
			if (float.IsNaN(_metres))
			{
				Reload();
			}

			return _metres;
		}
	}

	public static bool Active => Metres > 0f;

	private static void Reload()
	{
		float metres = 0f;
		try
		{
			metres = Engine_ShaderVariant.Lite
				? Refii.Core.Engine_RefiiConfig.Float(Refii.Core.Engine_RefiiConfig.Read(out _), Key, 0f)
				: 0f;
		}
		catch (Exception)
		{
			metres = 0f;
		}

		_metres = metres > 0f && float.IsFinite(metres) ? metres : 0f;
		if (!_hooked)
		{
			_hooked = true;
			Refii.Core.Engine_RefiiConfig.OnConfigChanged += (name, _) =>
			{
				if (string.Equals(name, Key, StringComparison.OrdinalIgnoreCase))
				{
					_metres = float.NaN;
					// The census again after the next whole sweep, so a live change can be checked in the log.
					_reported = false;
				}
			};
		}
	}

	/// <summary>
	/// Whether a prop at <paramref name="distance"/> draws far, given where it is now. Pure: far past
	/// the edge plus the hysteresis, near again inside the edge, unchanged between; off is near.
	/// </summary>
	public static bool Decide(float distance, float metres, bool far) =>
		metres > 0f && (far ? distance >= metres : distance > metres + HysteresisMetres);

	/// <summary>A prop's mesh has stood (Engine_PropStreamer): it takes part from the next sweep. Main thread.</summary>
	public static void Register(GeometryInstance3D node, Mesh mesh)
	{
		// Whenever lite draws, so a live `cvar_set` can turn it on in play (unwarmed then).
		if (!Engine_ShaderVariant.Lite || mesh.GetSurfaceCount() == 0)
		{
			return;
		}

		if (node is MultiMeshInstance3D && mesh.GetSurfaceCount() != 1)
		{
			++_skippedMultiSurface;
			return;
		}

		if (node is not (MultiMeshInstance3D or MeshInstance3D) || !Registered.Add(node.GetInstanceId()))
		{
			return;
		}

		Entries.Add(new Entry { Node = node });
	}

	/// <summary>Whether a node takes part, for the pipeline warm-up.</summary>
	public static bool IsRegistered(GodotObject node) => Registered.Contains(node.GetInstanceId());

	/// <summary>For a level change, where the props go away.</summary>
	public static void Forget()
	{
		Entries.Clear();
		Registered.Clear();
		_next = 0;
		_far = 0;
		_reported = false;
	}

	/// <summary>
	/// Whether a sweep has nothing to do: no props, or the key off with none left far. Pure.
	/// </summary>
	public static bool Idle(int entries, float metres, int far) => entries == 0 || (metres <= 0f && far == 0);

	/// <summary>
	/// The sweep from a node that processes every frame (AndroidGameHost), measured from its
	/// viewport's camera. The streamers call <see cref="Tick(Camera3D?)"/> too, but each stops
	/// processing once its props have settled, which used to end the sweep with the load.
	/// </summary>
	public static void Tick(Node owner)
	{
		if (!Idle(Entries.Count, Metres, _far))
		{
			Tick(owner.GetViewport()?.GetCamera3D());
		}
	}

	/// <summary>One frame's slice of the sweep. Once per process frame whoever calls it.</summary>
	public static void Tick(Camera3D? camera)
	{
		ulong frame = Engine.GetProcessFrames();
		if (frame == _tickedFrame || Idle(Entries.Count, Metres, _far) || camera is null)
		{
			return;
		}

		_tickedFrame = frame;
		float metres = Metres;
		Vector3 eye = camera.GlobalPosition;
		int budget = Math.Min(NodesPerFrame, Entries.Count);
		for (int n = 0; n < budget && Entries.Count != 0; ++n)
		{
			if (_next >= Entries.Count)
			{
				_next = 0;
				if (!_reported && Active)
				{
					_reported = true;
					GD.Print($"[props] {Census()}");
				}
			}

			Entry entry = Entries[_next];
			if (!GodotObject.IsInstanceValid(entry.Node))
			{
				_far -= entry.Far ? 1 : 0;
				Entries[_next] = Entries[^1];
				Entries.RemoveAt(Entries.Count - 1);
				continue;
			}

			++_next;
			if (!entry.Node.IsInsideTree())
			{
				continue;
			}

			if (!entry.Measured)
			{
				entry.World = entry.Node.GlobalTransform * (entry.Node is MultiMeshInstance3D { Multimesh: { } mm }
					? mm.CustomAabb.HasVolume() ? mm.CustomAabb : mm.GetAabb()
					: (entry.Node as MeshInstance3D)?.Mesh?.GetAabb() ?? default);
				entry.Measured = true;
			}

			bool far = Decide(Distance(entry.World, eye), metres, entry.Far);
			if (far != entry.Far)
			{
				entry.Far = far;
				_far += far ? 1 : -1;
				Apply(entry.Node, far);
				++_swaps;
			}
		}
	}

	/// <summary>Metres from a point to the nearest point of a box (0 inside it).</summary>
	public static float Distance(Aabb box, Vector3 point)
	{
		Vector3 end = box.End;
		float dx = MathF.Max(MathF.Max(box.Position.X - point.X, 0f), point.X - end.X);
		float dy = MathF.Max(MathF.Max(box.Position.Y - point.Y, 0f), point.Y - end.Y);
		float dz = MathF.Max(MathF.Max(box.Position.Z - point.Z, 0f), point.Z - end.Z);
		return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
	}

	private static void Apply(GeometryInstance3D node, bool far)
	{
		// The node's mesh as it is now: a single prop's vertex bake replaces it with a private copy.
		if (node is MultiMeshInstance3D { Multimesh.Mesh: { } mesh } multi)
		{
			Material? current = multi.MaterialOverride;
			if (current is not null && !Engine_ShaderSpecialisation.IsFarCopy(current))
			{
				return;
			}

			Material? want = far ? Engine_ShaderSpecialisation.FarCopy(mesh.SurfaceGetMaterial(0)) : null;
			if (!ReferenceEquals(current, want))
			{
				multi.MaterialOverride = want;
			}
		}
		else if (node is MeshInstance3D { Mesh: { } singleMesh } single)
		{
			int count = Math.Min(singleMesh.GetSurfaceCount(), single.GetSurfaceOverrideMaterialCount());
			for (int i = 0; i < count; ++i)
			{
				Material? current = single.GetSurfaceOverrideMaterial(i);
				if (current is not null && !Engine_ShaderSpecialisation.IsFarCopy(current))
				{
					continue;
				}

				Material? want = far ? Engine_ShaderSpecialisation.FarCopy(singleMesh.SurfaceGetMaterial(i)) : null;
				if (!ReferenceEquals(current, want))
				{
					single.SetSurfaceOverrideMaterial(i, want);
				}
			}
		}
	}

	/// <summary>
	/// The material the warm-up should draw a registered node's surface with in each state: the
	/// near (shared) one and its far copy, or null where there is no far build.
	/// </summary>
	public static (Material? Near, Material? Far) WarmMaterials(Material? current)
	{
		Material? near = Engine_ShaderSpecialisation.NearOf(current);
		return (near, Engine_ShaderSpecialisation.FarCopy(near));
	}

	/// <summary>For the load log and `metrics`: nodes taking part, drawing far, swaps, batches left near.</summary>
	public static string Census()
	{
		int far = 0;
		foreach (Entry entry in Entries)
		{
			if (entry.Far)
			{
				++far;
			}
		}

		return $"{Key}={Metres:0.#}: {Entries.Count} node(s), {far} far, {_swaps} swap(s), " +
			$"{_skippedMultiSurface} multi-surface batch(es) left near";
	}
}
