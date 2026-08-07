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

bool IsFableTitleBillboardAtlasCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot) {
  if (slot >= command.resources.textures.size()) {
    return false;
  }

  const uint32_t texture = command.resources.textures[slot];
  if (!IsFableTitleTextureIndirectionChainAddress(texture)) {
    return false;
  }
  if (IsFablePrivateTextureCopyProxyAddress(texture) ||
      IsFableTitlePrivateTextureProxyAddress(texture)) {
    return false;
  }

  if (slot == 13u &&
      command.draw.source == RendererDrawEventSource::kSceneObject &&
      DirectVertexDeclarationMatchesHashLocked(
          command.resources.vertex_declaration,
          kFableTitleIndexedDeclarationHash) &&
      IsFableTitleSceneVertexShaderCommandLocked(command)) {
    return true;
  }

  return slot == 13u && IsFableFinalTitlePrivateIndirectionCommandLocked(command);
}

bool DirectFableTitleBillboardRegisteredFetchAtlasMatchesCommandLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& candidate) {
  if (!IsFableTitleBillboardAtlasCommandSlotLocked(command, slot) ||
      !DirectTitleSyncFetchTextureMatchesCommandLocked(command, slot,
                                                       candidate)) {
    return false;
  }

  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  if (!command_fetch_info.valid ||
      !IsFableTitleBillboardTileUploadBase(command_fetch_info.base_address,
                                           command_fetch_info.width,
                                           command_fetch_info.height) ||
      candidate.texture_depth_target ||
      DirectCommandBindsResourceAsTargetLocked(command, candidate)) {
    return false;
  }

  const uint32_t candidate_base = PlumeGuestTextureBaseAddress(candidate);
  if (candidate_base != command_fetch_info.base_address ||
      PlumeGuestTextureWidth(candidate) != command_fetch_info.width ||
      PlumeGuestTextureHeight(candidate) != command_fetch_info.height ||
      !DirectTextureFetchDwordsMatch(PlumeGuestTextureFetchDwords(candidate),
                                     command_fetch)) {
    return false;
  }

  const uint32_t guest = command.resources.textures[slot];
  return IsFableExactTitleBillboardTileUploadIdentity(
             guest, command_fetch_info, candidate) &&
         DirectTextureFetchDwordsMatch(PlumeGuestTextureFetchDwords(candidate),
                                       command_fetch);
}

PlumeTrackedResource* FindExactFableTitleBillboardAtlasForCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (slot >= command.resources.textures.size() ||
      !IsFableTitleBillboardAtlasCommandSlotLocked(command, slot)) {
    return nullptr;
  }

  const uint32_t guest = command.resources.textures[slot];
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  const auto valid_exact_action_atlas =
      [&](PlumeTrackedResource* candidate,
          const FableTextureHeaderAction& action) -> PlumeTrackedResource* {
	    if (!candidate || !IsImageResource(candidate->kind) ||
	        !PlumeGuestImageReady(*candidate) ||
	        candidate->texture_depth_target ||
	        DirectCommandBindsResourceAsTargetLocked(command, *candidate) ||
        !command_fetch_info.valid ||
        action.base_address != command_fetch_info.base_address ||
        action.width != command_fetch_info.width ||
        action.height != command_fetch_info.height ||
        action.format != command_fetch_info.format ||
        !DirectTextureFetchDwordsMatch(action.fetch_dwords, command_fetch) ||
        !IsFableTitleUploadedTextureIdentityBase(action.base_address,
                                                action.width,
                                                action.height) ||
        !FableTrackedResourceHasMeaningfulTextureUpload(*candidate) ||
        FableTrackedResourceIsBlankUploadedSampleProxy(*candidate)) {
      return nullptr;
    }
    const uint32_t candidate_base = PlumeGuestTextureBaseAddress(*candidate);
    const uint32_t candidate_width = PlumeGuestTextureWidth(*candidate);
    const uint32_t candidate_height = PlumeGuestTextureHeight(*candidate);
    if (candidate_base != action.base_address ||
        candidate_width != action.width || candidate_height != action.height ||
        !DirectTextureFetchDwordsMatch(PlumeGuestTextureFetchDwords(*candidate),
                                       command_fetch)) {
      return nullptr;
    }

    static uint32_t logged_exact_action_atlas_samples = 0;
    if (logged_exact_action_atlas_samples < 32u) {
      REXLOG_INFO(
          "Refii plume exact title billboard action atlas #{} frame={} "
          "slot={} guest=0x{:08X} sample=0x{:08X} action_object=0x{:08X} "
          "action_header=0x{:08X} action_base=0x{:08X} {}x{} fmt={} "
          "gen={} size={} action_serial={} call={} context=0x{:016X}",
          ++logged_exact_action_atlas_samples,
          g_direct_renderer.current_frame_index, slot, guest,
          candidate->guest_address, action.texture_object,
          action.texture_header, action.base_address, action.width,
          action.height, action.format,
          static_cast<unsigned long long>(candidate->upload_generation),
          static_cast<unsigned long long>(candidate->texture_upload_size),
          static_cast<unsigned long long>(action.serial),
          static_cast<unsigned long long>(action.call_serial),
          static_cast<unsigned long long>(action.context_key));
    }
    return candidate;
  };
  const auto valid_exact_command_atlas =
      [&](PlumeTrackedResource* candidate) -> PlumeTrackedResource* {
    if (!candidate || !IsImageResource(candidate->kind) ||
        !PlumeGuestImageReady(*candidate) || candidate->texture_depth_target ||
        DirectCommandBindsResourceAsTargetLocked(command, *candidate) ||
        !command_fetch_info.valid) {
      return nullptr;
    }
    const uint32_t candidate_base = PlumeGuestTextureBaseAddress(*candidate);
    const uint32_t candidate_width = PlumeGuestTextureWidth(*candidate);
    const uint32_t candidate_height = PlumeGuestTextureHeight(*candidate);
    const uint32_t candidate_guest_format =
        PlumeGuestTextureGuestFormat(*candidate);
    const bool exact_billboard_upload_identity =
        IsFableExactTitleBillboardTileUploadIdentity(
            guest, command_fetch_info, *candidate);
    const bool exact_billboard_tile_fetch =
        command_fetch_info.valid &&
        IsFableTitleBillboardTileUploadBase(command_fetch_info.base_address,
                                           command_fetch_info.width,
                                           command_fetch_info.height);
    const bool authoritative_billboard_tile_upload =
        exact_billboard_tile_fetch && exact_billboard_upload_identity &&
        DirectFableTitleBillboardAtlasHasAuthoritativeUpload(*candidate);
    const bool exact_fetch_match =
        DirectTextureFetchDwordsMatch(PlumeGuestTextureFetchDwords(*candidate),
                                      command_fetch);
    const bool exact_content_ready =
        DirectFableTitleBillboardAtlasHasAuthoritativeUpload(*candidate);
    if ((candidate->texture_render_target && !exact_billboard_upload_identity) ||
        candidate_base != command_fetch_info.base_address ||
        candidate_width != command_fetch_info.width ||
        candidate_height != command_fetch_info.height ||
        (!exact_fetch_match && !exact_billboard_upload_identity) ||
        (!IsFableTitleUploadedTextureIdentityBase(candidate_base,
                                                 candidate_width,
                                                 candidate_height) &&
         !exact_billboard_upload_identity) ||
        !exact_content_ready ||
        (!exact_billboard_upload_identity &&
         FableTrackedResourceIsBlankUploadedSampleProxy(*candidate))) {
      return nullptr;
    }

    static uint32_t logged_exact_command_atlas_samples = 0;
    if (logged_exact_command_atlas_samples < 32u) {
      REXLOG_INFO(
          "Refii plume exact title billboard command atlas #{} frame={} "
          "slot={} guest=0x{:08X} sample=0x{:08X} base=0x{:08X} "
          "{}x{} fmt={} gen={} size={} exact_upload={} fetch_match={} "
          "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
          ++logged_exact_command_atlas_samples,
          g_direct_renderer.current_frame_index, slot, guest,
          candidate->guest_address, candidate_base, candidate_width,
          candidate_height, candidate_guest_format,
          static_cast<unsigned long long>(candidate->upload_generation),
          static_cast<unsigned long long>(candidate->texture_upload_size),
          exact_billboard_upload_identity ? 1 : 0,
          exact_fetch_match ? 1 : 0,
          command_fetch[0], command_fetch[1], command_fetch[2],
          command_fetch[3], command_fetch[4], command_fetch[5]);
    }
    return candidate;
  };
  const auto valid_atlas = [](PlumeTrackedResource* candidate) {
    return candidate && IsFableTitleBillboardAtlasResource(*candidate) &&
                   DirectFableTitleBillboardAtlasHasAuthoritativeUpload(
                       *candidate)
               ? candidate
               : nullptr;
  };
  const auto valid_surface_link_atlas =
      [&](const FableSurfaceTextureLink* link,
          const char* source_name) -> PlumeTrackedResource* {
    if (!link || !link->trusted_destination_edge ||
        link->source_surface == 0 ||
        !DirectPrivateTextureCopyKindUsesColorBacking(link->kind)) {
      return nullptr;
    }
    if (command_fetch_info.valid) {
      if (link->texture_base_address != 0 &&
          link->texture_base_address != command_fetch_info.base_address) {
        return nullptr;
      }
      if (link->texture_width != 0 &&
          link->texture_width != command_fetch_info.width) {
        return nullptr;
      }
      if (link->texture_height != 0 &&
          link->texture_height != command_fetch_info.height) {
        return nullptr;
      }
      if (link->texture_format != 0 &&
          link->texture_format != command_fetch_info.format) {
        return nullptr;
      }
    }
    if (DirectSurfaceTextureLinkIsCurrentTargetSelfEdgeLocked(command, *link) ||
        DirectSurfaceTextureLinkIsCurrentTargetExactEdgeLocked(command,
                                                              *link)) {
      return nullptr;
    }

    PlumeTrackedResource* sample =
        ResolveDirectSurfaceTextureLinkResourceLocked(command, *link);
    if (!sample || !IsImageResource(sample->kind) ||
        !PlumeGuestImageReady(*sample) || sample->texture_depth_target ||
        DirectCommandBindsResourceAsTargetLocked(command, *sample) ||
        (!FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(*sample) &&
         !DirectFableTitleBillboardAtlasHasAuthoritativeUpload(*sample))) {
      return nullptr;
    }

    static uint32_t logged_exact_surface_link_billboard_samples = 0;
    if (logged_exact_surface_link_billboard_samples < 32u) {
      REXLOG_INFO(
          "Refii plume exact title billboard surface link atlas #{} frame={} "
          "source={} slot={} guest=0x{:08X} texture=0x{:08X} "
          "surface=0x{:08X} backing=0x{:08X} sample=0x{:08X} "
          "base=0x{:08X} {}x{} fmt={} link_base=0x{:08X} "
          "link={}x{} link_fmt={} target_serial={} backing_serial={} "
          "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
          ++logged_exact_surface_link_billboard_samples,
          g_direct_renderer.current_frame_index, source_name, slot, guest,
          link->texture, link->source_surface, link->backing_texture,
          sample->guest_address, PlumeGuestTextureBaseAddress(*sample),
          PlumeGuestTextureWidth(*sample), PlumeGuestTextureHeight(*sample),
          static_cast<uint32_t>(PlumeGuestTextureFormat(*sample)),
          link->texture_base_address, link->texture_width,
          link->texture_height, link->texture_format,
          static_cast<unsigned long long>(link->target_content_serial),
          static_cast<unsigned long long>(link->backing_content_serial),
          command_fetch[0], command_fetch[1], command_fetch[2],
          command_fetch[3], command_fetch[4], command_fetch[5]);
    }
    return sample;
  };
  const auto try_address = [&](uint32_t address) -> PlumeTrackedResource* {
    if (address == 0) {
      return nullptr;
    }
    PlumeTrackedResource* candidate = FindDirectResourceLocked(address);
    if (PlumeTrackedResource* atlas = valid_atlas(candidate)) {
      return atlas;
    }
    return valid_exact_command_atlas(candidate);
  };
  const auto try_exact_identity =
      [&](const FableTextureIdentity* identity) -> PlumeTrackedResource* {
    return TryDirectTitleUploadIdentityAddresses(identity, try_address);
  };
  const auto observed_tile_upload_atlas =
      [&](PlumeTrackedResource* candidate) -> PlumeTrackedResource* {
    if (!candidate || !command_fetch_info.valid ||
        !IsFableTitleBillboardTileUploadBase(command_fetch_info.base_address,
                                             command_fetch_info.width,
                                             command_fetch_info.height) ||
        !IsImageResource(candidate->kind) ||
        !PlumeGuestImageReady(*candidate) ||
        candidate->texture_depth_target ||
        DirectCommandBindsResourceAsTargetLocked(command, *candidate) ||
        !DirectFableTitleBillboardAtlasHasAuthoritativeUpload(*candidate)) {
      return nullptr;
    }

    const uint32_t candidate_base = PlumeGuestTextureBaseAddress(*candidate);
    if (candidate_base != command_fetch_info.base_address ||
        PlumeGuestTextureWidth(*candidate) != command_fetch_info.width ||
        PlumeGuestTextureHeight(*candidate) != command_fetch_info.height ||
        !IsFableTitleUploadedTextureIdentityBase(
            candidate_base, PlumeGuestTextureWidth(*candidate),
            PlumeGuestTextureHeight(*candidate))) {
      return nullptr;
    }

    const bool candidate_title_tile_address =
        IsFableTitleTextureIndirectionChainAddress(candidate->guest_address) ||
        IsFableTitleTextureIndirectionChainAddress(candidate->parent_address) ||
        IsFableTitleBillboardTileUploadObjectAddress(candidate->guest_address) ||
        IsFableTitleBillboardTileUploadObjectAddress(candidate->parent_address) ||
        IsFableTitleBillboardTileUploadHeaderAddress(candidate->guest_address) ||
        IsFableTitleBillboardTileUploadHeaderAddress(candidate->parent_address) ||
        IsFableTitleBillboardTileUploadBaseAddress(candidate_base);
    if (!candidate_title_tile_address) {
      return nullptr;
    }

    const FableTextureIdentity* identity =
        FableDirectFindTextureIdentityByObject(candidate->guest_address);
    const bool identity_matches_command_fetch =
        identity && identity->base_address == command_fetch_info.base_address &&
        identity->width == command_fetch_info.width &&
        identity->height == command_fetch_info.height &&
        DirectTextureFetchDwordsMatch(identity->fetch_dwords, command_fetch);
    const bool resource_matches_command_fetch =
        DirectTextureFetchDwordsMatch(PlumeGuestTextureFetchDwords(*candidate),
                                      command_fetch);
    const bool resource_is_command_base_upload =
        candidate->guest_address == command_fetch_info.base_address ||
        candidate->parent_address == command_fetch_info.base_address ||
        candidate_base == command_fetch_info.base_address;
    if (!identity_matches_command_fetch && !resource_matches_command_fetch &&
        !resource_is_command_base_upload) {
      return nullptr;
    }

    return candidate;
  };
  PlumeTrackedResource* observed_tile_upload = nullptr;
  uint64_t observed_tile_upload_serial = 0;
  const auto consider_observed_tile_upload = [&](uint32_t address) {
    PlumeTrackedResource* candidate =
        observed_tile_upload_atlas(FindDirectResourceLocked(address));
    if (!candidate) {
      return;
    }
    const uint64_t serial = FableTrackedResourceVisibleContentSerial(*candidate);
    if (!observed_tile_upload || serial >= observed_tile_upload_serial) {
      observed_tile_upload = candidate;
      observed_tile_upload_serial = serial;
    }
  };
  const auto consider_observed_tile_upload_identity =
      [&](const FableTextureIdentity* identity) {
    if (!identity || !command_fetch_info.valid ||
        identity->base_address != command_fetch_info.base_address ||
        identity->width != command_fetch_info.width ||
        identity->height != command_fetch_info.height ||
        !DirectTextureFetchDwordsMatch(identity->fetch_dwords, command_fetch)) {
      return;
    }
    consider_observed_tile_upload(identity->object_texture);
    consider_observed_tile_upload(identity->parent_texture);
    consider_observed_tile_upload(identity->base_address);
  };
  const auto consider_observed_tile_upload_identity_objects =
      [&](const std::vector<uint32_t>* objects) {
    if (!objects) {
      return;
    }
    for (uint32_t object : *objects) {
      consider_observed_tile_upload_identity(
          FableDirectFindTextureIdentityByObject(object));
    }
  };
  consider_observed_tile_upload(guest);
  consider_observed_tile_upload_identity(
      FableDirectFindTextureIdentityByObject(guest));
  if (command_fetch_info.valid) {
    consider_observed_tile_upload(command_fetch_info.base_address);
    if (IsFableTitleBillboardTileUploadBase(command_fetch_info.base_address,
                                            command_fetch_info.width,
                                            command_fetch_info.height)) {
      const uint32_t canonical_tile_object =
          FableTitleBillboardTileObjectAddressForBase(
              command_fetch_info.base_address);
      const uint32_t canonical_tile_header =
          FableTitleBillboardTileHeaderAddressForObject(canonical_tile_object);
      consider_observed_tile_upload(canonical_tile_object);
      consider_observed_tile_upload(canonical_tile_header);

      const uint32_t observed_tile_object =
          FableObservedTitleBillboardTileObjectAddressForBaseLocked(
              command_fetch_info.base_address, command_fetch_info.width,
              command_fetch_info.height);
      if (observed_tile_object != canonical_tile_object) {
        consider_observed_tile_upload(observed_tile_object);
        consider_observed_tile_upload(
            FableTitleBillboardTileHeaderAddressForObject(observed_tile_object));
      }
    }
    // The singular ByBase lookup that sat here was deleted 2026-07-22: it is
    // redundant BY CONSTRUCTION, not merely unused. SelectUniqueTextureIdentity
    // walks the very list ObjectsByBase returns and yields an identity only
    // when exactly one object carries one — and the _objects helper below then
    // calls consider_observed_tile_upload_identity for EVERY object in that
    // same list. The singular call could therefore only ever repeat a subset
    // of the next line's work.
    // Run 152 agrees from the other direction: c=61 h=61 u=0, every hit
    // ambiguous, so it contributed nothing observable either.
    consider_observed_tile_upload_identity_objects(
        FableDirectFindTextureIdentityObjectsByBase(
            command_fetch_info.base_address));
  }
  if (HasTrackedFetchDwords(command_fetch)) {
    // Same subset argument for the fetch key (run 152: c=61 h=61 u=0).
    consider_observed_tile_upload_identity_objects(
        FableDirectFindTextureIdentityObjectsByFetch(command_fetch));
  }
  if (observed_tile_upload) {
    static uint32_t logged_observed_tile_upload_atlases = 0;
    if (logged_observed_tile_upload_atlases < 32u) {
      REXLOG_INFO(
          "Refii plume observed title billboard tile upload atlas #{} "
          "frame={} slot={} guest=0x{:08X} sample=0x{:08X} "
          "base=0x{:08X} {}x{} fmt={} gen={} size={} serial={} "
          "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
          ++logged_observed_tile_upload_atlases,
          g_direct_renderer.current_frame_index, slot, guest,
          observed_tile_upload->guest_address,
          PlumeGuestTextureBaseAddress(*observed_tile_upload),
          PlumeGuestTextureWidth(*observed_tile_upload),
          PlumeGuestTextureHeight(*observed_tile_upload),
          PlumeGuestTextureGuestFormat(*observed_tile_upload),
          static_cast<unsigned long long>(
              observed_tile_upload->upload_generation),
          static_cast<unsigned long long>(
              observed_tile_upload->texture_upload_size),
          static_cast<unsigned long long>(observed_tile_upload_serial),
          command_fetch[0], command_fetch[1], command_fetch[2],
          command_fetch[3], command_fetch[4], command_fetch[5]);
    }
    return observed_tile_upload;
  }

  if (PlumeTrackedResource* registered_fetch_sample =
          ResolveDirectSampledTextureFromFetchConstantsLocked(command, slot)) {
    if (DirectFableTitleBillboardRegisteredFetchAtlasMatchesCommandLocked(
            command, slot, *registered_fetch_sample)) {
      static uint32_t logged_registered_fetch_billboard_atlases = 0;
      if (logged_registered_fetch_billboard_atlases < 32u) {
        REXLOG_INFO(
            "Refii plume exact title billboard registered fetch atlas #{} "
            "frame={} slot={} guest=0x{:08X} sample=0x{:08X} "
            "base=0x{:08X} {}x{} fmt={} gen={} size={} "
            "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
            ++logged_registered_fetch_billboard_atlases,
            g_direct_renderer.current_frame_index, slot, guest,
            registered_fetch_sample->guest_address,
            PlumeGuestTextureBaseAddress(*registered_fetch_sample),
            PlumeGuestTextureWidth(*registered_fetch_sample),
            PlumeGuestTextureHeight(*registered_fetch_sample),
            PlumeGuestTextureGuestFormat(*registered_fetch_sample),
            static_cast<unsigned long long>(
                registered_fetch_sample->upload_generation),
            static_cast<unsigned long long>(
                registered_fetch_sample->texture_upload_size),
            command_fetch[0], command_fetch[1], command_fetch[2],
            command_fetch[3], command_fetch[4], command_fetch[5]);
      }
      return registered_fetch_sample;
    }
  }

  if (PlumeTrackedResource* candidate = try_address(guest)) {
    return candidate;
  }
  if (PlumeTrackedResource* candidate =
          try_exact_identity(FableDirectFindTextureIdentityByObject(guest))) {
    return candidate;
  }
  if (command_fetch_info.valid &&
      IsFableTitleBillboardTileUploadBase(command_fetch_info.base_address,
                                          command_fetch_info.width,
                                          command_fetch_info.height)) {
    const uint32_t canonical_tile_object =
        FableTitleBillboardTileObjectAddressForBase(
            command_fetch_info.base_address);
    if (PlumeTrackedResource* candidate = try_address(canonical_tile_object)) {
      return candidate;
    }
    if (PlumeTrackedResource* candidate = try_address(
            FableTitleBillboardTileHeaderAddressForObject(
                canonical_tile_object))) {
      return candidate;
    }

    const uint32_t observed_tile_object =
        FableObservedTitleBillboardTileObjectAddressForBaseLocked(
            command_fetch_info.base_address, command_fetch_info.width,
            command_fetch_info.height);
    if (observed_tile_object != canonical_tile_object) {
      if (PlumeTrackedResource* candidate = try_address(observed_tile_object)) {
        return candidate;
      }
      if (PlumeTrackedResource* candidate = try_address(
              FableTitleBillboardTileHeaderAddressForObject(
                  observed_tile_object))) {
        return candidate;
      }
    }
  }
  if (PlumeTrackedResource* candidate = valid_surface_link_atlas(
          FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(guest, slot),
          "destination-slot")) {
    return candidate;
  }

  // Census-dead (runs 538/539): the 0x821B6EE0-symbol binding-action
  // candidate that lived here never hit. Deleted per migration step 3; the
  // chain continues with the header-action and address routes below.

  if (command_fetch_info.valid &&
      IsFableTitleUploadedTextureIdentityBase(command_fetch_info.base_address,
                                             command_fetch_info.width,
                                             command_fetch_info.height)) {
    if (const FableTextureHeaderAction* action =
            FableDirectFindTextureHeaderActionForExactFetchIdentity(
                command_fetch_info.base_address, command_fetch_info.width,
                command_fetch_info.height, command_fetch_info.format,
                command_fetch)) {
      const uint32_t action_addresses[] = {
          action->texture_object, action->parent_texture,
          action->texture_header, action->base_address};
      for (uint32_t action_address : action_addresses) {
        if (PlumeTrackedResource* candidate =
                valid_exact_action_atlas(FindDirectResourceLocked(action_address),
                                         *action)) {
          return candidate;
        }
      }
    }
    if (PlumeTrackedResource* candidate =
            try_address(command_fetch_info.base_address)) {
      return candidate;
    }
    const uint64_t draw_serial =
        command.draw_serial != 0 ? command.draw_serial : command.serial;
    if (PlumeTrackedResource* candidate = valid_surface_link_atlas(
            FableDirectFindVisibleSurfaceTextureLinkForBase(
                command_fetch_info.base_address,
                g_direct_renderer.current_frame_index, draw_serial, slot),
            "visible-base")) {
      return candidate;
    }
    if (PlumeTrackedResource* candidate = valid_surface_link_atlas(
            FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
                command_fetch_info.base_address, slot),
            "base-destination-slot")) {
      return candidate;
    }
    if (PlumeTrackedResource* candidate = try_exact_identity(
            FableDirectFindTextureIdentityByBase(command_fetch_info.base_address))) {
      return candidate;
    }
    if (PlumeTrackedResource* candidate = TryDirectTitleUploadIdentityObjects(
            FableDirectFindTextureIdentityObjectsByBase(
                command_fetch_info.base_address),
            try_exact_identity)) {
      return candidate;
    }
  }
  if (HasTrackedFetchDwords(command_fetch)) {
    const uint64_t draw_serial =
        command.draw_serial != 0 ? command.draw_serial : command.serial;
    if (PlumeTrackedResource* candidate = valid_surface_link_atlas(
            FableDirectFindVisibleSurfaceTextureLinkForFetch(
                command_fetch, g_direct_renderer.current_frame_index,
                draw_serial, slot),
            "visible-fetch")) {
      return candidate;
    }
    if (PlumeTrackedResource* candidate = try_exact_identity(
            FableDirectFindTextureIdentityByFetch(command_fetch))) {
      return candidate;
    }
    if (PlumeTrackedResource* candidate = TryDirectTitleUploadIdentityObjects(
            FableDirectFindTextureIdentityObjectsByFetch(command_fetch),
            try_exact_identity)) {
      return candidate;
    }
  }

  if (!texture) {
    texture = FindDirectResourceLocked(guest);
  }
  if (texture) {
    if (PlumeTrackedResource* candidate = try_address(texture->guest_address)) {
      return candidate;
    }
    if (PlumeTrackedResource* candidate = try_address(texture->parent_address)) {
      return candidate;
    }
    const uint32_t texture_base = PlumeGuestTextureBaseAddress(*texture);
    if (PlumeTrackedResource* candidate = try_address(texture_base)) {
      return candidate;
    }
    if (PlumeTrackedResource* candidate = try_exact_identity(
            FableDirectFindTextureIdentityByObject(texture->guest_address))) {
      return candidate;
    }
    if (PlumeTrackedResource* candidate = try_exact_identity(
            FableDirectFindTextureIdentityByObject(texture->parent_address))) {
      return candidate;
    }
    if (PlumeTrackedResource* candidate = try_exact_identity(
            FableDirectFindTextureIdentityByBase(texture_base))) {
      return candidate;
    }
    if (PlumeTrackedResource* candidate = TryDirectTitleUploadIdentityObjects(
            FableDirectFindTextureIdentityObjectsByBase(texture_base),
            try_exact_identity)) {
      return candidate;
    }
    const std::array<uint32_t, 6> texture_fetch =
        PlumeGuestTextureFetchDwords(*texture);
    if (HasTrackedFetchDwords(texture_fetch)) {
      if (PlumeTrackedResource* candidate = try_exact_identity(
              FableDirectFindTextureIdentityByFetch(texture_fetch))) {
        return candidate;
      }
      if (PlumeTrackedResource* candidate = TryDirectTitleUploadIdentityObjects(
              FableDirectFindTextureIdentityObjectsByFetch(texture_fetch),
              try_exact_identity)) {
        return candidate;
      }
    }
  }

  static uint32_t logged_billboard_atlas_misses = 0;
  if (logged_billboard_atlas_misses < 48u) {
    const PlumeTrackedResource* miss_texture =
        texture ? texture : FindDirectResourceLocked(guest);
    REXLOG_INFO(
        "Refii plume exact title billboard atlas MISS #{} frame={} serial={} "
        "slot={} guest=0x{:08X} tracked={} tracked_guest=0x{:08X} "
        "parent=0x{:08X} tracked_base=0x{:08X} {}x{} gen={} upload={} "
        "blank_proxy={} ready={} cmd_fetch_valid={} cmd_base=0x{:08X} "
        "cmd_dims={}x{} cmd_fmt={} fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/"
        "{:08X}",
        ++logged_billboard_atlas_misses, g_direct_renderer.current_frame_index,
        static_cast<unsigned long long>(command.serial), slot, guest,
        miss_texture ? 1 : 0,
        miss_texture ? miss_texture->guest_address : 0u,
        miss_texture ? miss_texture->parent_address : 0u,
        miss_texture ? PlumeGuestTextureBaseAddress(*miss_texture) : 0u,
        miss_texture ? PlumeGuestTextureWidth(*miss_texture) : 0u,
        miss_texture ? PlumeGuestTextureHeight(*miss_texture) : 0u,
        miss_texture
            ? static_cast<unsigned long long>(miss_texture->upload_generation)
            : 0ull,
        miss_texture
            ? static_cast<unsigned long long>(miss_texture->texture_upload_size)
            : 0ull,
        miss_texture && FableTrackedResourceIsBlankUploadedSampleProxy(
                            *miss_texture)
            ? 1
            : 0,
        miss_texture && PlumeGuestImageReady(*miss_texture) ? 1 : 0,
        command_fetch_info.valid ? 1 : 0, command_fetch_info.base_address,
        command_fetch_info.width, command_fetch_info.height,
        command_fetch_info.format, command_fetch[0], command_fetch[1],
        command_fetch[2], command_fetch[3], command_fetch[4],
        command_fetch[5]);
  }
  return nullptr;
}

