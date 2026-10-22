// refii — THE MOD EVENT API. Named, typed events on the game's own seams, for hard mods.
//
// WHY EVENTS. A hook on a game function is the loader-level primitive (core/refii_mod.h):
// exact, but a mod using it has to know the function, its registers and its structs, and two
// mods on one function share it by a chain. An EVENT is that seam with the knowledge done once:
// refii hooks the function, reads the fields, and hands every subscribed mod a struct it can
// read, change or cancel. Subscribing to an event never conflicts with another mod and never
// needs a game function's name. This is the Fabric/Forge shape: the events API is the surface,
// hooks are the mixins underneath for what the events do not cover yet.
//
//   #include "core/refii_mod_events.h"
//
//   static void OnHit(refii::mods::HitEvent& e) {
//     if (e.attacker_player != 0) e.damage *= 2.0f;      // either hero hits twice as hard
//     if (e.victim_player != 0 && e.spell_type == 2) e.cancel = true;   // heroes ignore slow-time hits
//   }
//   REFII_MOD_ON(Hit, OnHit);
//
// EVERY EVENT CARRIES `ctx` AND `base` (the guest context — a copy, legal for guest calls — and
// guest memory) and a `cancel` flag. Where the seam allows a refusal, the event says what
// `cancel` means; where it does not, `cancel` is ignored. Writable fields are named as such;
// everything else is a copy. A `player` field is 1 or 2 when the entity is that player's
// hero and 0 otherwise — both players are principals. Handlers run in priority order (higher first; ties in registration
// order, which is load order for mods, refii's own last), every handler sees every event, and
// the final state after the last handler is what the game gets. Seams are listed per event:
// the game function (named in versions/*/refii_config.toml) and what was read from the binary.
//
// EVENT HANDLERS RUN ON THE GUEST TICK THREAD, inside the game function they wrap. Keep them
// short; never block; a guest call is fine (CallGuest/CallVirtual/refii_mod_* helpers).
//
// VERSIONING. REFII_MOD_API_VERSION rises when an event's fields change meaning or an event is
// removed; new events and new trailing fields do not bump it. A mod may test it at compile time.
// Version 2: Lua and Godot receive entities and objects as HANDLES (refii_mod_handle) and
// hashes (definitions, text) as hex strings; native structs are unchanged.

#pragma once

#include <cstdint>

#include <rex/ppc/context.h>

#define REFII_MOD_API_VERSION 2

/// The numeric kinds. Stable within an API version; the names are what mods use.
enum refii_mod_event_kind : int32_t {
  REFII_EVENT_TICK = 0,
  REFII_EVENT_HERO_READY = 1,
  REFII_EVENT_HERO_GONE = 2,
  REFII_EVENT_LUA_READY = 3,
  REFII_EVENT_SAVE_LOADED = 4,
  REFII_EVENT_GAME_ACTION = 5,
  REFII_EVENT_ABILITY_REQUEST = 6,
  REFII_EVENT_MELEE_SWING = 7,
  REFII_EVENT_ROLL_CAN_START = 8,
  REFII_EVENT_ROLL_STARTED = 9,
  REFII_EVENT_BLOCK = 10,
  REFII_EVENT_ANIM_EVENT = 11,
  REFII_EVENT_HIT = 12,
  REFII_EVENT_HIT_LANDED = 13,
  REFII_EVENT_HIT_RESPONSE = 14,
  REFII_EVENT_SPELL_CHARGE = 15,
  REFII_EVENT_SPELL_CHARGE_LEVEL = 16,
  REFII_EVENT_SPELL_LAUNCH = 17,
  REFII_EVENT_SPELL_LAUNCHED = 18,
  REFII_EVENT_SPELL_CREATED = 19,
  REFII_EVENT_EXPERIENCE = 20,
  REFII_EVENT_ENTITY_DIED = 21,
  REFII_EVENT_HERO_DOWNED = 22,
  REFII_EVENT_DAMAGE = 23,
  REFII_EVENT_LEVEL_LOADED = 24,
  REFII_EVENT_LEVEL_UNLOADING = 25,
  REFII_EVENT_GAME_SAVING = 26,
  REFII_EVENT_ITEM_ADDED = 27,
  REFII_EVENT_ITEM_USED = 28,
  REFII_EVENT_HELD_ITEM = 29,
  REFII_EVENT_WEAPON_EQUIPPED = 30,
  REFII_EVENT_ABILITY_LEVEL = 31,
  REFII_EVENT_GOLD = 32,
  REFII_EVENT_RELATION = 33,
  REFII_EVENT_INPUT_ACTION = 34,
  REFII_EVENT_QUEST_STARTED = 35,
  REFII_EVENT_QUEST_ENDED = 36,
  REFII_EVENT_QUEST_OBJECTIVE = 37,
  REFII_EVENT_DIALOGUE = 38,
  REFII_EVENT_ENTITY_SPAWNED = 39,
  REFII_EVENT_ENTITY_REMOVED = 40,
  REFII_EVENT_EXPRESSION_STARTED = 41,
  REFII_EVENT_RENOWN = 42,
  REFII_EVENT_MORALITY = 43,
  REFII_EVENT_PURITY = 44,
  REFII_EVENT_MARRIAGE = 45,
  REFII_EVENT_DOG_TRICK = 46,
  REFII_EVENT_RANGED_SHOT = 47,
  REFII_EVENT_STATUS_EFFECT = 48,
  REFII_EVENT_TARGET_CHANGED = 49,
  REFII_EVENT_OPINION_DEED = 50,
  REFII_EVENT_OPINION_MODIFIED = 51,
  REFII_EVENT_TIME_OF_DAY = 52,
  REFII_EVENT_WEATHER_THEME = 53,
  REFII_EVENT_SLEEP_MENU = 54,
  REFII_EVENT_TRAVEL = 55,
  REFII_EVENT_FAST_TRAVEL = 56,
  REFII_EVENT_MENU_OPENED = 57,
  REFII_EVENT_MENU_CLOSED = 58,
  REFII_EVENT_MENU_SCREEN = 59,
  REFII_EVENT_WEATHER_REGION = 60,
  REFII_EVENT_SLEEP_CHOSEN = 61,
  REFII_EVENT_SLOW_TIME = 62,
  REFII_EVENT_SUMMONED = 63,
  REFII_EVENT_CRIME = 64,
  REFII_EVENT_ACHIEVEMENT = 65,
  REFII_EVENT_COLLECTABLE_TAKEN = 66,
  REFII_EVENT_PARTNER_CONSENT = 67,
  REFII_EVENT_PROPERTY_BOUGHT = 68,
  REFII_EVENT_PROPERTY_SOLD = 69,
  REFII_EVENT_RENT_PAID = 70,
  REFII_EVENT_PROPERTY_RENTED = 71,
  REFII_EVENT_PROPERTY_CONDITION = 72,
  REFII_EVENT_DIVORCE = 73,
  REFII_EVENT_WIDOWED = 74,
  REFII_EVENT_CHILD_BORN = 75,
  REFII_EVENT_GIFT_GIVEN = 76,
  REFII_EVENT_GIFT_ACCEPTED = 77,
  REFII_EVENT_ENGAGED = 78,
  REFII_EVENT_FOLLOW = 79,
  REFII_EVENT_FURNITURE_UPGRADED = 80,
  REFII_EVENT_REDECORATING = 81,
  REFII_EVENT_HOUSEHOLD_BUDGET = 82,
  REFII_EVENT_TRACKED_STAT = 83,
  REFII_EVENT_PARTNERSHIP = 84,
  REFII_EVENT_HOUSEHOLD_RESTORED = 85,
  REFII_EVENT_DYNAMIC_WEATHER = 86,
  REFII_EVENT_MOD_MENU_ROW = 87,
  REFII_EVENT_MOD_MESSAGE_BOX = 88,
  REFII_EVENT_MOD_NOTICE = 89,
  REFII_EVENT_MOD_MENU_BOX = 90,
  REFII_EVENT_MOD_ABILITY = 91,
  REFII_EVENT_SPELL_PICK = 92,
  REFII_EVENT_PARTNER_MESSAGE = 93,
  REFII_EVENT_LOOK_AT_BUTTON = 94,
  REFII_EVENT_VORTEX_STARTED = 95,
  REFII_EVENT_VORTEX_ENDED = 96,
  REFII_EVENT_FIREBALL_IMPACT = 97,
  REFII_EVENT_PAUSED = 98,
  REFII_EVENT_START_GAME = 99,
  REFII_EVENT_COUNT = 100,
};

/// The kind the host's query replies arrive under (RefiiHost in Godot): never a bus event.
#define REFII_MOD_HOST_REPLY_KIND 1000

/// The kind a play_fx / stop_fx request crosses to Godot under (refii_mod_play_fx in
/// core/refii_mod.h; Engine_FxPlayer draws it): never a bus event, posted whatever the mask says.
#define REFII_MOD_HOST_FX_KIND 1001

