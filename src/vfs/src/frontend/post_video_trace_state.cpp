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

void MaybeRecordFirstRefiiPostFrontendConsumerAssetRequest(
    std::string_view api,
    std::string_view asset_path,
    uint32_t resource_hash,
    uint32_t lr) {
  if (!kTraceFrontendHandoffDiagnostics || !VfsTracingEnabled()) {
    return;
  }

  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  if (normalized.empty() ||
      (!IsGameFaceBoundaryPath(normalized) &&
       !IsMainMenuTextureTraceAsset(normalized))) {
    return;
  }

  bool should_log = false;
  const bool active_frontend_consumer = FrontendTextureConsumerTraceActive();
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    if (!trace.frontend_consumer_seen && active_frontend_consumer &&
        !IsGameFaceBoundaryPath(normalized)) {
      return;
    }
    if ((!trace.frontend_consumer_seen && !active_frontend_consumer) ||
        trace.post_frontend_consumer_request_seen) {
      return;
    }
    trace.post_frontend_consumer_request_seen = true;
    trace.first_post_frontend_consumer_request_api = std::string(api);
    trace.first_post_frontend_consumer_request_asset = normalized;
    trace.first_post_frontend_consumer_request_lr = lr;
    trace.first_post_frontend_consumer_request_hash = resource_hash;
    trace.first_post_frontend_consumer_request_at =
        std::chrono::steady_clock::now();
    should_log = true;
  }

  if (!should_log) {
    return;
  }

  REXLOG_INFO("Refii VFS first post-frontend-consumer asset request api={} "
              "asset={} hash=0x{:08X} lr=0x{:08X} handoff=[{}] "
              "main_menu=[{}]",
              api,
              normalized,
              resource_hash,
              lr,
              FormatRefiiPostVideoHandoffTraceForLog(),
              FormatRefiiMainMenuBlockerTraceForLog());
  LogRefiiVfsChainVerdict("frontend_post_consumer_request",
                          lr,
                          normalized,
                          "",
                          "",
                          "Fable2_frontend_post_consumer",
                          api,
                          "TraceOnce",
                          IsGameFaceBoundaryPath(normalized)
                              ? "gameface_request_seen"
                              : "frontend_texture_request_seen");
}

void BeginRefiiPostVideoFrameStageTiming() {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  ++trace.stage_frame;
  trace.frame_slowest_stage.clear();
  trace.frame_slowest_stage_lr = 0;
  trace.frame_slowest_stage_result = 0;
  trace.frame_slowest_stage_elapsed_ms = -1;
  trace.frame_slowest_stage_at = {};
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
  trace.last_startup_update_vector_begin = snapshot.vector_begin;
  trace.last_startup_update_vector_end = snapshot.vector_end;
  trace.last_startup_update_vector_count = snapshot.vector_count;
  trace.last_startup_update_complete_flag = snapshot.complete_flag;
  trace.last_startup_update_at = std::chrono::steady_clock::now();
}

uint32_t LastRefiiStartupUpdateGuest() {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  return RefiiPostVideoHandoffTraceState().last_startup_update_guest;
}

void RecordRefiiStartupReadyPoll(uint8_t* base,
                                 uint32_t object_guest,
                                 uint32_t lr,
                                 uint32_t result) {
  const auto now = std::chrono::steady_clock::now();
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  trace.last_ready_poll_object = object_guest;
  trace.last_ready_poll_lr = lr;
  trace.last_ready_poll_result = result;
  const bool object_pointer = base != nullptr && object_guest >= 0x10000;
  trace.last_ready_poll_ready_byte =
      object_pointer ? LoadGuestU8(base, object_guest + 5) : 0;
  trace.last_ready_poll_pending_word52 =
      object_pointer ? LoadGuestU32(base, object_guest + 52) : 0;
  trace.last_ready_poll_word0 =
      object_pointer ? LoadGuestU32(base, object_guest + 0) : 0;
  trace.last_ready_poll_word1 =
      object_pointer ? LoadGuestU32(base, object_guest + 4) : 0;
  trace.last_ready_poll_word2 =
      object_pointer ? LoadGuestU32(base, object_guest + 8) : 0;
  trace.last_ready_poll_at = now;
  if (result == 0) {
    if (trace.ready_gate_false_since.time_since_epoch().count() == 0) {
      trace.ready_gate_false_since = now;
    }
  } else {
    trace.ready_gate_false_since = {};
  }
}

