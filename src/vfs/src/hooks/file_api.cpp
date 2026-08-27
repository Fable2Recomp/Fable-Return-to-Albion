#include "vfs_hooks_private.h"
#include <rex/system/xthread.h>
#include "runtime/replacement_paths.h"

REX_EXTERN(rex_CreateFileA) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_CreateFileA);


  const PPCContext original_ctx = ctx;
  const std::string path =
      refii::vfs::FormatGuestAsciiBytes(base, ctx.r3.u32, 4096);
  const bool trace =
      refii::vfs::ShouldLogFileApiPath("CreateFileA", path);
  if (trace) {
    REXLOG_INFO("VFS trace CreateFileA enter lr=0x{:08X} path={} "
                "access=0x{:08X} share=0x{:08X} disposition={} "
                "flags=0x{:08X} template=0x{:08X}",
                original_ctx.lr,
                path,
                original_ctx.r4.u32,
                original_ctx.r5.u32,
                original_ctx.r7.u32,
                original_ctx.r8.u32,
                original_ctx.r9.u32);
    bool is_directory = false;
    refii::vfs::LooseAssetResolution resolution;
    std::string candidate;
    if (refii::vfs::ResolveRefiiVirtualFileSystemProbe(
            path, &is_directory, &resolution, &candidate) &&
        !is_directory) {
      REXLOG_INFO("VFS trace CreateFileA Refii candidate pending host-handle "
                  "mapping path={} candidate={} provider_relative={} host={}",
                  path,
                  candidate,
                  resolution.provider_relative_path,
                  resolution.host_path.string());
    }
  }

  uint32_t refii_handle = refii::vfs::kRefiiPrivateHandleInvalid;
  std::string directory_candidate;
  if (refii::vfs::TryCreateRefiiPrivateDirectoryHandle(path,
                                                       original_ctx.r4.u32,
                                                       original_ctx.r7.u32,
                                                       &refii_handle,
                                                       &directory_candidate)) {
    ctx.r3.u32 = refii_handle;
    if (trace || refii::vfs::VfsTracingEnabled()) {
      REXLOG_INFO("Refii VFS CreateFileA private directory handle lr=0x{:08X} "
                  "path={} handle=0x{:08X} candidate={}",
                  original_ctx.lr,
                  path,
                  refii_handle,
                  directory_candidate);
    }
    return;
  }

  refii::vfs::LooseAssetResolution resolution;
  std::string candidate;
  bool replacement_claimed = false;
  if (refii::vfs::TryCreateRefiiPrivateFileHandle(path,
                                                       original_ctx.r4.u32,
                                                       original_ctx.r7.u32,
                                                       &refii_handle,
                                                       &resolution,
                                                       &candidate,
                                                       &replacement_claimed)) {
    ctx.r3.u32 = refii_handle;
    if (trace || refii::vfs::VfsTracingEnabled()) {
      REXLOG_INFO("Refii VFS CreateFileA private handle lr=0x{:08X} path={} "
                  "handle=0x{:08X} candidate={} provider_relative={} host={}",
                  original_ctx.lr,
                  path,
                  refii_handle,
                  candidate,
                  resolution.provider_relative_path,
                  resolution.host_path.string());
    }
    return;
  }
  if (replacement_claimed) {
    ctx.r3.u32 = refii::vfs::kRefiiPrivateHandleInvalid;
    if (trace || refii::vfs::VfsTracingEnabled()) {
      REXLOG_INFO("Refii VFS CreateFileA replacement miss lr=0x{:08X} path={} "
                  "access=0x{:08X} disposition={} handle=0x{:08X}",
                  original_ctx.lr,
                  path,
                  original_ctx.r4.u32,
                  original_ctx.r7.u32,
                  ctx.r3.u32);
    }
    return;
  }

  if ((refii::vfs::IsCachedOptionalSyntheticVfsMiss(path) ||
       refii::vfs::IsOptionalSyntheticVfsProbe(path) ||
       refii::vfs::IsKnownAbsentBootProbe(path))) {
    refii::vfs::SetRefiiWin32LastError(
        refii::vfs::kRefiiWin32ErrorFileNotFound);
    ctx.r3.u32 = refii::vfs::kRefiiPrivateHandleInvalid;
    if (trace || (refii::vfs::VfsTracingEnabled() &&
                  refii::vfs::ShouldLogRepeatedVfsTraceOperation(
                      "CreateFileAOptionalMiss", 32))) {
      REXLOG_INFO("Refii VFS CreateFileA optional miss lr=0x{:08X} path={} "
                  "handle=0x{:08X}",
                  original_ctx.lr,
                  path,
                  ctx.r3.u32);
    }
    return;
  }

  __imp__rex_CreateFileA(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace CreateFileA exit lr=0x{:08X} path={} "
                "handle=0x{:08X}",
                original_ctx.lr,
                path,
                ctx.r3.u32);
  }
}

