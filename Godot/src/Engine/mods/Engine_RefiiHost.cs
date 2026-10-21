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
	[Signal] public delegate void property_boughtEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void property_soldEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void rent_paidEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void property_rentedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void property_conditionEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void divorceEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void widowedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void child_bornEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void gift_givenEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void gift_acceptedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void engagedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void followEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void furniture_upgradedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void redecoratingEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void household_budgetEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void tracked_statEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void partnershipEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void household_restoredEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void dynamic_weatherEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void mod_menu_rowEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void mod_message_boxEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void mod_noticeEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void mod_menu_boxEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void mod_abilityEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void spell_pickEventHandler(Godot.Collections.Dictionary e);

	/// <summary>A <see cref="message_box"/>'s answer: the id it returned, and the button's index (0 is A, 1 is B; -1 when another box replaced it).</summary>
	[Signal] public delegate void message_box_answeredEventHandler(long id, long button);

	/// <summary>A <see cref="notice"/> has gone: the id it returned, 0 once shown, -1 when it never showed.</summary>
	[Signal] public delegate void notice_doneEventHandler(long id, long outcome);

	/// <summary>A <see cref="menu_box"/>'s answer: the id it returned and the chosen option's index (-1 for none).</summary>
	[Signal] public delegate void menu_box_answeredEventHandler(long id, long option);

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
		"property_bought", "property_sold", "rent_paid", "property_rented", "property_condition", "divorce", "widowed", "child_born", "gift_given", "gift_accepted", "engaged", "follow", "furniture_upgraded", "redecorating", "household_budget", "tracked_stat", "partnership", "household_restored",
		"dynamic_weather",
		"mod_menu_row",
		"mod_message_box",
		"mod_notice", "mod_menu_box",
		"mod_ability", "spell_pick",
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

	/// <summary>The guest's last <c>coop_state</c> answer: <see cref="local_hero"/>, <see cref="coop_mode"/>, <see cref="menu_player"/>.</summary>
	private long _localHero;
	private int _coopMode;
	private int _menuPlayer = 1;

	/// <summary>The id of the <c>coop_state</c> query in flight, answered into the fields above (0: none).</summary>
	private long _coopStateQuery;

	/// <summary>Frames the query in flight has waited; a line the guest never ran (a session change) is asked again.</summary>
	private int _coopStateWait;
	private const int CoopStateRetryFrames = 120;

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
		Refii.Fx.Engine_FxPlayer.StopAll();
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

		// One question a frame while a script is loaded: the co-op state the sync getters answer.
		if (mask != 0 && (_coopStateQuery == 0 || ++_coopStateWait > CoopStateRetryFrames))
		{
			_coopStateQuery = query("coop_state", new Godot.Collections.Array());
			_coopStateWait = 0;
		}

		List<(int Kind, string Fields)> events = HostBridge.DrainModEvents();
		foreach ((int kind, string fields) in events)
		{
			// An effect a native or Lua mod asked Godot to play or stop: drawn here, never an event.
			if (kind == Refii.Fx.Engine_FxPlayer.RequestKind)
			{
				Refii.Fx.Engine_FxPlayer.Handle(fields);
				continue;
			}

			// A menu or a message box a native or Lua mod asked for: Godot's UI, never an event.
			if (kind == Refii.Ui.Engine_UiModRequest.Kind)
			{
				Refii.Ui.Engine_UiModBoxes.Handle(fields);
				continue;
			}

			// A mod menu's rows or a host screen a native or Lua mod asked for: Godot's UI, never an event.
			if (kind == Refii.Ui.Engine_UiModRows.Kind)
			{
				Refii.Ui.Engine_UiModRows.Handle(fields);
				continue;
			}

			// A query's answer: the id goes back as the signal's first argument, never to mod_event.
			if (kind == ReplyKind)
			{
				Godot.Collections.Dictionary result = ParseFields(fields);
				long id = result.TryGetValue("id", out Variant idValue) ? idValue.AsInt64() : 0;
				result.Remove("id");
				if (id != 0 && id == _coopStateQuery)
				{
					_coopStateQuery = 0;
					_localHero = result.TryGetValue("local_hero", out Variant local) ? local.AsInt64() : 0;
					_coopMode = result.TryGetValue("coop_mode", out Variant mode) ? mode.AsInt32() : 0;
					_menuPlayer = result.TryGetValue("menu_player", out Variant owner) ? owner.AsInt32() : 1;
					continue;
				}

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

		Refii.Fx.Engine_FxPlayer.Pump(delta);
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

	/// <summary>
	/// Runs a Lua chunk in the mods' FRONT-END Lua on the guest's next tick (frontend_lua): the title
	/// and the front end, where there is no game VM — the current Gameface presentation's own state,
	/// with every mod's mod/frontend.lua run in it first (Refii.Mods.ModLua.FrontendPreamble says
	/// what Refii.* holds there). False when the line would not fit, and nothing runs then.
	/// </summary>
	public bool frontend_lua(string chunk)
	{
		if (string.IsNullOrEmpty(chunk))
		{
			return false;
		}

		return Post(ModLua.FrontendCommand + chunk.Replace("\r", string.Empty));
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

	/// <summary>
	/// Sets a player's SESSION value (<c>Refii.player_set</c>): the player store refii_mod_player_*
	/// keeps, the same on both co-op machines. <paramref name="key"/> is "&lt;mod&gt;.&lt;name&gt;", no spaces.
	/// </summary>
	public void player_set(int player, string key, float value) =>
		Post($"player_set {player} {key.Replace(' ', '_')} {Num(value)}");

	/// <summary>Adds to a player's value (<c>Refii.player_add</c>; missing counts as 0); <paramref name="saved"/> keeps the key with the hero's save.</summary>
	public void player_add(int player, string key, float delta, bool saved) =>
		Post($"player_add {player} {key.Replace(' ', '_')} {Num(delta)} {(saved ? 1 : 0)}");

	/// <summary>Sets a player's SAVED value (<c>Refii.player_save</c>): kept with that hero's own save, back when it loads.</summary>
	public void player_save(int player, string key, float value) =>
		Post($"player_save {player} {key.Replace(' ', '_')} {Num(value)}");

	/// <summary>Sets an integer under <c>RefiiModSave.&lt;key&gt;</c>, persisted with the save; the key carries no spaces.</summary>
	public void save_set(string key, long value) =>
		Post($"save_set {key.Replace(' ', '_')} {value}");

	/// <summary>
	/// Asks the guest a question (<c>health</c>, <c>position</c>, <c>gold</c>, <c>item_count</c>,
	/// <c>experience</c>, <c>ability_level</c>, <c>relation</c>, <c>alive</c>, <c>definition</c>,
	/// <c>name</c>, <c>level_name</c>, <c>hero</c>, <c>player_of</c>, <c>lua_int</c>, and the co-op
	/// surface the Lua side answers: <c>player_for E</c> (who "the hero" is for E: a dog its master,
	/// else the nearer hero), <c>world_here</c>, <c>combat_state P</c>, <c>local_hero</c>, <c>coop_mode</c>, <c>menu_player</c>,
	/// <c>coop_state</c>, <c>player_value P KEY [FALLBACK]</c>, <c>player_here P</c>,
	/// <c>player_dog P</c>, <c>focus P</c>, <c>opinion E P</c>, <c>uid E</c> — ModApi.GodotHelpers)
	/// with its arguments, and returns the id the answer will carry on <c>reply(id, result)</c>; 0
	/// when the line could not be posted. The answer comes on a later frame, after the guest's tick.
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

	/// <summary>
	/// The player's hero entity as the bus last stated it (hero_ready / hero_gone), or 0. Player 1 is
	/// the host's hero on both machines: on an online joiner <c>hero(1)</c> is the partner, and
	/// "this machine's hero" is <see cref="local_hero"/>.
	/// </summary>
	public long hero(int player) => _hero.TryGetValue(player, out long id) ? id : 0;

	/// <summary>
	/// The local player's hero handle, as the guest last answered (Refii.local_hero; asked every
	/// frame while a script is loaded, so one guest tick behind), or 0: player two's on an online
	/// joiner, player one's on a host or alone, and in local co-op the hero of whoever owns the
	/// open menus. Name a player with <see cref="hero"/> when it must not follow the menus.
	/// </summary>
	public long local_hero() => _localHero;

	/// <summary>Refii.coop_mode as the guest last answered: 0 alone, 1 local co-op, 2 online hosting, 3 online joined.</summary>
	public int coop_mode() => _coopMode;

	/// <summary>Refii.menu_player as the guest last answered: whose menus are open on this machine, 1 or 2.</summary>
	public int menu_player() => _menuPlayer;

	/// <summary>
	/// Refii.world_here as the guest last answered: whether this machine runs the shared world (the
	/// AI, every hit's outcome, the NPCs) — alone, in local co-op and on an online host; false on an
	/// online joiner. A script's world effects (give_gold to an NPC, spawn, cast_spell on a victim)
	/// belong where this holds; an event dictionary with <c>replica = true</c> is the other machine's.
	/// </summary>
	public bool world_here() => _coopMode != 3;

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

	// ---- the UI: mod menus, message boxes, published values ----------------------------------

	/// <summary>
	/// Publishes a mod value under <paramref name="key"/> ("&lt;mod&gt;.&lt;name&gt;", no spaces): what
	/// a HUD piece reads as <c>mod:&lt;key&gt;</c>, Lua as <c>Refii.value(key)</c> and native code as
	/// <c>refii::mods::Published</c>. Set on the guest's next tick, the moment every reader sees it.
	/// </summary>
	public void publish(string key, float value) =>
		Post($"publish {key.Replace(' ', '_')} {Num(value)}");

	/// <summary>
	/// Opens a menu a mod defines in <c>ui/templates.json</c> for <paramref name="player"/> (1 or 2;
	/// 0, the default, for whoever owns the menus here): over whichever menu list has the input (the
	/// front end, the pause book or a screen's list) — B returns to it — or, with no list up, in
	/// play or on the title, as a list of its own that takes the pad and closes on B. False when no
	/// template has that name or the player is played on the other machine (which opens it there).
	/// </summary>
	public bool open_menu(string name, long player = 0) =>
		Refii.Ui.Engine_UiModMenus.OpenFor(name, (int)Math.Clamp(player, 0, 2));

	/// <summary>
	/// States the rows of a menu list for <paramref name="player"/> (1 or 2; 0, the default, for every
	/// player), replacing what was stated before — what a menu or entry with <c>"source": "rows"</c>
	/// (or <c>rows:&lt;list&gt;</c>) shows, on screen at once if it is open. Each row is a string (its
	/// name and label) or a dictionary <c>{ name, label, command, ghosted }</c>: <c>label</c> a Babel
	/// tag or literal, <c>command</c> any row verb (<c>Mod:lua:</c>, <c>Opens:</c>, <c>Screen:open:</c>,
	/// a registered command). An empty array clears the list.
	/// </summary>
	public void set_menu_rows(string menu, Godot.Collections.Array rows, long player = 0)
	{
		var stated = new List<Refii.Ui.Engine_UiModRows.Stated>();
		foreach (Variant row in rows)
		{
			if (row.VariantType == Variant.Type.Dictionary)
			{
				Godot.Collections.Dictionary record = row.AsGodotDictionary();
				string Text(string key) => record.TryGetValue(key, out Variant v) ? v.AsString() : string.Empty;
				string name = Text("name").Length != 0 ? Text("name") : Text("label");
				if (name.Length != 0)
				{
					stated.Add(new Refii.Ui.Engine_UiModRows.Stated(name, Text("label"),
						Text("command") is { Length: > 0 } command ? command : null,
						record.TryGetValue("ghosted", out Variant ghosted) && ghosted.AsBool()));
				}
			}
			else if (row.AsString() is { Length: > 0 } name)
			{
				stated.Add(new Refii.Ui.Engine_UiModRows.Stated(name, name, null, false));
			}
		}

		Refii.Ui.Engine_UiModRows.Set(menu, stated, (int)Math.Clamp(player, 0, 2));
	}

	/// <summary>
	/// Opens a host-owned screen by id for <paramref name="player"/> (0, the default, for whoever owns
	/// the menus here) — the <c>Screen:open:&lt;id&gt;</c> verb, as a row on the list with the input
	/// would: the game's (achievements 1013, multiplayer 1014) or a <c>screens</c> record a mod states
	/// (id 1000 and up, with a <c>menu</c> and a <c>presentation</c>). A template book stands over
	/// the pause book, so it opens while the pause list has the input. False when it did not open
	/// (the log says why) or the player is played on the other machine.
	/// </summary>
	public bool open_screen(long screen, long player = 0) =>
		screen is > 0 and <= uint.MaxValue &&
		Refii.Ui.Engine_UiModRows.OpenScreen((uint)screen, (int)Math.Clamp(player, 0, 2));

	/// <summary>
	/// Registers a ROW VERB every list runs (<c>Refii.Ui.Engine_UiCommands</c>): a row whose command is
	/// <paramref name="verb"/> — or, when it ends in <c>:</c>, begins with it — calls
	/// <paramref name="handler"/> with <c>(argument, menu, row, player)</c>. A return of <c>false</c>
	/// reports the row refused; anything else is done. The game's verbs stay the game's: false when
	/// the name is one of them.
	/// </summary>
	public bool register_command(string verb, Callable handler)
	{
		var record = new Refii.Ui.Engine_UiVerb(verb, verb, "a Godot script's verb", Array.Empty<Refii.Ui.Engine_UiCommandList>(),
			Owner: "godot");
		return Refii.Ui.Engine_UiCommands.Register(record, (argument, context) =>
		{
			if (handler.Target is { } target && !IsInstanceValid(target))
			{
				return Refii.Ui.Engine_UiCommandResult.Refused("the script that registered it is gone");
			}

			Variant answer = handler.Call(argument, context.Menu, context.Row, (long)context.Player);
			return answer.VariantType == Variant.Type.Bool && !answer.AsBool()
				? Refii.Ui.Engine_UiCommandResult.Refused("the script's handler answered false")
				: Refii.Ui.Engine_UiCommandResult.Done;
		});
	}

	private long _nextBoxId = Refii.Ui.Engine_UiModRequest.FirstHostBoxId - 1;

	/// <summary>
	/// Raises a message box in the game's own box art — in game the query or message box, on the
	/// front end its own dialog — for <paramref name="player"/> (1 or 2; 0, the default, for whoever
	/// owns the menus here): <paramref name="title"/> above <paramref name="text"/>, with one button
	/// (a notice) or two (a question, A and B, their captions <paramref name="buttons"/>[0] and [1];
	/// none keeps the box's own captions); more than two is a menu box (<see cref="menu_box"/>) whose
	/// answer is the button's index, on this box's signal and event. Returns the id <c>message_box_answered(id, button)</c> and
	/// the <c>mod_message_box</c> event carry: 0 for A, 1 for B, -1 when another box replaced it or
	/// the player is played on the other machine. One question and one notice stand at a time.
	/// </summary>
	public long message_box(string title, string text, Godot.Collections.Array<string> buttons, long player = 0)
	{
		long id = NextBoxId();
		void Answered(int button)
		{
			if (IsInstanceValid(this))
			{
				EmitSignal(SignalName.message_box_answered, id, (long)button);
			}
		}

		// More than two buttons is a menu box, answered as this box: the button's index.
		if (buttons.Count > 2)
		{
			Refii.Ui.Engine_UiModBoxes.MenuBox((uint)id, (int)Math.Clamp(player, 0, 2), title, text,
				MenuOptions(buttons), cancelable: true, Answered, asMessageBox: true);
			return id;
		}

		string? Caption(int i) => buttons.Count > i && buttons[i].Length != 0 ? buttons[i] : null;
		Refii.Ui.Engine_UiModBoxes.Raise((uint)id, (int)Math.Clamp(player, 0, 2), title, text,
			Caption(0), Caption(1), buttons.Count >= 2, Answered);
		return id;
	}

	/// <summary>The next box id, shared by message_box, notice and menu_box (Godot's half of the id space).</summary>
	private long NextBoxId()
	{
		if (++_nextBoxId > Refii.Ui.Engine_UiModRequest.LastBoxId)
		{
			_nextBoxId = Refii.Ui.Engine_UiModRequest.FirstHostBoxId;
		}

		return _nextBoxId;
	}

	/// <summary>A menu box's options: at most <see cref="Refii.Ui.Engine_UiModRequest.MaxMenuOptions"/>.</summary>
	private static List<string> MenuOptions(Godot.Collections.Array<string> options)
	{
		var list = new List<string>();
		foreach (string option in options)
		{
			if (list.Count == Refii.Ui.Engine_UiModRequest.MaxMenuOptions)
			{
				GD.PushWarning($"[mods] godot: a menu box takes {Refii.Ui.Engine_UiModRequest.MaxMenuOptions} options; the rest are dropped");
				break;
			}

			list.Add(option);
		}

		return list;
	}

	/// <summary>
	/// Posts a notice for <paramref name="player"/> (1 or 2; 0, the default, for whoever owns the
	/// menus here) on the notice channel the game's own co-op notices use — in game the HUD's info
	/// box for <paramref name="seconds"/> (0: the channel's length), on the front end its dialog until
	/// dismissed: <paramref name="title"/> above <paramref name="text"/>. Notices queue one after
	/// another. Returns the id <c>notice_done(id, outcome)</c> and the <c>mod_notice</c> event carry:
	/// 0 once it has shown and gone, -1 when it never showed (its player is played on the other
	/// machine, which shows it there).
	/// </summary>
	public long notice(string title, string text, double seconds = 0, long player = 0)
	{
		long id = NextBoxId();
		Refii.Ui.Engine_UiModBoxes.Notice((uint)id, (int)Math.Clamp(player, 0, 2), title, text,
			double.IsFinite(seconds) ? (float)Math.Clamp(seconds, 0, 600) : 0f, outcome =>
			{
				if (IsInstanceValid(this))
				{
					EmitSignal(SignalName.notice_done, id, (long)outcome);
				}
			});
		return id;
	}

	/// <summary>
	/// Puts up a menu box in the game's own menu box art (the Golden Cock's), on the front end and in
	/// game, for <paramref name="player"/> (1 or 2; 0, the default, for whoever owns the menus here):
	/// <paramref name="title"/> above <paramref name="text"/>, then <paramref name="options"/> (up to
	/// 16; more than the box's six rows are paged). B closes a <paramref name="cancelable"/> box.
	/// Returns the id <c>menu_box_answered(id, option)</c> and the <c>mod_menu_box</c> event carry:
	/// the chosen option's index, -1 for B, another box replacing it, or a player played on the
	/// other machine. One menu box stands at a time.
	/// </summary>
	public long menu_box(string title, string text, Godot.Collections.Array<string> options, long player = 0, bool cancelable = true)
	{
		long id = NextBoxId();
		Refii.Ui.Engine_UiModBoxes.MenuBox((uint)id, (int)Math.Clamp(player, 0, 2), title, text, MenuOptions(options),
			cancelable, option =>
			{
				if (IsInstanceValid(this))
				{
					EmitSignal(SignalName.menu_box_answered, id, (long)option);
				}
			});
		return id;
	}

	/// <summary>The ModNotice event's Godot surface, raised where the notice went (the channel is Godot's).</summary>
	internal void RaiseModNotice(uint id, int outcome, int player)
	{
		var e = new Godot.Collections.Dictionary
		{
			["id"] = (long)id,
			["outcome"] = (long)outcome,
			["player"] = (long)player,
		};
		EmitSignal(SignalName.mod_notice, e);
		EmitSignal(SignalName.mod_event, "mod_notice", e);
	}

	/// <summary>The ModMenuBox event's Godot surface, raised where the box was answered.</summary>
	internal void RaiseModMenuBox(uint id, int option, int player)
	{
		var e = new Godot.Collections.Dictionary
		{
			["id"] = (long)id,
			["option"] = (long)option,
			["player"] = (long)player,
		};
		EmitSignal(SignalName.mod_menu_box, e);
		EmitSignal(SignalName.mod_event, "mod_menu_box", e);
	}

	/// <summary>
	/// The ModMessageBox event's Godot surface: a box a mod raised (any surface's) was answered —
	/// the <c>mod_message_box</c> signal and <c>mod_event</c>, here as it happens. The guest raises
	/// the same event for native and Lua handlers on its tick and never posts it back here.
	/// </summary>
	internal void RaiseModMessageBox(uint id, int button, int player)
	{
		var e = new Godot.Collections.Dictionary
		{
			["id"] = (long)id,
			["button"] = (long)button,
			["player"] = (long)player,
		};
		EmitSignal(SignalName.mod_message_box, e);
		EmitSignal(SignalName.mod_event, "mod_message_box", e);
	}

	/// <summary>
	/// The ModMenuRow event's Godot surface, raised where it happens (the menu is Godot's): the
	/// <c>mod_menu_row</c> signal and <c>mod_event</c>. The guest raises the same event for native
	/// and Lua handlers on its tick and never posts it back here.
	/// </summary>
	internal void RaiseModMenuRow(string menu, string row, int player, string mod)
	{
		var e = new Godot.Collections.Dictionary
		{
			["menu"] = menu,
			["row"] = row,
			["player"] = (long)player,
			["mod"] = mod,
		};
		EmitSignal(SignalName.mod_menu_row, e);
		EmitSignal(SignalName.mod_event, "mod_menu_row", e);
	}

	// ---- effects: Godot draws them (Engine_FxPlayer) --------------------------------------------

	/// <summary>
	/// Plays effect <paramref name="name"/> (the particle bank's or a mod's fx/effects/, by name or
	/// FX_XXXXXXXX token) at a world position in the guest's axes, as the events state positions.
	/// <paramref name="options"/>: duration (seconds, 0 until stop_fx), scale, tint (Color or
	/// [r, g, b]), intensity, loop (true; false plays it once). Returns the id stop_fx takes, or 0
	/// for an unknown effect.
	/// </summary>
	public long play_fx(string name, float x, float y, float z, Godot.Collections.Dictionary options) =>
		PlayFx(name, new Vector3(x, y, z), 0, options);

	/// <summary>
	/// Plays effect <paramref name="name"/> on an entity by its HANDLE: it stands in the entity's own
	/// frame, options.offset (Vector3 or [x, y, z]) along the entity's axes, and moves and turns with
	/// it; the other options as play_fx. 0 for an unknown effect or handle.
	/// </summary>
	public long play_fx_on(string name, long handle, Godot.Collections.Dictionary options) =>
		handle <= 0 || handle > uint.MaxValue ? 0 : PlayFx(name, Vector3.Zero, (uint)handle, options);

	/// <summary>Stops a played effect by any surface's id (native, Lua or Godot); false when none plays under it.</summary>
	public bool stop_fx(long id) => id > 0 && id <= uint.MaxValue && Refii.Fx.Engine_FxPlayer.Stop((uint)id);

	private static long PlayFx(string name, Vector3 at, uint entity, Godot.Collections.Dictionary options)
	{
		float Number(string key, float fallback) =>
			options.TryGetValue(key, out Variant v) && v.VariantType is Variant.Type.Float or Variant.Type.Int ? (float)v.AsDouble() : fallback;
		Vector3 Triple(string key, Vector3 fallback)
		{
			if (!options.TryGetValue(key, out Variant v))
			{
				return fallback;
			}

			if (v.VariantType == Variant.Type.Vector3)
			{
				return v.AsVector3();
			}

			if (v.VariantType == Variant.Type.Color)
			{
				Color c = v.AsColor();
				return new Vector3(c.R, c.G, c.B);
			}

			if (v.VariantType == Variant.Type.Array && v.AsGodotArray() is { Count: >= 3 } a)
			{
				return new Vector3((float)a[0].AsDouble(), (float)a[1].AsDouble(), (float)a[2].AsDouble());
			}

			return fallback;
		}

		Vector3 tint = Triple("tint", Vector3.One);
		bool loop = !options.TryGetValue("loop", out Variant loopValue) || loopValue.AsBool();
		return Refii.Fx.Engine_FxPlayer.Play(
			name, at, entity, Number("duration", 0f), Number("scale", 1f), new Color(tint.X, tint.Y, tint.Z),
			Number("intensity", 1f), loop, Triple("offset", Vector3.Zero));
	}

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