void RecordRefiiStartupCompletionGateSnapshot(uint8_t* base,
                                              uint32_t callback_guest,
                                              uint32_t lr,
                                              uint32_t result) {
  constexpr uint32_t kStartupVideoGlobalRoot = 0x83496B0C;
  const uint32_t callback_kind =
      callback_guest != 0 ? LoadGuestU32(base, callback_guest + 44) : 0;
  const uint32_t global_root = LoadGuestU32(base, kStartupVideoGlobalRoot);
  const uint32_t game_state =
      global_root != 0 ? LoadGuestU32(base, global_root + 8) : 0;
  const uint32_t owner =
      game_state != 0 ? LoadGuestU32(base, game_state + 40) : 0;
  const uint32_t handoff =
      owner != 0 ? LoadGuestU32(base, owner + 4) : 0;
  const uint32_t duration_word =
      handoff != 0 ? LoadGuestU32(base, handoff + 4) : 0;
  const uint32_t elapsed_word =
      handoff != 0 ? LoadGuestU32(base, handoff + 8) : 0;
  const bool should_complete =
      callback_kind == 13 && handoff != 0 &&
      RefiiGuestF32FromWord(elapsed_word) > RefiiGuestF32FromWord(duration_word);

  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  trace.last_completion_gate_callback = callback_guest;
  trace.last_completion_gate_kind = callback_kind;
  trace.last_completion_gate_result = result;
  trace.last_completion_gate_should_complete = should_complete ? 1 : 0;
  trace.last_completion_gate_at = std::chrono::steady_clock::now();
}

bool ApplyRefiiStartupVideoReadyPatchInternal(uint8_t* base,
                                              uint32_t update_guest,
                                              std::string_view reason,
                                              bool require_skip_videos) {
  if ((require_skip_videos && !SkipStartupVideosEnabled()) || base == nullptr ||
      update_guest < 0x10000) {
    return false;
  }

  const RefiiStartupUpdateSnapshot before =
      CaptureRefiiStartupUpdateSnapshot(base, update_guest);
  const uint32_t complete_index = before.vector_count;
  StoreGuestU32(base, update_guest + 84, complete_index);
  StoreGuestU8(base, update_guest + 96, 1);
  const RefiiStartupUpdateSnapshot after =
      CaptureRefiiStartupUpdateSnapshot(base, update_guest);

  bool should_log = false;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    trace.last_ready_patch_update = update_guest;
    trace.last_ready_patch_reason = std::string(reason);
    trace.last_ready_patch_at = std::chrono::steady_clock::now();
    should_log = !trace.skip_ready_patch_emitted;
    trace.skip_ready_patch_emitted = true;
  }

  if (should_log && VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS startup-video ready patch reason={} "
                "update=0x{:08X} before_index={} before_count={} "
                "before_complete={} after_index={} after_count={} "
                "after_complete={} skip_mode={} update_words={}",
                reason,
                update_guest,
                before.video_index,
                before.vector_count,
                before.complete_flag,
                after.video_index,
                after.vector_count,
                after.complete_flag,
                SkipStartupVideosEnabled() ? "true" : "false",
                FormatGuestWordsIfPointer(base, update_guest, 28));
  }
  return true;
}

bool ApplyRefiiSkipStartupVideoReadyPatch(uint8_t* base,
                                          uint32_t update_guest,
                                          std::string_view reason) {
  return ApplyRefiiStartupVideoReadyPatchInternal(
      base, update_guest, reason, true);
}

bool ApplyRefiiVfsStartupVideoReadyPatch(uint8_t* base,
                                         uint32_t update_guest,
                                         std::string_view reason) {
  return ApplyRefiiStartupVideoReadyPatchInternal(
      base, update_guest, reason, false);
}

bool ApplyRefiiStartupReadyPollObjectPatch(uint8_t* base,
                                           uint32_t object_guest,
                                           std::string_view reason) {
  if (!SkipStartupVideosEnabled() || base == nullptr || object_guest < 0x10000) {
    return false;
  }

  const uint32_t before_ready = LoadGuestU8(base, object_guest + 5);
  const uint32_t before_pending = LoadGuestU32(base, object_guest + 52);
  StoreGuestU8(base, object_guest + 5, 1);
  const uint32_t after_ready = LoadGuestU8(base, object_guest + 5);
  const uint32_t after_pending = LoadGuestU32(base, object_guest + 52);

  bool should_log = false;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    trace.last_ready_patch_object = object_guest;
    trace.last_ready_patch_reason = std::string(reason);
    trace.last_ready_patch_at = std::chrono::steady_clock::now();
    should_log = !trace.skip_ready_patch_emitted;
    trace.skip_ready_patch_emitted = true;
  }

  if (should_log && VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS skip startup-video ready object patch reason={} "
                "object=0x{:08X} before_ready={} before_pending=0x{:08X} "
                "after_ready={} after_pending=0x{:08X} object_words={}",
                reason,
                object_guest,
                before_ready,
                before_pending,
                after_ready,
                after_pending,
                FormatGuestWordsIfPointer(base, object_guest, 16));
  }
  return true;
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

