#include "renderer_hooks_private.h"

#include "runtime/cvars.h"
#include "runtime/hook_observer.h"
// For the critical-section ownership check in
// FableCriticalSectionRecursionDepth.
#include "rex/system/xthread.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstring>
#include <mutex>
#include <string_view>
#include <unordered_map>
#include <unordered_set>

namespace {

constexpr uint32_t kFableRendererFrameCriticalSection = 0x8349F7FCu;

thread_local uint32_t t_fable_render_frame_lock = 0;

uint32_t GuestPhysicalHostOffset(uint32_t guest_address) {
#if defined(_WIN32)
  return guest_address >= 0xE0000000u ? 0x1000u : 0u;
#else
  (void)guest_address;
  return 0;
#endif
}

bool IsReadableGuestByteRange(uint8_t *base, uint32_t guest_address,
                              uint32_t byte_size) {
  if (base == nullptr || guest_address == 0 || byte_size == 0 ||
      guest_address > UINT32_MAX - (byte_size - 1u)) {
    return false;
  }

  (void)*(volatile uint8_t *)(base + guest_address +
                              GuestPhysicalHostOffset(guest_address));
  (void)*(volatile uint8_t *)(base + guest_address + byte_size - 1u +
                              GuestPhysicalHostOffset(guest_address));
  return true;
}

uint8_t LoadGuestU8(uint8_t *base, uint32_t guest_address) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint8_t))) {
    return 0;
  }
  return *(base + guest_address + GuestPhysicalHostOffset(guest_address));
}

uint32_t LoadGuestU32(uint8_t *base, uint32_t guest_address) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint32_t))) {
    return 0;
  }

  uint32_t raw = 0;
  std::memcpy(&raw,
              base + guest_address + GuestPhysicalHostOffset(guest_address),
              sizeof(raw));
  return __builtin_bswap32(raw);
}

// Counterpart to LoadGuestU32 above; this TU had a load but no store. Used by
// the native-render-resolution hook to rewrite the device descriptor.
bool StoreGuestU32(uint8_t *base, uint32_t guest_address, uint32_t value) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint32_t))) {
    return false;
  }

  const uint32_t raw = __builtin_bswap32(value);
  std::memcpy(base + guest_address + GuestPhysicalHostOffset(guest_address),
              &raw, sizeof(raw));
  return true;
}

uint32_t LoadReadableGuestU32(uint8_t *base, uint32_t guest_address) {
  return IsReadableGuestByteRange(base, guest_address, sizeof(uint32_t))
             ? LoadGuestU32(base, guest_address)
             : 0u;
}

void LogStartupTitleCallbackState(std::string_view symbol,
                                  const refii::renderer::RendererHookArgs &args,
                                  const PPCContext &ctx, uint8_t *base) {
  if (!refii::renderer::RendererDeepObserverHooksEnabled()) {
    return;
  }
  if (!refii::renderer::FableStartupVideoPumpCompletedForHandoff()) {
    return;
  }

  static std::atomic<uint32_t> samples{0};
  const uint32_t count = samples.fetch_add(1, std::memory_order_relaxed) + 1u;
  if (count > 48u && (count % 120u) != 0u) {
    return;
  }

  auto word = [&](uint32_t guest, uint32_t offset) -> uint32_t {
    return LoadReadableGuestU32(base, guest + offset);
  };

  uint32_t detail0 = 0;
  uint32_t detail1 = 0;
  uint32_t detail2 = 0;
  uint32_t detail3 = 0;
  uint32_t detail4 = 0;
  uint32_t detail5 = 0;
  if (symbol == "sub_821D4F60") {
    const uint32_t list_guest = args.r3 + 3412u;
    const uint32_t list_begin = word(list_guest, 24u);
    const uint32_t list_end = word(list_guest, 28u);
    const uint32_t high_mask = args.r4 & 0xFF80u;
    uint32_t first_object = 0;
    uint32_t first_callback = 0;
    if (high_mask != 0u && list_end >= list_begin &&
        (list_end - list_begin) <= 24u * 256u) {
      for (uint32_t entry = list_begin; entry < list_end; entry += 24u) {
        if ((word(entry, 4u) & high_mask) == 0u) {
          continue;
        }
        const uint32_t objects_begin = word(entry, 12u);
        const uint32_t objects_end = word(entry, 16u);
        if (objects_begin == 0u || objects_end <= objects_begin ||
            objects_end - objects_begin > 4u * 4096u) {
          continue;
        }
        first_object = LoadReadableGuestU32(base, objects_begin);
        const uint32_t first_vtable = word(first_object, 0u);
        first_callback = word(first_vtable, 8u);
        break;
      }
    }
    detail0 = args.r4 & 0x7Fu;
    detail1 = high_mask;
    detail2 = list_begin;
    detail3 = list_end;
    detail4 = first_object;
    detail5 = first_callback;
  } else if (symbol == "sub_822975F8") {
    const uint32_t owner = word(args.r3, 4u);
    const uint32_t table_owner = word(owner, 20u);
    const uint32_t table = word(table_owner, 3676u);
    detail0 = owner;
    detail1 = table_owner;
    detail2 = table;
    detail3 = table != 0u ? table + 96u : 0u;
    for (uint32_t i = 0; i < 16u && detail3 != 0u; ++i) {
      detail4 += word(detail3, i * 448u) != 0u ? 1u : 0u;
    }
    detail5 = detail3 != 0u ? word(detail3, 0u) : 0u;
  } else if (symbol == "sub_821D9C98") {
    const uint32_t registry = args.r5 + 3412u;
    const uint32_t registry_begin = word(registry, 24u);
    const uint32_t registry_end = word(registry, 28u);
    detail0 = args.r3;
    detail1 = args.r4;
    detail2 = args.r5;
    detail3 = word(args.r3, 16u);
    detail4 = registry_begin;
    detail5 = registry_end;
  } else if (symbol == "sub_82297698") {
    detail0 = word(args.r4, 72u);
    detail1 = word(args.r4, 84u);
    detail2 = word(args.r4, 88u);
    detail3 = word(args.r4, 92u);
    detail4 = word(args.r4, 96u);
    detail5 = word(args.r4, 100u);
  } else if (symbol == "sub_821CB498") {
    const uint32_t owner = word(args.r3, 0u);
    detail0 = owner;
    detail1 = word(args.r3, 4u);
    detail2 = word(args.r3, 8u);
    detail3 = word(args.r4, 72u);
    detail4 = owner != 0u ? LoadGuestU8(base, owner + 1800u) : 0u;
    detail5 = word(args.r3, 32u);
  } else if (symbol == "sub_82231ED8") {
    const uint32_t owner = word(args.r3, 0u);
    detail0 = owner;
    detail1 = word(args.r3, 4u);
    detail2 = word(args.r3, 8u);
    detail3 = word(args.r6, 16u);
    detail4 = word(detail3, 28u);
    detail5 = owner != 0u ? LoadGuestU8(base, owner + 1801u) : 0u;
  } else if (symbol == "sub_82192028") {
    const uint32_t object = args.r4;
    const uint32_t vtable = word(object, 0u);
    detail0 = object;
    detail1 = vtable;
    detail2 = word(vtable, 8u);
    detail3 = word(vtable, 12u);
    detail4 = word(object, 28u);
    detail5 = word(object, 72u);
  } else if (symbol == "sub_821A4ED0") {
    const uint32_t object = args.r3;
    const uint32_t list_owner = args.r4;
    const uint32_t vtable = word(object, 0u);
    detail0 = object;
    detail1 = list_owner;
    detail2 = vtable;
    detail3 = word(vtable, 4u);
    detail4 = word(object, 84u);
    detail5 = word(list_owner, 0u);
  } else if (symbol == "sub_8222C2B0") {
    const uint32_t list = args.r3;
    const uint32_t render_object = args.r4;
    const uint32_t vtable = word(render_object, 0u);
    detail0 = list;
    detail1 = render_object;
    detail2 = vtable;
    detail3 = word(vtable, 4u);
    detail4 = word(render_object, 20u);
    detail5 = args.r5;
  } else if (symbol == "sub_822749E0" || symbol == "sub_82A679C0" ||
             symbol == "sub_82A73DC8") {
    const uint32_t render_object = args.r3;
    const uint32_t callback_context = args.r4;
    const uint32_t vtable = word(render_object, 0u);
    detail0 = render_object;
    detail1 = callback_context;
    detail2 = vtable;
    detail3 = word(vtable, 8u);
    detail4 = word(render_object, 176u);
    detail5 = word(render_object, 180u);
  } else if (symbol == "sub_82B285F8") {
    const uint32_t world_registry = args.r5 + 3412u;
    detail0 = args.r3;
    detail1 = args.r4;
    detail2 = args.r5;
    detail3 = word(world_registry, 24u);
    detail4 = word(world_registry, 28u);
    detail5 = 26u;
  } else if (symbol == "sub_82A657C0") {
    const uint32_t primary_resource = word(args.r3, 1444u);
    const uint32_t secondary_resource = word(args.r3, 1448u);
    detail0 = args.r3;
    detail1 = word(args.r3, 8u);
    detail2 = word(args.r3, 624u);
    detail3 = primary_resource != 0u ? word(primary_resource, 84u) : 0u;
    detail4 = secondary_resource != 0u ? word(secondary_resource, 84u) : 0u;
    detail5 = LoadGuestU8(base, args.r3 + 1144u);
  }

  REXLOG_INFO(
      "Refii renderer startup title callback state #{} symbol={} "
      "lr=0x{:08X} args=0x{:08X}/0x{:08X}/0x{:08X}/0x{:08X}/0x{:08X}/0x{:08X} "
      "result=0x{:08X} detail=0x{:08X}/0x{:08X}/0x{:08X}/0x{:08X}/0x{:08X}/0x{:08X}",
      count, symbol, args.lr, args.r3, args.r4, args.r5, args.r6, args.r7,
      args.r8, ctx.r3.u32, detail0, detail1, detail2, detail3, detail4,
      detail5);
}

bool ShouldReleaseFableRendererFrameCriticalSectionDuringSceneStreamWait(
    const refii::renderer::RendererHookArgs &args) {
  if (!refii::renderer::RendererPlumeOwnsCommandSubmissionEnabled()) {
    return false;
  }

  // WAS A GUEST-ADDRESS WHITELIST (removed 2026-07-28). It used to require the
  // return address to equal one of two hard-coded call sites (0x8222A6C8 /
  // 0x82A60814).
  //
  // MEASURED RESULT: zero unlisted callers in a full run — the whitelist was
  // COMPLETE, so this change is behaviourally a no-op and the lock inversion it
  // guards against was NOT the menu bottleneck. Kept anyway because the rule is
  // semantic: if we are about to BLOCK we must not hold a lock the unblocking
  // party needs, and that is true of every caller. Guest-address whitelists are
  // banned in this project precisely because they silently miss cases.
  // so the release only fired for TWO hard-coded call sites. Any other caller
  // entered the wait still holding stru_8349F7FC — which is the deadlock this
  // whole mechanism exists to prevent.
  //
  // Why that is fatal (IDA, sub_82388380 @0x8238852C-0x82388540): the render
  // thread takes the GPU-access lock, calls sub_821C3858 for the ENTIRE frame,
  // then releases. sub_822DFE08's wait for the producer runs INSIDE that lock,
  // and the simulation thread needs the same lock to publish (every
  // Render_GpuAccessLock_Enter/Leave pair emits a publish). So the consumer
  // blocks on the producer while holding the lock the producer needs: producer
  // cannot publish -> consumer times out -> returns without updating -> the
  // whole pass re-runs. Self-sustaining, and it matches the measured ~71% of
  // menu wall time sitting outside our wrappers in a guest-side critical
  // section wait.
  //
  // The correct rule is semantic, not positional: if we are about to BLOCK, we
  // must not hold a lock the unblocking party needs. That is true for every
  // caller, so the caller identity is irrelevant. Whitelisting guest addresses
  // is exactly the failure mode this project bans.
  //
  // The two known callers are still recognised, but only to LOG anything
  // outside them — so we can see what the whitelist was missing rather than
  // assume.
  if (args.r3 == 0) {
    return false;
  }
  return true;
}

uint32_t FableRenderFrameLockDuringSceneStreamWait(
    const refii::renderer::RendererHookArgs &args) {
  if (!ShouldReleaseFableRendererFrameCriticalSectionDuringSceneStreamWait(
          args)) {
    return 0;
  }
  return t_fable_render_frame_lock;
}

uint32_t FableCriticalSectionRecursionDepth(uint8_t *base,
                                            uint32_t critical_section_guest) {
  // X_RTL_CRITICAL_SECTION: +0x00 dispatch header (16B), +0x10 lock_count,
  // +0x14 recursion_count, +0x18 owning_thread.
  constexpr uint32_t kCriticalSectionRecursionOffset = 0x14u;
  constexpr uint32_t kCriticalSectionOwningThreadOffset = 0x18u;
  if (base == nullptr || critical_section_guest == 0 ||
      !IsReadableGuestByteRange(base, critical_section_guest + 0x10u, 12u)) {
    return 0;
  }

  // OWNERSHIP CHECK (added 2026-07-28). This previously returned the recursion
  // count WITHOUT checking who owns the section, so a caller that did not hold
  // it would read another thread's depth and the release loop would drop a lock
  // it never took — corrupting the section for its real owner. That was latent
  // while the only user was sub_8222A5D8 (always called on the owning thread);
  // it becomes a live hazard now that sub_822DFE08 uses the same helper.
  const uint32_t owning_thread =
      LoadGuestU32(base, critical_section_guest +
                             kCriticalSectionOwningThreadOffset);
  if (owning_thread == 0 ||
      owning_thread != rex::system::XThread::GetCurrentThreadId()) {
    return 0;
  }

  return std::min<uint32_t>(
      LoadGuestU32(base,
                   critical_section_guest + kCriticalSectionRecursionOffset),
      16u);
}

uint32_t ReleaseFableCriticalSectionForSceneStreamWait(
    PPCContext &ctx, uint8_t *base, uint32_t critical_section_guest) {
  const uint32_t release_count =
      FableCriticalSectionRecursionDepth(base, critical_section_guest);
  for (uint32_t i = 0; i < release_count; ++i) {
    PPCContext critical_ctx = ctx;
    critical_ctx.r3.u32 = critical_section_guest;
    __imp__RtlLeaveCriticalSection(critical_ctx, base);
  }
  return release_count;
}

void ReacquireFableCriticalSectionForSceneStreamWait(
    PPCContext &ctx, uint8_t *base, uint32_t critical_section_guest,
    uint32_t acquire_count) {
  for (uint32_t i = 0; i < acquire_count; ++i) {
    PPCContext critical_ctx = ctx;
    critical_ctx.r3.u32 = critical_section_guest;
    __imp__RtlEnterCriticalSection(critical_ctx, base);
  }
}

} // namespace

