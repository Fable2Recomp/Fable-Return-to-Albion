#include "renderer_runtime.h"
#include "runtime/refii_renderer/guest/guest_memory_access.h"
#include "hooks/ppc_externs.h"
#include "runtime/refii_renderer/bik/bink_startup_audio_pump.h"
#include "runtime/refii_renderer/bik/bink_plane_publish.h"
#include "runtime/refii_renderer/bik/bink_video_plane_setup.h"
#include "renderdoc_debugger.h"
#include "runtime/cvars.h"
#include "runtime/hook_observer.h"
#include "runtime/refii_renderer/gtl/refii_gtl.h"
#include "runtime/renderer_hook_policy.h"
#include "runtime/refii_renderer/hooks/general/fable_scene_boundary_hooks.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_present_state.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_scene_population.h"
#include "runtime/refii_renderer/plume/general/plume_fable_addressing.h"
#include "runtime/plume/general/plume_backend.h"
#include "runtime/refii_renderer/hooks/root/fable_hook_symbols.h"
#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/graphics/pipeline/texture/info.h>
#include <rex/graphics/pipeline/texture/util.h>
#include <rex/graphics/xenos.h>
#include <rex/ppc/context.h>
#include <rex/system/xthread.h>
#include <tracy/Tracy.hpp>
#include <algorithm>
#include <array>
#include <atomic>
#include <cerrno>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <deque>
#include <fstream>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <windows.h>
#include "runtime/refii_renderer/hooks/renderer_runtime_internal.h"

namespace refii::renderer {

uint32_t EmulateFableAllocateCommandSegment(uint32_t device, uint8_t *base,
                                            uint32_t requested_words);
FableGuestCommandSubmitEmulation ApplyFableScissorState(
    uint8_t *base, uint32_t device, int32_t left, int32_t top, int32_t right,
    int32_t bottom, bool store_requested_rect);

bool IsLikelyGuestDwordAddress(uint32_t guest_address) {
  constexpr uint32_t kFableObjectAddressMax = 0x80000000u;
  return guest_address >= 0x1000u && guest_address < kFableObjectAddressMax &&
         (guest_address & (alignof(uint32_t) - 1u)) == 0;
}

uint32_t LoadHookStackU32(uint8_t *base, const RendererHookArgs &args,
                          uint32_t offset) {
  const uint32_t address = args.r1 + offset;
  return base && IsLikelyGuestDwordAddress(address) ? LoadGuestU32(base, address)
                                                    : 0u;
}

bool IsLikelyGuestMemoryAddress(uint32_t guest_address) {
  constexpr uint32_t kFableObjectAddressMax = 0x80000000u;
  return guest_address >= 0x1000u &&
         (guest_address < kFableObjectAddressMax ||
          guest_address >= 0xE0000000u);
}

uint32_t CountLeadingZeroBits64(uint64_t value) {
#if defined(__GNUC__) || defined(__clang__)
  return value == 0 ? 64u : static_cast<uint32_t>(__builtin_clzll(value));
#else
  if (value == 0) {
    return 64u;
  }
  uint32_t count = 0;
  while ((value & 0x8000000000000000ull) == 0) {
    value <<= 1u;
    ++count;
  }
  return count;
#endif
}

struct FableSurfaceExtent {
  uint32_t width = 0;
  uint32_t height = 0;
};

bool IsPlausibleFableSurfaceExtent(uint32_t width, uint32_t height) {
  constexpr uint32_t kMaxFableSurfaceExtent = 2048;
  constexpr uint64_t kMaxFableSurfacePixels =
      static_cast<uint64_t>(kMaxFableSurfaceExtent) *
      static_cast<uint64_t>(kMaxFableSurfaceExtent);
  return width != 0 && height != 0 && width <= kMaxFableSurfaceExtent &&
         height <= kMaxFableSurfaceExtent &&
         static_cast<uint64_t>(width) * height <= kMaxFableSurfacePixels;
}

FableSurfaceExtent DecodeFableSurfaceExtent(uint8_t *base, uint32_t surface) {
  if (base == nullptr || !IsLikelyFableObjectAddress(surface)) {
    return {};
  }

  constexpr uint32_t kSurfacePackedExtentOffset = 0x0024;
  const uint32_t packed_extent =
      LoadGuestU32(base, surface + kSurfacePackedExtentOffset);
  const uint32_t width = (packed_extent >> 18u) + 1u;
  const uint32_t height = ((packed_extent >> 3u) & 0x7FFFu) + 1u;
  if (!IsPlausibleFableSurfaceExtent(width, height)) {
    return {};
  }
  return {.width = width, .height = height};
}

FableSurfaceExtent CurrentFableDrawSurfaceExtent(uint8_t *base,
                                                 uint32_t device) {
  if (base == nullptr || !IsLikelyFableObjectAddress(device)) {
    return {};
  }

  constexpr uint32_t kRenderTargetBaseOffset = 0x3098;
  constexpr uint32_t kDepthStencilOffset = 0x30A8;
  const uint32_t color_surface =
      LoadGuestU32(base, device + kRenderTargetBaseOffset);
  FableSurfaceExtent extent = DecodeFableSurfaceExtent(base, color_surface);
  if (extent.width != 0 && extent.height != 0) {
    return extent;
  }

  const uint32_t depth_surface = LoadGuestU32(base, device + kDepthStencilOffset);
  return DecodeFableSurfaceExtent(base, depth_surface);
}

FableSurfaceExtent CurrentFableViewportClampExtent(uint8_t *base,
                                                   uint32_t device) {
  if (base == nullptr || !IsLikelyFableObjectAddress(device)) {
    return {};
  }

  constexpr uint32_t kSurfaceFlagsOffset = 0x2ABC;
  constexpr uint32_t kRenderTargetBaseOffset = 0x3098;
  constexpr uint32_t kDepthStencilOffset = 0x30A8;
  constexpr uint32_t kTrackedSurfaceBaseOffset = 0x31B8;
  constexpr uint32_t kTrackedDepthOffset = 0x31C8;
  constexpr uint32_t kViewportClampWidthOffset = 0x337C;
  constexpr uint32_t kViewportClampHeightOffset = 0x3380;

  const uint8_t flags = LoadGuestU8(base, device + kSurfaceFlagsOffset);
  bool use_override_extent = (flags & 0x10u) != 0;
  if (!use_override_extent && (flags & 0x20u) != 0) {
    use_override_extent = true;
    for (uint32_t slot = 0; slot < 4u; ++slot) {
      const uint32_t surface =
          LoadGuestU32(base, device + kRenderTargetBaseOffset + slot * 4u);
      const uint32_t tracked =
          LoadGuestU32(base, device + kTrackedSurfaceBaseOffset + slot * 4u);
      if (surface != 0 && tracked != surface) {
        use_override_extent = false;
        break;
      }
    }
    const uint32_t depth_surface = LoadGuestU32(base, device + kDepthStencilOffset);
    const uint32_t tracked_depth = LoadGuestU32(base, device + kTrackedDepthOffset);
    if (depth_surface != 0 && tracked_depth != depth_surface) {
      use_override_extent = false;
    }
  }

  if (use_override_extent) {
    const uint32_t width = LoadGuestU32(base, device + kViewportClampWidthOffset);
    const uint32_t height = LoadGuestU32(base, device + kViewportClampHeightOffset);
    if (IsPlausibleFableSurfaceExtent(width, height)) {
      return {.width = width, .height = height};
    }
  }

  return CurrentFableDrawSurfaceExtent(base, device);
}

RendererScissorSnapshot MakeFableScissor(int32_t left, int32_t top,
                                         int32_t right, int32_t bottom) {
  RendererScissorSnapshot scissor;
  if (right > left && bottom > top) {
    scissor.left = left;
    scissor.top = top;
    scissor.right = right;
    scissor.bottom = bottom;
    scissor.valid = true;
  }
  return scissor;
}

RendererViewportSnapshot MakeFableViewport(float x, float y, float width,
                                           float height, float min_depth,
                                           float max_depth) {
  RendererViewportSnapshot viewport;
  if (std::isfinite(x) && std::isfinite(y) && std::isfinite(width) &&
      std::isfinite(height) && std::isfinite(min_depth) &&
      std::isfinite(max_depth) && width >= 0.0f && height >= 0.0f) {
    viewport.x = x;
    viewport.y = y;
    viewport.width = width;
    viewport.height = height;
    viewport.min_depth = min_depth;
    viewport.max_depth = max_depth;
    viewport.valid = true;
  }
  return viewport;
}

void OrFableGuestQwordLow(uint8_t *base, uint32_t guest_address,
                          uint32_t bits) {
  StoreGuestU32(base, guest_address + sizeof(uint32_t),
                LoadGuestU32(base, guest_address + sizeof(uint32_t)) | bits);
}

void OrFableGuestQword(uint8_t *base, uint32_t guest_address, uint64_t bits) {
  StoreGuestU64(base, guest_address, LoadGuestU64(base, guest_address) | bits);
}

void AndNotFableGuestQword(uint8_t *base, uint32_t guest_address,
                           uint64_t mask) {
  StoreGuestU64(base, guest_address, LoadGuestU64(base, guest_address) & ~mask);
}

void RetireFableBoundResourceObject(uint8_t *base, uint32_t device,
                                    uint32_t object) {
  if (base == nullptr || device == 0 || object == 0) {
    return;
  }

  constexpr uint32_t kCurrentBackBufferOffset = 0x2A9C;
  constexpr uint32_t kBackBufferDirtyMaskOffset = 0x2AA0;
  constexpr uint32_t kExportListWriteOffset = 0x34D4;
  constexpr uint32_t kExportListLimitOffset = 0x34D8;

  const uint32_t current_back_buffer =
      LoadGuestU32(base, device + kCurrentBackBufferOffset);
  if (current_back_buffer != 0) {
    StoreGuestU32(base, object + 8u, current_back_buffer);
    return;
  }

  if ((LoadGuestU32(base, device + kBackBufferDirtyMaskOffset) &
       LoadGuestU32(base, object)) == 0) {
    return;
  }

  const uint32_t write = LoadGuestU32(base, device + kExportListWriteOffset);
  const uint32_t limit = LoadGuestU32(base, device + kExportListLimitOffset);
  if (write != 0 && write + 2u * sizeof(uint32_t) <= limit) {
    StoreGuestU32(base, write, (object >> 2u) & 0xBFFFFFFFu);
    StoreGuestU32(base, write + sizeof(uint32_t), 0xFFFFFFFFu);
    StoreGuestU32(base, device + kExportListWriteOffset,
                  write + 2u * sizeof(uint32_t));
  }
}

void ApplyFableShaderPatchStream(uint8_t *base, uint32_t device,
                                 uint32_t header, uint32_t dirty_qword_offset) {
  if (base == nullptr || device == 0 || header == 0) {
    return;
  }

  constexpr uint32_t kShaderConstantsBaseOffset = 0x0480;
  constexpr uint32_t kDeviceWideDirtyQwordOffset = 0x0020;
  constexpr uint64_t kShaderPatchWideDirtyBit = 0x0100000000000000ull;
  constexpr uint32_t kMaxShaderPatchBytes = 0x10000;

  AndNotFableGuestQword(base, device + dirty_qword_offset,
                        LoadGuestU64(base, header));
  if (LoadGuestU64(base, header + 8u) != 0) {
    OrFableGuestQword(base, device + kDeviceWideDirtyQwordOffset,
                      kShaderPatchWideDirtyBit);
  }

  const uint32_t patch_size = LoadGuestU32(base, header + 0x10u);
  if (patch_size == 0 || patch_size > kMaxShaderPatchBytes) {
    return;
  }

  uint32_t cursor = header + 0x14u;
  const uint32_t end = cursor + patch_size;
  while (cursor + 4u <= end) {
    const uint16_t count = LoadGuestU16(base, cursor + 2u);
    cursor += 4u;
    if (count == 0) {
      break;
    }
    cursor += 4u;
  }

  if (cursor >= end) {
    return;
  }

  while (cursor + 4u <= end) {
    const uint16_t destination = LoadGuestU16(base, cursor);
    const uint16_t count = LoadGuestU16(base, cursor + 2u);
    cursor += 4u;
    if (count == 0) {
      break;
    }
    const uint32_t bytes = count * sizeof(uint32_t);
    if (cursor + bytes > end) {
      return;
    }
    for (uint32_t i = 0; i < count; ++i) {
      StoreGuestU32(base,
                    device + kShaderConstantsBaseOffset + destination +
                        i * sizeof(uint32_t),
                    LoadGuestU32(base, cursor + i * sizeof(uint32_t)));
    }
    cursor += bytes;
  }

  while (cursor + 4u <= end) {
    const uint16_t destination = LoadGuestU16(base, cursor);
    uint16_t count = LoadGuestU16(base, cursor + 2u);
    cursor += 4u;
    if (count == 0) {
      break;
    }
    uint32_t destination_address =
        device + kShaderConstantsBaseOffset + destination;
    do {
      if (cursor + 2u * sizeof(uint32_t) > end) {
        return;
      }
      const uint32_t mask = LoadGuestU32(base, cursor);
      const uint32_t value = LoadGuestU32(base, cursor + sizeof(uint32_t));
      StoreGuestU32(base, destination_address,
                    (LoadGuestU32(base, destination_address) & mask) | value);
      cursor += 2u * sizeof(uint32_t);
      destination_address += sizeof(uint32_t);
      count = static_cast<uint16_t>(count - 2u);
    } while (count != 0);
  }
}

void EmulateFablePendingStateDispatch(uint32_t record, uint8_t *base,
                                      uint32_t source_lr,
                                      bool notify_observer) {
  if (record == 0) {
    return;
  }

  // Order and side effects mirror Render_ApplyDirtyStateVar (0x8221E550)
  // exactly (IDA, 2026-08-02): it clears the in-list flag at +0x14
  // UNCONDITIONALLY and BEFORE the compare, returns without committing when
  // current == target, and otherwise -- after the kind-1/kind-2 call, or with
  // NO call at all for any other kind -- always commits current = target at
  // loc_8221E5D4. Kind 3 is the scope sentinel (g_StateScopeSentinel), the
  // only other kind that exists; the guest calls nothing for it but still
  // commits. Skipping that commit leaves current != target forever, and since
  // the inlined enqueue guard only re-adds a descriptor when +0x14 is clear,
  // a mismatched pair can wedge that state permanently.
  const uint32_t current_value = LoadGuestU32(base, record);
  const uint32_t pending_value = LoadGuestU32(base, record + 4u);
  StoreGuestU8(base, record + 20u, 0);
  if (current_value == pending_value) {
    return;
  }
  const uint8_t dispatch_kind = LoadGuestU8(base, record + 21u);
  if (dispatch_kind != 1u && dispatch_kind != 2u) {
    StoreGuestU32(base, record, pending_value);
    return;
  }

  // THIS is the carrier, not the guest hook.
  //
  // The guest's own sub_8221E550 is Plume-owned and suppressed, so it fires
  // ~once per run; Refii EMULATES the deferred dispatch here instead. Wiring
  // GTL's decode only to the guest hook (as the first attempt did) therefore
  // gave it exactly 1 call per run against the observer's ~178k -- the decode
  // was correct, it was simply on the dead path.
  //
  // Deliberately OUTSIDE the observer gate below: GTL must not depend on
  // observer policy, which is the whole point of owning the derivation. Read
  // before the StoreGuestU32 that commits `pending` into `current`, so the
  // record still shows the pre-commit values the decode expects.
  {
    RendererHookArgs gtl_dispatch_args = {};
    gtl_dispatch_args.r3 = record;
    gtl_dispatch_args.lr = source_lr;
    refii::gtl::OnStateDispatch(gtl_dispatch_args, base);
  }
  if (notify_observer && RendererStateDispatchObserverAllowed()) {
    RendererHookArgs dispatch_args = {};
    dispatch_args.r3 = record;
    dispatch_args.lr = source_lr;
    ObserveRendererStateDispatch(dispatch_args, base);
  }
  StoreGuestU32(base, record, pending_value);
}

void EmulateFableDeferredStateDispatch(uint8_t *base, uint32_t source_lr) {
  if (base == nullptr) {
    return;
  }

  constexpr uint32_t kDeferredStateCount = 0x83490DB0;
  constexpr uint32_t kDeferredStateRecords = 0x83490DB8;
  constexpr uint32_t kPendingQueueBegin = 0x834B2390;
  constexpr uint32_t kMaxMirroredDeferredRecords =
      (kPendingQueueBegin - kDeferredStateRecords) / sizeof(uint32_t);

  const uint32_t count =
      std::min(LoadGuestU32(base, kDeferredStateCount),
               kMaxMirroredDeferredRecords);
  for (uint32_t i = 0; i < count; ++i) {
    const uint32_t record =
        LoadGuestU32(base, kDeferredStateRecords + i * sizeof(uint32_t));
    EmulateFablePendingStateDispatch(record, base, source_lr, true);
  }
  StoreGuestU32(base, kDeferredStateCount, 0);
}

void EmulateFableDeferredStatePostDispatch(uint8_t *base) {
  if (base == nullptr) {
    return;
  }

  constexpr uint32_t kDeviceAddress = 0x834970E8;
  constexpr uint32_t kDepthModeEnableAddress = 0x834B644C;
  constexpr uint32_t kSrcBlendAddress = 0x834B6494;
  constexpr uint32_t kDestBlendAddress = 0x834B64AC;
  constexpr uint32_t kHighPrecisionBlendOffset = 12028;
  constexpr uint32_t kBlendControlOffset = 10372;
  constexpr uint32_t kBlendControlValidOffset = 12440;
  constexpr uint32_t kDirtyQwordOffset = 16;
  constexpr uint32_t kBlendControlDirtyBit = 0x01000000;

  uint32_t high_precision_blend = 0;
  if (LoadGuestU32(base, kDepthModeEnableAddress) == 1u) {
    const uint32_t src_blend = LoadGuestU32(base, kSrcBlendAddress);
    const uint32_t dest_blend = LoadGuestU32(base, kDestBlendAddress);
    high_precision_blend =
        ((src_blend == 1u && dest_blend != 1u) ||
         (src_blend == 14u && dest_blend != 15u))
            ? 1u
            : 0u;
  }

  const uint32_t device = LoadGuestU32(base, kDeviceAddress);
  if (device == 0) {
    return;
  }

  StoreGuestU32(base, device + kHighPrecisionBlendOffset,
                high_precision_blend);
  if (LoadGuestU32(base, device + kBlendControlValidOffset) == 0) {
    return;
  }

  const uint32_t blend_control = LoadGuestU32(base, device + kBlendControlOffset);
  const uint32_t mode = (blend_control >> 16u) & 0xFu;
  if ((mode == 2u || mode == 3u || mode == 10u || mode == 12u) &&
      (((blend_control >> 19u) & 1u) != high_precision_blend)) {
    const uint32_t replacement =
        (((~(high_precision_blend - 1u) << 16u) & ((mode + 3u) << 17u)) |
         ((((mode >> 1u) - 3u) & (high_precision_blend - 1u)) << 16u)) &
        0x000F0000u;
    StoreGuestU32(base, device + kBlendControlOffset,
                  (blend_control & 0xFFF0FFFFu) | replacement);
    OrFableGuestQwordLow(base, device + kDirtyQwordOffset,
                         kBlendControlDirtyBit);
  }
}

FableGuestCommandSubmitEmulation EmulateFableDeferredStateDispatchHook(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr) {
    return emulation;
  }