void RecordRefiiPostVideoWaitSnapshot(uint8_t* base,
                                      uint32_t owner_guest,
                                      uint32_t lr,
                                      uint32_t result,
                                      int64_t wait_ms) {
  if (owner_guest == 0) {
    return;
  }
  const uint32_t handoff_guest = LoadGuestU32(base, owner_guest + 4);
  if (handoff_guest == 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  trace.last_wait_owner = owner_guest;
  trace.last_wait_handoff = handoff_guest;
  trace.last_wait_signal_handle = LoadGuestU32(base, handoff_guest + 456);
  trace.last_wait_object = handoff_guest + 460;
  trace.last_wait_handle = LoadGuestU32(base, trace.last_wait_object + 128);
  if (lr != 0) {
    trace.last_wait_lr = lr;
  }
  trace.last_wait_result = result;
  if (wait_ms >= 0) {
    trace.last_wait_ms = wait_ms;
  }
  trace.last_wait_at = std::chrono::steady_clock::now();
}

void RecordRefiiPostVideoWaitResult(uint32_t lr,
                                    uint32_t result,
                                    int64_t wait_ms,
                                    uint32_t wait_object_guest,
                                    uint32_t wait_handle_guest) {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  const bool matches_handoff_wait =
      (wait_object_guest != 0 &&
       (wait_object_guest == trace.last_wait_object ||
        wait_object_guest == trace.last_wait_handle ||
        wait_object_guest == trace.last_wait_signal_handle)) ||
      (wait_handle_guest != 0 &&
       (wait_handle_guest == trace.last_wait_object ||
        wait_handle_guest == trace.last_wait_handle ||
        wait_handle_guest == trace.last_wait_signal_handle));
  if (!matches_handoff_wait) {
    return;
  }
  trace.last_wait_lr = lr;
  trace.last_wait_result = result;
  trace.last_wait_ms = wait_ms;
  trace.last_wait_at = std::chrono::steady_clock::now();
}

void RecordRefiiPostVideoFrameHandoffSnapshot(uint8_t* base,
                                              uint32_t owner_guest,
                                              uint32_t lr) {
  if (base == nullptr || !LooksLikeGuestPointer(owner_guest)) {
    return;
  }
  const uint32_t handoff_guest = LoadGuestU32(base, owner_guest + 4);
  if (!LooksLikeGuestPointer(handoff_guest)) {
    return;
  }

  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  trace.last_frame_owner = owner_guest;
  trace.last_frame_handoff = handoff_guest;
  trace.last_frame_lr = lr;
  trace.last_frame_flag233 = LoadGuestU8(base, handoff_guest + 233);
  trace.last_frame_flag244 = LoadGuestU8(base, handoff_guest + 244);
  trace.last_frame_at = std::chrono::steady_clock::now();

  // Some generated paths never call the BIK object finalizer hook before the
  // frontend frame handoff exists. Treat that as a diagnostic hint only; it is
  // not a real BIK finalizer and must not unlock the menu handoff by itself.
  if (RefiiStartupVideoSlotReady(trace.microsoft) &&
      RefiiStartupVideoSlotReady(trace.lionhead)) {
    if (!trace.microsoft.finalizer_seen) {
      trace.microsoft.finalizer_seen = true;
      trace.microsoft.finalizer_synthetic = true;
      trace.microsoft.last_finalizer_result = 0;
      trace.microsoft.finalizer_at = trace.last_frame_at;
    }
    if (!trace.lionhead.finalizer_seen) {
      trace.lionhead.finalizer_seen = true;
      trace.lionhead.finalizer_synthetic = true;
      trace.lionhead.last_finalizer_result = 0;
      trace.lionhead.finalizer_at = trace.last_frame_at;
    }
  }
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

bool RefiiPostVideoEventMatchesHandoffWait(uint32_t guest) {
  if (guest == 0) {
    return false;
  }
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  const auto& trace = RefiiPostVideoHandoffTraceState();
  return guest == trace.last_wait_signal_handle ||
         guest == trace.last_wait_object || guest == trace.last_wait_handle;
}

bool RefiiStartupVideoSlotReady(const RefiiStartupVideoItemTrace& slot) {
  if (slot.provider.empty()) {
    return false;
  }
  const bool size_matches =
      slot.expected_size == 0 || slot.last_size_result == 0 ||
      static_cast<uint64_t>(slot.last_size_result) == slot.expected_size;
  return slot.read_complete && slot.cursor_seen && slot.size_seen && size_matches;
}

bool RefiiStartupVideosRealFinalizersSeenLocked(
    const RefiiPostVideoHandoffTrace& trace);
bool RefiiStartupVideosHaveAttachedUnretiredNativeObjectLocked(
    const RefiiPostVideoHandoffTrace& trace);

bool RefiiStartupVideosFinalizedForPostVideoHandoffLocked(
    const RefiiPostVideoHandoffTrace& trace) {
  return RefiiStartupVideosRealFinalizersSeenLocked(trace) &&
         !RefiiStartupVideosHaveAttachedUnretiredNativeObjectLocked(trace);
}

bool RefiiStartupVideosRealFinalizersSeenLocked(
    const RefiiPostVideoHandoffTrace& trace) {
  return RefiiStartupVideoSlotReady(trace.microsoft) &&
         RefiiStartupVideoSlotReady(trace.lionhead) &&
         trace.microsoft.finalizer_seen && trace.lionhead.finalizer_seen &&
         !trace.microsoft.finalizer_synthetic &&
         !trace.lionhead.finalizer_synthetic;
}

bool RefiiStartupVideoNativeObjectRetiredLocked(
    const RefiiStartupVideoItemTrace& slot) {
  if (!RefiiStartupVideoSlotReady(slot) || !slot.finalizer_seen ||
      slot.finalizer_synthetic) {
    return false;
  }

  // The generated startup loop can invoke the BIK finalizer while the object
  // still has its native buffered stream attached. Do not unlock the frontend
  // handoff until that object has actually released the stream; otherwise the
  // Lionhead/Xbox videos can be interrupted or overlap the frontend load.
  if (slot.object_guest != 0 && slot.object_stream_attached_seen &&
      !slot.object_stream_cleared_seen && slot.last_object_stream_after != 0) {
    return false;
  }

  return true;
}

bool RefiiStartupVideosNativeObjectsRetiredLocked(
    const RefiiPostVideoHandoffTrace& trace) {
  return RefiiStartupVideoNativeObjectRetiredLocked(trace.microsoft) &&
         RefiiStartupVideoNativeObjectRetiredLocked(trace.lionhead);
}

bool RefiiStartupVideosHaveAttachedUnretiredNativeObjectLocked(
    const RefiiPostVideoHandoffTrace& trace) {
  auto unretired = [](const RefiiStartupVideoItemTrace& slot) {
    return slot.object_guest != 0 && slot.object_stream_attached_seen &&
           !slot.object_stream_cleared_seen && slot.last_object_stream_after != 0;
  };
  return unretired(trace.microsoft) || unretired(trace.lionhead);
}

bool RefiiStartupVideosReadyForMenuHandoff();
bool RefiiStartupVideosFinalizedForMenuHandoff();
bool RefiiStartupVideosSettledForMenuHandoff();
bool RefiiStartupVideosSettledForPostVideoHandoffLocked(
    const RefiiPostVideoHandoffTrace& trace,
    int64_t* settled_age_ms_out);
bool RefiiMainMenuTraceIsTextureOwnedButNotOpened();
bool RefiiMainMenuTraceNeedsTexturePrime();
bool RefiiMainMenuTraceNeedsFrontendHandoff();

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
      "frame={} frame_slowest={} frame_slowest_lr=0x{:08X} "
      "frame_slowest_result=0x{:08X} frame_slowest_elapsed_ms={} "
      "frame_slowest_age_ms={} "
      "slowest={} slowest_lr=0x{:08X} slowest_result=0x{:08X} "
      "slowest_elapsed_ms={} slowest_age_ms={}",
      trace.last_stage.empty() ? "none" : trace.last_stage,
      trace.last_stage_lr,
      trace.last_stage_result,
      trace.last_stage_elapsed_ms,
      age_ms(trace.last_stage_at),
      trace.stage_frame,
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

std::string RefiiPostVideoSlowFrameNextBoundary() {
  std::string frame_slowest_stage;
  int64_t frame_slowest_elapsed_ms = -1;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    const auto& trace = RefiiPostVideoHandoffTraceState();
    frame_slowest_stage = trace.frame_slowest_stage;
    frame_slowest_elapsed_ms = trace.frame_slowest_stage_elapsed_ms;
  }

  if (!frame_slowest_stage.empty() && frame_slowest_elapsed_ms >= 250) {
    return fmt::format("slow_child_stage_{}", frame_slowest_stage);
  }
  if (RefiiStartupVideosReadyForMenuHandoff() &&
      RefiiMainMenuTraceIsTextureOwnedButNotOpened()) {
    return "handoff_timer_waiting_duration";
  }
  return "sub_82388A18_or_generated_event_wait";
}

bool RefiiStartupVideosReadyForMenuHandoff() {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  const auto& trace = RefiiPostVideoHandoffTraceState();
  return RefiiStartupVideosSettledForPostVideoHandoffLocked(trace, nullptr);
}

bool RefiiStartupVideosFinalizedForMenuHandoff() {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  const auto& trace = RefiiPostVideoHandoffTraceState();
  return RefiiStartupVideosFinalizedForPostVideoHandoffLocked(trace);
}

bool RefiiStartupVideosSettledForMenuHandoff() {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  const auto& trace = RefiiPostVideoHandoffTraceState();
  return RefiiStartupVideosSettledForPostVideoHandoffLocked(trace, nullptr);
}

bool IsStartupLuaHostPath(std::string_view path) {
  const std::string normalized = NormalizeLooseAssetKey(path);
  return normalized.find("luasource/scripts/startup/") != std::string::npos &&
         normalized.size() >= 4 &&
         normalized.substr(normalized.size() - 4) == ".lua";
}

struct ActiveRefiiStreamSnapshot {
  uint32_t guest = 0;
  uint32_t ref_count = 0;
  std::string owner;
  std::string provider;
  std::string path;
  uint64_t expected_size = 0;
  uint64_t size = 0;
  uint64_t position = 0;
  uint64_t read_calls = 0;
  uint64_t read_bytes = 0;
};

std::vector<ActiveRefiiStreamSnapshot> SnapshotActiveRefiiHostStreams(
    uint8_t* base) {
  std::vector<std::pair<uint32_t, std::shared_ptr<RefiiHostStreamRecord>>>
      records;
  {
    std::lock_guard<std::mutex> lock(RefiiHostStreamRecordsMutex());
    records.reserve(RefiiHostStreamRecords().size());
    for (const auto& [guest, record] : RefiiHostStreamRecords()) {
      records.emplace_back(guest, record);
    }
  }

  uint32_t microsoft_source = 0;
  uint32_t lionhead_source = 0;
  uint32_t microsoft_wrapped = 0;
  uint32_t lionhead_wrapped = 0;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    const auto& trace = RefiiPostVideoHandoffTraceState();
    microsoft_source = trace.microsoft.source_stream_guest;
    lionhead_source = trace.lionhead.source_stream_guest;
    microsoft_wrapped = trace.microsoft.wrapped_stream_guest;
    lionhead_wrapped = trace.lionhead.wrapped_stream_guest;
  }

  std::vector<ActiveRefiiStreamSnapshot> snapshots;
  snapshots.reserve(records.size());
  for (const auto& [guest, host_record] : records) {
    if (host_record == nullptr) {
      continue;
    }

    ActiveRefiiStreamSnapshot snapshot;
    snapshot.guest = guest;
    {
      std::lock_guard<std::mutex> lock(host_record->mutex);
      snapshot.path = host_record->path.string();
      snapshot.size = host_record->size;
      snapshot.position = host_record->position;
      snapshot.read_calls = host_record->read_calls;
      snapshot.read_bytes = host_record->read_bytes;
    }

    LooseStreamRecord loose_record;
    std::string match;
    if (FindLooseStreamRecord(base, guest, &loose_record, &match)) {
      snapshot.provider = NormalizeLooseAssetKey(loose_record.provider_name);
      snapshot.expected_size = loose_record.expected_size;
    }
    if (snapshot.provider.empty()) {
      snapshot.provider = NormalizeLooseAssetKey(snapshot.path);
    }

    if (guest == microsoft_source) {
      snapshot.owner = "startup_bik_raw_source:microsoft_logo";
    } else if (guest == lionhead_source) {
      snapshot.owner = "startup_bik_raw_source:lionhead_logo";
    } else if (guest == microsoft_wrapped || guest == lionhead_wrapped) {
      snapshot.owner = "startup_bik_buffered_alias";
    } else if (IsStartupLuaHostPath(snapshot.path) ||
               IsStartupLuaAssetName(snapshot.provider)) {
      snapshot.owner = "startup_lua";
    } else {
      snapshot.owner = "refii_owned";
    }

    if (base != nullptr && guest >= 0x10000 && guest < 0x90000000) {
      snapshot.ref_count = LoadGuestU32(base, guest + 4);
    }
    snapshots.push_back(std::move(snapshot));
  }

  std::sort(snapshots.begin(),
            snapshots.end(),
            [](const ActiveRefiiStreamSnapshot& a,
               const ActiveRefiiStreamSnapshot& b) {
              if (a.owner != b.owner) {
                return a.owner < b.owner;
              }
              return a.guest < b.guest;
            });
  return snapshots;
}

