#include "vfs_hooks_private.h"

namespace {

bool IsNativeStartupBikCursorCaller(uint32_t link_register) {
  return link_register == 0x82377C98u || link_register == 0x822F4FC8u;
}

bool ShouldLogNativeStartupVideoNativeMiscAudit(std::string_view key,
                                                uint32_t limit) {
  if (refii::vfs::RefiiLooseAssetBackendEnabled()) {
    return false;
  }

  static std::mutex mutex;
  static std::unordered_map<std::string, uint32_t> counts;

  std::lock_guard<std::mutex> lock(mutex);
  const uint32_t next_count = ++counts[std::string(key)];
  return next_count <= limit;
}

} // namespace

REX_EXTERN(sub_82C6E8F0) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82C6E8F0);

  const PPCContext original_ctx = ctx;
  const uint32_t object_guest = original_ctx.r3.u32;
  const uint32_t dst_guest = original_ctx.r4.u32;
  const uint32_t request_size = original_ctx.r5.u32;
  const uint32_t stream_guest =
      object_guest != 0 ? refii::vfs::LoadGuestU32(base, object_guest + 8)
                        : 0;
  const uint32_t object_offset =
      object_guest != 0 ? refii::vfs::LoadGuestU32(base, object_guest + 12)
                        : 0;

  const bool trace_read =
      refii::vfs::ShouldTraceBikPpcShape(
          base, stream_guest, "BIKPpcReadAdapter", 12);
  if (trace_read) {
    REXLOG_INFO("VFS trace BIK read enter lr=0x{:08X} ctr=0x{:08X} "
                "object=0x{:08X} stream=0x{:08X} dst=0x{:08X} "
                "request=0x{:08X} object_offset=0x{:08X} object_words={} "
                "generated_shape=slot12(r3=stream,r4={{dst,0}},r5=offset,r6=size) "
                "stream_summary=[{}] handoff=[{}]",
                original_ctx.lr,
                original_ctx.ctr.u32,
                object_guest,
                stream_guest,
                dst_guest,
                request_size,
                object_offset,
                refii::vfs::FormatGuestWordsIfPointer(base, object_guest, 8),
                refii::vfs::FormatBikPpcStreamSummary(base, stream_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog());
  }

  __imp__sub_82C6E8F0(ctx, base);

  if (trace_read) {
    const uint32_t new_object_offset =
        object_guest != 0 ? refii::vfs::LoadGuestU32(base, object_guest + 12)
                          : 0;
    REXLOG_INFO("VFS trace BIK read exit lr=0x{:08X} result=0x{:08X} "
                "object=0x{:08X} stream=0x{:08X} request=0x{:08X} "
                "object_offset=0x{:08X}->0x{:08X} object_words={} "
                "stream_summary=[{}] handoff=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                object_guest,
                stream_guest,
                request_size,
                object_offset,
                new_object_offset,
                refii::vfs::FormatGuestWordsIfPointer(base, object_guest, 8),
                refii::vfs::FormatBikPpcStreamSummary(base, stream_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog());
  }
}

REX_EXTERN(sub_82C6EAD8) {
  REX_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t object_guest = original_ctx.r3.u32;
  const uint32_t stream_guest =
      object_guest != 0 ? refii::vfs::LoadGuestU32(base, object_guest + 8)
                        : 0;
  if (!refii::vfs::RefiiLooseAssetBackendEnabled()) {
    const bool startup_loop_cursor =
        IsNativeStartupBikCursorCaller(original_ctx.lr);
    const bool audit =
        startup_loop_cursor ||
        ShouldLogNativeStartupVideoNativeMiscAudit("NativeBikCursorAny", 8);
    if (audit) {
      REXLOG_INFO("Refii VFS native BIK cursor enter "
                  "lr=0x{:08X} object=0x{:08X} stream=0x{:08X} "
                  "startup_loop={} object_words={} stream_words={} {}",
                  original_ctx.lr,
                  object_guest,
                  stream_guest,
                  startup_loop_cursor,
                  refii::vfs::FormatGuestWordsIfPointer(base, object_guest, 8),
                  refii::vfs::FormatGuestWords(base, stream_guest, 12),
                  refii::vfs::FormatGuestObjectVTable(
                      base, stream_guest, 12));
    }
    const auto cursor_started_at = std::chrono::steady_clock::now();
    __imp__sub_82C6EAD8(ctx, base);
    const auto cursor_elapsed_ms =
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - cursor_started_at)
            .count();
    if (startup_loop_cursor) {
      refii::vfs::RecordRefiiStartupVideoCursor(
          base, object_guest, stream_guest, ctx.r3.u32);
    }
    if (audit) {
      REXLOG_INFO("Refii VFS native BIK cursor exit "
                  "lr=0x{:08X} object=0x{:08X} stream=0x{:08X} "
                  "result=0x{:08X} elapsed_ms={} startup_loop={}",
                  original_ctx.lr,
                  object_guest,
                  stream_guest,
                  ctx.r3.u32,
                  cursor_elapsed_ms,
                  startup_loop_cursor);
    }
    return;
  }

  const bool trace_cursor =
      refii::vfs::ShouldTraceBikPpcShape(
          base, stream_guest, "BIKPpcCursor", 8);
  const bool startup_bik_cursor =
      refii::vfs::IsBikLooseStreamTraceAsset(base, stream_guest);
  if (startup_bik_cursor &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "StartupBikCursorCompactEnter", 12)) {
    REXLOG_INFO("Refii VFS startup BIK cursor enter "
                "lr=0x{:08X} object=0x{:08X} stream=0x{:08X} "
                "stream_summary=[{}]",
                original_ctx.lr,
                object_guest,
                stream_guest,
                refii::vfs::FormatBikPpcStreamSummary(base, stream_guest));
  }
  if (trace_cursor) {
    REXLOG_INFO("VFS trace BIK cursor enter lr=0x{:08X} object=0x{:08X} "
                "stream=0x{:08X} object_words={} object_vtable={} "
                "stream_summary=[{}] handoff=[{}]",
                original_ctx.lr,
                object_guest,
                stream_guest,
                refii::vfs::FormatGuestWordsIfPointer(base, object_guest, 8),
                refii::vfs::FormatGuestObjectVTableSlots(
                    base, object_guest, 8),
                refii::vfs::FormatBikPpcStreamSummary(base, stream_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog());
  }

  const auto cursor_started_at = std::chrono::steady_clock::now();
  __imp__sub_82C6EAD8(ctx, base);
  const auto cursor_elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - cursor_started_at)
          .count();
  refii::vfs::RecordRefiiStartupVideoCursor(
      base, object_guest, stream_guest, ctx.r3.u32);
  if (startup_bik_cursor &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "StartupBikCursorCompactExit", 12)) {
    REXLOG_INFO("Refii VFS startup BIK cursor exit "
                "lr=0x{:08X} object=0x{:08X} stream=0x{:08X} "
                "result=0x{:08X} elapsed_ms={}",
                original_ctx.lr,
                object_guest,
                stream_guest,
                ctx.r3.u32,
                cursor_elapsed_ms);
  }
  if (IsNativeStartupBikCursorCaller(original_ctx.lr)) {
    const bool trace_loop =
        refii::vfs::ShouldTraceStartupVideoLoopProbe(
            "StartupVideoLoop_CursorPoll", original_ctx.lr, 12);
    if (trace_loop) {
      refii::vfs::RecordRefiiPostVideoUiEvent("StartupVideoCursorPoll",
                                                  original_ctx.lr,
                                                  ctx.r3.u32,
                                                  original_ctx.r3.u32,
                                                  original_ctx.r4.u32,
                                                  original_ctx.r5.u32);
      REXLOG_INFO("VFS trace startup video loop cursor poll "
                  "lr=0x{:08X} object=0x{:08X} stream=0x{:08X} "
                  "result=0x{:08X} elapsed_ms={} stream_summary=[{}] "
                  "handoff=[{}] main_menu=[{}]",
                  original_ctx.lr,
                  object_guest,
                  stream_guest,
                  ctx.r3.u32,
                  cursor_elapsed_ms,
                  refii::vfs::FormatBikPpcStreamSummary(base, stream_guest),
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    }
  }

  if (trace_cursor) {
    REXLOG_INFO("VFS trace BIK cursor exit lr=0x{:08X} object=0x{:08X} "
                "stream=0x{:08X} result=0x{:08X} object_words={} handoff=[{}]",
                original_ctx.lr,
                object_guest,
                stream_guest,
                ctx.r3.u32,
                refii::vfs::FormatGuestWordsIfPointer(base, object_guest, 8),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog());
  }
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(original_ctx.lr,
                                                        "BIKCursorExit",
                                                        base);
}

