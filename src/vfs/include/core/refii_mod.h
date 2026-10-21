// refii — the surface a HARD MOD's native sources include. One header, C linkage underneath.
//
// A hard mod ships .cpp files under native/ in its package. The launcher compiles them into the
// game module on the player's machine, in the same step and with the same compiler as refii's
// own hooks (docs/hard_mods_plan.md). This header is the contract:
//
//   OVERRIDE a game function by its generated name, and run the original through __imp__:
//
//     #include "core/refii_mod.h"
//
//     REFII_MOD_ORIGINAL(rex_CECFirearm_Serialize);
//
//     REX_EXTERN(rex_CECFirearm_Serialize) {
//       REX_FUNC_PROLOGUE();
//       __imp__rex_CECFirearm_Serialize(ctx, base);   // the game's own body
//     }
//
//   NAMES, NOT ADDRESSES. Two builds of the game are supported and share no address, so hook a
//   function by the name versions/<build>/refii_config.toml gives it. A sub_XXXXXXXX name binds
//   on one build only; the launcher refuses a name the build does not declare, and says which
//   config name it has when there is one.
//
//   SHARED FUNCTIONS CHAIN. When more than one owner (a mod, refii) hooks a function, the launcher
//   compiles each hook under its own symbol and links them in load order: this hook's
//   __imp__name call reaches the next owner inward, and the innermost reaches the game. A hook
//   that returns without calling __imp__name stops the chain (a gate). A hook that NEVER calls
//   __imp__name replaces the body and must be the only owner of that function; otherwise the
//   build is refused with both names.
//
//   ANOTHER MOD'S API. `requires = ["that_mod"]` in mod.toml puts that mod beneath this one and
//   its native/include/ on this mod's include path; its extern "C" functions link directly, and
//   its published values are read with refii::mods::Published.
//
//   GUEST MEMORY is big-endian at `base + address`; use the helpers below. A raw TU1 address
//   goes through refii::guest::Address() so it is right on the disc build too.
//
//   TALK TO GODOT with refii::mods::Publish("<mod>.<name>", value) and read settings with
//   refii::mods::Setting(...). A mod's HUD piece binds to "mod:<mod>.<name>".
//
//   EVENTS FIRST, HOOKS SECOND. core/refii_mod_events.h (included below) names the game's seams
//   as typed events — a hit, a swing, a block, a spell launch, experience changing, the tick —
//   that a mod subscribes to with REFII_MOD_ON(Hit, fn). An event never conflicts with another
//   mod and needs no function name. Hook a function directly only for a seam no event covers.
//
//   INCLUDE NOTHING ELSE FROM REFII. Anything else is C++ that the game module must not import
//   from refii's libraries; the platform's own C/C++ headers are fine.

#pragma once

#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/guest_build.h"
#include "core/mod_values.h"
#include "core/rex_payload_bridge.h"

/// Declares the game's own body of `name`, callable as __imp__name(ctx, base).
#define REFII_MOD_ORIGINAL(name) extern "C" REX_FUNC(__imp__##name)

/// A mod's per-tick callback: the guest's context (a copy, safe to use for guest calls), guest
/// memory, and the real seconds since the last tick (capped at a quarter second across a hitch).
/// Runs on the guest tick thread every game tick, menus included.
typedef void (*refii_mod_tick_fn)(PPCContext* ctx, uint8_t* base, float seconds);

extern "C" void refii_mod_register_tick(refii_mod_tick_fn fn);

/// The local player's hero entity, or 0 when there is none (front end, loading): the guest's own
/// local-player rule — player two's hero on an online joiner, player one's on a host or alone, and
/// in LOCAL co-op the hero of whoever owns the open menus (refii_mod_menu_player): player two's
/// while player two owns the pause, else player one's. Both players are local there; name a
/// player (refii_mod_player_hero) when the answer must not follow the menus.
extern "C" uint32_t refii_mod_local_hero(uint8_t* base);

/// Player `player`'s hero entity (1 = the hero, 2 = the co-op partner), or 0. Both players are
/// principals: a mod that changes how the hero plays applies it to either.
extern "C" uint32_t refii_mod_player_hero(uint8_t* base, int32_t player);

/// Component `id` of `entity`, or 0 — the guest's own lookup (presence bits at +0x24, the sorted
/// {id, component} pairs at +0x48..+0x4C, the index table at +0x8C). Ids: 33 the player
/// controller, 36 health, 57 stats, 164 experience.
extern "C" uint32_t refii_mod_component(uint8_t* base, uint32_t entity, uint32_t id);

