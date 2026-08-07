#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer_internal.h"

#include "runtime/cvars.h"
#include "runtime/hook_observer.h"
#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "runtime/refii_renderer/bik/bink_plane_publish.h"
#include "runtime/refii_renderer/gtl/refii_gtl.h"
#include "renderdoc_debugger.h"
#include "runtime/refii_renderer/plume/fable/fable_guest_display.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_private_rect_symbols.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_surface_texture_link_classification.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_private_rect_geometry.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_cache_keys.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_command_batch.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_command_classification.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_command_readback.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_framebuffer_binding.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_index_data.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_inline_data.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_primitive8.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_render_state.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_scratch_wrapper_state.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_present_state.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_direct_resource_readiness.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_synthetic_private_rect.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_frame_database_command_packets.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_frame_actions.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_refii_packet_analysis.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_refii_packet_builders.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_refii_replay_builders.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_texture_slot_masks.h"
#include "runtime/refii_renderer/plume/fable/debug/fable_renderdoc_capture.h"
#include "runtime/refii_renderer/plume/fable/debug/fable_resource_debug_snapshot.h"
#include "runtime/refii_renderer/plume/fable/debug/fable_renderdoc_draw_targets.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_render_graph_snapshot.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_classification.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_constants.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_draw_classification.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_frame_packet_classification.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_resource_facts.h"
#include "runtime/refii_renderer/plume/fable/video/fable_bink_plane_readiness.h"
#include "runtime/refii_renderer/plume/fable/video/fable_bink_vertex_transform.h"
#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_readiness.h"
#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_uploads.h"
#include "runtime/refii_renderer/plume/fable/video/fable_video_classification.h"
#include "runtime/refii_renderer/plume/fable/video/fable_video_constants.h"
#include "runtime/plume/draw/plume_draw_state.h"
#include "runtime/refii_renderer/plume/general/plume_renderdoc_capture.h"
#include "runtime/plume/resource/plume_resource_manager_internal.h"
#include "runtime/plume/resource/plume_vertex_declaration.h"
#include "renderer_runtime.h"
#include "runtime/shader_library.h"

#include <algorithm>
#include <atomic>
#include <array>
#include <chrono>
#include <cctype>
#include <bit>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <fstream>
#include <limits>
#include <memory>
#include <mutex>
#include <span>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include <rex/logging.h>
#include <tracy/Tracy.hpp>

#if REFII_RENDERER_HAS_PLUME
#include <plume_render_interface_builders.h>
#if defined(_WIN64)
#include "plume_present_blit_ps.hlsl.dxil.h"
#include "plume_present_blit_vs.hlsl.dxil.h"
#endif
#include "plume_present_blit_ps.hlsl.spirv.h"
#include "plume_present_blit_vs.hlsl.spirv.h"
#endif

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME

PlumeTrackedResource* ResolveDirectTrustedSurfaceTextureEdgeLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (slot >= command.resources.textures.size() ||
      command.draw.source == RendererDrawEventSource::kPixVideo ||
      !DirectCommandUsesFablePrivateTexturePath(command, slot, texture)) {
    return nullptr;
  }

  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  const uint32_t guest = command.resources.textures[slot];
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  const uint64_t command_fetch_key = FableDirectTextureFetchKey(command_fetch);
  const uint32_t texture_base =
      texture ? PlumeGuestTextureBaseAddress(*texture) : 0u;
  const uint64_t texture_fetch_key =
      texture ? FableDirectTextureFetchKey(PlumeGuestTextureFetchDwords(*texture))
              : 0u;
  auto exact_link_matches_command_texture_identity =
      [&](const FableSurfaceTextureLink& candidate) {
    const uint64_t link_fetch_key =
        FableDirectTextureFetchKey(candidate.texture_fetch_dwords);
    auto matches_guest = [&](uint32_t guest_address) {
      if (guest_address == 0) {
        return false;
      }
      return DirectResourceAddressMatchesLocked(candidate.texture,
                                                guest_address) ||
             DirectResourceAddressMatchesLocked(candidate.backing_texture,
                                                guest_address) ||
             DirectResourceAddressMatchesLocked(candidate.texture_base_address,
                                                guest_address) ||
             DirectResourceAddressMatchesLocked(candidate.backing_base_address,
                                                guest_address);
    };
    if (matches_guest(guest)) {
      return true;
    }
    if (texture &&
        (matches_guest(texture->guest_address) ||
         matches_guest(texture->parent_address) || matches_guest(texture_base))) {
      return true;
    }
    if (command_fetch_info.valid &&
        (candidate.texture_base_address == command_fetch_info.base_address ||
         candidate.backing_base_address == command_fetch_info.base_address)) {
      return true;
    }
    if (texture_base != 0 &&
        (candidate.texture_base_address == texture_base ||
         candidate.backing_base_address == texture_base)) {
      return true;
    }
    if (command_fetch_key != 0 && link_fetch_key != 0 &&
        command_fetch_key == link_fetch_key) {
      return true;
    }
    if (texture_fetch_key != 0 && link_fetch_key != 0 &&
        texture_fetch_key == link_fetch_key) {
      return true;
    }
    auto identity_matches = [&](const FableTextureIdentity* identity) {
      if (!identity) {
        return false;
      }
      const uint64_t identity_fetch_key =
          FableDirectTextureFetchKey(identity->fetch_dwords);
      return matches_guest(identity->object_texture) ||
             matches_guest(identity->parent_texture) ||
             matches_guest(identity->base_address) ||
             (identity_fetch_key != 0 && link_fetch_key != 0 &&
              identity_fetch_key == link_fetch_key);
    };
    return identity_matches(FableDirectFindTextureIdentityByObject(guest)) ||
           (texture &&
            (identity_matches(FableDirectFindTextureIdentityByObject(
                 texture->guest_address)) ||
             identity_matches(FableDirectFindTextureIdentityByObject(
                 texture->parent_address)) ||
             identity_matches(FableDirectFindTextureIdentityByBase(
                 texture_base)))) ||
           (command_fetch_info.valid &&
            identity_matches(FableDirectFindTextureIdentityByBase(
                command_fetch_info.base_address)));
  };
  auto exact_link_visible_to_command =
      [&](const FableSurfaceTextureLink *link) -> bool {
    const bool exact_identity_match =
        link && exact_link_matches_command_texture_identity(*link);
    const bool valid_source_slot =
        link && (link->source_slot == kUnknownSlot ||
                 link->source_slot < command.resources.textures.size());
    const bool valid_copy_symbol =
        link && (link->copy_symbol_address == 0 ||
                 DirectIdaSurfaceTextureWrapperProofSymbol(
                     link->copy_symbol_address));
    const bool same_slot = link && link->source_slot == slot;
    const bool unknown_slot_exact_match =
        link && link->source_slot == kUnknownSlot && exact_identity_match;
    const bool fable_private_chain =
        link &&
        DirectSurfaceTextureLinkTouchesFablePrivateChain(*link, texture);
    if (!link || !link->trusted_destination_edge ||
        !valid_source_slot || !valid_copy_symbol ||
        link->frame_index > g_direct_renderer.current_frame_index ||
        (!same_slot && !unknown_slot_exact_match &&
         !(fable_private_chain && exact_identity_match))) {
      return false;
    }
    if (!DirectSurfaceTextureLinkPrivateWrapperVisibleToCommandLocked(command,
                                                                      *link)) {
      return false;
    }
    if (fable_private_chain &&
        DirectSurfaceTextureLinkHasExactCopyIdentity(*link)) {
      const bool current_target_exact_edge =
          DirectSurfaceTextureLinkIsCurrentTargetExactEdgeLocked(command,
                                                                 *link);
      if (!current_target_exact_edge &&
          (!exact_identity_match ||
           !DirectSurfaceTextureLinkHasCommandConsumableSampleIdentity(
               *link))) {
        return false;
      }
    }
    return true;
  };
  auto log_exact_trusted_surface_texture_ownership =
      [&](const FableSurfaceTextureLink& link,
          const PlumeTrackedResource& linked, const char* source_name,
          uint64_t copy_call_serial, uint64_t copy_context_key,
          uint32_t copy_symbol_address) {
    if (g_direct_renderer.diagnostics.logged_private_rect_surface_texture_ownership_proofs >=
        24u) {
      return;
    }
    REXLOG_INFO(
        "Refii plume direct exact trusted surface-texture ownership proof #{} "
        "reason=trusted-surface-texture frame={} source={} slot={} "
        "link_slot={} texture=0x{:08X} surface=0x{:08X} backing=0x{:08X} "
        "sample=0x{:08X}/{}x{} fmt={} call={} context=0x{:016X} "
        "symbol=0x{:08X} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
        "ps=0x{:08X}",
        ++g_direct_renderer
              .diagnostics.logged_private_rect_surface_texture_ownership_proofs,
        g_direct_renderer.current_frame_index, source_name, slot,
        link.source_slot, link.texture, link.source_surface,
        link.backing_texture, linked.guest_address,
        PlumeGuestTextureWidth(linked), PlumeGuestTextureHeight(linked),
        static_cast<uint32_t>(PlumeGuestTextureFormat(linked)),
        static_cast<unsigned long long>(copy_call_serial),
        static_cast<unsigned long long>(copy_context_key), copy_symbol_address,
        command.resources.render_targets[0], command.resources.depth_stencil,
        command.resources.vertex_shader, command.resources.pixel_shader);
  };
  auto try_exact_alias_destination =
      [&](uint32_t destination_texture,
          const char *source_name) -> PlumeTrackedResource * {
    if (destination_texture == 0) {
      return nullptr;
    }
    RendererSurfaceCopyAliasSnapshot alias;
    if (!FindDirectSurfaceCopyAliasByDestinationAddressLocked(
            destination_texture, &alias) ||
        !DirectSurfaceCopyAliasHasExactIdentity(alias)) {
      return nullptr;
    }
    if (alias.copy_symbol_address != 0 &&
        !DirectIdaSurfaceTextureWrapperProofSymbol(alias.copy_symbol_address)) {
      return nullptr;
    }
    const FableSurfaceTextureLink* link =
        FindDirectSurfaceTextureLinkByAliasLocked(alias);
    if (!exact_link_visible_to_command(link)) {
      return nullptr;
    }
    const FableSurfaceTextureLink selected_link = *link;

    PlumeTrackedResource* linked =
        ResolveDirectSurfaceTextureLinkResourceLocked(command, selected_link);
    if (!linked) {
      return nullptr;
    }

    log_exact_trusted_surface_texture_ownership(
        selected_link, *linked, source_name, alias.copy_call_serial,
        alias.copy_context_key, alias.copy_symbol_address);
    LogDirectPrivateChainSurfaceTextureLinkLocked(
        command, slot, selected_link, *linked, source_name);
    return linked;
  };
  auto try_exact_destination_link =
      [&](const FableSurfaceTextureLink* link,
          const char* source_name) -> PlumeTrackedResource* {
    if (!exact_link_visible_to_command(link)) {
      return nullptr;
    }
    const FableSurfaceTextureLink selected_link = *link;
    if (DirectSurfaceTextureLinkIsCurrentTargetSelfEdgeLocked(
            command, selected_link) ||
        DirectSurfaceTextureLinkIsCurrentTargetExactEdgeLocked(command,
                                                              selected_link)) {
      return nullptr;
    }

    PlumeTrackedResource* linked =
        ResolveDirectSurfaceTextureLinkResourceLocked(command, selected_link);
    if (!linked) {
      return nullptr;
    }

    log_exact_trusted_surface_texture_ownership(
        selected_link, *linked, source_name, selected_link.copy_call_serial,
        selected_link.copy_context_key, selected_link.copy_symbol_address);
    LogDirectPrivateChainSurfaceTextureLinkLocked(
        command, slot, selected_link, *linked, source_name);
    return linked;
  };
  auto try_direct_destination =
      [&](uint32_t destination_texture,
          const char *source_name) -> PlumeTrackedResource * {
    if (destination_texture == 0) {
      return nullptr;
    }
    if (PlumeTrackedResource *linked = try_exact_destination_link(
            FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
                destination_texture, g_direct_renderer.current_frame_index,
                command.draw_serial, slot),
            source_name)) {
      return linked;
    }
    if (PlumeTrackedResource *linked = try_exact_destination_link(
            FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
                destination_texture, slot),
            source_name)) {
      return linked;
    }
    if (PlumeTrackedResource *linked = try_exact_destination_link(
            FableDirectFindSurfaceTextureLinkForDestinationTexture(
                destination_texture),
            source_name)) {
      return linked;
    }
    const std::vector<FableSurfaceTextureLink> *links =
        FableDirectFindSurfaceTextureLinksForDestinationTexture(
            destination_texture);
    if (!links) {
      return nullptr;
    }
    for (auto it = links->rbegin(); it != links->rend(); ++it) {
      if (PlumeTrackedResource *linked =
              try_exact_destination_link(&*it, source_name)) {
        return linked;
      }
    }
    return nullptr;
  };
  std::array<std::pair<uint32_t, const char*>, 16> candidates = {};
  uint32_t candidate_count = 0;
  auto add_candidate = [&](uint32_t address, const char* source_name) {
    if (address == 0) {
      return;
    }
    for (uint32_t i = 0; i < candidate_count; ++i) {
      if (candidates[i].first == address) {
        return;
      }
    }
    if (candidate_count < candidates.size()) {
      candidates[candidate_count++] = {address, source_name};
    }
  };

  add_candidate(guest, "guest");
  if (texture) {
    add_candidate(texture->guest_address, "tracked");
    add_candidate(texture->parent_address, "parent");
    add_candidate(texture_base, "tracked-base");
  }
  const FableTextureIdentity* identity =
      FableDirectFindTextureIdentityByObject(guest);
  if (!identity && texture) {
    identity = FableDirectFindTextureIdentityByObject(texture->guest_address);
  }
  auto add_identity_candidates = [&](const FableTextureIdentity* candidate,
                                     const char* object_source,
                                     const char* parent_source,
                                     const char* base_source) {
    if (!candidate) {
      return;
    }
    add_candidate(candidate->object_texture, object_source);
    add_candidate(candidate->parent_texture, parent_source);
    add_candidate(candidate->base_address, base_source);
  };
  add_identity_candidates(identity, "identity-object", "identity-parent",
                          "identity-base");
  if (texture_base != 0) {
    add_identity_candidates(FableDirectFindTextureIdentityByBase(texture_base),
                            "tracked-base-object", "tracked-base-parent",
                            "tracked-base");
  }
  if (command_fetch_info.valid) {
    add_candidate(command_fetch_info.base_address, "command-fetch-base");
    add_identity_candidates(
        FableDirectFindTextureIdentityByBase(command_fetch_info.base_address),
        "command-fetch-base-object", "command-fetch-base-parent",
        "command-fetch-base");
  }
  if (HasTrackedFetchDwords(command_fetch)) {
    add_identity_candidates(FableDirectFindTextureIdentityByFetch(command_fetch),
                            "command-fetch-object", "command-fetch-parent",
                            "command-fetch-base");
  }
  if (texture) {
    add_identity_candidates(
        FableDirectFindTextureIdentityByFetch(
            PlumeGuestTextureFetchDwords(*texture)),
        "tracked-fetch-object", "tracked-fetch-parent", "tracked-fetch-base");
  }

	  for (uint32_t i = 0; i < candidate_count; ++i) {
	    if (PlumeTrackedResource* linked =
	            try_direct_destination(candidates[i].first, candidates[i].second)) {
	      return linked;
	    }
	    if (PlumeTrackedResource* linked =
	            try_exact_alias_destination(candidates[i].first,
	                                        candidates[i].second)) {
      return linked;
    }
  }

  return nullptr;
}

PlumeTrackedResource* ResolveDirectCommandTextureSlotExactProducerLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (slot >= command.resources.textures.size() ||
      command.draw.source == RendererDrawEventSource::kPixVideo) {
    return nullptr;
  }

  auto valid_sample_resource =
      [&](PlumeTrackedResource* resource) -> PlumeTrackedResource* {
    if (!resource || !IsImageResource(resource->kind) ||
        !PlumeGuestImageReady(*resource) ||
        !DirectResourceHasTextureSampleContentForExactProducer(*resource) ||
        FableTrackedResourceIsBlankUploadedSampleProxy(*resource) ||
        DirectCommandBindsResourceAsTargetLocked(command, *resource)) {
      return nullptr;
    }
    return resource;
  };

  auto valid_sample = [&](uint32_t address) -> PlumeTrackedResource* {
    return address != 0 ? valid_sample_resource(FindDirectResourceLocked(address))
                        : nullptr;
  };

  auto link_matches_requested_destination =
      [&](const FableSurfaceTextureLink* link,
          uint32_t destination_texture) -> bool {
    if (!link || destination_texture == 0) {
      return false;
    }
    if (DirectResourceAddressMatchesLocked(link->texture,
                                           destination_texture)) {
      return true;
    }
    if (link->texture_base_address != 0 &&
        link->texture_base_address == destination_texture) {
      return true;
    }
    if (const FableTextureIdentity* destination_identity =
            FableDirectFindTextureIdentityByObject(destination_texture)) {
      if (DirectResourceAddressMatchesLocked(
              link->texture, destination_identity->object_texture) ||
          DirectResourceAddressMatchesLocked(
              link->texture, destination_identity->parent_texture) ||
          (destination_identity->base_address != 0 &&
           link->texture_base_address == destination_identity->base_address)) {
        return true;
      }
      const uint64_t destination_identity_fetch_key =
          FableDirectTextureFetchKey(destination_identity->fetch_dwords);
      if (destination_identity_fetch_key != 0 &&
          FableDirectTextureFetchKey(link->texture_fetch_dwords) ==
              destination_identity_fetch_key) {
        return true;
      }
    }
    return false;
  };
  auto link_endpoint_matches_request =
      [&](const FableSurfaceTextureLink* link, uint32_t requested_texture,
          uint32_t endpoint_texture, uint32_t endpoint_base_address,
          const std::array<uint32_t, 6>& endpoint_fetch_dwords) -> bool {
    if (!link || requested_texture == 0) {
      return false;
    }
    if (DirectResourceAddressMatchesLocked(endpoint_texture,
                                           requested_texture)) {
      return true;
    }
    if (endpoint_base_address != 0 &&
        endpoint_base_address == requested_texture) {
      return true;
    }
    if (const FableTextureIdentity* requested_identity =
            FableDirectFindTextureIdentityByObject(requested_texture)) {
      if (DirectResourceAddressMatchesLocked(
              endpoint_texture, requested_identity->object_texture) ||
          DirectResourceAddressMatchesLocked(
              endpoint_texture, requested_identity->parent_texture) ||
          (requested_identity->base_address != 0 &&
           endpoint_base_address == requested_identity->base_address)) {
        return true;
      }
      const uint64_t requested_fetch_key =
          FableDirectTextureFetchKey(requested_identity->fetch_dwords);
      if (requested_fetch_key != 0 &&
          FableDirectTextureFetchKey(endpoint_fetch_dwords) ==
              requested_fetch_key) {
        return true;
      }
    }
    return false;
  };
  auto link_matches_requested_backing_identity =
      [&](const FableSurfaceTextureLink* link,
          uint32_t requested_texture) -> bool {
    return link_endpoint_matches_request(
        link, requested_texture, link ? link->backing_texture : 0u,
        link ? link->backing_base_address : 0u,
        link ? link->backing_fetch_dwords : std::array<uint32_t, 6>{});
  };

		  if (PlumeTrackedResource* trusted_edge_sample =
		          ResolveDirectTrustedSurfaceTextureEdgeLocked(command, slot, texture)) {
		    static uint32_t logged_exact_producer_source_surface_textures = 0;
	    if (logged_exact_producer_source_surface_textures < 32u) {
	      const std::array<uint32_t, 6> command_fetch =
	          DirectCommandTextureFetchDwords(command, slot);
	      REXLOG_INFO(
	          "Refii plume exact producer source-surface texture identity #{} "
	          "frame={} serial={} draw={} slot={} guest=0x{:08X} "
	          "sample=0x{:08X}/{}x{} base=0x{:08X} fmt={} gen={} content={} "
	          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
	          "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
	          ++logged_exact_producer_source_surface_textures,
	          g_direct_renderer.current_frame_index,
	          static_cast<unsigned long long>(command.serial),
	          static_cast<unsigned long long>(command.draw_serial), slot,
	          command.resources.textures[slot], trusted_edge_sample->guest_address,
	          PlumeGuestTextureWidth(*trusted_edge_sample),
	          PlumeGuestTextureHeight(*trusted_edge_sample),
	          PlumeGuestTextureBaseAddress(*trusted_edge_sample),
	          static_cast<uint32_t>(PlumeGuestTextureFormat(*trusted_edge_sample)),
	          static_cast<unsigned long long>(
	              trusted_edge_sample->upload_generation),
	          static_cast<unsigned long long>(
	              FableTrackedResourceVisibleContentSerial(*trusted_edge_sample)),
	          command.resources.render_targets[0], command.resources.depth_stencil,
	          command.resources.vertex_shader, command.resources.pixel_shader,
	          command_fetch[0], command_fetch[1], command_fetch[2],
	          command_fetch[3], command_fetch[4], command_fetch[5]);
		    }
		    return trusted_edge_sample;
		  }

  if (PlumeTrackedResource* mutable_header_sample =
          ResolveDirectMutablePrivateHeaderSampleByIdentityLocked(
              command, slot, texture, true)) {
    return mutable_header_sample;
  }

  if (PlumeTrackedResource* exact_title_upload =
          ResolveDirectExactTitleUploadSourceForCommandSlotLocked(command, slot,
                                                                  texture)) {
    static uint32_t logged_exact_title_upload_producer_samples = 0;
    if (logged_exact_title_upload_producer_samples < 32u) {
      const std::array<uint32_t, 6> command_fetch =
          DirectCommandTextureFetchDwords(command, slot);
      REXLOG_INFO(
          "Refii plume exact producer title-upload texture identity #{} "
          "frame={} serial={} draw={} slot={} guest=0x{:08X} "
          "sample=0x{:08X}/{}x{} base=0x{:08X} fmt={} gen={} content={} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
          "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
          ++logged_exact_title_upload_producer_samples,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.serial),
          static_cast<unsigned long long>(command.draw_serial), slot,
          command.resources.textures[slot], exact_title_upload->guest_address,
          PlumeGuestTextureWidth(*exact_title_upload),
          PlumeGuestTextureHeight(*exact_title_upload),
          PlumeGuestTextureBaseAddress(*exact_title_upload),
          static_cast<uint32_t>(PlumeGuestTextureFormat(*exact_title_upload)),
          static_cast<unsigned long long>(
              exact_title_upload->upload_generation),
          static_cast<unsigned long long>(
              FableTrackedResourceVisibleContentSerial(*exact_title_upload)),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader,
          command_fetch[0], command_fetch[1], command_fetch[2],
          command_fetch[3], command_fetch[4], command_fetch[5]);
    }
    return exact_title_upload;
  }

  auto resolve_direct_resource_sample = [&]() -> PlumeTrackedResource* {
    PlumeTrackedResource* direct_resource_candidate =
        texture ? FindDirectResourceLocked(texture->guest_address) : nullptr;
    if (!direct_resource_candidate && command.resources.textures[slot] != 0) {
      direct_resource_candidate =
          FindDirectResourceLocked(command.resources.textures[slot]);
    }
    PlumeTrackedResource* direct_resource_sample =
        valid_sample_resource(direct_resource_candidate);
    if (!direct_resource_sample ||
        !DirectCommandTextureSlotDirectResourceProvidesSampleLocked(
            command, slot, direct_resource_sample, true)) {
      return nullptr;
    }

	    static uint32_t logged_exact_producer_owned_resource_samples = 0;
	    if (logged_exact_producer_owned_resource_samples < 32u) {
	      const std::array<uint32_t, 6> command_fetch =
	          DirectCommandTextureFetchDwords(command, slot);
	      REXLOG_INFO(
	          "Refii plume exact producer-owned texture identity #{} "
	          "frame={} serial={} draw={} slot={} guest=0x{:08X} "
	          "sample=0x{:08X}/{}x{} base=0x{:08X} fmt={} gen={} content={} "
	          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
	          "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
	          ++logged_exact_producer_owned_resource_samples,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.serial),
          static_cast<unsigned long long>(command.draw_serial), slot,
          command.resources.textures[slot], direct_resource_sample->guest_address,
          PlumeGuestTextureWidth(*direct_resource_sample),
          PlumeGuestTextureHeight(*direct_resource_sample),
          PlumeGuestTextureBaseAddress(*direct_resource_sample),
          static_cast<uint32_t>(
              PlumeGuestTextureFormat(*direct_resource_sample)),
          static_cast<unsigned long long>(
              direct_resource_sample->upload_generation),
          static_cast<unsigned long long>(
              FableTrackedResourceVisibleContentSerial(*direct_resource_sample)),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader,
          command_fetch[0], command_fetch[1], command_fetch[2],
          command_fetch[3], command_fetch[4], command_fetch[5]);
    }
    return direct_resource_sample;
  };

  if (slot == 0 && DirectCommandIsTitleGuiStripProofCandidateLocked(command)) {
    const uint32_t hdr_proxy = command.resources.textures[0];
    const bool exact_title_hdr_proxy =
        command.draw.private_texture_wrapper_symbol_address ==
            kFableBeginVerticesTitleWrapperSymbol &&
        FablePrivateTextureProxyRoleForAddress(hdr_proxy) ==
            FablePrivateTextureProxyRole::kColor &&
        command.resources.textures.size() > 2u &&
        command.resources.textures[2] != 0;
    if (exact_title_hdr_proxy) {
      PlumeTrackedResource* bloom_texture =
          FindDirectResourceLocked(command.resources.textures[2]);
      PlumeTrackedResource* title_surface_sample = nullptr;
      if (bloom_texture) {
        title_surface_sample =
            ResolveDirectTrustedSurfaceTextureEdgeLocked(command, 2u,
                                                        bloom_texture);
        if (!title_surface_sample) {
          title_surface_sample =
              ResolveDirectMutablePrivateHeaderSampleByIdentityLocked(
                  command, 2u, bloom_texture, true);
        }
        if (!title_surface_sample &&
            DirectCommandTextureSlotDirectResourceProvidesSampleLocked(
                command, 2u, bloom_texture, true)) {
          title_surface_sample = bloom_texture;
        }
      }
      if (PlumeTrackedResource* valid_title_surface_sample =
              valid_sample_resource(title_surface_sample)) {
        const uint64_t draw_serial =
            command.draw_serial != 0 ? command.draw_serial : command.serial;
        auto resolve_title_proxy_source_surface =
            [&](uint32_t destination_texture) -> PlumeTrackedResource* {
          if (destination_texture == 0) {
            return nullptr;
          }
          const FableSurfaceDestinationTextureRelation* relation =
              FableDirectFindVisibleSurfaceDestinationRelationForDestinationTexture(
                  destination_texture, g_direct_renderer.current_frame_index,
                  draw_serial, 2u);
          if (!relation) {
            relation =
                FableDirectFindSurfaceDestinationRelationForDestinationTextureSlot(
                    destination_texture, 2u);
          }
          if (!relation) {
            relation = FableDirectFindSurfaceDestinationRelationForDestinationTexture(
                destination_texture);
          }
          if (!relation || !relation->link.trusted_destination_edge ||
              relation->link.copy_symbol_address !=
                  kFableBeginVerticesTitleWrapperSymbol ||
              relation->source_surface == 0) {
            return nullptr;
          }
          return valid_sample_resource(
              FindDirectResourceLocked(relation->source_surface));
        };
        PlumeTrackedResource* title_proxy_source_sample =
            resolve_title_proxy_source_surface(command.resources.textures[2]);
        if (!title_proxy_source_sample) {
          static uint32_t logged_title_hdr_proxy_missing_source = 0;
          if (logged_title_hdr_proxy_missing_source < 32u) {
            REXLOG_INFO(
                "Refii plume exact title hdr proxy source missing #{} "
                "frame={} serial={} draw={} slot={} proxy=0x{:08X} "
                "slot2=0x{:08X} rejected_sample=0x{:08X}/{}x{} "
                "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
                ++logged_title_hdr_proxy_missing_source,
                g_direct_renderer.current_frame_index,
                static_cast<unsigned long long>(command.serial),
                static_cast<unsigned long long>(command.draw_serial), slot,
                hdr_proxy, command.resources.textures[2],
                valid_title_surface_sample->guest_address,
                PlumeGuestTextureWidth(*valid_title_surface_sample),
                PlumeGuestTextureHeight(*valid_title_surface_sample),
                command.resources.render_targets[0],
                command.resources.depth_stencil,
                command.resources.vertex_shader,
                command.resources.pixel_shader);
          }
        } else {
          FablePrivateTextureCopyAction action;
          action.source_slot = slot;
          action.source_texture = hdr_proxy;
          action.source_base_address = 0;
          action.source_width =
              PlumeGuestTextureWidth(*title_proxy_source_sample);
          action.source_height =
              PlumeGuestTextureHeight(*title_proxy_source_sample);
          action.source_format =
              PlumeGuestTextureGuestFormat(*title_proxy_source_sample);
          action.source_fetch_dwords =
              DirectCommandTextureFetchDwords(command, slot);
          PopulateDirectPrivateTextureCopyTargetLocked(
              action, *title_proxy_source_sample);
          action.target_content_serial =
              FableTrackedResourceVisibleContentSerial(
                  *title_proxy_source_sample);
          action.render_target = command.resources.render_targets[0];
          action.depth_stencil = command.resources.depth_stencil;
          action.vertex_shader = command.resources.vertex_shader;
          action.pixel_shader = command.resources.pixel_shader;
          PopulateDirectPrivateTextureCopyShaderRoots(action, command);
          action.lr = command.draw.state_flush_lr;
          action.wrapper_call_serial =
              command.draw.private_texture_wrapper_call_serial;
          action.wrapper_context_key =
              command.draw.private_texture_wrapper_context_key;
          action.wrapper_symbol_address =
              command.draw.private_texture_wrapper_symbol_address;
          PopulateDirectPrivateTextureWrapperSourceIdentity(action, command);
          action.frame_index = g_direct_renderer.current_frame_index;
          action.draw_serial = draw_serial;
          action.kind = FablePrivateTextureCopyKind::kColor;
          action.sampled_source.kind =
              FableRegisteredSampleSourceKind::kGpuSurface;
          action.sampled_source.resource =
              title_proxy_source_sample->guest_address;
          action.sampled_source.content_serial =
              FableTrackedResourceVisibleContentSerial(
                  *title_proxy_source_sample);
          RegisterDirectPrivateTextureCopyActionLocked(action);
          RememberDirectPrivateRectExactActionLocked(action);
          RegisterDirectSurfaceTextureLinkForPrivateCopyActionLocked(
              action, "beginvertices-title-hdr-proxy");
          TrackDirectExactSurfaceAliasForPrivateCopyActionLocked(
              action, "beginvertices-title-hdr-proxy");
          RememberDirectPrivateTextureAliasIndexLocked(
              action.source_texture, action.source_base_address,
              action.source_fetch_dwords, *title_proxy_source_sample);
          RememberDirectMutablePrivateHeaderExactSampleLocked(
              command, slot, action, *title_proxy_source_sample,
              "beginvertices-title-hdr-proxy");
          return title_proxy_source_sample;
        }
      }
    }

    if (PlumeTrackedResource* title_hdr_sample =
            resolve_direct_resource_sample()) {
      const uint32_t sample_width = PlumeGuestTextureWidth(*title_hdr_sample);
      const uint32_t sample_height = PlumeGuestTextureHeight(*title_hdr_sample);
      const PlumeTrackedResource* render_target =
          FindDirectResourceLocked(command.resources.render_targets[0]);
      const uint32_t target_width =
          render_target ? PlumeGuestTextureWidth(*render_target) : 0u;
      const uint32_t target_height =
          render_target ? PlumeGuestTextureHeight(*render_target) : 0u;
      if ((target_width == 0u || sample_width >= target_width) &&
          (target_height == 0u || sample_height >= target_height) &&
          sample_width >= FableGuestDisplayWidth() &&
          sample_height >= FableGuestDisplayHeight()) {
        return title_hdr_sample;
      }
    }
  }


  if (PlumeTrackedResource* direct_resource_sample =
          resolve_direct_resource_sample()) {
    return direct_resource_sample;
  }

  return nullptr;
}

