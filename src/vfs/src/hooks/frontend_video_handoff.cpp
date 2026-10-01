#include "vfs_hooks_private.h"

#include <atomic>
#include <chrono>
#include <bit>
#include <cstdlib>

#include "core/guest_yield_backoff.h"
#include "hooks/ppc_externs.h"
#include <rex/kernel/xboxkrnl/video.h>
#include <rex/system/xvideo.h>

namespace refii::renderer {
// No-renderer shims.
//
// This hook's own work — tracking the guest's startup-video pump and keeping
// the boot sequence moving — is renderer-independent; what it hands the
// renderer is notifications. With no renderer built there is nothing to notify,
// so these are the honest no-ops rather than ten scattered #ifs through 2,800
// lines. Sibling hooks guard per-call-site because they have renderer logic to
// omit; this file does not.
//
// The two predicates must answer FALSE, and that is a behavioural statement,
// not a convenience: line ~1839 asks whether Plume owns the startup Bink draw
// before declining to advance the pump itself. With no Plume, nobody else
// advances it, so the hook must take the path where it does.
inline void NotifyFableStartupVideoPumpInitComplete(uint32_t, uint32_t) {}
inline void NotifyFableStartupVideoPumpDispatchComplete(uint32_t, uint32_t) {}
inline void NotifyFableStartupVideoPumpRunComplete(uint32_t) {}

inline bool QueryRendererHookThreadTelemetry(uint32_t, const char** last_symbol,
                                             uint64_t* us_since_enter) {
  // Leave the caller's defaults alone and report "no telemetry" — the caller
  // logs these, and inventing values would put fiction in the log.
  (void)last_symbol;
  (void)us_since_enter;
  return false;
}
}  // namespace refii::renderer

namespace {
struct CompactStartupVideoProgress {
  uint32_t update_guest = 0;
  uint32_t frame = 0;
  uint32_t index = 0;
  uint32_t count = 0;
  uint8_t complete = 0;
  uint32_t result = 0;
};

// The startup audit's own rate limit.

bool ShouldLogNativeStartupBikAudit(std::string_view key, uint32_t limit) {
  if (refii::vfs::RefiiStartupVideosPlaybackCriticalActive()) {
    return false;
  }

  static std::mutex mutex;
  static std::unordered_map<std::string, uint32_t> counts;

  std::lock_guard<std::mutex> lock(mutex);
  const uint32_t next_count = ++counts[std::string(key)];
  return next_count <= limit;
}

bool NativeStartupUpdateHasActiveMovie(uint8_t* base, uint32_t update_guest) {
  if (base == nullptr || !refii::vfs::LooksLikeGuestPointer(update_guest)) {
    return false;
  }

  const uint32_t vector_begin = refii::vfs::LoadGuestU32(base, update_guest + 56);
  const uint32_t vector_end = refii::vfs::LoadGuestU32(base, update_guest + 60);
  const uint32_t video_index = refii::vfs::LoadGuestU32(base, update_guest + 84);
  const uint32_t count =
      vector_end >= vector_begin ? (vector_end - vector_begin) / 8 : 0;
  if (count != 0 && video_index < count) {
    return true;
  }

  constexpr uint32_t kStartupVideoGlobalRoot = 0x83496B0C;
  const uint32_t global_root =
      refii::vfs::LoadGuestU32(base, kStartupVideoGlobalRoot);
  const uint32_t game_state =
      global_root != 0 ? refii::vfs::LoadGuestU32(base, global_root + 8) : 0;
  const uint32_t owner =
      game_state != 0 ? refii::vfs::LoadGuestU32(base, game_state + 40) : 0;
  return owner != 0 && refii::vfs::LoadGuestU32(base, owner + 4) != 0;
}

void NotifyRendererStartupVideoPumpInitIfNeeded(const char* label,
                                                uint8_t* base,
                                                uint32_t wrapper_guest) {
  const std::string_view stage(label);
  const uint32_t update_guest =
      wrapper_guest != 0 ? refii::vfs::LoadGuestU32(base, wrapper_guest + 12)
                         : 0;
  if (stage == "StartupReadyRequest_Update_82378110") {
    refii::renderer::NotifyFableStartupVideoPumpInitComplete(wrapper_guest,
                                                             update_guest);
  } else if (stage == "StartupReadyRequest_Dispatch_82378118") {
    refii::renderer::NotifyFableStartupVideoPumpDispatchComplete(wrapper_guest,
                                                                 update_guest);
  }
}

std::string FormatNativeStartupMovieOwnerSlot(uint8_t* base) {
  constexpr uint32_t kStartupVideoGlobalRoot = 0x83496B0C;
  if (base == nullptr) {
    return "base=null";
  }

  const uint32_t global_root =
      refii::vfs::LoadGuestU32(base, kStartupVideoGlobalRoot);
  const uint32_t game_state =
      global_root != 0 ? refii::vfs::LoadGuestU32(base, global_root + 8) : 0;
  const uint32_t owner =
      game_state != 0 ? refii::vfs::LoadGuestU32(base, game_state + 40) : 0;
  const uint32_t movie_owner =
      owner != 0 ? refii::vfs::LoadGuestU32(base, owner + 4) : 0;
  const uint32_t duration_word =
      movie_owner != 0 ? refii::vfs::LoadGuestU32(base, movie_owner + 4) : 0;
  const uint32_t elapsed_word =
      movie_owner != 0 ? refii::vfs::LoadGuestU32(base, movie_owner + 8) : 0;
  const uint8_t complete_gate =
      movie_owner != 0 ? refii::vfs::LoadGuestU8(base, movie_owner + 233) : 0;
  const uint8_t ready_gate =
      movie_owner != 0 ? refii::vfs::LoadGuestU8(base, movie_owner + 244) : 0;

  return fmt::format(
      "global_root=0x{:08X} game_state=0x{:08X} owner=0x{:08X} "
      "movie_owner=0x{:08X} flags233/244={}/{} duration={} elapsed={} "
      "owner_words={} movie_words={}",
      global_root,
      game_state,
      owner,
      movie_owner,
      complete_gate,
      ready_gate,
      refii::vfs::RefiiGuestF32FromWord(duration_word),
      refii::vfs::RefiiGuestF32FromWord(elapsed_word),
      refii::vfs::FormatGuestWordsIfPointer(base, owner, 8),
      refii::vfs::FormatGuestWordsIfPointer(base, movie_owner, 16));
}

void MaybeLogCompactStartupVideoProgress(uint8_t* base,
                                         uint32_t update_guest,
                                         uint32_t link_register,
                                         uint32_t result) {
  if (!refii::vfs::kTraceFrontendHandoffDiagnostics ||
      !refii::vfs::VfsTracingEnabled() ||
      refii::vfs::VfsVerboseIoTracingEnabled() || base == nullptr ||
      !refii::vfs::LooksLikeGuestPointer(update_guest)) {
    return;
  }

  const uint32_t vector_begin = refii::vfs::LoadGuestU32(base, update_guest + 56);
  const uint32_t vector_end = refii::vfs::LoadGuestU32(base, update_guest + 60);
  const uint32_t count =
      vector_end >= vector_begin ? (vector_end - vector_begin) / 8 : 0;

  CompactStartupVideoProgress progress;
  progress.update_guest = update_guest;
  progress.frame = refii::vfs::LoadGuestU32(base, update_guest + 4);
  progress.index = refii::vfs::LoadGuestU32(base, update_guest + 84);
  progress.count = count;
  progress.complete = refii::vfs::LoadGuestU8(base, update_guest + 96);
  progress.result = result;

  static std::mutex mutex;
  static CompactStartupVideoProgress last;
  static bool have_last = false;

  std::lock_guard<std::mutex> lock(mutex);
  const bool changed =
      !have_last || last.update_guest != progress.update_guest ||
      last.index != progress.index || last.count != progress.count ||
      last.complete != progress.complete || last.result != progress.result;
  const bool heartbeat = progress.frame != 0 && (progress.frame % 180) == 0 &&
                         last.frame != progress.frame;
  if (!changed && !heartbeat) {
    return;
  }

  have_last = true;
  last = progress;
  REXLOG_INFO("Refii VFS startup video progress lr=0x{:08X} update=0x{:08X} "
              "result=0x{:08X} frame={} index={}/{} complete={} boot_ms={}",
              link_register,
              progress.update_guest,
              progress.result,
              progress.frame,
              progress.index,
              progress.count,
              progress.complete != 0 ? "true" : "false",
              refii::vfs::RefiiBootTraceAgeMs());
}
} // namespace

