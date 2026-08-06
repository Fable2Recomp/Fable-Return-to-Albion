#include "runtime/refii_renderer/bik/bink_movie_clock.h"

#include <algorithm>
#include <mutex>
#include <unordered_map>

namespace refii::renderer::bink {
namespace {

struct MovieFramePace {
  MovieClock::time_point timeline_origin = {};
  MovieClock::time_point next_advance = {};
  uint32_t origin_frame = 0;
  uint32_t last_frame = 0;
  uint32_t recovery_cooldown = 0;
  bool initialized = false;
};

std::mutex g_frame_pace_mutex;
std::unordered_map<uint32_t, MovieFramePace> g_frame_pace;

std::chrono::nanoseconds SingleFrameResyncThreshold(
    std::chrono::nanoseconds interval) {
  return std::chrono::nanoseconds((interval.count() * 3) / 4);
}

} // namespace

std::chrono::nanoseconds FrameIntervalForDelta(
    std::chrono::nanoseconds interval, uint32_t frame_delta) {
  if (frame_delta == 0u) {
    return std::chrono::nanoseconds(0);
  }
  if (frame_delta == 1u) {
    return interval;
  }

  const uint64_t interval_ns = static_cast<uint64_t>(interval.count());
  constexpr uint64_t kMaxPaceDeltaNs = 1000000000ull;
  const uint64_t delta_ns =
      std::min<uint64_t>(interval_ns * static_cast<uint64_t>(frame_delta),
                         kMaxPaceDeltaNs);
  return std::chrono::nanoseconds(delta_ns);
}

// Absolute timeline offset since the pace origin — UNCLAMPED. The 1s clamp in
// FrameIntervalForDelta is for bounded catch-up BURSTS; applying it to the
// absolute offset saturated the schedule one second past origin (frame ~30 at
// 29.97fps), leaving every deadline permanently in the past — the pacer never
// slept again and each lateness resync re-anchored origin to now-1s, keeping
// it that way. The movies then free-ran at the guest's 60Hz outer-loop
// ceiling (run 424: the 25fps movie played at exactly 60.0fps, slept=false).
// The old 4fps renderer masked this as a ~27fps "accidental throttle".
std::chrono::nanoseconds TimelineOffsetForDelta(
    std::chrono::nanoseconds interval, uint32_t frame_delta) {
  return std::chrono::nanoseconds(interval.count() *
                                  static_cast<int64_t>(frame_delta));
}

MovieFramePaceResult ObserveFrame(uint32_t movie_guest, uint32_t frame,
                                  std::chrono::nanoseconds interval) {
  MovieFramePaceResult result = {};
  if (movie_guest == 0 || interval <= std::chrono::nanoseconds(0)) {
    return result;
  }

  const auto now = MovieClock::now();
  std::lock_guard<std::mutex> lock(g_frame_pace_mutex);
  MovieFramePace &pace = g_frame_pace[movie_guest];
  if (!pace.initialized || frame <= 1u || frame + 1u < pace.last_frame) {
    pace.initialized = true;
    pace.timeline_origin = now;
    pace.origin_frame = frame;
    pace.last_frame = frame;
    pace.next_advance = now + interval;
    return result;
  }

  if (frame == pace.last_frame) {
    if (pace.next_advance > now) {
      result.sleep_until = pace.next_advance;
      result.wait_requested = true;
    }
    return result;
  }

  result.frame_delta = frame > pace.last_frame ? frame - pace.last_frame : 1u;
  const uint32_t timeline_delta =
      frame >= pace.origin_frame ? frame - pace.origin_frame
                                 : result.frame_delta;
  result.sleep_until =
      pace.timeline_origin + TimelineOffsetForDelta(interval, timeline_delta);
  if (result.sleep_until < now) {
    result.lateness = now - result.sleep_until;
  }
  if (result.frame_delta > 1u) {
    pace.timeline_origin =
        now - TimelineOffsetForDelta(interval, timeline_delta);
    result.sleep_until = {};
    result.lateness = {};
    result.frame_advanced = true;
    return result;
  }
  const auto resync_lateness =
      FrameIntervalForDelta(interval, std::max(result.frame_delta, 4u));
  if (result.sleep_until <= now) {
    const auto target_time = result.sleep_until;
    result.sleep_until = {};
    result.lateness = now - target_time;
    if (result.lateness > resync_lateness) {
      pace.timeline_origin =
          now - TimelineOffsetForDelta(interval, timeline_delta);
      result.lateness = resync_lateness;
    }
  }
  result.frame_advanced = true;
  return result;
}

MovieFrameCommitResult CommitFrame(uint32_t movie_guest, uint32_t frame,
                                   uint32_t frame_delta,
                                   std::chrono::nanoseconds interval,
                                   MovieClock::time_point after_sleep,
                                   std::chrono::nanoseconds lateness) {
  MovieFrameCommitResult result = {};
  if (movie_guest == 0 || interval <= std::chrono::nanoseconds(0)) {
    return result;
  }

  std::lock_guard<std::mutex> lock(g_frame_pace_mutex);
  MovieFramePace &pace = g_frame_pace[movie_guest];
  const uint32_t timeline_delta =
      frame >= pace.origin_frame ? frame - pace.origin_frame : frame_delta;
  auto target_time =
      pace.timeline_origin + TimelineOffsetForDelta(interval, timeline_delta);
  pace.initialized = true;
  pace.last_frame = frame;
  if (pace.recovery_cooldown != 0) {
    --pace.recovery_cooldown;
  }
  const auto live_lateness =
      after_sleep > target_time ? after_sleep - target_time
                                : std::chrono::nanoseconds(0);
  const auto effective_lateness = std::max(lateness, live_lateness);
  uint32_t late_frames =
      effective_lateness >= interval
          ? static_cast<uint32_t>(std::min<int64_t>(
                (effective_lateness.count() + interval.count() - 1) /
                    interval.count(),
                4))
          : 0u;
  const uint32_t decode_catchup_frame_cap = late_frames;
  const bool should_resync_single_frame =
      frame_delta == 1u &&
      (late_frames != 0u ||
       effective_lateness >= SingleFrameResyncThreshold(interval));
  if (should_resync_single_frame) {
    pace.timeline_origin =
        after_sleep - TimelineOffsetForDelta(interval, timeline_delta);
    target_time = after_sleep;
    late_frames = 0u;
    result.resynced = true;
  }
  result.catchup_frame_cap = decode_catchup_frame_cap;
  result.post_resync_lateness =
      after_sleep > target_time ? after_sleep - target_time
                                : std::chrono::nanoseconds(0);
  if (frame_delta > 3u) {
    pace.recovery_cooldown = 1u;
  }
  pace.next_advance = target_time + interval;
  return result;
}

void ClearMovie(uint32_t movie_guest) {
  if (movie_guest == 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_frame_pace_mutex);
  g_frame_pace.erase(movie_guest);
}

} // namespace refii::renderer::bink
