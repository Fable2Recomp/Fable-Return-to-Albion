#include "runtime/plume/resource/plume_resource_manager_internal.h"

#include <cstddef>
#include <cstdint>
#include <iterator>

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
namespace {

bool TextureLookupVersionMatches(
    const RendererTrackedResourceVersion& lhs,
    const RendererTrackedResourceVersion& rhs) {
  // Texture lookup keys are based on object identity, base address, fetch
  // shape, and dimensions. Unlocks refresh contents, but they do not by
  // themselves require rebuilding the lookup table for Plume-owned resources.
  return lhs.serial == rhs.serial &&
         lhs.resource_creates == rhs.resource_creates &&
         lhs.resource_destroys == rhs.resource_destroys;
}

void CacheNewestTextureShapeResourceLocked(
    const FableTextureShapeKey& key, const PlumeTrackedResource& resource) {
  auto [it, inserted] =
      g_plume_resources.texture_fetch_shape_index.emplace(key, &resource);
  if (!inserted && (!it->second || resource.serial > it->second->serial)) {
    it->second = &resource;
  }
}

void CacheNewestTextureBaseResourceLocked(uint32_t base,
                                          const PlumeTrackedResource& resource) {
  if (base == 0) {
    return;
  }
  auto [it, inserted] =
      g_plume_resources.texture_base_index.emplace(base, &resource);
  if (!inserted && (!it->second || resource.serial > it->second->serial)) {
    it->second = &resource;
  }
}

void CacheNewestTextureFetchResourceLocked(
    const FableTextureFetchKey& key, const PlumeTrackedResource& resource) {
  auto [it, inserted] =
      g_plume_resources.texture_fetch_exact_index.emplace(key, &resource);
  if (!inserted && (!it->second || resource.serial > it->second->serial)) {
    it->second = &resource;
  }
}

void RebuildFableTextureLookupIndexLocked() {
  g_plume_resources.texture_fetch_exact_index.clear();
  g_plume_resources.texture_fetch_shape_index.clear();
  g_plume_resources.texture_base_index.clear();
  for (const auto& [address, resource] : g_plume_resources.resources) {
    (void)address;
    CacheTextureLookupForResourceLocked(resource);
  }
  for (const auto& [address, resource] :
       g_plume_resources.decoded_color_targets) {
    (void)address;
    CacheTextureLookupForResourceLocked(resource);
  }
  for (const auto& [address, resource] :
       g_plume_resources.decoded_depth_targets) {
    (void)address;
    CacheTextureLookupForResourceLocked(resource);
  }
}

}  // namespace

uint32_t FableFetchTextureWidth(
    const rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch) {
  namespace xenos = rex::graphics::xenos;
  switch (fetch.dimension) {
    case xenos::DataDimension::k1D:
      return fetch.size_1d.width + 1u;
    case xenos::DataDimension::k3D:
      return fetch.size_3d.width + 1u;
    case xenos::DataDimension::k2DOrStacked:
    case xenos::DataDimension::kCube:
      return fetch.size_2d.width + 1u;
    default:
      return 0;
  }
}

uint32_t FableFetchTextureHeight(
    const rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch) {
  namespace xenos = rex::graphics::xenos;
  switch (fetch.dimension) {
    case xenos::DataDimension::k1D:
      return 1;
    case xenos::DataDimension::k3D:
      return fetch.size_3d.height + 1u;
    case xenos::DataDimension::k2DOrStacked:
    case xenos::DataDimension::kCube:
      return fetch.size_2d.height + 1u;
    default:
      return 0;
  }
}

