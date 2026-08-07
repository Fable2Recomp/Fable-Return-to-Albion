#include "runtime/refii_renderer/plume/fable/packets/fable_refii_replay_builders.h"

#include "runtime/cvars.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_refii_packet_analysis.h"
#include "runtime/plume/draw/plume_draw_state.h"

#include <algorithm>
#include <cstddef>
#include <memory>

namespace refii::renderer {

namespace {

FableDirectStateRoot FromRefiiPacketRoot(const RefiiPacketRoot& root) {
  FableDirectStateRoot state_root;
  state_root.call_serial = root.call_serial;
  state_root.context_key = root.context_key;
  state_root.symbol_address = root.symbol_address;
  state_root.lr = root.lr;
  return state_root;
}

FableDirectInlineBuffer ToFableDirectInlineBuffer(
    const RefiiInlineBufferPayload& payload) {
  FableDirectInlineBuffer buffer;
  if (!payload.valid()) {
    return buffer;
  }
  buffer.address = payload.address;
  buffer.byte_size = payload.byte_size;
  buffer.stride_or_format = payload.stride_or_format;
  buffer.content_hash = payload.content_hash;
  buffer.data = payload.data;
  return buffer;
}

RendererInlineTextureUploadSnapshot ToRendererInlineTextureUploadSnapshot(
    const RefiiInlineTexturePayload& payload) {
  RendererInlineTextureUploadSnapshot upload;
  if (!payload.valid()) {
    return upload;
  }
  upload.address = payload.address;
  upload.parent_address = payload.parent_address;
  upload.base_address = payload.base_address;
  upload.byte_size = payload.byte_size;
  upload.raw_fetch_dwords = payload.raw_fetch_dwords;
  upload.normalized_fetch_dwords = payload.normalized_fetch_dwords;
  upload.upload_generation = payload.upload_generation;
  upload.upload_hash = payload.upload_hash;
  upload.serial = payload.serial;
  upload.data = std::make_shared<const std::vector<uint8_t>>(payload.data);
  return upload;
}

}  // namespace

FableDirectRenderCommand BuildDirectCommandFromRefiiClearPacket(
    const RefiiClearPacket& packet) {
  FableDirectRenderCommand command;
  command.type = FableDirectRenderCommandType::kClear;
  command.serial =
      packet.command_serial != 0 ? packet.command_serial : packet.packet_id;
  command.draw_serial = packet.packet_id;
  command.resources.render_targets = packet.render_targets;
  command.resources.depth_stencil = packet.depth_target;
  command.state_root = FromRefiiPacketRoot(packet.root);
  command.clear.flags = packet.flags;
  command.clear.color = packet.color;
  command.clear.depth = packet.depth;
  command.clear.stencil = packet.stencil;
  command.clear.rect.valid = packet.rect.valid;
  command.clear.rect.left = packet.rect.left;
  command.clear.rect.top = packet.rect.top;
  command.clear.rect.right = packet.rect.right;
  command.clear.rect.bottom = packet.rect.bottom;
  command.clear.root_call_serial = packet.root.call_serial;
  command.clear.root_context_key = packet.root.context_key;
  command.clear.root_symbol_address = packet.root.symbol_address;
  command.clear.root_lr = packet.root.lr;
  for (size_t index = 0; index < command.clear.target_roots.color.size();
       ++index) {
    command.clear.target_roots.color[index] =
        FromRefiiPacketRoot(packet.render_target_roots[index]);
  }
  command.clear.target_roots.depth =
      FromRefiiPacketRoot(packet.depth_target_root);
  return command;
}

FableDirectRenderCommand BuildDirectCommandFromRefiiResolvePacket(
    const RefiiResolvePacket& packet) {
  FableDirectRenderCommand command;
  command.type = FableDirectRenderCommandType::kResolveCopy;
  command.serial =
      packet.command_serial != 0 ? packet.command_serial : packet.packet_id;
  command.draw_serial = packet.packet_id;
  command.resources.render_targets = packet.render_targets;
  command.resources.depth_stencil = packet.depth_target;
  command.state_root = FromRefiiPacketRoot(packet.root);
  command.draw.source = RendererDrawEventSource::kPm4Resolve;
  command.pipeline.pm4.valid = true;
  command.pipeline.pm4.rb_surface_info = packet.surface_info;
  command.pipeline.pm4.rb_color_info = packet.color_info;
  command.pipeline.pm4.rb_depth_info = packet.depth_info;
  command.pipeline.pm4.rb_copy_control = packet.copy_control;
  command.pipeline.pm4.rb_copy_dest_base = packet.dest_base;
  command.pipeline.pm4.rb_copy_dest_pitch = packet.dest_pitch;
  command.pipeline.pm4.rb_copy_dest_info = packet.dest_info;
  command.pipeline.pm4.rb_copy_func = packet.copy_func;
  command.pipeline.pm4.rb_copy_ref = packet.copy_ref;
  command.pipeline.pm4.rb_copy_mask = packet.copy_mask;
  command.pipeline.pm4.rb_copy_surface_slice = packet.surface_slice;
  command.resolve.rb_surface_info = packet.surface_info;
  command.resolve.rb_color_info = packet.color_info;
  command.resolve.rb_depth_info = packet.depth_info;
  command.resolve.rb_copy_control = packet.copy_control;
  command.resolve.rb_copy_dest_base = packet.dest_base;
  command.resolve.rb_copy_dest_pitch = packet.dest_pitch;
  command.resolve.rb_copy_dest_info = packet.dest_info;
  command.resolve.rb_copy_func = packet.copy_func;
  command.resolve.rb_copy_ref = packet.copy_ref;
  command.resolve.rb_copy_mask = packet.copy_mask;
  command.resolve.rb_copy_surface_slice = packet.surface_slice;
  command.resolve.refii_source_surface = packet.source_surface;
  command.resolve.refii_source_slot = packet.source_slot;
  command.resolve.refii_source_depth = packet.source_depth;
  return command;
}

FableDirectRenderCommand BuildDirectCommandFromRefiiDrawPacket(
    const RefiiDrawPacket& packet) {
  FableDirectRenderCommand command;
  const RendererDrawEventSource source =
      static_cast<RendererDrawEventSource>(packet.source);
  const bool draw_vertices_up =
      source == RendererDrawEventSource::kDrawVerticesUp &&
      RefiiPacketDrawVerticesUpReplaySupported(packet);
  command.type =
      draw_vertices_up
          ? FableDirectRenderCommandType::kDrawPrimitiveUP
          : packet.indexed ? FableDirectRenderCommandType::kDrawIndexedPrimitive
                           : FableDirectRenderCommandType::kDrawPrimitive;
  command.serial =
      packet.command_serial != 0 ? packet.command_serial : packet.packet_id;
  command.draw_serial = packet.packet_id;
  command.state_root = FromRefiiPacketRoot(packet.draw_root);
  command.resources.shader_roots.vertex_shader =
      FromRefiiPacketRoot(packet.vertex_shader_root);
  command.resources.shader_roots.pixel_shader =
      FromRefiiPacketRoot(packet.pixel_shader_root);
  command.resources.vertex_shader = packet.vertex_shader;
  command.resources.pixel_shader = packet.pixel_shader;
  command.resources.vertex_declaration = packet.vertex_declaration;
  command.resources.render_targets = packet.render_targets;
  command.resources.depth_stencil = packet.depth_target;
  for (size_t index = 0; index < command.resources.textures.size() &&
                         index < packet.textures.size();
       ++index) {
    command.resources.textures[index] = packet.textures[index];
  }
  for (size_t index = 0; index < command.sampler_states.size() &&
                         index < packet.sampler_states.size();
       ++index) {
    command.sampler_states[index] = packet.sampler_states[index];
  }
  command.resources.index_buffer = packet.index_buffer;
  command.draw.source = source;
  command.draw.draw.indexed = packet.indexed;
  command.draw.draw.primitive_type = packet.primitive_topology;
  command.draw.draw.start = packet.start_index;
  command.draw.draw.primitive_count = packet.primitive_count;
  command.draw.draw.base_vertex = static_cast<uint32_t>(packet.base_vertex);
  command.draw.vertex_or_index_count = packet.index_count;
  if (command.draw.vertex_or_index_count != 0) {
    command.draw.draw.primitive_count = packet.primitive_count;
  }
  command.draw.index_format = packet.index_format;
  command.draw.base_vertex = packet.base_vertex;
  command.draw.base_vertex_valid = true;
  command.draw.draw_vertices_up = draw_vertices_up;
  command.draw.private_texture_rectangle = packet.private_texture.enabled;
  command.draw.private_texture_slot = packet.private_texture.texture_slot;
  command.draw.private_texture_source_x = packet.private_texture.source_x;
  command.draw.private_texture_source_y = packet.private_texture.source_y;
  command.draw.private_texture_corners_valid =
      packet.private_texture.corners_valid;
  command.draw.private_texture_corners = packet.private_texture.corners;
  command.draw.private_texture_uv_override =
      packet.private_texture.uv_override;
  command.draw.private_texture_u0 = packet.private_texture.u0;
  command.draw.private_texture_v0 = packet.private_texture.v0;
  command.draw.private_texture_u1 = packet.private_texture.u1;
  command.draw.private_texture_v1 = packet.private_texture.v1;
  const FableDirectStateRoot private_texture_wrapper_root =
      FromRefiiPacketRoot(packet.private_texture_wrapper_root);
  command.draw.private_texture_wrapper_call_serial =
      private_texture_wrapper_root.call_serial;
  command.draw.private_texture_wrapper_context_key =
      private_texture_wrapper_root.context_key;
  command.draw.private_texture_wrapper_symbol_address =
      private_texture_wrapper_root.symbol_address;
  command.draw.private_texture_wrapper_source_texture =
      packet.private_texture.wrapper_source_texture;
  command.draw.private_texture_wrapper_source_exact =
      packet.private_texture.wrapper_source_exact;
  command.viewport.valid = packet.viewport.valid;
  command.viewport.x = packet.viewport.x;
  command.viewport.y = packet.viewport.y;
  command.viewport.width = packet.viewport.width;
  command.viewport.height = packet.viewport.height;
  command.viewport.min_depth = packet.viewport.min_depth;
  command.viewport.max_depth = packet.viewport.max_depth;
  command.scissor.valid = packet.scissor.valid;
  command.scissor.left = packet.scissor.left;
  command.scissor.top = packet.scissor.top;
  command.scissor.right = packet.scissor.right;
  command.scissor.bottom = packet.scissor.bottom;
  command.pipeline.pm4.valid = packet.raster_state.pm4_valid ||
                               packet.depth_stencil_state.pm4_valid ||
                               packet.blend_state.pm4_valid;
  command.pipeline.pm4.rb_depthcontrol =
      packet.depth_stencil_state.depth_control;
  command.pipeline.pm4.rb_stencilrefmask =
      packet.depth_stencil_state.stencil_ref_mask;
  command.pipeline.pm4.rb_stencilrefmask_bf =
      packet.depth_stencil_state.stencil_ref_mask_bf;
  command.pipeline.pm4.pa_su_sc_mode_cntl =
      packet.raster_state.pa_su_sc_mode_cntl;
  command.pipeline.pm4.rb_blendcontrol = packet.blend_state.blend_controls;
  command.pipeline.pm4.rb_colorcontrol = packet.blend_state.color_control;
  command.pipeline.pm4.rb_alpha_ref = packet.blend_state.alpha_ref;
  command.pipeline.pm4.rb_color_mask = 0;
  for (size_t slot = 0; slot < packet.blend_state.color_write_masks.size();
       ++slot) {
    command.pipeline.pm4.rb_color_mask |=
        (packet.blend_state.color_write_masks[slot] & 0xFu) << (slot * 4u);
  }
  command.pipeline.render_states[kFableRenderStateCullMode] =
      packet.raster_state.cull_control;
  command.pipeline.render_states[kFableRenderStateColorWriteEnable] =
      packet.blend_state.color_write_masks[0];
  command.pipeline.render_states[kFableRenderStateAlphaRef] =
      packet.blend_state.alpha_ref;
  command.pipeline.render_states[kFableRenderStateAlphaTestEnable] =
      packet.blend_state.alpha_test_enable;
  command.pipeline.render_states[kFableRenderStateAlphaFunc] =
      packet.blend_state.alpha_func;
  command.constants.fetch = packet.constants.fetch;
  command.constants.vertex_float = packet.constants.vertex_float;
  command.constants.pixel_float = packet.constants.pixel_float;
  command.constants.vertex_int = packet.constants.vertex_int;
  command.constants.pixel_int = packet.constants.pixel_int;
  command.constants.vertex_int_valid_mask =
      packet.constants.vertex_int_valid_mask;
  command.constants.pixel_int_valid_mask =
      packet.constants.pixel_int_valid_mask;
  command.constants.vertex_bool = packet.constants.vertex_bool;
  command.constants.pixel_bool = packet.constants.pixel_bool;

  for (size_t index = 0; index < command.inline_vertex_buffers.size() &&
                         index < packet.inline_vertex_buffers.size();
       ++index) {
    command.inline_vertex_buffers[index] =
        ToFableDirectInlineBuffer(packet.inline_vertex_buffers[index]);
  }
  command.inline_index_buffer =
      ToFableDirectInlineBuffer(packet.inline_index_buffer);
  if (packet.indexed && command.resources.index_buffer == 0 &&
      command.inline_index_buffer.ready()) {
    command.resources.index_buffer = command.inline_index_buffer.address;
  }

  for (size_t index = 0; index < command.resources.streams.size() &&
                         index < packet.vertex_buffers.size();
       ++index) {
    RendererStreamBindingSnapshot& stream = command.resources.streams[index];
    stream.buffer = packet.vertex_buffers[index];
    stream.stride = packet.vertex_strides[index];
    stream.offset = packet.vertex_offsets[index];
  }
  for (size_t index = 0; index < command.inline_vertex_buffers.size();
       ++index) {
    const FableDirectInlineBuffer& inline_vertex =
        command.inline_vertex_buffers[index];
    RendererStreamBindingSnapshot& stream = command.resources.streams[index];
    if (!inline_vertex.ready() || stream.buffer != 0) {
      continue;
    }
    stream.buffer = inline_vertex.address;
    stream.stride = inline_vertex.stride_or_format;
  }

  const bool pix_video_private_rect =
      source == RendererDrawEventSource::kPixVideo &&
         packet.private_texture.corners_valid &&
      RefiiPacketHasPixVideoPlanes(packet);
  switch (source) {
    case RendererDrawEventSource::kSceneObject:
    case RendererDrawEventSource::kPm4Draw:
    case RendererDrawEventSource::kUnknown:
    case RendererDrawEventSource::kPm4Resolve:
    case RendererDrawEventSource::kClear:
    default:
      break;
  }
  if (pix_video_private_rect || RefiiPacketPixVideoReplaySupported(packet)) {
    for (size_t index = 0; index < command.pix_video_planes.size() &&
                           index < packet.pix_video_planes.size();
         ++index) {
      command.pix_video_planes[index] =
          ToRendererInlineTextureUploadSnapshot(packet.pix_video_planes[index]);
    }
    for (RendererInlineTextureUploadSnapshot& upload :
         command.pix_video_planes) {
      if (upload.ready()) {
        continue;
      }
      const auto mirrored_plane = std::find_if(
          command.pix_video_planes.begin(), command.pix_video_planes.end(),
          [](const RendererInlineTextureUploadSnapshot& plane) {
            return plane.ready();
          });
      if (mirrored_plane != command.pix_video_planes.end()) {
        upload = *mirrored_plane;
      }
    }
  }
  return command;
}

std::vector<FableDirectRenderCommand> BuildDirectReplayCommandsFromRefiiQueue(
    const RefiiFramePacketQueueSnapshot& queue) {
  std::vector<FableDirectRenderCommand> commands;
  commands.reserve(queue.actions.size());
  for (const RefiiFrameActionPacket& action : queue.actions) {
    switch (action.type) {
      case RefiiFrameActionType::kDraw:
        if (action.draw_packet_index < queue.packets.size()) {
          commands.push_back(BuildDirectCommandFromRefiiDrawPacket(
              queue.packets[action.draw_packet_index]));
        } else {
          commands.push_back(FableDirectRenderCommand{});
        }
        break;
      case RefiiFrameActionType::kClear:
        commands.push_back(BuildDirectCommandFromRefiiClearPacket(
            action.clear));
        break;
      case RefiiFrameActionType::kResolve:
        // NOTE (§3b): do NOT blanket-suppress these under GTL compose. The
        // replay carries TWO resolve families: wrapper D3DDevice_Resolve
        // (0x82206888, GTL-owned) AND export-invalidate page-tile resolves
        // (QueueExportInvalidateResolveEventLocked), which the GTL does not
        // capture — suppressing both scrambled the storybook page strip
        // (2026-07-18 run). Per-destination ownership is enforced at the
        // kResolveCopy execution seam in the direct renderer instead.
        commands.push_back(BuildDirectCommandFromRefiiResolvePacket(
            action.resolve));
        break;
      default:
        break;
    }
  }
  return commands;
}

}  // namespace refii::renderer