// GTL exact identity (design doc §3.1-3.2, refii_gtl_migration.md step 3):
// the object the game handed to SetTexture IS the binding. Resolution is the
// record at that exact address, with one deterministic fallback to the
// slot's fetch base address — two headers over one memory range are views of
// the same content on console, and the base-keyed record carries that
// content today. No candidate ranking, no title gates, no serial heuristics.
// Bink video planes stay on their dedicated decode path until the GTL
// content phase absorbs it.

PlumeTrackedResource* ResolveDirectSampledTextureForCommandLocked(
    const FableDirectRenderCommand& command, uint32_t slot) {
  if (slot >= command.resources.textures.size()) {
    return nullptr;
  }
  if (PlumeTrackedResource* bink_plane =
          ResolveDirectExactBinkWrapperPlaneForSlotLocked(command, slot)) {
    return bink_plane;
  }
  // §3b packet-stated private-rect source override REMOVED (2026-07-17): it was
  // inert (hard-coded usable=false since run 432) and its premise is disproven —
  // sub_82206888 is D3DDevice_Resolve, which samples no source texture at all.
  const auto usable = [&](PlumeTrackedResource* resource) {
    return resource != nullptr && IsImageResource(resource->kind) &&
           PlumeGuestImageReady(*resource) &&
           !resource->texture_depth_target &&
           !DirectCommandBindsResourceAsTargetLocked(command, *resource);
  };
  // §3 binding authority (migration doc): the exact object route —
  // packet.textures[slot] → registry — is the primary route. The deterministic
  // fetch-base step below it was deleted 2026-07-22 on runs 141-149 measuring
  // `fetch_fallback=0 null=0` — but those runs predate the 07-29 observer
  // draw-event gate, which removed the feed that pre-materialized records at
  // bound header addresses. The deletion's own designated alarm fired in run
  // 480: `null=20865`, all recycled title-atlas headers (e.g. 0x4051D610)
  // binding over a base (0x1A0D9000) whose record is live and ready under a
  // sibling header key. REINSTATED 2026-08-01, still stated-never-inferred:
  // the fetch constant is a verbatim ring value (SetTexture is ring-complete,
  // 0x82BA77D0), and on real Xenos the TFETCH samples the fetch constant's
  // base — the CPU header object address never reaches the GPU, so base
  // identity IS the Xbox semantic; header-object keying is our bookkeeping.
  static uint64_t resolver_exact_hits = 0;
  static uint64_t resolver_fetch_hits = 0;
  static uint64_t resolver_nulls = 0;
  const auto report_census = [] {
    const uint64_t total =
        resolver_exact_hits + resolver_fetch_hits + resolver_nulls;
    if (total != 0 && (total % 16384u) == 0u) {
      REXLOG_INFO("Refii resolver census exact={} fetch={} null={}",
                  static_cast<unsigned long long>(resolver_exact_hits),
                  static_cast<unsigned long long>(resolver_fetch_hits),
                  static_cast<unsigned long long>(resolver_nulls));
    }
  };
  const uint32_t guest = command.resources.textures[slot];
  if (guest != 0) {
    PlumeTrackedResource* exact = FindDirectResourceLocked(guest);
    if (usable(exact)) {
      ++resolver_exact_hits;
      report_census();
      return exact;
    }
  }
  const std::array<uint32_t, 6> fetch_dwords =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo fetch_info =
      DirectTextureFetchInfoFromDwords(fetch_dwords);
  // OVER-GATE REMOVED 2026-08-03. This step used to require
  // `fetch_info.valid`, which is a SNAPSHOT predicate: it demands a supported
  // shape (no stacked arrays, cubes only at stack_depth==5), a known format
  // with a non-degenerate FormatInfo, a sane mip configuration and a successful
  // TextureInfo::Prepare (fable_texture_fetch.cpp:211-271). Identifying which
  // tracked resource the GPU samples requires none of that — on Xenos the
  // TFETCH samples the fetch constant's base, so the base IS the Xbox
  // semantic. Gating identification on our ability to snapshot the texture
  // made every world texture whose shape or format we cannot yet decode
  // unresolvable, AND indistinguishable in the null log below from a slot the
  // guest never bound: both printed `fetch_base=0x00000000`. The reject stage
  // is now named there instead.
  const uint32_t fetch_base =
      (fetch_info.valid && fetch_info.base_address != 0)
          ? fetch_info.base_address
          : FableTextureFetchIdentityBaseFromDwords(fetch_dwords);
  if (fetch_base != 0) {
    PlumeTrackedResource* base_match = FindDirectResourceLocked(fetch_base);
    if (usable(base_match)) {
      ++resolver_fetch_hits;
      report_census();
      return base_match;
    }
    // Second route on the same base: the base->objects identity map. That is
    // the correctly-keyed lookup the GTL packet builder (:53880) and the
    // packet<->command identity predicate (:53240) both use;
    // FindDirectResourceLocked answers a different question and has returned a
    // device-adjacent fetch-slot pseudo-object for a base before (run 210).
    //
    // UNAMBIGUOUS ONLY. A base naming several objects is precisely the case
    // that would bind the WRONG texture, and a wrong texture is worse than a
    // missing one — the same rule NormalizeDirectTextureSlotsToObjectsLocked
    // states at :54092. Leave the ambiguous case to the null= alarm.
    if (const std::vector<uint32_t>* base_objects =
            FableDirectFindTextureIdentityObjectsByBase(fetch_base)) {
      if (base_objects->size() == 1u) {
        PlumeTrackedResource* object_match =
            FindDirectResourceLocked(base_objects->front());
        if (usable(object_match)) {
          ++resolver_fetch_hits;
          report_census();
          return object_match;
        }
      }
    }
  }
  ++resolver_nulls;
  report_census();
  // Unresolved bind: register plain address interest so the next sync
  // materializes the record (address-driven — the game bound it, so it must
  // exist; no identity blessing required). The old resolver's side effects
  // performed this materialization pressure implicitly.
  if (g_direct_renderer.deferred_resolve_interest.size() < 256u) {
    if (guest != 0) {
      g_direct_renderer.deferred_resolve_interest.insert(guest);
    }
    if (fetch_base != 0) {
      g_direct_renderer.deferred_resolve_interest.insert(fetch_base);
      // A BARE BASE CAN NEVER MINT (2026-08-03). On-demand materialization
      // decodes a texture header at `address + 28` and is guarded by
      // IsLikelyFableObjectAddress (hook_observer.cpp on-demand texture funnel;
      // plume_fable_addressing.cpp:34 requires >= 0x40000000). A Xenos fetch
      // base is a PHYSICAL address well below that window, so registering it
      // alone only ever matches a record that already exists — for an
      // unregistered world texture that half of the interest is inert.
      // Register the OBJECTS the base names as well: those are real header
      // addresses in the object window and can therefore be minted.
      if (const std::vector<uint32_t>* base_objects =
              FableDirectFindTextureIdentityObjectsByBase(fetch_base)) {
        for (const uint32_t object : *base_objects) {
          if (object == 0 ||
              g_direct_renderer.deferred_resolve_interest.size() >= 256u) {
            continue;
          }
          g_direct_renderer.deferred_resolve_interest.insert(object);
        }
      }
    }
  }
  {
    // Reject-reason probe: which usable() gate nulls a visible bind. Sampled
    // (every 32nd after the first 48) so late phases — post-title scene
    // draws, in-game loads — still surface their reject flags.
    static uint32_t logged_resolver_null_reasons = 0;
    static uint64_t resolver_null_probe_events = 0;
    ++resolver_null_probe_events;
    if (guest != 0 && (logged_resolver_null_reasons < 48u ||
                       (resolver_null_probe_events % 32u) == 0u)) {
      PlumeTrackedResource* exact = FindDirectResourceLocked(guest);
      // NAME THE STAGE THAT FAILED (2026-08-03). `fetch_base=0` used to mean
      // three different things at once — no constant written, a constant our
      // snapshot decoder rejected, or a decoded base with no record — and the
      // three demand completely different fixes. Print the raw dword, the
      // identity base, the decoder's own reject reason, and how many objects
      // the base names, so one run separates them.
      const TextureFetchSnapshotInfo fetch_snapshot =
          GetTextureFetchSnapshotInfo(fetch_dwords);
      const std::vector<uint32_t>* base_objects =
          fetch_base != 0
              ? FableDirectFindTextureIdentityObjectsByBase(fetch_base)
              : nullptr;
      REXLOG_INFO(
          "Refii plume resolver null reason #{} serial={} slot={} "
          "guest=0x{:08X} tracked={} image={} ready={} depth={} "
          "bound_as_target={} rt0=0x{:08X} ds=0x{:08X} fetch_base=0x{:08X} "
          "fetch_raw1=0x{:08X} fetch_decode={} base_objs={} src={}",
          ++logged_resolver_null_reasons,
          static_cast<unsigned long long>(command.serial), slot, guest,
          exact ? 1 : 0, exact && IsImageResource(exact->kind) ? 1 : 0,
          exact && PlumeGuestImageReady(*exact) ? 1 : 0,
          exact && exact->texture_depth_target ? 1 : 0,
          exact && DirectCommandBindsResourceAsTargetLocked(command, *exact)
              ? 1
              : 0,
          command.resources.render_targets[0],
          command.resources.depth_stencil, fetch_base, fetch_dwords[1],
          fetch_snapshot.failure_reason ? fetch_snapshot.failure_reason : "ok",
          base_objects != nullptr ? static_cast<uint32_t>(base_objects->size())
                                  : 0u,
          static_cast<uint32_t>(command.draw.source));
    }
  }
  return nullptr;
}


bool IsFableTitleSceneTextureSyncCommandLocked(
    const FableDirectRenderCommand& command) {
  if (command.draw.source == RendererDrawEventSource::kPixVideo) {
    return false;
  }
  return IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
         IsFableTitleSceneVertexShaderCommandLocked(command);
}

bool DirectPrivateTextureCopyActionProvidesSampleLocked(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action) {
  if (action.kind == FablePrivateTextureCopyKind::kDepth) {
    return false;
  }

  if (ResolveDirectRegisteredSampleSourceLocked(command,
                                                action.sampled_source)) {
    return true;
  }

  if (DirectPrivateTextureCopyKindUsesColorBacking(action.kind) &&
      action.source_slot < command.resources.textures.size() &&
      DirectSyntheticPrivateRectColorActionCanExecuteLocked(
          command, action.source_slot,
          FindDirectResourceLocked(
              command.resources.textures[action.source_slot]))) {
    return true;
  }

  auto usable_sample = [&](uint32_t address) {
    const PlumeTrackedResource* resource = FindDirectResourceLocked(address);
    return resource && IsImageResource(resource->kind) &&
           PlumeGuestImageReady(*resource) &&
           (FableTrackedResourceHasSampleableContent(*resource) ||
            FableTrackedResourceHasGpuProducedContent(*resource)) &&
           !FableTrackedResourceIsBlankUploadedSampleProxy(*resource) &&
           !DirectCommandBindsResourceAsTargetLocked(command, *resource);
  };

  return usable_sample(action.backing_texture) ||
         usable_sample(action.target_surface) ||
         usable_sample(action.render_target);
}

bool DirectSyntheticPrivateRectCopyHasRegisteredInputsLocked(
    const FableDirectRenderCommand& command) {
  if (command.draw.source != RendererDrawEventSource::kPrivateTextureRectangle ||
      !command.draw.private_texture_rectangle ||
      !IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader)) {
    return false;
  }

  const uint32_t texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(command);
  bool saw_bound_texture = false;
  for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
    if (!DirectCommandUsesTextureSlotForSyncLocked(command, texture_slot_mask,
                                                   slot)) {
      continue;
    }
    const uint32_t guest = command.resources.textures[slot];
    if (guest == 0) {
      continue;
    }
    saw_bound_texture = true;

    PlumeTrackedResource* direct_texture = FindDirectResourceLocked(guest);
    if (DirectCommandCanSnapshotPrivateTextureFeedbackLocked(
            command, slot, direct_texture) ||
        DirectCommandCanBindMaterializedPrivateTextureHeaderLocked(
            command, slot, direct_texture) ||
        DirectCommandCanBootstrapRegisteredPrivateRectMaterializationLocked(
            command, slot, direct_texture)) {
      continue;
    }

    if (DirectCommandTextureSlotExactProducerProvidesSampleLocked(
            command, slot, direct_texture)) {
      continue;
    }

    PlumeTrackedResource* sampled =
        ResolveDirectSampledTextureForCommandLocked(command, slot);
    if (!direct_texture) {
      direct_texture = sampled;
    }
    if (direct_texture &&
        DirectCommandCanBindMaterializedPrivateTextureHeaderLocked(
            command, slot, direct_texture)) {
      continue;
    }

    return false;
  }

  return saw_bound_texture;
}

bool DirectSyntheticPrivateRectDepthActionCanExecuteLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* direct_texture) {
  if (!DirectSyntheticPrivateRectCommandUsesDepthTarget(command) ||
      slot >= command.resources.textures.size()) {
    return false;
  }

  const uint32_t depth_stencil = command.resources.depth_stencil;
  if (depth_stencil == 0) {
    return false;
  }

  PlumeTrackedResource* depth_target = FindDirectResourceLocked(depth_stencil);
  if (!depth_target || !IsImageResource(depth_target->kind) ||
      !PlumeGuestImageReady(*depth_target) ||
      !depth_target->texture_depth_target ||
      !DirectCommandBindsResourceAsTargetLocked(command, *depth_target)) {
    return false;
  }

  const uint64_t execute_cache_key =
      DirectRegisteredPrivateRectScaledDecisionCacheKey(command, *depth_target,
                                                        slot);
  if (DirectRegisteredPrivateRectExecuteDecisionCacheHit(execute_cache_key)) {
    return true;
  }

  const FablePrivateTextureCopyAction* action =
      FindDirectPrivateRectMaterializationActionLocked(command, slot,
                                                       direct_texture,
                                                       *depth_target);
  if (!action || action->kind != FablePrivateTextureCopyKind::kDepth ||
      !DirectPrivateTextureCopyActionCommandSlotMatches(command, *action)) {
    return false;
  }

  if ((!DirectResourceAddressMatchesLocked(action->target_surface,
                                           depth_stencil) &&
       !DirectResourceAddressMatchesLocked(action->depth_stencil,
                                           depth_stencil))) {
    return false;
  }

  if (action->pixel_shader != 0 &&
      !DirectPrivateRectSyntheticPixelShadersCompatible(
          command.resources.pixel_shader, action->pixel_shader, action->kind) &&
      !DirectPixelShaderObjectsEquivalentLocked(command.resources.pixel_shader,
                                                action->pixel_shader)) {
    return false;
  }
  if (action->vertex_shader != 0 &&
      !DirectVertexShaderObjectsEquivalentLocked(command.resources.vertex_shader,
                                                 action->vertex_shader)) {
    return false;
  }

  RememberDirectRegisteredPrivateRectExecuteDecision(execute_cache_key);

  static uint32_t logged_exact_depth_private_rect_surface_texture_identities = 0;
  if (logged_exact_depth_private_rect_surface_texture_identities < 32u) {
    REXLOG_INFO(
        "Refii plume exact depth private-rect surface-texture identity #{} frame={} "
        "serial={} draw={} slot={} guest=0x{:08X} target=0x{:08X}/{}x{} "
        "fmt={} action_source=0x{:08X} action_target=0x{:08X} "
        "action_ds=0x{:08X} action_draw={} rt=0x{:08X} ds=0x{:08X} "
        "vs=0x{:08X} ps=0x{:08X}",
        ++logged_exact_depth_private_rect_surface_texture_identities,
        g_direct_renderer.current_frame_index,
        static_cast<unsigned long long>(command.serial),
        static_cast<unsigned long long>(command.draw_serial), slot,
        command.resources.textures[slot], depth_target->guest_address,
        PlumeGuestTextureWidth(*depth_target),
        PlumeGuestTextureHeight(*depth_target),
        static_cast<uint32_t>(PlumeGuestTextureFormat(*depth_target)),
        action->source_texture, action->target_surface, action->depth_stencil,
        static_cast<unsigned long long>(action->draw_serial),
        command.resources.render_targets[0], command.resources.depth_stencil,
        command.resources.vertex_shader, command.resources.pixel_shader);
  }

  return true;
}

bool DirectSyntheticPrivateRectColorActionCanExecuteLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* direct_texture) {
  if (DirectSyntheticPrivateRectCommandUsesDepthTarget(command) ||
      slot >= command.resources.textures.size() ||
      command.draw.source != RendererDrawEventSource::kPrivateTextureRectangle ||
      !command.draw.private_texture_rectangle ||
      !IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader)) {
    return false;
  }

  const uint32_t color_target_address = command.resources.render_targets[0];
  if (color_target_address == 0) {
    return false;
  }

  PlumeTrackedResource* color_target =
      FindDirectResourceLocked(color_target_address);
  if (!color_target || !IsImageResource(color_target->kind) ||
      !PlumeGuestImageReady(*color_target) ||
      !color_target->texture_render_target ||
      color_target->texture_depth_target ||
      !DirectCommandBindsResourceAsTargetLocked(command, *color_target)) {
    return false;
  }

  const uint64_t execute_cache_key =
      DirectRegisteredPrivateRectScaledDecisionCacheKey(command, *color_target,
                                                        slot);
  if (DirectRegisteredPrivateRectExecuteDecisionCacheHit(execute_cache_key)) {
    return true;
  }

  const uint64_t command_draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  auto exact_color_action_can_execute =
      [&](const FablePrivateTextureCopyAction* candidate)
          -> const FablePrivateTextureCopyAction* {
    if (!candidate ||
        (candidate->kind != FablePrivateTextureCopyKind::kColor &&
         candidate->kind != FablePrivateTextureCopyKind::kColorSibling) ||
        !DirectPrivateTextureCopyActionCommandSlotMatches(command,
                                                          *candidate)) {
      return nullptr;
    }

    const bool action_targets_current_color =
        DirectResourceAddressMatchesLocked(candidate->target_surface,
                                           color_target->guest_address) ||
        DirectResourceAddressMatchesLocked(candidate->render_target,
                                           color_target->guest_address) ||
        DirectResourceAddressMatchesLocked(candidate->target_surface,
                                           color_target_address) ||
        DirectResourceAddressMatchesLocked(candidate->render_target,
                                           color_target_address);
    if (!action_targets_current_color) {
      return nullptr;
    }

    const bool same_registered_draw =
        candidate->draw_serial != 0 &&
        candidate->draw_serial == command_draw_serial;
    if (!same_registered_draw &&
        !DirectPrivateTextureCopyActionContextMatches(command, *candidate)) {
      return nullptr;
    }

    if (candidate->pixel_shader != 0 &&
        !DirectPrivateRectSyntheticPixelShadersCompatible(
            command.resources.pixel_shader, candidate->pixel_shader,
            candidate->kind) &&
        !DirectPixelShaderObjectsEquivalentLocked(command.resources.pixel_shader,
                                                  candidate->pixel_shader)) {
      return nullptr;
    }
    if (candidate->vertex_shader != 0 &&
        !DirectVertexShaderObjectsEquivalentLocked(command.resources.vertex_shader,
                                                   candidate->vertex_shader)) {
      return nullptr;
    }
    return candidate;
  };

  const FablePrivateTextureCopyAction* action = nullptr;
  action = exact_color_action_can_execute(
      FindDirectPrivateRectMaterializationActionLocked(
          command, slot, direct_texture, *color_target));
  if (!action) {
    return false;
  }

  RememberDirectRegisteredPrivateRectExecuteDecision(execute_cache_key);
  if (direct_texture && IsImageResource(direct_texture->kind) &&
      PlumeGuestImageReady(*direct_texture) &&
      !direct_texture->texture_depth_target &&
      direct_texture->guest_address == command.resources.textures[slot]) {
    const uint64_t target_content = FableTrackedResourceVisibleContentSerial(
        *color_target);
    if (target_content != 0) {
      MarkDirectResourceGpuProducedContentLocked(direct_texture->guest_address,
                                                target_content);
    }
  }

  static uint32_t logged_exact_color_private_rect_surface_texture_identities = 0;
  if (logged_exact_color_private_rect_surface_texture_identities < 32u) {
    REXLOG_INFO(
        "Refii plume exact color private-rect surface-texture identity #{} frame={} "
        "serial={} draw={} slot={} guest=0x{:08X} target=0x{:08X}/{}x{} "
        "fmt={} action_source=0x{:08X} action_target=0x{:08X} "
        "action_rt=0x{:08X} action_draw={} rt=0x{:08X} ds=0x{:08X} "
        "vs=0x{:08X} ps=0x{:08X}",
        ++logged_exact_color_private_rect_surface_texture_identities,
        g_direct_renderer.current_frame_index,
        static_cast<unsigned long long>(command.serial),
        static_cast<unsigned long long>(command.draw_serial), slot,
        command.resources.textures[slot], color_target->guest_address,
        PlumeGuestTextureWidth(*color_target),
        PlumeGuestTextureHeight(*color_target),
        static_cast<uint32_t>(PlumeGuestTextureFormat(*color_target)),
        action->source_texture, action->target_surface, action->render_target,
        static_cast<unsigned long long>(action->draw_serial),
        command.resources.render_targets[0], command.resources.depth_stencil,
        command.resources.vertex_shader, command.resources.pixel_shader);
  }

  return true;
}

