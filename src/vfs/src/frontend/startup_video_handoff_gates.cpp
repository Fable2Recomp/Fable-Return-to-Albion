#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

bool MaybeSeedRefiiGeneratedRenderReadyPayload(const PPCContext& source_ctx,
                                               uint8_t* base,
                                               uint32_t ready_object_guest) {
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !LooksLikeGuestPointer(ready_object_guest) ||
      !IsGeneratedPostVideoRenderReadyPollLr(source_ctx.lr)) {
    return false;
  }

  if (LoadGuestU32(base, ready_object_guest + 4) != 0 ||
      !RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload()) {
    return false;
  }

  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    if (trace.post_video_generated_ready_payload_seeded ||
        !RefiiStartupVideosSettledForPostVideoHandoffLocked(trace, nullptr)) {
      return false;
    }
    trace.post_video_generated_ready_payload_seeded = true;
    trace.post_video_generated_ready_object = ready_object_guest;
    trace.post_video_generated_ready_lr = source_ctx.lr;
    trace.post_video_generated_ready_at = std::chrono::steady_clock::now();
  }

  constexpr std::string_view kReadyAsset =
      "art/gui/frames/framedetailmap.tex";
  constexpr uint32_t kGeneratedReadyKind = 0x83497588u;
  PPCContext alloc_ctx = source_ctx;
  const uint32_t string_object_guest = CallLhMalloc(alloc_ctx, base, 32);
  const uint32_t string_data_guest = CallLhMalloc(
      alloc_ctx, base, static_cast<uint32_t>(kReadyAsset.size() + 1));
  if (string_object_guest == 0 || string_data_guest == 0) {
    REXLOG_WARN("Refii VFS generated ready payload seed allocation failed "
                "lr=0x{:08X} object=0x{:08X} string_object=0x{:08X} "
                "string_data=0x{:08X}",
                source_ctx.lr,
                ready_object_guest,
                string_object_guest,
                string_data_guest);
    return false;
  }

  for (size_t index = 0; index < kReadyAsset.size(); ++index) {
    StoreGuestU8(base,
                 string_data_guest + static_cast<uint32_t>(index),
                 static_cast<uint8_t>(kReadyAsset[index]));
  }
  StoreGuestU8(base,
               string_data_guest + static_cast<uint32_t>(kReadyAsset.size()),
               0);

  PPCContext string_ctx = source_ctx;
  string_ctx.r3.u32 = string_object_guest;
  string_ctx.r4.u32 = string_data_guest;
  string_ctx.r5.u32 = 0xFFFFFFFFu;
  string_ctx.lr = 0x823195A0u;
  ::sub_8222CED0(string_ctx, base);

  const uint32_t before_payload = LoadGuestU32(base, ready_object_guest + 4);
  PPCContext set_ctx = source_ctx;
  set_ctx.r3.u32 = ready_object_guest;
  set_ctx.r4.u32 = string_object_guest;
  set_ctx.r5.u32 = kGeneratedReadyKind;
  set_ctx.r6.u32 = 1;
  set_ctx.lr = 0x823195BCu;
  ::__imp__sub_82361200(set_ctx, base);
  const uint32_t after_payload = LoadGuestU32(base, ready_object_guest + 4);

  PPCContext dtor_ctx = source_ctx;
  dtor_ctx.r3.u32 = string_object_guest;
  dtor_ctx.lr = 0x823195C4u;
  ::sub_82214DD8(dtor_ctx, base);
  PPCContext free_ctx = source_ctx;
  CallLhFree(free_ctx, base, string_object_guest);
  CallLhFree(free_ctx, base, string_data_guest);

  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    trace.post_video_generated_ready_object = ready_object_guest;
    trace.post_video_generated_ready_payload = after_payload;
    trace.post_video_generated_ready_lr = source_ctx.lr;
    trace.post_video_generated_ready_string_object = string_object_guest;
    trace.post_video_generated_ready_at = std::chrono::steady_clock::now();
  }

  if (VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS generated render ready payload seed "
                "lr=0x{:08X} object=0x{:08X} payload=0x{:08X}->0x{:08X} "
                "asset={} string_object=0x{:08X} result=0x{:08X} "
                "handoff=[{}] main_menu=[{}]",
                source_ctx.lr,
                ready_object_guest,
                before_payload,
                after_payload,
                kReadyAsset,
                string_object_guest,
                set_ctx.r3.u32,
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  LogRefiiVfsChainVerdict("frontend_post_video",
                          source_ctx.lr,
                          kReadyAsset,
                          kReadyAsset,
                          kReadyAsset,
                          "GeneratedRenderReadyPayload",
                          "sub_82361128_empty_payload",
                          after_payload != 0 ? "Opened" : "OpenFailed",
                          "generated_sub_82319340_native_branch");

  return after_payload != 0;
}

