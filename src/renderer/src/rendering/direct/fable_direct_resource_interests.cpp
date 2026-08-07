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

bool DirectCommandTextureSlotExactProducerProvidesSampleLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (slot >= command.resources.textures.size()) {
    return false;
  }

  const uint32_t guest = command.resources.textures[slot];
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  if (guest == 0 && !command_fetch_info.valid) {
    return false;
  }
  if (DirectCommandSlotUsesExactBinkWrapperPlaneLocked(command, slot)) {
    if (PlumeTrackedResource* bink_plane =
            ResolveDirectExactBinkWrapperPlaneForSlotLocked(command, slot)) {
      return true;
    }
    return false;
  }
  const uint32_t texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(command);
  if (texture_slot_mask != 0u &&
      !DirectCommandUsesTextureSlotForSyncLocked(command, texture_slot_mask,
                                                 slot)) {
    return false;
  }
  uint64_t exact_producer_query_key = 1469598103934665603ull;
  auto mix_exact_producer_query_key = [&](uint64_t value) {
    exact_producer_query_key ^= value;
    exact_producer_query_key *= 1099511628211ull;
  };
  mix_exact_producer_query_key(g_direct_renderer.current_frame_index);
  mix_exact_producer_query_key(command.serial);
  mix_exact_producer_query_key(command.draw_serial);
  mix_exact_producer_query_key(slot);
  mix_exact_producer_query_key(guest);
  mix_exact_producer_query_key(FableDirectTextureFetchKey(command_fetch));
  mix_exact_producer_query_key(command.resources.render_targets[0]);
  mix_exact_producer_query_key(command.resources.depth_stencil);
  mix_exact_producer_query_key(command.resources.vertex_shader);
  mix_exact_producer_query_key(command.resources.pixel_shader);
  mix_exact_producer_query_key(command.draw.private_texture_wrapper_call_serial);
  mix_exact_producer_query_key(command.draw.private_texture_wrapper_context_key);
  mix_exact_producer_query_key(
      command.draw.private_texture_wrapper_symbol_address);
  if (texture) {
    mix_exact_producer_query_key(texture->guest_address);
    mix_exact_producer_query_key(texture->parent_address);
    mix_exact_producer_query_key(PlumeGuestTextureBaseAddress(*texture));
    mix_exact_producer_query_key(FableTrackedResourceVisibleContentSerial(*texture));
  }
  static thread_local std::vector<uint64_t> resolving_exact_producer_queries;
  if (std::find(resolving_exact_producer_queries.begin(),
                resolving_exact_producer_queries.end(),
                exact_producer_query_key) !=
      resolving_exact_producer_queries.end()) {
    static uint32_t logged_exact_producer_reentrant_queries = 0;
    if (RendererDeepObserverHooksEnabled() &&
        logged_exact_producer_reentrant_queries < 32u) {
      REXLOG_INFO(
          "Refii plume exact producer reentrant query guarded #{} "
          "reason=recursive-exact-producer-query frame={} serial={} draw={} "
          "slot={} guest=0x{:08X} source={} wrapper=0x{:08X} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_producer_reentrant_queries,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.serial),
          static_cast<unsigned long long>(command.draw_serial), slot, guest,
          static_cast<uint32_t>(command.draw.source),
          command.draw.private_texture_wrapper_symbol_address,
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return false;
  }
  struct ExactProducerQueryScope {
    std::vector<uint64_t>& stack;
    ~ExactProducerQueryScope() { stack.pop_back(); }
  };
  resolving_exact_producer_queries.push_back(exact_producer_query_key);
  ExactProducerQueryScope exact_producer_query_scope{
      resolving_exact_producer_queries};
  const bool exact_begin_vertices_title_strip =
      FableTitleCommandSlotIsExactBeginVerticesTitleStrip(command, slot,
                                                      command_fetch_info);
  if (exact_begin_vertices_title_strip) {
    EnsureDirectPrivateTextureHeaderResourceLocked(g_direct_renderer.device,
                                                   command, slot);
    if (PlumeTrackedResource* title_strip = FindDirectResourceLocked(guest);
        DirectResourceMatchesExactBeginVerticesTitleStripFetch(
            title_strip, command_fetch_info, command_fetch)) {
      PublishDirectBeginVerticesTitleTextureActionForSlotLocked(command, slot,
                                                                title_strip);
      return true;
    }
  }

  PublishDirectBeginVerticesTitleTextureActionForSlotLocked(command, slot,
                                                            texture);
  if (PlumeTrackedResource* exact_mutable_header =
          ResolveDirectMutablePrivateHeaderExactSampleLocked(
              command, slot, texture)) {
    static uint32_t logged_exact_mutable_header_producer_samples = 0;
    if (RendererVerboseTracingEnabled() &&
        logged_exact_mutable_header_producer_samples < 32u) {
      REXLOG_INFO(
          "Refii plume exact mutable private header producer sample #{} "
          "frame={} slot={} guest=0x{:08X} sample=0x{:08X}/{}x{} "
          "source={} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_mutable_header_producer_samples,
          g_direct_renderer.current_frame_index, slot, guest,
          exact_mutable_header->guest_address,
          PlumeGuestTextureWidth(*exact_mutable_header),
          PlumeGuestTextureHeight(*exact_mutable_header),
          static_cast<uint32_t>(command.draw.source),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return true;
  }
  if (ResolveDirectExactProducerSampleForSlotLocked(command, slot, texture)) {
    return true;
  }

  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  const auto link_visible_to_command = [&](const FableSurfaceTextureLink* link) {
    if (!link) {
      return false;
    }
    const bool same_slot =
        link->source_slot == kUnknownSlot || link->source_slot == slot;
    if (!same_slot &&
        !DirectSurfaceTextureLinkTouchesFablePrivateChain(*link, texture)) {
      return false;
    }
    if (link->frame_index != 0 &&
        link->frame_index > g_direct_renderer.current_frame_index) {
      return false;
    }
    if (link->frame_index != 0 &&
        link->frame_index == g_direct_renderer.current_frame_index &&
        link->draw_serial != 0 && command.draw_serial != 0 &&
        link->draw_serial > command.draw_serial) {
      return false;
    }
    return true;
  };
  const uint64_t draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  const auto remember_link_source_action_sample =
      [&](const FableSurfaceTextureLink& link) -> bool {
    if (!link.trusted_destination_edge ||
        !DirectSurfaceTextureLinkHasExactCopyIdentity(link) ||
        !DirectPrivateTextureCopyKindUsesColorBacking(link.kind) ||
        link.source_surface == 0 ||
        DirectResourceAddressMatchesLocked(link.source_surface, guest)) {
      return false;
    }

    const std::vector<FablePrivateTextureCopyAction>* source_actions =
        FableDirectFindPrivateTextureCopyActionsBySource(link.source_surface);
    if (!source_actions || source_actions->empty()) {
      return false;
    }

    const FablePrivateTextureCopyAction* best_action = nullptr;
    PlumeTrackedResource* best_sample = nullptr;
    uint64_t best_score = 0;
    auto visible_action =
        [&](const FablePrivateTextureCopyAction& action) {
      if (action.frame_index != 0 &&
          action.frame_index > g_direct_renderer.current_frame_index) {
        return false;
      }
      if (action.frame_index != 0 &&
          action.frame_index == g_direct_renderer.current_frame_index &&
          action.draw_serial != 0 && draw_serial != 0 &&
          action.draw_serial > draw_serial) {
        return false;
      }
      return true;
    };

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
          FindDirectResourceLocked(action.target_surface);
      if (!sample || !IsImageResource(sample->kind) ||
          !PlumeGuestImageReady(*sample) ||
          DirectResourceIsPrivateFeedbackAlias(*sample) ||
          FableTrackedResourceIsBlankUploadedSampleProxy(*sample) ||
          DirectCommandBindsResourceAsTargetLocked(command, *sample) ||
          (!FableTrackedResourceHasSampleableContent(*sample) &&
           !FableTrackedResourceHasGpuProducedContent(*sample))) {
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
      if (action.source_texture == link.source_surface) {
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
      return false;
    }

    FablePrivateTextureCopyAction proxy_action = *best_action;
    proxy_action.source_slot = slot;
    proxy_action.source_texture = guest;
    proxy_action.source_base_address =
        command_fetch_info.valid ? command_fetch_info.base_address : 0u;
    proxy_action.source_width =
        command_fetch_info.valid ? command_fetch_info.width : 0u;
    proxy_action.source_height =
        command_fetch_info.valid ? command_fetch_info.height : 0u;
    proxy_action.source_format =
        command_fetch_info.valid ? command_fetch_info.format : 0u;
    proxy_action.source_fetch_dwords = command_fetch;
    proxy_action.backing_texture = link.texture;
    proxy_action.backing_base_address = link.texture_base_address;
    proxy_action.backing_width = link.texture_width;
    proxy_action.backing_height = link.texture_height;
    proxy_action.backing_format = link.texture_format;
    proxy_action.backing_fetch_dwords = link.texture_fetch_dwords;
    proxy_action.backing_content_serial = link.target_content_serial;
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
    proxy_action.wrapper_call_serial = link.copy_call_serial;
    proxy_action.wrapper_context_key = link.copy_context_key;
    proxy_action.wrapper_symbol_address = link.copy_symbol_address;
    proxy_action.wrapper_source_texture = guest;
    proxy_action.wrapper_source_exact = guest != 0;
    proxy_action.frame_index = g_direct_renderer.current_frame_index;
    proxy_action.draw_serial = draw_serial;
    proxy_action.kind = link.kind;
    PopulateDirectPrivateTextureCopyRectangleFromCommand(proxy_action,
                                                         command);
    proxy_action.sampled_source = {};
    proxy_action.sampled_source.kind = FableRegisteredSampleSourceKind::kGpuSurface;
    proxy_action.sampled_source.resource = best_sample->guest_address;
    proxy_action.sampled_source.content_serial =
        proxy_action.target_content_serial;

    RegisterDirectPrivateTextureCopyActionLocked(proxy_action);
    RememberDirectPrivateRectExactActionLocked(proxy_action);
    RegisterDirectSurfaceTextureLinkForPrivateCopyActionLocked(
        proxy_action, "mutable-private-header-producer-link-source-action");
    TrackDirectExactSurfaceAliasForPrivateCopyActionLocked(
        proxy_action, "mutable-private-header-producer-link-source-action");
    RememberDirectMutablePrivateHeaderExactSampleLocked(
        command, slot, proxy_action, *best_sample,
        "mutable-private-header-producer-link-source-action");

    static uint32_t
        logged_exact_private_header_producer_source_action_textures = 0;
    if (logged_exact_private_header_producer_source_action_textures < 32u) {
      REXLOG_INFO(
          "Refii plume exact private-header producer source-action "
          "texture identity #{} frame={} serial={} draw={} slot={} "
          "guest=0x{:08X} link_tex=0x{:08X} "
          "link_source=0x{:08X} action_source=0x{:08X} "
          "tracked=0x{:08X} base=0x{:08X} {}x{} fmt={} gen={} "
          "content={} call={} context=0x{:016X} "
          "symbol=0x{:08X} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
          "ps=0x{:08X} fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
          ++logged_exact_private_header_producer_source_action_textures,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.serial),
          static_cast<unsigned long long>(command.draw_serial), slot, guest,
          link.texture,
          link.source_surface, best_action->source_texture,
          best_sample->guest_address,
          PlumeGuestTextureBaseAddress(*best_sample),
          PlumeGuestTextureWidth(*best_sample),
          PlumeGuestTextureHeight(*best_sample),
          static_cast<uint32_t>(PlumeGuestTextureFormat(*best_sample)),
          static_cast<unsigned long long>(best_sample->upload_generation),
          static_cast<unsigned long long>(
              FableTrackedResourceVisibleContentSerial(*best_sample)),
          static_cast<unsigned long long>(link.copy_call_serial),
          static_cast<unsigned long long>(link.copy_context_key),
          link.copy_symbol_address, command.resources.render_targets[0],
          command.resources.depth_stencil, command.resources.vertex_shader,
          command.resources.pixel_shader, command_fetch[0], command_fetch[1],
          command_fetch[2], command_fetch[3], command_fetch[4],
          command_fetch[5]);
    }
    return true;
  };
  const auto remember_link_sample =
      [&](const FableSurfaceTextureLink* link,
          bool require_exact_copy_identity = false) -> bool {
    if (!link_visible_to_command(link)) {
      return false;
    }
    if (require_exact_copy_identity &&
        !DirectSurfaceTextureLinkHasExactCopyIdentity(*link)) {
      return false;
    }
    const bool exact_native_depth_pending =
        IsFableTitleSceneTextureSyncCommandLocked(command) &&
        link->trusted_destination_edge &&
        DirectSurfaceTextureLinkHasExactCopyIdentity(*link) &&
        link->kind == FablePrivateTextureCopyKind::kDepth &&
        link->copy_symbol_address == kFablePrivateRectNativeDepthWrapperSymbol &&
        FablePrivateTextureProxyRoleForAddress(link->texture) ==
            FablePrivateTextureProxyRole::kDepth &&
        link->source_surface != 0 && link->copy_call_serial != 0 &&
        link->copy_context_key != 0 &&
        (DirectResourceAddressMatchesLocked(link->texture, guest) ||
         DirectResourceAddressMatchesLocked(link->backing_texture, guest) ||
         (texture &&
          (DirectResourceAddressMatchesLocked(link->texture,
                                              texture->guest_address) ||
           DirectResourceAddressMatchesLocked(link->texture,
                                              texture->parent_address))));
    if (exact_native_depth_pending) {
      static uint32_t logged_exact_native_depth_producer_owners = 0;
      if (logged_exact_native_depth_producer_owners < 32u) {
        REXLOG_INFO(
            "Refii plume exact native-depth pending link owns producer #{} "
            "frame={} slot={} guest=0x{:08X} texture=0x{:08X} "
            "surface=0x{:08X} call={} context=0x{:016X} rt=0x{:08X} "
            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_native_depth_producer_owners,
            g_direct_renderer.current_frame_index, slot, guest, link->texture,
            link->source_surface,
            static_cast<unsigned long long>(link->copy_call_serial),
            static_cast<unsigned long long>(link->copy_context_key),
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
      return true;
    }
    PlumeTrackedResource* sample =
        ResolveDirectSurfaceTextureLinkResourceLocked(command, *link);
    if (!sample) {
      return remember_link_source_action_sample(*link);
    }
    return true;
  };
	  const auto find_link_by_destination =
	      [&](uint32_t destination_texture) -> bool {
	    if (destination_texture == 0) {
	      return false;
	    }
	    const FableSurfaceTextureLink* slot_link =
	        FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
	            destination_texture, slot);
	    if (remember_link_sample(slot_link)) {
	      return true;
	    }
	    return remember_link_sample(
	        FableDirectFindSurfaceTextureLinkForDestinationTexture(
	            destination_texture));
	  };
	  const auto find_exact_link_by_destination =
	      [&](uint32_t destination_texture) -> bool {
	    if (destination_texture == 0) {
	      return false;
	    }
	    const FableSurfaceTextureLink* slot_link =
	        FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
	            destination_texture, slot);
	    if (remember_link_sample(slot_link, true)) {
	      return true;
	    }
	    return remember_link_sample(
	        FableDirectFindSurfaceTextureLinkForDestinationTexture(
	            destination_texture),
	        true);
	  };
	  const auto find_visible_link_by_base = [&](uint32_t base_address) -> bool {
	    if (base_address == 0) {
	      return false;
	    }
	    return remember_link_sample(FableDirectFindVisibleSurfaceTextureLinkForBase(
	        base_address, g_direct_renderer.current_frame_index, draw_serial, slot));
	  };
	  const auto find_exact_visible_link_by_base =
	      [&](uint32_t base_address) -> bool {
	    if (base_address == 0) {
	      return false;
	    }
	    return remember_link_sample(
	        FableDirectFindVisibleSurfaceTextureLinkForBase(
	            base_address, g_direct_renderer.current_frame_index, draw_serial,
	            slot),
	        true);
	  };
	  const auto find_visible_link_by_fetch =
	      [&](const std::array<uint32_t, 6>& fetch_dwords) -> bool {
	    if (!HasTrackedFetchDwords(fetch_dwords)) {
	      return false;
	    }
	    return remember_link_sample(FableDirectFindVisibleSurfaceTextureLinkForFetch(
	        fetch_dwords, g_direct_renderer.current_frame_index, draw_serial, slot));
	  };
	  const auto find_exact_visible_link_by_fetch =
	      [&](const std::array<uint32_t, 6>& fetch_dwords) -> bool {
	    if (!HasTrackedFetchDwords(fetch_dwords)) {
	      return false;
	    }
	    return remember_link_sample(
	        FableDirectFindVisibleSurfaceTextureLinkForFetch(
	            fetch_dwords, g_direct_renderer.current_frame_index, draw_serial,
	            slot),
	        true);
	  };

  if (PlumeTrackedResource* exact_title_upload =
          ResolveDirectExactTitleUploadSourceForCommandSlotLocked(command, slot,
                                                                  texture)) {
    return true;
  }
  if (PlumeTrackedResource* tiny_title_upload =
          ResolveDirectTinyTitleUploadedTextureForCommandSlotLocked(command, slot,
                                                                    texture)) {
    return true;
  }
  if (IsFableTitleSceneTextureSyncCommandLocked(command)) {
    if (PlumeTrackedResource* registered_fetch_sample =
            ResolveDirectSampledTextureFromFetchConstantsLocked(command,
                                                                slot)) {
      if (DirectTitleSyncFetchTextureMatchesCommandLocked(
              command, slot, *registered_fetch_sample)) {
        static uint32_t logged_exact_title_sync_fetch_textures = 0;
        if (logged_exact_title_sync_fetch_textures < 32u) {
          ++logged_exact_title_sync_fetch_textures;
          LogDirectTitleSyncFetchTextureIdentityLocked(
              command, slot, *registered_fetch_sample,
              "Refii plume exact title-sync fetch texture identity");
        }
        return true;
      }
    }
  }
  const bool private_or_title_surface_copy_command =
      command.draw.source != RendererDrawEventSource::kPixVideo &&
      (DirectCommandUsesPrivateOrTitleSurfaceCopyPathLocked(command) ||
       DirectCommandUsesFablePrivateTexturePath(command, slot, texture) ||
       command.draw.private_texture_rectangle ||
       IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader) ||
       IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
       IsFableTitleSceneVertexShaderCommandLocked(command) ||
       IsFableTitleSceneTextureSyncCommandLocked(command));
  const bool escaped_private_title_like_command =
      command.draw.source != RendererDrawEventSource::kPixVideo &&
      (IsFableTitlePrivateTextureProxyAddress(guest) ||
       IsFableMutablePrivateTextureHeaderAddress(guest) ||
       (command_fetch_info.valid &&
        (IsFableTitlePrivateTextureProxyAddress(
             command_fetch_info.base_address) ||
         IsFableMutablePrivateTextureHeaderAddress(
             command_fetch_info.base_address))) ||
       (texture &&
        (IsFableTitlePrivateTextureProxyAddress(texture->guest_address) ||
         IsFableMutablePrivateTextureHeaderAddress(texture->guest_address) ||
         IsFableTitlePrivateTextureProxyAddress(texture->parent_address) ||
         IsFableMutablePrivateTextureHeaderAddress(texture->parent_address) ||
         IsFableTitlePrivateTextureProxyAddress(
             PlumeGuestTextureBaseAddress(*texture)) ||
	         IsFableMutablePrivateTextureHeaderAddress(
	             PlumeGuestTextureBaseAddress(*texture)))));
	  const bool command_private_header_identity =
	      IsFableTitlePrivateTextureProxyAddress(guest) ||
	      IsFableMutablePrivateTextureHeaderAddress(guest) ||
	      (command_fetch_info.valid &&
	       (IsFableTitlePrivateTextureProxyAddress(
	            command_fetch_info.base_address) ||
	        IsFableMutablePrivateTextureHeaderAddress(
	            command_fetch_info.base_address))) ||
	      (texture &&
	       (IsFableTitlePrivateTextureProxyAddress(texture->guest_address) ||
	        IsFableMutablePrivateTextureHeaderAddress(texture->guest_address) ||
	        IsFableTitlePrivateTextureProxyAddress(texture->parent_address) ||
	        IsFableMutablePrivateTextureHeaderAddress(texture->parent_address) ||
		        IsFableTitlePrivateTextureProxyAddress(
		            PlumeGuestTextureBaseAddress(*texture)) ||
		        IsFableMutablePrivateTextureHeaderAddress(
		            PlumeGuestTextureBaseAddress(*texture))));
  if (private_or_title_surface_copy_command && guest == 0 &&
      !command_private_header_identity) {
    return false;
  }
  auto exact_current_producer_owner_cache_key =
      [&](uint32_t destination_texture) {
    size_t seed = 0;
    const FableGuestResourceRegistry& registry =
        FableDirectGuestResourceRegistry();
    DirectHashCombine(seed, std::hash<uint64_t>{}(registry.private_texture_copy_serial));
    DirectHashCombine(seed,
                std::hash<uint64_t>{}(registry.surface_texture_link_serial));
    DirectHashCombine(seed, std::hash<uint64_t>{}(registry.texture_identity_serial));
    DirectHashCombine(seed,
                std::hash<uint64_t>{}(g_direct_renderer.private_texture_alias_serial));
    DirectHashCombine(seed, std::hash<uint32_t>{}(g_direct_renderer.current_frame_index));
    DirectHashCombine(seed, std::hash<uint64_t>{}(command.serial));
    DirectHashCombine(seed, std::hash<uint64_t>{}(draw_serial));
    DirectHashCombine(seed, std::hash<uint32_t>{}(slot));
    DirectHashCombine(seed, std::hash<uint32_t>{}(guest));
    DirectHashCombine(seed, std::hash<uint32_t>{}(destination_texture));
    DirectHashCombine(seed, std::hash<uint32_t>{}(command.resources.render_targets[0]));
    DirectHashCombine(seed, std::hash<uint32_t>{}(command.resources.depth_stencil));
    DirectHashCombine(seed, std::hash<uint32_t>{}(command.resources.vertex_shader));
    DirectHashCombine(seed, std::hash<uint32_t>{}(command.resources.pixel_shader));
    DirectHashCombine(seed, std::hash<uint32_t>{}(
                          command.resources.shader_roots.vertex_shader
                              .symbol_address));
    DirectHashCombine(seed, std::hash<uint64_t>{}(
                          command.resources.shader_roots.vertex_shader
                              .call_serial));
    DirectHashCombine(seed, std::hash<uint32_t>{}(
                          command.resources.shader_roots.pixel_shader
                              .symbol_address));
    DirectHashCombine(seed, std::hash<uint64_t>{}(
                          command.resources.shader_roots.pixel_shader
                              .call_serial));
    DirectHashCombine(seed, std::hash<uint32_t>{}(
                          static_cast<uint32_t>(command.draw.source)));
    DirectHashCombine(seed, std::hash<bool>{}(command.draw.private_texture_rectangle));
    DirectHashCombine(seed, std::hash<uint64_t>{}(
                          command.draw.private_texture_wrapper_call_serial));
    DirectHashCombine(seed, std::hash<uint64_t>{}(
                          command.draw.private_texture_wrapper_context_key));
    DirectHashCombine(seed, std::hash<uint32_t>{}(
                          command.draw.private_texture_wrapper_symbol_address));
    DirectHashCombine(seed, std::hash<uint64_t>{}(
                          FableDirectTextureFetchKey(command_fetch)));
    if (command_fetch_info.valid) {
      DirectHashCombine(seed,
                  std::hash<uint32_t>{}(command_fetch_info.base_address));
      DirectHashCombine(seed, std::hash<uint32_t>{}(command_fetch_info.width));
      DirectHashCombine(seed, std::hash<uint32_t>{}(command_fetch_info.height));
      DirectHashCombine(seed, std::hash<uint32_t>{}(command_fetch_info.format));
    }
    if (texture) {
      DirectHashCombine(seed, std::hash<uint32_t>{}(texture->guest_address));
      DirectHashCombine(seed, std::hash<uint32_t>{}(texture->parent_address));
      DirectHashCombine(seed,
                  std::hash<uint32_t>{}(PlumeGuestTextureBaseAddress(*texture)));
      DirectHashCombine(seed,
                  std::hash<uint64_t>{}(FableDirectTextureFetchKey(
                      PlumeGuestTextureFetchDwords(*texture))));
      DirectHashCombine(seed, std::hash<uint64_t>{}(texture->content_serial));
      DirectHashCombine(seed, std::hash<uint64_t>{}(texture->upload_generation));
      DirectHashCombine(seed, std::hash<uint64_t>{}(texture->draw_content_serial));
    }
    return seed != 0 ? static_cast<uint64_t>(seed) : 1ull;
  };
  auto find_cached_exact_current_producer_owner =
      [&](uint64_t cache_key, uint32_t destination_texture) {
    auto cached =
        g_direct_renderer.exact_current_producer_owner_cache.find(cache_key);
    if (cached == g_direct_renderer.exact_current_producer_owner_cache.end()) {
      return false;
    }

    const FableGuestResourceRegistry& registry =
        FableDirectGuestResourceRegistry();
    const DirectExactCurrentProducerOwnerCacheEntry& entry = cached->second;
    if (!entry.owned ||
        entry.frame_index != g_direct_renderer.current_frame_index ||
        entry.command_serial != command.serial ||
        entry.draw_serial != draw_serial ||
        entry.resource_serial != g_direct_renderer.resource_serial ||
        entry.resource_destroys != g_direct_renderer.resource_destroys ||
        entry.private_copy_serial != registry.private_texture_copy_serial ||
        entry.surface_texture_link_serial !=
            registry.surface_texture_link_serial ||
        entry.surface_destination_relation_serial !=
            registry.surface_destination_relation_serial ||
        entry.texture_identity_serial != registry.texture_identity_serial ||
        entry.private_texture_alias_serial !=
            g_direct_renderer.private_texture_alias_serial) {
      g_direct_renderer.exact_current_producer_owner_cache.erase(cached);
      return false;
    }

    if (g_direct_renderer.diagnostics.logged_exact_current_producer_owner_cache_hits <
        32u) {
      REXLOG_INFO(
          "Refii plume exact current producer owner cache hit #{} frame={} "
          "slot={} guest=0x{:08X} destination=0x{:08X} rt=0x{:08X} "
          "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++g_direct_renderer.diagnostics.logged_exact_current_producer_owner_cache_hits,
          g_direct_renderer.current_frame_index, slot, guest,
          destination_texture, command.resources.render_targets[0],
          command.resources.depth_stencil, command.resources.vertex_shader,
          command.resources.pixel_shader);
    }
    return true;
  };
  auto remember_exact_current_producer_owner = [&](uint64_t cache_key) {
    constexpr size_t kMaxExactCurrentProducerOwnerCacheEntries = 4096u;
    if (g_direct_renderer.exact_current_producer_owner_cache.size() >=
        kMaxExactCurrentProducerOwnerCacheEntries) {
      g_direct_renderer.exact_current_producer_owner_cache.clear();
    }
    const FableGuestResourceRegistry& registry =
        FableDirectGuestResourceRegistry();
    DirectExactCurrentProducerOwnerCacheEntry entry;
    entry.owned = true;
    entry.frame_index = g_direct_renderer.current_frame_index;
    entry.command_serial = command.serial;
    entry.draw_serial = draw_serial;
    entry.resource_serial = g_direct_renderer.resource_serial;
    entry.resource_destroys = g_direct_renderer.resource_destroys;
    entry.private_copy_serial = registry.private_texture_copy_serial;
    entry.surface_texture_link_serial = registry.surface_texture_link_serial;
    entry.surface_destination_relation_serial =
        registry.surface_destination_relation_serial;
    entry.texture_identity_serial = registry.texture_identity_serial;
    entry.private_texture_alias_serial =
        g_direct_renderer.private_texture_alias_serial;
    g_direct_renderer.exact_current_producer_owner_cache[cache_key] = entry;
  };
		  auto exact_surface_link_owns_current_producer =
		      [&](uint32_t destination_texture) -> bool {
	    if (destination_texture == 0) {
	      return false;
	    }
      const uint64_t cache_key =
          exact_current_producer_owner_cache_key(destination_texture);
      if (find_cached_exact_current_producer_owner(cache_key,
                                                  destination_texture)) {
        return true;
      }
	    const uint64_t command_draw_serial =
	        command.draw_serial != 0 ? command.draw_serial : command.serial;
	    auto copy_identity_matches_command =
	        [&](const FableSurfaceTextureLink& link) {
	      bool matched = false;
	      if (command.draw.private_texture_wrapper_symbol_address != 0 &&
	          link.copy_symbol_address != 0) {
	        if (command.draw.private_texture_wrapper_symbol_address !=
	            link.copy_symbol_address) {
	          return false;
	        }
	        matched = true;
	      }
	      if (command.draw.private_texture_wrapper_call_serial != 0 &&
	          link.copy_call_serial != 0) {
	        if (command.draw.private_texture_wrapper_call_serial !=
	            link.copy_call_serial) {
	          return false;
	        }
	        matched = true;
	      }
	      if (command.draw.private_texture_wrapper_context_key != 0 &&
	          link.copy_context_key != 0) {
	        if (command.draw.private_texture_wrapper_context_key !=
	            link.copy_context_key) {
	          return false;
	        }
	        matched = true;
	      }
	      return matched;
	    };
    auto link_visible = [&](const FableSurfaceTextureLink* link) {
      if (!link || !link->trusted_destination_edge || link->texture == 0 ||
          link->source_surface == 0) {
        return false;
      }
      if (DirectSurfaceTextureLinkIsPrivateTitleWrapperRoot(*link) &&
          !DirectSurfaceTextureLinkPrivateWrapperVisibleToCommandLocked(command,
                                                                        *link)) {
        return false;
      }
      if (link->frame_index > g_direct_renderer.current_frame_index) {
        return false;
      }
	      if (link->frame_index == g_direct_renderer.current_frame_index &&
	          link->draw_serial != 0 && command_draw_serial != 0 &&
	          link->draw_serial > command_draw_serial) {
	        return false;
	      }
	      if (!DirectResourceAddressMatchesCommandTargetLocked(
	              command, link->source_surface)) {
	        return false;
	      }
	      constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
	      return link->source_slot == kUnknownSlot || link->source_slot == slot ||
	             copy_identity_matches_command(*link);
	    };
	    auto link_identity_matches = [&](const FableSurfaceTextureLink* link) {
	      if (!link_visible(link)) {
	        return false;
	      }
	      if (DirectResourceAddressMatchesLocked(link->texture,
	                                             destination_texture) ||
	          DirectResourceAddressMatchesLocked(link->backing_texture,
	                                             destination_texture)) {
	        return true;
	      }
	      if (command_fetch_info.valid &&
	          link->texture_base_address == command_fetch_info.base_address) {
	        return true;
	      }
	      if (!texture) {
	        return false;
	      }
	      return DirectResourceAddressMatchesLocked(link->texture,
	                                               texture->guest_address) ||
	             DirectResourceAddressMatchesLocked(link->texture,
	                                               texture->parent_address) ||
	             DirectResourceAddressMatchesLocked(link->backing_texture,
	                                               texture->guest_address) ||
	             DirectResourceAddressMatchesLocked(link->backing_texture,
	                                               texture->parent_address);
	    };
	    const FableSurfaceTextureLink* link =
	        FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
	            destination_texture, g_direct_renderer.current_frame_index,
	            command_draw_serial, slot);
	    if (!link_identity_matches(link)) {
	      link = FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
	          destination_texture, slot);
	    }
	    if (!link_identity_matches(link)) {
	      link = FableDirectFindSurfaceTextureLinkForDestinationTexture(
	          destination_texture);
	    }
      if (!link_identity_matches(link)) {
        const FableSurfaceDestinationTextureRelation* relation =
            FableDirectFindVisibleSurfaceDestinationRelationForDestinationTexture(
                destination_texture, g_direct_renderer.current_frame_index,
                command_draw_serial, slot);
        link = relation ? &relation->link : nullptr;
      }
      if (!link_identity_matches(link)) {
        const FableSurfaceDestinationTextureRelation* relation =
            FableDirectFindSurfaceDestinationRelationForDestinationTextureSlot(
                destination_texture, slot);
        link = relation ? &relation->link : nullptr;
      }
      if (!link_identity_matches(link)) {
        const FableSurfaceDestinationTextureRelation* relation =
            FableDirectFindSurfaceDestinationRelationForDestinationTexture(
                destination_texture);
        link = relation ? &relation->link : nullptr;
      }
	    if (!link_identity_matches(link)) {
	      return false;
	    }

	    const PlumeTrackedResource* source =
	        FindDirectResourceLocked(link->source_surface);
	    const PlumeTrackedResource* destination =
	        FindDirectResourceLocked(link->texture);
	    const bool source_ready =
	        source && IsImageResource(source->kind) && PlumeGuestImageReady(*source);
	    const bool destination_ready =
	        destination && IsImageResource(destination->kind) &&
	        PlumeGuestImageReady(*destination);
	    const bool exact_private_title_destination =
	        command_private_header_identity &&
	        (DirectResourceAddressMatchesLocked(link->texture, guest) ||
	         DirectResourceAddressMatchesLocked(link->backing_texture, guest) ||
	         IsFableMutablePrivateTextureHeaderAddress(link->texture) ||
	         IsFableTitlePrivateTextureProxyAddress(link->texture));
	    if (!source_ready ||
	        (!destination_ready && !exact_private_title_destination)) {
	      return false;
      }
      remember_exact_current_producer_owner(cache_key);
	    return true;
	  };
	  auto remember_exact_alias_link_sample =
	      [&](uint32_t destination_texture) -> bool {
    RendererSurfaceCopyAliasSnapshot alias;
    if (!FindDirectSurfaceCopyAliasByDestinationAddressLocked(
            destination_texture, &alias) ||
        !DirectSurfaceCopyAliasHasExactIdentity(alias)) {
      return false;
    }
    return remember_link_sample(FindDirectSurfaceTextureLinkByAliasLocked(alias));
  };
  auto remember_exact_depth_family_alias_link_sample =
      [&](uint32_t destination_texture) -> bool {
    if (destination_texture == 0 ||
        FablePrivateTextureProxyRoleForAddress(destination_texture) !=
            FablePrivateTextureProxyRole::kDepth) {
      return false;
    }

    RendererSurfaceCopyAliasSnapshot alias;
    if (!FindDirectSurfaceCopyAliasByDestinationAddressLocked(
            destination_texture, &alias) ||
        !DirectSurfaceCopyAliasHasExactIdentity(alias)) {
      return false;
    }

    auto link_matches_alias_identity =
        [&](const FableSurfaceTextureLink* link) {
      if (!link || !link->trusted_destination_edge) {
        return false;
      }
      const bool has_matching_identity =
          (alias.copy_call_serial != 0 &&
           link->copy_call_serial == alias.copy_call_serial) ||
          (alias.copy_context_key != 0 &&
           link->copy_context_key == alias.copy_context_key) ||
          (alias.copy_symbol_address != 0 &&
           link->copy_symbol_address == alias.copy_symbol_address);
      if (!has_matching_identity) {
        return false;
      }
      if (alias.copy_symbol_address != 0 &&
          link->copy_symbol_address != 0 &&
          link->copy_symbol_address != alias.copy_symbol_address) {
        return false;
      }
      if (alias.copy_call_serial != 0 && link->copy_call_serial != 0 &&
          link->copy_call_serial != alias.copy_call_serial) {
        return false;
      }
      if (alias.copy_context_key != 0 && link->copy_context_key != 0 &&
          link->copy_context_key != alias.copy_context_key) {
        return false;
      }
      if (!DirectResourceAddressMatchesLocked(link->texture,
                                              alias.destination_surface)) {
        return false;
      }
      return DirectResourceAddressMatchesLocked(link->source_surface,
                                               alias.source_surface) ||
             DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
                 *link, link->source_surface, alias.source_surface);
    };
    const FableSurfaceTextureLink* exact_link = nullptr;
    bool ambiguous_exact_link = false;
    auto consider_link = [&](const FableSurfaceTextureLink* candidate) {
      if (!candidate || !candidate->trusted_destination_edge ||
          candidate->kind != FablePrivateTextureCopyKind::kDepth ||
          FablePrivateTextureProxyRoleForAddress(candidate->texture) !=
              FablePrivateTextureProxyRole::kDepth ||
          (candidate->source_slot != slot &&
           candidate->source_slot != kUnknownSlot) ||
          !DirectResourceAddressMatchesLocked(candidate->texture,
                                              destination_texture) ||
          !link_visible_to_command(candidate) ||
          !link_matches_alias_identity(candidate)) {
        return;
      }

      const bool same_depth_family =
          DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
              *candidate, candidate->source_surface,
              command.resources.depth_stencil) ||
          DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
              *candidate, candidate->depth_stencil,
              command.resources.depth_stencil) ||
          DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
              *candidate, candidate->source_surface, alias.source_surface) ||
          DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
              *candidate, candidate->depth_stencil, alias.source_surface);
      if (!same_depth_family) {
        return;
      }
      if (exact_link && exact_link->serial != candidate->serial) {
        ambiguous_exact_link = true;
        return;
      }
      exact_link = candidate;
    };

    if (const std::vector<FableSurfaceTextureLink>* destination_links =
            FableDirectFindSurfaceTextureLinksForDestinationTexture(
                destination_texture)) {
      for (auto it = destination_links->rbegin();
           it != destination_links->rend(); ++it) {
        consider_link(&*it);
        if (ambiguous_exact_link) {
          break;
        }
      }
    }
    if (ambiguous_exact_link || !exact_link) {
      return false;
    }

    static uint32_t logged_native_depth_family_sample_selects = 0;
    if (logged_native_depth_family_sample_selects < 32u) {
      REXLOG_INFO(
          "Refii plume exact native depth family sample select #{} "
          "frame={} slot={} guest=0x{:08X} texture=0x{:08X} "
          "surface=0x{:08X} call={} context=0x{:016X} symbol=0x{:08X} "
          "alias_source=0x{:08X} alias_call={} alias_context=0x{:016X} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_native_depth_family_sample_selects,
          g_direct_renderer.current_frame_index, slot, destination_texture,
          exact_link->texture, exact_link->source_surface,
          static_cast<unsigned long long>(exact_link->copy_call_serial),
          static_cast<unsigned long long>(exact_link->copy_context_key),
          exact_link->copy_symbol_address, alias.source_surface,
          static_cast<unsigned long long>(alias.copy_call_serial),
          static_cast<unsigned long long>(alias.copy_context_key),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    if (remember_link_sample(exact_link)) {
      return true;
    }
    return true;
  };
  auto remember_exact_native_depth_pending_link_sample =
      [&](uint32_t destination_texture) -> bool {
    if (!private_or_title_surface_copy_command ||
        command.draw.source == RendererDrawEventSource::kPixVideo ||
        !IsFableTitleSceneTextureSyncCommandLocked(command) ||
        destination_texture == 0 ||
        FablePrivateTextureProxyRoleForAddress(destination_texture) !=
            FablePrivateTextureProxyRole::kDepth) {
      return false;
    }

    auto link_matches_destination =
        [&](const FableSurfaceTextureLink* link) -> bool {
      if (!link || !link->trusted_destination_edge ||
          !DirectSurfaceTextureLinkHasExactCopyIdentity(*link) ||
          link->kind != FablePrivateTextureCopyKind::kDepth ||
          link->copy_symbol_address !=
              kFablePrivateRectNativeDepthWrapperSymbol ||
          FablePrivateTextureProxyRoleForAddress(link->texture) !=
              FablePrivateTextureProxyRole::kDepth ||
          link->source_surface == 0 || link->copy_call_serial == 0 ||
          link->copy_context_key == 0 || !link_visible_to_command(link)) {
        return false;
      }
      return DirectResourceAddressMatchesLocked(link->texture,
                                                destination_texture) ||
             DirectResourceAddressMatchesLocked(link->backing_texture,
                                                destination_texture) ||
             (texture &&
              (DirectResourceAddressMatchesLocked(link->texture,
                                                  texture->guest_address) ||
               DirectResourceAddressMatchesLocked(link->texture,
                                                  texture->parent_address)));
    };

    auto action_matches_link =
        [&](const FablePrivateTextureCopyAction& action,
            const FableSurfaceTextureLink& link) {
      if (action.kind != FablePrivateTextureCopyKind::kDepth) {
        return false;
      }
      if (action.frame_index != 0 &&
          action.frame_index > g_direct_renderer.current_frame_index) {
        return false;
      }
      if (action.frame_index != 0 &&
          action.frame_index == g_direct_renderer.current_frame_index &&
          action.draw_serial != 0 && draw_serial != 0 &&
          action.draw_serial > draw_serial) {
        return false;
      }
      const bool source_matches =
          DirectResourceAddressMatchesLocked(action.source_texture,
                                             link.texture) ||
          DirectResourceAddressMatchesLocked(action.wrapper_source_texture,
                                             link.texture) ||
          DirectResourceAddressMatchesLocked(action.source_texture,
                                             destination_texture) ||
          (command_fetch_info.valid &&
           action.source_base_address == command_fetch_info.base_address);
      if (!source_matches) {
        return false;
      }
      const bool target_matches =
          DirectResourceAddressMatchesLocked(action.target_surface,
                                             link.source_surface) ||
          DirectResourceAddressMatchesLocked(action.depth_stencil,
                                             link.source_surface) ||
          DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
              link, link.source_surface, action.target_surface) ||
          DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
              link, link.source_surface, action.depth_stencil);
      if (!target_matches) {
        return false;
      }
      const bool identity_matches =
          (action.wrapper_call_serial != 0 &&
           action.wrapper_call_serial == link.copy_call_serial) ||
          (action.wrapper_context_key != 0 &&
           action.wrapper_context_key == link.copy_context_key) ||
          (action.wrapper_symbol_address != 0 &&
           FablePrivateRectWrapperSymbolsCompatible(
               action.wrapper_symbol_address, link.copy_symbol_address));
      if (!identity_matches) {
        return false;
      }
      if (action.wrapper_call_serial != 0 &&
          action.wrapper_call_serial != link.copy_call_serial) {
        return false;
      }
      if (action.wrapper_context_key != 0 &&
          action.wrapper_context_key != link.copy_context_key) {
        return false;
      }
      return action.wrapper_symbol_address == 0 ||
             FablePrivateRectWrapperSymbolsCompatible(
                 action.wrapper_symbol_address, link.copy_symbol_address);
    };

    auto find_matching_action = [&](const FableSurfaceTextureLink& link)
        -> const FablePrivateTextureCopyAction* {
      if (const FablePrivateTextureCopyAction* action =
              FableDirectFindPrivateTextureCopyActionByWrapperCall(
                  link.copy_call_serial);
          action && action_matches_link(*action, link)) {
        return action;
      }
      auto find_in_actions =
          [&](const std::vector<FablePrivateTextureCopyAction>* actions)
          -> const FablePrivateTextureCopyAction* {
        if (!actions) {
          return nullptr;
        }
        for (auto it = actions->rbegin(); it != actions->rend(); ++it) {
          if (action_matches_link(*it, link)) {
            return &*it;
          }
        }
        return nullptr;
      };
      if (const FablePrivateTextureCopyAction* action =
              find_in_actions(FableDirectFindPrivateTextureCopyActionsBySource(
                  link.texture))) {
        return action;
      }
      return find_in_actions(FableDirectFindPrivateTextureCopyActionsByTarget(
          link.source_surface));
    };

    auto remember_link = [&](const FableSurfaceTextureLink* link) -> bool {
      if (!link_matches_destination(link) || !find_matching_action(*link)) {
        return false;
      }
      PlumeTrackedResource* source = FindDirectResourceLocked(link->source_surface);
      if (source && IsImageResource(source->kind) &&
          PlumeGuestImageReady(*source) &&
          !DirectResourceIsPrivateFeedbackAlias(*source) &&
          !FableTrackedResourceIsBlankUploadedSampleProxy(*source) &&
          DirectCommandBindsResourceAsTargetLocked(command, *source) &&
          DirectSyntheticPrivateRectDepthActionCanExecuteLocked(command, slot,
                                                                texture)) {
        FableSurfaceTextureLink prepared_link = *link;
        prepared_link.frame_index = g_direct_renderer.current_frame_index;
        prepared_link.draw_serial = draw_serial;
        prepared_link.render_target = command.resources.render_targets[0];
        prepared_link.depth_stencil = command.resources.depth_stencil;
        prepared_link.vertex_shader = command.resources.vertex_shader;
        prepared_link.pixel_shader = command.resources.pixel_shader;
        PopulateDirectSurfaceTextureLinkShaderRoots(prepared_link, command);
        PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(prepared_link,
                                                               *source);
        RegisterDirectSurfaceTextureLinkPreparedLocked(
            prepared_link, "exact-depth-title-sync-depth-execute");
        return true;
      }
      if (!source || !IsImageResource(source->kind) ||
          !PlumeGuestImageReady(*source) ||
          DirectResourceIsPrivateFeedbackAlias(*source) ||
          FableTrackedResourceIsBlankUploadedSampleProxy(*source) ||
          DirectCommandBindsResourceAsTargetLocked(command, *source)) {
        return DirectSyntheticPrivateRectDepthActionCanExecuteLocked(
            command, slot, texture);
      }

      FableSurfaceTextureLink prepared_link = *link;
      prepared_link.frame_index = g_direct_renderer.current_frame_index;
      prepared_link.draw_serial = draw_serial;
      prepared_link.render_target = command.resources.render_targets[0];
      prepared_link.depth_stencil = command.resources.depth_stencil;
      prepared_link.vertex_shader = command.resources.vertex_shader;
      prepared_link.pixel_shader = command.resources.pixel_shader;
      PopulateDirectSurfaceTextureLinkShaderRoots(prepared_link, command);
      PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(prepared_link,
                                                             *source);
      RegisterDirectSurfaceTextureLinkPreparedLocked(
          prepared_link, "exact-depth-title-sync-pending");

      static uint32_t logged_exact_native_depth_title_sync_samples = 0;
      if (logged_exact_native_depth_title_sync_samples < 48u) {
        REXLOG_INFO(
            "Refii plume exact depth title-sync source sample #{} "
            "frame={} slot={} guest=0x{:08X} texture=0x{:08X} "
            "surface=0x{:08X}/{}x{} call={} context=0x{:016X} "
            "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_native_depth_title_sync_samples,
            g_direct_renderer.current_frame_index, slot, destination_texture,
            link->texture, source->guest_address,
            PlumeGuestTextureWidth(*source), PlumeGuestTextureHeight(*source),
            static_cast<unsigned long long>(link->copy_call_serial),
            static_cast<unsigned long long>(link->copy_context_key),
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
      return true;
    };

    if (remember_link(FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
            destination_texture, g_direct_renderer.current_frame_index,
            draw_serial, slot))) {
      return true;
    }
    if (remember_link(FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
            destination_texture, slot))) {
      return true;
    }
    return remember_link(
        FableDirectFindSurfaceTextureLinkForDestinationTexture(
            destination_texture));
  };
  if (private_or_title_surface_copy_command) {
    if (remember_exact_native_depth_pending_link_sample(guest)) {
      return true;
    }
    if (remember_exact_alias_link_sample(guest)) {
      return true;
    }
    if (remember_exact_depth_family_alias_link_sample(guest)) {
      return true;
    }
    if (texture) {
      if (remember_exact_alias_link_sample(texture->guest_address) ||
          remember_exact_alias_link_sample(texture->parent_address)) {
        return true;
      }
      if (remember_exact_depth_family_alias_link_sample(
              texture->guest_address) ||
          remember_exact_depth_family_alias_link_sample(
              texture->parent_address)) {
        return true;
      }
      if (remember_exact_native_depth_pending_link_sample(
              texture->guest_address) ||
          remember_exact_native_depth_pending_link_sample(
              texture->parent_address)) {
        return true;
      }
    }
    if (const FableTextureIdentity* identity =
            FableDirectFindTextureIdentityByObject(guest)) {
      if (remember_exact_native_depth_pending_link_sample(
              identity->object_texture) ||
          remember_exact_native_depth_pending_link_sample(
              identity->parent_texture)) {
        return true;
      }
      if (remember_exact_alias_link_sample(identity->object_texture) ||
          remember_exact_alias_link_sample(identity->parent_texture)) {
        return true;
      }
      if (remember_exact_depth_family_alias_link_sample(
              identity->object_texture) ||
          remember_exact_depth_family_alias_link_sample(
              identity->parent_texture)) {
        return true;
      }
    }
    if (find_exact_link_by_destination(guest)) {
      return true;
    }
    if (texture) {
      if (find_exact_link_by_destination(texture->guest_address) ||
          find_exact_link_by_destination(texture->parent_address)) {
        return true;
      }
    }
    if (const FableTextureIdentity* identity =
            FableDirectFindTextureIdentityByObject(guest)) {
      if (find_exact_link_by_destination(identity->object_texture) ||
          find_exact_link_by_destination(identity->parent_texture)) {
        return true;
      }
    }
    if (PlumeTrackedResource* resolved_mutable_header =
            ResolveDirectMutablePrivateHeaderSampleByIdentityLocked(
                command, slot, texture, true)) {
      static uint32_t logged_exact_private_header_texture_identities = 0;
      if (logged_exact_private_header_texture_identities < 32u) {
        REXLOG_INFO(
            "Refii plume exact private-header texture identity #{} "
            "frame={} serial={} draw={} slot={} guest=0x{:08X} "
            "tracked=0x{:08X} base=0x{:08X} {}x{} fmt={} gen={} "
            "content={} source={} rt=0x{:08X} ds=0x{:08X} "
            "vs=0x{:08X} ps=0x{:08X} "
            "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
            ++logged_exact_private_header_texture_identities,
            g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(command.serial),
            static_cast<unsigned long long>(command.draw_serial), slot, guest,
            resolved_mutable_header->guest_address,
            PlumeGuestTextureBaseAddress(*resolved_mutable_header),
            PlumeGuestTextureWidth(*resolved_mutable_header),
            PlumeGuestTextureHeight(*resolved_mutable_header),
            PlumeGuestTextureGuestFormat(*resolved_mutable_header),
            static_cast<unsigned long long>(
                resolved_mutable_header->upload_generation),
            static_cast<unsigned long long>(
                FableTrackedResourceVisibleContentSerial(*resolved_mutable_header)),
            static_cast<uint32_t>(command.draw.source),
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader,
            command_fetch[0], command_fetch[1], command_fetch[2],
            command_fetch[3], command_fetch[4], command_fetch[5]);
      }
      return true;
    }
	    if (exact_surface_link_owns_current_producer(guest)) {
	      return true;
	    }
	    if (texture &&
	        (exact_surface_link_owns_current_producer(texture->guest_address) ||
	         exact_surface_link_owns_current_producer(texture->parent_address))) {
	      return true;
	    }
	    if (ResolveDirectPrivateTextureCopyActionLocked(
	            command, slot, const_cast<PlumeTrackedResource*>(texture))) {
	      return true;
	    }
	    DirectPrivateTextureCopyActionMatch exact_action_match =
	        DirectPrivateTextureCopyActionMatch::kNone;
	    if (const FablePrivateTextureCopyAction* action =
	            SelectDirectExactPrivateTextureCopyActionForCommandSlotLocked(
	                command, slot, texture, &exact_action_match);
        action && exact_action_match ==
                      DirectPrivateTextureCopyActionMatch::
                          kExactCurrentColorAction &&
        action->kind != FablePrivateTextureCopyKind::kDepth &&
        DirectPrivateTextureCopyActionWrapperMatchesCommand(command, *action)) {
      static uint32_t logged_exact_current_color_producer_waits = 0;
      if (logged_exact_current_color_producer_waits < 32u) {
        REXLOG_INFO(
            "Refii plume exact current-color producer owns private/title "
            "sample #{} frame={} slot={} guest=0x{:08X} source=0x{:08X} "
            "target=0x{:08X} call={} context=0x{:016X} rt=0x{:08X} "
            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_current_color_producer_waits,
            g_direct_renderer.current_frame_index, slot, guest,
            action->source_texture, action->target_surface,
            static_cast<unsigned long long>(action->wrapper_call_serial),
            static_cast<unsigned long long>(action->wrapper_context_key),
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader);
	      }
	      return true;
	    }
    const PlumeTrackedResource* snapshot_source = texture;
    if (!snapshot_source && guest != 0) {
      snapshot_source = FindDirectResourceLocked(guest);
    }
    if (DirectCommandCanSnapshotPrivateTextureFeedbackLocked(command, slot,
                                                            snapshot_source)) {
      static uint32_t logged_exact_snapshot_producer_waits = 0;
      if (logged_exact_snapshot_producer_waits < 32u) {
        REXLOG_INFO(
            "Refii plume exact private/title snapshot producer owns sample #{} "
            "frame={} slot={} guest=0x{:08X} source=0x{:08X}/{}x{} "
            "target=0x{:08X} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
            "ps=0x{:08X}",
            ++logged_exact_snapshot_producer_waits,
            g_direct_renderer.current_frame_index, slot, guest,
            snapshot_source ? snapshot_source->guest_address : 0u,
            snapshot_source ? PlumeGuestTextureWidth(*snapshot_source) : 0u,
            snapshot_source ? PlumeGuestTextureHeight(*snapshot_source) : 0u,
            command.resources.render_targets[0],
            command.resources.render_targets[0],
            command.resources.depth_stencil,
            command.resources.vertex_shader,
            command.resources.pixel_shader);
      }
      return true;
    }
    const bool allow_direct_resource_exact_sample =
        !DirectCommandTextureSlotHasPrivateTitleIdentityLocked(command, slot,
                                                              texture);
	    if (DirectCommandTextureSlotDirectResourceProvidesSampleLocked(
	            command, slot, texture, allow_direct_resource_exact_sample)) {
      static uint32_t logged_direct_private_title_exact_samples = 0;
      if (logged_direct_private_title_exact_samples < 32u) {
        REXLOG_INFO(
            "Refii plume direct private/title exact producer sample #{} "
            "frame={} slot={} guest=0x{:08X} tracked=0x{:08X} source={} "
            "title_sync={} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
            "ps=0x{:08X}",
            ++logged_direct_private_title_exact_samples,
            g_direct_renderer.current_frame_index, slot, guest,
            texture ? texture->guest_address : 0u,
            static_cast<uint32_t>(command.draw.source),
            IsFableTitleSceneTextureSyncCommandLocked(command) ? 1 : 0,
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader);
	      }
	      return true;
	    }
    if (DirectCommandSlotUsesExactTitleUploadSourceLocked(command, slot)) {
      static uint32_t logged_exact_title_upload_sample_producers = 0;
      if (logged_exact_title_upload_sample_producers < 32u) {
        REXLOG_INFO(
            "Refii plume exact title upload producer owns private/title "
            "sample #{} frame={} slot={} guest=0x{:08X} source={} "
            "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_title_upload_sample_producers,
            g_direct_renderer.current_frame_index, slot, guest,
            static_cast<uint32_t>(command.draw.source),
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
      return true;
    }
    if (IsFableTitleSceneTextureSyncCommandLocked(command) &&
        command_fetch_info.valid) {
      return false;
	    }
	    return false;
	  }
  if (escaped_private_title_like_command) {
    return false;
  }
  auto link_matches_bound_destination =
      [&](const FableSurfaceTextureLink& link,
          uint32_t destination_texture) -> bool {
    if (destination_texture != 0 &&
        DirectResourceAddressMatchesLocked(link.texture, destination_texture)) {
      return true;
    }
    if (command_fetch_info.valid && link.texture_base_address != 0 &&
        link.texture_base_address == command_fetch_info.base_address) {
      return true;
    }
    const uint64_t command_fetch_key = FableDirectTextureFetchKey(command_fetch);
    if (command_fetch_key != 0 &&
        FableDirectTextureFetchKey(link.texture_fetch_dwords) ==
            command_fetch_key) {
      return true;
    }
    if (const FableTextureIdentity* identity =
            FableDirectFindTextureIdentityByObject(destination_texture)) {
      if (DirectResourceAddressMatchesLocked(link.texture,
                                             identity->object_texture) ||
          DirectResourceAddressMatchesLocked(link.texture,
                                             identity->parent_texture) ||
          (identity->base_address != 0 &&
           link.texture_base_address == identity->base_address)) {
        return true;
      }
      const uint64_t identity_fetch_key =
          FableDirectTextureFetchKey(identity->fetch_dwords);
      if (identity_fetch_key != 0 &&
          FableDirectTextureFetchKey(link.texture_fetch_dwords) ==
              identity_fetch_key) {
        return true;
      }
    }
    return false;
  };
  auto remember_relation_sample =
      [&](const FableSurfaceDestinationTextureRelation* relation,
          uint32_t destination_texture) -> bool {
    if (!relation || !relation->link.trusted_destination_edge ||
        !DirectSurfaceTextureLinkHasExactCopyIdentity(relation->link) ||
        !link_matches_bound_destination(relation->link, destination_texture)) {
      return false;
    }
    if (relation->link.frame_index != 0 &&
        relation->link.frame_index > g_direct_renderer.current_frame_index) {
      return false;
    }
    if (relation->link.frame_index != 0 &&
        relation->link.frame_index == g_direct_renderer.current_frame_index &&
        relation->link.draw_serial != 0 && draw_serial != 0 &&
        relation->link.draw_serial > draw_serial) {
      return false;
    }
    const bool same_slot =
        relation->link.source_slot == kUnknownSlot ||
        relation->link.source_slot == slot;
    if (!same_slot &&
        !DirectSurfaceTextureLinkTouchesFablePrivateChain(relation->link,
                                                          nullptr)) {
      return false;
    }
    PlumeTrackedResource* sample =
        ResolveDirectSurfaceTextureLinkResourceLocked(command, relation->link);
    if (!sample) {
      return false;
    }
    static uint32_t logged_cross_slot_relation_samples = 0;
    if (!same_slot && logged_cross_slot_relation_samples < 32u) {
      REXLOG_INFO(
          "Refii plume exact relation cross-slot sample #{} frame={} "
          "slot={} link_slot={} guest=0x{:08X} texture=0x{:08X} "
          "base=0x{:08X} surface=0x{:08X} backing=0x{:08X} "
          "sample=0x{:08X}/{}x{} rt=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_cross_slot_relation_samples,
          g_direct_renderer.current_frame_index, slot, relation->link.source_slot,
          guest, relation->link.texture, relation->link.texture_base_address,
          relation->link.source_surface, relation->link.backing_texture,
          sample->guest_address, PlumeGuestTextureWidth(*sample),
          PlumeGuestTextureHeight(*sample), command.resources.render_targets[0],
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return true;
  };
  auto find_relation_by_destination = [&](uint32_t destination_texture) -> bool {
    if (destination_texture == 0) {
      return false;
    }
    if (remember_relation_sample(
            FableDirectFindVisibleSurfaceDestinationRelationForDestinationTexture(
                destination_texture, g_direct_renderer.current_frame_index,
                draw_serial, slot),
            destination_texture)) {
      return true;
    }
    if (remember_relation_sample(
            FableDirectFindSurfaceDestinationRelationForDestinationTextureSlot(
                destination_texture, slot),
            destination_texture)) {
      return true;
    }
    return remember_relation_sample(
        FableDirectFindSurfaceDestinationRelationForDestinationTexture(
            destination_texture),
        destination_texture);
  };
  auto remember_exact_link_by_destination =
      [&](uint32_t destination_texture) -> bool {
    if (destination_texture == 0) {
      return false;
    }
    const FableSurfaceTextureLink* slot_link =
        FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
            destination_texture, slot);
    if (remember_link_sample(slot_link, true)) {
      return true;
    }
    return remember_link_sample(
        FableDirectFindSurfaceTextureLinkForDestinationTexture(
            destination_texture),
        true);
	  };
	  if (find_relation_by_destination(guest)) {
	    return true;
	  }
	  if (remember_exact_link_by_destination(guest)) {
	    return true;
	  }
	  if (command_fetch_info.valid) {
	    if (find_relation_by_destination(command_fetch_info.base_address)) {
	      return true;
	    }
	    if (find_exact_visible_link_by_base(command_fetch_info.base_address)) {
	      return true;
	    }
	  }
	  if (find_exact_visible_link_by_fetch(command_fetch)) {
	    return true;
	  }
	  if (texture) {
	    if (find_relation_by_destination(texture->guest_address)) {
	      return true;
	    }
	    if (remember_exact_link_by_destination(texture->guest_address)) {
	      return true;
	    }
	    if (find_relation_by_destination(texture->parent_address)) {
	      return true;
	    }
	    if (remember_exact_link_by_destination(texture->parent_address)) {
	      return true;
	    }
	    const uint32_t texture_base = PlumeGuestTextureBaseAddress(*texture);
	    if (find_relation_by_destination(texture_base)) {
	      return true;
	    }
	    if (remember_exact_link_by_destination(texture_base)) {
	      return true;
	    }
	    if (find_exact_visible_link_by_base(texture_base)) {
	      return true;
	    }
	    if (find_exact_visible_link_by_fetch(PlumeGuestTextureFetchDwords(
	            *texture))) {
	      return true;
	    }
	  }
	  if (const FableTextureIdentity* identity =
	          FableDirectFindTextureIdentityByObject(guest)) {
	    if (find_relation_by_destination(identity->object_texture)) {
	      return true;
	    }
	    if (remember_exact_link_by_destination(identity->object_texture)) {
	      return true;
	    }
	    if (find_relation_by_destination(identity->parent_texture)) {
	      return true;
	    }
	    if (remember_exact_link_by_destination(identity->parent_texture)) {
	      return true;
	    }
	    if (find_relation_by_destination(identity->base_address)) {
	      return true;
	    }
	    if (remember_exact_link_by_destination(identity->base_address)) {
	      return true;
	    }
	    if (find_exact_visible_link_by_base(identity->base_address)) {
	      return true;
	    }
	    if (find_exact_visible_link_by_fetch(identity->fetch_dwords)) {
	      return true;
	    }
  }
  return false;
}

PlumeTrackedResource* ResolveDirectExactWrapperSourceSampleForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (slot >= command.resources.textures.size() ||
      command.draw.source == RendererDrawEventSource::kPixVideo ||
      !command.draw.private_texture_wrapper_source_exact ||
      command.draw.private_texture_wrapper_source_texture == 0 ||
      (command.draw.private_texture_wrapper_symbol_address == 0 &&
       command.draw.private_texture_wrapper_call_serial == 0 &&
       command.draw.private_texture_wrapper_context_key == 0)) {
    return nullptr;
  }

  const bool private_title_context =
      DirectCommandTextureSlotHasPrivateTitleIdentityLocked(command, slot,
                                                            texture) ||
      DirectCommandUsesFablePrivateTexturePath(command, slot, texture) ||
      DirectCommandUsesPrivateOrTitleSurfaceCopyPathLocked(command) ||
      command.draw.private_texture_rectangle ||
      IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader) ||
      IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
      IsFableTitleSceneVertexShaderCommandLocked(command) ||
      IsFableTitleSceneTextureSyncCommandLocked(command);
  if (!private_title_context) {
    return nullptr;
  }

  PlumeTrackedResource* wrapper_source = FindDirectResourceLocked(
      command.draw.private_texture_wrapper_source_texture);
  const bool binds_current_target =
      wrapper_source != nullptr &&
      DirectCommandBindsResourceAsTargetLocked(command, *wrapper_source);
  const bool exact_current_target_private_rect_alias =
      binds_current_target && command.draw.private_texture_rectangle &&
      FableTrackedResourceHasGpuProducedContent(*wrapper_source) &&
      command.resources.render_targets[0] != 0 &&
      (DirectResourceAddressMatchesLocked(wrapper_source->parent_address,
                                          command.resources.render_targets[0]) ||
       DirectResourceAddressMatchesLocked(wrapper_source->guest_address,
                                          command.resources.render_targets[0]));
  const bool exact_wrapper_feedback_alias =
      !binds_current_target && command.draw.private_texture_rectangle &&
      command.draw.private_texture_wrapper_source_exact &&
      (FableTrackedResourceHasSampleableContent(*wrapper_source) ||
       FableTrackedResourceHasGpuProducedContent(*wrapper_source) ||
       FableTrackedResourceVisibleContentSerial(*wrapper_source) != 0);
  if (!wrapper_source || !IsImageResource(wrapper_source->kind) ||
      !PlumeGuestImageReady(*wrapper_source) ||
      (!exact_current_target_private_rect_alias &&
       !exact_wrapper_feedback_alias &&
       DirectResourceIsPrivateFeedbackAlias(*wrapper_source)) ||
      FableTrackedResourceIsBlankUploadedSampleProxy(*wrapper_source) ||
      (binds_current_target && !exact_current_target_private_rect_alias) ||
      (!FableTrackedResourceHasMeaningfulTextureUpload(*wrapper_source) &&
       !FableTrackedResourceHasSampleableContent(*wrapper_source) &&
       !FableTrackedResourceHasGpuProducedContent(*wrapper_source) &&
       FableTrackedResourceVisibleContentSerial(*wrapper_source) == 0)) {
    return nullptr;
  }

  static uint32_t logged_exact_wrapper_source_private_title_samples = 0;
  if (logged_exact_wrapper_source_private_title_samples < 64u) {
    const uint32_t guest = command.resources.textures[slot];
    REXLOG_INFO(
        "Refii plume exact wrapper private/title source sample #{} frame={} "
        "slot={} guest=0x{:08X} wrapper_source=0x{:08X}/{}x{} "
        "base=0x{:08X} fmt={} content={} wrapper=0x{:08X}/{} "
        "context=0x{:016X} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
        "ps=0x{:08X}",
        ++logged_exact_wrapper_source_private_title_samples,
        g_direct_renderer.current_frame_index, slot, guest,
        wrapper_source->guest_address, PlumeGuestTextureWidth(*wrapper_source),
        PlumeGuestTextureHeight(*wrapper_source),
        PlumeGuestTextureBaseAddress(*wrapper_source),
        static_cast<uint32_t>(PlumeGuestTextureFormat(*wrapper_source)),
        static_cast<unsigned long long>(
            FableTrackedResourceVisibleContentSerial(*wrapper_source)),
        command.draw.private_texture_wrapper_symbol_address,
        static_cast<unsigned long long>(
            command.draw.private_texture_wrapper_call_serial),
        static_cast<unsigned long long>(
            command.draw.private_texture_wrapper_context_key),
        command.resources.render_targets[0], command.resources.depth_stencil,
        command.resources.vertex_shader, command.resources.pixel_shader);
  }
  return wrapper_source;
}

