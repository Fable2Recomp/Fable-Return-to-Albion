#include "vfs_hooks_private.h"

PPC_FUNC_IMPL(sub_82377A00) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const bool trace = refii::vfs::ShouldTraceStartupVideoLoopProbe(
      "StartupVideoUpdate_82377A00", original_ctx.lr, 24);
  if (trace) {
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

  if (refii::vfs::SkipStartupVideosEnabled()) {
    refii::vfs::ApplyRefiiSkipStartupVideoReadyPatch(
        base, original_ctx.r3.u32, "startup_update_exit");
  }
  refii::vfs::RecordRefiiStartupUpdateSnapshot(base,
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
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
      original_ctx.lr, "StartupVideoUpdate_82377A00", base);
  refii::vfs::MaybeLogRefiiStartupReadyGateTextureOpenVerdict(
      original_ctx.lr, "StartupVideoUpdate_82377A00", base);
  refii::vfs::MaybeLogRefiiStartupVideoReadyNoFinalizerBlocker(
      original_ctx.lr, "StartupVideoUpdate_82377A00", base);
}

PPC_FUNC_IMPL(sub_82377980) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const bool trace =
      refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      (refii::vfs::RefiiMainMenuTraceNeedsFrontendHandoff() ||
       refii::vfs::RefiiStartupVideosReadyForMenuHandoff() ||
       refii::vfs::SkipStartupVideosEnabled()) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "StartupVideoReadyCompletion_82377980", 4);
  if (trace) {
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
  const bool collapse_refii_ready_completion = false;
  if (trace || (refii::vfs::VfsTracingEnabled() && update_complete)) {
    REXLOG_INFO("Refii VFS startup-video ready completion collapse gate "
                "sub_82377980 lr=0x{:08X} update=0x{:08X} backend={} "
                "update_valid={} update_complete={} texture_owned={} "
                "hash_only={} menu_texture_refii_owned={} "
                "menu_texture_needs_prime={} collapse={}",
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
                menu_texture_needs_prime ? "true" : "false",
                collapse_refii_ready_completion ? "true" : "false");
  }
  if (refii::vfs::RefiiLooseAssetBackendEnabled() && update_complete &&
      menu_texture_refii_owned && !refii::vfs::SkipStartupVideosEnabled() &&
      !startup_videos_settled) {
    ctx = original_ctx;
    ctx.r3.u32 = 0;
    refii::vfs::RecordRefiiStartupUpdateSnapshot(base,
                                                     original_ctx.r3.u32,
                                                     original_ctx.lr,
                                                     ctx.r3.u32);
    refii::vfs::RecordRefiiPostVideoUiEvent(
        "StartupVideoReadyCompletion_82377980_waiting_for_playback",
        original_ctx.lr,
        ctx.r3.u32,
        original_ctx.r3.u32,
        original_ctx.r4.u32,
        original_ctx.r5.u32);
    if (refii::vfs::VfsTracingEnabled() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "StartupVideoReadyCompletion_waiting_for_playback", 8)) {
      REXLOG_INFO("Refii VFS startup-video ready completion delayed "
                  "sub_82377980 lr=0x{:08X} update=0x{:08X} "
                  "videos_settled=false update_after=[{}] handoff=[{}] "
                  "main_menu=[{}] next=startup_video_loop_until_native_finalizer",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  refii::vfs::FormatStartupVideoUpdateTimingState(
                      base, original_ctx.r3.u32),
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    }
    return;
  }
  if (collapse_refii_ready_completion) {
    const uint32_t ready_object_guest =
        update_guest_valid
            ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 92)
            : 0;
    ctx = original_ctx;
    ctx.r3.u32 = original_ctx.r3.u32;
    refii::vfs::RecordRefiiStartupUpdateSnapshot(base,
                                                     original_ctx.r3.u32,
                                                     original_ctx.lr,
                                                     ctx.r3.u32);
    refii::vfs::RecordRefiiPostVideoUiEvent(
        "StartupVideoReadyCompletion_82377980_collapsed_noalloc",
        original_ctx.lr,
        ctx.r3.u32,
        original_ctx.r3.u32,
        original_ctx.r4.u32,
        original_ctx.r5.u32);
    if (trace || refii::vfs::VfsTracingEnabled()) {
      REXLOG_INFO("Refii VFS startup-video ready completion collapsed "
                  "sub_82377980 lr=0x{:08X} update=0x{:08X} "
                  "ready_object=0x{:08X} update_complete={} "
                  "menu_texture_refii_owned={} allocation=false "
                  "update_after=[{}] handoff=[{}] main_menu=[{}]",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  ready_object_guest,
                  update_complete ? "true" : "false",
                  menu_texture_refii_owned ? "true" : "false",
                  refii::vfs::FormatStartupVideoUpdateTimingState(
                      base, original_ctx.r3.u32),
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
      refii::vfs::LogRefiiVfsChainVerdict(
          "frontend_post_video",
          static_cast<uint32_t>(original_ctx.lr),
          "menu_texture_refii_owned",
          "",
          "",
          "startup_ready_completion",
          "sub_82377980_collapsed_noalloc",
          "TraceOnce",
          "frontend_texture_consumer_or_DB20");
    }
    refii::vfs::MaybeLogRefiiStartupReadyGateTextureOpenVerdict(
        original_ctx.lr, "StartupVideoReadyCompletion_82377980_collapsed", base);
    refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
        original_ctx.lr, "StartupVideoReadyCompletion_82377980_collapsed", base);
    PPCContext post_video_synthetic_ctx = original_ctx;
    refii::vfs::TryRunPostVideoFrontendSyntheticHandoff(
        post_video_synthetic_ctx,
        base,
        original_ctx.r3.u32,
        "StartupVideoReadyCompletion_82377980_collapsed");
    return;
  } else if (refii::vfs::RefiiLooseAssetBackendEnabled() &&
             update_complete && menu_texture_refii_owned &&
             (refii::vfs::SkipStartupVideosEnabled() ||
              startup_videos_settled)) {
    const uint32_t ready_object_guest =
        update_guest_valid
            ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 92)
            : 0;
    uint32_t completion_guest =
        update_guest_valid
            ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 88)
            : 0;
    uint32_t inner_guest = 0;
    bool constructed = false;
    bool linked = false;
    if (update_guest_valid && refii::vfs::LooksLikeGuestPointer(ready_object_guest)) {
      PPCContext alloc_ctx = original_ctx;
      const uint32_t allocated_guest =
          refii::vfs::CallLhMalloc(alloc_ctx, base, 16);
      if (refii::vfs::LooksLikeGuestPointer(allocated_guest)) {
        PPCContext ctor_ctx = original_ctx;
        ctor_ctx.r3.u32 = allocated_guest;
        ctor_ctx.r4.u32 = ready_object_guest;
        ctor_ctx.lr = 0x823779A8u;
        __imp__sub_8231A908(ctor_ctx, base);
        completion_guest = ctor_ctx.r3.u32;
        constructed = refii::vfs::LooksLikeGuestPointer(completion_guest);
        if (constructed) {
          inner_guest = refii::vfs::LoadGuestU32(base, completion_guest + 12);
        }
      }
    }

    if (update_guest_valid &&
        refii::vfs::LooksLikeGuestPointer(completion_guest) &&
        refii::vfs::LooksLikeGuestPointer(inner_guest)) {
      const uint32_t source_begin =
          refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 8);
      const uint32_t source_end =
          refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 12);
      const uint32_t previous_end =
          refii::vfs::LoadGuestU32(base, inner_guest + 20);
      if (previous_end != source_end) {
        refii::vfs::StoreGuestU32(base, inner_guest + 16, source_begin);
        refii::vfs::StoreGuestU32(base, inner_guest + 20, source_end);
        if (refii::vfs::LooksLikeGuestPointer(source_end)) {
          const uint32_t refs = refii::vfs::LoadGuestU32(base, source_end);
          refii::vfs::StoreGuestU32(base, source_end, refs + 1);
        }
      }
      refii::vfs::StoreGuestU32(base, original_ctx.r3.u32 + 88,
                                    completion_guest);
      linked = true;
    }

    ctx = original_ctx;
    ctx.r3.u32 = update_guest_valid
                     ? refii::vfs::LoadGuestU32(base,
                                                    original_ctx.r3.u32 + 88)
                     : completion_guest;
    refii::vfs::RecordRefiiStartupUpdateSnapshot(base,
                                                     original_ctx.r3.u32,
                                                     original_ctx.lr,
                                                     ctx.r3.u32);
    refii::vfs::RecordRefiiPostVideoUiEvent(
        "StartupVideoReadyCompletion_82377980_guarded_generated",
        original_ctx.lr,
        ctx.r3.u32,
        original_ctx.r3.u32,
        original_ctx.r4.u32,
        original_ctx.r5.u32);
    if (trace || refii::vfs::VfsTracingEnabled()) {
      REXLOG_INFO("Refii VFS startup-video ready completion guarded "
                  "generated-equivalent sub_82377980 lr=0x{:08X} "
                  "update=0x{:08X} ready_object=0x{:08X} "
                  "completion=0x{:08X} inner=0x{:08X} constructed={} "
                  "linked={} update_after=[{}] handoff=[{}] main_menu=[{}]",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  ready_object_guest,
                  ctx.r3.u32,
                  inner_guest,
                  constructed ? "true" : "false",
                  linked ? "true" : "false",
                  refii::vfs::FormatStartupVideoUpdateTimingState(
                      base, original_ctx.r3.u32),
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
      refii::vfs::LogRefiiVfsChainVerdict(
          "frontend_post_video",
          static_cast<uint32_t>(original_ctx.lr),
          "menu_texture_refii_owned",
          "",
          "",
          "startup_ready_completion",
          "sub_82377980_guarded_generated",
          linked ? "Opened" : "OpenFailed",
          linked ? "frontend_texture_consumer_or_event_dispatch"
                 : "ready_completion_constructor_rejected");
    }
    if (trace || refii::vfs::VfsTracingEnabled()) {
      REXLOG_INFO("Refii VFS startup-video ready completion guarded "
                  "generated-equivalent returning to native event dispatch "
                  "sub_82377980 lr=0x{:08X} completion=0x{:08X} "
                  "linked={} next=frontend_texture_consumer_or_event_wait",
                  original_ctx.lr,
                  ctx.r3.u32,
                  linked ? "true" : "false");
    }
    refii::vfs::MaybeLogRefiiStartupReadyGateTextureOpenVerdict(
        original_ctx.lr,
        "StartupVideoReadyCompletion_82377980_guarded_generated",
        base);
    refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
        original_ctx.lr,
        "StartupVideoReadyCompletion_82377980_guarded_generated",
        base);
    PPCContext post_video_synthetic_ctx = original_ctx;
    refii::vfs::TryRunPostVideoFrontendSyntheticHandoff(
        post_video_synthetic_ctx,
        base,
        original_ctx.r3.u32,
        "StartupVideoReadyCompletion_82377980_guarded_generated");
    return;
  } else if (trace) {
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
  PPCContext post_video_synthetic_ctx = original_ctx;
  const uint32_t post_video_root_guest =
      refii::vfs::LooksLikeGuestPointer(ctx.r3.u32) ? ctx.r3.u32
                                                        : original_ctx.r3.u32;
  refii::vfs::TryRunPostVideoFrontendSyntheticHandoff(
      post_video_synthetic_ctx,
      base,
      post_video_root_guest,
      "StartupVideoReadyCompletion_82377980");

  if (trace) {
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
  PPC_FUNC_IMPL(function_name) {                                              \
    PPC_FUNC_PROLOGUE();                                                      \
                                                                               \
    const PPCContext original_ctx = ctx;                                       \
    const uint32_t request_guest = original_ctx.r3.u32;                        \
    const bool tracked_request =                                               \
        refii::vfs::IsNativeVfsScheduledRequest(base, request_guest);      \
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
  PPC_FUNC_IMPL(function_name) {                                              \
    PPC_FUNC_PROLOGUE();                                                      \
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
  PPC_FUNC_IMPL(function_name) {                                              \
    PPC_FUNC_PROLOGUE();                                                      \
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
REFII_NATIVE_VFS_SHARED_REQUEST_TRACE(sub_8231A368,
                                      __imp__sub_8231A368,
                                      "FrontendRenderRequest_Slot24_8231A368")

#undef REFII_NATIVE_VFS_SHARED_REQUEST_TRACE

PPC_FUNC_IMPL(sub_82378618) {
  PPC_FUNC_PROLOGUE();

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
          const bool suppress_collapsed_hash_wake =
              mode == "collapsed_descriptor_bundle" &&
              frontend_trace.any_refii_owned &&
              frontend_trace.texture_parser_seen &&
              frontend_trace.followon_frame_list_committed;
          if (suppress_collapsed_hash_wake) {
            refii::vfs::RecordRefiiMainMenuBlockerEvent(
                "deferred_texture_hash_wake_collapsed_augmented",
                fmt::format("mode={} lr=0x{:08X} summary=[{}]",
                            mode,
                            original_ctx.lr,
                            refii::vfs::
                                FormatFrontendTextureConsumerTraceForLog(
                                    frontend_trace)));
            PPCContext augment_ctx = original_ctx;
            refii::vfs::RerunKnownRequestManagerHashAugmentation(
                augment_ctx,
                base,
                "FrontendTextureConsumer_DeferredTextureWakeCollapsedParsed",
                0,
                "frontend_texture_consumer");
            refii::vfs::MaybeSignalRefiiFrontendDeferredHashWakeHandoff(
                original_ctx, base, mode);
            if (refii::vfs::VfsTracingEnabled() &&
                refii::vfs::ShouldLogRepeatedVfsTraceOperation(
                    "FrontendTextureConsumerDeferredTextureWakeAugmented",
                    16)) {
              REXLOG_INFO("Refii VFS frontend texture consumer deferred "
                          "texture hash wake augmented mode={} lr=0x{:08X} "
                          "reason=collapsed_consumer_already_parsed "
                          "summary=[{}]",
                          mode,
                          original_ctx.lr,
                          refii::vfs::
                              FormatFrontendTextureConsumerTraceForLog(
                                  frontend_trace));
            }
            refii::vfs::LogRefiiVfsChainVerdict(
                "request_manager_hash",
                static_cast<uint32_t>(original_ctx.lr),
                "frontend_texture_consumer",
                "",
                "",
                "RefiiRequestManagerHashEntry",
                "FrontendTextureConsumer_DeferredTextureWake",
                "Opened",
                "RequestManager_FindStreamByHashEntry");
          } else {
          PPCContext augment_ctx = original_ctx;
          refii::vfs::RerunKnownRequestManagerHashAugmentation(
              augment_ctx,
              base,
              "FrontendTextureConsumer_DeferredTextureWake",
              0,
              "frontend_texture_consumer");
          }
        }
        refii::vfs::TryPrimeRefiiPostFrontendGameFacePackage(
            original_ctx, base, frontend_trace, mode);
        refii::vfs::MaybeSignalRefiiFrontendDeferredHashWakeHandoff(
            original_ctx, base, mode);
        refii::vfs::MaybeSignalRefiiPostFrontendConsumerHandoff(
            original_ctx, base, frontend_trace, mode);
      };

  const uint32_t collapsible_bundle_guest =
      refii::vfs::TryGetCollapsibleRefiiFrontendTextureBundle(base,
                                                                  original_ctx);
  if (collapsible_bundle_guest != 0) {
    PPCContext bundle_ctx = original_ctx;
    bundle_ctx.r3.u32 = collapsible_bundle_guest;
    bundle_ctx.lr = refii::vfs::kFrontendTextureConsumerBundleCallLr;
    if (trace) {
      REXLOG_INFO("Refii VFS frontend texture consumer collapsed descriptor "
                  "bundle enter lr=0x{:08X} object=0x{:08X} bundle=0x{:08X} "
                  "bundle_words={} main_menu=[{}]",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  collapsible_bundle_guest,
                  refii::vfs::FormatGuestWordsIfPointer(
                      base, collapsible_bundle_guest, 28),
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    }
    bool native_bundle_ran = false;
    PPCContext synthetic_ctx = original_ctx;
    bool synthetic_descriptors =
        refii::vfs::TrySynthesizeFrontendTextureDescriptorPhase(
            synthetic_ctx, base, original_ctx.r3.u32, true, false);
    if (!synthetic_descriptors) {
      __imp__sub_826D5E48(bundle_ctx, base);
      native_bundle_ran = true;
      synthetic_descriptors =
          refii::vfs::TrySynthesizeFrontendTextureDescriptorPhase(
              bundle_ctx, base, original_ctx.r3.u32, true, false);
    }
    refii::vfs::RecordFrontendTextureConsumerResourceBoundary(
        native_bundle_ran ? "sub_82378618_collapsed_descriptor_bundle_native"
                          : "sub_82378618_collapsed_descriptor_bundle_refii",
        0,
        native_bundle_ran ? bundle_ctx.r3.u32 : original_ctx.r3.u32);
    bool generated_handoff = false;
    uint32_t generated_handoff_result = 0;
    if (synthetic_descriptors &&
        refii::vfs::kRunCollapsedFrontendGeneratedHandoff) {
      PPCContext handoff_ctx = original_ctx;
      handoff_ctx.r3.u32 = original_ctx.r3.u32;
      handoff_ctx.lr = 0x82378DB4u;
      if (trace) {
        REXLOG_INFO("Refii VFS frontend texture consumer collapsed generated "
                    "handoff enter lr=0x{:08X} object=0x{:08X} "
                    "list_200={} tex_slots_260={} tex_slots_268={} "
                    "tex_out_280={} main_menu=[{}]",
                    handoff_ctx.lr,
                    original_ctx.r3.u32,
                    refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                        base, original_ctx.r3.u32, 200, 8),
                    refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                        base, original_ctx.r3.u32, 260, 8),
                    refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                        base, original_ctx.r3.u32, 268, 8),
                    refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                        base, original_ctx.r3.u32, 280, 4),
                    refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
      }
      generated_handoff =
          refii::vfs::TryCollapseFrontendTextureGeneratedHandoff(
              handoff_ctx,
              base,
              false,
              "sub_82378618_collapsed");
      generated_handoff_result = handoff_ctx.r3.u32;
      if (generated_handoff) {
        refii::vfs::
            TryRunFrontendTextureConsumerNativeUpdateForPendingVfsDirect(
                handoff_ctx,
                base,
                original_ctx.r3.u32,
                "sub_82378618_collapsed");
      }
      if (trace) {
        REXLOG_INFO("Refii VFS frontend texture consumer collapsed generated "
                    "handoff exit lr=0x{:08X} object=0x{:08X} "
                    "collapsed={} result=0x{:08X} list_200={} "
                    "tex_slots_260={} tex_slots_268={} tex_out_280={} "
                    "main_menu=[{}]",
                    handoff_ctx.lr,
                    original_ctx.r3.u32,
                    generated_handoff ? "true" : "false",
                    generated_handoff_result,
                    refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                        base, original_ctx.r3.u32, 200, 8),
                    refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                        base, original_ctx.r3.u32, 260, 8),
                    refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                        base, original_ctx.r3.u32, 268, 8),
                    refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                        base, original_ctx.r3.u32, 280, 4),
                    refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
      }
    }
    else if (synthetic_descriptors) {
      refii::vfs::RecordRefiiMainMenuBlockerEvent(
          "frontend_descriptor_collapsed_generated_handoff_skipped",
          fmt::format("object=0x{:08X} owner_slots=[{}] next={}",
                      original_ctx.r3.u32,
                      refii::vfs::FormatFrontendTextureConsumerOwnerSlots(
                          base, original_ctx.r3.u32),
                      "ResourceManager_Open_or_DB20"));
      refii::vfs::LogRefiiVfsChainVerdict(
          "frontend_texture_consumer",
          static_cast<uint32_t>(original_ctx.lr),
          "menu_texture_refii_owned",
          "",
          "",
          "frontend_texture_descriptor_consumer",
          "sub_82378618_collapsed",
          "TraceOnce",
          "generated_handoff_suppressed");
      if (trace) {
        REXLOG_INFO("Refii VFS frontend texture consumer collapsed generated "
                    "handoff skipped lr=0x{:08X} object=0x{:08X} "
                    "list_200={} tex_slots_260={} tex_slots_268={} "
                    "tex_out_280={} next={} main_menu=[{}]",
                    original_ctx.lr,
                    original_ctx.r3.u32,
                    refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                        base, original_ctx.r3.u32, 200, 8),
                    refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                        base, original_ctx.r3.u32, 260, 8),
                    refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                        base, original_ctx.r3.u32, 268, 8),
                    refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                        base, original_ctx.r3.u32, 280, 4),
                    "ResourceManager_Open_or_DB20",
                    refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
      }
    }
    ctx = original_ctx;
    ctx.r3.u32 = original_ctx.r3.u32;
    if (trace) {
      REXLOG_INFO("Refii VFS frontend texture consumer collapsed descriptor "
                  "bundle exit lr=0x{:08X} object=0x{:08X} bundle=0x{:08X} "
                  "bundle_result=0x{:08X} synthetic_descriptors={} "
                  "native_bundle_ran={} generated_handoff={} "
                  "handoff_result=0x{:08X} "
                  "bundle_after={} main_menu=[{}]",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  collapsible_bundle_guest,
                  native_bundle_ran ? bundle_ctx.r3.u32 : original_ctx.r3.u32,
                  synthetic_descriptors ? "true" : "false",
                  native_bundle_ran ? "true" : "false",
                  generated_handoff ? "true" : "false",
                  generated_handoff_result,
                  refii::vfs::FormatGuestWordsIfPointer(
                      base, collapsible_bundle_guest, 28),
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    }
    finish_consumer("collapsed_descriptor_bundle",
                    "FrontendTextureConsumer_82378618_collapsed");
    return;
  }

  __imp__sub_82378618(ctx, base);
  finish_consumer("native", "FrontendTextureConsumer_82378618");
}

