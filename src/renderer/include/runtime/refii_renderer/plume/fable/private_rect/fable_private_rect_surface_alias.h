#pragma once

#include "runtime/refii_renderer/plume/fable/packets/fable_render_types.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h"

#include <cstdint>

namespace refii::renderer {

struct FablePrivateRectSurfaceAliasCallbacks {
  bool (*is_likely_fable_object_address)(uint32_t guest_address) = nullptr;
  uint64_t (*next_serial)() = nullptr;
  void (*store_surface_copy_alias)(const RendererSurfaceCopyAliasSnapshot& alias) =
      nullptr;
  void (*track_surface_copy_alias)(const RendererSurfaceCopyAliasSnapshot& alias) =
      nullptr;
  void (*claim_blank_probe)(const FablePrivateTextureCopyAction& action,
                            const char* reason) = nullptr;
  uint32_t (*reserve_log_index)() = nullptr;
};

void FablePrivateTextureRectanglePublishExactSurfaceAlias(
    const FablePrivateTextureCopyAction& action,
    const char* reason,
    const FablePrivateRectSurfaceAliasCallbacks& callbacks);

}  // namespace refii::renderer
