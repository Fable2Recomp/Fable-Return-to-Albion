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

/// States that `hero`'s target lock switches target by the MOUSE WHEEL, for this game tick: call it
/// every tick the lock holds (a statement lapses two ticks after it was made). While it holds and
/// the hero is locked (player mode 4) under the third-person camera the keyboard and mouse drive,
/// the mouse's movement never switches target, and each wheel notch is one switch through the
/// camera's own `Targeting.CheckForBetterTargetInDirection` — down the next target right, up the
/// next target left — waiting on the camera's ClearToRotate as a stick flick does (at most one
/// notch waits). Without it a mouse flick switches. Any hero: it acts where the mouse drives that
/// hero's camera (src/vfs/src/hooks/mouse_look.cpp); a pad is untouched.
extern "C" void refii_mod_wheel_switches_target(uint8_t* base, uint32_t hero);

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

/// The most bytes a partner message's `payload` carries; longer is refused.
#define REFII_MOD_PARTNER_PAYLOAD_MAX 256
/// The most characters a partner message's `name` (and the `mod` naming it) carries.
#define REFII_MOD_PARTNER_NAME_MAX 32

/// Sends a mod's own message to THE OTHER MACHINE of an online session: `name` (a word of
/// letters, digits, '_', '.' and '-', at most REFII_MOD_PARTNER_NAME_MAX) and `payload` (any text,
/// at most REFII_MOD_PARTNER_PAYLOAD_MAX bytes; empty is fine) from `mod` (the sending mod's name,
/// REFII_MOD_NAME). It arrives there as the PartnerMessage event (core/refii_mod_events.h) on the
/// guest tick, with `from` the player this machine plays — reliable, in the order sent, once: it
/// rides the ROUTE (core/event_relay.h), the ordered, acknowledged lane the game's own cross-machine
/// calls take, and waits out a load or a pause. Either machine may send; the host is player one's
/// and the joiner player two's. Returns 1 when posted; 0 outside an online session (alone and in
/// local co-op nothing is sent), for a bad name or mod, or a payload over the limit. Any thread.
extern "C" int32_t refii_mod_send_to_partner(const char* mod, const char* name, const char* payload);

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

/// Tints `entity` through the game's own appearance setter: the vector tint of its renderable
/// (CECGraphicAppearance, component 3; `sub_82573770`, disc `sub_82548418`), the vector twin of
/// the byte setter `sub_82573570` a death's grey goes through. Lanes are linear multipliers of the
/// lit colour, 1 = untouched (the byte setter states its 0..255 as n/255): {1, 0.3, 0.3} reads red, {0.4,
/// 0.4, 0.4} darker; above 1 brightens (the title's own ghosts state up to 392; the modern look
/// caps a lane at 31.875/4, the scene target's ceiling). `a` is stored and replicated but no shader
/// reads it: opacity is SetAlpha's, never the tint's. The setter stores +80..+95 and states the
/// record the entity feed draws under both looks and co-op replicates; a change under 1e-4 on
/// every lane is ignored by the game itself, so restating a tint each tick costs nothing. Negative
/// lanes are taken as 0; a non-finite one refuses the call. True when the entity has a renderable.
/// Guest tick thread only.
extern "C" int32_t refii_mod_entity_tint(PPCContext* ctx, uint8_t* base, uint32_t entity, float r, float g, float b,
                                         float a);

/// Puts `entity`'s tint back to the constructor's {1, 1, 1, 1} (`sub_82571DF8` 0x82571f04) through
/// the same setter. An entity the GAME tints (a corpse's grey) is the game's to restore: untint only
/// what you tinted. True when the entity has a renderable. Guest tick thread only.
extern "C" int32_t refii_mod_entity_untint(PPCContext* ctx, uint8_t* base, uint32_t entity);

/// THE OTHER APPEARANCE SETTERS of the same renderable (component 3), each storing its value and
/// calling `sub_82573318`, which packs the record the entity feed draws under both looks and co-op
/// replicates — exactly the tint's path. Reset = call with the constructor's value
/// (`sub_82571DF8`: alpha 1, scale 1, saturation mask 0, drawable 1). A GAME-DRIVEN VALUE WINS
/// WHERE THE GAME RESTATES IT: a Blades sword's update (`sub_825A63A8`) restates its scale and
/// alpha every frame, and deaths 5/6/7 ramp alpha to 0 through `sub_82681868` — a value stated on
/// such an entity holds for one frame. Guest tick thread only; true when the entity has a renderable.
///
/// Alpha: `sub_82573220(comp3, f1)` (disc `sub_82547EC8`): stores +112 (`stfs f31,0x70(r31)`
/// 0x825732cc), skips an equal value (`fcmpu/beq` 0x82573244), and crossing 1e-4 either way
/// (flt_82099A80, 0x82573254..60) re-registers the drawable (`sub_825728E8`) and flips bits
/// 0x10/0x80 at +0x90 — so 0 hides the body and anything above 1e-4 shows it again. Takes 0..1;
/// below 0 is 0, above 1 is 1.
extern "C" int32_t refii_mod_entity_alpha(PPCContext* ctx, uint8_t* base, uint32_t entity, float alpha);

