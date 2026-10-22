using System;
using System.Collections.Generic;
using System.Linq;

namespace Refii.Mods;

/// <summary>One field of a mod event, as the native struct and the Lua table both carry it.</summary>
public readonly record struct ModApiField(string Name, string Type, string Meaning, bool Writable = false)
{
	/// <summary>
	/// A field only a native handler receives: an object of the seam's own (a hit record, a
	/// component, an action) that Lua and Godot have no use for and are not given.
	/// </summary>
	public bool NativeOnly => Meaning.Contains("(native", StringComparison.Ordinal);
}

/// <summary>
/// One event of the mod API: its name in C++ (<c>&lt;Name&gt;Event</c>, <c>REFII_MOD_ON(Name, fn)</c>)
/// and in Lua (<c>Refii.on("lua_name", fn)</c>), the numeric kind the bus carries, the game
/// seam it is raised from, its fields, what cancelling means (empty when it cannot be), and — for
/// a RELAYED event — who decides it and what the other co-op machine receives (empty otherwise).
/// </summary>
public sealed record ModApiEvent(
	string Name,
	string LuaName,
	int Kind,
	string Seam,
	string Summary,
	string Cancel,
	IReadOnlyList<ModApiField> Fields,
	string Relayed = "")
{
	/// <summary>
	/// The event is relayed: decided on one co-op machine and dispatched on the other as a replica
	/// (mod_events.cpp's relay table, <c>kRelayed</c>).
	/// </summary>
	public bool IsRelayed => Relayed.Length != 0;

	/// <summary>The config names of the guest functions behind <see cref="Seam"/>, if any.</summary>
	public IEnumerable<string> SeamFunctions =>
		Seam.Split(new[] { ' ', ',', '/', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
			.Where(token => token.StartsWith("rex_", StringComparison.Ordinal));

	public bool Cancellable => Cancel.Length != 0;
}

/// <summary>One helper call of the native surface.</summary>
public readonly record struct ModApiHelper(string Signature, string Meaning);

/// <summary>
/// THE MOD API CATALOG: the one description of what <c>core/refii_mod_events.h</c> and
/// <c>core/refii_mod.h</c> offer, read by the Modding Guide, by <c>RefiiModApi</c> (the editor's
/// scripting surface), by the Lua runtime preamble (the event names), and by a test that holds
/// it to the header. A new event is added in the header and here, in the same change.
/// </summary>
public static class ModApi
{
	/// <summary><c>REFII_MOD_API_VERSION</c>.</summary>
	public const int Version = 2;

	/// <summary>
	/// THE SUBSCRIPTION MASKS' CAPACITY, per surface; <see cref="Events"/>.Count stays at or under
	/// each (ModApiTests holds these to the payload's constants and to REFII_EVENT_COUNT). Lua: the
	/// game Lua's number is a 32-bit float, exact to 24 bits, so a mask crosses the seam as groups of
	/// <see cref="LuaMaskBits"/> bits — never more than 20 significant bits in one number —
	/// <see cref="LuaMaskGroups"/> groups (mod_events.cpp kLuaMaskBits / kLuaMaskGroups, the
	/// preamble's <c>Refii.on</c>). Host (RefiiHost's event and verdict masks): <see cref="HostMaskWords"/>
	/// words of 64 (payload_bridge.h REFII_MOD_HOST_MASK_WORDS; <see cref="ModEventMask"/>).
	/// </summary>
	public const int LuaMaskBits = 20;
	public const int LuaMaskGroups = 10;
	public const int LuaMaskCapacity = LuaMaskBits * LuaMaskGroups;
	public const int HostMaskWords = 4;
	public const int HostMaskCapacity = 64 * HostMaskWords;

	/// <summary>
	/// THE RULE THE WHOLE SURFACE KEEPS, shown at the top of the Guide's API page: the API is the
	/// shared layer the base game's own co-op fixes and the mods both stand on, Forge/Fabric style.
	/// </summary>
	public const string PlayerAgnostic =
		"EVERY API SERVES BOTH PLAYERS; NEVER ASSUME PLAYER ONE. Events carry the player they are about (1 or 2), every helper takes a player or an entity, " +
		"and the \"which hero\" questions have one answer each, the one the base game's own co-op fixes use: player_of (is it a hero), player_for (who \"the hero\" is for an NPC or a dog), " +
		"acting_player / run_as_player (the hero a script stands for), combat_state(p), player_here(p) (where a hero's own effects are made) and world_here (where the world's are). " +
		"An event decided on one machine reaches the other when its effect belongs there (RELAYED: player two's swings reach the host's AI; the host's hits reach the joiner's looks), so a mod needs no co-op channel of its own. " +
		"What is shown follows the player it is for: a box, question, tutorial, fade, camera or Player.* call made while acting as player p appears on p's screen and acts on p's hero (online on p's machine, the answer coming back; split screen in p's half) — show_to_player(p, ...) asks for it outright.";

	/// <summary>The note a player-decided relayed event carries.</summary>
	private const string RelayedFromPlayer =
		"RELAYED: decided where the hero is played; online, the other machine receives a copy as a replica (refii_mod_event_replica / Refii.event_replica(); Godot's e.replica) with the actor as its own copy of that hero and the deciding machine's objects 0 — the host's AI sees player two's, the joiner sees player one's";

	/// <summary>Player and entity helpers every co-op rule is written with.</summary>
	private const string PlayerForMeaning =
		"the player an entity deals with, 1 or 2 (0 only with no hero): a hero its own, a dog its master's, anything else the nearer living hero with 3 m of hysteresis";

	private static ModApiField F(string name, string type, string meaning, bool writable = false) =>
		new(name, type, meaning, writable);

	/// <summary>
	/// What an entity or object field holds on each side: natively the guest address; in Lua and
	/// in a Godot script a HANDLE (<c>refii_mod_handle</c>), a small integer for that object this
	/// session, because the game's Lua number is a 32-bit float. Hashes (definitions, text) reach
	/// Lua and Godot as "0x…" strings for the same reason.
	/// </summary>
	public const string HandleNote = "an entity: natively its address, in Lua and Godot its handle";

	private static readonly ModApiField[] Common =
	{
		F("ctx", "PPCContext*", "the guest context, a copy: legal for guest calls (native only)"),
		F("base", "uint8_t*", "guest memory (native only)"),
		F("cancel", "bool", "set to refuse, where the event allows it; a Lua handler returns true", true),
	};

	private static readonly ModApiField[] Hit =
	{
		F("victim", "uint32_t", "the entity taking the hit"),
		F("victim_player", "int32_t", "1 or 2 when the victim is that player's hero, else 0"),
		F("attacker", "uint32_t", "the entity that dealt it, or 0"),
		F("attacker_player", "int32_t", "1 or 2 when the attacker is a player's hero, else 0"),
		F("hit", "uint32_t", "the hit record (native: +0x38 damage, +0x44 serial, +0x48 level, +0x54 spell type)"),
		F("damage", "float", "the damage the hit carries", true),
		F("spell_type", "int32_t", "-1 for a non-spell hit; 0 Lightning 1 Fireball 2 Slow Time 3 Blades 4 Vortex 5 Chaos 6 Force Push 7 Raise Dead"),
		F("spell_level", "int32_t", "the cast's power 0..4"),
		F("spell_serial", "uint32_t", "the cast's number: every hit of one cast carries the same"),
		F("knockdown", "bool", "the hit asks for a knockdown — force push and heavy blows set it (writable before delivery)", true),
	};

	/// <summary>The same fields after delivery: nothing is written back any more.</summary>
	private static readonly ModApiField[] HitAfter =
		Hit.Select(f => f with { Writable = false, Meaning = f.Name == "damage" ? "the damage the hit carried in" : f.Name == "knockdown" ? "the hit asked for a knockdown" : f.Meaning }).ToArray();

	/// <summary>Every event, in kind order. The kinds are <c>refii_mod_event_kind</c>'s.</summary>
	public static readonly IReadOnlyList<ModApiEvent> Events = new[]
	{
		new ModApiEvent("Tick", "tick", 0, "the guest tick (RunModTicks), after the game's own tick work",
			"Once per game step, menus included, on every surface at once: Lua handlers run every guest tick with that tick's seconds, so a Lua mod's delays and cooldowns resolve on the same grain as native ones (one 119-byte chunk a tick: 2.7 us load + run on the host's own Lua 5.1.2, the handlers' work on top).",
			string.Empty,
			new[] { F("seconds", "float", "real seconds since the last tick, capped at 0.25 across a hitch") }),
		new ModApiEvent("Hero", "hero_ready", 1, "derived in the tick from the player manager's handles (refii_mod_player_hero)",
			"A player's hero now exists: a session or save began, or a co-op partner joined. Lua also learns it through Refii.hero(player).",
			string.Empty,
			new[] { F("player", "int32_t", "1 or 2"), F("hero", "uint32_t", "the hero entity") }),
		new ModApiEvent("HeroGone", "hero_gone", 2, "derived in the tick, as HeroReady",
			"A player's hero stopped existing (a session ended, a partner left).",
			string.Empty,
			new[] { F("player", "int32_t", "1 or 2"), F("hero", "uint32_t", "the entity that was the hero") }),
		new ModApiEvent("Lua", "lua_ready", 3, "rex_lhLuaScriptSystem_LoadStartupScripts, after every mod's init.lua ran",
			"The game's Lua state was rebuilt for a session and the mods' init chunks have run.",
			string.Empty,
			new[] { F("generation", "uint32_t", "refii_mod_lua_generation(): steps on every rebuild and save load") }),
		new ModApiEvent("SaveLoaded", "save_loaded", 4, "rex_lhLuaScriptSystem_LoadScriptSave",
			"A save's Lua table was loaded into the VM: RefiiModSave now holds the save's values. A mod keeping state there re-reads it here.",
			string.Empty,
			new[] { F("generation", "uint32_t", "refii_mod_lua_generation()") }),
		new ModApiEvent("GameAction", "game_action", 5, "rex_CPlayerActionReceiver_ProcessAction (r3 receiver, r4 message; entity *(*(r3+0xC)))",
			"A GameAction reached a player hero's receiver: a button's press or release as the control scheme maps it (49 lock-on set, 50 lock-on release, 11 interact, ...; the GDB's GameAction rows).",
			"the game never sees the action",
			new[]
			{
				F("hero", "uint32_t", "the hero whose receiver got it"),
				F("player", "int32_t", "1 or 2"),
				F("action", "uint32_t", "the GameAction id"),
				F("message", "uint32_t", "the 24-byte action message (native)"),
			}),
		new ModApiEvent("AbilityRequest", "ability_request", 6, "rex_CECAbilities_PerformRequest (r3 entity, r4 request; type = request vtable slot 1)",
			"A creature asked its ability set for an ability: 16 melee pressed, 17 melee released, 18 magic charge, and the rest of the AbilityType enum.",
			"the request matches nothing; a refused melee press should refuse its release too",
			new[]
			{
				F("entity", "uint32_t", "the creature"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("request", "uint32_t", "the request object (native)"),
				F("type", "uint32_t", "the AbilityType"),
			}),
		new ModApiEvent("MeleeSwing", "melee_swing", 7, "rex_CAbilityActionMelee_CommitSwing (r3 ability, r4 entity), after the swing's animation started",
			"A strike's swing is committed. Not refusable here: refuse the AbilityRequest instead.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "the striker"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("ability", "uint32_t", "the strike ability (native; 0 in a relayed copy)"),
				F("action", "uint32_t", "the live strike action the action manager (component 49) now runs, or 0 (Lua and Godot: a handle, what current_action answers; 0 in a relayed copy)"),
				F("action_type", "int32_t", "the action's EScriptableAction (action_type), -1 unknown"),
				F("action_name", "const char* / string", "its enum name (action_name), \"\" unknown"),
			},
			RelayedFromPlayer),
		new ModApiEvent("Roll", "roll_can_start", 8, "rex_CAbilityActionStrafeRoll_CanStart (r4 entity)",
			"The dodge roll asks whether it may start; asked once per matching roll record, so it gates without spending.",
			"the roll does not start",
			new[] { F("entity", "uint32_t", "the roller"), F("player", "int32_t", "1 or 2 for a hero, else 0") }),
		new ModApiEvent("RollStarted", "roll_started", 9, "rex_CAbilityActionStrafeRoll_Start (r4 entity), when it returned 1",
			"A roll was queued. Rolls are invulnerable for their whole length in the game already.",
			string.Empty,
			new[] { F("entity", "uint32_t", "the roller"), F("player", "int32_t", "1 or 2 for a hero, else 0") },
			RelayedFromPlayer),
		new ModApiEvent("Block", "block", 10, "rex_CActionBlock_OnHitBlockPose / rex_CActionBlock_OnHitBlockedResponse (r3 listener at action+0x54, r4 hit)",
			"A block is taking a hit. Fired once per hit although the game runs two block listeners (the second takes the first's verdict).",
			"the guard BREAKS: the hit lands in full, as if there were no block (never for an unblockable hit)",
			new[]
			{
				F("blocker", "uint32_t", "the blocking entity"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("hit", "uint32_t", "the hit record (native)"),
				F("damage", "float", "the hit's damage"),
				F("unblockable", "bool", "the hit bypasses blocks in the game already"),
			}),
		new ModApiEvent("AnimEvent", "anim_event", 11, "rex_CAction_OnAnimEvent (r3 live action, r6 value object; text **r6)",
			"An animation event reached an action; ALLOW_ACTION_INTERRUPT is the one that lets the next action in. Actions whose class overrides the handler without calling the base (about 18 of 180) are not seen.",
			"the action never sees the event",
			new[]
			{
				F("action", "uint32_t", "the live action (Lua and Godot: a handle, what current_action answers — match a committed strike on it)"),
				F("action_type", "int32_t", "the action's EScriptableAction (action_type), -1 unknown"),
				F("action_name", "const char* / string", "its enum name (action_name), \"\" unknown"),
				F("name", "const char* / string", "the event's text"),
				F("entity", "uint32_t", "the action's owner, as the action states it (any creature)"),
				F("player", "int32_t", "1 or 2 when the owner is a player hero, else 0"),
			}),
		new ModApiEvent("Hit", "hit", 12, "rex_CECHitReceiver_OnHit (r3 victim's component 31, owner at +4; r4 hit), before delivery",
			"A hit is arriving at its victim: melee, spell, physics, the Blades' stabs. The one receiver every hit reaches.",
			"the hit is dropped entirely: no damage, no response",
			Hit),
		new ModApiEvent("HitLanded", "hit_landed", 13, "rex_CECHitReceiver_OnHit, after delivery",
			"A hit was delivered, with the damage it carried in. Read-only.",
			string.Empty,
			HitAfter,
			"RELAYED: decided where the world runs (the host); online, a hit with a hero on either side reaches the joiner as a replica (refii_mod_event_replica / Refii.event_replica(); Godot's e.replica) with the heroes as the joiner's copies, other entities by UniqueID, hit 0 and spell_serial the host's number — follow it for looks, never act on the world from it"),
		new ModApiEvent("HitResponse", "hit_response", 14, "rex_CECHitResponsePlayAction_OnHit (r3 listener, entity at r3-8; r4 hit; r5 result word), priority 20 on the dispatch",
			"The victim is about to flinch or be knocked down by a hit. The game's own early-outs (a blocked or parried hit, a ranged hit, a knockdown request, a killing blow) come first, so a lethal hit's fall always plays. A second response the listener can play (its +28 branch) is cancelled with it; a flinch played by a knockdown-flagged hit without the listener's response raises nothing.",
			"the hit is absorbed: no animation, no state left behind",
			new[]
			{
				F("victim", "uint32_t", "the entity"),
				F("victim_player", "int32_t", "1 or 2 for a hero, else 0"),
				F("hit", "uint32_t", "the hit record (native)"),
				F("damage", "float", "the game's own arithmetic for the hit (rex_CECHealth_CalculateDamage)"),
				F("health", "float", "the victim's current health"),
				F("max_health", "float", "the victim's maximum health"),
			}),
		new ModApiEvent("SpellCharge", "spell_charge", 15, "rex_CAbilityActionMagicCharge_CanStart (r4 entity)",
			"A hero pressed B to charge a spell. Also asked to continue a charge already held.",
			"the charge does not start",
			new[] { F("entity", "uint32_t", "the caster"), F("player", "int32_t", "1 or 2 for a hero, else 0") }),
		new ModApiEvent("SpellChargeLevel", "spell_charge_level", 16, "rex_CECSpellManager_ChargeLevelFromTime (r3 spell manager; r6 int* charged-slot count, r7 int* first slot), after it",
			"The charge time became the top slot to cast (first + count - 1); the release's cascade launches slots 0..level, each at its own power. Raised every frame of a held charge, to every surface alike: a cap written here is re-evaluated each frame (a pool refilling mid-hold raises it), and a repeat charge to the same slot is reported again.",
			string.Empty,
			new[]
			{
				F("caster", "uint32_t", "the caster"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("level", "int32_t", "the top slot 0..4; the cascade launches 0..level (native: writable, clamped)", true),
			}),
		new ModApiEvent("SpellLaunch", "spell_launch", 17, "rex_CECSpellManager_LaunchSlot (r3 manager = component 56, caster at +4; r4 targeted; r5 slot; slot table +0x80)",
			"One slot is about to launch. The type can be changed for this launch only; the slot keeps its assignment.",
			"the launch is skipped",
			new[]
			{
				F("caster", "uint32_t", "the caster"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("manager", "uint32_t", "the spell manager component (native)"),
				F("targeted", "bool", "a targeted cast (else area)"),
				F("slot", "int32_t", "the slot 0..4, also the power level"),
				F("type", "int32_t", "the spell type the slot holds; -1 empty (native: writable for this launch)", true),
				F("cascading", "bool", "the release cascade is running through the lower slots"),
			}),
		new ModApiEvent("SpellLaunched", "spell_launched", 18, "rex_CECSpellManager_OnSpellLaunched (r3 manager, r4 slot), behind the launch's own success test",
			"A spell was really created for a launch.",
			string.Empty,
			new[]
			{
				F("caster", "uint32_t", "the caster"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("manager", "uint32_t", "the spell manager component (native)"),
				F("slot", "int32_t", "the slot launched"),
			}),
		new ModApiEvent("SpellCreated", "spell_created", 19, "rex_CSpellRegistry_Create (r3 registry, r4 type, r5 targeted, r6 target, r7 level, r8 caster), after it",
			"The spell factory made a spell object: every cast, the player's and any creature's, and refii_mod_cast_spell's. Read-only.",
			string.Empty,
			new[]
			{
				F("spell", "uint32_t", "the spell object"),
				F("type", "int32_t", "0 Lightning .. 7 Raise Dead"),
				F("targeted", "bool", "a targeted cast"),
				F("target", "uint32_t", "the target entity, or 0"),
				F("level", "int32_t", "the power 0..4"),
				F("caster", "uint32_t", "the caster entity"),
				F("caster_player", "int32_t", "1 or 2 for a hero, else 0"),
				F("serial", "uint32_t", "the cast's number (the spell object's +0x44), carried by its hits"),
			}),
		new ModApiEvent("Experience", "experience", 20, "rex_CECExperience_Modify (r3 component 164, r4 pool, f1 amount) / rex_CECExperience_Spend (r3 component, r4 pool, f1 cost)",
			"Experience is changing on an entity: earned, refunded or spent. Pools: 0 General, 1 Strength, 2 Skill, 3 Will.",
			"the pools are left untouched",
			new[]
			{
				F("entity", "uint32_t", "the entity, when the component is its own experience component; else 0"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("component", "uint32_t", "the experience component (native)"),
				F("pool", "int32_t", "0 General, 1 Strength, 2 Skill, 3 Will"),
				F("amount", "float", "the change (signed) or the cost (native: writable)", true),
				F("spend", "bool", "true for a Spend, false for a Modify"),
				F("nested", "bool", "raised inside another experience change (a Spend's payments, the co-op share): scale only when false; cannot be cancelled"),
			}),
		new ModApiEvent("EntityDied", "entity_died", 21, "rex_CECCreature_Die (r3 creature component 20, owner at +4, alive byte +0x18; r4 killer) / rex_CECCreature_FinishDeath (r3 creature component)",
			"A creature is dying: every creature death goes through here, from damage, a script kill or a fall. Heroes come to HeroDowned instead.",
			"for a death by damage (health at zero) only: the creature stays alive with 1 health and the next hit asks again; other deaths cannot be refused",
			new[]
			{
				F("entity", "uint32_t", "the dying creature"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("killer", "uint32_t", "the killer entity, or 0"),
				F("killer_player", "int32_t", "1 or 2 when the killer is a hero, else 0"),
			}),
		new ModApiEvent("HeroDowned", "hero_downed", 22, "rex_CECHealth_OnHeroDeath (r3 health component 36, owner at +4; r4 killer), the hero branch of the zero-health dispatch",
			"A player's hero reached zero health: the knockdown, the scar, the resurrection potion and the revive flow follow.",
			"all of it is skipped and the hero keeps 1 health — a mod's own death and respawn starts here",
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2"),
				F("killer", "uint32_t", "the killer entity, or 0"),
				F("killer_player", "int32_t", "1 or 2 when the killer is a hero, else 0"),
			}),
		new ModApiEvent("Damage", "damage", 23, "rex_CECHealth_OnHit (r3 listener = health+0xC, entity at r3-8; r4 hit; r5 handled word), priority 30 on the dispatch",
			"The victim's health is about to take a hit's damage, after the responses and blocks ran. The damage is the game's final arithmetic; change it on Hit instead.",
			"no health is removed and no death follows; the flinch and sound already played",
			new[]
			{
				F("victim", "uint32_t", "the entity"),
				F("victim_player", "int32_t", "1 or 2 for a hero, else 0"),
				F("attacker", "uint32_t", "the attacker entity, or 0"),
				F("attacker_player", "int32_t", "1 or 2 when the attacker is a hero, else 0"),
				F("hit", "uint32_t", "the hit record (native)"),
				F("damage", "float", "the game's damage for the hit (rex_CECHealth_CalculateDamage)"),
				F("health", "float", "the victim's health before"),
				F("max_health", "float", "the victim's maximum health"),
				F("lethal", "bool", "the damage reaches the health"),
				F("spell_type", "int32_t", "-1 for a non-spell hit, else the spell"),
				F("spell_level", "int32_t", "the cast's power 0..4"),
				F("spell_serial", "uint32_t", "the cast's number"),
			}),
		new ModApiEvent("LevelLoaded", "level_loaded", 24, "rex_lhLuaScriptSystem_OnLevelLoad (r3 script-system owner, r4 from-save), after the game's AIManager:OnLevelLoad",
			"A level finished loading and the heroes stand in it: a new game, a fast travel, a save load. Not a door inside a region.",
			string.Empty,
			new[]
			{
				F("level", "const char* / string", "the level's name"),
				F("from_save", "bool", "the level came up from a save load"),
			}),
		new ModApiEvent("LevelUnloading", "level_unloading", 25, "rex_lhWorld_UnloadLevel (r3 world manager; +0x24 the level holder), before it",
			"The current level is about to be unloaded: the last moment its entities exist.",
			string.Empty,
			new[] { F("level", "const char* / string", "the level's name") }),
		new ModApiEvent("GameSaving", "game_saving", 26, "rex_lhSaveManager_WriteSave (r3 save manager, r4 file name), before it",
			"A save file is about to be written (the main save; a quest-fail save when one is made): put state into RefiiModSave now.",
			string.Empty,
			new[] { F("file", "const char* / string", "the save file's name") }),
		new ModApiEvent("ItemAdded", "item_added", 27, "rex_CECInventory_AddOneOfRecord (r3 out, r4 inventory component 13, owner at +4; r5 definition hash; r6 notify), after it",
			"An item was added to an entity's inventory by its definition: a pickup, a purchase, a gift, a reward, a script, the starting kit. After the add: several of its callers use the new item at once and cannot take a refusal.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "the inventory's owner"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("inventory", "uint32_t", "the inventory component (native)"),
				F("definition", "uint32_t", "the item definition's GDB record hash"),
				F("notify", "bool", "the game's item-added notifications run"),
			}),
		new ModApiEvent("ItemUsed", "item_used", 28, "rex_lhEntity_UseInventoryItem (r3 entity, r4 item shared pointer, r5 script override)",
			"An entity is using an inventory item: a potion, food, a book, a gift — anything with a carried-action script, which does the consuming.",
			"the use is refused",
			new[]
			{
				F("entity", "uint32_t", "the user"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("item", "uint32_t", "the item object (native; 0 in a relayed copy)"),
				F("definition", "uint32_t", "the item definition's GDB record hash"),
				F("count", "int32_t", "how many the stack holds"),
			},
			RelayedFromPlayer),
		new ModApiEvent("HeldItem", "held_item", 29, "rex_CECHeldItems_AttachItemToSlot (r3 component 24, owner at +4; r4 slot name; r5 item) / rex_CECHeldItems_ReleaseSlot (r3, r4)",
			"An item entity is attached to or released from one of an entity's holding slots: a weapon drawn (release from the holding slot, attach to the hand), sheathed, or picked up.",
			"a release is refused (the attach after it then sees nothing); an attach cannot be refused — its callers record the drawn weapon regardless",
			new[]
			{
				F("entity", "uint32_t", "the holder"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("slot", "const char* / string", "the slot's name"),
				F("item", "uint32_t", "the item entity on an attach; 0 on a release"),
				F("attached", "bool", "true for an attach, false for a release"),
			}),
		new ModApiEvent("WeaponEquipped", "weapon_equipped", 30, "rex_CECWeaponSet_RecordDrawnItem (r3 weapon set 28, owner at +4; r4 item entity), before it",
			"A weapon was equipped into an entity's weapon set: the melee or ranged record changes. At equip time, not on a draw. Read-only.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "the owner"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("item", "uint32_t", "the weapon's item entity"),
				F("definition", "uint32_t", "the weapon definition's GDB record hash"),
			}),
		new ModApiEvent("AbilityLevel", "ability_level", 31, "rex_CECStats_SetAbilityLevel (r3 stats 57, owner at +4; r4 type 1..14; r5 level; r6 force)",
			"An ability's level is being set on an entity's stats: a purchase or unlearn on the abilities book, a script, a save load.",
			"the level stays as it was (the book has already spent the experience)",
			new[]
			{
				F("entity", "uint32_t", "the entity"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("type", "int32_t", "the ability 1..14 (1 Heavy Weapons, 2 Physique, 3 Toughness, 4 Ranged Weapons, 5 Accuracy, 6 Speed, 7 Lightning .. 14 Raise Dead)"),
				F("level", "int32_t", "the level being set (native: writable)", true),
				F("old_level", "int32_t", "the level before"),
			}),
		new ModApiEvent("Gold", "gold", 32, "rex_CECMoney_Modify (r3 purse component 81, owner at +4, amount at +12; r4 delta; r5 no split; r6 co-op share; r7 passive)",
			"Gold is changing on an entity's purse: a purchase, a sale, a pickup, rent, a gift, a reward.",
			"the purse is left as it is",
			new[]
			{
				F("entity", "uint32_t", "the purse's owner"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("gold", "int32_t", "the purse before"),
				F("delta", "int32_t", "the signed change (writable)", true),
				F("passive", "bool", "income shown with the HUD's icon (rent)"),
				F("share", "bool", "the game's co-op split of a change already reported: scale only when false; cannot be cancelled"),
			}),
		new ModApiEvent("Relation", "relation", 33, "rex_CECFaction_RelationTo (r3 faction component 133, owner at +4; r4 the other entity), after it",
			"The game asked how an entity stands to another: its faction rule, asked by the AI, the targeting and the HUD many times a tick. The answer is writable: a handler makes two creatures friends or foes.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "the entity asking"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("other", "uint32_t", "the entity asked about"),
				F("other_player", "int32_t", "1 or 2 when the other is a hero, else 0"),
				F("relation", "int32_t", "1 neutral, 2 ally, 4 enemy (writable)", true),
			}),
		new ModApiEvent("InputAction", "input_action", 34, "the guest tick, from the host's input queue (a mod's input.json; RefiiHostPostModInput)",
			"A mod's own input action was pressed or released by a player: the keyboard or the first pad is player 1, the second pad player 2. Edges only. The player rebinds an action on Options › Controls › the mod's title (a key row and a pad row per action, where the declaration binds each), stored in refii.toml as key_<mod>.<action> / pad_<mod>.<action> over the file's default; none unbinds.",
			string.Empty,
			new[]
			{
				F("name", "const char* / string", "\"<mod>.<action>\" from the mod's input.json"),
				F("player", "int32_t", "1 or 2"),
				F("pressed", "bool", "true on the press, false on the release"),
			}),
		new ModApiEvent("QuestStarted", "quest_started", 35, "rex_CECQuestTracker_SetAsActive (r3 tracker component, owner at +4; r4 quest name; r5 flag; r6 make primary)",
			"A quest is being activated on a hero's quest tracker, by name (the GDB record's, the one the shipped scripts pass).",
			"the quest stays inactive",
			new[]
			{
				F("quest", "const char* / string", "the quest's name"),
				F("hero", "uint32_t", "the tracker's owner"),
				F("player", "int32_t", "1 or 2"),
				F("make_primary", "bool", "it becomes the tracked quest"),
			}),
		new ModApiEvent("QuestEnded", "quest_ended", 36, "rex_CECQuestTracker_OnQuestEnded (r3 tracker; r4 record: +0 name, +21 completed, +22 failed), after it",
			"A quest ended: completed (its rewards were just given) or failed. After the fact.",
			string.Empty,
			new[]
			{
				F("quest", "const char* / string", "the quest's name"),
				F("hero", "uint32_t", "the tracker's owner"),
				F("player", "int32_t", "1 or 2"),
				F("completed", "bool", "it was completed"),
				F("failed", "bool", "it was failed"),
			}),
		new ModApiEvent("QuestObjective", "quest_objective", 37, "rex_CECQuestTracker_SetObjectiveTagSilently (r3 tracker; r4 quest name; r5 tag)",
			"An objective is being added to a quest: the text tag the quest log shows. Behind SetObjectiveTag, SetObjectiveTagFinal and a quest's default objective.",
			"nothing is added",
			new[]
			{
				F("quest", "const char* / string", "the quest's name"),
				F("tag", "const char* / string", "the objective's text tag"),
				F("hero", "uint32_t", "the tracker's owner"),
				F("player", "int32_t", "1 or 2"),
			}),
		new ModApiEvent("Dialogue", "dialogue", 38, "rex_CECTalk_PlayLine (r3 talk component, owner at +4; r4 text hash; r5 force subtitle; r6 listener)",
			"A creature is about to speak a line: every line, the heroes' and the villagers', reaches here with its final text key (after the voice and variant suffixes).",
			"the line is not spoken: no audio, no subtitle, no speech animation",
			new[]
			{
				F("speaker", "uint32_t", "the speaking entity"),
				F("speaker_player", "int32_t", "1 or 2 when the speaker is a hero, else 0"),
				F("text", "uint32_t", "the line's text hash (the TextManager key; Lua and Godot get it as a \"0x…\" string)"),
				F("listener", "uint32_t", "the entity spoken to, or 0"),
				F("listener_player", "int32_t", "1 or 2 when the listener is a hero, else 0"),
				F("subtitle", "bool", "the subtitle is forced"),
			}),
		new ModApiEvent("EntitySpawned", "entity_spawned", 39, "rex_lhEntityManager_Add (r3 the manager, r4 the entity), raised on the next tick",
			"An entity joined the world: a script or generator created it, or a level or save loaded it. Raised on the first tick after the add, when its components exist; at most 64 a tick, so a level load spreads over a few ticks.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "the new entity"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("definition", "uint32_t", "its definition hash (Lua and Godot: a \"0x…\" string)"),
			}),
		new ModApiEvent("EntityRemoved", "entity_removed", 40, "rex_lhEntity_Destroy (r3 the entity), before it",
			"An entity is leaving the world (cleaned up after death, destroyed by a script, unloaded with its level): the last moment it is whole.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "the entity"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("definition", "uint32_t", "its definition hash"),
			}),
		new ModApiEvent("ExpressionStarted", "expression_started", 41, "rex_CECExpressionPerformer_StartExpression (r3 performer component, owner at +4; r4 the record pair)",
			"A creature starts an expression: the expression wheel, a script's PerformExpression, a trophy. Raised before the game's own can-perform check, which may still refuse it.",
			"the expression is refused",
			new[]
			{
				F("entity", "uint32_t", "the performer"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("expression", "uint32_t", "the expression record's hash"),
			}),
		new ModApiEvent("Renown", "renown", 42, "rex_lhHeroStats_ModifyRenown (r3 stats 57, owner at +4; r4 delta; r5 partner share)",
			"Renown is changing. The co-op partner's share is a second event with share set.",
			"renown is left as it is",
			new[]
			{
				F("entity", "uint32_t", "the entity"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("delta", "int32_t", "the change (writable)", true),
				F("share", "bool", "the game's co-op split of a change already reported: scale only when false; cannot be cancelled"),
			}),
		new ModApiEvent("Morality", "morality", 43, "rex_lhHeroStats_ModifyMorality (r3 stats, owner at +4; f1 delta)",
			"Morality (good and evil) is changing; the game clamps it afterwards. The game skips the change when the hero's stats are not live.",
			"morality is left as it is",
			new[]
			{
				F("entity", "uint32_t", "the entity"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("delta", "float", "the change, positive is good (writable)", true),
			}),
		new ModApiEvent("Purity", "purity", 44, "rex_lhHeroStats_ModifyPurity (r3 stats, owner at +4; f1 delta)",
			"Purity (pure and corrupt) is changing; the game clamps it afterwards. The game skips the change when the hero's stats are not live.",
			"purity is left as it is",
			new[]
			{
				F("entity", "uint32_t", "the entity"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("delta", "float", "the change, positive is pure (writable)", true),
			}),
		new ModApiEvent("Marriage", "marriage", 45, "rex_CECPlayerFamily_Marry (r3 the hero's family component, owner at +4; r4 spouse; r5 ceremony)",
			"A hero marries: the wedding ceremony or a script.",
			"the marriage is refused",
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2"),
				F("spouse", "uint32_t", "the spouse"),
			}),
		new ModApiEvent("DogTrick", "dog_trick", 46, "rex_CECDog_PerformCommand (r3 dog component, owner at +4; r4 the command record pair; r5 force)",
			"The dog is told a trick: an expression's completion, a gift, an item, a script. Raised before the dog's unlocked-trick check, which may still refuse it.",
			"the dog does not do it",
			new[]
			{
				F("dog", "uint32_t", "the dog"),
				F("trick", "uint32_t", "the command record's hash"),
				F("player", "int32_t", "whose dog: 1 or 2 by its master (Dog.GetMaster, the dog component's +300), else by the players' dog records (Refii.player_dog); 0 for neither"),
			}),
		new ModApiEvent("RangedShot", "ranged_shot", 47, "rex_CECFirearm_Fire (r3 firearm component, owner at +4; r4 target; r5 aimed)",
			"A ranged weapon is about to fire, the hero's or anyone's. The game may still refuse it (no ammunition, the hero busy).",
			"the shot is not fired",
			new[]
			{
				F("shooter", "uint32_t", "the creature holding the weapon"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("target", "uint32_t", "the entity aimed at, or 0"),
				F("aimed", "bool", "an aimed shot rather than a hip shot"),
				F("weapon", "uint32_t", "the weapon's item entity"),
			}),
		new ModApiEvent("StatusEffect", "status_effect", 48, "rex_lhEntity_AddComponent (after; the component's GetECType) / rex_lhEntity_RemoveComponent (r3 entity, r4 id) / rex_lhRagdoll_GoFloppy / rex_lhRagdoll_Recover (r3 entity) / rex_CECCreature_SetUnconscious (r3 comp 20, r4 down) / rex_HollowMan_SetCrumbled (r3 action, r4 crumbled)",
			"A status effect started or ended on an entity: 1 electrified, 2 chaos, 3 burning, 4 caught in a vortex, 5 knocked down (every ragdoll fall), 6 mauled by the dog, 7 unconscious, 8 a hollow man crumbled to bones.",
			string.Empty,
			new[]
			{
				F("entity", "uint32_t", "the entity"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("effect", "int32_t", "1 electrified, 2 chaos, 3 burning, 4 in a vortex, 5 knocked down, 6 mauled by the dog, 7 unconscious, 8 crumbled (hollow man)"),
				F("started", "bool", "true when it started, false when it ended"),
			}),
		new ModApiEvent("TargetChanged", "target_changed", 49, "rex_CECTargetingPlayer_SetTarget (r3 targeting 77, owner at +4; r4 target) / _ClearTarget (r3)",
			"A player's target changed, after the fact; 0 when it was cleared.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2"),
				F("target", "uint32_t", "the new target, or 0"),
			}),
		new ModApiEvent("OpinionDeed", "opinion_deed", 50, "rex_CECOpinionReaction_OnDeed (r3 opinion component, owner at +4; r4 message: +32 deed, +52 kind, +72 doer)",
			"A villager reacts to a deed, an expression or a gift it saw: the opinion change and the renown, morality and purity the deed earns follow. Raised before the villager's own in-world check.",
			"the villager ignores it",
			new[]
			{
				F("villager", "uint32_t", "the villager"),
				F("doer", "uint32_t", "who did it"),
				F("doer_player", "int32_t", "1 or 2 when the doer is a hero, else 0"),
				F("deed", "uint32_t", "the deed record's hash"),
				F("kind", "int32_t", "the message kind, 0..4"),
			}),
		new ModApiEvent("OpinionModified", "opinion_modified", 51, "rex_CECOpinionReaction_ModifyAxisValue (r3 component, owner at +4; r4 axis; f1 delta)",
			"A villager's opinion axis is changed directly, not through a deed (the villager's own reactions, scripts): 0 love, 1 fear, 2 attractiveness, 3 morality, 4 respect.",
			"the axis is left as it is",
			new[]
			{
				F("villager", "uint32_t", "the villager"),
				F("axis", "int32_t", "0 love, 1 fear, 2 attractiveness, 3 morality, 4 respect"),
				F("delta", "float", "the change, clamped to [-1, 1] after (writable)", true),
				F("player", "int32_t", "whose opinion set moves: 2 while the villager holds player two's set (their opinion scope), else 1"),
			}),
		new ModApiEvent("TimeOfDay", "time_of_day", 52, "rex_lhGameTimeManager_Update (r3 the time manager: +4 day fraction, +8 day count), after it",
			"The world clock reached a new hour or a new day: natural time, a fast forward, sleeping, travel time and a script's set all show here (a set one tick late).",
			string.Empty,
			new[]
			{
				F("hour", "int32_t", "0..23"),
				F("day", "int32_t", "the day count"),
				F("time", "float", "the day fraction 0..1"),
				F("new_day", "bool", "the day count changed"),
			}),
		new ModApiEvent("WeatherTheme", "weather_theme", 53, "rex_lhEnvironmentThemeManager_BlendToTheme (r3 manager; r4 name; f1 weight; f2 seconds)",
			"An environment theme (the weather and the sky's look) is blended to by a script or the network. Position-painted blending never comes here. On a co-op joiner the host's blends arrive here too; refusing one there desyncs the two skies.",
			"the blend is skipped",
			new[]
			{
				F("theme", "const char* / string", "the theme's name"),
				F("weight", "float", "the target weight (writable)", true),
				F("seconds", "float", "the blend time (writable)", true),
			}),
		new ModApiEvent("SleepMenu", "sleep_menu", 54, "rex_SleepMenu_Show (r3 the bed; the sleeper: the bed's newest user, rex_CECOnActionUse_Use r4)",
			"A bed's sleep menu is about to open; the time skip after it is the shipped scripts'.",
			"the menu stays closed",
			new[]
			{
				F("seen", "bool", "always true"),
				F("player", "int32_t", "the player sleeping: the bed's user as the game's use routine passed it to the bed's script (rex_CECOnActionUse_Use); 0 when no player used it"),
			}),
		new ModApiEvent("Travel", "travel", 55, "rex_CmdLevelChange_Start (r3 command: +20 world, +24 level, +28 entrance, +36 type, +50 full load), after it",
			"A travel started: a door, a level exit, a fast travel, a script. full_load is false for a change inside the loaded level, which LevelLoaded never reports.",
			string.Empty,
			new[]
			{
				F("world", "const char* / string", "the destination world"),
				F("level", "const char* / string", "the destination level"),
				F("entrance", "const char* / string", "the entrance marker"),
				F("type", "int32_t", "the travel type (3 free fast travel, 4 paid, 5 level exit)"),
				F("full_load", "bool", "a full level load follows"),
				F("player", "int32_t", "who asked for it (both heroes travel): the player a level exit fired for (its user, the player its walk-in test tested, an online joiner's request) or the world map's owner for a fast travel; 0 for a script's travel or one this machine did not see asked for"),
			}),
		new ModApiEvent("FastTravel", "fast_travel", 56, "rex_FastTravelManager_TravelToStop (r3 manager; r4 level; r5 stop, +4 marker; r6 paid); the chooser: inside rex_lhGuiWorldMap_FastTravel, the world map's owner",
			"The hero chose a fast-travel stop: the world map or a script. From the world map the map has already closed when this fires; a refusal leaves the hero there with the map shut.",
			"the hero stays put and pays nothing",
			new[]
			{
				F("level", "const char* / string", "the destination level"),
				F("stop", "const char* / string", "the stop's marker"),
				F("paid", "bool", "the travel costs gold"),
				F("player", "int32_t", "who chose it: the world map's owner (rex_lhGuiWorldMap_FastTravel); 0 for a script's"),
			}),
		new ModApiEvent("MenuOpened", "menu_opened", 57, "rex_lhGuiManager_EnterGuiMode / rex_lhGuiManager_EnterGuiModeQuiet (r3 the GUI manager), when menu mode was off",
			"The game entered its menu mode; the world pauses behind it.",
			string.Empty,
			new[]
			{
				F("seen", "bool", "always true"),
				F("player", "int32_t", MenuPlayerMeaning),
			}),
		new ModApiEvent("MenuClosed", "menu_closed", 58, "rex_lhGuiManager_ExitScreen (r3 the GUI manager), when it really leaves menu mode",
			"The game left its menu mode.",
			string.Empty,
			new[]
			{
				F("seen", "bool", "always true"),
				F("player", "int32_t", MenuPlayerMeaning),
			}),
		new ModApiEvent("MenuScreen", "menu_screen", 59, "rex_lhGuiManager_SetScreen (r3 the GUI manager; r4 the screen id), when it changes; TU1 numbering on both builds",
			"A menu shows a screen: 0 pause, 2 spells, 14 abilities, 16 equipment, 17 logbook, 18 options, 22 shop, 23 world map, 24 co-op.",
			string.Empty,
			new[]
			{
				F("screen", "int32_t", "the screen id the game states"),
				F("player", "int32_t", MenuPlayerMeaning),
			}),
		new ModApiEvent("WeatherRegion", "weather_region", 60, "rex_lhPaintMap_DaySetAt (r3 paint map, v1 the hero's position; returns the day set hash or -1), after it",
			"The hero walked into a different painted weather region: the region's environment day set, which picks the theme by time of day. A scripted blend is WeatherTheme. The game tests one hero, the one in player one's slot, once a frame — not once per split-screen view.",
			string.Empty,
			new[]
			{
				F("day_set", "uint32_t", "the region's day set (a GDB record hash; Lua and Godot: a \"0x…\" string), 0 outside every painted region"),
				F("previous", "uint32_t", "that player's day set before"),
				F("inside", "bool", "the hero is inside a painted region"),
				F("player", "int32_t", "the player whose hero the game tested (1 as shipped)"),
			}),
		new ModApiEvent("SleepChosen", "sleep_chosen", 61, "rex_lhMenuBox_Close (r3 the choice menu: +0 index, +13 cancelled), while the sleep menu is open",
			"The player answered a bed's sleep menu. A mod can change the choice or refuse the sleep; the shipped SleepFunction.Sleep applies it. A length the menu does not offer is a Lua mod wrapping SleepFunction.Sleep.",
			"the sleep is refused, as if cancelled",
			new[]
			{
				F("bed", "uint32_t", "the bed"),
				F("choice", "int32_t", "0 fast-forwards six hours, 1 twelve hours, 2 adds a day (the clock stays), 3 adds seven days (writable)", true),
				F("cancelled", "bool", "the player backed out"),
				F("player", "int32_t", "the player sleeping, as SleepMenu stated it"),
			}),
		new ModApiEvent("SlowTime", "slow_time", 62, "rex_SpellSlowTime_SetWorldScale (r3 the caster; f1 the scale; world +128)",
			"Slow Time changed the world's time scale. Every creature but the caster slows: the game slows the world and gives the caster (and the co-op partner) the inverse rate.",
			string.Empty,
			new[]
			{
				F("caster", "uint32_t", "the caster"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("scale", "float", "the world's time scale, 1 normal (writable)", true),
				F("active", "bool", "the world is slowed (scale < 1)"),
			}),
		new ModApiEvent("Summoned", "summoned", 63, "rex_SpellRaiseDead_BindSummoner (r3 summoner, r4 summon) / rex_SpellDamoclesSword_Create (r3 sword, r5 caster), after them",
			"A spell summoned a creature or a weapon. The summon leaves through EntityRemoved.",
			string.Empty,
			new[]
			{
				F("summoner", "uint32_t", "the caster"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("summon", "uint32_t", "the summoned entity"),
				F("kind", "int32_t", "1 a Raise Dead minion, 2 a Blades sword"),
			}),
		new ModApiEvent("Crime", "crime", 64, "rex_CECCrime_Add (r3 crime component, r4 criminal, r5 victim, r6 type)",
			"A crime is recorded against someone. The co-op partner's crimes are their own. A reported murder also records an assault (type 3), raised again.",
			"the crime is not recorded: no witness record, no bounty, no guards",
			new[]
			{
				F("criminal", "uint32_t", "who committed it"),
				F("player", "int32_t", "1 or 2 when the criminal is a hero, else 0"),
				F("victim", "uint32_t", "the victim, 0 when none"),
				F("victim_player", "int32_t", "1 or 2 when the victim is a hero, else 0"),
				F("type", "int32_t", "the game's crime type (2 murder, 3 assault, 6 trespass, 11 vandalism among them)"),
			}),
		new ModApiEvent("Achievement", "achievement", 65, "rex_CECStats_UnlockAchievement (r3 stats component, owner at +4; r4 id)",
			"An achievement unlocks, for both heroes (scripts and the stats code both unlock here).",
			"it stays locked",
			new[]
			{
				F("id", "int32_t", "the achievement, 1..50"),
				F("source", "uint32_t", "the entity whose stats asked"),
				F("player", "int32_t", "1 or 2 when the source is a hero, else 0"),
			}),
		new ModApiEvent("CollectableTaken", "collectable_taken", 66, "rex_CECInventory_AddEntity (coop_world_hooks.cpp), before the pickup",
			"A player picks up a one-time collectable kept per player in co-op (a silver key): each hero takes their own.",
			string.Empty,
			new[]
			{
				F("player", "int32_t", "1 or 2"),
				F("entity", "uint32_t", "the collectable in the world"),
				F("record", "uint32_t", "its GDB record"),
				F("stays", "bool", "it stays in the world for the partner"),
			}),
		new ModApiEvent("PartnerConsent", "partner_consent", 67, "the consent ask (mod_lua_natives.cpp: the core beds and Refii.ask_partner)",
			"A co-op script asks the other player's consent before something that changes both players' game. A handler can answer for the partner.",
			string.Empty,
			new[]
			{
				F("ticket", "uint32_t", "the question, as Refii.partner_answer takes it"),
				F("kind", "int32_t", "1 sleep, 2 sex, 0 a mod's own question"),
				F("asker", "int32_t", "the asking player, 1 or 2"),
				F("detail", "int32_t", "the sleep type for kind 1"),
				F("answer", "int32_t", "-1 asks the partner (the default), 0 refuses, 1 agrees (writable)", true),
			}),
		new ModApiEvent("PropertyBought", "property_bought", 68, "rex_CECPlayerProperties_Buy (r3 the buyer's properties comp69, r4 the building), before",
			"A hero buys a building. Online it fires on the host, where the economy is (a joiner's purchase is run there).",
			"the building stays unbought and the gold where it is",
			new[]
			{
				F("hero", "uint32_t", "the buyer"),
				F("player", "int32_t", "1 or 2"),
				F("building", "uint32_t", "the building"),
				F("price", "int32_t", "what it costs, condition included (writable)", true),
			}),
		new ModApiEvent("PropertySold", "property_sold", 69, "rex_CECPlayerProperties_Sell (r3 the seller's comp69, r4 the building), before",
			"A hero sells a building; a building shared with a Fable 3 partner gives them half.",
			"it stays owned",
			new[]
			{
				F("hero", "uint32_t", "the seller"),
				F("player", "int32_t", "1 or 2"),
				F("building", "uint32_t", "the building"),
				F("price", "int32_t", "what the seller is paid (writable)", true),
			}),
		new ModApiEvent("RentPaid", "rent_paid", 70, "rex_CECPlayerProperties_PayIncome (r3 comp69, r4 drops, r5 away), after",
			"A hero's rent and shop profits were paid, every five real minutes (or the catch-up for time away, paid on the next screen).",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the owner"),
				F("player", "int32_t", "1 or 2"),
				F("gold", "int32_t", "the total paid"),
				F("drops", "int32_t", "how many five-minute drops it covers"),
				F("away", "bool", "the catch-up for time out of the game"),
			}),
		new ModApiEvent("PropertyRented", "property_rented", 71, "rex_CECPlayerProperties_SetRentedOut (r3 comp69, r4 the building, r5 rented), before",
			"A building is rented out, or its tenants are evicted.",
			"it stays as it was",
			new[]
			{
				F("hero", "uint32_t", "the owner"),
				F("player", "int32_t", "1 or 2"),
				F("building", "uint32_t", "the building"),
				F("rented", "bool", "rented out (false: evicted)"),
			}),
		new ModApiEvent("PropertyCondition", "property_condition", 72, "the condition rules (property_hooks.cpp: the rent payout and the repair)",
			"A building's condition (Fable 3's, 0..1) changed: a rented home wore down at a rent drop, or it was repaired. At 0 its tenants pay nothing.",
			string.Empty,
			new[]
			{
				F("uid", "uint64_t", "the building's UniqueID (\"0x…\" in Lua and Godot)"),
				F("player", "int32_t", "the owner, 1 or 2"),
				F("condition", "float", "now, 0..1"),
				F("previous", "float", "before"),
				F("repaired", "bool", "a repair (else wear)"),
			}),
		new ModApiEvent("Divorce", "divorce", 73, "rex_CECPlayerFamily_FinishDivorcing (r3 the family comp136, r4 the spouse), after",
			"A marriage ended: the spouse has left the hero.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2"),
				F("spouse", "uint32_t", "the former spouse"),
			}),
		new ModApiEvent("Widowed", "widowed", 74, "rex_CECPlayerFamily_Widow (r3 the family comp136, r4 &the spouse's UID), after",
			"A hero's spouse died or was removed: the household ends.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2"),
				F("uid", "uint64_t", "the late spouse's UniqueID (\"0x…\")"),
			}),
		new ModApiEvent("ChildBorn", "child_born", 75, "rex_CECPlayerFamily_AdoptWithSpouse (r3 the family comp136, r4 the spouse, r5 the child), after",
			"A child is born to (or adopted by) a hero and their spouse. Online it fires on the host.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the parent hero"),
				F("player", "int32_t", "1 or 2"),
				F("spouse", "uint32_t", "the other parent"),
				F("child", "uint32_t", "the child"),
			}),
		new ModApiEvent("GiftGiven", "gift_given", 76, "rex_lhGiftComponent_GiveGift (r3 the giver's comp33, r4 the item), before",
			"A hero gives a gift (a ring among them) to the one they target. It leaves their inventory and comes back after fifteen seconds unless the villager keeps it (gift_accepted). Fires on the giver's machine.",
			"nothing is given",
			new[]
			{
				F("hero", "uint32_t", "the giver"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("target", "uint32_t", "the one it is given to"),
				F("item", "uint32_t", "the item's record (\"0x…\")"),
			}),
		new ModApiEvent("GiftAccepted", "gift_accepted", 77, "rex_CECHeroGifts_RemovePermanently (r3 the giver's comp33), after",
			"A villager kept a hero's gift: it is theirs for good and the gift's morality applies. Online it fires on the host.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "the giver"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("item", "uint32_t", "the item's record (\"0x…\")"),
			}),
		new ModApiEvent("Engaged", "engaged", 78, "rex_CECOpinionReaction_SetEngaged (r3 the villager's comp47, r4 engaged), before",
			"A villager becomes engaged to a hero (a ring accepted), or the engagement ends.",
			"it stays as it was",
			new[]
			{
				F("villager", "uint32_t", "the villager"),
				F("player", "int32_t", "the hero it concerns, 1 or 2"),
				F("engaged", "bool", "engaged (false: the engagement ends)"),
			}),
		new ModApiEvent("Follow", "follow", 79, "rex_CECFollow_Start (r3 the follower's comp94, r4 the leader), before / rex_CECFollow_Stop (r3 comp94), after",
			"A creature starts or stops following someone: a hero's follower, a fiancé, a partner for the night.",
			"on a start: it does not follow",
			new[]
			{
				F("follower", "uint32_t", "the creature"),
				F("leader", "uint32_t", "who it follows"),
				F("player", "int32_t", "1 or 2 when the leader is a hero, else 0"),
				F("following", "bool", "starts (false: stops)"),
			}),
		new ModApiEvent("FurnitureUpgraded", "furniture_upgraded", 80, "rex_CECUpgradeableFurniture_SetLevel (r3 the piece's comp68, r4 the level), before",
			"A piece of furniture changes level while redecorating (with a furniture card). Online it fires on the host.",
			"it keeps its level",
			new[]
			{
				F("piece", "uint32_t", "the furniture"),
				F("player", "int32_t", "the player redecorating, 1 or 2"),
				F("level", "int32_t", "the new level (writable)", true),
				F("previous", "int32_t", "the level before"),
			}),
		new ModApiEvent("Redecorating", "redecorating", 81, "rex_CECBuilding_SetRedecorating (r3 the building's comp67, r4 on), after",
			"Redecorating a building starts or ends. Fires on the redecorating player's machine.",
			string.Empty,
			new[]
			{
				F("building", "uint32_t", "the building"),
				F("player", "int32_t", "the player redecorating, 1 or 2"),
				F("on", "bool", "starts (false: ends)"),
			}),
		new ModApiEvent("HouseholdBudget", "household_budget", 82, "rex_lhGuiRealEstateScreen_SetBudget (r3 the estate screen, r4 the gold), before",
			"A hero sets a household's budget at its home: the upkeep the family is given. Online it fires on the host.",
			"the budget stays",
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2"),
				F("building", "uint32_t", "the home"),
				F("gold", "int32_t", "the budget (writable)", true),
			}),
		new ModApiEvent("TrackedStat", "tracked_stat", 83, "rex_CECStats_ModifyTrackedStat (r3 the hero's stats comp57, r4 the stat, r5 the amount), before",
			"A hero's tracked stat counts (the game's ETrackedStatType: kills, sex, marriages, children, gold spent and many more; achievements and titles are judged from them). Online, player two's are counted on the joiner's machine.",
			"nothing is counted",
			new[]
			{
				F("hero", "uint32_t", "the hero"),
				F("player", "int32_t", "1 or 2 for a hero, else 0"),
				F("stat", "int32_t", "the stat (ETrackedStatType)"),
				F("amount", "int32_t", "what is added (writable)", true),
			}),
		new ModApiEvent("Partnership", "partnership", 84, "the household tick (hero_partners.cpp): the two heroes' partnership changed",
			"Fable 3's player-to-player partnership between the two heroes changed. Fires on the host.",
			string.Empty,
			new[]
			{
				F("kind", "int32_t", "0 none, 1 business partners, 2 married"),
				F("previous", "int32_t", "the kind before"),
			}),
		new ModApiEvent("HouseholdRestored", "household_restored", 85, "the household tick (household_persist.cpp), after the restore",
			"Player two is back in the host's world: the household and property kept with the world were put back on their hero. Fires on the host.",
			string.Empty,
			new[]
			{
				F("hero", "uint32_t", "player two's hero"),
				F("player", "int32_t", "2"),
				F("spouses", "int32_t", "the spouses found in the world"),
			}),
		new ModApiEvent("DynamicWeather", "dynamic_weather", 86, "the guest tick, from the host's \"ModWeather:\" pump action (Engine_DynamicWeatherChoice's change)",
			"The dynamic weather in effect changed: the player's override, else the level's rule (fixed, interior, the game's own theme, or a roll that drifts every few minutes). One weather per co-op session: the host decides, an online joiner adopts the host's statement for its level; each player's sky switch only changes whether it is drawn. Fires on both machines.",
			string.Empty,
			new[]
			{
				F("level", "const char* / string", "the level the weather is for, \"<level>/<scenario>\" lower-case, as the statement names it"),
				F("weather", "const char* / string", "a preset (Clear, PartlyCloudy, Cloudy, Overcast, Rain, Storm, Snow, Fog, LightRain, LightSnow, Blizzard), \"theme\" (the game's own theme weather) or \"interior\""),
				F("previous", "const char* / string", "the weather before, the same way; empty for the session's first"),
				F("seconds", "float", "how long the change blends over (0: at once)"),
				F("from_host", "bool", "an online joiner adopting the host's statement; false where this machine decided it"),
			}),
		new ModApiEvent("ModMenuRow", "mod_menu_row", 87, "Godot's menu lists (Engine_UiModMenus), then the guest tick from the host's command queue (menu_row)",
			"A row of a menu a mod defines in ui/templates.json was chosen, or a row a mod patched into a shipped menu, or any row whose command is Opens:<menu> or Mod:lua:<function>. Godot scripts get it as it happens; native handlers on the next guest tick, on the title and the front end too; Lua handlers on the next guest tick: in a game the init.lua ones, on the title and the front end the mod/frontend.lua ones (run in the current Gameface presentation's own Lua state).",
			string.Empty,
			new[]
			{
				F("menu", "const char* / string", "the menu the row is on: the mod menu's name, or the list's own (frontend-root, pause-root, a screen's population script)"),
				F("row", "const char* / string", "the row's name"),
				F("player", "int32_t", "1 or 2: the player whose menu it is"),
				F("mod", "const char* / string", "the mod that stated the row or its menu, empty for a shipped row"),
			}),
		new ModApiEvent("ModMessageBox", "mod_message_box", 88, "Godot's host boxes (Engine_UiModBoxes), then the guest tick from the host's command queue (box_answer)",
			"A message box a mod raised (refii_mod_message_box, Refii.message_box, RefiiHost.message_box) was answered. Godot scripts get it as it happens; native handlers on the next guest tick, on the front end too; Lua handlers in a game (init.lua) and on the front end (mod/frontend.lua).",
			string.Empty,
			new[]
			{
				F("id", "uint32_t", "what the call returned: the payload's ids are 1 .. 2^23-1, Godot's 2^23 .. 2^24-1"),
				F("button", "int32_t", "0 for A, 1 for B, -1 when another box replaced it or its player is played on the other machine"),
				F("player", "int32_t", "1 or 2: whose box it was"),
			}),
		new ModApiEvent("ModNotice", "mod_notice", 89, "Godot's notice channel (Engine_UiNotices, shown by GameUi), then the guest tick from the host's command queue (notice_done)",
			"A notice a mod posted (refii_mod_notice, Refii.notice, RefiiHost.notice) has gone. Godot scripts get it as it happens; native handlers on the next guest tick, on the front end too; Lua handlers in a game.",
			string.Empty,
			new[]
			{
				F("id", "uint32_t", "what the call returned: the payload's ids are 1 .. 2^23-1, Godot's 2^23 .. 2^24-1 (one id space with the message and menu boxes)"),
				F("outcome", "int32_t", "0 once it was shown and its time ran out (or it was dismissed on the front end); -1 when it never showed: its player is played on the other machine, or the queue overflowed"),
				F("player", "int32_t", "1 or 2: whose notice it was"),
			}),
		new ModApiEvent("ModMenuBox", "mod_menu_box", 90, "Godot's host menu box (Engine_UiModMenuBox), then the guest tick from the host's command queue (menu_box_answer)",
			"A menu box a mod raised (refii_mod_menu_box, Refii.menu_box, RefiiHost.menu_box) was answered. Godot scripts get it as it happens; native handlers on the next guest tick, on the front end too; Lua handlers in a game. A RefiiHost.message_box with more than two buttons is a menu box answered as mod_message_box instead.",
			string.Empty,
			new[]
			{
				F("id", "uint32_t", "what the call returned (one id space with the message boxes and notices)"),
				F("option", "int32_t", "the chosen option's index in the list the mod gave (paging is the box's own); -1 for B on a cancelable box, another box replacing it, or its player played on the other machine"),
				F("player", "int32_t", "1 or 2: whose box it was"),
			}),
		new ModApiEvent("ModAbility", "mod_ability", 91, "the engine's mod-ability purchase (src/vfs/src/hooks/mod_abilities.cpp), run on the guest tick from the book's ability_buy / ability_refund",
			"A player is buying or giving back a level of a mod ability (ui/abilities.json). The ENGINE runs the purchase from the declaration: the level is the player's saved value under levelKey, the price the game's own table of the ability costLike names, split evenly across the declared disciplines' pools (the first named takes the odd point) and spent through the game's own Spend; an unlearn (\"refund\": \"half\") gives half the given-up level's price back into General, as the shipped unlearn does. Raised before anything changes, only for a purchase that can happen. A mod applies the level's effect by reading the level from the player store where it is used.",
			"refuses it: nothing is spent and the level stands",
			new[]
			{
				F("ability", "const char* / string", "the ability's declared name"),
				F("mod", "const char* / string", "the mod that declared it"),
				F("refund", "bool", "true for an unlearn, false for a purchase"),
				F("previous", "int32_t", "the level held"),
				F("level", "int32_t", "the level it will hold"),
				F("experience", "int32_t", "the price to be spent (a purchase) or what comes back into General (an unlearn)"),
				F("player", "int32_t", "1 or 2: whose book it is (the level, the hero and the experience are theirs)"),
			}),
		new ModApiEvent("SpellPick", "spell_pick", 92, "a mod's pick: the spell selectors (Engine_UiSpellPicks), then the guest tick from the host's command queue (spell_pick); the game's own pick: the guest's slot writer CECSpellManager::SetSpellSlot (TU1 sub_82541F30) when it is called as a choice",
			"A player chose or cleared a pick on one of their spell slots. A mod's pick (ui/spellwheel.json, Spell Weaving's partner) is kept per player and slot as a saved player value under the pick's store key, written after the handlers unless one refuses it. The game's own pick is the slot's spell (pick \"spell\", mod \"\"): every assignment the game makes as a choice raises it before the slot is written — each step of the selectors' carousel, the scripts' spell-table natives, the host's own assignment — and a changed spell is the one stored; the template's defaults, the other machine's pick arriving and a save load are not picks. Raised on the machine whose player made the pick; Godot scripts get it from the bus.",
			"keeps the slot's pick as it was",
			new[]
			{
				F("pick", "const char* / string", "the pick's name as ui/spellwheel.json states it; \"spell\" for the game's own pick"),
				F("mod", "const char* / string", "the mod that stated the pick; empty for the game's own"),
				F("slot", "int32_t", "the slot, 0..4"),
				F("spell", "int32_t", "the will chosen, 0 Lightning .. 7 Dead Rising; -1 cleared (writable: another will, or -1)", true),
				F("previous", "int32_t", "the will held before, -1 none"),
				F("player", "int32_t", "1 or 2: whose selectors"),
			}),
		new ModApiEvent("PartnerMessage", "partner_message", 93, "the receiving machine's guest tick, from the route's mod lane (core/event_relay.h pm: lines; mod_events.cpp DrainPartnerMessages), after the other machine's refii_mod_send_to_partner / Refii.send_to_partner / RefiiHost.send_to_partner",
			"A mod's own message from THE OTHER MACHINE of an online session. Carried on the route — the ordered, acknowledged lane the game's own cross-machine calls take — so it arrives once, in the order sent, and waits out a load or a pause; never raised on the sender's machine, never alone or in local co-op. Not a replica: it is this machine's own handling, so player-state writes in a handler count. Godot scripts get it from the bus like any other event.",
			string.Empty,
			new[]
			{
				F("mod", "const char* / string", "the mod that sent it (its name)"),
				F("name", "const char* / string", "the message's word, as the sender named it (letters, digits, '_', '.', '-'; at most 32)"),
				F("payload", "const char* / string", "its text, at most 256 bytes; on the Lua and Godot surfaces a quote, backslash or line break in it arrives as '_'"),
				F("from", "int32_t", "the player the sending machine plays: 1 the host, 2 the joiner"),
			}),
		new ModApiEvent("LookAtButton", "look_at_button", 94, "rex_CPlayer_SetLookAtButtonDown (r3 the player component 33, owner at +4; r4 the value), before it",
			"A player hero's look-at flag is being written — the byte every lock camera script reads, what a held LT states: by the camera's input event on LT down (1), the hero's message handler on the release (0), or the API's own lock_target / release_lock. A mod whose lock is its own writes down to keep the flag equal to it, whoever asks.",
			"the flag stays as it was (the writer never runs; the camera mode is not re-picked)",
			new[]
			{
				F("hero", "uint32_t", "the hero whose flag it is"),
				F("player", "int32_t", "1 or 2"),
				F("down", "bool", "the value being written (writable)", true),
				F("component", "uint32_t", "the player component the flag lives on (native)"),
			}),
		new ModApiEvent("VortexStarted", "vortex_started", 95, "rex_VortexComponent_Init (r3 the component 211, owner at +4; r4 caster, r5 target, r6 serial -> +0x18, r8 level -> +0x14), after it — inside the spell factory, before spell_created",
			"A vortex is live: its component was initialised on the vortex entity by the vortex spell's post-init, inside the factory and before spell_created reports the spell object, which finishes at once (no registry lookup ever returns a vortex). spell_parts(serial) finds the component again while it lives. Online the cast runs on both machines (replica on the copy).",
			string.Empty,
			new[]
			{
				F("vortex", "uint32_t", "the live component (native)"),
				F("entity", "uint32_t", "the vortex entity, the thing in the world the look rides"),
				F("caster", "uint32_t", "the caster entity"),
				F("caster_player", "int32_t", "1 or 2 for a hero, else 0"),
				F("target", "uint32_t", "the target entity, or 0"),
				F("serial", "uint32_t", "the cast's number (the spell object's +0x44), carried by its hits"),
				F("level", "int32_t", "the power 0..4"),
				F("x", "float", "the vortex entity's position"),
				F("y", "float", "the vortex entity's position"),
				F("z", "float", "the vortex entity's position"),
			}),
		new ModApiEvent("VortexEnded", "vortex_ended", 96, "rex_VortexComponent_Shutdown (r3 the component), before it",
			"The vortex is ending: its component's Shutdown, with the twister's last point (the component's +0x140, the vector its update hands every ground effect and places the entity at). The look holds where the twister ended, not where the cast began.",
			string.Empty,
			new[]
			{
				F("vortex", "uint32_t", "the component (native)"),
				F("entity", "uint32_t", "the vortex entity"),
				F("caster", "uint32_t", "the caster entity"),
				F("caster_player", "int32_t", "1 or 2 for a hero, else 0"),
				F("serial", "uint32_t", "the cast's number"),
				F("x", "float", "the twister's point"),
				F("y", "float", "the twister's point"),
				F("z", "float", "the twister's point"),
			}),
		new ModApiEvent("FireballImpact", "fireball_impact", 97, "rex_TargetedFireball_Impact (r3 the spell object, r4 what it struck or 0, v1 position, v2 normal), after it, when its exploded-byte guard passed",
			"A targeted fireball struck something: its Impact ran. detonated is the exploded byte (spell+0x48) going up inside the call — the blast delivered every victim's hit inside it and the fireball is spent; a creature the fireball passes and ignites leaves it clear and the fireball flies on. hits counts the hits the receiver took inside the call carrying this cast's serial: 0 on a detonation means the blast found nothing alive. Online the cast runs on both machines; the hits are decided where world_here holds.",
			string.Empty,
			new[]
			{
				F("spell", "uint32_t", "the spell object (native)"),
				F("serial", "uint32_t", "the cast's number"),
				F("level", "int32_t", "the power 0..4"),
				F("caster", "uint32_t", "the caster entity"),
				F("caster_player", "int32_t", "1 or 2 for a hero, else 0"),
				F("struck", "uint32_t", "the entity struck, 0 for ground, wall or water"),
				F("struck_player", "int32_t", "1 or 2 when a hero was struck, else 0"),
				F("x", "float", "the impact position"),
				F("y", "float", "the impact position"),
				F("z", "float", "the impact position"),
				F("nx", "float", "the impact normal"),
				F("ny", "float", "the impact normal"),
				F("nz", "float", "the impact normal"),
				F("detonated", "bool", "the blast went off inside this call"),
				F("hits", "int32_t", "hits delivered inside the call carrying this serial"),
			}),
		new ModApiEvent("Paused", "paused", 98, "rex_lhGameFlowGuiMode_Apply / rex_lhGameFlowGuiMode_Remove (r3 the GameFlow mode command; its id by vtable slot 1), after each",
			"The game's pause state flipped: a GameFlow mode command of the pause family became the mode in force (its apply: both players' input categories applied, the world held behind it — what rex_lhGameFlow_IsGuiActive answers), or stopped being it (its remove). Mode 1 is the GUI / pause command (the pause menu and every screen entered from play, the partner's relayed pause, a camera mode); mode 2 a level change. Raised again when a command above it is removed (the mode re-applied) and when one is pushed over it (removed for its length).",
			string.Empty,
			new[]
			{
				F("paused", "bool", "true after the apply (the mode is in force), false after the remove"),
				F("mode", "int32_t", "the command's id: 1 the pause / menu mode, 2 a level change"),
				F("player", "int32_t", "for mode 1 with a menu screen up: whose menu (2 on an online joiner or for local player two's pause book, else 1); 0 for a pause with no screen (the partner's relayed one, a camera mode) and for mode 2"),
				F("screen", "int32_t", "the GUI manager's screen id then, TU1 numbering (31 none, 0 the pause root, 29 co-op); -1 when the in-game GUI is not up"),
			}),
		new ModApiEvent("StartGame", "start_game", 99, "rex_lhFrontEnd_SetStartGameTarget (r3 the front-end object, r4 the save name LHString, r5 the start level LHString), before it",
			"The front end starts a game: New Game, Load, Continue or a co-op join stated the target the in-game state is created from. On the title there is no game VM, so Lua hears it in mod/frontend.lua; native handlers and Godot scripts as any event.",
			string.Empty,
			new[]
			{
				F("save", "const char* / string", "the save's name; \"\" for a new game"),
				F("level", "const char* / string", "the level the game starts in"),
				F("new_game", "bool", "no save: a new game"),
				F("player", "int32_t", "the signed-in pad's player: 1"),
			}),
	};

	/// <summary>The menu events' player: whose menu it is.</summary>
	private const string MenuPlayerMeaning =
		"whose menu: 2 on an online joiner, and in local co-op while player two owns the pause (the pause screen's owner) or their shop; 1 otherwise";

	/// <summary>The wheel-switched lock, the same on the native and Lua surfaces.</summary>
	private const string WheelSwitchesTargetMeaning =
		"that hero's target lock switches by the MOUSE WHEEL this tick — state it each tick the lock holds (it lapses two ticks later; the Lua tick runs every guest tick, so a lock stated from it never lapses): " +
		"while it holds and the hero is locked under the third-person camera the keyboard and mouse drive, the mouse's movement never switches target " +
		"and each wheel notch is one switch through the camera's own Targeting.CheckForBetterTargetInDirection (down the next target right, up the next left), " +
		"waiting on the camera's ClearToRotate as a stick flick does, at most one notch waiting; without it a mouse flick switches; a pad is untouched";

	/// <summary>The native helpers of <c>core/refii_mod.h</c>, for the Guide and the editor API.</summary>
	/// <summary>The tint primitive, the same on every surface.</summary>
	private const string EntityTintMeaning =
		"tints an entity through the game's own appearance setter (component 3, the one a corpse's grey goes through): " +
		"linear multipliers of its lit colour, 1 untouched, above 1 brighter (the modern look caps a lane at 31.875/4); " +
		"both looks draw it and co-op replicates it; a is stored but drawn by no shader (opacity is not the tint's); " +
		"untint puts back the constructor's 1, 1, 1, 1 — untint only what you tinted. Guest tick";

	private const string AppearanceMeaning =
		"the other setters of the same renderable (component 3), replicated like the tint: alpha 0..1 (0 hides the body), " +
		"a uniform scale, the saturation mask (drawn desaturated), drawable (off takes the body out of the drawn set; it keeps simulating); " +
		"reset = the constructor's 1, 1, off, on. A value the game restates each frame (a Blades sword's scale and alpha, a death's alpha ramp) wins. Guest tick";

	private const string LockMeaning =
		"the target lock as the game's own LT states it, for player 1 or 2: lock_target sets the targeting component's SetTarget (entity 0: the game's own pick), " +
		"the force byte and the look-at flag; release_lock clears the last two (a held LT is the trigger's); switch_target is the camera's own flick, left or right, " +
		"and answers the target held; locked_target is the target while player mode 4 or the force byte holds, else 0. Guest tick";

	private const string StatusMeaning =
		"a status through the spell's own per-victim installer, without the spell: kind 1 electrified, 2 chaos, 3 burning (4, a vortex, is refused), " +
		"for seconds at spell level 0..4, from source (0: the entity itself; chaos takes its direction from it), no damage; clear_status runs the game's own remover; " +
		"has_status reads the component. The status_effect event fires either way. Guest tick";

	public const string SpeedMeaning =
		"the player component's own movement-speed override for player 1 or 2, what the scripts' Player.SetOverriddenMovementSpeedMultiplier " +
		"and SetPlayerMovementSpeedOverridden state: the locomotion update multiplies the whole velocity by the factor while the override holds, so 0 holds the hero in place " +
		"(turning, rolls and blocks are untouched); clear puts the byte back; speed_multiplier answers whether one holds and the factor — the game never clears a script's, " +
		"so take it only while none holds, and give it back";

	public const string ActionMeaning =
		"the action an entity's action manager (component 49) runs — 0 for none or once stopped; its address is reused by the next action anyone plays — its clock (vtable slot 7, " +
		"the float the commitment is timed by; what writes it is unproven), and the interrupt permission the rule reads: a same-priority action gets in only once the running one has " +
		"granted ALLOW_ACTION_INTERRUPT (strikes, rolls and plain blocks play at priority 6; the preemptive block and parry at 8 need none); allow_action_interrupt grants it exactly as " +
		"the animation event does — a mod that cancels that anim_event (a committed swing) grants it later. Any creature, both heroes";

	public const string ActionKindMeaning =
		"what an action IS: its definition is a Lua table of the game's scripts (Action.SetCurrentAction(entity, def)), not a GDB record and not kept on the object; every action is " +
		"made from it by the one factory (sub_8218A8E0, hooked as rex_CActionFactory_CreateFromDefinition), which reads the table's Type — EScriptableAction of the game's own " +
		"scriptableactionenum.lua: 0 PLAY_ANIMATION, 1 MELEE_STRIKE, 2 STRAFE (the roll), 16 BATCH, 48 LOOP, ... 88 DIG. action_type answers that id (-1 for an action the factory " +
		"never saw), action_name its enum name (\"\" / nil unknown); melee_swing and anim_event carry both. Named for the last 64 actions made; an address reused later takes the later type";

	public const string FrontEndMeaning =
		"the front end's own state, on the title and its screens (no game, no hero, no save loaded): the signed-in pad's user index (-1 / nil none: dword_83496AF8 against the " +
		"sign-in mask dword_83496A70, as the New Game guard reads them), the signed-in profile's saved games on every device (rex_lhSaveManager_CountSavedGamesAllDevices on the " +
		"front end's save manager — a content enumeration, ask once a screen; -1 / nil with no front end), and the current Gameface presentation's name (\"FrontendMainMenu\", ...)";

	public const string LivingMeaning =
		"in the world and, for a creature, not yet died: alive's bit and the creature component's own byte the target selector tests (1 from construction, 0 once Die ran); " +
		"an entity without a creature component counts as living while in the world";

	public const string SlotMeaning =
		"the spell player 1 or 2's hero holds in slot 0..4 (0 Lightning .. 7 Dead Rising), -1 for none: the spell manager's slot table, row 0, what the launch reads";

	public const string CastPartsMeaning =
		"what a cast put in the world, by the serial its hits and events carry (spell_created / vortex_started / fireball_impact, a hit's spell_serial), read live and validated: " +
		"the spell object while it still carries the serial, type, targeted, level, caster; the live vortex component and its entity; the fireball's shot entity while it is in the world and whether it detonated; " +
		"the Blades' live swords; and the origin — the twister's point, the fireball's impact point once reported, else its shot's position, else the caster's. Nil / ok false for a cast the factory never saw";

	private const string KnockbackMeaning =
		"a hit of the game's own shape through its one receiver, no damage and no attacker: knockback is a Force Push's knockdown hit along dir scaled by force " +
		"(whether the physics keeps the length is unproven), stagger the flinch a 0-damage melee tap plays. Hit and HitLanded fire; HitResponse is not offered. Guest tick";

	private const string SoundMeaning =
		"the game's own cue player (the body of the scripts' Sound.PlayEvent): play_sound starts a cue on an entity's sound component's emitter " +
		"(or at its position when it has none), play_sound_at on an emitter of the API's own at a point (eight kept, the oldest freed); " +
		"cue names are the audio banks' event names (an .adb's strings), the cue's own volume and attenuation apply, and the tag (may be empty) " +
		"is what stop_sound matches on that entity's emitter (entity 0: the point emitters), fading over milliseconds. " +
		"play_music / stop_music are SoundTools.PlayMusic / StopMusic's own bodies: the MUSIC_* cues, stopped with the game's 2 s fade. Guest tick";

	private const string AnimationMeaning =
		"a named animation as the shipped AI plays one: Action.SetCurrentAction with a PLAY_ANIMATION action (Anim the GDB animation record's name, " +
		"OverrideLooking, SpeedMultiplier, BlendInTime), wrapped in a BATCH with NumLoops for loops above 1, so the game's own evaluator plays it " +
		"and the pose feed shows it; a higher-priority action keeps the body. stop_animation is Action.FinishScriptedActions. Guest tick";

	private const string OpinionSetMeaning =
		"a villager's opinion of a hero, per hero: modify_opinion is the game's own ModifyAxisValue on axis 0 love 1 fear 2 attractiveness 3 morality 4 respect, " +
		"clamped to [-1, 1], in player two's own block for their hero (opinion_modified fires); add_deed posts the deed message the game's debug " +
		"post sends (MESSAGE_EVENT_DEED_PERSON from the hero to the villager at the hero's position, extra_data the Deed record's id), so the " +
		"record's strength and kind and the villager's own rules apply (opinion_deed fires, routed to the doer's player). Guest tick";

	/// <summary>NPC orders, the same on every surface (the quests' own entries, run in the game VM).</summary>
	public const string NpcMeaning =
		"orders a creature as the shipped quests do, through the game's own entries in its Lua VM: npc_follow is Follow.FollowEntity (the follow component, " +
		"radius the quests' 2), npc_go_to ScriptFunction.StartScriptControlledMovement (the AI's script move-to, not waited for), npc_flee the AI's " +
		"Navigation.FleeFromEntity at sprint, npc_attack CombatRegister:SetScriptTarget (the enemy relationship plus the combat info's ScriptTarget), " +
		"npc_set_hostile Faction.AddTemporaryEntityRelationship FACTION_STATUS_ENEMY / RemoveTemporaryEntityRelationship against that entity — a hero is " +
		"its own entity, so player two's hero is hostile on its own — and npc_stop ends them all (StopFollowing, StopScriptControlledMovement, ClearScriptTarget). " +
		"The AI mind may retake the body later (combat, a cutscene), which the call cannot see. Guest tick";

	/// <summary>The game clock and the environment theme, the same on every surface.</summary>
	public const string TimeWeatherMeaning =
		"set_time_of_day sets the game clock to hours 0..24 through Timing.SetTimeOfDayAbsolute's own body on the time manager (clamped; an online session " +
		"tells the partner; time_of_day fires from the next clock update), time_of_day reads it back; set_weather blends the GAME's environment theme — " +
		"its weather and sky look — as the quests do, EnvironmentTheme.BlendToEnvironmentTheme(theme, weight 0..1, seconds), so weather_theme fires and a " +
		"handler may cancel it; theme is an EnvTheme* record name. The Dynamic Sky's own weather override (dynamic_sky_weather) is the host's switch, not this; " +
		"in co-op the host's weather is everyone's. Guest tick";

	public static readonly IReadOnlyList<ModApiHelper> Helpers = new[]
	{
		new ModApiHelper("uint32_t refii_mod_player_hero(base, player)", "player 1's or 2's hero entity, or 0"),
		new ModApiHelper("uint32_t refii_mod_local_hero(base)", "the local player's hero, or 0: player two's on an online joiner, player one's on a host or alone, and in local co-op the hero of whoever owns the open menus (both are local there: name a player when the answer must not follow the menus)"),
		new ModApiHelper("int32_t refii_mod_player_of(base, entity)", "1 or 2 when the entity is that player's hero, else 0"),
		new ModApiHelper("int32_t refii_mod_player_for(ctx, base, entity)", PlayerForMeaning + " — Lua's Refii.player_for, natively"),
		new ModApiHelper("int32_t refii_mod_world_here(base)", "whether this machine runs the shared world (AI, hits, NPCs): alone, local co-op, the online host; make world effects only here"),
		new ModApiHelper("int32_t refii_mod_menu_player(base)", "the player whose menus are open here, 1 or 2: an ability row publishes and buys for them"),
		new ModApiHelper("int32_t refii_mod_opinion(base, villager, player, float out[5])", "how a villager feels about that hero: love, fear, attractiveness, morality, respect; a set per hero, player two's moved only by player two's deeds"),
		new ModApiHelper("int32_t refii_mod_forgive_crimes(ctx, base, villager, hero) / refii_mod_discovered_crime(ctx, base, villager, hero) / refii_mod_perceived_renown(ctx, base, villager, hero, int32_t* out)", "a villager's crimes and renown for one hero: forgives only that hero's crimes the villager knows of (how many; the other hero's stand), whether it discovered one of theirs, the renown it credits them with — the shipped Villager natives name no hero"),
		new ModApiHelper("int32_t refii_mod_coop_mode(base)", "0 alone, 1 local co-op, 2 online hosting, 3 online joined"),
		new ModApiHelper("uint32_t refii_mod_focus(base, player)", "the living entity that hero is focused on (their targeting), or 0; meaningful only where that hero is played (refii_mod_player_here)"),
		new ModApiHelper("void refii_mod_wheel_switches_target(base, hero)", WheelSwitchesTargetMeaning),
		new ModApiHelper("float refii_mod_player_value(player, key, fallback)", "a player's value, the same on both co-op machines (fallback when unset)"),
		new ModApiHelper("int32_t refii_mod_player_get(player, key, float* out)", "whether the player has a value under key, written to out when not null"),
		new ModApiHelper("void refii_mod_player_set(player, key, v) / refii_mod_player_add(player, key, d, saved)", "writes a player's value on either machine; the hero's owner applies it (an add merges)"),
		new ModApiHelper("void refii_mod_player_save(player, key, v)", "a SAVED player value: kept with that hero's own save, back when it loads"),
		new ModApiHelper("int32_t refii_mod_player_here(player)", "whether this machine plays that hero: run a per-player refill or timer only there"),
		new ModApiHelper("int32_t refii_mod_event_replica()", "the event is a copy of something the other co-op machine decides — the game's own copy (verdicts apply) or a RELAYED one (it already happened): player-state writes are dropped; skip world and body effects"),
		new ModApiHelper("int32_t refii_mod_opinion_scope_two()", "1 while the game is moving player two's opinions on this thread (an OpinionModified for hero two)"),
		new ModApiHelper("refii::mods::IsPlayerHero(base, entity)", "whether the entity is either player's hero"),
		new ModApiHelper("int32_t refii_mod_subscribe(kind, fn, user, priority, ...) / void refii_mod_dispatch(kind, event)", "the bus underneath REFII_MOD_ON; a kind at or above REFII_EVENT_COUNT is a mod's own, raised with refii_mod_dispatch"),
		new ModApiHelper("uint32_t refii_mod_component(base, entity, id)", "component id of an entity (33 player, 36 health, 56 spells, 57 stats, 164 experience), or 0"),
		new ModApiHelper("int32_t refii_mod_entity_alive(base, entity)", "whether the entity is in the world"),
		new ModApiHelper("int32_t refii_mod_entity_position(ctx, base, entity, float out[3])", "the entity's position through its own placement getter"),
		new ModApiHelper("int32_t refii_mod_health(base, entity, float* current, float* max)", "current and maximum health"),
		new ModApiHelper("int32_t refii_mod_set_health(base, entity, value)", "writes current health, clamped to [0, max]"),
		new ModApiHelper("int32_t refii_mod_ability_level(base, entity, type)", "the level of ability 1..14 (1 Heavy Weapons, 2 Physique, 3 Toughness, 4 Ranged Weapons, 5 Accuracy, 6 Speed, 7 Lightning .. 14 Raise Dead), or -1"),
		new ModApiHelper("int32_t refii_mod_experience(base, entity, pool, float* out)", "the experience in a pool: 0 General 1 Strength 2 Skill 3 Will"),
		new ModApiHelper("int32_t refii_mod_ability_cost(ctx, base, entity, type, level)", "the game's own price of an ability level"),
		new ModApiHelper("int32_t refii_mod_give_experience(ctx, base, entity, pool, amount)", "adds (or takes, negative) experience through the game's Modify, outside the lifetime total"),
		new ModApiHelper("int32_t refii_mod_spend_experience(ctx, base, entity, pool, amount)", "spends through the game's Spend: a shortfall comes from General, unchecked"),
		new ModApiHelper("uint32_t refii_mod_cast_spell(ctx, base, caster, type, targeted, target, level, pos, dir)", "a real spell through the game's spell factory, no will cost; pos/dir may be null"),
		new ModApiHelper("uint32_t refii_mod_spell_serial(base, spell)", "a spell object's cast number"),
		new ModApiHelper("uint32_t refii_mod_live_spell(ctx, base, type, caster)", "the caster's live spell object of a type (never a Vortex)"),
		new ModApiHelper("uint32_t refii_mod_world(base)", "the world object"),
		new ModApiHelper("uint32_t refii_mod_entity_definition(base, entity)", "the GDB record hash of what the entity is"),
		new ModApiHelper("int32_t refii_mod_entity_name(base, entity, char* out, size)", "the entity's instance name"),
		new ModApiHelper("int32_t refii_mod_relation(ctx, base, entity, other)", "the game's faction rule: 1 neutral, 2 ally, 4 enemy"),
		new ModApiHelper("int32_t refii_mod_set_position(ctx, base, entity, xyz)", "moves the entity through its placement's own setter"),
		new ModApiHelper("int32_t refii_mod_entity_tint(ctx, base, entity, r, g, b, a) / refii_mod_entity_untint(ctx, base, entity)", EntityTintMeaning),
		new ModApiHelper("int32_t refii_mod_entity_alpha(ctx, base, entity, alpha) / refii_mod_entity_scale(ctx, base, entity, scale) / refii_mod_entity_saturation_mask(ctx, base, entity, on) / refii_mod_entity_drawable(ctx, base, entity, on)", AppearanceMeaning),
		new ModApiHelper("int32_t refii_mod_lock_target(ctx, base, player, entity) / refii_mod_release_lock(ctx, base, player) / uint32_t refii_mod_switch_target(ctx, base, player, left) / uint32_t refii_mod_locked_target(ctx, base, player)", LockMeaning),
		new ModApiHelper("int32_t refii_mod_apply_status(ctx, base, entity, kind, seconds, level, source) / refii_mod_clear_status(ctx, base, entity, kind) / refii_mod_has_status(base, entity, kind)", StatusMeaning),
		new ModApiHelper("int32_t refii_mod_knockback(ctx, base, entity, dir, force) / refii_mod_stagger(ctx, base, entity)", KnockbackMeaning),
		new ModApiHelper("int32_t refii_mod_set_speed_multiplier(base, player, factor) / refii_mod_clear_speed_multiplier(base, player) / refii_mod_speed_multiplier(base, player, float* out)", SpeedMeaning),
		new ModApiHelper("uint32_t refii_mod_current_action(base, entity) / int32_t refii_mod_action_elapsed(ctx, base, action, float* out) / refii_mod_action_interruptible(base, action) / refii_mod_allow_action_interrupt(base, action)", ActionMeaning),
		new ModApiHelper("int32_t refii_mod_action_type(base, action) / refii_mod_action_name(base, action, char* out, size) / const char* refii_mod_scriptable_action_name(type)", ActionKindMeaning),
		new ModApiHelper("int32_t refii_mod_signed_in_user(base) / refii_mod_save_count(ctx, base) / refii_mod_frontend_screen(ctx, base, char* out, size)", FrontEndMeaning),
		new ModApiHelper("int32_t refii_mod_entity_living(base, entity)", LivingMeaning),
		new ModApiHelper("int32_t refii_mod_spell_in_slot(base, player, slot)", SlotMeaning),
		new ModApiHelper("int32_t refii_mod_spell_parts(ctx, base, serial, refii_mod_cast_parts* out)", CastPartsMeaning + "; out: spell, type, targeted, level, caster, caster_player, vortex, anchor, swords[8], sword_count, origin[3], has_origin, exploded"),
		new ModApiHelper("int32_t refii_mod_play_sound_on(ctx, base, entity, cue, tag) / refii_mod_play_sound_at(ctx, base, xyz, cue, tag) / refii_mod_stop_sound(ctx, base, entity, tag, fade_ms) / refii_mod_play_music(ctx, base, name) / refii_mod_stop_music(ctx, base, name)", SoundMeaning),
		new ModApiHelper("int32_t refii_mod_play_animation(ctx, base, entity, name, loops, blend_seconds, speed) / refii_mod_stop_animation(ctx, base, entity)", AnimationMeaning),
		new ModApiHelper("int32_t refii_mod_modify_opinion(ctx, base, villager, hero, axis, delta) / refii_mod_add_deed(ctx, base, villager, hero, deed)", OpinionSetMeaning),
		new ModApiHelper("int32_t refii_mod_npc_follow(ctx, base, entity, target, radius) / refii_mod_npc_go_to(ctx, base, entity, xyz) / refii_mod_npc_flee(ctx, base, entity, from) / refii_mod_npc_attack(ctx, base, entity, target) / refii_mod_npc_set_hostile(ctx, base, entity, other, on) / refii_mod_npc_stop(ctx, base, entity)", NpcMeaning),
		new ModApiHelper("int32_t refii_mod_set_time_of_day(ctx, base, hours) / refii_mod_time_of_day(base, hours) / refii_mod_set_weather(ctx, base, theme, weight, seconds)", TimeWeatherMeaning),
		new ModApiHelper("int32_t refii_mod_modify_health(ctx, base, entity, delta, attacker)", "heals or hurts through the game's own Modify (death included)"),
		new ModApiHelper("int32_t refii_mod_level_name(base, char* out, size)", "the loaded level's name"),
		new ModApiHelper("int32_t refii_mod_gold(base, entity, int32_t* out) / refii_mod_give_gold(ctx, base, entity, delta)", "a purse, read and changed through the game's own Modify"),
		new ModApiHelper("int32_t refii_mod_item_count(base, entity, definition) / uint32_t refii_mod_add_item(ctx, base, entity, definition)", "an inventory, counted and added to through the game's own add"),
		new ModApiHelper("int32_t refii_mod_run_game_lua(ctx, base, chunk)", "runs a Lua chunk in the game VM"),
		new ModApiHelper("int32_t refii_mod_run_frontend_lua(ctx, base, chunk)", "runs a Lua chunk in the mods' front-end Lua (the title and the front end: the current Gameface presentation's own state, every mod's mod/frontend.lua run in it first); 0 when no presentation has one"),
		new ModApiHelper("int32_t refii_mod_game_lua_int(ctx, base, expr, lo, hi, int32_t* out)", "an integer Lua expression's value, by a search of assertions"),
		new ModApiHelper("uint32_t refii_mod_lua_generation()", "steps on every Lua rebuild and save load"),
		new ModApiHelper("int32_t refii_mod_save_set_int(ctx, base, key, value)", "RefiiModSave.<key> = value: persists with the save game"),
		new ModApiHelper("int32_t refii_mod_save_get_int(ctx, base, key, lo, hi, int32_t* out)", "reads RefiiModSave.<key>; do it on a generation change"),
		new ModApiHelper("refii::mods::Publish(key, value) / Published(key, out) / Setting(key, fallback)", "mod values to Godot (\"mod:<key>\" in the UI), another mod's values, and settings.json"),
		new ModApiHelper("refii::mods::LoadU8/U16/U32/F32, StoreU8/U32/F32 (base, address)", "big-endian guest memory"),
		new ModApiHelper("refii::mods::CallGuest(ctx, base, address) / CallVirtual(ctx, base, object, slot)", "guest calls; Frame(ctx) for one that needs stack"),
		new ModApiHelper("refii::mods::GetVector(ctx, 1|2, out[3]) / SetVector(ctx, 1|2, xyz)", "guest vector registers v1 / v2: positions and directions"),
		new ModApiHelper("REFII_MOD_ON(Kind, fn) / REFII_MOD_ON_PRIORITY(Kind, fn, p)", "subscribes void fn(refii::mods::KindEvent&)"),
		new ModApiHelper("REFII_MOD_ON_TICK(fn) / refii_mod_register_tick(fn)", "the plain per-tick callback (the Tick event is the same moment)"),
		new ModApiHelper("REX_EXTERN(name) { ... } + REFII_MOD_ORIGINAL(name)", "a direct hook on a game function, for a seam no event covers"),
		new ModApiHelper("uint32_t refii_mod_handle(address) / refii_mod_handle_address(handle)", "the handle Lua and Godot hold for an object, and back"),
		new ModApiHelper("uint32_t refii_mod_fnv1(text)", "the game's FNV-1 of a name: a GDB record's hash, a text tag's key"),
		new ModApiHelper("int32_t refii_mod_renown / refii_mod_morality / refii_mod_purity(ctx, base, entity, out)", "the game's own getters: renown (with bonuses), morality and purity (clamped)"),
		new ModApiHelper("int32_t refii_mod_physique(base, entity, float* fat, float* height) / refii_mod_age(base, entity, float* years)", "physique from the stats component, age from the age component (122)"),
		new ModApiHelper("int32_t refii_mod_inventory_stack(base, entity, index, uint32_t* definition, int32_t* count)", "stack index of an inventory, in the game's order; false past the end"),
		new ModApiHelper("int32_t refii_mod_equipped(base, entity, uint32_t* melee, uint32_t* ranged)", "the weapon set's melee and ranged definition hashes"),
		new ModApiHelper("int32_t refii_mod_entities_near(ctx, base, xyz, radius, creatures_only, out[], max)", "entities within a radius, nearest first"),
		new ModApiHelper("int32_t refii_mod_line_of_sight(ctx, base, from, to)", "the game's physics raycast: true when nothing is between"),
		new ModApiHelper("uint32_t refii_mod_spawn(ctx, base, definition, xyz)", "an entity created through the game's own CreateEntityFromIDAtPosition"),
		new ModApiHelper("uint32_t refii_mod_play_fx(name, xyz, options) / refii_mod_play_fx_on(name, entity, options)",
			"an effect of the particle bank or a mod's fx/effects/, drawn by Godot at a world position or riding an entity in its own frame (it turns with the entity; offset along the entity's axes); " +
			"options (refii_mod_fx_options, null for refii_mod_fx_default_options()): duration, scale, tint, intensity, loop, offset, at (a model helper: " +
			"the effect stands at that .mdl.gmd point in its own frame, on a rigged body on its bone; refii_mod_play_fx_at(name, entity, helper, options) is the same); returns the id. Any thread"),
		new ModApiHelper("void refii_mod_stop_fx(id)", "stops a played effect by any surface's id (native, Lua or Godot)"),
		new ModApiHelper("uint32_t refii_mod_mark(player, entity, xyz, icon, text) / refii_mod_unmark(id)", MarkMeaning + " Any thread"),
		new ModApiHelper("int32_t refii_mod_fade(player, to_black, seconds)", FadeMeaning + " Any thread"),
		new ModApiHelper("int32_t refii_mod_meter(player, id, label, value, max, style) / refii_mod_meter_remove(id)", MeterMeaning + " Any thread"),
		new ModApiHelper("uint32_t refii_mod_light(entity, xyz, rgb, energy, range, options) / refii_mod_light_remove(id)",
			LightMeaning + " options (refii_mod_light_options, null for none): offset, at (a model helper, as play_fx_on's), flicker; direction, cone, attenuation for a spot. Any thread"),
		new ModApiHelper("int32_t refii_mod_send_to_partner(mod, name, payload)", PartnerMeaning + " Any thread"),
		new ModApiHelper("int32_t refii_mod_set_menu_rows(player, menu, rows)",
			"states a menu list's rows for player 1 or 2 (0: every player) — what a ui/templates.json menu or entry with \"source\": \"rows\" " +
			"(or rows:<list>) shows, at once if open; rows is one text, \"name|label|command|ghosted\" split by ';' ('\\' escapes), " +
			"command any row verb (Mod:lua:, Opens:, Screen:open:); \"\" clears. Any thread"),
		new ModApiHelper("int32_t refii_mod_open_screen(player, screen)",
			"opens a host-owned screen by id for player 1 or 2 (0: the menu owner here) — the Screen:open:<id> verb: the game's (1013 " +
			"achievements, 1014 multiplayer) or a ui/templates.json screens record a mod states (id 1000+, a menu and a presentation, " +
			"filled by its provider — \"data\" binds fields to mod:/player: values); it stands over the pause book. Any thread"),
		new ModApiHelper("int32_t refii_mod_open_menu(player, name)",
			"opens a menu a mod defines in ui/templates.json for player 1 or 2 (0: whoever owns the menus here) over the list that has the input, " +
			"or on its own with none up — in play or on the title (it takes the pad; B at its root closes it); shown only where that player is played. Any thread"),
		new ModApiHelper("uint32_t refii_mod_message_box(player, title, text, accept, cancel)",
			"the game's own box for player 1 or 2 (0: the menu owner here) — in game the query or message box, on the front end its dialog: " +
			"a question with cancel non-null (null or \"\" captions keep the box's own), else a notice; returns the id mod_message_box carries. Any thread"),
		new ModApiHelper("uint32_t refii_mod_notice(player, title, text, seconds)",
			"a notice for player 1 or 2 (0: the menu owner here) on the notice channel the game's co-op notices use — in game the HUD's info box " +
			"for seconds (0: the channel's length), on the front end its dialog; notices queue; returns the id mod_notice carries (0 shown, -1 never). Any thread"),
		new ModApiHelper("uint32_t refii_mod_menu_box(player, title, text, options, count, cancelable)",
			"the game's own menu box (the Golden Cock's) for player 1 or 2 (0: the menu owner here), on the front end and in game: up to 16 options, " +
			"paged past six; B closes a cancelable box; returns the id mod_menu_box carries with the option's index (-1 none). Any thread"),
	};

	/// <summary>What mark means on every surface (Engine_HudModMarkers).</summary>
	public const string MarkMeaning =
		"a marker on the HUD's world-icon channel over an entity or at a world point for player 1 or 2 (0: every player this machine plays): " +
		"icon a game texture path (art/gui/.../x.tex) or an enabled mod's PNG (art/x.png), text a line under it; it rides the drawn body, is placed " +
		"as the game's own icons are (each player's camera and half) and sized by the game's distance curve (sub_821AA758); returns the id unmark takes; " +
		"a marker on an entity goes with the entity.";

	/// <summary>What fade means on every surface (Engine_UiModFade).</summary>
	public const string FadeMeaning =
		"fades player 1 or 2's screen (0: every player this machine plays) to black or back over seconds (0: at once): a host-drawn layer over that " +
		"player's half (their whole window alone or online), independent of the game's own fade, which level changes own.";

	/// <summary>What meter means on every surface (Engine_HudModMeters).</summary>
	public const string MeterMeaning =
		"shows or restates the HUD meter id for player 1 or 2 (0: every player this machine plays): label above a bar at value of max (a timer, a boss bar) " +
		"in style \"bar\" (the default), \"boss\" (wide, red) or \"timer\" (narrow) — the HUD's own primitives — or in the game's own meter art, " +
		"\"generic\" (hud.bgf's GenericMeter: the ornate bar), \"crucible\" (CrucibleMeter: the plain red bar) or \"price\" (PriceWidget: the bowl, the value as a number), " +
		"a copy lit on its own fader slide while the shipped original stays as authored; stacked under the top centre of that player's screen in the order first " +
		"shown; the same id restates it in place; meter_remove takes it down.";

	/// <summary>What light means on every surface (Engine_ModLights).</summary>
	public const string LightMeaning =
		"an omni light of a colour at an energy (1: a placed lamp's) over a range in metres (0: 6), on an entity in its own frame — riding the body, " +
		"at a model helper when one is named, gone with the entity — or at a world point (guest axes); placed as play_fx_on places an effect " +
		"(Engine_FxPlayer.Mount). A SPOT when a cone is stated (degrees from the axis to the edge, at most 89): it shines along the stated direction " +
		"in the offset's axes (the entity's or the helper's — it turns with them — the world's for a point), the attenuation its edge falloff " +
		"(0 or 1 linear, the title's own). Modern: a Godot light like the FX feed's own (zero decay, no shadow map); classic: a point light — or a " +
		"spot light with that direction and cone — appended to the guest's light list for every draw its sphere meets; returns the id light_remove " +
		"takes; everything goes when the game goes down.";

	/// <summary>What send_to_partner means on every surface (the route's mod lane).</summary>
	public const string PartnerMeaning =
		"sends a mod's own message — name (a word of letters, digits, '_', '.', '-'; at most 32) and payload (any text, at most 256 bytes) from mod — " +
		"to THE OTHER MACHINE of an online session, where it is raised as partner_message (mod, name, payload, from: the player the sender plays) " +
		"once and in the order sent, on the route the game's own cross-machine calls take (core/event_relay.h pm: lines); either machine may send; " +
		"nothing is sent alone or in local co-op (false), for a bad name, or a payload over the limit.";

	/// <summary>The Lua-side calls the init chunk's preamble defines.</summary>
	public static readonly IReadOnlyList<ModApiHelper> LuaHelpers = new[]
	{
		new ModApiHelper("Refii.on(event, function(e) ... end)", "subscribes; e is a table of the event's fields; return true to cancel; assigning a writable field (e.damage, e.delta, e.amount, e.level, e.type, e.relation) changes what the game gets. An event raised while a handler runs is delivered nested and live when it has writable fields (the hit a Refii.cast_spell's Start lands: your e.damage reaches the game; one level of nesting, per-depth write target and cancel), else after that chunk returns with e.deferred = true (a Refii.cast_spell's spell_created, the bolt's hit_landed): the game has acted, so a cancel there has no effect"),
		new ModApiHelper("Refii.publish(key, number) / Refii.value(key)", "a mod value Godot reads as \"mod:<key>\" (HUD pieces, FX recipes), and the last value under a key"),
		new ModApiHelper("Refii.log(text)", "a line in the guest log, prefixed [mod lua]"),
		new ModApiHelper("Refii.hash(name)", "the game's FNV-1 of a name as a \"0x…\" string: a GDB record's hash, a text tag's key"),
		new ModApiHelper("Refii.health(e) -> current, max / Refii.set_health(e, v) / Refii.modify_health(e, delta, attacker)", "health, read, written, or changed through the game's own Modify (death included)"),
		new ModApiHelper("Refii.position(e) -> x, y, z / Refii.set_position(e, x, y, z)", "a position through the entity's own placement"),
		new ModApiHelper("Refii.entity_tint(e, r, g, b [, a]) / Refii.entity_untint(e)", EntityTintMeaning),
		new ModApiHelper("Refii.entity_alpha(e, a) / Refii.entity_scale(e, s) / Refii.entity_saturation_mask(e, on) / Refii.entity_drawable(e, on)", AppearanceMeaning),
		new ModApiHelper("Refii.lock_target(p [, e]) / Refii.release_lock(p) / Refii.switch_target(p, left) -> target / Refii.locked_target(p) -> target", LockMeaning),
		new ModApiHelper("Refii.apply_status(e, kind, seconds [, level [, source]]) / Refii.clear_status(e, kind) / Refii.has_status(e, kind)", StatusMeaning),
		new ModApiHelper("Refii.knockback(e, x, y, z [, force]) / Refii.stagger(e)", KnockbackMeaning),
		new ModApiHelper("Refii.set_speed_multiplier(p, factor) / Refii.clear_speed_multiplier(p) / Refii.speed_multiplier(p) -> overridden, factor", SpeedMeaning),
		new ModApiHelper("Refii.current_action(e) -> action / Refii.action_elapsed(a) -> seconds / Refii.action_interruptible(a) / Refii.allow_action_interrupt(a)", ActionMeaning),
		new ModApiHelper("Refii.action_type(a) -> type / Refii.action_name(a) -> name", ActionKindMeaning),
		new ModApiHelper("Refii.living(e)", LivingMeaning),
		new ModApiHelper("Refii.spell_in_slot(p, slot) -> type", SlotMeaning),
		new ModApiHelper("Refii.spell_parts(serial) -> table", CastPartsMeaning + "; the table: spell, type, targeted, level, caster, caster_player, vortex, anchor, swords = { .. }, origin = { x, y, z }, exploded (absent parts nil)"),
		new ModApiHelper("Refii.play_sound(cue, e [, tag]) / Refii.play_sound_at(cue, x, y, z [, tag]) / Refii.stop_sound(e, tag [, fade_ms]) / Refii.play_music(name) / Refii.stop_music(name)", SoundMeaning),
		new ModApiHelper("Refii.play_animation(e, name [, loops [, blend_seconds [, speed]]]) / Refii.stop_animation(e)", AnimationMeaning),
		new ModApiHelper("Refii.modify_opinion(villager, hero, axis, delta) / Refii.add_deed(villager, hero, deed)", OpinionSetMeaning + "; deed is a \"0x…\" string (Refii.hash of the Deed record's name)"),
		new ModApiHelper("Refii.npc_follow(e, target [, radius]) / Refii.npc_go_to(e, x, y, z) / Refii.npc_flee(e, from) / Refii.npc_attack(e, target) / Refii.npc_set_hostile(e, other, on) / Refii.npc_stop(e)", NpcMeaning),
		new ModApiHelper("Refii.set_time_of_day(hours) / Refii.time_of_day() -> hours / Refii.set_weather(theme [, weight [, seconds]])", TimeWeatherMeaning + "; weight defaults to 1, seconds to 1"),
		new ModApiHelper("Refii.gold(e) / Refii.give_gold(e, delta)", "a purse, read and changed through the game's own Modify"),
		new ModApiHelper("Refii.item_count(e, definition) / Refii.add_item(e, definition)", "an inventory: definitions are \"0x…\" strings (Refii.hash of the record's name)"),
		new ModApiHelper("Refii.experience(e, pool) / Refii.give_experience(e, pool, amount) / Refii.spend_experience(e, pool, amount)", "experience: pools 0 General 1 Strength 2 Skill 3 Will"),
		new ModApiHelper("Refii.ability_level(e, type)", "the level of ability 1..14, or nil"),
		new ModApiHelper("Refii.cast_spell(caster, type, targeted, target, level [, pos [, dir]]) -> spell, serial / Refii.spell_serial(spell) -> serial", "a real spell through the game's spell factory, no will cost, started at pos = {x, y, z} facing dir (the caster's own when absent: Fable 2's targeted classes place from the position along the facing — Lightning draws its bolt from there, Fireball spawns its shot there, Force Push shoves away from there); the spell object's handle and its cast serial (the number its hits and spell_created carry), nil for no cast; spell_serial reads the serial of a spell handle — the serial, not the handle, is a cast's identity (a spell handle is never forgotten)"),
		new ModApiHelper("Refii.relation(a, b) / Refii.alive(e) / Refii.definition(e) / Refii.name(e) / Refii.player_of(e)", "the faction rule (1 neutral 2 ally 4 enemy), liveness, the definition hash, the instance name, 1/2/0"),
		new ModApiHelper("Refii.level_name() / Refii.player_hero(p) / Refii.local_hero() / Refii.menu_player()", "the loaded level, a player's hero, the local hero (in local co-op: the hero of whoever owns the open menus), whose menus are open (1/2)"),
		new ModApiHelper("Refii.player_value(p, key [, fallback]) / Refii.player_set(p, key, v) / Refii.player_add(p, key, d [, saved]) / Refii.player_save(p, key, v) / Refii.player_here(p) / Refii.event_replica()", "per-player values, the same on both co-op machines; saved ones go home with the hero"),
		new ModApiHelper("Refii.player_for(e)", PlayerForMeaning + " — the core co-op layer's own rule for who an NPC acts toward"),
		new ModApiHelper("Refii.acting_player()", "1 or 2: who \"the hero\" (GetPlayerHero) means right now — 2 inside Refii.run_as_player(2, …) and in AI acting for player two, else 1"),
		new ModApiHelper("Refii.run_as_player(p, fn, ...)", "runs fn with GetPlayerHero(), GetPlayerHenchman() and QuestManager.HeroEntity as player p's (the partner as the henchman): a shipped script acts for either hero"),
		new ModApiHelper("Refii.combat_state(p) -> midst, close_to_combat, in_any_combat", "player p's combat state, kept per hero (the shipped global is one state)"),
		new ModApiHelper("Refii.show_to_player(p, name, ...)", "runs the function a global path names (\"GUI.DisplayMessageBox\", \"GUI.AskYesNoQuestion\", \"TutorialManager.DisplayTutorial\", any) for player p on player p's screen: on the other machine when p is played there (a question waits and returns p's answer), else here as p (split screen: p's half). The game's own box, camera, HUD and Player.* calls made while acting as player p (Refii.run_as_player) follow p by themselves"),
		new ModApiHelper("Refii.forgive_crimes(villager, p) / Refii.discovered_crime(villager, p) / Refii.perceived_renown(villager, p)", "a villager's crimes and renown for player p's hero: forgives only p's crimes it knows of (how many), whether it discovered one of p's, the renown it credits p with (nil for no villager). Villager.ForgiveHerosCrimes / HasHeroCommittedCrimeIHaveDiscovered / GetPerceivedRenownOfHero answer for the acting player through these"),
		new ModApiHelper("Refii.world_here()", "true where the shared world runs (alone, local co-op, the online host): make world effects only there"),
		new ModApiHelper("Refii.split_screen()", "true when local co-op runs on split screen (each local player their own camera)"),
		new ModApiHelper("Refii.player_dog(p)", "a player's dog: player one's, or player two's co-op dog bound to them; nil when none. Use this, never the game's GetDog(): inside player two's dog's own AI and item use, GetDog() answers that dog"),
		new ModApiHelper("Refii.opinion(villager, p) -> love, fear, attractiveness, morality, respect", "how a villager feels about player p's hero (-1..1); a set per hero; nil when it holds no opinions"),
		new ModApiHelper("Refii.uid(e)", "the entity's own 64-bit UniqueID as \"0x…\": the same on both co-op machines and across saves (a handle is neither) — a key for per-thing player values"),
		new ModApiHelper("Refii.coop_mode()", "0 alone, 1 local co-op, 2 online and this machine hosts, 3 online and this machine joined"),
		new ModApiHelper("Refii.wheel_switches_target(e)", WheelSwitchesTargetMeaning),
		new ModApiHelper("Refii.focus(p) / Refii.key_taken(e, p)", "the living entity player p is focused on, or nil — meaningful only where that hero is played (Refii.player_here(p)); whether player p has taken the per-player collectable e (silver keys)"),
		new ModApiHelper("Refii.ask_partner(asker, question) -> ticket / Refii.partner_answer(ticket) -> nil, true, false", "your own yes/no question for the other player, on their screen; nil while pending, 30 s unanswered is no; no partner answers yes"),
		new ModApiHelper("Refii.renown(e) / Refii.morality(e) / Refii.purity(e) / Refii.age(e) / Refii.physique(e) -> fat, height", "the hero stats the game reports"),
		new ModApiHelper("Refii.inventory(e)", "{ {definition = \"0x…\", count = n}, … } in the game's order"),
		new ModApiHelper("Refii.equipped(e) -> melee, ranged", "the weapon set's definition hashes, nil for an empty hand"),
		new ModApiHelper("Refii.near(x, y, z, radius [, creatures_only])", "handles of the entities within a radius, nearest first (at most 2048)"),
		new ModApiHelper("Refii.line_of_sight(x1, y1, z1, x2, y2, z2)", "true when the game's physics sees nothing between"),
		new ModApiHelper("Refii.spawn(definition, x, y, z)", "a new entity through the game's own CreateEntityFromIDAtPosition: its handle, or nil"),
		new ModApiHelper("Refii.entity(handle) / Refii.handle(entity)", "the game's own Entity userdata for a handle (for the shipped scripts and natives), and back"),
		new ModApiHelper("Refii.play_fx(name, x, y, z [, options]) / Refii.play_fx_on(e, name [, options])",
			"an effect of the particle bank or a mod's fx/effects/, drawn by Godot at a world position or riding an entity handle in its own frame (it turns with the entity; offset along the entity's axes); " +
			"options { duration = s, scale = n, tint = {r, g, b}, intensity = n, loop = false, offset = {x, y, z}, at = \"Weapon.Weapon.Top\" (a model helper: " +
			"the effect stands at that .mdl.gmd point in its own frame, on a rigged body on its bone) }; the id, or nil"),
		new ModApiHelper("Refii.stop_fx(id)", "stops a played effect by any surface's id"),
		new ModApiHelper("Refii.mark(target, icon, text [, p]) / Refii.unmark(id)", MarkMeaning + " target is an entity handle or {x, y, z}"),
		new ModApiHelper("Refii.fade(p, dir [, seconds])", FadeMeaning + " dir is \"to_black\" or \"from_black\" (\"out\" / \"in\")"),
		new ModApiHelper("Refii.meter(id, label, value, max [, p [, style]]) / Refii.meter_remove(id)", MeterMeaning),
		new ModApiHelper("Refii.light(target, color, energy, range [, o]) / Refii.light_remove(id)",
			LightMeaning + " target an entity handle or {x, y, z}, color {r, g, b}, o = { offset = {x, y, z}, at = \"Weapon.Weapon.Top\", flicker = n, direction = {x, y, z}, cone = degrees, attenuation = n }; the id, or nil"),
		new ModApiHelper("Refii.send_to_partner(mod, name, payload)", PartnerMeaning),
		new ModApiHelper("Refii.set_menu_rows(menu, rows [, p])", "states a menu list's rows for player p (nil: every player): strings or { name, label, command, ghosted } — what a menu or entry with \"source\": \"rows\" shows, at once if open; {} clears; true when posted"),
		new ModApiHelper("Refii.open_screen(id [, p])", "opens a host-owned screen (Screen:open:<id>) for player p: the game's or a screens record a mod states (id 1000+); true when posted"),
		new ModApiHelper("Refii.open_menu(name [, p])", "opens a mod's menu (ui/templates.json) for player p (nil: the menu owner here) over the list that has the input, or on its own with none up; true when asked"),
		new ModApiHelper("Refii.message_box(title, text [, accept [, cancel [, p]]])", "the game's own box for player p: a question when cancel is given (nil captions keep the box's own), else a notice; the id mod_message_box carries"),
		new ModApiHelper("Refii.notice(title, text [, seconds [, p]])", "a notice on the notice channel for player p (nil: the menu owner here): the HUD's info box in game, queued behind the others; the id mod_notice carries (0 shown, -1 never)"),
		new ModApiHelper("Refii.signed_in() -> user / Refii.save_count() -> n / Refii.screen() -> name", "FRONT END ONLY (mod/frontend.lua): " + FrontEndMeaning + "; nil for none. With Refii.hash, setting, log, publish / value, notice, message_box, menu_box, open_menu, menu_player and Refii.on for start_game, mod_menu_row, mod_message_box, mod_notice, mod_menu_box — the whole front-end surface (docs/mod_api.md, the front-end table)"),
		new ModApiHelper("Refii.menu_box(title, text, options [, p [, cancelable]])", "the game's own menu box for player p: up to 12 options from the table, paged past six; B closes it unless cancelable is false; the id mod_menu_box carries with the option's index (-1 none)"),
		new ModApiHelper("Refii.setting(key, default)", "a settings.json value with the player's choice over it, read at the call: RefiiSettings, composed in at session start and kept current — a live change (the Options › Mods page, a script's set_setting, the Studio's live tune) reaches the running VM at the next guest tick, no reload"),
		new ModApiHelper("require(name)", "a mod's own module: mod/<name>.lua of the same mod (the path under mod/ without .lua; . or / between folders), composed into the init chunk and run once on the first require, its return value cached (nil as true); an error names a missing file. Only the three entry points (init.lua, camera.lua, frontend.lua) run on their own"),
		new ModApiHelper("Refii.hero(player)", "the player's hero handle as the bus last stated it, or nil"),
		new ModApiHelper("Refii.api_version", "the API version the chunk was composed for"),
		new ModApiHelper("RefiiModSave.<key>", "state that persists with the save game"),
		new ModApiHelper("RefiiModErrors", "every handler and init error, appended"),
	};

	/// <summary>
	/// The Godot script surface, <c>Engine.get_singleton("RefiiHost")</c> (Engine_RefiiHost.cs): its
	/// members and the <c>query</c> verbs (mod_commands.cpp's grammar), each answering on
	/// <c>reply(id, result)</c> with <c>ok</c> and the named fields. Entities are handles, as in Lua.
	/// A signature's " / "-separated parts name methods (<see cref="GodotMethodNames"/>), except
	/// those that start "signal ", "query " or "e." and a part's "-> result".
	/// </summary>
	public static readonly IReadOnlyList<ModApiHelper> GodotHelpers = new[]
	{
		new ModApiHelper("signal <event>(e) / signal mod_event(name, e)", "every catalog event, by its Lua name, with a Dictionary of its fields"),
		new ModApiHelper("on_verdict(event, handler) / remove_verdict(event, handler)", "a handler run inside the game's seam: change a writable field of e or set e.cancel (return true) to refuse"),
		new ModApiHelper("hero(player)", "the player's hero handle as the bus last stated it (hero_ready / hero_gone), or 0 — player 1 is the host's hero on BOTH machines, so on a joiner hero(1) is the partner"),
		new ModApiHelper("local_hero() / coop_mode() / menu_player() / world_here()", "the guest's last answers (asked every frame while a script is loaded, one guest tick behind): the local player's hero handle (in local co-op the menu owner's), 0 alone 1 local co-op 2 hosting 3 joined, whose menus are open (1/2), whether this machine runs the shared world"),
		new ModApiHelper("e.replica", "true in a signal's (or verdict's) dictionary when the event is a copy of the other co-op machine's — the game's own copy or a RELAYED one; absent otherwise"),
		new ModApiHelper("player_set(p, key, v) / player_add(p, key, d, saved) / player_save(p, key, v)", "per-player values, written to the store Refii.player_set / _add / _save write: the same on both co-op machines; saved ones go home with the hero"),
		new ModApiHelper("node_for(handle)", "the Node3D Godot draws that entity with, or null: parent to it, never move it"),
		new ModApiHelper("get_value(key) / setting(key, default) / set_setting(key, v) / run_lua(chunk) / frontend_lua(chunk) / mods()", "a mod's published value, a settings.json value, a live override (in force for native code at once and for the running Lua's Refii.setting at the next guest tick), a Lua chunk in the game VM, a Lua chunk in the mods' front-end Lua (the title and the front end, every mod's mod/frontend.lua in scope), the enabled mods"),
		new ModApiHelper("publish(key, value)", "sets a mod value (\"mod:<key>\" in the UI) on the guest's next tick, for every reader: HUD pieces, menus, Lua and native"),
		new ModApiHelper("set_menu_rows(menu, rows, player = 0)", "states a menu list's rows for player 1 or 2 (0: every player), replacing the last: what a ui/templates.json menu or entry with \"source\": \"rows\" (or rows:<list>) shows, at once if it is open. A row is a string or { name, label, command, ghosted }; label a Babel tag or literal, command any row verb; [] clears. \"source\": \"values:mod:<key>|<format>=<label>,...\" lists published values instead"),
		new ModApiHelper("open_screen(screen, player = 0) -> bool", "opens a host-owned screen by id — the Screen:open:<id> verb any row can name: the game's (1013 achievements, 1014 multiplayer) or a ui/templates.json screens record a mod states (id 1000+, with a menu and a presentation; provider \"data\" fills its page from the record's fields, bound to mod:/player: values, row:label, Babel tags). It stands over the pause book, so it opens while the pause list has the input; false says why in the log"),
		new ModApiHelper("register_command(verb, handler) -> bool", "a row verb every list runs (the front end, the pause book, screen lists, mod menus, a screen's own buttons): a row whose command is verb — or begins with it when it ends in ':' — calls handler(argument, menu, row, player); false refuses the row. The game's verbs (PauseTab:, Screen:open:, Opens:, Mod:lua:, setting:, Inventory: ...) stay the game's"),
		new ModApiHelper("open_menu(name, player = 0) -> bool", "opens a menu a mod defines in ui/templates.json for player 1 or 2 (0: whoever owns the menus here) over the menu list that has the input — B returns — or, with no list up (in play, on the title), as a list of its own that takes the pad and closes on B. False for no such menu or a player played on the other machine. A row chosen there raises mod_menu_row here at once, and reaches native handlers (and Lua in a game) on the guest's next tick through the menu_row command"),
		new ModApiHelper("message_box(title, text, buttons, player = 0) -> id / signal message_box_answered(id, button)", "the game's own box for player 1 or 2 (0: the menu owner here) — in game the message box (one button) or query box (two), on the front end its own dialog; more than two buttons is a menu box answered here with the button's index; button is 0 for A, 1 for B, -1 when another box replaced it or the player is played on the other machine. The mod_message_box event carries the same, on every surface"),
		new ModApiHelper("notice(title, text, seconds = 0, player = 0) -> id / signal notice_done(id, outcome)", "a notice on the channel the game's co-op notices use — in game the HUD's info box for seconds (0: its own length), on the front end its dialog — queued behind the others; outcome 0 once shown, -1 when it never showed. The mod_notice event carries the same, on every surface"),
		new ModApiHelper("menu_box(title, text, options, player = 0, cancelable = true) -> id / signal menu_box_answered(id, option)", "the game's own menu box (the Golden Cock's) on the front end and in game, for player 1 or 2 (0: the menu owner here): up to 16 options, paged past six; option is the index chosen, -1 for B, a box replacing it or a player played on the other machine. The mod_menu_box event carries the same, on every surface"),
		new ModApiHelper("give_gold / modify_health / set_health / add_item / give_experience / spend_experience / cast_spell / cast_spell_at(caster, type, targeted, target, level, pos, dir) / set_position / save_set", "helper calls, run in the guest tick with the real context; silent when there is nothing to act on; cast_spell_at starts the spell at pos (Vector3, guest axes) facing dir, as refii_mod_cast_spell's pos / dir"),
		new ModApiHelper("entity_tint(handle, r, g, b, a = 1) / entity_untint(handle)", EntityTintMeaning),
		new ModApiHelper("entity_alpha(handle, alpha) / entity_scale(handle, scale) / entity_saturation_mask(handle, on) / entity_drawable(handle, on)", AppearanceMeaning),
		new ModApiHelper("lock_target(player, entity = 0) / release_lock(player) / switch_target(player, left)", LockMeaning + "; query locked_target P answers the target held"),
		new ModApiHelper("wheel_switches_target(handle, on = true) -> bool", WheelSwitchesTargetMeaning + ". STICKY for a Godot script (the command wheel_switches_target E ON): a script runs off the guest tick, so the tick restates it every tick until on = false or the hero's player no longer holds a lock"),
		new ModApiHelper("apply_status(handle, kind, seconds, level = 0, source = 0) / clear_status(handle, kind)", StatusMeaning + "; query has_status E KIND asks"),
		new ModApiHelper("knockback(handle, x, y, z, force = 1) / stagger(handle)", KnockbackMeaning),
		new ModApiHelper("set_speed_multiplier(player, factor) / clear_speed_multiplier(player)", SpeedMeaning + "; query speed_multiplier P answers overridden and factor"),
		new ModApiHelper("allow_action_interrupt(action)", ActionMeaning + "; query current_action E answers action, elapsed, interruptible"),
		new ModApiHelper("play_sound(cue, handle, tag = \"\") / play_sound_at(cue, x, y, z, tag = \"\") / stop_sound(handle, tag, fade_ms = 0) / play_music(name) / stop_music(name)", SoundMeaning),
		new ModApiHelper("play_animation(handle, name, loops = 1, blend_seconds = 0.1, speed = 1) / stop_animation(handle)", AnimationMeaning),
		new ModApiHelper("modify_opinion(villager, hero, axis, delta) / add_deed(villager, hero, deed)", OpinionSetMeaning + "; deed is the record's hash, a \"0x…\" string or a number"),
		new ModApiHelper("npc_follow(entity, target, radius = 2) / npc_go_to(entity, x, y, z) / npc_flee(entity, from) / npc_attack(entity, target) / npc_set_hostile(entity, other, on) / npc_stop(entity)", NpcMeaning),
		new ModApiHelper("set_time_of_day(hours) / set_weather(theme, weight = 1, seconds = 1)", TimeWeatherMeaning + "; query time_of_day answers hours"),
		new ModApiHelper("query locked_target P / has_status E KIND", "target (0 when not locked) · has: refii_mod_locked_target / refii_mod_has_status"),
		new ModApiHelper("query speed_multiplier P / current_action E", "overridden, factor · action (0 for none), elapsed, interruptible: refii_mod_speed_multiplier / refii_mod_current_action, _action_elapsed, _action_interruptible"),
		new ModApiHelper("query action A / frontend", "type, name (ok false for an unknown action) · signed_in, saves, screen (ok false with no front end up): " + ActionKindMeaning + " / " + FrontEndMeaning),
		new ModApiHelper("query living E / spell_in_slot P SLOT", "living · type (-1 none): " + LivingMeaning + " / " + SlotMeaning),
		new ModApiHelper("query spell_parts SERIAL", "spell, type, targeted, level, caster, caster_player, vortex, anchor, sword_count, s1..s8, x, y, z, has_origin, exploded: " + CastPartsMeaning),
		new ModApiHelper("query time_of_day", "hours (the game clock, 0..24): refii_mod_time_of_day"),
		new ModApiHelper("query(what, args) -> id / signal reply(id, result)", "asks the guest; the answer comes on a later frame with ok and the fields below"),
		new ModApiHelper("query health E / position E / gold E / item_count E DEF / experience E POOL / ability_level E T", "current, max · x, y, z · gold · count · amount · level"),
		new ModApiHelper("query relation A B / alive E / definition E / name E / level_name / hero P / player_of E", "relation · alive · definition · name · level · hero · player"),
		new ModApiHelper("query player_for E / world_here / combat_state P", "player (refii_mod_player_for: who \"the hero\" is for E) · here · midst, close_to_combat, in_any_combat (Refii.combat_state, asked of the game VM); acting_player and run_as_player are Lua's — reach them with run_lua"),
		new ModApiHelper("query renown E / morality E / purity E / physique E / age E / equipped E / inventory_stack E I", "renown · value · value · fat, height · age · melee, ranged · definition, count"),
		new ModApiHelper("query near X Y Z R [CREATURES [OFFSET]] / line_of_sight X1 Y1 Z1 X2 Y2 Z2 / spawn DEF X Y Z / lua_int EXPR LO HI", "total, offset, count, e1..eN (100 a reply) · clear · entity · value"),
		new ModApiHelper("query local_hero / coop_mode / menu_player / coop_state", "hero · mode · player · local_hero, coop_mode, menu_player: Refii.local_hero / coop_mode / menu_player, asked now"),
		new ModApiHelper("query player_value P KEY [FALLBACK] / player_here P", "value, has (ok false when neither the value nor a fallback is there) · here: Refii.player_value / player_here"),
		new ModApiHelper("query player_dog P / focus P / opinion E P / uid E", "dog · entity · love, fear, attractiveness, morality, respect · uid (\"0x…\"): Refii.player_dog / focus / opinion / uid"),
		new ModApiHelper("query discovered_crime E P / perceived_renown E P · forgive_crimes E P", "discovered · renown: Refii.discovered_crime / perceived_renown for villager E and player P · the command forgives player P's crimes villager E knows of (Refii.forgive_crimes); show_to_player is Lua's — reach it with run_lua"),
		new ModApiHelper("play_fx(name, x, y, z, options) / play_fx_on(name, handle, options)",
			"an effect of the particle bank or a mod's fx/effects/ as a FableFx node at a world position (guest axes) or riding an entity handle in its own frame (it turns with the entity; offset along the entity's axes); " +
			"options { duration, scale, tint (Color or [r, g, b]), intensity, loop, offset (Vector3 or [x, y, z]), at (a model helper name: " +
			"the effect stands at that .mdl.gmd point in its own frame, on a rigged body on its bone) }; the id, 0 for an unknown effect"),
		new ModApiHelper("stop_fx(id)", "stops a played effect by any surface's id; false when none plays under it"),
		new ModApiHelper("mark(target, icon, text = \"\", player = 0) -> id / unmark(id) / markers()", MarkMeaning + " target is an entity handle or a Vector3; markers() lists what stands"),
		new ModApiHelper("fade(player, to_black, seconds = 0) -> bool / fade_state(player)", FadeMeaning + " fade_state answers alpha (0 clear .. 1 black), target and seconds"),
		new ModApiHelper("meter(id, label, value, max, player = 0, style = \"\") -> bool / meter_remove(id) / meters()", MeterMeaning + " meters() lists what stands, in stack order"),
		new ModApiHelper("light(target, color, energy, range, options = null) -> id / light_remove(id) / lights()",
			LightMeaning + " target an entity handle or a Vector3, color a Color or [r, g, b], options { offset, at, flicker, direction, cone, attenuation }; lights() lists what stands (id, entity, r, g, b, energy, range, x, y, z, cone, dx, dy, dz)"),
		new ModApiHelper("send_to_partner(mod, name, payload = \"\") -> bool (the command partner_send) / signal partner_message(e)", PartnerMeaning + " The signal carries mod, name, payload, from"),
	};

	/// <summary>The RefiiHost method names one <see cref="GodotHelpers"/> signature states.</summary>
	public static IEnumerable<string> GodotMethodNames(string signature)
	{
		if (signature.StartsWith("query ", StringComparison.Ordinal))
		{
			yield break;
		}

		foreach (string raw in signature.Split(" / "))
		{
			string part = raw.Split(" ->")[0].Trim();
			if (part.Length == 0 || part.StartsWith("signal ", StringComparison.Ordinal) ||
				part.StartsWith("query ", StringComparison.Ordinal) || part.StartsWith("e.", StringComparison.Ordinal))
			{
				continue;
			}

			yield return part.Split('(')[0].Trim();
		}
	}

	/// <summary>The writable numeric fields of each event, as the Lua preamble's write-back needs them.</summary>
	public static IEnumerable<(int Kind, string Field)> WritableFields =>
		Events.SelectMany(e => e.Fields.Where(f => f.Writable && f.Name != "cancel").Select(f => (e.Kind, f.Name)));

	public static ModApiEvent? Find(string name) =>
		Events.FirstOrDefault(e =>
			string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(e.LuaName, name, StringComparison.OrdinalIgnoreCase));

	/// <summary>A native handler source for <paramref name="e"/>, ready to drop under native/.</summary>
	public static string NativeTemplate(ModApiEvent e, string mod)
	{
		string fields = string.Join("\n", e.Fields.Select(f => $"  //   e.{f.Name,-16} {f.Type,-18} {f.Meaning}{(f.Writable ? " (writable)" : string.Empty)}"));
		string cancel = e.Cancellable ? $"  // e.cancel = true;  // {e.Cancel}\n" : string.Empty;
		return
			$"// {mod} — a handler for the {e.Name} event.\n" +
			$"//\n// {e.Summary}\n// Seam: {e.Seam}\n" +
			"\n#include \"core/refii_mod.h\"\n\n" +
			"namespace {\n\n" +
			$"void On{e.Name}(refii::mods::{e.Name}Event& e) {{\n" +
			$"  // Fields:\n{fields}\n" +
			cancel +
			"  (void)e;\n" +
			"}\n\n" +
			$"REFII_MOD_ON({e.Name}, On{e.Name});\n\n" +
			"}  // namespace\n";
	}

	/// <summary>A Lua handler for <paramref name="e"/>, for a mod's init.lua.</summary>
	public static string LuaTemplate(ModApiEvent e)
	{
		string fields = string.Join("\n", e.Fields.Where(f => !f.Type.Contains('*') && !f.NativeOnly).Select(f => $"\t-- e.{f.Name}: {f.Meaning}"));
		string cancel = e.Cancellable ? $"\t-- return true  -- {e.Cancel}\n" : string.Empty;
		return $"Refii.on(\"{e.LuaName}\", function(e)\n{fields}\n{cancel}end)\n";
	}
}