REX_EXTERN(sub_82C6EA48) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82C6EA48);

  const PPCContext original_ctx = ctx;
  const uint32_t object_guest = original_ctx.r3.u32;
  const uint32_t stream_guest =
      object_guest != 0 ? refii::vfs::LoadGuestU32(base, object_guest + 8)
                        : 0;

  const bool trace_size =
      refii::vfs::ShouldTraceBikPpcShape(
          base, stream_guest, "BIKPpcSize", 8);
  if (trace_size) {
    REXLOG_INFO("VFS trace BIK size enter lr=0x{:08X} object=0x{:08X} "
                "stream=0x{:08X} generated_shape=slot24(stream) "
                "stream_summary=[{}] handoff=[{}]",
                original_ctx.lr,
                object_guest,
                stream_guest,
                refii::vfs::FormatBikPpcStreamSummary(base, stream_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog());
  }
  __imp__sub_82C6EA48(ctx, base);
  refii::vfs::RecordRefiiStartupVideoSize(
      base, object_guest, stream_guest, ctx.r3.u32);
  if (trace_size) {
    REXLOG_INFO("VFS trace BIK size exit lr=0x{:08X} object=0x{:08X} "
                "stream=0x{:08X} result=0x{:08X} handoff=[{}]",
                original_ctx.lr,
                object_guest,
                stream_guest,
                ctx.r3.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog());
  }
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(original_ctx.lr,
                                                        "BIKSizeExit",
                                                        base);
  refii::vfs::MaybeLogRefiiVideosReadyTextureOpenGap(original_ctx.lr,
                                                         "BIKSizeExit",
                                                         base);
}

