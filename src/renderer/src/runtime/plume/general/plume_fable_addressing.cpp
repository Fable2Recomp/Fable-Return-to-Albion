#include "runtime/refii_renderer/plume/general/plume_fable_addressing.h"

#include "runtime/plume/resource/plume_resource_manager_internal.h"

namespace refii::renderer {
namespace {

constexpr uint32_t kFableObjectAddressMin = 0x40000000u;
constexpr uint32_t kFableObjectAddressMax = 0x80000000u;
constexpr uint32_t kMaxFableScratchUploadSnapshotSize = 16u << 20;

#if REFII_RENDERER_HAS_PLUME
void AddFableDisplayBaseCandidate(std::array<uint32_t, 4>& candidates,
                                  uint32_t candidate) {
  if (candidate == 0) {
    return;
  }
  for (uint32_t existing : candidates) {
    if (existing == candidate) {
      return;
    }
  }
  for (uint32_t& slot : candidates) {
    if (slot == 0) {
      slot = candidate;
      return;
    }
  }
}
#endif

}  // namespace

bool IsLikelyFableObjectAddress(uint32_t guest_address) {
  return guest_address >= kFableObjectAddressMin &&
         guest_address < kFableObjectAddressMax &&
         (guest_address & (alignof(uint32_t) - 1u)) == 0;
}

bool IsLikelyFableGpuByteBufferRange(uint32_t guest_address, uint32_t size) {
  return guest_address >= 0x1000u && guest_address < kFableObjectAddressMin &&
         size != 0 && size <= kMaxFableScratchUploadSnapshotSize;
}

bool IsLikelyFableHighScratchByteBufferRange(uint32_t guest_address,
                                             uint32_t size) {
  return guest_address >= 0x80000000u &&
         (guest_address & (alignof(uint32_t) - 1u)) == 0 && size != 0 &&
         size <= kMaxFableScratchUploadSnapshotSize;
}

#if REFII_RENDERER_HAS_PLUME

uint32_t NormalizeFableResolveDestinationBase(uint32_t destination_base) {
  return destination_base & ~0xFFFu;
}

uint32_t NormalizeFableGpuAddress(uint32_t pointer) {
  if (pointer >= 0x41000000u && pointer < 0x50000000u) {
    return pointer - 0x41000000u;
  }
  if (pointer >= 0x40000000u && pointer < 0x41000000u) {
    return pointer - 0x40000000u;
  }
  return pointer;
}

uint32_t NormalizeFableGpuBase(uint32_t base) {
  return NormalizeFableGpuAddress(base);
}

uint32_t DecodeFableGpuMemoryPointer(uint32_t pointer) {
  return (((pointer >> 20u) + 512u) & 0x1000u) +
         (pointer & 0x1FFFFFFFu);
}

uint32_t DecodeFableGpuBufferPointer(uint32_t pointer) {
  return DecodeFableGpuMemoryPointer(pointer & ~3u);
}

uint32_t DecodeFableGpuBase(uint32_t base) {
  return DecodeFableGpuMemoryPointer(base);
}

bool IsFableGuestPhysicalAddress(uint32_t address) {
  // Xenos physical memory is 512MB. Anything at or above 0x20000000 already
  // carries a view prefix (0x40 virtual, 0x80/0x90 xex, 0xA0/0xC0/0xE0
  // physical views) and must be left alone.
  return address != 0 && address < 0x20000000u;
}

uint32_t FableGuestPhysicalViewAddress(uint32_t address) {
  if (!IsFableGuestPhysicalAddress(address)) {
    return address;
  }
  // 0xC0000000 and not 0xE0000000: the 0xE0 view is mapped from file offset
  // 0x100001000 (rexglue xmemory.cpp map_info), so guest 0xE0000000+P reaches
  // physical P+0x1000 — one 4KB page PAST the intended texel. The 0xC0 view
  // is mapped at 0x100000000 with no skew, so 0xC0000000|P is exactly P.
  return 0xC0000000u | address;
}

std::array<uint32_t, 4> FableDisplayBaseCandidates(uint32_t base) {
  std::array<uint32_t, 4> candidates = {};
  const uint32_t normalized = NormalizeFableGpuBase(base);
  const uint32_t decoded = DecodeFableGpuBase(normalized);
  AddFableDisplayBaseCandidate(candidates, normalized);
  AddFableDisplayBaseCandidate(candidates, decoded);
  AddFableDisplayBaseCandidate(candidates,
                               NormalizeFableResolveDestinationBase(normalized));
  AddFableDisplayBaseCandidate(candidates,
                               NormalizeFableResolveDestinationBase(decoded));
  return candidates;
}


const FableRenderTargetResolveAlias* FindFableResolveAliasLocked(
    uint32_t destination_base) {
  if (destination_base == 0) {
    return nullptr;
  }

  const std::array<uint32_t, 4> candidates =
      FableDisplayBaseCandidates(destination_base);
  for (uint32_t candidate : candidates) {
    if (candidate == 0) {
      continue;
    }
    const auto it =
        g_plume_resources.render_target_resolve_aliases.find(candidate);
    if (it != g_plume_resources.render_target_resolve_aliases.end()) {
      return &it->second;
    }
  }

  return nullptr;
}
#endif

}  // namespace refii::renderer