REX_EXTERN(rex_ReadFile) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_ReadFile);


  const PPCContext original_ctx = ctx;
  if (refii::vfs::IsRefiiPrivateHandle(original_ctx.r3.u32) &&
      refii::vfs::TryReadRefiiPrivateFileHandle(ctx,
                                                    base,
                                                    original_ctx.r3.u32,
                                                    original_ctx.r4.u32,
                                                    original_ctx.r5.u32,
                                                    original_ctx.r6.u32,
                                                    original_ctx.r7.u32)) {
    if (refii::vfs::VfsTracingEnabled() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "ReadFileRefiiPrivate", 64)) {
      REXLOG_INFO("Refii VFS ReadFile private handle lr=0x{:08X} "
                  "handle=0x{:08X} buffer=0x{:08X} requested={} "
                  "bytes_out=0x{:08X} overlapped=0x{:08X} result={}",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  original_ctx.r4.u32,
                  original_ctx.r5.u32,
                  original_ctx.r6.u32,
                  original_ctx.r7.u32,
                  ctx.r3.u32);
    }
    return;
  }

  __imp__rex_ReadFile(ctx, base);
}

// The native rex_ReadFileAsync (sub_82CC7DD0) is a thin NtReadFile wrapper:
// it reads the byte offset from the IOSB extension (+8/+12), passes r7 as the
// ApcContext, and registers sub_82CD0760 as the IO-completion APC. The game's
// async read objects are completed BY THAT APC, so serving the read without
// queueing it leaves the async consumer waiting forever (observed: Bink video
// streaming stalls after the initial buffer -- first logo plays, the second
// movie hangs). Mirror the kernel semantics from rexglue NtReadFile_entry:
// complete synchronously, then EnqueueApc(routine & ~1, ctx, iosb, 0).
constexpr uint32_t kReadFileAsyncCompletionApc = 0x82CD0760u;

REX_EXTERN(rex_ReadFileAsync) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_ReadFileAsync);


  const PPCContext original_ctx = ctx;
  if (refii::vfs::IsRefiiPrivateHandle(original_ctx.r3.u32) &&
      refii::vfs::TryReadRefiiPrivateFileHandle(ctx,
                                                    base,
                                                    original_ctx.r3.u32,
                                                    original_ctx.r4.u32,
                                                    original_ctx.r5.u32,
                                                    0,
                                                    original_ctx.r6.u32)) {
    if (original_ctx.r7.u32 != 0) {
      if (auto* thread = rex::system::XThread::GetCurrentThread()) {
        thread->EnqueueApc(kReadFileAsyncCompletionApc,
                           original_ctx.r7.u32,
                           original_ctx.r6.u32,
                           0);
      }
    }
    if (refii::vfs::VfsTracingEnabled() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "ReadFileAsyncRefiiPrivate", 64)) {
      REXLOG_INFO("Refii VFS ReadFileAsync private handle lr=0x{:08X} "
                  "handle=0x{:08X} buffer=0x{:08X} requested={} "
                  "overlapped=0x{:08X} event=0x{:08X} result={}",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  original_ctx.r4.u32,
                  original_ctx.r5.u32,
                  original_ctx.r6.u32,
                  original_ctx.r7.u32,
                  ctx.r3.u32);
    }
    return;
  }

  __imp__rex_ReadFileAsync(ctx, base);
}