// Per-call-site descriptor: the symbol is a compile-time literal, so its
// descriptor is resolved exactly once (function-local static) and every
// enter/decision/exit below dispatches on cached integers instead of
// re-deriving identity from the string on each of the ~3,000 hooked calls
// per title frame. See FableHookSymbolDesc in renderer_hook_targets.h.
#define REFII_RENDERER_PASSTHROUGH_HOOK_AS(wrapper_symbol, imp_symbol, kind) \
  REX_EXTERN(wrapper_symbol) {                                               \
    REX_FUNC_PROLOGUE();                                                     \
    static const refii::renderer::FableHookSymbolDesc *hook_desc =           \
        refii::renderer::ResolveFableHookSymbolDesc(#wrapper_symbol, kind);  \
    const refii::renderer::RendererHookArgs args =                           \
        refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);         \
    refii::renderer::OnRendererGuestHookEnter(#wrapper_symbol, kind, args,   \
                                              base, hook_desc);              \
    if (refii::renderer::ShouldRunGuestRendererHook(                         \
            #wrapper_symbol, kind, args, base, hook_desc)) {                 \
      __imp__##imp_symbol(ctx, base);                                        \
      refii::renderer::OnRendererGuestHookExit(#wrapper_symbol, kind, args,  \
                                               ctx, base, hook_desc);        \
    } else {                                                                 \
      refii::renderer::OnRendererGuestHookSuppressedForPlume(                \
          #wrapper_symbol, kind, args, ctx, base, hook_desc);                \
    }                                                                        \
  }

#define REFII_RENDERER_GENERAL_HOOK(symbol)      \
  REFII_RENDERER_PASSTHROUGH_HOOK_AS(            \
      symbol, symbol,                             \
      refii::renderer::RendererHookKind::kGeneral)

#define REFII_RENDERER_RESOURCE_HOOK(symbol)     \
  REFII_RENDERER_PASSTHROUGH_HOOK_AS(            \
      symbol, symbol,                             \
      refii::renderer::RendererHookKind::kResource)

#define REFII_RENDERER_DRAW_HOOK(symbol)         \
  REFII_RENDERER_PASSTHROUGH_HOOK_AS(            \
      symbol, symbol,                             \
      refii::renderer::RendererHookKind::kDraw)

#define REFII_RENDERER_TITLE_DRAW_HOOK_BODY(symbol)                     \
    constexpr refii::renderer::RendererHookKind kind =                  \
        refii::renderer::RendererHookKind::kDraw;                       \
    static const refii::renderer::FableHookSymbolDesc *hook_desc =      \
        refii::renderer::ResolveFableHookSymbolDesc(#symbol, kind);     \
    const refii::renderer::RendererHookArgs args =                      \
        refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);    \
    refii::renderer::OnRendererGuestHookEnter(#symbol, kind, args,      \
                                              base, hook_desc);         \
    if (refii::renderer::ShouldRunGuestRendererHook(#symbol, kind,      \
                                                    args, base,         \
                                                    hook_desc)) {       \
      __imp__##symbol(ctx, base);                                       \
      LogStartupTitleCallbackState(#symbol, args, ctx, base);           \
      refii::renderer::OnRendererGuestHookExit(#symbol, kind, args,     \
                                               ctx, base, hook_desc);   \
    } else {                                                            \
      refii::renderer::OnRendererGuestHookSuppressedForPlume(           \
          #symbol, kind, args, ctx, base, hook_desc);                   \
    }

#define REFII_RENDERER_TITLE_DRAW_HOOK(symbol)                          \
  REX_EXTERN(symbol) {                                                  \
    REX_FUNC_PROLOGUE();                                                \
    REFII_RENDERER_TITLE_DRAW_HOOK_BODY(symbol)                         \
  }

// The four PURE scene-scope markers (title audit 2026-07-29): under the
// shipping hook policy their entire observable effect is the GTL
// scene_submit_depth ++/-- pair, so the wrapper short-circuits to exactly
// that. BeginFableSceneScopeMarkerFastPath returns false in every diagnostic
// configuration (deep observer, telemetry, tracing, non-Plume backend) and
// the full title-draw path below then runs unchanged. See the static proof at
// its definition in renderer_runtime.cpp.
#define REFII_RENDERER_SCENE_SCOPE_TITLE_DRAW_HOOK(symbol)              \
  REX_EXTERN(symbol) {                                                  \
    REX_FUNC_PROLOGUE();                                                \
    static const refii::renderer::FableHookSymbolDesc *scene_hook_desc = \
        refii::renderer::ResolveFableHookSymbolDesc(                    \
            #symbol, refii::renderer::RendererHookKind::kDraw);         \
    if (refii::renderer::BeginFableSceneScopeMarkerFastPath(            \
            #symbol, base, scene_hook_desc)) {                          \
      __imp__##symbol(ctx, base);                                       \
      refii::renderer::EndFableSceneScopeMarkerFastPath();              \
      return;                                                           \
    }                                                                   \
    REFII_RENDERER_TITLE_DRAW_HOOK_BODY(symbol)                         \
  }


REFII_RENDERER_RESOURCE_HOOK(sub_82B98930) // Mapped: LockTextureRect
REFII_RENDERER_RESOURCE_HOOK(sub_82B98950) // Mapped: UnlockTextureRect
REFII_RENDERER_RESOURCE_HOOK(sub_8225F980) // Lumen: D3DVertexBuffer_Lock
REFII_RENDERER_RESOURCE_HOOK(sub_82B9EEC0) // Mapped: UnlockVertexBuffer
REFII_RENDERER_RESOURCE_HOOK(sub_82196980) // Lumen: D3DIndexBuffer_Lock
REFII_RENDERER_RESOURCE_HOOK(sub_82B9EED0) // Mapped: UnlockIndexBuffer
// sub_821FC178 D3DResource_AddRef hook REMOVED 2026-07-29: the guest body is
// a lwarx/stwcx atomic increment on the same field the (non-atomic) emulation
// wrote, and the exit handler fed a ref_count no reader consumes. 218k
// wrapper calls/run for nothing. Release (sub_821FC320) STAYS hooked — its
// exit is the only resource-retirement path.
REFII_RENDERER_RESOURCE_HOOK(sub_821FC320) // Lumen: D3DResource_Release
REFII_RENDERER_RESOURCE_HOOK(sub_821FA070) // Lumen: D3DSurface_GetDesc
REFII_RENDERER_RESOURCE_HOOK(sub_82B98C88) // Lumen: D3DSurface_GetContainer
REFII_RENDERER_RESOURCE_HOOK(sub_821FA6C0) // Lumen: D3D::FindSurfaceWithinTexture
REFII_RENDERER_RESOURCE_HOOK(sub_821FAB38) // Lumen: D3D::GetTextureDimensions
REFII_RENDERER_RESOURCE_HOOK(sub_821FABD0) // Lumen: D3D::Get2DSurfaceDesc
REFII_RENDERER_RESOURCE_HOOK(sub_82B985C8) // Lumen: D3D::Get3DSurfaceDesc
REFII_RENDERER_RESOURCE_HOOK(sub_821FAD60) // Lumen: D3D::GetLevelSize
REFII_RENDERER_RESOURCE_HOOK(sub_82D160D8) // Lumen: D3DXLoadSurfaceFromSurface
REFII_RENDERER_RESOURCE_HOOK(sub_82D21C68) // Lumen: D3DXTex::InternalCopy(D3DSurface*,D3DSurface*)
REFII_RENDERER_RESOURCE_HOOK(sub_82D21D60) // Lumen: D3DXTex::CLockSurface::Unlock
REFII_RENDERER_RESOURCE_HOOK(sub_82D21EC0) // Lumen: D3DXTex::CLockSurface::Lock
REFII_RENDERER_RESOURCE_HOOK(sub_82D1EF90) // Lumen: D3DXTex::CBlt::BltCopy
REFII_RENDERER_RESOURCE_HOOK(sub_82D14B20) // Lumen: D3DXLoadSurfaceFromMemory
REFII_RENDERER_RESOURCE_HOOK(sub_82D14378) // Lumen: D3DXTex::CreateTextureFromTexture
REFII_RENDERER_RESOURCE_HOOK(sub_82B98B28) // Lumen: D3DDevice_CreateTexture
REFII_RENDERER_RESOURCE_HOOK(sub_82B98978) // Lumen: D3DCubeTexture_GetCubeMapSurface
REFII_RENDERER_RESOURCE_HOOK(sub_82B98A58) // Lumen: D3DVolumeTexture_GetVolumeLevel
REFII_RENDERER_RESOURCE_HOOK(sub_82B98860) // Lumen: D3DTexture_GetSurfaceLevel
REFII_RENDERER_RESOURCE_HOOK(sub_82A7B148) // IDA: streamed-texture pool async-read completion; writes residency cursor wrapper+84
REFII_RENDERER_RESOURCE_HOOK(sub_82A7AF68) // IDA: streamed-texture pool async completion callback (I/O thread); returns 1 when a stream-in finished
// sub_821FC8D8 gets a custom body below (EDRAM tile-split coarsening).
REFII_RENDERER_RESOURCE_HOOK(sub_82295380) // IDA: Fable tiled surface allocation wrapper
REFII_RENDERER_RESOURCE_HOOK(sub_82B8AB30) // IDA: Fable render/depth surface object ctor
REFII_RENDERER_GENERAL_HOOK(sub_82B96028) // IDA: Fable texture decoder/copy wrapper before XGTileTextureLevel
REFII_RENDERER_GENERAL_HOOK(sub_82B94DE0) // IDA: Fable texture decoder/copy body called by sub_82B96028
REFII_RENDERER_RESOURCE_HOOK(rex_lhTexture_Create) // Lumen: D3DDevice_CreateSurface
REFII_RENDERER_RESOURCE_HOOK(rex_lhGfx_SetupRenderTargets)
REFII_RENDERER_RESOURCE_HOOK(sub_82B6F040) // IDA: Fable presentation/depth scratch setup
REFII_RENDERER_RESOURCE_HOOK(sub_82B6F5C8) // IDA: Fable presentation/depth scratch teardown
REFII_RENDERER_GENERAL_HOOK(sub_821A5BD0) // IDA: Fable scene stream publish cursor sync
REFII_RENDERER_GENERAL_HOOK(sub_821A59E0) // IDA: Fable scene stream publish marker
// REMOVED 2026-07-28 — THESE TWO ARE SPIN-LOOP PREDICATES. DO NOT RE-ADD.
//
// Per-symbol census (run 346): sub_82294830 = 6,294,623 hits and
// sub_822A90B8 = 6,291,456 hits, against 878,583 for the next symbol down —
// SEVEN TIMES the third place. The guest busy-waits on them inside
// sub_822DFE08's queue drain, so every spin iteration paid a full hook wrapper
// (~20us measured). 6.29M x 20us is ~126 SECONDS, which is the entire wall time
// of the run.
//
// IDA confirms neither does anything a renderer needs to observe:
//   sub_822A90B8 — ~15 instructions, a queue peek: reads a1[3]/a1[4], may call
//                  sub_82294830, returns the head value.
//   sub_82294830 — ~20 instructions, refreshes ring cursors under a critical
//                  section.
// Both are internal queue bookkeeping.
//
// Hooking them also dragged RepairFableSceneStreamPublishHandoff onto every
// spin iteration (via IsFableSceneStreamBoundaryHook), so a repair routine ran
// 12.6M times. That repair still runs from the present pump and from the other
// six scene-stream boundary symbols, which are called at sane rates.
//
// This is why the native/Xenia backend is fast: RendererHooksEnabled() is false
// there, so the guest's spin costs what it costs on hardware.
REX_EXTERN(sub_8222A5D8) { // IDA: Fable scene stream wait for readable window
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "sub_8222A5D8", refii::renderer::RendererHookKind::kGeneral);
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  constexpr refii::renderer::RendererHookKind kind =
      refii::renderer::RendererHookKind::kGeneral;
  // NO OnRendererGuestHookEnter HERE (2026-07-28). This wrapper is hit 878,583
  // times because the guest function it wraps is a WAIT LOOP
  //   while (1) { ...; WaitAndReclaim(); Sleep(0); sub_82294830(a1); }
  // so the generic enter path (GTL + observer + stats, ~20us measured) was being
  // paid on entry to every wait the guest performs — third only to the two 6.29M
  // spin predicates removed above.
  //
  // The wrapper itself must STAY: unlike those two it is load-bearing, releasing
  // the renderer frame critical section (and the render-frame lock) around the
  // wait so the guest cannot deadlock against our own threads. That is a
  // correctness contract, not observation — and it is exactly why this one is a
  // targeted removal of the observation rather than a deletion of the hook.
  if (refii::renderer::RendererDeepObserverHooksEnabled()) {
    static std::atomic<uint32_t> scene_stream_wait_logs{0};
    const uint32_t wait_log_count =
        scene_stream_wait_logs.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (wait_log_count <= 16u ||
        (wait_log_count & (wait_log_count - 1u)) == 0) {
      const bool readable = IsReadableGuestByteRange(base, args.r3, 72u);
      REXLOG_INFO(
          "Refii renderer Fable scene stream wait window #{} object=0x{:08X} "
          "wait=0x{:08X} read={}/{}/{} last={} cap={} write={}/{} "
          "published={}/{} consumed={} lr=0x{:08X}",
          wait_log_count, args.r3, args.r4,
          readable ? LoadGuestU32(base, args.r3 + 12u) : 0u,
          readable ? LoadGuestU32(base, args.r3 + 16u) : 0u,
          readable ? LoadGuestU32(base, args.r3 + 20u) : 0u,
          readable ? LoadGuestU32(base, args.r3 + 24u) : 0u,
          readable ? LoadGuestU32(base, args.r3 + 28u) : 0u,
          readable ? LoadGuestU32(base, args.r3 + 32u) : 0u,
          readable ? LoadGuestU32(base, args.r3 + 40u) : 0u,
          readable ? LoadGuestU32(base, args.r3 + 60u) : 0u,
          readable ? LoadGuestU32(base, args.r3 + 68u) : 0u,
          readable ? LoadGuestU32(base, args.r3 + 64u) : 0u, args.lr);
    }
  }
  if (refii::renderer::ShouldRunGuestRendererHook("sub_8222A5D8", kind, args,
                                                  base, hook_desc)) {
    const bool release_scene_stream_lock =
        ShouldReleaseFableRendererFrameCriticalSectionDuringSceneStreamWait(
            args);
    const uint32_t render_frame_lock =
        FableRenderFrameLockDuringSceneStreamWait(args);
    uint32_t scene_stream_lock_release_count = 0;
    uint32_t render_frame_lock_release_count = 0;
    if (release_scene_stream_lock) {
      scene_stream_lock_release_count =
          ReleaseFableCriticalSectionForSceneStreamWait(
              ctx, base, kFableRendererFrameCriticalSection);
    }
    if (render_frame_lock != 0) {
      render_frame_lock_release_count =
          ReleaseFableCriticalSectionForSceneStreamWait(ctx, base,
                                                       render_frame_lock);
    }
    refii::renderer::RepairFableSceneStreamPublishHandoffForWait(
        "sub_8222A5D8_enter", base);
    __imp__sub_8222A5D8(ctx, base);
    // NO OnRendererGuestHookExit HERE (2026-07-28). Still #1 by call count at
    // 1,699,367 hits even after its enter-path observation was removed, because
    // the guest calls this wait on every scene-stream poll. The exit path is
    // pure observation (GTL exit + ObserveRendererHookExit) and this hook is
    // kGeneral, so the draw/resource observer gates never covered it.
    //
    // Everything retained around this line is load-bearing and must stay: the
    // critical-section release/reacquire pair (deadlock guard) and
    // RepairFableSceneStreamPublishHandoffForWait, which exists specifically to
    // let the wait terminate.
    if (render_frame_lock != 0) {
      ReacquireFableCriticalSectionForSceneStreamWait(
          ctx, base, render_frame_lock, render_frame_lock_release_count);
    }
    if (release_scene_stream_lock) {
      ReacquireFableCriticalSectionForSceneStreamWait(
          ctx, base, kFableRendererFrameCriticalSection,
          scene_stream_lock_release_count);
    }
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "sub_8222A5D8", kind, args, ctx, base, hook_desc);
  }
}
// REMOVED 2026-07-28 — FOURTH HOT SPIN/ALLOC PATH. DO NOT RE-ADD.
//
// After deleting the two 6.29M predicates this became the #1 symbol by a mile:
// **1,048,576 hits out of 1,376,256 total hooked calls — 76% of everything
// left.** It is per-MESSAGE, not per-frame.
//
// IDA: ~10 instructions. Sets a1[10]=a1[8], allocates 4 bytes twice via
// sub_8222ABE0, writes the tag, stores the result in a1[11]. A ring-buffer
// message-begin allocator — nothing a renderer needs to observe.
//
// Verified before removal: NO GTL case, NO emulation. Its only consumers were
// IsFableSceneStreamBoundaryHook (which dragged
// RepairFableSceneStreamPublishHandoff onto every message) and
// FableSceneStreamObserverHookIndex — observation only. The repair still runs
// from the present pump and the remaining boundary symbols.
REX_EXTERN(rex_lhRenderer_GetTimeDelta) {
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "rex_lhRenderer_GetTimeDelta", refii::renderer::RendererHookKind::kGeneral);
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  refii::renderer::OnRendererGuestHookEnter(
      "rex_lhRenderer_GetTimeDelta",
      refii::renderer::RendererHookKind::kGeneral, args, base, hook_desc);
  if (refii::renderer::BridgeFableRendererGetTimeDeltaForPlume(args, ctx,
                                                               base)) {
    refii::renderer::OnRendererGuestHookExit(
        "rex_lhRenderer_GetTimeDelta",
        refii::renderer::RendererHookKind::kGeneral, args, ctx, base,
        hook_desc);
    return;
  }
  if (refii::renderer::ShouldRunGuestRendererHook(
          "rex_lhRenderer_GetTimeDelta",
          refii::renderer::RendererHookKind::kGeneral, args, base,
          hook_desc)) {
    __imp__rex_lhRenderer_GetTimeDelta(ctx, base);
    refii::renderer::OnRendererGuestHookExit(
        "rex_lhRenderer_GetTimeDelta",
        refii::renderer::RendererHookKind::kGeneral, args, ctx, base,
        hook_desc);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "rex_lhRenderer_GetTimeDelta",
        refii::renderer::RendererHookKind::kGeneral, args, ctx, base,
        hook_desc);
  }
}

// sub_821E1F40 scene-table assign hook REMOVED 2026-07-29: enter did only two
// capped traversal diagnostics; the scene-boundary role is deep-observer-only.
// 124k wrapper calls/run.
//
// Scene FACTORY family REMOVED 2026-07-30 (sub_82A4F6A8, sub_82A4F718,
// sub_82A4F858, sub_82A4EC60, sub_82A4EDF8, sub_82A4F8D8, sub_832B2CE0,
// sub_8229BA10, sub_821CEA80). Proof of inertness under the shipping policy
// (root_only=true, deep_observer=false, Plume owns submission):
//   * kGeneral, in NO IsNativeRenderWorkHook group -> the guest body ALWAYS
//     runs (never a replacement; the sampler-setter trap does not apply);
//   * no RendererHookTarget entry, not IsMandatoryFableResourceReplacementHook
//     / terminal-draw members -> FableRendererHookAllowedForDesc is FALSE, so
//     ObserveRendererHookEnter/Exit never run for them in shipping — and even
//     their observer bodies (ObserveFableSceneFactoryCallLocked /
//     ObserveFableSceneTableLifecycleLocked) are additionally gated on
//     ShouldTraceFableScenePipelineHook, which returns false when
//     root_only && !deep_observer;
//   * scene_factory_boundary (NOT stream_boundary) — the exit path only runs
//     RepairFableSceneStreamPublishHandoff for STREAM boundaries; the factory
//     bit alone only feeds the deep-observer command-buffer capture;
//   * not GTL roots (no IsGtlKnownSymbolAddress / scene-scope case), not wait
//     probes. Their IsFableSceneFactoryBoundaryHook /
//     FableSceneFactoryObserverHookIndex entries stay (same precedent as the
//     removed spin predicates) for any future deep-observer re-hook.
REX_EXTERN(sub_822DFE08) { // IDA: Fable scene table assign caller
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "sub_822DFE08", refii::renderer::RendererHookKind::kGeneral);
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  constexpr refii::renderer::RendererHookKind kind =
      refii::renderer::RendererHookKind::kGeneral;
  refii::renderer::OnRendererGuestHookEnter("sub_822DFE08", kind, args, base,
                                            hook_desc);
  if (refii::renderer::ShouldRunGuestRendererHook("sub_822DFE08", kind, args,
                                                  base, hook_desc)) {
    // LOCK INVERSION FIX (2026-07-28). The comment ~350 lines above already
    // diagnosed this exactly: "sub_822DFE08's wait for the producer runs INSIDE
    // that lock, and the simulation thread needs the same lock to publish ...
    // the consumer blocks on the producer while holding the lock the producer
    // needs". The release/reacquire pair was wired to sub_8222A5D8 and NOT to
    // sub_822DFE08 — the function the diagnosis names.
    //
    // MEASURED consequence (run 379): 46.4ms per call, ~2.5 calls/frame,
    // 57.6 SECONDS of waiting in a ~100s run ~= 116ms of the ~200ms title frame.
    //
    // IDA (agent pass 2026-07-28): sub_822DFE08 is the deferred command-list
    // INTERPRETER, and the ring is a 150ms delay line — each published list
    // carries its own `now` and the drain target is `now - 0.15`. Because the
    // 30Hz producer stamps every list, the early-out at 0x822DFE80 should fire
    // EVERY time and ZERO waits is the designed steady state. A wait only
    // happens once the producer has published nothing for >150ms — which is
    // precisely what this lock inversion causes. Self-reinforcing: the consumer
    // spins on SwitchToThread (0x822DFF2C, one of only two guest yield sites,
    // ~817k/sec) while holding stru_8349F7FC, so the producer cannot publish,
    // so its stamps age further past 150ms.
    //
    // Releasing across the call lets the producer publish; its next stamp then
    // satisfies the early-out and the wait disappears rather than being bounded.
    const uint32_t frame_lock_release_count =
        ReleaseFableCriticalSectionForSceneStreamWait(
            ctx, base, kFableRendererFrameCriticalSection);
    __imp__sub_822DFE08(ctx, base);
    ReacquireFableCriticalSectionForSceneStreamWait(
        ctx, base, kFableRendererFrameCriticalSection,
        frame_lock_release_count);
    refii::renderer::OnRendererGuestHookExit("sub_822DFE08", kind, args, ctx,
                                             base, hook_desc);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "sub_822DFE08", kind, args, ctx, base, hook_desc);
  }
}
// Cloth-simulation rendezvous probes (IDA 2026-07-30). sub_82A4F5D8 blocks
// the render thread mid-SceneDrawSubmit (call site 0x8226e8c0) on the cloth
// worker's manual-reset completion event dword_83496F14 in 1000ms-granularity
// WaitForSingleObjectEx retries; rex_lhClothSim_ProcessQueue is the worker's
// batch drain (signals completion at 0x82a97008). On hardware the worker ran
// concurrently on its own core, so the wait normally found the event already
// set. Probe-only hooks: the enter/exit pair feeds the ClothSimWait /
// ClothWorkerBatch wait-probe roots in renderer_runtime.cpp.
REFII_RENDERER_GENERAL_HOOK(sub_82A4F5D8) // IDA: cloth-complete wait (render thread)
REFII_RENDERER_GENERAL_HOOK(rex_lhClothSim_ProcessQueue) // IDA: cloth worker batch drain
// Menu scene owner/factory-caller family REMOVED 2026-07-30 (sub_8235CA90,
// sub_8235CCB8, sub_8235CED8, sub_8235D0F8, sub_8235D268, sub_8235EF70,
// sub_8235F108, sub_8235FDC0, sub_82456E60, sub_826E4E58, sub_826E4F68,
// sub_82870B70, sub_82A77C48, sub_82A780C0, sub_82A931E0, sub_82AB99F8,
// sub_82AB9CF8, sub_82ABA800, sub_82ABAEF0). Same proof as the scene FACTORY
// family above: kGeneral passthroughs (no IsNativeRenderWorkHook membership,
// guest always runs), no RendererHookTarget entry so the observer is never
// allowed in root-only mode, their only handlers
// (FableSceneFactoryObserverHookIndex / ShouldTraceFableScenePipelineHook
// trace list) are dead when root_only && !deep_observer, factory-boundary
// exit does nothing without deep observer, and none is a GTL root or a wait
// probe.
//
// Scene enable/ready probes REMOVED 2026-07-30 (sub_82292AC0, sub_82298998,
// sub_82299AE8 — IsFableSceneEnableProbeHook): kGeneral passthroughs with no
// target entry; their ONLY handler branches (obs_scene_enable_probe in
// ObserveRendererHookEnter/Exit) early-return unless
// RendererDeepObserverHooksEnabled(), and the observer is unreachable for
// them in root-only mode anyway.
REFII_RENDERER_RESOURCE_HOOK(rex_lhGfx_CreateVertexBuffer)
REFII_RENDERER_RESOURCE_HOOK(rex_lhGfx_CreateIndexBuffer)
REFII_RENDERER_RESOURCE_HOOK(rex_lhGfx_LockBuffer)  // Lumen: D3D::LockResource
REFII_RENDERER_RESOURCE_HOOK(rex_lhGfx_FlushBuffer) // Lumen: D3D::UnlockResource
REFII_RENDERER_DRAW_HOOK(sub_82196628) // Lumen: D3DDevice_BeginExport
REFII_RENDERER_DRAW_HOOK(sub_82196750) // Lumen: D3DDevice_EndExport
REFII_RENDERER_DRAW_HOOK(sub_822C6BA8) // Lumen: D3D::InvalidateCaches
REFII_RENDERER_DRAW_HOOK(sub_8219CD68) // Lumen: D3DDevice_SetShaderGPRAllocation
REFII_RENDERER_DRAW_HOOK(sub_821EC798) // Lumen: D3DDevice_SetSamplerState_MipMapLodBias
// SAMPLER SETTER HOOKS REMOVED 2026-07-29 (AddressU/V/W, Mag/Min/MipFilter,
// FilterQuality — 0x82202D10/0x8222C568/0x822373F0/0x822383A0/0x82238128/
// 0x8222C818/0x822398D0). They were suppressed with
// EmulateFableSetSamplerState, which returns handled WITHOUT writing anything
// — every direct call's fetch-dword write was silently dropped. The guest
// bodies are pure device-memory writes (fetch dwords + pending-mask OR) that
// the GTL reads at draw, so running them natively is both faster (~100k
// wrapper calls/run removed) and more correct. Sampler state set through the
// deferred-dispatch seam was never affected.
// sub_8226B078 (Fable depth-surface bind helper) REMOVED 2026-07-30: no live
// consumer anywhere — not a GTL root, no RendererHookTarget, in no
// suppression/probe/boundary list. Pure per-call wrapper overhead on a
// per-pass helper.
REFII_RENDERER_DRAW_HOOK(sub_821BA0F0) // IDA: Fable alternate scene render submit
REX_EXTERN(sub_821C3858) { // IDA: Fable frame director / scene branch
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "sub_821C3858", refii::renderer::RendererHookKind::kDraw);
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  constexpr refii::renderer::RendererHookKind kind =
      refii::renderer::RendererHookKind::kDraw;
  // The shader-binding-table probe that used to live here has MOVED to the
  // hook on sub_821E5DB8 below. It was measuring outside the window in which
  // the table is meaningful and so reported -1 for every record, every sample,
  // for a whole run. See the comment on that hook for why.
  refii::renderer::OnRendererGuestHookEnter("sub_821C3858", kind, args, base,
                                            hook_desc);
  if (refii::renderer::ShouldRunGuestRendererHook(
          "sub_821C3858", kind, args, base, hook_desc)) {
    __imp__sub_821C3858(ctx, base);
    refii::renderer::OnRendererGuestHookExit("sub_821C3858", kind, args, ctx,
                                             base, hook_desc);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "sub_821C3858", kind, args, ctx, base, hook_desc);
  }
}
// SHADER BINDING TABLE PROBE (2026-08-04, second attempt) — the black-gameplay
// question. sub_821E5DB8 IS the tonemap composite; it binds the scene colour at
// 0x821E6254, but only past this gate:
//     0x821e622c  lwz   r4, 0x51C(r11)   ; sampler = binding-table record 109
//     0x821e623c  blt   cr6, ...         ; NEGATIVE -> skip the bind entirely
//
// WHY THE FIRST ATTEMPT WAS VOID, and the correction that matters:
// `g_ShaderBindingTable` (vector object 0x834BDED4, begin 0x834BDED8, 12-byte
// records) is NOT a load-time table. It is a per-shader SCRATCHPAD. The writer
// is sub_82221560 (PS) / sub_82221300 (VS), reached from
// Render_SetPixelShaderFromBinding 0x82208B60: it stamps the records of the
// shader being bound...
//     82221624  stw  r4, 0(r10)          ; record[i].field0 = sampler register
// ...after sub_82221470 has REVERTED the previous shader's records to -1:
//     82221534  *(v5)     = -1
//     82221538  *(v5 + 4) =  0
// So a record holds a real sampler register ONLY between its shader's bind and
// the next shader bind. The old probe sampled from the frame director, outside
// every such window, and therefore read -1 for all nine records on every sample
// of a whole run. That measurement carries NO information and has been deleted.
//
// Both ends of the window are inside sub_821E5DB8 (PS bound at 0x821E5EEC, the
// nine reads at 0x821E615C-0x821E63E0), so sampling on the way OUT of this
// function reads exactly what the binds read. field0 is the sampler register;
// field1 is the element count, 0 meaning "not declared by this shader".
//
// Record map for this draw's nine binds (offset = record * 12):
//   262 tonecurve | 56 depth | 109 SCENE COLOUR | 204 aux | 22 bloom
//   179 quarter   |  53 aux  |  74 aux          |   2 halfres
REX_EXTERN(sub_821E5DB8) { // IDA: Fable tonemap / post-process composite submit
  REX_FUNC_PROLOGUE();
  // Argument registers must be read BEFORE the body runs — the three declared
  // binds take their pTexture from them:
  //   scene colour (record 109) = *(*(a2 + 0x00) + 4)   a2 = r4
  //   bloom        (record  22) = *(a3 + 4)             a3 = r5
  //   tone curve   (record 262) = *(a6 + 4)             a6 = r8
  const uint32_t arg_a2 = ctx.r4.u32;
  const uint32_t arg_a3 = ctx.r5.u32;
  const uint32_t arg_a6 = ctx.r8.u32;
  __imp__sub_821E5DB8(ctx, base);
  static int32_t last_scene_record = INT32_MAX;
  static uint32_t tonemap_logs = 0;
  static uint32_t tonemap_calls = 0;
  ++tonemap_calls;
  if (base == nullptr || tonemap_logs >= 32u) {
    return;
  }
  constexpr uint32_t kBindingTableBeginPtr = 0x834BDED8u;
  constexpr uint32_t kBindingTableEndPtr = 0x834BDEDCu;
  constexpr uint32_t kRecordStride = 12u;
  const uint32_t begin = LoadGuestU32(base, kBindingTableBeginPtr);
  const uint32_t end = LoadGuestU32(base, kBindingTableEndPtr);
  if (begin == 0 || end <= begin) {
    return;
  }
  const uint32_t count = (end - begin) / kRecordStride;
  // field0 = sampler register (-1 when this shader does not declare it),
  // field1 = element count (0 = unbound). Report them as "reg/count" so a
  // legitimately-undeclared binding is distinguishable from a stale one.
  const auto reg = [&](uint32_t index) -> int32_t {
    if (index >= count) {
      return INT32_MIN; // past the end: the guest would read garbage
    }
    return static_cast<int32_t>(LoadGuestU32(base, begin + index * kRecordStride));
  };
  const auto elems = [&](uint32_t index) -> int32_t {
    if (index >= count) {
      return INT32_MIN;
    }
    return static_cast<int32_t>(
        LoadGuestU32(base, begin + index * kRecordStride + 4u));
  };
  const int32_t scene = reg(109);
  // THE GUEST TEXTURE POINTERS FOR THE THREE DECLARED BINDS (2026-08-04).
  // Run 560 proved the scene bind RUNS: SCENE[109] = register 0, count 1, with
  // tone curve at 1 and bloom at 2 — and all 64 `UNSTATED texture slot bound
  // null` warnings in that run are slot 0. So the guest calls
  // SetTexture(0, pTexture) and our command still states nothing for slot 0.
  // Only two things can produce that, and these three reads separate them:
  //   scene == 0                -> the GUEST has no scene texture to bind; the
  //                                defect is upstream, in whatever fills
  //                                dword_8331A97C (the EDRAM resolve
  //                                destination, written only by
  //                                Render_AllocateSceneRenderTargetSet
  //                                0x82A4E6D0)
  //   scene != 0                -> the guest binds a real texture and WE lose
  //                                it; key the resolver on this exact pointer
  // Tone curve and bloom are the control group: they bind correctly in
  // RenderDoc, so whatever they read is what a working holder looks like.
  const uint32_t scene_holder = LoadGuestU32(base, arg_a2);
  const uint32_t scene_tex =
      scene_holder != 0 ? LoadGuestU32(base, scene_holder + 4u) : 0u;
  const uint32_t bloom_tex = arg_a3 != 0 ? LoadGuestU32(base, arg_a3 + 4u) : 0u;
  const uint32_t tone_tex = arg_a6 != 0 ? LoadGuestU32(base, arg_a6 + 4u) : 0u;
  // Cross-check against the known global. IDA has the scene bind reading
  // dword_8331A97C; if scene_holder + 4 is not that address the holder is
  // dynamic and the global is stale.
  const uint32_t scene_global = LoadGuestU32(base, 0x8331A97Cu);
  // Log the first few calls unconditionally (menu-phase baseline), then only
  // when something MOVES. The record itself is expected to stay at 0, so the
  // scene texture pointer is the live variable; keying on it is what gets us
  // in-game samples instead of six menu frames.
  const int32_t change_key =
      static_cast<int32_t>(static_cast<uint32_t>(scene) ^ scene_tex);
  if (tonemap_logs >= 6u && change_key == last_scene_record) {
    return;
  }
  last_scene_record = change_key;
  ++tonemap_logs;
  // The currently-bound VS/PS indices, so the sample can be tied to a shader
  // permutation rather than floating free.
  const uint32_t bound_a = LoadGuestU32(base, 0x83316E94u);
  const uint32_t bound_b = LoadGuestU32(base, 0x83316E98u);
  REXLOG_INFO(
      "Refii tonemap binding table #{} calls={} count={} begin=0x{:08X} "
      "bound=0x{:08X}/0x{:08X} | SCENE[109]={}/{} tonecurve[262]={}/{} "
      "bloom[22]={}/{} depth[56]={}/{} halfres[2]={}/{} quarter[179]={}/{} "
      "aux[53]={}/{} aux[74]={}/{} aux[204]={}/{} | TEX scene=0x{:08X} "
      "(holder=0x{:08X} @0x{:08X}) bloom=0x{:08X} tone=0x{:08X} "
      "global_8331A97C=0x{:08X} — reg/count, reg -1 = the shader does not "
      "declare it so the bind is CORRECTLY skipped",
      tonemap_logs, tonemap_calls, count, begin, bound_a, bound_b, scene,
      elems(109), reg(262), elems(262), reg(22), elems(22), reg(56), elems(56),
      reg(2), elems(2), reg(179), elems(179), reg(53), elems(53), reg(74),
      elems(74), reg(204), elems(204), scene_tex, scene_holder, arg_a2,
      bloom_tex, tone_tex, scene_global);
}
// FRAME-BODY BISECTION ROOTS (2026-07-28). Purpose is measurement only: the
// title frame is ~207-230ms of which our renderer accounts for ~58-74ms, and
// the static cost map puts the remainder inside sub_82388380's body. These four
// were the ranked instrumentation points; each is called ~once per frame, so
// the ~20us wrapper cost is noise here (unlike the spin predicates removed
// above, which were per-iteration).
//
// sub_82181B68 is the one that matters: sole call site 0x821C3AB0 inside
// sub_821C3858, and it holds an UNBOUNDED NON-YIELDING loop at
// 0x82181D3C-0x82181D78 pumping a resource-load state machine to completion on
// the render thread, inside GPU LOCK #1. It uses no wait primitive, so it is
// invisible to every SwitchToThread/Sleep census we have run.
REFII_RENDERER_GENERAL_HOOK(sub_82181B68) // IDA: Fable scene prepare / sync resource load spin
REFII_RENDERER_GENERAL_HOOK(sub_821C3670) // IDA: Fable main scene pass (point-list + bloom downsample)
// NOT HOOKED HERE: sub_8218FA68 already has a hook in
// src/vfs/src/hooks/boot_lifecycle.cpp (adding one here is a link-time
// multiple-definition error). That hook takes the HOST RenderSharedStateMutex
// across the guest call and emits no enter/exit callbacks, so a probe root for
// it would never fire — see the probe table note in renderer_runtime.cpp.
REFII_RENDERER_DRAW_HOOK(sub_821C3C60) // IDA: Fable scene frame submit handoff
REFII_RENDERER_DRAW_HOOK(sub_8226E180) // IDA: Fable scene state flush/update path
REFII_RENDERER_DRAW_HOOK(sub_8226E738) // IDA: Fable scene draw submission path
REFII_RENDERER_DRAW_HOOK(sub_82ABBA60) // IDA: Fable scene object draw filter
REFII_RENDERER_DRAW_HOOK(sub_82ABD088) // IDA: Fable scene object batch traversal
REFII_RENDERER_DRAW_HOOK(sub_822189B0) // IDA: Fable scene object record builder / batch draw bridge caller
REFII_RENDERER_DRAW_HOOK(sub_82219740) // IDA: Fable scene batch terminal DrawPrimitive bridge
REFII_RENDERER_DRAW_HOOK(sub_822194B8) // Lumen: D3DQuery_Issue(D3DQuery*, DWORD IssueFlags) -- a QUERY, not a draw
REFII_RENDERER_DRAW_HOOK(sub_8223CFA0) // IDA: Fable scene object list walker
REFII_RENDERER_TITLE_DRAW_HOOK(sub_821D4F60) // IDA: title/menu list object callback thunk target
REFII_RENDERER_TITLE_DRAW_HOOK(sub_821D9C98) // IDA: title/menu world renderable registry callback
REFII_RENDERER_SCENE_SCOPE_TITLE_DRAW_HOOK(sub_822975F8) // IDA: title/menu object visibility traversal (pure scope marker)
REFII_RENDERER_TITLE_DRAW_HOOK(sub_82297698) // IDA: title/menu recursive visibility traversal
REX_EXTERN(sub_821CB498) { // IDA: title/menu visible object enqueue (probed)
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "sub_821CB498", refii::renderer::RendererHookKind::kDraw);
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  constexpr refii::renderer::RendererHookKind kind =
      refii::renderer::RendererHookKind::kDraw;
  refii::renderer::OnRendererGuestHookEnter("sub_821CB498", kind, args, base,
                                            hook_desc);
  if (!refii::renderer::ShouldRunGuestRendererHook("sub_821CB498", kind, args,
                                                   base, hook_desc)) {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "sub_821CB498", kind, args, ctx, base, hook_desc);
    return;
  }
  const bool probe = refii::renderer::RendererDeepObserverHooksEnabled();
  const uint32_t node = ctx.r4.u32;
  uint32_t vtable = 0;
  uint32_t mask = 0;
  uint16_t radius_half = 0;
  if (probe && base != nullptr && node != 0 && node < 0x7FF00000u) {
    std::memcpy(&vtable, base + node, 4);
    vtable = __builtin_bswap32(vtable);
    std::memcpy(&mask, base + node + 72u, 4);
    mask = __builtin_bswap32(mask);
    std::memcpy(&radius_half, base + node + 82u, 2);
    radius_half = __builtin_bswap16(radius_half);
  }
  __imp__sub_821CB498(ctx, base);
  if (probe) {
    const bool gui_family =
        vtable == 0x82005670u || vtable == 0x820068E0u ||
        vtable == 0x82006548u || vtable == 0x820F6E50u ||
        vtable == 0x820F6E98u;
    static std::atomic<uint32_t> logged_enqueue_probes{0};
    const uint32_t count =
        logged_enqueue_probes.fetch_add(1, std::memory_order_relaxed) + 1u;
    if ((gui_family && count < 100000u) || count <= 8u ||
        (count & (count - 1u)) == 0) {
      REXLOG_INFO(
          "Refii renderer gui enqueue probe #{} node=0x{:08X} vtable=0x{:08X} "
          "mask=0x{:08X} radius_half=0x{:04X} verdict=0x{:08X} gui={}",
          count, node, vtable, mask, radius_half, ctx.r3.u32,
          gui_family ? 1 : 0);
    }
  }
  refii::renderer::OnRendererGuestHookExit("sub_821CB498", kind, args, ctx,
                                           base, hook_desc);
}
REFII_RENDERER_TITLE_DRAW_HOOK(sub_82231ED8) // IDA: title/menu render-list callback dispatch
REX_EXTERN(sub_82192028) { // IDA: title/menu indirect object render callback (probed)
  REX_FUNC_PROLOGUE();
  // Pure scene-scope marker (see REFII_RENDERER_SCENE_SCOPE_TITLE_DRAW_HOOK):
  // the deep-observer probe below survives on the full path, which the fast
  // path defers to whenever any diagnostic mode is on.
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "sub_82192028", refii::renderer::RendererHookKind::kDraw);
  if (refii::renderer::BeginFableSceneScopeMarkerFastPath("sub_82192028", base,
                                                          hook_desc)) {
    __imp__sub_82192028(ctx, base);
    refii::renderer::EndFableSceneScopeMarkerFastPath();
    return;
  }
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  constexpr refii::renderer::RendererHookKind kind =
      refii::renderer::RendererHookKind::kDraw;
  refii::renderer::OnRendererGuestHookEnter("sub_82192028", kind, args, base,
                                            hook_desc);
  if (!refii::renderer::ShouldRunGuestRendererHook("sub_82192028", kind, args,
                                                   base, hook_desc)) {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "sub_82192028", kind, args, ctx, base, hook_desc);
    return;
  }
  if (refii::renderer::RendererDeepObserverHooksEnabled()) {
    const uint32_t object = ctx.r4.u32;
    uint32_t vtable = 0;
    if (base != nullptr && object != 0 && object < 0x7FF00000u) {
      std::memcpy(&vtable, base + object, 4);
      vtable = __builtin_bswap32(vtable);
    }
    static std::atomic<uint32_t> logged_object_dispatch{0};
    const uint32_t count =
        logged_object_dispatch.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (count <= 12u || (count & (count - 1u)) == 0 ||
        vtable == 0x82005670u) {
      REXLOG_INFO(
          "Refii renderer gui object dispatch #{} object=0x{:08X} "
          "vtable=0x{:08X}",
          count, object, vtable);
    }
  }
  __imp__sub_82192028(ctx, base);
  refii::renderer::OnRendererGuestHookExit("sub_82192028", kind, args, ctx,
                                           base, hook_desc);
}
REFII_RENDERER_TITLE_DRAW_HOOK(sub_821A4ED0) // IDA: title/menu render-list insert callback
REFII_RENDERER_SCENE_SCOPE_TITLE_DRAW_HOOK(sub_8222C2B0) // IDA: title/menu render-list sorted insert (pure scope marker)
REFII_RENDERER_SCENE_SCOPE_TITLE_DRAW_HOOK(sub_822749E0) // IDA: title/menu renderable node producer (pure scope marker)
REFII_RENDERER_TITLE_DRAW_HOOK(sub_82A679C0) // IDA: title/help-page renderable node producer
REFII_RENDERER_TITLE_DRAW_HOOK(sub_82A73DC8) // IDA: title/menu renderable node producer variant
REFII_RENDERER_TITLE_DRAW_HOOK(sub_82B285F8) // IDA: title/background renderable readiness pump
REFII_RENDERER_TITLE_DRAW_HOOK(sub_82A657C0) // IDA: title/background renderable resource gate
REFII_RENDERER_DRAW_HOOK(sub_822A8F68) // IDA: Fable scene walker wrapper + pending state flush
// sub_82EF2A90 (D3DDevice::BeginScene) REMOVED 2026-07-30: no live consumer —
// BeginScene is a no-op-adjacent guest call with no GTL case, no target entry,
// no list membership. The wrapper was pure overhead.
REFII_RENDERER_DRAW_HOOK(sub_82205F68) // Lumen: D3DDevice_BeginVertices
REFII_RENDERER_DRAW_HOOK(sub_82217DB8) // Lumen: D3DDevice_BeginIndexedVertices

