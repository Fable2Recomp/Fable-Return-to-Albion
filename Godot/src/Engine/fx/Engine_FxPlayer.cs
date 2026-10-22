using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Refii.Fx;

/// <summary>
/// <c>play_fx</c> ON EVERY SURFACE: a mod names an effect (the bank's or a mod's,
/// <see cref="Engine_FxModEffects"/>) and a place — a world position in the guest's axes, or an
/// entity by its handle. Under the modern look Godot draws it with the node it draws every bank
/// effect with, <see cref="FableFx"/>. Under the CLASSIC look it is the guest's own particle
/// instance, spawned on the guest tick by the game's own spawn sequence (<c>fx_play</c>,
/// src/vfs/src/hooks/mod_commands.cpp) — the mods' effects are in the guest's bank
/// (<see cref="Engine_FxModEffects.ComposeGuestBank"/>) — so the guest simulates it and the classic
/// renderer draws its groups like any other the game plays. Native and Lua requests arrive on the mod bus
/// under <see cref="RequestKind"/> (<c>refii_mod_play_fx</c>, <c>Refii.play_fx</c>), drained by
/// <see cref="Refii.Mods.RefiiHost"/>; a Godot script calls <c>RefiiHost.play_fx</c>, which lands
/// here directly. Every id, whichever surface issued it, stops through <see cref="Stop"/>.
/// </summary>
/// <remarks>
/// IDS: the payload issues 1 .. 2^23-1, this side <see cref="FirstHostId"/> (2^23) .. 2^24-1, so
/// every id is exact as a Lua number and none collides. A world effect hangs under the live
/// effect feed (<see cref="Refii.Game.GameFx"/>), placed unrotated at the stated point; an entity
/// effect under that entity's drawn node IN THE ENTITY'S OWN FRAME (<see cref="InEntityFrame"/>):
/// its axes are the entity's, the offset is along them, and as the node's child it turns with the
/// entity rigidly after, freed with it. AN ENTITY WITH NO DRAWN BODY (a
/// vortex, a fireball's shot — or one whose body is not built YET, until it is, when the effect is
/// made again in the body's frame) has no frame drawn here: it is where its own live effects are — the
/// first effect the guest's feed states it owns, is attached to, or its own update places
/// (<see cref="RideOf"/>), followed each frame — else where the guest states the entity; the
/// effect stands unrotated, follows that point plus the offset (guest world axes), and ends once
/// the entity has neither for <see cref="PendingSeconds"/>.
/// <para>A recipe binds its tint to a cast through the effect the cast plays (<see cref="Bind"/>,
/// <see cref="Engine_FxRecipes"/> <c>with</c>): a play on an entity binds the effects the guest states
/// belong to that entity — the feed's owner, what the effect is attached to, and the effects a spell's
/// own update places (<see cref="Ride"/>) — and never another cast's.</para>
/// <para>THE CLASSIC PATH keeps every option: the guest places the instance at the point (or each
/// tick in the entity's frame — its position plus the offset along its axes, and its orientation
/// through the instance's own setter, as the guest places its own FX entity's instance — ending it
/// when the entity is gone — and, for a
/// vortex or a Blades sword, again at the end of the spell's own update that moved it), states its
/// scale and intensity through the instance's own setters, respawns a loop each time it retires and
/// lets a one-shot go when it does; a duration and <see cref="Stop"/> are counted here and end it
/// with the guest's kill. The TINT goes where the guest keeps a particle's colour, its material's
/// keyframe table: the instance's emitters are pointed at a tinted copy of their material
/// (src/vfs/src/hooks/particle_dress.cpp TintFxInstance), on top of the effect's own tint.</para>
/// </remarks>
public static class Engine_FxPlayer
{
	/// <summary>REFII_MOD_HOST_FX_KIND (core/refii_mod_events.h).</summary>
	public const int RequestKind = 1001;

	public const uint FirstHostId = 0x800000u;
	private const uint LastHostId = 0xFFFFFFu;

	/// <summary>How long a request waits for its entity's node or the effect feed before it is dropped.</summary>
	private const double PendingSeconds = 2.0;

	/// <summary>A one-shot (loop off, no duration) is freed once it has run out, and at the latest after this.</summary>
	private const double OneShotLimitSeconds = 60.0;

	/// <summary>
	/// One request as the bus carries it (<c>op='play',id=…,name='…',x=…</c>), in plain values.
	/// <paramref name="At"/> names a helper point of the entity's model (<c>at='…'</c>, "" for the
	/// entity's own frame): a <c>.mdl.gmd</c> attachment or node, the effect standing in the
	/// helper's frame with the offset along the helper's axes.
	/// </summary>
	public readonly record struct Request(
		string Op, uint Id, string Name, float X, float Y, float Z, uint Entity, float Duration, float Scale,
		float R, float G, float B, float Intensity, bool Loop, float OffsetX, float OffsetY, float OffsetZ,
		string At = "")
	{
		public bool IsPlay => Op == "play";
	}