REX_EXTERN(rex_WriteFile) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_WriteFile);


  const PPCContext original_ctx = ctx;
  if (refii::vfs::IsRefiiPrivateHandle(original_ctx.r3.u32)) {
    if (refii::vfs::TryWriteRefiiPrivateFileHandle(ctx,
                                                   base,
                                                   original_ctx.r3.u32,
                                                   original_ctx.r4.u32,
                                                   original_ctx.r5.u32,
                                                   original_ctx.r6.u32,
                                                   original_ctx.r7.u32)) {
      if (refii::vfs::VfsTracingEnabled() &&
          refii::vfs::ShouldLogRepeatedVfsTraceOperation(
              "WriteFileRefiiPrivate", 64)) {
        REXLOG_INFO("Refii VFS WriteFile private handle lr=0x{:08X} "
                    "handle=0x{:08X} buffer=0x{:08X} requested={} "
                    "bytes_out=0x{:08X} overlapped=0x{:08X} result={}",
                    original_ctx.lr,
                    original_ctx.r3.u32,
                    original_ctx.r4.u32,
                    original_ctx.r5.u32,
                    original_ctx.r6.u32,
                    original_ctx.r7.u32,
                    ctx.r3.u32);
      }
      return;
    }
    ctx.r3.u32 = 0;
    return;
  }

  __imp__rex_WriteFile(ctx, base);
}

REX_EXTERN(rex_GetFileSizeEx) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_GetFileSizeEx);


  const PPCContext original_ctx = ctx;
  uint64_t size = 0;
  if (refii::vfs::TryGetRefiiPrivateFileSize(original_ctx.r3.u32, &size)) {
    if (original_ctx.r4.u32 != 0) {
      refii::vfs::StoreGuestU64(base, original_ctx.r4.u32, size);
    }
    ctx.r3.u32 = 1;
    if (refii::vfs::VfsTracingEnabled() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "GetFileSizeExRefiiPrivate", 64)) {
      REXLOG_INFO("Refii VFS GetFileSizeEx private handle lr=0x{:08X} "
                  "handle=0x{:08X} size={} out=0x{:08X}",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  size,
                  original_ctx.r4.u32);
    }
    return;
  }

  __imp__rex_GetFileSizeEx(ctx, base);
}

REX_EXTERN(rex_GetFileSize) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_GetFileSize);


  const PPCContext original_ctx = ctx;
  uint64_t size = 0;
  if (refii::vfs::TryGetRefiiPrivateFileSize(original_ctx.r3.u32, &size)) {
    if (original_ctx.r4.u32 != 0) {
      refii::vfs::StoreGuestU32(
          base, original_ctx.r4.u32, static_cast<uint32_t>(size >> 32));
    }
    ctx.r3.u32 = static_cast<uint32_t>(size);
    if (refii::vfs::VfsTracingEnabled() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "GetFileSizeRefiiPrivate", 64)) {
      REXLOG_INFO("Refii VFS GetFileSize private handle lr=0x{:08X} "
                  "handle=0x{:08X} size={} high_out=0x{:08X}",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  size,
                  original_ctx.r4.u32);
    }
    return;
  }

  __imp__rex_GetFileSize(ctx, base);
}

REX_EXTERN(rex_GetFileInformation) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_GetFileInformation);


  const PPCContext original_ctx = ctx;
  if (refii::vfs::TryGetRefiiPrivateFileInformation(
          base, original_ctx.r3.u32, original_ctx.r4.u32)) {
    refii::vfs::SetRefiiWin32LastError(
        refii::vfs::kRefiiWin32ErrorSuccess);
    ctx.r3.u32 = 1;
    if (refii::vfs::VfsTracingEnabled() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "GetFileInformationRefiiPrivate", 64)) {
      REXLOG_INFO("Refii VFS GetFileInformation private handle lr=0x{:08X} "
                  "handle=0x{:08X} out=0x{:08X} words={}",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  original_ctx.r4.u32,
                  refii::vfs::FormatGuestWords(base, original_ctx.r4.u32, 13));
    }
    return;
  }

  __imp__rex_GetFileInformation(ctx, base);
}

REX_EXTERN(rex_GetFileSectorInfo) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_GetFileSectorInfo);


  const PPCContext original_ctx = ctx;
  uint32_t sector_size = 0;
  if (refii::vfs::TryGetRefiiPrivateFileSectorInfo(original_ctx.r3.u32,
                                                   &sector_size)) {
    refii::vfs::SetRefiiWin32LastError(
        refii::vfs::kRefiiWin32ErrorSuccess);
    ctx.r3.u32 = sector_size;
    if (refii::vfs::VfsTracingEnabled() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "GetFileSectorInfoRefiiPrivate", 64)) {
      REXLOG_INFO("Refii VFS GetFileSectorInfo private handle lr=0x{:08X} "
                  "handle=0x{:08X} sector_size={}",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  sector_size);
    }
    return;
  }

  __imp__rex_GetFileSectorInfo(ctx, base);
}

