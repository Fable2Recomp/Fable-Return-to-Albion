using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Refii.Lighting;

/// <summary>
/// A MOD'S LIGHTS (<c>light</c> / <c>light_remove</c> on every surface: <c>refii_mod_light</c>,
/// <c>Refii.light</c>, <c>RefiiHost.light</c>): an omni light a mod places at a world point or on
/// an entity — in the entity's own frame, at a model helper when one is named — placed by the one
/// placement every mod-placed node takes (<see cref="Refii.Fx.Engine_FxPlayer.Mount"/>, the
/// <c>play_fx_on</c> path), so it rides the body, the bone and the helper exactly as an effect
/// does. Native and Lua requests cross the mod bus under the effect kind
/// (<see cref="Refii.Fx.Engine_FxPlayer.RequestKind"/>, <c>op='light'</c> / <c>'light_off'</c>);
/// a Godot script's call lands here directly. Any surface's id removes any surface's light.
/// </summary>
/// <remarks>
/// <para>MODERN: the light is the node the FX feed's own point lights and a recipe's glow are
/// (<see cref="Refii.Fx.Engine_FxRecipeLight"/>: an <see cref="OmniLight3D"/> with zero decay —
/// the form <c>fable_attenuation</c> inverts — no shadow map, an optional waver of its energy).
/// It is a DYNAMIC light: it is not registered in the placed lights' table (a placed fixture never
/// moves; this one rides an entity), takes no shadow budget, and bakes into nothing. Godot's one
/// lighting model lights every surface with it, the FX feed's lights beside it.</para>
/// <para>CLASSIC: the classic renderer lights a draw from the guest's own light list
/// (<see cref="Refii.Models.Engine_ClassicLights"/>), so the mod light is appended to that list
/// each frame as a point light in the guest's own 20-float form — position in the guest's axes
/// from the node's place this frame, colour, intensity, range as its radius — with an index past
/// the manager's 64 slots and the second view's 64, the way player two's lights join the list
/// (<c>GatherSecondView</c>). Every draw whose bounds its sphere meets takes it, grouped and passed
/// as the title groups its own. The node itself is hidden under the classic look (the translated
/// shaders read the list, not Godot's lights) and serves as the point.</para>
/// <para>A light on an entity dies with it: the node is the body's child (or its bone's) and is
/// freed with it, and the registry drops it on the next pump. Everything goes when the game goes
/// down (<see cref="Clear"/>). Ids: the payload issues 1 .. 2^23-1, this side from
/// <see cref="FirstHostId"/>.</para>
/// </remarks>
public static class Engine_ModLights
{
	public const uint FirstHostId = 0x800000u;
	private const uint LastHostId = 0xFFFFFFu;

	/// <summary>How long a request waits for its entity's node or the effect feed before it is dropped.</summary>
	private const double PendingSeconds = 2.0;

	/// <summary>The first classic list index a mod light takes: past the manager's 64 and the second view's 64.</summary>
	public const int FirstClassicIndex = 128;

	/// <summary>The most lights the classic list takes from mods in one frame (the list is bounded).</summary>
	public const int MaxClassicLights = 32;

	/// <summary>The range a request without one takes, and the ceiling on any (metres).</summary>
	public const float DefaultRange = 6f;
	public const float MaxRange = 100f;

	/// <summary>
	/// One request as the bus carries it: <c>op='light',id=…,entity=…,x=,y=,z=,r=,g=,b=,energy=,range=,ox=,oy=,oz=,at='…',flicker=</c>
	/// or <c>op='light_off',id=…</c>. <paramref name="Entity"/> is a handle (0: the world point
	/// <paramref name="X"/>/<paramref name="Y"/>/<paramref name="Z"/>, guest axes); the offset is
	/// along the entity's (or the helper's) axes; <paramref name="Flicker"/> 0..1 wavers the energy.
	/// </summary>
	public readonly record struct Request(
		string Op, uint Id, uint Entity, float X, float Y, float Z, float R, float G, float B, float Energy, float Range,
		float OffsetX, float OffsetY, float OffsetZ, string At, float Flicker)
	{
		public bool IsPlace => Op == "light";
	}

