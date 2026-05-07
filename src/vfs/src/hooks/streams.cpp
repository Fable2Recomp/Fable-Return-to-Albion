#include "vfs_hooks_private.h"

PPC_FUNC_IMPL(rex_lhStream_Buffered_GetSize) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t stream_guest = ctx.r3.u32;
  const bool trace_size =
      refii::vfs::ShouldTraceStartupBikStreamShape(
          base, stream_guest, "BIKBuffered_GetSize", 8);
  if (trace_size) {
    REXLOG_INFO("VFS trace BIK buffered GetSize enter lr=0x{:08X} "
                "ctr=0x{:08X} stream=0x{:08X} r4=0x{:08X} "
                "buffered_shape=[{}]",
                original_ctx.lr,
                original_ctx.ctr.u32,
                stream_guest,
                original_ctx.r4.u32,
                refii::vfs::FormatBikBufferedNativeShape(base, stream_guest));
  }
  __imp__rex_lhStream_Buffered_GetSize(ctx, base);
  refii::vfs::LogLooseStreamSizeResult(
      "Buffered_GetSize", base, stream_guest, ctx);
  if (trace_size) {
    refii::vfs::RecordRefiiStartupVideoBufferedSize(
        base, stream_guest, ctx.r3.u32);
    refii::vfs::MaybeReleaseCompletedStartupBikSource(
        ctx, base, stream_guest, "Buffered_GetSize");
    REXLOG_INFO("VFS trace BIK buffered GetSize exit lr=0x{:08X} "
                "stream=0x{:08X} result=0x{:08X} buffered_shape=[{}]",
                original_ctx.lr,
                stream_guest,
                ctx.r3.u32,
                refii::vfs::FormatBikBufferedNativeShape(base, stream_guest));
  }
}

PPC_FUNC_IMPL(sub_828D4110) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t stream_guest = ctx.r3.u32;
  const bool trace_size =
      refii::vfs::ShouldTraceStartupBikStreamShape(
          base, stream_guest, "BIKBuffered_SizeSlot24", 8);
  if (trace_size) {
    REXLOG_INFO("VFS trace BIK buffered size slot24 enter lr=0x{:08X} "
                "ctr=0x{:08X} stream=0x{:08X} buffered_shape=[{}]",
                original_ctx.lr,
                original_ctx.ctr.u32,
                stream_guest,
                refii::vfs::FormatBikBufferedNativeShape(base, stream_guest));
  }
  __imp__sub_828D4110(ctx, base);
  refii::vfs::RecordRefiiStartupVideoBufferedSize(
      base, stream_guest, ctx.r3.u32);
  refii::vfs::MaybeReleaseCompletedStartupBikSource(
      ctx, base, stream_guest, "BIK_size_slot24");
  if (trace_size) {
    REXLOG_INFO("VFS trace BIK buffered size slot24 exit lr=0x{:08X} "
                "stream=0x{:08X} result=0x{:08X} "
                "generated_shape=BIK_size_callback_reads_buffered_field32 "
                "buffered_shape=[{}]",
                original_ctx.lr,
                stream_guest,
                ctx.r3.u32,
                refii::vfs::FormatBikBufferedNativeShape(base, stream_guest));
  }
}

PPC_FUNC_IMPL(rex_lhStream_DiskStream_GetSize) {
  PPC_FUNC_PROLOGUE();

  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t stream_guest = ctx.r4.u32;
  if (refii::vfs::TryHandleRefiiHostDiskStreamGetSize(
          ctx, base, stream_guest, out_guest)) {
    refii::vfs::LogLooseStreamSizeResult(
        "DiskStream_GetSize", base, stream_guest, ctx);
    return;
  }

  __imp__rex_lhStream_DiskStream_GetSize(ctx, base);
  refii::vfs::LogLooseStreamSizeResult(
      "DiskStream_GetSize", base, stream_guest, ctx);
}