/// `entity`'s world position, through its placement component's own getter (the object at
/// entity+0x7C, vtable slot 16) — the call the game itself makes for a cast's origin. True when
/// read; `out_xyz` takes three floats. Guest tick thread only (a tick callback or a hook).
extern "C" int32_t refii_mod_entity_position(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out_xyz);

/// Runs a Lua source chunk of any length in the GAME VM — the shipped game scripts' and
/// mods' Lua. True when it ran without error. Guest tick thread only (a tick callback or a hook).
extern "C" int32_t refii_mod_run_game_lua(PPCContext* ctx, uint8_t* base, const char* chunk);

/// Runs a Lua chunk in the mods' FRONT-END Lua (the title and the front end, where there is no game
/// VM): the current Gameface presentation's own state, the mods' mod/frontend.lua run in it first.
/// 0 when no presentation has a script engine. Guest tick thread only.
extern "C" int32_t refii_mod_run_frontend_lua(PPCContext* ctx, uint8_t* base, const char* chunk);

/// The integer value of the Lua expression `expr` in [lo, hi], by a binary search of assertions —
/// a handful of chunk runs, so read it when it can have changed (see refii_mod_lua_generation),
/// not every tick. False when the expression is not a number in that range.
extern "C" int32_t refii_mod_game_lua_int(PPCContext* ctx, uint8_t* base, const char* expr,
                                          int32_t lo, int32_t hi, int32_t* out);

/// Changes every time the game's Lua state is rebuilt (a new session's startup scripts) or a
/// save's Lua state is loaded into it. A mod keeping state in the save re-reads it on a change.
extern "C" uint32_t refii_mod_lua_generation(void);

/// Registers `fn` (a refii_mod_tick_fn) when the game module loads:
///   static void Tick(PPCContext* ctx, uint8_t* base, float seconds) { ... }
///   REFII_MOD_ON_TICK(Tick);
#define REFII_MOD_ON_TICK(fn) \
  static const int refii_mod_on_tick_##fn = (refii_mod_register_tick(&fn), 0)

// The generated precompiled header defines the real prologue (the profiler zone, the alignment
// assumption); this stands in only where it is absent, as in vfs_hooks_private.h.
#ifndef REX_FUNC_PROLOGUE
#define REX_FUNC_PROLOGUE() ((void)0)
#endif