REX_EXTERN(sub_82C6EA80) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82C6EA80);

  const PPCContext original_ctx = ctx;
  const uint32_t object_guest = original_ctx.r3.u32;
  const uint32_t stream_guest =
      object_guest != 0 ? refii::vfs::LoadGuestU32(base, object_guest + 8)
                        : 0;

  const bool trace_status =
      refii::vfs::ShouldTraceBikPpcShape(
          base, stream_guest, "BIKPpcStatus", 8);
  if (trace_status) {
    REXLOG_INFO("VFS trace BIK status enter lr=0x{:08X} object=0x{:08X} "
                "stream=0x{:08X} generated_shape=GetPosition(stack+80,stream) "
                "object_words={} stream_summary=[{}]",
                original_ctx.lr,
                object_guest,
                stream_guest,
                refii::vfs::FormatGuestWordsIfPointer(base, object_guest, 8),
                refii::vfs::FormatBikPpcStreamSummary(base, stream_guest));
  }

  __imp__sub_82C6EA80(ctx, base);
  refii::vfs::RecordRefiiStartupVideoStatus(
      base, object_guest, stream_guest, ctx.r3.u32);

  if (trace_status) {
    REXLOG_INFO("VFS trace BIK status exit lr=0x{:08X} object=0x{:08X} "
                "stream=0x{:08X} result=0x{:08X} handoff=[{}]",
                original_ctx.lr,
                object_guest,
                stream_guest,
                ctx.r3.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog());
  }
}

REX_EXTERN(sub_82C8C718) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82C8C718);

  const PPCContext original_ctx = ctx;
  const uint32_t parser_guest = original_ctx.r3.u32;
  const uint32_t stream_guest = original_ctx.r4.u32;
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::IsRefiiHostDiskStream(base, stream_guest);
  if (trace) {
    REXLOG_INFO("VFS trace ConfigParser_ParseStream enter lr=0x{:08X} "
                "parser=0x{:08X} stream=0x{:08X} parser_words={} "
                "stream_words={} stream_vtable={}",
                original_ctx.lr,
                parser_guest,
                stream_guest,
                refii::vfs::FormatGuestWords(base, parser_guest, 8),
                refii::vfs::FormatGuestWords(base, stream_guest, 12),
                refii::vfs::FormatGuestObjectVTable(base, stream_guest, 12));
  }

  __imp__sub_82C8C718(ctx, base);
  if (trace) {
    refii::vfs::RecordRefiiPostVideoUiEvent("ConfigParser_ParseStream",
                                                original_ctx.lr,
                                                ctx.r3.u32,
                                                original_ctx.r3.u32,
                                                original_ctx.r4.u32,
                                                original_ctx.r5.u32);
  }

  if (trace) {
    REXLOG_INFO("VFS trace ConfigParser_ParseStream exit lr=0x{:08X} "
                "result=0x{:08X} parser=0x{:08X} parser_words={} "
                "handoff=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                parser_guest,
                refii::vfs::FormatGuestWords(base, parser_guest, 8),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog());
  }
}

REX_EXTERN(rex_lhGameInit_SetupAudio) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhGameInit_SetupAudio);

  const PPCContext original_ctx = ctx;
  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::VfsVerboseIoTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase enter SetupAudio lr=0x{:08X} "
                "game=0x{:08X} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__rex_lhGameInit_SetupAudio(ctx, base);
  if (refii::vfs::kTraceFrontendHandoffDiagnostics) {
    refii::vfs::RecordRefiiPostVideoUiEvent("SetupAudio",
                                                original_ctx.lr,
                                                ctx.r3.u32,
                                                original_ctx.r3.u32,
                                                original_ctx.r4.u32,
                                                original_ctx.r5.u32);
  }

  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::VfsVerboseIoTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase exit SetupAudio lr=0x{:08X} "
                "result=0x{:08X} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (refii::vfs::kTraceFrontendHandoffDiagnostics) {
    refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(original_ctx.lr,
                                                          "SetupAudioExit",
                                                          base);
  }
}

