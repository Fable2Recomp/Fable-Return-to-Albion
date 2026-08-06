#pragma once

#include <array>
#include <cstdint>
#include <string>
#include <vector>

namespace refii::renderer {

using RefiiResourceId = uint32_t;
using RefiiShaderId = uint32_t;
using RefiiRenderTargetId = uint32_t;
using RefiiDepthTargetId = uint32_t;
using RefiiTextureId = uint32_t;
using RefiiSamplerStateId = uint32_t;
using RefiiBufferId = uint32_t;
using RefiiVertexDeclarationId = uint32_t;

inline constexpr uint32_t kRefiiShaderFloatConstantDwords = 256u * 4u;
inline constexpr uint32_t kRefiiShaderIntConstantDwords = 32u * 4u;
inline constexpr uint32_t kRefiiShaderBoolConstantDwords = 8u;
inline constexpr uint32_t kRefiiFetchConstantDwords = 192u;

struct RefiiPacketRoot {
  uint64_t call_serial = 0;
  uint64_t context_key = 0;
  uint32_t symbol_address = 0;
  uint32_t lr = 0;
};

struct RefiiViewport {
  bool valid = false;
  float x = 0.0f;
  float y = 0.0f;
  float width = 0.0f;
  float height = 0.0f;
  float min_depth = 0.0f;
  float max_depth = 1.0f;
};

struct RefiiScissorRect {
  bool valid = false;
  int32_t left = 0;
  int32_t top = 0;
  int32_t right = 0;
  int32_t bottom = 0;
};

struct RefiiRasterState {
  bool pm4_valid = false;
  uint32_t cull_control = 0;
  uint32_t pa_su_sc_mode_cntl = 0;
};

struct RefiiDepthStencilState {
  bool pm4_valid = false;
  uint32_t depth_control = 0;
  uint32_t stencil_ref_mask = 0;
  uint32_t stencil_ref_mask_bf = 0;
};

struct RefiiBlendState {
  bool pm4_valid = false;
  std::array<uint32_t, 4> blend_controls = {};
  std::array<uint32_t, 4> color_write_masks = {};
  uint32_t color_control = 0;
  uint32_t alpha_ref = 0;
  uint32_t alpha_test_enable = 0;
  uint32_t alpha_func = 0;
};

struct RefiiPrivateTextureRectPayload {
  bool enabled = false;
  uint32_t texture_slot = 0;
  float source_x = 0.0f;
  float source_y = 0.0f;
  bool corners_valid = false;
  std::array<float, 6> corners = {};
  bool uv_override = false;
  float u0 = 0.0f;
  float v0 = 0.0f;
  float u1 = 1.0f;
  float v1 = 1.0f;
  RefiiTextureId wrapper_source_texture = 0;
  bool wrapper_source_exact = false;
};

struct RefiiConstantStore {
  std::array<uint32_t, kRefiiFetchConstantDwords> fetch = {};
  std::array<uint32_t, kRefiiShaderFloatConstantDwords> vertex_float = {};
  std::array<uint32_t, kRefiiShaderFloatConstantDwords> pixel_float = {};
  std::array<uint32_t, kRefiiShaderIntConstantDwords> vertex_int = {};
  std::array<uint32_t, kRefiiShaderIntConstantDwords> pixel_int = {};
  uint32_t vertex_int_valid_mask = 0;
  uint32_t pixel_int_valid_mask = 0;
  std::array<uint32_t, kRefiiShaderBoolConstantDwords> vertex_bool = {};
  std::array<uint32_t, kRefiiShaderBoolConstantDwords> pixel_bool = {};
};

struct RefiiInlineBufferPayload {
  RefiiBufferId address = 0;
  uint32_t byte_size = 0;
  uint32_t stride_or_format = 0;
  // DirectHashBytes(data[0..byte_size)) carried through the command<->packet
  // round-trip so re-recorded commands keep the stamp their payload writer
  // computed (see FableDirectInlineBuffer::content_hash). 0 = not computed.
  uint64_t content_hash = 0;
  std::vector<uint8_t> data;