namespace refii::mods {

inline uint8_t LoadU8(const uint8_t* base, uint32_t address) { return base[address]; }

inline uint16_t LoadU16(const uint8_t* base, uint32_t address) {
  uint16_t be;
  std::memcpy(&be, base + address, sizeof(be));
  return __builtin_bswap16(be);
}

inline uint32_t LoadU32(const uint8_t* base, uint32_t address) {
  uint32_t be;
  std::memcpy(&be, base + address, sizeof(be));
  return __builtin_bswap32(be);
}

inline float LoadF32(const uint8_t* base, uint32_t address) {
  const uint32_t bits = LoadU32(base, address);
  float value;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

inline void StoreU8(uint8_t* base, uint32_t address, uint8_t value) { base[address] = value; }

inline void StoreU32(uint8_t* base, uint32_t address, uint32_t value) {
  const uint32_t be = __builtin_bswap32(value);
  std::memcpy(base + address, &be, sizeof(be));
}

inline void StoreF32(uint8_t* base, uint32_t address, float value) {
  uint32_t bits;
  std::memcpy(&bits, &value, sizeof(bits));
  StoreU32(base, address, bits);
}

/// Whether `entity` is either player's hero.
inline bool IsPlayerHero(uint8_t* base, uint32_t entity) {
  return entity != 0 &&
         (entity == refii_mod_player_hero(base, 1) || entity == refii_mod_player_hero(base, 2));
}

/// Calls the guest function at `address` (a value read from guest memory, e.g. a vtable slot)
/// with `ctx` as the caller set it up. False when the address is not a known function.
inline bool CallGuest(PPCContext& ctx, uint8_t* base, uint32_t address) {
  using GuestFn = void (*)(PPCContext&, uint8_t*);
  if (address == 0 || refii_bridge_rex_has_function(address) == 0) {
    return false;
  }
  auto fn = reinterpret_cast<GuestFn>(refii_bridge_rex_resolve_indirect_function(address));
  if (fn == nullptr) {
    return false;
  }
  fn(ctx, base);
  return true;
}

/// Calls virtual slot `slot` of the guest object `object` (r3 = object; set r4.. first).
inline bool CallVirtual(PPCContext& ctx, uint8_t* base, uint32_t object, uint32_t slot) {
  const uint32_t vtable = LoadU32(base, object);
  if (vtable == 0) {
    return false;
  }
  ctx.r3.u32 = object;
  return CallGuest(ctx, base, LoadU32(base, vtable + slot * 4u));
}

/// A copy of `ctx` with the stack pointer moved below the caller's frame: what a guest call
/// that takes stack arguments or scratch is given.
inline PPCContext Frame(const PPCContext& ctx, uint32_t bytes = 0x200u) {
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - bytes) & ~0xFu;
  return work;
}

/// Three floats from guest vector register v1 or v2 (`which` 1 or 2) — a position or a
/// direction as the game passes them.
inline void GetVector(const PPCContext& ctx, int which, float* xyz) {
  const uint8_t* lanes = which == 1 ? ctx.v1.u8 : ctx.v2.u8;
  for (int i = 0; i < 3; ++i) {
    const uint32_t bits = (static_cast<uint32_t>(lanes[15 - 4 * i]) << 24) |
                          (static_cast<uint32_t>(lanes[14 - 4 * i]) << 16) |
                          (static_cast<uint32_t>(lanes[13 - 4 * i]) << 8) |
                          static_cast<uint32_t>(lanes[12 - 4 * i]);
    std::memcpy(&xyz[i], &bits, sizeof(bits));
  }
}

/// Puts three floats into guest vector register v1 or v2. The recompiler keeps a vector
/// byte-reversed against its memory image, so the big-endian image is written backwards.
inline void SetVector(PPCContext& ctx, int which, const float* xyz) {
  uint8_t image[16] = {0};
  for (int i = 0; i < 3; ++i) {
    uint32_t bits;
    std::memcpy(&bits, &xyz[i], sizeof(bits));
    image[4 * i] = static_cast<uint8_t>(bits >> 24);
    image[4 * i + 1] = static_cast<uint8_t>(bits >> 16);
    image[4 * i + 2] = static_cast<uint8_t>(bits >> 8);
    image[4 * i + 3] = static_cast<uint8_t>(bits);
  }
  uint8_t* lanes = which == 1 ? ctx.v1.u8 : ctx.v2.u8;
  for (int i = 0; i < 16; ++i) {
    lanes[15 - i] = image[i];
  }
}

}  // namespace refii::mods

// ---------------------------------------------------------------------------------------------
// Entities, the heroes' stats, spells and the save: the helpers behind the events.
// ---------------------------------------------------------------------------------------------

/// EVERY API SERVES BOTH PLAYERS; NEVER ASSUME PLAYER ONE. The calls below answer "which hero"
/// questions the way the core co-op layer answers them, so a mod and the base game's own co-op
/// fixes ask the same question the same way.

/// 1 or 2 when `entity` is that player's hero, 0 otherwise. Both players are principals.
extern "C" int32_t refii_mod_player_of(uint8_t* base, uint32_t entity);

/// The player `entity` deals with, 1 or 2 — who "the hero" is for it: a hero is its own player; a
/// player's dog is its master's (the game's own dog for player one, the co-op dog bound to player
/// two — Refii.player_dog's answers); anything else answers to the nearer living hero, keeping its last answer until the
/// other hero is more than 3 m nearer (so an NPC between the two does not flicker). One hero alone
/// is everyone's. 0 only while neither hero exists. Lua's Refii.player_for is the same rule. Guest
/// tick thread only (a tick callback or a hook).
extern "C" int32_t refii_mod_player_for(PPCContext* ctx, uint8_t* base, uint32_t entity);

/// Whether this machine runs the shared world — the AI, every hit's outcome, the NPCs: alone, in
/// local co-op and on an online host; false on an online joiner, whose world is the host's copy.
/// An effect on the world (casting a spell on a victim, spawning, changing an NPC) is made where
/// this holds; an effect on a hero is made where refii_mod_player_here holds for that hero.
extern "C" int32_t refii_mod_world_here(uint8_t* base);

/// The player whose menus are open on this machine, 1 or 2: 2 on an online joiner's machine, and
/// in local co-op while player two owns the pause; 1 otherwise. The book's pages read and act
/// for this player, so a mod's ability row publishes and buys for them.
extern "C" int32_t refii_mod_menu_player(uint8_t* base);