// ZERO-CALLER FindExactFableTitleBillboardAtlasForAddressLocked — DELETED
// 2026-07-26. Its ONLY caller was the billboard_atlas_image_ready readiness
// short-circuit removed earlier in this same pass (that gate's literal never
// matched in run 288, so this lookup never ran either). Compiler-verified
// unused afterwards (-Wunused-function) plus grep-verified: forward
// declaration and definition only, both in this file, both removed.
bool DirectCommandSlotUsesExactTitleUploadSourceLocked(
    const FableDirectRenderCommand& command, uint32_t slot) {
  if (slot >= command.resources.textures.size()) {
    return false;
  }
  if (DirectCommandIsExactBinkIndexedWrapperDrawLocked(command)) {
    return false;
  }

  const uint32_t texture = command.resources.textures[slot];
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(
          DirectCommandTextureFetchDwords(command, slot));
  const bool command_exact_title_upload_identity =
      command_fetch_info.valid &&
      IsFableTitleUploadedTextureIdentityBase(command_fetch_info.base_address,
                                             command_fetch_info.width,
                                             command_fetch_info.height);
  const FableTextureIdentity* title_identity =
      FableDirectFindTextureIdentityByObject(texture);
  const bool object_exact_title_upload_identity =
      IsFableTitleUploadedTextureIdentity(title_identity);
  const bool fetch_backed_title_gui =
      FableTitleGuiFetchBackedTextureSlotLocked(command) == slot &&
      command_exact_title_upload_identity;
  const bool private_rect_setup_exact_title_upload =
      command.draw.private_texture_rectangle &&
      command.draw.private_texture_wrapper_symbol_address ==
          kFablePrivateRectSetupWrapperSymbol &&
      command.draw.private_texture_wrapper_call_serial != 0 &&
      command.draw.private_texture_wrapper_context_key != 0 &&
      command_exact_title_upload_identity;
  if (texture == 0u) {
    return fetch_backed_title_gui || private_rect_setup_exact_title_upload;
  }
  if (IsFablePrivateTextureCopyProxyAddress(texture)) {
    return false;
  }

  const bool title_sync_command =
      IsFableTitleSceneTextureSyncCommandLocked(command) ||
      IsFableFinalTitlePrivateIndirectionCommandLocked(command) ||
      fetch_backed_title_gui || private_rect_setup_exact_title_upload;
  if (!title_sync_command) {
    return false;
  }

  if (IsFableTitlePrivateTextureProxyAddress(texture) &&
      !command_exact_title_upload_identity) {
    return false;
  }

  if (command_exact_title_upload_identity ||
      object_exact_title_upload_identity) {
    return true;
  }

  if (IsFableTitleGuiTextAtlasObjectAddress(texture) ||
      IsFableTitleGuiTextAtlasCommandLocked(command, slot) ||
      IsFableTitleBillboardAtlasCommandSlotLocked(command, slot)) {
    return true;
  }

  if (slot == kFablePrivateRectCopyTextureSlot &&
      IsFableTitleTextureIndirectionChainAddress(texture)) {
    return true;
  }

  return IsFableFinalTitlePrivateIndirectionCommandLocked(command) &&
         (slot == 0u || slot == 1u || slot == 2u) &&
         IsFableTitleTextureIndirectionChainAddress(texture);
}

PlumeTrackedResource* ResolveDirectExactTitleUploadSourceForCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (!DirectCommandSlotUsesExactTitleUploadSourceLocked(command, slot)) {
    return nullptr;
  }

  const uint32_t guest = command.resources.textures[slot];
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  auto exact_upload_sample = [&](PlumeTrackedResource* candidate)
      -> PlumeTrackedResource* {
    if (!candidate || !IsImageResource(candidate->kind) ||
        !PlumeGuestImageReady(*candidate) || candidate->texture_depth_target ||
        DirectCommandBindsExactResourceAsTargetLocked(command, *candidate)) {
      return nullptr;
    }
    if (DirectExactTitleUploadedResourceHasAuthoredPayload(*candidate)) {
      return candidate;
    }
    if (DirectFableTitleIndirectionHasAuthoritativeUpload(*candidate)) {
      return candidate;
    }
    if (candidate->texture_render_target) {
      return nullptr;
    }
    if (DirectTinyTitleCachedTextureCanBindLocked(command, guest, candidate) ||
        FableTitleGuiAtlasResourceHasUpload(
            DirectTitleResourceFactsForResource(*candidate))) {
      return candidate;
    }
    return nullptr;
  };
  auto exact_header_action_upload_sample =
      [&](const FableTextureHeaderAction* action) -> PlumeTrackedResource* {
    if (!action) {
      return nullptr;
    }
    const uint32_t action_addresses[] = {
        action->texture_object, action->parent_texture, action->texture_header,
        action->base_address};
    for (uint32_t action_address : action_addresses) {
      PlumeTrackedResource* sample =
          exact_upload_sample(FindDirectResourceLocked(action_address));
      if (!sample) {
        continue;
      }
      static uint32_t logged_exact_header_action_samples = 0;
      if (logged_exact_header_action_samples < 32u) {
        REXLOG_INFO(
            "Refii plume exact title upload header texture identity #{} "
            "frame={} slot={} guest=0x{:08X} sample=0x{:08X}/{}x{} "
            "sample_fmt={} sample_gen={} sample_content={} "
            "header=0x{:08X} object=0x{:08X} parent=0x{:08X} "
            "base=0x{:08X} size={}x{} bytes={} fmt={} "
            "action_serial={} call={} context=0x{:016X} symbol=0x{:08X} "
            "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
            ++logged_exact_header_action_samples,
            g_direct_renderer.current_frame_index, slot, guest,
            sample->guest_address, PlumeGuestTextureWidth(*sample),
            PlumeGuestTextureHeight(*sample),
            static_cast<uint32_t>(PlumeGuestTextureFormat(*sample)),
            static_cast<unsigned long long>(sample->upload_generation),
            static_cast<unsigned long long>(
                FableTrackedResourceVisibleContentSerial(*sample)),
            action->texture_header,
            action->texture_object, action->parent_texture,
            action->base_address, action->width, action->height,
            action->byte_size, action->format,
            static_cast<unsigned long long>(action->serial),
            static_cast<unsigned long long>(action->call_serial),
            static_cast<unsigned long long>(action->context_key),
            action->symbol_address, action->fetch_dwords[0],
            action->fetch_dwords[1], action->fetch_dwords[2],
            action->fetch_dwords[3], action->fetch_dwords[4],
            action->fetch_dwords[5]);
      }
      return sample;
    }
    return nullptr;
  };

  if (PlumeTrackedResource* sample =
          exact_upload_sample(FindDirectResourceLocked(guest))) {
    return sample;
  }
  if (texture) {
    if (PlumeTrackedResource* sample =
            exact_upload_sample(FindDirectResourceLocked(texture->guest_address))) {
      return sample;
    }
    if (PlumeTrackedResource* sample =
            exact_upload_sample(FindDirectResourceLocked(texture->parent_address))) {
      return sample;
    }
  }
  // Migration step 3: the command's own captured fetch constant names the
  // console memory; every header object registered for that base is a
  // candidate. This replaced the fetch-binding-action registry read, which was
  // deleted outright on 2026-07-18 (see the note below) — so there is no
  // "verification fallback" left to look for; this route stands alone.
  //
  // This is a LOAD-BEARING consumer, and it is the worked example of why the
  // identity census's `useful` column must not be read for the Objects*
  // variants: run 157 measured this lookup at c=2021 h=2021 u=0 — never once a
  // singleton — while its own consumer-side counter
  // (`fetch-binding-action site exact_upload_base_objects`) reports 1792 real
  // sample acceptances in the same run. `u=0` here means "the caller iterates",
  // not "the caller gets nothing".
  if (command_fetch_info.valid) {
    if (const std::vector<uint32_t>* base_objects =
            FableDirectFindTextureIdentityObjectsByBase(
                command_fetch_info.base_address)) {
      for (const uint32_t object : *base_objects) {
        if (PlumeTrackedResource* sample =
                exact_upload_sample(FindDirectResourceLocked(object))) {
          CountFetchBindingActionSiteHit("exact_upload_base_objects", true);
          static uint32_t logged_exact_base_object_samples = 0;
          if (logged_exact_base_object_samples < 32u) {
            REXLOG_INFO(
                "Refii plume exact title upload base-object identity #{} "
                "frame={} serial={} slot={} guest=0x{:08X} base=0x{:08X} "
                "object=0x{:08X} sample=0x{:08X}/{}x{} fmt={} gen={} "
                "content={}",
                ++logged_exact_base_object_samples,
                g_direct_renderer.current_frame_index,
                static_cast<unsigned long long>(command.serial), slot, guest,
                command_fetch_info.base_address, object, sample->guest_address,
                PlumeGuestTextureWidth(*sample),
                PlumeGuestTextureHeight(*sample),
                static_cast<uint32_t>(PlumeGuestTextureFormat(*sample)),
                static_cast<unsigned long long>(sample->upload_generation),
                static_cast<unsigned long long>(
                    FableTrackedResourceVisibleContentSerial(*sample)));
          }
          return sample;
        }
      }
    }
  }
  // The fetch-binding-action registry read that lived here was deleted
  // 2026-07-18 after run 542: 16/16 lookups found a binding but produced
  // ZERO samples (every binding object failed the sample gate;
  // REGISTRY_SAVE=0) - the routes above and below carry the title path.

  if (command_fetch_info.valid) {
    if (PlumeTrackedResource* sample =
            ResolveFableTitleBillboardAtlasUploadSourceForSlotLocked(
                command, slot, texture)) {
      return sample;
    }
    if (PlumeTrackedResource* sample = exact_header_action_upload_sample(
            FableDirectFindTextureHeaderActionForExactFetchIdentity(
                command_fetch_info.base_address, command_fetch_info.width,
                command_fetch_info.height, command_fetch_info.format,
                command_fetch))) {
      return sample;
    }
  }
  if (PlumeTrackedResource* sample =
          exact_upload_sample(ResolveFableTitleGuiTextAtlasTextureLocked(
              command, slot))) {
    return sample;
  }
  if (PlumeTrackedResource* sample =
          exact_upload_sample(ResolveFableTitleUploadedIdentityTextureLocked(
              command, slot, texture))) {
    return sample;
  }
  return nullptr;
}

PlumeTrackedResource* ResolveDirectAuthoritativeTitleSyncSampleForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (slot >= command.resources.textures.size() ||
      command.draw.source == RendererDrawEventSource::kPixVideo) {
    return nullptr;
  }

  const bool title_sync_command =
      IsFableTitleSceneTextureSyncCommandLocked(command) ||
      IsFableFinalTitlePrivateIndirectionCommandLocked(command) ||
      IsFableTitleBillboardAtlasCommandSlotLocked(command, slot) ||
      DirectCommandSlotUsesExactTitleUploadSourceLocked(command, slot);
  if (!title_sync_command) {
    return nullptr;
  }

  if (PlumeTrackedResource* title_billboard_sample =
          RegisterFableTitleBillboardBackingLinkLocked(
              command, slot, texture, "authoritative-title-sync")) {
    return title_billboard_sample;
  }

  return ResolveDirectExactTitleUploadSourceForCommandSlotLocked(command, slot,
                                                                 texture);
}

PlumeTrackedResource* RegisterFableTitleBillboardBackingLinkLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture, const char* source_name) {
  if (!IsFableTitleBillboardAtlasCommandSlotLocked(command, slot)) {
    return nullptr;
  }

  const uint32_t guest = command.resources.textures[slot];
  PlumeTrackedResource* backing =
      FindExactFableTitleBillboardAtlasForCommandSlotLocked(command, slot,
                                                            texture);
  if (!backing ||
      DirectCommandBindsResourceAsTargetLocked(command, *backing) ||
      (!FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(*backing) &&
       !DirectFableTitleBillboardAtlasHasAuthoritativeUpload(*backing))) {
    return nullptr;
  }

  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  const uint64_t command_fetch_key = FableDirectTextureFetchKey(command_fetch);
  if (const FableSurfaceTextureLink* existing =
          FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(guest, slot)) {
    const uint64_t existing_fetch_key =
        FableDirectTextureFetchKey(existing->texture_fetch_dwords);
    const bool fetch_matches =
        command_fetch_key == 0 || existing_fetch_key == command_fetch_key;
    const bool base_matches =
        !command_fetch_info.valid || existing->texture_base_address == 0 ||
        existing->texture_base_address == command_fetch_info.base_address;
    if (existing->trusted_destination_edge &&
        existing->backing_texture == backing->guest_address && fetch_matches &&
        base_matches) {
      return backing;
    }
  }

  const FableTextureIdentity* identity =
      FableDirectFindTextureIdentityByObject(guest);

  FableSurfaceTextureLink link;
  link.source_slot = slot;
  link.trusted_destination_edge = true;
  link.texture = guest;
  link.source_surface = backing->guest_address;
  link.kind = FablePrivateTextureCopyKind::kColor;
  if (command_fetch_info.valid) {
    link.texture_base_address = command_fetch_info.base_address;
    link.texture_width = command_fetch_info.width;
    link.texture_height = command_fetch_info.height;
    link.texture_format = command_fetch_info.format;
    link.texture_fetch_dwords = command_fetch;
  } else if (texture && PlumeGuestImageReady(*texture)) {
    link.texture_base_address = PlumeGuestTextureBaseAddress(*texture);
    link.texture_width = PlumeGuestTextureWidth(*texture);
    link.texture_height = PlumeGuestTextureHeight(*texture);
    link.texture_format = PlumeGuestTextureGuestFormat(*texture);
    link.texture_fetch_dwords = PlumeGuestTextureFetchDwords(*texture);
  } else if (identity) {
    link.texture_base_address = identity->base_address;
    link.texture_width = identity->width;
    link.texture_height = identity->height;
    link.texture_format = identity->format;
    link.texture_fetch_dwords = identity->fetch_dwords;
  }

  if (!HasTrackedFetchDwords(link.texture_fetch_dwords) && texture &&
      PlumeGuestImageReady(*texture)) {
    link.texture_fetch_dwords = PlumeGuestTextureFetchDwords(*texture);
  }
  if (link.texture_base_address == 0 && texture && PlumeGuestImageReady(*texture)) {
    link.texture_base_address = PlumeGuestTextureBaseAddress(*texture);
  }

  PopulateDirectSurfaceTextureLinkBackingLocked(link, *backing);
  RegisterDirectSurfaceTextureLinkPreparedLocked(link, "private-current");

  RegisterDirectTextureIdentityFromResourceLocked(
      *backing, PlumeGuestTextureFetchDwords(*backing));

  static uint32_t logged_title_billboard_backing_links = 0;
  if (logged_title_billboard_backing_links < 32u) {
    REXLOG_INFO(
        "Refii plume title billboard backing link #{} frame={} source={} "
        "slot={} guest=0x{:08X} base=0x{:08X} {}x{} fmt={} "
        "backing=0x{:08X} backing_base=0x{:08X} backing_serial={} "
        "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
        ++logged_title_billboard_backing_links,
        g_direct_renderer.current_frame_index,
        source_name ? source_name : "unknown", slot, guest,
        link.texture_base_address, link.texture_width, link.texture_height,
        link.texture_format, backing->guest_address,
        PlumeGuestTextureBaseAddress(*backing),
        static_cast<unsigned long long>(link.backing_content_serial),
        command.resources.render_targets[0], command.resources.depth_stencil,
        command.resources.vertex_shader, command.resources.pixel_shader);
  }
  return backing;
}

bool IsFableTitleBillboardCommandHeaderAddress(uint32_t address) {
  return address == kFableTitleBillboardCommandTexture ||
         address == kFableTitleBillboardCommandTextureAlias ||
         address == kFableTitleBillboardCommandSource ||
         address == kFableTitleBillboardCommandSourceAlias ||
         address == kFableTitleBillboardCommandSourceBase ||
         IsFableTitleBillboardTileUploadObjectAddress(address) ||
         IsFableTitleBillboardTileUploadHeaderAddress(address) ||
         IsFableTitleBillboardTileUploadBaseAddress(address) ||
         address == kFableCanonicalTitleBillboardBase ||
         // ADDRESS GATE (census: billboard_command_header_address).
         // PHASE 2 (2026-07-26): 0x4C132C70/0x4C132CA4/0x4C132CD8 DELETED as
         // provably redundant — the object/header strided-range predicates two
         // lines above already answer yes for all three (see the arithmetic
         // proof on IsFableDirectTitleBillboardAtlasSnapshot's
         // is_known_object). Pure boolean simplification, identical result for
         // every input. 0x4C133150/0x4C133184 lie past the range end and have
         // no structural cover, so they stay with the census attached.
         DirectAddressGateTest(
             DirectAddressGate::kBillboardCommandHeaderAddress,
             address == 0x4C133150u || address == 0x4C133184u, address, 0ull,
             IsFableTitleBillboardTileUploadObjectAddress(address) ||
                 IsFableTitleBillboardTileUploadHeaderAddress(address));
}

