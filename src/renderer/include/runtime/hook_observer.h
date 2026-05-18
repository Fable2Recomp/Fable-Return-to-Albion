#pragma once

#include "renderer_runtime.h"

#include <array>
#include <cstdint>
#include <unordered_map>
#include <vector>

namespace refii::renderer {

constexpr uint32_t kRendererShaderFloatConstantDwords = 256u * 4u;
constexpr uint32_t kRendererShaderIntConstantDwords = 32u * 4u;
constexpr uint32_t kRendererShaderBoolConstantDwords = 8u;
constexpr uint32_t kRendererFetchConstantDwords = 192u;
constexpr uint32_t kRendererD3DRenderStateSlots = 32u;
constexpr uint32_t kRendererD3DSamplerStateSlots = 32u;
constexpr uint32_t kRendererSurfaceCopyAliasSlots = 64u;

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

enum class RendererTrackedResourceKind {
  kUnknown,
  kTexture,
  kSurface,
  kDepthSurface,
  kVertexBuffer,
  kIndexBuffer,
  kVertexDeclaration,
  kVertexShader,
  kPixelShader,
};

struct RendererTrackedResourceSnapshot {
  RendererTrackedResourceKind kind = RendererTrackedResourceKind::kUnknown;
  uint32_t guest_address = 0;
  uint32_t parent_address = 0;
  uint32_t size = 0;
  uint32_t stride_or_format = 0;
  uint32_t buffer_object = 0;
  uint32_t raw_flags = 0;
  uint32_t raw_packed28 = 0;
  uint32_t raw_packed32 = 0;
  uint32_t raw_packed36 = 0;
  uint32_t raw_packed40 = 0;
  uint32_t raw_packed44 = 0;
  uint32_t raw_packed48 = 0;
  std::array<uint32_t, 6> normalized_fetch_dwords = {};
  uint64_t guest_bytecode_hash = 0;
  uint64_t upload_generation = 0;
  std::vector<uint8_t> upload_data;
  uint64_t serial = 0;
  bool scratch = false;
};

struct RendererTrackedResourceUploadCacheEntry {
  RendererTrackedResourceKind kind = RendererTrackedResourceKind::kUnknown;
  uint64_t upload_generation = 0;
  uint64_t serial = 0;
};

struct RendererTrackedResourceVersion {
  uint64_t serial = 0;
  uint64_t resource_creates = 0;
  uint64_t resource_destroys = 0;
  uint64_t resource_unlocks = 0;
};

struct RendererDrawEventSnapshot {
  uint64_t serial = 0;
  uint64_t resource_serial = 0;
  bool clear = false;
  uint32_t clear_flags = 0;
  float clear_color[4] = {0.0f, 0.0f, 0.0f, 1.0f};
  float clear_depth = 1.0f;
  uint32_t clear_stencil = 0;
  RendererScissorSnapshot clear_rect;
  RendererLastDrawSnapshot draw;
  bool draw_vertices_up = false;
  bool private_texture_rectangle = false;
  uint32_t private_texture_slot = 0;
  uint32_t private_texture_source_x = 0;
  uint32_t private_texture_source_y = 0;
  bool private_texture_corners_valid = false;
  std::array<float, 6> private_texture_corners = {};
  bool private_texture_uv_override = false;
  float private_texture_u0 = 0.0f;
  float private_texture_v0 = 0.0f;
  float private_texture_u1 = 1.0f;
  float private_texture_v1 = 1.0f;
  uint32_t render_targets[4] = {};
  uint32_t depth_stencil = 0;
  uint32_t vertex_declaration = 0;
  uint32_t vertex_shader = 0;
  uint32_t pixel_shader = 0;
  uint32_t index_buffer = 0;
  uint32_t textures[16] = {};
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
  uint32_t state_flush_lr = 0;
};

struct RendererSurfaceCopyAliasSnapshot {
  uint32_t source_surface = 0;
  uint32_t destination_surface = 0;
  uint32_t hook_address = 0;
  uint64_t serial = 0;
};

struct RendererHookObserverSnapshot {
  uint64_t live_resources = 0;
  uint64_t live_textures = 0;
  uint64_t live_surfaces = 0;
  uint64_t live_vertex_buffers = 0;
  uint64_t live_index_buffers = 0;
  uint64_t live_shaders = 0;
  uint64_t resource_creates = 0;
  uint64_t resource_destroys = 0;
  uint64_t resource_locks = 0;
  uint64_t resource_unlocks = 0;
  uint64_t draws = 0;
  uint64_t indexed_draws = 0;
  uint64_t pending_draw_events = 0;
  uint64_t dropped_draw_events = 0;
  uint64_t render_passes = 0;
  uint64_t state_flushes = 0;
  uint64_t state_dispatches = 0;
  uint64_t state_dispatch_targets = 0;
  uint64_t render_queue_callbacks = 0;
  uint64_t render_queue_callback_targets = 0;
  uint64_t command_writes = 0;
  uint64_t command_submits = 0;
  uint64_t command_words = 0;
  uint64_t pm4_type0_packets = 0;
  uint64_t pm4_type3_packets = 0;
  uint64_t pm4_indirect_packets = 0;
  uint64_t pm4_draw_packets = 0;
  uint64_t pm4_swap_packets = 0;
  uint64_t pm4_constant_packets = 0;
  uint64_t pm4_parse_errors = 0;
  uint64_t shader_constant_uploads = 0;
  uint64_t fetch_constant_uploads = 0;
  uint64_t shader_constant_upload_nonzero_dwords = 0;
  uint64_t shader_constant_zero_uploads = 0;
  uint64_t shader_constant_skipped_uploads = 0;
  uint64_t pix_decode_frames = 0;
  uint64_t pix_decode_pending_frames = 0;
  uint64_t pix_render_hooks = 0;
  uint64_t pix_render_events = 0;
  uint64_t pix_render_uv_overrides = 0;
  uint64_t pix_capture_hooks = 0;
  uint64_t pix_render_bad_packets = 0;
  uint64_t pix_render_bad_vertices = 0;
  uint32_t last_pix_texture = 0;
  uint32_t last_pix_packet = 0;
  uint32_t last_pix_decode_base = 0;
  uint32_t last_pix_decode_width = 0;
  uint32_t last_pix_decode_height = 0;
  uint32_t last_pix_fetch_base = 0;
  uint32_t last_pix_fetch_size = 0;
  uint32_t last_pix_fetch_format = 0;
  float last_pix_output_width = 0.0f;
  float last_pix_output_height = 0.0f;
  float last_pix_decoded_width = 0.0f;
  float last_pix_decoded_height = 0.0f;
  float last_pix_u1 = 0.0f;
  float last_pix_v1 = 0.0f;
  uint32_t vertex_shader_constant_nonzero_dwords = 0;
  uint32_t pixel_shader_constant_nonzero_dwords = 0;
  uint32_t last_shader_constant_bank = 0;
  uint32_t last_shader_constant_first_register = 0;
  uint32_t last_shader_constant_first_component = 0;
  uint32_t last_shader_constant_dwords = 0;
  uint32_t last_shader_constant_nonzero_dwords = 0;
  uint32_t last_shader_constant_source = 0;
  uint32_t last_shader_constant_lr = 0;
  uint32_t last_shader_constant_preview[4] = {};
  uint32_t fetch_constant_nonzero_dwords = 0;
  uint64_t clears = 0;
  uint64_t presents = 0;
  uint32_t display_present_texture_base = 0;
  uint32_t display_present_width = 0;
  uint32_t display_present_height = 0;
  uint32_t display_present_format = 0;
  uint32_t display_present_surface_object = 0;
  uint32_t display_present_source = 0;
  uint32_t display_present_register = 0;
  uint32_t display_present_register_value = 0;
  std::array<uint32_t, 6> display_present_fetch_dwords = {};
  uint32_t surface_copy_alias_count = 0;
  std::array<RendererSurfaceCopyAliasSnapshot, kRendererSurfaceCopyAliasSlots>
      surface_copy_aliases = {};
  uint64_t state_changes = 0;
  uint32_t render_targets[4] = {};
  uint32_t depth_stencil = 0;
  uint32_t vertex_declaration = 0;
  uint32_t vertex_shader = 0;
  uint32_t pixel_shader = 0;
  uint32_t index_buffer = 0;
  uint32_t textures[16] = {};
  std::array<uint32_t, kRendererD3DRenderStateSlots> render_states = {};
  std::array<uint32_t, kRendererD3DSamplerStateSlots> sampler_states = {};
  RendererStreamBindingSnapshot streams[16] = {};
  RendererLastDrawSnapshot last_draw;
  uint32_t viewport_pointer = 0;
  uint32_t scissor_pointer = 0;
  RendererViewportSnapshot viewport;
  RendererScissorSnapshot scissor;
  RendererScalerSnapshot scaler;
  uint32_t clear_flags = 0;
  uint32_t last_state_flush_lr = 0;
  uint32_t last_resource_address = 0;
  uint32_t last_resource_size = 0;
  uint32_t last_resource_stride_or_format = 0;
  uint64_t last_shader_guest_hash = 0;
  uint32_t last_command_source = 0;
  uint32_t last_command_pointer = 0;
  uint32_t last_command_word_count = 0;
  uint32_t last_command_first_word = 0;
  uint32_t last_command_packet_type = 0;
  uint32_t last_command_type3_opcode = 0;
  uint32_t last_command_preview[8] = {};
};

void ObserveRendererHookEnter(const char* symbol, RendererHookKind kind,
                              const RendererHookArgs& args,
                              uint8_t* base = nullptr);
void ObserveRendererStateDispatch(const RendererHookArgs& args, uint8_t* base);
void ObserveRendererPassQueue(const RendererHookArgs& args, uint8_t* base);
void ObserveRendererHookExit(const char* symbol, RendererHookKind kind,
                             const RendererHookArgs& args, uint32_t result,
                             uint8_t* base);
RendererHookObserverSnapshot GetRendererHookObserverSnapshot();
std::vector<RendererTrackedResourceSnapshot>
GetRendererTrackedResourceSnapshots(
    const std::unordered_map<uint32_t,
                             RendererTrackedResourceUploadCacheEntry>*
        known_resources = nullptr);
RendererTrackedResourceVersion GetRendererTrackedResourceVersion();
std::vector<uint32_t> GetRendererGuestWords(uint32_t guest_address,
                                            uint32_t word_count);
std::vector<RendererDrawEventSnapshot> ConsumeRendererDrawEvents();
std::vector<RendererDrawEventSnapshot> ConsumeRendererPresentedDrawEvents();

}  // namespace refii::renderer
