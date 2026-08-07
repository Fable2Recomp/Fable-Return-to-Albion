#pragma once

#include "renderer_runtime.h"
#include "runtime/hook_observer.h"
#include "runtime/plume/general/plume_backend.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_render_graph_snapshot.h"
#include "runtime/refii_renderer/plume/fable/debug/fable_resource_debug_snapshot.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_frame_database_command_packets.h"

#include <array>
#include <cstdint>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

#if REFII_RENDERER_HAS_PLUME
#include <plume_render_interface.h>
#endif

namespace refii::renderer {

std::string FrameDatabaseSyntheticName(std::string_view kind, uint32_t id);
std::string ClassifyFrameDatabasePassSummary(const struct FrameDatabasePassSummary& summary);

struct RefiiRenderGraphRuntimePlanSnapshot {
  uint32_t frame_index = 0;
  uint32_t passes = 0;
  uint32_t packet_passes = 0;
  uint32_t edge_only_passes = 0;
  uint32_t omitted_passes = 0;
  uint32_t draw_packets = 0;
  uint32_t omitted_draw_packets = 0;
  uint32_t command_packets = 0;
  uint32_t omitted_command_packets = 0;
  uint32_t resource_references = 0;
  uint32_t resource_edges = 0;
  uint32_t surface_destination_edges = 0;
  uint32_t present_blit_edges = 0;
  uint32_t execution_steps = 0;
  uint32_t barriers = 0;
  uint32_t phases = 0;
};

struct RefiiRenderGraphRuntimeSchedulerSnapshot {
  uint32_t frame_index = 0;
  uint32_t executions = 0;
  uint32_t steps = 0;
  uint32_t bridge_steps = 0;
  uint32_t graph_owned_steps = 0;
  uint32_t graph_owned_plan_steps = 0;
  uint32_t graph_owned_render_steps = 0;
  uint32_t graph_owned_clear_steps = 0;
  uint32_t graph_owned_clear_commands = 0;
  uint32_t graph_owned_draw_steps = 0;
  uint32_t graph_owned_draw_commands = 0;
  uint32_t graph_owned_resolve_steps = 0;
  uint32_t graph_owned_resolve_commands = 0;
  uint32_t bridge_commands = 0;
};

struct FrameDatabaseResourceNameIndex {
  std::unordered_map<uint32_t, std::string> names;
  std::unordered_map<uint32_t, std::string> kinds;
  std::unordered_map<uint32_t, uint64_t> content_hashes;

  void Add(uint32_t id, std::string_view kind, std::string_view name,
           uint64_t content_hash) {
    if (id == 0) {
      return;
    }
    kinds.try_emplace(id, std::string(kind));
    if (content_hash != 0) {
      content_hashes.try_emplace(id, content_hash);
    }
    if (!name.empty()) {
      names.try_emplace(id, std::string(name));
    }
  }

  std::string NameFor(uint32_t id, std::string_view default_role) const {
    if (id == 0) {
      return "none";
    }
    const auto found = names.find(id);
    if (found != names.end()) {
      return found->second;
    }
    const auto kind = kinds.find(id);
    if (kind != kinds.end()) {
      return FrameDatabaseSyntheticName(kind->second, id);
    }
    return FrameDatabaseSyntheticName(default_role, id);
  }

  std::string KindFor(uint32_t id, std::string_view default_kind) const {
    if (id == 0) {
      return "none";
    }
    const auto found = kinds.find(id);
    if (found != kinds.end()) {
      return found->second;
    }
    return std::string(default_kind);
  }