  emulation.handled = true;
  emulation.result = LoadGuestU32(base, 0x834970E8);
  EmulateFableDeferredStateDispatch(base, args.lr);
  EmulateFableDeferredStatePostDispatch(base);
  return emulation;
}

void AppendFableDeferredStateRecord(uint32_t record, uint8_t *base,
                                    uint32_t source_lr) {
  constexpr uint32_t kDeferredStateCount = 0x83490DB0;
  constexpr uint32_t kDeferredStateRecords = 0x83490DB8;
  constexpr uint32_t kPendingQueueBegin = 0x834B2390;
  constexpr uint32_t kMaxMirroredDeferredRecords =
      (kPendingQueueBegin - kDeferredStateRecords) / sizeof(uint32_t);

  const uint32_t count = LoadGuestU32(base, kDeferredStateCount);
  if (count >= kMaxMirroredDeferredRecords) {
    EmulateFablePendingStateDispatch(record, base, source_lr, true);
    return;
  }

  StoreGuestU8(base, record + 20u, 1);
  StoreGuestU32(base, kDeferredStateCount, count + 1u);
  StoreGuestU32(base, kDeferredStateRecords + count * sizeof(uint32_t),
                record);
}

uint32_t EmulateFablePendingStateQueue(uint32_t queue, uint8_t *base,
                                       uint32_t source_lr) {
  constexpr uint32_t kQueueScaleOffset = 0x2000;
  constexpr uint32_t kQueueCountOffset = 0x2004;

  uint32_t count = LoadGuestU32(base, queue + kQueueCountOffset);
  if (count == 0) {
    StoreGuestU32(base, queue + kQueueScaleOffset,
                  LoadGuestU32(base, queue + kQueueScaleOffset) >> 1u);
    return 0;
  }

  --count;
  StoreGuestU32(base, queue + kQueueCountOffset, count);
  uint32_t record = LoadGuestU32(base, queue + count * 8u);
  while (record != 0 && LoadGuestU8(base, record + 21u) != 3u) {
    const uint32_t queue_scale = LoadGuestU32(base, queue + kQueueScaleOffset);
    StoreGuestU32(base, record + 8u,
                  LoadGuestU32(base, record + 8u) - queue_scale);
    StoreGuestU32(base, record + 4u,
                  LoadGuestU32(base, queue + count * 8u + 4u));

    const bool immediate = LoadGuestU8(base, record + 12u) != 0;
    const bool pending = LoadGuestU8(base, record + 20u) != 0;
    if (immediate) {
      if (pending) {
        EmulateFablePendingStateDispatch(record, base, source_lr, true);
      }
    } else if (!pending) {
      AppendFableDeferredStateRecord(record, base, source_lr);
    }

    count = LoadGuestU32(base, queue + kQueueCountOffset);
    if (count == 0) {
      break;
    }
    --count;
    StoreGuestU32(base, queue + kQueueCountOffset, count);
    record = LoadGuestU32(base, queue + count * 8u);
  }

  StoreGuestU32(base, queue + kQueueScaleOffset,
                LoadGuestU32(base, queue + kQueueScaleOffset) >> 1u);
  return record;
}

FableGuestCommandSubmitEmulation EmulateFablePendingStateDispatchHook(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  emulation.handled = true;
  emulation.result = args.r3;
  EmulateFablePendingStateDispatch(args.r3, base, args.lr, false);
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFablePendingStateQueueHook(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  emulation.handled = true;
  emulation.result = EmulateFablePendingStateQueue(args.r3, base, args.lr);
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableFlushPendingGpuState(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kPendingQueueBegin = 0x834B2390;
  constexpr uint32_t kPendingQueueEnd = 0x834B63A0;
  constexpr uint32_t kPendingQueueStride = 0x2008;

  emulation.handled = true;
  emulation.result = args.r3;
  if (LoadGuestU8(base, args.r3) == 0) {
    return emulation;
  }

  StoreGuestU8(base, args.r3, 0);
  for (uint32_t queue = kPendingQueueBegin; queue < kPendingQueueEnd;
       queue += kPendingQueueStride) {
    (void)EmulateFablePendingStateQueue(queue, base, args.lr);
  }
  return emulation;
}

uint32_t FableGpuAddress(uint32_t guest_address) {
  return ((((guest_address >> 20u) + 512u) & 0x1000u) +
          (guest_address & 0x1FFFFFFFu));
}

uint32_t FableGpuSegmentPointer(uint32_t guest_address) {
  return FableGpuAddress(guest_address) - 0x40000000u;
}

uint32_t AlignGuestU32(uint32_t value, uint32_t alignment) {
  return (value + alignment - 1u) & ~(alignment - 1u);
}

uint32_t EmulateFableInlineCommandBuffer(uint32_t device, uint8_t *base) {
  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandEndOffset = 0x0034;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kDeviceFlags2Offset = 0x2ABD;
  constexpr uint32_t kInlineCommandBufferOffset = 0x4158;
  constexpr uint32_t kInlineCommandBufferSize = 4800;
  constexpr uint32_t kInlineCommandReserve = 160;

  const uint32_t buffer =
      LoadGuestU32(base, device + kInlineCommandBufferOffset);
  if (buffer != 0) {
    StoreGuestU32(base, device + kCommandPointerOffset, buffer);
    StoreGuestU32(base, device + kCommandEndOffset,
                  buffer + kInlineCommandBufferSize);
    StoreGuestU32(base, device + kCommandLimitOffset,
                  buffer + kInlineCommandBufferSize - kInlineCommandReserve);
  } else {
    StoreGuestU32(base, device + kCommandPointerOffset,
                  device + kInlineCommandBufferOffset);
    StoreGuestU32(base, device + kCommandEndOffset,
                  device + kInlineCommandBufferOffset +
                      kInlineCommandBufferSize);
    StoreGuestU32(base, device + kCommandLimitOffset,
                  device + kInlineCommandBufferOffset +
                      kInlineCommandBufferSize - kInlineCommandReserve);
  }
  // Fable's transient scratch allocator treats bit 0x20 here as a hard
  // allocation failure latch. When Plume owns submission, this inline buffer is
  // the guest-visible command space, so keep the guest allocator live.
  StoreGuestU8(base, device + kDeviceFlags2Offset,
               LoadGuestU8(base, device + kDeviceFlags2Offset) & ~0x20u);
  return 0;
}

uint32_t EmulateFableReserveCommandSpace(uint32_t device, uint8_t *base,
                                         uint32_t &size_bytes,
                                         uint32_t alignment) {
  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandEndOffset = 0x0034;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kDeferredFenceOffset = 0x33B0;
  constexpr uint32_t kDeferredEpochOffset = 0x33B4;
  constexpr uint32_t kSegmentRingBaseOffset = 0x3A38;
  constexpr uint32_t kSegmentRingEndOffset = 0x3A3C;
  constexpr uint32_t kSegmentReserveSizeOffset = 0x3A40;
  constexpr uint32_t kSegmentScratchOffset = 0x3A44;
  constexpr uint32_t kSegmentRingTailOffset = 0x3A4C;
  constexpr uint32_t kSegmentEpochOffset = 0x3A48;
  constexpr uint32_t kSegmentMaxPendingSpanOffset = 0x3A58;
  constexpr uint32_t kDeviceFlags3Offset = 0x2ABE;

  const uint32_t requested_words = size_bytes >> 2u;
  uint32_t cursor = LoadGuestU32(base, device + kSegmentScratchOffset);
  if (cursor == 0) {
    cursor = LoadGuestU32(base, device + kCommandPointerOffset) + 4u;
  }

  uint32_t start = AlignGuestU32(cursor, alignment);
  uint32_t words = requested_words != 0 ? requested_words : 54u;
  words = std::max(
      words, LoadGuestU32(base, device + kSegmentReserveSizeOffset) >> 2u);
  const uint32_t reserve_bytes = words * sizeof(uint32_t);
  uint32_t end = start + reserve_bytes;
  const uint32_t ring_end = LoadGuestU32(base, device + kSegmentRingEndOffset);
  if (end > ring_end) {
    end = ring_end;
  }

  const uint32_t primary_base =
      LoadGuestU32(base, device + kSegmentRingBaseOffset);
  const uint32_t ring_tail =
      LoadGuestU32(base, device + kSegmentRingTailOffset);
  bool wrap = ((end - start) >> 2u) < words;
  if (((ring_tail - primary_base) & ~3u) >
      LoadGuestU32(base, device + kSegmentMaxPendingSpanOffset)) {
    wrap = true;
  }
  if (wrap) {
    StoreGuestU32(base, device + kSegmentRingTailOffset, primary_base);
    start = AlignGuestU32(ring_tail, alignment);
    StoreGuestU32(base, device + kSegmentEpochOffset,
                  LoadGuestU32(base, device + kSegmentEpochOffset) + 1u);
    StoreGuestU8(base, device + kDeviceFlags3Offset,
                 LoadGuestU8(base, device + kDeviceFlags3Offset) | 4u);
    end = start + reserve_bytes;
  }

  const uint32_t deferred_fence =
      LoadGuestU32(base, device + kDeferredFenceOffset);
  if (deferred_fence != 0) {
    const uint32_t deferred_epoch =
        LoadGuestU32(base, device + kDeferredEpochOffset);
    const uint32_t current_epoch =
        LoadGuestU32(base, device + kSegmentEpochOffset);
    if (deferred_epoch - current_epoch == UINT32_MAX ||
        (deferred_epoch == current_epoch && deferred_fence < end)) {
      end = deferred_fence;
      if (current_epoch != deferred_epoch ||
          ((deferred_fence - start) >> 2u) < words) {
        return 0;
      }
    }
  }

  size_bytes = ((end - start) >> 2u) * sizeof(uint32_t);
  return start;
}

uint32_t EmulateFableAllocateCommandSegment(uint32_t device, uint8_t *base,
                                            uint32_t requested_words) {
  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandEndOffset = 0x0034;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kDeviceFlags2Offset = 0x2ABD;
  constexpr uint32_t kSegmentReserveSizeOffset = 0x3A40;
  constexpr uint32_t kSegmentStartOffset = 0x3A50;
  constexpr uint32_t kSegmentScratchOffset = 0x3A44;
  constexpr uint32_t kSegmentUsedSizeOffset = 0x3A54;
  constexpr uint32_t kCommandReserve = 160;
  constexpr uint32_t kSegmentHeaderWords = 14;

  if ((LoadGuestU8(base, device + kDeviceFlags2Offset) & 0x20u) != 0) {
    return EmulateFableInlineCommandBuffer(device, base);
  }

  uint32_t size_bytes =
      requested_words != 0
          ? (requested_words + kSegmentHeaderWords) * sizeof(uint32_t)
          : 0;
  const uint32_t minimum_size =
      LoadGuestU32(base, device + kSegmentReserveSizeOffset);
  if (minimum_size != 0) {
    size_bytes = std::max(size_bytes, minimum_size);
  }

  const uint32_t allocation =
      EmulateFableReserveCommandSpace(device, base, size_bytes, 32u);
  if (allocation == 0) {
    return EmulateFableInlineCommandBuffer(device, base);
  }

  StoreGuestU32(base, device + kSegmentStartOffset, allocation);
  StoreGuestU32(base, device + kCommandPointerOffset, allocation - 4u);
  StoreGuestU32(base, device + kSegmentScratchOffset, 0);
  StoreGuestU32(base, device + kSegmentUsedSizeOffset, 0);
  const uint32_t command_end =
      allocation +
      (((size_bytes >> 2u) - kSegmentHeaderWords) * sizeof(uint32_t)) - 4u;
  StoreGuestU32(base, device + kCommandEndOffset, command_end);
  StoreGuestU32(base, device + kCommandLimitOffset,
                command_end - kCommandReserve);
  return allocation;
}

uint32_t EmulateFableWriteCommandBufferEpilogue(uint32_t device, uint8_t *base,
                                                uint32_t command_pointer) {
  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kDeferredFenceOffset = 0x33B0;
  constexpr uint32_t kDeferredEpochOffset = 0x33B4;
  constexpr uint32_t kFencePointerOffset = 0x2A90;
  constexpr uint32_t kFenceValueOffset = 0x2A9C;
  constexpr uint32_t kFenceDisableOffset = 0x541C;
  constexpr uint32_t kDeviceFlags2Offset = 0x2ABD;
  constexpr uint32_t kWaitUntilPacket = 0xC0004000;
  constexpr uint32_t kWaitUntilMask = 0x00007FFF;
  constexpr uint32_t kWriteRegisterPacket = 0xC0025C00;

  const uint32_t current_command =
      LoadGuestU32(base, device + kCommandPointerOffset);
  const uint32_t command_epoch = LoadGuestU32(base, device + 0x3A48);
  const uint32_t fence_pointer =
      LoadGuestU32(base, device + kFencePointerOffset);
  const uint32_t fence_value = LoadGuestU32(base, device + kFenceValueOffset);
  const uint32_t command_token = current_command | (command_epoch & 3u);

  StoreGuestU32(base, device + kDeferredFenceOffset, current_command);
  StoreGuestU32(base, device + kDeferredEpochOffset, command_epoch);

  uint32_t out = command_pointer + sizeof(uint32_t);
  StoreGuestU32(base, out, kWaitUntilPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kWaitUntilMask);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kWriteRegisterPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 3);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out,
                FableGpuAddress(fence_pointer + sizeof(uint32_t)) | 2u);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, command_token);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kWriteRegisterPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 3);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, FableGpuAddress(fence_pointer) | 2u);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, fence_value);

  if (LoadGuestU32(base, device + kFenceDisableOffset) == 0 &&
      (LoadGuestU8(base, device + kDeviceFlags2Offset) & 2u) != 0 &&
      fence_pointer != 0) {
    StoreGuestU32(base, fence_pointer, fence_value);
    StoreGuestU32(base, fence_pointer + sizeof(uint32_t), command_token);
  }
  StoreGuestU32(base, device + kFenceValueOffset, fence_value + 2u);
  return out;
}