PlumeTrackedResource* ResolveFableTitleBillboardAtlasUploadSourceForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (slot >= command.resources.textures.size() ||
      command.draw.source == RendererDrawEventSource::kPixVideo) {
    return nullptr;
  }

  const uint32_t guest = command.resources.textures[slot];
  const bool billboard_atlas_slot =
      IsFableTitleBillboardAtlasCommandSlotLocked(command, slot);
  const bool final_title_header_slot =
      IsFableFinalTitlePrivateIndirectionCommandLocked(command) &&
      (slot == 0u || slot == 1u || slot == 2u) &&
      IsFableTitleTextureIndirectionChainAddress(guest);
  const bool scene_title_billboard_upload_slot =
      slot == 13u && command.draw.source == RendererDrawEventSource::kSceneObject &&
      IsFableTitleSceneTextureSyncCommandLocked(command) &&
      IsFableTitleBillboardCommandHeaderAddress(guest);
  if (!billboard_atlas_slot && !final_title_header_slot &&
      !scene_title_billboard_upload_slot) {
    return nullptr;
  }

  if (billboard_atlas_slot) {
    if (PlumeTrackedResource* linked =
            RegisterFableTitleBillboardBackingLinkLocked(
                command, slot, texture, "title-billboard-upload")) {
      return linked;
    }
  }

  PlumeTrackedResource* backing =
      FindExactFableTitleBillboardAtlasForCommandSlotLocked(command, slot,
                                                            texture);
  if (!backing ||
      DirectCommandBindsResourceAsTargetLocked(command, *backing) ||
      (!FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(*backing) &&
       !DirectFableTitleBillboardAtlasHasAuthoritativeUpload(*backing))) {
    return nullptr;
  }

  static uint32_t logged_title_billboard_upload_sources = 0;
  if (logged_title_billboard_upload_sources < 32u) {
    REXLOG_INFO(
        "Refii plume exact title billboard upload source #{} frame={} "
        "slot={} guest=0x{:08X} backing=0x{:08X} base=0x{:08X} "
        "{}x{} fmt={} gen={} hash=0x{:016X} rt=0x{:08X} "
        "vs=0x{:08X} ps=0x{:08X}",
        ++logged_title_billboard_upload_sources,
        g_direct_renderer.current_frame_index, slot, guest,
        backing->guest_address, PlumeGuestTextureBaseAddress(*backing),
        PlumeGuestTextureWidth(*backing), PlumeGuestTextureHeight(*backing),
        static_cast<uint32_t>(PlumeGuestTextureFormat(*backing)),
        static_cast<unsigned long long>(backing->upload_generation),
        static_cast<unsigned long long>(backing->texture_upload_hash),
        command.resources.render_targets[0], command.resources.vertex_shader,
        command.resources.pixel_shader);
  }
  return backing;
}

PlumeTrackedResource*
ResolveDirectDestinationEdgeSampleForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (slot >= command.resources.textures.size() ||
      command.draw.source == RendererDrawEventSource::kPixVideo) {
    return nullptr;
  }

  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  const uint64_t draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  auto link_slot_matches = [&](const FableSurfaceTextureLink* link) {
    return link && (link->source_slot == kUnknownSlot ||
                    link->source_slot == slot);
  };
  auto resolve_link_sample =
      [&](const FableSurfaceTextureLink* link) -> PlumeTrackedResource* {
    if (!link_slot_matches(link)) {
      return nullptr;
    }
    PlumeTrackedResource* sample =
        ResolveDirectSurfaceTextureLinkResourceLocked(command, *link);
    if (!sample || !IsImageResource(sample->kind) ||
        !PlumeGuestImageReady(*sample) ||
        FableTrackedResourceIsBlankUploadedSampleProxy(*sample) ||
        (!FableTrackedResourceHasSampleableContent(*sample) &&
         !FableTrackedResourceHasGpuProducedContent(*sample))) {
      return nullptr;
    }
    static uint32_t logged_exact_destination_edge_samples = 0;
    if (RendererVerboseTracingEnabled() &&
        logged_exact_destination_edge_samples < 32u) {
      REXLOG_INFO(
          "Refii plume exact destination edge sample #{} frame={} draw={} "
          "slot={} texture=0x{:08X} source_surface=0x{:08X} "
          "backing=0x{:08X} sample=0x{:08X}/{}x{} rt=0x{:08X} "
          "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_destination_edge_samples,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.draw_serial), slot,
          link->texture, link->source_surface, link->backing_texture,
          sample->guest_address, PlumeGuestTextureWidth(*sample),
          PlumeGuestTextureHeight(*sample), command.resources.render_targets[0],
          command.resources.depth_stencil, command.resources.vertex_shader,
          command.resources.pixel_shader);
    }
    return sample;
  };
  auto resolve_destination = [&](uint32_t destination)
      -> PlumeTrackedResource* {
    if (destination == 0) {
      return nullptr;
    }
    if (PlumeTrackedResource* sample = resolve_link_sample(
            FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
                destination, g_direct_renderer.current_frame_index, draw_serial,
                slot))) {
      return sample;
    }
    if (PlumeTrackedResource* sample = resolve_link_sample(
            FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
                destination, slot))) {
      return sample;
    }
    return resolve_link_sample(
        FableDirectFindSurfaceTextureLinkForDestinationTexture(destination));
  };
  auto resolve_source_surface = [&](uint32_t source_surface)
      -> PlumeTrackedResource* {
    if (source_surface == 0) {
      return nullptr;
    }
    if (PlumeTrackedResource* sample = resolve_link_sample(
            FableDirectFindVisibleSurfaceTextureLinkForSourceSurface(
                source_surface, g_direct_renderer.current_frame_index,
                draw_serial, slot))) {
      return sample;
    }
    if (PlumeTrackedResource* sample = resolve_link_sample(
            FableDirectFindSurfaceTextureLinkForSourceSurfaceSlot(
                source_surface, slot))) {
      return sample;
    }
	    if (PlumeTrackedResource* sample = resolve_link_sample(
	            FableDirectFindSurfaceTextureLinkForSourceSurface(
	                source_surface))) {
	      return sample;
	    }
	    const std::vector<FableSurfaceDestinationTextureRelation>* relations =
	        FableDirectFindSurfaceDestinationRelationsBySourceSurface(
	            source_surface);
	    if (!relations) {
	      return nullptr;
	    }
	    for (const FableSurfaceDestinationTextureRelation& relation :
	         *relations) {
	      if (PlumeTrackedResource* sample =
	              resolve_link_sample(&relation.link)) {
	        return sample;
	      }
	      if (PlumeTrackedResource* sample =
	              resolve_destination(relation.destination_texture)) {
	        return sample;
	      }
	    }
	    return nullptr;
	  };

  std::array<uint32_t, 16> candidates = {};
  size_t candidate_count = 0;
  auto add_candidate = [&](uint32_t candidate) {
    if (candidate == 0) {
      return;
    }
    for (size_t i = 0; i < candidate_count; ++i) {
      if (candidates[i] == candidate) {
        return;
      }
    }
    if (candidate_count < candidates.size()) {
      candidates[candidate_count++] = candidate;
    }
  };

  const uint32_t guest = command.resources.textures[slot];
  add_candidate(guest);
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  if (command_fetch_info.valid) {
    add_candidate(command_fetch_info.base_address);
  }
  if (texture) {
    add_candidate(texture->guest_address);
    add_candidate(texture->parent_address);
    add_candidate(PlumeGuestTextureBaseAddress(*texture));
  }
  if (const FableTextureIdentity* identity =
          FableDirectFindTextureIdentityByObject(guest)) {
    add_candidate(identity->object_texture);
    add_candidate(identity->parent_texture);
    add_candidate(identity->base_address);
  }
  if (const FableTextureIdentity* identity =
          FableDirectFindTextureIdentityByFetch(command_fetch)) {
    add_candidate(identity->object_texture);
    add_candidate(identity->parent_texture);
    add_candidate(identity->base_address);
  }
  if (texture) {
    if (const FableTextureIdentity* identity =
            FableDirectFindTextureIdentityByObject(texture->guest_address)) {
      add_candidate(identity->object_texture);
      add_candidate(identity->parent_texture);
      add_candidate(identity->base_address);
    }
    if (texture->parent_address != 0) {
      if (const FableTextureIdentity* identity =
              FableDirectFindTextureIdentityByObject(texture->parent_address)) {
        add_candidate(identity->object_texture);
        add_candidate(identity->parent_texture);
        add_candidate(identity->base_address);
      }
    }
    if (const FableTextureIdentity* identity =
            FableDirectFindTextureIdentityByFetch(
                PlumeGuestTextureFetchDwords(*texture))) {
      add_candidate(identity->object_texture);
      add_candidate(identity->parent_texture);
      add_candidate(identity->base_address);
    }
  }

  for (size_t i = 0; i < candidate_count; ++i) {
    if (PlumeTrackedResource* sample = resolve_destination(candidates[i])) {
      return sample;
    }
    if (PlumeTrackedResource* sample =
            resolve_source_surface(candidates[i])) {
      return sample;
    }
  }
  return nullptr;
}

PlumeTrackedResource*
ResolveDirectPrivateCopyOutputSampleForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (slot >= command.resources.textures.size() ||
      command.draw.source == RendererDrawEventSource::kPixVideo) {
    return nullptr;
  }

  const uint64_t draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  auto valid_output_sample =
      [&](PlumeTrackedResource* sample,
          const FablePrivateTextureCopyAction& action) -> PlumeTrackedResource* {
    if (!sample || !IsImageResource(sample->kind) ||
        !PlumeGuestImageReady(*sample) ||
        FableTrackedResourceIsBlankUploadedSampleProxy(*sample) ||
        DirectCommandBindsResourceAsTargetLocked(command, *sample) ||
        (!FableTrackedResourceHasGpuProducedContent(*sample) &&
         !FableTrackedResourceHasSampleableContent(*sample))) {
      return nullptr;
    }
    if (action.target_content_serial != 0 &&
        FableTrackedResourceVisibleContentSerial(*sample) <
            action.target_content_serial) {
      return nullptr;
    }
    return sample;
  };
  auto resolve_action_sample =
      [&](const FablePrivateTextureCopyAction* action) -> PlumeTrackedResource* {
    if (!action) {
      return nullptr;
    }
    if (action->kind != FablePrivateTextureCopyKind::kDepth) {
      if (PlumeTrackedResource* source_sample =
              ResolveDirectRegisteredSampleSourceLocked(command,
                                                        action->sampled_source)) {
        static uint32_t logged_exact_private_copy_source_samples = 0;
        if (RendererVerboseTracingEnabled() &&
            logged_exact_private_copy_source_samples < 32u) {
          REXLOG_INFO(
              "Refii plume exact private copy source sample #{} frame={} "
              "draw={} slot={} dest=0x{:08X} source=0x{:08X} "
              "source_base=0x{:08X} target=0x{:08X} backing=0x{:08X} "
              "sample=0x{:08X}/{}x{} kind={} serial={} sample_kind={} "
              "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
              ++logged_exact_private_copy_source_samples,
              g_direct_renderer.current_frame_index,
              static_cast<unsigned long long>(command.draw_serial), slot,
              command.resources.textures[slot], action->source_texture,
              action->source_base_address, action->target_surface,
              action->backing_texture, source_sample->guest_address,
              PlumeGuestTextureWidth(*source_sample),
              PlumeGuestTextureHeight(*source_sample),
              FablePrivateTextureCopyKindName(action->kind),
              static_cast<unsigned long long>(action->serial),
              FableRegisteredSampleSourceKindName(action->sampled_source.kind),
              command.resources.render_targets[0],
              command.resources.depth_stencil,
              command.resources.vertex_shader, command.resources.pixel_shader);
        }
        return source_sample;
      }
    }
    PlumeTrackedResource* target = FindDirectResourceLocked(action->target_surface);
    if (!target && action->kind == FablePrivateTextureCopyKind::kColor) {
      target = FindDirectResourceLocked(action->render_target);
    }
    if (!target && action->kind == FablePrivateTextureCopyKind::kDepth) {
      target = FindDirectResourceLocked(action->depth_stencil);
    }

    PlumeTrackedResource* sample = valid_output_sample(target, *action);
    if (!sample && action->backing_texture != 0) {
      PlumeTrackedResource* backing =
          FindDirectResourceLocked(action->backing_texture);
      if (backing && target &&
          (DirectPrivateTextureCopyActionBackingContainsTargetContentLocked(
               *action, *backing, *target) ||
           DirectPrivateFeedbackAliasContainsTargetContentLocked(*backing,
                                                                 *target))) {
        sample = valid_output_sample(backing, *action);
      }
    }
    if (!sample) {
      return nullptr;
    }

    static uint32_t logged_exact_private_copy_output_samples = 0;
    if (RendererVerboseTracingEnabled() &&
        logged_exact_private_copy_output_samples < 32u) {
      REXLOG_INFO(
          "Refii plume exact private copy output sample #{} frame={} draw={} "
          "slot={} dest=0x{:08X} target=0x{:08X} backing=0x{:08X} "
          "sample=0x{:08X}/{}x{} kind={} serial={} rt=0x{:08X} "
          "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_private_copy_output_samples,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.draw_serial), slot,
          command.resources.textures[slot], action->target_surface,
          action->backing_texture, sample->guest_address,
          PlumeGuestTextureWidth(*sample), PlumeGuestTextureHeight(*sample),
          FablePrivateTextureCopyKindName(action->kind),
          static_cast<unsigned long long>(action->serial),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return sample;
  };
  auto resolve_destination = [&](uint32_t destination)
      -> PlumeTrackedResource* {
    if (destination == 0) {
      return nullptr;
    }
    if (PlumeTrackedResource* sample = resolve_action_sample(
            FableDirectFindVisiblePrivateTextureCopyOutputActionForDestinationTextureSlot(
                destination, slot, g_direct_renderer.current_frame_index,
                draw_serial))) {
      return sample;
    }
    return resolve_action_sample(
        FableDirectFindVisiblePrivateTextureCopyOutputActionForDestinationTexture(
            destination, g_direct_renderer.current_frame_index, draw_serial,
            slot));
  };

  std::array<uint32_t, 8> candidates = {};
  size_t candidate_count = 0;
  auto add_candidate = [&](uint32_t candidate) {
    if (candidate == 0) {
      return;
    }
    for (size_t i = 0; i < candidate_count; ++i) {
      if (candidates[i] == candidate) {
        return;
      }
    }
    if (candidate_count < candidates.size()) {
      candidates[candidate_count++] = candidate;
    }
  };

  const uint32_t guest = command.resources.textures[slot];
  add_candidate(guest);
  if (texture) {
    add_candidate(texture->guest_address);
    add_candidate(texture->parent_address);
  }
  if (const FableTextureIdentity* identity =
          FableDirectFindTextureIdentityByObject(guest)) {
    add_candidate(identity->object_texture);
    add_candidate(identity->parent_texture);
  }
  if (texture) {
    if (const FableTextureIdentity* identity =
            FableDirectFindTextureIdentityByObject(texture->guest_address)) {
      add_candidate(identity->object_texture);
      add_candidate(identity->parent_texture);
    }
    if (texture->parent_address != 0) {
      if (const FableTextureIdentity* identity =
              FableDirectFindTextureIdentityByObject(texture->parent_address)) {
        add_candidate(identity->object_texture);
        add_candidate(identity->parent_texture);
      }
    }
  }

  if (PlumeTrackedResource* title_billboard_sample =
          RegisterFableTitleBillboardBackingLinkLocked(
              command, slot, texture, "destination-edge")) {
    return title_billboard_sample;
  }

  for (size_t i = 0; i < candidate_count; ++i) {
    if (PlumeTrackedResource* sample = resolve_destination(candidates[i])) {
      return sample;
    }
  }
  return nullptr;
}

PlumeTrackedResource*
ResolveDirectExactProducerSampleForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (slot >= command.resources.textures.size() ||
      command.draw.source == RendererDrawEventSource::kPixVideo) {
    return nullptr;
  }

  const uint32_t guest = command.resources.textures[slot];
  const PlumeTrackedResource* tracked_texture = texture;
  if (!tracked_texture && guest != 0) {
    tracked_texture = FindDirectResourceLocked(guest);
  }
  if (tracked_texture && !PlumeGuestImageReady(*tracked_texture)) {
    tracked_texture = nullptr;
  }

  if (PlumeTrackedResource* destination_edge_sample =
          ResolveDirectDestinationEdgeSampleForSlotLocked(
              command, slot, tracked_texture)) {
    return destination_edge_sample;
  }
  if (PlumeTrackedResource* private_copy_output_sample =
          ResolveDirectPrivateCopyOutputSampleForSlotLocked(
              command, slot, tracked_texture)) {
    return private_copy_output_sample;
  }

  PlumeTrackedResource* exact_sample =
      ResolveDirectCommandTextureSlotExactProducerLocked(command, slot,
                                                        tracked_texture);
  const bool exact_sample_is_bound_target =
      exact_sample &&
      DirectCommandBindsNativeImageAsTargetLocked(command, *exact_sample);
  bool registered_private_depth_feedback = false;
  if (exact_sample_is_bound_target && exact_sample->texture_depth_target &&
      DirectResourceAddressMatchesLocked(exact_sample->guest_address,
                                         command.resources.depth_stencil)) {
    DirectPrivateTextureCopyActionMatch action_match =
        DirectPrivateTextureCopyActionMatch::kNone;
    const FablePrivateTextureCopyAction* action =
        SelectDirectExactPrivateTextureCopyActionForCommandSlotLocked(
            command, slot, tracked_texture, &action_match);
    registered_private_depth_feedback =
        action &&
        DirectPrivateTextureCopyActionMatchIsExactRegistered(action_match) &&
        action->kind == FablePrivateTextureCopyKind::kDepth &&
        (DirectResourceAddressMatchesLocked(exact_sample->guest_address,
                                            action->target_surface) ||
         DirectResourceAddressMatchesLocked(exact_sample->guest_address,
                                            action->depth_stencil)) &&
        (DirectResourceAddressMatchesLocked(action->target_surface,
                                            command.resources.depth_stencil) ||
         DirectResourceAddressMatchesLocked(action->depth_stencil,
                                            command.resources.depth_stencil));
  }
  if (!exact_sample || !IsImageResource(exact_sample->kind) ||
      !PlumeGuestImageReady(*exact_sample) ||
      FableTrackedResourceIsBlankUploadedSampleProxy(*exact_sample) ||
      (exact_sample_is_bound_target && !registered_private_depth_feedback) ||
      !DirectResourceHasTextureSampleContentForExactProducer(*exact_sample)) {
    return nullptr;
  }

  return exact_sample;
}

PlumeTrackedResource* ResolveDirectRegisteredSampleSourceLocked(
    const FableDirectRenderCommand& command,
    const FableRegisteredSampleSource& sampled_source) {
  if (sampled_source.kind == FableRegisteredSampleSourceKind::kUnknown) {
    return nullptr;
  }

  const uint64_t sampled_fetch_key =
      FableDirectTextureFetchKey(sampled_source.fetch_dwords);
  auto valid_sample_resource =
      [&](PlumeTrackedResource* resource) -> PlumeTrackedResource* {
    if (!resource || !IsImageResource(resource->kind) ||
        !PlumeGuestImageReady(*resource) ||
        FableTrackedResourceIsBlankUploadedSampleProxy(*resource) ||
        DirectCommandBindsExactResourceAsTargetLocked(command, *resource)) {
      return nullptr;
    }
    const bool exact_gpu_surface_content =
        sampled_source.kind == FableRegisteredSampleSourceKind::kGpuSurface &&
        sampled_source.content_serial != 0 &&
        FableTrackedResourceVisibleContentSerial(*resource) >=
            sampled_source.content_serial;
    if (!FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(*resource) &&
        !exact_gpu_surface_content) {
      return nullptr;
    }
    if (sampled_source.base_address != 0 &&
        PlumeGuestTextureBaseAddress(*resource) !=
            sampled_source.base_address) {
      return nullptr;
    }
    if (sampled_fetch_key != 0 &&
        FableDirectTextureFetchKey(PlumeGuestTextureFetchDwords(*resource)) !=
            sampled_fetch_key) {
      return nullptr;
    }
    if (sampled_source.content_serial != 0 &&
        FableTrackedResourceVisibleContentSerial(*resource) <
            sampled_source.content_serial) {
      return nullptr;
    }
    return resource;
  };

  if (PlumeTrackedResource* resource =
          valid_sample_resource(FindDirectResourceLocked(
              sampled_source.resource))) {
    return resource;
  }

  if (const FableTextureIdentity* identity =
          FableDirectFindTextureIdentityByObject(sampled_source.resource)) {
    if (PlumeTrackedResource* resource =
            valid_sample_resource(FindDirectResourceLocked(
                identity->object_texture))) {
      return resource;
    }
    if (PlumeTrackedResource* resource =
            valid_sample_resource(FindDirectResourceLocked(
                identity->parent_texture))) {
      return resource;
    }
  }

  if (sampled_source.base_address != 0) {
    if (const std::vector<uint32_t>* objects =
            FableDirectFindTextureIdentityObjectsByBase(
                sampled_source.base_address)) {
      for (uint32_t object : *objects) {
        if (PlumeTrackedResource* resource =
                valid_sample_resource(FindDirectResourceLocked(object))) {
          return resource;
        }
      }
    }
  }

  if (sampled_fetch_key != 0) {
    if (const std::vector<uint32_t>* objects =
            FableDirectFindTextureIdentityObjectsByFetch(
                sampled_source.fetch_dwords)) {
      for (uint32_t object : *objects) {
        if (PlumeTrackedResource* resource =
                valid_sample_resource(FindDirectResourceLocked(object))) {
          return resource;
        }
      }
    }
  }

  return nullptr;
}