/// Scale: `sub_82573078(comp3, f1)` (disc `sub_82547D20`): stores +108 (`stfs f1,0x6C(r31)`
/// 0x825730c0) unless within 0.003 of the old value (flt_82099054, 0x82573094..a8), then tells the
/// entity's placement (vtable slot 43, 0x825730f8..108: the bounds follow). Positive, finite; the
/// renderable is scaled about its origin. Refused without a placement.
extern "C" int32_t refii_mod_entity_scale(PPCContext* ctx, uint8_t* base, uint32_t entity, float scale);

/// Saturation mask: `sub_822B8188(comp3, r4)` (disc `sub_822B7EA8`), byte +196 (`stb r4,0xC4(r31)`
/// 0x822b81b0), the flag the title's shaders take as "draw desaturated" (Engine_EntityAppearance
/// carries it as kAppearanceSaturation). `on` non-zero sets it; 0 is the constructor's.
extern "C" int32_t refii_mod_entity_saturation_mask(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t on);

/// Drawable: `sub_825725A8(comp3, r4)` (disc `sub_82547250`), byte +197 (`stb r4,0xC5(r31)`
/// 0x825725cc): 0 takes the body out of the drawn set (bit 0x80 at +0x90, `sub_825728E8`
/// 0x825725dc), 1 (the constructor's) puts it back; the entity itself keeps simulating.
extern "C" int32_t refii_mod_entity_drawable(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t on);

// ---------------------------------------------------------------------------------------------
// The target lock: what the game's own LT does, as calls. Both players are principals.
// ---------------------------------------------------------------------------------------------

/// Locks `player`'s (1 or 2) hero on `entity`, or with 0 on the game's own pick. Three of the
/// game's own switches, in the order the game flips them:
///   - `entity` given: the targeting component's (77) SetTarget, vtable slot 19 (`sub_82845120`,
///     the TargetChanged event's seam), which refuses an entity not in the world (+0x90 & 0x40,
///     0x828451b0) and marks it targeted-by-player (`sub_82427FD0` 0x828452f4).
///   - the FORCE byte, comp77 +0x14 — `Targeting.SetForceTargetLock`, `sub_8290F078` = `stb
///     r4,0x14(r3); blr` (disc `sub_82455F10`): while it is set and a target exists the component's
///     8-tick update (`sub_82181EA0`) adds player mode 4 TARGET_LOCK (0x821821dc..0x82182274),
///     the mode that outlines the target and makes the camera's look flicks switch target; with
///     no target it re-selects one each pass (slot 35, 0x82182070).
///   - the look-at flag, through the player component's (33) own writer
///     `rex_CPlayer_SetLookAtButtonDown(comp33, 1)` (`stb r29,0x20(r30)`), which every lock camera
///     script reads and which re-picks the camera mode.
/// A held LT states the same three. True when the hero has both components. Guest tick thread only.
extern "C" int32_t refii_mod_lock_target(PPCContext* ctx, uint8_t* base, int32_t player, uint32_t entity);

/// Ends the lock refii_mod_lock_target stated: the force byte cleared and the look-at flag
/// written 0. The targeting update then drops player mode 4 on its next pass (`+112 && !+20` ->
/// `sub_82389CD0(handle, 4)`, 0x82182290..0x821822a4, within 8 ticks). It cannot end a lock the
/// player is holding LT for — that is the trigger's. True when the hero has both components.
extern "C" int32_t refii_mod_release_lock(PPCContext* ctx, uint8_t* base, int32_t player);

/// Switches `player`'s locked target to the next one left (`left` non-zero) or right: the
/// targeting component's slot 49 `sub_82844DF0` (what the camera's `CheckForBetterTargetInDirection`
/// calls), which seeds the direction from the current target (0x82844e44..0x82844e9c), re-selects
/// (slot 35), and keeps the old target when nothing better is found (0x82844f34..0x82844f74). The
/// sign is the stick's: 0x82844eac..0x82844ebc turn `left` into -1 / +1, and the scorer favours
/// the right for +1 (see src/vfs/src/hooks/mouse_look.cpp). Returns the target held afterwards,
/// 0 when none. Without a current target the call picks nothing (the direction needs one).
extern "C" uint32_t refii_mod_switch_target(PPCContext* ctx, uint8_t* base, int32_t player, int32_t left);

