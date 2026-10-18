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

#pragma once

#include <cstdint>

#include <rex/ppc/context.h>

#define REFII_MOD_API_VERSION 1

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
  REFII_EVENT_COUNT = 33,
};

/// A subscriber: `event` is the kind's struct (below), `user` is what was registered with it.
typedef void (*refii_mod_event_fn)(void* event, void (*user)(void));

/// Subscribes `fn` to `kind` at `priority` (higher runs first). `owner` is the mod's name, for
/// the log. Returns the subscription's index, or -1 for an unknown kind.
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
/// no block. Fired once per hit (the game runs two block listeners per hit, pose and response;
/// the second takes the first's verdict). `unblockable` hits bypass blocks in the game already
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
};

/// A creature is dying: the one function every creature death goes through, from damage (the
/// health component at zero), from a script kill, from a fall. `cancel` keeps it alive: the
/// death is not started and the creature's health is set to 1 so the next hit asks again (the
/// game's own CantDie answer). Heroes never come here from damage — see HeroDowned. Seam:
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

/// An item is being added to an entity's inventory (component 13): a pickup, a purchase, a gift,
/// a quest reward, a script, the starting kit — by its definition's hash (the GDB record). One
/// more of an item already held raises its count. `cancel` adds nothing. Seam:
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
  /// The item entity: known on an attach, 0 on a release until the game has done it.
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