bool DirectSurfaceTextureLinkIsRetiredByNewerExactEdgeLocked(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link,
    FableSurfaceTextureLink* versioned_retired_sibling_shell) {
  if (command.draw.source == RendererDrawEventSource::kPixVideo ||
      !link.trusted_destination_edge) {
    return false;
  }
  if (DirectSurfaceTextureLinkIsUnresolvedSurfaceViewIdentity(link)) {
    return false;
  }
  if (link.target_content_serial != 0 || link.backing_content_serial != 0 ||
      link.sampled_source.content_serial != 0) {
    return false;
  }

  const uint64_t command_draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  const bool can_version_retired_sibling_shell =
      link.copy_symbol_address == kFablePrivateRectSiblingWrapperCallsite &&
      link.trusted_destination_edge &&
      link.kind != FablePrivateTextureCopyKind::kDepth;
  bool has_versioned_sibling_shell = false;
  FableSurfaceTextureLink versioned_sibling_shell = {};

  auto link_visible_to_command = [&](const FableSurfaceTextureLink& candidate) {
    if (candidate.frame_index != 0 &&
        candidate.frame_index > g_direct_renderer.current_frame_index) {
      return false;
    }
    if (candidate.frame_index != 0 &&
        candidate.frame_index == g_direct_renderer.current_frame_index &&
        candidate.draw_serial != 0 && command_draw_serial != 0 &&
        candidate.draw_serial > command_draw_serial) {
      return false;
    }
    return DirectSurfaceTextureLinkPrivateWrapperVisibleToCommandLocked(
        command, candidate);
  };

  auto candidate_has_authoritative_sample_identity =
      [](const FableSurfaceTextureLink& candidate) {
    return candidate.target_content_serial != 0 ||
           candidate.backing_content_serial != 0 ||
           candidate.sampled_source.content_serial != 0 ||
           (candidate.backing_texture != 0 &&
            (candidate.backing_base_address != 0 ||
             HasTrackedFetchDwords(candidate.backing_fetch_dwords)));
  };

  auto link_texture_identity_matches = [&](const FableSurfaceTextureLink& a,
                                           const FableSurfaceTextureLink& b) {
    if (DirectResourceAddressMatchesLocked(a.texture, b.texture)) {
      return true;
    }
    if (a.texture_base_address != 0 && b.texture_base_address != 0 &&
        a.texture_base_address == b.texture_base_address) {
      return true;
    }
    const uint64_t a_fetch_key =
        FableDirectTextureFetchKey(a.texture_fetch_dwords);
    const uint64_t b_fetch_key =
        FableDirectTextureFetchKey(b.texture_fetch_dwords);
    if (a_fetch_key != 0 && b_fetch_key != 0 && a_fetch_key == b_fetch_key) {
      return true;
    }
    auto identity_matches = [&](const FableTextureIdentity* identity,
                                const FableSurfaceTextureLink& candidate) {
      if (!identity) {
        return false;
      }
      return DirectResourceAddressMatchesLocked(candidate.texture,
                                                identity->object_texture) ||
             DirectResourceAddressMatchesLocked(candidate.texture,
                                                identity->parent_texture) ||
             (identity->base_address != 0 &&
              candidate.texture_base_address == identity->base_address) ||
             (FableDirectTextureFetchKey(identity->fetch_dwords) != 0 &&
              FableDirectTextureFetchKey(identity->fetch_dwords) ==
                  FableDirectTextureFetchKey(candidate.texture_fetch_dwords));
    };
    return identity_matches(FableDirectFindTextureIdentityByObject(a.texture),
                            b) ||
           identity_matches(FableDirectFindTextureIdentityByObject(b.texture),
                            a) ||
           identity_matches(FableDirectFindTextureIdentityByBase(
                                a.texture_base_address),
                            b) ||
           identity_matches(FableDirectFindTextureIdentityByBase(
                                b.texture_base_address),
                            a);
  };

  const uint32_t texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(command);
  auto link_matches_command_slot_texture =
      [&](const FableSurfaceTextureLink& candidate, uint32_t slot) {
    constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
    if (slot >= command.resources.textures.size() ||
        (candidate.source_slot != kUnknownSlot && candidate.source_slot != slot)) {
      return false;
    }
    const uint32_t guest = command.resources.textures[slot];
    if (guest == 0) {
      return false;
    }
    if (DirectResourceAddressMatchesLocked(candidate.texture, guest)) {
      return true;
    }
    const std::array<uint32_t, 6> command_fetch =
        DirectCommandTextureFetchDwords(command, slot);
    const DirectTextureFetchInfo command_fetch_info =
        DirectTextureFetchInfoFromDwords(command_fetch);
    if (command_fetch_info.valid && candidate.texture_base_address != 0 &&
        command_fetch_info.base_address == candidate.texture_base_address) {
      return true;
    }
    const uint64_t command_fetch_key =
        FableDirectTextureFetchKey(command_fetch);
    const uint64_t link_fetch_key =
        FableDirectTextureFetchKey(candidate.texture_fetch_dwords);
    if (command_fetch_key != 0 && link_fetch_key != 0 &&
        command_fetch_key == link_fetch_key) {
      return true;
    }
    if (const PlumeTrackedResource* texture = FindDirectResourceLocked(guest)) {
      if (DirectResourceAddressMatchesLocked(candidate.texture,
                                             texture->guest_address) ||
          DirectResourceAddressMatchesLocked(candidate.texture,
                                             texture->parent_address)) {
        return true;
      }
      if (candidate.texture_base_address != 0 &&
          PlumeGuestTextureBaseAddress(*texture) ==
              candidate.texture_base_address) {
        return true;
      }
      const uint64_t tracked_fetch_key =
          FableDirectTextureFetchKey(PlumeGuestTextureFetchDwords(*texture));
      if (tracked_fetch_key != 0 && link_fetch_key != 0 &&
          tracked_fetch_key == link_fetch_key) {
        return true;
      }
    }
    if (const FableTextureIdentity* identity =
            FableDirectFindTextureIdentityByObject(guest)) {
      if (DirectResourceAddressMatchesLocked(candidate.texture,
                                             identity->object_texture) ||
          DirectResourceAddressMatchesLocked(candidate.texture,
                                             identity->parent_texture) ||
          (identity->base_address != 0 &&
           identity->base_address == candidate.texture_base_address)) {
        return true;
      }
      const uint64_t identity_fetch_key =
          FableDirectTextureFetchKey(identity->fetch_dwords);
      if (identity_fetch_key != 0 && link_fetch_key != 0 &&
          identity_fetch_key == link_fetch_key) {
        return true;
      }
    }
    return false;
  };

  auto link_matches_command_texture_identity =
      [&](const FableSurfaceTextureLink& candidate) {
    for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
      if (!DirectCommandUsesTextureSlotForSyncLocked(command, texture_slot_mask,
                                                     slot)) {
        continue;
      }
      if (link_matches_command_slot_texture(candidate, slot)) {
        return true;
      }
    }
    return false;
  };

  auto candidate_is_newer = [&](const FableSurfaceTextureLink& candidate) {
    if (candidate.serial != 0 && link.serial != 0 &&
        candidate.serial == link.serial) {
      return false;
    }
    if (!candidate.trusted_destination_edge ||
        !DirectSurfaceTextureLinkHasExactCopyIdentity(candidate) ||
        !candidate_has_authoritative_sample_identity(candidate) ||
        !link_visible_to_command(candidate) ||
        !link_texture_identity_matches(link, candidate) ||
        !link_matches_command_texture_identity(candidate)) {
      return false;
    }
    if (candidate.target_content_serial != 0 &&
        candidate.target_content_serial > link.target_content_serial) {
      return true;
    }
    if (candidate.backing_content_serial != 0 &&
        candidate.backing_content_serial > link.backing_content_serial) {
      return true;
    }
    if (candidate.sampled_source.content_serial != 0 &&
        candidate.sampled_source.content_serial >
            link.sampled_source.content_serial) {
      return true;
    }
    if (candidate.frame_index > link.frame_index) {
      return true;
    }
    return candidate.frame_index == link.frame_index &&
           candidate.draw_serial > link.draw_serial;
  };

  auto consider_links = [&](const std::vector<FableSurfaceTextureLink>* links) {
    if (!links) {
      return false;
    }
    for (const FableSurfaceTextureLink& candidate : *links) {
      if (candidate_is_newer(candidate)) {
        if (can_version_retired_sibling_shell && !has_versioned_sibling_shell) {
          versioned_sibling_shell = link;
          versioned_sibling_shell.serial = 0;
          versioned_sibling_shell.frame_index =
              g_direct_renderer.current_frame_index;
          versioned_sibling_shell.draw_serial = command_draw_serial;
          versioned_sibling_shell.render_target =
              command.resources.render_targets[0];
          versioned_sibling_shell.depth_stencil =
              command.resources.depth_stencil;
          versioned_sibling_shell.vertex_shader =
              command.resources.vertex_shader;
          versioned_sibling_shell.pixel_shader =
              command.resources.pixel_shader;
          PopulateDirectSurfaceTextureLinkShaderRoots(versioned_sibling_shell,
                                                      command);
          if (versioned_sibling_shell.texture_base_address == 0) {
            versioned_sibling_shell.texture_base_address =
                candidate.texture_base_address;
          }
          if (!HasTrackedFetchDwords(
                  versioned_sibling_shell.texture_fetch_dwords)) {
            versioned_sibling_shell.texture_fetch_dwords =
                candidate.texture_fetch_dwords;
          }
          versioned_sibling_shell.backing_texture = candidate.backing_texture;
          versioned_sibling_shell.backing_base_address =
              candidate.backing_base_address;
          versioned_sibling_shell.backing_fetch_dwords =
              candidate.backing_fetch_dwords;
          versioned_sibling_shell.backing_content_serial =
              candidate.backing_content_serial;
          versioned_sibling_shell.target_content_serial =
              candidate.target_content_serial;
          versioned_sibling_shell.sampled_source = candidate.sampled_source;
          if (versioned_sibling_shell.wrapper_source_texture == 0) {
            versioned_sibling_shell.wrapper_source_texture =
                candidate.wrapper_source_texture;
            versioned_sibling_shell.wrapper_source_exact =
                candidate.wrapper_source_exact;
          }
          if (versioned_sibling_shell.source_slot == 0xFFFFFFFFu &&
              candidate.source_slot != 0xFFFFFFFFu) {
            versioned_sibling_shell.source_slot = candidate.source_slot;
          }
          versioned_sibling_shell.kind = candidate.kind;
          has_versioned_sibling_shell = true;
        }
        return true;
      }
    }
    return false;
  };

  const bool retired =
      consider_links(FableDirectFindSurfaceTextureLinksForDestinationTexture(
          link.texture)) ||
      consider_links(FableDirectFindSurfaceTextureLinksByTexture(
          link.texture)) ||
      consider_links(FableDirectFindSurfaceTextureLinksByBase(
          link.texture_base_address)) ||
      consider_links(FableDirectFindSurfaceTextureLinksByFetch(
          link.texture_fetch_dwords));
  if (retired && has_versioned_sibling_shell) {
    if (versioned_retired_sibling_shell) {
      *versioned_retired_sibling_shell = versioned_sibling_shell;
    }
  }
  return retired;
}

