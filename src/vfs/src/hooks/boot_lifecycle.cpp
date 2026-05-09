#include "vfs_hooks_private.h"

namespace {

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

bool ShouldLogNativeStartupFrameProgress() {
  if (!refii::vfs::kTraceFrontendHandoffDiagnostics ||
      !refii::vfs::VfsTracingEnabled() ||
      !refii::vfs::VfsVerboseIoTracingEnabled() ||
      !refii::vfs::RefiiLooseAssetBackendEnabled()) {
    return false;
  }

  static std::mutex mutex;
  static uint32_t count = 0;

  std::lock_guard<std::mutex> lock(mutex);
  ++count;
  return count <= 8 || (count % 20) == 0;
}

double RefiiStartupVideoFrameDelta(double native_delta,
                                   bool startup_playback_critical) {
  static std::mutex mutex;
  static std::chrono::steady_clock::time_point last_frame_at;

  std::lock_guard<std::mutex> lock(mutex);
  if (!startup_playback_critical) {
    last_frame_at = {};
    return native_delta;
  }

  // Keep startup playback timing native. Refii owns the streams, not the movie
  // player's frame clock; feeding larger deltas here makes the generated BIK
  // holder do non-native catch-up work and can stall visible playback.
  last_frame_at = std::chrono::steady_clock::now();
  return native_delta;
}

bool MaybeApplyNativeStartupVideoDurationCompletion(uint8_t* base,
                                                    uint32_t owner_guest,
                                                    uint32_t lr) {
  if (base == nullptr || !refii::vfs::LooksLikeGuestPointer(owner_guest)) {
    return false;
  }

  const uint32_t handoff_guest =
      refii::vfs::LoadGuestU32(base, owner_guest + 4);
  if (!refii::vfs::LooksLikeGuestPointer(handoff_guest)) {
    return false;
  }

  const uint8_t flag_233 = refii::vfs::LoadGuestU8(base, handoff_guest + 233);
  const uint8_t flag_244 = refii::vfs::LoadGuestU8(base, handoff_guest + 244);
  if (flag_233 != 0 || flag_244 != 0) {
    return false;
  }

  const float duration =
      refii::vfs::RefiiGuestF32FromWord(
          refii::vfs::LoadGuestU32(base, handoff_guest + 4));
  const float elapsed =
      refii::vfs::RefiiGuestF32FromWord(
          refii::vfs::LoadGuestU32(base, handoff_guest + 8));
  if (duration <= 0.0f || elapsed <= duration) {
    return false;
  }

  // Generated sub_82377DC8 performs this same elapsed > duration check and
  // marks byte 244. If that callback is starved while Refii owns the startup
  // BIK streams, mirror the native gate so the startup vector can advance
  // without inventing a different player ABI.
  refii::vfs::StoreGuestU8(base, handoff_guest + 244, 1);
  if (refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "StartupVideoDurationCompletionFallback", 8)) {
    REXLOG_INFO("Refii VFS native startup duration completion fallback "
                "lr=0x{:08X} owner=0x{:08X} handoff=0x{:08X} "
                "elapsed={} duration={} generated_equivalent=sub_82377DC8",
                lr,
                owner_guest,
                handoff_guest,
                elapsed,
                duration);
  }
  return true;
}

std::string ReadGameDataAudioLateBindName(uint8_t* base, uint32_t entry_guest) {
  if (base == nullptr || !refii::vfs::LooksLikeGuestPointer(entry_guest)) {
    return {};
  }
  const uint32_t string_holder =
      refii::vfs::LoadGuestU32(base, entry_guest + 0);
  if (!refii::vfs::LooksLikeGuestPointer(string_holder)) {
    return {};
  }
  const uint32_t string_guest =
      refii::vfs::LoadGuestU32(base, string_holder + 0);
  if (!refii::vfs::LooksLikeGuestPointer(string_guest)) {
    return {};
  }
  return refii::vfs::ReadBestEffortGuestString(base, string_guest, 512);
}

bool ShouldTraceGameDataBootBoundary(uint32_t lr) {
  if (!refii::vfs::kTraceFrontendHandoffDiagnostics ||
      !refii::vfs::VfsTracingEnabled() ||
      !refii::vfs::RefiiLooseAssetBackendEnabled()) {
    return false;
  }

  switch (lr) {
    case 0x822F4094:
    case 0x822F40CC:
    case 0x822F4124:
    case 0x822F413C:
    case 0x822F4140:
    case 0x822F4170:
    case 0x822F41B0:
    case 0x822F41F0:
    case 0x822F4208:
    case 0x822F443C:
    case 0x822F49E8:
      return true;
    default:
      return false;
  }
}

std::string FormatContentManagerIteratorState(uint8_t* base,
                                              uint32_t manager_guest) {
  if (base == nullptr || !refii::vfs::LooksLikeGuestPointer(manager_guest)) {
    return fmt::format("manager=0x{:08X}", manager_guest);
  }

  const uint32_t begin = refii::vfs::LoadGuestU32(base, manager_guest + 40);
  const uint32_t end = refii::vfs::LoadGuestU32(base, manager_guest + 44);
  const uint32_t capacity = refii::vfs::LoadGuestU32(base, manager_guest + 48);
  const uint32_t count =
      end >= begin && ((end - begin) % sizeof(uint32_t)) == 0
          ? (end - begin) / sizeof(uint32_t)
          : 0xFFFFFFFFu;
  uint32_t first_object = 0;
  uint32_t first_vtable = 0;
  uint32_t first_add_ref = 0;
  uint32_t first_release = 0;
  if (count != 0 && count != 0xFFFFFFFFu &&
      refii::vfs::LooksLikeGuestPointer(begin)) {
    first_object = refii::vfs::LoadGuestU32(base, begin);
    if (refii::vfs::LooksLikeGuestPointer(first_object)) {
      first_vtable = refii::vfs::LoadGuestU32(base, first_object);
      if (refii::vfs::LooksLikeGuestPointer(first_vtable)) {
        first_add_ref = refii::vfs::LoadGuestU32(base, first_vtable + 4);
        first_release = refii::vfs::LoadGuestU32(base, first_vtable + 8);
      }
    }
  }

  return fmt::format("manager=0x{:08X} list=[0x{:08X},0x{:08X},0x{:08X}] "
                     "count={} first=0x{:08X} vtable=0x{:08X} "
                     "add_ref=0x{:08X} release=0x{:08X}",
                     manager_guest,
                     begin,
                     end,
                     capacity,
                     count == 0xFFFFFFFFu ? std::string("invalid")
                                           : fmt::format("{}", count),
                     first_object,
                     first_vtable,
                     first_add_ref,
                     first_release);
}

std::string FormatGuestOutSlot(uint8_t* base, uint32_t out_guest) {
  if (base == nullptr || !refii::vfs::LooksLikeGuestPointer(out_guest)) {
    return fmt::format("out=0x{:08X}", out_guest);
  }
  return fmt::format("out=0x{:08X}->0x{:08X}",
                     out_guest,
                     refii::vfs::LoadGuestU32(base, out_guest));
}

std::string_view ContentIteratorCallbackName(uint32_t callback_guest) {
  switch (callback_guest) {
    case 0x822F3D90:
      return "DLCStartup";
    case 0x822F3B20:
      return "BootPackageCollect";
    case 0x822F6698:
      return "ResourceStringMatch";
    default:
      return "unknown";
  }
}

uint32_t ContentManagerIteratorFirstObject(uint8_t* base,
                                           uint32_t manager_guest) {
  if (base == nullptr || !refii::vfs::LooksLikeGuestPointer(manager_guest)) {
    return 0;
  }

  const uint32_t begin = refii::vfs::LoadGuestU32(base, manager_guest + 40);
  const uint32_t end = refii::vfs::LoadGuestU32(base, manager_guest + 44);
  if (!refii::vfs::LooksLikeGuestPointer(begin) || end < begin ||
      ((end - begin) % sizeof(uint32_t)) != 0 || begin == end) {
    return 0;
  }

  return refii::vfs::LoadGuestU32(base, begin);
}

bool TryRunRefiiDlcStartupContentFind(PPCContext& ctx,
                                      uint8_t* base,
                                      const PPCContext& original_ctx) {
  if (!refii::vfs::RefiiLooseAssetBackendEnabled() ||
      original_ctx.lr != 0x822F4094 || original_ctx.r5.u32 != 0x822F3D90 ||
      !refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)) {
    return false;
  }

  const uint32_t first_object =
      ContentManagerIteratorFirstObject(base, original_ctx.r4.u32);
  if (!refii::vfs::LooksLikeGuestPointer(first_object)) {
    return false;
  }

  PPCContext callback_ctx = original_ctx;
  callback_ctx.r3.u32 = first_object;
  callback_ctx.r4.u32 = original_ctx.r6.u32;
  callback_ctx.lr = 0x8237FEC8;

  if (refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS collapsed DLCStartup content find enter "
                "lr=0x{:08X} out=0x{:08X} request_manager=0x{:08X} "
                "arg=0x{:08X}",
                original_ctx.lr,
                original_ctx.r3.u32,
                first_object,
                original_ctx.r6.u32);
  }

  __imp__sub_822F3D90(callback_ctx, base);

  const bool keep_scanning = (callback_ctx.r3.u32 & 0xFF) != 0;
  if (keep_scanning) {
    refii::vfs::StoreGuestU32(base, original_ctx.r3.u32, 0);
  } else {
    refii::vfs::StoreGuestU32(base, original_ctx.r3.u32, first_object);
  }
  ctx = original_ctx;
  ctx.r3.u32 = original_ctx.r3.u32;

  if (refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS collapsed DLCStartup content find exit "
                "lr=0x{:08X} callback_result=0x{:08X} "
                "keep_scanning={} out=0x{:08X}->0x{:08X}",
                original_ctx.lr,
                callback_ctx.r3.u32,
                keep_scanning ? "true" : "false",
                original_ctx.r3.u32,
                refii::vfs::LoadGuestU32(base, original_ctx.r3.u32));
  }
  return true;
}

} // namespace

REX_EXTERN(rex_lhResource_InitModelHeaders) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhResource_InitModelHeaders);

  const PPCContext original_ctx = ctx;
  const bool log_trace =
      refii::vfs::kTraceMeshModelAudioDiagnostics &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation("InitModelHeaders");
  if (log_trace) {
    REXLOG_INFO("VFS trace InitModelHeaders enter lr=0x{:08X} "
                "streams={} active_streams={} synthetic_banks={}",
                original_ctx.lr,
                refii::vfs::LooseStats().streams_opened.load(
                    std::memory_order_relaxed),
                refii::vfs::RefiiHostStreamRecordCount(),
                refii::vfs::SyntheticBankRecordCount());
  }

  __imp__rex_lhResource_InitModelHeaders(ctx, base);

  if (log_trace) {
    REXLOG_INFO("VFS trace InitModelHeaders exit lr=0x{:08X} "
                "result=0x{:08X} streams={} active_streams={} "
                "synthetic_banks={}",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::LooseStats().streams_opened.load(
                    std::memory_order_relaxed),
                refii::vfs::RefiiHostStreamRecordCount(),
                refii::vfs::SyntheticBankRecordCount());
  }
}

REX_EXTERN(rex_lhScopedLock_Acquire) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhScopedLock_Acquire);

  refii::vfs::SanitizeCriticalSectionBeforeEnter(base, ctx.r4.u32, ctx.lr);
  __imp__rex_lhScopedLock_Acquire(ctx, base);
}

REX_EXTERN(sub_8218FA68) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_8218FA68);

  const std::lock_guard lock(refii::vfs::RenderSharedStateMutex());
  __imp__sub_8218FA68(ctx, base);
}