bool DirectCommandTextureSlotDirectResourceProvidesSampleLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture,
    bool allow_private_title_exact_sample) {
  if (slot >= command.resources.textures.size()) {
    return false;
  }

  if (PlumeTrackedResource* wrapper_source =
          ResolveDirectExactWrapperSourceSampleForSlotLocked(command, slot,
                                                             texture)) {
    return true;
  }

  if (!texture || !IsImageResource(texture->kind) ||
      !PlumeGuestImageReady(*texture) ||
      FableTrackedResourceIsBlankUploadedSampleProxy(*texture) ||
      DirectCommandBindsResourceAsTargetLocked(command, *texture)) {
    return false;
  }

  if (IsFableTitleGuiTextAtlasCommandLocked(command, slot)) {
    return FableTitleGuiAtlasResourceHasUpload(
        DirectTitleResourceFactsForResource(*texture));
  }

  if (DirectTitleSyncFetchTextureMatchesCommandLocked(command, slot, *texture)) {
    LogDirectTitleSyncFetchTextureIdentityLocked(
        command, slot, *texture,
        "Refii plume exact title-sync fetch texture identity");
    return true;
  }

  const bool has_content =
      FableTrackedResourceHasMeaningfulTextureUpload(*texture) ||
      FableTrackedResourceHasSampleableContent(*texture) ||
      FableTrackedResourceHasGpuProducedContent(*texture);
  if (PlumeGuestTextureBaseAddress(*texture) == 0x1FAA0000u &&
      PlumeGuestTextureWidth(*texture) == 512u &&
      PlumeGuestTextureHeight(*texture) == 1u &&
      PlumeGuestTextureGuestFormat(*texture) == 36u &&
      command.draw.source != RendererDrawEventSource::kPixVideo &&
      (has_content || FableTrackedResourceVisibleContentSerial(*texture) != 0)) {
    return true;
  }
  if (DirectCommandSlotUsesExactBinkWrapperPlaneLocked(command, slot)) {
    const DirectTextureFetchInfo fetch_info =
        DirectTextureFetchInfoFromDwords(
            DirectCommandTextureFetchDwords(command, slot));
    if (fetch_info.valid &&
        DirectTrackedResourceMatchesExactBinkPlaneSlotLocked(
            command, slot, fetch_info, *texture)) {
      return true;
    }
  }
  if (!has_content) {
    return false;
  }

  const bool header_needs_alias =
      DirectPrivateTextureHeaderNeedsAlias(command, slot, texture);
  const bool private_title_context =
      command.draw.source != RendererDrawEventSource::kPixVideo &&
      (DirectCommandUsesFablePrivateTexturePath(command, slot, texture) ||
       command.draw.private_texture_rectangle ||
       IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader) ||
       IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
       IsFableTitleSceneVertexShaderCommandLocked(command) ||
       IsFableTitleSceneTextureSyncCommandLocked(command));
  if (private_title_context && !allow_private_title_exact_sample) {
    constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
    const uint32_t guest = command.resources.textures[slot];
    const uint32_t texture_base = PlumeGuestTextureBaseAddress(*texture);
    const uint32_t texture_width = PlumeGuestTextureWidth(*texture);
    const uint32_t texture_height = PlumeGuestTextureHeight(*texture);
    const uint32_t texture_guest_format = PlumeGuestTextureGuestFormat(*texture);
    const uint32_t texture_render_format =
        static_cast<uint32_t>(PlumeGuestTextureFormat(*texture));
    const uint64_t command_draw_serial =
        command.draw_serial != 0 ? command.draw_serial : command.serial;
    const uint64_t texture_fetch_key =
        FableDirectTextureFetchKey(PlumeGuestTextureFetchDwords(*texture));
    const std::array<uint32_t, 6> command_fetch =
        DirectCommandTextureFetchDwords(command, slot);
    const uint64_t command_fetch_key = FableDirectTextureFetchKey(command_fetch);
    if (PlumeTrackedResource* exact_title_upload =
            ResolveDirectExactTitleUploadSourceForCommandSlotLocked(
                command, slot, texture)) {
      static uint32_t logged_exact_private_title_upload_texture_identities = 0;
      if (logged_exact_private_title_upload_texture_identities < 32u) {
        REXLOG_INFO(
            "Refii plume exact private/title upload texture identity #{} "
            "frame={} slot={} guest=0x{:08X} tracked=0x{:08X} "
            "base=0x{:08X} size={}x{} fmt={} gen={} "
            "content={} hash=0x{:016X} rt=0x{:08X} ds=0x{:08X} "
            "vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_private_title_upload_texture_identities,
            g_direct_renderer.current_frame_index, slot, guest,
            exact_title_upload->guest_address,
            PlumeGuestTextureBaseAddress(*exact_title_upload),
            PlumeGuestTextureWidth(*exact_title_upload),
            PlumeGuestTextureHeight(*exact_title_upload),
            PlumeGuestTextureGuestFormat(*exact_title_upload),
            static_cast<unsigned long long>(
                exact_title_upload->upload_generation),
            static_cast<unsigned long long>(
                FableTrackedResourceVisibleContentSerial(*exact_title_upload)),
            static_cast<unsigned long long>(
                exact_title_upload->texture_upload_hash),
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
      return true;
    }
    auto exact_destination_link_matches =
        [&](const FableSurfaceTextureLink* link) -> bool {
      if (!link || !link->trusted_destination_edge ||
          !DirectSurfaceTextureLinkHasExactCopyIdentity(*link) ||
          texture->parent_address == 0) {
        return false;
      }
      if (!DirectResourceAddressMatchesLocked(link->texture,
                                              texture->guest_address) &&
          !DirectResourceAddressMatchesLocked(link->texture, guest)) {
        return false;
      }
      if (!DirectResourceAddressMatchesLocked(link->source_surface,
                                              texture->parent_address)) {
        return false;
      }
      if (!DirectSurfaceTextureLinkTouchesFablePrivateChain(*link, texture)) {
        return false;
      }
      if (link->source_slot != kUnknownSlot && link->source_slot != slot &&
          !DirectSurfaceTextureLinkTouchesFablePrivateChain(*link, nullptr)) {
        return false;
      }
      if (link->frame_index != 0 &&
          link->frame_index > g_direct_renderer.current_frame_index) {
        return false;
      }
      if (link->frame_index != 0 &&
          link->frame_index == g_direct_renderer.current_frame_index &&
          link->draw_serial != 0 && command_draw_serial != 0 &&
          link->draw_serial > command_draw_serial) {
        return false;
      }
      if (link->texture_width != 0 && link->texture_width != texture_width) {
        return false;
      }
      if (link->texture_height != 0 && link->texture_height != texture_height) {
        return false;
      }
      if (link->texture_format != 0 &&
          link->texture_format != texture_guest_format &&
          link->texture_format != texture_render_format) {
        return false;
      }
      const uint64_t link_fetch_key =
          FableDirectTextureFetchKey(link->texture_fetch_dwords);
      const bool fetch_matches =
          link_fetch_key != 0 &&
          (link_fetch_key == texture_fetch_key ||
           link_fetch_key == command_fetch_key);
      if (link->texture_base_address != 0 && texture_base != 0 &&
          link->texture_base_address != texture_base && !fetch_matches) {
        return false;
      }
      if (link_fetch_key != 0 && texture_fetch_key != 0 &&
          link_fetch_key != texture_fetch_key &&
          link_fetch_key != command_fetch_key) {
        return false;
      }
      const PlumeTrackedResource* source_surface =
          FindDirectResourceLocked(link->source_surface);
      return link->target_content_serial != 0 ||
             (source_surface &&
              FableTrackedResourceHasGpuProducedContent(*source_surface));
    };
    auto exact_destination_relation_matches =
        [&](const FableSurfaceDestinationTextureRelation* relation) -> bool {
      return relation &&
             (DirectResourceAddressMatchesLocked(relation->destination_texture,
                                                 texture->guest_address) ||
              DirectResourceAddressMatchesLocked(relation->destination_texture,
                                                 guest)) &&
             DirectResourceAddressMatchesLocked(relation->source_surface,
                                                texture->parent_address) &&
             exact_destination_link_matches(&relation->link);
    };
    auto remember_exact_destination_sample =
        [&](const FableSurfaceTextureLink& link) {
      static uint32_t logged_exact_private_title_destination_samples = 0;
      if (logged_exact_private_title_destination_samples < 32u) {
        REXLOG_INFO(
            "Refii plume exact private/title destination sample #{} frame={} "
            "slot={} link_slot={} guest=0x{:08X} texture=0x{:08X}/{}x{} "
            "base=0x{:08X} parent=0x{:08X} surface=0x{:08X} "
            "target_serial={} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
            "ps=0x{:08X}",
            ++logged_exact_private_title_destination_samples,
            g_direct_renderer.current_frame_index, slot, link.source_slot,
            guest, texture->guest_address, texture_width, texture_height,
            texture_base, texture->parent_address, link.source_surface,
            static_cast<unsigned long long>(link.target_content_serial),
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
    };
    auto try_exact_destination_sample = [&](uint32_t destination_texture) {
      if (destination_texture == 0) {
        return false;
      }
      if (const FableSurfaceDestinationTextureRelation* relation =
              FableDirectFindVisibleSurfaceDestinationRelationForDestinationTexture(
                  destination_texture, g_direct_renderer.current_frame_index,
                  command_draw_serial, slot);
          exact_destination_relation_matches(relation)) {
        remember_exact_destination_sample(relation->link);
        return true;
      }
      if (const FableSurfaceDestinationTextureRelation* relation =
              FableDirectFindSurfaceDestinationRelationForDestinationTextureSlot(
                  destination_texture, slot);
          exact_destination_relation_matches(relation)) {
        remember_exact_destination_sample(relation->link);
        return true;
      }
      if (const FableSurfaceDestinationTextureRelation* relation =
              FableDirectFindSurfaceDestinationRelationForDestinationTexture(
                  destination_texture);
          exact_destination_relation_matches(relation)) {
        remember_exact_destination_sample(relation->link);
        return true;
      }
      if (const FableSurfaceTextureLink* link =
              FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
                  destination_texture, g_direct_renderer.current_frame_index,
                  command_draw_serial, slot);
          exact_destination_link_matches(link)) {
        remember_exact_destination_sample(*link);
        return true;
      }
      if (const FableSurfaceTextureLink* link =
              FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
                  destination_texture, slot);
          exact_destination_link_matches(link)) {
        remember_exact_destination_sample(*link);
        return true;
      }
      if (const FableSurfaceTextureLink* link =
              FableDirectFindSurfaceTextureLinkForDestinationTexture(
                  destination_texture);
          exact_destination_link_matches(link)) {
        remember_exact_destination_sample(*link);
        return true;
      }
      return false;
    };
    if (try_exact_destination_sample(texture->guest_address) ||
        try_exact_destination_sample(guest)) {
      return true;
    }


    // The texture-fetch-resource-link sample route was deleted 2026-07-18
    // (migration step 3): the link maps' only writer was census-dead, so
    // every finder here returned null for all recent runs.

    const bool private_chain_resource =
        IsFablePrivateTextureChainResource(*texture);
    const bool upload_defined_texture_identity =
        DirectResourceUploadDefinesTextureIdentity(*texture);
	    const bool tracked_scene_output =
	        DirectResourceIsTrackedSceneOutputLocked(*texture);
	    if ((private_chain_resource || upload_defined_texture_identity ||
	         tracked_scene_output) &&
	        FableTrackedResourceVisibleContentSerial(*texture) != 0 &&
	        (texture->parent_address != 0 || upload_defined_texture_identity ||
	         tracked_scene_output) &&
	        !DirectCommandBindsResourceAsTargetLocked(command, *texture)) {
      const bool exact_registered_direct_producer =
          DirectCommandTextureSlotHasExactRegisteredProducerLocked(
              command, slot, texture, false);
      const bool exact_producer_direct_owner =
          !exact_registered_direct_producer &&
          DirectCommandTextureSlotExactProducerProvidesSampleLocked(command, slot,
                                                                   texture);
      if (exact_registered_direct_producer || exact_producer_direct_owner) {
        static uint32_t logged_exact_private_title_direct_producer_samples = 0;
        if (logged_exact_private_title_direct_producer_samples < 32u) {
          const std::array<uint32_t, 6> dwords =
              DirectCommandTextureFetchDwords(command, slot);
          const char* role = exact_registered_direct_producer
                                 ? "registered-producer"
                                 : "producer-owner";
          const char* proof = exact_registered_direct_producer
                                  ? "exact-registered-producer"
                                  : "exact-producer-owner";
          const char* identity_log_name =
              exact_registered_direct_producer
                  ? "Refii plume exact private/title registered producer "
                    "texture identity"
                  : "Refii plume exact private/title producer-owned texture "
                    "identity";
          REXLOG_INFO(
              "{} #{} "
              "role={} frame={} serial={} draw={} slot={} "
              "guest=0x{:08X} tracked=0x{:08X}/{}x{} "
              "base=0x{:08X} fmt={} guest_fmt={} parent=0x{:08X} "
              "upload_gen={} content={} proof={} link_serial={} "
              "link_surface=0x{:08X} link_call={} link_context=0x{:016X} "
              "link_symbol=0x{:08X} link_kind={} rt=0x{:08X} ds=0x{:08X} "
              "vs=0x{:08X} ps=0x{:08X} "
              "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
              identity_log_name,
              ++logged_exact_private_title_direct_producer_samples, role,
              g_direct_renderer.current_frame_index,
              static_cast<unsigned long long>(command.serial),
              static_cast<unsigned long long>(command.draw_serial), slot,
              command.resources.textures[slot], texture->guest_address,
              texture_width, texture_height, texture_base,
              texture_render_format, texture_guest_format,
              texture->parent_address,
              static_cast<unsigned long long>(texture->upload_generation),
              static_cast<unsigned long long>(
                  FableTrackedResourceVisibleContentSerial(*texture)),
              proof, 0ull,
              texture->parent_address != 0 ? texture->parent_address
                                           : texture->guest_address,
              0ull, 0ull, 0u,
              FablePrivateTextureCopyKindName(
                  FablePrivateTextureCopyKind::kColor),
              command.resources.render_targets[0],
              command.resources.depth_stencil, command.resources.vertex_shader,
              command.resources.pixel_shader, dwords[0], dwords[1], dwords[2],
              dwords[3], dwords[4], dwords[5]);
        }
        return true;
      }
	      const FableSurfaceTextureLink* producer_link = nullptr;
	      auto producer_link_matches_direct_resource =
	          [&](const FableSurfaceTextureLink* link) -> bool {
	        if (!link) {
	          return false;
	        }
	        const bool wrapper_visible =
	            DirectSurfaceTextureLinkPrivateWrapperVisibleToCommandLocked(
	                command, *link);
	        const uint64_t link_fetch_key =
	            FableDirectTextureFetchKey(link->texture_fetch_dwords);
	        const bool fetch_matches =
	            link_fetch_key != 0 &&
	            (link_fetch_key == texture_fetch_key ||
	             link_fetch_key == command_fetch_key);
	        const bool base_matches =
	            link->texture_base_address != 0 && texture_base != 0 &&
	            link->texture_base_address == texture_base;
	        const bool ida_private_rect_producer =
	            DirectIdaPrivateRectWrapperProofName(link->copy_symbol_address) !=
	            nullptr;
	        const bool beginvertices_title_parent_producer =
	            link->copy_symbol_address == kFableBeginVerticesTitleWrapperSymbol &&
	            texture->parent_address != 0 &&
	            DirectResourceAddressMatchesLocked(link->source_surface,
	                                               texture->parent_address);
	        const bool exact_wrapper_backing_identity =
	            (ida_private_rect_producer || beginvertices_title_parent_producer) &&
	            (base_matches || fetch_matches) &&
	            (link->texture_width == 0 ||
	             link->texture_width == texture_width) &&
	            (link->texture_height == 0 ||
	             link->texture_height == texture_height);
	        const bool historical_exact_wrapper_producer =
	            exact_wrapper_backing_identity &&
	            (link->draw_serial == 0 || command_draw_serial == 0 ||
	             link->draw_serial <= command_draw_serial);
	        if (!wrapper_visible && !historical_exact_wrapper_producer) {
	          return false;
	        }
	        if (link->texture != 0 &&
	            !DirectResourceAddressMatchesLocked(link->texture,
	                                                texture->guest_address) &&
	            !DirectResourceAddressMatchesLocked(link->texture, guest) &&
	            !exact_wrapper_backing_identity) {
	          return false;
	        }
	        if (link->texture_base_address != 0 && texture_base != 0 &&
	            !base_matches && !fetch_matches) {
	          return false;
	        }
	        if (link->texture_width != 0 && link->texture_width != texture_width) {
	          return false;
	        }
	        if (link->texture_height != 0 &&
	            link->texture_height != texture_height) {
	          return false;
	        }
	        if (link->texture_format != 0 &&
	            link->texture_format != texture_guest_format &&
	            link->texture_format != texture_render_format &&
	            !exact_wrapper_backing_identity) {
	          return false;
	        }
	        if (link->frame_index != 0 &&
	            link->frame_index > g_direct_renderer.current_frame_index) {
	          return false;
	        }
	        if (link->frame_index != 0 &&
	            link->frame_index == g_direct_renderer.current_frame_index &&
	            link->draw_serial != 0 && command_draw_serial != 0 &&
	            link->draw_serial > command_draw_serial) {
	          return false;
	        }
	        const bool exact_native_depth_bridge =
	            link->kind == FablePrivateTextureCopyKind::kDepth &&
	            ida_private_rect_producer && link->source_surface != 0 &&
	            DirectResourceAddressMatchesLocked(link->source_surface,
	                                               texture->parent_address) &&
	            FableTrackedResourceVisibleContentSerial(*texture) != 0;
	        return link->target_content_serial != 0 ||
	               DirectSurfaceTextureLinkHasExactCopyPayloadIdentity(*link) ||
	               DirectSurfaceTextureLinkHasExactWrapperProducerSurfaceIdentity(
	                   *link) ||
	               exact_native_depth_bridge;
	      };
	      auto try_producer_link = [&](const FableSurfaceTextureLink* link) {
	        if (producer_link_matches_direct_resource(link)) {
	          producer_link = link;
	          return true;
	        }
	        return false;
	      };
	      auto try_producer_links =
	          [&](const std::vector<FableSurfaceTextureLink>* links) {
	        if (!links) {
	          return false;
	        }
	        for (const FableSurfaceTextureLink& link : *links) {
	          if (try_producer_link(&link)) {
	            return true;
	          }
	        }
	        return false;
	      };
	      if (!try_producer_link(
	              FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
	                  texture->guest_address, g_direct_renderer.current_frame_index,
	                  command_draw_serial, slot)) &&
	          !try_producer_link(
	              FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
	                  texture->guest_address, slot)) &&
	          !try_producer_link(
	              FableDirectFindSurfaceTextureLinkForDestinationTexture(
	                  texture->guest_address)) &&
	          !try_producer_link(
	              FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
	                  guest, g_direct_renderer.current_frame_index,
	                  command_draw_serial, slot)) &&
	          !try_producer_link(
	              FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
	                  guest, slot)) &&
	          !try_producer_link(
	              FableDirectFindSurfaceTextureLinkForDestinationTexture(guest)) &&
	          !try_producer_links(
	              FableDirectFindSurfaceTextureLinksForDestinationTexture(
	                  texture->guest_address)) &&
		          !try_producer_links(
		              FableDirectFindSurfaceTextureLinksForDestinationTexture(guest)) &&
		          !try_producer_links(
		              FableDirectFindSurfaceTextureLinksByTexture(
		                  texture->guest_address)) &&
		          !try_producer_links(FableDirectFindSurfaceTextureLinksByTexture(
		              guest)) &&
		          !try_producer_link(
		              FableDirectFindVisibleSurfaceTextureLinkForSourceSurface(
		                  texture->parent_address,
		                  g_direct_renderer.current_frame_index, command_draw_serial,
		                  slot)) &&
		          !try_producer_link(FableDirectFindSurfaceTextureLinkForSourceSurfaceSlot(
		              texture->parent_address, slot)) &&
		          !try_producer_link(
		              FableDirectFindSurfaceTextureLinkForSourceSurface(
		                  texture->parent_address)) &&
		          !try_producer_links(FableDirectFindSurfaceTextureLinksBySurface(
		              texture->parent_address)) &&
			          !try_producer_link(FableDirectFindVisibleSurfaceTextureLinkForBase(
			              texture_base, g_direct_renderer.current_frame_index,
			              command_draw_serial, slot)) &&
		          !try_producer_link(
		              FableDirectFindSurfaceTextureLinkForBase(texture_base)) &&
		          !try_producer_links(
		              FableDirectFindSurfaceTextureLinksByBase(texture_base)) &&
		          !try_producer_link(FableDirectFindVisibleSurfaceTextureLinkForFetch(
		              command_fetch, g_direct_renderer.current_frame_index,
		              command_draw_serial, slot)) &&
		          !try_producer_link(
		              FableDirectFindSurfaceTextureLinkForFetch(command_fetch)) &&
		          !try_producer_links(
		              FableDirectFindSurfaceTextureLinksByFetch(command_fetch)) &&
		          !try_producer_links(FableDirectFindSurfaceTextureLinksByFetch(
		              PlumeGuestTextureFetchDwords(*texture)))) {
	        static uint32_t logged_exact_private_title_direct_resource_missing_proof =
	            0;
	        if (logged_exact_private_title_direct_resource_missing_proof < 32u) {
	          REXLOG_INFO(
	              "Refii plume exact private/title direct texture identity missing "
	              "producer proof #{} frame={} serial={} draw={} slot={} "
	              "guest=0x{:08X} tracked=0x{:08X}/{}x{} base=0x{:08X} "
	              "fmt={} guest_fmt={} parent=0x{:08X} content={} rt=0x{:08X} "
	              "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
	              ++logged_exact_private_title_direct_resource_missing_proof,
	              g_direct_renderer.current_frame_index,
	              static_cast<unsigned long long>(command.serial),
	              static_cast<unsigned long long>(command.draw_serial), slot,
	              command.resources.textures[slot], texture->guest_address,
	              texture_width, texture_height, texture_base,
	              texture_render_format, texture_guest_format,
	              texture->parent_address,
	              static_cast<unsigned long long>(
	                  FableTrackedResourceVisibleContentSerial(*texture)),
	              command.resources.render_targets[0],
	              command.resources.depth_stencil, command.resources.vertex_shader,
	              command.resources.pixel_shader);
	        }
	        return false;
	      }
	      static uint32_t logged_exact_private_title_direct_resource_samples = 0;
	      if (logged_exact_private_title_direct_resource_samples < 32u) {
	        const std::array<uint32_t, 6> dwords =
	            DirectCommandTextureFetchDwords(command, slot);
	        const char* role =
	            private_chain_resource
	                ? "surface-linked-resource"
	                : (tracked_scene_output ? "scene-output" : "direct-upload");
	        const char* identity_log_name =
	            private_chain_resource
	                ? "Refii plume exact private/title surface-linked texture "
	                  "identity"
	                : "Refii plume exact private/title direct texture identity";
	        const char* proof =
	            DirectIdaPrivateRectWrapperProofName(
	                producer_link->copy_symbol_address);
	        if (!proof &&
	            producer_link->copy_symbol_address ==
	                kFableBeginVerticesTitleWrapperSymbol) {
	          proof = "ida-beginvertices-title-current-target";
	        }
	        if (!proof) {
	          proof = "registered-surface-link";
	        }
	        REXLOG_INFO(
	            "{} #{} "
	            "role={} frame={} serial={} draw={} slot={} "
	            "guest=0x{:08X} tracked=0x{:08X}/{}x{} "
	            "base=0x{:08X} fmt={} guest_fmt={} parent=0x{:08X} "
	            "upload_gen={} content={} proof={} link_serial={} "
	            "link_surface=0x{:08X} link_call={} link_context=0x{:016X} "
	            "link_symbol=0x{:08X} link_kind={} rt=0x{:08X} ds=0x{:08X} "
	            "vs=0x{:08X} ps=0x{:08X} "
	            "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
	            identity_log_name,
	            ++logged_exact_private_title_direct_resource_samples, role,
	            g_direct_renderer.current_frame_index,
	            static_cast<unsigned long long>(command.serial),
	            static_cast<unsigned long long>(command.draw_serial), slot,
            command.resources.textures[slot], texture->guest_address,
            PlumeGuestTextureWidth(*texture), PlumeGuestTextureHeight(*texture),
	            texture_base, texture_render_format, texture_guest_format,
	            texture->parent_address,
	            static_cast<unsigned long long>(texture->upload_generation),
	            static_cast<unsigned long long>(
	                FableTrackedResourceVisibleContentSerial(*texture)),
	            proof, static_cast<unsigned long long>(producer_link->serial),
	            producer_link->source_surface,
	            static_cast<unsigned long long>(producer_link->copy_call_serial),
	            static_cast<unsigned long long>(producer_link->copy_context_key),
	            producer_link->copy_symbol_address,
	            FablePrivateTextureCopyKindName(producer_link->kind),
	            command.resources.render_targets[0], command.resources.depth_stencil,
	            command.resources.vertex_shader, command.resources.pixel_shader,
	            dwords[0], dwords[1], dwords[2], dwords[3], dwords[4],
	            dwords[5]);
	      }
      return true;
    }

    const bool content_backed_non_private_title_texture =
        !DirectCommandUsesFablePrivateTexturePath(command, slot, texture) &&
        !header_needs_alias &&
        !DirectCommandTextureSlotHasPrivateTitleIdentityLocked(command, slot,
                                                               texture) &&
        FableTrackedResourceVisibleContentSerial(*texture) != 0 &&
        !DirectResourceIsPrivateFeedbackAlias(*texture);
    if (content_backed_non_private_title_texture) {
      static uint32_t logged_exact_private_title_direct_content_samples = 0;
      if (logged_exact_private_title_direct_content_samples < 32u) {
        const std::array<uint32_t, 6> dwords =
            DirectCommandTextureFetchDwords(command, slot);
        REXLOG_INFO(
            "Refii plume exact private/title direct texture identity #{} "
            "role=direct-content frame={} serial={} draw={} slot={} "
            "guest=0x{:08X} tracked=0x{:08X}/{}x{} base=0x{:08X} "
            "fmt={} guest_fmt={} parent=0x{:08X} upload_gen={} "
            "content={} proof=content-backed-title-resource link_serial={} "
            "link_surface=0x{:08X} link_call={} link_context=0x{:016X} "
            "link_symbol=0x{:08X} link_kind={} rt=0x{:08X} ds=0x{:08X} "
            "vs=0x{:08X} ps=0x{:08X} "
            "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
            ++logged_exact_private_title_direct_content_samples,
            g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(command.serial),
            static_cast<unsigned long long>(command.draw_serial), slot,
            command.resources.textures[slot], texture->guest_address,
            texture_width, texture_height, texture_base,
            texture_render_format, texture_guest_format,
            texture->parent_address,
            static_cast<unsigned long long>(texture->upload_generation),
            static_cast<unsigned long long>(
                FableTrackedResourceVisibleContentSerial(*texture)),
            0ull,
            texture->parent_address != 0 ? texture->parent_address
                                         : texture->guest_address,
            0ull, 0ull, 0u,
            FablePrivateTextureCopyKindName(FablePrivateTextureCopyKind::kColor),
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader,
            dwords[0], dwords[1], dwords[2], dwords[3], dwords[4],
            dwords[5]);
      }
      return true;
    }

    static uint32_t logged_exact_private_title_direct_resource_rejects = 0;
    if (logged_exact_private_title_direct_resource_rejects < 32u) {
      REXLOG_INFO(
          "Refii plume exact private/title direct-resource sample reject #{} "
          "frame={} slot={} guest=0x{:08X} tracked=0x{:08X}/{}x{} "
          "base=0x{:08X} fmt={} guest_fmt={} parent=0x{:08X} "
          "upload_gen={} content={} rt=0x{:08X} ds=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_private_title_direct_resource_rejects,
          g_direct_renderer.current_frame_index, slot,
          command.resources.textures[slot], texture->guest_address,
          PlumeGuestTextureWidth(*texture), PlumeGuestTextureHeight(*texture),
          texture_base, texture_render_format, texture_guest_format,
          texture->parent_address,
          static_cast<unsigned long long>(texture->upload_generation),
          static_cast<unsigned long long>(texture->content_serial),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return false;
  }

  if (header_needs_alias) {
    return false;
  }

  return true;
}


