#include "renderer_hooks_private.h"

#include "runtime/refii_renderer/bik/bink_guest_layout.h"
#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "runtime/refii_renderer/bik/bink_movie_clock.h"
#include "runtime/refii_renderer/bik/bink_startup_audio_drain.h"
#include "runtime/refii_renderer/bik/bink_startup_audio_pump.h"
#include "runtime/refii_renderer/bik/bink_startup_ownership.h"
#include "runtime/refii_renderer/bik/bink_startup_trace.h"
#include "runtime/refii_renderer/bik/bink_state_token_bridge.h"
#include "renderer_runtime.h"
#include "runtime/hook_observer.h"

#include <atomic>
#include <thread>

namespace {

using refii::renderer::bink::IsReadableGuestByteRange;
using refii::renderer::bink::kStartupBinkMovieBytes;
using refii::renderer::bink::kStartupBinkPlaneBytes;
using refii::renderer::bink::LoadGuestU8;
using refii::renderer::bink::LoadGuestU32;
using refii::renderer::bink::BinkRingFill;
using refii::renderer::bink::StoreGuestU32;
using refii::renderer::bink::BinkPlumeOwnsMovie;
using refii::renderer::bink::BinkMovieFinished;
using refii::renderer::bink::ClearMovie;
using refii::renderer::bink::ClearStartupAudioCursorsForMovie;
using refii::renderer::bink::LogStartupBinkAudioTrace;
using refii::renderer::bink::LogStartupBinkBitstreamCopyTrace;
using refii::renderer::bink::LogStartupBinkCallbackTrace;
using refii::renderer::bink::LogStartupBinkDrainState;
using refii::renderer::bink::LogStartupBinkMovieState;
using refii::renderer::bink::LogStartupBinkSoundCallbackTrace;
using refii::renderer::bink::LogStartupBinkTiming;
using refii::renderer::bink::StartupBinkTimingClock;
using refii::renderer::bink::StartupBinkTimingUs;
using refii::renderer::bink::TryRunBinkNativeAudioDrain;

std::atomic<uint32_t> g_bink_plume_direct_drain_skips{0};

std::atomic<uint32_t> g_bink_plume_clock_waits{0};

uint8_t StartupVideoOwnerFailed(uint8_t *base) {
  if (base == nullptr) {
    return 0;
  }
  const uint32_t root = LoadGuestU32(base, 0x83496B0Cu);
  if (!IsReadableGuestByteRange(base, root + 8u, sizeof(uint32_t))) {
    return 0;
  }
  const uint32_t state = LoadGuestU32(base, root + 8u);
  if (!IsReadableGuestByteRange(base, state + 40u, sizeof(uint32_t))) {
    return 0;
  }
  const uint32_t owner = LoadGuestU32(base, state + 40u);
  if (!IsReadableGuestByteRange(base, owner + 4u, sizeof(uint32_t))) {
    return 0;
  }
  const uint32_t frame_object = LoadGuestU32(base, owner + 4u);
  return IsReadableGuestByteRange(base, frame_object + 244u, 1u)
             ? LoadGuestU8(base, frame_object + 244u)
             : 0;
}

void LogStartupVideoOwnerFailureStage(const char *stage, uint32_t movie_guest,
                                      uint8_t before, uint8_t *base) {
  const uint8_t after = StartupVideoOwnerFailed(base);
  if (before == 0 && after != 0) {
    REXLOG_INFO("Refii renderer startup BIK native owner failure stage={} "
                "movie=0x{:08X} failed={}->{} frame={}/{}",
                stage, movie_guest, before, after,
                IsReadableGuestByteRange(base, movie_guest + 12u, 4u)
                    ? LoadGuestU32(base, movie_guest + 12u)
                    : 0u,
                IsReadableGuestByteRange(base, movie_guest + 8u, 4u)
                    ? LoadGuestU32(base, movie_guest + 8u)
                    : 0u);
  }
}

struct StartupFrameBoundaryState {
  uint32_t frame_object = 0;
  uint32_t movie = 0;
  uint32_t frame = 0;
  uint32_t total = 0;
  uint8_t finished = 0;
  uint8_t failed = 0;
  bool readable = false;
};

StartupFrameBoundaryState ReadStartupFrameBoundaryState(uint8_t *base,
                                                        uint32_t builder) {
  StartupFrameBoundaryState state;
  if (base == nullptr || builder < 12u) {
    return state;
  }
  state.frame_object = builder - 12u;
  if (!IsReadableGuestByteRange(base, state.frame_object, 245u)) {
    return state;
  }
  state.readable = true;
  state.finished = LoadGuestU8(base, state.frame_object + 233u);
  state.failed = LoadGuestU8(base, state.frame_object + 244u);
  state.movie = LoadGuestU32(base, state.frame_object + 76u);
  if (state.movie != 0 &&
      IsReadableGuestByteRange(base, state.movie, kStartupBinkMovieBytes)) {
    state.total = LoadGuestU32(base, state.movie + 8u);
    state.frame = LoadGuestU32(base, state.movie + 12u);
  }
  return state;
}

void LogStartupFrameBoundaryTransition(
    const char *stage, const StartupFrameBoundaryState &before,
    const StartupFrameBoundaryState &after) {
  if (!before.readable || !after.readable ||
      (before.finished == after.finished && before.failed == after.failed &&
       before.movie == after.movie && before.frame == after.frame)) {
    return;
  }
  REXLOG_INFO("Refii renderer startup BIK frame builder boundary stage={} "
              "object=0x{:08X} finished={}->{} failed={}->{} "
              "movie=0x{:08X}->0x{:08X} frame={}->{} total={}",
              stage, after.frame_object, before.finished, after.finished,
              before.failed, after.failed, before.movie, after.movie,
              before.frame, after.frame, after.total);
}

void SyncPlumeOwnedBinkMovieClock(const PPCContext &ctx, uint8_t *base,
                                  uint32_t movie_guest) {
  if (base == nullptr || movie_guest == 0 ||
      !IsReadableGuestByteRange(base, movie_guest, kStartupBinkMovieBytes)) {
    return;
  }

  PPCContext timer_ctx = ctx;
  timer_ctx.r3.u32 = movie_guest;
  __imp__sub_832C0AB8(timer_ctx, base);
  StoreGuestU32(base, movie_guest + 704u, timer_ctx.r3.u32);
  // Native clock reset (sub_832BC598/sub_832BD710) uses frame-1 and zeroes
  // the skip counter at +836, but those run at RESET points only; this sync
  // re-bases EVERY tick for Plume-owned pacing, where the frame-1 baseline
  // makes the decoder run perpetual catch-up. Keep the same-frame baseline.
  const uint32_t wrapper_frame = LoadGuestU32(base, movie_guest + 692u);
  if (wrapper_frame != 0) {
    StoreGuestU32(base, movie_guest + 708u, wrapper_frame);
  }
}

void ClearStartupBinkStateContext(uint8_t *base, uint32_t context_guest) {
  if (base != nullptr && context_guest != 0) {
    *reinterpret_cast<volatile uint32_t *>(base + context_guest) =
        __builtin_bswap32(0u);
  }
}

void RestoreStartupBinkNonvolatileRegisters(PPCContext &ctx,
                                            const PPCContext &preserved) {
  ctx.r14 = preserved.r14;
  ctx.r15 = preserved.r15;
  ctx.r16 = preserved.r16;
  ctx.r17 = preserved.r17;
  ctx.r18 = preserved.r18;
  ctx.r19 = preserved.r19;
  ctx.r20 = preserved.r20;
  ctx.r21 = preserved.r21;
  ctx.r22 = preserved.r22;
  ctx.r23 = preserved.r23;
  ctx.r24 = preserved.r24;
  ctx.r25 = preserved.r25;
  ctx.r26 = preserved.r26;
  ctx.r27 = preserved.r27;
  ctx.r28 = preserved.r28;
  ctx.r29 = preserved.r29;
  ctx.r30 = preserved.r30;
  ctx.r31 = preserved.r31;
}

} // namespace