bool DirectSyntheticPrivateRectCopyCanExecuteFromConcreteSourceLocked(
    const FableDirectRenderCommand& command) {
  if (command.draw.source != RendererDrawEventSource::kPrivateTextureRectangle ||
      !command.draw.private_texture_rectangle ||
      !IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader)) {
    return false;
  }

  const uint32_t target_address =
      DirectSyntheticPrivateRectCommandUsesDepthTarget(command)
          ? command.resources.depth_stencil
          : command.resources.render_targets[0];
  PlumeTrackedResource* target =
      target_address != 0 ? FindDirectResourceLocked(target_address) : nullptr;

  const uint32_t texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(command);
  for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
    if (!DirectCommandUsesTextureSlotForSyncLocked(command, texture_slot_mask,
                                                   slot)) {
      continue;
    }

    const uint32_t guest = command.resources.textures[slot];
    if (guest == 0) {
      continue;
    }

    PlumeTrackedResource* direct_texture = FindDirectResourceLocked(guest);
    const uint64_t execute_cache_key =
        target && IsImageResource(target->kind) && PlumeGuestImageReady(*target)
            ? DirectRegisteredPrivateRectScaledDecisionCacheKey(command,
                                                                *target, slot)
            : 0u;
    if (execute_cache_key != 0 &&
        DirectRegisteredPrivateRectExecuteDecisionCacheHit(execute_cache_key)) {
      return true;
    }
    if (DirectSyntheticPrivateRectDepthActionCanExecuteLocked(
            command, slot, direct_texture)) {
      if (execute_cache_key != 0) {
        RememberDirectRegisteredPrivateRectExecuteDecision(execute_cache_key);
      }
      return true;
    }
    if (DirectSyntheticPrivateRectColorActionCanExecuteLocked(
            command, slot, direct_texture)) {
      if (execute_cache_key != 0) {
        RememberDirectRegisteredPrivateRectExecuteDecision(execute_cache_key);
      }
      return true;
    }

    if (DirectCommandCanSnapshotPrivateTextureFeedbackLocked(
            command, slot, direct_texture)) {
      return false;
    }

    if (PlumeTrackedResource* exact_sample =
            ResolveDirectCommandTextureSlotExactProducerLocked(
                command, slot, direct_texture)) {
      if (DirectCommandTextureSlotDirectResourceProvidesSampleLocked(
              command, slot, exact_sample, true)) {
        if (execute_cache_key != 0) {
          RememberDirectRegisteredPrivateRectExecuteDecision(execute_cache_key);
        }
        return true;
      }
    }
  }

  return false;
}

bool DirectCommandHasExactSyntheticPrivateRectCopyInputsLocked(
    const FableDirectRenderCommand& command) {
  if (command.draw.source != RendererDrawEventSource::kPrivateTextureRectangle ||
      !command.draw.private_texture_rectangle ||
      !IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader)) {
    return false;
  }
  return DirectSyntheticPrivateRectCopyHasRegisteredInputsLocked(command) ||
         DirectSyntheticPrivateRectCopyCanExecuteFromConcreteSourceLocked(command);
}

uint64_t DirectRegisteredPrivateRectScaledDecisionCacheKey(
    const FableDirectRenderCommand& command, const PlumeTrackedResource& target,
    uint32_t slot) {
  uint64_t key = 1469598103934665603ull;
  const auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };
  const uint32_t guest =
      slot < command.resources.textures.size() ? command.resources.textures[slot]
                                               : 0u;
  PlumeTrackedResource* source =
      guest != 0 ? FindDirectResourceLocked(guest) : nullptr;
  mix(slot);
  mix(guest);
  if (source) {
    mix(source->guest_address);
    mix(source->parent_address);
    mix(PlumeGuestTextureBaseAddress(*source));
    mix(PlumeGuestTextureWidth(*source));
    mix(PlumeGuestTextureHeight(*source));
    mix(PlumeGuestTextureGuestFormat(*source));
  }
  mix(FableDirectTextureFetchKey(DirectCommandTextureFetchDwords(command, slot)));
  mix(command.resources.render_targets[0]);
  mix(command.resources.depth_stencil);
  mix(command.resources.vertex_shader);
  mix(command.resources.pixel_shader);
  mix(target.guest_address);
  mix(target.parent_address);
  mix(PlumeGuestTextureBaseAddress(target));
  mix(PlumeGuestTextureWidth(target));
  mix(PlumeGuestTextureHeight(target));
  mix(PlumeGuestTextureGuestFormat(target));
  mix(static_cast<uint32_t>(command.draw.source));
  mix(command.draw.private_texture_rectangle ? 1u : 0u);
  mix(command.draw.private_texture_uv_override ? 1u : 0u);
  return key != 0 ? key : 1ull;
}

bool DirectRegisteredPrivateRectExecuteDecisionCacheHit(uint64_t cache_key) {
  const auto cached_it =
      g_direct_renderer.registered_private_rect_execute_decision_cache.find(
          cache_key);
  if (cached_it ==
      g_direct_renderer.registered_private_rect_execute_decision_cache.end()) {
    return false;
  }
  const DirectRegisteredPrivateRectExecuteDecisionCacheEntry& entry =
      cached_it->second;
  if (entry.private_copy_serial != DirectPrivateTextureCopySerialLocked()) {
    g_direct_renderer.registered_private_rect_execute_decision_cache.erase(
        cached_it);
    return false;
  }
  return true;
}

void RememberDirectRegisteredPrivateRectExecuteDecision(uint64_t cache_key) {
  constexpr size_t kMaxExecuteDecisionCacheEntries = 4096u;
  if (g_direct_renderer.registered_private_rect_execute_decision_cache.size() >=
      kMaxExecuteDecisionCacheEntries) {
    g_direct_renderer.registered_private_rect_execute_decision_cache.clear();
  }
  g_direct_renderer.registered_private_rect_execute_decision_cache[cache_key] = {
      DirectPrivateTextureCopySerialLocked()};
}

std::unique_ptr<plume::RenderBuffer> CreateUploadBufferWithData(
    plume::RenderDevice* device, const void* data, size_t size,
    const char* name,
    plume::RenderBufferFlags flags) {
  if (!device || !data || size == 0) {
    return nullptr;
  }
  auto buffer = device->createBuffer(
      plume::RenderBufferDesc::UploadBuffer(static_cast<uint64_t>(size),
                                            flags));
  if (!buffer) {
    return nullptr;
  }
  if (name) {
    buffer->setName(name);
  }
  if (void* mapped = buffer->map()) {
    std::memcpy(mapped, data, size);
    plume::RenderRange written_range(0, size);
    buffer->unmap(0, &written_range);
  }
  return buffer;
}

// ---------------------------------------------------------------------------
// Per-frame shader-constant arena
// ---------------------------------------------------------------------------
// Satisfies both consumers: D3D12 constant-buffer views require 256-byte
// alignment, and Vulkan minUniformBufferOffsetAlignment is at most 256 on every
// desktop GPU. Aligning every allocation to 256 keeps one rule for both paths.

size_t AlignDirectConstantOffset(size_t offset) {
  return (offset + (kDirectConstantArenaAlignment - 1u)) &
         ~(kDirectConstantArenaAlignment - 1u);
}

// Map a chunk for CPU writes. Chunks stay mapped for the duration of a frame
// and are unmapped (which is what flushes the written range through VMA) in
// FlushDirectConstantArenaLocked at end of frame.
bool EnsureDirectConstantChunkMappedLocked(DirectConstantArenaChunk& chunk) {
  if (chunk.mapped != nullptr) {
    return true;
  }
  if (!chunk.buffer) {
    return false;
  }
  chunk.mapped = static_cast<uint8_t*>(chunk.buffer->map());
  return chunk.mapped != nullptr;
}

// Bump-allocate `size` bytes of constant data out of the active frame slot,
// growing by whole chunks. Returns the buffer plus the offset to bind at.
DirectConstantAllocation AllocateDirectConstantsLocked(
    plume::RenderDevice* device, const void* data, size_t size) {
  DirectConstantAllocation allocation;
  if (!device || !data || size == 0) {
    return allocation;
  }
  DirectConstantArenaSlot& slot =
      g_direct_renderer
          .constant_arena_slots[g_direct_renderer.constant_arena_active_slot];

  // A single constant payload larger than a chunk would never fit; the banks
  // are 4 KiB so this is a guard, not a path we expect to take.
  const size_t chunk_bytes =
      std::max(kDirectConstantArenaChunkBytes,
               AlignDirectConstantOffset(size) + kDirectConstantArenaAlignment);

  for (;;) {
    if (slot.current_chunk < slot.chunks.size()) {
      DirectConstantArenaChunk& chunk = slot.chunks[slot.current_chunk];
      const size_t offset = AlignDirectConstantOffset(chunk.used);
      if (offset + size <= chunk.capacity) {
        if (!EnsureDirectConstantChunkMappedLocked(chunk)) {
          return allocation;
        }
        std::memcpy(chunk.mapped + offset, data, size);
        chunk.used = offset + size;
        allocation.buffer = chunk.buffer.get();
        allocation.device_address = chunk.device_address + offset;
        allocation.offset = static_cast<uint32_t>(offset);
        g_direct_renderer.constant_arena_bytes_this_frame +=
            static_cast<uint32_t>(size);
        ++g_direct_renderer.constant_arena_allocs_this_frame;
        return allocation;
      }
      // This chunk is full -- advance and try the next one.
      ++slot.current_chunk;
      continue;
    }

    // Out of chunks in this slot: grow it. Chunks are created once and then
    // recycled every kDirectConstantArenaSlots frames, so this settles after
    // the first few frames at a steady-state working set.
    DirectConstantArenaChunk chunk;
    chunk.buffer = device->createBuffer(plume::RenderBufferDesc::UploadBuffer(
        chunk_bytes, plume::RenderBufferFlag::CONSTANT |
                         plume::RenderBufferFlag::DEVICE_ADDRESSABLE));
    if (!chunk.buffer) {
      return allocation;
    }
    chunk.capacity = chunk_bytes;
    chunk.used = 0;
    chunk.device_address = chunk.buffer->getDeviceAddress();
    slot.chunks.emplace_back(std::move(chunk));
    ++g_direct_renderer.constant_arena_chunks_created;
  }
}

// Called at frame start. The slot we are about to reuse was last written
// kDirectConstantArenaSlots frames ago, and plume has already waited on that
// frame context's fence, so reusing its memory cannot race the GPU.
void ResetDirectConstantArenaForFrameLocked(uint32_t frame_index) {
  g_direct_renderer.constant_arena_active_slot =
      static_cast<size_t>(frame_index % kDirectConstantArenaSlots);
  DirectConstantArenaSlot& slot =
      g_direct_renderer
          .constant_arena_slots[g_direct_renderer.constant_arena_active_slot];
  slot.current_chunk = 0;
  for (DirectConstantArenaChunk& chunk : slot.chunks) {
    chunk.used = 0;
  }
  g_direct_renderer.constant_arena_bytes_this_frame = 0;
  g_direct_renderer.constant_arena_allocs_this_frame = 0;
}

// Called at frame end. Unmapping is what flushes the written range through VMA,
// so this is the correctness half of the persistent-map optimisation, not just
// cleanup.
void FlushDirectConstantArenaLocked() {
  DirectConstantArenaSlot& slot =
      g_direct_renderer
          .constant_arena_slots[g_direct_renderer.constant_arena_active_slot];
  for (DirectConstantArenaChunk& chunk : slot.chunks) {
    if (chunk.mapped == nullptr || !chunk.buffer) {
      continue;
    }
    const plume::RenderRange written_range(0, chunk.used);
    chunk.buffer->unmap(0, chunk.used != 0 ? &written_range : nullptr);
    chunk.mapped = nullptr;
  }
}

// Release every arena chunk. Chunks are device allocations and may still be
// mapped, and VMA asserts on destroying a mapped allocation (and on leaked
// blocks at device teardown), so unmap before dropping the buffer.
void DestroyDirectConstantArenaLocked() {
  for (DirectConstantArenaSlot& slot : g_direct_renderer.constant_arena_slots) {
    for (DirectConstantArenaChunk& chunk : slot.chunks) {
      if (chunk.mapped != nullptr && chunk.buffer) {
        chunk.buffer->unmap(0, nullptr);
      }
      chunk.mapped = nullptr;
      chunk.buffer.reset();
    }
    slot.chunks.clear();
    slot.current_chunk = 0;
  }
  g_direct_renderer.constant_arena_active_slot = 0;
  g_direct_renderer.constant_arena_bytes_this_frame = 0;
  g_direct_renderer.constant_arena_allocs_this_frame = 0;
  g_direct_renderer.constant_arena_chunks_created = 0;
}

void TrimCachedUploadBuffersLocked() {
  constexpr uint32_t kMaxCachedUploadBuffers = 4096u;
  constexpr uint32_t kTargetCachedUploadBuffers = 3072u;
  constexpr uint64_t kCachedUploadFrameLifetime = 900u;
  if (g_direct_renderer.cached_upload_buffer_count <= kMaxCachedUploadBuffers) {
    return;
  }
  // Trim at most once per frame.
  //
  // Entries only expire at current_frame - 900, so once the cache passes its
  // high-water mark the loop below can rarely reach kTargetCachedUploadBuffers:
  // it walks the ENTIRE map, erases little or nothing, and returns -- and the
  // count never drops back under the early-out above, so the next miss walked
  // the whole map again. That made eviction cost O(map) PER MISS against a map
  // that only grows, which is both a large per-draw cost and the reason frame
  // time drifted upward at constant draw count.
  //
  // This changes only how OFTEN the sweep runs, never which entries it evicts,
  // so eviction semantics (and the GPU-lifetime guarantee the 900-frame window
  // provides) are untouched.
  if (g_direct_renderer.cached_upload_buffer_trim_frame ==
      g_direct_renderer.current_frame_index) {
    return;
  }
  g_direct_renderer.cached_upload_buffer_trim_frame =
      g_direct_renderer.current_frame_index;

  const uint64_t oldest_live_frame =
      g_direct_renderer.current_frame_index > kCachedUploadFrameLifetime
          ? g_direct_renderer.current_frame_index - kCachedUploadFrameLifetime
          : 0u;
  for (auto bucket_it = g_direct_renderer.cached_upload_buffers.begin();
       bucket_it != g_direct_renderer.cached_upload_buffers.end() &&
       g_direct_renderer.cached_upload_buffer_count > kTargetCachedUploadBuffers;) {
    auto& bucket = bucket_it->second;
    bucket.erase(
        std::remove_if(bucket.begin(), bucket.end(),
                       [&](const DirectCachedUploadBuffer& entry) {
                         const bool erase =
                             entry.last_used_frame < oldest_live_frame;
                         if (erase &&
                             g_direct_renderer.cached_upload_buffer_count != 0) {
                           --g_direct_renderer.cached_upload_buffer_count;
                         }
                         return erase;
                       }),
        bucket.end());
    if (bucket.empty()) {
      bucket_it = g_direct_renderer.cached_upload_buffers.erase(bucket_it);
    } else {
      ++bucket_it;
    }
  }
}

// Hit-only probe of the cached-upload map: no bucket insertion, no name
// formatting — callers that expect a hit (the stamped-hash inline-VB path,
// the regenerated private-rect quads) pay only the map find plus the
// collision-discriminating memcmp, and build their debug name only when the
// miss path actually creates a buffer.
plume::RenderBuffer* FindCachedUploadBufferForKeyLocked(
    const DirectUploadBufferKey& key, const void* data, size_t size) {
  auto bucket_it = g_direct_renderer.cached_upload_buffers.find(key);
  if (bucket_it == g_direct_renderer.cached_upload_buffers.end()) {
    return nullptr;
  }
  for (DirectCachedUploadBuffer& entry : bucket_it->second) {
    if (entry.bytes.size() == size &&
        std::memcmp(entry.bytes.data(), data, size) == 0) {
      entry.last_used_frame = g_direct_renderer.current_frame_index;
      return entry.buffer.get();
    }
  }
  return nullptr;
}

// Keyed inner body of GetOrCreateCachedUploadBufferWithDataLocked: the caller
// supplies the once-computed content key so memoized paths (the inline-VB bind
// memo) never re-hash an unchanged payload. The bucket memcmp stays — it is
// what discriminates true hash collisions within a bucket.
plume::RenderBuffer* GetOrCreateCachedUploadBufferForKeyLocked(
    plume::RenderDevice* device, const DirectUploadBufferKey& key,
    const void* data, size_t size, const char* name,
    plume::RenderBufferFlags flags) {
  if (plume::RenderBuffer* found =
          FindCachedUploadBufferForKeyLocked(key, data, size)) {
    return found;
  }

  auto buffer = CreateUploadBufferWithData(device, data, size, name, flags);
  if (!buffer) {
    return nullptr;
  }

  DirectCachedUploadBuffer entry;
  entry.bytes.assign(static_cast<const uint8_t*>(data),
                     static_cast<const uint8_t*>(data) + size);
  entry.last_used_frame = g_direct_renderer.current_frame_index;
  entry.buffer = std::move(buffer);
  plume::RenderBuffer* result = entry.buffer.get();
  g_direct_renderer.cached_upload_buffers[key].emplace_back(std::move(entry));
  ++g_direct_renderer.cached_upload_buffer_count;
  TrimCachedUploadBuffersLocked();
  return result;
}

plume::RenderBuffer* GetOrCreateCachedUploadBufferWithDataLocked(
    plume::RenderDevice* device, const void* data, size_t size,
    const char* name,
    plume::RenderBufferFlags flags) {
  constexpr size_t kMaxCachedUploadBytes = 64u * 1024u;
  if (!device || !data || size == 0 || size > kMaxCachedUploadBytes) {
    return nullptr;
  }

  const DirectUploadBufferKey key{
      DirectHashBytes(data, size), static_cast<uint32_t>(size), flags};
  return GetOrCreateCachedUploadBufferForKeyLocked(device, key, data, size,
                                                   name, flags);
}

void RetainFrameUploadBuffersLocked(
    std::vector<std::unique_ptr<plume::RenderBuffer>> buffers) {
  for (auto& buffer : buffers) {
    if (buffer) {
      g_direct_renderer.frame_upload_buffers.emplace_back(std::move(buffer));
    }
  }
  if (g_direct_renderer.frame_upload_buffers.size() > 16384u) {
    g_direct_renderer.frame_upload_buffers.erase(
        g_direct_renderer.frame_upload_buffers.begin(),
        g_direct_renderer.frame_upload_buffers.begin() +
            (g_direct_renderer.frame_upload_buffers.size() - 12288u));
  }
}

// T3 execution census (kGtlTessDraw). Run-cumulative, reported by the
// tessdraw[...] line in the executor (first executions then every 64th).
GtlTessDrawStats g_gtl_tess_draw_stats;

// T3 tessellation-shape decode from the CAPTURED register shadows.
// Register map is the public Xenos/Xenia one (register_table.inc): the VGT
// tess block rides vgt_tess_regs[i] = reg 0x2280+i, so [5] VGT_HOS_CNTL,
// [6] VGT_HOS_MAX_TESS_LEVEL (kFloat), [7] VGT_HOS_MIN_TESS_LEVEL (kFloat),
// [11] VGT_GROUP_DECR. Host rule: N = guest level + 1.0 (Xenia SDK
// d3d12/command_processor.cpp:3790 adds 1.0 to every tess factor before the
// host tessellator).
//   mode 2 (adaptive/per-edge, terrain): the DMA'd per-edge factors are
//     CPU-prefilled all-1.0f (sub_82A8ED70) and our guest-RAM uploads never
//     rewrite them, so every edge is clamp(1.0, min, max) -> host
//     clamp(1.0+1, min+1, max+1). Terrain ships min=max=1.0 => N=2 uniform.
//   mode 1 (continuous, water): N = max+1 (15.0+1 => 16).
//   mode 0 (discrete)/other: same max+1 rule; counted in n_other.
// Patch count: num_indices (VGT_DRAW_INITIATOR bits 31:16) divided by
// VGT_GROUP_DECR — in per-edge mode each patch consumes GROUP_DECR(=4)
// factor "indices" (the factor DMA is NOT vertex data), else DECR=1 and
// indices==patches. The emitters chunk >0xFFFE draws across several
// DRAW_INDX packets, in which case the first initiator undercounts and the
// captured call arg (terrain r7 / water r6 = patches) is the total.
void DecodeGtlTessDrawShape(const FableDirectGtlTessDrawCommand& tess,
                            uint32_t& n_out, uint32_t& patches_out,
                            uint32_t& mode_out) {
  const uint32_t mode =
      tess.vgt_regs_valid ? (tess.vgt_tess_regs[5] & 3u) : 0u;
  const float max_level =
      tess.vgt_regs_valid ? std::bit_cast<float>(tess.vgt_tess_regs[6]) : 0.0f;
  const float min_level =
      tess.vgt_regs_valid ? std::bit_cast<float>(tess.vgt_tess_regs[7]) : 0.0f;
  const auto sane_level = [](float level) {
    return level >= 0.0f && level <= 127.0f ? level : 0.0f;  // NaN/junk guard
  };
  const float lo = sane_level(std::min(min_level, max_level));
  const float hi = sane_level(std::max(min_level, max_level));
  float host_n;
  if (mode == 2u) {
    host_n = std::clamp(1.0f + 1.0f, lo + 1.0f, hi + 1.0f);
  } else {
    host_n = hi + 1.0f;
  }
  n_out = std::clamp(static_cast<uint32_t>(host_n), 1u, 128u);
  mode_out = mode;

  const uint32_t group_decr =
      tess.vgt_regs_valid ? std::max(1u, tess.vgt_tess_regs[11] & 0xFFFFu)
                          : 1u;
  const uint32_t initiator_patches =
      tess.draw_initiator != 0u ? (tess.draw_initiator >> 16u) / group_decr
                                : 0u;
  if (tess.draw_packet_count > 1u) {
    patches_out = tess.arg_count;  // chunked call: args carry the total
  } else if (initiator_patches != 0u) {
    patches_out = initiator_patches;
  } else {
    patches_out = tess.arg_count;  // ring capture failed/refused
  }
}

// The effective SharedConstants +564 value: the hardware VGT_INDX_OFFSET when
// programmed, else (auto-index/water only) the call's start-vertex argument —
// the sub-quad quadrant offset the guest passes per emit.
uint32_t GtlTessIndexOffset(const FableDirectGtlTessDrawCommand& tess) {
  if (tess.vgt_indx_offset != 0u) {
    return tess.vgt_indx_offset;
  }
  return tess.indexed ? 0u : tess.arg_index_offset;
}

