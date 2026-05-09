#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

bool RefiiMainMenuTraceIsHashOnly();
bool RefiiMainMenuTraceIsTextureOwnedButNotOpened();
bool RefiiMainMenuTraceNeedsTexturePrime();
bool RefiiMainMenuTraceNeedsFrontendHandoff();
bool RefiiPostVideoFrontendChainConsumed();
bool RefiiPostVideoInitialLoadingOwnerReleaseReady();
bool RefiiPostFrontendConsumerHandoffSignalEmitted();
bool RefiiPostFrontendDeferredHashWakeSignalEmitted();
bool RefiiPostGameFacePackageHandoffSignalEmitted();
bool RefiiPostLoadingFrontendFinalHandoffWakeEmitted();
uint32_t ResolveRefiiPostFrontendLoadingOwnerCandidate(uint8_t* base);

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
  const bool middleware_required = !trace.middleware.provider.empty();
  const bool videos_loaded = (!middleware_required ||
                              RefiiStartupVideoSlotReady(trace.middleware)) &&
                             RefiiStartupVideoSlotReady(trace.microsoft) &&
                             RefiiStartupVideoSlotReady(trace.lionhead);
  const bool videos_handoff_ready =
      RefiiStartupVideosSettledForPostVideoHandoffLocked(trace, nullptr);
  return fmt::format("boot_ms={} videos_loaded={} videos_handoff_ready={} "
                     "middleware=[{}] microsoft=[{}] lionhead=[{}] "
                     "last_bik={} object=0x{:08X} stream=0x{:08X} age_ms={} "
                     "last_ui={} lr=0x{:08X} result=0x{:08X} "
                     "args=0x{:08X}/0x{:08X}/0x{:08X} age_ms={} "
                     "ready_update=0x{:08X} update_lr=0x{:08X} "
                     "update_result=0x{:08X} update_frame={} "
                     "update_index={} update_count={} update_complete={} "
                     "update_age_ms={} ready_poll_object=0x{:08X} "
                     "ready_poll_lr=0x{:08X} ready_result=0x{:08X} "
                     "ready_byte={} pending_word52=0x{:08X} "
                     "ready_words=0x{:08X}/0x{:08X}/0x{:08X} "
                     "ready_age_ms={} ready_false_age_ms={} "
                     "completion_callback=0x{:08X} completion_kind={} "
                     "completion_result=0x{:08X} completion_should={} "
                     "completion_age_ms={} ready_patch=0x{:08X} "
                     "ready_patch_object=0x{:08X} "
                     "ready_patch_reason={} ready_patch_age_ms={} "
                     "frontend_consumer_seen={} frontend_consumer_owned={} "
                     "frontend_consumer_lr=0x{:08X} "
                     "frontend_consumer_result=0x{:08X} "
                     "frontend_consumer_owner=0x{:08X} "
                     "frontend_consumer_descriptors={} "
                     "frontend_consumer_resolvers={} "
                     "frontend_consumer_stores={} "
                     "frontend_consumer_next={} "
                     "frontend_consumer_age_ms={} "
                     "post_frontend_request_seen={} "
                     "post_frontend_request_api={} "
                     "post_frontend_request_asset={} "
                     "post_frontend_request_lr=0x{:08X} "
                     "post_frontend_request_hash=0x{:08X} "
                     "post_frontend_request_age_ms={} "
                     "last_getcached={} lr=0x{:08X} stream=0x{:08X} age_ms={} "
                     "post_vfs_signal={} signal_lr=0x{:08X} "
                     "signal_handle=0x{:08X} signal_age_ms={} "
                     "wait_owner=0x{:08X} handoff=0x{:08X} "
                     "signal=0x{:08X} wait_object=0x{:08X} "
                     "wait_handle=0x{:08X} wait_lr=0x{:08X} "
                     "wait_result=0x{:08X} wait_ms={} wait_age_ms={} "
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
                     "stage_age_ms={} stage_frame={} "
                     "frame_slowest_stage={} frame_slowest_lr=0x{:08X} "
                     "frame_slowest_result=0x{:08X} "
                     "frame_slowest_elapsed_ms={} frame_slowest_age_ms={} "
                     "slowest_stage={} slowest_lr=0x{:08X} slowest_result=0x{:08X} "
                     "slowest_elapsed_ms={} slowest_age_ms={}",
                     RefiiBootTraceAgeMs(),
                     videos_loaded,
                     videos_handoff_ready,
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
                     trace.last_ready_poll_object,
                     trace.last_ready_poll_lr,
                     trace.last_ready_poll_result,
                     trace.last_ready_poll_ready_byte,
                     trace.last_ready_poll_pending_word52,
                     trace.last_ready_poll_word0,
                     trace.last_ready_poll_word1,
                     trace.last_ready_poll_word2,
                     age_ms(trace.last_ready_poll_at),
                     age_ms(trace.ready_gate_false_since),
                     trace.last_completion_gate_callback,
                     trace.last_completion_gate_kind,
                     trace.last_completion_gate_result,
                     trace.last_completion_gate_should_complete,
                     age_ms(trace.last_completion_gate_at),
                     trace.last_ready_patch_update,
                     trace.last_ready_patch_object,
                     trace.last_ready_patch_reason.empty()
                         ? "none"
                         : trace.last_ready_patch_reason,
                     age_ms(trace.last_ready_patch_at),
                     trace.frontend_consumer_seen ? "true" : "false",
                     trace.frontend_consumer_any_refii_owned ? "true" : "false",
                     trace.last_frontend_consumer_lr,
                     trace.last_frontend_consumer_result,
                     trace.last_frontend_consumer_owner,
                     trace.last_frontend_consumer_descriptors,
                     trace.last_frontend_consumer_resolvers,
                     trace.last_frontend_consumer_stores,
                     trace.last_frontend_consumer_next.empty()
                         ? "none"
                         : trace.last_frontend_consumer_next,
                     age_ms(trace.last_frontend_consumer_at),
                     trace.post_frontend_consumer_request_seen ? "true" : "false",
                     trace.first_post_frontend_consumer_request_api.empty()
                         ? "none"
                         : trace.first_post_frontend_consumer_request_api,
                     trace.first_post_frontend_consumer_request_asset.empty()
                         ? "none"
                         : trace.first_post_frontend_consumer_request_asset,
                     trace.first_post_frontend_consumer_request_lr,
                     trace.first_post_frontend_consumer_request_hash,
                     age_ms(trace.first_post_frontend_consumer_request_at),
                     trace.last_getcached_name,
                     trace.last_getcached_lr,
                     trace.last_getcached_stream,
                     age_ms(trace.last_getcached_at),
                     trace.post_gameface_package_handoff_asset.empty()
                         ? "none"
                         : trace.post_gameface_package_handoff_asset,
                     trace.post_gameface_package_handoff_signal_lr,
                     trace.post_gameface_package_handoff_signal_handle,
                     age_ms(trace.post_gameface_package_handoff_signal_at),
                     trace.last_wait_owner,
                     trace.last_wait_handoff,
                     trace.last_wait_signal_handle,
                     trace.last_wait_object,
                     trace.last_wait_handle,
                     trace.last_wait_lr,
                     trace.last_wait_result,
                     trace.last_wait_ms,
                     age_ms(trace.last_wait_at),
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

bool RefiiIsTrackedStartupVideoObject(uint32_t object_guest) {
  if (object_guest == 0) {
    return false;
  }

  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  const auto& trace = RefiiPostVideoHandoffTraceState();
  return trace.middleware.object_guest == object_guest ||
         trace.microsoft.object_guest == object_guest ||
         trace.lionhead.object_guest == object_guest;
}

bool ShouldTracePostVideoBoundaryProbe(std::string_view api) {
  return kTraceFrontendHandoffDiagnostics && VfsTracingEnabled() &&
         !RefiiStartupVideosPlaybackCriticalActive() &&
         (RefiiStartupVideosReadyForMenuHandoff() ||
          RefiiMainMenuTraceIsHashOnly() ||
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
         (RefiiStartupVideosReadyForMenuHandoff() ||
          RefiiMainMenuTraceIsHashOnly() ||
          RefiiMainMenuTraceIsTextureOwnedButNotOpened());
}

bool IsPostVideoGeneratedWaitLr(uint32_t lr) {
  return lr == 0x82388A6C || lr == 0x823889FC || lr == 0x823882E0 ||
         lr == 0x82B48B08 || lr == 0x82B48EC8 || lr == 0x82B48F40;
}

std::string FormatGeneratedEventSetState(uint8_t* base,
                                         uint32_t event_handle_guest);
std::string FormatGeneratedEventWaitState(uint8_t* base,
                                          uint32_t wait_object_guest);
bool RefiiMainMenuTextureKnownOwnedByVfsIndex();
std::atomic<uint32_t>& RefiiRememberedLoadingScreenOwner();
std::atomic<uint32_t>& RefiiCompletedLoadingScreenOwner();
bool RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
bool RefiiPostVfsChainReadyForLoadingOwnerRelease();

struct RefiiFrontEndAudioLoadingTrace {
  uint32_t late_bind_count = 0;
  uint32_t last_lr = 0;
  bool terminal_bank_seen = false;
  std::string last_bank;
  std::chrono::steady_clock::time_point last_bank_at{};
  std::chrono::steady_clock::time_point terminal_bank_at{};
};

std::mutex& RefiiFrontEndAudioLoadingTraceMutex() {
  static std::mutex mutex;
  return mutex;
}

RefiiFrontEndAudioLoadingTrace& RefiiFrontEndAudioLoadingTraceState() {
  static RefiiFrontEndAudioLoadingTrace trace;
  return trace;
}

int64_t RefiiAudioTraceAgeMs(
    std::chrono::steady_clock::time_point timestamp) {
  if (timestamp.time_since_epoch().count() == 0) {
    return -1;
  }
  return std::chrono::duration_cast<std::chrono::milliseconds>(
             std::chrono::steady_clock::now() - timestamp)
      .count();
}

std::string NormalizeFrontEndAudioBankName(std::string_view bank_name) {
  std::string normalized;
  normalized.reserve(bank_name.size());
  for (const char ch : bank_name) {
    normalized.push_back(ch == '\\' ? '/' : static_cast<char>(
                                         std::tolower(
                                             static_cast<unsigned char>(ch))));
  }
  return normalized;
}

bool FrontEndAudioStringStartsWith(std::string_view value,
                                   std::string_view prefix) {
  return value.size() >= prefix.size() &&
         value.substr(0, prefix.size()) == prefix;
}

bool FrontEndAudioStringEndsWith(std::string_view value,
                                 std::string_view suffix) {
  return value.size() >= suffix.size() &&
         value.substr(value.size() - suffix.size()) == suffix;
}

bool IsRefiiFrontEndAudioTerminalBank(std::string_view bank_name) {
  return bank_name == "audio/bandit_vocalisations.bnk" ||
         bank_name == "audio\\bandit_vocalisations.bnk";
}

void RecordRefiiFrontEndAudioBankLateBind(std::string_view bank_name,
                                          uint32_t lr) {
  if (bank_name.empty() || !RefiiLooseAssetBackendEnabled()) {
    return;
  }
  const std::string normalized = NormalizeFrontEndAudioBankName(bank_name);
  if (!FrontEndAudioStringStartsWith(normalized, "audio/") ||
      !FrontEndAudioStringEndsWith(normalized, ".bnk")) {
    return;
  }

  uint32_t count = 0;
  bool terminal = false;
  {
    std::lock_guard<std::mutex> lock(RefiiFrontEndAudioLoadingTraceMutex());
    auto& trace = RefiiFrontEndAudioLoadingTraceState();
    trace.last_bank = normalized;
    trace.last_lr = lr;
    trace.last_bank_at = std::chrono::steady_clock::now();
    count = ++trace.late_bind_count;
    if (IsRefiiFrontEndAudioTerminalBank(normalized)) {
      trace.terminal_bank_seen = true;
      trace.terminal_bank_at = trace.last_bank_at;
      terminal = true;
    }
  }

  if (terminal || (VfsTracingEnabled() &&
                   ShouldLogRepeatedVfsTraceOperation(
                       "FrontEndAudioBankLateBind", 12))) {
    RecordRefiiMainMenuBlockerEvent(
        "front_end_audio_bank_late_bind",
        fmt::format("bank={} lr=0x{:08X} count={} terminal={}",
                    normalized,
                    lr,
                    count,
                    terminal ? "true" : "false"));
    if (VfsTracingEnabled() && terminal) {
      REXLOG_INFO("Refii VFS front-end audio terminal bank reached "
                  "bank={} lr=0x{:08X} count={}",
                  normalized,
                  lr,
                  count);
    }
  }
}
bool RefiiOwnerlessPostVideoVfsChainReadyForAfterHolder();
std::atomic<uint32_t>& RefiiPostLoadingAfterHolderMode5CleanedHolder();
bool RefiiOwnerlessPostVideoAfterHolderCleaned();
bool RefiiCollapsedLoadingSequenceReadyForOwnerRelease();
bool TryRunRefiiPostLoadingFrontendRenderRequestNativeFromTrace(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view source_api);
bool TryFinalizeRefiiPostLoadingFrontendGeneratedState(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view source_api,
    bool clear_render_handoff,
    bool preserve_post_gameface_primary_allowed = true);
bool RefiiPostGameFacePrimaryOwnerReady(uint8_t* base);
bool TryPublishRefiiPostGameFacePrimaryOwnerFromTrace(PPCContext& source_ctx,
                                                      uint8_t* base,
                                                      std::string_view source_api);
bool TryRunRefiiPostGameFacePrimaryOwnerNativeFromTrace(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view source_api);
bool TryRunRefiiPostGameFaceFrontendRenderRequestFromTrace(
    PPCContext& source_ctx,
    uint8_t* base,
    std::string_view source_api);
bool TryFinishRememberedRefiiLoadingOwnerFromPostFrontendSignal(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view source_api);

std::atomic<uint32_t>& RefiiNativeVfsWorkerWaitObject() {
  static std::atomic<uint32_t> object{0};
  return object;
}

std::atomic<uint32_t>& RefiiNativeVfsWorkerWaitHandle() {
  static std::atomic<uint32_t> handle{0};
  return handle;
}

std::atomic<uint32_t>& RefiiNativeVfsWorkerWaitSignalEmitted() {
  static std::atomic<uint32_t> emitted{0};
  return emitted;
}

void RememberRefiiNativeVfsWorkerWait(uint32_t lr,
                                      uint32_t wait_object_guest,
                                      uint32_t wait_handle_guest) {
  if ((lr != 0x82B48EC8u && lr != 0x82B48F40u) ||
      wait_object_guest == 0 || wait_handle_guest == 0) {
    return;
  }
  RefiiNativeVfsWorkerWaitObject().store(wait_object_guest,
                                         std::memory_order_relaxed);
  RefiiNativeVfsWorkerWaitHandle().store(wait_handle_guest,
                                         std::memory_order_relaxed);
}

bool TrySignalRefiiCompletedNativeVfsWorkerWait(
    const PPCContext& original_ctx,
    uint8_t* base,
    std::string_view source_api,
    bool trace) {
  const bool post_loading_final_handoff_ready =
      RefiiPostLoadingFrontendFinalHandoffWakeEmitted() &&
      RefiiPostGameFacePackageHandoffSignalEmitted() &&
      RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      (!RefiiPostVfsChainReadyForLoadingOwnerRelease() &&
       !post_loading_final_handoff_ready)) {
    return false;
  }
  const uint32_t wait_handle_guest =
      RefiiNativeVfsWorkerWaitHandle().load(std::memory_order_relaxed);
  const uint32_t wait_object_guest =
      RefiiNativeVfsWorkerWaitObject().load(std::memory_order_relaxed);
  if (wait_handle_guest == 0) {
    return false;
  }

  const uint32_t finalizer_worker_guest =
      wait_object_guest >= 20u ? wait_object_guest - 20u : 0;
  const uint32_t finalizer_queue_guest =
      LooksLikeGuestPointer(finalizer_worker_guest)
          ? finalizer_worker_guest + 160u
          : 0;
  const uint32_t queued_work =
      LooksLikeGuestPointer(finalizer_queue_guest)
          ? LoadGuestU32(base, finalizer_queue_guest + 16)
          : 0;
  uint8_t exit_flag =
      LooksLikeGuestPointer(finalizer_worker_guest)
          ? LoadGuestU8(base, finalizer_worker_guest + 152)
          : 0;
  if (queued_work == 0 && exit_flag == 0 &&
      post_loading_final_handoff_ready &&
      LooksLikeGuestPointer(finalizer_worker_guest)) {
    StoreGuestU8(base, finalizer_worker_guest + 152, 1);
    exit_flag = LoadGuestU8(base, finalizer_worker_guest + 152);
    RecordRefiiMainMenuBlockerEvent(
        "native_vfs_worker_finalizer_exit_released",
        fmt::format("source={} wait_object=0x{:08X} wait_handle=0x{:08X} "
                    "worker=0x{:08X} queue=0x{:08X} queued={} "
                    "final_handoff_ready=true exit152=0x{:02X}",
                    source_api,
                    wait_object_guest,
                    wait_handle_guest,
                    finalizer_worker_guest,
                    finalizer_queue_guest,
                    queued_work,
                    exit_flag));
  }
  if (queued_work == 0 && exit_flag == 0) {
    if (trace || ShouldLogRepeatedVfsTraceOperation(
                     "NativeVfsWorkerWaitSignalSkipped", 8)) {
      REXLOG_INFO("Refii VFS native worker wait signal skipped source={} "
                  "reason=empty_queue_not_shutdown wait_object=0x{:08X} "
                  "wait_handle=0x{:08X} worker=0x{:08X} queue=0x{:08X} "
                  "queued={} exit152=0x{:02X} state=[{}] handoff=[{}] "
                  "main_menu=[{}]",
                  source_api,
                  wait_object_guest,
                  wait_handle_guest,
                  finalizer_worker_guest,
                  finalizer_queue_guest,
                  queued_work,
                  exit_flag,
                  FormatGeneratedEventWaitState(base, wait_object_guest),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    RecordRefiiMainMenuBlockerEvent(
        "native_vfs_worker_wait_signal_skipped",
        fmt::format("source={} reason=empty_queue_not_shutdown "
                    "wait_object=0x{:08X} wait_handle=0x{:08X} "
                    "worker=0x{:08X} queue=0x{:08X} queued={} "
                    "exit152=0x{:02X}",
                    source_api,
                    wait_object_guest,
                    wait_handle_guest,
                    finalizer_worker_guest,
                    finalizer_queue_guest,
                    queued_work,
                    exit_flag));
    return false;
  }

  if (RefiiNativeVfsWorkerWaitSignalEmitted().exchange(
          wait_handle_guest, std::memory_order_relaxed) == wait_handle_guest) {
    return false;
  }

  PPCContext signal_ctx = original_ctx;
  signal_ctx.r3.u32 = wait_handle_guest;
  ::__imp__sub_822C9388(signal_ctx, base);

  RecordRefiiMainMenuBlockerEvent(
      "native_vfs_worker_wait_signaled",
      fmt::format("source={} wait_object=0x{:08X} wait_handle=0x{:08X} "
                  "signal_result=0x{:08X} lr=0x{:08X}",
                  source_api,
                  wait_object_guest,
                  wait_handle_guest,
                  signal_ctx.r3.u32,
                  static_cast<uint32_t>(original_ctx.lr)));
  if (trace || ShouldLogRepeatedVfsTraceOperation(
                   "NativeVfsWorkerWaitSignaled", 8)) {
    REXLOG_INFO("Refii VFS native worker wait signaled source={} "
                "wait_object=0x{:08X} wait_handle=0x{:08X} "
                "signal_result=0x{:08X} state=[{}] handoff=[{}] "
                "main_menu=[{}]",
                source_api,
                wait_object_guest,
                wait_handle_guest,
                signal_ctx.r3.u32,
                FormatGeneratedEventWaitState(base, wait_object_guest),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  return true;
}

bool TryReleaseRefiiCompletedNativeVfsFinalizerExit(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view source_api,
    bool trace) {
  if (base == nullptr || !RefiiLooseAssetBackendEnabled()) {
    return false;
  }

  const uint32_t wait_handle_guest =
      RefiiNativeVfsWorkerWaitHandle().load(std::memory_order_relaxed);
  const uint32_t wait_object_guest =
      RefiiNativeVfsWorkerWaitObject().load(std::memory_order_relaxed);
  const uint32_t finalizer_worker_guest =
      wait_object_guest >= 20u ? wait_object_guest - 20u : 0;
  const uint32_t finalizer_queue_guest =
      LooksLikeGuestPointer(finalizer_worker_guest)
          ? finalizer_worker_guest + 160u
          : 0;
  const uint32_t queued_work =
      LooksLikeGuestPointer(finalizer_queue_guest)
          ? LoadGuestU32(base, finalizer_queue_guest + 16)
          : 0;
  const uint8_t exit152_before =
      LooksLikeGuestPointer(finalizer_worker_guest)
          ? LoadGuestU8(base, finalizer_worker_guest + 152)
          : 0;
  if (wait_handle_guest == 0 || !LooksLikeGuestPointer(finalizer_worker_guest) ||
      queued_work != 0) {
    if (trace || ShouldLogRepeatedVfsTraceOperation(
                     "CompletedNativeVfsFinalizerExitReleaseSkipped", 8)) {
      REXLOG_INFO("Refii VFS completed native VFS finalizer exit release "
                  "skipped source={} wait_object=0x{:08X} "
                  "wait_handle=0x{:08X} worker=0x{:08X} queue=0x{:08X} "
                  "queued={} exit152=0x{:02X}",
                  source_api,
                  wait_object_guest,
                  wait_handle_guest,
                  finalizer_worker_guest,
                  finalizer_queue_guest,
                  queued_work,
                  exit152_before);
    }
    return false;
  }

  if (exit152_before == 0) {
    StoreGuestU8(base, finalizer_worker_guest + 152, 1);
  }
  const uint8_t exit152_after = LoadGuestU8(base, finalizer_worker_guest + 152);
  const bool signal_already_emitted =
      RefiiNativeVfsWorkerWaitSignalEmitted().exchange(
          wait_handle_guest, std::memory_order_relaxed) == wait_handle_guest;
  if (signal_already_emitted) {
    if (trace || ShouldLogRepeatedVfsTraceOperation(
                     "CompletedNativeVfsFinalizerExitAlreadyReleased", 8)) {
      REXLOG_INFO("Refii VFS completed native VFS finalizer exit already "
                  "released source={} wait_object=0x{:08X} "
                  "wait_handle=0x{:08X} worker=0x{:08X} queue=0x{:08X} "
                  "queued={} exit152=0x{:02X}->0x{:02X}",
                  source_api,
                  wait_object_guest,
                  wait_handle_guest,
                  finalizer_worker_guest,
                  finalizer_queue_guest,
                  queued_work,
                  exit152_before,
                  exit152_after);
    }
    return false;
  }

  PPCContext signal_ctx = source_ctx;
  signal_ctx.r3.u32 = wait_handle_guest;
  ::__imp__sub_822C9388(signal_ctx, base);
  RecordRefiiMainMenuBlockerEvent(
      "completed_native_vfs_finalizer_exit_released",
      fmt::format("source={} wait_object=0x{:08X} wait_handle=0x{:08X} "
                  "worker=0x{:08X} queue=0x{:08X} queued={} "
                  "exit152=0x{:02X}->0x{:02X} signal_result=0x{:08X}",
                  source_api,
                  wait_object_guest,
                  wait_handle_guest,
                  finalizer_worker_guest,
                  finalizer_queue_guest,
                  queued_work,
                  exit152_before,
                  exit152_after,
                  signal_ctx.r3.u32));
  if (trace || ShouldLogRepeatedVfsTraceOperation(
                   "CompletedNativeVfsFinalizerExitReleased", 8)) {
    REXLOG_INFO("Refii VFS completed native VFS finalizer exit released "
                "source={} wait_object=0x{:08X} wait_handle=0x{:08X} "
                "worker=0x{:08X} queue=0x{:08X} queued={} "
                "exit152=0x{:02X}->0x{:02X} signal_result=0x{:08X} "
                "handoff=[{}] main_menu=[{}]",
                source_api,
                wait_object_guest,
                wait_handle_guest,
                finalizer_worker_guest,
                finalizer_queue_guest,
                queued_work,
                exit152_before,
                exit152_after,
                signal_ctx.r3.u32,
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  return true;
}

bool MaybeSignalRefiiPostVideoGeneratedWaitBeforeNative(
    const PPCContext& original_ctx,
    uint8_t* base,
    uint32_t wait_object_guest,
    uint32_t wait_handle_guest) {
  const uint32_t lr = static_cast<uint32_t>(original_ctx.lr);
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !IsPostVideoGeneratedWaitLr(lr) || wait_object_guest == 0) {
    return false;
  }

  const bool menu_chain_can_advance =
      RefiiMainMenuTraceAllowsPostVideoReadyAdvance() ||
      RefiiMainMenuTraceNeedsFrontendHandoff() ||
      RefiiMainMenuTextureKnownOwnedByVfsIndex();

  uint32_t signal_handle_guest = 0;
  uint32_t wait_signal_handle_guest = wait_handle_guest;
  uint32_t handoff_guest = 0;
  uint32_t before_244 = 0;
  int64_t settled_age_ms = -1;
  bool should_signal = false;
  std::string skip_reason;
  auto looks_like_private_event_handle = [](uint32_t handle) {
    return (handle & 0xFF000000u) == 0xF8000000u;
  };
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    if (trace.post_video_generated_wait_signal_emitted) {
      return false;
    }

    const bool videos_settled =
        SkipStartupVideosEnabled() ||
        RefiiStartupVideosSettledForPostVideoHandoffLocked(trace,
                                                           &settled_age_ms);
    if (!videos_settled) {
      skip_reason = "startup_videos_not_settled";
    } else if (!menu_chain_can_advance) {
      skip_reason = "menu_chain_not_ready";
    } else {
      auto select_handoff = [&](uint32_t owner_candidate,
                                uint32_t handoff_candidate) {
        if (!LooksLikeGuestPointer(owner_candidate) ||
            !LooksLikeGuestPointer(handoff_candidate) ||
            LoadGuestU32(base, owner_candidate + 4) != handoff_candidate) {
          return false;
        }
        handoff_guest = handoff_candidate;
        signal_handle_guest = LoadGuestU32(base, handoff_guest + 456);
        const uint32_t traced_wait_handle =
            LoadGuestU32(base, handoff_guest + 460 + 128);
        if (wait_signal_handle_guest == 0 && traced_wait_handle != 0) {
          wait_signal_handle_guest = traced_wait_handle;
        }
        before_244 = LoadGuestU8(base, handoff_guest + 244);
        return true;
      };

      if (lr == 0x823882E0) {
        if (!select_handoff(trace.last_nonidle_render_schedule_owner,
                            trace.last_nonidle_render_schedule_handoff) &&
            !select_handoff(trace.last_render_schedule_owner,
                            trace.last_render_schedule_handoff)) {
          select_handoff(trace.last_wait_owner, trace.last_wait_handoff);
        }
      } else if (lr == 0x82388A6C && wait_object_guest > 460) {
        handoff_guest = wait_object_guest - 460;
        if (LooksLikeGuestPointer(handoff_guest)) {
          signal_handle_guest = LoadGuestU32(base, handoff_guest + 456);
          const uint32_t traced_wait_handle =
              LoadGuestU32(base, handoff_guest + 460 + 128);
          if (wait_signal_handle_guest == 0 && traced_wait_handle != 0) {
            wait_signal_handle_guest = traced_wait_handle;
          }
          before_244 = LoadGuestU8(base, handoff_guest + 244);
        }
      } else if ((wait_object_guest != 0 &&
                  wait_object_guest == trace.last_wait_object) ||
                 (wait_signal_handle_guest != 0 &&
                  (wait_signal_handle_guest == trace.last_wait_handle ||
                   wait_signal_handle_guest ==
                       trace.last_wait_signal_handle))) {
        select_handoff(trace.last_wait_owner, trace.last_wait_handoff);
      } else if (wait_object_guest > 4 &&
                 LooksLikeGuestPointer(wait_object_guest - 4)) {
        const uint32_t derived_signal_handle =
            LoadGuestU32(base, wait_object_guest - 4);
        if (looks_like_private_event_handle(derived_signal_handle)) {
          signal_handle_guest = derived_signal_handle;
        }
      }
      if (!looks_like_private_event_handle(signal_handle_guest)) {
        signal_handle_guest = 0;
      }
      if (!looks_like_private_event_handle(wait_signal_handle_guest)) {
        wait_signal_handle_guest = 0;
      }
      if (signal_handle_guest == 0) {
        signal_handle_guest = wait_signal_handle_guest;
      }
      if (signal_handle_guest == 0 && wait_signal_handle_guest == 0) {
        skip_reason = "generated_wait_handle_missing";
      } else {
        trace.post_video_generated_wait_signal_emitted = true;
        trace.post_video_generated_wait_signal_handle =
            signal_handle_guest != 0 ? signal_handle_guest
                                     : wait_signal_handle_guest;
        trace.post_video_generated_wait_signal_lr = lr;
        trace.post_video_generated_wait_signal_at =
            std::chrono::steady_clock::now();
        should_signal = true;
      }
    }
  }

  if (!should_signal) {
    const bool defer_logging_until_post_startup =
        skip_reason == "startup_videos_not_settled";
    if (VfsTracingEnabled() && !defer_logging_until_post_startup &&
        !RefiiStartupVideosPlaybackCriticalActive() && !skip_reason.empty() &&
        ShouldLogRepeatedVfsTraceOperation(
            "GeneratedEvent_Wait_PreSignalSkipped", 6)) {
      REXLOG_INFO("Refii VFS generated event wait pre-signal skipped "
                  "reason={} lr=0x{:08X} wait_object=0x{:08X} "
                  "wait_handle=0x{:08X} settled_age_ms={} handoff=[{}] "
                  "main_menu=[{}]",
                  skip_reason,
                  lr,
                  wait_object_guest,
                  wait_handle_guest,
                  settled_age_ms,
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    return false;
  }

  if (handoff_guest != 0 && LooksLikeGuestPointer(handoff_guest)) {
    StoreGuestU8(base, handoff_guest + 244, 1);
  }

  PPCContext signal_ctx = original_ctx;
  signal_ctx.r3.u32 = signal_handle_guest;
  if (signal_ctx.r3.u32 != 0) {
    ::__imp__sub_822C9388(signal_ctx, base);
  }

  PPCContext wait_signal_ctx = original_ctx;
  wait_signal_ctx.r3.u32 = wait_signal_handle_guest;
  if (wait_signal_ctx.r3.u32 != 0 &&
      wait_signal_ctx.r3.u32 != signal_ctx.r3.u32) {
    ::__imp__sub_822C9388(wait_signal_ctx, base);
  }

  RecordRefiiMainMenuBlockerEvent(
      "generated_wait_pre_signal",
      fmt::format("lr=0x{:08X} wait_object=0x{:08X} wait_handle=0x{:08X} "
                  "signal_handle=0x{:08X} handoff=0x{:08X} "
                  "settled_age_ms={} flag244_before=0x{:02X} "
                  "flag244_after=0x{:02X} signal_result=0x{:08X} "
                  "wait_signal_result=0x{:08X}",
                  lr,
                  wait_object_guest,
                  wait_signal_handle_guest,
                  signal_handle_guest,
                  handoff_guest,
                  settled_age_ms,
                  before_244,
                  LooksLikeGuestPointer(handoff_guest)
                      ? LoadGuestU8(base, handoff_guest + 244)
                      : 0,
                  signal_ctx.r3.u32,
                  wait_signal_ctx.r3.u32));

  if (VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS generated event wait pre-signal "
                "lr=0x{:08X} wait_object=0x{:08X} wait_handle=0x{:08X} "
                "signal_handle=0x{:08X} handoff=0x{:08X} "
                "settled_age_ms={} flag244_before=0x{:02X} "
                "signal_result=0x{:08X} wait_signal_result=0x{:08X} "
                "event_state=[{}] wait_event_state=[{}] handoff_trace=[{}] "
                "main_menu=[{}]",
                lr,
                wait_object_guest,
                wait_signal_handle_guest,
                signal_handle_guest,
                handoff_guest,
                settled_age_ms,
                before_244,
                signal_ctx.r3.u32,
                wait_signal_ctx.r3.u32,
                FormatGeneratedEventSetState(base, signal_handle_guest),
                FormatGeneratedEventSetState(base, wait_signal_handle_guest),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
    LogRefiiVfsChainVerdict("frontend_post_video",
                            lr,
                            "menu_texture_refii_owned",
                            "",
                            "",
                            "generated_event_wait",
                            "pre_wait_signal",
                            "Opened",
                            "native_wait_return_or_frontend_consumer");
  }
  return true;
}

bool TryFinishRefiiLoadingScreenOwnerFromCollapsedPipeline(
    const PPCContext& original_ctx,
    uint8_t* base,
    uint32_t owner_guest,
    std::string_view source_api,
    uint32_t source_result,
    bool sequence_ready,
    uint32_t sequence_handle);

bool TryRunRefiiPostVideoGeneratedFrameTailFromCompletedWait(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view source_api) {
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !RefiiStartupVideosReadyForMenuHandoff() ||
      !RefiiPostVideoFrontendChainConsumed() ||
      !RefiiPostGameFacePackageHandoffSignalEmitted() ||
      !RefiiPostVfsChainReadyForLoadingOwnerRelease()) {
    return false;
  }

  uint32_t owner_guest = 0;
  uint32_t handoff_guest = 0;
  uint32_t source124 = 0xFFFFFFFFu;
  uint32_t source128 = 0xFFFFFFFFu;
  double frame_time = 0.0;
  int64_t schedule_age_ms = -1;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    const auto& trace = RefiiPostVideoHandoffTraceState();
    owner_guest = trace.last_nonidle_render_schedule_owner;
    handoff_guest = trace.last_nonidle_render_schedule_handoff;
    source124 = trace.last_nonidle_render_schedule_source124;
    source128 = trace.last_nonidle_render_schedule_source128;
    frame_time = trace.last_nonidle_render_schedule_frame_time;
    if (trace.last_nonidle_render_schedule_at.time_since_epoch().count() != 0) {
      schedule_age_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                            std::chrono::steady_clock::now() -
                            trace.last_nonidle_render_schedule_at)
                            .count();
    }
  }

  if (!LooksLikeGuestPointer(owner_guest) ||
      !LooksLikeGuestPointer(handoff_guest) ||
      LoadGuestU32(base, owner_guest + 4) != handoff_guest ||
      source124 == 0xFFFFFFFFu || source128 == 0xFFFFFFFFu ||
      (schedule_age_ms >= 0 && schedule_age_ms > 180000)) {
    return false;
  }

  static std::atomic<uint64_t> last_tail_key{0};
  const uint64_t tail_key =
      (static_cast<uint64_t>(owner_guest) << 32) | handoff_guest;
  if (last_tail_key.exchange(tail_key, std::memory_order_relaxed) ==
      tail_key) {
    return false;
  }

  const uint8_t flag233_before = LoadGuestU8(base, handoff_guest + 233);
  const uint8_t flag244_before = LoadGuestU8(base, handoff_guest + 244);
  const uint32_t owner_plus4_before = LoadGuestU32(base, owner_guest + 4);

  if (LoadGuestU32(base, handoff_guest + 120) != source128) {
    StoreGuestU8(base, handoff_guest + 132, 1);
  }
  StoreGuestU32(base, handoff_guest + 120, source128);
  StoreGuestU32(base, handoff_guest + 116, source124);
  StoreGuestU8(base, handoff_guest + 168, 1);
  StoreGuestU8(base, handoff_guest + 244, 1);

  PPCContext frame_ctx = source_ctx;
  frame_ctx.lr = 0x826E318Cu;
  frame_ctx.r3.u32 = owner_guest;
  frame_ctx.f1.f64 = frame_time;
  ::__imp__sub_82389210(frame_ctx, base);

  const uint32_t owner_plus4_after = LoadGuestU32(base, owner_guest + 4);
  RecordRefiiMainMenuBlockerEvent(
      "completed_wait_generated_frame_tail",
      fmt::format("source={} owner=0x{:08X} handoff=0x{:08X} "
                  "result=0x{:08X} owner_plus4=0x{:08X}->0x{:08X} "
                  "source124=0x{:08X} source128=0x{:08X} "
                  "flag233=0x{:02X}->0x{:02X} flag244=0x{:02X}->0x{:02X} "
                  "frame_time={} schedule_age_ms={}",
                  source_api,
                  owner_guest,
                  handoff_guest,
                  frame_ctx.r3.u32,
                  owner_plus4_before,
                  owner_plus4_after,
                  source124,
                  source128,
                  flag233_before,
                  LooksLikeGuestPointer(handoff_guest)
                      ? LoadGuestU8(base, handoff_guest + 233)
                      : flag233_before,
                  flag244_before,
                  LooksLikeGuestPointer(handoff_guest)
                      ? LoadGuestU8(base, handoff_guest + 244)
                      : flag244_before,
                  frame_time,
                  schedule_age_ms));
  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation(
          "CompletedWaitGeneratedFrameTail", 8)) {
    REXLOG_INFO("Refii VFS completed wait generated frame tail "
                "source={} owner=0x{:08X} handoff=0x{:08X} "
                "result=0x{:08X} owner_plus4=0x{:08X}->0x{:08X} "
                "source124=0x{:08X} source128=0x{:08X} "
                "flag233=0x{:02X}->0x{:02X} flag244=0x{:02X}->0x{:02X} "
                "frame_time={} schedule_age_ms={} handoff=[{}] "
                "main_menu=[{}]",
                source_api,
                owner_guest,
                handoff_guest,
                frame_ctx.r3.u32,
                owner_plus4_before,
                owner_plus4_after,
                source124,
                source128,
                flag233_before,
                LooksLikeGuestPointer(handoff_guest)
                    ? LoadGuestU8(base, handoff_guest + 233)
                    : flag233_before,
                flag244_before,
                LooksLikeGuestPointer(handoff_guest)
                    ? LoadGuestU8(base, handoff_guest + 244)
                    : flag244_before,
                frame_time,
                schedule_age_ms,
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  return owner_plus4_after == 0;
}

bool TrySignalRefiiCompletedPostGameFacePackageHandoffFromTrace(
    const PPCContext& original_ctx,
    uint8_t* base,
    uint32_t wait_object_guest,
    uint32_t wait_handle_guest,
    bool trace) {
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !RefiiStartupVideosReadyForMenuHandoff() ||
      !RefiiPostVideoFrontendChainConsumed()) {
    return false;
  }

  uint32_t owner_guest = 0;
  uint32_t handoff_guest = 0;
  uint32_t signal_handle = 0;
  uint32_t handoff_wait_handle = 0;
  uint32_t source124 = 0xFFFFFFFFu;
  uint32_t source128 = 0xFFFFFFFFu;
  uint32_t package_mask = 0;
  std::string asset;
  const char* signal_source = "completed_package_retry";
  const bool parsed_texture_ready =
      RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& handoff_trace = RefiiPostVideoHandoffTraceState();
    if (handoff_trace.post_gameface_package_handoff_signal_emitted) {
      return true;
    }
    package_mask = RefiiCompletedPostGameFacePackageMaskLocked(handoff_trace);
    const bool package_ready_after_frontend_consumer =
        handoff_trace.frontend_consumer_seen && parsed_texture_ready;
    if ((package_mask & 0x7u) != 0x7u ||
        (!handoff_trace.post_frontend_consumer_request_seen &&
         !package_ready_after_frontend_consumer)) {
      return false;
    }

    auto select_handoff = [&](uint32_t owner_candidate,
                              uint32_t handoff_candidate,
                              uint32_t candidate_source124,
                              uint32_t candidate_source128,
                              const char* source_name) {
      if (!LooksLikeGuestPointer(owner_candidate) ||
          !LooksLikeGuestPointer(handoff_candidate) ||
          LoadGuestU32(base, owner_candidate + 4) != handoff_candidate) {
        return false;
      }
      owner_guest = owner_candidate;
      handoff_guest = handoff_candidate;
      source124 = candidate_source124;
      source128 = candidate_source128;
      signal_source = source_name;
      return true;
    };

    if (!select_handoff(handoff_trace.last_nonidle_render_schedule_owner,
                        handoff_trace.last_nonidle_render_schedule_handoff,
                        handoff_trace.last_nonidle_render_schedule_source124,
                        handoff_trace.last_nonidle_render_schedule_source128,
                        "completed_package_nonidle_handoff") &&
        !select_handoff(handoff_trace.last_render_schedule_owner,
                        handoff_trace.last_render_schedule_handoff,
                        handoff_trace.last_render_schedule_source124,
                        handoff_trace.last_render_schedule_source128,
                        "completed_package_render_handoff")) {
      select_handoff(handoff_trace.last_wait_owner,
                     handoff_trace.last_wait_handoff,
                     0xFFFFFFFFu,
                     0xFFFFFFFFu,
                     "completed_package_wait_handoff");
    }

    if (!LooksLikeGuestPointer(handoff_guest)) {
      return false;
    }

    signal_handle = LoadGuestU32(base, handoff_guest + 456);
    handoff_wait_handle = LoadGuestU32(base, handoff_guest + 460 + 128);
    if (signal_handle == 0) {
      signal_handle = handoff_wait_handle != 0 ? handoff_wait_handle
                                               : wait_handle_guest;
    }
    if (handoff_wait_handle == 0) {
      handoff_wait_handle = wait_handle_guest;
    }
    if (signal_handle == 0 && handoff_wait_handle == 0) {
      return false;
    }

    asset = handoff_trace.post_gameface_package_handoff_asset;
    if (asset.empty()) {
      if (!handoff_trace.post_gameface_completed_package_key.empty()) {
        asset = handoff_trace.post_gameface_completed_package_key + ".fac";
      } else if (!handoff_trace.first_post_frontend_consumer_request_asset.empty()) {
        const std::string& request =
            handoff_trace.first_post_frontend_consumer_request_asset;
        const size_t dot = request.find_last_of('.');
        asset = (dot == std::string::npos ? request : request.substr(0, dot)) +
                ".fac";
      } else {
        asset = "art/gui/gameface/frontendstartupscreen.fac";
      }
    }

    handoff_trace.post_gameface_package_handoff_signal_emitted = true;
    handoff_trace.post_gameface_package_handoff_signal_handle = signal_handle;
    handoff_trace.post_gameface_package_handoff_signal_lr =
        static_cast<uint32_t>(original_ctx.lr);
    handoff_trace.post_gameface_package_handoff_asset = asset;
    handoff_trace.post_gameface_package_handoff_signal_at =
        std::chrono::steady_clock::now();
  }

  const uint8_t complete_before = LoadGuestU8(base, handoff_guest + 168);
  const uint8_t flag244_before = LoadGuestU8(base, handoff_guest + 244);
  if (source128 != 0xFFFFFFFFu) {
    if (LoadGuestU32(base, handoff_guest + 120) != source128) {
      StoreGuestU8(base, handoff_guest + 132, 1);
    }
    StoreGuestU32(base, handoff_guest + 120, source128);
  }
  if (source124 != 0xFFFFFFFFu) {
    StoreGuestU32(base, handoff_guest + 116, source124);
  }
  StoreGuestU8(base, handoff_guest + 168, 1);
  StoreGuestU8(base, handoff_guest + 244, 1);

  PPCContext signal_ctx = original_ctx;
  signal_ctx.r3.u32 = signal_handle;
  if (signal_ctx.r3.u32 != 0) {
    ::__imp__sub_822C9388(signal_ctx, base);
  }

  PPCContext wait_signal_ctx = original_ctx;
  wait_signal_ctx.r3.u32 = handoff_wait_handle;
  if (wait_signal_ctx.r3.u32 != 0 &&
      wait_signal_ctx.r3.u32 != signal_ctx.r3.u32) {
    ::__imp__sub_822C9388(wait_signal_ctx, base);
  }

  RecordRefiiMainMenuBlockerEvent(
      "post_gameface_completed_package_handoff_retry",
      fmt::format("asset={} source={} lr=0x{:08X} wait_object=0x{:08X} "
                  "worker_wait_handle=0x{:08X} owner=0x{:08X} "
                  "handoff=0x{:08X} signal_handle=0x{:08X} "
                  "handoff_wait_handle=0x{:08X} package_mask=0x{:02X} "
                  "complete=0x{:02X}->0x{:02X} flag244=0x{:02X}->0x{:02X} "
                  "source124=0x{:08X} source128=0x{:08X} "
                  "signal_result=0x{:08X} wait_signal_result=0x{:08X}",
                  asset,
                  signal_source,
                  static_cast<uint32_t>(original_ctx.lr),
                  wait_object_guest,
                  wait_handle_guest,
                  owner_guest,
                  handoff_guest,
                  signal_handle,
                  handoff_wait_handle,
                  package_mask,
                  complete_before,
                  LoadGuestU8(base, handoff_guest + 168),
                  flag244_before,
                  LoadGuestU8(base, handoff_guest + 244),
                  source124,
                  source128,
                  signal_ctx.r3.u32,
                  wait_signal_ctx.r3.u32));
  if (trace ||
      ShouldLogRepeatedVfsTraceOperation(
          "PostGameFaceCompletedPackageHandoffRetry", 8)) {
    REXLOG_INFO("Refii VFS post-GameFace completed package handoff retry "
                "asset={} source={} lr=0x{:08X} wait_object=0x{:08X} "
                "owner=0x{:08X} handoff=0x{:08X} signal_handle=0x{:08X} "
                "handoff_wait_handle=0x{:08X} package_mask=0x{:02X} "
                "complete=0x{:02X}->0x{:02X} flag244=0x{:02X}->0x{:02X} "
                "source124=0x{:08X} source128=0x{:08X} handoff_trace=[{}] "
                "main_menu=[{}]",
                asset,
                signal_source,
                static_cast<uint32_t>(original_ctx.lr),
                wait_object_guest,
                owner_guest,
                handoff_guest,
                signal_handle,
                handoff_wait_handle,
                package_mask,
                complete_before,
                LoadGuestU8(base, handoff_guest + 168),
                flag244_before,
                LoadGuestU8(base, handoff_guest + 244),
                source124,
                source128,
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  return true;
}

bool TryCollapseRefiiCompletedLoadingOwnerWorkerWait(
    PPCContext& ctx,
    const PPCContext& original_ctx,
    uint8_t* base,
    uint32_t wait_object_guest,
    uint32_t wait_handle_guest,
    bool trace) {
  const bool initial_owner_release_ready =
      RefiiPostVideoInitialLoadingOwnerReleaseReady();
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      original_ctx.lr != 0x82B48B08u || original_ctx.r4.u32 != 0x1F4u ||
      original_ctx.r5.u32 != 1u || wait_object_guest == 0 ||
      !RefiiStartupVideosReadyForMenuHandoff() ||
      (!RefiiPostVideoFrontendChainConsumed() &&
       !initial_owner_release_ready)) {
    return false;
  }
  if (!RefiiPostGameFacePackageHandoffSignalEmitted() &&
      !initial_owner_release_ready &&
      !TrySignalRefiiCompletedPostGameFacePackageHandoffFromTrace(
          original_ctx, base, wait_object_guest, wait_handle_guest, trace)) {
    return false;
  }

  const uint32_t worker_owner_guest =
      wait_object_guest >= 0x28u ? wait_object_guest - 0x28u : 0;
  const uint8_t shutdown172_before =
      LooksLikeGuestPointer(worker_owner_guest)
          ? LoadGuestU8(base, worker_owner_guest + 172)
          : 0;
  uint32_t completed_owner =
      RefiiCompletedLoadingScreenOwner().load(std::memory_order_relaxed);
  uint32_t finish_repair_owner =
      RefiiRememberedLoadingScreenOwner().load(std::memory_order_relaxed);
  const uint32_t remembered_finish_repair_owner = finish_repair_owner;
  if (initial_owner_release_ready &&
      !LooksLikeRefiiLoadingScreenSequenceOwner(base, finish_repair_owner)) {
    const uint32_t resolved_owner =
        ResolveRefiiPostFrontendLoadingOwnerCandidate(base);
    if (LooksLikeRefiiLoadingScreenSequenceOwner(base, resolved_owner)) {
      finish_repair_owner = resolved_owner;
      RefiiRememberedLoadingScreenOwner().store(
          finish_repair_owner, std::memory_order_relaxed);
    } else {
      finish_repair_owner = 0;
    }
    if (remembered_finish_repair_owner != 0 &&
        remembered_finish_repair_owner != finish_repair_owner) {
      RecordRefiiMainMenuBlockerEvent(
          "generated_wait_stale_loading_owner_rejected",
          fmt::format("lr=0x{:08X} wait_object=0x{:08X} "
                      "remembered=0x{:08X} resolved=0x{:08X} "
                      "initial_owner_release_ready=true main_menu=[{}]",
                      static_cast<uint32_t>(original_ctx.lr),
                      wait_object_guest,
                      remembered_finish_repair_owner,
                      finish_repair_owner,
                      FormatRefiiMainMenuBlockerTraceForLog()));
      if (trace || ShouldLogRepeatedVfsTraceOperation(
                       "GeneratedWaitStaleLoadingOwnerRejected", 8)) {
        REXLOG_INFO("Refii VFS generated wait stale loading owner rejected "
                    "lr=0x{:08X} wait_object=0x{:08X} "
                    "remembered=0x{:08X} resolved=0x{:08X} "
                    "state=[{}] handoff=[{}] main_menu=[{}]",
                    static_cast<uint32_t>(original_ctx.lr),
                    wait_object_guest,
                    remembered_finish_repair_owner,
                    finish_repair_owner,
                    FormatGeneratedEventWaitState(base, wait_object_guest),
                    FormatRefiiPostVideoHandoffTraceForLog(),
                    FormatRefiiMainMenuBlockerTraceForLog());
      }
    }
  }
  const bool ownerless_after_holder_cleaned =
      RefiiOwnerlessPostVideoAfterHolderCleaned();
  const bool ownerless_vfs_chain_ready =
      RefiiPostVfsChainReadyForLoadingOwnerRelease();
  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kPrimaryOwnerGlobal = kLoadingGlobalBase + 27320u;
  constexpr uint32_t kPrimaryReadyGlobal = kLoadingGlobalBase + 27230u;
  const uint32_t post_gameface_primary_owner =
      LoadGuestU32(base, kPrimaryOwnerGlobal);
  const uint8_t post_gameface_primary_ready =
      LoadGuestU8(base, kPrimaryReadyGlobal);
  const bool post_gameface_primary_ready_for_release =
      LooksLikeGuestPointer(post_gameface_primary_owner) &&
      post_gameface_primary_ready != 0;
  const bool post_loading_final_handoff_ready =
      RefiiPostLoadingFrontendFinalHandoffWakeEmitted();
  if (!LooksLikeRefiiLoadingScreenSequenceOwner(base, finish_repair_owner)) {
    finish_repair_owner =
        LooksLikeRefiiLoadingScreenSequenceOwner(base, completed_owner)
            ? completed_owner
            : 0;
  }
  if (completed_owner == 0 && !LooksLikeGuestPointer(finish_repair_owner) &&
      !ownerless_after_holder_cleaned && !ownerless_vfs_chain_ready &&
      !initial_owner_release_ready) {
    return false;
  }
  const uint32_t release_key =
      LooksLikeGuestPointer(finish_repair_owner)
          ? finish_repair_owner
                 : (completed_owner != 0
                 ? completed_owner
                 : (ownerless_after_holder_cleaned
                        ? RefiiPostLoadingAfterHolderMode5CleanedHolder().load(
                              std::memory_order_relaxed)
                        : wait_object_guest));
  uint32_t release_count = 0;
  static std::mutex release_counts_mutex;
  static std::unordered_map<uint32_t, uint32_t> release_counts;
  {
    std::lock_guard<std::mutex> lock(release_counts_mutex);
    release_count = ++release_counts[release_key];
  }
  const bool vfs_chain_ready_for_release =
      RefiiCollapsedLoadingSequenceReadyForOwnerRelease();
  const bool allow_ownerless_vfs_retry =
      completed_owner == 0 &&
      !LooksLikeGuestPointer(finish_repair_owner) &&
      ownerless_vfs_chain_ready &&
      post_gameface_primary_ready_for_release &&
      !ownerless_after_holder_cleaned &&
      RefiiPostVideoFrontendChainConsumed();
  const bool allow_completed_owner_vfs_release =
      (completed_owner != 0 || post_gameface_primary_ready_for_release ||
       post_loading_final_handoff_ready) &&
      ownerless_vfs_chain_ready &&
      RefiiPostGameFacePackageHandoffSignalEmitted() &&
      RefiiPostVideoFrontendChainConsumed();
  const bool allow_initial_ownerless_release =
      initial_owner_release_ready && completed_owner == 0 &&
      !LooksLikeGuestPointer(finish_repair_owner) &&
      !ownerless_after_holder_cleaned &&
      RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
  const bool allow_initial_ownerless_release_burst =
      allow_initial_ownerless_release && release_count <= 6;
  if (release_count > 1 && !allow_initial_ownerless_release_burst &&
      !allow_ownerless_vfs_retry &&
      !allow_completed_owner_vfs_release) {
    if (trace || ShouldLogRepeatedVfsTraceOperation(
                     "GeneratedEventWaitCompletedOwnerNativePaced", 8)) {
      REXLOG_INFO("Refii VFS generated event wait completed-owner native-paced "
                  "lr=0x{:08X} wait_object=0x{:08X} wait_handle=0x{:08X} "
                  "timeout=0x{:08X} completed_owner=0x{:08X} "
                  "worker_owner=0x{:08X} release_count={} "
                  "shutdown172=0x{:02X} post_gameface_primary=0x{:08X}/{} "
                  "initial_owner_release={} initial_owner_burst={} "
                  "final_handoff_ready={} "
                  "completed_owner_release={} "
                  "state=[{}] handoff=[{}] main_menu=[{}]",
                  static_cast<uint32_t>(original_ctx.lr),
                  wait_object_guest,
                  wait_handle_guest,
                  original_ctx.r4.u32,
                  completed_owner,
                  worker_owner_guest,
                  release_count,
                  shutdown172_before,
                  post_gameface_primary_owner,
                  post_gameface_primary_ready,
                  allow_initial_ownerless_release ? "true" : "false",
                  allow_initial_ownerless_release_burst ? "true" : "false",
                  post_loading_final_handoff_ready ? "true" : "false",
                  allow_completed_owner_vfs_release ? "true" : "false",
                  FormatGeneratedEventWaitState(base, wait_object_guest),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    return false;
  }
  // Generated sub_82B48670 only leaves its 500ms wait loop when byte 172 is
  // raised. Keep the worker alive for the first post-video wake so GameFace and
  // texture follow-on requests can run, then release it once the ownerless VFS
  // chain has actually produced the native-facing menu resource shape.
  bool worker_shutdown_released = false;
  if ((allow_ownerless_vfs_retry || allow_completed_owner_vfs_release) &&
      vfs_chain_ready_for_release &&
      release_count >= 2 && LooksLikeGuestPointer(worker_owner_guest) &&
      shutdown172_before == 0) {
    StoreGuestU8(base, worker_owner_guest + 172, 1);
    worker_shutdown_released = true;
    RecordRefiiMainMenuBlockerEvent(
        "generated_wait_ownerless_worker_shutdown",
        fmt::format("lr=0x{:08X} wait_object=0x{:08X} "
                    "wait_handle=0x{:08X} worker_owner=0x{:08X} "
                    "release_count={} shutdown172=0x{:02X}->0x{:02X} "
                    "completed_owner_release={} globals=[{}]",
                    static_cast<uint32_t>(original_ctx.lr),
                    wait_object_guest,
                    wait_handle_guest,
                    worker_owner_guest,
                    release_count,
                    shutdown172_before,
                    LoadGuestU8(base, worker_owner_guest + 172),
                    allow_completed_owner_vfs_release ? "true" : "false",
                    FormatRefiiLoadingScreenSequenceGlobals(base)));
    if (trace || ShouldLogRepeatedVfsTraceOperation(
                     "GeneratedWaitOwnerlessWorkerShutdown", 8)) {
      REXLOG_INFO("Refii VFS generated wait ownerless worker shutdown "
                  "lr=0x{:08X} wait_object=0x{:08X} "
                  "wait_handle=0x{:08X} worker_owner=0x{:08X} "
                  "release_count={} shutdown172=0x{:02X}->0x{:02X} "
                  "completed_owner_release={} handoff=[{}] main_menu=[{}]",
                  static_cast<uint32_t>(original_ctx.lr),
                  wait_object_guest,
                  wait_handle_guest,
                  worker_owner_guest,
                  release_count,
                  shutdown172_before,
                  LoadGuestU8(base, worker_owner_guest + 172),
                  allow_completed_owner_vfs_release ? "true" : "false",
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
  }
  const uint32_t finalizer_wait_object_guest =
      RefiiNativeVfsWorkerWaitObject().load(std::memory_order_relaxed);
  const uint32_t finalizer_worker_guest =
      finalizer_wait_object_guest >= 20u ? finalizer_wait_object_guest - 20u
                                         : 0;
  const uint8_t finalizer_exit152_before =
      LooksLikeGuestPointer(finalizer_worker_guest)
          ? LoadGuestU8(base, finalizer_worker_guest + 152)
          : 0;
  const uint32_t finalizer_wait_handle_guest =
      RefiiNativeVfsWorkerWaitHandle().load(std::memory_order_relaxed);
  bool finalizer_shutdown_released = false;

  const bool post_loading_native_run =
      TryRunRefiiPostLoadingFrontendRenderRequestNativeFromTrace(
          original_ctx, base, "completed_owner_wait_release");
  const bool post_loading_final_handoff_after_native =
      RefiiPostLoadingFrontendFinalHandoffWakeEmitted();
  const bool post_gameface_primary_can_finalize =
      RefiiPostGameFacePrimaryOwnerReady(base);
  const bool post_loading_finalizer_exit_ready_before_handoff =
      post_loading_native_run &&
      RefiiStartupVideosReadyForMenuHandoff() &&
      RefiiPostGameFacePackageHandoffSignalEmitted() &&
      (RefiiPostGameFacePackageComplete() ||
       RefiiPostGameFacePackageNativeFallbackReady()) &&
      RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
  if (post_loading_finalizer_exit_ready_before_handoff &&
      LooksLikeGuestPointer(finalizer_worker_guest) &&
      LoadGuestU8(base, finalizer_worker_guest + 152) == 0) {
    StoreGuestU8(base, finalizer_worker_guest + 152, 1);
    uint32_t finalizer_signal_result = 0;
    if (finalizer_wait_handle_guest != 0) {
      PPCContext signal_ctx = original_ctx;
      signal_ctx.r3.u32 = finalizer_wait_handle_guest;
      ::__imp__sub_822C9388(signal_ctx, base);
      finalizer_signal_result = signal_ctx.r3.u32;
    }
    finalizer_shutdown_released = true;
    RecordRefiiMainMenuBlockerEvent(
        "generated_wait_completed_owner_finalizer_exit_pre_handoff",
        fmt::format("lr=0x{:08X} wait_object=0x{:08X} "
                    "wait_handle=0x{:08X} finalizer_worker=0x{:08X} "
                    "finalizer_wait=0x{:08X}/0x{:08X} "
                    "release_count={} exit152=0x{:02X}->0x{:02X} "
                    "signal_result=0x{:08X} "
                    "post_loading_native_run=true package_complete=true "
                    "parsed_texture=true",
                    static_cast<uint32_t>(original_ctx.lr),
                    wait_object_guest,
                    wait_handle_guest,
                    finalizer_worker_guest,
                    finalizer_wait_object_guest,
                    finalizer_wait_handle_guest,
                    release_count,
                    finalizer_exit152_before,
                    LoadGuestU8(base, finalizer_worker_guest + 152),
                    finalizer_signal_result));
    if (trace || ShouldLogRepeatedVfsTraceOperation(
                     "GeneratedWaitCompletedOwnerFinalizerExitPreHandoff", 8)) {
      REXLOG_INFO("Refii VFS generated wait completed-owner finalizer exit "
                  "pre-handoff lr=0x{:08X} wait_object=0x{:08X} "
                  "wait_handle=0x{:08X} finalizer_worker=0x{:08X} "
                  "finalizer_wait=0x{:08X}/0x{:08X} "
                  "release_count={} exit152=0x{:02X}->0x{:02X} "
                  "signal_result=0x{:08X} handoff=[{}] main_menu=[{}]",
                  static_cast<uint32_t>(original_ctx.lr),
                  wait_object_guest,
                  wait_handle_guest,
                  finalizer_worker_guest,
                  finalizer_wait_object_guest,
                  finalizer_wait_handle_guest,
                  release_count,
                  finalizer_exit152_before,
                  LoadGuestU8(base, finalizer_worker_guest + 152),
                  finalizer_signal_result,
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
  }
  const bool generated_frame_tail_run =
      worker_shutdown_released
          ? false
          : TryRunRefiiPostVideoGeneratedFrameTailFromCompletedWait(
                original_ctx, base, "completed_owner_wait_release");
  if (!worker_shutdown_released && post_loading_native_run &&
      generated_frame_tail_run &&
      RefiiPostVfsChainReadyForLoadingOwnerRelease() &&
      (completed_owner != 0 || post_gameface_primary_can_finalize ||
       post_loading_final_handoff_after_native) &&
      release_count >= 2 &&
      LooksLikeGuestPointer(worker_owner_guest) &&
      LoadGuestU8(base, worker_owner_guest + 172) == 0) {
    StoreGuestU8(base, worker_owner_guest + 172, 1);
    worker_shutdown_released = true;
    RecordRefiiMainMenuBlockerEvent(
        "generated_wait_completed_owner_worker_shutdown_after_tail",
        fmt::format("lr=0x{:08X} wait_object=0x{:08X} "
                    "wait_handle=0x{:08X} worker_owner=0x{:08X} "
                    "release_count={} shutdown172=0x{:02X}->0x{:02X} "
                    "post_loading_native_run=true generated_frame_tail_run=true "
                    "post_gameface_primary=0x{:08X}/{} "
                    "final_handoff_ready={}",
                    static_cast<uint32_t>(original_ctx.lr),
                    wait_object_guest,
                    wait_handle_guest,
                    worker_owner_guest,
                    release_count,
                    shutdown172_before,
                    LoadGuestU8(base, worker_owner_guest + 172),
                    post_gameface_primary_owner,
                    post_gameface_primary_ready,
                    post_loading_final_handoff_after_native ? "true" : "false"));
    if (trace || ShouldLogRepeatedVfsTraceOperation(
                     "GeneratedWaitCompletedOwnerWorkerShutdownAfterTail", 8)) {
      REXLOG_INFO("Refii VFS generated wait completed-owner worker shutdown "
                  "after tail lr=0x{:08X} wait_object=0x{:08X} "
                  "wait_handle=0x{:08X} worker_owner=0x{:08X} "
                  "release_count={} shutdown172=0x{:02X}->0x{:02X} "
                  "post_gameface_primary=0x{:08X}/{} "
                  "final_handoff_ready={} handoff=[{}] main_menu=[{}]",
                  static_cast<uint32_t>(original_ctx.lr),
                  wait_object_guest,
                  wait_handle_guest,
                  worker_owner_guest,
                  release_count,
                  shutdown172_before,
                  LoadGuestU8(base, worker_owner_guest + 172),
                  post_gameface_primary_owner,
                  post_gameface_primary_ready,
                  post_loading_final_handoff_after_native ? "true" : "false",
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
  }
  const bool native_worker_signaled =
      worker_shutdown_released
          ? false
          : TrySignalRefiiCompletedNativeVfsWorkerWait(
                original_ctx, base, "completed_owner_wait_release", trace);
  bool loading_owner_finish_repair = false;
  bool post_loading_generated_state_released = false;
  static std::atomic<uint32_t> last_finish_repair_owner{0};
  if (vfs_chain_ready_for_release &&
      LooksLikeGuestPointer(finish_repair_owner) &&
      LoadGuestU8(base, finish_repair_owner + 177) == 0 &&
      last_finish_repair_owner.exchange(finish_repair_owner,
                                        std::memory_order_relaxed) !=
          finish_repair_owner) {
    loading_owner_finish_repair =
        TryFinishRefiiLoadingScreenOwnerFromCollapsedPipeline(
            original_ctx,
            base,
            finish_repair_owner,
            "completed_owner_wait_release",
            1,
            LoadGuestU32(base, finish_repair_owner + 388) != 0,
            0);
    completed_owner =
        RefiiCompletedLoadingScreenOwner().load(std::memory_order_relaxed);
  }
  if (post_loading_native_run && RefiiPostVfsChainReadyForLoadingOwnerRelease() &&
      (completed_owner != 0 || post_gameface_primary_can_finalize ||
       post_loading_final_handoff_after_native)) {
    post_loading_generated_state_released =
        TryFinalizeRefiiPostLoadingFrontendGeneratedState(
            original_ctx,
            base,
            "completed_owner_wait_release",
            true,
            true);
  }
  if (post_loading_generated_state_released &&
      LooksLikeGuestPointer(worker_owner_guest) &&
      LoadGuestU8(base, worker_owner_guest + 172) == 0) {
    StoreGuestU8(base, worker_owner_guest + 172, 1);
    worker_shutdown_released = true;
    RecordRefiiMainMenuBlockerEvent(
        "generated_wait_completed_owner_worker_shutdown",
        fmt::format("lr=0x{:08X} wait_object=0x{:08X} "
                    "wait_handle=0x{:08X} worker_owner=0x{:08X} "
                    "release_count={} shutdown172=0x{:02X}->0x{:02X} "
                    "post_loading_generated_state_released=true",
                    static_cast<uint32_t>(original_ctx.lr),
                    wait_object_guest,
                    wait_handle_guest,
                    worker_owner_guest,
                    release_count,
                    shutdown172_before,
                    LoadGuestU8(base, worker_owner_guest + 172)));
    if (trace || ShouldLogRepeatedVfsTraceOperation(
                     "GeneratedWaitCompletedOwnerWorkerShutdown", 8)) {
      REXLOG_INFO("Refii VFS generated wait completed-owner worker shutdown "
                  "lr=0x{:08X} wait_object=0x{:08X} "
                  "wait_handle=0x{:08X} worker_owner=0x{:08X} "
                  "release_count={} shutdown172=0x{:02X}->0x{:02X} "
                  "handoff=[{}] main_menu=[{}]",
                  static_cast<uint32_t>(original_ctx.lr),
                  wait_object_guest,
                  wait_handle_guest,
                  worker_owner_guest,
                  release_count,
                  shutdown172_before,
                  LoadGuestU8(base, worker_owner_guest + 172),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
  }
  if (post_loading_generated_state_released &&
      LooksLikeGuestPointer(finalizer_worker_guest) &&
      LoadGuestU8(base, finalizer_worker_guest + 152) == 0) {
    uint32_t finalizer_signal_result = 0;
    if (finalizer_wait_handle_guest != 0) {
      PPCContext signal_ctx = original_ctx;
      signal_ctx.r3.u32 = finalizer_wait_handle_guest;
      ::__imp__sub_822C9388(signal_ctx, base);
      finalizer_signal_result = signal_ctx.r3.u32;
    }
    finalizer_shutdown_released = true;
    RecordRefiiMainMenuBlockerEvent(
        "generated_wait_completed_owner_finalizer_wake",
        fmt::format("lr=0x{:08X} wait_object=0x{:08X} "
                    "wait_handle=0x{:08X} finalizer_worker=0x{:08X} "
                    "finalizer_wait=0x{:08X}/0x{:08X} "
                    "release_count={} exit152=0x{:02X}->0x{:02X} "
                    "signal_result=0x{:08X} "
                    "post_loading_generated_state_released=true "
                    "native_exit_preserved=true",
                    static_cast<uint32_t>(original_ctx.lr),
                    wait_object_guest,
                    wait_handle_guest,
                    finalizer_worker_guest,
                    finalizer_wait_object_guest,
                    finalizer_wait_handle_guest,
                    release_count,
                    finalizer_exit152_before,
                    LoadGuestU8(base, finalizer_worker_guest + 152),
                    finalizer_signal_result));
    if (trace || ShouldLogRepeatedVfsTraceOperation(
                     "GeneratedWaitCompletedOwnerFinalizerWake", 8)) {
      REXLOG_INFO("Refii VFS generated wait completed-owner finalizer wake "
                  "lr=0x{:08X} wait_object=0x{:08X} "
                  "wait_handle=0x{:08X} finalizer_worker=0x{:08X} "
                  "finalizer_wait=0x{:08X}/0x{:08X} "
                  "release_count={} exit152=0x{:02X}->0x{:02X} "
                  "signal_result=0x{:08X} native_exit_preserved=true "
                  "handoff=[{}] main_menu=[{}]",
                  static_cast<uint32_t>(original_ctx.lr),
                  wait_object_guest,
                  wait_handle_guest,
                  finalizer_worker_guest,
                  finalizer_wait_object_guest,
                  finalizer_wait_handle_guest,
                  release_count,
                  finalizer_exit152_before,
                  LoadGuestU8(base, finalizer_worker_guest + 152),
                  finalizer_signal_result,
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
  }
  const bool post_loading_finalizer_exit_ready =
      RefiiPostLoadingFrontendFinalHandoffWakeEmitted() &&
      (RefiiPostGameFacePackageComplete() ||
       RefiiPostGameFacePackageNativeFallbackReady()) &&
      RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
  if (!finalizer_shutdown_released && post_loading_finalizer_exit_ready &&
      LooksLikeGuestPointer(finalizer_worker_guest) &&
      LoadGuestU8(base, finalizer_worker_guest + 152) == 0) {
    StoreGuestU8(base, finalizer_worker_guest + 152, 1);
    uint32_t finalizer_signal_result = 0;
    if (finalizer_wait_handle_guest != 0) {
      PPCContext signal_ctx = original_ctx;
      signal_ctx.r3.u32 = finalizer_wait_handle_guest;
      ::__imp__sub_822C9388(signal_ctx, base);
      finalizer_signal_result = signal_ctx.r3.u32;
    }
    finalizer_shutdown_released = true;
    RecordRefiiMainMenuBlockerEvent(
        "generated_wait_completed_owner_finalizer_exit_released",
        fmt::format("lr=0x{:08X} wait_object=0x{:08X} "
                    "wait_handle=0x{:08X} finalizer_worker=0x{:08X} "
                    "finalizer_wait=0x{:08X}/0x{:08X} "
                    "release_count={} exit152=0x{:02X}->0x{:02X} "
                    "signal_result=0x{:08X} final_handoff_ready=true",
                    static_cast<uint32_t>(original_ctx.lr),
                    wait_object_guest,
                    wait_handle_guest,
                    finalizer_worker_guest,
                    finalizer_wait_object_guest,
                    finalizer_wait_handle_guest,
                    release_count,
                    finalizer_exit152_before,
                    LoadGuestU8(base, finalizer_worker_guest + 152),
                    finalizer_signal_result));
    if (trace || ShouldLogRepeatedVfsTraceOperation(
                     "GeneratedWaitCompletedOwnerFinalizerExitReleased", 8)) {
      REXLOG_INFO("Refii VFS generated wait completed-owner finalizer exit "
                  "released lr=0x{:08X} wait_object=0x{:08X} "
                  "wait_handle=0x{:08X} finalizer_worker=0x{:08X} "
                  "finalizer_wait=0x{:08X}/0x{:08X} "
                  "release_count={} exit152=0x{:02X}->0x{:02X} "
                  "signal_result=0x{:08X} handoff=[{}] main_menu=[{}]",
                  static_cast<uint32_t>(original_ctx.lr),
                  wait_object_guest,
                  wait_handle_guest,
                  finalizer_worker_guest,
                  finalizer_wait_object_guest,
                  finalizer_wait_handle_guest,
                  release_count,
                  finalizer_exit152_before,
                  LoadGuestU8(base, finalizer_worker_guest + 152),
                  finalizer_signal_result,
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
  }
  ctx.r3.u32 = 0;
  RecordRefiiPostVideoWaitResult(original_ctx.lr,
                                 ctx.r3.u32,
                                 0,
                                 wait_object_guest,
                                 wait_handle_guest);
  RecordRefiiMainMenuBlockerEvent(
      "generated_wait_completed_owner_nonblocking",
      fmt::format("lr=0x{:08X} wait_object=0x{:08X} "
                  "wait_handle=0x{:08X} timeout=0x{:08X} result=0x{:08X} "
                  "completed_owner=0x{:08X} worker_owner=0x{:08X} "
                  "finish_repair_owner=0x{:08X} "
                  "ownerless_after_holder_cleaned={} "
                  "ownerless_vfs_chain_ready={} "
                  "post_gameface_primary=0x{:08X}/{} "
                  "release_count={} shutdown172=0x{:02X}->0x{:02X} "
                  "finalizer_worker=0x{:08X} "
                  "finalizer_exit152=0x{:02X}->0x{:02X} "
                  "native_worker_signaled={} post_loading_native_run={} "
                  "generated_frame_tail_run={} "
                  "loading_owner_finish_repair={} worker_shutdown_released={} "
                  "finalizer_shutdown_released={} "
                  "post_loading_generated_state_released={}",
                  static_cast<uint32_t>(original_ctx.lr),
                  wait_object_guest,
                  wait_handle_guest,
                  original_ctx.r4.u32,
                  ctx.r3.u32,
                  completed_owner,
                  worker_owner_guest,
                  finish_repair_owner,
                  ownerless_after_holder_cleaned ? "true" : "false",
                  ownerless_vfs_chain_ready ? "true" : "false",
                  post_gameface_primary_owner,
                  post_gameface_primary_ready,
                  release_count,
                  shutdown172_before,
                  LooksLikeGuestPointer(worker_owner_guest)
                      ? LoadGuestU8(base, worker_owner_guest + 172)
                      : 0,
                  finalizer_worker_guest,
                  finalizer_exit152_before,
                  LooksLikeGuestPointer(finalizer_worker_guest)
                      ? LoadGuestU8(base, finalizer_worker_guest + 152)
                      : 0,
                  native_worker_signaled ? "true" : "false",
                  post_loading_native_run ? "true" : "false",
                  generated_frame_tail_run ? "true" : "false",
                  loading_owner_finish_repair ? "true" : "false",
                  worker_shutdown_released ? "true" : "false",
                  finalizer_shutdown_released ? "true" : "false",
                  post_loading_generated_state_released ? "true" : "false"));

  if (trace || ShouldLogRepeatedVfsTraceOperation(
                   "GeneratedEventWaitCompletedOwnerNonblocking", 8)) {
    REXLOG_INFO("Refii VFS generated event wait completed-owner nonblocking "
                "lr=0x{:08X} wait_object=0x{:08X} wait_handle=0x{:08X} "
                "timeout=0x{:08X} result=0x{:08X} worker_owner=0x{:08X} "
                "completed_owner=0x{:08X} finish_repair_owner=0x{:08X} "
                "ownerless_after_holder_cleaned={} "
                "ownerless_vfs_chain_ready={} "
                "post_gameface_primary=0x{:08X}/{} "
                "release_count={} shutdown172=0x{:02X}->0x{:02X} "
                "finalizer_worker=0x{:08X} "
                "finalizer_exit152=0x{:02X}->0x{:02X} "
                "native_worker_signaled={} post_loading_native_run={} "
                "generated_frame_tail_run={} "
                "loading_owner_finish_repair={} worker_shutdown_released={} "
                "finalizer_shutdown_released={} "
                "post_loading_generated_state_released={} "
                "state=[{}] handoff=[{}] main_menu=[{}]",
                static_cast<uint32_t>(original_ctx.lr),
                wait_object_guest,
                wait_handle_guest,
                original_ctx.r4.u32,
                ctx.r3.u32,
                worker_owner_guest,
                completed_owner,
                finish_repair_owner,
                ownerless_after_holder_cleaned ? "true" : "false",
                ownerless_vfs_chain_ready ? "true" : "false",
                post_gameface_primary_owner,
                post_gameface_primary_ready,
                release_count,
                shutdown172_before,
                LooksLikeGuestPointer(worker_owner_guest)
                    ? LoadGuestU8(base, worker_owner_guest + 172)
                    : 0,
                finalizer_worker_guest,
                finalizer_exit152_before,
                LooksLikeGuestPointer(finalizer_worker_guest)
                    ? LoadGuestU8(base, finalizer_worker_guest + 152)
                    : 0,
                native_worker_signaled ? "true" : "false",
                post_loading_native_run ? "true" : "false",
                generated_frame_tail_run ? "true" : "false",
                loading_owner_finish_repair ? "true" : "false",
                worker_shutdown_released ? "true" : "false",
                finalizer_shutdown_released ? "true" : "false",
                post_loading_generated_state_released ? "true" : "false",
                FormatGeneratedEventWaitState(base, wait_object_guest),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(original_ctx.lr),
                          "loading_screen_owner",
                          "",
                          "",
                          "native_generated_event_wait",
                          "completed_owner_nonblocking",
                          "Opened",
                          "native_worker_continue");
  return true;
}

bool TryCollapseRefiiCompletedFrontEndAudioSyncWait(
    PPCContext& ctx,
    const PPCContext& original_ctx,
    uint8_t* base,
    uint32_t wait_object_guest,
    uint32_t wait_handle_guest,
    bool trace) {
  const uint32_t lr = static_cast<uint32_t>(original_ctx.lr);
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      lr != 0x82A3ECB0u || original_ctx.r4.u32 != 1000u ||
      original_ctx.r5.u32 != 0u || wait_object_guest == 0 ||
      !RefiiStartupVideosSettledForMenuHandoff() ||
      !RefiiPostVideoFrontendChainConsumed() ||
      !RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload()) {
    return false;
  }

  std::string last_bank;
  uint32_t late_bind_count = 0;
  uint32_t last_lr = 0;
  bool terminal_bank_seen = false;
  int64_t terminal_age_ms = -1;
  {
    std::lock_guard<std::mutex> lock(RefiiFrontEndAudioLoadingTraceMutex());
    const auto& audio_trace = RefiiFrontEndAudioLoadingTraceState();
    last_bank = audio_trace.last_bank;
    late_bind_count = audio_trace.late_bind_count;
    last_lr = audio_trace.last_lr;
    terminal_bank_seen = audio_trace.terminal_bank_seen;
    terminal_age_ms = RefiiAudioTraceAgeMs(audio_trace.terminal_bank_at);
  }

  // Generated sub_82319268 marks the audio-loading thread complete after
  // sub_823191F8 finishes the flagged audio-bank list. With Refii synthetic
  // banks the file work is complete by the final late-bind, but the generated
  // wait can remain asleep on the host thread object. Let the native thread
  // have one normal polling interval after the terminal bank before mirroring
  // the completed wait shape that rex_lhRenderer_SyncThread expects.
  constexpr int64_t kTerminalSettleMs = 1200;
  if (!terminal_bank_seen || terminal_age_ms < kTerminalSettleMs) {
    return false;
  }

  static std::mutex collapse_mutex;
  static std::unordered_set<uint32_t> collapsed_waits;
  bool first_for_wait = false;
  {
    std::lock_guard<std::mutex> lock(collapse_mutex);
    first_for_wait = collapsed_waits.insert(wait_object_guest).second;
  }

  uint32_t signal_result = 0;
  if (wait_handle_guest != 0) {
    PPCContext signal_ctx = original_ctx;
    signal_ctx.r3.u32 = wait_handle_guest;
    ::__imp__sub_822C9388(signal_ctx, base);
    signal_result = signal_ctx.r3.u32;
  }

  ctx.r3.u32 = 2;
  RecordRefiiPostVideoWaitResult(lr,
                                 ctx.r3.u32,
                                 0,
                                 wait_object_guest,
                                 wait_handle_guest);
  RecordRefiiMainMenuBlockerEvent(
      "front_end_audio_sync_wait_completed",
      fmt::format("lr=0x{:08X} wait_object=0x{:08X} "
                  "wait_handle=0x{:08X} result=0x{:08X} "
                  "signal_result=0x{:08X} bank={} bank_lr=0x{:08X} "
                  "count={} terminal_age_ms={} first_for_wait={}",
                  lr,
                  wait_object_guest,
                  wait_handle_guest,
                  ctx.r3.u32,
                  signal_result,
                  last_bank,
                  last_lr,
                  late_bind_count,
                  terminal_age_ms,
                  first_for_wait ? "true" : "false"));
  if (trace || first_for_wait ||
      ShouldLogRepeatedVfsTraceOperation("FrontEndAudioSyncWaitCompleted", 8)) {
    REXLOG_INFO("Refii VFS front-end audio sync wait completed "
                "lr=0x{:08X} wait_object=0x{:08X} wait_handle=0x{:08X} "
                "result=0x{:08X} signal_result=0x{:08X} bank={} "
                "bank_lr=0x{:08X} count={} terminal_age_ms={} "
                "handoff=[{}] main_menu=[{}]",
                lr,
                wait_object_guest,
                wait_handle_guest,
                ctx.r3.u32,
                signal_result,
                last_bank,
                last_lr,
                late_bind_count,
                terminal_age_ms,
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  LogRefiiVfsChainVerdict("frontend_audio",
                          lr,
                          last_bank,
                          "",
                          last_bank,
                          "native_generated_event_wait",
                          "front_end_audio_sync_wait_completed",
                          "Opened",
                          "frontend_post_audio_handoff");
  return true;
}

bool TryCollapseRefiiPostVideoRendererSyncWait(PPCContext& ctx,
                                               const PPCContext& original_ctx,
                                               uint8_t* base,
                                               uint32_t wait_object_guest,
                                               uint32_t wait_handle_guest,
                                               bool trace) {
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      original_ctx.lr != 0x82A3ECB0u || original_ctx.r4.u32 != 0x3E8u ||
      original_ctx.r5.u32 != 0u || wait_object_guest == 0 ||
      !RefiiStartupVideosReadyForMenuHandoff() ||
      !RefiiPostGameFacePackageHandoffSignalEmitted() ||
      !RefiiPostVideoFrontendChainConsumed() ||
      !RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload()) {
    return false;
  }

  const bool loading_owner_finish =
      TryFinishRememberedRefiiLoadingOwnerFromPostFrontendSignal(
          original_ctx, base, "renderer_sync_post_vfs_wait_release");

  uint32_t wait_signal_result = 0;
  if (wait_handle_guest != 0) {
    PPCContext wait_signal_ctx = original_ctx;
    wait_signal_ctx.r3.u32 = wait_handle_guest;
    ::__imp__sub_822C9388(wait_signal_ctx, base);
    wait_signal_result = wait_signal_ctx.r3.u32;
  }

  const uint32_t finalizer_worker_guest =
      wait_object_guest >= 20u ? wait_object_guest - 20u : 0;
  const uint32_t finalizer_signal_handle =
      LooksLikeGuestPointer(finalizer_worker_guest)
          ? LoadGuestU32(base, finalizer_worker_guest + 148)
          : 0;
  uint32_t finalizer_signal_result = 0;
  if (finalizer_signal_handle != 0 &&
      finalizer_signal_handle != wait_handle_guest) {
    PPCContext finalizer_signal_ctx = original_ctx;
    finalizer_signal_ctx.r3.u32 = finalizer_signal_handle;
    ::__imp__sub_822C9388(finalizer_signal_ctx, base);
    finalizer_signal_result = finalizer_signal_ctx.r3.u32;
  }

  ctx.r3.u32 = 2;
  RecordRefiiPostVideoWaitResult(original_ctx.lr,
                                 ctx.r3.u32,
                                 0,
                                 wait_object_guest,
                                 wait_handle_guest);
  RecordRefiiMainMenuBlockerEvent(
      "renderer_sync_post_vfs_nonblocking",
      fmt::format("lr=0x{:08X} wait_object=0x{:08X} "
                  "wait_handle=0x{:08X} timeout=0x{:08X} result=0x{:08X} "
                  "completed_owner=0x{:08X} loading_owner_finish={} "
                  "wait_signal_result=0x{:08X} "
                  "finalizer_worker=0x{:08X} finalizer_signal=0x{:08X} "
                  "finalizer_signal_result=0x{:08X}",
                  static_cast<uint32_t>(original_ctx.lr),
                  wait_object_guest,
                  wait_handle_guest,
                  original_ctx.r4.u32,
                  ctx.r3.u32,
                  RefiiCompletedLoadingScreenOwner().load(
                      std::memory_order_relaxed),
                  loading_owner_finish ? "true" : "false",
                  wait_signal_result,
                  finalizer_worker_guest,
                  finalizer_signal_handle,
                  finalizer_signal_result));

  if (trace || ShouldLogRepeatedVfsTraceOperation(
                   "RendererSyncPostVfsNonblocking", 8)) {
    REXLOG_INFO("Refii VFS renderer sync post-VFS nonblocking "
                "lr=0x{:08X} wait_object=0x{:08X} wait_handle=0x{:08X} "
                "timeout=0x{:08X} result=0x{:08X} "
                "loading_owner_finish={} wait_signal_result=0x{:08X} "
                "finalizer_worker=0x{:08X} "
                "finalizer_signal=0x{:08X} finalizer_signal_result=0x{:08X} "
                "state=[{}] handoff=[{}] "
                "main_menu=[{}]",
                static_cast<uint32_t>(original_ctx.lr),
                wait_object_guest,
                wait_handle_guest,
                original_ctx.r4.u32,
                ctx.r3.u32,
                loading_owner_finish ? "true" : "false",
                wait_signal_result,
                finalizer_worker_guest,
                finalizer_signal_handle,
                finalizer_signal_result,
                FormatGeneratedEventWaitState(base, wait_object_guest),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(original_ctx.lr),
                          "renderer_sync_after_vfs_texture",
                          "",
                          "",
                          "native_renderer_sync_wait",
                          "post_vfs_texture_ready",
                          "Opened",
                          "main_menu_continue");
  return true;
}

bool TryCollapseRefiiPostVideoGeneratedRenderWait(PPCContext& ctx,
                                                  const PPCContext& original_ctx,
                                                  uint8_t* base,
                                                  uint32_t wait_object_guest,
                                                  uint32_t wait_handle_guest,
                                                  bool trace) {
  const uint32_t lr = static_cast<uint32_t>(original_ctx.lr);
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      lr != 0x823882E0u || original_ctx.r4.u32 != 0xFFFFFFFFu ||
      original_ctx.r5.u32 != 0u || wait_object_guest == 0 ||
      !RefiiStartupVideosSettledForMenuHandoff() ||
      !RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload()) {
    return false;
  }

  const bool vfs_frontend_ready =
      RefiiPostGameFacePackageHandoffSignalEmitted() ||
      RefiiPostGameFacePackageComplete() ||
      RefiiMainMenuTraceAllowsPostVideoReadyAdvance() ||
      RefiiMainMenuTextureKnownOwnedByVfsIndex();
  if (!vfs_frontend_ready) {
    return false;
  }

  uint32_t signal_result = 0;
  if (wait_handle_guest != 0) {
    PPCContext signal_ctx = original_ctx;
    signal_ctx.r3.u32 = wait_handle_guest;
    ::__imp__sub_822C9388(signal_ctx, base);
    signal_result = signal_ctx.r3.u32;
  }

  ctx.r3.u32 = 0;
  RecordRefiiPostVideoWaitResult(lr,
                                 ctx.r3.u32,
                                 0,
                                 wait_object_guest,
                                 wait_handle_guest);
  RecordRefiiMainMenuBlockerEvent(
      "post_video_generated_render_wait_nonblocking",
      fmt::format("lr=0x{:08X} wait_object=0x{:08X} "
                  "wait_handle=0x{:08X} timeout=0x{:08X} result=0x{:08X} "
                  "signal_result=0x{:08X} package_signal={} package_complete={} "
                  "ready_advance={} owned_texture={}",
                  lr,
                  wait_object_guest,
                  wait_handle_guest,
                  original_ctx.r4.u32,
                  ctx.r3.u32,
                  signal_result,
                  RefiiPostGameFacePackageHandoffSignalEmitted() ? "true"
                                                                 : "false",
                  RefiiPostGameFacePackageComplete() ? "true" : "false",
                  RefiiMainMenuTraceAllowsPostVideoReadyAdvance() ? "true"
                                                                  : "false",
                  RefiiMainMenuTextureKnownOwnedByVfsIndex() ? "true"
                                                             : "false"));
  if (trace || ShouldLogRepeatedVfsTraceOperation(
                   "PostVideoGeneratedRenderWaitNonblocking", 8)) {
    REXLOG_INFO("Refii VFS post-video generated render wait nonblocking "
                "lr=0x{:08X} wait_object=0x{:08X} wait_handle=0x{:08X} "
                "timeout=0x{:08X} result=0x{:08X} signal_result=0x{:08X} "
                "handoff=[{}] main_menu=[{}]",
                lr,
                wait_object_guest,
                wait_handle_guest,
                original_ctx.r4.u32,
                ctx.r3.u32,
                signal_result,
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  LogRefiiVfsChainVerdict("frontend_post_video",
                          lr,
                          "post_video_generated_render_wait",
                          "",
                          "",
                          "native_generated_event_wait",
                          "render_wait_nonblocking",
                          "Opened",
                          "prepare_frame_or_frontend_request");
  return true;
}

bool TryCollapseRefiiCompletedNativeVfsWorkerWait(PPCContext& ctx,
                                                  const PPCContext& original_ctx,
                                                  uint8_t* base,
                                                  uint32_t wait_object_guest,
                                                  uint32_t wait_handle_guest,
                                                  bool trace) {
  const uint32_t lr = static_cast<uint32_t>(original_ctx.lr);
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      (lr != 0x82B48EC8u && lr != 0x82B48F40u) ||
      original_ctx.r4.u32 != 0xFFFFFFFFu || original_ctx.r5.u32 != 0u ||
      wait_object_guest == 0) {
    return false;
  }

  const uint32_t worker_guest =
      wait_object_guest >= 20u ? wait_object_guest - 20u : 0;
  const uint32_t finalizer_queue_guest =
      LooksLikeGuestPointer(worker_guest) ? worker_guest + 160u : 0;
  const uint32_t queued_before =
      LooksLikeGuestPointer(finalizer_queue_guest)
          ? LoadGuestU32(base, finalizer_queue_guest + 16)
          : 0;
  const uint8_t exit_flag_before =
      LooksLikeGuestPointer(worker_guest) ? LoadGuestU8(base, worker_guest + 152)
                                          : 0;
  if (!RefiiPostGameFacePackageHandoffSignalEmitted() &&
      RefiiPostVideoFrontendChainConsumed() &&
      RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload()) {
    PPCContext signal_ctx = original_ctx;
    TrySignalRefiiCompletedPostGameFacePackageHandoffFromTrace(
        signal_ctx, base, wait_object_guest, wait_handle_guest, trace);
  }
  const bool post_loading_final_handoff_ready =
      RefiiPostLoadingFrontendFinalHandoffWakeEmitted() &&
      RefiiPostGameFacePackageHandoffSignalEmitted() &&
      RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
  const bool vfs_chain_ready =
      RefiiPostVfsChainReadyForLoadingOwnerRelease() ||
      post_loading_final_handoff_ready;
  if (!vfs_chain_ready) {
    // This is generated sub_82B48E88's finalizer thread wait. Native can block
    // forever here, but while Refii owns the startup VFS chain we need the
    // generated loop to keep servicing queued completions and checking its exit
    // byte. Return the same broad shapes the loop already expects: zero exits,
    // nonzero drains the queue/retries.
    if (exit_flag_before != 0) {
      ctx.r3.u32 = 0;
    } else if (queued_before != 0) {
      ctx.r3.u32 = 1;
    } else {
      std::this_thread::sleep_for(std::chrono::milliseconds(16));
      ctx.r3.u32 = 2;
    }
    RecordRefiiPostVideoWaitResult(lr, ctx.r3.u32, 16, wait_object_guest,
                                   wait_handle_guest);
    RecordRefiiMainMenuBlockerEvent(
        "native_vfs_worker_wait_polling",
        fmt::format("lr=0x{:08X} wait_object=0x{:08X} "
                    "wait_handle=0x{:08X} timeout=0x{:08X} result=0x{:08X} "
                    "worker=0x{:08X} queue=0x{:08X} queued={} "
                    "exit152=0x{:02X} startup_ready={} handoff_active={} "
                    "vfs_chain_ready=false",
                    lr,
                    wait_object_guest,
                    wait_handle_guest,
                    original_ctx.r4.u32,
                    ctx.r3.u32,
                    worker_guest,
                    finalizer_queue_guest,
                    queued_before,
                    exit_flag_before,
                    RefiiStartupVideosReadyForMenuHandoff() ? "true"
                                                            : "false",
                    RefiiPostVideoVfsHandoffTraceActive() ? "true"
                                                          : "false"));
    const bool startup_playback_critical =
        RefiiStartupVideosPlaybackCriticalActive();
    const bool log_worker_poll =
        !startup_playback_critical &&
        (trace ||
         (RefiiStartupVideosReadyForMenuHandoff() &&
          ShouldLogRepeatedVfsTraceOperation("NativeVfsWorkerWaitPolling", 8)));
    if (log_worker_poll) {
      REXLOG_INFO("Refii VFS native worker wait polling "
                  "lr=0x{:08X} wait_object=0x{:08X} wait_handle=0x{:08X} "
                  "timeout=0x{:08X} result=0x{:08X} worker=0x{:08X} "
                  "queue=0x{:08X} queued={} exit152=0x{:02X} "
                  "state=[{}] handoff=[{}] main_menu=[{}]",
                  lr,
                  wait_object_guest,
                  wait_handle_guest,
                  original_ctx.r4.u32,
                  ctx.r3.u32,
                  worker_guest,
                  finalizer_queue_guest,
                  queued_before,
                  exit_flag_before,
                  FormatGeneratedEventWaitState(base, wait_object_guest),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    return true;
  }

  // Generated sub_82B48E88 uses the BaseRequestManager finalizer wait as a
  // queue wake when work is pending, and as a shutdown wait once the VFS-owned
  // post-video chain has handed off. Keep those two shapes distinct so we do
  // not accidentally skip queued native VFS completions.
  const bool post_gameface_assets_ready =
      RefiiPostGameFacePackageComplete() ||
      RefiiPostGameFacePackageNativeFallbackReady() ||
      RefiiPostVfsChainReadyForLoadingOwnerRelease() ||
      RefiiOwnerlessPostVideoVfsChainReadyForAfterHolder();
  const bool finalizer_release_ready =
      queued_before == 0 && RefiiStartupVideosReadyForMenuHandoff() &&
      RefiiPostGameFacePackageHandoffSignalEmitted() &&
      post_gameface_assets_ready &&
      RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload() &&
      (RefiiPostVideoFrontendChainConsumed() ||
       RefiiPostVfsChainReadyForLoadingOwnerRelease() ||
       RefiiOwnerlessPostVideoVfsChainReadyForAfterHolder() ||
       post_loading_final_handoff_ready);
  if (queued_before == 0 && exit_flag_before == 0 &&
      !finalizer_release_ready) {
    return false;
  }
  if (finalizer_release_ready && exit_flag_before == 0 &&
      LooksLikeGuestPointer(worker_guest)) {
    StoreGuestU8(base, worker_guest + 152, 1);
  }
  ctx.r3.u32 = (exit_flag_before != 0 || finalizer_release_ready) ? 0u : 1u;
  RecordRefiiPostVideoWaitResult(lr, ctx.r3.u32, 0, wait_object_guest,
                                 wait_handle_guest);
  RecordRefiiMainMenuBlockerEvent(
      "native_vfs_worker_wait_completed_nonblocking",
      fmt::format("lr=0x{:08X} wait_object=0x{:08X} "
                  "wait_handle=0x{:08X} timeout=0x{:08X} result=0x{:08X} "
                    "worker=0x{:08X} queue=0x{:08X} queued={} "
                    "exit152=0x{:02X}->0x{:02X} "
                    "completed_owner=0x{:08X} finalizer_release_ready={}",
                  lr,
                  wait_object_guest,
                  wait_handle_guest,
                  original_ctx.r4.u32,
                  ctx.r3.u32,
                  worker_guest,
                  finalizer_queue_guest,
                  queued_before,
                  exit_flag_before,
                  LooksLikeGuestPointer(worker_guest)
                      ? LoadGuestU8(base, worker_guest + 152)
                      : 0,
                  RefiiCompletedLoadingScreenOwner().load(
                      std::memory_order_relaxed),
                  finalizer_release_ready ? "true" : "false"));

  if (trace || ShouldLogRepeatedVfsTraceOperation(
                   "NativeVfsWorkerWaitCompletedNonblocking", 8)) {
    REXLOG_INFO("Refii VFS native worker wait completed nonblocking "
                "lr=0x{:08X} wait_object=0x{:08X} wait_handle=0x{:08X} "
                "timeout=0x{:08X} result=0x{:08X} worker=0x{:08X} "
                "queue=0x{:08X} queued={} exit152=0x{:02X}->0x{:02X} "
                "finalizer_release_ready={} "
                "state=[{}] handoff=[{}] main_menu=[{}]",
                lr,
                wait_object_guest,
                wait_handle_guest,
                original_ctx.r4.u32,
                ctx.r3.u32,
                worker_guest,
                finalizer_queue_guest,
                queued_before,
                exit_flag_before,
                LooksLikeGuestPointer(worker_guest)
                    ? LoadGuestU8(base, worker_guest + 152)
                    : 0,
                finalizer_release_ready ? "true" : "false",
                FormatGeneratedEventWaitState(base, wait_object_guest),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  LogRefiiVfsChainVerdict("frontend_post_video",
                          lr,
                          "native_vfs_worker",
                          "",
                          "",
                          "native_generated_event_wait",
                          "completed_worker_nonblocking",
                          "Opened",
                          "main_menu_continue");
  return true;
}

bool ShouldTracePostVideoGeneratedEventWait(uint32_t lr,
                                            uint32_t wait_object_guest,
                                            uint32_t wait_handle_guest) {
  if (!RefiiPostVideoVfsHandoffTraceActive()) {
    return false;
  }
  if (RefiiStartupVideosPlaybackCriticalActive()) {
    return false;
  }

  const bool matches_handoff =
      RefiiPostVideoEventMatchesHandoffWait(wait_object_guest) ||
      RefiiPostVideoEventMatchesHandoffWait(wait_handle_guest);
  if (!IsPostVideoGeneratedWaitLr(lr) && !matches_handoff) {
    if (RefiiPostVideoFrontendChainConsumed() ||
        RefiiPostFrontendConsumerHandoffSignalEmitted()) {
      return ShouldLogRepeatedVfsTraceOperation(
          "GeneratedEvent_Wait_AfterPostFrontendSignal",
          24);
    }
    return false;
  }

  return ShouldLogLooseStreamOperation("GeneratedEvent_Wait_PostVideo",
                                       wait_object_guest,
                                       16);
}

bool ShouldRecordPostVideoWaitSnapshot(uint32_t lr) {
  return RefiiLooseAssetBackendEnabled() &&
         (lr == 0x82377D1C || lr == 0x823891C8 || lr == 0x82389308 ||
          lr == 0x82389390);
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

std::string FormatPostVideoFrameChainState(uint8_t* base,
                                           uint32_t owner_guest) {
  const uint32_t handoff_guest =
      owner_guest != 0 ? LoadGuestU32(base, owner_guest + 4) : 0;
  const uint32_t flag_233 =
      handoff_guest != 0 ? LoadGuestU8(base, handoff_guest + 233) : 0;
  const uint32_t flag_244 =
      handoff_guest != 0 ? LoadGuestU8(base, handoff_guest + 244) : 0;
  const uint32_t render_resource =
      handoff_guest != 0 ? LoadGuestU32(base, handoff_guest + 240) : 0;
  const uint32_t render_vtable =
      render_resource != 0 ? LoadGuestU32(base, render_resource) : 0;
  const uint32_t render_slot16 =
      render_vtable != 0 ? LoadGuestU32(base, render_vtable + 16) : 0;

  return fmt::format(
      "owner=0x{:08X} owner_words={} handoff=0x{:08X} "
      "handoff_flags[233,244]=0x{:02X}/0x{:02X} handoff_words={} "
      "handoff_228={} render_resource=0x{:08X} render_words={} "
      "render_vtable=0x{:08X} render_slots={} slot16=0x{:08X}",
      owner_guest,
      FormatGuestWordsIfPointer(base, owner_guest, 8),
      handoff_guest,
      flag_233,
      flag_244,
      FormatGuestWordsIfPointer(base, handoff_guest, 12),
      FormatGuestWordsAtOffsetIfPointer(base, handoff_guest, 228, 8),
      render_resource,
      FormatGuestWordsIfPointer(base, render_resource, 10),
      render_vtable,
      FormatGuestWordsIfPointer(base, render_vtable, 8),
      render_slot16);
}

uint32_t FindPostVideoOwnerFromGlobalRoot(uint8_t* base) {
  constexpr uint32_t kStartupVideoGlobalRoot = 0x83496B0C;
  if (base == nullptr) {
    return 0;
  }

  const uint32_t global_root = LoadGuestU32(base, kStartupVideoGlobalRoot);
  const uint32_t game_state =
      global_root != 0 ? LoadGuestU32(base, global_root + 8) : 0;
  return game_state != 0 ? LoadGuestU32(base, game_state + 40) : 0;
}

std::string FormatCurrentPostVideoFrameChainState(uint8_t* base) {
  return FormatPostVideoFrameChainState(base,
                                        FindPostVideoOwnerFromGlobalRoot(base));
}

float RefiiGuestF32FromWord(uint32_t word) {
  float value = 0.0f;
  std::memcpy(&value, &word, sizeof(value));
  return value;
}

uint32_t RefiiGuestF32ToWord(float value) {
  uint32_t word = 0;
  std::memcpy(&word, &value, sizeof(word));
  return word;
}

double RefiiGuestF64FromWord(uint64_t word) {
  double value = 0.0;
  std::memcpy(&value, &word, sizeof(value));
  return value;
}

uint64_t RefiiGuestF64ToWord(double value) {
  uint64_t word = 0;
  std::memcpy(&word, &value, sizeof(word));
  return word;
}

std::string FormatStartupVideoRenderPollDispatchState(uint8_t* base) {
  constexpr uint32_t kStartupVideoRenderPollObjectGlobal = 0x8349E9EC;
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

std::string FormatPostVideoFrameTimingState(uint8_t* base,
                                            uint32_t owner_guest,
                                            double delta_seconds) {
  const uint32_t handoff_guest =
      owner_guest != 0 ? LoadGuestU32(base, owner_guest + 4) : 0;
  const uint32_t duration_word =
      handoff_guest != 0 ? LoadGuestU32(base, handoff_guest + 4) : 0;
  const uint32_t elapsed_word =
      handoff_guest != 0 ? LoadGuestU32(base, handoff_guest + 8) : 0;
  const float duration = RefiiGuestF32FromWord(duration_word);
  const float elapsed = RefiiGuestF32FromWord(elapsed_word);
  const float remaining = duration - elapsed;
  const double projected_frames =
      delta_seconds > 0.0 ? std::ceil(double(remaining) / delta_seconds) : -1.0;
  const uint32_t flag_233 =
      handoff_guest != 0 ? LoadGuestU8(base, handoff_guest + 233) : 0;
  const uint32_t flag_244 =
      handoff_guest != 0 ? LoadGuestU8(base, handoff_guest + 244) : 0;

  return fmt::format(
      "owner=0x{:08X} handoff=0x{:08X} delta={} delta_ms={} "
      "elapsed={} elapsed_word=0x{:08X} duration={} duration_word=0x{:08X} "
      "remaining={} projected_frames={} flags[233,244]=0x{:02X}/0x{:02X}",
      owner_guest,
      handoff_guest,
      delta_seconds,
      delta_seconds * 1000.0,
      elapsed,
      elapsed_word,
      duration,
      duration_word,
      remaining,
      projected_frames,
      flag_233,
      flag_244);
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

bool ShouldTraceStartupVideoLoopProbe(std::string_view operation,
                                      uint32_t lr,
                                      int interval = 24) {
  if (!kTraceFrontendHandoffDiagnostics || !VfsTracingEnabled() ||
      !VfsVerboseIoTracingEnabled() ||
      RefiiStartupVideosPlaybackCriticalActive() ||
      (!RefiiStartupVideosReadyForMenuHandoff() &&
       !RefiiMainMenuTraceIsHashOnly() &&
       !RefiiMainMenuTraceIsTextureOwnedButNotOpened())) {
    return false;
  }

  const bool is_cursor_poll =
      operation.find("CursorPoll") != std::string_view::npos;
  const bool is_pump = operation.find("Pump") != std::string_view::npos;
  const bool is_ready_poll =
      operation.find("ReadyPoll") != std::string_view::npos;
  const bool is_sleep = operation.find("Sleep") != std::string_view::npos;
  const bool is_update = operation.find("Update") != std::string_view::npos;

  const bool matches_generated_startup_loop =
      (is_cursor_poll && lr == 0x82377C98) ||
      (is_pump && lr == 0x82377D40) ||
      (is_ready_poll && (lr == 0x82377D74 || lr == 0x82377D94)) ||
      (is_sleep && lr == 0x82377D88) ||
      is_update;
  if (!matches_generated_startup_loop) {
    return false;
  }

  return ShouldLogRepeatedVfsTraceOperation(operation, interval);
}

std::string FormatPostVideoCompletionGateState(uint8_t* base,
                                               uint32_t callback_guest) {
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
  const float duration = RefiiGuestF32FromWord(duration_word);
  const float elapsed = RefiiGuestF32FromWord(elapsed_word);
  const uint32_t flag_233 =
      handoff != 0 ? LoadGuestU8(base, handoff + 233) : 0;
  const uint32_t flag_244 =
      handoff != 0 ? LoadGuestU8(base, handoff + 244) : 0;
  const bool should_complete =
      callback_kind == 13 && handoff != 0 && elapsed > duration;

  return fmt::format(
      "callback=0x{:08X} kind={} callback_words={} global_root=0x{:08X} "
      "game_state=0x{:08X} owner=0x{:08X} owner_words={} handoff=0x{:08X} "
      "flags[233,244]=0x{:02X}/0x{:02X} duration={} "
      "duration_word=0x{:08X} elapsed={} elapsed_word=0x{:08X} "
      "should_complete={} handoff_words={} handoff_228={}",
      callback_guest,
      callback_kind,
      FormatGuestWordsIfPointer(base, callback_guest, 12),
      global_root,
      game_state,
      owner,
      FormatGuestWordsIfPointer(base, owner, 8),
      handoff,
      flag_233,
      flag_244,
      duration,
      duration_word,
      elapsed,
      elapsed_word,
      should_complete,
      FormatGuestWordsIfPointer(base, handoff, 12),
      FormatGuestWordsAtOffsetIfPointer(base, handoff, 228, 8));
}

std::string FormatPostVideoDynamicHandoffGetterState(uint8_t* base,
                                                     uint32_t object_guest) {
  const uint32_t inner_guest =
      object_guest != 0 ? LoadGuestU32(base, object_guest + 12) : 0;
  const uint32_t handoff_owner =
      inner_guest != 0 ? LoadGuestU32(base, inner_guest + 16) : 0;
  const uint32_t handoff_guest =
      handoff_owner != 0 ? LoadGuestU32(base, handoff_owner + 4) : 0;
  return fmt::format("object=0x{:08X} object_words={} object_vtable={} "
                     "inner=0x{:08X} inner_words={} handoff_owner=0x{:08X} "
                     "owner_words={} handoff=0x{:08X} handoff_words={} "
                     "wait_words={}",
                     object_guest,
                     FormatGuestWordsIfPointer(base, object_guest, 10),
                     FormatGuestObjectVTableSlots(base, object_guest, 8),
                     inner_guest,
                     FormatGuestWordsIfPointer(base, inner_guest, 8),
                     handoff_owner,
                     FormatGuestWordsIfPointer(base, handoff_owner, 8),
                     handoff_guest,
                     FormatGuestWordsIfPointer(base, handoff_guest, 12),
                     FormatGuestWordsIfPointer(
                         base, handoff_guest != 0 ? handoff_guest + 460 : 0, 8));
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

std::string FormatPostVideoRenderProducerState(uint8_t* base,
                                               uint32_t handoff_guest) {
  const uint32_t signal_handle =
      handoff_guest != 0 ? LoadGuestU32(base, handoff_guest + 456) : 0;
  const uint32_t wait_object = handoff_guest != 0 ? handoff_guest + 460 : 0;
  const uint32_t wait_handle =
      wait_object != 0 ? LoadGuestU32(base, wait_object + 128) : 0;
  const uint32_t source_124 =
      handoff_guest != 0 ? LoadGuestU32(base, handoff_guest + 124) : 0;
  const uint32_t source_128 =
      handoff_guest != 0 ? LoadGuestU32(base, handoff_guest + 128) : 0;
  const uint32_t result_116 =
      handoff_guest != 0 ? LoadGuestU32(base, handoff_guest + 116) : 0;
  const uint32_t frame_120 =
      handoff_guest != 0 ? LoadGuestU32(base, handoff_guest + 120) : 0;
  const uint32_t dirty_132 =
      handoff_guest != 0 ? LoadGuestU8(base, handoff_guest + 132) : 0;
  const uint32_t complete_168 =
      handoff_guest != 0 ? LoadGuestU8(base, handoff_guest + 168) : 0;
  return fmt::format(
      "handoff=0x{:08X} signal=0x{:08X} wait_object=0x{:08X} "
      "wait_handle=0x{:08X} result116=0x{:08X} frame120=0x{:08X} "
      "source124=0x{:08X} source128=0x{:08X} dirty132=0x{:02X} "
      "complete168=0x{:02X} handoff_words={} work_words={} wait_words={}",
      handoff_guest,
      signal_handle,
      wait_object,
      wait_handle,
      result_116,
      frame_120,
      source_124,
      source_128,
      dirty_132,
      complete_168,
      FormatGuestWordsIfPointer(base, handoff_guest, 12),
      FormatGuestWordsAtOffsetIfPointer(base, handoff_guest, 116, 16),
      FormatGuestWordsIfPointer(base, wait_object, 8));
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