bool RefiiMainMenuTextureKnownOwnedByVfsIndex() {
  if (!RefiiLooseAssetBackendEnabled()) {
    return false;
  }

  const auto* owned_hashes =
      GetRefiiVfsRuntimeOwnedHashes(GetLooseAssetRoot());
  return owned_hashes != nullptr && owned_hashes->contains(0x73BCB48Fu);
}

void MaybeLogRefiiPostVideoTextureOwnedNoOpenBlocker(
    uint32_t lr,
    std::string_view context,
    uint8_t* base = nullptr);
void MaybeLogRefiiFrontendConsumerMissingVerdict(uint32_t lr,
                                                 std::string_view context,
                                                 uint8_t* base = nullptr);

bool ShouldLogRefiiPostVideoTextureOwnedNoOpenContext(
    std::string_view context) {
  static std::mutex mutex;
  static bool initial_emitted = false;
  static bool frame_emitted = false;
  static bool completion_gate_emitted = false;

  std::lock_guard<std::mutex> lock(mutex);
  if (context == "PostVideoFrame_82389210") {
    if (frame_emitted) {
      return false;
    }
    frame_emitted = true;
    return true;
  }
  if (context == "StartupVideoCompletionGate_82377DC8") {
    if (completion_gate_emitted) {
      return false;
    }
    completion_gate_emitted = true;
    return true;
  }
  if (initial_emitted) {
    return false;
  }
  initial_emitted = true;
  return true;
}

void MaybeLogRefiiPostVideoHashOnlyBlocker(uint32_t lr,
                                           std::string_view context,
                                           uint8_t* base = nullptr) {
  if (!kTraceFrontendHandoffDiagnostics || !VfsTracingEnabled() ||
      !RefiiStartupVideosReadyForMenuHandoff()) {
    return;
  }

  if (RefiiMainMenuTraceIsHashOnly() &&
      ShouldLogRepeatedVfsTraceOperation("PostVideoHashOnlyBlocker", 12)) {
    REXLOG_INFO("Refii VFS post-video hash-only blocker context={} lr=0x{:08X} "
                "last_op=[{}] main_menu=[{}] post_video=[{}]",
                context,
                lr,
                FormatRefiiLastVfsOperationForLog(),
                FormatRefiiMainMenuBlockerTraceForLog(),
                FormatRefiiPostVideoHandoffTraceForLog());
    LogRefiiVfsChainVerdict("request_manager_hash",
                            lr,
                            "menu_texture_hashes",
                            "",
                            "",
                            "RefiiRequestManagerHashEntry",
                            "PostVideoHashOnlyBlocker",
                            "TraceOnce",
                            "texture_hash_refii_owned_registration");
  }
  MaybeLogRefiiPostVideoTextureOwnedNoOpenBlocker(lr, context, base);
  MaybeLogRefiiStartupVideoReadyNoFinalizerBlocker(lr, context, base);
}

void MaybeLogRefiiPostVideoTextureOwnedNoOpenBlocker(uint32_t lr,
                                                     std::string_view context,
                                                     uint8_t* base) {
  if (!kTraceFrontendHandoffDiagnostics || !VfsTracingEnabled() ||
      !RefiiStartupVideosReadyForMenuHandoff() ||
      !RefiiMainMenuTraceIsTextureOwnedButNotOpened() ||
      !ShouldLogRefiiPostVideoTextureOwnedNoOpenContext(context)) {
    return;
  }

  REXLOG_INFO("Refii VFS post-video texture-owned-no-open blocker context={} "
              "lr=0x{:08X} targeted_hashes={} managers=[{}] hash_audit=[{}] "
              "last_op=[{}] main_menu=[{}] post_video=[{}]",
              context,
              lr,
              TargetedRequestManagerHashContributionCount(),
              FormatKnownRequestManagerGuestsForLog(),
              FormatTargetedTextureHashTableAudit(base),
              FormatRefiiLastVfsOperationForLog(),
              FormatRefiiMainMenuBlockerTraceForLog(),
              FormatRefiiPostVideoHandoffTraceForLog());
  LogRefiiVfsChainVerdict(
      "request_manager_hash",
      lr,
      "menu_texture_refii_owned",
      "",
      "",
      "RefiiRequestManagerHashEntry",
      "PostVideoTextureOwnedNoOpenBlocker",
      "TraceOnce",
      "sub_82378618_consumer_verdict_or_ready_gate");
  MaybeLogRefiiFrontendConsumerMissingVerdict(lr, context, base);
}