REX_EXTERN(sub_82B75328) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B75328);

  const PPCContext original_ctx = ctx;
  const bool log_trace =
      refii::vfs::kTraceMeshModelAudioDiagnostics &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation("sub_82B75328");
  if (log_trace) {
    REXLOG_INFO("VFS trace InitModelHeaders phase enter sub_82B75328 "
                "lr=0x{:08X}",
                original_ctx.lr);
  }

  __imp__sub_82B75328(ctx, base);

  if (log_trace) {
    REXLOG_INFO("VFS trace InitModelHeaders phase exit sub_82B75328 "
                "lr=0x{:08X} result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(sub_82B75D18) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B75D18);

  const PPCContext original_ctx = ctx;
  const bool log_trace =
      refii::vfs::kTraceMeshModelAudioDiagnostics &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation("sub_82B75D18", 2);
  if (log_trace) {
    REXLOG_INFO("VFS trace InitModelHeaders phase enter sub_82B75D18 "
                "lr=0x{:08X} {}",
                original_ctx.lr,
                refii::vfs::DescribeInitModelHeaderSummary(base));
  }

  __imp__sub_82B75D18(ctx, base);

  if (log_trace) {
    REXLOG_INFO("VFS trace InitModelHeaders phase exit sub_82B75D18 "
                "lr=0x{:08X} result=0x{:08X} {}",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::DescribeInitModelHeaderSummary(base));
  }
}

REX_EXTERN(sub_82286D78) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82286D78);

  const PPCContext original_ctx = ctx;
  const bool log_trace =
      refii::vfs::kTraceMeshModelAudioDiagnostics &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation("sub_82286D78", 2);
  if (log_trace) {
    REXLOG_INFO("VFS trace InitModelHeaders callback enter sub_82286D78 "
                "lr=0x{:08X} callbacks=0x{:08X} requested=0x{:08X} {}",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                refii::vfs::DescribeResourceCallbackState(
                    base, original_ctx.r3.u32));
  }

  __imp__sub_82286D78(ctx, base);

  if (log_trace) {
    REXLOG_INFO("VFS trace InitModelHeaders callback exit sub_82286D78 "
                "lr=0x{:08X} callbacks=0x{:08X} result=0x{:08X} {}",
                original_ctx.lr,
                original_ctx.r3.u32,
                ctx.r3.u32,
                refii::vfs::DescribeResourceCallbackState(
                    base, original_ctx.r3.u32));
  }
}

REX_EXTERN(sub_8220B470) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_8220B470);

  const PPCContext original_ctx = ctx;
  const bool log_trace =
      refii::vfs::kTraceMeshModelAudioDiagnostics &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation("sub_8220B470", 2);
  if (log_trace) {
    REXLOG_INFO("VFS trace InitModelHeaders callback enter sub_8220B470 "
                "lr=0x{:08X} callbacks=0x{:08X} requested=0x{:08X} {}",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                refii::vfs::DescribeResourceCallbackState(
                    base, original_ctx.r3.u32));
  }

  __imp__sub_8220B470(ctx, base);

  if (log_trace) {
    REXLOG_INFO("VFS trace InitModelHeaders callback exit sub_8220B470 "
                "lr=0x{:08X} callbacks=0x{:08X} result=0x{:08X} {}",
                original_ctx.lr,
                original_ctx.r3.u32,
                ctx.r3.u32,
                refii::vfs::DescribeResourceCallbackState(
                    base, original_ctx.r3.u32));
  }
}

REX_EXTERN(sub_82237338) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82237338);

  const PPCContext original_ctx = ctx;
  const bool log_trace =
      refii::vfs::kTraceMeshModelAudioDiagnostics &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation("sub_82237338", 2);
  if (log_trace) {
    REXLOG_INFO("VFS trace InitModelHeaders phase enter sub_82237338 "
                "lr=0x{:08X} {}",
                original_ctx.lr,
                refii::vfs::DescribeInitModelHeaderSummary(base));
  }

  __imp__sub_82237338(ctx, base);

  if (log_trace) {
    REXLOG_INFO("VFS trace InitModelHeaders phase exit sub_82237338 "
                "lr=0x{:08X} result=0x{:08X} {}",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::DescribeInitModelHeaderSummary(base));
  }
}

REX_EXTERN(sub_82B86B40) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B86B40);

  const PPCContext original_ctx = ctx;
  const bool log_trace =
      refii::vfs::kTraceMeshModelAudioDiagnostics &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation("sub_82B86B40");
  if (log_trace) {
    REXLOG_INFO("VFS trace InitModelHeaders phase enter sub_82B86B40 "
                "lr=0x{:08X}",
                original_ctx.lr);
  }

  __imp__sub_82B86B40(ctx, base);

  if (log_trace) {
    REXLOG_INFO("VFS trace InitModelHeaders phase exit sub_82B86B40 "
                "lr=0x{:08X} result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(rex_lhResource_CreateModelCache) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhResource_CreateModelCache);

  const PPCContext original_ctx = ctx;
  if (refii::vfs::kTraceNonVfsBootFrameProbes &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase enter CreateModelCache lr=0x{:08X} "
                "r3=0x{:08X}",
                original_ctx.lr,
                original_ctx.r3.u32);
  }

  __imp__rex_lhResource_CreateModelCache(ctx, base);

  if (refii::vfs::kTraceNonVfsBootFrameProbes &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase exit CreateModelCache lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(rex_lhResource_SetupModelCache) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhResource_SetupModelCache);

  const PPCContext original_ctx = ctx;
  if (refii::vfs::kTraceNonVfsBootFrameProbes &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase enter SetupModelCache lr=0x{:08X} "
                "out=0x{:08X} cache=0x{:08X}",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32);
  }

  __imp__rex_lhResource_SetupModelCache(ctx, base);

  if (refii::vfs::kTraceNonVfsBootFrameProbes &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase exit SetupModelCache lr=0x{:08X} "
                "result=0x{:08X} out_words={}",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::FormatGuestWords(base, original_ctx.r3.u32, 4));
  }
}

REX_EXTERN(rex_lhResource_SetLoadMode) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhResource_SetLoadMode);

  const PPCContext original_ctx = ctx;
  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::VfsVerboseIoTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase enter SetLoadMode lr=0x{:08X} "
                "this=0x{:08X} mode_a=0x{:08X} mode_b=0x{:08X} "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                original_ctx.r5.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__rex_lhResource_SetLoadMode(ctx, base);
  refii::vfs::RecordRefiiPostVideoUiEvent("SetLoadMode",
                                              original_ctx.lr,
                                              ctx.r3.u32,
                                              original_ctx.r3.u32,
                                              original_ctx.r4.u32,
                                              original_ctx.r5.u32);

  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::VfsVerboseIoTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase exit SetLoadMode lr=0x{:08X} "
                "result=0x{:08X} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