REX_EXTERN(rex_lhRefCounted_ReturnMinusOne) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhRefCounted_ReturnMinusOne);

  const uint32_t object_guest = ctx.r3.u32;
  refii::vfs::LooseStreamRecord record;
  std::string match;
  if (refii::vfs::FindLooseStreamRecord(
          base, object_guest, &record, &match) &&
      record.expected_size <= UINT32_MAX) {
    ctx.r3.u32 = static_cast<uint32_t>(record.expected_size);
    if (refii::vfs::LooseAssetOverrideTracingEnabled() &&
        refii::vfs::ShouldLogLooseStreamOperation(
            "ReturnLooseStreamSize", object_guest, 8)) {
      REXLOG_INFO("Loose stream size ReturnMinusOne override "
                  "caller_lr=0x{:08X} object=0x{:08X} match={} size={} "
                  "path={} words={} {}",
                  ctx.lr,
                  object_guest,
                  match,
                  record.expected_size,
                  record.path,
                  refii::vfs::FormatGuestWords(base, object_guest, 12),
                  refii::vfs::FormatGuestObjectVTable(
                      base, object_guest, 12));
    }
    return;
  }

  __imp__rex_lhRefCounted_ReturnMinusOne(ctx, base);
}

REX_EXTERN(sub_82A20EE8) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82A20EE8);

  const PPCContext original_ctx = ctx;
  const bool mesh_reader_trace =
      refii::vfs::kTraceMeshModelAudioDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      original_ctx.lr >= 0x82B70900 && original_ctx.lr <= 0x82B71500;
  if (mesh_reader_trace) {
    REXLOG_INFO("VFS trace MeshResource byte-reader enter lr=0x{:08X} "
                "reader=0x{:08X} out=0x{:08X} bytes={} reader_words={}",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                original_ctx.r5.u32,
                refii::vfs::FormatGuestWords(base, original_ctx.r3.u32, 8));
  }

  __imp__sub_82A20EE8(ctx, base);

  if (mesh_reader_trace) {
    REXLOG_INFO("VFS trace MeshResource byte-reader exit lr=0x{:08X} "
                "reader=0x{:08X} result=0x{:08X} reader_words={}",
                original_ctx.lr,
                original_ctx.r3.u32,
                ctx.r3.u32,
                refii::vfs::FormatGuestWords(base, original_ctx.r3.u32, 8));
  }
}

REX_EXTERN(rex_lhRefCounted_Release) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhRefCounted_Release);

  const PPCContext original_ctx = ctx;
  const uint32_t object_guest = ctx.r3.u32;
  const bool trace_game_data_iterator_release =
      refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      (original_ctx.lr == 0x8237FEF0 || original_ctx.lr == 0x8237FF28 ||
       original_ctx.lr == 0x8237FF3C);
  if (trace_game_data_iterator_release) {
    REXLOG_INFO("VFS trace GameData iterator ref release enter lr=0x{:08X} "
                "object=0x{:08X} words={} {}",
                original_ctx.lr,
                object_guest,
                refii::vfs::FormatGuestWords(base, object_guest, 8),
                refii::vfs::FormatGuestObjectVTable(base, object_guest, 8));
  }

  __imp__rex_lhRefCounted_Release(ctx, base);

  if (trace_game_data_iterator_release) {
    REXLOG_INFO("VFS trace GameData iterator ref release exit lr=0x{:08X} "
                "object=0x{:08X} result=0x{:08X} words={}",
                original_ctx.lr,
                object_guest,
                ctx.r3.u32,
                refii::vfs::FormatGuestWords(base, object_guest, 8));
  }

  if (ctx.r3.u32 == 0) {
    refii::vfs::EraseSyntheticBankRecord(object_guest);
    refii::vfs::EraseSyntheticBankIteratorRecord(object_guest);
    refii::vfs::RetireRefiiReadRequestAfterNativeRelease(
        ctx, base, object_guest, "RefCounted_Release");
    refii::vfs::EraseRefiiHostStreamRecord(object_guest);
    refii::vfs::EraseLooseStreamRecord(object_guest);
  }
}

REX_EXTERN(sub_822C72E8) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_822C72E8);

  const uint32_t object_guest = ctx.r3.u32;
  if (refii::vfs::TryHandleRefiiReadRequestRelease(
          ctx, base, object_guest)) {
    return;
  }

  __imp__sub_822C72E8(ctx, base);
  if (ctx.r3.u32 == 0) {
    refii::vfs::RetireRefiiReadRequestAfterNativeRelease(
        ctx, base, object_guest, "sub_822C72E8");
    refii::vfs::EraseSyntheticBankRecord(object_guest);
    refii::vfs::EraseSyntheticBankIteratorRecord(object_guest);
    refii::vfs::EraseRefiiHostStreamRecord(object_guest);
    refii::vfs::EraseLooseStreamRecord(object_guest);
  }
}

