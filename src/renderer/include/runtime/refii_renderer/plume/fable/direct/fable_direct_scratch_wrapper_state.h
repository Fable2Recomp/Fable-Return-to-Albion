#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"

#include <array>
#include <cstdint>
#include <vector>

namespace refii::renderer {

struct DirectScratchWrapperShaderBinding {
  uint32_t vertex_declaration = 0;
  uint32_t vertex_shader = 0;
  uint32_t pixel_shader = 0;
  FableDirectShaderRoots shader_roots;
  std::array<uint32_t, 4> render_targets = {};
  uint32_t depth_stencil = 0;
  uint32_t frame_index = 0;
};

void NormalizeDirectInheritedScratchWrapperShaders(
    std::vector<FableDirectRenderCommand>& commands,
    const FableDirectStateSnapshot& staged_snapshot,
    DirectScratchWrapperShaderBinding& recent_binding,
    uint32_t current_frame_index);

}  // namespace refii::renderer