REX_EXTERN(rex_lhResource_InitSystem) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhResource_InitSystem);

  const PPCContext original_ctx = ctx;
  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::VfsVerboseIoTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase enter InitSystem lr=0x{:08X}",
                original_ctx.lr);
  }

  __imp__rex_lhResource_InitSystem(ctx, base);

  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase exit InitSystem lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(rex_lhResource_InitLoader) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhResource_InitLoader);

  const PPCContext original_ctx = ctx;
  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::VfsVerboseIoTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase enter InitLoader lr=0x{:08X}",
                original_ctx.lr);
  }

  __imp__rex_lhResource_InitLoader(ctx, base);

  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase exit InitLoader lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(rex_lhRenderer_Construct) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhRenderer_Construct);

  const PPCContext original_ctx = ctx;
  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::VfsVerboseIoTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase enter Renderer_Construct lr=0x{:08X} "
                "this=0x{:08X}",
                original_ctx.lr,
                original_ctx.r3.u32);
  }

  __imp__rex_lhRenderer_Construct(ctx, base);

  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase exit Renderer_Construct lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(rex_lhRenderer_Setup) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhRenderer_Setup);

  const PPCContext original_ctx = ctx;
  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase enter Renderer_Setup lr=0x{:08X} "
                "renderer=0x{:08X} owner=0x{:08X} game=0x{:08X}",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                original_ctx.r5.u32);
  }

  __imp__rex_lhRenderer_Setup(ctx, base);

  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase exit Renderer_Setup lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(sub_82389210) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82389210);

  const PPCContext original_ctx = ctx;
  const auto begin = std::chrono::steady_clock::now();
  const bool native_startup_frame = original_ctx.lr == 0x82377BCCu;
  const bool startup_playback_critical =
      native_startup_frame &&
      refii::vfs::RefiiStartupVideosPlaybackCriticalActive();
  const double native_frame_delta = original_ctx.f1.f64;
  const double corrected_startup_frame_delta =
      RefiiStartupVideoFrameDelta(native_frame_delta, startup_playback_critical);
  const bool startup_frame_delta_corrected =
      startup_playback_critical &&
      corrected_startup_frame_delta > native_frame_delta;
  if (startup_frame_delta_corrected) {
    ctx.f1.f64 = corrected_startup_frame_delta;
  }
  const bool startup_frame_progress =
      startup_playback_critical && ShouldLogNativeStartupFrameProgress();
  const bool native_startup_audit =
      native_startup_frame &&
      ShouldLogNativeStartupPlaybackAudit("NativeStartupFrame_82389210", 3,
                                          300);
  if (!startup_playback_critical &&
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::BeginRefiiPostVideoFrameStageTiming();
  }
  const bool trace =
      !startup_playback_critical &&
      refii::vfs::ShouldTracePostVideoBoundaryProbe("PostVideoFrame_82389210");
  if (!startup_playback_critical) {
    refii::vfs::RecordRefiiPostVideoFrameHandoffSnapshot(
        base, original_ctx.r3.u32, original_ctx.lr);
  }
  if (startup_frame_progress) {
    REXLOG_INFO("Refii VFS native startup frame progress enter "
                "lr=0x{:08X} owner=0x{:08X} delta={} native_delta={} "
                "corrected={} timing=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                ctx.f1.f64,
                native_frame_delta,
                startup_frame_delta_corrected ? "true" : "false",
                refii::vfs::FormatPostVideoFrameTimingState(
                    base, original_ctx.r3.u32, ctx.f1.f64));
  }
  if (native_startup_audit) {
    REXLOG_INFO("Refii VFS native startup frame enter sub_82389210 "
                "lr=0x{:08X} owner=0x{:08X} delta={} timing=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.f1.f64,
                refii::vfs::FormatPostVideoFrameTimingState(
                    base, original_ctx.r3.u32, original_ctx.f1.f64));
  }
  if (trace) {
    REXLOG_INFO("VFS trace post-video frame update enter sub_82389210 "
                "lr=0x{:08X} delta={} timing=[{}] args=[{}] chain=[{}] "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                original_ctx.f1.f64,
                refii::vfs::FormatPostVideoFrameTimingState(
                    base, original_ctx.r3.u32, original_ctx.f1.f64),
                refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
                refii::vfs::FormatPostVideoFrameChainState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  bool pre_native_post_gameface_frame_handoff = false;
  if (!startup_playback_critical && refii::vfs::RefiiLooseAssetBackendEnabled() &&
      refii::vfs::RefiiStartupVideosReadyForMenuHandoff()) {
    pre_native_post_gameface_frame_handoff =
        refii::vfs::TryAttachRefiiPostGameFaceRenderHandoffToActiveFrame(
            original_ctx, base, "PostVideoFrame_82389210_PreNative");
  }
  if (pre_native_post_gameface_frame_handoff &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS post-video frame pre-native post-GameFace handoff "
                "lr=0x{:08X} owner=0x{:08X} frame_chain=[{}] handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                refii::vfs::FormatPostVideoFrameChainState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82389210(ctx, base);

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  const bool startup_duration_completion_applied =
      startup_playback_critical &&
      MaybeApplyNativeStartupVideoDurationCompletion(base,
                                                     original_ctx.r3.u32,
                                                     original_ctx.lr);
  if (refii::vfs::VfsTracingEnabled() &&
      !startup_playback_critical &&
      (refii::vfs::RefiiStartupVideosReadyForMenuHandoff() ||
       refii::vfs::RefiiMainMenuTraceIsHashOnly() ||
       refii::vfs::RefiiMainMenuTraceNeedsFrontendHandoff())) {
    refii::vfs::RecordRefiiPostVideoUiEvent("PostVideoFrame_82389210",
                                                original_ctx.lr,
                                                ctx.r3.u32,
                                                original_ctx.r3.u32,
                                                original_ctx.r4.u32,
                                                original_ctx.r5.u32);
  }
  if (!startup_playback_critical) {
    refii::vfs::MaybeApplyRefiiVfsPostVideoHandoffTimerPatch(
        base, original_ctx.lr, original_ctx.r3.u32, "PostVideoFrame_82389210");
    PPCContext post_video_synthetic_ctx = original_ctx;
    refii::vfs::TryRunPostVideoFrontendSyntheticHandoff(
        post_video_synthetic_ctx,
        base,
        original_ctx.r3.u32,
        "PostVideoFrame_82389210");
  }

  if (trace) {
    REXLOG_INFO("VFS trace post-video frame update exit sub_82389210 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} delta={} "
                "timing_after=[{}] "
                "args_after=[{}] chain_after=[{}] "
                "milestones=Renderer_BeginScene_lr_823892F0 "
                "dynamic_slot_lr_82389304 sub_82388A18_lr_82389308 "
                "sub_82A41B00_lr_82389310 final_owner_plus4_clear "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                original_ctx.f1.f64,
                refii::vfs::FormatPostVideoFrameTimingState(
                    base, original_ctx.r3.u32, original_ctx.f1.f64),
                refii::vfs::FormatPostVideoBoundaryArgs(base, ctx),
                refii::vfs::FormatPostVideoFrameChainState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (native_startup_audit && !trace) {
    REXLOG_INFO("Refii VFS native startup frame exit sub_82389210 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} delta={} "
                "native_delta={} corrected={} duration_complete={} "
                "timing_after=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                ctx.f1.f64,
                native_frame_delta,
                startup_frame_delta_corrected ? "true" : "false",
                startup_duration_completion_applied ? "true" : "false",
                refii::vfs::FormatPostVideoFrameTimingState(
                    base, original_ctx.r3.u32, ctx.f1.f64));
  }
  if (startup_frame_progress) {
    REXLOG_INFO("Refii VFS native startup frame progress exit "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} delta={} "
                "duration_complete={} timing_after=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                original_ctx.f1.f64,
                startup_duration_completion_applied ? "true" : "false",
                refii::vfs::FormatPostVideoFrameTimingState(
                    base, original_ctx.r3.u32, original_ctx.f1.f64));
  }
  if (!startup_playback_critical && elapsed_ms >= 250 &&
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoFrameSlowBoundary", 8)) {
    const std::string next_boundary =
        refii::vfs::RefiiPostVideoSlowFrameNextBoundary();
    REXLOG_INFO("Refii VFS post-video frame slow boundary "
                "lr=0x{:08X} elapsed_ms={} delta={} next={} timing=[{}] "
                "stage_timing=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                elapsed_ms,
                original_ctx.f1.f64,
                next_boundary,
                refii::vfs::FormatPostVideoFrameTimingState(
                    base, original_ctx.r3.u32, original_ctx.f1.f64),
                refii::vfs::FormatRefiiPostVideoStageTimingForLog(),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    refii::vfs::LogRefiiVfsChainVerdict(
        "frontend_post_video",
        static_cast<uint32_t>(original_ctx.lr),
        "startup_bik_pair",
        "art/videos/microsoft_logo.bik|art/videos/lionhead_logo.bik",
        "art/videos/microsoft_logo.bik|art/videos/lionhead_logo.bik",
        "native_buffered_stream",
        "sub_82389210_slow_frame",
        "TraceOnce",
        next_boundary);
  }
  if (!startup_playback_critical) {
    refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
        original_ctx.lr, "PostVideoFrame_82389210", base);
    refii::vfs::MaybeLogRefiiStartupReadyGateTextureOpenVerdict(
        original_ctx.lr, "PostVideoFrame_82389210", base);
  }
}

REX_EXTERN(sub_823781E0) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_823781E0);

  const PPCContext original_ctx = ctx;
  const auto begin = std::chrono::steady_clock::now();
  const bool trace =
      refii::vfs::ShouldTracePostVideoBoundaryProbe("PostVideoFrame_823781E0");
  if (trace) {
    REXLOG_INFO("VFS trace post-video dynamic handoff getter enter sub_823781E0 "
                "lr=0x{:08X} args=[{}] getter=[{}] handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
                refii::vfs::FormatPostVideoDynamicHandoffGetterState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  bool refii_dynamic_getter_shortcut = false;
  uint32_t refii_request_guest = 0;
  uint32_t refii_owner_guest = 0;
  uint32_t refii_handoff_guest = 0;
  if (base != nullptr && original_ctx.lr == 0x82389304u &&
      refii::vfs::RefiiLooseAssetBackendEnabled() &&
      refii::vfs::RefiiStartupVideosReadyForMenuHandoff() &&
      refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted() &&
      refii::vfs::RefiiPostVideoFrontendChainConsumed() &&
      refii::vfs::RefiiPostVfsChainReadyForLoadingOwnerRelease() &&
      refii::vfs::RefiiPostGameFacePrimaryOwnerReady(base) &&
      refii::vfs::TryGetRefiiPostGameFacePrimaryRenderRequest(
          base, &refii_request_guest, &refii_owner_guest,
          &refii_handoff_guest) &&
      refii::vfs::LooksLikeGuestPointer(refii_owner_guest) &&
      refii::vfs::LooksLikeGuestPointer(refii_handoff_guest) &&
      refii::vfs::LoadGuestU32(base, refii_owner_guest + 4) ==
          refii_handoff_guest &&
      (refii::vfs::LoadGuestU8(base, refii_handoff_guest + 168) != 0 ||
       refii::vfs::LoadGuestU8(base, refii_handoff_guest + 244) != 0)) {
    ctx.r3.u32 = refii_owner_guest;
    refii_dynamic_getter_shortcut = true;
    if (refii::vfs::VfsTracingEnabled() ||
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "PostGameFaceDynamicGetterShortcut", 8)) {
      REXLOG_INFO("Refii VFS post-GameFace dynamic handoff getter shortcut "
                  "lr=0x{:08X} dynamic=0x{:08X} request=0x{:08X} "
                  "owner=0x{:08X} handoff=0x{:08X} "
                  "complete=0x{:02X} flag244=0x{:02X} "
                  "state=[{}] handoff_trace=[{}] main_menu=[{}]",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  refii_request_guest,
                  refii_owner_guest,
                  refii_handoff_guest,
                  refii::vfs::LoadGuestU8(base, refii_handoff_guest + 168),
                  refii::vfs::LoadGuestU8(base, refii_handoff_guest + 244),
                  refii::vfs::FormatPostVideoDynamicHandoffGetterState(
                      base, original_ctx.r3.u32),
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    }
  }

  if (!refii_dynamic_getter_shortcut) {
    __imp__sub_823781E0(ctx, base);
  }

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_823781E0",
                                                    original_ctx.lr,
                                                    ctx.r3.u32,
                                                    elapsed_ms);
  }

  if (trace) {
    refii::vfs::RecordRefiiPostVideoUiEvent("PostVideoFrame_823781E0",
                                                original_ctx.lr,
                                                ctx.r3.u32,
                                                original_ctx.r3.u32,
                                                original_ctx.r4.u32,
                                                original_ctx.r5.u32);
    REXLOG_INFO("VFS trace post-video dynamic handoff getter exit sub_823781E0 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
                "result_wait_state=[{}] getter_after=[{}] handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatPostVideoFrameWaitState(base, ctx.r3.u32),
                refii::vfs::FormatPostVideoDynamicHandoffGetterState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (elapsed_ms >= 250 &&
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoStageSlow_sub_823781E0", 8)) {
    REXLOG_INFO("Refii VFS post-video stage slow "
                "stage=sub_823781E0 lr=0x{:08X} result=0x{:08X} "
                "elapsed_ms={} stage_timing=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatRefiiPostVideoStageTimingForLog(),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
      original_ctx.lr, "PostVideoFrame_823781E0", base);
}

REX_EXTERN(sub_823889A8) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_823889A8);

  const PPCContext original_ctx = ctx;
  const auto begin = std::chrono::steady_clock::now();
  const uint32_t owner_guest = original_ctx.r3.u32;
  const uint32_t handoff_guest =
      owner_guest != 0 ? refii::vfs::LoadGuestU32(base, owner_guest + 4) : 0;
  refii::vfs::RecordRefiiRenderProducerScheduleSnapshot(
      base,
      owner_guest,
      original_ctx.lr,
      original_ctx.r6.u32,
      original_ctx.r5.u32,
      original_ctx.f1.f64);
  const bool trace =
      refii::vfs::ShouldTracePostVideoTextureOwnedHandoffProbe(
          "StartupVideoFrame_823889A8") ||
      (refii::vfs::kTraceNonVfsBootFrameProbes &&
       refii::vfs::VfsTracingEnabled() &&
       (original_ctx.lr == 0x823890EC ||
        refii::vfs::ShouldTracePostVideoBoundaryProbe(
            "PostVideoFrame_823889A8")));
  if (trace) {
    REXLOG_INFO("VFS trace startup-video frame handshake enter sub_823889A8 "
                "lr=0x{:08X} owner=0x{:08X} handoff=0x{:08X} "
                "state=[{}] args=[{}] post_video=[{}] main_menu=[{}]",
                original_ctx.lr,
                owner_guest,
                handoff_guest,
                refii::vfs::FormatPostVideoFrameWaitState(base,
                                                              owner_guest),
                refii::vfs::FormatPostVideoBoundaryArgs(base,
                                                            original_ctx),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_823889A8(ctx, base);

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_823889A8",
                                                    original_ctx.lr,
                                                    ctx.r3.u32,
                                                    elapsed_ms);
  }
  refii::vfs::MaybeApplyRefiiVfsStartupHandoffReadyPatch(
      base, original_ctx.lr, "StartupVideoFrame_823889A8");

  if (trace) {
    refii::vfs::RecordRefiiPostVideoUiEvent("StartupVideoFrame_823889A8",
                                                original_ctx.lr,
                                                ctx.r3.u32,
                                                owner_guest,
                                                handoff_guest,
                                                original_ctx.r5.u32);
    REXLOG_INFO("VFS trace startup-video frame handshake exit sub_823889A8 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} state_after=[{}] "
                "post_video=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatPostVideoFrameWaitState(base,
                                                              owner_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (elapsed_ms >= 250 &&
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoStageSlow_sub_823889A8", 8)) {
    REXLOG_INFO("Refii VFS post-video stage slow "
                "stage=sub_823889A8 lr=0x{:08X} result=0x{:08X} "
                "elapsed_ms={} stage_timing=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatRefiiPostVideoStageTimingForLog(),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
      original_ctx.lr, "StartupVideoFrame_823889A8", base);
}

REX_EXTERN(sub_82388A88) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82388A88);

  const PPCContext original_ctx = ctx;
  const auto begin = std::chrono::steady_clock::now();
  const uint32_t owner_guest = original_ctx.r3.u32;
  const uint32_t handoff_guest =
      owner_guest != 0 ? refii::vfs::LoadGuestU32(base, owner_guest + 4) : 0;
  refii::vfs::RecordRefiiRenderProducerScheduleSnapshot(
      base,
      owner_guest,
      original_ctx.lr,
      original_ctx.r6.u32,
      original_ctx.r5.u32,
      original_ctx.f1.f64);
  const bool trace =
      refii::vfs::ShouldTracePostVideoTextureOwnedHandoffProbe(
          "RenderProducerSchedule_82388A88") ||
      (refii::vfs::kTraceNonVfsBootFrameProbes &&
       refii::vfs::VfsTracingEnabled() &&
       (original_ctx.lr == 0x82276D68 ||
        refii::vfs::ShouldTracePostVideoBoundaryProbe(
            "RenderProducerSchedule_82388A88")));
  if (trace) {
    REXLOG_INFO("VFS trace render producer schedule enter sub_82388A88 "
                "lr=0x{:08X} owner=0x{:08X} handoff=0x{:08X} "
                "frame_time={} source124=0x{:08X} source128=0x{:08X} "
                "args=[{}] producer=[{}] handoff_trace=[{}] main_menu=[{}]",
                original_ctx.lr,
                owner_guest,
                handoff_guest,
                original_ctx.f1.f64,
                original_ctx.r6.u32,
                original_ctx.r5.u32,
                refii::vfs::FormatPostVideoBoundaryArgs(base,
                                                            original_ctx),
                refii::vfs::FormatPostVideoRenderProducerState(
                    base, handoff_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82388A88(ctx, base);

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_82388A88",
                                                    original_ctx.lr,
                                                    ctx.r3.u32,
                                                    elapsed_ms);
  }
  if (trace ||
      (refii::vfs::kTraceNonVfsBootFrameProbes &&
       refii::vfs::VfsTracingEnabled() &&
       refii::vfs::RefiiStartupVideosReadyForMenuHandoff())) {
    refii::vfs::RecordRefiiPostVideoUiEvent("RenderProducerSchedule_82388A88",
                                                original_ctx.lr,
                                                ctx.r3.u32,
                                                owner_guest,
                                                handoff_guest,
                                                original_ctx.r5.u32);
  }
  if (trace) {
    REXLOG_INFO("VFS trace render producer schedule exit sub_82388A88 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
                "producer_after=[{}] handoff_trace=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatPostVideoRenderProducerState(
                    base, handoff_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (elapsed_ms >= 250 &&
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoStageSlow_sub_82388A88", 8)) {
    REXLOG_INFO("Refii VFS post-video stage slow "
                "stage=sub_82388A88 lr=0x{:08X} result=0x{:08X} "
                "elapsed_ms={} producer=[{}] stage_timing=[{}] "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatPostVideoRenderProducerState(
                    base, handoff_guest),
                refii::vfs::FormatRefiiPostVideoStageTimingForLog(),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
      original_ctx.lr, "RenderProducerSchedule_82388A88", base);
}

REX_EXTERN(sub_823865F8) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_823865F8);

  const PPCContext original_ctx = ctx;
  const auto begin = std::chrono::steady_clock::now();
  const bool trace =
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() ||
      refii::vfs::ShouldTracePostVideoBoundaryProbe(
          "FrontendReadyTail_823865F8");
  if (trace) {
    REXLOG_INFO("VFS trace frontend ready tail enter sub_823865F8 "
                "lr=0x{:08X} args=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                refii::vfs::FormatPostVideoBoundaryArgs(base,
                                                            original_ctx),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_823865F8(ctx, base);

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_823865F8",
                                                    original_ctx.lr,
                                                    ctx.r3.u32,
                                                    elapsed_ms);
  }
  if (trace) {
    refii::vfs::RecordRefiiPostVideoUiEvent("FrontendReadyTail_823865F8",
                                                original_ctx.lr,
                                                ctx.r3.u32,
                                                original_ctx.r3.u32,
                                                original_ctx.r4.u32,
                                                original_ctx.r5.u32);
    REXLOG_INFO("VFS trace frontend ready tail exit sub_823865F8 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
                "stage_timing=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatRefiiPostVideoStageTimingForLog(),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (elapsed_ms >= 250 &&
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoStageSlow_sub_823865F8", 8)) {
    REXLOG_INFO("Refii VFS post-video stage slow "
                "stage=sub_823865F8 lr=0x{:08X} result=0x{:08X} "
                "elapsed_ms={} stage_timing=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatRefiiPostVideoStageTimingForLog(),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
      original_ctx.lr, "FrontendReadyTail_823865F8", base);
}

REX_EXTERN(sub_82361128) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82361128);

  const PPCContext original_ctx = ctx;
  const uint32_t ready_object = original_ctx.r3.u32;
  const uint32_t ready_payload =
      refii::vfs::LooksLikeGuestPointer(ready_object)
          ? refii::vfs::LoadGuestU32(base, ready_object + 4)
          : 0;
  const uint32_t ready_lock =
      refii::vfs::LooksLikeGuestPointer(ready_object)
          ? refii::vfs::LoadGuestU32(base, ready_object + 8)
          : 0;
  const bool trace =
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "GeneratedRenderReady_82361128", 32);

  ::__imp__sub_82361128(ctx, base);

  bool seeded_ready_payload = false;
  if (ctx.r3.u32 == 0 && ready_payload == 0 &&
      refii::vfs::MaybeSeedRefiiGeneratedRenderReadyPayload(
          original_ctx, base, ready_object)) {
    seeded_ready_payload = true;
    ctx = original_ctx;
    ::__imp__sub_82361128(ctx, base);
  }

  if (trace) {
    const uint32_t current_payload =
        refii::vfs::LooksLikeGuestPointer(ready_object)
            ? refii::vfs::LoadGuestU32(base, ready_object + 4)
            : 0;
    REXLOG_INFO("VFS trace generated render ready sub_82361128 "
                "lr=0x{:08X} object=0x{:08X} payload=0x{:08X}->0x{:08X} "
                "lock=0x{:08X} result=0x{:08X} seeded={} "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ready_object,
                ready_payload,
                current_payload,
                ready_lock,
                ctx.r3.u32,
                seeded_ready_payload ? "true" : "false",
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

REX_EXTERN(sub_82361200) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82361200);

  const PPCContext original_ctx = ctx;
  const uint32_t ready_object = original_ctx.r3.u32;
  const uint32_t before_payload =
      refii::vfs::LooksLikeGuestPointer(ready_object)
          ? refii::vfs::LoadGuestU32(base, ready_object + 4)
          : 0;
  const bool trace =
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "GeneratedRenderReadySet_82361200", 16);

  ::__imp__sub_82361200(ctx, base);

  if (trace) {
    const uint32_t after_payload =
        refii::vfs::LooksLikeGuestPointer(ready_object)
            ? refii::vfs::LoadGuestU32(base, ready_object + 4)
            : 0;
    REXLOG_INFO("VFS trace generated render ready set sub_82361200 "
                "lr=0x{:08X} object=0x{:08X} payload=0x{:08X}->0x{:08X} "
                "asset_words={} kind=0x{:08X} flag=0x{:08X} "
                "result=0x{:08X} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ready_object,
                before_payload,
                after_payload,
                refii::vfs::FormatGuestWords(base, original_ctx.r4.u32, 8),
                original_ctx.r5.u32,
                original_ctx.r6.u32,
                ctx.r3.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

REX_EXTERN(sub_82388B48) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82388B48);

  const PPCContext original_ctx = ctx;
  const auto begin = std::chrono::steady_clock::now();
  const uint32_t owner_guest = original_ctx.r3.u32;
  const uint32_t handoff_guest =
      owner_guest != 0 ? refii::vfs::LoadGuestU32(base, owner_guest + 4) : 0;
  const bool trace =
      refii::vfs::ShouldTracePostVideoTextureOwnedHandoffProbe(
          "RenderProducerFadeSchedule_82388B48") ||
      (refii::vfs::kTraceNonVfsBootFrameProbes &&
       refii::vfs::VfsTracingEnabled() &&
       refii::vfs::ShouldTracePostVideoBoundaryProbe(
           "RenderProducerFadeSchedule_82388B48"));
  if (trace) {
    REXLOG_INFO("VFS trace render producer fade schedule enter sub_82388B48 "
                "lr=0x{:08X} owner=0x{:08X} handoff=0x{:08X} "
                "f1={} f2={} r6=0x{:08X} r7=0x{:08X} args=[{}] "
                "producer=[{}] handoff_trace=[{}] main_menu=[{}]",
                original_ctx.lr,
                owner_guest,
                handoff_guest,
                original_ctx.f1.f64,
                original_ctx.f2.f64,
                original_ctx.r6.u32,
                original_ctx.r7.u32,
                refii::vfs::FormatPostVideoBoundaryArgs(base,
                                                            original_ctx),
                refii::vfs::FormatPostVideoRenderProducerState(
                    base, handoff_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82388B48(ctx, base);

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_82388B48",
                                                    original_ctx.lr,
                                                    ctx.r3.u32,
                                                    elapsed_ms);
  }
  if (trace) {
    refii::vfs::RecordRefiiPostVideoUiEvent(
        "RenderProducerFadeSchedule_82388B48",
        original_ctx.lr,
        ctx.r3.u32,
        owner_guest,
        handoff_guest,
        original_ctx.r6.u32);
    REXLOG_INFO("VFS trace render producer fade schedule exit sub_82388B48 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
                "producer_after=[{}] handoff_trace=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatPostVideoRenderProducerState(
                    base, handoff_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
      original_ctx.lr, "RenderProducerFadeSchedule_82388B48", base);
}

REX_EXTERN(sub_82388040) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82388040);

  const PPCContext original_ctx = ctx;
  const auto begin = std::chrono::steady_clock::now();
  const uint32_t handoff_guest = original_ctx.r3.u32;
  const bool trace =
      refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "RenderProducer_82388040", 8);
  if (trace) {
    REXLOG_INFO("VFS trace render producer enter sub_82388040 "
                "lr=0x{:08X} handoff=0x{:08X} producer=[{}] "
                "handoff_trace=[{}] main_menu=[{}]",
                original_ctx.lr,
                handoff_guest,
                refii::vfs::FormatPostVideoRenderProducerState(
                    base, handoff_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82388040(ctx, base);

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_82388040",
                                                    original_ctx.lr,
                                                    ctx.r3.u32,
                                                    elapsed_ms);
  }
  if (trace ||
      (elapsed_ms >= 250 &&
       refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
       refii::vfs::ShouldLogRepeatedVfsTraceOperation(
           "RenderProducer_82388040_exit", 8))) {
    REXLOG_INFO("VFS trace render producer exit sub_82388040 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
                "producer_after=[{}] stage_timing=[{}] "
                "handoff_trace=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatPostVideoRenderProducerState(
                    base, handoff_guest),
                refii::vfs::FormatRefiiPostVideoStageTimingForLog(),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
      original_ctx.lr, "RenderProducer_82388040", base);
}

REX_EXTERN(sub_82A415C0) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82A415C0);

  const PPCContext original_ctx = ctx;
  const uint32_t descriptor_guest = original_ctx.r3.u32;
  const uint32_t hash_pointer_guest = original_ctx.r5.u32;
  const uint32_t ref_pointer_guest = original_ctx.r6.u32;
  const uint32_t resource_hash =
      hash_pointer_guest == 0
          ? 0
          : refii::vfs::LoadGuestU32(base, hash_pointer_guest);

  refii::vfs::LooseAssetResolution resolution;
  std::string name;
  std::string candidate;
  std::string source;
  const bool refii_owned_texture =
      refii::vfs::ResolveRefiiOwnedTextureDescriptorHash(
          "TextureDescriptor_Ctor",
          resource_hash,
          static_cast<uint32_t>(original_ctx.lr),
          &resolution,
          &name,
          &candidate,
          &source);
  const bool main_menu_texture =
      refii_owned_texture &&
      refii::vfs::IsMainMenuTextureHashTraceAsset(name);
  const bool trace =
      refii_owned_texture &&
      (refii::vfs::VfsTracingEnabled() ||
       refii::vfs::LooseAssetOverrideTracingEnabled() ||
       main_menu_texture) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          fmt::format("TextureDescriptorConstruct:{:08X}:{}",
                      resource_hash,
                      refii::vfs::NormalizeLooseAssetKey(name)),
          main_menu_texture ? 32 : 64);

  if (trace) {
    REXLOG_INFO("Refii VFS texture descriptor ctor enter lr=0x{:08X} "
                "hash=0x{:08X} name={} out=0x{:08X} mode={} hash_ptr=0x{:08X} "
                "ref_ptr=0x{:08X} ref_object=0x{:08X} r7={} r8={} "
                "candidate={} source={} provider_relative={} out_before=[{}] "
                "ref_words=[{}]",
                original_ctx.lr,
                resource_hash,
                refii::vfs::NormalizeLooseAssetKey(name),
                descriptor_guest,
                original_ctx.r4.u32,
                hash_pointer_guest,
                ref_pointer_guest,
                ref_pointer_guest == 0
                    ? 0
                    : refii::vfs::LoadGuestU32(base, ref_pointer_guest),
                original_ctx.r7.u32,
                original_ctx.r8.u32,
                candidate,
                source,
                resolution.provider_relative_path,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, descriptor_guest, 8),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, ref_pointer_guest, 4));
  }

  if (base != nullptr && descriptor_guest != 0 && hash_pointer_guest != 0 &&
      ref_pointer_guest != 0) {
    refii::vfs::BuildTextureResourceDescriptor(ctx,
                                                   base,
                                                   descriptor_guest,
                                                   original_ctx.r4.u32,
                                                   hash_pointer_guest,
                                                   ref_pointer_guest);
    ctx.r3.u32 = descriptor_guest;
  } else {
    __imp__sub_82A415C0(ctx, base);
  }

  const uint32_t descriptor_vtable =
      descriptor_guest == 0
          ? 0
          : refii::vfs::LoadGuestU32(base, descriptor_guest + 0);
  refii::vfs::RecordFrontendTextureConsumerDescriptor(
      descriptor_guest,
      original_ctx.lr,
      resource_hash,
      descriptor_vtable,
      descriptor_vtable == 0
          ? 0
          : refii::vfs::LoadGuestU32(base, descriptor_vtable + 12),
      descriptor_vtable == 0
          ? 0
          : refii::vfs::LoadGuestU32(base, descriptor_vtable + 16),
      name,
      candidate,
      source,
      refii_owned_texture);

  if (trace) {
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "texture_descriptor",
        fmt::format("ctor lr=0x{:08X} hash=0x{:08X} name={} out=0x{:08X} "
                    "candidate={} source={} descriptor=[{}]",
                    original_ctx.lr,
                    resource_hash,
                    refii::vfs::NormalizeLooseAssetKey(name),
                    descriptor_guest,
                    candidate,
                    source,
                    refii::vfs::FormatGuestWordsIfPointer(
                        base, descriptor_guest, 8)));
    REXLOG_INFO("Refii VFS texture descriptor ctor exit lr=0x{:08X} "
                "hash=0x{:08X} name={} out=0x{:08X} result=0x{:08X} "
                "descriptor=[{}] vtable=[{}]",
                original_ctx.lr,
                resource_hash,
                refii::vfs::NormalizeLooseAssetKey(name),
                descriptor_guest,
                ctx.r3.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, descriptor_guest, 8),
                refii::vfs::FormatGuestObjectVTable(
                    base, descriptor_guest, 8));
    refii::vfs::LogRefiiVfsChainVerdict(
        "texture_descriptor",
        static_cast<uint32_t>(original_ctx.lr),
        name,
        candidate,
        resolution.provider_relative_path,
        "Refii_descriptor_builder",
        "sub_82A415C0_collapsed",
        "CollapseNow",
        "sub_82A41758_or_ResourceManager_Open");
  }
}

