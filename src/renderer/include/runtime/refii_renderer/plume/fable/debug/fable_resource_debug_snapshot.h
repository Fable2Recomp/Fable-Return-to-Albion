#pragma once

#include "renderer_runtime.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer.h"

#if REFII_RENDERER_HAS_PLUME
#include <vector>
#endif

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
struct PlumeTrackedResource;

FableDirectResourceDebugSnapshot BuildFableDirectResourceDebugSnapshot(
    std::vector<const PlumeTrackedResource*> resources);
#endif

}  // namespace refii::renderer
