// refii — the movie the guest states, as SHARED STATE.
//
// In librefii_runtime because the two ends live in different libraries: a title's Bink hooks
// (the payload, on the guest's threads) state each call through the refii_bridge_movie_* entries,
// and the host bridge's movie exports (on Godot's thread) read it through core/movie_state.h.
//
// THE CONTRACT IS THE PLAYER'S, NOT A TITLE'S. Every Bink player the titles have is opened with a
// source (the .bik's bytes, or a path), opened with or without sound, drawn in a rect the guest
// states, and ends on a byte of its own that its decode sets; some classes also carry a skip byte.
// The title's hooks name those bytes as guest addresses; this file reads and writes them and
// nothing else in the player.

#include <algorithm>
#include <atomic>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>

#include <rex/logging.h>

#include "core/movie_state.h"
#include "core/payload_bridge.h"

namespace {

/// The movie the guest most recently asked to play, and a counter that changes whenever it does.
///
/// A generation counter rather than a "dirty" flag: the host polls, and a flag would make two
/// requests for the same file in a row indistinguishable from one — which is what a startup
/// sequence does when a logo is replayed.
std::atomic<uint32_t> g_requested_address{0};
std::atomic<uint32_t> g_requested_size{0};
std::atomic<uint32_t> g_requested_swapped{0};
std::atomic<uint32_t> g_requested_generation{0};

/// The path form. Guarded rather than atomic because it is a string; it changes at most once
/// per movie, so the lock is uncontended.
std::mutex g_requested_name_mutex;
std::string g_requested_name;

/// The live player and its two bytes. The player is cleared once retired or closed: the guest
/// frees it soon after either, so the addresses are good until then and not after.
std::mutex g_live_mutex;
uint32_t g_live_player = 0;
uint32_t g_live_done = 0;
uint32_t g_live_skip = 0;

constexpr uint32_t kMaxStatedTracks = 8u;

struct StatedSound {
  uint32_t player = 0;
  uint32_t sound = 0;
  uint32_t count = 0;
  uint32_t ids[kMaxStatedTracks] = {};
  float volumes[kMaxStatedTracks] = {};
};

std::mutex g_sound_mutex;
StatedSound g_sound;

// The rects the guest has stated, by the player each was stated for. PER PLAYER, because players
// coexist: a UI movie restates its rect on every element update while a fullscreen player, which
// states once, may be the live one. A player's entry is dropped when that player is opened again
// or closed, so a rect never outlives its movie — the allocator hands the next movie the same block.
struct StatedRect {
  uint32_t player = 0;
  int32_t left = 0;
  int32_t top = 0;
  int32_t right = 0;
  int32_t bottom = 0;
};

std::mutex g_rect_mutex;
StatedRect g_rects[8];
uint32_t g_rect_next = 0;

// Both under g_rect_mutex.
StatedRect* FindStatedRect(uint32_t player) {
  for (StatedRect& rect : g_rects) {
    if (rect.player == player) {
      return &rect;
    }
  }
  return nullptr;
}

void DropStatedRect(uint32_t player) {
  if (StatedRect* rect = FindStatedRect(player)) {
    *rect = {};
  }
}

/// Is this guest memory a plain path rather than a loaded file?
///
/// A title opens some movies from a buffer it has already read (the startup logos) and others BY
/// NAME — the argument is then "game:\\data\\art\\videos\\....bik". Both arrive at the same call, so
/// the pointer has to be classified rather than assumed.
bool LooksLikeMoviePath(const uint8_t* data, std::string* out) {
  std::string text;
  for (uint32_t i = 0; i < 260u; ++i) {
    const uint8_t c = data[i];
    if (c == 0) {
      break;
    }
    // Printable ASCII only: a path is single-byte here, so any high byte means it is not one.
    if (c < 32 || c > 126) {
      return false;
    }
    text.push_back(static_cast<char>(c));
  }

  if (text.size() < 5) {
    return false;
  }

  std::string lowered = text;
  for (char& c : lowered) {
    c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
  }

  if (lowered.find(".bik") == std::string::npos) {
    return false;
  }

  *out = std::move(text);
  return true;
}

}  // namespace

