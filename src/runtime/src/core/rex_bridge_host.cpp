// refii — the host side of the payload's ReXGlue runtime bridge.
//
// Each entry is the real rex:: call, reached through C so the payload never imports a C++
// symbol from us. Lives in refii_runtime, which the payload already links, so no shadow SDK
// source is needed; the pointers are the host's own objects handed back opaquely.

#include "core/rex_payload_bridge.h"

// For the cvar bridge below: TakeString turns the caller-buffer getter back into a
// std::string on THIS side's heap, which is where rex::cvar's getter has to return one.
#include "core/guest_step_sampler.h"
#include "core/payload_bridge_string.h"
#include "core/traced_mutex.h"

#include <algorithm>
#include <atomic>
#include <cstring>
#include <mutex>
#include <filesystem>
#include <string>
#include <string_view>
#include <utility>

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/thread.h>

#include <rex/chrono/clock.h>
#include <rex/perf/counter.h>
#include <rex/ppc/context.h>
#include <rex/runtime.h>
#include <rex/system/kernel_state.h>
#include <rex/system/mmio_handler.h>
#include <rex/system/xmemory.h>
#include <rex/system/xthread.h>
#include <rex/kernel/xboxkrnl/video.h>
#include <rex/kernel/xboxkrnl/threading.h>