std::string FormatActiveRefiiHostStreamAudit(
    const std::vector<ActiveRefiiStreamSnapshot>& streams) {
  if (streams.empty()) {
    return "none";
  }

  constexpr size_t kMaxAuditStreams = 16;
  std::vector<std::string> items;
  items.reserve(std::min(streams.size(), kMaxAuditStreams));
  for (size_t i = 0; i < streams.size() && i < kMaxAuditStreams; ++i) {
    const auto& stream = streams[i];
    items.push_back(fmt::format(
        "0x{:08X}:owner={} ref={} read={}/{} calls={} pos={} size={} "
        "provider={} path={}",
        stream.guest,
        stream.owner,
        stream.ref_count,
        stream.read_bytes,
        stream.expected_size != 0 ? stream.expected_size : stream.size,
        stream.read_calls,
        stream.position,
        stream.size,
        stream.provider,
        stream.path));
  }
  if (streams.size() > kMaxAuditStreams) {
    items.push_back(fmt::format("...{} more", streams.size() - kMaxAuditStreams));
  }
  std::string joined;
  for (size_t i = 0; i < items.size(); ++i) {
    if (i != 0) {
      joined += " | ";
    }
    joined += items[i];
  }
  return joined;
}

void MaybeLogRefiiActiveStreamCompletionAudit(uint32_t lr,
                                              std::string_view context,
                                              uint8_t* base,
                                              bool force = false) {
  if (!VfsTracingEnabled() || !RefiiStartupVideosReadyForMenuHandoff() ||
      !RefiiMainMenuTraceIsTextureOwnedButNotOpened()) {
    return;
  }

  bool should_log = force;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    const auto now = std::chrono::steady_clock::now();
    if (!trace.active_stream_audit_emitted) {
      trace.active_stream_audit_emitted = true;
      trace.last_active_stream_audit_at = now;
      should_log = true;
    } else if (!force && trace.last_active_stream_audit_at.time_since_epoch()
                              .count() != 0) {
      const int64_t age_ms =
          std::chrono::duration_cast<std::chrono::milliseconds>(
              now - trace.last_active_stream_audit_at)
              .count();
      if (age_ms >= 5000) {
        trace.last_active_stream_audit_at = now;
        should_log = true;
      }
    }
  }
  if (!should_log) {
    return;
  }

  const auto streams = SnapshotActiveRefiiHostStreams(base);
  const size_t bik_sources =
      std::count_if(streams.begin(), streams.end(), [](const auto& stream) {
        return stream.owner.find("startup_bik_raw_source") == 0;
      });
  const size_t lua_streams =
      std::count_if(streams.begin(), streams.end(), [](const auto& stream) {
        return stream.owner == "startup_lua";
      });

  const char* next = "no_vfs_streams_blocking";
  if (bik_sources != 0) {
    next = "bik_sources_still_retained";
  } else if (lua_streams != 0) {
    next = "lua_streams_still_retained";
  } else if (!streams.empty()) {
    next = "active_streams_blocking_completion";
  }

  REXLOG_INFO("Refii VFS active stream completion audit context={} "
              "lr=0x{:08X} active={} bik_sources={} startup_lua={} "
              "next={} streams=[{}] main_menu=[{}] post_video=[{}]",
              context,
              lr,
              streams.size(),
              bik_sources,
              lua_streams,
              next,
              FormatActiveRefiiHostStreamAudit(streams),
              FormatRefiiMainMenuBlockerTraceForLog(),
              FormatRefiiPostVideoHandoffTraceForLog());
  LogRefiiVfsChainVerdict("lifecycle",
                          lr,
                          "startup_assets",
                          "",
                          "",
                          "Refii_host_stream",
                          "ActiveRefiiStreamCompletionAudit",
                          "TraceOnce",
                          next);

  if (streams.empty()) {
    bool emit_no_active_verdict = false;
    {
      std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
      auto& trace = RefiiPostVideoHandoffTraceState();
      if (!trace.texture_no_active_stream_verdict_emitted) {
        trace.texture_no_active_stream_verdict_emitted = true;
        emit_no_active_verdict = true;
      }
    }
    if (emit_no_active_verdict) {
      LogRefiiVfsChainVerdict(
          "request_manager_hash",
          lr,
          "menu_texture_refii_owned",
          "",
          "",
          "RefiiRequestManagerHashEntry",
          "texture_hashes_ready_no_vfs_open_no_active_streams",
          "TraceOnce",
          "texture_consumer_open_missing_after_vfs_lifecycle_clear");
    }
  }
}