REX_EXTERN(sub_82A41758) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82A41758);

  const PPCContext original_ctx = ctx;
  const uint32_t table_guest = original_ctx.r3.u32;
  const uint32_t descriptor_guest = original_ctx.r4.u32;
  const uint32_t resource_hash =
      descriptor_guest == 0
          ? 0
          : refii::vfs::LoadGuestU32(base, descriptor_guest + 12);

  refii::vfs::LooseAssetResolution resolution;
  std::string name;
  std::string candidate;
  std::string source;
  const bool refii_owned_texture =
      refii::vfs::ResolveRefiiOwnedTextureDescriptorHash(
          "TextureDescriptor_Serialize",
          resource_hash,
          static_cast<uint32_t>(original_ctx.lr),
          &resolution,
          &name,
          &candidate,
          &source);
  const bool main_menu_texture =
      refii_owned_texture &&
      refii::vfs::IsMainMenuTextureHashTraceAsset(name);
  const bool trace =
      refii_owned_texture &&
      (refii::vfs::VfsTracingEnabled() ||
       refii::vfs::LooseAssetOverrideTracingEnabled() ||
       main_menu_texture ||
       refii::vfs::ShouldLogRepeatedVfsTraceOperation(
           "TextureDescriptorSerialize", 64));

  if (trace) {
    REXLOG_INFO("Refii VFS texture descriptor serialize enter lr=0x{:08X} "
                "hash=0x{:08X} name={} table=0x{:08X} descriptor=0x{:08X} "
                "candidate={} source={} provider_relative={} "
                "descriptor_words=[{}] table_words=[{}]",
                original_ctx.lr,
                resource_hash,
                refii::vfs::NormalizeLooseAssetKey(name),
                table_guest,
                descriptor_guest,
                candidate,
                source,
                resolution.provider_relative_path,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, descriptor_guest, 8),
                refii::vfs::FormatGuestWordsIfPointer(base,
                                                          table_guest,
                                                          12));
  }

  __imp__sub_82A41758(ctx, base);

  if (trace) {
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "texture_descriptor",
        fmt::format("serialize lr=0x{:08X} hash=0x{:08X} name={} "
                    "table=0x{:08X} descriptor=0x{:08X} result=0x{:08X}",
                    original_ctx.lr,
                    resource_hash,
                    refii::vfs::NormalizeLooseAssetKey(name),
                    table_guest,
                    descriptor_guest,
                    ctx.r3.u32));
    REXLOG_INFO("Refii VFS texture descriptor serialize exit lr=0x{:08X} "
                "hash=0x{:08X} name={} table=0x{:08X} descriptor=0x{:08X} "
                "result=0x{:08X} descriptor_after=[{}] table_after=[{}]",
                original_ctx.lr,
                resource_hash,
                refii::vfs::NormalizeLooseAssetKey(name),
                table_guest,
                descriptor_guest,
                ctx.r3.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, descriptor_guest, 8),
                refii::vfs::FormatGuestWordsIfPointer(base,
                                                          table_guest,
                                                          12));
    refii::vfs::LogRefiiVfsChainVerdict(
        "texture_descriptor",
        static_cast<uint32_t>(original_ctx.lr),
        name,
        candidate,
        resolution.provider_relative_path,
        "native_descriptor_serializer",
        "sub_82A41758_forwarded",
        "TraceOnce",
        "audit_generated_ppc_for_collapse");
  }
}

