#include "vfs_hooks_private.h"

#include <atomic>
#include <chrono>
#include <bit>
#include <cstdlib>

#include "core/guest_yield_backoff.h"
#include "hooks/ppc_externs.h"
#if REFII_VFS_HAS_RENDERER_RUNTIME
#include "renderer_runtime.h"
#endif
#include <rex/kernel/xboxkrnl/video.h>
#include <rex/system/xvideo.h>

namespace refii::renderer {
#if REFII_VFS_HAS_RENDERER_RUNTIME
bool PlumeRendererBackendEnabled();
#else
// No-renderer shims.
//
// This hook's own work — tracking the guest's startup-video pump and keeping
// the boot sequence moving — is renderer-independent; what it hands the
// renderer is notifications. With no renderer built there is nothing to notify,
// so these are the honest no-ops rather than ten scattered #ifs through 2,800
// lines. Sibling hooks guard per-call-site because they have renderer logic to
// omit; this file does not.
//
// The two predicates must answer FALSE, and that is a behavioural statement,
// not a convenience: line ~1839 asks whether Plume owns the startup Bink draw
// before declining to advance the pump itself. With no Plume, nobody else
// advances it, so the hook must take the path where it does.
inline void NotifyFableStartupVideoPumpInitComplete(uint32_t, uint32_t) {}
inline void NotifyFableStartupVideoPumpDispatchComplete(uint32_t, uint32_t) {}
inline void NotifyFableStartupVideoPumpRunComplete(uint32_t) {}
inline void RepairFableSceneStreamPublishHandoffForWait(const char*, uint8_t*) {}
inline bool PlumeRendererBackendEnabled() { return false; }
inline bool FableStartupVideoPumpActive() { return false; }

inline bool QueryRendererHookThreadTelemetry(uint32_t, const char** last_symbol,
                                             uint64_t* us_since_enter) {
  // Leave the caller's defaults alone and report "no telemetry" — the caller
  // logs these, and inventing values would put fiction in the log.
  (void)last_symbol;
  (void)us_since_enter;
  return false;
}
#endif  // REFII_VFS_HAS_RENDERER_RUNTIME
}  // namespace refii::renderer

namespace {

struct CompactStartupVideoProgress {
  uint32_t update_guest = 0;
  uint32_t frame = 0;
  uint32_t index = 0;
  uint32_t count = 0;
  uint8_t complete = 0;
  uint32_t result = 0;
};

bool ShouldLogNativeStartupPlaybackAudit(std::string_view key,
                                         uint32_t first_count,
                                         uint32_t interval) {
  if (!refii::vfs::kTraceFrontendHandoffDiagnostics ||
      !refii::vfs::VfsTracingEnabled() ||
      !refii::vfs::VfsVerboseIoTracingEnabled() ||
      !refii::vfs::RefiiLooseAssetBackendEnabled()) {
    return false;
  }
  if (refii::vfs::RefiiStartupVideosPlaybackCriticalActive()) {
    return false;
  }

  static std::mutex mutex;
  static std::unordered_map<std::string, uint32_t> counts;

  std::lock_guard<std::mutex> lock(mutex);
  const uint32_t next_count = ++counts[std::string(key)];
  return next_count <= first_count ||
         (interval != 0 && (next_count % interval) == 0);
}

bool ShouldLogNativeStartupBikAudit(std::string_view key, uint32_t limit) {
  if (refii::vfs::RefiiLooseAssetBackendEnabled()) {
    return false;
  }
  if (refii::vfs::RefiiStartupVideosPlaybackCriticalActive()) {
    return false;
  }

  static std::mutex mutex;
  static std::unordered_map<std::string, uint32_t> counts;

  std::lock_guard<std::mutex> lock(mutex);
  const uint32_t next_count = ++counts[std::string(key)];
  return next_count <= limit;
}

bool NativeStartupUpdateHasActiveMovie(uint8_t* base, uint32_t update_guest) {
  if (base == nullptr || !refii::vfs::LooksLikeGuestPointer(update_guest)) {
    return false;
  }

  const uint32_t vector_begin = refii::vfs::LoadGuestU32(base, update_guest + 56);
  const uint32_t vector_end = refii::vfs::LoadGuestU32(base, update_guest + 60);
  const uint32_t video_index = refii::vfs::LoadGuestU32(base, update_guest + 84);
  const uint32_t count =
      vector_end >= vector_begin ? (vector_end - vector_begin) / 8 : 0;
  if (count != 0 && video_index < count) {
    return true;
  }

  constexpr uint32_t kStartupVideoGlobalRoot = 0x83496B0C;
  const uint32_t global_root =
      refii::vfs::LoadGuestU32(base, kStartupVideoGlobalRoot);
  const uint32_t game_state =
      global_root != 0 ? refii::vfs::LoadGuestU32(base, global_root + 8) : 0;
  const uint32_t owner =
      game_state != 0 ? refii::vfs::LoadGuestU32(base, game_state + 40) : 0;
  return owner != 0 && refii::vfs::LoadGuestU32(base, owner + 4) != 0;
}

void NotifyRendererStartupVideoPumpInitIfNeeded(const char* label,
                                                uint8_t* base,
                                                uint32_t wrapper_guest) {
  const std::string_view stage(label);
  const uint32_t update_guest =
      wrapper_guest != 0 ? refii::vfs::LoadGuestU32(base, wrapper_guest + 12)
                         : 0;
  if (stage == "StartupReadyRequest_Update_82378110") {
    refii::renderer::NotifyFableStartupVideoPumpInitComplete(wrapper_guest,
                                                             update_guest);
  } else if (stage == "StartupReadyRequest_Dispatch_82378118") {
    refii::renderer::NotifyFableStartupVideoPumpDispatchComplete(wrapper_guest,
                                                                 update_guest);
  }
}

std::string FormatNativeStartupMovieOwnerSlot(uint8_t* base) {
  constexpr uint32_t kStartupVideoGlobalRoot = 0x83496B0C;
  if (base == nullptr) {
    return "base=null";
  }

  const uint32_t global_root =
      refii::vfs::LoadGuestU32(base, kStartupVideoGlobalRoot);
  const uint32_t game_state =
      global_root != 0 ? refii::vfs::LoadGuestU32(base, global_root + 8) : 0;
  const uint32_t owner =
      game_state != 0 ? refii::vfs::LoadGuestU32(base, game_state + 40) : 0;
  const uint32_t movie_owner =
      owner != 0 ? refii::vfs::LoadGuestU32(base, owner + 4) : 0;
  const uint32_t duration_word =
      movie_owner != 0 ? refii::vfs::LoadGuestU32(base, movie_owner + 4) : 0;
  const uint32_t elapsed_word =
      movie_owner != 0 ? refii::vfs::LoadGuestU32(base, movie_owner + 8) : 0;
  const uint8_t complete_gate =
      movie_owner != 0 ? refii::vfs::LoadGuestU8(base, movie_owner + 233) : 0;
  const uint8_t ready_gate =
      movie_owner != 0 ? refii::vfs::LoadGuestU8(base, movie_owner + 244) : 0;

  return fmt::format(
      "global_root=0x{:08X} game_state=0x{:08X} owner=0x{:08X} "
      "movie_owner=0x{:08X} flags233/244={}/{} duration={} elapsed={} "
      "owner_words={} movie_words={}",
      global_root,
      game_state,
      owner,
      movie_owner,
      complete_gate,
      ready_gate,
      refii::vfs::RefiiGuestF32FromWord(duration_word),
      refii::vfs::RefiiGuestF32FromWord(elapsed_word),
      refii::vfs::FormatGuestWordsIfPointer(base, owner, 8),
      refii::vfs::FormatGuestWordsIfPointer(base, movie_owner, 16));
}

void MaybeLogCompactStartupVideoProgress(uint8_t* base,
                                         uint32_t update_guest,
                                         uint32_t link_register,
                                         uint32_t result) {
  if (!refii::vfs::kTraceFrontendHandoffDiagnostics ||
      !refii::vfs::VfsTracingEnabled() ||
      refii::vfs::VfsVerboseIoTracingEnabled() || base == nullptr ||
      !refii::vfs::LooksLikeGuestPointer(update_guest)) {
    return;
  }

  const uint32_t vector_begin = refii::vfs::LoadGuestU32(base, update_guest + 56);
  const uint32_t vector_end = refii::vfs::LoadGuestU32(base, update_guest + 60);
  const uint32_t count =
      vector_end >= vector_begin ? (vector_end - vector_begin) / 8 : 0;

  CompactStartupVideoProgress progress;
  progress.update_guest = update_guest;
  progress.frame = refii::vfs::LoadGuestU32(base, update_guest + 4);
  progress.index = refii::vfs::LoadGuestU32(base, update_guest + 84);
  progress.count = count;
  progress.complete = refii::vfs::LoadGuestU8(base, update_guest + 96);
  progress.result = result;

  static std::mutex mutex;
  static CompactStartupVideoProgress last;
  static bool have_last = false;

  std::lock_guard<std::mutex> lock(mutex);
  const bool changed =
      !have_last || last.update_guest != progress.update_guest ||
      last.index != progress.index || last.count != progress.count ||
      last.complete != progress.complete || last.result != progress.result;
  const bool heartbeat = progress.frame != 0 && (progress.frame % 180) == 0 &&
                         last.frame != progress.frame;
  if (!changed && !heartbeat) {
    return;
  }

  have_last = true;
  last = progress;
  REXLOG_INFO("Refii VFS startup video progress lr=0x{:08X} update=0x{:08X} "
              "result=0x{:08X} frame={} index={}/{} complete={} boot_ms={} "
              "ready={}",
              link_register,
              progress.update_guest,
              progress.result,
              progress.frame,
              progress.index,
              progress.count,
              progress.complete != 0 ? "true" : "false",
              refii::vfs::RefiiBootTraceAgeMs(),
              refii::vfs::RefiiStartupVideosReadyForMenuHandoff() ? "true"
                                                                  : "false");
}

} // namespace