/// How `villager` feels about player `player`'s hero, its five current opinion axes into `out`:
/// love, fear, attractiveness, morality, respect (-1..1). Each villager keeps a set per hero:
/// player two's deeds move how it sees player two (and only that). False when `villager` holds
/// no opinions. Inside an OpinionDeed handler for a deed of player two's, the component itself
/// holds player two's set for the call.
extern "C" int32_t refii_mod_opinion(uint8_t* base, uint32_t villager, int32_t player, float* out);

/// A VILLAGER'S CRIMES AND RENOWN FOR ONE HERO (`hero`, either player's). The shipped
/// Villager.ForgiveHerosCrimes / HasHeroCommittedCrimeIHaveDiscovered / GetPerceivedRenownOfHero
/// name no hero (they forgive everyone's crimes, ask about hero one, read hero one's renown).
/// forgive_crimes: the crimes of `hero` the villager knows of (the game's own lists and order) are
/// forgiven — the village's records erased — and the other hero's stay; returns how many.
/// discovered_crime: 1 when the villager has discovered a crime `hero` committed. perceived_renown:
/// the renown the villager credits `hero` with (their own plus the village's regional fame), into
/// `out`; false when `villager` is no villager. Guest tick thread only.
extern "C" int32_t refii_mod_forgive_crimes(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero);
extern "C" int32_t refii_mod_discovered_crime(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero);
extern "C" int32_t refii_mod_perceived_renown(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero,
                                              int32_t* out);

/// How this game is shared: 0 alone, 1 local co-op (split or shared screen), 2 online and this
/// machine hosts, 3 online and this machine joined. A partner counts once their hero exists.
extern "C" int32_t refii_mod_coop_mode(uint8_t* base);
/// 1 while code on this thread runs in an opinion scope for hero two (mod_events.cpp).
extern "C" int32_t refii_mod_opinion_scope_two(void);

/// The living entity player `player`'s hero is focused on (their targeting's target), or 0. The
/// targeting (component 77) is driven where that hero is PLAYED, so the answer is only meaningful
/// where refii_mod_player_here(player) holds: on an online joiner focus(1) reads the host hero's
/// copy, not the host player's choice.
extern "C" uint32_t refii_mod_focus(uint8_t* base, int32_t player);

// ---------------------------------------------------------------------------------------------
// PLAYER STATE: a mod's values for one player, right on both co-op machines.
// ---------------------------------------------------------------------------------------------
//
// Keep anything that belongs to a player here rather than in a mod's own array: a pool, a level,
// a cooldown. `player` is 1 or 2, `key` "<mod>.<name>". The values are THE SAME ON BOTH MACHINES
// of an online session: each player's are owned by the machine that plays that hero, and a write
// on either machine reaches the owner (an add merges, so a spend on the host and a refill on the
// joiner both count). SAVED values go home with the hero: they are written beside the save the
// game writes that hero into — player one's main save, a local player two's own save, an online
// joiner's own save on the joiner's machine — and come back when the game loads the hero from it.
// So a mod written as if for one player is right for both, in every co-op mode.

/// The value, or `fallback` when the player has none under `key`.
extern "C" float refii_mod_player_value(int32_t player, const char* key, float fallback);
/// Whether the player has a value under `key` (written to `out` when not null).
extern "C" int32_t refii_mod_player_get(int32_t player, const char* key, float* out);
/// Sets a session value (kept while the hero is in play, never saved).
extern "C" void refii_mod_player_set(int32_t player, const char* key, float value);
/// Adds to a value (a missing one counts as 0). `saved` keeps the key with the hero's save.
extern "C" void refii_mod_player_add(int32_t player, const char* key, float delta, int32_t saved);
/// Sets a SAVED value: kept with the hero's own save and back when it loads.
extern "C" void refii_mod_player_save(int32_t player, const char* key, float value);
/// Whether this machine plays that player's hero (both outside an online session; the host plays
/// player one and the joiner player two inside one). Work that runs on its own every tick for a
/// player — a refill, a timer — runs where the hero is played, so it runs once per session.
extern "C" int32_t refii_mod_player_here(int32_t player);
/// Whether the event being handled is the game's COPY of something the other co-op machine
/// decides — a cloned action's animation event, a replicated cast's charge or launch, a hit
/// response predicted for a body played elsewhere, a received experience change. The game runs
/// those on both machines, so a handler runs on both: its verdict (cancel, a written field) applies
/// on both, from the same player values, but PLAYER-STATE WRITES ARE DROPPED during the copy — the
/// deciding machine makes them, so a spend counts once. A handler with an effect on the body itself
/// (holding the hero in place) skips it during the copy; nothing else needs this.
///
/// RELAYED events are copies too. An event the game raises on one machine only, but whose effect
/// belongs on the other — a swing, a roll or an item use where that hero is played (the host's AI
/// must see player two's), a hit landing on the host (the joiner's looks follow its victims) — is
/// sent by refii over the co-op channel and dispatched on the other machine as a replica, its
/// actor that machine's copy of the hero (or the entity of the same UniqueID). Its objects of the
/// deciding machine's own (a hit record, an ability, an action, an item) are 0, cancel and writes
/// do nothing (it already happened), and it arrives a network moment later. The catalog marks
/// each relayed event; a handler that makes a world or body effect skips the copy.
extern "C" int32_t refii_mod_event_replica(void);

