// The guest's CRT copy and fill, answered by the host's libc.
//
// The recompiler keeps the Xbox CRT's memcpy (0x82CA9480), memset (0x82CA99B0) and XMemCpy
// (0x82208498) as PowerPC code — byte loops with an unrolled vector path — and names them in
// generated/refii_init.cpp. Guest memory is host memory at `base + address` and every guest
// store is a plain host store (REX_STORE_* in the codegen prelude), so the host's own routines
// answer the same contract byte for byte, on x86_64 and ARM64 alike.
//
// The translation is the SDK's `GuestPtr`, not bare `base + address`: on Windows and macOS
// arm64 the 0xE0000000 physical alias (write-combined memory, where the GPU-visible shader and
// vertex data lives) sits 0x1000 bytes further into the arena than its guest address says
// (rex::memory::detail::PhysicalHostOffset — the mapping API rounds the view down to its
// allocation granularity). The codegen's REX_RAW_ADDR adds that offset to every guest load
// and store, so a copy that skips it lands 4 KB away from where the guest then reads it.
//
// PowerPC ABI: r3 = destination, r4 = source (memcpy/XMemCpy) or the fill byte (memset),
// r5 = length; each returns its destination in r3. memmove rather than memcpy for the copies:
// the contract forbids overlap, and memmove is never wrong where memcpy would be.
#include <algorithm>
#include <cstdint>
#include <cstring>

#include <rex/system/xmemory.h>

#include "vfs_hooks_private.h"

namespace {

constexpr uint32_t kPhysicalAliasBase = 0xE0000000u;

inline uint8_t* GuestBytes(uint8_t* base, uint32_t address) {
    return rex::memory::GuestPtr<uint8_t*>(base, address);
}

/// The length of `[address, address + length)` that stays on one side of the 0xE0000000
/// alias boundary, where the host offset changes. A range never straddles it in practice
/// (the alias is its own heap), but a copy that did would need two host ranges.
inline uint32_t ContiguousSpan(uint32_t address, uint32_t length) {
    if (address < kPhysicalAliasBase && length > kPhysicalAliasBase - address) {
        return kPhysicalAliasBase - address;
    }
    return length;
}

void GuestMove(uint8_t* base, uint32_t destination, uint32_t source, uint32_t length) {
    while (length != 0u) {
        const uint32_t span =
            std::min(ContiguousSpan(destination, length), ContiguousSpan(source, length));
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

REX_EXTERN(rex_memcpy) {
    REX_FUNC_PROLOGUE();
    GuestMove(base, ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

REX_EXTERN(rex_XMemCpy) {
    REX_FUNC_PROLOGUE();
    GuestMove(base, ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

REX_EXTERN(rex_memset) {
    REX_FUNC_PROLOGUE();
    GuestFill(base, ctx.r3.u32, static_cast<int>(ctx.r4.u8), ctx.r5.u32);
}