// Native resolution override (2026-08-02, IDA-specced). sub_82B6EA60 =
// Render_InitPresentationAndFirstSwap: r3 -> guest param block whose first
// two u32 fields are the display width/height that sub_82A1AC98 hardcodes to
// 1280x720 (ignoring XGetVideoMode). Overriding them HERE propagates
// everywhere: device creation copies them into the present params and into
// qword_834A5FE8 (the guest's only resolution source of truth — no other
// writer), from which the whole RT set, downsample chain, viewports and
// pass setup derive. Alignment matches the guest's own resize path
// (sub_82B6EBC0): width up to 160, height up to 16. Inactive (stock 720p)
// unless both cvars are set.
// ===========================================================================
// EDRAM TILE-SPLIT COARSENING (2026-08-03, IDA-specced)
//
// THE BUG THIS PREVENTS: the render-target object carries exactly FIVE per-tile
// constant slots (a1[11..15]), but sub_82B8B338's fill loop is bounded only by
// the mode entry's tile count. The guest's tiler (sub_82B8AFE0) shrinks the
// slice height until the surface fits 1024 EDRAM tiles -- half of the 10MB,
// because depth is placed immediately after colour at the same size -- so the
// tile count RISES with resolution:
//
//     res          msaaIdx 0    1 (default)   2
//     1280x720         1            2         3
//     1920x1088        2            4         7
//     2560x1440        3            6        12
//     3840x2160        7           14        34
//
// At 1440p on the DEFAULT path (sub_821C3C60 maps sampleCount 0 and 2 to
// msaaIdx 1) that is 6 tiles into 5 slots: the loop writes past a1[15] into
// a1[16], the whole-frame buffer. Silent corruption, not a crash.
//
// WHY COARSENING IS THE RIGHT FIX: the tile count only shrinks to satisfy an
// EDRAM budget we do not emulate -- the GTL does not model EDRAM as storage,
// and IDA confirms an out-of-range tile base is never truncated, wrapped or
// branched on anywhere in the guest. So oversized tiles cost us nothing, and
// keeping >1 tile preserves the tiled path, which is the ONLY route to
// Render_BeginTilingPass (SetViewport / SetScissorRect / ClearF) and one-pass Z.
// Forcing tileCount=1 would silently drop all of that.
//
// THE CEILING WE MUST RESPECT: sub_82295380 bails when
// XGSurfaceSize(tile) > 0x800, leaving the depth surface NULL, after which
// sub_82B8B338 reads *(NULL+44) into a1[10]. That bounds coarsening to exactly
// 2x the solver's count. 1440p/msaaIdx1 needs >=3 tiles and we use 3 -- at the
// limit, with margin. 1440p/msaaIdx2 needs >=6 and cannot be rescued at all,
// so that case downgrades MSAA rather than corrupting the object.
//
// Hook point: sub_821FC8D8 (colour surface create) ENTRY. It fires immediately
// after every retile, and IDA proves sub_82B8B338 RELOADS the entry pointer and
// the {W,H} pair from guest stack AFTER it returns, so writes here reach the
// colour surface, XGSurfaceSize, the depth surface, the per-tile constant loop
// and Render_BeginScenePass. Nothing downstream re-derives from the display
// global.
namespace {

constexpr uint32_t kFableEdramModeTableBase = 0x834BE010u;
constexpr uint32_t kFableEdramModeTableStride = 0x6Cu;
// TEN entries, not nine: sub_832B6F10 destroys 10 x 27 dwords down from
// 0x834BE448. Entry 9 (0x834BE3DC) is left fully zeroed by the initialiser.
constexpr uint32_t kFableEdramModeTableEntries = 10u;
// Byte offsets inside a mode entry (IDA sub_83293788 / sub_82B8AE70).
constexpr uint32_t kModeEntryWidth = 0x04u;
constexpr uint32_t kModeEntryHeight = 0x08u;
constexpr uint32_t kModeEntryFormat = 0x0Cu;
constexpr uint32_t kModeEntryMsaa = 0x10u;
constexpr uint32_t kModeEntryTileCount = 0x14u;
constexpr uint32_t kModeEntryRects = 0x18u;
// a1[11..15] -- five slots, and the loop is not bounded by them.
constexpr uint32_t kFableEdramMaxTileSlots = 5u;
// sub_82295380's bail threshold on XGSurfaceSize.
constexpr uint32_t kFableEdramMaxSurfaceTiles = 0x800u;

constexpr uint32_t AlignUpU32(uint32_t value, uint32_t alignment) {
  return ((value + alignment - 1u) / alignment) * alignment;
}

// XGSurfaceSize (0x821F52A0), verbatim: align16(Heff) * align80(Weff) * bpp
// / 0x1400. MSAA doubles height at 2x and BOTH axes at 4x, before alignment.
uint32_t FableXGSurfaceTiles(uint32_t width, uint32_t height, uint32_t msaa_index,
                             uint32_t bytes_per_pixel) {
  const uint32_t effective_width = (msaa_index == 2u) ? width * 2u : width;
  const uint32_t effective_height = (msaa_index >= 1u) ? height * 2u : height;
  return AlignUpU32(effective_height, 16u) * AlignUpU32(effective_width, 80u) *
         bytes_per_pixel / 0x1400u;
}

// Largest slice height whose tile still satisfies sub_82295380's ceiling.
uint32_t FableMaxSliceHeight(uint32_t aligned_width, uint32_t msaa_index,
                             uint32_t bytes_per_pixel) {
  uint32_t best = 0;
  for (uint32_t slice = 16u; slice <= 4096u; slice += 16u) {
    if (FableXGSurfaceTiles(aligned_width, slice, msaa_index,
                            bytes_per_pixel) <= kFableEdramMaxSurfaceTiles) {
      best = slice;
    } else {
      break;
    }
  }
  return best;
}

// Resolve r5 (a pointer to entry+0x0C) back to its entry base, structurally
// rather than by whitelisting one address: it must land inside the table and
// sit exactly on an entry's format field.
bool FableResolveEdramModeEntry(uint32_t format_field_pointer, uint32_t &entry) {
  if (format_field_pointer < kFableEdramModeTableBase + kModeEntryFormat) {
    return false;
  }
  const uint32_t offset = format_field_pointer - kFableEdramModeTableBase;
  if (offset >= kFableEdramModeTableEntries * kFableEdramModeTableStride) {
    return false;
  }
  if (offset % kFableEdramModeTableStride != kModeEntryFormat) {
    return false;
  }
  entry = kFableEdramModeTableBase +
          (offset / kFableEdramModeTableStride) * kFableEdramModeTableStride;
  return true;
}

// Rewrite the entry to `tiles` full-width horizontal slices covering [0, H).
// Rect layout proven half-open: tile i+1's top == tile i's bottom, left/right
// are always 0/align32(W) (this path can never emit a vertical split).
void FableWriteEdramTileSplit(uint8_t *base, uint32_t entry, uint32_t aligned_width,
                              uint32_t height, uint32_t tiles, uint32_t slice) {
  StoreGuestU32(base, entry + kModeEntryTileCount, tiles);
  for (uint32_t i = 0; i < tiles; ++i) {
    const uint32_t rect = entry + kModeEntryRects + i * 16u;
    const uint32_t top = i * slice;
    const uint32_t bottom = (i + 1u == tiles) ? height : (top + slice);
    StoreGuestU32(base, rect + 0u, 0u);
    StoreGuestU32(base, rect + 4u, top);
    StoreGuestU32(base, rect + 8u, aligned_width);
    StoreGuestU32(base, rect + 12u, bottom);
  }
}

}  // namespace