REX_EXTERN(sub_82377A00) {
  REX_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;

  // The native startup update — the guest's own boot-movie pump, run and returned. This is the
  // path that runs; falling through to the synthetic path below leaves the boot-movie thread
  // silent once the videos end, so `sub_82378618` never runs and no presentation is bound.
  const bool native_startup_audit =
      !NativeStartupUpdateHasActiveMovie(base, original_ctx.r3.u32) &&
      ShouldLogNativeStartupBikAudit("NativeStartupUpdate_82377A00", 24);
  if (native_startup_audit) {
    REXLOG_INFO("Refii VFS native startup update enter sub_82377A00 "
                "lr=0x{:08X} update_timing=[{}] owner_slot=[{}] args=[{}]",
                original_ctx.lr,
                refii::vfs::FormatStartupVideoUpdateTimingState(
                    base, original_ctx.r3.u32),
                FormatNativeStartupMovieOwnerSlot(base),
                refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx));
  }

  __imp__sub_82377A00(ctx, base);
  refii::renderer::NotifyFableStartupVideoPumpRunComplete(original_ctx.r3.u32);

  refii::vfs::RecordRefiiStartupUpdateSnapshot(base,
                                               original_ctx.r3.u32,
                                               original_ctx.lr,
                                               ctx.r3.u32);
  MaybeLogCompactStartupVideoProgress(base,
                                      original_ctx.r3.u32,
                                      original_ctx.lr,
                                      ctx.r3.u32);

  if (native_startup_audit) {
    REXLOG_INFO("Refii VFS native startup update exit sub_82377A00 "
                "lr=0x{:08X} result=0x{:08X} update_after=[{}] "
                "owner_slot_after=[{}] args_after=[{}]",
                original_ctx.lr,
                ctx.r3.u32,
                refii::vfs::FormatStartupVideoUpdateTimingState(
                    base, original_ctx.r3.u32),
                FormatNativeStartupMovieOwnerSlot(base),
                refii::vfs::FormatPostVideoBoundaryArgs(base, ctx));
  }
}