REX_EXTERN(sub_82377A00) {
  REX_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  if (!refii::vfs::RefiiLooseAssetBackendEnabled()) {
    const bool native_startup_audit =
        !NativeStartupUpdateHasActiveMovie(base, original_ctx.r3.u32) &&
        ShouldLogNativeStartupBikAudit("NativeStartupUpdate_82377A00", 24);
    if (native_startup_audit) {
      REXLOG_INFO("Refii VFS native startup update enter sub_82377A00 "
                  "lr=0x{:08X} update_timing=[{}] owner_slot=[{}] args=[{}]",
                  original_ctx.lr,
                  refii::vfs::FormatStartupVideoUpdateTimingState(
                      base, original_ctx.r3.u32),
                  FormatNativeStartupMovieOwnerSlot(base),
                  refii::vfs::FormatPostVideoBoundaryArgs(base,
                                                          original_ctx));
    }

    __imp__sub_82377A00(ctx, base);
    refii::renderer::NotifyFableStartupVideoPumpRunComplete(
        original_ctx.r3.u32);

    refii::vfs::RecordRefiiStartupUpdateSnapshot(base,
                                                 original_ctx.r3.u32,
                                                 original_ctx.lr,
                                                 ctx.r3.u32);
    MaybeLogCompactStartupVideoProgress(base,
                                        original_ctx.r3.u32,
                                        original_ctx.lr,
                                        ctx.r3.u32);
    if (native_startup_audit) {
      REXLOG_INFO("Refii VFS native startup update exit sub_82377A00 "
                  "lr=0x{:08X} result=0x{:08X} update_after=[{}] "
                  "owner_slot_after=[{}] args_after=[{}]",
                  original_ctx.lr,
                  ctx.r3.u32,
                  refii::vfs::FormatStartupVideoUpdateTimingState(
                      base, original_ctx.r3.u32),
                  FormatNativeStartupMovieOwnerSlot(base),
                  refii::vfs::FormatPostVideoBoundaryArgs(base, ctx));
    }
    return;
  }

  const bool native_startup_audit = ShouldLogNativeStartupPlaybackAudit(
      "NativeStartupUpdate_82377A00", 4, 120);
  const bool trace = refii::vfs::ShouldTraceStartupVideoLoopProbe(
      "StartupVideoUpdate_82377A00", original_ctx.lr, 24);
  if (trace || native_startup_audit) {
    REXLOG_INFO("VFS trace startup video update enter sub_82377A00 "
                "lr=0x{:08X} update_timing=[{}] args=[{}] handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                refii::vfs::FormatStartupVideoUpdateTimingState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82377A00(ctx, base);
  refii::renderer::NotifyFableStartupVideoPumpRunComplete(
      original_ctx.r3.u32);

  if (refii::vfs::SkipStartupVideosEnabled()) {
    refii::vfs::ApplyRefiiSkipStartupVideoReadyPatch(
        base, original_ctx.r3.u32, "startup_update_exit");
  }
  refii::vfs::RecordRefiiStartupUpdateSnapshot(base,
                                                   original_ctx.r3.u32,
                                                   original_ctx.lr,
                                                   ctx.r3.u32);
  MaybeLogCompactStartupVideoProgress(base,
                                      original_ctx.r3.u32,
                                      original_ctx.lr,
                                      ctx.r3.u32);

  if (trace || (refii::vfs::kTraceFrontendHandoffDiagnostics &&
                refii::vfs::VfsTracingEnabled() &&
                refii::vfs::RefiiStartupVideosReadyForMenuHandoff())) {
    refii::vfs::RecordRefiiPostVideoUiEvent("StartupVideoUpdate_82377A00",
                                                original_ctx.lr,
                                                ctx.r3.u32,
                                                original_ctx.r3.u32,
                                                original_ctx.r4.u32,
                                                original_ctx.r5.u32);
  }

  if (trace) {
    REXLOG_INFO("VFS trace startup video update exit sub_82377A00 "
                "lr=0x{:08X} result=0x{:08X} update_timing_after=[{}] "
                "args_after=[{}] handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::FormatStartupVideoUpdateTimingState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatPostVideoBoundaryArgs(base, ctx),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (native_startup_audit && !trace) {
    REXLOG_INFO("Refii VFS native startup update audit sub_82377A00 "
                "lr=0x{:08X} result=0x{:08X} update_after=[{}] "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::FormatStartupVideoUpdateTimingState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
      original_ctx.lr, "StartupVideoUpdate_82377A00", base);
  refii::vfs::MaybeLogRefiiStartupReadyGateTextureOpenVerdict(
      original_ctx.lr, "StartupVideoUpdate_82377A00", base);
  refii::vfs::MaybeLogRefiiStartupVideoReadyNoFinalizerBlocker(
      original_ctx.lr, "StartupVideoUpdate_82377A00", base);
}

REX_EXTERN(sub_82377980) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82377980);

  const PPCContext original_ctx = ctx;
  const bool native_completion_audit = ShouldLogNativeStartupPlaybackAudit(
      "NativeStartupCompletion_82377980", 8, 1);
  const bool trace =
      refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      (refii::vfs::RefiiMainMenuTraceNeedsFrontendHandoff() ||
       refii::vfs::RefiiStartupVideosReadyForMenuHandoff() ||
       refii::vfs::SkipStartupVideosEnabled()) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "StartupVideoReadyCompletion_82377980", 4);
  if (trace || native_completion_audit) {
    REXLOG_INFO("VFS trace startup-video ready completion enter sub_82377980 "
                "lr=0x{:08X} update=0x{:08X} update_timing=[{}] "
                "args=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                refii::vfs::FormatStartupVideoUpdateTimingState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  const bool update_guest_valid =
      base != nullptr &&
      refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32);
  const bool update_complete =
      update_guest_valid &&
      refii::vfs::LoadGuestU8(base, original_ctx.r3.u32 + 96) != 0;
  const bool menu_texture_refii_owned =
      refii::vfs::RefiiMainMenuTraceNeedsFrontendHandoff() ||
      refii::vfs::RefiiMainMenuTraceIsHashOnly() ||
      refii::vfs::RefiiMainMenuTextureKnownOwnedByVfsIndex();
  const bool menu_texture_needs_prime =
      refii::vfs::RefiiMainMenuTraceNeedsTexturePrime();
  const bool startup_videos_settled =
      refii::vfs::RefiiStartupVideosSettledForMenuHandoff();
  if (trace || (refii::vfs::VfsTracingEnabled() && update_complete)) {
    REXLOG_INFO("Refii VFS startup-video ready completion collapse gate "
                "sub_82377980 lr=0x{:08X} update=0x{:08X} backend={} "
                "update_valid={} update_complete={} texture_owned={} "
                "hash_only={} menu_texture_refii_owned={} "
                "menu_texture_needs_prime={} completion=native",
                original_ctx.lr,
                original_ctx.r3.u32,
                refii::vfs::RefiiLooseAssetBackendEnabled() ? "true"
                                                                : "false",
                update_guest_valid ? "true" : "false",
                update_complete ? "true" : "false",
                refii::vfs::RefiiMainMenuTraceIsTextureOwnedButNotOpened()
                    ? "true"
                    : "false",
                refii::vfs::RefiiMainMenuTraceIsHashOnly() ? "true"
                                                               : "false",
                menu_texture_refii_owned ? "true" : "false",
                menu_texture_needs_prime ? "true" : "false");
  }
  if (refii::vfs::RefiiLooseAssetBackendEnabled() && update_complete &&
      menu_texture_refii_owned && !refii::vfs::SkipStartupVideosEnabled() &&
      !startup_videos_settled && refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "StartupVideoReadyCompletionNativeFirst", 4)) {
    REXLOG_INFO("Refii VFS startup-video ready completion keeping native "
                "constructor sub_82377980 lr=0x{:08X} update=0x{:08X} "
                "videos_settled=false update_before=[{}] handoff=[{}] "
                "main_menu=[{}] next=native_completion_then_vfs_handoff",
                original_ctx.lr,
                original_ctx.r3.u32,
                refii::vfs::FormatStartupVideoUpdateTimingState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (trace) {
    REXLOG_INFO("Refii VFS startup-video ready completion collapse skipped "
                "sub_82377980 lr=0x{:08X} update=0x{:08X} "
                "backend={} update_valid={} update_complete={} "
                "texture_owned={} hash_only={} menu_texture_refii_owned={} "
                "videos_ready={} videos_settled={} skip_videos={} "
                "main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                refii::vfs::RefiiLooseAssetBackendEnabled() ? "true"
                                                                : "false",
                update_guest_valid ? "true" : "false",
                update_complete ? "true" : "false",
                refii::vfs::RefiiMainMenuTraceIsTextureOwnedButNotOpened()
                    ? "true"
                    : "false",
                refii::vfs::RefiiMainMenuTraceIsHashOnly() ? "true"
                                                               : "false",
                menu_texture_refii_owned ? "true" : "false",
                refii::vfs::RefiiStartupVideosReadyForMenuHandoff()
                    ? "true"
                    : "false",
                startup_videos_settled ? "true" : "false",
                refii::vfs::SkipStartupVideosEnabled() ? "true" : "false",
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82377980(ctx, base);
  refii::vfs::RecordRefiiStartupUpdateSnapshot(base,
                                                   original_ctx.r3.u32,
                                                   original_ctx.lr,
                                                   ctx.r3.u32);
  refii::vfs::RecordRefiiPostVideoUiEvent("StartupVideoReadyCompletion_82377980",
                                              original_ctx.lr,
                                              ctx.r3.u32,
                                              original_ctx.r3.u32,
                                              original_ctx.r4.u32,
                                              original_ctx.r5.u32);
  if (trace || native_completion_audit) {
    REXLOG_INFO("VFS trace startup-video ready completion exit sub_82377980 "
                "lr=0x{:08X} result=0x{:08X} update_after=[{}] "
                "args_after=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::FormatStartupVideoUpdateTimingState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatPostVideoBoundaryArgs(base, ctx),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    refii::vfs::LogRefiiVfsChainVerdict(
        "frontend_post_video",
        static_cast<uint32_t>(original_ctx.lr),
        "menu_texture_refii_owned",
        "",
        "",
        "startup_ready_completion",
        "sub_82377980_ready_completion",
        "TraceOnce",
        "frontend_texture_consumer_open_or_sub_82A415C0");
  }

  refii::vfs::MaybeLogRefiiStartupReadyGateTextureOpenVerdict(
      original_ctx.lr, "StartupVideoReadyCompletion_82377980", base);
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
      original_ctx.lr, "StartupVideoReadyCompletion_82377980", base);
}

#define REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(function_name, imp_name,     \
                                                label, log_after_state)       \
  REX_EXTERN(function_name) {                                              \
    REX_FUNC_PROLOGUE();                                                      \
                                                                               \
    const PPCContext original_ctx = ctx;                                       \
    const uint32_t request_guest = original_ctx.r3.u32;                        \
    const bool tracked_request =                                               \
        refii::vfs::IsNativeVfsScheduledRequest(base, request_guest);      \
    if (!refii::vfs::RefiiLooseAssetBackendEnabled()) {                       \
      const bool native_startup_audit =                                        \
          tracked_request &&                                                   \
          ShouldLogNativeStartupBikAudit(label, 24);                           \
      std::string before_state;                                                \
      if (native_startup_audit) {                                              \
        before_state = refii::vfs::FormatNativeVfsAsyncRequestState(       \
            base, request_guest);                                              \
        REXLOG_INFO("Refii VFS native scheduled request enter " label          \
                    " lr=0x{:08X} ctr=0x{:08X} r4=0x{:08X} state=[{}]",       \
                    original_ctx.lr,                                           \
                    original_ctx.ctr.u32,                                      \
                    original_ctx.r4.u32,                                       \
                    before_state);                                             \
      }                                                                        \
                                                                               \
      imp_name(ctx, base);                                                     \
      NotifyRendererStartupVideoPumpInitIfNeeded(label, base, request_guest);  \
                                                                               \
      if (native_startup_audit) {                                              \
        REXLOG_INFO("Refii VFS native scheduled request exit " label           \
                    " lr=0x{:08X} result=0x{:08X} before=[{}] after=[{}]",    \
                    original_ctx.lr,                                           \
                    ctx.r3.u32,                                                \
                    before_state,                                              \
                    log_after_state                                            \
                        ? refii::vfs::FormatNativeVfsAsyncRequestState(    \
                              base, request_guest)                             \
                        : std::string{"<skipped_after_destructor>"});         \
      }                                                                        \
      return;                                                                  \
    }                                                                          \
    const bool trace =                                                         \
        refii::vfs::VfsTracingEnabled() &&                                 \
        tracked_request &&                                                     \
        (refii::vfs::RefiiPostVideoVfsHandoffTraceActive() ||              \
         refii::vfs::RefiiStartupVideosReadyForMenuHandoff() ||            \
         refii::vfs::RefiiPostFrontendConsumerHandoffSignalEmitted()) &&   \
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(                    \
            label, 96);                                                        \
    std::string before_state;                                                  \
    if (trace) {                                                               \
      before_state = refii::vfs::FormatNativeVfsAsyncRequestState(         \
          base, request_guest);                                                \
      REXLOG_INFO("VFS trace native scheduled request enter " label            \
                  " lr=0x{:08X} ctr=0x{:08X} r4=0x{:08X} state=[{}] "         \
                  "handoff=[{}] main_menu=[{}]",                              \
                  original_ctx.lr,                                             \
                  original_ctx.ctr.u32,                                        \
                  original_ctx.r4.u32,                                         \
                  before_state,                                                \
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),    \
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());    \
    }                                                                          \
                                                                               \
    imp_name(ctx, base);                                                       \
    NotifyRendererStartupVideoPumpInitIfNeeded(label, base, request_guest);    \
                                                                               \
    if (trace) {                                                               \
      REXLOG_INFO("VFS trace native scheduled request exit " label             \
                  " lr=0x{:08X} result=0x{:08X} before=[{}] after=[{}]",      \
                  original_ctx.lr,                                             \
                  ctx.r3.u32,                                                  \
                  before_state,                                                \
                  log_after_state                                              \
                      ? refii::vfs::FormatNativeVfsAsyncRequestState(      \
                            base, request_guest)                               \
                      : std::string{"<skipped_after_destructor>"});           \
    }                                                                          \
  }

REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_82377E10,
                                         __imp__sub_82377E10,
                                         "StartupReadyRequest_Dtor_82377E10",
                                         false)
REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_82378020,
                                         __imp__sub_82378020,
                                         "StartupReadyRequest_Dtor2_82378020",
                                         false)
REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_82378110,
                                         __imp__sub_82378110,
                                         "StartupReadyRequest_Update_82378110",
                                         true)
REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_82378118,
                                         __imp__sub_82378118,
                                         "StartupReadyRequest_Dispatch_82378118",
                                         true)
REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_8231A748,
                                         __imp__sub_8231A748,
                                         "FrontendRenderRequest_Dtor_8231A748",
                                         false)
REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_8231A9A8,
                                         __imp__sub_8231A9A8,
                                         "FrontendRenderRequest_Dtor2_8231A9A8",
                                         false)
REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_8231AA98,
                                         __imp__sub_8231AA98,
                                         "FrontendRenderRequest_Ready_8231AA98",
                                         true)
REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_8231AAA0,
                                         __imp__sub_8231AAA0,
                                         "FrontendRenderRequest_Run_8231AAA0",
                                         true)
REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_8231AAA8,
                                         __imp__sub_8231AAA8,
                                         "FrontendRenderRequest_Tick_8231AAA8",
                                         true)
REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_8231AAD0,
                                         __imp__sub_8231AAD0,
                                         "FrontendRenderRequest_Commit_8231AAD0",
                                         true)

#undef REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE

#define REFII_PRIMARY_FRONTEND_OWNER_TRACE(function_name, imp_name, label)    \
  REX_EXTERN(function_name) {                                              \
    REX_FUNC_PROLOGUE();                                                      \
    REFII_VFS_NATIVE_PASSTHROUGH(function_name);                              \
                                                                              \
    const PPCContext original_ctx = ctx;                                      \
    const uint32_t owner_guest = original_ctx.r3.u32;                         \
    const uint32_t owner_inner =                                              \
        base != nullptr && refii::vfs::LooksLikeGuestPointer(owner_guest) \
            ? refii::vfs::LoadGuestU32(base, owner_guest + 12)            \
            : 0;                                                              \
    const bool trace =                                                        \
        refii::vfs::VfsTracingEnabled() &&                                \
        refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&              \
        refii::vfs::ShouldLogLooseStreamOperation(                        \
            label, owner_guest, 64);                                          \
    std::string before_owner;                                                 \
    std::string before_inner;                                                 \
    if (trace) {                                                              \
      before_owner =                                                          \
          refii::vfs::FormatGuestWordsIfPointer(base, owner_guest, 16);   \
      before_inner =                                                          \
          refii::vfs::FormatGuestWordsIfPointer(base, owner_inner, 48);   \
      REXLOG_INFO("VFS trace primary frontend owner enter " label             \
                  " lr=0x{:08X} owner=0x{:08X} inner=0x{:08X} "              \
                  "owner_words=[{}] inner_words=[{}] globals=[{}] "          \
                  "handoff=[{}] main_menu=[{}]",                             \
                  original_ctx.lr,                                            \
                  owner_guest,                                                \
                  owner_inner,                                                \
                  before_owner,                                               \
                  before_inner,                                               \
                  refii::vfs::FormatRefiiLoadingScreenSequenceGlobals(    \
                      base),                                                  \
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),   \
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());   \
    }                                                                         \
                                                                              \
    imp_name(ctx, base);                                                      \
                                                                              \
    if (trace) {                                                              \
      const uint32_t after_inner =                                            \
          base != nullptr &&                                                  \
                  refii::vfs::LooksLikeGuestPointer(owner_guest)          \
              ? refii::vfs::LoadGuestU32(base, owner_guest + 12)          \
              : 0;                                                            \
      REXLOG_INFO("VFS trace primary frontend owner exit " label              \
                  " lr=0x{:08X} result=0x{:08X} owner=0x{:08X} "             \
                  "inner=0x{:08X}->0x{:08X} before_owner=[{}] "              \
                  "after_owner=[{}] before_inner=[{}] after_inner=[{}] "     \
                  "globals=[{}]",                                            \
                  original_ctx.lr,                                            \
                  ctx.r3.u32,                                                 \
                  owner_guest,                                                \
                  owner_inner,                                                \
                  after_inner,                                                \
                  before_owner,                                               \
                  refii::vfs::FormatGuestWordsIfPointer(                  \
                      base, owner_guest, 16),                                  \
                  before_inner,                                               \
                  refii::vfs::FormatGuestWordsIfPointer(                  \
                      base, after_inner, 48),                                  \
                  refii::vfs::FormatRefiiLoadingScreenSequenceGlobals(    \
                      base));                                                 \
    }                                                                         \
  }