PPC_FUNC_IMPL(rex_lhStream_GetInnerSize) {
  PPC_FUNC_PROLOGUE();

  const uint32_t stream_guest = ctx.r3.u32;
  __imp__rex_lhStream_GetInnerSize(ctx, base);
  refii::vfs::LogLooseStreamSizeResult(
      "GetInnerSize", base, stream_guest, ctx);
}

PPC_FUNC_IMPL(rex_lhStream_GetPosition) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t stream_guest = ctx.r4.u32;
  const bool trace_position =
      refii::vfs::ShouldTraceStartupBikStreamShape(
          base, stream_guest, "BIKStream_GetPosition", 8);
  const std::string out_before =
      trace_position
          ? refii::vfs::FormatGuestWordsIfPointer(base, out_guest, 2)
          : std::string{};
  if (trace_position) {
    REXLOG_INFO("VFS trace BIK stream GetPosition enter lr=0x{:08X} "
                "ctr=0x{:08X} out=0x{:08X} stream=0x{:08X} "
                "out_before={} stream_shape=[{}]",
                original_ctx.lr,
                original_ctx.ctr.u32,
                out_guest,
                stream_guest,
                out_before,
                refii::vfs::FormatBikBufferedNativeShape(base, stream_guest));
  }
  std::string match;
  uint64_t position = 0;
  uint32_t out_word0 = 0;
  uint32_t out_word1 = 0;
  bool handled = false;
  if (const auto host_record =
          refii::vfs::FindRefiiHostStreamRecord(stream_guest)) {
    {
      std::lock_guard<std::mutex> lock(host_record->mutex);
      position = host_record->position;
    }
    out_word0 = static_cast<uint32_t>(position);
    out_word1 = static_cast<uint32_t>(position >> 32);
    handled = true;
    match = "host";
  }
  if (handled) {
    if (out_guest != 0) {
      refii::vfs::StoreGuestU32(base, out_guest + 0, out_word0);
      refii::vfs::StoreGuestU32(base, out_guest + 4, out_word1);
    }
    ctx.r3.u32 = out_guest;
    refii::vfs::RecordRefiiStartupVideoPosition(
        base, stream_guest, out_guest, out_word0, out_word1);
    if (refii::vfs::ShouldLogLooseStreamOperation(
            "Stream_GetPosition/refii", stream_guest, 8)) {
      REXLOG_INFO("Loose stream position Stream_GetPosition/refii "
                  "stream=0x{:08X} match={} value=0x{:08X}:0x{:08X} "
                  "position={} out=0x{:08X} words={}",
                  stream_guest,
                  match,
                  out_word0,
                  out_word1,
                  position,
                  out_guest,
                  refii::vfs::FormatGuestWords(base, stream_guest, 12));
    }
    if (trace_position) {
      REXLOG_INFO("VFS trace BIK stream GetPosition exit lr=0x{:08X} "
                  "stream=0x{:08X} handled=refii_host result=0x{:08X} "
                  "position=0x{:08X}:0x{:08X} out_after={} "
                  "request_or_stream_shape=[{}]",
                  original_ctx.lr,
                  stream_guest,
                  ctx.r3.u32,
                  out_word1,
                  out_word0,
                  refii::vfs::FormatGuestWordsIfPointer(base, out_guest, 2),
                  refii::vfs::FormatBikPpcStreamSummary(base, stream_guest));
    }
    return;
  }

  __imp__rex_lhStream_GetPosition(ctx, base);
  const uint32_t native_low =
      out_guest != 0 ? refii::vfs::LoadGuestU32(base, out_guest) : 0;
  const uint32_t native_high =
      out_guest != 0 ? refii::vfs::LoadGuestU32(base, out_guest + 4) : 0;
  uint32_t after_low = native_low;
  uint32_t after_high = native_high;
  const bool repaired_bik_position =
      refii::vfs::TryRepairRefiiStartupBikBufferedPosition(base,
                                                               stream_guest,
                                                               out_guest,
                                                               native_low,
                                                               native_high,
                                                               original_ctx.lr,
                                                               &after_low,
                                                               &after_high);
  if (trace_position || repaired_bik_position) {
    refii::vfs::RecordRefiiStartupVideoPosition(
        base, stream_guest, out_guest, after_low, after_high);
    refii::vfs::MaybeReleaseCompletedStartupBikSource(
        ctx, base, stream_guest, "Stream_GetPosition");
    REXLOG_INFO("VFS trace BIK stream GetPosition exit lr=0x{:08X} "
                "stream=0x{:08X} handled=native_buffered result=0x{:08X} "
                "native_position=0x{:08X}:0x{:08X} "
                "position=0x{:08X}:0x{:08X} repaired={} out_after={} "
                "generated_shape=Buffered_GetPosition_returns_fields12_16 "
                "buffered_shape=[{}]",
                original_ctx.lr,
                stream_guest,
                ctx.r3.u32,
                native_high,
                native_low,
                after_high,
                after_low,
                repaired_bik_position,
                refii::vfs::FormatGuestWordsIfPointer(base, out_guest, 2),
                refii::vfs::FormatBikBufferedNativeShape(base, stream_guest));
  }
}