REX_EXTERN(rex_GetFileTime) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_GetFileTime);


  const PPCContext original_ctx = ctx;
  if (refii::vfs::TryGetRefiiPrivateFileTimes(base,
                                              original_ctx.r3.u32,
                                              original_ctx.r4.u32,
                                              original_ctx.r5.u32,
                                              original_ctx.r6.u32)) {
    refii::vfs::SetRefiiWin32LastError(
        refii::vfs::kRefiiWin32ErrorSuccess);
    ctx.r3.u32 = 1;
    if (refii::vfs::VfsTracingEnabled() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "GetFileTimeRefiiPrivate", 64)) {
      REXLOG_INFO("Refii VFS GetFileTime private handle lr=0x{:08X} "
                  "handle=0x{:08X} creation=0x{:08X} access=0x{:08X} "
                  "write=0x{:08X}",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  original_ctx.r4.u32,
                  original_ctx.r5.u32,
                  original_ctx.r6.u32);
    }
    return;
  }

  __imp__rex_GetFileTime(ctx, base);
}

REX_EXTERN(rex_SetFilePointer) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_SetFilePointer);


  const PPCContext original_ctx = ctx;
  if (refii::vfs::TrySetRefiiPrivateFilePointer(ctx,
                                                    base,
                                                    original_ctx.r3.u32,
                                                    original_ctx.r4.s32,
                                                    original_ctx.r5.u32,
                                                    original_ctx.r6.u32)) {
    if (refii::vfs::VfsTracingEnabled() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "SetFilePointerRefiiPrivate", 64)) {
      REXLOG_INFO("Refii VFS SetFilePointer private handle lr=0x{:08X} "
                  "handle=0x{:08X} distance={} high=0x{:08X} method={} "
                  "result=0x{:08X}",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  original_ctx.r4.s32,
                  original_ctx.r5.u32,
                  original_ctx.r6.u32,
                  ctx.r3.u32);
    }
    return;
  }

  __imp__rex_SetFilePointer(ctx, base);
}

REX_EXTERN(rex_SetEndOfFile) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_SetEndOfFile);


  const PPCContext original_ctx = ctx;
  if (refii::vfs::IsRefiiPrivateHandle(original_ctx.r3.u32)) {
    if (refii::vfs::TrySetEndOfRefiiPrivateFile(ctx, original_ctx.r3.u32)) {
      if (refii::vfs::VfsTracingEnabled() &&
          refii::vfs::ShouldLogRepeatedVfsTraceOperation(
              "SetEndOfFileRefiiPrivate", 64)) {
        REXLOG_INFO("Refii VFS SetEndOfFile private handle lr=0x{:08X} "
                    "handle=0x{:08X} result={}",
                    original_ctx.lr,
                    original_ctx.r3.u32,
                    ctx.r3.u32);
      }
      return;
    }
    ctx.r3.u32 = 0;
    return;
  }

  __imp__rex_SetEndOfFile(ctx, base);
}

REX_EXTERN(rex_FlushFileBuffers) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_FlushFileBuffers);


  const PPCContext original_ctx = ctx;
  if (refii::vfs::IsRefiiPrivateHandle(original_ctx.r3.u32)) {
    if (refii::vfs::TryFlushRefiiPrivateFileHandle(ctx, original_ctx.r3.u32)) {
      if (refii::vfs::VfsTracingEnabled() &&
          refii::vfs::ShouldLogRepeatedVfsTraceOperation(
              "FlushFileBuffersRefiiPrivate", 64)) {
        REXLOG_INFO("Refii VFS FlushFileBuffers private handle lr=0x{:08X} "
                    "handle=0x{:08X} result={}",
                    original_ctx.lr,
                    original_ctx.r3.u32,
                    ctx.r3.u32);
      }
      return;
    }
    ctx.r3.u32 = 0;
    return;
  }

  __imp__rex_FlushFileBuffers(ctx, base);
}

