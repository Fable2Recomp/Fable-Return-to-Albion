#pragma once

#include <chrono>
#include <cstdint>

namespace refii::renderer::bink {

using MovieClock = std::chrono::steady_clock;

struct MovieFramePaceResult {
  MovieClock::time_point sleep_until = {};
  std::chrono::nanoseconds lateness = {};
  uint32_t frame_delta = 0;
  bool frame_advanced = false;
  bool wait_requested = false;
};

struct MovieFrameCommitResult {
  std::chrono::nanoseconds post_resync_lateness = {};
  uint32_t catchup_frame_cap = 0;
  bool resynced = false;
};

std::chrono::nanoseconds FrameIntervalForDelta(
    std::chrono::nanoseconds interval, uint32_t frame_delta);

MovieFramePaceResult ObserveFrame(uint32_t movie_guest, uint32_t frame,
                                  std::chrono::nanoseconds interval);

MovieFrameCommitResult CommitFrame(uint32_t movie_guest, uint32_t frame,
                                   uint32_t frame_delta,
                                   std::chrono::nanoseconds interval,
                                   MovieClock::time_point after_sleep,
                                   std::chrono::nanoseconds lateness);

void ClearMovie(uint32_t movie_guest);

} // namespace refii::renderer::bink