/// The entity `player`'s hero is locked on — the targeting component's own GetTarget (slot 17)
/// while the lock holds: player mode 4 (`sub_821E8B90(*(comp33+0x14), 4)`, disc `sub_821E8870`,
/// the HasPlayerMode every script asks) or the force byte set. 0 otherwise, so a target merely
/// in the reticle is not reported. Guest tick thread only.
extern "C" uint32_t refii_mod_locked_target(PPCContext* ctx, uint8_t* base, int32_t player);

// ---------------------------------------------------------------------------------------------
// Status effects: the spells' own per-victim installers, without the spell.
// ---------------------------------------------------------------------------------------------

/// Status kinds, the StatusEffect event's numbering: 1 electrified (component 205 CECElectrified),
/// 2 chaos (206 CECInChaos), 3 burning (208 CECOnFire), 4 in a vortex (209 CECInVortex).
#define REFII_STATUS_ELECTRIFIED 1
#define REFII_STATUS_CHAOS 2
#define REFII_STATUS_BURNING 3
#define REFII_STATUS_VORTEX 4

/// Puts status `kind` on `entity` for `seconds` at `level` (0..4, the spell level the component
/// records), as the spell's own hit would, through the installer each spell calls — each adds the
/// component itself (`sub_8238CD68(entity, "CEC…", 0)`), each refuses an entity not in the world
/// (+0x90 & 0x40), and the StatusEffect event fires from the add:
///   - electrified: `rex_SpellLightning_Electrify` `sub_82742FA8` (disc `sub_827180A8`): r3 source,
///     r4 victim, r5 serial, r6 level; f1 duration -> +40, f2 total damage -> +52 as damage/second
///     (0 here: a status, not a hit), f3 first-tick delay -> +60, f4 tick interval -> +64, f5 -> +68
///     (`sub_8294CE50` 0x8294ce98..0x8294cef8); gated by the world's spell-count table
///     (`sub_8260D988(*(W+0x38), 2)` 0x82743040) like the spell.
///   - chaos: `rex_SpellChaos_ApplyToVictim` `sub_8274A230` (disc `sub_8271F330`): r3 source, r4
///     victim, r5 serial, r7 is-creature, r8 0 (not a replica: co-op sends it, 0x8274a494..);
///     f1 seconds; v1 the victim's position. Its FX and a zero-damage chaos hit (flags 0x10000,
///     0x8274a448) take their direction from the SOURCE's body, so `source` must be an entity:
///     0 makes the victim its own source.
///   - burning: `rex_SpellFireball_Ignite` `sub_82747260` (disc `sub_8271C360`): r3 victim, r4
///     source, r5 serial, r6 level; f1 strength (1), f2 falloff (0: no distance loss), f3 burn
///     time = seconds, f4 burn damage (0), f5 heat (1); v1 the victim's position; and three bytes
///     the fireball's impact leaves in ITS caller's frame, read as 0x120+arg_6F/74/7F
///     (0x827473d8..ec): +0x6F 1 and +0x7F 1 as `rex_TargetedFireball_Impact` stores them (`li
///     r24,1` 0x82744254 -> var_F1/var_101), +0x74 0 (no level threshold, 0x82747654). +0x7F is
///     what makes a creature flail (+102/+92/+84, 0x82747734..4c) and what co-op replicates.
///   - vortex: refused (0). A vortex holds a creature by a weak reference its component inserts
///     from its own update; there is no installer to call.
/// `seconds` positive and finite, else refused. The serial every one records is -1, a hit's
/// "no cast". Returns 1 when the installer ran. Guest tick thread only.
extern "C" int32_t refii_mod_apply_status(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t kind,
                                          float seconds, int32_t level, uint32_t source);

/// Ends status `kind` on `entity` through the game's own remover — the one its expiry and a
/// death call, which stops the component's FX and removes it (the StatusEffect ended event
/// fires): electrified `sub_827427B8` (disc `sub_827178B8`: `sub_8294ECC8`, +12 = 0, RemoveComponent
/// 205), chaos `sub_82749F38` (disc `sub_8271F038`: `sub_828F7CB8/AE0`, RemoveComponent 206),
/// burning `sub_827469A8` (disc `sub_8271BAA8`: `sub_825FBF38`, RemoveComponent 208). Vortex is
/// refused: the vortex releases what it holds. True when the status was present. Guest tick only.
extern "C" int32_t refii_mod_clear_status(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t kind);

/// Whether `entity` carries status `kind`: its component is present (the presence bits at
/// entity+0x24). Any thread.
extern "C" int32_t refii_mod_has_status(uint8_t* base, uint32_t entity, int32_t kind);

// ---------------------------------------------------------------------------------------------
// Knockback and stagger: a hit of the game's own shape through its one receiver.
// ---------------------------------------------------------------------------------------------

