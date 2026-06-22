#include "runtime/plume/general/plume_api_bridge_private.h"

#include "renderdoc_debugger.h"

#if REFII_RENDERER_HAS_PLUME
#include <plume_vulkan.h>
#endif

namespace refii::renderer {

bool PlumeVulkanBridgeSupported() {
#if REFII_RENDERER_HAS_PLUME
  return true;
#else
  return false;
#endif
}

void PreparePlumeVulkanBridge() {
  PrepareRenderDocVulkanLayer();
}

#if REFII_RENDERER_HAS_PLUME
std::unique_ptr<plume::RenderInterface> CreatePlumeVulkanInterface() {
  std::unique_ptr<plume::VulkanInterface> render_interface =
      std::make_unique<plume::VulkanInterface>();
  return render_interface->isValid() ? std::move(render_interface) : nullptr;
}
#endif

}  // namespace refii::renderer
