#include "runtime/plume/resource/plume_resource_manager_internal.h"

namespace refii::renderer {
namespace {

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

#if REFII_RENDERER_HAS_PLUME
bool IsFableResolveCopyDraw(const RendererDrawEventSnapshot& draw_event) {
  constexpr uint32_t kXenosEdramModeCopy = 6u;
  return draw_event.pm4_registers_valid &&
         (draw_event.rb_modecontrol & 0x7u) == kXenosEdramModeCopy;
}

uint32_t NormalizeFableResolveDestinationBase(uint32_t destination_base) {
  return destination_base & ~0xFFFu;
}

uint32_t NormalizeFableGpuBase(uint32_t base) {
  if (base >= 0x41000000u && base < 0x50000000u) {
    return base - 0x41000000u;
  }
  if (base >= 0x40000000u && base < 0x41000000u) {
    return base - 0x40000000u;
  }
  return base;
}

uint32_t DecodeFableGpuBase(uint32_t base) {
  return (((base >> 20u) + 512u) & 0x1000u) + (base & 0x1FFFFFFFu);
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

bool FableDisplayBasesMatch(uint32_t lhs, uint32_t rhs) {
  if (lhs == 0 || rhs == 0) {
    return false;
  }
  const std::array<uint32_t, 4> lhs_candidates =
      FableDisplayBaseCandidates(lhs);
  const std::array<uint32_t, 4> rhs_candidates =
      FableDisplayBaseCandidates(rhs);
  for (uint32_t lhs_candidate : lhs_candidates) {
    if (lhs_candidate == 0) {
      continue;
    }
    for (uint32_t rhs_candidate : rhs_candidates) {
      if (rhs_candidate != 0 && lhs_candidate == rhs_candidate) {
        return true;
      }
    }
  }
  return false;
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