REFII_PRIMARY_FRONTEND_OWNER_TRACE(sub_82313268,
                                   __imp__sub_82313268,
                                   "PrimaryFrontendOwner_Ready_82313268")
REFII_PRIMARY_FRONTEND_OWNER_TRACE(sub_82313270,
                                   __imp__sub_82313270,
                                   "PrimaryFrontendOwner_Run_82313270")
REFII_PRIMARY_FRONTEND_OWNER_TRACE(sub_82313278,
                                   __imp__sub_82313278,
                                   "PrimaryFrontendOwner_Tick_82313278")

#undef REFII_PRIMARY_FRONTEND_OWNER_TRACE

#define REFII_NATIVE_VFS_SHARED_REQUEST_TRACE(function_name, imp_name, label) \
  REX_EXTERN(function_name) {                                              \
    REX_FUNC_PROLOGUE();                                                      \
    REFII_VFS_NATIVE_PASSTHROUGH(function_name);                              \
                                                                              \
    const PPCContext original_ctx = ctx;                                      \
    const uint32_t request_guest = original_ctx.r3.u32;                       \
    const bool tracked_request =                                              \
        refii::vfs::IsNativeVfsScheduledRequest(base, request_guest);     \
    const bool trace =                                                        \
        refii::vfs::VfsTracingEnabled() && tracked_request &&             \
        (refii::vfs::RefiiPostVideoVfsHandoffTraceActive() ||             \
         refii::vfs::RefiiStartupVideosReadyForMenuHandoff() ||           \
         refii::vfs::RefiiPostFrontendConsumerHandoffSignalEmitted()) &&  \
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(label, 128);       \
    std::string before_state;                                                 \
    if (trace) {                                                              \
      before_state = refii::vfs::FormatNativeVfsAsyncRequestState(        \
          base, request_guest);                                               \
      REXLOG_INFO("VFS trace native scheduled shared enter " label            \
                  " lr=0x{:08X} ctr=0x{:08X} r4=0x{:08X} state=[{}] "        \
                  "handoff=[{}] main_menu=[{}]",                             \
                  original_ctx.lr,                                            \
                  original_ctx.ctr.u32,                                       \
                  original_ctx.r4.u32,                                        \
                  before_state,                                               \
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),   \
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());   \
    }                                                                         \
                                                                              \
    imp_name(ctx, base);                                                      \
                                                                              \
    if (trace) {                                                              \
      REXLOG_INFO("VFS trace native scheduled shared exit " label             \
                  " lr=0x{:08X} result=0x{:08X} before=[{}] after=[{}]",     \
                  original_ctx.lr,                                            \
                  ctx.r3.u32,                                                 \
                  before_state,                                               \
                  refii::vfs::FormatNativeVfsAsyncRequestState(           \
                      base, request_guest));                                  \
    }                                                                         \
  }

REFII_NATIVE_VFS_SHARED_REQUEST_TRACE(sub_82214890,
                                      __imp__sub_82214890,
                                      "ScheduledRequest_Slot8_82214890")
REFII_NATIVE_VFS_SHARED_REQUEST_TRACE(sub_82B96E78,
                                      __imp__sub_82B96E78,
                                      "ScheduledRequest_Noop_82B96E78")
REFII_NATIVE_VFS_SHARED_REQUEST_TRACE(sub_82C2FC78,
                                      __imp__sub_82C2FC78,
                                      "ScheduledRequest_Group_82C2FC78")
REFII_NATIVE_VFS_SHARED_REQUEST_TRACE(sub_82EA63A8,
                                      __imp__sub_82EA63A8,
                                      "ScheduledRequest_Slot20_82EA63A8")
// sub_8231A368 doubles as the title-state input poll (`if (!state[+132] &&
// !byte_8331960F) route input to the press-start screen at state+140`), so it
// gets a hand-written hook instead of the shared request-trace macro: when the
// auto-press testing aid is enabled, emulate the player's Start press exactly
// once by driving the native press handler (sub_82506BD8 case 0: stop title
// sounds, set byte_8331960F, kick the frontend) with the same screen object
// the real input path would use. Lets unattended runs advance into the menu.
REX_EXTERN(sub_8231A368) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_8231A368);

  if (refii::vfs::AutoPressStartEnabled() && base != nullptr &&
      refii::vfs::LoadGuestU8(base, 0x8331960Fu) == 0) {
    const uint32_t state_obj = ctx.r3.u32;
    const bool state_gate_open =
        refii::vfs::LooksLikeGuestPointer(state_obj) &&
        refii::vfs::LoadGuestU32(base, state_obj + 132) == 0;
    const uint32_t screen_obj =
        state_gate_open ? refii::vfs::LoadGuestU32(base, state_obj + 140) : 0;
    if (refii::vfs::LooksLikeGuestPointer(screen_obj) &&
        refii::vfs::LoadGuestU32(base, screen_obj + 4) == 0) {
      static std::atomic<bool> auto_press_fired{false};
      if (!auto_press_fired.exchange(true, std::memory_order_relaxed)) {
        if (PPCFunc* press = refii::vfs::FindGuestFunction(0x82506BD8u)) {
          PPCContext press_ctx = ctx;
          press_ctx.r3.u32 = screen_obj;
          press_ctx.r4.u32 = 0;
          press_ctx.r5.u32 = 0;
          press_ctx.ctr.u32 = 0x82506BD8u;
          press(press_ctx, base);
          REXLOG_INFO("Refii VFS auto press-start fired screen=0x{:08X} "
                      "state=0x{:08X} flag_after=0x{:02X}",
                      screen_obj,
                      state_obj,
                      refii::vfs::LoadGuestU8(base, 0x8331960Fu));
          return;
        }
      }
    }
  }
  __imp__sub_8231A368(ctx, base);
}

#undef REFII_NATIVE_VFS_SHARED_REQUEST_TRACE

REX_EXTERN(sub_82378618) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82378618);

  const PPCContext original_ctx = ctx;
  const bool context_active =
      (refii::vfs::RefiiMainMenuTraceNeedsFrontendHandoff() ||
       refii::vfs::RefiiMainMenuTraceIsHashOnly() ||
       refii::vfs::RefiiStartupVideosReadyForMenuHandoff() ||
       refii::vfs::SkipStartupVideosEnabled());
  const bool trace =
      context_active &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "FrontendTextureConsumer_82378618", 4);
  if (context_active) {
    refii::vfs::BeginFrontendTextureConsumerTrace(original_ctx.r3.u32,
                                                      original_ctx.lr,
                                                      trace);
  }
  if (trace) {
    REXLOG_INFO("VFS trace frontend texture consumer enter sub_82378618 "
                "lr=0x{:08X} object=0x{:08X} args=[{}] object_words={} "
                "streams_180={} streams_184={} list_200={} tex_slots_248={} "
                "tex_slots_260={} tex_slots_268={} tex_out_280={} "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 24),
                refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                    base, original_ctx.r3.u32, 180, 8),
                refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                    base, original_ctx.r3.u32, 184, 8),
                refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                    base, original_ctx.r3.u32, 200, 8),
                refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                    base, original_ctx.r3.u32, 248, 12),
                refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                    base, original_ctx.r3.u32, 260, 8),
                refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                    base, original_ctx.r3.u32, 268, 8),
                refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                    base, original_ctx.r3.u32, 280, 4),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  auto finish_consumer =
      [&](std::string_view mode, std::string_view ui_event_api) {
        if (context_active) {
          refii::vfs::RecordFrontendTextureConsumerOwnerSlotSnapshot(
              base, original_ctx.r3.u32);
        }
        const refii::vfs::ActiveFrontendTextureConsumerTraceContext
            frontend_trace =
                context_active
                    ? refii::vfs::EndFrontendTextureConsumerTrace()
                    : refii::vfs::ActiveFrontendTextureConsumerTraceContext{};
        refii::vfs::RecordRefiiPostVideoUiEvent(ui_event_api,
                                                    original_ctx.lr,
                                                    ctx.r3.u32,
                                                    original_ctx.r3.u32,
                                                    original_ctx.r4.u32,
                                                    original_ctx.r5.u32);
        const bool should_emit_consumer_verdict =
            context_active &&
            refii::vfs::RecordRefiiFrontendTextureConsumerVerdict(
                original_ctx.lr,
                ctx.r3.u32,
                frontend_trace,
                refii::vfs::RefiiMainMenuTraceNeedsFrontendHandoff());

        if (trace) {
          REXLOG_INFO("VFS trace frontend texture consumer exit sub_82378618 "
                      "mode={} lr=0x{:08X} result=0x{:08X} object=0x{:08X} "
                      "object_after={} list_200_after={} tex_slots_248_after={} "
                      "tex_slots_260_after={} tex_slots_268_after={} "
                      "tex_out_280_after={} consumer_trace=[{}] handoff=[{}] "
                      "main_menu=[{}]",
                      mode,
                      original_ctx.lr,
                      ctx.r3.u32,
                      original_ctx.r3.u32,
                      refii::vfs::FormatGuestWordsIfPointer(
                          base, original_ctx.r3.u32, 24),
                      refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                          base, original_ctx.r3.u32, 200, 8),
                      refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                          base, original_ctx.r3.u32, 248, 12),
                      refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                          base, original_ctx.r3.u32, 260, 8),
                      refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                          base, original_ctx.r3.u32, 268, 8),
                      refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                          base, original_ctx.r3.u32, 280, 4),
                      refii::vfs::FormatFrontendTextureConsumerTraceForLog(
                          frontend_trace),
                      refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                      refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
        }
        if (should_emit_consumer_verdict) {
          if (frontend_trace.resolver_empty_refii_owned) {
            refii::vfs::RecordRefiiMainMenuBlockerEvent(
                "frontend_descriptor_resolver_empty_refii_owned",
                fmt::format("lr=0x{:08X} summary=[{}]",
                            original_ctx.lr,
                            refii::vfs::
                                FormatFrontendTextureConsumerTraceForLog(
                                    frontend_trace)));
          }
          REXLOG_INFO("Refii VFS frontend texture consumer verdict lr=0x{:08X} "
                      "result=0x{:08X} any_refii_owned={} generated_next={} "
                      "summary=[{}]",
                      original_ctx.lr,
                      ctx.r3.u32,
                      frontend_trace.any_refii_owned ? "true" : "false",
                      refii::vfs::FrontendTextureConsumerNextBoundary(
                          frontend_trace),
                      refii::vfs::FormatFrontendTextureConsumerTraceForLog(
                          frontend_trace));
          refii::vfs::LogRefiiVfsChainVerdict(
              "frontend_texture_consumer",
              static_cast<uint32_t>(original_ctx.lr),
              "menu_texture_refii_owned",
              "",
              "",
              "frontend_texture_descriptor_consumer",
              "sub_82378618",
              "TraceOnce",
              refii::vfs::FrontendTextureConsumerNextBoundary(
                  frontend_trace));
        }

        refii::vfs::MaybeLogRefiiStartupReadyGateTextureOpenVerdict(
            original_ctx.lr, ui_event_api, base);
        if (refii::vfs::ConsumeDeferredFrontendTextureHashWake()) {
          PPCContext augment_ctx = original_ctx;
          refii::vfs::RerunKnownRequestManagerHashAugmentation(
              augment_ctx,
              base,
              "FrontendTextureConsumer_DeferredTextureWake",
              0,
              "frontend_texture_consumer");
        }
        refii::vfs::MaybeSignalRefiiPostFrontendConsumerHandoff(
            original_ctx, base, frontend_trace, mode);
      };

  __imp__sub_82378618(ctx, base);
  finish_consumer("native", "FrontendTextureConsumer_82378618");
}

REX_EXTERN(sub_826D5E48) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_826D5E48);

  const PPCContext original_ctx = ctx;
  const bool active = refii::vfs::FrontendTextureConsumerTraceActive();
  const bool trace =
      active && refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "FrontendTextureDescriptorBundle_826D5E48", 8);
  if (trace) {
    REXLOG_INFO("VFS trace frontend texture descriptor bundle enter "
                "sub_826D5E48 lr=0x{:08X} object=0x{:08X} args=[{}] "
                "object_words={} handles_80={} handles_96={} "
                "main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 28),
                refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                    base, original_ctx.r3.u32, 80, 8),
                refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                    base, original_ctx.r3.u32, 96, 4),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_826D5E48(ctx, base);

  if (active) {
    refii::vfs::RecordFrontendTextureConsumerResourceBoundary(
        "sub_826D5E48_descriptor_bundle",
        0,
        ctx.r3.u32);
  }
  if (trace) {
    REXLOG_INFO("VFS trace frontend texture descriptor bundle exit "
                "sub_826D5E48 lr=0x{:08X} result=0x{:08X} object=0x{:08X} "
                "object_after={} handles_80_after={} handles_96_after={} "
                "main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                original_ctx.r3.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 28),
                refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                    base, original_ctx.r3.u32, 80, 8),
                refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                    base, original_ctx.r3.u32, 96, 4),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    refii::vfs::LogRefiiVfsChainVerdict(
        "frontend_texture_consumer",
        static_cast<uint32_t>(original_ctx.lr),
        "frontend_descriptor_bundle",
        "",
        "",
        "Fable2_frontend_descriptor_bundle",
        "sub_826D5E48",
        "TraceOnce",
        "sub_821DE2E8_or_frontend_store");
  }
}

