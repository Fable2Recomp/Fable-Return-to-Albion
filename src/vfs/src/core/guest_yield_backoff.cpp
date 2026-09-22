#include "core/guest_yield_backoff.h"

#include "core/precise_sleep.h"

#include <atomic>
#include <chrono>
#include <cstdlib>
#include <string_view>

namespace refii::vfs {

namespace {

// Fast yields allowed before escalating. At the measured 449 ns per yield this is
// ~115 us of ordinary spinning, so every short handoff keeps the latency it has today
// and only a wait that is already long escalates.
constexpr uint32_t kFastYields = 256u;

// A gap wider than this means the thread did real work between yields, so the run
// restarts. Sits deliberately just above kMaxSleepUs: it MUST exceed it, or the sleep
// this file imposes would itself look like work and reset the run every time, but every
// microsecond above that is a thread doing real work that could be mistaken for a spin.
// Measured spin iterations are single-digit microseconds, and a backed-off iteration is
// the sleep plus those — so 750 us separates the two with ~245 us of margin while still
// exempting any loop that does 750 us of work per yield.
constexpr uint64_t kWorkGapUs = 750u;

// Sleep growth, doubling from the floor. The floor is the smallest sleep a Linux host
// actually honours — a 1 ns clock_nanosleep measured 53.6 us, so asking for less just
// wastes a syscall. Windows honours nothing under ~500 us; refii::PreciseSleepFor spends a
// shorter request yielding, so the ramp costs the same time there and only the ceiling
// sleeps. See core/precise_sleep.h.
//
// THE RAMP IS WHAT PROTECTS SHORT WAITS. Reaching the ceiling costs 256 fast yields
// (~115 us) plus 50+100+200+400 us of sleeps, so no wait shorter than ~865 us ever sees
// a 500 us sleep; a brief handoff sees 50 us at worst. The ceiling itself stays far
// inside the 33.3 ms budget the drain loop at 0x822DFE08 gives itself (RefreshRate 60 /
// PresentationInterval 2), so a back-off can never be what ends one of these waits.
//
// Sleeping does NOT extend a lock hold. The drain spin holds the GPU-access lock either
// way and its exit condition is unchanged, so the hold lasts exactly as long as the wait
// does — and not thrashing the scheduler lets the producer it is waiting on publish
// sooner, which shortens it.
constexpr uint32_t kMinSleepUs = 50u;
constexpr uint32_t kMaxSleepUs = 500u;

struct YieldRun {
  uint64_t last_yield_us = 0;
  uint32_t last_lr = 0;
  uint32_t length = 0;
  uint32_t sleep_us = 0;
};

thread_local YieldRun t_run;

std::atomic<uint64_t> g_sleeps{0};

uint64_t NowMicroseconds() {
  return static_cast<uint64_t>(
      std::chrono::duration_cast<std::chrono::microseconds>(
          std::chrono::steady_clock::now().time_since_epoch())
          .count());
}

// REFII_YIELD_BACKOFF=0 leaves every guest yield a plain yield: the A/B for "is the back-off
// what makes guest state arrive unevenly on this host". Costs the ~1.3 cores it exists to save.
bool BackoffEnabled() {
  static const bool enabled = [] {
    const char* value = std::getenv("REFII_YIELD_BACKOFF");
    return value == nullptr || std::string_view(value) != "0";
  }();
  return enabled;
}

}  // namespace

void MaybeBackOffBeforeGuestYield(uint32_t lr) {
  if (!BackoffEnabled()) {
    return;
  }

  const uint64_t now_us = NowMicroseconds();
  const uint64_t gap_us = now_us - t_run.last_yield_us;

  // A different call site, or a wide enough gap, is the only progress signal available
  // here — the hook cannot see what the guest did in between. Either one restarts the
  // run, so a thread that is genuinely working never escalates.
  const bool same_place = (lr == t_run.last_lr) && t_run.last_yield_us != 0u;
  const bool spinning = same_place && gap_us <= kWorkGapUs;
  // Saturates rather than wrapping: past the threshold the exact count means nothing,
  // and an unbounded counter would eventually wrap back through the fast path.
  t_run.length = spinning ? (t_run.length < kFastYields ? t_run.length + 1u : kFastYields)
                          : 0u;
  t_run.last_lr = lr;

  if (t_run.length < kFastYields) {
    t_run.sleep_us = 0u;
    t_run.last_yield_us = now_us;
    return;
  }

  t_run.sleep_us = (t_run.sleep_us == 0u) ? kMinSleepUs : t_run.sleep_us * 2u;
  if (t_run.sleep_us > kMaxSleepUs) {
    t_run.sleep_us = kMaxSleepUs;
  }

  // Header-only, not rex::thread::Sleep, DELIBERATELY. This compiles into the payload,
  // which the player's Zig builds; on Windows the rex runtime is MSVC-built and a C++
  // symbol imported from it does not resolve against a MinGW-driver link — the same reason
  // the log bridge carries -DFMT_HEADER_ONLY. And not std::this_thread::sleep_for: on
  // Windows that rounds every step of this ramp up to a whole timer tick.
  g_sleeps.fetch_add(1, std::memory_order_relaxed);
  refii::PreciseSleepFor(std::chrono::microseconds(t_run.sleep_us));

  t_run.last_yield_us = NowMicroseconds();
}

uint64_t GuestYieldBackoffSleepCount() {
  return g_sleeps.load(std::memory_order_relaxed);
}

}  // namespace refii::vfs