/// The kind an open_menu / message_box / notice / menu_box request crosses to Godot under
/// (refii_mod_open_menu, refii_mod_message_box, refii_mod_notice, refii_mod_menu_box in
/// core/refii_mod.h; Engine_UiModBoxes acts on it): never a bus event, posted whatever the mask says.
#define REFII_MOD_HOST_UI_KIND 1002

/// The kind a mod menu's rows and a host screen request cross to Godot under
/// (refii_mod_set_menu_rows, refii_mod_open_screen in core/refii_mod.h; Engine_UiModRows acts on
/// it): never a bus event, posted whatever the mask says.
#define REFII_MOD_HOST_MENU_KIND 1003

/// A subscriber: `event` is the kind's struct (below), `user` is what was registered with it.
typedef void (*refii_mod_event_fn)(void* event, void (*user)(void));

/// Subscribes `fn` to `kind` at `priority` (higher runs first). `owner` is the mod's name, for
/// the log. A kind at or above REFII_EVENT_COUNT is a mod's own (raised with refii_mod_dispatch).
/// Returns the subscription's index, or -1 for a negative kind or no function.
extern "C" int32_t refii_mod_subscribe(int32_t kind, refii_mod_event_fn fn, void (*user)(void), int32_t priority,
                                       const char* owner);

/// The API version the running payload was built with.
extern "C" int32_t refii_mod_api_version(void);

/// Fires `event` of `kind` to every subscriber (native, then the mods' Lua handlers). What
/// refii's own seams call; a mod may raise its own kinds this way only above REFII_EVENT_COUNT.
extern "C" void refii_mod_dispatch(int32_t kind, void* event);

#ifndef REFII_MOD_NAME
/// The launcher defines this per mod (`-DREFII_MOD_NAME="<mod>"`); refii's own sources have none.
#define REFII_MOD_NAME "refii"
#endif

namespace refii::mods {

/// What every event carries.
struct Event {
  PPCContext* ctx;
  uint8_t* base;
  /// Set to refuse, where the event says a refusal exists; otherwise ignored.
  bool cancel;
};

/// Once per guest tick, menus included, after the game's own tick work. `seconds` is real time
/// since the last tick, capped at a quarter second across a hitch.
struct TickEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_TICK;
  float seconds;
};

/// A player's hero exists (HeroReady: a session or save started, a co-op partner joined) or
/// stopped existing (HeroGone). `player` is 1 or 2. Derived in the tick from the player
/// manager's handles (refii_mod_player_hero); fires on the first tick the change is seen.
struct HeroEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_HERO_READY;
  int32_t player;
  uint32_t hero;
};

struct HeroGoneEvent : HeroEvent {
  static constexpr int32_t kKind = REFII_EVENT_HERO_GONE;
};

/// The game's Lua state was rebuilt and every mod's init.lua has run (LuaReady), or a save's
/// Lua table was loaded into it (SaveLoaded). Either steps `generation`
/// (refii_mod_lua_generation). A mod keeping state in RefiiModSave re-reads it here.
struct LuaEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_LUA_READY;
  uint32_t generation;
};

struct SaveLoadedEvent : LuaEvent {
  static constexpr int32_t kKind = REFII_EVENT_SAVE_LOADED;
};

/// A GameAction reached a player hero's action receiver: a button's press or release as the
/// control scheme maps it (49 TARGET_LOCK_SET and 50 _RELEASE for LT, 11 Interact ...; the
/// ids are the GDB's GameAction rows). `cancel` swallows it: the game never sees the action.
/// Seam: rex_CPlayerActionReceiver_ProcessAction (r3 receiver, r4 message whose first word is
/// the action; the receiver's entity is *(*(r3+0xC))).
struct GameActionEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_GAME_ACTION;
  uint32_t hero;
  int32_t player;
  uint32_t action;
  /// The 24-byte action message, for the fields past the id.
  uint32_t message;
};

/// A creature asked its ability set for an ability: 16 melee pressed, 17 melee released, 18
/// magic charge, and the rest of the game's AbilityType enum (the request's vtable slot 1).
/// `cancel` refuses it (the request matches nothing). A refused melee press should also refuse
/// its release. Seam: rex_CECAbilities_PerformRequest (r3 entity, r4 request).
struct AbilityRequestEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ABILITY_REQUEST;
  uint32_t entity;
  int32_t player;
  uint32_t request;
  uint32_t type;
};

/// A strike's swing has started (its animation is playing); not refusable here — refuse the
/// AbilityRequest instead. Seam: rex_CAbilityActionMelee_CommitSwing (r3 ability, r4 entity),
/// called by every strike's Perform after PlayAction succeeded.
struct MeleeSwingEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MELEE_SWING;
  uint32_t entity;
  int32_t player;
  uint32_t ability;
  /// The live strike action the entity's action manager (component 49) now runs, or 0.
  uint32_t action;
  /// The action's scriptable type (EScriptableAction, 0..88; -1 unknown) and its name
  /// (refii_mod_action_type / refii_mod_action_name; "" unknown).
  int32_t action_type;
  const char* action_name;
};

/// The roll asks whether it may start (RollCanStart: `cancel` refuses, asked once per matching
/// roll record, so it never spends) and reports it was queued (RollStarted: after the fact).
/// Seams: rex_CAbilityActionStrafeRoll_CanStart / _Start (r4 entity; Start returns 1 only when
/// a roll was queued). Rolls are already invulnerable for their whole length in the game.
struct RollEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ROLL_CAN_START;
  uint32_t entity;
  int32_t player;
};

struct RollStartedEvent : RollEvent {
  static constexpr int32_t kKind = REFII_EVENT_ROLL_STARTED;
};

/// A block is taking a hit. `cancel` BREAKS THE GUARD: the hit lands in full, as if there were
/// no block. Fired once per hit (two block classes each register one listener — pose and response;
/// when both are live on a victim the second takes the first's verdict). Refusing a response-class
/// block also drops a parry it would make (hit +0x1A). `unblockable` hits bypass blocks in the game already
/// and are reported with cancel ignored. Seams: rex_CActionBlock_OnHitBlockPose /
/// _OnHitBlockedResponse (r3 listener at action+0x54, r4 hit, r5 result word).
struct BlockEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_BLOCK;
  uint32_t blocker;
  int32_t player;
  uint32_t hit;
  float damage;
  bool unblockable;
};

/// An animation event reached an action: `name` is the event's text ("ALLOW_ACTION_INTERRUPT"
/// is the one that lets the next action in). `cancel` drops it. Seam: rex_CAction_OnAnimEvent
/// (r3 live action, r4 animation, r6 value object; the text is **r6).
struct AnimEventEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ANIM_EVENT;
  uint32_t action;
  const char* name;
  /// The action's owner, as the action states it (its weak reference at +4): any creature.
  uint32_t entity;
  /// 1 or 2 when the owner is a player hero, else 0.
  int32_t player;
  /// The action's scriptable type (EScriptableAction, 0..88; -1 unknown) and its name
  /// (refii_mod_action_type / refii_mod_action_name; "" unknown).
  int32_t action_type;
  const char* action_name;
};

/// A hit is arriving at its victim — melee, spell, physics, the Blades' stabs: the one receiver
/// every hit reaches. Fired BEFORE the game delivers it: `damage` is writable, `cancel` drops
/// the hit entirely (no damage, no response). HitLanded fires after, read-only. `spell_type` is
/// -1 for a non-spell hit (0 Lightning 1 Fireball 2 SlowTime 3 Swords 4 Vortex 5 Chaos 6
/// ForcePush 7 DeadRising), `spell_level` 0..4, `spell_serial` the cast's number. Seam:
/// rex_CECHitReceiver_OnHit (r3 victim's component 31, owner at +4; r4 hit: +0 attacker ref,
/// +0x38 damage, +0x44 serial, +0x48 level, +0x54 type).
struct HitEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_HIT;
  uint32_t victim;
  int32_t victim_player;
  uint32_t attacker;
  int32_t attacker_player;
  uint32_t hit;
  /// Writable before delivery.
  float damage;
  int32_t spell_type;
  int32_t spell_level;
  uint32_t spell_serial;
  /// The hit asks for a knockdown (hit +0x18; force push sets it, as do heavy blows). Writable
  /// before delivery: the victim's hit response reads it to choose the knockdown.
  bool knockdown;
};

struct HitLandedEvent : HitEvent {
  static constexpr int32_t kKind = REFII_EVENT_HIT_LANDED;
};

/// The victim's hit-response component is about to play the flinch or knockdown for a hit.
/// `cancel` absorbs it: no animation, no state left behind. The game's own early-outs (a
/// blocked or parried hit, a ranged hit, a knockdown request, a killing blow) are honoured
/// before this fires, so a lethal hit's fall always plays. `damage` is the game's own
/// arithmetic for the hit. Seam: rex_CECHitResponsePlayAction_OnHit (r3 listener, entity at
/// r3-8; r4 hit; r5 result word), priority 20 on the dispatch, before health's 30.
struct HitResponseEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_HIT_RESPONSE;
  uint32_t victim;
  int32_t victim_player;
  uint32_t hit;
  float damage;
  float health;
  float max_health;
};