REX_EXTERN(sub_821DE2E8) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_821DE2E8);

  const PPCContext original_ctx = ctx;
  const uint32_t descriptor_guest = original_ctx.r3.u32;
  const uint32_t resource_hash =
      descriptor_guest == 0
          ? 0
          : refii::vfs::LoadGuestU32(base, descriptor_guest + 12);
  const uint32_t descriptor_vtable =
      descriptor_guest == 0
          ? 0
          : refii::vfs::LoadGuestU32(base, descriptor_guest + 0);
  const uint32_t descriptor_slot12 =
      descriptor_vtable == 0
          ? 0
          : refii::vfs::LoadGuestU32(base, descriptor_vtable + 12);
  const uint32_t descriptor_slot16 =
      descriptor_vtable == 0
          ? 0
          : refii::vfs::LoadGuestU32(base, descriptor_vtable + 16);
  const uint32_t table_index = original_ctx.r5.u32;
  constexpr uint32_t kFrontendResolverConstantTableBase = 0x8349F9B0;
  const uint32_t table_entry_guest =
      kFrontendResolverConstantTableBase + table_index * 4;
  const uint32_t table_object_before =
      base == nullptr ? 0 : refii::vfs::LoadGuestU32(base, table_entry_guest);
  const std::string table_before =
      refii::vfs::FormatGuestWordsIfPointer(base, table_object_before, 12);
  refii::vfs::LooseAssetResolution resolution;
  std::string name;
  std::string candidate;
  std::string source;
  const bool refii_owned_texture =
      resource_hash != 0 &&
      refii::vfs::ResolveRefiiOwnedTextureDescriptorHash(
          "FrontendTextureResolver_821DE2E8",
          resource_hash,
          static_cast<uint32_t>(original_ctx.lr),
          &resolution,
          &name,
          &candidate,
          &source);
  const bool active = refii::vfs::FrontendTextureConsumerTraceActive();
  const bool verbose =
      refii::vfs::FrontendTextureConsumerVerboseTraceActive();
  if (verbose) {
    REXLOG_INFO("VFS trace frontend texture resolver enter sub_821DE2E8 "
                "lr=0x{:08X} descriptor=0x{:08X} hash=0x{:08X} "
                "r4=0x{:08X} r5=0x{:08X} site={} vtable=0x{:08X} "
                "slot12=0x{:08X} slot16=0x{:08X} table_entry=0x{:08X} "
                "table_object=0x{:08X} table_before=[{}] owned={} name={} "
                "candidate={} source={} descriptor_words=[{}] r4_words=[{}]",
                original_ctx.lr,
                descriptor_guest,
                resource_hash,
                original_ctx.r4.u32,
                original_ctx.r5.u32,
                refii::vfs::LabelFrontendTextureConsumerGeneratedCallSite(
                    original_ctx.lr),
                descriptor_vtable,
                descriptor_slot12,
                descriptor_slot16,
                table_entry_guest,
                table_object_before,
                table_before,
                refii_owned_texture ? "true" : "false",
                refii::vfs::NormalizeLooseAssetKey(name),
                candidate,
                source,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, descriptor_guest, 8),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r4.u32, 8));
  }
  if (active && refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "FrontendTextureResolver_821DE2E8_boundary", 4)) {
    REXLOG_INFO("Refii VFS frontend texture resolver boundary enter "
                "lr=0x{:08X} descriptor=0x{:08X} hash=0x{:08X} "
                "owned={} slot16=0x{:08X} slot12=0x{:08X} "
                "table_index={} table_object=0x{:08X} "
                "generated_next=descriptor_vslot16_or_resolver_exit",
                original_ctx.lr,
                descriptor_guest,
                resource_hash,
                refii_owned_texture ? "true" : "false",
                descriptor_slot16,
                descriptor_slot12,
                table_index,
                table_object_before);
    refii::vfs::LogRefiiVfsChainVerdict(
        "frontend_texture_consumer",
        static_cast<uint32_t>(original_ctx.lr),
        refii_owned_texture ? "menu_texture_refii_owned"
                            : "frontend_descriptor",
        candidate,
        source,
        "frontend_descriptor_resolver",
        "sub_821DE2E8_enter",
        "TraceOnce",
        "descriptor_vslot16_or_resolver_exit");
  }

  const std::string_view collapse_shape =
      refii::vfs::LabelFrontendConstantTableResolverShape(
          descriptor_vtable, descriptor_slot16, descriptor_slot12);
  const bool can_collapse_frontend_constant_table_resolver =
      active && refii_owned_texture && descriptor_guest != 0 &&
      !collapse_shape.empty();
  if (can_collapse_frontend_constant_table_resolver) {
    uint32_t collapsed_result = 0;
    std::string collapse_failure;
    if (refii::vfs::TryResolveRefiiFrontendTextureDescriptor(
            ctx,
            base,
            descriptor_guest,
            resource_hash,
            static_cast<uint32_t>(original_ctx.lr),
            table_index,
            table_object_before,
            resolution,
            name,
            candidate,
            refii_owned_texture ? source : "frontend_constant_table",
            &collapsed_result,
            &collapse_failure)) {
      const uint32_t table_object_after =
          base == nullptr
              ? 0
              : refii::vfs::LoadGuestU32(base, table_entry_guest);
      const std::string table_after =
          refii::vfs::FormatGuestWordsIfPointer(base,
                                                    table_object_after,
                                                    12);
      refii::vfs::RecordFrontendTextureConsumerResolver(
          base,
          original_ctx.lr,
          descriptor_guest,
          resource_hash,
          descriptor_vtable,
          descriptor_slot12,
          descriptor_slot16,
          table_index,
          table_object_after,
          table_before,
          table_after,
          ctx.r3.u32,
          refii_owned_texture);
      if (refii_owned_texture &&
          resource_hash == refii::vfs::kRefiiLoadingAnimationHash) {
        refii::vfs::TryRefreshRememberedLoadingScreenSequenceBridge(
            ctx,
            base,
            "sub_821DE2E8_collapsed_resolver",
            nullptr);
      }
      if (verbose) {
        REXLOG_INFO("VFS trace frontend texture resolver collapsed return "
                    "sub_821DE2E8 "
                    "lr=0x{:08X} descriptor=0x{:08X} hash=0x{:08X} "
                    "result=0x{:08X} native_result=skipped "
                    "refii_fallback={} site={} shape={} "
                    "slot16_inferred=accepted owned={} "
                    "next=sub_826D5E48_continue",
                    original_ctx.lr,
                    descriptor_guest,
                    resource_hash,
                    ctx.r3.u32,
                    refii_owned_texture ? "true" : "constant_table_collapse",
                    refii::vfs::LabelFrontendTextureConsumerGeneratedCallSite(
                        original_ctx.lr),
                    collapse_shape,
                    refii_owned_texture ? "true" : "false");
      }
      return;
    }

    if (refii::vfs::VfsTracingEnabled() ||
        refii::vfs::LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS frontend texture descriptor constant-table "
                  "collapse failed; falling back to native lr=0x{:08X} "
                  "hash=0x{:08X} descriptor=0x{:08X} reason={} "
                  "site={} shape={} table_index={} table_object=0x{:08X}",
                  original_ctx.lr,
                  resource_hash,
                  descriptor_guest,
                  collapse_failure,
                  refii::vfs::LabelFrontendTextureConsumerGeneratedCallSite(
                      original_ctx.lr),
                  collapse_shape,
                  table_index,
                  table_object_before);
    }
  }

  __imp__sub_821DE2E8(ctx, base);

  const uint32_t native_result = ctx.r3.u32;
  bool refii_fallback_succeeded = false;
  uint32_t refii_fallback_result = 0;
  std::string refii_fallback_failure;
  if (refii_owned_texture && native_result == 0xFFFFFFFF) {
    PPCContext fallback_ctx = original_ctx;
    refii_fallback_succeeded =
        refii::vfs::TryResolveRefiiFrontendTextureDescriptor(
            fallback_ctx,
            base,
            descriptor_guest,
            resource_hash,
            static_cast<uint32_t>(original_ctx.lr),
            table_index,
            table_object_before,
            resolution,
            name,
            candidate,
            source,
            &refii_fallback_result,
            &refii_fallback_failure);
    if (refii_fallback_succeeded) {
      ctx = fallback_ctx;
      refii::vfs::RecordFrontendTextureConsumerResourceBoundary(
          "sub_821DE2E8_refii_table_fallback",
          resource_hash,
          refii_fallback_result);
    } else if (refii::vfs::VfsTracingEnabled() ||
               refii::vfs::LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS frontend texture descriptor fallback failed "
                  "lr=0x{:08X} hash=0x{:08X} descriptor=0x{:08X} "
                  "native_result=0x{:08X} reason={} name={} candidate={} "
                  "source={} table_index={} table_object=0x{:08X}",
                  original_ctx.lr,
                  resource_hash,
                  descriptor_guest,
                  native_result,
                  refii_fallback_failure,
                  refii::vfs::NormalizeLooseAssetKey(name),
                  refii::vfs::NormalizeLooseAssetKey(candidate),
                  source,
                  table_index,
                  table_object_before);
      refii::vfs::RecordRefiiMainMenuBlockerEvent(
          "frontend_descriptor_refii_fallback_failed",
          fmt::format("lr=0x{:08X} hash=0x{:08X} result=0x{:08X} "
                      "reason={} name={} candidate={}",
                      original_ctx.lr,
                      resource_hash,
                      native_result,
                      refii_fallback_failure,
                      refii::vfs::NormalizeLooseAssetKey(name),
                      refii::vfs::NormalizeLooseAssetKey(candidate)));
      refii::vfs::LogRefiiVfsChainVerdict(
          "frontend_texture_consumer",
          static_cast<uint32_t>(original_ctx.lr),
          name,
          candidate,
          resolution.provider_relative_path,
          "Fable2_constant_table_descriptor",
          "sub_821DE2E8_refii_fallback_failed",
          "TraceOnce",
          refii_fallback_failure);
    }
  }

  if (active) {
    const uint32_t table_object_after =
        base == nullptr
            ? 0
            : refii::vfs::LoadGuestU32(base, table_entry_guest);
    const std::string table_after =
        refii::vfs::FormatGuestWordsIfPointer(base,
                                                  table_object_after,
                                                  12);
    refii::vfs::RecordFrontendTextureConsumerResolver(
        base,
        original_ctx.lr,
        descriptor_guest,
        resource_hash,
        descriptor_vtable,
        descriptor_slot12,
        descriptor_slot16,
        table_index,
        table_object_after,
        table_before,
        table_after,
        ctx.r3.u32,
        refii_owned_texture);
    if (refii_owned_texture &&
        resource_hash == refii::vfs::kRefiiLoadingAnimationHash) {
      refii::vfs::TryRefreshRememberedLoadingScreenSequenceBridge(
          ctx,
          base,
          "sub_821DE2E8_native_or_fallback_resolver",
          nullptr);
    }
    if (refii_owned_texture && ctx.r3.u32 == 0xFFFFFFFF) {
      refii::vfs::RecordRefiiMainMenuBlockerEvent(
          "frontend_descriptor_resolver_empty_refii_owned",
          fmt::format("resolver lr=0x{:08X} hash=0x{:08X} result=0x{:08X} "
                      "slot12=0x{:08X} slot16=0x{:08X} table_index={} "
                      "table_object=0x{:08X} name={} candidate={} source={}",
                      original_ctx.lr,
                      resource_hash,
                      ctx.r3.u32,
                      descriptor_slot12,
                      descriptor_slot16,
                      table_index,
                      table_object_after,
                      refii::vfs::NormalizeLooseAssetKey(name),
                      candidate,
                      source));
    }
    if (refii_fallback_succeeded) {
      refii::vfs::RecordRefiiMainMenuBlockerEvent(
          "frontend_descriptor_refii_fallback_result",
          fmt::format("resolver lr=0x{:08X} hash=0x{:08X} "
                      "native_result=0x{:08X} resolver_result=0x{:08X} "
                      "name={} candidate={} source={}",
                      original_ctx.lr,
                      resource_hash,
                      native_result,
                      refii_fallback_result,
                      refii::vfs::NormalizeLooseAssetKey(name),
                      candidate,
                      source));
    }
  }
  if (verbose) {
    REXLOG_INFO("VFS trace frontend texture resolver exit sub_821DE2E8 "
                "lr=0x{:08X} descriptor=0x{:08X} hash=0x{:08X} "
                "result=0x{:08X} native_result=0x{:08X} "
                "refii_fallback={} slot16_inferred={} owned={} "
                "result_words=[{}] result_vtable=[{}] table_after=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                descriptor_guest,
                resource_hash,
                ctx.r3.u32,
                native_result,
                refii_fallback_succeeded ? "true" : "false",
                ctx.r3.u32 == 0xFFFFFFFF ? "rejected_or_empty" : "accepted",
                refii_owned_texture ? "true" : "false",
                refii::vfs::FormatGuestWordsIfPointer(base, ctx.r3.u32, 10),
                refii::vfs::FormatGuestObjectVTable(base, ctx.r3.u32, 8),
                refii::vfs::FormatGuestWordsIfPointer(
                    base,
                    base == nullptr
                        ? 0
                        : refii::vfs::LoadGuestU32(base,
                                                       table_entry_guest),
                    12),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

REX_EXTERN(sub_82379D78) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82379D78);

  const PPCContext original_ctx = ctx;
  const bool active = refii::vfs::FrontendTextureConsumerTraceActive();
  const bool verbose =
      refii::vfs::FrontendTextureConsumerVerboseTraceActive();
  if (verbose) {
    REXLOG_INFO("VFS trace frontend texture store-pair enter sub_82379D78 "
                "lr=0x{:08X} dest=0x{:08X} source=0x{:08X} value=0x{:08X} "
                "dest_before=[{}] source_words=[{}] value_words=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                original_ctx.r5.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 6),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r4.u32, 6),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r5.u32, 6));
  }

  __imp__sub_82379D78(ctx, base);

  if (active) {
    refii::vfs::RecordFrontendTextureConsumerStore(base,
                                                       "sub_82379D78",
                                                       original_ctx.lr,
                                                       original_ctx.r3.u32,
                                                       original_ctx.r4.u32,
                                                       original_ctx.r5.u32);
  }
  if (verbose) {
    REXLOG_INFO("VFS trace frontend texture store-pair exit sub_82379D78 "
                "lr=0x{:08X} result=0x{:08X} dest_after=[{}] "
                "source_after=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 6),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r4.u32, 6));
  }
}

REX_EXTERN(sub_82378F00) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82378F00);

  const PPCContext original_ctx = ctx;
  const bool active = refii::vfs::FrontendTextureConsumerTraceActive();
  const bool verbose =
      refii::vfs::FrontendTextureConsumerVerboseTraceActive();
  const uint32_t source_value =
      original_ctx.r4.u32 == 0
          ? 0
          : refii::vfs::LoadGuestU32(base, original_ctx.r4.u32);
  if (verbose) {
    REXLOG_INFO("VFS trace frontend texture slot-store enter sub_82378F00 "
                "lr=0x{:08X} dest=0x{:08X} source=0x{:08X} "
                "source_value=0x{:08X} dest_before=[{}] source_words=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                source_value,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 6),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r4.u32, 6));
  }

  __imp__sub_82378F00(ctx, base);

  if (active) {
    refii::vfs::RecordFrontendTextureConsumerStore(base,
                                                       "sub_82378F00",
                                                       original_ctx.lr,
                                                       original_ctx.r3.u32,
                                                       original_ctx.r4.u32,
                                                       source_value);
  }
  if (verbose) {
    REXLOG_INFO("VFS trace frontend texture slot-store exit sub_82378F00 "
                "lr=0x{:08X} result=0x{:08X} dest_after=[{}] "
                "source_after=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 6),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r4.u32, 6));
  }
}

REX_EXTERN(sub_82CC38E8) { // Lumen: SwitchToThread
  REX_FUNC_PROLOGUE();

  // Spend a long wait sleeping instead of yielding. Placed ahead of everything else in
  // this hook, including the native-passthrough early out, so the policy is not
  // conditional on the loose-asset backend.
  refii::vfs::MaybeBackOffBeforeGuestYield(ctx.lr);

  // PACING GLOBALS (IDA-proven, 2026-07-29). The drain spin's overdue test and its escape
  // budget both divide by flt_8336035C (XVIDEO_MODE.RefreshRate) and multiply by
  // dword_83360358 (PresentationInterval). Their ONLY writer is guest CreateDevice
  // (sub_82B6F6C0 @0x82b6f8f0/0x82b6f90c), which runs only when the front-buffer texture
  // creation in Render_InitPresentationAndFirstSwap succeeds — a region our port is
  // suspected to skip. RefreshRate==0.0 makes the overdue threshold +inf AND the budget
  // +inf simultaneously: the exact unbounded spin measured in runs 437/439 (13.9M-51.8M
  // yields). Diagnose from live guest memory and, if poisoned, write what the guest's own
  // code would have written (values from the same VdQueryVideoMode the guest copy reads).
  // Idempotent: if CreateDevice later runs, it rewrites the same values.
  //
  // TIME-GATED, NOT COUNTED. This used to ride the spin probe's one-in-100,000 branch,
  // and the back-off above cuts that rate by two orders of magnitude — a heal that first
  // arrives 50 s into a run is no heal at all.
  {
    static std::atomic<bool> pacing_globals_settled{false};
    static std::atomic<bool> pacing_globals_healed{false};
    static std::atomic<uint64_t> pacing_globals_next_check_us{0};

    if (!pacing_globals_settled.load(std::memory_order_relaxed)) {
      const uint64_t now_us = static_cast<uint64_t>(
          std::chrono::duration_cast<std::chrono::microseconds>(
              std::chrono::steady_clock::now().time_since_epoch())
              .count());
      uint64_t due = pacing_globals_next_check_us.load(std::memory_order_relaxed);
      if (now_us >= due &&
          pacing_globals_next_check_us.compare_exchange_strong(
              due, now_us + 100000ull, std::memory_order_relaxed)) {
        constexpr uint32_t kGuestRefreshRateFloat = 0x8336035Cu;
        constexpr uint32_t kGuestPresentInterval = 0x83360358u;
        constexpr uint32_t kGuestDisplayPairW = 0x834A5FE8u;
        const uint32_t refresh_bits =
            refii::vfs::LoadGuestU32(base, kGuestRefreshRateFloat);
        if (refresh_bits == 0u &&
            !pacing_globals_healed.exchange(true, std::memory_order_relaxed)) {
          rex::system::X_VIDEO_MODE mode;
          rex::kernel::xboxkrnl::VdQueryVideoMode(&mode);
          const float refresh = mode.refresh_rate;
          refii::vfs::StoreGuestU32(base, kGuestRefreshRateFloat,
                                    std::bit_cast<uint32_t>(refresh));
          if (refii::vfs::LoadGuestU32(base, kGuestPresentInterval) == 0u) {
            refii::vfs::StoreGuestU32(base, kGuestPresentInterval, 1u);
          }
          REXLOG_INFO(
              "Refii pacing globals HEALED refresh={} interval={} "
              "display_pair=0x{:08X}/0x{:08X} (guest CreateDevice stores "
              "never ran — sub_82B6F6C0 skipped)",
              refresh,
              refii::vfs::LoadGuestU32(base, kGuestPresentInterval),
              refii::vfs::LoadGuestU32(base, kGuestDisplayPairW),
              refii::vfs::LoadGuestU32(base, kGuestDisplayPairW + 4u));
        } else if (refresh_bits != 0u) {
          REXLOG_INFO(
              "Refii pacing globals OK refresh_bits=0x{:08X} ({}) "
              "interval={} display_pair=0x{:08X}/0x{:08X}",
              refresh_bits, std::bit_cast<float>(refresh_bits),
              refii::vfs::LoadGuestU32(base, kGuestPresentInterval),
              refii::vfs::LoadGuestU32(base, kGuestDisplayPairW),
              refii::vfs::LoadGuestU32(base, kGuestDisplayPairW + 4u));
          pacing_globals_settled.store(true, std::memory_order_relaxed);
        }
      }
    }
  }

  // SPIN PROBE. The instrument the yield back-off is measured with, and the record of
  // what the yields turned out to be.
  //
  // Run 011 (2026-08-19, 817.8 s): 2,359,000,000 yields, 2.88 M/s, from exactly two
  // threads — 52.5% at lr=0x822DFF2C (the stream-drain wait in sub_822DFE08) and 46.2%
  // at lr=0x82277348 (the "no game tick due yet" wait in the main loop sub_82276D10).
  // At a measured 449 ns per sched_yield that is ~1.3 cores spent on nothing.
  //
  // BOTH ORIGINAL HYPOTHESES ARE DEAD, and the counters are what killed them:
  // from_present_throttle stayed 0, so the two-frame Present throttle at 0x82B6F380 is
  // not involved; and the pacing globals read back RefreshRate 60 / interval 2, so the
  // +inf-budget unbounded spin of runs 437/439 is not happening either. These are
  // ordinary waits that the 360 could afford to burn a hardware thread on and a host
  // cannot.
  //
  // Counting by CALLER (lr), because "SwitchToThread is hot" on its own would not
  // separate one waiting loop from another.
  {
    static std::atomic<uint64_t> total{0};
    static std::atomic<uint64_t> from_present_throttle{0};
    const uint32_t lr = ctx.lr;
    const bool present_throttle = lr >= 0x82B6F380u && lr <= 0x82B6F410u;
    if (present_throttle) {
      from_present_throttle.fetch_add(1, std::memory_order_relaxed);
    }
    const uint64_t n = total.fetch_add(1, std::memory_order_relaxed) + 1;
    if ((n % 100000ull) == 0ull) {
      REXLOG_INFO("Refii guest SwitchToThread spin probe total={} "
                  "from_present_throttle={} backoff_sleeps={} last_lr=0x{:08X}",
                  static_cast<unsigned long long>(n),
                  static_cast<unsigned long long>(
                      from_present_throttle.load(std::memory_order_relaxed)),
                  static_cast<unsigned long long>(
                      refii::vfs::GuestYieldBackoffSleepCount()),
                  lr);
      // Slot autopsy for the stream-drain spin (0x822DFF2C, inside
      // sub_822DFE08): IDA-proven r31 = the command-stream slot being
      // drained. The spin ends only when the OWNING guest thread (tid at
      // slot+100) publishes its shared WPTR (slot[15], guarded by the CS at
      // slot+72). Run 437 spent 51s here hostaging the GPU-access lock and
      // the loading-screen fence (black logo videos) — this names the
      // parked producer thread so we can see what OUR runtime has it
      // blocked in. Fields: [1]=buffer, [3]=read cursor, [4]=WPTR snapshot,
      // [15]=shared WPTR.
      if (lr == 0x822DFF2Cu) {
        const uint32_t slot = ctx.r31.u32;
        if (refii::vfs::LooksLikeGuestPointer(slot)) {
          const uint32_t owner_tid =
              refii::vfs::LoadGuestU32(base, slot + 100u);
          const uint32_t read_cursor =
              refii::vfs::LoadGuestU32(base, slot + 12u);
          const uint32_t wptr_snapshot =
              refii::vfs::LoadGuestU32(base, slot + 16u);
          const uint32_t shared_wptr =
              refii::vfs::LoadGuestU32(base, slot + 60u);
          // Autopsy of the PARKED owner: its most recent hooked guest symbol
          // and how long ago that hook entered (registry written on every
          // hook enter; see NoteRendererHookThreadTelemetry). A stale
          // symbol + large us_since pins where tid parked; "none" means the
          // owner never passed through a renderer hook.
          const char* owner_last_hook = "none";
          uint64_t owner_hook_age_us = 0;
          refii::renderer::QueryRendererHookThreadTelemetry(
              owner_tid, &owner_last_hook, &owner_hook_age_us);
          REXLOG_INFO(
              "Refii guest stream-drain spin slot=0x{:08X} owner_tid=0x{:08X} "
              "read=0x{:08X} wptr_snap=0x{:08X} shared_wptr=0x{:08X} "
              "owner_last_hook={} owner_hook_age_us={}",
              slot, owner_tid, read_cursor, wptr_snapshot, shared_wptr,
              owner_last_hook, owner_hook_age_us);
        }
      }
    }
  }

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82CC38E8);

  const PPCContext original_ctx = ctx;
  if (original_ctx.lr == 0x82377D40u) {
    // sub_82377A00 calls SwitchToThread immediately after publishing a newly
    // constructed startup-frame owner into global_owner+4.  Keep this probe at
    // that native boundary so an address-reused second Bink can be
    // distinguished from an owner that was never published or was cleared.
    const uint32_t root = refii::vfs::LoadGuestU32(base, 0x83496B0Cu);
    const uint32_t root_state =
        refii::vfs::LooksLikeGuestPointer(root)
            ? refii::vfs::LoadGuestU32(base, root + 8u)
            : 0u;
    const uint32_t owner =
        refii::vfs::LooksLikeGuestPointer(root_state)
            ? refii::vfs::LoadGuestU32(base, root_state + 40u)
            : 0u;
    const uint32_t frame_object =
        refii::vfs::LooksLikeGuestPointer(owner)
            ? refii::vfs::LoadGuestU32(base, owner + 4u)
            : 0u;
    const uint32_t movie =
        refii::vfs::LooksLikeGuestPointer(frame_object)
            ? refii::vfs::LoadGuestU32(base, frame_object + 76u)
            : 0u;
    static std::atomic<uint32_t> owner_publish_logs{0};
    static std::atomic<uint32_t> last_published_frame_object{0};
    static std::atomic<uint32_t> last_published_movie_total{0};
    static std::atomic<uint32_t> last_published_owner_flags{0};
    const uint32_t count =
        owner_publish_logs.fetch_add(1, std::memory_order_relaxed) + 1u;
    const uint32_t previous_frame_object =
        last_published_frame_object.exchange(frame_object,
                                             std::memory_order_relaxed);
    const uint32_t movie_total =
        refii::vfs::LooksLikeGuestPointer(movie)
            ? refii::vfs::LoadGuestU32(base, movie + 8u)
            : 0u;
    const uint32_t owner_flags =
        refii::vfs::LooksLikeGuestPointer(frame_object)
            ? (static_cast<uint32_t>(
                   refii::vfs::LoadGuestU8(base, frame_object + 233u)) |
               (static_cast<uint32_t>(
                    refii::vfs::LoadGuestU8(base, frame_object + 244u))
                << 8u))
            : 0u;
    const uint32_t previous_movie_total =
        last_published_movie_total.exchange(movie_total,
                                            std::memory_order_relaxed);
    const uint32_t previous_owner_flags =
        last_published_owner_flags.exchange(owner_flags,
                                            std::memory_order_relaxed);
    if (count <= 8u || previous_frame_object != frame_object ||
        previous_movie_total != movie_total ||
        previous_owner_flags != owner_flags) {
      REXLOG_INFO(
          "Refii VFS startup-video native owner publish boundary #{} "
          "root=0x{:08X} state=0x{:08X} owner=0x{:08X} previous=0x{:08X} "
          "frame_object=0x{:08X} finished={} failed={} movie=0x{:08X} "
          "frame={}/{} previous_total={} previous_flags=0x{:04X}",
          count, root, root_state, owner, previous_frame_object, frame_object,
          refii::vfs::LooksLikeGuestPointer(frame_object)
              ? refii::vfs::LoadGuestU8(base, frame_object + 233u)
              : 0u,
          refii::vfs::LooksLikeGuestPointer(frame_object)
              ? refii::vfs::LoadGuestU8(base, frame_object + 244u)
              : 0u,
          movie,
          refii::vfs::LooksLikeGuestPointer(movie)
              ? refii::vfs::LoadGuestU32(base, movie + 12u)
              : 0u,
          movie_total, previous_movie_total, previous_owner_flags);
    }
  }
  const bool trace = refii::vfs::ShouldTraceStartupVideoLoopProbe(
      "StartupVideoLoop_Pump_82CC38E8", original_ctx.lr, 12);
  const auto started_at = std::chrono::steady_clock::now();
  if (trace) {
    REXLOG_INFO("VFS trace startup video loop pump enter sub_82CC38E8 "
                "lr=0x{:08X} render_poll=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                refii::vfs::FormatStartupVideoRenderPollDispatchState(base),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82CC38E8(ctx, base);
  refii::vfs::MaybeLogRefiiStartupVideoReadyNoFinalizerBlocker(
      original_ctx.lr, "StartupVideoLoop_Pump_82CC38E8", base);

  if (trace) {
    const auto elapsed_ms =
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - started_at)
            .count();
    REXLOG_INFO("VFS trace startup video loop pump exit sub_82CC38E8 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} render_poll=[{}] "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatStartupVideoRenderPollDispatchState(base),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

REX_EXTERN(sub_822F3B20) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_822F3B20);

  const PPCContext original_ctx = ctx;
  const bool trace = refii::vfs::ShouldTraceStartupVideoLoopProbe(
      "StartupVideoLoop_ReadyPoll_822F3B20", original_ctx.lr, 12);
  const auto started_at = std::chrono::steady_clock::now();
  if (trace) {
    REXLOG_INFO("VFS trace startup video loop ready poll enter sub_822F3B20 "
                "lr=0x{:08X} object=0x{:08X} object_words={} handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 10),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  if (refii::vfs::SkipStartupVideosEnabled()) {
    refii::vfs::ApplyRefiiSkipStartupVideoReadyPatch(
        base,
        refii::vfs::LastRefiiStartupUpdateGuest(),
        "ready_poll_pre_native");
    refii::vfs::ApplyRefiiStartupReadyPollObjectPatch(
        base, original_ctx.r3.u32, "ready_poll_pre_native");
  }

  __imp__sub_822F3B20(ctx, base);
  const uint32_t native_result = ctx.r3.u32;
  refii::vfs::RecordRefiiStartupReadyPoll(
      base, original_ctx.r3.u32, original_ctx.lr, native_result);
  if (const uint32_t update_guest =
          refii::vfs::LastRefiiStartupUpdateGuest();
      update_guest >= 0x10000) {
    refii::vfs::RecordRefiiStartupUpdateSnapshot(
        base, update_guest, original_ctx.lr, native_result);
  }

  if (native_result == 0 && refii::vfs::SkipStartupVideosEnabled() &&
      refii::vfs::RefiiMainMenuTraceIsTextureOwnedButNotOpened()) {
    ctx.r3.u32 = 1;
    refii::vfs::RecordRefiiStartupReadyPoll(
        base, original_ctx.r3.u32, original_ctx.lr, ctx.r3.u32);
    if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
        refii::vfs::VfsTracingEnabled()) {
      REXLOG_INFO("Refii VFS skip startup-video ready result override "
                  "lr=0x{:08X} object=0x{:08X} native_result=0x{:08X} "
                  "result=0x{:08X} ready_state=[{}]",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  native_result,
                  ctx.r3.u32,
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog());
    }
  }

  if (trace) {
    const auto elapsed_ms =
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - started_at)
            .count();
    REXLOG_INFO("VFS trace startup video loop ready poll exit sub_822F3B20 "
                "lr=0x{:08X} object=0x{:08X} native_result=0x{:08X} "
                "result=0x{:08X} "
                "elapsed_ms={} object_after={} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                native_result,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 10),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  refii::vfs::MaybeLogRefiiStartupReadyGateTextureOpenVerdict(
      original_ctx.lr, "StartupVideoLoop_ReadyPoll_822F3B20", base);
}

REX_EXTERN(rex_Sleep) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_Sleep);

  const PPCContext original_ctx = ctx;
  const bool trace = refii::vfs::ShouldTraceStartupVideoLoopProbe(
      "StartupVideoLoop_Sleep", original_ctx.lr, 1);
  if (trace) {
    REXLOG_INFO("VFS trace startup video loop sleep enter rex_Sleep "
                "lr=0x{:08X} millis={} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  if (original_ctx.lr == 0x8222A680u) {
    refii::renderer::RepairFableSceneStreamPublishHandoffForWait(
        "sub_8222A5D8_sleep", base);
  }
  __imp__rex_Sleep(ctx, base);
  if (original_ctx.lr == 0x8222A680u) {
    refii::renderer::RepairFableSceneStreamPublishHandoffForWait(
        "sub_8222A5D8_sleep_exit", base);
  }
  refii::vfs::MaybeLogRefiiStartupVideoReadyNoFinalizerBlocker(
      original_ctx.lr, "StartupVideoLoop_Sleep", base);
  if (const uint32_t update_guest =
          refii::vfs::LastRefiiStartupUpdateGuest();
      update_guest >= 0x10000) {
    refii::vfs::RecordRefiiStartupUpdateSnapshot(
        base, update_guest, original_ctx.lr, ctx.r3.u32);
  }
  if (trace) {
    REXLOG_INFO("VFS trace startup video loop sleep exit rex_Sleep "
                "lr=0x{:08X} millis={} result=0x{:08X} handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                ctx.r3.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

REX_EXTERN(sub_821E2C08) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_821E2C08);

  const PPCContext original_ctx = ctx;
  const bool trace =
      refii::vfs::kTraceNonVfsBootFrameProbes &&
      refii::vfs::VfsTracingEnabled() &&
      original_ctx.lr == 0x8238925C &&
      (refii::vfs::RefiiStartupVideosReadyForMenuHandoff() ||
       refii::vfs::RefiiMainMenuTraceNeedsFrontendHandoff()) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoFrame_RenderPrep_821E2C08", 12);
  const auto started_at = std::chrono::steady_clock::now();
  if (trace) {
    REXLOG_INFO("VFS trace post-video frame render-prep enter sub_821E2C08 "
                "lr=0x{:08X} args=[{}] chain=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
                refii::vfs::FormatCurrentPostVideoFrameChainState(base),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  // IDA: this callsite only emits the legacy D3D command-buffer preamble for
  // sub_82389210.  Plume owns the startup Bink draw and must not advance that
  // legacy ring; doing so once per movie frame eventually faults the native
  // owner asynchronously.  Decode, BinkWait, frame advance, and owner
  // lifecycle remain native.
  const bool plume_owns_startup_bink_draw =
      original_ctx.lr == 0x8238925Cu &&
      refii::renderer::PlumeRendererBackendEnabled() &&
      refii::renderer::FableStartupVideoPumpActive();
  if (plume_owns_startup_bink_draw) {
    ctx.r3 = original_ctx.r3;
    return;
  }

  __imp__sub_821E2C08(ctx, base);

  if (trace) {
    const auto elapsed_ms =
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - started_at)
            .count();
    REXLOG_INFO("VFS trace post-video frame render-prep exit sub_821E2C08 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} args_after=[{}] "
                "chain_after=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatPostVideoBoundaryArgs(base, ctx),
                refii::vfs::FormatCurrentPostVideoFrameChainState(base),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

REX_EXTERN(sub_82377DC8) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82377DC8);

  const PPCContext original_ctx = ctx;
  const uint32_t callback_guest = original_ctx.r4.u32;
  const uint32_t callback_kind =
      callback_guest != 0
          ? refii::vfs::LoadGuestU32(base, callback_guest + 44)
          : 0;
  const bool trace =
      refii::vfs::kTraceNonVfsBootFrameProbes &&
      refii::vfs::VfsTracingEnabled() &&
      (callback_kind == 13 ||
       refii::vfs::RefiiStartupVideosReadyForMenuHandoff() ||
       refii::vfs::RefiiMainMenuTraceIsHashOnly() ||
       refii::vfs::RefiiMainMenuTraceNeedsFrontendHandoff()) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "StartupVideoCompletionGate_82377DC8", 24);
  if (trace) {
    REXLOG_INFO("VFS trace startup-video completion gate enter sub_82377DC8 "
                "lr=0x{:08X} args=[{}] gate=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
                refii::vfs::FormatPostVideoCompletionGateState(
                    base, callback_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82377DC8(ctx, base);
  refii::vfs::RecordRefiiStartupCompletionGateSnapshot(base,
                                                           callback_guest,
                                                           original_ctx.lr,
                                                           ctx.r3.u32);

  if (trace) {
    refii::vfs::RecordRefiiPostVideoUiEvent(
        "StartupVideoCompletionGate_82377DC8",
        original_ctx.lr,
        ctx.r3.u32,
        original_ctx.r3.u32,
        original_ctx.r4.u32,
        original_ctx.r5.u32);
    REXLOG_INFO("VFS trace startup-video completion gate exit sub_82377DC8 "
                "lr=0x{:08X} result=0x{:08X} gate_after=[{}] handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::FormatPostVideoCompletionGateState(
                    base, callback_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
      original_ctx.lr, "StartupVideoCompletionGate_82377DC8", base);
  refii::vfs::MaybeLogRefiiStartupReadyGateTextureOpenVerdict(
      original_ctx.lr, "StartupVideoCompletionGate_82377DC8", base);
}

REX_EXTERN(sub_82B49028) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B49028);

  const PPCContext original_ctx = ctx;
  const uint32_t queue_guest = original_ctx.r3.u32;
  const uint32_t request_guest = original_ctx.r4.u32;
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      (refii::vfs::RefiiPostVideoVfsHandoffTraceActive() ||
       refii::vfs::RefiiStartupVideosReadyForMenuHandoff()) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "NativeVfsAsyncQueuePush", 64);
  std::string before_state;
  if (trace) {
    before_state =
        refii::vfs::FormatNativeVfsAsyncQueueState(base, queue_guest);
    REXLOG_INFO("VFS trace native async queue push enter sub_82B49028 "
                "lr=0x{:08X} queue=[{}] request=[{}] handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                before_state,
                refii::vfs::FormatNativeVfsAsyncRequestState(base,
                                                                  request_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82B49028(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace native async queue push exit sub_82B49028 "
                "lr=0x{:08X} result=0x{:08X} queue_before=[{}] "
                "queue_after=[{}] request=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                before_state,
                refii::vfs::FormatNativeVfsAsyncQueueState(base,
                                                                queue_guest),
                refii::vfs::FormatNativeVfsAsyncRequestState(base,
                                                                  request_guest));
  }
}

REX_EXTERN(sub_82B49220) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B49220);

  const PPCContext original_ctx = ctx;
  const uint32_t queue_guest = original_ctx.r3.u32;
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      (refii::vfs::RefiiPostVideoVfsHandoffTraceActive() ||
       refii::vfs::RefiiStartupVideosReadyForMenuHandoff()) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "NativeVfsAsyncQueuePop", 96);

  __imp__sub_82B49220(ctx, base);

  const bool trace_nonempty_request =
      ctx.r3.u32 != 0 && refii::vfs::VfsTracingEnabled() &&
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          fmt::format("NativeVfsAsyncQueuePopRequest:{:08X}:{:08X}",
                      queue_guest,
                      ctx.r3.u32),
          16);
  if (trace || trace_nonempty_request) {
    REXLOG_INFO("VFS trace native async queue pop exit sub_82B49220 "
                "lr=0x{:08X} queue=[{}] request=[{}] handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                refii::vfs::FormatNativeVfsAsyncQueueState(base,
                                                                queue_guest),
                refii::vfs::FormatNativeVfsAsyncRequestState(base,
                                                                  ctx.r3.u32),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

REX_EXTERN(sub_8244B978) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_8244B978);

  const PPCContext original_ctx = ctx;
  const uint32_t scheduler_guest = original_ctx.r3.u32;
  const uint32_t request_guest = original_ctx.r4.u32;
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      (refii::vfs::RefiiPostVideoVfsHandoffTraceActive() ||
       refii::vfs::RefiiPostFrontendConsumerHandoffSignalEmitted()) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "NativeVfsSchedulerInsertPending", 48);
  std::string before_state;
  if (trace) {
    before_state =
        refii::vfs::FormatNativeVfsSchedulerState(base, scheduler_guest);
    REXLOG_INFO("VFS trace native scheduler pending insert enter sub_8244B978 "
                "lr=0x{:08X} scheduler=[{}] request=[{}] handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                before_state,
                refii::vfs::FormatNativeVfsAsyncRequestState(base,
                                                                  request_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  if (refii::vfs::RefiiFrontendRenderRequestInlineAlreadyCompleted(
          request_guest)) {
    ctx.r3.u32 = refii::vfs::LooksLikeGuestPointer(scheduler_guest)
                     ? scheduler_guest + 12u
                     : scheduler_guest;
    if (trace || refii::vfs::ShouldLogRepeatedVfsTraceOperation(
                     "NativeVfsSchedulerInsertCompletedFrontendCollapsed", 8)) {
      REXLOG_INFO("VFS trace native scheduler pending insert collapsed "
                  "completed frontend request sub_8244B978 lr=0x{:08X} "
                  "scheduler=0x{:08X} request=0x{:08X} result=0x{:08X} "
                  "before=[{}]",
                  original_ctx.lr,
                  scheduler_guest,
                  request_guest,
                  ctx.r3.u32,
                  before_state);
    }
    return;
  }

  __imp__sub_8244B978(ctx, base);

  PPCContext inline_ctx = original_ctx;
  const bool inline_frontend_request =
      refii::vfs::TryRunRefiiFrontendRenderRequestInline(
          inline_ctx,
          base,
          request_guest,
          "NativeVfsSchedulerInsertPending",
          true,
          true);

  if (trace) {
    REXLOG_INFO("VFS trace native scheduler pending insert exit sub_8244B978 "
                "lr=0x{:08X} result=0x{:08X} inline_frontend_request={} "
                "before=[{}] after=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                inline_frontend_request ? "true" : "false",
                before_state,
                refii::vfs::FormatNativeVfsSchedulerState(base,
                                                               scheduler_guest));
  }
}

REX_EXTERN(sub_8244BA28) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_8244BA28);

  const PPCContext original_ctx = ctx;
  const uint32_t scheduler_guest = original_ctx.r3.u32;
  const uint32_t request_guest = original_ctx.r4.u32;
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      (refii::vfs::RefiiPostVideoVfsHandoffTraceActive() ||
       refii::vfs::RefiiPostFrontendConsumerHandoffSignalEmitted()) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "NativeVfsSchedulerInsertActive", 48);
  std::string before_state;
  if (trace) {
    before_state =
        refii::vfs::FormatNativeVfsSchedulerState(base, scheduler_guest);
    REXLOG_INFO("VFS trace native scheduler active insert enter sub_8244BA28 "
                "lr=0x{:08X} scheduler=[{}] request=[{}] handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                before_state,
                refii::vfs::FormatNativeVfsAsyncRequestState(base,
                                                                  request_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_8244BA28(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace native scheduler active insert exit sub_8244BA28 "
                "lr=0x{:08X} result=0x{:08X} before=[{}] after=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                before_state,
                refii::vfs::FormatNativeVfsSchedulerState(base,
                                                               scheduler_guest));
  }
}

REX_EXTERN(sub_8244BAD8) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_8244BAD8);

  const PPCContext original_ctx = ctx;
  const uint32_t scheduler_guest = original_ctx.r3.u32;
  const uint32_t request_kind = original_ctx.r4.u32;
  const uint32_t vector_guest = original_ctx.r5.u32;
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      (refii::vfs::RefiiPostVideoVfsHandoffTraceActive() ||
       refii::vfs::RefiiPostFrontendConsumerHandoffSignalEmitted()) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "NativeVfsSchedulerPump", 32);
  std::string before_state;
  if (trace) {
    before_state =
        refii::vfs::FormatNativeVfsSchedulerState(base, scheduler_guest);
    REXLOG_INFO("VFS trace native scheduler pump enter sub_8244BAD8 "
                "lr=0x{:08X} scheduler=[{}] kind=0x{:08X} vector=[{}] "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                before_state,
                request_kind,
                refii::vfs::FormatNativeVfsSchedulerVectorState(base,
                                                                     vector_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_8244BAD8(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace native scheduler pump exit sub_8244BAD8 "
                "lr=0x{:08X} result=0x{:08X} before=[{}] after=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                before_state,
                refii::vfs::FormatNativeVfsSchedulerState(base,
                                                               scheduler_guest));
  }
}

REX_EXTERN(sub_8244BED0) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_8244BED0);

  const PPCContext original_ctx = ctx;
  const uint32_t scheduler_guest = original_ctx.r3.u32;
  const uint32_t request_kind = original_ctx.r4.u32;
  const uint32_t request_group = original_ctx.r5.u32;
  const uint32_t output_list_guest = original_ctx.r6.u32;
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      (refii::vfs::RefiiPostVideoVfsHandoffTraceActive() ||
       refii::vfs::RefiiPostFrontendConsumerHandoffSignalEmitted()) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "NativeVfsSchedulerMoveMatching", 64);
  std::string before_state;
  std::string before_output;
  if (trace) {
    before_state =
        refii::vfs::FormatNativeVfsSchedulerState(base, scheduler_guest);
    before_output =
        refii::vfs::FormatNativeVfsSchedulerVectorState(base,
                                                            output_list_guest);
    REXLOG_INFO("VFS trace native scheduler move matching enter sub_8244BED0 "
                "lr=0x{:08X} scheduler=[{}] kind=0x{:08X} group=0x{:08X} "
                "out=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                before_state,
                request_kind,
                request_group,
                before_output,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_8244BED0(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace native scheduler move matching exit sub_8244BED0 "
                "lr=0x{:08X} result=0x{:08X} before=[{}] after=[{}] "
                "out_before=[{}] out_after=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                before_state,
                refii::vfs::FormatNativeVfsSchedulerState(base,
                                                               scheduler_guest),
                before_output,
                refii::vfs::FormatNativeVfsSchedulerVectorState(
                    base, output_list_guest));
  }
}

REX_EXTERN(sub_82B49D80) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B49D80);

  const PPCContext original_ctx = ctx;
  const uint32_t list_guest = original_ctx.r3.u32;
  const uint32_t pair_high_guest =
      static_cast<uint32_t>(original_ctx.r4.u64 >> 32);
  const uint32_t pair_low_guest = original_ctx.r4.u32;
  const uint32_t value_holder_guest = original_ctx.r5.u32;
  const uint32_t request_guest =
      refii::vfs::LooksLikeGuestPointer(value_holder_guest)
          ? refii::vfs::LoadGuestU32(base, value_holder_guest)
          : 0;
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      (original_ctx.lr == 0x82B47FE4 ||
       refii::vfs::RefiiPostVideoVfsHandoffTraceActive() ||
       refii::vfs::RefiiStartupVideosReadyForMenuHandoff()) &&
      (request_guest != 0 ||
       refii::vfs::ShouldLogRepeatedVfsTraceOperation(
           "NativeVfsRetryInsert", 64)) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          fmt::format("NativeVfsRetryInsert:{:08X}:{:08X}",
                      original_ctx.lr,
                      request_guest),
          16);

  std::string before_state;
  if (trace) {
    before_state = refii::vfs::FormatNativeVfsRetryInsertState(
        base, list_guest, pair_high_guest, pair_low_guest, value_holder_guest);
    REXLOG_INFO("VFS trace native async retry insert enter sub_82B49D80 "
                "lr=0x{:08X} state=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                before_state,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  if (refii::vfs::RefiiFrontendRenderRequestInlineAlreadyCompleted(
          request_guest)) {
    ctx.r3.u32 = list_guest;
    if (trace || refii::vfs::ShouldLogRepeatedVfsTraceOperation(
                     "NativeVfsRetryInsertCompletedFrontendCollapsed", 8)) {
      REXLOG_INFO("VFS trace native async retry insert collapsed completed "
                  "frontend request sub_82B49D80 lr=0x{:08X} list=0x{:08X} "
                  "request=0x{:08X} result=0x{:08X} before=[{}]",
                  original_ctx.lr,
                  list_guest,
                  request_guest,
                  ctx.r3.u32,
                  before_state);
    }
    return;
  }

  __imp__sub_82B49D80(ctx, base);

  PPCContext inline_ctx = original_ctx;
  const bool inline_frontend_request =
      refii::vfs::TryRunRefiiFrontendRenderRequestInline(
          inline_ctx,
          base,
          request_guest,
          "NativeVfsRetryInsert",
          true,
          true);

  if (trace) {
    REXLOG_INFO("VFS trace native async retry insert exit sub_82B49D80 "
                "lr=0x{:08X} result=0x{:08X} inline_frontend_request={} "
                "before=[{}] after=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                inline_frontend_request ? "true" : "false",
                before_state,
                refii::vfs::FormatNativeVfsRetryInsertState(
                    base,
                    list_guest,
                    pair_high_guest,
                    pair_low_guest,
                    value_holder_guest));
  }
}

