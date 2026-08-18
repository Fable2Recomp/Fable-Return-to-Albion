// refii — the guest tick pump's SHARED STATE.
//
// THIS FILE MUST BE IN librefii_vfs, NOT IN THE PPC HOOK SET, for exactly the reason
// bink_frame_state.cpp is: the two ends of this queue live in different libraries. The HOST
// posts (RefiiHostPostFrontEndAction, on Godot's thread) and the PAYLOAD drains (the pump
// hook in src/hooks/guest_tick_pump.cpp, on the guest's own tick thread). Both link
// refii_vfs, so this is the one place they can agree on one queue and one set of counters.
//
// IT USED TO LIVE BESIDE THE PUMP, which left the host bridge naming five symbols only the
// payload defined. That resolves on Linux and cannot resolve on Windows, and the difference
// is not a build flag: the payload is dlopen'd RTLD_GLOBAL, so an undefined reference in
// librefii_hostbridge.so binds to it at load time, while a Windows DLL is linked closed and
// has no global scope to fall back on. lld-link says so as five undefined symbols. Linux was
// never right either — refii_payload/payload.h states that NOTHING WE SHIP MAY LINK AGAINST
// THE PAYLOAD, because the payload is built from the player's own game image and is absent
// on a build machine. Windows is just the platform that enforces it.
//
// The host reaches these as ordinary C++ symbols; both sides of that call are our build. The
// PAYLOAD reaches them through the C entries in core/payload_bridge.h, because its side of
// that call is the player's compiler — see that header for the contract.

#include <atomic>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#include <rex/kernel/xboxkrnl/threading.h>
#include <rex/logging.h>
#include <rex/system/xmemory.h>

#include "core/payload_bridge.h"

