#pragma once

#include "runtime/cvars.h"

#include <memory>

#if !defined(REFII_RENDERER_HAS_PLUME)
#define REFII_RENDERER_HAS_PLUME 0
#endif

#if REFII_RENDERER_HAS_PLUME
namespace plume {
class RenderInterface;
}
#endif

namespace refii::renderer {

const char* PlumeApiBridgeName(RendererGraphicsApi api);
bool PlumeApiBridgeSupported(RendererGraphicsApi api);
void PreparePlumeApiBridge(RendererGraphicsApi api);

#if REFII_RENDERER_HAS_PLUME
std::unique_ptr<plume::RenderInterface> CreatePlumeApiInterface(
    RendererGraphicsApi api);
#endif

}  // namespace refii::renderer
