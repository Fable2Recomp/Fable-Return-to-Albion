#include "vfs_hooks_private.h"

REX_EXTERN(rex_lhStream_Buffered_GetSize) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhStream_Buffered_GetSize);


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

REX_EXTERN(sub_828D4110) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_828D4110);


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

REX_EXTERN(rex_lhStream_DiskStream_GetSize) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.


  // `0x82C72F20` is `LHVFS::CBankFile::GetDevice`, vtable slot +32, not GetSize despite the
  // config's name: it answers with the underlying device interface pointer. (GetSize is slot
  // +24, `sub_82C6AB38` = `ld r3, 0x10(r3)`, already correct unhooked.) A Refii stream has no
  // container file, so the answer is a null device — returned rather than passed through
  // because the guest's implementation dereferences the container at `+24` unguarded
  // (`lwz r4, 0x18(r4)` then `lwz r11, 0(r4)`) and we leave `+24` at 0.
  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t stream_guest = ctx.r4.u32;
  if (refii::vfs::IsRefiiHostDiskStream(base, stream_guest)) {
    NoteRefiiStreamMethod("GetDevice", stream_guest, 32);
    if (out_guest != 0) {
      const uint32_t none = 0;
      refii::vfs::StoreGuestU32(base, out_guest, none);
    }
    ctx.r3.u32 = out_guest;
    return;
  }

  __imp__rex_lhStream_DiskStream_GetSize(ctx, base);
  refii::vfs::LogLooseStreamSizeResult(
      "DiskStream_GetSize", base, stream_guest, ctx);
}

REX_EXTERN(rex_lhStream_GetInnerSize) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhStream_GetInnerSize);


  const uint32_t stream_guest = ctx.r3.u32;
  __imp__rex_lhStream_GetInnerSize(ctx, base);
  refii::vfs::LogLooseStreamSizeResult(
      "GetInnerSize", base, stream_guest, ctx);
}