namespace {

/// The guest's time base, owned by the host.
///
/// `mftb` in recompiled code funnels through here, and the guest's in-game loop (`sub_82276D10`)
/// runs its own wall-clock accumulator on it, on its own thread, ungated by Godot. When more
/// than one step is owed it advances ONE step and re-bases the sim clock to now, discarding the
/// backlog:
///
///     82276fa4  cmpwi cr6, r30, 1
///     82276fb0  ble   cr6, loc_82276FC8   ; owed <= 1 -> normal
///     82276fb4  addi  r29, r28, 1         ; advance exactly one step
///     82276fb8  fmadd f0, f27, f24, f31   ; f24 = 0.0, so f0 = now
///     82276fbc  stfd  f0, 0x110(r31)      ; sim clock := now, backlog discarded
///
/// Modes:
///   Off     — the real host clock; the guest drops time under contention.
///   Paced   — caps the advance between consecutive reads on a thread, so the loop always finds
///             one step owed; a starved guest runs slow instead of skipping.
///   Driven  — the clock advances only when the host grants a step, so the guest's rate is
///             Godot's and its stepping is deterministic (required for lockstep multiplayer).
///
/// Other guest threads (audio, streaming, network, timed waits) read the same clock, so in
/// driven mode a watchdog resumes real-rate time after `kGrantWatchdogSteps` without a grant
/// and counts the lapse. The clock never leads real time by more than `kMaxLeadSteps`.
enum class TimeBaseMode : int { Off = 0, Paced = 1, Driven = 2 };

/// Driven by default. `{"op":"render","timeBase":0}` restores the real clock.
std::atomic<int> g_time_base_mode{static_cast<int>(TimeBaseMode::Driven)};
std::atomic<uint64_t> g_virtual_ticks{0};
std::atomic<uint64_t> g_capped_reads{0};
std::atomic<uint64_t> g_granted_steps{0};
std::atomic<uint64_t> g_watchdog_lapses{0};

/// Whether the clock is currently running on the watchdog rather than on grants.
/// `g_watchdog_lapses` counts transitions into a lapse, not reads during one.
bool g_in_lapse = false;

/// Granted steps not yet released to the guest, because releasing them would have run the
/// clock further ahead of real time than `kMaxLeadSteps` allows. Deferred, not dropped, so the
/// guest's rate equals the grant rate.
std::atomic<uint64_t> g_pending_steps{0};

/// Granted steps dropped because the backlog exceeded `kMaxPendingSteps`: the guest cannot
/// consume the rate it is granted.
std::atomic<uint64_t> g_dropped_steps{0};
refii::TracedMutex g_time_mutex{"guest clock grant"};

/// Atomic because the fast read path in `refii_bridge_rex_query_guest_tick_count` does not
/// take the lock; both are only written under `g_time_mutex`.
std::atomic<uint64_t> g_last_grant_real{0};
std::atomic<uint64_t> g_last_seen_real{0};
std::atomic<bool> g_time_base_frozen{false};
std::atomic<uint64_t> g_frozen_real_ticks{0};
std::atomic<uint64_t> g_freeze_start_real{0};
std::atomic<bool> g_step_in_flight{false};
std::atomic<uint64_t> g_step_release_real{0};
std::atomic<uint64_t> g_last_hf_step_real{0};
std::atomic<bool> g_host_read_active{false};
std::atomic<uint64_t> g_host_read_start_real{0};

/// Real clock ticks excluding intentional host freeze intervals (pause, load cover), so
/// `g_virtual_ticks` and `effective_real` stay on the same continuous timeline across holds.
uint64_t EffectiveReal(uint64_t real) {
  uint64_t frozen = g_frozen_real_ticks.load(std::memory_order_relaxed);
  if (g_time_base_frozen.load(std::memory_order_relaxed)) {
    const uint64_t start = g_freeze_start_real.load(std::memory_order_relaxed);
    if (start != 0u && real > start) {
      frozen += (real - start);
    }
  }
  return real > frozen ? (real - frozen) : 0u;
}

/// Clock reads that answered without taking the lock, and those that had to. Every guest
/// `mftb` lands here and the guest loop spins on it (`SwitchToThread` then re-read at
/// `0x82277344`), while Godot's frame takes `g_time_mutex` to grant time, so the common case
/// must stay lock-free; `g_locked_reads` shows whether it does.
std::atomic<uint64_t> g_fast_reads{0};
std::atomic<uint64_t> g_locked_reads{0};

/// A real-clock slice on this thread: while set, driven-mode reads answer the virtual tick
/// plus the real time elapsed since the slice began, so a guest loop that runs "until now +
/// N ms" on `mftb` gets N ms of real time. The guest's Lua GC driver (`sub_821956A0`) is that
/// loop: with the clock frozen between grants its 2 ms deadline was unreachable and it marked
/// and swept until the host's next frame. Thread-local, POD, never set from a signal handler.
thread_local bool t_real_slice = false;
thread_local uint64_t t_real_slice_base = 0;

/// High-frequency steps a second — `dbl_83319668`, raised from the image's 15.0 to 30.0 during
/// `Game_InitDatabases` (0x82310900); the image bytes are not this number.
constexpr uint64_t kGuestHfStepsPerSecond = 30u;

/// How long without a grant before time resumes on its own, in HF steps.
///
/// The watchdog also fires the moment a title's tick pump reports its HF step open for longer
/// than one step (core/guest_step_sampler.h): the game thread is then blocked on another guest
/// thread, and the host is withholding grants because that very tick is not moving. Frozen,
/// the clock stops the thread it waits on — Fable 3's scene-stream consumer is gated on the
/// frame clock (sub_822A21A8, `dbl_8330E498[slot] < T` from mftb stamps) and its loading
/// screen fades on mftb (sub_823022D0) — so each such wait cost the whole 8-step lapse before
/// it could even start (r2: fence 1464 ms, fade 898 ms). A title that states no steps keeps the
/// 8-step rule.
constexpr uint64_t kGrantWatchdogSteps = 8u;

/// How far the virtual clock may lead real time, in HF steps — bounded to four steps so a
/// low-FPS host frame (15-29 FPS) can drain its exact 2..4 single-step grants sequentially
/// within the frame window without allowing unbounded fast-forward bursts.
constexpr uint64_t kMaxLeadSteps = 4u;

/// The most deferred steps the queue may hold; the excess is dropped and counted in
/// `g_dropped_steps`, so a guest that cannot keep up does not lag permanently.
constexpr uint64_t kMaxPendingSteps = 8u;

/// Whether the watchdog is due: silence for `kGrantWatchdogSteps`, or a stated step blocked
/// beyond `kMaxLeadSteps` step periods (`StepBlocked` measures raw wall ticks).
bool WatchdogDue(uint64_t effective_real, uint64_t raw_real, uint64_t seen_grant, uint64_t step) {
  if (g_time_base_frozen.load(std::memory_order_relaxed)) {
    return false;
  }

  return (effective_real > seen_grant &&
          (effective_real - seen_grant) > (kGrantWatchdogSteps * step)) ||
         refii::pace::StepBlocked(raw_real, kMaxLeadSteps * step);
}

uint64_t HfStepTicks() {
  const uint64_t frequency = ::rex::chrono::Clock::guest_tick_frequency();
  return frequency / kGuestHfStepsPerSecond;
}

/// True while a released step has not yet completed its 30 Hz HF pass (`refii_bridge_rex_note_guest_step`).
/// When the HF loop is inactive (boot logos, frontend menus, loading screens), expires after one
/// 30 Hz step period; when active, allows up to two step periods before falling back so transitioning
/// out of gameplay never leaves `g_step_in_flight` wedged.
bool StepInFlightAt(uint64_t effective_real, uint64_t step) {
  if (!g_step_in_flight.load(std::memory_order_relaxed)) {
    return false;
  }

  const uint64_t released = g_step_release_real.load(std::memory_order_relaxed);
  const uint64_t last_hf = g_last_hf_step_real.load(std::memory_order_relaxed);
  const bool hf_active =
      last_hf != 0u && effective_real >= last_hf && (effective_real - last_hf) <= (4u * step);
  const uint64_t expiry = hf_active ? (2u * step) : step;
  return effective_real < released || (effective_real - released) < expiry;
}

/// True while the host main thread is in its per-frame `_Process` read pass (`mode == -2`
/// until `mode == -3` or `refii_bridge_rex_advance_time_base`), auto-expiring after half an
/// HF step (`~16.7 ms`) so a stalled frame or exception never wedges deferred step release.
bool HostReadActiveAt(uint64_t effective_real, uint64_t step) {
  if (!g_host_read_active.load(std::memory_order_relaxed)) {
    return false;
  }

  const uint64_t started = g_host_read_start_real.load(std::memory_order_relaxed);
  const uint64_t expiry = step / 2u;
  return expiry != 0u && (effective_real < started || (effective_real - started) < expiry);
}

/// Releases at most one deferred HF step if none is currently in flight, the host read window is
/// inactive, and `g_virtual_ticks` remains within `kMaxLeadSteps` of `effective_real`. Caller must
/// hold `g_time_mutex`.
bool TryReleaseStepLocked(uint64_t effective_real, uint64_t step, uint64_t* ticks,
                          uint64_t* pending) {
  if (*pending == 0u || StepInFlightAt(effective_real, step) ||
      HostReadActiveAt(effective_real, step)) {
    return false;
  }

  const uint64_t ceiling = effective_real + (kMaxLeadSteps * step);
  if (*ticks + step > ceiling) {
    return false;
  }

  *ticks += step;
  --(*pending);
  g_step_release_real.store(effective_real, std::memory_order_relaxed);
  g_step_in_flight.store(true, std::memory_order_relaxed);
  return true;
}

/// Attempts to release one queued HF step from `g_pending_steps` into `g_virtual_ticks`.
/// Caller must hold `g_time_mutex`.
void ReleaseQueuedStepLocked(uint64_t effective_real, uint64_t step,
                             uint64_t* out_ticks = nullptr) {
  if (g_time_base_frozen.load(std::memory_order_relaxed)) {
    return;
  }

  uint64_t pending = g_pending_steps.load(std::memory_order_relaxed);
  uint64_t ticks =
      out_ticks != nullptr ? *out_ticks : g_virtual_ticks.load(std::memory_order_relaxed);
  if (pending == 0u || ticks == 0u) {
    return;
  }

  if (TryReleaseStepLocked(effective_real, step, &ticks, &pending)) {
    g_pending_steps.store(pending, std::memory_order_relaxed);
    g_virtual_ticks.store(ticks, std::memory_order_relaxed);
    if (out_ticks != nullptr) {
      *out_ticks = ticks;
    }
  }
}

}  // namespace