namespace refii::movie {

void Requested(uint32_t* out_address, uint32_t* out_size, uint32_t* out_generation,
               uint32_t* out_swapped) {
  if (out_swapped != nullptr) {
    *out_swapped = g_requested_swapped.load(std::memory_order_relaxed);
  }
  if (out_address != nullptr) {
    *out_address = g_requested_address.load(std::memory_order_relaxed);
  }
  if (out_size != nullptr) {
    *out_size = g_requested_size.load(std::memory_order_relaxed);
  }
  if (out_generation != nullptr) {
    *out_generation = g_requested_generation.load(std::memory_order_acquire);
  }
}

uint32_t RequestedName(char* out, uint32_t capacity) {
  if (out == nullptr || capacity == 0) {
    return 0;
  }

  std::lock_guard<std::mutex> lock(g_requested_name_mutex);
  const uint32_t length =
      static_cast<uint32_t>(std::min<size_t>(g_requested_name.size(), capacity - 1));
  std::memcpy(out, g_requested_name.data(), length);
  out[length] = '\0';
  return length;
}

uint32_t Sound(uint32_t* out_sound, uint32_t* out_ids, float* out_volumes, uint32_t capacity) {
  std::lock_guard<std::mutex> lock(g_sound_mutex);
  if (out_sound != nullptr) {
    *out_sound = g_sound.sound;
  }

  const uint32_t count = std::min(g_sound.count, capacity);
  for (uint32_t i = 0; i < count; ++i) {
    if (out_ids != nullptr) {
      out_ids[i] = g_sound.ids[i];
    }
    if (out_volumes != nullptr) {
      out_volumes[i] = g_sound.volumes[i];
    }
  }
  return count;
}

uint32_t State(const uint8_t* base) {
  if (base == nullptr) {
    return 0;
  }

  uint32_t player = 0;
  uint32_t done = 0;
  uint32_t skip = 0;
  {
    std::lock_guard<std::mutex> lock(g_live_mutex);
    player = g_live_player;
    done = g_live_done;
    skip = g_live_skip;
  }
  if (player == 0) {
    return 0;
  }

  const uint8_t skipped = skip != 0 ? base[skip] : 0u;
  static std::atomic<uint32_t> polls{0};
  const uint32_t poll = polls.fetch_add(1, std::memory_order_relaxed);
  if (poll < 40u || (poll % 512u) == 0u) {
    REXLOG_INFO("Refii movie poll #{} player=0x{:08X} done(0x{:08X})={} skip(0x{:08X})={}",
                poll, player, done, done != 0 ? base[done] : 0u, skip, skipped);
  }

  // A class with no skip byte cannot be skipped from here: still wanted.
  return skipped != 0 ? 2u : 1u;
}

uint32_t Finish(uint8_t* base) {
  if (base == nullptr) {
    return 0;
  }

  uint32_t player = 0;
  uint32_t done = 0;
  {
    std::lock_guard<std::mutex> lock(g_live_mutex);
    player = g_live_player;
    done = g_live_done;
    g_live_player = 0;
    g_live_done = 0;
    g_live_skip = 0;
  }
  if (player == 0 || done == 0) {
    return 0;
  }

  base[done] = 1u;

  static std::atomic<uint32_t> retired{0};
  if (retired.fetch_add(1) < 64u) {
    REXLOG_INFO("Refii movie retired by host: player=0x{:08X} (done at 0x{:08X})", player, done);
  }
  return player;
}

uint32_t Rect(float* out_x0, float* out_y0, float* out_x1, float* out_y1) {
  uint32_t player = 0;
  {
    std::lock_guard<std::mutex> lock(g_live_mutex);
    player = g_live_player;
  }
  if (player == 0) {
    return 0;
  }

  std::lock_guard<std::mutex> lock(g_rect_mutex);
  const StatedRect* rect = FindStatedRect(player);
  if (rect == nullptr) {
    return 0;
  }

  if (out_x0 != nullptr) *out_x0 = static_cast<float>(rect->left);
  if (out_y0 != nullptr) *out_y0 = static_cast<float>(rect->top);
  if (out_x1 != nullptr) *out_x1 = static_cast<float>(rect->right);
  if (out_y1 != nullptr) *out_y1 = static_cast<float>(rect->bottom);
  return 1;
}

}  // namespace refii::movie

