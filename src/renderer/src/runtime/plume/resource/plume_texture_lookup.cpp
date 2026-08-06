#include "runtime/plume/resource/plume_resource_manager_internal.h"

#include "rex/logging.h"

#include <cstddef>
#include <cstdint>
#include <iterator>

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
namespace {






}  // namespace







bool SetFableTextureDescriptorAtIndexLocked(
    uint32_t set_index, uint32_t descriptor_index,
    plume::RenderTexture* texture, plume::RenderTextureLayout layout,
    plume::RenderTextureView* view) {
  if (set_index >= std::size(g_plume_resources.texture_descriptor_sets) ||
      descriptor_index >= kFableDescriptorCount ||
      !g_plume_resources.texture_descriptor_sets[set_index] || !texture ||
      !view) {
    return false;
  }

  PlumeCachedTextureDescriptor& cached =
      g_plume_resources.cached_texture_descriptors[set_index][descriptor_index];
  // DESCRIPTOR-WRITE HISTORY (2026-07-25): investigating a confirmed-real
  // (not a RenderDoc replay artifact -- verified via the bindless CSV tool,
  // which resolves other shaders' heap indices to real resources fine)
  // tree-billboard bug: the shared-constants buffer correctly names heap
  // indices 28/24 for slots 13/14/15, but RenderDoc's replay finds no valid
  // resource bound there. Hypothesis: cached.texture/view are raw pointers,
  // and pointer-identity reuse after a texture object is destroyed+recreated
  // at the same address would make this "already correct, skip" branch wrongly
  // treat a NEW, different texture as already-bound, leaving the real Vulkan
  // descriptor stale. Unconditional, unfiltered write-history log (unlocked
  // cap; if this floods, lower it, but the whole point is to see index 28/24's
  // full history across a run, not a sampled slice).
  // NOTE: not filtered by descriptor_index -- the bindless heap allocator
  // assigns indices in first-seen order per run, so index 24/28 (this
  // session's observed values) are not stable across runs. Log every
  // write/skip unconditionally (capped) instead; grep the resulting log by
  // whatever index the next run's draw-state CSV names as the problem slot.
  static uint32_t logged_descriptor_writes = 0;
  const bool cache_hit = cached.texture == texture && cached.view == view &&
                         cached.layout == layout;
  if (logged_descriptor_writes < 8000u) {
    ++logged_descriptor_writes;
    REXLOG_INFO(
        "Refii plume descriptor-write history #{} set={} index={} "
        "cache_hit={} new_texture={} new_view={} prev_texture={} "
        "prev_view={}",
        logged_descriptor_writes, set_index, descriptor_index,
        cache_hit ? 1 : 0, static_cast<const void*>(texture),
        static_cast<const void*>(view),
        static_cast<const void*>(cached.texture),
        static_cast<const void*>(cached.view));
  }
  if (cache_hit) {
    return true;
  }

  if (cached.texture || cached.view ||
      cached.layout != plume::RenderTextureLayout::UNKNOWN) {
    g_plume_resources.texture_descriptor_lookup[set_index].erase(
        PlumeTextureDescriptorKey{cached.texture, cached.view, cached.layout});
  }
  g_plume_resources.texture_descriptor_sets[set_index]->setTexture(
      descriptor_index, texture, layout, view);
  cached.texture = texture;
  cached.view = view;
  cached.layout = layout;
  g_plume_resources.texture_descriptor_lookup[set_index]
      [PlumeTextureDescriptorKey{texture, view, layout}] = descriptor_index;
  return true;
}

uint32_t FindOrSetFableTextureDescriptorLocked(
    uint32_t set_index, plume::RenderTexture* texture,
    plume::RenderTextureLayout layout, plume::RenderTextureView* view,
    uint32_t guest_identity) {
  if (set_index >= std::size(g_plume_resources.texture_descriptor_sets) ||
      !g_plume_resources.texture_descriptor_sets[set_index] || !texture ||
      !view) {
    return 0;
  }

  const uint32_t next_descriptor = std::min<uint32_t>(
      g_plume_resources.next_texture_descriptor_indices[set_index],
      kFableDescriptorCount);
  if (guest_identity != 0) {
    auto& identities =
        g_plume_resources.texture_descriptor_identity_lookup[set_index];
    const auto identity_it = identities.find(guest_identity);
    if (identity_it != identities.end() && identity_it->second != 0 &&
        identity_it->second < kFableDescriptorCount) {
      if (SetFableTextureDescriptorAtIndexLocked(
              set_index, identity_it->second, texture, layout, view)) {
        return identity_it->second;
      }
      return 0;
    }
  }
  const PlumeTextureDescriptorKey key{texture, view, layout};
  const auto descriptor_it =
      g_plume_resources.texture_descriptor_lookup[set_index].find(key);
  if (descriptor_it !=
          g_plume_resources.texture_descriptor_lookup[set_index].end() &&
      descriptor_it->second != 0 &&
      descriptor_it->second < next_descriptor) {
    return descriptor_it->second;
  }

  if (next_descriptor >= kFableDescriptorCount) {
    return 0;
  }

  if (!SetFableTextureDescriptorAtIndexLocked(set_index, next_descriptor,
                                              texture, layout, view)) {
    return 0;
  }
  g_plume_resources.next_texture_descriptor_indices[set_index] =
      next_descriptor + 1u;
  if (guest_identity != 0) {
    g_plume_resources.texture_descriptor_identity_lookup[set_index]
                                                        [guest_identity] =
        next_descriptor;
  }
  return next_descriptor;
}

bool SetFableSamplerDescriptorAtIndexLocked(uint32_t descriptor_index,
                                            plume::RenderSampler* sampler) {
  if (descriptor_index >= kFableSamplerDescriptorCount ||
      !g_plume_resources.sampler_descriptor_set || !sampler) {
    return false;
  }

  plume::RenderSampler*& cached =
      g_plume_resources.cached_sampler_descriptors[descriptor_index];
  if (cached == sampler) {
    return true;
  }

  if (cached) {
    g_plume_resources.sampler_descriptor_lookup.erase(cached);
  }
  g_plume_resources.sampler_descriptor_set->setSampler(descriptor_index,
                                                       sampler);
  cached = sampler;
  g_plume_resources.sampler_descriptor_lookup[sampler] = descriptor_index;
  return true;
}

uint32_t FindOrSetFableSamplerDescriptorLocked(plume::RenderSampler* sampler) {
  if (!g_plume_resources.sampler_descriptor_set || !sampler) {
    return 0;
  }

  const uint32_t next_descriptor =
      std::min<uint32_t>(g_plume_resources.next_sampler_descriptor_index,
                         kFableSamplerDescriptorCount);
  const auto descriptor_it =
      g_plume_resources.sampler_descriptor_lookup.find(sampler);
  if (descriptor_it != g_plume_resources.sampler_descriptor_lookup.end() &&
      descriptor_it->second != 0 &&
      descriptor_it->second < next_descriptor) {
    return descriptor_it->second;
  }

  if (next_descriptor >= kFableSamplerDescriptorCount) {
    return 0;
  }

  if (!SetFableSamplerDescriptorAtIndexLocked(next_descriptor, sampler)) {
    return 0;
  }
  g_plume_resources.next_sampler_descriptor_index = next_descriptor + 1u;
  return next_descriptor;
}
#endif

}  // namespace refii::renderer