extern "C" void refii_bridge_rex_set_time_base_mode(int mode) {
  if (mode == -2) {
    const uint64_t real = ::rex::chrono::Clock::QueryGuestTickCount();
    std::lock_guard lock(g_time_mutex);
    g_host_read_start_real.store(EffectiveReal(real), std::memory_order_relaxed);
    g_host_read_active.store(true, std::memory_order_relaxed);
    return;
  }

  if (mode == -3) {
    const uint64_t real = ::rex::chrono::Clock::QueryGuestTickCount();
    const uint64_t step = HfStepTicks();
    std::lock_guard lock(g_time_mutex);
    g_host_read_active.store(false, std::memory_order_relaxed);
    if (g_time_base_mode.load(std::memory_order_relaxed) ==
            static_cast<int>(TimeBaseMode::Driven) &&
        step != 0u) {
      ReleaseQueuedStepLocked(EffectiveReal(real), step);
    }
    return;
  }

  if (mode < 0 || mode > 2) {
    return;
  }

  std::lock_guard lock(g_time_mutex);
  if (g_time_base_mode.load(std::memory_order_relaxed) == mode) {
    return;
  }

  // Re-seed on every change: carrying a virtual clock across a mode change would step the
  // guest's whole timeline at the moment of the switch.
  g_virtual_ticks.store(0u, std::memory_order_relaxed);
  g_pending_steps.store(0u, std::memory_order_relaxed);
  g_dropped_steps.store(0u, std::memory_order_relaxed);
  g_last_grant_real.store(0u, std::memory_order_relaxed);
  g_last_seen_real.store(0u, std::memory_order_relaxed);
  g_time_base_frozen.store(false, std::memory_order_relaxed);
  g_frozen_real_ticks.store(0u, std::memory_order_relaxed);
  g_freeze_start_real.store(0u, std::memory_order_relaxed);
  g_step_in_flight.store(false, std::memory_order_relaxed);
  g_step_release_real.store(0u, std::memory_order_relaxed);
  g_last_hf_step_real.store(0u, std::memory_order_relaxed);
  g_host_read_active.store(false, std::memory_order_relaxed);
  g_host_read_start_real.store(0u, std::memory_order_relaxed);
  g_in_lapse = false;
  g_time_base_mode.store(mode, std::memory_order_relaxed);
}