/// A player hero pressed B to charge a spell. `cancel` refuses the charge. Seam:
/// rex_CAbilityActionMagicCharge_CanStart (r4 entity).
struct SpellChargeEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SPELL_CHARGE;
  uint32_t entity;
  int32_t player;
};

/// The charge time became the top slot to cast; `level` (0..4, writable) is the slot the
/// release casts at full power — the cascade launches slots 0..level, each at its own power.
/// Seam: rex_CECSpellManager_ChargeLevelFromTime (r3 spell manager; r6 int* count of charged
/// slots, r7 int* first slot — the top is first + count - 1; a write moves the count, or the
/// first slot in the game's single-slot mode).
struct SpellChargeLevelEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SPELL_CHARGE_LEVEL;
  uint32_t caster;
  int32_t player;
  int32_t level;
};

/// One slot is about to launch. `type` (writable) is the spell the slot casts for this launch
/// only (the slot keeps its own assignment); `cancel` skips the launch. `cascading` says the
/// release cascade is running through the lower slots (slots_to_come > 0 at a lower slot).
/// Seam: rex_CECSpellManager_LaunchSlot (r3 manager = component 56, caster at +4; r4 targeted;
/// r5 slot; slot table int8[2][5] at +0x80; cascade state +0x70 == 5, +0x170 slots to come).
struct SpellLaunchEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SPELL_LAUNCH;
  uint32_t caster;
  int32_t player;
  uint32_t manager;
  bool targeted;
  int32_t slot;
  int32_t type;
  bool cascading;
};

/// A spell was really created for a launch. Seam: rex_CECSpellManager_OnSpellLaunched (r3
/// manager, r4 slot), reached only behind the launch's own success test.
struct SpellLaunchedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SPELL_LAUNCHED;
  uint32_t caster;
  int32_t player;
  uint32_t manager;
  int32_t slot;
};

/// The spell factory made a spell object — every cast, the player's and any creature's, and
/// refii_mod_cast_spell's. Read-only. Seam: rex_CSpellRegistry_Create (r3 registry, r4 type,
/// r5 targeted, r6 target, r7 level, r8 caster; the object's serial at +0x44).
struct SpellCreatedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SPELL_CREATED;
  uint32_t spell;
  int32_t type;
  bool targeted;
  uint32_t target;
  int32_t level;
  uint32_t caster;
  int32_t caster_player;
  uint32_t serial;
};

/// Experience is changing on an entity's experience component: `spend` false is a Modify
/// (earned or refunded; `amount` signed), true a Spend (`amount` the cost; a shortfall comes
/// out of General). Pools: 0 General, 1 Strength, 2 Skill, 3 Will. `amount` is writable,
/// `cancel` leaves the pools untouched. Seams: rex_CECExperience_Modify (r3 component 164, r4
/// pool, f1 amount, r6..r8 flags) / rex_CECExperience_Spend (r3 component, r4 pool, f1 cost).
/// NESTED: a Spend is done by one or two Modify calls (the pool, then General for a shortfall,
/// 0x8256e330/0x8256e348/0x8256e368), so one `spend` event is followed by the negative Modify
/// events that pay it; and a co-op share recurses on the partner's component (0x8256de64), a
/// second event for the other player. A handler summing amounts counts each change once only
/// by taking the Modify events and ignoring `spend`.
struct ExperienceEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_EXPERIENCE;
  uint32_t entity;
  int32_t player;
  uint32_t component;
  int32_t pool;
  float amount;
  bool spend;
  /// Raised inside another Experience event: a Spend's own payments, or the game's co-op share of
  /// a change already reported. A handler that scales `amount` acts only when this is false;
  /// a nested event cannot be cancelled (it would half-apply its outer change).
  bool nested;
};

/// A creature is dying, from damage (the health component at zero), a script kill, a fall, or a
/// death finished without the dying step (rex_CECCreature_FinishDeath: knockdown ends, scripts).
/// `cancel` keeps it alive only for a death by damage (health at zero): the death is not started
/// and health is set to 1 so the next hit asks again; other deaths cannot be refused — their
/// callers act on the death regardless. Heroes never come here from damage — see HeroDowned. Seam:
/// rex_CECCreature_Die (r3 creature component 20, owner at +4, alive byte +0x18; r4 killer or 0).
struct EntityDiedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ENTITY_DIED;
  uint32_t entity;
  int32_t player;
  uint32_t killer;
  int32_t killer_player;
};

/// A player's hero reached zero health: the knockdown, the scar, the resurrection potion and
/// the revive flow are what follows. `cancel` skips all of it and sets the hero's health to 1,
/// as the game itself does on its way in. Seam: rex_CECHealth_OnHeroDeath (r3 health component
/// 36, owner at +4; r4 killer or 0), reached from the zero-health dispatch for either hero.
struct HeroDownedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_HERO_DOWNED;
  uint32_t hero;
  int32_t player;
  uint32_t killer;
  int32_t killer_player;
};

/// The victim's health is about to take a hit's damage: the health component's own listener
/// on the hit dispatch (priority 30, after responses and blocks). `damage` is the game's final
/// arithmetic for the hit (rex_CECHealth_CalculateDamage), read-only here — change it on the
/// Hit event, before delivery. `cancel` removes no health and starts no death; every other
/// listener on the hit (the flinch, the sound) has already run. Seam: rex_CECHealth_OnHit (r3
/// listener = health+0xC, entity at r3-8; r4 hit; r5 handled word, set when a block took it).
struct DamageEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_DAMAGE;
  uint32_t victim;
  int32_t victim_player;
  uint32_t attacker;
  int32_t attacker_player;
  uint32_t hit;
  float damage;
  float health;
  float max_health;
  bool lethal;
  int32_t spell_type;
  int32_t spell_level;
  uint32_t spell_serial;
};

/// A level finished loading and the heroes stand in it, on a new game, a fast travel and a
/// save load alike; fired after the game's own Lua `AIManager:OnLevelLoad(fromSave)`. `level` is
/// the level's name (the world/level manager's current level, +0xAC). Region-internal level
/// changes (walking through a door) do not come here. Seam: rex_lhLuaScriptSystem_OnLevelLoad
/// (r3 script-system owner, r4 from-save byte), after it.
struct LevelLoadedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_LEVEL_LOADED;
  const char* level;
  bool from_save;
};

/// The current level is about to be unloaded (a travel, a load, the session's end): the last
/// moment its entities exist. Seam: rex_lhWorld_UnloadLevel (r3 world manager; +0x24 the level
/// holder), before it, only when a level is loaded.
struct LevelUnloadingEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_LEVEL_UNLOADING;
  const char* level;
};

/// A save file is about to be written (the main save, and a quest-fail save when one is made):
/// the moment to put state into RefiiModSave. Seam: rex_lhSaveManager_WriteSave (r3 save
/// manager, r4 file name string), before it.
struct GameSavingEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_GAME_SAVING;
  const char* file;
};

/// An item was added to an entity's inventory (component 13): a pickup, a purchase, a gift,
/// a quest reward, a script, the starting kit — by its definition's hash (the GDB record). One
/// more of an item already held raises its count. After the add, not cancellable: several callers
/// use the new item at once and cannot take a refusal (verified: two crash on it, one destroys the
/// picked-up entity regardless). Seam:
/// rex_CECInventory_AddOneOfRecord (r3 out, r4 inventory, owner at +4; r5 definition hash; r6
/// notify), the funnel every add goes through.
struct ItemAddedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ITEM_ADDED;
  uint32_t entity;
  int32_t player;
  uint32_t inventory;
  uint32_t definition;
  bool notify;
};

/// An entity is using an inventory item — a potion, food, a book, a gift: anything whose
/// definition has a carried-action script; the script does the consuming. `cancel` refuses the
/// use (the game answers "not used"). Seam: rex_lhEntity_UseInventoryItem (r3 entity, r4 the
/// item's shared pointer: item at *r4, definition at item+4, count at item+12; r5 script override).
struct ItemUsedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ITEM_USED;
  uint32_t entity;
  int32_t player;
  uint32_t item;
  uint32_t definition;
  int32_t count;
};

/// An item entity is being attached to, or released from, one of an entity's holding slots
/// (component 24): drawing and sheathing a weapon are a release from the holding slot and an
/// attach to the hand, a pick-up is an attach. `cancel` refuses it (a refused release makes the
/// attach that follows see nothing). Seams: rex_CECHeldItems_AttachItemToSlot (r3 held items,
/// owner at +4; r4 slot name; r5 item entity) / rex_CECHeldItems_ReleaseSlot (r3, r4 slot name;
/// returns the item released).
struct HeldItemEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_HELD_ITEM;
  uint32_t entity;
  int32_t player;
  const char* slot;
  /// The item entity: known on an attach, 0 on a release until the game has done it. Only a
  /// release can be refused: an attach's callers record the drawn weapon regardless.
  uint32_t item;
  bool attached;
};

/// A weapon was equipped into an entity's weapon set (component 28): the melee or ranged
/// record changes — at equip time, not on a draw. Read-only. Seam:
/// rex_CECWeaponSet_RecordDrawnItem (r3 weapon set, owner at +4; r4 item entity, definition at
/// +0x1C), before it.
struct WeaponEquippedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_WEAPON_EQUIPPED;
  uint32_t entity;
  int32_t player;
  uint32_t item;
  uint32_t definition;
};

