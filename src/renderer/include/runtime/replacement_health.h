#pragma once

#include "renderer_runtime.h"
#include "runtime/plume/direct/fable_direct_renderer.h"
#include "runtime/shader_library.h"

#include <cstdint>

namespace refii::renderer {

struct RendererReplacementHealthSnapshot {
  bool plume_available = false;
  bool plume_backend_enabled = false;
  bool replace_resources = false;
  bool replace_state = false;
  bool replace_draws = false;
  bool replace_present = false;
  bool shader_require_precompiled = false;
  RendererHookTimingSnapshot timing;
  RendererShaderLibrarySnapshot shader_library;
  FableDirectRendererStats direct_renderer;
  uint32_t missing_resource_draws = 0;
  uint32_t missing_shader_draws = 0;
  uint32_t present_render_target = 0;
  uint32_t present_render_target_width = 0;
  uint32_t present_render_target_height = 0;
};

RendererReplacementHealthSnapshot GetRendererReplacementHealthSnapshot();
const char* RendererReplacementHealthFailureName(
    const RendererReplacementHealthSnapshot& snapshot);

}  // namespace refii::renderer