namespace {

// ---- lhRenderer suspend/resume handshake emulation (2026-08-02) ----------
// Save-load black screen root cause (runs 516/517/518). Ground truth from
// generated/refii_recomp.19.cpp: the "3D Engine" render-thread controller
// object obj carries flags +140 running / +148 stop / +192 suspend-request /
// +193 suspend-ack, and THREE auto-reset events signalled with PulseEvent
// (sub_822C9388), each waited INFINITE via sub_82B4F400(event_obj = obj +
// embed_offset, -1, 0) where the OS handle lives at event_obj+128:
//   A embed +196 (handle +324) suspend ack — waited at lr 0x823889FC
//     (sub_823889A8), pulsed at lr 0x823882B8 AFTER the render thread stores
//     +192=0, +193=1;
//   B embed +328 (handle +456) resume kick — waited at lr 0x823882E0
//     (rex_lhRenderer_ThreadFunc), pulsed at lr 0x82388A40 (sub_82388A18);
//     the only other +456 xref is CloseHandle in the dtor sub_82388720;
//   C embed +460 (handle +588) resume ack — waited at lr 0x82388A6C
//     (sub_82388A18), pulsed at lr 0x82388308 AFTER the render thread clears
//     +193.
// Every waiter releases the controller CS BEFORE entering its wait, and
// PulseEvent releases only threads ALREADY waiting — a pulse landing in that
// gap is silently discarded and, the waits being INFINITE, both the loading
// thread and the render thread park forever (run-518 watchdog autopsy: the
// pair parked together at stall onset). The loading screen is drawn by the
// LOADING thread while the render thread is suspended, so the result is a
// permanent black screen.
//
// Fix shape (waiter-side, guest state untouched): the FLAGS are the
// authoritative handshake state; the pulses are only wakeups. Each of the
// three waits runs in bounded slices through __imp__sub_82B4F400 (so a
// DELIVERED pulse still wakes it immediately) and additionally completes on
// its condition:
//   A completes when +193 == 1 (stored before the pulse, same CS),
//   C completes when +193 == 0 (cleared before the pulse, same CS),
//   B has no guest flag — the pulse IS the whole message — so the single
//     pulse site bumps a host-side epoch BEFORE the guest pulse and the wait
//     completes when epoch > consumed.
// A lost pulse therefore costs at most one slice instead of a permanent
// park. Behaviour is unchanged whenever the pulse is delivered. Kill switch:
// REX_VFS_LH_HANDSHAKE_EMULATION=0 restores the original INFINITE waits.
constexpr uint32_t kLhSuspendAckWaitLr = 0x823889FCu;
constexpr uint32_t kLhResumeKickWaitLr = 0x823882E0u;
constexpr uint32_t kLhResumeAckWaitLr = 0x82388A6Cu;
// THE WORLD UPDATE'S PRESENT GATE. rex_lhRenderer_SyncThread (sub_82A3EC50)
// polls a present-completion event with a bounded 1000ms wait (lr 0x82A3ECB0,
// loop loc_82A3ECA0) and re-loops while the result is timeout. The render side
// that signals it is off in this headless build, so the game thread parks here
// forever after New Game — the camera frozen in the void (the world loads, the
// guest is released, but the frame/camera pass never advances). Release it at
// the call site: signal the event and return signalled so the loop exits.
constexpr uint32_t kPresentGateWaitLr = 0x82A3ECB0u;
constexpr uint32_t kLhResumeKickPulseLr = 0x82388A40u;
constexpr uint32_t kLhEventSuspendAckEmbedOffset = 196u;
constexpr uint32_t kLhEventResumeKickEmbedOffset = 328u;
constexpr uint32_t kLhEventResumeAckEmbedOffset = 460u;
constexpr uint32_t kLhSuspendAckFlagOffset = 193u;
// sub_82B4F400 result mapping (generated/refii_recomp.83.cpp): 2 = signalled
// (WAIT_OBJECT_0/ABANDONED), 1 = APC (WAIT_IO_COMPLETION), 0 = WAIT_TIMEOUT.
constexpr uint32_t kLhWaitResultSignalled = 2u;
constexpr uint32_t kLhWaitResultTimeout = 0u;
constexpr uint32_t kLhWaitSliceMs = 4u;

std::atomic<uint64_t> g_lh_resume_kick_epoch{0};
std::atomic<uint64_t> g_lh_resume_kick_consumed{0};

bool LhHandshakeEmulationEnabled() {
  static const bool enabled = [] {
    const char* value = std::getenv("REX_VFS_LH_HANDSHAKE_EMULATION");
    return value == nullptr || value[0] != '0';
  }();
  return enabled;
}

// Returns true when it handled the wait (ctx.r3 holds the mapped result);
// false hands the call to the original INFINITE wait.
bool TryEmulateLhRendererHandshakeWait(PPCContext& ctx, uint8_t* base,
                                       const PPCContext& original_ctx) {
  if (!LhHandshakeEmulationEnabled()) {
    return false;
  }
  // Only the three known INFINITE handshake waits; anything else (including
  // these sites with a finite timeout, which never ships) is untouched.
  if (original_ctx.r4.u32 != 0xFFFFFFFFu) {
    return false;
  }
  const uint32_t lr = static_cast<uint32_t>(original_ctx.lr);
  enum class LhWaitKind { kSuspendAck, kResumeKick, kResumeAck };
  LhWaitKind kind;
  uint32_t controller_guest = 0;
  switch (lr) {
    case kLhSuspendAckWaitLr:
      kind = LhWaitKind::kSuspendAck;
      controller_guest = original_ctx.r3.u32 - kLhEventSuspendAckEmbedOffset;
      break;
    case kLhResumeKickWaitLr:
      kind = LhWaitKind::kResumeKick;
      controller_guest = original_ctx.r3.u32 - kLhEventResumeKickEmbedOffset;
      break;
    case kLhResumeAckWaitLr:
      kind = LhWaitKind::kResumeAck;
      controller_guest = original_ctx.r3.u32 - kLhEventResumeAckEmbedOffset;
      break;
    default:
      return false;
  }
  const auto condition_satisfied = [&]() -> bool {
    switch (kind) {
      case LhWaitKind::kSuspendAck:
        return refii::vfs::LoadGuestU8(
                   base, controller_guest + kLhSuspendAckFlagOffset) != 0;
      case LhWaitKind::kResumeAck:
        return refii::vfs::LoadGuestU8(
                   base, controller_guest + kLhSuspendAckFlagOffset) == 0;
      case LhWaitKind::kResumeKick: {
        const uint64_t epoch =
            g_lh_resume_kick_epoch.load(std::memory_order_acquire);
        if (epoch >
            g_lh_resume_kick_consumed.load(std::memory_order_relaxed)) {
          g_lh_resume_kick_consumed.store(epoch, std::memory_order_relaxed);
          return true;
        }
        return false;
      }
    }
    return false;
  };
  const auto t0 = std::chrono::steady_clock::now();
  uint32_t slices = 0;
  bool recovered = false;
  for (;;) {
    // Condition FIRST: a pulse lost BEFORE this wait was entered (the exact
    // run-516/517/518 race window) is recovered without blocking at all.
    if (condition_satisfied()) {
      ctx.r3.u64 = kLhWaitResultSignalled;
      recovered = true;
      break;
    }
    ctx.r3.u32 = original_ctx.r3.u32;
    ctx.r4.s64 = static_cast<int32_t>(kLhWaitSliceMs);
    ctx.r5.u32 = original_ctx.r5.u32;
    __imp__sub_82B4F400(ctx, base);
    ++slices;
    if (ctx.r3.u32 != kLhWaitResultTimeout) {
      // Real wake (or APC): original semantics, pass the result through. Keep
      // the resume-kick ledger aligned — its pulse pre-incremented the epoch.
      if (kind == LhWaitKind::kResumeKick &&
          ctx.r3.u32 == kLhWaitResultSignalled) {
        g_lh_resume_kick_consumed.store(
            g_lh_resume_kick_epoch.load(std::memory_order_acquire),
            std::memory_order_relaxed);
      }
      break;
    }
  }
  if (recovered) {
    // Only a recovery is interesting: the original code would have parked
    // forever here. Expected once per lost pulse, i.e. rarely.
    static std::atomic<uint32_t> recovery_logs{0};
    const uint32_t count =
        recovery_logs.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (count <= 16u || (count % 64u) == 0u) {
      const int64_t waited_ms =
          std::chrono::duration_cast<std::chrono::milliseconds>(
              std::chrono::steady_clock::now() - t0)
              .count();
      REXLOG_WARN(
          "Refii loading-screen handshake emulated #{}: {} satisfied by "
          "condition after a lost pulse — waited {}ms (slices={}) "
          "controller=0x{:08X} lr=0x{:08X}",
          count,
          kind == LhWaitKind::kSuspendAck    ? "suspend ack"
          : kind == LhWaitKind::kResumeKick  ? "resume kick"
                                             : "resume ack",
          waited_ms, slices, controller_guest, lr);
    }
  }
  return true;
}

}  // namespace

