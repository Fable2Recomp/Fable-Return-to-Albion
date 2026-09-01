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
#ifndef _WIN32
#include <signal.h>
#endif
#include <string>
#include <vector>

#include <rex/kernel/xboxkrnl/threading.h>
#include <rex/logging.h>
#include <rex/system/xmemory.h>

#include "core/payload_bridge.h"
#include "core/hero_derived.h"

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

/// See FrontEndSubtitles().
std::atomic<int32_t> g_subtitles{-1};

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

/// The subtitles setting as the guest last stated it. -1 until the pump's first sample.
int32_t FrontEndSubtitles() { return g_subtitles.load(std::memory_order_relaxed); }

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

  // NEVER CACHE THIS HANDLE, AND NEVER SIGNAL ONE THE SCREEN DID NOT JUST NAME.
  // `screen+180` is a CreateEventA handle (0x8235CB58) that ~CLoadingScreen closes
  // (CloseHandle, 0x8235D10C), and ObjectTable::FindFreeSlot reuses freed slots
  // (`handle = kHandleBase + (slot << 2)`), so a handle held past its screen comes to name a
  // different kernel object. Measured in two core dumps: handles 0xF8000240 and 0xF8000144,
  // kept in a ring here and set every step, had become the XEvents backing the Kynapse world
  // lock (0x83498D14) and the VFS request-manager lock (0x42100018). Setting one releases a
  // critical-section waiter that does not own the lock, and RtlEnterCriticalSection aborts on
  // `owning_thread == 0` (xboxkrnl_rtl.cpp:405).
  const uint32_t screen = ResolveInGameLoadingScreen(base);

  static uint32_t watched = 0;
  static uint32_t stuck_steps = 0;

  // NOT gated on the world being up. The guest's game thread blocks on the loading
  // screen's fade-out, which only its present loop ticks — off in this headless build —
  // so the level change that would activate the world can never advance while the block
  // holds. Waiting for the world before breaking the wait is the deadlock, not a guard.
  // The stuck test below is the specificity: `sub_82361458` sets fade=1.0 (+172) and
  // shutdown requested (+176) and then blocks on the event (+180) until the render side
  // latches done (+177). Stuck = requested set, done not latched, 30 host steps.
  // `+176` ALONE. `+177` was in this test and must not be: it is write-once (only the ctor
  // `sub_8235CA90` clears it) and the screen object is reused between loads, so a `done` left
  // set by a previous teardown made every later load read as "not stuck" and never released.
  const bool stuck = screen != 0 && ReadU8(base, screen + 176u) != 0u;
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

  // The take-down `sub_8235E5A0` performs at 0x8235E7C4: clear requested, latch done, release
  // the game thread parked in `sub_82361458`'s WaitForSingleObjectEx(+180, INFINITE). Set
  // rather than pulse — the wait need not be entered yet, and an auto-reset event holds a set
  // where the guest's PulseEvent would drop it.
  const uint32_t event = ReadU32(base, screen + 180u);
  if (event != 0u) {
    rex::kernel::xboxkrnl::xeNtSetEvent(event, nullptr);
  }

  watched = 0;
  stuck_steps = 0;

  static std::atomic<uint32_t> logged{0};
  if (logged.fetch_add(1, std::memory_order_relaxed) < 8u) {
    REXLOG_INFO("Refii in-game loading screen finished from host step "
                "(screen=0x{:08X} event=0x{:08X})",
                screen, event);
  }
}

/// One spawned menu row, as `sub_826CE8D0` was handed it.
struct MenuRow {
  std::string name;
  std::string parent;

  // String2 — the already-localised display text, decorations included. UTF-16BE in the
  // guest; decoded at capture.
  std::string label;
  uint32_t id = 0;
  int32_t event_type = 0;

  // Star or pip rating from `sub_826CE230`, SpawnButton's ninth argument (first stack slot,
  // caller_SP+0x54). Bimodal by magnitude: below 100 a star slide (2 x the GDB "Rating", so
  // 0..10, 11 meaning empty); 100 and above a five-digit base-10 pip word, each digit 1 for an
  // empty augment slot and 2 for a filled one.
  int32_t rating = 0;
};

std::mutex g_menu_mutex;

