// refii — the guest tick queue: work the host posts onto the guest's own tick thread.
//
// SHARED STATE, IN refii_runtime, because the two ends of this queue live in different
// libraries. The HOST posts (on Godot's thread) and the PAYLOAD drains (the title's tick pump
// hook, on the guest's own tick thread). Both link refii_runtime, so this is the one place they
// can agree on one queue and one set of counters.
//
// The host reaches these as ordinary C++ symbols; both sides of that call are our build. The
// PAYLOAD reaches them through the C entries in core/payload_bridge.h, because its side of
// that call is the player's compiler — see that header for the contract.

#include <atomic>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#ifndef _WIN32
#include <signal.h>
#endif
#include <string>
#include <vector>

#include <rex/logging.h>

#include "core/payload_bridge.h"
#include "core/traced_mutex.h"

namespace refii::vfs {

/// An alternate signal stack for the calling thread, once. A fault on a thread whose stack has
/// overflowed cannot have a handler frame pushed onto it, so the kernel kills the process with
/// no handler run and no dmesg line. Guest threads are created without one; the tick pump and
/// the SwitchToThread hook call this so each gets its own.
extern "C" void refii_bridge_ensure_alternate_signal_stack() {
#ifndef _WIN32
  thread_local bool installed = false;
  if (installed) {
    return;
  }
  installed = true;
  stack_t current{};
  if (::sigaltstack(nullptr, &current) == 0 && (current.ss_flags & SS_DISABLE) == 0 &&
      current.ss_sp != nullptr) {
    return;  // .NET (or someone) already gave this thread one
  }
  constexpr std::size_t kAltStackBytes = 256u * 1024u;
  stack_t alt{};
  alt.ss_sp = std::malloc(kAltStackBytes);
  alt.ss_size = kAltStackBytes;
  if (alt.ss_sp != nullptr && ::sigaltstack(&alt, nullptr) != 0) {
    std::free(alt.ss_sp);
  }
#endif
}

namespace {

refii::TracedMutex g_mutex{"guest tick pump"};
std::vector<std::string> g_pending;

/// The queue's ceiling. Past it the OLDEST action is dropped and counted: a pump that is not
/// draining (front end down, guest stalled) must not turn every poll into unbounded memory.
constexpr size_t kMaxPending = 256;
std::atomic<uint64_t> g_dropped{0};

/// The coalescing key of a `Coop*` action, or empty for any other. Those actions are
/// idempotent STATE restated while it matters ("player two wears X"), so one per verb and
/// player is all the pump needs: `CoopHeroGive:2:<list>` keys as `CoopHeroGive:2`,
/// `CoopHeroBody:male` as `CoopHeroBody`. The key is the verb up to the first ':' plus the
/// next field when it is a player number (1 or 2).
std::string CoalesceKey(const std::string& name) {
  if (name.compare(0, 4, "Coop") != 0) {
    return std::string();
  }
  const size_t verb_end = name.find(':');
  if (verb_end == std::string::npos) {
    return name;
  }
  const size_t field_end = name.find(':', verb_end + 1);
  const std::string field = name.substr(verb_end + 1, field_end == std::string::npos
                                                          ? std::string::npos
                                                          : field_end - verb_end - 1);
  return field == "1" || field == "2" ? name.substr(0, verb_end + 1) + field
                                      : name.substr(0, verb_end);
}

/// Ticks observed, for the diagnostic in the pump. Relaxed: it is a counter nobody makes a
/// decision on, and making it ordered would put a barrier in a function that runs every frame.
std::atomic<uint64_t> g_ticks{0};

/// Actions taken off the queue. Separate from ticks so "the pump never ran" and "the pump ran
/// and the queue was always empty" are distinguishable — a single number cannot tell those
/// apart, and they need completely different fixes.
std::atomic<uint64_t> g_drained{0};

/// The drain's own scratch, NOT shared and NOT locked. Only the guest tick thread touches
/// these, between one begin_drain and its end_drain, which is why the queue above can be
/// swapped out from under the lock and then walked without holding it.
std::vector<std::string> g_taken;
std::vector<std::string> g_deferred;

/// Copies into the caller's buffer and returns the length that WOULD have been written, so a
/// caller can size a buffer with a null probe — the convention the whole payload ABI uses.
size_t WriteOut(const std::string& text, char* out, size_t capacity) {
  if (out != nullptr && capacity > 0) {
    const size_t copied = text.size() < capacity - 1 ? text.size() : capacity - 1;
    std::memcpy(out, text.data(), copied);
    out[copied] = '\0';
  }
  return text.size();
}

}  // namespace

/// Queues work to run on the guest's tick thread. Safe from any thread; returns the queue
/// depth after the post, so a caller can notice a pump that has stopped draining.
///
/// A `Coop*` action REPLACES the queued one with the same key (see CoalesceKey) in place —
/// latest text wins, order kept. The queue holds kMaxPending; past that the oldest is dropped.
uint32_t PostGuestTickAction(const char* name) {
  if (name == nullptr || name[0] == '\0') {
    return 0;
  }

  std::string action(name);
  const std::string key = CoalesceKey(action);
  std::lock_guard lock(g_mutex);
  if (!key.empty()) {
    for (std::string& queued : g_pending) {
      if (CoalesceKey(queued) == key) {
        queued = std::move(action);
        return static_cast<uint32_t>(g_pending.size());
      }
    }
  }
  while (g_pending.size() >= kMaxPending) {
    g_pending.erase(g_pending.begin());
    const uint64_t dropped = g_dropped.fetch_add(1, std::memory_order_relaxed) + 1;
    if ((dropped & (dropped - 1)) == 0) {  // 1, 2, 4, 8, ... so a stuck pump is said, not spammed
      REXLOG_INFO("Refii guest tick pump: queue full at {}, oldest action dropped ({} so far)",
                  kMaxPending, dropped);
    }
  }
  g_pending.push_back(std::move(action));
  return static_cast<uint32_t>(g_pending.size());
}

/// Ticks seen, actions drained and actions queued, for the host's diagnostics.
void GuestTickPumpCounters(uint64_t* out_ticks, uint64_t* out_drained, uint32_t* out_queued) {
  if (out_ticks != nullptr) {
    *out_ticks = g_ticks.load(std::memory_order_relaxed);
  }
  if (out_drained != nullptr) {
    *out_drained = g_drained.load(std::memory_order_relaxed);
  }
  if (out_queued != nullptr) {
    std::lock_guard lock(g_mutex);
    *out_queued = static_cast<uint32_t>(g_pending.size());
  }
}

namespace {
std::atomic<uint32_t> g_ui_data_generation{0};
std::atomic<bool> g_ui_data_dirty{false};
}  // namespace

extern "C" void refii_bridge_ui_data_changed(void) {
  g_ui_data_dirty.store(true, std::memory_order_release);
  g_ui_data_generation.fetch_add(1, std::memory_order_relaxed);
}

extern "C" int32_t refii_bridge_ui_data_take_dirty(void) {
  return g_ui_data_dirty.exchange(false, std::memory_order_acq_rel) ? 1 : 0;
}

extern "C" void refii_bridge_ui_data_published(void) {
  g_ui_data_generation.fetch_add(1, std::memory_order_relaxed);
}

extern "C" uint32_t refii_bridge_ui_data_generation(void) { return g_ui_data_generation.load(std::memory_order_relaxed); }

}  // namespace refii::vfs

