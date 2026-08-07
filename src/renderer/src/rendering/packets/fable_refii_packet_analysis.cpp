#include "runtime/refii_renderer/plume/fable/packets/fable_refii_packet_analysis.h"

#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_synthetic_private_rect.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_render_types.h"

#include <algorithm>
#include <cstdint>

namespace refii::renderer {

bool RefiiPacketHasAnyPixVideoPlane(const RefiiDrawPacket& packet) {
  return std::any_of(packet.pix_video_planes.begin(),
                     packet.pix_video_planes.end(),
                     [](const RefiiInlineTexturePayload& payload) {
                       return payload.valid();
                     });
}

bool RefiiPacketHasPixVideoPlanes(const RefiiDrawPacket& packet) {
  return RefiiPacketHasAnyPixVideoPlane(packet);
}

bool RefiiPacketPixVideoReplaySupported(const RefiiDrawPacket& packet) {
  return RefiiPacketHasAnyPixVideoPlane(packet);
}

bool RefiiPacketHasAnyTarget(const RefiiDrawPacket& packet) {
  if (packet.depth_target != 0) {
    return true;
  }
  return std::any_of(packet.render_targets.begin(),
                     packet.render_targets.end(),
                     [](RefiiRenderTargetId target) {
                       return target != 0;
                     });
}

bool RefiiPacketUsesSyntheticPrivateRectInput(const RefiiDrawPacket& packet) {
  const auto source = static_cast<RendererDrawEventSource>(packet.source);
  if (!packet.private_texture.enabled ||
      packet.vertex_shader != kFableSyntheticPrivateRectVertexShader) {
    return false;
  }
  if (source == RendererDrawEventSource::kPrivateTextureRectangle) {
    return true;
  }
  return source == RendererDrawEventSource::kPixVideo &&
         packet.private_texture.corners_valid &&
         RefiiPacketHasAnyPixVideoPlane(packet);
}

bool RefiiPacketHasInlineVertexPayload(const RefiiDrawPacket& packet) {
  return std::any_of(packet.inline_vertex_buffers.begin(),
                     packet.inline_vertex_buffers.end(),
                     [](const RefiiInlineBufferPayload& payload) {
                       return payload.valid();
                     });
}

bool RefiiPacketHasVertexInput(const RefiiDrawPacket& packet) {
  if (RefiiPacketUsesSyntheticPrivateRectInput(packet)) {
    return true;
  }
  if (RefiiPacketHasInlineVertexPayload(packet)) {
    return true;
  }
  return packet.vertex_declaration != 0 &&
         std::any_of(packet.vertex_buffers.begin(),
                     packet.vertex_buffers.end(),
                     [](RefiiBufferId buffer) { return buffer != 0; });
}

bool RefiiPacketDrawVerticesUpReplaySupported(const RefiiDrawPacket& packet) {
  return !packet.indexed && RefiiPacketHasInlineVertexPayload(packet);
}

bool RefiiPacketHasSupportedReplaySource(const RefiiDrawPacket& packet) {
  const auto source = static_cast<RendererDrawEventSource>(packet.source);
  switch (source) {
    case RendererDrawEventSource::kPixVideo:
      return RefiiPacketPixVideoReplaySupported(packet);
    case RendererDrawEventSource::kDrawVerticesUp:
      return RefiiPacketDrawVerticesUpReplaySupported(packet);
    case RendererDrawEventSource::kD3DWrapper:
    case RendererDrawEventSource::kScratchWrapper:
    case RendererDrawEventSource::kPrivateTextureRectangle:
    case RendererDrawEventSource::kPm4Draw:
    case RendererDrawEventSource::kSceneObject:
      return true;
    case RendererDrawEventSource::kUnknown:
    case RendererDrawEventSource::kPm4Resolve:
    case RendererDrawEventSource::kClear:
    default:
      return false;
  }
}

RefiiPacketReplaySummary BuildRefiiPacketReplaySummary(
    const std::vector<RefiiDrawPacket>& packets,
    const std::vector<RefiiFrameActionPacket>& actions) {
  RefiiPacketReplaySummary summary;
  summary.queue_depth = static_cast<uint32_t>(
      std::min<size_t>(packets.size(), UINT32_MAX));
  summary.action_queue_depth = static_cast<uint32_t>(
      std::min<size_t>(actions.size(), UINT32_MAX));
  for (const RefiiDrawPacket& packet : packets) {
    bool supported = true;
    if (packet.indexed) {
      ++summary.indexed_packets;
    }
    if (packet.vertex_shader == 0 || packet.pixel_shader == 0) {
      ++summary.missing_shader_packets;
      supported = false;
    }
    if (!RefiiPacketHasAnyTarget(packet)) {
      ++summary.missing_target_packets;
      supported = false;
    }
    if (!RefiiPacketHasVertexInput(packet)) {
      ++summary.missing_vertex_input_packets;
      supported = false;
    }
    if (packet.indexed && packet.index_buffer == 0 &&
        !packet.inline_index_buffer.valid()) {
      ++summary.missing_index_input_packets;
      supported = false;
    }
    if (packet.index_count == 0 || packet.primitive_count == 0) {
      ++summary.empty_draw_packets;
      supported = false;
    }
    if (static_cast<RendererDrawEventSource>(packet.source) ==
            RendererDrawEventSource::kPrivateTextureRectangle &&
        !packet.private_texture.enabled) {
      ++summary.missing_private_texture_payload_packets;
      supported = false;
    }
    if (!RefiiPacketHasSupportedReplaySource(packet)) {
      ++summary.unsupported_source_packets;
      supported = false;
    }
    if (supported) {
      ++summary.planned_draw_packets;
    } else {
      ++summary.unsupported_packets;
    }
  }
  for (const RefiiFrameActionPacket& action : actions) {
    switch (action.type) {
      case RefiiFrameActionType::kDraw:
        ++summary.planned_action_packets;
        break;
      case RefiiFrameActionType::kClear:
        ++summary.planned_clear_packets;
        ++summary.planned_action_packets;
        break;
      case RefiiFrameActionType::kResolve:
        ++summary.planned_resolve_packets;
        ++summary.planned_action_packets;
        break;
      case RefiiFrameActionType::kSurfaceDestination:
        ++summary.planned_copy_packets;
        ++summary.planned_action_packets;
        break;
      case RefiiFrameActionType::kPresentBlit:
        ++summary.planned_present_packets;
        ++summary.planned_action_packets;
        break;
      case RefiiFrameActionType::kUnknown:
      default:
        ++summary.unsupported_packets;
        break;
    }
  }
  return summary;
}

}  // namespace refii::renderer