PlumeTrackedResource* ResolveDirectSurfaceTextureLinkResourceLocked(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link) {
  auto valid_sample_resource =
      [&](PlumeTrackedResource* resource) -> PlumeTrackedResource* {
    if (!resource || !IsImageResource(resource->kind) ||
        !PlumeGuestImageReady(*resource) ||
        !FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(*resource) ||
        DirectResourceIsPrivateFeedbackAlias(*resource) ||
        FableTrackedResourceIsBlankUploadedSampleProxy(*resource) ||
        DirectCommandBindsResourceAsTargetLocked(command, *resource)) {
      return nullptr;
    }
    return resource;
  };

  auto valid_sample = [&](uint32_t address) -> PlumeTrackedResource* {
    if (address == 0) {
      return nullptr;
    }
    return valid_sample_resource(FindDirectResourceLocked(address));
  };

  const uint64_t command_draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;

  auto surface_link_sample_cache_key = [&]() {
    size_t seed = 0;
    DirectHashCombine(seed,
                std::hash<uint32_t>{}(g_direct_renderer.current_frame_index));
    DirectHashCombine(seed, std::hash<uint32_t>{}(command.resources.render_targets[0]));
    DirectHashCombine(seed, std::hash<uint32_t>{}(command.resources.depth_stencil));
    DirectHashCombine(seed, std::hash<uint32_t>{}(command.resources.vertex_shader));
    DirectHashCombine(seed, std::hash<uint32_t>{}(command.resources.pixel_shader));
    DirectHashCombine(seed, std::hash<uint32_t>{}(
                          static_cast<uint32_t>(command.draw.source)));
    DirectHashCombine(seed, std::hash<uint64_t>{}(
                          command.draw.private_texture_wrapper_call_serial));
    DirectHashCombine(seed, std::hash<uint64_t>{}(
                          command.draw.private_texture_wrapper_context_key));
    DirectHashCombine(seed, std::hash<uint32_t>{}(
                          command.draw.private_texture_wrapper_symbol_address));
    DirectHashCombine(seed, std::hash<uint32_t>{}(
                          command.draw.private_texture_wrapper_source_texture));
    for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
      DirectHashCombine(seed, std::hash<uint32_t>{}(slot));
      DirectHashCombine(seed,
                  std::hash<uint32_t>{}(command.resources.textures[slot]));
      const std::array<uint32_t, 6> fetch =
          DirectCommandTextureFetchDwords(command, slot);
      for (uint32_t dword : fetch) {
        DirectHashCombine(seed, std::hash<uint32_t>{}(dword));
      }
    }
    DirectHashCombine(seed, std::hash<uint64_t>{}(link.serial));
    DirectHashCombine(seed, std::hash<uint32_t>{}(link.texture));
    DirectHashCombine(seed, std::hash<uint32_t>{}(link.texture_base_address));
    DirectHashCombine(seed, std::hash<uint32_t>{}(link.source_surface));
    DirectHashCombine(seed, std::hash<uint32_t>{}(link.backing_texture));
    DirectHashCombine(seed, std::hash<uint32_t>{}(link.backing_base_address));
    DirectHashCombine(seed, std::hash<uint64_t>{}(link.target_content_serial));
    DirectHashCombine(seed, std::hash<uint64_t>{}(link.backing_content_serial));
    DirectHashCombine(seed, std::hash<uint64_t>{}(link.copy_call_serial));
    DirectHashCombine(seed, std::hash<uint64_t>{}(link.copy_context_key));
    DirectHashCombine(seed, std::hash<uint32_t>{}(link.copy_symbol_address));
    DirectHashCombine(seed, std::hash<uint32_t>{}(link.sampled_source.resource));
    DirectHashCombine(seed,
                std::hash<uint64_t>{}(link.sampled_source.content_serial));
    DirectHashCombine(seed,
                std::hash<uint32_t>{}(
                    static_cast<uint32_t>(link.sampled_source.kind)));
    return seed != 0 ? static_cast<uint64_t>(seed) : 1ull;
  };
  const uint64_t surface_link_cache_key = surface_link_sample_cache_key();
  auto find_cached_surface_link_sample = [&]() -> PlumeTrackedResource* {
    const auto it = g_direct_renderer.surface_texture_link_sample_frame_cache.find(
        surface_link_cache_key);
    if (it == g_direct_renderer.surface_texture_link_sample_frame_cache.end()) {
      return nullptr;
    }
    PlumeTrackedResource* sample = valid_sample_resource(
        FindDirectResourceLocked(it->second));
    if (!sample) {
      g_direct_renderer.surface_texture_link_sample_frame_cache.erase(it);
      return nullptr;
    }
    return sample;
  };
  auto remember_surface_link_sample =
      [&](PlumeTrackedResource* sample) -> PlumeTrackedResource* {
    if (sample) {
      g_direct_renderer.surface_texture_link_sample_frame_cache
          [surface_link_cache_key] = sample->guest_address;
    }
    return sample;
  };

  auto link_visible_to_command =
      [&](const FableSurfaceTextureLink& candidate) {
    if (candidate.frame_index != 0 &&
        candidate.frame_index > g_direct_renderer.current_frame_index) {
      return false;
    }
    if (candidate.frame_index != 0 &&
        candidate.frame_index == g_direct_renderer.current_frame_index &&
        candidate.draw_serial != 0 && command_draw_serial != 0 &&
        candidate.draw_serial > command_draw_serial) {
      return false;
    }
    return DirectSurfaceTextureLinkPrivateWrapperVisibleToCommandLocked(
        command, candidate);
  };

  auto unowned_exact_local_wrapper_link = [&]() {
    return command.draw.source != RendererDrawEventSource::kPixVideo &&
           link.copy_symbol_address ==
               kFablePrivateRectTitleLocalWrapperCallsite &&
           !DirectSurfaceTextureLinkExactWrapperOwnedByCommand(command, link);
  };

  auto publish_begin_vertices_title_edge_for_unowned_local_link = [&]() {
    if (!unowned_exact_local_wrapper_link() ||
        command.draw.private_texture_wrapper_symbol_address !=
            kFableBeginVerticesTitleWrapperSymbol) {
      return false;
    }

    constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
    const uint64_t link_fetch_key =
        FableDirectTextureFetchKey(link.texture_fetch_dwords);
    bool published = false;
    for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
      if (link.source_slot != kUnknownSlot && link.source_slot != slot) {
        continue;
      }

      const uint32_t guest = command.resources.textures[slot];
      const std::array<uint32_t, 6> command_fetch =
          DirectCommandTextureFetchDwords(command, slot);
      const DirectTextureFetchInfo command_fetch_info =
          DirectTextureFetchInfoFromDwords(command_fetch);
      PlumeTrackedResource* texture =
          guest != 0 ? FindDirectResourceLocked(guest) : nullptr;
      const uint32_t texture_base =
          texture ? PlumeGuestTextureBaseAddress(*texture) : 0u;
      const uint64_t command_fetch_key =
          FableDirectTextureFetchKey(command_fetch);
      const uint64_t texture_fetch_key =
          texture ? FableDirectTextureFetchKey(
                        PlumeGuestTextureFetchDwords(*texture))
                  : 0u;
      const bool texture_matches =
          (guest != 0 &&
           (DirectResourceAddressMatchesLocked(link.texture, guest) ||
            DirectResourceAddressMatchesLocked(link.backing_texture, guest))) ||
          (texture &&
           (DirectResourceAddressMatchesLocked(link.texture,
                                               texture->guest_address) ||
            DirectResourceAddressMatchesLocked(link.texture,
                                               texture->parent_address) ||
            DirectResourceAddressMatchesLocked(link.backing_texture,
                                               texture->guest_address) ||
            DirectResourceAddressMatchesLocked(link.backing_texture,
                                               texture->parent_address))) ||
          (command_fetch_info.valid &&
           (link.texture_base_address == command_fetch_info.base_address ||
            link.backing_base_address == command_fetch_info.base_address)) ||
          (texture_base != 0 &&
           (link.texture_base_address == texture_base ||
            link.backing_base_address == texture_base)) ||
          (link_fetch_key != 0 &&
           (link_fetch_key == command_fetch_key ||
            link_fetch_key == texture_fetch_key));
      if (!texture_matches) {
        continue;
      }

      published |= PublishDirectBeginVerticesTitleTextureActionForSlotLocked(
          command, slot, texture);
    }
    return published;
  };


  auto log_unowned_exact_local_wrapper_link = [&]() {
    if (!unowned_exact_local_wrapper_link()) {
      return;
    }

    static uint32_t logged_exact_local_wrapper_owner_filters = 0;
    static uint32_t local_wrapper_owner_filter_frame = UINT32_MAX;
    static std::unordered_set<uint64_t> local_wrapper_owner_filter_keys;
    if (local_wrapper_owner_filter_frame !=
        g_direct_renderer.current_frame_index) {
      local_wrapper_owner_filter_frame = g_direct_renderer.current_frame_index;
      local_wrapper_owner_filter_keys.clear();
    }
    size_t owner_seed = 0;
    DirectHashCombine(owner_seed,
                std::hash<uint32_t>{}(g_direct_renderer.current_frame_index));
    DirectHashCombine(owner_seed, std::hash<uint64_t>{}(command.serial));
    DirectHashCombine(owner_seed, std::hash<uint64_t>{}(command.draw_serial));
    DirectHashCombine(owner_seed, std::hash<uint32_t>{}(link.texture));
    DirectHashCombine(owner_seed, std::hash<uint32_t>{}(link.source_surface));
    DirectHashCombine(owner_seed, std::hash<uint32_t>{}(link.backing_texture));
    DirectHashCombine(owner_seed, std::hash<uint64_t>{}(link.target_content_serial));
    DirectHashCombine(owner_seed, std::hash<uint64_t>{}(link.backing_content_serial));
    DirectHashCombine(owner_seed, std::hash<uint64_t>{}(link.copy_call_serial));
    DirectHashCombine(owner_seed, std::hash<uint64_t>{}(link.copy_context_key));
    DirectHashCombine(owner_seed, std::hash<uint32_t>{}(link.copy_symbol_address));
    DirectHashCombine(owner_seed,
                std::hash<uint64_t>{}(
                    command.draw.private_texture_wrapper_call_serial));
    DirectHashCombine(owner_seed,
                std::hash<uint64_t>{}(
                    command.draw.private_texture_wrapper_context_key));
    DirectHashCombine(owner_seed,
                std::hash<uint32_t>{}(
                    command.draw.private_texture_wrapper_symbol_address));
    DirectHashCombine(owner_seed, std::hash<uint32_t>{}(link.wrapper_source_texture));
    DirectHashCombine(owner_seed,
                std::hash<uint32_t>{}(
                    command.draw.private_texture_wrapper_source_texture));
    DirectHashCombine(owner_seed,
                std::hash<uint32_t>{}(command.resources.render_targets[0]));
    DirectHashCombine(owner_seed,
                std::hash<uint32_t>{}(command.resources.depth_stencil));
    DirectHashCombine(owner_seed,
                std::hash<uint32_t>{}(command.resources.vertex_shader));
    DirectHashCombine(owner_seed,
                std::hash<uint32_t>{}(command.resources.pixel_shader));
    const uint64_t owner_key =
        owner_seed != 0 ? static_cast<uint64_t>(owner_seed) : 1ull;
    if (logged_exact_local_wrapper_owner_filters < 48u &&
        local_wrapper_owner_filter_keys.insert(owner_key).second) {
      REXLOG_INFO(
          "Refii plume exact local wrapper surface-link owner filter #{} "
          "reason=not-command-owner frame={} texture=0x{:08X} "
          "surface=0x{:08X} backing=0x{:08X} "
          "target_serial={} backing_serial={} link_call={} "
          "cmd_call={} link_context=0x{:016X} cmd_context=0x{:016X} "
          "link_symbol=0x{:08X} cmd_symbol=0x{:08X} "
          "link_wrapper_source=0x{:08X}/{} cmd_wrapper_source=0x{:08X}/{} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_local_wrapper_owner_filters,
          g_direct_renderer.current_frame_index, link.texture,
          link.source_surface, link.backing_texture,
          static_cast<unsigned long long>(link.target_content_serial),
          static_cast<unsigned long long>(link.backing_content_serial),
          static_cast<unsigned long long>(link.copy_call_serial),
          static_cast<unsigned long long>(
              command.draw.private_texture_wrapper_call_serial),
          static_cast<unsigned long long>(link.copy_context_key),
          static_cast<unsigned long long>(
              command.draw.private_texture_wrapper_context_key),
          link.copy_symbol_address,
          command.draw.private_texture_wrapper_symbol_address,
          link.wrapper_source_texture, link.wrapper_source_exact ? 1 : 0,
          command.draw.private_texture_wrapper_source_texture,
          command.draw.private_texture_wrapper_source_exact ? 1 : 0,
          command.resources.render_targets[0],
          command.resources.depth_stencil, command.resources.vertex_shader,
          command.resources.pixel_shader);
    }
  };
  const bool published_begin_vertices_title_edge =
      publish_begin_vertices_title_edge_for_unowned_local_link();
  if (unowned_exact_local_wrapper_link()) {
    if (published_begin_vertices_title_edge) {
      static uint32_t logged_exact_local_wrapper_beginvertices_ownership = 0;
      static uint32_t beginvertices_ownership_frame = UINT32_MAX;
      static std::unordered_set<uint64_t> beginvertices_ownership_keys;
      if (beginvertices_ownership_frame !=
          g_direct_renderer.current_frame_index) {
        beginvertices_ownership_frame = g_direct_renderer.current_frame_index;
        beginvertices_ownership_keys.clear();
      }
      size_t beginvertices_seed = 0;
      DirectHashCombine(beginvertices_seed,
                  std::hash<uint32_t>{}(g_direct_renderer.current_frame_index));
      DirectHashCombine(beginvertices_seed, std::hash<uint32_t>{}(link.texture));
      DirectHashCombine(beginvertices_seed,
                  std::hash<uint32_t>{}(link.source_surface));
      DirectHashCombine(beginvertices_seed,
                  std::hash<uint32_t>{}(link.backing_texture));
      DirectHashCombine(beginvertices_seed,
                  std::hash<uint64_t>{}(link.copy_call_serial));
      DirectHashCombine(beginvertices_seed,
                  std::hash<uint64_t>{}(link.copy_context_key));
      DirectHashCombine(beginvertices_seed,
                  std::hash<uint32_t>{}(link.copy_symbol_address));
      DirectHashCombine(
          beginvertices_seed,
          std::hash<uint64_t>{}(
              command.draw.private_texture_wrapper_call_serial));
      DirectHashCombine(
          beginvertices_seed,
          std::hash<uint64_t>{}(
              command.draw.private_texture_wrapper_context_key));
      DirectHashCombine(
          beginvertices_seed,
          std::hash<uint32_t>{}(
              command.draw.private_texture_wrapper_symbol_address));
      DirectHashCombine(beginvertices_seed,
                  std::hash<uint32_t>{}(command.resources.render_targets[0]));
      DirectHashCombine(beginvertices_seed,
                  std::hash<uint32_t>{}(command.resources.depth_stencil));
      DirectHashCombine(beginvertices_seed,
                  std::hash<uint32_t>{}(command.resources.vertex_shader));
      DirectHashCombine(beginvertices_seed,
                  std::hash<uint32_t>{}(command.resources.pixel_shader));
      const uint64_t beginvertices_key =
          beginvertices_seed != 0 ? static_cast<uint64_t>(beginvertices_seed)
                                  : 1ull;
      if (logged_exact_local_wrapper_beginvertices_ownership < 48u &&
          beginvertices_ownership_keys.insert(beginvertices_key).second) {
        REXLOG_INFO(
            "Refii plume exact local wrapper BeginVertices ownership proof #{} "
            "reason=beginvertices-title-owner frame={} texture=0x{:08X} "
            "surface=0x{:08X} backing=0x{:08X} link_call={} cmd_call={} "
            "link_context=0x{:016X} cmd_context=0x{:016X} "
            "link_symbol=0x{:08X} cmd_symbol=0x{:08X} rt=0x{:08X} "
            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_local_wrapper_beginvertices_ownership,
            g_direct_renderer.current_frame_index, link.texture,
            link.source_surface, link.backing_texture,
            static_cast<unsigned long long>(link.copy_call_serial),
            static_cast<unsigned long long>(
                command.draw.private_texture_wrapper_call_serial),
            static_cast<unsigned long long>(link.copy_context_key),
            static_cast<unsigned long long>(
                command.draw.private_texture_wrapper_context_key),
            link.copy_symbol_address,
            command.draw.private_texture_wrapper_symbol_address,
            command.resources.render_targets[0],
            command.resources.depth_stencil, command.resources.vertex_shader,
            command.resources.pixel_shader);
      }
      return nullptr;
    }
    log_unowned_exact_local_wrapper_link();
  }

  if (PlumeTrackedResource* cached_sample = find_cached_surface_link_sample()) {
    return cached_sample;
  }

  auto version_retired_native_depth_link_from_exact_output_sample =
      [&]() -> bool {
    constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
    if (command.draw.source == RendererDrawEventSource::kPixVideo ||
        !link.trusted_destination_edge ||
        !DirectSurfaceTextureLinkHasExactCopyIdentity(link) ||
        link.kind != FablePrivateTextureCopyKind::kDepth ||
        FablePrivateTextureProxyRoleForAddress(link.texture) !=
            FablePrivateTextureProxyRole::kDepth ||
        link.source_surface == 0 ||
        DirectResourceAddressMatchesLocked(link.texture, link.source_surface)) {
      return false;
    }

    const uint32_t texture_slot_mask =
        DirectCommandPixelTextureSlotMaskLocked(command);
    const uint64_t link_fetch_key =
        FableDirectTextureFetchKey(link.texture_fetch_dwords);
    auto link_matches_command_slot_texture = [&](uint32_t slot) {
      if (slot >= command.resources.textures.size() ||
          (link.source_slot != kUnknownSlot && link.source_slot != slot)) {
        return false;
      }
      const uint32_t guest = command.resources.textures[slot];
      if (guest == 0) {
        return false;
      }
      if (DirectResourceAddressMatchesLocked(link.texture, guest)) {
        return true;
      }
      const std::array<uint32_t, 6> command_fetch =
          DirectCommandTextureFetchDwords(command, slot);
      const DirectTextureFetchInfo command_fetch_info =
          DirectTextureFetchInfoFromDwords(command_fetch);
      if (command_fetch_info.valid && link.texture_base_address != 0 &&
          command_fetch_info.base_address == link.texture_base_address) {
        return true;
      }
      const uint64_t command_fetch_key =
          FableDirectTextureFetchKey(command_fetch);
      if (command_fetch_key != 0 && link_fetch_key != 0 &&
          command_fetch_key == link_fetch_key) {
        return true;
      }
      if (const PlumeTrackedResource* texture = FindDirectResourceLocked(guest)) {
        if (DirectResourceAddressMatchesLocked(link.texture,
                                               texture->guest_address) ||
            DirectResourceAddressMatchesLocked(link.texture,
                                               texture->parent_address)) {
          return true;
        }
        if (link.texture_base_address != 0 &&
            PlumeGuestTextureBaseAddress(*texture) ==
                link.texture_base_address) {
          return true;
        }
        const uint64_t tracked_fetch_key =
            FableDirectTextureFetchKey(PlumeGuestTextureFetchDwords(*texture));
        if (tracked_fetch_key != 0 && link_fetch_key != 0 &&
            tracked_fetch_key == link_fetch_key) {
          return true;
        }
      }
      if (const FableTextureIdentity* identity =
              FableDirectFindTextureIdentityByObject(guest)) {
        if (DirectResourceAddressMatchesLocked(link.texture,
                                               identity->object_texture) ||
            DirectResourceAddressMatchesLocked(link.texture,
                                               identity->parent_texture) ||
            (identity->base_address != 0 &&
             identity->base_address == link.texture_base_address)) {
          return true;
        }
        const uint64_t identity_fetch_key =
            FableDirectTextureFetchKey(identity->fetch_dwords);
        if (identity_fetch_key != 0 && link_fetch_key != 0 &&
            identity_fetch_key == link_fetch_key) {
          return true;
        }
      }
      return false;
    };

    for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
      if (!DirectCommandUsesTextureSlotForSyncLocked(command, texture_slot_mask,
                                                     slot)) {
        continue;
      }
      if (!link_matches_command_slot_texture(slot)) {
        continue;
      }

      const uint32_t guest = command.resources.textures[slot];
      PlumeTrackedResource* texture =
          guest != 0 ? FindDirectResourceLocked(guest) : nullptr;
      PlumeTrackedResource* source =
          ResolveDirectPrivateCopyOutputSampleForSlotLocked(command, slot,
                                                            texture);
      if (!source || !IsImageResource(source->kind) ||
          !PlumeGuestImageReady(*source) ||
          FableTrackedResourceIsBlankUploadedSampleProxy(*source) ||
          DirectCommandBindsResourceAsTargetLocked(command, *source) ||
          !DirectResourceAddressMatchesLocked(source->guest_address,
                                              link.source_surface) ||
          FableTrackedResourceVisibleContentSerial(*source) == 0) {
        continue;
      }

      FableSurfaceTextureLink versioned_link = link;
      versioned_link.serial = 0;
      versioned_link.frame_index = g_direct_renderer.current_frame_index;
      versioned_link.draw_serial = command_draw_serial;
      versioned_link.source_slot = slot;
      versioned_link.render_target = command.resources.render_targets[0];
      versioned_link.depth_stencil = command.resources.depth_stencil;
      versioned_link.vertex_shader = command.resources.vertex_shader;
      versioned_link.pixel_shader = command.resources.pixel_shader;
      PopulateDirectSurfaceTextureLinkShaderRoots(versioned_link, command);
      PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(versioned_link,
                                                             *source);
      RegisterDirectSurfaceTextureLinkPreparedLocked(
          versioned_link, "exact-depth-source-surface-texture-identity");

      static uint32_t logged_versioned_native_depth_links = 0;
      if (RendererVerboseTracingEnabled() &&
          logged_versioned_native_depth_links < 48u) {
        REXLOG_INFO(
            "Refii plume exact depth source-surface texture identity #{} "
            "frame={} draw={} slot={} texture=0x{:08X} "
            "surface=0x{:08X}/{}x{} content={} call={} "
            "context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} "
            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_versioned_native_depth_links,
            g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(command_draw_serial), slot,
            versioned_link.texture, source->guest_address,
            PlumeGuestTextureWidth(*source), PlumeGuestTextureHeight(*source),
            static_cast<unsigned long long>(
                versioned_link.sampled_source.content_serial),
            static_cast<unsigned long long>(versioned_link.copy_call_serial),
            static_cast<unsigned long long>(versioned_link.copy_context_key),
            versioned_link.copy_symbol_address,
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
      return true;
    }
    return false;
  };

  if (DirectSurfaceTextureLinkIsCurrentTargetSelfEdgeLocked(command, link)) {
    static uint32_t logged_exact_surface_link_self_edges = 0;
    if (RendererVerboseTracingEnabled() &&
        logged_exact_surface_link_self_edges < 24u &&
        DirectSurfaceTextureLinkTouchesFablePrivateChain(link, nullptr)) {
      REXLOG_INFO(
          "Refii plume exact surface-link self-edge #{} frame={} "
          "texture=0x{:08X} surface=0x{:08X} backing=0x{:08X} "
          "target_serial={} backing_serial={} rt=0x{:08X} vs=0x{:08X} "
          "ps=0x{:08X}",
          ++logged_exact_surface_link_self_edges,
          g_direct_renderer.current_frame_index, link.texture,
          link.source_surface, link.backing_texture,
          static_cast<unsigned long long>(link.target_content_serial),
          static_cast<unsigned long long>(link.backing_content_serial),
          command.resources.render_targets[0], command.resources.vertex_shader,
          command.resources.pixel_shader);
    }
    return nullptr;
  }
  if (DirectSurfaceTextureLinkIsCurrentTargetExactEdgeLocked(command,
                                                                   link)) {
    auto current_target_backing_sample = [&]() -> PlumeTrackedResource* {
      if (link.backing_texture == 0 ||
          link.sampled_source.kind !=
              FableRegisteredSampleSourceKind::kBackingTexture) {
        return nullptr;
      }
      PlumeTrackedResource* backing = FindDirectResourceLocked(link.backing_texture);
      if (!backing || !IsImageResource(backing->kind) ||
          !PlumeGuestImageReady(*backing) ||
          !FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(*backing) ||
          FableTrackedResourceIsBlankUploadedSampleProxy(*backing)) {
        return nullptr;
      }
      auto backing_is_live_target_storage = [&]() {
        for (uint32_t target_address : command.resources.render_targets) {
          if (target_address == 0) {
            continue;
          }
          if (target_address == backing->guest_address) {
            return true;
          }
          const PlumeTrackedResource* target =
              FindDirectResourceLocked(target_address);
          if (target && target->guest_texture.texture &&
              backing->guest_texture.texture &&
              target->guest_texture.texture.get() ==
                  backing->guest_texture.texture.get()) {
            return true;
          }
        }
        const uint32_t depth_address = command.resources.depth_stencil;
        if (depth_address == 0) {
          return false;
        }
        if (depth_address == backing->guest_address) {
          return true;
        }
        const PlumeTrackedResource* depth =
            FindDirectResourceLocked(depth_address);
        return depth && depth->guest_texture.texture &&
               backing->guest_texture.texture &&
               depth->guest_texture.texture.get() ==
                   backing->guest_texture.texture.get();
      };
      if (backing_is_live_target_storage()) {
        return nullptr;
      }
      if (link.backing_content_serial != 0 &&
          FableTrackedResourceVisibleContentSerial(*backing) <
              link.backing_content_serial) {
        return nullptr;
      }
      static uint32_t logged_current_target_backing_samples = 0;
      if (RendererVerboseTracingEnabled() &&
          logged_current_target_backing_samples < 48u &&
          DirectSurfaceTextureLinkTouchesFablePrivateChain(link, backing)) {
        REXLOG_INFO(
            "Refii plume exact current-target backing sample #{} frame={} "
            "texture=0x{:08X} surface=0x{:08X} backing=0x{:08X}/{}x{} "
            "content={} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_current_target_backing_samples,
            g_direct_renderer.current_frame_index, link.texture,
            link.source_surface, backing->guest_address,
            PlumeGuestTextureWidth(*backing), PlumeGuestTextureHeight(*backing),
            static_cast<unsigned long long>(
                FableTrackedResourceVisibleContentSerial(*backing)),
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
      return backing;
    };
    if (PlumeTrackedResource* backing_sample =
            current_target_backing_sample()) {
      return remember_surface_link_sample(backing_sample);
    }

    uint64_t current_target_edge_key = 0xCBF29CE484222325ull;
    auto mix_current_target_edge_key = [&](uint64_t value) {
      current_target_edge_key ^= value;
      current_target_edge_key *= 0x100000001B3ull;
    };
    mix_current_target_edge_key(g_direct_renderer.current_frame_index);
    mix_current_target_edge_key(link.texture);
    mix_current_target_edge_key(link.source_surface);
    mix_current_target_edge_key(link.target_content_serial);
    mix_current_target_edge_key(command.resources.render_targets[0]);
    mix_current_target_edge_key(command.resources.depth_stencil);
    mix_current_target_edge_key(command.resources.vertex_shader);
    mix_current_target_edge_key(command.resources.pixel_shader);
    const bool first_current_target_edge_this_frame =
        g_direct_renderer.current_target_surface_link_frame_keys
            .insert(current_target_edge_key)
            .second;
    static uint32_t logged_exact_surface_link_current_target_edges = 0;
    if (RendererVerboseTracingEnabled() &&
        first_current_target_edge_this_frame &&
        logged_exact_surface_link_current_target_edges < 24u &&
        DirectSurfaceTextureLinkTouchesFablePrivateChain(link, nullptr)) {
      REXLOG_INFO(
          "Refii plume exact surface-link current-target ownership proof #{} "
          "reason=private-chain-current-target frame={} texture=0x{:08X} "
          "surface=0x{:08X} backing=0x{:08X} target_serial={} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_surface_link_current_target_edges,
          g_direct_renderer.current_frame_index, link.texture,
          link.source_surface, link.backing_texture,
          static_cast<unsigned long long>(link.target_content_serial),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return nullptr;
  }

  if (!DirectSurfaceTextureLinkExactLocalWrapperVisibleToCommandLocked(command,
                                                                       link)) {
    return nullptr;
  }

  FableSurfaceTextureLink exact_sibling_source_shell = {};
  if (DirectSurfaceTextureLinkIsRetiredByNewerExactEdgeLocked(
          command, link, &exact_sibling_source_shell)) {
    if (version_retired_native_depth_link_from_exact_output_sample()) {
      return nullptr;
    }
    if (exact_sibling_source_shell.texture != 0 &&
        exact_sibling_source_shell.source_surface != 0) {
      RegisterDirectSurfaceTextureLinkPreparedLocked(
          exact_sibling_source_shell, "exact-private-rect-source-surface");

      static uint32_t logged_exact_sibling_source_surface_identities = 0;
      if (logged_exact_sibling_source_surface_identities < 48u) {
        REXLOG_INFO(
            "Refii plume exact private-rect source-surface texture identity #{} "
            "frame={} serial={} draw={} slot={} texture=0x{:08X} "
            "surface=0x{:08X} sample=0x{:08X} content={} call={} "
            "context=0x{:016X} symbol=0x{:08X} "
            "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_sibling_source_surface_identities,
            g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(command.serial),
            static_cast<unsigned long long>(
                exact_sibling_source_shell.draw_serial),
            exact_sibling_source_shell.source_slot,
            exact_sibling_source_shell.texture,
            exact_sibling_source_shell.source_surface,
            exact_sibling_source_shell.sampled_source.resource,
            static_cast<unsigned long long>(
                exact_sibling_source_shell.sampled_source.content_serial),
            static_cast<unsigned long long>(
                exact_sibling_source_shell.copy_call_serial),
            static_cast<unsigned long long>(
                exact_sibling_source_shell.copy_context_key),
            exact_sibling_source_shell.copy_symbol_address,
            exact_sibling_source_shell.render_target,
            exact_sibling_source_shell.depth_stencil,
            exact_sibling_source_shell.vertex_shader,
            exact_sibling_source_shell.pixel_shader);
      }
      return nullptr;
    }

    auto retired_link_resolves_to_exact_output_sample = [&]() -> bool {
      constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
      if (command.draw.source == RendererDrawEventSource::kPixVideo ||
          !link.trusted_destination_edge || link.texture == 0 ||
          link.source_surface == 0 ||
          DirectResourceAddressMatchesLocked(link.texture,
                                             link.source_surface)) {
        return false;
      }

      const uint32_t texture_slot_mask =
          DirectCommandPixelTextureSlotMaskLocked(command);
      for (uint32_t slot = 0; slot < command.resources.textures.size();
           ++slot) {
        if (link.source_slot != kUnknownSlot && link.source_slot != slot) {
          continue;
        }
        if (!DirectCommandUsesTextureSlotForSyncLocked(command,
                                                       texture_slot_mask,
                                                       slot)) {
          continue;
        }
        const uint32_t guest = command.resources.textures[slot];
        if (!DirectResourceAddressMatchesLocked(link.texture, guest)) {
          continue;
        }

        PlumeTrackedResource* texture =
            guest != 0 ? FindDirectResourceLocked(guest) : nullptr;
        PlumeTrackedResource* sample =
            ResolveDirectPrivateCopyOutputSampleForSlotLocked(command, slot,
                                                              texture);
        if (!sample || !IsImageResource(sample->kind) ||
            !PlumeGuestImageReady(*sample) ||
            FableTrackedResourceIsBlankUploadedSampleProxy(*sample) ||
            !DirectResourceAddressMatchesLocked(sample->guest_address,
                                                link.source_surface)) {
          continue;
        }

        static uint32_t logged_versioned_exact_link_output_samples = 0;
        if (logged_versioned_exact_link_output_samples < 48u &&
            DirectSurfaceTextureLinkTouchesFablePrivateChain(link, nullptr)) {
          REXLOG_INFO(
              "Refii plume exact versioned surface-link output sample #{} "
              "frame={} slot={} texture=0x{:08X} "
              "surface=0x{:08X}/{}x{} content={} rt=0x{:08X} "
              "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} link_call={} "
              "link_context=0x{:016X} link_symbol=0x{:08X}",
              ++logged_versioned_exact_link_output_samples,
              g_direct_renderer.current_frame_index, slot, link.texture,
              sample->guest_address, PlumeGuestTextureWidth(*sample),
              PlumeGuestTextureHeight(*sample),
              static_cast<unsigned long long>(
                  FableTrackedResourceVisibleContentSerial(*sample)),
              command.resources.render_targets[0],
              command.resources.depth_stencil,
              command.resources.vertex_shader,
              command.resources.pixel_shader,
              static_cast<unsigned long long>(link.copy_call_serial),
              static_cast<unsigned long long>(link.copy_context_key),
              link.copy_symbol_address);
        }
        return true;
      }
      return false;
    };
    if (retired_link_resolves_to_exact_output_sample()) {
      return nullptr;
    }

    auto retired_link_resolves_to_exact_copy_edge = [&]() -> bool {
      constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
      if (command.draw.source == RendererDrawEventSource::kPixVideo ||
          !link.trusted_destination_edge ||
          !DirectSurfaceTextureLinkHasExactCopyIdentity(link) ||
          !DirectSurfaceTextureLinkTouchesFablePrivateChain(link, nullptr) ||
          link.texture == 0 || link.source_surface == 0 ||
          DirectResourceAddressMatchesLocked(link.texture,
                                             link.source_surface)) {
        return false;
      }

      const uint32_t texture_slot_mask =
          DirectCommandPixelTextureSlotMaskLocked(command);
      for (uint32_t slot = 0; slot < command.resources.textures.size();
           ++slot) {
        if (link.source_slot != kUnknownSlot && link.source_slot != slot) {
          continue;
        }
        if (!DirectCommandUsesTextureSlotForSyncLocked(command,
                                                       texture_slot_mask,
                                                       slot)) {
          continue;
        }
        const uint32_t guest = command.resources.textures[slot];
        if (!DirectResourceAddressMatchesLocked(link.texture, guest)) {
          continue;
        }

        PlumeTrackedResource* source =
            FindDirectResourceLocked(link.source_surface);
        PlumeTrackedResource* destination =
            guest != 0 ? FindDirectResourceLocked(guest) : nullptr;
        const bool exact_native_depth_live_source =
            link.kind == FablePrivateTextureCopyKind::kDepth && source &&
            IsImageResource(source->kind) && PlumeGuestImageReady(*source) &&
            !FableTrackedResourceIsBlankUploadedSampleProxy(*source) &&
            FableTrackedResourceHasGpuProducedContent(*source) &&
            DirectResourceAddressMatchesLocked(source->guest_address,
                                               link.source_surface) &&
            !DirectCommandBindsResourceAsTargetLocked(command, *source);
        if (exact_native_depth_live_source) {
          static uint32_t logged_versioned_exact_live_depth_edges = 0;
          if (logged_versioned_exact_live_depth_edges < 48u) {
            REXLOG_INFO(
                "Refii plume exact versioned native-depth surface-link "
                "live source edge #{} frame={} slot={} texture=0x{:08X} "
                "surface=0x{:08X}/{}x{} content={} depth_target={} "
                "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} link_call={} "
                "link_context=0x{:016X} link_symbol=0x{:08X}",
                ++logged_versioned_exact_live_depth_edges,
                g_direct_renderer.current_frame_index, slot, link.texture,
                source->guest_address, PlumeGuestTextureWidth(*source),
                PlumeGuestTextureHeight(*source),
                static_cast<unsigned long long>(
                    FableTrackedResourceVisibleContentSerial(*source)),
                source->texture_depth_target ? 1u : 0u,
                command.resources.render_targets[0],
                command.resources.depth_stencil,
                command.resources.vertex_shader,
                command.resources.pixel_shader,
                static_cast<unsigned long long>(link.copy_call_serial),
                static_cast<unsigned long long>(link.copy_context_key),
                link.copy_symbol_address);
          }
          return true;
        }
        auto exact_alias_matches_retired_native_depth_link = [&]() {
          if (link.kind != FablePrivateTextureCopyKind::kDepth ||
              FablePrivateTextureProxyRoleForAddress(link.texture) !=
                  FablePrivateTextureProxyRole::kDepth) {
            return false;
          }

          RendererSurfaceCopyAliasSnapshot alias;
          if (!FindDirectSurfaceCopyAliasByDestinationAddressLocked(
                  guest, &alias) ||
              !DirectSurfaceCopyAliasHasExactIdentity(alias)) {
            return false;
          }
          const bool has_matching_identity =
              (alias.copy_call_serial != 0 &&
               link.copy_call_serial == alias.copy_call_serial) ||
              (alias.copy_context_key != 0 &&
               link.copy_context_key == alias.copy_context_key) ||
              (alias.copy_symbol_address != 0 &&
               link.copy_symbol_address == alias.copy_symbol_address);
          if (!has_matching_identity) {
            return false;
          }
          if (alias.copy_symbol_address != 0 &&
              link.copy_symbol_address != 0 &&
              link.copy_symbol_address != alias.copy_symbol_address) {
            return false;
          }
          if (link.copy_call_serial != 0 && alias.copy_call_serial != 0 &&
              link.copy_call_serial != alias.copy_call_serial) {
            return false;
          }
          if (link.copy_context_key != 0 && alias.copy_context_key != 0 &&
              link.copy_context_key != alias.copy_context_key) {
            return false;
          }
          if (!DirectResourceAddressMatchesLocked(link.texture,
                                                  alias.destination_surface)) {
            return false;
          }
          if (DirectResourceAddressMatchesLocked(link.source_surface,
                                                 alias.source_surface)) {
            return true;
          }
          return DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
              link, link.source_surface, alias.source_surface);
        };
        auto exact_action_matches_retired_native_depth_link = [&]() {
          if (link.kind != FablePrivateTextureCopyKind::kDepth ||
              FablePrivateTextureProxyRoleForAddress(link.texture) !=
                  FablePrivateTextureProxyRole::kDepth) {
            return false;
          }

          const uint64_t command_draw_serial =
              command.draw_serial != 0 ? command.draw_serial : command.serial;
          auto address_matches = [](uint32_t lhs, uint32_t rhs) {
            return lhs != 0 && rhs != 0 &&
                   DirectResourceAddressMatchesLocked(lhs, rhs);
          };

          auto action_matches_retired_link =
              [&](const FablePrivateTextureCopyAction& action) {
            if (action.kind != FablePrivateTextureCopyKind::kDepth) {
              return false;
            }
            if (action.source_slot != kUnknownSlot &&
                action.source_slot != slot) {
              return false;
            }
            if (action.frame_index != 0 &&
                action.frame_index > g_direct_renderer.current_frame_index) {
              return false;
            }
            if (action.frame_index != 0 &&
                action.frame_index == g_direct_renderer.current_frame_index &&
                action.draw_serial != 0 && command_draw_serial != 0 &&
                action.draw_serial > command_draw_serial) {
              return false;
            }

            const bool source_matches =
                address_matches(action.source_texture, link.texture) ||
                address_matches(action.wrapper_source_texture, link.texture) ||
                address_matches(action.source_base_address,
                                link.texture_base_address);
            if (!source_matches) {
              return false;
            }

            const bool target_matches =
                address_matches(action.target_surface, link.source_surface) ||
                address_matches(action.depth_stencil, link.source_surface) ||
                DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
                    link, link.source_surface, action.target_surface) ||
                DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
                    link, link.source_surface, action.depth_stencil);
            if (!target_matches) {
              return false;
            }

            const bool has_matching_identity =
                (action.wrapper_call_serial != 0 &&
                 link.copy_call_serial == action.wrapper_call_serial) ||
                (action.wrapper_context_key != 0 &&
                 link.copy_context_key == action.wrapper_context_key) ||
                (action.wrapper_symbol_address != 0 &&
                 link.copy_symbol_address != 0 &&
                 FablePrivateRectWrapperSymbolsCompatible(
                     action.wrapper_symbol_address,
                     link.copy_symbol_address));
            if (!has_matching_identity) {
              return false;
            }
            if (link.copy_call_serial != 0 &&
                action.wrapper_call_serial != 0 &&
                link.copy_call_serial != action.wrapper_call_serial) {
              return false;
            }
            if (link.copy_context_key != 0 &&
                action.wrapper_context_key != 0 &&
                link.copy_context_key != action.wrapper_context_key) {
              return false;
            }
            if (link.copy_symbol_address != 0 &&
                action.wrapper_symbol_address != 0 &&
                !FablePrivateRectWrapperSymbolsCompatible(
                    link.copy_symbol_address,
                    action.wrapper_symbol_address)) {
              return false;
            }
            return true;
          };

          auto action_list_matches_retired_link =
              [&](const std::vector<FablePrivateTextureCopyAction>* actions) {
            if (!actions) {
              return false;
            }
            for (auto it = actions->rbegin(); it != actions->rend(); ++it) {
              if (action_matches_retired_link(*it)) {
                return true;
              }
            }
            return false;
          };

          if (link.copy_call_serial != 0) {
            if (const FablePrivateTextureCopyAction* action =
                    FableDirectFindPrivateTextureCopyActionByWrapperCall(
                        link.copy_call_serial);
                action && action_matches_retired_link(*action)) {
              return true;
            }
          }
          if (action_list_matches_retired_link(
                  FableDirectFindPrivateTextureCopyActionsBySource(
                      link.texture)) ||
              action_list_matches_retired_link(
                  FableDirectFindPrivateTextureCopyActionsByTarget(
                      link.source_surface)) ||
              action_list_matches_retired_link(
                  FableDirectFindPrivateTextureCopyActionsForDestinationTextureSlot(
                      link.texture, slot)) ||
              action_list_matches_retired_link(
                  FableDirectFindPrivateTextureCopyActionsForDestinationTexture(
                      link.texture))) {
            return true;
          }
          if (link.texture_base_address != 0 &&
              action_list_matches_retired_link(
                  FableDirectFindPrivateTextureCopyActionsByBase(
                      link.texture_base_address))) {
            return true;
          }
          return false;
        };
        const bool exact_native_depth_edge_owner =
            source && IsImageResource(source->kind) &&
            PlumeGuestImageReady(*source) &&
            !FableTrackedResourceIsBlankUploadedSampleProxy(*source) &&
            DirectResourceAddressMatchesLocked(source->guest_address,
                                               link.source_surface) &&
            (exact_alias_matches_retired_native_depth_link() ||
             exact_action_matches_retired_native_depth_link());
        if (exact_native_depth_edge_owner) {
          static uint32_t logged_versioned_exact_native_depth_edges = 0;
          if (logged_versioned_exact_native_depth_edges < 48u) {
            REXLOG_INFO(
                "Refii plume exact versioned native-depth surface-link "
                "exact depth edge #{} frame={} slot={} "
                "texture=0x{:08X} surface=0x{:08X}/{}x{} "
                "destination=0x{:08X}/{}x{} rt=0x{:08X} ds=0x{:08X} "
                "vs=0x{:08X} ps=0x{:08X} link_call={} "
                "link_context=0x{:016X} link_symbol=0x{:08X}",
                ++logged_versioned_exact_native_depth_edges,
                g_direct_renderer.current_frame_index, slot, link.texture,
                source->guest_address, PlumeGuestTextureWidth(*source),
                PlumeGuestTextureHeight(*source),
                destination ? destination->guest_address : guest,
                destination ? PlumeGuestTextureWidth(*destination) : 0u,
                destination ? PlumeGuestTextureHeight(*destination) : 0u,
                command.resources.render_targets[0],
                command.resources.depth_stencil,
                command.resources.vertex_shader,
                command.resources.pixel_shader,
                static_cast<unsigned long long>(link.copy_call_serial),
                static_cast<unsigned long long>(link.copy_context_key),
                link.copy_symbol_address);
          }
          return true;
        }
        if (!source || !destination || !IsImageResource(source->kind) ||
            !IsImageResource(destination->kind) ||
            !PlumeGuestImageReady(*source) ||
            !PlumeGuestImageReady(*destination) ||
            FableTrackedResourceIsBlankUploadedSampleProxy(*source) ||
            DirectCommandBindsResourceAsTargetLocked(command, *source) ||
            DirectCommandBindsResourceAsTargetLocked(command, *destination) ||
            PlumeGuestTextureWidth(*source) !=
                PlumeGuestTextureWidth(*destination) ||
            PlumeGuestTextureHeight(*source) !=
                PlumeGuestTextureHeight(*destination)) {
          continue;
        }

        const bool depth_edge =
            link.kind == FablePrivateTextureCopyKind::kDepth ||
            source->texture_depth_target || destination->texture_depth_target;
        const bool color_edge =
            DirectPrivateTextureCopyKindUsesColorBacking(link.kind) &&
            !source->texture_depth_target && !destination->texture_depth_target;
        if (!depth_edge && !color_edge) {
          continue;
        }

        static uint32_t logged_versioned_exact_copy_edges = 0;
        if (logged_versioned_exact_copy_edges < 48u) {
          REXLOG_INFO(
              "Refii plume exact versioned surface-link copy edge #{} "
              "frame={} slot={} texture=0x{:08X} "
              "surface=0x{:08X}/{}x{} destination=0x{:08X}/{}x{} "
              "kind={} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
              "ps=0x{:08X} link_call={} link_context=0x{:016X} "
              "link_symbol=0x{:08X}",
              ++logged_versioned_exact_copy_edges,
              g_direct_renderer.current_frame_index, slot, link.texture,
              source->guest_address, PlumeGuestTextureWidth(*source),
              PlumeGuestTextureHeight(*source), destination->guest_address,
              PlumeGuestTextureWidth(*destination),
              PlumeGuestTextureHeight(*destination),
              FablePrivateTextureCopyKindName(link.kind),
              command.resources.render_targets[0],
              command.resources.depth_stencil,
              command.resources.vertex_shader,
              command.resources.pixel_shader,
              static_cast<unsigned long long>(link.copy_call_serial),
              static_cast<unsigned long long>(link.copy_context_key),
              link.copy_symbol_address);
        }
        return true;
      }
      return false;
    };
    if (link.trusted_destination_edge &&
        DirectSurfaceTextureLinkHasExactCopyIdentity(link) &&
        link.kind == FablePrivateTextureCopyKind::kDepth &&
        link.copy_symbol_address == kFablePrivateRectNativeDepthWrapperSymbol &&
        FablePrivateTextureProxyRoleForAddress(link.texture) ==
            FablePrivateTextureProxyRole::kDepth &&
        link.source_surface != 0) {
      if (PlumeTrackedResource* source = valid_sample(link.source_surface)) {
        static uint32_t logged_versioned_native_depth_source_samples = 0;
        if (logged_versioned_native_depth_source_samples < 48u) {
          REXLOG_INFO(
              "Refii plume exact depth source-surface texture identity #{} "
              "frame={} serial={} draw={} texture=0x{:08X} "
              "surface=0x{:08X}/{}x{} base=0x{:08X} fmt={} gen={} "
              "content={} target_serial={} rt=0x{:08X} ds=0x{:08X} "
              "vs=0x{:08X} ps=0x{:08X} "
              "source={} link_slot={} link_frame={} link_draw={} "
              "link_call={} link_context=0x{:016X} link_symbol=0x{:08X} "
              "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
              ++logged_versioned_native_depth_source_samples,
              g_direct_renderer.current_frame_index,
              static_cast<unsigned long long>(command.serial),
              static_cast<unsigned long long>(command.draw_serial),
              link.texture,
              source->guest_address, PlumeGuestTextureWidth(*source),
              PlumeGuestTextureHeight(*source),
              PlumeGuestTextureBaseAddress(*source),
              static_cast<uint32_t>(PlumeGuestTextureFormat(*source)),
              static_cast<unsigned long long>(source->upload_generation),
              static_cast<unsigned long long>(
                  FableTrackedResourceVisibleContentSerial(*source)),
              static_cast<unsigned long long>(link.target_content_serial),
              command.resources.render_targets[0], command.resources.depth_stencil,
              command.resources.vertex_shader, command.resources.pixel_shader,
              static_cast<uint32_t>(command.draw.source), link.source_slot,
              link.frame_index, static_cast<unsigned long long>(link.draw_serial),
              static_cast<unsigned long long>(link.copy_call_serial),
              static_cast<unsigned long long>(link.copy_context_key),
              link.copy_symbol_address, link.texture_fetch_dwords[0],
              link.texture_fetch_dwords[1], link.texture_fetch_dwords[2],
              link.texture_fetch_dwords[3], link.texture_fetch_dwords[4],
              link.texture_fetch_dwords[5]);
        }
        return source;
      }
    }
	    if (retired_link_resolves_to_exact_copy_edge()) {
	      return nullptr;
	    }

	    if (g_direct_renderer.duplicate_exact_private_rect_surface_link_keys
	            .find(DirectCoveredDuplicatePrivateRectSurfaceLinkKey(link)) !=
	        g_direct_renderer.duplicate_exact_private_rect_surface_link_keys
	            .end()) {
	      return nullptr;
	    }

    const PlumeTrackedResource* retired_source =
        link.source_surface != 0 ? FindDirectResourceLocked(link.source_surface)
                                 : nullptr;
    const PlumeTrackedResource* retired_backing =
        link.backing_texture != 0 ? FindDirectResourceLocked(link.backing_texture)
                                  : nullptr;
    const bool retired_link_has_no_exact_payload =
        link.target_content_serial == 0 && link.backing_content_serial == 0 &&
        link.backing_texture == 0 &&
        std::all_of(link.wrapper_setup_globals.begin(),
                    link.wrapper_setup_globals.end(),
                    [](uint32_t value) { return value == 0; });
    const bool retired_link_has_produced_content =
        link.target_content_serial != 0 || link.backing_content_serial != 0 ||
        (retired_source && FableTrackedResourceHasGpuProducedContent(*retired_source)) ||
        (retired_backing && FableTrackedResourceHasGpuProducedContent(*retired_backing));
    if (retired_link_has_no_exact_payload || !retired_link_has_produced_content) {
      return nullptr;
    }

	    uint64_t retired_log_key = 0xCBF29CE484222325ull;
	    auto mix_retired_log_key = [&](uint64_t value) {
	      retired_log_key ^= value;
      retired_log_key *= 0x100000001B3ull;
    };
    mix_retired_log_key(link.texture);
    mix_retired_log_key(link.source_surface);
    mix_retired_log_key(link.backing_texture);
    mix_retired_log_key(link.copy_symbol_address);
    for (uint32_t value : link.wrapper_setup_globals) {
      mix_retired_log_key(value);
    }
    const bool first_retired_log_for_key =
        g_direct_renderer.retired_stale_surface_link_log_keys
            .insert(retired_log_key)
            .second;
    static uint32_t logged_retired_exact_surface_links = 0;
    if (first_retired_log_for_key &&
        logged_retired_exact_surface_links < 48u &&
        DirectSurfaceTextureLinkTouchesFablePrivateChain(link, nullptr)) {
      REXLOG_INFO(
          "Refii plume retired stale exact surface-link before sample #{} "
          "frame={} texture=0x{:08X} surface=0x{:08X} backing=0x{:08X} "
          "target_serial={} backing_serial={} rt=0x{:08X} ds=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X} source={} link_slot={} "
          "link_frame={} link_draw={} link_call={} "
          "link_context=0x{:016X} link_symbol=0x{:08X} "
          "link_vs=0x{:08X} link_ps=0x{:08X} "
          "link_vs_root=0x{:08X}/{} link_ps_root=0x{:08X}/{} "
          "cmd_vs_root=0x{:08X}/{} cmd_ps_root=0x{:08X}/{} "
          "setup={:08X}/{:08X}/{:08X}/{:08X}",
          ++logged_retired_exact_surface_links,
          g_direct_renderer.current_frame_index, link.texture,
          link.source_surface, link.backing_texture,
          static_cast<unsigned long long>(link.target_content_serial),
          static_cast<unsigned long long>(link.backing_content_serial),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader,
          static_cast<uint32_t>(command.draw.source), link.source_slot,
          link.frame_index, static_cast<unsigned long long>(link.draw_serial),
          static_cast<unsigned long long>(link.copy_call_serial),
          static_cast<unsigned long long>(link.copy_context_key),
          link.copy_symbol_address, link.vertex_shader, link.pixel_shader,
          link.vertex_shader_root_symbol_address,
          static_cast<unsigned long long>(
              link.vertex_shader_root_call_serial),
          link.pixel_shader_root_symbol_address,
          static_cast<unsigned long long>(link.pixel_shader_root_call_serial),
          command.resources.shader_roots.vertex_shader.symbol_address,
          static_cast<unsigned long long>(
              command.resources.shader_roots.vertex_shader.call_serial),
          command.resources.shader_roots.pixel_shader.symbol_address,
          static_cast<unsigned long long>(
              command.resources.shader_roots.pixel_shader.call_serial),
          link.wrapper_setup_globals[0], link.wrapper_setup_globals[1],
          link.wrapper_setup_globals[2], link.wrapper_setup_globals[3]);
    }
    return nullptr;
  }

  if (link.trusted_destination_edge) {
    const uint32_t texture_slot_mask =
        DirectCommandPixelTextureSlotMaskLocked(command);
    const uint64_t link_texture_fetch_key =
        FableDirectTextureFetchKey(link.texture_fetch_dwords);
    auto link_matches_command_slot_texture =
        [&](const FableSurfaceTextureLink& candidate, uint32_t slot) {
      constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
      if (slot >= command.resources.textures.size() ||
          (candidate.source_slot != kUnknownSlot && candidate.source_slot != slot)) {
        return false;
      }
      const uint32_t guest = command.resources.textures[slot];
      if (guest == 0) {
        return false;
      }
      if (DirectResourceAddressMatchesLocked(candidate.texture, guest)) {
        return true;
      }
      const std::array<uint32_t, 6> command_fetch =
          DirectCommandTextureFetchDwords(command, slot);
      const DirectTextureFetchInfo command_fetch_info =
          DirectTextureFetchInfoFromDwords(command_fetch);
      if (command_fetch_info.valid && candidate.texture_base_address != 0 &&
          command_fetch_info.base_address == candidate.texture_base_address) {
        return true;
      }
      const uint64_t command_fetch_key =
          FableDirectTextureFetchKey(command_fetch);
      if (command_fetch_key != 0 && link_texture_fetch_key != 0 &&
          command_fetch_key == link_texture_fetch_key) {
        return true;
      }
      if (const PlumeTrackedResource* texture = FindDirectResourceLocked(guest)) {
        if (DirectResourceAddressMatchesLocked(candidate.texture,
                                               texture->guest_address) ||
            DirectResourceAddressMatchesLocked(candidate.texture,
                                               texture->parent_address)) {
          return true;
        }
        if (candidate.texture_base_address != 0 &&
            PlumeGuestTextureBaseAddress(*texture) ==
                candidate.texture_base_address) {
          return true;
        }
        const uint64_t tracked_fetch_key = FableDirectTextureFetchKey(
            PlumeGuestTextureFetchDwords(*texture));
        if (tracked_fetch_key != 0 && link_texture_fetch_key != 0 &&
            tracked_fetch_key == link_texture_fetch_key) {
          return true;
        }
      }
      if (const FableTextureIdentity* identity =
              FableDirectFindTextureIdentityByObject(guest)) {
        if (DirectResourceAddressMatchesLocked(candidate.texture,
                                               identity->object_texture) ||
            DirectResourceAddressMatchesLocked(candidate.texture,
                                               identity->parent_texture) ||
            (identity->base_address != 0 &&
             identity->base_address == candidate.texture_base_address)) {
          return true;
        }
        const uint64_t identity_fetch_key =
            FableDirectTextureFetchKey(identity->fetch_dwords);
        if (identity_fetch_key != 0 && link_texture_fetch_key != 0 &&
            identity_fetch_key == link_texture_fetch_key) {
          return true;
        }
      }
      return false;
    };
    auto link_matches_command_texture_identity =
        [&](const FableSurfaceTextureLink& candidate) {
      for (uint32_t slot = 0; slot < command.resources.textures.size();
           ++slot) {
        if (!DirectCommandUsesTextureSlotForSyncLocked(command, texture_slot_mask,
                                                       slot)) {
          continue;
        }
        if (link_matches_command_slot_texture(candidate, slot)) {
          return true;
        }
      }
      return false;
    };
    auto link_source_surface_matches_command_texture = [&]() {
      if (link.source_surface == 0) {
        return false;
      }
      for (uint32_t slot = 0; slot < command.resources.textures.size();
           ++slot) {
        if (!DirectCommandUsesTextureSlotForSyncLocked(command,
                                                       texture_slot_mask,
                                                       slot)) {
          continue;
        }
        const uint32_t guest = command.resources.textures[slot];
        if (DirectResourceAddressMatchesLocked(link.source_surface, guest)) {
          return true;
        }
        if (const PlumeTrackedResource* slot_texture =
                FindDirectResourceLocked(guest)) {
          if (DirectResourceAddressMatchesLocked(
                  link.source_surface, slot_texture->guest_address) ||
              DirectResourceAddressMatchesLocked(
                  link.source_surface, slot_texture->parent_address)) {
            return true;
          }
        }
      }
      return false;
    };

    auto link_texture_identity_matches = [&](const FableSurfaceTextureLink& a,
                                             const FableSurfaceTextureLink& b) {
      if (DirectResourceAddressMatchesLocked(a.texture, b.texture)) {
        return true;
      }
      if (a.texture_base_address != 0 && b.texture_base_address != 0 &&
          a.texture_base_address == b.texture_base_address) {
        return true;
      }
      const uint64_t a_fetch_key =
          FableDirectTextureFetchKey(a.texture_fetch_dwords);
      const uint64_t b_fetch_key =
          FableDirectTextureFetchKey(b.texture_fetch_dwords);
      if (a_fetch_key != 0 && b_fetch_key != 0 &&
          a_fetch_key == b_fetch_key) {
        return true;
      }
      auto identity_matches = [&](const FableTextureIdentity* identity,
                                  const FableSurfaceTextureLink& candidate) {
        if (!identity) {
          return false;
        }
        return DirectResourceAddressMatchesLocked(candidate.texture,
                                                  identity->object_texture) ||
               DirectResourceAddressMatchesLocked(candidate.texture,
                                                  identity->parent_texture) ||
               (identity->base_address != 0 &&
                candidate.texture_base_address == identity->base_address) ||
               (FableDirectTextureFetchKey(identity->fetch_dwords) != 0 &&
                FableDirectTextureFetchKey(identity->fetch_dwords) ==
                    FableDirectTextureFetchKey(candidate.texture_fetch_dwords));
      };
      return identity_matches(FableDirectFindTextureIdentityByObject(a.texture),
                              b) ||
             identity_matches(FableDirectFindTextureIdentityByObject(b.texture),
                              a) ||
             identity_matches(FableDirectFindTextureIdentityByBase(
                                  a.texture_base_address),
                              b) ||
             identity_matches(FableDirectFindTextureIdentityByBase(
                                  b.texture_base_address),
                              a);
    };

    auto has_newer_visible_exact_texture_edge = [&]() {
      if (!link.trusted_destination_edge) {
        return false;
      }
      auto candidate_has_authoritative_sample_identity =
          [](const FableSurfaceTextureLink& candidate) {
        return candidate.target_content_serial != 0 ||
               candidate.backing_content_serial != 0 ||
               candidate.backing_texture != 0 ||
               candidate.backing_base_address != 0 ||
               HasTrackedFetchDwords(candidate.backing_fetch_dwords) ||
               candidate.sampled_source.kind !=
                   FableRegisteredSampleSourceKind::kUnknown;
      };
      auto candidate_is_newer = [&](const FableSurfaceTextureLink& candidate) {
        if (!candidate.trusted_destination_edge ||
            !DirectSurfaceTextureLinkHasExactCopyIdentity(candidate) ||
            !candidate_has_authoritative_sample_identity(candidate) ||
            !link_visible_to_command(candidate) ||
            !link_texture_identity_matches(link, candidate) ||
            !link_matches_command_texture_identity(candidate)) {
          return false;
        }
        if (candidate.target_content_serial > link.target_content_serial) {
          return true;
        }
        if (candidate.frame_index > link.frame_index) {
          return true;
        }
        return candidate.frame_index == link.frame_index &&
               candidate.draw_serial > link.draw_serial;
      };
      auto consider_links =
          [&](const std::vector<FableSurfaceTextureLink>* links) {
        if (!links) {
          return false;
        }
        for (const FableSurfaceTextureLink& candidate : *links) {
          if (&candidate != &link && candidate_is_newer(candidate)) {
            return true;
          }
        }
        return false;
      };
      return consider_links(
                 FableDirectFindSurfaceTextureLinksForDestinationTexture(
                     link.texture)) ||
             consider_links(FableDirectFindSurfaceTextureLinksByTexture(
                 link.texture)) ||
             consider_links(
                 FableDirectFindSurfaceTextureLinksByBase(
                     link.texture_base_address)) ||
             consider_links(FableDirectFindSurfaceTextureLinksByFetch(
                 link.texture_fetch_dwords));
    };

    if (command.draw.source != RendererDrawEventSource::kPixVideo &&
        DirectSurfaceTextureLinkIsPrivateTitleWrapperRoot(link) &&
        !DirectSurfaceTextureLinkPrivateWrapperVisibleToCommandLocked(command,
                                                                      link)) {
      return nullptr;
    }

    auto resolve_exact_link_source_action_sample =
        [&](const FableSurfaceTextureLink& source_link,
            bool register_proxy_action) -> PlumeTrackedResource* {
      if (command.draw.source == RendererDrawEventSource::kPixVideo ||
          !source_link.trusted_destination_edge ||
          !DirectSurfaceTextureLinkHasExactCopyIdentity(source_link) ||
          !DirectPrivateTextureCopyKindUsesColorBacking(source_link.kind) ||
          source_link.source_surface == 0) {
        return nullptr;
      }

      uint32_t selected_slot = 0xFFFFFFFFu;
      if (source_link.source_slot < command.resources.textures.size() &&
          link_matches_command_slot_texture(source_link,
                                            source_link.source_slot)) {
        selected_slot = source_link.source_slot;
      } else {
        for (uint32_t slot = 0; slot < command.resources.textures.size();
             ++slot) {
          if (link_matches_command_slot_texture(source_link, slot)) {
            selected_slot = slot;
            break;
          }
        }
      }
      if (selected_slot == 0xFFFFFFFFu) {
        return nullptr;
      }

      const uint32_t command_guest = command.resources.textures[selected_slot];
      if (DirectResourceAddressMatchesLocked(source_link.source_surface,
                                             command_guest)) {
        return nullptr;
      }

      const std::vector<FablePrivateTextureCopyAction>* source_actions =
          FableDirectFindPrivateTextureCopyActionsBySource(
              source_link.source_surface);
      if (!source_actions || source_actions->empty()) {
        return nullptr;
      }

      auto visible_action = [&](const FablePrivateTextureCopyAction& action) {
        if (action.frame_index != 0 &&
            action.frame_index > g_direct_renderer.current_frame_index) {
          return false;
        }
        if (action.frame_index != 0 &&
            action.frame_index == g_direct_renderer.current_frame_index &&
            action.draw_serial != 0 && command_draw_serial != 0 &&
            action.draw_serial > command_draw_serial) {
          return false;
        }
        return true;
      };

      const FablePrivateTextureCopyAction* best_action = nullptr;
      PlumeTrackedResource* best_sample = nullptr;
      uint64_t best_score = 0;
      for (auto action_it = source_actions->rbegin();
           action_it != source_actions->rend(); ++action_it) {
        const FablePrivateTextureCopyAction& action = *action_it;
        if (!visible_action(action) ||
            action.kind == FablePrivateTextureCopyKind::kDepth ||
            !DirectPrivateTextureCopyKindUsesColorBacking(action.kind) ||
            action.target_surface == 0) {
          continue;
        }
        PlumeTrackedResource* sample =
            valid_sample_resource(FindDirectResourceLocked(
                action.target_surface));
        if (!sample) {
          continue;
        }
        const uint64_t sample_content_serial =
            FableTrackedResourceVisibleContentSerial(*sample);
        if (sample_content_serial == 0 ||
            (action.target_content_serial != 0 &&
             sample_content_serial < action.target_content_serial)) {
          continue;
        }

        uint64_t score =
            sample_content_serial + action.draw_serial +
            static_cast<uint64_t>(action.frame_index) * 1000000ull;
        if (action.source_texture == source_link.source_surface) {
          score += 1000000000000ull;
        }
        if (action.target_width == PlumeGuestTextureWidth(*sample) &&
            action.target_height == PlumeGuestTextureHeight(*sample)) {
          score += 1000000000ull;
        }
        if (!best_action || score > best_score) {
          best_action = &action;
          best_sample = sample;
          best_score = score;
        }
      }

      if (!best_action || !best_sample) {
        return nullptr;
      }

      if (!register_proxy_action) {
        return best_sample;
      }

      const std::array<uint32_t, 6> command_fetch =
          DirectCommandTextureFetchDwords(command, selected_slot);
      const DirectTextureFetchInfo command_fetch_info =
          DirectTextureFetchInfoFromDwords(command_fetch);
      FablePrivateTextureCopyAction proxy_action = *best_action;
      proxy_action.source_slot = selected_slot;
      proxy_action.source_texture = command_guest;
      proxy_action.source_base_address =
          command_fetch_info.valid ? command_fetch_info.base_address : 0u;
      proxy_action.source_width =
          command_fetch_info.valid ? command_fetch_info.width : 0u;
      proxy_action.source_height =
          command_fetch_info.valid ? command_fetch_info.height : 0u;
      proxy_action.source_format =
          command_fetch_info.valid ? command_fetch_info.format : 0u;
      proxy_action.source_fetch_dwords = command_fetch;
      proxy_action.backing_texture = source_link.texture;
      proxy_action.backing_base_address = source_link.texture_base_address;
      proxy_action.backing_width = source_link.texture_width;
      proxy_action.backing_height = source_link.texture_height;
      proxy_action.backing_format = source_link.texture_format;
      proxy_action.backing_fetch_dwords = source_link.texture_fetch_dwords;
      proxy_action.backing_content_serial = source_link.target_content_serial;
      proxy_action.target_surface = best_sample->guest_address;
      proxy_action.target_width = PlumeGuestTextureWidth(*best_sample);
      proxy_action.target_height = PlumeGuestTextureHeight(*best_sample);
      proxy_action.target_format = PlumeGuestTextureGuestFormat(*best_sample);
      proxy_action.target_content_serial =
          FableTrackedResourceVisibleContentSerial(*best_sample);
      proxy_action.render_target = command.resources.render_targets[0];
      proxy_action.depth_stencil = command.resources.depth_stencil;
      proxy_action.vertex_shader = command.resources.vertex_shader;
      proxy_action.pixel_shader = command.resources.pixel_shader;
      PopulateDirectPrivateTextureCopyShaderRoots(proxy_action, command);
      proxy_action.wrapper_call_serial = source_link.copy_call_serial;
      proxy_action.wrapper_context_key = source_link.copy_context_key;
      proxy_action.wrapper_symbol_address = source_link.copy_symbol_address;
      proxy_action.wrapper_source_texture = command_guest;
      proxy_action.wrapper_source_exact = command_guest != 0;
      proxy_action.frame_index = g_direct_renderer.current_frame_index;
      proxy_action.draw_serial = command_draw_serial;
      proxy_action.kind = source_link.kind;
      PopulateDirectPrivateTextureCopyRectangleFromCommand(proxy_action,
                                                           command);
      proxy_action.sampled_source = {};
      proxy_action.sampled_source.kind =
          FableRegisteredSampleSourceKind::kGpuSurface;
      proxy_action.sampled_source.resource = best_sample->guest_address;
      proxy_action.sampled_source.content_serial =
          proxy_action.target_content_serial;

      RegisterDirectPrivateTextureCopyActionLocked(proxy_action);
      RememberDirectPrivateRectExactActionLocked(proxy_action);
      RegisterDirectSurfaceTextureLinkForPrivateCopyActionLocked(
          proxy_action, "exact-surface-link-source-action");
      TrackDirectExactSurfaceAliasForPrivateCopyActionLocked(
          proxy_action, "exact-surface-link-source-action");
      RememberDirectMutablePrivateHeaderExactSampleLocked(
          command, selected_slot, proxy_action, *best_sample,
          "exact-surface-link-source-action");

      static uint32_t logged_exact_surface_link_source_action_samples = 0;
      if (RendererVerboseTracingEnabled() &&
          logged_exact_surface_link_source_action_samples < 32u) {
        REXLOG_INFO(
            "Refii plume exact surface-link source-action sample #{} "
            "frame={} slot={} guest=0x{:08X} link_tex=0x{:08X} "
            "link_source=0x{:08X} action_source=0x{:08X} "
            "sample=0x{:08X}/{}x{} call={} context=0x{:016X} "
            "symbol=0x{:08X} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
            "ps=0x{:08X}",
            ++logged_exact_surface_link_source_action_samples,
            g_direct_renderer.current_frame_index, selected_slot,
            command_guest, source_link.texture, source_link.source_surface,
            best_action->source_texture, best_sample->guest_address,
            PlumeGuestTextureWidth(*best_sample),
            PlumeGuestTextureHeight(*best_sample),
            static_cast<unsigned long long>(source_link.copy_call_serial),
            static_cast<unsigned long long>(source_link.copy_context_key),
            source_link.copy_symbol_address, command.resources.render_targets[0],
            command.resources.depth_stencil, command.resources.vertex_shader,
            command.resources.pixel_shader);
      }
      return best_sample;
    };

    auto valid_exact_link_sample =
        [&](uint32_t address, uint32_t expected_base,
            const std::array<uint32_t, 6>& expected_fetch_dwords,
            uint64_t expected_content_serial) -> PlumeTrackedResource* {
      PlumeTrackedResource* resource =
          address != 0 ? FindDirectResourceLocked(address) : nullptr;
      if (!resource || !IsImageResource(resource->kind) ||
          !PlumeGuestImageReady(*resource) ||
          !FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(*resource) ||
          DirectResourceIsPrivateFeedbackAlias(*resource) ||
          FableTrackedResourceIsBlankUploadedSampleProxy(*resource)) {
        return nullptr;
      }
      if (expected_base != 0 &&
          PlumeGuestTextureBaseAddress(*resource) != expected_base) {
        return nullptr;
      }
      const uint64_t expected_fetch_key =
          FableDirectTextureFetchKey(expected_fetch_dwords);
      if (expected_fetch_key != 0 &&
          FableDirectTextureFetchKey(PlumeGuestTextureFetchDwords(*resource)) !=
              expected_fetch_key) {
        return nullptr;
      }
      const uint64_t content_serial =
          FableTrackedResourceVisibleContentSerial(*resource);
      if (expected_content_serial != 0 &&
          content_serial < expected_content_serial) {
        return nullptr;
      }
      if (link.target_content_serial != 0 &&
          content_serial < link.target_content_serial) {
        return nullptr;
      }
      auto resource_uses_live_target_storage =
          [&](const PlumeTrackedResource& sample) {
        const plume::RenderTexture* sample_texture =
            DirectResourceNativeTexture(sample);
        auto target_uses_sample_storage = [&](uint32_t target_address) {
          if (target_address == 0) {
            return false;
          }
          if (target_address == sample.guest_address) {
            return true;
          }
          const PlumeTrackedResource* target =
              FindDirectResourceLocked(target_address);
          return target && sample_texture &&
                 DirectResourceNativeTexture(*target) == sample_texture;
        };
        for (uint32_t target_address : command.resources.render_targets) {
          if (target_uses_sample_storage(target_address)) {
            return true;
          }
        }
        return target_uses_sample_storage(command.resources.depth_stencil);
      };
      const bool exact_copied_backing_sample =
          DirectSurfaceTextureLinkHasExactCopyIdentity(link) &&
          DirectPrivateTextureCopyKindUsesColorBacking(link.kind) &&
          (DirectResourceAddressMatchesLocked(resource->guest_address,
                                              link.backing_texture) ||
           DirectResourceAddressMatchesLocked(resource->guest_address,
                                              link.sampled_source.resource)) &&
          !resource_uses_live_target_storage(*resource);
      if (DirectCommandBindsResourceAsTargetLocked(command, *resource) &&
          !exact_copied_backing_sample) {
        return nullptr;
      }
      if (!DirectPrivateFeedbackAliasContainsTargetSerialLocked(
              *resource, link.target_content_serial) &&
          !exact_copied_backing_sample) {
        return nullptr;
      }
      return resource;
    };

    auto valid_exact_link_source_surface =
        [&](uint32_t address) -> PlumeTrackedResource* {
      PlumeTrackedResource* resource = valid_sample(address);
      if (!resource) {
        return nullptr;
      }
      const uint64_t content_serial =
          FableTrackedResourceVisibleContentSerial(*resource);
      if (link.target_content_serial != 0 &&
          content_serial < link.target_content_serial) {
        const bool allocation_serial_target =
            content_serial != 0 && link.target_content_serial == resource->serial;
        const bool exact_gpu_surface_source =
            allocation_serial_target && link.trusted_destination_edge &&
            link.source_surface == address &&
            FableTrackedResourceHasGpuProducedContent(*resource);
        if (!exact_gpu_surface_source) {
          return nullptr;
        }
      }
      return resource;
    };

    if (PlumeTrackedResource* backing =
            valid_exact_link_sample(link.backing_texture,
                                    link.backing_base_address,
                                    link.backing_fetch_dwords,
                                    link.backing_content_serial)) {
      static uint32_t logged_exact_link_backing_samples = 0;
      if (RendererVerboseTracingEnabled() &&
          logged_exact_link_backing_samples < 24u &&
          DirectSurfaceTextureLinkTouchesFablePrivateChain(link, backing)) {
        REXLOG_INFO(
            "Refii plume exact surface-link backing sample #{} frame={} "
            "texture=0x{:08X} surface=0x{:08X} backing=0x{:08X} "
            "sample=0x{:08X}/{}x{} target_serial={} backing_serial={} "
            "rt=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_link_backing_samples,
            g_direct_renderer.current_frame_index, link.texture,
            link.source_surface, link.backing_texture, backing->guest_address,
            PlumeGuestTextureWidth(*backing), PlumeGuestTextureHeight(*backing),
            static_cast<unsigned long long>(link.target_content_serial),
            static_cast<unsigned long long>(link.backing_content_serial),
            command.resources.render_targets[0], command.resources.vertex_shader,
            command.resources.pixel_shader);
      }
      return backing;
    }

    const bool exact_edge_can_sample_source_surface =
        command.draw.source == RendererDrawEventSource::kPixVideo ||
        DirectCommandIsExactTitleBillboardAtlasLocked(command) ||
        !link.trusted_destination_edge ||
        !DirectSurfaceTextureLinkHasExactCopyIdentity(link) ||
        DirectResourceAddressMatchesLocked(link.source_surface, link.texture) ||
        link_source_surface_matches_command_texture();
	    if (exact_edge_can_sample_source_surface) {
	      if (PlumeTrackedResource* source =
	              valid_exact_link_source_surface(link.source_surface)) {
      static uint32_t logged_exact_link_source_samples = 0;
      if (RendererVerboseTracingEnabled() &&
          logged_exact_link_source_samples < 24u &&
          DirectSurfaceTextureLinkTouchesFablePrivateChain(link, source)) {
        REXLOG_INFO(
            "Refii plume exact surface-link source sample #{} frame={} "
            "texture=0x{:08X} surface=0x{:08X} backing=0x{:08X} "
            "sample=0x{:08X}/{}x{} target_serial={} backing_serial={} "
            "rt=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_link_source_samples,
            g_direct_renderer.current_frame_index, link.texture,
            link.source_surface, link.backing_texture, source->guest_address,
            PlumeGuestTextureWidth(*source), PlumeGuestTextureHeight(*source),
            static_cast<unsigned long long>(link.target_content_serial),
            static_cast<unsigned long long>(link.backing_content_serial),
            command.resources.render_targets[0], command.resources.vertex_shader,
            command.resources.pixel_shader);
      }
	      return remember_surface_link_sample(source);
	      }
	    }

	    if (command.draw.source != RendererDrawEventSource::kPixVideo &&
	        !link_matches_command_texture_identity(link)) {
	      auto exact_wrapper_identity_matches =
	          [&](const FableSurfaceTextureLink& candidate) {
	        bool compared = false;
	        if (link.copy_call_serial != 0 && candidate.copy_call_serial != 0) {
	          compared = true;
	          if (link.copy_call_serial != candidate.copy_call_serial) {
	            return false;
	          }
	        }
	        if (link.copy_context_key != 0 && candidate.copy_context_key != 0) {
	          compared = true;
	          if (link.copy_context_key != candidate.copy_context_key) {
	            return false;
	          }
	        }
	        if (link.copy_symbol_address != 0 &&
	            candidate.copy_symbol_address != 0) {
	          compared = true;
	          if (!FablePrivateRectWrapperSymbolsCompatible(
	                  link.copy_symbol_address, candidate.copy_symbol_address)) {
	            return false;
	          }
	        }
	        return compared;
	      };
	      auto exact_link_owned_by_command_sibling_source_action = [&]() {
	        if (!link.trusted_destination_edge ||
	            !DirectSurfaceTextureLinkHasExactCopyIdentity(link) ||
	            !DirectPrivateTextureCopyKindUsesColorBacking(link.kind) ||
	            link.source_surface == 0) {
	          return false;
	        }
	        const std::vector<FableSurfaceTextureLink>* sibling_links =
	            FableDirectFindSurfaceTextureLinksBySurface(link.source_surface);
	        if (!sibling_links) {
	          return false;
	        }
	        for (const FableSurfaceTextureLink& sibling : *sibling_links) {
	          if (&sibling == &link || sibling.source_surface != link.source_surface ||
	              !sibling.trusted_destination_edge ||
	              !DirectSurfaceTextureLinkHasExactCopyIdentity(sibling) ||
	              !DirectPrivateTextureCopyKindUsesColorBacking(sibling.kind) ||
	              !link_visible_to_command(sibling) ||
	              !link_matches_command_texture_identity(sibling) ||
	              !exact_wrapper_identity_matches(sibling)) {
	            continue;
	          }
	          PlumeTrackedResource* sibling_sample =
	              resolve_exact_link_source_action_sample(sibling, false);
	          if (!sibling_sample) {
	            continue;
	          }
	          if (DirectSurfaceTextureLinkTouchesFablePrivateChain(
	                  link, sibling_sample)) {
	            size_t owner_seed = 0;
	            DirectHashCombine(owner_seed, std::hash<uint32_t>{}(0x51B11A9u));
	            DirectHashCombine(owner_seed, std::hash<uint32_t>{}(
	                                        g_direct_renderer.current_frame_index));
	            DirectHashCombine(owner_seed, std::hash<uint64_t>{}(command.serial));
	            DirectHashCombine(owner_seed, std::hash<uint64_t>{}(command.draw_serial));
	            DirectHashCombine(owner_seed, std::hash<uint64_t>{}(link.serial));
	            DirectHashCombine(owner_seed, std::hash<uint32_t>{}(link.texture));
	            DirectHashCombine(owner_seed,
	                        std::hash<uint32_t>{}(link.source_surface));
	            DirectHashCombine(owner_seed, std::hash<uint64_t>{}(sibling.serial));
	            DirectHashCombine(owner_seed, std::hash<uint32_t>{}(sibling.texture));
	            DirectHashCombine(owner_seed,
	                        std::hash<uint32_t>{}(sibling_sample->guest_address));
	            DirectHashCombine(owner_seed,
	                        std::hash<uint64_t>{}(link.copy_call_serial));
	            DirectHashCombine(owner_seed,
	                        std::hash<uint64_t>{}(link.copy_context_key));
	            DirectHashCombine(owner_seed,
	                        std::hash<uint32_t>{}(link.copy_symbol_address));
	            DirectHashCombine(owner_seed, std::hash<uint32_t>{}(
	                                        command.resources.render_targets[0]));
	            DirectHashCombine(owner_seed, std::hash<uint32_t>{}(
	                                        command.resources.depth_stencil));
	            DirectHashCombine(owner_seed, std::hash<uint32_t>{}(
	                                        command.resources.vertex_shader));
	            DirectHashCombine(owner_seed, std::hash<uint32_t>{}(
	                                        command.resources.pixel_shader));
	            const uint64_t owner_key =
	                owner_seed != 0 ? static_cast<uint64_t>(owner_seed) : 1ull;
	            static uint32_t logged_sibling_source_action_owners = 0;
	            if (logged_sibling_source_action_owners < 48u &&
	                g_direct_renderer.sibling_source_action_owner_frame_keys
	                    .insert(owner_key)
	                    .second) {
	              REXLOG_INFO(
	                  "Refii plume exact surface-link owned by sibling "
	                  "source-action #{} frame={} texture=0x{:08X} "
	                  "surface=0x{:08X} sibling=0x{:08X} "
	                  "sample=0x{:08X}/{}x{} call={} context=0x{:016X} "
	                  "symbol=0x{:08X} rt=0x{:08X} ds=0x{:08X} "
	                  "vs=0x{:08X} ps=0x{:08X}",
	                  ++logged_sibling_source_action_owners,
	                  g_direct_renderer.current_frame_index, link.texture,
	                  link.source_surface, sibling.texture,
	                  sibling_sample->guest_address,
	                  PlumeGuestTextureWidth(*sibling_sample),
	                  PlumeGuestTextureHeight(*sibling_sample),
	                  static_cast<unsigned long long>(link.copy_call_serial),
	                  static_cast<unsigned long long>(link.copy_context_key),
	                  link.copy_symbol_address,
	                  command.resources.render_targets[0],
	                  command.resources.depth_stencil,
	                  command.resources.vertex_shader,
	                  command.resources.pixel_shader);
	            }
	          }
	          return true;
	        }
	        return false;
	      };
	      if (exact_link_owned_by_command_sibling_source_action()) {
	        return nullptr;
	      }
	      if (DirectSurfaceTextureLinkTouchesFablePrivateChain(link, nullptr)) {
	        uint32_t command_texture_slot = UINT32_MAX;
	        uint32_t command_texture_guest = 0;
	        uint32_t command_texture_count = 0;
	        for (uint32_t slot = 0; slot < command.resources.textures.size();
	             ++slot) {
	          if (!DirectCommandUsesTextureSlotForSyncLocked(
	                  command, texture_slot_mask, slot)) {
	            continue;
	          }
	          ++command_texture_count;
	          if (command_texture_slot == UINT32_MAX) {
	            command_texture_slot = slot;
	            command_texture_guest = command.resources.textures[slot];
	          }
	        }
	        size_t exclusion_seed = 0;
	        DirectHashCombine(exclusion_seed, std::hash<uint32_t>{}(0xE5C1D17u));
	        DirectHashCombine(exclusion_seed, std::hash<uint32_t>{}(
	                                      g_direct_renderer.current_frame_index));
	        DirectHashCombine(exclusion_seed, std::hash<uint64_t>{}(command.serial));
	        DirectHashCombine(exclusion_seed,
	                    std::hash<uint64_t>{}(command.draw_serial));
	        DirectHashCombine(exclusion_seed, std::hash<uint32_t>{}(link.texture));
	        DirectHashCombine(exclusion_seed,
	                    std::hash<uint32_t>{}(link.source_surface));
	        DirectHashCombine(exclusion_seed,
	                    std::hash<uint32_t>{}(link.backing_texture));
	        DirectHashCombine(exclusion_seed,
	                    std::hash<uint64_t>{}(link.target_content_serial));
	        DirectHashCombine(exclusion_seed,
	                    std::hash<uint64_t>{}(link.backing_content_serial));
	        DirectHashCombine(exclusion_seed,
	                    std::hash<uint64_t>{}(link.copy_call_serial));
	        DirectHashCombine(exclusion_seed,
	                    std::hash<uint64_t>{}(link.copy_context_key));
	        DirectHashCombine(exclusion_seed,
	                    std::hash<uint32_t>{}(link.copy_symbol_address));
	        DirectHashCombine(exclusion_seed, std::hash<uint32_t>{}(
	                                      command.resources.render_targets[0]));
	        DirectHashCombine(exclusion_seed,
	                    std::hash<uint32_t>{}(command.resources.depth_stencil));
	        DirectHashCombine(exclusion_seed,
	                    std::hash<uint32_t>{}(command.resources.vertex_shader));
	        DirectHashCombine(exclusion_seed,
	                    std::hash<uint32_t>{}(command.resources.pixel_shader));
	        DirectHashCombine(exclusion_seed, std::hash<uint32_t>{}(texture_slot_mask));
	        DirectHashCombine(exclusion_seed, std::hash<uint32_t>{}(command_texture_slot));
	        DirectHashCombine(exclusion_seed, std::hash<uint32_t>{}(command_texture_guest));
	        DirectHashCombine(exclusion_seed, std::hash<uint32_t>{}(command_texture_count));
	        const uint64_t exclusion_key =
	            exclusion_seed != 0 ? static_cast<uint64_t>(exclusion_seed) : 1ull;
	        static uint32_t logged_exact_surface_link_command_texture_filters = 0;
	        if (logged_exact_surface_link_command_texture_filters < 48u &&
	            g_direct_renderer.surface_link_command_identity_exclusion_frame_keys
	                .insert(exclusion_key)
	                .second) {
	          REXLOG_INFO(
	              "Refii plume exact surface-link non-command-texture "
	              "exclusion proof #{} reason=not-command-texture frame={} "
	              "texture=0x{:08X} surface=0x{:08X} backing=0x{:08X} "
	              "target_serial={} backing_serial={} rt=0x{:08X} "
	              "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} source={} "
	              "texture_mask=0x{:08X} command_slot={} "
	              "command_texture=0x{:08X} command_textures={} "
	              "link_slot={} link_frame={} link_draw={} link_call={} "
	              "link_context=0x{:016X} link_symbol=0x{:08X} "
	              "setup={:08X}/{:08X}/{:08X}/{:08X}",
	              ++logged_exact_surface_link_command_texture_filters,
	              g_direct_renderer.current_frame_index, link.texture,
	              link.source_surface, link.backing_texture,
	              static_cast<unsigned long long>(link.target_content_serial),
	              static_cast<unsigned long long>(link.backing_content_serial),
	              command.resources.render_targets[0],
	              command.resources.depth_stencil,
	              command.resources.vertex_shader, command.resources.pixel_shader,
	              static_cast<uint32_t>(command.draw.source), texture_slot_mask,
	              command_texture_slot, command_texture_guest,
	              command_texture_count, link.source_slot,
	              link.frame_index,
	              static_cast<unsigned long long>(link.draw_serial),
	              static_cast<unsigned long long>(link.copy_call_serial),
	              static_cast<unsigned long long>(link.copy_context_key),
	              link.copy_symbol_address, link.wrapper_setup_globals[0],
	              link.wrapper_setup_globals[1], link.wrapper_setup_globals[2],
	              link.wrapper_setup_globals[3]);
	        }
	      }
	      return nullptr;
	    }

		    if (command.draw.source != RendererDrawEventSource::kPixVideo &&
		        DirectSurfaceTextureLinkHasExactCopyIdentity(link) &&
	        link.sampled_source.kind != FableRegisteredSampleSourceKind::kUnknown) {
      if (PlumeTrackedResource* registered_source =
              ResolveDirectRegisteredSampleSourceLocked(command,
                                                        link.sampled_source)) {
        static uint32_t logged_exact_link_registered_samples = 0;
        if (RendererVerboseTracingEnabled() &&
            logged_exact_link_registered_samples < 48u &&
            DirectSurfaceTextureLinkTouchesFablePrivateChain(
                link, registered_source)) {
          REXLOG_INFO(
              "Refii plume exact surface-link registered sample #{} "
              "frame={} texture=0x{:08X} surface=0x{:08X} "
              "backing=0x{:08X} sample=0x{:08X}/{}x{} kind={} "
              "target_serial={} backing_serial={} rt=0x{:08X} "
              "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
              ++logged_exact_link_registered_samples,
              g_direct_renderer.current_frame_index, link.texture,
              link.source_surface, link.backing_texture,
              registered_source->guest_address,
              PlumeGuestTextureWidth(*registered_source),
              PlumeGuestTextureHeight(*registered_source),
              FableRegisteredSampleSourceKindName(link.sampled_source.kind),
              static_cast<unsigned long long>(link.target_content_serial),
              static_cast<unsigned long long>(link.backing_content_serial),
              command.resources.render_targets[0],
              command.resources.depth_stencil, command.resources.vertex_shader,
              command.resources.pixel_shader);
        }
        return remember_surface_link_sample(registered_source);
      }
    }

    if (command.draw.source != RendererDrawEventSource::kPixVideo &&
        DirectSurfaceTextureLinkHasExactCopyIdentity(link)) {
      const uint32_t texture_slot_mask =
          DirectCommandPixelTextureSlotMaskLocked(command);
      for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
        if (!DirectCommandUsesTextureSlotForSyncLocked(command,
                                                       texture_slot_mask,
                                                       slot) ||
            !DirectCommandSlotUsesExactTitleUploadSourceLocked(command, slot) ||
            !link_matches_command_slot_texture(link, slot)) {
          continue;
        }
        const uint32_t guest = command.resources.textures[slot];
        PlumeTrackedResource* slot_texture =
            guest != 0 ? FindDirectResourceLocked(guest) : nullptr;
        PlumeTrackedResource* title_sample =
            ResolveDirectExactTitleUploadSourceForCommandSlotLocked(
                command, slot, slot_texture);
        if (!title_sample) {
          title_sample = ResolveDirectTinyTitleUploadedTextureForCommandSlotLocked(
              command, slot, slot_texture);
        }
        if (!title_sample ||
            DirectCommandBindsResourceAsTargetLocked(command, *title_sample)) {
          continue;
        }
        static uint32_t logged_exact_link_title_upload_samples = 0;
        if (RendererVerboseTracingEnabled() &&
            logged_exact_link_title_upload_samples < 48u) {
          REXLOG_INFO(
              "Refii plume exact surface-link title upload sample #{} frame={} "
              "slot={} guest=0x{:08X} texture=0x{:08X} surface=0x{:08X} "
              "backing=0x{:08X} sample=0x{:08X}/{}x{} target_serial={} "
              "backing_serial={} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
              "ps=0x{:08X}",
              ++logged_exact_link_title_upload_samples,
              g_direct_renderer.current_frame_index, slot, guest, link.texture,
              link.source_surface, link.backing_texture,
              title_sample->guest_address, PlumeGuestTextureWidth(*title_sample),
              PlumeGuestTextureHeight(*title_sample),
              static_cast<unsigned long long>(link.target_content_serial),
              static_cast<unsigned long long>(link.backing_content_serial),
              command.resources.render_targets[0], command.resources.depth_stencil,
              command.resources.vertex_shader, command.resources.pixel_shader);
        }
        return title_sample;
      }
    }

    if (PlumeTrackedResource* source_action_sample =
            resolve_exact_link_source_action_sample(link, true)) {
      return source_action_sample;
    }

    if (DirectSurfaceTextureLinkIsUnresolvedSurfaceViewIdentity(link)) {
      return nullptr;
    }

	    if (command.draw.source != RendererDrawEventSource::kPixVideo) {
	      if (has_newer_visible_exact_texture_edge()) {
	        return nullptr;
	      }

      const bool exact_native_depth_requires_target_content =
          link.trusted_destination_edge &&
          DirectSurfaceTextureLinkHasExactCopyIdentity(link) &&
          link.kind == FablePrivateTextureCopyKind::kDepth &&
          link.copy_symbol_address == kFablePrivateRectNativeDepthWrapperSymbol &&
          FablePrivateTextureProxyRoleForAddress(link.texture) ==
              FablePrivateTextureProxyRole::kDepth &&
          link.source_surface != 0 && link.copy_call_serial != 0 &&
          link.copy_context_key != 0;
      if (exact_native_depth_requires_target_content) {
        if (IsFableTitleSceneTextureSyncCommandLocked(command)) {
          return nullptr;
        }
        static uint32_t logged_exact_native_depth_surface_link_content_waits = 0;
        if (logged_exact_native_depth_surface_link_content_waits < 48u) {
          REXLOG_INFO(
              "Refii plume exact native-depth surface-link requires target "
              "content #{} frame={} "
              "texture=0x{:08X} surface=0x{:08X} target_serial={} "
              "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
              "source={} link_slot={} link_frame={} link_draw={} "
              "link_call={} link_context=0x{:016X}",
              ++logged_exact_native_depth_surface_link_content_waits,
              g_direct_renderer.current_frame_index, link.texture,
              link.source_surface,
              static_cast<unsigned long long>(link.target_content_serial),
              command.resources.render_targets[0], command.resources.depth_stencil,
              command.resources.vertex_shader, command.resources.pixel_shader,
              static_cast<uint32_t>(command.draw.source), link.source_slot,
              link.frame_index, static_cast<unsigned long long>(link.draw_serial),
              static_cast<unsigned long long>(link.copy_call_serial),
              static_cast<unsigned long long>(link.copy_context_key));
        }
        return nullptr;
      }

      if (link.frame_index != 0 &&
          link.frame_index > g_direct_renderer.current_frame_index) {
        return nullptr;
      }
      if (link.frame_index != 0 &&
          link.frame_index == g_direct_renderer.current_frame_index &&
          link.draw_serial != 0 && command_draw_serial != 0 &&
          link.draw_serial > command_draw_serial) {
        return nullptr;
      }

      static uint32_t logged_exact_link_sample_pending_proofs = 0;
      static std::unordered_set<uint64_t>
          logged_exact_link_sample_pending_proof_keys;
      if (logged_exact_link_sample_pending_proofs < 48u &&
          DirectSurfaceTextureLinkTouchesFablePrivateChain(link, nullptr)) {
        struct ExactSurfaceLinkSampleCandidateStatus {
          bool present = false;
          bool ready = false;
          bool nonblank_or_gpu = false;
          bool gpu_produced = false;
          bool blank_proxy = false;
          bool feedback_alias = false;
          bool bound_as_target = false;
          bool contains_target = false;
          bool base_match = false;
          bool fetch_match = false;
          uint64_t visible_serial = 0;
        };

        auto candidate_status =
            [&](uint32_t address, uint32_t expected_base,
                const std::array<uint32_t, 6>& expected_fetch_dwords) {
              ExactSurfaceLinkSampleCandidateStatus status = {};
              PlumeTrackedResource* resource =
                  address != 0 ? FindDirectResourceLocked(address) : nullptr;
              if (!resource || !IsImageResource(resource->kind)) {
                return status;
              }
              status.present = true;
              status.ready = PlumeGuestImageReady(*resource);
              status.nonblank_or_gpu =
                  FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(*resource);
              status.gpu_produced = FableTrackedResourceHasGpuProducedContent(*resource);
              status.blank_proxy =
                  FableTrackedResourceIsBlankUploadedSampleProxy(*resource);
              status.feedback_alias = DirectResourceIsPrivateFeedbackAlias(*resource);
              status.bound_as_target =
                  DirectCommandBindsResourceAsTargetLocked(command, *resource);
              status.contains_target =
                  DirectPrivateFeedbackAliasContainsTargetSerialLocked(
                      *resource, link.target_content_serial);
              status.visible_serial = FableTrackedResourceVisibleContentSerial(*resource);
              status.base_match =
                  expected_base == 0 ||
                  PlumeGuestTextureBaseAddress(*resource) == expected_base;
              const uint64_t expected_fetch_key =
                  FableDirectTextureFetchKey(expected_fetch_dwords);
              status.fetch_match =
                  expected_fetch_key == 0 ||
                  FableDirectTextureFetchKey(
                      PlumeGuestTextureFetchDwords(*resource)) ==
                      expected_fetch_key;
              return status;
            };

        const ExactSurfaceLinkSampleCandidateStatus backing_status =
            candidate_status(link.backing_texture, link.backing_base_address,
                             link.backing_fetch_dwords);
        const ExactSurfaceLinkSampleCandidateStatus source_status =
            candidate_status(link.source_surface, 0, {});
        const char* exclusion_owner = "none";
        if (source_status.bound_as_target &&
            source_status.contains_target) {
          exclusion_owner = "source-bound-target";
        } else if (backing_status.bound_as_target &&
                   backing_status.contains_target) {
          exclusion_owner = "backing-bound-target";
        } else if (source_status.gpu_produced &&
                   source_status.contains_target) {
          exclusion_owner = "source-produced-target";
        } else if (backing_status.gpu_produced &&
                   backing_status.contains_target) {
          exclusion_owner = "backing-produced-target";
        } else if (source_status.contains_target) {
          exclusion_owner = "source-tracked-target";
        } else if (backing_status.contains_target) {
          exclusion_owner = "backing-tracked-target";
        }
        if (std::strcmp(exclusion_owner, "none") == 0) {
          return nullptr;
        }

        size_t proof_seed = 0;
        DirectHashCombine(proof_seed, std::hash<uint32_t>{}(0xA17E5A7u));
        DirectHashCombine(proof_seed,
                    std::hash<uint32_t>{}(g_direct_renderer.current_frame_index));
        DirectHashCombine(proof_seed, std::hash<uint64_t>{}(command.serial));
        DirectHashCombine(proof_seed, std::hash<uint64_t>{}(command.draw_serial));
        DirectHashCombine(proof_seed, std::hash<uint32_t>{}(link.texture));
        DirectHashCombine(proof_seed, std::hash<uint32_t>{}(link.source_surface));
        DirectHashCombine(proof_seed, std::hash<uint32_t>{}(link.backing_texture));
        DirectHashCombine(proof_seed,
                    std::hash<uint64_t>{}(link.target_content_serial));
        DirectHashCombine(proof_seed,
                    std::hash<uint64_t>{}(link.backing_content_serial));
        DirectHashCombine(proof_seed, std::hash<uint64_t>{}(link.copy_call_serial));
        DirectHashCombine(proof_seed, std::hash<uint64_t>{}(link.copy_context_key));
        DirectHashCombine(proof_seed, std::hash<uint32_t>{}(link.copy_symbol_address));
        DirectHashCombine(proof_seed,
                    std::hash<uint32_t>{}(command.resources.render_targets[0]));
        DirectHashCombine(proof_seed,
                    std::hash<uint32_t>{}(command.resources.depth_stencil));
        DirectHashCombine(proof_seed,
                    std::hash<uint32_t>{}(command.resources.vertex_shader));
        DirectHashCombine(proof_seed,
                    std::hash<uint32_t>{}(command.resources.pixel_shader));
        DirectHashCombine(proof_seed,
                    std::hash<uint64_t>{}(backing_status.visible_serial));
        DirectHashCombine(proof_seed,
                    std::hash<uint64_t>{}(source_status.visible_serial));
        const uint64_t proof_key =
            proof_seed != 0 ? static_cast<uint64_t>(proof_seed) : 1ull;
        const bool bound_current_target_owner =
            std::strcmp(exclusion_owner, "source-bound-target") == 0 ||
            std::strcmp(exclusion_owner, "backing-bound-target") == 0;
        if (bound_current_target_owner) {
          static uint32_t logged_bound_current_target_surface_link_owners = 0;
          if (logged_bound_current_target_surface_link_owners < 48u &&
              logged_exact_link_sample_pending_proof_keys.insert(proof_key)
                  .second) {
            REXLOG_INFO(
                "Refii plume exact surface-link current-target ownership "
                "proof #{} reason=private-chain-current-target owner={} "
                "frame={} texture=0x{:08X} surface=0x{:08X} "
                "backing=0x{:08X} target_serial={} rt=0x{:08X} "
                "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} source={} "
                "link_slot={} link_frame={} link_draw={} link_call={} "
                "link_context=0x{:016X} link_symbol=0x{:08X} "
                "source_visible={} backing_visible={}",
                ++logged_bound_current_target_surface_link_owners,
                exclusion_owner, g_direct_renderer.current_frame_index,
                link.texture, link.source_surface, link.backing_texture,
                static_cast<unsigned long long>(link.target_content_serial),
                command.resources.render_targets[0],
                command.resources.depth_stencil, command.resources.vertex_shader,
                command.resources.pixel_shader,
                static_cast<uint32_t>(command.draw.source), link.source_slot,
                link.frame_index,
                static_cast<unsigned long long>(link.draw_serial),
                static_cast<unsigned long long>(link.copy_call_serial),
                static_cast<unsigned long long>(link.copy_context_key),
                link.copy_symbol_address,
                static_cast<unsigned long long>(source_status.visible_serial),
                static_cast<unsigned long long>(backing_status.visible_serial));
          }
          return nullptr;
        }
        if (logged_exact_link_sample_pending_proof_keys.insert(proof_key)
                .second) {
          REXLOG_INFO(
              "Refii plume exact surface-link no-produced-sample exclusion "
              "proof #{} reason=no-valid-produced-sample owner={} frame={} "
              "texture=0x{:08X} surface=0x{:08X} backing=0x{:08X} "
              "target_serial={} backing_serial={} rt=0x{:08X} "
              "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} source={} "
              "link_slot={} link_frame={} link_draw={} link_call={} "
              "link_context=0x{:016X} link_symbol=0x{:08X} "
              "setup={:08X}/{:08X}/{:08X}/{:08X} "
              "backing_present={} backing_ready={} backing_nonblank_gpu={} "
              "backing_gpu={} backing_blank={} backing_feedback={} "
              "backing_bound={} backing_contains_target={} "
              "backing_base_match={} backing_fetch_match={} "
              "backing_visible={} source_present={} source_ready={} "
              "source_nonblank_gpu={} source_gpu={} source_blank={} "
              "source_feedback={} source_bound={} source_contains_target={} "
              "source_visible={}",
              ++logged_exact_link_sample_pending_proofs,
              exclusion_owner, g_direct_renderer.current_frame_index, link.texture,
              link.source_surface, link.backing_texture,
              static_cast<unsigned long long>(link.target_content_serial),
              static_cast<unsigned long long>(link.backing_content_serial),
              command.resources.render_targets[0],
              command.resources.depth_stencil, command.resources.vertex_shader,
              command.resources.pixel_shader,
              static_cast<uint32_t>(command.draw.source), link.source_slot,
              link.frame_index,
              static_cast<unsigned long long>(link.draw_serial),
              static_cast<unsigned long long>(link.copy_call_serial),
              static_cast<unsigned long long>(link.copy_context_key),
              link.copy_symbol_address, link.wrapper_setup_globals[0],
              link.wrapper_setup_globals[1], link.wrapper_setup_globals[2],
              link.wrapper_setup_globals[3], backing_status.present,
              backing_status.ready, backing_status.nonblank_or_gpu,
              backing_status.gpu_produced, backing_status.blank_proxy,
              backing_status.feedback_alias, backing_status.bound_as_target,
              backing_status.contains_target, backing_status.base_match,
              backing_status.fetch_match,
              static_cast<unsigned long long>(backing_status.visible_serial),
              source_status.present, source_status.ready,
              source_status.nonblank_or_gpu, source_status.gpu_produced,
              source_status.blank_proxy, source_status.feedback_alias,
              source_status.bound_as_target, source_status.contains_target,
              static_cast<unsigned long long>(source_status.visible_serial));
        }
	      }
      return nullptr;
    }
  }

  if (PlumeTrackedResource* registered_source =
          ResolveDirectRegisteredSampleSourceLocked(command,
                                                    link.sampled_source)) {
    return registered_source;
  }

  if (PlumeTrackedResource* backing_texture =
          valid_sample(link.backing_texture)) {
    if (DirectPrivateFeedbackAliasContainsTargetSerialLocked(
            *backing_texture, link.target_content_serial) &&
        (link.backing_content_serial == 0 ||
         FableTrackedResourceVisibleContentSerial(*backing_texture) >=
             link.backing_content_serial)) {
      return backing_texture;
    }
  }
  if (PlumeTrackedResource* backing_texture =
          valid_sample(link.backing_texture)) {
    if (DirectPrivateFeedbackAliasContainsTargetSerialLocked(
            *backing_texture, link.target_content_serial)) {
      return backing_texture;
    }
  }
  if (PlumeTrackedResource* backing_base =
          valid_sample(link.backing_base_address)) {
    return backing_base;
  }
  if (PlumeTrackedResource* source_surface =
          valid_sample(link.source_surface)) {
    return source_surface;
  }
  return nullptr;
}

