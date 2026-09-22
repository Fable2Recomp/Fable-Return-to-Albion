using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Refii.Lighting;

/// <summary>
/// Which shadow casters move on frames where the guest stated nothing new. Debug instrument.
/// </summary>
/// <remarks>
/// Godot re-renders a positional light's whole shadow map when any instance paired with it that
/// can cast shadows changes transform or bounds (`_update_instance`). So one caster moving every
/// displayed frame inside a lantern's reach re-renders that map every frame, however still
/// everything else is. This samples every visible casting instance for a number of frames and
/// reports the ones that moved on host-only frames (`Engine_GuestFrame.Changed` false), by
/// name and owner, with how many of those frames they moved on. Skeleton writes are not
/// transform changes and are not seen here; the shadow proxies only take those on guest frames.
///
/// `{"op":"casters","dirtiers":120}` starts a sample; `{"op":"casters","dirtiers":0}` reads the report.
/// </remarks>
public sealed partial class Engine_ShadowDirtiers : Node
{
	private sealed class Seen
	{
		public string Name = string.Empty;
		public string Owner = string.Empty;
		public int HostFrames;
		public int GuestFrames;
	}

	private static Engine_ShadowDirtiers? _running;
	private static Dictionary<string, object?>? _lastReport;

	private readonly Dictionary<ulong, (Transform3D At, Aabb Box)> _previous = new();
	private readonly Dictionary<ulong, Seen> _seen = new();
	private int _framesLeft;
	private int _hostFrames;
	private int _guestFrames;

	/// <summary>Starts a sample of <paramref name="frames"/> frames under <paramref name="root"/>; a running one is replaced.</summary>
	public static void Start(Node root, int frames)
	{
		if (_running is not null && GodotObject.IsInstanceValid(_running))
		{
			_running.QueueFree();
		}

		var sampler = new Engine_ShadowDirtiers { Name = "ShadowDirtiers", _framesLeft = frames, ProcessPriority = int.MaxValue };
		root.AddChild(sampler);
		_running = sampler;
		_lastReport = new Dictionary<string, object?> { ["sampling"] = true, ["framesLeft"] = frames };
	}

	public static Dictionary<string, object?> Report() =>
		_lastReport ?? new Dictionary<string, object?> { ["sampling"] = false, ["note"] = "no sample taken" };

	public override void _Process(double delta)
	{
		bool guest = Refii.Core.Engine_GuestFrame.Changed;
		if (guest)
		{
			++_guestFrames;
		}
		else
		{
			++_hostFrames;
		}

		var now = new Dictionary<ulong, (Transform3D At, Aabb Box)>();
		Visit(GetTree().Root, "(root)", guest, now);
		_previous.Clear();
		foreach ((ulong id, var state) in now)
		{
			_previous[id] = state;
		}

		if (--_framesLeft > 0)
		{
			_lastReport = new Dictionary<string, object?> { ["sampling"] = true, ["framesLeft"] = _framesLeft };
			return;
		}

		var rows = new List<Dictionary<string, object?>>();
		foreach (Seen seen in _seen.Values.Where(s => s.HostFrames > 0).OrderByDescending(s => s.HostFrames).Take(60))
		{
			rows.Add(new Dictionary<string, object?>
			{
				["name"] = seen.Name,
				["owner"] = seen.Owner,
				["hostFrames"] = seen.HostFrames,
				["guestFrames"] = seen.GuestFrames,
			});
		}

		var byOwner = new Dictionary<string, int>();
		foreach (Seen seen in _seen.Values.Where(s => s.HostFrames > 0))
		{
			byOwner[seen.Owner] = byOwner.GetValueOrDefault(seen.Owner) + 1;
		}

		_lastReport = new Dictionary<string, object?>
		{
			["sampling"] = false,
			["hostFrames"] = _hostFrames,
			["guestFrames"] = _guestFrames,
			["castersMovedOnHostFrames"] = rows.Count == 60 ? _seen.Values.Count(s => s.HostFrames > 0) : rows.Count,
			["byOwner"] = byOwner,
			["worst"] = rows,
		};
		_running = null;
		QueueFree();
	}

	private void Visit(Node node, string owner, bool guest, Dictionary<ulong, (Transform3D At, Aabb Box)> now)
	{
		string? ns = node.GetType().Namespace;
		if (ns is not null && ns.StartsWith("Refii", System.StringComparison.Ordinal))
		{
			owner = node.GetType().Name;
		}

		if (node is GeometryInstance3D geometry && geometry.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off &&
			geometry.IsVisibleInTree())
		{
			ulong id = geometry.GetInstanceId();
			Aabb box = geometry is MultiMeshInstance3D { Multimesh: { } batch } ? batch.CustomAabb : geometry.GetAabb();
			(Transform3D At, Aabb Box) state = (geometry.GlobalTransform, box);
			now[id] = state;
			if (_previous.TryGetValue(id, out var before) && (before.At != state.At || before.Box != state.Box))
			{
				if (!_seen.TryGetValue(id, out Seen? seen))
				{
					seen = new Seen { Name = $"{geometry.GetClass()} {geometry.Name}", Owner = owner };
					_seen[id] = seen;
				}

				if (guest)
				{
					++seen.GuestFrames;
				}
				else
				{
					++seen.HostFrames;
				}
			}
		}

		foreach (Node child in node.GetChildren())
		{
			Visit(child, owner, guest, now);
		}
	}
}