/// An ability's level is being set on an entity's stats (component 57): the abilities book's
/// purchase and unlearn, a script, a save load. `level` is writable; `cancel` leaves the level
/// as it was (the book has already spent the experience by then). Seam:
/// rex_CECStats_SetAbilityLevel (r3 stats, owner at +4; r4 type 1..14; r5 level; r6 force).
struct AbilityLevelEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ABILITY_LEVEL;
  uint32_t entity;
  int32_t player;
  int32_t type;
  int32_t level;
  int32_t old_level;
};

/// Gold is changing on an entity's purse (component 81): a purchase, a sale, a pickup, rent, a
/// gift, a reward. `delta` (signed) is writable; `cancel` changes nothing. `passive` marks
/// income (rent) that the HUD shows with its icon. Seam: rex_CECMoney_Modify (r3 money
/// component, owner at +4, amount at +12; r4 delta; r5 no-split; r6 co-op share; r7 passive).
/// NESTED: the co-op share recurses on the partner's purse (0x8242e938, 0x8242eb60), a second
/// event for the other player.
struct GoldEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_GOLD;
  uint32_t entity;
  int32_t player;
  int32_t gold;
  int32_t delta;
  bool passive;
  /// The game's co-op split of a change already reported (forwarded to player one, or the
  /// partner's share): a handler that scales `delta` acts only when this is false.
  bool share;
};

/// The game asked how `entity` stands to `other` — its faction rule, asked by the AI, the
/// targeting, the HUD's colours, many times a tick. `relation` (writable) is the answer: 1
/// neutral, 2 ally, 4 enemy; a handler makes two creatures friends or foes. Not cancellable. Seam:
/// rex_CECFaction_RelationTo (r3 faction component 133, owner at +4; r4 the other entity; returns
/// the relation), after it. refii_mod_relation calls the game's body directly and never raises this.
struct RelationEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_RELATION;
  uint32_t entity;
  int32_t player;
  uint32_t other;
  int32_t other_player;
  int32_t relation;
};

/// A mod's own input action changed state: `name` is "<mod>.<action>" from the mod's input.json,
/// `player` 1 or 2 (player 1 is the keyboard and the first pad, player 2 the second pad), `pressed`
/// true on the press and false on the release. Edges only, raised in the guest tick from the
/// host's queue (Godot reads the devices; RefiiHostPostModInput). Not cancellable.
struct InputActionEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_INPUT_ACTION;
  const char* name;
  int32_t player;
  bool pressed;
};

/// A quest is being activated on a hero's quest tracker (component, owner at +4): `quest` is the
/// quest's name (the GDB record's, the one the shipped scripts pass), `make_primary` whether it
/// becomes the tracked quest. `cancel` leaves it inactive. Seam: rex_CECQuestTracker_SetAsActive
/// (r3 tracker, r4 name, r5 flag, r6 make-primary; the record's +13 active and +14 activated bytes).
struct QuestStartedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_QUEST_STARTED;
  const char* quest;
  uint32_t hero;
  int32_t player;
  bool make_primary;
};

/// A quest ended: `completed` (the rewards were just given) or `failed`. After the fact, so not
/// cancellable. Seam: rex_CECQuestTracker_OnQuestEnded (r3 tracker, r4 record: +0 name LHString,
/// +21 completed, +22 failed), the one function both the completion and the failure reach.
struct QuestEndedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_QUEST_ENDED;
  const char* quest;
  uint32_t hero;
  int32_t player;
  bool completed;
  bool failed;
};

/// An objective is being added to a quest: `tag` is its text tag (the quest log's line). `cancel`
/// adds nothing. Seam: rex_CECQuestTracker_SetObjectiveTagSilently (r3 tracker, r4 quest name, r5
/// tag), the record write behind SetObjectiveTag, SetObjectiveTagFinal and a quest's default.
struct QuestObjectiveEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_QUEST_OBJECTIVE;
  const char* quest;
  const char* tag;
  uint32_t hero;
  int32_t player;
};

/// A creature is about to speak a line: `text` is the line's text hash (the TextManager key, the
/// final tag after the voice and variant suffixes), `listener` the entity it is said to or 0,
/// `subtitle` whether the subtitle is forced. `cancel` keeps the line unspoken (no audio, no
/// subtitle, no speech animation). Seam: rex_CECTalk_PlayLine (r3 talk component, owner at +4;
/// r4 text hash; r5 force subtitle; r6 listener), the one function every line reaches.
struct DialogueEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_DIALOGUE;
  uint32_t speaker;
  int32_t speaker_player;
  uint32_t text;
  uint32_t listener;
  int32_t listener_player;
  bool subtitle;
};

/// An entity joined the world: created by a script or a generator, or loaded with a level or a
/// save. Raised on the first tick after the world's entity manager took it, when its components
/// exist (the manager's add comes before they are built), at most 64 a tick, oldest first, so a
/// level load's burst spreads over a few ticks. Not cancellable. Seam: rex_lhEntityManager_Add
/// (r3 the manager, r4 the entity: +0x14 its 64-bit id; the one add every constructor path reaches).
struct EntitySpawnedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ENTITY_SPAWNED;
  uint32_t entity;
  int32_t player;
  uint32_t definition;
};

/// An entity is leaving the world (killed and cleaned up, destroyed by a script, unloaded with its
/// level): the last moment it is whole. Not cancellable. Seam: rex_lhEntity_Destroy (r3 the entity),
/// before it — the one function that clears the in-world bit (+0x90 & 0x40).
struct EntityRemovedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ENTITY_REMOVED;
  uint32_t entity;
  int32_t player;
  uint32_t definition;
};

/// A creature starts an expression (the wheel, a script's PerformExpression, a trophy): `expression`
/// is the expression record's hash. `cancel` refuses it. Seam:
/// rex_CECExpressionPerformer_StartExpression (r3 performer component, owner at +4; r4 the record
/// pair {hash, entry}; returns 1 when started), before it.
/// (Raised before the game's own can-perform check, which may still refuse it.)
struct ExpressionStartedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_EXPRESSION_STARTED;
  uint32_t entity;
  int32_t player;
  uint32_t expression;
};

/// Renown is changing on an entity's stats (57; +28). `delta` is writable; `cancel` leaves it.
/// `share` marks the co-op partner's share (the game's own recursion, a second event). Seam:
/// rex_lhHeroStats_ModifyRenown (r3 stats, owner at +4; r4 delta; r5 share), before it.
struct RenownEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_RENOWN;
  uint32_t entity;
  int32_t player;
  int32_t delta;
  bool share;
};

/// Morality (good/evil, stats +16) or purity (pure/corrupt, stats +36) is changing: `delta`
/// writable, `cancel` leaves it; the game clamps afterwards. Seams: rex_lhHeroStats_ModifyMorality /
/// rex_lhHeroStats_ModifyPurity (r3 stats, owner at +4; f1 delta), before them.
struct MoralityEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MORALITY;
  uint32_t entity;
  int32_t player;
  float delta;
};

struct PurityEvent : MoralityEvent {
  static constexpr int32_t kKind = REFII_EVENT_PURITY;
};

/// A hero marries (the wedding ceremony or a script). `cancel` refuses it. Seam:
/// rex_CECPlayerFamily_Marry (r3 the hero's family component, owner at +4; r4 the spouse; r5 the
/// ceremony), before it.
struct MarriageEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MARRIAGE;
  uint32_t hero;
  int32_t player;
  uint32_t spouse;
};

/// The dog is told a trick (an expression's completion, a gift, an item, a script): `trick` is the
/// command record's hash. `cancel` refuses it. Seam: rex_CECDog_PerformCommand (r3 dog component,
/// owner at +4; r4 the record pair; r5 force), before it.
struct DogTrickEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_DOG_TRICK;
  uint32_t dog;
  uint32_t trick;
  /// Whose dog: 1 or 2 by its master (the dog component's weak reference at +300, Dog.GetMaster),
  /// else by the players' dog records (refii's Refii.player_dog); 0 for a dog of neither.
  int32_t player;
};

/// A ranged weapon is about to fire (hero and AI alike): `target` the entity aimed at or 0, `aimed`
/// an aimed rather than a hip shot. `cancel` stops the shot. The game may still refuse it (no
/// ammunition). Seam: rex_CECFirearm_Fire (r3 firearm component, owner at +4; r4 target; r5 aimed).
struct RangedShotEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_RANGED_SHOT;
  /// The creature holding the weapon (the weapon's held-item component 83, its holder's weak
  /// reference at +12), or 0.
  uint32_t shooter;
  int32_t player;
  uint32_t target;
  bool aimed;
  /// The weapon's own item entity (the firearm component's owner).
  uint32_t weapon;
};

