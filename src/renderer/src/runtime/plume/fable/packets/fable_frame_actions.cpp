#include "runtime/refii_renderer/plume/fable/packets/fable_frame_actions.h"

#include <algorithm>

namespace refii::renderer {


bool RefiiFrameActionRecordsOutOfBandBackendWork(
    const RefiiFrameActionPacket& action) {
  switch (action.type) {
    case RefiiFrameActionType::kSurfaceDestination:
    case RefiiFrameActionType::kPresentBlit:
      return true;
    case RefiiFrameActionType::kDraw:
    case RefiiFrameActionType::kClear:
    case RefiiFrameActionType::kResolve:
    case RefiiFrameActionType::kUnknown:
    default:
      return false;
  }
}

bool RefiiPacketQueueHasOutOfBandBackendReplayWork(
    const RefiiFramePacketQueueSnapshot& queue) {
  return std::any_of(queue.actions.begin(), queue.actions.end(),
                     RefiiFrameActionRecordsOutOfBandBackendWork);
}

}  // namespace refii::renderer
