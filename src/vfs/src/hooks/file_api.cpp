#include "vfs_hooks_private.h"
#include <rex/system/xthread.h>

REX_EXTERN(rex_lhFile_ResolveAndOpen) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it would return before the Refii-owned check. The call to the
  // original at the end is the passthrough for the guest's own objects.

  const PPCContext original_ctx = ctx;
  const uint32_t file_guest = ctx.r3.u32;
  std::string path =
      refii::vfs::ReadGuestWideStringObject(base, original_ctx.r4.u32, 4096);
  if (path.empty() && file_guest != 0) {
    const uint32_t stored_name_guest =
        refii::vfs::LoadGuestU32(base, file_guest + 4);
    path =
        refii::vfs::ReadGuestWideStringObject(base, stored_name_guest, 4096);
  }
  if (path.empty()) {
    path =
        refii::vfs::ReadGuestWideStringObject(base, file_guest + 4, 4096);
  }
  const bool trace =
      refii::vfs::VfsVerboseIoTracingEnabled() &&
      refii::vfs::ShouldLogFileApiPath("lhFile_ResolveAndOpen", path);
  if (trace) {
    REXLOG_INFO("VFS trace lhFile_ResolveAndOpen enter lr=0x{:08X} "
                "file=0x{:08X} path={} r4=0x{:08X} mode={} words={} "
                "path_words={}",
                original_ctx.lr,
                file_guest,
                path,
                original_ctx.r4.u32,
                original_ctx.r5.u32,
                refii::vfs::FormatGuestWords(base, file_guest, 12),
                refii::vfs::FormatGuestWords(base, original_ctx.r4.u32, 8));
  }

  if (file_guest != 0 &&
      !path.empty()) {
    PPCContext open_ctx = original_ctx;
    const uint32_t out_guest = refii::vfs::CallLhMalloc(open_ctx, base, 4);
    if (out_guest != 0) {
      refii::vfs::StoreGuestU32(base, out_guest, 0);
      refii::vfs::RefiiVfsOpenResult open_result;
      refii::vfs::TryOpenRefiiVfsAsset(
          open_ctx,
          base,
          original_ctx,
          refii::vfs::RefiiVfsOpenShape::RawStream,
          refii::vfs::RefiiVfsOpenRequest{
              .api = "lhFile_ResolveAndOpen",
              .request_name = path,
              .out_guest = out_guest,
              .manager_guest = file_guest,
              .mode = original_ctx.r5.u32,
              .caller_lr = static_cast<uint32_t>(original_ctx.lr),
              .allow_optional_null_miss = true,
          },
          &open_result);

      const uint32_t stream_guest =
          refii::vfs::TryReadOutputPointer(base, out_guest);
      refii::vfs::CallLhFree(open_ctx, base, out_guest);

      if (open_result.disposition ==
          refii::vfs::RefiiVfsOpenDisposition::Opened) {
        const uint32_t old_total =
            refii::vfs::LoadGuestU32(base, file_guest + 8);
        const uint32_t old_read =
            refii::vfs::LoadGuestU32(base, file_guest + 12);
        if (refii::vfs::IsRefiiHostDiskStream(base, old_total)) {
          refii::vfs::ReleaseGuestObject(open_ctx, base, old_total);
        }
        if (old_read != old_total &&
            refii::vfs::IsRefiiHostDiskStream(base, old_read)) {
          refii::vfs::ReleaseGuestObject(open_ctx, base, old_read);
        }
        refii::vfs::StoreGuestU32(base, file_guest + 8, stream_guest);
        refii::vfs::StoreGuestU32(base, file_guest + 12, 0);
        ctx.r3.u32 = 1;
        if (trace || refii::vfs::VfsTracingEnabled()) {
          REXLOG_INFO("Refii VFS lhFile_ResolveAndOpen hit lr=0x{:08X} "
                      "file=0x{:08X} path={} stream=0x{:08X} source={} "
                      "candidate={} provider_relative={} host={} words={}",
                      original_ctx.lr,
                      file_guest,
                      path,
                      stream_guest,
                      open_result.source,
                      open_result.candidate,
                      open_result.resolution.provider_relative_path,
                      open_result.resolution.host_path.string(),
                      refii::vfs::FormatGuestWords(base, file_guest, 12));
        }
        return;
      }

      if (open_result.disposition ==
          refii::vfs::RefiiVfsOpenDisposition::OptionalNull) {
        refii::vfs::StoreGuestU32(base, file_guest + 8, 0);
        refii::vfs::StoreGuestU32(base, file_guest + 12, 0);
        ctx.r3.u32 = 1;
        if (trace || refii::vfs::VfsTracingEnabled()) {
          REXLOG_INFO("Refii VFS lhFile_ResolveAndOpen optional null lr=0x{:08X} "
                      "file=0x{:08X} path={} words={}",
                      original_ctx.lr,
                      file_guest,
                      path,
                      refii::vfs::FormatGuestWords(base, file_guest, 12));
        }
        return;
      }

      if (open_result.disposition ==
              refii::vfs::RefiiVfsOpenDisposition::OpenFailed &&
          (trace || refii::vfs::VfsTracingEnabled())) {
        REXLOG_WARN("Refii VFS lhFile_ResolveAndOpen matched path={}, but "
                    "host stream failed {}",
                    path,
                    open_result.resolution.host_path.string());
      }
    }
  }

  __imp__rex_lhFile_ResolveAndOpen(ctx, base);

  if (trace) {
    const uint32_t total_stream =
        file_guest != 0 ? refii::vfs::LoadGuestU32(base, file_guest + 8) : 0;
    const uint32_t read_stream =
        file_guest != 0 ? refii::vfs::LoadGuestU32(base, file_guest + 12) : 0;
    REXLOG_INFO("VFS trace lhFile_ResolveAndOpen exit lr=0x{:08X} "
                "file=0x{:08X} path={} result=0x{:08X} total_stream=0x{:08X} "
                "read_stream=0x{:08X} file_words={} total={} read={}",
                original_ctx.lr,
                file_guest,
                path,
                ctx.r3.u32,
                total_stream,
                read_stream,
                refii::vfs::FormatGuestWords(base, file_guest, 12),
                refii::vfs::DescribeGuestObject(base, total_stream),
                refii::vfs::DescribeGuestObject(base, read_stream));
  }
}
