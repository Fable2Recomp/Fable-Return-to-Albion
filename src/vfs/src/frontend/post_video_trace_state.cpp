#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {
void RecordRefiiPostVideoUiEvent(std::string_view event,
                                 uint32_t lr,
                                 uint32_t result,
                                 uint32_t r3,
                                 uint32_t r4,
                                 uint32_t r5) {
  if (!kTraceFrontendHandoffDiagnostics) {
    return;
  }
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  trace.last_ui_event = std::string(event);
  trace.last_ui_lr = lr;
  trace.last_ui_result = result;
  trace.last_ui_r3 = r3;
  trace.last_ui_r4 = r4;
  trace.last_ui_r5 = r5;
  trace.last_ui_at = std::chrono::steady_clock::now();
}

void RecordRefiiPostVideoStageTiming(std::string_view stage,
                                      uint32_t lr,
                                      uint32_t result,
                                      int64_t elapsed_ms) {
  if (elapsed_ms < 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  const auto now = std::chrono::steady_clock::now();
  trace.last_stage = std::string(stage);
  trace.last_stage_lr = lr;
  trace.last_stage_result = result;
  trace.last_stage_elapsed_ms = elapsed_ms;
  trace.last_stage_at = now;
  if (elapsed_ms > trace.slowest_stage_elapsed_ms) {
    trace.slowest_stage = std::string(stage);
    trace.slowest_stage_lr = lr;
    trace.slowest_stage_result = result;
    trace.slowest_stage_elapsed_ms = elapsed_ms;
    trace.slowest_stage_at = now;
  }
  if (elapsed_ms > trace.frame_slowest_stage_elapsed_ms) {
    trace.frame_slowest_stage = std::string(stage);
    trace.frame_slowest_stage_lr = lr;
    trace.frame_slowest_stage_result = result;
    trace.frame_slowest_stage_elapsed_ms = elapsed_ms;
    trace.frame_slowest_stage_at = now;
  }
}

struct RefiiStartupUpdateSnapshot {
  uint32_t guest = 0;
  uint32_t frame_counter = 0;
  uint32_t video_index = 0;
  uint32_t vector_begin = 0;
  uint32_t vector_end = 0;
  uint32_t vector_count = 0;
  uint32_t complete_flag = 0;
};

RefiiStartupUpdateSnapshot CaptureRefiiStartupUpdateSnapshot(
    uint8_t* base,
    uint32_t update_guest) {
  RefiiStartupUpdateSnapshot snapshot;
  snapshot.guest = update_guest;
  if (base == nullptr || update_guest < 0x10000) {
    return snapshot;
  }

  snapshot.frame_counter = LoadGuestU32(base, update_guest + 4);
  snapshot.video_index = LoadGuestU32(base, update_guest + 84);
  snapshot.vector_begin = LoadGuestU32(base, update_guest + 56);
  snapshot.vector_end = LoadGuestU32(base, update_guest + 60);
  snapshot.vector_count =
      snapshot.vector_end >= snapshot.vector_begin
          ? (snapshot.vector_end - snapshot.vector_begin) / 8
          : 0;
  snapshot.complete_flag = LoadGuestU8(base, update_guest + 96);
  return snapshot;
}

void RecordRefiiStartupUpdateSnapshot(uint8_t* base,
                                      uint32_t update_guest,
                                      uint32_t lr,
                                      uint32_t result) {
  const RefiiStartupUpdateSnapshot snapshot =
      CaptureRefiiStartupUpdateSnapshot(base, update_guest);
  if (snapshot.guest < 0x10000) {
    return;
  }

  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  trace.last_startup_update_guest = snapshot.guest;
  trace.last_startup_update_lr = lr;
  trace.last_startup_update_result = result;
  trace.last_startup_update_frame_counter = snapshot.frame_counter;
  trace.last_startup_update_video_index = snapshot.video_index;
  trace.last_startup_update_vector_count = snapshot.vector_count;
  trace.last_startup_update_complete_flag = snapshot.complete_flag;
  trace.last_startup_update_at = std::chrono::steady_clock::now();
}

void RecordRefiiGetCachedHandoffEvent(std::string_view name,
                                      uint32_t lr,
                                      uint32_t stream_guest) {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  trace.last_getcached_name = NormalizeLooseAssetKey(name);
  trace.last_getcached_lr = lr;
  trace.last_getcached_stream = stream_guest;
  trace.last_getcached_at = std::chrono::steady_clock::now();
}

void RecordRefiiRenderProducerScheduleSnapshot(uint8_t* base,
                                               uint32_t owner_guest,
                                               uint32_t lr,
                                               uint32_t source124,
                                               uint32_t source128,
                                               double frame_time) {
  if (base == nullptr || !LooksLikeGuestPointer(owner_guest)) {
    return;
  }
  const uint32_t handoff_guest = LoadGuestU32(base, owner_guest + 4);
  if (!LooksLikeGuestPointer(handoff_guest)) {
    return;
  }

  const auto now = std::chrono::steady_clock::now();
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  trace.last_render_schedule_owner = owner_guest;
  trace.last_render_schedule_handoff = handoff_guest;
  trace.last_render_schedule_lr = lr;
  trace.last_render_schedule_source124 = source124;
  trace.last_render_schedule_source128 = source128;
  trace.last_render_schedule_frame_time = frame_time;
  trace.last_render_schedule_at = now;

  if (source124 != 0xFFFFFFFFu || source128 != 0xFFFFFFFFu) {
    trace.last_nonidle_render_schedule_owner = owner_guest;
    trace.last_nonidle_render_schedule_handoff = handoff_guest;
    trace.last_nonidle_render_schedule_lr = lr;
    trace.last_nonidle_render_schedule_source124 = source124;
    trace.last_nonidle_render_schedule_source128 = source128;
    trace.last_nonidle_render_schedule_frame_time = frame_time;
    trace.last_nonidle_render_schedule_at = now;
  }
}

std::string FormatRefiiPostVideoStageTimingForLog() {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  const auto& trace = RefiiPostVideoHandoffTraceState();
  const auto now = std::chrono::steady_clock::now();
  auto age_ms = [&](std::chrono::steady_clock::time_point timestamp) -> int64_t {
    if (timestamp.time_since_epoch().count() == 0) {
      return -1;
    }
    return std::chrono::duration_cast<std::chrono::milliseconds>(now - timestamp)
        .count();
  };
  return fmt::format(
      "last={} lr=0x{:08X} result=0x{:08X} elapsed_ms={} age_ms={} "
      "frame_slowest={} frame_slowest_lr=0x{:08X} "
      "frame_slowest_result=0x{:08X} frame_slowest_elapsed_ms={} "
      "frame_slowest_age_ms={} "
      "slowest={} slowest_lr=0x{:08X} slowest_result=0x{:08X} "
      "slowest_elapsed_ms={} slowest_age_ms={}",
      trace.last_stage.empty() ? "none" : trace.last_stage,
      trace.last_stage_lr,
      trace.last_stage_result,
      trace.last_stage_elapsed_ms,
      age_ms(trace.last_stage_at),
      trace.frame_slowest_stage.empty() ? "none" : trace.frame_slowest_stage,
      trace.frame_slowest_stage_lr,
      trace.frame_slowest_stage_result,
      trace.frame_slowest_stage_elapsed_ms,
      age_ms(trace.frame_slowest_stage_at),
      trace.slowest_stage.empty() ? "none" : trace.slowest_stage,
      trace.slowest_stage_lr,
      trace.slowest_stage_result,
      trace.slowest_stage_elapsed_ms,
      age_ms(trace.slowest_stage_at));
}

std::string FormatRefiiStartupVideoSlotTrace(
    const RefiiStartupVideoItemTrace& slot,
    std::chrono::steady_clock::time_point now) {
  if (slot.provider.empty()) {
    return "none";
  }
  auto age_ms = [&](std::chrono::steady_clock::time_point timestamp) -> int64_t {
    if (timestamp.time_since_epoch().count() == 0) {
      return -1;
    }
    return std::chrono::duration_cast<std::chrono::milliseconds>(now - timestamp)
        .count();
  };
  return fmt::format("{} object=0x{:08X} source=0x{:08X} wrapped=0x{:08X} "
                     "size={} read={}/{} queued={} complete={} cursor={} "
                     "ages_ms=read:{} first_cursor:{} cursor:{}",
                     slot.provider,
                     slot.object_guest,
                     slot.source_stream_guest,
                     slot.wrapped_stream_guest,
                     slot.expected_size,
                     slot.last_read_completed,
                     slot.last_read_requested,
                     slot.read_queued,
                     slot.read_complete,
                     slot.cursor_seen,
                     age_ms(slot.read_complete_at),
                     age_ms(slot.first_cursor_at),
                     age_ms(slot.cursor_at));
}

std::string FormatRefiiMainMenuBlockerTraceForLog();
}  // namespace detail
}  // namespace refii::vfs