REX_EXTERN(sub_822CBB98) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_822CBB98);

  if (refii::vfs::TryHandleRefiiReadRequestAddRef(
          ctx, base, ctx.r3.u32)) {
    return;
  }

  __imp__sub_822CBB98(ctx, base);
}

REX_EXTERN(sub_8290DD10) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_8290DD10);

  if (refii::vfs::TryHandleRefiiReadRequestGetStatus(
          ctx, base, ctx.r3.u32, "ReadRequest_GetStatus")) {
    return;
  }

  __imp__sub_8290DD10(ctx, base);
}

REX_EXTERN(sub_82B54D28) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B54D28);

  if (refii::vfs::TryHandleRefiiResourceWrapperDtor(
          ctx, base, ctx.r3.u32, ctx.r4.u32)) {
    return;
  }

  __imp__sub_82B54D28(ctx, base);
}

REX_EXTERN(sub_82B54250) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B54250);

  if (refii::vfs::TryHandleRefiiResourceWrapperWait(
          ctx, base, ctx.r3.u32, "ResourceWrapper_Wait")) {
    return;
  }

  __imp__sub_82B54250(ctx, base);
}

REX_EXTERN(sub_82B54268) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B54268);

  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = original_ctx.r3.u32;
  const uint32_t wrapper_guest = original_ctx.r4.u32;

  if (refii::vfs::TryHandleRefiiResourceWrapperGetBytes(
          ctx, base, ctx.r3.u32, ctx.r4.u32)) {
    return;
  }

  const bool trace_native =
      refii::vfs::ShouldTraceNativeResourceWrapperApi(
          "GetBytes", original_ctx.lr);
  const std::string out_before =
      trace_native ? refii::vfs::FormatGuestWords(base, out_guest, 2)
                   : std::string{};
  const std::string wrapper_before =
      trace_native ? refii::vfs::FormatGuestWords(base, wrapper_guest, 12)
                   : std::string{};

  __imp__sub_82B54268(ctx, base);

  if (trace_native) {
    REXLOG_INFO("Native resource wrapper ResourceWrapper_GetBytes "
                "lr=0x{:08X} ctr=0x{:08X} r3=0x{:08X} r4=0x{:08X} "
                "result=0x{:08X} wrapper=0x{:08X} out=0x{:08X} "
                "out_before={} out_after={} wrapper_before={} "
                "wrapper_after={}",
                original_ctx.lr,
                original_ctx.ctr.u32,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                ctx.r3.u32,
                wrapper_guest,
                out_guest,
                out_before,
                refii::vfs::FormatGuestWords(base, out_guest, 2),
                wrapper_before,
                refii::vfs::FormatGuestWords(base, wrapper_guest, 12));
  }
}

REX_EXTERN(sub_82B542D8) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B542D8);

  const PPCContext original_ctx = ctx;
  const uint32_t wrapper_guest = original_ctx.r3.u32;

  if (refii::vfs::TryHandleRefiiResourceWrapperWait(
          ctx, base, ctx.r3.u32, "ResourceWrapper_Status")) {
    return;
  }

  const bool trace_native =
      refii::vfs::ShouldTraceNativeResourceWrapperApi(
          "Status", original_ctx.lr);
  const std::string wrapper_before =
      trace_native ? refii::vfs::FormatGuestWords(base, wrapper_guest, 12)
                   : std::string{};

  __imp__sub_82B542D8(ctx, base);

  if (trace_native) {
    REXLOG_INFO("Native resource wrapper ResourceWrapper_Status "
                "lr=0x{:08X} ctr=0x{:08X} r3=0x{:08X} r4=0x{:08X} "
                "result=0x{:08X} wrapper=0x{:08X} wrapper_before={} "
                "wrapper_after={}",
                original_ctx.lr,
                original_ctx.ctr.u32,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                ctx.r3.u32,
                wrapper_guest,
                wrapper_before,
                refii::vfs::FormatGuestWords(base, wrapper_guest, 12));
  }
}

REX_EXTERN(sub_82B542F0) { // Lumen: LHVFS::CBasicBufferedRequest::GetDevice(void)
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B542F0);

  if (refii::vfs::TryHandleRefiiResourceWrapperSlot36(
          ctx, base, ctx.r3.u32, ctx.r4.u32)) {
    return;
  }

  __imp__sub_82B542F0(ctx, base);
}

