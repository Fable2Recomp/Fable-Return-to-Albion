#include "runtime/refii_renderer/plume/fable/packets/fable_render_graph_snapshot.h"

#include <algorithm>
#include <cstring>
#include <string_view>
#include <unordered_set>

namespace refii::renderer {
namespace {

template <size_t Capacity>
void CopyDirectDebugName(std::array<char, Capacity>& output,
                         std::string_view name) {
  output.fill('\0');
  if (name.empty()) {
    return;
  }
  const size_t copy_size =
      std::min(name.size(), static_cast<size_t>(Capacity - 1));
  std::memcpy(output.data(), name.data(), copy_size);
}

bool DirectFrameDatabaseRootPresent(
    const FableDirectFrameDatabaseRoot& root) {
  return root.call_serial != 0 || root.context_key != 0 ||
         root.symbol_address != 0 || root.lr != 0;
}

struct DirectRenderGraphPassAccumulator {
  FableDirectRenderGraphPassEntry entry;
  std::unordered_set<uint32_t> render_targets;
  std::unordered_set<uint32_t> depth_targets;
  std::unordered_set<uint32_t> textures;
  std::unordered_set<uint32_t> vertex_buffers;
};

void AddDirectRenderGraphResource(std::unordered_set<uint32_t>& resources,
                                  uint32_t address) {
  if (address != 0) {
    resources.insert(address);
  }
}

void AccumulateDirectRenderGraphPacket(
    DirectRenderGraphPassAccumulator& pass,
    const FableDirectFrameDatabaseDrawPacket& packet) {
  ++pass.entry.draw_packets;
  if (pass.entry.first_packet_id == 0) {
    pass.entry.first_packet_id = packet.packet_id;
  }
  if (packet.indexed) {
    ++pass.entry.indexed_draw_packets;
  }
  const bool draw_rooted =
      DirectFrameDatabaseRootPresent(packet.draw_root);
  const bool vertex_rooted =
      DirectFrameDatabaseRootPresent(packet.vertex_shader_root);
  const bool pixel_rooted =
      DirectFrameDatabaseRootPresent(packet.pixel_shader_root);
  const bool private_rooted =
      DirectFrameDatabaseRootPresent(packet.private_texture_wrapper_root);
  if (draw_rooted || vertex_rooted || pixel_rooted || private_rooted) {
    ++pass.entry.rooted_packets;
  }
  if (vertex_rooted && pixel_rooted) {
    ++pass.entry.fully_shader_rooted_packets;
  }
  for (uint32_t render_target : packet.render_targets) {
    AddDirectRenderGraphResource(pass.render_targets, render_target);
  }
  AddDirectRenderGraphResource(pass.depth_targets, packet.depth_stencil);
  for (uint32_t texture : packet.textures) {
    AddDirectRenderGraphResource(pass.textures, texture);
  }
  for (uint32_t vertex_buffer : packet.vertex_buffers) {
    AddDirectRenderGraphResource(pass.vertex_buffers, vertex_buffer);
  }
}

void AccumulateDirectRenderGraphCommand(
    DirectRenderGraphPassAccumulator& pass,
    const FableDirectFrameDatabaseCommandPacket& packet) {
  ++pass.entry.command_packets;
  if (pass.entry.first_command_packet_id == 0) {
    pass.entry.first_command_packet_id = packet.packet_id;
  }
  if (packet.kind == "clear") {
    ++pass.entry.clear_packets;
  } else if (packet.kind == "resolve") {
    ++pass.entry.resolve_packets;
  }
  if (packet.kind == "surface_destination") {
    AddDirectRenderGraphResource(
        pass.textures, packet.surface_destination.source_surface);
    AddDirectRenderGraphResource(
        pass.render_targets, packet.surface_destination.destination_texture);
    AddDirectRenderGraphResource(
        pass.textures, packet.surface_destination.backing_texture);
    return;
  }
  if (packet.kind == "present_blit") {
    AddDirectRenderGraphResource(pass.textures,
                                 packet.present_blit.source_target);
    AddDirectRenderGraphResource(pass.render_targets,
                                 packet.present_blit.output_resource);
    return;
  }
  if (packet.kind == "resolve") {
    const uint32_t source_slot =
        packet.resolve_source_surface != 0
            ? packet.resolve_source_slot
            : packet.resolve_copy_control & 0x7u;
    if (packet.resolve_source_surface != 0) {
      if (packet.resolve_source_depth) {
        AddDirectRenderGraphResource(pass.depth_targets,
                                     packet.resolve_source_surface);
      } else {
        AddDirectRenderGraphResource(pass.textures,
                                     packet.resolve_source_surface);
      }
    } else if (source_slot < packet.render_targets.size()) {
      AddDirectRenderGraphResource(pass.textures,
                                   packet.render_targets[source_slot]);
    } else {
      AddDirectRenderGraphResource(pass.depth_targets, packet.depth_stencil);
    }
    AddDirectRenderGraphResource(pass.render_targets, packet.resolve_dest_base);
    return;
  }
  for (uint32_t render_target : packet.render_targets) {
    AddDirectRenderGraphResource(pass.render_targets, render_target);
  }
  AddDirectRenderGraphResource(pass.depth_targets, packet.depth_stencil);
}

}  // namespace

FableDirectRenderGraphSnapshot BuildFableDirectRenderGraphSnapshot(
    uint32_t frame_index,
    const std::vector<FableDirectFrameDatabaseDrawPacket>& draw_packets,
    uint32_t omitted_draw_packets,
    const std::vector<FableDirectFrameDatabaseCommandPacket>& command_packets,
    uint32_t omitted_command_packets) {
  FableDirectRenderGraphSnapshot snapshot;
  snapshot.frame_index = frame_index;
  snapshot.draw_packet_count = static_cast<uint32_t>(
      std::min<size_t>(draw_packets.size(), UINT32_MAX));
  snapshot.omitted_draw_packets = omitted_draw_packets;
  snapshot.command_packet_count = static_cast<uint32_t>(
      std::min<size_t>(command_packets.size(), UINT32_MAX));
  snapshot.omitted_command_packets = omitted_command_packets;

  std::vector<DirectRenderGraphPassAccumulator> passes;
  auto find_or_add_pass =
      [&](std::string_view pass_name) -> DirectRenderGraphPassAccumulator& {
    auto it = std::find_if(
        passes.begin(), passes.end(),
        [&](const DirectRenderGraphPassAccumulator& pass) {
          return std::string_view(pass.entry.pass_name.data()) == pass_name;
        });
    if (it == passes.end()) {
      DirectRenderGraphPassAccumulator pass;
      pass.entry.valid = true;
      CopyDirectDebugName(pass.entry.pass_name, pass_name);
      passes.push_back(std::move(pass));
      it = std::prev(passes.end());
    }
    return *it;
  };

  for (const FableDirectFrameDatabaseCommandPacket& packet : command_packets) {
    const std::string_view pass_name =
        packet.pass_name.empty() ? std::string_view("Unknown")
                                 : std::string_view(packet.pass_name);
    AccumulateDirectRenderGraphCommand(find_or_add_pass(pass_name), packet);
  }

  for (const FableDirectFrameDatabaseDrawPacket& packet : draw_packets) {
    const std::string_view pass_name =
        packet.pass_name.empty() ? std::string_view("Unknown")
                                 : std::string_view(packet.pass_name);
    AccumulateDirectRenderGraphPacket(find_or_add_pass(pass_name), packet);
  }

  for (DirectRenderGraphPassAccumulator& pass : passes) {
    pass.entry.render_target_count =
        static_cast<uint32_t>(std::min<size_t>(pass.render_targets.size(),
                                               UINT32_MAX));
    pass.entry.depth_target_count =
        static_cast<uint32_t>(std::min<size_t>(pass.depth_targets.size(),
                                               UINT32_MAX));
    pass.entry.texture_count =
        static_cast<uint32_t>(std::min<size_t>(pass.textures.size(),
                                               UINT32_MAX));
    pass.entry.vertex_buffer_count =
        static_cast<uint32_t>(std::min<size_t>(pass.vertex_buffers.size(),
                                               UINT32_MAX));
    snapshot.resource_reference_count += pass.entry.render_target_count;
    snapshot.resource_reference_count += pass.entry.depth_target_count;
    snapshot.resource_reference_count += pass.entry.texture_count;
    snapshot.resource_reference_count += pass.entry.vertex_buffer_count;
  }

  for (const DirectRenderGraphPassAccumulator& pass : passes) {
    if (snapshot.pass_count >= kFableDirectRenderGraphPassCapacity) {
      ++snapshot.omitted_passes;
      continue;
    }
    snapshot.passes[snapshot.pass_count++] = pass.entry;
  }
  return snapshot;
}

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
    const std::vector<FableDirectFrameDatabaseCommandPacket>& command_packets) {
  FableDirectRenderGraphExecutionSnapshot snapshot;
  uint32_t packet_backed_resolve_commands = 0;
  uint32_t packet_backed_surface_destination_commands = 0;
  for (const FableDirectFrameDatabaseCommandPacket& packet : command_packets) {
    if (packet.kind == "resolve") {
      ++packet_backed_resolve_commands;
    } else if (packet.kind == "surface_destination") {
      ++packet_backed_surface_destination_commands;
    }
  }
  if (packet_backed_resolve_commands == 0) {
    packet_backed_resolve_commands = packet_backed_surface_destination_commands;
  }

  snapshot.frame_index = frame_index;
  snapshot.graph_owned_clear_steps = graph_owned_clear_steps;
  snapshot.graph_owned_clear_commands = graph_owned_clear_commands;
  snapshot.graph_owned_draw_steps = graph_owned_draw_steps;
  snapshot.graph_owned_draw_commands = graph_owned_draw_commands;
  snapshot.graph_owned_resolve_steps =
      std::max(graph_owned_resolve_steps,
               packet_backed_resolve_commands != 0 ? 1u : 0u);
  snapshot.graph_owned_resolve_commands =
      std::max(graph_owned_resolve_commands, packet_backed_resolve_commands);
  snapshot.graph_owned_render_steps =
      std::max(graph_owned_render_steps,
               snapshot.graph_owned_clear_steps +
                   snapshot.graph_owned_draw_steps +
                   snapshot.graph_owned_resolve_steps);
  snapshot.bridge_steps = bridge_steps;
  snapshot.bridge_commands = bridge_commands;
  return snapshot;
}

}  // namespace refii::renderer
