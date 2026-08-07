#include "runtime/refii_renderer/plume/fable/packets/fable_refii_packet_builders.h"

namespace refii::renderer {

RefiiPacketRoot ToRefiiPacketRoot(const FableDirectFrameDatabaseRoot& root) {
  RefiiPacketRoot packet_root;
  packet_root.call_serial = root.call_serial;
  packet_root.context_key = root.context_key;
  packet_root.symbol_address = root.symbol_address;
  packet_root.lr = root.lr;
  return packet_root;
}

RefiiPacketRoot ToRefiiPacketRoot(const FableDirectStateRoot& root) {
  RefiiPacketRoot packet_root;
  packet_root.call_serial = root.call_serial;
  packet_root.context_key = root.context_key;
  packet_root.symbol_address = root.symbol_address;
  packet_root.lr = root.lr;
  return packet_root;
}

RefiiInlineBufferPayload ToRefiiInlineBufferPayload(
    const FableDirectInlineBuffer& source) {
  RefiiInlineBufferPayload payload;
  if (!source.ready()) {
    return payload;
  }
  payload.address = source.address;
  payload.byte_size = source.byte_size;
  payload.stride_or_format = source.stride_or_format;
  // The hash covers data[0..byte_size), exactly the bytes kept below, so the
  // trim does not invalidate it.
  payload.content_hash = source.content_hash;
  payload.data.assign(source.data.begin(),
                      source.data.begin() + source.byte_size);
  return payload;
}

RefiiInlineTexturePayload ToRefiiInlineTexturePayload(
    const RendererInlineTextureUploadSnapshot& source) {
  RefiiInlineTexturePayload payload;
  if (!source.ready()) {
    return payload;
  }
  payload.address = source.address;
  payload.parent_address = source.parent_address;
  payload.base_address = source.base_address;
  payload.byte_size = source.byte_size;
  payload.raw_fetch_dwords = source.raw_fetch_dwords;
  payload.normalized_fetch_dwords = source.normalized_fetch_dwords;
  payload.upload_generation = source.upload_generation;
  payload.upload_hash = source.upload_hash;
  payload.serial = source.serial;
  payload.data = *source.data;
  return payload;
}

RefiiDrawPacket ToRefiiDrawPacket(
    uint64_t frame_id,
    const FableDirectFrameDatabaseDrawPacket& source) {
  RefiiDrawPacket packet;
  packet.packet_id = source.packet_id;
  packet.frame_id = frame_id;
  packet.command_serial = source.command_serial;
  packet.command_type = source.command_type;
  packet.source = source.source;
  packet.pass_name = source.pass_name;
  packet.debug_label = source.debug_label;
  packet.vertex_shader = source.vertex_shader;
  packet.pixel_shader = source.pixel_shader;
  packet.vertex_declaration = source.vertex_declaration;
  packet.render_targets = source.render_targets;
  packet.depth_target = source.depth_stencil;
  packet.textures = source.textures;
  packet.sampler_states = source.sampler_states;
  packet.vertex_buffers = source.vertex_buffers;
  packet.vertex_strides = source.vertex_strides;
  packet.vertex_offsets = source.vertex_offsets;
  packet.index_buffer = source.index_buffer;
  packet.index_format = source.index_format;
  packet.inline_vertex_buffers = source.inline_vertex_buffers;
  packet.inline_index_buffer = source.inline_index_buffer;
  packet.pix_video_planes = source.pix_video_planes;
  packet.raster_state.pm4_valid = source.pipeline_pm4_valid;
  packet.raster_state.cull_control = source.cull_control;
  packet.raster_state.pa_su_sc_mode_cntl = source.pa_su_sc_mode_cntl;
  packet.depth_stencil_state.pm4_valid = source.pipeline_pm4_valid;
  packet.depth_stencil_state.depth_control = source.depth_control;
  packet.depth_stencil_state.stencil_ref_mask = source.stencil_ref_mask;
  packet.depth_stencil_state.stencil_ref_mask_bf = source.stencil_ref_mask_bf;
  packet.blend_state.pm4_valid = source.pipeline_pm4_valid;
  packet.blend_state.blend_controls = source.blend_controls;
  packet.blend_state.color_write_masks = source.color_write_masks;
  packet.blend_state.color_control = source.color_control;
  packet.blend_state.alpha_ref = source.alpha_ref;
  packet.blend_state.alpha_test_enable = source.alpha_test_enable;
  packet.blend_state.alpha_func = source.alpha_func;
  packet.private_texture = source.private_texture;
  packet.constants = source.constants;
  packet.draw_root = ToRefiiPacketRoot(source.draw_root);
  packet.vertex_shader_root = ToRefiiPacketRoot(source.vertex_shader_root);
  packet.pixel_shader_root = ToRefiiPacketRoot(source.pixel_shader_root);
  packet.private_texture_wrapper_root =
      ToRefiiPacketRoot(source.private_texture_wrapper_root);
  packet.indexed = source.indexed;
  packet.primitive_topology = source.primitive_type;
  packet.start_index = source.start;
  packet.index_count = source.vertex_or_index_count;
  packet.base_vertex = source.base_vertex;
  packet.primitive_count = source.primitive_count;
  packet.viewport.valid = source.viewport_valid;
  packet.viewport.x = source.viewport_x;
  packet.viewport.y = source.viewport_y;
  packet.viewport.width = source.viewport_width;
  packet.viewport.height = source.viewport_height;
  packet.viewport.min_depth = source.viewport_min_depth;
  packet.viewport.max_depth = source.viewport_max_depth;
  packet.scissor.valid = source.scissor_valid;
  packet.scissor.left = source.scissor_left;
  packet.scissor.top = source.scissor_top;
  packet.scissor.right = source.scissor_right;
  packet.scissor.bottom = source.scissor_bottom;
  return packet;
}

RefiiFrameActionPacket BuildRefiiSurfaceDestinationActionPacket(
    uint64_t frame_id,
    const FableDirectFrameDatabaseCommandPacket& packet) {
  RefiiFrameActionPacket action;
  action.type = RefiiFrameActionType::kSurfaceDestination;
  action.packet_id = packet.packet_id;
  action.frame_id = frame_id;
  action.command_serial = packet.command_serial;
  action.command_type = packet.command_type;
  action.surface_destination.packet_id = packet.packet_id;
  action.surface_destination.frame_id = action.frame_id;
  action.surface_destination.command_serial = packet.command_serial;
  action.surface_destination.root = ToRefiiPacketRoot(packet.root);
  action.surface_destination.relation_serial =
      packet.surface_destination.relation_serial;
  action.surface_destination.source_surface =
      packet.surface_destination.source_surface;
  action.surface_destination.destination_texture =
      packet.surface_destination.destination_texture;
  action.surface_destination.source_slot =
      packet.surface_destination.source_slot;
  action.surface_destination.source_width =
      packet.surface_destination.source_width;
  action.surface_destination.source_height =
      packet.surface_destination.source_height;
  action.surface_destination.destination_width =
      packet.surface_destination.destination_width;
  action.surface_destination.destination_height =
      packet.surface_destination.destination_height;
  action.surface_destination.texture_base =
      packet.surface_destination.texture_base;
  action.surface_destination.backing_texture =
      packet.surface_destination.backing_texture;
  action.surface_destination.backing_base =
      packet.surface_destination.backing_base;
  action.surface_destination.backing_content_serial =
      packet.surface_destination.backing_content_serial;
  action.surface_destination.target_content_serial =
      packet.surface_destination.target_content_serial;
  action.surface_destination.scaled_copy =
      packet.surface_destination.scaled_copy;
  action.surface_destination.trusted_destination_edge =
      packet.surface_destination.trusted_destination_edge;
  action.surface_destination.wrapper_source_exact =
      packet.surface_destination.wrapper_source_exact;
  return action;
}

RefiiFrameActionPacket BuildRefiiPresentBlitActionPacket(
    uint64_t frame_id,
    const FableDirectFrameDatabaseCommandPacket& packet) {
  RefiiFrameActionPacket action;
  action.type = RefiiFrameActionType::kPresentBlit;
  action.packet_id = packet.packet_id;
  action.frame_id = frame_id;
  action.command_serial = packet.command_serial;
  action.command_type = packet.command_type;
  action.present_blit.packet_id = packet.packet_id;
  action.present_blit.frame_id = action.frame_id;
  action.present_blit.command_serial = packet.command_serial;
  action.present_blit.source_target = packet.present_blit.source_target;
  action.present_blit.output_resource = packet.present_blit.output_resource;
  action.present_blit.source_width = packet.present_blit.source_width;
  action.present_blit.source_height = packet.present_blit.source_height;
  action.present_blit.texture_width = packet.present_blit.texture_width;
  action.present_blit.texture_height = packet.present_blit.texture_height;
  action.present_blit.output_width = packet.present_blit.output_width;
  action.present_blit.output_height = packet.present_blit.output_height;
  action.present_blit.source_write_serial =
      packet.present_blit.source_write_serial;
  action.present_blit.source_frame = packet.present_blit.source_frame;
  action.present_blit.reason = packet.present_blit.reason;
  return action;
}

}  // namespace refii::renderer
