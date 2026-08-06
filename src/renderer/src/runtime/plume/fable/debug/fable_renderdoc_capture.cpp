#include "runtime/refii_renderer/plume/fable/debug/fable_renderdoc_capture.h"

#include "runtime/refii_renderer/plume/fable/debug/fable_renderdoc_draw_targets.h"
#include "runtime/refii_renderer/plume/general/plume_renderdoc_capture.h"

namespace refii::renderer {

bool FableRenderDocDrawCaptureTargetReady(
    FableRenderDocDrawTargetCaptureState& state,
    std::string_view target,
    bool matched) {
  if (target.empty()) {
    state.requested_capture_target.clear();
    return false;
  }
  return state.requested_capture_target != target && matched;
}

void FableRenderDocMarkDrawCaptureStarted(
    FableRenderDocDrawTargetCaptureState& state,
    std::string_view target) {
  state.requested_capture_target = std::string(target);
}

bool FableRenderDocTakeTitleMenuMatchLogSlot(
    FableRenderDocDrawTargetCaptureState& state,
    std::string_view target,
    uint32_t& slot) {
  if (!FableRenderDocTargetIsTitleMenu(target) ||
      state.logged_titlemenu_matches >= 16u) {
    return false;
  }
  slot = ++state.logged_titlemenu_matches;
  return true;
}

bool FableRenderDocTakeDrawCaptureFailureLogSlot(
    FableRenderDocDrawTargetCaptureState& state,
    uint32_t& slot) {
  if (state.logged_capture_failures >= 16u) {
    return false;
  }
  slot = ++state.logged_capture_failures;
  return true;
}

bool FableRenderDocResolveCaptureTargetReady(
    FableRenderDocResolveTargetCaptureState& state,
    std::string_view target,
    const FableDirectRenderCommand& command) {
  if (target.empty()) {
    state.requested_capture_target.clear();
    return false;
  }
  if (!IsRenderDocResolveCaptureTarget(target) ||
      state.requested_capture_target == target) {
    return false;
  }
  return FableRenderDocResolveTargetMatches(target, command);
}

void FableRenderDocMarkResolveCaptureStarted(
    FableRenderDocResolveTargetCaptureState& state,
    std::string_view target) {
  state.requested_capture_target = std::string(target);
}

}  // namespace refii::renderer