void AddDirectCompactPrivateTextureCopyActionResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FablePrivateTextureCopyAction& action) {
  AddDirectResourceInterest(resource_filter, action.target_surface);
  AddDirectResourceInterest(texture_upload_filter, action.target_surface);

  if (action.backing_texture != 0 || action.backing_base_address != 0 ||
      FableDirectTextureFetchKey(action.backing_fetch_dwords) != 0) {
    AddDirectResourceInterest(resource_filter, action.backing_texture);
    AddDirectResourceInterest(texture_upload_filter, action.backing_texture);
    AddDirectTextureBaseAddressResourceInterestsLocked(
        resource_filter, texture_upload_filter, action.backing_base_address);
    AddDirectTextureFetchResourceInterestsLocked(
        resource_filter, texture_upload_filter, action.backing_fetch_dwords);
    return;
  }

  AddDirectResourceInterest(resource_filter, action.source_texture);
  AddDirectResourceInterest(texture_upload_filter, action.source_texture);
  AddDirectTextureBaseAddressResourceInterestsLocked(
      resource_filter, texture_upload_filter, action.source_base_address);
  AddDirectTextureFetchResourceInterestsLocked(
      resource_filter, texture_upload_filter, action.source_fetch_dwords);
}

const FablePrivateTextureCopyAction*
FindDirectPrivateTextureCopyOutputActionForClearTargetLocked(
    const FableDirectRenderCommand& command, uint32_t target,
    bool depth_target) {
  if (target == 0) {
    return nullptr;
  }
  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  const FablePrivateTextureCopyAction* action =
      FableDirectFindVisiblePrivateTextureCopyOutputActionForDestinationTexture(
          target, g_direct_renderer.current_frame_index, command.serial,
          kUnknownSlot);
  if (!action) {
    return nullptr;
  }
  if (depth_target) {
    return action->kind == FablePrivateTextureCopyKind::kDepth ? action
                                                               : nullptr;
  }
  return action->kind != FablePrivateTextureCopyKind::kDepth ? action
                                                             : nullptr;
}