REX_EXTERN(rex_CloseHandle) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_CloseHandle);


  const PPCContext original_ctx = ctx;
  if (refii::vfs::IsRefiiPrivateHandle(original_ctx.r3.u32)) {
    const bool closed =
        refii::vfs::CloseRefiiPrivateHandle(original_ctx.r3.u32);
    ctx.r3.u32 = closed ? 1 : 0;
    if (refii::vfs::VfsTracingEnabled() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "CloseHandleRefiiPrivate", 64)) {
      REXLOG_INFO("Refii VFS CloseHandle private handle lr=0x{:08X} "
                  "handle=0x{:08X} result={} active_handles={}/{}",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  ctx.r3.u32,
                  refii::vfs::LooseStats()
                      .active_private_file_handles.load(
                          std::memory_order_relaxed),
                  refii::vfs::LooseStats()
                      .active_private_find_handles.load(
                          std::memory_order_relaxed));
    }
    return;
  }

  __imp__rex_CloseHandle(ctx, base);
}

REX_EXTERN(rex_DeleteFileA) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_DeleteFileA);


  const PPCContext original_ctx = ctx;
  const std::string path =
      refii::vfs::FormatGuestAsciiBytes(base, ctx.r3.u32, 4096);
  const bool trace =
      refii::vfs::ShouldLogFileApiPath("DeleteFileA", path);
  refii::vfs::LooseAssetResolution resolution;
  std::string candidate;
  if (refii::vfs::TryDeleteRefiiPrivateFile(
          ctx, path, &resolution, &candidate)) {
    if (trace || refii::vfs::VfsTracingEnabled()) {
      REXLOG_INFO("Refii VFS DeleteFileA private path lr=0x{:08X} path={} "
                  "candidate={} provider_relative={} host={} result={}",
                  original_ctx.lr,
                  path,
                  candidate,
                  resolution.provider_relative_path,
                  resolution.host_path.string(),
                  ctx.r3.u32);
    }
    return;
  }

  if (trace) {
    REXLOG_INFO("VFS trace DeleteFileA enter lr=0x{:08X} path={}",
                original_ctx.lr,
                path);
  }

  __imp__rex_DeleteFileA(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace DeleteFileA exit lr=0x{:08X} path={} result={}",
                original_ctx.lr,
                path,
                ctx.r3.u32);
  }
}

REX_EXTERN(rex_GetFileAttributesA) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_GetFileAttributesA);


  const PPCContext original_ctx = ctx;
  const std::string path =
      refii::vfs::FormatGuestAsciiBytes(base, ctx.r3.u32, 4096);
  const bool trace =
      refii::vfs::ShouldLogFileApiPath("GetFileAttributesA", path);

  bool is_directory = false;
  refii::vfs::LooseAssetResolution resolution;
  std::string candidate;
  if (refii::vfs::ResolveRefiiVirtualFileSystemProbe(
          path, &is_directory, &resolution, &candidate)) {
    refii::vfs::SetRefiiWin32LastError(
        refii::vfs::kRefiiWin32ErrorSuccess);
    ctx.r3.u64 = is_directory ? 0x00000010u : 0x00000081u;

    // An unservable movie must answer "not there": `sub_8286E91C` gates BinkOpen on these
    // attributes, and on a failed open `sub_82A41B48` stores the null handle unconditionally
    // (`stw r3, 0x40(r30)` at 0x82A41BFC) and uses it twice a frame (`BinkDoFrame` at 0x82A41F34,
    // `lwz r10, 0xBC(r10)` at 0x82A42244). Scoped to `.bik` with no host file — videos ship loose,
    // so only for them does a missing host path mean unservable.
    if (!is_directory && path.size() >= 4u &&
        path.compare(path.size() - 4u, 4u, ".bik") == 0) {
      std::error_code missing_ec;
      // A host-served asset has no path to stat; its presence is Godot's answer.
      const bool servable =
          resolution.host_served
              ? HostFileSize(resolution) > 0
              : (!resolution.host_path.empty() &&
                 std::filesystem::exists(resolution.host_path, missing_ec));
      if (!servable) {
        refii::vfs::SetRefiiWin32LastError(refii::vfs::kRefiiWin32ErrorFileNotFound);
        ctx.r3.u32 = 0xFFFFFFFFu;
        REXLOG_INFO("Refii VFS: '{}' probes as present but has no servable file — answering "
                    "NOT FOUND so the guest never opens it. Claiming it exists makes BinkOpen "
                    "return 0, which the guest stores unchecked and dereferences at 0x82A42244.",
                    path);
        return;
      }
    }

    if (trace) {
      REXLOG_INFO("Refii VFS GetFileAttributesA hit lr=0x{:08X} path={} "
                  "candidate={} kind={} attributes=0x{:08X} host={}",
                  original_ctx.lr,
                  path,
                  candidate,
                  is_directory ? "directory" : "file",
                  ctx.r3.u32,
                  is_directory ? "" : resolution.host_path.string());
    }
    return;
  }

  refii::vfs::RefiiReplacementPathResolution replacement;
  if (refii::vfs::ResolveRefiiReplacementPath(path, &replacement)) {
    refii::vfs::SetRefiiWin32LastError(
        refii::vfs::kRefiiWin32ErrorFileNotFound);
    ctx.r3.u32 = 0xFFFFFFFFu;
    if (trace || refii::vfs::VfsTracingEnabled()) {
      REXLOG_INFO("Refii VFS GetFileAttributesA replacement miss lr=0x{:08X} "
                  "path={} candidate={} host={}",
                  original_ctx.lr,
                  path,
                  replacement.resolution.provider_relative_path,
                  replacement.resolution.host_path.string());
    }
    return;
  }

  if (trace) {
    REXLOG_INFO("VFS trace GetFileAttributesA enter lr=0x{:08X} path={}",
                original_ctx.lr,
                path);
  }

  __imp__rex_GetFileAttributesA(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace GetFileAttributesA exit lr=0x{:08X} path={} "
                "attributes=0x{:08X}",
                original_ctx.lr,
                path,
                ctx.r3.u32);
  }
}

