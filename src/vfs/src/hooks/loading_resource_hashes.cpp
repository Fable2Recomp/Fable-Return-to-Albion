#include "vfs_hooks_private.h"

bool RefiiShouldTraceLoadingResourceLookup(uint32_t lr) {
  switch (lr) {
  case 0x8235E51Cu:
  case 0x82360594u:
  case 0x823605A0u:
  case 0x82360780u:
  case 0x8236078Cu:
    return refii::vfs::VfsTracingEnabled() &&
           refii::vfs::RefiiPostVideoVfsHandoffTraceActive();
  default:
    return false;
  }
}

std::string RefiiBootLocaleBankAliasForRequested(std::string_view requested_key) {
  const std::string normalized = refii::vfs::NormalizeLooseAssetKey(requested_key);
  if (normalized == "language/speech/speech.bnk") {
    return "language/en-uk/speech/speech.bnk";
  }
  if (normalized == "language/lipsync/lipsync.bnk" ||
      normalized == "language/lipsync.bnk") {
    return "language/en-uk/lipsync/lipsync.bnk";
  }
  return {};
}

PPC_FUNC_IMPL(sub_8237AC80) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const bool trace =
      RefiiShouldTraceLoadingResourceLookup(original_ctx.lr) &&
      refii::vfs::ShouldLogLooseStreamOperation(
          "LoadingResourceVectorLookup_8237AC80", original_ctx.r3.u32, 16);
  if (trace) {
    REXLOG_INFO("VFS trace loading resource vector lookup enter "
                "sub_8237AC80 lr=0x{:08X} table=0x{:08X} name_obj=0x{:08X} "
                "name={} table_words={} name_words={}",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                refii::vfs::ReadGuestWideStringObject(
                    base, original_ctx.r4.u32, 512),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 48),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r4.u32, 12));
  }

  __imp__sub_8237AC80(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace loading resource vector lookup exit "
                "sub_8237AC80 lr=0x{:08X} result=0x{:08X} vector_words={} "
                "first_values={}",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::FormatGuestWordsIfPointer(base, ctx.r3.u32, 8),
                refii::vfs::FormatGuestWordsAtOffsetIfPointer(
                    base, ctx.r3.u32, 4, 12));
  }
}

PPC_FUNC_IMPL(sub_8237AA98) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const bool trace =
      RefiiShouldTraceLoadingResourceLookup(original_ctx.lr) &&
      refii::vfs::ShouldLogLooseStreamOperation(
          "LoadingResourceHandleLookup_8237AA98", original_ctx.r4.u32, 32);
  const uint32_t lookup_handle =
      base != nullptr && refii::vfs::LooksLikeGuestPointer(original_ctx.r5.u32)
          ? refii::vfs::LoadGuestU32(base, original_ctx.r5.u32)
          : 0;
  if (trace) {
    REXLOG_INFO("VFS trace loading resource handle lookup enter "
                "sub_8237AA98 lr=0x{:08X} out=0x{:08X} table=0x{:08X} "
                "handle_ptr=0x{:08X} handle=0x{:08X} table_words={} "
                "out_before={} handle_words={}",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                original_ctx.r5.u32,
                lookup_handle,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r4.u32, 48),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 12),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r5.u32, 4));
  }

  const bool refii_bridged_lookup =
      refii::vfs::TryResolveRefiiBridgedLoadingSequenceHandleLookup(
          ctx,
          base,
          original_ctx.r3.u32,
          original_ctx.r4.u32,
          lookup_handle);
  if (!refii_bridged_lookup) {
    __imp__sub_8237AA98(ctx, base);
  }

  if (trace) {
    REXLOG_INFO("VFS trace loading resource handle lookup exit "
                "sub_8237AA98 lr=0x{:08X} result=0x{:08X} "
                "bridged_lookup={} out_after={} resolved_name={}",
                original_ctx.lr,
                ctx.r3.u32,
                refii_bridged_lookup ? "true" : "false",
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 12),
                refii::vfs::ReadGuestWideStringObject(
                    base, original_ctx.r3.u32, 512));
  }
}

PPC_FUNC_IMPL(sub_822B88A8) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const bool trace =
      RefiiShouldTraceLoadingResourceLookup(original_ctx.lr) &&
      refii::vfs::ShouldLogLooseStreamOperation(
          "LoadingResourceStringAssign_822B88A8", original_ctx.r3.u32, 32);
  if (trace) {
    REXLOG_INFO("VFS trace loading resource string assign enter "
                "sub_822B88A8 lr=0x{:08X} dst=0x{:08X} src=0x{:08X} "
                "dst_before={} src_words={} src_name={}",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 12),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r4.u32, 12),
                refii::vfs::ReadGuestWideStringObject(
                    base, original_ctx.r4.u32, 512));
  }

  __imp__sub_822B88A8(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace loading resource string assign exit "
                "sub_822B88A8 lr=0x{:08X} result=0x{:08X} dst_after={} "
                "dst_name={}",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 12),
                refii::vfs::ReadGuestWideStringObject(
                    base, original_ctx.r3.u32, 512));
  }
}