bool IsFablePrivateTextureRectangleSetupClearLr(uint32_t lr) {
  switch (lr) {
    case 0x82279370u:  // sub_82278F38: setup clear before private rect helper.
    case 0x8228AC84u:  // sub_8228AB70: setup wrapper around private rect.
    case 0x8228AE04u:  // sub_8228ACB8: setup/flush wrapper around private rect.
      return true;
    default:
      return false;
  }
}

bool DirectPrivateTextureSetupActionTargetExactMatchesLocked(
    uint32_t setup_target, const FablePrivateTextureCopyAction& action,
    bool depth_target) {
  if (setup_target == 0) {
    return false;
  }
  auto matches = [&](uint32_t action_target) {
    return action_target != 0 &&
           DirectResourceAddressMatchesLocked(setup_target, action_target);
  };
  if (depth_target) {
    return matches(action.target_surface) || matches(action.depth_stencil);
  }
  return matches(action.target_surface) || matches(action.render_target);
}

bool DirectPrivateTextureSetupClearIntentMatchesActionTargetLocked(
    const DirectPrivateTextureSetupClearIntent& intent,
    const FablePrivateTextureCopyAction& action) {
  return DirectPrivateTextureSetupActionTargetExactMatchesLocked(
      intent.target, action, intent.depth_target);
}