/// Knocks `entity` down and away along `dir` (xyz, the guest's axes; its length times `force` is
/// the vector the hit carries), as a Force Push does: the hit `sub_828B4650` builds (0x828b4954..
/// 0x828b49f4) — knockdown byte +0x18 = 1, unblockable +0x19 = 1, damage +0x38 = 0, flags +0x40 =
/// 0x20000, kind +0x50 = 3, spell type +0x54 = 6, +0x58 = 1, origin +0x70 = the victim less the
/// vector, +0x90 = the vector, +0xA0 = its negation, +0xC0 = "ResponseToKnockdownUp" — built by the
/// hit's own constructor `sub_825C4958` (disc `sub_824831E8`, 212 bytes, the counter at
/// dword_83497088), named through `sub_82275438`, given to `rex_CECHitReceiver_OnHit(comp31, hit,
/// 1)` and destroyed by `sub_823919C0` (disc `sub_8237D608`). The victim's hit-response listener
/// takes the knockdown (`sub_82689268`, 0x8295e7ec) unless the creature cannot be knocked down
/// (comp36 +307, comp31 slot 41: 0x8295e34c..0x8295e384). The hit has no attacker. Hit and
/// HitLanded fire for it; HitResponse does not (a stated knockback is already the mod's
/// decision). Refused when the receiver drops hits (comp31 +0x18/+0x19). True when the hit was
/// delivered. Guest tick thread only. Whether the knockdown's physics scales with the vector's
/// length is unproven: the direction is read, the magnitude may be normalised away.
extern "C" int32_t refii_mod_knockback(PPCContext* ctx, uint8_t* base, uint32_t entity, const float* dir, float force);

/// Staggers `entity`: the same hit with the knockdown byte clear, no flags and the constructor's
/// melee kind, so the listener plays the flinch (`sub_828B6F38`, 0x8295e854) — the GetHit a
/// 0-damage melee tap plays. Blockable like one (+0x19 = 0). Hit and HitLanded fire; HitResponse
/// does not. True when delivered. Guest tick thread only.
extern "C" int32_t refii_mod_stagger(PPCContext* ctx, uint8_t* base, uint32_t entity);

// ---------------------------------------------------------------------------------------------
// Sound: the game's own cue player and its music box.
// ---------------------------------------------------------------------------------------------

/// Plays audio cue `cue` on `entity`, as the shipped scripts' `Sound.PlayEvent(entity, cue, tag)`
/// does: its native `sub_82431A50` builds the cue record from an LHString (`sub_822671A8`, disc
/// `sub_82267168`: flags |= 3, the name copied, +8 = FNV-1 of its upper case `sub_8228A9D8`) and
/// calls `sub_821D9608(out[2], comp135, cue, LHString* tag)` (disc `sub_821D9258`, the same 62
/// instructions): the entity's sound component (135, what PlayEventOnHero `sub_823BA910` looks up
/// at 0x823ba99c) lazily gets an emitter at +44 from `sub_829FD018(sound manager, v1 = the
/// placement's position)` (0x821d9664), the cue's hash must be in the loaded banks
/// (`sub_82287DE0(manager+12, hash)` 0x821d9698, else 0), and `sub_821E0F48` (disc `sub_821E0B08`)
/// starts a voice on the emitter, recorded under `tag` in the emitter's voice list
/// (`sub_821D53B8`). `out[0]` is the voice, released at once (`sub_82C05EF8`) as the native does.
/// An entity without component 135 plays at its position instead (refii_mod_play_sound_at). `tag`
/// (may be "") is what refii_mod_stop_sound matches. Cue names are the audio banks' event names
/// (docs/mod_api.md, Sound); the cue's own volume and attenuation apply — there is no volume
/// argument, the game's player has none. True when a voice started. Guest tick thread only.
extern "C" int32_t refii_mod_play_sound_on(PPCContext* ctx, uint8_t* base, uint32_t entity, const char* cue,
                                           const char* tag);

/// Plays `cue` at a world point: an emitter of our own from the game's `sub_829FD018(manager, v1 =
/// xyz)` (disc `sub_829F8250`), the cue validated as above and started on it by `sub_821E0F48`, under
/// `tag`. The API keeps eight such emitters: the ninth play frees the oldest through the manager's
/// own `sub_829FD168(manager, emitter)` (disc `sub_829F83A0`: the emitter's destroy, the `bctrl`
/// at 0x829fd208, and its list node freed), ending whatever it still played. True when a voice
/// started.
extern "C" int32_t refii_mod_play_sound_at(PPCContext* ctx, uint8_t* base, const float* xyz, const char* cue,
                                           const char* tag);

