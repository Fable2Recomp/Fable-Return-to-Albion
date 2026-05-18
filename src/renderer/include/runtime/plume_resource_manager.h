#pragma once

#include "runtime/hook_observer.h"

#include <array>
#include <cstdint>
#include <cstddef>
#include <vector>

#if REFII_RENDERER_HAS_PLUME
namespace plume {
struct RenderCommandList;
struct RenderDevice;
struct RenderFramebuffer;
struct RenderTexture;
enum class RenderFormat;
enum class RenderShaderFormat;
}  // namespace plume
#endif

namespace refii::renderer {

struct PlumeDrawResourceReadiness {
  uint32_t total_draws = 0;
  uint32_t indexed_draws = 0;
  uint32_t draws_with_vertex_buffer = 0;
  uint32_t draws_with_index_buffer = 0;
  uint32_t draws_with_vertex_declaration = 0;
  uint32_t draws_with_vertex_shader = 0;
  uint32_t draws_with_pixel_shader = 0;
  uint32_t missing_vertex_buffer = 0;
  uint32_t missing_index_buffer = 0;
  uint32_t missing_vertex_declaration = 0;
  uint32_t missing_vertex_shader = 0;
  uint32_t missing_pixel_shader = 0;
};

struct PlumeDrawReplayStats {
  static constexpr size_t kPrimitiveStatsCount = 32;

  uint32_t total_draws = 0;
  uint32_t indexed_draws = 0;
  uint32_t resource_ready_draws = 0;
  uint32_t declaration_ready_draws = 0;
  uint32_t recorded_draws = 0;
  uint32_t missing_vertex_buffer = 0;
  uint32_t missing_index_buffer = 0;
  uint32_t missing_vertex_declaration = 0;
  uint32_t missing_vertex_shader = 0;
  uint32_t missing_pixel_shader = 0;
  uint32_t missing_pipeline = 0;
  uint32_t pipeline_ready_draws = 0;
  uint32_t pipeline_create_failed = 0;
  uint32_t texture_object_bindings = 0;
  uint32_t texture_fetch_bindings = 0;
  uint32_t missing_texture_bindings = 0;
  uint32_t texture_uploads_this_frame = 0;
  uint32_t decoded_texture_uploads_this_frame = 0;
  uint32_t raw_texture_uploads_this_frame = 0;
  uint32_t placeholder_texture_uploads_this_frame = 0;
  uint32_t pending_texture_uploads = 0;
  uint64_t texture_uploads_total = 0;
  uint32_t depth_bound_draws = 0;
  uint32_t depth_requested_without_surface_draws = 0;
  uint32_t z_requested_without_surface_draws = 0;
  uint32_t hiz_requested_without_surface_draws = 0;
  uint32_t depth_tracked_draws = 0;
  uint32_t depth_only_draws = 0;
  uint32_t depth_enabled_draws = 0;
  uint32_t depth_write_draws = 0;
  uint32_t depth_sample_shader_draws = 0;
  uint32_t depth_sample_source_draws = 0;
  uint32_t depth_sample_active_depth_fallback_draws = 0;
  uint32_t depth_sample_alias_draws = 0;
  uint32_t depth_sample_copy_draws = 0;
  uint32_t depth_sample_copy_slots = 0;
  uint32_t depth_sample_unavailable_draws = 0;
  uint32_t stencil_enabled_draws = 0;
  uint32_t stencil_clear_requests = 0;
  uint32_t stencil_clears = 0;
  uint32_t target_feedback_copy_draws = 0;
  uint32_t target_feedback_copy_slots = 0;
  uint32_t resolve_copy_draws = 0;
  uint32_t resolve_color_aliases = 0;
  uint32_t resolve_depth_aliases = 0;
  uint32_t resolve_texture_alias_hits = 0;
  uint32_t alpha_test_draws = 0;
  uint32_t rectangle_list_draws = 0;
  uint32_t rectangle_list_direct_draws = 0;
  uint32_t rectangle_list_indexed_draws = 0;
  uint64_t rectangle_list_vertex_or_index_count = 0;
  uint32_t quad_list_draws = 0;
  uint32_t quad_list_direct_draws = 0;
  uint32_t quad_list_indexed_draws = 0;
  uint64_t quad_list_vertex_or_index_count = 0;
  uint64_t quad_list_expanded_index_count = 0;
  uint32_t unique_render_targets = 0;
  uint32_t multi_render_target_draws = 0;
  uint32_t sparse_render_target_draws = 0;
  uint32_t max_render_targets_per_draw = 0;
  uint32_t present_target_draws = 0;
  uint32_t present_target_nonzero_slot_draws = 0;
  uint32_t offscreen_target_draws = 0;
  uint32_t target_filtered_draws = 0;
  uint32_t framebuffer_switches = 0;
  uint32_t present_target_copies = 0;
  uint32_t missing_render_targets = 0;
  uint32_t present_render_target = 0;
  uint32_t present_render_target_width = 0;
  uint32_t present_render_target_height = 0;
  uint64_t present_render_target_resource_serial = 0;
  uint32_t first_render_target = 0;
  uint32_t first_missing_vertex_declaration = 0;
  uint32_t first_missing_vertex_shader = 0;
  uint32_t first_missing_pixel_shader = 0;
  std::array<uint32_t, kPrimitiveStatsCount> primitive_draws = {};
  std::array<uint64_t, kPrimitiveStatsCount> primitive_counts = {};
};

#if REFII_RENDERER_HAS_PLUME
void SyncPlumeTrackedResources(plume::RenderDevice* device,
                               plume::RenderShaderFormat shader_format,
                               const std::vector<RendererDrawEventSnapshot>*
                                   retained_draw_events = nullptr);
void RecordPlumeResourceUploads(plume::RenderDevice* device,
                                plume::RenderCommandList* command_list);
PlumeDrawReplayStats RecordPlumeFableDraws(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    const std::vector<RendererDrawEventSnapshot>& draw_events,
    plume::RenderFramebuffer* output_framebuffer,
    plume::RenderTexture* output_texture,
    plume::RenderFramebuffer* present_framebuffer,
    plume::RenderShaderFormat shader_format,
    plume::RenderFormat output_depth_format);
PlumeDrawReplayStats RecordPlumeCachedFablePresent(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    plume::RenderTexture* output_texture,
    plume::RenderFramebuffer* present_framebuffer,
    plume::RenderShaderFormat shader_format);
#else
inline void SyncPlumeTrackedResources(void*) {}
#endif
PlumeDrawResourceReadiness AnalyzePlumeDrawResourceReadiness(
    const std::vector<RendererDrawEventSnapshot>& draw_events);
PlumeDrawReplayStats GetLastPlumeDrawReplayStats();
void ShutdownPlumeResourceManager();

}  // namespace refii::renderer
