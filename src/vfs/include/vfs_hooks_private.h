#pragma once

#include "loose_asset_hooks.h"
#include "banks/hash_bank_runtime.h"
#include "banks/manifest_resolution.h"
#include "banks/synthetic_bank_runtime.h"
#include "core/guest_state.h"
#include "core/host_asset_reader.h"
#include "frontend/loading_sequence.h"
#include "frontend/post_gameface.h"
#include "frontend/post_loading.h"
#include "frontend/post_video.h"
#include "frontend/texture_handoff.h"
#include "index/asset_aliases.h"
#include "index/concrete_asset_resolution.h"
#include "index/open_pipeline.h"
#include "index/ownership.h"
#include "index/resource_hash_index.h"
#include "index/resource_hash_resolution.h"
#include "index/runtime_alias_cache.h"
#include "index/runtime_tables.h"
#include "resources/resource_wrappers.h"
#include "resources/texture_tracing.h"
#include "streams/host_streams.h"
#include "streams/resource_callbacks.h"
#include "streams/resource_wrapper_handlers.h"
#include "vfs_hook_constants.h"
#include "vfs_hook_types.h"
#include "core/native_lua_hooks.h"
#include "hooks/ppc_externs.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <cstdint>
#include <deque>
#include <filesystem>
#include <fstream>
#include <future>
#include <initializer_list>
#include <limits>
#include <memory>
#include <mutex>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

#include <fmt/format.h>
#include <rex/logging.h>
#include <rex/ppc.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>
#include <rex/system/xtypes.h>
#include <tracy/Tracy.hpp>

#include "core/payload_bridge.h"

#if !defined(REX_FUNC_PROLOGUE)
#define REX_FUNC_PROLOGUE() ((void)0)
#endif

// Logs each vtable call the guest makes on a stream object we handed it, keyed by
// (object, slot). `slot` is the vtable byte offset dispatched through, or -1 if unknown.
inline void NoteRefiiStreamMethod(std::string_view api,
                                  uint32_t stream_guest,
                                  int32_t slot = -1) {
  static std::mutex mutex;
  static std::unordered_map<uint64_t, uint32_t> seen;
  const uint64_t key =
      (static_cast<uint64_t>(stream_guest) << 32) |
      static_cast<uint32_t>(slot);
  uint32_t nth = 0;
  {
    std::lock_guard<std::mutex> lock(mutex);
    nth = ++seen[key];
    if (seen.size() > 4096u) {
      seen.clear();
    }
  }

  // First four calls, then powers of two, so the count stays readable as a frequency.
  if (nth <= 4u || (nth & (nth - 1u)) == 0u) {
    if (slot >= 0) {
      REXLOG_INFO("Refii stream 0x{:08X}: guest called slot +{} ({}) — call #{}",
                  stream_guest, slot, api, nth);
    } else {
      REXLOG_INFO("Refii stream 0x{:08X}: guest called {} — call #{}",
                  stream_guest, api, nth);
    }
  }
}

/// Logs a stream open: the object handed back and the vtable it advertises, which decides
/// every later dispatch.
inline void NoteRefiiStreamOpened(std::string_view api,
                                  uint32_t stream_guest,
                                  uint32_t vtable_guest,
                                  uint64_t size,
                                  std::string_view asset,
                                  uint32_t caller_lr) {
  static std::atomic<uint32_t> opened{0};
  const uint32_t nth = opened.fetch_add(1, std::memory_order_relaxed) + 1u;
  if (nth <= 32u || (nth & (nth - 1u)) == 0u) {
    REXLOG_INFO("Refii stream OPENED #{} api={} object=0x{:08X} vtable=0x{:08X} size={} "
                "caller_lr=0x{:08X} asset={}",
                nth, api, stream_guest, vtable_guest, size, caller_lr, asset);
  }
}

// Runs the guest's own function and returns; the hook body below it is not reached.
#define REFII_VFS_NATIVE_PASSTHROUGH(function_name) \
  do {                                              \
    __imp__##function_name(ctx, base);              \
    return;                                         \
  } while (false)