REX_EXTERN(sub_82C0EE10) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82C0EE10);

  const PPCContext original_ctx = ctx;
  const uint32_t wrapper_guest = original_ctx.r3.u32;

  if (refii::vfs::TryHandleRefiiResourceWrapperGetSize(
          ctx, base, ctx.r3.u32)) {
    return;
  }

  const bool trace_native =
      refii::vfs::ShouldTraceNativeResourceWrapperApi(
          "GetSize", original_ctx.lr);
  const std::string wrapper_before =
      trace_native ? refii::vfs::FormatGuestWords(base, wrapper_guest, 12)
                   : std::string{};

  __imp__sub_82C0EE10(ctx, base);

  if (trace_native) {
    REXLOG_INFO("Native resource wrapper ResourceWrapper_GetSize "
                "lr=0x{:08X} ctr=0x{:08X} r3=0x{:08X} r4=0x{:08X} "
                "result={} wrapper=0x{:08X} wrapper_before={} "
                "wrapper_after={}",
                original_ctx.lr,
                original_ctx.ctr.u32,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                ctx.r3.u32,
                wrapper_guest,
                wrapper_before,
                refii::vfs::FormatGuestWords(base, wrapper_guest, 12));
  }
}

REX_EXTERN(sub_82B5BFF8) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B5BFF8);

  if (refii::vfs::TryHandleRefiiReadRequestWait(
          ctx, base, ctx.r3.u32, "ReadRequest_Wait")) {
    return;
  }

  __imp__sub_82B5BFF8(ctx, base);
}

REX_EXTERN(sub_82B5C0C0) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B5C0C0);

  if (refii::vfs::TryHandleRefiiReadRequestGetBytes(
          ctx, base, ctx.r3.u32, ctx.r4.u32)) {
    return;
  }

  __imp__sub_82B5C0C0(ctx, base);
}

REX_EXTERN(sub_82B5C0D8) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B5C0D8);

  if (refii::vfs::TryHandleRefiiReadRequestGetOffset(ctx, ctx.r3.u32)) {
    return;
  }

  __imp__sub_82B5C0D8(ctx, base);
}

REX_EXTERN(sub_82B5C0E0) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B5C0E0);

  if (refii::vfs::TryHandleRefiiReadRequestReturnSuccess(
          ctx, base, ctx.r3.u32, "ReadRequest_Notify")) {
    return;
  }

  __imp__sub_82B5C0E0(ctx, base);
}

REX_EXTERN(sub_82B5C110) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B5C110);

  if (refii::vfs::TryHandleRefiiReadRequestReturnSuccess(
          ctx, base, ctx.r3.u32, "ReadRequest_SetCallback")) {
    return;
  }

  __imp__sub_82B5C110(ctx, base);
}

REX_EXTERN(sub_82B5C148) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B5C148);

  __imp__sub_82B5C148(ctx, base);
}

REX_EXTERN(sub_82B5C160) { // Lumen: LHVFS::CWin32Request::GetDevice(void)
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B5C160);

  __imp__sub_82B5C160(ctx, base);
}

REX_EXTERN(sub_8252AA18) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_8252AA18);

  if (refii::vfs::TryHandleRefiiReadRequestSmartPtrAssign(
          ctx, base, ctx.r3.u32, ctx.r4.u32)) {
    return;
  }

  __imp__sub_8252AA18(ctx, base);
}

REX_EXTERN(sub_82B4EC30) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B4EC30);

  if (refii::vfs::TryHandleRefiiStreamReaderFlush(
          ctx, base, ctx.r3.u32, "StreamReader_FlushPage")) {
    return;
  }

  __imp__sub_82B4EC30(ctx, base);
}

