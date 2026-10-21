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
/// world axes) and riding it rigidly after, freed with it.
/// <para>THE CLASSIC PATH keeps every option but one: the guest places the instance at the point
/// (or each tick at the entity's position plus the offset, ending it when the entity is gone),
/// states its scale and intensity through the instance's own setters, respawns a loop each time it
/// retires and lets a one-shot go when it does; a duration and <see cref="Stop"/> are counted here
/// and end it with the guest's kill. A TINT has no instance-level equivalent in the guest (a
/// particle's colour is its material's keyframe table) and is not applied on that path.</para>
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

		/// <summary>Spawned in the guest (the classic look): no node, ended by <c>fx_stop</c>.</summary>
		public bool Guest;
		public double Age;
		public double Waited;
	}

	/// <summary>
	/// The guest tick's command for <paramref name="r"/> as the effect <paramref name="hash"/> (the
	/// bank key): <c>fx_play ID HASH X Y Z ENTITY OX OY OZ SCALE INTENSITY LOOP</c>.
	/// </summary>
	public static string GuestPlayCommand(Request r, uint hash)
	{
		static string N(float v) => v.ToString("R", CultureInfo.InvariantCulture);
		return string.Join(' ',
			"fx_play", r.Id.ToString(CultureInfo.InvariantCulture), $"0x{hash:X8}", N(r.X), N(r.Y), N(r.Z),
			r.Entity.ToString(CultureInfo.InvariantCulture), N(r.OffsetX), N(r.OffsetY), N(r.OffsetZ),
			N(r.Scale > 0.0001f ? r.Scale : 1f), N(r.Intensity), r.Loop ? "1" : "0");
	}

	/// <summary>The guest tick's command ending id <paramref name="id"/>.</summary>
	public static string GuestStopCommand(uint id) => "fx_stop " + id.ToString(CultureInfo.InvariantCulture);

	private static readonly Dictionary<uint, Played> _played = new();
	private static readonly HashSet<string> _unknownWarned = new(StringComparer.OrdinalIgnoreCase);
	private static uint _nextHostId = FirstHostId;
	private static readonly List<uint> _done = new();

	/// <summary>Effects playing or waiting to be placed, for the debug channel.</summary>
	public static int Count => _played.Count;

	/// <summary>A request from the bus (<see cref="RequestKind"/>): plays or stops.</summary>
	public static void Handle(string fields)
	{
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

		var played = new Played { Request = request };
		_played[request.Id] = played;
		if (TryPlace(played) == Placing.Unknown)
		{
			_played.Remove(request.Id);
		}
	}

	/// <summary>Once per frame: places what waits, frees what has run its course or lost its parent.</summary>
	public static void Pump(double delta)
	{
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
			if (r.R != 1f || r.G != 1f || r.B != 1f)
			{
				GD.PushWarning($"[mods] fx: '{r.Name}' (id {r.Id}) plays untinted under the classic look: the guest's particle instance has no colour of its own");
			}

			Refii.Host.HostBridge.PostModCommand(GuestPlayCommand(r, Engine_FxEffectJson.HashOf(r.Name)));
			played.Guest = true;
			return Placing.Placed;
		}

		Node3D? parent;
		Transform3D world;
		if (r.Entity != 0)
		{
			uint address = Refii.Host.HostBridge.ModHandleAddress(r.Entity);
			parent = address == 0 ? null : Refii.Game.GameEntities.Current?.NodeFor(address);
			world = parent is null
				? default
				: new Transform3D(
					Refii.Game.GameFx.UnrotatedAt(Vector3.Zero).Basis,
					parent.GlobalPosition + Refii.Game.GameFx.GuestToGodot(new Vector3(r.OffsetX, r.OffsetY, r.OffsetZ)));
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

		var node = new FableFx
		{
			Name = $"ModFx_{r.Id.ToString(CultureInfo.InvariantCulture)}",
			EffectName = r.Name,
			Loop = r.Loop,
			EffectScale = r.Scale > 0.0001f ? r.Scale : 1f,
			Tint = new Color(r.R, r.G, r.B),
			Intensity = r.Intensity,
		};
		parent.AddChild(node);
		node.GlobalTransform = world;
		played.Node = node;
		return Placing.Placed;
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
