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
	public const int Version = 2;

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
			"An animation event reached an action; ALLOW_ACTION_INTERRUPT is the one that lets the next action in. Actions whose class overrides the handler without calling the base (about 18 of 180) are not seen.",
			"the action never sees the event",
			new[]
			{
				F("action", "uint32_t", "the live action (native)"),
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
			HitAfter),
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
			"The charge time became the top slot to cast (first + count - 1); the release's cascade launches slots 0..level, each at its own power. Raised every frame of a held charge; Lua handlers run when the level changes, their verdict holds between.",
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
				F("item", "uint32_t", "the item object (native)"),
				F("definition", "uint32_t", "the item definition's GDB record hash"),
				F("count", "int32_t", "how many the stack holds"),
			}),
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
			"A mod's own input action was pressed or released by a player: the keyboard or the first pad is player 1, the second pad player 2. Edges only.",
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
		new ModApiEvent("SleepMenu", "sleep_menu", 54, "rex_SleepMenu_Show",
			"A bed's sleep menu is about to open; the time skip after it is the shipped scripts'.",
			"the menu stays closed",
			new[]
			{
				F("seen", "bool", "always true"),
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
			}),
		new ModApiEvent("FastTravel", "fast_travel", 56, "rex_FastTravelManager_TravelToStop (r3 manager; r4 level; r5 stop, +4 marker; r6 paid)",
			"The hero chose a fast-travel stop: the world map or a script. From the world map the map has already closed when this fires; a refusal leaves the hero there with the map shut.",
			"the hero stays put and pays nothing",
			new[]
			{
				F("level", "const char* / string", "the destination level"),
				F("stop", "const char* / string", "the stop's marker"),
				F("paid", "bool", "the travel costs gold"),
			}),
		new ModApiEvent("MenuOpened", "menu_opened", 57, "rex_lhGuiManager_EnterGuiMode / rex_lhGuiManager_EnterGuiModeQuiet (r3 the GUI manager), when menu mode was off",
			"The game entered its menu mode; the world pauses behind it.",
			string.Empty,
			new[]
			{
				F("seen", "bool", "always true"),
			}),
		new ModApiEvent("MenuClosed", "menu_closed", 58, "rex_lhGuiManager_ExitScreen (r3 the GUI manager), when it really leaves menu mode",
			"The game left its menu mode.",
			string.Empty,
			new[]
			{
				F("seen", "bool", "always true"),
			}),
		new ModApiEvent("MenuScreen", "menu_screen", 59, "rex_lhGuiManager_SetScreen (r3 the GUI manager; r4 the screen id), when it changes; TU1 numbering on both builds",
			"A menu shows a screen: 0 pause, 2 spells, 14 abilities, 16 equipment, 17 logbook, 18 options, 22 shop, 23 world map, 24 co-op.",
			string.Empty,
			new[]
			{
				F("screen", "int32_t", "the screen id the game states"),
			}),
		new ModApiEvent("WeatherRegion", "weather_region", 60, "rex_lhPaintMap_DaySetAt (r3 paint map, v1 the hero's position; returns the day set hash or -1), after it",
			"The hero walked into a different painted weather region: the region's environment day set, which picks the theme by time of day. A scripted blend is WeatherTheme.",
			string.Empty,
			new[]
			{
				F("day_set", "uint32_t", "the region's day set (a GDB record hash; Lua and Godot: a \"0x…\" string), 0 outside every painted region"),
				F("previous", "uint32_t", "the day set before"),
				F("inside", "bool", "the hero is inside a painted region"),
			}),
		new ModApiEvent("SleepChosen", "sleep_chosen", 61, "rex_lhMenuBox_Close (r3 the choice menu: +0 index, +13 cancelled), while the sleep menu is open",
			"The player answered a bed's sleep menu. A mod can change the choice or refuse the sleep; the shipped SleepFunction.Sleep applies it. A length the menu does not offer is a Lua mod wrapping SleepFunction.Sleep.",
			"the sleep is refused, as if cancelled",
			new[]
			{
				F("bed", "uint32_t", "the bed"),
				F("choice", "int32_t", "0 fast-forwards six hours, 1 twelve hours, 2 adds a day (the clock stays), 3 adds seven days (writable)", true),
				F("cancelled", "bool", "the player backed out"),
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
	};

	/// <summary>The native helpers of <c>core/refii_mod.h</c>, for the Guide and the editor API.</summary>
	public static readonly IReadOnlyList<ModApiHelper> Helpers = new[]
	{
		new ModApiHelper("uint32_t refii_mod_player_hero(base, player)", "player 1's or 2's hero entity, or 0"),
		new ModApiHelper("uint32_t refii_mod_local_hero(base)", "the local player's hero, or 0"),
		new ModApiHelper("int32_t refii_mod_player_of(base, entity)", "1 or 2 when the entity is that player's hero, else 0"),
		new ModApiHelper("int32_t refii_mod_menu_player(base)", "the player whose menus are open here, 1 or 2: an ability row publishes and buys for them"),
		new ModApiHelper("float refii_mod_player_value(player, key, fallback)", "a player's value, the same on both co-op machines (fallback when unset)"),
		new ModApiHelper("void refii_mod_player_set(player, key, v) / refii_mod_player_add(player, key, d, saved)", "writes a player's value on either machine; the hero's owner applies it (an add merges)"),
		new ModApiHelper("void refii_mod_player_save(player, key, v)", "a SAVED player value: kept with that hero's own save, back when it loads"),
		new ModApiHelper("int32_t refii_mod_player_here(player)", "whether this machine plays that hero: run a per-player refill or timer only there"),
		new ModApiHelper("int32_t refii_mod_event_replica()", "the event is the game's copy of something the other co-op machine decides: verdicts apply, player-state writes are dropped"),
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
		new ModApiHelper("int32_t refii_mod_run_game_lua(ctx, base, chunk)", "runs a Lua chunk in the game VM"),
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
	};

	/// <summary>The Lua-side calls the init chunk's preamble defines.</summary>
	public static readonly IReadOnlyList<ModApiHelper> LuaHelpers = new[]
	{
		new ModApiHelper("Refii.on(event, function(e) ... end)", "subscribes; e is a table of the event's fields; return true to cancel; assigning a writable field (e.damage, e.delta, e.amount, e.level, e.type, e.relation) changes what the game gets"),
		new ModApiHelper("Refii.publish(key, number) / Refii.value(key)", "a mod value Godot reads as \"mod:<key>\" (HUD pieces, FX recipes), and the last value under a key"),
		new ModApiHelper("Refii.log(text)", "a line in the guest log, prefixed [mod lua]"),
		new ModApiHelper("Refii.hash(name)", "the game's FNV-1 of a name as a \"0x…\" string: a GDB record's hash, a text tag's key"),
		new ModApiHelper("Refii.health(e) -> current, max / Refii.set_health(e, v) / Refii.modify_health(e, delta, attacker)", "health, read, written, or changed through the game's own Modify (death included)"),
		new ModApiHelper("Refii.position(e) -> x, y, z / Refii.set_position(e, x, y, z)", "a position through the entity's own placement"),
		new ModApiHelper("Refii.gold(e) / Refii.give_gold(e, delta)", "a purse, read and changed through the game's own Modify"),
		new ModApiHelper("Refii.item_count(e, definition) / Refii.add_item(e, definition)", "an inventory: definitions are \"0x…\" strings (Refii.hash of the record's name)"),
		new ModApiHelper("Refii.experience(e, pool) / Refii.give_experience(e, pool, amount) / Refii.spend_experience(e, pool, amount)", "experience: pools 0 General 1 Strength 2 Skill 3 Will"),
		new ModApiHelper("Refii.ability_level(e, type)", "the level of ability 1..14, or nil"),
		new ModApiHelper("Refii.cast_spell(caster, type, targeted, target, level)", "a real spell through the game's spell factory, no will cost; the spell object's handle or nil"),
		new ModApiHelper("Refii.relation(a, b) / Refii.alive(e) / Refii.definition(e) / Refii.name(e) / Refii.player_of(e)", "the faction rule (1 neutral 2 ally 4 enemy), liveness, the definition hash, the instance name, 1/2/0"),
		new ModApiHelper("Refii.level_name() / Refii.player_hero(p) / Refii.local_hero() / Refii.menu_player()", "the loaded level, a player's hero, the local hero, whose menus are open (1/2)"),
		new ModApiHelper("Refii.player_value(p, key [, fallback]) / Refii.player_set(p, key, v) / Refii.player_add(p, key, d [, saved]) / Refii.player_save(p, key, v) / Refii.player_here(p) / Refii.event_replica()", "per-player values, the same on both co-op machines; saved ones go home with the hero"),
		new ModApiHelper("Refii.split_screen()", "true when local co-op runs on split screen (each local player their own camera)"),
		new ModApiHelper("Refii.player_dog(p)", "a player's dog: player one's is the game's own GetDog, player two's the co-op dog bound to them; nil when none"),
		new ModApiHelper("Refii.renown(e) / Refii.morality(e) / Refii.purity(e) / Refii.age(e) / Refii.physique(e) -> fat, height", "the hero stats the game reports"),
		new ModApiHelper("Refii.inventory(e)", "{ {definition = \"0x…\", count = n}, … } in the game's order"),
		new ModApiHelper("Refii.equipped(e) -> melee, ranged", "the weapon set's definition hashes, nil for an empty hand"),
		new ModApiHelper("Refii.near(x, y, z, radius [, creatures_only])", "handles of the entities within a radius, nearest first (at most 2048)"),
		new ModApiHelper("Refii.line_of_sight(x1, y1, z1, x2, y2, z2)", "true when the game's physics sees nothing between"),
		new ModApiHelper("Refii.spawn(definition, x, y, z)", "a new entity through the game's own CreateEntityFromIDAtPosition: its handle, or nil"),
		new ModApiHelper("Refii.entity(handle) / Refii.handle(entity)", "the game's own Entity userdata for a handle (for the shipped scripts and natives), and back"),
		new ModApiHelper("Refii.setting(key, default)", "a settings.json value (composed in at session start as RefiiSettings)"),
		new ModApiHelper("Refii.hero(player)", "the player's hero handle as the bus last stated it, or nil"),
		new ModApiHelper("Refii.api_version", "the API version the chunk was composed for"),
		new ModApiHelper("RefiiModSave.<key>", "state that persists with the save game"),
		new ModApiHelper("RefiiModErrors", "every handler and init error, appended"),
	};

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