#define REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(function_name, imp_name,     \
                                                label, log_after_state)       \
  REX_EXTERN(function_name) {                                              \
    REX_FUNC_PROLOGUE();                                                      \
                                                                               \
    const PPCContext original_ctx = ctx;                                       \
    const uint32_t request_guest = original_ctx.r3.u32;                        \
    const bool tracked_request =                                               \
        refii::vfs::IsNativeVfsScheduledRequest(base, request_guest);      \
    const bool trace =                                                         \
        refii::vfs::VfsTracingEnabled() &&                                 \
        tracked_request &&                                                     \
        refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&               \
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(                    \
            label, 96);                                                        \
    std::string before_state;                                                  \
    if (trace) {                                                               \
      before_state = refii::vfs::FormatNativeVfsAsyncRequestState(         \
          base, request_guest);                                                \
      REXLOG_INFO("VFS trace native scheduled request enter " label            \
                  " lr=0x{:08X} ctr=0x{:08X} r4=0x{:08X} state=[{}] "         \
                  "handoff=[{}] main_menu=[{}]",                              \
                  original_ctx.lr,                                             \
                  original_ctx.ctr.u32,                                        \
                  original_ctx.r4.u32,                                         \
                  before_state,                                                \
                  refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),    \
                  refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());    \
    }                                                                          \
                                                                               \
    imp_name(ctx, base);                                                       \
    NotifyRendererStartupVideoPumpInitIfNeeded(label, base, request_guest);    \
                                                                               \
    if (trace) {                                                               \
      REXLOG_INFO("VFS trace native scheduled request exit " label             \
                  " lr=0x{:08X} result=0x{:08X} before=[{}] after=[{}]",      \
                  original_ctx.lr,                                             \
                  ctx.r3.u32,                                                  \
                  before_state,                                                \
                  log_after_state                                              \
                      ? refii::vfs::FormatNativeVfsAsyncRequestState(      \
                            base, request_guest)                               \
                      : std::string{"<skipped_after_destructor>"});           \
    }                                                                          \
  }

REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_82377E10,
                                         __imp__sub_82377E10,
                                         "StartupReadyRequest_Dtor_82377E10",
                                         false)
REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_82378020,
                                         __imp__sub_82378020,
                                         "StartupReadyRequest_Dtor2_82378020",
                                         false)
REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_82378110,
                                         __imp__sub_82378110,
                                         "StartupReadyRequest_Update_82378110",
                                         true)
REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_82378118,
                                         __imp__sub_82378118,
                                         "StartupReadyRequest_Dispatch_82378118",
                                         true)
REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_8231A748,
                                         __imp__sub_8231A748,
                                         "FrontendRenderRequest_Dtor_8231A748",
                                         false)
REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_8231A9A8,
                                         __imp__sub_8231A9A8,
                                         "FrontendRenderRequest_Dtor2_8231A9A8",
                                         false)
REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_8231AA98,
                                         __imp__sub_8231AA98,
                                         "FrontendRenderRequest_Ready_8231AA98",
                                         true)
REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_8231AAA0,
                                         __imp__sub_8231AAA0,
                                         "FrontendRenderRequest_Run_8231AAA0",
                                         true)
REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_8231AAA8,
                                         __imp__sub_8231AAA8,
                                         "FrontendRenderRequest_Tick_8231AAA8",
                                         true)
REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE(sub_8231AAD0,
                                         __imp__sub_8231AAD0,
                                         "FrontendRenderRequest_Commit_8231AAD0",
                                         true)

#undef REFII_NATIVE_VFS_SCHEDULED_REQUEST_TRACE

/// The command-stream drain, which headless spends its whole budget waiting for a GPU.
///
/// The startup movie's constructor `sub_82389040` asks the "3D Engine" thread for the D3D
/// device (`sub_823889A8`: `stb` 1 to `engine+192` at `0x823889CC`, then an infinite wait at
/// `0x823889F8`). Only `0x823882B4` releases it, reached once per engine-loop iteration with the
/// engine critical section held from `0x82388270` to `0x8238832C`; the requester needs that
/// lock, so it waits a whole frame. Headless, this drain runs its full budget (one presentation
/// period per slot, four slots a frame) inside that hold.
///
/// The guest's own early-out ends it: at `0x822DFE68` it returns immediately when `force == 0`
/// and `dbl_8335F7D8[slot] >= deadline` (`bge cr6, loc_822E1694` at `0x822DFE80`), so a deadline
/// of zero with force cleared takes that exit.
///
/// Only when the ring is empty: a queued packet is still drained, since the present gate's
/// fence is opcode 17 in this ring. `r4` is a slot index, not a pointer (`lwzx r31, r10, r9` at
/// `0x822DFE44` indexes `dword_8349F9B0`). Read cursor is slot+12, published write pointer
/// slot+60.
REX_EXTERN(sub_822DFE08) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();

  // Headless only: with a renderer attached the producer can publish while this spins.
  static const bool headless = [] {
    const char* value = std::getenv("REX_EMBED_HEADLESS");
    return value != nullptr && value[0] == '1';
  }();

  // Not gated on `force`: callers already pass force 0 but a future deadline, so the
  // early-out's deadline test fails. The deadline is the lever.
  if (headless) {
    const uint32_t slot_index = ctx.r4.u32;
    const uint32_t slot =
        slot_index < 4u
            ? refii::vfs::LoadGuestU32(base, 0x8349F9B0u + (slot_index * 4u))
            : 0u;
    const bool usable = refii::vfs::LooksLikeGuestPointer(slot);
    const uint32_t read_cursor = usable ? refii::vfs::LoadGuestU32(base, slot + 12u) : 0u;
    const uint32_t shared_wptr = usable ? refii::vfs::LoadGuestU32(base, slot + 60u) : 0u;

    // Log the inputs of the first few calls.
    static std::atomic<uint64_t> entered{0};
    const uint64_t seen = entered.fetch_add(1u, std::memory_order_relaxed) + 1u;
    if (seen <= 8u) {
      REXLOG_INFO(
          "Refii drain entry #{}: slot={} ring=0x{:08X} read=0x{:08X} wptr=0x{:08X} "
          "force={} deadline={:.6f}",
          static_cast<unsigned long long>(seen), slot_index, slot, read_cursor, shared_wptr,
          ctx.r5.u32, ctx.f1.f64);
    }

    if (usable && read_cursor == shared_wptr) {
      static std::atomic<uint64_t> skipped{0};
      const uint64_t n = skipped.fetch_add(1u, std::memory_order_relaxed) + 1u;
      if (n == 1u || (n % 20000u) == 0u) {
        REXLOG_INFO(
            "Refii drain: ring {} empty, taking the guest's own early-out rather than "
            "spinning a presentation period with the engine lock held (#{} so far)",
            slot_index, static_cast<unsigned long long>(n));
      }

      // Both, so the early-out at 0x822DFE80 is reached whichever way the caller set force.
      ctx.r5.u32 = 0u;
      ctx.f1.f64 = 0.0;
    }
  }

  REFII_VFS_NATIVE_PASSTHROUGH(sub_822DFE08);
}

