#include "runtime/plume/general/plume_api_bridge_private.h"

#if REFII_RENDERER_HAS_PLUME && defined(__APPLE__)
#include <plume_metal.h>
#endif

namespace refii::renderer {

bool PlumeMetalBridgeSupported() {
#if REFII_RENDERER_HAS_PLUME && defined(__APPLE__)
  return true;
#else
  return false;
#endif
}

void PreparePlumeMetalBridge() {}

#if REFII_RENDERER_HAS_PLUME
std::unique_ptr<plume::RenderInterface> CreatePlumeMetalInterface() {
#if defined(__APPLE__)
  return plume::CreateMetalInterface();
#else
  return nullptr;
#endif
}
#endif

}  // namespace refii::renderer
