#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_render_packets.h"

#include <vector>

namespace refii::renderer {

FableDirectRenderCommand BuildDirectCommandFromRefiiClearPacket(
    const RefiiClearPacket& packet);

FableDirectRenderCommand BuildDirectCommandFromRefiiResolvePacket(
    const RefiiResolvePacket& packet);

FableDirectRenderCommand BuildDirectCommandFromRefiiDrawPacket(
    const RefiiDrawPacket& packet);

std::vector<FableDirectRenderCommand> BuildDirectReplayCommandsFromRefiiQueue(
    const RefiiFramePacketQueueSnapshot& queue);

}  // namespace refii::renderer