uint32_t EmulateFableAllocateAuxiliaryCommandWords(uint32_t device,
                                                   uint8_t *base,
                                                   uint32_t word_count,
                                                   uint32_t alignment) {
  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandEndOffset = 0x0034;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kDeferredCommandContextOffset = 0x33B0;
  constexpr uint32_t kDirectPrimaryContextOffset = 0x34CC;
  constexpr uint32_t kDeviceFlags2Offset = 0x2ABD;
  constexpr uint32_t kAuxiliaryScratchOffset = 0x3A44;
  constexpr uint32_t kSegmentRingBaseOffset = 0x3A38;
  constexpr uint32_t kSegmentRingEndOffset = 0x3A3C;
  constexpr uint32_t kSegmentRingTailOffset = 0x3A4C;
  constexpr uint32_t kAuxiliaryBytesUsedOffset = 0x3A54;
  constexpr uint32_t kAuxiliaryBytesLimitOffset = 0x3A58;
  constexpr uint32_t kCommandReserve = 160;

  if (base == nullptr || device == 0 || word_count == 0 || alignment == 0) {
    return 0;
  }
  if ((LoadGuestU8(base, device + kDeviceFlags2Offset) & 0x20u) != 0) {
    return 0;
  }

  const uint32_t byte_count = word_count * sizeof(uint32_t);
  const uint32_t direct_context =
      LoadGuestU32(base, device + kDirectPrimaryContextOffset);
  if (direct_context != 0 && LoadGuestU32(base, direct_context + 0x98u) != 0) {
    uint32_t high_water = LoadGuestU32(base, device + kAuxiliaryScratchOffset);
    if (high_water == 0) {
      high_water = (LoadGuestU32(base, direct_context + 0x9Cu) & ~3u) +
                   LoadGuestU32(base, direct_context + 0x98u);
    }
    const uint32_t allocation =
        (high_water - byte_count) & ~(alignment - 1u);
    if (allocation >= LoadGuestU32(base, device + kCommandPointerOffset) +
                          kCommandReserve + sizeof(uint32_t)) {
      StoreGuestU32(base, device + kAuxiliaryScratchOffset, allocation);
      const uint32_t consumed = high_water - allocation;
      StoreGuestU32(base, device + kCommandEndOffset,
                    LoadGuestU32(base, device + kCommandEndOffset) - consumed);
      StoreGuestU32(base, device + kCommandLimitOffset,
                    LoadGuestU32(base, device + kCommandLimitOffset) -
                        consumed);
      return allocation;
    }
    StoreGuestU8(base, device + kDeviceFlags2Offset,
                 LoadGuestU8(base, device + kDeviceFlags2Offset) | 0x20u);
    return 0;
  }

  uint32_t size_bytes = byte_count;
  const uint32_t allocation =
      EmulateFableReserveCommandSpace(device, base, size_bytes, alignment);
  if (allocation != 0 && size_bytes >= byte_count) {
    StoreGuestU32(base, device + kAuxiliaryBytesUsedOffset,
                  LoadGuestU32(base, device + kAuxiliaryBytesUsedOffset) +
                      byte_count);
    if (LoadGuestU32(base, device + kAuxiliaryBytesUsedOffset) >
            LoadGuestU32(base, device + kAuxiliaryBytesLimitOffset) &&
        LoadGuestU32(base, device + kDeferredCommandContextOffset) == 0) {
      const uint32_t command_pointer =
          LoadGuestU32(base, device + kCommandPointerOffset);
      StoreGuestU32(base, device + kCommandEndOffset,
                    command_pointer + kCommandReserve);
      StoreGuestU32(base, device + kCommandLimitOffset, command_pointer);
      if (LoadGuestU32(base, device + kSegmentRingTailOffset) !=
          LoadGuestU32(base, device + kSegmentRingBaseOffset)) {
        StoreGuestU32(base, device + kAuxiliaryScratchOffset,
                      LoadGuestU32(base, device + kSegmentRingEndOffset));
      }
    }
    return allocation;
  }

  StoreGuestU8(base, device + kDeviceFlags2Offset,
               LoadGuestU8(base, device + kDeviceFlags2Offset) | 0x20u);
  return 0;
}

uint32_t EmulateFableMakePrimaryCallListSpace(uint32_t device, uint8_t *base,
                                              uint32_t head_offset,
                                              uint32_t write_offset,
                                              uint32_t limit_offset,
                                              uint32_t context_link_offset,
                                              uint32_t word_count,
                                              uint32_t list_byte_size) {
  constexpr uint32_t kDirectPrimaryContextOffset = 0x34CC;
  constexpr uint32_t kDeviceFlags2Offset = 0x2ABD;
  constexpr uint32_t kInlineCommandBufferOffset = 0x4158;

  const uint32_t previous_head = LoadGuestU32(base, device + head_offset);
  uint32_t allocation = EmulateFableAllocateAuxiliaryCommandWords(
      device, base, word_count, 128u);
  if ((LoadGuestU8(base, device + kDeviceFlags2Offset) & 0x20u) != 0 ||
      allocation == 0) {
    allocation = device + kInlineCommandBufferOffset;
  } else {
    StoreGuestU32(base, device + head_offset, allocation);
    StoreGuestU32(base, allocation, 0);
    const uint32_t chain_pointer = FableGpuSegmentPointer(allocation);
    if (previous_head != 0) {
      StoreGuestU32(base, previous_head, chain_pointer);
      const uint32_t previous_write = LoadGuestU32(base, device + write_offset);
      StoreGuestU32(base, previous_head + sizeof(uint32_t),
                    (previous_write - previous_head - 8u) >> 3u);
    } else {
      const uint32_t direct_context =
          LoadGuestU32(base, device + kDirectPrimaryContextOffset);
      if (direct_context != 0) {
        StoreGuestU32(base, direct_context + context_link_offset,
                      chain_pointer);
      }
    }
  }

  const uint32_t write = allocation + 8u;
  StoreGuestU32(base, device + write_offset, write);
  StoreGuestU32(base, device + limit_offset, allocation + list_byte_size);
  if (!g_logged_fable_primary_list_space_emulation) {
    REXLOG_INFO("Refii plume exact Fable primary call-list allocation "
                "device=0x{:08X} list=0x{:08X}",
                device, allocation);
    g_logged_fable_primary_list_space_emulation = true;
  }
  return write;
}

uint32_t EmulateFableMakeDeferredCallListSpace(uint32_t device, uint8_t *base,
                                               uint32_t list_offset) {
  constexpr uint32_t kListHeadOffset = 0x0;
  constexpr uint32_t kListPreviousLinkOffset = 0x4;
  constexpr uint32_t kListWriteOffset = 0x8;
  constexpr uint32_t kListLimitOffset = 0xC;
  constexpr uint32_t kInlineCommandBufferOffset = 0x4158;
  constexpr uint32_t kDeferredCallListWords = 1056;
  constexpr uint32_t kDeferredCallListByteSize = 4224;

  const uint32_t list = device + list_offset;
  const uint32_t previous_link =
      LoadGuestU32(base, list + kListPreviousLinkOffset);
  if (previous_link != 0) {
    StoreGuestU32(base, previous_link,
                  FableGpuSegmentPointer(
                      LoadGuestU32(base, list + kListWriteOffset)));
  }

  uint32_t allocation = EmulateFableAllocateAuxiliaryCommandWords(
      device, base, kDeferredCallListWords, 128u);
  if (allocation != 0) {
    StoreGuestU32(base, allocation,
                  FableGpuSegmentPointer(allocation + sizeof(uint32_t)));
    const uint32_t current_write =
        LoadGuestU32(base, list + kListWriteOffset);
    if (current_write != 0) {
      StoreGuestU32(base, current_write, FableGpuSegmentPointer(allocation));
    } else {
      StoreGuestU32(base, list + kListHeadOffset, allocation);
    }
  } else {
    allocation = device + kInlineCommandBufferOffset;
    StoreGuestU32(base, list + kListHeadOffset, 0);
  }

  const uint32_t write = allocation + sizeof(uint32_t);
  StoreGuestU32(base, list + kListPreviousLinkOffset, allocation);
  StoreGuestU32(base, list + kListWriteOffset, write);
  StoreGuestU32(base, list + kListLimitOffset,
                allocation + kDeferredCallListByteSize - sizeof(uint32_t));
  if (!g_logged_fable_deferred_list_space_emulation) {
    REXLOG_INFO("Refii plume exact Fable secondary call-list allocation "
                "device=0x{:08X} list=0x{:08X}",
                device, allocation);
    g_logged_fable_deferred_list_space_emulation = true;
  }
  return write;
}

uint32_t WriteFableCacheRangeCommand(uint8_t *base, uint32_t out,
                                     uint32_t wait_mask,
                                     uint32_t range_start,
                                     uint32_t range_end) {
  constexpr uint32_t kWaitPacket = 0x00000A31;
  constexpr uint32_t kReadPacket = 0x00010A2F;
  constexpr uint32_t kWriteRegisterPacket = 0xC0043C00;
  constexpr uint32_t kEndPacket = 0x80000000;

  if (range_end <= range_start) {
    return out;
  }
  StoreGuestU32(base, out, kWaitPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, wait_mask);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kReadPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, range_end - range_start);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, range_start);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kWriteRegisterPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 3);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kWaitPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 0);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kEndPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 8);
  out += sizeof(uint32_t);
  return out;
}

bool EmulateFableBuildKickOffPrefixCommands(uint32_t device, uint8_t *base,
                                            uint32_t &gpu_address,
                                            uint32_t &word_count) {
  constexpr uint32_t kQueuedCacheFenceOffset = 0x2E30;
  constexpr uint32_t kDeviceFlags3Offset = 0x2ABE;
  constexpr uint32_t kSegmentRingBaseOffset = 0x3A38;
  constexpr uint32_t kSegmentRingEndOffset = 0x3A3C;
  constexpr uint32_t kFenceWaitMask = 0x03000000;
  constexpr uint32_t kRingWaitMask = 0x01000000;
  constexpr uint32_t kMaxPrefixWords = 22;

  gpu_address = 0;
  word_count = 0;
  if (base == nullptr || device == 0) {
    return false;
  }

  const bool has_queued_cache_fence =
      LoadGuestU32(base, device + kQueuedCacheFenceOffset +
                            sizeof(uint32_t)) != UINT32_MAX;
  const bool needs_ring_cache_flush =
      ((LoadGuestU8(base, device + kDeviceFlags3Offset) >> 2u) & 1u) != 0;
  word_count = 11u * (static_cast<uint32_t>(has_queued_cache_fence) +
                      static_cast<uint32_t>(needs_ring_cache_flush));
  if (word_count == 0) {
    return false;
  }

  const uint32_t command_buffer = EmulateFableAllocateAuxiliaryCommandWords(
      device, base, word_count, 32u);
  if (command_buffer == 0) {
    word_count = 0;
    return false;
  }

  uint32_t out = command_buffer;
  if (has_queued_cache_fence) {
    const uint32_t range_start =
        FableGpuAddress(command_buffer - sizeof(uint32_t)) & 0xFFFFF000u;
    const uint32_t range_end =
        (FableGpuAddress(command_buffer - sizeof(uint32_t)) + 4095u) &
        0xFFFFF000u;
    out = WriteFableCacheRangeCommand(base, out, kFenceWaitMask, range_start,
                                      range_end);
    StoreGuestU64(base, device + kQueuedCacheFenceOffset, UINT64_MAX);
  }

  if (needs_ring_cache_flush) {
    const uint32_t ring_base =
        FableGpuAddress(LoadGuestU32(base, device + kSegmentRingBaseOffset)) &
        0xFFFFF000u;
    const uint32_t ring_end =
        (FableGpuAddress(LoadGuestU32(base, device + kSegmentRingEndOffset)) +
         4095u) &
        0xFFFFF000u;
    out = WriteFableCacheRangeCommand(base, out, kRingWaitMask, ring_base,
                                      ring_end);
  }

  word_count = (out - command_buffer) / sizeof(uint32_t);
  if (word_count == 0) {
    return false;
  }
  gpu_address = FableGpuAddress(command_buffer);
  return true;
}

uint32_t AddFablePrimaryIndirectBufferCall(uint32_t device, uint8_t *base,
                                           uint32_t command_address,
                                           uint32_t word_count) {
  constexpr uint32_t kPrimaryRingBaseOffset = 0x3A30;
  constexpr uint32_t kPrimaryRingMaskOffset = 0x3A34;
  constexpr uint32_t kPrimaryRingWriteOffset = 0x2AC8;
  constexpr uint32_t kPrimaryIndirectBufferPacket = 0xC0013F00;

  const uint32_t ring_base =
      LoadGuestU32(base, device + kPrimaryRingBaseOffset);
  const uint32_t ring_mask =
      LoadGuestU32(base, device + kPrimaryRingMaskOffset);
  uint32_t write_index = LoadGuestU32(base, device + kPrimaryRingWriteOffset);
  if (ring_base == 0 || ring_mask == 0) {
    return write_index;
  }

  const uint32_t packet_index = write_index;
  const uint32_t address_index = (packet_index + 1u) & ring_mask;
  const uint32_t count_index = (address_index + 1u) & ring_mask;
  StoreGuestU32(base, ring_base + packet_index * sizeof(uint32_t),
                kPrimaryIndirectBufferPacket);
  StoreGuestU32(base, ring_base + address_index * sizeof(uint32_t),
                command_address);
  StoreGuestU32(base, ring_base + count_index * sizeof(uint32_t),
                word_count & 0x00FFFFFFu);

  write_index = (count_index + 1u) & ring_mask;
  StoreGuestU32(base, device + kPrimaryRingWriteOffset, write_index);
  return write_index;
}

FableGuestCommandSubmitEmulation
EmulateFableAddCallsToPrimaryBuffer(const RendererHookArgs &args,
                                    uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kDeviceFlagsOffset = 0x2ABD;
  constexpr uint32_t kDebugCommandSinkOffset = 0x541C;
  const uint32_t device = args.r3;
  const uint32_t descriptor_base = args.r4;
  const uint32_t descriptor_count = args.r5;

  emulation.handled = true;
  emulation.result = LoadGuestU32(base, device + kDebugCommandSinkOffset);
  if ((LoadGuestU8(base, device + kDeviceFlagsOffset) & 2u) != 0 ||
      descriptor_base == 0 || descriptor_count == 0) {
    return emulation;
  }

  for (uint32_t index = 0; index < descriptor_count; ++index) {
    const uint32_t descriptor = descriptor_base + index * 8u;
    const uint32_t word_count = LoadGuestU32(base, descriptor) & 0x00FFFFFFu;
    const uint32_t command_address =
        LoadGuestU32(base, descriptor + sizeof(uint32_t));
    AddFablePrimaryIndirectBufferCall(device, base, command_address,
                                      word_count);
  }
  return emulation;
}

FableGuestCommandSubmitEmulation
EmulateFableAddCommandsToPrimaryBuffer(const RendererHookArgs &args,
                                       uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || !IsLikelyFableObjectAddress(args.r3)) {
    return emulation;
  }

  constexpr uint32_t kPrimaryRingWriteOffset = 0x2AC8;
  constexpr uint32_t kPrimaryRingBaseOffset = 0x3A30;
  constexpr uint32_t kPrimaryRingMaskOffset = 0x3A34;
  constexpr uint32_t kMaxDirectPrimaryCopyDwords = 1u << 20u;

  const uint32_t device = args.r3;
  const uint32_t command_pointer = args.r4;
  const uint32_t command_word_count = args.r5;

  emulation.handled = true;
  emulation.result = 0;
  if (command_word_count == 0) {
    return emulation;
  }
  if (!IsLikelyGuestDwordAddress(command_pointer) ||
      command_word_count > kMaxDirectPrimaryCopyDwords) {
    return emulation;
  }

  const uint32_t ring_base =
      LoadGuestU32(base, device + kPrimaryRingBaseOffset);
  const uint32_t ring_mask =
      LoadGuestU32(base, device + kPrimaryRingMaskOffset);
  if (!IsLikelyGuestDwordAddress(ring_base) || ring_mask == 0) {
    return emulation;
  }

  uint32_t write_index = LoadGuestU32(base, device + kPrimaryRingWriteOffset);
  for (uint32_t index = 0; index < command_word_count; ++index) {
    StoreGuestU32(base, ring_base + write_index * sizeof(uint32_t),
                  LoadGuestU32(base, command_pointer +
                                         index * sizeof(uint32_t)));
    write_index = (write_index + 1u) & ring_mask;
  }
  StoreGuestU32(base, device + kPrimaryRingWriteOffset, write_index);
  return emulation;
}

FableGuestCommandSubmitEmulation
EmulateFableQueueIndirectBuffer(const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kQueuedIndirectDwordCountOffset = 0x2B00;
  const uint32_t device = args.r3;
  const uint32_t output_buffer = args.r4;
  const uint32_t command_address = args.r5;
  const uint32_t word_count = args.r6;
  const uint32_t queued_dwords = args.r7;
  const uint32_t previous_queued_dwords =
      LoadGuestU32(base, device + kQueuedIndirectDwordCountOffset);

  emulation.handled = true;
  emulation.result = output_buffer;
  StoreGuestU32(base, device + kQueuedIndirectDwordCountOffset,
                previous_queued_dwords + queued_dwords);

  if (previous_queued_dwords != 0) {
    if (output_buffer != 0) {
      StoreGuestU32(base, output_buffer + sizeof(uint32_t),
                    word_count | 0x81000000u);
      StoreGuestU32(base, output_buffer + 2u * sizeof(uint32_t),
                    command_address);
      StoreGuestU32(base, output_buffer + 3u * sizeof(uint32_t), 0xC0000000u);
      emulation.result = output_buffer + 4u * sizeof(uint32_t);
    }
    return emulation;
  }

  AddFablePrimaryIndirectBufferCall(device, base, command_address,
                                    word_count & 0x00FFFFFFu);
  return emulation;
}