  bool valid() const {
    return address != 0 && byte_size != 0 && data.size() >= byte_size;
  }
};

struct RefiiInlineTexturePayload {
  RefiiTextureId address = 0;
  RefiiTextureId parent_address = 0;
  uint32_t base_address = 0;
  uint32_t byte_size = 0;
  std::array<uint32_t, 6> raw_fetch_dwords = {};
  std::array<uint32_t, 6> normalized_fetch_dwords = {};
  uint64_t upload_generation = 0;
  uint64_t upload_hash = 0;
  uint64_t serial = 0;
  std::vector<uint8_t> data;

  bool valid() const {
    return address != 0 && upload_generation != 0 && !data.empty();
  }
};

struct RefiiDrawPacket {
  uint64_t packet_id = 0;
  uint64_t frame_id = 0;
  uint64_t command_serial = 0;
  uint32_t command_type = 0;
  uint32_t source = 0;
  std::string pass_name;
  std::string debug_label;

  RefiiShaderId vertex_shader = 0;
  RefiiShaderId pixel_shader = 0;
  RefiiVertexDeclarationId vertex_declaration = 0;

  std::array<RefiiRenderTargetId, 4> render_targets = {};
  RefiiDepthTargetId depth_target = 0;

  std::array<RefiiTextureId, 16> textures = {};
  std::array<RefiiSamplerStateId, 16> sampler_states = {};

  std::array<RefiiBufferId, 16> vertex_buffers = {};
  std::array<uint32_t, 16> vertex_strides = {};
  std::array<uint32_t, 16> vertex_offsets = {};

  RefiiBufferId index_buffer = 0;
  uint32_t index_format = 0;
  std::array<RefiiInlineBufferPayload, 16> inline_vertex_buffers = {};
  RefiiInlineBufferPayload inline_index_buffer;
  std::array<RefiiInlineTexturePayload, 3> pix_video_planes = {};

  RefiiPacketRoot draw_root;
  RefiiPacketRoot vertex_shader_root;
  RefiiPacketRoot pixel_shader_root;
  RefiiPacketRoot private_texture_wrapper_root;

  bool indexed = false;
  uint32_t primitive_topology = 0;
  uint32_t start_index = 0;
  uint32_t index_count = 0;
  int32_t base_vertex = 0;
  uint32_t primitive_count = 0;

  RefiiViewport viewport;
  RefiiScissorRect scissor;
  RefiiRasterState raster_state;
  RefiiDepthStencilState depth_stencil_state;
  RefiiBlendState blend_state;
  RefiiPrivateTextureRectPayload private_texture;
  RefiiConstantStore constants;
};

enum class RefiiFrameActionType : uint32_t {
  kUnknown = 0,
  kDraw = 1,
  kClear = 2,
  kResolve = 3,
  kSurfaceDestination = 4,
  kPresentBlit = 5,
};

struct RefiiClearPacket {
  uint64_t packet_id = 0;
  uint64_t frame_id = 0;
  uint64_t command_serial = 0;
  uint32_t command_type = 0;

  std::array<RefiiRenderTargetId, 4> render_targets = {};
  RefiiDepthTargetId depth_target = 0;
  RefiiPacketRoot root;
  std::array<RefiiPacketRoot, 4> render_target_roots = {};
  RefiiPacketRoot depth_target_root;

  uint32_t flags = 0;
  std::array<float, 4> color = {0.0f, 0.0f, 0.0f, 1.0f};
  float depth = 1.0f;
  uint32_t stencil = 0;
  RefiiScissorRect rect;
};

struct RefiiResolvePacket {
  uint64_t packet_id = 0;
  uint64_t frame_id = 0;
  uint64_t command_serial = 0;
  uint32_t command_type = 0;

  std::array<RefiiRenderTargetId, 4> render_targets = {};
  RefiiDepthTargetId depth_target = 0;
  RefiiPacketRoot root;