REX_EXTERN(sub_821FC8D8) { // IDA: Fable surface allocation wrapper
  REX_FUNC_PROLOGUE();
  constexpr auto kind = refii::renderer::RendererHookKind::kResource;
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc("sub_821FC8D8", kind);
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);

  uint32_t entry = 0;
  if (base != nullptr && FableResolveEdramModeEntry(ctx.r5.u32, entry)) {
    const uint32_t width = LoadGuestU32(base, entry + kModeEntryWidth);
    const uint32_t height = LoadGuestU32(base, entry + kModeEntryHeight);
    uint32_t msaa_index = LoadGuestU32(base, entry + kModeEntryMsaa);
    const uint32_t tile_count = LoadGuestU32(base, entry + kModeEntryTileCount);
    // *** EDRAM TILING ELIMINATED, NOT EMULATED (2026-08-04) ***
    //
    // Fable splits its framebuffer into N passes because the Xbox 360 has 10MB
    // of embedded DRAM. Refii is a REPLACEMENT renderer on a modern GPU
    // (CLAUDE.md §5) and has no such budget, so the correct answer is not to
    // implement predication and tile placement — it is to make the guest stop
    // tiling. Collapse the split to ONE full-surface tile.
    //
    // This is not fighting the guest: it has a native one-tile fast path.
    // `D3DDevice_EndTiling` 0x8227F3B0 tests the count and branches straight to
    // KickOff, skipping the ENTIRE predicated command-buffer replay:
    //     8227f58c  lwz    r11, 0x31CC(r31)
    //     8227f594  cmplwi cr6, r11, 1
    //     8227f598  beq    cr6, loc_8227F6E8   ; -> D3D_CDevice_KickOff
    //
    // Every other consumer degenerates correctly (all IDA-verified):
    //  * Render_BeginPredicatedTileClears 0x821C6478 — its max-reduction over
    //    tiles yields the full surface; both predicated clears cover the same
    //    (0,0,W,H). No behavioural change beyond one loop iteration.
    //  * Render_EmitClearRects 0x821F0E00 — the per-tile clip/translate
    //    collapses to a single untranslated ClearRect (point[0] is (0,0), so
    //    the `screen - point` subtractions are no-ops). Its unindexed tile-0
    //    read at 0x821F120C becomes trivially correct instead of accidentally
    //    correct.
    //  * D3D_StartWorkerQueue 0x822A5EE8 — packet[2] = 1 and one rect; the
    //    0xFC buffer advance is count-independent, so layout is unchanged.
    //  * D3DDevice_Resolve — takes the final-tile branch, keeps the caller's
    //    ResolveFlags, and never ORs the 0x100/0x200 clear-on-resolve priming
    //    bits. pDestPoint is rect[0] = (0,0).
    //  * D3DQuery_Issue 0x822194E0 — expected-result count 1, which is exactly
    //    what the non-tiled path already writes.
    //
    // Nothing in the guest validates that one full-screen tile fits in EDRAM
    // (D3D_ConfigureForOnePassZPass sizes Hi-Z from max(rect) with no capacity
    // check), so the guest has no way to object — and no reason to, since we
    // render to real textures and never model EDRAM at all.
    //
    // What this deletes, in one move: the 576-tall strip that was poisoning the
    // scissor scale basis (a capture showed the scene resolve running as
    // 1120x576 + 1120x144 at NATIVE resolution — tiling was every frame, not
    // just >720p); the 5-slot per-tile array overflow above 1440p; and the
    // silent MSAA downgrade the old coarsening solver used to buy slots back.
    // MSAA is now left exactly as the guest asked for it.
    if (width != 0 && height != 0 && msaa_index <= 2u && tile_count > 1u) {
      const uint32_t aligned_width = AlignUpU32(width, 32u);
      FableWriteEdramTileSplit(base, entry, aligned_width, height, /*tiles=*/1u,
                               /*slice=*/height);
      // The {W,H} pair is the MAX TILE extent and sizes BOTH surfaces; IDA
      // proves it is reloaded from guest stack after we return. With one tile
      // the max tile IS the whole surface.
      if (ctx.r4.u32 != 0) {
        StoreGuestU32(base, ctx.r4.u32 + 0u, aligned_width);
        StoreGuestU32(base, ctx.r4.u32 + 4u, height);
      }
      static uint32_t logged_tile_collapse = 0;
      if (logged_tile_collapse < 8u) {
        ++logged_tile_collapse;
        REXLOG_INFO(
            "Refii EDRAM tiling COLLAPSED {}x{} msaa_idx={} tiles {}->1 "
            "(replacement renderer: no 10MB budget, so no tiling; guest takes "
            "its own one-tile fast path at EndTiling 0x8227F598)",
            width, height, msaa_index, tile_count);
      }
    }
  }

  refii::renderer::OnRendererGuestHookEnter("sub_821FC8D8", kind, args, base,
                                            hook_desc);
  if (refii::renderer::ShouldRunGuestRendererHook("sub_821FC8D8", kind, args,
                                                  base, hook_desc)) {
    __imp__sub_821FC8D8(ctx, base);
    refii::renderer::OnRendererGuestHookExit("sub_821FC8D8", kind, args, ctx,
                                             base, hook_desc);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume("sub_821FC8D8", kind,
                                                           args, ctx, base,
                                                           hook_desc);
  }
}

