#include "vfs_hooks_private.h"

namespace {

bool IsNativeStartupBikCursorCaller(uint32_t link_register) {
  return link_register == 0x82377C98u || link_register == 0x822F4FC8u;
}

} // namespace

// The BIK-cursor audit's rate limit.
bool ShouldLogNativeStartupVideoNativeMiscAudit(std::string_view key,
                                                uint32_t limit) {
  static std::mutex mutex;
  static std::unordered_map<std::string, uint32_t> counts;

  std::lock_guard<std::mutex> lock(mutex);
  const uint32_t next_count = ++counts[std::string(key)];
  return next_count <= limit;
}

REX_EXTERN(sub_82C6EAD8) {
  REX_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t object_guest = original_ctx.r3.u32;
  const uint32_t stream_guest =
      object_guest != 0 ? refii::vfs::LoadGuestU32(base, object_guest + 8)
                        : 0;
  // The startup video's BIK cursor step, run by the guest.
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
    refii::vfs::RecordRefiiStartupVideoCursor(base, object_guest, stream_guest);
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
}

REX_EXTERN(sub_82C8C718) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

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

REX_EXTERN(sub_822C72E8) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

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

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

  if (refii::vfs::TryHandleRefiiReadRequestAddRef(
          ctx, base, ctx.r3.u32)) {
    return;
  }

  __imp__sub_822CBB98(ctx, base);
}

REX_EXTERN(sub_8290DD10) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

  if (refii::vfs::TryHandleRefiiReadRequestGetStatus(
          ctx, base, ctx.r3.u32, "ReadRequest_GetStatus")) {
    return;
  }

  __imp__sub_8290DD10(ctx, base);
}

REX_EXTERN(sub_82B54D28) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

  if (refii::vfs::TryHandleRefiiResourceWrapperDtor(
          ctx, base, ctx.r3.u32, ctx.r4.u32)) {
    return;
  }

  __imp__sub_82B54D28(ctx, base);
}

REX_EXTERN(sub_82B54250) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

  if (refii::vfs::TryHandleRefiiResourceWrapperWait(
          ctx, base, ctx.r3.u32, "ResourceWrapper_Wait")) {
    return;
  }

  __imp__sub_82B54250(ctx, base);
}

REX_EXTERN(sub_82B54268) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

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

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

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

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

  if (refii::vfs::TryHandleRefiiResourceWrapperSlot36(
          ctx, base, ctx.r3.u32, ctx.r4.u32)) {
    return;
  }

  __imp__sub_82B542F0(ctx, base);
}

REX_EXTERN(sub_82C0EE10) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

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

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

  if (refii::vfs::TryHandleRefiiReadRequestWait(
          ctx, base, ctx.r3.u32, "ReadRequest_Wait")) {
    return;
  }

  __imp__sub_82B5BFF8(ctx, base);
}

REX_EXTERN(sub_82B5C0C0) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

  if (refii::vfs::TryHandleRefiiReadRequestGetBytes(
          ctx, base, ctx.r3.u32, ctx.r4.u32)) {
    return;
  }

  __imp__sub_82B5C0C0(ctx, base);
}

REX_EXTERN(sub_82B5C0D8) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

  if (refii::vfs::TryHandleRefiiReadRequestGetOffset(ctx, ctx.r3.u32)) {
    return;
  }

  __imp__sub_82B5C0D8(ctx, base);
}

REX_EXTERN(sub_82B5C0E0) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

  if (refii::vfs::TryHandleRefiiReadRequestReturnSuccess(
          ctx, base, ctx.r3.u32, "ReadRequest_Notify")) {
    return;
  }

  __imp__sub_82B5C0E0(ctx, base);
}

REX_EXTERN(sub_82B5C110) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

  if (refii::vfs::TryHandleRefiiReadRequestReturnSuccess(
          ctx, base, ctx.r3.u32, "ReadRequest_SetCallback")) {
    return;
  }

  __imp__sub_82B5C110(ctx, base);
}

REX_EXTERN(sub_8252AA18) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

  if (refii::vfs::TryHandleRefiiReadRequestSmartPtrAssign(
          ctx, base, ctx.r3.u32, ctx.r4.u32)) {
    return;
  }

  __imp__sub_8252AA18(ctx, base);
}

REX_EXTERN(sub_82B4ED70) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

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


REX_EXTERN(sub_82C51CA0) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

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
