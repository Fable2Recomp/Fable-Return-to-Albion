#pragma once

#include "runtime/refii_render_packets.h"

namespace refii::renderer {

bool RefiiFrameActionRecordsOutOfBandBackendWork(
    const RefiiFrameActionPacket& action);
bool RefiiPacketQueueHasOutOfBandBackendReplayWork(
    const RefiiFramePacketQueueSnapshot& queue);

}  // namespace refii::renderer