/// Stops the voices tagged `tag` on `entity`'s emitter (its component 135's +44), or with entity 0
/// on every point emitter refii_mod_play_sound_at made, fading over `fade_ms` milliseconds: the
/// body of `Sound.StopSoundCategoryPlaying(entity, tag, ms)` — `sub_829FDEF0(emitter, LHString*
/// tag, ms)` (disc `sub_829F9128`) walks the emitter's voice list and calls slot 8 `(voice, ms, 0)`
/// on each whose tag equals (`LHString_CBasicString_char_operator` 0x829fdf98, `lwz r10,0x20(r11)`
/// and the `bctrl` at 0x829fdfd4). True when an emitter was walked.
extern "C" int32_t refii_mod_stop_sound(PPCContext* ctx, uint8_t* base, uint32_t entity, const char* tag,
                                        int32_t fade_ms);

/// Starts music cue `name` through the game's own `SoundTools.PlayMusic` body, `sub_8255BB28`
/// (disc `sub_825204B0`, the same 61 instructions; r3 = an LHString slot): refused (0) unless
/// the name is a known cue (`sub_82C02B40` 0x8255bb64), noted in the music box (game+140,
/// `sub_82349A60`, 0x8255bbe8) when it is listening, and played in the manager's category 3
/// (`sub_829FCCA0(manager, 3, name)`). What `MUSIC_*` cues the quest scripts start. True when
/// the manager took it. Guest tick thread only.
extern "C" int32_t refii_mod_play_music(PPCContext* ctx, uint8_t* base, const char* name);

/// Stops music cue `name`: `SoundTools.StopMusic`'s body `sub_8255BC20` (disc `sub_825205A8`,
/// 20 instructions): the music box told (`sub_82336020`) and category 3's stop with the game's
/// 2000 ms fade (`sub_829FCD50`, 0x829fcdb4). Guest tick thread only.
extern "C" int32_t refii_mod_stop_music(PPCContext* ctx, uint8_t* base, const char* name);

// ---------------------------------------------------------------------------------------------
// Animation: a named animation as a scripted action, the entry the shipped AI uses.
// ---------------------------------------------------------------------------------------------

/// Plays animation `name` on `entity` the way `BehaviourBase:PlayAnimation` (ai/aibase.lua) does:
/// `Action.SetCurrentAction(entity, { Type = EScriptableAction.PLAY_ANIMATION (0), Anim = name,
/// OverrideLooking = true, SpeedMultiplier = speed, BlendInTime = blend_seconds })`, run in the
/// game VM — the native (`sub_82960FD8`'s binding, `sub_822BF530` → the action component's slot 33
/// with a Lua table reference) builds the action from the table: the base parser `sub_8229DC48`
/// reads Target, Priority, BlendInTime (0.1), BlendOutTime, OverrideLooking, IsComboBreak,
/// IsPrelude, KillFXOnFinish, CanBeOverridenBySamePriority; the animation parser Anim and
/// SpeedMultiplier. `loops` above 1 wraps it as `PlayLoopedAnimation` does: `{ Type = BATCH (16),
/// LoopAction = <it>, NumLoops = loops, KillFXOnFinish = true }` (`sub_829CEDA0` reads NumLoops,
/// IntoAction, LoopAction, OutOfAction; the two we leave out are unproven to be optional). The
/// evaluator then plays it and the pose feed shows it. `name` is a GDB animation record name
/// (letters, digits, '_'; docs/mod_api.md, Animation). True when the chunk ran — not whether the
/// action was accepted (a higher-priority action keeps the body). Guest tick thread only.
extern "C" int32_t refii_mod_play_animation(PPCContext* ctx, uint8_t* base, uint32_t entity, const char* name,
                                            int32_t loops, float blend_seconds, float speed);

/// Ends `entity`'s scripted actions: `Action.FinishScriptedActions(entity)` (`sub_829647F0`), what
/// the shipped behaviours call to cut a played animation. True when the chunk ran.
extern "C" int32_t refii_mod_stop_animation(PPCContext* ctx, uint8_t* base, uint32_t entity);

// ---------------------------------------------------------------------------------------------
// Opinion and deeds: the game's own setters, per hero.
// ---------------------------------------------------------------------------------------------

/// Moves `villager`'s opinion of `hero` on `axis` (0 love, 1 fear, 2 attractiveness, 3 morality,
/// 4 respect — EOpinionAxes) by `delta`, through the game's own `OpinionReaction.ModifyAxisValue`
/// (`sub_827915D8`, named rex_CECOpinionReaction_ModifyAxisValue on both builds): `+24 + 4*axis
/// += f1` then clamped to [-1, 1] (`fadds f13,f0,f1` 0x827915fc, the two `fsel` 0x8279162c/4c).
/// For player two's hero the write lands in THEIR block (the opinion scope the co-op core keeps,
/// refii_mod_opinion_enter); the opinion_modified event fires with that player. True when
/// written. Guest tick thread only.
extern "C" int32_t refii_mod_modify_opinion(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero,
                                            int32_t axis, float delta);