extern "C" int refii_bridge_rex_time_base_mode(void) {
  return g_time_base_mode.load(std::memory_order_relaxed);
}

extern "C" void refii_bridge_rex_time_base_stats(uint64_t* out_capped, uint64_t* out_granted,
                                                 uint64_t* out_watchdog) {
  if (out_capped != nullptr) {
    // Paced mode's held-back reads in the low half; driven mode's DROPPED grants in the high
    // half. A rising drop count means the guest cannot consume its granted rate.
    *out_capped = g_capped_reads.load(std::memory_order_relaxed) |
                  (g_dropped_steps.load(std::memory_order_relaxed) << 32);
  }

  if (out_granted != nullptr) {
    // Granted steps in the low half; the share of clock reads that had to take the lock in the
    // high half, as parts per thousand. A number near zero means the fast path is carrying the
    // traffic; a rising one means the guest is contending with the frame again.
    const uint64_t fast = g_fast_reads.load(std::memory_order_relaxed);
    const uint64_t locked = g_locked_reads.load(std::memory_order_relaxed);
    const uint64_t total = fast + locked;
    const uint64_t permille = total == 0u ? 0u : (locked * 1000u) / total;
    *out_granted = (g_granted_steps.load(std::memory_order_relaxed) & 0xFFFFFFFFULL) |
                   (permille << 32);
  }

  if (out_watchdog != nullptr) {
    // Bits [31:0]: watchdog lapses; bits [62:32]: unconsumed HF step depth (`pending + in_flight`);
    // bit [63]: whether an HF step is currently in flight on the guest thread.
    const uint64_t effective_real = EffectiveReal(::rex::chrono::Clock::QueryGuestTickCount());
    const uint64_t step = HfStepTicks();
    const uint64_t in_flight = (step != 0u && StepInFlightAt(effective_real, step)) ? 1u : 0u;
    const uint64_t unconsumed =
        (g_pending_steps.load(std::memory_order_relaxed) + in_flight) & 0x7FFFFFFFULL;
    *out_watchdog = (g_watchdog_lapses.load(std::memory_order_relaxed) & 0xFFFFFFFFULL) |
                    (unconsumed << 32) | (in_flight << 63);
  }
}