bool DirectPrivateTextureSetupClearActionMatchesActionTargetLocked(
    const DirectPrivateTextureSetupClearAction& setup_action,
    uint32_t target) {
  return DirectPrivateTextureSetupActionTargetExactMatchesLocked(
      target, setup_action.action, setup_action.depth_target);
}

bool DirectPrivateTextureSetupClearIntentHasExactRoots(
    const DirectPrivateTextureSetupClearIntent& intent) {
  return intent.clear_root_call_serial != 0 &&
         intent.clear_root_context_key != 0 &&
         intent.clear_root_symbol_address != 0 &&
         intent.target_root_call_serial != 0 &&
         intent.target_root_context_key != 0 &&
         intent.target_root_symbol_address != 0;
}

bool DirectPrivateTextureCopyActionHasCompleteWrapperIdentity(
    const FablePrivateTextureCopyAction& action) {
  return action.wrapper_call_serial != 0 &&
         action.wrapper_context_key != 0 &&
         action.wrapper_symbol_address != 0;
}

bool DirectPrivateTextureCopyActionHasCompleteIdaWrapperIdentity(
    const FablePrivateTextureCopyAction& action) {
  return DirectPrivateTextureCopyActionHasCompleteWrapperIdentity(action) &&
         DirectIdaSurfaceTextureWrapperProofSymbol(
             action.wrapper_symbol_address);
}

uint64_t DirectPrivateTextureSetupClearIntentKey(
    const FableDirectRenderCommand& command, uint32_t target,
    bool depth_target) {
  uint64_t key = 1469598103934665603ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };
  mix(g_direct_renderer.current_frame_index);
  mix(command.serial);
  mix(command.draw_serial);
  mix(command.draw.state_flush_lr);
  mix(target);
  mix(depth_target ? 1u : 0u);
  mix(command.clear.root_call_serial);
  mix(command.clear.root_context_key);
  mix(command.clear.root_symbol_address);
  return key;
}

bool DirectPrivateTextureSetupClearActionMatchesLocked(
    const DirectPrivateTextureSetupClearAction& setup_action,
    const FableDirectRenderCommand& command, uint32_t target,
    bool depth_target) {
  if (target == 0 || setup_action.action.serial == 0 ||
      setup_action.depth_target != depth_target ||
      setup_action.setup_lr != command.draw.state_flush_lr ||
      !IsFablePrivateTextureRectangleSetupClearLr(setup_action.setup_lr)) {
    return false;
  }
  if (setup_action.setup_serial != command.serial) {
    return false;
  }
  if (!DirectResourceAddressMatchesLocked(target, setup_action.setup_target)) {
    return false;
  }
  if (!DirectPrivateTextureSetupClearActionMatchesActionTargetLocked(
          setup_action, target)) {
    return false;
  }

  if (command.clear.root_call_serial == 0 ||
      command.clear.root_context_key == 0 ||
      setup_action.clear_root_call_serial != command.clear.root_call_serial ||
      setup_action.clear_root_context_key != command.clear.root_context_key ||
      setup_action.clear_root_symbol_address !=
          command.clear.root_symbol_address) {
    return false;
  }

  const FableDirectStateRoot* command_target_root = nullptr;
  if (depth_target) {
    command_target_root = &command.clear.target_roots.depth;
  } else {
    for (uint32_t slot = 0; slot < command.resources.render_targets.size();
         ++slot) {
      if (command.resources.render_targets[slot] == target) {
        command_target_root = &command.clear.target_roots.color[slot];
        break;
      }
    }
  }
  if (!command_target_root || command_target_root->call_serial == 0 ||
      command_target_root->context_key == 0 ||
      setup_action.target_root_call_serial !=
          command_target_root->call_serial ||
      setup_action.target_root_context_key != command_target_root->context_key ||
      setup_action.target_root_symbol_address !=
          command_target_root->symbol_address) {
    return false;
  }

  const uint64_t command_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  if (setup_action.setup_draw_serial != 0 && command_serial != 0 &&
      setup_action.setup_draw_serial > command_serial) {
    return false;
  }
  return setup_action.action.frame_index == 0 ||
         setup_action.action.frame_index >= setup_action.setup_frame_index;
}

void RegisterDirectPrivateTextureSetupClearSurfaceLinkLocked(
    const DirectPrivateTextureSetupClearIntent& intent,
    const FablePrivateTextureCopyAction& action, uint32_t action_target) {
  if (intent.depth_target ||
      action.kind == FablePrivateTextureCopyKind::kDepth ||
      intent.target == 0 || action_target == 0) {
    return;
  }
  if (!DirectPrivateTextureSetupClearIntentHasExactRoots(intent) ||
      !DirectPrivateTextureCopyActionHasCompleteIdaWrapperIdentity(action)) {
    static uint32_t logged_exact_setup_clear_private_link_root_rejects = 0;
    if (logged_exact_setup_clear_private_link_root_rejects < 32u) {
      REXLOG_INFO(
          "Refii plume exact setup-clear private link root reject #{} "
          "setup_frame={} action_frame={} setup_target=0x{:08X} "
          "action_target=0x{:08X} clear_root=0x{:08X}/{} "
          "target_root=0x{:08X}/{} wrapper=0x{:08X}/{} "
          "context=0x{:016X}",
          ++logged_exact_setup_clear_private_link_root_rejects,
          intent.frame_index,
          action.frame_index, intent.target, action_target,
          intent.clear_root_symbol_address,
          static_cast<unsigned long long>(intent.clear_root_call_serial),
          intent.target_root_symbol_address,
          static_cast<unsigned long long>(intent.target_root_call_serial),
          action.wrapper_symbol_address,
          static_cast<unsigned long long>(action.wrapper_call_serial),
          static_cast<unsigned long long>(action.wrapper_context_key));
    }
    return;
  }

  const bool use_source_identity =
      action.source_texture != 0 || action.source_base_address != 0 ||
      FableDirectTextureFetchKey(action.source_fetch_dwords) != 0;
  const bool use_backing_identity =
      !use_source_identity &&
      (action.backing_texture != 0 || action.backing_base_address != 0 ||
       FableDirectTextureFetchKey(action.backing_fetch_dwords) != 0);
  if (!use_source_identity && !use_backing_identity) {
    return;
  }

  FableSurfaceTextureLink link;
  link.source_slot = action.source_slot;
  link.copy_call_serial = action.wrapper_call_serial;
  link.copy_context_key = action.wrapper_context_key;
  link.copy_symbol_address = action.wrapper_symbol_address;
  link.trusted_destination_edge = true;
  link.texture =
      use_source_identity ? action.source_texture : action.backing_texture;
  link.texture_base_address = use_source_identity
                                  ? action.source_base_address
                                  : action.backing_base_address;
  link.texture_width =
      use_source_identity ? action.source_width : action.backing_width;
  link.texture_height =
      use_source_identity ? action.source_height : action.backing_height;
  link.texture_format =
      use_source_identity ? action.source_format : action.backing_format;
  link.texture_fetch_dwords = use_source_identity
                                  ? action.source_fetch_dwords
                                  : action.backing_fetch_dwords;
  link.source_surface = intent.target;
  link.target_content_serial = action.target_content_serial;
  link.render_target = action.render_target != 0 ? action.render_target
                                                 : action_target;
  link.depth_stencil = action.depth_stencil;
  link.vertex_shader = action.vertex_shader;
  link.pixel_shader = action.pixel_shader;
  PopulateDirectSurfaceTextureLinkShaderRoots(link, action);
  link.frame_index = action.frame_index;
  link.draw_serial = action.draw_serial;
  link.wrapper_source_texture = action.wrapper_source_texture;
  link.wrapper_source_exact = action.wrapper_source_exact;
  link.wrapper_setup_globals = action.wrapper_setup_globals;
  link.kind = action.kind;
  if (action.backing_texture != 0 || action.backing_base_address != 0 ||
      FableDirectTextureFetchKey(action.backing_fetch_dwords) != 0) {
    link.backing_texture = action.backing_texture;
    link.backing_base_address = action.backing_base_address;
    link.backing_fetch_dwords = action.backing_fetch_dwords;
    link.backing_content_serial = action.backing_content_serial;
  }
  if (action.sampled_source.kind != FableRegisteredSampleSourceKind::kUnknown) {
    link.sampled_source = action.sampled_source;
  } else if (link.backing_texture != 0 || link.backing_base_address != 0 ||
             FableDirectTextureFetchKey(link.backing_fetch_dwords) != 0) {
    link.sampled_source.kind = FableRegisteredSampleSourceKind::kBackingTexture;
    link.sampled_source.resource = link.backing_texture;
    link.sampled_source.base_address = link.backing_base_address;
    link.sampled_source.fetch_dwords = link.backing_fetch_dwords;
    link.sampled_source.content_serial = link.backing_content_serial;
  }

  if ((link.texture == 0 && link.texture_base_address == 0 &&
       FableDirectTextureFetchKey(link.texture_fetch_dwords) == 0) ||
      link.texture == link.source_surface) {
    return;
  }

  if (!DirectPrivateTextureSetupClearIntentMatchesActionTargetLocked(intent,
                                                                     action)) {
    return;
  }

  uint64_t key = 1469598103934665603ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };
  mix(intent.frame_index);
  mix(intent.serial);
  mix(intent.target);
  mix(intent.clear_root_call_serial);
  mix(intent.clear_root_context_key);
  mix(intent.target_root_call_serial);
  mix(intent.target_root_context_key);
  mix(action.serial);
  mix(action.frame_index);
  mix(action.wrapper_call_serial);
  mix(action.wrapper_context_key);
  mix(action.wrapper_symbol_address);
  mix(action.wrapper_source_texture);
  mix(action.wrapper_source_exact ? 1u : 0u);
  for (uint32_t setup_global : link.wrapper_setup_globals) {
    mix(setup_global);
  }
  mix(link.texture);
  mix(link.texture_base_address);
  mix(FableDirectTextureFetchKey(link.texture_fetch_dwords));
  mix(link.source_surface);
  mix(action_target);
  mix(static_cast<uint32_t>(link.kind));
  if (!g_direct_renderer.private_texture_setup_clear_surface_link_keys
           .insert(key)
           .second) {
    return;
  }

  RegisterDirectSurfaceTextureLinkPreparedLocked(link, "setup-clear-private");
  if (DirectSurfaceTextureLinkShouldPublishObjectMirror(link)) {
    FableSurfaceTextureLink object_link = link;
    object_link.serial = 0;
    object_link.source_slot = 0xFFFFFFFFu;
    RegisterDirectSurfaceTextureLinkPreparedLocked(
        object_link, "setup-clear-private-object");
  }
  constexpr size_t kMaxSetupClearSurfaceLinkKeys = 512;
  if (g_direct_renderer.private_texture_setup_clear_surface_link_keys.size() >
      kMaxSetupClearSurfaceLinkKeys) {
    g_direct_renderer.private_texture_setup_clear_surface_link_keys.clear();
  }

  if (g_direct_renderer.diagnostics.logged_private_rect_setup_clear_surface_links < 32u) {
    REXLOG_INFO(
        "Refii plume private-rect setup clear surface link #{} "
        "setup_frame={} action_frame={} setup_target=0x{:08X} "
        "action_target=0x{:08X} distance={} exact=1 texture=0x{:08X}/{}x{} "
        "base=0x{:08X} surface=0x{:08X} backing=0x{:08X} "
        "kind={} sample={} action_serial={} call={} context=0x{:016X} "
        "symbol=0x{:08X} setup={:08X}/{:08X}/{:08X}/{:08X}",
        ++g_direct_renderer.diagnostics.logged_private_rect_setup_clear_surface_links,
        intent.frame_index, action.frame_index, intent.target, action_target, 0u,
        link.texture, link.texture_width, link.texture_height,
        link.texture_base_address, link.source_surface, link.backing_texture,
        FablePrivateTextureCopyKindName(link.kind),
        FableRegisteredSampleSourceKindName(link.sampled_source.kind),
        static_cast<unsigned long long>(action.serial),
        static_cast<unsigned long long>(link.copy_call_serial),
        static_cast<unsigned long long>(link.copy_context_key),
        link.copy_symbol_address, link.wrapper_setup_globals[0],
        link.wrapper_setup_globals[1], link.wrapper_setup_globals[2],
        link.wrapper_setup_globals[3]);
  }
}

void RememberDirectPrivateTextureSetupClearIntentLocked(
    const FableDirectRenderCommand& command, uint32_t target,
    bool depth_target) {
  if (target == 0 ||
      !IsFablePrivateTextureRectangleSetupClearLr(
          command.draw.state_flush_lr)) {
    return;
  }
  const uint64_t key =
      DirectPrivateTextureSetupClearIntentKey(command, target, depth_target);
  if (!g_direct_renderer.private_texture_setup_clear_intent_keys.insert(key)
           .second) {
    return;
  }

  DirectPrivateTextureSetupClearIntent intent;
  intent.frame_index = g_direct_renderer.current_frame_index;
  intent.serial = command.serial;
  intent.draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  intent.lr = command.draw.state_flush_lr;
  intent.target = target;
  intent.depth_target = depth_target;
  intent.clear_root_call_serial = command.clear.root_call_serial;
  intent.clear_root_context_key = command.clear.root_context_key;
  intent.clear_root_symbol_address = command.clear.root_symbol_address;
  if (depth_target) {
    intent.target_root_call_serial = command.clear.target_roots.depth.call_serial;
    intent.target_root_context_key = command.clear.target_roots.depth.context_key;
    intent.target_root_symbol_address =
        command.clear.target_roots.depth.symbol_address;
  } else {
    for (uint32_t slot = 0; slot < command.resources.render_targets.size();
         ++slot) {
      if (command.resources.render_targets[slot] == target) {
        const uint32_t render_target_info =
            command.pipeline.pm4.valid &&
                    slot < command.pipeline.pm4.rb_color_info.size()
                ? command.pipeline.pm4.rb_color_info[slot]
                : 0u;
        intent.target_format = render_target_info & 0x3Fu;
        ResolvePrivateRectTargetExtent(command, intent.target_width,
                                       intent.target_height);
        intent.target_root_call_serial =
            command.clear.target_roots.color[slot].call_serial;
        intent.target_root_context_key =
            command.clear.target_roots.color[slot].context_key;
        intent.target_root_symbol_address =
            command.clear.target_roots.color[slot].symbol_address;
        break;
      }
    }
  }
  g_direct_renderer.private_texture_setup_clear_intents.push_back(intent);
  constexpr size_t kMaxPrivateTextureSetupClearIntents = 160;
  while (g_direct_renderer.private_texture_setup_clear_intents.size() >
         kMaxPrivateTextureSetupClearIntents) {
    g_direct_renderer.private_texture_setup_clear_intents.pop_front();
  }
  constexpr size_t kMaxPrivateTextureSetupClearIntentKeys = 512;
  if (g_direct_renderer.private_texture_setup_clear_intent_keys.size() >
      kMaxPrivateTextureSetupClearIntentKeys) {
    g_direct_renderer.private_texture_setup_clear_intent_keys.clear();
  }

  if (g_direct_renderer.diagnostics.logged_private_rect_setup_clear_intents < 32u) {
    REXLOG_INFO(
        "Refii plume remembered private-rect setup clear intent #{} "
        "frame={} serial={} lr=0x{:08X} target=0x{:08X} depth={} "
        "clear_root=0x{:08X}/{} target_root=0x{:08X}/{}",
        ++g_direct_renderer.diagnostics.logged_private_rect_setup_clear_intents,
        g_direct_renderer.current_frame_index,
        static_cast<unsigned long long>(command.serial),
        command.draw.state_flush_lr, target, depth_target ? 1 : 0,
        intent.clear_root_symbol_address,
        static_cast<unsigned long long>(intent.clear_root_call_serial),
        intent.target_root_symbol_address,
        static_cast<unsigned long long>(intent.target_root_call_serial));
  }
}

void RememberDirectPrivateTextureSetupClearIntentsLocked(
    const std::vector<FableDirectRenderCommand>& commands) {
  for (const FableDirectRenderCommand& command : commands) {
    if (command.type != FableDirectRenderCommandType::kClear ||
        !IsFablePrivateTextureRectangleSetupClearLr(
            command.draw.state_flush_lr)) {
      continue;
    }
    const bool any_color_flags =
        (command.clear.flags & kFableClearColorTargetMask) != 0;
    for (uint32_t slot = 0; slot < command.resources.render_targets.size();
         ++slot) {
      if (any_color_flags && (command.clear.flags & (1u << slot)) == 0) {
        continue;
      }
      RememberDirectPrivateTextureSetupClearIntentLocked(
          command, command.resources.render_targets[slot], false);
    }
    if ((command.clear.flags & (kFableClearDepth | kFableClearStencil)) != 0) {
      RememberDirectPrivateTextureSetupClearIntentLocked(
          command, command.resources.depth_stencil, true);
    }
  }
}

void BackfillDirectPrivateTextureSetupClearActionsLocked(
    const FablePrivateTextureCopyAction& action) {
  if (action.serial == 0) {
    return;
  }
  const bool depth_action = action.kind == FablePrivateTextureCopyKind::kDepth;
  const uint32_t action_target =
      depth_action && action.depth_stencil != 0
          ? action.depth_stencil
          : (action.target_surface != 0 ? action.target_surface
                                        : action.render_target);
  if (action_target == 0) {
    return;
  }

  constexpr uint32_t kMaxSetupClearActionFrameGap = 32;
  for (const DirectPrivateTextureSetupClearIntent& intent :
       g_direct_renderer.private_texture_setup_clear_intents) {
    if (intent.depth_target != depth_action ||
        !IsFablePrivateTextureRectangleSetupClearLr(intent.lr) ||
        action.frame_index < intent.frame_index ||
        action.frame_index - intent.frame_index >
            kMaxSetupClearActionFrameGap) {
      continue;
    }
    if (!DirectPrivateTextureSetupClearIntentHasExactRoots(intent) ||
        !DirectPrivateTextureCopyActionHasCompleteIdaWrapperIdentity(action)) {
      static uint32_t logged_exact_setup_clear_action_backfill_root_rejects = 0;
      if (logged_exact_setup_clear_action_backfill_root_rejects < 32u) {
        REXLOG_INFO(
            "Refii plume exact setup-clear/action backfill root reject #{} "
            "setup_frame={} action_frame={} setup_target=0x{:08X} "
            "action_target=0x{:08X} clear_root=0x{:08X}/{} "
            "target_root=0x{:08X}/{} wrapper=0x{:08X}/{} "
            "context=0x{:016X}",
            ++logged_exact_setup_clear_action_backfill_root_rejects,
            intent.frame_index, action.frame_index, intent.target,
            action_target, intent.clear_root_symbol_address,
            static_cast<unsigned long long>(intent.clear_root_call_serial),
            intent.target_root_symbol_address,
            static_cast<unsigned long long>(intent.target_root_call_serial),
            action.wrapper_symbol_address,
            static_cast<unsigned long long>(action.wrapper_call_serial),
            static_cast<unsigned long long>(action.wrapper_context_key));
      }
      continue;
    }
    if (action.draw_serial != 0 && intent.draw_serial != 0 &&
        action.draw_serial < intent.draw_serial) {
      continue;
    }
    if (!DirectPrivateTextureSetupClearIntentMatchesActionTargetLocked(intent,
                                                                       action)) {
      continue;
    }
    bool duplicate = false;
    for (const DirectPrivateTextureSetupClearAction& existing :
         g_direct_renderer.private_texture_setup_clear_actions) {
      if (existing.setup_serial == intent.serial &&
          existing.setup_target == intent.target &&
          existing.depth_target == intent.depth_target &&
          existing.action.serial == action.serial) {
        duplicate = true;
        break;
      }
    }
    if (duplicate) {
      continue;
    }
    const uint64_t frame_distance = action.frame_index - intent.frame_index;

    RegisterDirectPrivateTextureSetupClearSurfaceLinkLocked(
        intent, action, action_target);

    DirectPrivateTextureSetupClearAction setup_action;
    setup_action.setup_frame_index = intent.frame_index;
    setup_action.setup_serial = intent.serial;
    setup_action.setup_draw_serial = intent.draw_serial;
    setup_action.setup_lr = intent.lr;
    setup_action.setup_target = intent.target;
    setup_action.setup_target_width = intent.target_width;
    setup_action.setup_target_height = intent.target_height;
    setup_action.setup_target_format = intent.target_format;
    setup_action.depth_target = intent.depth_target;
    setup_action.clear_root_call_serial = intent.clear_root_call_serial;
    setup_action.clear_root_context_key = intent.clear_root_context_key;
    setup_action.clear_root_symbol_address = intent.clear_root_symbol_address;
    setup_action.target_root_call_serial = intent.target_root_call_serial;
    setup_action.target_root_context_key = intent.target_root_context_key;
    setup_action.target_root_symbol_address = intent.target_root_symbol_address;
    setup_action.action = action;
    g_direct_renderer.private_texture_setup_clear_actions.push_back(
        setup_action);
    constexpr size_t kMaxPrivateTextureSetupClearActions = 160;
    while (g_direct_renderer.private_texture_setup_clear_actions.size() >
           kMaxPrivateTextureSetupClearActions) {
      g_direct_renderer.private_texture_setup_clear_actions.pop_front();
    }

    if (g_direct_renderer.diagnostics.logged_private_rect_setup_clear_backfills < 32u) {
      REXLOG_INFO(
          "Refii plume private-rect setup clear/action backfill #{} "
          "setup_frame={} action_frame={} setup_serial={} action_serial={} "
          "lr=0x{:08X} setup_target=0x{:08X} action_target=0x{:08X} "
          "distance={} frame_distance={} depth={} source=0x{:08X} "
          "base=0x{:08X} backing=0x{:08X} setup_shape={}x{} fmt={} "
          "action_shape={}x{} fmt={}",
          ++g_direct_renderer.diagnostics.logged_private_rect_setup_clear_backfills,
          intent.frame_index, action.frame_index,
          static_cast<unsigned long long>(intent.serial),
          static_cast<unsigned long long>(action.serial), intent.lr,
          intent.target, action_target, 0u,
          static_cast<unsigned long long>(frame_distance),
          intent.depth_target ? 1 : 0, action.source_texture,
          action.source_base_address, action.backing_texture,
          intent.target_width, intent.target_height, intent.target_format,
          action.target_width, action.target_height, action.target_format);
    }
  }
}