bool FableResolveAliasMatchesFetch(
    const PlumeTrackedResource& resource,
    const rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch) {
  namespace xenos = rex::graphics::xenos;
  if (fetch.dimension != xenos::DataDimension::k2DOrStacked) {
    return false;
  }

  const uint32_t fetch_width = FableFetchTextureWidth(fetch);
  const uint32_t fetch_height = FableFetchTextureHeight(fetch);
  if (fetch_width == 0 || fetch_height == 0 ||
      PlumeGuestTextureWidth(resource) != fetch_width ||
      PlumeGuestTextureHeight(resource) != fetch_height ||
      PlumeGuestTextureDepth(resource) != 1) {
    return false;
  }

  if (resource.texture_depth_target) {
    return true;
  }

  const plume::RenderFormat fetch_format =
      ConvertXenosTextureFormat(fetch.format);
  return fetch_format == plume::RenderFormat::UNKNOWN ||
         PlumeGuestTextureFormat(resource) == fetch_format;
}

void CacheTextureLookupForResourceLocked(const PlumeTrackedResource& resource) {
  if (!IsImageResource(resource.kind) || !PlumeGuestImageReady(resource)) {
    return;
  }

  FableTextureFetchKey exact_key{};
  exact_key.dwords = PlumeGuestTextureFetchDwords(resource);
  const bool has_exact_key =
      (exact_key.dwords[0] | exact_key.dwords[1] | exact_key.dwords[2] |
       exact_key.dwords[3] | exact_key.dwords[4] | exact_key.dwords[5]) != 0;
  if (has_exact_key) {
    CacheNewestTextureFetchResourceLocked(exact_key, resource);
  }

  const FableTextureShapeKey shape_key{
      PlumeGuestTextureBaseAddress(resource),
      PlumeGuestTextureMipAddress(resource),
      PlumeGuestTexturePitch(resource),
      PlumeGuestTextureGuestFormat(resource),
      PlumeGuestTextureDimension(resource)};
  if ((shape_key.base | shape_key.mips | shape_key.pitch | shape_key.format |
       shape_key.dimension) != 0) {
    CacheNewestTextureShapeResourceLocked(shape_key, resource);
  }
  CacheNewestTextureBaseResourceLocked(PlumeGuestTextureBaseAddress(resource),
                                       resource);
}

bool SameTrackedTextureShape(const PlumeTrackedResource& lhs,
                             const PlumeTrackedResource& rhs) {
  if (lhs.texture_dimension != rhs.texture_dimension ||
      lhs.texture_view_dimension != rhs.texture_view_dimension ||
      PlumeGuestTextureFormat(lhs) != PlumeGuestTextureFormat(rhs) ||
      PlumeGuestTextureWidth(lhs) != PlumeGuestTextureWidth(rhs) ||
      PlumeGuestTextureHeight(lhs) != PlumeGuestTextureHeight(rhs) ||
      PlumeGuestTextureDepth(lhs) != PlumeGuestTextureDepth(rhs) ||
      lhs.texture_mip_levels != rhs.texture_mip_levels ||
      lhs.texture_render_target != rhs.texture_render_target ||
      lhs.texture_depth_target != rhs.texture_depth_target) {
    return false;
  }

  return true;
}

void EnsureFableTextureLookupIndexLocked() {
  if (g_plume_resources.texture_lookup_index_valid &&
      g_plume_resources.resource_sync_version_valid &&
      TextureLookupVersionMatches(
          g_plume_resources.texture_lookup_index_version,
          g_plume_resources.last_resource_sync_version)) {
    return;
  }

  RebuildFableTextureLookupIndexLocked();
  g_plume_resources.texture_lookup_index_version =
      g_plume_resources.resource_sync_version_valid
          ? g_plume_resources.last_resource_sync_version
          : RendererTrackedResourceVersion{};
  g_plume_resources.texture_lookup_index_valid =
      g_plume_resources.resource_sync_version_valid;
}

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
  if (cached.texture == texture && cached.view == view &&
      cached.layout == layout) {
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
    plume::RenderTextureLayout layout, plume::RenderTextureView* view) {
  if (set_index >= std::size(g_plume_resources.texture_descriptor_sets) ||
      !g_plume_resources.texture_descriptor_sets[set_index] || !texture ||
      !view) {
    return 0;
  }

  const uint32_t next_descriptor = std::min<uint32_t>(
      g_plume_resources.next_texture_descriptor_indices[set_index],
      kFableDescriptorCount);
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