REX_EXTERN(sub_82B4E6D0) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B4E6D0);

  const PPCContext original_ctx = ctx;
  const uint32_t reader_guest = original_ctx.r3.u32;
  const uint32_t destination_guest = original_ctx.r4.u32;
  const uint32_t requested_bytes = original_ctx.r5.u32;
  const uint32_t source_stream_guest =
      reader_guest == 0 ? 0 : refii::vfs::LoadGuestU32(base, reader_guest + 8);
  const bool interesting =
      refii::vfs::VfsVerboseIoTracingEnabled() &&
      refii::vfs::IsRefiiStreamReader(base, reader_guest) &&
      refii::vfs::ShouldLogLooseStreamOperation(
          "StreamReaderReadE6D0", reader_guest, 16);
  if (interesting) {
    REXLOG_INFO("VFS trace StreamReader read enter api=sub_82B4E6D0 "
                "lr=0x{:08X} reader=0x{:08X} source=0x{:08X} "
                "destination=0x{:08X} destination_base=0x{:08X} "
                "destination_kind={} requested={} reader_offset={} "
                "reader_words={} destination_words={} source_words={}",
                original_ctx.lr,
                reader_guest,
                source_stream_guest,
                destination_guest,
                destination_guest == 0
                    ? 0
                    : refii::vfs::LoadGuestU32(base, destination_guest),
                destination_guest == 0
                    ? 0
                    : refii::vfs::LoadGuestU32(base, destination_guest + 4),
                requested_bytes,
                refii::vfs::LoadGuestU64(base, reader_guest + 16),
                refii::vfs::FormatGuestWords(base, reader_guest, 16),
                refii::vfs::FormatGuestWords(base, destination_guest, 4),
                refii::vfs::FormatGuestWords(base, source_stream_guest, 12));
  }

  __imp__sub_82B4E6D0(ctx, base);

  if (interesting) {
    REXLOG_INFO("VFS trace StreamReader read exit api=sub_82B4E6D0 "
                "lr=0x{:08X} result=0x{:08X} reader_offset={} "
                "reader_words={} destination_words={}",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::LoadGuestU64(base, reader_guest + 16),
                refii::vfs::FormatGuestWords(base, reader_guest, 16),
                refii::vfs::FormatGuestWords(base, destination_guest, 4));
  }
}

REX_EXTERN(sub_82B4E8E0) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B4E8E0);

  const PPCContext original_ctx = ctx;
  const uint32_t reader_guest = original_ctx.r3.u32;
  const uint32_t destination_guest = original_ctx.r4.u32;
  const uint32_t requested_bytes = original_ctx.r5.u32;
  const uint32_t source_stream_guest =
      reader_guest == 0 ? 0 : refii::vfs::LoadGuestU32(base, reader_guest + 8);
  const bool interesting =
      refii::vfs::VfsVerboseIoTracingEnabled() &&
      refii::vfs::IsRefiiStreamReader(base, reader_guest) &&
      refii::vfs::ShouldLogLooseStreamOperation(
          "StreamReaderReadE8E0", reader_guest, 16);
  if (interesting) {
    REXLOG_INFO("VFS trace StreamReader read enter api=sub_82B4E8E0 "
                "lr=0x{:08X} reader=0x{:08X} source=0x{:08X} "
                "destination=0x{:08X} destination_base=0x{:08X} "
                "destination_kind={} requested={} reader_offset={} "
                "reader_words={} destination_words={} source_words={}",
                original_ctx.lr,
                reader_guest,
                source_stream_guest,
                destination_guest,
                destination_guest == 0
                    ? 0
                    : refii::vfs::LoadGuestU32(base, destination_guest),
                destination_guest == 0
                    ? 0
                    : refii::vfs::LoadGuestU32(base, destination_guest + 4),
                requested_bytes,
                refii::vfs::LoadGuestU64(base, reader_guest + 16),
                refii::vfs::FormatGuestWords(base, reader_guest, 16),
                refii::vfs::FormatGuestWords(base, destination_guest, 4),
                refii::vfs::FormatGuestWords(base, source_stream_guest, 12));
  }

  __imp__sub_82B4E8E0(ctx, base);

  if (interesting) {
    REXLOG_INFO("VFS trace StreamReader read exit api=sub_82B4E8E0 "
                "lr=0x{:08X} result=0x{:08X} reader_offset={} "
                "reader_words={} destination_words={}",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::LoadGuestU64(base, reader_guest + 16),
                refii::vfs::FormatGuestWords(base, reader_guest, 16),
                refii::vfs::FormatGuestWords(base, destination_guest, 4));
  }
}

REX_EXTERN(sub_82B4ED70) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B4ED70);

  if (refii::vfs::TryHandleRefiiStreamReaderRefill(
          ctx, base, ctx.r3.u32, ctx.r4.u32, ctx.r5.u64)) {
    return;
  }

  const PPCContext original_ctx = ctx;
  const uint32_t reader_guest = original_ctx.r3.u32;
  const uint32_t source_stream_guest =
      reader_guest == 0 ? 0 : refii::vfs::LoadGuestU32(base, reader_guest + 8);
  const bool interesting =
      refii::vfs::VfsVerboseIoTracingEnabled() &&
      (refii::vfs::IsRefiiHostDiskStream(base, source_stream_guest) ||
       refii::vfs::IsCompositeStreamWithRefiiHostSegment(base,
                                                             source_stream_guest)) &&
      refii::vfs::ShouldLogLooseStreamOperation(
          "StreamReaderRefillNative", reader_guest, 16);
  if (interesting) {
    REXLOG_INFO("VFS trace StreamReader refill enter lr=0x{:08X} "
                "reader={} source={} page=0x{:08X} target_offset=0x{:016X} "
                "reader_words={}",
                original_ctx.lr,
                refii::vfs::DescribeGuestObject(base, reader_guest, 12, 12),
                refii::vfs::DescribeGuestObject(
                    base, source_stream_guest, 12, 12),
                original_ctx.r4.u32,
                original_ctx.r5.u64,
                refii::vfs::FormatGuestWords(base, reader_guest, 16));
  }

  __imp__sub_82B4ED70(ctx, base);

  if (interesting) {
    const uint32_t request_slot_guest =
        reader_guest == 0 ? 0 : reader_guest + 40 +
                                   (((refii::vfs::LoadGuestU32(
                                           base, reader_guest + 36) ^
                                      original_ctx.r4.u32)
                                     << 5) &
                                    0xFFFFFFE0) +
                                   16;
    const uint32_t request_guest =
        request_slot_guest == 0
            ? 0
            : refii::vfs::LoadGuestU32(base, request_slot_guest);
    REXLOG_INFO("VFS trace StreamReader refill exit lr=0x{:08X} result={} "
                "request_slot=0x{:08X} request=0x{:08X} slot_words={}",
                original_ctx.lr,
                ctx.r3.u32,
                request_slot_guest,
                request_guest,
                refii::vfs::FormatGuestWords(base, request_slot_guest, 4));
  }
}