void FillDirectSharedConstants(const FableDirectRenderCommand& command,
                               const DirectFramebufferBinding& framebuffer,
                               std::array<uint8_t,
                                          kDirectGeneratedSharedConstantsBytes>&
                                   shared) {
  shared.fill(0);
  auto store_u32 = [&](uint32_t byte_offset, uint32_t value) {
    if (byte_offset + sizeof(uint32_t) <= shared.size()) {
      std::memcpy(shared.data() + byte_offset, &value, sizeof(value));
    }
  };
  auto store_f32 = [&](uint32_t byte_offset, float value) {
    if (byte_offset + sizeof(float) <= shared.size()) {
      std::memcpy(shared.data() + byte_offset, &value, sizeof(value));
    }
  };

  const uint32_t title_gui_fetch_backed_slot =
      FableTitleGuiFetchBackedTextureSlotLocked(command);
  const uint32_t texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(command);
  const bool exact_bink_wrapper_draw =
      DirectCommandIsExactBinkIndexedWrapperDrawLocked(command);
  for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
    const bool exact_bink_alpha_mirror_slot =
        slot == 3u && exact_bink_wrapper_draw;
    const bool slot_used =
        slot >= 32u || (texture_slot_mask & (1u << slot)) != 0 ||
        exact_bink_alpha_mirror_slot;
    const bool slot0_has_native_texture =
        slot == 0u && slot < command.resources.textures.size() &&
        command.resources.textures[slot] != 0u;
    const bool title_gui_slot0_mirror =
        slot == 0u && !slot0_has_native_texture &&
        title_gui_fetch_backed_slot != UINT32_MAX &&
        title_gui_fetch_backed_slot < 32u &&
        (texture_slot_mask & (1u << title_gui_fetch_backed_slot)) != 0;
    if (!slot_used && !title_gui_slot0_mirror) {
      continue;
    }
    const uint32_t sampled_slot =
        exact_bink_alpha_mirror_slot
            ? 0u
            : (title_gui_slot0_mirror ? title_gui_fetch_backed_slot : slot);
    uint32_t sampler_state = command.sampler_states[sampled_slot];
    if (DirectCommandIsExactTitleBillboardAtlasLocked(command) &&
        sampled_slot >= 13u && sampled_slot <= 15u) {
      sampler_state = DirectPackedSamplerStateWithClampAddress(sampler_state);
    }
    if (DirectCommandIsTitleRepeatedMeshOverlayLocked(command) &&
        sampled_slot <= 1u) {
      sampler_state = DirectPackedSamplerStateWithClampAddress(sampler_state);
    }
    // The fetch constant's clamp fields are the console's authoritative
    // addressing for the binding; without this, bindings lacking packed
    // sampler state fell to the WRAP default sampler and bilinear pulled
    // texels across the u=0/1 edge — the visible hairline seams between
    // storybook tiles.
    {
      const std::array<uint32_t, 6> slot_fetch =
          DirectCommandTextureFetchDwords(command, sampled_slot);
      uint32_t fetch_state = DirectPackedSamplerStateWithFetchClampAddress(
          sampler_state, slot_fetch);
      // WRAP with UVs confined to [0,1] is indistinguishable from CLAMP on
      // console (at 1:1 texel:pixel the last pixel center lands exactly on
      // the edge texel, so wrap never blends). At scaled output resolutions
      // edge pixels DO sample across the wrap and paint hairline seams
      // between abutting tiles, so substitute the console-equivalent CLAMP
      // when the quad's UV range provably stays inside [0,1]. Storybook
      // panels use the 0x48-stride quad layout with texcoords at float
      // offset 12.
      if (command.inline_vertex_buffers[0].ready() &&
          command.inline_vertex_buffers[0].stride_or_format == 0x48u &&
          command.inline_vertex_buffers[0].byte_size == 4u * 0x48u) {
        const uint8_t *payload = command.inline_vertex_buffers[0].data.data();
        bool uv_within_unit = true;
        for (uint32_t vertex = 0; vertex < 4u && uv_within_unit; ++vertex) {
          for (uint32_t component = 0; component < 2u; ++component) {
            float value = 0.0f;
            std::memcpy(&value,
                        payload + vertex * 0x48u + 48u + component * 4u,
                        sizeof(value));
            if (!(value >= -0.001f && value <= 1.001f)) {
              uv_within_unit = false;
              break;
            }
          }
        }
        if (uv_within_unit) {
          fetch_state = DirectPackedSamplerStateWithClampAddress(fetch_state);
        }
      }
      if (fetch_state != sampler_state) {
        static std::unordered_map<uint32_t, uint32_t> logged_by_shader;
        uint32_t &shader_logs =
            logged_by_shader[command.resources.pixel_shader];
        if (shader_logs < 4u && logged_by_shader.size() <= 64u) {
          ++shader_logs;
          REXLOG_INFO("Refii fetch-clamp sampler override slot={} "
                      "before=0x{:08X} after=0x{:08X} u={} v={} ps=0x{:08X}",
                      sampled_slot, sampler_state, fetch_state,
                      (fetch_state >> kDirectPackedSamplerStateAddressUShift) &
                          kDirectPackedSamplerStateAddressMask,
                      (fetch_state >> kDirectPackedSamplerStateAddressVShift) &
                          kDirectPackedSamplerStateAddressMask,
                      command.resources.pixel_shader);
        }
      }
      sampler_state = fetch_state;
    }
    plume::RenderSampler* sampler =
        GetOrCreateDirectSamplerLocked(sampler_state);
    const uint32_t sampler_descriptor =
        FindOrSetFableSamplerDescriptorLocked(sampler);
    uint32_t tex2d = 0;
    uint32_t tex3d = 0;
    uint32_t texcube = 0;
    const bool exact_bink_declared_slot =
        sampled_slot < 3u && exact_bink_wrapper_draw;
    PlumeTrackedResource* sampled_texture =
        exact_bink_declared_slot
            ? ResolveDirectExactBinkWrapperPlaneForSlotLocked(command,
                                                              sampled_slot)
            : ResolveDirectSampledTextureForCommandLocked(command,
                                                          sampled_slot);
    if (sampled_texture && PlumeGuestImageReady(*sampled_texture)) {
      uint32_t descriptor_set = 0;
      if (sampled_texture->texture_view_dimension ==
          plume::RenderTextureViewDimension::TEXTURE_3D) {
        descriptor_set = 1;
      } else if (sampled_texture->texture_view_dimension ==
                 plume::RenderTextureViewDimension::TEXTURE_CUBE) {
        descriptor_set = 2;
      }
      plume::RenderTextureView* sample_view =
          exact_bink_declared_slot && descriptor_set == 0u
              ? DirectBinkPlaneIdentitySampleViewLocked(*sampled_texture)
              : sampled_texture->guest_texture.view.get();
      const uint32_t descriptor =
          FindOrSetFableTextureDescriptorLocked(
              descriptor_set, sampled_texture->guest_texture.texture.get(),
              plume::RenderTextureLayout::SHADER_READ, sample_view,
              sampled_texture->guest_address);
      if (descriptor_set == 1) {
        tex3d = descriptor;
      } else if (descriptor_set == 2) {
        texcube = descriptor;
      } else {
        tex2d = descriptor;
      }
    }
    // UNSTATED SLOTS WERE THE BLIND SPOT (2026-08-04). The counter below
    // requires `textures[sampled_slot] != 0` — "a slot the game populated" —
    // so a shader-used slot that states NOTHING binds the null descriptor in
    // complete silence: no resolver null (that logger also early-outs on
    // guest==0), no counter, no log line.
    //
    // That is not a corner case. RenderDoc capture frame8763 (the black
    // gameplay frame) shows the FINAL TONEMAP,
    // 0500_pshader_postprocessingmaterial_tonemap1, sampling descriptor 0 —
    // the 1x1 null texture — for its scene-colour input, while its bloom and
    // tone-curve inputs bind correctly. The whole frame renders and is then
    // tonemapped against nothing. Zero diagnostics fired for it.
    //
    // Count and name it. An unstated slot on a shader-used binding is a
    // STRONGER signal than a stated-but-unresolvable one: the former means we
    // never even learned what the guest wanted.
    if (tex2d == 0 && tex3d == 0 && texcube == 0 &&
        sampled_slot < command.resources.textures.size() &&
        command.resources.textures[sampled_slot] == 0 &&
        command.resources.render_targets[0] != 0 &&
        command.draw.source != RendererDrawEventSource::kPixVideo) {
      ++g_direct_renderer.unstated_slot_null_binds;
      static uint32_t logged_unstated_slot_binds = 0;
      if (logged_unstated_slot_binds < 64u ||
          (g_direct_renderer.unstated_slot_null_binds % 4096u) == 0u) {
        ++logged_unstated_slot_binds;
        REXLOG_WARN(
            "Refii plume UNSTATED texture slot bound null #{} frame={} "
            "serial={} source={} slot={} rt=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
            "fetch_base=0x{:08X} — shader reads this slot and the command "
            "states NOTHING for it (cum={})",
            logged_unstated_slot_binds, g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(command.serial),
            static_cast<uint32_t>(command.draw.source), sampled_slot,
            command.resources.render_targets[0],
            command.resources.vertex_shader, command.resources.pixel_shader,
            FableTextureFetchIdentityBaseFromDwords(
                DirectCommandTextureFetchDwords(command, sampled_slot)),
            static_cast<unsigned long long>(
                g_direct_renderer.unstated_slot_null_binds));
      }
    }
    // Outcome counter for the black-panel class: the draw is about to bind
    // NO texture for a slot the game populated. This is the ground-truth
    // residual (classifier-time NULL resolves can self-heal via sync before
    // execute; this cannot).
    if (tex2d == 0 && tex3d == 0 && texcube == 0 &&
        sampled_slot < command.resources.textures.size() &&
        command.resources.textures[sampled_slot] != 0 &&
        command.resources.render_targets[0] != 0 &&
        command.draw.source != RendererDrawEventSource::kPixVideo) {
      if (g_direct_renderer.textureless_compose_targets.size() < 32u) {
        g_direct_renderer.textureless_compose_targets.insert(
            command.resources.render_targets[0]);
      }
      static uint64_t textureless_visible_binds = 0;
      const uint64_t count = ++textureless_visible_binds;
      if (count <= 16u || count % 64u == 0u) {
        REXLOG_INFO(
            "Refii plume textureless visible bind #{} frame={} source={} "
            "slot={} guest=0x{:08X} rt=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            static_cast<unsigned long long>(count),
            g_direct_renderer.current_frame_index,
            static_cast<uint32_t>(command.draw.source), sampled_slot,
            command.resources.textures[sampled_slot],
            command.resources.render_targets[0],
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
    }
    // Write the live resolution back into this draw's stored frame-database
    // packet: a draw packet must be a complete, self-contained description
    // (design doc §3.2/§9), and capture-side diagnosis must never re-derive
    // bindings the live path already decided.
    if (sampled_slot < 16u) {
      const auto packet_it =
          g_direct_renderer.frame_database_draw_packet_index_by_serial.find(
              command.serial);
      if (packet_it !=
              g_direct_renderer.frame_database_draw_packet_index_by_serial
                  .end() &&
          packet_it->second <
              g_direct_renderer.frame_database_draw_packets.size()) {
        FableDirectFrameDatabaseDrawPacket& stored_packet =
            g_direct_renderer.frame_database_draw_packets[packet_it->second];
        const bool slot_bound = tex2d != 0 || tex3d != 0 || texcube != 0;
        stored_packet.resolved_textures[sampled_slot] =
            slot_bound && sampled_texture ? sampled_texture->guest_address
                                          : 0u;
        stored_packet.resolved_texture_bases[sampled_slot] =
            slot_bound && sampled_texture
                ? PlumeGuestTextureBaseAddress(*sampled_texture)
                : 0u;
        stored_packet.resolved_bindings_valid = true;
      }
    }
    static uint32_t logged_direct_descriptor_slots = 0;
    static uint32_t logged_direct_overdraw_descriptor_slots = 0;
    static uint32_t logged_direct_generated_single_texture_descriptor_slots = 0;
    const uint64_t pixel_shader_hash =
        DirectPixelShaderHashLocked(command.resources.pixel_shader);
    const bool particle_declared_slot =
        (sampled_slot == 0u || sampled_slot == 1u || sampled_slot == 2u) &&
        pixel_shader_hash == kFableParticleMaterialAnim1DepthPixelHash;
    const bool overdraw_declared_slot =
        (sampled_slot == 13u || sampled_slot == 14u) &&
        pixel_shader_hash == kFableTitleOverdrawVisualisePixelHash;
    // DERIVED (census: generated_single_slot0_ps). The nine pixel-shader
    // object-pointer literals harvested from one title run were DELETED
    // 2026-07-26: run 288 measured tested=123562 matched=1920 derived=1920
    // lit_only=0, so the four bytecode hashes below caught every match and the
    // nine pointers never decided anything alone. This gate only widens
    // descriptor-probe LOGGING, so it is also the lowest-stakes of the four.
    // Census call KEPT so a drop from 1920 falsifies this on the next run.
    const bool generated_single_slot0_hash =
        pixel_shader_hash == kFablePostprocessingDownscale4PixelHash ||
        pixel_shader_hash == kFablePostprocessingGaussBlurPixelHash ||
        pixel_shader_hash == kFableScreenFacingSpritePixelHash ||
        pixel_shader_hash == kFableDecalPixelHash;
    const bool generated_single_slot0_shader = DirectAddressGateTest(
        DirectAddressGate::kGeneratedSingleSlot0PixelShader,
        generated_single_slot0_hash, command.resources.pixel_shader,
        pixel_shader_hash, generated_single_slot0_hash);
    const bool generated_single_slot0 =
        slot == 0u && generated_single_slot0_shader;
    const bool generated_single_slot13 =
        sampled_slot == 13u &&
        pixel_shader_hash == kFableTexturedQuadPreserveStencilPixelHash;
    const bool descriptor_probe_slot =
        exact_bink_declared_slot || exact_bink_alpha_mirror_slot ||
        particle_declared_slot || generated_single_slot0 ||
        generated_single_slot13 || sampled_slot == 13u ||
        sampled_slot == 14u || sampled_slot == 15u;
    // Caps raised 64/32/64 -> 8000/4000/4000 (2026-07-25): the old caps
    // exhausted well before the RenderDoc capture frame used to verify the
    // tree-billboard stale-default-override fix (run250, frame~2156),
    // leaving that specific frame's actual descriptor-write outcome
    // unobserved. See refii-tree-billboard-stale-default-override.md.
    const bool log_regular_probe =
        descriptor_probe_slot && logged_direct_descriptor_slots < 64u;
    const bool log_overdraw_probe =
        overdraw_declared_slot && logged_direct_overdraw_descriptor_slots < 32u;
    const bool log_generated_single_texture_probe =
        (generated_single_slot0 || generated_single_slot13) &&
        logged_direct_generated_single_texture_descriptor_slots < 32u;
    if (descriptor_probe_slot &&
        command.draw.source != RendererDrawEventSource::kPixVideo &&
        (log_regular_probe || log_overdraw_probe ||
         log_generated_single_texture_probe)) {
      uint32_t proof_index = logged_direct_descriptor_slots + 1u;
      if (log_regular_probe) {
        proof_index = ++logged_direct_descriptor_slots;
      }
      if (log_overdraw_probe) {
        ++logged_direct_overdraw_descriptor_slots;
        if (!log_regular_probe) {
          proof_index = 1000u + logged_direct_overdraw_descriptor_slots;
        }
      }
      if (log_generated_single_texture_probe) {
        ++logged_direct_generated_single_texture_descriptor_slots;
        if (!log_regular_probe && !log_overdraw_probe) {
          proof_index =
              2000u + logged_direct_generated_single_texture_descriptor_slots;
        }
      }
      REXLOG_INFO(
          "Refii plume direct descriptor slot proof #{} frame={} source={} "
          "slot={} sampled_slot={} guest=0x{:08X} sampled_guest=0x{:08X} "
          "sampled=0x{:08X} base=0x{:08X} size={}x{} fmt={} ready={} "
          "tex2d={} tex3d={} texcube={} sampler={} mask=0x{:08X} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          proof_index,
          g_direct_renderer.current_frame_index,
          static_cast<uint32_t>(command.draw.source), slot, sampled_slot,
          slot < command.resources.textures.size()
              ? command.resources.textures[slot]
              : 0u,
          sampled_slot < command.resources.textures.size()
              ? command.resources.textures[sampled_slot]
              : 0u,
          sampled_texture ? sampled_texture->guest_address : 0u,
          sampled_texture ? PlumeGuestTextureBaseAddress(*sampled_texture) : 0u,
          sampled_texture ? PlumeGuestTextureWidth(*sampled_texture) : 0u,
          sampled_texture ? PlumeGuestTextureHeight(*sampled_texture) : 0u,
          sampled_texture
              ? static_cast<uint32_t>(PlumeGuestTextureFormat(*sampled_texture))
              : 0u,
          sampled_texture && PlumeGuestImageReady(*sampled_texture) ? 1 : 0,
          tex2d, tex3d, texcube, sampler_descriptor, texture_slot_mask,
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    store_u32(slot * 4u, tex2d);
    store_u32(128u + slot * 4u, tex3d);
    store_u32(256u + slot * 4u, texcube);
    store_u32(384u + slot * 4u, sampler_descriptor);
  }

  // Translated shaders test vertex bools at bits 0-15 and pixel bools at
  // bits 16-31 of g_Booleans; Fable's pixel shaders reference up to b9, so
  // 8-bit masks would silently drop b8+.
  const uint32_t vertex_bool_bits = command.constants.vertex_bool[0] & 0xFFFFu;
  const uint32_t pixel_bool_bits = command.constants.pixel_bool[0] & 0xFFFFu;
  const uint32_t bool_bits = vertex_bool_bits | (pixel_bool_bits << 16u);
  uint32_t swapped_texcoords = 0;
  if (const PlumeTrackedResource* declaration =
          FindDirectReadyDeclarationResourceByIdentityLocked(
              command.resources.vertex_declaration)) {
    swapped_texcoords = declaration->swapped_texcoords;
  }
  store_u32(512, bool_bits);
  store_u32(516, swapped_texcoords);
  store_f32(520, framebuffer.width != 0 ? 1.0f / float(framebuffer.width)
                                        : 0.0f);
  store_f32(524, framebuffer.height != 0 ? -1.0f / float(framebuffer.height)
                                         : 0.0f);
  // Scalar overload: the snapshot form default-constructed a ~13 KB
  // RendererDrawEventSnapshot (both float banks zero-initialized) per
  // recorded draw inside the submitsplit const= span, only to read these six
  // fields. BuildDrawEventFromCommandForShaderState copies exactly these
  // pm4/render-state fields, so this is behaviour-identical.
  store_u32(528, FableAlphaThresholdBits(
                     command.pipeline.pm4.valid,
                     command.pipeline.pm4.rb_colorcontrol,
                     command.pipeline.pm4.rb_alpha_ref,
                     command.pipeline
                         .render_states[kFableRenderStateAlphaTestEnable],
                     command.pipeline.render_states[kFableRenderStateAlphaFunc],
                     command.pipeline
                         .render_states[kFableRenderStateAlphaRef]));
  store_u32(536, command.constants.vertex_int_valid_mask);
  store_u32(540, command.constants.pixel_int_valid_mask);

  // T1/T3 tessellation contract (shader_common.h): +560 = uint grid factor N
  // (g_TessellationFactor), +564 = uint index offset added to SV_InstanceID
  // (g_TessellationIndexOffset); +568/572 reserved and already zeroed by the
  // fill(0) above. Only kGtlTessDraw commands carry these; the base variant
  // of every shader never reads them.
  if (command.gtl_tess_valid) {
    uint32_t tess_n = 0;
    uint32_t tess_patches = 0;
    uint32_t tess_mode = 0;
    DecodeGtlTessDrawShape(command.gtl_tess, tess_n, tess_patches, tess_mode);
    store_u32(560, tess_n);
    store_u32(564, GtlTessIndexOffset(command.gtl_tess));
  }

  const size_t vertex_int_bytes =
      command.constants.vertex_int.size() * sizeof(uint32_t);
  const size_t pixel_int_bytes =
      command.constants.pixel_int.size() * sizeof(uint32_t);
  std::memcpy(shared.data() + kDirectVertexIntegerConstantByteBase,
              command.constants.vertex_int.data(),
              std::min<size_t>(vertex_int_bytes,
                               shared.size() -
                                   kDirectVertexIntegerConstantByteBase));
  std::memcpy(shared.data() + kDirectPixelIntegerConstantByteBase,
              command.constants.pixel_int.data(),
              std::min<size_t>(pixel_int_bytes,
                               shared.size() -
                                   kDirectPixelIntegerConstantByteBase));
}

bool IsFableTitleToneMapDrawLocked(const FableDirectRenderCommand& command) {
  if (command.resources.pixel_shader == 0) {
    return false;
  }
  const PlumeTrackedResource* shader =
      FindDirectResourceLocked(command.resources.pixel_shader);
  return shader != nullptr && PlumeGuestShaderReady(*shader) &&
         shader->guest_shader.bytecode_hash ==
             kFableTitleToneMapPixelShaderHash;
}

bool InferExactBinkWrapperVertexExtentLocked(
    const FableDirectRenderCommand& command, float& width, float& height) {
  width = 0.0f;
  height = 0.0f;
  if (!DirectCommandIsExactBinkIndexedWrapperDrawLocked(command)) {
    return false;
  }

  const FableDirectInlineBuffer& vb = command.inline_vertex_buffers[0];
  const RendererStreamBindingSnapshot& stream = command.resources.streams[0];
  const uint32_t stride = ResolveDirectInputSlotStrideLocked(command, 0);
  const uint32_t offset = DirectInlineBufferOffsetForAddress(vb, stream.buffer);
  return FableVideoInferBinkWrapperVertexExtent(vb, stride, offset, width,
                                                height);
}

void ApplyExactBinkWrapperVertexTransformLocked(
    const FableDirectRenderCommand& command,
    std::array<uint32_t, kRendererShaderFloatConstantDwords>& constants) {
  if (!DirectCommandIsExactBinkIndexedWrapperDrawLocked(command)) {
    return;
  }

  float width = 0.0f;
  float height = 0.0f;
  if (!InferExactBinkWrapperVertexExtentLocked(command, width, height)) {
    // The compose quad's vertices live in the guest's screen space, which
    // the guest derives from qword_834A5FE8 — fall back to the live guest
    // display dims, not a hardcoded 1280x720.
    width = FableGuestDisplayWidthF();
    height = FableGuestDisplayHeightF();
  }
  FableVideoApplyBinkWrapperVertexTransform(constants, width, height);

  static uint32_t logged_bink_vertex_transforms = 0;
  if (RendererVerboseTracingEnabled() &&
      logged_bink_vertex_transforms < 32u) {
    REXLOG_INFO(
        "Refii plume applied exact Bink wrapper Vulkan screen transform #{} "
        "frame={} serial={} draw={} extent={:.1f}x{:.1f} "
        "rt=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
        ++logged_bink_vertex_transforms,
        g_direct_renderer.current_frame_index,
        static_cast<unsigned long long>(command.serial),
        static_cast<unsigned long long>(command.draw_serial), width, height,
        command.resources.render_targets[0], command.resources.vertex_shader,
        command.resources.pixel_shader);
  }
}

bool BindDirectDrawConstantsLocked(plume::RenderDevice* device,
                                   plume::RenderCommandList* command_list,
                                   const FableDirectRenderCommand& command,
                                   const DirectFramebufferBinding& framebuffer) {
  if (!device || !command_list) {
    return false;
  }

  // Perf: both float banks are 1024 dwords (4 KiB). Copying them by value cost
  // 8 KiB of memcpy on EVERY recorded draw even though the pixel bank is only
  // ever read, and the vertex bank is only ever rewritten for the exact Bink
  // indexed-wrapper draw (at most one per frame). Bind the command's banks
  // directly and materialize a mutable copy solely on the Bink path.
  // ApplyExactBinkWrapperVertexTransformLocked re-checks the same predicate and
  // no-ops when it is false, so this is behaviour-identical; the classifier is
  // memoized (returns before its filter logging), so the extra call is free and
  // side-effect-free.
  const std::array<uint32_t, kRendererShaderFloatConstantDwords>*
      vertex_float_constants = &command.constants.vertex_float;
  std::array<uint32_t, kRendererShaderFloatConstantDwords>
      bink_vertex_float_constants;
  if (DirectCommandIsExactBinkIndexedWrapperDrawLocked(command)) {
    bink_vertex_float_constants = command.constants.vertex_float;
    ApplyExactBinkWrapperVertexTransformLocked(command,
                                               bink_vertex_float_constants);
    vertex_float_constants = &bink_vertex_float_constants;
  }
  const std::array<uint32_t, kRendererShaderFloatConstantDwords>&
      pixel_float_constants = command.constants.pixel_float;

  // Constants are per-draw transients, so they come from the frame arena: a
  // bump allocation out of an already-created, already-mapped chunk. This
  // replaced a content-hash upload cache that missed on essentially every draw
  // (the vertex bank carries the per-object WVP), leaked a Vulkan buffer per
  // miss, and rescanned its whole eviction map per miss.
  //
  // RUN-495 per-compose dedup on top of the arena: const= measured
  // 23.2ms/compose at ~1200 draws (~19us/draw) with every draw re-uploading
  // both full 4 KiB float banks. Consecutive draws of a material batch ship
  // an identical pixel bank, and GTL commands whose packets carried no float
  // constants ship all-zero banks — so remember the previous upload per bank
  // and rebind it when the current content matches. No content serial exists
  // on the packet constants (FableDirectConstantState and the packet
  // vs/ps_float_constants vectors are raw payloads, checked 2026-08-01), so
  // the equivalence signal is a memcmp against the SOURCE bank of the
  // previous upload: that command lives in the batch vector, unmodified after
  // its own record, for the rest of the compose. A vertex-bank miss exits on
  // the first dwords (c0-c3 is the WVP), so misses cost a cache line, not
  // 4 KiB. Compose-scoped by the frame-index guard — the arena slot recycles
  // at frame start, so a stale allocation can never cross frames.
  struct DirectConstantBankUploadMemo {
    uint32_t frame_index = UINT32_MAX;
    const uint32_t* vertex_source = nullptr;
    DirectConstantAllocation vertex_allocation;
    const uint32_t* pixel_source = nullptr;
    DirectConstantAllocation pixel_allocation;
  };
  static DirectConstantBankUploadMemo bank_memo;
  if (bank_memo.frame_index != g_direct_renderer.current_frame_index) {
    bank_memo = DirectConstantBankUploadMemo{};
    bank_memo.frame_index = g_direct_renderer.current_frame_index;
  }
  constexpr size_t kDirectFloatBankBytes =
      kRendererShaderFloatConstantDwords * sizeof(uint32_t);
  // The Bink-transformed vertex bank is a local temporary whose content
  // differs from the command's stored bank, so it neither consults nor
  // updates the memo (at most one such draw per frame).
  const bool vertex_bank_memoizable =
      vertex_float_constants == &command.constants.vertex_float;
  DirectConstantAllocation vertex_allocation;
  if (vertex_bank_memoizable && bank_memo.vertex_source != nullptr &&
      std::memcmp(bank_memo.vertex_source, vertex_float_constants->data(),
                  kDirectFloatBankBytes) == 0) {
    vertex_allocation = bank_memo.vertex_allocation;
    ++g_direct_draw_phases.submit_const_bank_reuses;
  } else {
    vertex_allocation = AllocateDirectConstantsLocked(
        device, vertex_float_constants->data(), kDirectFloatBankBytes);
    ++g_direct_draw_phases.submit_const_bank_uploads;
    if (vertex_bank_memoizable && vertex_allocation) {
      bank_memo.vertex_source = vertex_float_constants->data();
      bank_memo.vertex_allocation = vertex_allocation;
    }
  }
  DirectConstantAllocation pixel_allocation;
  if (bank_memo.pixel_source != nullptr &&
      std::memcmp(bank_memo.pixel_source, pixel_float_constants.data(),
                  kDirectFloatBankBytes) == 0) {
    pixel_allocation = bank_memo.pixel_allocation;
    ++g_direct_draw_phases.submit_const_bank_reuses;
  } else {
    pixel_allocation = AllocateDirectConstantsLocked(
        device, pixel_float_constants.data(), kDirectFloatBankBytes);
    ++g_direct_draw_phases.submit_const_bank_uploads;
    if (pixel_allocation) {
      bank_memo.pixel_source = pixel_float_constants.data();
      bank_memo.pixel_allocation = pixel_allocation;
    }
  }
  std::array<uint8_t, kDirectGeneratedSharedConstantsBytes> shared_constants;
  FillDirectSharedConstants(command, framebuffer, shared_constants);
  const DirectConstantAllocation shared_allocation =
      AllocateDirectConstantsLocked(device, shared_constants.data(),
                                    shared_constants.size());
  if (!vertex_allocation || !pixel_allocation || !shared_allocation) {
    return false;
  }

  if (g_direct_renderer.shader_format == plume::RenderShaderFormat::SPIRV) {
    const uint64_t addresses[3] = {vertex_allocation.device_address,
                                   pixel_allocation.device_address,
                                   shared_allocation.device_address};
    command_list->setGraphicsPushConstants(0, addresses, 0,
                                           sizeof(addresses));
  } else {
    command_list->setGraphicsRootDescriptor(
        vertex_allocation.buffer->at(vertex_allocation.offset), 0);
    command_list->setGraphicsRootDescriptor(
        pixel_allocation.buffer->at(pixel_allocation.offset), 1);
    command_list->setGraphicsRootDescriptor(
        shared_allocation.buffer->at(shared_allocation.offset), 2);
  }
  return true;
}

DirectPipelineKey BuildDirectPipelineKey(
    const FableDirectRenderCommand& command,
    const std::vector<plume::RenderInputSlot>& input_slots,
    const std::vector<plume::RenderInputElement>& input_elements,
    const DirectFramebufferBinding& framebuffer, bool force_no_cull) {
  DirectPipelineKey key;
  const uint64_t vertex_shader_hash =
      DirectVertexShaderHashLocked(command.resources.vertex_shader);
  const uint64_t pixel_shader_hash =
      DirectPixelShaderHashLocked(command.resources.pixel_shader);
  key.vertex_shader_hash =
      vertex_shader_hash != 0 ? vertex_shader_hash
                              : command.resources.vertex_shader;
  key.pixel_shader_hash =
      pixel_shader_hash != 0 ? pixel_shader_hash : command.resources.pixel_shader;
  key.topology = ConvertPrimitiveTopology(command.draw.draw.primitive_type);
  key.color_count = framebuffer.color_count;
  key.color_formats = framebuffer.color_formats;
  key.depth_format = framebuffer.depth_format;
  key.pm4_valid = command.pipeline.pm4.valid;
  const RendererDrawEventSnapshot shader_state =
      BuildDrawEventFromCommandForShaderState(command);
  if (FableAlphaTestEnabled(shader_state)) {
    key.spec_constants |= kDirectSpecConstantAlphaTest;
  }
  if (FableViewportUsesReverseZ(shader_state)) {
    key.spec_constants |= kDirectSpecConstantReverseZ;
  }
  key.depth_control = DirectDepthControl(command);
  key.stencil_ref_mask = DirectStencilRefMask(command, false);
  key.stencil_ref_mask_bf = DirectStencilRefMask(command, true);
  for (uint32_t slot = 0;
       slot < framebuffer.color_count && slot < kMaxFableRenderTargets;
       ++slot) {
    key.blend_enabled[slot] = DirectBlendEnabledForTarget(command, slot);
    key.blend_controls[slot] =
        key.blend_enabled[slot] ? DirectBlendControlForTarget(command, slot)
                                : kDirectDefaultBlendControl;
    key.color_write_masks[slot] =
        DirectColorWriteMaskForFramebufferTarget(command, framebuffer, slot);
  }
  key.cull_control =
      command.pipeline.pm4.valid
          ? command.pipeline.pm4.pa_su_sc_mode_cntl
          : command.pipeline.render_states[kFableRenderStateCullMode];
  key.force_no_cull = force_no_cull;
  // The Bink wrapper compose needs the same video pipeline state as PixVideo:
  // the wrapper programs its state internally (invisible to the SetRenderState
  // root), so packet/state-cache-derived masks can be stale — a compose with
  // writeMask=0 wrote nothing (compose-trace frame 2265, white video). Blend
  // off / write-all / depth off / no cull is exactly what the guest sets for
  // PSHADER_BINK_MOVIE (IDA sub_82A41F10).
  if (command.draw.source == RendererDrawEventSource::kPixVideo ||
      DirectCommandIsExactBinkIndexedWrapperDrawLocked(command)) {
    for (uint32_t slot = 0; slot < key.color_count &&
                           slot < kMaxFableRenderTargets;
         ++slot) {
      key.blend_enabled[slot] = false;
      key.blend_controls[slot] = kDirectDefaultBlendControl;
      key.color_write_masks[slot] = 0xFu;
    }
    key.depth_control = 0;
    key.stencil_ref_mask = 0;
    key.stencil_ref_mask_bf = 0;
    // key.depth_format stays as the framebuffer's: it names the render-pass
    // attachment SHAPE, not depth state — a pipeline built without the depth
    // attachment is render-pass-incompatible with the bound framebuffer
    // (VUID-vkCmdDrawIndexed-renderPass-02684). depth_control=0 is what
    // turns depth test/write off.
    key.cull_control = 0;
    key.force_no_cull = true;
    key.spec_constants = 0;
  }
  // Match the Unleashed/06 style of caching the effective pipeline state, not
  // every raw guest register bit. Fable often leaves stale PM4/render-state
  // values around title quads; keeping those in the key creates redundant PSOs.
  const bool has_depth_attachment =
      framebuffer.depth_format != plume::RenderFormat::UNKNOWN;
  const bool depth_enabled = has_depth_attachment &&
                             (key.depth_control & 0x2u) != 0;
  const bool stencil_enabled = has_depth_attachment &&
                               (key.depth_control & 0x1u) != 0;
  if (!has_depth_attachment) {
    key.depth_control = 0;
    key.stencil_ref_mask = 0;
    key.stencil_ref_mask_bf = 0;
    key.depth_format = plume::RenderFormat::UNKNOWN;
  } else if (!depth_enabled && !stencil_enabled) {
    key.depth_control = 0;
    key.stencil_ref_mask = 0;
    key.stencil_ref_mask_bf = 0;
  } else {
    if (!depth_enabled) {
      key.depth_control &= ~0x76u;
    }
    if (!stencil_enabled) {
      key.depth_control &= 0x76u;
      key.stencil_ref_mask = 0;
      key.stencil_ref_mask_bf = 0;
    }
  }
  if (key.force_no_cull) {
    key.cull_control = 0;
  }
  key.pm4_valid = false;
  // The guest declaration object address is allocation-order noise. The Plume
  // pipeline is defined by the decoded slots/elements.
  key.input_slots.reserve(input_slots.size());
  for (const plume::RenderInputSlot& slot : input_slots) {
    key.input_slots.push_back({
        .index = slot.index,
        .stride = slot.stride,
        .classification = slot.classification,
    });
  }
  key.input_elements.reserve(input_elements.size());
  for (const plume::RenderInputElement& element : input_elements) {
    key.input_elements.push_back({
        .semantic_name =
            element.semanticName != nullptr ? element.semanticName : "",
        .semantic_index = element.semanticIndex,
        .location = element.location,
        .format = element.format,
        .slot_index = element.slotIndex,
        .aligned_byte_offset = element.alignedByteOffset,
    });
  }
  return key;
}

std::string DirectPipelineDiagnosticKey(
    const char* reason, const FableDirectRenderCommand& command) {
  char key[192] = {};
  std::snprintf(key, sizeof(key),
                "%s/%08X/%08X/%08X/%08X/%08X/%u/%u/%u",
                reason ? reason : "unknown", command.resources.vertex_shader,
                command.resources.pixel_shader,
                command.resources.vertex_declaration,
                command.resources.render_targets[0],
                command.resources.depth_stencil,
                static_cast<uint32_t>(command.draw.draw.primitive_type),
                command.draw.draw.primitive_count,
                command.draw.draw.indexed ? 1u : 0u);
  return key;
}

void LogDirectPipelineNotReadyLocked(
    const char* reason, const FableDirectRenderCommand& command,
    const PlumeTrackedResource* vertex_shader,
    const PlumeTrackedResource* pixel_shader,
    const PlumeTrackedResource* declaration, bool synthetic_private_rect,
    size_t layout_slot_count,
    size_t layout_element_count) {
  if (g_direct_renderer.diagnostics.logged_pipeline_not_ready_details >= 64) {
    return;
  }

  const std::string key = DirectPipelineDiagnosticKey(reason, command);
  if (!g_direct_renderer.diagnostics.logged_pipeline_not_ready_keys.insert(key).second) {
    return;
  }

  ++g_direct_renderer.diagnostics.logged_pipeline_not_ready_details;
  const auto shader_hash = [](const PlumeTrackedResource* resource) {
    return resource ? resource->guest_shader.bytecode_hash : 0ull;
  };
  const auto shader_size = [](const PlumeTrackedResource* resource) {
    return resource ? resource->guest_shader.bytecode_size : 0u;
  };
  const auto shader_name = [](const PlumeTrackedResource* resource) {
    return resource ? resource->guest_shader.name.c_str() : "";
  };
  const size_t declaration_slots =
      declaration ? declaration->guest_vertex_declaration.input_slots.size()
                  : 0u;
  const size_t declaration_elements =
      declaration ? declaration->guest_vertex_declaration.input_elements.size()
                  : 0u;
  const uint64_t declaration_hash =
      declaration ? declaration->guest_vertex_declaration.elements_hash : 0ull;
  REXLOG_WARN(
      "Refii plume direct pipeline not ready reason={} serial={} source={} "
      "rt=0x{:08X} depth=0x{:08X} vs=0x{:08X} found={} ready={} "
      "hash=0x{:016X} size={} name={} ps=0x{:08X} found={} ready={} "
      "hash=0x{:016X} size={} name={} decl=0x{:08X} found={} ready={} "
      "elems={} slots={} hash=0x{:016X} layout_elems={} layout_slots={} "
      "synthetic_private_rect={}",
      reason ? reason : "unknown", command.serial,
      static_cast<uint32_t>(command.draw.source),
      command.resources.render_targets[0], command.resources.depth_stencil,
      command.resources.vertex_shader, vertex_shader != nullptr,
      vertex_shader && PlumeGuestShaderReady(*vertex_shader),
      shader_hash(vertex_shader), shader_size(vertex_shader),
      shader_name(vertex_shader), command.resources.pixel_shader,
      pixel_shader != nullptr,
      pixel_shader && PlumeGuestShaderReady(*pixel_shader),
      shader_hash(pixel_shader), shader_size(pixel_shader),
      shader_name(pixel_shader), command.resources.vertex_declaration,
      declaration != nullptr,
      declaration && PlumeGuestDeclarationReady(*declaration),
      declaration_elements, declaration_slots, declaration_hash,
      layout_element_count == SIZE_MAX ? 0u : layout_element_count,
      layout_slot_count == SIZE_MAX ? 0u : layout_slot_count,
      synthetic_private_rect);
}

const std::vector<plume::RenderInputElement>& DirectVertexShaderInputsLocked(
    const PlumeTrackedResource& vertex_shader) {
  static const std::vector<plume::RenderInputElement> kEmptyInputs;
  static std::unordered_map<uint64_t, std::vector<plume::RenderInputElement>>
      cached_inputs;

  const uint64_t hash = vertex_shader.guest_shader.bytecode_hash;
  if (hash == 0) {
    return kEmptyInputs;
  }

  auto cached_it = cached_inputs.find(hash);
  if (cached_it != cached_inputs.end()) {
    return cached_it->second;
  }

  RendererShaderLibraryEntry entry;
  std::vector<plume::RenderInputElement> inputs;
  if (FindRendererShaderLibraryEntryByGuestHash(hash, entry) &&
      !entry.hlsl_path.empty()) {
    inputs = ParseShaderInputSignature(entry.hlsl_path);
  }
  auto [inserted_it, _] = cached_inputs.emplace(hash, std::move(inputs));
  return inserted_it->second;
}

void AlignDirectInputLayoutToVertexShaderLocked(
    const PlumeTrackedResource& vertex_shader,
    std::vector<plume::RenderInputSlot>& input_slots,
    std::vector<plume::RenderInputElement>& input_elements) {
  const std::vector<plume::RenderInputElement>& shader_inputs =
      DirectVertexShaderInputsLocked(vertex_shader);
  if (shader_inputs.empty()) {
    return;
  }

  FilterInputElementsToShaderInputs(input_elements, shader_inputs);
  RemapInputLocationsFromShader(input_elements, shader_inputs);
  PadMissingInputLocations(input_slots, input_elements, &shader_inputs);
}

// Pipeline-ensure content memo (2026-08-02 compose peel). Run 513: pipe= was
// 3.65ms/compose at the settled title with pipeline_misses FLAT — every
// lookup a hit, yet each one re-derived the full input layout (two vector
// builds + normalize + shader-input align + stride resolve + trim), built a
// shader-state snapshot for the spec-constant bits, constructed the
// vector+string DirectPipelineKey and hashed it into the pipelines map. This
// memo maps an FNV over every raw input of that derivation directly to the
// proven pipeline pointer.
//
// Why no generation guard is needed: every registry-dependent input is IN the
// key — the resolved shader records' bytecode hashes + serials and the
// declaration record's elements_hash + serial (the caller still performs the
// identity lookups and ready checks on every call, so not-ready behaviour is
// untouched) — and the shader-input alignment cache is append-only, keyed by
// bytecode hash. The ONE derivation input that can consult the registry
// without appearing in the key is ResolveDirectInputSlotStrideLocked's
// tracked-buffer stride fallback, reachable only for a stream with a bound
// buffer, no stream stride and no inline stride; commands matching that
// shape BYPASS the memo (census pipememo bypass=). Pipeline pointers are
// stable: g_direct_renderer.pipelines only ever grows, except at the
// wholesale .clear() sites, each of which clears this memo too.
std::unordered_map<uint64_t, plume::RenderPipeline*>
    g_direct_pipeline_ensure_memo;

void ClearDirectPipelineEnsureMemoLocked() {
  g_direct_pipeline_ensure_memo.clear();
}

plume::RenderPipeline* EnsurePipelineForDirectDrawLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer) {
  if (!device) {
    LogDirectPipelineNotReadyLocked("missing_device", command, nullptr,
                                    nullptr, nullptr, false);
    return nullptr;
  }
  if (!g_plume_resources.pipeline_layout) {
    LogDirectPipelineNotReadyLocked("missing_pipeline_layout", command, nullptr,
                                    nullptr, nullptr, false);
    return nullptr;
  }

  PlumeTrackedResource* vertex_shader =
      FindDirectReadyShaderResourceByIdentityLocked(
          command.resources.vertex_shader,
          FableGuestResourceKind::kVertexShader);
  PlumeTrackedResource* pixel_shader =
      FindDirectReadyShaderResourceByIdentityLocked(
          command.resources.pixel_shader,
          FableGuestResourceKind::kPixelShader);
  const bool synthetic_private_rect =
      UseSyntheticPrivateRectInputLayout(command);
  const bool synthetic_primitive8 =
      ShouldExpandPrimitive8Rectangle(command, 0);
  PlumeTrackedResource* declaration =
      (synthetic_private_rect || synthetic_primitive8)
          ? nullptr
          : FindDirectReadyDeclarationResourceByIdentityLocked(
                command.resources.vertex_declaration);
  if (!vertex_shader || !pixel_shader ||
      (!synthetic_private_rect && !synthetic_primitive8 && !declaration) ||
      !PlumeGuestShaderReady(*vertex_shader) ||
      !PlumeGuestShaderReady(*pixel_shader) ||
      (!synthetic_private_rect && !synthetic_primitive8 &&
       !PlumeGuestDeclarationReady(*declaration))) {
    const char* reason = !vertex_shader
                             ? "missing_vertex_shader_resource"
                             : (!pixel_shader
                                    ? "missing_pixel_shader_resource"
                                    : ((!synthetic_private_rect &&
                                        !synthetic_primitive8 && !declaration)
                                           ? "missing_declaration_resource"
                                           : (!PlumeGuestShaderReady(
                                                  *vertex_shader)
                                                  ? "vertex_shader_not_ready"
                                                  : (!PlumeGuestShaderReady(
                                                         *pixel_shader)
                                                         ? "pixel_shader_not_ready"
                                                         : "declaration_not_ready"))));
    LogDirectPipelineNotReadyLocked(reason, command, vertex_shader,
                                    pixel_shader, declaration,
                                    synthetic_private_rect);
    g_direct_renderer.diagnostics.logged_missing_draw_resource = true;
    return nullptr;
  }

  const bool force_no_cull_memo =
      IsFableTitleSceneFramebuffer(framebuffer) ||
      IsFableTitleSceneDrawTargetLocked(command, framebuffer);
  // Memo bypass: a stream whose stride can only come from the tracked-buffer
  // registry fallback (see the memo comment above).
  bool pipeline_memo_bypass = false;
  for (size_t stream_index = 0;
       stream_index < command.resources.streams.size(); ++stream_index) {
    const RendererStreamBindingSnapshot& stream =
        command.resources.streams[stream_index];
    if (stream.buffer == 0 || stream.stride != 0) {
      continue;
    }
    const FableDirectInlineBuffer& inline_stride_source =
        command.inline_vertex_buffers[stream_index];
    if (!(inline_stride_source.ready() &&
          inline_stride_source.stride_or_format != 0)) {
      pipeline_memo_bypass = true;
      break;
    }
  }
  uint64_t pipeline_memo_key = 0;
  if (!pipeline_memo_bypass) {
    uint64_t h = 1469598103934665603ull;
    const auto mix = [&h](uint64_t value) {
      h ^= value;
      h *= 1099511628211ull;
    };
    const auto mix_float = [&mix](float value) {
      uint32_t bits = 0;
      std::memcpy(&bits, &value, sizeof(bits));
      mix(bits);
    };
    mix(command.resources.vertex_shader);
    mix(command.resources.pixel_shader);
    mix(command.resources.vertex_declaration);
    mix(vertex_shader->guest_shader.bytecode_hash);
    mix(pixel_shader->guest_shader.bytecode_hash);
    mix(vertex_shader->serial);
    if (declaration != nullptr) {
      mix(declaration->guest_vertex_declaration.elements_hash);
      mix(declaration->serial);
    } else {
      mix(synthetic_private_rect ? 0xA5A5A5A5ull : 0x5A5A5A5Aull);
    }
    mix(command.draw.draw.primitive_type);
    mix(command.draw.draw.primitive_count);
    mix((command.draw.draw.indexed ? 1u : 0u) |
        (synthetic_private_rect ? 2u : 0u) | (synthetic_primitive8 ? 4u : 0u) |
        (force_no_cull_memo ? 8u : 0u) |
        (command.draw.private_texture_rectangle ? 16u : 0u) |
        (command.draw.private_texture_corners_valid ? 32u : 0u) |
        (DirectCommandIsExactBinkIndexedWrapperDrawLocked(command) ? 64u
                                                                  : 0u) |
        (command.viewport.valid ? 128u : 0u) |
        (command.viewport_guest_space ? 256u : 0u) |
        (command.pipeline.pm4.valid ? 512u : 0u));
    mix(static_cast<uint32_t>(command.draw.source));
    mix(command.draw.private_texture_wrapper_symbol_address);
    for (size_t stream_index = 0;
         stream_index < command.resources.streams.size(); ++stream_index) {
      const RendererStreamBindingSnapshot& stream =
          command.resources.streams[stream_index];
      const FableDirectInlineBuffer& inline_buffer =
          command.inline_vertex_buffers[stream_index];
      mix(stream.buffer);
      mix((uint64_t(stream.offset) << 32) | stream.stride);
      mix((uint64_t(inline_buffer.stride_or_format) << 32) |
          (uint64_t(inline_buffer.byte_size) << 1) |
          (inline_buffer.ready() ? 1u : 0u));
    }
    for (const uint32_t render_state : command.pipeline.render_states) {
      mix(render_state);
    }
    const FableDirectPm4State& pm4 = command.pipeline.pm4;
    mix(pm4.rb_surface_info);
    for (const uint32_t color_info : pm4.rb_color_info) {
      mix(color_info);
    }
    mix(pm4.rb_depth_info);
    mix(pm4.rb_modecontrol);
    mix(pm4.rb_depthcontrol);
    mix(pm4.rb_color_mask);
    for (const uint32_t blend_control : pm4.rb_blendcontrol) {
      mix(blend_control);
    }
    mix(pm4.rb_colorcontrol);
    mix(pm4.rb_alpha_ref);
    mix(pm4.rb_stencilrefmask);
    mix(pm4.rb_stencilrefmask_bf);
    mix(pm4.pa_su_sc_mode_cntl);
    mix_float(command.viewport.x);
    mix_float(command.viewport.y);
    mix_float(command.viewport.width);
    mix_float(command.viewport.height);
    mix_float(command.viewport.min_depth);
    mix_float(command.viewport.max_depth);
    mix(framebuffer.color_count);
    for (const plume::RenderFormat color_format : framebuffer.color_formats) {
      mix(static_cast<uint32_t>(color_format));
    }
    mix(static_cast<uint32_t>(framebuffer.depth_format));
    // DirectColorWriteMaskForFramebufferTarget matches command RT slots
    // against framebuffer attachment addresses, so both address sets are
    // pipeline-state inputs.
    for (const uint32_t render_target : command.resources.render_targets) {
      mix(render_target);
    }
    mix(command.resources.depth_stencil);
    for (const uint32_t color_address : framebuffer.color_addresses) {
      mix(color_address);
    }
    mix(framebuffer.depth_address);
    pipeline_memo_key = h;
    const auto memo_it = g_direct_pipeline_ensure_memo.find(pipeline_memo_key);
    if (memo_it != g_direct_pipeline_ensure_memo.end()) {
      ++g_direct_draw_phases.pipeline_memo_hits;
      ++g_direct_renderer.stats.pipeline_cache_hits;
      return memo_it->second;
    }
    ++g_direct_draw_phases.pipeline_memo_misses;
  } else {
    ++g_direct_draw_phases.pipeline_memo_bypass;
  }

  std::vector<plume::RenderInputSlot> input_slots;
  std::vector<plume::RenderInputElement> input_elements;
  if (synthetic_private_rect) {
    BuildSyntheticPrivateRectInputLayout(input_slots, input_elements);
  } else if (synthetic_primitive8) {
    BuildSyntheticPrimitive8RectangleInputLayout(input_slots, input_elements);
  } else {
    input_slots = declaration->guest_vertex_declaration.input_slots;
    input_elements = declaration->guest_vertex_declaration.input_elements;
    NormalizeInputElementFormatsToBoundStreams(
        command.resources.streams.data(), command.resources.streams.size(),
        input_elements);
    AlignDirectInputLayoutToVertexShaderLocked(*vertex_shader, input_slots,
                                               input_elements);
    ResolveDirectInputSlotStridesLocked(command, input_slots);
    TrimInputSlotsToElements(input_slots, input_elements);
  }
  if (input_slots.empty() || input_elements.empty()) {
    LogDirectPipelineNotReadyLocked(
        input_elements.empty() ? "empty_input_elements" : "empty_input_slots",
        command, vertex_shader, pixel_shader, declaration,
        synthetic_private_rect, input_slots.size(), input_elements.size());
    return nullptr;
  }

  const bool force_no_cull = force_no_cull_memo;
  const DirectPipelineKey key =
      BuildDirectPipelineKey(command, input_slots, input_elements, framebuffer,
                             force_no_cull);
  const auto remember_pipeline_memo = [&](plume::RenderPipeline* pipeline) {
    if (pipeline_memo_bypass || pipeline == nullptr) {
      return;
    }
    if (g_direct_pipeline_ensure_memo.size() >= kMaxDirectPipelineEnsureMemo) {
      g_direct_pipeline_ensure_memo.clear();
    }
    g_direct_pipeline_ensure_memo.emplace(pipeline_memo_key, pipeline);
  };
  auto pipeline_it = g_direct_renderer.pipelines.find(key);
  if (pipeline_it != g_direct_renderer.pipelines.end()) {
    ++g_direct_renderer.stats.pipeline_cache_hits;
    remember_pipeline_memo(pipeline_it->second.get());
    return pipeline_it->second.get();
  }

  // UNKNOWN TOPOLOGY IS A PROCESS-ENDING HAZARD, NOT A COSMETIC GAP. plume
  // asserts on it while building the pipeline (Debug is the only build this
  // project runs), and if the assert is ever compiled out it hands Vulkan an
  // out-of-range VkPrimitiveTopology. Refuse the draw instead: a dropped draw
  // is a missing object, a bad pipeline is a dead device.
  //
  // The live case (run 535, first in-world run): the hardware tessellator's
  // patch draws state primitive_type 0x12, which ConvertPrimitiveTopology has
  // no mapping for. Those draws are supposed to take the tess pipeline, but
  // their tess VS variant is missing (`TESS-DRAW tess-variant VS MISS
  // hash=0`), so 447 patch draws/frame fall out of the tess path and land
  // here. Do NOT "fix" this by mapping 0x12 to TRIANGLE_LIST: a patch stream
  // is not a triangle stream, and feeding it as one is exactly the kind of
  // malformed submission that loses the device. The correct fix is upstream —
  // regenerate the tess artifacts so the tess path takes them.
  const plume::RenderPrimitiveTopology topology =
      ConvertPrimitiveTopology(command.draw.draw.primitive_type);
  if (topology == plume::RenderPrimitiveTopology::UNKNOWN) {
    static std::unordered_set<uint32_t> logged_unknown_primitive_types;
    if (logged_unknown_primitive_types.insert(command.draw.draw.primitive_type)
            .second) {
      REXLOG_WARN(
          "Refii plume REFUSING draw with unmapped primitive type {} "
          "(0x{:02X}) vs=0x{:08X} ps=0x{:08X} rt=0x{:08X} — no Vulkan topology "
          "exists for it; submitting would assert or fault the device",
          command.draw.draw.primitive_type, command.draw.draw.primitive_type,
          command.resources.vertex_shader, command.resources.pixel_shader,
          command.resources.render_targets[0]);
    }
    return nullptr;
  }

  plume::RenderGraphicsPipelineDesc desc;
  desc.pipelineLayout = g_plume_resources.pipeline_layout.get();
  desc.vertexShader = vertex_shader->guest_shader.shader.get();
  desc.pixelShader = pixel_shader->guest_shader.shader.get();
  desc.primitiveTopology = topology;
  const uint32_t color_count = key.color_count;
  for (uint32_t slot = 0; slot < color_count; ++slot) {
    desc.renderTargetFormat[slot] =
        key.color_formats[slot] != plume::RenderFormat::UNKNOWN
            ? key.color_formats[slot]
            : plume::RenderFormat::B8G8R8A8_UNORM;
    desc.renderTargetBlend[slot] = DirectBlendDescFromEffectiveState(
        key.blend_enabled[slot], key.blend_controls[slot],
        key.color_write_masks[slot]);
  }
  desc.renderTargetCount = color_count;
  desc.inputSlots = input_slots.data();
  desc.inputSlotsCount = static_cast<uint32_t>(input_slots.size());
  desc.inputElements = input_elements.data();
  desc.inputElementsCount = static_cast<uint32_t>(input_elements.size());
  plume::RenderSpecConstant spec_constant;
  if (key.spec_constants != 0) {
    spec_constant = plume::RenderSpecConstant(0, key.spec_constants);
    desc.specConstants = &spec_constant;
    desc.specConstantsCount = 1;
  }
  desc.cullMode = ConvertFableCullMode(key.cull_control);
  if (key.force_no_cull) {
    desc.cullMode = plume::RenderCullMode::NONE;
  }
  desc.frontFace = ConvertFableFrontFace(key.cull_control);
  if (key.depth_format != plume::RenderFormat::UNKNOWN) {
    const uint32_t depth_control = key.depth_control;
    const uint32_t stencil_ref_mask = key.stencil_ref_mask;
    const uint32_t stencil_ref_mask_bf = key.stencil_ref_mask_bf;
    desc.depthTargetFormat = key.depth_format;
    desc.depthEnabled = (depth_control & 0x2u) != 0;
    desc.depthWriteEnabled = (depth_control & 0x4u) != 0;
    desc.depthFunction = ConvertFableCompare(
        (depth_control >> 4u) & 0x7u,
        plume::RenderComparisonFunction::LESS_EQUAL);
    desc.stencilEnabled = (depth_control & 0x1u) != 0;
    desc.stencilReference = stencil_ref_mask & 0xFFu;
    desc.stencilReadMask = (stencil_ref_mask >> 8u) & 0xFFu;
    desc.stencilWriteMask = (stencil_ref_mask >> 16u) & 0xFFu;
    desc.stencilFrontFace =
        FableStencilFaceFromDepthControl(depth_control, false);
    desc.stencilBackFace =
        FableStencilFaceFromDepthControl(depth_control, true);
    if (desc.stencilEnabled && stencil_ref_mask_bf != stencil_ref_mask) {
      desc.stencilReadMask =
          std::max(desc.stencilReadMask, (stencil_ref_mask_bf >> 8u) & 0xFFu);
      desc.stencilWriteMask =
          std::max(desc.stencilWriteMask, (stencil_ref_mask_bf >> 16u) & 0xFFu);
    }
  }

  if (g_direct_renderer.diagnostics.logged_effective_pipeline_states < 96u) {
    REXLOG_INFO(
        "Refii plume effective pipeline state #{} frame={} serial={} source={} "
        "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
        "topology={} colors={} color0_fmt={} depth_fmt={} "
        "depth_control=0x{:08X} depth_enabled={} depth_write={} "
        "depth_func={} stencil_enabled={} stencil_ref={} stencil_read={} "
        "stencil_write={} cull_control=0x{:08X} cull_mode={} front_face={} "
        "force_no_cull={} spec=0x{:08X} color_mask0=0x{:X} blend0={} "
        "blend0_control=0x{:08X} input_slots={} input_elements={}",
        ++g_direct_renderer.diagnostics.logged_effective_pipeline_states,
        g_direct_renderer.current_frame_index,
        static_cast<unsigned long long>(command.serial),
        static_cast<uint32_t>(command.draw.source),
        command.resources.render_targets[0], command.resources.depth_stencil,
        command.resources.vertex_shader, command.resources.pixel_shader,
        static_cast<uint32_t>(key.topology), key.color_count,
        static_cast<uint32_t>(key.color_formats[0]),
        static_cast<uint32_t>(key.depth_format), key.depth_control,
        desc.depthEnabled ? 1u : 0u, desc.depthWriteEnabled ? 1u : 0u,
        static_cast<uint32_t>(desc.depthFunction),
        desc.stencilEnabled ? 1u : 0u, desc.stencilReference,
        desc.stencilReadMask, desc.stencilWriteMask, key.cull_control,
        static_cast<uint32_t>(desc.cullMode),
        static_cast<uint32_t>(desc.frontFace), key.force_no_cull ? 1u : 0u,
        key.spec_constants, key.color_write_masks[0],
        key.blend_enabled[0] ? 1u : 0u, key.blend_controls[0],
        input_slots.size(), input_elements.size());
  }

  std::unique_ptr<plume::RenderPipeline> pipeline =
      device->createGraphicsPipeline(desc);
  if (!pipeline) {
    return nullptr;
  }
  char name[160] = {};
  std::snprintf(name, sizeof(name),
                "Refii pipeline VS_%016llX PS_%016llX RT0_%u DT_%u "
                "TOPO_%u BLEND0_%u SPEC_%08X CULL_%08X vsobj_%08X psobj_%08X "
                "decl_%08X",
                static_cast<unsigned long long>(key.vertex_shader_hash),
                static_cast<unsigned long long>(key.pixel_shader_hash),
                static_cast<uint32_t>(key.color_formats[0]),
                static_cast<uint32_t>(key.depth_format),
                static_cast<uint32_t>(key.topology),
                key.blend_enabled[0] ? 1u : 0u, key.spec_constants,
                key.cull_control, command.resources.vertex_shader,
                command.resources.pixel_shader, command.resources.vertex_declaration);
  pipeline->setName(name);
  plume::RenderPipeline* pipeline_ptr = pipeline.get();
  g_direct_renderer.pipelines.emplace(key, std::move(pipeline));
  ++g_direct_renderer.stats.pipeline_cache_misses;
  remember_pipeline_memo(pipeline_ptr);
  return pipeline_ptr;
}

std::unique_ptr<plume::RenderBuffer> CreateInlineDrawBufferLocked(
    plume::RenderDevice* device, const FableDirectInlineBuffer& inline_buffer,
    bool index_buffer, uint32_t slot) {
  if (!device || !inline_buffer.ready()) {
    return nullptr;
  }
  char name[128] = {};
  if (index_buffer) {
    std::snprintf(name, sizeof(name),
                  "Refii Fable direct inline IB guest=0x%08X bytes=%u",
                  inline_buffer.address, inline_buffer.byte_size);
  } else {
    std::snprintf(name, sizeof(name),
                  "Refii Fable direct inline VB%u guest=0x%08X bytes=%u",
                  slot, inline_buffer.address, inline_buffer.byte_size);
  }
  return CreateUploadBufferWithData(
      device, inline_buffer.data.data(), inline_buffer.byte_size, name,
      index_buffer ? plume::RenderBufferFlag::INDEX
                   : plume::RenderBufferFlag::VERTEX);
}

plume::RenderBuffer* EnsureDirectDefaultVertexInputBufferLocked(
    plume::RenderDevice* device) {
  if (!device) {
    return nullptr;
  }
  if (g_plume_resources.default_vertex_input_buffer) {
    return g_plume_resources.default_vertex_input_buffer.get();
  }

  static constexpr std::array<uint32_t, 4> kZeroVertexInput = {};
  g_plume_resources.default_vertex_input_buffer = CreateUploadBufferWithData(
      device, kZeroVertexInput.data(),
      kZeroVertexInput.size() * sizeof(kZeroVertexInput[0]),
      "Refii Fable default vertex input", plume::RenderBufferFlag::VERTEX);
  return g_plume_resources.default_vertex_input_buffer.get();
}

uint64_t DirectPixelShaderHashLocked(uint32_t shader) {
  const PlumeTrackedResource* resource = FindDirectResourceLocked(shader);
  if (resource && resource->guest_shader.bytecode_hash != 0) {
    return resource->guest_shader.bytecode_hash;
  }
  const FableShaderIdentity* identity =
      FableDirectFindShaderIdentityByObject(shader);
  return identity ? identity->bytecode_hash : 0ull;
}

std::unique_ptr<plume::RenderBuffer> CreateInlineVertexBufferLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    const FableDirectInlineBuffer& inline_buffer, uint32_t slot,
    uint32_t view_offset) {
  (void)view_offset;
  (void)command;
  // Scratch/inline vertex payloads are already normalized by the observer's
  // upload capture path. Swapping again here corrupts Fable title/menu
  // coordinates immediately before the draw is submitted.
  return CreateInlineDrawBufferLocked(device, inline_buffer, false, slot);
}

plume::RenderBuffer* GetOrCreateCachedInlineVertexBufferLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    const FableDirectInlineBuffer& inline_buffer, uint32_t slot) {
  (void)command;
  if (!device || !inline_buffer.ready()) {
    return nullptr;
  }
  constexpr size_t kMaxCachedUploadBytes = 64u * 1024u;
  if (inline_buffer.byte_size > kMaxCachedUploadBytes) {
    return nullptr;
  }

  const uint8_t* payload = inline_buffer.data.data();
  // The cache key's content hash is stamped ON the payload where its bytes
  // were written (StoreInlineScratchBuffer, the GTL authority fills) and
  // travels with every copy, so re-recorded commands reuse it with zero
  // per-bind hashing. (A serial-keyed bind memo tried this before and NEVER
  // hit: command serials are fresh on every re-record — runs 486-495 showed
  // upload_hits=0 against ~1189 full hashes per compose.) Zero means the
  // payload bypassed every stamping path — hash it lazily here, censused as
  // upload_misses. Collisions stay discriminated by the bucket memcmp inside
  // GetOrCreateCachedUploadBufferForKeyLocked.
  uint64_t content_hash = inline_buffer.content_hash;
  if (content_hash != 0) {
    ++g_direct_draw_phases.vbind_upload_stored_hash;
  } else {
    content_hash = DirectHashBytes(payload, inline_buffer.byte_size);
    ++g_direct_draw_phases.vbind_upload_lazy_hash;
  }

  const DirectUploadBufferKey key{
      content_hash, inline_buffer.byte_size,
      static_cast<uint32_t>(plume::RenderBufferFlag::VERTEX)};
  // Hit path first, with no name formatting: the steady-state title compose
  // re-binds ~150 unchanged payloads per record and paid a 128-byte snprintf
  // per hit purely to have a name ready for a creation that never happened.
  if (plume::RenderBuffer* found = FindCachedUploadBufferForKeyLocked(
          key, payload, inline_buffer.byte_size)) {
    return found;
  }
  char name[128] = {};
  std::snprintf(name, sizeof(name),
                "Refii Fable cached inline VB%u guest=0x%08X bytes=%u", slot,
                inline_buffer.address, inline_buffer.byte_size);
  return GetOrCreateCachedUploadBufferForKeyLocked(
      device, key, payload, inline_buffer.byte_size, name,
      plume::RenderBufferFlag::VERTEX);
}

