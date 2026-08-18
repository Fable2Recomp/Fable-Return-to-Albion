#pragma once

#include "loose_asset_hooks.h"
#include "banks/hash_bank_runtime.h"
#include "banks/manifest_resolution.h"
#include "banks/synthetic_bank_runtime.h"
#include "core/guest_state.h"
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

namespace refii::vfs {
// Bink video frame capture — defined in src/core/bink_frame_state.cpp, which links into
// librefii_vfs. These two hooks compile into the payload instead, so they cross to it
// through the C entries in core/payload_bridge.h rather than by C++ symbol.
inline void NoteBinkMovieOpen(uint32_t player, uint8_t* base_for_open = nullptr) {
  refii_bridge_note_bink_movie_open(player, base_for_open);
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

/// Brings a stuck in-game loading screen down from the host step thread — defined in
/// src/core/guest_tick_pump_state.cpp. Runs when the guest game thread is parked in its own
/// post-cutscene teardown wait and the pump can no longer fire.
void FinishStuckInGameLoadingScreenFromHost(uint8_t* base);

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
inline void GuestTickPumpNoteRan() { refii_bridge_guest_tick_pump_note_ran(); }

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

#define REFII_VFS_NATIVE_PASSTHROUGH(function_name) \
  do {                                              \
    if (!refii::vfs::RefiiLooseAssetBackendEnabled()) { \
      __imp__##function_name(ctx, base);            \
      return;                                       \
    }                                               \
  } while (false)