REX_EXTERN(sub_82B6EA60) { // Fable Render_InitPresentationAndFirstSwap
  REX_FUNC_PROLOGUE();
  // RendererNativeWidth/HeightOverride now FALL BACK to the launcher/toml
  // resolution (video_mode_width/height), so these are non-zero on every run
  // and this hook always states the extent — at the stock default it states
  // 1280x720, which is what the guest hardcodes anyway.
  const uint32_t requested_w = refii::renderer::RendererNativeWidthOverride();
  const uint32_t requested_h = refii::renderer::RendererNativeHeightOverride();
  if (requested_w != 0 && requested_h != 0 && ctx.r3.u32 != 0) {
    // Shared with DesiredFableNativeRenderExtent so the two host paths that
    // state an extent to the guest can never disagree again (run 544).
    const uint32_t aligned_w =
        refii::renderer::RendererGuestAlignedRenderWidth(requested_w);
    const uint32_t aligned_h =
        refii::renderer::RendererGuestAlignedRenderHeight(requested_h);
    StoreGuestU32(base, ctx.r3.u32 + 0u, aligned_w);
    StoreGuestU32(base, ctx.r3.u32 + 4u, aligned_h);
    static uint32_t logged_native_resolution = 0;
    if (logged_native_resolution < 4u) {
      ++logged_native_resolution;
      REXLOG_INFO(
          "Refii guest native resolution {}x{} (requested {}x{}, stock "
          "1280x720, source={})",
          aligned_w, aligned_h, requested_w, requested_h,
          refii::renderer::RendererNativeWidthOverrideIsExplicit()
              ? "renderer_native_* cvar"
              : "launcher/toml resolution");
    }
  }
  __imp__sub_82B6EA60(ctx, base);
}