REX_EXTERN(sub_82A41B48) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82A41B48);

  const PPCContext original_ctx = ctx;
  const auto begin = std::chrono::steady_clock::now();
  const bool trace =
      refii::vfs::ShouldTracePostVideoTextureOwnedHandoffProbe(
          "StartupVideoFrame_82A41B48") ||
      (refii::vfs::kTraceNonVfsBootFrameProbes &&
       refii::vfs::VfsTracingEnabled() &&
       (original_ctx.lr == 0x82389110 || original_ctx.lr == 0x82388FA8 ||
        refii::vfs::ShouldTracePostVideoBoundaryProbe(
            "PostVideoFrame_82A41B48")));
  if (trace) {
    REXLOG_INFO("VFS trace startup-video frame target build enter sub_82A41B48 "
                "lr=0x{:08X} args=[{}] frame_state=[{}] "
                "frame_source_words={} post_video=[{}] main_menu=[{}]",
                original_ctx.lr,
                refii::vfs::FormatPostVideoBoundaryArgs(base,
                                                            original_ctx),
                refii::vfs::FormatPostVideoFrameTeardownState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r4.u32, 8),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82A41B48(ctx, base);

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_82A41B48",
                                                    original_ctx.lr,
                                                    ctx.r3.u32,
                                                    elapsed_ms);
  }

  if (trace) {
    refii::vfs::RecordRefiiPostVideoUiEvent("StartupVideoFrame_82A41B48",
                                                original_ctx.lr,
                                                ctx.r3.u32,
                                                original_ctx.r3.u32,
                                                original_ctx.r4.u32,
                                                original_ctx.r5.u32);
    REXLOG_INFO("VFS trace startup-video frame target build exit sub_82A41B48 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
                "frame_state_after=[{}] post_video=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatPostVideoFrameTeardownState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (ctx.r3.u32 == 0 &&
      original_ctx.lr == 0x823C19D8u &&
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostLoadingAfterHolderBinkReject", 12)) {
    const std::string source_text =
        refii::vfs::ReadMaybeGuestString(base, original_ctx.r4.u32, 512);
    REXLOG_INFO("Refii VFS post-loading after-holder Bink reject "
                "lr=0x{:08X} child=0x{:08X} source=0x{:08X} "
                "source_text={} source_words=[{}] child_words=[{}] "
                "flags220_224=0x{:02X}/0x{:02X}/0x{:02X}/0x{:02X}/0x{:08X} "
                "elapsed_ms={} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                source_text.empty() ? "<unreadable>" : source_text,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r4.u32, 8),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 64),
                refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
                    ? refii::vfs::LoadGuestU8(base,
                                                  original_ctx.r3.u32 + 220)
                    : 0,
                refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
                    ? refii::vfs::LoadGuestU8(base,
                                                  original_ctx.r3.u32 + 221)
                    : 0,
                refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
                    ? refii::vfs::LoadGuestU8(base,
                                                  original_ctx.r3.u32 + 222)
                    : 0,
                refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
                    ? refii::vfs::LoadGuestU8(base,
                                                  original_ctx.r3.u32 + 223)
                    : 0,
                refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
                    ? refii::vfs::LoadGuestU32(base,
                                                   original_ctx.r3.u32 + 224)
                    : 0,
                elapsed_ms,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (elapsed_ms >= 250 &&
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoStageSlow_sub_82A41B48", 8)) {
    REXLOG_INFO("Refii VFS post-video stage slow "
                "stage=sub_82A41B48 lr=0x{:08X} result=0x{:08X} "
                "elapsed_ms={} stage_timing=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatRefiiPostVideoStageTimingForLog(),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
      original_ctx.lr, "StartupVideoFrame_82A41B48", base);
}

REX_EXTERN(sub_82389040) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82389040);

  const PPCContext original_ctx = ctx;
  const auto begin = std::chrono::steady_clock::now();
  const bool native_startup_audit =
      original_ctx.lr == 0x82377CD4 &&
      ShouldLogNativeStartupPlaybackAudit("NativeStartupFrame_82389040", 4,
                                          1);
  const bool trace =
      refii::vfs::ShouldTracePostVideoTextureOwnedHandoffProbe(
          "StartupVideoFrame_82389040") ||
      (refii::vfs::kTraceNonVfsBootFrameProbes &&
       refii::vfs::VfsTracingEnabled() &&
       original_ctx.lr != 0x82377CD4 &&
       refii::vfs::ShouldTracePostVideoBoundaryProbe(
           "PostVideoFrame_82389040"));
  if (native_startup_audit && !trace) {
    REXLOG_INFO("Refii VFS native startup frame construct enter sub_82389040 "
                "lr=0x{:08X} frame_object=0x{:08X} render_resource=0x{:08X} "
                "frame_source=0x{:08X} source_words={}",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                original_ctx.r5.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r5.u32, 4));
  }
  if (trace) {
    REXLOG_INFO("VFS trace startup-video frame object construct enter "
                "sub_82389040 lr=0x{:08X} args=[{}] frame_object=0x{:08X} "
                "render_resource=0x{:08X} frame_source=0x{:08X} "
                "frame_source_words={} post_video=[{}] main_menu=[{}]",
                original_ctx.lr,
                refii::vfs::FormatPostVideoBoundaryArgs(base,
                                                            original_ctx),
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                original_ctx.r5.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r5.u32, 8),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82389040(ctx, base);

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_82389040",
                                                    original_ctx.lr,
                                                    ctx.r3.u32,
                                                    elapsed_ms);
  }

  if (trace || (native_startup_audit && !trace)) {
    refii::vfs::RecordRefiiPostVideoUiEvent("StartupVideoFrame_82389040",
                                                original_ctx.lr,
                                                ctx.r3.u32,
                                                original_ctx.r3.u32,
                                                original_ctx.r4.u32,
                                                original_ctx.r5.u32);
  }
  if (native_startup_audit && !trace) {
    REXLOG_INFO("Refii VFS native startup frame construct exit sub_82389040 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
                "frame_object_after={}",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 8));
  }
  if (trace) {
    REXLOG_INFO("VFS trace startup-video frame object construct exit "
                "sub_82389040 lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
                "frame_object_after={} post_video=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 16),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (elapsed_ms >= 250 &&
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoStageSlow_sub_82389040", 8)) {
    REXLOG_INFO("Refii VFS post-video stage slow "
                "stage=sub_82389040 lr=0x{:08X} result=0x{:08X} "
                "elapsed_ms={} stage_timing=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatRefiiPostVideoStageTimingForLog(),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
      original_ctx.lr, "StartupVideoFrame_82389040", base);
}

REX_EXTERN(rex_lhRenderer_BeginScene) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhRenderer_BeginScene);

  const PPCContext original_ctx = ctx;
  const auto begin = std::chrono::steady_clock::now();
  const bool trace =
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "Renderer_BeginScene_PostVideo", 12);
  if (trace || (refii::vfs::kTraceNonVfsBootFrameProbes &&
                refii::vfs::VfsTracingEnabled())) {
    REXLOG_INFO("VFS trace Boot phase enter Renderer_BeginScene lr=0x{:08X} "
                "args=[{}] frame_chain=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
                refii::vfs::FormatCurrentPostVideoFrameChainState(base),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  // The first post-ready renderer call can block inside native code while the
  // frontend texture remains only hash-owned by Refii. Bridge the VFS-owned
  // texture before entering that native boundary.
  const bool pre_native_needs_texture_prime =
      refii::vfs::RefiiMainMenuTraceNeedsTexturePrime();
  const bool pre_native_needs_frontend_handoff =
      refii::vfs::RefiiMainMenuTraceNeedsFrontendHandoff();
  const bool pre_native_videos_ready =
      refii::vfs::RefiiStartupVideosSettledForMenuHandoff() ||
      refii::vfs::SkipStartupVideosEnabled();
  const bool pre_native_post_gameface_videos_ready =
      refii::vfs::RefiiStartupVideosReadyForMenuHandoff() ||
      refii::vfs::SkipStartupVideosEnabled();
  bool pre_native_loading_owner_finish = false;
  if (!pre_native_needs_texture_prime && !pre_native_needs_frontend_handoff &&
      pre_native_videos_ready &&
      refii::vfs::RefiiCollapsedLoadingSequenceReadyForOwnerRelease()) {
    uint32_t loading_owner =
        refii::vfs::RefiiRememberedLoadingScreenOwner().load(
            std::memory_order_relaxed);
    uint32_t sequence_handle = 0;
    const bool sequence_ready =
        refii::vfs::RefiiLoadingScreenSequenceReady(
            base, loading_owner, &sequence_handle);
    if (refii::vfs::LooksLikeGuestPointer(loading_owner) &&
        refii::vfs::LoadGuestU8(base, loading_owner + 177) == 0) {
      pre_native_loading_owner_finish =
          refii::vfs::TryFinishRefiiLoadingScreenOwnerFromCollapsedPipeline(
              original_ctx,
              base,
              loading_owner,
              "Renderer_BeginScene_PostReady",
              1,
              sequence_ready,
              sequence_handle);
    }
  }
  bool pre_native_post_gameface_frame_handoff = false;
  if (!pre_native_needs_texture_prime &&
      pre_native_post_gameface_videos_ready) {
    pre_native_post_gameface_frame_handoff =
        refii::vfs::TryAttachRefiiPostGameFaceRenderHandoffToActiveFrame(
            original_ctx,
            base,
            "Renderer_BeginScene_PreNativePostGameFaceFrame");
  }
  if (pre_native_needs_texture_prime && pre_native_videos_ready) {
    if (refii::vfs::VfsTracingEnabled() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "RendererBeginScenePreNativeHandoffGate", 8)) {
      REXLOG_INFO("Refii VFS renderer pre-native handoff gate "
                  "lr=0x{:08X} root=0x{:08X} needs_texture_prime=true "
                  "videos_ready=true frame_chain=[{}] handoff=[{}] "
                  "main_menu=[{}]",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  refii::vfs::FormatCurrentPostVideoFrameChainState(base),
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    }
    PPCContext pre_native_prime_ctx = original_ctx;
    const bool pre_native_button_prime =
        refii::vfs::TryPrimeRefiiFrontendTextureResource(
            pre_native_prime_ctx,
            base,
            0x73BCB48Fu,
            "Renderer_BeginScene_PreNativeButtonPrime");
    if (pre_native_button_prime && refii::vfs::VfsTracingEnabled() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "RendererBeginScenePreNativeButtonPrime", 8)) {
      REXLOG_INFO("Refii VFS renderer pre-native button texture prime "
                  "lr=0x{:08X} root=0x{:08X} frame_chain=[{}] handoff=[{}] "
                  "main_menu=[{}]",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  refii::vfs::FormatCurrentPostVideoFrameChainState(base),
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    }
    if (original_ctx.lr == 0x8238835Cu && pre_native_videos_ready) {
      refii::vfs::RecordRefiiPostVideoUiEvent(
          "Renderer_BeginScene_PostReadyNative",
          original_ctx.lr,
          original_ctx.r3.u32,
          original_ctx.r3.u32,
          original_ctx.r4.u32,
          original_ctx.r5.u32);
      if (refii::vfs::VfsTracingEnabled() ||
          refii::vfs::LooseAssetOverrideTracingEnabled()) {
        REXLOG_INFO("Refii VFS renderer post-ready native handoff "
                    "lr=0x{:08X} root=0x{:08X} "
                    "reason=menu_texture_refii_owned frame_chain=[{}] "
                    "handoff=[{}] main_menu=[{}]",
                    original_ctx.lr,
                    original_ctx.r3.u32,
                    refii::vfs::FormatCurrentPostVideoFrameChainState(base),
                    refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                    refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
        refii::vfs::LogRefiiVfsChainVerdict(
            "frontend_post_video",
            static_cast<uint32_t>(original_ctx.lr),
            "menu_texture_refii_owned",
            "",
            "",
            "post_video_renderer_handoff",
            "Renderer_BeginScene_PostReadyNative",
            "TraceOnce",
            "native_renderer_consumer_or_DB20");
      }
      if (pre_native_button_prime) {
        ctx = original_ctx;
        ctx.r3.u32 = 0;
        refii::vfs::RecordRefiiPostVideoUiEvent(
            "Renderer_BeginScene_PostReadyCollapsed",
            original_ctx.lr,
            ctx.r3.u32,
            original_ctx.r3.u32,
            original_ctx.r4.u32,
            original_ctx.r5.u32);
        if (refii::vfs::VfsTracingEnabled() ||
            refii::vfs::LooseAssetOverrideTracingEnabled()) {
          REXLOG_INFO("Refii VFS renderer post-ready native boundary "
                      "collapsed lr=0x{:08X} root=0x{:08X} "
                      "reason=refii_texture_prime_complete frame_chain=[{}] "
                      "handoff=[{}] main_menu=[{}]",
                      original_ctx.lr,
                      original_ctx.r3.u32,
                      refii::vfs::FormatCurrentPostVideoFrameChainState(base),
                      refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                      refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
          refii::vfs::LogRefiiVfsChainVerdict(
              "frontend_post_video",
              static_cast<uint32_t>(original_ctx.lr),
              "menu_texture_refii_owned",
              "",
              "",
              "post_video_renderer_handoff",
              "Renderer_BeginScene_PostReadyCollapsed",
              "Opened",
              "post_ready_frame_or_gameface");
        }
        return;
      }
    }
  } else if ((pre_native_needs_frontend_handoff || pre_native_videos_ready) &&
             refii::vfs::VfsTracingEnabled() &&
             refii::vfs::ShouldLogRepeatedVfsTraceOperation(
                 "RendererBeginScenePreNativeHandoffSkip", 8)) {
    REXLOG_INFO("Refii VFS renderer pre-native handoff skipped "
                "lr=0x{:08X} root=0x{:08X} needs_texture_prime={} "
                "needs_frontend_handoff={} videos_ready={} handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                pre_native_needs_texture_prime ? "true" : "false",
                pre_native_needs_frontend_handoff ? "true" : "false",
                pre_native_videos_ready ? "true" : "false",
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (pre_native_loading_owner_finish &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS renderer pre-native loading owner finish "
                "lr=0x{:08X} root=0x{:08X} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (pre_native_post_gameface_frame_handoff &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS renderer pre-native post-GameFace frame handoff "
                "lr=0x{:08X} root=0x{:08X} frame_chain=[{}] handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                refii::vfs::FormatCurrentPostVideoFrameChainState(base),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (pre_native_loading_owner_finish) {
    refii::vfs::TryFinalizeRefiiPostLoadingFrontendGeneratedState(
        original_ctx,
        base,
        "Renderer_BeginScene_PostReadyOwnerFinish",
        true,
        true);
  }

  __imp__rex_lhRenderer_BeginScene(ctx, base);
  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  refii::vfs::RecordRefiiPostVideoUiEvent("Renderer_BeginScene",
                                              original_ctx.lr,
                                              ctx.r3.u32,
                                              original_ctx.r3.u32,
                                              original_ctx.r4.u32,
                                              original_ctx.r5.u32);
  if (refii::vfs::RefiiMainMenuTraceNeedsFrontendHandoff() &&
      (refii::vfs::RefiiStartupVideosSettledForMenuHandoff() ||
       refii::vfs::SkipStartupVideosEnabled())) {
    PPCContext post_video_synthetic_ctx = original_ctx;
    refii::vfs::TryRunPostVideoFrontendSyntheticHandoff(
        post_video_synthetic_ctx,
        base,
        original_ctx.r3.u32,
        "Renderer_BeginScene_PostReadyCollapse");
  }

  if (trace || (refii::vfs::kTraceNonVfsBootFrameProbes &&
                refii::vfs::VfsTracingEnabled())) {
    REXLOG_INFO("VFS trace Boot phase exit Renderer_BeginScene lr=0x{:08X} "
                "result=0x{:08X} elapsed_ms={} frame_chain=[{}] "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                elapsed_ms,
                refii::vfs::FormatCurrentPostVideoFrameChainState(base),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (elapsed_ms >= 250 &&
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "RendererBeginSceneSlowBoundary", 8)) {
    REXLOG_INFO("Refii VFS renderer begin-scene slow boundary "
                "lr=0x{:08X} elapsed_ms={} frame_chain=[{}] "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                elapsed_ms,
                refii::vfs::FormatCurrentPostVideoFrameChainState(base),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    refii::vfs::LogRefiiVfsChainVerdict(
        "frontend_post_video",
        static_cast<uint32_t>(original_ctx.lr),
        "startup_bik_pair",
        "art/videos/microsoft_logo.bik|art/videos/lionhead_logo.bik",
        "art/videos/microsoft_logo.bik|art/videos/lionhead_logo.bik",
        "renderer_frame",
        "Renderer_BeginScene_slow",
        "TraceOnce",
        "renderer_or_shader_frame_time_before_texture_consumer_open");
  }
  if (refii::vfs::kTraceNonVfsBootFrameProbes &&
      refii::vfs::VfsTracingEnabled()) {
    static std::chrono::steady_clock::time_point last_idle_log;
    const auto now = std::chrono::steady_clock::now();
    if (last_idle_log.time_since_epoch().count() == 0 ||
        now - last_idle_log >= std::chrono::seconds(5)) {
      last_idle_log = now;
      const auto& stats = refii::vfs::LooseStats();
      REXLOG_INFO("Refii VFS boot idle context lr=0x{:08X} last_op=[{}] "
                  "main_menu=[{}] post_video=[{}] streams={} records={} "
                  "reads={} banks={} handles={}/{}",
                  original_ctx.lr,
                  refii::vfs::FormatRefiiLastVfsOperationForLog(),
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog(),
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                  stats.streams_opened.load(std::memory_order_relaxed),
                  stats.active_stream_records.load(std::memory_order_relaxed),
                  stats.active_read_requests.load(std::memory_order_relaxed),
                  stats.active_synthetic_banks.load(std::memory_order_relaxed),
                  stats.active_private_file_handles.load(std::memory_order_relaxed),
                  stats.active_private_find_handles.load(std::memory_order_relaxed));
      refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(original_ctx.lr,
                                                            "BootIdle",
                                                            base);
    }
  }
}

REX_EXTERN(rex_lhMeshResource_Deserialize) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhMeshResource_Deserialize);

  const PPCContext original_ctx = ctx;
  const uint32_t descriptor_guest = original_ctx.r5.u32;
  const uint32_t resource_hash =
      descriptor_guest != 0 ? refii::vfs::LoadGuestU32(base, descriptor_guest) : 0;
  const bool trace_deserialize =
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::VfsVerboseIoTracingEnabled() &&
      (refii::vfs::kTraceMeshModelAudioDiagnostics ||
       refii::vfs::IsBootFontResourceHash(resource_hash) ||
       (refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "MeshResource_Deserialize_PostVideo", 16)));
  if (trace_deserialize) {
    REXLOG_INFO("VFS trace MeshResource_Deserialize enter lr=0x{:08X} "
                "out=0x{:08X} arg4=0x{:08X} descriptor=0x{:08X} "
                "resource_hash=0x{:08X} descriptor_words={} handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                descriptor_guest,
                resource_hash,
                refii::vfs::FormatGuestWords(base, descriptor_guest, 12),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    if (refii::vfs::IsBootFontResourceHash(resource_hash)) {
      refii::vfs::RecordRefiiMainMenuBlockerEvent(
          "font_deserialize",
          fmt::format("enter lr=0x{:08X} hash=0x{:08X} out=0x{:08X} "
                      "descriptor=0x{:08X}",
                      original_ctx.lr,
                      resource_hash,
                      original_ctx.r3.u32,
                      descriptor_guest));
    }
  }

  __imp__rex_lhMeshResource_Deserialize(ctx, base);

  if (trace_deserialize) {
    const uint32_t mesh_guest =
        original_ctx.r3.u32 != 0
            ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32)
            : 0;
    REXLOG_INFO("VFS trace MeshResource_Deserialize exit lr=0x{:08X} "
                "result=0x{:08X} mesh=0x{:08X} mesh_words={} handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                mesh_guest,
                refii::vfs::FormatGuestWords(base, mesh_guest, 20),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    if (refii::vfs::IsBootFontResourceHash(resource_hash)) {
      refii::vfs::RecordRefiiMainMenuBlockerEvent(
          "font_deserialize",
          fmt::format("exit lr=0x{:08X} hash=0x{:08X} result=0x{:08X} "
                      "mesh=0x{:08X} mesh_words={}",
                      original_ctx.lr,
                      resource_hash,
                      ctx.r3.u32,
                      mesh_guest,
                      refii::vfs::FormatGuestWords(base, mesh_guest, 12)));
    }
  }
}

REX_EXTERN(rex_lhMeshResource_Load) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhMeshResource_Load);

  const PPCContext original_ctx = ctx;
  const uint32_t descriptor_guest = original_ctx.r5.u32;
  const uint32_t resource_hash =
      descriptor_guest != 0 ? refii::vfs::LoadGuestU32(base, descriptor_guest) : 0;
  const bool trace_load =
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::VfsVerboseIoTracingEnabled() &&
      (refii::vfs::kTraceMeshModelAudioDiagnostics ||
       refii::vfs::IsBootFontResourceHash(resource_hash) ||
       (refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "MeshResource_Load_PostVideo", 16)));
  if (trace_load) {
    REXLOG_INFO("VFS trace MeshResource_Load enter lr=0x{:08X} "
                "out=0x{:08X} container=0x{:08X} descriptor=0x{:08X} "
                "resource_hash=0x{:08X} out_before={} container_words={} "
                "descriptor_words={} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                descriptor_guest,
                resource_hash,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 4),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r4.u32, 8),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, descriptor_guest, 12),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__rex_lhMeshResource_Load(ctx, base);

  if (trace_load) {
    const uint32_t loaded_guest =
        original_ctx.r3.u32 != 0
            ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32)
            : 0;
    REXLOG_INFO("VFS trace MeshResource_Load exit lr=0x{:08X} "
                "result=0x{:08X} out=0x{:08X} loaded=0x{:08X} "
                "out_after={} loaded_words={} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                original_ctx.r3.u32,
                loaded_guest,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 4),
                refii::vfs::FormatGuestWordsIfPointer(base, loaded_guest, 20),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

REX_EXTERN(sub_8286FDB0) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_8286FDB0);

  const PPCContext original_ctx = ctx;
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldTracePostVideoFontCaller(original_ctx.lr) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          fmt::format("PostVideoFontSelect:{:08X}", original_ctx.lr), 16);
  if (trace) {
    REXLOG_INFO("VFS trace PostVideoFontSelect enter lr=0x{:08X} "
                "out=0x{:08X} selector=0x{:08X} out_before={} "
                "selector_words={} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 4),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r4.u32, 8),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_8286FDB0(ctx, base);

  if (trace) {
    const uint32_t loaded_guest =
        original_ctx.r3.u32 != 0
            ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32)
            : 0;
    REXLOG_INFO("VFS trace PostVideoFontSelect exit lr=0x{:08X} "
                "result=0x{:08X} out=0x{:08X} loaded=0x{:08X} "
                "out_after={} loaded_words={} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                original_ctx.r3.u32,
                loaded_guest,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 4),
                refii::vfs::FormatGuestWordsIfPointer(base, loaded_guest, 16),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "font_select",
        fmt::format("exit lr=0x{:08X} result=0x{:08X} loaded=0x{:08X} "
                    "out_after={}",
                    original_ctx.lr,
                    ctx.r3.u32,
                    loaded_guest,
                    refii::vfs::FormatGuestWordsIfPointer(
                        base, original_ctx.r3.u32, 4)));
  }
}

