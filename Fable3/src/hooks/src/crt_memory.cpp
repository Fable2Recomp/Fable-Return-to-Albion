// refii - Fable 3's CRT copy and fill, answered by the host's libc.
//
// The recompiler keeps the Xbox CRT's memcpy/memmove/memset and the XDK's XMemCpy as PPC code
// (byte loops with an unrolled vector path), so every copy the game makes runs them instruction
// by instruction. Guest memory is host memory at `base + address` and every guest store is a
// plain host store, so the host's own routines answer the same contract byte for byte
// (Fable2/src/vfs/src/hooks/crt_memory.cpp is the reference; this file names Fable 3's own
// entries, refiii_config.toml).
//
// EACH ENTRY IS FABLE 2'S ROUTINE AT FABLE 3'S ADDRESS, found by Fable 2 TU1's whole body (every
// word, relative branches masked) matching exactly one function start in the Fable 3 IDB, of the
// same size:
//   memcpy  0x82E64780 (0x488) `std r3,0(r1); clrlwi r6,r3,29; dcbt 0,r4` (Fable 2 0x82CDCBD0)
//   memset  0x82E64D80 (0xA0)  `addi r0,r5,1; mtctr r0; mr r6,r3` (Fable 2 0x82CDD100)
//   memmove 0x82E650F0 (0xBC)  `cmpw r3,r4; beqlr; bge 0x82E65100; b memcpy` and a backward
//           byte loop (Fable 2 0x82CE4228, which Fable 2 does not hook: its forward half is
//           memcpy; Fable 3 answers the backward half too)
//   XMemCpy 0x8221E4E0 (0x2F4) no word match (register allocation moved); the same routine by
//           shape: `bl __savegprlr_26; mr r26,r3; mr r29,r5`, the <16/<8/<4 heads into _blkmov
//           (0x82E64C08, Fable 2's _blkmov word for word), the 0x400 dcbt and 0x80 dcbz128 blocks,
//           the misaligned-cache-line helper call (0x8221E758), `mr r3,r26` on both returns.
// NOT NAMED: XMemSet128 (Fable 2 0x82CF9618) matches no Fable 3 function, and Fable 2 does not
// answer it either.
//
// The translation is the SDK's `GuestPtr`, not bare `base + address`: on Windows and macOS arm64
// the 0xE0000000 physical alias sits 0x1000 bytes further into the arena than its guest address
// says (rex::memory::detail::PhysicalHostOffset), and the codegen adds that offset to every guest
// load and store.
//
// PowerPC ABI: r3 = destination, r4 = source (copies) or the fill byte (memset), r5 = length;
// each returns its destination in r3, which is left as the caller passed it. memmove for every
// copy: the memcpy and XMemCpy contracts forbid overlap, and memmove is never wrong where memcpy
// would be.
#include <algorithm>
#include <cstdint>
#include <cstring>

#include <rex/ppc.h>
#include <rex/system/xmemory.h>

namespace {

constexpr uint32_t kPhysicalAliasBase = 0xE0000000u;

inline uint8_t* GuestBytes(uint8_t* base, uint32_t address) {
  return rex::memory::GuestPtr<uint8_t*>(base, address);
}

/// The length of `[address, address + length)` that stays on one side of the 0xE0000000 alias
/// boundary, where the host offset changes.
inline uint32_t ContiguousSpan(uint32_t address, uint32_t length) {
  if (address < kPhysicalAliasBase && length > kPhysicalAliasBase - address) {
    return kPhysicalAliasBase - address;
  }
  return length;
}

void GuestMove(uint8_t* base, uint32_t destination, uint32_t source, uint32_t length) {
  if (destination > source && destination - source < length) {
    // Overlapping with the destination above: copied tail first, span by span, so no byte is
    // read after it was overwritten.
    while (length != 0u) {
      const uint32_t at_destination = destination + length;
      const uint32_t at_source = source + length;
      uint32_t span = length;
      if (at_destination > kPhysicalAliasBase && destination < kPhysicalAliasBase) {
        span = std::min(span, at_destination - kPhysicalAliasBase);
      }
      if (at_source > kPhysicalAliasBase && source < kPhysicalAliasBase) {
        span = std::min(span, at_source - kPhysicalAliasBase);
      }
      length -= span;
      std::memmove(GuestBytes(base, destination + length), GuestBytes(base, source + length), span);
    }
    return;
  }
  while (length != 0u) {
    const uint32_t span = std::min(ContiguousSpan(destination, length), ContiguousSpan(source, length));
    std::memmove(GuestBytes(base, destination), GuestBytes(base, source), span);
    destination += span;
    source += span;
    length -= span;
  }
}

void GuestFill(uint8_t* base, uint32_t destination, int value, uint32_t length) {
  while (length != 0u) {
    const uint32_t span = ContiguousSpan(destination, length);
    std::memset(GuestBytes(base, destination), value, span);
    destination += span;
    length -= span;
  }
}

}  // namespace

REX_EXTERN(rex_memcpy) { GuestMove(base, ctx.r3.u32, ctx.r4.u32, ctx.r5.u32); }

REX_EXTERN(rex_memmove) { GuestMove(base, ctx.r3.u32, ctx.r4.u32, ctx.r5.u32); }

REX_EXTERN(rex_XMemCpy) { GuestMove(base, ctx.r3.u32, ctx.r4.u32, ctx.r5.u32); }

REX_EXTERN(rex_memset) { GuestFill(base, ctx.r3.u32, static_cast<int>(ctx.r4.u8), ctx.r5.u32); }
