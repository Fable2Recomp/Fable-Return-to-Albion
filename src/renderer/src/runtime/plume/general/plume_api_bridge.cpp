#include "runtime/plume/general/plume_api_bridge.h"

#include "runtime/plume/general/plume_api_bridge_private.h"

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
    case RendererGraphicsApi::kMetal:
      return "metal";
  }

  return "unknown";
}

bool PlumeApiBridgeSupported(RendererGraphicsApi api) {
  switch (api) {
    case RendererGraphicsApi::kVulkan:
      return PlumeVulkanBridgeSupported();
    case RendererGraphicsApi::kD3D12:
      return PlumeD3D12BridgeSupported();
    case RendererGraphicsApi::kMetal:
      return PlumeMetalBridgeSupported();
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
    case RendererGraphicsApi::kMetal:
      PreparePlumeMetalBridge();
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
    case RendererGraphicsApi::kMetal:
#if defined(__APPLE__)
      return CreatePlumeMetalInterface();
#else
      return nullptr;
#endif
  }

  return nullptr;
}
#endif

}  // namespace refii::renderer
