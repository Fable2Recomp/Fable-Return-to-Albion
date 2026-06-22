#include "runtime/replacement_health.h"

#include "runtime/cvars.h"

namespace refii::renderer {

RendererReplacementHealthSnapshot GetRendererReplacementHealthSnapshot() {
  RendererReplacementHealthSnapshot snapshot;
  snapshot.plume_available = PlumeAvailable();
  snapshot.plume_backend_enabled = PlumeRendererBackendEnabled();
  snapshot.replace_resources = RendererReplaceResourcesEnabled();
  snapshot.replace_state = RendererReplaceStateEnabled();
  snapshot.replace_draws = RendererReplaceDrawsEnabled();
  snapshot.replace_present = RendererReplacePresentEnabled();
  snapshot.shader_require_precompiled = RendererShaderRequirePrecompiled();
  snapshot.timing = GetRendererHookTimingSnapshot();
  snapshot.shader_library = GetRendererShaderLibrarySnapshot();
  snapshot.direct_renderer = GetFableDirectRendererStats();
  snapshot.present_render_target =
      snapshot.direct_renderer.last_present_target;
  snapshot.present_render_target_width =
      snapshot.direct_renderer.last_present_width;
  snapshot.present_render_target_height =
      snapshot.direct_renderer.last_present_height;
  return snapshot;
}

const char* RendererReplacementHealthFailureName(
    const RendererReplacementHealthSnapshot& snapshot) {
  return snapshot.direct_renderer.failed_hooks == 0 ? "none"
                                                   : "direct_hook_failed";
}

}  // namespace refii::renderer