/// A status effect started or ended on an entity: `effect` 1 electrified (component 205),
/// 2 chaos (206), 3 burning (208), 4 caught in a vortex (209), 5 knocked down (every ragdoll fall:
/// a knockdown, a vortex throw, a kicked chicken, the co-op partner's replica), 6 mauled by the dog
/// (245), 7 unconscious (the hero downed, a script's SetAsUnconscious; ended by waking), 8 a hollow
/// man crumbled to bones (ended when it rises). After the fact. Seams: rex_lhEntity_AddComponent
/// (r3 entity; the new component's id from its own GetECType), rex_lhEntity_RemoveComponent (r3
/// entity, r4 id), rex_lhRagdoll_GoFloppy / _Recover (r3 entity), rex_CECCreature_SetUnconscious
/// (r3 creature component, r4 down), rex_HollowMan_SetCrumbled (r3 action, r4 crumbled).
struct StatusEffectEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_STATUS_EFFECT;
  uint32_t entity;
  int32_t player;
  int32_t effect;
  bool started;
};

/// A player's target changed: `target` is the new one, 0 when it was cleared. After the fact.
/// Seams: rex_CECTargetingPlayer_SetTarget (r3 targeting component 77, owner at +4; r4 target) and
/// _ClearTarget (r3), the player targeting's vtable slots 19 and 18.
struct TargetChangedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_TARGET_CHANGED;
  uint32_t hero;
  int32_t player;
  uint32_t target;
};

/// A villager reacts to a deed, an expression or a gift it saw: `deed` the deed record's hash,
/// `kind` the message kind (0..4), `doer` who did it. `cancel` makes the villager ignore it (no
/// opinion change, no renown, morality or purity from it). Seam: rex_CECOpinionReaction_OnDeed
/// (r3 opinion component, owner at +4; r4 the deed message: +32 record, +52 kind, +72 doer weak ref).
struct OpinionDeedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_OPINION_DEED;
  uint32_t villager;
  uint32_t doer;
  int32_t doer_player;
  uint32_t deed;
  int32_t kind;
};

/// A villager's opinion axis is changed directly, not through a deed (0 love, 1 fear, 2 attractiveness,
/// 3 morality, 4 respect): `delta` writable, `cancel` leaves it. Seam:
/// rex_CECOpinionReaction_ModifyAxisValue (r3 component, owner at +4; r4 axis; f1 delta).
struct OpinionModifiedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_OPINION_MODIFIED;
  uint32_t villager;
  int32_t axis;
  float delta;
  /// Whose opinion set moves: 2 while the component holds player two's set (their opinion scope
  /// for this villager), else 1.
  int32_t player;
};

/// The world clock reached a new hour (`hour` 0..23) or a new day (`day`): natural time, a fast
/// forward, sleeping, travel time and a script's set all show here, a set one tick late. `time` is
/// the day fraction 0..1. Seam: rex_lhGameTimeManager_Update (r3 the time manager: +4 time,
/// +8 day count), after it.
struct TimeOfDayEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_TIME_OF_DAY;
  int32_t hour;
  int32_t day;
  float time;
  bool new_day;
};

/// An environment theme (the weather and the look of the sky) is blended to: `theme` its name,
/// `weight` and `seconds` writable. `cancel` skips the blend. Position-painted blending never comes
/// here. Seam: rex_lhEnvironmentThemeManager_BlendToTheme (r3 manager; r4 name; f1 weight; f2 seconds).
struct WeatherThemeEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_WEATHER_THEME;
  const char* theme;
  float weight;
  float seconds;
};

/// The bed's sleep menu is about to open (the time skip that follows is the shipped Lua's).
/// `cancel` keeps it closed. Seam: rex_SleepMenu_Show.
struct SleepMenuEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SLEEP_MENU;
  /// Always true: the event itself is the news.
  bool seen;
  /// The player who is sleeping: the user the game's use of the bed passed to its script
  /// (rex_CECOnActionUse_Use, r4; OnActionUseBed hands it to SleepFunction.Sleep); 0 when no
  /// player used it.
  int32_t player;
};

/// A travel started: a door, a level exit, a fast travel, a script. `full_load` is false for a
/// change inside the loaded level (a new entrance in the same region), which LevelLoaded never
/// reports. After the game decided. Seam: rex_CmdLevelChange_Start (r3 the command: +20 world,
/// +24 level, +28 entrance names, +36 travel type, +50 full load), after it.
struct TravelEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_TRAVEL;
  const char* world;
  const char* level;
  const char* entrance;
  int32_t type;
  bool full_load;
  /// The player who asked for it (both heroes travel): the one a level exit fired for (its user,
  /// the player its walk-in test tested, an online joiner's request) or the world map's owner for
  /// a fast travel; 0 for a script's travel, or one this machine did not see asked for.
  int32_t player;
};

/// The hero chose a fast-travel stop (the world map, a script): `level` and `stop`, `paid`.
/// `cancel` stays put (nothing is paid). Seam: rex_FastTravelManager_TravelToStop (r3 manager;
/// r4 level name; r5 stop, +4 its marker name; r6 paid), before it.
struct FastTravelEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_FAST_TRAVEL;
  const char* level;
  const char* stop;
  bool paid;
  /// The player who chose it: the world map's owner (rex_lhGuiWorldMap_FastTravel, the map's
  /// travel, around the call); 0 for a script's.
  int32_t player;
};

/// The game entered its menu mode (the world pauses behind it) or left it — real transitions only;
/// MenuScreen states each screen a menu changes to (0 pause, 2 spells, 14 abilities, 16 equipment,
/// 17 logbook, 18 options, 22 shop, 23 world map, 24 co-op, …; TU1 numbering on both builds).
/// Seams: rex_lhGuiManager_EnterGuiMode / _EnterGuiModeQuiet (when menu mode was off),
/// rex_lhGuiManager_ExitScreen (when it really leaves), rex_lhGuiManager_SetScreen (r4 the id).
/// `player` (all three) is whose menu it is: 2 on an online joiner, in local co-op while player
/// two owns the pause (the pause screen's +4, set by its vtable slot 8 before the screen and the
/// menu mode are entered) or their shop; 1 otherwise.
struct MenuOpenedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MENU_OPENED;
  /// Always true: the event itself is the news.
  bool seen;
  int32_t player;
};

struct MenuClosedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MENU_CLOSED;
  /// Always true: the event itself is the news.
  bool seen;
  int32_t player;
};

struct MenuScreenEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MENU_SCREEN;
  int32_t screen;
  int32_t player;
};

/// The hero walked into a different PAINTED weather region: `day_set` is the region's
/// environment day set (a GDB record hash; the day set picks the theme by time of day), 0 outside
/// every painted region; `previous` the one before. Read-only. A scripted blend is WeatherTheme.
/// Seam: rex_lhPaintMap_DaySetAt (r3 the paint map, v1 the hero's position; returns the day set
/// hash or -1), called once a frame by the painted-weight pass (sub_821ABFD8, its one caller) for
/// the hero in player one's slot (players+0x6C) — never once per split-screen view.
struct WeatherRegionEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_WEATHER_REGION;
  uint32_t day_set;
  uint32_t previous;
  /// The hero is inside a painted region (day_set is only meaningful then).
  bool inside;
  /// The player whose hero the pass tested (1 as shipped); `previous` is that player's own.
  int32_t player;
};

/// The player answered a bed's sleep menu: `choice` 0..3, `cancelled`, the `bed`. What each choice
/// does is the shipped SleepFunction.Sleep's (sleepfunctions.lua, reading Bed.GetSleepType = choice
/// + 1, 0 cancelled): 0 fast-forwards six hours, 1 twelve hours, 2 adds one day without moving the
/// clock, 3 adds seven days (its enum is named SLEEP_FORTY_EIGHT_HOURS). `choice` is writable and
/// `cancel` refuses the sleep; a length the menu does not offer is a Lua mod wrapping
/// SleepFunction.Sleep. Seam: rex_lhMenuBox_Close (r3 the
/// shared choice menu: +0 the selected index, +13 cancelled), while the sleep menu is the one open.
struct SleepChosenEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SLEEP_CHOSEN;
  uint32_t bed;
  int32_t choice;
  bool cancelled;
  /// The player who is sleeping, as SleepMenu stated it for this menu.
  int32_t player;
};

/// Slow Time changed the world's time scale: `scale` (1 is normal; writable before the game
/// applies it) and whether it is now `active` (scale < 1). Every creature but the caster slows —
/// the game slows the world and gives the caster (and the co-op partner) the inverse rate.
/// Seam: rex_SpellSlowTime_SetWorldScale (r3 the caster; f1 the scale; world +128 the scale).
struct SlowTimeEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SLOW_TIME;
  uint32_t caster;
  int32_t player;
  float scale;
  bool active;
};

/// A spell summoned a creature or a weapon: `kind` 1 a Raise Dead minion, 2 a Blades sword (an
/// entity of its own). The summon leaves the world through EntityRemoved. After the fact. Seams:
/// rex_SpellRaiseDead_BindSummoner (r3 summoner, r4 the summoned entity) and
/// rex_SpellDamoclesSword_Create (r3 the sword object, its entity's weak ref at +0; r5 the caster).
struct SummonedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SUMMONED;
  uint32_t summoner;
  int32_t player;
  uint32_t summon;
  int32_t kind;
};

