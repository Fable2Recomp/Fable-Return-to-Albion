#include "runtime/plume_api_bridge_private.h"

#if REFII_RENDERER_HAS_PLUME && defined(_WIN64)
#include <plume_d3d12.h>
#endif

namespace refii::renderer {

bool PlumeD3D12BridgeSupported() {
#if REFII_RENDERER_HAS_PLUME && defined(_WIN64)
  return true;
#else
  return false;
#endif
}

void PreparePlumeD3D12Bridge() {}

#if REFII_RENDERER_HAS_PLUME
std::unique_ptr<plume::RenderInterface> CreatePlumeD3D12Interface() {
#if defined(_WIN64)
  std::unique_ptr<plume::D3D12Interface> render_interface =
      std::make_unique<plume::D3D12Interface>();
  return render_interface->isValid() ? std::move(render_interface) : nullptr;
#else
  return nullptr;
#endif
}
#endif

}  // namespace refii::renderer
