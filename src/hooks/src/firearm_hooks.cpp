// Native override for CECFirearm::Serialize (sub_8272A270 in TU1).
//
// In vanilla Fable 2, CECFirearm::Serialize unconditionally writes and reads
// 22 static archetype definition properties (MaxBullets, BulletsPerReload,
// ReloadOnlyIfEmpty, ReloadTime, Range, Damage multipliers, Zoom FOVs, etc.)
// to/from mainsave.bin, alongside the single dynamic runtime field NumBullets.
//
// This caused stale modded weapon stats to persist across saves: when a mod was
// reverted or removed, CECFirearm::InitFromGDB correctly initialized the vanilla stats,
// but CECFirearm::Serialize immediately read the saved XML values and clobbered them.
//
// This hook overrides CECFirearm::Serialize to ONLY serialize "NumBullets", ignoring
// any static archetype tags from existing saves and preventing future saves from
// baking static stats into disk saves. It also clamps NumBullets <= MaxBullets.

#include <algorithm>
#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/guest_build.h"

namespace {

inline uint32_t LoadGuestU32(const uint8_t* base, uint32_t guest_address) {
  uint32_t be;
  std::memcpy(&be, base + guest_address, sizeof(be));
  return __builtin_bswap32(be);
}

inline void StoreGuestU32(uint8_t* base, uint32_t guest_address, uint32_t value) {
  const uint32_t be = __builtin_bswap32(value);
  std::memcpy(base + guest_address, &be, sizeof(be));
}

}  // namespace

extern "C" REX_FUNC(__imp__rex_lhCharString_CtorFromCStr);
extern "C" REX_FUNC(__imp__rex_lhCharString_Dtor);
extern "C" REX_FUNC(__imp__rex_Serializer_SerializeInt32);

// int CECFirearm::Serialize(CECFirearm* this, CSerializer* serializer)
REX_EXTERN(rex_CECFirearm_Serialize) {
  const uint32_t firearm_addr = ctx.r3.u32;
  const uint32_t serializer_addr = ctx.r4.u32;

  if (!firearm_addr || !serializer_addr) {
    ctx.r3.u64 = 0;
    return;
  }

  // Allocate stack space for the temporary string object
  const uint32_t frame = ctx.r1.u32 - 0x80u;
  const uint32_t str_addr = frame + 0x50u;
  const uint32_t text_addr = frame + 0x20u;

  const char tag[] = "NumBullets";
  for (uint32_t i = 0; tag[i] != '\0'; ++i) {
    base[text_addr + i] = static_cast<uint8_t>(tag[i]);
  }
  base[text_addr + sizeof(tag) - 1] = 0;

  // 1. Construct CStdString(&str, "NumBullets")
  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = str_addr;
  work.r4.u32 = text_addr;
  work.r5.u32 = 0xFFFFFFFFu;
  __imp__rex_lhCharString_CtorFromCStr(work, base);

  // 2. Resolve c_str pointer:
  // When str_addr points to valid buffer, dereference once; otherwise fallback to empty string
  // The guest's empty string literal (`byte_8208FFDF` in TU1), by the running build.
  uint32_t cstr_ptr = refii::guest::Address(0x8208FFDFu);
  const uint32_t str_buf_ptr = LoadGuestU32(base, str_addr);
  if (str_buf_ptr != 0) {
    cstr_ptr = LoadGuestU32(base, str_buf_ptr);
  }

  // 3. Serialize "NumBullets" at firearm + 0x10
  work.r1.u32 = frame;
  work.r3.u32 = serializer_addr;
  work.r4.u32 = cstr_ptr;
  work.r5.u32 = firearm_addr + 0x10u;  // &firearm->NumBullets
  __imp__rex_Serializer_SerializeInt32(work, base);

  // 4. Destruct CStdString(&str)
  work.r1.u32 = frame;
  work.r3.u32 = str_addr;
  __imp__rex_lhCharString_Dtor(work, base);

  // 5. Clamp NumBullets <= MaxBullets
  const uint32_t max_bullets = LoadGuestU32(base, firearm_addr + 12u);
  const uint32_t num_bullets = LoadGuestU32(base, firearm_addr + 16u);
  if (num_bullets > max_bullets && max_bullets > 0) {
    StoreGuestU32(base, firearm_addr + 16u, max_bullets);
  }

  ctx.r3.u64 = 1;
}