#define REFII_LOADING_SCREEN_OWNER_WRAPPER(function_name, trace_name, limit)  \
  PPC_FUNC_IMPL(function_name) {                                             \
    PPC_FUNC_PROLOGUE();                                                     \
                                                                              \
    const PPCContext original_ctx = ctx;                                      \
    const uint32_t loading_owner = original_ctx.r3.u32;                       \
    refii::vfs::RecordRefiiLoadingScreenOwner(base,                      \
                                                  loading_owner,             \
                                                  #function_name);           \
    const bool trace =                                                       \
        refii::vfs::VfsTracingEnabled() &&                               \
        refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&             \
        refii::vfs::ShouldLogLooseStreamOperation(                       \
            trace_name, loading_owner, limit);                               \
    std::string before_state;                                                \
    if (trace) {                                                             \
      before_state = refii::vfs::FormatRefiiLoadingScreenOwnerState(     \
          base, loading_owner);                                              \
      const uint32_t stack_arg92 =                                            \
          base != nullptr &&                                                  \
                  refii::vfs::LooksLikeGuestPointer(                     \
                      original_ctx.r1.u32 + 92)                               \
              ? refii::vfs::LoadGuestU32(base, original_ctx.r1.u32 + 92) \
              : 0;                                                           \
      REXLOG_INFO("VFS trace loading-screen pipeline enter " #function_name  \
                  " lr=0x{:08X} r3=0x{:08X} r4=0x{:08X} r5=0x{:08X} "       \
                  "r6=0x{:08X} r7=0x{:08X} r8=0x{:08X} r9=0x{:08X} "        \
                  "r10=0x{:08X} f1={} f2={} stack92=0x{:08X} owner=[{}] "   \
                  "handoff=[{}] main_menu=[{}]",                            \
                  original_ctx.lr,                                           \
                  original_ctx.r3.u32,                                       \
                  original_ctx.r4.u32,                                       \
                  original_ctx.r5.u32,                                       \
                  original_ctx.r6.u32,                                       \
                  original_ctx.r7.u32,                                       \
                  original_ctx.r8.u32,                                       \
                  original_ctx.r9.u32,                                       \
                  original_ctx.r10.u32,                                      \
                  original_ctx.f1.f64,                                       \
                  original_ctx.f2.f64,                                       \
                  stack_arg92,                                               \
                  before_state,                                              \
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),  \
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());  \
    }                                                                        \
    const std::string_view refii_function_name(#function_name);              \
    const bool refii_sequence_bridge_site =                                  \
        refii_function_name == "sub_8235E1E0" ||                             \
        refii_function_name == "sub_8235E420" ||                             \
        refii_function_name == "sub_82360418" ||                             \
        refii_function_name == "sub_82360618" ||                             \
        refii_function_name == "sub_82360968";                               \
    uint32_t refii_sequence_bridge_handle = 0;                                \
    bool refii_sequence_bridged =                                             \
        refii_sequence_bridge_site &&                                         \
        refii::vfs::TryBridgeLoadingScreenSequenceListFromRefiiHandle(   \
            ctx, base, loading_owner, &refii_sequence_bridge_handle);         \
    uint32_t refii_sequence_ready_for_skip_handle =                           \
        refii_sequence_bridge_handle;                                         \
    const bool refii_sequence_ready_for_skip =                                \
        refii_sequence_bridged ||                                             \
        refii::vfs::RefiiLoadingScreenSequenceReady(                      \
            base, loading_owner, &refii_sequence_ready_for_skip_handle);       \
    if (!refii_sequence_bridged && refii_sequence_ready_for_skip &&           \
        refii_sequence_bridge_handle == 0) {                                  \
      refii_sequence_bridge_handle = refii_sequence_ready_for_skip_handle;    \
    }                                                                         \
    const bool refii_loading_render_collapsed =                               \
        refii_sequence_ready_for_skip &&                                      \
        refii_function_name == "sub_82360968" &&                             \
        refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted() &&    \
        refii::vfs::RefiiPostVideoFrontendChainConsumed();               \
    const bool refii_loading_owner_completed =                                \
        base != nullptr &&                                                    \
        refii::vfs::LooksLikeGuestPointer(loading_owner) &&              \
        refii::vfs::RefiiCompletedLoadingScreenOwner().load(              \
            std::memory_order_relaxed) == loading_owner;                      \
    const bool refii_loading_owner_done =                                     \
        base != nullptr &&                                                    \
        refii::vfs::LooksLikeGuestPointer(loading_owner) &&              \
        ((refii::vfs::LoadGuestU8(base, loading_owner + 177) != 0 &&      \
          refii::vfs::RefiiCollapsedLoadingSequenceReadyForOwnerRelease())\
         || refii_loading_owner_completed);                                   \
    const bool refii_loading_owner_already_finished =                         \
        refii_loading_owner_done &&                                           \
        (refii_function_name == "sub_8235D5E8" ||                            \
         refii_function_name == "sub_8235E1E0" ||                            \
         refii_function_name == "sub_8235E420");                             \
    const bool refii_loading_stage_already_finished =                         \
        refii_loading_owner_done &&                                           \
        (refii_function_name == "sub_82360418" ||                            \
         refii_function_name == "sub_82360618" ||                            \
         refii_function_name == "sub_82360968");                             \
    const bool refii_loading_fx_collapsed = false;                            \
    const bool refii_sequence_native_skipped =                                \
        refii_loading_render_collapsed ||                                     \
        refii_loading_owner_already_finished ||                               \
        refii_loading_stage_already_finished;                                 \
    if (!refii_sequence_native_skipped) {                                     \
      __imp__##function_name(ctx, base);                                      \
      if (!refii_sequence_bridged) {                                          \
        refii_sequence_bridged =                                              \
            refii_sequence_bridge_site &&                                     \
            refii::vfs::TryBridgeLoadingScreenSequenceListFromRefiiHandle(\
                ctx, base, loading_owner, &refii_sequence_bridge_handle);     \
      }                                                                       \
    } else {                                                                  \
      if (refii_loading_fx_collapsed || refii_loading_render_collapsed) {     \
        ctx.r3.u32 = 1;                                                       \
      } else if (refii_loading_owner_already_finished) {                      \
        ctx.r3.u32 = loading_owner;                                           \
      } else if (refii_loading_stage_already_finished) {                      \
        ctx.r3.u32 = 1;                                                       \
      }                                                                       \
      if (trace) {                                                            \
        REXLOG_INFO("VFS trace loading-screen pipeline collapsed native "     \
                    #function_name " owner=0x{:08X} sequence_handle=0x{:08X} "\
                    "fx_collapsed={} render_collapsed={} owner_finished={} "  \
                    "stage_finished={} owner_completed={}",                   \
                    loading_owner,                                            \
                    refii_sequence_bridge_handle,                             \
                    refii_loading_fx_collapsed ? "true" : "false",          \
                    refii_loading_render_collapsed ? "true" : "false",      \
                    refii_loading_owner_already_finished ? "true" : "false", \
                    refii_loading_stage_already_finished ? "true" : "false", \
                    refii_loading_owner_completed ? "true" : "false");       \
      }                                                                       \
    }                                                                         \
    uint32_t refii_sequence_ready_handle = refii_sequence_bridge_handle;       \
    const bool refii_sequence_ready =                                         \
        refii_sequence_ready_for_skip ||                                      \
        refii::vfs::RefiiLoadingScreenSequenceReady(                      \
            base, loading_owner, &refii_sequence_ready_handle);               \
    if (!refii_sequence_bridged && refii_sequence_ready &&                    \
        refii_sequence_bridge_handle == 0) {                                  \
      refii_sequence_bridge_handle = refii_sequence_ready_handle;             \
    }                                                                         \
    if (refii_sequence_ready &&                                               \
        !refii::vfs::LooksLikeNativeResourceHandle(                       \
            refii_sequence_bridge_handle)) {                                  \
      const uint32_t refii_remembered_sequence_handle =                       \
          refii::vfs::RefiiBridgedLoadingSequenceHandle().load(           \
              std::memory_order_relaxed);                                     \
      if (refii::vfs::LooksLikeNativeResourceHandle(                      \
              refii_remembered_sequence_handle)) {                            \
        refii_sequence_bridge_handle = refii_remembered_sequence_handle;      \
      }                                                                       \
    }                                                                         \
    const bool refii_primary_owner_published =                               \
        refii_sequence_ready &&                                               \
        (refii_function_name == "sub_8235E420" ||                            \
         refii_function_name == "sub_82360418" ||                            \
         refii_function_name == "sub_82360618" ||                            \
         refii_function_name == "sub_82360968") &&                           \
        refii::vfs::TryPublishRefiiPostGameFacePrimaryOwnerFromTrace(    \
            ctx, base, #function_name);                                       \
    PPCContext refii_frontend_run_ctx = original_ctx;                         \
    const bool refii_frontend_request_run =                                   \
        refii_sequence_ready &&                                               \
        (refii_function_name != "sub_8235E420" ||                            \
         refii_primary_owner_published ||                                     \
         refii::vfs::RefiiPostGameFacePrimaryOwnerReady(base)) &&         \
        refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted() &&    \
        refii::vfs::TryRunRefiiPostGameFaceFrontendRenderRequestFromTrace(\
            refii_frontend_run_ctx,                                           \
            base,                                                             \
            refii_sequence_bridged ? #function_name "_sequence_bridge"       \
                                   : #function_name "_sequence_ready");      \
    const bool refii_collapsed_sequence_finish_ready =                        \
        (refii_function_name == "sub_8235D5E8" ||                            \
         refii_function_name == "sub_8235E1E0" ||                            \
         refii_function_name == "sub_8235E420") &&                           \
        refii_sequence_ready &&                                               \
        ctx.r3.u32 != 0 &&                                                    \
        refii::vfs::RefiiCollapsedLoadingSequenceReadyForOwnerRelease();  \
    const bool refii_loading_request_finish_ready =                           \
        refii_collapsed_sequence_finish_ready ||                              \
        (refii_function_name == "sub_82360418" &&                            \
         refii_sequence_ready &&                                              \
         ctx.r3.u32 != 0 &&                                                   \
         refii::vfs::RefiiPostVfsChainReadyForLoadingOwnerRelease()) ||   \
        ((refii_function_name == "sub_82360618" ||                           \
          refii_function_name == "sub_82360968") &&                          \
         refii_sequence_ready &&                                              \
         ctx.r3.u32 != 0 &&                                                   \
         refii_loading_owner_completed);                                      \
    const bool refii_finish_tick_run =                                        \
        (refii_loading_fx_collapsed || refii_loading_render_collapsed ||      \
         refii_loading_request_finish_ready) &&                               \
        refii::vfs::TryFinishRefiiLoadingScreenOwnerFromCollapsedPipeline(\
            original_ctx,                                                     \
            base,                                                             \
            loading_owner,                                                    \
            #function_name,                                                   \
            ctx.r3.u32,                                                       \
            refii_sequence_ready,                                             \
            refii_sequence_bridge_handle);                                    \
    const bool refii_loading_owner_completed_after =                          \
        base != nullptr &&                                                    \
        refii::vfs::LooksLikeGuestPointer(loading_owner) &&              \
        refii::vfs::RefiiCompletedLoadingScreenOwner().load(              \
            std::memory_order_relaxed) == loading_owner;                      \
                                                                              \
    if (trace) {                                                             \
      const std::string after_state =                                        \
          refii::vfs::FormatRefiiLoadingScreenOwnerState(base,            \
                                                             loading_owner);  \
      const std::string summary = fmt::format(                               \
          #function_name " lr=0x{:08X} owner=0x{:08X} result=0x{:08X} "      \
          "sequence_bridged={} sequence_ready={} handle=0x{:08X} "           \
          "primary_published={} frontend_run={} finish_ready={} finish_tick={} "\
          "owner_completed={}",                                               \
          static_cast<uint32_t>(original_ctx.lr),                             \
          loading_owner,                                                      \
          ctx.r3.u32,                                                         \
          refii_sequence_bridged ? "true" : "false",                        \
          refii_sequence_ready ? "true" : "false",                          \
          refii_sequence_bridge_handle,                                       \
          refii_primary_owner_published ? "true" : "false",                 \
          refii_frontend_request_run ? "true" : "false",                    \
          refii_loading_request_finish_ready ? "true" : "false",            \
          refii_finish_tick_run ? "true" : "false",                         \
          refii_loading_owner_completed_after ? "true" : "false");          \
      refii::vfs::RecordRefiiMainMenuBlockerEvent("loading_frame",       \
                                                      summary);               \
      REXLOG_INFO("VFS trace loading-screen pipeline exit " #function_name   \
                  " lr=0x{:08X} result=0x{:08X} sequence_bridged={} "        \
                  "sequence_ready={} sequence_handle=0x{:08X} "              \
                  "primary_published={} frontend_run={} finish_ready={} "    \
                  "finish_tick={} owner_completed={} "                       \
                  "before=[{}] "                                             \
                  "after=[{}] "                                              \
                  "handoff=[{}] main_menu=[{}]",                             \
                  original_ctx.lr,                                           \
                  ctx.r3.u32,                                                \
                  refii_sequence_bridged ? "true" : "false",                \
                  refii_sequence_ready ? "true" : "false",                  \
                  refii_sequence_bridge_handle,                               \
                  refii_primary_owner_published ? "true" : "false",         \
                  refii_frontend_request_run ? "true" : "false",            \
                  refii_loading_request_finish_ready ? "true" : "false",    \
                  refii_finish_tick_run ? "true" : "false",                 \
                  refii_loading_owner_completed_after ? "true" : "false",   \
                  before_state,                                              \
                  after_state,                                               \
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),  \
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());  \
    }                                                                        \
  }

REFII_LOADING_SCREEN_OWNER_WRAPPER(sub_8235D5E8,
                                   "LoadingScreenData_8235D5E8",
                                   8)
REFII_LOADING_SCREEN_OWNER_WRAPPER(sub_8235E1E0,
                                   "LoadingScreenSetup_8235E1E0",
                                   8)
REFII_LOADING_SCREEN_OWNER_WRAPPER(sub_8235E420,
                                   "LoadingScreenSequence_8235E420",
                                   8)
REFII_LOADING_SCREEN_OWNER_WRAPPER(sub_82360418,
                                   "LoadingScreenRequest_82360418",
                                   16)
REFII_LOADING_SCREEN_OWNER_WRAPPER(sub_82360618,
                                   "LoadingScreenFx_82360618",
                                   16)
REFII_LOADING_SCREEN_OWNER_WRAPPER(sub_82360968,
                                   "LoadingScreenRender_82360968",
                                   16)

#undef REFII_LOADING_SCREEN_OWNER_WRAPPER

PPC_FUNC_IMPL(sub_8235E5A0) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t loading_owner = original_ctx.r3.u32;
  bool refii_finish_gate_armed = false;
  bool refii_forced_native_finish = false;
  bool refii_just_completed_finish = false;
  uint32_t refii_sequence_handle = 0;
  const uint32_t refii_loading_frame =
      base != nullptr && refii::vfs::LooksLikeGuestPointer(loading_owner)
          ? refii::vfs::LoadGuestU32(base, loading_owner + 168)
          : 0;
  if (base != nullptr && refii::vfs::LooksLikeGuestPointer(loading_owner) &&
      refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted() &&
      refii::vfs::RefiiPostVideoFrontendChainConsumed() &&
      (refii::vfs::RefiiPostFrontendDeferredHashWakeSignalEmitted() ||
       refii::vfs::RefiiPostVfsChainReadyForLoadingOwnerRelease()) &&
      refii::vfs::RefiiLoadingScreenSequenceReady(
          base, loading_owner, &refii_sequence_handle) &&
      refii_loading_frame >= 10) {
    refii_finish_gate_armed = true;
    // Keep the native fade branch at its teardown point once the VFS-owned
    // post-video assets have reached the real frontend/resource consumers.
    refii::vfs::StoreGuestU32(base, loading_owner + 172, 0);
    if (refii::vfs::LoadGuestU8(base, loading_owner + 176) == 0) {
      refii::vfs::StoreGuestU8(base, loading_owner + 176, 1);
      refii_forced_native_finish = true;
      refii::vfs::RecordRefiiMainMenuBlockerEvent(
          "loading_owner_native_finish_requested",
          fmt::format("owner=0x{:08X} frame={} sequence_handle=0x{:08X} "
                      "lr=0x{:08X}",
                      loading_owner,
                      refii_loading_frame,
                      refii_sequence_handle,
                      static_cast<uint32_t>(original_ctx.lr)));
      if (refii::vfs::VfsTracingEnabled()) {
        REXLOG_INFO("Refii VFS loading owner native finish requested "
                    "owner=0x{:08X} frame={} sequence_handle=0x{:08X} "
                    "lr=0x{:08X} before=[{}] handoff=[{}] main_menu=[{}]",
                    loading_owner,
                    refii_loading_frame,
                    refii_sequence_handle,
                    static_cast<uint32_t>(original_ctx.lr),
                    refii::vfs::FormatRefiiLoadingScreenOwnerState(
                        base, loading_owner),
                    refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                    refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
      }
    }
  }

  __imp__sub_8235E5A0(ctx, base);

  bool refii_completed_finish =
      refii_finish_gate_armed && base != nullptr &&
      refii::vfs::LooksLikeGuestPointer(loading_owner) &&
      refii::vfs::LoadGuestU8(base, loading_owner + 177) != 0;
  if (refii_finish_gate_armed && base != nullptr &&
      refii::vfs::LooksLikeGuestPointer(loading_owner) &&
      refii_completed_finish) {
    const uint32_t finish_event =
        refii::vfs::LoadGuestU32(base, loading_owner + 180);
    const uint32_t previously_completed =
        refii::vfs::RefiiCompletedLoadingScreenOwner().exchange(
            loading_owner, std::memory_order_relaxed);
    refii_just_completed_finish = previously_completed != loading_owner;
    if (refii_just_completed_finish) {
      refii::vfs::RecordRefiiMainMenuBlockerEvent(
          "loading_owner_finish_completed",
          fmt::format("owner=0x{:08X} frame={} event=0x{:08X} lr=0x{:08X}",
                      loading_owner,
                      refii::vfs::LoadGuestU32(base, loading_owner + 168),
                      finish_event,
                      static_cast<uint32_t>(original_ctx.lr)));
    }
    if (refii_just_completed_finish && refii::vfs::VfsTracingEnabled()) {
      REXLOG_INFO("Refii VFS loading owner finish completed "
                  "owner=0x{:08X} frame={} event=0x{:08X} lr=0x{:08X} "
                  "after=[{}] handoff=[{}] main_menu=[{}]",
                  loading_owner,
                  refii::vfs::LoadGuestU32(base, loading_owner + 168),
                  finish_event,
                  static_cast<uint32_t>(original_ctx.lr),
                  refii::vfs::FormatRefiiLoadingScreenOwnerState(
                      base, loading_owner),
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    }
  }

  if ((refii_forced_native_finish || refii::vfs::VfsTracingEnabled()) &&
      base != nullptr && refii::vfs::LooksLikeGuestPointer(loading_owner) &&
      refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted() &&
      (refii_forced_native_finish || refii_just_completed_finish)) {
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "loading_owner_tick",
        fmt::format("owner=0x{:08X} frame={} finish_requested={} "
                    "finish_completed={} fade176=0x{:02X} done177=0x{:02X} "
                    "pending308=0x{:02X} lr=0x{:08X}",
                    loading_owner,
                    refii::vfs::LoadGuestU32(base, loading_owner + 168),
                    refii_forced_native_finish ? "true" : "false",
                    refii_completed_finish ? "true" : "false",
                    refii::vfs::LoadGuestU8(base, loading_owner + 176),
                    refii::vfs::LoadGuestU8(base, loading_owner + 177),
                    refii::vfs::LoadGuestU8(base, loading_owner + 308),
                    static_cast<uint32_t>(original_ctx.lr)));
    REXLOG_INFO("Refii VFS loading owner tick exit "
                "owner=0x{:08X} finish_requested={} finish_completed={} "
                "result=0x{:08X} after=[{}] handoff=[{}] main_menu=[{}]",
                loading_owner,
                refii_forced_native_finish ? "true" : "false",
                refii_completed_finish ? "true" : "false",
                ctx.r3.u32,
                refii::vfs::FormatRefiiLoadingScreenOwnerState(
                    base, loading_owner),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

PPC_FUNC_IMPL(sub_82361AF8) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t holder = original_ctx.r3.u32;
  uint32_t owner_before = 0;
  if (base != nullptr && refii::vfs::LooksLikeGuestPointer(holder)) {
    owner_before = refii::vfs::LoadGuestU32(base, holder + 4);
  }

  __imp__sub_82361AF8(ctx, base);

  if (base == nullptr || !refii::vfs::LooksLikeGuestPointer(holder) ||
      !refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted()) {
    return;
  }

  const uint32_t completed_owner =
      refii::vfs::RefiiCompletedLoadingScreenOwner().load(
          std::memory_order_relaxed);
  const uint32_t owner_after = refii::vfs::LoadGuestU32(base, holder + 4);
  if (completed_owner == 0 || owner_after != completed_owner) {
    return;
  }

  static std::atomic<uint32_t> last_logged_holder{0};
  if (last_logged_holder.exchange(holder, std::memory_order_relaxed) == holder) {
    return;
  }

  const bool collapse_finished_holder =
      (refii::vfs::RefiiPostFrontendDeferredHashWakeSignalEmitted() ||
       refii::vfs::RefiiPostVfsChainReadyForLoadingOwnerRelease()) &&
      refii::vfs::RefiiPostVideoFrontendChainConsumed();
  bool post_loading_native_run = false;
  bool generated_state_released = false;
  if (collapse_finished_holder) {
    post_loading_native_run =
        refii::vfs::TryRunRefiiPostLoadingFrontendRenderRequestNativeFromTrace(
            original_ctx, base, "loading_owner_holder_collapsed");
    generated_state_released =
        refii::vfs::TryFinalizeRefiiPostLoadingFrontendGeneratedState(
            original_ctx,
            base,
            "loading_owner_holder_collapsed",
            true);
  }

  refii::vfs::RecordRefiiMainMenuBlockerEvent(
      collapse_finished_holder ? "loading_owner_holder_collapsed"
                               : "loading_owner_holder_retained",
      fmt::format("holder=0x{:08X} owner=0x{:08X} before=0x{:08X} "
                  "after=0x{:08X} lr=0x{:08X} reason={} "
                  "post_loading_native_run={} generated_state_released={}",
                  holder,
                  owner_after,
                  owner_before,
                  refii::vfs::LoadGuestU32(base, holder + 4),
                  static_cast<uint32_t>(original_ctx.lr),
                  collapse_finished_holder
                      ? "refii_vfs_completed_handoff_chain_owner_retained"
                      : "native_sub_82361AF8_keeps_owner",
                  post_loading_native_run ? "true" : "false",
                  generated_state_released ? "true" : "false"));
  if (refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS loading owner holder {} "
                "holder=0x{:08X} owner=0x{:08X} before=0x{:08X} "
                "after=0x{:08X} lr=0x{:08X} reason={} "
                "post_loading_native_run={} generated_state_released={} "
                "handoff=[{}] main_menu=[{}]",
                collapse_finished_holder ? "collapsed" : "retained",
                holder,
                owner_after,
                owner_before,
                refii::vfs::LoadGuestU32(base, holder + 4),
                static_cast<uint32_t>(original_ctx.lr),
                collapse_finished_holder
                    ? "refii_vfs_completed_handoff_chain_owner_retained"
                    : "native_sub_82361AF8_keeps_owner",
                post_loading_native_run ? "true" : "false",
                generated_state_released ? "true" : "false",
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

PPC_FUNC_IMPL(sub_82180418) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const bool trace = refii::vfs::VfsTracingEnabled() &&
                     refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted() &&
                     refii::vfs::ShouldLogRepeatedVfsTraceOperation(
                         "PostLoadingAfterHolder_82180418", 16);
  if (trace) {
    REXLOG_INFO("VFS trace post-loading after-holder enter sub_82180418 "
                "lr=0x{:08X} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82180418(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace post-loading after-holder exit sub_82180418 "
                "lr=0x{:08X} result=0x{:08X} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

PPC_FUNC_IMPL(sub_82467B08) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  if (refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted()) {
    refii::vfs::RecordRefiiPostLoadingAfterHolderRenderOwner(
        original_ctx.r4.u32);
  }
  const bool trace = refii::vfs::VfsTracingEnabled() &&
                     refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted() &&
                     refii::vfs::ShouldLogRepeatedVfsTraceOperation(
                         "PostLoadingAfterHolder_82467B08", 16);
  if (trace) {
    REXLOG_INFO("VFS trace post-loading after-holder enter sub_82467B08 "
                "lr=0x{:08X} object=0x{:08X} arg=0x{:08X} "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  constexpr uint32_t kAfterHolderSingleton = 0x834C20B8u;
  bool after_holder_armed = false;
  bool after_holder_ready_marked = false;
  bool after_holder_advanced = false;
  bool after_holder_completed_owner_collapsed = false;
  bool after_holder_mode2_collapsed = false;
  bool after_holder_terminal_collapsed = false;
  if (refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted()) {
    PPCContext holder_ctx = original_ctx;
    holder_ctx.r3.u32 = kAfterHolderSingleton;
    holder_ctx.r4.u32 = original_ctx.r4.u32;
    const uint32_t after_holder_mode =
        refii::vfs::LooksLikeGuestPointer(kAfterHolderSingleton)
            ? refii::vfs::LoadGuestU32(base, kAfterHolderSingleton + 52)
            : 0xFFFFFFFFu;
    const uint32_t after_holder_child =
        refii::vfs::LooksLikeGuestPointer(kAfterHolderSingleton)
            ? refii::vfs::LoadGuestU32(base, kAfterHolderSingleton + 40)
            : 0;
    const bool post_loading_final_handoff_ready =
        refii::vfs::RefiiPostLoadingFrontendFinalHandoffWakeEmitted() &&
        refii::vfs::RefiiPostGameFacePackageComplete() &&
        refii::vfs::
            RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
    const bool after_holder_release_ready =
        refii::vfs::RefiiPostVfsChainReadyForLoadingOwnerRelease() ||
        refii::vfs::RefiiOwnerlessPostVideoVfsChainReadyForAfterHolder() ||
        post_loading_final_handoff_ready;
    after_holder_terminal_collapsed =
        refii::vfs::RefiiStartupVideosReadyForMenuHandoff() &&
        (refii::vfs::RefiiPostVideoFrontendChainConsumed() ||
         refii::vfs::
             RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload()) &&
        after_holder_release_ready &&
        original_ctx.r3.u32 == kAfterHolderSingleton &&
        after_holder_mode == 0 && after_holder_child == 0;
    if (after_holder_terminal_collapsed) {
      refii::vfs::RecordRefiiMainMenuBlockerEvent(
          "post_loading_after_holder_terminal_collapsed",
          fmt::format("source=sub_82467B08_enter_before_native "
                      "holder=0x{:08X} arg=0x{:08X} state=[{}]",
                      kAfterHolderSingleton,
                      original_ctx.r4.u32,
                      refii::vfs::FormatRefiiPostLoadingAfterHolderState(
                          base, kAfterHolderSingleton)));
      refii::vfs::LogRefiiVfsChainVerdict(
          "frontend_post_video",
          static_cast<uint32_t>(original_ctx.lr),
          "post_loading_after_holder",
          "",
          "",
          "native_after_holder",
          "post_loading_after_holder_terminal_collapsed",
          "Opened",
          "terminal_holder_satisfied_by_vfs");
    }
    after_holder_armed =
        !after_holder_terminal_collapsed &&
        refii::vfs::TryArmRefiiPostLoadingAfterHolderFromCompletedVfsChain(
            holder_ctx,
            base,
            kAfterHolderSingleton,
            "sub_82467B08_enter_before_native");
    after_holder_mode2_collapsed =
        !after_holder_terminal_collapsed &&
        (after_holder_armed ||
         refii::vfs::LoadGuestU32(base, kAfterHolderSingleton + 52) == 2) &&
        refii::vfs::TryCollapseRefiiPostLoadingAfterHolderMode2(
            holder_ctx,
            base,
            kAfterHolderSingleton,
            "sub_82467B08_enter_before_native");
    after_holder_ready_marked =
        !after_holder_mode2_collapsed &&
        refii::vfs::TryMarkRefiiPostLoadingAfterHolderReady(
            holder_ctx,
            base,
            kAfterHolderSingleton,
            "sub_82467B08_enter_before_native");
    after_holder_advanced =
        !after_holder_ready_marked &&
        refii::vfs::TryAdvanceRefiiPostLoadingAfterHolderMode4(
            holder_ctx,
            base,
            kAfterHolderSingleton,
            "sub_82467B08_enter_before_native");
    after_holder_completed_owner_collapsed =
        !after_holder_ready_marked && !after_holder_advanced &&
        refii::vfs::
            TryCompleteRefiiPostLoadingAfterHolderForCompletedOwner(
                holder_ctx,
                base,
                kAfterHolderSingleton,
                "sub_82467B08_enter_completed_owner");
  }

  const bool skip_native_after_holder =
      after_holder_terminal_collapsed ||
      after_holder_mode2_collapsed || after_holder_ready_marked ||
      after_holder_advanced ||
      after_holder_completed_owner_collapsed;
  if (skip_native_after_holder) {
    ctx.r3.u32 = 0;
  } else {
    __imp__sub_82467B08(ctx, base);

    if (refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted()) {
      PPCContext holder_ctx = original_ctx;
      holder_ctx.r3.u32 = kAfterHolderSingleton;
      holder_ctx.r4.u32 = original_ctx.r4.u32;
      after_holder_mode2_collapsed =
          refii::vfs::TryCollapseRefiiPostLoadingAfterHolderMode2(
              holder_ctx,
              base,
              kAfterHolderSingleton,
              "sub_82467B08_exit_before_822C6C68");
      after_holder_ready_marked =
          !after_holder_mode2_collapsed &&
          refii::vfs::TryMarkRefiiPostLoadingAfterHolderReady(
              holder_ctx,
              base,
              kAfterHolderSingleton,
              "sub_82467B08_exit_before_822C6C68");
      after_holder_advanced =
          !after_holder_ready_marked &&
          refii::vfs::TryAdvanceRefiiPostLoadingAfterHolderMode4(
              holder_ctx,
              base,
              kAfterHolderSingleton,
              "sub_82467B08_exit_before_822C6C68");
      after_holder_completed_owner_collapsed =
          !after_holder_ready_marked && !after_holder_advanced &&
          refii::vfs::
              TryCompleteRefiiPostLoadingAfterHolderForCompletedOwner(
                  holder_ctx,
                  base,
                  kAfterHolderSingleton,
                  "sub_82467B08_exit_completed_owner");
    }
  }

  if (trace) {
    REXLOG_INFO("VFS trace post-loading after-holder exit sub_82467B08 "
                "lr=0x{:08X} result=0x{:08X} "
                "terminal_collapsed={} armed={} mode2_collapsed={} "
                "ready_marked={} advanced={} "
                "completed_owner_collapsed={} native_skipped={} holder=[{}] "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                after_holder_terminal_collapsed ? "true" : "false",
                after_holder_armed ? "true" : "false",
                after_holder_mode2_collapsed ? "true" : "false",
                after_holder_ready_marked ? "true" : "false",
                after_holder_advanced ? "true" : "false",
                after_holder_completed_owner_collapsed ? "true" : "false",
                skip_native_after_holder ? "true" : "false",
                refii::vfs::FormatRefiiPostLoadingAfterHolderState(
                    base, 0x834C20B8u),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

PPC_FUNC_IMPL(sub_822C6C68) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const bool trace = refii::vfs::VfsTracingEnabled() &&
                     refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted() &&
                     refii::vfs::ShouldLogRepeatedVfsTraceOperation(
                         "PostLoadingAfterHolder_822C6C68", 16);
  if (trace) {
    REXLOG_INFO("VFS trace post-loading after-holder enter sub_822C6C68 "
                "lr=0x{:08X} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  uint32_t refii_holder_guest = 0;
  const bool refii_singleton_returned =
      refii::vfs::
          TryReturnRefiiPostLoadingAfterHolderSingletonForCompletedVfsChain(
              original_ctx,
              base,
              &refii_holder_guest,
              "sub_822C6C68_pre_native_singleton");
  if (refii_singleton_returned) {
    ctx.r3.u32 = refii_holder_guest;
  } else {
    __imp__sub_822C6C68(ctx, base);
  }

  bool early_armed = false;
  bool early_mode2_collapsed = false;
  bool early_ready_marked = false;
  if (refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted()) {
    PPCContext holder_ctx = original_ctx;
    holder_ctx.r3.u32 = ctx.r3.u32;
    holder_ctx.r4.u32 =
        refii::vfs::SelectRefiiPostLoadingAfterHolderRenderOwner();
    early_armed =
        refii::vfs::TryArmRefiiPostLoadingAfterHolderFromCompletedVfsChain(
            holder_ctx,
            base,
            ctx.r3.u32,
            "sub_822C6C68_exit_before_823C1658");
    early_mode2_collapsed =
        early_armed &&
        refii::vfs::TryCollapseRefiiPostLoadingAfterHolderMode2(
            holder_ctx,
            base,
            ctx.r3.u32,
            "sub_822C6C68_exit_after_arm");
    early_ready_marked =
        !early_mode2_collapsed &&
        refii::vfs::TryMarkRefiiPostLoadingAfterHolderReady(
            holder_ctx,
            base,
            ctx.r3.u32,
            "sub_822C6C68_exit_before_823C1658");
  }

  if (trace) {
    REXLOG_INFO("VFS trace post-loading after-holder exit sub_822C6C68 "
                "lr=0x{:08X} result=0x{:08X} early_armed={} "
                "early_mode2_collapsed={} early_ready_marked={} "
                "refii_singleton_returned={} "
                "holder=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                early_armed ? "true" : "false",
                early_mode2_collapsed ? "true" : "false",
                early_ready_marked ? "true" : "false",
                refii_singleton_returned ? "true" : "false",
                refii::vfs::FormatRefiiPostLoadingAfterHolderState(
                    base, ctx.r3.u32),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

PPC_FUNC_IMPL(sub_823C1658) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const bool trace = refii::vfs::VfsTracingEnabled() &&
                     refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted() &&
                     refii::vfs::ShouldLogRepeatedVfsTraceOperation(
                         "PostLoadingAfterHolder_823C1658", 16);
  std::string before_holder_state;
  if (trace) {
    before_holder_state =
        refii::vfs::FormatRefiiPostLoadingAfterHolderState(
            base, original_ctx.r3.u32);
  }
  bool after_holder_armed =
      refii::vfs::TryArmRefiiPostLoadingAfterHolderFromCompletedVfsChain(
          original_ctx, base, original_ctx.r3.u32, "sub_823C1658_enter");
  bool after_holder_mode2_collapsed =
      refii::vfs::TryCollapseRefiiPostLoadingAfterHolderMode2(
          original_ctx, base, original_ctx.r3.u32, "sub_823C1658_enter");
  bool after_holder_ready_marked =
      !after_holder_mode2_collapsed &&
      refii::vfs::TryMarkRefiiPostLoadingAfterHolderReady(
          original_ctx, base, original_ctx.r3.u32, "sub_823C1658_enter");
  bool after_holder_advanced =
      !after_holder_mode2_collapsed && !after_holder_ready_marked &&
      refii::vfs::TryAdvanceRefiiPostLoadingAfterHolderMode4(
          original_ctx, base, original_ctx.r3.u32, "sub_823C1658_enter");
  if (trace) {
    REXLOG_INFO("VFS trace post-loading after-holder enter sub_823C1658 "
                "lr=0x{:08X} object=0x{:08X} arg=0x{:08X} delta={} "
                "armed={} mode2_collapsed={} ready_marked={} advanced={} "
                "holder=[{}] "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                original_ctx.f1.f64,
                after_holder_armed ? "true" : "false",
                after_holder_mode2_collapsed ? "true" : "false",
                after_holder_ready_marked ? "true" : "false",
                after_holder_advanced ? "true" : "false",
                before_holder_state,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  if (after_holder_mode2_collapsed) {
    ctx.r3.u32 = original_ctx.r3.u32 + 48;
  } else {
    __imp__sub_823C1658(ctx, base);
  }

  bool deferred_generated_run = false;
  if (refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted()) {
    PPCContext render_thread_ctx = original_ctx;
    deferred_generated_run =
        refii::vfs::TryRunRefiiDeferredPostLoadingFrontendRequestOnRenderThread(
            render_thread_ctx, base, "post_loading_after_holder_823C1658");
    if (!after_holder_mode2_collapsed && !after_holder_ready_marked &&
        !after_holder_advanced) {
      after_holder_ready_marked =
          refii::vfs::TryMarkRefiiPostLoadingAfterHolderReady(
              original_ctx,
              base,
              original_ctx.r3.u32,
              "sub_823C1658_exit_after_deferred");
      after_holder_advanced =
          refii::vfs::TryAdvanceRefiiPostLoadingAfterHolderMode4(
              original_ctx,
              base,
              original_ctx.r3.u32,
              after_holder_ready_marked
                  ? "sub_823C1658_exit_after_ready_marked"
                  : "sub_823C1658_exit_after_deferred");
    }
  }

  if (trace) {
    REXLOG_INFO("VFS trace post-loading after-holder exit sub_823C1658 "
                "lr=0x{:08X} result=0x{:08X} armed={} "
                "mode2_collapsed={} ready_marked={} advanced={} "
                "deferred_generated_run={} holder=[{}] "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                after_holder_armed ? "true" : "false",
                after_holder_mode2_collapsed ? "true" : "false",
                after_holder_ready_marked ? "true" : "false",
                after_holder_advanced ? "true" : "false",
                deferred_generated_run ? "true" : "false",
                refii::vfs::FormatRefiiPostLoadingAfterHolderState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

PPC_FUNC_IMPL(sub_82360300) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t loading_owner = original_ctx.r3.u32;
  refii::vfs::RecordRefiiLoadingScreenOwner(base,
                                                loading_owner,
                                                "sub_82360300");
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogLooseStreamOperation(
          "LoadingAnimationFrame_82360300", loading_owner, 16);
  std::string before_words;
  uint32_t before_frame = 0;
  uint32_t before_frame_count = 0;
  uint32_t before_sequence_count = 0;
  uint32_t before_animation_count = 0;
  uint8_t before_request_pending = 0;
  uint8_t before_sequence_ready = 0;
  uint8_t before_fx_ready = 0;
  uint8_t before_intro_done = 0;
  uint8_t before_scheduled = 0;
  if (trace && base != nullptr &&
      refii::vfs::LooksLikeGuestPointer(loading_owner)) {
    before_words =
        refii::vfs::FormatGuestWordsIfPointer(base, loading_owner, 104);
    before_frame = refii::vfs::LoadGuestU32(base, loading_owner + 168);
    before_frame_count =
        refii::vfs::LoadGuestU32(base, loading_owner + 300);
    before_sequence_count =
        refii::vfs::LoadGuestU32(base, loading_owner + 388);
    before_animation_count =
        refii::vfs::LoadGuestU32(base, loading_owner + 392);
    before_request_pending =
        refii::vfs::LoadGuestU8(base, loading_owner + 308);
    before_sequence_ready =
        refii::vfs::LoadGuestU8(base, loading_owner + 313);
    before_fx_ready =
        refii::vfs::LoadGuestU8(base, loading_owner + 314);
    before_intro_done =
        refii::vfs::LoadGuestU8(base, loading_owner + 320);
    before_scheduled =
        refii::vfs::LoadGuestU8(base, loading_owner + 322);
  }

  __imp__sub_82360300(ctx, base);

  uint32_t refii_loading_sequence_handle = 0;
  bool refii_loading_intro_primed = false;
  if (base != nullptr && refii::vfs::LooksLikeGuestPointer(loading_owner) &&
      refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted() &&
      refii::vfs::RefiiLoadingScreenSequenceReady(
          base, loading_owner, &refii_loading_sequence_handle) &&
      refii::vfs::LoadGuestU8(base, loading_owner + 320) == 0) {
    refii::vfs::StoreGuestU8(base, loading_owner + 320, 1);
    ctx.r3.u32 = 1;
    refii_loading_intro_primed = true;
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "loading_animation_intro_primed",
        fmt::format("owner=0x{:08X} sequence_handle=0x{:08X} lr=0x{:08X}",
                    loading_owner,
                    refii_loading_sequence_handle,
                    static_cast<uint32_t>(original_ctx.lr)));
    if (refii::vfs::VfsTracingEnabled()) {
      REXLOG_INFO("Refii VFS loading animation intro primed "
                  "owner=0x{:08X} sequence_handle=0x{:08X} lr=0x{:08X} "
                  "state=[{}] handoff=[{}] main_menu=[{}]",
                  loading_owner,
                  refii_loading_sequence_handle,
                  static_cast<uint32_t>(original_ctx.lr),
                  refii::vfs::FormatRefiiLoadingScreenOwnerState(
                      base, loading_owner),
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    }
  }

  if (trace && base != nullptr &&
      refii::vfs::LooksLikeGuestPointer(loading_owner)) {
    const uint32_t after_frame =
        refii::vfs::LoadGuestU32(base, loading_owner + 168);
    const uint32_t after_frame_count =
        refii::vfs::LoadGuestU32(base, loading_owner + 300);
    const uint32_t after_sequence_count =
        refii::vfs::LoadGuestU32(base, loading_owner + 388);
    const uint32_t after_animation_count =
        refii::vfs::LoadGuestU32(base, loading_owner + 392);
    const uint8_t after_request_pending =
        refii::vfs::LoadGuestU8(base, loading_owner + 308);
    const uint8_t after_sequence_ready =
        refii::vfs::LoadGuestU8(base, loading_owner + 313);
    const uint8_t after_fx_ready =
        refii::vfs::LoadGuestU8(base, loading_owner + 314);
    const uint8_t after_intro_done =
        refii::vfs::LoadGuestU8(base, loading_owner + 320);
    const uint8_t after_scheduled =
        refii::vfs::LoadGuestU8(base, loading_owner + 322);
    const std::string summary = fmt::format(
        "sub_82360300 lr=0x{:08X} owner=0x{:08X} result=0x{:08X} "
        "frame={}->{} frame_count={}->{} sequence_count={}->{} "
        "animation_count={}->{} pending308=0x{:02X}->0x{:02X} "
        "sequence313=0x{:02X}->0x{:02X} fx314=0x{:02X}->0x{:02X} "
        "intro320=0x{:02X}->0x{:02X} scheduled322=0x{:02X}->0x{:02X} "
        "refii_intro_primed={} sequence_handle=0x{:08X}",
        static_cast<uint32_t>(original_ctx.lr),
        loading_owner,
        ctx.r3.u32,
        before_frame,
        after_frame,
        before_frame_count,
        after_frame_count,
        before_sequence_count,
        after_sequence_count,
        before_animation_count,
        after_animation_count,
        before_request_pending,
        after_request_pending,
        before_sequence_ready,
        after_sequence_ready,
        before_fx_ready,
        after_fx_ready,
        before_intro_done,
        after_intro_done,
        before_scheduled,
        after_scheduled,
        refii_loading_intro_primed ? "true" : "false",
        refii_loading_sequence_handle);
    refii::vfs::RecordRefiiMainMenuBlockerEvent("loading_frame", summary);
    REXLOG_INFO("VFS trace loading animation frame {} before_words={} "
                "after_words={} handoff=[{}] main_menu=[{}]",
                summary,
                before_words,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, loading_owner, 104),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

PPC_FUNC_IMPL(rex_lhResourceName_ComputeHash) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t resource_name_guest = original_ctx.r3.u32;
  std::string name;
  if (base != nullptr && resource_name_guest != 0) {
    const uint32_t string_owner =
        refii::vfs::LoadGuestU32(base, resource_name_guest);
    const uint32_t string_guest =
        string_owner == 0 ? 0 : refii::vfs::LoadGuestU32(base, string_owner);
    if (string_guest != 0) {
      name = refii::vfs::ReadBestEffortGuestString(base, string_guest, 4096);
    }
    if (name.empty()) {
      name = refii::vfs::ReadGuestWideStringObject(
          base, resource_name_guest, 4096);
    }
  }

  __imp__rex_lhResourceName_ComputeHash(ctx, base);

  if (!name.empty()) {
    refii::vfs::RecordRuntimeResourceHashName(ctx.r3.u32, name);
    if (refii::vfs::IsTextureAssetCandidate(name)) {
      refii::vfs::RegisterRefiiOwnedTextureHashContribution(
          ctx,
          base,
          ctx.r3.u32,
          name,
          static_cast<uint32_t>(original_ctx.lr));
    } else {
      refii::vfs::RegisterRefiiOwnedResourceHashContribution(
          ctx,
          base,
          ctx.r3.u32,
          name,
          static_cast<uint32_t>(original_ctx.lr));
    }
  }

  if (refii::vfs::VfsTracingEnabled() && !name.empty() &&
      refii::vfs::IsMainMenuTextureHashTraceAsset(name)) {
    REXLOG_INFO("VFS trace ResourceName outer hash lr=0x{:08X} "
                "name_obj=0x{:08X} owner=0x{:08X} name={} hash=0x{:08X} "
                "expected=0x{:08X}",
                original_ctx.lr,
                resource_name_guest,
                resource_name_guest == 0
                    ? 0
                    : refii::vfs::LoadGuestU32(base, resource_name_guest),
                refii::vfs::NormalizeLooseAssetKey(name),
                ctx.r3.u32,
                refii::vfs::ComputeResourceNameHash(name));
  }
}

PPC_FUNC_IMPL(rex_lhResourceName_ComputeHashFromName) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t name_guest = original_ctx.r3.u32;
  const std::string name =
      refii::vfs::ReadGuestWideStringObject(base, name_guest, 4096);

  __imp__rex_lhResourceName_ComputeHashFromName(ctx, base);

  const bool called_from_resource_name_wrapper =
      original_ctx.lr == 0x82C0382C;
  if (!name.empty() && !called_from_resource_name_wrapper) {
    refii::vfs::RecordRuntimeResourceHashName(ctx.r3.u32, name);
    if (refii::vfs::IsTextureAssetCandidate(name)) {
      refii::vfs::RegisterRefiiOwnedTextureHashContribution(
          ctx,
          base,
          ctx.r3.u32,
          name,
          static_cast<uint32_t>(original_ctx.lr));
    } else {
      refii::vfs::RegisterRefiiOwnedResourceHashContribution(
          ctx,
          base,
          ctx.r3.u32,
          name,
          static_cast<uint32_t>(original_ctx.lr));
    }
  }

  const bool trace_interesting_hash =
      refii::vfs::IsTraceInterestingAsset(name) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "ResourceName_Interesting", 16);
  if (!called_from_resource_name_wrapper &&
      refii::vfs::IsMainMenuTextureHashTraceAsset(name)) {
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "texture_hash",
        fmt::format("ResourceNameHash lr=0x{:08X} name={} hash=0x{:08X}",
                    original_ctx.lr,
                    refii::vfs::NormalizeLooseAssetKey(name),
                    ctx.r3.u32));
  }
  if (!called_from_resource_name_wrapper &&
      refii::vfs::VfsTracingEnabled() && !name.empty() &&
      (trace_interesting_hash ||
       refii::vfs::ShouldLogVfsTraceOperation(
           "ResourceName_ComputeHashFromName"))) {
    REXLOG_INFO("VFS trace ResourceName hash lr=0x{:08X} name_obj=0x{:08X} "
                "name={} hash=0x{:08X} expected=0x{:08X}",
                original_ctx.lr,
                name_guest,
                name,
                ctx.r3.u32,
                refii::vfs::ComputeResourceNameHash(name));
  }
}

PPC_FUNC_IMPL(sub_82374728) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t owner_guest = original_ctx.r3.u32;
  const uint32_t requested_name_guest = original_ctx.r4.u32;
  const uint32_t resolved_name_guest = original_ctx.r5.u32;
  const std::string requested_name =
      refii::vfs::ReadGuestWideStringObject(base, requested_name_guest, 4096);
  const std::string resolved_name =
      refii::vfs::ReadGuestWideStringObject(base, resolved_name_guest, 4096);
  const uint32_t requested_hash =
      refii::vfs::ComputeResourceNameHash(requested_name);
  const uint32_t resolved_hash =
      refii::vfs::ComputeResourceNameHash(resolved_name);
  const std::string requested_key =
      refii::vfs::NormalizeLooseAssetKey(requested_name);
  const std::string resolved_key =
      refii::vfs::NormalizeLooseAssetKey(resolved_name);
  const bool localized_video_alias =
      requested_key.starts_with("language/videos/") &&
      (resolved_key.starts_with("language/") ||
       resolved_key.starts_with("language_dlc/")) &&
      resolved_key.find("/videos/") != std::string::npos &&
      resolved_key.ends_with(".bik");
  if (!requested_name.empty()) {
    refii::vfs::RecordRuntimeResourceHashName(requested_hash,
                                                  requested_name);
  }
  if (!resolved_name.empty() && !localized_video_alias) {
    refii::vfs::RecordRuntimeResourceHashName(resolved_hash,
                                                  resolved_name);
  }
  const bool interesting =
      refii::vfs::VfsTracingEnabled() &&
      !localized_video_alias &&
      (refii::vfs::IsTraceInterestingAsset(requested_name) ||
       refii::vfs::IsTraceInterestingAsset(resolved_name) ||
       refii::vfs::ShouldLogVfsTraceOperation("ResourceAliasResolver"));

  if (interesting) {
    const uint32_t primary_resource = owner_guest == 0
                                          ? 0
                                          : refii::vfs::LoadGuestU32(
                                                base, owner_guest + 24);
    REXLOG_INFO("VFS trace ResourceAliasResolver enter lr=0x{:08X} "
                "owner={} requested={} requested_hash=0x{:08X} "
                "resolved={} resolved_hash=0x{:08X} owner_words={} "
                "primary_resource={} requested_words={} resolved_words={}",
                original_ctx.lr,
                refii::vfs::DescribeGuestObject(base, owner_guest, 12, 12),
                requested_name,
                requested_hash,
                resolved_name,
                resolved_hash,
                refii::vfs::FormatGuestWords(base, owner_guest, 16),
                refii::vfs::DescribeGuestObject(
                    base, primary_resource, 16, 12),
                refii::vfs::FormatGuestWords(base, requested_name_guest, 8),
                refii::vfs::FormatGuestWords(base, resolved_name_guest, 8));
  }

  if (refii::vfs::RefiiLooseAssetBackendEnabled() &&
      localized_video_alias) {
    refii::vfs::LooseAssetResolution resolution;
    std::string candidate;
    // Localized video aliases are emitted for every known locale/DLC variant.
    // Only attach variants that truly exist as loose files. Falling back to the
    // English video here makes the resource table believe all localized videos
    // are resident, which later confuses the boot resource state machine.
    bool resolved = refii::vfs::ResolveLooseAssetOverride(
        resolved_key, &resolution);
    if (resolved) {
      const std::string provider_key =
          refii::vfs::NormalizeLooseAssetKey(
              resolution.provider_relative_path);
      if (provider_key == resolved_key) {
        candidate = resolved_key;
      } else {
        resolved = false;
      }
    }
    if (resolved) {
      refii::vfs::RecordRuntimeResourceHashName(resolved_hash,
                                                    resolved_name);
      refii::vfs::RegisterTargetedRequestManagerHashContribution(
          resolved_hash,
          resolved_key,
          "localized_video_alias",
          candidate,
          resolution.provider_relative_path,
          "localized_video");
      if (requested_hash != 0 && requested_hash != resolved_hash &&
          !requested_key.empty()) {
        refii::vfs::RegisterTargetedRequestManagerHashContribution(
            requested_hash,
            requested_key,
            "localized_video_requested_alias",
            candidate,
            resolution.provider_relative_path,
            "localized_video");
      }
      const bool name_side_effects =
          refii::vfs::ApplyResourceAliasNameSideEffects(
              ctx,
              base,
              owner_guest,
              resolved_name_guest,
              requested_hash);
      const refii::vfs::ResourceAliasValidationResult validation =
          refii::vfs::ValidateSyntheticResourceAliasAttach(
              ctx, base, owner_guest, requested_hash);
      const bool attach_side_effects =
          validation.attach &&
          refii::vfs::ApplySyntheticResourceAliasAttach(
              ctx, base, owner_guest, requested_name_guest);
      const uint32_t primary_resource =
          owner_guest == 0
              ? 0
              : refii::vfs::LoadGuestU32(base, owner_guest + 24);
      if (primary_resource != 0) {
        refii::vfs::RecordKnownRequestManagerGuest(
            primary_resource, "ResourceAliasResolver_LocalizedVideo");
        PPCContext augment_ctx = ctx;
        refii::vfs::EnsureRefiiBootBankTemplates(
            augment_ctx, base, "ResourceAliasResolver_LocalizedVideo");
        refii::vfs::AugmentRequestManagerHashTable(
            augment_ctx,
            base,
            primary_resource,
            "ResourceAliasResolver_LocalizedVideo");
      }
      refii::vfs::RecordRefiiLastVfsAliasOperation(
          "ResourceAliasResolver_LocalizedVideo",
          requested_name,
          resolved_name,
          requested_hash,
          resolved_hash,
          primary_resource,
          "localized_video");
      const bool log_localized_video_hit =
          refii::vfs::LooseAssetOverrideTracingEnabled() ||
          refii::vfs::ShouldLogRepeatedVfsTraceOperation(
              "ResourceAliasResolver_LocalizedVideoHit", 8);
      if (log_localized_video_hit) {
        REXLOG_INFO("VFS trace ResourceAliasResolver resolved localized "
                    "video alias requested={} resolved={} hash=0x{:08X} "
                    "candidate={} provider_relative={} host={} "
                    "native_side_effects=synthetic name={} attach={} "
                    "validation=0x{:08X}/0x{:08X}->0x{:08X}/0x{:08X} "
                    "primary=0x{:08X} targeted_hashes={}",
                    requested_name,
                    resolved_name,
                    resolved_hash,
                    candidate,
                    resolution.provider_relative_path,
                    resolution.host_path.string(),
                    name_side_effects ? "ok" : "skipped",
                    attach_side_effects
                        ? "ok"
                        : (validation.ran && !validation.attach
                               ? "validation_skip"
                               : "skipped"),
                    validation.result_container,
                    validation.result_node,
                    validation.expected_container,
                    validation.expected_node,
                    primary_resource,
                    refii::vfs::TargetedRequestManagerHashContributionCount());
      }
      ctx.r3.u32 = owner_guest;
      return;
    }

    ctx.r3.u32 = owner_guest;
    refii::vfs::IsCachedOptionalSyntheticVfsMiss(resolved_key);
    const bool log_localized_video_miss =
        refii::vfs::LooseAssetOverrideTracingEnabled() ||
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "ResourceAliasResolver_LocalizedVideoMiss", 8);
    if (log_localized_video_miss) {
      REXLOG_INFO("VFS trace ResourceAliasResolver skipped missing localized "
                  "video alias requested={} resolved={} hash=0x{:08X}",
                  requested_name,
                  resolved_name,
                  resolved_hash);
    }
    return;
  }

  if (refii::vfs::RefiiLooseAssetBackendEnabled() &&
      !localized_video_alias) {
    const bool resolved_optional_miss =
        refii::vfs::IsCachedOptionalSyntheticVfsMiss(resolved_key);
    const bool requested_optional_miss =
        refii::vfs::IsCachedOptionalSyntheticVfsMiss(requested_key);
    if (resolved_optional_miss || requested_optional_miss) {
      ctx.r3.u32 = owner_guest;
      if (interesting || refii::vfs::LooseAssetOverrideTracingEnabled() ||
          refii::vfs::ShouldLogRepeatedVfsTraceOperation(
              "ResourceAliasResolverOptionalBankMiss", 16)) {
        REXLOG_INFO("VFS trace ResourceAliasResolver skipped optional "
                    "synthetic bank alias requested={} resolved={} "
                    "requested_hash=0x{:08X} resolved_hash=0x{:08X} "
                    "requested_optional={} resolved_optional={} "
                    "ownership=optional_miss",
                    requested_name,
                    resolved_name,
                    requested_hash,
                    resolved_hash,
                    requested_optional_miss,
                    resolved_optional_miss);
      }
      return;
    }

    const std::string requested_boot_locale_bank_alias =
        RefiiBootLocaleBankAliasForRequested(requested_key);
    if (!requested_boot_locale_bank_alias.empty()) {
      refii::vfs::LooseAssetResolution boot_bank_resolution;
      std::string boot_bank_candidate;
      if (refii::vfs::ResolveExactTypedBankManifestLooseAsset(
              requested_boot_locale_bank_alias,
              &boot_bank_resolution,
              &boot_bank_candidate)) {
        refii::vfs::RegisterTargetedRequestManagerHashContribution(
            requested_hash,
            requested_key,
            "requested_boot_locale_bank_alias",
            boot_bank_candidate,
            boot_bank_resolution.provider_relative_path,
            "requested_boot_locale_bank");
        refii::vfs::RegisterResolvedSyntheticBankOpenAlias(
            requested_key,
            requested_boot_locale_bank_alias,
            "resource_alias_requested_boot_locale");
        refii::vfs::RegisterResolvedSyntheticBankOpenAlias(
            std::filesystem::path(requested_key).filename().generic_string(),
            requested_boot_locale_bank_alias,
            "resource_alias_boot_locale_basename");

        const uint32_t primary_resource =
            owner_guest == 0
                ? 0
                : refii::vfs::LoadGuestU32(base, owner_guest + 24);
        if (primary_resource != 0) {
          refii::vfs::RecordKnownRequestManagerGuest(
              primary_resource, "ResourceAliasResolver_BootLocaleBank");
          PPCContext augment_ctx = ctx;
          refii::vfs::EnsureRefiiBootBankTemplates(
              augment_ctx, base, "ResourceAliasResolver_BootLocaleBank");
          refii::vfs::AugmentRequestManagerHashTable(
              augment_ctx,
              base,
              primary_resource,
              "ResourceAliasResolver_BootLocaleBank");
        }
        refii::vfs::RecordRefiiLastVfsAliasOperation(
            "ResourceAliasResolver_BootLocaleBank",
            requested_name,
            requested_boot_locale_bank_alias,
            requested_hash,
            refii::vfs::ComputeResourceNameHash(
                requested_boot_locale_bank_alias),
            primary_resource,
            "requested_boot_locale_bank");
        if (interesting || refii::vfs::LooseAssetOverrideTracingEnabled() ||
            refii::vfs::ShouldLogRepeatedVfsTraceOperation(
                "ResourceAliasResolverBootLocaleBank", 16)) {
          REXLOG_INFO("VFS trace ResourceAliasResolver claimed boot locale "
                      "bank alias requested={} requested_hash=0x{:08X} "
                      "target={} candidate={} provider_relative={} "
                      "primary=0x{:08X} targeted_hashes={}",
                      requested_name,
                      requested_hash,
                      requested_boot_locale_bank_alias,
                      boot_bank_candidate,
                      boot_bank_resolution.provider_relative_path,
                      primary_resource,
                      refii::vfs::TargetedRequestManagerHashContributionCount());
        }
        ctx.r3.u32 = owner_guest;
        return;
      }
    }

    const bool resolved_exact_bank =
        refii::vfs::ResolveExactTypedBankManifestLooseAsset(
            resolved_key, nullptr, nullptr);
    const bool resolved_exact_file =
        !resolved_exact_bank &&
        refii::vfs::RefiiDatabaseHashHasExactOwnedCandidate(
            resolved_hash, resolved_key);
    refii::vfs::LooseAssetResolution resolved_alias_resolution;
    std::string resolved_alias_candidate;
    const bool resolved_alias_database_asset =
        !resolved_exact_bank && !resolved_exact_file &&
        !resolved_key.ends_with(".bnk") &&
        refii::vfs::ResolveVfsDatabaseExactAliasLooseAsset(
            resolved_key, &resolved_alias_resolution, &resolved_alias_candidate);
    const bool resolved_exact_database_asset =
        resolved_exact_bank || resolved_exact_file || resolved_alias_database_asset;
    if (!resolved_exact_bank) {
      const bool requested_exact_bank =
          refii::vfs::ResolveExactTypedBankManifestLooseAsset(
              requested_key, nullptr, nullptr);
      const bool resolved_missing_language_bank =
          (resolved_key.starts_with("language/") ||
           resolved_key.starts_with("language_dlc/")) &&
          resolved_key.ends_with(".bnk");
      if (requested_exact_bank && resolved_missing_language_bank) {
        ctx.r3.u32 = owner_guest;
        if (interesting || refii::vfs::LooseAssetOverrideTracingEnabled() ||
            refii::vfs::ShouldLogRepeatedVfsTraceOperation(
                "ResourceAliasResolverRequestedBankSkip", 16)) {
          REXLOG_INFO("VFS trace ResourceAliasResolver skipped requested-only "
                      "synthetic bank alias requested={} resolved={} "
                      "requested_hash=0x{:08X} resolved_hash=0x{:08X} "
                      "ownership=requested_exact_only",
                      requested_name,
                      resolved_name,
                      requested_hash,
                      resolved_hash);
        }
        return;
      }
    }
    if (resolved_exact_database_asset) {
      const bool alias_attach_required = requested_hash != resolved_hash;
      bool name_side_effects = false;
      refii::vfs::ResourceAliasValidationResult validation;
      bool attach_side_effects = false;
      if (alias_attach_required) {
        name_side_effects =
            refii::vfs::ApplyResourceAliasNameSideEffects(
                ctx, base, owner_guest, resolved_name_guest, requested_hash);
        validation = refii::vfs::ValidateSyntheticResourceAliasAttach(
            ctx, base, owner_guest, requested_hash);
        attach_side_effects =
            validation.attach &&
            refii::vfs::ApplySyntheticResourceAliasAttach(
                ctx, base, owner_guest, requested_name_guest);
      }

      const uint32_t primary_resource =
          owner_guest == 0
              ? 0
              : refii::vfs::LoadGuestU32(base, owner_guest + 24);
      const std::string targeted_name = resolved_key;
      const std::string targeted_resolved_candidate =
          resolved_alias_candidate.empty() ? resolved_key
                                           : resolved_alias_candidate;
      const std::string targeted_provider =
          refii::vfs::NormalizeLooseAssetKey(
              resolved_alias_resolution.provider_relative_path);
      const std::string ownership =
          resolved_exact_bank
              ? "resolved_exact_bank"
              : (resolved_alias_database_asset ? "resolved_alias_file"
                                                : "resolved_exact_file");
      refii::vfs::RegisterTargetedRequestManagerHashContribution(
          resolved_hash,
          targeted_name,
          resolved_exact_bank
              ? "resolved_exact_bank_alias"
              : (resolved_alias_database_asset ? "resolved_alias_file"
                                                : "resolved_exact_file_alias"),
          targeted_resolved_candidate,
          targeted_provider,
          ownership);
      if (requested_hash != 0 && requested_hash != resolved_hash &&
          !requested_key.empty()) {
        refii::vfs::RegisterTargetedRequestManagerHashContribution(
            requested_hash,
            requested_key,
            resolved_exact_bank
                ? "requested_exact_bank_alias"
                : (resolved_alias_database_asset ? "requested_alias_file"
                                                  : "requested_exact_file_alias"),
            targeted_resolved_candidate,
            targeted_provider,
            ownership);
      }
      if (resolved_exact_bank) {
        refii::vfs::RegisterResolvedSyntheticBankOpenAlias(
            requested_key, resolved_key, "resource_alias_requested");
        refii::vfs::RegisterResolvedSyntheticBankOpenAlias(
            std::filesystem::path(resolved_key).filename().generic_string(),
            resolved_key,
            "resource_alias_basename");
      } else if (resolved_alias_database_asset &&
                 !resolved_alias_candidate.empty()) {
        refii::vfs::RecordRuntimeResourceHashName(
            resolved_hash, resolved_alias_candidate);
      }
      if (primary_resource != 0) {
        refii::vfs::RecordKnownRequestManagerGuest(
            primary_resource, "ResourceAliasResolver");
        PPCContext augment_ctx = ctx;
        refii::vfs::EnsureRefiiBootBankTemplates(
            augment_ctx, base, "ResourceAliasResolver");
        refii::vfs::AugmentRequestManagerHashTable(
            augment_ctx, base, primary_resource, "ResourceAliasResolver");
      }
      refii::vfs::RecordRefiiLastVfsAliasOperation(
          "ResourceAliasResolver",
          requested_name,
          resolved_name,
          requested_hash,
          resolved_hash,
          primary_resource,
          ownership);

      if (interesting || refii::vfs::LooseAssetOverrideTracingEnabled() ||
          refii::vfs::ShouldLogRepeatedVfsTraceOperation(
              "ResourceAliasResolverSyntheticBank", 16)) {
        REXLOG_INFO("VFS trace ResourceAliasResolver claimed Refii "
                    "alias requested={} resolved={} requested_hash=0x{:08X} "
                    "resolved_hash=0x{:08X} name={} attach={} "
                    "validation=0x{:08X}/0x{:08X}->0x{:08X}/0x{:08X} "
                    "primary=0x{:08X} ownership={} alias_candidate={} "
                    "targeted_hashes={}",
                    requested_name,
                    resolved_name,
                    requested_hash,
                    resolved_hash,
                    name_side_effects ? "ok" : "skipped",
                    attach_side_effects
                        ? "ok"
                        : (validation.ran && !validation.attach
                               ? "validation_skip"
                               : "skipped"),
                    validation.result_container,
                    validation.result_node,
                    validation.expected_container,
                    validation.expected_node,
                    primary_resource,
                    ownership,
                    resolved_alias_candidate,
                    refii::vfs::TargetedRequestManagerHashContributionCount());
      }
      ctx.r3.u32 = owner_guest;
      return;
    }

    if (!resolved_key.empty() && !resolved_key.ends_with(".bnk") &&
        refii::vfs::ShouldQueryVfsDatabaseAlias(resolved_key) &&
        (interesting || refii::vfs::LooseAssetOverrideTracingEnabled() ||
         refii::vfs::ShouldLogRepeatedVfsTraceOperation(
             "ResourceAliasResolverAliasFallthrough", 16))) {
      REXLOG_INFO("VFS trace ResourceAliasResolver alias fallthrough "
                  "requested={} resolved={} requested_hash=0x{:08X} "
                  "resolved_hash=0x{:08X} ownership=unclaimed_alias",
                  requested_name,
                  resolved_name,
                  requested_hash,
                  resolved_hash);
    }
  }

  __imp__sub_82374728(ctx, base);

  if (interesting) {
    const uint32_t primary_resource = owner_guest == 0
                                          ? 0
                                          : refii::vfs::LoadGuestU32(
                                                base, owner_guest + 24);
    REXLOG_INFO("VFS trace ResourceAliasResolver exit lr=0x{:08X} "
                "result={} owner_words={} primary_resource={}",
                original_ctx.lr,
                refii::vfs::DescribeGuestObject(base, ctx.r3.u32, 16, 12),
                refii::vfs::FormatGuestWords(base, owner_guest, 16),
                refii::vfs::DescribeGuestObject(
                    base, primary_resource, 16, 12));
  }
}

PPC_FUNC_IMPL(sub_82B54E58) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t resource_stream_guest = ctx.r4.u32;
  const uint32_t extra_out_guest = ctx.r5.u32;
  const uint32_t name_guest = ctx.r6.u32;
  const uint32_t arg7 = ctx.r7.u32;
  const uint32_t mode = ctx.r8.u32;
  const uint32_t owner_out_guest = ctx.r9.u32;
  const std::string name =
      refii::vfs::ReadBestEffortGuestString(base, name_guest, 4096);
  const bool trace =
      refii::vfs::VfsTracingEnabled() ||
      refii::vfs::LooseAssetOverrideTracingEnabled();

  if (refii::vfs::RefiiLooseAssetBackendEnabled() &&
      resource_stream_guest != 0 &&
      refii::vfs::LoadGuestU32(base, resource_stream_guest) == 0x820F9EA4) {
    uint32_t helper_resource_hash = 0;
    bool pass_through_hash = false;
    if (name.empty()) {
      helper_resource_hash =
          refii::vfs::LoadGuestU32(base, resource_stream_guest + 48);
      const std::vector<std::string> runtime_names =
          refii::vfs::GetRuntimeResourceHashNames(helper_resource_hash);
      pass_through_hash =
          refii::vfs::ShouldPassThroughNativeResourceHash(
              helper_resource_hash, runtime_names);
      if (pass_through_hash) {
        refii::vfs::TraceLuaVfsBridgeResolution(
            "resource_stream_find_hash", helper_resource_hash, runtime_names);
      }
    }

    if ((!name.empty() || helper_resource_hash != 0) && !pass_through_hash) {
      refii::vfs::RefiiVfsOpenResult open_result;
      refii::vfs::TryOpenRefiiVfsAsset(
          ctx,
          base,
          original_ctx,
          refii::vfs::RefiiVfsOpenShape::ResourceWrapper,
          refii::vfs::RefiiVfsOpenRequest{
              .api = "ResourceStream_Find",
              .request_name = name,
              .resource_hash = helper_resource_hash,
              .out_guest = out_guest,
              .manager_guest = resource_stream_guest,
              .mode = mode == 0 ? 1 : mode,
              .resource_flags = arg7,
              .caller_lr = static_cast<uint32_t>(original_ctx.lr),
              .prefer_resource_manager_texture_shape = true,
          },
          &open_result);
      if (open_result.disposition ==
          refii::vfs::RefiiVfsOpenDisposition::Opened) {
        if (owner_out_guest != 0) {
          const uint32_t wrapper_guest =
              refii::vfs::TryReadOutputPointer(base, out_guest);
          refii::vfs::StoreGuestU32(base, owner_out_guest,
                                        wrapper_guest != 0 ? wrapper_guest + 4 : 0);
          if (wrapper_guest != 0) {
            refii::vfs::AddRefGuestObject(ctx, base, wrapper_guest);
          }
        }
        if (extra_out_guest != 0) {
          refii::vfs::StoreGuestU32(base, extra_out_guest, 0);
        }
        refii::vfs::LogRefiiOwnedTextureStreamBuilt(
            base,
            "ResourceStream_Find",
            static_cast<uint32_t>(original_ctx.lr),
            helper_resource_hash,
            name,
            open_result.resolution,
            open_result.candidate,
            open_result.source,
            out_guest,
            open_result.opened_guest,
            true);
        return;
      }
      if (refii::vfs::IsHandledRefiiVfsOpenDisposition(
              open_result.disposition)) {
        return;
      }
    }
  }

  __imp__sub_82B54E58(ctx, base);

  if (trace) {
    REXLOG_INFO("Resource stream find native exit lr=0x{:08X} resource=0x{:08X} "
                "name={} out=0x{:08X} stream=0x{:08X} owner_out=0x{:08X} "
                "owner=0x{:08X} result=0x{:08X}",
                original_ctx.lr,
                resource_stream_guest,
                name,
                out_guest,
                refii::vfs::TryReadOutputPointer(base, out_guest),
                owner_out_guest,
                owner_out_guest != 0
                    ? refii::vfs::TryReadOutputPointer(base, owner_out_guest)
                    : 0,
                ctx.r3.u32);
  }
}