	/// <summary>Whether a bus text under the effect kind is a light request (<c>op='light'</c> or <c>'light_off'</c>).</summary>
	public static bool IsLightRequest(string fields) =>
		fields.Contains("op='light'", StringComparison.Ordinal) || fields.Contains("op='light_off'", StringComparison.Ordinal);

	/// <summary>The request in <paramref name="fields"/>, or null when it is not one (no light op, no id).</summary>
	public static Request? ParseRequest(string fields)
	{
		var values = new Dictionary<string, object>(StringComparer.Ordinal);
		foreach ((string key, object value) in Refii.Mods.RefiiHost.ParseFieldsPlain(fields))
		{
			values[key] = value;
		}

		string Text(string key) => values.TryGetValue(key, out object? v) && v is string s ? s : string.Empty;
		float Number(string key, float fallback) => values.TryGetValue(key, out object? v)
			? v switch { long l => l, double d => (float)d, bool b => b ? 1f : 0f, _ => fallback }
			: fallback;

		string op = Text("op");
		double rawId = Number("id", 0f);
		uint id = rawId is > 0 and <= uint.MaxValue ? (uint)rawId : 0u;
		if (id == 0 || op is not ("light" or "light_off"))
		{
			return null;
		}

		float entity = Number("entity", 0f);
		return new Request(
			op, id, entity is > 0f and <= uint.MaxValue ? (uint)entity : 0u,
			Number("x", 0f), Number("y", 0f), Number("z", 0f),
			Number("r", 1f), Number("g", 1f), Number("b", 1f), Number("energy", 1f), Number("range", DefaultRange),
			Number("ox", 0f), Number("oy", 0f), Number("oz", 0f), Text("at").Trim(), Number("flicker", 0f));
	}

	private sealed class Placed
	{
		public Request Request;
		public Refii.Fx.Engine_FxRecipeLight? Node;
		public BoneAttachment3D? Hook;
		public uint Address;
		public bool Follows;
		public double Waited;
	}

	private static readonly Dictionary<uint, Placed> _lights = new();
	private static readonly List<uint> _done = new();
	private static uint _nextHostId = FirstHostId;

	/// <summary>Lights placed or waiting to be placed, for the debug channel.</summary>
	public static int Count => _lights.Count;

	/// <summary>A request from the bus: places or removes.</summary>
	public static void Handle(string fields)
	{
		if (ParseRequest(fields) is not { } request)
		{
			GD.PushWarning($"[mods] light: a request the host does not read: {fields}");
			return;
		}

		if (request.IsPlace)
		{
			Start(request);
		}
		else
		{
			Remove(request.Id);
		}
	}

	/// <summary>
	/// Places a light for a Godot script: on <paramref name="entity"/> (a handle; 0 for the world
	/// point <paramref name="guestPosition"/>), <paramref name="colour"/> at <paramref name="energy"/>
	/// over <paramref name="range"/> metres, <paramref name="offset"/> along the entity's axes,
	/// <paramref name="at"/> a model helper or "", <paramref name="flicker"/> 0..1. The id
	/// <see cref="Remove"/> takes.
	/// </summary>
	public static uint Place(uint entity, Vector3 guestPosition, Color colour, float energy, float range, Vector3 offset, string at, float flicker)
	{
		uint id = _nextHostId;
		_nextHostId = _nextHostId >= LastHostId ? FirstHostId : _nextHostId + 1;
		Start(new Request(
			"light", id, entity, guestPosition.X, guestPosition.Y, guestPosition.Z, colour.R, colour.G, colour.B, energy, range,
			offset.X, offset.Y, offset.Z, (at ?? string.Empty).Trim(), flicker));
		return id;
	}

