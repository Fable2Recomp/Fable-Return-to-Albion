#include "runtime/plume/resource/plume_resource_manager_internal.h"

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
void InvalidateFableDescriptorCacheLocked() {
  for (auto& descriptor_set_cache :
       g_plume_resources.cached_texture_descriptors) {
    for (PlumeCachedTextureDescriptor& descriptor : descriptor_set_cache) {
      descriptor = {};
    }
  }
  for (auto& lookup : g_plume_resources.texture_descriptor_lookup) {
    lookup.clear();
  }
  ++g_plume_resources.texture_descriptor_epoch;
  g_plume_resources.draw_texture_descriptor_cache.clear();
  g_plume_resources.draw_texture_binding_cache.clear();
  g_plume_resources.cached_sampler_descriptors.fill(nullptr);
  g_plume_resources.sampler_descriptor_lookup.clear();
}

void BeginFableFrameLookupCacheLocked() {
  g_plume_resources.texture_fetch_frame_misses.clear();
  g_plume_resources.render_target_resolve_aliases.clear();

  if (g_plume_resources.resource_lookup_cache.size() > 16384u) {
    g_plume_resources.resource_lookup_cache.clear();
  }
  if (g_plume_resources.texture_fetch_lookup_cache.size() > 8192u) {
    g_plume_resources.texture_fetch_lookup_cache.clear();
  }
  if (g_plume_resources.texture_fetch_serial_lookup_cache.size() > 32768u) {
    g_plume_resources.texture_fetch_serial_lookup_cache.clear();
  }
  if (g_plume_resources.texture_pointer_lookup_cache.size() > 4096u) {
    g_plume_resources.texture_pointer_lookup_cache.clear();
  }
  if (g_plume_resources.texture_alias_lookup_cache.size() > 8192u) {
    g_plume_resources.texture_alias_lookup_cache.clear();
  }
  if (g_plume_resources.live_render_target_alias_cache.size() > 8192u) {
    g_plume_resources.live_render_target_alias_cache.clear();
  }
  if (g_plume_resources.sampled_object_lookup_cache.size() > 8192u) {
    g_plume_resources.sampled_object_lookup_cache.clear();
  }
  if (g_plume_resources.sampled_texture_lookup_cache.size() > 32768u) {
    g_plume_resources.sampled_texture_lookup_cache.clear();
  }
  if (g_plume_resources.draw_texture_descriptor_cache.size() > 32768u) {
    g_plume_resources.draw_texture_descriptor_cache.clear();
  }
  if (g_plume_resources.draw_texture_binding_cache.size() > 32768u) {
    g_plume_resources.draw_texture_binding_cache.clear();
  }
  if (g_plume_resources.surface_alias_copy_cache.size() > 8192u) {
    g_plume_resources.surface_alias_copy_cache.clear();
  }
}

void ClearFableFrameLookupCacheLocked() {
  g_plume_resources.resource_lookup_cache.clear();
  g_plume_resources.texture_fetch_lookup_cache.clear();
  g_plume_resources.texture_fetch_serial_lookup_cache.clear();
  g_plume_resources.texture_fetch_frame_misses.clear();
  g_plume_resources.render_target_resolve_aliases.clear();
  g_plume_resources.texture_pointer_lookup_cache.clear();
  g_plume_resources.texture_alias_lookup_cache.clear();
  g_plume_resources.live_render_target_alias_cache.clear();
  g_plume_resources.sampled_object_lookup_cache.clear();
  g_plume_resources.sampled_texture_lookup_cache.clear();
  g_plume_resources.draw_texture_descriptor_cache.clear();
  g_plume_resources.draw_texture_binding_cache.clear();
}

void ClearFableTextureLookupIndexLocked() {
  g_plume_resources.texture_fetch_exact_index.clear();
  g_plume_resources.texture_fetch_shape_index.clear();
  g_plume_resources.texture_base_index.clear();
  g_plume_resources.texture_lookup_index_version = {};
  g_plume_resources.texture_lookup_index_valid = false;
}
#endif

}  // namespace refii::renderer
