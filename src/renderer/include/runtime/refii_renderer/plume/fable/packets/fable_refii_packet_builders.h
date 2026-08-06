#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_render_packets.h"

#include <cstdint>

namespace refii::renderer {

RefiiPacketRoot ToRefiiPacketRoot(const FableDirectFrameDatabaseRoot& root);
RefiiPacketRoot ToRefiiPacketRoot(const FableDirectStateRoot& root);

RefiiInlineBufferPayload ToRefiiInlineBufferPayload(
    const FableDirectInlineBuffer& source);

RefiiInlineTexturePayload ToRefiiInlineTexturePayload(
    const RendererInlineTextureUploadSnapshot& source);

RefiiDrawPacket ToRefiiDrawPacket(
    uint64_t frame_id,
    const FableDirectFrameDatabaseDrawPacket& source);

RefiiFrameActionPacket BuildRefiiSurfaceDestinationActionPacket(
    uint64_t frame_id,
    const FableDirectFrameDatabaseCommandPacket& packet);

RefiiFrameActionPacket BuildRefiiPresentBlitActionPacket(
    uint64_t frame_id,
    const FableDirectFrameDatabaseCommandPacket& packet);

}  // namespace refii::renderer