void MaybeLogRefiiFrontendConsumerMissingVerdict(uint32_t lr,
                                                 std::string_view context,
                                                 uint8_t* base) {
  if (!kTraceFrontendHandoffDiagnostics || !VfsTracingEnabled() ||
      !RefiiMainMenuTraceIsTextureOwnedButNotOpened() ||
      (!RefiiStartupVideosReadyForMenuHandoff() && !SkipStartupVideosEnabled())) {
    return;
  }

  std::string verdict;
  std::string next_boundary;
  bool should_log = false;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    if (trace.frontend_consumer_seen ||
        trace.frontend_consumer_missing_verdict_emitted) {
      return;
    }

    if (trace.last_ready_poll_at.time_since_epoch().count() != 0 &&
        trace.last_ready_poll_result == 0) {
      verdict = "ready_gate_false_before_sub_82378618";
      next_boundary = "sub_822F3B20_ready_state";
    } else if (trace.last_ready_poll_at.time_since_epoch().count() != 0 &&
               trace.last_ready_poll_result != 0) {
      verdict = "ready_gate_true_waiting_sub_82378618";
      next_boundary = "sub_82377980_or_sub_82378618";
    } else {
      verdict = "handoff_timer_waiting_before_sub_82378618";
      next_boundary = "sub_82377DC8_completion_gate_or_handoff_duration";
    }
    trace.frontend_consumer_missing_verdict_emitted = true;
    should_log = true;
  }

  if (!should_log) {
    return;
  }

  REXLOG_INFO("Refii VFS frontend texture consumer missing verdict "
              "verdict={} context={} lr=0x{:08X} targeted_hashes={} "
              "managers=[{}] hash_audit=[{}] last_op=[{}] main_menu=[{}] "
              "post_video=[{}]",
              verdict,
              context,
              lr,
              TargetedRequestManagerHashContributionCount(),
              FormatKnownRequestManagerGuestsForLog(),
              FormatTargetedTextureHashTableAudit(base),
              FormatRefiiLastVfsOperationForLog(),
              FormatRefiiMainMenuBlockerTraceForLog(),
              FormatRefiiPostVideoHandoffTraceForLog());
  LogRefiiVfsChainVerdict("frontend_post_video",
                          lr,
                          "menu_texture_refii_owned",
                          "",
                          "",
                          "frontend_texture_descriptor_consumer",
                          verdict,
                          "TraceOnce",
                          next_boundary);
}

void MaybeLogRefiiStartupReadyGateTextureOpenVerdict(uint32_t lr,
                                                     std::string_view context,
                                                     uint8_t* base) {
  if (!kTraceFrontendHandoffDiagnostics || !VfsTracingEnabled() ||
      !RefiiMainMenuTraceIsTextureOwnedButNotOpened() ||
      (!RefiiStartupVideosReadyForMenuHandoff() && !SkipStartupVideosEnabled())) {
    return;
  }

  std::string verdict;
  std::string next_boundary;
  bool should_log = false;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    if (trace.last_ready_poll_at.time_since_epoch().count() != 0 &&
        trace.last_ready_poll_result == 0) {
      verdict = "ready_gate_false";
      next_boundary = "sub_822F3B20_ready_state";
      should_log = !trace.ready_gate_false_verdict_emitted;
      trace.ready_gate_false_verdict_emitted = true;
    } else if (trace.last_ready_poll_at.time_since_epoch().count() != 0 &&
               trace.last_ready_poll_result != 0) {
      verdict = "ready_gate_true_waiting_consumer";
      next_boundary = "RequestManager_FindStreamByHashEntry_or_ResourceManager_Open";
      should_log = !trace.ready_gate_true_verdict_emitted;
      trace.ready_gate_true_verdict_emitted = true;
    } else {
      verdict = "handoff_timer_waiting";
      next_boundary = "sub_82377DC8_completion_gate_or_handoff_duration";
      should_log = !trace.handoff_timer_verdict_emitted;
      trace.handoff_timer_verdict_emitted = true;
    }
  }

  if (!should_log) {
    return;
  }

  REXLOG_INFO("Refii VFS startup ready-gate verdict verdict={} context={} "
              "lr=0x{:08X} targeted_hashes={} managers=[{}] hash_audit=[{}] "
              "last_op=[{}] main_menu=[{}] post_video=[{}]",
              verdict,
              context,
              lr,
              TargetedRequestManagerHashContributionCount(),
              FormatKnownRequestManagerGuestsForLog(),
              FormatTargetedTextureHashTableAudit(base),
              FormatRefiiLastVfsOperationForLog(),
              FormatRefiiMainMenuBlockerTraceForLog(),
              FormatRefiiPostVideoHandoffTraceForLog());
  LogRefiiVfsChainVerdict("frontend_post_video",
                          lr,
                          "menu_texture_refii_owned",
                          "",
                          "",
                          "RefiiRequestManagerHashEntry",
                          verdict,
                          "TraceOnce",
                          next_boundary);
  MaybeLogRefiiFrontendConsumerMissingVerdict(lr, context, base);
}