namespace refii::vfs {

namespace {

std::mutex g_mutex;
std::vector<std::string> g_pending;

/// Ticks observed, for the diagnostic in the pump. Relaxed: it is a counter nobody makes a
/// decision on, and making it ordered would put a barrier in a function that runs every frame.
std::atomic<uint64_t> g_ticks{0};

/// Actions taken off the queue. Separate from ticks so "the pump never ran" and "the pump ran
/// and the queue was always empty" are distinguishable — a single number cannot tell those
/// apart, and they need completely different fixes.
std::atomic<uint64_t> g_drained{0};

/// The saved-game count, sampled by the payload calling the guest's own probe on the tick
/// thread.
///
/// -1 means "not asked yet" and is NOT the same as 0. A host that cannot tell them apart draws
/// a menu with Load Game missing and no way to know whether that is the answer or the absence
/// of one — which is exactly the bug this replaces, where an empty vector stood in for both.
std::atomic<int32_t> g_save_count{-1};

/// Set to ask for a fresh sample. Starts true so the first front-end tick takes one, and is set
/// again whenever the host knows the answer has changed — after a game is started, say.
///
/// Sampled rather than polled every tick because the probe is not free: it allocates a 46 320
/// byte guest stack frame and walks up to 150 content entries. The guest itself asks exactly
/// once, at menu spawn.
std::atomic<bool> g_resample{true};

/// The drain's own scratch, NOT shared and NOT locked. Only the guest tick thread touches
/// these, between one begin_drain and its end_drain, which is why the queue above can be
/// swapped out from under the lock and then walked without holding it.
std::vector<std::string> g_taken;
std::vector<std::string> g_deferred;

/// Guest memory, big-endian, for the loading-screen finisher below.
///
/// Local copies rather than core/guest_state.h's, which are defined in
/// internal_state_guest.cpp — a PAYLOAD-only translation unit that refii_vfs does not
/// compile and must not import from. They are the same plain loads and stores; the payload's
/// StoreGuestU8 carries one extra guard, for the synthetic startup frame's failure byte at
/// frame+244, and that is not an address this file writes.
uint8_t ReadU8(uint8_t* base, uint32_t at) {
  return *rex::memory::GuestPtr<uint8_t*>(base, at);
}

uint32_t ReadU32(uint8_t* base, uint32_t at) {
  const auto* ptr = rex::memory::GuestPtr<uint8_t*>(base, at);
  return (uint32_t(ptr[0]) << 24) | (uint32_t(ptr[1]) << 16) | (uint32_t(ptr[2]) << 8) |
         uint32_t(ptr[3]);
}

void WriteU8(uint8_t* base, uint32_t at, uint8_t value) {
  *rex::memory::GuestPtr<uint8_t*>(base, at) = value;
}

void WriteU32(uint8_t* base, uint32_t at, uint32_t value) {
  auto* ptr = rex::memory::GuestPtr<uint8_t*>(base, at);
  ptr[0] = static_cast<uint8_t>((value >> 24) & 0xFF);
  ptr[1] = static_cast<uint8_t>((value >> 16) & 0xFF);
  ptr[2] = static_cast<uint8_t>((value >> 8) & 0xFF);
  ptr[3] = static_cast<uint8_t>(value & 0xFF);
}

bool IsGuestPointer(uint32_t value) { return value >= 0x10000 && value < 0x90000000; }

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
uint32_t PostGuestTickAction(const char* name) {
  if (name == nullptr || name[0] == '\0') {
    return 0;
  }

  std::lock_guard<std::mutex> lock(g_mutex);
  g_pending.emplace_back(name);
  return static_cast<uint32_t>(g_pending.size());
}

/// Ticks seen and actions drained, for the host's diagnostics.
void GuestTickPumpCounters(uint64_t* out_ticks, uint64_t* out_drained, uint32_t* out_queued) {
  if (out_ticks != nullptr) {
    *out_ticks = g_ticks.load(std::memory_order_relaxed);
  }
  if (out_drained != nullptr) {
    *out_drained = g_drained.load(std::memory_order_relaxed);
  }
  if (out_queued != nullptr) {
    std::lock_guard<std::mutex> lock(g_mutex);
    *out_queued = static_cast<uint32_t>(g_pending.size());
  }
}

/// The saved-game count as last sampled, or -1 if it has not been sampled yet.
int32_t GuestSavedGameCount() { return g_save_count.load(std::memory_order_relaxed); }

/// Asks for a fresh sample on the next front-end tick. Safe from any thread.
void RefreshGuestSavedGameCount() { g_resample.store(true, std::memory_order_relaxed); }

/// The in-game loading screen the guest stood up, or 0 when none is up.
///
/// Same three-root walk the host bridge's `loading_screen_object` uses — `sub_82388380`'s own
/// fallback chain (0x83496AB8, then 0x83496AC0, then 0x8335C4C4) -> app -> +8 context -> +36
/// manager -> +4 screen. `manager+4` is 0 when there is no screen, which is the ordinary case
/// out of game and during the front end.
///
/// Here rather than in the pump because BOTH sides walk it: the payload's own finisher on the
/// in-game tick and the host's step finisher below. One copy, so they cannot disagree about
/// which object is the screen.
uint32_t ResolveInGameLoadingScreen(uint8_t* base) {
  if (base == nullptr) {
    return 0;
  }
  for (uint32_t root : {0x83496AB8u, 0x83496AC0u, 0x8335C4C4u}) {
    const uint32_t app = ReadU32(base, root);
    if (!IsGuestPointer(app)) continue;
    const uint32_t owner = ReadU32(base, app + 8u);
    if (!IsGuestPointer(owner)) continue;
    const uint32_t manager = ReadU32(base, owner + 36u);
    if (!IsGuestPointer(manager)) continue;
    const uint32_t screen = ReadU32(base, manager + 4u);
    if (IsGuestPointer(screen)) return screen;
  }
  return 0;
}

void FinishStuckInGameLoadingScreenFromHost(uint8_t* base) {
  if (base == nullptr) {
    return;
  }

  static const bool enabled = [] {
    const char* value = std::getenv("REFII_FINISH_INGAME_LOADSCREEN");
    return value == nullptr || value[0] != '0';
  }();
  if (!enabled) {
    return;
  }

  // THE PRESENT GATE, released EVERY step the guest is past the boot. The world update's
  // frame construct (sub_82B4F400) waits on the loading screen's take-down event on every
  // present; the render tick that signals it is off in this headless build, so a single
  // pulse is consumed by the first waiter and the next frame blocks forever. The event is
  // auto-reset, so signalling it per step can only release a waiter, never stack.
  //
  // GATED ON PAST-THE-BOOT, NOT in-game: the New Game flow's loading screen (sub_82361458,
  // reached from the frontend loop sub_82319BE8) blocks on this event while the guest is
  // still in the frontend — gating on byte_83496A5E starved exactly that phase. The frontend
  // flag (0x83496AC4) is clear through the boot movies, so neither flag alone fires there.
  //
  // THE HANDLE OUTLIVES THE SCREEN, and MORE THAN ONE is live at once. The teardown clears
  // the loading-screen chain while the present gate keeps waiting on the SAME event, so
  // gating the signal on the screen resolving starves it — and a SINGLE remembered handle is
  // wrong too: the frame object's wait handle is the PREVIOUS screen's event, so the newest
  // screen's +180 overwrites the one the world update actually waits on (measured: the
  // thread parked on 0xF8000188 while the finisher signalled 0xF80000A4). Keep a small ring
  // and signal every entry; a stale handle is a no-op (INVALID_HANDLE).
  static uint32_t event_ring[4] = {0, 0, 0, 0};
  static uint32_t ring_count = 0;
  const uint32_t screen = ResolveInGameLoadingScreen(base);
  if (screen != 0) {
    const uint32_t event = ReadU32(base, screen + 180u);
    if (event != 0u) {
      bool known = false;
      for (uint32_t i = 0; i < ring_count; ++i) {
        if (event_ring[i] == event) {
          known = true;
          break;
        }
      }
      if (!known && ring_count < 4u) {
        event_ring[ring_count++] = event;
      }
    }
  }
  const bool past_boot = ReadU8(base, 0x83496AC4u) != 0u || ReadU8(base, 0x83496A5Eu) != 0u;
  if (past_boot) {
    for (uint32_t i = 0; i < ring_count; ++i) {
      rex::kernel::xboxkrnl::xeNtSetEvent(event_ring[i], nullptr);
    }
  }

  static uint32_t watched = 0;
  static uint32_t stuck_steps = 0;

  // NOT gated on the world being up. The guest's game thread blocks on the loading
  // screen's fade-out, which only its present loop ticks — off in this headless build —
  // so the level change that would activate the world can never advance while the block
  // holds. Waiting for the world before breaking the wait is the deadlock, not a guard.
  // The stuck test below is the specificity: `sub_82361458` sets fade=1.0 (+172) and
  // shutdown requested (+176) and then blocks on the event (+180) until the render side
  // latches done (+177). Stuck = requested set, done not latched, 30 host steps.
  const bool stuck =
      screen != 0 && ReadU8(base, screen + 176u) != 0u && ReadU8(base, screen + 177u) == 0u;
  if (!stuck) {
    watched = 0;
    stuck_steps = 0;
    return;
  }

  if (screen != watched) {
    watched = screen;
    stuck_steps = 0;
  }

  constexpr uint32_t kStuckSteps = 30u;
  if (stuck_steps < kStuckSteps) {
    ++stuck_steps;
    return;
  }

  if (ReadU32(base, screen + 168u) < 10u) {
    WriteU32(base, screen + 168u, 10u);
  }
  WriteU32(base, screen + 172u, 0u);
  WriteU8(base, screen + 176u, 0u);
  WriteU8(base, screen + 177u, 1u);

  watched = 0;
  stuck_steps = 0;

  static std::atomic<uint32_t> logged{0};
  if (logged.fetch_add(1, std::memory_order_relaxed) < 8u) {
    REXLOG_INFO("Refii in-game loading screen finished from host step "
                "(screen=0x{:08X} event=0x{:08X})",
                screen, ReadU32(base, screen + 180u));
  }
}

}  // namespace refii::vfs

// --- The payload's side. C only; see core/payload_bridge.h. --------------------------------

extern "C" {

uint64_t refii_bridge_guest_tick_pump_observe_tick(void) {
  return refii::vfs::g_ticks.fetch_add(1, std::memory_order_relaxed);
}

uint64_t refii_bridge_guest_tick_pump_drained(void) {
  return refii::vfs::g_drained.load(std::memory_order_relaxed);
}

void refii_bridge_guest_tick_pump_note_ran(void) {
  refii::vfs::g_drained.fetch_add(1, std::memory_order_relaxed);
}

uint32_t refii_bridge_guest_tick_pump_begin_drain(void) {
  refii::vfs::g_taken.clear();
  refii::vfs::g_deferred.clear();
  {
    std::lock_guard<std::mutex> lock(refii::vfs::g_mutex);
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
    std::lock_guard<std::mutex> lock(refii::vfs::g_mutex);
    // Front of the queue: an action posted while this one was waiting must not overtake it.
    refii::vfs::g_pending.insert(refii::vfs::g_pending.begin(), refii::vfs::g_deferred.begin(),
                                 refii::vfs::g_deferred.end());
  }
  refii::vfs::g_taken.clear();
  refii::vfs::g_deferred.clear();
}

int32_t refii_bridge_guest_tick_pump_wants_save_count(void) {
  return refii::vfs::g_resample.load(std::memory_order_relaxed) ? 1 : 0;
}

void refii_bridge_guest_tick_pump_clear_save_count_request(void) {
  refii::vfs::g_resample.store(false, std::memory_order_relaxed);
}

void refii_bridge_guest_tick_pump_note_save_count(int32_t count) {
  refii::vfs::g_save_count.store(count, std::memory_order_relaxed);
}

void refii_bridge_guest_tick_pump_request_save_count(void) {
  refii::vfs::RefreshGuestSavedGameCount();
}

uint32_t refii_bridge_resolve_in_game_loading_screen(uint8_t* base) {
  return refii::vfs::ResolveInGameLoadingScreen(base);
}

}  // extern "C"
