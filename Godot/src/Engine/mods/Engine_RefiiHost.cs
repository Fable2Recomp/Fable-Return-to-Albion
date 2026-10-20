using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Godot;
using Refii.Host;

namespace Refii.Mods;

/// <summary>
/// THE SINGLETON A MOD'S GDSCRIPT TALKS TO: <c>Engine.get_singleton("RefiiHost")</c>, registered
/// for the life of a game session (<see cref="ModGodot"/>). Every mod event the payload's bus
/// posts is drained once per frame and emitted as the signal named by the event's Lua name
/// (<c>hit</c>, <c>spell_launched</c>, <c>level_loaded</c> ... the catalog's
/// <see cref="ModApiEvent.LuaName"/>) with one Dictionary argument of the event's fields, and
/// as <c>mod_event(name, e)</c> for every kind. The host asks the bus only for the kinds some
/// script is connected to: the mask is recomputed from the live connections each frame, and
/// is zero when no mod script is loaded, so an idle host posts nothing.
/// </summary>
/// <remarks>
/// Member names are snake_case on purpose: Godot exposes C# members to GDScript under their
/// C# names, so this is what a script sees. The signal delegates are declared statically (the
/// generator needs them) and named after the catalog; <see cref="SignalNames"/> is the list a
/// test holds to <see cref="ModApi.Events"/>, in kind order. A Dictionary value is an int
/// (entity ids, players, kinds), a float, a bool or a String; an entity is a number, compared
/// with <c>hero(player)</c>.
/// </remarks>
[GlobalClass]
public partial class RefiiHost : Node
{
	/// <summary>Every event, with its name: the one connection for a script that filters itself.</summary>
	[Signal] public delegate void mod_eventEventHandler(string name, Godot.Collections.Dictionary e);

	/// <summary>A <see cref="query"/>'s answer: the id the query returned, and <c>ok</c> plus the results.</summary>
	[Signal] public delegate void replyEventHandler(long id, Godot.Collections.Dictionary result);

	// One signal per catalog event, in kind order; the delegate name minus "EventHandler" is the
	// signal name GDScript connects to. Held to ModApi.Events by RefiiHostTests.
	[Signal] public delegate void tickEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void hero_readyEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void hero_goneEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void lua_readyEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void save_loadedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void game_actionEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void ability_requestEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void melee_swingEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void roll_can_startEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void roll_startedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void blockEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void anim_eventEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void hitEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void hit_landedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void hit_responseEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void spell_chargeEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void spell_charge_levelEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void spell_launchEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void spell_launchedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void spell_createdEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void experienceEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void entity_diedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void hero_downedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void damageEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void level_loadedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void level_unloadingEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void game_savingEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void item_addedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void item_usedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void held_itemEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void weapon_equippedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void ability_levelEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void goldEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void relationEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void input_actionEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void quest_startedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void quest_endedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void quest_objectiveEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void dialogueEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void entity_spawnedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void entity_removedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void expression_startedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void renownEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void moralityEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void purityEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void marriageEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void dog_trickEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void ranged_shotEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void status_effectEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void target_changedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void opinion_deedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void opinion_modifiedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void time_of_dayEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void weather_themeEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void sleep_menuEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void travelEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void fast_travelEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void menu_openedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void menu_closedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void menu_screenEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void weather_regionEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void sleep_chosenEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void slow_timeEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void summonedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void crimeEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void achievementEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void collectable_takenEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void partner_consentEventHandler(Godot.Collections.Dictionary e);

	/// <summary>The per-event signal names, in kind order: the static twin of the catalog's Lua names.</summary>
	public static readonly string[] SignalNames =
	{
		"tick", "hero_ready", "hero_gone", "lua_ready", "save_loaded", "game_action",
		"ability_request", "melee_swing", "roll_can_start", "roll_started", "block", "anim_event",
		"hit", "hit_landed", "hit_response", "spell_charge", "spell_charge_level", "spell_launch",
		"spell_launched", "spell_created", "experience", "entity_died", "hero_downed", "damage",
		"level_loaded", "level_unloading", "game_saving", "item_added", "item_used", "held_item",
		"weapon_equipped", "ability_level", "gold", "relation", "input_action", "quest_started",
		"quest_ended", "quest_objective", "dialogue",
		"entity_spawned", "entity_removed", "expression_started", "renown", "morality", "purity",
		"marriage", "dog_trick", "ranged_shot", "status_effect", "target_changed", "opinion_deed",
		"opinion_modified", "time_of_day", "weather_theme", "sleep_menu", "travel", "fast_travel",
		"menu_opened", "menu_closed", "menu_screen", "weather_region", "sleep_chosen", "slow_time", "summoned",
		"crime", "achievement", "collectable_taken", "partner_consent",
	};