REX_EXTERN(sub_822C9388) { // Lumen: PulseEvent
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_822C9388);

  const PPCContext original_ctx = ctx;
  const uint32_t event_handle_guest = original_ctx.r3.u32;
  // Resume-kick ledger (see TryEmulateLhRendererHandshakeWait): the resume
  // request's only message is this pulse, so publish it host-side BEFORE the
  // guest pulse — a waiter woken by the real pulse then always observes
  // epoch > consumed.
  if (original_ctx.lr == kLhResumeKickPulseLr) {
    g_lh_resume_kick_epoch.fetch_add(1, std::memory_order_release);
  }
  const bool trace =
      refii::vfs::kTraceNonVfsBootFrameProbes &&
      refii::vfs::VfsTracingEnabled() &&
      (original_ctx.lr == 0x82388A40 || original_ctx.lr == 0x823882B8 ||
       original_ctx.lr == 0x82388308 ||
       refii::vfs::RefiiPostVideoEventMatchesHandoffWait(
           event_handle_guest)) &&
      refii::vfs::ShouldLogLooseStreamOperation(
          "GeneratedEvent_Set", event_handle_guest, 16);
  if (trace) {
    REXLOG_INFO("VFS trace generated event set enter sub_822C9388 "
                "lr=0x{:08X} event=0x{:08X} state=[{}]",
                original_ctx.lr,
                event_handle_guest,
                refii::vfs::FormatGeneratedEventSetState(
                    base, event_handle_guest));
  }

  __imp__sub_822C9388(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace generated event set exit sub_822C9388 "
                "lr=0x{:08X} event=0x{:08X} result=0x{:08X} state=[{}]",
                original_ctx.lr,
                event_handle_guest,
                ctx.r3.u32,
                refii::vfs::FormatGeneratedEventSetState(
                    base, event_handle_guest));
  }
}

