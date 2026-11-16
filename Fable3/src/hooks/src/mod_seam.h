// refii - what Fable 3's mod seams read from guest memory (mod_events_hooks.cpp, mod_combat_hooks.cpp):
// bounded big-endian loads, the guest's own live test, an entity's components, a smart pointer's
// object, and the field text every seam hands the mods.
#pragma once

#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>

#include <rex/ppc.h>

#include "core/payload_bridge.h"
#include "core/rex_payload_bridge.h"

namespace refii::fable3::seam {

inline bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

inline uint32_t LoadU32(const uint8_t* base, uint32_t at) {
  uint32_t value;
  std::memcpy(&value, base + at, sizeof(value));
  return __builtin_bswap32(value);
}

inline void StoreU32(uint8_t* base, uint32_t at, uint32_t value) {
  const uint32_t be = __builtin_bswap32(value);
  std::memcpy(base + at, &be, sizeof(be));
}

inline uint32_t Word(const uint8_t* base, uint32_t at) { return Plausible(at) ? LoadU32(base, at) : 0u; }

inline uint8_t Byte(const uint8_t* base, uint32_t at) { return Plausible(at) ? base[at] : 0u; }

inline float Float(const uint8_t* base, uint32_t at) {
  const uint32_t bits = Word(base, at);
  float value;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

inline void StoreFloat(uint8_t* base, uint32_t at, float value) {
  uint32_t bits;
  std::memcpy(&bits, &value, sizeof(bits));
  StoreU32(base, at, bits);
}

/// An entity that passes the guest's own live test (`*(u8*)(e+0x94) & 0x40`, IsPlayer 0x8220926c).
inline bool Live(const uint8_t* base, uint32_t entity) {
  return Plausible(entity) && (base[entity + 0x94u] & 0x40u) != 0u;
}

/// A live entity's mod handle, 0 for anything else.
inline uint32_t Handle(const uint8_t* base, uint32_t entity) {
  return Live(base, entity) ? refii_bridge_mod_handle(entity) : 0u;
}

/// Any other guest object's mod handle (an action), 0 for none.
inline uint32_t ObjectHandle(uint32_t object) { return Plausible(object) ? refii_bridge_mod_handle(object) : 0u; }

/// What a smart pointer at `pointer` holds: its +4 is the block whose first word is the object
/// (lhSmartPtr_Deref 0x82691588: `lwz r11,4(r3); lwz r3,0(r11)`), 0 when the block is gone. Read
/// only: the guest's own deref also releases a dead block, which a seam leaves to the guest.
inline uint32_t Deref(const uint8_t* base, uint32_t pointer) {
  const uint32_t object = Word(base, Word(base, pointer + 4u));
  return Plausible(object) ? object : 0u;
}

/// The entity's component `id`: its {id, component} pairs from +0x54 to +0x58, which hold every
/// component (the +0x90 index table only indexes them: `ROL(*(u8*)(table + id), 3) + *(e+0x54) + 4`,
/// CommitSwing 0x829c4438), or 0.
inline uint32_t Component(const uint8_t* base, uint32_t entity, uint32_t id) {
  const uint32_t first = Word(base, entity + 0x54u);
  const uint32_t last = Word(base, entity + 0x58u);
  if (!Plausible(first) || last < first || last - first > 8u * 256u) {
    return 0u;
  }
  for (uint32_t at = first; at < last; at += 8u) {
    if (LoadU32(base, at) == id) {
      const uint32_t component = LoadU32(base, at + 4u);
      return Plausible(component) ? component : 0u;
    }
  }
  return 0u;
}

/// A copy of `ctx` with the stack pointer moved below the caller's frame.
inline PPCContext Frame(const PPCContext& ctx, uint32_t bytes = 0x200u) {
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - bytes) & ~0xFu;
  return work;
}

/// Calls the guest function at `address` (a vtable slot's value) with `work` as set up. False
/// when the address is not a recompiled function.
inline bool CallAt(PPCContext& work, uint8_t* base, uint32_t address) {
  using GuestFn = void (*)(PPCContext&, uint8_t*);
  if (!Plausible(address) || refii_bridge_rex_has_function(address) == 0) {
    return false;
  }
  auto fn = reinterpret_cast<GuestFn>(refii_bridge_rex_resolve_indirect_function(address));
  if (fn == nullptr) {
    return false;
  }
  fn(work, base);
  return true;
}

/// Calls virtual slot `slot` of `object` (r3 = object; r4.. as `work` holds them).
inline bool CallSlot(PPCContext& work, uint8_t* base, uint32_t object, uint32_t slot) {
  const uint32_t vtable = Word(base, object);
  if (!Plausible(vtable)) {
    return false;
  }
  work.r3.u32 = object;
  return CallAt(work, base, Word(base, vtable + 4u * slot));
}

inline std::string Fields(const char* format, ...) __attribute__((format(printf, 1, 2)));
inline std::string Fields(const char* format, ...) {
  char text[768];
  va_list args;
  va_start(args, format);
  const int n = std::vsnprintf(text, sizeof(text), format, args);
  va_end(args);
  return n < 0 ? std::string() : std::string(text, n < static_cast<int>(sizeof(text)) ? n : sizeof(text) - 1);
}

/// A Lua literal boolean.
inline const char* Bool(bool value) { return value ? "true" : "false"; }

/// A float the field text can carry (NaN and infinities as 0).
inline double Fin(float value) { return value == value && value - value == 0.0f ? static_cast<double>(value) : 0.0; }

}  // namespace refii::fable3::seam