/// Whether `entity` is in the world: the flag the game's own target selector tests
/// ((entity+0x90) & 0x40). False for 0.
extern "C" int32_t refii_mod_entity_alive(uint8_t* base, uint32_t entity);

/// The world object (the game's W), or 0 before a session.
extern "C" uint32_t refii_mod_world(uint8_t* base);

/// `entity`'s current and maximum health, from its health component (36: +0x14, +0x1C). True
/// when the component exists.
extern "C" int32_t refii_mod_health(uint8_t* base, uint32_t entity, float* out_current, float* out_max);

/// Writes `entity`'s current health, clamped to [0, max]. A direct write of the field the game
/// reads; the game's own health-changed event is not raised. Returns true when written.
extern "C" int32_t refii_mod_set_health(uint8_t* base, uint32_t entity, float value);

/// The level of ability `type` (1..14 — 1 Heavy Weapons, 2 Physique, 3 Toughness, 4 Ranged Weapons,
/// 5 Accuracy, 6 Speed, 7 Lightning .. 14 Raise Dead, as `sub_824E45F8` names them: the stats
/// component's table at +0x110 + 4*type) on `entity`, or -1 without a stats component (57).
extern "C" int32_t refii_mod_ability_level(uint8_t* base, uint32_t entity, int32_t type);

/// The experience in `pool` (0 General, 1 Strength, 2 Skill, 3 Will) of `entity`'s experience
/// component (164: the pools' float array at +0x10). True when read.
extern "C" int32_t refii_mod_experience(uint8_t* base, uint32_t entity, int32_t pool, float* out);

/// The game's own cost of level `level` of ability `type` for `entity`
/// (rex_CECStats_AbilityExperienceCost), or -1.
extern "C" int32_t refii_mod_ability_cost(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t type, int32_t level);

/// Adds `amount` (negative to take) to `pool` of `entity`'s experience through the game's own
/// Modify, flags zero (outside the lifetime-earned total), raising its XP-changed event and the
/// Experience mod event. True when done.
extern "C" int32_t refii_mod_give_experience(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t pool, float amount);

/// Spends `amount` from `pool` of `entity`'s experience through the game's own Spend: a
/// shortfall comes out of General WITHOUT a check, so test affordability first. True when done.
extern "C" int32_t refii_mod_spend_experience(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t pool, float amount);

/// Casts spell `type` (0 Lightning .. 7 Dead Rising) with `caster` as the caster through the
/// game's own spell factory (rex_CSpellRegistry_Create): targeted at `target` (an entity, or 0
/// for a targeted cast with no target), or an area cast when `targeted` is 0; at power `level`
/// 0..4; starting at `pos` facing `dir` (three floats each; the caster's own position and
/// facing when null). No will cost, no cast state, no animation — the spell object only. Returns
/// the spell object, or 0. Guest tick thread only.
extern "C" uint32_t refii_mod_cast_spell(PPCContext* ctx, uint8_t* base, uint32_t caster, int32_t type, int32_t targeted,
                                         uint32_t target, int32_t level, const float* pos, const float* dir);

/// The cast serial of a spell object (+0x44): the number its hits carry as spell_serial.
extern "C" uint32_t refii_mod_spell_serial(uint8_t* base, uint32_t spell);

/// `caster`'s live, unfinished spell object of `type` from the world's registry, or 0. Never a
/// Vortex: the vortex spell object marks itself finished once its entity exists.
extern "C" uint32_t refii_mod_live_spell(PPCContext* ctx, uint8_t* base, int32_t type, uint32_t caster);