REX_EXTERN(sub_82B4F400) {
  REX_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;

  // A COMMAND-STREAM FENCE WAIT, not a present-completion event.
  //
  // sub_82A4EB08 builds the object this waits on: CreateEventA(0, 1, 0, 0) at +128, then
  // writes a 0xF0F0F0F0-tagged packet carrying it into the ring at dword_8349F9B0. The
  // consumer is sub_822DFE08, and Render_GpuAccessLock_Enter (0x82A4F0E0) picks between
  // them — 0x82A4F19C drains inline when this thread owns the lock, 0x82A4F1AC posts the
  // fence and waits (sub_82A3EC20) when it does not. Headless nothing drains the ring, so
  // the wait at lr 0x82A3ECB0 never completes and the game thread parks after New Game.
  //
  // Returning kLhWaitResultSignalled IS the release. Signalling the event first would be a
  // no-op: sub_82A3EC20 CloseHandle()s it at 0x82A3ECD4, two instructions after the wait
  // returns. (It also does not build — the payload reaches rex:: only through the C bridge.)
  //
  // The gate is NOT "past boot". byte_83496AC4 is set by the front-end setup (sub_82319340)
  // and cleared by its teardown (sub_82319A40 @0x82319B24), so it is 0 again in game;
  // byte_83496A5E is set by Game_InitDatabases (@0x82311648) and cleared by sub_823117A8.
  // The OR reads "front end up OR databases up", which is what holds it clear through the
  // startup movies — the property this relies on. The fence itself means the same thing in
  // both phases, so this is a phase heuristic, not a contract.
  const bool frontend_or_databases_up =
      refii::vfs::LoadGuestU8(base, 0x83496AC4u) != 0u ||
      refii::vfs::LoadGuestU8(base, 0x83496A5Eu) != 0u;
  if (original_ctx.lr == kPresentGateWaitLr && frontend_or_databases_up) {
    ctx.r3.u32 = kLhWaitResultSignalled;
    return;
  }

  // lhRenderer suspend/resume handshake waits are emulated (bounded slices +
  // authoritative-flag completion) so a lost PulseEvent cannot park the
  // loading/render thread pair forever. This MUST run before the native
  // passthrough: that macro returns early in the native backend, which would
  // otherwise bypass the emulation and let the in-game suspend-ack wait
  // (sub_823889A8) park the game thread forever — the "camera in the void"
  // stall after New Game. Emulation is backend-independent; a lost pulse is a
  // no-present hazard, not a loose-asset one.
  if (TryEmulateLhRendererHandshakeWait(ctx, base, original_ctx)) {
    return;
  }

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B4F400);

  const bool native_startup_frame_wait = original_ctx.lr == 0x823889FCu;
  const uint32_t wait_object_guest = original_ctx.r3.u32;
  const uint32_t wait_handle_guest =
      wait_object_guest != 0
          ? refii::vfs::LoadGuestU32(base, wait_object_guest + 128)
          : 0;
  refii::vfs::RememberRefiiNativeVfsWorkerWait(
      static_cast<uint32_t>(original_ctx.lr),
      wait_object_guest,
      wait_handle_guest);
  const bool trace =
      refii::vfs::ShouldTracePostVideoGeneratedEventWait(
          static_cast<uint32_t>(original_ctx.lr),
          wait_object_guest,
          wait_handle_guest) ||
      (refii::vfs::kTraceNonVfsBootFrameProbes &&
       refii::vfs::VfsTracingEnabled() &&
       (original_ctx.lr == 0x82388A6C || original_ctx.lr == 0x823889FC ||
        original_ctx.lr == 0x823882E0 ||
        refii::vfs::RefiiPostVideoEventMatchesHandoffWait(
            wait_object_guest) ||
        refii::vfs::RefiiPostVideoEventMatchesHandoffWait(
            wait_handle_guest)) &&
       refii::vfs::ShouldLogLooseStreamOperation(
           "GeneratedEvent_Wait", wait_object_guest, 16));
  const auto begin = std::chrono::steady_clock::now();
  if (trace) {
    REXLOG_INFO("VFS trace generated event wait enter sub_82B4F400 "
                "lr=0x{:08X} wait_object=0x{:08X} timeout=0x{:08X} "
                "alertable=0x{:08X} state=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                wait_object_guest,
                original_ctx.r4.u32,
                original_ctx.r5.u32,
                refii::vfs::FormatGeneratedEventWaitState(
                    base, wait_object_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  // Handshake waits were already handled (and returned) above; everything
  // that reaches here is a plain wait, so hand it to the original.
  __imp__sub_82B4F400(ctx, base);
  const int64_t wait_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                              std::chrono::steady_clock::now() - begin)
                              .count();
  if (native_startup_frame_wait && wait_ms >= 50) {
    static std::atomic<uint32_t> startup_frame_wait_logs{0};
    const uint32_t count =
        startup_frame_wait_logs.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (count <= 16u || (count % 120u) == 0u) {
      REXLOG_INFO(
          "Refii VFS startup-video native event wait handoff #{} "
          "lr=0x{:08X} wait_object=0x{:08X} wait_handle=0x{:08X} "
          "timeout=0x{:08X} result=0x{:08X} wait_ms={} state=[{}]",
          count,
          original_ctx.lr,
          wait_object_guest,
          wait_handle_guest,
          original_ctx.r4.u32,
          ctx.r3.u32,
          wait_ms,
          refii::vfs::FormatGeneratedEventWaitState(base, wait_object_guest));
    }
  }
  if (trace) {
    REXLOG_INFO("VFS trace generated event wait exit sub_82B4F400 "
                "lr=0x{:08X} wait_object=0x{:08X} result=0x{:08X} "
                "wait_ms={} state_after=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                wait_object_guest,
                ctx.r3.u32,
                wait_ms,
                refii::vfs::FormatGeneratedEventWaitState(
                    base, wait_object_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (trace || refii::vfs::IsPostVideoGeneratedWaitLr(
                   static_cast<uint32_t>(original_ctx.lr))) {
    refii::vfs::RecordRefiiPostVideoWaitResult(
        original_ctx.lr,
        ctx.r3.u32,
        wait_ms,
        wait_object_guest,
        wait_handle_guest);
  }
  if (wait_ms >= 250 &&
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      (trace || refii::vfs::IsPostVideoGeneratedWaitLr(
                    static_cast<uint32_t>(original_ctx.lr))) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "GeneratedEventWaitSlowBoundary", 8)) {
    REXLOG_INFO("Refii VFS generated event wait slow boundary "
                "lr=0x{:08X} wait_object=0x{:08X} wait_handle=0x{:08X} "
                "timeout=0x{:08X} result=0x{:08X} wait_ms={} state=[{}] "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                wait_object_guest,
                wait_handle_guest,
                original_ctx.r4.u32,
                ctx.r3.u32,
                wait_ms,
                refii::vfs::FormatGeneratedEventWaitState(
                    base, wait_object_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    refii::vfs::LogRefiiVfsChainVerdict(
        "frontend_post_video",
        static_cast<uint32_t>(original_ctx.lr),
        "startup_bik_pair",
        "art/videos/microsoft_logo.bik|art/videos/lionhead_logo.bik",
        "art/videos/microsoft_logo.bik|art/videos/lionhead_logo.bik",
        "native_buffered_stream",
        "GeneratedEvent_Wait_slow",
        "TraceOnce",
        "event_signal_or_startup_video_finalizer");
  }
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
      original_ctx.lr, "GeneratedEvent_Wait", base);
}

REX_EXTERN(sub_82388A18) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82388A18);

  const PPCContext original_ctx = ctx;
  const auto begin = std::chrono::steady_clock::now();
  if (refii::vfs::ShouldRecordPostVideoWaitSnapshot(
          static_cast<uint32_t>(original_ctx.lr)) ||
      (refii::vfs::kTraceNonVfsBootFrameProbes &&
       refii::vfs::VfsTracingEnabled())) {
    refii::vfs::RecordRefiiPostVideoWaitSnapshot(
        base, original_ctx.r3.u32, original_ctx.lr);
  }
  const bool trace =
      refii::vfs::ShouldTracePostVideoBoundaryProbe("PostVideoFrame_82388A18") ||
      (refii::vfs::kTraceNonVfsBootFrameProbes &&
       refii::vfs::VfsTracingEnabled() &&
       (original_ctx.lr == 0x82377D1C ||
        original_ctx.lr == 0x823891C8 ||
       original_ctx.lr == 0x82389308 ||
       original_ctx.lr == 0x82389390) &&
       (refii::vfs::RefiiMainMenuTraceIsHashOnly() ||
        refii::vfs::RefiiMainMenuTraceNeedsFrontendHandoff()));
  if (trace) {
    REXLOG_INFO("VFS trace post-video frame wait enter sub_82388A18 "
                "lr=0x{:08X} ctr=0x{:08X} dynamic_slot_returned=1 "
                "state=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                original_ctx.ctr.u32,
                refii::vfs::FormatPostVideoFrameWaitState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82388A18(ctx, base);

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_82388A18",
                                                    original_ctx.lr,
                                                    ctx.r3.u32,
                                                    elapsed_ms);
  }

  if (trace || (refii::vfs::kTraceNonVfsBootFrameProbes &&
                refii::vfs::VfsTracingEnabled() &&
                (original_ctx.lr == 0x82377D1C ||
                 original_ctx.lr == 0x823891C8 ||
                 original_ctx.lr == 0x82389308 ||
                 original_ctx.lr == 0x82389390) &&
                refii::vfs::RefiiStartupVideosReadyForMenuHandoff())) {
    refii::vfs::RecordRefiiPostVideoUiEvent("PostVideoFrame_82388A18",
                                                original_ctx.lr,
                                                ctx.r3.u32,
                                                original_ctx.r3.u32,
                                                original_ctx.r4.u32,
                                                original_ctx.r5.u32);
  }
  if (trace) {
    REXLOG_INFO("VFS trace post-video frame wait exit sub_82388A18 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
                "state_after=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatPostVideoFrameWaitState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (elapsed_ms >= 250 &&
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoStageSlow_sub_82388A18", 8)) {
    REXLOG_INFO("Refii VFS post-video stage slow "
                "stage=sub_82388A18 lr=0x{:08X} result=0x{:08X} "
                "elapsed_ms={} stage_timing=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatRefiiPostVideoStageTimingForLog(),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
      original_ctx.lr, "PostVideoFrame_82388A18", base);
}

REX_EXTERN(sub_82A41B00) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82A41B00);

  const PPCContext original_ctx = ctx;
  const auto begin = std::chrono::steady_clock::now();
  const bool trace =
      refii::vfs::ShouldTracePostVideoBoundaryProbe("PostVideoFrame_82A41B00") ||
      (refii::vfs::kTraceNonVfsBootFrameProbes &&
       refii::vfs::VfsTracingEnabled() &&
       (original_ctx.lr == 0x82377D24 ||
        original_ctx.lr == 0x823891D0 ||
       original_ctx.lr == 0x82389310 ||
       original_ctx.lr == 0x82389398) &&
       (refii::vfs::RefiiMainMenuTraceIsHashOnly() ||
        refii::vfs::RefiiMainMenuTraceNeedsFrontendHandoff()));
  if (trace) {
    REXLOG_INFO("VFS trace post-video frame teardown enter sub_82A41B00 "
                "lr=0x{:08X} ctr=0x{:08X} after_dynamic_slot=1 "
                "state=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                original_ctx.ctr.u32,
                refii::vfs::FormatPostVideoFrameTeardownState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82A41B00(ctx, base);

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_82A41B00",
                                                    original_ctx.lr,
                                                    ctx.r3.u32,
                                                    elapsed_ms);
  }

  if (trace || (refii::vfs::kTraceNonVfsBootFrameProbes &&
                refii::vfs::VfsTracingEnabled() &&
                (original_ctx.lr == 0x82377D24 ||
                 original_ctx.lr == 0x823891D0 ||
                 original_ctx.lr == 0x82389310 ||
                 original_ctx.lr == 0x82389398) &&
                refii::vfs::RefiiStartupVideosReadyForMenuHandoff())) {
    refii::vfs::RecordRefiiPostVideoUiEvent("PostVideoFrame_82A41B00",
                                                original_ctx.lr,
                                                ctx.r3.u32,
                                                original_ctx.r3.u32,
                                                original_ctx.r4.u32,
                                                original_ctx.r5.u32);
  }
  if (trace) {
    REXLOG_INFO("VFS trace post-video frame teardown exit sub_82A41B00 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
                "state_after=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatPostVideoFrameTeardownState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (elapsed_ms >= 250 &&
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoStageSlow_sub_82A41B00", 8)) {
    REXLOG_INFO("Refii VFS post-video stage slow "
                "stage=sub_82A41B00 lr=0x{:08X} result=0x{:08X} "
                "elapsed_ms={} stage_timing=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatRefiiPostVideoStageTimingForLog(),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
      original_ctx.lr, "PostVideoFrame_82A41B00", base);
}
