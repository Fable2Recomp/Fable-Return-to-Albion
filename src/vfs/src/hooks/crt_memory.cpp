// The guest's CRT copy and fill, answered by the host's libc.
//
// The recompiler keeps the Xbox CRT's memcpy (0x82CA9480), memset (0x82CA99B0) and XMemCpy
// (0x82208498) as PowerPC code — byte loops with an unrolled vector path — and names them in
// generated/refii_init.cpp. Guest memory is host memory at `base + address` and every guest
// store is a plain host store (REX_STORE_* in the codegen prelude), so the host's own routines
// answer the same contract byte for byte, on x86_64 and ARM64 alike.
//
// PowerPC ABI: r3 = destination, r4 = source (memcpy/XMemCpy) or the fill byte (memset),
// r5 = length; each returns its destination in r3. memmove rather than memcpy for the copies:
// the contract forbids overlap, and memmove is never wrong where memcpy would be.
#include <cstdint>
#include <cstring>

#include "vfs_hooks_private.h"

namespace {

inline uint8_t* GuestBytes(uint8_t* base, uint32_t address) { return base + address; }

}  // namespace

REX_EXTERN(rex_memcpy) {
    REX_FUNC_PROLOGUE();
    std::memmove(GuestBytes(base, ctx.r3.u32), GuestBytes(base, ctx.r4.u32), ctx.r5.u32);
}

REX_EXTERN(rex_XMemCpy) {
    REX_FUNC_PROLOGUE();
    std::memmove(GuestBytes(base, ctx.r3.u32), GuestBytes(base, ctx.r4.u32), ctx.r5.u32);
}

REX_EXTERN(rex_memset) {
    REX_FUNC_PROLOGUE();
    std::memset(GuestBytes(base, ctx.r3.u32), static_cast<int>(ctx.r4.u8), ctx.r5.u32);
}
