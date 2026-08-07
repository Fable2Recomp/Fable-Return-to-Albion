#include "runtime/plume/general/plume_api_bridge_private.h"

#include "renderdoc_debugger.h"

#include <rex/logging/macros.h>

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
namespace {

// Plume owns a debug-message sink so it needs no knowledge of our logger; this
// is the host end of it. Before this existed, Vulkan validation output and the
// VK_ERROR_DEVICE_LOST notice both went to the loader's stderr default and
// never reached refii_NNN.log — which is why more than one investigation
// recorded "validation produced no output" while the layer was in fact loaded
// and talking. The device loss is silent after the first notice (every queue
// operation simply returns false), so without this the log just stops.
void ForwardPlumeVulkanDebugMessage(const char* message, bool is_error) {
  if (message == nullptr) {
    return;
  }
  if (is_error) {
    REXLOG_ERROR("Refii vulkan {}", message);
  } else {
    REXLOG_WARN("Refii vulkan {}", message);
  }
}

}  // namespace

std::unique_ptr<plume::RenderInterface> CreatePlumeVulkanInterface() {
  // Install before construction: the messenger is created inside the
  // VulkanInterface constructor, and the layer can report during instance
  // creation itself.
  plume::SetRenderVulkanDebugMessageCallback(&ForwardPlumeVulkanDebugMessage);
  std::unique_ptr<plume::VulkanInterface> render_interface =
      std::make_unique<plume::VulkanInterface>();
  return render_interface->isValid() ? std::move(render_interface) : nullptr;
}
#endif

}  // namespace refii::renderer