	public const string SingletonName = "RefiiHost";
	public const string ModEventSignal = "mod_event";

	/// <summary>The kinds the host keeps in the mask while any script is loaded: hero_ready and hero_gone feed <see cref="hero"/>.</summary>
	private static readonly UInt128 HeroKinds = ((UInt128)1 << 1) | ((UInt128)1 << 2);

	/// <summary>The kind a query's reply arrives under (REFII_MOD_HOST_REPLY_KIND); never an event.</summary>
	public const int ReplyKind = 1000;

	/// <summary>A command line's limit: the tick drains into <c>char line[4096]</c> (mod_commands.cpp).</summary>
	public const int CommandLimit = 4096;

	private long _nextQueryId;

	/// <summary>Signal per kind, from the catalog; null where the catalog names a signal this class does not declare.</summary>
	private readonly StringName?[] _signalByKind;
	private readonly StringName[] _nameByKind;
	private readonly Dictionary<long, long> _hero = new();
	private UInt128 _mask;
	private bool _maskSent;

	/// <summary>The node the loaded scripts live under; no children means no script is loaded.</summary>
	public Node? ScriptsRoot { get; set; }

	/// <summary>The enabled mods, in load order, as <see cref="mods"/> reports them.</summary>
	public string[] EnabledMods { get; set; } = Array.Empty<string>();

	public RefiiHost()
	{
		_signalByKind = new StringName?[ModApi.Events.Count];
		_nameByKind = new StringName[ModApi.Events.Count];
		foreach (ModApiEvent e in ModApi.Events)
		{
			_nameByKind[e.Kind] = new StringName(e.LuaName);
			if (HasSignal(e.LuaName))
			{
				_signalByKind[e.Kind] = _nameByKind[e.Kind];
			}
			else
			{
				GD.PushError($"[mods] godot: RefiiHost declares no signal '{e.LuaName}' (kind {e.Kind}); " +
					"it reaches scripts through mod_event only");
			}
		}
	}

	/// <summary>Ids of the two hero queries a new host sends, answered into <see cref="_hero"/>.</summary>
	private readonly Dictionary<long, long> _heroSeedQueries = new();

	/// <summary>
	/// A host made after the heroes were announced (a "Reload mods" builds a new one) asks for them:
	/// hero_ready fires only when a hero changes, so it would otherwise answer 0 until then.
	/// </summary>
	public override void _Ready()
	{
		for (long player = 1; player <= 2; player++)
		{
			long id = query("hero", new Godot.Collections.Array { player });
			if (id != 0)
			{
				_heroSeedQueries[id] = player;
			}
		}
	}

	// ---------------------------------------------------------------------------------------
	// Verdicts: handlers that run INSIDE the game's seam and can cancel or change the event
	// ---------------------------------------------------------------------------------------

	private static readonly object VerdictLock = new();
	private static readonly Dictionary<int, List<Callable>> Verdicts = new();

	/// <summary>The one callback the payload holds; a static field keeps the delegate alive.</summary>
	private static readonly HostBridge.ModVerdictFn VerdictCallback = OnVerdict;

	private static bool _verdictInstalled;

	/// <summary>
	/// Registers a VERDICT handler for an event (its Lua name, e.g. "hit"): <c>handler(e)</c> runs
	/// on the GAME'S thread inside the seam, before the game acts, with the same dictionary the
	/// signal carries. Change a writable field in <c>e</c> (<c>e.damage *= 2</c>, <c>e.knockdown =
	/// true</c>) or set <c>e.cancel = true</c> / return true to refuse, where the event allows it.
	/// The handler must only compute: no nodes, no scene tree (use call_deferred for that), and it
	/// should be quick — the game waits for it. Returns false for an unknown event.
	/// </summary>
	public bool on_verdict(string eventName, Callable handler)
	{
		ModApiEvent? e = ModApi.Find(eventName);
		if (e is null)
		{
			GD.PushWarning($"[mods] godot: on_verdict: no event '{eventName}'");
			return false;
		}

		lock (VerdictLock)
		{
			if (!Verdicts.TryGetValue(e.Kind, out List<Callable>? list))
			{
				list = new List<Callable>();
				Verdicts[e.Kind] = list;
			}

			list.Add(handler);
		}

		PushVerdictMask();
		return true;
	}

