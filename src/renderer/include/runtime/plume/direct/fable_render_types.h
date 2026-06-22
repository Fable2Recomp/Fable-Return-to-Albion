#pragma once

#include <array>
#include <cstdint>
#include <memory>
#include <vector>

namespace refii::renderer {

constexpr uint32_t kRendererShaderFloatConstantDwords = 256u * 4u;
constexpr uint32_t kRendererShaderIntConstantDwords = 32u * 4u;
constexpr uint32_t kRendererShaderBoolConstantDwords = 8u;
constexpr uint32_t kRendererFetchConstantDwords = 192u;
constexpr uint32_t kRendererD3DRenderStateSlots = 32u;
constexpr uint32_t kRendererD3DTextureSlots = 32u;
constexpr uint32_t kRendererD3DSamplerStateSlots = 32u;
constexpr uint32_t kRendererSurfaceCopyAliasSlots = 256u;

struct RendererStreamBindingSnapshot {
  uint32_t buffer = 0;
  uint32_t offset = 0;
  uint32_t stride = 0;
};

struct RendererLastDrawSnapshot {
  bool indexed = false;
  uint32_t primitive_type = 0;
  uint32_t base_vertex = 0;
  uint32_t start = 0;
  uint32_t primitive_count = 0;
};

enum class RendererDrawEventSource : uint32_t {
  kUnknown,
  kD3DWrapper,
  kScratchWrapper,
  kDrawVerticesUp,
  kPrivateTextureRectangle,
  kPixVideo,
  kPm4Draw,
  kSceneObject,
  kPm4Resolve,
  kClear,
};

struct RendererViewportSnapshot {
  float x = 0.0f;
  float y = 0.0f;
  float width = 1280.0f;
  float height = 720.0f;
  float min_depth = 0.0f;
  float max_depth = 1.0f;
  bool valid = false;
};

struct RendererScissorSnapshot {
  int32_t left = 0;
  int32_t top = 0;
  int32_t right = 1280;
  int32_t bottom = 720;
  bool valid = false;
};

struct RendererScalerSnapshot {
  uint32_t parameter_pointer = 0;
  uint32_t source_left = 0;
  uint32_t source_top = 0;
  uint32_t source_right = 0;
  uint32_t source_bottom = 0;
  uint32_t output_width = 0;
  uint32_t output_height = 0;
  std::array<uint32_t, 14> raw_dwords = {};
  bool valid = false;
};

struct RendererInlineTextureUploadSnapshot {
  uint32_t address = 0;
  uint32_t parent_address = 0;
  uint32_t base_address = 0;
  uint32_t byte_size = 0;
  std::array<uint32_t, 6> raw_fetch_dwords = {};
  std::array<uint32_t, 6> normalized_fetch_dwords = {};
  uint64_t upload_generation = 0;
  uint64_t upload_hash = 0;
  uint64_t serial = 0;
  std::shared_ptr<const std::vector<uint8_t>> data;