constexpr int64_t kRefiiStartupReadyGateFalseDelayMs = 1000;
constexpr int64_t kRefiiStartupVideoMinimumReadyAgeMs = 4500;
constexpr int64_t kRefiiStartupVideoPostFinalizerHandoffDelayMs = 2000;

bool RefiiStartupVideoObservedSlotReady(
    const RefiiStartupVideoItemTrace& slot) {
  return slot.provider.empty() || RefiiStartupVideoSlotReady(slot);
}

bool RefiiStartupVideoObservedSlotSyntheticFinalized(
    const RefiiStartupVideoItemTrace& slot) {
  return slot.provider.empty() ||
         (slot.finalizer_seen && slot.finalizer_synthetic);
}

bool RefiiRequiredStartupVideosReadyLocked(
    const RefiiPostVideoHandoffTrace& trace) {
  return RefiiStartupVideoObservedSlotReady(trace.middleware) &&
         RefiiStartupVideoSlotReady(trace.microsoft) &&
         RefiiStartupVideoSlotReady(trace.lionhead);
}

std::chrono::steady_clock::time_point RefiiStartupVideosLatestReadyAtLocked(
    const RefiiPostVideoHandoffTrace& trace) {
  auto ready_at = std::max(trace.microsoft.size_at, trace.lionhead.size_at);
  if (!trace.middleware.provider.empty()) {
    ready_at = std::max(ready_at, trace.middleware.size_at);
  }
  return ready_at;
}

int64_t RefiiStartupVideoReadyAgeMsLocked(
    const RefiiPostVideoHandoffTrace& trace) {
  const auto ready_at = RefiiStartupVideosLatestReadyAtLocked(trace);
  if (ready_at.time_since_epoch().count() == 0) {
    return -1;
  }
  return std::chrono::duration_cast<std::chrono::milliseconds>(
             std::chrono::steady_clock::now() - ready_at)
      .count();
}

int64_t RefiiStartupVideoPlaybackAgeMsLocked(
    const RefiiStartupVideoItemTrace& slot) {
  if (slot.provider.empty() ||
      slot.first_cursor_at.time_since_epoch().count() == 0) {
    return -1;
  }
  return std::chrono::duration_cast<std::chrono::milliseconds>(
             std::chrono::steady_clock::now() - slot.first_cursor_at)
      .count();
}

int64_t RefiiStartupVideoCursorQuietAgeMsLocked(
    const RefiiStartupVideoItemTrace& slot) {
  if (slot.provider.empty() ||
      slot.cursor_at.time_since_epoch().count() == 0) {
    return -1;
  }
  return std::chrono::duration_cast<std::chrono::milliseconds>(
             std::chrono::steady_clock::now() - slot.cursor_at)
      .count();
}

bool RefiiStartupVideosMinimumPlaybackAgeReachedLocked(
    const RefiiPostVideoHandoffTrace& trace,
    int64_t minimum_per_video_ms,
    int64_t* playback_age_ms_out) {
  const int64_t middleware_playback_age_ms =
      trace.middleware.provider.empty()
          ? minimum_per_video_ms
          : RefiiStartupVideoPlaybackAgeMsLocked(trace.middleware);
  const int64_t microsoft_playback_age_ms =
      RefiiStartupVideoPlaybackAgeMsLocked(trace.microsoft);
  const int64_t lionhead_playback_age_ms =
      RefiiStartupVideoPlaybackAgeMsLocked(trace.lionhead);
  const int64_t playback_age_ms =
      middleware_playback_age_ms < 0 || microsoft_playback_age_ms < 0 ||
              lionhead_playback_age_ms < 0
          ? -1
          : std::min(
                std::min(middleware_playback_age_ms, microsoft_playback_age_ms),
                lionhead_playback_age_ms);
  if (playback_age_ms_out != nullptr) {
    *playback_age_ms_out = playback_age_ms;
  }
  return playback_age_ms >= minimum_per_video_ms;
}

bool RefiiStartupVideosMinimumCursorQuietAgeReachedLocked(
    const RefiiPostVideoHandoffTrace& trace,
    int64_t minimum_quiet_ms,
    int64_t* quiet_age_ms_out) {
  const int64_t middleware_quiet_age_ms =
      trace.middleware.provider.empty()
          ? minimum_quiet_ms
          : RefiiStartupVideoCursorQuietAgeMsLocked(trace.middleware);
  const int64_t microsoft_quiet_age_ms =
      RefiiStartupVideoCursorQuietAgeMsLocked(trace.microsoft);
  const int64_t lionhead_quiet_age_ms =
      RefiiStartupVideoCursorQuietAgeMsLocked(trace.lionhead);
  const int64_t quiet_age_ms =
      middleware_quiet_age_ms < 0 || microsoft_quiet_age_ms < 0 ||
              lionhead_quiet_age_ms < 0
          ? -1
          : std::min(std::min(middleware_quiet_age_ms, microsoft_quiet_age_ms),
                     lionhead_quiet_age_ms);
  if (quiet_age_ms_out != nullptr) {
    *quiet_age_ms_out = quiet_age_ms;
  }
  return quiet_age_ms >= minimum_quiet_ms;
}