bool ShouldExpandInlinePrimitive8Rectangle(
    const FableDirectRenderCommand& command, uint32_t slot) {
  return ShouldExpandInlinePrimitive8Rectangle(
      command, slot, UseSyntheticPrivateRectInputLayout(command));
}

bool ShouldExpandPrimitive8Rectangle(
    const FableDirectRenderCommand& command, uint32_t slot) {
  const uint32_t stride = ResolveDirectInputSlotStrideLocked(command, slot);
  return ShouldExpandPrimitive8Rectangle(
      command, slot, UseSyntheticPrivateRectInputLayout(command), stride);
}

uint32_t DirectDrawVertexOrIndexCount(const FableDirectRenderCommand& command) {
  if (command.draw.vertex_or_index_count != 0) {
    return command.draw.vertex_or_index_count;
  }
  if (ShouldExpandPrimitive8Rectangle(command, 0)) {
    return 6u;
  }
  if (DirectCommandIsExactBinkIndexedWrapperDrawLocked(command)) {
    return command.draw.draw.primitive_count;
  }
  if (DirectDrawUsesSyntheticQuadListIndices(command)) {
    return DirectQuadListIndexCount(command.draw.draw.primitive_count);
  }
  return DrawVertexOrIndexCount(command.draw.draw.primitive_type,
                                command.draw.draw.primitive_count);
}