REX_EXTERN(sub_8218DFB8) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_8218DFB8);

  const PPCContext original_ctx = ctx;
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldTracePostVideoFontCaller(original_ctx.lr) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          fmt::format("PostVideoFontAssign:{:08X}", original_ctx.lr), 16);
  if (trace) {
    REXLOG_INFO("VFS trace PostVideoFontAssign enter lr=0x{:08X} "
                "target=0x{:08X} source=0x{:08X} target_before={} "
                "source_words={} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 6),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r4.u32, 6),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_8218DFB8(ctx, base);

  if (original_ctx.lr == 0x828716B4) {
    refii::vfs::MaybeSignalRefiiPostGameFacePackageHandoff(
        original_ctx,
        base,
        "post_video_font:assign",
        original_ctx.r3.u32);
  }

  if (trace) {
    REXLOG_INFO("VFS trace PostVideoFontAssign exit lr=0x{:08X} "
                "result=0x{:08X} target=0x{:08X} source=0x{:08X} "
                "target_after={} source_words={} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 6),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r4.u32, 6),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "font_assign",
        fmt::format("exit lr=0x{:08X} result=0x{:08X} target=0x{:08X} "
                    "target_after={}",
                    original_ctx.lr,
                    ctx.r3.u32,
                    original_ctx.r3.u32,
                    refii::vfs::FormatGuestWordsIfPointer(
                        base, original_ctx.r3.u32, 6)));
  }
}

REX_EXTERN(sub_82231CB8) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82231CB8);

  const PPCContext original_ctx = ctx;
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldTracePostVideoFontCaller(original_ctx.lr) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          fmt::format("PostVideoFontTempRelease:{:08X}", original_ctx.lr), 16);
  if (trace) {
    REXLOG_INFO("VFS trace PostVideoFontTempRelease enter lr=0x{:08X} "
                "object=0x{:08X} object_words={} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 6),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82231CB8(ctx, base);

  if (original_ctx.lr == 0x828716BC) {
    refii::vfs::MaybeSignalRefiiPostGameFacePackageHandoff(
        original_ctx,
        base,
        "post_video_font:temp_release",
        original_ctx.r3.u32);
  }

  if (trace) {
    REXLOG_INFO("VFS trace PostVideoFontTempRelease exit lr=0x{:08X} "
                "result=0x{:08X} object=0x{:08X} object_after={} "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                original_ctx.r3.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 6),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "font_temp_release",
        fmt::format("exit lr=0x{:08X} result=0x{:08X} object=0x{:08X} "
                    "object_after={}",
                    original_ctx.lr,
                    ctx.r3.u32,
                    original_ctx.r3.u32,
                    refii::vfs::FormatGuestWordsIfPointer(
                        base, original_ctx.r3.u32, 6)));
  }
}