REX_EXTERN(rex_lhStream_GetPosition) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhStream_GetPosition);


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
  constexpr uint32_t kBikBufferedPointerGetPositionLr = 0x82C6EAF8u;
  const bool startup_bik_pointer_probe =
      original_ctx.lr == kBikBufferedPointerGetPositionLr &&
      refii::vfs::IsBikLooseStreamTraceAsset(base, stream_guest);
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
    uint32_t bik_buffer_guest = 0;
    uint32_t bik_bytes_read = 0;
    if (original_ctx.lr == kBikBufferedPointerGetPositionLr &&
        refii::vfs::TryGetRefiiStartupBikCompletedReadBuffer(
            base, stream_guest, &bik_buffer_guest, &bik_bytes_read)) {
      out_word0 = bik_buffer_guest;
      out_word1 = 0;
      handled = true;
      match = "startup_bik_buffer";
      position = bik_buffer_guest;
    } else {
      std::lock_guard<std::mutex> lock(host_record->mutex);
      if (original_ctx.lr == kBikBufferedPointerGetPositionLr &&
          host_record->materialized_buffer_guest != 0 &&
          host_record->materialized_bytes != 0) {
        out_word0 = host_record->materialized_buffer_guest;
        out_word1 = 0;
        position = out_word0;
        match = "startup_bik_materialized_buffer";
      } else {
        position = host_record->position;
        out_word0 = static_cast<uint32_t>(position);
        out_word1 = static_cast<uint32_t>(position >> 32);
        match = "host";
      }
      handled = true;
    }
  }
  if (handled) {
    if (out_guest != 0) {
      refii::vfs::StoreGuestU32(base, out_guest + 0, out_word0);
      refii::vfs::StoreGuestU32(base, out_guest + 4, out_word1);
    }
    ctx.r3.u32 = out_guest;
    refii::vfs::RecordRefiiStartupVideoPosition(
        base, stream_guest, out_guest, out_word0, out_word1);
    if (startup_bik_pointer_probe &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "StartupBikBufferedPointerGetPosition", 12)) {
      uint32_t materialized_buffer_guest = 0;
      uint32_t materialized_bytes = 0;
      if (const auto host_record =
              refii::vfs::FindRefiiHostStreamRecord(stream_guest)) {
        std::lock_guard<std::mutex> lock(host_record->mutex);
        materialized_buffer_guest = host_record->materialized_buffer_guest;
        materialized_bytes = host_record->materialized_bytes;
      }
      REXLOG_INFO("Refii VFS startup BIK buffered pointer GetPosition "
                  "lr=0x{:08X} stream=0x{:08X} out=0x{:08X} match={} "
                  "value=0x{:08X}:0x{:08X} materialized=0x{:08X} "
                  "materialized_bytes={} handled=refii",
                  original_ctx.lr,
                  stream_guest,
                  out_guest,
                  match,
                  out_word1,
                  out_word0,
                  materialized_buffer_guest,
                  materialized_bytes);
    }
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
  const bool log_repaired_position =
      repaired_bik_position &&
      !refii::vfs::RefiiStartupVideosPlaybackCriticalActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "BIKStream_GetPositionRepaired", 4);
  if (trace_position || repaired_bik_position) {
    refii::vfs::RecordRefiiStartupVideoPosition(
        base, stream_guest, out_guest, after_low, after_high);
    refii::vfs::MaybeReleaseCompletedStartupBikSource(
        ctx, base, stream_guest, "Stream_GetPosition");
  }
  if (startup_bik_pointer_probe &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "StartupBikBufferedPointerGetPositionNative", 12)) {
    REXLOG_INFO("Refii VFS startup BIK buffered pointer GetPosition "
                "lr=0x{:08X} stream=0x{:08X} out=0x{:08X} "
                "native=0x{:08X}:0x{:08X} after=0x{:08X}:0x{:08X} "
                "repaired={} handled=native",
                original_ctx.lr,
                stream_guest,
                out_guest,
                native_high,
                native_low,
                after_high,
                after_low,
                repaired_bik_position);
  }
  if (trace_position || log_repaired_position) {
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

REX_EXTERN(rex_lhResourceManager_vtbl_GetStreamSize) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhResourceManager_vtbl_GetStreamSize);


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
  if (refii::vfs::FindLooseStreamRecord(base, stream_guest, &record, &match)) {
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

REX_EXTERN(rex_lhStream_Buffered_Read) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhStream_Buffered_Read);


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

REX_EXTERN(sub_82B5AD98) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B5AD98);


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

REX_EXTERN(sub_82B72430) {
  REX_FUNC_PROLOGUE();

  // Diagnostic capture of the glyph-vector resize the .bft font parse dies in. r4 is the count
  // the parse decoded; the caller's reader struct sits at fixed offsets in its frame
  // (sub_82B70908, frame 0x110): +0x7C the count's 4-byte staging slot, +0xC0 the reader
  // {vtbl, consumed, size, ptr, _, remaining, swap, base}.
  {
    static std::atomic<uint32_t> logged{0};
    if (logged.fetch_add(1, std::memory_order_relaxed) < 8u) {
      const uint32_t vec = ctx.r3.u32;
      const uint32_t count = ctx.r4.u32;
      const uint32_t lr = static_cast<uint32_t>(ctx.lr);
      const uint32_t sp = ctx.r1.u32;
      constexpr uint32_t kFontParseReturn = 0x82B70E08u;
      if (lr == kFontParseReturn) {
        const uint32_t staged = refii::vfs::LoadGuestU32(base, sp + 0x7Cu);
        const uint32_t rd_consumed = refii::vfs::LoadGuestU32(base, sp + 0xC4u);
        const uint32_t rd_size = refii::vfs::LoadGuestU32(base, sp + 0xC8u);
        const uint32_t rd_ptr = refii::vfs::LoadGuestU32(base, sp + 0xCCu);
        const uint32_t rd_remaining = refii::vfs::LoadGuestU32(base, sp + 0xD4u);
        const uint32_t rd_swap = refii::vfs::LoadGuestU32(base, sp + 0xD8u);
        const uint32_t rd_base = refii::vfs::LoadGuestU32(base, sp + 0xDCu);
        REXLOG_INFO("Refii font glyph resize: vec=0x{:08X} count={} (0x{:08X}) staged=0x{:08X} "
                    "reader consumed={} size={} ptr=0x{:08X} remaining={} swap=0x{:08X} "
                    "base=0x{:08X} ptr_bytes={:08X} {:08X}",
                    vec, count, count, staged,
                    rd_consumed, rd_size, rd_ptr, rd_remaining, rd_swap, rd_base,
                    rd_ptr != 0 ? refii::vfs::LoadGuestU32(base, rd_ptr) : 0u,
                    rd_ptr != 0 ? refii::vfs::LoadGuestU32(base, rd_ptr + 4u) : 0u);
      } else {
        REXLOG_INFO("Refii glyph resize (other caller): vec=0x{:08X} count={} lr=0x{:08X}",
                    vec, count, lr);
      }
    }
  }

  __imp__sub_82B72430(ctx, base);
}

