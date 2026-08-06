#include "runtime/refii_renderer/bik/bink_audio_cursor_bridge.h"

#include <algorithm>
#include <chrono>
#include <mutex>
#include <unordered_map>

namespace refii::renderer {
bool RendererBinkRealtimeAudioClockEnabled();
} // namespace refii::renderer

namespace refii::renderer::bink {
namespace {

// How long an observed native-cursor tick keeps the device clock
// authoritative before the synthetic wall-clock advance may resume.
constexpr std::chrono::milliseconds kNativeCursorAliveWindow(300);

struct AudioCursorBridge {
  uint32_t ring_size = 0;
  uint32_t bytes_per_second = 0;
  uint32_t last_cursor = 0;
  uint32_t last_native_cursor = 0;
  std::chrono::steady_clock::time_point native_alive_until = {};
  bool native_cursor_seen = false;
};

std::mutex g_audio_cursor_bridge_mutex;
std::unordered_map<uint32_t, AudioCursorBridge> g_audio_cursor_bridge;

uint32_t CursorForwardDistance(uint32_t from, uint32_t to, uint32_t ring_size) {
  if (ring_size == 0) {
    return 0;
  }
  return to >= from ? to - from : ring_size - from + to;
}

bool CursorIsBehind(uint32_t cursor, uint32_t reference, uint32_t ring_size) {
  if (ring_size == 0 || cursor == reference) {
    return false;
  }

  const uint32_t forward = CursorForwardDistance(reference, cursor, ring_size);
  const uint32_t backward = CursorForwardDistance(cursor, reference, ring_size);
  return backward < ring_size / 2u && forward > ring_size / 2u;
}

bool CursorJumpIsPlausible(uint32_t cursor, uint32_t reference,
                           uint32_t ring_size) {
  if (ring_size == 0 || cursor == reference) {
    return true;
  }

  return CursorForwardDistance(reference, cursor, ring_size) <=
         ring_size / 4u;
}

AudioCursorAdvanceResult AdvanceAudioCursorLocked(
    AudioCursorBridge &bridge, uint32_t current_cursor,
    uint32_t output_bytes) {
  AudioCursorAdvanceResult result = {};
  result.old_cursor = current_cursor;
  result.ring_size = bridge.ring_size;
  result.bytes_per_second = bridge.bytes_per_second;
  if (bridge.ring_size == 0 || bridge.bytes_per_second == 0 ||
      output_bytes == 0) {
    return result;
  }

  result.advanced_bytes = output_bytes;
  result.cursor_base = current_cursor < bridge.ring_size
                           ? current_cursor
                           : bridge.last_cursor;
  if (CursorIsBehind(result.cursor_base, bridge.last_cursor,
                     bridge.ring_size) ||
      !CursorJumpIsPlausible(result.cursor_base, bridge.last_cursor,
                             bridge.ring_size)) {
    result.cursor_base = bridge.last_cursor;
  }
  result.new_cursor =
      static_cast<uint32_t>((static_cast<uint64_t>(result.cursor_base) +
                             result.advanced_bytes) %
                            bridge.ring_size);
  if (result.new_cursor == current_cursor) {
    return result;
  }

  bridge.last_cursor = result.new_cursor;
  bridge.native_cursor_seen = true;
  result.advanced = true;
  return result;
}

} // namespace

void RegisterAudioCursorBridge(uint32_t voice_guest, uint32_t ring_size,
                               uint32_t bytes_per_second,
                               uint32_t current_cursor) {
  if (voice_guest == 0 || ring_size == 0 || bytes_per_second == 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_audio_cursor_bridge_mutex);
  auto existing = g_audio_cursor_bridge.find(voice_guest);
  if (existing != g_audio_cursor_bridge.end()) {
    AudioCursorBridge &bridge = existing->second;
    if (bridge.ring_size == ring_size &&
        bridge.bytes_per_second == bytes_per_second) {
      return;
    }
  }

  AudioCursorBridge &bridge = g_audio_cursor_bridge[voice_guest];
  bridge.ring_size = ring_size;
  bridge.bytes_per_second = bytes_per_second;
  bridge.last_cursor = current_cursor % ring_size;
  bridge.native_cursor_seen = false;
}

void ClearAudioCursorBridge(uint32_t voice_guest) {
  if (voice_guest == 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_audio_cursor_bridge_mutex);
  g_audio_cursor_bridge.erase(voice_guest);
}

AudioCursorBridgeResult ObserveNativeAudioCursor(
    uint32_t voice_guest, uint32_t native_cursor) {
  AudioCursorBridgeResult result = {};
  if (voice_guest == 0) {
    return result;
  }

  std::lock_guard<std::mutex> lock(g_audio_cursor_bridge_mutex);
  auto it = g_audio_cursor_bridge.find(voice_guest);
  if (it == g_audio_cursor_bridge.end()) {
    return result;
  }

  AudioCursorBridge &bridge = it->second;
  result.ring_size = bridge.ring_size;
  result.bytes_per_second = bridge.bytes_per_second;
  if (bridge.ring_size == 0 || bridge.bytes_per_second == 0) {
    return result;
  }

  if (native_cursor != 0) {
    const uint32_t observed_cursor = native_cursor % bridge.ring_size;
    if (bridge.native_cursor_seen) {
      // Real-time clock: a MOVING native cursor is the actual device play
      // position — adopt it as the clock (phase lock; the synthetic
      // wall-clock advance can only drift against the DAC and eventually
      // laps the play head, heard as static bursts or echo). The synthetic
      // path stays as the stall fallback only.
      if (refii::renderer::RendererBinkRealtimeAudioClockEnabled() &&
          observed_cursor != bridge.last_native_cursor) {
        uint32_t consumed = CursorForwardDistance(
            bridge.last_native_cursor, observed_cursor, bridge.ring_size);
        // A jump of half the ring or more is a reset/rewind, not
        // consumption: adopt the position but accrue nothing.
        if (consumed >= bridge.ring_size / 2u) {
          consumed = 0;
        }
        bridge.last_native_cursor = observed_cursor;
        bridge.last_cursor = observed_cursor;
        bridge.native_alive_until =
            std::chrono::steady_clock::now() + kNativeCursorAliveWindow;
        result.cursor = observed_cursor;
        result.consumed_bytes = consumed;
        result.native_live = true;
        return result;
      }
      if (observed_cursor != bridge.last_cursor) {
        result.cursor = bridge.last_cursor;
        result.bridged = true;
      }
      return result;
    }

    bridge.last_cursor = observed_cursor;
    bridge.last_native_cursor = observed_cursor;
    bridge.native_cursor_seen = true;
    return result;
  }

  if (bridge.last_cursor != 0) {
    result.cursor = bridge.last_cursor;
    result.bridged = true;
    return result;
  }

  return result;
}

AudioCursorAdvanceResult AdvanceAudioCursorByBytes(uint32_t voice_guest,
                                                   uint32_t current_cursor,
                                                   uint32_t output_bytes) {
  AudioCursorAdvanceResult result = {};
  result.old_cursor = current_cursor;
  if (voice_guest == 0 || output_bytes == 0) {
    return result;
  }

  std::lock_guard<std::mutex> lock(g_audio_cursor_bridge_mutex);
  auto it = g_audio_cursor_bridge.find(voice_guest);
  if (it == g_audio_cursor_bridge.end()) {
    return result;
  }
  if (refii::renderer::RendererBinkRealtimeAudioClockEnabled() &&
      std::chrono::steady_clock::now() < it->second.native_alive_until) {
    // The real device cursor is ticking — it owns audio time; the synthetic
    // advance would race it.
    return result;
  }

  return AdvanceAudioCursorLocked(it->second, current_cursor, output_bytes);
}


} // namespace refii::renderer::bink