/// A crime is recorded against someone: `criminal` committed crime `type` (the game's crime
/// enum: 2 murder, 3 assault, 6 trespass, 11 vandalism are among them) on `victim` (0 when none).
/// `cancel` drops it: no witness record, no bounty, no guards. The co-op partner's crimes are their
/// own. Seam: rex_CECCrime_Add (r3 the crime component, r4 the criminal, r5 the victim, r6 the
/// type, r7 a flag), which also records assault against hero one when a murder is reported.
struct CrimeEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_CRIME;
  uint32_t criminal;
  int32_t player;
  uint32_t victim;
  int32_t victim_player;
  int32_t type;
};

/// An achievement unlocks (the game's ids 1..50; scripts and the stats code both unlock through
/// this). It is written for BOTH heroes. `source` is the entity whose stats asked. `cancel` keeps
/// it locked. Seam: rex_CECStats_UnlockAchievement (r3 the stats component, owner at +4; r4 the id).
struct AchievementEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ACHIEVEMENT;
  int32_t id;
  uint32_t source;
  int32_t player;
};

/// A player picks up a one-time collectable that is kept per player in co-op (a silver key):
/// `stays` is true when the entity stays in the world for the partner, who takes their own.
/// After the decision, before the pickup. Seam: rex_CECInventory_AddEntity (coop_world_hooks.cpp).
struct CollectableTakenEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_COLLECTABLE_TAKEN;
  int32_t player;
  uint32_t entity;
  uint32_t record;
  bool stays;
};

/// A co-op script asks the other player's consent (a bed's sleep or sex, or a mod's own question
/// from Refii.ask_partner): `kind` 1 sleep (`detail` the sleep type), 2 sex, 0 a mod's question;
/// `asker` the asking player. `answer` (writable) answers for the partner: -1 asks them (the
/// default), 0 refuses, 1 agrees. Seam: the consent ask (mod_lua_natives.cpp).
struct PartnerConsentEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_PARTNER_CONSENT;
  uint32_t ticket;
  int32_t kind;
  int32_t asker;
  int32_t detail;
  int32_t answer;
};

// --- PROPERTY AND HOUSEHOLD (2026-10-20) ------------------------------------------------------
// Online, all of these fire on the host, where player two's household and property live and the
// economy is (household_persist.cpp); an online joiner's own actions are forwarded there and fire
// there. Each fires once per occurrence.

/// A hero buys a building (before the purchase). `price` (writable) is what it costs, condition
/// included. `cancel` keeps the building unbought and the gold where it is. Seam:
/// rex_CECPlayerProperties_Buy (r3 the buyer's properties comp69, r4 the building's entity).
struct PropertyBoughtEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_PROPERTY_BOUGHT;
  uint32_t hero;
  int32_t player;
  uint32_t building;
  int32_t price;
};

/// A hero sells a building (before the sale). `price` (writable) is what they are paid; a shared
/// building's half then goes to the partner. `cancel` keeps it owned. Seam:
/// rex_CECPlayerProperties_Sell (r3 the seller's comp69, r4 the building's entity).
struct PropertySoldEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_PROPERTY_SOLD;
  uint32_t hero;
  int32_t player;
  uint32_t building;
  int32_t price;
};

/// A hero's rent and shop profits were paid (after the payout): `gold` in all, for `drops` five-minute
/// drops; `away` when it is the catch-up for time spent out of the game (paid on the next screen).
/// Seam: rex_CECPlayerProperties_PayIncome (r3 comp69, r4 drops, r5 away; the total in r3 after).
struct RentPaidEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_RENT_PAID;
  uint32_t hero;
  int32_t player;
  int32_t gold;
  int32_t drops;
  bool away;
};

/// A building is rented out or its tenants evicted (before). `cancel` leaves it as it was. Seam:
/// rex_CECPlayerProperties_SetRentedOut (r3 comp69, r4 the building's entity, r5 rented).
struct PropertyRentedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_PROPERTY_RENTED;
  uint32_t hero;
  int32_t player;
  uint32_t building;
  bool rented;
};

/// A building's condition changed (Fable 3's, 0..1): a rented home wore down at a rent drop, or it was
/// repaired. After the fact. `uid` is the building's UniqueID ("0x…"). Seam: property_hooks.cpp.
struct PropertyConditionEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_PROPERTY_CONDITION;
  uint64_t uid;
  int32_t player;
  float condition;
  float previous;
  bool repaired;
};

/// A marriage ended in divorce (the spouse has left). After the fact. Seam:
/// rex_CECPlayerFamily_FinishDivorcing (r3 the hero's family comp136, r4 the spouse).
struct DivorceEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_DIVORCE;
  uint32_t hero;
  int32_t player;
  uint32_t spouse;
};

/// A hero's spouse died or was removed: the household ends. After the fact. `uid` is the late
/// spouse's UniqueID. Seam: rex_CECPlayerFamily_Widow (r3 the family comp136, r4 &the spouse's UID).
struct WidowedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_WIDOWED;
  uint32_t hero;
  int32_t player;
  uint64_t uid;
};

/// A child is born to (or adopted by) a hero and their spouse. After the fact. Seam:
/// rex_CECPlayerFamily_AdoptWithSpouse (r3 the family comp136, r4 the spouse, r5 the child).
struct ChildBornEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_CHILD_BORN;
  uint32_t hero;
  int32_t player;
  uint32_t spouse;
  uint32_t child;
};

/// A hero gives a gift (a ring among them) to the one they target (before). The item leaves their
/// inventory and comes back after fifteen seconds unless the villager keeps it (GiftAccepted).
/// `cancel` gives nothing. Seam: rex_lhGiftComponent_GiveGift (r3 the giver's comp33, r4 the item).
struct GiftGivenEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_GIFT_GIVEN;
  uint32_t hero;
  int32_t player;
  uint32_t target;
  uint32_t item;
};

/// A villager kept a hero's gift: it is theirs for good, and the gift's morality applies. After
/// the fact. Seam: rex_CECHeroGifts_RemovePermanently (r3 the giver's comp33, the item's record as
/// the Lua argument).
struct GiftAcceptedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_GIFT_ACCEPTED;
  uint32_t hero;
  int32_t player;
  uint32_t item;
};

/// A villager becomes engaged to a hero (or the engagement ends). `cancel` leaves it as it was.
/// Seam: rex_CECOpinionReaction_SetEngaged (r3 the villager's comp47, r4 engaged).
struct EngagedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ENGAGED;
  uint32_t villager;
  int32_t player;
  bool engaged;
};

/// A creature starts or stops following someone (a hero's follower, a fiancé, a partner for the
/// night). `cancel` on a start keeps it from following. Seams: rex_CECFollow_Start (r3 the
/// follower's comp94, r4 the leader) / rex_CECFollow_Stop (r3 comp94).
struct FollowEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_FOLLOW;
  uint32_t follower;
  uint32_t leader;
  int32_t player;
  bool following;
};

/// A piece of upgradeable furniture changes level (redecorating, with a furniture card). `level`
/// (writable) is the new level. `cancel` leaves it. Seam: rex_CECUpgradeableFurniture_SetLevel (r3
/// the piece's comp68, r4 the level).
struct FurnitureUpgradedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_FURNITURE_UPGRADED;
  uint32_t piece;
  int32_t player;
  int32_t level;
  int32_t previous;
};

/// Redecorating a building starts or ends (its furniture becomes usable for upgrades). Fires on the
/// machine of the player redecorating. Seam: rex_CECBuilding_SetRedecorating (r3 comp67, r4 on).
struct RedecoratingEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_REDECORATING;
  uint32_t building;
  int32_t player;
  bool on;
};

/// A hero sets a household's budget at its home (the upkeep it is given). `gold` (writable) is the
/// budget. `cancel` leaves it. Seam: rex_lhGuiRealEstateScreen_SetBudget (r3 the estate screen, r4
/// the gold).
struct HouseholdBudgetEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_HOUSEHOLD_BUDGET;
  uint32_t hero;
  int32_t player;
  uint32_t building;
  int32_t gold;
};

/// A hero's tracked stat counts up (the game's ETrackedStatType: kills, sex, marriages, children, gold
/// spent and many more; achievements and titles are judged from them). `amount` (writable) is what
/// is added. `cancel` adds nothing. Seam: rex_CECStats_ModifyTrackedStat (r3 the hero's stats comp57,
/// r4 the stat, r5 the amount).
struct TrackedStatEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_TRACKED_STAT;
  uint32_t hero;
  int32_t player;
  int32_t stat;
  int32_t amount;
};

/// Fable 3's player-to-player partnership between the two heroes changed: `kind` 0 none, 1
/// business partners, 2 married (core/hero_partners.cpp). After the fact. Seam: the household tick.
struct PartnershipEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_PARTNERSHIP;
  int32_t kind;
  int32_t previous;
};

/// Player two is back in the host's world and their household and property, kept with the world,
/// were restored onto their hero (household_persist.cpp). After the fact.
struct HouseholdRestoredEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_HOUSEHOLD_RESTORED;
  uint32_t hero;
  int32_t player;
  int32_t spouses;
};