void RememberFableDeviceMemoryBase(uint32_t device, uint8_t *base) {
  if (base == nullptr || device == 0) {
    return;
  }
  g_last_fable_memory_base.store(reinterpret_cast<uintptr_t>(base),
                                 std::memory_order_relaxed);
  g_last_fable_device.store(device, std::memory_order_relaxed);
}

FableGuestCommandSubmitEmulation
EmulateFableKickOffSegment(const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kDeviceFlagsOffset = 0x2ABC;
  constexpr uint32_t kDeviceFlags2Offset = 0x2ABD;
  constexpr uint32_t kDeferredCommandContextOffset = 0x33B0;
  constexpr uint32_t kDeferredCallListOffset = 0x3438;
  constexpr uint32_t kDeferredListWriteOffset = 0x3440;
  constexpr uint32_t kDeferredListLimitOffset = 0x3444;
  constexpr uint32_t kDirectPrimaryHeadOffset = 0x34DC;
  constexpr uint32_t kDirectPrimaryContextOffset = 0x34CC;
  constexpr uint32_t kDirectPrimaryWriteOffset = 0x34E0;
  constexpr uint32_t kDirectPrimaryLimitOffset = 0x34E4;
  constexpr uint32_t kDirectPrimaryContextLinkOffset = 0x74;
  constexpr uint32_t kSegmentStartOffset = 0x3A50;
  constexpr uint32_t kDirectPrimaryCallListWords = 34;
  constexpr uint32_t kDirectPrimaryCallListByteSize = 136;
  const uint32_t device = args.r3;
  RememberFableDeviceMemoryBase(device, base);
  const uint32_t segment_start =
      LoadGuestU32(base, device + kSegmentStartOffset);
  uint32_t segment_end =
      LoadGuestU32(base, device + kCommandPointerOffset) + sizeof(uint32_t);
  const uint32_t observed_segment_end = segment_end;

  emulation.handled = true;
  emulation.result = args.r3;

  if ((LoadGuestU8(base, device + kDeviceFlags2Offset) & 0x20u) == 0) {
    const uint32_t word_count = (segment_end - segment_start) >> 2u;
    if ((LoadGuestU32(base, device + kDeviceFlagsOffset) & 0x80u) != 0) {
      const uint32_t direct_context =
          LoadGuestU32(base, device + kDirectPrimaryContextOffset);
      uint32_t write = LoadGuestU32(base, device + kDirectPrimaryWriteOffset);
      const uint32_t limit =
          LoadGuestU32(base, device + kDirectPrimaryLimitOffset);
      if (direct_context != 0 &&
          LoadGuestU32(base, direct_context + 152u) == 0 && word_count != 0 &&
          write + 2u * sizeof(uint32_t) > limit) {
        write = EmulateFableMakePrimaryCallListSpace(
            device, base, kDirectPrimaryHeadOffset, kDirectPrimaryWriteOffset,
            kDirectPrimaryLimitOffset, kDirectPrimaryContextLinkOffset,
            kDirectPrimaryCallListWords, kDirectPrimaryCallListByteSize);
      }
      if (direct_context != 0 &&
          LoadGuestU32(base, direct_context + 152u) == 0 && word_count != 0 &&
          write + 2u * sizeof(uint32_t) <=
              LoadGuestU32(base, device + kDirectPrimaryLimitOffset)) {
        StoreGuestU32(base, write, word_count | 0x81000000u);
        StoreGuestU32(base, write + sizeof(uint32_t),
                      FableGpuAddress(segment_start));
        StoreGuestU32(base, device + kDirectPrimaryWriteOffset,
                      write + 2u * sizeof(uint32_t));
      }
    } else if (LoadGuestU32(base, device + kDeferredCommandContextOffset) !=
               0) {
      uint32_t write = LoadGuestU32(base, device + kDeferredListWriteOffset);
      const uint32_t limit =
          LoadGuestU32(base, device + kDeferredListLimitOffset);
      if (word_count != 0 && write + 2u * sizeof(uint32_t) > limit) {
        write = EmulateFableMakeDeferredCallListSpace(
            device, base, kDeferredCallListOffset);
      }
      if (word_count != 0 && write + 2u * sizeof(uint32_t) <=
                                 LoadGuestU32(base, device +
                                                        kDeferredListLimitOffset)) {
        StoreGuestU32(base, write, word_count | 0x81000000u);
        StoreGuestU32(base, write + sizeof(uint32_t),
                      FableGpuAddress(segment_start));
        StoreGuestU32(base, device + kDeferredListWriteOffset,
                      write + 2u * sizeof(uint32_t));
      }
    } else {
      const uint32_t epilogue_end = EmulateFableWriteCommandBufferEpilogue(
          device, base, segment_end - 4u);
      RendererHookArgs queue_args = args;
      queue_args.r4 = epilogue_end + sizeof(uint32_t);
      queue_args.r5 = FableGpuAddress(segment_start);
      queue_args.r6 = (queue_args.r4 - segment_start) / sizeof(uint32_t);
      queue_args.r7 = 0;
      queue_args.r8 = device + 0x3438u;
      queue_args.r4_u64 = queue_args.r4;
      const FableGuestCommandSubmitEmulation queue_emulation =
          EmulateFableQueueIndirectBuffer(queue_args, base);
      segment_end =
          queue_emulation.handled ? queue_emulation.result : queue_args.r4;
    }
  }

  if (observed_segment_end > segment_start &&
      ((observed_segment_end - segment_start) & (sizeof(uint32_t) - 1u)) ==
          0) {
    ObserveEmulatedFablePm4SubmitSegment(
        device, args.lr, segment_start,
        (observed_segment_end - segment_start) / sizeof(uint32_t), base);
  }

  const uint32_t aligned_end = AlignGuestU32(segment_end, 32u);
  if (aligned_end <= LoadGuestU32(base, device + kCommandLimitOffset)) {
    StoreGuestU32(base, device + kSegmentStartOffset, aligned_end);
    StoreGuestU32(base, device + kCommandPointerOffset,
                  aligned_end - sizeof(uint32_t));
    return emulation;
  }

  StoreGuestU32(base, device + kCommandPointerOffset,
                segment_end - sizeof(uint32_t));
  EmulateFableAllocateCommandSegment(device, base, 0);
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableKickOff(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kDeviceFlagsOffset = 0x2ABC;
  constexpr uint32_t kDeviceFlags2Offset = 0x2ABD;
  constexpr uint32_t kDeferredCommandContextOffset = 0x33B0;
  constexpr uint32_t kGpuKickModeOffset = 0x2A9C;
  constexpr uint32_t kKickOffFenceGlobal = 0x83497144;
  const uint32_t device = args.r3;
  RememberFableDeviceMemoryBase(device, base);

  emulation.handled = true;
  emulation.result = LoadGuestU32(base, device + kCommandPointerOffset);

  if (LoadGuestU32(base, device + kDeferredCommandContextOffset) == 0 &&
      (LoadGuestU8(base, device + kDeviceFlagsOffset) & 0x80u) == 0) {
    uint32_t prefix_gpu_address = 0;
    uint32_t prefix_word_count = 0;
    if (EmulateFableBuildKickOffPrefixCommands(
            device, base, prefix_gpu_address, prefix_word_count)) {
      const uint32_t output_buffer = EmulateFableAllocateAuxiliaryCommandWords(
          device, base, 4u, alignof(uint32_t));
      if (output_buffer != 0) {
        RendererHookArgs queue_args = args;
        queue_args.r4 = output_buffer;
        queue_args.r5 = prefix_gpu_address;
        queue_args.r6 = prefix_word_count;
        queue_args.r7 = 0;
        queue_args.r8 = device + 0x3438u;
        queue_args.r4_u64 = queue_args.r4;
        EmulateFableQueueIndirectBuffer(queue_args, base);
      }
    }
  }

  RendererHookArgs segment_args = args;
  EmulateFableKickOffSegment(segment_args, base);

  if ((LoadGuestU8(base, device + kDeviceFlagsOffset) & 0x80u) == 0 &&
      LoadGuestU32(base, kKickOffFenceGlobal) != 0 &&
      (LoadGuestU8(base, device + kDeviceFlags2Offset) & 2u) == 0) {
    StoreGuestU8(base, device + kDeviceFlags2Offset,
                 LoadGuestU8(base, device + kDeviceFlags2Offset) | 2u);
    if (LoadGuestU32(base, device + kGpuKickModeOffset) != 2u) {
      REXLOG_TRACE("Refii plume completed guest KickOff fence state after "
                   "command submission emulation device=0x{:08X}",
                   device);
    }
  }

  emulation.result = LoadGuestU32(base, device + kCommandPointerOffset);
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableBlockUntilIdle(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (symbol != "sub_82BA2748" || base == nullptr ||
      !IsLikelyFableObjectAddress(args.r3)) {
    return {};
  }

  constexpr uint32_t kQueuedIndirectDwordCountOffset = 0x2B00;
  const uint32_t device = args.r3;
  RememberFableDeviceMemoryBase(device, base);

  RendererHookArgs kick_args = args;
  kick_args.r3 = device;
  const FableGuestCommandSubmitEmulation kick_emulation =
      EmulateFableKickOff(kick_args, base);

  StoreGuestU32(base, device + kQueuedIndirectDwordCountOffset, 0);
  return {.handled = true,
          .result = kick_emulation.handled ? kick_emulation.result : device};
}

FableGuestCommandSubmitEmulation EmulateFableInvalidateCaches(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kWaitPacket = 0x00000A31;
  constexpr uint32_t kCacheInvalidateMask = 0x03000200;
  constexpr uint32_t kReadPacket = 0x00010A2F;
  constexpr uint32_t kWriteRegisterPacket = 0xC0043C00;
  constexpr uint32_t kEndPacket = 0x80000000;
  const uint32_t device = args.r3;

  emulation.handled = true;
  uint32_t command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
  if (command_pointer > LoadGuestU32(base, device + kCommandLimitOffset)) {
    RendererHookArgs kick_args = args;
    const FableGuestCommandSubmitEmulation kick_emulation =
        EmulateFableKickOff(kick_args, base);
    command_pointer = kick_emulation.handled
                          ? kick_emulation.result
                          : LoadGuestU32(base, device + kCommandPointerOffset);
  }

  const uint32_t gpu_address = FableGpuAddress(args.r4);
  const uint32_t range_start = gpu_address & 0xFFFFF000u;
  const uint32_t range_end =
      (gpu_address + args.r5 + 4095u) & 0xFFFFF000u;
  const uint32_t range_size = range_end - range_start;

  uint32_t out = command_pointer + sizeof(uint32_t);
  StoreGuestU32(base, out, kWaitPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kCacheInvalidateMask);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kReadPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, range_size);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, range_start);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kWriteRegisterPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 3);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kWaitPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 0);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kEndPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 8);

  StoreGuestU32(base, device + kCommandPointerOffset, out);
  emulation.result = out;
  return emulation;
}

uint32_t LoadFableExportBaseAddress(uint8_t *base, uint32_t export_descriptor) {
  const uint32_t export_type = LoadGuestU32(base, export_descriptor) & 0xFu;
  if (export_type == 1u) {
    return LoadGuestU32(base, export_descriptor + 0x18u) & 0xFFFFFFFCu;
  }
  if (export_type == 3u) {
    const uint32_t primary =
        LoadGuestU32(base, export_descriptor + 0x20u) & 0xFFFFF000u;
    if (primary != 0) {
      return primary;
    }
    return LoadGuestU32(base, export_descriptor + 0x30u) & 0xFFFFF000u;
  }
  return LoadGuestU32(base, export_descriptor + 0x18u);
}

uint32_t LoadFableExportInvalidateByteSize(uint8_t *base,
                                           uint32_t export_descriptor) {
  if (base == nullptr || !IsLikelyFableObjectAddress(export_descriptor)) {
    return 0;
  }

  constexpr uint32_t kExportByteSizeOffset = 0x2Cu;
  uint32_t byte_size =
      LoadGuestU32(base, export_descriptor + kExportByteSizeOffset);
  if (byte_size != 0) {
    return byte_size;
  }

  const FableSurfaceExtent extent =
      DecodeFableSurfaceExtent(base, export_descriptor);
  if (extent.width == 0 || extent.height == 0) {
    return 0;
  }

  byte_size = extent.width * extent.height * sizeof(uint32_t);
  return (byte_size + 0xFFFu) & ~0xFFFu;
}

void QueueSyntheticFableInvalidateCaches(uint32_t device, uint8_t *base,
                                         uint32_t gpu_address,
                                         uint32_t byte_size,
                                         uint32_t source_lr,
                                         const RendererHookArgs &source_args) {
  if (gpu_address == 0 || byte_size == 0) {
    return;
  }

  RendererHookArgs invalidate_args = source_args;
  invalidate_args.r3 = device;
  invalidate_args.r4 = gpu_address;
  invalidate_args.r5 = byte_size;
  invalidate_args.lr = source_lr;
  invalidate_args.r4_u64 = gpu_address;
  static const FableHookSymbolDesc *invalidate_desc =
      ResolveFableHookSymbolDesc("sub_822C6BA8", RendererHookKind::kDraw);
  if (FableRendererHookAllowedInCurrentMode("sub_822C6BA8",
                                            RendererHookKind::kDraw)) {
    ObserveRendererHookEnter("sub_822C6BA8", RendererHookKind::kDraw,
                             invalidate_args, base, invalidate_desc);
  }
  EmulateFableInvalidateCaches(invalidate_args, base);
}

void EmulateFableEndExportStateRestore(uint8_t *base, uint32_t device,
                                       bool nested_export) {
  if (base == nullptr || device == 0) {
    return;
  }

  constexpr uint32_t kDirtyQword0Offset = 0x0010;
  constexpr uint32_t kDirtyQword1Offset = 0x0018;
  constexpr uint32_t kDirtyQword2Offset = 0x0020;
  constexpr uint32_t kPackedSurfaceClipMinOffset = 0x28C4;
  constexpr uint32_t kPackedSurfaceClipMaxOffset = 0x28C8;
  constexpr uint32_t kNestedExportDirty0 = 0x00080388u;
  constexpr uint32_t kFinalExportDirty0 = 0x00181BE8u;
  constexpr uint32_t kFinalExportDirty1 = 0x00400008u;
  constexpr uint32_t kFinalExportDirty2 = 0x00080008u;

  if (nested_export) {
    OrFableGuestQwordLow(base, device + kDirtyQword0Offset,
                         kNestedExportDirty0);
    return;
  }

  OrFableGuestQwordLow(base, device + kDirtyQword0Offset, kFinalExportDirty0);
  OrFableGuestQwordLow(base, device + kDirtyQword1Offset, kFinalExportDirty1);
  OrFableGuestQwordLow(base, device + kDirtyQword2Offset, kFinalExportDirty2);

  const uint32_t packed_min =
      LoadGuestU32(base, device + kPackedSurfaceClipMinOffset);
  const uint32_t packed_max =
      LoadGuestU32(base, device + kPackedSurfaceClipMaxOffset);
  const int32_t left = static_cast<int32_t>(packed_min << 17u) >> 17;
  const int32_t top = static_cast<int32_t>(packed_min << 1u) >> 17;
  const int32_t right = static_cast<int32_t>(packed_max << 17u) >> 17;
  const int32_t bottom = static_cast<int32_t>(packed_max << 1u) >> 17;
  ApplyFableScissorState(base, device, left, top, right, bottom,
                         false /* store_requested_rect */);
}