/// Grants the guest `steps` high-frequency steps of time. Driven mode only.
extern "C" void refii_bridge_rex_advance_time_base(uint32_t steps) {
  if (g_time_base_mode.load(std::memory_order_relaxed) !=
      static_cast<int>(TimeBaseMode::Driven) || steps == 0u) {
    return;
  }

  const uint64_t step = HfStepTicks();
  if (step == 0u) {
    return;
  }

  const uint64_t real = ::rex::chrono::Clock::QueryGuestTickCount();

  std::lock_guard lock(g_time_mutex);
  g_host_read_active.store(false, std::memory_order_relaxed);
  const uint64_t effective_real = EffectiveReal(real);
  uint64_t ticks = g_virtual_ticks.load(std::memory_order_relaxed);
  if (ticks == 0u) {
    ticks = effective_real;
    g_last_seen_real.store(effective_real, std::memory_order_relaxed);
  }

  // Exiting a watchdog lapse resumes normal grant queuing without jumping `g_virtual_ticks`
  // or discarding queued/newly granted steps.
  if (g_in_lapse) {
    g_last_seen_real.store(effective_real, std::memory_order_relaxed);
    g_in_lapse = false;
  }

  // Release at most ONE step now, and only if no previous step is still in flight; any
  // remaining steps stay in `g_pending_steps` and release one-by-one as each 30 Hz HF pass
  // completes (`refii_bridge_rex_note_guest_step`), so `0x82276FA4` never sees `owed > 1`.
  uint64_t pending = g_pending_steps.load(std::memory_order_relaxed) + steps;
  (void)TryReleaseStepLocked(effective_real, step, &ticks, &pending);

  if (pending > kMaxPendingSteps) {
    g_dropped_steps.fetch_add(pending - kMaxPendingSteps, std::memory_order_relaxed);
    pending = kMaxPendingSteps;
  }

  g_pending_steps.store(pending, std::memory_order_relaxed);
  g_virtual_ticks.store(ticks, std::memory_order_relaxed);
  g_last_grant_real.store(effective_real, std::memory_order_relaxed);
  g_granted_steps.fetch_add(steps, std::memory_order_relaxed);
}

extern "C" void refii_bridge_rex_note_guest_step(void) {
  const uint64_t real = ::rex::chrono::Clock::QueryGuestTickCount();
  const uint64_t effective_real = EffectiveReal(real);
  g_last_hf_step_real.store(effective_real, std::memory_order_relaxed);

  if (g_time_base_mode.load(std::memory_order_relaxed) !=
      static_cast<int>(TimeBaseMode::Driven)) {
    g_step_in_flight.store(false, std::memory_order_relaxed);
    return;
  }

  const uint64_t step = HfStepTicks();
  if (step == 0u) {
    g_step_in_flight.store(false, std::memory_order_relaxed);
    return;
  }

  std::lock_guard lock(g_time_mutex);
  g_step_in_flight.store(false, std::memory_order_relaxed);
  ReleaseQueuedStepLocked(EffectiveReal(::rex::chrono::Clock::QueryGuestTickCount()), step);
}

extern "C" void refii_bridge_rex_set_time_base_frozen(int frozen) {
  const bool want = frozen != 0;
  if (g_time_base_frozen.load(std::memory_order_relaxed) == want) {
    return;
  }

  const uint64_t real = ::rex::chrono::Clock::QueryGuestTickCount();
  std::lock_guard lock(g_time_mutex);
  if (g_time_base_frozen.exchange(want, std::memory_order_relaxed) == want) {
    return;
  }

  g_step_in_flight.store(false, std::memory_order_relaxed);
  g_step_release_real.store(0u, std::memory_order_relaxed);
  g_host_read_active.store(false, std::memory_order_relaxed);
  g_host_read_start_real.store(0u, std::memory_order_relaxed);
  if (want) {
    g_freeze_start_real.store(real, std::memory_order_relaxed);
    g_pending_steps.store(0u, std::memory_order_relaxed);
    g_in_lapse = false;
  } else {
    const uint64_t start = g_freeze_start_real.exchange(0u, std::memory_order_relaxed);
    if (start != 0u && real > start) {
      g_frozen_real_ticks.fetch_add(real - start, std::memory_order_relaxed);
    }
    const uint64_t effective_real = EffectiveReal(real);
    g_last_grant_real.store(effective_real, std::memory_order_relaxed);
    g_last_seen_real.store(effective_real, std::memory_order_relaxed);
    g_in_lapse = false;
  }
}

extern "C" void refii_bridge_rex_time_base_real_slice(int on) {
  if (on != 0) {
    t_real_slice_base = ::rex::chrono::Clock::QueryGuestTickCount();
    t_real_slice = true;
  } else {
    t_real_slice = false;
  }
}

