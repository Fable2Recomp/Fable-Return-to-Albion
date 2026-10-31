using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Refii.Lighting;

/// <summary>
/// A MOD'S LIGHTS (<c>light</c> / <c>light_remove</c> on every surface: <c>refii_mod_light</c>,
/// <c>Refii.light</c>, <c>RefiiHost.light</c>): an omni light — or, with a cone stated, a SPOT —
/// a mod places at a world point or on an entity — in the entity's own frame, at a model helper
/// when one is named — placed by the one
/// placement every mod-placed node takes (<see cref="Refii.Fx.Engine_FxPlayer.Mount"/>, the
/// <c>play_fx_on</c> path), so it rides the body, the bone and the helper exactly as an effect
/// does. Native and Lua requests cross the mod bus under the effect kind
/// (<see cref="Refii.Fx.Engine_FxPlayer.RequestKind"/>, <c>op='light'</c> / <c>'light_off'</c>);
/// a Godot script's call lands here directly. Any surface's id removes any surface's light.
/// </summary>
/// <remarks>
/// <para>MODERN: the light is the node the FX feed's own point lights and a recipe's glow are
/// (<see cref="Refii.Fx.Engine_FxRecipeLight"/>: an <see cref="OmniLight3D"/> with zero decay —
/// the form <c>fable_attenuation</c> inverts — no shadow map, an optional waver of its energy),
/// or its spot form (<see cref="Engine_ModSpotLight"/>: a <see cref="SpotLight3D"/> with the same
/// decay and waver, the stated cone, its edge falloff the stated attenuation). It is a DYNAMIC
/// light: it is not registered in the placed lights' table (a placed fixture never moves; this
/// one rides an entity), takes no shadow budget, and bakes into nothing. Godot's one lighting
/// model lights every surface with it, the FX feed's lights beside it.</para>
/// <para>CLASSIC: the classic renderer lights a draw from the guest's own light list
/// (<see cref="Refii.Models.Engine_ClassicLights"/>), so the mod light is appended to that list
/// each frame in the guest's own 20-float form — a point light (type 8), or a spot (type 9: the
/// direction the node shines along this frame in the guest's axes, the cone's half-angle, whose
/// cosine the packer writes, with the title's own 1/(1−cos) edge falloff) — position in the
/// guest's axes from the node's place this frame, colour, intensity, range as its radius — with
/// an index past the manager's 64 slots and the second view's 64, the way player two's lights
/// join the list (<c>GatherSecondView</c>). Every draw whose bounds its sphere meets takes it —
/// a spot with no receive frustum stated, as the second view's spots are — grouped and passed as
/// the title groups its own. The node itself is hidden under the classic look (the translated
/// shaders read the list, not Godot's lights) and serves as the point and the axis.</para>
/// <para>THE DIRECTION'S FRAME is the offset's: on an entity the entity's own axes (the helper's
/// when <c>at</c> names one), so a spot on a sword's tip turns with the blade; at a world point
/// the guest's world axes.</para>
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

	/// <summary>The widest cone a spot takes, degrees from its axis to its edge (Godot's own ceiling).</summary>
	public const float MaxCone = 89f;

	/// <summary>
	/// One request as the bus carries it: <c>op='light',id=…,entity=…,x=,y=,z=,r=,g=,b=,energy=,range=,ox=,oy=,oz=,at='…',flicker=,dx=,dy=,dz=,cone=,atten=</c>
	/// or <c>op='light_off',id=…</c>. <paramref name="Entity"/> is a handle (0: the world point
	/// <paramref name="X"/>/<paramref name="Y"/>/<paramref name="Z"/>, guest axes); the offset is
	/// along the entity's (or the helper's) axes; <paramref name="Flicker"/> 0..1 wavers the energy.
	/// A SPOT when <paramref name="Cone"/> is above 0 and the direction has a length: it shines
	/// along <paramref name="DirX"/>/<paramref name="DirY"/>/<paramref name="DirZ"/> (the offset's
	/// axes), <paramref name="Cone"/> degrees from its axis to its edge, <paramref name="Attenuation"/>
	/// its edge falloff (0 or 1: linear, the title's own).
	/// </summary>
	public readonly record struct Request(
		string Op, uint Id, uint Entity, float X, float Y, float Z, float R, float G, float B, float Energy, float Range,
		float OffsetX, float OffsetY, float OffsetZ, string At, float Flicker,
		float DirX = 0f, float DirY = 0f, float DirZ = 0f, float Cone = 0f, float Attenuation = 0f)
	{
		public bool IsPlace => Op == "light";

		/// <summary>Whether the request is a spot: a cone and a direction stated.</summary>
		public bool IsSpot => float.IsFinite(Cone) && Cone > 0f && new Vector3(DirX, DirY, DirZ).LengthSquared() > 1e-8f;

		/// <summary>The stated direction, unit length (zero for an omni light).</summary>
		public Vector3 Direction => IsSpot ? new Vector3(DirX, DirY, DirZ).Normalized() : Vector3.Zero;
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
			Number("ox", 0f), Number("oy", 0f), Number("oz", 0f), Text("at").Trim(), Number("flicker", 0f),
			Number("dx", 0f), Number("dy", 0f), Number("dz", 0f), Number("cone", 0f), Number("atten", 0f));
	}

	private sealed class Placed
	{
		public Request Request;
		/// <summary>The light: an <see cref="Refii.Fx.Engine_FxRecipeLight"/> (omni) or an <see cref="Engine_ModSpotLight"/>.</summary>
		public Light3D? Node;
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
	/// <paramref name="at"/> a model helper or "", <paramref name="flicker"/> 0..1. A spot when
	/// <paramref name="cone"/> is above 0: shining along <paramref name="direction"/> (the offset's
	/// axes) over that many degrees from its axis to its edge, <paramref name="attenuation"/> the
	/// edge falloff (0 or 1: linear). The id <see cref="Remove"/> takes.
	/// </summary>
	public static uint Place(uint entity, Vector3 guestPosition, Color colour, float energy, float range, Vector3 offset, string at, float flicker,
		Vector3 direction = default, float cone = 0f, float attenuation = 0f)
	{
		uint id = _nextHostId;
		_nextHostId = _nextHostId >= LastHostId ? FirstHostId : _nextHostId + 1;
		Start(new Request(
			"light", id, entity, guestPosition.X, guestPosition.Y, guestPosition.Z, colour.R, colour.G, colour.B, energy, range,
			offset.X, offset.Y, offset.Z, (at ?? string.Empty).Trim(), flicker, direction.X, direction.Y, direction.Z, cone, attenuation));
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

	/// <summary>
	/// The lights standing, for <c>RefiiHost.lights</c>: id, entity handle (0 for a point), colour,
	/// energy, range, where it is now (guest axes), the cone (0 for an omni light) and, for a spot,
	/// the direction it shines along now (guest world axes).
	/// </summary>
	public static List<(uint Id, uint Entity, Color Colour, float Energy, float Range, Vector3 GuestPosition, float Cone, Vector3 GuestDirection)> All()
	{
		var list = new List<(uint, uint, Color, float, float, Vector3, float, Vector3)>(_lights.Count);
		foreach ((uint id, Placed placed) in _lights)
		{
			Request r = placed.Request;
			bool live = placed.Node is { } node && GodotObject.IsInstanceValid(node) && node.IsInsideTree();
			Vector3 where = live ? GodotToGuest(placed.Node!.GlobalPosition) : new Vector3(r.X, r.Y, r.Z);
			Vector3 direction = !r.IsSpot ? Vector3.Zero : live ? ShineDirection(placed.Node!) : r.Direction;
			list.Add((id, r.Entity, new Color(r.R, r.G, r.B), r.Energy, r.Range, where, r.IsSpot ? r.Cone : 0f, direction));
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
		string name = $"ModLight_{r.Id.ToString(CultureInfo.InvariantCulture)}";
		Light3D node = r.IsSpot
			? MakeSpotNode(name, new Color(r.R, r.G, r.B), r.Energy, r.Range, r.Flicker, r.Cone, r.Attenuation)
			: MakeNode(name, new Color(r.R, r.G, r.B), r.Energy, r.Range, r.Flicker);
		Refii.Fx.Engine_FxPlayer.Placing placing = Refii.Fx.Engine_FxPlayer.Mount(
			node, r.Entity != 0, placed.Address, new Vector3(r.X, r.Y, r.Z), r.At, new Vector3(r.OffsetX, r.OffsetY, r.OffsetZ),
			$"ModLightHook_{r.Id.ToString(CultureInfo.InvariantCulture)}", out placed.Hook, out placed.Follows);
		if (placing != Refii.Fx.Engine_FxPlayer.Placing.Placed)
		{
			node.QueueFree();
			return placing;
		}

		if (r.IsSpot)
		{
			// Turned inside the frame Mount gave it: the node's axes are the stated frame's.
			node.Transform *= SpotLocal(r.Direction);
		}

		placed.Node = node;
		return placing;
	}

	/// <summary>
	/// The node a mod SPOT is: the omni's spot form — zero decay, no shadow, the waver — over a cone
	/// of <paramref name="cone"/> degrees from its axis to its edge (clamped to <see cref="MaxCone"/>),
	/// its edge falloff <paramref name="attenuation"/> (0 or 1: linear, the title's own 1/(1−cos)).
	/// It shines along its own −Z; <see cref="SpotLocal"/> turns that onto the stated direction.
	/// </summary>
	public static Engine_ModSpotLight MakeSpotNode(string name, Color colour, float energy, float range, float flicker, float cone, float attenuation)
	{
		float reach = float.IsFinite(range) && range > 0f ? Math.Min(range, MaxRange) : DefaultRange;
		float power = float.IsFinite(energy) && energy > 0f ? energy : 0f;
		return new Engine_ModSpotLight
		{
			Name = name,
			LightColor = new Color(Math.Max(colour.R, 0f), Math.Max(colour.G, 0f), Math.Max(colour.B, 0f)),
			LightEnergy = power,
			BaseEnergy = power,
			Flicker = float.IsFinite(flicker) ? Math.Clamp(flicker, 0f, 1f) : 0f,
			SpotRange = reach,
			SpotAttenuation = 1.0f,
			SpotAngle = ConeDegrees(cone),
			SpotAngleAttenuation = float.IsFinite(attenuation) && attenuation > 0f ? attenuation : 1f,
			LightVolumetricFogEnergy = 0.0f,
			ShadowEnabled = false,
			Visible = !Refii.Models.Engine_ClassicMaterial.Active,
		};
	}

	/// <summary>A stated cone as the spot takes it: degrees from the axis to the edge, (0, <see cref="MaxCone"/>].</summary>
	public static float ConeDegrees(float cone) => float.IsFinite(cone) && cone > 0f ? Math.Min(cone, MaxCone) : 0f;

	/// <summary>
	/// THE SPOT'S TURN inside the frame <see cref="Refii.Fx.Engine_FxPlayer.Mount"/> gives a node:
	/// post-multiplied onto the mounted transform, it points the spot's −Z along
	/// <paramref name="guestDirection"/>, a direction in the stated frame's guest axes (the entity's,
	/// the helper's, or the world's). Every mounted frame lays the frame's guest axes (x, y, z) on
	/// the node's local (x, z, y) — <c>InEntityLocal</c>'s basis for a body or helper,
	/// <c>GameFx.UnrotatedAt</c>'s for a world point — so the local direction is (x, z, y).
	/// </summary>
	public static Transform3D SpotLocal(Vector3 guestDirection)
	{
		var local = new Vector3(guestDirection.X, guestDirection.Z, guestDirection.Y);
		if (local.LengthSquared() <= 1e-8f)
		{
			return Transform3D.Identity;
		}

		local = local.Normalized();
		Vector3 up = Math.Abs(local.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
		return new Transform3D(Basis.LookingAt(local, up), Vector3.Zero);
	}

	/// <summary>The direction a placed spot shines along now, in the guest's world axes (unit length).</summary>
	public static Vector3 ShineDirection(Node3D node)
	{
		Vector3 godot = node.GlobalBasis * Vector3.Forward;
		Vector3 guest = GodotToGuest(godot);
		return guest.LengthSquared() > 1e-12f ? guest.Normalized() : Vector3.Zero;
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
			OmniAttenuation = 1.0f,
			ShadowEnabled = false,
			Visible = !Refii.Models.Engine_ClassicMaterial.Active,
		};
	}

	/// <summary>
	/// THE CLASSIC LIST: every standing light appended to <paramref name="lights"/> as the light
	/// the guest's manager would hold for it — a point light, or a spot light (type 9) shining
	/// along the node's axis this frame with the stated cone, no receive frustum (every draw its
	/// sphere meets takes it, as the second view's spots are taken) — from
	/// <paramref name="firstIndex"/> up; the count added. Called by
	/// <see cref="Refii.Models.Engine_ClassicLights.Pump"/> after the guest's own and the second view's.
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
			float range = node switch { OmniLight3D omni => omni.OmniRange, SpotLight3D spot => spot.SpotRange, _ => 0f };
			if (range <= 0f || node.LightEnergy <= 0f)
			{
				continue;
			}

			bool spotLight = node is SpotLight3D;
			lights.Add(new Refii.Models.Engine_ClassicLights.Light(
				GodotToGuest(node.GlobalPosition), range, new Vector3(r.R, r.G, r.B), node.LightEnergy,
				spotLight ? ShineDirection(node) : Vector3.Zero,
				spotLight ? Refii.Models.Engine_ClassicLights.Light.Spot : Refii.Models.Engine_ClassicLights.Light.Point,
				range, spotLight ? Mathf.DegToRad(ConeDegrees(r.Cone)) : 0f, 1f, 0, 0u, index++));
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
