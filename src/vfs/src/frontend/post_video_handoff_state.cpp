#include "vfs_hooks_private.h"
#include "core/guest_build.h"

namespace refii::vfs {
inline namespace detail {

bool RefiiMainMenuTraceIsHashOnly();
bool RefiiMainMenuTraceIsTextureOwnedButNotOpened();

std::string FormatRefiiPostVideoHandoffTraceForLog() {
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
  return fmt::format("boot_ms={} "
                     "middleware=[{}] microsoft=[{}] lionhead=[{}] "
                     "last_bik={} object=0x{:08X} stream=0x{:08X} age_ms={} "
                     "last_ui={} lr=0x{:08X} result=0x{:08X} "
                     "args=0x{:08X}/0x{:08X}/0x{:08X} age_ms={} "
                     "ready_update=0x{:08X} update_lr=0x{:08X} "
                     "update_result=0x{:08X} update_frame={} "
                     "update_index={} update_count={} update_complete={} "
                     "update_age_ms={} "
                     "last_getcached={} lr=0x{:08X} stream=0x{:08X} age_ms={} "
                     "render_schedule_owner=0x{:08X} "
                     "render_schedule_handoff=0x{:08X} "
                     "render_schedule_lr=0x{:08X} "
                     "render_schedule_source=0x{:08X}/0x{:08X} "
                     "render_schedule_time={} render_schedule_age_ms={} "
                     "nonidle_render_owner=0x{:08X} "
                     "nonidle_render_handoff=0x{:08X} "
                     "nonidle_render_lr=0x{:08X} "
                     "nonidle_render_source=0x{:08X}/0x{:08X} "
                     "nonidle_render_time={} nonidle_render_age_ms={} "
                     "last_stage={} stage_lr=0x{:08X} "
                     "stage_result=0x{:08X} stage_elapsed_ms={} "
                     "stage_age_ms={} "
                     "frame_slowest_stage={} frame_slowest_lr=0x{:08X} "
                     "frame_slowest_result=0x{:08X} "
                     "frame_slowest_elapsed_ms={} frame_slowest_age_ms={} "
                     "slowest_stage={} slowest_lr=0x{:08X} slowest_result=0x{:08X} "
                     "slowest_elapsed_ms={} slowest_age_ms={}",
                     RefiiBootTraceAgeMs(),
                     FormatRefiiStartupVideoSlotTrace(trace.middleware, now),
                     FormatRefiiStartupVideoSlotTrace(trace.microsoft, now),
                     FormatRefiiStartupVideoSlotTrace(trace.lionhead, now),
                     trace.last_bik_provider,
                     trace.last_bik_object_guest,
                     trace.last_bik_stream_guest,
                     age_ms(trace.last_bik_at),
                     trace.last_ui_event,
                     trace.last_ui_lr,
                     trace.last_ui_result,
                     trace.last_ui_r3,
                     trace.last_ui_r4,
                     trace.last_ui_r5,
                     age_ms(trace.last_ui_at),
                     trace.last_startup_update_guest,
                     trace.last_startup_update_lr,
                     trace.last_startup_update_result,
                     trace.last_startup_update_frame_counter,
                     trace.last_startup_update_video_index,
                     trace.last_startup_update_vector_count,
                     trace.last_startup_update_complete_flag,
                     age_ms(trace.last_startup_update_at),
                     trace.last_getcached_name,
                     trace.last_getcached_lr,
                     trace.last_getcached_stream,
                     age_ms(trace.last_getcached_at),
                     trace.last_render_schedule_owner,
                     trace.last_render_schedule_handoff,
                     trace.last_render_schedule_lr,
                     trace.last_render_schedule_source124,
                     trace.last_render_schedule_source128,
                     trace.last_render_schedule_frame_time,
                     age_ms(trace.last_render_schedule_at),
                     trace.last_nonidle_render_schedule_owner,
                     trace.last_nonidle_render_schedule_handoff,
                     trace.last_nonidle_render_schedule_lr,
                     trace.last_nonidle_render_schedule_source124,
                     trace.last_nonidle_render_schedule_source128,
                     trace.last_nonidle_render_schedule_frame_time,
                     age_ms(trace.last_nonidle_render_schedule_at),
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

bool ShouldTracePostVideoBoundaryProbe(std::string_view api) {
  return kTraceFrontendHandoffDiagnostics && VfsTracingEnabled() &&
         !RefiiStartupVideosPlaybackCriticalActive() &&
         (RefiiMainMenuTraceIsHashOnly() ||
          RefiiMainMenuTraceIsTextureOwnedButNotOpened()) &&
         ShouldLogRepeatedVfsTraceOperation(api, 8);
}

bool ShouldTracePostVideoTextureOwnedHandoffProbe(std::string_view api) {
  return kTraceFrontendHandoffDiagnostics && VfsTracingEnabled() &&
         RefiiMainMenuTraceIsTextureOwnedButNotOpened() &&
         ShouldLogRepeatedVfsTraceOperation(api, 8);
}

bool RefiiPostVideoVfsHandoffTraceActive() {
  return kTraceFrontendHandoffDiagnostics && VfsTracingEnabled() &&
         (RefiiMainMenuTraceIsHashOnly() ||
          RefiiMainMenuTraceIsTextureOwnedButNotOpened());
}

std::string FormatPostVideoBoundaryArgs(uint8_t* base,
                                        const PPCContext& ctx) {
  return fmt::format("r3=0x{:08X} r4=0x{:08X} r5=0x{:08X} r6=0x{:08X} "
                     "r7=0x{:08X} r8=0x{:08X} stack=0x{:08X} "
                     "r3_words={} r3_vtable={} r4_words={} "
                     "r5_words={} "
                     "stack_words={}",
                     ctx.r3.u32,
                     ctx.r4.u32,
                     ctx.r5.u32,
                     ctx.r6.u32,
                     ctx.r7.u32,
                     ctx.r8.u32,
                     ctx.r1.u32,
                     FormatGuestWordsIfPointer(base, ctx.r3.u32, 8),
                     FormatGuestObjectVTableSlots(base, ctx.r3.u32, 8),
                     FormatGuestWordsIfPointer(base, ctx.r4.u32, 6),
                     FormatGuestWordsIfPointer(base, ctx.r5.u32, 6),
                     FormatGuestWordsIfPointer(base, ctx.r1.u32, 10));
}

float RefiiGuestF32FromWord(uint32_t word) {
  float value = 0.0f;
  std::memcpy(&value, &word, sizeof(value));
  return value;
}

double RefiiGuestF64FromWord(uint64_t word) {
  double value = 0.0;
  std::memcpy(&value, &word, sizeof(value));
  return value;
}

std::string FormatStartupVideoRenderPollDispatchState(uint8_t* base) {
  const uint32_t kStartupVideoRenderPollObjectGlobal = refii::guest::Address(0x8349E9ECu);
  if (base == nullptr) {
    return {};
  }

  const uint32_t object_guest =
      LoadGuestU32(base, kStartupVideoRenderPollObjectGlobal);
  const uint32_t vtable_guest =
      object_guest != 0 ? LoadGuestU32(base, object_guest) : 0;
  const uint32_t slot16 =
      vtable_guest != 0 ? LoadGuestU32(base, vtable_guest + 16) : 0;

  return fmt::format(
      "global=0x{:08X} object=0x{:08X} vtable=0x{:08X} "
      "slot16=0x{:08X} object_words={} vtable_slots={} expected_lr=0x82377D54",
      kStartupVideoRenderPollObjectGlobal,
      object_guest,
      vtable_guest,
      slot16,
      FormatGuestWordsIfPointer(base, object_guest, 12),
      FormatGuestWordsIfPointer(base, vtable_guest, 10));
}

std::string FormatStartupVideoUpdateTimingState(uint8_t* base,
                                                uint32_t update_guest) {
  if (update_guest < 0x10000) {
    return {};
  }

  const uint32_t frame_counter = LoadGuestU32(base, update_guest + 4);
  const uint64_t last_time_word = LoadGuestU64(base, update_guest + 72);
  const double last_time = RefiiGuestF64FromWord(last_time_word);
  const uint32_t last_frame = LoadGuestU32(base, update_guest + 80);
  const uint32_t video_index = LoadGuestU32(base, update_guest + 84);
  const uint32_t vector_begin = LoadGuestU32(base, update_guest + 56);
  const uint32_t vector_end = LoadGuestU32(base, update_guest + 60);
  const uint32_t vector_count =
      vector_end >= vector_begin ? (vector_end - vector_begin) / 8 : 0;
  const uint32_t complete_flag = LoadGuestU8(base, update_guest + 96);

  return fmt::format(
      "update=0x{:08X} frame_counter={} last_frame={} "
      "last_time={} last_time_word=0x{:016X} video_index={} "
      "video_vector=0x{:08X}-0x{:08X} count={} complete_flag={} "
      "render_poll=[{}] update_words={} update_56={}",
      update_guest,
      frame_counter,
      last_frame,
      last_time,
      last_time_word,
      video_index,
      vector_begin,
      vector_end,
      vector_count,
      complete_flag,
      FormatStartupVideoRenderPollDispatchState(base),
      FormatGuestWordsIfPointer(base, update_guest, 28),
      FormatGuestWordsAtOffsetIfPointer(base, update_guest, 56, 12));
}

std::string FormatPostVideoFrameWaitState(uint8_t* base,
                                          uint32_t owner_guest) {
  const uint32_t handoff_guest =
      owner_guest != 0 ? LoadGuestU32(base, owner_guest + 4) : 0;
  const uint32_t lock_target =
      handoff_guest != 0 ? handoff_guest + 456 : 0;
  const uint32_t wait_target =
      handoff_guest != 0 ? handoff_guest + 460 : 0;
  const uint32_t event_object =
      handoff_guest != 0 ? LoadGuestU32(base, handoff_guest + 456) : 0;
  return fmt::format("owner=0x{:08X} handoff=0x{:08X} lock_target=0x{:08X} "
                     "wait_target=0x{:08X} event_object=0x{:08X} "
                     "handoff_words={} wait_words={}",
                     owner_guest,
                     handoff_guest,
                     lock_target,
                     wait_target,
                     event_object,
                     FormatGuestWordsIfPointer(base, handoff_guest, 12),
                     FormatGuestWordsIfPointer(base, wait_target, 8));
}

std::string FormatNativeVfsAsyncRequestState(uint8_t* base,
                                             uint32_t request_guest) {
  if (!LooksLikeGuestPointer(request_guest)) {
    return fmt::format("request=0x{:08X}", request_guest);
  }
  const uint32_t vtable = LoadGuestU32(base, request_guest);
  std::string kind = "generic";
  std::string inner_state;
  if (vtable == kNativeStartupReadyRequestVTable) {
    kind = "startup_ready";
    inner_state = fmt::format(
        " inline_owner=0x{:08X} video_source=0x{:08X} callback=0x{:08X} "
        "scheduler=0x{:08X} update=[{}] request_56={}",
        LoadGuestU32(base, request_guest + 16),
        LoadGuestU32(base, request_guest + 20),
        LoadGuestU32(base, request_guest + 24),
        LoadGuestU32(base, request_guest + 28),
        FormatStartupVideoUpdateTimingState(base, request_guest),
        FormatGuestWordsAtOffsetIfPointer(base, request_guest, 56, 8));
  } else if (vtable == kNativeFrontendRenderRequestVTable) {
    kind = "frontend_render";
    inner_state =
        fmt::format(" inline_owner=0x{:08X} render_source=0x{:08X} "
                    "render_group=0x{:08X} scheduler=0x{:08X} "
                    "handoff=0x{:08X} request_124={} render_flag196=0x{:02X}",
                    LoadGuestU32(base, request_guest + 24),
                    LoadGuestU32(base, request_guest + 28),
                    LoadGuestU32(base, request_guest + 32),
                    LoadGuestU32(base, request_guest + 36),
                    LoadGuestU32(base, request_guest + 40),
                    FormatGuestWordsAtOffsetIfPointer(base, request_guest, 124, 8),
                    LoadGuestU8(base, request_guest + 196));
  }
  return fmt::format("request=0x{:08X} kind={} words={} vtable=0x{:08X} "
                     "slots={} payload20={} payload40={}{}",
                     request_guest,
                     kind,
                     FormatGuestWordsIfPointer(base, request_guest, 12),
                     vtable,
                     FormatGuestWordsIfPointer(base, vtable, 10),
                     FormatGuestWordsAtOffsetIfPointer(base, request_guest, 20, 8),
                     FormatGuestWordsAtOffsetIfPointer(base, request_guest, 40, 8),
                     inner_state);
}
}  // namespace detail
}  // namespace refii::vfs