PPC_FUNC_IMPL(rex_lhResourceManager_vtbl_GetStreamSize) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t request_guest = original_ctx.r3.u32;
  if (refii::vfs::IsNativeVfsScheduledRequest(base, request_guest)) {
    const bool trace =
        refii::vfs::VfsTracingEnabled() &&
        (refii::vfs::RefiiPostVideoVfsHandoffTraceActive() ||
         refii::vfs::RefiiStartupVideosReadyForMenuHandoff() ||
         refii::vfs::RefiiPostFrontendConsumerHandoffSignalEmitted()) &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "ScheduledRequest_Kind_828FD4A0", 128);
    std::string before_state;
    if (trace) {
      before_state = refii::vfs::FormatNativeVfsAsyncRequestState(
          base, request_guest);
      REXLOG_INFO("VFS trace native scheduled shared enter "
                  "ScheduledRequest_Kind_828FD4A0 lr=0x{:08X} ctr=0x{:08X} "
                  "r4=0x{:08X} state=[{}] handoff=[{}] main_menu=[{}]",
                  original_ctx.lr,
                  original_ctx.ctr.u32,
                  original_ctx.r4.u32,
                  before_state,
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    }

    __imp__rex_lhResourceManager_vtbl_GetStreamSize(ctx, base);

    if (trace) {
      REXLOG_INFO("VFS trace native scheduled shared exit "
                  "ScheduledRequest_Kind_828FD4A0 lr=0x{:08X} result=0x{:08X} "
                  "before=[{}] after=[{}]",
                  original_ctx.lr,
                  ctx.r3.u32,
                  before_state,
                  refii::vfs::FormatNativeVfsAsyncRequestState(
                      base, request_guest));
    }
    return;
  }

  const uint32_t stream_guest = ctx.r4.u32;
  refii::vfs::LooseStreamRecord record;
  std::string match;
  if (refii::vfs::RefiiLooseAssetBackendEnabled() &&
      refii::vfs::FindLooseStreamRecord(base, stream_guest, &record, &match)) {
    ctx.r3.u32 = static_cast<uint32_t>(std::min<uint64_t>(
        record.expected_size, std::numeric_limits<uint32_t>::max()));
    if (refii::vfs::ShouldLogLooseStreamOperation(
            "ResourceManager_GetStreamSize/refii", stream_guest, 8)) {
      REXLOG_INFO("Loose stream size ResourceManager_GetStreamSize/refii "
                  "stream=0x{:08X} match={} size={} path={} words={}",
                  stream_guest,
                  match,
                  record.expected_size,
                  record.path,
                  refii::vfs::FormatGuestWords(base, stream_guest, 12));
    }
    return;
  }

  __imp__rex_lhResourceManager_vtbl_GetStreamSize(ctx, base);
  refii::vfs::LogLooseStreamSizeResult(
      "ResourceManager_GetStreamSize", base, stream_guest, ctx);
}