/// The dynamic weather in effect changed (Godot's dynamic sky decides it: the player's override,
/// else the level's rule — fixed, interior, the game's own theme, or a roll that drifts). One
/// weather per co-op session: the host decides it, an online joiner adopts the host's statement
/// for its level; each player's sky switch only changes whether it is drawn. Fires on both
/// machines: the host when it decides, the joiner when it adopts. After the fact. Seam: the guest
/// tick, from the host's "ModWeather:" pump action (Engine_DynamicWeatherChoice's change).
struct DynamicWeatherEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_DYNAMIC_WEATHER;
  /// The level the weather is for, "<level>/<scenario>" lower-case, as the statement names it.
  const char* level;
  /// The weather: a preset's name (Clear, PartlyCloudy, Cloudy, Overcast, Rain, Storm, Snow, Fog,
  /// LightRain, LightSnow, Blizzard), "theme" (the game's own theme weather) or "interior".
  const char* weather;
  /// The weather before, the same way; empty for the first one of a session.
  const char* previous;
  /// How long the change blends over (0: at once).
  float seconds;
  /// An online joiner adopting the host's statement; false where this machine decided it.
  bool from_host;
};

/// A row of a mod's menu was chosen in Godot: a row of a menu a mod defines in ui/templates.json,
/// a row a mod patched into a shipped menu, or any row whose command is Opens:<menu> or
/// Mod:lua:<function>. `menu` is the menu the row is on (the mod menu's name, or the list's own:
/// frontend-root, pause-root, a screen's population script), `row` the row's name, `player` 1 or 2
/// (whose menu it is), `mod` the mod that stated the row or its menu ("" for a shipped row). The
/// menu is Godot's, so Godot scripts get it as it happens; native and Lua handlers get it on the
/// next guest tick, raised from the host's command queue (`menu_row`). The guest ticks on the
/// title and the front end too, so native handlers hear a front-end row; Lua handlers live in the
/// game VM, which exists only in a game. Not cancellable.
struct ModMenuRowEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MOD_MENU_ROW;
  const char* menu;
  const char* row;
  int32_t player;
  const char* mod;
};

/// A message box a mod raised (refii_mod_message_box, Refii.message_box, RefiiHost.message_box) was
/// answered. `id` is what the call returned (the payload's ids are 1 .. 2^23-1, Godot's 2^23 ..
/// 2^24-1), `button` 0 for A, 1 for B, -1 when another box replaced it or its player is played on
/// the other machine, `player` 1 or 2 (whose box it was). The box is Godot's, so Godot scripts get
/// it as it happens; native and Lua handlers on the next guest tick, from the host's command queue
/// (`box_answer`) — native on the front end too, Lua in a game. Not cancellable.
struct ModMessageBoxEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MOD_MESSAGE_BOX;
  uint32_t id;
  int32_t button;
  int32_t player;
};

/// A notice a mod posted (refii_mod_notice, Refii.notice, RefiiHost.notice) has gone. `id` is what
/// the call returned, `outcome` 0 once it was shown and its time ran out (or it was dismissed on the
/// front end), -1 when it never showed (its player is played on the other machine, or the queue
/// overflowed), `player` 1 or 2 (whose notice it was). The channel is Godot's, so Godot scripts get
/// it as it happens; native and Lua handlers on the next guest tick, from the host's command queue
/// (`notice_done`) — native on the front end too, Lua in a game. Not cancellable.
struct ModNoticeEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MOD_NOTICE;
  uint32_t id;
  int32_t outcome;
  int32_t player;
};

/// A menu box a mod raised (refii_mod_menu_box, Refii.menu_box, RefiiHost.menu_box) was answered.
/// `id` is what the call returned, `option` the chosen option's index in the list the mod gave
/// (paging is the box's own), -1 for B on a cancelable box, another box replacing it, or its player
/// played on the other machine; `player` 1 or 2 (whose box it was). Godot scripts get it as it
/// happens; native and Lua handlers on the next guest tick (`menu_box_answer`) — native on the
/// front end too, Lua in a game. Not cancellable.
struct ModMenuBoxEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MOD_MENU_BOX;
  uint32_t id;
  int32_t option;
  int32_t player;
};

/// A player is buying or giving back a level of a MOD ability (ui/abilities.json). The ENGINE runs
/// the purchase from what the file declares — the level is the player's SAVED value under
/// `levelKey`, the price the game's own table of the ability `costLike` names
/// (rex_CECStats_AbilityExperienceCost), split evenly across the declared disciplines' pools (the
/// first named takes the odd point), each share spent through the game's own Spend; an unlearn
/// (`"refund": "half"`) gives half the given-up level's price back into General through the game's
/// own Modify, as the shipped unlearn does. The book's A and Y post it for the player whose book it
/// is, and it runs on the guest tick of that player's machine. Raised BEFORE anything changes and
/// only for a purchase that can happen (affordable, below the maximum; a level to give up):
/// `cancel` refuses it — nothing spent, the level stands. `ability` is the declared name, `mod` the
/// mod that declared it, `refund` true for an unlearn, `previous` the level held, `level` the level
/// it will hold, `experience` the price to be spent (a buy) or what comes back (a refund), `player`
/// 1 or 2. A mod applies the level's effect by reading the level from the player store
/// (refii_mod_player_value(player, levelKey, 0)) where it is used; it needs no handler for that.
struct ModAbilityEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MOD_ABILITY;
  const char* ability;
  const char* mod;
  bool refund;
  int32_t previous;
  int32_t level;
  int32_t experience;
  int32_t player;
};

/// A player chose (or cleared) a pick on one of their spell slots on the spell selectors — the
/// quick wheel or the abilities blade — through a pick a mod states in ui/spellwheel.json (Spell
/// Weaving's partner). The engine keeps each player's pick per slot as a SAVED player value under
/// the pick's `store` key ({slot} the slot's number, 1..5), written after the handlers unless one
/// cancels. `pick` is the pick's name, `mod` the mod that stated it, `slot` 0..4, `spell` the will
/// chosen (0 Lightning .. 7 Dead Rising; -1 cleared) — writable: a handler may choose another, or
/// -1 — `previous` the will held before (-1 none), `player` 1 or 2 (whose selectors). Raised on the
/// guest tick of the machine whose player made the pick, from the host's command queue
/// (`spell_pick`); Godot scripts get it from the bus like any other event. `cancel` keeps the slot's
/// pick as it was.
/// THE GAME'S OWN PICK (the slot's spell) is raised too, with `pick` "spell" and `mod` "", by the
/// guest's slot writer whenever the game calls it as a choice — each step of the selectors'
/// carousel, the scripts' spell-table natives, the host's own assignment (mod_abilities.cpp
/// rex_CECSpellManager_SetSpellSlot) — before the slot is written: a changed `spell` is the one
/// stored and sent to the session, `cancel` leaves the slot as it was. The template's defaults, the
/// other machine's pick arriving and a save load are not picks and raise nothing.
struct SpellPickEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SPELL_PICK;
  const char* pick;
  const char* mod;
  int32_t slot;
  int32_t spell;
  int32_t previous;
  int32_t player;
};

/// A mod's message from THE OTHER MACHINE of an online session (refii_mod_send_to_partner,
/// Refii.send_to_partner, RefiiHost.send_to_partner there): `mod` the mod that sent it, `name` its
/// word, `payload` its text (at most REFII_MOD_PARTNER_PAYLOAD_MAX bytes; on the Lua and Godot
/// surfaces a quote, backslash or line break in it arrives as '_'), `from` the player that machine
/// plays (1 the host, 2 the joiner). Raised on the receiving machine's guest tick, once per message
/// and in the order sent — the route (core/event_relay.h) carries it — and never on the sender's.
/// Not a replica: this machine's own handling, so player-state writes in a handler count. Never
/// raised alone or in local co-op. Not cancellable.
struct PartnerMessageEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_PARTNER_MESSAGE;
  const char* mod;
  const char* name;
  const char* payload;
  int32_t from;
};

/// A player hero's LOOK-AT flag is being written — the byte every lock camera script reads and
/// the one a held LT states. Seam: rex_CPlayer_SetLookAtButtonDown (r3 the player component 33,
/// its owner entity at +4 — `stw r4,4(r31)` in its constructor 0x823e0110 / disc 0x823beb20; r4
/// the value; `stb r29,0x20(r30)` 0x823e9f34 / disc 0x823c8964, after `lbz r10,0x20(r30); cmplw;
/// beq` returns unchanged on an equal value). Its callers are the camera's input event on LT
/// down (`sub_825ADD58`, `li r4,1` 0x825ae6e0), the hero's message handler on the release
/// (`sub_826E5B30` case 0x32, `li r4,0` 0x826e5fc0), and the API's own refii_mod_lock_target /
/// refii_mod_release_lock. `down` is WRITABLE: the value the game stores — a mod whose lock is
/// its own keeps the flag equal to it whoever asks. `cancel` leaves the flag as it was (the
/// writer never runs, so the camera mode is not re-picked either). Raised for player heroes only.
struct LookAtButtonEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_LOOK_AT_BUTTON;
  uint32_t hero;
  int32_t player;
  /// The value being written (writable).
  bool down;
  /// The player component (33) the flag lives on (native).
  uint32_t component;
};