  uint32_t surface_info = 0;
  std::array<uint32_t, 4> color_info = {};
  uint32_t depth_info = 0;
  uint32_t copy_control = 0;
  uint32_t source_surface = 0;
  uint32_t source_slot = 0;
  bool source_depth = false;
  uint32_t dest_base = 0;
  uint32_t dest_pitch = 0;
  uint32_t dest_info = 0;
  uint32_t copy_func = 0;
  uint32_t copy_ref = 0;
  uint32_t copy_mask = 0;
  uint32_t surface_slice = 0;
};

struct RefiiSurfaceDestinationPacket {
  uint64_t packet_id = 0;
  uint64_t frame_id = 0;
  uint64_t command_serial = 0;
  RefiiPacketRoot root;

  uint64_t relation_serial = 0;
  RefiiTextureId source_surface = 0;
  RefiiTextureId destination_texture = 0;
  uint32_t source_slot = UINT32_MAX;
  uint32_t source_width = 0;
  uint32_t source_height = 0;
  uint32_t destination_width = 0;
  uint32_t destination_height = 0;
  uint32_t texture_base = 0;
  RefiiTextureId backing_texture = 0;
  uint32_t backing_base = 0;
  uint64_t backing_content_serial = 0;
  uint64_t target_content_serial = 0;
  bool scaled_copy = false;
  bool trusted_destination_edge = false;
  bool wrapper_source_exact = false;
};

struct RefiiPresentBlitPacket {
  uint64_t packet_id = 0;
  uint64_t frame_id = 0;
  uint64_t command_serial = 0;

  RefiiTextureId source_target = 0;
  RefiiTextureId output_resource = 0;
  uint32_t source_width = 0;
  uint32_t source_height = 0;
  uint32_t texture_width = 0;
  uint32_t texture_height = 0;
  uint32_t output_width = 0;
  uint32_t output_height = 0;
  uint64_t source_write_serial = 0;
  uint32_t source_frame = 0;
  std::string reason;
};

struct RefiiFrameActionPacket {
  RefiiFrameActionType type = RefiiFrameActionType::kUnknown;
  uint64_t packet_id = 0;
  uint64_t frame_id = 0;
  uint64_t command_serial = 0;
  uint32_t command_type = 0;
  uint32_t draw_packet_index = UINT32_MAX;
  RefiiClearPacket clear;
  RefiiResolvePacket resolve;
  RefiiSurfaceDestinationPacket surface_destination;
  RefiiPresentBlitPacket present_blit;
};

struct RefiiFramePacketDump {
  uint64_t frame_id = 0;
  std::vector<RefiiDrawPacket> packets;
  std::vector<RefiiFrameActionPacket> actions;
  uint32_t omitted_packets = 0;
  uint32_t omitted_actions = 0;
};

struct RefiiPacketReplaySummary {
  uint32_t queue_depth = 0;
  uint32_t action_queue_depth = 0;
  uint32_t planned_draw_packets = 0;
  uint32_t planned_clear_packets = 0;
  uint32_t planned_resolve_packets = 0;
  uint32_t planned_copy_packets = 0;
  uint32_t planned_present_packets = 0;
  uint32_t planned_action_packets = 0;
  uint32_t unsupported_packets = 0;
  uint32_t indexed_packets = 0;
  uint32_t missing_shader_packets = 0;
  uint32_t missing_target_packets = 0;
  uint32_t missing_vertex_input_packets = 0;
  uint32_t missing_index_input_packets = 0;
  uint32_t empty_draw_packets = 0;
  uint32_t missing_private_texture_payload_packets = 0;
  uint32_t unsupported_source_packets = 0;
};

struct RefiiFramePacketQueueSnapshot {
  uint64_t frame_id = 0;
  uint64_t generation = 0;
  std::vector<RefiiDrawPacket> packets;
  std::vector<RefiiFrameActionPacket> actions;
  uint32_t omitted_packets = 0;
  uint32_t omitted_actions = 0;
  RefiiPacketReplaySummary replay;
  bool replay_ready = false;
};

}  // namespace refii::renderer