extern "C" void refii_bridge_movie_requested(uint8_t* base, uint32_t data_guest) {
  if (base == nullptr || data_guest == 0) {
    return;
  }

  const uint8_t* data = base + data_guest;

  // What the pointer holds. A .bik starts 'BIK' + a revision byte; a loader that byte-swaps every
  // dword on the way in (a big-endian console reading a little-endian container) leaves it
  // reversed, which is what the startup logos look like in memory.
  static std::atomic<uint32_t> probed{0};
  if (probed.fetch_add(1) < 64u) {
    REXLOG_INFO(
        "Refii movie candidate at 0x{:08X}: {:02X} {:02X} {:02X} {:02X} | "
        "{:02X} {:02X} {:02X} {:02X}  ('{}{}{}{}')",
        data_guest, data[0], data[1], data[2], data[3], data[4], data[5], data[6], data[7],
        (char)(data[0] >= 32 ? data[0] : '.'), (char)(data[1] >= 32 ? data[1] : '.'),
        (char)(data[2] >= 32 ? data[2] : '.'), (char)(data[3] >= 32 ? data[3] : '.'));
  }

  const bool forward = data[0] == 'B' && data[1] == 'I' && data[2] == 'K';
  const bool reversed = data[3] == 'B' && data[2] == 'I' && data[1] == 'K';

  if (!forward && !reversed) {
    std::string path;
    if (!LooksLikeMoviePath(data, &path)) {
      return;
    }

    {
      std::lock_guard<std::mutex> lock(g_requested_name_mutex);
      g_requested_name = path;
    }
    g_requested_address.store(0, std::memory_order_relaxed);
    g_requested_size.store(0, std::memory_order_relaxed);
    g_requested_swapped.store(0, std::memory_order_relaxed);

    static std::atomic<uint32_t> named{0};
    if (named.fetch_add(1) < 64u) {
      REXLOG_INFO("Refii movie requested by name: {}", path);
    }
    g_requested_generation.fetch_add(1, std::memory_order_release);
    return;
  }

  // The header's file size (bytes 4..7, little-endian on disc) counts from byte 8.
  const uint32_t stated =
      forward ? ((uint32_t)data[4] | ((uint32_t)data[5] << 8) | ((uint32_t)data[6] << 16) |
                 ((uint32_t)data[7] << 24))
              : ((uint32_t)data[7] | ((uint32_t)data[6] << 8) | ((uint32_t)data[5] << 16) |
                 ((uint32_t)data[4] << 24));
  const uint64_t size = (uint64_t)stated + 8u;

  // Guards against a false match on 'BIK' in unrelated memory.
  if (size < 64 || size > 512u * 1024u * 1024u) {
    return;
  }

  static std::atomic<uint32_t> seen{0};
  if (seen.fetch_add(1) < 64u) {
    REXLOG_INFO("Refii movie requested: data=0x{:08X} size={}", data_guest, (uint32_t)size);
  }

  {
    std::lock_guard<std::mutex> lock(g_requested_name_mutex);
    g_requested_name.clear();
  }
  g_requested_address.store(data_guest, std::memory_order_relaxed);
  g_requested_size.store((uint32_t)size, std::memory_order_relaxed);
  g_requested_swapped.store(reversed ? 1u : 0u, std::memory_order_relaxed);
  g_requested_generation.fetch_add(1, std::memory_order_release);
}