/// A VORTEX is live: its component (CECVortex, 211, on the vortex entity) was initialised by the
/// vortex spell's post-init, inside the spell factory and BEFORE SpellCreated reports the spell
/// object — which marks itself finished at once, so no registry lookup ever returns a vortex.
/// Seam: rex_VortexComponent_Init (r3 the component, owner entity at +4 from its constructor
/// `stw r4,4(r31)` 0x82757720; r4 the caster entity, kept as a weak reference at +0x160/+0x164;
/// r5 the target; r6 the cast's serial -> +0x18 `stw r25,0x18(r31)` 0x827588f4; r8 the level ->
/// +0x14; the call site `sub_828B20D0` 0x828b23c8..0x828b2438 reads them from the spell's +0x40
/// and +0x44), after it. `x, y, z` is the vortex entity's position then. Online the cast runs on
/// both machines (refii_mod_event_replica tells the copy). Read-only.
struct VortexStartedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_VORTEX_STARTED;
  /// The live component: refii_mod_spell_parts(serial) finds it again while it lives (native).
  uint32_t vortex;
  /// The vortex entity (the component's owner), the thing in the world the look rides.
  uint32_t entity;
  uint32_t caster;
  int32_t caster_player;
  uint32_t target;
  uint32_t serial;
  int32_t level;
  float x;
  float y;
  float z;
};

/// The vortex is ending: its component's Shutdown, BEFORE it runs, with the twister's last point.
/// Seam: rex_VortexComponent_Shutdown (r3 the component; +0x140 is the point the update hands
/// every ground effect — `li r28,0x140; lvx128 v1,r31,r28` 0x82759210..38 — and writes back as the
/// twister moves, `stvx128 v127,r29,r25` 0x82759bdc, then places the entity there through its
/// placement, 0x82759be4..f4; Shutdown stores nothing to +4/+0x18/+0x140). Read-only.
struct VortexEndedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_VORTEX_ENDED;
  uint32_t vortex;
  uint32_t entity;
  uint32_t caster;
  int32_t caster_player;
  uint32_t serial;
  /// The twister's point (component +0x140).
  float x;
  float y;
  float z;
};

/// A targeted FIREBALL struck something: its Impact ran (the guard `lbz r11,0x48(r31); bne`
/// 0x82744234 passed — a fireball already detonated returns at once), after it. Seam:
/// rex_TargetedFireball_Impact (r3 the spell object, r4 what it struck — an entity, 0 for ground,
/// wall or water — v1 the impact position, v2 the impact normal, f1 a range, r6 the reached-target
/// flag). `detonated` is the exploded byte (spell+0x48) going 0 -> 1 inside the call: the blast
/// (`sub_827478D8`, every victim's hit delivered inside the call) and then `stb r24,0x48(r31)`
/// 0x827446e4 — a struck creature the fireball passes (no blast) leaves it clear and the fireball
/// flies on. `hits` counts the hits the receiver took inside the call carrying this cast's serial
/// (hit+0x44): 0 on a detonation means the blast found nothing alive. Online the cast runs on both
/// machines; the hits are decided where refii_mod_world_here holds. Read-only.
struct FireballImpactEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_FIREBALL_IMPACT;
  /// The spell object (native).
  uint32_t spell;
  uint32_t serial;
  int32_t level;
  uint32_t caster;
  int32_t caster_player;
  uint32_t struck;
  int32_t struck_player;
  /// The impact position.
  float x;
  float y;
  float z;
  /// The impact normal.
  float nx;
  float ny;
  float nz;
  bool detonated;
  int32_t hits;
};

/// THE GAME'S PAUSE STATE flipped: a GameFlow mode command of the pause family became the mode in
/// force, or stopped being it. The game's own test is rex_lhGameFlow_IsGuiActive (`sub_821ECA30`:
/// the top of the mode stack at `*(*(*(game+0x80)+4)+4)`, its vtable slot 1 == 1), which the
/// gameplay loop asks every frame (`bl sub_821ECA30` 0x8227711c in `sub_82276D10`). The state is
/// SET by the mode command's apply, vtable slot +20 of `off_820AEC7C` = `sub_823D4D00` (disc
/// `sub_8245C2E0`, same 276 instructions: `bl sub_823D48E8` — both players' input categories —
/// then `li r4,1; bl sub_823D4A50` 0x823d4d14..1c), run by GameFlow_QueueCommand's push
/// (0x82318718: `(*cmd->vtable+20)(cmd)` as its last call) and again when a command above it
/// is removed (`sub_82318800` 0x82318800: re-applies the new top); and CLEARED by its remove,
/// slot +32 = `sub_823D5150` (disc `sub_8245C730`: `li r4,0; bl sub_823D4A50`), run by the
/// removal and by a push of another command over it. Mode 1 (slot 1 `li r3,1`, `sub_82EA63A8`)
/// is the GUI/pause command built by `sub_823D4C48` (input categories {12,10,9,11,1} on both
/// players): queued by rex_lhGuiManager_EnterGuiMode (0x8236ed3c, the pause menu and every
/// screen entered from play), _EnterGuiModeQuiet (0x82370100), the partner's relayed pause
/// (`sub_8232B2B0` "MULTI_PLAYER_OTHER_PLAYER_PAUSED_GAME", 0x8232b33c) and the camera's special
/// modes (`sub_825AD7E8`). Mode 2 (`off_820AECA0`, slot 1 `li r3,2`, the same apply/remove through
/// thunks `sub_823D53E0` / `sub_823D54F0`) is the level change (World_RequestLevelChange
/// 0x82350ca0..cc8, `sub_82313328`). Seams: rex_lhGameFlowGuiMode_Apply / _Remove (r3 the
/// command; its id by vtable slot 1), after each. Read-only.
struct PausedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_PAUSED;
  /// True after the apply (the mode is in force), false after the remove.
  bool paused;
  /// The command's id: 1 the pause / menu mode, 2 a level change.
  int32_t mode;
  /// For mode 1 with a menu screen up: whose menu (MenuOwnerNow: 2 for an online joiner or local
  /// player two's pause book, else 1); 0 for a pause with no screen (the partner's relayed one, a
  /// camera mode) and for mode 2.
  int32_t player;
  /// The GUI manager's screen id then (TU1 numbering; 31 none, 0 the pause root, 29 co-op).
  int32_t screen;
};

/// THE FRONT END STARTS A GAME: the title's New Game, Load, Continue or a co-op join state the
/// target through rex_lhFrontEnd_SetStartGameTarget (`sub_82379838`, disc `sub_8237A2D8`, same 52
/// instructions: r3 the front-end object, r4 the save name LHString (empty for a new game —
/// `sub_822651A0(&g_PendingLoadSaveName, a2)`), r5 the start level LHString
/// (`sub_822651A0(&g_StartLevelName, a3)`); it then flags the game-state change
/// `*(*(0x83496AC0+12)+92) = 1` and creates the in-game state `sub_82319928`). Callers: the root
/// screen's action handler 0x826df880, GUI_FrontendRoot_MenuActionDispatch 0x826e0498, the
/// load-game page 0x826e0f34, and the host's own New Game pump action. Raised BEFORE the call,
/// read-only; on the title there is no game VM, so Lua hears it in mod/frontend.lua.
struct StartGameEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_START_GAME;
  /// The save's name ("" for a new game).
  const char* save;
  /// The level the game starts in.
  const char* level;
  bool new_game;
  /// The signed-in pad's player: 1.
  int32_t player;
};

/// Subscribes a typed handler: `void fn(E&)`. Returns the subscription index.
template <typename E>
inline int32_t Subscribe(void (*fn)(E&), int32_t priority = 0, const char* owner = REFII_MOD_NAME) {
  return refii_mod_subscribe(
      E::kKind,
      [](void* event, void (*user)(void)) { reinterpret_cast<void (*)(E&)>(user)(*static_cast<E*>(event)); },
      reinterpret_cast<void (*)(void)>(fn), priority, owner);
}

/// Fires a typed event through every subscriber; the struct's state afterwards is the verdict.
template <typename E>
inline void Dispatch(E& event) {
  refii_mod_dispatch(E::kKind, &event);
}

}  // namespace refii::mods

/// Subscribes `fn` (a `void fn(refii::mods::<Kind>Event&)`) when the game module loads:
///   static void OnHit(refii::mods::HitEvent& e) { ... }
///   REFII_MOD_ON(Hit, OnHit);
/// REFII_MOD_ON_PRIORITY(Hit, OnHit, 10) runs before priority-0 handlers.
#define REFII_MOD_ON(Kind, fn) \
  static const int32_t refii_mod_on_##Kind##_##fn = refii::mods::Subscribe<refii::mods::Kind##Event>(&fn)
#define REFII_MOD_ON_PRIORITY(Kind, fn, priority) \
  static const int32_t refii_mod_on_##Kind##_##fn = refii::mods::Subscribe<refii::mods::Kind##Event>(&fn, (priority))