REX_EXTERN(rex_FindNextFileA) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_FindNextFileA);


  const PPCContext original_ctx = ctx;
  if (refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation("FindNextFileA", 16)) {
    REXLOG_INFO("VFS trace FindNextFileA enter lr=0x{:08X} handle=0x{:08X} "
                "find_data=0x{:08X}",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32);
  }

  if (refii::vfs::IsRefiiPrivateHandle(original_ctx.r3.u32) &&
      refii::vfs::TryFindNextRefiiPrivate(
          ctx, base, original_ctx.r3.u32, original_ctx.r4.u32)) {
    if (refii::vfs::VfsTracingEnabled() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "FindNextFileARefiiPrivate", 16)) {
      REXLOG_INFO("Refii VFS FindNextFileA private handle lr=0x{:08X} "
                  "handle=0x{:08X} result=0x{:08X}",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  ctx.r3.u32);
    }
    return;
  }

  __imp__rex_FindNextFileA(ctx, base);

  if (refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation("FindNextFileA", 16)) {
    REXLOG_INFO("VFS trace FindNextFileA exit lr=0x{:08X} handle=0x{:08X} "
                "result=0x{:08X} find_data_words={}",
                original_ctx.lr,
                original_ctx.r3.u32,
                ctx.r3.u32,
                refii::vfs::FormatGuestWords(base, original_ctx.r4.u32, 16));
  }
}

REX_EXTERN(rex_FindFirstFileA) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_FindFirstFileA);


  const PPCContext original_ctx = ctx;
  const std::string path =
      refii::vfs::FormatGuestAsciiBytes(base, ctx.r3.u32, 4096);
  const bool trace =
      refii::vfs::ShouldLogFileApiPath("FindFirstFileA", path);
  if (trace) {
    REXLOG_INFO("VFS trace FindFirstFileA enter lr=0x{:08X} path={} "
                "find_data=0x{:08X}",
                original_ctx.lr,
                path,
                original_ctx.r4.u32);
  }

  if (refii::vfs::TryFindFirstRefiiPrivate(
          ctx, base, path, original_ctx.r4.u32)) {
    if (trace ||
        (refii::vfs::VfsTracingEnabled() &&
         refii::vfs::ShouldLogRepeatedVfsTraceOperation(
             "FindFirstFileARefiiPrivate", 64))) {
      REXLOG_INFO("Refii VFS FindFirstFileA private result lr=0x{:08X} "
                  "path={} handle=0x{:08X} find_data_words={}",
                  original_ctx.lr,
                  path,
                  ctx.r3.u32,
                  refii::vfs::FormatGuestWords(
                      base, original_ctx.r4.u32, 16));
    }
    return;
  }

  __imp__rex_FindFirstFileA(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace FindFirstFileA exit lr=0x{:08X} path={} "
                "handle=0x{:08X} find_data_words={}",
                original_ctx.lr,
                path,
                ctx.r3.u32,
                refii::vfs::FormatGuestWords(base, original_ctx.r4.u32, 16));
  }
}