PPC_FUNC_IMPL(rex_lhStream_Buffered_Read) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t stream_guest = ctx.r3.u32;
  const uint32_t arg4 = ctx.r4.u32;
  const uint32_t arg5 = ctx.r5.u32;
  const uint32_t arg6 = ctx.r6.u32;
  const bool trace_read =
      refii::vfs::ShouldTraceStartupBikStreamShape(
          base, stream_guest, "BIKBuffered_Read", 8);
  const std::string destination_before =
      trace_read ? refii::vfs::FormatGuestWordsIfPointer(base, arg4, 4)
                 : std::string{};
  if (trace_read) {
    REXLOG_INFO("VFS trace BIK buffered read enter lr=0x{:08X} ctr=0x{:08X} "
                "stream=0x{:08X} r4=0x{:08X} r5=0x{:08X} r6=0x{:08X} "
                "r7=0x{:08X} destination_before={} buffered_shape=[{}]",
                original_ctx.lr,
                original_ctx.ctr.u32,
                stream_guest,
                arg4,
                arg5,
                arg6,
                original_ctx.r7.u32,
                destination_before,
                refii::vfs::FormatBikBufferedNativeShape(base, stream_guest));
  }
  __imp__rex_lhStream_Buffered_Read(ctx, base);
  refii::vfs::LogLooseStreamRead(
      "Buffered_Read", base, stream_guest, arg4, arg5, arg6, ctx);
  if (trace_read) {
    REXLOG_INFO("VFS trace BIK buffered read exit lr=0x{:08X} "
                "stream=0x{:08X} result=0x{:08X} destination_after={} "
                "buffered_shape=[{}]",
                original_ctx.lr,
                stream_guest,
                ctx.r3.u32,
                refii::vfs::FormatGuestWordsIfPointer(base, arg4, 4),
                refii::vfs::FormatBikBufferedNativeShape(base, stream_guest));
  }
}

PPC_FUNC_IMPL(sub_82B5AD98) {
  PPC_FUNC_PROLOGUE();

  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t stream_guest = ctx.r4.u32;
  const uint32_t buffer_guest = ctx.r5.u32;
  const uint64_t offset = ctx.r6.u64;
  const uint32_t requested_bytes = ctx.r7.u32;
  if (refii::vfs::TryHandleRefiiHostCompositeStreamRead(
          ctx,
          base,
          out_guest,
          stream_guest,
          buffer_guest,
          offset,
          requested_bytes)) {
    return;
  }

  __imp__sub_82B5AD98(ctx, base);
}

PPC_FUNC_IMPL(sub_83032078) {
  PPC_FUNC_PROLOGUE();

  if (refii::vfs::TryHandleRefiiHostDiskStreamNoopSuccess(
          ctx, base, ctx.r3.u32, "CompositeStream_UnsupportedNoop")) {
    return;
  }

  __imp__sub_83032078(ctx, base);
}

PPC_FUNC_IMPL(sub_83032088) {
  PPC_FUNC_PROLOGUE();

  if (refii::vfs::TryHandleRefiiHostDiskStreamNoopSuccess(
          ctx, base, ctx.r3.u32, "DiskStream_UnsupportedNoop")) {
    return;
  }

  __imp__sub_83032088(ctx, base);
}

PPC_FUNC_IMPL(sub_82C72E60) {
  PPC_FUNC_PROLOGUE();

  const uint32_t stream_guest = ctx.r3.u32;
  const uint32_t buffer_guest = ctx.r4.u32;
  const uint64_t offset = ctx.r5.u64;
  const uint32_t offset_low = ctx.r5.u32;
  const uint32_t requested_bytes = ctx.r6.u32;
  if (refii::vfs::TryHandleRefiiHostDiskStreamDirectRead(
          ctx, base, stream_guest, buffer_guest, offset, requested_bytes)) {
    refii::vfs::LogLooseStreamRead(
        "DiskStream_DirectRead",
        base,
        stream_guest,
        buffer_guest,
        offset_low,
        requested_bytes,
        ctx);
    return;
  }

  __imp__sub_82C72E60(ctx, base);
  refii::vfs::LogLooseStreamRead(
      "DiskStream_DirectRead",
      base,
      stream_guest,
      buffer_guest,
      offset_low,
      requested_bytes,
      ctx);
}