FableGuestCommandSubmitEmulation EmulateFableBeginExport(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0 || args.r5 == 0) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kDirtyMaskLowOffset = 0x0014;
  constexpr uint32_t kExportBaseOffset = 0x2898;
  constexpr uint32_t kExportDepthOffset = 0x34C0;
  constexpr uint32_t kExportPendingOffset = 0x34C4;
  constexpr uint32_t kExportScratchOffset = 0x34C8;
  const uint32_t device = args.r3;
  const uint32_t export_descriptor = args.r5;

  emulation.handled = true;
  uint32_t command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
  if (command_pointer > LoadGuestU32(base, device + kCommandLimitOffset)) {
    RendererHookArgs kick_args = args;
    const FableGuestCommandSubmitEmulation kick_emulation =
        EmulateFableKickOff(kick_args, base);
    command_pointer = kick_emulation.handled
                          ? kick_emulation.result
                          : LoadGuestU32(base, device + kCommandPointerOffset);
  }

  const uint32_t export_base =
      LoadFableExportBaseAddress(base, export_descriptor);
  StoreGuestU32(base, device + kExportBaseOffset,
                FableGpuAddress(export_base));
  StoreGuestU32(base, device + kDirtyMaskLowOffset,
                LoadGuestU32(base, device + kDirtyMaskLowOffset) | 0x00080000u);

  uint32_t out = command_pointer + sizeof(uint32_t);
  StoreGuestU32(base, out, 0x000005C8u);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 0x00020000u);
  StoreGuestU32(base, device + kCommandPointerOffset, out);

  const uint32_t export_depth = LoadGuestU32(base, device + kExportDepthOffset);
  StoreGuestU32(base, device + kExportScratchOffset, 0);
  StoreGuestU32(base, device + kExportPendingOffset, 0);
  StoreGuestU32(base, device + kExportDepthOffset, export_depth + 1u);

  emulation.result = LoadGuestU32(base, device + kCommandPointerOffset);
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableEndExport(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0 || args.r5 == 0) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kDirtyMaskLowOffset = 0x0014;
  constexpr uint32_t kExportBaseOffset = 0x2898;
  constexpr uint32_t kExportDepthOffset = 0x34C0;
  constexpr uint32_t kExportPendingOffset = 0x34C4;
  constexpr uint32_t kCurrentBackBufferOffset = 0x2A9C;
  constexpr uint32_t kBackBufferDirtyMaskOffset = 0x2AA0;
  constexpr uint32_t kExportListHeadOffset = 0x34D0;
  constexpr uint32_t kExportListWriteOffset = 0x34D4;
  constexpr uint32_t kExportListLimitOffset = 0x34D8;
  constexpr uint32_t kExportListContextLinkOffset = 0x70;
  constexpr uint32_t kExportCallListWords = 502;
  constexpr uint32_t kExportCallListByteSize = 2008;
  const uint32_t device = args.r3;
  const uint32_t export_descriptor = args.r5;

  emulation.handled = true;
  StoreGuestU32(base, device + kExportBaseOffset, 0);

  uint32_t command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
  if (command_pointer > LoadGuestU32(base, device + kCommandLimitOffset)) {
    RendererHookArgs kick_args = args;
    const FableGuestCommandSubmitEmulation kick_emulation =
        EmulateFableKickOff(kick_args, base);
    command_pointer = kick_emulation.handled
                          ? kick_emulation.result
                          : LoadGuestU32(base, device + kCommandPointerOffset);
  }

  uint32_t out = command_pointer + sizeof(uint32_t);
  StoreGuestU32(base, out, 0x00002006u);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 0);
  StoreGuestU32(base, device + kCommandPointerOffset, out);

  const uint32_t current_back_buffer =
      LoadGuestU32(base, device + kCurrentBackBufferOffset);
  if (current_back_buffer != 0) {
    StoreGuestU32(base, export_descriptor + 8u, current_back_buffer);
    StoreGuestU32(base, export_descriptor + 12u, current_back_buffer);
  } else if ((LoadGuestU32(base, device + kBackBufferDirtyMaskOffset) &
              LoadGuestU32(base, export_descriptor)) != 0) {
    uint32_t write = LoadGuestU32(base, device + kExportListWriteOffset);
    if (write + 8u > LoadGuestU32(base, device + kExportListLimitOffset)) {
      write = EmulateFableMakePrimaryCallListSpace(
          device, base, kExportListHeadOffset, kExportListWriteOffset,
          kExportListLimitOffset, kExportListContextLinkOffset,
          kExportCallListWords, kExportCallListByteSize);
    }
    if (write != 0 &&
        write + 8u <= LoadGuestU32(base, device + kExportListLimitOffset)) {
      StoreGuestU32(base, write, (export_descriptor >> 2u) | 0x40000000u);
      StoreGuestU32(base, write + sizeof(uint32_t), 0xFFFFFFFFu);
      StoreGuestU32(base, device + kExportListWriteOffset,
                    write + 2u * sizeof(uint32_t));
    }
  }

  const uint32_t export_type = LoadGuestU32(base, export_descriptor) & 0xFu;
  const uint32_t export_log_index =
      g_logged_fable_end_export_emulation.fetch_add(1u);
  if (export_log_index < 24u) {
    REXLOG_INFO(
        "Refii plume EndExport emulation #{} device=0x{:08X} "
        "descriptor=0x{:08X} type={} w18=0x{:08X} w1C=0x{:08X} "
        "w20=0x{:08X} w2C=0x{:08X} w30=0x{:08X} current_bb=0x{:08X} "
        "lr=0x{:08X}",
        export_log_index + 1u, device, export_descriptor, export_type,
        LoadGuestU32(base, export_descriptor + 0x18u),
        LoadGuestU32(base, export_descriptor + 0x1Cu),
        LoadGuestU32(base, export_descriptor + 0x20u),
        LoadGuestU32(base, export_descriptor + 0x2Cu),
        LoadGuestU32(base, export_descriptor + 0x30u), current_back_buffer,
        args.lr);
  }
  if (export_type == 1u) {
    const uint32_t base_address =
        LoadGuestU32(base, export_descriptor + 0x18u) & 0xFFFFFFFCu;
    const uint32_t byte_size =
        LoadGuestU32(base, export_descriptor + 0x1Cu) & 0x03FFFFFCu;
    QueueSyntheticFableInvalidateCaches(
        device, base, FableGpuAddress(base_address), byte_size, 0x821968CCu,
        args);
  } else if (export_type == 3u) {
    const uint32_t primary =
        LoadGuestU32(base, export_descriptor + 0x20u) & 0xFFFFF000u;
    const uint32_t secondary =
        LoadGuestU32(base, export_descriptor + 0x30u) & 0xFFFFF000u;
    constexpr uint32_t kConservativeExportInvalidateSize = 0x1000u;
    const uint32_t export_invalidate_size =
        std::max(LoadFableExportInvalidateByteSize(base, export_descriptor),
                 kConservativeExportInvalidateSize);
    if (primary != 0) {
      QueueSyntheticFableInvalidateCaches(
          device, base, FableGpuAddress(primary), export_invalidate_size,
          0x821968A4u, args);
    }
    if (secondary != 0) {
      QueueSyntheticFableInvalidateCaches(
          device, base, FableGpuAddress(secondary), export_invalidate_size,
          0x821968CCu, args);
    }
  }

  const uint32_t export_depth = LoadGuestU32(base, device + kExportDepthOffset);
  StoreGuestU32(base, device + kExportDepthOffset,
                export_depth != 0 ? export_depth - 1u : 0);

  command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
  if (command_pointer > LoadGuestU32(base, device + kCommandLimitOffset)) {
    RendererHookArgs kick_args = args;
    const FableGuestCommandSubmitEmulation kick_emulation =
        EmulateFableKickOff(kick_args, base);
    command_pointer = kick_emulation.handled
                          ? kick_emulation.result
                          : LoadGuestU32(base, device + kCommandPointerOffset);
  }

  out = command_pointer + sizeof(uint32_t);
  StoreGuestU32(base, out, 0xC0004600u);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 6);
  StoreGuestU32(base, device + kCommandPointerOffset, out);

  const bool nested_export = LoadGuestU32(base, device + kExportDepthOffset) != 0;
  if (!nested_export) {
    command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
    if (command_pointer > LoadGuestU32(base, device + kCommandLimitOffset)) {
      RendererHookArgs kick_args = args;
      const FableGuestCommandSubmitEmulation kick_emulation =
          EmulateFableKickOff(kick_args, base);
      command_pointer =
          kick_emulation.handled
              ? kick_emulation.result
              : LoadGuestU32(base, device + kCommandPointerOffset);
    }
    out = command_pointer + sizeof(uint32_t);
    StoreGuestU32(base, out, 0x000005C8u);
    out += sizeof(uint32_t);
    StoreGuestU32(base, out, 0x00020000u);
    StoreGuestU32(base, device + kCommandPointerOffset, out);
  }
  EmulateFableEndExportStateRestore(base, device, nested_export);

  StoreGuestU32(base, device + kDirtyMaskLowOffset,
                LoadGuestU32(base, device + kDirtyMaskLowOffset) | 8u);
  StoreGuestU32(base, device + kExportPendingOffset, 0);

  emulation.result = LoadGuestU32(base, device + kCommandPointerOffset);
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableSetVertexDeclaration(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kVertexDeclarationOffset = 0x2E2C;
  constexpr uint32_t kDirtyQwordOffset = 0x0010;
  constexpr uint32_t kVertexDeclarationDirtyBit = 0x00080000u;

  const uint32_t device = args.r3;
  emulation.handled = true;
  emulation.result = device;
  StoreGuestU32(base, device + kVertexDeclarationOffset, args.r4);
  OrFableGuestQwordLow(base, device + kDirtyQwordOffset,
                       kVertexDeclarationDirtyBit);
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableSetPendingRenderStates(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0 || args.r4_u64 == 0 ||
      !IsLikelyGuestDwordAddress(args.r6)) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandLimitOffset = 0x0038;

  const uint32_t device = args.r3;
  uint64_t mask = args.r4_u64;
  uint32_t register_base = args.r5;
  uint32_t source = args.r6;

  emulation.handled = true;
  emulation.result = device;

  while (mask != 0) {
    const uint32_t leading_zeroes = CountLeadingZeroBits64(mask);
    mask <<= leading_zeroes;
    source += leading_zeroes * sizeof(uint32_t);
    const uint32_t first_register = register_base + leading_zeroes;

    const uint32_t run_length = CountLeadingZeroBits64(~mask);
    if (run_length == 0 || run_length > 64u ||
        !IsLikelyGuestDwordAddress(source + (run_length - 1u) *
                                                sizeof(uint32_t))) {
      break;
    }

    const uint32_t required_words = run_length + 1u;
    uint32_t command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
    const uint32_t command_limit = LoadGuestU32(base, device + kCommandLimitOffset);
    if (command_pointer + required_words * sizeof(uint32_t) >= command_limit) {
      EmulateFableAllocateCommandSegment(device, base, required_words);
      command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
    }

    uint32_t out = command_pointer + sizeof(uint32_t);
    StoreGuestU32(base, out, ((run_length - 1u) << 16u) | first_register);
    for (uint32_t i = 0; i < run_length; ++i) {
      out += sizeof(uint32_t);
      StoreGuestU32(base, out, LoadGuestU32(base, source + i * sizeof(uint32_t)));
    }
    StoreGuestU32(base, device + kCommandPointerOffset, out);

    if (run_length >= 64u) {
      mask = 0;
    } else {
      mask <<= run_length;
    }
    source += run_length * sizeof(uint32_t);
    register_base = first_register + run_length;
  }

  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableSetPendingFetchConstants(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  uint64_t dirty_mask = args.r4_u64;
  if ((dirty_mask >> 32u) == 0 && args.r4 != 0) {
    dirty_mask = static_cast<uint64_t>(args.r4) << 32u;
  }

  emulation.handled = true;
  emulation.result = args.r3;
  (void)dirty_mask;
  // Plume consumes fetch constants directly from the guest device mirror in
  // ObservePendingFetchConstantsLocked on the skipped-hook exit path. Native
  // D3D only used this helper to write PM4 packets, so do not perturb Fable's
  // command cursor while Plume owns submission.
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableSetPendingShaders(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  emulation.handled = true;
  emulation.result = args.r3;
  // Plume consumes shader objects, declarations, and literal shader constants
  // from the direct-state mirror. The native helper only lowers pending shader
  // state into PM4 packets after those guest-visible values are already tracked.
  if (!g_logged_fable_set_pending_shaders_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume command replacement now owns "
        "D3D::SetPending_Shaders PM4 lowering");
    g_logged_fable_set_pending_shaders_emulation = true;
  }
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableSetPendingSplit(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  emulation.handled = true;
  emulation.result = args.r4;

  if (base == nullptr || args.r3 == 0 || args.r4 == 0 ||
      !IsLikelyGuestDwordAddress(args.r4) || args.r7 == 0 || args.r8 == 0 ||
      !IsLikelyGuestDwordAddress(args.r6 + sizeof(uint32_t))) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandEndOffset = 0x0034;

  const uint32_t device = args.r3;
  uint32_t command_pointer = args.r4;
  uint32_t register_base = args.r5;
  uint32_t source = args.r6;
  uint32_t remaining_words = args.r7;
  const uint32_t group_words = args.r8;

  uint32_t guard = 0;
  while (remaining_words != 0 && ++guard < 256u) {
    const uint32_t command_end = LoadGuestU32(base, device + kCommandEndOffset);
    uint32_t writable_words = 0;
    if (command_end > command_pointer + sizeof(uint32_t)) {
      writable_words = ((command_end - command_pointer) >> 2u) - 1u;
    }
    uint32_t write_words = (writable_words / group_words) * group_words;
    if (write_words > remaining_words) {
      write_words = remaining_words;
    }

    if (write_words != 0) {
      uint32_t out = command_pointer + sizeof(uint32_t);
      StoreGuestU32(base, out, ((write_words - 1u) << 16u) | register_base);
      for (uint32_t i = 0; i < write_words; ++i) {
        source += sizeof(uint32_t);
        out += sizeof(uint32_t);
        StoreGuestU32(base, out, LoadGuestU32(base, source));
      }
      command_pointer = out;
      register_base += write_words;
      remaining_words -= write_words;
      StoreGuestU32(base, device + kCommandPointerOffset, command_pointer);
    }

    if (remaining_words == 0) {
      break;
    }

    RendererHookArgs kick_args = args;
    kick_args.r3 = device;
    const uint32_t before_kick = command_pointer;
    const FableGuestCommandSubmitEmulation kick_emulation =
        EmulateFableKickOff(kick_args, base);
    command_pointer = kick_emulation.handled
                          ? kick_emulation.result
                          : LoadGuestU32(base, device + kCommandPointerOffset);
    if (command_pointer == before_kick) {
      EmulateFableAllocateCommandSegment(device, base, remaining_words);
      command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
      if (command_pointer == before_kick) {
        break;
      }
    }
  }

  emulation.result = command_pointer;
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableSetPendingPredicated(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  emulation.handled = true;
  emulation.result = args.r4_u64;

  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kDirtyMaskOffset = 0x0028;
  constexpr uint64_t kPredicationDirtyBit = 0x0000000000000100ull;
  constexpr uint64_t kDepthControlDirtyBit = 0x0000000002000000ull;
  constexpr uint64_t kClipControlDirtyBit = 0x0000000000000020ull;
  constexpr uint32_t kPredicationFlagsOffset = 0x2ABC;
  constexpr uint32_t kPredicationModeOffset = 0x2ABF;
  constexpr uint32_t kDepthControlOffset = 0x2E4C;
  constexpr uint32_t kViewportZOffset = 0x2880;
  constexpr uint32_t kViewportZRestoreOffset = 0x3378;
  constexpr uint32_t kClipControlOffset = 0x28DC;
  constexpr uint32_t kPredicationCompareBaseOffset = 0x3098;
  constexpr uint32_t kPredicationTrackedBaseOffset = 0x31B8;
  constexpr uint32_t kPredicationMaskOffset = 0x31A4;
  constexpr uint32_t kPredicationMaskRestoreOffset = 0x31A8;
  constexpr uint32_t kPredicationCountOffset = 0x31CC;
  constexpr uint32_t kPredicationValueBaseOffset = 0x3338;
  constexpr uint32_t kWriteMaskedPacket = 0xC0006000u;
  constexpr uint32_t kWriteFullPacket = 0xC0006100u;
  constexpr uint32_t kWriteRegisterPacket = 0xC0012D01u;

  const uint32_t device = args.r3;
  uint64_t dirty_mask = args.r4_u64;
  const uint32_t predication_value = args.r5;
  uint32_t command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);

  auto kick_if_needed = [&]() {
    if (command_pointer > LoadGuestU32(base, device + kCommandLimitOffset)) {
      StoreGuestU32(base, device + kCommandPointerOffset, command_pointer);
      RendererHookArgs kick_args = args;
      kick_args.r3 = device;
      const FableGuestCommandSubmitEmulation kick_emulation =
          EmulateFableKickOff(kick_args, base);
      command_pointer =
          kick_emulation.handled
              ? kick_emulation.result
              : LoadGuestU32(base, device + kCommandPointerOffset);
    }
    if (!IsLikelyGuestDwordAddress(command_pointer)) {
      EmulateFableAllocateCommandSegment(device, base, 32u);
      command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
    }
  };

  auto write_word = [&](uint32_t value) {
    command_pointer += sizeof(uint32_t);
    StoreGuestU32(base, command_pointer, value);
  };

  kick_if_needed();
  if (!IsLikelyGuestDwordAddress(command_pointer)) {
    return emulation;
  }

  uint32_t restore_predication = UINT32_MAX;
  uint32_t restore_depth_control = UINT32_MAX;
  uint32_t restore_clip_control = UINT32_MAX;

  if ((dirty_mask & kPredicationDirtyBit) != 0) {
    const uint8_t predication_mode =
        LoadGuestU8(base, device + kPredicationModeOffset);
    const uint8_t predication_flags =
        LoadGuestU8(base, device + kPredicationFlagsOffset);
    bool expanded_predication = false;
    if ((predication_mode & 0x20u) != 0) {
      if ((predication_flags & 0x10u) != 0) {
        expanded_predication = true;
      } else if ((predication_flags & 0x20u) != 0) {
        expanded_predication = true;
        for (uint32_t i = 0; i < 5u; ++i) {
          const uint32_t compare_value =
              LoadGuestU32(base, device + kPredicationCompareBaseOffset +
                                     i * sizeof(uint32_t));
          const uint32_t tracked_value =
              LoadGuestU32(base, device + kPredicationTrackedBaseOffset +
                                     i * sizeof(uint32_t));
          if (tracked_value != compare_value && compare_value != 0) {
            expanded_predication = false;
            break;
          }
        }
      }
    }

    if (expanded_predication) {
      write_word(kWriteFullPacket);
      write_word(0);

      uint32_t last_written_predication = predication_value;
      uint32_t count = LoadGuestU32(base, device + kPredicationCountOffset);
      if (count > 16u) {
        count = 16u;
      }
      for (uint32_t index = count; index > 0; --index) {
        const uint32_t slot = index - 1u;
        const uint32_t slot_value =
            LoadGuestU32(base, device + kPredicationValueBaseOffset +
                                   slot * sizeof(uint32_t));
        uint32_t value = (slot_value << 17u) | (predication_value & 0x1FFFFu);
        last_written_predication = value;

        const uint32_t depth_mode =
            (LoadGuestU32(base, device + kDepthControlOffset) >> 17u) & 7u;
        if (depth_mode == 0 ||
            (depth_mode == 2u &&
             (slot != 0 || (predication_mode & 0x10u) == 0))) {
          last_written_predication = value & ~1u;
        }

        write_word(kWriteMaskedPacket);
        write_word(3u << (2u * slot));
        write_word(kWriteRegisterPacket);
        write_word(0x00040203u);
        write_word(last_written_predication);
        StoreGuestU32(base, device + kCommandPointerOffset, command_pointer);
        kick_if_needed();
      }

      if (last_written_predication != predication_value &&
          (predication_flags & 0x40u) != 0) {
        write_word(kWriteMaskedPacket);
        write_word(1);
        write_word(kWriteRegisterPacket);
        write_word(0x00040203u);
        write_word(predication_value);
      }

      write_word(kWriteMaskedPacket);
      write_word(LoadGuestU32(base, device + kPredicationMaskOffset));
      write_word(kWriteFullPacket);
      write_word(LoadGuestU32(base, device + kPredicationMaskRestoreOffset));
    } else {
      uint32_t value = predication_value;
      if ((LoadGuestU32(base, device + kDepthControlOffset) & 0x000E0000u) ==
          0) {
        value &= ~1u;
      }
      write_word(0x00002203u);
      write_word(value);
      if (value != predication_value && (predication_flags & 0x40u) != 0) {
        restore_predication = predication_value;
      }
    }

    dirty_mask &= ~kPredicationDirtyBit;
  }

  const uint64_t effective_dirty =
      LoadGuestU64(base, device + kDirtyMaskOffset) & dirty_mask;
  if ((effective_dirty & kDepthControlDirtyBit) != 0) {
    const uint32_t viewport_z = LoadGuestU32(base, device + kViewportZOffset);
    write_word(0x00002000u);
    write_word(viewport_z);
    dirty_mask &= ~kDepthControlDirtyBit;
    restore_depth_control =
        (viewport_z & 0xFFFFC000u) |
        (LoadGuestU32(base, device + kViewportZRestoreOffset) & 0x3FFFu);
  }

  if ((effective_dirty & kClipControlDirtyBit) != 0) {
    write_word(0x00002104u);
    write_word(LoadGuestU32(base, device + kClipControlOffset));
    dirty_mask &= ~kClipControlDirtyBit;
    restore_clip_control = 0;
  }

  if ((restore_predication & restore_depth_control & restore_clip_control) !=
      UINT32_MAX) {
    write_word(kWriteFullPacket);
    write_word(0);
    write_word(kWriteMaskedPacket);
    write_word(0x15555555u);

    if (restore_predication != UINT32_MAX) {
      write_word(kWriteRegisterPacket);
      write_word(0x00040203u);
      write_word(restore_predication);
    }
    if (restore_depth_control != UINT32_MAX) {
      write_word(kWriteRegisterPacket);
      write_word(0x00040000u);
      write_word(restore_depth_control);
    }
    if (restore_clip_control != UINT32_MAX) {
      write_word(kWriteRegisterPacket);
      write_word(0x00040104u);
      write_word(restore_clip_control);
    }

    write_word(kWriteMaskedPacket);
    write_word(LoadGuestU32(base, device + kPredicationMaskOffset));
    write_word(kWriteFullPacket);
    write_word(LoadGuestU32(base, device + kPredicationMaskRestoreOffset));
  }

  StoreGuestU32(base, device + kCommandPointerOffset, command_pointer);
  emulation.result = dirty_mask;
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableSetPendingClipPlanes(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kClipControlOffset = 0x289C;
  constexpr uint32_t kClipPlaneConstantsOffset = 0x2820;
  constexpr uint32_t kClipPlaneRegisterBase = 0x2388;
  constexpr uint32_t kClipPlaneDwords = 4;
  constexpr uint32_t kClipPlaneCount = 32;
  constexpr uint32_t kInitialPacketWords = 13;

  const uint32_t device = args.r3;
  uint64_t dirty_mask = args.r4_u64;
  if ((dirty_mask >> 32u) == 0 && args.r4 != 0) {
    dirty_mask = static_cast<uint64_t>(args.r4) << 32u;
  }

  emulation.handled = true;
  emulation.result = device;

  uint32_t command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);

  auto ensure_words = [&](uint32_t words) {
    if (!IsLikelyGuestDwordAddress(command_pointer) ||
        command_pointer + words * sizeof(uint32_t) >=
            LoadGuestU32(base, device + kCommandLimitOffset)) {
      EmulateFableAllocateCommandSegment(device, base, words);
      command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
    }
    return IsLikelyGuestDwordAddress(command_pointer);
  };

  auto write_word = [&](uint32_t value) {
    command_pointer += sizeof(uint32_t);
    StoreGuestU32(base, command_pointer, value);
  };

  if (!ensure_words(kInitialPacketWords)) {
    return emulation;
  }

  write_word(0x00002007u);
  write_word(LoadGuestU32(base, device + kClipControlOffset));
  write_word(0x00000A31u);
  write_word(0x00010000u);
  write_word(0x00010A2Fu);
  write_word(0);
  write_word(0x00001000u);
  write_word(0xC0043C00u);
  write_word(3);
  write_word(0x00000A31u);
  write_word(0);
  write_word(0x80000000u);
  write_word(8);
  StoreGuestU32(base, device + kCommandPointerOffset, command_pointer);

  for (uint32_t slot = 0; slot < kClipPlaneCount;) {
    if ((dirty_mask & (1ull << (63u - slot))) == 0) {
      ++slot;
      continue;
    }

    const uint32_t first_slot = slot;
    do {
      ++slot;
    } while (slot < kClipPlaneCount &&
             (dirty_mask & (1ull << (63u - slot))) != 0);

    const uint32_t slot_count = slot - first_slot;
    const uint32_t dword_count = slot_count * kClipPlaneDwords;
    const uint32_t source =
        device + kClipPlaneConstantsOffset +
        first_slot * kClipPlaneDwords * sizeof(uint32_t);
    if (!IsLikelyGuestDwordAddress(source + (dword_count - 1u) *
                                                sizeof(uint32_t)) ||
        !ensure_words(dword_count + 1u)) {
      break;
    }

    write_word(((dword_count - 1u) << 16u) |
               (kClipPlaneRegisterBase + first_slot * kClipPlaneDwords));
    for (uint32_t i = 0; i < dword_count; ++i) {
      write_word(LoadGuestU32(base, source + i * sizeof(uint32_t)));
    }
    StoreGuestU32(base, device + kCommandPointerOffset, command_pointer);
  }

  emulation.result = command_pointer;
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableSetIndices(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kIndexBufferOffset = 0x3094;

  const uint32_t device = args.r3;
  emulation.handled = true;
  emulation.result = device;
  RetireFableBoundResourceObject(base, device,
                                 LoadGuestU32(base, device + kIndexBufferOffset));
  StoreGuestU32(base, device + kIndexBufferOffset, args.r4);
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableSetStreamSource(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kStreamGpuBaseOffset = 0x18;
  constexpr uint32_t kStreamSizeOffset = 0x1C;
  constexpr uint32_t kStreamResourceBaseOffset = 0x30AC;
  constexpr uint32_t kStreamStrideBaseOffset = 0x30F0;
  constexpr uint32_t kVertexDeclarationStrideBaseOffset = 0x2E38;
  constexpr uint32_t kStreamFetchDirtyQwordOffset = 0x0018;
  constexpr uint32_t kVertexDeclarationDirtyQwordOffset = 0x0010;
  constexpr uint32_t kVertexDeclarationDirtyBit = 0x00080000u;

  const uint32_t device = args.r3;
  const uint32_t stream = args.r4;
  const uint32_t buffer = args.r5;
  const uint32_t offset = args.r6;
  const uint32_t stride_words = args.r7 >> 2u;

  emulation.handled = true;
  emulation.result = device;
  if (stream >= 18u) {
    return emulation;
  }

  if (buffer != 0) {
    const uint32_t base_address =
        LoadGuestU32(base, buffer + kStreamGpuBaseOffset) + offset;
    const uint32_t remaining_size =
        LoadGuestU32(base, buffer + kStreamSizeOffset) - offset;
    const uint32_t reversed_stream_offset = 8u * (17u - stream);
    StoreGuestU32(base, device + 0x06F4u + reversed_stream_offset,
                  FableGpuAddress(base_address));
    StoreGuestU32(base, device + 0x06F8u + reversed_stream_offset,
                  remaining_size);
    OrFableGuestQwordLow(base, device + kStreamFetchDirtyQwordOffset, args.r8);
  }

  const uint32_t slot_address =
      device + kStreamResourceBaseOffset + stream * sizeof(uint32_t);
  RetireFableBoundResourceObject(base, device, LoadGuestU32(base, slot_address));
  StoreGuestU32(base, slot_address, buffer);
  StoreGuestU8(base, device + kStreamStrideBaseOffset + stream,
               static_cast<uint8_t>(stride_words));
  if (stride_words != 0 &&
      stride_words !=
          LoadGuestU8(base, device + kVertexDeclarationStrideBaseOffset +
                                 stream)) {
    OrFableGuestQwordLow(base, device + kVertexDeclarationDirtyQwordOffset,
                         kVertexDeclarationDirtyBit);
  }
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableSetTexture(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kTextureBindingBaseOffset = 0x3100;
  constexpr uint32_t kTextureRegisterBaseOffset = 0x0480;
  constexpr uint32_t kTextureFetchDirtyQwordOffset = 0x0018;
  constexpr uint32_t kTextureMaxLodBaseOffset = 0x2EAE;
  constexpr uint32_t kTextureMinLodBaseOffset = 0x2EC8;

  const uint32_t device = args.r3;
  const uint32_t slot = args.r4;
  const uint32_t texture = args.r5;

  emulation.handled = true;
  emulation.result = device;
  if (slot >= 16u) {
    return emulation;
  }

  const uint32_t binding_address =
      device + kTextureBindingBaseOffset + slot * sizeof(uint32_t);
  const uint32_t previous_texture = LoadGuestU32(base, binding_address);

  if (texture != 0) {
    const uint32_t registers = device + kTextureRegisterBaseOffset + slot * 24u;
    const uint32_t reg0 = LoadGuestU32(base, registers);
    const uint32_t reg1 = LoadGuestU32(base, registers + 4u);
    const uint32_t reg3 = LoadGuestU32(base, registers + 12u);
    const uint32_t reg4 = LoadGuestU32(base, registers + 16u);
    const uint32_t reg5 = LoadGuestU32(base, registers + 20u);
    const uint32_t base_address = LoadGuestU32(base, texture + 0x20u);
    const uint32_t texture_1c = LoadGuestU32(base, texture + 0x1Cu);
    const uint32_t texture_24 = LoadGuestU32(base, texture + 0x24u);
    const uint32_t texture_28 = LoadGuestU32(base, texture + 0x28u);
    const uint32_t texture_2c = LoadGuestU32(base, texture + 0x2Cu);
    const uint32_t texture_30 = LoadGuestU32(base, texture + 0x30u);

    StoreGuestU32(base, registers + 8u, texture_24);
    StoreGuestU32(base, registers + 16u,
                  (reg4 & 0xFFFFFC03u) | (texture_2c & 0x000003FCu));
    StoreGuestU32(base, registers,
                  (reg0 & 0x003FFC00u) | (texture_1c & 0xFFC003FFu));
    StoreGuestU32(base, registers + 4u,
                  (reg1 & 0x00000800u) | (FableGpuAddress(base_address) &
                                           0xFFFFF7FFu));
    StoreGuestU32(base, registers + 12u,
                  (reg3 & 0x7FF80000u) | (texture_28 & 0x8007FFFFu));
    StoreGuestU32(base, registers + 20u,
                  (reg5 & 0x000001FFu) |
                      (FableGpuAddress(texture_30) & 0xFFFFFE00u));

    uint32_t max_lod = LoadGuestU8(base, device + kTextureMaxLodBaseOffset + slot);
    max_lod = std::max(max_lod, (texture_2c >> 2u) & 0xFu);
    uint32_t reg4_lod =
        ((4u * max_lod) & 0x3Cu) |
        (LoadGuestU32(base, registers + 16u) & 0xFFFFFC03u) |
        (texture_2c & 0x000003C0u);
    StoreGuestU32(base, registers + 16u, reg4_lod);

    uint32_t min_lod = LoadGuestU8(base, device + kTextureMinLodBaseOffset + slot);
    min_lod = std::min(min_lod, (texture_2c >> 6u) & 0xFu);
    StoreGuestU32(base, registers + 16u,
                  ((min_lod << 6u) & 0x3C0u) |
                      (reg4_lod & 0xFFFFFC3Fu));
    OrFableGuestQwordLow(base, device + kTextureFetchDirtyQwordOffset, args.r6);
  }

  StoreGuestU32(base, binding_address, texture);
  RetireFableBoundResourceObject(base, device, previous_texture);
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableSetVertexShader(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kDirtyQwordOffset = 0x0010;
  constexpr uint32_t kVertexShaderDirtyBit = 0x00080000u;
  constexpr uint32_t kVertexShaderOffset = 0x3198;
  constexpr uint32_t kDeviceFlags3Offset = 0x2ABE;
  constexpr uint32_t kShaderPatchBaseOffset = 0x0368;
  constexpr uint32_t kShaderPatchOffsetOffset = 0x037C;

  const uint32_t device = args.r3;
  const uint32_t shader = args.r4;
  emulation.handled = true;
  emulation.result = device;
  if (shader != 0) {
    OrFableGuestQwordLow(base, device + kDirtyQwordOffset,
                         kVertexShaderDirtyBit);
  }

  RetireFableBoundResourceObject(base, device,
                                 LoadGuestU32(base, device + kVertexShaderOffset));
  StoreGuestU32(base, device + kVertexShaderOffset, shader);
  StoreGuestU8(base, device + kDeviceFlags3Offset,
               LoadGuestU8(base, device + kDeviceFlags3Offset) & 0x7Fu);

  if (shader != 0) {
    const uint32_t patch_offset =
        LoadGuestU32(base, shader + kShaderPatchOffsetOffset);
    if (patch_offset != 0) {
      ApplyFableShaderPatchStream(base, device,
                                  shader + kShaderPatchBaseOffset +
                                      patch_offset,
                                  0x0000);
    }
  }
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableSetPixelShader(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kDirtyQwordOffset = 0x0010;
  constexpr uint32_t kPixelShaderDirtyBits = 0x00120000u;
  constexpr uint32_t kPixelShaderOffset = 0x3194;
  constexpr uint32_t kShaderPatchBaseOffset = 0x0028;
  constexpr uint32_t kShaderPatchOffsetOffset = 0x003C;

  const uint32_t device = args.r3;
  const uint32_t shader = args.r4;
  emulation.handled = true;
  emulation.result = device;
  RetireFableBoundResourceObject(base, device,
                                 LoadGuestU32(base, device + kPixelShaderOffset));
  StoreGuestU32(base, device + kPixelShaderOffset, shader);
  OrFableGuestQwordLow(base, device + kDirtyQwordOffset,
                       kPixelShaderDirtyBits);

  if (shader != 0) {
    const uint32_t patch_offset =
        LoadGuestU32(base, shader + kShaderPatchOffsetOffset);
    if (patch_offset != 0) {
      ApplyFableShaderPatchStream(base, device,
                                  shader + kShaderPatchBaseOffset +
                                      patch_offset,
                                  0x0008);
    }
  }
  return emulation;
}

void StoreFableViewportScissor(uint8_t *base, uint32_t device, int32_t left,
                               int32_t top, int32_t right, int32_t bottom) {
  constexpr uint32_t kPackedScissorMinOffset = 0x28C4;
  constexpr uint32_t kPackedScissorMaxOffset = 0x28C8;

  const uint32_t packed_min_old =
      LoadGuestU32(base, device + kPackedScissorMinOffset);
  const uint32_t packed_max_old =
      LoadGuestU32(base, device + kPackedScissorMaxOffset);
  StoreGuestU32(base, device + kPackedScissorMinOffset,
                (packed_min_old & 0x80008000u) |
                    (static_cast<uint32_t>(left) & 0x7FFFu) |
                    ((static_cast<uint32_t>(top) << 16u) & 0x7FFF0000u));
  StoreGuestU32(base, device + kPackedScissorMaxOffset,
                (packed_max_old & 0x80008000u) |
                    (static_cast<uint32_t>(right) & 0x7FFFu) |
                    ((static_cast<uint32_t>(bottom) << 16u) & 0x7FFF0000u));
}

FableGuestCommandSubmitEmulation ApplyFableViewportState(
    uint8_t *base, uint32_t device, float x, float y, float width,
    float height, float min_depth, float max_depth) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || device == 0 || !std::isfinite(x) ||
      !std::isfinite(y) || !std::isfinite(width) || !std::isfinite(height) ||
      !std::isfinite(min_depth) || !std::isfinite(max_depth) || width < 0.0f ||
      height < 0.0f) {
    return emulation;
  }

  constexpr uint32_t kViewportTransformHalfWidthOffset = 0x2908;
  constexpr uint32_t kViewportTransformCenterXOffset = 0x290C;
  constexpr uint32_t kViewportTransformNegHalfHeightOffset = 0x2910;
  constexpr uint32_t kViewportTransformCenterYOffset = 0x2914;
  constexpr uint32_t kViewportTransformDepthScaleOffset = 0x2918;
  constexpr uint32_t kViewportTransformDepthBiasOffset = 0x291C;
  constexpr uint32_t kDirtyQwordOffset = 0x0010;
  constexpr uint32_t kViewportDirtyBits = 0x07E00000u;
  constexpr uint32_t kViewportXOffset = 0x3168;
  constexpr uint32_t kViewportYOffset = 0x316C;
  constexpr uint32_t kViewportWidthOffset = 0x3170;
  constexpr uint32_t kViewportHeightOffset = 0x3174;
  constexpr uint32_t kViewportMinZOffset = 0x3178;
  constexpr uint32_t kViewportMaxZOffset = 0x317C;
  constexpr uint32_t kViewportUnknownOffset = 0x3180;

  const FableSurfaceExtent extent =
      CurrentFableViewportClampExtent(base, device);
  if (extent.width != 0 && extent.height != 0 && x >= 0.0f && y >= 0.0f) {
    width =
        std::min(width, std::max(0.0f, static_cast<float>(extent.width) - x));
    height =
        std::min(height, std::max(0.0f, static_cast<float>(extent.height) - y));
  }

  emulation.handled = true;
  emulation.result = device;
  StoreGuestF32(base, device + kViewportXOffset, x);
  StoreGuestF32(base, device + kViewportYOffset, y);
  StoreGuestF32(base, device + kViewportWidthOffset, width);
  StoreGuestF32(base, device + kViewportHeightOffset, height);
  StoreGuestF32(base, device + kViewportMinZOffset, min_depth);
  StoreGuestF32(base, device + kViewportMaxZOffset, max_depth);
  StoreGuestU32(base, device + kViewportUnknownOffset, 0);

  const float half_width = width * 0.5f;
  const float half_height = height * 0.5f;
  StoreGuestF32(base, device + kViewportTransformHalfWidthOffset, half_width);
  StoreGuestF32(base, device + kViewportTransformCenterXOffset,
                half_width + x);
  StoreGuestF32(base, device + kViewportTransformNegHalfHeightOffset,
                -half_height);
  StoreGuestF32(base, device + kViewportTransformCenterYOffset,
                half_height + y);
  StoreGuestF32(base, device + kViewportTransformDepthScaleOffset,
                max_depth - min_depth);
  StoreGuestF32(base, device + kViewportTransformDepthBiasOffset, min_depth);

  const int32_t left = static_cast<int32_t>(std::floor(x));
  const int32_t top = static_cast<int32_t>(std::floor(y));
  const int32_t right = static_cast<int32_t>(std::ceil(x + width));
  const int32_t bottom = static_cast<int32_t>(std::ceil(y + height));
  StoreFableViewportScissor(base, device, left, top, right, bottom);
  FableDirectMirrorViewportState(
      device, MakeFableViewport(x, y, width, height, min_depth, max_depth));
  FableDirectMirrorScissorState(device,
                                MakeFableScissor(left, top, right, bottom));
  OrFableGuestQwordLow(base, device + kDirtyQwordOffset, kViewportDirtyBits);
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableSetViewportPointer(
    const RendererHookArgs &args, uint8_t *base) {
  if (base == nullptr || args.r3 == 0 || args.r4 == 0) {
    return {};
  }

  const uint32_t device = args.r3;
  const uint32_t viewport = args.r4;
  const float x = static_cast<float>(LoadGuestU32(base, viewport));
  const float y = static_cast<float>(LoadGuestU32(base, viewport + 4u));
  const float width = static_cast<float>(LoadGuestU32(base, viewport + 8u));
  const float height = static_cast<float>(LoadGuestU32(base, viewport + 12u));
  const float min_depth = LoadGuestF32(base, viewport + 16u);
  const float max_depth = LoadGuestF32(base, viewport + 20u);
  return ApplyFableViewportState(base, device, x, y, width, height, min_depth,
                                 max_depth);
}

FableGuestCommandSubmitEmulation EmulateFableSetViewportFloats(
    const RendererHookArgs &args, uint8_t *base) {
  return ApplyFableViewportState(base, args.r3, static_cast<float>(args.f1),
                                 static_cast<float>(args.f2),
                                 static_cast<float>(args.f3),
                                 static_cast<float>(args.f4),
                                 static_cast<float>(args.f5),
                                 static_cast<float>(args.f6));
}

FableGuestCommandSubmitEmulation ApplyFableScissorState(
    uint8_t *base, uint32_t device, int32_t left, int32_t top, int32_t right,
    int32_t bottom, bool store_requested_rect) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || device == 0) {
    return emulation;
  }

  constexpr uint32_t kRequestedScissorLeftOffset = 0x3184;
  constexpr uint32_t kRequestedScissorTopOffset = 0x3188;
  constexpr uint32_t kRequestedScissorRightOffset = 0x318C;
  constexpr uint32_t kRequestedScissorBottomOffset = 0x3190;

  emulation.handled = true;
  emulation.result = device;
  if (store_requested_rect) {
    StoreGuestU32(base, device + kRequestedScissorLeftOffset,
                  static_cast<uint32_t>(left));
    StoreGuestU32(base, device + kRequestedScissorTopOffset,
                  static_cast<uint32_t>(top));
    StoreGuestU32(base, device + kRequestedScissorRightOffset,
                  static_cast<uint32_t>(right));
    StoreGuestU32(base, device + kRequestedScissorBottomOffset,
                  static_cast<uint32_t>(bottom));
  }
  StoreFableViewportScissor(base, device, left, top, right, bottom);
  RendererScissorSnapshot scissor;
  scissor.left = left;
  scissor.top = top;
  scissor.right = right;
  scissor.bottom = bottom;
  scissor.valid = right > left && bottom > top;
  FableDirectMirrorScissorState(device, scissor);
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableSetScissorPointer(
    const RendererHookArgs &args, uint8_t *base) {
  if (base == nullptr || args.r3 == 0 || args.r4 == 0) {
    return {};
  }

  const uint32_t device = args.r3;
  const uint32_t scissor = args.r4;
  const int32_t left = static_cast<int32_t>(LoadGuestU32(base, scissor));
  const int32_t top = static_cast<int32_t>(LoadGuestU32(base, scissor + 4u));
  const int32_t right = static_cast<int32_t>(LoadGuestU32(base, scissor + 8u));
  const int32_t bottom =
      static_cast<int32_t>(LoadGuestU32(base, scissor + 12u));
  return ApplyFableScissorState(base, device, left, top, right, bottom,
                                true /* store_requested_rect */);
}

FableGuestCommandSubmitEmulation EmulateFableSetSurfaceClip(
    const RendererHookArgs &args, uint8_t *base) {
  return ApplyFableScissorState(base, args.r3, static_cast<int32_t>(args.r4),
                                static_cast<int32_t>(args.r5),
                                static_cast<int32_t>(args.r6),
                                static_cast<int32_t>(args.r7),
                                false /* store_requested_rect */);
}

FableGuestCommandSubmitEmulation EmulateFableSetShaderGPRAllocation(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (!base || !IsLikelyFableObjectAddress(args.r3)) {
    return emulation;
  }

  constexpr uint32_t kShaderGprAllocationOffset = 2730u * sizeof(uint32_t);
  constexpr uint32_t kPackedSurfaceClipMinOffset = 0x28C4;
  constexpr uint32_t kPackedSurfaceClipMaxOffset = 0x28C8;

  const uint32_t device = args.r3;
  uint32_t vertex_gprs = args.r5;
  uint32_t pixel_gprs = args.r6;
  if (vertex_gprs == 0 && pixel_gprs == 0) {
    vertex_gprs = 64;
    pixel_gprs = 64;
  }

  uint32_t allocation = 0;
  if ((args.r4 & 1u) == 0) {
    const uint32_t packed =
        ((vertex_gprs << 8u) & 0x00007F00u) | (pixel_gprs & 0xFFFF80FFu);
    const uint32_t shifted = packed << 4u;
    allocation = (shifted & 0x0007F000u) | (shifted & 0x000007F0u) |
                 (LoadGuestU32(base, device + kShaderGprAllocationOffset) &
                  0xFFF8080Fu);
  }
  StoreGuestU32(base, device + kShaderGprAllocationOffset, allocation);

  const uint32_t packed_min =
      LoadGuestU32(base, device + kPackedSurfaceClipMinOffset);
  const uint32_t packed_max =
      LoadGuestU32(base, device + kPackedSurfaceClipMaxOffset);
  const int32_t left = static_cast<int32_t>(packed_min << 17u) >> 17;
  const int32_t top = static_cast<int32_t>(packed_min << 1u) >> 17;
  const int32_t right = static_cast<int32_t>(packed_max << 17u) >> 17;
  const int32_t bottom = static_cast<int32_t>(packed_max << 1u) >> 17;
  ApplyFableScissorState(base, device, left, top, right, bottom,
                         false /* store_requested_rect */);

  emulation.handled = true;
  emulation.result = device;
  return emulation;
}

uint16_t FableGammaIdentityLookup(uint16_t value) {
  return static_cast<uint16_t>((static_cast<uint32_t>(value) >> 6u) << 6u);
}

std::array<uint16_t, 768> ConvertFableGammaRamp(uint8_t *base,
                                                uint32_t ramp,
                                                bool pair_delta_encoded) {
  std::array<uint16_t, 768> converted{};
  if (!pair_delta_encoded) {
    for (uint32_t channel = 0; channel < 3u; ++channel) {
      for (uint32_t entry = 0; entry < 256u; ++entry) {
        const uint32_t source =
            ramp + (channel * 256u + entry) * sizeof(uint16_t);
        converted[channel * 256u + entry] =
            FableGammaIdentityLookup(LoadGuestU16(base, source));
      }
    }
    return converted;
  }

  for (uint32_t channel = 0; channel < 3u; ++channel) {
    for (uint32_t pair = 0; pair < 128u; ++pair) {
      const uint32_t first_entry = pair * 2u;
      const uint32_t second_entry = first_entry + 1u;
      const uint32_t first_source =
          ramp + (channel * 256u + first_entry) * sizeof(uint16_t);
      const uint32_t second_source =
          ramp + (channel * 256u + second_entry) * sizeof(uint16_t);
      const uint32_t first = LoadGuestU16(base, first_source) >> 6u;
      const uint32_t second = LoadGuestU16(base, second_source) >> 6u;
      const uint32_t combined = std::min(first + second, 1023u);
      const uint16_t base_value = static_cast<uint16_t>(first << 6u);
      const uint16_t combined_value = static_cast<uint16_t>(combined << 6u);
      converted[channel * 256u + first_entry] = base_value;
      converted[channel * 256u + second_entry] =
          static_cast<uint16_t>(combined_value - base_value);
    }
  }
  return converted;
}

FableGuestCommandSubmitEmulation EmulateFableSetGammaRamp(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  constexpr uint32_t kGammaEntries = 256u * 3u;
  constexpr uint32_t kGammaBytes = kGammaEntries * sizeof(uint16_t);
  constexpr uint32_t kDeviceGammaCacheOffset = 3751u * sizeof(uint32_t);
  constexpr uint32_t kPreviousGammaCacheOffset = 5988u * sizeof(uint32_t);
  constexpr uint32_t kGammaFlagsOffset = 5989u * sizeof(uint32_t);
  constexpr uint32_t kDeviceDirtyFlagsOffset = 4177u * sizeof(uint32_t);

  const uint32_t device = args.r3;
  const uint32_t ramp = args.r5;
  if (base == nullptr || !IsLikelyFableObjectAddress(device) ||
      !IsReadableGuestByteRange(base, ramp, kGammaBytes) ||
      !IsReadableGuestByteRange(base, device + kDeviceGammaCacheOffset,
                                kGammaBytes) ||
      !IsReadableGuestByteRange(base, device + kGammaFlagsOffset,
                                sizeof(uint32_t)) ||
      !IsReadableGuestByteRange(base, device + kDeviceDirtyFlagsOffset,
                                sizeof(uint32_t))) {
    return {};
  }

  const bool pair_delta_encoded = symbol == "sub_82B98318";
  const std::array<uint16_t, kGammaEntries> converted =
      ConvertFableGammaRamp(base, ramp, pair_delta_encoded);
  const uint32_t cache = device + kDeviceGammaCacheOffset;
  bool changed = false;
  for (uint32_t i = 0; i < kGammaEntries; ++i) {
    if (LoadGuestU16(base, cache + i * sizeof(uint16_t)) != converted[i]) {
      changed = true;
      break;
    }
  }

  if (changed) {
    const uint32_t previous_cache =
        LoadGuestU32(base, device + kPreviousGammaCacheOffset);
    const uint32_t flags = LoadGuestU32(base, device + kGammaFlagsOffset);
    if (previous_cache != 0 && (flags & 0x80000000u) == 0 &&
        IsReadableGuestByteRange(base, previous_cache, kGammaBytes)) {
      CopyGuestBytes(base, previous_cache, cache, kGammaBytes);
    }
    StoreGuestU32(base, device + kGammaFlagsOffset, flags | 0x80000000u);
    for (uint32_t i = 0; i < kGammaEntries; ++i) {
      StoreGuestU16(base, cache + i * sizeof(uint16_t), converted[i]);
    }
    StoreGuestU32(base, device + kDeviceDirtyFlagsOffset,
                  LoadGuestU32(base, device + kDeviceDirtyFlagsOffset) | 2u);
  }

  return {.handled = true, .result = cache};
}

FableGuestCommandSubmitEmulation EmulateFableSetScreenExtentQueryMode(
    const RendererHookArgs &args, uint8_t *base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3)) {
    return {};
  }

  constexpr uint32_t kScreenExtentQueryModeOffset = 0x2AA4;
  const uint32_t device = args.r3;
  const uint32_t current =
      LoadGuestU32(base, device + kScreenExtentQueryModeOffset);
  StoreGuestU32(base, device + kScreenExtentQueryModeOffset,
                ((args.r4 << 12u) & 0x00003000u) |
                    (current & 0xFFFFCFFFu));
  return {.handled = true, .result = device};
}

FableGuestCommandSubmitEmulation EmulateFableSetScalerParameters(
    const RendererHookArgs &args, uint8_t *base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3) ||
      !IsLikelyGuestDwordAddress(args.r4)) {
    return {};
  }

  constexpr uint32_t kScalerParametersOffset = 0x359C;
  constexpr uint32_t kScalerDirtyFlagsOffset = 0x2ABE;
  constexpr uint32_t kScalerParameterDwords = 14;
  const uint32_t device = args.r3;
  const uint32_t parameter_pointer = args.r4;

  for (uint32_t index = 0; index < kScalerParameterDwords; ++index) {
    StoreGuestU32(base, device + kScalerParametersOffset +
                            index * sizeof(uint32_t),
                  0);
  }
  for (uint32_t index = 0; index < 6u; ++index) {
    StoreGuestU32(base, device + kScalerParametersOffset +
                            index * sizeof(uint32_t),
                  LoadGuestU32(base, parameter_pointer +
                                      index * sizeof(uint32_t)));
  }
  const uint32_t filter_enabled =
      LoadGuestU32(base, parameter_pointer + 6u * sizeof(uint32_t)) != 0 ? 3u
                                                                        : 0u;
  StoreGuestU32(base, device + kScalerParametersOffset +
                          6u * sizeof(uint32_t),
                filter_enabled);
  StoreGuestU32(base, device + kScalerParametersOffset +
                          10u * sizeof(uint32_t),
                filter_enabled);
  StoreGuestU8(base, device + kScalerDirtyFlagsOffset,
               static_cast<uint8_t>(
                   LoadGuestU8(base, device + kScalerDirtyFlagsOffset) |
                   0x10u));
  return {.handled = true, .result = device};
}

FableGuestCommandSubmitEmulation EmulateFableSetupScaler(
    const RendererHookArgs &args, uint8_t *base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3)) {
    return {};
  }

  return {.handled = true, .result = args.r3};
}