bool RefiiStartupVideosTimedPlaybackCompleteForHandoffLocked(
    const RefiiPostVideoHandoffTrace& trace,
    int64_t* ready_age_ms_out = nullptr) {
  const int64_t configured_minimum_playback_ms =
      StartupVideoMinimumPlaybackMs();
  if (!RefiiRequiredStartupVideosReadyLocked(trace)) {
    if (ready_age_ms_out != nullptr) {
      *ready_age_ms_out = -1;
    }
    return false;
  }
  if (trace.last_bik_provider != trace.lionhead.provider) {
    if (ready_age_ms_out != nullptr) {
      *ready_age_ms_out = -1;
    }
    return false;
  }
  // Refii-backed startup BIK objects do not always reach the native BIK
  // finalizer hook. Treat repeated cursor activity followed by a quiet window
  // as the VFS-facing equivalent of native playback settling; the cvar only
  // raises that observed-playback floor for debugging.
  const int64_t minimum_playback_ms =
      std::max<int64_t>(kRefiiStartupVideoMinimumReadyAgeMs,
                        configured_minimum_playback_ms);
  const bool using_synthetic_finalizer =
      (!trace.middleware.provider.empty() &&
       trace.middleware.finalizer_synthetic) ||
      trace.microsoft.finalizer_synthetic || trace.lionhead.finalizer_synthetic;
  if (using_synthetic_finalizer) {
    const bool native_update_complete =
        trace.last_startup_update_complete_flag != 0 &&
        (trace.last_startup_update_vector_count == 0 ||
         trace.last_startup_update_video_index >=
             trace.last_startup_update_vector_count);
    const int64_t conservative_synthetic_fallback_ms =
        std::max<int64_t>(minimum_playback_ms, minimum_playback_ms * 2);
    if (!native_update_complete &&
        RefiiBootTraceAgeMs() < conservative_synthetic_fallback_ms) {
      if (ready_age_ms_out != nullptr) {
        *ready_age_ms_out = -1;
      }
      return false;
    }
  }
  const int64_t ready_age_ms = RefiiStartupVideoReadyAgeMsLocked(trace);
  if (ready_age_ms_out != nullptr) {
    *ready_age_ms_out = ready_age_ms;
  }
  const bool ready_age_reached = ready_age_ms >= minimum_playback_ms;
  const int64_t minimum_per_video_ms =
      std::max<int64_t>(kRefiiStartupVideoMinimumReadyAgeMs,
                        minimum_playback_ms / 2);
  const int64_t minimum_sequence_ms =
      std::max<int64_t>(minimum_playback_ms, minimum_playback_ms * 2);
  int64_t playback_age_ms = -1;
  const bool both_videos_played =
      RefiiStartupVideosMinimumPlaybackAgeReachedLocked(
          trace, minimum_per_video_ms, &playback_age_ms);
  if (ready_age_ms_out != nullptr && playback_age_ms >= 0) {
    *ready_age_ms_out = std::min(*ready_age_ms_out, playback_age_ms);
  }
  int64_t quiet_age_ms = -1;
  const bool both_videos_quiet =
      RefiiStartupVideosMinimumCursorQuietAgeReachedLocked(
          trace, kRefiiStartupVideoMinimumReadyAgeMs, &quiet_age_ms);
  if (ready_age_ms_out != nullptr && quiet_age_ms >= 0) {
    *ready_age_ms_out = std::min(*ready_age_ms_out, quiet_age_ms);
  }
  auto native_bik_object_pending = [](const RefiiStartupVideoItemTrace& slot) {
    return slot.object_guest != 0 && slot.object_stream_attached_seen &&
           !slot.object_stream_cleared_seen && slot.last_object_stream_after != 0 &&
           !slot.finalizer_synthetic;
  };
  if (native_bik_object_pending(trace.middleware) ||
      native_bik_object_pending(trace.microsoft) ||
      native_bik_object_pending(trace.lionhead)) {
    if (ready_age_ms_out != nullptr) {
      *ready_age_ms_out = -1;
    }
    return false;
  }
  return ready_age_reached && both_videos_played && both_videos_quiet &&
         RefiiBootTraceAgeMs() >= minimum_sequence_ms;
}