PPC_FUNC_IMPL(sub_82C73028) {
  PPC_FUNC_PROLOGUE();

  const uint32_t stream_guest = ctx.r3.u32;
  if (refii::vfs::IsRefiiHostDiskStream(base, stream_guest)) {
    const auto record = refii::vfs::FindRefiiHostStreamRecord(stream_guest);
    const uint64_t size =
        record != nullptr
            ? record->size
            : refii::vfs::LoadGuestU64(base, stream_guest + 16);
    ctx.r3.u32 = static_cast<uint32_t>(
        std::min<uint64_t>(size, std::numeric_limits<uint32_t>::max()));
    if (refii::vfs::ShouldLogLooseStreamOperation(
            "DiskStream_ScalarGetSize", stream_guest, 16)) {
      REXLOG_INFO("Refii host loose stream scalar size stream=0x{:08X} "
                  "size={} returned={} caller_lr=0x{:08X} words={}",
                  stream_guest,
                  size,
                  ctx.r3.u32,
                  ctx.lr,
                  refii::vfs::FormatGuestWords(base, stream_guest, 12));
    }
    return;
  }

  __imp__sub_82C73028(ctx, base);
}

PPC_FUNC_IMPL(rex_lhStream_DecompressStream_Read) {
  PPC_FUNC_PROLOGUE();

  const uint32_t stream_guest = ctx.r3.u32;
  const uint32_t buffer_guest = ctx.r4.u32;
  const uint64_t offset = ctx.r5.u64;
  const uint32_t offset_low = ctx.r5.u32;
  const uint32_t requested_bytes = ctx.r6.u32;
  if (refii::vfs::TryHandleRefiiHostCompositeStreamDirectRead(
          ctx, base, stream_guest, buffer_guest, offset, requested_bytes)) {
    refii::vfs::LogLooseStreamRead(
        "CompositeStream_DirectRead",
        base,
        stream_guest,
        buffer_guest,
        offset_low,
        requested_bytes,
        ctx);
    return;
  }

  __imp__rex_lhStream_DecompressStream_Read(ctx, base);
  refii::vfs::LogLooseStreamRead(
      "CompositeStream_DirectRead",
      base,
      stream_guest,
      buffer_guest,
      offset_low,
      requested_bytes,
      ctx);
}

PPC_FUNC_IMPL(rex_lhStream_DiskStream_Read) {
  PPC_FUNC_PROLOGUE();

  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t stream_guest = ctx.r4.u32;
  const uint32_t arg4 = ctx.r4.u32;
  const uint32_t arg5 = ctx.r5.u32;
  const uint32_t arg6 = ctx.r6.u32;
  const uint64_t offset = ctx.r6.u64;
  const uint32_t arg7 = ctx.r7.u32;
  if (refii::vfs::TryHandleRefiiHostDiskStreamRead(
          ctx, base, out_guest, stream_guest, arg5, offset, arg7)) {
    refii::vfs::LogLooseStreamRead(
        "DiskStream_Read", base, stream_guest, arg4, arg5, arg6, ctx);
    return;
  }

  __imp__rex_lhStream_DiskStream_Read(ctx, base);
  refii::vfs::LogLooseStreamRead(
      "DiskStream_Read", base, stream_guest, arg4, arg5, arg6, ctx);
}