	/// <summary>Removes a verdict handler registered with <see cref="on_verdict"/>.</summary>
	public void remove_verdict(string eventName, Callable handler)
	{
		ModApiEvent? e = ModApi.Find(eventName);
		if (e is null)
		{
			return;
		}

		lock (VerdictLock)
		{
			if (Verdicts.TryGetValue(e.Kind, out List<Callable>? list))
			{
				list.RemoveAll(c => c.Equals(handler));
			}
		}

		PushVerdictMask();
	}

	private static void PushVerdictMask()
	{
		UInt128 mask = 0;
		lock (VerdictLock)
		{
			foreach ((int kind, List<Callable> list) in Verdicts)
			{
				if (list.Count != 0 && kind >= 0 && kind < 128)
				{
					mask |= (UInt128)1 << kind;
				}
			}
		}

		if (mask != 0 && !_verdictInstalled)
		{
			HostBridge.SetModVerdictHandler(VerdictCallback);
			_verdictInstalled = true;
		}

		HostBridge.SetModVerdictMask(mask);
		if (mask == 0 && _verdictInstalled)
		{
			HostBridge.SetModVerdictHandler(null);
			_verdictInstalled = false;
		}
	}

	private static void ClearVerdicts()
	{
		lock (VerdictLock)
		{
			Verdicts.Clear();
		}

		PushVerdictMask();
	}

	/// <summary>
	/// The payload's call, on the guest thread: every handler for the kind runs on one dictionary;
	/// what they changed in a writable field, and a cancel, go back as "name=value" pairs.
	/// </summary>
	private static int OnVerdict(int kind, IntPtr fields, UIntPtr length, IntPtr output, UIntPtr capacity)
	{
		try
		{
			Callable[] handlers;
			lock (VerdictLock)
			{
				if (!Verdicts.TryGetValue(kind, out List<Callable>? list) || list.Count == 0)
				{
					return 0;
				}

				handlers = list.ToArray();
			}

			string text = Marshal.PtrToStringUTF8(fields, (int)length) ?? string.Empty;
			var before = new Dictionary<string, object>();
			foreach ((string key, object value) in ParseFieldsPlain(text))
			{
				before[key] = value;
			}
			Godot.Collections.Dictionary e = ParseFields(text);
			bool cancel = false;
			foreach (Callable handler in handlers)
			{
				if (handler.Target is { } target && !GodotObject.IsInstanceValid(target))
				{
					continue;
				}

				Variant result = handler.Call(e);
				if (result.VariantType == Variant.Type.Bool && result.AsBool())
				{
					cancel = true;
				}
			}

			if (e.TryGetValue("cancel", out Variant cancelled) && cancelled.VariantType == Variant.Type.Bool && cancelled.AsBool())
			{
				cancel = true;
			}

			var answer = new StringBuilder();
			if (cancel)
			{
				answer.Append("cancel=1");
			}

			if (kind >= 0 && kind < ModApi.Events.Count)
			{
				foreach (ModApiField f in ModApi.Events[kind].Fields.Where(f => f.Writable && f.Name != "cancel"))
				{
					if (!e.TryGetValue(f.Name, out Variant now))
					{
						continue;
					}

					double? value = now.VariantType switch
					{
						Variant.Type.Bool => now.AsBool() ? 1 : 0,
						Variant.Type.Int => now.AsInt64(),
						Variant.Type.Float => now.AsDouble(),
						_ => null,
					};
					if (value is null)
					{
						continue;
					}

					double old = before.TryGetValue(f.Name, out object? o) ? o switch
					{
						bool b => b ? 1 : 0,
						long l => l,
						double d => d,
						_ => double.NaN,
					} : double.NaN;
					if (value.Value != old)
					{
						if (answer.Length != 0)
						{
							answer.Append(',');
						}

						answer.Append(f.Name).Append('=').Append(value.Value.ToString("R", CultureInfo.InvariantCulture));
					}
				}
			}

			if (answer.Length == 0)
			{
				return 0;
			}

			byte[] bytes = Encoding.UTF8.GetBytes(answer.ToString());
			int n = Math.Min(bytes.Length, (int)capacity);
			Marshal.Copy(bytes, 0, output, n);
			return n;
		}
		catch (Exception ex)
		{
			GD.PushError($"[mods] godot: verdict handler failed: {ex.Message}");
			return 0;
		}
	}

