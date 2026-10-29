#include "vfs_hooks_private.h"
#include "core/guest_build.h"

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

REX_EXTERN(rex_lhFontGlyphVector_Resize) {
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
      const uint32_t kFontParseReturn = refii::guest::Address(0x82B70E08u);
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

  __imp__rex_lhFontGlyphVector_Resize(ctx, base);
}

REX_EXTERN(rex_lhStream_ReturnENotImpl) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it returns before the check that recognises Refii-owned
  // objects. The call to the original at the end is the passthrough for the guest's own.

  if (refii::vfs::TryHandleRefiiHostDiskStreamNoopSuccess(
          ctx, base, ctx.r3.u32, "CompositeStream_UnsupportedNoop")) {
    NoteRefiiStreamMethod("composite_unsupported_noop", ctx.r3.u32, -1);
    return;
  }


  __imp__rex_lhStream_ReturnENotImpl(ctx, base);
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

REX_EXTERN(rex_lhStream_ReturnEFail) {
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

  __imp__rex_lhStream_ReturnEFail(ctx, base);
}

REX_EXTERN(rex_lhStream_DiskStream_ReadDirect) {
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

  __imp__rex_lhStream_DiskStream_ReadDirect(ctx, base);
  refii::vfs::LogLooseStreamRead(
      "DiskStream_DirectRead",
      base,
      stream_guest,
      buffer_guest,
      offset_low,
      requested_bytes,
      ctx);
}

REX_EXTERN(rex_lhStream_DiskStream_GetInnerSize) {
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

  __imp__rex_lhStream_DiskStream_GetInnerSize(ctx, base);
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

REX_EXTERN(rex_lhBank_OpenStreamByName) {
  REX_FUNC_PROLOGUE();

  // Arguments:
  // r3: int* out_guest (sret pointer for returned stream)
  // r4: CBankFile* bank_guest
  // r5: const FableString* name_obj_guest
  // r6: int mode
  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = original_ctx.r3.u32;
  const uint32_t bank_guest = original_ctx.r4.u32;
  const uint32_t name_obj_guest = original_ctx.r5.u32;
  const uint32_t mode = original_ctx.r6.u32;

  if (out_guest != 0 && name_obj_guest != 0) {
    std::string path =
        refii::vfs::ReadGuestWideStringObject(base, name_obj_guest, 4096);
    if (!path.empty()) {
      std::string normalized = refii::vfs::NormalizeLooseAssetKey(path);
      std::string override_key;
      if (refii::vfs::HostAssetIsOverride(path)) {
        override_key = path;
      } else if (refii::vfs::HostAssetIsOverride(normalized)) {
        override_key = normalized;
      } else if (normalized.starts_with("data/") &&
                 refii::vfs::HostAssetIsOverride(normalized.substr(5))) {
        override_key = normalized.substr(5);
      } else if (!normalized.starts_with("data/") &&
                 refii::vfs::HostAssetIsOverride("data/" + normalized)) {
        override_key = "data/" + normalized;
      }

      if (normalized.ends_with(".save")) {
        REXLOG_INFO("[vfs_bank] Bank file query for .save: '{}' (key: '{}') is_override={}",
                    path, override_key, !override_key.empty());
      }

      if (!override_key.empty()) {
        PPCContext open_ctx = original_ctx;
        const uint32_t stream_guest = refii::vfs::CreateRefiiHostMemoryStream(
            open_ctx, base, mode != 0 ? mode : 1, override_key, override_key);
        if (stream_guest != 0) {
          refii::vfs::StoreGuestU32(base, out_guest, stream_guest);
          ctx.r3.u32 = out_guest;
          REXLOG_INFO("[vfs_bank] Bank file intercepted: '{}' (key: '{}') -> serving Godot memory stream 0x{:08X}",
                      path, override_key, stream_guest);
          return;
        }
      }
    }
  }

  __imp__rex_lhBank_OpenStreamByName(ctx, base);
}