bool DirectSurfaceTextureLinkTouchesFablePrivateChain(
    const FableSurfaceTextureLink& link,
    const PlumeTrackedResource* sample) {
  return IsFableTitlePrivateTextureProxyAddress(link.texture) ||
         IsFableTitlePrivateTextureProxyAddress(link.texture_base_address) ||
         IsFableTitlePrivateTextureProxyAddress(link.source_surface) ||
         IsFableTitlePrivateTextureProxyAddress(link.backing_texture) ||
         IsFableTitlePrivateTextureProxyAddress(link.backing_base_address) ||
         IsFablePrivateTextureChainExtent(link.texture_width,
                                          link.texture_height) ||
         (sample && IsFablePrivateTextureChainResource(*sample));
}

bool DirectSurfaceTextureLinkIsCurrentTargetSelfEdgeLocked(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link) {
  if (!link.trusted_destination_edge || link.source_surface == 0 ||
      link.backing_texture == 0) {
    return false;
  }

  const bool self_edge =
      DirectResourceAddressMatchesLocked(link.source_surface, link.backing_texture) &&
      (DirectResourceAddressMatchesLocked(link.texture, link.source_surface) ||
       DirectResourceAddressMatchesLocked(link.texture, link.backing_texture));
  if (!self_edge) {
    return false;
  }

  const bool current_color_target =
      DirectResourceAddressMatchesCommandTargetLocked(command,
                                                      link.source_surface) ||
      DirectResourceAddressMatchesCommandTargetLocked(command,
                                                      link.backing_texture) ||
      DirectResourceAddressMatchesCommandTargetLocked(command,
                                                      link.render_target);
  const bool current_depth_target =
      DirectResourceAddressMatchesLocked(
          link.source_surface, command.resources.depth_stencil) ||
      DirectResourceAddressMatchesLocked(
          link.backing_texture, command.resources.depth_stencil) ||
      DirectResourceAddressMatchesLocked(
          link.depth_stencil, command.resources.depth_stencil);
  if (!current_color_target && !current_depth_target) {
    return false;
  }

  return true;
}