extern "C" uint64_t refii_bridge_rex_query_guest_tick_count(void) {
  const uint64_t real = ::rex::chrono::Clock::QueryGuestTickCount();
  const int mode = g_time_base_mode.load(std::memory_order_relaxed);
  if (mode == static_cast<int>(TimeBaseMode::Off)) {
    return real;
  }

  // Inside a real-clock slice the virtual tick runs at the real rate for this thread only.
  // Never past the value the next grant would produce in practice (a slice is milliseconds,
  // a step is 33), and the reads after the slice fall back to the frozen tick, which the
  // guest loop's owed-step arithmetic never sees mid-slice.
  if (mode == static_cast<int>(TimeBaseMode::Driven) && t_real_slice) {
    const uint64_t current = g_virtual_ticks.load(std::memory_order_relaxed);
    if (current != 0u) {
      return current + (real > t_real_slice_base ? real - t_real_slice_base : 0u);
    }
  }

  const uint64_t step = HfStepTicks();
  if (step == 0u) {
    return real;
  }

  if (mode == static_cast<int>(TimeBaseMode::Paced)) {
    // POD, so first touch cannot allocate (this can run from a signal handler).
    static thread_local uint64_t last_real = 0;

    std::lock_guard lock(g_time_mutex);
    uint64_t ticks = g_virtual_ticks.load(std::memory_order_relaxed);
    if (ticks == 0u || last_real == 0u) {
      if (ticks == 0u) {
        ticks = real;
        g_virtual_ticks.store(ticks, std::memory_order_relaxed);
      }

      last_real = real;
      return ticks;
    }

    uint64_t advance = real > last_real ? real - last_real : 0u;
    if (advance > step) {
      advance = step;
      g_capped_reads.fetch_add(1u, std::memory_order_relaxed);
    }

    last_real = real;
    // Never past real time, and never backwards: a clock already ahead holds until real catches up.
    ticks = std::max(ticks, std::min(ticks + advance, real));

    g_virtual_ticks.store(ticks, std::memory_order_relaxed);
    return ticks;
  }

  // Driven.
  //
  // Fast path, no lock: unless a deferred grant is pending or the watchdog is due, the answer
  // is the current virtual tick. Reading `g_virtual_ticks` unlocked is safe: writers store a
  // complete value under the lock, so a racing reader sees the old tick or the new one.
  {
    const uint64_t effective_real = EffectiveReal(real);
    const uint64_t seen_grant = g_last_grant_real.load(std::memory_order_relaxed);
    const uint64_t current = g_virtual_ticks.load(std::memory_order_relaxed);
    const bool watchdog_due = WatchdogDue(effective_real, real, seen_grant, step);
    const bool drain_ready = g_pending_steps.load(std::memory_order_relaxed) != 0u &&
                             !StepInFlightAt(effective_real, step) &&
                             !HostReadActiveAt(effective_real, step);
    if (current != 0u && !watchdog_due && !drain_ready) {
      if (!g_time_base_frozen.load(std::memory_order_relaxed)) {
        g_last_seen_real.store(effective_real, std::memory_order_relaxed);
      }
#if !defined(__ANDROID__)
      // Diagnostics only, and a cache line every guest thread's clock read would write: not
      // on Android, where the guest is CPU-bound (the read share then reports zero).
      g_fast_reads.fetch_add(1u, std::memory_order_relaxed);
#endif
      return current;
    }
  }

#if !defined(__ANDROID__)
  g_locked_reads.fetch_add(1u, std::memory_order_relaxed);
#endif
  std::lock_guard lock(g_time_mutex);
  const uint64_t lock_raw = ::rex::chrono::Clock::QueryGuestTickCount();
  const uint64_t effective_real = EffectiveReal(lock_raw);
  uint64_t ticks = g_virtual_ticks.load(std::memory_order_relaxed);
  if (ticks == 0u) {
    ticks = effective_real;
    g_last_grant_real.store(effective_real, std::memory_order_relaxed);
    g_last_seen_real.store(effective_real, std::memory_order_relaxed);
    g_virtual_ticks.store(ticks, std::memory_order_relaxed);
    return ticks;
  }

  // Release deferred grants only once any in-flight step has finished and the host read window is
  // inactive, so mid-step internal `mftb` calls never advance `g_virtual_ticks` before
  // `0x82276FA4` observes the single step.
  ReleaseQueuedStepLocked(effective_real, step, &ticks);

  // Watchdog: with no grant for `kGrantWatchdogSteps`, time resumes at the real rate so guest
  // threads on timed waits are not frozen while the host is not stepping.
  const uint64_t last_grant = g_last_grant_real.load(std::memory_order_relaxed);
  if (WatchdogDue(effective_real, lock_raw, last_grant, step)) {
    const uint64_t seen = g_last_seen_real.load(std::memory_order_relaxed);
    if (effective_real > seen) {
      // THE CLOCK NEVER RUNS BACKWARDS. Releasing grants lets it lead real time by up to
      // `kMaxLeadSteps`; capping a lapse at `real` used to pull such a clock BACK by that lead,
      // up to 66 ms, the moment the host stalled for `kGrantWatchdogSteps`. The guest's own
      // fixed-step loops take time as monotonic — Fable 3's startup loop (sub_82312B80) turned a
      // 17 ms step backwards into a negative owed-step count and spun counting it down for
      // billions of steps, never reaching the next logo. A clock already ahead holds until real
      // time catches up.
      ticks = std::max(ticks, std::min(ticks + (effective_real - seen), effective_real));

      g_virtual_ticks.store(ticks, std::memory_order_relaxed);
    }

    if (!g_in_lapse) {
      g_in_lapse = true;
      g_watchdog_lapses.fetch_add(1u, std::memory_order_relaxed);
    }
  } else {
    g_in_lapse = false;
  }

  g_last_seen_real.store(effective_real, std::memory_order_relaxed);
  return ticks;
}

