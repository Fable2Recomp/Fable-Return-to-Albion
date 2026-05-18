#include "runtime/plume_api_bridge.h"

#include "runtime/plume_api_bridge_private.h"

#if REFII_RENDERER_HAS_PLUME
#include <plume_render_interface.h>
#endif

namespace refii::renderer {

const char* PlumeApiBridgeName(RendererGraphicsApi api) {
  switch (api) {
    case RendererGraphicsApi::kVulkan:
      return "vulkan";
    case RendererGraphicsApi::kD3D12:
      return "d3d12";
  }

  return "unknown";
}

bool PlumeApiBridgeSupported(RendererGraphicsApi api) {
  switch (api) {
    case RendererGraphicsApi::kVulkan:
      return PlumeVulkanBridgeSupported();
    case RendererGraphicsApi::kD3D12:
      return PlumeD3D12BridgeSupported();
  }

  return false;
}

void PreparePlumeApiBridge(RendererGraphicsApi api) {
  switch (api) {
    case RendererGraphicsApi::kVulkan:
      PreparePlumeVulkanBridge();
      return;
    case RendererGraphicsApi::kD3D12:
      PreparePlumeD3D12Bridge();
      return;
  }
}

#if REFII_RENDERER_HAS_PLUME
std::unique_ptr<plume::RenderInterface> CreatePlumeApiInterface(
    RendererGraphicsApi api) {
  switch (api) {
    case RendererGraphicsApi::kVulkan:
      return CreatePlumeVulkanInterface();
    case RendererGraphicsApi::kD3D12:
      return CreatePlumeD3D12Interface();
  }

  return nullptr;
}
#endif

}  // namespace refii::renderer
