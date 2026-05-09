#pragma once

#include <cstdint>

#include <rex/system/xmemory.h>
#include <rex/ppc/context.h>
#include <rex/types.h>

namespace refii::lua {

inline uint8_t PpcLoadU8(uint8_t* base, uint32_t guest_address) {
  return *rex::memory::GuestPtr<uint8_t*>(base, guest_address);
}

inline uint32_t PpcLoadU32(uint8_t* base, uint32_t guest_address) {
  return static_cast<uint32_t>(
      *rex::memory::GuestPtr<rex::be<uint32_t>*>(base, guest_address));
}

inline uint8_t* PpcGuestPtr(uint8_t* base, uint32_t guest_address) {
  return rex::memory::GuestPtr<uint8_t*>(base, guest_address);
}

}  // namespace refii::lua

#if !defined(REX_FUNC_PROLOGUE)
#define REX_FUNC_PROLOGUE() ((void)0)
#endif

#if !defined(PPC_EXTERN_FUNC)
#if defined(REX_EXTERN)
#define PPC_EXTERN_FUNC(symbol) REX_EXTERN(symbol)
#elif defined(REX_FUNC)
#define PPC_EXTERN_FUNC(symbol) extern "C" REX_FUNC(symbol)
#else
#define PPC_EXTERN_FUNC(symbol) extern "C" void symbol([[maybe_unused]] PPCContext& __restrict ctx, uint8_t* base)
#endif
#endif