uint32_t DirectDrawVertexInputCount(const FableDirectRenderCommand& command) {
  if (ShouldExpandPrimitive8Rectangle(command, 0)) {
    return 6u;
  }
  if (DirectDrawUsesSyntheticQuadListIndices(command)) {
    return command.draw.draw.primitive_count;
  }
  return DrawVertexOrIndexCount(command.draw.draw.primitive_type,
                                command.draw.draw.primitive_count);
}

bool DirectCommandIsTitleWrapperCopyProofDrawLocked(
    const FableDirectRenderCommand& command) {
  const bool has_wrapper_root =
      command.draw.private_texture_wrapper_call_serial != 0 ||
      command.draw.private_texture_wrapper_context_key != 0 ||
      command.draw.private_texture_wrapper_symbol_address != 0;
  if (!has_wrapper_root || command.draw.draw.indexed ||
      command.draw.source == RendererDrawEventSource::kPixVideo) {
    return false;
  }

  const uint32_t render_target = command.resources.render_targets[0];
  if (!FableTitleTargetIsExactVisibleOrScene(render_target) &&
      !IsFableTitleSceneTargetLocked(render_target)) {
    return false;
  }

  if (command.draw.draw.primitive_type != 13u &&
      !DirectCommandIsTitleVisibleProofCandidateLocked(command) &&
      !DirectCommandIsTitleGuiStripProofCandidateLocked(command)) {
    return false;
  }

  return command.resources.vertex_shader != 0 &&
         command.resources.pixel_shader != 0 &&
         command.resources.vertex_declaration != 0 &&
         std::any_of(command.resources.textures.begin(),
                     command.resources.textures.end(),
                     [](uint32_t texture) { return texture != 0; });
}

const char* ClassifyDirectRenderPassLocked(
    const FableDirectRenderCommand& command) {
  if (command.draw.source == RendererDrawEventSource::kPixVideo ||
      DirectCommandIsExactBinkIndexedWrapperDrawLocked(command) ||
      IsRenderDocEarlyBinkWrapperDrawTargetLocked(command) ||
      IsRenderDocStartupVideoCopyDrawTargetLocked(command)) {
    return "Video";
  }
  if (command.draw.source ==
          RendererDrawEventSource::kPrivateTextureRectangle &&
      command.draw.private_texture_rectangle) {
    if (DirectSyntheticPrivateRectCommandUsesDepthTarget(command)) {
      return "PrivateRectDepth";
    }
    if (IsFableSyntheticPrivateRectCopyShader(
            command.resources.pixel_shader)) {
      return "PrivateRectCopy";
    }
    return "PrivateRect";
  }
  if (DirectCommandIsTitleWrapperCopyProofDrawLocked(command)) {
    return "PrivateRectCopy";
  }
  // Topology bookkeeping: remember every depth target, mark depth targets
  // sampled as textures (their writers are shadow passes), and track color
  // targets written this frame (their readers are post-process passes).
  // Inserting the command's own targets after classification keeps repeat
  // calls for the same command stable.
  const auto note_command_topology = [&command]() {
    if (command.resources.depth_stencil != 0) {
      g_direct_renderer.known_depth_targets.insert(
          command.resources.depth_stencil);
    }
    for (uint32_t target : command.resources.render_targets) {
      if (target != 0) {
        g_direct_renderer.frame_written_color_targets.insert(target);
      }
    }
    for (uint32_t texture : command.resources.textures) {
      if (texture != 0 &&
          g_direct_renderer.known_depth_targets.count(texture) != 0) {
        g_direct_renderer.sampled_depth_targets.insert(texture);
      }
    }
  };
  const auto samples_frame_written_color_target = [&command]() {
    for (uint32_t texture : command.resources.textures) {
      if (texture == 0 ||
          g_direct_renderer.frame_written_color_targets.count(texture) == 0) {
        continue;
      }
      bool own_target = false;
      for (uint32_t target : command.resources.render_targets) {
        if (target != 0 && target == texture) {
          own_target = true;
          break;
        }
      }
      if (!own_target) {
        return true;
      }
    }
    return false;
  };
  if (command.resources.render_targets[0] == 0 &&
      command.resources.depth_stencil != 0) {
    const bool shadow = g_direct_renderer.sampled_depth_targets.count(
                            command.resources.depth_stencil) != 0;
    note_command_topology();
    return shadow ? "Shadow" : "DepthOnly";
  }
  if (command.resources.depth_stencil == 0 &&
      command.resources.render_targets[0] != 0 &&
      (command.draw.draw.primitive_type == 13u ||
       command.draw.draw.primitive_count <= 2u)) {
    const bool post_process = samples_frame_written_color_target();
    note_command_topology();
    return post_process ? "PostProcess" : "PostProcessOrUI";
  }
  note_command_topology();
  switch (command.draw.source) {
    case RendererDrawEventSource::kSceneObject:
      return "MainScene";
    case RendererDrawEventSource::kDrawVerticesUp:
      return "Immediate";
    case RendererDrawEventSource::kPm4Draw:
      return "PM4";
    case RendererDrawEventSource::kD3DWrapper:
    case RendererDrawEventSource::kScratchWrapper:
      return "WrapperDraw";
    default:
      return "Unknown";
  }
}

uint32_t ResolveFrameDatabaseIndexFormatLocked(
    const FableDirectRenderCommand& command) {
  if (!command.draw.draw.indexed) {
    return 0;
  }
  if (command.draw.index_format != 0) {
    return NormalizeDirectIndexFormat(command.draw.index_format);
  }
  if (command.inline_index_buffer.ready()) {
    return NormalizeDirectIndexFormat(
        command.inline_index_buffer.stride_or_format);
  }
  PlumeTrackedResource* index_buffer =
      FindDirectResourceLocked(command.resources.index_buffer);
  if (index_buffer && PlumeGuestBufferReady(*index_buffer)) {
    return NormalizeDirectIndexFormat(
        PlumeGuestBufferStrideOrFormat(*index_buffer));
  }
  return 0;
}

FableDirectFrameDatabaseDrawPacket BuildFrameDatabaseDrawPacketLocked(
    const FableDirectRenderCommand& command) {
  FableDirectFrameDatabaseDrawPacket packet;
  packet.packet_id =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  packet.command_serial = command.serial;
  packet.command_type = static_cast<uint32_t>(command.type);
  packet.source = static_cast<uint32_t>(command.draw.source);
  packet.pass_name = ClassifyDirectRenderPassLocked(command);
  packet.indexed = command.draw.draw.indexed;
  packet.primitive_type = command.draw.draw.primitive_type;
  packet.start = command.draw.draw.start;
  packet.primitive_count = command.draw.draw.primitive_count;
  packet.vertex_or_index_count = DirectDrawVertexOrIndexCount(command);
  packet.debug_label = BuildDirectDrawDebugLabelLocked(
      command, packet.vertex_or_index_count,
      DirectDrawUsesSyntheticQuadListIndices(command), packet.pass_name);
  packet.draw_root.call_serial = command.state_root.call_serial;
  packet.draw_root.context_key = command.state_root.context_key;
  packet.draw_root.symbol_address = command.state_root.symbol_address;
  packet.draw_root.lr = command.state_root.lr;
  packet.vertex_shader_root.call_serial =
      command.resources.shader_roots.vertex_shader.call_serial;
  packet.vertex_shader_root.context_key =
      command.resources.shader_roots.vertex_shader.context_key;
  packet.vertex_shader_root.symbol_address =
      command.resources.shader_roots.vertex_shader.symbol_address;
  packet.vertex_shader_root.lr = command.resources.shader_roots.vertex_shader.lr;
  packet.pixel_shader_root.call_serial =
      command.resources.shader_roots.pixel_shader.call_serial;
  packet.pixel_shader_root.context_key =
      command.resources.shader_roots.pixel_shader.context_key;
  packet.pixel_shader_root.symbol_address =
      command.resources.shader_roots.pixel_shader.symbol_address;
  packet.pixel_shader_root.lr = command.resources.shader_roots.pixel_shader.lr;
  packet.private_texture_wrapper_root.call_serial =
      command.draw.private_texture_wrapper_call_serial;
  packet.private_texture_wrapper_root.context_key =
      command.draw.private_texture_wrapper_context_key;
  packet.private_texture_wrapper_root.symbol_address =
      command.draw.private_texture_wrapper_symbol_address;
  packet.base_vertex = static_cast<int32_t>(command.draw.draw.base_vertex);
  packet.vertex_shader = command.resources.vertex_shader;
  packet.pixel_shader = command.resources.pixel_shader;
  packet.vertex_declaration = command.resources.vertex_declaration;
  packet.render_targets = command.resources.render_targets;
  packet.depth_stencil = command.resources.depth_stencil;
  packet.pipeline_pm4_valid = command.pipeline.pm4.valid;
  packet.depth_control = DirectDepthControl(command);
  packet.stencil_ref_mask = DirectStencilRefMask(command, false);
  packet.stencil_ref_mask_bf = DirectStencilRefMask(command, true);
  packet.cull_control = command.pipeline.pm4.valid
                            ? command.pipeline.pm4.pa_su_sc_mode_cntl
                            : command.pipeline
                                  .render_states[kFableRenderStateCullMode];
  packet.pa_su_sc_mode_cntl = command.pipeline.pm4.valid
                                  ? command.pipeline.pm4.pa_su_sc_mode_cntl
                                  : 0;
  for (size_t slot = 0; slot < packet.blend_controls.size(); ++slot) {
    packet.blend_controls[slot] =
        DirectBlendControlForTarget(command, static_cast<uint32_t>(slot));
  }
  const DirectFramebufferBinding frame_database_framebuffer =
      FrameDatabaseFramebufferBindingForCommand(command);
  for (size_t slot = 0; slot < packet.color_write_masks.size(); ++slot) {
    if (slot >= frame_database_framebuffer.color_count) {
      packet.color_write_masks[slot] = 0;
      continue;
    }
    packet.color_write_masks[slot] =
        frame_database_framebuffer.color_count != 0
            ? DirectColorWriteMaskForFramebufferTarget(
                  command, frame_database_framebuffer,
                  static_cast<uint32_t>(slot))
            : DirectColorWriteMaskForTarget(command,
                                            static_cast<uint32_t>(slot));
  }
  packet.color_control =
      command.pipeline.pm4.valid ? command.pipeline.pm4.rb_colorcontrol : 0;
  packet.alpha_ref =
      command.pipeline.pm4.valid
          ? command.pipeline.pm4.rb_alpha_ref
          : command.pipeline.render_states[kFableRenderStateAlphaRef];
  packet.alpha_test_enable =
      command.pipeline.render_states[kFableRenderStateAlphaTestEnable];
  packet.alpha_func = command.pipeline.render_states[kFableRenderStateAlphaFunc];
  packet.private_texture.enabled = command.draw.private_texture_rectangle;
  packet.private_texture.texture_slot = command.draw.private_texture_slot;
  packet.private_texture.source_x = command.draw.private_texture_source_x;
  packet.private_texture.source_y = command.draw.private_texture_source_y;
  packet.private_texture.corners_valid =
      command.draw.private_texture_corners_valid;
  packet.private_texture.corners = command.draw.private_texture_corners;
  packet.private_texture.uv_override =
      command.draw.private_texture_uv_override;
  packet.private_texture.u0 = command.draw.private_texture_u0;
  packet.private_texture.v0 = command.draw.private_texture_v0;
  packet.private_texture.u1 = command.draw.private_texture_u1;
  packet.private_texture.v1 = command.draw.private_texture_v1;
  packet.private_texture.wrapper_source_texture =
      command.draw.private_texture_wrapper_source_texture;
  packet.private_texture.wrapper_source_exact =
      command.draw.private_texture_wrapper_source_exact;
  packet.constants.fetch = command.constants.fetch;
  packet.constants.vertex_float = command.constants.vertex_float;
  packet.constants.pixel_float = command.constants.pixel_float;
  packet.constants.vertex_int = command.constants.vertex_int;
  packet.constants.pixel_int = command.constants.pixel_int;
  packet.constants.vertex_int_valid_mask =
      command.constants.vertex_int_valid_mask;
  packet.constants.pixel_int_valid_mask =
      command.constants.pixel_int_valid_mask;
  packet.constants.vertex_bool = command.constants.vertex_bool;
  packet.constants.pixel_bool = command.constants.pixel_bool;
  packet.index_buffer = command.resources.index_buffer;
  packet.index_format = ResolveFrameDatabaseIndexFormatLocked(command);
  packet.viewport_valid = command.viewport.valid;
  packet.viewport_x = command.viewport.x;
  packet.viewport_y = command.viewport.y;
  packet.viewport_width = command.viewport.width;
  packet.viewport_height = command.viewport.height;
  packet.viewport_min_depth = command.viewport.min_depth;
  packet.viewport_max_depth = command.viewport.max_depth;
  packet.scissor_valid = command.scissor.valid;
  packet.scissor_left = command.scissor.left;
  packet.scissor_top = command.scissor.top;
  packet.scissor_right = command.scissor.right;
  packet.scissor_bottom = command.scissor.bottom;

  for (size_t index = 0; index < packet.textures.size() &&
                         index < command.resources.textures.size();
       ++index) {
    packet.textures[index] = command.resources.textures[index];
  }
  for (size_t index = 0; index < packet.sampler_states.size() &&
                         index < command.sampler_states.size();
       ++index) {
    packet.sampler_states[index] = command.sampler_states[index];
  }
  for (size_t index = 0; index < packet.vertex_buffers.size() &&
                         index < command.resources.streams.size();
       ++index) {
    const RendererStreamBindingSnapshot& stream =
        command.resources.streams[index];
    packet.vertex_buffers[index] = stream.buffer;
    packet.vertex_strides[index] = stream.stride;
    packet.vertex_offsets[index] = stream.offset;
  }
  for (size_t index = 0; index < packet.inline_vertex_buffers.size() &&
                         index < command.inline_vertex_buffers.size();
       ++index) {
    packet.inline_vertex_buffers[index] =
        ToRefiiInlineBufferPayload(command.inline_vertex_buffers[index]);
  }
  packet.inline_index_buffer =
      ToRefiiInlineBufferPayload(command.inline_index_buffer);
  if (command.draw.source == RendererDrawEventSource::kPixVideo) {
    for (size_t index = 0; index < packet.pix_video_planes.size() &&
                           index < command.pix_video_planes.size();
         ++index) {
      packet.pix_video_planes[index] =
          ToRefiiInlineTexturePayload(command.pix_video_planes[index]);
    }
  }
  return packet;
}