extern "C" void refii_bridge_movie_sound(uint32_t player, uint32_t sound, const uint32_t* track_ids,
                                         uint32_t count) {
  if (player == 0) {
    return;
  }

  StatedSound stated;
  stated.player = player;
  stated.sound = sound != 0 ? 1u : 0u;
  stated.count = track_ids != nullptr ? std::min(count, kMaxStatedTracks) : 0u;
  for (uint32_t i = 0; i < stated.count; ++i) {
    stated.ids[i] = track_ids[i];
    stated.volumes[i] = 1.0f;
  }

  REXLOG_INFO("Refii movie sound stated: player=0x{:08X} sound={} tracks={} ids={},{},{},{}",
              player, stated.sound, stated.count, stated.ids[0], stated.ids[1], stated.ids[2],
              stated.ids[3]);

  std::lock_guard<std::mutex> lock(g_sound_mutex);
  g_sound = stated;
}

extern "C" void refii_bridge_movie_volume(uint32_t player, uint32_t track_id, float volume) {
  std::lock_guard<std::mutex> lock(g_sound_mutex);
  if (g_sound.player != player) {
    return;
  }

  for (uint32_t i = 0; i < g_sound.count; ++i) {
    if (g_sound.ids[i] == track_id) {
      g_sound.volumes[i] = std::clamp(volume, 0.0f, 1.0f);
    }
  }
}

extern "C" void refii_bridge_movie_open(uint32_t player, uint32_t done_byte, uint32_t skip_byte) {
  if (player == 0) {
    return;
  }

  // A new movie in this player: whatever rect was stated for the last one is not this one's.
  {
    std::lock_guard<std::mutex> lock(g_rect_mutex);
    DropStatedRect(player);
  }
  {
    std::lock_guard<std::mutex> lock(g_live_mutex);
    g_live_player = player;
    g_live_done = done_byte;
    g_live_skip = skip_byte;
  }

  static std::atomic<uint32_t> seen{0};
  if (seen.fetch_add(1) < 64u) {
    REXLOG_INFO("Refii movie open: player=0x{:08X} done=0x{:08X} skip=0x{:08X}", player,
                done_byte, skip_byte);
  }
}

extern "C" void refii_bridge_movie_skip_byte(uint32_t player, uint32_t skip_byte) {
  std::lock_guard<std::mutex> lock(g_live_mutex);
  if (g_live_player == player && player != 0) {
    g_live_skip = skip_byte;
  }
}

extern "C" void refii_bridge_movie_rect(uint32_t player, int32_t left, int32_t top, int32_t right,
                                        int32_t bottom) {
  if (player == 0) {
    return;
  }

  bool first = false;
  {
    std::lock_guard<std::mutex> lock(g_rect_mutex);
    StatedRect* rect = FindStatedRect(player);
    first = rect == nullptr;
    if (rect == nullptr) {
      // A free slot, else the oldest: eight live players is already more than a title has.
      rect = FindStatedRect(0u);
      if (rect == nullptr) {
        rect = &g_rects[g_rect_next++ % 8u];
      }
    }
    *rect = {player, left, top, right, bottom};
  }

  static std::atomic<uint32_t> seen{0};
  if (first && seen.fetch_add(1, std::memory_order_relaxed) < 64u) {
    REXLOG_INFO("Refii movie rect stated player=0x{:08X} left={} top={} right={} bottom={}",
                player, left, top, right, bottom);
  }
}

extern "C" void refii_bridge_movie_closed(uint32_t player) {
  if (player == 0) {
    return;
  }

  {
    std::lock_guard<std::mutex> lock(g_live_mutex);
    if (g_live_player == player) {
      g_live_player = 0;
      g_live_done = 0;
      g_live_skip = 0;
    }
  }

  std::lock_guard<std::mutex> lock(g_rect_mutex);
  DropStatedRect(player);
}
