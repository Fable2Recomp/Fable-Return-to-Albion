#pragma once

#include <chrono>
#include <cstdint>
#include <string>

namespace refii::vfs {

struct RefiiStartupVideoItemTrace {
  std::string provider;
  uint32_t object_guest = 0;
  uint32_t source_stream_guest = 0;
  uint32_t wrapped_stream_guest = 0;
  uint64_t expected_size = 0;
  uint32_t last_read_requested = 0;
  uint32_t last_read_completed = 0;
  bool wrapper_verdict_emitted = false;
  bool read_queued = false;
  bool read_complete = false;
  bool cursor_seen = false;
  std::chrono::steady_clock::time_point read_queued_at{};
  std::chrono::steady_clock::time_point read_complete_at{};
  std::chrono::steady_clock::time_point first_cursor_at{};
  std::chrono::steady_clock::time_point cursor_at{};
};

struct RefiiPostVideoHandoffTrace {
  RefiiStartupVideoItemTrace middleware;
  RefiiStartupVideoItemTrace microsoft;
  RefiiStartupVideoItemTrace lionhead;
  std::string last_bik_provider;
  uint32_t last_bik_object_guest = 0;
  uint32_t last_bik_stream_guest = 0;
  std::string last_ui_event;
  uint32_t last_ui_lr = 0;
  uint32_t last_ui_result = 0;
  uint32_t last_ui_r3 = 0;
  uint32_t last_ui_r4 = 0;
  uint32_t last_ui_r5 = 0;
  std::string last_getcached_name;
  uint32_t last_getcached_lr = 0;
  uint32_t last_getcached_stream = 0;
  uint32_t last_render_schedule_owner = 0;
  uint32_t last_render_schedule_handoff = 0;
  uint32_t last_render_schedule_lr = 0;
  uint32_t last_render_schedule_source124 = 0xFFFFFFFFu;
  uint32_t last_render_schedule_source128 = 0xFFFFFFFFu;
  double last_render_schedule_frame_time = 0.0;
  uint32_t last_nonidle_render_schedule_owner = 0;
  uint32_t last_nonidle_render_schedule_handoff = 0;
  uint32_t last_nonidle_render_schedule_lr = 0;
  uint32_t last_nonidle_render_schedule_source124 = 0xFFFFFFFFu;
  uint32_t last_nonidle_render_schedule_source128 = 0xFFFFFFFFu;
  double last_nonidle_render_schedule_frame_time = 0.0;
  std::string last_stage;
  uint32_t last_stage_lr = 0;
  uint32_t last_stage_result = 0;
  int64_t last_stage_elapsed_ms = -1;
  std::string slowest_stage;
  uint32_t slowest_stage_lr = 0;
  uint32_t slowest_stage_result = 0;
  int64_t slowest_stage_elapsed_ms = -1;
  std::string frame_slowest_stage;
  uint32_t frame_slowest_stage_lr = 0;
  uint32_t frame_slowest_stage_result = 0;
  int64_t frame_slowest_stage_elapsed_ms = -1;
  uint32_t last_startup_update_guest = 0;
  uint32_t last_startup_update_lr = 0;
  uint32_t last_startup_update_result = 0;
  uint32_t last_startup_update_frame_counter = 0;
  uint32_t last_startup_update_video_index = 0;
  uint32_t last_startup_update_vector_count = 0;
  uint32_t last_startup_update_complete_flag = 0;
  std::chrono::steady_clock::time_point last_bik_at{};
  std::chrono::steady_clock::time_point last_ui_at{};
  std::chrono::steady_clock::time_point last_getcached_at{};
  std::chrono::steady_clock::time_point last_render_schedule_at{};
  std::chrono::steady_clock::time_point last_nonidle_render_schedule_at{};
  std::chrono::steady_clock::time_point last_stage_at{};
  std::chrono::steady_clock::time_point slowest_stage_at{};
  std::chrono::steady_clock::time_point frame_slowest_stage_at{};
  std::chrono::steady_clock::time_point last_startup_update_at{};
};
}  // namespace refii::vfs