REX_EXTERN(rex_lhMeshResource_Validate) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhMeshResource_Validate);

  const PPCContext original_ctx = ctx;
  const bool trace =
      refii::vfs::kTraceMeshModelAudioDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldTraceMeshResourceValidate(original_ctx.lr) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "MeshResource_Validate", 8);
  if (trace) {
    const uint32_t container_guest = original_ctx.r4.u32;
    const uint32_t key_guest = original_ctx.r5.u32;
    REXLOG_INFO("VFS trace MeshResource_Validate enter lr=0x{:08X} "
                "out=0x{:08X} container=0x{:08X} key=0x{:08X} "
                "key_ascii={} key_words={} container_words={}",
                original_ctx.lr,
                original_ctx.r3.u32,
                container_guest,
                key_guest,
                refii::vfs::FormatGuestAsciiBytes(base, key_guest, 32),
                refii::vfs::FormatGuestWords(base, key_guest, 4),
                refii::vfs::FormatGuestWords(base, container_guest, 12));
  }

  __imp__rex_lhMeshResource_Validate(ctx, base);

  if (trace) {
    const uint32_t out_guest = original_ctx.r3.u32;
    const uint32_t result_container =
        out_guest != 0 ? refii::vfs::LoadGuestU32(base, out_guest) : 0;
    const uint32_t result_node =
        out_guest != 0 ? refii::vfs::LoadGuestU32(base, out_guest + 4) : 0;
    REXLOG_INFO("VFS trace MeshResource_Validate exit lr=0x{:08X} "
                "out=0x{:08X} result_container=0x{:08X} "
                "result_node=0x{:08X} result_words={} node_words={}",
                original_ctx.lr,
                out_guest,
                result_container,
                result_node,
                refii::vfs::FormatGuestWords(base, out_guest, 2),
                refii::vfs::FormatGuestWords(base, result_node, 8));
  }
}

