#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"

#include <cstdint>
#include <string>
#include <string_view>

namespace refii::renderer {

struct FableRenderDocDrawTargetCaptureState {
  std::string requested_capture_target;
  uint32_t logged_titlemenu_matches = 0;
  uint32_t logged_capture_failures = 0;
};

struct FableRenderDocResolveTargetCaptureState {
  std::string requested_capture_target;
};

bool FableRenderDocDrawCaptureTargetReady(
    FableRenderDocDrawTargetCaptureState& state,
    std::string_view target,
    bool matched);

void FableRenderDocMarkDrawCaptureStarted(
    FableRenderDocDrawTargetCaptureState& state,
    std::string_view target);

bool FableRenderDocTakeTitleMenuMatchLogSlot(
    FableRenderDocDrawTargetCaptureState& state,
    std::string_view target,
    uint32_t& slot);

bool FableRenderDocTakeDrawCaptureFailureLogSlot(
    FableRenderDocDrawTargetCaptureState& state,
    uint32_t& slot);

bool FableRenderDocResolveCaptureTargetReady(
    FableRenderDocResolveTargetCaptureState& state,
    std::string_view target,
    const FableDirectRenderCommand& command);

void FableRenderDocMarkResolveCaptureStarted(
    FableRenderDocResolveTargetCaptureState& state,
    std::string_view target);

}  // namespace refii::renderer