REX_EXTERN(sub_83032078) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.


  if (refii::vfs::TryHandleRefiiHostDiskStreamNoopSuccess(
          ctx, base, ctx.r3.u32, "CompositeStream_UnsupportedNoop")) {
    NoteRefiiStreamMethod("composite_unsupported_noop", ctx.r3.u32, -1);
    return;
  }

  __imp__sub_83032078(ctx, base);
}

namespace {

/// The resident-buffer accessor, slot +20 — the branch that picks the request kind.
///
/// `rex_lhResourceManager_FindAndCreateStream` (0x82B53D68) asks the file whether its bytes are
/// already in memory:
///
///     82b53dd8  addi r3, r1, var_50    ; sret buffer on the stack
///     82b53de4  lwz  r10, 0x14(r11)    ; slot +20
///     82b53dec  bctrl
///     82b53df0  lwz  r9, 0(r3)         ; re-read the sret
///     82b53df8  beq  cr6, loc_82B53EF0 ; ptr == 0 -> async CBasicBufferedRequest
///
/// A non-null pointer gets the 16-byte `CBasicCompleteRequest`, whose `GetDataBuffer`
/// (0x82174050) calls straight back through to this accessor. Our streams borrow `CBankFile`'s
/// vtable (0x8200E02C), whose +20 is the shared stub 0x83032088 (`li r3, E_FAIL; blr`) — it
/// never writes the sret, leaving `lwz r9, 0(r3)` reading stack garbage and, when that is zero,
/// an async request waiting forever on an event nothing signals. Answering here retires the
/// async path for every host-served asset. Materialised lazily.
bool TryAnswerRefiiStreamResidentBuffer(PPCContext& ctx,
                                        uint8_t* base,
                                        uint32_t out_guest,
                                        uint32_t stream_guest) {
  if (base == nullptr || out_guest == 0 || stream_guest == 0) {
    return false;
  }

  const auto record = refii::vfs::FindRefiiHostStreamRecord(stream_guest);
  if (!record) {
    return false;
  }

  std::lock_guard<std::mutex> lock(record->mutex);

  if (record->materialized_buffer_guest == 0) {
    const uint32_t bytes = static_cast<uint32_t>(
        std::min<uint64_t>(record->size, std::numeric_limits<uint32_t>::max()));
    if (bytes == 0) {
      return false;
    }

    const uint32_t buffer_guest = refii::vfs::CallLhMalloc(ctx, base, bytes);
    if (buffer_guest == 0) {
      REXLOG_WARN("Refii stream resident buffer: allocation of {} byte(s) refused for "
                  "stream=0x{:08X} path={}",
                  bytes, stream_guest, record->path.string());
      return false;
    }

    auto* destination = rex::memory::GuestPtr<uint8_t*>(base, buffer_guest);
    uint32_t filled = 0;
    if (record->host_served) {
      filled = static_cast<uint32_t>(
          std::min<size_t>(bytes, record->bytes.size()));
      std::memcpy(destination, record->bytes.data(), filled);
    } else {
      record->file.clear();
      record->file.seekg(0, std::ios::beg);
      record->file.read(reinterpret_cast<char*>(destination),
                        static_cast<std::streamsize>(bytes));
      filled = static_cast<uint32_t>(record->file.gcount());
    }

    // A short fill is a refusal, not a smaller file: the guest sizes the parse from slot +24,
    // not from this answer, and would read a partly-filled buffer to that full length.
    if (filled != bytes) {
      REXLOG_WARN("Refii stream resident buffer: short fill stream=0x{:08X} wanted={} got={} "
                  "path={}",
                  stream_guest, bytes, filled, record->path.string());
      return false;
    }

    record->materialized_buffer_guest = buffer_guest;
    record->materialized_bytes = bytes;

    static std::atomic<uint32_t> served{0};
    const uint32_t nth = served.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (nth <= 8u || (nth & (nth - 1u)) == 0u) {
      REXLOG_INFO("Refii stream resident buffer #{}: stream=0x{:08X} buffer=0x{:08X} "
                  "bytes={} asset={} — the guest takes the COMPLETE request path.",
                  nth, stream_guest, buffer_guest, bytes, record->path.string());
    }
  }

  refii::vfs::StoreGuestU32(base, out_guest + 0, record->materialized_buffer_guest);
  refii::vfs::StoreGuestU32(base, out_guest + 4, record->materialized_bytes);
  ctx.r3.u32 = out_guest;
  return true;
}

}  // namespace