PPC_FUNC_IMPL(rex_lhStream_DiskStream_Dtor) {
  PPC_FUNC_PROLOGUE();

  const uint32_t stream_guest = ctx.r3.u32;
  if (refii::vfs::IsRefiiHostDiskStream(base, stream_guest)) {
    refii::vfs::EraseRefiiHostStreamRecord(stream_guest);
    refii::vfs::EraseLooseStreamRecord(stream_guest);
    refii::vfs::StoreGuestU32(base, stream_guest + 28, 0);
  }

  __imp__rex_lhStream_DiskStream_Dtor(ctx, base);
}

PPC_FUNC_IMPL(rex_lhStream_Reader_Read) {
  PPC_FUNC_PROLOGUE();

  const uint32_t stream_guest = ctx.r3.u32;
  const uint32_t arg4 = ctx.r4.u32;
  const uint32_t arg5 = ctx.r5.u32;
  const uint32_t arg6 = ctx.r6.u32;
  __imp__rex_lhStream_Reader_Read(ctx, base);
  refii::vfs::LogLooseStreamRead(
      "Reader_Read", base, stream_guest, arg4, arg5, arg6, ctx);
}

PPC_FUNC_IMPL(rex_lhStream_ReadUInt32) {
  PPC_FUNC_PROLOGUE();

  const uint32_t stream_guest = ctx.r3.u32;
  const uint32_t arg4 = ctx.r4.u32;
  const uint32_t arg5 = ctx.r5.u32;
  const uint32_t arg6 = ctx.r6.u32;
  __imp__rex_lhStream_ReadUInt32(ctx, base);
  refii::vfs::LogLooseStreamRead(
      "ReadUInt32", base, stream_guest, arg4, arg5, arg6, ctx);
}

PPC_FUNC_IMPL(rex_lhStream_Buffered_Init) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t buffered_guest = ctx.r3.u32;
  const uint32_t source_stream_guest = ctx.r4.u32;
  const uint32_t arg5 = ctx.r5.u32;
  const uint32_t arg6 = ctx.r6.u32;
  const bool trace_init =
      refii::vfs::ShouldTraceStartupBikStreamShape(
          base, source_stream_guest, "BIKBuffered_Init", 8);
  if (trace_init) {
    REXLOG_INFO("VFS trace BIK buffered init enter lr=0x{:08X} ctr=0x{:08X} "
                "buffered=0x{:08X} source=0x{:08X} r5=0x{:08X} "
                "r6=0x{:08X} generated_shape=init_80_bytes_size_slot24_"
                "alloc_buffer_then_optional_source_slot16_prebuffer "
                "source_shape=[{}] buffered_before=[{}]",
                original_ctx.lr,
                original_ctx.ctr.u32,
                buffered_guest,
                source_stream_guest,
                arg5,
                arg6,
                refii::vfs::FormatBikPpcStreamSummary(
                    base, source_stream_guest),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, buffered_guest, 20));
  }

  __imp__rex_lhStream_Buffered_Init(ctx, base);
  refii::vfs::LogLooseBufferedInit(base,
                                       original_ctx,
                                       ctx,
                                       buffered_guest,
                                       source_stream_guest,
                                       arg5,
                                       arg6);
  if (trace_init) {
    REXLOG_INFO("VFS trace BIK buffered init exit lr=0x{:08X} "
                "buffered=0x{:08X} source=0x{:08X} result=0x{:08X} "
                "buffered_shape=[{}] source_shape=[{}]",
                original_ctx.lr,
                buffered_guest,
                source_stream_guest,
                ctx.r3.u32,
                refii::vfs::FormatBikBufferedNativeShape(
                    base, buffered_guest),
                refii::vfs::FormatBikPpcStreamSummary(
                    base, source_stream_guest));
  }
  refii::vfs::RecordLooseStreamAlias(
      base, "Buffered_Init/source", buffered_guest, source_stream_guest);
}

