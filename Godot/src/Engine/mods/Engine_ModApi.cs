using System;
using System.Collections.Generic;
using System.Linq;

namespace Refii.Mods;

/// <summary>One field of a mod event, as the native struct and the Lua table both carry it.</summary>
public readonly record struct ModApiField(string Name, string Type, string Meaning, bool Writable = false);

/// <summary>
/// One event of the mod API: its name in C++ (<c>&lt;Name&gt;Event</c>, <c>REFII_MOD_ON(Name, fn)</c>)
/// and in Lua (<c>Refii.on("lua_name", fn)</c>), the numeric kind the bus carries, the game
/// seam it is raised from, its fields, and what cancelling means (empty when it cannot be).
/// </summary>
public sealed record ModApiEvent(
	string Name,
	string LuaName,
	int Kind,
	string Seam,
	string Summary,
	string Cancel,
	IReadOnlyList<ModApiField> Fields)
{
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
	public const int Version = 1;

	private static ModApiField F(string name, string type, string meaning, bool writable = false) =>
		new(name, type, meaning, writable);

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
	};

	/// <summary>Every event, in kind order. The kinds are <c>refii_mod_event_kind</c>'s.</summary>
	public static readonly IReadOnlyList<ModApiEvent> Events = new[]
	{
		new ModApiEvent("Tick", "tick", 0, "the guest tick (RunModTicks), after the game's own tick work",
			"Once per game step, menus included. Lua handlers get it at most ten times a second, with the seconds accumulated.",
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
				F("ability", "uint32_t", "the strike ability (native)"),
				F("action", "uint32_t", "the live strike action the action manager (component 49) now runs, or 0 (native)"),
			}),
		new ModApiEvent("Roll", "roll_can_start", 8, "rex_CAbilityActionStrafeRoll_CanStart (r4 entity)",
			"The dodge roll asks whether it may start; asked once per matching roll record, so it gates without spending.",
			"the roll does not start",
			new[] { F("entity", "uint32_t", "the roller"), F("player", "int32_t", "1 or 2 for a hero, else 0") }),
		new ModApiEvent("RollStarted", "roll_started", 9, "rex_CAbilityActionStrafeRoll_Start (r4 entity), when it returned 1",
			"A roll was queued. Rolls are invulnerable for their whole length in the game already.",
			string.Empty,
			new[] { F("entity", "uint32_t", "the roller"), F("player", "int32_t", "1 or 2 for a hero, else 0") }),
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
			"An animation event reached an action; ALLOW_ACTION_INTERRUPT is the one that lets the next action in.",
			"the action never sees the event",
			new[]
			{
				F("action", "uint32_t", "the live action"),
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
			Hit),
		new ModApiEvent("HitResponse", "hit_response", 14, "rex_CECHitResponsePlayAction_OnHit (r3 listener, entity at r3-8; r4 hit; r5 result word), priority 20 on the dispatch",
			"The victim is about to flinch or be knocked down by a hit. The game's own early-outs (a blocked or parried hit, a ranged hit, a knockdown request, a killing blow) come first, so a lethal hit's fall always plays.",
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
			"A hero pressed B to charge a spell.",
			"the charge does not start",
			new[] { F("entity", "uint32_t", "the caster"), F("player", "int32_t", "1 or 2 for a hero, else 0") }),
		new ModApiEvent("SpellChargeLevel", "spell_charge_level", 16, "rex_CECSpellManager_ChargeLevelFromTime (r3 spell manager; r6 int* charged-slot count, r7 int* first slot), after it",
			"The charge time became the top slot to cast (first + count - 1); the release's cascade launches slots 0..level, each at its own power.",
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
			}),
		new ModApiEvent("EntityDied", "entity_died", 21, "rex_CECCreature_Die (r3 creature component 20, owner at +4, alive byte +0x18; r4 killer)",
			"A creature is dying: every creature death goes through here, from damage, a script kill or a fall. Heroes come to HeroDowned instead.",
			"the creature stays alive with 1 health, as the game's own CantDie answer; the next hit asks again",
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
		new ModApiEvent("ItemAdded", "item_added", 27, "rex_CECInventory_AddOneOfRecord (r3 out, r4 inventory component 13, owner at +4; r5 definition hash; r6 notify)",
			"An item is being added to an entity's inventory by its definition: a pickup, a purchase, a gift, a reward, a script, the starting kit.",
			"nothing is added",
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
				F("item", "uint32_t", "the item object (native)"),
				F("definition", "uint32_t", "the item definition's GDB record hash"),
				F("count", "int32_t", "how many the stack holds"),
			}),
		new ModApiEvent("HeldItem", "held_item", 29, "rex_CECHeldItems_AttachItemToSlot (r3 component 24, owner at +4; r4 slot name; r5 item) / rex_CECHeldItems_ReleaseSlot (r3, r4)",
			"An item entity is attached to or released from one of an entity's holding slots: a weapon drawn (release from the holding slot, attach to the hand), sheathed, or picked up.",
			"the attach or release is refused; a refused release makes the attach after it see nothing",
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
				F("type", "int32_t", "the ability 1..14 (1 Toughness .. 7 Lightning .. 14 Raise Dead)"),
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
				F("delta", "int32_t", "the signed change (native: writable)", true),
				F("passive", "bool", "income shown with the HUD's icon (rent)"),
			}),
	};

	/// <summary>The native helpers of <c>core/refii_mod.h</c>, for the Guide and the editor API.</summary>
	public static readonly IReadOnlyList<ModApiHelper> Helpers = new[]
	{
		new ModApiHelper("uint32_t refii_mod_player_hero(base, player)", "player 1's or 2's hero entity, or 0"),
		new ModApiHelper("uint32_t refii_mod_local_hero(base)", "the local player's hero, or 0"),
		new ModApiHelper("int32_t refii_mod_player_of(base, entity)", "1 or 2 when the entity is that player's hero, else 0"),
		new ModApiHelper("uint32_t refii_mod_component(base, entity, id)", "component id of an entity (33 player, 36 health, 56 spells, 57 stats, 164 experience), or 0"),
		new ModApiHelper("int32_t refii_mod_entity_alive(base, entity)", "whether the entity is in the world"),
		new ModApiHelper("int32_t refii_mod_entity_position(ctx, base, entity, float out[3])", "the entity's position through its own placement getter"),
		new ModApiHelper("int32_t refii_mod_health(base, entity, float* current, float* max)", "current and maximum health"),
		new ModApiHelper("int32_t refii_mod_set_health(base, entity, value)", "writes current health, clamped to [0, max]"),
		new ModApiHelper("int32_t refii_mod_ability_level(base, entity, type)", "the level of ability 1..14 (1 Toughness .. 7 Lightning .. 14 Raise Dead), or -1"),
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
		new ModApiHelper("int32_t refii_mod_modify_health(ctx, base, entity, delta, attacker)", "heals or hurts through the game's own Modify (death included)"),
		new ModApiHelper("int32_t refii_mod_level_name(base, char* out, size)", "the loaded level's name"),
		new ModApiHelper("int32_t refii_mod_gold(base, entity, int32_t* out) / refii_mod_give_gold(ctx, base, entity, delta)", "a purse, read and changed through the game's own Modify"),
		new ModApiHelper("int32_t refii_mod_item_count(base, entity, definition) / uint32_t refii_mod_add_item(ctx, base, entity, definition)", "an inventory, counted and added to through the game's own add"),
		new ModApiHelper("int32_t refii_mod_run_game_lua(ctx, base, chunk)", "runs a Lua chunk (under 944 bytes) in the game VM"),
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
	};

	/// <summary>The Lua-side calls the init chunk's preamble defines.</summary>
	public static readonly IReadOnlyList<ModApiHelper> LuaHelpers = new[]
	{
		new ModApiHelper("Refii.on(event, function(e) ... end)", "subscribes; e is a table of the event's fields; return true to cancel"),
		new ModApiHelper("Refii.setting(key, default)", "a settings.json value (composed in at session start as RefiiSettings)"),
		new ModApiHelper("Refii.hero(player)", "the player's hero id as the bus last stated it, or nil"),
		new ModApiHelper("Refii.api_version", "the API version the chunk was composed for"),
		new ModApiHelper("RefiiModSave.<key>", "state that persists with the save game"),
		new ModApiHelper("RefiiModErrors", "every handler and init error, appended"),
	};

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
		string fields = string.Join("\n", e.Fields.Where(f => !f.Type.Contains('*')).Select(f => $"\t-- e.{f.Name}: {f.Meaning}"));
		string cancel = e.Cancellable ? $"\t-- return true  -- {e.Cancel}\n" : string.Empty;
		return $"Refii.on(\"{e.LuaName}\", function(e)\n{fields}\n{cancel}end)\n";
	}
}