/// Tells `villager` that `hero` did deed `deed` (a GDB Deed record's hash, e.g. Refii.hash of
/// "Generic_Scary"): the message `Debug.PostGenericDeed` (ai/opinionsdebug.lua) posts, through the
/// game's own poster `sub_82279B80(messages, 50 = MESSAGE_EVENT_DEED_PERSON, &pos, from = hero,
/// to = villager, lifespan 101, extra_data = the record's id — its first word, what
/// RecordPtr:GetID `sub_827E60A0` answers)`, pos the hero's position. The villager's opinion
/// component takes it in OnDeed (`sub_82786F58`: +0x20 the deed, +0x34 the kind, +0x40 the doer)
/// — its strength, its kind and whether this villager cares are the Deed record's and the game's
/// rules; the opinion_deed event fires, routed to the doer's player. Whether `to` narrows delivery
/// to this villager alone is unproven (the shipped debug post names no target). Refused when the
/// hash names no record. True when posted. Guest tick thread only.
extern "C" int32_t refii_mod_add_deed(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero, uint32_t deed);

// ---------------------------------------------------------------------------------------------
// NPC behaviour: the entries the shipped quests and AI scripts order a creature with, run in the
// game VM against Refii.entity(handle). Every one is the game's own Lua-side or native entry, so
// the AI mind sees the order as a quest's; a behaviour the mind runs later (combat, a cutscene)
// may retake the body, which the call cannot see. True when the chunk ran. Guest tick thread only.
// ---------------------------------------------------------------------------------------------

/// `entity` follows `target` at `radius` metres as a quest's `Follow.FollowEntity(e, t, 2.0)`
/// orders it (qr_assassination.lua): the follow component's native `sub_825E33D0` (disc
/// `sub_82456568`, 147 instructions both) — the target's weak reference into +28
/// (`sub_82214898` / `sub_824D1EC8`), +24 = radius (`stfs` 0x825e3484), +17 = 1 "for script",
/// +12 = 1 following, then `sub_825E42C8`; BehaviourFollow then walks it with
/// `MoveToEntityNoWaitForFollowing`. `radius` <= 0 is the quests' 2.
extern "C" int32_t refii_mod_npc_follow(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t target, float radius);

/// `entity` walks to `xyz` as a quest's `ScriptFunction.StartScriptControlledMovement(e, pos,
/// false)` orders it (quests/miscfunctions.lua main.25): `AIManager:SetScriptMoveToPosAndDir`
/// (so BehaviourScriptMoveToPos owns the walk) and `Navigation.MoveToPosition(e, { position,
/// radius = Debug.MOVE_TO_POS_BODGE_DIST, speed })` (`sub_821B9DF0`, disc `sub_821B9ED0`: the
/// table's position / radius / speed / check_destination keys). Not waited for.
extern "C" int32_t refii_mod_npc_go_to(PPCContext* ctx, uint8_t* base, uint32_t entity, const float* xyz);

/// `entity` runs from `from` as `BehaviourBase:FleeFromEntityNoWait` (ai/aibase.lua main.41)
/// does: `Navigation.FleeFromEntity(e, from, ENavigationSpeed.NAV_SPEED_SPRINT, true)` — the
/// navigation component's `sub_82663FF8` (disc `sub_82674C08`, 67 instructions both): mode +168
/// = 3, kind +172 = 2 flee, the target's weak reference and +640 = from (0x826640e0). The AI's
/// own BehaviourFlee repeats this call each tick while it flees; ours is one order.
extern "C" int32_t refii_mod_npc_flee(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t from);

/// `entity` attacks `target` as the quests order it: `CombatRegister:SetScriptTarget(e, t, true,
/// false)` (ai/combatregister.lua main.19) — `Faction.AddTemporaryEntityRelationship(e, t,
/// FACTION_STATUS_ENEMY)` then the combat info's `ScriptTarget` = t, `IgnoreChaseRadius` = true,
/// which the combat behaviours' `FindTargetForCombatant` takes before any other target. Refused
/// by the script (an error, false back) for a dead or non-combatant entity.
extern "C" int32_t refii_mod_npc_attack(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t target);

/// `entity` holds `other` (a hero or any entity) as an enemy (`on`) or no longer: the faction
/// component's own `Faction.AddTemporaryEntityRelationship(e, other, EFactionStatus.FACTION_STATUS_ENEMY)`
/// (`sub_824D2F90`, disc `sub_82540398`, 354 instructions both: status 4 moves `other` into the
/// enemy list at comp+36 and out of the ally/neutral lists; `IsMyEnemy` 0x824d2f30 is
/// `sub_82203F98(..) == 4`, ally 2, neutral 1) or `RemoveTemporaryEntityRelationship` (`sub_824D3518`,
/// disc `sub_82540920`: out of all three). Per hero: player two's hero is its own `other`.
extern "C" int32_t refii_mod_npc_set_hostile(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t other, int32_t on);