REX_EXTERN(sub_82B70868) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B70868);

  const PPCContext original_ctx = ctx;
  if (refii::vfs::kTraceMeshModelAudioDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace MeshResource lookup enter lr=0x{:08X} "
                "resource=0x{:08X} key=0x{:08X} key_ascii={} "
                "key_words={} resource_words={}",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                refii::vfs::FormatGuestAsciiBytes(base, original_ctx.r4.u32, 32),
                refii::vfs::FormatGuestWords(base, original_ctx.r4.u32, 4),
                refii::vfs::FormatGuestWords(base, original_ctx.r3.u32, 20));
  }

  __imp__sub_82B70868(ctx, base);

  if (refii::vfs::kTraceMeshModelAudioDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace MeshResource lookup exit lr=0x{:08X} "
                "resource=0x{:08X} key=0x{:08X} result=0x{:08X}",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                ctx.r3.u32);
  }
}

REX_EXTERN(rex_lhRenderer_StartThread) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhRenderer_StartThread);

  const PPCContext original_ctx = ctx;
  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::VfsVerboseIoTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase enter Renderer_StartThread lr=0x{:08X} "
                "renderer=0x{:08X}",
                original_ctx.lr,
                original_ctx.r3.u32);
  }

  __imp__rex_lhRenderer_StartThread(ctx, base);

  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::VfsVerboseIoTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase exit Renderer_StartThread lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(rex_lhGameInit_SetupUI) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhGameInit_SetupUI);

  const PPCContext original_ctx = ctx;
  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::VfsVerboseIoTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase enter SetupUI lr=0x{:08X} game=0x{:08X} "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                original_ctx.r3.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  if (refii::vfs::RefiiLooseAssetBackendEnabled() &&
      refii::vfs::SkipStartupVideosEnabled()) {
    ctx.r3.u32 = 1;
    refii::vfs::RecordRefiiPostVideoUiEvent("SetupUISkipStartupVideos",
                                                original_ctx.lr,
                                                ctx.r3.u32,
                                                original_ctx.r3.u32,
                                                original_ctx.r4.u32,
                                                original_ctx.r5.u32);
    if ((refii::vfs::kTraceFrontendHandoffDiagnostics &&
         refii::vfs::VfsTracingEnabled() &&
         refii::vfs::VfsVerboseIoTracingEnabled()) ||
        refii::vfs::LooseAssetOverrideTracingEnabled()) {
      REXLOG_INFO("VFS trace Boot phase skip SetupUI startup videos "
                  "lr=0x{:08X} game=0x{:08X} handoff=[{}]",
                  original_ctx.lr,
                  original_ctx.r3.u32,
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog());
    }
    return;
  }

  __imp__rex_lhGameInit_SetupUI(ctx, base);
  refii::vfs::RecordRefiiPostVideoUiEvent("SetupUI",
                                              original_ctx.lr,
                                              ctx.r3.u32,
                                              original_ctx.r3.u32,
                                              original_ctx.r4.u32,
                                              original_ctx.r5.u32);

  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::VfsVerboseIoTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase exit SetupUI lr=0x{:08X} result=0x{:08X} "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

REX_EXTERN(rex_lhGame_UpdateThread) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhGame_UpdateThread);

  const PPCContext original_ctx = ctx;
  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace Game_UpdateThread enter lr=0x{:08X} game=0x{:08X}",
                original_ctx.lr,
                original_ctx.r3.u32);
  }

  __imp__rex_lhGame_UpdateThread(ctx, base);

  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace Game_UpdateThread exit lr=0x{:08X} result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(rex_lhGameData_Init) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhGameData_Init);

  const PPCContext original_ctx = ctx;
  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace GameData_Init enter lr=0x{:08X} game=0x{:08X}",
                original_ctx.lr,
                original_ctx.r3.u32);
  }

  __imp__rex_lhGameData_Init(ctx, base);

  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace GameData_Init exit lr=0x{:08X} result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(sub_822F3D90) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_822F3D90);

  const PPCContext original_ctx = ctx;
  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace DLCStartup runner enter lr=0x{:08X} "
                "request_manager=0x{:08X}",
                original_ctx.lr,
                original_ctx.r3.u32);
  }

  __imp__sub_822F3D90(ctx, base);

  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace DLCStartup runner exit lr=0x{:08X} result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(sub_8237FE30) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_8237FE30);

  const PPCContext original_ctx = ctx;
  const bool trace = ShouldTraceGameDataBootBoundary(original_ctx.lr);
  if (trace) {
    REXLOG_INFO("VFS trace GameData content find enter lr=0x{:08X} "
                "{} callback=0x{:08X}/{} arg=0x{:08X} {}",
                original_ctx.lr,
                FormatGuestOutSlot(base, original_ctx.r3.u32),
                original_ctx.r5.u32,
                ContentIteratorCallbackName(original_ctx.r5.u32),
                original_ctx.r6.u32,
                FormatContentManagerIteratorState(base, original_ctx.r4.u32));
  }

  if (TryRunRefiiDlcStartupContentFind(ctx, base, original_ctx)) {
    return;
  }

  __imp__sub_8237FE30(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace GameData content find exit lr=0x{:08X} "
                "result=0x{:08X} {}",
                original_ctx.lr,
                ctx.r3.u32,
                FormatGuestOutSlot(base, original_ctx.r3.u32));
  }
}

REX_EXTERN(sub_8237FD60) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_8237FD60);

  const PPCContext original_ctx = ctx;
  const bool trace = ShouldTraceGameDataBootBoundary(original_ctx.lr);
  if (trace) {
    REXLOG_INFO("VFS trace GameData content for-each enter lr=0x{:08X} "
                "arg=0x{:08X} {}",
                original_ctx.lr,
                original_ctx.r4.u32,
                FormatContentManagerIteratorState(base, original_ctx.r3.u32));
  }

  __imp__sub_8237FD60(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace GameData content for-each exit lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(rex_lhRenderer_BeginGPUFrame) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhRenderer_BeginGPUFrame);

  const PPCContext original_ctx = ctx;
  const bool trace = ShouldTraceGameDataBootBoundary(original_ctx.lr);
  if (trace) {
    REXLOG_INFO("VFS trace GameData GPU frame begin enter lr=0x{:08X}",
                original_ctx.lr);
  }

  __imp__rex_lhRenderer_BeginGPUFrame(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace GameData GPU frame begin exit lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(rex_lhRenderer_EndGPUFrame) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhRenderer_EndGPUFrame);

  const PPCContext original_ctx = ctx;
  const bool trace = ShouldTraceGameDataBootBoundary(original_ctx.lr);
  if (trace) {
    REXLOG_INFO("VFS trace GameData GPU frame end enter lr=0x{:08X}",
                original_ctx.lr);
  }

  __imp__rex_lhRenderer_EndGPUFrame(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace GameData GPU frame end exit lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(sub_82A4E998) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82A4E998);

  const PPCContext original_ctx = ctx;
  const bool trace = ShouldTraceGameDataBootBoundary(original_ctx.lr);
  if (trace) {
    REXLOG_INFO("VFS trace GameData renderer resource pair enter lr=0x{:08X} "
                "left='{}' right='{}'",
                original_ctx.lr,
                refii::vfs::ReadMaybeGuestString(base, original_ctx.r3.u32),
                refii::vfs::ReadMaybeGuestString(base, original_ctx.r4.u32));
  }

  __imp__sub_82A4E998(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace GameData renderer resource pair exit lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(sub_822F4948) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_822F4948);

  const PPCContext original_ctx = ctx;
  const bool trace = ShouldTraceGameDataBootBoundary(original_ctx.lr);
  if (trace) {
    REXLOG_INFO("VFS trace GameData resource register enter lr=0x{:08X} "
                "game=0x{:08X} name='{}' flag=0x{:08X}",
                original_ctx.lr,
                original_ctx.r3.u32,
                refii::vfs::ReadMaybeGuestString(base, original_ctx.r4.u32),
                original_ctx.r5.u32);
  }

  __imp__sub_822F4948(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace GameData resource register exit lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(sub_822F4210) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_822F4210);

  const PPCContext original_ctx = ctx;
  const bool trace = ShouldTraceGameDataBootBoundary(original_ctx.lr);
  if (trace) {
    REXLOG_INFO("VFS trace GameData resource setup enter lr=0x{:08X} "
                "entry=0x{:08X}",
                original_ctx.lr,
                original_ctx.r3.u32);
  }

  __imp__sub_822F4210(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace GameData resource setup exit lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(sub_822F4750) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_822F4750);

  const PPCContext original_ctx = ctx;
  const bool trace = ShouldTraceGameDataBootBoundary(original_ctx.lr);
  if (original_ctx.lr == 0x82319244u) {
    refii::vfs::RecordRefiiFrontEndAudioBankLateBind(
        ReadGameDataAudioLateBindName(base, original_ctx.r4.u32),
        original_ctx.lr);
  }
  if (trace) {
    REXLOG_INFO("VFS trace GameData resource late-bind enter lr=0x{:08X} "
                "game=0x{:08X} entry=0x{:08X}",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32);
  }

  __imp__sub_822F4750(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace GameData resource late-bind exit lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

REX_EXTERN(rex_lhFile_CloseAndRelease) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhFile_CloseAndRelease);

  const PPCContext original_ctx = ctx;
  const bool trace = ShouldTraceGameDataBootBoundary(original_ctx.lr);
  if (trace) {
    REXLOG_INFO("VFS trace GameData file close/release enter lr=0x{:08X} "
                "file=0x{:08X} words=0x{:08X} 0x{:08X} 0x{:08X} 0x{:08X}",
                original_ctx.lr,
                original_ctx.r3.u32,
                refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
                    ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 0)
                    : 0,
                refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
                    ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 4)
                    : 0,
                refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
                    ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 8)
                    : 0,
                refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
                    ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 12)
                    : 0);
  }

  if (original_ctx.lr == 0x822F4208 &&
      refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)) {
    const uint32_t vtable =
        refii::vfs::LoadGuestU32(base, original_ctx.r3.u32);
    const uint32_t total_stream =
        refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 8);
    const uint32_t read_stream =
        refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 12);
    const bool has_refii_stream =
        refii::vfs::IsRefiiHostDiskStream(base, total_stream) ||
        refii::vfs::IsRefiiHostDiskStream(base, read_stream);
    const bool known_lh_file_vtable =
        vtable == 0x8200DF08 || vtable == 0x820A4928;
    if (!known_lh_file_vtable) {
      PPCContext release_ctx = original_ctx;
      if (refii::vfs::IsRefiiHostDiskStream(base, total_stream)) {
        refii::vfs::ReleaseGuestObject(release_ctx, base, total_stream);
      }
      if (read_stream != total_stream &&
          refii::vfs::IsRefiiHostDiskStream(base, read_stream)) {
        refii::vfs::ReleaseGuestObject(release_ctx, base, read_stream);
      }
      if (trace || refii::vfs::VfsTracingEnabled()) {
        REXLOG_WARN("Refii VFS skipped invalid GameData lhFile final close "
                    "lr=0x{:08X} file=0x{:08X} vtable=0x{:08X} "
                    "total=0x{:08X} read=0x{:08X} released_refii={}",
                    original_ctx.lr,
                    original_ctx.r3.u32,
                    vtable,
                    total_stream,
                    read_stream,
                    has_refii_stream);
      }
      ctx.r3.u32 = original_ctx.r3.u32;
      return;
    }
  }

  __imp__rex_lhFile_CloseAndRelease(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace GameData file close/release exit lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}