bool MaybeReleaseCompletedStartupBikSource(PPCContext& source_ctx,
                                           uint8_t* base,
                                           uint32_t stream_guest,
                                           std::string_view context) {
  if (base == nullptr || stream_guest == 0) {
    return false;
  }

  std::string provider;
  uint32_t source_stream = 0;
  uint32_t wrapped_stream = 0;
  uint32_t read_completed = 0;
  uint32_t read_requested = 0;
  bool should_release = false;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    RefiiStartupVideoItemTrace* slot =
        SelectStartupVideoTraceSlotByStreamLocked(trace, base, stream_guest);
    if (slot == nullptr || slot->source_release_attempted ||
        !slot->read_complete || !slot->cursor_seen || !slot->size_seen ||
        slot->source_stream_guest == 0 || slot->wrapped_stream_guest == 0) {
      return false;
    }

    const uint32_t pending =
        LoadGuestU32(base, slot->wrapped_stream_guest + 36);
    if (pending != 0) {
      return false;
    }

    slot->source_release_attempted = true;
    slot->source_release_verdict_emitted = true;
    provider = slot->provider;
    source_stream = slot->source_stream_guest;
    wrapped_stream = slot->wrapped_stream_guest;
    read_completed = slot->last_read_completed;
    read_requested = slot->last_read_requested;
    should_release = true;
  }

  if (!should_release || !IsRefiiHostDiskStream(base, source_stream)) {
    return false;
  }

  const uint32_t ref_before = LoadGuestU32(base, source_stream + 4);
  ReleaseGuestObject(source_ctx, base, source_stream);
  const bool still_active = FindRefiiHostStreamRecord(source_stream) != nullptr;
  const uint32_t ref_after =
      still_active ? LoadGuestU32(base, source_stream + 4) : 0;

  if (VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS BIK full-prebuffer source release context={} "
                "provider={} source=0x{:08X} wrapped=0x{:08X} "
                "ref_before={} ref_after={} still_active={} read={}/{} "
                "wrapped_shape=[{}] next={}",
                context,
                provider,
                source_stream,
                wrapped_stream,
                ref_before,
                ref_after,
                still_active,
                read_completed,
                read_requested,
                FormatBikBufferedNativeShape(base, wrapped_stream),
                still_active ? "active_streams_blocking_completion"
                             : "BIK_finalizer_or_texture_open");
    LogRefiiVfsChainVerdict("lifecycle",
                            source_ctx.lr,
                            provider,
                            provider,
                            provider,
                            "native_buffered_stream",
                            "BIK_full_prebuffer_source_release",
                            "KeepHooked",
                            still_active ? "active_streams_blocking_completion"
                                         : "BIK_finalizer_or_texture_open");
    MaybeLogRefiiActiveStreamCompletionAudit(
        source_ctx.lr, "BIK_full_prebuffer_source_release", base, true);
  }
  return !still_active;
}