REX_EXTERN(rex_lh_OpenDiscFile) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lh_OpenDiscFile);


  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t provider_guest = ctx.r4.u32;
  const uint32_t name_guest = ctx.r5.u32;
  const uint32_t mode = ctx.r6.u32;
  const std::string provider =
      refii::vfs::ReadGuestWideStringObject(base, provider_guest + 8, 4096);
  const std::string name =
      refii::vfs::ReadGuestWideStringObject(base, name_guest, 4096);
  const std::vector<std::string> asset_candidates =
      refii::vfs::BuildProviderOpenAssetPathCandidates(provider, name);
  const std::string asset_path =
      asset_candidates.empty() ? refii::vfs::NormalizeLooseAssetKey(name)
                               : asset_candidates.front();
  const bool trace =
      refii::vfs::ShouldLogFileApiPath("OpenDiscFile", asset_path) ||
      refii::vfs::ShouldLogFileApiPath("OpenDiscFile", name) ||
      refii::vfs::ShouldLogFileApiPath("OpenDiscFile", provider);

  if (refii::vfs::g_suppress_request_manager_hash_entry_loose_streams == 0 &&
      out_guest != 0 && !asset_candidates.empty()) {
    refii::vfs::RefiiVfsOpenResult open_result;
    std::string opened_asset_path;
    for (const std::string& candidate : asset_candidates) {
      ctx = original_ctx;
      refii::vfs::TryOpenRefiiVfsAsset(
          ctx,
          base,
          original_ctx,
          refii::vfs::RefiiVfsOpenShape::RawStream,
          refii::vfs::RefiiVfsOpenRequest{
              .api = "OpenDiscFile",
              .request_name = candidate,
              .out_guest = out_guest,
              .manager_guest = provider_guest,
              .mode = mode,
              .caller_lr = static_cast<uint32_t>(original_ctx.lr),
              .prefer_resource_manager_texture_shape =
                  refii::vfs::IsTextureAssetCandidate(candidate),
              .allow_optional_null_miss = true,
          },
          &open_result);
      if (open_result.disposition ==
          refii::vfs::RefiiVfsOpenDisposition::Opened) {
        opened_asset_path = candidate;
        break;
      }
      if (open_result.disposition !=
          refii::vfs::RefiiVfsOpenDisposition::Miss) {
        opened_asset_path = candidate;
        break;
      }
    }
    if (open_result.disposition ==
        refii::vfs::RefiiVfsOpenDisposition::Opened) {
      if (trace || refii::vfs::VfsTracingEnabled() ||
          refii::vfs::LooseAssetOverrideTracingEnabled() ||
          refii::vfs::ShouldLogLooseStatsMilestone(open_result.hit_count)) {
        REXLOG_INFO("Refii VFS OpenDiscFile hit hit={} provider={} name={} "
                    "asset={} source={} candidate={} provider_relative={} "
                    "host={} stream=0x{:08X} mode=0x{:08X}",
                    open_result.hit_count,
                    provider,
                    name,
                    opened_asset_path.empty() ? asset_path : opened_asset_path,
                    open_result.source,
                    open_result.candidate,
                    open_result.resolution.provider_relative_path,
                    open_result.resolution.host_path.string(),
                    open_result.opened_guest,
                    mode);
      }
      return;
    }
    if (refii::vfs::IsHandledRefiiVfsOpenDisposition(
            open_result.disposition)) {
      return;
    }
    if (open_result.disposition ==
            refii::vfs::RefiiVfsOpenDisposition::OpenFailed &&
        (trace || refii::vfs::VfsTracingEnabled())) {
      REXLOG_WARN("Refii VFS OpenDiscFile matched asset={} name={}, but "
                  "host stream failed {}",
                  asset_path,
                  name,
                  open_result.resolution.host_path.string());
    }
  } else if (trace &&
             refii::vfs::g_suppress_request_manager_hash_entry_loose_streams != 0) {
    REXLOG_INFO("Refii VFS OpenDiscFile suppressed during ResourceManager cache "
                "lookup provider={} name={} asset={} mode=0x{:08X}",
                provider,
                name,
                asset_path,
                mode);
  }

  if (trace) {
    REXLOG_INFO("VFS trace OpenDiscFile enter lr=0x{:08X} out=0x{:08X} "
                "provider=0x{:08X} provider_path={} name=0x{:08X} "
                "name_path={} asset={} mode={} provider_words={} name_words={}",
                original_ctx.lr,
                out_guest,
                provider_guest,
                provider,
                name_guest,
                name,
                asset_path,
                mode,
                refii::vfs::FormatGuestWords(base, provider_guest, 12),
                refii::vfs::FormatGuestWords(base, name_guest, 8));
  }

  __imp__rex_lh_OpenDiscFile(ctx, base);

  if (trace) {
    const uint32_t file_guest =
        out_guest != 0 ? refii::vfs::TryReadOutputPointer(base, out_guest)
                       : 0;
    REXLOG_INFO("VFS trace OpenDiscFile exit lr=0x{:08X} out=0x{:08X} "
                "file=0x{:08X} result=0x{:08X} file_words={}",
                original_ctx.lr,
                out_guest,
                file_guest,
                ctx.r3.u32,
                refii::vfs::FormatGuestWords(base, file_guest, 14));
  }
}