FableDirectFrameDatabaseCommandPacket BuildFrameDatabaseCommandPacketLocked(
    const FableDirectRenderCommand& command) {
  FableDirectFrameDatabaseCommandPacket packet;
  packet.packet_id =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  packet.command_serial = command.serial;
  packet.command_type = static_cast<uint32_t>(command.type);
  packet.render_targets = command.resources.render_targets;
  packet.depth_stencil = command.resources.depth_stencil;
  if (IsDirectDrawCommand(command)) {
    packet.kind = "draw";
    packet.pass_name = ClassifyDirectRenderPassLocked(command);
    packet.draw_packet_id = packet.packet_id;
    packet.root.call_serial = command.state_root.call_serial;
    packet.root.context_key = command.state_root.context_key;
    packet.root.symbol_address = command.state_root.symbol_address;
    packet.root.lr = command.state_root.lr;
    return packet;
  }
  if (command.type == FableDirectRenderCommandType::kClear) {
    packet.kind = "clear";
    packet.pass_name = "Clear";
    packet.root.call_serial = command.clear.root_call_serial;
    packet.root.context_key = command.clear.root_context_key;
    packet.root.symbol_address = command.clear.root_symbol_address;
    packet.root.lr = command.clear.root_lr;
    packet.clear_flags = command.clear.flags;
    packet.clear_color = command.clear.color;
    packet.clear_depth = command.clear.depth;
    packet.clear_stencil = command.clear.stencil;
    packet.rect_valid = command.clear.rect.valid;
    packet.rect_left = command.clear.rect.left;
    packet.rect_top = command.clear.rect.top;
    packet.rect_right = command.clear.rect.right;
    packet.rect_bottom = command.clear.rect.bottom;
    return packet;
  }
  if (command.type == FableDirectRenderCommandType::kResolveCopy) {
    packet.kind = "resolve";
    packet.pass_name = "Resolve";
    uint32_t copy_control = DirectResolveCopyControl(command);
    const DirectResolveCopySourceSelection selection =
        SelectDirectResolveCopySourceLocked(command);
    if (selection.ready) {
      copy_control = (copy_control & ~0x7u) | (selection.source_slot & 0x7u);
    }
    packet.resolve_source_surface = selection.source_address;
    packet.resolve_source_slot = selection.source_slot;
    packet.resolve_source_depth = selection.depth_resolve;
    packet.resolve_surface_info = command.resolve.rb_surface_info;
    packet.resolve_color_info = command.resolve.rb_color_info;
    packet.resolve_depth_info = command.resolve.rb_depth_info;
    packet.resolve_copy_control = copy_control;
    packet.resolve_dest_base = DirectResolveCopyDestinationBase(command);
    packet.resolve_dest_pitch = command.resolve.rb_copy_dest_pitch != 0
                                    ? command.resolve.rb_copy_dest_pitch
                                    : command.pipeline.pm4.rb_copy_dest_pitch;
    packet.resolve_dest_info = command.resolve.rb_copy_dest_info != 0
                                   ? command.resolve.rb_copy_dest_info
                                   : command.pipeline.pm4.rb_copy_dest_info;
    packet.resolve_copy_func = command.resolve.rb_copy_func;
    packet.resolve_copy_ref = command.resolve.rb_copy_ref;
    packet.resolve_copy_mask = command.resolve.rb_copy_mask;
    packet.resolve_surface_slice = command.resolve.rb_copy_surface_slice;
    return packet;
  }
  packet.kind = "state";
  packet.pass_name = "State";
  return packet;
}

RefiiClearPacket BuildRefiiClearPacket(uint64_t frame_id,
                                       const FableDirectRenderCommand& command) {
  RefiiClearPacket packet;
  packet.packet_id = command.serial;
  packet.frame_id = frame_id;
  packet.command_serial = command.serial;
  packet.command_type = static_cast<uint32_t>(command.type);
  packet.render_targets = command.resources.render_targets;
  packet.depth_target = command.resources.depth_stencil;
  packet.root.call_serial = command.clear.root_call_serial;
  packet.root.context_key = command.clear.root_context_key;
  packet.root.symbol_address = command.clear.root_symbol_address;
  packet.root.lr = command.clear.root_lr;
  for (size_t index = 0; index < packet.render_target_roots.size(); ++index) {
    packet.render_target_roots[index] =
        ToRefiiPacketRoot(command.clear.target_roots.color[index]);
  }
  packet.depth_target_root =
      ToRefiiPacketRoot(command.clear.target_roots.depth);
  packet.flags = command.clear.flags;
  packet.color = command.clear.color;
  packet.depth = command.clear.depth;
  packet.stencil = command.clear.stencil;
  packet.rect.valid = command.clear.rect.valid;
  packet.rect.left = command.clear.rect.left;
  packet.rect.top = command.clear.rect.top;
  packet.rect.right = command.clear.rect.right;
  packet.rect.bottom = command.clear.rect.bottom;
  return packet;
}

RefiiResolvePacket BuildRefiiResolvePacket(
    uint64_t frame_id,
    const FableDirectRenderCommand& command) {
  RefiiResolvePacket packet;
  packet.packet_id = command.serial;
  packet.frame_id = frame_id;
  packet.command_serial = command.serial;
  packet.command_type = static_cast<uint32_t>(command.type);
  packet.render_targets = command.resources.render_targets;
  packet.depth_target = command.resources.depth_stencil;
  packet.root = ToRefiiPacketRoot(command.state_root);
  uint32_t copy_control = DirectResolveCopyControl(command);
  const DirectResolveCopySourceSelection selection =
      SelectDirectResolveCopySourceLocked(command);
  if (selection.ready) {
    copy_control = (copy_control & ~0x7u) | (selection.source_slot & 0x7u);
  }
  packet.surface_info = command.resolve.rb_surface_info;
  packet.color_info = command.resolve.rb_color_info;
  packet.depth_info = command.resolve.rb_depth_info;
  packet.copy_control = copy_control;
  packet.source_surface = selection.source_address;
  packet.source_slot = selection.source_slot;
  packet.source_depth = selection.depth_resolve;
  packet.dest_base = DirectResolveCopyDestinationBase(command);
  packet.dest_pitch = command.resolve.rb_copy_dest_pitch != 0
                          ? command.resolve.rb_copy_dest_pitch
                          : command.pipeline.pm4.rb_copy_dest_pitch;
  packet.dest_info = command.resolve.rb_copy_dest_info != 0
                         ? command.resolve.rb_copy_dest_info
                         : command.pipeline.pm4.rb_copy_dest_info;
  packet.copy_func = command.resolve.rb_copy_func;
  packet.copy_ref = command.resolve.rb_copy_ref;
  packet.copy_mask = command.resolve.rb_copy_mask;
  packet.surface_slice = command.resolve.rb_copy_surface_slice;
  return packet;
}

RefiiFrameActionPacket BuildRefiiFrameActionPacket(
    uint64_t frame_id,
    const FableDirectRenderCommand& command,
    uint32_t draw_packet_index) {
  RefiiFrameActionPacket action;
  action.frame_id = frame_id;
  action.command_serial = command.serial;
  action.command_type = static_cast<uint32_t>(command.type);
  action.packet_id =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  if (IsDirectDrawCommand(command)) {
    action.type = RefiiFrameActionType::kDraw;
    action.draw_packet_index = draw_packet_index;
  } else if (command.type == FableDirectRenderCommandType::kClear) {
    action.type = RefiiFrameActionType::kClear;
    action.clear = BuildRefiiClearPacket(frame_id, command);
  } else if (command.type == FableDirectRenderCommandType::kResolveCopy) {
    action.type = RefiiFrameActionType::kResolve;
    action.resolve = BuildRefiiResolvePacket(frame_id, command);
  }
  return action;
}

void CaptureFrameDatabaseDrawPacketsLocked(
    const std::vector<FableDirectRenderCommand>& commands) {
  g_direct_renderer.frame_database_draw_packets.clear();
  g_direct_renderer.frame_database_draw_packet_index_by_serial.clear();
  g_direct_renderer.frame_database_command_packets.clear();
  g_direct_renderer.frame_database_omitted_draw_packets = 0;
  g_direct_renderer.frame_database_omitted_command_packets = 0;
  const uint64_t previous_queue_generation =
      g_direct_renderer.refii_packet_queue.generation;
  g_direct_renderer.refii_packet_queue = {};
  g_direct_renderer.refii_packet_queue.frame_id =
      g_direct_renderer.current_frame_index;
  g_direct_renderer.refii_packet_queue.generation =
      previous_queue_generation + 1u;

  uint32_t draw_packet_count = 0;
  uint32_t action_packet_count = 0;
  for (const FableDirectRenderCommand& command : commands) {
    const bool draw_command = IsDirectDrawCommand(command);
    const bool action_command = draw_command ||
        command.type == FableDirectRenderCommandType::kClear ||
        command.type == FableDirectRenderCommandType::kResolveCopy;
    uint32_t draw_packet_index = UINT32_MAX;
    bool stored_draw_packet = false;
    if (draw_command) {
      ++draw_packet_count;
      if (g_direct_renderer.frame_database_draw_packets.size() <
          kMaxFrameDatabaseDrawPackets) {
        draw_packet_index = static_cast<uint32_t>(
            g_direct_renderer.frame_database_draw_packets.size());
        g_direct_renderer.frame_database_draw_packets.push_back(
            BuildFrameDatabaseDrawPacketLocked(command));
        g_direct_renderer
            .frame_database_draw_packet_index_by_serial[command.serial] =
            draw_packet_index;
        stored_draw_packet = true;
      }
    }
    if (action_command) {
      ++action_packet_count;
      if (g_direct_renderer.frame_database_command_packets.size() <
          kMaxFrameDatabaseCommandPackets) {
        g_direct_renderer.frame_database_command_packets.push_back(
            BuildFrameDatabaseCommandPacketLocked(command));
      }
      if ((!draw_command || stored_draw_packet) &&
          g_direct_renderer.refii_packet_queue.actions.size() <
              kMaxFrameDatabaseCommandPackets) {
        g_direct_renderer.refii_packet_queue.actions.push_back(
            BuildRefiiFrameActionPacket(
                g_direct_renderer.refii_packet_queue.frame_id, command,
                draw_packet_index));
      }
    }
  }
  if (draw_packet_count >
      g_direct_renderer.frame_database_draw_packets.size()) {
    g_direct_renderer.frame_database_omitted_draw_packets =
        draw_packet_count -
        static_cast<uint32_t>(
            g_direct_renderer.frame_database_draw_packets.size());
  }
  if (action_packet_count >
      g_direct_renderer.frame_database_command_packets.size()) {
    g_direct_renderer.frame_database_omitted_command_packets =
        action_packet_count -
        static_cast<uint32_t>(
            g_direct_renderer.frame_database_command_packets.size());
  }
  g_direct_renderer.refii_packet_queue.omitted_packets =
      g_direct_renderer.frame_database_omitted_draw_packets;
  if (action_packet_count >
      g_direct_renderer.refii_packet_queue.actions.size()) {
    g_direct_renderer.refii_packet_queue.omitted_actions =
        action_packet_count -
        static_cast<uint32_t>(
            g_direct_renderer.refii_packet_queue.actions.size());
  }
  g_direct_renderer.refii_packet_queue.packets.reserve(
      g_direct_renderer.frame_database_draw_packets.size());
  for (const FableDirectFrameDatabaseDrawPacket& packet :
       g_direct_renderer.frame_database_draw_packets) {
    g_direct_renderer.refii_packet_queue.packets.push_back(
        ToRefiiDrawPacket(g_direct_renderer.refii_packet_queue.frame_id,
                          packet));
  }
  g_direct_renderer.refii_packet_queue.replay = BuildRefiiPacketReplaySummary(
      g_direct_renderer.refii_packet_queue.packets,
      g_direct_renderer.refii_packet_queue.actions);
  g_direct_renderer.refii_packet_queue.replay_ready =
      !g_direct_renderer.refii_packet_queue.actions.empty() &&
      g_direct_renderer.refii_packet_queue.omitted_packets == 0 &&
      g_direct_renderer.refii_packet_queue.omitted_actions == 0 &&
      g_direct_renderer.refii_packet_queue.replay.unsupported_packets == 0;
}

uint32_t DirectMinimumVertexBufferViewBytes(
    const FableDirectRenderCommand& command, uint32_t stride) {
  if (stride == 0) {
    return 0;
  }
  if (command.draw.draw.indexed) {
    // Indexed draws need enough vertices for the referenced max index, not
    // enough vertices for every index. The index binder validates that once the
    // inline/tracked IB is available.
    return 0;
  }
  if (ShouldExpandPrimitive8Rectangle(command, 0)) {
    return command.draw.draw.primitive_count * stride;
  }
  const uint32_t count = DirectDrawVertexInputCount(command);
  if (count == 0) {
    return 0;
  }

  uint64_t vertex_count = count;
  if (command.draw.draw.indexed) {
    const int64_t base_vertex =
        static_cast<int64_t>(DirectDrawBaseVertex(command));
    if (base_vertex > 0) {
      vertex_count += static_cast<uint64_t>(base_vertex);
    }
  } else {
    vertex_count += command.draw.draw.start;
  }

  const uint64_t required = vertex_count * uint64_t(stride);
  return required <= std::numeric_limits<uint32_t>::max()
             ? static_cast<uint32_t>(required)
             : UINT32_MAX;
}


bool DirectInlineIndexReferencedVertexCount(
    const FableDirectRenderCommand& command, uint32_t& vertex_count) {
  vertex_count = 0;
  const FableDirectInlineBuffer& index_buffer = command.inline_index_buffer;
  if (!command.draw.draw.indexed || !index_buffer.ready()) {
    return false;
  }

  const uint32_t index_offset =
      DirectInlineBufferOffsetForCommandAddress(command, index_buffer,
                                                command.resources.index_buffer);
  if (index_offset == UINT32_MAX || index_offset >= index_buffer.data.size()) {
    return false;
  }
  const uint32_t stride =
      command.draw.index_format != 0
          ? NormalizeDirectIndexFormat(command.draw.index_format)
          : NormalizeDirectIndexFormat(index_buffer.stride_or_format);
  const uint8_t* index_data = index_buffer.data.data() + index_offset;
  const size_t index_size = index_buffer.data.size() - index_offset;
  const uint32_t index_count = DirectDrawVertexOrIndexCount(command);
  uint32_t max_index = 0;
  if (!DirectIndexBytesMaxIndex(index_data, index_size, stride,
                                command.draw.draw.start, index_count,
                                max_index, false) &&
      !DirectIndexBytesMaxIndex(index_data, index_size, stride,
                                command.draw.draw.start, index_count,
                                max_index, true)) {
    return false;
  }
  vertex_count = max_index + 1u;
  return vertex_count != 0;
}

bool DirectShouldBindInlineScratchVertexFromBase(
    const FableDirectRenderCommand& command,
    const FableDirectInlineBuffer& inline_buffer,
    const RendererStreamBindingSnapshot& stream, uint32_t stride) {
  auto address_looks_like_scratch = [](uint32_t address) {
    if (address == 0 || (address & (alignof(uint32_t) - 1u)) != 0) {
      return false;
    }
    return (address >= 0x1000u && address < 0x40000000u) ||
           address >= 0x80000000u;
  };
  // Non-indexed ring draws need the same rescue: the pinned payload is the
  // draw's own geometry (attached per-serial), while the stream pointer can
  // drift to a NEWER ring allocation when device-binding patches land late
  // in a compose burst (bind-reject forensics: inline one slot below the
  // stream, sizes equal). kD3DWrapper is the source these bursts arrive as.
  if (!inline_buffer.ready() || stride == 0 ||
      stream.buffer == 0 ||
      (command.draw.source != RendererDrawEventSource::kSceneObject &&
       command.draw.source != RendererDrawEventSource::kScratchWrapper &&
       command.draw.source != RendererDrawEventSource::kD3DWrapper) ||
      !address_looks_like_scratch(stream.buffer) ||
      !address_looks_like_scratch(inline_buffer.address)) {
    return false;
  }

  const uint32_t exact_offset =
      DirectInlineBufferOffsetForAddress(inline_buffer, stream.buffer);
  if (exact_offset == UINT32_MAX || exact_offset == 0 ||
      exact_offset >= inline_buffer.byte_size) {
    // A per-serial pinned payload is the draw's own geometry by
    // construction — serve it from base even when the stream pointer has
    // drifted past the payload's range entirely (run 083: 13KB of drift in
    // a long compose burst, sizes matching exactly). A payload whose byte
    // size EXACTLY equals this draw's requirement carries the same
    // provenance even when the attach path lost the flag (run 102 reject
    // #29: 3264==3264 bytes, stride 48, stream 4.8KB BELOW the payload —
    // reverse drift; these failed menu-page composes leave the smear
    // copies in the accumulating storybook composite).
    // Begin-family non-indexed draws carry VERTEX counts in the raw
    // primitive-count field (run 072); DirectDrawVertexOrIndexCount
    // expands primitives and overshoots (why run 103 still failed) — the
    // raw field times stride is the byte-exact identity.
    const uint64_t draw_exact_bytes =
        !command.draw.draw.indexed
            ? uint64_t(command.draw.draw.primitive_count) * stride
            : 0u;
    const bool per_draw_provenance =
        inline_buffer.pinned_per_draw ||
        (draw_exact_bytes != 0 &&
         draw_exact_bytes == inline_buffer.byte_size);
    if (per_draw_provenance && exact_offset != 0) {
      const uint32_t base_capacity_pinned =
          stream.offset < inline_buffer.byte_size
              ? static_cast<uint32_t>(
                    (uint64_t(inline_buffer.byte_size) - stream.offset) /
                    stride)
              : 0u;
      if (base_capacity_pinned >= 3u) {
        // Deletion census: this rescue had no counter, so earlier censuses
        // (grepping log lines it never emitted) wrongly read it as idle.
        static uint32_t served_pinned_drift = 0;
        ++served_pinned_drift;
        if (served_pinned_drift <= 8u ||
            (served_pinned_drift % 256u) == 0u) {
          REXLOG_INFO("Refii plume inline rescue served #{} reason=pinned_drift "
                      "serial={}",
                      served_pinned_drift,
                      static_cast<unsigned long long>(command.serial));
        }
        return true;
      }
      return false;
    }
    return false;
  }
  const uint64_t command_offset =
      uint64_t(exact_offset) + uint64_t(stream.offset);
  const uint32_t command_capacity =
      command_offset < inline_buffer.byte_size
          ? static_cast<uint32_t>(
                (uint64_t(inline_buffer.byte_size) - command_offset) / stride)
          : 0u;
  const uint32_t base_capacity =
      stream.offset < inline_buffer.byte_size
          ? static_cast<uint32_t>(
                (uint64_t(inline_buffer.byte_size) - stream.offset) / stride)
          : 0u;
  if (base_capacity <= command_capacity || base_capacity < 3u) {
    return false;
  }

  uint32_t referenced_vertices = 0;
  if (!DirectInlineIndexReferencedVertexCount(command, referenced_vertices)) {
    referenced_vertices = DirectDrawVertexOrIndexCount(command);
    if (!command.draw.draw.indexed && referenced_vertices > base_capacity) {
      // Begin-family draws carry VERTEX counts in the primitive-count field;
      // the primitive expansion overshoots the payload that the capture
      // sized from the wrapper's own count (run 072: prim=13 count=4 with a
      // 4-vertex, 192-byte payload expanded to 12 referenced vertices). The
      // per-draw payload IS the draw's geometry — serve it whole.
      referenced_vertices = base_capacity;
    }
  }
  if (referenced_vertices == 0 || referenced_vertices > base_capacity) {
    return false;
  }
  if (command_capacity < referenced_vertices) {
    static uint32_t served_in_range_drift = 0;
    ++served_in_range_drift;
    if (served_in_range_drift <= 8u || (served_in_range_drift % 256u) == 0u) {
      REXLOG_INFO(
          "Refii plume inline rescue served #{} reason=in_range_drift "
          "serial={}",
          served_in_range_drift,
          static_cast<unsigned long long>(command.serial));
    }
    return true;
  }
  return false;
}

std::vector<uint8_t> CopyLiveGuestStreamBytes(uint32_t guest_address,
                                                     uint32_t byte_size) {
  constexpr uint32_t kMaxLiveGuestStreamCopyBytes = 16u << 20;
  uint8_t* base = FableGuestMemoryBase();
  if (base == nullptr || byte_size == 0 ||
      byte_size > kMaxLiveGuestStreamCopyBytes) {
    return {};
  }
  uint8_t* src = bink::GuestMemoryPtr(base, guest_address, byte_size);
  if (src == nullptr) {
    return {};
  }
  std::vector<uint8_t> bytes(byte_size);
  std::memcpy(bytes.data(), src, byte_size);
  return bytes;
}

bool DirectInlineVertexBufferCoversDraw(
    const FableDirectRenderCommand& command,
    const FableDirectInlineBuffer& inline_buffer,
    const RendererStreamBindingSnapshot& stream, uint32_t slot,
    uint32_t required_view_bytes) {
  if (ShouldExpandInlinePrimitive8Rectangle(command, slot)) {
    return inline_buffer.ready();
  }
  // Mirror the bind loop's precedence: a rebind-from-base decision means the
  // pinned payload serves the draw from its own base regardless of where the
  // (possibly drifted) stream pointer lands inside it.
  if (DirectShouldBindInlineScratchVertexFromBase(
          command, inline_buffer, stream,
          ResolveDirectInputSlotStrideLocked(command, slot))) {
    return uint64_t(stream.offset) + uint64_t(required_view_bytes) <=
           uint64_t(inline_buffer.byte_size);
  }
  const uint32_t inline_base_offset =
      DirectInlineBufferOffsetForCommandAddress(command, inline_buffer,
                                                stream.buffer);
  if (inline_base_offset == UINT32_MAX) {
    return false;
  }
  const uint64_t combined_offset =
      uint64_t(inline_base_offset) + uint64_t(stream.offset);
  if (combined_offset >= inline_buffer.byte_size) {
    return false;
  }
  return required_view_bytes == 0 ||
         uint64_t(inline_buffer.byte_size) - combined_offset >=
             uint64_t(required_view_bytes);
}

uint32_t DirectPrimaryVertexCapacityLocked(
    const FableDirectRenderCommand& command) {
  const RendererStreamBindingSnapshot& stream = command.resources.streams[0];
  const uint32_t stride = ResolveDirectInputSlotStrideLocked(command, 0);
  if (stride == 0) {
    return 0;
  }

  const FableDirectInlineBuffer& inline_buffer =
      command.inline_vertex_buffers[0];
  if (DirectShouldBindInlineScratchVertexFromBase(command, inline_buffer,
                                                  stream, stride)) {
    const uint64_t base_offset = uint64_t(stream.offset);
    if (base_offset < inline_buffer.byte_size) {
      return static_cast<uint32_t>(
          (uint64_t(inline_buffer.byte_size) - base_offset) / stride);
    }
  }
  const uint32_t inline_offset =
      DirectInlineBufferOffsetForCommandAddress(command, inline_buffer,
                                                stream.buffer);
  if (inline_offset != UINT32_MAX) {
    const uint64_t combined_offset =
        uint64_t(inline_offset) + uint64_t(stream.offset);
    if (combined_offset < inline_buffer.byte_size) {
      return static_cast<uint32_t>(
          (uint64_t(inline_buffer.byte_size) - combined_offset) / stride);
    }
  }

  PlumeTrackedResource* buffer = FindDirectResourceLocked(stream.buffer);
  if (buffer && PlumeGuestBufferReady(*buffer)) {
    return DirectTrackedBufferViewBytes(*buffer, stream.offset) / stride;
  }
  return 0;
}

bool DirectIndexBufferLooksValidForCommand(
    const FableDirectRenderCommand& command, const uint8_t* data,
    size_t byte_size, uint32_t stride, uint32_t vertex_capacity,
    uint32_t& max_index, bool byte_swap) {
  return DirectIndexBufferLooksValid(
      data, byte_size, stride, command.draw.draw.start,
      DirectDrawVertexOrIndexCount(command), vertex_capacity, max_index,
      byte_swap);
}