bool RefiiStartupNativePlaybackCompleteForHandoffLocked(
    const RefiiPostVideoHandoffTrace& trace) {
  if (trace.last_startup_update_guest == 0 &&
      RefiiStartupVideosNativeObjectsRetiredLocked(trace)) {
    return true;
  }
  if (trace.last_startup_update_complete_flag == 0) {
    return false;
  }
  const bool native_update_complete =
      trace.last_startup_update_vector_count == 0 ||
      trace.last_startup_update_video_index >=
          trace.last_startup_update_vector_count;
  return native_update_complete &&
         RefiiStartupVideosNativeObjectsRetiredLocked(trace);
}

bool RefiiStartupNativeReadyPollStillBlocksHandoffLocked(
    const RefiiPostVideoHandoffTrace& trace) {
  if (SkipStartupVideosEnabled()) {
    return false;
  }
  if (trace.last_ready_poll_at.time_since_epoch().count() != 0) {
    return trace.last_ready_poll_result == 0;
  }
  if (RefiiRequiredStartupVideosReadyLocked(trace) &&
      RefiiStartupVideoObservedSlotSyntheticFinalized(trace.middleware) &&
      trace.microsoft.finalizer_seen && trace.lionhead.finalizer_seen &&
      trace.microsoft.finalizer_synthetic &&
      trace.lionhead.finalizer_synthetic) {
    return false;
  }
  return !trace.last_bik_provider.empty() ||
         trace.last_bik_at.time_since_epoch().count() != 0 ||
         trace.last_startup_update_guest != 0;
}

int64_t RefiiStartupVideoFinalizerAgeMsLocked(
    const RefiiPostVideoHandoffTrace& trace) {
  if (!RefiiStartupVideosNativeObjectsRetiredLocked(trace)) {
    return -1;
  }
  auto finalized_at =
      std::max(trace.microsoft.finalizer_at, trace.lionhead.finalizer_at);
  if (!trace.middleware.provider.empty()) {
    finalized_at = std::max(finalized_at, trace.middleware.finalizer_at);
  }
  if (finalized_at.time_since_epoch().count() == 0) {
    return -1;
  }
  return std::chrono::duration_cast<std::chrono::milliseconds>(
             std::chrono::steady_clock::now() - finalized_at)
      .count();
}

bool RefiiStartupVideosSettledForPostVideoHandoffLocked(
    const RefiiPostVideoHandoffTrace& trace,
    int64_t* settled_age_ms_out = nullptr) {
  if (RefiiStartupNativePlaybackCompleteForHandoffLocked(trace)) {
    const int64_t finalizer_age_ms =
        RefiiStartupVideoFinalizerAgeMsLocked(trace);
    if (settled_age_ms_out != nullptr) {
      *settled_age_ms_out = finalizer_age_ms;
    }
    return finalizer_age_ms >= kRefiiStartupVideoPostFinalizerHandoffDelayMs;
  }
  int64_t ready_age_ms = -1;
  const bool timed_complete =
      RefiiStartupVideosTimedPlaybackCompleteForHandoffLocked(trace,
                                                             &ready_age_ms);
  if (RefiiStartupNativeReadyPollStillBlocksHandoffLocked(trace)) {
    if (settled_age_ms_out != nullptr) {
      *settled_age_ms_out =
          timed_complete
              ? ready_age_ms
              : trace.ready_gate_false_since.time_since_epoch().count() == 0
                    ? -1
                    : std::chrono::duration_cast<std::chrono::milliseconds>(
                          std::chrono::steady_clock::now() -
                          trace.ready_gate_false_since)
                          .count();
    }
    return timed_complete;
  }

  if (settled_age_ms_out != nullptr) {
    *settled_age_ms_out = ready_age_ms;
  }
  return timed_complete;
}

bool RefiiStartupVideosPlaybackCriticalActive() {
  if (!RefiiLooseAssetBackendEnabled() || SkipStartupVideosEnabled()) {
    return false;
  }

  std::unique_lock<std::mutex> lock(RefiiPostVideoHandoffTraceMutex(),
                                   std::try_to_lock);
  if (!lock.owns_lock()) {
    // This predicate is used by trace/log throttles on hot video paths. If the
    // trace mutex is already busy, assume playback is still critical so logging
    // fails closed instead of formatting large state snapshots inside the BIK
    // frame loop.
    return true;
  }

  const auto& trace = RefiiPostVideoHandoffTraceState();
  const bool startup_seen =
      !trace.middleware.provider.empty() || !trace.microsoft.provider.empty() ||
      !trace.lionhead.provider.empty() ||
      trace.last_bik_at.time_since_epoch().count() != 0 ||
      trace.last_startup_update_guest != 0;
  if (!startup_seen) {
    return false;
  }
  return !RefiiStartupVideosSettledForPostVideoHandoffLocked(trace, nullptr);
}

