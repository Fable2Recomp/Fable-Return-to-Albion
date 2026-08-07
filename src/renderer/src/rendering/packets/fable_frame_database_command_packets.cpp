#include "runtime/refii_renderer/plume/fable/packets/fable_frame_database_command_packets.h"

#include "runtime/plume/resource/plume_resource_manager_internal.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h"

namespace refii::renderer {

FableDirectFrameDatabaseCommandPacket
BuildFableDirectPresentBlitFrameDatabaseCommandPacket(
    const FableDirectPresentBlitCommandPacketInput& source,
    uint32_t output_width,
    uint32_t output_height) {
  FableDirectFrameDatabaseCommandPacket packet;
  packet.packet_id = source.source_write_serial != 0
                         ? source.source_write_serial
                         : uint64_t(source.source_frame);
  packet.command_serial = packet.packet_id;
  packet.kind = "present_blit";
  packet.pass_name = "PresentBlit";
  packet.render_targets[0] = kFableDirectPresentOutputResource;
  packet.present_blit.source_target = source.source_target;
  packet.present_blit.output_resource = kFableDirectPresentOutputResource;
  packet.present_blit.source_width = source.source_width;
  packet.present_blit.source_height = source.source_height;
  packet.present_blit.texture_width = source.texture_width;
  packet.present_blit.texture_height = source.texture_height;
  packet.present_blit.output_width = output_width;
  packet.present_blit.output_height = output_height;
  packet.present_blit.source_write_serial = source.source_write_serial;
  packet.present_blit.source_frame = source.source_frame;
  packet.present_blit.reason = source.reason != nullptr ? source.reason : "";
  return packet;
}

FableDirectFrameDatabaseCommandPacket
BuildFableDirectSurfaceDestinationFrameDatabaseCommandPacket(
    const FableSurfaceDestinationTextureRelation& relation,
    const PlumeTrackedResource& source,
    const PlumeTrackedResource& target,
    bool copy_used_scaled_path) {
  const FableSurfaceTextureLink& link = relation.link;
  FableDirectFrameDatabaseCommandPacket packet;
  packet.packet_id = relation.serial != 0 ? relation.serial
                                          : link.copy_call_serial;
  packet.command_serial = link.copy_call_serial != 0 ? link.copy_call_serial
                                                     : relation.serial;
  packet.kind = "surface_destination";
  packet.pass_name = "SurfaceDestination";
  packet.root.call_serial = link.copy_call_serial;
  packet.root.context_key = link.copy_context_key;
  packet.root.symbol_address = link.copy_symbol_address;
  packet.render_targets[0] = link.render_target;
  packet.depth_stencil = link.depth_stencil;
  packet.surface_destination.relation_serial = relation.serial;
  packet.surface_destination.source_surface = source.guest_address;
  packet.surface_destination.destination_texture = target.guest_address;
  packet.surface_destination.source_slot = relation.source_slot;
  packet.surface_destination.source_width = PlumeGuestTextureWidth(source);
  packet.surface_destination.source_height = PlumeGuestTextureHeight(source);
  packet.surface_destination.destination_width = PlumeGuestTextureWidth(target);
  packet.surface_destination.destination_height =
      PlumeGuestTextureHeight(target);
  packet.surface_destination.texture_base = link.texture_base_address;
  packet.surface_destination.backing_texture = link.backing_texture;
  packet.surface_destination.backing_base = link.backing_base_address;
  packet.surface_destination.backing_content_serial =
      link.backing_content_serial;
  packet.surface_destination.target_content_serial =
      link.target_content_serial;
  const bool extents_differ =
      packet.surface_destination.source_width !=
          packet.surface_destination.destination_width ||
      packet.surface_destination.source_height !=
          packet.surface_destination.destination_height;
  packet.surface_destination.scaled_copy =
      copy_used_scaled_path || extents_differ;
  packet.surface_destination.trusted_destination_edge =
      link.trusted_destination_edge;
  packet.surface_destination.wrapper_source_exact = link.wrapper_source_exact;
  return packet;
}

}  // namespace refii::renderer