void RegisterDirectPrivateTextureCopyActionLocked(
    const FablePrivateTextureCopyAction& action) {
  // §3b note: the observer's actions are deliberately NOT suppressed under
  // renderer_gtl_private_rect_compose. The packet now answers the blit's source
  // at the bind seam (ResolveGtlPacketPrivateRectSourceForCommandLocked), and
  // the observer's action path remains the fallback for every blit no packet
  // names — per the plan's gated-deletion rule, the replaced path is deleted
  // only once the replacement is visually proven, not when it is written.
  FableDirectRegisterPrivateTextureCopyAction(action);
  BackfillDirectPrivateTextureSetupClearActionsLocked(action);
}

const FablePrivateTextureCopyAction*
FindDirectPrivateTextureCopySetupActionForClearTargetLocked(
    const FableDirectRenderCommand& command, uint32_t target,
    bool depth_target) {
  if (target == 0 ||
      !IsFablePrivateTextureRectangleSetupClearLr(
          command.draw.state_flush_lr)) {
    return nullptr;
  }

  const FablePrivateTextureCopyAction* exact_setup_action = nullptr;
  for (const DirectPrivateTextureSetupClearAction& setup_action :
       g_direct_renderer.private_texture_setup_clear_actions) {
    if (!DirectPrivateTextureSetupClearActionMatchesLocked(
            setup_action, command, target, depth_target)) {
      continue;
    }
    if (exact_setup_action &&
        exact_setup_action->serial != setup_action.action.serial) {
      return nullptr;
    }
    exact_setup_action = &setup_action.action;
  }
  return exact_setup_action;
}

void AddDirectClearPrivateTextureCopyOutputInterestLocked(
    const FableDirectRenderCommand& command, uint32_t target,
    bool depth_target, std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter) {
  const FablePrivateTextureCopyAction* action =
      FindDirectPrivateTextureCopyOutputActionForClearTargetLocked(
          command, target, depth_target);
  const bool setup_clear_action = action == nullptr;
  if (!action) {
    action = FindDirectPrivateTextureCopySetupActionForClearTargetLocked(
        command, target, depth_target);
  }
  if (!action) {
    AddDirectResourceInterest(resource_filter, target);
    (void)texture_upload_filter;
    return;
  }

  AddDirectCompactPrivateTextureCopyActionResourceInterestsLocked(
      resource_filter, texture_upload_filter, *action);
  if (RendererDeepObserverHooksEnabled() &&
      g_direct_renderer.diagnostics.logged_private_rect_clear_action_ownership_proofs <
      32u) {
    REXLOG_INFO(
        "Refii plume private-rect clear/action ownership proof #{} "
        "reason=clear-action-output-interest frame={} serial={} "
        "target=0x{:08X} depth={} setup={} lr=0x{:08X} action={} kind={} "
        "src=0x{:08X} base=0x{:08X} rt=0x{:08X} ds=0x{:08X} "
        "draw_serial={} wrapper_call={} wrapper_context=0x{:016X} "
        "wrapper_symbol=0x{:08X}",
        ++g_direct_renderer.diagnostics.logged_private_rect_clear_action_ownership_proofs,
        g_direct_renderer.current_frame_index,
        static_cast<unsigned long long>(command.serial), target,
        depth_target ? 1 : 0, setup_clear_action ? 1 : 0,
        command.draw.state_flush_lr,
        static_cast<unsigned long long>(action->serial),
        FablePrivateTextureCopyKindName(action->kind), action->source_texture,
        action->source_base_address, action->render_target,
        action->depth_stencil,
        static_cast<unsigned long long>(action->draw_serial),
        static_cast<unsigned long long>(action->wrapper_call_serial),
        static_cast<unsigned long long>(action->wrapper_context_key),
        action->wrapper_symbol_address);
  }
}

void AddDirectSurfaceTextureLinkResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FableSurfaceTextureLink* link) {
  if (!link) {
    return;
  }
  AddDirectResourceInterest(resource_filter, link->texture);
  AddDirectResourceInterest(texture_upload_filter, link->texture);
  AddDirectTextureBaseAddressResourceInterestsLocked(
      resource_filter, texture_upload_filter, link->texture_base_address);
  AddDirectResourceInterest(resource_filter, link->source_surface);
  AddDirectResourceInterest(texture_upload_filter, link->source_surface);
  AddDirectResourceInterest(resource_filter, link->backing_texture);
  AddDirectResourceInterest(texture_upload_filter, link->backing_texture);
  AddDirectTextureBaseAddressResourceInterestsLocked(
      resource_filter, texture_upload_filter, link->backing_base_address);
  AddDirectResourceInterest(resource_filter, link->render_target);
  AddDirectResourceInterest(texture_upload_filter, link->render_target);
  AddDirectResourceInterest(resource_filter, link->depth_stencil);
  AddDirectResourceInterest(texture_upload_filter, link->depth_stencil);

  AddDirectTextureIdentityResourceInterestLocked(
      resource_filter, texture_upload_filter,
      FableDirectFindTextureIdentityByObject(link->texture));
  AddDirectTextureIdentityResourceInterestLocked(
      resource_filter, texture_upload_filter,
      FableDirectFindTextureIdentityByBase(link->texture_base_address));
  AddDirectTextureBaseIdentityResourceInterestsLocked(
      resource_filter, texture_upload_filter, link->texture_base_address);
  AddDirectTextureIdentityResourceInterestLocked(
      resource_filter, texture_upload_filter,
      FableDirectFindTextureIdentityByObject(link->source_surface));
  AddDirectTextureIdentityResourceInterestLocked(
      resource_filter, texture_upload_filter,
      FableDirectFindTextureIdentityByObject(link->backing_texture));
  AddDirectTextureIdentityResourceInterestLocked(
      resource_filter, texture_upload_filter,
      FableDirectFindTextureIdentityByBase(link->backing_base_address));
  AddDirectTextureBaseIdentityResourceInterestsLocked(
      resource_filter, texture_upload_filter, link->backing_base_address);
  AddDirectTextureFetchResourceInterestsLocked(
      resource_filter, texture_upload_filter, link->texture_fetch_dwords);
  AddDirectTextureFetchResourceInterestsLocked(
      resource_filter, texture_upload_filter, link->backing_fetch_dwords);
}

void AddDirectCompactSurfaceTextureLinkResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FableSurfaceTextureLink* link) {
  if (!link) {
    return;
  }

  AddDirectResourceInterest(resource_filter, link->source_surface);
  AddDirectResourceInterest(texture_upload_filter, link->source_surface);

  if (link->backing_texture != 0 || link->backing_base_address != 0 ||
      FableDirectTextureFetchKey(link->backing_fetch_dwords) != 0) {
    AddDirectResourceInterest(resource_filter, link->backing_texture);
    AddDirectResourceInterest(texture_upload_filter, link->backing_texture);
    AddDirectTextureBaseAddressResourceInterestsLocked(
        resource_filter, texture_upload_filter, link->backing_base_address);
    AddDirectTextureFetchResourceInterestsLocked(
        resource_filter, texture_upload_filter, link->backing_fetch_dwords);
    return;
  }

  AddDirectResourceInterest(resource_filter, link->texture);
  AddDirectResourceInterest(texture_upload_filter, link->texture);
  AddDirectTextureBaseAddressResourceInterestsLocked(
      resource_filter, texture_upload_filter, link->texture_base_address);
  AddDirectTextureFetchResourceInterestsLocked(
      resource_filter, texture_upload_filter, link->texture_fetch_dwords);
}

void AddDirectSurfaceTextureLinksResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const std::vector<FableSurfaceTextureLink>* links) {
  if (!links) {
    return;
  }
  for (const FableSurfaceTextureLink& link : *links) {
    AddDirectSurfaceTextureLinkResourceInterestsLocked(resource_filter,
                                                      texture_upload_filter,
                                                      &link);
  }
}

void AddDirectSurfaceDestinationRelationsResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const std::vector<FableSurfaceDestinationTextureRelation>* relations) {
  if (!relations) {
    return;
  }
  for (const FableSurfaceDestinationTextureRelation& relation : *relations) {
    AddDirectResourceInterest(resource_filter, relation.destination_texture);
    AddDirectResourceInterest(texture_upload_filter,
                              relation.destination_texture);
    AddDirectSurfaceTextureLinkResourceInterestsLocked(
        resource_filter, texture_upload_filter, &relation.link);
  }
}

void AddDirectSourceSurfaceDestinationResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    uint32_t source_surface) {
	  if (const std::vector<FableSurfaceDestinationTextureRelation>* relations =
	          FableDirectFindSurfaceDestinationRelationsBySourceSurface(
	              source_surface)) {
	    AddDirectSurfaceDestinationRelationsResourceInterestsLocked(
	        resource_filter, texture_upload_filter, relations);
	  }
	}

void AddDirectTextureSlotIdentityResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FableDirectRenderCommand& command, uint32_t slot) {
  if (slot >= command.resources.textures.size()) {
    return;
  }
  const uint32_t guest = command.resources.textures[slot];
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  if (guest == 0 && !command_fetch_info.valid) {
    return;
  }

  PlumeTrackedResource* texture = FindDirectResourceLocked(guest);
  if (DirectCommandSlotUsesExactBinkWrapperPlaneLocked(command, slot)) {
    AddDirectResourceInterest(resource_filter, guest);
    AddDirectResourceInterest(texture_upload_filter, guest);
    AddDirectTextureIdentityResourceInterestLocked(
        resource_filter, texture_upload_filter,
        FableDirectFindTextureIdentityByObject(guest));
    if (command_fetch_info.valid) {
      AddDirectTextureBaseAddressResourceInterestsLocked(
          resource_filter, texture_upload_filter,
          command_fetch_info.base_address);
    }
    if (HasTrackedFetchDwords(command_fetch)) {
      AddDirectTextureFetchIdentityResourceInterestsLocked(
          resource_filter, texture_upload_filter, command_fetch);
    }
    if (texture) {
      AddDirectResourceInterest(resource_filter, texture->guest_address);
      AddDirectResourceInterest(texture_upload_filter, texture->guest_address);
      AddDirectResourceInterest(resource_filter, texture->parent_address);
      AddDirectResourceInterest(texture_upload_filter, texture->parent_address);
      AddDirectTextureIdentityResourceInterestLocked(
          resource_filter, texture_upload_filter,
          FableDirectFindTextureIdentityByObject(texture->guest_address));
      AddDirectTextureIdentityResourceInterestLocked(
          resource_filter, texture_upload_filter,
          FableDirectFindTextureIdentityByObject(texture->parent_address));
      AddDirectTextureBaseAddressResourceInterestsLocked(
          resource_filter, texture_upload_filter,
          PlumeGuestTextureBaseAddress(*texture));
      const std::array<uint32_t, 6> texture_fetch =
          PlumeGuestTextureFetchDwords(*texture);
      if (HasTrackedFetchDwords(texture_fetch)) {
        AddDirectTextureFetchIdentityResourceInterestsLocked(
            resource_filter, texture_upload_filter, texture_fetch);
      }
    }
    if (PlumeTrackedResource* bink_plane =
            ResolveDirectExactBinkWrapperPlaneForSlotLocked(command, slot)) {
      AddDirectResourceInterest(resource_filter, bink_plane->guest_address);
      AddDirectResourceInterest(texture_upload_filter,
                                bink_plane->guest_address);
      AddDirectResourceInterest(resource_filter, bink_plane->parent_address);
      AddDirectResourceInterest(texture_upload_filter,
                                bink_plane->parent_address);
      AddDirectTextureBaseAddressResourceInterestsLocked(
          resource_filter, texture_upload_filter,
          PlumeGuestTextureBaseAddress(*bink_plane));
      const std::array<uint32_t, 6> bink_fetch =
          PlumeGuestTextureFetchDwords(*bink_plane);
      if (HasTrackedFetchDwords(bink_fetch)) {
        AddDirectTextureFetchIdentityResourceInterestsLocked(
            resource_filter, texture_upload_filter, bink_fetch);
      }
    }
    return;
  }
  if (ResolveDirectAuthoritativeTitleSyncSampleForSlotLocked(command, slot,
                                                             texture)) {
    return;
  }

  if (IsFableTitleBillboardAtlasCommandSlotLocked(command, slot) ||
      IsFableTitleBillboardCommandHeaderAddress(guest) ||
      (command_fetch_info.valid &&
       IsFableTitleBillboardCommandHeaderAddress(
           command_fetch_info.base_address))) {
    AddFableTitleBillboardAtlasResourceInterestsLocked(
        resource_filter, texture_upload_filter, command, slot);
  }

  AddDirectResourceInterest(resource_filter, guest);
  AddDirectResourceInterest(texture_upload_filter, guest);
  AddDirectTextureIdentityResourceInterestLocked(
      resource_filter, texture_upload_filter,
      FableDirectFindTextureIdentityByObject(guest));
  if (DirectCommandSlotUsesExactTitleUploadSourceLocked(command, slot)) {
    if (command_fetch_info.valid) {
      AddDirectTextureBaseAddressResourceInterestsLocked(
          resource_filter, texture_upload_filter, command_fetch_info.base_address);
    }
    if (HasTrackedFetchDwords(command_fetch)) {
      AddDirectTextureFetchIdentityResourceInterestsLocked(
          resource_filter, texture_upload_filter, command_fetch);
    }
    if (texture) {
      AddDirectResourceInterest(resource_filter, texture->guest_address);
      AddDirectResourceInterest(texture_upload_filter, texture->guest_address);
      AddDirectTextureIdentityResourceInterestLocked(
          resource_filter, texture_upload_filter,
          FableDirectFindTextureIdentityByObject(texture->guest_address));
      AddDirectTextureIdentityResourceInterestLocked(
          resource_filter, texture_upload_filter,
          FableDirectFindTextureIdentityByObject(texture->parent_address));
    }
    // Census-dead (runs 538/539): binding-action interest registration that
    // lived here never hit — fetch-identity + object-identity interests above
    // already cover materialization pressure. Deleted per migration step 3.
    auto add_exact_header_action_interests =
        [&](const FableTextureHeaderAction* action) -> bool {
      if (!action) {
        return false;
      }

      const uint32_t action_addresses[] = {
          action->texture_object, action->parent_texture,
          action->texture_header};
      for (uint32_t action_address : action_addresses) {
        AddDirectResourceInterest(resource_filter, action_address);
        AddDirectResourceInterest(texture_upload_filter, action_address);
        AddDirectTextureIdentityResourceInterestLocked(
            resource_filter, texture_upload_filter,
            FableDirectFindTextureIdentityByObject(action_address));
      }
      AddDirectTextureBaseAddressResourceInterestsLocked(
          resource_filter, texture_upload_filter, action->base_address);
      if (IsFableTitleBillboardTileUploadBase(action->base_address,
                                             action->width, action->height)) {
        AddFableTitleBillboardTileUploadAliasResourceInterestsLocked(
            resource_filter, texture_upload_filter, action->base_address,
            action->width, action->height);
      }
      AddDirectTextureFetchIdentityResourceInterestsLocked(
          resource_filter, texture_upload_filter, action->fetch_dwords);

      return true;
    };
    if (command_fetch_info.valid &&
        add_exact_header_action_interests(
            FableDirectFindTextureHeaderActionForExactFetchIdentity(
                command_fetch_info.base_address, command_fetch_info.width,
                command_fetch_info.height, command_fetch_info.format,
                command_fetch))) {
      return;
    }
    static uint32_t logged_exact_title_upload_interests = 0;
    if (logged_exact_title_upload_interests < 32u) {
      REXLOG_INFO(
          "Refii plume exact title upload resource interest #{} frame={} "
          "slot={} guest=0x{:08X} tracked=0x{:08X} base=0x{:08X} "
          "source={} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_title_upload_interests,
          g_direct_renderer.current_frame_index, slot, guest,
          texture ? texture->guest_address : 0u,
          command_fetch_info.valid
              ? command_fetch_info.base_address
              : (texture ? PlumeGuestTextureBaseAddress(*texture) : 0u),
          static_cast<uint32_t>(command.draw.source),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return;
  }

  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  const uint64_t command_draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  auto add_visible_exact_link =
      [&](const FableSurfaceTextureLink* link) -> bool {
    if (!link || !link->trusted_destination_edge) {
      return false;
    }
    if (link->source_slot != kUnknownSlot && link->source_slot != slot) {
      return false;
    }
    if (link->frame_index != 0 &&
        link->frame_index > g_direct_renderer.current_frame_index) {
      return false;
    }
    if (link->frame_index != 0 &&
        link->frame_index == g_direct_renderer.current_frame_index &&
        link->draw_serial != 0 && command_draw_serial != 0 &&
        link->draw_serial > command_draw_serial) {
      return false;
    }
    AddDirectCompactSurfaceTextureLinkResourceInterestsLocked(
        resource_filter, texture_upload_filter, link);
    return true;
  };
	  auto add_exact_destination_edge = [&](uint32_t destination_texture) -> bool {
	    if (destination_texture == 0) {
	      return false;
	    }
    if (add_visible_exact_link(
            FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
                destination_texture, g_direct_renderer.current_frame_index,
                command_draw_serial, slot))) {
      return true;
    }
    if (add_visible_exact_link(
            FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
                destination_texture, slot))) {
      return true;
    }
	    return add_visible_exact_link(
	        FableDirectFindSurfaceTextureLinkForDestinationTexture(
	            destination_texture));
	  };
	  auto add_visible_exact_relation =
	      [&](const FableSurfaceDestinationTextureRelation* relation) -> bool {
	    if (!relation || !relation->link.trusted_destination_edge) {
	      return false;
	    }
	    if (relation->link.frame_index != 0 &&
	        relation->link.frame_index > g_direct_renderer.current_frame_index) {
	      return false;
	    }
	    if (relation->link.frame_index != 0 &&
	        relation->link.frame_index == g_direct_renderer.current_frame_index &&
	        relation->link.draw_serial != 0 && command_draw_serial != 0 &&
	        relation->link.draw_serial > command_draw_serial) {
	      return false;
	    }
	    const bool same_slot =
	        relation->link.source_slot == kUnknownSlot ||
	        relation->link.source_slot == slot;
	    if (!same_slot &&
	        !DirectSurfaceTextureLinkTouchesFablePrivateChain(relation->link,
	                                                          nullptr)) {
	      return false;
	    }
	    AddDirectResourceInterest(resource_filter, relation->destination_texture);
	    AddDirectResourceInterest(texture_upload_filter,
	                              relation->destination_texture);
	    AddDirectCompactSurfaceTextureLinkResourceInterestsLocked(
	        resource_filter, texture_upload_filter, &relation->link);
	    return true;
	  };
	  auto add_exact_destination_relation =
	      [&](uint32_t destination_texture) -> bool {
	    if (destination_texture == 0) {
	      return false;
	    }
	    if (add_visible_exact_relation(
	            FableDirectFindVisibleSurfaceDestinationRelationForDestinationTexture(
	                destination_texture, g_direct_renderer.current_frame_index,
	                command_draw_serial, slot))) {
	      return true;
	    }
	    if (add_visible_exact_relation(
	            FableDirectFindSurfaceDestinationRelationForDestinationTextureSlot(
	                destination_texture, slot))) {
	      return true;
	    }
	    return add_visible_exact_relation(
	        FableDirectFindSurfaceDestinationRelationForDestinationTexture(
	            destination_texture));
	  };
	  auto add_exact_base_edge = [&](uint32_t base_address) -> bool {
	    return base_address != 0 &&
	           add_visible_exact_link(FableDirectFindVisibleSurfaceTextureLinkForBase(
	               base_address, g_direct_renderer.current_frame_index,
	               command_draw_serial, slot));
  };
  auto add_exact_fetch_edge =
      [&](const std::array<uint32_t, 6>& fetch_dwords) -> bool {
    return HasTrackedFetchDwords(fetch_dwords) &&
           add_visible_exact_link(
               FableDirectFindVisibleSurfaceTextureLinkForFetch(
                   fetch_dwords, g_direct_renderer.current_frame_index,
                   command_draw_serial, slot));
  };
  auto add_exact_identity_edge =
      [&](const FableTextureIdentity* identity) -> bool {
	    if (!identity) {
	      return false;
	    }
	    return add_exact_destination_relation(identity->object_texture) ||
	           add_exact_destination_edge(identity->object_texture) ||
	           add_exact_destination_relation(identity->parent_texture) ||
	           add_exact_destination_edge(identity->parent_texture) ||
	           add_exact_destination_relation(identity->base_address) ||
	           add_exact_destination_edge(identity->base_address) ||
	           add_exact_base_edge(identity->base_address) ||
	           add_exact_fetch_edge(identity->fetch_dwords);
	  };
  auto add_exact_private_copy_output =
      [&](uint32_t destination_texture) -> bool {
    if (destination_texture == 0) {
      return false;
    }
    const FablePrivateTextureCopyAction* action =
        FableDirectFindVisiblePrivateTextureCopyOutputActionForDestinationTextureSlot(
            destination_texture, slot, g_direct_renderer.current_frame_index,
            command_draw_serial);
    if (!action) {
      action =
          FableDirectFindVisiblePrivateTextureCopyOutputActionForDestinationTexture(
              destination_texture, g_direct_renderer.current_frame_index,
              command_draw_serial, slot);
    }
    if (!action) {
      return false;
    }
    AddDirectCompactPrivateTextureCopyActionResourceInterestsLocked(
        resource_filter, texture_upload_filter, *action);
    return true;
  };
  auto add_exact_private_copy_output_identity =
      [&](const FableTextureIdentity* identity) -> bool {
    if (!identity) {
      return false;
    }
    return add_exact_private_copy_output(identity->object_texture) ||
           add_exact_private_copy_output(identity->parent_texture) ||
           add_exact_private_copy_output(identity->base_address);
  };
  auto add_exact_alias_destination_edge = [&](uint32_t destination_texture) {
    RendererSurfaceCopyAliasSnapshot alias;
    if (destination_texture == 0 ||
        !FindDirectSurfaceCopyAliasByDestinationAddressLocked(
            destination_texture, &alias) ||
        !DirectSurfaceCopyAliasHasExactIdentity(alias)) {
      return false;
    }
    return add_visible_exact_link(FindDirectSurfaceTextureLinkByAliasLocked(alias));
  };
  auto add_exact_alias_identity_edge =
      [&](const FableTextureIdentity* identity) -> bool {
    if (!identity) {
      return false;
    }
    return add_exact_alias_destination_edge(identity->object_texture) ||
           add_exact_alias_destination_edge(identity->parent_texture) ||
           add_exact_alias_destination_edge(identity->base_address);
  };
  const bool private_or_title_exact_interest_required =
      command.draw.source != RendererDrawEventSource::kPixVideo &&
      (DirectCommandUsesPrivateOrTitleSurfaceCopyPathLocked(command) ||
       DirectCommandUsesFablePrivateTexturePath(command, slot, texture) ||
       command.draw.private_texture_rectangle ||
       IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader) ||
       IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
       IsFableTitleSceneVertexShaderCommandLocked(command) ||
       IsFableTitleSceneTextureSyncCommandLocked(command));
  if (private_or_title_exact_interest_required) {
    if (command.draw.source == RendererDrawEventSource::kPrivateTextureRectangle ||
        command.draw.private_texture_rectangle ||
        IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader)) {
      AddDirectResourceInterest(resource_filter, command.resources.render_targets[0]);
      AddDirectResourceInterest(resource_filter, command.resources.depth_stencil);
    }
    if (add_exact_alias_destination_edge(guest) ||
        (command_fetch_info.valid &&
         add_exact_alias_destination_edge(command_fetch_info.base_address)) ||
        (texture &&
         (add_exact_alias_destination_edge(texture->guest_address) ||
          add_exact_alias_destination_edge(texture->parent_address) ||
          add_exact_alias_destination_edge(
              PlumeGuestTextureBaseAddress(*texture)))) ||
        add_exact_alias_identity_edge(FableDirectFindTextureIdentityByObject(
            guest)) ||
        (texture &&
         (add_exact_alias_identity_edge(FableDirectFindTextureIdentityByObject(
              texture->guest_address)) ||
          add_exact_alias_identity_edge(FableDirectFindTextureIdentityByObject(
              texture->parent_address)) ||
          add_exact_alias_identity_edge(FableDirectFindTextureIdentityByBase(
              PlumeGuestTextureBaseAddress(*texture)))))) {
      return;
    }
    if (add_exact_private_copy_output(guest) ||
        (command_fetch_info.valid &&
         add_exact_private_copy_output(command_fetch_info.base_address)) ||
        (texture &&
         (add_exact_private_copy_output(texture->guest_address) ||
          add_exact_private_copy_output(texture->parent_address) ||
          add_exact_private_copy_output(
              PlumeGuestTextureBaseAddress(*texture)))) ||
        add_exact_private_copy_output_identity(
            FableDirectFindTextureIdentityByObject(guest)) ||
        (texture &&
         (add_exact_private_copy_output_identity(
              FableDirectFindTextureIdentityByObject(texture->guest_address)) ||
          add_exact_private_copy_output_identity(
              FableDirectFindTextureIdentityByObject(texture->parent_address)) ||
          add_exact_private_copy_output_identity(
              FableDirectFindTextureIdentityByBase(
                  PlumeGuestTextureBaseAddress(*texture)))))) {
      return;
    }
    if (IsFableTitleSceneTextureSyncCommandLocked(command) &&
        command_fetch_info.valid) {
      AddDirectTextureFetchResourceInterestsLocked(resource_filter,
                                                  texture_upload_filter,
                                                  command_fetch);
      return;
    }
    const bool exact_tiny_title_upload_interest =
        command_fetch_info.valid &&
        (IsFableTinyTitleUploadedTextureIdentityBase(
             command_fetch_info.base_address, command_fetch_info.width,
             command_fetch_info.height) ||
         IsFableTinyTitleUploadedTextureAddress(guest) ||
         (texture &&
          (IsFableTinyTitleUploadedTextureAddress(texture->guest_address) ||
           IsFableTinyTitleUploadedTextureAddress(texture->parent_address) ||
           IsFableTinyTitleUploadedTextureAddress(
               PlumeGuestTextureBaseAddress(*texture)))));
    if (exact_tiny_title_upload_interest) {
      AddFableTinyTitleTextureResourceInterestsLocked(resource_filter,
                                                      texture_upload_filter,
                                                      command, slot);
      return;
    }
    static uint32_t logged_private_title_exact_interest_misses = 0;
    if (logged_private_title_exact_interest_misses < 64u) {
      REXLOG_INFO(
          "Refii plume exact private/title resource-interest reject #{} "
          "frame={} slot={} guest=0x{:08X} source={} private_rect={} "
          "title_sync={} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_private_title_exact_interest_misses,
          g_direct_renderer.current_frame_index, slot, guest,
          static_cast<uint32_t>(command.draw.source),
          command.draw.private_texture_rectangle ? 1 : 0,
          IsFableTitleSceneTextureSyncCommandLocked(command) ? 1 : 0,
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return;
  }
	  if (add_exact_destination_relation(guest) ||
	      add_exact_destination_edge(guest) ||
	      (command_fetch_info.valid &&
	       (add_exact_destination_relation(command_fetch_info.base_address) ||
	        add_exact_base_edge(command_fetch_info.base_address))) ||
	      add_exact_fetch_edge(command_fetch) ||
	      (texture &&
	       (add_exact_destination_relation(texture->guest_address) ||
	        add_exact_destination_edge(texture->guest_address) ||
	        add_exact_destination_relation(texture->parent_address) ||
	        add_exact_destination_edge(texture->parent_address) ||
	        add_exact_destination_relation(PlumeGuestTextureBaseAddress(*texture)) ||
	        add_exact_destination_edge(PlumeGuestTextureBaseAddress(*texture)) ||
	        add_exact_base_edge(PlumeGuestTextureBaseAddress(*texture)) ||
	        add_exact_fetch_edge(PlumeGuestTextureFetchDwords(*texture)))) ||
      add_exact_identity_edge(FableDirectFindTextureIdentityByObject(guest)) ||
      (texture &&
       (add_exact_identity_edge(
            FableDirectFindTextureIdentityByObject(texture->guest_address)) ||
        add_exact_identity_edge(
            FableDirectFindTextureIdentityByObject(texture->parent_address)) ||
        add_exact_identity_edge(FableDirectFindTextureIdentityByBase(
            PlumeGuestTextureBaseAddress(*texture)))))) {
    return;
  }
  if (add_exact_private_copy_output(guest) ||
      (command_fetch_info.valid &&
       add_exact_private_copy_output(command_fetch_info.base_address)) ||
      (texture &&
       (add_exact_private_copy_output(texture->guest_address) ||
        add_exact_private_copy_output(texture->parent_address) ||
        add_exact_private_copy_output(PlumeGuestTextureBaseAddress(*texture)))) ||
      add_exact_private_copy_output_identity(
          FableDirectFindTextureIdentityByObject(guest)) ||
      (texture &&
       (add_exact_private_copy_output_identity(
            FableDirectFindTextureIdentityByObject(texture->guest_address)) ||
        add_exact_private_copy_output_identity(
            FableDirectFindTextureIdentityByObject(texture->parent_address)) ||
        add_exact_private_copy_output_identity(FableDirectFindTextureIdentityByBase(
            PlumeGuestTextureBaseAddress(*texture)))))) {
    return;
  }

  const bool private_texture_path =
      DirectCommandUsesFablePrivateTexturePath(command, slot, texture);
  if (private_texture_path) {
    auto add_trusted_link = [&](const FableSurfaceTextureLink* link) -> bool {
      if (!link || !link->trusted_destination_edge) {
        return false;
      }
      if (link->source_slot != kUnknownSlot && link->source_slot != slot) {
        return false;
      }
      if (link->frame_index != 0 &&
          link->frame_index > g_direct_renderer.current_frame_index) {
        return false;
      }
      if (link->frame_index != 0 &&
          link->frame_index == g_direct_renderer.current_frame_index &&
          link->draw_serial != 0 && command_draw_serial != 0 &&
          link->draw_serial > command_draw_serial) {
        return false;
      }
      AddDirectCompactSurfaceTextureLinkResourceInterestsLocked(
          resource_filter, texture_upload_filter, link);
      return true;
    };
    auto add_trusted_destination_edge =
        [&](uint32_t destination_texture) -> bool {
      if (destination_texture == 0) {
        return false;
      }
      if (add_trusted_link(
              FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
                  destination_texture, slot))) {
        return true;
      }
      return add_trusted_link(
          FableDirectFindSurfaceTextureLinkForDestinationTexture(
              destination_texture));
    };
    auto add_trusted_identity_edge =
        [&](const FableTextureIdentity* identity) -> bool {
      if (!identity) {
        return false;
      }
      return add_trusted_destination_edge(identity->object_texture) ||
             add_trusted_destination_edge(identity->parent_texture) ||
             add_trusted_destination_edge(identity->base_address);
    };
    if (add_trusted_destination_edge(guest) ||
        (texture && texture->guest_address != guest &&
         add_trusted_destination_edge(texture->guest_address)) ||
        (texture && texture->parent_address != 0 &&
         add_trusted_destination_edge(texture->parent_address)) ||
        (texture &&
         add_trusted_destination_edge(PlumeGuestTextureBaseAddress(*texture))) ||
        add_trusted_identity_edge(FableDirectFindTextureIdentityByObject(guest)) ||
        (texture &&
         (add_trusted_identity_edge(FableDirectFindTextureIdentityByObject(
              texture->guest_address)) ||
          add_trusted_identity_edge(FableDirectFindTextureIdentityByObject(
              texture->parent_address)) ||
	          add_trusted_identity_edge(FableDirectFindTextureIdentityByBase(
	              PlumeGuestTextureBaseAddress(*texture)))))) {
      return;
    }
  }
  if (texture) {
    AddDirectResourceInterest(resource_filter, texture->guest_address);
    AddDirectResourceInterest(texture_upload_filter, texture->guest_address);
    AddDirectResourceInterest(resource_filter, texture->parent_address);
    AddDirectResourceInterest(texture_upload_filter, texture->parent_address);
    AddDirectTextureIdentityResourceInterestLocked(
        resource_filter, texture_upload_filter,
        FableDirectFindTextureIdentityByObject(texture->guest_address));
    AddDirectTextureIdentityResourceInterestLocked(
        resource_filter, texture_upload_filter,
        FableDirectFindTextureIdentityByObject(texture->parent_address));
    AddDirectTextureIdentityResourceInterestLocked(
        resource_filter, texture_upload_filter,
        FableDirectFindTextureIdentityByBase(
            PlumeGuestTextureBaseAddress(*texture)));
    AddDirectTextureBaseIdentityResourceInterestsLocked(
        resource_filter, texture_upload_filter,
        PlumeGuestTextureBaseAddress(*texture));
    AddDirectTextureFetchResourceInterestsLocked(
        resource_filter, texture_upload_filter,
        PlumeGuestTextureFetchDwords(*texture));
  }

  AddDirectTextureFetchResourceInterestsLocked(
      resource_filter, texture_upload_filter,
      DirectCommandTextureFetchDwords(command, slot));

  if (command.draw.source != RendererDrawEventSource::kPixVideo) {
    return;
  }

  AddDirectSurfaceTextureLinksResourceInterestsLocked(
      resource_filter, texture_upload_filter,
      FableDirectFindSurfaceTextureLinksByTexture(guest));
  AddDirectSurfaceTextureLinksResourceInterestsLocked(
      resource_filter, texture_upload_filter,
      FableDirectFindSurfaceTextureLinksBySurface(guest));
  AddDirectSourceSurfaceDestinationResourceInterestsLocked(
      resource_filter, texture_upload_filter, guest);
  AddDirectSurfaceTextureLinksResourceInterestsLocked(
      resource_filter, texture_upload_filter,
      FableDirectFindSurfaceTextureLinksByFetch(
          DirectCommandTextureFetchDwords(command, slot)));
  if (texture) {
    AddDirectSurfaceTextureLinksResourceInterestsLocked(
        resource_filter, texture_upload_filter,
        FableDirectFindSurfaceTextureLinksByTexture(texture->guest_address));
    AddDirectSurfaceTextureLinksResourceInterestsLocked(
        resource_filter, texture_upload_filter,
        FableDirectFindSurfaceTextureLinksBySurface(texture->guest_address));
    AddDirectSourceSurfaceDestinationResourceInterestsLocked(
        resource_filter, texture_upload_filter, texture->guest_address);
    AddDirectSourceSurfaceDestinationResourceInterestsLocked(
        resource_filter, texture_upload_filter, texture->parent_address);
    AddDirectSurfaceTextureLinksResourceInterestsLocked(
        resource_filter, texture_upload_filter,
        FableDirectFindSurfaceTextureLinksByBase(
            PlumeGuestTextureBaseAddress(*texture)));
    AddDirectSurfaceTextureLinksResourceInterestsLocked(
        resource_filter, texture_upload_filter,
        FableDirectFindSurfaceTextureLinksByFetch(
            PlumeGuestTextureFetchDwords(*texture)));
  }
}

void AddFableTinyTitleTextureResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FableDirectRenderCommand& command, uint32_t slot) {
  if (slot >= command.resources.textures.size()) {
    return;
  }

  const uint32_t guest = command.resources.textures[slot];
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);

  const auto add_texture_identity_address = [&](uint32_t address) {
    if (address == 0) {
      return;
    }
    AddDirectResourceInterest(resource_filter, address);
    AddDirectResourceInterest(texture_upload_filter, address);
    AddDirectTextureIdentityResourceInterestLocked(
        resource_filter, texture_upload_filter,
        FableDirectFindTextureIdentityByObject(address));
    AddDirectTextureIdentityResourceInterestLocked(
        resource_filter, texture_upload_filter,
        FableDirectFindTextureIdentityByBase(address));
    AddDirectTextureBaseIdentityResourceInterestsLocked(
        resource_filter, texture_upload_filter, address);
  };

  add_texture_identity_address(guest);
  if (command_fetch_info.valid) {
    add_texture_identity_address(command_fetch_info.base_address);
    add_texture_identity_address(command_fetch_info.base_address & 0xFFFFF000u);
  }
  AddDirectTextureFetchIdentityResourceInterestsLocked(
      resource_filter, texture_upload_filter, command_fetch);

  if (PlumeTrackedResource* texture = FindDirectResourceLocked(guest)) {
    add_texture_identity_address(texture->guest_address);
    add_texture_identity_address(texture->parent_address);
    add_texture_identity_address(PlumeGuestTextureBaseAddress(*texture));
    AddDirectTextureFetchIdentityResourceInterestsLocked(
        resource_filter, texture_upload_filter,
        PlumeGuestTextureFetchDwords(*texture));
  }

  // ADDRESS GATE (census: title_upload_interest_seed). UNCONDITIONAL — this
  // block seeds resource interest for ten one-run object pointers on EVERY
  // texture slot of EVERY draw, with no test at all, so there is nothing to
  // "stop matching": at gameplay it simply seeds ten addresses that no longer
  // name anything, wasting the work and, more importantly, no longer pulling in
  // whatever the equivalent gameplay resources are. `matched` counts how often
  // it runs (i.e. its cost); `derived` counts the calls where at least one
  // seeded address actually resolves to a tracked resource — that column going
  // to zero is the signal that the seed list has gone stale. The resolve check
  // is sampled (every 256th call) to keep this off the hot path.
  {
    static std::atomic<uint64_t> seed_calls{0};
    const uint64_t seed_call =
        seed_calls.fetch_add(1u, std::memory_order_relaxed) + 1u;
    bool any_seed_resolves = false;
    if ((seed_call % 256u) == 0u) {
      static constexpr uint32_t kSeedProbeAddresses[] = {
          0x401317D0u, 0x40131810u, 0x40131850u, 0x40131890u, 0x401318D0u,
          0x40131910u, 0x404DE490u, 0x404DE4D0u, 0x404DE510u, 0x404DE550u};
      for (uint32_t probe : kSeedProbeAddresses) {
        if (FindDirectResourceLocked(probe) != nullptr) {
          any_seed_resolves = true;
          break;
        }
      }
      DirectAddressGateTest(DirectAddressGate::kTitleUploadInterestSeed, true,
                            guest, seed_call, any_seed_resolves);
    }
  }
  add_texture_identity_address(0x401317D0u);
  add_texture_identity_address(0x40131810u);
  add_texture_identity_address(0x40131850u);
  add_texture_identity_address(0x40131890u);
  add_texture_identity_address(0x401318D0u);
  add_texture_identity_address(0x40131910u);
  add_texture_identity_address(0x1FAB0000u);
  add_texture_identity_address(0x1FAB1000u);
  add_texture_identity_address(0x1FAB2000u);
  add_texture_identity_address(0x1FAB3000u);
  add_texture_identity_address(0x1FAB5000u);
  add_texture_identity_address(0x1FAB4000u);
  add_texture_identity_address(0x404DE490u);
  add_texture_identity_address(0x404DE4D0u);
  add_texture_identity_address(0x404DE510u);
  add_texture_identity_address(0x404DE550u);
  add_texture_identity_address(0x1A0D7000u);
}