// GUI quad-batch pipeline stage counters (diagnostic; guest always runs).
REX_EXTERN(sub_82A5AA98) { // Fable GUI draw-item queue append
  REX_FUNC_PROLOGUE();
  refii::renderer::CountFableGuiPipelineStage(0);
  __imp__sub_82A5AA98(ctx, base);
}
REX_EXTERN(sub_822AA0A0) { // Fable GUI batch enable + vertex prep
  REX_FUNC_PROLOGUE();
  refii::renderer::CountFableGuiPipelineStage(1);
  __imp__sub_822AA0A0(ctx, base);
}
REX_EXTERN(sub_821CA5E8) { // Fable GUI draw-item dispatcher
  REX_FUNC_PROLOGUE();
  refii::renderer::CountFableGuiPipelineStage(2);
  __imp__sub_821CA5E8(ctx, base);
}
REX_EXTERN(sub_82AA0A98) { // Fable GUI widget geometry factory
  REX_FUNC_PROLOGUE();
  refii::renderer::CountFableGuiPipelineStage(3);
  __imp__sub_82AA0A98(ctx, base);
}
REX_EXTERN(sub_82A653C8) { // Fable GUI widget derived ctor (dual geometry)
  REX_FUNC_PROLOGUE();
  const uint32_t widget = ctx.r3.u32;
  refii::renderer::CountFableGuiPipelineStage(4);
  __imp__sub_82A653C8(ctx, base);
  if (refii::renderer::RendererDeepObserverHooksEnabled()) {
    static std::atomic<uint32_t> logged_widget_ctors{0};
    const uint32_t count =
        logged_widget_ctors.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (count <= 48u && base != nullptr) {
      const uint8_t enable = *(base + widget + 336u);
      const uint8_t failed = *(base + widget + 338u);
      REXLOG_INFO(
          "Refii renderer gui widget ctor #{} widget=0x{:08X} type={} "
          "enable={} failed={}",
          count, widget, ctx.r4.u32, enable, failed);
    }
  }
}
REX_EXTERN(sub_82B1E1B8) { // Fable title GUI mesh geometry build/refresh
  REX_FUNC_PROLOGUE();
  refii::renderer::CountFableGuiPipelineStage(6);
  __imp__sub_82B1E1B8(ctx, base);
}
REX_EXTERN(sub_82B81868) { // Fable D3D buffer data write (create+fill pair)
  REX_FUNC_PROLOGUE();
  // IDA: three parameters (object, data, size); r6 is not an argument.
  if (refii::renderer::RendererDeepObserverHooksEnabled()) {
    static std::atomic<uint32_t> logged_buffer_writes{0};
    const uint32_t count =
        logged_buffer_writes.fetch_add(1, std::memory_order_relaxed) + 1u;
    const uint32_t size = ctx.r5.u32;
    if (count <= 12u || (count & (count - 1u)) == 0 || size == 256u ||
        size == 64u) {
      REXLOG_INFO(
          "Refii renderer buffer write probe #{} obj=0x{:08X} data=0x{:08X} "
          "size={} lr=0x{:08X}",
          count, ctx.r3.u32, ctx.r4.u32, size, ctx.lr);
    }
  }
  __imp__sub_82B81868(ctx, base);
}
REX_EXTERN(sub_82A73FC8) { // Fable GUI mesh world-command handler family
  REX_FUNC_PROLOGUE();
  refii::renderer::CountFableGuiPipelineStage(7);
  __imp__sub_82A73FC8(ctx, base);
}
REFII_RENDERER_DRAW_HOOK(sub_821EBF30) // IDA: BeginVertices wrapper; copies 2D scratch payload after allocation
REFII_RENDERER_DRAW_HOOK(sub_82242C58) // IDA: BeginVertices wrapper; writes packed scratch payload after allocation
REFII_RENDERER_DRAW_HOOK(sub_82A61860) // IDA: BeginVertices wrapper; copies generated scratch payload after allocation
REFII_RENDERER_DRAW_HOOK(sub_82B667F0) // IDA: BeginVertices wrapper; copies shared scratch payload after allocation
REFII_RENDERER_DRAW_HOOK(sub_821D7550) // IDA: BeginIndexedVertices wrapper; copies scratch payload after allocation
REFII_RENDERER_DRAW_HOOK(sub_8220A3F0) // IDA: BeginIndexedVertices wrapper; copies scratch payload after allocation
REFII_RENDERER_DRAW_HOOK(sub_8222E120) // IDA: BeginIndexedVertices wrapper; copies scratch payload after allocation
REFII_RENDERER_DRAW_HOOK(sub_82AA59B8) // IDA: BeginIndexedVertices wrapper; copies stack quad payload after allocation
REFII_RENDERER_DRAW_HOOK(sub_821ECD30) // IDA: exact private texture rectangle wrapper; active private surface slot
REFII_RENDERER_DRAW_HOOK(sub_822326E0) // IDA: exact private texture rectangle wrapper
REFII_RENDERER_DRAW_HOOK(sub_82263D28) // IDA: exact private texture rectangle wrapper; source object arg
REFII_RENDERER_DRAW_HOOK(sub_8227F3B0) // IDA: exact batched private texture rectangle wrapper
REFII_RENDERER_DRAW_HOOK(sub_822A0A78) // IDA: exact private texture rectangle wrapper; 64-bit rect arg
REFII_RENDERER_DRAW_HOOK(sub_822A0B08) // IDA: exact private texture rectangle wrapper; source object field
REFII_RENDERER_DRAW_HOOK(sub_822827B8) // IDA: exact private texture rectangle wrapper; global source + stack rects
REFII_RENDERER_DRAW_HOOK(sub_822C40C8) // IDA: exact private texture rectangle wrapper; source object
REFII_RENDERER_DRAW_HOOK(sub_822C4290) // IDA: exact private texture rectangle wrapper; title/private global source
REFII_RENDERER_DRAW_HOOK(sub_82A6E698) // IDA: exact private texture rectangle wrapper; postprocess source field
REFII_RENDERER_DRAW_HOOK(sub_82A7FDE0) // IDA: exact private texture rectangle wrapper; shared source global
REFII_RENDERER_DRAW_HOOK(sub_82A7FF20) // IDA: exact batched private texture rectangle wrapper; shared source global
REFII_RENDERER_DRAW_HOOK(sub_82A82FE8) // IDA: exact private texture rectangle wrapper; render object source field
REFII_RENDERER_DRAW_HOOK(sub_82A6A458) // IDA: exact private texture rectangle wrapper; shared source global
REFII_RENDERER_DRAW_HOOK(sub_82A6A598) // IDA: exact private texture rectangle wrapper; postprocess source global
REFII_RENDERER_DRAW_HOOK(sub_82A6AA90) // IDA: exact private texture rectangle wrapper; postprocess source global
REFII_RENDERER_DRAW_HOOK(sub_8228AB70) // IDA: exact setup/private texture rectangle wrapper; global source
REFII_RENDERER_DRAW_HOOK(sub_8228ACB8) // IDA: exact setup/private texture rectangle wrapper
REFII_RENDERER_DRAW_HOOK(sub_82242108) // IDA: exact title/private texture sibling wrapper
REFII_RENDERER_DRAW_HOOK(sub_82206888) // IDA: private texture/video rectangle draw
REFII_RENDERER_DRAW_HOOK(sub_8221AE18) // Lumen: D3D::SetPending_HiZEnable
REFII_RENDERER_DRAW_HOOK(sub_8221B010) // Lumen: D3D::SetPending_Shaders
REFII_RENDERER_DRAW_HOOK(sub_8221C7D8) // Lumen: D3D::SetPending_RenderStates
REFII_RENDERER_DRAW_HOOK(sub_8221D1B0) // Lumen: D3D::SetPending_FetchConstants
// IDA 2026-07-25: the ONLY two draw emitters that bypass all eight hooked D3D
// draw roots. Both write PM4 DRAW_INDX packets straight into the ring, with a
// VGT_DRAW_INITIATOR PRIM_TYPE of 0x12 (quad patch) and VGT_OUTPUT_PATH_CNTL
// set to TESSELLATE, so Refii saw ZERO draws from terrain or water.
//   sub_82207AF8 D3D_EmitDrawIndexedPackets_INFERRED   terrain "bg patch %d"
//   sub_8221C898 D3D_EmitDrawPrimitivePackets_INFERRED water heightfield
// CAPTURE-ONLY: neither symbol appears in any IsNativeRenderWorkHook list, so
// the guest body still runs and nothing about the rendered frame changes.
REFII_RENDERER_DRAW_HOOK(sub_82207AF8) // IDA: direct PM4 terrain patch emitter
REFII_RENDERER_DRAW_HOOK(sub_8221C898) // IDA: direct PM4 water patch emitter
REFII_RENDERER_DRAW_HOOK(sub_822154B0) // Lumen: D3D::SetPending_Predicated
REFII_RENDERER_DRAW_HOOK(sub_822655F0) // Lumen: D3DDevice_SetPredication
REFII_RENDERER_DRAW_HOOK(sub_822AF568) // Lumen: D3DDevice_SetScreenExtentQueryMode
REFII_RENDERER_GENERAL_HOOK(sub_8222BFA0) // Lumen: D3D::SetLiteralShaderConstants
REFII_RENDERER_GENERAL_HOOK(sub_82BA76F8) // Lumen: D3D::PM4LoadShaderConstantsImmediate
REFII_RENDERER_GENERAL_HOOK(sub_82BA77D0) // Lumen: D3D::PM4SetTextureFetchConstant
// sub_8221A880 D3DDevice_SetVertexShaderConstantF hook REMOVED 2026-07-29:
// EmulateFableShaderConstantState byte-for-byte reproduced the guest body
// (VMX copy into device+0x780 + dirty-mask OR). Top hook by volume (262k
// calls/run) doing duplicate work; the GTL reads constants from device
// memory at draw.
REFII_RENDERER_GENERAL_HOOK(sub_82214E30) // IDA: D3DDevice_SetPixelShaderConstantF
REFII_RENDERER_GENERAL_HOOK(sub_821C6708) // Lumen: D3DDevice_SetVertexShaderConstantB
REFII_RENDERER_GENERAL_HOOK(sub_82B991B0) // Lumen: D3DDevice_SetVertexShaderConstantI
REFII_RENDERER_GENERAL_HOOK(sub_8226E120) // Lumen: D3DDevice_SetPixelShaderConstantB
REFII_RENDERER_GENERAL_HOOK(sub_82B99208) // Lumen: D3DDevice_SetPixelShaderConstantI
REFII_RENDERER_DRAW_HOOK(sub_8221E4A0) // IDA: dispatch queued GPU state packets
REX_EXTERN(sub_8221E550) {
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "sub_8221E550", refii::renderer::RendererHookKind::kDraw);
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  refii::renderer::OnRendererStateDispatchHook(args, base);
  if (refii::renderer::ShouldRunGuestRendererHook(
          "sub_8221E550", refii::renderer::RendererHookKind::kDraw, args,
          base, hook_desc)) {
    __imp__sub_8221E550(ctx, base);
    refii::renderer::OnRendererGuestHookExit(
        "sub_8221E550", refii::renderer::RendererHookKind::kDraw, args, ctx,
        base, hook_desc);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "sub_8221E550", refii::renderer::RendererHookKind::kDraw, args, ctx,
        base, hook_desc);
  }
}
REFII_RENDERER_DRAW_HOOK(sub_8221F348) // IDA: process one queued GPU state packet
REFII_RENDERER_DRAW_HOOK(sub_8221F2E0) // IDA: flush pending per-context GPU state
REFII_RENDERER_DRAW_HOOK(sub_821FC540) // IDA: Fable transient render-target release queue
REFII_RENDERER_DRAW_HOOK(sub_82BA25C0) // Lumen: D3D::CDevice::AddCommandsToPrimaryBuffer
REFII_RENDERER_GENERAL_HOOK(sub_82BA6508) // Lumen: D3D::InitializePresentationParameters