bool FableColorSurfaceUsesFloatDepthCompare(uint8_t *base, uint32_t surface) {
  if (!base || !IsLikelyFableObjectAddress(surface)) {
    return false;
  }

  const uint32_t format = LoadGuestU32(base, surface + 0x1Cu) & 0xFu;
  return format == 5u || format == 7u || format == 15u;
}

bool FableBoundTargetsMatchTrackedTargets(uint8_t *base, uint32_t device) {
  if (!base || !IsLikelyFableObjectAddress(device)) {
    return false;
  }

  constexpr uint32_t kDisplayFlagsOffset = 0x2ABC;
  constexpr uint32_t kRenderTargetBaseOffset = 0x3098;
  constexpr uint32_t kDepthStencilOffset = 0x30A8;
  constexpr uint32_t kTrackedTargetBaseOffset = 0x31B8;
  constexpr uint32_t kTrackedDepthOffset = 0x31C8;
  constexpr uint32_t kPredicateCountOffset = 0x31CC;
  if (LoadGuestU32(base, device + kPredicateCountOffset) != 0 ||
      (LoadGuestU8(base, device + kDisplayFlagsOffset) & 0x0Cu) != 0) {
    return false;
  }
  if ((LoadGuestU8(base, device + kDisplayFlagsOffset) & 0x10u) != 0) {
    return true;
  }
  if ((LoadGuestU8(base, device + kDisplayFlagsOffset) & 0x20u) == 0) {
    return false;
  }

  for (uint32_t i = 0; i < 4u; ++i) {
    const uint32_t target =
        LoadGuestU32(base, device + kRenderTargetBaseOffset +
                               i * sizeof(uint32_t));
    const uint32_t tracked =
        LoadGuestU32(base, device + kTrackedTargetBaseOffset +
                               i * sizeof(uint32_t));
    if (target != 0 && tracked != target) {
      return false;
    }
  }

  const uint32_t depth = LoadGuestU32(base, device + kDepthStencilOffset);
  return depth == 0 || LoadGuestU32(base, device + kTrackedDepthOffset) == depth;
}