/// Writes `RefiiModSave.<key> = value` in the game VM: an integer that persists with the save
/// game (Engine_ModLua's preamble carries the table through GetSaveTable / LoadScriptSave).
/// `key` is a Lua identifier. True when written. Guest tick thread only.
extern "C" int32_t refii_mod_save_set_int(PPCContext* ctx, uint8_t* base, const char* key, int32_t value);

/// Reads `RefiiModSave.<key>` in [lo, hi] (a binary search of chunk runs — read it on a
/// refii_mod_lua_generation change, not every tick). False when absent or out of range.
extern "C" int32_t refii_mod_save_get_int(PPCContext* ctx, uint8_t* base, const char* key, int32_t lo, int32_t hi,
                                          int32_t* out);

/// The GDB record hash of `entity`'s definition (entity+0x1C): what the entity is.
extern "C" uint32_t refii_mod_entity_definition(uint8_t* base, uint32_t entity);

/// `entity`'s instance name (the string at entity+0x80, the one SetName writes) copied into
/// `out`; false (and an empty string) when it has none.
extern "C" int32_t refii_mod_entity_name(uint8_t* base, uint32_t entity, char* out, uint32_t size);

/// How `entity` stands to `other` by the game's own faction rule (component 133; explicit
/// lists first, then the faction table): 1 neutral, 2 ally, 4 enemy; 0 without a faction
/// component. The game's Lua IsMyEnemy is this == 4. Guest tick thread only.
extern "C" int32_t refii_mod_relation(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t other);

/// Moves `entity` to `xyz` through its placement's own setter (vtable slot 25), which raises the
/// game's moved event. True when done. Guest tick thread only.
extern "C" int32_t refii_mod_set_position(PPCContext* ctx, uint8_t* base, uint32_t entity, const float* xyz);

/// Changes `entity`'s health by `delta` (negative to hurt) through the game's own Modify, the
/// function every heal and damage goes through: clamps, stats, the death at zero with
/// `attacker` (or 0) as the killer, the mod's Damage/EntityDied events. True when done.
extern "C" int32_t refii_mod_modify_health(PPCContext* ctx, uint8_t* base, uint32_t entity, float delta, uint32_t attacker);

/// The current level's name (the world manager's current level, +0xAC) into `out`; false and
/// an empty string when no level is loaded.
extern "C" int32_t refii_mod_level_name(uint8_t* base, char* out, uint32_t size);

/// `entity`'s gold (purse component 81, +12). True when it has a purse.
extern "C" int32_t refii_mod_gold(uint8_t* base, uint32_t entity, int32_t* out);

/// Gives (or takes, negative) gold through the game's own Modify — the HUD's "ModifyMoney"
/// message, the stats, the co-op share, the Gold event. True when done.
extern "C" int32_t refii_mod_give_gold(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t delta);

/// How many of definition `definition` (a GDB record hash) `entity`'s inventory (component 13)
/// holds: the items' counts summed, 0 for none or no inventory.
extern "C" int32_t refii_mod_item_count(uint8_t* base, uint32_t entity, uint32_t definition);

/// Adds one of definition `definition` to `entity`'s inventory through the game's own add (the
/// item's count rises when it is already held; the "item added" notifications run; the
/// ItemAdded event fires). Returns the item object, or 0. Guest tick thread only.
extern "C" uint32_t refii_mod_add_item(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t definition);

/// LUA AND GODOT SEE HANDLES, NOT ADDRESSES. The game's Lua number is a 32-bit float, so a
/// guest address above 2^24 is not exact there; every entity or object the bus hands to Lua or
/// to a Godot script is a HANDLE: a small integer assigned to that address for the session (0 for
/// 0). These convert both ways; a native mod needs them only to talk to those two sides.
extern "C" uint32_t refii_mod_handle(uint32_t address);
extern "C" uint32_t refii_mod_handle_address(uint32_t handle);

/// The game's own 32-bit FNV-1 of a name (basis 0x811C9DC5): a GDB record's hash from its name,
/// a text tag's hash as the TextManager keys it.
extern "C" uint32_t refii_mod_fnv1(const char* text);

/// The hero stats the game's own getters report (stats component 57): renown (base +28 plus the
/// active bonus), morality and purity (base +16 / +36 plus bonuses, clamped by the game). True when
/// the entity has stats. Guest tick thread only.
extern "C" int32_t refii_mod_renown(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t* out);
extern "C" int32_t refii_mod_morality(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out);
extern "C" int32_t refii_mod_purity(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out);