void MaybeLogRefiiVideosReadyTextureOpenGap(uint32_t lr,
                                            std::string_view context,
                                            uint8_t* base) {
  if (!VfsTracingEnabled() || !RefiiMainMenuTraceIsTextureOwnedButNotOpened()) {
    return;
  }

  bool should_log = false;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    if (trace.videos_ready_texture_open_gap_emitted ||
        !RefiiStartupVideoSlotReady(trace.microsoft) ||
        !RefiiStartupVideoSlotReady(trace.lionhead)) {
      return;
    }
    trace.videos_ready_texture_open_gap_emitted = true;
    should_log = true;
  }

  if (!should_log) {
    return;
  }

  REXLOG_INFO("Refii VFS videos-ready texture-open gap context={} "
              "lr=0x{:08X} targeted_hashes={} managers=[{}] "
              "hash_audit=[{}] last_op=[{}] main_menu=[{}] post_video=[{}]",
              context,
              lr,
              TargetedRequestManagerHashContributionCount(),
              FormatKnownRequestManagerGuestsForLog(),
              FormatTargetedTextureHashTableAudit(base),
              FormatRefiiLastVfsOperationForLog(),
              FormatRefiiMainMenuBlockerTraceForLog(),
              FormatRefiiPostVideoHandoffTraceForLog());
  LogRefiiVfsChainVerdict("request_manager_hash",
                          lr,
                          "menu_texture_refii_owned",
                          "",
                          "",
                          "RefiiRequestManagerHashEntry",
                          "StartupVideosReady_TextureOpenMissing",
                          "TraceOnce",
                          "texture_consumer_open_missing_after_videos_ready");
  MaybeLogRefiiActiveStreamCompletionAudit(lr, context, base, true);
}