/// Ends every order above: `Follow.StopFollowing(e)` (`sub_825E3660`, disc `sub_82456800`),
/// `ScriptFunction.StopScriptControlledMovement(e)` (`AIManager:ClearScriptMoveToPos` and
/// `Navigation.StopMoving` — `sub_821AFFB8`, disc `sub_821B01D0`: modes +168/+172 = 0, the
/// target cleared, +608 = 3), `CombatRegister:ClearScriptTarget(e)` (the enemy relationship removed).
/// The AI mind then picks its next behaviour itself — idle, or whatever its group mind says.
extern "C" int32_t refii_mod_npc_stop(PPCContext* ctx, uint8_t* base, uint32_t entity);

// ---------------------------------------------------------------------------------------------
// Time of day and weather: the game clock's own setter and the environment-theme blend.
// ---------------------------------------------------------------------------------------------

/// Sets the game clock to `hours` (0..24, 13.5 = half past one) through `Timing.SetTimeOfDayAbsolute`'s
/// body `sub_82515AA0` (disc `sub_8249A790`, 41 instructions both; r3 = the time manager, f1 = the
/// day fraction): `stfs f1,4(r3)` then the clamp to [0, 1] (`fsel` 0x82515aec / 0x82515b0c,
/// `stfs f7,4(r3)` 0x82515b10), and in an online session (`*(*(root+12)+0x8C)` bytes +0x34 and
/// +0x35) `sub_82516670` tells the partner. The manager is what the Timing natives are bound to:
/// `*(*(*(*(root+0xC)+0x58)+4)+0x14)` (the binding site 0x82459000..14, disc 0x825ad218..2c).
/// The time_of_day event fires from the clock's next update. True when set.
extern "C" int32_t refii_mod_set_time_of_day(PPCContext* ctx, uint8_t* base, float hours);

/// The game clock in hours (0..24): the manager's day fraction at +4 (what `Timing.GetTimeOfDay`
/// `sub_8288B918` answers) times 24. False, and 0, with no world.
extern "C" int32_t refii_mod_time_of_day(uint8_t* base, float* hours);

/// Blends the GAME's environment theme — its weather and sky look — as the quests do:
/// `EnvironmentTheme.BlendToEnvironmentTheme(theme, weight, seconds)` (qc090_garth.lua:
/// `("EnvThemeWestcliffNight", 1.0, 1.0)` in, `(.., 0.0, 1.0)` out), the native the mod event
/// hooks (`sub_82549B80`, rex_lhEnvironmentThemeManager_BlendToTheme; r3 the theme manager, r4 the
/// name, f1 weight, f2 seconds), so weather_theme fires and a handler may cancel it. `theme` is an
/// environment-theme record's name (`EnvTheme*`; docs/mod_api.md, Time and weather). The Dynamic
/// Sky's own weather override (`Engine_DynamicSky.WeatherKey`) is the HOST's switch, not this.
/// True when the chunk ran. Guest tick thread only.
extern "C" int32_t refii_mod_set_weather(PPCContext* ctx, uint8_t* base, const char* theme, float weight, float seconds);

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
  /// From the entity (refii_mod_play_fx_on), along the ENTITY'S OWN axes: an effect on an entity
  /// stands in its frame — the effect's axes are the entity's — and turns with it. An entity with
  /// no drawn body of its own (a vortex, a fireball's shot) is followed by position only, the
  /// effect unrotated and the offset in the guest's world axes.
  float offset[3];
  /// A HELPER POINT of the entity's model (refii_mod_play_fx_on): the name of a `.mdl.gmd`
  /// attachment or node — a sword's "Weapon.Weapon.Top" / "Weapon.Weapon.Hold", a creature's
  /// "Character.Carry.Hand.Right" — and the effect stands there, in the helper's own frame (its
  /// position AND orientation when the gmd states one), `offset` along the helper's axes; on a
  /// rigged body it hangs on the bone the helper hangs from, so it follows the pose. Null or ""
  /// is the entity's own frame. An unknown name is warned once and the entity's frame used.
  /// Under the classic look the helper's REST-pose point is folded into the offset.
  const char* at;
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

/// Plays effect `name` on `entity` (an address; its handle is what crosses): it stands in the
/// entity's own frame, offset by options->offset along the entity's axes, and moves and turns with
/// it.
extern "C" uint32_t refii_mod_play_fx_on(const char* name, uint32_t entity, const refii_mod_fx_options* options);