/// Physique from the stats component: fatness (+56) and height (+60). True when read.
extern "C" int32_t refii_mod_physique(uint8_t* base, uint32_t entity, float* out_fat, float* out_height);

/// Age in years from the age component (122, +12). True when read.
extern "C" int32_t refii_mod_age(uint8_t* base, uint32_t entity, float* out);

/// Stack `index` of `entity`'s inventory (component 13: the list head at +16, node +0 next, +8 the
/// item; item +4 definition hash, +12 stack count), in the game's own order. False past the end.
extern "C" int32_t refii_mod_inventory_stack(uint8_t* base, uint32_t entity, int32_t index, uint32_t* out_definition,
                                             int32_t* out_count);

/// The definition hashes in `entity`'s weapon set (component 28: +84 melee, +88 ranged; 0 for none).
/// True when it has a weapon set.
extern "C" int32_t refii_mod_equipped(uint8_t* base, uint32_t entity, uint32_t* out_melee, uint32_t* out_ranged);

/// Entities in the world within `radius` of `xyz` (the world entity manager's list, in-world ones
/// with a placement, measured by their own position getter), nearest first, up to `max` into `out`.
/// `creatures_only` keeps those with a creature component (20). Returns how many were written.
/// Guest tick thread only.
extern "C" int32_t refii_mod_entities_near(PPCContext* ctx, uint8_t* base, const float* xyz, float radius,
                                           int32_t creatures_only, uint32_t* out, int32_t max);

/// Whether the game's physics sees nothing between `from` and `to` (the raycast the village AI's
/// IsLineOfSight makes: filter 20, every hit collected). Guest tick thread only.
extern "C" int32_t refii_mod_line_of_sight(PPCContext* ctx, uint8_t* base, const float* from, const float* to);

/// Creates an entity of definition `definition` at `xyz` through the game's own
/// CreateEntityFromIDAtPosition (a fresh entity id, the components, the activation), refused by the
/// game while it does not allow script creation. Returns the entity, or 0. Guest tick thread only.
extern "C" uint32_t refii_mod_spawn(PPCContext* ctx, uint8_t* base, uint32_t definition, const float* xyz);

// ---------------------------------------------------------------------------------------------
// Effects: a request crosses to the host, which plays it the way the active look draws effects:
// under the modern look Godot spawns the node it draws every bank effect with (FableFx); under the
// classic look the host hands it back to the guest tick (`fx_play`, mod_commands.cpp), which spawns
// the guest's own particle instance by the game's own spawn sequence. A tint applies only to the
// modern look; the guest's instance has no colour of its own.
// ---------------------------------------------------------------------------------------------

/// How a played effect is shown. Null options mean refii_mod_fx_default_options().
typedef struct refii_mod_fx_options {
  /// Seconds before it is stopped; 0 plays until refii_mod_stop_fx (or, with loop 0, until the
  /// effect has run out).
  float duration;
  /// A size multiplier on every position, motion and size of the effect, as a node's scale is.
  float scale;
  /// A colour multiplier on every particle (1, 1, 1 untinted).
  float tint[3];
  /// The instance intensity, an alpha term (1 as authored).
  float intensity;
  /// 1 repeats the effect, 0 plays it once.
  int32_t loop;
  /// From the entity (refii_mod_play_fx_on), in the guest's world axes at the moment it starts;
  /// after that the effect rides the entity rigidly.
  float offset[3];
} refii_mod_fx_options;

inline refii_mod_fx_options refii_mod_fx_default_options(void) {
  refii_mod_fx_options options{};
  options.scale = 1.0f;
  options.tint[0] = options.tint[1] = options.tint[2] = 1.0f;
  options.intensity = 1.0f;
  options.loop = 1;
  return options;
}

/// Plays effect `name` — any effect of the particle bank or of an enabled mod's fx/effects/, by
/// its name or an "FX_XXXXXXXX" hash token — at the world position `xyz` (the guest's axes).
/// Returns the id refii_mod_stop_fx takes (never 0 for a request that was posted; 0 for a null or
/// empty name). Any thread; the effect appears on Godot's next frame. An unknown name is reported
/// by Godot, not here.
extern "C" uint32_t refii_mod_play_fx(const char* name, const float* xyz, const refii_mod_fx_options* options);

