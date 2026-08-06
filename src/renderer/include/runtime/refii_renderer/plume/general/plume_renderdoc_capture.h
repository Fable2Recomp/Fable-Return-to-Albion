#pragma once

#include <cstdint>
#include <string>
#include <string_view>

namespace refii::renderer {

struct PlumeRenderDocAutoCaptureArm {
  bool armed = false;
  std::string draw_target;
  std::string reason;
};

PlumeRenderDocAutoCaptureArm MaybeArmPlumeRenderDocAutoContentCapture(
    uint32_t frame_index,
    bool ready_to_capture,
    uint32_t requested_frame,
    uint32_t requested_draw,
    std::string_view requested_draw_target,
    std::string_view requested_pass);

bool IsRenderDocSurfaceDestinationCaptureTarget(std::string_view target);
bool IsRenderDocResolveCaptureTarget(std::string_view target);
std::string RenderDocDeepObserverCaptureDrawTarget();

}  // namespace refii::renderer