REX_EXTERN(sub_82CC38E8) { // Lumen: SwitchToThread
  // Every guest thread yields here sooner or later; one thread_local test per call.
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();

  // Spend a long wait sleeping instead of yielding. Placed ahead of everything else in
  // this hook, including the native-passthrough early out, so the policy is not
  // conditional on the loose-asset backend.
  refii::vfs::MaybeBackOffBeforeGuestYield(ctx.lr);

  // PACING GLOBALS (IDA-proven, 2026-07-29). The drain spin's overdue test and its escape
  // budget both divide by flt_8336035C (XVIDEO_MODE.RefreshRate) and multiply by
  // dword_83360358 (PresentationInterval). Their ONLY writer is guest CreateDevice
  // (sub_82B6F6C0 @0x82b6f8f0/0x82b6f90c), which runs only when the front-buffer texture
  // creation in Render_InitPresentationAndFirstSwap succeeds — a region our port is
  // suspected to skip. RefreshRate==0.0 makes the overdue threshold +inf AND the budget
  // +inf simultaneously: the exact unbounded spin measured in runs 437/439 (13.9M-51.8M
  // yields). Diagnose from live guest memory and, if poisoned, write what the guest's own
  // code would have written (values from the same VdQueryVideoMode the guest copy reads).
  // Idempotent: if CreateDevice later runs, it rewrites the same values.
  //
  // TIME-GATED, NOT COUNTED. This used to ride the spin probe's one-in-100,000 branch,
  // and the back-off above cuts that rate by two orders of magnitude — a heal that first
  // arrives 50 s into a run is no heal at all.
  // Release the D3D device-ownership guard: nothing owns a device that does not exist.
  //
  // `Render_D3DDeviceOwnership_WaitAndReclaim` (0x82B6EDE0 — refii_config misnames it
  // `rex_lhRenderer_SetupFrame`) gates on `dword_83360350`:
  //
  //     if (dword_83360350 == -1 || dword_83360350 == KeGetCurrentThreadId()) { proceed }
  //     else { while (dword_83360350 != -1) SwitchToThread(); }
  //
  // `-1` is unowned (`sub_82A1AE18` writes it on teardown), but the image ships the word as 0
  // and no thread has id 0, so every non-owner spins from boot; guest `CreateDevice`, which
  // would initialise it, does not fully run headless — the same cause as the pacing globals
  // below at `0x83360358`/`0x8336035C`. Healed here because the spin's own yield is this hook.
  // Once only, and only from 0: a real owner id is left alone.
  {
    static std::atomic<bool> device_guard_healed{false};
    if (!device_guard_healed.load(std::memory_order_relaxed)) {
      constexpr uint32_t kGuestD3DOwnershipGuard = 0x83360350u;
      if (refii::vfs::LoadGuestU32(base, kGuestD3DOwnershipGuard) == 0u &&
          !device_guard_healed.exchange(true, std::memory_order_relaxed)) {
        refii::vfs::StoreGuestU32(base, kGuestD3DOwnershipGuard, 0xFFFFFFFFu);
        REXLOG_INFO("Refii D3D device-ownership guard RELEASED: dword_83360350 shipped as 0 "
                    "and guest CreateDevice never initialised it, so every non-owner spun in "
                    "Render_D3DDeviceOwnership_WaitAndReclaim. Set to -1 (unowned).");
      }
    }
  }

  {
    static std::atomic<bool> pacing_globals_settled{false};
    static std::atomic<bool> pacing_globals_healed{false};
    static std::atomic<uint64_t> pacing_globals_next_check_us{0};

    if (!pacing_globals_settled.load(std::memory_order_relaxed)) {
      const uint64_t now_us = static_cast<uint64_t>(
          std::chrono::duration_cast<std::chrono::microseconds>(
              std::chrono::steady_clock::now().time_since_epoch())
              .count());
      uint64_t due = pacing_globals_next_check_us.load(std::memory_order_relaxed);
      if (now_us >= due &&
          pacing_globals_next_check_us.compare_exchange_strong(
              due, now_us + 100000ull, std::memory_order_relaxed)) {
        constexpr uint32_t kGuestRefreshRateFloat = 0x8336035Cu;
        constexpr uint32_t kGuestPresentInterval = 0x83360358u;
        constexpr uint32_t kGuestDisplayPairW = 0x834A5FE8u;
        const uint32_t refresh_bits =
            refii::vfs::LoadGuestU32(base, kGuestRefreshRateFloat);
        if (refresh_bits == 0u &&
            !pacing_globals_healed.exchange(true, std::memory_order_relaxed)) {
          rex::system::X_VIDEO_MODE mode;
          rex::kernel::xboxkrnl::VdQueryVideoMode(&mode);
          const float refresh = mode.refresh_rate;
          refii::vfs::StoreGuestU32(base, kGuestRefreshRateFloat,
                                    std::bit_cast<uint32_t>(refresh));
          if (refii::vfs::LoadGuestU32(base, kGuestPresentInterval) == 0u) {
            refii::vfs::StoreGuestU32(base, kGuestPresentInterval, 1u);
          }
          REXLOG_INFO(
              "Refii pacing globals HEALED refresh={} interval={} "
              "display_pair=0x{:08X}/0x{:08X} (guest CreateDevice stores "
              "never ran — sub_82B6F6C0 skipped)",
              refresh,
              refii::vfs::LoadGuestU32(base, kGuestPresentInterval),
              refii::vfs::LoadGuestU32(base, kGuestDisplayPairW),
              refii::vfs::LoadGuestU32(base, kGuestDisplayPairW + 4u));
        } else if (refresh_bits != 0u) {
          REXLOG_INFO(
              "Refii pacing globals OK refresh_bits=0x{:08X} ({}) "
              "interval={} display_pair=0x{:08X}/0x{:08X}",
              refresh_bits, std::bit_cast<float>(refresh_bits),
              refii::vfs::LoadGuestU32(base, kGuestPresentInterval),
              refii::vfs::LoadGuestU32(base, kGuestDisplayPairW),
              refii::vfs::LoadGuestU32(base, kGuestDisplayPairW + 4u));
          pacing_globals_settled.store(true, std::memory_order_relaxed);
        }
      }
    }
  }

  // SPIN PROBE. The instrument the yield back-off is measured with, and the record of
  // what the yields turned out to be.
  //
  // Run 011 (2026-08-19, 817.8 s): 2,359,000,000 yields, 2.88 M/s, from exactly two
  // threads — 52.5% at lr=0x822DFF2C (the stream-drain wait in sub_822DFE08) and 46.2%
  // at lr=0x82277348 (the "no game tick due yet" wait in the main loop sub_82276D10).
  // At a measured 449 ns per sched_yield that is ~1.3 cores spent on nothing.
  //
  // BOTH ORIGINAL HYPOTHESES ARE DEAD, and the counters are what killed them:
  // from_present_throttle stayed 0, so the two-frame Present throttle at 0x82B6F380 is
  // not involved; and the pacing globals read back RefreshRate 60 / interval 2, so the
  // +inf-budget unbounded spin of runs 437/439 is not happening either. These are
  // ordinary waits that the 360 could afford to burn a hardware thread on and a host
  // cannot.
  //
  // Counting by CALLER (lr), because "SwitchToThread is hot" on its own would not
  // separate one waiting loop from another.
  {
    static std::atomic<uint64_t> total{0};
    static std::atomic<uint64_t> from_present_throttle{0};
    const uint32_t lr = ctx.lr;
    const bool present_throttle = lr >= 0x82B6F380u && lr <= 0x82B6F410u;
    if (present_throttle) {
      from_present_throttle.fetch_add(1, std::memory_order_relaxed);
    }
    const uint64_t n = total.fetch_add(1, std::memory_order_relaxed) + 1;
    if ((n % 100000ull) == 0ull) {
      REXLOG_INFO("Refii guest SwitchToThread spin probe total={} "
                  "from_present_throttle={} backoff_sleeps={} last_lr=0x{:08X}",
                  static_cast<unsigned long long>(n),
                  static_cast<unsigned long long>(
                      from_present_throttle.load(std::memory_order_relaxed)),
                  static_cast<unsigned long long>(
                      refii::vfs::GuestYieldBackoffSleepCount()),
                  lr);
      // Slot autopsy for the stream-drain spin (0x822DFF2C, inside
      // sub_822DFE08): IDA-proven r31 = the command-stream slot being
      // drained. The spin ends only when the OWNING guest thread (tid at
      // slot+100) publishes its shared WPTR (slot[15], guarded by the CS at
      // slot+72). Run 437 spent 51s here hostaging the GPU-access lock and
      // the loading-screen fence (black logo videos) — this names the
      // parked producer thread so we can see what OUR runtime has it
      // blocked in. Fields: [1]=buffer, [3]=read cursor, [4]=WPTR snapshot,
      // [15]=shared WPTR.
      if (lr == 0x822DFF2Cu) {
        const uint32_t slot = ctx.r31.u32;
        if (refii::vfs::LooksLikeGuestPointer(slot)) {
          const uint32_t owner_tid =
              refii::vfs::LoadGuestU32(base, slot + 100u);
          const uint32_t read_cursor =
              refii::vfs::LoadGuestU32(base, slot + 12u);
          const uint32_t wptr_snapshot =
              refii::vfs::LoadGuestU32(base, slot + 16u);
          const uint32_t shared_wptr =
              refii::vfs::LoadGuestU32(base, slot + 60u);
          // Autopsy of the PARKED owner: its most recent hooked guest symbol
          // and how long ago that hook entered (registry written on every
          // hook enter; see NoteRendererHookThreadTelemetry). A stale
          // symbol + large us_since pins where tid parked; "none" means the
          // owner never passed through a renderer hook.
          const char* owner_last_hook = "none";
          uint64_t owner_hook_age_us = 0;
          refii::renderer::QueryRendererHookThreadTelemetry(
              owner_tid, &owner_last_hook, &owner_hook_age_us);
          // Presents vs swap completions. The spin is bounded — `sub_822DFE08` gives up
          // after one presentation period (timeout branch 0x822DFF24 into the epilogue at
          // 0x822E1694) — so a hung run's yields are cumulative over many doomed calls.
          // The drain's frame deadline comes from `sub_82B6FCE8` = `presented - completed + 1`;
          // `Render_PresentFrame_INFERRED` bumps g_PresentFrameCount at 0x82B6F290 and only the
          // swap-completion callback advances the completed count, so a widening gap means the
          // deadline is running ahead of the stream clock.
          //
          // Read the record, not the selector: 0x83497124 is the frame-sync SLOT INDEX, 0 or 1
          // (`Render_FrameCompletionCallback` 0x82B90160 publishes it one bit wide). The real
          // count is `*(u32*)(0x83491D98 + 24*index + 0x10)`.
          const uint32_t presented =
              refii::vfs::LoadGuestU32(base, 0x83497120u);
          const uint32_t frame_sync_index =
              refii::vfs::LoadGuestU32(base, 0x83497124u);
          const uint32_t completed =
              frame_sync_index < 2u
                  ? refii::vfs::LoadGuestU32(
                        base, 0x83491D98u + frame_sync_index * 24u + 0x10u)
                  : 0u;
          REXLOG_INFO(
              "Refii guest stream-drain spin slot=0x{:08X} owner_tid=0x{:08X} "
              "read=0x{:08X} wptr_snap=0x{:08X} shared_wptr=0x{:08X} "
              "owner_last_hook={} owner_hook_age_us={} "
              "presented={} completed={} skew={} sync_slot={}",
              slot, owner_tid, read_cursor, wptr_snapshot, shared_wptr,
              owner_last_hook, owner_hook_age_us, presented, completed,
              static_cast<int32_t>(presented - completed), frame_sync_index);
        }
      }
    }
  }

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82CC38E8);
}