  bool ready() const {
    return address != 0 && data && !data->empty() &&
           upload_generation != 0;
  }
};

struct RendererDrawEventSnapshot {
  uint64_t serial = 0;
  uint64_t resource_serial = 0;
  RendererDrawEventSource source = RendererDrawEventSource::kUnknown;
  bool clear = false;
  uint32_t clear_flags = 0;
  float clear_color[4] = {0.0f, 0.0f, 0.0f, 1.0f};
  float clear_depth = 1.0f;
  uint32_t clear_stencil = 0;
  RendererScissorSnapshot clear_rect;
  uint64_t clear_root_call_serial = 0;
  uint64_t clear_root_context_key = 0;
  uint32_t clear_root_symbol_address = 0;
  uint32_t clear_root_lr = 0;
  uint64_t clear_target_root_call_serial[4] = {};
  uint64_t clear_target_root_context_key[4] = {};
  uint32_t clear_target_root_symbol_address[4] = {};
  uint32_t clear_target_root_lr[4] = {};
  uint64_t clear_depth_root_call_serial = 0;
  uint64_t clear_depth_root_context_key = 0;
  uint32_t clear_depth_root_symbol_address = 0;
  uint32_t clear_depth_root_lr = 0;
  RendererLastDrawSnapshot draw;
  bool draw_vertices_up = false;
  bool private_texture_rectangle = false;
  uint32_t private_texture_slot = 0;
  float private_texture_source_x = 0.0f;
  float private_texture_source_y = 0.0f;
  bool private_texture_corners_valid = false;
  std::array<float, 6> private_texture_corners = {};
  bool private_texture_uv_override = false;
  float private_texture_u0 = 0.0f;
  float private_texture_v0 = 0.0f;
  float private_texture_u1 = 1.0f;
  float private_texture_v1 = 1.0f;
  uint64_t private_texture_wrapper_call_serial = 0;
  uint64_t private_texture_wrapper_context_key = 0;
  uint32_t private_texture_wrapper_symbol_address = 0;
  uint32_t private_texture_wrapper_source_texture = 0;
  bool private_texture_wrapper_source_exact = false;
  uint32_t render_targets[4] = {};
  uint32_t depth_stencil = 0;
  uint32_t vertex_declaration = 0;
  uint32_t vertex_shader = 0;
  uint64_t vertex_shader_root_call_serial = 0;
  uint64_t vertex_shader_root_context_key = 0;
  uint32_t vertex_shader_root_symbol_address = 0;
  uint32_t vertex_shader_root_lr = 0;
  uint32_t pixel_shader = 0;
  uint64_t pixel_shader_root_call_serial = 0;
  uint64_t pixel_shader_root_context_key = 0;
  uint32_t pixel_shader_root_symbol_address = 0;
  uint32_t pixel_shader_root_lr = 0;
  uint32_t index_buffer = 0;
  uint32_t textures[kRendererD3DTextureSlots] = {};
  bool pm4_registers_valid = false;
  bool device_registers_valid = false;
  uint32_t rb_surface_info = 0;
  uint32_t rb_color_info[4] = {};
  uint32_t rb_depth_info = 0;
  uint32_t rb_copy_control = 0;
  uint32_t rb_copy_dest_base = 0;
  uint32_t rb_copy_dest_pitch = 0;
  uint32_t rb_copy_dest_info = 0;
  uint32_t rb_copy_func = 0;
  uint32_t rb_copy_ref = 0;
  uint32_t rb_copy_mask = 0;
  uint32_t rb_copy_surface_slice = 0;
  uint32_t rb_modecontrol = 4u;
  uint32_t rb_depthcontrol = 0;
  uint32_t rb_color_mask = 0xFFFFu;
  uint32_t rb_blendcontrol[4] = {0x00010001u, 0x00010001u, 0x00010001u,
                                 0x00010001u};
  uint32_t rb_colorcontrol = 0;
  uint32_t rb_alpha_ref = 0;
  uint32_t rb_stencilrefmask = 0x00FFFF00u;
  uint32_t rb_stencilrefmask_bf = 0x00FFFF00u;
  uint32_t pa_su_sc_mode_cntl = 0;
  uint8_t rb_stencil_ref = 0;
  uint8_t rb_stencil_read_mask = 0xFF;
  uint8_t rb_stencil_write_mask = 0xFF;
  std::array<uint32_t, kRendererD3DRenderStateSlots> render_states = {};
  std::array<uint32_t, kRendererD3DSamplerStateSlots> sampler_states = {};
  std::array<uint32_t, kRendererFetchConstantDwords> fetch_constants = {};
  RendererStreamBindingSnapshot streams[16] = {};
  std::array<uint32_t, kRendererShaderFloatConstantDwords>
      vertex_shader_constants = {};
  std::array<uint32_t, kRendererShaderFloatConstantDwords>
      pixel_shader_constants = {};
  std::array<uint32_t, kRendererShaderIntConstantDwords>
      vertex_shader_int_constants = {};
  std::array<uint32_t, kRendererShaderIntConstantDwords>
      pixel_shader_int_constants = {};
  uint32_t vertex_shader_int_constant_valid_mask = 0;
  uint32_t pixel_shader_int_constant_valid_mask = 0;
  std::array<uint32_t, kRendererShaderBoolConstantDwords>
      vertex_shader_bool_constants = {};
  std::array<uint32_t, kRendererShaderBoolConstantDwords>
      pixel_shader_bool_constants = {};
  uint32_t viewport_pointer = 0;
  uint32_t scissor_pointer = 0;
  RendererViewportSnapshot viewport;
  RendererScissorSnapshot scissor;
  RendererScalerSnapshot scaler;
  std::array<RendererInlineTextureUploadSnapshot, 3> pix_video_planes = {};
  uint32_t state_flush_lr = 0;
};

struct RendererSurfaceCopyAliasSnapshot {
  uint32_t source_surface = 0;
  uint32_t destination_surface = 0;
  uint32_t hook_address = 0;
  uint64_t serial = 0;
  uint64_t copy_call_serial = 0;
  uint64_t copy_context_key = 0;
  uint32_t copy_symbol_address = 0;
};

}  // namespace refii::renderer