void UpdateFableTargetPredicateFlag(uint8_t *base, uint32_t device) {
  constexpr uint32_t kDisplayFlagsOffset = 0x2ABC;
  const uint8_t flags = LoadGuestU8(base, device + kDisplayFlagsOffset);
  const uint8_t predicate_ready =
      FableBoundTargetsMatchTrackedTargets(base, device) ? 1u : 0u;
  StoreGuestU8(base, device + kDisplayFlagsOffset,
               static_cast<uint8_t>((flags & 0xFEu) | predicate_ready));
}

void ApplyFableSurfaceExtentState(uint8_t *base, uint32_t device,
                                  uint32_t surface) {
  if (!base || !IsLikelyFableObjectAddress(device) ||
      !IsLikelyFableObjectAddress(surface)) {
    return;
  }

  constexpr uint32_t kSurfaceExtentOffset = 0x18;
  constexpr uint32_t kSurfaceExtentRegisterOffset = 0x2880;
  constexpr uint32_t kSurfaceExtentAuxOffset = 0x29BC;
  constexpr uint32_t kPrimaryDirtyQwordOffset = 0x0010;
  constexpr uint32_t kWideDirtyQwordOffset = 0x0020;
  constexpr uint32_t kDefaultViewportPointer = 0x82001AA8;

  const uint32_t extent_register = LoadGuestU32(base, surface + kSurfaceExtentOffset);
  uint32_t aux = 0;
  if ((extent_register & 0x00030000u) != 0) {
    aux = (((extent_register >> 16u) & 3u) == 1u) ? 0x8001u : 0xC003u;
  }

  StoreGuestU32(base, device + kSurfaceExtentAuxOffset, aux);
  StoreGuestU32(base, device + kSurfaceExtentRegisterOffset, extent_register);
  OrFableGuestQwordLow(base, device + kPrimaryDirtyQwordOffset, 0x02000000u);
  OrFableGuestQwordLow(base, device + kWideDirtyQwordOffset, 0x00000010u);
  StoreGuestU32(base, device + 0x3184u, 0);
  StoreGuestU32(base, device + 0x3188u, 0);
  StoreGuestU32(base, device + 0x318Cu, 0xFFFFu);
  StoreGuestU32(base, device + 0x3190u, 0xFFFFu);

  RendererHookArgs viewport_args = {};
  viewport_args.r3 = device;
  viewport_args.r4 = kDefaultViewportPointer;
  viewport_args.r4_u64 = kDefaultViewportPointer;
  EmulateFableSetViewportPointer(viewport_args, base);
}