namespace {
// ---- lhRenderer suspend/resume handshake emulation (2026-08-02) ----------
// Save-load black screen root cause (runs 516/517/518). Ground truth from
// generated/refii_recomp.19.cpp: the "3D Engine" render-thread controller
// object obj carries flags +140 running / +148 stop / +192 suspend-request /
// +193 suspend-ack, and THREE auto-reset events signalled with PulseEvent
// (sub_822C9388), each waited INFINITE via sub_82B4F400(event_obj = obj +
// embed_offset, -1, 0) where the OS handle lives at event_obj+128:
//   A embed +196 (handle +324) suspend ack — waited at lr 0x823889FC
//     (sub_823889A8), pulsed at lr 0x823882B8 AFTER the render thread stores
//     +192=0, +193=1;
//   B embed +328 (handle +456) resume kick — waited at lr 0x823882E0
//     (rex_lhRenderer_ThreadFunc), pulsed at lr 0x82388A40 (sub_82388A18);
//     the only other +456 xref is CloseHandle in the dtor sub_82388720;
//   C embed +460 (handle +588) resume ack — waited at lr 0x82388A6C
//     (sub_82388A18), pulsed at lr 0x82388308 AFTER the render thread clears
//     +193.
// Every waiter releases the controller CS BEFORE entering its wait, and
// PulseEvent releases only threads ALREADY waiting — a pulse landing in that
// gap is silently discarded and, the waits being INFINITE, both the loading
// thread and the render thread park forever (run-518 watchdog autopsy: the
// pair parked together at stall onset). The loading screen is drawn by the
// LOADING thread while the render thread is suspended, so the result is a
// permanent black screen.
//
// Fix shape (waiter-side, guest state untouched): the FLAGS are the
// authoritative handshake state; the pulses are only wakeups. Each of the
// three waits runs in bounded slices through __imp__sub_82B4F400 (so a
// DELIVERED pulse still wakes it immediately) and additionally completes on
// its condition:
//   A completes when +193 == 1 (stored before the pulse, same CS),
//   C completes when +193 == 0 (cleared before the pulse, same CS),
//   B has no guest flag — the pulse IS the whole message — so the single
//     pulse site bumps a host-side epoch BEFORE the guest pulse and the wait
//     completes when epoch > consumed.
// A lost pulse therefore costs at most one slice instead of a permanent
// park. Behaviour is unchanged whenever the pulse is delivered. Kill switch:
// REX_VFS_LH_HANDSHAKE_EMULATION=0 restores the original INFINITE waits.
constexpr uint32_t kLhSuspendAckWaitLr = 0x823889FCu;
constexpr uint32_t kLhResumeKickWaitLr = 0x823882E0u;
constexpr uint32_t kLhResumeAckWaitLr = 0x82388A6Cu;
// THE WORLD UPDATE'S PRESENT GATE. rex_lhRenderer_SyncThread (sub_82A3EC50)
// polls a present-completion event with a bounded 1000ms wait (lr 0x82A3ECB0,
// loop loc_82A3ECA0) and re-loops while the result is timeout. The render side
// that signals it is off in this headless build, so the game thread parks here
// forever after New Game — the camera frozen in the void (the world loads, the
// guest is released, but the frame/camera pass never advances). Release it at
// the call site: signal the event and return signalled so the loop exits.
constexpr uint32_t kPresentGateWaitLr = 0x82A3ECB0u;
constexpr uint32_t kLhEventSuspendAckEmbedOffset = 196u;
constexpr uint32_t kLhEventResumeKickEmbedOffset = 328u;
constexpr uint32_t kLhEventResumeAckEmbedOffset = 460u;
constexpr uint32_t kLhSuspendAckFlagOffset = 193u;
// sub_82B4F400 result mapping (generated/refii_recomp.83.cpp): 2 = signalled
// (WAIT_OBJECT_0/ABANDONED), 1 = APC (WAIT_IO_COMPLETION), 0 = WAIT_TIMEOUT.
constexpr uint32_t kLhWaitResultSignalled = 2u;
constexpr uint32_t kLhWaitResultTimeout = 0u;
constexpr uint32_t kLhWaitSliceMs = 4u;

std::atomic<uint64_t> g_lh_resume_kick_epoch{0};
std::atomic<uint64_t> g_lh_resume_kick_consumed{0};

bool LhHandshakeEmulationEnabled() {
  static const bool enabled = [] {
    const char* value = std::getenv("REX_VFS_LH_HANDSHAKE_EMULATION");
    return value == nullptr || value[0] != '0';
  }();
  return enabled;
}

// Returns true when it handled the wait (ctx.r3 holds the mapped result);
// false hands the call to the original INFINITE wait.
bool TryEmulateLhRendererHandshakeWait(PPCContext& ctx, uint8_t* base,
                                       const PPCContext& original_ctx) {
  if (!LhHandshakeEmulationEnabled()) {
    return false;
  }
  // Only the three known INFINITE handshake waits; anything else (including
  // these sites with a finite timeout, which never ships) is untouched.
  if (original_ctx.r4.u32 != 0xFFFFFFFFu) {
    return false;
  }
  const uint32_t lr = static_cast<uint32_t>(original_ctx.lr);
  enum class LhWaitKind { kSuspendAck, kResumeKick, kResumeAck };
  LhWaitKind kind;
  uint32_t controller_guest = 0;
  switch (lr) {
    case kLhSuspendAckWaitLr:
      kind = LhWaitKind::kSuspendAck;
      controller_guest = original_ctx.r3.u32 - kLhEventSuspendAckEmbedOffset;
      break;
    case kLhResumeKickWaitLr:
      kind = LhWaitKind::kResumeKick;
      controller_guest = original_ctx.r3.u32 - kLhEventResumeKickEmbedOffset;
      break;
    case kLhResumeAckWaitLr:
      kind = LhWaitKind::kResumeAck;
      controller_guest = original_ctx.r3.u32 - kLhEventResumeAckEmbedOffset;
      break;
    default:
      return false;
  }
  const auto condition_satisfied = [&]() -> bool {
    switch (kind) {
      case LhWaitKind::kSuspendAck:
        return refii::vfs::LoadGuestU8(
                   base, controller_guest + kLhSuspendAckFlagOffset) != 0;
      case LhWaitKind::kResumeAck:
        return refii::vfs::LoadGuestU8(
                   base, controller_guest + kLhSuspendAckFlagOffset) == 0;
      case LhWaitKind::kResumeKick: {
        const uint64_t epoch =
            g_lh_resume_kick_epoch.load(std::memory_order_acquire);
        if (epoch >
            g_lh_resume_kick_consumed.load(std::memory_order_relaxed)) {
          g_lh_resume_kick_consumed.store(epoch, std::memory_order_relaxed);
          return true;
        }
        return false;
      }
    }
    return false;
  };
  const auto t0 = std::chrono::steady_clock::now();
  uint32_t slices = 0;
  bool recovered = false;
  for (;;) {
    // Condition FIRST: a pulse lost BEFORE this wait was entered (the exact
    // run-516/517/518 race window) is recovered without blocking at all.
    if (condition_satisfied()) {
      ctx.r3.u64 = kLhWaitResultSignalled;
      recovered = true;
      break;
    }
    ctx.r3.u32 = original_ctx.r3.u32;
    ctx.r4.s64 = static_cast<int32_t>(kLhWaitSliceMs);
    ctx.r5.u32 = original_ctx.r5.u32;
    __imp__sub_82B4F400(ctx, base);
    ++slices;
    if (ctx.r3.u32 != kLhWaitResultTimeout) {
      // Real wake (or APC): original semantics, pass the result through. Keep
      // the resume-kick ledger aligned — its pulse pre-incremented the epoch.
      if (kind == LhWaitKind::kResumeKick &&
          ctx.r3.u32 == kLhWaitResultSignalled) {
        g_lh_resume_kick_consumed.store(
            g_lh_resume_kick_epoch.load(std::memory_order_acquire),
            std::memory_order_relaxed);
      }
      break;
    }
  }
  if (recovered) {
    // Only a recovery is interesting: the original code would have parked
    // forever here. Expected once per lost pulse, i.e. rarely.
    static std::atomic<uint32_t> recovery_logs{0};
    const uint32_t count =
        recovery_logs.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (count <= 16u || (count % 64u) == 0u) {
      const int64_t waited_ms =
          std::chrono::duration_cast<std::chrono::milliseconds>(
              std::chrono::steady_clock::now() - t0)
              .count();
      REXLOG_WARN(
          "Refii loading-screen handshake emulated #{}: {} satisfied by "
          "condition after a lost pulse — waited {}ms (slices={}) "
          "controller=0x{:08X} lr=0x{:08X}",
          count,
          kind == LhWaitKind::kSuspendAck    ? "suspend ack"
          : kind == LhWaitKind::kResumeKick  ? "resume kick"
                                             : "resume ack",
          waited_ms, slices, controller_guest, lr);
    }
  }
  return true;
}
}  // namespace