REX_EXTERN(sub_83032088) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.


  // `CBankFile`'s vtable points both +20 and +28 at 0x83032088, and they do not share an ABI:
  // +28 is SetSize with `this` in r3; +20 is the resident-buffer accessor, which returns a
  // struct and so takes the sret in r3 and `this` in r4. Test the +20 form first, by r4.
  if (TryAnswerRefiiStreamResidentBuffer(ctx, base, ctx.r3.u32, ctx.r4.u32)) {
    NoteRefiiStreamMethod("resident_buffer_accessor", ctx.r4.u32, 20);
    return;
  }

  if (refii::vfs::TryHandleRefiiHostDiskStreamNoopSuccess(
          ctx, base, ctx.r3.u32, "DiskStream_UnsupportedNoop")) {
    NoteRefiiStreamMethod("unsupported_noop", ctx.r3.u32, 28);
    return;
  }

  __imp__sub_83032088(ctx, base);
}

REX_EXTERN(sub_82C72E60) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.


  const uint32_t stream_guest = ctx.r3.u32;
  const uint32_t buffer_guest = ctx.r4.u32;
  const uint64_t offset = ctx.r5.u64;
  const uint32_t offset_low = ctx.r5.u32;
  const uint32_t requested_bytes = ctx.r6.u32;
  if (refii::vfs::TryHandleRefiiHostDiskStreamDirectRead(
          ctx, base, stream_guest, buffer_guest, offset, requested_bytes)) {
    NoteRefiiStreamMethod("Read", stream_guest, 12);
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

REX_EXTERN(sub_82C73028) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.


  const uint32_t stream_guest = ctx.r3.u32;
  if (refii::vfs::IsRefiiHostDiskStream(base, stream_guest)) {
    NoteRefiiStreamMethod("sub_82C73028", stream_guest, -1);
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

REX_EXTERN(rex_lhStream_DecompressStream_Read) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhStream_DecompressStream_Read);


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

REX_EXTERN(rex_lhStream_DiskStream_Read) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.


  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t stream_guest = ctx.r4.u32;
  const uint32_t arg4 = ctx.r4.u32;
  const uint32_t arg5 = ctx.r5.u32;
  const uint32_t arg6 = ctx.r6.u32;
  const uint64_t offset = ctx.r6.u64;
  const uint32_t arg7 = ctx.r7.u32;
  // r9 is the transfer out-parameter — see TryHandleRefiiHostDiskStreamRead. CBankFile::ReadAsync
  // (0x82C72EA8) is a forwarder that leaves r9 for the inner file's implementation to fill.
  const uint32_t transfer_out_guest = ctx.r9.u32;
  if (refii::vfs::TryHandleRefiiHostDiskStreamRead(
          ctx, base, out_guest, stream_guest, arg5, offset, arg7,
          transfer_out_guest)) {
    NoteRefiiStreamMethod("ReadAsync", stream_guest, 16);
    refii::vfs::LogLooseStreamRead(
        "DiskStream_Read", base, stream_guest, arg4, arg5, arg6, ctx);
    return;
  }

  __imp__rex_lhStream_DiskStream_Read(ctx, base);
  refii::vfs::LogLooseStreamRead(
      "DiskStream_Read", base, stream_guest, arg4, arg5, arg6, ctx);
}