	public override void _ExitTree()
	{
		ClearVerdicts();
		if (_maskSent)
		{
			HostBridge.SetModEventMask(0);
			_maskSent = false;
		}
	}

	/// <summary>Once per frame: the mask from the live connections, then every event posted since the last frame.</summary>
	public override void _Process(double delta)
	{
		UInt128 mask = ComputeMask();
		if (mask != _mask || !_maskSent)
		{
			_mask = mask;
			_maskSent = true;
			HostBridge.SetModEventMask(mask);
		}

		List<(int Kind, string Fields)> events = HostBridge.DrainModEvents();
		foreach ((int kind, string fields) in events)
		{
			// A query's answer: the id goes back as the signal's first argument, never to mod_event.
			if (kind == ReplyKind)
			{
				Godot.Collections.Dictionary result = ParseFields(fields);
				long id = result.TryGetValue("id", out Variant idValue) ? idValue.AsInt64() : 0;
				result.Remove("id");
				if (_heroSeedQueries.Remove(id, out long seededPlayer))
				{
					long seeded = result.TryGetValue("hero", out Variant heroValue) ? heroValue.AsInt64() : 0;
					if (seeded != 0 && !_hero.ContainsKey(seededPlayer))
					{
						_hero[seededPlayer] = seeded;
					}

					continue;
				}

				EmitSignal(SignalName.reply, id, result);
				continue;
			}

			if (kind < 0 || kind >= _nameByKind.Length)
			{
				continue;
			}

			Godot.Collections.Dictionary e = ParseFields(fields);
			TrackHero(kind, e);
			if (_signalByKind[kind] is { } signal)
			{
				EmitSignal(signal, e);
			}

			EmitSignal(SignalName.mod_event, _nameByKind[kind].ToString(), e);
		}
	}

	private void TrackHero(int kind, Godot.Collections.Dictionary e)
	{
		if (kind != 1 && kind != 2)
		{
			return;
		}

		long player = e.TryGetValue("player", out Variant p) ? p.AsInt64() : 0;
		long hero = e.TryGetValue("hero", out Variant h) ? h.AsInt64() : 0;
		if (kind == 1)
		{
			_hero[player] = hero;
		}
		else if (_hero.TryGetValue(player, out long current) && current == hero)
		{
			_hero.Remove(player);
		}
	}

	/// <summary>
	/// Bit k set when some script is connected to kind k's signal; every bit when one is
	/// connected to mod_event; the hero kinds whenever a script is loaded at all; zero when none is.
	/// </summary>
	private UInt128 ComputeMask()
	{
		if (ScriptsRoot is null || !IsInstanceValid(ScriptsRoot) || ScriptsRoot.GetChildCount() == 0)
		{
			return 0;
		}

		UInt128 mask = HeroKinds;
		if (GetSignalConnectionList(SignalName.mod_event).Count != 0)
		{
			for (int kind = 0; kind < _nameByKind.Length; kind++)
			{
				mask |= (UInt128)1 << kind;
			}

			return mask;
		}

		for (int kind = 0; kind < _signalByKind.Length; kind++)
		{
			if (_signalByKind[kind] is { } signal && GetSignalConnectionList(signal).Count != 0)
			{
				mask |= (UInt128)1 << kind;
			}
		}

		return mask;
	}

	// ---- the surface a script calls -------------------------------------------------------

	/// <summary><c>REFII_MOD_API_VERSION</c>.</summary>
	public int api_version() => ModApi.Version;

	/// <summary>A hard mod's published value by key (<c>combat.stamina</c>); NaN when its hook has published none.</summary>
	public float get_value(string key) =>
		HostBridge.TryGetModValue(key, out float value) ? value : float.NaN;