/// Plays effect `name` on `entity` (an address; its handle is what crosses): it follows the
/// entity's drawn node, offset by options->offset, and goes with it.
extern "C" uint32_t refii_mod_play_fx_on(const char* name, uint32_t entity, const refii_mod_fx_options* options);

/// Stops a played effect by the id play_fx returned — any surface's id: native, Lua or Godot.
extern "C" void refii_mod_stop_fx(uint32_t id);

// ---------------------------------------------------------------------------------------------
// The UI: a mod's menus, message boxes, notices and menu boxes, drawn by Godot (posted as REFII_MOD_HOST_UI_KIND)
// ---------------------------------------------------------------------------------------------

/// Opens a menu a mod defines in ui/templates.json for `player` (1 or 2; 0 for whoever owns the
/// menus on this machine): over whichever menu list has the input, else — in play or on the title,
/// with no list up — as a list of its own that takes the pad and closes on B. A machine opens it
/// only for a player it plays (the mod's code runs on the other machine too). Returns 1 when the
/// request was posted, 0 for no name. Any thread; Godot acts on it on its next frame.
extern "C" int32_t refii_mod_open_menu(int32_t player, const char* name);

/// Raises a message box for `player` (1 or 2; 0 for whoever owns the menus here) in the game's own
/// box art — in game the query or message box, on the front end its own dialog: `title` above
/// `text` ('\n' breaks a line). With `cancel` non-null it is a question, A and B captioned
/// `accept` and `cancel` (null or "" keeps the box's own caption); without, a notice with one
/// button. Returns the id the ModMessageBox event carries when it is answered (0 for no text). A
/// machine shows it only for a player it plays, and answers -1 elsewhere. Any thread.
extern "C" uint32_t refii_mod_message_box(int32_t player, const char* title, const char* text, const char* accept,
                                          const char* cancel);

/// Posts a notice for `player` (1 or 2; 0 for whoever owns the menus here) on the notice channel the
/// game's own co-op notices use — in game the HUD's info box for `seconds` (0 or less: the
/// channel's own length), on the front end its own dialog until dismissed: `title` above `text`.
/// Notices queue one after another. Returns the id the ModNotice event carries when it has gone
/// (0 for no text). A machine shows it only for a player it plays, and reports -1 elsewhere. Any
/// thread.
extern "C" uint32_t refii_mod_notice(int32_t player, const char* title, const char* text, float seconds);

/// Raises a menu box for `player` (1 or 2; 0 for whoever owns the menus here) in the game's own
/// menu box art, on the front end and in game: `title` above `text`, then `count` options (at most
/// 16; more than the box's six rows are paged). B closes it when `cancelable` is non-zero. Returns
/// the id the ModMenuBox event carries with the chosen option's index (-1 for none) — 0 for no
/// option. One menu box stands at a time; a machine shows it only for a player it plays. Any thread.
extern "C" uint32_t refii_mod_menu_box(int32_t player, const char* title, const char* text, const char* const* options,
                                       int32_t count, int32_t cancelable);

// ---------------------------------------------------------------------------------------------
// Menu rows and host screens, drawn by Godot (posted as REFII_MOD_HOST_MENU_KIND)
// ---------------------------------------------------------------------------------------------

/// States the rows of a menu list for `player` (1 or 2; 0 for every player), replacing what was
/// stated before: what a ui/templates.json menu or entry with "source": "rows" (or
/// "rows:<list>") shows, at once if it is open. `rows` is one text: rows split by ';', each
/// "name|label|command|ghosted" ('\' escapes '|', ';' and itself; label a Babel tag or literal,
/// the name when empty; command any row verb — Mod:lua:, Opens:, Screen:open:; ghosted 1 or 0).
/// An empty text clears the list. About 1.8 KB of rows cross at once. Returns 1 when posted.
extern "C" int32_t refii_mod_set_menu_rows(int32_t player, const char* menu, const char* rows);

/// Opens a host-owned screen by id for `player` (1 or 2; 0 for whoever owns the menus here): the
/// Screen:open:<id> verb, as a row on the list with the input would — the game's (achievements
/// 1013, multiplayer 1014) or a ui/templates.json screens record a mod states (id 1000 and up,
/// with a menu and a presentation). A template book stands over the pause book, so it opens while
/// the pause list has the input. Returns 1 when posted. Any thread.
extern "C" int32_t refii_mod_open_screen(int32_t player, uint32_t screen);

// The events — one include gives a mod the whole surface.
#include "core/refii_mod_events.h"