	/// <summary>
	/// The request in <paramref name="fields"/>, or null when it is not one (no op, or a play with
	/// no name or id). Missing options take their defaults: scale 1, white, intensity 1, looping.
	/// </summary>
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
		if (id == 0 || op is not ("play" or "stop"))
		{
			return null;
		}

		string name = Text("name");
		if (op == "play" && name.Length == 0)
		{
			return null;
		}

		float entity = Number("entity", 0f);
		return new Request(
			op, id, name, Number("x", 0f), Number("y", 0f), Number("z", 0f),
			entity is > 0f and <= uint.MaxValue ? (uint)entity : 0u,
			Math.Max(0f, Number("duration", 0f)), Number("scale", 1f),
			Number("r", 1f), Number("g", 1f), Number("b", 1f), Number("intensity", 1f), Number("loop", 1f) != 0f,
			Number("ox", 0f), Number("oy", 0f), Number("oz", 0f), Text("at").Trim());
	}

	private sealed class Played
	{
		public Request Request;
		public FableFx? Node;

		/// <summary>The bank key of the effect (<see cref="Engine_FxEffectJson.HashOf"/>).</summary>
		public uint Hash;

		/// <summary>The entity's guest address (the request carries its handle), 0 for a world position.</summary>
		public uint Address;

		/// <summary>An entity with no drawn body, followed each frame (see the remarks); 0 when parented.</summary>
		public bool Follows;

		/// <summary>Spawned in the guest (the classic look): no node, ended by <c>fx_stop</c>.</summary>
		public bool Guest;

		/// <summary>
		/// The bone attachment the node hangs under when the request names a helper on a rigged
		/// body (<see cref="Request.At"/>); freed with the node. Null when the node is the body's
		/// direct child.
		/// </summary>
		public BoneAttachment3D? Hook;
		public double Age;
		public double Waited;
	}

	/// <summary>
	/// The guest tick's command for <paramref name="r"/> as the effect <paramref name="hash"/> (the
	/// bank key): <c>fx_play ID HASH X Y Z ENTITY OX OY OZ SCALE INTENSITY LOOP R G B</c>.
	/// </summary>
	public static string GuestPlayCommand(Request r, uint hash)
	{
		static string N(float v) => v.ToString("R", CultureInfo.InvariantCulture);
		return string.Join(' ',
			"fx_play", r.Id.ToString(CultureInfo.InvariantCulture), $"0x{hash:X8}", N(r.X), N(r.Y), N(r.Z),
			r.Entity.ToString(CultureInfo.InvariantCulture), N(r.OffsetX), N(r.OffsetY), N(r.OffsetZ),
			N(r.Scale > 0.0001f ? r.Scale : 1f), N(r.Intensity), r.Loop ? "1" : "0",
			N(Math.Max(r.R, 0f)), N(Math.Max(r.G, 0f)), N(Math.Max(r.B, 0f)));
	}

	/// <summary>The guest tick's command ending id <paramref name="id"/>.</summary>
	public static string GuestStopCommand(uint id) => "fx_stop " + id.ToString(CultureInfo.InvariantCulture);

	private static readonly Dictionary<uint, Played> _played = new();
	private static readonly HashSet<string> _unknownWarned = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Helper names warned about once: the model has no such gmd entry, the entity's own frame was used.</summary>
	private static readonly HashSet<string> _unknownHelperWarned = new(StringComparer.OrdinalIgnoreCase);
	private static uint _nextHostId = FirstHostId;
	private static readonly List<uint> _done = new();

	/// <summary>Effects playing or waiting to be placed, for the debug channel.</summary>
	public static int Count => _played.Count;

	/// <summary>
	/// A spell's effect, as the spell's own update states it (src/vfs/src/hooks/spell_fx_owner.cpp):
	/// <c>op='ride',fx=…,on=…</c> — <paramref name="Fx"/> an FX entity or an effect instance that the
	/// update of the spell entity <paramref name="On"/> placed (the vortex's ground effects, a Blades
	/// sword's effect entity and its spell-owned instance). Guest addresses, the live-effect feed's own
	/// currency; never a mod's.
	/// </summary>
	public readonly record struct Ride(uint Fx, uint On);

	/// <summary>The ride in <paramref name="fields"/>, or null when it is not one.</summary>
	public static Ride? ParseRide(string fields)
	{
		string op = string.Empty;
		double fx = 0;
		double on = 0;
		foreach ((string key, object value) in Refii.Mods.RefiiHost.ParseFieldsPlain(fields))
		{
			switch (key)
			{
				case "op":
					op = value as string ?? string.Empty;
					break;
				case "fx":
					fx = value is long l ? l : value is double d ? d : 0;
					break;
				case "on":
					on = value is long m ? m : value is double e ? e : 0;
					break;
			}
		}

		return op == "ride" && fx is > 0 and <= uint.MaxValue && on is > 0 and <= uint.MaxValue
			? new Ride((uint)fx, (uint)on)
			: null;
	}

	/// <summary>How long a ride holds once the guest has stopped stating it (it restates each second).</summary>
	public const double RideSeconds = 3.0;

	private static readonly Dictionary<uint, (uint On, double At)> _rides = new();
	private static double _clock;

	/// <summary>The spell entity whose update places <paramref name="effect"/> (an FX entity or an instance), or 0.</summary>
	public static uint RideOf(uint effect) =>
		effect != 0 && _rides.TryGetValue(effect, out (uint On, double At) ride) && _clock - ride.At <= RideSeconds ? ride.On : 0u;

	/// <summary>
	/// A request from the bus (<see cref="RequestKind"/>): plays, stops, or states a spell's
	/// effect — or places or removes a mod's light (<c>op='light'</c> / <c>'light_off'</c>,
	/// <see cref="Refii.Lighting.Engine_ModLights"/>), which rides the same kind.
	/// </summary>
	public static void Handle(string fields)
	{
		if (ParseRide(fields) is { } ride)
		{
			_rides[ride.Fx] = (ride.On, _clock);
			return;
		}

		if (Refii.Lighting.Engine_ModLights.IsLightRequest(fields))
		{
			Refii.Lighting.Engine_ModLights.Handle(fields);
			return;
		}

		if (ParseRequest(fields) is not { } request)
		{
			GD.PushWarning($"[mods] fx: a request the host does not read: {fields}");
			return;
		}

		if (request.IsPlay)
		{
			Start(request);
		}
		else
		{
			Stop(request.Id);
		}
	}

	/// <summary>
	/// Plays <paramref name="name"/> for a Godot script; the id, or 0 when the bank holds no such
	/// effect. <paramref name="entity"/> is a handle (0 for the world position);
	/// <paramref name="at"/> a helper point of its model, or "" (<see cref="Request.At"/>).
	/// </summary>
	public static uint Play(
		string name, Vector3 guestPosition, uint entity, float duration, float scale, Color tint, float intensity,
		bool loop, Vector3 offset, string at = "")
	{
		if (FableFx.SharedBank() is { } bank && Engine_BnkFxLoader.Resolve(bank, name) is null)
		{
			GD.PushWarning($"[mods] fx: play_fx: no effect '{name}' in the bank or the mods' effects");
			return 0;
		}

		uint id = _nextHostId;
		_nextHostId = _nextHostId >= LastHostId ? FirstHostId : _nextHostId + 1;
		Start(new Request(
			"play", id, name, guestPosition.X, guestPosition.Y, guestPosition.Z, entity, Math.Max(0f, duration), scale,
			tint.R, tint.G, tint.B, intensity, loop, offset.X, offset.Y, offset.Z, (at ?? string.Empty).Trim()));
		return id;
	}

	/// <summary>Stops any surface's id; false when nothing plays under it.</summary>
	public static bool Stop(uint id)
	{
		if (!_played.Remove(id, out Played? played))
		{
			return false;
		}

		Free(played);
		return true;
	}

	/// <summary>Stops everything (the session ends).</summary>
	public static void StopAll()
	{
		foreach (Played played in _played.Values)
		{
			Free(played);
		}

		_played.Clear();
	}

	private static void Start(Request request)
	{
		if (_played.Remove(request.Id, out Played? earlier))
		{
			Free(earlier);
		}

		var played = new Played
		{
			Request = request,
			Hash = Engine_FxEffectJson.HashOf(request.Name),
			Address = request.Entity != 0 ? Refii.Host.HostBridge.ModHandleAddress(request.Entity) : 0u,
		};
		_played[request.Id] = played;
		if (TryPlace(played) == Placing.Unknown)
		{
			_played.Remove(request.Id);
		}
	}

	/// <summary>Once per frame: places what waits, frees what has run its course or lost its parent.</summary>
	public static void Pump(double delta)
	{
		_clock += delta;
		if (_rides.Count > 512)
		{
			_done.Clear();
			foreach ((uint fx, (uint _, double at)) in _rides)
			{
				if (_clock - at > RideSeconds)
				{
					_done.Add(fx);
				}
			}

			foreach (uint fx in _done)
			{
				_rides.Remove(fx);
			}
		}

		_done.Clear();
		foreach ((uint id, Played played) in _played)
		{
			if (played.Guest)
			{
				played.Age += delta;
				Request g = played.Request;
				if (g.Duration > 0f ? played.Age >= g.Duration : !g.Loop && played.Age >= OneShotLimitSeconds)
				{
					_done.Add(id);
				}

				continue;
			}

			if (played.Node is null)
			{
				played.Waited += delta;
				Placing placing = TryPlace(played);
				if (placing == Placing.Unknown)
				{
					_done.Add(id);
				}
				else if (placing == Placing.Waiting && played.Waited >= PendingSeconds)
				{
					GD.PushWarning(played.Request.Entity != 0
						? $"[mods] fx: '{played.Request.Name}' (id {id}) dropped: entity handle {played.Request.Entity} has no drawn node"
						: $"[mods] fx: '{played.Request.Name}' (id {id}) dropped: no effect feed in the tree");
					_done.Add(id);
				}

				continue;
			}

			if (!GodotObject.IsInstanceValid(played.Node) || !played.Node.IsInsideTree())
			{
				_done.Add(id);
				continue;
			}

			if (played.Follows && Refii.Game.GameEntities.Current?.NodeFor(played.Address) is { } body &&
				GodotObject.IsInstanceValid(body) && body.IsInsideTree())
			{
				// THE BODY HAS COME: the entity was followed only because its node did not exist
				// yet (GameEntities builds a node once its mesh has loaded, off-thread, which a
				// fresh spawn — a Blades sword — has not when its first play arrives). Followed,
				// the look stood upright at the entity's own effect (a sword's FX entity, which
				// the guest places at the blade's .Top, sub_825A63A8 0x825a6658), past the tip;
				// it is made again in the body's frame.
				played.Node.QueueFree();
				played.Node = null;
				played.Follows = false;
				if (TryPlace(played) == Placing.Unknown)
				{
					_done.Add(id);
				}

				continue;
			}

			if (played.Follows)
			{
				if (FollowPoint(played.Address) is { } point)
				{
					played.Waited = 0;
					played.Node.GlobalPosition = point + Refii.Game.GameFx.GuestToGodot(
						new Vector3(played.Request.OffsetX, played.Request.OffsetY, played.Request.OffsetZ));
				}
				else if ((played.Waited += delta) >= PendingSeconds)
				{
					_done.Add(id);
					continue;
				}
			}

			played.Age += delta;
			Request r = played.Request;
			bool expired = r.Duration > 0f
				? played.Age >= r.Duration
				: !r.Loop && played.Age >= 0.5 && (played.Node.DebugAlive == 0 || played.Age >= OneShotLimitSeconds);
			if (expired)
			{
				_done.Add(id);
			}
		}

		foreach (uint id in _done)
		{
			Stop(id);
		}
	}

	private static Placing TryPlace(Played played)
	{
		Request r = played.Request;
		FxBank? bank = FableFx.SharedBank();
		if (bank is not null && Engine_BnkFxLoader.Resolve(bank, r.Name) is null)
		{
			if (_unknownWarned.Add(r.Name))
			{
				GD.PushWarning($"[mods] fx: play_fx: no effect '{r.Name}' in the bank or the mods' effects");
			}

			return Placing.Unknown;
		}

		if (Refii.Models.Engine_ClassicMaterial.Active)
		{
			// A helper under the classic look: the guest's instance takes no node, so the helper's
			// REST point (the gmd's, in the guest's model axes) is folded into the offset — the
			// right point, not the live pose. The model is known once the entity's node is built;
			// a request made before that waits as a modern one does, then goes to the entity's frame.
			if (r.At.Length != 0 && r.Entity != 0)
			{
				if (ClassicHelperOffset(played) is { } folded)
				{
					r = r with { OffsetX = folded.X, OffsetY = folded.Y, OffsetZ = folded.Z, At = string.Empty };
				}
				else if (Refii.Game.GameEntities.Current?.NodeFor(played.Address) is null && played.Waited < PendingSeconds)
				{
					return Placing.Waiting;
				}
			}

			Refii.Host.HostBridge.PostModCommand(GuestPlayCommand(r, Engine_FxEffectJson.HashOf(r.Name)));
			played.Guest = true;
			return Placing.Placed;
		}

		FableFx node = Make(
			$"ModFx_{r.Id.ToString(CultureInfo.InvariantCulture)}", r.Name, r.Scale, new Color(r.R, r.G, r.B), r.Intensity,
			r.Loop, true, null);
		Placing placing = Mount(node, r.Entity != 0, played.Address, new Vector3(r.X, r.Y, r.Z), r.At,
			new Vector3(r.OffsetX, r.OffsetY, r.OffsetZ), $"ModFxHook_{r.Id.ToString(CultureInfo.InvariantCulture)}",
			out played.Hook, out played.Follows);
		if (placing != Placing.Placed)
		{
			node.QueueFree();
			return placing;
		}

		played.Node = node;
		return Placing.Placed;
	}

	/// <summary>How a mount went: parented, waiting for the entity's node or the effect feed, or nothing to mount on.</summary>
	public enum Placing
	{
		Placed,
		Waiting,
		Unknown,
	}

	/// <summary>
	/// THE ONE PLACEMENT every mod-placed node takes — an effect (<c>play_fx</c>), a light
	/// (<see cref="Refii.Lighting.Engine_ModLights"/>): <paramref name="node"/> is parented and
	/// given its transform, and the caller owns it from then on (freed with its parent, or through
	/// <paramref name="hook"/> when it hangs on a bone).
	/// <list type="bullet">
	/// <item>A world placement (<paramref name="onEntity"/> false): under the effect feed, unrotated
	/// at <paramref name="point"/> (guest axes).</item>
	/// <item>An entity with a drawn body: its child in the entity's own frame
	/// (<see cref="InEntityLocal"/>), <paramref name="offset"/> along the entity's axes — or, with
	/// <paramref name="at"/> naming a model helper, at that gmd point's own frame
	/// (<see cref="AtHelperLocal"/>), on a rigged body under a <see cref="BoneAttachment3D"/> on
	/// the helper's bone. An unknown helper is warned once and the entity's frame used.</item>
	/// <item>An entity with no drawn body (a vortex, a shot; a body not built yet): under the effect
	/// feed at the entity's own point (<see cref="FollowPoint"/>) plus the offset in world axes,
	/// <paramref name="follows"/> set so the caller keeps it on the point each frame.</item>
	/// </list>
	/// <see cref="Placing.Waiting"/> leaves the node unparented: the entity's node or the feed is
	/// not there yet.
	/// </summary>
	public static Placing Mount(
		Node3D node, bool onEntity, uint address, Vector3 point, string at, Vector3 offset, string hookName,
		out BoneAttachment3D? hook, out bool follows)
	{
		hook = null;
		follows = false;
		Node3D? parent;
		Transform3D world;
		if (onEntity)
		{
			parent = address == 0 ? null : Refii.Game.GameEntities.Current?.NodeFor(address);
			Vector3? origin = parent?.GlobalPosition;
			if (parent is null && address != 0 && FollowPoint(address) is { } followed)
			{
				// No drawn body: the node stands in the effect feed and follows the entity's point.
				parent = Refii.Game.GameFx.Current;
				origin = followed;
				follows = true;
			}

			if (!follows && parent is not null && GodotObject.IsInstanceValid(parent) && parent.IsInsideTree())
			{
				// ON THE BODY: a constant local transform in the entity's own frame, so the node
				// takes the body's rotation AND scale as the guest restates them every frame.
				// AT A HELPER: the gmd point's own frame — on a rigged body under the bone it
				// hangs from (a BoneAttachment3D follows the pose), else the model-space rest.
				if (at.Length != 0 && Refii.Game.GameEntities.Current is { } entities &&
					entities.TryHelper(address, at, out Skeleton3D? skeleton, out int bone, out Transform3D mount, out _))
				{
					if (skeleton is not null && bone >= 0)
					{
						hook = new BoneAttachment3D { Name = hookName, BoneIdx = bone };
						skeleton.AddChild(hook);
						hook.AddChild(node);
					}
					else
					{
						parent.AddChild(node);
					}

					node.Transform = AtHelperLocal(mount, offset);
					return Placing.Placed;
				}

				if (at.Length != 0 && _unknownHelperWarned.Add(at))
				{
					GD.PushWarning($"[mods] fx: play_fx_on: no helper '{at}' on the model of entity {address:X8}; the entity's own frame is used");
				}

				parent.AddChild(node);
				node.Transform = InEntityLocal(offset);
				return Placing.Placed;
			}

			world = origin is not { } from
				? default
				: new Transform3D(
					Refii.Game.GameFx.UnrotatedAt(Vector3.Zero).Basis,
					from + Refii.Game.GameFx.GuestToGodot(offset));
		}
		else
		{
			parent = Refii.Game.GameFx.Current;
			world = Refii.Game.GameFx.UnrotatedAt(point);
		}

		if (parent is null || !GodotObject.IsInstanceValid(parent) || !parent.IsInsideTree())
		{
			follows = false;
			return Placing.Waiting;
		}

		parent.AddChild(node);
		node.GlobalTransform = world;
		return Placing.Placed;
	}

	/// <summary>
	/// Where an entity with no drawn body is this frame, in Godot's space — the point a
	/// <see cref="Mount"/> that set <c>follows</c> keeps its node on — or null when the entity has
	/// neither a live effect nor a stated position.
	/// </summary>
	public static Vector3? FollowedPoint(uint address) => FollowPoint(address);

	/// <summary>
	/// An effect riding an entity's drawn node, in THAT ENTITY'S OWN FRAME: the effect's axes are the
	/// entity's guest axes and <paramref name="guestOffset"/> is measured along them, so the effect
	/// turns with the entity. <paramref name="drawn"/> is the node's global basis, which
	/// <c>GameEntities.ToGodot</c> builds as <c>N * R * M^-1</c> — columns N·localX, N·localZ,
	/// −N·localY (R's rows are the guest's local axes; M is the mesh's Z-up to Y-up bake). An effect
	/// node takes guest rows as <c>(N·row0, N·row2, N·row1)</c> (<c>GameFx</c>'s placement from rows,
	/// <c>UnrotatedAt</c> for the identity), so the effect's basis is the node's with its third column
	/// negated. The same placement the guest makes for its own FX entity, sub_825F8900: the placement
	/// component's position (slot 0x40) and orientation (slot 0x44) put on the instance with
	/// sub_832325A8 and sub_83231C40.
	/// </summary>
	/// <remarks>
	/// THE BODY'S SCALE IS THE EFFECT'S TOO. The guest restates an entity's appearance scale every
	/// frame (a Blades sword grows in as it appears), and an effect authored along the model — a
	/// blade's burn from guard to tip — lines it only at the model's own size. Set as a world
	/// transform under a node still growing, the counter-scale was baked into the child and the burn
	/// came out magnified and pushed past the tip; as a constant local transform it scales with the
	/// body. play_fx's own scale multiplies on top.
	/// </remarks>
	public static Transform3D InEntityFrame(Basis drawn, Vector3 origin, Vector3 guestOffset) =>
		new Transform3D(drawn, origin) * InEntityLocal(guestOffset);

	/// <summary>
	/// The effect's transform under the entity's drawn node: the third axis negated (see
	/// <see cref="InEntityFrame"/>), the guest offset (x, y, z) at the node's local (x, z, −y).
	/// </summary>
	public static Transform3D InEntityLocal(Vector3 guestOffset) =>
		new(new Basis(Vector3.Right, Vector3.Up, Vector3.Forward),
			new Vector3(guestOffset.X, guestOffset.Z, -guestOffset.Y));

	/// <summary>
	/// The effect's transform at a model HELPER: <paramref name="mount"/> is the helper's frame in
	/// the space of the node the effect is parented to (the model's own Godot Y-up space under the
	/// entity node, or relative to a bone under its BoneAttachment3D —
	/// <see cref="Refii.Entities.Engine_EntityAttachments.TryResolveHelper"/>), and the effect
	/// stands in it exactly as it stands in the entity's frame (<see cref="InEntityLocal"/>): its
	/// axes the helper's guest-local axes, <paramref name="guestOffset"/> along them. A helper with
	/// an orientation (a sword's .Top, laid along the blade) turns the effect with it.
	/// </summary>
	public static Transform3D AtHelperLocal(Transform3D mount, Vector3 guestOffset) =>
		mount * InEntityLocal(guestOffset);

	/// <summary>
	/// The guest offset a play at a helper takes under the classic look: the helper's REST point
	/// in the guest's model axes, with <paramref name="guestOffset"/> laid along the helper's rest
	/// axes — what <c>fx_play</c>'s <c>ox oy oz</c> carry so the guest's instance stands at the right
	/// point in the entity's frame. <paramref name="rest"/> is the helper's rest transform in the
	/// node's space (Godot Y-up), as <c>GameEntities.TryHelper</c> gives it.
	/// </summary>
	public static Vector3 ClassicHelperOffset(Transform3D rest, Vector3 guestOffset)
	{
		Transform3D guest = Refii.Models.Engine_GmdSkeleton.ToGameSpace(rest);
		return guest.Origin + (guest.Basis * guestOffset);
	}

	/// <summary><see cref="ClassicHelperOffset(Transform3D, Vector3)"/> for a play, or null while its entity has no model or helper.</summary>
	private static Vector3? ClassicHelperOffset(Played played)
	{
		Request r = played.Request;
		Refii.Game.GameEntities? entities = Refii.Game.GameEntities.Current;
		if (entities is null)
		{
			return null;
		}

		if (!entities.TryHelper(played.Address, r.At, out _, out _, out _, out Transform3D rest))
		{
			if (entities.NodeFor(played.Address) is not null && _unknownHelperWarned.Add(r.At))
			{
				GD.PushWarning($"[mods] fx: play_fx_on: no helper '{r.At}' on the model of entity {played.Address:X8}; the entity's own frame is used");
			}

			return null;
		}

		return ClassicHelperOffset(rest, new Vector3(r.OffsetX, r.OffsetY, r.OffsetZ));
	}

	/// <summary>The live effects the feed states per entity this frame (owner and attached), and the last complete frame's.</summary>
	private static Dictionary<uint, FableFx> _ownedBuilding = new();
	private static Dictionary<uint, FableFx> _owned = new();

	/// <summary>Once per frame before the effect feed's walk (<see cref="Engine_FxRecipes.Begin"/>): the walk being noted becomes the next.</summary>
	public static void BeginLiveEffects()
	{
		(_owned, _ownedBuilding) = (_ownedBuilding, _owned);
		_ownedBuilding.Clear();
	}

	/// <summary>
	/// One live effect of the feed with the entities it states (<paramref name="owner"/>, what its
	/// entity is <paramref name="attached"/> to; 0 for none), for the entities with no drawn body. The
	/// first noted per entity stands for it; a new walk keeps the one followed last when it lives.
	/// </summary>
	public static void NoteLiveEffect(FableFx node, uint owner, uint attached, uint handle = 0)
	{
		Note(owner, node);
		if (attached != owner)
		{
			Note(attached, node);
		}

		// The spell whose own update places it (the vortex's ground effects, a sword's).
		Note(RideOf(owner), node);
		Note(RideOf(handle), node);

		static void Note(uint entity, FableFx node)
		{
			if (entity == 0)
			{
				return;
			}

			if (_owned.TryGetValue(entity, out FableFx? was) && ReferenceEquals(was, node))
			{
				_ownedBuilding[entity] = node;
			}
			else
			{
				_ownedBuilding.TryAdd(entity, node);
			}
		}
	}

	/// <summary>Where an entity with no drawn body is, in Godot's space: its own live effect, else its stated position; null when neither.</summary>
	private static Vector3? FollowPoint(uint address)
	{
		if ((_ownedBuilding.TryGetValue(address, out FableFx? effect) || _owned.TryGetValue(address, out effect)) &&
			GodotObject.IsInstanceValid(effect) && effect.IsInsideTree())
		{
			return effect.GlobalPosition;
		}

		return Refii.Game.GameEntities.TryGuestPosition(address, out Vector3 guest) ? Refii.Game.GameFx.GuestToGodot(guest) : null;
	}

	/// <summary>Whether any surface plays the effect keyed <paramref name="hash"/> right now (placed or waiting).</summary>
	public static bool IsPlaying(uint hash)
	{
		if (hash == 0)
		{
			return false;
		}

		foreach (Played played in _played.Values)
		{
			if (played.Hash == hash)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// One play as the <c>with</c> rule sees it: its id, its effect's key, the entity it rides (0 for a
	/// point) and its squared distance to the live effect being bound (NaN when it has no place yet).
	/// </summary>
	public readonly record struct PlayAt(uint Id, uint Hash, uint Address, float DistanceSquared);

	/// <summary>
	/// THE WITH RULE. Which play of the effect keyed <paramref name="hash"/> binds a live effect, or 0.
	/// <paramref name="entities"/> are what the guest states the effect belongs to — its owner (or the
	/// spell whose update places that owner), the entity it is attached to, the spell whose update
	/// places its instance; zeros are ignored. <paramref name="claimed"/>: a spell's own update placed it.
	/// <list type="number">
	/// <item>A play of the effect riding one of those entities binds it — exactly that cast.</item>
	/// <item>An effect the guest states belongs to another cast — a spell's update claimed it, or a play
	/// of any effect rides one of its entities — binds no other play.</item>
	/// <item>An effect the guest states no cast for binds the nearest play of the effect within
	/// <paramref name="radius"/> metres (a look played at a point: a blast where the shot was).</item>
	/// </list>
	/// </summary>
	public static uint Bind(uint hash, ReadOnlySpan<uint> entities, bool claimed, IReadOnlyList<PlayAt> plays, float radius)
	{
		static bool Holds(ReadOnlySpan<uint> entities, uint address)
		{
			if (address == 0)
			{
				return false;
			}

			foreach (uint entity in entities)
			{
				if (entity == address)
				{
					return true;
				}
			}

			return false;
		}

		foreach (PlayAt play in plays)
		{
			if (play.Hash == hash && Holds(entities, play.Address))
			{
				return play.Id;
			}
		}

		if (claimed)
		{
			return 0;
		}

		foreach (PlayAt play in plays)
		{
			if (Holds(entities, play.Address))
			{
				return 0;
			}
		}

		uint id = 0;
		float best = radius * radius;
		foreach (PlayAt play in plays)
		{
			if (play.Hash == hash && play.DistanceSquared <= best)
			{
				best = play.DistanceSquared;
				id = play.Id;
			}
		}

		return id;
	}

	private static readonly List<PlayAt> _plays = new();

	/// <summary>
	/// Whether a play of the effect keyed <paramref name="hash"/> binds a live effect of the feed
	/// (<see cref="Bind"/>): the feed's <paramref name="owner"/>, <paramref name="attached"/> and
	/// effect <paramref name="handle"/>, each through the spell whose update places it
	/// (<see cref="RideOf"/>), and the effect's place <paramref name="at"/> (Godot's space).
	/// <paramref name="id"/> is the binding play.
	/// </summary>
	public static bool Binds(uint hash, uint owner, uint attached, uint handle, Vector3 at, float radius, out uint id)
	{
		uint ownerRide = RideOf(owner);
		uint handleRide = RideOf(handle);
		Span<uint> entities = stackalloc uint[] { ownerRide != 0 ? ownerRide : owner, attached, handleRide };

		_plays.Clear();
		foreach ((uint key, Played played) in _played)
		{
			float distance = float.NaN;
			if (played.Hash == hash)
			{
				Vector3? where = played.Node is { } node && GodotObject.IsInstanceValid(node) && node.IsInsideTree()
					? node.GlobalPosition
					: played.Address != 0
						? Refii.Game.GameEntities.TryGuestPosition(played.Address, out Vector3 guest) ? Refii.Game.GameFx.GuestToGodot(guest) : null
						: Refii.Game.GameFx.GuestToGodot(new Vector3(played.Request.X, played.Request.Y, played.Request.Z));
				distance = where is { } point ? point.DistanceSquaredTo(at) : float.NaN;
			}

			_plays.Add(new PlayAt(key, played.Hash, played.Address, distance));
		}

		id = Bind(hash, entities, ownerRide != 0 || handleRide != 0, _plays, radius);
		return id != 0;
	}

	/// <summary>
	/// ATTACHING: <paramref name="name"/> as a child of <paramref name="parent"/> at
	/// <paramref name="local"/>, made exactly as play_fx makes its Godot-drawn node (the options mapped
	/// the same way) and owned by the caller, which frees it with <paramref name="parent"/> or itself —
	/// a recipe's attachments and pieces (<see cref="Engine_FxRecipes"/>). <paramref name="bank"/>
	/// draws from a bank other than the shared one (a preview's). Null when the bank holds no such
	/// effect.
	/// </summary>
	public static FableFx? Attach(
		Node3D parent, string name, Vector3 local, float scale = 1f, Color? tint = null, float intensity = 1f,
		bool loop = true, bool playing = true, string nodeName = "", FxBank? bank = null)
	{
		if ((bank ?? FableFx.SharedBank()) is { } known && Engine_BnkFxLoader.Resolve(known, name) is null)
		{
			if (_unknownWarned.Add(name))
			{
				GD.PushWarning($"[fx] attach: no effect '{name}' in the bank or the mods' effects");
			}

			return null;
		}

		FableFx node = Make(nodeName.Length != 0 ? nodeName : "Fx_" + name, name, scale, tint ?? Colors.White, intensity, loop, playing, bank);
		node.Position = local;
		parent.AddChild(node);
		return node;
	}

	/// <summary>The node every surface draws a Godot-side effect with.</summary>
	private static FableFx Make(string nodeName, string name, float scale, Color tint, float intensity, bool loop, bool playing, FxBank? bank)
	{
		var node = new FableFx
		{
			Name = nodeName,
			EffectName = name,
			Loop = loop,
			EffectScale = scale > 0.0001f ? scale : 1f,
			Tint = tint,
			Intensity = intensity,
			Playing = playing,
		};
		if (bank is not null)
		{
			node.BankOverride = bank;
		}

		return node;
	}

	private static void Free(Played played)
	{
		if (played.Guest)
		{
			Refii.Host.HostBridge.PostModCommand(GuestStopCommand(played.Request.Id));
			played.Guest = false;
		}

		if (played.Hook is not null && GodotObject.IsInstanceValid(played.Hook))
		{
			played.Hook.QueueFree();
		}
		else if (played.Node is not null && GodotObject.IsInstanceValid(played.Node))
		{
			played.Node.QueueFree();
		}

		played.Hook = null;
		played.Node = null;
	}
}