/// The list being built by the population currently running.
std::vector<MenuRow> g_menu_building;

/// The last published copy — what the host reads.
std::vector<MenuRow> g_menu_rows;

/// Bumped on every row, so a host can tell "still the same list" from "read it again".
std::atomic<uint64_t> g_menu_generation{0};

/// True from `DeleteAllSpawnedMenuItems` until the next guest tick — the window in which the
/// capture is partial. There is no end marker: the in-game lists never call
/// `UpdateAfterSpawning`. A population runs to completion inside the GUI update that began it,
/// so the next guest tick is the first moment the list is whole; hence cleared from the tick,
/// not from a row.
std::atomic<bool> g_menu_populating{false};

// The hero's derived state, sampled on the guest's own thread. The logbook's numbers are
// accessor returns, not fields: morality is `sub_8223D320` (raw + GDB script constants +
// real-estate and worship terms), renown `sub_821C7D30` (field + modifier 10, clamped), and
// the personality bands at 0.375/0.625 name the wrong title if fed the raw field. The host
// bridge runs on Godot's thread and cannot call them; the pump is already on the guest tick
// thread, so it samples and stashes the answer here.

namespace {

std::mutex g_hero_mutex;
HeroDerived g_hero;

std::mutex g_region_mutex;
WorldMapRegion g_region;
std::atomic<uint32_t> g_region_wanted{0};

std::mutex g_quest_mutex;
QuestEntry g_quests[kMaxCompletedQuests];
uint32_t g_quest_count = 0;

}  // namespace

extern "C" void refii_bridge_publish_hero_derived(const void* sampled) {
  if (sampled == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_hero_mutex);
  g_hero = *static_cast<const HeroDerived*>(sampled);
}

extern "C" void refii_bridge_hero_derived_snapshot(void* out) {
  if (out == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_hero_mutex);
  *static_cast<HeroDerived*>(out) = g_hero;
}

void RequestWorldMapRegion(uint32_t index) { g_region_wanted.store(index); }

extern "C" uint32_t refii_bridge_requested_world_map_region() {
  return g_region_wanted.load();
}

extern "C" void refii_bridge_publish_world_map_region(const void* region) {
  if (region == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_region_mutex);
  g_region = *static_cast<const WorldMapRegion*>(region);
}

WorldMapRegion WorldMapRegionSnapshot() {
  std::lock_guard<std::mutex> lock(g_region_mutex);
  return g_region;
}

extern "C" void refii_bridge_publish_quests(const void* entries, uint32_t count) {
  const QuestEntry* typed = static_cast<const QuestEntry*>(entries);
  std::lock_guard<std::mutex> lock(g_quest_mutex);
  g_quest_count = count < kMaxCompletedQuests ? count : kMaxCompletedQuests;
  if (typed == nullptr) {
    g_quest_count = 0;
    return;
  }
  for (uint32_t i = 0; i < g_quest_count; ++i) {
    g_quests[i] = typed[i];
  }
}

uint32_t QuestSnapshot(QuestEntry* out, uint32_t capacity) {
  std::lock_guard<std::mutex> lock(g_quest_mutex);
  const uint32_t copied = capacity < g_quest_count ? capacity : g_quest_count;
  for (uint32_t i = 0; i < copied; ++i) {
    out[i] = g_quests[i];
  }

  // The total, not the copied count: a caller sizes its buffer from the first call.
  return g_quest_count;
}

uint32_t MenuRowLabel(uint32_t index, char* out_label, size_t label_capacity) {
  std::lock_guard<std::mutex> lock(g_menu_mutex);
  if (out_label != nullptr && label_capacity > 0) {
    out_label[0] = '\0';
    if (index < g_menu_rows.size()) {
      const std::string& label = g_menu_rows[index].label;
      const size_t take =
          label.size() < label_capacity - 1 ? label.size() : label_capacity - 1;
      std::memcpy(out_label, label.data(), take);
      out_label[take] = '\0';
    }
  }

  return static_cast<uint32_t>(g_menu_rows.size());
}

