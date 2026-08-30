#include "vfs_hooks_private.h"

#if REFII_VFS_HAS_RENDERER_RUNTIME
#include "renderer_runtime.h"

namespace refii::renderer {
bool PlumeRendererBackendEnabled();
}  // namespace refii::renderer
#endif

#include <rex/system/xthread.h>

namespace {

constexpr uint32_t kFableRendererFrameCriticalSection = 0x8349F7FCu;
constexpr uint32_t kFableCriticalSectionRecursionOffset = 0x14u;
constexpr uint32_t kFableCriticalSectionOwnerOffset = 0x18u;
std::atomic<uint32_t> g_fable_renderer_dynamic_critical_section{0};
thread_local uint32_t g_dlc_startup_content_manager = 0;
std::atomic<uint32_t> g_native_dlc_iterator_trace_count{0};

std::string FormatContentIteratorEntries(uint8_t *base,
                                         uint32_t manager_guest) {
  if (base == nullptr || !refii::vfs::LooksLikeGuestPointer(manager_guest)) {
    return "entries=[]";
  }

  const uint32_t begin = refii::vfs::LoadGuestU32(base, manager_guest + 40);
  const uint32_t end = refii::vfs::LoadGuestU32(base, manager_guest + 44);
  if (!refii::vfs::LooksLikeGuestPointer(begin) || end < begin ||
      ((end - begin) % sizeof(uint32_t)) != 0) {
    return "entries=[invalid]";
  }

  const uint32_t count = (end - begin) / sizeof(uint32_t);
  std::string result = "entries=[";
  const uint32_t shown = std::min<uint32_t>(count, 8u);
  for (uint32_t i = 0; i < shown; ++i) {
    const uint32_t object =
        refii::vfs::LoadGuestU32(base, begin + i * sizeof(uint32_t));
    const uint32_t vtable = refii::vfs::LooksLikeGuestPointer(object)
                                ? refii::vfs::LoadGuestU32(base, object)
                                : 0;
    result += fmt::format("{}{}:0x{:08X}/vt=0x{:08X}",
                          i == 0 ? "" : ",", i, object, vtable);
  }
  if (shown < count) {
    result += fmt::format(",...+{}", count - shown);
  }
  result += "]";
  return result;
}

bool PlumeStartupVideoWaitHandoffEnabled() {
#if REFII_VFS_HAS_RENDERER_RUNTIME
  return refii::renderer::PlumeRendererBackendEnabled();
#else
  return false;
#endif
}

bool ShouldLogNativeStartupPlaybackAudit(std::string_view key,
                                         uint32_t first_count,
                                         uint32_t interval) {
  if (!refii::vfs::kTraceFrontendHandoffDiagnostics ||
      !refii::vfs::VfsTracingEnabled() ||
      !refii::vfs::VfsVerboseIoTracingEnabled()) {
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
      !refii::vfs::VfsVerboseIoTracingEnabled()) {
    return false;
  }

  static std::mutex mutex;
  static uint32_t count = 0;

  std::lock_guard<std::mutex> lock(mutex);
  ++count;
  return count <= 8 || (count % 20) == 0;
}

void MaybeLogStartupCriticalSectionContention(uint8_t *base,
                                              const PPCContext &ctx) {
  const uint32_t critical_section_guest = ctx.r4.u32;
  if (base == nullptr || critical_section_guest < 0x40000000u ||
      critical_section_guest >= 0x50000000u) {
    return;
  }

  const uint32_t lock_count =
      refii::vfs::LoadGuestU32(base, critical_section_guest + 0x10u);
  const uint32_t recursion_count =
      refii::vfs::LoadGuestU32(base, critical_section_guest + 0x14u);
  const uint32_t owning_thread =
      refii::vfs::LoadGuestU32(base, critical_section_guest + 0x18u);
  if (ctx.lr == 0x823883E8u) {
    // IDA: sub_82388380 passes its renderer object (a1) directly to
    // sub_82200770/rex_lhScopedLock_Acquire. The object is dynamically
    // allocated, so retain the observed native address for startup waits.
    g_fable_renderer_dynamic_critical_section.store(
        critical_section_guest, std::memory_order_release);
  }
  if (lock_count == 0xFFFFFFFFu || owning_thread == 0) {
    return;
  }

  const uint32_t waiting_thread =
      rex::system::XThread::IsInThread()
          ? rex::system::XThread::GetCurrentThread()->guest_object()
          : 0u;
  // A recursively entered RTL critical section remains owned and has a
  // non-negative lock count. That is native ownership, not contention. The
  // 3D Engine takes this path once per frame; treating it as a waiter hid the
  // actual VFS state in thousands of misleading diagnostics.
  if (waiting_thread != 0 && waiting_thread == owning_thread) {
    return;
  }

  static std::atomic<uint32_t> contention_logs{0};
  const uint32_t count =
      contention_logs.fetch_add(1, std::memory_order_relaxed) + 1u;
  if (count > 32u && count % 120u != 0u) {
    return;
  }

  REXLOG_INFO(
      "Refii VFS startup critical section contention #{} "
      "cs=0x{:08X} lr=0x{:08X} scoped=0x{:08X} lock_count=0x{:08X} "
      "recursion={} owner=0x{:08X} waiter=0x{:08X}",
      count,
      critical_section_guest,
      ctx.lr,
      ctx.r3.u32,
      lock_count,
      recursion_count,
      owning_thread,
      waiting_thread);
}

uint32_t CurrentFableGuestThreadObject() {
  if (!rex::system::XThread::IsInThread()) {
    return 0;
  }
  return rex::system::XThread::GetCurrentThread()->guest_object();
}

uint32_t FableCriticalSectionRecursionDepthForCurrentThread(
    uint8_t *base, uint32_t critical_section_guest) {
  if (base == nullptr || critical_section_guest == 0) {
    return 0;
  }
  const uint32_t owner_guest = refii::vfs::LoadGuestU32(
      base, critical_section_guest + kFableCriticalSectionOwnerOffset);
  if (owner_guest == 0 || owner_guest != CurrentFableGuestThreadObject()) {
    return 0;
  }
  return std::min<uint32_t>(
      refii::vfs::LoadGuestU32(
          base, critical_section_guest + kFableCriticalSectionRecursionOffset),
      16u);
}

uint32_t FableRendererCriticalSectionForCurrentThread(uint8_t *base) {
  const uint32_t dynamic =
      g_fable_renderer_dynamic_critical_section.load(std::memory_order_acquire);
  if (FableCriticalSectionRecursionDepthForCurrentThread(base, dynamic) != 0) {
    return dynamic;
  }
  return kFableRendererFrameCriticalSection;
}

uint32_t ReleaseFableCriticalSectionForStartupVideoWait(
    const PPCContext &ctx, uint8_t *base, uint32_t critical_section_guest) {
  const uint32_t release_count =
      FableCriticalSectionRecursionDepthForCurrentThread(base,
                                                         critical_section_guest);
  for (uint32_t i = 0; i < release_count; ++i) {
    PPCContext critical_ctx = ctx;
    critical_ctx.r3.u32 = critical_section_guest;
    __imp__RtlLeaveCriticalSection(critical_ctx, base);
  }
  return release_count;
}

void ReacquireFableCriticalSectionForStartupVideoWait(
    const PPCContext &ctx, uint8_t *base, uint32_t critical_section_guest,
    uint32_t acquire_count) {
  for (uint32_t i = 0; i < acquire_count; ++i) {
    PPCContext critical_ctx = ctx;
    critical_ctx.r3.u32 = critical_section_guest;
    __imp__RtlEnterCriticalSection(critical_ctx, base);
  }
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

std::string ReadGameDataAudioLateBindName(uint8_t *base, uint32_t entry_guest) {
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
      !refii::vfs::VfsTracingEnabled()) {
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

std::string FormatContentManagerIteratorState(uint8_t *base,
                                              uint32_t manager_guest) {
  if (base == nullptr || !refii::vfs::LooksLikeGuestPointer(manager_guest)) {
    return fmt::format("manager=0x{:08X}", manager_guest);
  }

  const uint32_t begin = refii::vfs::LoadGuestU32(base, manager_guest + 40);
  const uint32_t end = refii::vfs::LoadGuestU32(base, manager_guest + 44);
  const uint32_t capacity = refii::vfs::LoadGuestU32(base, manager_guest + 48);
  const uint32_t count = end >= begin && ((end - begin) % sizeof(uint32_t)) == 0
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
                     manager_guest, begin, end, capacity,
                     count == 0xFFFFFFFFu ? std::string("invalid")
                                          : fmt::format("{}", count),
                     first_object, first_vtable, first_add_ref, first_release);
}

std::string FormatGuestOutSlot(uint8_t *base, uint32_t out_guest) {
  if (base == nullptr || !refii::vfs::LooksLikeGuestPointer(out_guest)) {
    return fmt::format("out=0x{:08X}", out_guest);
  }
  return fmt::format("out=0x{:08X}->0x{:08X}", out_guest,
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

uint32_t ContentManagerIteratorFirstObject(uint8_t *base,
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

bool TryRunRefiiDlcStartupContentFind(PPCContext &ctx, uint8_t *base,
                                      const PPCContext &original_ctx) {
  if (!refii::vfs::FrontendSyntheticHandoffEnabled() ||
      original_ctx.lr != 0x822F4094 || original_ctx.r5.u32 != 0x822F3D90 ||
      !refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)) {
    return false;
  }

  // This legacy recovery path collapses the content-manager iterator to its
  // first object and invokes DLCStartup inline. It must never participate in
  // the natural loose-VFS path: doing so can publish a GUI_FRONT_END resource
  // manager before the permanent-bank mounter installs its owner. Native then
  // destroys that old slot after constructing the replacement, and the old
  // destructor clears the replacement's singleton (dword_83496B08). Keep the
  // shortcut available only with the explicitly enabled synthetic frontend
  // recovery machinery; the default path preserves native iterator ordering.

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
                original_ctx.lr, original_ctx.r3.u32, first_object,
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
                original_ctx.lr, callback_ctx.r3.u32,
                keep_scanning ? "true" : "false", original_ctx.r3.u32,
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
    REXLOG_INFO(
        "VFS trace InitModelHeaders enter lr=0x{:08X} "
        "streams={} active_streams={} synthetic_banks={}",
        original_ctx.lr,
        refii::vfs::LooseStats().streams_opened.load(std::memory_order_relaxed),
        refii::vfs::RefiiHostStreamRecordCount(),
        refii::vfs::SyntheticBankRecordCount());
  }

  __imp__rex_lhResource_InitModelHeaders(ctx, base);

  if (log_trace) {
    REXLOG_INFO(
        "VFS trace InitModelHeaders exit lr=0x{:08X} "
        "result=0x{:08X} streams={} active_streams={} "
        "synthetic_banks={}",
        original_ctx.lr, ctx.r3.u32,
        refii::vfs::LooseStats().streams_opened.load(std::memory_order_relaxed),
        refii::vfs::RefiiHostStreamRecordCount(),
        refii::vfs::SyntheticBankRecordCount());
  }
}

REX_EXTERN(rex_lhScopedLock_Acquire) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhScopedLock_Acquire);


  const PPCContext original_ctx = ctx;
  const auto begin = std::chrono::steady_clock::now();
  refii::vfs::SanitizeCriticalSectionBeforeEnter(base, ctx.r4.u32, ctx.lr);
  MaybeLogStartupCriticalSectionContention(base, ctx);
  __imp__rex_lhScopedLock_Acquire(ctx, base);
  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (elapsed_ms >= 100 &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "StartupCriticalSectionAcquireSlow", 16)) {
    const uint32_t critical_section_guest = original_ctx.r4.u32;
    REXLOG_INFO(
        "Refii VFS startup critical section acquire slow "
        "elapsed_ms={} cs=0x{:08X} lr=0x{:08X} scoped=0x{:08X} "
        "lock_count=0x{:08X} recursion={} owner=0x{:08X} result=0x{:08X}",
        elapsed_ms,
        critical_section_guest,
        original_ctx.lr,
        original_ctx.r3.u32,
        refii::vfs::LoadGuestU32(base, critical_section_guest + 0x10u),
        refii::vfs::LoadGuestU32(base, critical_section_guest + 0x14u),
        refii::vfs::LoadGuestU32(base, critical_section_guest + 0x18u),
        ctx.r3.u32);
  }
}