PPC_FUNC_IMPL(sub_82379088) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t owner_guest = original_ctx.r3.u32;
  const bool active = refii::vfs::FrontendTextureConsumerTraceActive();
  const bool collapse_frontend_handoff =
      active && base != nullptr &&
      static_cast<uint32_t>(original_ctx.lr) == 0x82378DB4u &&
      refii::vfs::LooksLikeGuestPointer(owner_guest) &&
      refii::vfs::LoadGuestU32(base, owner_guest) ==
          refii::vfs::kFrontendTextureConsumerObjectVTable;
  if (collapse_frontend_handoff) {
    refii::vfs::TryCollapseFrontendTextureGeneratedHandoff(
        ctx,
        base,
        true,
        "sub_82379088_hook");
    return;
  }

  __imp__sub_82379088(ctx, base);
}

PPC_FUNC_IMPL(sub_826DB598) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const bool active = refii::vfs::FrontendTextureConsumerTraceActive();
  const bool trace =
      active && refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "FrontendTextureStaticBridge_826DB598", 4);
  if (trace) {
    REXLOG_INFO("VFS trace frontend texture static bridge enter sub_826DB598 "
                "lr=0x{:08X} args=[{}] main_menu=[{}]",
                original_ctx.lr,
                refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  if (active && base != nullptr &&
      static_cast<uint32_t>(original_ctx.lr) == 0x82378638u) {
    constexpr uint32_t kBridgeVectorA = 0x8349B4F0u;
    constexpr uint32_t kBridgeVectorB = 0x8349B500u;
    constexpr uint32_t kBridgeVectorC = 0x8349B4E0u;
    if (trace) {
      REXLOG_INFO(
          "VFS trace frontend texture static bridge collapsed sub_826DB598 "
          "lr=0x{:08X} preserving vectors A=[{}] B=[{}] C=[{}]",
          original_ctx.lr,
          refii::vfs::FormatGuestWords(base, kBridgeVectorA, 4),
          refii::vfs::FormatGuestWords(base, kBridgeVectorB, 4),
          refii::vfs::FormatGuestWords(base, kBridgeVectorC, 4));
    }
    refii::vfs::RecordFrontendTextureConsumerResourceBoundary(
        "sub_826DB598_frontend_static_bridge_collapsed",
        0,
        original_ctx.r3.u32);
    PPCContext synth_ctx = original_ctx;
    uint32_t synth_owner = 0;
    bool synth_opened = false;
    const bool synth_attempted =
        refii::vfs::TryRunActiveFrontendTextureConsumerSyntheticPhase(
            synth_ctx, base, &synth_owner, &synth_opened);
    bool generated_handoff = false;
    uint32_t generated_handoff_result = 0;
    if (synth_opened && refii::vfs::kRunCollapsedFrontendGeneratedHandoff &&
        refii::vfs::LooksLikeGuestPointer(synth_owner)) {
      PPCContext handoff_ctx = original_ctx;
      handoff_ctx.r3.u32 = synth_owner;
      handoff_ctx.lr = 0x82378DB4u;
      generated_handoff =
          refii::vfs::TryCollapseFrontendTextureGeneratedHandoff(
              handoff_ctx,
              base,
              true,
              "sub_826DB598_static_bridge");
      generated_handoff_result = handoff_ctx.r3.u32;
      if (generated_handoff) {
        refii::vfs::
            TryRunFrontendTextureConsumerNativeUpdateForPendingVfsDirect(
                handoff_ctx,
                base,
                synth_owner,
                "sub_826DB598_static_bridge");
      }
    }
    if (trace || synth_opened) {
      REXLOG_INFO("Refii VFS frontend texture static bridge synthetic phase "
                  "attempted={} opened={} generated_handoff={} "
                  "handoff_result=0x{:08X} owner=0x{:08X} lr=0x{:08X} "
                  "owner_slots=[{}] main_menu=[{}]",
                  synth_attempted ? "true" : "false",
                  synth_opened ? "true" : "false",
                  generated_handoff ? "true" : "false",
                  generated_handoff_result,
                  synth_owner,
                  original_ctx.lr,
                  refii::vfs::FormatFrontendTextureConsumerOwnerSlots(
                      base, synth_owner),
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    }
    ctx = original_ctx;
    return;
  }

  __imp__sub_826DB598(ctx, base);

  if (active) {
    PPCContext synth_ctx = original_ctx;
    uint32_t synth_owner = 0;
    bool synth_opened = false;
    const bool synth_attempted =
        refii::vfs::TryRunActiveFrontendTextureConsumerSyntheticPhase(
            synth_ctx, base, &synth_owner, &synth_opened);
    bool generated_handoff = false;
    uint32_t generated_handoff_result = 0;
    if (synth_opened && refii::vfs::kRunCollapsedFrontendGeneratedHandoff &&
        refii::vfs::LooksLikeGuestPointer(synth_owner)) {
      PPCContext handoff_ctx = original_ctx;
      handoff_ctx.r3.u32 = synth_owner;
      handoff_ctx.lr = 0x82378DB4u;
      generated_handoff =
          refii::vfs::TryCollapseFrontendTextureGeneratedHandoff(
              handoff_ctx,
              base,
              true,
              "sub_826DB598_static_bridge_after_native");
      generated_handoff_result = handoff_ctx.r3.u32;
      if (generated_handoff) {
        refii::vfs::
            TryRunFrontendTextureConsumerNativeUpdateForPendingVfsDirect(
                handoff_ctx,
                base,
                synth_owner,
                "sub_826DB598_static_bridge_after_native");
      }
    }
    if (trace || synth_opened) {
      REXLOG_INFO("Refii VFS frontend texture static bridge synthetic phase "
                  "attempted={} opened={} generated_handoff={} "
                  "handoff_result=0x{:08X} owner=0x{:08X} lr=0x{:08X} "
                  "owner_slots=[{}] main_menu=[{}]",
                  synth_attempted ? "true" : "false",
                  synth_opened ? "true" : "false",
                  generated_handoff ? "true" : "false",
                  generated_handoff_result,
                  synth_owner,
                  original_ctx.lr,
                  refii::vfs::FormatFrontendTextureConsumerOwnerSlots(
                      base, synth_owner),
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    }
    refii::vfs::RecordFrontendTextureConsumerResourceBoundary(
        "sub_826DB598_frontend_static_bridge",
        0,
        ctx.r3.u32);
  }
  if (trace) {
    REXLOG_INFO("VFS trace frontend texture static bridge exit sub_826DB598 "
                "lr=0x{:08X} result=0x{:08X} main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

PPC_FUNC_IMPL(sub_826D5E48) {
  PPC_FUNC_PROLOGUE();

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

PPC_FUNC_IMPL(sub_821DE2E8) {
  PPC_FUNC_PROLOGUE();

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
      active && descriptor_guest != 0 && !collapse_shape.empty();
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

PPC_FUNC_IMPL(sub_82379D78) {
  PPC_FUNC_PROLOGUE();

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

PPC_FUNC_IMPL(sub_82378F00) {
  PPC_FUNC_PROLOGUE();

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

PPC_FUNC_IMPL(sub_82CC38E8) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
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

PPC_FUNC_IMPL(sub_822F3B20) {
  PPC_FUNC_PROLOGUE();

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
  } else if (native_result == 0) {
    int64_t false_age_ms = -1;
    if (refii::vfs::RefiiStartupReadyGateFalseLongEnoughForDebugAccelerator(
            &false_age_ms)) {
      uint32_t pending_before = 0;
      if (original_ctx.r3.u32 >= 0x10000) {
        pending_before =
            refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 52);
        refii::vfs::StoreGuestU8(base, original_ctx.r3.u32 + 5, 1);
        if (pending_before != 0) {
          refii::vfs::StoreGuestU32(base, original_ctx.r3.u32 + 52, 0);
        }
      }
      ctx.r3.u32 = 1;
      constexpr const char* ready_patch_reason =
          "ready_poll_refii_frontend_texture_ready";
      refii::vfs::RecordRefiiStartupReadyGateObjectPatch(
          original_ctx.r3.u32, ready_patch_reason);
      refii::vfs::MarkRefiiStartupReadyGateDebugAcceleratorEmitted();
      refii::vfs::RecordRefiiStartupReadyPoll(
          base, original_ctx.r3.u32, original_ctx.lr, ctx.r3.u32);
      if (refii::vfs::VfsTracingEnabled()) {
        REXLOG_INFO("Refii VFS startup ready-gate VFS completion bridge "
                    "lr=0x{:08X} object=0x{:08X} reason={} false_age_ms={} "
                    "pending_before=0x{:08X} native_result=0x{:08X} "
                    "result=0x{:08X} handoff=[{}] main_menu=[{}]",
                    original_ctx.lr,
                    original_ctx.r3.u32,
                    ready_patch_reason,
                    false_age_ms,
                    pending_before,
                    native_result,
                    ctx.r3.u32,
                    refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                    refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
      }
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

PPC_FUNC_IMPL(rex_Sleep) {
  PPC_FUNC_PROLOGUE();

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

  __imp__rex_Sleep(ctx, base);
  refii::vfs::MaybeLogRefiiStartupVideoReadyNoFinalizerBlocker(
      original_ctx.lr, "StartupVideoLoop_Sleep", base);
  if (const uint32_t update_guest =
          refii::vfs::LastRefiiStartupUpdateGuest();
      update_guest >= 0x10000) {
    refii::vfs::RecordRefiiStartupUpdateSnapshot(
        base, update_guest, original_ctx.lr, ctx.r3.u32);
  }
  refii::vfs::MaybeApplyRefiiVfsStartupHandoffReadyPatch(
      base, original_ctx.lr, "StartupVideoLoop_Sleep");

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

PPC_FUNC_IMPL(sub_821E2C08) {
  PPC_FUNC_PROLOGUE();

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

PPC_FUNC_IMPL(sub_82A41F10) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const bool trace =
      refii::vfs::kTraceNonVfsBootFrameProbes &&
      refii::vfs::VfsTracingEnabled() &&
      original_ctx.lr == 0x82389274 &&
      (refii::vfs::RefiiStartupVideosReadyForMenuHandoff() ||
       refii::vfs::RefiiMainMenuTraceNeedsFrontendHandoff()) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoFrame_RenderQueue_82A41F10", 12);
  const auto started_at = std::chrono::steady_clock::now();
  if (trace) {
    REXLOG_INFO("VFS trace post-video frame render-queue enter sub_82A41F10 "
                "lr=0x{:08X} args=[{}] chain=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
                refii::vfs::FormatCurrentPostVideoFrameChainState(base),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82A41F10(ctx, base);

  if (trace) {
    const auto elapsed_ms =
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - started_at)
            .count();
    refii::vfs::RecordRefiiPostVideoUiEvent("PostVideoFrame_82A41F10",
                                                original_ctx.lr,
                                                ctx.r3.u32,
                                                original_ctx.r3.u32,
                                                original_ctx.r4.u32,
                                                original_ctx.r5.u32);
    REXLOG_INFO("VFS trace post-video frame render-queue exit sub_82A41F10 "
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

PPC_FUNC_IMPL(sub_82377DC8) {
  PPC_FUNC_PROLOGUE();

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

#define REFII_BIK_OBJECT_METHOD_TRACE(function_name, imp_name, label)        \
  PPC_FUNC_IMPL(function_name) {                                             \
    PPC_FUNC_PROLOGUE();                                                     \
                                                                             \
    const PPCContext original_ctx = ctx;                                     \
    const uint32_t object_guest = original_ctx.r3.u32;                       \
    const uint32_t stream_before =                                           \
        object_guest != 0 ? refii::vfs::LoadGuestU32(base, object_guest + 8) \
                          : 0;                                               \
    const bool trace =                                                       \
        refii::vfs::ShouldTraceBikConsumerHandoffProbe(                  \
            base, object_guest, stream_before, {}, label, 6);                \
    if (trace) {                                                             \
      REXLOG_INFO("VFS trace BIK object method enter " label                 \
                  " lr=0x{:08X} ctr=0x{:08X} args=[{}] {}",                 \
                  original_ctx.lr,                                           \
                  original_ctx.ctr.u32,                                      \
                  refii::vfs::FormatPostVideoBoundaryArgs(base,          \
                                                              original_ctx),  \
                  refii::vfs::FormatBikObjectVirtualProbe(base,          \
                                                              object_guest)); \
    }                                                                        \
                                                                             \
    imp_name(ctx, base);                                                     \
                                                                             \
    const uint32_t stream_after =                                            \
        object_guest != 0 ? refii::vfs::LoadGuestU32(base, object_guest + 8) \
                          : 0;                                               \
    refii::vfs::RecordRefiiStartupVideoObjectHandoff(base,               \
                                                         label,              \
                                                         original_ctx.lr,    \
                                                         object_guest,       \
                                                         stream_before,      \
                                                         stream_after,       \
                                                         ctx.r3.u32);        \
    if (trace) {                                                             \
      REXLOG_INFO("VFS trace BIK object method exit " label                  \
                  " lr=0x{:08X} result=0x{:08X} stream=0x{:08X}->0x{:08X} " \
                  "args_after=[{}] {}",                                     \
                  original_ctx.lr,                                           \
                  ctx.r3.u32,                                                \
                  stream_before,                                             \
                  stream_after,                                              \
                  refii::vfs::FormatPostVideoBoundaryArgs(base, ctx),    \
                  refii::vfs::FormatBikObjectVirtualProbe(base,          \
                                                              object_guest)); \
    }                                                                        \
  }

REFII_BIK_OBJECT_METHOD_TRACE(sub_82C6E308,
                              __imp__sub_82C6E308,
                              "BIKObject_Method_82C6E308")
REFII_BIK_OBJECT_METHOD_TRACE(sub_82C6E888,
                              __imp__sub_82C6E888,
                              "BIKObject_ReleaseStream_82C6E888")
REFII_BIK_OBJECT_METHOD_TRACE(sub_82C6E978,
                              __imp__sub_82C6E978,
                              "BIKObject_SeekOrRead_82C6E978")
REFII_BIK_OBJECT_METHOD_TRACE(sub_82C6E9F0,
                              __imp__sub_82C6E9F0,
                              "BIKObject_SetOffset_82C6E9F0")
REFII_BIK_OBJECT_METHOD_TRACE(sub_82C6EA08,
                              __imp__sub_82C6EA08,
                              "BIKObject_AddOffset_82C6EA08")
REFII_BIK_OBJECT_METHOD_TRACE(sub_82C6EA28,
                              __imp__sub_82C6EA28,
                              "BIKObject_GetOffset_82C6EA28")

#undef REFII_BIK_OBJECT_METHOD_TRACE

PPC_FUNC_IMPL(sub_82C6F2A8) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t object_guest = original_ctx.r3.u32;
  const uint32_t stream_before =
      object_guest != 0 ? refii::vfs::LoadGuestU32(base, object_guest + 8)
                        : 0;
  const std::string requested_name =
      refii::vfs::ReadStartupBikRequestName(base, original_ctx.r4.u32);
  const bool trace = refii::vfs::ShouldTraceBikConsumerHandoffProbe(
      base,
      object_guest,
      stream_before,
      requested_name,
      "BIKObject_Open_82C6F2A8",
      8);
  if (trace) {
    REXLOG_INFO("VFS trace BIK object open enter sub_82C6F2A8 "
                "lr=0x{:08X} ctr=0x{:08X} requested={} mode=0x{:08X} "
                "flags=0x{:08X} object=0x{:08X} stream_before=0x{:08X} "
                "object_state=[{}] stream_before_shape=[{}] handoff=[{}]",
                original_ctx.lr,
                original_ctx.ctr.u32,
                requested_name.empty() ? "unknown" : requested_name,
                original_ctx.r5.u32,
                original_ctx.r6.u32,
                object_guest,
                stream_before,
                refii::vfs::FormatBikObjectVirtualProbe(base, object_guest),
                refii::vfs::FormatBikPpcStreamSummary(base, stream_before),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog());
  }

  __imp__sub_82C6F2A8(ctx, base);

  const uint32_t stream_after =
      object_guest != 0 ? refii::vfs::LoadGuestU32(base, object_guest + 8)
                        : 0;
  const bool trace_after =
      trace || refii::vfs::ShouldTraceBikConsumerHandoffProbe(
                   base,
                   object_guest,
                   stream_after,
                   requested_name,
                   "BIKObject_Open_82C6F2A8_exit",
                   8);
  refii::vfs::RecordRefiiStartupVideoObjectHandoff(
      base,
      "BIKObject_Open_82C6F2A8",
      original_ctx.lr,
      object_guest,
      stream_before,
      stream_after,
      ctx.r3.u32,
      requested_name);
  if (trace_after) {
    REXLOG_INFO("VFS trace BIK object open exit sub_82C6F2A8 "
                "lr=0x{:08X} result=0x{:08X} requested={} object=0x{:08X} "
                "stream=0x{:08X}->0x{:08X} object_state=[{}] "
                "stream_after_shape=[{}] handoff=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                requested_name.empty() ? "unknown" : requested_name,
                object_guest,
                stream_before,
                stream_after,
                refii::vfs::FormatBikObjectVirtualProbe(base, object_guest),
                refii::vfs::FormatBikBufferedNativeShape(base, stream_after),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog());
  }
}

PPC_FUNC_IMPL(sub_82C6EF70) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t object_guest = original_ctx.r3.u32;
  const bool trace = refii::vfs::ShouldTraceBikObjectVirtualProbe(
      base, object_guest, "BIKObject_Finalizer_82C6EF70", 6);
  if (trace) {
    REXLOG_INFO("VFS trace BIK object finalizer enter lr=0x{:08X} ctr=0x{:08X} "
                "args=[{}] {}",
                original_ctx.lr,
                original_ctx.ctr.u32,
                refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
                refii::vfs::FormatBikObjectVirtualProbe(base, object_guest));
  }

  __imp__sub_82C6EF70(ctx, base);
  refii::vfs::RecordRefiiStartupVideoFinalizer(
      base, object_guest, ctx.r3.u32);

  if (trace && refii::vfs::RefiiIsTrackedStartupVideoObject(object_guest)) {
    refii::vfs::RecordRefiiPostVideoUiEvent("BIKObject_Finalizer_82C6EF70",
                                                original_ctx.lr,
                                                ctx.r3.u32,
                                                original_ctx.r3.u32,
                                                original_ctx.r4.u32,
                                                original_ctx.r5.u32);
  }
  if (trace) {
    REXLOG_INFO("VFS trace BIK object finalizer exit lr=0x{:08X} result=0x{:08X} "
                "args_after=[{}] {}",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::FormatPostVideoBoundaryArgs(base, ctx),
                refii::vfs::FormatBikObjectVirtualProbe(base, object_guest));
  }
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
      original_ctx.lr, "BIKObject_Finalizer_82C6EF70", base);
}

PPC_FUNC_IMPL(sub_82B49028) {
  PPC_FUNC_PROLOGUE();

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

PPC_FUNC_IMPL(sub_82B49220) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t queue_guest = original_ctx.r3.u32;
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      (refii::vfs::RefiiPostVideoVfsHandoffTraceActive() ||
       refii::vfs::RefiiStartupVideosReadyForMenuHandoff()) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "NativeVfsAsyncQueuePop", 96);

  __imp__sub_82B49220(ctx, base);

  if (trace || (ctx.r3.u32 != 0 && refii::vfs::VfsTracingEnabled() &&
                refii::vfs::RefiiPostVideoVfsHandoffTraceActive())) {
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

PPC_FUNC_IMPL(sub_8244B978) {
  PPC_FUNC_PROLOGUE();

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
          inline_ctx, base, request_guest, "NativeVfsSchedulerInsertPending");

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

PPC_FUNC_IMPL(sub_8244BA28) {
  PPC_FUNC_PROLOGUE();

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

PPC_FUNC_IMPL(sub_8244BAD8) {
  PPC_FUNC_PROLOGUE();

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

PPC_FUNC_IMPL(sub_8244BED0) {
  PPC_FUNC_PROLOGUE();

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

PPC_FUNC_IMPL(sub_82B49D80) {
  PPC_FUNC_PROLOGUE();

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
           "NativeVfsRetryInsert", 64));

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
          inline_ctx, base, request_guest, "NativeVfsRetryInsert");

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

PPC_FUNC_IMPL(sub_822C9388) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t event_handle_guest = original_ctx.r3.u32;
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

PPC_FUNC_IMPL(sub_82B4F400) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
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

  refii::vfs::MaybeSignalRefiiPostVideoGeneratedWaitBeforeNative(
      original_ctx, base, wait_object_guest, wait_handle_guest);

  if (refii::vfs::TryCollapseRefiiCompletedLoadingOwnerWorkerWait(
          ctx, original_ctx, base, wait_object_guest, wait_handle_guest, trace)) {
    return;
  }
  if (refii::vfs::TryCollapseRefiiPostVideoRendererSyncWait(
          ctx, original_ctx, base, wait_object_guest, wait_handle_guest, trace)) {
    return;
  }
  if (refii::vfs::TryCollapseRefiiPostVideoGeneratedRenderWait(
          ctx, original_ctx, base, wait_object_guest, wait_handle_guest, trace)) {
    return;
  }
  if (refii::vfs::TryCollapseRefiiCompletedNativeVfsWorkerWait(
          ctx, original_ctx, base, wait_object_guest, wait_handle_guest, trace)) {
    return;
  }

  __imp__sub_82B4F400(ctx, base);

  const int64_t wait_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                              std::chrono::steady_clock::now() - begin)
                              .count();
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
  if (ctx.r3.u32 == 0 &&
      refii::vfs::IsPostVideoGeneratedWaitLr(
          static_cast<uint32_t>(original_ctx.lr)) &&
      refii::vfs::RefiiStartupVideosReadyForMenuHandoff() &&
      (refii::vfs::RefiiMainMenuTraceNeedsFrontendHandoff() ||
       refii::vfs::RefiiMainMenuTraceAllowsPostVideoReadyAdvance() ||
       refii::vfs::RefiiMainMenuTextureKnownOwnedByVfsIndex())) {
    PPCContext post_video_synthetic_ctx = original_ctx;
    refii::vfs::TryRunPostVideoFrontendSyntheticHandoff(
        post_video_synthetic_ctx,
        base,
        wait_object_guest,
        "GeneratedEvent_Wait_PostNativeOwnerProbe");
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

PPC_FUNC_IMPL(sub_82388A18) {
  PPC_FUNC_PROLOGUE();

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

  bool refii_nonblocking_wait = false;
  bool refii_wait_signaled = false;
  bool refii_post_loading_native_run = false;
  bool refii_generated_state_released = false;
  uint32_t refii_wait_owner = original_ctx.r3.u32;
  uint32_t refii_wait_handoff = 0;
  uint32_t refii_signal_handle = 0;
  uint32_t refii_wait_handle = 0;
  const bool refii_post_video_wait_lr =
      original_ctx.lr == 0x82377D1Cu || original_ctx.lr == 0x823891C8u ||
      original_ctx.lr == 0x82389308u || original_ctx.lr == 0x82389390u;
  const bool refii_completed_post_vfs_chain =
      refii::vfs::RefiiPostVfsChainReadyForLoadingOwnerRelease();
  const bool refii_menu_texture_shape_ready =
      original_ctx.lr == 0x82389390u &&
      refii::vfs::RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload() &&
      !refii::vfs::RefiiMainMenuTraceNeedsTexturePrime();
  const bool refii_frame_wait_can_release =
      refii_completed_post_vfs_chain || refii_menu_texture_shape_ready;
  if (base != nullptr && refii_post_video_wait_lr &&
      refii::vfs::RefiiLooseAssetBackendEnabled() &&
      refii::vfs::RefiiStartupVideosReadyForMenuHandoff() &&
      refii_frame_wait_can_release &&
      refii::vfs::LooksLikeGuestPointer(refii_wait_owner)) {
    refii_wait_handoff =
        refii::vfs::LoadGuestU32(base, refii_wait_owner + 4);
    if (refii::vfs::LooksLikeGuestPointer(refii_wait_handoff)) {
      refii_signal_handle =
          refii::vfs::LoadGuestU32(base, refii_wait_handoff + 456);
      refii_wait_handle =
          refii::vfs::LoadGuestU32(base, refii_wait_handoff + 460 + 128);
      refii::vfs::StoreGuestU8(base, refii_wait_handoff + 168, 1);
      refii::vfs::StoreGuestU8(base, refii_wait_handoff + 244, 1);

      auto looks_like_generated_event_handle = [](uint32_t handle) {
        return (handle & 0xFF000000u) == 0xF8000000u ||
               refii::vfs::LooksLikeGuestPointer(handle);
      };

      PPCContext signal_ctx = original_ctx;
      if (looks_like_generated_event_handle(refii_signal_handle)) {
        signal_ctx.r3.u32 = refii_signal_handle;
        ::__imp__sub_822C9388(signal_ctx, base);
        refii_wait_signaled = true;
      }
      PPCContext wait_signal_ctx = original_ctx;
      if (looks_like_generated_event_handle(refii_wait_handle) &&
          refii_wait_handle != refii_signal_handle) {
        wait_signal_ctx.r3.u32 = refii_wait_handle;
        ::__imp__sub_822C9388(wait_signal_ctx, base);
        refii_wait_signaled = true;
      }

      refii_post_loading_native_run =
          refii::vfs::TryRunRefiiPostLoadingFrontendRenderRequestNativeFromTrace(
              original_ctx, base, "sub_82388A18_vfs_wait_release");
      refii_generated_state_released =
          refii::vfs::TryFinalizeRefiiPostLoadingFrontendGeneratedState(
              original_ctx,
              base,
              "sub_82388A18_vfs_wait_release",
              true,
              false);
      refii_nonblocking_wait =
          refii_wait_signaled || refii_post_loading_native_run ||
          refii_generated_state_released;
      if (refii_nonblocking_wait) {
        ctx.r3.u32 = 0;
        refii::vfs::RecordRefiiMainMenuBlockerEvent(
            "post_video_frame_wait_vfs_nonblocking",
            fmt::format("lr=0x{:08X} owner=0x{:08X} handoff=0x{:08X} "
                        "signal_handle=0x{:08X} wait_handle=0x{:08X} "
                        "signaled={} native_run={} generated_release={} "
                        "completed_chain={} texture_shape_ready={} "
                        "state=[{}]",
                        static_cast<uint32_t>(original_ctx.lr),
                        refii_wait_owner,
                        refii_wait_handoff,
                        refii_signal_handle,
                        refii_wait_handle,
                        refii_wait_signaled ? "true" : "false",
                        refii_post_loading_native_run ? "true" : "false",
                        refii_generated_state_released ? "true" : "false",
                        refii_completed_post_vfs_chain ? "true" : "false",
                        refii_menu_texture_shape_ready ? "true" : "false",
                        refii::vfs::FormatPostVideoFrameWaitState(
                            base, original_ctx.r3.u32)));
        if (trace || refii::vfs::VfsTracingEnabled() ||
            refii::vfs::ShouldLogRepeatedVfsTraceOperation(
                "PostVideoFrameWaitVfsNonblocking", 8)) {
          REXLOG_INFO("Refii VFS post-video frame wait released by VFS "
                      "lr=0x{:08X} owner=0x{:08X} handoff=0x{:08X} "
                      "signal_handle=0x{:08X} wait_handle=0x{:08X} "
                      "signaled={} native_run={} generated_release={} "
                      "completed_chain={} texture_shape_ready={} "
                      "handoff_trace=[{}] main_menu=[{}]",
                      original_ctx.lr,
                      refii_wait_owner,
                      refii_wait_handoff,
                      refii_signal_handle,
                      refii_wait_handle,
                      refii_wait_signaled ? "true" : "false",
                      refii_post_loading_native_run ? "true" : "false",
                      refii_generated_state_released ? "true" : "false",
                      refii_completed_post_vfs_chain ? "true" : "false",
                      refii_menu_texture_shape_ready ? "true" : "false",
                      refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                      refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
        }
        refii::vfs::LogRefiiVfsChainVerdict(
            "frontend_post_video",
            static_cast<uint32_t>(original_ctx.lr),
            "post_video_frame_wait",
            "",
            "",
            "native_generated_wait_pair",
            "sub_82388A18_vfs_nonblocking",
            "Opened",
            "main_menu_or_next_frontend_request");
      }
    }
  }

  if (!refii_nonblocking_wait) {
    __imp__sub_82388A18(ctx, base);
  }

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

PPC_FUNC_IMPL(sub_82A41B00) {
  PPC_FUNC_PROLOGUE();

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