uint32_t MenuRows(uint32_t index, char* out_name, size_t name_capacity, char* out_parent,
                  size_t parent_capacity, uint32_t* out_id, int32_t* out_event_type,
                  uint64_t* out_generation, int32_t* out_rating) {
  std::lock_guard<std::mutex> lock(g_menu_mutex);
  if (out_generation != nullptr) {
    *out_generation = g_menu_generation.load(std::memory_order_relaxed);
  }

  if (index >= g_menu_rows.size()) {
    return static_cast<uint32_t>(g_menu_rows.size());
  }

  const MenuRow& row = g_menu_rows[index];
  if (out_name != nullptr && name_capacity > 0) {
    const size_t take = row.name.size() < name_capacity - 1 ? row.name.size() : name_capacity - 1;
    std::memcpy(out_name, row.name.data(), take);
    out_name[take] = '\0';
  }

  if (out_parent != nullptr && parent_capacity > 0) {
    const size_t take =
        row.parent.size() < parent_capacity - 1 ? row.parent.size() : parent_capacity - 1;
    std::memcpy(out_parent, row.parent.data(), take);
    out_parent[take] = '\0';
  }

  if (out_id != nullptr) {
    *out_id = row.id;
  }

  if (out_rating != nullptr) {
    *out_rating = row.rating;
  }

  if (out_event_type != nullptr) {
    *out_event_type = row.event_type;
  }

  return static_cast<uint32_t>(g_menu_rows.size());
}

}  // namespace refii::vfs

// --- The payload's side. C only; see core/payload_bridge.h. --------------------------------

extern "C" {

void refii_bridge_front_end_note_subtitles(int32_t state) {
  refii::vfs::g_subtitles.store(state, std::memory_order_relaxed);
}

void refii_bridge_menu_spawn_begin(void) {
  std::lock_guard<std::mutex> lock(refii::vfs::g_menu_mutex);
  refii::vfs::g_menu_building.clear();

  // The published copy goes too: `DeleteAllSpawnedMenuItems` runs before every repopulation
  // and publication is per row, so clearing only the builder leaves the host reading the
  // previous screen's population in full. Empty is the honest answer for that window — an
  // unnamed row falls back to the guest's own A press.
  refii::vfs::g_menu_rows.clear();
  refii::vfs::g_menu_generation.fetch_add(1, std::memory_order_relaxed);

  // Mark it partial from here: "empty" and "this screen has no rows" are otherwise the same
  // value, and a consumer would rebuild its model from a list it is about to be given again.
  refii::vfs::g_menu_populating.store(true, std::memory_order_release);
}

/// Clears the partial-population flag. Called once per guest tick.
void refii_bridge_menu_settle(void) {
  refii::vfs::g_menu_populating.store(false, std::memory_order_release);
}

/// True while a population is in flight and the captured rows are not yet whole.
int32_t refii_bridge_menu_populating(void) {
  return refii::vfs::g_menu_populating.load(std::memory_order_acquire) ? 1 : 0;
}

void refii_bridge_menu_spawn_row(const char* name, const char* parent, uint32_t id,
                                 int32_t event_type, int32_t rating) {
  refii_bridge_menu_spawn_row_labelled(name, parent, id, event_type, rating, nullptr);
}

void refii_bridge_menu_spawn_row_labelled(const char* name, const char* parent, uint32_t id,
                                          int32_t event_type, int32_t rating,
                                          const char* label) {
  refii::vfs::MenuRow row;
  row.name = name != nullptr ? name : "";
  row.parent = parent != nullptr ? parent : "";
  row.label = label != nullptr ? label : "";
  row.id = id;
  row.event_type = event_type;
  row.rating = rating;

  std::lock_guard<std::mutex> lock(refii::vfs::g_menu_mutex);

  // Ceiling: nothing tells the payload a population has finished, so a screen that spawns
  // without ever clearing would grow this without bound. The guest's pool is 26 buttons.
  if (refii::vfs::g_menu_building.size() < 104u) {
    refii::vfs::g_menu_building.push_back(std::move(row));
  }

  // Published per row, not at the end: the in-game lists never call `UpdateAfterSpawning`,
  // so there is no end marker to wait for.
  refii::vfs::g_menu_rows = refii::vfs::g_menu_building;
  refii::vfs::g_menu_generation.fetch_add(1, std::memory_order_relaxed);
}

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
