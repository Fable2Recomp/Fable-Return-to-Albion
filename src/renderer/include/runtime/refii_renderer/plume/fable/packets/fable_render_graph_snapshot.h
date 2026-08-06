#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer.h"

#include <cstdint>
#include <vector>

namespace refii::renderer {

FableDirectRenderGraphSnapshot BuildFableDirectRenderGraphSnapshot(
    uint32_t frame_index,
    const std::vector<FableDirectFrameDatabaseDrawPacket>& draw_packets,
    uint32_t omitted_draw_packets,
    const std::vector<FableDirectFrameDatabaseCommandPacket>& command_packets,
    uint32_t omitted_command_packets);

FableDirectRenderGraphExecutionSnapshot
BuildFableDirectRenderGraphExecutionSnapshot(
    uint32_t frame_index,
    uint32_t graph_owned_render_steps,
    uint32_t graph_owned_clear_steps,
    uint32_t graph_owned_clear_commands,
    uint32_t graph_owned_draw_steps,
    uint32_t graph_owned_draw_commands,
    uint32_t graph_owned_resolve_steps,
    uint32_t graph_owned_resolve_commands,
    uint32_t bridge_steps,
    uint32_t bridge_commands,
    const std::vector<FableDirectFrameDatabaseCommandPacket>& command_packets);

}  // namespace refii::renderer
