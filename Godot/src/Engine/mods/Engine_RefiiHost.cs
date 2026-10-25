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
	[Signal] public delegate void partner_messageEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void look_at_buttonEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void vortex_startedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void vortex_endedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void fireball_impactEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void pausedEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void start_gameEventHandler(Godot.Collections.Dictionary e);
	[Signal] public delegate void cutsceneEventHandler(Godot.Collections.Dictionary e);

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
		"partner_message",
		"look_at_button", "vortex_started", "vortex_ended", "fireball_impact",
		"paused", "start_game",
		"cutscene",
	};

	public const string SingletonName = "RefiiHost";

	/// <summary>The <c>lua_ready</c> event's kind (REFII_EVENT_LUA): every mod's init.lua has run again.</summary>
	public const int LuaReadyKind = 3;
	public const string ModEventSignal = "mod_event";

	/// <summary>The cutscene event's kind: the host hears it while any script is loaded, for <see cref="in_cutscene"/>.</summary>
	private const int CutsceneKind = 100;

	/// <summary>
	/// The kinds the host keeps in the mask while any script is loaded: hero_ready and hero_gone feed
	/// <see cref="hero"/>, cutscene feeds <see cref="in_cutscene"/>.
	/// </summary>
	private static readonly ModEventMask HeroKinds = new ModEventMask().With(1).With(2).With(CutsceneKind);

	/// <summary>The cutscene statements as the bus last stated them (the cutscene event, seeded by the <c>in_cutscene</c> queries a new host sends).</summary>
	private readonly ModCutscene _cutscene = new();

	/// <summary>Ids of the two <c>in_cutscene</c> queries a new host sends, answered into <see cref="_cutscene"/>.</summary>
	private readonly Dictionary<long, long> _cutsceneSeedQueries = new();

	/// <summary>The kind a query's reply arrives under (REFII_MOD_HOST_REPLY_KIND); never an event.</summary>
	public const int ReplyKind = 1000;

	/// <summary>A command line's limit: the tick drains into <c>char line[4096]</c> (mod_commands.cpp).</summary>
	public const int CommandLimit = 4096;

	private long _nextQueryId;

	/// <summary>Signal per kind, from the catalog; null where the catalog names a signal this class does not declare.</summary>
	private readonly StringName?[] _signalByKind;
	private readonly StringName[] _nameByKind;
	private readonly Dictionary<long, long> _hero = new();
	private ModEventMask _mask;
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
		// The save gate's verdict on start_game is always installed (ModSaveGate).
		PushVerdictMask();
		for (long player = 1; player <= 2; player++)
		{
			long id = query("hero", new Godot.Collections.Array { player });
			if (id != 0)
			{
				_heroSeedQueries[id] = player;
			}

			// A scene already playing when this host was made raised its edge before the host heard.
			long cutscene = query("in_cutscene", new Godot.Collections.Array { player });
			if (cutscene != 0)
			{
				_cutsceneSeedQueries[cutscene] = player;
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
		var mask = new ModEventMask();
		lock (VerdictLock)
		{
			foreach ((int kind, List<Callable> list) in Verdicts)
			{
				if (list.Count != 0)
				{
					mask = mask.With(kind);
				}
			}
		}

		// The engine's own verdict: the save gate refuses a start whose save needs filtering.
		mask = mask.With(ModSaveGate.StartGameKind);
		if (!mask.IsZero && !_verdictInstalled)
		{
			HostBridge.SetModVerdictHandler(VerdictCallback);
			_verdictInstalled = true;
		}

		HostBridge.SetModVerdictMask(mask);
		if (mask.IsZero && _verdictInstalled)
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
				handlers = Verdicts.TryGetValue(kind, out List<Callable>? list) ? list.ToArray() : Array.Empty<Callable>();
			}

			string text = Marshal.PtrToStringUTF8(fields, (int)length) ?? string.Empty;
			List<KeyValuePair<string, object>> plain = ParseFieldsPlain(text);
			var before = new Dictionary<string, object>();
			foreach ((string key, object value) in plain)
			{
				before[key] = value;
			}

			// The engine's own verdict first: the save gate (start_game), which no script sees.
			bool cancel = ModSaveGate.Verdict(kind, plain);
			if (handlers.Length == 0)
			{
				if (!cancel)
				{
					return 0;
				}

				byte[] refusal = Encoding.UTF8.GetBytes("cancel=1");
				int m = Math.Min(refusal.Length, (int)capacity);
				Marshal.Copy(refusal, 0, output, m);
				return m;
			}

			Godot.Collections.Dictionary e = ParseFields(text);
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
		Refii.Lighting.Engine_ModLights.Clear();
		if (_maskSent)
		{
			HostBridge.SetModEventMask(new ModEventMask());
			_maskSent = false;
		}
	}

	/// <summary>Once per frame: the mask from the live connections, then every event posted since the last frame.</summary>
	public override void _Process(double delta)
	{
		ModEventMask mask = ComputeMask();
		if (mask != _mask || !_maskSent)
		{
			_mask = mask;
			_maskSent = true;
			HostBridge.SetModEventMask(mask);
		}

		// One question a frame while a script is loaded: the co-op state the sync getters answer.
		if (!mask.IsZero && (_coopStateQuery == 0 || ++_coopStateWait > CoopStateRetryFrames))
		{
			_coopStateQuery = query("coop_state", new Godot.Collections.Array());
			_coopStateWait = 0;
		}

		List<(int Kind, string Fields)> events = HostBridge.DrainModEvents();
		foreach ((int kind, string fields) in events)
		{
			// A save package the guest wrote: its sidecar is stamped with the mod set (ModSaveGate).
			if (kind == ModSaveGate.SaveWrittenKind)
			{
				ModSaveGate.OnSaveWritten(fields);
				continue;
			}

			// A save's Lua table loaded: a filtered load drops the missing mods' keys now.
			if (kind == ModSaveGate.SaveLoadedKind)
			{
				ModSaveGate.OnSaveLoaded();
			}

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

			// An online joiner's weapon pick a native or Lua mod made (refii_mod_equip_weapon): into
			// the co-op kit, which carries it to the host; never an event.
			if (kind == CoopHeroKit.PickKind)
			{
				CoopHeroKit.HandlePick(fields);
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

				if (_cutsceneSeedQueries.Remove(id, out long cutscenePlayer))
				{
					_cutscene.Stated(cutscenePlayer, result.TryGetValue("kinds", out Variant kinds) ? kinds.AsInt32() : 0);
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

			// THE MODS' LUA RELOADED (lua_ready): the claims and HUD overrides the last chunk held go
			// with it, so a reloaded mod starts from nothing claimed and nothing overridden.
			if (kind == LuaReadyKind)
			{
				ModInput.ClearClaims();
				Refii.Ui.Engine_UiHudOverrides.Clear();
			}

			Godot.Collections.Dictionary e = ParseFields(fields);
			TrackHero(kind, e);
			TrackCutscene(kind, e);
			if (_signalByKind[kind] is { } signal)
			{
				EmitSignal(signal, e);
			}

			EmitSignal(SignalName.mod_event, _nameByKind[kind].ToString(), e);
		}

		Refii.Fx.Engine_FxPlayer.Pump(delta);
		Refii.Lighting.Engine_ModLights.Pump(delta);
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

	/// <summary>The cutscene event into <see cref="_cutscene"/>: kinds is every statement holding after the edge.</summary>
	private void TrackCutscene(int kind, Godot.Collections.Dictionary e)
	{
		if (kind == CutsceneKind)
		{
			_cutscene.Stated(e.TryGetValue("player", out Variant p) ? p.AsInt64() : -1,
				e.TryGetValue("kinds", out Variant k) ? k.AsInt32() : 0);
		}
	}

	/// <summary>
	/// Bit k set when some script is connected to kind k's signal; every bit when one is
	/// connected to mod_event; the hero kinds whenever a script is loaded at all; zero when none is.
	/// </summary>
	private ModEventMask ComputeMask()
	{
		// The engine's own want: a filtered load drops the missing mods' RefiiModSave keys on
		// save_loaded (ModSaveGate), scripts or none.
		ModEventMask own = ModSaveGate.WantsSaveLoaded ? new ModEventMask().With(ModSaveGate.SaveLoadedKind) : new ModEventMask();
		if (ScriptsRoot is null || !IsInstanceValid(ScriptsRoot) || ScriptsRoot.GetChildCount() == 0)
		{
			return own;
		}

		ModEventMask mask = ModSaveGate.WantsSaveLoaded ? HeroKinds.With(ModSaveGate.SaveLoadedKind) : HeroKinds;
		if (GetSignalConnectionList(SignalName.mod_event).Count != 0)
		{
			for (int kind = 0; kind < _nameByKind.Length; kind++)
			{
				mask = mask.With(kind);
			}

			return mask;
		}

		for (int kind = 0; kind < _signalByKind.Length; kind++)
		{
			if (_signalByKind[kind] is { } signal && GetSignalConnectionList(signal).Count != 0)
			{
				mask = mask.With(kind);
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

	/// <summary>Casts a spell as caster: the spell type, targeted (true/false), the target entity (0 for none) and the level 0..4, starting at the caster's own position and facing.</summary>
	public void cast_spell(long caster, int type, bool targeted, long target, int level) =>
		Post($"cast_spell {caster} {type} {(targeted ? 1 : 0)} {target} {level}");

	/// <summary>
	/// Casts a spell as caster from a stated start position facing <paramref name="dir"/> (guest axes), as
	/// Fable III's weave places its partner casts: Fable 2's targeted classes place from the position along
	/// the facing (Lightning draws its bolt from there to the target, Fireball spawns its shot there, Force
	/// Push shoves the target away from there).
	/// </summary>
	public void cast_spell_at(long caster, int type, bool targeted, long target, int level, Vector3 pos, Vector3 dir) =>
		Post($"cast_spell {caster} {type} {(targeted ? 1 : 0)} {target} {level} {Num(pos.X)} {Num(pos.Y)} {Num(pos.Z)} {Num(dir.X)} {Num(dir.Y)} {Num(dir.Z)}");

	/// <summary>Moves an entity to a world position.</summary>
	public void set_position(long entity, float x, float y, float z) =>
		Post($"set_position {entity} {Num(x)} {Num(y)} {Num(z)}");

	/// <summary>
	/// Tints an entity through the game's own appearance setter (refii_mod_entity_tint): linear
	/// multipliers of its lit colour, 1 untouched, above 1 brighter; both looks draw it and co-op
	/// replicates it. <paramref name="a"/> is stored, never drawn: opacity is not the tint's.
	/// </summary>
	public void entity_tint(long entity, float r, float g, float b, float a = 1f) =>
		Post($"entity_tint {entity} {Num(r)} {Num(g)} {Num(b)} {Num(a)}");

	/// <summary>Puts an entity's tint back to the constructor's {1, 1, 1, 1} (refii_mod_entity_untint).</summary>
	public void entity_untint(long entity) => Post($"entity_untint {entity}");

	/// <summary>
	/// An entity's opacity 0..1 through the game's own SetAlpha (refii_mod_entity_alpha); 1 is the
	/// constructor's. An entity whose own update restates its alpha (a Blades sword, a dying
	/// creature) keeps the game's value.
	/// </summary>
	public void entity_alpha(long entity, float alpha) => Post($"entity_alpha {entity} {Num(alpha)}");

	/// <summary>An entity's uniform scale through the game's own SetScale (refii_mod_entity_scale); 1 is the constructor's.</summary>
	public void entity_scale(long entity, float scale) => Post($"entity_scale {entity} {Num(scale)}");

	/// <summary>The renderable's saturation mask (refii_mod_entity_saturation_mask): drawn desaturated while on; off is the constructor's.</summary>
	public void entity_saturation_mask(long entity, bool on) => Post($"entity_saturation_mask {entity} {(on ? 1 : 0)}");

	/// <summary>Whether the entity's body is drawn at all (refii_mod_entity_drawable); the entity keeps simulating. On is the constructor's.</summary>
	public void entity_drawable(long entity, bool on) => Post($"entity_drawable {entity} {(on ? 1 : 0)}");

	/// <summary>
	/// Locks player 1 or 2's hero on <paramref name="entity"/>, or with 0 on the game's own pick
	/// (refii_mod_lock_target): the game's force byte, look-at flag and SetTarget, what a held LT
	/// states. <c>query locked_target P</c> answers the target held.
	/// </summary>
	public void lock_target(int player, long entity = 0) => Post($"lock_target {player} {entity}");

	/// <summary>Ends the lock lock_target stated for player 1 or 2 (refii_mod_release_lock); a held LT is the trigger's.</summary>
	public void release_lock(int player) => Post($"release_lock {player}");

	/// <summary>
	/// States that hero <paramref name="handle"/>'s target lock switches by the MOUSE WHEEL
	/// (refii_mod_wheel_switches_target, what <c>Refii.wheel_switches_target</c> states each
	/// tick): STICKY — a Godot script runs off the guest tick, so the tick restates it on every
	/// tick from this call until <paramref name="on"/> false or the hero's player no longer holds
	/// a lock (the game's own release, or <see cref="release_lock"/>). While it holds and the hero
	/// is locked under the third-person camera the keyboard and mouse drive, the mouse's movement
	/// never switches target and each wheel notch is one switch; a pad is untouched. False for a
	/// bad handle.
	/// </summary>
	public bool wheel_switches_target(long handle, bool on = true) =>
		handle > 0 && handle <= uint.MaxValue && Post($"wheel_switches_target {handle} {(on ? 1 : 0)}");

	/// <summary>Switches a player's locked target to the next one left or right (refii_mod_switch_target), the camera's own flick.</summary>
	public void switch_target(int player, bool left) => Post($"switch_target {player} {(left ? 1 : 0)}");

	/// <summary>
	/// Uses an inventory item of player 1 or 2's hero by its definition (its GDB record hash: the
	/// "0x…" string the events carry, or a number) through the game's own use (refii_mod_use_item):
	/// the quick menu's action, gated as the quick menu is (the hero alive, the d-pad up, the game's
	/// CanPlayerOpenQuickMenu for that player).
	/// </summary>
	public void use_item(int player, Variant definition) => Post($"use_item {player} {Arg(definition)}");

	/// <summary>
	/// Equips a weapon the hero's inventory holds, by definition, through the weapon set's own
	/// EquipWeaponByRecord (refii_mod_equip_weapon). On an online joiner the pick goes through the
	/// co-op kit to the host, who equips it and replicates the held item back — the caller never cares.
	/// </summary>
	public void equip_weapon(int player, Variant definition) => Post($"equip_weapon {player} {Arg(definition)}");

	/// <summary>Performs an expression the hero's performer knows, by its record (refii_mod_perform_expression); <c>query expressions P</c> lists them.</summary>
	public void perform_expression(int player, Variant expression) => Post($"perform_expression {player} {Arg(expression)}");

	/// <summary>Assigns spell type 0..7 to every tier player 1 or 2's hero knows (refii_mod_assign_spell); <c>query spells_known P</c> lists the types.</summary>
	public void assign_spell(int player, int spell) => Post($"assign_spell {player} {spell}");

	/// <summary>
	/// Puts a status on an entity for seconds (refii_mod_apply_status): kind 1 electrified, 2 chaos,
	/// 3 burning (4, a vortex, is refused), at spell level 0..4, from <paramref name="source"/> (0:
	/// the entity itself; chaos takes its direction from it). The status_effect event fires.
	/// </summary>
	public void apply_status(long entity, int kind, float seconds, int level = 0, long source = 0) =>
		Post($"apply_status {entity} {kind} {Num(seconds)} {level} {source}");

	/// <summary>Ends a status through the game's own remover (refii_mod_clear_status). <c>query has_status E KIND</c> asks.</summary>
	public void clear_status(long entity, int kind) => Post($"clear_status {entity} {kind}");

	/// <summary>
	/// Overrides player 1 or 2's movement speed by a factor (refii_mod_set_speed_multiplier): the
	/// player component's own override, what the scripts' Player.SetOverriddenMovementSpeedMultiplier
	/// and SetPlayerMovementSpeedOverridden state; 0 holds the hero in place. <c>query speed_multiplier P</c>
	/// answers whether one holds and the factor — take it only while none holds, and give it back.
	/// </summary>
	public void set_speed_multiplier(int player, float factor) => Post($"set_speed_multiplier {player} {Num(factor)}");

	/// <summary>Clears the override set_speed_multiplier stated (refii_mod_clear_speed_multiplier).</summary>
	public void clear_speed_multiplier(int player) => Post($"clear_speed_multiplier {player}");

	/// <summary>
	/// Grants an action's "ALLOW_ACTION_INTERRUPT" (refii_mod_allow_action_interrupt): what the
	/// animation event itself does, so a cancelled anim_event (a committed swing) is granted later.
	/// <c>query current_action E</c> answers the action an entity's manager runs, its clock and whether it is granted.
	/// </summary>
	public void allow_action_interrupt(long action) => Post($"allow_action_interrupt {action}");

	/// <summary>
	/// Knocks an entity down and away along a direction scaled by force (refii_mod_knockback): a
	/// Force Push's own hit with no damage and no attacker through the game's one hit receiver.
	/// </summary>
	public void knockback(long entity, float x, float y, float z, float force = 1f) =>
		Post($"knockback {entity} {Num(x)} {Num(y)} {Num(z)} {Num(force)}");

	/// <summary>Staggers an entity: the flinch a 0-damage melee tap plays (refii_mod_stagger).</summary>
	public void stagger(long entity) => Post($"stagger {entity}");

	/// <summary>
	/// Plays an audio cue on an entity through the game's own cue player (refii_mod_play_sound_on,
	/// the body of the scripts' Sound.PlayEvent): the entity's sound component's emitter, or its
	/// position when it has none. <paramref name="tag"/> is what stop_sound matches. The cue's own
	/// volume applies; cue names are the audio banks' event names (docs/modding/gameplay.md, Sound).
	/// </summary>
	public void play_sound(string cue, long entity, string tag = "") =>
		Post($"play_sound {Word(cue)} {entity} {TagWord(tag)}");

	/// <summary>Plays an audio cue at a world point on an emitter of the API's own (refii_mod_play_sound_at; eight kept, the oldest freed).</summary>
	public void play_sound_at(string cue, float x, float y, float z, string tag = "") =>
		Post($"play_sound_at {Word(cue)} {Num(x)} {Num(y)} {Num(z)} {TagWord(tag)}");

	/// <summary>Stops the voices under a tag on an entity's emitter, or with entity 0 on the point emitters, fading over milliseconds (refii_mod_stop_sound).</summary>
	public void stop_sound(long entity, string tag, int fade_ms = 0) =>
		Post($"stop_sound {entity} {TagWord(tag)} {fade_ms}");

	/// <summary>Starts a music cue through SoundTools.PlayMusic's own body (refii_mod_play_music): the MUSIC_* cues the quests start.</summary>
	public void play_music(string name) => Post($"play_music {Word(name)}");

	/// <summary>Stops a music cue with the game's 2 s fade (refii_mod_stop_music).</summary>
	public void stop_music(string name) => Post($"stop_music {Word(name)}");

	/// <summary>
	/// Plays a named animation on an entity as the shipped AI does (refii_mod_play_animation):
	/// Action.SetCurrentAction with a PLAY_ANIMATION action — Anim the GDB animation record's name,
	/// SpeedMultiplier, BlendInTime — wrapped in a BATCH of <paramref name="loops"/> above 1. The
	/// game's evaluator plays it and the pose feed shows it; a higher-priority action keeps the body.
	/// </summary>
	public void play_animation(long entity, string name, int loops = 1, float blend_seconds = 0.1f, float speed = 1f) =>
		Post($"play_animation {entity} {Word(name)} {loops} {Num(blend_seconds)} {Num(speed)}");

	/// <summary>Ends an entity's scripted actions, the played animation among them (refii_mod_stop_animation: Action.FinishScriptedActions).</summary>
	public void stop_animation(long entity) => Post($"stop_animation {entity}");

	/// <summary>
	/// Moves a villager's opinion of a hero on an axis (0 love, 1 fear, 2 attractiveness, 3 morality,
	/// 4 respect) by delta through the game's own ModifyAxisValue, clamped to [-1, 1]
	/// (refii_mod_modify_opinion); player two's lands in their own opinion block; opinion_modified fires.
	/// </summary>
	public void modify_opinion(long villager, long hero, int axis, float delta) =>
		Post($"modify_opinion {villager} {hero} {axis} {Num(delta)}");

	/// <summary>
	/// Tells a villager the hero did a deed (refii_mod_add_deed): the deed message the game's own
	/// debug post sends, from the hero to the villager at the hero's position; the Deed record
	/// (<paramref name="deed"/> its GDB hash, a "0x…" string or number) decides the strength and
	/// kind, the villager's opinion component takes it, opinion_deed fires.
	/// </summary>
	public void add_deed(long villager, long hero, Variant deed) => Post($"add_deed {villager} {hero} {Arg(deed)}");

	/// <summary>
	/// An NPC follows a target as a quest's Follow.FollowEntity orders it (refii_mod_npc_follow):
	/// the follow component's own entry, radius the quests' 2 by default; BehaviourFollow walks it.
	/// </summary>
	public void npc_follow(long entity, long target, float radius = 2f) => Post($"npc_follow {entity} {target} {Num(radius)}");

	/// <summary>An NPC walks to a point as a quest's ScriptFunction.StartScriptControlledMovement orders it (refii_mod_npc_go_to); not waited for.</summary>
	public void npc_go_to(long entity, float x, float y, float z) => Post($"npc_go_to {entity} {Num(x)} {Num(y)} {Num(z)}");

	/// <summary>An NPC runs from an entity as the AI's BehaviourBase:FleeFromEntityNoWait does (refii_mod_npc_flee: Navigation.FleeFromEntity at sprint); one order, the AI may retake the body.</summary>
	public void npc_flee(long entity, long from) => Post($"npc_flee {entity} {from}");

	/// <summary>An NPC attacks a target as the quests order it (refii_mod_npc_attack: CombatRegister:SetScriptTarget — the enemy relationship and the combat info's ScriptTarget).</summary>
	public void npc_attack(long entity, long target) => Post($"npc_attack {entity} {target}");

	/// <summary>
	/// An NPC holds another entity — a hero (player two's is its own) or anything — as an enemy, or no
	/// longer (refii_mod_npc_set_hostile: Faction.AddTemporaryEntityRelationship FACTION_STATUS_ENEMY /
	/// RemoveTemporaryEntityRelationship, the faction component's own lists).
	/// </summary>
	public void npc_set_hostile(long entity, long other, bool on) => Post($"npc_set_hostile {entity} {other} {(on ? 1 : 0)}");

	/// <summary>Ends every order above (refii_mod_npc_stop: StopFollowing, StopScriptControlledMovement, ClearScriptTarget); the AI mind picks its next behaviour.</summary>
	public void npc_stop(long entity) => Post($"npc_stop {entity}");

	/// <summary>
	/// Sets the game clock to hours 0..24 (refii_mod_set_time_of_day: Timing.SetTimeOfDayAbsolute's
	/// body on the time manager, clamped, told to an online partner); <c>query time_of_day</c> reads it back.
	/// </summary>
	public void set_time_of_day(float hours) => Post($"set_time_of_day {Num(hours)}");

	/// <summary>
	/// Blends the GAME's environment theme — its weather and sky — as the quests do
	/// (refii_mod_set_weather: EnvironmentTheme.BlendToEnvironmentTheme(theme, weight, seconds); the
	/// weather_theme event fires). The Dynamic Sky's own weather override is the host's switch, not this.
	/// </summary>
	public void set_weather(string theme, float weight = 1f, float seconds = 1f) =>
		Post($"set_weather {Word(theme)} {Num(weight)} {Num(seconds)}");

	/// <summary>A name on a command line: no spaces (a space becomes '_'); empty is "-".</summary>
	private static string Word(string name) =>
		string.IsNullOrEmpty(name) ? "-" : name.Replace(' ', '_');

	private static string TagWord(string tag) => Word(tag);

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
	/// <c>name</c>, <c>level_name</c>, <c>hero</c>, <c>player_of</c>, <c>lua_int</c>, <c>time_of_day</c>, and the co-op
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

	/// <summary>
	/// The cutscene statements holding for player 1 or 2 (0: either player) as the bus last stated
	/// them, as bits: <see cref="ModApi.CutsceneFull"/> 1 (every player's), <see cref="ModApi.CutsceneInteractive"/>
	/// 2, <see cref="ModApi.CutsceneLookAt"/> 4 (the game gave that player's held LT a camera). One
	/// guest tick behind, like every signal; <c>query in_cutscene P</c> asks the guest now.
	/// </summary>
	public int cutscene_kinds(int player = 0) => _cutscene.Kinds(player);

	/// <summary>Whether any of <paramref name="kinds"/> (bits; all three by default) holds for player 1 or 2 (0: either): <see cref="cutscene_kinds"/>.</summary>
	public bool in_cutscene(int player = 0, int kinds = ModApi.CutsceneAll) => _cutscene.In(player, kinds);

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
	internal void RaiseModMenuRow(string menu, string row, int player, string mod, string command = "", string button = "")
	{
		var e = new Godot.Collections.Dictionary
		{
			["menu"] = menu,
			["row"] = row,
			["player"] = (long)player,
			["mod"] = mod,
			["command"] = command ?? string.Empty,
			["button"] = button ?? string.Empty,
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

	// ---- lights: Godot draws them, the classic list takes them (Engine_ModLights) ----------------

	/// <summary>
	/// Places a mod light over <paramref name="target"/> — an entity HANDLE (int), or a Vector3 /
	/// [x, y, z] world point in the guest's axes — in <paramref name="color"/> (Color or [r, g, b])
	/// at <paramref name="energy"/> over <paramref name="range"/> metres. <paramref name="options"/>:
	/// offset (Vector3 or [x, y, z], along the entity's axes), at (a model helper name: the light
	/// stands at that .mdl.gmd point, on a rigged body on its bone — as play_fx_on's at), flicker
	/// (0..1, a waver of the energy); and for a SPOT: direction (Vector3 or [x, y, z], the offset's
	/// axes — the entity's or the helper's, the world's for a point), cone (degrees from the axis to
	/// the cone's edge, above 0 makes it a spot; at most 89) and attenuation (the edge falloff; 0 or
	/// 1 linear, the title's own). Placed as play_fx_on places an effect, so it rides the body
	/// and goes with the entity. Returns the id <see cref="light_remove"/> takes; 0 for an unknown
	/// handle or target.
	/// </summary>
	public long light(Variant target, Variant color, double energy, double range, Godot.Collections.Dictionary? options = null)
	{
		uint handle = 0;
		var point = Vector3.Zero;
		switch (target.VariantType)
		{
			case Variant.Type.Int:
			case Variant.Type.Float:
			{
				double raw = target.AsDouble();
				if (raw is <= 0 or > uint.MaxValue)
				{
					return 0;
				}

				handle = (uint)raw;
				break;
			}

			case Variant.Type.Vector3:
				point = target.AsVector3();
				break;

			case Variant.Type.Array when target.AsGodotArray() is { Count: >= 3 } a:
				point = new Vector3((float)a[0].AsDouble(), (float)a[1].AsDouble(), (float)a[2].AsDouble());
				break;

			default:
				return 0;
		}

		Vector3 rgb = color.VariantType switch
		{
			Variant.Type.Color => new Vector3(color.AsColor().R, color.AsColor().G, color.AsColor().B),
			Variant.Type.Vector3 => color.AsVector3(),
			Variant.Type.Array when color.AsGodotArray() is { Count: >= 3 } c =>
				new Vector3((float)c[0].AsDouble(), (float)c[1].AsDouble(), (float)c[2].AsDouble()),
			_ => Vector3.One,
		};
		var offset = Vector3.Zero;
		var direction = Vector3.Zero;
		string at = string.Empty;
		float flicker = 0f, cone = 0f, attenuation = 0f;
		if (options is not null)
		{
			static Vector3 Triple(Variant v) => v.VariantType == Variant.Type.Vector3
				? v.AsVector3()
				: v.VariantType == Variant.Type.Array && v.AsGodotArray() is { Count: >= 3 } arr
					? new Vector3((float)arr[0].AsDouble(), (float)arr[1].AsDouble(), (float)arr[2].AsDouble())
					: Vector3.Zero;
			static float Number(Variant v) => v.VariantType is Variant.Type.Float or Variant.Type.Int ? (float)v.AsDouble() : 0f;

			if (options.TryGetValue("offset", out Variant o))
			{
				offset = Triple(o);
			}

			if (options.TryGetValue("at", out Variant helper) && helper.VariantType == Variant.Type.String)
			{
				at = helper.AsString();
			}

			if (options.TryGetValue("flicker", out Variant waver))
			{
				flicker = Number(waver);
			}

			if (options.TryGetValue("direction", out Variant d))
			{
				direction = Triple(d);
			}

			if (options.TryGetValue("cone", out Variant c))
			{
				cone = Number(c);
			}

			if (options.TryGetValue("attenuation", out Variant a))
			{
				attenuation = Number(a);
			}
		}

		return Refii.Lighting.Engine_ModLights.Place(
			handle, point, new Color(rgb.X, rgb.Y, rgb.Z), (float)energy, (float)range, offset, at, flicker, direction, cone, attenuation);
	}

	/// <summary>Removes a mod light by any surface's id (native, Lua or Godot); false when none stands under it.</summary>
	public bool light_remove(long id) => id > 0 && id <= uint.MaxValue && Refii.Lighting.Engine_ModLights.Remove((uint)id);

	/// <summary>
	/// Sends <paramref name="mod"/>'s message <paramref name="name"/> with <paramref name="payload"/>
	/// to THE OTHER MACHINE of an online session (refii_mod_send_to_partner, run on the guest tick
	/// through <c>partner_send</c>): it arrives there as the <c>partner_message</c> event — on every
	/// surface, Godot's signal included — once and in the order sent, with <c>from</c> the player this
	/// machine plays. <paramref name="name"/> and <paramref name="mod"/> are words (letters, digits,
	/// '_', '.', '-'; at most 32), the payload any text up to 256 bytes. False, and nothing sent,
	/// outside an online session (alone or in local co-op there is no partner machine), for a bad
	/// name, or a payload over the limit.
	/// </summary>
	public bool send_to_partner(string mod, string name, string payload = "")
	{
		if (_coopMode < 2 || string.IsNullOrEmpty(mod) || string.IsNullOrEmpty(name) ||
			mod.Length > 32 || name.Length > 32 || mod.Contains(' ') || name.Contains(' '))
		{
			return false;
		}

		byte[] bytes = Encoding.UTF8.GetBytes(payload ?? string.Empty);
		if (bytes.Length > 256)
		{
			return false;
		}

		return Post($"partner_send {mod} {name} {Convert.ToBase64String(bytes)}");
	}

	/// <summary>
	/// The mod lights standing: id, entity (handle, 0 for a point), r, g, b, energy, range, x, y, z
	/// (where it is now, guest axes), cone (0 for an omni light) and, for a spot, dx, dy, dz (the
	/// direction it shines along now, guest world axes).
	/// </summary>
	public Godot.Collections.Array<Godot.Collections.Dictionary> lights()
	{
		var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
		foreach ((uint id, uint entity, Color colour, float energy, float range, Vector3 where, float cone, Vector3 direction) in Refii.Lighting.Engine_ModLights.All())
		{
			list.Add(new Godot.Collections.Dictionary
			{
				["id"] = (long)id,
				["entity"] = (long)entity,
				["cone"] = cone,
				["dx"] = direction.X, ["dy"] = direction.Y, ["dz"] = direction.Z,
				["r"] = colour.R, ["g"] = colour.G, ["b"] = colour.B,
				["energy"] = energy,
				["range"] = range,
				["x"] = where.X, ["y"] = where.Y, ["z"] = where.Z,
			});
		}

		return list;
	}

	// ---- the HUD: markers, fades and meters (Engine_HudModMarkers, Engine_UiModFade, Engine_HudModMeters) ----

	/// <summary>
	/// Puts a marker on the HUD's world-icon channel over <paramref name="target"/> — an entity
	/// HANDLE (int), or a Vector3 / [x, y, z] world point in the guest's axes — for
	/// <paramref name="player"/> (1 or 2; 0, the default, every player this machine plays):
	/// <paramref name="icon"/> a game texture path ("art/gui/…/x.tex") or an enabled mod's PNG
	/// ("art/x.png"), <paramref name="text"/> a line under it. It rides the drawn body, is placed
	/// as the game's own icons are (each player's camera and half) and is sized by the game's
	/// distance curve. Returns the id <see cref="unmark"/> takes; 0 for no icon and no text, or an
	/// unknown handle. A marker on an entity goes with the entity.
	/// </summary>
	public long mark(Variant target, string icon, string text = "", long player = 0)
	{
		uint handle = 0;
		var point = Vector3.Zero;
		switch (target.VariantType)
		{
			case Variant.Type.Int:
			case Variant.Type.Float:
			{
				double raw = target.AsDouble();
				if (raw is <= 0 or > uint.MaxValue)
				{
					return 0;
				}

				handle = (uint)raw;
				break;
			}

			case Variant.Type.Vector3:
				point = target.AsVector3();
				break;

			case Variant.Type.Array when target.AsGodotArray() is { Count: >= 3 } a:
				point = new Vector3((float)a[0].AsDouble(), (float)a[1].AsDouble(), (float)a[2].AsDouble());
				break;

			default:
				return 0;
		}

		return Refii.Ui.Engine_HudModMarkers.Add((int)Math.Clamp(player, 0, 2), handle, point, icon ?? string.Empty, text ?? string.Empty);
	}

	/// <summary>Removes a marker by any surface's id; false when none stands under it.</summary>
	public bool unmark(long id) => id > 0 && id <= uint.MaxValue && Refii.Ui.Engine_HudModMarkers.Remove((uint)id);

	/// <summary>The markers standing: id, player, entity (handle, 0 for a point), x, y, z, icon, text.</summary>
	public Godot.Collections.Array<Godot.Collections.Dictionary> markers()
	{
		var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
		foreach (Refii.Ui.Engine_HudModMarkers.Marker m in Refii.Ui.Engine_HudModMarkers.All)
		{
			list.Add(new Godot.Collections.Dictionary
			{
				["id"] = (long)m.Id,
				["player"] = (long)m.Player,
				["entity"] = (long)m.Handle,
				["x"] = m.Point.X, ["y"] = m.Point.Y, ["z"] = m.Point.Z,
				["icon"] = m.Icon,
				["text"] = m.Text,
			});
		}

		return list;
	}

	/// <summary>
	/// Fades <paramref name="player"/>'s screen (1 or 2; 0 every player this machine plays) to
	/// black (<paramref name="to_black"/> true) or back over <paramref name="seconds"/> (0: at
	/// once): a host-drawn layer over that player's half (their whole window alone or online),
	/// independent of the game's own fade, which level changes own. False for a player not played here.
	/// </summary>
	public bool fade(long player, bool to_black, double seconds = 0)
	{
		int who = (int)Math.Clamp(player, 0, 2);
		return Refii.Ui.Engine_UiModBoxes.PlayedHere(who) &&
			Refii.Ui.Engine_UiModFade.Request(who, to_black, double.IsFinite(seconds) ? (float)seconds : 0f);
	}

	/// <summary>A player's mod fade as it stands: alpha (0 clear .. 1 black), target, seconds of the last request.</summary>
	public Godot.Collections.Dictionary fade_state(long player)
	{
		(float alpha, float target, float seconds) = Refii.Ui.Engine_UiModFade.Describe((int)Math.Clamp(player, 0, 2));
		return new Godot.Collections.Dictionary { ["alpha"] = alpha, ["target"] = target, ["seconds"] = seconds };
	}

	/// <summary>
	/// Shows or restates the HUD meter <paramref name="id"/> for <paramref name="player"/> (1 or
	/// 2; 0, the default, every player this machine plays): <paramref name="label"/> above a bar at
	/// <paramref name="value"/> of <paramref name="max"/>, in <paramref name="style"/> "bar" (the
	/// default), "boss" (wide, red, centred) or "timer" (narrow). Meters stack under the top of the
	/// player's screen in the order first shown; the same id restates it in place. False for no id.
	/// </summary>
	public bool meter(string id, string label, double value, double max, long player = 0, string style = "") =>
		Refii.Ui.Engine_HudModMeters.Set(id, (int)Math.Clamp(player, 0, 2), label ?? string.Empty, (float)value, (float)max, style ?? string.Empty);

	/// <summary>Removes the meter from every player's HUD; false when none stands under the id.</summary>
	public bool meter_remove(string id) => Refii.Ui.Engine_HudModMeters.Remove(id ?? string.Empty);

	// ---- input claims and HUD overrides: the pad publish and the HUD feed read them (ModInput, Engine_UiHudOverrides) ----

	/// <summary>
	/// Claims (or releases) a mod's input action — its scoped name <c>&lt;mod&gt;.&lt;action&gt;</c> from
	/// <c>input.json</c> — for <paramref name="player"/> (1 or 2; 0 both): while claimed, the action's
	/// pad buttons, triggers and keys are withheld from the pad the guest sees for that player, and a
	/// chord's modifier with them while the chord is held; the action's own <c>input_action</c> events
	/// still fire. Cleared when the mods' Lua reloads and when the game goes down. False for an
	/// action no enabled mod declares.
	/// </summary>
	public bool claim_input(long player, string action, bool on = true) =>
		player is >= 0 and <= 2 && ModInput.Claim((int)player, action ?? string.Empty, on);

	/// <summary>Whether <paramref name="action"/> is claimed for <paramref name="player"/> (1 or 2).</summary>
	public bool input_claimed(long player, string action) =>
		player is 1 or 2 && ModInput.Claimed((int)player, action ?? string.Empty);

	/// <summary>The actions claimed for <paramref name="player"/> (1 or 2), in declaration order.</summary>
	public Godot.Collections.Array<string> input_claims(long player)
	{
		var list = new Godot.Collections.Array<string>();
		if (player is 1 or 2)
		{
			foreach (string name in ModInput.Claims((int)player))
			{
				list.Add(name);
			}
		}

		return list;
	}

	/// <summary>
	/// States the texture a HUD element shows for <paramref name="player"/> (1 or 2; 0 both):
	/// <paramref name="element"/> a slide-toggle binding name (<c>DPadUp</c>) or an element path by its
	/// trailing segments; <paramref name="texture"/> a game texture path or an enabled mod's PNG under
	/// <c>art/</c>, fitted to the slot when it is not 128 × 128; "" clears. False for no element.
	/// </summary>
	public bool hud_icon(long player, string element, string texture) =>
		player is >= 0 and <= 2 && Refii.Ui.Engine_UiHudOverrides.SetIcon((int)player, element ?? string.Empty, texture ?? string.Empty);

	/// <summary>States the slide a HUD element sits on, as the game counts them (1 the first); 0 clears. False for no element.</summary>
	public bool hud_slide(long player, string element, long slide) =>
		player is >= 0 and <= 2 && slide is >= 0 and <= 65535 &&
		Refii.Ui.Engine_UiHudOverrides.SetSlide((int)player, element ?? string.Empty, (int)slide);

	/// <summary>Holds a HUD piece visible (its owner chain kept on its shown slides) for a player, or lets it go. False for no name.</summary>
	public bool hud_hold(long player, string piece, bool on = true) =>
		player is >= 0 and <= 2 && Refii.Ui.Engine_UiHudOverrides.SetHold((int)player, piece ?? string.Empty, on);

	/// <summary>The overrides standing for <paramref name="player"/>: <c>icons</c> (element → texture), <c>slides</c> (element → slide), <c>holds</c> (pieces).</summary>
	public Godot.Collections.Dictionary hud_overrides(long player)
	{
		var icons = new Godot.Collections.Dictionary();
		var slides = new Godot.Collections.Dictionary();
		var holds = new Godot.Collections.Array<string>();
		if (player is 1 or 2)
		{
			foreach ((string element, string texture) in Refii.Ui.Engine_UiHudOverrides.IconsFor((int)player))
			{
				icons[element] = texture;
			}

			foreach ((string element, int slide) in Refii.Ui.Engine_UiHudOverrides.SlidesFor((int)player))
			{
				slides[element] = (long)slide;
			}

			foreach (string piece in Refii.Ui.Engine_UiHudOverrides.HoldsFor((int)player))
			{
				holds.Add(piece);
			}
		}

		return new Godot.Collections.Dictionary { ["icons"] = icons, ["slides"] = slides, ["holds"] = holds };
	}

	/// <summary>The meters standing, in stack order: id, player, label, value, max, style.</summary>
	public Godot.Collections.Array<Godot.Collections.Dictionary> meters()
	{
		var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
		foreach (Refii.Ui.Engine_HudModMeters.Meter m in Refii.Ui.Engine_HudModMeters.All())
		{
			list.Add(new Godot.Collections.Dictionary
			{
				["id"] = m.Id,
				["player"] = (long)m.Player,
				["label"] = m.Label,
				["value"] = m.Value,
				["max"] = m.Max,
				["style"] = m.Style,
			});
		}

		return list;
	}

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
			Number("intensity", 1f), loop, Triple("offset", Vector3.Zero),
			options.TryGetValue("at", out Variant helper) && helper.VariantType == Variant.Type.String ? helper.AsString() : string.Empty);
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