PPC_FUNC_IMPL(rex_lhStream_CreateBuffered) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t source_stream_guest = ctx.r4.u32;
  refii::vfs::LogLooseStreamWrapperCall(
      "CreateBuffered", base, ctx, out_guest, source_stream_guest);

  __imp__rex_lhStream_CreateBuffered(ctx, base);
  refii::vfs::LogLooseStreamWrapperResult("CreateBuffered",
                                              base,
                                              original_ctx,
                                              ctx,
                                              out_guest,
                                              source_stream_guest);
  refii::vfs::RecordLooseStreamAliasFromOutput(
      base,
      "CreateBuffered/r3out",
      out_guest,
      source_stream_guest);
  refii::vfs::RecordLooseStreamAlias(
      base, "CreateBuffered/r3ret", ctx.r3.u32, source_stream_guest);
}

PPC_FUNC_IMPL(rex_lhStream_CreateFromPosition) {
  PPC_FUNC_PROLOGUE();

  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t source_stream_guest = ctx.r4.u32;
  refii::vfs::LogLooseStreamWrapperCall(
      "CreateFromPosition", base, ctx, out_guest, source_stream_guest);
  __imp__rex_lhStream_CreateFromPosition(ctx, base);
  refii::vfs::RecordLooseStreamAliasFromOutput(
      base,
      "CreateFromPosition/r3out",
      out_guest,
      source_stream_guest);
  refii::vfs::RecordLooseStreamAlias(
      base, "CreateFromPosition/r3ret", ctx.r3.u32, source_stream_guest);
}

PPC_FUNC_IMPL(rex_lhStream_CreateReadStream) {
  PPC_FUNC_PROLOGUE();

  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t source_stream_guest = ctx.r4.u32;
  refii::vfs::LogLooseStreamWrapperCall(
      "CreateReadStream", base, ctx, out_guest, source_stream_guest);
  __imp__rex_lhStream_CreateReadStream(ctx, base);
  refii::vfs::RecordLooseStreamAliasFromOutput(
      base,
      "CreateReadStream/r3out",
      out_guest,
      source_stream_guest);
  refii::vfs::RecordLooseStreamAlias(
      base, "CreateReadStream/r3ret", ctx.r3.u32, source_stream_guest);
}

PPC_FUNC_IMPL(rex_lhStream_CreateSubStream) {
  PPC_FUNC_PROLOGUE();

  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t source_stream_guest = ctx.r4.u32;
  refii::vfs::LogLooseStreamWrapperCall(
      "CreateSubStream", base, ctx, out_guest, source_stream_guest);
  __imp__rex_lhStream_CreateSubStream(ctx, base);
  refii::vfs::RecordLooseStreamAliasFromOutput(
      base,
      "CreateSubStream/r3out",
      out_guest,
      source_stream_guest);
  refii::vfs::RecordLooseStreamAlias(
      base, "CreateSubStream/r3ret", ctx.r3.u32, source_stream_guest);
}

PPC_FUNC_IMPL(rex_lhStream_CreateReader) {
  PPC_FUNC_PROLOGUE();

  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t source_stream_guest = ctx.r4.u32;
  refii::vfs::LogLooseStreamWrapperCall(
      "CreateReader", base, ctx, out_guest, source_stream_guest);
  __imp__rex_lhStream_CreateReader(ctx, base);
  refii::vfs::RecordLooseStreamAliasFromOutput(
      base,
      "CreateReader/r3out",
      out_guest,
      source_stream_guest);
  refii::vfs::RecordLooseStreamAlias(
      base, "CreateReader/r3ret", ctx.r3.u32, source_stream_guest);
}

PPC_FUNC_IMPL(rex_lhStream_Reader_Ctor) {
  PPC_FUNC_PROLOGUE();

  const uint32_t reader_guest = ctx.r3.u32;
  const uint32_t source_stream_guest = ctx.r4.u32;
  refii::vfs::LogLooseStreamWrapperCall(
      "Reader_Ctor", base, ctx, reader_guest, source_stream_guest);
  __imp__rex_lhStream_Reader_Ctor(ctx, base);
  refii::vfs::RecordLooseStreamAlias(
      base, "Reader_Ctor", reader_guest, source_stream_guest);
}