	/// <summary>A settings.json value by key, the live override first; <paramref name="default"/> when no mod states it.</summary>
	public float setting(string key, float @default) =>
		ModSettings.TryGet(key, out float value) ? value : @default;

	/// <summary>Sets a mod setting in the running game, kept over the file's value for the session.</summary>
	public void set_setting(string key, float value) => ModSettings.Set(key, value);

	/// <summary>
	/// Runs a Lua chunk in the GAME VM on the guest's next tick (the shipped scripts' globals and
	/// <c>Refii.*</c> are in scope). The chunk is the rest of the command line, newlines kept;
	/// false when the line would not fit the tick's buffer, and nothing runs then.
	/// </summary>
	public bool run_lua(string chunk)
	{
		if (string.IsNullOrEmpty(chunk))
		{
			return false;
		}

		return Post("run_lua " + chunk.Replace("\r", string.Empty));
	}

	// ---- helper calls: one line each, run in the guest tick with the real context -------------

	/// <summary>Adds (or takes, negative) gold from an entity's purse.</summary>
	public void give_gold(long entity, long delta) => Post($"give_gold {entity} {delta}");

	/// <summary>Changes an entity's health by delta, attributed to attacker (0 for none); negative hurts.</summary>
	public void modify_health(long entity, float delta, long attacker) =>
		Post($"modify_health {entity} {Num(delta)} {attacker}");

	/// <summary>Sets an entity's current health outright.</summary>
	public void set_health(long entity, float value) => Post($"set_health {entity} {Num(value)}");

	/// <summary>Adds one of an item definition (its GDB record hash: the "0x…" string the events carry, or a number) to an entity's inventory.</summary>
	public void add_item(long entity, Variant definition) => Post($"add_item {entity} {Arg(definition)}");

	/// <summary>Gives experience to one of an entity's pools.</summary>
	public void give_experience(long entity, int pool, float amount) =>
		Post($"give_experience {entity} {pool} {Num(amount)}");

	/// <summary>Spends experience from one of an entity's pools.</summary>
	public void spend_experience(long entity, int pool, float amount) =>
		Post($"spend_experience {entity} {pool} {Num(amount)}");

	/// <summary>Casts a spell as caster: the spell type, targeted (true/false), the target entity (0 for none) and the level 0..4.</summary>
	public void cast_spell(long caster, int type, bool targeted, long target, int level) =>
		Post($"cast_spell {caster} {type} {(targeted ? 1 : 0)} {target} {level}");

	/// <summary>Moves an entity to a world position.</summary>
	public void set_position(long entity, float x, float y, float z) =>
		Post($"set_position {entity} {Num(x)} {Num(y)} {Num(z)}");

	/// <summary>Sets an integer under <c>RefiiModSave.&lt;key&gt;</c>, persisted with the save; the key carries no spaces.</summary>
	public void save_set(string key, long value) =>
		Post($"save_set {key.Replace(' ', '_')} {value}");

	/// <summary>
	/// Asks the guest a question (<c>health</c>, <c>position</c>, <c>gold</c>, <c>item_count</c>,
	/// <c>experience</c>, <c>ability_level</c>, <c>relation</c>, <c>alive</c>, <c>definition</c>,
	/// <c>name</c>, <c>level_name</c>, <c>hero</c>, <c>player_of</c>, <c>lua_int</c>) with its
	/// arguments, and returns the id the answer will carry on <c>reply(id, result)</c>; 0 when
	/// the line could not be posted. The answer comes on a later frame, after the guest's tick.
	/// </summary>
	public long query(string what, Godot.Collections.Array args)
	{
		long id = ++_nextQueryId;
		var line = new StringBuilder("query ").Append(id).Append(' ').Append(what.Replace(' ', '_'));
		foreach (Variant arg in args)
		{
			line.Append(' ').Append(Arg(arg));
		}

		return Post(line.ToString()) ? id : 0;
	}

	/// <summary>One line to the guest tick; false, and nothing posted, when it would overflow the tick's buffer.</summary>
	private static bool Post(string line)
	{
		if (Encoding.UTF8.GetByteCount(line) >= CommandLimit)
		{
			GD.PushWarning($"[mods] godot: command of {Encoding.UTF8.GetByteCount(line)} bytes is over the {CommandLimit} limit; not posted");
			return false;
		}

		HostBridge.PostModCommand(line);
		return true;
	}