#define REFII_RENDERER_PASSTHROUGH_HOOK_AS(wrapper_symbol, imp_symbol, kind)   \
  REX_EXTERN(wrapper_symbol) {                                                 \
    REX_FUNC_PROLOGUE();                                                       \
    static const refii::renderer::FableHookSymbolDesc *hook_desc =             \
        refii::renderer::ResolveFableHookSymbolDesc(#wrapper_symbol, kind);    \
    const refii::renderer::RendererHookArgs args =                             \
        refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);                         \
    refii::renderer::OnRendererGuestHookEnter(#wrapper_symbol, kind, args,     \
                                              base, hook_desc);                           \
    if (refii::renderer::ShouldRunGuestRendererHook(#wrapper_symbol, kind,     \
                                                     args, base, hook_desc)) {            \
      __imp__##imp_symbol(ctx, base);                                          \
      refii::renderer::OnRendererGuestHookExit(#wrapper_symbol, kind, args,    \
                                               ctx, base, hook_desc);                     \
    } else {                                                                   \
      refii::renderer::OnRendererGuestHookSuppressedForPlume(                 \
          #wrapper_symbol, kind, args, ctx, base, hook_desc);                             \
    }                                                                          \
  }

#define REFII_RENDERER_GENERAL_HOOK(symbol)                                    \
  REFII_RENDERER_PASSTHROUGH_HOOK_AS(                                          \
      symbol, symbol, refii::renderer::RendererHookKind::kGeneral)

#define REFII_RENDERER_RESOURCE_HOOK(symbol)                                   \
  REFII_RENDERER_PASSTHROUGH_HOOK_AS(                                          \
      symbol, symbol, refii::renderer::RendererHookKind::kResource)

#define REFII_RENDERER_DRAW_HOOK(symbol)                                       \
  REFII_RENDERER_PASSTHROUGH_HOOK_AS(symbol, symbol,                           \
                                     refii::renderer::RendererHookKind::kDraw)

#define REFII_RENDERER_TIMED_STARTUP_BINK_GENERAL_HOOK(symbol)                 \
  REX_EXTERN(symbol) {                                                         \
    REX_FUNC_PROLOGUE();                                                       \
    constexpr refii::renderer::RendererHookKind kind =                         \
        refii::renderer::RendererHookKind::kGeneral;                           \
    static const refii::renderer::FableHookSymbolDesc *hook_desc =            \
        refii::renderer::ResolveFableHookSymbolDesc(#symbol, kind);           \
    const refii::renderer::RendererHookArgs args =                             \
        refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);                         \
    refii::renderer::OnRendererGuestHookEnter(#symbol, kind, args, base, hook_desc);      \
    if (refii::renderer::ShouldRunGuestRendererHook(#symbol, kind, args,       \
                                                     base, hook_desc)) {                  \
      const auto begin = StartupBinkTimingClock::now();                        \
      __imp__##symbol(ctx, base);                                              \
      const uint64_t elapsed_us =                                              \
          StartupBinkTimingUs(begin, StartupBinkTimingClock::now());           \
      LogStartupBinkTiming(#symbol, args, ctx, elapsed_us);                    \
      refii::renderer::OnRendererGuestHookExit(#symbol, kind, args, ctx,       \
                                               base, hook_desc);                          \
    } else {                                                                   \
      refii::renderer::OnRendererGuestHookSuppressedForPlume(                 \
          #symbol, kind, args, ctx, base, hook_desc);                                     \
    }                                                                          \
  }

#define REFII_RENDERER_TIMED_STARTUP_BINK_UPLOAD_HOOK(symbol)                  \
  REX_EXTERN(symbol) {                                                         \
    REX_FUNC_PROLOGUE();                                                       \
    constexpr refii::renderer::RendererHookKind kind =                         \
        refii::renderer::RendererHookKind::kGeneral;                           \
    static const refii::renderer::FableHookSymbolDesc *hook_desc =            \
        refii::renderer::ResolveFableHookSymbolDesc(#symbol, kind);           \
    const refii::renderer::RendererHookArgs args =                             \
        refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);                         \
    refii::renderer::OnRendererGuestHookEnter(#symbol, kind, args, base, hook_desc);      \
    if (refii::renderer::ShouldRunGuestRendererHook(#symbol, kind, args,       \
                                                     base, hook_desc)) {                  \
      const auto begin = StartupBinkTimingClock::now();                        \
      __imp__##symbol(ctx, base);                                              \
      refii::renderer::RefreshBinkVideoUploadsFromRendererHook(#symbol, args,  \
                                                               base);          \
      const uint64_t elapsed_us =                                              \
          StartupBinkTimingUs(begin, StartupBinkTimingClock::now());           \
      LogStartupBinkTiming(#symbol, args, ctx, elapsed_us);                    \
      refii::renderer::OnRendererGuestHookExit(#symbol, kind, args, ctx,       \
                                               base, hook_desc);                          \
    } else {                                                                   \
      refii::renderer::OnRendererGuestHookSuppressedForPlume(                 \
          #symbol, kind, args, ctx, base, hook_desc);                                     \
    }                                                                          \
  }

#define REFII_RENDERER_STARTUP_BINK_LOOKUP_HOOK(symbol)                        \
  REX_EXTERN(symbol) {                                                         \
    REX_FUNC_PROLOGUE();                                                       \
    constexpr refii::renderer::RendererHookKind kind =                         \
        refii::renderer::RendererHookKind::kDraw;                              \
    static const refii::renderer::FableHookSymbolDesc *hook_desc =            \
        refii::renderer::ResolveFableHookSymbolDesc(#symbol, kind);           \
    const refii::renderer::RendererHookArgs args =                             \
        refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);                         \
    refii::renderer::OnRendererGuestHookEnter(#symbol, kind, args, base, hook_desc);      \
    const PPCContext preserved = ctx;                                          \
    if (refii::renderer::bink::TryGetBridgeStartupBinkTokenLookup(            \
            #symbol, args, ctx, base)) {                                       \
      const auto result = ctx.r3;                                              \
      RestoreStartupBinkNonvolatileRegisters(ctx, preserved);                  \
      ctx.r3 = result;                                                         \
      refii::renderer::OnRendererGuestHookExit(#symbol, kind, args, ctx,       \
                                               base, hook_desc);                          \
    } else if (refii::renderer::ShouldRunGuestRendererHook(#symbol, kind,      \
                                                            args, base, hook_desc)) {     \
      refii::renderer::bink::EnterStartupBinkStateContextBridge();             \
      __imp__##symbol(ctx, base);                                              \
      refii::renderer::bink::LeaveStartupBinkStateContextBridge();             \
      refii::renderer::bink::CacheBridgeStartupBinkTokenLookup(                \
          #symbol, args, ctx, base);                                           \
      refii::renderer::OnRendererGuestHookExit(#symbol, kind, args, ctx,       \
                                               base, hook_desc);                          \
    } else {                                                                   \
      refii::renderer::OnRendererGuestHookSuppressedForPlume(                 \
          #symbol, kind, args, ctx, base, hook_desc);                                     \
    }                                                                          \
  }

#define REFII_RENDERER_STARTUP_BINK_STRING_CONSTRUCT_HOOK(symbol)              \
  REX_EXTERN(symbol) {                                                         \
    REX_FUNC_PROLOGUE();                                                       \
    constexpr refii::renderer::RendererHookKind kind =                         \
        refii::renderer::RendererHookKind::kDraw;                              \
    static const refii::renderer::FableHookSymbolDesc *hook_desc =            \
        refii::renderer::ResolveFableHookSymbolDesc(#symbol, kind);           \
    const refii::renderer::RendererHookArgs args =                             \
        refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);                         \
    refii::renderer::OnRendererGuestHookEnter(#symbol, kind, args, base, hook_desc);      \
    const PPCContext preserved = ctx;                                          \
    if (refii::renderer::bink::TryBridgeStartupBinkStackStringConstruct(       \
            args, ctx, base)) {                                                \
      const auto result = ctx.r3;                                              \
      RestoreStartupBinkNonvolatileRegisters(ctx, preserved);                  \
      ctx.r3 = result;                                                         \
      refii::renderer::OnRendererGuestHookExit(#symbol, kind, args, ctx,       \
                                               base, hook_desc);                          \
    } else if (refii::renderer::ShouldRunGuestRendererHook(#symbol, kind,      \
                                                            args, base, hook_desc)) {     \
      __imp__##symbol(ctx, base);                                              \
      refii::renderer::OnRendererGuestHookExit(#symbol, kind, args, ctx,       \
                                               base, hook_desc);                          \
    } else {                                                                   \
      refii::renderer::OnRendererGuestHookSuppressedForPlume(                 \
          #symbol, kind, args, ctx, base, hook_desc);                                     \
    }                                                                          \
  }

#define REFII_RENDERER_STARTUP_BINK_STRING_DESTRUCT_HOOK(symbol)               \
  REX_EXTERN(symbol) {                                                         \
    REX_FUNC_PROLOGUE();                                                       \
    constexpr refii::renderer::RendererHookKind kind =                         \
        refii::renderer::RendererHookKind::kDraw;                              \
    static const refii::renderer::FableHookSymbolDesc *hook_desc =            \
        refii::renderer::ResolveFableHookSymbolDesc(#symbol, kind);           \
    const refii::renderer::RendererHookArgs args =                             \
        refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);                         \
    refii::renderer::OnRendererGuestHookEnter(#symbol, kind, args, base, hook_desc);      \
    const PPCContext preserved = ctx;                                          \
    if (refii::renderer::bink::TryBridgeStartupBinkStackStringDestruct(        \
            args, ctx, base)) {                                                \
      const auto result = ctx.r3;                                              \
      RestoreStartupBinkNonvolatileRegisters(ctx, preserved);                  \
      ctx.r3 = result;                                                         \
      refii::renderer::OnRendererGuestHookExit(#symbol, kind, args, ctx,       \
                                               base, hook_desc);                          \
    } else if (refii::renderer::ShouldRunGuestRendererHook(#symbol, kind,      \
                                                            args, base, hook_desc)) {     \
      __imp__##symbol(ctx, base);                                              \
      refii::renderer::OnRendererGuestHookExit(#symbol, kind, args, ctx,       \
                                               base, hook_desc);                          \
    } else {                                                                   \
      refii::renderer::OnRendererGuestHookSuppressedForPlume(                 \
          #symbol, kind, args, ctx, base, hook_desc);                                     \
    }                                                                          \
  }

#define REFII_RENDERER_STARTUP_BINK_CONTEXT_HOOK(symbol)                       \
  REX_EXTERN(symbol) {                                                         \
    REX_FUNC_PROLOGUE();                                                       \
    constexpr refii::renderer::RendererHookKind kind =                         \
        refii::renderer::RendererHookKind::kDraw;                              \
    static const refii::renderer::FableHookSymbolDesc *hook_desc =            \
        refii::renderer::ResolveFableHookSymbolDesc(#symbol, kind);           \
    const refii::renderer::RendererHookArgs args =                             \
        refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);                         \
    const bool state_context_bridge =                                          \
        refii::renderer::bink::IsStartupBinkStateContextBridge();              \
    if (state_context_bridge) {                                                \
      refii::renderer::OnRendererGuestHookEnter(#symbol, kind, args, base, hook_desc);    \
      ClearStartupBinkStateContext(base, args.r3);                             \
      ctx.r3.u32 = args.r3;                                                    \
      refii::renderer::OnRendererGuestHookExit(#symbol, kind, args, ctx,       \
                                               base, hook_desc);                          \
      return;                                                                  \
    }                                                                          \
    refii::renderer::OnRendererGuestHookEnter(#symbol, kind, args, base, hook_desc);      \
    if (refii::renderer::ShouldRunGuestRendererHook(#symbol, kind, args,       \
                                                     base, hook_desc)) {                  \
      __imp__##symbol(ctx, base);                                              \
      refii::renderer::OnRendererGuestHookExit(#symbol, kind, args, ctx,       \
                                               base, hook_desc);                          \
    } else {                                                                   \
      refii::renderer::OnRendererGuestHookSuppressedForPlume(                 \
          #symbol, kind, args, ctx, base, hook_desc);                                     \
    }                                                                          \
  }

REFII_RENDERER_RESOURCE_HOOK(
    sub_82A4E8D0) // Fable Bink video plane surface setup

REX_EXTERN(sub_82377F30) {
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "sub_82377F30", refii::renderer::RendererHookKind::kGeneral);

  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  constexpr refii::renderer::RendererHookKind kind =
      refii::renderer::RendererHookKind::kGeneral;
  refii::renderer::OnRendererGuestHookEnter("sub_82377F30", kind, args, base, hook_desc);
  __imp__sub_82377F30(ctx, base);
  const uint32_t update_guest =
      IsReadableGuestByteRange(base, args.r3 + 12u, sizeof(uint32_t))
          ? LoadGuestU32(base, args.r3 + 12u)
          : 0u;
  refii::renderer::RegisterFableStartupVideoPumpContext(base, ctx, args.r3,
                                                        update_guest);
  static std::atomic<uint32_t> startup_owner_ctor_logs{0};
  const uint32_t count =
      startup_owner_ctor_logs.fetch_add(1, std::memory_order_relaxed) + 1u;
  if (count <= 8u || count % 120u == 0u) {
    const bool update_readable =
        IsReadableGuestByteRange(base, update_guest, 104u);
    REXLOG_INFO(
        "Refii renderer startup video owner ctor #{} owner=0x{:08X} "
        "update=0x{:08X} owner_vt=0x{:08X} update_vt=0x{:08X} "
        "owner_slots=0x{:08X}/0x{:08X}/0x{:08X}/0x{:08X} "
        "update_slots=0x{:08X}/0x{:08X}/0x{:08X}/0x{:08X} "
        "frame={} active_movie=0x{:08X} movie_state=0x{:08X} "
        "pump_state=0x{:08X} vector=0x{:08X}-0x{:08X} index={} "
        "complete={} lr=0x{:08X}",
        count,
        args.r3,
        update_guest,
        IsReadableGuestByteRange(base, args.r3, sizeof(uint32_t))
            ? LoadGuestU32(base, args.r3)
            : 0u,
        update_readable ? LoadGuestU32(base, update_guest) : 0u,
        IsReadableGuestByteRange(base, args.r3, sizeof(uint32_t))
            ? LoadGuestU32(base, LoadGuestU32(base, args.r3) + 0u)
            : 0u,
        IsReadableGuestByteRange(base, args.r3, sizeof(uint32_t))
            ? LoadGuestU32(base, LoadGuestU32(base, args.r3) + 4u)
            : 0u,
        IsReadableGuestByteRange(base, args.r3, sizeof(uint32_t))
            ? LoadGuestU32(base, LoadGuestU32(base, args.r3) + 8u)
            : 0u,
        IsReadableGuestByteRange(base, args.r3, sizeof(uint32_t))
            ? LoadGuestU32(base, LoadGuestU32(base, args.r3) + 12u)
            : 0u,
        update_readable ? LoadGuestU32(base, LoadGuestU32(base, update_guest) + 0u)
                        : 0u,
        update_readable ? LoadGuestU32(base, LoadGuestU32(base, update_guest) + 4u)
                        : 0u,
        update_readable ? LoadGuestU32(base, LoadGuestU32(base, update_guest) + 8u)
                        : 0u,
        update_readable ? LoadGuestU32(base, LoadGuestU32(base, update_guest) + 12u)
                        : 0u,
        update_readable ? LoadGuestU32(base, update_guest + 4u) : 0u,
        update_readable ? LoadGuestU32(base, update_guest + 8u) : 0u,
        update_readable ? LoadGuestU32(base, update_guest + 12u) : 0u,
        update_readable ? LoadGuestU32(base, update_guest + 24u) : 0u,
        update_readable ? LoadGuestU32(base, update_guest + 56u) : 0u,
        update_readable ? LoadGuestU32(base, update_guest + 60u) : 0u,
        update_readable ? LoadGuestU32(base, update_guest + 84u) : 0u,
        update_readable ? LoadGuestU8(base, update_guest + 96u) : 0u,
        args.lr);
  }
  refii::renderer::OnRendererGuestHookExit("sub_82377F30", kind, args, ctx,
                                           base, hook_desc);
}

REX_EXTERN(sub_823781D8) {
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "sub_823781D8", refii::renderer::RendererHookKind::kGeneral);

  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  const auto begin = std::chrono::steady_clock::now();
  constexpr refii::renderer::RendererHookKind kind =
      refii::renderer::RendererHookKind::kGeneral;
  refii::renderer::OnRendererGuestHookEnter("sub_823781D8", kind, args, base, hook_desc);
  const uint32_t update_guest =
      IsReadableGuestByteRange(base, args.r3 + 12u, sizeof(uint32_t))
          ? LoadGuestU32(base, args.r3 + 12u)
          : 0u;
  const bool update_readable = IsReadableGuestByteRange(base, update_guest, 104u);
  const uint32_t frame_before =
      update_readable ? LoadGuestU32(base, update_guest + 4u) : 0u;
  const uint32_t active_movie_before =
      update_readable ? LoadGuestU32(base, update_guest + 8u) : 0u;
  const uint32_t movie_state_before =
      update_readable ? LoadGuestU32(base, update_guest + 12u) : 0u;
  const uint32_t pump_state_before =
      update_readable ? LoadGuestU32(base, update_guest + 24u) : 0u;
  const uint32_t index_before =
      update_readable ? LoadGuestU32(base, update_guest + 84u) : 0u;
  const uint8_t complete_before =
      update_readable ? LoadGuestU8(base, update_guest + 96u) : 0u;
  refii::renderer::RegisterFableStartupVideoPumpContext(base, ctx, args.r3,
                                                        update_guest);
  if (refii::renderer::ShouldRunGuestRendererHook("sub_823781D8", kind, args,
                                                  base, hook_desc)) {
    __imp__sub_823781D8(ctx, base);
    const int64_t elapsed_ms =
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - begin)
            .count();
    const bool update_after_readable =
        IsReadableGuestByteRange(base, update_guest, 104u);
    static std::atomic<uint32_t> startup_update_run_logs{0};
    const uint32_t count =
        startup_update_run_logs.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (count <= 16u || elapsed_ms >= 50 || (count % 120u) == 0u) {
      REXLOG_INFO(
          "Refii renderer startup video native update run #{} "
          "wrapper=0x{:08X} update=0x{:08X} result=0x{:08X} "
          "elapsed_ms={} frame={}->{} active_movie=0x{:08X}->0x{:08X} "
          "movie_state=0x{:08X}->0x{:08X} pump_state=0x{:08X}->0x{:08X} "
          "index={}->{} complete={}->{} lr=0x{:08X}",
          count,
          args.r3,
          update_guest,
          ctx.r3.u32,
          elapsed_ms,
          frame_before,
          update_after_readable ? LoadGuestU32(base, update_guest + 4u) : 0u,
          active_movie_before,
          update_after_readable ? LoadGuestU32(base, update_guest + 8u) : 0u,
          movie_state_before,
          update_after_readable ? LoadGuestU32(base, update_guest + 12u) : 0u,
          pump_state_before,
          update_after_readable ? LoadGuestU32(base, update_guest + 24u) : 0u,
          index_before,
          update_after_readable ? LoadGuestU32(base, update_guest + 84u) : 0u,
          complete_before,
          update_after_readable ? LoadGuestU8(base, update_guest + 96u) : 0u,
          args.lr);
    }
    refii::renderer::NotifyFableStartupVideoPumpRunComplete(update_guest);
    refii::renderer::OnRendererGuestHookExit("sub_823781D8", kind, args, ctx,
                                             base, hook_desc);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "sub_823781D8", kind, args, ctx, base, hook_desc);
  }
}

REX_EXTERN(sub_82A41F10) { // Startup Bink frame builder.
  REX_FUNC_PROLOGUE();

  const PPCContext preserved = ctx;
  const uint32_t builder = ctx.r3.u32;
  const StartupFrameBoundaryState before =
      ReadStartupFrameBoundaryState(base, builder);
  __imp__sub_82A41F10(ctx, base);
  const StartupFrameBoundaryState after_native =
      ReadStartupFrameBoundaryState(base, builder);
  LogStartupFrameBoundaryTransition("after-native", before, after_native);
  refii::renderer::RefreshBinkVideoUploadsFromRendererHook("sub_82A41F10",
                                                           base);
  const StartupFrameBoundaryState after_refresh =
      ReadStartupFrameBoundaryState(base, builder);
  LogStartupFrameBoundaryTransition("after-refresh", after_native,
                                    after_refresh);
  ctx.r21 = preserved.r21;
  ctx.r22 = preserved.r22;
  ctx.r23 = preserved.r23;
  ctx.r24 = preserved.r24;
  ctx.r25 = preserved.r25;
  ctx.r26 = preserved.r26;
  ctx.r27 = preserved.r27;
  ctx.r28 = preserved.r28;
  ctx.r29 = preserved.r29;
  ctx.r30 = preserved.r30;
  ctx.r31 = preserved.r31;
}

REX_EXTERN(sub_82A42D20) { // Startup Bink frame target builder.
  REX_FUNC_PROLOGUE();

  const PPCContext preserved = ctx;
  __imp__sub_82A42D20(ctx, base);
  refii::renderer::RefreshBinkVideoUploadsFromRendererHook("sub_82A42D20",
                                                           base);
  ctx.r22 = preserved.r22;
  ctx.r23 = preserved.r23;
  ctx.r24 = preserved.r24;
  ctx.r25 = preserved.r25;
  ctx.r26 = preserved.r26;
  ctx.r27 = preserved.r27;
  ctx.r28 = preserved.r28;
  ctx.r29 = preserved.r29;
  ctx.r30 = preserved.r30;
  ctx.r31 = preserved.r31;
}

REX_EXTERN(sub_82CFD4A8) { // Startup Bink system-version/video helper.
  REX_FUNC_PROLOGUE();

  const PPCContext preserved = ctx;
  __imp__sub_82CFD4A8(ctx, base);
  ctx.r28 = preserved.r28;
  ctx.r29 = preserved.r29;
  ctx.r30 = preserved.r30;
  ctx.r31 = preserved.r31;
}

// Binding-wrapper hooks REMOVED 2026-07-29 (sub_82208B10
// Render_SetPixelShaderFromBinding, sub_822102E0
// Render_SetVertexShaderConstantFromBinding, sub_8222C210
// Render_SetVertexShaderFromBinding): no observer case, no GTL case, no
// emulation — pure wrapper overhead (273k calls/run combined). Their inner
// D3D setters are separately hooked/mirrored, so nothing is lost.
REFII_RENDERER_STARTUP_BINK_STRING_DESTRUCT_HOOK(
    sub_82214DD8)                      // IDA: stack string release/end helper
REFII_RENDERER_STARTUP_BINK_STRING_CONSTRUCT_HOOK(
    sub_8222CED0) // IDA: stack string begin helper
REFII_RENDERER_DRAW_HOOK(
    sub_82A431A8) // IDA: startup Bink plane shader/resource bind
REFII_RENDERER_STARTUP_BINK_LOOKUP_HOOK(
    sub_82B843B8) // IDA: D3D state token lookup
REFII_RENDERER_STARTUP_BINK_LOOKUP_HOOK(
    sub_82B84460) // IDA: D3D state token lookup

REFII_RENDERER_RESOURCE_HOOK(
    sub_82A42FA0) // Fable Bink video plane surface setup
REFII_RENDERER_TIMED_STARTUP_BINK_GENERAL_HOOK(
    sub_832BB230) // Bink async frame ready probe
REFII_RENDERER_TIMED_STARTUP_BINK_GENERAL_HOOK(
    sub_832BB288) // Bink async frame kick
REFII_RENDERER_TIMED_STARTUP_BINK_GENERAL_HOOK(
    sub_832BB350) // Bink async handle release
REFII_RENDERER_TIMED_STARTUP_BINK_GENERAL_HOOK(
    sub_832BB378) // Bink async index ready probe
REFII_RENDERER_TIMED_STARTUP_BINK_GENERAL_HOOK(
    sub_832BC6C0) // Bink frame packet lookup
REFII_RENDERER_TIMED_STARTUP_BINK_GENERAL_HOOK(
    sub_832BC7F0) // Bink frame decode commit
REX_EXTERN(sub_832BC9E0) { // Bink plane bitstream copy/audio ring fill.
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "sub_832BC9E0", refii::renderer::RendererHookKind::kGeneral);
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  constexpr refii::renderer::RendererHookKind kind =
      refii::renderer::RendererHookKind::kGeneral;
  refii::renderer::OnRendererGuestHookEnter("sub_832BC9E0", kind, args, base, hook_desc);
  uint32_t before_write = 0;
  uint32_t before_read = 0;
  uint32_t before_fill = 0;
  if (base != nullptr && args.r3 != 0 &&
      IsReadableGuestByteRange(base, args.r3, 860u)) {
    const uint32_t plane_count = LoadGuestU32(base, args.r3 + 276u);
    const uint32_t plane_base = LoadGuestU32(base, args.r3 + 280u);
    if (args.r4 < plane_count && plane_base != 0) {
      const uint32_t plane_guest = plane_base + args.r4 * 388u;
      if (IsReadableGuestByteRange(base, plane_guest, 388u)) {
        before_write = LoadGuestU32(base, plane_guest + 0u);
        before_read = LoadGuestU32(base, plane_guest + 24u);
        before_fill = BinkRingFill(base, plane_guest);
      }
    }
  }
  if (refii::renderer::ShouldRunGuestRendererHook("sub_832BC9E0", kind, args,
                                                  base, hook_desc)) {
    const auto begin = StartupBinkTimingClock::now();
    __imp__sub_832BC9E0(ctx, base);
    const uint64_t elapsed_us =
        StartupBinkTimingUs(begin, StartupBinkTimingClock::now());
    LogStartupBinkTiming("sub_832BC9E0", args, ctx, elapsed_us);
    LogStartupBinkBitstreamCopyTrace(args, ctx, base, elapsed_us,
                                     before_write, before_read, before_fill);
    refii::renderer::OnRendererGuestHookExit("sub_832BC9E0", kind, args, ctx,
                                             base, hook_desc);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "sub_832BC9E0", kind, args, ctx, base, hook_desc);
  }
}
REX_EXTERN(sub_832BCB30) { // Bink frame decode/update
  REX_FUNC_PROLOGUE();
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsLight(ctx);
  const uint8_t failed_before = StartupVideoOwnerFailed(base);
  __imp__sub_832BCB30(ctx, base);
  refii::renderer::RefreshBinkVideoUploadsFromRendererHook("sub_832BCB30",
                                                           args, base);
  LogStartupVideoOwnerFailureStage("sub_832BCB30", args.r3, failed_before,
                                   base);
}
REX_EXTERN(sub_832BD018) { // Bink frame advance
  REX_FUNC_PROLOGUE();
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsLight(ctx);
  const uint8_t failed_before = StartupVideoOwnerFailed(base);
  __imp__sub_832BD018(ctx, base);
  LogStartupVideoOwnerFailureStage("sub_832BD018", args.r3, failed_before,
                                   base);
}
REX_EXTERN(sub_832BC598) { // Bink pre-render catch-up gate.
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "sub_832BC598", refii::renderer::RendererHookKind::kGeneral);
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  constexpr refii::renderer::RendererHookKind kind =
      refii::renderer::RendererHookKind::kGeneral;
  refii::renderer::OnRendererGuestHookEnter("sub_832BC598", kind, args, base, hook_desc);
  const uint8_t failed_before = StartupVideoOwnerFailed(base);
  if (BinkPlumeOwnsMovie(base, args.r3)) {
    const bool readable =
        base != nullptr && args.r3 != 0 &&
        IsReadableGuestByteRange(base, args.r3, 836u);
    if (readable) {
      SyncPlumeOwnedBinkMovieClock(ctx, base, args.r3);
    }
    __imp__sub_832BC598(ctx, base);
    if (readable) {
      SyncPlumeOwnedBinkMovieClock(ctx, base, args.r3);
    }
  } else if (refii::renderer::ShouldRunGuestRendererHook(
                 "sub_832BC598", kind, args, base, hook_desc)) {
    __imp__sub_832BC598(ctx, base);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "sub_832BC598", kind, args, ctx, base, hook_desc);
  }
  refii::renderer::OnRendererGuestHookExit("sub_832BC598", kind, args, ctx,
                                           base, hook_desc);
  LogStartupVideoOwnerFailureStage("sub_832BC598", args.r3, failed_before,
                                   base);
}
REFII_RENDERER_TIMED_STARTUP_BINK_UPLOAD_HOOK(
    sub_832BCDE8) // Bink async frame file read
REFII_RENDERER_TIMED_STARTUP_BINK_GENERAL_HOOK(
    sub_832BFFF8) // Bink synchronous file read
REFII_RENDERER_TIMED_STARTUP_BINK_UPLOAD_HOOK(
    sub_832C01D0) // Bink frame file read
REFII_RENDERER_TIMED_STARTUP_BINK_GENERAL_HOOK(
    sub_832C0578) // Bink file reader buffer size
REFII_RENDERER_TIMED_STARTUP_BINK_GENERAL_HOOK(
    sub_832C05A0) // Bink file reader buffer setup
REFII_RENDERER_TIMED_STARTUP_BINK_GENERAL_HOOK(
    sub_832C0630) // Bink file reader close
REFII_RENDERER_TIMED_STARTUP_BINK_GENERAL_HOOK(
    sub_832C0698) // Bink file reader async pump
REFII_RENDERER_TIMED_STARTUP_BINK_GENERAL_HOOK(
    sub_832C0900) // Bink file reader pause/resume
REFII_RENDERER_TIMED_STARTUP_BINK_UPLOAD_HOOK(
    sub_832C49E8)          // Bink ExpandBink frame decode
REX_EXTERN(sub_832BAD00) { // Bink async buffer drain
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "sub_832BAD00", refii::renderer::RendererHookKind::kGeneral);
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  constexpr refii::renderer::RendererHookKind kind =
      refii::renderer::RendererHookKind::kGeneral;
  refii::renderer::OnRendererGuestHookEnter("sub_832BAD00", kind, args, base, hook_desc);
  const auto begin = StartupBinkTimingClock::now();
  const bool plume_owned = BinkPlumeOwnsMovie(base, args.r3);
  if (plume_owned) {
    const bool drained = TryRunBinkNativeAudioDrain(args, ctx, base);
    if (!drained) {
      ctx.r3.u32 = 0;
    }
    if (drained) {
      const uint32_t drain_count =
          g_bink_plume_direct_drain_skips.fetch_add(
              1, std::memory_order_relaxed) +
          1u;
      if (drain_count <= 8u || drain_count % 240u == 0u) {
        REXLOG_INFO(
            "Refii renderer BIK direct drain served by Plume pump #{} "
            "lr=0x{:08X} movie=0x{:08X}",
            drain_count, args.lr, args.r3);
      }
    }
  } else if (refii::renderer::ShouldRunGuestRendererHook("sub_832BAD00", kind,
                                                         args, base, hook_desc)) {
    __imp__sub_832BAD00(ctx, base);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "sub_832BAD00", kind, args, ctx, base, hook_desc);
  }
  const uint64_t elapsed_us =
      StartupBinkTimingUs(begin, StartupBinkTimingClock::now());
  LogStartupBinkTiming(plume_owned ? "sub_832BAD00.plume"
                                   : "sub_832BAD00.native",
                       args, ctx, elapsed_us);
  LogStartupBinkDrainState(args, ctx, base, elapsed_us);
  refii::renderer::OnRendererGuestHookExit("sub_832BAD00", kind, args, ctx,
                                           base, hook_desc);
}
REX_EXTERN(sub_832BB370) { // Bink async sleep callback.
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "sub_832BB370", refii::renderer::RendererHookKind::kGeneral);
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  constexpr refii::renderer::RendererHookKind kind =
      refii::renderer::RendererHookKind::kGeneral;
  refii::renderer::OnRendererGuestHookEnter("sub_832BB370", kind, args, base, hook_desc);
  if (refii::renderer::ShouldRunGuestRendererHook("sub_832BB370", kind, args,
                                                  base, hook_desc)) {
    __imp__sub_832BB370(ctx, base);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "sub_832BB370", kind, args, ctx, base, hook_desc);
  }
  refii::renderer::OnRendererGuestHookExit("sub_832BB370", kind, args, ctx,
                                           base, hook_desc);
}
REX_EXTERN(sub_832BB390) { // IDA: Bink sound/async index commit callback
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "sub_832BB390", refii::renderer::RendererHookKind::kGeneral);
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  constexpr refii::renderer::RendererHookKind kind =
      refii::renderer::RendererHookKind::kGeneral;
  refii::renderer::OnRendererGuestHookEnter("sub_832BB390", kind, args, base, hook_desc);
  const auto begin = StartupBinkTimingClock::now();
  const uint32_t movie_guest = args.r3 >= 852u ? args.r3 - 852u : 0u;
  const bool plume_owned = BinkPlumeOwnsMovie(base, movie_guest);
  if (plume_owned && BinkMovieFinished(base, movie_guest)) {
    refii::renderer::NotifyFableStartupVideoBinkMovieFinished(base,
                                                              movie_guest);
    StoreGuestU32(base, movie_guest + 848u, args.r4);
    ClearMovie(movie_guest);
    ClearStartupAudioCursorsForMovie(base, movie_guest);
    ctx.r3.u32 = args.r3;
  } else if (plume_owned) {
    refii::renderer::RendererHookArgs drain_args = args;
    drain_args.r3 = movie_guest;
    TryRunBinkNativeAudioDrain(drain_args, ctx, base);
    StoreGuestU32(base, movie_guest + 848u, args.r4);
    ctx.r3.u32 = args.r3;
  } else if (refii::renderer::ShouldRunGuestRendererHook(
                 "sub_832BB390", kind, args, base, hook_desc)) {
    __imp__sub_832BB390(ctx, base);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "sub_832BB390", kind, args, ctx, base, hook_desc);
  }
  const uint64_t elapsed_us =
      StartupBinkTimingUs(begin, StartupBinkTimingClock::now());
  LogStartupBinkTiming(plume_owned ? "sub_832BB390.plume"
                                   : "sub_832BB390.native",
                       args, ctx, elapsed_us);
  LogStartupBinkSoundCallbackTrace(
      plume_owned ? "sub_832BB390.plume" : "sub_832BB390.native",
      args, ctx, base);
  refii::renderer::OnRendererGuestHookExit("sub_832BB390", kind, args, ctx,
                                           base, hook_desc);
}
REX_EXTERN(sub_832BD710) { // IDA: BinkWait
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "sub_832BD710", refii::renderer::RendererHookKind::kGeneral);
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  constexpr refii::renderer::RendererHookKind kind =
      refii::renderer::RendererHookKind::kGeneral;
  refii::renderer::OnRendererGuestHookEnter("sub_832BD710", kind, args, base, hook_desc);
  LogStartupBinkMovieState("wait-enter", args, ctx, base);
  const uint8_t failed_before = StartupVideoOwnerFailed(base);
  const bool plume_owned = BinkPlumeOwnsMovie(base, args.r3);
  if (plume_owned) {
    TryRunBinkNativeAudioDrain(args, ctx, base);
    // BinkWait's result is control flow for the caller: nonzero means the
    // current frame is still on time and the startup update must yield.  The
    // native wait itself can block forever on the emulated async/fence state,
    // so Plume owns this timing decision.  A hardcoded zero is also wrong: it
    // lets one update invocation decode through the whole movie.  Observe the
    // real native frame number and open one decode step only when its Plume
    // movie-clock deadline has arrived.
    const uint32_t frame = LoadGuestU32(base, args.r3 + 12u);
    const auto interval =
        refii::renderer::bink::StartupBinkMovieFrameInterval(base, args.r3);
    const auto pace =
        refii::renderer::bink::ObserveFrame(args.r3, frame, interval);
    const auto before_wait = refii::renderer::bink::MovieClock::now();
    const bool wait_for_deadline =
        (pace.wait_requested || pace.frame_advanced) &&
        pace.sleep_until !=
            refii::renderer::bink::MovieClock::time_point{} &&
        pace.sleep_until > before_wait;
    if (wait_for_deadline) {
      std::this_thread::sleep_until(pace.sleep_until);
    }
    const auto after_wait = refii::renderer::bink::MovieClock::now();
    if (pace.frame_advanced) {
      refii::renderer::bink::CommitFrame(args.r3, frame, pace.frame_delta,
                                         interval, after_wait, pace.lateness);
    }
    // The deadline has now been honored, so permit exactly one decode step.
    // Returning one here only makes this game's caller spin on BinkWait in the
    // same update invocation; it does not yield back to the present pump.
    ctx.r3.u32 = 0u;
    const uint32_t wait_count =
        g_bink_plume_clock_waits.fetch_add(
            1, std::memory_order_relaxed) +
        1u;
    if (wait_count <= 8u || wait_count % 240u == 0u) {
      REXLOG_INFO(
          "Refii renderer BIK wait preserved Plume cadence for Plume-owned "
          "frame #{} lr=0x{:08X} movie=0x{:08X} slept={}",
          wait_count, args.lr, args.r3, wait_for_deadline);
    }
  } else if (refii::renderer::ShouldRunGuestRendererHook("sub_832BD710", kind,
                                                         args, base, hook_desc)) {
    __imp__sub_832BD710(ctx, base);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "sub_832BD710", kind, args, ctx, base, hook_desc);
  }
  if (plume_owned && BinkMovieFinished(base, args.r3)) {
    refii::renderer::NotifyFableStartupVideoBinkMovieFinished(base, args.r3);
    ClearMovie(args.r3);
    ClearStartupAudioCursorsForMovie(base, args.r3);
  }
  LogStartupBinkMovieState("wait-exit", args, ctx, base);
  LogStartupVideoOwnerFailureStage("sub_832BD710", args.r3, failed_before,
                                   base);
  refii::renderer::OnRendererGuestHookExit("sub_832BD710", kind, args, ctx,
                                           base, hook_desc);
}
REX_EXTERN(sub_832BDA40) { // Lumen: BinkSetVolume
  REX_FUNC_PROLOGUE();
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsLight(ctx);
  __imp__sub_832BDA40(ctx, base);
  LogStartupBinkAudioTrace("sub_832BDA40.BinkSetVolume", args, ctx, base);
}
REX_EXTERN(sub_82CD9650) { // XAudio voice cursor query used by Bink audio.
  REX_FUNC_PROLOGUE();
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsLight(ctx);
  __imp__sub_82CD9650(ctx, base);
  refii::renderer::bink::TryBridgeStartupAudioCursor(args, ctx, base);
}
REX_EXTERN(sub_832BDAD0) { // Lumen: BinkSetSoundOnOff
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "sub_832BDAD0", refii::renderer::RendererHookKind::kGeneral);
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  constexpr refii::renderer::RendererHookKind kind =
      refii::renderer::RendererHookKind::kGeneral;
  refii::renderer::OnRendererGuestHookEnter("sub_832BDAD0", kind, args, base, hook_desc);
  const auto begin = StartupBinkTimingClock::now();
  const bool plume_owned = BinkPlumeOwnsMovie(base, args.r3);
  const bool movie_readable =
      IsReadableGuestByteRange(base, args.r3, kStartupBinkMovieBytes);
  const uint32_t current_frame =
      movie_readable ? LoadGuestU32(base, args.r3 + 12u) : 0u;
  const uint32_t total_frames =
      movie_readable ? LoadGuestU32(base, args.r3 + 8u) : 0u;
  const uint32_t plane_count =
      movie_readable ? LoadGuestU32(base, args.r3 + 276u) : 0u;
  const uint32_t plane_guest =
      movie_readable ? LoadGuestU32(base, args.r3 + 280u) : 0u;
  const bool plane_readable =
      plane_guest != 0 &&
      IsReadableGuestByteRange(base, plane_guest, kStartupBinkPlaneBytes);
  if (refii::renderer::ShouldRunGuestRendererHook("sub_832BDAD0", kind, args,
                                                  base, hook_desc)) {
    __imp__sub_832BDAD0(ctx, base);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "sub_832BDAD0", kind, args, ctx, base, hook_desc);
  }
  const uint64_t elapsed_us =
      StartupBinkTimingUs(begin, StartupBinkTimingClock::now());
  LogStartupBinkTiming(plume_owned ? "sub_832BDAD0.plume"
                                   : "sub_832BDAD0.native",
                       args, ctx, elapsed_us);
  if (plume_owned && (elapsed_us >= 16000u ||
                      refii::renderer::RendererVerboseTracingEnabled())) {
    REXLOG_INFO(
        "Refii BIK sound-toggle detail lr=0x{:08X} movie=0x{:08X} "
        "request={} result=0x{:08X} elapsed_us={} "
        "frame={}/{} planes={} p0=0x{:08X} enabled={} gate={} hold={} "
        "out_read={} out_pending={} out_cursor={} voice=0x{:08X}",
        args.lr, args.r3, args.r4, ctx.r3.u32, elapsed_us,
        current_frame, total_frames,
        plane_count, plane_guest,
        plane_readable ? LoadGuestU32(base, plane_guest + 56u) : 0u,
        plane_readable ? LoadGuestU32(base, plane_guest + 208u) : 0u,
        plane_readable ? LoadGuestU32(base, plane_guest + 212u) : 0u,
        plane_readable ? LoadGuestU32(base, plane_guest + 216u) : 0u,
        plane_readable ? LoadGuestU32(base, plane_guest + 220u) : 0u,
        plane_readable ? LoadGuestU32(base, plane_guest + 228u) : 0u,
        plane_readable ? LoadGuestU32(base, plane_guest + 92u) : 0u);
  }
  refii::renderer::OnRendererGuestHookExit("sub_832BDAD0", kind, args, ctx,
                                           base, hook_desc);
}
REX_EXTERN(sub_832BED88) { // IDA: RAD callback queue register
  REX_FUNC_PROLOGUE();
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsLight(ctx);
  __imp__sub_832BED88(ctx, base);
  LogStartupBinkCallbackTrace("sub_832BED88.register", args, ctx, base);
}
REX_EXTERN(sub_832BFEC8) { // Lumen: BinkAudioDecompress/window unpack.
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "sub_832BFEC8", refii::renderer::RendererHookKind::kGeneral);
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  constexpr refii::renderer::RendererHookKind kind =
      refii::renderer::RendererHookKind::kGeneral;
  refii::renderer::OnRendererGuestHookEnter("sub_832BFEC8", kind, args, base, hook_desc);
  if (refii::renderer::ShouldRunGuestRendererHook("sub_832BFEC8", kind, args,
                                                  base, hook_desc)) {
    __imp__sub_832BFEC8(ctx, base);
    LogStartupBinkAudioTrace("sub_832BFEC8.BinkAudioDecompress", args, ctx,
                             base);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "sub_832BFEC8", kind, args, ctx, base, hook_desc);
  }
  refii::renderer::OnRendererGuestHookExit("sub_832BFEC8", kind, args, ctx,
                                           base, hook_desc);
}
REX_EXTERN(sub_832BE980) { // IDA: Bink submit/readiness wait helper
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "sub_832BE980", refii::renderer::RendererHookKind::kGeneral);
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  constexpr refii::renderer::RendererHookKind kind =
      refii::renderer::RendererHookKind::kGeneral;
  refii::renderer::OnRendererGuestHookEnter("sub_832BE980", kind, args, base, hook_desc);
  if (refii::renderer::ShouldRunGuestRendererHook("sub_832BE980", kind, args,
                                                  base, hook_desc)) {
    __imp__sub_832BE980(ctx, base);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "sub_832BE980", kind, args, ctx, base, hook_desc);
  }
  refii::renderer::OnRendererGuestHookExit("sub_832BE980", kind, args, ctx,
                                           base, hook_desc);
}