extern "C" void* refii_bridge_rex_resolve_indirect_function(uint32_t guest_address) {
  return reinterpret_cast<void*>(::rex::runtime::ResolveIndirectFunction(guest_address));
}

extern "C" int32_t refii_bridge_rex_has_function(uint32_t guest_address) {
  ::rex::Runtime* runtime = ::rex::Runtime::instance();
  auto* dispatcher = runtime != nullptr ? runtime->function_dispatcher() : nullptr;
  return dispatcher != nullptr && dispatcher->GetFunction(guest_address) != nullptr ? 1 : 0;
}

extern "C" uint32_t refii_bridge_rex_allocate_thunk(void* host_function, uint32_t caller_address) {
  ::rex::Runtime* runtime = ::rex::Runtime::instance();
  if (runtime == nullptr || host_function == nullptr) {
    return 0;
  }
  auto* dispatcher = runtime->function_dispatcher();
  if (dispatcher == nullptr) {
    return 0;
  }
  return dispatcher->AllocateThunk(reinterpret_cast<PPCFunc*>(host_function), caller_address);
}

extern "C" void refii_bridge_rex_increment_counter(uint32_t counter_id, int64_t delta) {
  ::rex::perf::IncrementCounter(static_cast<::rex::perf::CounterId>(counter_id), delta);
}

extern "C" uint32_t refii_bridge_rex_system_heap_alloc(void* memory, uint32_t size,
                                                       uint32_t alignment, uint32_t flags) {
  return static_cast<::rex::memory::Memory*>(memory)->SystemHeapAlloc(size, alignment, flags);
}

extern "C" void refii_bridge_rex_system_heap_free(void* memory, uint32_t address,
                                                  uint32_t* out_region_size) {
  static_cast<::rex::memory::Memory*>(memory)->SystemHeapFree(address, out_region_size);
}

extern "C" const void* refii_bridge_rex_lookup_heap(const void* memory, uint32_t address) {
  return static_cast<const ::rex::memory::Memory*>(memory)->LookupHeap(address);
}

extern "C" int32_t refii_bridge_rex_query_region_info(void* heap, uint32_t base_address,
                                                      void* out_info) {
  return static_cast<::rex::memory::BaseHeap*>(heap)->QueryRegionInfo(
             base_address, static_cast<::rex::memory::HeapAllocationInfo*>(out_info))
             ? 1
             : 0;
}

extern "C" void* refii_bridge_rex_mmio_global_handler(void) {
  return ::rex::runtime::MMIOHandler::global_handler();
}

extern "C" int32_t refii_bridge_rex_mmio_check_load(void* handler, uint32_t address,
                                                    uint32_t* out_value) {
  return static_cast<::rex::runtime::MMIOHandler*>(handler)->CheckLoad(address, out_value) ? 1
                                                                                           : 0;
}

extern "C" int32_t refii_bridge_rex_mmio_check_store(void* handler, uint32_t address,
                                                     uint32_t value) {
  return static_cast<::rex::runtime::MMIOHandler*>(handler)->CheckStore(address, value) ? 1 : 0;
}

extern "C" void* refii_bridge_rex_runtime_instance(void) { return ::rex::Runtime::instance(); }