// ZERO-CALLER FUNCTION AddFableTitleGuiTextAtlasResourceInterestsLocked —
// DELETED 2026-07-26 (~150 lines). It seeded resource/texture-upload interest
// for the title GUI-text atlas slot from every identity key (object, parent,
// base, fetch) and logged when no exact upload identity was found. Verified
// zero-caller across the WHOLE repo (src/, tools/, both rexglue shadows,
// headers included): the only two occurrences were its forward declaration and
// its definition, both in this file, and both are removed. Internal linkage
// (anonymous namespace), so no cross-TU call path exists. NOTE the sibling
// AddFableTitleBillboardAtlasResourceInterestsLocked below is NOT dead and is
// untouched.
void AddFableTitleBillboardAtlasResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FableDirectRenderCommand& command, uint32_t slot) {
  if (slot >= command.resources.textures.size()) {
    return;
  }

  const uint32_t command_texture = command.resources.textures[slot];
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  if (PlumeTrackedResource* exact_atlas =
          FindExactFableTitleBillboardAtlasForCommandSlotLocked(
              command, slot, FindDirectResourceLocked(command_texture))) {
    AddDirectResourceInterest(resource_filter, command_texture);
    AddDirectResourceInterest(texture_upload_filter, command_texture);
    if (command_fetch_info.valid) {
      AddDirectResourceInterest(texture_upload_filter,
                                command_fetch_info.base_address);
      const uint32_t command_base_object =
          FableObservedTitleBillboardTileObjectAddressForBaseLocked(
              command_fetch_info.base_address, command_fetch_info.width,
              command_fetch_info.height);
      AddDirectResourceInterest(texture_upload_filter, command_base_object);
      AddDirectResourceInterest(
          texture_upload_filter,
          FableTitleBillboardTileHeaderAddressForObject(command_base_object));
    }
    AddDirectResourceInterest(resource_filter, exact_atlas->guest_address);
    AddDirectResourceInterest(texture_upload_filter, exact_atlas->guest_address);
    AddDirectResourceInterest(texture_upload_filter, exact_atlas->parent_address);
    AddDirectResourceInterest(texture_upload_filter,
                              PlumeGuestTextureBaseAddress(*exact_atlas));
    AddDirectResourceInterest(texture_upload_filter,
                              command_fetch_info.valid
                                  ? command_fetch_info.base_address
                                  : 0u);
    const uint32_t atlas_base = PlumeGuestTextureBaseAddress(*exact_atlas);
    const uint32_t atlas_base_object =
        FableObservedTitleBillboardTileObjectAddressForBaseLocked(
            atlas_base, PlumeGuestTextureWidth(*exact_atlas),
            PlumeGuestTextureHeight(*exact_atlas));
    AddDirectResourceInterest(texture_upload_filter, atlas_base_object);
    AddDirectResourceInterest(
        texture_upload_filter,
        FableTitleBillboardTileHeaderAddressForObject(atlas_base_object));
    const uint32_t command_base_object =
        command_fetch_info.valid
            ? FableObservedTitleBillboardTileObjectAddressForBaseLocked(
                  command_fetch_info.base_address, command_fetch_info.width,
                  command_fetch_info.height)
            : 0u;
    AddDirectResourceInterest(texture_upload_filter, command_base_object);
    AddDirectResourceInterest(
        texture_upload_filter,
        FableTitleBillboardTileHeaderAddressForObject(command_base_object));
    AddDirectResourceInterest(
        texture_upload_filter,
        FableTitleBillboardTileHeaderAddressForObject(
            exact_atlas->guest_address));
    AddDirectResourceInterest(
        texture_upload_filter,
        FableTitleBillboardTileObjectAddressForHeader(
            exact_atlas->guest_address));

    static uint32_t logged_exact_billboard_alias_satisfied = 0;
    if (logged_exact_billboard_alias_satisfied < 16u) {
      REXLOG_INFO(
          "Refii plume exact title billboard alias satisfied #{} frame={} "
          "slot={} guest=0x{:08X} sample=0x{:08X} base=0x{:08X} "
          "{}x{} gen={} size={} hash=0x{:016X}",
          ++logged_exact_billboard_alias_satisfied,
          g_direct_renderer.current_frame_index, slot, command_texture,
          exact_atlas->guest_address, PlumeGuestTextureBaseAddress(*exact_atlas),
          PlumeGuestTextureWidth(*exact_atlas),
          PlumeGuestTextureHeight(*exact_atlas),
          static_cast<unsigned long long>(exact_atlas->upload_generation),
          static_cast<unsigned long long>(exact_atlas->texture_upload_size),
          static_cast<unsigned long long>(exact_atlas->texture_upload_hash));
    }
    return;
  }
  auto add_texture_identity_address = [&](uint32_t address) {
    if (address == 0) {
      return;
    }
    AddDirectResourceInterest(resource_filter, address);
    AddDirectResourceInterest(texture_upload_filter, address);
    AddDirectTextureIdentityResourceInterestLocked(
        resource_filter, texture_upload_filter,
        FableDirectFindTextureIdentityByObject(address));
    AddDirectTextureIdentityResourceInterestLocked(
        resource_filter, texture_upload_filter,
        FableDirectFindTextureIdentityByBase(address));
    AddDirectTextureBaseIdentityResourceInterestsLocked(
        resource_filter, texture_upload_filter, address);
  };
  auto add_tracked_texture = [&](const PlumeTrackedResource* texture) {
    if (!texture) {
      return;
    }
    add_texture_identity_address(texture->guest_address);
    add_texture_identity_address(texture->parent_address);
    add_texture_identity_address(PlumeGuestTextureBaseAddress(*texture));
    AddDirectTextureFetchIdentityResourceInterestsLocked(
        resource_filter, texture_upload_filter,
        PlumeGuestTextureFetchDwords(*texture));
  };
  bool added_exact_title_upload_identity = false;
  const auto add_exact_title_upload_identity =
      [&](const FableTextureIdentity* identity) {
        if (!IsFableTitleUploadedTextureIdentity(identity)) {
          return;
        }
        added_exact_title_upload_identity = true;
        AddDirectTextureIdentityResourceInterestLocked(
            resource_filter, texture_upload_filter, identity);
        AddDirectTextureBaseAddressResourceInterestsLocked(
            resource_filter, texture_upload_filter, identity->base_address);
        AddDirectTextureFetchIdentityResourceInterestsLocked(
            resource_filter, texture_upload_filter, identity->fetch_dwords);
      };
  const auto add_exact_title_upload_identity_objects =
      [&](const std::vector<uint32_t>* objects) {
        if (!objects) {
          return;
        }
        for (uint32_t object : *objects) {
          add_exact_title_upload_identity(
              FableDirectFindTextureIdentityByObject(object));
        }
      };

  add_texture_identity_address(command_texture);
  AddDirectTextureFetchIdentityResourceInterestsLocked(
      resource_filter, texture_upload_filter, command_fetch);

  if (PlumeTrackedResource* command_resource =
          FindDirectResourceLocked(command_texture)) {
    add_tracked_texture(command_resource);
    const bool exact_billboard_upload_identity =
        command_resource->guest_address == command_texture &&
        IsFableExactTitleBillboardTileUploadIdentity(
            command_texture, command_fetch_info, *command_resource);
    if (exact_billboard_upload_identity) {
      added_exact_title_upload_identity = true;
    }
  }

  add_exact_title_upload_identity(
      FableDirectFindTextureIdentityByObject(command_texture));
  if (command_fetch_info.valid) {
    add_texture_identity_address(command_fetch_info.base_address);
    if (IsFableTitleBillboardTileUploadBase(command_fetch_info.base_address,
                                            command_fetch_info.width,
                                            command_fetch_info.height)) {
      AddFableTitleBillboardTileUploadAliasResourceInterestsLocked(
          resource_filter, texture_upload_filter, command_fetch_info.base_address,
          command_fetch_info.width, command_fetch_info.height);
    }
    add_exact_title_upload_identity(
        FableDirectFindTextureIdentityByBase(command_fetch_info.base_address));
    add_exact_title_upload_identity_objects(
        FableDirectFindTextureIdentityObjectsByBase(
            command_fetch_info.base_address));
  }
  if (HasTrackedFetchDwords(command_fetch)) {
    add_exact_title_upload_identity(
        FableDirectFindTextureIdentityByFetch(command_fetch));
    add_exact_title_upload_identity_objects(
        FableDirectFindTextureIdentityObjectsByFetch(command_fetch));
  }
  if (PlumeTrackedResource* texture = FindDirectResourceLocked(command_texture)) {
    add_exact_title_upload_identity(
        FableDirectFindTextureIdentityByObject(texture->guest_address));
    add_exact_title_upload_identity(
        FableDirectFindTextureIdentityByObject(texture->parent_address));
    add_exact_title_upload_identity(FableDirectFindTextureIdentityByBase(
        PlumeGuestTextureBaseAddress(*texture)));
    add_exact_title_upload_identity_objects(
        FableDirectFindTextureIdentityObjectsByBase(
            PlumeGuestTextureBaseAddress(*texture)));
    const std::array<uint32_t, 6> texture_fetch =
        PlumeGuestTextureFetchDwords(*texture);
    if (HasTrackedFetchDwords(texture_fetch)) {
      add_exact_title_upload_identity(
          FableDirectFindTextureIdentityByFetch(texture_fetch));
      add_exact_title_upload_identity_objects(
          FableDirectFindTextureIdentityObjectsByFetch(texture_fetch));
    }
  }

  const bool billboard_identity_expected =
      IsFableTitleBillboardAtlasCommandSlotLocked(command, slot) ||
      IsFableTitleBillboardCommandHeaderAddress(command_texture) ||
      (command_fetch_info.valid &&
       IsFableTitleBillboardCommandHeaderAddress(command_fetch_info.base_address));
  if (billboard_identity_expected && !added_exact_title_upload_identity) {
    static uint32_t logged_title_billboard_missing_exact_identity = 0;
    if (logged_title_billboard_missing_exact_identity < 16u) {
      REXLOG_INFO(
          "Refii plume title billboard atlas missing exact upload identity #{} "
          "frame={} slot={} guest=0x{:08X} base=0x{:08X} "
          "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_title_billboard_missing_exact_identity,
          g_direct_renderer.current_frame_index, slot, command_texture,
          command_fetch_info.valid ? command_fetch_info.base_address : 0u,
          command_fetch[0], command_fetch[1], command_fetch[2],
          command_fetch[3], command_fetch[4], command_fetch[5],
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
  }
}

void AddMissingDirectResourceInterest(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter, uint32_t address,
    bool texture_upload) {
  if (address == 0) {
    return;
  }
  AddDirectResourceInterest(resource_filter, address);
  if (texture_upload) {
    AddDirectResourceInterest(texture_upload_filter, address);
  }
}

bool DirectCommandInputWaitReasonReady(
    const DirectCommandInputWaitReason& reason) {
  return reason.reason == nullptr || std::strcmp(reason.reason, "ready") == 0;
}

DirectCommandInputWaitReason FindDirectDrawNonTextureInputWaitReasonLocked(
    const FableDirectRenderCommand& command) {
  DirectCommandInputWaitReason reason;
  switch (command.type) {
    case FableDirectRenderCommandType::kDrawPrimitive:
    case FableDirectRenderCommandType::kDrawIndexedPrimitive:
    case FableDirectRenderCommandType::kDrawPrimitiveUP:
      break;
    default:
      return reason;
  }

  bool materialized_private_rect_color_target = false;
  for (uint32_t render_target : command.resources.render_targets) {
    if (render_target == 0) {
      continue;
    }
    if (DirectCommandCanMaterializePrivateRectTargetLocked(command,
                                                           render_target)) {
      materialized_private_rect_color_target = true;
      continue;
    }
    if (!DirectImageResourceReadyLocked(render_target)) {
      reason.reason = "rt";
      reason.guest = render_target;
      return reason;
    }
  }
  if (!materialized_private_rect_color_target &&
      command.resources.depth_stencil != 0 &&
      !DirectImageResourceReadyLocked(command.resources.depth_stencil)) {
    reason.reason = "depth";
    reason.guest = command.resources.depth_stencil;
    return reason;
  }
  if (!DirectShaderResourceReadyLocked(command.resources.vertex_shader)) {
    reason.reason = "vs";
    reason.guest = command.resources.vertex_shader;
    return reason;
  }
  if (!DirectShaderResourceReadyLocked(command.resources.pixel_shader)) {
    reason.reason = "ps";
    reason.guest = command.resources.pixel_shader;
    return reason;
  }

  const bool synthetic_private_rect = UseSyntheticPrivateRectInputLayout(command);
  PlumeTrackedResource* declaration =
      synthetic_private_rect
          ? nullptr
          : FindDirectReadyDeclarationResourceByIdentityLocked(
                command.resources.vertex_declaration);
  if (!synthetic_private_rect && !declaration) {
    reason.reason = "decl";
    reason.guest = command.resources.vertex_declaration;
    return reason;
  }

  if (command.draw.draw.indexed && command.resources.index_buffer != 0 &&
      !command.inline_index_buffer.ready() &&
      !DirectBufferResourceReadyLocked(command.resources.index_buffer)) {
    reason.reason = "index";
    reason.guest = command.resources.index_buffer;
    return reason;
  }

  std::array<bool, 16> stream_required = {};
  if (synthetic_private_rect) {
    stream_required[0] = true;
  } else if (declaration) {
    for (const plume::RenderInputSlot& input_slot :
         declaration->guest_vertex_declaration.input_slots) {
      if (input_slot.index < stream_required.size()) {
        stream_required[input_slot.index] = true;
      }
    }
  }
  for (size_t slot = 0; slot < command.resources.streams.size(); ++slot) {
    if (!stream_required[slot]) {
      continue;
    }
    const RendererStreamBindingSnapshot& stream = command.resources.streams[slot];
    if (stream.buffer == 0 ||
        DirectInlineBufferCoversAddress(command.inline_vertex_buffers[slot],
                                        stream.buffer) ||
        DirectBufferResourceReadyLocked(stream.buffer)) {
      continue;
    }
    reason.reason = "stream";
    reason.slot = static_cast<uint32_t>(slot);
    reason.guest = stream.buffer;
    return reason;
  }

  return reason;
}

bool DirectCommandInputReadyCacheEligibleLocked(
    const FableDirectRenderCommand& command) {
  switch (command.type) {
    case FableDirectRenderCommandType::kDrawPrimitive:
    case FableDirectRenderCommandType::kDrawIndexedPrimitive:
    case FableDirectRenderCommandType::kDrawPrimitiveUP:
      break;
    default:
      return false;
  }
  return command.draw.source != RendererDrawEventSource::kPixVideo &&
         IsFableTitleSceneTextureSyncCommandLocked(command) &&
         !DirectCommandHasExactSyntheticPrivateRectCopyInputsLocked(command);
}

uint64_t DirectCommandInputReadyCacheKeyLocked(
    const FableDirectRenderCommand& command) {
  uint64_t key = 0x9E3779B97F4A7C15ull;
  const auto mix = [&](uint64_t value) {
    key ^= value + 0x9E3779B97F4A7C15ull + (key << 6u) + (key >> 2u);
  };
  const uint32_t texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(command);

  mix(static_cast<uint32_t>(command.type));
  mix(static_cast<uint32_t>(command.draw.source));
  mix(command.draw.draw.indexed ? 1u : 0u);
  mix(command.draw.draw.primitive_type);
  mix(command.draw.draw.base_vertex);
  mix(command.draw.draw.start);
  mix(command.draw.draw.primitive_count);
  mix(command.resources.render_targets[0]);
  mix(command.resources.depth_stencil);
  mix(command.resources.vertex_shader);
  mix(command.resources.pixel_shader);
  mix(command.resources.shader_roots.vertex_shader.symbol_address);
  mix(command.resources.shader_roots.vertex_shader.call_serial);
  mix(command.resources.shader_roots.pixel_shader.symbol_address);
  mix(command.resources.shader_roots.pixel_shader.call_serial);
  mix(command.resources.vertex_declaration);
  mix(command.resources.index_buffer);
  mix(command.inline_index_buffer.ready() ? 1u : 0u);
  if (command.inline_index_buffer.ready()) {
    mix(command.inline_index_buffer.address);
    mix(command.inline_index_buffer.byte_size);
    mix(command.inline_index_buffer.stride_or_format);
  }
  mix(texture_slot_mask);

  for (uint32_t slot = 0; slot < command.resources.streams.size(); ++slot) {
    const RendererStreamBindingSnapshot& stream = command.resources.streams[slot];
    if (stream.buffer == 0 &&
        !command.inline_vertex_buffers[slot].ready()) {
      continue;
    }
    mix(0x1000u + slot);
    mix(stream.buffer);
    mix(stream.offset);
    mix(stream.stride);
    const FableDirectInlineBuffer& inline_buffer =
        command.inline_vertex_buffers[slot];
    mix(inline_buffer.ready() ? 1u : 0u);
    if (inline_buffer.ready()) {
      mix(inline_buffer.address);
      mix(inline_buffer.byte_size);
      mix(inline_buffer.stride_or_format);
    }
  }

  for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
    if (!DirectCommandUsesTextureSlotForSyncLocked(command, texture_slot_mask,
                                                   slot)) {
      continue;
    }
    mix(0x2000u + slot);
    mix(command.resources.textures[slot]);
    const std::array<uint32_t, 6> fetch =
        DirectCommandTextureFetchDwords(command, slot);
    for (uint32_t dword : fetch) {
      mix(dword);
    }
  }

  return key;
}

bool FindCachedDirectCommandInputReadyLocked(uint64_t cache_key) {
  const auto it = g_direct_renderer.command_input_ready_cache.find(cache_key);
  if (it == g_direct_renderer.command_input_ready_cache.end()) {
    return false;
  }
  const DirectCommandInputReadyCacheEntry& entry = it->second;
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  if (entry.resource_serial != g_direct_renderer.resource_serial ||
      entry.resource_destroys != g_direct_renderer.resource_destroys ||
      entry.private_copy_serial != registry.private_texture_copy_serial ||
      entry.surface_texture_link_serial !=
          registry.surface_texture_link_serial ||
      entry.texture_identity_serial != registry.texture_identity_serial ||
      entry.private_texture_alias_serial !=
          g_direct_renderer.private_texture_alias_serial) {
    g_direct_renderer.command_input_ready_cache.erase(it);
    return false;
  }

  if (g_direct_renderer.diagnostics.logged_direct_input_ready_cache_hits < 64u) {
    REXLOG_INFO(
        "Refii plume direct input-ready cache hit #{} frame={} "
        "cache_frame={} resource_serial={} destroys={} copy_serial={} "
        "surface_link_serial={} texture_identity_serial={}",
        ++g_direct_renderer.diagnostics.logged_direct_input_ready_cache_hits,
        g_direct_renderer.current_frame_index, entry.frame_index,
        static_cast<unsigned long long>(g_direct_renderer.resource_serial),
        static_cast<unsigned long long>(g_direct_renderer.resource_destroys),
        static_cast<unsigned long long>(registry.private_texture_copy_serial),
        static_cast<unsigned long long>(registry.surface_texture_link_serial),
        static_cast<unsigned long long>(registry.texture_identity_serial));
  }
  return true;
}

void RememberCachedDirectCommandInputReadyLocked(uint64_t cache_key) {
  constexpr size_t kMaxDirectCommandInputReadyCacheEntries = 4096u;
  if (g_direct_renderer.command_input_ready_cache.size() >=
      kMaxDirectCommandInputReadyCacheEntries) {
    g_direct_renderer.command_input_ready_cache.clear();
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  DirectCommandInputReadyCacheEntry entry;
  entry.frame_index = g_direct_renderer.current_frame_index;
  entry.resource_serial = g_direct_renderer.resource_serial;
  entry.resource_destroys = g_direct_renderer.resource_destroys;
  entry.private_copy_serial = registry.private_texture_copy_serial;
  entry.surface_texture_link_serial = registry.surface_texture_link_serial;
  entry.texture_identity_serial = registry.texture_identity_serial;
  entry.private_texture_alias_serial =
      g_direct_renderer.private_texture_alias_serial;
  g_direct_renderer.command_input_ready_cache[cache_key] = entry;
}

bool DirectCommandNonTextureInputsReadyLocked(
    const FableDirectRenderCommand& command) {
  return DirectCommandInputWaitReasonReady(
      FindDirectDrawNonTextureInputWaitReasonLocked(command));
}

bool DirectCommandInputsReadyWithoutMissingInterestLocked(
    const FableDirectRenderCommand& command) {
  switch (command.type) {
    case FableDirectRenderCommandType::kClear:
      for (uint32_t render_target : command.resources.render_targets) {
        if (render_target == 0) {
          continue;
        }
        if (!DirectImageResourceReadyLocked(render_target)) {
          if (DirectSetupClearCanMaterializePrivateRectTargetLocked(
                  command, render_target, false)) {
            continue;
          }
          if (FindDirectPrivateTextureCopyOutputActionForClearTargetLocked(
                  command, render_target, false) ||
              FindDirectPrivateTextureCopySetupActionForClearTargetLocked(
                  command, render_target, false)) {
            continue;
          }
          return false;
        }
      }
      if ((command.clear.flags & (kFableClearDepth | kFableClearStencil)) !=
              0 &&
          command.resources.depth_stencil != 0 &&
          !DirectImageResourceReadyLocked(command.resources.depth_stencil)) {
        if (DirectSetupClearCanMaterializePrivateRectTargetLocked(
                command, command.resources.depth_stencil, true)) {
          return true;
        }
        if (FindDirectPrivateTextureCopyOutputActionForClearTargetLocked(
                command, command.resources.depth_stencil, true) ||
            FindDirectPrivateTextureCopySetupActionForClearTargetLocked(
                command, command.resources.depth_stencil, true)) {
          return true;
        }
        return false;
      }
      return true;
    case FableDirectRenderCommandType::kResolveCopy: {
      const DirectResolveCopySourceSelection selection =
          SelectDirectResolveCopySourceLocked(command);
      if (!selection.ready) {
        return false;
      }
      return DirectResolveCopyDestinationBase(command) != 0;
    }
    case FableDirectRenderCommandType::kDrawPrimitive:
    case FableDirectRenderCommandType::kDrawIndexedPrimitive:
    case FableDirectRenderCommandType::kDrawPrimitiveUP:
      break;
    default:
      return true;
  }

  const bool input_ready_cache_eligible =
      DirectCommandInputReadyCacheEligibleLocked(command);
  const uint64_t input_ready_cache_key =
      input_ready_cache_eligible
          ? DirectCommandInputReadyCacheKeyLocked(command)
          : 0ull;
  if (input_ready_cache_eligible &&
      FindCachedDirectCommandInputReadyLocked(input_ready_cache_key)) {
    return true;
  }

  if (!DirectCommandInputWaitReasonReady(
          FindDirectDrawNonTextureInputWaitReasonLocked(command))) {
    return false;
  }

  if (DirectCommandHasExactSyntheticPrivateRectCopyInputsLocked(command)) {
    return true;
  }

  const uint32_t texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(command);
  for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
    if (!DirectCommandUsesTextureSlotForSyncLocked(command, texture_slot_mask,
                                                   slot)) {
      continue;
    }
    const uint32_t texture_guest = command.resources.textures[slot];
    const DirectTextureFetchInfo command_fetch_info =
        DirectTextureFetchInfoFromDwords(
            DirectCommandTextureFetchDwords(command, slot));
    const bool command_tiny_title_fetch_identity =
        command_fetch_info.valid &&
        IsFableTinyTitleUploadedTextureIdentityBase(
            command_fetch_info.base_address, command_fetch_info.width,
            command_fetch_info.height);
    if (texture_guest == 0 && !command_tiny_title_fetch_identity) {
      continue;
    }
    if (DirectCommandSlotUsesReadyInlinePixVideoPlaneLocked(command, slot,
                                                           texture_guest)) {
      continue;
    }
    if (ResolveDirectExactBinkWrapperPlaneForSlotLocked(command, slot)) {
      continue;
    }
    if (DirectCommandSlotUsesExactBinkWrapperPlaneLocked(command, slot)) {
      return false;
    }
    if (IsFableTitleBillboardAtlasCommandSlotLocked(command, slot) &&
        !FindExactFableTitleBillboardAtlasForCommandSlotLocked(
            command, slot, FindDirectResourceLocked(texture_guest))) {
      return false;
    }
    PlumeTrackedResource* direct_texture = FindDirectResourceLocked(texture_guest);
    if (IsFableTitleGuiTextAtlasCommandLocked(command, slot) &&
        !ResolveFableTitleGuiTextAtlasTextureLocked(command, slot)) {
      return false;
    }
    if (DirectCommandCanBindMaterializedPrivateTextureHeaderLocked(
            command, slot, direct_texture) ||
        DirectCommandCanSnapshotPrivateTextureFeedbackLocked(
            command, slot, direct_texture) ||
        DirectCommandCanBootstrapRegisteredPrivateRectMaterializationLocked(
            command, slot, direct_texture)) {
      continue;
    }
    if (DirectCommandTextureSlotExactProducerProvidesSampleLocked(
            command, slot, direct_texture)) {
      continue;
    }
    if (DirectCommandTextureSlotDirectResourceProvidesSampleLocked(
            command, slot, direct_texture)) {
      continue;
    }
    if (DirectTinyTitleCachedTextureCanBindLocked(command, texture_guest,
                                                  direct_texture)) {
      continue;
    }
    if (ResolveDirectTinyTitleUploadedTextureForCommandSlotLocked(
            command, slot, direct_texture)) {
      continue;
    }
    if (command_tiny_title_fetch_identity &&
        (!direct_texture ||
         g_direct_renderer.known_uploads.find(direct_texture->guest_address) ==
             g_direct_renderer.known_uploads.end())) {
      return false;
    }
    if (PlumeTrackedResource* sampled =
            ResolveDirectSampledTextureForCommandLocked(command, slot)) {
      if (DirectCommandTextureSlotDirectResourceProvidesSampleLocked(
              command, slot, sampled) ||
          DirectTinyTitleCachedTextureCanBindLocked(command, texture_guest,
                                                    sampled)) {
        continue;
      }
    }
    return false;
  }

  if (input_ready_cache_eligible) {
    RememberCachedDirectCommandInputReadyLocked(input_ready_cache_key);
  }
  return true;
}

void CollectDirectVideoCommandNonPlaneResourceInterestLocked(
    const FableDirectRenderCommand& command,
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter) {
  if (!IsDirectDrawCommand(command)) {
    return;
  }

  for (uint32_t render_target : command.resources.render_targets) {
    if (render_target != 0 && !DirectImageResourceReadyLocked(render_target)) {
      AddMissingDirectResourceInterest(resource_filter, texture_upload_filter,
                                       render_target, false);
    }
  }
  if (command.resources.depth_stencil != 0 &&
      !DirectImageResourceReadyLocked(command.resources.depth_stencil)) {
    AddMissingDirectResourceInterest(resource_filter, texture_upload_filter,
                                     command.resources.depth_stencil, false);
  }
  if (!DirectShaderResourceReadyLocked(command.resources.vertex_shader)) {
    AddMissingDirectResourceInterest(resource_filter, texture_upload_filter,
                                     command.resources.vertex_shader, false);
    AddDirectShaderIdentityResourceInterestsLocked(
        resource_filter, command.resources.vertex_shader);
  }
  if (!DirectShaderResourceReadyLocked(command.resources.pixel_shader)) {
    AddMissingDirectResourceInterest(resource_filter, texture_upload_filter,
                                     command.resources.pixel_shader, false);
    AddDirectShaderIdentityResourceInterestsLocked(
        resource_filter, command.resources.pixel_shader);
  }

  PlumeTrackedResource* declaration =
      FindDirectReadyDeclarationResourceByIdentityLocked(
          command.resources.vertex_declaration);
  if (!declaration) {
    AddMissingDirectResourceInterest(resource_filter, texture_upload_filter,
                                     command.resources.vertex_declaration,
                                     false);
    AddDirectVertexDeclarationIdentityResourceInterestsLocked(
        resource_filter, command.resources.vertex_declaration);
  }

  if (command.draw.draw.indexed && command.resources.index_buffer != 0 &&
      !command.inline_index_buffer.ready() &&
      !DirectBufferResourceReadyLocked(command.resources.index_buffer)) {
    AddMissingDirectResourceInterest(resource_filter, texture_upload_filter,
                                     command.resources.index_buffer, false);
  }

  std::array<bool, 16> stream_required = {};
  if (declaration) {
    for (const plume::RenderInputSlot& input_slot :
         declaration->guest_vertex_declaration.input_slots) {
      if (input_slot.index < stream_required.size()) {
        stream_required[input_slot.index] = true;
      }
    }
  } else {
    for (size_t slot = 0; slot < command.resources.streams.size(); ++slot) {
      stream_required[slot] = command.resources.streams[slot].buffer != 0;
    }
  }

  for (size_t slot = 0; slot < command.resources.streams.size(); ++slot) {
    if (!stream_required[slot]) {
      continue;
    }
    const RendererStreamBindingSnapshot& stream = command.resources.streams[slot];
    if (stream.buffer == 0 ||
        DirectInlineBufferCoversAddress(command.inline_vertex_buffers[slot],
                                        stream.buffer) ||
        DirectBufferResourceReadyLocked(stream.buffer)) {
      continue;
    }
    AddMissingDirectResourceInterest(resource_filter, texture_upload_filter,
                                     stream.buffer, false);
  }
}

// Per-base exponential backoff for failed exact-title resolutions. Run 410:
// with the observer draw-event pipeline gated, two contentless GUI-atlas
// header records (bases 0x1A0D9000/0x1A0EA000) failed resolution EVERY frame
// with frozen serials, and this collect + the SyncDirectResourcesLocked it
// triggers re-ran the full interest/sync/starved-retry machine each time
// (~27ms/frame, "sync starved request #2560"). There is no attempt cap in
// this path by design (a blunt cap would leave late-arriving panels stale
// forever); backoff retries at 1,2,4,...,kMax frames instead, and the memo is
// erased the moment a resolution succeeds, so real content arriving through
// the unlock publish restores full freshness on its next retry.
std::unordered_map<uint32_t, ExactTitlePreflightBackoff>
    g_exact_title_preflight_backoff;

#endif

}  // namespace refii::renderer