// D3D_InitializeRingBuffer, 0x82BA2830 — the no-GPU latch is armed IN-LINE, on the guest's
// own thread, the moment ring setup completes. The boot race this kills: with no GPU,
// `VdEnableRingBufferRPtrWriteBack` is a no-op, and the first latch-clear reader commits the
// device to GPU-completion waits that never finish — `D3D_CDevice_KickOff` (0x821e8f70) parks
// in `D3D_CDevice_BlockOnFence`, and `D3D_CDevice_SetFence` (0x822986ec) silently skips the
// completion write-back for any fence issued while the bit is down. The host's step-loop arm
// polls at 30 Hz and lost that race roughly half of one day's launches.
//
// This site is provably early enough: the wait gate also requires `dword_83497144`
// (HSIO-training-failed, 0x82ba6be0), which is still 0 until the device-init tail — after this
// function returns and after this write. So no wait-capable reader can ever see the bit clear.
// Re-runs on a device reset re-arm the same byte; the OR is idempotent. The hostbridge's own
// arm (`ArmGuestNoGpuMode`) stays as the belt for any path that skips ring init.
REX_EXTERN(sub_82BA2830) {
  REX_FUNC_PROLOGUE();

  const uint32_t device = ctx.r3.u32;
  __imp__sub_82BA2830(ctx, base);

  if (device != 0u) {
    const uint8_t before = base[device + 0x2ABDu];
    base[device + 0x2ABDu] = static_cast<uint8_t>(before | 0x02u);
    REXLOG_INFO(
        "Refii boot: no-GPU latch armed in-line after ring-buffer init "
        "(device=0x{:08X}, 0x{:02X} -> 0x{:02X})",
        device, before, before | 0x02u);
  }
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
                original_ctx.lr, ctx.r3.u32);
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
                original_ctx.lr, ctx.r3.u32,
                refii::vfs::DescribeInitModelHeaderSummary(base));
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
                original_ctx.lr, ctx.r3.u32,
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
                original_ctx.lr, ctx.r3.u32);
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
                original_ctx.lr, original_ctx.r3.u32);
  }

  __imp__rex_lhResource_CreateModelCache(ctx, base);

  if (refii::vfs::kTraceNonVfsBootFrameProbes &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase exit CreateModelCache lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr, ctx.r3.u32);
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
                original_ctx.lr, original_ctx.r3.u32, original_ctx.r4.u32);
  }

  __imp__rex_lhResource_SetupModelCache(ctx, base);

  if (refii::vfs::kTraceNonVfsBootFrameProbes &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase exit SetupModelCache lr=0x{:08X} "
                "result=0x{:08X} out_words={}",
                original_ctx.lr, ctx.r3.u32,
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
                original_ctx.lr, original_ctx.r3.u32, original_ctx.r4.u32,
                original_ctx.r5.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__rex_lhResource_SetLoadMode(ctx, base);
  refii::vfs::RecordRefiiPostVideoUiEvent(
      "SetLoadMode", original_ctx.lr, ctx.r3.u32, original_ctx.r3.u32,
      original_ctx.r4.u32, original_ctx.r5.u32);

  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::VfsVerboseIoTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase exit SetLoadMode lr=0x{:08X} "
                "result=0x{:08X} handoff=[{}] main_menu=[{}]",
                original_ctx.lr, ctx.r3.u32,
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
                original_ctx.lr, ctx.r3.u32);
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
                original_ctx.lr, ctx.r3.u32);
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
                original_ctx.lr, original_ctx.r3.u32);
  }

  __imp__rex_lhRenderer_Construct(ctx, base);

  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase exit Renderer_Construct lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr, ctx.r3.u32);
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
                original_ctx.lr, original_ctx.r3.u32, original_ctx.r4.u32,
                original_ctx.r5.u32);
  }

  __imp__rex_lhRenderer_Setup(ctx, base);

  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase exit Renderer_Setup lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr, ctx.r3.u32);
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
  const double corrected_startup_frame_delta = RefiiStartupVideoFrameDelta(
      native_frame_delta, startup_playback_critical);
  const bool startup_frame_delta_corrected =
      startup_playback_critical &&
      corrected_startup_frame_delta > native_frame_delta;
  if (startup_frame_delta_corrected) {
    ctx.f1.f64 = corrected_startup_frame_delta;
  }
  const bool startup_frame_progress =
      startup_playback_critical && ShouldLogNativeStartupFrameProgress();
  const bool native_startup_audit =
      native_startup_frame && ShouldLogNativeStartupPlaybackAudit(
                                  "NativeStartupFrame_82389210", 3, 300);
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
                original_ctx.lr, original_ctx.r3.u32, ctx.f1.f64,
                native_frame_delta,
                startup_frame_delta_corrected ? "true" : "false",
                refii::vfs::FormatPostVideoFrameTimingState(
                    base, original_ctx.r3.u32, ctx.f1.f64));
  }
  if (native_startup_audit) {
    REXLOG_INFO("Refii VFS native startup frame enter sub_82389210 "
                "lr=0x{:08X} owner=0x{:08X} delta={} timing=[{}]",
                original_ctx.lr, original_ctx.r3.u32, original_ctx.f1.f64,
                refii::vfs::FormatPostVideoFrameTimingState(
                    base, original_ctx.r3.u32, original_ctx.f1.f64));
  }
  if (trace) {
    REXLOG_INFO(
        "VFS trace post-video frame update enter sub_82389210 "
        "lr=0x{:08X} delta={} timing=[{}] args=[{}] chain=[{}] "
        "handoff=[{}] main_menu=[{}]",
        original_ctx.lr, original_ctx.f1.f64,
        refii::vfs::FormatPostVideoFrameTimingState(base, original_ctx.r3.u32,
                                                    original_ctx.f1.f64),
        refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
        refii::vfs::FormatPostVideoFrameChainState(base, original_ctx.r3.u32),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  bool pre_native_post_gameface_frame_handoff = false;
  if (!startup_playback_critical &&
      refii::vfs::RefiiStartupVideosReadyForMenuHandoff()) {
    pre_native_post_gameface_frame_handoff =
        refii::vfs::TryAttachRefiiPostGameFaceRenderHandoffToActiveFrame(
            original_ctx, base, "PostVideoFrame_82389210_PreNative");
  }
  if (pre_native_post_gameface_frame_handoff &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO(
        "Refii VFS post-video frame pre-native post-GameFace handoff "
        "lr=0x{:08X} owner=0x{:08X} frame_chain=[{}] handoff=[{}] "
        "main_menu=[{}]",
        original_ctx.lr, original_ctx.r3.u32,
        refii::vfs::FormatPostVideoFrameChainState(base, original_ctx.r3.u32),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  const uint32_t startup_frame_object =
      refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 4u);
  const uint32_t startup_bink_movie = startup_frame_object != 0
                                          ? refii::vfs::LoadGuestU32(
                                                base, startup_frame_object + 76u)
                                          : 0u;
  const uint8_t startup_finished_before =
      startup_frame_object != 0
          ? refii::vfs::LoadGuestU8(base, startup_frame_object + 233u)
          : 0u;
  const uint8_t startup_failed_before =
      startup_frame_object != 0
          ? refii::vfs::LoadGuestU8(base, startup_frame_object + 244u)
          : 0u;
  __imp__sub_82389210(ctx, base);
  const uint8_t startup_finished_after =
      startup_frame_object != 0
          ? refii::vfs::LoadGuestU8(base, startup_frame_object + 233u)
          : 0u;
  const uint8_t startup_failed_after =
      startup_frame_object != 0
          ? refii::vfs::LoadGuestU8(base, startup_frame_object + 244u)
          : 0u;
  if (native_startup_frame &&
      (startup_finished_before != startup_finished_after ||
       startup_failed_before != startup_failed_after ||
       startup_finished_before != 0u || startup_failed_before != 0u)) {
    REXLOG_INFO(
        "Refii VFS startup-video owner lifecycle owner=0x{:08X} "
        "frame_object=0x{:08X} bink=0x{:08X} critical={} "
        "finished={}->{} failed={}->{} frame={}/{}",
        original_ctx.r3.u32, startup_frame_object, startup_bink_movie,
        startup_playback_critical, startup_finished_before,
        startup_finished_after, startup_failed_before, startup_failed_after,
        startup_bink_movie != 0
            ? refii::vfs::LoadGuestU32(base, startup_bink_movie + 12u)
            : 0u,
        startup_bink_movie != 0
            ? refii::vfs::LoadGuestU32(base, startup_bink_movie + 8u)
            : 0u);
  }

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::VfsTracingEnabled() && !startup_playback_critical &&
      (refii::vfs::RefiiStartupVideosReadyForMenuHandoff() ||
       refii::vfs::RefiiMainMenuTraceIsHashOnly() ||
       refii::vfs::RefiiMainMenuTraceNeedsFrontendHandoff())) {
    refii::vfs::RecordRefiiPostVideoUiEvent(
        "PostVideoFrame_82389210", original_ctx.lr, ctx.r3.u32,
        original_ctx.r3.u32, original_ctx.r4.u32, original_ctx.r5.u32);
  }
  if (!startup_playback_critical) {
    refii::vfs::MaybeApplyRefiiVfsPostVideoHandoffTimerPatch(
        base, original_ctx.lr, original_ctx.r3.u32, "PostVideoFrame_82389210");
  }

  if (trace) {
    REXLOG_INFO(
        "VFS trace post-video frame update exit sub_82389210 "
        "lr=0x{:08X} result=0x{:08X} elapsed_ms={} delta={} "
        "timing_after=[{}] "
        "args_after=[{}] chain_after=[{}] "
        "milestones=Renderer_BeginScene_lr_823892F0 "
        "dynamic_slot_lr_82389304 sub_82388A18_lr_82389308 "
        "sub_82A41B00_lr_82389310 final_owner_plus4_clear "
        "handoff=[{}] main_menu=[{}]",
        original_ctx.lr, ctx.r3.u32, elapsed_ms, original_ctx.f1.f64,
        refii::vfs::FormatPostVideoFrameTimingState(base, original_ctx.r3.u32,
                                                    original_ctx.f1.f64),
        refii::vfs::FormatPostVideoBoundaryArgs(base, ctx),
        refii::vfs::FormatPostVideoFrameChainState(base, original_ctx.r3.u32),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (native_startup_audit && !trace) {
    REXLOG_INFO("Refii VFS native startup frame exit sub_82389210 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} delta={} "
                "native_delta={} corrected={} "
                "timing_after=[{}]",
                original_ctx.lr, ctx.r3.u32, elapsed_ms, ctx.f1.f64,
                native_frame_delta,
                startup_frame_delta_corrected ? "true" : "false",
                refii::vfs::FormatPostVideoFrameTimingState(
                    base, original_ctx.r3.u32, ctx.f1.f64));
  }
  if (startup_frame_progress) {
    REXLOG_INFO("Refii VFS native startup frame progress exit "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} delta={} "
                "timing_after=[{}]",
                original_ctx.lr, ctx.r3.u32, elapsed_ms, original_ctx.f1.f64,
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
                original_ctx.lr, elapsed_ms, original_ctx.f1.f64, next_boundary,
                refii::vfs::FormatPostVideoFrameTimingState(
                    base, original_ctx.r3.u32, original_ctx.f1.f64),
                refii::vfs::FormatRefiiPostVideoStageTimingForLog(),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    refii::vfs::LogRefiiVfsChainVerdict(
        "frontend_post_video", static_cast<uint32_t>(original_ctx.lr),
        "startup_bik_pair",
        "art/videos/microsoft_logo.bik|art/videos/lionhead_logo.bik",
        "art/videos/microsoft_logo.bik|art/videos/lionhead_logo.bik",
        "native_buffered_stream", "sub_82389210_slow_frame", "TraceOnce",
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
    REXLOG_INFO(
        "VFS trace post-video dynamic handoff getter enter sub_823781E0 "
        "lr=0x{:08X} args=[{}] getter=[{}] handoff=[{}] "
        "main_menu=[{}]",
        original_ctx.lr,
        refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
        refii::vfs::FormatPostVideoDynamicHandoffGetterState(
            base, original_ctx.r3.u32),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_823781E0(ctx, base);

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_823781E0", original_ctx.lr,
                                                ctx.r3.u32, elapsed_ms);
  }

  if (trace) {
    refii::vfs::RecordRefiiPostVideoUiEvent(
        "PostVideoFrame_823781E0", original_ctx.lr, ctx.r3.u32,
        original_ctx.r3.u32, original_ctx.r4.u32, original_ctx.r5.u32);
    REXLOG_INFO("VFS trace post-video dynamic handoff getter exit sub_823781E0 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
                "result_wait_state=[{}] getter_after=[{}] handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr, ctx.r3.u32, elapsed_ms,
                refii::vfs::FormatPostVideoFrameWaitState(base, ctx.r3.u32),
                refii::vfs::FormatPostVideoDynamicHandoffGetterState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (elapsed_ms >= 250 && refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoStageSlow_sub_823781E0", 8)) {
    REXLOG_INFO("Refii VFS post-video stage slow "
                "stage=sub_823781E0 lr=0x{:08X} result=0x{:08X} "
                "elapsed_ms={} stage_timing=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr, ctx.r3.u32, elapsed_ms,
                refii::vfs::FormatRefiiPostVideoStageTimingForLog(),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
      original_ctx.lr, "PostVideoFrame_823781E0", base);
}

REX_EXTERN(sub_823889A8) {
  REX_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t owner_guest = original_ctx.r3.u32;
  const uint32_t handoff_guest =
      owner_guest != 0 ? refii::vfs::LoadGuestU32(base, owner_guest + 4) : 0;
  const bool native_startup_frame_wait = original_ctx.lr == 0x823890ECu;


  const auto begin = std::chrono::steady_clock::now();
  refii::vfs::RecordRefiiRenderProducerScheduleSnapshot(
      base, owner_guest, original_ctx.lr, original_ctx.r6.u32,
      original_ctx.r5.u32, original_ctx.f1.f64);
  const bool trace = refii::vfs::ShouldTracePostVideoTextureOwnedHandoffProbe(
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
                original_ctx.lr, owner_guest, handoff_guest,
                refii::vfs::FormatPostVideoFrameWaitState(base, owner_guest),
                refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  const uint32_t renderer_critical_section =
      FableRendererCriticalSectionForCurrentThread(base);
  const uint32_t render_frame_lock_release_count =
      native_startup_frame_wait
          ? ReleaseFableCriticalSectionForStartupVideoWait(
                ctx, base, renderer_critical_section)
          : 0u;
  __imp__sub_823889A8(ctx, base);
  if (render_frame_lock_release_count != 0) {
    ReacquireFableCriticalSectionForStartupVideoWait(
        original_ctx, base, renderer_critical_section,
        render_frame_lock_release_count);
  }

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_823889A8", original_ctx.lr,
                                                ctx.r3.u32, elapsed_ms);
  }
  if (trace) {
    refii::vfs::RecordRefiiPostVideoUiEvent(
        "StartupVideoFrame_823889A8", original_ctx.lr, ctx.r3.u32, owner_guest,
        handoff_guest, original_ctx.r5.u32);
    REXLOG_INFO("VFS trace startup-video frame handshake exit sub_823889A8 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} state_after=[{}] "
                "post_video=[{}] main_menu=[{}]",
                original_ctx.lr, ctx.r3.u32, elapsed_ms,
                refii::vfs::FormatPostVideoFrameWaitState(base, owner_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (native_startup_frame_wait &&
      (elapsed_ms >= 50 || render_frame_lock_release_count != 0) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "StartupVideoFrameWaitNativeHandoff", 16)) {
    REXLOG_INFO(
        "Refii VFS startup-video native handoff wait "
        "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
        "released_render_frame_lock={} owner=0x{:08X} handoff=0x{:08X} "
        "state_after=[{}]",
        original_ctx.lr,
        ctx.r3.u32,
        elapsed_ms,
        render_frame_lock_release_count,
        owner_guest,
        handoff_guest,
        refii::vfs::FormatPostVideoFrameWaitState(base, owner_guest));
  }
  if (elapsed_ms >= 250 && refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoStageSlow_sub_823889A8", 8)) {
    REXLOG_INFO("Refii VFS post-video stage slow "
                "stage=sub_823889A8 lr=0x{:08X} result=0x{:08X} "
                "elapsed_ms={} stage_timing=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr, ctx.r3.u32, elapsed_ms,
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
      base, owner_guest, original_ctx.lr, original_ctx.r6.u32,
      original_ctx.r5.u32, original_ctx.f1.f64);
  const bool trace = refii::vfs::ShouldTracePostVideoTextureOwnedHandoffProbe(
                         "RenderProducerSchedule_82388A88") ||
                     (refii::vfs::kTraceNonVfsBootFrameProbes &&
                      refii::vfs::VfsTracingEnabled() &&
                      (original_ctx.lr == 0x82276D68 ||
                       refii::vfs::ShouldTracePostVideoBoundaryProbe(
                           "RenderProducerSchedule_82388A88")));
  if (trace) {
    REXLOG_INFO(
        "VFS trace render producer schedule enter sub_82388A88 "
        "lr=0x{:08X} owner=0x{:08X} handoff=0x{:08X} "
        "frame_time={} source124=0x{:08X} source128=0x{:08X} "
        "args=[{}] producer=[{}] handoff_trace=[{}] main_menu=[{}]",
        original_ctx.lr, owner_guest, handoff_guest, original_ctx.f1.f64,
        original_ctx.r6.u32, original_ctx.r5.u32,
        refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
        refii::vfs::FormatPostVideoRenderProducerState(base, handoff_guest),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82388A88(ctx, base);

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_82388A88", original_ctx.lr,
                                                ctx.r3.u32, elapsed_ms);
  }
  if (trace || (refii::vfs::kTraceNonVfsBootFrameProbes &&
                refii::vfs::VfsTracingEnabled() &&
                refii::vfs::RefiiStartupVideosReadyForMenuHandoff())) {
    refii::vfs::RecordRefiiPostVideoUiEvent(
        "RenderProducerSchedule_82388A88", original_ctx.lr, ctx.r3.u32,
        owner_guest, handoff_guest, original_ctx.r5.u32);
  }
  if (trace) {
    REXLOG_INFO(
        "VFS trace render producer schedule exit sub_82388A88 "
        "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
        "producer_after=[{}] handoff_trace=[{}] main_menu=[{}]",
        original_ctx.lr, ctx.r3.u32, elapsed_ms,
        refii::vfs::FormatPostVideoRenderProducerState(base, handoff_guest),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (elapsed_ms >= 250 && refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoStageSlow_sub_82388A88", 8)) {
    REXLOG_INFO(
        "Refii VFS post-video stage slow "
        "stage=sub_82388A88 lr=0x{:08X} result=0x{:08X} "
        "elapsed_ms={} producer=[{}] stage_timing=[{}] "
        "handoff=[{}] main_menu=[{}]",
        original_ctx.lr, ctx.r3.u32, elapsed_ms,
        refii::vfs::FormatPostVideoRenderProducerState(base, handoff_guest),
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
  const bool trace = refii::vfs::RefiiPostVideoVfsHandoffTraceActive() ||
                     refii::vfs::ShouldTracePostVideoBoundaryProbe(
                         "FrontendReadyTail_823865F8");
  if (trace) {
    REXLOG_INFO("VFS trace frontend ready tail enter sub_823865F8 "
                "lr=0x{:08X} args=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_823865F8(ctx, base);

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_823865F8", original_ctx.lr,
                                                ctx.r3.u32, elapsed_ms);
  }
  if (trace) {
    refii::vfs::RecordRefiiPostVideoUiEvent(
        "FrontendReadyTail_823865F8", original_ctx.lr, ctx.r3.u32,
        original_ctx.r3.u32, original_ctx.r4.u32, original_ctx.r5.u32);
    REXLOG_INFO("VFS trace frontend ready tail exit sub_823865F8 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
                "stage_timing=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr, ctx.r3.u32, elapsed_ms,
                refii::vfs::FormatRefiiPostVideoStageTimingForLog(),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (elapsed_ms >= 250 && refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoStageSlow_sub_823865F8", 8)) {
    REXLOG_INFO("Refii VFS post-video stage slow "
                "stage=sub_823865F8 lr=0x{:08X} result=0x{:08X} "
                "elapsed_ms={} stage_timing=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr, ctx.r3.u32, elapsed_ms,
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
  const bool trace = refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
                     refii::vfs::VfsTracingEnabled() &&
                     refii::vfs::ShouldLogRepeatedVfsTraceOperation(
                         "GeneratedRenderReady_82361128", 32);

  ::__imp__sub_82361128(ctx, base);

  bool seeded_ready_payload = false;
  if (ctx.r3.u32 == 0 && ready_payload == 0 &&
      refii::vfs::MaybeSeedRefiiGeneratedRenderReadyPayload(original_ctx, base,
                                                            ready_object)) {
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
                original_ctx.lr, ready_object, ready_payload, current_payload,
                ready_lock, ctx.r3.u32, seeded_ready_payload ? "true" : "false",
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
  const bool trace = refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
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
                original_ctx.lr, ready_object, before_payload, after_payload,
                refii::vfs::FormatGuestWords(base, original_ctx.r4.u32, 8),
                original_ctx.r5.u32, original_ctx.r6.u32, ctx.r3.u32,
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
  const bool trace = refii::vfs::ShouldTracePostVideoTextureOwnedHandoffProbe(
                         "RenderProducerFadeSchedule_82388B48") ||
                     (refii::vfs::kTraceNonVfsBootFrameProbes &&
                      refii::vfs::VfsTracingEnabled() &&
                      refii::vfs::ShouldTracePostVideoBoundaryProbe(
                          "RenderProducerFadeSchedule_82388B48"));
  if (trace) {
    REXLOG_INFO(
        "VFS trace render producer fade schedule enter sub_82388B48 "
        "lr=0x{:08X} owner=0x{:08X} handoff=0x{:08X} "
        "f1={} f2={} r6=0x{:08X} r7=0x{:08X} args=[{}] "
        "producer=[{}] handoff_trace=[{}] main_menu=[{}]",
        original_ctx.lr, owner_guest, handoff_guest, original_ctx.f1.f64,
        original_ctx.f2.f64, original_ctx.r6.u32, original_ctx.r7.u32,
        refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
        refii::vfs::FormatPostVideoRenderProducerState(base, handoff_guest),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82388B48(ctx, base);

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_82388B48", original_ctx.lr,
                                                ctx.r3.u32, elapsed_ms);
  }
  if (trace) {
    refii::vfs::RecordRefiiPostVideoUiEvent(
        "RenderProducerFadeSchedule_82388B48", original_ctx.lr, ctx.r3.u32,
        owner_guest, handoff_guest, original_ctx.r6.u32);
    REXLOG_INFO(
        "VFS trace render producer fade schedule exit sub_82388B48 "
        "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
        "producer_after=[{}] handoff_trace=[{}] main_menu=[{}]",
        original_ctx.lr, ctx.r3.u32, elapsed_ms,
        refii::vfs::FormatPostVideoRenderProducerState(base, handoff_guest),
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
  const bool trace = refii::vfs::kTraceFrontendHandoffDiagnostics &&
                     refii::vfs::VfsTracingEnabled() &&
                     refii::vfs::ShouldLogRepeatedVfsTraceOperation(
                         "RenderProducer_82388040", 8);
  if (trace) {
    REXLOG_INFO(
        "VFS trace render producer enter sub_82388040 "
        "lr=0x{:08X} handoff=0x{:08X} producer=[{}] "
        "handoff_trace=[{}] main_menu=[{}]",
        original_ctx.lr, handoff_guest,
        refii::vfs::FormatPostVideoRenderProducerState(base, handoff_guest),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82388040(ctx, base);

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_82388040", original_ctx.lr,
                                                ctx.r3.u32, elapsed_ms);
  }
  if (trace ||
      (elapsed_ms >= 250 && refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
       refii::vfs::ShouldLogRepeatedVfsTraceOperation(
           "RenderProducer_82388040_exit", 8))) {
    REXLOG_INFO(
        "VFS trace render producer exit sub_82388040 "
        "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
        "producer_after=[{}] stage_timing=[{}] "
        "handoff_trace=[{}] main_menu=[{}]",
        original_ctx.lr, ctx.r3.u32, elapsed_ms,
        refii::vfs::FormatPostVideoRenderProducerState(base, handoff_guest),
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
          "TextureDescriptor_Ctor", resource_hash,
          static_cast<uint32_t>(original_ctx.lr), &resolution, &name,
          &candidate, &source);
  const bool main_menu_texture =
      refii_owned_texture && refii::vfs::IsMainMenuTextureHashTraceAsset(name);
  const bool trace =
      refii_owned_texture &&
      (refii::vfs::VfsTracingEnabled() ||
       refii::vfs::LooseAssetOverrideTracingEnabled() || main_menu_texture) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          fmt::format("TextureDescriptorConstruct:{:08X}:{}", resource_hash,
                      refii::vfs::NormalizeLooseAssetKey(name)),
          main_menu_texture ? 32 : 64);

  if (trace) {
    REXLOG_INFO(
        "Refii VFS texture descriptor ctor enter lr=0x{:08X} "
        "hash=0x{:08X} name={} out=0x{:08X} mode={} hash_ptr=0x{:08X} "
        "ref_ptr=0x{:08X} ref_object=0x{:08X} r7={} r8={} "
        "candidate={} source={} provider_relative={} out_before=[{}] "
        "ref_words=[{}]",
        original_ctx.lr, resource_hash,
        refii::vfs::NormalizeLooseAssetKey(name), descriptor_guest,
        original_ctx.r4.u32, hash_pointer_guest, ref_pointer_guest,
        ref_pointer_guest == 0
            ? 0
            : refii::vfs::LoadGuestU32(base, ref_pointer_guest),
        original_ctx.r7.u32, original_ctx.r8.u32, candidate, source,
        resolution.provider_relative_path,
        refii::vfs::FormatGuestWordsIfPointer(base, descriptor_guest, 8),
        refii::vfs::FormatGuestWordsIfPointer(base, ref_pointer_guest, 4));
  }

  if (base != nullptr && descriptor_guest != 0 && hash_pointer_guest != 0 &&
      ref_pointer_guest != 0) {
    refii::vfs::BuildTextureResourceDescriptor(
        ctx, base, descriptor_guest, original_ctx.r4.u32, hash_pointer_guest,
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
      descriptor_guest, original_ctx.lr, resource_hash, descriptor_vtable,
      descriptor_vtable == 0
          ? 0
          : refii::vfs::LoadGuestU32(base, descriptor_vtable + 12),
      descriptor_vtable == 0
          ? 0
          : refii::vfs::LoadGuestU32(base, descriptor_vtable + 16),
      name, candidate, source, refii_owned_texture);

  if (trace) {
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "texture_descriptor",
        fmt::format(
            "ctor lr=0x{:08X} hash=0x{:08X} name={} out=0x{:08X} "
            "candidate={} source={} descriptor=[{}]",
            original_ctx.lr, resource_hash,
            refii::vfs::NormalizeLooseAssetKey(name), descriptor_guest,
            candidate, source,
            refii::vfs::FormatGuestWordsIfPointer(base, descriptor_guest, 8)));
    REXLOG_INFO(
        "Refii VFS texture descriptor ctor exit lr=0x{:08X} "
        "hash=0x{:08X} name={} out=0x{:08X} result=0x{:08X} "
        "descriptor=[{}] vtable=[{}]",
        original_ctx.lr, resource_hash,
        refii::vfs::NormalizeLooseAssetKey(name), descriptor_guest, ctx.r3.u32,
        refii::vfs::FormatGuestWordsIfPointer(base, descriptor_guest, 8),
        refii::vfs::FormatGuestObjectVTable(base, descriptor_guest, 8));
    refii::vfs::LogRefiiVfsChainVerdict(
        "texture_descriptor", static_cast<uint32_t>(original_ctx.lr), name,
        candidate, resolution.provider_relative_path,
        "Refii_descriptor_builder", "sub_82A415C0_collapsed", "CollapseNow",
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
          "TextureDescriptor_Serialize", resource_hash,
          static_cast<uint32_t>(original_ctx.lr), &resolution, &name,
          &candidate, &source);
  const bool main_menu_texture =
      refii_owned_texture && refii::vfs::IsMainMenuTextureHashTraceAsset(name);
  const bool trace =
      refii_owned_texture &&
      (refii::vfs::VfsTracingEnabled() ||
       refii::vfs::LooseAssetOverrideTracingEnabled() || main_menu_texture ||
       refii::vfs::ShouldLogRepeatedVfsTraceOperation(
           "TextureDescriptorSerialize", 64));

  if (trace) {
    REXLOG_INFO(
        "Refii VFS texture descriptor serialize enter lr=0x{:08X} "
        "hash=0x{:08X} name={} table=0x{:08X} descriptor=0x{:08X} "
        "candidate={} source={} provider_relative={} "
        "descriptor_words=[{}] table_words=[{}]",
        original_ctx.lr, resource_hash,
        refii::vfs::NormalizeLooseAssetKey(name), table_guest, descriptor_guest,
        candidate, source, resolution.provider_relative_path,
        refii::vfs::FormatGuestWordsIfPointer(base, descriptor_guest, 8),
        refii::vfs::FormatGuestWordsIfPointer(base, table_guest, 12));
  }

  __imp__sub_82A41758(ctx, base);

  if (trace) {
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "texture_descriptor",
        fmt::format("serialize lr=0x{:08X} hash=0x{:08X} name={} "
                    "table=0x{:08X} descriptor=0x{:08X} result=0x{:08X}",
                    original_ctx.lr, resource_hash,
                    refii::vfs::NormalizeLooseAssetKey(name), table_guest,
                    descriptor_guest, ctx.r3.u32));
    REXLOG_INFO(
        "Refii VFS texture descriptor serialize exit lr=0x{:08X} "
        "hash=0x{:08X} name={} table=0x{:08X} descriptor=0x{:08X} "
        "result=0x{:08X} descriptor_after=[{}] table_after=[{}]",
        original_ctx.lr, resource_hash,
        refii::vfs::NormalizeLooseAssetKey(name), table_guest, descriptor_guest,
        ctx.r3.u32,
        refii::vfs::FormatGuestWordsIfPointer(base, descriptor_guest, 8),
        refii::vfs::FormatGuestWordsIfPointer(base, table_guest, 12));
    refii::vfs::LogRefiiVfsChainVerdict(
        "texture_descriptor", static_cast<uint32_t>(original_ctx.lr), name,
        candidate, resolution.provider_relative_path,
        "native_descriptor_serializer", "sub_82A41758_forwarded", "TraceOnce",
        "audit_generated_ppc_for_collapse");
  }
}

REX_EXTERN(sub_82A41B48) {
  REX_FUNC_PROLOGUE();


  // Bink movie open. Capture the frame-buffer block BEFORE the passthrough below, which
  // returns early whenever the loose-asset backend is off — i.e. always, in a bank
  // build — so anything after it never runs.
  //
  // BINKFRAMEBUFFERS is inline at player+68 (sub_82A42D20: `v2 = a1 + 68;
  // memset(a1 + 68, 0, 0x78); BinkGetFrameBuffersInfo(*(a1 + 64), v2)`), so the ADDRESS
  // is valid on entry even though the contents are filled during the call. The host
  // polls, so it picks the contents up on a later frame.
  refii::vfs::NoteBinkMovieOpen(ctx.r3.u32, base);

  // r4 is the file name handed to BinkOpen (`sub_832BB4B8(a2, *(a1 + 224))`). Capturing
  // it is what lets the host play the video itself rather than scraping the guest's
  // decoded planes — no dependence on the guest's render path or its frame timing.
  refii::vfs::NoteBinkVideoRequested(base, ctx.r4.u32);

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
    REXLOG_INFO(
        "VFS trace startup-video frame target build enter sub_82A41B48 "
        "lr=0x{:08X} args=[{}] frame_state=[{}] "
        "frame_source_words={} post_video=[{}] main_menu=[{}]",
        original_ctx.lr,
        refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
        refii::vfs::FormatPostVideoFrameTeardownState(base,
                                                      original_ctx.r3.u32),
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r4.u32, 8),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82A41B48(ctx, base);

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_82A41B48", original_ctx.lr,
                                                ctx.r3.u32, elapsed_ms);
  }

  if (trace) {
    refii::vfs::RecordRefiiPostVideoUiEvent(
        "StartupVideoFrame_82A41B48", original_ctx.lr, ctx.r3.u32,
        original_ctx.r3.u32, original_ctx.r4.u32, original_ctx.r5.u32);
    REXLOG_INFO("VFS trace startup-video frame target build exit sub_82A41B48 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
                "frame_state_after=[{}] post_video=[{}] main_menu=[{}]",
                original_ctx.lr, ctx.r3.u32, elapsed_ms,
                refii::vfs::FormatPostVideoFrameTeardownState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (ctx.r3.u32 == 0 && original_ctx.lr == 0x823C19D8u &&
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostLoadingAfterHolderBinkReject", 12)) {
    const std::string source_text =
        refii::vfs::ReadMaybeGuestString(base, original_ctx.r4.u32, 512);
    REXLOG_INFO(
        "Refii VFS post-loading after-holder Bink reject "
        "lr=0x{:08X} child=0x{:08X} source=0x{:08X} "
        "source_text={} source_words=[{}] child_words=[{}] "
        "flags220_224=0x{:02X}/0x{:02X}/0x{:02X}/0x{:02X}/0x{:08X} "
        "elapsed_ms={} handoff=[{}] main_menu=[{}]",
        original_ctx.lr, original_ctx.r3.u32, original_ctx.r4.u32,
        source_text.empty() ? "<unreadable>" : source_text,
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r4.u32, 8),
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r3.u32, 64),
        refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
            ? refii::vfs::LoadGuestU8(base, original_ctx.r3.u32 + 220)
            : 0,
        refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
            ? refii::vfs::LoadGuestU8(base, original_ctx.r3.u32 + 221)
            : 0,
        refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
            ? refii::vfs::LoadGuestU8(base, original_ctx.r3.u32 + 222)
            : 0,
        refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
            ? refii::vfs::LoadGuestU8(base, original_ctx.r3.u32 + 223)
            : 0,
        refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
            ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 224)
            : 0,
        elapsed_ms, refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (elapsed_ms >= 250 && refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoStageSlow_sub_82A41B48", 8)) {
    REXLOG_INFO("Refii VFS post-video stage slow "
                "stage=sub_82A41B48 lr=0x{:08X} result=0x{:08X} "
                "elapsed_ms={} stage_timing=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr, ctx.r3.u32, elapsed_ms,
                refii::vfs::FormatRefiiPostVideoStageTimingForLog(),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(
      original_ctx.lr, "StartupVideoFrame_82A41B48", base);
}

REX_EXTERN(sub_82389040) {
  REX_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const auto begin = std::chrono::steady_clock::now();
  const bool native_startup_frame_construct =
      original_ctx.lr == 0x82377CD4u;
  const bool native_startup_audit =
      native_startup_frame_construct &&
      ShouldLogNativeStartupPlaybackAudit("NativeStartupFrame_82389040", 4, 1);
  const bool trace =
      refii::vfs::ShouldTracePostVideoTextureOwnedHandoffProbe(
          "StartupVideoFrame_82389040") ||
      (refii::vfs::kTraceNonVfsBootFrameProbes &&
       refii::vfs::VfsTracingEnabled() && original_ctx.lr != 0x82377CD4 &&
       refii::vfs::ShouldTracePostVideoBoundaryProbe(
           "PostVideoFrame_82389040"));
  if (native_startup_audit && !trace) {
    REXLOG_INFO(
        "Refii VFS native startup frame construct enter sub_82389040 "
        "lr=0x{:08X} frame_object=0x{:08X} render_resource=0x{:08X} "
        "frame_source=0x{:08X} source_words={}",
        original_ctx.lr, original_ctx.r3.u32, original_ctx.r4.u32,
        original_ctx.r5.u32,
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r5.u32, 4));
  }
  if (trace) {
    REXLOG_INFO(
        "VFS trace startup-video frame object construct enter "
        "sub_82389040 lr=0x{:08X} args=[{}] frame_object=0x{:08X} "
        "render_resource=0x{:08X} frame_source=0x{:08X} "
        "frame_source_words={} post_video=[{}] main_menu=[{}]",
        original_ctx.lr,
        refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
        original_ctx.r3.u32, original_ctx.r4.u32, original_ctx.r5.u32,
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r5.u32, 8),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  const uint32_t renderer_critical_section =
      FableRendererCriticalSectionForCurrentThread(base);
  const uint32_t render_frame_lock_release_count =
      native_startup_frame_construct
          ? ReleaseFableCriticalSectionForStartupVideoWait(
                ctx, base, renderer_critical_section)
          : 0u;
  __imp__sub_82389040(ctx, base);
  if (render_frame_lock_release_count != 0) {
    ReacquireFableCriticalSectionForStartupVideoWait(
        original_ctx, base, renderer_critical_section,
        render_frame_lock_release_count);
  }

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_82389040", original_ctx.lr,
                                                ctx.r3.u32, elapsed_ms);
  }

  if (trace || (native_startup_audit && !trace)) {
    refii::vfs::RecordRefiiPostVideoUiEvent(
        "StartupVideoFrame_82389040", original_ctx.lr, ctx.r3.u32,
        original_ctx.r3.u32, original_ctx.r4.u32, original_ctx.r5.u32);
  }
  if (native_startup_audit && !trace) {
    REXLOG_INFO(
        "Refii VFS native startup frame construct exit sub_82389040 "
        "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
        "frame_object_after={}",
        original_ctx.lr, ctx.r3.u32, elapsed_ms,
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r3.u32, 8));
  }
  if (native_startup_frame_construct &&
      (elapsed_ms >= 50 || render_frame_lock_release_count != 0)) {
    static std::atomic<uint32_t> startup_construct_logs{0};
    const uint32_t count =
        startup_construct_logs.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (count <= 16u || (count % 120u) == 0u) {
      REXLOG_INFO(
          "Refii VFS startup-video native frame construct handoff #{} "
          "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
          "released_render_frame_lock={} frame_object=0x{:08X} "
          "render_resource=0x{:08X} frame_source=0x{:08X} "
          "finished={} failed={} bink=0x{:08X} bink_frame={}/{}",
          count, original_ctx.lr, ctx.r3.u32, elapsed_ms,
          render_frame_lock_release_count, original_ctx.r3.u32,
          original_ctx.r4.u32, original_ctx.r5.u32,
          refii::vfs::LoadGuestU8(base, original_ctx.r3.u32 + 233u),
          refii::vfs::LoadGuestU8(base, original_ctx.r3.u32 + 244u),
          refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 76u),
          refii::vfs::LoadGuestU32(
              base,
              refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 76u) +
                  12u),
          refii::vfs::LoadGuestU32(
              base,
              refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 76u) +
                  8u));
    }
  }
  if (trace) {
    REXLOG_INFO(
        "VFS trace startup-video frame object construct exit "
        "sub_82389040 lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
        "frame_object_after={} post_video=[{}] main_menu=[{}]",
        original_ctx.lr, ctx.r3.u32, elapsed_ms,
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r3.u32, 16),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (elapsed_ms >= 250 && refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoStageSlow_sub_82389040", 8)) {
    REXLOG_INFO("Refii VFS post-video stage slow "
                "stage=sub_82389040 lr=0x{:08X} result=0x{:08X} "
                "elapsed_ms={} stage_timing=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr, ctx.r3.u32, elapsed_ms,
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
  const bool trace = refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
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

  __imp__rex_lhRenderer_BeginScene(ctx, base);
  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  refii::vfs::RecordRefiiPostVideoUiEvent(
      "Renderer_BeginScene", original_ctx.lr, ctx.r3.u32, original_ctx.r3.u32,
      original_ctx.r4.u32, original_ctx.r5.u32);
  if (trace || (refii::vfs::kTraceNonVfsBootFrameProbes &&
                refii::vfs::VfsTracingEnabled())) {
    REXLOG_INFO("VFS trace Boot phase exit Renderer_BeginScene lr=0x{:08X} "
                "result=0x{:08X} elapsed_ms={} frame_chain=[{}] "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr, ctx.r3.u32, elapsed_ms,
                refii::vfs::FormatCurrentPostVideoFrameChainState(base),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (elapsed_ms >= 250 && refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "RendererBeginSceneSlowBoundary", 8)) {
    REXLOG_INFO("Refii VFS renderer begin-scene slow boundary "
                "lr=0x{:08X} elapsed_ms={} frame_chain=[{}] "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr, elapsed_ms,
                refii::vfs::FormatCurrentPostVideoFrameChainState(base),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    refii::vfs::LogRefiiVfsChainVerdict(
        "frontend_post_video", static_cast<uint32_t>(original_ctx.lr),
        "startup_bik_pair",
        "art/videos/microsoft_logo.bik|art/videos/lionhead_logo.bik",
        "art/videos/microsoft_logo.bik|art/videos/lionhead_logo.bik",
        "renderer_frame", "Renderer_BeginScene_slow", "TraceOnce",
        "renderer_or_shader_frame_time_before_texture_consumer_open");
  }
  if (refii::vfs::kTraceNonVfsBootFrameProbes &&
      refii::vfs::VfsTracingEnabled()) {
    static std::chrono::steady_clock::time_point last_idle_log;
    const auto now = std::chrono::steady_clock::now();
    if (last_idle_log.time_since_epoch().count() == 0 ||
        now - last_idle_log >= std::chrono::seconds(5)) {
      last_idle_log = now;
      const auto &stats = refii::vfs::LooseStats();
      REXLOG_INFO(
          "Refii VFS boot idle context lr=0x{:08X} last_op=[{}] "
          "main_menu=[{}] post_video=[{}] streams={} records={} "
          "reads={} banks={} handles={}/{}",
          original_ctx.lr, refii::vfs::FormatRefiiLastVfsOperationForLog(),
          refii::vfs::FormatRefiiMainMenuBlockerTraceForLog(),
          refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
          stats.streams_opened.load(std::memory_order_relaxed),
          stats.active_stream_records.load(std::memory_order_relaxed),
          stats.active_read_requests.load(std::memory_order_relaxed),
          stats.active_synthetic_banks.load(std::memory_order_relaxed),
          stats.active_private_file_handles.load(std::memory_order_relaxed),
          stats.active_private_find_handles.load(std::memory_order_relaxed));
      refii::vfs::MaybeLogRefiiPostVideoHashOnlyBlocker(original_ctx.lr,
                                                        "BootIdle", base);
    }
  }
}

REX_EXTERN(rex_lhMeshResource_Deserialize) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhMeshResource_Deserialize);


  const PPCContext original_ctx = ctx;
  const uint32_t descriptor_guest = original_ctx.r5.u32;
  const uint32_t resource_hash =
      descriptor_guest != 0 ? refii::vfs::LoadGuestU32(base, descriptor_guest)
                            : 0;
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
                original_ctx.lr, original_ctx.r3.u32, original_ctx.r4.u32,
                descriptor_guest, resource_hash,
                refii::vfs::FormatGuestWords(base, descriptor_guest, 12),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    if (refii::vfs::IsBootFontResourceHash(resource_hash)) {
      refii::vfs::RecordRefiiMainMenuBlockerEvent(
          "font_deserialize",
          fmt::format("enter lr=0x{:08X} hash=0x{:08X} out=0x{:08X} "
                      "descriptor=0x{:08X}",
                      original_ctx.lr, resource_hash, original_ctx.r3.u32,
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
                original_ctx.lr, ctx.r3.u32, mesh_guest,
                refii::vfs::FormatGuestWords(base, mesh_guest, 20),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    if (refii::vfs::IsBootFontResourceHash(resource_hash)) {
      refii::vfs::RecordRefiiMainMenuBlockerEvent(
          "font_deserialize",
          fmt::format("exit lr=0x{:08X} hash=0x{:08X} result=0x{:08X} "
                      "mesh=0x{:08X} mesh_words={}",
                      original_ctx.lr, resource_hash, ctx.r3.u32, mesh_guest,
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
      descriptor_guest != 0 ? refii::vfs::LoadGuestU32(base, descriptor_guest)
                            : 0;
  const bool trace_load = refii::vfs::VfsTracingEnabled() &&
                          refii::vfs::VfsVerboseIoTracingEnabled() &&
                          (refii::vfs::kTraceMeshModelAudioDiagnostics ||
                           refii::vfs::IsBootFontResourceHash(resource_hash) ||
                           (refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
                            refii::vfs::ShouldLogRepeatedVfsTraceOperation(
                                "MeshResource_Load_PostVideo", 16)));
  if (trace_load) {
    REXLOG_INFO(
        "VFS trace MeshResource_Load enter lr=0x{:08X} "
        "out=0x{:08X} container=0x{:08X} descriptor=0x{:08X} "
        "resource_hash=0x{:08X} out_before={} container_words={} "
        "descriptor_words={} handoff=[{}] main_menu=[{}]",
        original_ctx.lr, original_ctx.r3.u32, original_ctx.r4.u32,
        descriptor_guest, resource_hash,
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r3.u32, 4),
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r4.u32, 8),
        refii::vfs::FormatGuestWordsIfPointer(base, descriptor_guest, 12),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__rex_lhMeshResource_Load(ctx, base);

  if (trace_load) {
    const uint32_t loaded_guest =
        original_ctx.r3.u32 != 0
            ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32)
            : 0;
    REXLOG_INFO(
        "VFS trace MeshResource_Load exit lr=0x{:08X} "
        "result=0x{:08X} out=0x{:08X} loaded=0x{:08X} "
        "out_after={} loaded_words={} handoff=[{}] main_menu=[{}]",
        original_ctx.lr, ctx.r3.u32, original_ctx.r3.u32, loaded_guest,
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r3.u32, 4),
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
    REXLOG_INFO(
        "VFS trace PostVideoFontSelect enter lr=0x{:08X} "
        "out=0x{:08X} selector=0x{:08X} out_before={} "
        "selector_words={} handoff=[{}] main_menu=[{}]",
        original_ctx.lr, original_ctx.r3.u32, original_ctx.r4.u32,
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r3.u32, 4),
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r4.u32, 8),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_8286FDB0(ctx, base);

  if (trace) {
    const uint32_t loaded_guest =
        original_ctx.r3.u32 != 0
            ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32)
            : 0;
    REXLOG_INFO(
        "VFS trace PostVideoFontSelect exit lr=0x{:08X} "
        "result=0x{:08X} out=0x{:08X} loaded=0x{:08X} "
        "out_after={} loaded_words={} handoff=[{}] main_menu=[{}]",
        original_ctx.lr, ctx.r3.u32, original_ctx.r3.u32, loaded_guest,
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r3.u32, 4),
        refii::vfs::FormatGuestWordsIfPointer(base, loaded_guest, 16),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "font_select",
        fmt::format("exit lr=0x{:08X} result=0x{:08X} loaded=0x{:08X} "
                    "out_after={}",
                    original_ctx.lr, ctx.r3.u32, loaded_guest,
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
    REXLOG_INFO(
        "VFS trace PostVideoFontAssign enter lr=0x{:08X} "
        "target=0x{:08X} source=0x{:08X} target_before={} "
        "source_words={} handoff=[{}] main_menu=[{}]",
        original_ctx.lr, original_ctx.r3.u32, original_ctx.r4.u32,
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r3.u32, 6),
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r4.u32, 6),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_8218DFB8(ctx, base);

  if (original_ctx.lr == 0x828716B4) {
    refii::vfs::MaybeSignalRefiiPostGameFacePackageHandoff(
        original_ctx, base, "post_video_font:assign", original_ctx.r3.u32);
  }

  if (trace) {
    REXLOG_INFO(
        "VFS trace PostVideoFontAssign exit lr=0x{:08X} "
        "result=0x{:08X} target=0x{:08X} source=0x{:08X} "
        "target_after={} source_words={} handoff=[{}] main_menu=[{}]",
        original_ctx.lr, ctx.r3.u32, original_ctx.r3.u32, original_ctx.r4.u32,
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r3.u32, 6),
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r4.u32, 6),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "font_assign",
        fmt::format("exit lr=0x{:08X} result=0x{:08X} target=0x{:08X} "
                    "target_after={}",
                    original_ctx.lr, ctx.r3.u32, original_ctx.r3.u32,
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
    REXLOG_INFO(
        "VFS trace PostVideoFontTempRelease enter lr=0x{:08X} "
        "object=0x{:08X} object_words={} handoff=[{}] main_menu=[{}]",
        original_ctx.lr, original_ctx.r3.u32,
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r3.u32, 6),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82231CB8(ctx, base);

  if (original_ctx.lr == 0x828716BC) {
    refii::vfs::MaybeSignalRefiiPostGameFacePackageHandoff(
        original_ctx, base, "post_video_font:temp_release",
        original_ctx.r3.u32);
  }

  if (trace) {
    REXLOG_INFO(
        "VFS trace PostVideoFontTempRelease exit lr=0x{:08X} "
        "result=0x{:08X} object=0x{:08X} object_after={} "
        "handoff=[{}] main_menu=[{}]",
        original_ctx.lr, ctx.r3.u32, original_ctx.r3.u32,
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r3.u32, 6),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "font_temp_release",
        fmt::format("exit lr=0x{:08X} result=0x{:08X} object=0x{:08X} "
                    "object_after={}",
                    original_ctx.lr, ctx.r3.u32, original_ctx.r3.u32,
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
      refii::vfs::ShouldLogRepeatedVfsTraceOperation("MeshResource_Validate",
                                                     8);
  if (trace) {
    const uint32_t container_guest = original_ctx.r4.u32;
    const uint32_t key_guest = original_ctx.r5.u32;
    REXLOG_INFO("VFS trace MeshResource_Validate enter lr=0x{:08X} "
                "out=0x{:08X} container=0x{:08X} key=0x{:08X} "
                "key_ascii={} key_words={} container_words={}",
                original_ctx.lr, original_ctx.r3.u32, container_guest,
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
                original_ctx.lr, out_guest, result_container, result_node,
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
    REXLOG_INFO(
        "VFS trace MeshResource lookup enter lr=0x{:08X} "
        "resource=0x{:08X} key=0x{:08X} key_ascii={} "
        "key_words={} resource_words={}",
        original_ctx.lr, original_ctx.r3.u32, original_ctx.r4.u32,
        refii::vfs::FormatGuestAsciiBytes(base, original_ctx.r4.u32, 32),
        refii::vfs::FormatGuestWords(base, original_ctx.r4.u32, 4),
        refii::vfs::FormatGuestWords(base, original_ctx.r3.u32, 20));
  }

  __imp__sub_82B70868(ctx, base);

  if (refii::vfs::kTraceMeshModelAudioDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace MeshResource lookup exit lr=0x{:08X} "
                "resource=0x{:08X} key=0x{:08X} result=0x{:08X}",
                original_ctx.lr, original_ctx.r3.u32, original_ctx.r4.u32,
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
                original_ctx.lr, original_ctx.r3.u32);
  }

  __imp__rex_lhRenderer_StartThread(ctx, base);

  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::VfsVerboseIoTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase exit Renderer_StartThread lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr, ctx.r3.u32);
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
                original_ctx.lr, original_ctx.r3.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  if (refii::vfs::SkipStartupVideosEnabled()) {
    ctx.r3.u32 = 1;
    refii::vfs::RecordRefiiPostVideoUiEvent(
        "SetupUISkipStartupVideos", original_ctx.lr, ctx.r3.u32,
        original_ctx.r3.u32, original_ctx.r4.u32, original_ctx.r5.u32);
    if ((refii::vfs::kTraceFrontendHandoffDiagnostics &&
         refii::vfs::VfsTracingEnabled() &&
         refii::vfs::VfsVerboseIoTracingEnabled()) ||
        refii::vfs::LooseAssetOverrideTracingEnabled()) {
      REXLOG_INFO("VFS trace Boot phase skip SetupUI startup videos "
                  "lr=0x{:08X} game=0x{:08X} handoff=[{}]",
                  original_ctx.lr, original_ctx.r3.u32,
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog());
    }
    return;
  }

  __imp__rex_lhGameInit_SetupUI(ctx, base);
  refii::vfs::RecordRefiiPostVideoUiEvent(
      "SetupUI", original_ctx.lr, ctx.r3.u32, original_ctx.r3.u32,
      original_ctx.r4.u32, original_ctx.r5.u32);

  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::VfsVerboseIoTracingEnabled()) {
    REXLOG_INFO("VFS trace Boot phase exit SetupUI lr=0x{:08X} result=0x{:08X} "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr, ctx.r3.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

REX_EXTERN(rex_lhGame_UpdateThread) {
  REX_FUNC_PROLOGUE();

  // Brackets the "Permanent Bank Mounter" thread body (`0x822F3898`, spawned by `sub_822F3660`).
  // It ends by setting the byte `sub_822F3B20` returns, which `sub_82377A00` polls at
  // `0x82377D74` with `Sleep(100)`; enter with no exit means no front end.
  const PPCContext original_ctx = ctx;
  REXLOG_INFO("Refii boot: bank mounter ENTER lr=0x{:08X} ctx=0x{:08X}",
              original_ctx.lr, original_ctx.r3.u32);

  __imp__rex_lhGame_UpdateThread(ctx, base);

  REXLOG_INFO("Refii boot: bank mounter EXIT lr=0x{:08X} result=0x{:08X}",
              original_ctx.lr, ctx.r3.u32);
}

REX_EXTERN(sub_826D6108) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_826D6108);


  // GUI resource-slot container dtor: pushes every registered resource id
  // onto the manager's deferred-release list (one 12-byte guest alloc per id
  // under the contended LH heap lock), then tears down the 24-byte slot
  // array. This is the multi-minute boot "grind" while the loading screen
  // waits for gui.gdb groups; log the workload so the inflation source is
  // measurable.
  const uint32_t container = ctx.r3.u32;
  uint32_t id_count = 0;
  uint32_t slot_count = 0;
  if (refii::vfs::LooksLikeGuestPointer(container)) {
    const uint32_t ids_begin =
        refii::vfs::LoadGuestU32(base, container + 84);   // a1[21]
    const uint32_t ids_end =
        refii::vfs::LoadGuestU32(base, container + 88);   // a1[22]
    if (ids_end >= ids_begin) {
      id_count = (ids_end - ids_begin) / 4;
    }
    const uint32_t slots_begin =
        refii::vfs::LoadGuestU32(base, container + 56);   // a1[14]
    const uint32_t slots_end =
        refii::vfs::LoadGuestU32(base, container + 60);   // a1[15]
    if (slots_end >= slots_begin) {
      slot_count = (slots_end - slots_begin) / 24;
    }
  }
  const auto begin = std::chrono::steady_clock::now();
  const uint32_t small_pool =
      refii::vfs::LoadGuestU32(base, 0x83496DA8u);
  const uint32_t small_pool_lock =
      refii::vfs::LooksLikeGuestPointer(small_pool)
          ? refii::vfs::LoadGuestU32(base, small_pool + 36u)
          : 0u;
  const uint32_t resource_manager =
      refii::vfs::LoadGuestU32(base, 0x83496B08u);
  uint32_t shutdown_ids_retired = 0;
  if (resource_manager == 0 && refii::vfs::LooksLikeGuestPointer(container)) {
    // sub_823716D0 clears the singleton and destroys its deferred-release list
    // before invoking the base GUI slot-container destructor. At that shutdown
    // boundary sub_826D6108 must not enqueue IDs into the already-destroyed
    // manager (the generated code otherwise repeatedly reads guest address
    // 0x94). Preserve the native structural destructor, but mark resources as
    // retired because their owning manager has already been torn down.
    const uint32_t ids_begin =
        refii::vfs::LoadGuestU32(base, container + 84u);
    const uint32_t ids_end =
        refii::vfs::LoadGuestU32(base, container + 88u);
    if (refii::vfs::LooksLikeGuestPointer(ids_begin) && ids_end >= ids_begin &&
        ids_end - ids_begin <= 0x10000u) {
      for (uint32_t id_guest = ids_begin; id_guest < ids_end;
           id_guest += sizeof(uint32_t)) {
        if (refii::vfs::LoadGuestU32(base, id_guest) != 0xFFFFFFFFu) {
          refii::vfs::StoreGuestU32(base, id_guest, 0xFFFFFFFFu);
          ++shutdown_ids_retired;
        }
      }
    }
    for (uint32_t offset : {96u, 100u, 104u}) {
      if (refii::vfs::LoadGuestU32(base, container + offset) != 0xFFFFFFFFu) {
        refii::vfs::StoreGuestU32(base, container + offset, 0xFFFFFFFFu);
        ++shutdown_ids_retired;
      }
    }
  }
  REXLOG_INFO("Refii VFS GUI slot-container dtor enter container=0x{:08X} "
              "release_ids={} slots={} lr=0x{:08X} "
              "manager=0x{:08X} shutdown_ids_retired={} "
              "small_pool=0x{:08X} small_pool_lock=0x{:08X}",
              container, id_count, slot_count,
              static_cast<uint32_t>(ctx.lr), resource_manager,
              shutdown_ids_retired, small_pool, small_pool_lock);
  __imp__sub_826D6108(ctx, base);
  const int64_t ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                         std::chrono::steady_clock::now() - begin)
                         .count();
  REXLOG_INFO("Refii VFS GUI slot-container dtor exit container=0x{:08X} "
              "release_ids={} slots={} elapsed_ms={}",
              container, id_count, slot_count, ms);
}

REX_EXTERN(sub_82BBA8D0) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82BBA8D0);


  // Resource release helper: notifies the destroy callback, releases the
  // resource's child object at +32, returns it to the pool. The Xbox 360
  // tolerates a null resource here (guest low memory reads back zeros, so
  // every step no-ops); on the host the +32 load hits the unmapped null page
  // and the pass-through SIGSEGV handler re-executes it forever -- a silent
  // boot freeze when a never-loaded resource slot is destroyed (deterministic
  // at the 16th synthetic bank while the permanent-bank mounter replaces the
  // previous GUI state object). Match console semantics instead.
  if (ctx.r4.u32 == 0) {
    if (refii::vfs::VfsTracingEnabled() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "ResourceReleaseNullGuard_82BBA8D0", 8)) {
      REXLOG_INFO("VFS trace resource release null-guard sub_82BBA8D0 "
                  "lr=0x{:08X} manager=0x{:08X} (console zero-page semantics)",
                  static_cast<uint32_t>(ctx.lr), ctx.r3.u32);
    }
    return;
  }
  __imp__sub_82BBA8D0(ctx, base);
}

REX_EXTERN(rex_lhGameInit_CheckUpdateMode) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhGameInit_CheckUpdateMode);


  __imp__rex_lhGameInit_CheckUpdateMode(ctx, base);

  // 0x822F26D0 is the loose-files volume probe: GetVolumeInformationA("GAME:\")
  // with maxComponentLength > 42 meaning "host filesystem, not a disc". TRUE
  // routes GameInit into the game's own loose branch (CWin32FileSystem over
  // L"data" + Bank_OpenWithManifest("dir.manifest")). Log the verdict once on
  // every backend so runs record which boot path the game actually took.
  {
    static std::once_flag probe_log_once;
    std::call_once(probe_log_once, [&] {
      REXLOG_INFO("GameInit loose-mode volume probe result={} force_retail={} "
                  "(probe TRUE + force off => native loose dir.manifest boot)",
                  ctx.r3.u32,
                  refii::vfs::ForceRetailUpdateModeEnabled());
    });
  }

  // The probe reports TRUE under refii (HostPathDevice max component 255), and
  // an earlier session observed the loose branch leaving per-resource
  // completion markers unset with the refii synthetic-bank facade active, so
  // the refii backend forces retail mode to keep bootstrap on the
  // packed-container path the facade emulates. (Whether the loose branch works
  // without the facade is an open experiment -- see the probe log above.)
  if (refii::vfs::ForceRetailUpdateModeEnabled() && ctx.r3.u32 != 0) {
    if (refii::vfs::VfsTracingEnabled()) {
      REXLOG_INFO("Refii VFS forcing retail update mode: "
                  "CheckUpdateMode 0x{:08X} -> 0",
                  ctx.r3.u32);
    }
    ctx.r3.u32 = 0;
  }
}

REX_EXTERN(rex_lhGameData_Init) {
  REX_FUNC_PROLOGUE();

  // Brackets the mounter's first stage, `0x822F3FF8` (vfsconfig / permanent resources) — the only
  // stage reaching an unbounded wait (`WaitForSingleObjectEx(evt+0x80, INFINITE)`).
  const PPCContext original_ctx = ctx;
  REXLOG_INFO("Refii boot: mounter stage 1 (vfsconfig/permanent resources) ENTER "
              "lr=0x{:08X} game=0x{:08X}",
              original_ctx.lr, original_ctx.r3.u32);

  __imp__rex_lhGameData_Init(ctx, base);

  REXLOG_INFO("Refii boot: mounter stage 1 EXIT lr=0x{:08X} result=0x{:08X}",
              original_ctx.lr, ctx.r3.u32);
}

REX_EXTERN(sub_822F3D90) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_822F3D90);


  const PPCContext original_ctx = ctx;
  const bool trace_native_iterator =
      refii::vfs::VfsTracingEnabled() &&
      g_dlc_startup_content_manager != 0;
  if (trace_native_iterator) {
    const uint32_t object_vtable =
        refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
            ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32)
            : 0;
    REXLOG_INFO("Refii VFS native DLCStartup callback enter "
                "lr=0x{:08X} object=0x{:08X} vtable=0x{:08X} "
                "r28=0x{:08X} r29=0x{:08X} r30=0x{:08X} r31=0x{:08X} {} {}",
                original_ctx.lr, original_ctx.r3.u32, object_vtable,
                original_ctx.r28.u32, original_ctx.r29.u32,
                original_ctx.r30.u32, original_ctx.r31.u32,
                FormatContentManagerIteratorState(
                    base, g_dlc_startup_content_manager),
                FormatContentIteratorEntries(base,
                                             g_dlc_startup_content_manager));
  }
  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace DLCStartup runner enter lr=0x{:08X} "
                "request_manager=0x{:08X}",
                original_ctx.lr, original_ctx.r3.u32);
  }

  __imp__sub_822F3D90(ctx, base);

  // sub_8237FE30 keeps its output slot, callback argument, callback, vector
  // cursor, iterator metadata, and current object in PPC nonvolatile r26-r31
  // across the indirect DLCStartup call.
  // The generated callback body restores those registers internally, but the
  // extern dispatch boundary publishes its scratch register file back to the
  // caller. Preserve the PPC ABI here. Without this, the native iterator was
  // observed after the final startup movie with r30=0x00020208 and
  // r31=0x00800007, then attempted to call release through a null vtable.
  // r3 deliberately remains the callback result.
  ctx.r26 = original_ctx.r26;
  ctx.r27 = original_ctx.r27;
  ctx.r28 = original_ctx.r28;
  ctx.r29 = original_ctx.r29;
  ctx.r30 = original_ctx.r30;
  ctx.r31 = original_ctx.r31;

  if (trace_native_iterator) {
    const uint32_t object_vtable =
        refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
            ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32)
            : 0;
    REXLOG_INFO("Refii VFS native DLCStartup callback exit "
                "result=0x{:08X} object=0x{:08X} vtable=0x{:08X} {} {}",
                ctx.r3.u32, original_ctx.r3.u32, object_vtable,
                FormatContentManagerIteratorState(
                    base, g_dlc_startup_content_manager),
                FormatContentIteratorEntries(base,
                                             g_dlc_startup_content_manager));
  }

  if (refii::vfs::kTraceFrontendHandoffDiagnostics &&
      refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("VFS trace DLCStartup runner exit lr=0x{:08X} result=0x{:08X}",
                original_ctx.lr, ctx.r3.u32);
  }
}

REX_EXTERN(sub_82374980) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82374980);


  const PPCContext original_ctx = ctx;
  const bool trace = ShouldTraceGameDataBootBoundary(original_ctx.lr) ||
                     (refii::vfs::VfsTracingEnabled() &&
                      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
                          "GameDataRemapOpen_82374980", 64));
  if (trace) {
    REXLOG_INFO("VFS trace GameData remap open enter lr=0x{:08X} "
                "out=0x{:08X} manager=0x{:08X} provider=0x{:08X} "
                "name_obj=0x{:08X} name={}",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                original_ctx.r5.u32,
                original_ctx.r6.u32,
                refii::vfs::ReadMaybeGuestString(base, original_ctx.r6.u32));
  }

  __imp__sub_82374980(ctx, base);

  if (trace) {
    const uint32_t out_stream =
        refii::vfs::TryReadOutputPointer(base, original_ctx.r3.u32);
    REXLOG_INFO("VFS trace GameData remap open exit lr=0x{:08X} "
                "result=0x{:08X} stream=0x{:08X} name={}",
                original_ctx.lr,
                ctx.r3.u32,
                out_stream,
                refii::vfs::ReadMaybeGuestString(base, original_ctx.r6.u32));
  }
}

REX_EXTERN(sub_8237FE30) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_8237FE30);


  const PPCContext original_ctx = ctx;
  const bool trace_native_dlc_iterator =
      refii::vfs::VfsTracingEnabled() &&
      original_ctx.lr == 0x822F4094 && original_ctx.r5.u32 == 0x822F3D90 &&
      g_native_dlc_iterator_trace_count.fetch_add(1,
                                                  std::memory_order_relaxed) <
          4;
  const bool trace = ShouldTraceGameDataBootBoundary(original_ctx.lr) ||
                     trace_native_dlc_iterator;
  if (trace) {
    REXLOG_INFO("VFS trace GameData content find enter lr=0x{:08X} "
                "{} callback=0x{:08X}/{} arg=0x{:08X} arg_name={} {}",
                original_ctx.lr, FormatGuestOutSlot(base, original_ctx.r3.u32),
                original_ctx.r5.u32,
                ContentIteratorCallbackName(original_ctx.r5.u32),
                original_ctx.r6.u32,
                refii::vfs::ReadMaybeGuestString(base, original_ctx.r6.u32),
                FormatContentManagerIteratorState(base, original_ctx.r4.u32));
  }

  if (TryRunRefiiDlcStartupContentFind(ctx, base, original_ctx)) {
    return;
  }

  const uint32_t previous_dlc_manager = g_dlc_startup_content_manager;
  if (trace_native_dlc_iterator) {
    g_dlc_startup_content_manager = original_ctx.r4.u32;
    REXLOG_INFO("Refii VFS native DLCStartup iterator snapshot {} {}",
                FormatContentManagerIteratorState(base, original_ctx.r4.u32),
                FormatContentIteratorEntries(base, original_ctx.r4.u32));
  }
  __imp__sub_8237FE30(ctx, base);
  g_dlc_startup_content_manager = previous_dlc_manager;

  // The generated iterator restores its own r26-r31 save area, but the extern
  // dispatch boundary can still publish the iterator's final scratch register
  // file to GameData_Init. Preserve the caller-visible PPC nonvolatile state;
  // GameData_Init keeps its content manager in r31 and immediately reuses it
  // for sub_8237FD60. A leaked iterator cursor was observed here as r31=0x800.
  ctx.r26 = original_ctx.r26;
  ctx.r27 = original_ctx.r27;
  ctx.r28 = original_ctx.r28;
  ctx.r29 = original_ctx.r29;
  ctx.r30 = original_ctx.r30;
  ctx.r31 = original_ctx.r31;

  if (trace) {
    REXLOG_INFO("VFS trace GameData content find exit lr=0x{:08X} "
                "result=0x{:08X} {}",
                original_ctx.lr, ctx.r3.u32,
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
                original_ctx.lr, original_ctx.r4.u32,
                FormatContentManagerIteratorState(base, original_ctx.r3.u32));
  }

  __imp__sub_8237FD60(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace GameData content for-each exit lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr, ctx.r3.u32);
  }
}

REX_EXTERN(rex_lhRenderer_BeginGPUFrame) {
  REX_FUNC_PROLOGUE();

#if !REFII_VFS_HAS_RENDERER_RUNTIME
  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhRenderer_BeginGPUFrame);
#endif

  const PPCContext original_ctx = ctx;
  const bool trace = ShouldTraceGameDataBootBoundary(original_ctx.lr);
  if (trace) {
    REXLOG_INFO("VFS trace GameData GPU frame begin enter lr=0x{:08X}",
                original_ctx.lr);
  }

#if REFII_VFS_HAS_RENDERER_RUNTIME
  static const refii::renderer::FableHookSymbolDesc *renderer_hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "rex_lhRenderer_BeginGPUFrame",
          refii::renderer::RendererHookKind::kGeneral);
  const refii::renderer::RendererHookArgs renderer_args =
      refii::renderer::CaptureRendererHookArgs(ctx);
  refii::renderer::OnRendererGuestHookEnter(
      "rex_lhRenderer_BeginGPUFrame",
      refii::renderer::RendererHookKind::kGeneral, renderer_args, base,
      renderer_hook_desc);
  if (refii::renderer::ShouldRunGuestRendererHook(
          "rex_lhRenderer_BeginGPUFrame",
          refii::renderer::RendererHookKind::kGeneral, renderer_args, base,
          renderer_hook_desc)) {
#endif
    __imp__rex_lhRenderer_BeginGPUFrame(ctx, base);

    if (trace) {
      REXLOG_INFO("VFS trace GameData GPU frame begin exit lr=0x{:08X} "
                  "result=0x{:08X}",
                  original_ctx.lr, ctx.r3.u32);
    }
#if REFII_VFS_HAS_RENDERER_RUNTIME
    refii::renderer::OnRendererGuestHookExit(
        "rex_lhRenderer_BeginGPUFrame",
        refii::renderer::RendererHookKind::kGeneral, renderer_args, ctx, base,
        renderer_hook_desc);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "rex_lhRenderer_BeginGPUFrame",
        refii::renderer::RendererHookKind::kGeneral, renderer_args, ctx, base,
        renderer_hook_desc);
    if (trace) {
      REXLOG_INFO("VFS trace GameData GPU frame begin guest skipped for Plume "
                  "lr=0x{:08X} result=0x{:08X}",
                  original_ctx.lr, ctx.r3.u32);
    }
  }
#endif
}

REX_EXTERN(rex_lhRenderer_EndGPUFrame) {
  REX_FUNC_PROLOGUE();

#if !REFII_VFS_HAS_RENDERER_RUNTIME
  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhRenderer_EndGPUFrame);
#endif

  const PPCContext original_ctx = ctx;
  const bool trace = ShouldTraceGameDataBootBoundary(original_ctx.lr);
  if (trace) {
    REXLOG_INFO("VFS trace GameData GPU frame end enter lr=0x{:08X}",
                original_ctx.lr);
  }

#if REFII_VFS_HAS_RENDERER_RUNTIME
  static const refii::renderer::FableHookSymbolDesc *renderer_hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "rex_lhRenderer_EndGPUFrame",
          refii::renderer::RendererHookKind::kGeneral);
  const refii::renderer::RendererHookArgs renderer_args =
      refii::renderer::CaptureRendererHookArgs(ctx);
  refii::renderer::OnRendererGuestHookEnter(
      "rex_lhRenderer_EndGPUFrame", refii::renderer::RendererHookKind::kGeneral,
      renderer_args, base, renderer_hook_desc);
  if (refii::renderer::ShouldRunGuestRendererHook(
          "rex_lhRenderer_EndGPUFrame",
          refii::renderer::RendererHookKind::kGeneral, renderer_args, base,
          renderer_hook_desc)) {
#endif
    __imp__rex_lhRenderer_EndGPUFrame(ctx, base);

    if (trace) {
      REXLOG_INFO("VFS trace GameData GPU frame end exit lr=0x{:08X} "
                  "result=0x{:08X}",
                  original_ctx.lr, ctx.r3.u32);
    }
#if REFII_VFS_HAS_RENDERER_RUNTIME
    refii::renderer::OnRendererGuestHookExit(
        "rex_lhRenderer_EndGPUFrame",
        refii::renderer::RendererHookKind::kGeneral, renderer_args, ctx, base,
        renderer_hook_desc);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "rex_lhRenderer_EndGPUFrame",
        refii::renderer::RendererHookKind::kGeneral, renderer_args, ctx, base,
        renderer_hook_desc);
    if (trace) {
      REXLOG_INFO("VFS trace GameData GPU frame end guest skipped for Plume "
                  "lr=0x{:08X} result=0x{:08X}",
                  original_ctx.lr, ctx.r3.u32);
    }
  }
#endif
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
                original_ctx.lr, ctx.r3.u32);
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
                original_ctx.lr, original_ctx.r3.u32,
                refii::vfs::ReadMaybeGuestString(base, original_ctx.r4.u32),
                original_ctx.r5.u32);
  }

  __imp__sub_822F4948(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace GameData resource register exit lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr, ctx.r3.u32);
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
                original_ctx.lr, original_ctx.r3.u32);
  }

  __imp__sub_822F4210(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace GameData resource setup exit lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr, ctx.r3.u32);
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
                original_ctx.lr, original_ctx.r3.u32, original_ctx.r4.u32);
  }

  __imp__sub_822F4750(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace GameData resource late-bind exit lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr, ctx.r3.u32);
  }
}

REX_EXTERN(rex_lhFile_CloseAndRelease) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it would return before the Refii-owned check. The call to the
  // original at the end is the passthrough for the guest's own objects.


  const PPCContext original_ctx = ctx;
  const bool trace = ShouldTraceGameDataBootBoundary(original_ctx.lr);
  if (trace) {
    REXLOG_INFO("VFS trace GameData file close/release enter lr=0x{:08X} "
                "file=0x{:08X} words=0x{:08X} 0x{:08X} 0x{:08X} 0x{:08X}",
                original_ctx.lr, original_ctx.r3.u32,
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
    const uint32_t vtable = refii::vfs::LoadGuestU32(base, original_ctx.r3.u32);
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
                    original_ctx.lr, original_ctx.r3.u32, vtable, total_stream,
                    read_stream, has_refii_stream);
      }
      ctx.r3.u32 = original_ctx.r3.u32;
      return;
    }
  }

  __imp__rex_lhFile_CloseAndRelease(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace GameData file close/release exit lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr, ctx.r3.u32);
  }
}