void MaybeLogRefiiStartupVideoReadyNoFinalizerBlocker(
    uint32_t lr,
    std::string_view context,
    uint8_t* base) {
  if (!kTraceFrontendHandoffDiagnostics || !VfsTracingEnabled()) {
    return;
  }

  bool should_log = false;
  int64_t ready_age_ms = -1;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    if (trace.ready_no_finalizer_verdict_emitted ||
        !RefiiStartupVideoSlotReady(trace.microsoft) ||
        !RefiiStartupVideoSlotReady(trace.lionhead) ||
        (trace.microsoft.finalizer_seen && trace.lionhead.finalizer_seen)) {
      return;
    }

    const auto now = std::chrono::steady_clock::now();
    const auto ready_at =
        std::max(trace.microsoft.size_at, trace.lionhead.size_at);
    if (ready_at.time_since_epoch().count() != 0) {
      ready_age_ms =
          std::chrono::duration_cast<std::chrono::milliseconds>(now - ready_at)
              .count();
    }
    if (ready_age_ms >= 1000) {
      trace.ready_no_finalizer_verdict_emitted = true;
      should_log = true;
    }
  }

  if (!should_log) {
    return;
  }

  REXLOG_INFO("Refii VFS startup-video ready-no-finalizer blocker "
              "context={} lr=0x{:08X} ready_age_ms={} last_op=[{}] "
              "main_menu=[{}] post_video=[{}]",
              context,
              lr,
              ready_age_ms,
              FormatRefiiLastVfsOperationForLog(),
              FormatRefiiMainMenuBlockerTraceForLog(),
              FormatRefiiPostVideoHandoffTraceForLog());
  LogRefiiVfsChainVerdict(
      "native_shaped_stream",
      lr,
      "startup_bik_pair",
      "art/videos/microsoft_logo.bik|art/videos/lionhead_logo.bik",
      "art/videos/microsoft_logo.bik|art/videos/lionhead_logo.bik",
      "native_buffered_stream",
      "BIK_ready_no_finalizer_or_frontend_consumer",
      "TraceOnce",
      base != nullptr && RefiiMainMenuTraceIsTextureOwnedButNotOpened()
          ? "texture_hash_installed_waiting_for_consumer_open"
          : "startup_video_loop_or_finalizer");
  MaybeLogRefiiActiveStreamCompletionAudit(lr, context, base, true);
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
                     "size={}/0x{:08X} read={}/{} queued={} complete={} "
                     "cursor={} pos=0x{:08X}:0x{:08X} status=0x{:08X} "
                     "finalizer={} finalizer_source={} object_method={} "
                     "method_lr=0x{:08X} "
                     "object_stream=0x{:08X}->0x{:08X} "
                     "object_attached={} object_cleared={} ready={} ages_ms="
                     "read:{} first_cursor:{} cursor:{} size:{} finalizer:{} "
                     "object:{}",
                     slot.provider,
                     slot.object_guest,
                     slot.source_stream_guest,
                     slot.wrapped_stream_guest,
                     slot.expected_size,
                     slot.last_size_result,
                     slot.last_read_completed,
                     slot.last_read_requested,
                     slot.read_queued,
                     slot.read_complete,
                     slot.cursor_seen,
                     slot.last_position_high,
                     slot.last_position_low,
                     slot.last_status_result,
                     slot.finalizer_seen,
                     slot.finalizer_synthetic ? "synthetic_frame_handoff"
                                              : "native_bik_object",
                     slot.last_object_method.empty() ? "none"
                                                     : slot.last_object_method,
                     slot.last_object_method_lr,
                     slot.last_object_stream_before,
                     slot.last_object_stream_after,
                     slot.object_stream_attached_seen,
                     slot.object_stream_cleared_seen,
                     RefiiStartupVideoSlotReady(slot),
                     age_ms(slot.read_complete_at),
                     age_ms(slot.first_cursor_at),
                     age_ms(slot.cursor_at),
                     age_ms(slot.size_at),
                     age_ms(slot.finalizer_at),
                     age_ms(slot.object_method_at));
}

std::string FormatRefiiMainMenuBlockerTraceForLog();

}  // namespace detail
}  // namespace refii::vfs