REX_EXTERN(rex_lhStream_DiskStream_Dtor) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.


  const uint32_t stream_guest = ctx.r3.u32;
  if (refii::vfs::IsRefiiHostDiskStream(base, stream_guest)) {
    NoteRefiiStreamMethod("Dtor", stream_guest, 40);

    // Last chance to serve a read deferred at creation: it is otherwise only collected when the
    // guest asks, and this caller issues one read and destroys the stream without asking.
    refii::vfs::CompleteRefiiDeferredReadsForStream(ctx, base, stream_guest);

    refii::vfs::EraseRefiiHostStreamRecord(stream_guest);
    refii::vfs::EraseLooseStreamRecord(stream_guest);
    refii::vfs::StoreGuestU32(base, stream_guest + 28, 0);
  }

  __imp__rex_lhStream_DiskStream_Dtor(ctx, base);
}

REX_EXTERN(rex_lhStream_Reader_Read) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhStream_Reader_Read);


  const uint32_t stream_guest = ctx.r3.u32;
  const uint32_t arg4 = ctx.r4.u32;
  const uint32_t arg5 = ctx.r5.u32;
  const uint32_t arg6 = ctx.r6.u32;
  __imp__rex_lhStream_Reader_Read(ctx, base);
  refii::vfs::LogLooseStreamRead(
      "Reader_Read", base, stream_guest, arg4, arg5, arg6, ctx);
}

REX_EXTERN(rex_lhStream_ReadUInt32) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhStream_ReadUInt32);


  const uint32_t stream_guest = ctx.r3.u32;
  const uint32_t arg4 = ctx.r4.u32;
  const uint32_t arg5 = ctx.r5.u32;
  const uint32_t arg6 = ctx.r6.u32;
  __imp__rex_lhStream_ReadUInt32(ctx, base);
  refii::vfs::LogLooseStreamRead(
      "ReadUInt32", base, stream_guest, arg4, arg5, arg6, ctx);
}

REX_EXTERN(rex_lhStream_Buffered_Init) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhStream_Buffered_Init);


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

REX_EXTERN(rex_lhStream_CreateBuffered) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhStream_CreateBuffered);


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

REX_EXTERN(rex_lhStream_CreateFromPosition) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhStream_CreateFromPosition);


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

REX_EXTERN(rex_lhStream_CreateReadStream) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhStream_CreateReadStream);


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

REX_EXTERN(rex_lhStream_CreateSubStream) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhStream_CreateSubStream);


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

REX_EXTERN(rex_lhStream_CreateReader) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhStream_CreateReader);


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

REX_EXTERN(rex_lhStream_Reader_Ctor) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhStream_Reader_Ctor);


  const uint32_t reader_guest = ctx.r3.u32;
  const uint32_t source_stream_guest = ctx.r4.u32;
  refii::vfs::LogLooseStreamWrapperCall(
      "Reader_Ctor", base, ctx, reader_guest, source_stream_guest);
  __imp__rex_lhStream_Reader_Ctor(ctx, base);
  refii::vfs::RecordLooseStreamAlias(
      base, "Reader_Ctor", reader_guest, source_stream_guest);
}