bool DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
    const FableSurfaceTextureLink& link, uint32_t surface_address,
    uint32_t depth_target_address) {
  if (!link.trusted_destination_edge ||
      link.kind != FablePrivateTextureCopyKind::kDepth ||
      surface_address == 0 || depth_target_address == 0) {
    return false;
  }

  const FablePrivateTextureProxyRole texture_role =
      FablePrivateTextureProxyRoleForAddress(link.texture);
  if (texture_role != FablePrivateTextureProxyRole::kDepth) {
    return false;
  }

  return DirectResourceAddressMatchesLocked(surface_address,
                                            depth_target_address);
}

bool DirectSurfaceTextureLinkIsCurrentTargetExactEdgeLocked(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link) {
  if (!link.trusted_destination_edge || link.texture == 0 ||
      link.source_surface == 0 || link.backing_texture != 0) {
    return false;
  }

  if (link.texture == link.source_surface) {
    return false;
  }

  bool source_surface_is_current_color_target = false;
  for (uint32_t target : command.resources.render_targets) {
    if (DirectResourceAddressMatchesLocked(link.source_surface, target)) {
      source_surface_is_current_color_target = true;
      break;
    }
  }
  const bool source_surface_is_current_depth_target =
      DirectResourceAddressMatchesLocked(
          link.source_surface, command.resources.depth_stencil) ||
      DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
          link, link.source_surface, command.resources.depth_stencil);
  const bool current_color_target =
      source_surface_is_current_color_target ||
      DirectResourceAddressMatchesCommandTargetLocked(command,
                                                      link.render_target);
  const bool current_depth_target =
      source_surface_is_current_depth_target ||
      DirectResourceAddressMatchesLocked(
          link.depth_stencil, command.resources.depth_stencil) ||
      DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
          link, link.depth_stencil, command.resources.depth_stencil);
  if (!current_color_target && !current_depth_target) {
    return false;
  }

  return true;
}