	private static string Num(float value) => value.ToString("R", CultureInfo.InvariantCulture);

	/// <summary>A query argument as the grammar reads it: a decimal number, true/false as 1/0, or the text itself.</summary>
	private static string Arg(Variant arg) => arg.VariantType switch
	{
		Variant.Type.Bool => arg.AsBool() ? "1" : "0",
		Variant.Type.Int => arg.AsInt64().ToString(CultureInfo.InvariantCulture),
		Variant.Type.Float => Num((float)arg.AsDouble()),
		_ => arg.AsString(),
	};

	/// <summary>The player's hero entity as the bus last stated it (hero_ready / hero_gone), or 0.</summary>
	public long hero(int player) => _hero.TryGetValue(player, out long id) ? id : 0;

	/// <summary>
	/// The node Godot draws an entity with, by the HANDLE an event gave the script, or null when
	/// the entity has none (not drawn, not yet built, or the guest draws it itself). Parent a light
	/// or an effect to it to follow the creature; never move it — the entity feed owns its transform.
	/// </summary>
	public Node3D? node_for(long handle)
	{
		if (handle <= 0 || handle > uint.MaxValue)
		{
			return null;
		}

		uint address = Refii.Host.HostBridge.ModHandleAddress((uint)handle);
		return address == 0 ? null : Refii.Game.GameEntities.Current?.NodeFor(address);
	}

	/// <summary>The enabled mods' names, in load order.</summary>
	public Godot.Collections.Array<string> mods() => new(EnabledMods);

	// ---- the field text --------------------------------------------------------------------

	/// <summary>
	/// The bus's Lua-table-literal body as a Dictionary: <c>key=value</c> pairs separated by
	/// commas; a value is a number (int when it has no point or exponent), <c>true</c>/<c>false</c>,
	/// or a 'single-quoted' string with no escapes (the bus replaces a quote inside with '_').
	/// Text that does not fit is kept as a string rather than dropped.
	/// </summary>
	/// <summary>
	/// The fields text as plain .NET values (long, double, bool, string), in order: the parser the
	/// tests exercise, with no Godot type in it (a Godot collection needs the engine running).
	/// </summary>
	public static List<KeyValuePair<string, object>> ParseFieldsPlain(string fields)
	{
		var result = new List<KeyValuePair<string, object>>();
		int i = 0;
		int n = fields.Length;
		while (i < n)
		{
			while (i < n && (fields[i] == ',' || fields[i] == ' '))
			{
				i++;
			}

			int keyStart = i;
			while (i < n && fields[i] != '=' && fields[i] != ',')
			{
				i++;
			}

			string key = fields[keyStart..i].Trim();
			if (i >= n || fields[i] != '=')
			{
				break;
			}

			i++;
			object value;
			if (i < n && fields[i] == '\'')
			{
				int close = fields.IndexOf('\'', i + 1);
				if (close < 0)
				{
					close = n;
				}

				value = fields[(i + 1)..close];
				i = Math.Min(close + 1, n);
			}
			else
			{
				int valueStart = i;
				while (i < n && fields[i] != ',')
				{
					i++;
				}

				value = ParseScalarPlain(fields[valueStart..i].Trim());
			}

			if (key.Length != 0)
			{
				result.Add(new KeyValuePair<string, object>(key, value));
			}
		}

		return result;
	}

	private static object ParseScalarPlain(string text)
	{
		if (text == "true")
		{
			return true;
		}

		if (text == "false")
		{
			return false;
		}

		if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long integer))
		{
			return integer;
		}

		if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double real))
		{
			return real;
		}

		return text;
	}

	/// <summary>The fields text as the Dictionary a script receives (engine only).</summary>
	public static Godot.Collections.Dictionary ParseFields(string fields)
	{
		var result = new Godot.Collections.Dictionary();
		foreach ((string key, object value) in ParseFieldsPlain(fields))
		{
			result[key] = value switch
			{
				long l => Variant.From(l),
				double d => Variant.From(d),
				bool b => Variant.From(b),
				_ => Variant.From((string)value),
			};
		}

		return result;
	}

}