	/// <summary>Removes any surface's id; false when no light stands under it.</summary>
	public static bool Remove(uint id)
	{
		if (!_lights.Remove(id, out Placed? placed))
		{
			return false;
		}

		Free(placed);
		return true;
	}

	/// <summary>Everything goes (the game goes down).</summary>
	public static void Clear()
	{
		foreach (Placed placed in _lights.Values)
		{
			Free(placed);
		}

		_lights.Clear();
	}

	/// <summary>The lights standing, for <c>RefiiHost.lights</c>: id, entity handle (0 for a point), colour, energy, range, where it is now (guest axes).</summary>
	public static List<(uint Id, uint Entity, Color Colour, float Energy, float Range, Vector3 GuestPosition)> All()
	{
		var list = new List<(uint, uint, Color, float, float, Vector3)>(_lights.Count);
		foreach ((uint id, Placed placed) in _lights)
		{
			Request r = placed.Request;
			Vector3 where = placed.Node is { } node && GodotObject.IsInstanceValid(node) && node.IsInsideTree()
				? GodotToGuest(node.GlobalPosition)
				: new Vector3(r.X, r.Y, r.Z);
			list.Add((id, r.Entity, new Color(r.R, r.G, r.B), r.Energy, r.Range, where));
		}

		list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
		return list;
	}

	private static void Start(Request request)
	{
		if (_lights.Remove(request.Id, out Placed? earlier))
		{
			Free(earlier);
		}

		var placed = new Placed
		{
			Request = request,
			Address = request.Entity != 0 ? Refii.Host.HostBridge.ModHandleAddress(request.Entity) : 0u,
		};
		if (request.Entity != 0 && placed.Address == 0)
		{
			GD.PushWarning($"[mods] light: id {request.Id} dropped: entity handle {request.Entity} is unknown");
			return;
		}

		_lights[request.Id] = placed;
		TryPlace(placed);
	}

	/// <summary>Once per frame: places what waits, keeps a followed light on its point, drops what lost its node.</summary>
	public static void Pump(double delta)
	{
		_done.Clear();
		foreach ((uint id, Placed placed) in _lights)
		{
			if (placed.Node is null)
			{
				placed.Waited += delta;
				if (TryPlace(placed) != Refii.Fx.Engine_FxPlayer.Placing.Placed && placed.Waited >= PendingSeconds)
				{
					GD.PushWarning(placed.Request.Entity != 0
						? $"[mods] light: id {id} dropped: entity handle {placed.Request.Entity} has no drawn node"
						: $"[mods] light: id {id} dropped: no effect feed in the tree");
					_done.Add(id);
				}

				continue;
			}

			if (!GodotObject.IsInstanceValid(placed.Node) || !placed.Node.IsInsideTree())
			{
				_done.Add(id);
				continue;
			}

			if (placed.Follows && Refii.Game.GameEntities.Current?.NodeFor(placed.Address) is { } body &&
				GodotObject.IsInstanceValid(body) && body.IsInsideTree())
			{
				// The body has come: made again in the body's frame (as a followed effect is).
				Free(placed);
				TryPlace(placed);
				continue;
			}

			if (placed.Follows)
			{
				Request r = placed.Request;
				if (Refii.Fx.Engine_FxPlayer.FollowedPoint(placed.Address) is { } point)
				{
					placed.Waited = 0;
					placed.Node.GlobalPosition = point + Refii.Game.GameFx.GuestToGodot(new Vector3(r.OffsetX, r.OffsetY, r.OffsetZ));
				}
				else if ((placed.Waited += delta) >= PendingSeconds)
				{
					_done.Add(id);
				}
			}
		}

		foreach (uint id in _done)
		{
			Remove(id);
		}
	}