namespace refii::vfs {
// Bink video frame capture — defined in src/core/bink_frame_state.cpp, which links into
// librefii_vfs. These two hooks compile into the payload instead, so they cross to it
// through the C entries in core/payload_bridge.h rather than by C++ symbol.
inline void NoteBinkMovieOpen(uint32_t player, uint8_t* base_for_open = nullptr) {
  refii_bridge_note_bink_movie_open(player, base_for_open);
}

/// The guest is closing this BINK object. Its caller frees the player immediately afterwards,
/// so the host must stop reading through it — see the note in `refii_bridge_note_bink_movie_closed`.
inline void NoteBinkMovieClosed(uint32_t player) {
  refii_bridge_note_bink_movie_closed(player);
}

/// The guest stated this player's destination rect (`sub_82A41E30`), in back-buffer pixels.
inline void NoteBinkMovieRect(uint32_t player, int32_t left, int32_t top, int32_t right,
                              int32_t bottom) {
  refii_bridge_note_bink_movie_rect(player, left, top, right, bottom);
}
uint32_t BinkFrameBuffersGuestAddress();

/// The movie the guest last handed to BinkOpen. The argument is EITHER a pointer to the
/// already-loaded .bik in guest memory OR a pointer to its path — the title opens the
/// startup logos from a preloaded buffer and later movies by name.
inline void NoteBinkVideoRequested(uint8_t* base, uint32_t data_guest) {
  refii_bridge_note_bink_video_requested(base, data_guest);
}
void RequestedBinkVideo(uint32_t* out_address, uint32_t* out_size,
                        uint32_t* out_generation, uint32_t* out_swapped);

/// The path form of the same request. Empty when the last request carried a buffer.
/// Writes at most `capacity` bytes including the terminator; returns the length written.
uint32_t RequestedBinkVideoName(char* out, uint32_t capacity);

/// Marks the live movie finished, so the guest's startup sequence advances in step with
/// what the host has actually shown. Returns the player retired, or 0.
uint32_t FinishCurrentBinkMovie(uint8_t* base);

/// 0 no movie, 1 the guest still wants it, 2 the guest has ended it (done or skipped).
uint32_t BinkMovieState(uint8_t* base);

/// The destination rect the guest stated for the live movie (bink+8/+12/+16/+20).
uint32_t BinkMovieDestRect(uint8_t* base, float* out_x0, float* out_y0,
                           float* out_x1, float* out_y1);

/// Brings a stuck in-game loading screen down from the host step thread — defined in
/// src/core/guest_tick_pump_state.cpp. Runs when the guest game thread is parked in its own
/// post-cutscene teardown wait and the pump can no longer fire.
void FinishStuckInGameLoadingScreenFromHost(uint8_t* base);

/// The subtitles setting the pump last sampled from the guest: 1 on, 0 off, -1 unknown.
int32_t FrontEndSubtitles();

/// Which pause tabs the guest's own predicate said it would offer, sampled on the GUI tick.
/// Bit N = tab N available, N in 2..8; 0 means it has not been sampled yet.
uint32_t PauseTabsMask();

// The hero's derived state lives in core/hero_derived.h.

/// One captured menu row; returns the TOTAL row count. Every out parameter is optional;
/// `out_generation` is bumped on every spawn.
uint32_t MenuRowLabel(uint32_t index, char* out_label, size_t label_capacity);

/// The screen the published capture belongs to, or 0xFFFFFFFF for none.
uint32_t MenuScreenId();

/// The in-game GUI's roots, or false when no in-game screen is up. Defined in
/// hooks/guest_tick_pump.cpp; shared so a menu boundary in another TU can key its population by
/// the screen the guest states rather than publishing it as belonging to none.
bool InGameScreenRoots(uint8_t* base, uint32_t* out_owner, uint32_t* out_screen_id);
uint32_t MenuRows(uint32_t index, char* out_name, size_t name_capacity, char* out_parent,
                  size_t parent_capacity, uint32_t* out_id, int32_t* out_event_type,
                  uint64_t* out_generation, int32_t* out_rating);

// The guest tick pump's shared state — defined in src/core/guest_tick_pump_state.cpp, which
// links into librefii_vfs. The HOST posts and the PAYLOAD drains, so the queue cannot live
// beside the pump hook; see that file. The pump compiles into the payload, so it crosses
// through the C entries in core/payload_bridge.h rather than by C++ symbol.

/// The in-game loading screen the guest stood up, or 0 when none is up.
inline uint32_t ResolveInGameLoadingScreen(uint8_t* base) {
  return refii_bridge_resolve_in_game_loading_screen(base);
}

/// Ticks observed BEFORE this one, so the pump's first tick reads 0.
inline uint64_t GuestTickPumpObserveTick() {
  return refii_bridge_guest_tick_pump_observe_tick();
}
inline uint64_t GuestTickPumpDrained() { return refii_bridge_guest_tick_pump_drained(); }

/// Gives the calling thread an alternate signal stack, once (guest_tick_pump_state.cpp).
inline void EnsureAlternateSignalStack() {
  refii_bridge_ensure_alternate_signal_stack();
}

/// Lifetime for Raise Dead's creation effect, which the guest spawns and never kills (see
/// hooks/spell_fx_lifetime.cpp). The orientation hook in particle_transform.cpp notes the
/// PSInstance it is passed while a watched effect is being created; the guest-tick sweep fades
/// and then destroys what is due.
void NoteSpellFxInstance(uint32_t instance);
void SweepOrphanSpellFx(PPCContext& ctx, uint8_t* base);

// The effect transforms and the targeting highlight the guest states — defined in
// src/core/stated_fx_state.cpp and src/core/target_highlight_state.cpp, which link into
// librefii_vfs. The PAYLOAD writes and the HOST BRIDGE reads, so the tables cannot live beside
// the hooks that fill them; see those files. The hooks compile into the payload, so they cross
// through the C entries in core/payload_bridge.h rather than by C++ symbol.

/// One effect's stated world position: all four lanes of the setter's vector register.
inline void NoteFxPosition(uint32_t instance, const float* lanes) {
  refii_bridge_note_fx_position(instance, lanes);
}

/// Its stated orientation: the quaternion's four register halves, high word first.
inline void NoteFxOrientation(uint32_t instance, const uint32_t* lanes) {
  refii_bridge_note_fx_orientation(instance, lanes);
}

/// The colour stated for one renderable component, packed big-endian with R in the top byte.
inline void NoteHighlightColour(uint32_t component, uint32_t rgba) {
  refii_bridge_note_highlight_colour(component, rgba);
}

/// An entity became or stopped being a target. Counted only.
inline void NoteHighlightTarget() { refii_bridge_note_highlight_target(); }

/// One renderable's appearance as the guest just stated it; `words` per core/payload_bridge.h.
inline void NoteAppearance(uint32_t component, const uint32_t* words) {
  refii_bridge_note_appearance(component, words);
}

/// The renderable is being constructed or destroyed: whatever was stated for its address is
/// not its own.
inline void ForgetAppearance(uint32_t component) { refii_bridge_forget_appearance(component); }
inline void GuestTickPumpNoteRan() { refii_bridge_guest_tick_pump_note_ran(); }

/// Records a debug-call/debug-lua outcome where the host's RefiiHostGetDebugCallResult reads it.
inline void NoteDebugCallResult(uint32_t addr, uint64_t r3, const std::string& note) {
  refii_bridge_guest_tick_pump_note_result(addr, r3, note.c_str(), note.size());
}

/// Takes the whole queue aside and answers how many actions came with it.
inline uint32_t GuestTickPumpBeginDrain() {
  return refii_bridge_guest_tick_pump_begin_drain();
}

/// One action from that batch. Measured first, then filled: the name is the front end's own
/// identity for a menu entry and nothing bounds its length, so a fixed buffer would be a
/// silent truncation waiting for a long one.
inline std::string GuestTickPumpTakenAction(uint32_t index) {
  const size_t needed = refii_bridge_guest_tick_pump_taken_action(index, nullptr, 0);
  std::string name(needed, '\0');
  if (needed != 0) {
    refii_bridge_guest_tick_pump_taken_action(index, name.data(), needed + 1);
  }
  return name;
}

/// Holds an action that could not run yet. End puts every deferred one back at the front.
inline void GuestTickPumpDeferAction(const std::string& name) {
  refii_bridge_guest_tick_pump_defer_action(name.c_str(), name.size());
}
inline void GuestTickPumpEndDrain() { refii_bridge_guest_tick_pump_end_drain(); }

inline bool GuestTickPumpWantsSaveCount() {
  return refii_bridge_guest_tick_pump_wants_save_count() != 0;
}
inline void GuestTickPumpClearSaveCountRequest() {
  refii_bridge_guest_tick_pump_clear_save_count_request();
}
inline void GuestTickPumpNoteSaveCount(int32_t count) {
  refii_bridge_guest_tick_pump_note_save_count(count);
}
inline void GuestTickPumpRequestSaveCount() {
  refii_bridge_guest_tick_pump_request_save_count();
}
}  // namespace refii::vfs

