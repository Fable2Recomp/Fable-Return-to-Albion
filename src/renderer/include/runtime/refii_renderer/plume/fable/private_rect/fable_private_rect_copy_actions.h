#pragma once

#include "renderer_runtime.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_render_types.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"

#include <array>
#include <cstdint>

namespace refii::renderer {

struct FablePrivateRectCopyActionCallbacks {
  bool (*is_likely_fable_object_address)(uint32_t guest_address) = nullptr;
  uint32_t (*current_frame_index)() = nullptr;
  void (*publish_source_identity)(uint8_t* base,
                                  uint32_t source,
                                  uint32_t target,
                                  const TextureFetchSnapshotInfo& fetch_info,
                                  const std::array<uint32_t, 6>& fetch_dwords,
                                  uint32_t source_slot,
                                  uint32_t lr) = nullptr;
  void (*upsert_texture_from_fetch_dwords)(
      const std::array<uint32_t, 6>& fetch_dwords,
      uint8_t* base) = nullptr;
  void (*publish_target_surface)(uint32_t target,
                                 bool depth_target,
                                 const RendererDrawEventSnapshot& event) =
      nullptr;
  void (*attach_wrapper_action)(FablePrivateTextureCopyAction& action) =
      nullptr;
  void (*register_copy_action)(const FablePrivateTextureCopyAction& action) =
      nullptr;
  void (*publish_exact_surface_alias)(
      const FablePrivateTextureCopyAction& action,
      const char* reason) = nullptr;
  uint32_t (*reserve_transient_log_index)() = nullptr;
};

void FablePrivateTextureRectangleRegisterCopyAction(
    uint8_t* base,
    uint32_t source,
    uint32_t target,
    uint32_t render_target,
    uint32_t depth_stencil,
    uint32_t vertex_shader,
    uint32_t pixel_shader,
    uint32_t lr,
    const TextureFetchSnapshotInfo& source_fetch_info,
    const std::array<uint32_t, 6>& source_fetch,
    FablePrivateTextureCopyKind copy_kind,
    const RendererDrawEventSnapshot& event,
    uint32_t source_slot,
    const FablePrivateRectCopyActionCallbacks& callbacks);

}  // namespace refii::renderer
