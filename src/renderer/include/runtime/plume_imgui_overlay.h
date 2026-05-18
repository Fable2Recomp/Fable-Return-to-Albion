#pragma once

#include <cstdint>

#if !defined(REFII_RENDERER_HAS_PLUME)
#define REFII_RENDERER_HAS_PLUME 0
#endif

namespace plume {
struct RenderCommandFence;
struct RenderCommandList;
struct RenderCommandQueue;
struct RenderDevice;
enum class RenderShaderFormat;
}  // namespace plume

namespace rex::ui {
class ImGuiDrawer;
class Window;
}  // namespace rex::ui

namespace refii::renderer {

void AttachPlumeImGuiToReXGlue(rex::ui::Window* window,
                               rex::ui::ImGuiDrawer* drawer);
void DetachPlumeImGuiFromReXGlue();

void DrawPlumeImGuiOverlay(uint32_t width, uint32_t height,
                           uint32_t present_count);

#if REFII_RENDERER_HAS_PLUME
bool EnsurePlumeImGuiRenderer(plume::RenderDevice* device,
                              plume::RenderShaderFormat shader_format);
void RecordPlumeImGuiUploads(plume::RenderCommandList* command_list);
void RenderPlumeImGuiOverlay(plume::RenderDevice* device,
                             plume::RenderCommandList* command_list);
#endif

void ShutdownPlumeImGuiOverlay();

}  // namespace refii::renderer
