#pragma once

#include "runtime/refii_render_packets.h"

#include <vector>

namespace refii::renderer {

bool RefiiPacketHasAnyPixVideoPlane(const RefiiDrawPacket& packet);
bool RefiiPacketHasPixVideoPlanes(const RefiiDrawPacket& packet);
bool RefiiPacketPixVideoReplaySupported(const RefiiDrawPacket& packet);
bool RefiiPacketDrawVerticesUpReplaySupported(const RefiiDrawPacket& packet);

RefiiPacketReplaySummary BuildRefiiPacketReplaySummary(
    const std::vector<RefiiDrawPacket>& packets,
    const std::vector<RefiiFrameActionPacket>& actions);

}  // namespace refii::renderer
