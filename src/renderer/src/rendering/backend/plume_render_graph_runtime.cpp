#include "runtime/refii_renderer/plume/general/plume_frame_database.h"
#include "runtime/refii_renderer/plume/general/plume_debug_labels.h"

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_render_graph_snapshot.h"

#include <atomic>
#include <algorithm>
#include <cstdint>
#include <vector>

#include <tracy/Tracy.hpp>

namespace refii::renderer {

std::atomic<uint32_t> g_refii_render_graph_runtime_plan_frame{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_passes{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_packet_passes{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_edge_only_passes{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_omitted_passes{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_draw_packets{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_omitted_draw_packets{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_command_packets{0};
std::atomic<uint32_t>
    g_refii_render_graph_runtime_plan_omitted_command_packets{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_resource_references{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_resource_edges{0};
std::atomic<uint32_t>
    g_refii_render_graph_runtime_plan_surface_destination_edges{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_execution_steps{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_barriers{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_phases{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_frame{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_executions{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_steps{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_bridge_steps{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_graph_owned_steps{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_graph_owned_plan_steps{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_graph_owned_render_steps{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_graph_owned_clear_steps{0};
std::atomic<uint32_t>
    g_refii_render_graph_scheduler_graph_owned_clear_commands{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_graph_owned_draw_steps{0};
std::atomic<uint32_t>
    g_refii_render_graph_scheduler_graph_owned_draw_commands{0};
std::atomic<uint32_t>
    g_refii_render_graph_scheduler_graph_owned_resolve_steps{0};
std::atomic<uint32_t>
    g_refii_render_graph_scheduler_graph_owned_resolve_commands{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_bridge_commands{0};

void StoreRefiiRenderGraphRuntimeSchedulerSnapshot(
    const RefiiRenderGraphRuntimeSchedulerSnapshot& snapshot) {
  g_refii_render_graph_scheduler_frame.store(snapshot.frame_index,
                                            std::memory_order_release);
  g_refii_render_graph_scheduler_executions.store(snapshot.executions,
                                                 std::memory_order_release);
  g_refii_render_graph_scheduler_steps.store(snapshot.steps,
                                            std::memory_order_release);
  g_refii_render_graph_scheduler_bridge_steps.store(
      snapshot.bridge_steps, std::memory_order_release);
  g_refii_render_graph_scheduler_graph_owned_steps.store(
      snapshot.graph_owned_steps, std::memory_order_release);
  g_refii_render_graph_scheduler_graph_owned_plan_steps.store(
      snapshot.graph_owned_plan_steps, std::memory_order_release);
  g_refii_render_graph_scheduler_graph_owned_render_steps.store(
      snapshot.graph_owned_render_steps, std::memory_order_release);
  g_refii_render_graph_scheduler_graph_owned_clear_steps.store(
      snapshot.graph_owned_clear_steps, std::memory_order_release);
  g_refii_render_graph_scheduler_graph_owned_clear_commands.store(
      snapshot.graph_owned_clear_commands, std::memory_order_release);
  g_refii_render_graph_scheduler_graph_owned_draw_steps.store(
      snapshot.graph_owned_draw_steps, std::memory_order_release);
  g_refii_render_graph_scheduler_graph_owned_draw_commands.store(
      snapshot.graph_owned_draw_commands, std::memory_order_release);
  g_refii_render_graph_scheduler_graph_owned_resolve_steps.store(
      snapshot.graph_owned_resolve_steps, std::memory_order_release);
  g_refii_render_graph_scheduler_graph_owned_resolve_commands.store(
      snapshot.graph_owned_resolve_commands, std::memory_order_release);
  g_refii_render_graph_scheduler_bridge_commands.store(
      snapshot.bridge_commands, std::memory_order_release);
}

RefiiRenderGraphRuntimeSchedulerSnapshot
GetRefiiRenderGraphRuntimeSchedulerSnapshot() {
  RefiiRenderGraphRuntimeSchedulerSnapshot snapshot;
  snapshot.frame_index =
      g_refii_render_graph_scheduler_frame.load(std::memory_order_acquire);
  snapshot.executions = g_refii_render_graph_scheduler_executions.load(
      std::memory_order_acquire);
  snapshot.steps =
      g_refii_render_graph_scheduler_steps.load(std::memory_order_acquire);
  snapshot.bridge_steps = g_refii_render_graph_scheduler_bridge_steps.load(
      std::memory_order_acquire);
  snapshot.graph_owned_steps =
      g_refii_render_graph_scheduler_graph_owned_steps.load(
          std::memory_order_acquire);
  snapshot.graph_owned_plan_steps =
      g_refii_render_graph_scheduler_graph_owned_plan_steps.load(
          std::memory_order_acquire);
  snapshot.graph_owned_render_steps =
      g_refii_render_graph_scheduler_graph_owned_render_steps.load(
          std::memory_order_acquire);
  snapshot.graph_owned_clear_steps =
      g_refii_render_graph_scheduler_graph_owned_clear_steps.load(
          std::memory_order_acquire);
  snapshot.graph_owned_clear_commands =
      g_refii_render_graph_scheduler_graph_owned_clear_commands.load(
          std::memory_order_acquire);
  snapshot.graph_owned_draw_steps =
      g_refii_render_graph_scheduler_graph_owned_draw_steps.load(
          std::memory_order_acquire);
  snapshot.graph_owned_draw_commands =
      g_refii_render_graph_scheduler_graph_owned_draw_commands.load(
          std::memory_order_acquire);
  snapshot.graph_owned_resolve_steps =
      g_refii_render_graph_scheduler_graph_owned_resolve_steps.load(
          std::memory_order_acquire);
  snapshot.graph_owned_resolve_commands =
      g_refii_render_graph_scheduler_graph_owned_resolve_commands.load(
          std::memory_order_acquire);
  snapshot.bridge_commands = g_refii_render_graph_scheduler_bridge_commands.load(
      std::memory_order_acquire);
  return snapshot;
}

void StoreRefiiRenderGraphRuntimePlanSnapshot(
    const RefiiRenderGraphRuntimePlanSnapshot& snapshot) {
  g_refii_render_graph_runtime_plan_frame.store(snapshot.frame_index,
                                               std::memory_order_release);
  g_refii_render_graph_runtime_plan_passes.store(snapshot.passes,
                                                std::memory_order_release);
  g_refii_render_graph_runtime_plan_packet_passes.store(
      snapshot.packet_passes, std::memory_order_release);
  g_refii_render_graph_runtime_plan_edge_only_passes.store(
      snapshot.edge_only_passes, std::memory_order_release);
  g_refii_render_graph_runtime_plan_omitted_passes.store(
      snapshot.omitted_passes, std::memory_order_release);
  g_refii_render_graph_runtime_plan_draw_packets.store(
      snapshot.draw_packets, std::memory_order_release);
  g_refii_render_graph_runtime_plan_omitted_draw_packets.store(
      snapshot.omitted_draw_packets, std::memory_order_release);
  g_refii_render_graph_runtime_plan_command_packets.store(
      snapshot.command_packets, std::memory_order_release);
  g_refii_render_graph_runtime_plan_omitted_command_packets.store(
      snapshot.omitted_command_packets, std::memory_order_release);
  g_refii_render_graph_runtime_plan_resource_references.store(
      snapshot.resource_references, std::memory_order_release);
  g_refii_render_graph_runtime_plan_resource_edges.store(
      snapshot.resource_edges, std::memory_order_release);
  g_refii_render_graph_runtime_plan_surface_destination_edges.store(
      snapshot.surface_destination_edges, std::memory_order_release);
  g_refii_render_graph_runtime_plan_execution_steps.store(
      snapshot.execution_steps, std::memory_order_release);
  g_refii_render_graph_runtime_plan_barriers.store(snapshot.barriers,
                                                  std::memory_order_release);
  g_refii_render_graph_runtime_plan_phases.store(snapshot.phases,
                                                std::memory_order_release);
}

void StoreRefiiRenderGraphRuntimePlanSnapshot(
    const FableDirectRenderGraphSnapshot& snapshot) {
  RefiiRenderGraphRuntimePlanSnapshot runtime_plan;
  runtime_plan.frame_index = snapshot.frame_index;
  runtime_plan.passes = snapshot.pass_count;
  runtime_plan.packet_passes = snapshot.pass_count;
  runtime_plan.omitted_passes = snapshot.omitted_passes;
  runtime_plan.draw_packets = snapshot.draw_packet_count;
  runtime_plan.omitted_draw_packets = snapshot.omitted_draw_packets;
  runtime_plan.command_packets = snapshot.command_packet_count;
  runtime_plan.omitted_command_packets = snapshot.omitted_command_packets;
  runtime_plan.resource_references = snapshot.resource_reference_count;
  runtime_plan.execution_steps = snapshot.pass_count;
  StoreRefiiRenderGraphRuntimePlanSnapshot(runtime_plan);
}

FableDirectRendererStats ExecuteRefiiRenderGraphFrame(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    plume::RenderFramebuffer* output_framebuffer,
    plume::RenderTexture* output_texture,
    plume::RenderFramebuffer* present_framebuffer,
    plume::RenderShaderFormat shader_format,
    plume::RenderFormat output_depth_format,
    uint32_t frame_index,
    uint32_t width,
    uint32_t height) {
  ZoneScopedN("RenderGraph::Execute");
  PlumeDebugLabelScope execute_scope(
      command_list, BuildRenderGraphExecuteLabel(frame_index, width, height));
  {
    ZoneScopedN("RenderGraph::Scheduler");
    PlumeDebugLabelScope scheduler_scope(
        command_list, BuildRenderGraphSchedulerLabel(frame_index));
    RefiiRenderGraphRuntimeSchedulerSnapshot scheduler_snapshot;
    scheduler_snapshot.frame_index = frame_index;
    scheduler_snapshot.executions = 1;
    scheduler_snapshot.steps = 2;
    scheduler_snapshot.bridge_steps = 1;
    scheduler_snapshot.graph_owned_steps = 1;
    scheduler_snapshot.graph_owned_plan_steps = 1;
    scheduler_snapshot.graph_owned_render_steps = 0;
    scheduler_snapshot.graph_owned_clear_steps = 0;
    scheduler_snapshot.graph_owned_clear_commands = 0;
    scheduler_snapshot.graph_owned_draw_steps = 0;
    scheduler_snapshot.graph_owned_draw_commands = 0;
    scheduler_snapshot.graph_owned_resolve_steps = 0;
    scheduler_snapshot.graph_owned_resolve_commands = 0;
    scheduler_snapshot.bridge_commands = 0;
    StoreRefiiRenderGraphRuntimeSchedulerSnapshot(scheduler_snapshot);
    {
      ZoneScopedN("RenderGraph::Scheduler/Step::FableDirectBridge");
      PlumeDebugLabelScope scheduler_step_scope(
          command_list, BuildRenderGraphSchedulerBridgeStepLabel(frame_index));
      {
        ZoneScopedN("RenderGraph::Execute/FableDirectBridge");
        PlumeDebugLabelScope bridge_scope(
            command_list, BuildRenderGraphBridgeLabel(frame_index));
        // Deferred texel captures land here so the recorded frame sees
        // fresh content without the guest thread paying for the copies.
        //
        // STEP 6 capture-tied ack: sample the guest present boundary BEFORE
        // the drain (conservative — a boundary that lands mid-record is NOT
        // claimed), and publish it back once RecordFableDirectFrame returns:
        // by then every CPU-side guest-memory read for this frame (deferred
        // texel captures + record) has completed, which is what releases the
        // guest thread's capture-tied present ack in renderer_runtime.cpp.
        const uint64_t capture_ack_boundary_seq =
            FableGuestPresentBoundarySeqForCapture();
        DrainDeferredTextureCaptures();
        RecordFableDirectFrame(device, command_list, output_framebuffer,
                               output_texture, present_framebuffer,
                               shader_format, output_depth_format,
                               frame_index);
        PublishFableCapturesCompletedThroughBoundary(capture_ack_boundary_seq);
      }
    }
    const FableDirectRenderGraphExecutionSnapshot graph_execution =
        GetFableDirectRenderGraphExecutionSnapshot();
    {
      ZoneScopedN("RenderGraph::Scheduler/Step::FrameGraphPlanPublish");
      PlumeDebugLabelScope scheduler_plan_step_scope(
          command_list, BuildRenderGraphSchedulerPlanStepLabel(frame_index));
      StoreRefiiRenderGraphRuntimePlanSnapshot(
          GetFableDirectRenderGraphSnapshot());
    }
    scheduler_snapshot.bridge_steps =
        graph_execution.bridge_steps != 0 ? graph_execution.bridge_steps : 1u;
    scheduler_snapshot.bridge_commands = graph_execution.bridge_commands;
    scheduler_snapshot.graph_owned_render_steps =
        graph_execution.graph_owned_render_steps;
    scheduler_snapshot.graph_owned_clear_steps =
        graph_execution.graph_owned_clear_steps;
    scheduler_snapshot.graph_owned_clear_commands =
        graph_execution.graph_owned_clear_commands;
    scheduler_snapshot.graph_owned_draw_steps =
        graph_execution.graph_owned_draw_steps;
    scheduler_snapshot.graph_owned_draw_commands =
        graph_execution.graph_owned_draw_commands;
    scheduler_snapshot.graph_owned_resolve_steps =
        graph_execution.graph_owned_resolve_steps;
    scheduler_snapshot.graph_owned_resolve_commands =
        graph_execution.graph_owned_resolve_commands;
    scheduler_snapshot.graph_owned_steps =
        scheduler_snapshot.graph_owned_plan_steps +
        scheduler_snapshot.graph_owned_render_steps;
    scheduler_snapshot.steps =
        scheduler_snapshot.bridge_steps + scheduler_snapshot.graph_owned_steps;
    StoreRefiiRenderGraphRuntimeSchedulerSnapshot(scheduler_snapshot);
  }
  return GetFableDirectRendererStats();
}


}  // namespace refii::renderer