extern "C" void* refii_bridge_rex_kernel_state(void) { return ::rex::system::kernel_state(); }

extern "C" void* refii_bridge_rex_xthread_current(void) {
  return ::rex::system::XThread::GetCurrentThread();
}

extern "C" int32_t refii_bridge_rex_xthread_is_in_thread(void) {
  return ::rex::system::XThread::IsInThread() ? 1 : 0;
}

extern "C" void refii_bridge_rex_xthread_set_last_error(uint32_t error_code) {
  ::rex::system::XThread::SetLastError(error_code);
}

extern "C" void refii_bridge_rex_xthread_enqueue_apc(void* thread, uint32_t normal_routine,
                                                     uint32_t normal_context, uint32_t arg1,
                                                     uint32_t arg2) {
  static_cast<::rex::system::XThread*>(thread)->EnqueueApc(normal_routine, normal_context, arg1,
                                                           arg2);
}

extern "C" void refii_bridge_rex_vd_query_video_mode(void* video_mode) {
  ::rex::kernel::xboxkrnl::VdQueryVideoMode(
      static_cast<::rex::system::X_VIDEO_MODE*>(video_mode));
}

extern "C" uint32_t refii_bridge_rex_nt_set_event(uint32_t handle, void* previous_state) {
  return ::rex::kernel::xboxkrnl::xeNtSetEvent(
      handle, static_cast<::rex::be<uint32_t>*>(previous_state));
}

extern "C" void* refii_bridge_rex_default_logger(void) { return ::rex::GetLogger().get(); }

extern "C" void refii_bridge_rex_logger_flush(void* logger) {
  if (logger != nullptr) {
    static_cast<spdlog::logger*>(logger)->flush();
  }
}

extern "C" void refii_bridge_rex_global_critical_region_lock(void) {
  ::rex::thread::global_critical_region::mutex().lock();
}

extern "C" int32_t refii_bridge_rex_global_critical_region_try_lock(void) {
  return ::rex::thread::global_critical_region::mutex().try_lock() ? 1 : 0;
}

extern "C" void refii_bridge_rex_global_critical_region_unlock(void) {
  ::rex::thread::global_critical_region::mutex().unlock();
}

// CVAR REGISTRATION FOR THE PAYLOAD. Its cvars have to reach the same registry the launcher
// and the config file drive, and rex::cvar::RegisterFlag takes a FlagEntry holding
// std::function and std::string -- neither of which survives a compiler boundary. The entry
// is therefore rebuilt HERE, on this side, from scalars and text, and its setter and getter
// close over the payload's own function pointers.
//
// The name is copied into the closures because the payload's `name` argument is only valid
// for the duration of this call.
extern "C" int32_t refii_bridge_rex_cvar_register(
    const char* name, uint32_t type, const char* category, const char* description,
    const char* default_value, uint32_t lifecycle, int32_t is_debug_only,
    int32_t (*setter)(const char* name, const char* value),
    size_t (*getter)(const char* name, char* out, size_t capacity)) {
  if (name == nullptr || setter == nullptr || getter == nullptr) {
    return 0;
  }

  std::string owned_name(name);

  ::rex::cvar::FlagEntry entry{};
  entry.name = owned_name;
  entry.type = static_cast<::rex::cvar::FlagType>(type);
  entry.category = category != nullptr ? category : "";
  entry.description = description != nullptr ? description : "";
  entry.setter = [setter, owned_name](std::string_view value) {
    // std::string_view is not NUL-terminated; the C side takes a C string.
    return setter(owned_name.c_str(), std::string(value).c_str()) != 0;
  };
  entry.getter = [getter, owned_name]() {
    return ::refii::bridge::TakeString([&](char* out, size_t capacity) {
      return getter(owned_name.c_str(), out, capacity);
    });
  };
  entry.command_callback = [](auto&&...) {};
  entry.lifecycle = static_cast<::rex::cvar::Lifecycle>(lifecycle);
  entry.default_value = default_value != nullptr ? default_value : "";
  entry.is_debug_only = is_debug_only != 0;

  return ::rex::cvar::RegisterFlag(std::move(entry)).has_value() ? 1 : 0;
}

extern "C" void refii_bridge_rex_cvar_unregister(const char* name) {
  if (name != nullptr) {
    ::rex::cvar::UnregisterFlag(name);
  }
}
