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
//   ONE BODY PER FUNCTION. Two mods, or a mod and refii, defining the same function is refused at
//   build time with both named. It is never resolved by load order.
//
//   GUEST MEMORY is big-endian at `base + address`; use the helpers below. A raw TU1 address
//   goes through refii::guest::Address() so it is right on the disc build too.
//
//   TALK TO GODOT with refii::mods::Publish("<mod>.<name>", value) and read settings with
//   refii::mods::Setting(...). A mod's HUD piece binds to "mod:<mod>.<name>".
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

/// The local player's hero entity, or 0 when there is none (front end, loading).
extern "C" uint32_t refii_mod_local_hero(uint8_t* base);

/// Player `player`'s hero entity (1 = the hero, 2 = the co-op partner), or 0. Both players are
/// principals: a mod that changes how the hero plays applies it to either.
extern "C" uint32_t refii_mod_player_hero(uint8_t* base, int32_t player);

/// Component `id` of `entity`, or 0 — the guest's own lookup (presence bits at +0x24, the sorted
/// {id, component} pairs at +0x48..+0x4C, the index table at +0x8C). Ids: 33 the player
/// controller, 36 health, 57 stats, 164 experience.
extern "C" uint32_t refii_mod_component(uint8_t* base, uint32_t entity, uint32_t id);

/// Runs a Lua source chunk (under 944 bytes) in the GAME VM — the shipped game scripts' and
/// mods' Lua. True when it ran without error. Guest tick thread only (a tick callback or a hook).
extern "C" int32_t refii_mod_run_game_lua(PPCContext* ctx, uint8_t* base, const char* chunk);

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

}  // namespace refii::mods