// --- The payload's side. C only; see core/payload_bridge.h. --------------------------------

extern "C" {

uint64_t refii_bridge_guest_tick_pump_observe_tick(void) {
  return refii::vfs::g_ticks.fetch_add(1, std::memory_order_relaxed);
}

uint64_t refii_bridge_guest_tick_pump_drained(void) {
  return refii::vfs::g_drained.load(std::memory_order_relaxed);
}

namespace {
std::atomic<uint32_t> g_guest_frame{0};
}  // namespace

uint32_t refii_bridge_guest_frame(void) {
  return g_guest_frame.load(std::memory_order_acquire);
}

void refii_bridge_note_guest_frame(void) {
  g_guest_frame.fetch_add(1, std::memory_order_release);
}

namespace {
/// The last HF passes' (guest frame, step time) pairs, oldest first in ring order.
struct GuestStep {
  uint32_t frame;
  double time;
};
constexpr uint32_t kGuestSteps = 32u;
std::mutex g_step_lock;
GuestStep g_steps[kGuestSteps];
uint32_t g_step_next = 0u;
uint32_t g_step_count = 0u;
}  // namespace

void refii_bridge_note_guest_step_time(double time) {
  const uint32_t frame = g_guest_frame.load(std::memory_order_acquire);
  std::lock_guard<std::mutex> lock(g_step_lock);
  if (g_step_count != 0u) {
    const GuestStep& newest = g_steps[(g_step_next + kGuestSteps - 1u) % kGuestSteps];
    if (time <= newest.time || frame <= newest.frame) {
      // A clock or counter that went back is a new run of steps: forget the old ones.
      g_step_count = 0u;
    }
  }
  g_steps[g_step_next] = GuestStep{frame, time};
  g_step_next = (g_step_next + 1u) % kGuestSteps;
  if (g_step_count < kGuestSteps) {
    ++g_step_count;
  }
}

double refii_bridge_guest_frame_at(double time) {
  std::lock_guard<std::mutex> lock(g_step_lock);
  if (g_step_count == 0u || !(time == time)) {
    return -1.0;
  }
  const uint32_t oldest = (g_step_next + kGuestSteps - g_step_count) % kGuestSteps;
  const GuestStep* later = &g_steps[(g_step_next + kGuestSteps - 1u) % kGuestSteps];
  if (time >= later->time) {
    return static_cast<double>(later->frame);
  }
  for (uint32_t k = 1u; k < g_step_count; ++k) {
    const GuestStep* earlier = &g_steps[(g_step_next + kGuestSteps - 1u - k) % kGuestSteps];
    if (time >= earlier->time) {
      const double span = later->time - earlier->time;
      const double into = span > 0.0 ? (time - earlier->time) / span : 1.0;
      return static_cast<double>(earlier->frame) +
             into * static_cast<double>(later->frame - earlier->frame);
    }
    later = earlier;
  }
  return static_cast<double>(g_steps[oldest].frame);
}

void refii_bridge_guest_tick_pump_note_ran(void) {
  refii::vfs::g_drained.fetch_add(1, std::memory_order_relaxed);
}

uint32_t refii_bridge_guest_tick_pump_begin_drain(void) {
  refii::vfs::g_taken.clear();
  refii::vfs::g_deferred.clear();
  {
    std::lock_guard lock(refii::vfs::g_mutex);
    refii::vfs::g_taken.swap(refii::vfs::g_pending);
  }
  return static_cast<uint32_t>(refii::vfs::g_taken.size());
}

size_t refii_bridge_guest_tick_pump_taken_action(uint32_t index, char* out, size_t capacity) {
  if (index >= refii::vfs::g_taken.size()) {
    return 0;
  }
  return refii::vfs::WriteOut(refii::vfs::g_taken[index], out, capacity);
}

void refii_bridge_guest_tick_pump_defer_action(const char* name, size_t length) {
  if (name == nullptr || length == 0) {
    return;
  }
  refii::vfs::g_deferred.emplace_back(name, length);
}

void refii_bridge_guest_tick_pump_end_drain(void) {
  if (!refii::vfs::g_deferred.empty()) {
    std::lock_guard lock(refii::vfs::g_mutex);
    // Front of the queue: an action posted while this one was waiting must not overtake it.
    refii::vfs::g_pending.insert(refii::vfs::g_pending.begin(), refii::vfs::g_deferred.begin(),
                                 refii::vfs::g_deferred.end());
  }
  refii::vfs::g_taken.clear();
  refii::vfs::g_deferred.clear();
}
}  // extern "C"