void UpdateFableDepthControlBits(uint8_t *base, uint32_t device,
                                 uint32_t surface) {
  if (!base || !IsLikelyFableObjectAddress(device)) {
    return;
  }

  constexpr uint32_t kDepthControlOffset = 0x2934;
  constexpr uint32_t kDepthEnableStateOffset = 0x2E64;
  constexpr uint32_t kDepthWriteStateOffset = 0x2E68;
  constexpr uint32_t kPrimaryDirtyQwordOffset = 0x0010;

  uint32_t depth_control = LoadGuestU32(base, device + kDepthControlOffset);
  uint32_t depth_enable = LoadGuestU32(base, device + kDepthEnableStateOffset);
  uint32_t depth_write = LoadGuestU32(base, device + kDepthWriteStateOffset);
  if (surface == 0) {
    depth_enable = 0;
    depth_write = 0;
  }

  depth_control = (depth_control & ~0x3u) | (depth_write & 1u) |
                  ((depth_enable << 1u) & 2u);
  StoreGuestU32(base, device + kDepthControlOffset, depth_control);
  OrFableGuestQwordLow(base, device + kPrimaryDirtyQwordOffset, 0x00020800u);
}

FableGuestCommandSubmitEmulation EmulateFableSetRenderTarget(
    const RendererHookArgs &args, uint8_t *base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3) || args.r4 >= 4u) {
    return {};
  }

  constexpr uint32_t kPrimaryDirtyQwordOffset = 0x0010;
  constexpr uint32_t kRenderTargetBaseOffset = 0x3098;
  constexpr uint32_t kRenderTargetRegisterBaseOffset = 0x2884;
  constexpr uint32_t kMrtEnableRegisterOffset = 0x28DC;
  constexpr uint32_t kDisplayFlagsOffset = 0x2ABC;
  constexpr uint32_t kDisplayFlags3Offset = 0x2ABF;
  constexpr uint64_t kRenderTargetDirtyMaskBase =
      0x0002010700000000ull | 0x80000000ull;

  const uint32_t device = args.r3;
  const uint32_t slot = args.r4;
  const uint32_t surface = args.r5;
  StoreGuestU32(base,
                device + kRenderTargetBaseOffset + slot * sizeof(uint32_t),
                surface);
  if (surface != 0) {
    const uint32_t register_index = slot == 0 ? 0u : slot + 1u;
    StoreGuestU32(base,
                  device + kRenderTargetRegisterBaseOffset +
                      register_index * sizeof(uint32_t),
                  LoadGuestU32(base, surface + 0x1Cu));
    OrFableGuestQword(base, device + kPrimaryDirtyQwordOffset,
                      kRenderTargetDirtyMaskBase >> (register_index + 7u));
  }

  if (slot == 0) {
    const uint8_t flags3 =
        static_cast<uint8_t>(LoadGuestU8(base, device + kDisplayFlags3Offset) &
                             ~0x08u);
    StoreGuestU8(base, device + kDisplayFlags3Offset, flags3);
    if (FableColorSurfaceUsesFloatDepthCompare(base, surface)) {
      StoreGuestU8(base, device + kDisplayFlags3Offset,
                   static_cast<uint8_t>(flags3 | 0x08u));
      OrFableGuestQwordLow(base, device + kPrimaryDirtyQwordOffset,
                           0x00100000u);
    }
    if (surface != 0) {
      ApplyFableSurfaceExtentState(base, device, surface);
    } else {
      const uint32_t depth = LoadGuestU32(base, device + 0x30A8u);
      if (depth != 0) {
        ApplyFableSurfaceExtentState(base, device, depth);
      }
    }
  }

  uint32_t mrt_mask = LoadGuestU32(base, device + kMrtEnableRegisterOffset);
  const uint32_t render_target_state =
      LoadGuestU32(base, device + 0x2E54u + slot * sizeof(uint32_t));
  if (surface == 0) {
    mrt_mask &= ~(0xFu << (slot * 4u));
  } else {
    mrt_mask = (mrt_mask & ~(0xFu << (slot * 4u))) |
               ((render_target_state & 0xFu) << (slot * 4u));
  }
  StoreGuestU32(base, device + kMrtEnableRegisterOffset, mrt_mask);
  OrFableGuestQwordLow(base, device + kPrimaryDirtyQwordOffset, 0x00000010u);
  UpdateFableTargetPredicateFlag(base, device);
  return {.handled = true, .result = args.r3};
}

FableGuestCommandSubmitEmulation EmulateFableSetDepthStencilSurface(
    const RendererHookArgs &args, uint8_t *base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3)) {
    return {};
  }

  constexpr uint32_t kPrimaryDirtyQwordOffset = 0x0010;
  constexpr uint32_t kDepthStencilOffset = 0x30A8;
  constexpr uint32_t kDepthSurfaceRegisterOffset = 0x2888;
  constexpr uint32_t kDepthControlOffset = 0x2940;
  constexpr uint32_t kDisplayFlagsOffset = 0x2ABC;
  constexpr uint32_t kDisplayFlags2Offset = 0x2ABE;
  const uint32_t device = args.r3;
  const uint32_t surface = args.r4;

  StoreGuestU32(base, device + kDepthStencilOffset, surface);
  if (surface != 0) {
    if (LoadGuestU32(base, device + 0x3098u) == 0) {
      ApplyFableSurfaceExtentState(base, device, surface);
    }
    StoreGuestU32(base, device + kDepthSurfaceRegisterOffset,
                  LoadGuestU32(base, surface + 0x1Cu));
    const uint32_t depth_control = LoadGuestU32(base, surface + 0x20u);
    StoreGuestU32(base, device + kDepthControlOffset, depth_control);
    StoreGuestU8(base, device + kDisplayFlags2Offset,
                 static_cast<uint8_t>(
                     (LoadGuestU8(base, device + kDisplayFlags2Offset) &
                      ~0x20u) |
                     ((depth_control & 1u) != 0 ? 0x20u : 0u)));
    OrFableGuestQwordLow(base, device + kPrimaryDirtyQwordOffset, 0x00820100u);
  } else {
    StoreGuestU32(base, device + kDepthControlOffset,
                  LoadGuestU32(base, device + kDepthControlOffset) &
                      0xFFFFFFF0u);
    StoreGuestU8(base, device + kDisplayFlags2Offset,
                 static_cast<uint8_t>(
                     LoadGuestU8(base, device + kDisplayFlags2Offset) &
                     ~0x20u));
    OrFableGuestQwordLow(base, device + kPrimaryDirtyQwordOffset, 0x00000100u);
  }

  UpdateFableTargetPredicateFlag(base, device);
  UpdateFableDepthControlBits(base, device, surface);
  OrFableGuestQwordLow(base, device + kPrimaryDirtyQwordOffset, 0x00020000u);
  (void)kDisplayFlagsOffset;
  return {.handled = true, .result = args.r3};
}

FableGuestCommandSubmitEmulation EmulateFableGetRenderTarget(
    const RendererHookArgs &args, uint8_t *base) {
  if (base == nullptr || !IsLikelyFableObjectAddress(args.r3) ||
      args.r4 >= 4u) {
    return {};
  }

  constexpr uint32_t kRenderTargetBaseOffset = 0x3098;
  const FableDirectStateSnapshot direct_state =
      GetFableDirectStateSnapshot();
  uint32_t surface = direct_state.targets.color[args.r4];
  if (surface == 0) {
    surface = LoadGuestU32(
        base, args.r3 + kRenderTargetBaseOffset + args.r4 * sizeof(uint32_t));
  } else {
    StoreGuestU32(base,
                  args.r3 + kRenderTargetBaseOffset +
                      args.r4 * sizeof(uint32_t),
                  surface);
  }

  if (surface != 0) {
    RendererHookArgs add_ref_args = {};
    add_ref_args.r3 = surface;
    EmulateFableResourceLifetime("sub_821FC178", add_ref_args, base);
  }

  if (!g_logged_fable_get_render_target_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns "
        "D3DDevice_GetRenderTarget/GetBackBuffer tracked surface reads");
    g_logged_fable_get_render_target_emulation = true;
  }

  return {.handled = true, .result = surface};
}

FableGuestCommandSubmitEmulation EmulateFableSurfaceSize(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (!RendererReplaceResourcesEnabled() || !IsFableSurfaceSizeHook(symbol)) {
    return {};
  }

  uint64_t width = args.r3;
  uint64_t height = args.r4;
  const uint32_t format = args.r5 & 0x3Fu;
  const uint32_t msaa = args.r6;
  if (msaa >= 1u) {
    height *= 2u;
  }
  if (msaa == 2u) {
    width *= 2u;
  }

  const uint64_t aligned_width = 80u * ((width + 79u) / 80u);
  const uint64_t aligned_height = (height + 15u) & ~uint64_t{15u};
  const uint64_t bytes_per_tile_unit =
      (format == 21u || format == 32u || format == 37u) ? 8u : 4u;
  const uint64_t tile_units =
      (aligned_width * aligned_height * bytes_per_tile_unit) / 0x1400u;
  const uint32_t result =
      tile_units > UINT32_MAX ? UINT32_MAX : static_cast<uint32_t>(tile_units);

  if (!g_logged_fable_surface_size_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns XGSurfaceSize "
        "surface tile-size queries with IDA-backed replacement formula "
        "width={} height={} format={} msaa={} result=0x{:08X}",
        args.r3, args.r4, format, msaa, result);
    g_logged_fable_surface_size_emulation = true;
  }

  return {.handled = true, .result = result};
}

FableGuestCommandSubmitEmulation EmulateFableSetRenderState(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3) ||
      FableRenderStateSlotForSymbol(symbol) == UINT32_MAX) {
    return {};
  }

  return {.handled = true, .result = args.r3};
}

FableGuestCommandSubmitEmulation EmulateFableSetSamplerState(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3) ||
      FableSamplerStateSlotForSymbol(symbol) == UINT32_MAX ||
      args.r4 >= kRendererD3DSamplerStateSlots) {
    return {};
  }

  return {.handled = true, .result = args.r3};
}

void MarkFableClearCommandDirty(uint8_t *base, uint32_t device,
                                bool full_device_clear) {
  if (!base || !IsLikelyFableObjectAddress(device)) {
    return;
  }

  constexpr uint32_t kPrimaryDirtyQwordOffset = 0x0010;
  constexpr uint32_t kSecondaryDirtyQwordOffset = 0x0018;
  constexpr uint32_t kWideDirtyQwordOffset = 0x0020;
  constexpr uint32_t kDisplayFlagsOffset = 0x2ABD;

  // D3DDevice_ClearF eventually returns through the device path and sets the
  // same dirty families as Fable's command builder. Keep those cheap
  // guest-visible bits moving while Plume owns the actual clear.
  if (full_device_clear) {
    OrFableGuestQword(base, device + kPrimaryDirtyQwordOffset, 1ull << 37u);
    OrFableGuestQwordLow(base, device + kPrimaryDirtyQwordOffset, 0x00000600u);
    StoreGuestU8(base, device + kDisplayFlagsOffset,
                 LoadGuestU8(base, device + kDisplayFlagsOffset) | 4u);
  }

  // D3D::ClearRect marks the target/clear/scissor related dirty groups after
  // writing the clear packet.
  OrFableGuestQwordLow(base, device + kSecondaryDirtyQwordOffset, 0x80000000u);
  OrFableGuestQword(base, device + kPrimaryDirtyQwordOffset,
                    (1ull << 57u) | (1ull << 56u));
  OrFableGuestQword(base, device + kWideDirtyQwordOffset, 1ull << 36u);
  OrFableGuestQwordLow(base, device + kPrimaryDirtyQwordOffset, 0x00000808u);
}

FableGuestCommandSubmitEmulation EmulateFableClearCommand(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3)) {
    return {};
  }

  MarkFableClearCommandDirty(base, args.r3, symbol == "sub_821E2FE0");
  return {.handled = true, .result = args.r3};
}

FableGuestCommandSubmitEmulation EmulateFableSetColorClear(
    const RendererHookArgs &args, uint8_t *base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3)) {
    return {};
  }

  constexpr uint32_t kWideDirtyQwordOffset = 0x0020;
  constexpr uint32_t kColorClearDirtyBits = 0x000000C0u;
  OrFableGuestQwordLow(base, args.r3 + kWideDirtyQwordOffset,
                       kColorClearDirtyBits);
  return {.handled = true, .result = 0};
}

}  // namespace refii::renderer
