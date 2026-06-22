#include "runtime/plume/resource/plume_resource_manager_internal.h"

#include <cstdint>
#include <utility>

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
void TrimRetiredFableFramebuffersLocked() {
  while (!g_plume_resources.retired_framebuffers.empty()) {
    const PlumeRetiredFramebuffer& retired =
        g_plume_resources.retired_framebuffers.front();
    const bool old_enough =
        retired.retire_epoch + kRetiredPlumeResourceEpochs <=
        g_plume_resources.resource_retire_epoch;
    const bool over_budget =
        g_plume_resources.retired_framebuffers.size() >
        kMaxFableFramebufferCacheEntries * 2u;
    if (!old_enough && !over_budget) {
      break;
    }
    g_plume_resources.retired_framebuffers.pop_front();
  }
}

void RetireFableFramebuffersLocked() {
  if (g_plume_resources.framebuffers.empty()) {
    return;
  }
  for (auto& [key, framebuffer] : g_plume_resources.framebuffers) {
    (void)key;
    if (framebuffer) {
      g_plume_resources.retired_framebuffers.push_back(
          PlumeRetiredFramebuffer{g_plume_resources.resource_retire_epoch,
                                  std::move(framebuffer)});
    }
  }
  g_plume_resources.framebuffers.clear();
  TrimRetiredFableFramebuffersLocked();
}

void TrimRetiredPlumeResourcesLocked() {
  TrimRetiredFableFramebuffersLocked();
  while (!g_plume_resources.retired_resources.empty()) {
    const PlumeRetiredResource& retired =
        g_plume_resources.retired_resources.front();
    const uint64_t retire_epochs =
        IsBufferResource(retired.resource.kind)
            ? kRetiredPlumeBufferResourceEpochs
            : kRetiredPlumeResourceEpochs;
    const bool old_enough =
        retired.retire_epoch + retire_epochs <=
        g_plume_resources.resource_retire_epoch;
    const bool over_budget =
        g_plume_resources.retired_resources.size() > kMaxRetiredPlumeResources;
    if (!old_enough && !over_budget) {
      break;
    }
    g_plume_resources.retired_resources.pop_front();
  }
}

void RetirePlumeResourceLocked(PlumeTrackedResource&& resource,
                               uint64_t valid_until_serial) {
  if (!resource.guest_buffer.buffer && !resource.guest_shader.shader &&
      !resource.guest_texture.texture && !resource.guest_texture.view &&
      !resource.guest_surface.attachment_view) {
    return;
  }
  if (resource.guest_texture.texture || resource.guest_texture.view ||
      resource.guest_surface.attachment_view) {
    RetireFableFramebuffersLocked();
  }
  g_plume_resources.retired_resources.push_back(
      PlumeRetiredResource{g_plume_resources.resource_retire_epoch,
                           valid_until_serial, std::move(resource)});
  TrimRetiredPlumeResourcesLocked();
}
#endif

}  // namespace refii::renderer