REX_EXTERN(sub_82B4F400) {
  REX_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;

  // Names every unbounded wait once per caller. This is the guest's WaitForSingleObjectEx
  // wrapper, so a thread that parks forever parks here and only `lr` says which wait it was —
  // hence keying on the caller rather than a call count. `r4 == -1` is INFINITE; bounded waits
  // are not reported.
  if (original_ctx.r4.u32 == 0xFFFFFFFFu) {
    static std::mutex infinite_wait_mutex;
    static std::unordered_set<uint32_t> infinite_wait_callers;
    const uint32_t caller = static_cast<uint32_t>(original_ctx.lr);
    bool first = false;
    {
      std::lock_guard<std::mutex> lock(infinite_wait_mutex);
      first = infinite_wait_callers.insert(caller).second;
    }
    if (first) {
      REXLOG_INFO("Refii boot: INFINITE wait entered lr=0x{:08X} object=0x{:08X} "
                  "— if the run stalls, this is a candidate for where",
                  caller, original_ctx.r3.u32);
    }
  }

  // A COMMAND-STREAM FENCE WAIT, not a present-completion event.
  //
  // sub_82A4EB08 builds the object this waits on: CreateEventA(0, 1, 0, 0) at +128, then
  // writes a 0xF0F0F0F0-tagged packet carrying it into the ring at dword_8349F9B0. The
  // consumer is sub_822DFE08, and Render_GpuAccessLock_Enter (0x82A4F0E0) picks between
  // them — 0x82A4F19C drains inline when this thread owns the lock, 0x82A4F1AC posts the
  // fence and waits (sub_82A3EC20) when it does not. Headless nothing drains the ring, so
  // the wait at lr 0x82A3ECB0 never completes and the game thread parks after New Game.
  //
  // Returning kLhWaitResultSignalled IS the release. Signalling the event first would be a
  // no-op: sub_82A3EC20 CloseHandle()s it at 0x82A3ECD4, two instructions after the wait
  // returns. (It also does not build — the payload reaches rex:: only through the C bridge.)
  //
  // The gate is NOT "past boot". byte_83496AC4 is set by the front-end setup (sub_82319340)
  // and cleared by its teardown (sub_82319A40 @0x82319B24), so it is 0 again in game;
  // byte_83496A5E is set by Game_InitDatabases (@0x82311648) and cleared by sub_823117A8.
  // The OR reads "front end up OR databases up", which is what holds it clear through the
  // startup movies — the property this relies on. The fence itself means the same thing in
  // both phases, so this is a phase heuristic, not a contract.
  const bool frontend_or_databases_up =
      refii::vfs::LoadGuestU8(base, 0x83496AC4u) != 0u ||
      refii::vfs::LoadGuestU8(base, 0x83496A5Eu) != 0u;
  if (original_ctx.lr == kPresentGateWaitLr && frontend_or_databases_up) {
    ctx.r3.u32 = kLhWaitResultSignalled;
    return;
  }

  // Both bytes are clear during the startup movies (where the original wait is deliberate) and
  // also between front-end teardown (`sub_82319A40` clears 0x83496AC4) and `Game_InitDatabases`
  // (@0x82311648 sets 0x83496A5E). A present-gate wait in that window goes to a fence nothing
  // drains headless. The decision is unchanged, since no signal is yet known to separate the
  // two cases; this only logs the wait.
  if (original_ctx.lr == kPresentGateWaitLr) {
    static std::atomic<uint32_t> gap_waits{0u};
    const uint32_t seen = gap_waits.fetch_add(1u, std::memory_order_relaxed) + 1u;
    if (seen == 1u || seen == 64u) {
      REXLOG_WARN(
          "Refii present gate: wait #{} handed to the original with the front end DOWN and "
          "databases NOT up (0x83496AC4=0, 0x83496A5E=0). Headless nothing drains this fence; "
          "if the game thread parks here, this is why.",
          seen);
    }
  }

  // lhRenderer suspend/resume handshake waits are emulated (bounded slices +
  // authoritative-flag completion) so a lost PulseEvent cannot park the
  // loading/render thread pair forever. This MUST run before the native
  // passthrough: that macro returns early in the native backend, which would
  // otherwise bypass the emulation and let the in-game suspend-ack wait
  // (sub_823889A8) park the game thread forever — the "camera in the void"
  // stall after New Game. Emulation is backend-independent; a lost pulse is a
  // no-present hazard, not a loose-asset one.
  if (TryEmulateLhRendererHandshakeWait(ctx, base, original_ctx)) {
    return;
  }

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B4F400);
}

REX_EXTERN(sub_82A41B00) {
  REX_FUNC_PROLOGUE();

  // BinkClose. r3 is B, the same handle the open hook publishes — and the caller frees the
  // player right after this returns (`sub_82389210`: close at 0x8238930C, free at 0x82389314,
  // manager slot nulled at 0x82389318). Retire the host's handle FIRST, before the passthrough
  // below, which returns straight out in a bank build.
  refii::vfs::NoteBinkMovieClosed(ctx.r3.u32);

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82A41B00);
}