	private static Refii.Fx.Engine_FxPlayer.Placing TryPlace(Placed placed)
	{
		Request r = placed.Request;
		Refii.Fx.Engine_FxRecipeLight node = MakeNode(
			$"ModLight_{r.Id.ToString(CultureInfo.InvariantCulture)}", new Color(r.R, r.G, r.B), r.Energy, r.Range, r.Flicker);
		Refii.Fx.Engine_FxPlayer.Placing placing = Refii.Fx.Engine_FxPlayer.Mount(
			node, r.Entity != 0, placed.Address, new Vector3(r.X, r.Y, r.Z), r.At, new Vector3(r.OffsetX, r.OffsetY, r.OffsetZ),
			$"ModLightHook_{r.Id.ToString(CultureInfo.InvariantCulture)}", out placed.Hook, out placed.Follows);
		if (placing != Refii.Fx.Engine_FxPlayer.Placing.Placed)
		{
			node.QueueFree();
			return placing;
		}

		placed.Node = node;
		return placing;
	}

	/// <summary>
	/// The node a mod light is: the recipe glow's omni (zero decay, no shadow, the waver), lit under
	/// the modern look and hidden under the classic one, where the list it feeds is the light.
	/// </summary>
	public static Refii.Fx.Engine_FxRecipeLight MakeNode(string name, Color colour, float energy, float range, float flicker)
	{
		float reach = float.IsFinite(range) && range > 0f ? Math.Min(range, MaxRange) : DefaultRange;
		float power = float.IsFinite(energy) && energy > 0f ? energy : 0f;
		return new Refii.Fx.Engine_FxRecipeLight
		{
			Name = name,
			LightColor = new Color(Math.Max(colour.R, 0f), Math.Max(colour.G, 0f), Math.Max(colour.B, 0f)),
			LightEnergy = power,
			BaseEnergy = power,
			Flicker = float.IsFinite(flicker) ? Math.Clamp(flicker, 0f, 1f) : 0f,
			OmniRange = reach,
			OmniAttenuation = 0.0f,
			ShadowEnabled = false,
			Visible = !Refii.Models.Engine_ClassicMaterial.Active,
		};
	}

	/// <summary>
	/// THE CLASSIC LIST: every standing light appended to <paramref name="lights"/> as the point
	/// light the guest's manager would hold for it, from <paramref name="firstIndex"/> up; the
	/// count added. Called by <see cref="Refii.Models.Engine_ClassicLights.Pump"/> after the
	/// guest's own and the second view's.
	/// </summary>
	public static int AppendClassic(List<Refii.Models.Engine_ClassicLights.Light> lights, int firstIndex)
	{
		int index = firstIndex;
		foreach (Placed placed in _lights.Values)
		{
			if (index - firstIndex >= MaxClassicLights)
			{
				break;
			}

			if (placed.Node is not { } node || !GodotObject.IsInstanceValid(node) || !node.IsInsideTree())
			{
				continue;
			}

			Request r = placed.Request;
			float range = node.OmniRange;
			if (range <= 0f || node.LightEnergy <= 0f)
			{
				continue;
			}

			lights.Add(new Refii.Models.Engine_ClassicLights.Light(
				GodotToGuest(node.GlobalPosition), range, new Vector3(r.R, r.G, r.B), node.LightEnergy, Vector3.Zero,
				Refii.Models.Engine_ClassicLights.Light.Point, range, 0f, 1f, 0, 0u, index++));
		}

		return index - firstIndex;
	}

	/// <summary>A Godot-axes point in the guest's axes: Godot (x, y, z) is guest (-x, z, y), and back the same way.</summary>
	public static Vector3 GodotToGuest(Vector3 godot) => new(-godot.X, godot.Z, godot.Y);

	private static void Free(Placed placed)
	{
		if (placed.Hook is not null && GodotObject.IsInstanceValid(placed.Hook))
		{
			placed.Hook.QueueFree();
		}
		else if (placed.Node is not null && GodotObject.IsInstanceValid(placed.Node))
		{
			placed.Node.QueueFree();
		}

		placed.Hook = null;
		placed.Node = null;
		placed.Follows = false;
	}
}
