#include "runtime/refii_renderer/plume/general/plume_renderdoc_capture.h"

#include "renderdoc_debugger.h"
#include "runtime/cvars.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer.h"

#include <mutex>

#include <rex/logging.h>

namespace refii::renderer {
namespace {

constexpr size_t kRenderDocAutoCaptureCommandProbe = 4096;

struct PlumeRenderDocAutoCaptureState {
  bool video_armed = false;
  bool title_armed = false;
};

std::mutex g_auto_capture_mutex;
PlumeRenderDocAutoCaptureState g_auto_capture;

bool ExplicitRenderDocCaptureRequested(uint32_t requested_frame,
                                       uint32_t requested_draw,
                                       std::string_view requested_draw_target,
                                       std::string_view requested_pass) {
  return requested_frame != 0 || requested_draw != 0 ||
         !requested_draw_target.empty() || !requested_pass.empty();
}

bool TryArmAutoCapture(std::string_view target,
                       std::string_view reason,
                       bool& armed_flag,
                       uint32_t frame_index,
                       PlumeRenderDocAutoCaptureArm& result) {
  if (armed_flag ||
      !FableDirectQueuedRenderDocTargetPresent(
          target, kRenderDocAutoCaptureCommandProbe)) {
    return false;
  }
  if (!RequestRenderDocFrameCaptureWithReason(std::string(reason).c_str())) {
    return false;
  }

  armed_flag = true;
  result.armed = true;
  result.draw_target = std::string(target);
  result.reason = std::string(reason);
  REXLOG_INFO(
      "Refii plume RenderDoc auto content capture armed target={} "
      "reason={} frame={}",
      result.draw_target, result.reason, frame_index);
  return true;
}

}  // namespace

PlumeRenderDocAutoCaptureArm MaybeArmPlumeRenderDocAutoContentCapture(
    uint32_t frame_index,
    bool ready_to_capture,
    uint32_t requested_frame,
    uint32_t requested_draw,
    std::string_view requested_draw_target,
    std::string_view requested_pass) {
  PlumeRenderDocAutoCaptureArm result;
  if (!ready_to_capture || !RendererDeepObserverHooksEnabled() ||
      !RenderDocDebuggerEnabled() ||
      !RenderDocDebuggerAvailable() ||
      RenderDocCaptureRequestPending() ||
      RenderDocTriggerCaptureRequestPending() ||
      ExplicitRenderDocCaptureRequested(requested_frame, requested_draw,
                                        requested_draw_target,
                                        requested_pass)) {
    return result;
  }

  std::lock_guard lock(g_auto_capture_mutex);
  if (TryArmAutoCapture("binkvideo", "auto_video", g_auto_capture.video_armed,
                        frame_index, result)) {
    return result;
  }
  TryArmAutoCapture("titlemenu", "auto_title", g_auto_capture.title_armed,
                    frame_index, result);
  return result;
}

bool IsRenderDocSurfaceDestinationCaptureTarget(std::string_view target) {
  return target == "surfacedestination" || target == "surface_destination" ||
         target == "surface-destination" || target == "surfacecopy" ||
         target == "surface-copy" || target == "surface_copy";
}

bool IsRenderDocResolveCaptureTarget(std::string_view target) {
  return target == "resolvecopy" || target == "pm4resolve" ||
         target == "resolve";
}

std::string RenderDocDeepObserverCaptureDrawTarget() {
  // Explicit draw-target captures work from normal-mode facts (shader hashes
  // come from the shader library, sources/RTs from the command). Requiring
  // deep-observer mode made targeted runs too slow to reach the title phase;
  // deep observer remains required only for the automatic content captures
  // (MaybeArmPlumeRenderDocAutoContentCapture).
  return RenderDocCaptureDrawTarget();
}

}  // namespace refii::renderer
