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

// EXTRACTED FROM RecordDirectDrawCommandLocked 2026-07-27.
//
// This was a 3,359-line lambda -- 70% of that 4,819-line function and 6.1% of
// this entire file -- and it is not draw code at all: it is exact-surface-copy
// resolution that happened to live inside the draw recorder. Segmenting the
// enclosing function by dominant callee showed only 0.7% of it doing actual
// draw/pipeline work.
//
// Moved as a free function in the SAME TU on purpose: all mutable state flows
// through the file-static g_direct_renderer, so nothing has to be relocated and
// the anonymous-namespace boundary is untouched. Verified before moving: ONE
// call site, exactly four captured values (draw_command with 367 reads and 0
// writes, device, command_list, pixel_texture_slot_mask), no writes to any
// enclosing local, and no gtl:: calls -- so it cannot collide with the GTL
// cutover. `draw_command` keeps its non-const reference binding rather than
// being tightened to const, because the helpers it is forwarded to were not all
// audited for constness and that tightening buys nothing here.
bool FlushExactSurfaceCopyForSlotLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    FableDirectRenderCommand& draw_command, uint32_t pixel_texture_slot_mask,
    uint32_t slot) {
    if (draw_command.draw.source == RendererDrawEventSource::kPixVideo ||
        slot >= draw_command.resources.textures.size() ||
        !DirectCommandUsesTextureSlotForSyncLocked(
            draw_command, pixel_texture_slot_mask, slot)) {
      return false;
    }

    const uint32_t guest = draw_command.resources.textures[slot];
    if (guest == 0) {
      return false;
    }
    PlumeTrackedResource* direct_texture = FindDirectResourceLocked(guest);

    if (DirectCommandSlotUsesExactBinkWrapperPlaneLocked(draw_command, slot)) {
      if (PlumeTrackedResource* plane =
              ResolveDirectExactBinkWrapperPlaneForSlotLocked(draw_command,
                                                              slot)) {
        return true;
      }
      return false;
    }

    const bool exact_title_upload_source =
        DirectCommandSlotUsesExactTitleUploadSourceLocked(draw_command, slot);
    const bool exact_begin_vertices_title_action =
        draw_command.draw.source != RendererDrawEventSource::kPixVideo &&
        draw_command.draw.private_texture_wrapper_symbol_address ==
            kFableBeginVerticesTitleWrapperSymbol &&
        (draw_command.draw.private_texture_wrapper_call_serial != 0 ||
         draw_command.draw.private_texture_wrapper_context_key != 0);
    const bool private_or_title_surface_copy_command =
        DirectCommandUsesPrivateOrTitleSurfaceCopyPathLocked(draw_command) ||
        DirectCommandUsesFablePrivateTexturePath(draw_command, slot,
                                                 direct_texture) ||
        exact_title_upload_source || exact_begin_vertices_title_action ||
        IsFableTitleSceneTargetLocked(draw_command.resources.render_targets[0]) ||
        IsFableTitleSceneVertexShaderCommandLocked(draw_command) ||
        IsFableTitleSceneTextureSyncCommandLocked(draw_command);
    const uint64_t draw_serial =
        draw_command.draw_serial != 0 ? draw_command.draw_serial
                                      : draw_command.serial;
    auto log_exact_surface_copy_miss =
        [&](const char* reason, const FableSurfaceTextureLink* link,
            const PlumeTrackedResource* source,
            const PlumeTrackedResource* destination) {
      if (!RendererDeepObserverHooksEnabled()) {
        return;
      }
      if (slot != 0u && slot != 2u && slot != kFablePrivateRectCopyTextureSlot) {
        return;
      }
      static uint32_t logged_exact_surface_copy_misses = 0;
      if (logged_exact_surface_copy_misses >= 48u) {
        return;
      }
      const bool future_exact_link =
          reason != nullptr &&
          (std::strcmp(reason, "exact_alias_link_future_frame") == 0 ||
           std::strcmp(reason, "exact_alias_link_future_draw") == 0);
      if (future_exact_link) {
        static uint32_t logged_exact_future_surface_copy_rejects = 0;
        if (logged_exact_future_surface_copy_rejects >= 48u) {
          return;
        }
        REXLOG_INFO(
            "Refii plume exact future surface copy reject #{} "
            "frame={} reason={} serial={} draw={} source_kind={} slot={} "
            "guest=0x{:08X} link_tex=0x{:08X} link_surface=0x{:08X} "
            "link_backing=0x{:08X} link_slot={} link_frame={} link_draw={} "
            "link_call={} link_context=0x{:016X} link_symbol=0x{:08X} "
            "setup={:08X}/{:08X}/{:08X}/{:08X} "
            "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_future_surface_copy_rejects,
            g_direct_renderer.current_frame_index, reason,
            static_cast<unsigned long long>(draw_command.serial),
            static_cast<unsigned long long>(draw_serial),
            static_cast<uint32_t>(draw_command.draw.source), slot, guest,
            link ? link->texture : 0u, link ? link->source_surface : 0u,
            link ? link->backing_texture : 0u,
            link ? link->source_slot : 0xFFFFFFFFu,
            link ? link->frame_index : 0u,
            static_cast<unsigned long long>(link ? link->draw_serial : 0u),
            static_cast<unsigned long long>(link ? link->copy_call_serial : 0u),
            static_cast<unsigned long long>(link ? link->copy_context_key : 0u),
            link ? link->copy_symbol_address : 0u,
            link ? link->wrapper_setup_globals[0] : 0u,
            link ? link->wrapper_setup_globals[1] : 0u,
            link ? link->wrapper_setup_globals[2] : 0u,
            link ? link->wrapper_setup_globals[3] : 0u,
            draw_command.resources.render_targets[0],
            draw_command.resources.depth_stencil,
            draw_command.resources.vertex_shader,
            draw_command.resources.pixel_shader);
        return;
      }
      REXLOG_INFO(
          "Refii plume exact surface copy reject #{} frame={} "
          "reason={} serial={} draw={} source_kind={} slot={} guest=0x{:08X} "
          "link_tex=0x{:08X} link_surface=0x{:08X} link_backing=0x{:08X} "
          "link_slot={} link_frame={} link_draw={} link_call={} "
          "link_context=0x{:016X} link_symbol=0x{:08X} "
          "setup={:08X}/{:08X}/{:08X}/{:08X} "
          "src=0x{:08X}/{}x{}/fmt={}/rt={}/depth={} "
          "dst=0x{:08X}/{}x{}/fmt={}/rt={}/depth={} kind={} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_surface_copy_misses,
          g_direct_renderer.current_frame_index,
          reason ? reason : "unknown",
          static_cast<unsigned long long>(draw_command.serial),
          static_cast<unsigned long long>(draw_serial),
          static_cast<uint32_t>(draw_command.draw.source), slot, guest,
          link ? link->texture : 0u, link ? link->source_surface : 0u,
          link ? link->backing_texture : 0u,
          link ? link->source_slot : 0xFFFFFFFFu,
          link ? link->frame_index : 0u,
          static_cast<unsigned long long>(link ? link->draw_serial : 0u),
          static_cast<unsigned long long>(link ? link->copy_call_serial : 0u),
          static_cast<unsigned long long>(link ? link->copy_context_key : 0u),
          link ? link->copy_symbol_address : 0u,
          link ? link->wrapper_setup_globals[0] : 0u,
          link ? link->wrapper_setup_globals[1] : 0u,
          link ? link->wrapper_setup_globals[2] : 0u,
          link ? link->wrapper_setup_globals[3] : 0u,
          source ? source->guest_address : 0u,
          source ? PlumeGuestTextureWidth(*source) : 0u,
          source ? PlumeGuestTextureHeight(*source) : 0u,
          source ? static_cast<uint32_t>(PlumeGuestTextureFormat(*source))
                 : 0u,
          source && source->texture_render_target ? 1u : 0u,
          source && source->texture_depth_target ? 1u : 0u,
          destination ? destination->guest_address : 0u,
          destination ? PlumeGuestTextureWidth(*destination) : 0u,
          destination ? PlumeGuestTextureHeight(*destination) : 0u,
          destination
              ? static_cast<uint32_t>(PlumeGuestTextureFormat(*destination))
              : 0u,
          destination && destination->texture_render_target ? 1u : 0u,
          destination && destination->texture_depth_target ? 1u : 0u,
          link ? FablePrivateTextureCopyKindName(link->kind) : "unknown",
          draw_command.resources.render_targets[0],
          draw_command.resources.depth_stencil,
          draw_command.resources.vertex_shader,
          draw_command.resources.pixel_shader);
    };
    constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
    auto link_visible = [&](const FableSurfaceTextureLink* link) {
      if (!link || !link->trusted_destination_edge) {
        return false;
      }
      const bool same_slot =
          link->source_slot == kUnknownSlot || link->source_slot == slot;
      const bool exact_private_chain_link =
          DirectSurfaceTextureLinkTouchesFablePrivateChain(*link, nullptr);
      const bool exact_private_chain_cross_slot =
          private_or_title_surface_copy_command && exact_private_chain_link;
      if (!same_slot && !exact_private_chain_cross_slot) {
        return false;
      }
      if (link->frame_index != 0 &&
          link->frame_index > g_direct_renderer.current_frame_index) {
        return false;
      }
      if (link->frame_index != 0 &&
          link->frame_index == g_direct_renderer.current_frame_index &&
          link->draw_serial != 0 && draw_serial != 0 &&
          link->draw_serial > draw_serial) {
        return false;
      }
      const bool current_target_pending =
          DirectSurfaceTextureLinkIsCurrentTargetExactEdgeLocked(
              draw_command, *link);
      if (private_or_title_surface_copy_command) {
        const std::array<uint32_t, 6> slot_fetch =
            DirectCommandTextureFetchDwords(draw_command, slot);
        const DirectTextureFetchInfo slot_fetch_info =
            DirectTextureFetchInfoFromDwords(slot_fetch);
        const bool exact_title_billboard_destination_link =
            exact_title_upload_source &&
            IsFableTitleBillboardAtlasCommandSlotLocked(draw_command, slot) &&
            DirectPrivateTextureCopyKindUsesColorBacking(link->kind) &&
            !DirectResourceAddressMatchesLocked(link->texture,
                                                link->source_surface) &&
            (DirectResourceAddressMatchesLocked(link->texture, guest) ||
             (slot_fetch_info.valid && link->texture_base_address != 0 &&
              link->texture_base_address == slot_fetch_info.base_address)) &&
            (!slot_fetch_info.valid ||
             link->texture_base_address == 0 ||
             link->texture_base_address == slot_fetch_info.base_address) &&
            (!HasTrackedFetchDwords(link->texture_fetch_dwords) ||
             DirectTextureFetchDwordsMatch(link->texture_fetch_dwords,
                                           slot_fetch)) &&
            (link->target_content_serial != 0 ||
             link->backing_content_serial != 0 ||
             link->backing_texture != 0 ||
             link->sampled_source.kind !=
                 FableRegisteredSampleSourceKind::kUnknown);
        if (exact_title_billboard_destination_link) {
          return true;
        }
        const bool command_owned_private_chain_link =
            exact_private_chain_link &&
            DirectSurfaceTextureLinkWrapperOwnedByCommand(draw_command, *link);
        const bool command_visible_private_chain_link =
            exact_private_chain_link &&
            DirectSurfaceTextureLinkPrivateWrapperVisibleToCommandLocked(
                draw_command, *link);
        return current_target_pending ||
               ((command_owned_private_chain_link ||
                 command_visible_private_chain_link) &&
                link->texture != link->source_surface &&
                DirectSurfaceTextureLinkHasExactCopyPayloadIdentity(*link));
      }
      return current_target_pending;
    };
    auto exact_alias_link_visibility_reject_reason =
        [&](const FableSurfaceTextureLink* link,
            const RendererSurfaceCopyAliasSnapshot& alias) -> const char* {
      if (link_visible(link)) {
        return nullptr;
      }
      if (!link || !link->trusted_destination_edge) {
        return "exact_alias_link_untrusted";
      }
      if (link->frame_index != 0 &&
          link->frame_index > g_direct_renderer.current_frame_index) {
        return "exact_alias_link_future_frame";
      }
      if (link->frame_index != 0 &&
          link->frame_index == g_direct_renderer.current_frame_index &&
          link->draw_serial != 0 && draw_serial != 0 &&
          link->draw_serial > draw_serial) {
        return "exact_alias_link_future_draw";
      }
      if (!DirectSurfaceCopyAliasHasExactIdentity(alias)) {
        return "exact_alias_no_identity";
      }
      if (link->texture == link->source_surface) {
        return "exact_alias_self_edge";
      }
      if (!private_or_title_surface_copy_command &&
          !DirectSurfaceTextureLinkIsCurrentTargetExactEdgeLocked(
              draw_command, *link)) {
        return "exact_alias_target_family";
      }
      return nullptr;
    };
    auto exact_alias_link_visible =
        [&](const FableSurfaceTextureLink* link,
            const RendererSurfaceCopyAliasSnapshot& alias) {
          return exact_alias_link_visibility_reject_reason(link, alias) ==
                 nullptr;
    };
    bool exact_surface_copy_alias_seen = false;
    bool exact_surface_copy_alias_link_selected = false;
    bool exact_surface_copy_alias_reject_logged = false;
    bool exact_surface_copy_alias_missing_link = false;
    bool exact_surface_copy_alias_snapshot_valid = false;
    RendererSurfaceCopyAliasSnapshot exact_surface_copy_alias_snapshot = {};
    FableSurfaceTextureLink exact_surface_copy_alias_link_snapshot;
    const std::array<uint32_t, 6> command_fetch =
        DirectCommandTextureFetchDwords(draw_command, slot);
    const DirectTextureFetchInfo command_fetch_info =
        DirectTextureFetchInfoFromDwords(command_fetch);
    auto find_exact_link_by_surface_alias_destination =
        [&](uint32_t destination_texture) -> const FableSurfaceTextureLink* {
      RendererSurfaceCopyAliasSnapshot alias;
      if (!FindDirectSurfaceCopyAliasByDestinationAddressLocked(
              destination_texture, &alias) ||
          !DirectSurfaceCopyAliasHasExactIdentity(alias)) {
        return nullptr;
      }
      exact_surface_copy_alias_seen = true;
      exact_surface_copy_alias_snapshot = alias;
      exact_surface_copy_alias_snapshot_valid = true;
      auto exact_alias_source_matches_link =
          [&](const FableSurfaceTextureLink* link) {
        if (!link) {
          return false;
        }
        if (DirectResourceAddressMatchesLocked(link->source_surface,
                                               alias.source_surface)) {
          return true;
        }
        return DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
            *link, link->source_surface, alias.source_surface);
      };
      auto exact_link_has_visible_private_header_action_payload =
          [&](const FableSurfaceTextureLink* link) {
        if (!private_or_title_surface_copy_command || !link ||
            !link->trusted_destination_edge ||
            !DirectSurfaceTextureLinkTouchesFablePrivateChain(*link, nullptr) ||
            link->texture == link->source_surface ||
            !DirectResourceAddressMatchesLocked(link->texture,
                                                alias.destination_surface) ||
            !exact_alias_source_matches_link(link)) {
          return false;
        }

        const uint64_t command_draw_serial =
            draw_serial != 0 ? draw_serial : draw_command.serial;
        auto action_visible =
            [&](const FablePrivateTextureCopyAction& action) {
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
        auto action_wrapper_matches_link =
            [&](const FablePrivateTextureCopyAction& action) {
          if (!DirectPrivateTextureCopyActionHasExactWrapperIdentity(action)) {
            return false;
          }
          if (action.wrapper_call_serial != 0 &&
              link->copy_call_serial != 0 &&
              action.wrapper_call_serial != link->copy_call_serial) {
            return false;
          }
          if (action.wrapper_context_key != 0 &&
              link->copy_context_key != 0 &&
              action.wrapper_context_key != link->copy_context_key) {
            return false;
          }
          if (action.wrapper_symbol_address != 0 &&
              link->copy_symbol_address != 0 &&
              !FablePrivateRectWrapperSymbolsCompatible(
                  action.wrapper_symbol_address, link->copy_symbol_address)) {
            return false;
          }
          return action.wrapper_call_serial == link->copy_call_serial ||
                 action.wrapper_context_key == link->copy_context_key ||
                 (action.wrapper_symbol_address != 0 &&
                  action.wrapper_symbol_address == link->copy_symbol_address);
        };
        auto action_endpoint_matches_alias_destination =
            [&](const FablePrivateTextureCopyAction& action) {
          if (DirectResourceAddressMatchesLocked(action.source_texture,
                                                alias.destination_surface) ||
              DirectResourceAddressMatchesLocked(action.backing_texture,
                                                alias.destination_surface)) {
            return true;
          }
          if (command_fetch_info.valid &&
              (action.source_base_address == command_fetch_info.base_address ||
               action.backing_base_address == command_fetch_info.base_address)) {
            return true;
          }
          const uint64_t command_fetch_key =
              FableDirectTextureFetchKey(command_fetch);
          return command_fetch_key != 0 &&
                 (FableDirectTextureFetchKey(action.source_fetch_dwords) ==
                      command_fetch_key ||
                  FableDirectTextureFetchKey(action.backing_fetch_dwords) ==
                      command_fetch_key);
        };
        auto action_has_ready_payload =
            [&](const FablePrivateTextureCopyAction& action) {
          if (!action_visible(action) ||
              action.kind == FablePrivateTextureCopyKind::kDepth ||
              !DirectPrivateTextureCopyKindUsesColorBacking(action.kind) ||
              !action_wrapper_matches_link(action) ||
              !action_endpoint_matches_alias_destination(action) ||
              action.target_surface == 0) {
            return false;
          }
          const PlumeTrackedResource* target =
              FindDirectResourceLocked(action.target_surface);
          return target && IsImageResource(target->kind) &&
                 PlumeGuestImageReady(*target) &&
                 !DirectCommandBindsResourceAsTargetLocked(draw_command,
                                                           *target) &&
                 (FableTrackedResourceHasSampleableContent(*target) ||
                  FableTrackedResourceHasGpuProducedContent(*target)) &&
                 FableTrackedResourceVisibleContentSerial(*target) != 0;
        };
        auto any_action_has_ready_payload =
            [&](const std::vector<FablePrivateTextureCopyAction>* actions) {
          if (!actions) {
            return false;
          }
          for (auto it = actions->rbegin(); it != actions->rend(); ++it) {
            if (action_has_ready_payload(*it)) {
              return true;
            }
          }
          return false;
        };

        return any_action_has_ready_payload(
                   FableDirectFindPrivateTextureCopyActionsBySource(
                       link->source_surface)) ||
               any_action_has_ready_payload(
                   FableDirectFindPrivateTextureCopyActionsBySource(
                       link->texture)) ||
               any_action_has_ready_payload(
                   FableDirectFindPrivateTextureCopyActionsByTarget(
                       link->source_surface)) ||
               any_action_has_ready_payload(
                   FableDirectFindPrivateTextureCopyActionsByFetch(
                       command_fetch));
      };
      auto exact_link_alias_reject_reason =
          [&](const FableSurfaceTextureLink* link) -> const char* {
        if (!link || !link->trusted_destination_edge) {
          return "exact_alias_link_untrusted";
        }
        const bool has_matching_identity =
            (alias.copy_call_serial != 0 &&
             link->copy_call_serial == alias.copy_call_serial) ||
            (alias.copy_context_key != 0 &&
             link->copy_context_key == alias.copy_context_key) ||
            (alias.copy_symbol_address != 0 &&
             link->copy_symbol_address == alias.copy_symbol_address);
        const bool command_visible_private_chain_identity =
            private_or_title_surface_copy_command &&
            DirectSurfaceTextureLinkTouchesFablePrivateChain(*link, nullptr) &&
            DirectSurfaceTextureLinkPrivateWrapperVisibleToCommandLocked(
                draw_command, *link) &&
            DirectResourceAddressMatchesLocked(link->texture,
                                               alias.destination_surface) &&
            exact_alias_source_matches_link(link) &&
            (DirectSurfaceTextureLinkHasExactCopyPayloadIdentity(*link) ||
             exact_link_has_visible_private_header_action_payload(link));
        if (!has_matching_identity && !command_visible_private_chain_identity) {
          return "exact_alias_identity_mismatch";
        }
        if (!command_visible_private_chain_identity &&
            alias.copy_symbol_address != 0 && link->copy_symbol_address != 0 &&
            link->copy_symbol_address != alias.copy_symbol_address) {
          return "exact_alias_symbol_mismatch";
        }
        if (!command_visible_private_chain_identity &&
            link->copy_call_serial != 0 && alias.copy_call_serial != 0 &&
            link->copy_call_serial != alias.copy_call_serial) {
          return "exact_alias_call_mismatch";
        }
        if (!command_visible_private_chain_identity &&
            link->copy_context_key != 0 && alias.copy_context_key != 0 &&
            link->copy_context_key != alias.copy_context_key) {
          return "exact_alias_context_mismatch";
        }
        if (!DirectResourceAddressMatchesLocked(link->texture,
                                                alias.destination_surface)) {
          return "exact_alias_destination_mismatch";
        }
        if (!exact_alias_source_matches_link(link)) {
          return "exact_alias_source_mismatch";
        }
        return nullptr;
      };
      auto exact_link_matches_alias = [&](const FableSurfaceTextureLink* link) {
        return exact_link_alias_reject_reason(link) == nullptr;
      };
      auto exact_alias_reject_is_stale_identity = [](const char* reason) {
        return reason != nullptr &&
               (std::strcmp(reason, "exact_alias_identity_mismatch") == 0 ||
                std::strcmp(reason, "exact_alias_call_mismatch") == 0 ||
                std::strcmp(reason, "exact_alias_context_mismatch") == 0);
      };
      auto build_visible_exact_alias_snapshot_link =
          [&](const char* stale_reason) -> const FableSurfaceTextureLink* {
        if (!private_or_title_surface_copy_command ||
            !exact_alias_reject_is_stale_identity(stale_reason)) {
          return nullptr;
        }
        const bool begin_vertices_alias_snapshot =
            alias.copy_symbol_address == kFableBeginVerticesTitleWrapperSymbol &&
            (draw_command.draw.private_texture_wrapper_symbol_address ==
                 kFableBeginVerticesTitleWrapperSymbol ||
             IsFableTitleSceneVertexShaderCommandLocked(draw_command) ||
             IsFableTitleSceneTargetLocked(draw_command.resources.render_targets[0]));
        const bool sibling_private_rect_alias_snapshot =
            (alias.copy_symbol_address == kFablePrivateRectSiblingWrapperSymbol ||
             alias.copy_symbol_address ==
                 kFablePrivateRectSiblingWrapperCallsite) &&
            (draw_command.draw.private_texture_wrapper_symbol_address ==
                 kFableD3DBeginIndexedVerticesWrapperSymbol ||
             draw_command.draw.private_texture_wrapper_symbol_address ==
                 kFableBeginVerticesTitleWrapperSymbol ||
             draw_command.draw.private_texture_wrapper_symbol_address ==
                 kFablePrivateRectSiblingWrapperSymbol ||
             draw_command.draw.private_texture_wrapper_symbol_address ==
                 kFablePrivateRectSiblingWrapperCallsite ||
             DirectSyntheticPrivateRectCopyCanExecuteFromConcreteSourceLocked(
                 draw_command) ||
             IsFableTitleSceneVertexShaderCommandLocked(draw_command) ||
             IsFableTitleSceneTargetLocked(draw_command.resources.render_targets[0]));
        if (!begin_vertices_alias_snapshot &&
            !sibling_private_rect_alias_snapshot) {
          return nullptr;
        }
        PlumeTrackedResource* alias_source =
            FindDirectResourceLocked(alias.source_surface);
        PlumeTrackedResource* alias_destination =
            FindDirectResourceLocked(alias.destination_surface);
        if (!alias_source || !IsImageResource(alias_source->kind) ||
            !PlumeGuestImageReady(*alias_source)) {
          return nullptr;
        }

        FableSurfaceTextureLink alias_link = {};
        alias_link.copy_call_serial = alias.copy_call_serial;
        alias_link.copy_context_key = alias.copy_context_key;
        alias_link.copy_symbol_address = alias.copy_symbol_address;
        alias_link.source_slot = slot;
        alias_link.trusted_destination_edge = true;
        alias_link.texture = alias.destination_surface;
        alias_link.texture_base_address =
            alias_destination ? PlumeGuestTextureBaseAddress(*alias_destination)
                              : (command_fetch_info.valid
                                     ? command_fetch_info.base_address
                                     : 0u);
        alias_link.texture_width =
            alias_destination
                ? PlumeGuestTextureWidth(*alias_destination)
                : (command_fetch_info.valid ? command_fetch_info.width : 0u);
        alias_link.texture_height =
            alias_destination
                ? PlumeGuestTextureHeight(*alias_destination)
                : (command_fetch_info.valid ? command_fetch_info.height : 0u);
        alias_link.texture_format =
            alias_destination
                ? PlumeGuestTextureGuestFormat(*alias_destination)
                : (command_fetch_info.valid ? command_fetch_info.format : 0u);
        alias_link.texture_fetch_dwords =
            alias_destination ? PlumeGuestTextureFetchDwords(*alias_destination)
                              : command_fetch;
        alias_link.source_surface = alias.source_surface;
        alias_link.render_target = draw_command.resources.render_targets[0];
        alias_link.depth_stencil = draw_command.resources.depth_stencil;
        alias_link.vertex_shader = draw_command.resources.vertex_shader;
        alias_link.pixel_shader = draw_command.resources.pixel_shader;
        PopulateDirectSurfaceTextureLinkShaderRoots(alias_link, draw_command);
        alias_link.frame_index = g_direct_renderer.current_frame_index;
        alias_link.draw_serial = draw_serial;
        alias_link.kind = alias_source->texture_depth_target
                              ? FablePrivateTextureCopyKind::kDepth
                              : FablePrivateTextureCopyKind::kColor;
        alias_link.target_content_serial =
            FableTrackedResourceVisibleContentSerial(*alias_source);
        if (alias_link.target_content_serial != 0) {
          alias_link.sampled_source.kind =
              FableRegisteredSampleSourceKind::kGpuSurface;
          alias_link.sampled_source.resource = alias_source->guest_address;
          alias_link.sampled_source.content_serial =
              alias_link.target_content_serial;
        }
        if (!exact_alias_source_matches_link(&alias_link) ||
            !exact_alias_link_visible(&alias_link, alias)) {
          return nullptr;
        }

        exact_surface_copy_alias_link_snapshot = alias_link;
        static uint32_t logged_exact_alias_snapshot_links = 0;
        if (logged_exact_alias_snapshot_links < 48u) {
          REXLOG_INFO(
              "Refii plume exact IDA-backed alias snapshot link #{} frame={} "
              "reason={} slot={} guest=0x{:08X} source=0x{:08X}/{}x{} "
              "destination=0x{:08X} call={} context=0x{:016X} "
              "symbol=0x{:08X} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
              "ps=0x{:08X}",
              ++logged_exact_alias_snapshot_links,
              g_direct_renderer.current_frame_index,
              stale_reason ? stale_reason : "stale", slot, guest,
              alias_source->guest_address, PlumeGuestTextureWidth(*alias_source),
              PlumeGuestTextureHeight(*alias_source), alias.destination_surface,
              static_cast<unsigned long long>(alias.copy_call_serial),
              static_cast<unsigned long long>(alias.copy_context_key),
              alias.copy_symbol_address,
              draw_command.resources.render_targets[0],
              draw_command.resources.depth_stencil,
              draw_command.resources.vertex_shader,
              draw_command.resources.pixel_shader);
        }
        return &exact_surface_copy_alias_link_snapshot;
      };
      auto exact_alias_copy_identity_matches_link =
          [&](const FableSurfaceTextureLink* link) {
        if (!link) {
          return false;
        }
        const bool call_matches =
            alias.copy_call_serial != 0 &&
            link->copy_call_serial == alias.copy_call_serial;
        const bool context_matches =
            alias.copy_context_key != 0 &&
            link->copy_context_key == alias.copy_context_key;
        if (!call_matches && !context_matches) {
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
        if (alias.copy_symbol_address != 0 && link->copy_symbol_address != 0 &&
            link->copy_symbol_address != alias.copy_symbol_address) {
          return false;
        }
        return true;
      };
      auto find_visible_exact_alias_link =
          [&]() -> const FableSurfaceTextureLink* {
        const FableSurfaceTextureLink* best_link = nullptr;
        const FableSurfaceTextureLink* best_rejected_link = nullptr;
        const char* best_reject_reason = nullptr;
        auto link_rank = [&](const FableSurfaceTextureLink* link) {
          if (!link) {
            return 3u;
          }
          if (link->source_slot == slot) {
            return 0u;
          }
          if (link->source_slot == kUnknownSlot) {
            return 1u;
          }
          return 2u;
        };
	        auto remember_rejected_link =
	            [&](const FableSurfaceTextureLink* candidate,
	                const char* reason) {
	          if (!candidate || !reason) {
	            return;
	          }
	          if (!best_rejected_link ||
	              link_rank(candidate) < link_rank(best_rejected_link)) {
	            best_rejected_link = candidate;
	            best_reject_reason = reason;
	          }
	        };
	        auto sub_82242108_chain_rank =
	            [&](const FableSurfaceTextureLink* link) {
	          if (!link) {
	            return 12u;
	          }
	          return link_rank(link);
	        };
	        auto sub_82242108_chain_link_matches =
	            [&](const FableSurfaceTextureLink* candidate) {
	          if (!candidate || !candidate->trusted_destination_edge ||
	              draw_command.draw.source != RendererDrawEventSource::kSceneObject ||
	              !FablePrivateRectWrapperSymbolsCompatible(
	                  alias.copy_symbol_address,
	                  kFablePrivateRectSiblingWrapperSymbol) ||
		              !FablePrivateRectWrapperSymbolsCompatible(
		                  candidate->copy_symbol_address,
		                  kFablePrivateRectSiblingWrapperSymbol) ||
		              !exact_alias_copy_identity_matches_link(candidate) ||
		              !DirectSurfaceTextureLinkTouchesFablePrivateChain(*candidate,
		                                                                nullptr) ||
		              !DirectResourceAddressMatchesLocked(
	                  candidate->texture, alias.destination_surface) ||
	              candidate->texture == candidate->source_surface ||
	              !link_visible(candidate)) {
	            return false;
	          }
	          return exact_alias_source_matches_link(candidate);
	        };
	        auto find_sub_82242108_chain_link =
	            [&]() -> const FableSurfaceTextureLink* {
	          const FableSurfaceTextureLink* best_chain_link = nullptr;
	          auto consider_chain_link =
	              [&](const FableSurfaceTextureLink* candidate) {
	            if (!sub_82242108_chain_link_matches(candidate)) {
	              return;
	            }
	            if (!best_chain_link ||
	                sub_82242108_chain_rank(candidate) <
	                    sub_82242108_chain_rank(best_chain_link)) {
	              best_chain_link = candidate;
	            }
	          };

	          if (const std::vector<FableSurfaceTextureLink>* destination_links =
	                  FableDirectFindSurfaceTextureLinksForDestinationTexture(
	                      alias.destination_surface)) {
	            for (auto it = destination_links->rbegin();
	                 it != destination_links->rend(); ++it) {
	              consider_chain_link(&*it);
	              if (best_chain_link &&
	                  sub_82242108_chain_rank(best_chain_link) == 0u) {
	                return best_chain_link;
	              }
	            }
	          }
	          if (const std::vector<FableSurfaceTextureLink>* texture_links =
	                  FableDirectFindSurfaceTextureLinksByTexture(
	                      alias.destination_surface)) {
	            for (auto it = texture_links->rbegin();
	                 it != texture_links->rend(); ++it) {
	              consider_chain_link(&*it);
	              if (best_chain_link &&
	                  sub_82242108_chain_rank(best_chain_link) == 0u) {
	                return best_chain_link;
	              }
	            }
	          }
	          return best_chain_link;
	        };
        auto command_visible_private_rect_source_surface_alias =
            [&](const FableSurfaceTextureLink* candidate) {
          if (!candidate || !candidate->trusted_destination_edge ||
              !private_or_title_surface_copy_command ||
              !FablePrivateRectWrapperSymbolsCompatible(
                  candidate->copy_symbol_address,
                  kFablePrivateRectSiblingWrapperSymbol) ||
              !DirectSurfaceTextureLinkTouchesFablePrivateChain(*candidate,
                                                                nullptr) ||
              !DirectResourceAddressMatchesLocked(candidate->texture,
                                                  alias.destination_surface) ||
              !DirectResourceAddressMatchesLocked(candidate->texture, guest) ||
              candidate->texture == candidate->source_surface ||
              !DirectSurfaceTextureLinkHasExactCopyPayloadIdentity(
                  *candidate) ||
              !link_visible(candidate)) {
            return false;
          }
          if (candidate->copy_symbol_address != alias.copy_symbol_address &&
              alias.copy_symbol_address != 0 &&
              !FablePrivateRectWrapperSymbolsCompatible(
                  candidate->copy_symbol_address, alias.copy_symbol_address)) {
            return false;
          }
          PlumeTrackedResource* source =
              FindDirectResourceLocked(candidate->source_surface);
          if (!source || !IsImageResource(source->kind) ||
              !PlumeGuestImageReady(*source)) {
            return false;
          }
          const uint64_t visible_content =
              FableTrackedResourceVisibleContentSerial(*source);
          if (visible_content == 0 ||
              (candidate->sampled_source.content_serial != 0 &&
               candidate->sampled_source.content_serial > visible_content)) {
            return false;
          }
          return draw_command.draw.source ==
                     RendererDrawEventSource::kSceneObject &&
                 (IsFableTitleSceneTargetLocked(
                      draw_command.resources.render_targets[0]) ||
                  IsFableTitleSceneVertexShaderCommandLocked(draw_command) ||
                  IsFableTitleSceneTextureSyncCommandLocked(draw_command));
        };
	        auto consider_link = [&](const FableSurfaceTextureLink* candidate) {
	          if (const char* reason = exact_link_alias_reject_reason(candidate)) {
            if (std::strcmp(reason, "exact_alias_source_mismatch") == 0 &&
                command_visible_private_rect_source_surface_alias(candidate)) {
              static uint32_t
                  logged_exact_private_rect_source_surface_alias_selects = 0;
              if (logged_exact_private_rect_source_surface_alias_selects <
                  32u) {
                REXLOG_INFO(
                    "Refii plume exact private-rect source-surface alias "
                    "selected #{} frame={} reason={} slot={} guest=0x{:08X} "
                    "texture=0x{:08X} surface=0x{:08X} call={} "
                    "context=0x{:016X} symbol=0x{:08X} alias_source=0x{:08X} "
                    "alias_destination=0x{:08X} rt=0x{:08X} ds=0x{:08X} "
                    "vs=0x{:08X} ps=0x{:08X}",
                    ++logged_exact_private_rect_source_surface_alias_selects,
                    g_direct_renderer.current_frame_index, reason, slot, guest,
                    candidate->texture, candidate->source_surface,
                    static_cast<unsigned long long>(
                        candidate->copy_call_serial),
                    static_cast<unsigned long long>(
                        candidate->copy_context_key),
                    candidate->copy_symbol_address, alias.source_surface,
                    alias.destination_surface,
                    draw_command.resources.render_targets[0],
                    draw_command.resources.depth_stencil,
                    draw_command.resources.vertex_shader,
                    draw_command.resources.pixel_shader);
              }
              if (!best_link || link_rank(candidate) < link_rank(best_link)) {
                best_link = candidate;
              }
              return;
            }
	            remember_rejected_link(candidate, reason);
	            return;
	          }
          if (const char* reason =
                  exact_alias_link_visibility_reject_reason(candidate,
                                                            alias)) {
            remember_rejected_link(candidate, reason);
            return;
          }
          if (!best_link || link_rank(candidate) < link_rank(best_link)) {
            best_link = candidate;
          }
        };

        consider_link(FindDirectSurfaceTextureLinkByAliasLocked(alias));
        if (const std::vector<FableSurfaceTextureLink>* destination_links =
                FableDirectFindSurfaceTextureLinksForDestinationTexture(
                    alias.destination_surface)) {
          for (auto it = destination_links->rbegin();
               it != destination_links->rend(); ++it) {
            consider_link(&*it);
            if (best_link && link_rank(best_link) == 0u) {
              break;
            }
          }
        }
        if (!best_link &&
            FablePrivateTextureProxyRoleForAddress(
                alias.destination_surface) ==
                FablePrivateTextureProxyRole::kDepth) {
          if (const std::vector<FableSurfaceTextureLink>* destination_links =
                  FableDirectFindSurfaceTextureLinksForDestinationTexture(
                      alias.destination_surface)) {
            for (auto it = destination_links->rbegin();
                 it != destination_links->rend(); ++it) {
              if (!it->trusted_destination_edge ||
                  it->kind != FablePrivateTextureCopyKind::kDepth ||
                  FablePrivateTextureProxyRoleForAddress(it->texture) !=
                      FablePrivateTextureProxyRole::kDepth ||
                  !DirectResourceAddressMatchesLocked(
                      it->texture, alias.destination_surface) ||
                  !link_visible(&*it)) {
                continue;
              }
              const bool same_depth_family =
                  DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
                      *it, it->source_surface,
                      draw_command.resources.depth_stencil) ||
                  DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
                      *it, it->source_surface, alias.source_surface) ||
                  DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
                      *it, it->depth_stencil,
                      draw_command.resources.depth_stencil);
              if (!same_depth_family) {
                continue;
              }
              best_link = &*it;
              if (link_rank(best_link) == 0u) {
                break;
              }
	            }
	          }
	        }
	        if (!best_link) {
	          if (const FableSurfaceTextureLink* sibling_chain_link =
	                  find_sub_82242108_chain_link()) {
	            static uint32_t logged_sub_82242108_chain_selects = 0;
	            if (logged_sub_82242108_chain_selects < 32u) {
	              REXLOG_INFO(
	                  "Refii plume exact sub_82242108 alias select #{} "
	                  "frame={} slot={} guest=0x{:08X} texture=0x{:08X} "
	                  "surface=0x{:08X} call={} context=0x{:016X} "
	                  "alias_source=0x{:08X} alias_call={} "
	                  "alias_context=0x{:016X} rt=0x{:08X} ds=0x{:08X} "
	                  "vs=0x{:08X} ps=0x{:08X}",
	                  ++logged_sub_82242108_chain_selects,
	                  g_direct_renderer.current_frame_index, slot,
	                  alias.destination_surface, sibling_chain_link->texture,
	                  sibling_chain_link->source_surface,
	                  static_cast<unsigned long long>(
	                      sibling_chain_link->copy_call_serial),
	                  static_cast<unsigned long long>(
	                      sibling_chain_link->copy_context_key),
	                  alias.source_surface,
	                  static_cast<unsigned long long>(alias.copy_call_serial),
	                  static_cast<unsigned long long>(alias.copy_context_key),
	                  draw_command.resources.render_targets[0],
	                  draw_command.resources.depth_stencil,
	                  draw_command.resources.vertex_shader,
	                  draw_command.resources.pixel_shader);
	            }
	            best_link = sibling_chain_link;
	          }
	        }
        if (!best_link && best_rejected_link) {
          if (const FableSurfaceTextureLink* alias_snapshot_link =
                  build_visible_exact_alias_snapshot_link(
                      best_reject_reason)) {
            return alias_snapshot_link;
          }
          auto build_private_rect_action_link =
              [&]() -> const FableSurfaceTextureLink* {
            if (!private_or_title_surface_copy_command ||
                !exact_alias_reject_is_stale_identity(best_reject_reason) ||
                !best_rejected_link->trusted_destination_edge ||
                !DirectSurfaceTextureLinkHasExactCopyIdentity(
                    *best_rejected_link) ||
                !DirectSyntheticPrivateRectCopyCanExecuteFromConcreteSourceLocked(
                    draw_command) ||
                !DirectResourceAddressMatchesLocked(
                    best_rejected_link->texture, alias.destination_surface) ||
                best_rejected_link->source_surface == 0 ||
                DirectResourceAddressMatchesLocked(
                    best_rejected_link->source_surface,
                    best_rejected_link->texture)) {
              return nullptr;
            }
            const bool source_is_current_target =
                DirectResourceAddressMatchesLocked(
                    best_rejected_link->source_surface,
                    draw_command.resources.render_targets[0]) ||
                DirectResourceAddressMatchesLocked(
                    best_rejected_link->source_surface,
                    draw_command.resources.depth_stencil);
            if (!source_is_current_target) {
              return nullptr;
            }
            PlumeTrackedResource* source =
                FindDirectResourceLocked(best_rejected_link->source_surface);
            PlumeTrackedResource* destination =
                FindDirectResourceLocked(alias.destination_surface);
            if (!source || !IsImageResource(source->kind) ||
                !PlumeGuestImageReady(*source) ||
                !DirectCommandBindsResourceAsTargetLocked(draw_command,
                                                          *source) ||
                FableTrackedResourceVisibleContentSerial(*source) == 0) {
              return nullptr;
            }

            FableSurfaceTextureLink action_link = *best_rejected_link;
            action_link.source_slot = slot;
            action_link.trusted_destination_edge = true;
            action_link.texture = alias.destination_surface;
            action_link.texture_base_address =
                destination ? PlumeGuestTextureBaseAddress(*destination)
                            : (command_fetch_info.valid
                                   ? command_fetch_info.base_address
                                   : best_rejected_link->texture_base_address);
            action_link.texture_width =
                destination ? PlumeGuestTextureWidth(*destination)
                            : (command_fetch_info.valid
                                   ? command_fetch_info.width
                                   : best_rejected_link->texture_width);
            action_link.texture_height =
                destination ? PlumeGuestTextureHeight(*destination)
                            : (command_fetch_info.valid
                                   ? command_fetch_info.height
                                   : best_rejected_link->texture_height);
            action_link.texture_format =
                destination ? PlumeGuestTextureGuestFormat(*destination)
                            : (command_fetch_info.valid
                                   ? command_fetch_info.format
                                   : best_rejected_link->texture_format);
            action_link.texture_fetch_dwords =
                destination ? PlumeGuestTextureFetchDwords(*destination)
                            : (HasTrackedFetchDwords(command_fetch)
                                   ? command_fetch
                                   : best_rejected_link->texture_fetch_dwords);
            action_link.source_surface = source->guest_address;
            action_link.backing_texture = 0;
            action_link.backing_base_address = 0;
            action_link.backing_fetch_dwords = {};
            action_link.backing_content_serial = 0;
            action_link.render_target = draw_command.resources.render_targets[0];
            action_link.depth_stencil = draw_command.resources.depth_stencil;
            action_link.vertex_shader = draw_command.resources.vertex_shader;
            action_link.pixel_shader = draw_command.resources.pixel_shader;
            PopulateDirectSurfaceTextureLinkShaderRoots(action_link,
                                                        draw_command);
            action_link.frame_index = g_direct_renderer.current_frame_index;
            action_link.draw_serial = draw_serial;
            action_link.wrapper_source_texture =
                best_rejected_link->wrapper_source_texture != 0
                    ? best_rejected_link->wrapper_source_texture
                    : (draw_command.draw.private_texture_wrapper_source_texture !=
                               0
                           ? draw_command.draw
                                 .private_texture_wrapper_source_texture
                           : guest);
            action_link.wrapper_source_exact =
                action_link.wrapper_source_texture != 0;
            action_link.kind = source->texture_depth_target
                                   ? FablePrivateTextureCopyKind::kDepth
                                   : FablePrivateTextureCopyKind::kColor;
            PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(action_link,
                                                                   *source);
            if (!DirectSurfaceTextureLinkIsCurrentTargetExactEdgeLocked(
                    draw_command, action_link)) {
              return nullptr;
            }

            exact_surface_copy_alias_link_snapshot = action_link;
            static uint32_t logged_exact_private_rect_action_links = 0;
            if (logged_exact_private_rect_action_links < 48u) {
              REXLOG_INFO(
                  "Refii plume exact IDA-backed private-rect action link #{} "
                  "frame={} reason={} slot={} guest=0x{:08X} "
                  "source=0x{:08X}/{}x{} destination=0x{:08X}/{}x{} "
                  "call={} context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} "
                  "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
                  ++logged_exact_private_rect_action_links,
                  g_direct_renderer.current_frame_index,
                  best_reject_reason ? best_reject_reason : "stale", slot,
                  guest, source->guest_address,
                  PlumeGuestTextureWidth(*source),
                  PlumeGuestTextureHeight(*source),
                  action_link.texture, action_link.texture_width,
                  action_link.texture_height,
                  static_cast<unsigned long long>(
                      action_link.copy_call_serial),
                  static_cast<unsigned long long>(
                      action_link.copy_context_key),
                  action_link.copy_symbol_address,
                  draw_command.resources.render_targets[0],
                  draw_command.resources.depth_stencil,
                  draw_command.resources.vertex_shader,
                  draw_command.resources.pixel_shader);
            }
            return &exact_surface_copy_alias_link_snapshot;
          };
          if (const FableSurfaceTextureLink* private_rect_action_link =
                  build_private_rect_action_link()) {
            return private_rect_action_link;
          }
          if (private_or_title_surface_copy_command &&
              exact_alias_reject_is_stale_identity(best_reject_reason)) {
            exact_surface_copy_alias_missing_link = true;
            return nullptr;
          }
          exact_surface_copy_alias_link_snapshot = *best_rejected_link;
          log_exact_surface_copy_miss(best_reject_reason,
                                      &exact_surface_copy_alias_link_snapshot,
                                      nullptr, nullptr);
          exact_surface_copy_alias_reject_logged = true;
        }
        return best_link;
      };
      const FableSurfaceTextureLink* exact_link =
          find_visible_exact_alias_link();
      if (!exact_link) {
        if (exact_surface_copy_alias_reject_logged) {
          return nullptr;
        }
        exact_surface_copy_alias_missing_link = true;
        return nullptr;
      }
      exact_surface_copy_alias_link_selected = true;
      exact_surface_copy_alias_link_snapshot = *exact_link;
      return &exact_surface_copy_alias_link_snapshot;
    };
    auto find_link_by_destination =
        [&](uint32_t destination_texture) -> const FableSurfaceTextureLink* {
      if (destination_texture == 0) {
        return nullptr;
      }
      const FableSurfaceTextureLink* first_visible_link = nullptr;
      const FableSurfaceTextureLink* unknown_slot_link = nullptr;
      if (const std::vector<FableSurfaceTextureLink>* destination_links =
              FableDirectFindSurfaceTextureLinksForDestinationTexture(
                  destination_texture)) {
        for (auto it = destination_links->rbegin();
             it != destination_links->rend(); ++it) {
          if (!link_visible(&*it)) {
            continue;
          }
          if (it->source_slot == slot) {
            return &*it;
          }
          if (!unknown_slot_link && it->source_slot == kUnknownSlot) {
            unknown_slot_link = &*it;
          }
          if (!first_visible_link) {
            first_visible_link = &*it;
          }
        }
      }
      if (unknown_slot_link) {
        return unknown_slot_link;
      }
      if (first_visible_link) {
        return first_visible_link;
      }
      if (const FableSurfaceTextureLink* slot_link =
              FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
                  destination_texture, slot);
          link_visible(slot_link)) {
        return slot_link;
      }
      const FableSurfaceTextureLink* link =
          FableDirectFindSurfaceTextureLinkForDestinationTexture(
              destination_texture);
      return link_visible(link) ? link : nullptr;
    };
    auto find_exact_depth_family_link_by_destination =
        [&](uint32_t destination_texture) -> const FableSurfaceTextureLink* {
      if (destination_texture == 0 ||
          FablePrivateTextureProxyRoleForAddress(destination_texture) !=
              FablePrivateTextureProxyRole::kDepth) {
        return nullptr;
      }

      const FableSurfaceTextureLink* best_link = nullptr;
      auto link_rank = [&](const FableSurfaceTextureLink* link) {
        if (!link) {
          return 3u;
        }
        if (link->source_slot == slot) {
          return 0u;
        }
        if (link->source_slot == kUnknownSlot) {
          return 1u;
        }
        return 2u;
      };
      auto consider_link = [&](const FableSurfaceTextureLink* candidate) {
        if (!candidate || !candidate->trusted_destination_edge ||
            candidate->kind != FablePrivateTextureCopyKind::kDepth ||
            FablePrivateTextureProxyRoleForAddress(candidate->texture) !=
                FablePrivateTextureProxyRole::kDepth ||
            !DirectResourceAddressMatchesLocked(candidate->texture,
                                                destination_texture) ||
            !link_visible(candidate)) {
          return;
        }
        const bool same_depth_family =
            DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
                *candidate, candidate->source_surface,
                draw_command.resources.depth_stencil) ||
            DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
                *candidate, candidate->depth_stencil,
                draw_command.resources.depth_stencil) ||
            (exact_surface_copy_alias_snapshot_valid &&
             (DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
                  *candidate, candidate->source_surface,
                  exact_surface_copy_alias_snapshot.source_surface) ||
              DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
                  *candidate, candidate->depth_stencil,
                  exact_surface_copy_alias_snapshot.source_surface)));
        if (!same_depth_family) {
          return;
        }
        if (!best_link || link_rank(candidate) < link_rank(best_link)) {
          best_link = candidate;
        }
      };

      if (const std::vector<FableSurfaceTextureLink>* destination_links =
              FableDirectFindSurfaceTextureLinksForDestinationTexture(
                  destination_texture)) {
        for (auto it = destination_links->rbegin();
             it != destination_links->rend(); ++it) {
          consider_link(&*it);
          if (best_link && link_rank(best_link) == 0u) {
            return best_link;
          }
        }
      }
      if (const std::vector<FableSurfaceTextureLink>* texture_links =
              FableDirectFindSurfaceTextureLinksByTexture(destination_texture)) {
        for (auto it = texture_links->rbegin(); it != texture_links->rend();
             ++it) {
          consider_link(&*it);
          if (best_link && link_rank(best_link) == 0u) {
            return best_link;
          }
        }
      }
      return best_link;
    };
    auto exact_destination_link_matches_alias =
        [&](const FableSurfaceTextureLink* candidate) {
          if (!candidate || !exact_surface_copy_alias_snapshot_valid ||
              !candidate->trusted_destination_edge) {
            return false;
          }
          if (!DirectResourceAddressMatchesLocked(
                  candidate->texture,
                  exact_surface_copy_alias_snapshot.destination_surface)) {
            return false;
          }
          const bool source_matches_alias =
              DirectResourceAddressMatchesLocked(
                  candidate->source_surface,
                  exact_surface_copy_alias_snapshot.source_surface) ||
              DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
                  *candidate, candidate->source_surface,
                  exact_surface_copy_alias_snapshot.source_surface);
          if (!source_matches_alias) {
            return false;
          }
          if (candidate->copy_symbol_address != 0 &&
              exact_surface_copy_alias_snapshot.copy_symbol_address != 0 &&
              candidate->copy_symbol_address !=
                  exact_surface_copy_alias_snapshot.copy_symbol_address) {
            return false;
          }
          if (candidate->copy_call_serial != 0 &&
              exact_surface_copy_alias_snapshot.copy_call_serial != 0 &&
              candidate->copy_call_serial !=
                  exact_surface_copy_alias_snapshot.copy_call_serial) {
            return false;
          }
          if (candidate->copy_context_key != 0 &&
              exact_surface_copy_alias_snapshot.copy_context_key != 0 &&
              candidate->copy_context_key !=
                  exact_surface_copy_alias_snapshot.copy_context_key) {
            return false;
          }
          return true;
        };
    auto find_visible_link_by_base =
        [&](uint32_t base_address) -> const FableSurfaceTextureLink* {
      if (base_address == 0) {
        return nullptr;
      }
      const FableSurfaceTextureLink* link =
          FableDirectFindVisibleSurfaceTextureLinkForBase(
              base_address, g_direct_renderer.current_frame_index,
              draw_serial, slot);
      return link_visible(link) ? link : nullptr;
    };
	    auto find_visible_link_by_fetch =
	        [&](const std::array<uint32_t, 6>& fetch_dwords)
	            -> const FableSurfaceTextureLink* {
	      if (!HasTrackedFetchDwords(fetch_dwords)) {
	        return nullptr;
      }
      const FableSurfaceTextureLink* link =
          FableDirectFindVisibleSurfaceTextureLinkForFetch(
	              fetch_dwords, g_direct_renderer.current_frame_index,
	              draw_serial, slot);
	      return link_visible(link) ? link : nullptr;
	    };
	    auto find_exact_private_rect_source_surface_link_by_texture =
	        [&](uint32_t texture_address) -> const FableSurfaceTextureLink* {
	      if (!private_or_title_surface_copy_command || texture_address == 0) {
	        return nullptr;
	      }
	      const FableSurfaceTextureLink* best_link = nullptr;
	      auto link_rank = [&](const FableSurfaceTextureLink* candidate) {
	        if (!candidate) {
	          return 3u;
	        }
	        if (candidate->source_slot == slot) {
	          return 0u;
	        }
	        if (candidate->source_slot == kUnknownSlot) {
	          return 1u;
	        }
	        return 2u;
	      };
	      const uint64_t command_fetch_key =
	          FableDirectTextureFetchKey(command_fetch);
	      auto candidate_matches =
	          [&](const FableSurfaceTextureLink* candidate) {
	        if (!candidate || !candidate->trusted_destination_edge ||
	            !DirectResourceAddressMatchesLocked(candidate->texture,
	                                                texture_address) ||
	            !DirectSurfaceTextureLinkHasExactCopyIdentity(*candidate) ||
	            !DirectIdaPrivateRectWrapperProofSymbol(
	                candidate->copy_symbol_address) ||
	            candidate->copy_symbol_address ==
	                kFablePrivateRectNativeDepthWrapperSymbol ||
	            !DirectPrivateTextureCopyKindUsesColorBacking(candidate->kind) ||
	            candidate->source_surface == 0 ||
	            DirectResourceAddressMatchesLocked(candidate->source_surface,
	                                               candidate->texture)) {
	          return false;
	        }
	        if (candidate->source_slot != kUnknownSlot &&
	            candidate->source_slot != slot) {
	          return false;
	        }
	        if (candidate->frame_index != 0 &&
	            candidate->frame_index > g_direct_renderer.current_frame_index) {
	          return false;
	        }
	        if (candidate->frame_index != 0 &&
	            candidate->frame_index == g_direct_renderer.current_frame_index &&
	            candidate->draw_serial != 0 && draw_serial != 0 &&
	            candidate->draw_serial > draw_serial) {
	          return false;
	        }
	        PlumeTrackedResource* source =
	            FindDirectResourceLocked(candidate->source_surface);
	        if (!source || !IsImageResource(source->kind) ||
	            !PlumeGuestImageReady(*source) ||
	            FableTrackedResourceIsBlankUploadedSampleProxy(*source) ||
	            DirectCommandBindsResourceAsTargetLocked(draw_command, *source)) {
	          return false;
	        }
	        const uint64_t visible_content =
	            FableTrackedResourceVisibleContentSerial(*source);
	        if (visible_content == 0 ||
	            (candidate->target_content_serial != 0 &&
	             candidate->target_content_serial > visible_content) ||
	            (candidate->sampled_source.content_serial != 0 &&
	             candidate->sampled_source.content_serial > visible_content)) {
	          return false;
	        }
	        if (candidate->sampled_source.resource != 0 &&
	            !DirectResourceAddressMatchesLocked(
	                candidate->sampled_source.resource, source->guest_address)) {
	          return false;
	        }
	        if (command_fetch_info.valid) {
	          const uint64_t link_fetch_key =
	              FableDirectTextureFetchKey(candidate->texture_fetch_dwords);
	          const bool fetch_matches =
	              link_fetch_key != 0 && command_fetch_key != 0 &&
	              link_fetch_key == command_fetch_key;
	          const bool base_matches =
	              candidate->texture_base_address != 0 &&
	              candidate->texture_base_address ==
	                  command_fetch_info.base_address;
	          if (candidate->texture_width != 0 &&
	              candidate->texture_width != command_fetch_info.width) {
	            return false;
	          }
	          if (candidate->texture_height != 0 &&
	              candidate->texture_height != command_fetch_info.height) {
	            return false;
	          }
	          if (candidate->texture_format != 0 &&
	              candidate->texture_format != command_fetch_info.format &&
	              !fetch_matches) {
	            return false;
	          }
	          if (candidate->texture_base_address != 0 && !base_matches &&
	              !fetch_matches) {
	            return false;
	          }
	          if (link_fetch_key != 0 && !fetch_matches && !base_matches) {
	            return false;
	          }
	        }
	        return true;
	      };
	      auto consider_link = [&](const FableSurfaceTextureLink* candidate) {
	        if (!candidate_matches(candidate)) {
	          return;
	        }
	        if (!best_link || link_rank(candidate) < link_rank(best_link)) {
	          best_link = candidate;
	        }
	      };
	      if (const std::vector<FableSurfaceTextureLink>* texture_links =
	              FableDirectFindSurfaceTextureLinksByTexture(texture_address)) {
	        for (auto it = texture_links->rbegin(); it != texture_links->rend();
	             ++it) {
	          consider_link(&*it);
	          if (best_link && link_rank(best_link) == 0u) {
	            return best_link;
	          }
	        }
	      }
	      if (const std::vector<FableSurfaceTextureLink>* destination_links =
	              FableDirectFindSurfaceTextureLinksForDestinationTexture(
	                  texture_address)) {
	        for (auto it = destination_links->rbegin();
	             it != destination_links->rend(); ++it) {
	          consider_link(&*it);
	          if (best_link && link_rank(best_link) == 0u) {
	            return best_link;
	          }
	        }
	      }
	      return best_link;
	    };

	    const char* selected_copy_edge_bucket = nullptr;
	    bool selected_copy_edge_pix_video_broad_match = false;
	    const auto select_copy_edge_link =
	        [&](const FableSurfaceTextureLink* candidate, const char* bucket,
            bool pix_video_broad_match) -> const FableSurfaceTextureLink* {
      if (candidate) {
        selected_copy_edge_bucket = bucket;
        selected_copy_edge_pix_video_broad_match = pix_video_broad_match;
      }
      return candidate;
    };
    auto exact_alias_copy_covered_by_title_upload =
        [&](const char* reason) -> bool {
      if (!exact_title_upload_source) {
        return false;
      }
      PlumeTrackedResource* direct_texture = FindDirectResourceLocked(guest);
      PlumeTrackedResource* title_sample =
          ResolveDirectExactTitleUploadSourceForCommandSlotLocked(
              draw_command, slot, direct_texture);
      if (!title_sample) {
        title_sample =
            ResolveDirectTinyTitleUploadedTextureForCommandSlotLocked(
                draw_command, slot, direct_texture);
      }
      if (!title_sample) {
        return false;
      }

      static uint32_t logged_exact_alias_copy_title_upload_owners = 0;
      if (logged_exact_alias_copy_title_upload_owners < 48u) {
        REXLOG_INFO(
            "Refii plume exact alias copy covered by title upload #{} "
            "frame={} reason={} serial={} draw={} source={} slot={} "
            "guest=0x{:08X} sample=0x{:08X} rt=0x{:08X} ds=0x{:08X} "
            "vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_alias_copy_title_upload_owners,
            g_direct_renderer.current_frame_index, reason ? reason : "unknown",
            static_cast<unsigned long long>(draw_command.serial),
            static_cast<unsigned long long>(draw_serial),
            static_cast<uint32_t>(draw_command.draw.source), slot, guest,
            title_sample ? title_sample->guest_address : 0u,
            draw_command.resources.render_targets[0],
            draw_command.resources.depth_stencil,
            draw_command.resources.vertex_shader,
            draw_command.resources.pixel_shader);
      }
      return true;
    };
    auto exact_alias_copy_covered_by_private_rect_action =
        [&](const char* reason) -> bool {
      if (!private_or_title_surface_copy_command) {
        return false;
      }
      const uint32_t wrapper_symbol =
          draw_command.draw.private_texture_wrapper_symbol_address;
      if (!DirectIdaPrivateRectWrapperProofSymbol(wrapper_symbol) &&
          wrapper_symbol != kFableBeginVerticesTitleWrapperSymbol) {
        return false;
      }
      if (wrapper_symbol == kFablePrivateRectNativeDepthWrapperSymbol) {
        return false;
      }

      PlumeTrackedResource* direct_texture = FindDirectResourceLocked(guest);
      PlumeTrackedResource* exact_sample =
          ResolveDirectExactWrapperSourceSampleForSlotLocked(
              draw_command, slot, direct_texture);
      if (!exact_sample &&
          DirectCommandTextureSlotDirectResourceProvidesSampleLocked(
              draw_command, slot, direct_texture, true)) {
        exact_sample = direct_texture;
      }
      bool covered_by_concrete_sample =
          exact_sample &&
          DirectSyntheticPrivateRectCopyCanExecuteFromConcreteSourceLocked(
              draw_command);
      bool covered_by_current_target_alias = false;
      uint32_t alias_source_address = 0;
      uint32_t alias_destination_address = 0;
      uint64_t alias_call_serial = 0;
      uint64_t alias_context_key = 0;
      uint32_t alias_symbol = 0;
      FableSurfaceTextureLink published_current_target_alias_link = {};
      if (!covered_by_concrete_sample &&
          exact_surface_copy_alias_snapshot_valid &&
          DirectSurfaceCopyAliasHasExactIdentity(
              exact_surface_copy_alias_snapshot) &&
          DirectIdaPrivateRectWrapperProofSymbol(
              exact_surface_copy_alias_snapshot.copy_symbol_address) &&
          exact_surface_copy_alias_snapshot.copy_symbol_address !=
              kFablePrivateRectNativeDepthWrapperSymbol &&
          (exact_surface_copy_alias_snapshot.copy_symbol_address ==
               wrapper_symbol ||
           FablePrivateRectWrapperSymbolsCompatible(
               exact_surface_copy_alias_snapshot.copy_symbol_address,
               wrapper_symbol)) &&
          DirectResourceAddressMatchesLocked(
              exact_surface_copy_alias_snapshot.destination_surface, guest)) {
        const bool call_matches =
            exact_surface_copy_alias_snapshot.copy_call_serial != 0 &&
            draw_command.draw.private_texture_wrapper_call_serial != 0 &&
            exact_surface_copy_alias_snapshot.copy_call_serial ==
                draw_command.draw.private_texture_wrapper_call_serial;
        const bool context_matches =
            exact_surface_copy_alias_snapshot.copy_context_key != 0 &&
            draw_command.draw.private_texture_wrapper_context_key != 0 &&
            exact_surface_copy_alias_snapshot.copy_context_key ==
                draw_command.draw.private_texture_wrapper_context_key;
        const bool source_is_current_target =
            DirectResourceAddressMatchesLocked(
                exact_surface_copy_alias_snapshot.source_surface,
                draw_command.resources.render_targets[0]) ||
            DirectResourceAddressMatchesLocked(
                exact_surface_copy_alias_snapshot.source_surface,
                draw_command.resources.depth_stencil);
        PlumeTrackedResource* alias_source =
            FindDirectResourceLocked(
                exact_surface_copy_alias_snapshot.source_surface);
        if ((call_matches || context_matches) && source_is_current_target &&
            alias_source && IsImageResource(alias_source->kind) &&
            PlumeGuestImageReady(*alias_source) &&
            DirectCommandBindsResourceAsTargetLocked(draw_command,
                                                     *alias_source) &&
            FableTrackedResourceVisibleContentSerial(*alias_source) != 0) {
          covered_by_current_target_alias = true;
          alias_source_address =
              exact_surface_copy_alias_snapshot.source_surface;
          alias_destination_address =
              exact_surface_copy_alias_snapshot.destination_surface;
          alias_call_serial = exact_surface_copy_alias_snapshot.copy_call_serial;
          alias_context_key = exact_surface_copy_alias_snapshot.copy_context_key;
          alias_symbol = exact_surface_copy_alias_snapshot.copy_symbol_address;
          published_current_target_alias_link.copy_call_serial =
              alias_call_serial;
          published_current_target_alias_link.copy_context_key =
              alias_context_key;
          published_current_target_alias_link.copy_symbol_address =
              alias_symbol;
          published_current_target_alias_link.source_slot = slot;
          published_current_target_alias_link.trusted_destination_edge = true;
          published_current_target_alias_link.texture =
              alias_destination_address;
          published_current_target_alias_link.texture_base_address =
              direct_texture ? PlumeGuestTextureBaseAddress(*direct_texture)
                             : (command_fetch_info.valid
                                    ? command_fetch_info.base_address
                                    : 0u);
          published_current_target_alias_link.texture_width =
              direct_texture ? PlumeGuestTextureWidth(*direct_texture)
                             : (command_fetch_info.valid
                                    ? command_fetch_info.width
                                    : 0u);
          published_current_target_alias_link.texture_height =
              direct_texture ? PlumeGuestTextureHeight(*direct_texture)
                             : (command_fetch_info.valid
                                    ? command_fetch_info.height
                                    : 0u);
          published_current_target_alias_link.texture_format =
              direct_texture ? PlumeGuestTextureGuestFormat(*direct_texture)
                             : (command_fetch_info.valid
                                    ? command_fetch_info.format
                                    : 0u);
          published_current_target_alias_link.texture_fetch_dwords =
              direct_texture ? PlumeGuestTextureFetchDwords(*direct_texture)
                             : command_fetch;
          published_current_target_alias_link.source_surface =
              alias_source_address;
          published_current_target_alias_link.render_target =
              draw_command.resources.render_targets[0];
          published_current_target_alias_link.depth_stencil =
              draw_command.resources.depth_stencil;
          published_current_target_alias_link.vertex_shader =
              draw_command.resources.vertex_shader;
          published_current_target_alias_link.pixel_shader =
              draw_command.resources.pixel_shader;
          PopulateDirectSurfaceTextureLinkShaderRoots(
              published_current_target_alias_link, draw_command);
          published_current_target_alias_link.frame_index =
              g_direct_renderer.current_frame_index;
          published_current_target_alias_link.draw_serial = draw_serial;
          published_current_target_alias_link.wrapper_source_texture =
              draw_command.draw.private_texture_wrapper_source_texture != 0
                  ? draw_command.draw.private_texture_wrapper_source_texture
                  : guest;
          published_current_target_alias_link.wrapper_source_exact =
              published_current_target_alias_link.wrapper_source_texture != 0;
          published_current_target_alias_link.kind =
              alias_source->texture_depth_target
                  ? FablePrivateTextureCopyKind::kDepth
                  : FablePrivateTextureCopyKind::kColor;
          PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(
              published_current_target_alias_link, *alias_source);
          RegisterDirectSurfaceTextureLinkPreparedLocked(
              published_current_target_alias_link,
              "exact-current-target-surface-texture-identity");
        }
      }
      if (!covered_by_concrete_sample && !covered_by_current_target_alias) {
        return false;
      }

      const bool alias_snapshot_exact =
          exact_surface_copy_alias_snapshot_valid &&
          DirectSurfaceCopyAliasHasExactIdentity(
              exact_surface_copy_alias_snapshot);
      const bool alias_source_current_target =
          exact_surface_copy_alias_snapshot_valid &&
          (DirectResourceAddressMatchesLocked(
               exact_surface_copy_alias_snapshot.source_surface,
               draw_command.resources.render_targets[0]) ||
           DirectResourceAddressMatchesLocked(
               exact_surface_copy_alias_snapshot.source_surface,
               draw_command.resources.depth_stencil));
      const bool sample_bound_as_target =
          exact_sample &&
          DirectCommandBindsResourceAsTargetLocked(draw_command, *exact_sample);
      const bool sample_direct_texture =
          exact_sample && direct_texture &&
          DirectResourceAddressMatchesLocked(exact_sample->guest_address,
                                             direct_texture->guest_address);
      static uint32_t logged_exact_alias_private_rect_ownership_proofs = 0;
      if (logged_exact_alias_private_rect_ownership_proofs < 48u) {
        REXLOG_INFO(
            "Refii plume exact IDA-backed private-rect alias ownership "
            "proof #{} frame={} reason={} serial={} draw={} source={} "
            "slot={} guest=0x{:08X} sample=0x{:08X} wrapper=0x{:08X} "
            "alias_source=0x{:08X} alias_destination=0x{:08X} "
            "alias_call={} alias_context=0x{:016X} alias_symbol=0x{:08X} "
            "published={} target_serial={} ida_wrapper={} "
            "concrete_sample={} concrete_executable={} current_target_alias={} "
            "alias_exact={} alias_source_current_target={} "
            "sample_direct_texture={} sample_bound_as_target={} "
            "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_alias_private_rect_ownership_proofs,
            g_direct_renderer.current_frame_index, reason ? reason : "unknown",
            static_cast<unsigned long long>(draw_command.serial),
            static_cast<unsigned long long>(draw_serial),
            static_cast<uint32_t>(draw_command.draw.source), slot, guest,
            exact_sample ? exact_sample->guest_address : guest, wrapper_symbol,
            alias_source_address, alias_destination_address,
            static_cast<unsigned long long>(alias_call_serial),
            static_cast<unsigned long long>(alias_context_key), alias_symbol,
            covered_by_current_target_alias ? 1 : 0,
            static_cast<unsigned long long>(
                published_current_target_alias_link.target_content_serial),
            true, exact_sample != nullptr, covered_by_concrete_sample,
            covered_by_current_target_alias, alias_snapshot_exact,
            alias_source_current_target, sample_direct_texture,
            sample_bound_as_target,
            draw_command.resources.render_targets[0],
            draw_command.resources.depth_stencil,
            draw_command.resources.vertex_shader,
            draw_command.resources.pixel_shader);
      }
      return true;
    };

    const FableSurfaceTextureLink* link =
        select_copy_edge_link(find_exact_link_by_surface_alias_destination(guest),
                              "exact_alias_guest", false);
    if (!link && exact_surface_copy_alias_seen) {
      const FableSurfaceTextureLink* destination_link =
          find_link_by_destination(guest);
      if (exact_destination_link_matches_alias(destination_link)) {
        exact_surface_copy_alias_link_selected = true;
        exact_surface_copy_alias_link_snapshot = *destination_link;
        link = select_copy_edge_link(&exact_surface_copy_alias_link_snapshot,
                                     "exact_alias_destination", false);
      } else if (const FableSurfaceTextureLink* exact_depth_family_link =
                     find_exact_depth_family_link_by_destination(guest)) {
        static uint32_t logged_native_depth_alias_family_selects = 0;
        if (logged_native_depth_alias_family_selects < 32u) {
          REXLOG_INFO(
              "Refii plume exact native depth family alias select #{} "
              "frame={} slot={} guest=0x{:08X} texture=0x{:08X} "
              "surface=0x{:08X} call={} context=0x{:016X} symbol=0x{:08X} "
              "alias_source=0x{:08X} alias_call={} "
              "alias_context=0x{:016X} rt=0x{:08X} ds=0x{:08X} "
              "vs=0x{:08X} ps=0x{:08X}",
              ++logged_native_depth_alias_family_selects,
              g_direct_renderer.current_frame_index, slot, guest,
              exact_depth_family_link->texture,
              exact_depth_family_link->source_surface,
              static_cast<unsigned long long>(
                  exact_depth_family_link->copy_call_serial),
              static_cast<unsigned long long>(
                  exact_depth_family_link->copy_context_key),
              exact_depth_family_link->copy_symbol_address,
              exact_surface_copy_alias_snapshot.source_surface,
              static_cast<unsigned long long>(
                  exact_surface_copy_alias_snapshot.copy_call_serial),
              static_cast<unsigned long long>(
                  exact_surface_copy_alias_snapshot.copy_context_key),
              draw_command.resources.render_targets[0],
              draw_command.resources.depth_stencil,
              draw_command.resources.vertex_shader,
              draw_command.resources.pixel_shader);
        }
        exact_surface_copy_alias_link_selected = true;
        exact_surface_copy_alias_link_snapshot = *exact_depth_family_link;
        link = select_copy_edge_link(&exact_surface_copy_alias_link_snapshot,
                                     "native_depth_family", false);
	      } else {
	        if (exact_surface_copy_alias_missing_link &&
	            !exact_surface_copy_alias_reject_logged) {
	          if (const FableSurfaceTextureLink* source_surface_link =
	                  find_exact_private_rect_source_surface_link_by_texture(
	                      guest)) {
	            exact_surface_copy_alias_link_selected = true;
	            exact_surface_copy_alias_link_snapshot = *source_surface_link;
	            link = select_copy_edge_link(
	                &exact_surface_copy_alias_link_snapshot,
	                "exact_private_rect_source_surface", false);
	            static uint32_t logged_exact_source_surface_copy_link_selects =
	                0;
	            if (logged_exact_source_surface_copy_link_selects < 48u) {
	              const PlumeTrackedResource* selected_source =
	                  FindDirectResourceLocked(
	                      source_surface_link->source_surface);
	              REXLOG_INFO(
	                  "Refii plume exact private-rect source-surface copy link "
	                  "selected #{} frame={} reason=exact_alias_missing_link "
	                  "serial={} draw={} slot={} guest=0x{:08X} "
	                  "texture=0x{:08X} surface=0x{:08X}/{}x{} content={} "
	                  "call={} context=0x{:016X} symbol=0x{:08X} "
	                  "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
	                  ++logged_exact_source_surface_copy_link_selects,
	                  g_direct_renderer.current_frame_index,
	                  static_cast<unsigned long long>(draw_command.serial),
	                  static_cast<unsigned long long>(draw_serial), slot, guest,
	                  source_surface_link->texture,
	                  source_surface_link->source_surface,
	                  selected_source ? PlumeGuestTextureWidth(*selected_source)
	                                  : 0u,
	                  selected_source ? PlumeGuestTextureHeight(*selected_source)
	                                  : 0u,
	                  selected_source
	                      ? static_cast<unsigned long long>(
	                            FableTrackedResourceVisibleContentSerial(
	                                *selected_source))
	                      : 0ull,
	                  static_cast<unsigned long long>(
	                      source_surface_link->copy_call_serial),
	                  static_cast<unsigned long long>(
	                      source_surface_link->copy_context_key),
	                  source_surface_link->copy_symbol_address,
	                  draw_command.resources.render_targets[0],
	                  draw_command.resources.depth_stencil,
	                  draw_command.resources.vertex_shader,
	                  draw_command.resources.pixel_shader);
	            }
	          } else {
	          if (exact_alias_copy_covered_by_private_rect_action(
	                  "exact_alias_missing_link")) {
	            return true;
	          }
	          if (exact_alias_copy_covered_by_title_upload(
	                  "exact_alias_missing_link")) {
	            return true;
	          }
	          log_exact_surface_copy_miss("exact_alias_missing_link", nullptr,
	                                      nullptr, nullptr);
	          }
	        }
	        if (!link) {
	          return false;
	        }
	      }
	    }
    if (!link && private_or_title_surface_copy_command &&
        !exact_title_upload_source) {
      return false;
    }
    if (!link && draw_command.draw.source != RendererDrawEventSource::kPixVideo) {
      if (private_or_title_surface_copy_command && !exact_title_upload_source) {
        log_exact_surface_copy_miss("non_video_missing_exact_link", nullptr,
                                    nullptr, nullptr);
      }
      return false;
    }
    if (!link) {
      link = select_copy_edge_link(find_link_by_destination(guest),
                                   "guest_destination", true);
    }
    if (!link && command_fetch_info.valid) {
      link = select_copy_edge_link(
          find_visible_link_by_base(command_fetch_info.base_address),
          "command_base", true);
    }
    if (!link) {
      link = select_copy_edge_link(find_visible_link_by_fetch(command_fetch),
                                   "command_fetch", true);
    }

    PlumeTrackedResource* destination = FindDirectResourceLocked(guest);
    if (destination) {
      if (!link) {
        link = select_copy_edge_link(
            find_link_by_destination(destination->guest_address),
            "resource_guest_destination", true);
      }
      if (!link) {
        link = select_copy_edge_link(
            find_link_by_destination(destination->parent_address),
            "resource_parent_destination", true);
      }
      if (!link) {
        link = select_copy_edge_link(
            find_visible_link_by_base(PlumeGuestTextureBaseAddress(*destination)),
            "resource_base", true);
      }
      if (!link) {
        link = select_copy_edge_link(
            find_visible_link_by_fetch(PlumeGuestTextureFetchDwords(*destination)),
            "resource_fetch", true);
      }
    }
    if (!link) {
      if (const FableTextureIdentity* identity =
              FableDirectFindTextureIdentityByObject(guest)) {
        link = select_copy_edge_link(
            find_link_by_destination(identity->object_texture),
            "identity_object_destination", true);
        if (!link) {
          link = select_copy_edge_link(
              find_link_by_destination(identity->parent_texture),
              "identity_parent_destination", true);
        }
        if (!link) {
          link = select_copy_edge_link(
              find_visible_link_by_base(identity->base_address),
              "identity_base", true);
        }
        if (!link) {
          link = select_copy_edge_link(
              find_visible_link_by_fetch(identity->fetch_dwords),
              "identity_fetch", true);
        }
      }
    }
    if (!link) {
      if (private_or_title_surface_copy_command && !exact_title_upload_source) {
        log_exact_surface_copy_miss("private_title_missing_exact_alias",
                                    nullptr, nullptr, nullptr);
      }
      return false;
    }
    if (!destination && link->trusted_destination_edge &&
        DirectResourceAddressMatchesLocked(link->texture, guest) &&
        (DirectPrivateTextureCopyKindCanBlitToColorTarget(link->kind) ||
         link->kind == FablePrivateTextureCopyKind::kDepth)) {
      EnsureDirectPrivateTextureHeaderResourceLocked(device, draw_command, slot);
      destination = FindDirectResourceLocked(guest);
    }

    PlumeTrackedResource* source = FindDirectResourceLocked(link->source_surface);
    if (!source && link->kind == FablePrivateTextureCopyKind::kDepth) {
      source = FindDirectResourceLocked(link->depth_stencil);
      if (!source) {
        source = FindDirectResourceLocked(draw_command.resources.depth_stencil);
      }
    }
    if (!source && link->kind != FablePrivateTextureCopyKind::kDepth &&
        link->render_target != 0) {
      source = FindDirectResourceLocked(link->render_target);
    }
    if (!destination) {
      destination = FindDirectResourceLocked(link->texture);
    }
    auto materialize_exact_copy_destination_from_command_fetch =
        [&]() -> bool {
      const bool exact_sibling_title_copy_symbol =
          link && (link->copy_symbol_address ==
                       kFablePrivateRectSiblingWrapperSymbol ||
                   link->copy_symbol_address ==
                       kFablePrivateRectSiblingWrapperCallsite);
      const bool exact_title_copy_symbol =
          link && (link->copy_symbol_address ==
                       kFableBeginVerticesTitleWrapperSymbol ||
                   exact_sibling_title_copy_symbol);
      const bool exact_command_destination =
          link && (DirectResourceAddressMatchesLocked(link->texture, guest) ||
                   (exact_title_copy_symbol &&
                    IsFableTitleTextureIndirectionChainAddress(guest) &&
                    DirectSurfaceTextureLinkTouchesFablePrivateChain(*link,
                                                                    nullptr)));
      if (!device || !link->trusted_destination_edge ||
          !DirectSurfaceTextureLinkHasExactCopyIdentity(*link) ||
          !DirectPrivateTextureCopyKindCanBlitToColorTarget(link->kind) ||
          !command_fetch_info.valid || guest == 0 ||
          !exact_command_destination) {
        return false;
      }
      if (destination && IsImageResource(destination->kind) &&
          PlumeGuestImageReady(*destination)) {
        PromoteDirectExactCopyDestinationToColorTargetLocked(device,
                                                             *destination,
                                                             *link);
        return true;
      }

      FableGuestResourceSnapshot snapshot;
      snapshot.kind = FableGuestResourceKind::kTexture;
      snapshot.guest_address = guest;
      snapshot.parent_address = guest;
      snapshot.texture.object_address = guest;
      snapshot.texture.parent_address = guest;
      snapshot.texture.base_address = command_fetch_info.base_address;
      snapshot.texture.raw_fetch_dwords = command_fetch;
      snapshot.texture.normalized_fetch_dwords = command_fetch;
      snapshot.serial = std::max<uint64_t>(
          1ull, std::max(draw_command.resource_serial,
                         std::max(draw_command.serial, draw_serial)));
      if (!SyncTrackedResourceLocked(device, snapshot, nullptr)) {
        ++g_direct_renderer.failed_resource_syncs;
        LogDirectResourceSyncFailureLocked(snapshot,
                                           "exact_copy_destination_fetch");
        return false;
      }

      destination = FindDirectResourceLocked(guest);
      if (!destination || !IsImageResource(destination->kind) ||
          !PlumeGuestImageReady(*destination)) {
        return false;
      }

      RegisterDirectTextureIdentityFromResourceLocked(*destination,
                                                      command_fetch);
      PromoteDirectExactCopyDestinationToColorTargetLocked(device, *destination,
                                                           *link);
      static uint32_t logged_exact_destination_materializations = 0;
      if (logged_exact_destination_materializations < 48u) {
        REXLOG_INFO(
            "Refii plume materialized exact copy destination #{} frame={} "
            "slot={} guest=0x{:08X} base=0x{:08X} {}x{} fmt={} "
            "link_surface=0x{:08X} symbol=0x{:08X} call={} "
            "context=0x{:016X} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
            "ps=0x{:08X}",
            ++logged_exact_destination_materializations,
            g_direct_renderer.current_frame_index, slot, guest,
            PlumeGuestTextureBaseAddress(*destination),
            PlumeGuestTextureWidth(*destination),
            PlumeGuestTextureHeight(*destination),
            static_cast<uint32_t>(PlumeGuestTextureFormat(*destination)),
            link->source_surface, link->copy_symbol_address,
            static_cast<unsigned long long>(link->copy_call_serial),
            static_cast<unsigned long long>(link->copy_context_key),
            draw_command.resources.render_targets[0],
            draw_command.resources.depth_stencil,
            draw_command.resources.vertex_shader,
            draw_command.resources.pixel_shader);
      }
      return true;
    };
    materialize_exact_copy_destination_from_command_fetch();
    const bool command_has_wrapper_identity =
        draw_command.draw.private_texture_wrapper_symbol_address != 0 &&
        (draw_command.draw.private_texture_wrapper_call_serial != 0 ||
         draw_command.draw.private_texture_wrapper_context_key != 0);
    const bool command_wrapper_matches_link =
        !command_has_wrapper_identity ||
        (link->copy_symbol_address ==
             draw_command.draw.private_texture_wrapper_symbol_address &&
         ((link->copy_call_serial != 0 &&
           link->copy_call_serial ==
               draw_command.draw.private_texture_wrapper_call_serial) ||
          (link->copy_context_key != 0 &&
           link->copy_context_key ==
               draw_command.draw.private_texture_wrapper_context_key)));
    const bool exact_wrapper_link_selected =
        link->trusted_destination_edge && link->copy_symbol_address != 0 &&
        (link->copy_call_serial != 0 || link->copy_context_key != 0) &&
        command_wrapper_matches_link;
    const bool exact_title_indirection_destination =
        IsFableTitleTextureIndirectionChainAddress(guest) && destination &&
        IsImageResource(destination->kind) &&
        (PlumeGuestTextureWidth(*destination) == 128u ||
         PlumeGuestTextureWidth(*destination) == 256u) &&
        PlumeGuestTextureWidth(*destination) ==
            PlumeGuestTextureHeight(*destination) &&
        static_cast<uint32_t>(PlumeGuestTextureFormat(*destination)) == 61u &&
        (!command_fetch_info.valid ||
         (command_fetch_info.width == PlumeGuestTextureWidth(*destination) &&
          command_fetch_info.height == PlumeGuestTextureHeight(*destination) &&
          (link->texture_base_address == 0u ||
           link->texture_base_address == command_fetch_info.base_address)));
    const bool exact_tiny_title_destination =
        IsFableTinyTitleUploadedTextureAddress(guest) && destination &&
        IsImageResource(destination->kind) &&
        PlumeGuestTextureWidth(*destination) == 1u &&
        PlumeGuestTextureHeight(*destination) == 1u &&
        static_cast<uint32_t>(PlumeGuestTextureFormat(*destination)) == 20u;
    const bool exact_sibling_title_copy_symbol =
        link->copy_symbol_address == kFablePrivateRectSiblingWrapperSymbol ||
        link->copy_symbol_address == kFablePrivateRectSiblingWrapperCallsite;
    const bool exact_private_rect_command_root_bridge_symbol =
        DirectIdaPrivateRectWrapperProofSymbol(link->copy_symbol_address) &&
        link->copy_symbol_address != kFablePrivateRectNativeDepthWrapperSymbol;
    const bool exact_title_copy_symbol =
        link->copy_symbol_address == kFableBeginVerticesTitleWrapperSymbol ||
        exact_sibling_title_copy_symbol;
    const bool exact_prior_title_producer_link_selected =
        link->trusted_destination_edge && link->copy_symbol_address != 0 &&
        (link->copy_call_serial != 0 || link->copy_context_key != 0) &&
        exact_title_copy_symbol &&
        (exact_title_indirection_destination || exact_tiny_title_destination);
    const bool exact_wrapper_title_source_sample =
        (exact_wrapper_link_selected ||
         exact_prior_title_producer_link_selected) &&
        exact_title_copy_symbol &&
        DirectPrivateTextureCopyKindUsesColorBacking(link->kind) && source &&
        IsImageResource(source->kind) && PlumeGuestImageReady(*source) &&
        !DirectCommandBindsResourceAsTargetLocked(draw_command, *source) &&
        ((link->copy_symbol_address == kFableBeginVerticesTitleWrapperSymbol &&
          (exact_begin_vertices_title_action ||
           exact_title_indirection_destination ||
           exact_tiny_title_destination)) ||
         (exact_sibling_title_copy_symbol &&
          (exact_title_indirection_destination ||
           exact_tiny_title_destination)));
    if (exact_wrapper_title_source_sample) {
      FableSurfaceTextureLink prepared_link = *link;
      prepared_link.frame_index = g_direct_renderer.current_frame_index;
      prepared_link.draw_serial = draw_serial;
      PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(prepared_link,
                                                             *source);
      const char* prepared_reason =
          exact_sibling_title_copy_symbol
              ? "sub_82242108-title-source-sample"
              : "beginvertices-title-source-sample";
      RegisterDirectSurfaceTextureLinkPreparedLocked(prepared_link,
                                                     prepared_reason);
      static uint32_t logged_exact_wrapper_source_samples = 0;
      if (RendererVerboseTracingEnabled() &&
          logged_exact_wrapper_source_samples < 64u) {
        REXLOG_INFO(
            "Refii plume exact wrapper title source sample #{} frame={} "
            "reason={} serial={} draw={} slot={} texture=0x{:08X} "
            "surface=0x{:08X}/{}x{} symbol=0x{:08X} call={} "
            "context=0x{:016X} title_dst={} tiny_dst={} rt=0x{:08X} "
            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_wrapper_source_samples,
            g_direct_renderer.current_frame_index, prepared_reason,
            static_cast<unsigned long long>(draw_command.serial),
            static_cast<unsigned long long>(draw_serial), slot, guest,
            source->guest_address, PlumeGuestTextureWidth(*source),
            PlumeGuestTextureHeight(*source), link->copy_symbol_address,
            static_cast<unsigned long long>(link->copy_call_serial),
            static_cast<unsigned long long>(link->copy_context_key),
            exact_title_indirection_destination ? 1u : 0u,
            exact_tiny_title_destination ? 1u : 0u,
            draw_command.resources.render_targets[0],
            draw_command.resources.depth_stencil,
            draw_command.resources.vertex_shader,
            draw_command.resources.pixel_shader);
      }
      return true;
    }
    const bool exact_tiny_title_source_surface_sample =
        private_or_title_surface_copy_command &&
        link->trusted_destination_edge &&
        DirectSurfaceTextureLinkHasExactCopyIdentity(*link) &&
        (link->copy_symbol_address == kFableBeginVerticesTitleWrapperSymbol ||
         exact_sibling_title_copy_symbol) &&
        DirectPrivateTextureCopyKindUsesColorBacking(link->kind) &&
        exact_tiny_title_destination && link->source_surface != 0 &&
        !DirectResourceAddressMatchesLocked(link->source_surface,
                                            link->texture) &&
        source && IsImageResource(source->kind) &&
        PlumeGuestImageReady(*source) &&
        !FableTrackedResourceIsBlankUploadedSampleProxy(*source) &&
        !DirectCommandBindsResourceAsTargetLocked(draw_command, *source);
    if (exact_tiny_title_source_surface_sample) {
      FableSurfaceTextureLink prepared_link = *link;
      prepared_link.frame_index = g_direct_renderer.current_frame_index;
      prepared_link.draw_serial = draw_serial;
      PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(prepared_link,
                                                             *source);
      const char* prepared_reason =
          exact_sibling_title_copy_symbol
              ? "sub_82242108-tiny-title-source-sample"
              : "beginvertices-tiny-title-source-sample";
      RegisterDirectSurfaceTextureLinkPreparedLocked(
          prepared_link, prepared_reason);

      bool materialized_tiny_destination = false;
      FableSurfaceTextureLink destination_link = prepared_link;
      destination_link.source_slot = slot;
      destination_link.texture = destination->guest_address;
      destination_link.texture_base_address =
          PlumeGuestTextureBaseAddress(*destination);
      destination_link.texture_width = PlumeGuestTextureWidth(*destination);
      destination_link.texture_height = PlumeGuestTextureHeight(*destination);
      destination_link.texture_format =
          PlumeGuestTextureGuestFormat(*destination);
      destination_link.texture_fetch_dwords =
          PlumeGuestTextureFetchDwords(*destination);
      destination_link.render_target = draw_command.resources.render_targets[0];
      destination_link.depth_stencil = draw_command.resources.depth_stencil;
      destination_link.vertex_shader = draw_command.resources.vertex_shader;
      destination_link.pixel_shader = draw_command.resources.pixel_shader;
      PopulateDirectSurfaceTextureLinkShaderRoots(destination_link,
                                                  draw_command);

      uint64_t tiny_materialize_key = 0xCBF29CE484222325ull;
      auto mix_tiny_materialize_key = [&](uint64_t value) {
        tiny_materialize_key ^= value;
        tiny_materialize_key *= 0x100000001B3ull;
      };
      mix_tiny_materialize_key(g_direct_renderer.current_frame_index);
      mix_tiny_materialize_key(destination_link.texture);
      mix_tiny_materialize_key(source->guest_address);
      mix_tiny_materialize_key(prepared_link.copy_call_serial);
      mix_tiny_materialize_key(prepared_link.copy_context_key);
      mix_tiny_materialize_key(prepared_link.copy_symbol_address);
      mix_tiny_materialize_key(draw_command.resources.render_targets[0]);
      mix_tiny_materialize_key(draw_command.resources.vertex_shader);
      mix_tiny_materialize_key(draw_command.resources.pixel_shader);

      if (g_direct_renderer.materialized_private_surface_link_frame_keys
              .insert(tiny_materialize_key)
              .second) {
        if ((!destination->texture_render_target ||
             !destination->guest_surface.attachment_view) &&
            destination_link.trusted_destination_edge) {
          PromoteDirectExactCopyDestinationToColorTargetLocked(
              device, *destination, destination_link);
        }
        DirectFramebufferBinding tiny_destination_framebuffer;
        if (destination->texture_render_target &&
            !destination->texture_depth_target &&
            ResolveDirectSingleColorFramebufferForResourceLocked(
                device, *destination, tiny_destination_framebuffer)) {
          FablePrivateTextureCopyAction tiny_action;
          tiny_action.source_slot = slot;
          tiny_action.source_texture = guest;
          tiny_action.source_base_address =
              PlumeGuestTextureBaseAddress(*source);
          tiny_action.source_width = PlumeGuestTextureWidth(*source);
          tiny_action.source_height = PlumeGuestTextureHeight(*source);
          tiny_action.source_format = PlumeGuestTextureGuestFormat(*source);
          tiny_action.source_fetch_dwords =
              HasTrackedFetchDwords(command_fetch)
                  ? command_fetch
                  : PlumeGuestTextureFetchDwords(*source);
          PopulateDirectPrivateTextureCopyTargetLocked(tiny_action,
                                                       *destination);
          tiny_action.render_target = draw_command.resources.render_targets[0];
          tiny_action.depth_stencil = draw_command.resources.depth_stencil;
          tiny_action.vertex_shader = draw_command.resources.vertex_shader;
          tiny_action.pixel_shader = draw_command.resources.pixel_shader;
          PopulateDirectPrivateTextureCopyShaderRoots(tiny_action,
                                                      draw_command);
          tiny_action.lr = draw_command.draw.state_flush_lr;
          tiny_action.wrapper_call_serial = link->copy_call_serial;
          tiny_action.wrapper_context_key = link->copy_context_key;
          tiny_action.wrapper_symbol_address = link->copy_symbol_address;
          tiny_action.wrapper_source_texture =
              link->texture != 0 ? link->texture : guest;
          tiny_action.wrapper_source_exact = true;
          tiny_action.frame_index = g_direct_renderer.current_frame_index;
          tiny_action.draw_serial = draw_serial;
          tiny_action.kind = FablePrivateTextureCopyKind::kColor;
          tiny_action.uv_override = true;
          tiny_action.u0 = 0.0f;
          tiny_action.v0 = 0.0f;
          tiny_action.u1 = 1.0f;
          tiny_action.v1 = 1.0f;
          tiny_action.sampled_source = destination_link.sampled_source;

          materialized_tiny_destination =
              RecordDirectScaledPrivateRectCopyLocked(
                  command_list, draw_command, tiny_destination_framebuffer,
                  tiny_action, *source, *destination, slot,
                  exact_sibling_title_copy_symbol
                      ? "sub_82242108-tiny-title-destination"
                      : "beginvertices-tiny-title-destination");
        }
      } else {
        materialized_tiny_destination = true;
      }

      if (materialized_tiny_destination) {
        destination_link.target_content_serial =
            FableTrackedResourceVisibleContentSerial(*destination);
        destination_link.sampled_source.kind =
            FableRegisteredSampleSourceKind::kGpuSurface;
        destination_link.sampled_source.resource = destination->guest_address;
        destination_link.sampled_source.content_serial =
            destination_link.target_content_serial;
        RegisterDirectSurfaceTextureLinkPreparedLocked(
            destination_link,
            exact_sibling_title_copy_symbol
                ? "sub_82242108-tiny-title-destination"
                : "beginvertices-tiny-title-destination");

        static uint32_t logged_exact_tiny_title_destination_materializations = 0;
        if (logged_exact_tiny_title_destination_materializations < 64u) {
          REXLOG_INFO(
              "Refii plume exact tiny-title destination materialized #{} "
              "frame={} reason={} serial={} draw={} slot={} texture=0x{:08X} "
              "source=0x{:08X}/{}x{} destination=0x{:08X}/{}x{} "
              "call={} context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} "
              "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
              ++logged_exact_tiny_title_destination_materializations,
              g_direct_renderer.current_frame_index,
              destination_link.copy_symbol_address ==
                      kFablePrivateRectSiblingWrapperSymbol ||
                      destination_link.copy_symbol_address ==
                          kFablePrivateRectSiblingWrapperCallsite
                  ? "sub_82242108-tiny-title-destination"
                  : "beginvertices-tiny-title-destination",
              static_cast<unsigned long long>(draw_command.serial),
              static_cast<unsigned long long>(draw_serial), slot,
              destination_link.texture, source->guest_address,
              PlumeGuestTextureWidth(*source), PlumeGuestTextureHeight(*source),
              destination->guest_address, PlumeGuestTextureWidth(*destination),
              PlumeGuestTextureHeight(*destination),
              static_cast<unsigned long long>(
                  destination_link.copy_call_serial),
              static_cast<unsigned long long>(
                  destination_link.copy_context_key),
              destination_link.copy_symbol_address,
              draw_command.resources.render_targets[0],
              draw_command.resources.depth_stencil,
              draw_command.resources.vertex_shader,
              draw_command.resources.pixel_shader);
        }
        return true;
      }


      static uint32_t logged_exact_tiny_title_source_samples = 0;
      if (logged_exact_tiny_title_source_samples < 64u) {
        REXLOG_INFO(
            "Refii plume exact tiny-title source-surface sample #{} "
            "frame={} reason={} serial={} draw={} slot={} texture=0x{:08X} "
            "surface=0x{:08X}/{}x{} destination=0x{:08X}/{}x{} "
            "call={} context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} "
            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
            "link_vs_root=0x{:08X}/{} link_ps_root=0x{:08X}/{} "
            "cmd_vs_root=0x{:08X}/{} cmd_ps_root=0x{:08X}/{}",
            ++logged_exact_tiny_title_source_samples,
            g_direct_renderer.current_frame_index, prepared_reason,
            static_cast<unsigned long long>(draw_command.serial),
            static_cast<unsigned long long>(draw_serial), slot, link->texture,
            source->guest_address, PlumeGuestTextureWidth(*source),
            PlumeGuestTextureHeight(*source), destination->guest_address,
            PlumeGuestTextureWidth(*destination),
            PlumeGuestTextureHeight(*destination),
            static_cast<unsigned long long>(link->copy_call_serial),
            static_cast<unsigned long long>(link->copy_context_key),
            link->copy_symbol_address, draw_command.resources.render_targets[0],
            draw_command.resources.depth_stencil,
            draw_command.resources.vertex_shader,
            draw_command.resources.pixel_shader,
            link->vertex_shader_root_symbol_address,
            static_cast<unsigned long long>(
                link->vertex_shader_root_call_serial),
            link->pixel_shader_root_symbol_address,
            static_cast<unsigned long long>(
                link->pixel_shader_root_call_serial),
            draw_command.resources.shader_roots.vertex_shader.symbol_address,
            static_cast<unsigned long long>(
                draw_command.resources.shader_roots.vertex_shader.call_serial),
            draw_command.resources.shader_roots.pixel_shader.symbol_address,
            static_cast<unsigned long long>(
                draw_command.resources.shader_roots.pixel_shader.call_serial));
      }
      return true;
    }
    const bool selected_link_names_command_destination =
        DirectResourceAddressMatchesLocked(link->texture, guest) ||
        DirectResourceAddressMatchesLocked(link->backing_texture, guest);
    if (private_or_title_surface_copy_command &&
        selected_copy_edge_pix_video_broad_match &&
        !selected_link_names_command_destination) {
      return false;
    }
    if (private_or_title_surface_copy_command &&
        !selected_link_names_command_destination) {
      static uint32_t logged_exact_private_title_destination_rejects = 0;
      if (logged_exact_private_title_destination_rejects < 64u) {
        REXLOG_INFO(
            "Refii plume exact private/title surface-copy destination "
            "reject #{} frame={} serial={} draw={} slot={} guest=0x{:08X} "
            "link_tex=0x{:08X} link_surface=0x{:08X} "
            "link_backing=0x{:08X} link_slot={} link_frame={} "
            "link_draw={} link_call={} link_context=0x{:016X} "
            "link_symbol=0x{:08X} bucket={} rt=0x{:08X} ds=0x{:08X} "
            "vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_private_title_destination_rejects,
            g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(draw_command.serial),
            static_cast<unsigned long long>(draw_serial), slot, guest,
            link->texture, link->source_surface, link->backing_texture,
            link->source_slot, link->frame_index,
            static_cast<unsigned long long>(link->draw_serial),
            static_cast<unsigned long long>(link->copy_call_serial),
            static_cast<unsigned long long>(link->copy_context_key),
            link->copy_symbol_address,
            selected_copy_edge_bucket ? selected_copy_edge_bucket : "unknown",
            draw_command.resources.render_targets[0],
            draw_command.resources.depth_stencil,
            draw_command.resources.vertex_shader,
            draw_command.resources.pixel_shader);
      }
      return false;
    }
    const bool exact_private_chain_wrapper_identity =
        link->copy_symbol_address != 0 &&
        (link->copy_call_serial != 0 || link->copy_context_key != 0);
    const bool exact_private_chain_target_source_surface_sample =
        private_or_title_surface_copy_command &&
        exact_private_chain_wrapper_identity &&
        link->trusted_destination_edge &&
        DirectSurfaceTextureLinkHasExactCopyIdentity(*link) &&
        DirectPrivateTextureCopyKindUsesColorBacking(link->kind) &&
        source && destination && IsImageResource(source->kind) &&
        IsImageResource(destination->kind) &&
        IsFablePrivateTextureChainResource(*destination) &&
        DirectSurfaceTextureLinkTouchesFablePrivateChain(*link, nullptr) &&
        link->source_surface != 0 &&
        !DirectResourceAddressMatchesLocked(link->source_surface,
                                            link->texture) &&
        PlumeGuestImageReady(*source) &&
        !FableTrackedResourceIsBlankUploadedSampleProxy(*source) &&
        !DirectCommandBindsResourceAsTargetLocked(draw_command, *source) &&
        DirectCommandBindsResourceAsTargetLocked(draw_command, *destination);
    if (exact_private_chain_target_source_surface_sample) {
      FableSurfaceTextureLink prepared_link = *link;
      prepared_link.frame_index = g_direct_renderer.current_frame_index;
      prepared_link.draw_serial = draw_serial;
      PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(prepared_link,
                                                             *source);
      if (exact_private_rect_command_root_bridge_symbol) {
        prepared_link.render_target = draw_command.resources.render_targets[0];
        prepared_link.depth_stencil = draw_command.resources.depth_stencil;
        prepared_link.vertex_shader = draw_command.resources.vertex_shader;
        prepared_link.pixel_shader = draw_command.resources.pixel_shader;
        PopulateDirectSurfaceTextureLinkShaderRoots(prepared_link, draw_command);
      }
      RegisterDirectSurfaceTextureLinkPreparedLocked(
          prepared_link, "exact-private-chain-target-source-sample");

      static uint32_t logged_exact_private_chain_target_source_samples = 0;
      if (RendererVerboseTracingEnabled() &&
          logged_exact_private_chain_target_source_samples < 64u) {
        REXLOG_INFO(
            "Refii plume exact private-chain target-source sample #{} "
            "frame={} serial={} draw={} slot={} texture=0x{:08X} "
            "surface=0x{:08X}/{}x{} destination=0x{:08X}/{}x{} "
            "call={} context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} "
            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
            "link_vs_root=0x{:08X}/{} link_ps_root=0x{:08X}/{} "
            "cmd_vs_root=0x{:08X}/{} cmd_ps_root=0x{:08X}/{}",
            ++logged_exact_private_chain_target_source_samples,
            g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(draw_command.serial),
            static_cast<unsigned long long>(draw_serial), slot, link->texture,
            source->guest_address, PlumeGuestTextureWidth(*source),
            PlumeGuestTextureHeight(*source), destination->guest_address,
            PlumeGuestTextureWidth(*destination),
            PlumeGuestTextureHeight(*destination),
            static_cast<unsigned long long>(link->copy_call_serial),
            static_cast<unsigned long long>(link->copy_context_key),
            link->copy_symbol_address, draw_command.resources.render_targets[0],
            draw_command.resources.depth_stencil,
            draw_command.resources.vertex_shader,
            draw_command.resources.pixel_shader,
            prepared_link.vertex_shader_root_symbol_address,
            static_cast<unsigned long long>(
                prepared_link.vertex_shader_root_call_serial),
            prepared_link.pixel_shader_root_symbol_address,
            static_cast<unsigned long long>(
                prepared_link.pixel_shader_root_call_serial),
            draw_command.resources.shader_roots.vertex_shader.symbol_address,
            static_cast<unsigned long long>(
                draw_command.resources.shader_roots.vertex_shader.call_serial),
            draw_command.resources.shader_roots.pixel_shader.symbol_address,
            static_cast<unsigned long long>(
                draw_command.resources.shader_roots.pixel_shader.call_serial));
      }
      return true;
    }
    const bool exact_private_chain_source_surface_sample =
        private_or_title_surface_copy_command &&
        link->trusted_destination_edge &&
        DirectSurfaceTextureLinkHasExactCopyIdentity(*link) &&
        DirectPrivateTextureCopyKindUsesColorBacking(link->kind) &&
        source && destination && IsImageResource(source->kind) &&
        IsImageResource(destination->kind) &&
        IsFablePrivateTextureChainResource(*destination) &&
        DirectSurfaceTextureLinkTouchesFablePrivateChain(*link, nullptr) &&
        link->source_surface != 0 &&
        !DirectResourceAddressMatchesLocked(link->source_surface,
                                            link->texture) &&
        PlumeGuestImageReady(*source) &&
        !FableTrackedResourceIsBlankUploadedSampleProxy(*source) &&
        !DirectCommandBindsResourceAsTargetLocked(draw_command, *source) &&
        !DirectCommandBindsResourceAsTargetLocked(draw_command, *destination);
    if (exact_private_chain_source_surface_sample) {
      FableSurfaceTextureLink prepared_link = *link;
      prepared_link.frame_index = g_direct_renderer.current_frame_index;
      prepared_link.draw_serial = draw_serial;
      PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(prepared_link,
                                                             *source);
      if (exact_private_rect_command_root_bridge_symbol) {
        prepared_link.render_target = draw_command.resources.render_targets[0];
        prepared_link.depth_stencil = draw_command.resources.depth_stencil;
        prepared_link.vertex_shader = draw_command.resources.vertex_shader;
        prepared_link.pixel_shader = draw_command.resources.pixel_shader;
        PopulateDirectSurfaceTextureLinkShaderRoots(prepared_link, draw_command);
      }
      RegisterDirectSurfaceTextureLinkPreparedLocked(
          prepared_link, "exact-private-chain-source-sample");

      static uint32_t logged_exact_private_chain_source_samples = 0;
      if (RendererVerboseTracingEnabled() &&
          logged_exact_private_chain_source_samples < 64u) {
        REXLOG_INFO(
            "Refii plume exact private-chain source-surface sample #{} "
            "frame={} serial={} draw={} slot={} texture=0x{:08X} "
            "surface=0x{:08X}/{}x{} destination=0x{:08X}/{}x{} "
            "call={} context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} "
            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
            "link_vs_root=0x{:08X}/{} link_ps_root=0x{:08X}/{} "
            "cmd_vs_root=0x{:08X}/{} cmd_ps_root=0x{:08X}/{}",
            ++logged_exact_private_chain_source_samples,
            g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(draw_command.serial),
            static_cast<unsigned long long>(draw_serial), slot, link->texture,
            source->guest_address, PlumeGuestTextureWidth(*source),
            PlumeGuestTextureHeight(*source), destination->guest_address,
            PlumeGuestTextureWidth(*destination),
            PlumeGuestTextureHeight(*destination),
            static_cast<unsigned long long>(link->copy_call_serial),
            static_cast<unsigned long long>(link->copy_context_key),
            link->copy_symbol_address, draw_command.resources.render_targets[0],
            draw_command.resources.depth_stencil,
            draw_command.resources.vertex_shader,
            draw_command.resources.pixel_shader,
            prepared_link.vertex_shader_root_symbol_address,
            static_cast<unsigned long long>(
                prepared_link.vertex_shader_root_call_serial),
            prepared_link.pixel_shader_root_symbol_address,
            static_cast<unsigned long long>(
                prepared_link.pixel_shader_root_call_serial),
            draw_command.resources.shader_roots.vertex_shader.symbol_address,
            static_cast<unsigned long long>(
                draw_command.resources.shader_roots.vertex_shader.call_serial),
            draw_command.resources.shader_roots.pixel_shader.symbol_address,
            static_cast<unsigned long long>(
                draw_command.resources.shader_roots.pixel_shader.call_serial));
      }
      return true;
    }
	    if ((!source || !destination || !IsImageResource(source->kind) ||
	         !IsImageResource(destination->kind) || !PlumeGuestImageReady(*source) ||
	         !PlumeGuestImageReady(*destination)) &&
	        DirectSyntheticPrivateRectCopyCanExecuteFromConcreteSourceLocked(
	            draw_command)) {
	      return true;
	    }
	    const bool exact_private_title_self_edge =
	        private_or_title_surface_copy_command && !exact_title_upload_source &&
	        link->trusted_destination_edge &&
	        link->kind != FablePrivateTextureCopyKind::kDepth &&
	        DirectSurfaceTextureLinkHasExactCopyIdentity(*link) &&
	        DirectSurfaceTextureLinkTouchesFablePrivateChain(*link, nullptr) &&
	        link->texture != 0 &&
	        DirectResourceAddressMatchesLocked(link->texture,
	                                           link->source_surface);
	    if ((!source || !destination || !IsImageResource(source->kind) ||
	         !IsImageResource(destination->kind) ||
	         !PlumeGuestImageReady(*source) ||
	         !PlumeGuestImageReady(*destination)) &&
	        exact_private_title_self_edge) {
      static uint32_t logged_exact_copy_self_edge_owners = 0;
      if (logged_exact_copy_self_edge_owners < 32u) {
        REXLOG_INFO(
            "Refii plume exact surface copy self-edge owner #{} "
	            "frame={} serial={} draw={} source={} slot={} guest=0x{:08X} "
	            "texture=0x{:08X} call={} context=0x{:016X} "
	            "symbol=0x{:08X} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
	            "ps=0x{:08X} link_vs_root=0x{:08X}/{} "
	            "link_ps_root=0x{:08X}/{} cmd_vs_root=0x{:08X}/{} "
	            "cmd_ps_root=0x{:08X}/{}",
            ++logged_exact_copy_self_edge_owners,
	            g_direct_renderer.current_frame_index,
	            static_cast<unsigned long long>(draw_command.serial),
	            static_cast<unsigned long long>(draw_serial),
	            static_cast<uint32_t>(draw_command.draw.source), slot, guest,
	            link->texture,
	            static_cast<unsigned long long>(link->copy_call_serial),
	            static_cast<unsigned long long>(link->copy_context_key),
	            link->copy_symbol_address, draw_command.resources.render_targets[0],
	            draw_command.resources.depth_stencil,
	            draw_command.resources.vertex_shader,
	            draw_command.resources.pixel_shader,
	            link->vertex_shader_root_symbol_address,
	            static_cast<unsigned long long>(
	                link->vertex_shader_root_call_serial),
	            link->pixel_shader_root_symbol_address,
	            static_cast<unsigned long long>(
	                link->pixel_shader_root_call_serial),
	            draw_command.resources.shader_roots.vertex_shader.symbol_address,
	            static_cast<unsigned long long>(
	                draw_command.resources.shader_roots.vertex_shader.call_serial),
	            draw_command.resources.shader_roots.pixel_shader.symbol_address,
	            static_cast<unsigned long long>(
	                draw_command.resources.shader_roots.pixel_shader.call_serial));
	      }
	      return true;
	    }
    const bool exact_native_depth_missing_destination_source_sample =
        private_or_title_surface_copy_command &&
        link->trusted_destination_edge &&
        DirectSurfaceTextureLinkHasExactCopyIdentity(*link) &&
        link->kind == FablePrivateTextureCopyKind::kDepth &&
        FablePrivateTextureProxyRoleForAddress(link->texture) ==
            FablePrivateTextureProxyRole::kDepth &&
        link->source_surface != 0 &&
        !DirectResourceAddressMatchesLocked(link->source_surface,
                                            link->texture) &&
        source && IsImageResource(source->kind) && PlumeGuestImageReady(*source) &&
        !FableTrackedResourceIsBlankUploadedSampleProxy(*source) &&
        !DirectCommandBindsResourceAsTargetLocked(draw_command, *source) &&
        (!destination || !IsImageResource(destination->kind) ||
         !PlumeGuestImageReady(*destination));
	    if (exact_native_depth_missing_destination_source_sample) {
	      FableSurfaceTextureLink prepared_link = *link;
	      prepared_link.frame_index = g_direct_renderer.current_frame_index;
	      prepared_link.draw_serial = draw_serial;
      prepared_link.render_target = draw_command.resources.render_targets[0];
      prepared_link.depth_stencil = draw_command.resources.depth_stencil;
      prepared_link.vertex_shader = draw_command.resources.vertex_shader;
      prepared_link.pixel_shader = draw_command.resources.pixel_shader;
      PopulateDirectSurfaceTextureLinkShaderRoots(prepared_link, draw_command);
      PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(prepared_link,
                                                             *source);
      RegisterDirectSurfaceTextureLinkPreparedLocked(
          prepared_link, "exact-native-depth-source-proxy-sample");

      static uint32_t logged_exact_native_depth_proxy_samples = 0;
      if (logged_exact_native_depth_proxy_samples < 64u) {
        REXLOG_INFO(
            "Refii plume exact native-depth source proxy sample #{} "
            "frame={} serial={} draw={} slot={} texture=0x{:08X} "
            "surface=0x{:08X}/{}x{} call={} context=0x{:016X} "
            "symbol=0x{:08X} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
            "ps=0x{:08X}",
            ++logged_exact_native_depth_proxy_samples,
            g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(draw_command.serial),
            static_cast<unsigned long long>(draw_serial), slot, link->texture,
            source->guest_address, PlumeGuestTextureWidth(*source),
            PlumeGuestTextureHeight(*source),
            static_cast<unsigned long long>(link->copy_call_serial),
            static_cast<unsigned long long>(link->copy_context_key),
            link->copy_symbol_address, draw_command.resources.render_targets[0],
            draw_command.resources.depth_stencil,
            draw_command.resources.vertex_shader,
            draw_command.resources.pixel_shader);
	      }
	      return true;
	    }
		    const bool exact_native_depth_current_target_ownership_proof =
		        link->kind == FablePrivateTextureCopyKind::kDepth &&
		        link->copy_symbol_address == kFablePrivateRectNativeDepthWrapperSymbol &&
		        DirectSurfaceTextureLinkHasExactCopyIdentity(*link) && source &&
	        destination && source->texture_depth_target &&
	        destination->texture_render_target &&
	        DirectResourceAddressMatchesLocked(source->guest_address,
	                                           draw_command.resources.depth_stencil) &&
	        DirectResourceAddressMatchesLocked(link->texture,
	                                           destination->guest_address) &&
		        PlumeGuestTextureWidth(*source) == PlumeGuestTextureWidth(*destination) &&
		        PlumeGuestTextureHeight(*source) ==
		            PlumeGuestTextureHeight(*destination);
		    if (exact_native_depth_current_target_ownership_proof) {
		      static uint32_t
		          logged_exact_native_depth_current_target_ownership_proofs = 0;
		      if (logged_exact_native_depth_current_target_ownership_proofs < 32u) {
		        REXLOG_INFO(
		            "Refii plume exact native-depth current-target ownership "
		            "proof #{} reason=depth-source-color-destination frame={} "
		            "serial={} draw={} slot={} texture=0x{:08X} "
		            "surface=0x{:08X}/{}x{} destination=0x{:08X}/{}x{} "
		            "call={} context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} "
		            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
		            "source_depth_target={} destination_render_target={} "
		            "source_matches_ds={} texture_matches_destination={} "
		            "same_size={}",
		            ++logged_exact_native_depth_current_target_ownership_proofs,
	            g_direct_renderer.current_frame_index,
	            static_cast<unsigned long long>(draw_command.serial),
	            static_cast<unsigned long long>(draw_serial), slot, link->texture,
	            source->guest_address, PlumeGuestTextureWidth(*source),
	            PlumeGuestTextureHeight(*source), destination->guest_address,
	            PlumeGuestTextureWidth(*destination),
	            PlumeGuestTextureHeight(*destination),
	            static_cast<unsigned long long>(link->copy_call_serial),
	            static_cast<unsigned long long>(link->copy_context_key),
	            link->copy_symbol_address, draw_command.resources.render_targets[0],
	            draw_command.resources.depth_stencil,
	            draw_command.resources.vertex_shader,
	            draw_command.resources.pixel_shader,
	            source->texture_depth_target,
	            destination->texture_render_target,
	            DirectResourceAddressMatchesLocked(
	                source->guest_address, draw_command.resources.depth_stencil),
	            DirectResourceAddressMatchesLocked(link->texture,
	                                               destination->guest_address),
	            PlumeGuestTextureWidth(*source) ==
	                    PlumeGuestTextureWidth(*destination) &&
	                PlumeGuestTextureHeight(*source) ==
	                    PlumeGuestTextureHeight(*destination));
	      }
	      return true;
	    }
	    if (!source || !destination || !IsImageResource(source->kind) ||
	        !IsImageResource(destination->kind) || !PlumeGuestImageReady(*source) ||
	        !PlumeGuestImageReady(*destination)) {
      log_exact_surface_copy_miss("invalid_endpoint", link, source,
                                  destination);
      return false;
    }
    const bool begin_vertices_current_target_size_mismatch =
        link->copy_symbol_address == kFableBeginVerticesTitleWrapperSymbol &&
        DirectCommandBindsResourceAsTargetLocked(draw_command, *source) &&
        link->texture != 0 &&
        PlumeGuestTextureWidth(*source) != 0 &&
        PlumeGuestTextureHeight(*source) != 0 &&
        link->texture_width != 0 && link->texture_height != 0 &&
        (PlumeGuestTextureWidth(*source) != link->texture_width ||
         PlumeGuestTextureHeight(*source) != link->texture_height);
	    const bool exact_current_target_surface_link_raw =
	        private_or_title_surface_copy_command &&
	        link->trusted_destination_edge &&
	        link->kind != FablePrivateTextureCopyKind::kDepth &&
	        !DirectSurfaceTextureLinkIsBeginVerticesTitleStripUpload(*link) &&
          !begin_vertices_current_target_size_mismatch &&
	        DirectSurfaceTextureLinkHasExactCopyIdentity(*link) &&
	        DirectSurfaceTextureLinkIsCurrentTargetExactEdgeLocked(
	            draw_command, *link);
    const bool concrete_depth_source =
        source->texture_depth_target ||
        DirectResourceAddressMatchesLocked(source->guest_address,
                                           draw_command.resources.depth_stencil) ||
        DirectResourceAddressMatchesLocked(source->parent_address,
                                           draw_command.resources.depth_stencil);
    const bool concrete_depth_destination = destination->texture_depth_target;
    const bool native_depth_family_color_endpoints =
        link->kind == FablePrivateTextureCopyKind::kDepth &&
        link->copy_symbol_address == kFablePrivateRectNativeDepthWrapperSymbol &&
        FablePrivateTextureProxyRoleForAddress(link->texture) ==
            FablePrivateTextureProxyRole::kDepth &&
        source->texture_render_target && destination->texture_render_target &&
        !concrete_depth_source && !concrete_depth_destination;
    const bool source_is_depth_edge =
        !native_depth_family_color_endpoints &&
        (link->kind == FablePrivateTextureCopyKind::kDepth ||
         concrete_depth_source);
	    const bool exact_current_target_dimension_mismatch =
	        exact_current_target_surface_link_raw &&
	        DirectCommandBindsResourceAsTargetLocked(draw_command, *source) &&
	        link->texture != 0 &&
	        !DirectResourceAddressMatchesLocked(link->texture,
	                                           link->source_surface) &&
	        PlumeGuestTextureWidth(*source) != 0 &&
	        PlumeGuestTextureHeight(*source) != 0 &&
	        PlumeGuestTextureWidth(*destination) != 0 &&
	        PlumeGuestTextureHeight(*destination) != 0 &&
	        (PlumeGuestTextureWidth(*source) !=
	             PlumeGuestTextureWidth(*destination) ||
	         PlumeGuestTextureHeight(*source) !=
	             PlumeGuestTextureHeight(*destination));
	    if (exact_current_target_dimension_mismatch) {
      if (source_is_depth_edge &&
          DirectSyntheticPrivateRectDepthActionCanExecuteLocked(
              draw_command, slot, destination)) {
        static uint32_t
            logged_exact_scaled_current_target_depth_texture_identities = 0;
        if (logged_exact_scaled_current_target_depth_texture_identities <
            48u) {
          REXLOG_INFO(
              "Refii plume exact scaled current-target depth surface-texture identity #{} "
              "frame={} serial={} draw={} slot={} texture=0x{:08X} "
              "surface=0x{:08X}/{}x{} target=0x{:08X}/{}x{} "
              "call={} context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} "
              "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
              ++logged_exact_scaled_current_target_depth_texture_identities,
              g_direct_renderer.current_frame_index,
              static_cast<unsigned long long>(draw_command.serial),
              static_cast<unsigned long long>(draw_serial), slot, link->texture,
              source->guest_address, PlumeGuestTextureWidth(*source),
              PlumeGuestTextureHeight(*source), destination->guest_address,
              PlumeGuestTextureWidth(*destination),
              PlumeGuestTextureHeight(*destination),
              static_cast<unsigned long long>(link->copy_call_serial),
              static_cast<unsigned long long>(link->copy_context_key),
              link->copy_symbol_address, draw_command.resources.render_targets[0],
              draw_command.resources.depth_stencil,
              draw_command.resources.vertex_shader,
              draw_command.resources.pixel_shader);
        }
        return true;
      }
      if (!source_is_depth_edge &&
          DirectSyntheticPrivateRectColorActionCanExecuteLocked(
              draw_command, slot, destination)) {
        static uint32_t
            logged_exact_scaled_current_target_color_texture_identities = 0;
        if (logged_exact_scaled_current_target_color_texture_identities < 48u) {
          REXLOG_INFO(
              "Refii plume exact scaled current-target color surface-texture identity #{} "
              "frame={} serial={} draw={} slot={} texture=0x{:08X} "
              "surface=0x{:08X}/{}x{} target=0x{:08X}/{}x{} "
              "call={} context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} "
              "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
              ++logged_exact_scaled_current_target_color_texture_identities,
              g_direct_renderer.current_frame_index,
              static_cast<unsigned long long>(draw_command.serial),
              static_cast<unsigned long long>(draw_serial), slot, link->texture,
              source->guest_address, PlumeGuestTextureWidth(*source),
              PlumeGuestTextureHeight(*source), destination->guest_address,
              PlumeGuestTextureWidth(*destination),
              PlumeGuestTextureHeight(*destination),
              static_cast<unsigned long long>(link->copy_call_serial),
              static_cast<unsigned long long>(link->copy_context_key),
              link->copy_symbol_address, draw_command.resources.render_targets[0],
              draw_command.resources.depth_stencil,
              draw_command.resources.vertex_shader,
              draw_command.resources.pixel_shader);
        }
        return true;
      }
	      static uint32_t
	          logged_exact_current_target_surface_link_dimension_exclusions = 0;
	      if (logged_exact_current_target_surface_link_dimension_exclusions < 48u) {
	        REXLOG_INFO(
	            "Refii plume exact current-target surface-link dimension excluded #{} "
	            "frame={} serial={} draw={} slot={} texture=0x{:08X} "
	            "surface=0x{:08X}/{}x{} destination=0x{:08X}/{}x{} "
	            "call={} context=0x{:016X} symbol=0x{:08X} "
	            "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
	            ++logged_exact_current_target_surface_link_dimension_exclusions,
	            g_direct_renderer.current_frame_index,
	            static_cast<unsigned long long>(draw_command.serial),
	            static_cast<unsigned long long>(draw_serial), slot,
	            link->texture, source->guest_address,
	            PlumeGuestTextureWidth(*source),
	            PlumeGuestTextureHeight(*source), destination->guest_address,
	            PlumeGuestTextureWidth(*destination),
	            PlumeGuestTextureHeight(*destination),
	            static_cast<unsigned long long>(link->copy_call_serial),
	            static_cast<unsigned long long>(link->copy_context_key),
	            link->copy_symbol_address, draw_command.resources.render_targets[0],
	            draw_command.resources.depth_stencil,
	            draw_command.resources.vertex_shader,
	            draw_command.resources.pixel_shader);
	      }
	    }
	    const bool exact_current_target_surface_link =
	        exact_current_target_surface_link_raw &&
	        !exact_current_target_dimension_mismatch;
	    if (exact_current_target_surface_link) {
	      const uint64_t source_content_serial =
	          FableTrackedResourceVisibleContentSerial(*source);
	      if (source_content_serial != 0 &&
	          link->target_content_serial < source_content_serial) {
        if (link->copy_symbol_address ==
                kFablePrivateRectSiblingWrapperCallsite &&
            link->target_content_serial == 0 &&
            link->backing_content_serial == 0 &&
            link->sampled_source.content_serial == 0) {
          FableSurfaceTextureLink sibling_sample_link = *link;
          sibling_sample_link.frame_index =
              g_direct_renderer.current_frame_index;
          sibling_sample_link.draw_serial = draw_serial;
          sibling_sample_link.render_target =
              draw_command.resources.render_targets[0];
          sibling_sample_link.depth_stencil =
              draw_command.resources.depth_stencil;
          sibling_sample_link.vertex_shader =
              draw_command.resources.vertex_shader;
          sibling_sample_link.pixel_shader =
              draw_command.resources.pixel_shader;
          PopulateDirectSurfaceTextureLinkShaderRoots(sibling_sample_link,
                                                      draw_command);
          PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(
              sibling_sample_link, *source);
          RegisterDirectSurfaceTextureLinkPreparedLocked(
              sibling_sample_link, "exact-current-target-private-rect");

          static uint32_t logged_exact_current_target_sibling_links = 0;
          if (logged_exact_current_target_sibling_links < 48u) {
            REXLOG_INFO(
                "Refii plume exact current-target private-rect surface-texture identity #{} "
                "role=private-rect frame={} serial={} draw={} slot={} "
                "texture=0x{:08X} surface=0x{:08X}/{}x{} content={} call={} "
                "context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} "
                "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
                ++logged_exact_current_target_sibling_links,
                g_direct_renderer.current_frame_index,
                static_cast<unsigned long long>(draw_command.serial),
                static_cast<unsigned long long>(draw_serial), slot,
                sibling_sample_link.texture,
                sibling_sample_link.source_surface,
                PlumeGuestTextureWidth(*source),
                PlumeGuestTextureHeight(*source),
                static_cast<unsigned long long>(
                    sibling_sample_link.sampled_source.content_serial),
                static_cast<unsigned long long>(
                    sibling_sample_link.copy_call_serial),
                static_cast<unsigned long long>(
                    sibling_sample_link.copy_context_key),
                sibling_sample_link.copy_symbol_address,
                draw_command.resources.render_targets[0],
                draw_command.resources.depth_stencil,
                draw_command.resources.vertex_shader,
                draw_command.resources.pixel_shader);
          }
        }
	        FableSurfaceTextureLink prepared_link = *link;
	        prepared_link.frame_index = g_direct_renderer.current_frame_index;
	        prepared_link.draw_serial = draw_serial;
	        prepared_link.render_target = draw_command.resources.render_targets[0];
	        prepared_link.depth_stencil = draw_command.resources.depth_stencil;
	        prepared_link.vertex_shader = draw_command.resources.vertex_shader;
	        prepared_link.pixel_shader = draw_command.resources.pixel_shader;
	        PopulateDirectSurfaceTextureLinkShaderRoots(prepared_link,
	                                                    draw_command);
	        PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(prepared_link,
	                                                               *source);
          RegisterDirectSurfaceTextureLinkPreparedLocked(
              prepared_link, "exact-current-target-surface-texture-identity");

	        static uint32_t logged_exact_current_target_surface_samples = 0;
	        if (logged_exact_current_target_surface_samples < 48u) {
	          REXLOG_INFO(
              "Refii plume exact current-target surface-texture identity #{} "
	              "frame={} serial={} draw={} slot={} texture=0x{:08X} "
	              "surface=0x{:08X}/{}x{} destination=0x{:08X}/{}x{} "
	              "content={} call={} context=0x{:016X} symbol=0x{:08X} "
	              "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
	              ++logged_exact_current_target_surface_samples,
	              g_direct_renderer.current_frame_index,
	              static_cast<unsigned long long>(draw_command.serial),
	              static_cast<unsigned long long>(draw_serial), slot,
	              link->texture, source->guest_address,
	              PlumeGuestTextureWidth(*source),
	              PlumeGuestTextureHeight(*source), destination->guest_address,
	              PlumeGuestTextureWidth(*destination),
	              PlumeGuestTextureHeight(*destination),
	              static_cast<unsigned long long>(source_content_serial),
	              static_cast<unsigned long long>(link->copy_call_serial),
	              static_cast<unsigned long long>(link->copy_context_key),
	              link->copy_symbol_address,
	              draw_command.resources.render_targets[0],
	              draw_command.resources.depth_stencil,
	              draw_command.resources.vertex_shader,
	              draw_command.resources.pixel_shader);
	        }
	      }
      static uint32_t logged_exact_current_target_texture_identity_owners = 0;
      if (logged_exact_current_target_texture_identity_owners < 64u) {
	        REXLOG_INFO(
            "Refii plume exact current-target owned surface-texture identity #{} "
            "frame={} serial={} draw={} slot={} texture=0x{:08X} "
            "surface=0x{:08X}/{}x{} destination=0x{:08X}/{}x{} "
	            "call={} context=0x{:016X} symbol=0x{:08X} "
	            "wrapper_source=0x{:08X}/{} rt=0x{:08X} ds=0x{:08X} "
	            "vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_current_target_texture_identity_owners,
	            g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(draw_command.serial),
            static_cast<unsigned long long>(draw_serial), slot, link->texture,
            source->guest_address, PlumeGuestTextureWidth(*source),
            PlumeGuestTextureHeight(*source), destination->guest_address,
            PlumeGuestTextureWidth(*destination),
            PlumeGuestTextureHeight(*destination),
	            static_cast<unsigned long long>(link->copy_call_serial),
	            static_cast<unsigned long long>(link->copy_context_key),
	            link->copy_symbol_address, link->wrapper_source_texture,
	            link->wrapper_source_exact ? 1 : 0,
	            draw_command.resources.render_targets[0],
	            draw_command.resources.depth_stencil,
	            draw_command.resources.vertex_shader,
            draw_command.resources.pixel_shader);
      }
      // The link proves ownership, but the active draw still needs a stable
      // sampleable destination instead of sampling the bound render target.
    }

    const bool exact_native_depth_source_surface_sample =
        private_or_title_surface_copy_command &&
        link->trusted_destination_edge &&
        DirectSurfaceTextureLinkHasExactCopyIdentity(*link) &&
        link->kind == FablePrivateTextureCopyKind::kDepth &&
        FablePrivateTextureProxyRoleForAddress(link->texture) ==
            FablePrivateTextureProxyRole::kDepth &&
        link->source_surface != 0 &&
        !DirectResourceAddressMatchesLocked(link->source_surface,
                                            link->texture) &&
        PlumeGuestImageReady(*source) &&
        !FableTrackedResourceIsBlankUploadedSampleProxy(*source) &&
        !DirectCommandBindsResourceAsTargetLocked(draw_command, *source);
    if (exact_native_depth_source_surface_sample) {
      FableSurfaceTextureLink prepared_link = *link;
      prepared_link.frame_index = g_direct_renderer.current_frame_index;
      prepared_link.draw_serial = draw_serial;
      PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(prepared_link,
                                                             *source);
      RegisterDirectSurfaceTextureLinkPreparedLocked(
          prepared_link, "exact-depth-source-sample");

      static uint32_t logged_exact_native_depth_source_samples = 0;
      if (logged_exact_native_depth_source_samples < 64u) {
        REXLOG_INFO(
            "Refii plume exact depth source-surface sample #{} "
            "frame={} serial={} draw={} slot={} texture=0x{:08X} "
            "surface=0x{:08X}/{}x{} destination=0x{:08X}/{}x{} "
            "call={} context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} "
            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_native_depth_source_samples,
            g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(draw_command.serial),
            static_cast<unsigned long long>(draw_serial), slot, link->texture,
            source->guest_address, PlumeGuestTextureWidth(*source),
            PlumeGuestTextureHeight(*source), destination->guest_address,
            PlumeGuestTextureWidth(*destination),
            PlumeGuestTextureHeight(*destination),
            static_cast<unsigned long long>(link->copy_call_serial),
            static_cast<unsigned long long>(link->copy_context_key),
            link->copy_symbol_address, draw_command.resources.render_targets[0],
            draw_command.resources.depth_stencil,
            draw_command.resources.vertex_shader,
            draw_command.resources.pixel_shader);
      }
      return true;
    }

    if (source_is_depth_edge) {
      if (DirectSyntheticPrivateRectDepthActionCanExecuteLocked(
              draw_command, slot, destination)) {
        static uint32_t logged_exact_depth_surface_texture_identities = 0;
        if (logged_exact_depth_surface_texture_identities < 32u) {
          REXLOG_INFO(
              "Refii plume exact depth surface-texture identity #{} frame={} "
              "serial={} draw={} slot={} texture=0x{:08X} "
              "surface=0x{:08X}/{}x{} target=0x{:08X}/{}x{} "
              "call={} context=0x{:016X} symbol=0x{:08X} "
              "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
              ++logged_exact_depth_surface_texture_identities,
              g_direct_renderer.current_frame_index,
              static_cast<unsigned long long>(draw_command.serial),
              static_cast<unsigned long long>(draw_serial), slot, guest,
              source->guest_address, PlumeGuestTextureWidth(*source),
              PlumeGuestTextureHeight(*source), destination->guest_address,
              PlumeGuestTextureWidth(*destination),
              PlumeGuestTextureHeight(*destination),
              static_cast<unsigned long long>(link->copy_call_serial),
              static_cast<unsigned long long>(link->copy_context_key),
              link->copy_symbol_address,
              draw_command.resources.render_targets[0],
              draw_command.resources.depth_stencil,
              draw_command.resources.vertex_shader,
              draw_command.resources.pixel_shader);
        }
        return true;
      }
      const uint64_t depth_execute_cache_key =
          DirectRegisteredPrivateRectScaledDecisionCacheKey(draw_command,
                                                            *source, slot);
      if (DirectRegisteredPrivateRectExecuteDecisionCacheHit(
              depth_execute_cache_key)) {
        static uint32_t logged_exact_native_depth_edge_consumed = 0;
        if (logged_exact_native_depth_edge_consumed < 32u) {
          REXLOG_INFO(
              "Refii plume exact depth edge consumed #{} frame={} "
              "serial={} draw={} slot={} texture=0x{:08X} "
              "surface=0x{:08X}/{}x{} target=0x{:08X}/{}x{} "
              "call={} context=0x{:016X} symbol=0x{:08X} "
              "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
              ++logged_exact_native_depth_edge_consumed,
              g_direct_renderer.current_frame_index,
              static_cast<unsigned long long>(draw_command.serial),
              static_cast<unsigned long long>(draw_serial), slot, guest,
              source->guest_address, PlumeGuestTextureWidth(*source),
              PlumeGuestTextureHeight(*source), destination->guest_address,
              PlumeGuestTextureWidth(*destination),
              PlumeGuestTextureHeight(*destination),
              static_cast<unsigned long long>(link->copy_call_serial),
              static_cast<unsigned long long>(link->copy_context_key),
              link->copy_symbol_address,
              draw_command.resources.render_targets[0],
              draw_command.resources.depth_stencil,
              draw_command.resources.vertex_shader,
              draw_command.resources.pixel_shader);
        }
        return true;
      }
    } else if (DirectSyntheticPrivateRectColorActionCanExecuteLocked(
                   draw_command, slot, destination)) {
      return true;
    }

    const bool exact_same_resource_surface_edge_owned =
        private_or_title_surface_copy_command && !exact_title_upload_source &&
        link->trusted_destination_edge &&
        DirectSurfaceTextureLinkHasExactCopyIdentity(*link) &&
        DirectPrivateTextureCopyKindUsesColorBacking(link->kind) &&
        DirectSurfaceTextureLinkTouchesFablePrivateChain(*link, nullptr) &&
        link->texture != 0 && link->source_surface != 0 &&
        !DirectResourceAddressMatchesLocked(link->source_surface,
                                            link->texture) &&
        source->guest_texture.texture.get() ==
            destination->guest_texture.texture.get() &&
        !source->texture_depth_target && !destination->texture_depth_target &&
        !FableTrackedResourceIsBlankUploadedSampleProxy(*source) &&
        !DirectCommandBindsResourceAsTargetLocked(draw_command, *source) &&
        !DirectCommandBindsResourceAsTargetLocked(draw_command, *destination) &&
        (DirectResourceAddressMatchesLocked(link->texture,
                                            source->guest_address) ||
         DirectResourceAddressMatchesLocked(link->texture,
                                            destination->guest_address) ||
         DirectResourceAddressMatchesLocked(link->texture, guest));
    if (exact_same_resource_surface_edge_owned) {
      FableSurfaceTextureLink prepared_link = *link;
      prepared_link.frame_index = g_direct_renderer.current_frame_index;
      prepared_link.draw_serial = draw_serial;
      PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(prepared_link,
                                                             *source);
      PopulateDirectSurfaceTextureLinkShaderRoots(prepared_link, draw_command);
      RegisterDirectSurfaceTextureLinkPreparedLocked(
          prepared_link, "exact-same-resource-surface-edge");

      static uint32_t logged_exact_same_resource_surface_edge_owners = 0;
      if (logged_exact_same_resource_surface_edge_owners < 48u) {
        REXLOG_INFO(
            "Refii plume exact same-resource surface edge owner #{} "
            "frame={} serial={} draw={} slot={} texture=0x{:08X} "
            "surface=0x{:08X}/{}x{} destination=0x{:08X}/{}x{} call={} "
            "context=0x{:016X} symbol=0x{:08X} bucket={} rt=0x{:08X} "
            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_same_resource_surface_edge_owners,
            g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(draw_command.serial),
            static_cast<unsigned long long>(draw_serial), slot, link->texture,
            source->guest_address, PlumeGuestTextureWidth(*source),
            PlumeGuestTextureHeight(*source), destination->guest_address,
            PlumeGuestTextureWidth(*destination),
            PlumeGuestTextureHeight(*destination),
            static_cast<unsigned long long>(link->copy_call_serial),
            static_cast<unsigned long long>(link->copy_context_key),
            link->copy_symbol_address,
            selected_copy_edge_bucket ? selected_copy_edge_bucket : "unknown",
            draw_command.resources.render_targets[0],
            draw_command.resources.depth_stencil,
            draw_command.resources.vertex_shader,
            draw_command.resources.pixel_shader);
      }
      return true;
    }

	    bool exact_private_title_same_surface_sample = false;
	    const bool exact_consumed_same_surface_alias =
	        link->trusted_destination_edge &&
	        DirectSurfaceTextureLinkHasExactCopyIdentity(*link) &&
        link->texture != 0 && link->source_surface != 0 &&
        (DirectResourceAddressMatchesLocked(source->guest_address,
                                            destination->guest_address) ||
         source->guest_texture.texture.get() ==
             destination->guest_texture.texture.get()) &&
        (DirectResourceAddressMatchesLocked(link->texture,
                                            source->guest_address) ||
	         DirectResourceAddressMatchesLocked(link->texture,
	                                            destination->guest_address) ||
	         DirectResourceAddressMatchesLocked(link->texture, guest));
	    if (exact_consumed_same_surface_alias) {
	      auto same_surface_alias_proof = [&]() -> const char* {
	        if (!DirectPrivateTextureCopyKindUsesColorBacking(link->kind) ||
	            source->texture_depth_target || destination->texture_depth_target ||
	            FableTrackedResourceIsBlankUploadedSampleProxy(*source)) {
	          return nullptr;
	        }
	        const bool source_is_current_target =
	            DirectResourceAddressMatchesLocked(
	                source->guest_address,
	                draw_command.resources.render_targets[0]) ||
	            DirectResourceAddressMatchesLocked(
	                link->source_surface,
	                draw_command.resources.render_targets[0]);
	        if (link->copy_symbol_address == kFableBeginVerticesTitleWrapperSymbol &&
	            source_is_current_target &&
	            (DirectSurfaceTextureLinkIsPrivateTitleWrapperRoot(*link) ||
	             IsFableTitleSceneVertexShaderCommandLocked(draw_command) ||
	             IsFableTitleSceneTargetLocked(
	                 draw_command.resources.render_targets[0]))) {
	          return "ida-beginvertices-title-current-target";
	        }
	        if (link->copy_symbol_address ==
	                kFablePrivateRectNativeDepthWrapperSymbol &&
	            source_is_current_target) {
	          return "ida-native-depth-current-target";
	        }
	        const bool ida_private_rect_wrapper =
	            link->copy_symbol_address == kFablePrivateRectSetupWrapperSymbol ||
	            link->copy_symbol_address ==
	                kFablePrivateRectSetupFlushWrapperSymbol ||
	            link->copy_symbol_address == kFablePrivateRectCompactWrapperSymbol ||
	            link->copy_symbol_address == kFablePrivateRectSiblingWrapperSymbol ||
	            link->copy_symbol_address ==
	                kFablePrivateRectSiblingWrapperCallsite ||
	            link->copy_symbol_address ==
	                kFablePrivateRectTitleLocalWrapperCallsite ||
	            link->copy_symbol_address ==
	                kFablePrivateRectSourceObjectWrapperSymbol ||
	            link->copy_symbol_address ==
	                kFablePrivateRectBatchedIntermediateCallsite ||
	            link->copy_symbol_address ==
	                kFablePrivateRectBatchedFinalCallsite ||
	            link->copy_symbol_address ==
	                kFablePrivateRectTitleGlobalWrapperSymbol ||
	            link->copy_symbol_address ==
	                kFablePrivateRectTitleGlobalWrapperCallsite;
	        if (ida_private_rect_wrapper && source_is_current_target &&
	            DirectCommandUsesPrivateOrTitleSurfaceCopyPathLocked(
	                draw_command)) {
	          return "ida-private-rect-current-target";
	        }
	        return nullptr;
	      }();
	      const bool exact_private_title_same_surface_root =
	          private_or_title_surface_copy_command ||
	          exact_private_rect_command_root_bridge_symbol ||
	          DirectSurfaceTextureLinkIsPrivateTitleWrapperRoot(*link) ||
          IsFableTitleSceneVertexShaderCommandLocked(draw_command) ||
          IsFableTitleSceneTargetLocked(
              draw_command.resources.render_targets[0]);
      const bool exact_private_title_same_surface_chain =
          DirectSurfaceTextureLinkTouchesFablePrivateChain(*link, nullptr) ||
          exact_private_rect_command_root_bridge_symbol;
      const bool exact_title_upload_proxy_samples_gpu_surface =
          exact_title_upload_source &&
          (FableTrackedResourceHasGpuProducedContent(*source) ||
           FableTrackedResourceVisibleContentSerial(*source) != 0) &&
          !FableTrackedResourceIsBlankUploadedSampleProxy(*source) &&
          FableTrackedResourceIsBlankUploadedSampleProxy(*destination);
      const bool exact_private_title_same_surface_after_startup =
          !FableStartupVideoPumpActive() &&
          (FableStartupVideoPumpCompletedForHandoff() ||
           exact_title_upload_proxy_samples_gpu_surface);
      exact_private_title_same_surface_sample =
          exact_private_title_same_surface_after_startup &&
          exact_private_title_same_surface_root &&
          (!exact_title_upload_source ||
           exact_private_rect_command_root_bridge_symbol ||
           exact_title_upload_proxy_samples_gpu_surface) &&
          DirectPrivateTextureCopyKindUsesColorBacking(link->kind) &&
          exact_private_title_same_surface_chain &&
          !source->texture_depth_target && !destination->texture_depth_target &&
          !FableTrackedResourceIsBlankUploadedSampleProxy(*source) &&
          !DirectCommandBindsResourceAsTargetLocked(draw_command, *source) &&
          !DirectCommandBindsResourceAsTargetLocked(draw_command,
                                                   *destination);
      if (exact_private_title_same_surface_sample) {
        FableSurfaceTextureLink prepared_link = *link;
        prepared_link.frame_index = g_direct_renderer.current_frame_index;
        prepared_link.draw_serial = draw_serial;
        PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(prepared_link,
                                                               *source);
        PopulateDirectSurfaceTextureLinkShaderRoots(prepared_link,
                                                    draw_command);
        RegisterDirectSurfaceTextureLinkPreparedLocked(
            prepared_link, "exact-same-surface-sample");

        static uint32_t logged_exact_same_surface_samples = 0;
        if (logged_exact_same_surface_samples < 48u) {
          REXLOG_INFO(
              "Refii plume exact same-surface sample #{} frame={} "
              "serial={} draw={} slot={} texture=0x{:08X} "
              "surface=0x{:08X}/{}x{} destination=0x{:08X}/{}x{} "
              "kind={} call={} context=0x{:016X} symbol=0x{:08X} "
              "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
              ++logged_exact_same_surface_samples,
              g_direct_renderer.current_frame_index,
              static_cast<unsigned long long>(draw_command.serial),
              static_cast<unsigned long long>(draw_serial), slot, link->texture,
              source->guest_address, PlumeGuestTextureWidth(*source),
              PlumeGuestTextureHeight(*source), destination->guest_address,
              PlumeGuestTextureWidth(*destination),
              PlumeGuestTextureHeight(*destination),
              FablePrivateTextureCopyKindName(link->kind),
              static_cast<unsigned long long>(link->copy_call_serial),
              static_cast<unsigned long long>(link->copy_context_key),
              link->copy_symbol_address,
              draw_command.resources.render_targets[0],
              draw_command.resources.depth_stencil,
              draw_command.resources.vertex_shader,
              draw_command.resources.pixel_shader);
        }
	      }
	      if (!exact_private_title_same_surface_sample &&
	          same_surface_alias_proof) {
	        static uint32_t logged_exact_consumed_same_surface_aliases = 0;
	        if (logged_exact_consumed_same_surface_aliases < 48u) {
	          REXLOG_INFO(
	              "Refii plume exact same-surface alias consumed #{} frame={} "
	              "serial={} draw={} slot={} texture=0x{:08X} "
	              "surface=0x{:08X}/{}x{} destination=0x{:08X}/{}x{} "
	              "kind={} call={} context=0x{:016X} symbol=0x{:08X} "
	              "proof={} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
	              "ps=0x{:08X}",
	              ++logged_exact_consumed_same_surface_aliases,
	              g_direct_renderer.current_frame_index,
	              static_cast<unsigned long long>(draw_command.serial),
	              static_cast<unsigned long long>(draw_serial), slot, link->texture,
              source->guest_address, PlumeGuestTextureWidth(*source),
              PlumeGuestTextureHeight(*source), destination->guest_address,
              PlumeGuestTextureWidth(*destination),
              PlumeGuestTextureHeight(*destination),
	              FablePrivateTextureCopyKindName(link->kind),
	              static_cast<unsigned long long>(link->copy_call_serial),
	              static_cast<unsigned long long>(link->copy_context_key),
	              link->copy_symbol_address, same_surface_alias_proof,
	              draw_command.resources.render_targets[0],
	              draw_command.resources.depth_stencil,
	              draw_command.resources.vertex_shader,
              draw_command.resources.pixel_shader);
        }
        return true;
      }
    }

    if (source->guest_texture.texture.get() ==
            destination->guest_texture.texture.get() ||
        DirectCommandBindsResourceAsTargetLocked(draw_command, *destination) ||
        (!exact_surface_copy_alias_link_selected &&
         !exact_wrapper_link_selected &&
         !exact_private_title_same_surface_sample &&
         !DirectCommandBindsResourceAsTargetLocked(draw_command, *source))) {
      log_exact_surface_copy_miss("invalid_endpoint", link, source,
                                  destination);
      return false;
    }

    if (source_is_depth_edge) {
      if (destination->texture_depth_target) {
        log_exact_surface_copy_miss("depth_edge_no_execute", link, source,
                                    destination);
        return false;
      }
    }

    const bool exact_scaled_native_depth_copy_action =
        source_is_depth_edge && slot == source_slot &&
        !DirectTextureCopySizeMatches(*source, *destination) &&
        link->trusted_destination_edge &&
        DirectSurfaceTextureLinkHasExactCopyIdentity(*link) &&
        destination->texture_render_target &&
        !destination->texture_depth_target;

    bool copied = false;
    if (!source_is_depth_edge &&
        DirectTextureCopySizeMatches(*source, *destination)) {
      copied = RecordDirectTextureCopyLocked(command_list, *source,
                                             *destination);
    } else {
      DirectFramebufferBinding destination_framebuffer;
      FablePrivateTextureCopyAction scaled_action;
      scaled_action.source_slot = slot;
      scaled_action.source_texture = guest;
      scaled_action.source_base_address = PlumeGuestTextureBaseAddress(*source);
      scaled_action.source_width = PlumeGuestTextureWidth(*source);
      scaled_action.source_height = PlumeGuestTextureHeight(*source);
      scaled_action.source_format = PlumeGuestTextureGuestFormat(*source);
      scaled_action.source_fetch_dwords =
          HasTrackedFetchDwords(command_fetch)
              ? command_fetch
              : PlumeGuestTextureFetchDwords(*source);
      PopulateDirectPrivateTextureCopyTargetLocked(scaled_action,
                                                   *destination);
      scaled_action.render_target = draw_command.resources.render_targets[0];
      scaled_action.depth_stencil = draw_command.resources.depth_stencil;
      scaled_action.vertex_shader = draw_command.resources.vertex_shader;
      scaled_action.pixel_shader = draw_command.resources.pixel_shader;
      PopulateDirectPrivateTextureCopyShaderRoots(scaled_action,
                                                  draw_command);
      scaled_action.lr = draw_command.draw.state_flush_lr;
      scaled_action.frame_index = g_direct_renderer.current_frame_index;
      scaled_action.draw_serial = draw_serial;
      scaled_action.kind = source_is_depth_edge
                               ? FablePrivateTextureCopyKind::kDepth
                               : (destination->texture_depth_target
                                      ? FablePrivateTextureCopyKind::kDepth
                                      : FablePrivateTextureCopyKind::kColor);
      scaled_action.uv_override = true;
      scaled_action.u0 = 0.0f;
      scaled_action.v0 = 0.0f;
      scaled_action.u1 = 1.0f;
      scaled_action.v1 = 1.0f;
      scaled_action.rectangle_valid = false;
      if ((!destination->texture_render_target ||
           !destination->guest_surface.attachment_view) &&
          link->trusted_destination_edge) {
        PromoteDirectExactCopyDestinationToColorTargetLocked(
            device, *destination, *link);
      }
      if (destination->texture_render_target &&
          !destination->texture_depth_target &&
          ResolveDirectSingleColorFramebufferForResourceLocked(
              device, *destination, destination_framebuffer)) {
        copied = RecordDirectScaledPrivateRectCopyLocked(
            command_list, draw_command, destination_framebuffer, scaled_action,
            *source, *destination, slot,
            exact_scaled_native_depth_copy_action
                ? "exact-scaled-depth-copy"
                : "exact-surface-copy");
      } else {
        log_exact_surface_copy_miss("scaled_destination_not_rt", link, source,
                                    destination);
      }
    }
    if (!copied) {
      log_exact_surface_copy_miss("copy_failed", link, source, destination);
      return false;
    }

    FableSurfaceTextureLink flushed_link = *link;
    PopulateDirectSurfaceTextureLinkBackingLocked(flushed_link, *destination);
    flushed_link.frame_index = g_direct_renderer.current_frame_index;
    flushed_link.draw_serial = draw_serial;
    RegisterDirectSurfaceTextureLinkPreparedLocked(flushed_link,
                                                  exact_scaled_native_depth_copy_action
                                                      ? "exact-scaled-depth-copy"
                                                      : "flushed-exact-copy");

    if (exact_scaled_native_depth_copy_action) {
      static uint32_t
          logged_exact_scaled_native_depth_texture_identities = 0;
      if (logged_exact_scaled_native_depth_texture_identities < 64u) {
        REXLOG_INFO(
            "Refii plume exact scaled current-target depth surface-texture identity #{} "
            "frame={} serial={} draw={} slot={} texture=0x{:08X} "
            "surface=0x{:08X}/{}x{} target=0x{:08X}/{}x{} call={} "
            "context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} ds=0x{:08X} "
            "vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_scaled_native_depth_texture_identities,
            g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(draw_command.serial),
            static_cast<unsigned long long>(draw_serial), slot, link->texture,
            source->guest_address, PlumeGuestTextureWidth(*source),
            PlumeGuestTextureHeight(*source), destination->guest_address,
            PlumeGuestTextureWidth(*destination),
            PlumeGuestTextureHeight(*destination),
            static_cast<unsigned long long>(link->copy_call_serial),
            static_cast<unsigned long long>(link->copy_context_key),
            link->copy_symbol_address,
            draw_command.resources.render_targets[0],
            draw_command.resources.depth_stencil,
            draw_command.resources.vertex_shader,
            draw_command.resources.pixel_shader);
      }
    } else {
      static uint32_t logged_exact_surface_copy_executions = 0;
      if (logged_exact_surface_copy_executions < 64u) {
        REXLOG_INFO(
            "Refii plume exact surface copy executed #{} frame={} "
            "serial={} draw={} slot={} source=0x{:08X}/{}x{} "
            "destination=0x{:08X}/{}x{} texture=0x{:08X} rt=0x{:08X} "
            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_surface_copy_executions,
            g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(draw_command.serial),
            static_cast<unsigned long long>(draw_serial), slot,
            source->guest_address, PlumeGuestTextureWidth(*source),
            PlumeGuestTextureHeight(*source), destination->guest_address,
            PlumeGuestTextureWidth(*destination),
            PlumeGuestTextureHeight(*destination), link->texture,
            draw_command.resources.render_targets[0],
            draw_command.resources.depth_stencil,
            draw_command.resources.vertex_shader,
            draw_command.resources.pixel_shader);
      }
    }
    return true;
}

#endif

}  // namespace refii::renderer
