#pragma once

#include <chrono>
#include <cstdint>

namespace refii::renderer::bink {

struct AudioCursorBridgeResult {
  uint32_t cursor = 0;
  uint32_t ring_size = 0;
  uint32_t bytes_per_second = 0;
  // Native (device) cursor movement observed on this query: the consumption
  // delta in bytes since the previous observation. Nonzero only when the
  // real voice cursor is alive and was adopted as the clock.
  uint32_t consumed_bytes = 0;
  bool bridged = false;
  bool native_live = false;
};

struct AudioCursorAdvanceResult {
  std::chrono::nanoseconds cursor_duration = {};
  uint32_t old_cursor = 0;
  uint32_t cursor_base = 0;
  uint32_t new_cursor = 0;
  uint32_t ring_size = 0;
  uint32_t bytes_per_second = 0;
  uint32_t advanced_bytes = 0;
  bool advanced = false;
};

void RegisterAudioCursorBridge(uint32_t voice_guest, uint32_t ring_size,
                               uint32_t bytes_per_second,
                               uint32_t current_cursor);

void ClearAudioCursorBridge(uint32_t voice_guest);

AudioCursorBridgeResult ObserveNativeAudioCursor(
    uint32_t voice_guest, uint32_t native_cursor);

AudioCursorAdvanceResult AdvanceAudioCursorByBytes(uint32_t voice_guest,
                                                   uint32_t current_cursor,
                                                   uint32_t output_bytes);

} // namespace refii::renderer::bink