  uint64_t ContentHashFor(uint32_t id) const {
    const auto found = content_hashes.find(id);
    return found != content_hashes.end() ? found->second : 0ull;
  }
};

enum class FrameDatabaseResourceRole : uint8_t {
  kVertexShader,
  kPixelShader,
  kVertexDeclaration,
  kRenderTarget,
  kDepthStencil,
  kTexture,
  kVertexBuffer,
  kIndexBuffer,
};

struct FrameDatabaseResourceEvent {
  uint32_t resource_id = 0;
  uint64_t first_packet_id = 0;
  uint32_t reference_count = 0;
  bool vertex_shader = false;
  bool pixel_shader = false;
  bool vertex_declaration = false;
  bool render_target = false;
  bool depth_stencil = false;
  bool texture = false;
  bool vertex_buffer = false;
  bool index_buffer = false;
};

struct FrameDatabaseResourceEdge {
  std::string kind;
  std::string pass_name;
  uint64_t packet_id = 0;
  uint64_t command_serial = 0;
  FableDirectFrameDatabaseRoot root;
  std::array<uint32_t, 4> source_render_targets = {};
  uint32_t source_depth_stencil = 0;
  uint32_t destination_base = 0;
  uint32_t destination_pitch = 0;
  uint32_t destination_info = 0;
  uint32_t destination_slice = 0;
  uint64_t relation_serial = 0;
  uint32_t source_surface = 0;
  uint32_t destination_texture = 0;
  uint32_t source_slot = 0xFFFFFFFFu;
  uint32_t source_width = 0;
  uint32_t source_height = 0;
  uint32_t destination_width = 0;
  uint32_t destination_height = 0;
  uint32_t texture_base_address = 0;
  uint32_t backing_texture = 0;
  uint32_t backing_base_address = 0;
  uint64_t backing_content_serial = 0;
  uint64_t target_content_serial = 0;
  bool scaled_copy = false;
  bool trusted_destination_edge = false;
  bool wrapper_source_exact = false;
};

struct FrameDatabasePassSummary {
  std::string pass_name;
  std::string pass_class;
  uint64_t first_packet_id = 0;
  uint64_t first_command_packet_id = 0;
  uint32_t command_packets = 0;
  uint32_t clear_packets = 0;
  uint32_t resolve_packets = 0;
  uint32_t resource_edges = 0;
  uint32_t clear_resource_edges = 0;
  uint32_t resolve_resource_edges = 0;
  uint32_t surface_destination_edges = 0;
  uint32_t present_blit_edges = 0;
  uint32_t draw_packets = 0;
  uint32_t indexed_draw_packets = 0;
  uint32_t draw_rooted_packets = 0;
  uint32_t vertex_shader_rooted_packets = 0;
  uint32_t pixel_shader_rooted_packets = 0;
  uint32_t fully_shader_rooted_packets = 0;
  uint32_t private_texture_wrapper_rooted_packets = 0;
  uint32_t any_rooted_packets = 0;
};

enum class FrameDatabasePassResourceRole : uint8_t {
  kVertexShader,
  kPixelShader,
  kVertexDeclaration,
  kRenderTarget,
  kDepthStencil,
  kTexture,
  kVertexBuffer,
  kIndexBuffer,
  kClearTarget,
  kResolveSource,
  kResolveDestination,
  kSurfaceSource,
  kSurfaceDestination,
  kPresentSource,
  kPresentOutput,
};

struct FrameDatabasePassResourceUsage {
  std::string pass_name;
  uint32_t resource_id = 0;
  uint32_t read_count = 0;
  uint32_t write_count = 0;
  bool vertex_shader = false;
  bool pixel_shader = false;
  bool vertex_declaration = false;
  bool render_target = false;
  bool depth_stencil = false;
  bool texture = false;
  bool vertex_buffer = false;
  bool index_buffer = false;
  bool clear_target = false;
  bool resolve_source = false;
  bool resolve_destination = false;
  bool surface_source = false;
  bool surface_destination = false;
  bool present_source = false;
  bool present_output = false;
};

struct FrameDatabasePassDependency {
  std::string producer_pass;
  std::string consumer_pass;
  uint32_t resource_id = 0;
  std::string hazard;
  uint32_t producer_write_count = 0;
  uint32_t consumer_read_count = 0;
  uint32_t consumer_write_count = 0;
};

struct FrameDatabasePassResourceTransition {
  std::string before_pass;
  std::string after_pass;
  uint32_t resource_id = 0;
  std::string before_access;
  std::string after_access;
  std::string hazard;
};

struct FrameDatabaseFrameResourceLifetime {
  uint32_t resource_id = 0;
  uint32_t first_pass_index = 0;
  uint32_t last_pass_index = 0;
  std::string first_pass;
  std::string last_pass;
  uint32_t read_count = 0;
  uint32_t write_count = 0;
  bool vertex_shader = false;
  bool pixel_shader = false;
  bool vertex_declaration = false;
  bool render_target = false;
  bool depth_stencil = false;
  bool texture = false;
  bool vertex_buffer = false;
  bool index_buffer = false;
  bool clear_target = false;
  bool resolve_source = false;
  bool resolve_destination = false;
  bool surface_source = false;
  bool surface_destination = false;
  bool present_source = false;
  bool present_output = false;
};

struct FrameDatabaseMaterialRecord {
  uint64_t material_id = 0;
  uint64_t first_packet_id = 0;
  uint32_t draw_count = 0;
  std::string first_pass;
  std::string last_pass;
  std::string semantic_class;
  uint32_t pixel_shader = 0;
  std::array<uint32_t, 16> textures = {};
  std::array<uint32_t, 16> sampler_states = {};
  std::array<uint32_t, 4> render_targets = {};
  uint32_t depth_stencil = 0;
};

struct FrameDatabaseMeshRecord {
  uint64_t mesh_id = 0;
  uint64_t first_packet_id = 0;
  uint32_t draw_count = 0;
  std::string first_pass;
  std::string last_pass;
  std::string semantic_class;
  uint32_t vertex_declaration = 0;
  std::array<uint32_t, 16> vertex_buffers = {};
  std::array<uint32_t, 16> vertex_strides = {};
  std::array<uint32_t, 16> vertex_offsets = {};
  uint32_t index_buffer = 0;
  uint32_t index_format = 0;
  bool indexed = false;
  uint32_t primitive_type = 0;
  uint32_t start = 0;
  uint32_t vertex_or_index_count = 0;
  int32_t base_vertex = 0;
};

struct FrameDatabaseAssetUsageRecord {
  std::string kind;
  uint32_t resource_id = 0;
  uint32_t slot = 0xFFFFFFFFu;
  uint64_t packet_id = 0;
  std::string pass_name;
  std::string semantic_class;
  uint64_t material_id = 0;
  uint64_t mesh_id = 0;
};

struct FrameDatabaseRenderGraphNode {
  uint32_t pass_index = 0;
  std::string pass_name;
  std::string pass_class;
  std::string phase_name;
  uint32_t draw_packets = 0;
  uint32_t command_packets = 0;
  uint32_t resource_usages = 0;
  uint32_t read_resources = 0;
  uint32_t write_resources = 0;
  uint32_t material_usages = 0;
  uint32_t mesh_usages = 0;
  uint32_t asset_usages = 0;
};

struct FrameDatabaseRenderGraphEdge {
  uint32_t edge_index = 0;
  std::string producer_pass;
  std::string consumer_pass;
  uint32_t resource_id = 0;
  std::string hazard;
};

struct FrameDatabaseRenderGraphExecutionStep {
  uint32_t step_index = 0;
  uint32_t pass_index = 0;
  std::string pass_name;
  std::string pass_class;
  uint32_t wait_edges = 0;
  uint32_t signal_edges = 0;
  uint32_t barriers_before = 0;
  uint32_t barriers_after = 0;
  bool execute_draws = false;
  bool execute_commands = false;
  bool reads_resources = false;
  bool writes_resources = false;
};

struct FrameDatabaseRenderGraphPhase {
  uint32_t phase_index = 0;
  std::string phase_name;
  uint32_t first_pass_index = 0;
  uint32_t last_pass_index = 0;
  uint32_t pass_count = 0;
  uint32_t draw_packets = 0;
  uint32_t command_packets = 0;
  uint32_t resource_usages = 0;
  uint32_t read_resources = 0;
  uint32_t write_resources = 0;
  uint32_t material_usages = 0;
  uint32_t mesh_usages = 0;
  uint32_t asset_usages = 0;
  uint32_t wait_edges = 0;
  uint32_t signal_edges = 0;
  uint32_t barriers_before = 0;
  uint32_t barriers_after = 0;
};

struct FrameDatabaseResourceEdgeCounts {
  uint32_t total_edges = 0;
  uint32_t clear_edges = 0;
  uint32_t resolve_edges = 0;
  uint32_t surface_destination_edges = 0;
  uint32_t present_blit_edges = 0;
};

std::vector<FrameDatabaseAssetUsageRecord> BuildFrameDatabaseAssetUsageRecords(
    const FableDirectFrameDatabaseDrawPackets& draw_packets,
    const FrameDatabaseResourceNameIndex& name_index);

std::vector<FrameDatabaseMaterialRecord> BuildFrameDatabaseMaterialRecords(
    const FableDirectFrameDatabaseDrawPackets& draw_packets,
    const FrameDatabaseResourceNameIndex& name_index);

std::vector<FrameDatabaseMeshRecord> BuildFrameDatabaseMeshRecords(
    const FableDirectFrameDatabaseDrawPackets& draw_packets,
    const FrameDatabaseResourceNameIndex& name_index);

std::vector<FrameDatabasePassDependency> BuildFrameDatabasePassDependencies(
    const std::vector<FrameDatabasePassSummary>& pass_summaries,
    const std::vector<FrameDatabasePassResourceUsage>& pass_resource_usages);

std::vector<FrameDatabasePassResourceUsage> BuildFrameDatabasePassResourceUsages(
    const FableDirectFrameDatabaseDrawPackets& draw_packets,
    const std::vector<FrameDatabaseResourceEdge>& resource_edges);

std::vector<FrameDatabasePassSummary> BuildFrameDatabasePassSummaries(
    const FableDirectFrameDatabaseDrawPackets& draw_packets,
    const FableDirectFrameDatabaseCommandPackets& command_packets,
    const std::vector<FrameDatabaseResourceEdge>& resource_edges);

std::vector<FrameDatabaseRenderGraphEdge> BuildFrameDatabaseRenderGraphEdges(
    const std::vector<FrameDatabasePassDependency>& pass_dependencies);

std::vector<FrameDatabaseRenderGraphNode> BuildFrameDatabaseRenderGraphNodes(
    const std::vector<FrameDatabasePassSummary>& pass_summaries,
    const std::vector<FrameDatabasePassResourceUsage>& pass_resource_usages,
    const std::vector<FrameDatabaseAssetUsageRecord>& asset_usage_records);

std::vector<FrameDatabaseRenderGraphPhase> BuildFrameDatabaseRenderGraphPhases(
    const std::vector<FrameDatabaseRenderGraphNode>& nodes,
    const std::vector<FrameDatabaseRenderGraphExecutionStep>& execution_plan);

std::vector<FrameDatabaseResourceEdge> BuildFrameDatabaseResourceEdges(
    const FableDirectFrameDatabaseCommandPackets& command_packets);

FrameDatabaseResourceNameIndex BuildFrameDatabaseResourceNameIndex(
    const FableDirectResourceDebugSnapshot& snapshot);

uint32_t CountFrameDatabaseClassifiedPassSummaries(
    const std::vector<FrameDatabasePassSummary>& summaries);

uint32_t CountFrameDatabaseCommandKind(
    const FableDirectFrameDatabaseCommandPackets& command_packets,
    std::string_view kind);

uint32_t CountFrameDatabaseExecutableCommandPackets(
    const FableDirectFrameDatabaseCommandPackets& command_packets);

FrameDatabaseResourceEdgeCounts CountFrameDatabaseResourceEdges(
    const std::vector<FrameDatabaseResourceEdge>& edges);

uint64_t FrameDatabasePacketBytesEstimate(
    const FableDirectRendererStats& direct_stats);

void PlotFrameDatabasePassSummaries(
    const std::vector<FrameDatabasePassSummary>& summaries);

void PlotFrameDatabaseRootCoverage(
    const FableDirectFrameDatabaseDrawPackets& draw_packets);

std::string WriteRenderDocFrameDatabaseDump(
    uint32_t frame_index, uint32_t width, uint32_t height,
    const FableDirectRendererStats& direct_stats,
    const RendererHookObserverSnapshot& hook_snapshot,
    const FableDirectStateSnapshot& direct_snapshot,
    std::string_view correlation_label,
    uint32_t capture_draw_index,
    std::string_view capture_draw_target,
    std::string_view capture_pass,
    std::string_view capture_reason,
    bool renderdoc_refii_owned_capture,
    bool renderdoc_record_time_capture);

std::string WriteRenderDocHookTraceDump(
    uint32_t frame_index, uint32_t width, uint32_t height,
    std::string_view correlation_label,
    uint32_t capture_draw_index,
    std::string_view capture_draw_target,
    std::string_view capture_pass,
    std::string_view capture_reason,
    bool renderdoc_refii_owned_capture,
    bool renderdoc_record_time_capture);

std::string BuildRenderDocCaptureComments(
    uint32_t frame_index, uint32_t width, uint32_t height,
    const FableDirectRendererStats& direct_stats,
    const RendererHookObserverSnapshot& hook_snapshot,
    const RendererHookTimingSnapshot& timing_snapshot,
    const FableDirectStateSnapshot& direct_snapshot,
    std::string_view frame_database_path,
    std::string_view hook_trace_path,
    std::string_view correlation_label,
    uint32_t capture_draw_index,
    std::string_view capture_draw_target,
    std::string_view capture_pass,
    std::string_view capture_reason);

RefiiRenderGraphRuntimePlanSnapshot BuildRefiiRenderGraphRuntimePlanSnapshot(
    uint32_t frame_index,
    const FableDirectFrameDatabaseDrawPackets& draw_packets,
    const FableDirectFrameDatabaseCommandPackets& command_packets,
    const std::vector<FrameDatabasePassSummary>& pass_summaries,
    const std::vector<FrameDatabaseResourceEdge>& resource_edges,
    const std::vector<FrameDatabasePassResourceUsage>& pass_resource_usages,
    const std::vector<FrameDatabaseRenderGraphNode>& render_graph_nodes,
    const std::vector<FrameDatabaseRenderGraphExecutionStep>&
        render_graph_execution_steps,
    const std::vector<FrameDatabaseRenderGraphPhase>& render_graph_phases,
    const std::vector<FrameDatabasePassResourceTransition>&
        pass_resource_transitions);

std::vector<FrameDatabaseFrameResourceLifetime>
BuildFrameDatabaseFrameResourceLifetimes(
    const std::vector<FrameDatabasePassSummary>& pass_summaries,
    const std::vector<FrameDatabasePassResourceUsage>& pass_resource_usages);

std::vector<FrameDatabasePassResourceTransition>
BuildFrameDatabasePassResourceTransitions(
    const std::vector<FrameDatabasePassDependency>& dependencies);

std::vector<FrameDatabaseRenderGraphExecutionStep>
BuildFrameDatabaseRenderGraphExecutionPlan(
    const std::vector<FrameDatabaseRenderGraphNode>& nodes,
    const std::vector<FrameDatabaseRenderGraphEdge>& edges,
    const std::vector<FrameDatabasePassResourceTransition>& transitions);

std::string SanitizeRenderDocTagComponent(std::string_view value);

void StoreRefiiRenderGraphRuntimeSchedulerSnapshot(
    const RefiiRenderGraphRuntimeSchedulerSnapshot& snapshot);

RefiiRenderGraphRuntimeSchedulerSnapshot
GetRefiiRenderGraphRuntimeSchedulerSnapshot();

void StoreRefiiRenderGraphRuntimePlanSnapshot(
    const RefiiRenderGraphRuntimePlanSnapshot& snapshot);

void StoreRefiiRenderGraphRuntimePlanSnapshot(
    const FableDirectRenderGraphSnapshot& snapshot);

#if REFII_RENDERER_HAS_PLUME
FableDirectRendererStats ExecuteRefiiRenderGraphFrame(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    plume::RenderFramebuffer* output_framebuffer,
    plume::RenderTexture* output_texture,
    plume::RenderFramebuffer* present_framebuffer,
    plume::RenderShaderFormat shader_format,
    plume::RenderFormat output_depth_format,
    uint32_t frame_index,
    uint32_t width,
    uint32_t height);
#endif

}  // namespace refii::renderer