uint32_t DirectInlineBufferOffsetForCommandAddress(
    const FableDirectRenderCommand& command,
    const FableDirectInlineBuffer& buffer, uint32_t address) {
  const bool scratch_wrapper =
      command.draw.source == RendererDrawEventSource::kScratchWrapper;
  if (scratch_wrapper &&
      DirectCommandIsExactBinkIndexedWrapperDrawLocked(command) &&
      buffer.ready() && address != 0 &&
      DirectAddressLooksLikeScratchBuffer(address) &&
      DirectAddressLooksLikeScratchBuffer(buffer.address)) {
    return 0;
  }
  const uint32_t exact_offset =
      DirectInlineBufferOffsetForAddress(buffer, address);
  if (exact_offset != UINT32_MAX) {
    return exact_offset;
  }
  if (!buffer.ready() || address == 0 ||
      (!scratch_wrapper &&
       !DirectCommandIsExactBinkIndexedWrapperDrawLocked(command))) {
    return UINT32_MAX;
  }
  if (!DirectAddressLooksLikeScratchBuffer(address) ||
      !DirectAddressLooksLikeScratchBuffer(buffer.address)) {
    return UINT32_MAX;
  }
  return 0;
}

bool BindDirectTrackedIndexBufferLocked(
    plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& index_buffer, uint32_t vertex_capacity,
    bool validate_shadow) {
  if (!command_list || !PlumeGuestBufferReady(index_buffer)) {
    return false;
  }
  const uint32_t stride =
      command.draw.index_format != 0
          ? NormalizeDirectIndexFormat(command.draw.index_format)
          : NormalizeDirectIndexFormat(
                PlumeGuestBufferStrideOrFormat(index_buffer));
  const uint64_t required_bytes =
      (uint64_t(command.draw.draw.start) +
       uint64_t(DirectDrawVertexOrIndexCount(command))) *
      uint64_t(stride);
  const uint32_t size = PlumeGuestBufferByteSize(index_buffer);
  if (required_bytes > size) {
    return false;
  }

  uint32_t max_index = 0;
  const bool has_shadow = !index_buffer.index_shadow_upload.empty();
  if (validate_shadow && has_shadow &&
      !DirectIndexBufferLooksValidForCommand(
          command, index_buffer.index_shadow_upload.data(),
          index_buffer.index_shadow_upload.size(), stride, vertex_capacity,
          max_index)) {
    return false;
  }
  const plume::RenderFormat format =
      stride == 4u ? plume::RenderFormat::R32_UINT
                   : plume::RenderFormat::R16_UINT;
  plume::RenderIndexBufferView view(index_buffer.guest_buffer.buffer->at(0),
                                    size, format);
  command_list->setIndexBuffer(&view);
  return true;
}


bool BindDirectVertexInputLocked(plume::RenderDevice* device,
                                 plume::RenderCommandList* command_list,
                                 const FableDirectRenderCommand& command) {
  if (!device || !command_list) {
    return false;
  }
  // vbindsplit: phase boundaries accumulate on the success path; an early
  // reject drops its unfinished phase (the parent vbind= still carries it).
  const FableDirectClock::time_point vbind_t0 = FableDirectClock::now();
  const bool synthetic_private_rect =
      UseSyntheticPrivateRectInputLayout(command);
  const bool synthetic_primitive8 =
      ShouldExpandPrimitive8Rectangle(command, 0);
  PlumeTrackedResource* declaration =
      (synthetic_private_rect || synthetic_primitive8)
          ? nullptr
          : FindDirectReadyDeclarationResourceByIdentityLocked(
                command.resources.vertex_declaration);
  if (!synthetic_private_rect && !synthetic_primitive8 &&
      !declaration) {
    static uint32_t logged_declaration_bind_rejects = 0;
    if (logged_declaration_bind_rejects < 32u) {
      REXLOG_INFO(
          "Refii plume vertex bind reject #{} serial={} reason=declaration "
          "decl=0x{:08X} vs=0x{:08X} vb0=0x{:08X}/{}",
          ++logged_declaration_bind_rejects,
          static_cast<unsigned long long>(command.serial),
          command.resources.vertex_declaration,
          command.resources.vertex_shader, command.resources.streams[0].buffer,
          command.resources.streams[0].stride);
    }
    return false;
  }

  std::vector<plume::RenderInputSlot> input_slots;
  std::vector<plume::RenderInputElement> input_elements;
  const std::vector<plume::RenderInputSlot>* required_slots = nullptr;
  if (synthetic_private_rect) {
    input_slots.emplace_back(0, 48);
    required_slots = &input_slots;
  } else if (synthetic_primitive8) {
    input_slots.emplace_back(0, 8);
    required_slots = &input_slots;
  } else {
    input_slots = declaration->guest_vertex_declaration.input_slots;
    input_elements = declaration->guest_vertex_declaration.input_elements;
    NormalizeInputElementFormatsToBoundStreams(
        command.resources.streams.data(), command.resources.streams.size(),
        input_elements);
    if (const PlumeTrackedResource* vertex_shader =
            FindDirectReadyShaderResourceByIdentityLocked(
                command.resources.vertex_shader,
                FableGuestResourceKind::kVertexShader)) {
      AlignDirectInputLayoutToVertexShaderLocked(*vertex_shader, input_slots,
                                                 input_elements);
    }
    ResolveDirectInputSlotStridesLocked(command, input_slots);
    TrimInputSlotsToElements(input_slots, input_elements);
    required_slots = &input_slots;
  }
  const FableDirectClock::time_point vbind_layout_t1 = FableDirectClock::now();
  g_direct_draw_phases.vbind_layout_ms +=
      FableDirectElapsedMs(vbind_t0, vbind_layout_t1);

  // Per-slot decision carry (2026-08-02 compose peel): the readiness pass
  // below and the view-build pass after it derived the SAME pure-command
  // values (stride resolve — itself run twice more inside the primitive8
  // predicate — minimum view bytes, inline coverage) independently, twice per
  // slot per draw. The readiness pass records them here and the view build
  // reuses them; nothing cached depends on state the passes could change
  // (both run back to back under the renderer mutex with no sync between).
  struct VbindSlotDecision {
    bool valid = false;
    uint32_t stride = 0;
    uint32_t required_view_bytes = 0;
    bool primitive8_expansion = false;
    bool inline_ready = false;
    bool expand_inline_primitive8 = false;
  };
  std::array<VbindSlotDecision, 16> slot_decisions = {};
  bool have_required_slot = false;
  for (const plume::RenderInputSlot& input_slot : *required_slots) {
    if (input_slot.index == kDefaultVertexInputSlot) {
      if (!EnsureDirectDefaultVertexInputBufferLocked(device)) {
        return false;
      }
      have_required_slot = true;
      continue;
    }
    if (input_slot.index >= command.resources.streams.size()) {
      continue;
    }
    const RendererStreamBindingSnapshot& stream =
        command.resources.streams[input_slot.index];
    const uint32_t stride =
        ResolveDirectInputSlotStrideLocked(command, input_slot.index);
    const uint32_t required_view_bytes =
        DirectMinimumVertexBufferViewBytes(command, stride);
    const bool primitive8_expansion =
        ShouldExpandPrimitive8Rectangle(command, input_slot.index);
    const bool inline_ready =
        DirectInlineVertexBufferCoversDraw(
            command, command.inline_vertex_buffers[input_slot.index], stream,
            input_slot.index, required_view_bytes);
    const bool expand_inline_primitive8 =
        ShouldExpandInlinePrimitive8Rectangle(command, input_slot.index);
    const bool inline_primitive8_ready =
        primitive8_expansion && expand_inline_primitive8;
    if (input_slot.index < slot_decisions.size()) {
      VbindSlotDecision& decision = slot_decisions[input_slot.index];
      decision.valid = true;
      decision.stride = stride;
      decision.required_view_bytes = required_view_bytes;
      decision.primitive8_expansion = primitive8_expansion;
      decision.inline_ready = inline_ready;
      decision.expand_inline_primitive8 = expand_inline_primitive8;
    }
    const bool regeneratable_private_rect =
        synthetic_private_rect && input_slot.index == 0u &&
        command.draw.private_texture_corners_valid;
    // GTL-PRIMARY: `stream.buffer` for these draws is a RING CURSOR, not a
    // resource identity — the guest bump-allocates every BeginVertices payload
    // out of one 6 MB ring that wraps and restarts per segment, so the same
    // address recurs constantly and the observer's address-keyed scratch
    // registry (capped, self-overwriting) mostly does not hold it. Measured:
    // ~95% of the remaining full-map scans are MISSES on exactly these lookups.
    //
    // The GTL pin already copied the authoritative bytes into
    // inline_vertex_buffers at the Begin root, so when the inline payload
    // covers the draw the tracked-buffer answer cannot change the outcome:
    // `buffer` is read only by the three readiness predicates below and by the
    // reject log, and the reject branch is unreachable once any *_ready holds.
    // Skipping the lookup in that case is therefore result-identical and
    // removes the dominant scan cost.
    PlumeTrackedResource* buffer = nullptr;
    bool buffer_upload_ready = false;
    bool buffer_ready = false;
    bool tracked_primitive8_ready = false;
    std::array<float, 12> ignored_expanded_vertices = {};
    if (!inline_ready && !inline_primitive8_ready &&
        !regeneratable_private_rect) {
      buffer = FindDirectResourceLocked(stream.buffer);
      const bool exact_scratch_wrapper =
          command.draw.source == RendererDrawEventSource::kScratchWrapper;
      buffer_upload_ready =
          buffer && (exact_scratch_wrapper ||
                     DirectTrackedScratchBufferUploadLooksReady(*buffer));
      buffer_ready =
          !primitive8_expansion && buffer_upload_ready &&
          DirectTrackedVertexBufferCoversDraw(*buffer, stream.offset,
                                              required_view_bytes);
      tracked_primitive8_ready =
          primitive8_expansion && buffer &&
          BuildExpandedTrackedPrimitive8Rectangle(
              *buffer, stream.offset, ignored_expanded_vertices);
    }
    if (!buffer_ready && !tracked_primitive8_ready && !inline_ready &&
        !inline_primitive8_ready && !regeneratable_private_rect) {
      // Forensics for the inline-attached-but-still-failing class: which
      // predicate rejected the slot, and how the inline payload compares to
      // the requirement.
      static uint32_t logged_vertex_bind_rejects = 0;
      if (logged_vertex_bind_rejects < 32u) {
        const FableDirectInlineBuffer& reject_inline =
            command.inline_vertex_buffers[input_slot.index];
        REXLOG_INFO(
            "Refii plume vertex bind reject #{} serial={} slot={} "
            "stream=0x{:08X}/{}+{} tracked={} upload_ready={} covers={} "
            "inline_ready={} inline_addr=0x{:08X} inline_bytes={} "
            "inline_stride={} required_bytes={} stride={} prim={}/{} "
            "gtl={} wrapper_sym=0x{:08X}",
            ++logged_vertex_bind_rejects,
            static_cast<unsigned long long>(command.serial), input_slot.index,
            stream.buffer, stream.stride, stream.offset, buffer ? 1 : 0,
            buffer_upload_ready ? 1 : 0,
            buffer && buffer_upload_ready &&
                    DirectTrackedVertexBufferCoversDraw(*buffer, stream.offset,
                                                        required_view_bytes)
                ? 1
                : 0,
            reject_inline.ready() ? 1 : 0, reject_inline.address,
            reject_inline.byte_size, reject_inline.stride_or_format,
            required_view_bytes, stride, command.draw.draw.primitive_type,
            command.draw.draw.primitive_count, command.gtl_packet_sourced ? 1 : 0,
            command.draw.private_texture_wrapper_symbol_address);
      }
      return false;
    }
    have_required_slot = true;
  }
  if (!have_required_slot) {
    // The last unexplained missing_vertex_input class exits here (boot
    // bursts fire it without hitting the declaration or slot-loop probes):
    // the input layout resolved to no slot that maps onto the command's
    // streams at all.
    static uint32_t logged_no_required_slot_rejects = 0;
    if (logged_no_required_slot_rejects < 32u) {
      REXLOG_INFO(
          "Refii plume vertex bind reject #{} serial={} reason=no_slots "
          "decl=0x{:08X} vs=0x{:08X} slots={} vb0=0x{:08X}/{} prim={}/{}",
          ++logged_no_required_slot_rejects,
          static_cast<unsigned long long>(command.serial),
          command.resources.vertex_declaration,
          command.resources.vertex_shader,
          static_cast<uint32_t>(required_slots->size()),
          command.resources.streams[0].buffer,
          command.resources.streams[0].stride,
          command.draw.draw.primitive_type, command.draw.draw.primitive_count);
    }
    return false;
  }
  const FableDirectClock::time_point vbind_ready_t1 = FableDirectClock::now();
  g_direct_draw_phases.vbind_ready_ms +=
      FableDirectElapsedMs(vbind_layout_t1, vbind_ready_t1);

  std::vector<plume::RenderVertexBufferView> views;
  std::vector<plume::RenderInputSlot> slots;
  std::vector<std::unique_ptr<plume::RenderBuffer>> inline_uploads;
  views.reserve(required_slots->size());
  slots.reserve(required_slots->size());
  std::vector<plume::RenderInputSlot> sorted_required_slots = *required_slots;
  std::sort(sorted_required_slots.begin(), sorted_required_slots.end(),
            [](const plume::RenderInputSlot& lhs,
               const plume::RenderInputSlot& rhs) {
              return lhs.index < rhs.index;
            });
  for (const plume::RenderInputSlot& required_input_slot :
       sorted_required_slots) {
    const uint32_t slot = required_input_slot.index;
    if (slot == kDefaultVertexInputSlot) {
      plume::RenderBuffer* default_buffer =
          EnsureDirectDefaultVertexInputBufferLocked(device);
      if (!default_buffer) {
        return false;
      }
      views.emplace_back(default_buffer->at(0), kDefaultVertexInputStride);
      slots.emplace_back(slot, kDefaultVertexInputStride);
      continue;
    }
    if (slot >= command.resources.streams.size()) {
      continue;
    }
    const RendererStreamBindingSnapshot& stream = command.resources.streams[slot];
    PlumeTrackedResource* buffer = FindDirectResourceLocked(stream.buffer);
    const FableDirectInlineBuffer& inline_buffer =
        command.inline_vertex_buffers[slot];
    // Pure-command values carried from the readiness pass above (see
    // VbindSlotDecision); the fallback recompute covers slots the readiness
    // pass skipped or indexes past the carry array.
    const VbindSlotDecision* decision =
        slot < slot_decisions.size() && slot_decisions[slot].valid
            ? &slot_decisions[slot]
            : nullptr;
    const uint32_t stride =
        decision != nullptr ? decision->stride
                            : ResolveDirectInputSlotStrideLocked(command, slot);
    const uint32_t required_view_bytes =
        decision != nullptr ? decision->required_view_bytes
                            : DirectMinimumVertexBufferViewBytes(command, stride);
    const bool primitive8_expansion =
        decision != nullptr ? decision->primitive8_expansion
                            : ShouldExpandPrimitive8Rectangle(command, slot);
    const uint32_t bind_stride = primitive8_expansion ? 8u : stride;
    const bool exact_scratch_wrapper =
        command.draw.source == RendererDrawEventSource::kScratchWrapper;
    const bool buffer_upload_ready =
        buffer && (exact_scratch_wrapper ||
                   DirectTrackedScratchBufferUploadLooksReady(*buffer));
    const bool buffer_ready =
        !primitive8_expansion && buffer_upload_ready &&
        DirectTrackedVertexBufferCoversDraw(*buffer, stream.offset,
                                            required_view_bytes);
    const bool inline_ready =
        decision != nullptr
            ? decision->inline_ready
            : DirectInlineVertexBufferCoversDraw(command, inline_buffer, stream,
                                                 slot, required_view_bytes);
    const bool prefer_inline =
        inline_ready &&
        DirectShouldPreferInlineScratchVertexBuffer(inline_buffer, stream);
    const bool regeneratable_private_rect =
        synthetic_private_rect && slot == 0u &&
        command.draw.private_texture_corners_valid && !buffer_ready &&
        !inline_ready;
    if (regeneratable_private_rect) {
      // Content-keyed cache first (identical corners quad every re-record);
      // the fresh per-draw upload remains only as the cache-refusal fallback.
      if (plume::RenderBuffer* cached_rect =
              GetOrCreateCachedSyntheticPrivateRectVertexBufferLocked(device,
                                                                     command)) {
        views.emplace_back(cached_rect->at(0), 48u * 6u);
        slots.emplace_back(slot, stride);
        continue;
      }
      auto upload =
          CreateSyntheticPrivateRectVertexUpload(device, command, slot);
      if (!upload) {
        return false;
      }
      views.emplace_back(
          upload->at(0), 48u * 6u);
      slots.emplace_back(slot, stride);
      inline_uploads.emplace_back(std::move(upload));
      continue;
    }
    if (!primitive8_expansion && buffer && PlumeGuestBufferReady(*buffer) &&
        !buffer_ready && !inline_ready &&
        g_direct_renderer.diagnostics.logged_undersized_vertex_buffers < 24u) {
      REXLOG_INFO(
          "Refii plume exact undersized vertex buffer reject #{} "
          "slot={} guest=0x{:08X}+{} bytes={} required={} stride={} "
          "indexed={} prim={} count={} start={} base={} inline_ready={}",
          g_direct_renderer.diagnostics.logged_undersized_vertex_buffers + 1u, slot,
          stream.buffer, stream.offset, PlumeGuestBufferByteSize(*buffer),
          required_view_bytes, stride, command.draw.draw.indexed ? 1 : 0,
          command.draw.draw.primitive_type, command.draw.draw.primitive_count,
          command.draw.draw.start, command.draw.draw.base_vertex,
          DirectInlineVertexBufferCoversDraw(command, inline_buffer, stream,
                                             slot, required_view_bytes)
              ? 1
              : 0);
      ++g_direct_renderer.diagnostics.logged_undersized_vertex_buffers;
    }
    if (primitive8_expansion && buffer) {
      std::array<float, 12> expanded_vertices = {};
      if (BuildExpandedTrackedPrimitive8Rectangle(*buffer, stream.offset,
                                                  expanded_vertices)) {
        if (g_direct_renderer.diagnostics.logged_primitive8_expansions < 48u) {
          REXLOG_INFO(
              "Refii plume direct renderer expanded tracked primitive8 #{} "
              "serial={} src={} vs=0x{:08X} ps=0x{:08X} decl=0x{:08X} "
              "target=0x{:08X} vb=0x{:08X}+{} stride={} in={} bytes={} "
              "out=({:.4f},{:.4f}) ({:.4f},{:.4f}) ({:.4f},{:.4f}) "
              "({:.4f},{:.4f}) ({:.4f},{:.4f}) ({:.4f},{:.4f})",
              g_direct_renderer.diagnostics.logged_primitive8_expansions + 1u,
              static_cast<unsigned long long>(command.serial),
              static_cast<uint32_t>(command.draw.source),
              command.resources.vertex_shader, command.resources.pixel_shader,
              command.resources.vertex_declaration,
              command.resources.render_targets[0], stream.buffer,
              stream.offset, stride, command.draw.draw.primitive_count,
              PlumeGuestBufferByteSize(*buffer), expanded_vertices[0],
              expanded_vertices[1], expanded_vertices[2],
              expanded_vertices[3], expanded_vertices[4],
              expanded_vertices[5], expanded_vertices[6],
              expanded_vertices[7], expanded_vertices[8],
              expanded_vertices[9], expanded_vertices[10],
              expanded_vertices[11]);
          ++g_direct_renderer.diagnostics.logged_primitive8_expansions;
        }
        char name[128] = {};
        std::snprintf(
            name, sizeof(name),
            "Refii Fable expanded tracked primitive8 VB%u guest=0x%08X",
            slot, stream.buffer);
        plume::RenderBuffer* upload = GetOrCreateCachedUploadBufferWithDataLocked(
            device, expanded_vertices.data(),
            static_cast<uint32_t>(sizeof(float) * expanded_vertices.size()),
            name, plume::RenderBufferFlag::VERTEX);
        if (!upload) {
          return false;
        }
        views.emplace_back(upload->at(0),
                           static_cast<uint32_t>(sizeof(float) *
                                                 expanded_vertices.size()));
        slots.emplace_back(slot, bind_stride);
        continue;
      }
    }
    if (buffer_ready && !prefer_inline) {
      const uint32_t size =
          DirectTrackedBufferViewBytes(*buffer, stream.offset);
      views.emplace_back(buffer->guest_buffer.buffer->at(stream.offset), size);
      slots.emplace_back(slot, stride);
      continue;
    }

    const uint32_t inline_base_offset =
        DirectInlineBufferOffsetForCommandAddress(command, inline_buffer,
                                                  stream.buffer);
    if (inline_base_offset != UINT32_MAX) {
      std::unique_ptr<plume::RenderBuffer> upload;
      const bool bind_inline_from_base =
          DirectShouldBindInlineScratchVertexFromBase(command, inline_buffer,
                                                      stream, stride);
      const uint32_t effective_inline_base_offset =
          bind_inline_from_base ? 0u : inline_base_offset;
      if (bind_inline_from_base &&
          g_direct_renderer.diagnostics.logged_undersized_vertex_buffers < 24u) {
        REXLOG_INFO(
            "Refii plume direct renderer rebound inline scratch VB from base "
            "#{} serial={} draw={} source={} vb=0x{:08X} inline=0x{:08X}/{} "
            "offset={} stride={} prim={} count={} ib=0x{:08X}",
            g_direct_renderer.diagnostics.logged_undersized_vertex_buffers + 1u,
            static_cast<unsigned long long>(command.serial),
            static_cast<unsigned long long>(command.draw_serial),
            static_cast<uint32_t>(command.draw.source), stream.buffer,
            inline_buffer.address, inline_buffer.byte_size, inline_base_offset,
            stride, command.draw.draw.primitive_type,
            command.draw.draw.primitive_count, command.resources.index_buffer);
        ++g_direct_renderer.diagnostics.logged_undersized_vertex_buffers;
      }
      const uint64_t combined_offset64 =
          uint64_t(effective_inline_base_offset) + uint64_t(stream.offset);
      const uint32_t offset =
          combined_offset64 < inline_buffer.byte_size
              ? static_cast<uint32_t>(combined_offset64)
              : effective_inline_base_offset;
      uint32_t upload_offset = offset;
      uint32_t upload_size = inline_buffer.byte_size - offset;
      if (decision != nullptr
              ? decision->expand_inline_primitive8
              : ShouldExpandInlinePrimitive8Rectangle(command, slot)) {
        std::array<float, 12> expanded_vertices = {};
        if (!BuildExpandedInlinePrimitive8Rectangle(inline_buffer,
                                                    expanded_vertices)) {
          return false;
        }
        if (g_direct_renderer.diagnostics.logged_primitive8_expansions < 48u) {
          REXLOG_INFO(
              "Refii plume direct renderer expanded primitive8 #{} serial={} "
              "src={} vs=0x{:08X} ps=0x{:08X} decl=0x{:08X} "
              "target=0x{:08X} stride={} in={} bytes={} "
              "out=({:.4f},{:.4f}) ({:.4f},{:.4f}) ({:.4f},{:.4f}) "
              "({:.4f},{:.4f}) ({:.4f},{:.4f}) ({:.4f},{:.4f})",
              g_direct_renderer.diagnostics.logged_primitive8_expansions + 1u,
              static_cast<unsigned long long>(command.serial),
              static_cast<uint32_t>(command.draw.source),
              command.resources.vertex_shader, command.resources.pixel_shader,
              command.resources.vertex_declaration,
              command.resources.render_targets[0], inline_buffer.stride_or_format,
              command.draw.draw.primitive_count, inline_buffer.byte_size,
              expanded_vertices[0], expanded_vertices[1], expanded_vertices[2],
              expanded_vertices[3], expanded_vertices[4], expanded_vertices[5],
              expanded_vertices[6], expanded_vertices[7], expanded_vertices[8],
              expanded_vertices[9], expanded_vertices[10],
              expanded_vertices[11]);
          ++g_direct_renderer.diagnostics.logged_primitive8_expansions;
        }
        char name[128] = {};
        std::snprintf(
            name, sizeof(name),
            "Refii Fable expanded primitive8 inline VB%u guest=0x%08X", slot,
            inline_buffer.address);
        plume::RenderBuffer* cached_upload =
            GetOrCreateCachedUploadBufferWithDataLocked(
            device, expanded_vertices.data(),
            static_cast<uint32_t>(sizeof(float) * expanded_vertices.size()),
            name, plume::RenderBufferFlag::VERTEX);
        if (!cached_upload) {
          return false;
        }
        upload_size = static_cast<uint32_t>(sizeof(float) *
                                            expanded_vertices.size());
        upload_offset = 0u;
        views.emplace_back(cached_upload->at(upload_offset), upload_size);
        slots.emplace_back(slot, bind_stride);
        continue;
      } else {
        plume::RenderBuffer* cached_upload =
            GetOrCreateCachedInlineVertexBufferLocked(device, command,
                                                      inline_buffer, slot);
        if (cached_upload) {
          views.emplace_back(cached_upload->at(upload_offset), upload_size);
          slots.emplace_back(slot, stride);
          continue;
        }
        upload = CreateInlineVertexBufferLocked(device, command, inline_buffer,
                                                slot, upload_offset);
        if (!upload) {
          return false;
        }
        views.emplace_back(upload->at(upload_offset), upload_size);
        slots.emplace_back(slot, stride);
        inline_uploads.emplace_back(std::move(upload));
        continue;
      }
    }
  }
  const FableDirectClock::time_point vbind_upload_t1 = FableDirectClock::now();
  g_direct_draw_phases.vbind_upload_ms +=
      FableDirectElapsedMs(vbind_ready_t1, vbind_upload_t1);
  for (size_t run_start = 0; run_start < views.size();) {
    size_t run_end = run_start + 1u;
    while (run_end < views.size() &&
           slots[run_end].index == slots[run_end - 1u].index + 1u) {
      ++run_end;
    }
    command_list->setVertexBuffers(
        slots[run_start].index, views.data() + run_start,
        static_cast<uint32_t>(run_end - run_start), slots.data() + run_start);
    run_start = run_end;
  }
  RetainFrameUploadBuffersLocked(std::move(inline_uploads));
  g_direct_draw_phases.vbind_setvb_ms +=
      FableDirectElapsedMs(vbind_upload_t1, FableDirectClock::now());
  return true;
}

#endif

}  // namespace refii::renderer
