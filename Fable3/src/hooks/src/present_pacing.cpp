// refii - Fable 3's present, paced to the vblank the null device does not have.
//
// On hardware every swap waited for its vblank; with no GPU the 3D Engine thread (sub_822E1CE0)
// presents as fast as it can loop, measured ~1,300 presents a second into nothing. The guest
// states its own rate: its frame clock sub_82D4A9F8 advances by dword_8330F080 / flt_8330F084
// per present (presentation interval 2 over 60.0 Hz, read live 2026-10-30) — 30 Hz.
//
// THE SITE. The engine loop holds its lock (the critical section at the thread's argument)
// across the frame sub_822E1EA0 and releases it at 0x822E1E4C, then calls slot 3 of the app
// object (*dword_83566A78, vtable 0x820025C4) at 0x822E1E64 before re-entering at 0x822E1D90.
// That call is sub_82D32268, an idle wait on the app's flags; it is the only point in the loop
// where the lock is free, so the frame's pacing sleep lives here and every waiter gets the
// whole period to take the lock — the same seam as Fable 2's rex_lhApp_IdleWaitForFlags.

#include <chrono>
#include <cstdint>
#include <cstring>

#include <rex/ppc.h>

#include "core/precise_sleep.h"

REX_EXTERN(__imp__rex_App_IdleWaitForFlags);

namespace {

constexpr uint32_t kEngineLoopReturn = 0x822E1E68u;
constexpr uint32_t kPresentInterval = 0x8330F080u;
constexpr uint32_t kRefreshHz = 0x8330F084u;

uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

/// One present period as the guest states it, or zero while it states none.
std::chrono::steady_clock::duration StatedPeriod(const uint8_t* base) {
  const int32_t interval = static_cast<int32_t>(LoadGuestU32(base, kPresentInterval));
  const uint32_t bits = LoadGuestU32(base, kRefreshHz);
  float refresh = 0.0f;
  std::memcpy(&refresh, &bits, sizeof(refresh));
  if (interval <= 0 || !(refresh > 0.0f)) {
    return std::chrono::steady_clock::duration::zero();
  }
  return std::chrono::duration_cast<std::chrono::steady_clock::duration>(
      std::chrono::duration<double>(static_cast<double>(interval) / refresh));
}

void PacePresent(const uint8_t* base) {
  const auto period = StatedPeriod(base);
  if (period == std::chrono::steady_clock::duration::zero()) {
    return;
  }

  static std::chrono::steady_clock::time_point next{};
  const auto now = std::chrono::steady_clock::now();
  if (next < now - period) {
    next = now;  // fell behind (a load, a stall): resume from now rather than catch up
  }

  if (next > now) {
    refii::PreciseSleepUntil(next);
  }

  next += period;
}

}  // namespace

REX_EXTERN(rex_App_IdleWaitForFlags) {
  const uint32_t caller = ctx.lr;
  __imp__rex_App_IdleWaitForFlags(ctx, base);
  if (caller == kEngineLoopReturn) {
    PacePresent(base);
  }
}
