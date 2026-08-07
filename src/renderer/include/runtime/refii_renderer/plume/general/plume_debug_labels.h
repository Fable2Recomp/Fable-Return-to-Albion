#pragma once

#include <cstdint>
#include <string>

#if REFII_RENDERER_HAS_PLUME
#include <plume_render_interface.h>
#endif

namespace refii::renderer {

std::string BuildRenderGraphExecuteLabel(uint32_t frame_index,
                                         uint32_t width,
                                         uint32_t height);
std::string BuildRenderGraphBridgeLabel(uint32_t frame_index);
std::string BuildRenderGraphSchedulerLabel(uint32_t frame_index);
std::string BuildRenderGraphSchedulerBridgeStepLabel(uint32_t frame_index);
std::string BuildRenderGraphSchedulerPlanStepLabel(uint32_t frame_index);

#if REFII_RENDERER_HAS_PLUME
class PlumeDebugLabelScope {
 public:
  PlumeDebugLabelScope(plume::RenderCommandList* command_list,
                       const std::string& label)
      : command_list_(command_list), active_(command_list != nullptr) {
    if (active_) {
      command_list_->beginDebugLabel(label);
    }
  }

  ~PlumeDebugLabelScope() {
    End();
  }

  void End() {
    if (!active_) {
      return;
    }
    command_list_->endDebugLabel();
    active_ = false;
  }

 private:
  plume::RenderCommandList* command_list_ = nullptr;
  bool active_ = false;
};
#endif

}  // namespace refii::renderer