bool RefiiStartupReadyGateFalseLongEnoughForDebugAccelerator(
    int64_t* false_age_ms_out) {
  if (false_age_ms_out != nullptr) {
    *false_age_ms_out = -1;
  }
  if (!RefiiLooseAssetBackendEnabled() || SkipStartupVideosEnabled()) {
    return false;
  }

  const bool menu_chain_can_advance =
      RefiiMainMenuTraceAllowsPostVideoReadyAdvance();

  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  const auto& trace = RefiiPostVideoHandoffTraceState();
  if (trace.ready_gate_false_since.time_since_epoch().count() == 0 ||
      trace.debug_ready_accelerator_emitted) {
    return false;
  }
  const auto false_age_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - trace.ready_gate_false_since)
          .count();
  if (false_age_ms_out != nullptr) {
    *false_age_ms_out = false_age_ms;
  }
  const int64_t minimum_false_age_ms =
      std::max<int64_t>(kRefiiStartupReadyGateFalseDelayMs,
                        StartupVideoMinimumPlaybackMs());
  if (false_age_ms < minimum_false_age_ms ||
      !RefiiStartupVideosTimedPlaybackCompleteForHandoffLocked(trace,
                                                              nullptr)) {
    return false;
  }

  // The ready poll sits after the generated video loop and returns byte +5 on
  // the ready object. That transition is what lets generated code reach the
  // frontend/resource consumer, so waiting for the texture chain to have
  // already opened creates a circular gate. Once playback has really finalized,
  // a VFS-owned menu texture hash is enough to let native frontend code ask for
  // the ResourceWrapper through the normal open path.
  return menu_chain_can_advance || RefiiMainMenuTextureKnownOwnedByVfsIndex();
}

bool RefiiStartupHandoffTimerLongEnoughForVfsAccelerator(
    int64_t* ready_age_ms_out,
    bool for_ready_gate_patch = true) {
  if (ready_age_ms_out != nullptr) {
    *ready_age_ms_out = -1;
  }
  const bool menu_chain_can_advance =
      RefiiMainMenuTraceAllowsPostVideoReadyAdvance();
  if (!RefiiLooseAssetBackendEnabled() || SkipStartupVideosEnabled() ||
      !RefiiStartupVideosReadyForMenuHandoff() ||
      (for_ready_gate_patch && !menu_chain_can_advance)) {
    return false;
  }

  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  const auto& trace = RefiiPostVideoHandoffTraceState();
  const bool finalized_missing_menu_chain_handoff =
      !for_ready_gate_patch && !menu_chain_can_advance &&
      RefiiStartupVideosSettledForPostVideoHandoffLocked(trace, nullptr);
  if (!menu_chain_can_advance && !finalized_missing_menu_chain_handoff) {
    return false;
  }
  if (!for_ready_gate_patch &&
      !RefiiStartupVideosSettledForPostVideoHandoffLocked(trace, nullptr)) {
    return false;
  }
  if (trace.frontend_consumer_seen ||
      (for_ready_gate_patch
           ? trace.debug_ready_accelerator_emitted
           : trace.post_video_handoff_timer_accelerator_emitted)) {
    return false;
  }
  int64_t finalizer_age_ms = -1;
  const bool settled =
      RefiiStartupVideosSettledForPostVideoHandoffLocked(trace,
                                                         &finalizer_age_ms);
  if (ready_age_ms_out != nullptr) {
    *ready_age_ms_out = finalizer_age_ms;
  }
  return settled;
}

void MarkRefiiStartupReadyGateDebugAcceleratorEmitted() {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  RefiiPostVideoHandoffTraceState().debug_ready_accelerator_emitted = true;
}

void RecordRefiiStartupReadyGateObjectPatch(uint32_t object_guest,
                                            std::string_view reason) {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  trace.last_ready_patch_object = object_guest;
  trace.last_ready_patch_reason = std::string(reason);
  trace.last_ready_patch_at = std::chrono::steady_clock::now();
}

void MarkRefiiPostVideoHandoffTimerAcceleratorEmitted() {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  RefiiPostVideoHandoffTraceState()
      .post_video_handoff_timer_accelerator_emitted = true;
}

bool MaybeApplyRefiiVfsStartupHandoffReadyPatch(uint8_t* base,
                                                uint32_t lr,
                                                std::string_view context) {
  int64_t finalizer_age_ms = -1;
  if (!RefiiStartupHandoffTimerLongEnoughForVfsAccelerator(
          &finalizer_age_ms)) {
    return false;
  }

  const uint32_t update_guest = LastRefiiStartupUpdateGuest();
  if (!ApplyRefiiVfsStartupVideoReadyPatch(base, update_guest, context)) {
    return false;
  }

  MarkRefiiStartupReadyGateDebugAcceleratorEmitted();
  if (kTraceFrontendHandoffDiagnostics && VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS startup handoff timer accelerator context={} "
                "lr=0x{:08X} update=0x{:08X} finalizer_age_ms={} "
                "handoff=[{}] main_menu=[{}]",
                context,
                lr,
                update_guest,
                finalizer_age_ms,
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
    LogRefiiVfsChainVerdict(
        "frontend_post_video",
        lr,
        "menu_texture_refii_owned",
        "",
        "",
        "startup_video_ready_patch",
        "handoff_timer_ready_patch",
        "TraceOnce",
        "sub_82377980_or_sub_82378618");
  }
  return true;
}

