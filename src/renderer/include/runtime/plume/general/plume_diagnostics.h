#pragma once

#include <cstdint>

#if REFII_RENDERER_HAS_PLUME
namespace plume {
struct RenderCommandList;
struct RenderDevice;
enum class RenderShaderFormat;
}  // namespace plume
#endif

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
void RenderPlumeDiagnostics(plume::RenderDevice* device,
                            plume::RenderCommandList* command_list,
                            plume::RenderShaderFormat shader_format,
                            uint32_t frame_index, uint32_t width,
                            uint32_t height);
void ShutdownPlumeDiagnostics();
#else
inline void ShutdownPlumeDiagnostics() {}
#endif

}  // namespace refii::renderer