// NATIVE RENDER RESOLUTION — DELIBERATELY *NOT* DONE GUEST-SIDE.
//
// A guest-side patch was written here on 2026-07-29 and REMOVED the same day.
// It worked in the narrow sense: hooking sub_82B6F6C0 and rewriting desc+8 /
// desc+0xC does reach all three consumers of the render size (the present
// params, the saved template dword_83360368, and the global qword_834A5FE8 that
// every scene render-target allocation reads at 0x82B6F9A0/A4).
//
// It is the WRONG ARCHITECTURE, because making the GUEST render larger drags in
// every Xbox 360 constraint we replaced the guest renderer to escape:
//   * the EDRAM tiling-preset table at 0x834BE010 is hardcoded 1280x720 with
//     per-MSAA split rects and has no 1080p entry (static init sub_83293788,
//     consumed by sub_82A4CCD8 -- the same function that allocates the scene
//     RTs), so tile rects and predication would still describe a 720p surface;
//   * 1920x1088 colour alone is 8.36MB against 10MB of EDRAM, and colour+depth
//     is 16.7MB, so on real hardware 1080p REQUIRES tiling -- which is exactly
//     why no preset exists;
//   * the guest aligns to a 160-pixel EDRAM tile it does not have to obey here.
//
// We own the renderer. The guest coordinate space stays 1280x720 and never
// learns otherwise; OUR host targets are allocated at output resolution and
// guest coordinates are scaled uniformly on the way in. That keeps every guest
// assumption -- tiling, predication, resolve rects, the preset table -- exactly
// as the binary expects, and confines the resolution decision to our side.
//
// See DesiredFableNativeRenderExtent / RendererInternalScale and the guest->host
// viewport scaling already present in the GTL executor (fable_direct_renderer.cpp
// vp_scale_x/y) and the observer path (ScaleGuestViewportToCurrentDrawSurface).
REFII_RENDERER_GENERAL_HOOK(sub_821F5028) // IDA: inline Fable RT descriptor builder
REFII_RENDERER_RESOURCE_HOOK(sub_821FB650) // IDA: inline Fable texture/fetch descriptor builder
REFII_RENDERER_RESOURCE_HOOK(sub_821FB968) // IDA: XGRAPHICS::SetBaseTextureHeader
REFII_RENDERER_RESOURCE_HOOK(sub_821FBB78) // Lumen: XGSetTextureHeader
REFII_RENDERER_RESOURCE_HOOK(rex_XGOffsetBaseTextureAddress) // Lumen: XGOffsetBaseTextureAddress
// THE SINGLE-LEVEL SIBLING WAS NEVER HOOKED (2026-08-04).
//
// Texture_InitHeaderWrapper (0x821FBD90) builds a render target BY HAND over an
// ~18.5 MB XPhysicalAlloc scratchpad (bump-allocated by sub_8225E340) and then
// stamps the base address into the header through one of two calls, chosen by
// level count:
//   /*0x821fbe98*/ bl XGOffsetResourceAddress      ; Levels == 1
//   /*0x821fbea8*/ bl XGOffsetBaseTextureAddress   ; Levels > 1
// The wrapper passes Levels = 1 for every scratchpad render target, so they ALL
// take the first branch — and only the second was hooked. There is no
// CreateTexture anywhere on this path (the sibling SURFACE at record+0x14 does
// get a real D3DDevice_CreateSurface /*0x821fc950*/; the TEXTURE at record+0x04,
// which is the Resolve destination, gets nothing), so a call-observing backend
// saw these textures come into existence never.
//
// Consequence, measured run 603: the last draw of every frame samples fetch
// slot 13 (shader binding record 252, g_ShaderBindingTable+0xBD0) and our
// resolver reports `slot=13 tracked=0 image=0 ready=0` 287 times — an unbacked
// texture alpha-blended over the finished frame.
//
// TWO separate features land on this one gap, which is why it is worth the hook
// rather than a special case:
//   * g_PostProcessResolveTexture (0x8331AB30) — the half-res screen grab that
//     Render_PostProcessCompositePass (0x82A6AF88) blends back through a stencil
//     mask.
//   * the tree/foliage impostor targets built by sub_82268C70 — square powers of
//     two in [64,256], consumed by Render_LodMeshDraw (0x821F16B0) at binding
//     records 175/176.
//
// This is the same structural shape as the tone curve: real guest content with
// no observable creation or upload event. See
// [[refii-tone-curve-has-no-upload-event]].
REFII_RENDERER_RESOURCE_HOOK(rex_XGOffsetResourceAddress) // Lumen: XGOffsetResourceAddress
REFII_RENDERER_RESOURCE_HOOK(sub_8300BD38) // Lumen: XGSetVertexBufferHeader
REFII_RENDERER_RESOURCE_HOOK(sub_8300BDD0) // Lumen: XGSetIndexBufferHeader
REFII_RENDERER_RESOURCE_HOOK(sub_8300BE60) // Lumen: XGSetConstantBufferHeader
REFII_RENDERER_RESOURCE_HOOK(sub_8300BEE0) // Lumen: XGSetLineTextureHeader
REFII_RENDERER_RESOURCE_HOOK(sub_8300BF48) // Lumen: XGSetCubeTextureHeader
REFII_RENDERER_GENERAL_HOOK(sub_821F52A0) // Lumen: XGSurfaceSize
REFII_RENDERER_RESOURCE_HOOK(sub_8300C290) // Lumen: XGRAPHICS__TileSurface
REFII_RENDERER_RESOURCE_HOOK(sub_8300CB40) // Lumen: j_XGRAPHICS__TileSurface
REFII_RENDERER_RESOURCE_HOOK(sub_8300CB48) // Lumen: XGTileTextureLevel
REFII_RENDERER_RESOURCE_HOOK(sub_83010E60) // Lumen: XGRAPHICS__TileVolume
REFII_RENDERER_RESOURCE_HOOK(sub_830117B8) // Lumen: XGTileVolumeTextureLevel
REFII_RENDERER_RESOURCE_HOOK(sub_8300C660) // Lumen: XGRAPHICS__UntileSurface
REFII_RENDERER_RESOURCE_HOOK(sub_8300CDA0) // Lumen: j_XGRAPHICS__UntileSurface
REFII_RENDERER_RESOURCE_HOOK(sub_8300CDA8) // Lumen: XGUntileTextureLevel
REFII_RENDERER_RESOURCE_HOOK(sub_830112F0) // Lumen: XGRAPHICS__UntileVolume
REFII_RENDERER_RESOURCE_HOOK(sub_83011AE0) // Lumen: XGUntileVolumeTextureLevel
// sub_8300F7E8 (XGCopyVolume) and sub_83012888 (XGEndianSwapSurface) REMOVED
// 2026-07-30: kResource passthroughs matched by NO CanEmulate* predicate and
// no Is*Hook list, so ShouldRun always ran the guest — and, being kResource,
// every call also paid LogFableUnsupportedAdapterShape's mutex + string key
// build for a diagnostic that never concerned them. No GTL case, no target.
REFII_RENDERER_RESOURCE_HOOK(sub_8300F928) // Lumen: XGCompressSurface
REFII_RENDERER_RESOURCE_HOOK(sub_83013A70) // Lumen: XG_D3DXTex::CBlt::BltCopy
// sub_831C51F8 (XGRAPHICS__DoMemExport) REMOVED 2026-07-30: zero live
// consumers (MemExport observation keys on the two draw-path emitters
// sub_8222EA20/sub_8227B548, never this XDK helper). Pure overhead.
REFII_RENDERER_DRAW_HOOK(sub_82BA7B28) // IDA: Fable Pix/video conversion draw chain
REFII_RENDERER_DRAW_HOOK(sub_82BA7898) // Lumen: D3D::PixEnableVideoPlayback
REFII_RENDERER_DRAW_HOOK(sub_82BA7928) // Lumen: D3D::PixDisableVideoPlayback
// sub_82BA7988 (D3D::PixTimeSinceVBlank) REMOVED 2026-07-30: unlike its
// neighbours it is NOT in kFableVideoStateHooks — zero consumers anywhere, so
// the video-feed keep rule does not apply. Pure overhead on a timing getter.
REFII_RENDERER_DRAW_HOOK(sub_82BA79D0) // Lumen: D3D::CodecSetState
REFII_RENDERER_DRAW_HOOK(sub_82BA8928) // Lumen: D3D::PixDecodeVideoFrame
REFII_RENDERER_DRAW_HOOK(sub_822866E0) // Lumen: D3D::CDevice::AddCallsToPrimaryBuffer
REFII_RENDERER_DRAW_HOOK(sub_822868C8) // Lumen: D3D::CDevice::QueueIndirectBuffer
REFII_RENDERER_DRAW_HOOK(sub_8228EF80) // Lumen: D3D::SetPending_Split
REFII_RENDERER_DRAW_HOOK(sub_821E8D38) // Lumen: D3D::CDevice::KickOffSegment(void)
REFII_RENDERER_DRAW_HOOK(sub_821E8EC0) // Lumen: D3D::CDevice::KickOff(void)
REFII_RENDERER_GENERAL_HOOK(sub_821D3570) // Lumen: D3D::PatchVertexShaderToMatchVertexDeclaration
REFII_RENDERER_GENERAL_HOOK(sub_821E0220) // Lumen: D3D::IncrementalShaderPatchAndLoad
REFII_RENDERER_GENERAL_HOOK(sub_821F7650) // Lumen: D3D::DirectShaderPatch
REFII_RENDERER_GENERAL_HOOK(sub_82BA3D88) // Lumen: D3D::PatchVertexShaderToMatchPixelShader
REFII_RENDERER_GENERAL_HOOK(sub_831CFA58) // Lumen: XGRAPHICS::ProcessVertexInputPatch
REFII_RENDERER_DRAW_HOOK(sub_821BFDF0) // Lumen: D3DDevice_SetRenderState_ZFunc
REFII_RENDERER_DRAW_HOOK(sub_821C91C8) // Lumen: D3DDevice_SetRenderState_ColorWriteEnable
REFII_RENDERER_DRAW_HOOK(sub_821DCAA0) // Lumen: D3DDevice_SetRenderState_AlphaBlendEnable
REFII_RENDERER_DRAW_HOOK(sub_821DC158) // Lumen: D3DDevice_SetRenderState_SrcBlend
REFII_RENDERER_DRAW_HOOK(sub_821E56F8) // Lumen/IDA: D3DDevice_SetRenderState_StencilEnable
REFII_RENDERER_DRAW_HOOK(sub_821F0490) // Lumen: D3DDevice_SetRenderState_DestBlend
REFII_RENDERER_DRAW_HOOK(sub_82215488) // Lumen: D3DDevice_SetRenderState_AlphaTestEnable
REFII_RENDERER_DRAW_HOOK(sub_821B1330) // IDA: D3DDevice_SetRenderState_AlphaFunc
REFII_RENDERER_DRAW_HOOK(sub_821B1350) // IDA: D3DDevice_SetRenderState_StencilWriteMask
REFII_RENDERER_DRAW_HOOK(sub_82225E08) // IDA: D3DDevice_SetRenderState_AlphaRef
REFII_RENDERER_DRAW_HOOK(sub_8221D190) // IDA: packed blend control callback
REFII_RENDERER_DRAW_HOOK(sub_8226AE78) // IDA: packed depth mode callback
REFII_RENDERER_DRAW_HOOK(sub_821E0BF8) // IDA: packed depth mode callback
REFII_RENDERER_DRAW_HOOK(sub_82272E50) // IDA: D3DDevice_SetRenderState_PixelCenterOffset
REFII_RENDERER_DRAW_HOOK(sub_8220B470) // Lumen: D3DDevice_SetRenderState_CullMode
REFII_RENDERER_DRAW_HOOK(sub_82286D78) // Lumen: D3DDevice_SetRenderState_ZEnable
REFII_RENDERER_DRAW_HOOK(sub_8229F790) // Lumen: D3DDevice_SetRenderState_HiZWriteEnable
REFII_RENDERER_DRAW_HOOK(sub_822C0968) // Lumen: D3DDevice_SetRenderState_BlendOp
REFII_RENDERER_DRAW_HOOK(sub_8221E410) // Lumen: D3DDevice_SetRenderState_HighPrecisionBlendEnable
REFII_RENDERER_DRAW_HOOK(sub_8226B058) // IDA: D3DDevice_SetRenderState_ZWriteEnable
REFII_RENDERER_DRAW_HOOK(sub_821887D8) // Lumen: D3DDevice_SetRenderState_StencilFail
REFII_RENDERER_DRAW_HOOK(sub_82188800) // Lumen: D3DDevice_SetRenderState_StencilZFail
REFII_RENDERER_DRAW_HOOK(sub_8229ABB8) // IDA: D3DDevice_SetRenderState_StencilPass
REFII_RENDERER_DRAW_HOOK(sub_822A6180) // IDA: D3DDevice_SetRenderState_StencilFunc
REFII_RENDERER_DRAW_HOOK(sub_822B4008) // Lumen: D3D::SetPending_ClipPlanes
REFII_RENDERER_DRAW_HOOK(sub_822C08E8) // IDA: D3DDevice_SetRenderState_StencilMask
REFII_RENDERER_DRAW_HOOK(sub_82239910) // IDA: D3DDevice_SetRenderState_StencilRef
REFII_RENDERER_DRAW_HOOK(sub_82B96EF0) // Lumen: D3DDevice_SetRenderState_BlendOpAlpha
REFII_RENDERER_DRAW_HOOK(sub_82B96F60) // Lumen: D3DDevice_SetRenderState_SrcBlendAlpha
REFII_RENDERER_DRAW_HOOK(sub_82B96FD0) // Lumen: D3DDevice_SetRenderState_DestBlendAlpha
REFII_RENDERER_DRAW_HOOK(sub_82B97040) // Lumen: D3DDevice_SetRenderState_SeparateAlphaBlendEnable
REFII_RENDERER_DRAW_HOOK(sub_82B97448) // Lumen: D3DDevice_SetRenderState_ClipPlaneEnable
REFII_RENDERER_DRAW_HOOK(sub_82B97BB0) // Lumen: D3DDevice_SetSamplerState_MinFilterZ
REFII_RENDERER_DRAW_HOOK(sub_82B97C90) // Lumen: D3DDevice_SetSamplerState_MagFilterZ
REFII_RENDERER_DRAW_HOOK(sub_82B97D48) // Lumen: D3DDevice_SetSamplerState_SeparateZFilterEnable
REFII_RENDERER_GENERAL_HOOK(sub_82B981D0) // Lumen: D3DDevice_GetRenderTarget
REX_EXTERN(sub_82B668D0) {
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "sub_82B668D0", refii::renderer::RendererHookKind::kDraw);
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  refii::renderer::OnRendererPassQueueHook(args, base);
  if (refii::renderer::ShouldRunGuestRendererHook(
          "sub_82B668D0", refii::renderer::RendererHookKind::kDraw, args,
          base, hook_desc)) {
    __imp__sub_82B668D0(ctx, base);
    refii::renderer::OnRendererPassQueueSnapshot(args, base);
    refii::renderer::OnRendererGuestHookExit(
        "sub_82B668D0", refii::renderer::RendererHookKind::kDraw, args, ctx,
        base, hook_desc);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "sub_82B668D0", refii::renderer::RendererHookKind::kDraw, args, ctx,
        base, hook_desc);
  }
}
REFII_RENDERER_DRAW_HOOK(sub_822869A0) // Lumen: D3DDevice_SetRenderTarget
REFII_RENDERER_DRAW_HOOK(sub_82264590) // Lumen: D3DDevice_SetDepthStencilSurface
REFII_RENDERER_DRAW_HOOK(sub_822A09A0) // Lumen: D3D::SetColorClear
REFII_RENDERER_DRAW_HOOK(sub_821E2FE0) // Lumen: D3DDevice_ClearF
REFII_RENDERER_DRAW_HOOK(sub_821EF830) // Lumen: D3D::ClearRect
REFII_RENDERER_DRAW_HOOK(sub_821F9D00) // Lumen: D3D::SetViewport
REFII_RENDERER_DRAW_HOOK(sub_821F9918) // Lumen: D3D::SetSurfaceClip
REFII_RENDERER_DRAW_HOOK(sub_821F9F58) // Lumen: D3DDevice_SetViewport
REFII_RENDERER_DRAW_HOOK(sub_821B6EE0) // Lumen: D3DDevice_SetTexture
REFII_RENDERER_DRAW_HOOK(sub_821F9C08) // Lumen: D3DDevice_SetScissorRect
REFII_RENDERER_DRAW_HOOK(sub_8221C3E8) // Mapped: DrawPrimitive
REFII_RENDERER_DRAW_HOOK(sub_8221DFC0) // Mapped: DrawIndexedPrimitive
REFII_RENDERER_DRAW_HOOK(sub_82265CD8) // Lumen: D3DDevice_DrawVerticesUP
REFII_RENDERER_DRAW_HOOK(rex_lhRenderer_EndScene)
REFII_RENDERER_DRAW_HOOK(sub_82BA23E8) // Lumen: D3DDevice_UnsetAll
REFII_RENDERER_DRAW_HOOK(sub_82BA34D8) // Lumen: D3DDevice_Swap
REFII_RENDERER_DRAW_HOOK(sub_82BA32F8) // Lumen: D3D::ComplexSwap
REFII_RENDERER_DRAW_HOOK(sub_82BA2DB8) // Lumen: D3D::SwapCallback
REFII_RENDERER_DRAW_HOOK(sub_82BA2F68) // Lumen: D3D::SynchronizeToPresentationInterval
REX_EXTERN(sub_82B87330) { // IDA: texture-pool block free (asserts block IN pool)
  REX_FUNC_PROLOGUE();
  // POOL-FREE GUARD (2026-08-02, runs 521/524/527). The guest body asserts
  // AI_cl_Stack_bIsOnStack(g_TexturePoolActive, block) with a __debugbreak
  // (twi 31,r0,22) and, when our runtime presses through it, UNLINKS the
  // foreign block into the active pool's 32-bucket free list — garbage node
  // links that make the next Render_PhysicalPoolAlloc first-fit walk cycle
  // forever holding the pool CS + GpuAccessLock (the run-521/524/527 render-
  // thread death at the level-load boundary: 45 Bink loading-movie plane
  // blocks freed by 0xF0-form base after a lag-driven fallback allocated
  // them outside the pool). Real hardware halts on the assert; the assert's
  // own intent is "a foreign free must not touch the pool". Honor exactly
  // that host-side: leak the foreign block, keep the pool intact, keep the
  // thread alive. In-bounds frees run the guest body unchanged. Bounds are
  // written once by the pool ctor (sub_82BEB6A8) and never move (IDA), so
  // the unlocked read here cannot race a legitimate mutation.
  constexpr uint32_t kTexturePoolActiveGlobal = 0x8349176Cu;
  const uint32_t block = ctx.r3.u32;
  const uint32_t pool = LoadGuestU32(base, kTexturePoolActiveGlobal);
  if (block != 0 && pool != 0) {
    const uint32_t pool_begin = LoadGuestU32(base, pool);
    const uint32_t pool_end = LoadGuestU32(base, pool + 4u);
    if (pool_begin != 0 && (block < pool_begin || block >= pool_end)) {
      static std::atomic<uint32_t> foreign_pool_frees{0};
      const uint32_t count = ++foreign_pool_frees;
      if (count <= 64u || (count & 1023u) == 0u) {
        REXLOG_WARN(
            "Refii renderer foreign texture-pool free LEAKED #{} "
            "block=0x{:08X} pool=[0x{:08X},0x{:08X}) lr=0x{:08X}",
            count, block, pool_begin, pool_end, (uint32_t)ctx.lr);
      }
      return;
    }
  }
  __imp__sub_82B87330(ctx, base);
}

