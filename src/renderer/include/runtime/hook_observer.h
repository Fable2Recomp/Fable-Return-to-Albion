#pragma once

#include "renderer_runtime.h"
#include "runtime/plume/direct/fable_render_types.h"
#include "runtime/plume/direct/fable_resource_snapshots.h"

#include <array>
#include <cstdint>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace refii::renderer {

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
  uint64_t scene_walker_calls = 0;
  uint64_t scene_walker_flush_calls = 0;
  uint64_t scene_walker_nonempty_calls = 0;
  uint64_t scene_walker_object_slots_total = 0;
  uint64_t scene_draw_filter_calls = 0;
  uint64_t scene_batch_traversal_calls = 0;
  uint64_t scene_batch_bridge_calls = 0;
  uint64_t scene_batch_bridge_rejected_calls = 0;
  uint64_t scene_batch_bridge_queued_draws = 0;
  uint64_t scene_inline_vertex_draws = 0;
  uint32_t last_scene_walker_list = 0;
  uint32_t last_scene_walker_begin = 0;
  uint32_t last_scene_walker_end = 0;
  uint32_t last_scene_walker_count = 0;
  uint32_t last_scene_walker_first = 0;
  uint32_t last_scene_walker_result = 0;
  uint32_t last_scene_walker_lr = 0;
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
  uint64_t get_render_target_calls = 0;
  uint32_t last_get_render_target_slot = 0;
  uint32_t last_get_render_target_surface = 0;
  uint64_t state_changes = 0;
  uint32_t render_targets[4] = {};
  uint32_t depth_stencil = 0;
  uint32_t vertex_declaration = 0;
  uint32_t vertex_shader = 0;
  uint32_t pixel_shader = 0;
  uint32_t index_buffer = 0;
  uint32_t textures[kRendererD3DTextureSlots] = {};
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
                             uint8_t* base,
                             const RendererHookArgs* exit_args = nullptr);
void ObserveRendererGuestMemoryCopy(uint32_t dst, uint32_t src, uint32_t size,
                                    uint8_t* base);
uint32_t FindFableFrameDirectorRenderableSceneIndex(
    const RendererHookArgs& args, uint8_t* base);
RendererHookObserverSnapshot GetRendererHookObserverSnapshot();
std::vector<uint32_t> GetRendererGuestWords(uint32_t guest_address,
                                            uint32_t word_count);

}  // namespace refii::renderer
