#pragma once

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

bool PlumeVulkanBridgeSupported();
void PreparePlumeVulkanBridge();

bool PlumeD3D12BridgeSupported();
void PreparePlumeD3D12Bridge();

#if REFII_RENDERER_HAS_PLUME
std::unique_ptr<plume::RenderInterface> CreatePlumeVulkanInterface();
std::unique_ptr<plume::RenderInterface> CreatePlumeD3D12Interface();
#endif

}  // namespace refii::renderer