REX_EXTERN(sub_8221EAF0) { // streamed-texture wrapper touch (per material use)
  REX_FUNC_PROLOGUE();
  // Native/xenia: pure pass-through. The census + pool-stream diagnostics
  // and the residency-cursor recapture below are Plume observer machinery;
  // in xenia they only burned time on the hot material path and flooded the
  // logs (the info spam the user flagged in xenia runs).
  if (!refii::renderer::RendererHooksEnabled()) {
    __imp__sub_8221EAF0(ctx, base);
    return;
  }
  const uint32_t wrapper = ctx.r3.u32;
  // Touch-cost census (title scroll campaign; remove when done).
  static std::atomic<uint32_t> touch_calls{0};
  static std::atomic<uint32_t> touch_notifies{0};
  static std::atomic<int64_t> touch_total_us{0};
  const auto touch_t0 = std::chrono::steady_clock::now();
  const auto touch_census_exit = [&](bool notified) {
    const int64_t us = std::chrono::duration_cast<std::chrono::microseconds>(
                           std::chrono::steady_clock::now() - touch_t0)
                           .count();
    touch_total_us.fetch_add(us, std::memory_order_relaxed);
    if (notified) {
      touch_notifies.fetch_add(1, std::memory_order_relaxed);
    }
    const uint32_t n = ++touch_calls;
    if ((n & 511u) == 0) {
      REXLOG_INFO("Refii wrapper-touch census calls={} notifies={} total_us={}",
                  n, touch_notifies.load(std::memory_order_relaxed),
                  static_cast<long long>(
                      touch_total_us.load(std::memory_order_relaxed)));
      // Pool-streaming stall diagnosis: manager singleton (0x8349FA0C),
      // in-flight request counter (0x83496E78, gate is <4), fail-flag word
      // (0x83496E30 contains byte_83496E33), plus this wrapper's streaming
      // fields (skip byte +129, cursor +84, pending +132, limit +160).
      REXLOG_INFO(
          "Refii pool-stream globals mgr=0x{:08X} inflight={} "
          "failword=0x{:08X} wrapper=0x{:08X} skip={} cursor=0x{:08X} "
          "pending={} limit={}",
          LoadGuestU32(base, 0x8349FA0Cu), LoadGuestU32(base, 0x83496E78u),
          LoadGuestU32(base, 0x83496E30u), wrapper,
          (LoadGuestU32(base, wrapper + 128u) >> 16) & 0xFFu,
          LoadGuestU32(base, wrapper + 84u), LoadGuestU32(base, wrapper + 132u),
          LoadGuestU32(base, wrapper + 160u));
    }
  };
  __imp__sub_8221EAF0(ctx, base);
  // Streamed texel bytes land via async file I/O with no D3D call for the
  // observer to see.
  //
  // PREMISE CORRECTION (run 132, 2026-07-22): the parenthetical here used to
  // read "our sub_82A7B148/sub_82A7AF68 wrappers never fire". That is false —
  // sub_82A7B148 dispatched 14 times in run 132 (sub_82A7AF68 is the one at
  // 0), and sub_8221EAF0 reaches it by an ordinary `bl` (code xref 0x8221ecdc),
  // not inlined. The async-I/O completion itself still has no D3D call to
  // intercept, which is the real and unchanged justification for using the
  // touch below. Only INLINED callees are genuinely invisible to us.
  //
  // The touch is the render
  // path's own residency check, so use it: the residency cursor at
  // wrapper+84 is the guest's own record of how much content has streamed
  // in (it leaves the 0x7FFFFFFF sentinel on first residency and moves as
  // further mip levels land). Re-capture texture uploads on EVERY cursor
  // advance — first-sight-only refresh left atlases blank when their pool
  // I/O completed after the wrapper first turned resident, and eviction/
  // re-stream cycles re-signal here naturally.
  const uint32_t residency_cursor = LoadGuestU32(base, wrapper + 84u);
  static std::mutex resident_mutex;
  static std::unordered_map<uint32_t, uint32_t> resident_cursor_seen;
  // Touch-to-resident latency: the title panel renderer skips a panel until
  // its wrapper is resident, so streaming latency shows on screen as a
  // transient hole at panel handoffs. Record when a wrapper is first touched
  // while NON-resident and report the wait once it turns resident.
  static std::unordered_map<uint32_t,
                            std::chrono::steady_clock::time_point>
      first_nonresident_touch;
  if (residency_cursor != 0x7FFFFFFFu &&
      LoadGuestU8(base, wrapper + 129u) == 0) {
    bool advanced = false;
    double waited_ms = -1.0;
    {
      std::lock_guard<std::mutex> lock(resident_mutex);
      auto [it, inserted] =
          resident_cursor_seen.try_emplace(wrapper, residency_cursor);
      if (!inserted && it->second != residency_cursor) {
        it->second = residency_cursor;
        advanced = true;
      } else {
        advanced = inserted;
      }
      if (inserted) {
        auto wait_it = first_nonresident_touch.find(wrapper);
        if (wait_it != first_nonresident_touch.end()) {
          waited_ms = std::chrono::duration<double, std::milli>(
                          std::chrono::steady_clock::now() - wait_it->second)
                          .count();
          first_nonresident_touch.erase(wait_it);
        }
      }
    }
    if (waited_ms >= 0.0) {
      static std::atomic<uint32_t> logged_resident_waits{0};
      const uint32_t count = ++logged_resident_waits;
      if (count <= 48u) {
        REXLOG_INFO("Refii renderer streamed wrapper resident after {:.0f} ms "
                    "wait #{} wrapper=0x{:08X}",
                    waited_ms, count, wrapper);
      }
    }
    if (advanced) {
      refii::renderer::NotifyFableStreamedWrapperResident(wrapper, base);
    }
    touch_census_exit(advanced);
    return;
  } else if (residency_cursor == 0x7FFFFFFFu) {
    {
      std::lock_guard<std::mutex> lock(resident_mutex);
      first_nonresident_touch.try_emplace(wrapper,
                                          std::chrono::steady_clock::now());
    }
    // Residency LOSS: the pool evicted this wrapper (cursor back at the
    // sentinel). Rate-limited log to answer whether the title panel-handoff
    // gap (transient black panel mid-cycle) is premature eviction of the
    // outgoing panel or late arrival of the incoming one.
    bool lost = false;
    {
      std::lock_guard<std::mutex> lock(resident_mutex);
      auto it = resident_cursor_seen.find(wrapper);
      if (it != resident_cursor_seen.end()) {
        resident_cursor_seen.erase(it);
        lost = true;
      }
    }
    if (lost) {
      static std::atomic<uint32_t> logged_residency_losses{0};
      const uint32_t count = ++logged_residency_losses;
      if (count <= 48u) {
        REXLOG_INFO(
            "Refii renderer streamed wrapper residency LOST #{} "
            "wrapper=0x{:08X} pending={} async=0x{:08X}",
            count, wrapper,
            static_cast<int32_t>(LoadGuestU32(base, wrapper + 132u)),
            LoadGuestU32(base, wrapper + 76u));
      }
    }
  }
  touch_census_exit(false);
}

REX_EXTERN(rex_lhRenderer_RenderFrame) { // IDA: Fable outer frame owner
  REX_FUNC_PROLOGUE();
  static const refii::renderer::FableHookSymbolDesc *hook_desc =
      refii::renderer::ResolveFableHookSymbolDesc(
          "rex_lhRenderer_RenderFrame", refii::renderer::RendererHookKind::kDraw);
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgsFor(hook_desc, ctx);
  constexpr refii::renderer::RendererHookKind kind =
      refii::renderer::RendererHookKind::kDraw;
  refii::renderer::OnRendererGuestHookEnter("rex_lhRenderer_RenderFrame", kind,
                                            args, base, hook_desc);
  if (refii::renderer::ShouldRunGuestRendererHook(
          "rex_lhRenderer_RenderFrame", kind, args, base, hook_desc)) {
    const uint32_t previous_frame_lock = t_fable_render_frame_lock;
    t_fable_render_frame_lock = args.r3;
    __imp__rex_lhRenderer_RenderFrame(ctx, base);
    t_fable_render_frame_lock = previous_frame_lock;
    refii::renderer::OnRendererGuestHookExit("rex_lhRenderer_RenderFrame", kind,
                                             args, ctx, base, hook_desc);
  } else {
    refii::renderer::OnRendererGuestHookSuppressedForPlume(
        "rex_lhRenderer_RenderFrame", kind, args, ctx, base, hook_desc);
  }
}
// rex_lhRenderer_PrepareFrame / rex_lhRenderer_SetupFrame REMOVED 2026-07-30:
// SymbolAddress() maps neither rex_ name (not in the GTL's explicit shim
// list), so every call was a GTL "UNMAPPED root" counter bump and nothing
// else — no target entry, no list membership, guest always ran.
REFII_RENDERER_DRAW_HOOK(sub_82B98248) // Lumen: D3DDevice_SetGammaRamp
REFII_RENDERER_DRAW_HOOK(sub_82B98318) // Exact Unleashed stub: D3DDevice_SetGammaRamp
REFII_RENDERER_DRAW_HOOK(sub_82BA2748) // Lumen: D3DDevice_BlockUntilIdle
// The three entries below are complete MemExport (GPU stream-out) compute
// passes that bracket their own D3DDevice_BeginExport/EndExport pair -- they
// are not scene-submit, title-copy or PIX/video callers. See
// renderer_hook_targets.h for the per-pass IDA evidence.
// IDA: landscape/water adaptive tessellation MemExport pass (1 stream, bind
// 313, k_32_32_32_32_FLOAT, 1024 B = 64 elements per cell)
REFII_RENDERER_DRAW_HOOK(sub_8222EA20)
// IDA: skinned-mesh MemExport pass (3 streams, binds 314/315/316; bone stream
// on stream 3; 64-byte submesh records)
REFII_RENDERER_DRAW_HOOK(sub_8227B548)
// IDA: decal-bake MemExport pass (3 streams; 289 points = 17x17 lattice; 50-slot
// decal ring unk_8349FBD0, stride 96; enclosing PIX scope "Decal Render")
REFII_RENDERER_DRAW_HOOK(sub_82A63310)
REFII_RENDERER_DRAW_HOOK(sub_82BA83C0) // IDA: D3D::PixRenderVideoFrame
REFII_RENDERER_DRAW_HOOK(sub_82BA8EF8) // IDA: Fable Pix/video frame packet builder
// sub_82BA8E58 (D3D::FrameDone) REMOVED 2026-07-30: zero consumers — frame
// boundary sealing keys on rex_lhRenderer_RenderFrame and sub_82BA34D8, never
// on this symbol. Pure overhead.
REFII_RENDERER_DRAW_HOOK(sub_82BA95E0) // Lumen: D3D::PixCaptureVideoFrame
REFII_RENDERER_DRAW_HOOK(sub_82BAA848) // Lumen: D3D::PixSyncronizeToPresentationInterval
REFII_RENDERER_DRAW_HOOK(sub_82BAA920) // Lumen: D3D::PixInitializePerfMemory
REFII_RENDERER_GENERAL_HOOK(sub_82BAE960) // Lumen: D3D::InitializeDefaultScalerParameters
REFII_RENDERER_DRAW_HOOK(sub_82BAEA88) // Lumen: D3D::SetupScaler
REFII_RENDERER_DRAW_HOOK(sub_82BAEC38) // Lumen: D3DDevice_SetScalerParameters
REFII_RENDERER_GENERAL_HOOK(sub_8217F3E8) // Lumen: D3DDevice_QueryBufferSpace
REFII_RENDERER_GENERAL_HOOK(sub_821F6270) // Lumen: D3DDevice_IsFencePending
REFII_RENDERER_GENERAL_HOOK(sub_82242668) // Lumen: D3D::CDevice::BlockOnFence
// sub_82298630 (D3D::CDevice::SetFence) REMOVED 2026-07-30: zero consumers.
// It is NOT in IsFableGuestCommandSubmissionHook (BlockOnFence/IsFencePending
// are; SetFence never was), so it always ran the guest and fed nothing —
// fence retirement is driven from the KickOff exits in the GTL, not from
// SetFence. Per-fence call rate made this measurable pure overhead.
// WAIT-ROOT PROBE (2026-07-28, temporary). These four are the guest GPU waits
// that were NOT hooked, so their real spin loops run. Each waits on a progress
// value our port has never published, and D3D_CBlocker_Check's watchdog cannot
// fire (it compares KTHREAD+0x58, which is `uint8_t unk_58[4]` in ReXGlue and
// is never written), so a wait whose condition is unmet spins forever WITHOUT
// yielding. Hooked here as PASSTHROUGH only — kGeneral is not in any
// IsNativeRenderWorkHook predicate, so the guest body still runs and behaviour
// is unchanged. The enter/exit counters in renderer_runtime.cpp turn these into
// a stuck-detector: a persistent enter>exit imbalance names the parked wait.
REFII_RENDERER_GENERAL_HOOK(sub_82206400) // W2 D3D::CDevice::BlockOnSecondaryPosition
REFII_RENDERER_GENERAL_HOOK(sub_82286618) // W3 D3D::CDevice::BlockOnPrimaryRange
REFII_RENDERER_GENERAL_HOOK(sub_82BA4D80) // W5 D3D::XpsConfigureForWork
REFII_RENDERER_GENERAL_HOOK(sub_82BA5818) // W6 D3D::DispatchCompletion
REFII_RENDERER_GENERAL_HOOK(sub_82BA2218) // Lumen: D3DDevice_Release
REFII_RENDERER_GENERAL_HOOK(sub_82BA2270) // Lumen: D3DDevice_AcquireThreadOwnership
REFII_RENDERER_GENERAL_HOOK(sub_82BA22B0) // Exact Unleashed stub: D3DDevice_ReleaseThreadOwnership
// sub_82BA2500 (D3DDevice_GetDeviceState) REMOVED 2026-07-30: zero consumers —
// unlike its three neighbours it has no kFableUnleashedExactHooks entry, no
// target, no list membership. A state getter polled by the guest; the wrapper
// was pure overhead.
REFII_RENDERER_DRAW_HOOK(sub_822224A8) // Mapped: SetVertexDeclaration
REFII_RENDERER_RESOURCE_HOOK(rex_lhGfx_CreateGPUState) // Lumen: D3DDevice_CreateVertexDeclaration
REFII_RENDERER_DRAW_HOOK(rex_XGSetVertexDeclaration) // Lumen: XGSetVertexDeclaration
REFII_RENDERER_RESOURCE_HOOK(sub_82278138) // Lumen: D3D::LockSurface
REFII_RENDERER_RESOURCE_HOOK(sub_82B986C8) // Lumen: D3D::Lock3DSurface
REFII_RENDERER_RESOURCE_HOOK(sub_82B98778) // Lumen: D3DBaseTexture_LockTail
REFII_RENDERER_RESOURCE_HOOK(sub_82B98C48) // Lumen: D3DSurface_LockRect
REFII_RENDERER_RESOURCE_HOOK(sub_82B994B8) // Lumen: D3DDevice_CreateVertexShader
// FABLE'S ACTUAL SHADER CREATE ROOTS (IDA 2026-08-04). The XDK roots
// sub_82B994B8 / sub_82B992B0 above are genuine D3D9 roots but have exactly ONE
// caller each -- GFxRendererXbox360_Ensure{Vertex,Pixel}Shader @0x82FEEB20 /
// 0x82FEEC20, the Scaleform HLSL compile ("vs_1_1"/"ps_1_1", "main") that backs
// Pub Games. They never fire in Fable's own renderer, so the GTL registry held
// ZERO shader resources and every draw failed missing_vertex_shader_resource.
// These two are what Fable's shader deserializer sub_82B91130 (and the clone
// path sub_82B91A18) actually call, dispatching on *(res+12): 1 = pixel, 2 =
// vertex. Hooked as kResource passthrough, read on EXIT -- the shader object is
// memset early in construction, so an enter-side read is garbage.
REFII_RENDERER_RESOURCE_HOOK(sub_82B87738) // IDA: Fable CreateVertexShader (pWrapper, pContainer)
REFII_RENDERER_RESOURCE_HOOK(sub_82B87818) // IDA: Fable CreatePixelShader (pWrapper, pContainer)
REFII_RENDERER_RESOURCE_HOOK(rex_XGRegisterVertexShader) // Lumen: XGRegisterVertexShader
REFII_RENDERER_RESOURCE_HOOK(sub_830FE488) // Lumen: XGRAPHICS__ThreadSafeSSMPrecompileShader
REFII_RENDERER_RESOURCE_HOOK(sub_831CBAF8) // Lumen: XGRAPHICS__TranslateShader
REFII_RENDERER_DRAW_HOOK(sub_82BA40F0) // Lumen: D3DVertexShader_Bind
REFII_RENDERER_DRAW_HOOK(sub_82232510) // Lumen: D3DDevice_SetVertexShader
REFII_RENDERER_DRAW_HOOK(sub_821B6C60) // Lumen: D3DDevice_SetStreamSource
REFII_RENDERER_DRAW_HOOK(sub_8219CC30) // Lumen: D3DDevice_SetIndices
// BUFFER PAYLOAD RELOCATION (IDA 2026-08-04). Fable's vertex/index/constant
// buffers are allocated out of a COMPACTING physical pool: sub_82B87670 is
// registered as that pool's move callback at 0x82B87258, and it dispatches
// vtable slot 4 of off_820FB130, which is this function. Four instructions:
//   stw r5,4(r3) / subf r4,r4,r5 / addi r3,r3,8 / b XGOffsetResourceAddress
// i.e. (obj, oldAddr, newAddr) -> the D3D header at obj+8 is re-pointed at the
// new memory. Without this root a buffer's stated data pointer silently goes
// stale mid-run and the consumer keeps uploading from the old allocation.
// kResource so the GTL sees it; the handler is enter-side (kRelocateResource).
REFII_RENDERER_RESOURCE_HOOK(sub_82B876F0) // IDA: Fable buffer pool relocation
REFII_RENDERER_RESOURCE_HOOK(sub_82B992B0) // Lumen: D3DDevice_CreatePixelShader
REFII_RENDERER_DRAW_HOOK(sub_82208BB0) // Lumen: D3DDevice_SetPixelShader
REFII_RENDERER_RESOURCE_HOOK(sub_82D16CF8) // Lumen: D3DXFillTexture
REFII_RENDERER_RESOURCE_HOOK(sub_82D168C8) // Exact Unleashed stub: D3DXFilterTexture
REFII_RENDERER_RESOURCE_HOOK(sub_82D17250) // Lumen: CreateTextureFromFileInMemoryEx

#undef REFII_RENDERER_DRAW_HOOK
#undef REFII_RENDERER_TITLE_DRAW_HOOK
#undef REFII_RENDERER_RESOURCE_HOOK
#undef REFII_RENDERER_GENERAL_HOOK
#undef REFII_RENDERER_PASSTHROUGH_HOOK_AS