/// Stops a played effect by the id play_fx returned — any surface's id: native, Lua or Godot.
extern "C" void refii_mod_stop_fx(uint32_t id);

/// refii_mod_play_fx_on at the model helper `helper` (options->at, see refii_mod_fx_options): the
/// convenience form, options->at replaced by `helper`.
extern "C" uint32_t refii_mod_play_fx_at(const char* name, uint32_t entity, const char* helper,
                                         const refii_mod_fx_options* options);

// ---------------------------------------------------------------------------------------------
// Lights: a mod's omni light, placed as an effect is (posted as REFII_MOD_HOST_FX_KIND)
// ---------------------------------------------------------------------------------------------

/// The options of refii_mod_light. Zero-initialise and set what you need.
typedef struct refii_mod_light_options {
  /// From the entity, along its own axes (or the helper's when `at` names one), as
  /// refii_mod_fx_options.offset; ignored for a world point.
  float offset[3];
  /// A helper point of the entity's model, as refii_mod_fx_options.at ("Weapon.Weapon.Top"): the
  /// light stands there, on a rigged body on the helper's bone. Null or "" is the entity's frame.
  const char* at;
  /// 0..1: a waver of the energy (a torch, a brazier); 0 is steady.
  float flicker;
} refii_mod_light_options;

/// Places an omni light of colour `rgb` (0..1 each) at `energy` (1 is a placed lamp's) reaching
/// `range` metres (0: 6), on `entity` (an address; its handle crosses) in the entity's own frame
/// — riding the body and gone with it — or, with entity 0, at the world point `xyz` (the guest's
/// axes). Under the modern look it is a Godot light like the FX feed's own; under the classic look
/// it joins the guest's light list for every draw its sphere meets (Engine_ModLights). Returns the
/// id refii_mod_light_remove takes (0 for no colour). Any thread; it appears on Godot's next frame.
extern "C" uint32_t refii_mod_light(uint32_t entity, const float* xyz, const float* rgb, float energy, float range,
                                    const refii_mod_light_options* options);

/// Removes a mod light by the id refii_mod_light returned — any surface's id.
extern "C" void refii_mod_light_remove(uint32_t id);

// ---------------------------------------------------------------------------------------------
// HUD: a mod's world markers, screen fades and meters, drawn by Godot (posted as REFII_MOD_HOST_UI_KIND)
// ---------------------------------------------------------------------------------------------

/// Puts a marker on the HUD's world-icon channel for `player` (1 or 2; 0 for every player this
/// machine plays) over `entity` (an address; its handle crosses) or, with entity 0, at the world
/// point `xyz` (the guest's axes): `icon` a game texture path ("art/gui/.../x.tex") or a PNG of an
/// enabled mod ("art/x.png"), `text` a line under it (null or "" for none). It rides the drawn
/// body, is placed as the game's own icons are (each player's camera and half), and is sized by
/// the game's own distance curve. Returns the id refii_mod_unmark takes (0 for no icon). A
/// marker on an entity goes when the entity does. Any thread.
extern "C" uint32_t refii_mod_mark(int32_t player, uint32_t entity, const float* xyz, const char* icon,
                                   const char* text);

/// Removes a marker by the id refii_mod_mark returned — any surface's id.
extern "C" void refii_mod_unmark(uint32_t id);

/// Fades `player`'s screen (1 or 2; 0 for every player this machine plays) to black
/// (`to_black` non-zero) or back from it over `seconds` (0: at once): a host-drawn layer over that
/// player's half (their whole window alone or online), independent of the game's own fade, which
/// level changes own. Returns 1 when posted. Any thread.
extern "C" int32_t refii_mod_fade(int32_t player, int32_t to_black, float seconds);

/// Shows or updates the HUD meter `id` for `player` (1 or 2; 0 for every player this machine
/// plays): `label` above a bar at `value` of `max` (a timer, a boss bar), in `style` "bar" (null
/// or "": the HUD's gold bar), "boss" (wide, red, centred) or "timer" (narrow) — the HUD's own
/// primitives — or in the game's own meter art, "generic" (hud.bgf's GenericMeter, the ornate bar),
/// "crucible" (CrucibleMeter, the plain red bar) or "price" (PriceWidget, the bowl with the value as
/// a number): a copy lit on its own fader slide, the shipped original left as authored. Meters stack
/// under the top of the player's screen in the order first shown; the same id restates it in
/// place. Returns 1 when posted (0 for no id). Any thread.
extern "C" int32_t refii_mod_meter(int32_t player, const char* id, const char* label, float value, float max,
                                   const char* style);

/// Removes the meter `id` from every player's HUD. Returns 1 when posted.
extern "C" int32_t refii_mod_meter_remove(const char* id);

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