REX_EXTERN(sub_82190728) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82190728);

  const PPCContext original_ctx = ctx;
  static std::atomic<uint32_t> job_pump_trace_count{0};
  const uint32_t trace_count =
      refii::vfs::kTraceMeshModelAudioDiagnostics &&
              refii::vfs::VfsTracingEnabled()
          ? job_pump_trace_count.fetch_add(1, std::memory_order_relaxed)
          : std::numeric_limits<uint32_t>::max();
  const bool trace = trace_count < 2;
  if (trace) {
    const uint32_t scheduler = original_ctx.r3.u32;
    const uint32_t queue_owner =
        scheduler == 0 ? 0 : refii::vfs::LoadGuestU32(base, scheduler + 4);
    const uint32_t queue_state =
        queue_owner == 0 ? 0 : refii::vfs::LoadGuestU32(base, queue_owner);
    REXLOG_INFO("VFS trace JobPump enter lr=0x{:08X} this=0x{:08X} "
                "budget={} queue_owner=0x{:08X} queue_state=0x{:08X}",
                original_ctx.lr,
                scheduler,
                original_ctx.r4.u32,
                queue_owner,
                queue_state);
  }

  __imp__sub_82190728(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace JobPump exit lr=0x{:08X} result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(sub_82C51CA0) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82C51CA0);

  const PPCContext original_ctx = ctx;
  const bool interesting =
      refii::vfs::kTraceMeshModelAudioDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      (refii::vfs::IsRefiiHostDiskStream(base, original_ctx.r4.u32) ||
       refii::vfs::IsCompositeStreamWithRefiiHostSegment(base,
                                                             original_ctx.r4.u32));
  if (interesting) {
    REXLOG_INFO("VFS trace AudioProfile parse enter lr=0x{:08X} "
                "this=0x{:08X} stream={} error_out={} "
                "stream_words={}",
                original_ctx.lr,
                original_ctx.r3.u32,
                refii::vfs::DescribeGuestObject(
                    base, original_ctx.r4.u32, 12, 12),
                refii::vfs::DescribeGuestObject(
                    base, original_ctx.r5.u32, 8, 8),
                refii::vfs::FormatGuestWords(base, original_ctx.r4.u32, 16));
  }

  __imp__sub_82C51CA0(ctx, base);

  if (interesting) {
    REXLOG_INFO("VFS trace AudioProfile parse exit lr=0x{:08X} result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(sub_82C51EC0) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82C51EC0);

  const PPCContext original_ctx = ctx;
  if (refii::vfs::kTraceMeshModelAudioDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    const uint32_t name_owner =
        original_ctx.r4.u32 == 0
            ? 0
            : refii::vfs::LoadGuestU32(base, original_ctx.r4.u32);
    const uint32_t name_guest =
        name_owner == 0 ? 0 : refii::vfs::LoadGuestU32(base, name_owner);
    REXLOG_INFO("VFS trace AudioProfile load enter lr=0x{:08X} "
                "this={} name_owner={} name_ptr=0x{:08X} name={}",
                original_ctx.lr,
                refii::vfs::DescribeGuestObject(
                    base, original_ctx.r3.u32, 12, 8),
                refii::vfs::DescribeGuestObject(
                    base, name_owner, 8, 8),
                name_guest,
                refii::vfs::ReadMaybeGuestString(base, name_guest));
  }

  __imp__sub_82C51EC0(ctx, base);

  if (refii::vfs::kTraceMeshModelAudioDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace AudioProfile load exit lr=0x{:08X} result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}