bool MaybeApplyRefiiVfsPostVideoHandoffTimerPatch(uint8_t* base,
                                                  uint32_t lr,
                                                  uint32_t owner_guest,
                                                  std::string_view context) {
  if (context == "PostVideoFrame_82389210" &&
      !RefiiStartupVideosReadyForMenuHandoff()) {
    return false;
  }

  int64_t finalizer_age_ms = -1;
  if (!RefiiStartupHandoffTimerLongEnoughForVfsAccelerator(&finalizer_age_ms,
                                                           false)) {
    return false;
  }
  const uint32_t requested_owner_guest = owner_guest;
  bool used_global_owner = false;
  if (base == nullptr || !LooksLikeGuestPointer(owner_guest)) {
    owner_guest = FindPostVideoOwnerFromGlobalRoot(base);
    used_global_owner = owner_guest != requested_owner_guest;
  }
  if (!LooksLikeGuestPointer(owner_guest)) {
    return false;
  }

  uint32_t handoff_guest = LoadGuestU32(base, owner_guest + 4);
  if (!LooksLikeGuestPointer(handoff_guest)) {
    const uint32_t global_owner_guest = FindPostVideoOwnerFromGlobalRoot(base);
    if (LooksLikeGuestPointer(global_owner_guest) &&
        global_owner_guest != owner_guest) {
      const uint32_t global_handoff_guest =
          LoadGuestU32(base, global_owner_guest + 4);
      if (LooksLikeGuestPointer(global_handoff_guest)) {
        owner_guest = global_owner_guest;
        handoff_guest = global_handoff_guest;
        used_global_owner = true;
      }
    }
  }
  if (!LooksLikeGuestPointer(handoff_guest)) {
    return false;
  }

  const uint32_t before_duration_word = LoadGuestU32(base, handoff_guest + 4);
  const uint32_t before_elapsed_word = LoadGuestU32(base, handoff_guest + 8);
  const uint8_t before_handoff_233 = LoadGuestU8(base, handoff_guest + 233);
  const uint8_t before_handoff_244 = LoadGuestU8(base, handoff_guest + 244);
  const float before_duration = RefiiGuestF32FromWord(before_duration_word);
  const float before_elapsed = RefiiGuestF32FromWord(before_elapsed_word);
  const float patched_duration =
      before_duration > 0.0f && std::isfinite(before_duration)
          ? before_duration
          : 5.0f;
  const float patched_elapsed = patched_duration + 0.25f;
  StoreGuestU32(base, handoff_guest + 8, RefiiGuestF32ToWord(patched_elapsed));
  StoreGuestU8(base, handoff_guest + 244, 1);

  MarkRefiiPostVideoHandoffTimerAcceleratorEmitted();
  if (kTraceFrontendHandoffDiagnostics && VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS post-video handoff timer accelerator context={} "
                "lr=0x{:08X} owner=0x{:08X} requested_owner=0x{:08X} "
                "owner_source={} handoff=0x{:08X} "
                "finalizer_age_ms={} before_elapsed={} before_elapsed_word=0x{:08X} "
                "duration={} duration_word=0x{:08X} patched_elapsed={} "
                "flags_before[233,244]=0x{:02X}/0x{:02X} "
                "flags_after[233,244]=0x{:02X}/0x{:02X} "
                "handoff_words={} main_menu=[{}]",
                context,
                lr,
                owner_guest,
                requested_owner_guest,
                used_global_owner ? "global_root" : "caller",
                handoff_guest,
                finalizer_age_ms,
                before_elapsed,
                before_elapsed_word,
                patched_duration,
                before_duration_word,
                patched_elapsed,
                before_handoff_233,
                before_handoff_244,
                LoadGuestU8(base, handoff_guest + 233),
                LoadGuestU8(base, handoff_guest + 244),
                FormatGuestWordsIfPointer(base, handoff_guest, 12),
                FormatRefiiMainMenuBlockerTraceForLog());
    LogRefiiVfsChainVerdict("frontend_post_video",
                            lr,
                            "menu_texture_refii_owned",
                            "",
                            "",
                            "post_video_handoff_timer",
                            "handoff_timer_elapsed_patch",
                            "TraceOnce",
                            "sub_82377980_or_sub_82378618");
  }
  return true;
}


}  // namespace detail
}  // namespace refii::vfs
