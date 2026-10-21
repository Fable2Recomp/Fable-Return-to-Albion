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
/// effect under that entity's drawn node, started at the node's position plus the offset (guest
/// world axes) and riding it rigidly after, freed with it. AN ENTITY WITH NO DRAWN BODY (a
/// vortex, a fireball's shot) is where its own live effects are — the first effect the guest's
/// feed states it owns, is attached to, or its own update places (<see cref="RideOf"/>), followed each
/// frame — else where the guest states the entity;
/// the effect follows that point plus the offset, and ends once the entity has neither for
/// <see cref="PendingSeconds"/>.
/// <para>A recipe binds its tint to a cast through the effect the cast plays (<see cref="Bind"/>,
/// <see cref="Engine_FxRecipes"/> <c>with</c>): a play on an entity binds the effects the guest states
/// belong to that entity — the feed's owner, what the effect is attached to, and the effects a spell's
/// own update places (<see cref="Ride"/>) — and never another cast's.</para>
/// <para>THE CLASSIC PATH keeps every option: the guest places the instance at the point (or each
/// tick at the entity's position plus the offset, ending it when the entity is gone — and, for a
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

	/// <summary>One request as the bus carries it (<c>op='play',id=…,name='…',x=…</c>), in plain values.</summary>
	public readonly record struct Request(
		string Op, uint Id, string Name, float X, float Y, float Z, uint Entity, float Duration, float Scale,
		float R, float G, float B, float Intensity, bool Loop, float OffsetX, float OffsetY, float OffsetZ)
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
			Number("ox", 0f), Number("oy", 0f), Number("oz", 0f));
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

	/// <summary>A request from the bus (<see cref="RequestKind"/>): plays, stops, or states a spell's effect.</summary>
	public static void Handle(string fields)
	{
		if (ParseRide(fields) is { } ride)
		{
			_rides[ride.Fx] = (ride.On, _clock);
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
	/// effect. <paramref name="entity"/> is a handle (0 for the world position).
	/// </summary>
	public static uint Play(
		string name, Vector3 guestPosition, uint entity, float duration, float scale, Color tint, float intensity,
		bool loop, Vector3 offset)
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
			tint.R, tint.G, tint.B, intensity, loop, offset.X, offset.Y, offset.Z));
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

	private enum Placing
	{
		Placed,
		Waiting,
		Unknown,
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
			Refii.Host.HostBridge.PostModCommand(GuestPlayCommand(r, Engine_FxEffectJson.HashOf(r.Name)));
			played.Guest = true;
			return Placing.Placed;
		}

		Node3D? parent;
		Transform3D world;
		if (r.Entity != 0)
		{
			uint address = played.Address;
			parent = address == 0 ? null : Refii.Game.GameEntities.Current?.NodeFor(address);
			Vector3? at = parent?.GlobalPosition;
			played.Follows = false;
			if (parent is null && address != 0 && FollowPoint(address) is { } point)
			{
				// No drawn body: the effect stands in the effect feed and follows the entity's point.
				parent = Refii.Game.GameFx.Current;
				at = point;
				played.Follows = true;
			}

			world = at is not { } origin
				? default
				: new Transform3D(
					Refii.Game.GameFx.UnrotatedAt(Vector3.Zero).Basis,
					origin + Refii.Game.GameFx.GuestToGodot(new Vector3(r.OffsetX, r.OffsetY, r.OffsetZ)));
		}
		else
		{
			parent = Refii.Game.GameFx.Current;
			world = Refii.Game.GameFx.UnrotatedAt(new Vector3(r.X, r.Y, r.Z));
		}

		if (parent is null || !GodotObject.IsInstanceValid(parent) || !parent.IsInsideTree())
		{
			return Placing.Waiting;
		}

		FableFx node = Make(
			$"ModFx_{r.Id.ToString(CultureInfo.InvariantCulture)}", r.Name, r.Scale, new Color(r.R, r.G, r.B), r.Intensity,
			r.Loop, true, null);
		parent.AddChild(node);
		node.GlobalTransform = world;
		played.Node = node;
		return Placing.Placed;
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

		if (played.Node is not null && GodotObject.IsInstanceValid(played.Node))
		{
			played.Node.QueueFree();
		}

		played.Node = null;
	}
}