void LogDirectPrivateChainSurfaceTextureLinkLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const FableSurfaceTextureLink& link, const PlumeTrackedResource& sample,
    const char* source_name) {
  if (g_direct_renderer.diagnostics.logged_private_chain_surface_links >= 48u ||
      !link.trusted_destination_edge ||
      !DirectSurfaceTextureLinkTouchesFablePrivateChain(link, &sample)) {
    return;
  }
  REXLOG_INFO(
      "Refii plume private-chain surface-link ownership proof #{} "
      "reason=trusted-destination frame={} source={} slot={} link_slot={} "
      "trusted=1 texture=0x{:08X}/{}x{} "
      "base=0x{:08X} surface=0x{:08X} backing=0x{:08X} "
      "sample=0x{:08X}/{}x{} fmt={} target_serial={} backing_serial={} "
      "draw={} call={} context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} "
      "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
      ++g_direct_renderer.diagnostics.logged_private_chain_surface_links,
      g_direct_renderer.current_frame_index, source_name, slot,
      link.source_slot, link.texture, link.texture_width, link.texture_height,
      link.texture_base_address, link.source_surface, link.backing_texture,
      sample.guest_address, PlumeGuestTextureWidth(sample),
      PlumeGuestTextureHeight(sample),
      static_cast<uint32_t>(PlumeGuestTextureFormat(sample)),
      static_cast<unsigned long long>(link.target_content_serial),
      static_cast<unsigned long long>(link.backing_content_serial),
      static_cast<unsigned long long>(link.draw_serial),
      static_cast<unsigned long long>(link.copy_call_serial),
      static_cast<unsigned long long>(link.copy_context_key),
      link.copy_symbol_address,
      command.resources.render_targets[0], command.resources.depth_stencil,
      command.resources.vertex_shader, command.resources.pixel_shader);
}

#endif

}  // namespace refii::renderer