REX_EXTERN(rex_lhFile_IsOpen) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhFile_IsOpen);


  const uint32_t file_guest = ctx.r3.u32;
  if (refii::vfs::IsRefiiOwnedLhFileStream(base, file_guest, 8) ||
      refii::vfs::IsRefiiOwnedLhFileStream(base, file_guest, 12)) {
    ctx.r3.u32 = 1;
    if (refii::vfs::VfsTracingEnabled() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "lhFile_IsOpen_refii", 24)) {
      REXLOG_INFO("Refii VFS lhFile IsOpen file=0x{:08X} result=1 "
                  "total=0x{:08X} read=0x{:08X} words={}",
                  file_guest,
                  refii::vfs::LoadGuestU32(base, file_guest + 8),
                  refii::vfs::LoadGuestU32(base, file_guest + 12),
                  refii::vfs::FormatGuestWords(base, file_guest, 12));
    }
    return;
  }

  __imp__rex_lhFile_IsOpen(ctx, base);
}

REX_EXTERN(rex_lhFile_GetSize) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhFile_GetSize);


  const uint32_t file_guest = ctx.r3.u32;
  uint32_t size = 0;
  if (refii::vfs::TryGetRefiiOwnedLhFileStreamSize(base,
                                                       file_guest,
                                                       12,
                                                       &size)) {
    ctx.r3.u32 = size;
    if (refii::vfs::VfsTracingEnabled() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "lhFile_GetSize_refii", 24)) {
      REXLOG_INFO("Refii VFS lhFile GetSize file=0x{:08X} "
                  "read_stream=0x{:08X} size={} words={}",
                  file_guest,
                  refii::vfs::LoadGuestU32(base, file_guest + 12),
                  size,
                  refii::vfs::FormatGuestWords(base, file_guest, 12));
    }
    return;
  }

  __imp__rex_lhFile_GetSize(ctx, base);
}

REX_EXTERN(rex_lhFile_GetTotalSize) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhFile_GetTotalSize);


  const uint32_t file_guest = ctx.r3.u32;
  uint32_t size = 0;
  if (refii::vfs::TryGetRefiiOwnedLhFileStreamSize(base,
                                                       file_guest,
                                                       8,
                                                       &size)) {
    ctx.r3.u32 = size;
    if (refii::vfs::VfsTracingEnabled() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "lhFile_GetTotalSize_refii", 24)) {
      REXLOG_INFO("Refii VFS lhFile GetTotalSize file=0x{:08X} "
                  "total_stream=0x{:08X} size={} words={}",
                  file_guest,
                  refii::vfs::LoadGuestU32(base, file_guest + 8),
                  size,
                  refii::vfs::FormatGuestWords(base, file_guest, 12));
    }
    return;
  }

  __imp__rex_lhFile_GetTotalSize(ctx, base);
}

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
              .prefer_resource_manager_texture_shape =
                  refii::vfs::IsTextureAssetCandidate(path),
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
