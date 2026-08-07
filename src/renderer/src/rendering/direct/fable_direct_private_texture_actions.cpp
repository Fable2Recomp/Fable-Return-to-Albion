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

bool DirectPrivateTextureCopyActionContextMatches(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action) {
  const uint32_t current_rt = command.resources.render_targets[0];
  const uint32_t current_ds = command.resources.depth_stencil;
  auto command_binds_private_title_header = [&]() {
    if (command.draw.source == RendererDrawEventSource::kPixVideo) {
      return false;
    }
    for (uint32_t texture : command.resources.textures) {
      if (IsFableMutablePrivateTextureHeaderAddress(texture) ||
          IsFableTitlePrivateTextureProxyAddress(texture)) {
        return true;
      }
    }
    return false;
  };
  const bool title_scene_sample =
      command.draw.source == RendererDrawEventSource::kSceneObject &&
      (IsFableTitleSceneTargetLocked(current_rt) ||
       IsFableTitleSceneVertexShaderCommandLocked(command) ||
       command_binds_private_title_header());
  const bool command_samples_action_source =
      DirectPrivateTextureCopyActionCommandSlotMatches(command, action);
  const bool private_rect_action_sample =
      command.draw.source == RendererDrawEventSource::kPrivateTextureRectangle ||
      command.draw.private_texture_rectangle ||
      IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader);
  const bool title_private_scale_up_copy =
      DirectPrivateTextureCopyActionIsTitleScaleUpLocked(command, action);
  const bool title_private_scale_down_copy =
      DirectPrivateTextureCopyActionIsTitleScaleDownLocked(command, action);
  const PlumeTrackedResource* current_rt_resource =
      FindDirectResourceLocked(current_rt);
  const bool current_rt_private_chain =
      current_rt_resource &&
      IsFablePrivateTextureChainResource(*current_rt_resource);
  if (action.pixel_shader != 0 &&
      IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader) &&
      !DirectPrivateRectSyntheticPixelShadersCompatible(
          command.resources.pixel_shader, action.pixel_shader, action.kind) &&
      !DirectPixelShaderObjectsEquivalentLocked(command.resources.pixel_shader,
                                                action.pixel_shader)) {
    return false;
  }
  if (action.vertex_shader != 0 &&
      command.draw.private_texture_rectangle &&
      !DirectVertexShaderObjectsEquivalentLocked(command.resources.vertex_shader,
                                                 action.vertex_shader)) {
    return false;
  }
  if (private_rect_action_sample && action.draw_serial != 0 &&
      command.draw_serial != 0 && action.draw_serial != command.draw_serial) {
    return false;
  }
  auto color_action_targets_current_rt = [&]() {
    if (current_rt == 0) {
      return false;
    }
    return (action.render_target != 0 &&
            DirectResourceAddressMatchesLocked(action.render_target,
                                               current_rt)) ||
           (action.target_surface != 0 &&
            DirectResourceAddressMatchesLocked(action.target_surface,
                                               current_rt));
  };
  if (title_scene_sample && !private_rect_action_sample &&
      (action.kind == FablePrivateTextureCopyKind::kColor ||
       action.kind == FablePrivateTextureCopyKind::kColorSibling) &&
      action.target_surface != 0 && !color_action_targets_current_rt()) {
    return false;
  }
  if (title_scene_sample && !private_rect_action_sample &&
      (action.kind == FablePrivateTextureCopyKind::kColor ||
       action.kind == FablePrivateTextureCopyKind::kColorSibling) &&
      action.render_target != 0 &&
      !DirectResourceAddressMatchesLocked(action.render_target, current_rt) &&
      action.target_surface != 0 &&
      DirectResourceAddressMatchesLocked(action.target_surface, current_rt) &&
      DirectPrivateTextureCopyActionShaderRootsSupersededLocked(action,
                                                                command)) {
    return false;
  }
  if (!DirectPrivateTextureCopyActionShapeMatches(command, action)) {
    if (g_direct_renderer.diagnostics.logged_private_rect_shape_mismatch_rejects < 16u) {
      float command_rect_width = 0.0f;
      float command_rect_height = 0.0f;
      if (command.draw.private_texture_corners_valid) {
        const auto& corners = command.draw.private_texture_corners;
        command_rect_width = std::fabs(corners[2] - corners[0]);
        command_rect_height = std::fabs(corners[5] - corners[1]);
      }
      const PlumeTrackedResource* action_target =
          FindDirectResourceLocked(action.target_surface);
      REXLOG_INFO(
          "Refii plume exact private copy action shape mismatch reject #{} "
          "frame={} command_draw={} action_draw={} source=0x{:08X} "
          "target=0x{:08X} command_rect={:.2f}x{:.2f} "
          "action_rect={}x{} target_extent={}x{} action_src={},{} "
          "sampled={} sample=0x{:08X} rt=0x{:08X} ds=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X} action_vs=0x{:08X} action_ps=0x{:08X}",
          ++g_direct_renderer.diagnostics.logged_private_rect_shape_mismatch_rejects,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.draw_serial),
          static_cast<unsigned long long>(action.draw_serial),
          action.source_texture, action.target_surface, command_rect_width,
          command_rect_height, action.rect_width, action.rect_height,
          action_target ? PlumeGuestTextureWidth(*action_target) : 0,
          action_target ? PlumeGuestTextureHeight(*action_target) : 0,
          action.source_x, action.source_y,
          FableRegisteredSampleSourceKindName(action.sampled_source.kind),
          action.sampled_source.resource, current_rt, current_ds,
          command.resources.vertex_shader, command.resources.pixel_shader,
          action.vertex_shader, action.pixel_shader);
    }
    return false;
  }

  auto log_exact_title_target_cross_alias_reject = [&]() {
    if (action.target_surface == 0) {
      return;
    }
    PlumeTrackedResource* target =
        FindDirectResourceLocked(action.target_surface);
    if (!target || !PlumeGuestImageReady(*target) ||
        !FableTrackedResourceHasSampleableContent(*target)) {
      return;
    }
    const bool title_private_intermediate =
        title_scene_sample && command_samples_action_source &&
        IsFablePrivateTextureChainResource(*target) &&
        !DirectCommandBindsResourceAsTargetLocked(command, *target);
    if (DirectResourceIsTrackedSceneOutputLocked(*target) ||
        (IsFableTitleSceneTargetLocked(action.target_surface) &&
         !title_private_intermediate)) {
      return;
    }
    if (IsFablePrivateTextureChainResource(*target)) {
      static uint32_t logged_exact_title_target_cross_alias_rejects = 0;
      if (logged_exact_title_target_cross_alias_rejects < 32u) {
        REXLOG_INFO(
            "Refii plume exact title target cross-alias reject #{} frame={} "
            "kind={} source=0x{:08X} target=0x{:08X} action_rt=0x{:08X} "
            "rt=0x{:08X} ds=0x{:08X} private_chain={} title_sample={} "
            "vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_title_target_cross_alias_rejects,
            g_direct_renderer.current_frame_index,
            FablePrivateTextureCopyKindName(action.kind),
            action.source_texture, action.target_surface, action.render_target,
            current_rt, current_ds, current_rt_private_chain ? 1 : 0,
            title_scene_sample ? 1 : 0, command.resources.vertex_shader,
            command.resources.pixel_shader);
      }
    }
  };

  switch (action.kind) {
    case FablePrivateTextureCopyKind::kDepth: {
      const bool command_targets_action_depth =
          DirectSyntheticPrivateRectCommandUsesDepthTarget(command) &&
          command_samples_action_source &&
          action.depth_stencil != 0 &&
          current_ds == action.depth_stencil &&
          (action.render_target == 0 || current_rt == action.render_target) &&
          (DirectResourceAddressMatchesLocked(action.target_surface,
                                              current_ds) ||
           DirectResourceAddressMatchesLocked(action.depth_stencil,
                                              current_ds));
      if (command_targets_action_depth) {
        return true;
      }

      if (private_rect_action_sample &&
          IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader)) {
        if (g_direct_renderer.diagnostics.logged_private_rect_static_context_rejects < 24u) {
          REXLOG_INFO(
              "Refii plume exact private copy action depth/color role reject #{} "
              "frame={} source=0x{:08X} target=0x{:08X} action_rt=0x{:08X} "
              "action_ds=0x{:08X} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
              "ps=0x{:08X}",
              ++g_direct_renderer.diagnostics.logged_private_rect_static_context_rejects,
              g_direct_renderer.current_frame_index, action.source_texture,
              action.target_surface, action.render_target, action.depth_stencil,
              current_rt, current_ds, command.resources.vertex_shader,
              command.resources.pixel_shader);
        }
        return false;
      }

      const bool bound_depth_source =
          action.depth_stencil != 0 && current_ds == action.depth_stencil &&
          (action.render_target == 0 || current_rt == action.render_target);
      if (bound_depth_source) {
        return true;
      }
      const PlumeTrackedResource* action_source_resource =
          FindDirectResourceLocked(action.source_texture);
      const bool action_source_private_chain =
          IsFableTitlePrivateTextureProxyAddress(action.source_texture) ||
          IsFablePrivateTextureCopyProxyAddress(action.source_texture) ||
          (action_source_resource &&
           IsFablePrivateTextureChainResource(*action_source_resource));
      const bool exact_depth_family_alias_source =
          action.depth_stencil != 0 &&
          action.target_surface == action.depth_stencil &&
          command_samples_action_source && action_source_private_chain;
      if (action.draw_serial != 0 && exact_depth_family_alias_source &&
          (command.draw.source == RendererDrawEventSource::kSceneObject ||
           private_rect_action_sample) &&
          (title_scene_sample || current_rt == 0 || current_rt_private_chain ||
           private_rect_action_sample)) {
        if (action.frame_index == 0) {
          if (g_direct_renderer.diagnostics.logged_private_rect_static_context_rejects < 24u) {
            REXLOG_INFO(
                "Refii plume exact private copy action static-context reject #{} "
                "frame={} kind={} source=0x{:08X} target=0x{:08X} "
                "action_rt=0x{:08X} action_ds=0x{:08X} rt=0x{:08X} "
                "ds=0x{:08X} private_chain={} private_rect={} "
                "title_sample={} vs=0x{:08X} ps=0x{:08X}",
                ++g_direct_renderer.diagnostics.logged_private_rect_static_context_rejects,
                g_direct_renderer.current_frame_index,
                FablePrivateTextureCopyKindName(action.kind),
                action.source_texture, action.target_surface,
                action.render_target, action.depth_stencil, current_rt,
                current_ds, current_rt_private_chain, private_rect_action_sample,
                title_scene_sample, command.resources.vertex_shader,
                command.resources.pixel_shader);
          }
          return false;
        }
        return true;
      }
      // Fable registers this private-rectangle depth family before later title
      // composite passes sample the same header while rendering into smaller
      // intermediates. Treat the call/context-owned edge as exact.
      if (action.frame_index == 0 && action.draw_serial == 0 &&
          exact_depth_family_alias_source &&
          command.draw.source == RendererDrawEventSource::kSceneObject) {
        if (g_direct_renderer.diagnostics.logged_private_rect_static_context_rejects < 24u) {
          REXLOG_INFO(
              "Refii plume exact private copy action static-context reject #{} "
              "frame={} kind={} source=0x{:08X} target=0x{:08X} "
              "action_rt=0x{:08X} action_ds=0x{:08X} rt=0x{:08X} "
              "ds=0x{:08X} private_chain={} private_rect={} "
              "title_sample={} vs=0x{:08X} ps=0x{:08X}",
              ++g_direct_renderer.diagnostics.logged_private_rect_static_context_rejects,
              g_direct_renderer.current_frame_index,
              FablePrivateTextureCopyKindName(action.kind),
              action.source_texture, action.target_surface,
              action.render_target, action.depth_stencil, current_rt,
              current_ds, current_rt_private_chain, private_rect_action_sample,
              title_scene_sample, command.resources.vertex_shader,
              command.resources.pixel_shader);
        }
      }
      return false;
    }
    case FablePrivateTextureCopyKind::kColor:
      if (action.render_target != 0 && current_rt == action.render_target) {
        return true;
      }
      if ((title_private_scale_up_copy || title_private_scale_down_copy) &&
          private_rect_action_sample &&
          command_samples_action_source) {
        return true;
      }
      if (action.frame_index == 0 && action.draw_serial == 0) {
        return false;
      }
      if (title_scene_sample) {
        log_exact_title_target_cross_alias_reject();
      }
      return false;
    case FablePrivateTextureCopyKind::kColorSibling: {
      if (action.render_target != 0 && current_rt == action.render_target) {
        return true;
      }
      if (action.draw_serial != 0 && action.source_base_address == 0 &&
          action.target_surface != 0 &&
          command.draw.source == RendererDrawEventSource::kSceneObject &&
          command_samples_action_source &&
          IsFableTitlePrivateTextureProxyAddress(action.source_texture) &&
          (title_scene_sample || current_rt == 0 || current_rt_private_chain ||
           IsFableTitleSceneTargetLocked(action.render_target))) {
        if (action.frame_index == 0) {
          if (g_direct_renderer.diagnostics.logged_private_rect_static_context_rejects < 24u) {
            REXLOG_INFO(
                "Refii plume exact private copy action static-context reject #{} "
                "frame={} kind={} source=0x{:08X} target=0x{:08X} "
                "action_rt=0x{:08X} action_ds=0x{:08X} rt=0x{:08X} "
                "ds=0x{:08X} private_chain={} private_rect={} "
                "title_sample={} vs=0x{:08X} ps=0x{:08X}",
                ++g_direct_renderer.diagnostics.logged_private_rect_static_context_rejects,
                g_direct_renderer.current_frame_index,
                FablePrivateTextureCopyKindName(action.kind),
                action.source_texture, action.target_surface,
                action.render_target, action.depth_stencil, current_rt,
                current_ds, current_rt_private_chain, private_rect_action_sample,
                title_scene_sample, command.resources.vertex_shader,
                command.resources.pixel_shader);
          }
          return false;
        }
        return true;
      }
      if (action.frame_index == 0 && action.draw_serial == 0 &&
          action.target_surface != 0 &&
          command.draw.source == RendererDrawEventSource::kSceneObject &&
          command_samples_action_source &&
          IsFableTitlePrivateTextureProxyAddress(action.source_texture)) {
        if (g_direct_renderer.diagnostics.logged_private_rect_static_context_rejects < 24u) {
          REXLOG_INFO(
              "Refii plume exact private copy action static-context reject #{} "
              "frame={} kind={} source=0x{:08X} target=0x{:08X} "
              "action_rt=0x{:08X} action_ds=0x{:08X} rt=0x{:08X} "
              "ds=0x{:08X} private_chain={} private_rect={} "
              "title_sample={} vs=0x{:08X} ps=0x{:08X}",
              ++g_direct_renderer.diagnostics.logged_private_rect_static_context_rejects,
              g_direct_renderer.current_frame_index,
              FablePrivateTextureCopyKindName(action.kind),
              action.source_texture, action.target_surface,
              action.render_target, action.depth_stencil, current_rt,
              current_ds, current_rt_private_chain, private_rect_action_sample,
              title_scene_sample, command.resources.vertex_shader,
              command.resources.pixel_shader);
        }
        return false;
      }
      if (action.vertex_shader != 0 &&
          !DirectVertexShaderObjectsEquivalentLocked(
              command.resources.vertex_shader, action.vertex_shader)) {
        return false;
      }
      if (action.pixel_shader != 0 &&
          !DirectPixelShaderObjectsEquivalentLocked(command.resources.pixel_shader,
                                                    action.pixel_shader)) {
        return false;
      }
      if ((title_private_scale_up_copy || title_private_scale_down_copy) &&
          private_rect_action_sample &&
          command_samples_action_source) {
        return true;
      }
      if (title_scene_sample && action.frame_index != 0 &&
          (action.vertex_shader != 0 || action.pixel_shader != 0)) {
        log_exact_title_target_cross_alias_reject();
      }
      return false;
    }
  }
  return false;
}


const FablePrivateTextureCopyAction*
FindDirectPrivateRectMaterializationActionLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* source, const PlumeTrackedResource& target) {
  const bool private_rect_copy =
      command.draw.private_texture_rectangle &&
      IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader);
  const bool title_scene_feedback =
      command.draw.source == RendererDrawEventSource::kSceneObject &&
      (IsFableTitleSceneVertexShaderCommandLocked(command) ||
       IsFableTitleSceneTargetLocked(command.resources.render_targets[0]));
  if (slot >= command.resources.textures.size() ||
      (!private_rect_copy && !title_scene_feedback) ||
      !DirectCommandBindsResourceAsTargetLocked(command, target)) {
    return nullptr;
  }

  const uint32_t guest = command.resources.textures[slot];
  auto native_private_proxy_address = [](uint32_t address) {
    return IsFableTitlePrivateTextureProxyAddress(address) ||
           IsFableMutablePrivateTextureHeaderAddress(address) ||
           FablePrivateTextureProxyRoleForAddress(address) !=
               FablePrivateTextureProxyRole::kUnknown;
  };
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  const bool native_private_proxy_source =
      native_private_proxy_address(guest) ||
      (command_fetch_info.valid &&
       native_private_proxy_address(command_fetch_info.base_address)) ||
      (source &&
       (native_private_proxy_address(source->guest_address) ||
        native_private_proxy_address(source->parent_address) ||
        native_private_proxy_address(PlumeGuestTextureBaseAddress(*source)) ||
        IsFablePrivateTextureChainResource(*source)));
  if (title_scene_feedback && !private_rect_copy &&
      native_private_proxy_source && target.texture_render_target &&
      DirectResourceAddressMatchesLocked(target.guest_address,
                                         command.resources.render_targets[0])) {
    return nullptr;
  }
  if (source) {
    if (const FablePrivateTextureCopyAction* exact_action =
            FindDirectPrivateRectExactActionLocked(command, slot, *source,
                                                   target)) {
      return exact_action;
    }
  }

  auto action_targets_current_resource =
      [&](const FablePrivateTextureCopyAction& action) {
        return DirectResourceAddressMatchesLocked(action.target_surface,
                                                  target.guest_address) ||
               DirectResourceAddressMatchesLocked(action.render_target,
                                                  target.guest_address) ||
               DirectResourceAddressMatchesLocked(
                   action.depth_stencil, command.resources.depth_stencil);
      };
  auto action_source_matches_slot =
      [&](const FablePrivateTextureCopyAction& action) {
        constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
        if (action.source_slot != kUnknownSlot && action.source_slot != slot) {
          return false;
        }
        if (DirectResourceAddressMatchesLocked(action.source_texture, guest)) {
          return true;
        }
        if (DirectResourceAddressMatchesLocked(action.backing_texture, guest)) {
          return true;
        }
        if (source) {
          if (DirectResourceAddressMatchesLocked(action.source_texture,
                                                source->guest_address) ||
              DirectResourceAddressMatchesLocked(action.source_texture,
                                                source->parent_address) ||
              DirectResourceAddressMatchesLocked(action.backing_texture,
                                                source->guest_address) ||
              DirectResourceAddressMatchesLocked(action.backing_texture,
                                                source->parent_address)) {
            return true;
          }
        }
        return false;
      };
  auto current_target_surface_link_owns_slot =
      [&]() -> const FableSurfaceTextureLink* {
    if (!title_scene_feedback ||
        command.draw.source == RendererDrawEventSource::kPixVideo) {
      return nullptr;
    }

    const std::array<uint32_t, 6> command_fetch =
        DirectCommandTextureFetchDwords(command, slot);
    const DirectTextureFetchInfo command_fetch_info =
        DirectTextureFetchInfoFromDwords(command_fetch);
    const bool private_title_identity =
        IsFableTitlePrivateTextureProxyAddress(guest) ||
        IsFableMutablePrivateTextureHeaderAddress(guest) ||
        FablePrivateTextureProxyRoleForAddress(guest) !=
            FablePrivateTextureProxyRole::kUnknown ||
        (command_fetch_info.valid &&
         (IsFableTitlePrivateTextureProxyAddress(
              command_fetch_info.base_address) ||
          IsFableMutablePrivateTextureHeaderAddress(
              command_fetch_info.base_address) ||
          FablePrivateTextureProxyRoleForAddress(
              command_fetch_info.base_address) !=
              FablePrivateTextureProxyRole::kUnknown)) ||
        (source &&
         (IsFableTitlePrivateTextureProxyAddress(source->guest_address) ||
          IsFableMutablePrivateTextureHeaderAddress(source->guest_address) ||
          IsFableTitlePrivateTextureProxyAddress(source->parent_address) ||
          IsFableMutablePrivateTextureHeaderAddress(source->parent_address) ||
          IsFablePrivateTextureChainResource(*source) ||
          FablePrivateTextureProxyRoleForAddress(source->guest_address) !=
              FablePrivateTextureProxyRole::kUnknown ||
          FablePrivateTextureProxyRoleForAddress(source->parent_address) !=
              FablePrivateTextureProxyRole::kUnknown ||
          FablePrivateTextureProxyRoleForAddress(
              PlumeGuestTextureBaseAddress(*source)) !=
              FablePrivateTextureProxyRole::kUnknown));
    if (!private_title_identity) {
      return nullptr;
    }

    const uint64_t command_draw_serial =
        command.draw_serial != 0 ? command.draw_serial : command.serial;
    auto copy_identity_matches_command =
        [&](const FableSurfaceTextureLink& link) {
          bool matched = false;
          if (command.draw.private_texture_wrapper_symbol_address != 0 &&
              link.copy_symbol_address != 0) {
            if (!FablePrivateRectWrapperSymbolsCompatible(
                    command.draw.private_texture_wrapper_symbol_address,
                    link.copy_symbol_address)) {
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
          link->source_surface == 0 ||
          !DirectPrivateTextureCopyKindUsesColorBacking(link->kind)) {
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
      const bool current_target_source =
          DirectResourceAddressMatchesCommandTargetLocked(
              command, link->source_surface) ||
          DirectResourceAddressMatchesCommandTargetLocked(command,
                                                          link->render_target) ||
          DirectSurfaceTextureLinkIsCurrentTargetExactEdgeLocked(command,
                                                                       *link);
      if (!current_target_source) {
        return false;
      }
      constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
      return link->source_slot == kUnknownSlot || link->source_slot == slot ||
             DirectSurfaceTextureLinkTouchesFablePrivateChain(*link, source) ||
             copy_identity_matches_command(*link);
    };
    auto link_identity_matches = [&](const FableSurfaceTextureLink* link) {
      if (!link_visible(link)) {
        return false;
      }
      if (DirectResourceAddressMatchesLocked(link->texture, guest) ||
          DirectResourceAddressMatchesLocked(link->backing_texture, guest)) {
        return true;
      }
      if (command_fetch_info.valid &&
          (DirectResourceAddressMatchesLocked(link->texture_base_address,
                                              command_fetch_info.base_address) ||
           DirectResourceAddressMatchesLocked(link->backing_base_address,
                                              command_fetch_info.base_address))) {
        return true;
      }
      if (!source) {
        return false;
      }
      return DirectResourceAddressMatchesLocked(link->texture,
                                                source->guest_address) ||
             DirectResourceAddressMatchesLocked(link->texture,
                                                source->parent_address) ||
             DirectResourceAddressMatchesLocked(link->backing_texture,
                                                source->guest_address) ||
             DirectResourceAddressMatchesLocked(link->backing_texture,
                                                source->parent_address) ||
             DirectResourceAddressMatchesLocked(
                 link->texture_base_address,
                 PlumeGuestTextureBaseAddress(*source)) ||
             DirectResourceAddressMatchesLocked(
                 link->backing_base_address,
                 PlumeGuestTextureBaseAddress(*source));
    };
    auto find_link_for_destination =
        [&](uint32_t destination) -> const FableSurfaceTextureLink* {
      if (destination == 0) {
        return nullptr;
      }
      const FableSurfaceTextureLink* link =
          FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
              destination, g_direct_renderer.current_frame_index,
              command_draw_serial, slot);
      if (!link_identity_matches(link)) {
        link = FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
            destination, slot);
      }
      if (!link_identity_matches(link)) {
        link = FableDirectFindSurfaceTextureLinkForDestinationTexture(
            destination);
      }
      return link_identity_matches(link) ? link : nullptr;
    };

    const FableSurfaceTextureLink* link = find_link_for_destination(guest);
    if (!link && source) {
      link = find_link_for_destination(source->guest_address);
    }
    if (!link && source) {
      link = find_link_for_destination(source->parent_address);
    }
    if (!link) {
      return nullptr;
    }

    const PlumeTrackedResource* link_source =
        FindDirectResourceLocked(link->source_surface);
    const PlumeTrackedResource* link_destination =
        FindDirectResourceLocked(link->texture);
    const bool source_ready =
        link_source && IsImageResource(link_source->kind) &&
        PlumeGuestImageReady(*link_source);
    const bool destination_ready =
        link_destination && IsImageResource(link_destination->kind) &&
        PlumeGuestImageReady(*link_destination);
    const bool exact_private_title_destination =
        private_title_identity &&
        (DirectResourceAddressMatchesLocked(link->texture, guest) ||
         DirectResourceAddressMatchesLocked(link->backing_texture, guest) ||
         IsFableMutablePrivateTextureHeaderAddress(link->texture) ||
         IsFableTitlePrivateTextureProxyAddress(link->texture) ||
         FablePrivateTextureProxyRoleForAddress(link->texture) !=
             FablePrivateTextureProxyRole::kUnknown);
    if (!source_ready ||
        (!destination_ready && !exact_private_title_destination)) {
      return nullptr;
    }
    return link;
  };
  auto action_matches = [&](const FablePrivateTextureCopyAction& action) {
    if (action.kind == FablePrivateTextureCopyKind::kDepth) {
      if (!private_rect_copy ||
          !DirectSyntheticPrivateRectCommandUsesDepthTarget(command) ||
          !action_targets_current_resource(action) ||
          !action_source_matches_slot(action)) {
        return false;
      }
      if (action.pixel_shader != 0 &&
          !DirectPrivateRectSyntheticPixelShadersCompatible(
              command.resources.pixel_shader, action.pixel_shader,
              action.kind) &&
          !DirectPixelShaderObjectsEquivalentLocked(
              command.resources.pixel_shader, action.pixel_shader)) {
        return false;
      }
      if (action.vertex_shader != 0 &&
          !DirectVertexShaderObjectsEquivalentLocked(
              command.resources.vertex_shader, action.vertex_shader)) {
        return false;
      }
      return true;
    }
    if (action.kind != FablePrivateTextureCopyKind::kColor &&
        action.kind != FablePrivateTextureCopyKind::kColorSibling) {
      return false;
    }
    const uint64_t command_draw_serial =
        command.draw_serial != 0 ? command.draw_serial : command.serial;
    const bool same_registered_draw =
        action.draw_serial != 0 && action.draw_serial == command_draw_serial;
    if (!action_targets_current_resource(action) ||
        !action_source_matches_slot(action)) {
      return false;
    }
    if (same_registered_draw ||
        DirectPrivateTextureCopyActionContextMatches(command, action)) {
      return true;
    }
    // The surface/texture copy edge is independent of the shader pair
    // that later consumes it. Fable's title path materializes a private
    // texture with a copy shader, then samples the same source/target
    // edge in scene draws with a different title shader. Treat that
    // exact edge like Unleashed/06 sourceSurface ownership instead of
    // forcing a draw-time backfill just to rewrite the shader context.
    const bool materialized_texture_edge =
        title_scene_feedback && action.frame_index != 0 &&
        action.target_surface != 0 &&
        (action.source_texture != 0 ||
         HasTrackedFetchDwords(action.source_fetch_dwords) ||
         action.backing_texture != 0 ||
         HasTrackedFetchDwords(action.backing_fetch_dwords));
    if (!materialized_texture_edge) {
      return false;
    }
    if (const FableSurfaceTextureLink* current_link =
            current_target_surface_link_owns_slot()) {
      if (g_direct_renderer
              .diagnostics.logged_private_rect_materialized_current_link_rejections < 16u) {
        REXLOG_INFO(
            "Refii plume rejected materialized private rect edge because "
            "current-link owns slot #{} frame={} slot={} source=0x{:08X} "
            "backing=0x{:08X} "
            "target=0x{:08X} link_texture=0x{:08X} link_source=0x{:08X} "
            "link_kind={} link_call={} link_context=0x{:016X} "
            "action_vs=0x{:08X} action_ps=0x{:08X} "
            "action_vs_root=0x{:08X}/{} action_ps_root=0x{:08X}/{} "
            "cmd_vs=0x{:08X} cmd_ps=0x{:08X} "
            "cmd_vs_root=0x{:08X}/{} cmd_ps_root=0x{:08X}/{}",
            ++g_direct_renderer
                  .diagnostics.logged_private_rect_materialized_current_link_rejections,
            g_direct_renderer.current_frame_index, slot, action.source_texture,
            action.backing_texture, action.target_surface, current_link->texture,
            current_link->source_surface,
            static_cast<uint32_t>(current_link->kind),
            static_cast<unsigned long long>(current_link->copy_call_serial),
            static_cast<unsigned long long>(current_link->copy_context_key),
            action.vertex_shader, action.pixel_shader,
            action.vertex_shader_root_symbol_address,
            static_cast<unsigned long long>(
                action.vertex_shader_root_call_serial),
            action.pixel_shader_root_symbol_address,
            static_cast<unsigned long long>(
                action.pixel_shader_root_call_serial),
            command.resources.vertex_shader, command.resources.pixel_shader,
            command.resources.shader_roots.vertex_shader.symbol_address,
            static_cast<unsigned long long>(
                command.resources.shader_roots.vertex_shader.call_serial),
            command.resources.shader_roots.pixel_shader.symbol_address,
            static_cast<unsigned long long>(
                command.resources.shader_roots.pixel_shader.call_serial));
      }
      return false;
    }
    auto native_private_proxy = [](uint32_t address) {
      return FablePrivateTextureProxyRoleForAddress(address) !=
             FablePrivateTextureProxyRole::kUnknown;
    };
    const bool superseded_or_unkeyed_shader_root =
        DirectPrivateTextureCopyActionShaderRootsSupersededLocked(action,
                                                                  command);
    const bool native_proxy_source =
        native_private_proxy(guest) ||
        native_private_proxy(action.source_texture) ||
        native_private_proxy(action.backing_texture) ||
        (source && (native_private_proxy(source->guest_address) ||
                    native_private_proxy(source->parent_address)));
    const bool current_title_color_feedback =
        superseded_or_unkeyed_shader_root && native_proxy_source &&
        title_scene_feedback &&
        target.texture_render_target &&
        DirectResourceAddressMatchesLocked(target.guest_address,
                                           command.resources.render_targets[0]);
    if (current_title_color_feedback) {
      if (g_direct_renderer
              .diagnostics.logged_current_title_color_feedback_materialized_edges < 16u) {
        REXLOG_INFO(
            "Refii plume exact current title color feedback materialized "
            "private rect edge #{} "
            "frame={} slot={} source=0x{:08X} backing=0x{:08X} "
            "target=0x{:08X} action_vs=0x{:08X} action_ps=0x{:08X} "
            "action_vs_root=0x{:08X}/{} action_ps_root=0x{:08X}/{} "
            "cmd_vs=0x{:08X} cmd_ps=0x{:08X} "
            "cmd_vs_root=0x{:08X}/{} cmd_ps_root=0x{:08X}/{}",
            ++g_direct_renderer
                  .diagnostics.logged_current_title_color_feedback_materialized_edges,
            g_direct_renderer.current_frame_index, slot, action.source_texture,
            action.backing_texture, action.target_surface, action.vertex_shader,
            action.pixel_shader, action.vertex_shader_root_symbol_address,
            static_cast<unsigned long long>(
                action.vertex_shader_root_call_serial),
            action.pixel_shader_root_symbol_address,
            static_cast<unsigned long long>(
                action.pixel_shader_root_call_serial),
            command.resources.vertex_shader, command.resources.pixel_shader,
            command.resources.shader_roots.vertex_shader.symbol_address,
            static_cast<unsigned long long>(
                command.resources.shader_roots.vertex_shader.call_serial),
            command.resources.shader_roots.pixel_shader.symbol_address,
            static_cast<unsigned long long>(
                command.resources.shader_roots.pixel_shader.call_serial));
      }
      return false;
    }
    if (g_direct_renderer.diagnostics.logged_private_rect_materialized_edge_reuse_rejections <
        16u) {
      REXLOG_INFO(
          "Refii plume rejected materialized private rect edge reuse #{} "
          "frame={} slot={} source=0x{:08X} backing=0x{:08X} "
          "target=0x{:08X} action_vs=0x{:08X} action_ps=0x{:08X} "
          "action_vs_root=0x{:08X}/{} action_ps_root=0x{:08X}/{} "
          "cmd_vs=0x{:08X} cmd_ps=0x{:08X} "
          "cmd_vs_root=0x{:08X}/{} cmd_ps_root=0x{:08X}/{}",
          ++g_direct_renderer
                .diagnostics.logged_private_rect_materialized_edge_reuse_rejections,
          g_direct_renderer.current_frame_index, slot, action.source_texture,
          action.backing_texture, action.target_surface, action.vertex_shader,
          action.pixel_shader, action.vertex_shader_root_symbol_address,
          static_cast<unsigned long long>(
              action.vertex_shader_root_call_serial),
          action.pixel_shader_root_symbol_address,
          static_cast<unsigned long long>(action.pixel_shader_root_call_serial),
          command.resources.vertex_shader, command.resources.pixel_shader,
          command.resources.shader_roots.vertex_shader.symbol_address,
          static_cast<unsigned long long>(
              command.resources.shader_roots.vertex_shader.call_serial),
          command.resources.shader_roots.pixel_shader.symbol_address,
          static_cast<unsigned long long>(
            command.resources.shader_roots.pixel_shader.call_serial));
    }
    return false;
  };
  auto find_from_actions =
      [&](const std::vector<FablePrivateTextureCopyAction>* actions)
      -> const FablePrivateTextureCopyAction* {
    if (!actions) {
      return nullptr;
    }
    for (auto it = actions->rbegin(); it != actions->rend(); ++it) {
      if (action_matches(*it)) {
        return &*it;
      }
    }
    return nullptr;
  };

  return find_from_actions(FableDirectFindPrivateTextureCopyActionsByTarget(
      target.guest_address));
}


PlumeTrackedResource* FindDirectPrivateRectUploadedActionSourceLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource* source) {
  auto valid_uploaded_source = [&](PlumeTrackedResource* candidate) {
    return candidate && IsImageResource(candidate->kind) &&
           PlumeGuestImageReady(*candidate) &&
           FableTrackedResourceHasSampleableContent(*candidate) &&
           !FableTrackedResourceIsBlankUploadedSampleProxy(*candidate) &&
           !DirectCommandBindsExactResourceAsTargetLocked(command, *candidate);
  };
  auto valid_action_uploaded_source = [&](PlumeTrackedResource* candidate) {
    if (!valid_uploaded_source(candidate) ||
        action.source_base_address == 0 ||
        PlumeGuestTextureBaseAddress(*candidate) !=
            action.source_base_address) {
      return false;
    }
    if (action.source_width != 0 &&
        PlumeGuestTextureWidth(*candidate) != action.source_width) {
      return false;
    }
    if (action.source_height != 0 &&
        PlumeGuestTextureHeight(*candidate) != action.source_height) {
      return false;
    }
    if (action.source_format != 0) {
      const uint32_t candidate_guest_format =
          PlumeGuestTextureGuestFormat(*candidate);
      const uint32_t candidate_render_format =
          static_cast<uint32_t>(PlumeGuestTextureFormat(*candidate));
      if (candidate_guest_format != action.source_format &&
          candidate_render_format != action.source_format) {
        return false;
      }
    }
    return true;
  };
  auto valid_action_backing_source = [&](PlumeTrackedResource* candidate) {
    if (!valid_uploaded_source(candidate) ||
        action.backing_texture == 0 ||
        candidate->guest_address != action.backing_texture) {
      return false;
    }
    if (action.backing_base_address != 0 &&
        PlumeGuestTextureBaseAddress(*candidate) !=
            action.backing_base_address) {
      return false;
    }
    if (action.backing_width != 0 &&
        PlumeGuestTextureWidth(*candidate) != action.backing_width) {
      return false;
    }
    if (action.backing_height != 0 &&
        PlumeGuestTextureHeight(*candidate) != action.backing_height) {
      return false;
    }
    if (action.backing_format != 0) {
      const uint32_t candidate_guest_format =
          PlumeGuestTextureGuestFormat(*candidate);
      const uint32_t candidate_render_format =
          static_cast<uint32_t>(PlumeGuestTextureFormat(*candidate));
      if (candidate_guest_format != action.backing_format &&
          candidate_render_format != action.backing_format) {
        return false;
      }
    }
    return true;
  };
  auto valid_begin_vertices_title_action_source =
      [&](PlumeTrackedResource* candidate) {
    if (!candidate || action.wrapper_symbol_address !=
                          kFableBeginVerticesTitleWrapperSymbol ||
        !DirectPrivateTextureCopyActionHasExactWrapperIdentity(action) ||
        !DirectPrivateTextureCopyKindUsesColorBacking(action.kind) ||
        !IsImageResource(candidate->kind) ||
        !PlumeGuestImageReady(*candidate) ||
        FableTrackedResourceIsBlankUploadedSampleProxy(*candidate) ||
        DirectCommandBindsExactResourceAsTargetLocked(command, *candidate) ||
        FableTrackedResourceVisibleContentSerial(*candidate) == 0) {
      return false;
    }
    const bool matches_action_address =
        DirectResourceAddressMatchesLocked(candidate->guest_address,
                                           action.source_texture) ||
        DirectResourceAddressMatchesLocked(candidate->guest_address,
                                           action.backing_texture) ||
        (action.source_base_address != 0 &&
         PlumeGuestTextureBaseAddress(*candidate) == action.source_base_address);
    if (!matches_action_address) {
      return false;
    }
    if (action.source_width != 0 &&
        PlumeGuestTextureWidth(*candidate) != action.source_width) {
      return false;
    }
    if (action.source_height != 0 &&
        PlumeGuestTextureHeight(*candidate) != action.source_height) {
      return false;
    }
    if (action.source_format != 0) {
      const uint32_t candidate_guest_format =
          PlumeGuestTextureGuestFormat(*candidate);
      const uint32_t candidate_render_format =
          static_cast<uint32_t>(PlumeGuestTextureFormat(*candidate));
      if (candidate_guest_format != action.source_format &&
          candidate_render_format != action.source_format) {
        return false;
      }
    }
    return true;
  };
  auto exact_action_source_reject_reason = [&](PlumeTrackedResource* candidate)
      -> const char* {
    if (!candidate) {
      return "missing";
    }
    if (!IsImageResource(candidate->kind)) {
      return "not-image";
    }
    if (!PlumeGuestImageReady(*candidate)) {
      return "not-ready";
    }
    if (!FableTrackedResourceHasSampleableContent(*candidate)) {
      return "no-sampleable";
    }
    if (FableTrackedResourceIsBlankUploadedSampleProxy(*candidate)) {
      return "blank-proxy";
    }
    if (DirectCommandBindsExactResourceAsTargetLocked(command, *candidate)) {
      return "bound-target";
    }
    if (action.source_base_address == 0) {
      return "no-action-base";
    }
    if (PlumeGuestTextureBaseAddress(*candidate) !=
        action.source_base_address) {
      return "base";
    }
    if (action.source_width != 0 &&
        PlumeGuestTextureWidth(*candidate) != action.source_width) {
      return "width";
    }
    if (action.source_height != 0 &&
        PlumeGuestTextureHeight(*candidate) != action.source_height) {
      return "height";
    }
    if (action.source_format != 0) {
      const uint32_t candidate_guest_format =
          PlumeGuestTextureGuestFormat(*candidate);
      const uint32_t candidate_render_format =
          static_cast<uint32_t>(PlumeGuestTextureFormat(*candidate));
      if (candidate_guest_format != action.source_format &&
          candidate_render_format != action.source_format) {
        return "format";
      }
    }
    return "ok";
  };
  auto exact_action_backing_reject_reason = [&](PlumeTrackedResource* candidate)
      -> const char* {
    if (!candidate) {
      return "missing";
    }
    if (!IsImageResource(candidate->kind)) {
      return "not-image";
    }
    if (!PlumeGuestImageReady(*candidate)) {
      return "not-ready";
    }
    if (!FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(*candidate)) {
      return "no-content";
    }
    if (FableTrackedResourceIsBlankUploadedSampleProxy(*candidate)) {
      return "blank-proxy";
    }
    if (DirectCommandBindsExactResourceAsTargetLocked(command, *candidate)) {
      return "bound-target";
    }
    if (action.backing_texture != 0 &&
        candidate->guest_address != action.backing_texture) {
      return "object";
    }
    if (action.backing_base_address != 0 &&
        PlumeGuestTextureBaseAddress(*candidate) !=
            action.backing_base_address) {
      return "base";
    }
    if (action.backing_width != 0 &&
        PlumeGuestTextureWidth(*candidate) != action.backing_width) {
      return "width";
    }
    if (action.backing_height != 0 &&
        PlumeGuestTextureHeight(*candidate) != action.backing_height) {
      return "height";
    }
    if (action.backing_format != 0) {
      const uint32_t candidate_guest_format =
          PlumeGuestTextureGuestFormat(*candidate);
      const uint32_t candidate_render_format =
          static_cast<uint32_t>(PlumeGuestTextureFormat(*candidate));
      if (candidate_guest_format != action.backing_format &&
          candidate_render_format != action.backing_format) {
        return "format";
      }
    }
    return "ok";
  };
  auto texture_base_or_zero = [](const PlumeTrackedResource* resource) {
    return resource ? PlumeGuestTextureBaseAddress(*resource) : 0u;
  };
  auto texture_width_or_zero = [](const PlumeTrackedResource* resource) {
    return resource ? PlumeGuestTextureWidth(*resource) : 0u;
  };
  auto texture_height_or_zero = [](const PlumeTrackedResource* resource) {
    return resource ? PlumeGuestTextureHeight(*resource) : 0u;
  };
  auto texture_format_or_zero = [](const PlumeTrackedResource* resource) {
    return resource ? static_cast<uint32_t>(PlumeGuestTextureFormat(*resource))
                    : 0u;
  };
  auto texture_guest_format_or_zero = [](const PlumeTrackedResource* resource) {
    return resource ? PlumeGuestTextureGuestFormat(*resource) : 0u;
  };
  auto upload_generation_or_zero = [](const PlumeTrackedResource* resource) {
    return resource ? static_cast<unsigned long long>(resource->upload_generation)
                    : 0ull;
  };
  auto content_serial_or_zero = [](const PlumeTrackedResource* resource) {
    return resource ? static_cast<unsigned long long>(resource->content_serial)
                    : 0ull;
  };
  auto draw_content_serial_or_zero = [](const PlumeTrackedResource* resource) {
    return resource
               ? static_cast<unsigned long long>(resource->draw_content_serial)
               : 0ull;
  };
  auto clear_content_serial_or_zero = [](const PlumeTrackedResource* resource) {
    return resource
               ? static_cast<unsigned long long>(resource->clear_content_serial)
               : 0ull;
  };
  auto has_meaningful_upload_flag = [](const PlumeTrackedResource* resource) {
    return resource && FableTrackedResourceHasMeaningfulTextureUpload(*resource) ? 1
                                                                          : 0;
  };
  auto has_gpu_content_flag = [](const PlumeTrackedResource* resource) {
    return resource && FableTrackedResourceHasGpuProducedContent(*resource) ? 1 : 0;
  };
  auto is_bound_target_flag = [&](const PlumeTrackedResource* resource) {
    return resource && DirectCommandBindsResourceAsTargetLocked(command,
                                                               *resource)
               ? 1
               : 0;
  };
  auto find_registered_link_source = [&]() -> PlumeTrackedResource* {
    const std::array<uint32_t, 6> command_fetch =
        DirectCommandTextureFetchDwords(command, slot);
    const uint64_t action_source_fetch_key =
        FableDirectTextureFetchKey(action.source_fetch_dwords);
    const uint64_t action_backing_fetch_key =
        FableDirectTextureFetchKey(action.backing_fetch_dwords);
    const uint64_t command_fetch_key =
        FableDirectTextureFetchKey(command_fetch);

    auto link_score = [&](const FableSurfaceTextureLink& link) -> uint64_t {
      uint64_t score = link.serial + static_cast<uint64_t>(link.frame_index) +
                       link.draw_serial;
      if (action.source_texture != 0 && link.texture == action.source_texture) {
        score += 1'000'000'000'000ull;
      }
      if (source && link.texture == source->guest_address) {
        score += 500'000'000'000ull;
      }
      if (action.target_surface != 0 &&
          link.source_surface == action.target_surface) {
        score += 250'000'000'000ull;
      }
      if (action.render_target != 0 &&
          (link.source_surface == action.render_target ||
           link.render_target == action.render_target)) {
        score += 125'000'000'000ull;
      }
      if (action.backing_texture != 0 &&
          link.backing_texture == action.backing_texture) {
        score += 100'000'000'000ull;
      }
      if (action.source_base_address != 0 &&
          link.texture_base_address == action.source_base_address) {
        score += 50'000'000'000ull;
      }
      if (action.backing_base_address != 0 &&
          link.backing_base_address == action.backing_base_address) {
        score += 25'000'000'000ull;
      }
      const uint64_t link_texture_fetch_key =
          FableDirectTextureFetchKey(link.texture_fetch_dwords);
      const uint64_t link_backing_fetch_key =
          FableDirectTextureFetchKey(link.backing_fetch_dwords);
      if (action_source_fetch_key != 0 &&
          link_texture_fetch_key == action_source_fetch_key) {
        score += 10'000'000'000ull;
      }
      if (action_backing_fetch_key != 0 &&
          link_backing_fetch_key == action_backing_fetch_key) {
        score += 5'000'000'000ull;
      }
      if (command_fetch_key != 0 &&
          (link_texture_fetch_key == command_fetch_key ||
           link_backing_fetch_key == command_fetch_key)) {
        score += 2'500'000'000ull;
      }
      if (action.source_width != 0 && link.texture_width == action.source_width) {
        score += 100'000'000ull;
      }
      if (action.source_height != 0 &&
          link.texture_height == action.source_height) {
        score += 100'000'000ull;
      }
      if (action.source_format != 0 &&
          link.texture_format == action.source_format) {
        score += 50'000'000ull;
      }
      if (link.render_target != 0 &&
          link.render_target == command.resources.render_targets[0]) {
        score += 25'000'000ull;
      }
      if (link.depth_stencil != 0 &&
          link.depth_stencil == command.resources.depth_stencil) {
        score += 10'000'000ull;
      }
      if (link.vertex_shader != 0 &&
          DirectVertexShaderObjectsEquivalentLocked(
              command.resources.vertex_shader, link.vertex_shader)) {
        score += 5'000'000ull;
      }
      if (link.pixel_shader != 0 &&
          DirectPixelShaderObjectsEquivalentLocked(
              command.resources.pixel_shader, link.pixel_shader)) {
        score += 5'000'000ull;
      }
      if (link.frame_index == g_direct_renderer.current_frame_index) {
        score += 1'000'000ull;
      }
      return score;
    };
    auto private_copy_kinds_compatible =
        [](FablePrivateTextureCopyKind lhs,
           FablePrivateTextureCopyKind rhs) {
          if (lhs == rhs) {
            return true;
          }
          const bool lhs_color =
              lhs == FablePrivateTextureCopyKind::kColor ||
              lhs == FablePrivateTextureCopyKind::kColorSibling;
          const bool rhs_color =
              rhs == FablePrivateTextureCopyKind::kColor ||
              rhs == FablePrivateTextureCopyKind::kColorSibling;
          return lhs_color && rhs_color;
        };
    auto link_is_visible_exact_action_edge =
        [&](const FableSurfaceTextureLink& link) {
          if (!link.trusted_destination_edge ||
              !private_copy_kinds_compatible(link.kind, action.kind)) {
            return false;
          }
          if (link.frame_index != 0 &&
              link.frame_index > g_direct_renderer.current_frame_index) {
            return false;
          }
          if (link.frame_index != 0 &&
              link.frame_index == g_direct_renderer.current_frame_index &&
              link.draw_serial != 0 && command.draw_serial != 0 &&
              link.draw_serial > command.draw_serial) {
            return false;
          }
          constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
          const uint64_t link_texture_fetch_key =
              FableDirectTextureFetchKey(link.texture_fetch_dwords);
          const uint64_t link_backing_fetch_key =
              FableDirectTextureFetchKey(link.backing_fetch_dwords);
          const bool link_matches_action_identity =
              (action.source_texture != 0 &&
               (link.texture == action.source_texture ||
                link.backing_texture == action.source_texture)) ||
              (action.backing_texture != 0 &&
               link.backing_texture == action.backing_texture) ||
              (action.source_base_address != 0 &&
               (link.texture_base_address == action.source_base_address ||
                link.backing_base_address == action.source_base_address)) ||
              (action.backing_base_address != 0 &&
               link.backing_base_address == action.backing_base_address) ||
              (action_source_fetch_key != 0 &&
               (link_texture_fetch_key == action_source_fetch_key ||
                link_backing_fetch_key == action_source_fetch_key)) ||
              (action_backing_fetch_key != 0 &&
               link_backing_fetch_key == action_backing_fetch_key);
          if (link.source_slot != kUnknownSlot && link.source_slot != slot &&
              !link_matches_action_identity) {
            return false;
          }
          if (action.target_surface != 0 &&
              link.source_surface == action.target_surface) {
            return true;
          }
          if (action.kind == FablePrivateTextureCopyKind::kDepth) {
            return action.depth_stencil != 0 &&
                   (link.source_surface == action.depth_stencil ||
                    link.depth_stencil == action.depth_stencil);
          }
          if (action.render_target != 0 &&
              (link.source_surface == action.render_target ||
               link.render_target == action.render_target)) {
            return true;
          }
          if (action.source_texture != 0 &&
              (link.texture == action.source_texture ||
               link.backing_texture == action.source_texture)) {
            return true;
          }
          if (action.backing_texture != 0 &&
              link.backing_texture == action.backing_texture) {
            return true;
          }
          if (action.source_base_address != 0 &&
              (link.texture_base_address == action.source_base_address ||
               link.backing_base_address == action.source_base_address)) {
            return true;
          }
          if (action.backing_base_address != 0 &&
              link.backing_base_address == action.backing_base_address) {
            return true;
          }
          return (action_source_fetch_key != 0 &&
                  (link_texture_fetch_key == action_source_fetch_key ||
                   link_backing_fetch_key == action_source_fetch_key)) ||
                 (action_backing_fetch_key != 0 &&
                  link_backing_fetch_key == action_backing_fetch_key);
        };

    auto try_exact_destination_link =
        [&](uint32_t destination_texture) -> PlumeTrackedResource* {
      if (destination_texture == 0) {
        return nullptr;
      }
      const FableSurfaceTextureLink* link =
          FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
              destination_texture, g_direct_renderer.current_frame_index,
              command.draw_serial, slot);
      if (!link) {
        link = FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
            destination_texture, slot);
      }
      if (!link) {
        link = FableDirectFindSurfaceTextureLinkForDestinationTexture(
            destination_texture);
      }
      if (!link || !link_is_visible_exact_action_edge(*link)) {
        return nullptr;
      }
      PlumeTrackedResource* linked =
          ResolveDirectSurfaceTextureLinkResourceLocked(command, *link);
      if (!linked) {
        return nullptr;
      }
      if (g_direct_renderer.diagnostics.logged_private_rect_exact_destination_link_sources <
          24u) {
        REXLOG_INFO(
            "Refii plume private rect exact destination link source #{} "
            "frame={} slot={} destination=0x{:08X} action_source=0x{:08X} "
            "target=0x{:08X} link_texture=0x{:08X} "
            "link_surface=0x{:08X} link_backing=0x{:08X} "
            "sample=0x{:08X}/{}x{} fmt={} rt=0x{:08X} vs=0x{:08X} "
            "ps=0x{:08X}",
            ++g_direct_renderer
                  .diagnostics.logged_private_rect_exact_destination_link_sources,
            g_direct_renderer.current_frame_index, slot, destination_texture,
            action.source_texture, action.target_surface, link->texture,
            link->source_surface, link->backing_texture, linked->guest_address,
            PlumeGuestTextureWidth(*linked), PlumeGuestTextureHeight(*linked),
            static_cast<uint32_t>(PlumeGuestTextureFormat(*linked)),
            command.resources.render_targets[0], command.resources.vertex_shader,
            command.resources.pixel_shader);
      }
      return linked;
    };

    if (PlumeTrackedResource* linked =
            try_exact_destination_link(action.source_texture)) {
      return linked;
    }
    if (source) {
      if (PlumeTrackedResource* linked =
              try_exact_destination_link(source->guest_address)) {
        return linked;
      }
      if (source->parent_address != 0) {
        if (PlumeTrackedResource* linked =
                try_exact_destination_link(source->parent_address)) {
          return linked;
        }
      }
    }
    if (PlumeTrackedResource* linked =
            try_exact_destination_link(action.backing_texture)) {
      return linked;
    }

    const FableSurfaceTextureLink* best_link = nullptr;
    PlumeTrackedResource* best_resource = nullptr;
    uint64_t best_score = 0;
    auto consider_link = [&](const FableSurfaceTextureLink* link) {
      if (!link || !link_is_visible_exact_action_edge(*link)) {
        return;
      }
      PlumeTrackedResource* linked =
          ResolveDirectSurfaceTextureLinkResourceLocked(command, *link);
      if (!linked) {
        return;
      }
      const uint64_t score = link_score(*link);
      if (!best_resource || score > best_score) {
        best_link = link;
        best_resource = linked;
        best_score = score;
      }
    };
    auto consider_links = [&](const std::vector<FableSurfaceTextureLink>* links) {
      if (!links) {
        return;
      }
      for (const FableSurfaceTextureLink& link : *links) {
        consider_link(&link);
      }
    };

    consider_link(FableDirectFindSurfaceTextureLinkByCopyCall(
        action.wrapper_call_serial));
    consider_link(FableDirectFindSurfaceTextureLinkByCopyContext(
        action.wrapper_context_key));
    consider_link(FableDirectFindSurfaceTextureLinkForSourceSurfaceSlot(
        action.target_surface, slot));
    consider_link(
        FableDirectFindSurfaceTextureLinkForSourceSurface(action.target_surface));
    consider_links(
        FableDirectFindSurfaceTextureLinksByTexture(action.source_texture));
    consider_links(
        FableDirectFindSurfaceTextureLinksBySurface(action.source_texture));
    consider_links(
        FableDirectFindSurfaceTextureLinksBySurface(action.target_surface));
    consider_links(
        FableDirectFindSurfaceTextureLinksBySurface(action.render_target));
    consider_links(
        FableDirectFindSurfaceTextureLinksByTexture(action.backing_texture));
    consider_links(
        FableDirectFindSurfaceTextureLinksByBase(action.source_base_address));
    consider_links(
        FableDirectFindSurfaceTextureLinksByBase(action.backing_base_address));
    consider_links(
        FableDirectFindSurfaceTextureLinksByFetch(action.source_fetch_dwords));
    consider_links(
        FableDirectFindSurfaceTextureLinksByFetch(action.backing_fetch_dwords));
    consider_links(FableDirectFindSurfaceTextureLinksByFetch(command_fetch));
    if (source) {
      consider_links(
          FableDirectFindSurfaceTextureLinksByTexture(source->guest_address));
      consider_links(
          FableDirectFindSurfaceTextureLinksBySurface(source->guest_address));
      consider_links(FableDirectFindSurfaceTextureLinksByBase(
          PlumeGuestTextureBaseAddress(*source)));
      consider_links(FableDirectFindSurfaceTextureLinksByFetch(
          PlumeGuestTextureFetchDwords(*source)));
    }

    if (!best_resource || !best_link) {
      return nullptr;
    }
    if (g_direct_renderer.diagnostics.logged_private_rect_surface_texture_ownership_proofs <
        24u) {
      REXLOG_INFO(
          "Refii plume private rect action link source #{} frame={} slot={} "
          "action_source=0x{:08X} target=0x{:08X} link_texture=0x{:08X} "
          "link_surface=0x{:08X} link_backing=0x{:08X} "
          "sample=0x{:08X}/{}x{} fmt={} rt=0x{:08X} vs=0x{:08X} "
          "ps=0x{:08X}",
          ++g_direct_renderer
                .diagnostics.logged_private_rect_surface_texture_ownership_proofs,
          g_direct_renderer.current_frame_index, slot, action.source_texture,
          action.target_surface, best_link->texture, best_link->source_surface,
          best_link->backing_texture, best_resource->guest_address,
          PlumeGuestTextureWidth(*best_resource),
          PlumeGuestTextureHeight(*best_resource),
          static_cast<uint32_t>(PlumeGuestTextureFormat(*best_resource)),
          command.resources.render_targets[0], command.resources.vertex_shader,
          command.resources.pixel_shader);
    }
    return best_resource;
  };
  if (source && valid_action_uploaded_source(
                    const_cast<PlumeTrackedResource*>(source))) {
    return const_cast<PlumeTrackedResource*>(source);
  }
  if (source && valid_begin_vertices_title_action_source(
                    const_cast<PlumeTrackedResource*>(source))) {
    static uint32_t logged_begin_vertices_action_content_sources = 0;
    if (logged_begin_vertices_action_content_sources < 24u) {
      REXLOG_INFO(
          "Refii plume exact BeginVertices private action content source #{} "
          "frame={} slot={} action_source=0x{:08X} source=0x{:08X}/{}x{} "
          "base=0x{:08X} content={} call={} context=0x{:016X} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_begin_vertices_action_content_sources,
          g_direct_renderer.current_frame_index, slot, action.source_texture,
          source->guest_address, PlumeGuestTextureWidth(*source),
          PlumeGuestTextureHeight(*source),
          PlumeGuestTextureBaseAddress(*source),
          static_cast<unsigned long long>(
              FableTrackedResourceVisibleContentSerial(*source)),
          static_cast<unsigned long long>(action.wrapper_call_serial),
          static_cast<unsigned long long>(action.wrapper_context_key),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return const_cast<PlumeTrackedResource*>(source);
  }
  if (PlumeTrackedResource* sampled_source =
          ResolveDirectRegisteredSampleSourceLocked(command,
                                                    action.sampled_source)) {
    if (g_direct_renderer.diagnostics.logged_private_rect_surface_texture_ownership_proofs <
        24u) {
      REXLOG_INFO(
          "Refii plume exact private rect sampled-source owner #{} "
          "frame={} slot={} action_source=0x{:08X} sampled={} "
          "sample=0x{:08X}/{}x{} fmt={} content={} rt=0x{:08X} "
          "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++g_direct_renderer
                .diagnostics.logged_private_rect_surface_texture_ownership_proofs,
          g_direct_renderer.current_frame_index, slot, action.source_texture,
          FableRegisteredSampleSourceKindName(action.sampled_source.kind),
          sampled_source->guest_address, PlumeGuestTextureWidth(*sampled_source),
          PlumeGuestTextureHeight(*sampled_source),
          static_cast<uint32_t>(PlumeGuestTextureFormat(*sampled_source)),
          static_cast<unsigned long long>(
              FableTrackedResourceVisibleContentSerial(*sampled_source)),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return sampled_source;
  }
  if (PlumeTrackedResource* uploaded =
          FindDirectResourceLocked(action.backing_texture);
      valid_action_backing_source(uploaded)) {
    return uploaded;
  }
  if (PlumeTrackedResource* uploaded =
          FindDirectResourceLocked(action.source_texture);
      valid_uploaded_source(uploaded)) {
    return uploaded;
  }
  if (PlumeTrackedResource* uploaded = find_registered_link_source()) {
    return uploaded;
  }
  PlumeTrackedResource* target_object =
      FindDirectResourceLocked(action.target_surface);
  const bool exact_current_target_owner =
      target_object && IsImageResource(target_object->kind) &&
      PlumeGuestImageReady(*target_object) &&
      FableTrackedResourceHasGpuProducedContent(*target_object) &&
      DirectCommandBindsExactResourceAsTargetLocked(command, *target_object) &&
      (action.render_target == 0 ||
       DirectResourceAddressMatchesLocked(target_object->guest_address,
                                          action.render_target)) &&
      DirectResourceAddressMatchesLocked(target_object->guest_address,
                                         command.resources.render_targets[0]);
  if (exact_current_target_owner) {
    return nullptr;
  }
	  const char* pending_target_proof =
	      DirectIdaPrivateRectWrapperProofName(action.wrapper_symbol_address);
	  const bool exact_ida_private_rect_action =
	      pending_target_proof &&
	      DirectPrivateTextureCopyActionHasExactWrapperIdentity(action);
	  const bool action_source_is_private =
	      IsFableTitlePrivateTextureProxyAddress(action.source_texture) ||
	      IsFableTitlePrivateTextureProxyAddress(action.source_base_address) ||
	      (source && IsFablePrivateTextureChainResource(*source)) ||
	      exact_ida_private_rect_action;
  const bool exact_gpu_target_source =
      action_source_is_private && target_object &&
      IsImageResource(target_object->kind) && PlumeGuestImageReady(*target_object) &&
      DirectPrivateTextureCopyKindUsesColorBacking(action.kind) &&
      FableTrackedResourceHasGpuProducedContent(*target_object) &&
      FableTrackedResourceVisibleContentSerial(*target_object) != 0 &&
      !DirectCommandBindsExactResourceAsTargetLocked(command, *target_object);
  if (exact_gpu_target_source) {
    if (g_direct_renderer.diagnostics.logged_private_action_current_gpu_surface_sources <
        32u) {
      REXLOG_INFO(
          "Refii plume exact private rect gpu target-source #{} frame={} "
          "slot={} action_source=0x{:08X} target=0x{:08X}/{}x{} "
          "target_content={} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
          "ps=0x{:08X}",
          ++g_direct_renderer.diagnostics.logged_private_action_current_gpu_surface_sources,
          g_direct_renderer.current_frame_index, slot, action.source_texture,
          target_object->guest_address, PlumeGuestTextureWidth(*target_object),
          PlumeGuestTextureHeight(*target_object),
          static_cast<unsigned long long>(
              FableTrackedResourceVisibleContentSerial(*target_object)),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return target_object;
  }
  const uint64_t target_visible_content =
      target_object ? FableTrackedResourceVisibleContentSerial(*target_object) : 0;
  const uint64_t required_target_content =
      std::max(action.target_content_serial,
               action.sampled_source.kind ==
                       FableRegisteredSampleSourceKind::kGpuSurface
                   ? action.sampled_source.content_serial
                   : uint64_t{0});
  const bool exact_gpu_target_source_by_serial =
      exact_ida_private_rect_action && action_source_is_private &&
      target_object && IsImageResource(target_object->kind) &&
      PlumeGuestImageReady(*target_object) &&
      DirectPrivateTextureCopyKindUsesColorBacking(action.kind) &&
      action.target_surface != 0 &&
      DirectResourceAddressMatchesLocked(target_object->guest_address,
                                         action.target_surface) &&
      action.sampled_source.kind == FableRegisteredSampleSourceKind::kGpuSurface &&
      (action.sampled_source.resource == 0 ||
       DirectResourceAddressMatchesLocked(action.sampled_source.resource,
                                          action.target_surface)) &&
      required_target_content != 0 &&
      target_visible_content >= required_target_content &&
      !DirectCommandBindsExactResourceAsTargetLocked(command, *target_object);
  if (exact_gpu_target_source_by_serial) {
    if (g_direct_renderer.diagnostics.logged_private_action_current_gpu_surface_sources <
        32u) {
      REXLOG_INFO(
          "Refii plume exact private rect gpu target-source serial #{} "
          "frame={} slot={} proof={} action_source=0x{:08X} "
          "target=0x{:08X}/{}x{} target_content={} required_content={} "
          "call={} context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} "
          "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++g_direct_renderer.diagnostics.logged_private_action_current_gpu_surface_sources,
          g_direct_renderer.current_frame_index, slot, pending_target_proof,
          action.source_texture, target_object->guest_address,
          PlumeGuestTextureWidth(*target_object),
          PlumeGuestTextureHeight(*target_object),
          static_cast<unsigned long long>(target_visible_content),
          static_cast<unsigned long long>(required_target_content),
          static_cast<unsigned long long>(action.wrapper_call_serial),
          static_cast<unsigned long long>(action.wrapper_context_key),
          action.wrapper_symbol_address, command.resources.render_targets[0],
          command.resources.depth_stencil, command.resources.vertex_shader,
          command.resources.pixel_shader);
    }
    return target_object;
  }
	  const bool exact_private_rect_target_pending =
	      exact_ida_private_rect_action &&
	      action_source_is_private && target_object &&
      IsImageResource(target_object->kind) && PlumeGuestImageReady(*target_object) &&
      DirectPrivateTextureCopyKindUsesColorBacking(action.kind) &&
      !FableTrackedResourceHasSampleableContent(*target_object) &&
      !FableTrackedResourceHasGpuProducedContent(*target_object);
  if (exact_private_rect_target_pending) {
    static uint32_t logged_private_rect_pending_target_sources = 0;
    if (logged_private_rect_pending_target_sources < 32u) {
      REXLOG_INFO(
          "Refii plume exact private rect action source pending target #{} "
          "frame={} slot={} proof={} action_source=0x{:08X} "
          "target=0x{:08X}/{}x{} target_content={} call={} "
          "context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} ds=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X}",
          ++logged_private_rect_pending_target_sources,
          g_direct_renderer.current_frame_index, slot, pending_target_proof,
          action.source_texture, target_object->guest_address,
          PlumeGuestTextureWidth(*target_object),
          PlumeGuestTextureHeight(*target_object),
          static_cast<unsigned long long>(
              FableTrackedResourceVisibleContentSerial(*target_object)),
          static_cast<unsigned long long>(action.wrapper_call_serial),
          static_cast<unsigned long long>(action.wrapper_context_key),
          action.wrapper_symbol_address, command.resources.render_targets[0],
          command.resources.depth_stencil, command.resources.vertex_shader,
          command.resources.pixel_shader);
    }
    return nullptr;
  }
  if (g_direct_renderer.diagnostics.logged_private_rect_exact_source_misses < 16u) {
    PlumeTrackedResource* action_object =
        FindDirectResourceLocked(action.source_texture);
    PlumeTrackedResource* backing_object =
        FindDirectResourceLocked(action.backing_texture);
    REXLOG_INFO(
        "Refii plume exact private rect action source reject #{} frame={} "
        "slot={} action_source=0x{:08X} base=0x{:08X} {}x{} fmt={} "
        "backing=0x{:08X} backing_base=0x{:08X} {}x{} fmt={} "
        "reason={} source_reason={} backing_reason={} target_reason={} "
        "object=0x{:08X}/{}x{} base=0x{:08X} fmt={}/{} gen={} "
        "content={}/{}/{} upload={} gpu={} bound={} "
        "backing_object=0x{:08X}/{}x{} base=0x{:08X} fmt={}/{} gen={} "
        "content={}/{}/{} upload={} gpu={} bound={} "
        "target=0x{:08X} target_object=0x{:08X}/{}x{} base=0x{:08X} "
        "fmt={}/{} gen={} content={}/{}/{} upload={} gpu={} bound={} "
        "rt=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
        ++g_direct_renderer.diagnostics.logged_private_rect_exact_source_misses,
        g_direct_renderer.current_frame_index, slot, action.source_texture,
        action.source_base_address, action.source_width, action.source_height,
        action.source_format, action.backing_texture,
        action.backing_base_address, action.backing_width,
        action.backing_height, action.backing_format,
        exact_action_source_reject_reason(action_object),
        exact_action_source_reject_reason(
            const_cast<PlumeTrackedResource*>(source)),
        exact_action_backing_reject_reason(backing_object),
        exact_action_backing_reject_reason(target_object),
        action_object ? action_object->guest_address : 0,
        texture_width_or_zero(action_object),
        texture_height_or_zero(action_object),
        texture_base_or_zero(action_object),
        texture_format_or_zero(action_object),
        texture_guest_format_or_zero(action_object),
        upload_generation_or_zero(action_object),
        content_serial_or_zero(action_object),
        draw_content_serial_or_zero(action_object),
        clear_content_serial_or_zero(action_object),
        has_meaningful_upload_flag(action_object),
        has_gpu_content_flag(action_object),
        is_bound_target_flag(action_object),
        backing_object ? backing_object->guest_address : 0,
        texture_width_or_zero(backing_object),
        texture_height_or_zero(backing_object),
        texture_base_or_zero(backing_object),
        texture_format_or_zero(backing_object),
        texture_guest_format_or_zero(backing_object),
        upload_generation_or_zero(backing_object),
        content_serial_or_zero(backing_object),
        draw_content_serial_or_zero(backing_object),
        clear_content_serial_or_zero(backing_object),
        has_meaningful_upload_flag(backing_object),
        has_gpu_content_flag(backing_object),
        is_bound_target_flag(backing_object), action.target_surface,
        target_object ? target_object->guest_address : 0,
        texture_width_or_zero(target_object),
        texture_height_or_zero(target_object),
        texture_base_or_zero(target_object),
        texture_format_or_zero(target_object),
        texture_guest_format_or_zero(target_object),
        upload_generation_or_zero(target_object),
        content_serial_or_zero(target_object),
        draw_content_serial_or_zero(target_object),
        clear_content_serial_or_zero(target_object),
        has_meaningful_upload_flag(target_object),
        has_gpu_content_flag(target_object),
        is_bound_target_flag(target_object), command.resources.render_targets[0],
        command.resources.vertex_shader, command.resources.pixel_shader);
  }
  return nullptr;
}

PlumeTrackedResource* FindDirectPrivateRectExactUploadedBackingLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* source) {
  if (!source || slot >= command.resources.textures.size() ||
      !command.draw.private_texture_rectangle ||
      !IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader) ||
      !DirectCommandUsesFablePrivateTexturePath(command, slot, source)) {
    return nullptr;
  }

  const uint32_t source_width = PlumeGuestTextureWidth(*source);
  const uint32_t source_height = PlumeGuestTextureHeight(*source);
  if (source_width == 0 || source_height == 0 ||
      (source_width == 256u && source_height == 256u &&
       FableTrackedResourceIsUnbackedSampleProxy(*source))) {
    return nullptr;
  }

  auto valid_uploaded_source = [&](PlumeTrackedResource* candidate) {
    return candidate && candidate != source && IsImageResource(candidate->kind) &&
           PlumeGuestImageReady(*candidate) &&
           FableTrackedResourceHasMeaningfulTextureUpload(*candidate) &&
           !FableTrackedResourceIsBlankUploadedSampleProxy(*candidate) &&
           !candidate->texture_render_target && !candidate->texture_depth_target &&
           !DirectCommandBindsResourceAsTargetLocked(command, *candidate) &&
           PlumeGuestTextureWidth(*candidate) == source_width &&
           PlumeGuestTextureHeight(*candidate) == source_height;
  };

  if (PlumeTrackedResource* uploaded =
          FindDirectReadyUploadedTextureByFetchDwordsLocked(
              DirectCommandTextureFetchDwords(command, slot), source);
      valid_uploaded_source(uploaded)) {
    return uploaded;
  }
  if (PlumeTrackedResource* uploaded =
          FindDirectReadyUploadedTextureByFetchDwordsLocked(
              PlumeGuestTextureFetchDwords(*source), source);
      valid_uploaded_source(uploaded)) {
    return uploaded;
  }
  if (PlumeTrackedResource* uploaded =
          FindDirectReadyUploadedTextureByBaseLocked(
              PlumeGuestTextureBaseAddress(*source), *source);
      valid_uploaded_source(uploaded)) {
    return uploaded;
  }
  return nullptr;
}

bool DirectCommandUsesFablePrivateTexturePath(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (slot >= command.resources.textures.size()) {
    return false;
  }

  const uint32_t guest = command.resources.textures[slot];
  const bool synthetic_private_rect =
      IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader);
  const bool explicit_private_rect_source_slot =
      synthetic_private_rect
          ? slot == source_slot
          : (command.draw.private_texture_rectangle &&
             slot == (command.draw.private_texture_slot &
                      (kMaxFableTextureSlots - 1u)));
  if (!explicit_private_rect_source_slot && texture &&
      IsFableHeaderOnlyPrivateTextureProxyAddress(texture->guest_address) &&
      FableTrackedResourceHasMeaningfulTextureUpload(*texture)) {
    return false;
  }

  const bool texture_has_private_identity =
      IsFableTitlePrivateTextureProxyAddress(guest) ||
      (texture &&
       (IsFableTitlePrivateTextureProxyAddress(texture->guest_address) ||
        IsFableTitlePrivateTextureProxyAddress(
            PlumeGuestTextureBaseAddress(*texture)) ||
        IsFablePrivateTextureChainResource(*texture)));
  if (texture_has_private_identity) {
    return true;
  }

  if (synthetic_private_rect) {
    return explicit_private_rect_source_slot;
  }
  if (!command.draw.private_texture_rectangle) {
    return false;
  }
  return explicit_private_rect_source_slot;
}

bool EnsureDirectPrivateTextureHeaderResourceLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    uint32_t slot) {
  const FableDirectClock::time_point header_t0 = FableDirectClock::now();
  double find_ms = 0.0;
  double path_ms = 0.0;
  double insert_check_ms = 0.0;
  double sync_ms = 0.0;
  double final_find_ms = 0.0;

  if (!device || command.draw.source == RendererDrawEventSource::kPixVideo ||
      slot >= command.resources.textures.size()) {
    return false;
  }

  const uint32_t guest = command.resources.textures[slot];
  if (guest == 0) {
    return false;
  }

  const std::array<uint32_t, 6> fetch_dwords =
      DirectCommandTextureFetchDwords(command, slot);
  if (!HasTrackedFetchDwords(fetch_dwords)) {
    return false;
  }

  namespace xenos = rex::graphics::xenos;
  xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = fetch_dwords[0];
  fetch.dword_1 = fetch_dwords[1];
  fetch.dword_2 = fetch_dwords[2];
  fetch.dword_3 = fetch_dwords[3];
  fetch.dword_4 = fetch_dwords[4];
  fetch.dword_5 = fetch_dwords[5];
  if (fetch.type != xenos::FetchConstantType::kTexture &&
      fetch.type != xenos::FetchConstantType::kInvalidTexture) {
    return false;
  }

  const uint32_t fetch_base = fetch.base_address << 12;
  if (!IsFableTitlePrivateTextureProxyAddress(guest) &&
      !IsFableMutablePrivateTextureHeaderAddress(guest) &&
      !IsFableTitlePrivateTextureProxyAddress(fetch_base) &&
      !IsFableMutablePrivateTextureHeaderAddress(fetch_base)) {
    return false;
  }

  const FableDirectClock::time_point find_t0 = FableDirectClock::now();
  PlumeTrackedResource* existing = FindDirectResourceLocked(guest);
  find_ms += FableDirectElapsedMs(find_t0, FableDirectClock::now());
  const FableDirectClock::time_point path_t0 = FableDirectClock::now();
  if (!DirectCommandUsesFablePrivateTexturePath(command, slot, existing) &&
      !IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) &&
      !IsFableTitleSceneVertexShaderCommandLocked(command)) {
    return false;
  }
  path_ms += FableDirectElapsedMs(path_t0, FableDirectClock::now());

  const bool existing_ready =
      existing && PlumeGuestImageReady(*existing);
  const bool existing_fetch_match =
      existing_ready &&
      DirectTextureFetchDwordsMatch(PlumeGuestTextureFetchDwords(*existing),
                                    fetch_dwords);
  const DirectTextureFetchInfo header_fetch_info =
      DirectTextureFetchInfoFromDwords(fetch_dwords);
  const bool exact_begin_vertices_title_strip_header =
      FableTitleCommandSlotIsExactBeginVerticesTitleStrip(command, slot,
                                                      header_fetch_info);

  if (existing_ready &&
      FableTrackedResourceHasMeaningfulTextureUpload(*existing) &&
      !FableTrackedResourceIsBlankUploadedSampleProxy(*existing)) {
    return false;
  }

  if (!exact_begin_vertices_title_strip_header &&
      DirectCommandTextureSlotExactProducerProvidesSampleLocked(command, slot,
                                                               existing)) {
    static uint32_t
        logged_exact_private_texture_header_producer_ownership_proofs = 0;
    if (logged_exact_private_texture_header_producer_ownership_proofs < 32u) {
      REXLOG_INFO(
          "Refii plume exact private texture header producer ownership proof "
          "#{} reason=producer-already-owns-sample frame={} serial={} draw={} "
          "slot={} guest=0x{:08X} base=0x{:08X} existing=0x{:08X}/{}x{} "
          "fmt={} fetch_match={} source={} wrapper_call={} "
          "wrapper_context=0x{:016X} wrapper_symbol=0x{:08X} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
          "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
          ++logged_exact_private_texture_header_producer_ownership_proofs,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.serial),
          static_cast<unsigned long long>(command.draw_serial), slot, guest,
          fetch_base, existing ? existing->guest_address : 0u,
          existing ? PlumeGuestTextureWidth(*existing) : 0u,
          existing ? PlumeGuestTextureHeight(*existing) : 0u,
          existing ? static_cast<uint32_t>(PlumeGuestTextureFormat(*existing))
                   : 0u,
          existing_fetch_match, static_cast<uint32_t>(command.draw.source),
          static_cast<unsigned long long>(
              command.draw.private_texture_wrapper_call_serial),
          static_cast<unsigned long long>(
              command.draw.private_texture_wrapper_context_key),
          command.draw.private_texture_wrapper_symbol_address,
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader,
          fetch_dwords[0], fetch_dwords[1], fetch_dwords[2], fetch_dwords[3],
          fetch_dwords[4], fetch_dwords[5]);
    }
    return false;
  }

  if (existing_fetch_match) {
    return false;
  }

  FableGuestResourceSnapshot snapshot;
  snapshot.kind = FableGuestResourceKind::kTexture;
  snapshot.guest_address = guest;
  snapshot.parent_address = guest;
  snapshot.texture.object_address = guest;
  snapshot.texture.parent_address = guest;
  snapshot.texture.base_address = fetch.base_address << 12;
  snapshot.texture.raw_fetch_dwords = fetch_dwords;
  snapshot.texture.normalized_fetch_dwords = fetch_dwords;
  snapshot.serial = std::max<uint64_t>(
      1ull, std::max(command.resource_serial,
                     std::max(command.serial, command.draw_serial)));

  const FableDirectClock::time_point insert_t0 = FableDirectClock::now();
  const bool inserted =
      g_direct_renderer.resources.find(snapshot.guest_address) ==
      g_direct_renderer.resources.end();
  insert_check_ms += FableDirectElapsedMs(insert_t0, FableDirectClock::now());
  const FableDirectClock::time_point sync_t0 = FableDirectClock::now();
  if (!SyncTrackedResourceLocked(device, snapshot, nullptr)) {
    sync_ms += FableDirectElapsedMs(sync_t0, FableDirectClock::now());
    ++g_direct_renderer.failed_resource_syncs;
    LogDirectResourceSyncFailureLocked(snapshot, "private_texture_header");
    return false;
  }
  sync_ms += FableDirectElapsedMs(sync_t0, FableDirectClock::now());
  if (inserted) {
    ++g_direct_renderer.resource_creations;
  }

  const FableDirectClock::time_point final_find_t0 = FableDirectClock::now();
  PlumeTrackedResource* materialized =
      FindDirectResourceLocked(snapshot.guest_address);
  final_find_ms +=
      FableDirectElapsedMs(final_find_t0, FableDirectClock::now());
  const double total_ms =
      FableDirectElapsedMs(header_t0, FableDirectClock::now());
  if ((total_ms >= 1.0 || sync_ms >= 0.5) &&
      g_direct_renderer.diagnostics.logged_private_texture_header_timing_breakdowns <
          128u) {
    REXLOG_INFO(
        "Refii plume private texture header timing #{} frame={} slot={} "
        "guest=0x{:08X} base=0x{:08X} total_ms={:.3f} find_ms={:.3f} "
        "path_ms={:.3f} insert_check_ms={:.3f} sync_ms={:.3f} "
        "final_find_ms={:.3f} inserted={} existing_ready={} fetch_match={} "
        "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
        ++g_direct_renderer.diagnostics.logged_private_texture_header_timing_breakdowns,
        g_direct_renderer.current_frame_index, slot, guest, fetch_base,
        total_ms, find_ms, path_ms, insert_check_ms, sync_ms, final_find_ms,
        inserted, existing_ready, existing_fetch_match,
        command.resources.render_targets[0], command.resources.depth_stencil,
        command.resources.vertex_shader, command.resources.pixel_shader);
  }
  if (materialized &&
      g_direct_renderer.diagnostics.logged_private_texture_header_materializations < 32u) {
    const uint32_t log_index =
        ++g_direct_renderer.diagnostics.logged_private_texture_header_materializations;
    if (exact_begin_vertices_title_strip_header) {
      REXLOG_INFO(
          "Refii plume exact BeginVertices title strip header texture "
          "identity #{} frame={} serial={} draw={} slot={} guest=0x{:08X} "
          "texture=0x{:08X} base=0x{:08X} {}x{} fmt={} "
          "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          log_index, g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.serial),
          static_cast<unsigned long long>(command.draw_serial), slot, guest,
          materialized->guest_address,
          PlumeGuestTextureBaseAddress(*materialized),
          PlumeGuestTextureWidth(*materialized),
          PlumeGuestTextureHeight(*materialized),
          static_cast<uint32_t>(PlumeGuestTextureFormat(*materialized)),
          fetch_dwords[0], fetch_dwords[1], fetch_dwords[2], fetch_dwords[3],
          fetch_dwords[4], fetch_dwords[5],
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    } else {
      REXLOG_INFO(
          "Refii plume materialized private texture header #{} frame={} "
          "slot={} guest=0x{:08X} base=0x{:08X} {}x{} fmt={} "
          "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          log_index, g_direct_renderer.current_frame_index, slot, guest,
          PlumeGuestTextureBaseAddress(*materialized),
          PlumeGuestTextureWidth(*materialized),
          PlumeGuestTextureHeight(*materialized),
          static_cast<uint32_t>(PlumeGuestTextureFormat(*materialized)),
          fetch_dwords[0], fetch_dwords[1], fetch_dwords[2], fetch_dwords[3],
          fetch_dwords[4], fetch_dwords[5],
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
  }
  return true;
}

void EnsureMissingDirectPrivateTextureHeaderResourcesForCommandLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command) {
  if (!device || command.draw.source == RendererDrawEventSource::kPixVideo) {
    return;
  }

  const bool title_or_private_command =
      command.draw.private_texture_rectangle ||
      IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
      IsFableTitleSceneVertexShaderCommandLocked(command);
  if (!title_or_private_command) {
    return;
  }

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
    PlumeTrackedResource* existing = FindDirectResourceLocked(guest);
    if (existing && PlumeGuestImageReady(*existing)) {
      continue;
    }
    EnsureDirectPrivateTextureHeaderResourceLocked(device, command, slot);
  }
}

void PreflightDirectPrivateRectCopyActionsLocked(
    plume::RenderDevice* device,
    const std::vector<FableDirectRenderCommand>& commands) {
  if (!device || commands.empty()) {
    return;
  }

  const FableDirectClock::time_point preflight_t0 = FableDirectClock::now();
  double header_ms = 0.0;
  double target_ms = 0.0;
  double source_check_ms = 0.0;
  double remember_ms = 0.0;
  uint32_t candidate_commands = 0;
  uint32_t target_ready_commands = 0;
  uint32_t source_slots_checked = 0;
  uint32_t source_slots_ready = 0;
  uint32_t remembered_actions = 0;

  for (const FableDirectRenderCommand& command : commands) {
    const bool draw_command = IsDirectDrawCommand(command);
    if (!draw_command || command.draw.source == RendererDrawEventSource::kPixVideo ||
        !IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader)) {
      continue;
    }
    if (DirectCommandHasExactSyntheticPrivateRectCopyInputsLocked(command)) {
      continue;
    }

    ++candidate_commands;
    const std::array<uint32_t, 2> action_slots = {
        kFablePrivateRectCopyTextureSlot,
        command.draw.private_texture_slot & (kMaxFableTextureSlots - 1u),
    };
    const uint32_t action_texture_slot_mask =
        DirectCommandPixelTextureSlotMaskLocked(command);
    auto for_each_unique_action_slot = [&](auto&& callback) {
      std::array<uint32_t, action_slots.size()> visited_action_slots = {};
      uint32_t visited_action_slot_count = 0;
      for (uint32_t slot : action_slots) {
        if (slot >= command.resources.textures.size()) {
          continue;
        }
        if (slot < 32u && action_texture_slot_mask != 0u &&
            (action_texture_slot_mask & (1u << slot)) == 0) {
          continue;
        }
        bool duplicate_slot = false;
        for (uint32_t i = 0; i < visited_action_slot_count; ++i) {
          if (visited_action_slots[i] == slot) {
            duplicate_slot = true;
            break;
          }
        }
        if (duplicate_slot) {
          continue;
        }
        visited_action_slots[visited_action_slot_count++] = slot;
        callback(slot);
      }
    };

    const FableDirectClock::time_point target_t0 = FableDirectClock::now();
    PlumeTrackedResource* target = nullptr;
    if (DirectSyntheticPrivateRectCommandUsesDepthTarget(command)) {
      target = FindDirectResourceLocked(command.resources.depth_stencil);
    } else {
      if (!EnsurePrivateRectRenderTargetSurfaceLocked(device, command, 0,
                                                     false, "preflight")) {
        target_ms += FableDirectElapsedMs(target_t0, FableDirectClock::now());
        continue;
      }
      target = FindDirectResourceLocked(command.resources.render_targets[0]);
    }
    target_ms += FableDirectElapsedMs(target_t0, FableDirectClock::now());
    if (target && IsImageResource(target->kind) &&
        PlumeGuestImageReady(*target) &&
        DirectCommandBindsResourceAsTargetLocked(command, *target)) {
      ++target_ready_commands;
      const FablePrivateTextureCopyKind kind =
          DirectSyntheticPrivateRectCopyKindForTarget(command, *target);
      for_each_unique_action_slot([&](uint32_t slot) {
        ++source_slots_checked;
        FableDirectClock::time_point source_t0 = FableDirectClock::now();
        const uint32_t source_guest = command.resources.textures[slot];
        if (source_guest == 0 || source_guest == target->guest_address) {
          source_check_ms +=
              FableDirectElapsedMs(source_t0, FableDirectClock::now());
          return;
        }
        PlumeTrackedResource* source = FindDirectResourceLocked(source_guest);
        const bool source_is_initially_ready =
            source && IsImageResource(source->kind) &&
            PlumeGuestImageReady(*source);
        if (!source_is_initially_ready &&
            DirectCommandUsesFablePrivateTexturePath(command, slot, source)) {
          source_check_ms +=
              FableDirectElapsedMs(source_t0, FableDirectClock::now());
          const FableDirectClock::time_point header_t0 = FableDirectClock::now();
          EnsureDirectPrivateTextureHeaderResourceLocked(device, command, slot);
          header_ms += FableDirectElapsedMs(header_t0, FableDirectClock::now());
          source_t0 = FableDirectClock::now();
          source = FindDirectResourceLocked(source_guest);
        }
        if (!source || source == target || !IsImageResource(source->kind) ||
            !PlumeGuestImageReady(*source) ||
            !DirectCommandUsesFablePrivateTexturePath(command, slot, source) ||
            DirectCommandBindsResourceAsTargetLocked(command, *source)) {
          source_check_ms +=
              FableDirectElapsedMs(source_t0, FableDirectClock::now());
          return;
        }
        source_check_ms +=
            FableDirectElapsedMs(source_t0, FableDirectClock::now());
        ++source_slots_ready;
        const FableDirectClock::time_point remember_t0 = FableDirectClock::now();
        RememberDirectPrivateTextureCopyActionLocked(command, slot, *source,
                                                    *target, kind, false);
        remember_ms += FableDirectElapsedMs(remember_t0, FableDirectClock::now());
        ++remembered_actions;
      });
    }

    if (g_direct_renderer.diagnostics.logged_private_rect_action_preflights < 32u) {
      const uint32_t slot =
          command.draw.private_texture_slot & (kMaxFableTextureSlots - 1u);
      const uint32_t tex0 = command.resources.textures[0];
      const uint32_t tex_slot =
          slot < command.resources.textures.size()
              ? command.resources.textures[slot]
              : 0u;
      REXLOG_INFO(
          "Refii plume private rect action preflight #{} frame={} "
          "serial={} draw={} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
          "ps=0x{:08X} slot={} tex0=0x{:08X} tex_slot=0x{:08X}",
          ++g_direct_renderer.diagnostics.logged_private_rect_action_preflights,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.serial),
          static_cast<unsigned long long>(command.draw_serial),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader, slot,
          tex0, tex_slot);
    }
  }
  const double total_ms =
      FableDirectElapsedMs(preflight_t0, FableDirectClock::now());
  if ((total_ms >= 5.0 || remembered_actions >= 16u) &&
      g_direct_renderer.diagnostics.logged_private_rect_preflight_timing_breakdowns < 128u) {
    REXLOG_INFO(
        "Refii plume private rect preflight timing #{} frame={} "
        "total_ms={:.3f} header_ms={:.3f} target_ms={:.3f} "
        "source_check_ms={:.3f} remember_ms={:.3f} commands={} "
        "candidates={} target_ready={} slots_checked={} slots_ready={} "
        "remembered={}",
        ++g_direct_renderer.diagnostics.logged_private_rect_preflight_timing_breakdowns,
        g_direct_renderer.current_frame_index, total_ms, header_ms, target_ms,
        source_check_ms, remember_ms, commands.size(), candidate_commands,
        target_ready_commands, source_slots_checked, source_slots_ready,
        remembered_actions);
  }
}

bool DirectPrivateTextureHeaderNeedsAlias(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (!texture ||
      !DirectCommandUsesFablePrivateTexturePath(command, slot, texture) ||
      texture->texture_render_target || texture->texture_depth_target) {
    return false;
  }

  const uint32_t guest =
      slot < command.resources.textures.size() ? command.resources.textures[slot]
                                               : 0u;
  const bool title_private_proxy_header =
      IsFableTitlePrivateTextureProxyAddress(guest) ||
      IsFableTitlePrivateTextureProxyAddress(texture->guest_address) ||
      IsFableTitlePrivateTextureProxyAddress(
          PlumeGuestTextureBaseAddress(*texture));
  const bool native_private_copy_proxy =
      IsFablePrivateTextureCopyProxyAddress(guest) ||
      IsFablePrivateTextureCopyProxyAddress(texture->guest_address) ||
      IsFablePrivateTextureCopyProxyAddress(
          PlumeGuestTextureBaseAddress(*texture));
  const bool mutable_private_chain_header =
      (IsFableMutablePrivateTextureHeaderAddress(guest) ||
       IsFableMutablePrivateTextureHeaderAddress(texture->guest_address) ||
       IsFableMutablePrivateTextureHeaderAddress(
           PlumeGuestTextureBaseAddress(*texture))) &&
      IsFablePrivateTextureChainExtent(PlumeGuestTextureWidth(*texture),
                                       PlumeGuestTextureHeight(*texture));
  const bool uploaded_title_scaler_copy_source =
      native_private_copy_proxy && command.draw.private_texture_rectangle &&
      IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader) &&
      IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) &&
      slot == source_slot && PlumeGuestTextureBaseAddress(*texture) != 0 &&
      // Display-sized scaler copy source: tracks the live guest dims.
      PlumeGuestTextureWidth(*texture) == FableGuestDisplayWidth() &&
      PlumeGuestTextureHeight(*texture) == FableGuestDisplayHeight() &&
      (FableTrackedResourceHasMeaningfulTextureUpload(*texture) ||
       texture->content_serial != 0 || texture->upload_generation != 0);
  if (uploaded_title_scaler_copy_source) {
    return false;
  }
  if (native_private_copy_proxy) {
    return true;
  }
  if (FableTrackedResourceHasMeaningfulTextureUpload(*texture) ||
      FableTrackedResourceHasSampleableContent(*texture) ||
      FableTrackedResourceHasGpuProducedContent(*texture)) {
    return false;
  }
  const FableTextureIdentity* texture_identity =
      FableDirectFindTextureIdentityByObject(texture->guest_address);
  if (title_private_proxy_header && texture_identity &&
      texture_identity->base_address != 0 && texture->content_serial != 0 &&
      PlumeGuestTextureWidth(*texture) <= 64u &&
      PlumeGuestTextureHeight(*texture) <= 64u) {
    return false;
  }
  if (title_private_proxy_header &&
      PlumeGuestTextureBaseAddress(*texture) != 0 &&
      texture->content_serial != 0 && !mutable_private_chain_header) {
    return false;
  }
  if (title_private_proxy_header) {
    return true;
  }
  return IsFablePrivateTextureChainResource(*texture) &&
         !FableTrackedResourceIsBlankUploadedSampleProxy(*texture);
}

bool DirectPrivateTextureCopyActionShouldPreferFetchLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (slot >= command.resources.textures.size()) {
    return false;
  }
  const uint32_t guest = command.resources.textures[slot];
  return IsFableMutablePrivateTextureHeaderAddress(guest) ||
         IsFableTitlePrivateTextureProxyAddress(guest) ||
         (texture &&
          (IsFableMutablePrivateTextureHeaderAddress(texture->guest_address) ||
           IsFableTitlePrivateTextureProxyAddress(texture->guest_address) ||
           IsFableTitlePrivateTextureProxyAddress(texture->parent_address) ||
           IsFableMutablePrivateTextureHeaderAddress(
               PlumeGuestTextureBaseAddress(*texture)) ||
           IsFableTitlePrivateTextureProxyAddress(
               PlumeGuestTextureBaseAddress(*texture))));
}

uint64_t DirectPrivateTextureCopySerialLocked() {
  const FableGuestResourceRegistry& registry =
      FableDirectGuestResourceRegistry();
  return registry.private_texture_copy_serial ^
         (registry.private_texture_copy_index_serial * 1099511628211ull);
}

uint64_t DirectPrivateTextureCopyActionCacheKeyLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  size_t seed = 0;
  const uint32_t guest =
      slot < command.resources.textures.size() ? command.resources.textures[slot]
                                               : 0u;
  const FableGuestResourceRegistry& registry =
      FableDirectGuestResourceRegistry();
  DirectHashCombine(seed, std::hash<uint64_t>{}(
                        registry.private_texture_copy_serial));
  DirectHashCombine(seed, std::hash<uint64_t>{}(
                        registry.private_texture_copy_index_serial));
  DirectHashCombine(seed,
              std::hash<uint64_t>{}(g_direct_renderer.private_texture_alias_serial));
  // Keep this as a stable state/action key. Per-command serials made the cache
  // effectively one-shot, while the registry/resource serials above already
  // invalidate stale action pointers when the producer graph changes.
  DirectHashCombine(seed, std::hash<uint32_t>{}(slot));
  DirectHashCombine(seed, std::hash<uint32_t>{}(guest));
  DirectHashCombine(seed, std::hash<uint32_t>{}(command.resources.render_targets[0]));
  DirectHashCombine(seed, std::hash<uint32_t>{}(command.resources.depth_stencil));
  DirectHashCombine(seed, std::hash<uint32_t>{}(command.resources.vertex_shader));
  DirectHashCombine(seed, std::hash<uint32_t>{}(command.resources.pixel_shader));
  DirectHashCombine(seed, std::hash<uint64_t>{}(
                        FableDirectTextureFetchKey(
                            slot < command.resources.textures.size()
                                ? DirectCommandTextureFetchDwords(command, slot)
                                : std::array<uint32_t, 6>{})));
  DirectHashCombine(seed, std::hash<uint32_t>{}(static_cast<uint32_t>(
                        command.draw.source)));
  DirectHashCombine(seed, std::hash<bool>{}(command.draw.private_texture_rectangle));
  DirectHashCombine(seed, std::hash<bool>{}(command.draw.private_texture_uv_override));
  DirectHashCombine(seed, std::hash<uint64_t>{}(
                        command.draw.private_texture_wrapper_call_serial));
  DirectHashCombine(seed, std::hash<uint64_t>{}(
                        command.draw.private_texture_wrapper_context_key));
  DirectHashCombine(seed, std::hash<uint32_t>{}(
                        command.draw.private_texture_wrapper_symbol_address));
  if (texture) {
    DirectHashCombine(seed, std::hash<uint32_t>{}(texture->guest_address));
    DirectHashCombine(seed, std::hash<uint32_t>{}(texture->parent_address));
    DirectHashCombine(seed,
                std::hash<uint32_t>{}(PlumeGuestTextureBaseAddress(*texture)));
    DirectHashCombine(seed,
                std::hash<uint32_t>{}(PlumeGuestTextureWidth(*texture)));
    DirectHashCombine(seed,
                std::hash<uint32_t>{}(PlumeGuestTextureHeight(*texture)));
    DirectHashCombine(seed,
                std::hash<uint64_t>{}(texture->content_serial));
    DirectHashCombine(seed,
                std::hash<uint64_t>{}(texture->upload_generation));
    DirectHashCombine(seed,
                std::hash<uint64_t>{}(texture->draw_content_serial));
    DirectHashCombine(seed, std::hash<uint64_t>{}(
                          FableDirectTextureFetchKey(
                              PlumeGuestTextureFetchDwords(*texture))));
  }
  return seed != 0 ? static_cast<uint64_t>(seed) : 1ull;
}

// ZERO-CALLER DirectPrivateTextureExactInterestCacheKeyLocked — DELETED
// 2026-07-26. It hashed a command/slot/texture tuple into a private-texture
// "exact interest" cache key. Compiler-verified unused (-Wunused-function)
// plus grep-verified: exactly ONE occurrence repo-wide, the definition, with
// no forward declaration and no overloads. The sibling
// DirectPrivateTextureCopyActionSampleCacheKeyLocked below IS live.
uint64_t DirectPrivateTextureCopyActionSampleCacheKeyLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture,
    const FablePrivateTextureCopyAction& action) {
  size_t seed = 0;
  const uint32_t guest =
      slot < command.resources.textures.size() ? command.resources.textures[slot]
                                               : 0u;
  DirectHashCombine(seed, std::hash<uint64_t>{}(command.serial));
  DirectHashCombine(seed, std::hash<uint64_t>{}(command.draw_serial));
  DirectHashCombine(seed, std::hash<uint32_t>{}(g_direct_renderer.current_frame_index));
  DirectHashCombine(seed, std::hash<uint32_t>{}(slot));
  DirectHashCombine(seed, std::hash<uint32_t>{}(guest));
  DirectHashCombine(seed, std::hash<uint64_t>{}(action.serial));
  DirectHashCombine(seed, std::hash<uint32_t>{}(action.source_texture));
  DirectHashCombine(seed, std::hash<uint32_t>{}(action.backing_texture));
  DirectHashCombine(seed, std::hash<uint32_t>{}(action.target_surface));
  DirectHashCombine(seed, std::hash<uint32_t>{}(action.render_target));
  DirectHashCombine(seed, std::hash<uint32_t>{}(action.depth_stencil));
  DirectHashCombine(seed, std::hash<uint32_t>{}(action.sampled_source.resource));
  DirectHashCombine(seed, std::hash<uint32_t>{}(
                        static_cast<uint32_t>(action.sampled_source.kind)));
  if (texture) {
    DirectHashCombine(seed, std::hash<uint32_t>{}(texture->guest_address));
    DirectHashCombine(seed, std::hash<uint32_t>{}(texture->parent_address));
    DirectHashCombine(seed,
                std::hash<uint32_t>{}(PlumeGuestTextureBaseAddress(*texture)));
    DirectHashCombine(seed, std::hash<uint64_t>{}(texture->content_serial));
    DirectHashCombine(seed, std::hash<uint64_t>{}(texture->upload_generation));
    DirectHashCombine(seed, std::hash<uint64_t>{}(texture->draw_content_serial));
  }
  return seed != 0 ? static_cast<uint64_t>(seed) : 1ull;
}

const FablePrivateTextureCopyAction*
SelectDirectExactPrivateTextureCopyActionForCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture,
    DirectPrivateTextureCopyActionMatch* action_match) {
  if (action_match) {
    *action_match = DirectPrivateTextureCopyActionMatch::kNone;
  }
  if (slot >= command.resources.textures.size()) {
    return nullptr;
  }

  const uint64_t cache_key =
      DirectPrivateTextureCopyActionCacheKeyLocked(command, slot, texture);
  const uint64_t private_copy_serial =
      DirectPrivateTextureCopySerialLocked();
  const auto cached_it =
      g_direct_renderer.private_texture_copy_action_cache.find(cache_key);
  if (cached_it != g_direct_renderer.private_texture_copy_action_cache.end() &&
      cached_it->second.private_copy_serial == private_copy_serial) {
    if (action_match) {
      *action_match = cached_it->second.match;
    }
    return cached_it->second.action;
  }

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
  const FablePrivateTextureCopyAction* action = nullptr;
  DirectPrivateTextureCopyActionMatch match =
      DirectPrivateTextureCopyActionMatch::kNone;
  auto exact_private_link_matches_command_texture_identity =
      [&](const FableSurfaceTextureLink& link) {
    const uint64_t link_fetch_key =
        FableDirectTextureFetchKey(link.texture_fetch_dwords);
    auto matches_guest = [&](uint32_t guest_address) {
      if (guest_address == 0) {
        return false;
      }
      return DirectResourceAddressMatchesLocked(link.texture, guest_address) ||
             DirectResourceAddressMatchesLocked(link.backing_texture,
                                                guest_address) ||
             DirectResourceAddressMatchesLocked(link.texture_base_address,
                                                guest_address) ||
             DirectResourceAddressMatchesLocked(link.backing_base_address,
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
        (link.texture_base_address == command_fetch_info.base_address ||
         link.backing_base_address == command_fetch_info.base_address)) {
      return true;
    }
    if (texture_base != 0 &&
        (link.texture_base_address == texture_base ||
         link.backing_base_address == texture_base)) {
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
  auto try_current_color_source_action = [&]() -> bool {
    const uint32_t current_rt = command.resources.render_targets[0];
    if (current_rt == 0 ||
        command.draw.source == RendererDrawEventSource::kPixVideo ||
        !(command.draw.source ==
              RendererDrawEventSource::kPrivateTextureRectangle ||
          command.draw.private_texture_rectangle ||
          IsFableSyntheticPrivateRectCopyShader(
              command.resources.pixel_shader))) {
      return false;
    }

    const uint64_t command_draw_serial =
        command.draw_serial != 0 ? command.draw_serial : command.serial;

    auto exact_address_match = [](uint32_t lhs, uint32_t rhs) {
      return lhs != 0 && lhs == rhs;
    };
    auto action_source_matches =
        [&](const FablePrivateTextureCopyAction& candidate) {
          if (exact_address_match(candidate.source_texture, guest)) {
            return true;
          }
          if (exact_address_match(candidate.backing_texture, guest)) {
            return true;
          }
          if (!texture) {
            return false;
          }
          return exact_address_match(candidate.source_texture,
                                     texture->guest_address) ||
                 exact_address_match(candidate.source_texture,
                                     texture->parent_address) ||
                 exact_address_match(candidate.backing_texture,
                                     texture->guest_address) ||
                 exact_address_match(candidate.backing_texture,
                                     texture->parent_address);
        };
    auto action_is_visible =
        [&](const FablePrivateTextureCopyAction& candidate) {
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
          constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
          if (candidate.source_slot == kUnknownSlot ||
              candidate.source_slot == slot) {
            return true;
          }
          return DirectPrivateTextureCopyActionWrapperMatchesCommand(
                     command, candidate) &&
                 action_source_matches(candidate);
        };
    auto action_targets_current_color =
        [&](const FablePrivateTextureCopyAction& candidate) {
          return candidate.kind != FablePrivateTextureCopyKind::kDepth &&
                 ((candidate.target_surface != 0 &&
                   candidate.target_surface == current_rt) ||
                  (candidate.render_target != 0 &&
                   candidate.render_target == current_rt));
        };
    auto action_belongs_to_current_draw =
        [&](const FablePrivateTextureCopyAction& candidate) {
          if (candidate.draw_serial != 0 && command_draw_serial != 0 &&
              candidate.draw_serial == command_draw_serial) {
            return true;
          }
          return candidate.frame_index == g_direct_renderer.current_frame_index &&
                 DirectVertexShaderObjectsEquivalentLocked(
                     command.resources.vertex_shader, candidate.vertex_shader) &&
                 DirectPixelShaderObjectsEquivalentLocked(
                     command.resources.pixel_shader, candidate.pixel_shader) &&
                 (candidate.render_target == 0 ||
                  candidate.render_target == current_rt) &&
                 (candidate.depth_stencil == 0 ||
                  candidate.depth_stencil == command.resources.depth_stencil);
        };

    auto consider_candidates =
        [&](const std::vector<FablePrivateTextureCopyAction>* candidates)
        -> const FablePrivateTextureCopyAction* {
          if (!candidates) {
            return nullptr;
          }
          for (auto it = candidates->rbegin(); it != candidates->rend(); ++it) {
            const FablePrivateTextureCopyAction& candidate = *it;
            if (!action_is_visible(candidate) ||
                !action_targets_current_color(candidate) ||
                !action_source_matches(candidate) ||
                !action_belongs_to_current_draw(candidate)) {
              continue;
            }
            return &candidate;
          }
          return nullptr;
        };

    const FablePrivateTextureCopyAction* selected = consider_candidates(
        FableDirectFindPrivateTextureCopyActionsByTarget(current_rt));
    if (!selected) {
      return false;
    }
    action = selected;
    match = DirectPrivateTextureCopyActionMatch::kExactCurrentColorAction;
    return true;
  };

  auto try_exact_private_title_parent_action = [&]() -> bool {
    if (!texture || texture->parent_address == 0 ||
        texture->parent_address == texture->guest_address ||
        command.draw.source == RendererDrawEventSource::kPixVideo) {
      return false;
    }
    const bool title_or_private_command =
        DirectCommandUsesFablePrivateTexturePath(command, slot, texture) ||
        IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
        IsFableTitleSceneVertexShaderCommandLocked(command);
    if (!title_or_private_command) {
      return false;
    }
    const uint32_t texture_base = PlumeGuestTextureBaseAddress(*texture);
    const bool private_title_identity =
        IsFableTitlePrivateTextureProxyAddress(guest) ||
        IsFableMutablePrivateTextureHeaderAddress(guest) ||
        IsFableTitlePrivateTextureProxyAddress(texture->guest_address) ||
        IsFableMutablePrivateTextureHeaderAddress(texture->guest_address) ||
        IsFableTitlePrivateTextureProxyAddress(texture->parent_address) ||
        IsFableMutablePrivateTextureHeaderAddress(texture->parent_address) ||
        IsFableTitlePrivateTextureProxyAddress(texture_base) ||
        IsFableMutablePrivateTextureHeaderAddress(texture_base);
    if (!private_title_identity) {
      return false;
    }

    const uint64_t command_draw_serial =
        command.draw_serial != 0 ? command.draw_serial : command.serial;

    auto source_identity_rank =
        [&](const FablePrivateTextureCopyAction& candidate) -> uint32_t {
      if (candidate.source_texture == guest) {
        return 12u;
      }
      if (candidate.source_texture == texture->guest_address ||
          candidate.source_texture == texture->parent_address) {
        return 9u;
      }
      if (candidate.backing_texture == guest) {
        return 6u;
      }
      if (candidate.backing_texture == texture->guest_address ||
          candidate.backing_texture == texture->parent_address) {
        return 4u;
      }
      return 0u;
    };
    auto candidate_visible =
        [&](const FablePrivateTextureCopyAction& candidate) {
      if (candidate.frame_index != 0 &&
          candidate.frame_index > g_direct_renderer.current_frame_index) {
        return false;
      }
      return !(candidate.frame_index != 0 &&
               candidate.frame_index == g_direct_renderer.current_frame_index &&
               candidate.draw_serial != 0 && command_draw_serial != 0 &&
               candidate.draw_serial > command_draw_serial);
    };
    uint32_t selected_identity_rank = 0;
    auto consider_candidates =
        [&](const std::vector<FablePrivateTextureCopyAction>* candidates)
        -> const FablePrivateTextureCopyAction* {
      if (!candidates) {
        return nullptr;
      }
      for (auto it = candidates->rbegin(); it != candidates->rend(); ++it) {
        const FablePrivateTextureCopyAction& candidate = *it;
        if (!candidate_visible(candidate) ||
            candidate.kind == FablePrivateTextureCopyKind::kDepth ||
            (candidate.target_surface != texture->parent_address &&
             candidate.render_target != texture->parent_address)) {
          continue;
        }
        const uint32_t identity_rank = source_identity_rank(candidate);
        if (identity_rank == 0u) {
          continue;
        }
        const PlumeTrackedResource* target =
            FindDirectResourceLocked(candidate.target_surface);
        if (!target || !IsImageResource(target->kind) ||
            DirectCommandBindsResourceAsTargetLocked(command, *target)) {
          continue;
        }
        const bool target_has_registered_content =
            (PlumeGuestImageReady(*target) &&
             FableTrackedResourceHasGpuProducedContent(*target)) ||
            candidate.target_content_serial != 0 ||
            candidate.sampled_source.kind ==
                FableRegisteredSampleSourceKind::kGpuSurface;
        if (!target_has_registered_content) {
          continue;
        }
        selected_identity_rank = identity_rank;
        return &candidate;
      }
      return nullptr;
    };

    const FablePrivateTextureCopyAction* selected = consider_candidates(
        FableDirectFindPrivateTextureCopyActionsByTarget(
            texture->parent_address));
    if (!selected) {
      return false;
    }
    action = selected;
    match = DirectPrivateTextureCopyActionMatch::kExactParentDestination;
    return true;
  };
  auto try_surface_texture_link_destination_actions = [&]() -> bool {
		    constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
		    auto link_matches_command_slot =
		        [&](const FableSurfaceTextureLink& link) -> bool {
		      if (!link.trusted_destination_edge ||
		          command.draw.source == RendererDrawEventSource::kPixVideo ||
		          link.frame_index > g_direct_renderer.current_frame_index) {
		        return false;
		      }
		      if (link.source_slot != slot) {
		        if (link.source_slot != kUnknownSlot ||
		            !DirectSurfaceTextureLinkHasExactCopyIdentity(link) ||
		            !DirectSurfaceTextureLinkTouchesFablePrivateChain(link,
		                                                             texture) ||
		            !exact_private_link_matches_command_texture_identity(link)) {
		          return false;
		        }
		      }
		      if (link.frame_index == g_direct_renderer.current_frame_index &&
		          link.draw_serial != 0 && command.draw_serial != 0 &&
		          link.draw_serial > command.draw_serial) {
		        return false;
		      }
		      if (link.texture == guest || link.backing_texture == guest ||
		          link.source_surface == guest) {
		        return true;
		      }
		      if (texture) {
		        if (link.texture == texture->guest_address ||
		            link.texture == texture->parent_address ||
		            link.backing_texture == texture->guest_address ||
		            link.backing_texture == texture->parent_address ||
		            link.source_surface == texture->guest_address ||
		            link.source_surface == texture->parent_address) {
		          return true;
		        }
		      }
		      if (const FableTextureIdentity* identity =
		              FableDirectFindTextureIdentityByObject(guest)) {
		        return link.texture == identity->object_texture ||
		               link.texture == identity->parent_texture ||
		               link.backing_texture == identity->object_texture ||
		               link.backing_texture == identity->parent_texture ||
		               link.source_surface == identity->object_texture ||
		               link.source_surface == identity->parent_texture;
		      }
		      return false;
		    };
		    auto try_promote_link_action =
		        [&](const FableSurfaceTextureLink& link) -> bool {
		      if (!link_matches_command_slot(link)) {
		        return false;
		      }
		      PlumeTrackedResource* sample =
		          ResolveDirectSurfaceTextureLinkResourceLocked(command, link);
		      const bool native_depth_current_target_edge =
		          link.kind == FablePrivateTextureCopyKind::kDepth &&
		          link.copy_symbol_address ==
		              kFablePrivateRectNativeDepthWrapperSymbol &&
		          DirectSyntheticPrivateRectCommandUsesDepthTarget(command) &&
		          DirectSurfaceTextureLinkIsCurrentTargetExactEdgeLocked(
		              command, link);
		      if (!sample &&
		          DirectSurfaceTextureLinkIsCurrentTargetExactEdgeLocked(
		              command, link) &&
		          (link.kind != FablePrivateTextureCopyKind::kDepth ||
		           native_depth_current_target_edge)) {
		        PlumeTrackedResource* source =
		            FindDirectResourceLocked(link.source_surface);
		        PlumeTrackedResource* destination =
		            FindDirectResourceLocked(link.texture);
		        if (!source || !destination || !IsImageResource(source->kind) ||
		            !IsImageResource(destination->kind) ||
		            !PlumeGuestImageReady(*source) ||
		            !PlumeGuestImageReady(*destination)) {
		          return false;
		        }
		        sample = source;
		        static uint32_t logged_promoted_current_target_link_actions = 0;
		        if (logged_promoted_current_target_link_actions < 32u) {
		          REXLOG_INFO(
		              "Refii plume promoted current-target surface link action "
		              "#{} frame={} slot={} texture=0x{:08X} "
		              "surface=0x{:08X}/{}x{} target=0x{:08X}/{}x{} "
			              "call={} context=0x{:016X} wrapper_source=0x{:08X}/{} "
			              "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
                      "link_vs_root=0x{:08X}/{} link_ps_root=0x{:08X}/{} "
                      "cmd_vs_root=0x{:08X}/{} cmd_ps_root=0x{:08X}/{}",
			              ++logged_promoted_current_target_link_actions,
			              g_direct_renderer.current_frame_index, slot, link.texture,
			              source->guest_address, PlumeGuestTextureWidth(*source),
		              PlumeGuestTextureHeight(*source),
		              destination->guest_address,
		              PlumeGuestTextureWidth(*destination),
		              PlumeGuestTextureHeight(*destination),
			              static_cast<unsigned long long>(link.copy_call_serial),
			              static_cast<unsigned long long>(link.copy_context_key),
			              link.wrapper_source_texture,
			              link.wrapper_source_exact ? 1 : 0,
			              command.resources.render_targets[0],
			              command.resources.depth_stencil,
		              command.resources.vertex_shader,
		              command.resources.pixel_shader,
                  link.vertex_shader_root_symbol_address,
                  static_cast<unsigned long long>(
                      link.vertex_shader_root_call_serial),
                  link.pixel_shader_root_symbol_address,
                  static_cast<unsigned long long>(
                      link.pixel_shader_root_call_serial),
                  command.resources.shader_roots.vertex_shader.symbol_address,
                  static_cast<unsigned long long>(
                      command.resources.shader_roots.vertex_shader.call_serial),
                  command.resources.shader_roots.pixel_shader.symbol_address,
                  static_cast<unsigned long long>(
                      command.resources.shader_roots.pixel_shader.call_serial));
		        }
		      }
		      if (!sample) {
		        return false;
		      }

		      FablePrivateTextureCopyAction promoted;
		      promoted.source_slot = slot;
		      promoted.source_texture = link.texture != 0 ? link.texture : guest;
		      promoted.source_base_address = link.texture_base_address;
		      promoted.source_width = link.texture_width != 0
		                                  ? link.texture_width
		                                  : PlumeGuestTextureWidth(*sample);
		      promoted.source_height = link.texture_height != 0
		                                   ? link.texture_height
		                                   : PlumeGuestTextureHeight(*sample);
		      promoted.source_format = link.texture_format;
		      promoted.source_fetch_dwords = link.texture_fetch_dwords;
		      promoted.backing_texture = link.backing_texture;
		      promoted.backing_base_address = link.backing_base_address;
		      promoted.backing_fetch_dwords = link.backing_fetch_dwords;
		      promoted.backing_content_serial = link.backing_content_serial;
		      if (promoted.backing_texture == 0 &&
		          sample->guest_address != link.source_surface) {
		        promoted.backing_texture = sample->guest_address;
		        promoted.backing_base_address =
		            PlumeGuestTextureBaseAddress(*sample);
		        promoted.backing_width = PlumeGuestTextureWidth(*sample);
		        promoted.backing_height = PlumeGuestTextureHeight(*sample);
		        promoted.backing_format = PlumeGuestTextureGuestFormat(*sample);
		        promoted.backing_fetch_dwords = PlumeGuestTextureFetchDwords(*sample);
		        promoted.backing_content_serial =
		            FableTrackedResourceVisibleContentSerial(*sample);
		      }
		      promoted.target_surface = link.source_surface;
		      if (const PlumeTrackedResource* target_resource =
		              FindDirectResourceLocked(link.source_surface)) {
		        promoted.target_width = PlumeGuestTextureWidth(*target_resource);
		        promoted.target_height = PlumeGuestTextureHeight(*target_resource);
		        promoted.target_format =
		            PlumeGuestTextureGuestFormat(*target_resource);
		      } else {
		        promoted.target_width = PlumeGuestTextureWidth(*sample);
		        promoted.target_height = PlumeGuestTextureHeight(*sample);
		        promoted.target_format = PlumeGuestTextureGuestFormat(*sample);
		      }
		      promoted.target_content_serial =
		          link.target_content_serial != 0
		              ? link.target_content_serial
		              : FableTrackedResourceVisibleContentSerial(*sample);
		      if (sample->guest_address != promoted.target_surface) {
		        PopulateDirectPrivateTextureCopyBackingLocked(promoted, *sample);
		      } else {
		        promoted.sampled_source.kind =
		            FableRegisteredSampleSourceKind::kGpuSurface;
		        promoted.sampled_source.resource = promoted.target_surface;
		        promoted.sampled_source.content_serial =
		            promoted.target_content_serial;
		      }
		      promoted.render_target = link.render_target;
		      promoted.depth_stencil = link.depth_stencil;
		      promoted.vertex_shader =
		          link.vertex_shader != 0 ? link.vertex_shader
		                                  : command.resources.vertex_shader;
		      promoted.pixel_shader =
		          link.pixel_shader != 0 ? link.pixel_shader
		                                 : command.resources.pixel_shader;
		      promoted.wrapper_call_serial = link.copy_call_serial;
		      promoted.wrapper_context_key = link.copy_context_key;
		      promoted.wrapper_symbol_address = link.copy_symbol_address;
		      promoted.wrapper_source_texture = link.wrapper_source_texture;
		      promoted.wrapper_source_exact = link.wrapper_source_exact;
		      promoted.wrapper_setup_globals = link.wrapper_setup_globals;
		      promoted.frame_index = link.frame_index;
		      promoted.draw_serial = link.draw_serial;
		      promoted.kind = link.kind;
		      RegisterDirectPrivateTextureCopyActionLocked(promoted);
		      RememberDirectPrivateRectExactActionLocked(promoted);

		      const FablePrivateTextureCopyAction* registered =
		          FableDirectFindVisiblePrivateTextureCopyActionForDestinationTextureSlot(
		              promoted.source_texture, slot,
		              g_direct_renderer.current_frame_index, command.draw_serial);
		      if (!registered) {
		        registered =
		            FableDirectFindVisiblePrivateTextureCopyActionForDestinationTexture(
		                promoted.source_texture, g_direct_renderer.current_frame_index,
		                command.draw_serial, slot);
		      }
		      if (!registered) {
		        registered =
		            FableDirectFindPrivateTextureCopyActionForDestinationTextureSlot(
		                promoted.source_texture, slot);
		      }
		      if (!registered) {
		        registered =
		            FableDirectFindPrivateTextureCopyActionForDestinationTexture(
		                promoted.source_texture);
		      }
		      if (!registered) {
		        return false;
		      }
		      action = registered;
		      match = DirectPrivateTextureCopyActionMatch::kExactSurfaceTextureLink;
		      return true;
		    };
		    auto try_native_depth_pending_link_action =
		        [&](const FableSurfaceTextureLink& link) -> bool {
		      const bool native_depth_consumer =
		          IsFableTitleSceneTextureSyncCommandLocked(command) ||
		          (DirectSyntheticPrivateRectCommandUsesDepthTarget(command) &&
		           DirectSurfaceTextureLinkIsCurrentTargetExactEdgeLocked(
		               command, link));
		      if (!link_matches_command_slot(link) ||
		          !native_depth_consumer ||
		          !link.trusted_destination_edge ||
		          !DirectSurfaceTextureLinkHasExactCopyIdentity(link) ||
		          link.kind != FablePrivateTextureCopyKind::kDepth ||
		          link.copy_symbol_address !=
		              kFablePrivateRectNativeDepthWrapperSymbol ||
		          FablePrivateTextureProxyRoleForAddress(link.texture) !=
		              FablePrivateTextureProxyRole::kDepth ||
		          link.source_surface == 0 || link.copy_call_serial == 0 ||
		          link.copy_context_key == 0) {
		        return false;
		      }

		      auto action_matches_link =
		          [&](const FablePrivateTextureCopyAction& candidate) {
		        if (candidate.kind != FablePrivateTextureCopyKind::kDepth) {
		          return false;
		        }
		        const bool source_matches =
		            DirectResourceAddressMatchesLocked(candidate.source_texture,
		                                               link.texture) ||
		            DirectResourceAddressMatchesLocked(
		                candidate.wrapper_source_texture, link.texture);
		        const bool target_matches =
		            DirectResourceAddressMatchesLocked(candidate.target_surface,
		                                               link.source_surface) ||
		            DirectResourceAddressMatchesLocked(candidate.depth_stencil,
		                                               link.source_surface) ||
		            DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
		                link, link.source_surface, candidate.target_surface) ||
		            DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
		                link, link.source_surface, candidate.depth_stencil);
		        if (!source_matches || !target_matches) {
		          return false;
		        }
		        if (candidate.wrapper_call_serial != 0 &&
		            candidate.wrapper_call_serial != link.copy_call_serial) {
		          return false;
		        }
		        if (candidate.wrapper_context_key != 0 &&
		            candidate.wrapper_context_key != link.copy_context_key) {
		          return false;
		        }
		        return candidate.wrapper_symbol_address == 0 ||
		               FablePrivateRectWrapperSymbolsCompatible(
		                   candidate.wrapper_symbol_address,
		                   link.copy_symbol_address);
		      };

		      const FablePrivateTextureCopyAction* selected = nullptr;
		      if (const FablePrivateTextureCopyAction* candidate =
		              FableDirectFindPrivateTextureCopyActionByWrapperCall(
		                  link.copy_call_serial);
		          candidate && action_matches_link(*candidate)) {
		        selected = candidate;
		      }
		      if (!selected) {
		        return false;
		      }

		      action = selected;
		      match = DirectPrivateTextureCopyActionMatch::kExactSurfaceTextureLink;
		      static uint32_t logged_native_depth_link_action_selects = 0;
		      if (logged_native_depth_link_action_selects < 32u) {
		        REXLOG_INFO(
		            "Refii plume exact native-depth link action select "
		            "#{} frame={} slot={} texture=0x{:08X} "
		            "surface=0x{:08X} call={} context=0x{:016X} "
		            "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
		            ++logged_native_depth_link_action_selects,
		            g_direct_renderer.current_frame_index, slot, link.texture,
		            link.source_surface,
		            static_cast<unsigned long long>(link.copy_call_serial),
		            static_cast<unsigned long long>(link.copy_context_key),
		            command.resources.render_targets[0],
		            command.resources.depth_stencil,
		            command.resources.vertex_shader,
		            command.resources.pixel_shader);
		      }
		      return true;
		    };
		    auto try_link_action = [&](const FableSurfaceTextureLink& link) -> bool {
		      if (!link_matches_command_slot(link)) {
		        return false;
		      }
		      if (try_native_depth_pending_link_action(link)) {
		        return true;
		      }
		      if (try_promote_link_action(link)) {
		        return true;
		      }
	      return false;
	    };
            auto try_destination_link =
                [&](uint32_t destination_texture) -> bool {
              const FableSurfaceTextureLink *link =
                  FableDirectFindSurfaceTextureLinkForDestinationTexture(
                      destination_texture);
              if (link && try_link_action(*link)) {
                return true;
              }
              const std::vector<FableSurfaceTextureLink> *links =
                  FableDirectFindSurfaceTextureLinksForDestinationTexture(
                      destination_texture);
              if (!links) {
                return false;
              }
              for (auto it = links->rbegin(); it != links->rend(); ++it) {
                if (try_link_action(*it)) {
                  return true;
                }
              }
              return false;
            };

            if (try_destination_link(guest)) {
	      return true;
	    }

	    if (!texture) {
	      return false;
	    }
	    return try_destination_link(texture->guest_address) ||
	           try_destination_link(texture->parent_address);
	  };

  const bool prefer_fetch_actions =
      DirectPrivateTextureCopyActionShouldPreferFetchLocked(command, slot,
                                                           texture);
  const uint32_t command_texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(command);
  const bool declared_texture_slot =
      command_texture_slot_mask == 0u ||
      DirectCommandUsesTextureSlotForSyncLocked(command,
                                                command_texture_slot_mask,
                                                slot);
  const bool require_exact_private_title_action =
      DirectCommandRequiresExactPrivateTitleCopyActionLocked(
          command, slot, texture, prefer_fetch_actions);
  const bool empty_nonprivate_title_slot =
      guest == 0 &&
      !(command_fetch_info.valid &&
        (IsFableTitlePrivateTextureProxyAddress(
             command_fetch_info.base_address) ||
         IsFableMutablePrivateTextureHeaderAddress(
             command_fetch_info.base_address))) &&
      !(texture &&
        (IsFableTitlePrivateTextureProxyAddress(texture->guest_address) ||
         IsFableMutablePrivateTextureHeaderAddress(texture->guest_address) ||
         IsFableTitlePrivateTextureProxyAddress(texture->parent_address) ||
         IsFableMutablePrivateTextureHeaderAddress(texture->parent_address) ||
         IsFableTitlePrivateTextureProxyAddress(
             PlumeGuestTextureBaseAddress(*texture)) ||
         IsFableMutablePrivateTextureHeaderAddress(
             PlumeGuestTextureBaseAddress(*texture))));
  if (try_exact_private_title_parent_action()) {
    // Exact parent surface/texture graph action won.
  } else if (try_current_color_source_action()) {
    // Exact current render-target producer action won.
  } else if (try_surface_texture_link_destination_actions()) {
    // Trusted surface/texture graph action won.
  } else if (require_exact_private_title_action &&
             !empty_nonprivate_title_slot) {
    const PlumeTrackedResource* snapshot_source = texture;
    if (!snapshot_source && guest != 0) {
      snapshot_source = FindDirectResourceLocked(guest);
    }
    if (slot < 3u &&
        DirectCommandLooksLikeBinkIndexedWrapperCandidateLocked(command)) {
      static uint32_t logged_exact_bink_plane_resolver_waits = 0;
      if (logged_exact_bink_plane_resolver_waits < 32u) {
        REXLOG_INFO(
            "Refii plume exact Bink wrapper plane owns private/title "
            "resolver wait #{} frame={} slot={} guest=0x{:08X} "
            "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_bink_plane_resolver_waits,
            g_direct_renderer.current_frame_index, slot, guest,
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
	    } else if (DirectCommandCanSnapshotPrivateTextureFeedbackLocked(
	            command, slot, snapshot_source)) {
	      static uint32_t logged_exact_snapshot_action_resolver_waits = 0;
	      if (logged_exact_snapshot_action_resolver_waits < 64u) {
	        REXLOG_INFO(
            "Refii plume exact snapshot producer owns private/title "
            "resolver wait #{} frame={} slot={} guest=0x{:08X} "
            "source=0x{:08X}/{}x{} target=0x{:08X} rt=0x{:08X} "
            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_snapshot_action_resolver_waits,
            g_direct_renderer.current_frame_index, slot, guest,
            snapshot_source ? snapshot_source->guest_address : 0u,
            snapshot_source ? PlumeGuestTextureWidth(*snapshot_source) : 0u,
            snapshot_source ? PlumeGuestTextureHeight(*snapshot_source) : 0u,
	            command.resources.render_targets[0],
	            command.resources.render_targets[0],
	            command.resources.depth_stencil, command.resources.vertex_shader,
	            command.resources.pixel_shader);
	      }
	    } else if (DirectCommandTextureSlotExactProducerProvidesSampleLocked(
	                   command, slot, snapshot_source)) {
	      static uint32_t logged_exact_producer_sample_resolver_waits = 0;
	      if (logged_exact_producer_sample_resolver_waits < 32u) {
	        REXLOG_INFO(
	            "Refii plume exact producer sample owns private/title resolver "
	            "#{} frame={} slot={} guest=0x{:08X} rt=0x{:08X} "
	            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
	            ++logged_exact_producer_sample_resolver_waits,
	            g_direct_renderer.current_frame_index, slot, guest,
	            command.resources.render_targets[0], command.resources.depth_stencil,
	            command.resources.vertex_shader, command.resources.pixel_shader);
	      }
	    } else if (DirectCommandTextureSlotDirectResourceProvidesSampleLocked(
	                   command, slot, snapshot_source, true)) {
	      static uint32_t logged_exact_direct_sample_resolver_waits = 0;
	      if (logged_exact_direct_sample_resolver_waits < 32u) {
	        REXLOG_INFO(
            "Refii plume exact direct sample owns private/title resolver #{} "
            "frame={} slot={} guest=0x{:08X} sample=0x{:08X}/{}x{} "
            "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_direct_sample_resolver_waits,
            g_direct_renderer.current_frame_index, slot, guest,
            snapshot_source ? snapshot_source->guest_address : 0u,
            snapshot_source ? PlumeGuestTextureWidth(*snapshot_source) : 0u,
            snapshot_source ? PlumeGuestTextureHeight(*snapshot_source) : 0u,
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
    }
  } else if (declared_texture_slot &&
             command.draw.source != RendererDrawEventSource::kPixVideo) {
    static uint32_t logged_exact_private_copy_action_misses = 0;
    if (logged_exact_private_copy_action_misses < 32u) {
      REXLOG_INFO(
          "Refii plume exact private copy action reject #{} frame={} "
          "slot={} guest=0x{:08X} tracked=0x{:08X} parent=0x{:08X} "
          "base=0x{:08X} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
          "ps=0x{:08X} prefer_fetch={} source={} private_rect={}",
          ++logged_exact_private_copy_action_misses,
          g_direct_renderer.current_frame_index, slot, guest,
          texture ? texture->guest_address : 0u,
          texture ? texture->parent_address : 0u,
          texture ? PlumeGuestTextureBaseAddress(*texture) : 0u,
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader,
          prefer_fetch_actions ? 1 : 0,
          static_cast<uint32_t>(command.draw.source),
          command.draw.private_texture_rectangle ? 1 : 0);
    }
  }

  constexpr size_t kMaxPrivateTextureCopyActionCacheEntries = 4096u;
  if (g_direct_renderer.private_texture_copy_action_cache.size() >=
      kMaxPrivateTextureCopyActionCacheEntries) {
    g_direct_renderer.private_texture_copy_action_cache.clear();
    g_direct_renderer.private_texture_copy_action_sample_cache.clear();
    g_direct_renderer.private_texture_exact_interest_cache.clear();
    g_direct_renderer.private_texture_backing_miss_cache.clear();
    g_direct_renderer.registered_private_rect_execute_decision_cache.clear();
    g_direct_renderer.command_input_ready_cache.clear();
    g_direct_renderer.exact_current_producer_owner_cache.clear();
  }
  g_direct_renderer.private_texture_copy_action_cache[cache_key] = {
      action, match, private_copy_serial};
  if (action_match) {
    *action_match = match;
  }
  return action;
}

bool DirectDepthTargetHasExactPrivateRectActionLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& depth_target) {
  if (!DirectSyntheticPrivateRectCommandUsesDepthTarget(command) ||
      depth_target.guest_address == 0 ||
      !DirectCommandBindsResourceAsTargetLocked(command, depth_target)) {
    return false;
  }

  std::array<uint32_t, 3> candidate_slots = {
      source_slot,
      command.draw.private_texture_slot & (kMaxFableTextureSlots - 1u),
      kFablePrivateRectCopyTextureSlot,
  };
  std::array<uint32_t, 3> visited_slots = {};
  uint32_t visited_count = 0;
  for (uint32_t slot : candidate_slots) {
    if (slot >= command.resources.textures.size()) {
      continue;
    }
    if (std::find(visited_slots.begin(), visited_slots.begin() + visited_count,
                  slot) != visited_slots.begin() + visited_count) {
      continue;
    }
    if (visited_count < visited_slots.size()) {
      visited_slots[visited_count++] = slot;
    }

    PlumeTrackedResource* source =
        FindDirectResourceLocked(command.resources.textures[slot]);
    if (!source || !IsImageResource(source->kind) ||
        !PlumeGuestImageReady(*source)) {
      continue;
    }

    DirectPrivateTextureCopyActionMatch action_match =
        DirectPrivateTextureCopyActionMatch::kNone;
    const FablePrivateTextureCopyAction* action =
        SelectDirectExactPrivateTextureCopyActionForCommandSlotLocked(
            command, slot, source, &action_match);
    if (!action ||
        !DirectPrivateTextureCopyActionMatchIsExactRegistered(action_match) ||
        action->kind != FablePrivateTextureCopyKind::kDepth ||
        !DirectPrivateTextureCopyActionCommandSlotMatches(command, *action)) {
      continue;
    }
    if (DirectResourceAddressMatchesLocked(action->target_surface,
                                           depth_target.guest_address) ||
        DirectResourceAddressMatchesLocked(action->depth_stencil,
                                           depth_target.guest_address) ||
        DirectResourceAddressMatchesLocked(action->depth_stencil,
                                           command.resources.depth_stencil)) {
      return true;
    }
  }
  return false;
}

bool DirectCommandCanBindMaterializedPrivateTextureHeaderLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (slot >= command.resources.textures.size() || !texture ||
      command.draw.source == RendererDrawEventSource::kPixVideo ||
      !PlumeGuestImageReady(*texture) ||
      !DirectPrivateTextureHeaderNeedsAlias(command, slot, texture) ||
      !DirectCommandUsesFablePrivateTexturePath(command, slot, texture) ||
      DirectCommandBindsResourceAsTargetLocked(command, *texture)) {
    return false;
  }

  const bool title_or_private_chain_draw =
      command.draw.private_texture_rectangle ||
      IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
      IsFableTitleSceneVertexShaderCommandLocked(command);
  if (!title_or_private_chain_draw) {
    return false;
  }

  const bool materialized_header =
      texture->content_serial != 0 || texture->upload_generation != 0 ||
      HasTrackedFetchDwords(PlumeGuestTextureFetchDwords(*texture));
  if (!materialized_header) {
    return false;
  }

  const uint32_t guest = command.resources.textures[slot];
  return IsFableTitlePrivateTextureProxyAddress(guest) ||
         IsFableMutablePrivateTextureHeaderAddress(guest) ||
         IsFableTitlePrivateTextureProxyAddress(texture->guest_address) ||
         IsFableMutablePrivateTextureHeaderAddress(texture->guest_address) ||
         IsFableTitlePrivateTextureProxyAddress(texture->parent_address) ||
         IsFableMutablePrivateTextureHeaderAddress(texture->parent_address) ||
         IsFableTitlePrivateTextureProxyAddress(
             PlumeGuestTextureBaseAddress(*texture)) ||
         IsFableMutablePrivateTextureHeaderAddress(
             PlumeGuestTextureBaseAddress(*texture)) ||
         IsFablePrivateTextureChainResource(*texture);
}

bool DirectCommandCanSnapshotPrivateTextureFeedbackLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* source) {
  if (slot >= command.resources.textures.size() || !source ||
      !DirectCommandUsesFablePrivateTexturePath(command, slot, source)) {
    return false;
  }

  const bool source_needs_feedback_snapshot =
      DirectPrivateTextureHeaderNeedsAlias(command, slot, source) ||
      FableTrackedResourceIsBlankUploadedSampleProxy(*source) ||
      FableTrackedResourceIsUnbackedSampleProxy(*source) ||
      (IsFablePrivateTextureChainResource(*source) &&
       !FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(*source));
  if (!source_needs_feedback_snapshot) {
    return false;
  }

  if (command.draw.source == RendererDrawEventSource::kPixVideo ||
      command.resources.render_targets[0] == 0) {
    return false;
  }

  const bool title_feedback_draw =
      command.draw.source == RendererDrawEventSource::kSceneObject &&
      (IsFableTitleSceneVertexShaderCommandLocked(command) ||
       IsFableTitleSceneTargetLocked(command.resources.render_targets[0]));
  const bool private_rect_feedback_draw =
      command.draw.private_texture_rectangle &&
      IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader);
  if (!title_feedback_draw && !private_rect_feedback_draw) {
    return false;
  }

  PlumeTrackedResource* target =
      FindDirectResourceLocked(command.resources.render_targets[0]);
  const bool target_is_scene_output =
      target && title_feedback_draw &&
      DirectResourceIsTrackedSceneOutputLocked(*target);
  if (!target || target == source || !IsImageResource(target->kind) ||
      !PlumeGuestImageReady(*target) || !target->texture_render_target ||
      target->texture_depth_target ||
      !DirectCommandBindsResourceAsTargetLocked(command, *target) ||
      (!IsFablePrivateTextureChainResource(*target) &&
       !target_is_scene_output) ||
      FableTrackedResourceVisibleContentSerial(*target) == 0) {
    return false;
  }

  const uint32_t source_guest = command.resources.textures[slot];
  if (title_feedback_draw && !private_rect_feedback_draw) {
    const std::array<uint32_t, 6> command_fetch =
        DirectCommandTextureFetchDwords(command, slot);
    const std::array<uint32_t, 6> source_fetch =
        PlumeGuestTextureFetchDwords(*source);
    const uint32_t source_base = PlumeGuestTextureBaseAddress(*source);
    auto source_matches_target = [&](uint32_t address) {
      if (address == 0) {
        return false;
      }
      return DirectResourceAddressMatchesLocked(address, target->guest_address) ||
             DirectResourceAddressMatchesLocked(address, target->parent_address) ||
             DirectResourceAddressMatchesLocked(
                 address, command.resources.render_targets[0]);
    };
    auto action_targets_current = [&](const FablePrivateTextureCopyAction& action) {
      return action.kind != FablePrivateTextureCopyKind::kDepth &&
             (DirectResourceAddressMatchesLocked(action.target_surface,
                                                 target->guest_address) ||
              DirectResourceAddressMatchesLocked(action.render_target,
                                                 target->guest_address) ||
              DirectResourceAddressMatchesLocked(
                  action.target_surface, command.resources.render_targets[0]) ||
              DirectResourceAddressMatchesLocked(action.render_target,
                                                 command.resources.render_targets[0]));
    };
    auto fetch_matches = [](const std::array<uint32_t, 6>& lhs,
                            const std::array<uint32_t, 6>& rhs) {
      const uint64_t lhs_key = FableDirectTextureFetchKey(lhs);
      return lhs_key != 0 && lhs_key == FableDirectTextureFetchKey(rhs);
    };
    auto action_samples_source = [&](const FablePrivateTextureCopyAction& action) {
      constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
      if (action.source_slot != kUnknownSlot && action.source_slot != slot) {
        return false;
      }
      return DirectResourceAddressMatchesLocked(action.source_texture,
                                                source_guest) ||
             DirectResourceAddressMatchesLocked(action.source_texture,
                                                source->guest_address) ||
             DirectResourceAddressMatchesLocked(action.source_texture,
                                                source->parent_address) ||
             DirectResourceAddressMatchesLocked(action.source_base_address,
                                                source_base) ||
             DirectResourceAddressMatchesLocked(action.backing_texture,
                                                source_guest) ||
             DirectResourceAddressMatchesLocked(action.backing_texture,
                                                source->guest_address) ||
             DirectResourceAddressMatchesLocked(action.backing_texture,
                                                source->parent_address) ||
             DirectResourceAddressMatchesLocked(action.backing_base_address,
                                                source_base) ||
             fetch_matches(action.source_fetch_dwords, command_fetch) ||
             fetch_matches(action.source_fetch_dwords, source_fetch) ||
             fetch_matches(action.backing_fetch_dwords, command_fetch) ||
             fetch_matches(action.backing_fetch_dwords, source_fetch);
    };
    auto registered_same_target_action_exists =
        [&](const std::vector<FablePrivateTextureCopyAction>* actions) {
          if (!actions) {
            return false;
          }
          const uint64_t command_draw_serial =
              command.draw_serial != 0 ? command.draw_serial : command.serial;
          for (auto it = actions->rbegin(); it != actions->rend(); ++it) {
            const FablePrivateTextureCopyAction& action = *it;
            if (action.frame_index != 0 &&
                action.frame_index > g_direct_renderer.current_frame_index) {
              continue;
            }
            if (action.frame_index != 0 &&
                action.frame_index == g_direct_renderer.current_frame_index &&
                action.draw_serial != 0 && command_draw_serial != 0 &&
                action.draw_serial > command_draw_serial) {
              continue;
            }
            if (action_targets_current(action) && action_samples_source(action)) {
              return true;
            }
          }
          return false;
        };
    auto exact_feedback_action_exists = [&]() {
      if (FindDirectPrivateRectMaterializationActionLocked(
              command, slot, source, *target)) {
        return true;
      }
      if (registered_same_target_action_exists(
              FableDirectFindPrivateTextureCopyActionsBySource(source_guest))) {
        return true;
      }
      if (registered_same_target_action_exists(
              FableDirectFindPrivateTextureCopyActionsBySource(
                  source->guest_address))) {
        return true;
      }
      if (source->parent_address != 0 &&
          registered_same_target_action_exists(
              FableDirectFindPrivateTextureCopyActionsBySource(
                  source->parent_address))) {
        return true;
      }
      if (source_base != 0 &&
          registered_same_target_action_exists(
              FableDirectFindPrivateTextureCopyActionsByBase(source_base))) {
        return true;
      }
      if (HasTrackedFetchDwords(command_fetch) &&
          registered_same_target_action_exists(
              FableDirectFindPrivateTextureCopyActionsByFetch(command_fetch))) {
        return true;
      }
      return HasTrackedFetchDwords(source_fetch) &&
             registered_same_target_action_exists(
                 FableDirectFindPrivateTextureCopyActionsByFetch(source_fetch));
    };
    const bool same_source_target =
        source_matches_target(source_guest) ||
        source_matches_target(source->guest_address) ||
        source_matches_target(source->parent_address) ||
        source_matches_target(PlumeGuestTextureBaseAddress(*source));
    if (!exact_feedback_action_exists() && !same_source_target) {
      return false;
    }
  }
  return IsFableTitlePrivateTextureProxyAddress(source_guest) ||
         IsFableTitlePrivateTextureProxyAddress(source->guest_address) ||
         IsFableTitlePrivateTextureProxyAddress(source->parent_address) ||
         IsFableTitlePrivateTextureProxyAddress(
             PlumeGuestTextureBaseAddress(*source)) ||
         IsFablePrivateTextureChainResource(*source);
}


bool DirectCommandCanBootstrapRegisteredPrivateRectMaterializationLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (!texture ||
      !DirectPrivateTextureHeaderNeedsAlias(command, slot, texture)) {
    return false;
  }
  if (const PlumeTrackedResource* target =
          FindDirectResourceLocked(command.resources.render_targets[0])) {
    if (FindDirectPrivateRectMaterializationActionLocked(command, slot, texture,
                                                        *target)) {
      return true;
    }
  }
  return false;
}

PlumeTrackedResource* FindDirectReadyUploadedTextureByIdentityLocked(
    const FableDirectRenderCommand& command,
    const FableTextureIdentity* identity,
    const PlumeTrackedResource* skip_resource) {
  if (!identity || identity->object_texture == 0) {
    return nullptr;
  }

  PlumeTrackedResource* resource =
      FindDirectResourceLocked(identity->object_texture);
  if (!resource || resource == skip_resource || !IsImageResource(resource->kind) ||
      !PlumeGuestImageReady(*resource) ||
      !FableTrackedResourceHasMeaningfulTextureUpload(*resource) ||
      FableTrackedResourceIsBlankUploadedSampleProxy(*resource) ||
      DirectCommandBindsResourceAsTargetLocked(command, *resource)) {
    return nullptr;
  }

  const bool same_fetch = DirectTextureFetchDwordsMatch(
      PlumeGuestTextureFetchDwords(*resource), identity->fetch_dwords);
  const bool same_base = identity->base_address != 0 &&
                         PlumeGuestTextureBaseAddress(*resource) ==
                             identity->base_address;
  if (!same_fetch && !same_base) {
    return nullptr;
  }

  if (identity->width != 0 &&
      PlumeGuestTextureWidth(*resource) != identity->width) {
    return nullptr;
  }
  if (identity->height != 0 &&
      PlumeGuestTextureHeight(*resource) != identity->height) {
    return nullptr;
  }
  return resource;
}

PlumeTrackedResource* FindDirectReadyUploadedTextureByIdentityObjectsLocked(
    const FableDirectRenderCommand& command,
    const std::vector<uint32_t>* identity_objects,
    const PlumeTrackedResource* skip_resource) {
  if (!identity_objects) {
    return nullptr;
  }

  PlumeTrackedResource* best = nullptr;
  uint64_t best_score = 0;
  struct CandidateLog {
    uint32_t guest = 0;
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t format = 0;
    uint64_t score = 0;
    uint64_t generation = 0;
    uint64_t content = 0;
  };
  std::array<CandidateLog, 4> logged_candidates = {};
  uint32_t logged_candidate_count = 0;
  uint32_t viable_candidate_count = 0;
  for (uint32_t object : *identity_objects) {
    PlumeTrackedResource* resource =
        FindDirectReadyUploadedTextureByIdentityLocked(
            command, FableDirectFindTextureIdentityByObject(object),
            skip_resource);
    if (!resource) {
      continue;
    }

    uint64_t score = std::max(resource->upload_generation,
                              resource->content_serial);
    if (FableTrackedResourceHasSampleableContent(*resource)) {
      score += 1'000'000'000ull;
    }
    if (IsFablePrivateTextureChainResource(*resource)) {
      score += 100'000'000ull;
    }
    if (logged_candidate_count < logged_candidates.size()) {
      logged_candidates[logged_candidate_count++] = CandidateLog{
          resource->guest_address,
          PlumeGuestTextureWidth(*resource),
          PlumeGuestTextureHeight(*resource),
          static_cast<uint32_t>(PlumeGuestTextureFormat(*resource)),
          score,
          resource->upload_generation,
          resource->content_serial};
    }
    ++viable_candidate_count;
    if (!best || score > best_score ||
        (score == best_score && resource->serial > best->serial)) {
      best = resource;
      best_score = score;
    }
  }
  if (best && viable_candidate_count > 1u &&
      g_direct_renderer.diagnostics.logged_private_rect_identity_bucket_choices < 16u) {
    const CandidateLog& c0 = logged_candidates[0];
    const CandidateLog& c1 = logged_candidates[1];
    const CandidateLog& c2 = logged_candidates[2];
    const CandidateLog& c3 = logged_candidates[3];
    REXLOG_INFO(
        "Refii plume private rect identity bucket choice #{} frame={} "
        "draw={} bucket={} viable={} chosen=0x{:08X}/{}x{} fmt={} "
        "score={} rt=0x{:08X} ps=0x{:08X} "
        "c0=0x{:08X}/{}x{} fmt={} score={} gen={} content={} "
        "c1=0x{:08X}/{}x{} fmt={} score={} gen={} content={} "
        "c2=0x{:08X}/{}x{} fmt={} score={} gen={} content={} "
        "c3=0x{:08X}/{}x{} fmt={} score={} gen={} content={}",
        ++g_direct_renderer.diagnostics.logged_private_rect_identity_bucket_choices,
        g_direct_renderer.current_frame_index,
        static_cast<unsigned long long>(command.draw_serial),
        identity_objects->size(), viable_candidate_count, best->guest_address,
        PlumeGuestTextureWidth(*best), PlumeGuestTextureHeight(*best),
        static_cast<uint32_t>(PlumeGuestTextureFormat(*best)), best_score,
        command.resources.render_targets[0], command.resources.pixel_shader,
        c0.guest, c0.width, c0.height, c0.format, c0.score, c0.generation,
        c0.content, c1.guest, c1.width, c1.height, c1.format, c1.score,
        c1.generation, c1.content, c2.guest, c2.width, c2.height, c2.format,
        c2.score, c2.generation, c2.content, c3.guest, c3.width, c3.height,
        c3.format, c3.score, c3.generation, c3.content);
  }
  return best;
}

bool DirectPrivateTextureFetchAliasIsAmbiguousHeaderProxyLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* proxy, const PlumeTrackedResource* alias,
    DirectPrivateTextureResolveSource source) {
  (void)command;
  (void)slot;
  if (!proxy || !alias || proxy == alias) {
    return false;
  }
  if (source != DirectPrivateTextureResolveSource::kIdentityCommandFetch &&
      source != DirectPrivateTextureResolveSource::kIdentityResourceFetch) {
    return false;
  }
  if (!IsFableHeaderOnlyPrivateTextureProxyAddress(proxy->guest_address) ||
      !FableTrackedResourceIsUnbackedSampleProxy(*proxy)) {
    return false;
  }
  if (!FableTrackedResourceHasMeaningfulTextureUpload(*alias) ||
      DirectCommandBindsResourceAsTargetLocked(command, *alias)) {
    return false;
  }

  const uint32_t proxy_width = PlumeGuestTextureWidth(*proxy);
  const uint32_t proxy_height = PlumeGuestTextureHeight(*proxy);
  const uint32_t alias_width = PlumeGuestTextureWidth(*alias);
  const uint32_t alias_height = PlumeGuestTextureHeight(*alias);
  if (proxy_width != 256u || proxy_height != 256u || alias_width != 256u ||
      alias_height != 256u) {
    return false;
  }

  // Fable's title/menu private headers reuse a generic 256x256 fetch header.
  // Treating that fetch as object identity collapses several independent
  // private passes onto the newest upload and produces the wrong title texture.
  if (alias->guest_address != proxy->guest_address) {
    return true;
  }

  const uint32_t proxy_base = PlumeGuestTextureBaseAddress(*proxy);
  const uint32_t alias_base = PlumeGuestTextureBaseAddress(*alias);
  const bool same_meaningful_base =
      proxy_base != 0 && proxy_base == alias_base && proxy_base >= 0x100000u;
  return !same_meaningful_base;
}

PlumeTrackedResource* ResolveDirectPrivateTextureIdentityLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture,
    DirectPrivateTextureResolveSource* source) {
  if (!DirectCommandUsesFablePrivateTexturePath(command, slot, texture)) {
    return nullptr;
  }

  auto resolve_fetch = [&](const std::array<uint32_t, 6>& fetch_dwords,
                           DirectPrivateTextureResolveSource resolve_source)
      -> PlumeTrackedResource* {
    if (PlumeTrackedResource* resource =
            FindDirectReadyUploadedTextureByIdentityObjectsLocked(
                command,
                FableDirectFindTextureIdentityObjectsByFetch(fetch_dwords),
                texture)) {
      if (DirectPrivateTextureFetchAliasIsAmbiguousHeaderProxyLocked(
              command, slot, texture, resource, resolve_source)) {
        return nullptr;
      }
      if (source) {
        *source = resolve_source;
      }
      return resource;
    }
    return nullptr;
  };

  if (PlumeTrackedResource* resource =
          resolve_fetch(DirectCommandTextureFetchDwords(command, slot),
                        DirectPrivateTextureResolveSource::
                            kIdentityCommandFetch)) {
    return resource;
  }
  if (texture) {
    if (PlumeTrackedResource* resource =
            resolve_fetch(PlumeGuestTextureFetchDwords(*texture),
                          DirectPrivateTextureResolveSource::
                              kIdentityResourceFetch)) {
      return resource;
    }
    if (PlumeTrackedResource* resource =
            FindDirectReadyUploadedTextureByIdentityLocked(
                command,
                FableDirectFindTextureIdentityByBase(
                    PlumeGuestTextureBaseAddress(*texture)),
                texture)) {
      if (source) {
        *source = DirectPrivateTextureResolveSource::kIdentityBase;
      }
      return resource;
    }
  }
  return nullptr;
}

bool DirectPrivateTextureCopyActionAllowsUploadedSource(
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    FablePrivateTextureCopyKind kind) {
  if (source.guest_address == target.guest_address ||
      source.texture_render_target || source.texture_depth_target ||
      target.texture_depth_target) {
    return false;
  }
  const bool source_private = IsFablePrivateTextureChainResource(source);
  const bool target_private = IsFablePrivateTextureChainResource(target);
  if (!source_private || !target_private) {
    return false;
  }
  if (kind == FablePrivateTextureCopyKind::kColorSibling) {
    return !target.texture_render_target;
  }
  if (kind == FablePrivateTextureCopyKind::kColor) {
    return target.texture_render_target &&
           FableTrackedResourceHasMeaningfulTextureUpload(source) &&
           !FableTrackedResourceIsBlankUploadedSampleProxy(source);
  }
  return false;
}

void RememberDirectPrivateTextureSiblingAliasAddressLocked(
    uint32_t source_guest, const PlumeTrackedResource& target) {
  if (source_guest == 0 || source_guest == target.guest_address ||
      !IsImageResource(target.kind) || !PlumeGuestImageReady(target) ||
      !FableTrackedResourceHasSampleableContent(target)) {
    return;
  }

  const auto existing_it =
      g_direct_renderer.private_texture_aliases_by_object.find(source_guest);
  if (existing_it != g_direct_renderer.private_texture_aliases_by_object.end() &&
      existing_it->second == target.guest_address) {
    return;
  }
  g_direct_renderer.private_texture_aliases_by_object[source_guest] =
      target.guest_address;
  MarkDirectPrivateTextureAliasStateChangedLocked();
}

void RememberDirectPrivateTextureSiblingAliasLocked(
    const PlumeTrackedResource& source, const PlumeTrackedResource& target) {
  RememberDirectPrivateTextureSiblingAliasAddressLocked(source.guest_address,
                                                       target);
}

void RememberDirectPrivateTextureAliasIndexLocked(
    uint32_t source_guest, uint32_t source_base,
    const std::array<uint32_t, 6>& source_fetch_dwords,
    const PlumeTrackedResource& target,
    bool include_equivalent_identity_aliases) {
  if (target.guest_address == 0 || !PlumeGuestImageReady(target) ||
      !FableTrackedResourceHasSampleableContent(target)) {
    return;
  }
  if (!DirectResourceIsPersistentPrivateSampleBacking(target)) {
    return;
  }
  if (DirectResourceIsPrivateFeedbackAlias(target)) {
    return;
  }
  const FablePrivateTextureProxyRole source_role =
      FablePrivateTextureProxyRoleForAddress(source_guest);
  if (!FablePrivateTextureProxyRoleAllowsTarget(source_role, target)) {
    return;
  }

  bool changed = false;

  const uint64_t fetch_key =
      FableDirectTextureFetchKey(source_fetch_dwords);
  if (fetch_key != 0) {
    const auto existing_it =
        g_direct_renderer.private_texture_aliases_by_fetch.find(fetch_key);
    changed =
        changed ||
        existing_it == g_direct_renderer.private_texture_aliases_by_fetch.end() ||
        existing_it->second != target.guest_address;
    g_direct_renderer.private_texture_aliases_by_fetch[fetch_key] =
        target.guest_address;
  }

  if (source_base != 0 && source_base != target.guest_address) {
    const auto existing_it =
        g_direct_renderer.private_texture_aliases_by_base.find(source_base);
    changed =
        changed ||
        existing_it == g_direct_renderer.private_texture_aliases_by_base.end() ||
        existing_it->second != target.guest_address;
    g_direct_renderer.private_texture_aliases_by_base[source_base] =
        target.guest_address;
  }

  auto remember_equivalent_identity_alias =
      [&](uint32_t identity_object) {
    if (identity_object == 0 || identity_object == source_guest ||
        identity_object == target.guest_address) {
      return;
    }
    const FableTextureIdentity* identity =
        FableDirectFindTextureIdentityByObject(identity_object);
    if (!identity) {
      return;
    }
    PlumeTrackedResource* identity_resource =
        FindDirectResourceLocked(identity_object);
    const bool identity_private =
        IsFableTitlePrivateTextureProxyAddress(identity->object_texture) ||
        IsFableTitlePrivateTextureProxyAddress(identity->parent_texture) ||
        IsFableTitlePrivateTextureProxyAddress(identity->base_address) ||
        (identity_resource && IsFablePrivateTextureChainResource(*identity_resource));
    const FablePrivateTextureProxyRole identity_role =
        FablePrivateTextureProxyRoleForAddress(identity->object_texture);
    if (!identity_private ||
        !FablePrivateTextureProxyRoleAllowsTarget(identity_role, target)) {
      return;
    }

    if (identity->base_address != 0 &&
        identity->base_address != target.guest_address) {
      const auto existing_it =
          g_direct_renderer.private_texture_aliases_by_base.find(
              identity->base_address);
      changed =
          changed ||
          existing_it == g_direct_renderer.private_texture_aliases_by_base.end() ||
          existing_it->second != target.guest_address;
      g_direct_renderer.private_texture_aliases_by_base[identity->base_address] =
          target.guest_address;
    }
    const uint64_t identity_fetch_key =
        FableDirectTextureFetchKey(identity->fetch_dwords);
    if (identity_fetch_key != 0) {
      const auto existing_it =
          g_direct_renderer.private_texture_aliases_by_fetch.find(
              identity_fetch_key);
      changed =
          changed ||
          existing_it ==
              g_direct_renderer.private_texture_aliases_by_fetch.end() ||
          existing_it->second != target.guest_address;
      g_direct_renderer.private_texture_aliases_by_fetch[identity_fetch_key] =
          target.guest_address;
    }
  };

  auto remember_equivalent_identity_aliases =
      [&](const std::vector<uint32_t>* identity_objects) {
    if (!identity_objects) {
      return;
    }
    constexpr size_t kMaxEquivalentPrivateIdentityAliases = 16;
    size_t remembered = 0;
    for (uint32_t identity_object : *identity_objects) {
      remember_equivalent_identity_alias(identity_object);
      if (++remembered >= kMaxEquivalentPrivateIdentityAliases) {
        break;
      }
    }
  };

  if (include_equivalent_identity_aliases) {
    remember_equivalent_identity_aliases(
        FableDirectFindTextureIdentityObjectsByBase(source_base));
    remember_equivalent_identity_aliases(
        FableDirectFindTextureIdentityObjectsByFetch(source_fetch_dwords));
  }

  if (changed) {
    MarkDirectPrivateTextureAliasStateChangedLocked();
  }

  if (changed &&
      g_direct_renderer.diagnostics.logged_private_rect_exact_alias_index_remembers < 32u) {
    REXLOG_INFO(
        "Refii plume remembered private exact alias index #{} frame={} "
        "source=0x{:08X} base=0x{:08X} alias=0x{:08X}/{}x{} fmt={} gen={}",
        ++g_direct_renderer.diagnostics.logged_private_rect_exact_alias_index_remembers,
        g_direct_renderer.current_frame_index, source_guest, source_base,
        target.guest_address, PlumeGuestTextureWidth(target),
        PlumeGuestTextureHeight(target),
        static_cast<uint32_t>(PlumeGuestTextureFormat(target)),
        static_cast<unsigned long long>(target.upload_generation));
  }
}


bool DirectPrivateTextureBackingCandidateValidLocked(
    const FableDirectRenderCommand& command, const PlumeTrackedResource& source,
    const PlumeTrackedResource& target, const PlumeTrackedResource* candidate) {
  return candidate && candidate != &source && candidate != &target &&
         IsImageResource(candidate->kind) && PlumeGuestImageReady(*candidate) &&
         FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(*candidate) &&
         !FableTrackedResourceIsBlankUploadedSampleProxy(*candidate) &&
         !DirectCommandBindsExactResourceAsTargetLocked(command, *candidate);
}

bool DirectCurrentPrivateTextureActionCanSampleTargetSurfaceLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target) {
  if (slot >= command.resources.textures.size() ||
      command.draw.source == RendererDrawEventSource::kPixVideo ||
      source.guest_address == 0 || target.guest_address == 0 ||
      source.guest_address == target.guest_address ||
      !IsImageResource(source.kind) || !IsImageResource(target.kind) ||
      !PlumeGuestImageReady(source) || !PlumeGuestImageReady(target) ||
      !DirectCommandUsesFablePrivateTexturePath(command, slot, &source) ||
      !DirectCommandBindsResourceAsTargetLocked(command, target)) {
    return false;
  }

  const uint64_t target_content = FableTrackedResourceVisibleContentSerial(target);
  if (target_content == 0 && !FableTrackedResourceHasGpuProducedContent(target)) {
    return false;
  }

  const bool source_needs_backing =
      !FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(source) ||
      FableTrackedResourceIsBlankUploadedSampleProxy(source) ||
      FableTrackedResourceIsUnbackedSampleProxy(source) ||
      DirectCommandBindsExactResourceAsTargetLocked(command, source);
  if (!source_needs_backing) {
    return false;
  }

  if (FableTrackedResourceHasMeaningfulTextureUpload(source) &&
      !FableTrackedResourceIsBlankUploadedSampleProxy(source)) {
    return false;
  }

  const uint32_t source_width = PlumeGuestTextureWidth(source);
  const uint32_t source_height = PlumeGuestTextureHeight(source);
  const uint32_t target_width = PlumeGuestTextureWidth(target);
  const uint32_t target_height = PlumeGuestTextureHeight(target);
  if (source_width == 0 || source_height == 0 || target_width == 0 ||
      target_height == 0) {
    return false;
  }
  const bool same_extent =
      source_width == target_width && source_height == target_height;
  const bool scaled_private_rect_edge =
      command.draw.private_texture_rectangle &&
      IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader) &&
      command.draw.private_texture_corners_valid &&
      (command.draw.private_texture_uv_override ||
       (command.draw.private_texture_u0 == 0.0f &&
        command.draw.private_texture_v0 == 0.0f &&
        command.draw.private_texture_u1 == 1.0f &&
        command.draw.private_texture_v1 == 1.0f));
  if (!same_extent && !scaled_private_rect_edge) {
    return false;
  }

  const uint32_t source_format = PlumeGuestTextureGuestFormat(source);
  const uint32_t target_format = PlumeGuestTextureGuestFormat(target);
  if (source_format != 0 && target_format != 0 && source_format != target_format) {
    return false;
  }

  if (g_direct_renderer.diagnostics.logged_private_action_current_gpu_surface_sources < 32u) {
    REXLOG_INFO(
        "Refii plume current private action GPU-surface source #{} "
        "frame={} slot={} guest=0x{:08X} source=0x{:08X}/{}x{} "
        "base=0x{:08X} target=0x{:08X}/{}x{} content={} "
        "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
        ++g_direct_renderer.diagnostics.logged_private_action_current_gpu_surface_sources,
        g_direct_renderer.current_frame_index, slot,
        command.resources.textures[slot], source.guest_address, source_width,
        source_height, PlumeGuestTextureBaseAddress(source),
        target.guest_address, target_width, target_height,
        static_cast<unsigned long long>(target_content),
        command.resources.render_targets[0], command.resources.depth_stencil,
        command.resources.vertex_shader, command.resources.pixel_shader);
  }
  return true;
}

void PopulateDirectPrivateTextureCopyBackingLocked(
    FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& backing) {
  action.backing_texture = backing.guest_address;
  action.backing_base_address = PlumeGuestTextureBaseAddress(backing);
  action.backing_width = PlumeGuestTextureWidth(backing);
  action.backing_height = PlumeGuestTextureHeight(backing);
  action.backing_format = PlumeGuestTextureGuestFormat(backing);
  action.backing_fetch_dwords = PlumeGuestTextureFetchDwords(backing);
  action.backing_content_serial = FableTrackedResourceVisibleContentSerial(backing);
  action.sampled_source = {};
  action.sampled_source.kind = FableRegisteredSampleSourceKind::kBackingTexture;
  action.sampled_source.resource = action.backing_texture;
  action.sampled_source.base_address = action.backing_base_address;
  action.sampled_source.fetch_dwords = action.backing_fetch_dwords;
  action.sampled_source.content_serial = action.backing_content_serial;
}

bool DirectPrivateTextureCopyActionCanSampleUploadedSource(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    FablePrivateTextureCopyKind kind) {
  if (!FableTrackedResourceHasMeaningfulTextureUpload(source) ||
      FableTrackedResourceIsBlankUploadedSampleProxy(source)) {
    return false;
  }
  if (DirectPrivateTextureCopyActionAllowsUploadedSource(source, target, kind)) {
    return true;
  }
  if (source.guest_address == target.guest_address ||
      source.texture_render_target || source.texture_depth_target ||
      target.texture_depth_target ||
      !DirectPrivateTextureCopyKindUsesColorBacking(kind) ||
      !IsFablePrivateTextureChainResource(source) ||
      !IsFablePrivateTextureChainResource(target)) {
    return false;
  }
  return DirectCommandBindsResourceAsTargetLocked(command, target);
}

void PopulateDirectPrivateTextureCopySourceLocked(
    FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& source) {
  action.sampled_source = {};
  action.sampled_source.kind = FableRegisteredSampleSourceKind::kGuestTexture;
  action.sampled_source.resource = action.source_texture != 0
                                       ? action.source_texture
                                       : source.guest_address;
  action.sampled_source.base_address = action.source_base_address;
  action.sampled_source.fetch_dwords = action.source_fetch_dwords;
  action.sampled_source.content_serial =
      FableTrackedResourceVisibleContentSerial(source);
}

void PopulateDirectPrivateTextureCopyTargetLocked(
    FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& target) {
  action.target_surface = target.guest_address;
  action.target_width = PlumeGuestTextureWidth(target);
  action.target_height = PlumeGuestTextureHeight(target);
  action.target_format = PlumeGuestTextureGuestFormat(target);
  action.target_content_serial = FableTrackedResourceVisibleContentSerial(target);
}

bool RefreshDirectSetupFlushPrivateCopyActionGpuSurfaceSampleLocked(
    FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& target) {
  if (action.wrapper_symbol_address != kFablePrivateRectSetupFlushWrapperSymbol ||
      target.guest_address == 0 || !IsImageResource(target.kind) ||
      !PlumeGuestImageReady(target) ||
      (!DirectResourceAddressMatchesLocked(action.target_surface,
                                           target.guest_address) &&
       !DirectResourceAddressMatchesLocked(action.render_target,
                                           target.guest_address))) {
    return false;
  }

  const uint64_t target_content =
      FableTrackedResourceVisibleContentSerial(target);
  if (target_content == 0) {
    return false;
  }

  bool refreshed = false;
  if (action.target_content_serial == 0) {
    action.target_content_serial = target_content;
    refreshed = true;
  }
  if (refreshed) {
    static uint32_t logged_setup_flush_action_sample_refreshes = 0;
    if (logged_setup_flush_action_sample_refreshes < 48u) {
      REXLOG_INFO(
          "Refii plume refreshed setup-flush action GPU-surface sample #{} "
          "frame={} texture=0x{:08X}/{}x{} base=0x{:08X} "
          "target=0x{:08X}/{}x{} content={} call={} context=0x{:016X} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_setup_flush_action_sample_refreshes,
          g_direct_renderer.current_frame_index, action.source_texture,
          action.source_width, action.source_height,
          action.source_base_address, target.guest_address,
          PlumeGuestTextureWidth(target), PlumeGuestTextureHeight(target),
          static_cast<unsigned long long>(target_content),
          static_cast<unsigned long long>(action.wrapper_call_serial),
          static_cast<unsigned long long>(action.wrapper_context_key),
          action.render_target, action.depth_stencil, action.vertex_shader,
          action.pixel_shader);
    }
  }
  return refreshed;
}

void PopulateDirectSurfaceTextureLinkBackingLocked(
    FableSurfaceTextureLink& link, const PlumeTrackedResource& backing) {
  link.backing_texture = backing.guest_address;
  link.backing_base_address = PlumeGuestTextureBaseAddress(backing);
  link.backing_fetch_dwords = PlumeGuestTextureFetchDwords(backing);
  link.backing_content_serial = FableTrackedResourceVisibleContentSerial(backing);
  link.sampled_source = {};
  link.sampled_source.kind = FableRegisteredSampleSourceKind::kBackingTexture;
  link.sampled_source.resource = link.backing_texture;
  link.sampled_source.base_address = link.backing_base_address;
  link.sampled_source.fetch_dwords = link.backing_fetch_dwords;
  link.sampled_source.content_serial = link.backing_content_serial;
}

void PopulateDirectSurfaceTextureLinkSourceLocked(
    FableSurfaceTextureLink& link, const PlumeTrackedResource& source) {
  link.sampled_source = {};
  link.sampled_source.kind = FableRegisteredSampleSourceKind::kGuestTexture;
  link.sampled_source.resource = link.texture != 0 ? link.texture
                                                   : source.guest_address;
  link.sampled_source.base_address = link.texture_base_address;
  link.sampled_source.fetch_dwords = link.texture_fetch_dwords;
  link.sampled_source.content_serial = FableTrackedResourceVisibleContentSerial(source);
}

void PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(
    FableSurfaceTextureLink& link, const PlumeTrackedResource& source) {
  link.target_content_serial = FableTrackedResourceVisibleContentSerial(source);
  link.sampled_source = {};
  link.sampled_source.kind = FableRegisteredSampleSourceKind::kGpuSurface;
  link.sampled_source.resource = source.guest_address;
  link.sampled_source.content_serial = link.target_content_serial;
}

void TrackDirectExactSurfaceAliasForPrivateCopyActionLocked(
    const FablePrivateTextureCopyAction& action, const char* reason) {
  if ((action.wrapper_call_serial == 0 && action.wrapper_context_key == 0 &&
       action.wrapper_symbol_address == 0) ||
      !IsLikelyFableObjectAddress(action.source_texture) ||
      !IsLikelyFableObjectAddress(action.target_surface) ||
      action.source_texture == action.target_surface) {
    return;
  }

  RendererSurfaceCopyAliasSnapshot alias;
  alias.source_surface = action.target_surface;
  alias.destination_surface = action.source_texture;
  alias.hook_address = action.wrapper_symbol_address != 0
                           ? action.wrapper_symbol_address
                           : action.lr;
  alias.serial = action.draw_serial != 0
                     ? action.draw_serial
                     : (action.wrapper_call_serial != 0
                            ? action.wrapper_call_serial
                            : action.wrapper_context_key);
  alias.copy_call_serial = action.wrapper_call_serial;
  alias.copy_context_key = action.wrapper_context_key;
  alias.copy_symbol_address = action.wrapper_symbol_address;
  FableDirectTrackSurfaceCopyAlias(alias);

  static uint32_t logged_direct_exact_surface_aliases = 0;
  if (RendererVerboseTracingEnabled() &&
      logged_direct_exact_surface_aliases < 48u) {
    REXLOG_INFO(
        "Refii plume direct exact surface alias #{} frame={} reason={} "
        "source=0x{:08X} destination=0x{:08X} call={} context=0x{:016X} "
        "symbol=0x{:08X} kind={}",
        ++logged_direct_exact_surface_aliases,
        g_direct_renderer.current_frame_index, reason ? reason : "direct",
        alias.source_surface, alias.destination_surface,
        static_cast<unsigned long long>(alias.copy_call_serial),
        static_cast<unsigned long long>(alias.copy_context_key),
        alias.copy_symbol_address,
        FablePrivateTextureCopyKindName(action.kind));
  }
}

// ZERO-CALLER ResolveDirectPrivateTextureActionBackingFromSurfaceLinksLocked —
// DELETED 2026-07-26 (~255 lines). It scored every visible FableSurfaceTexture
// Link against a private-texture action's source/target to pick a backing
// sample. Compiler-verified unused (-Wunused-function) plus grep-verified:
// only its forward declaration and definition existed, both in this file, both
// removed. Not an overload set. Internal linkage.
bool DirectPrivateTextureActionHasExactGpuSurfaceNoBackingLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target) {
  if (slot >= command.resources.textures.size() ||
      command.draw.source == RendererDrawEventSource::kPixVideo) {
    return false;
  }

  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  const uint32_t guest = command.resources.textures[slot];
  const uint32_t source_base = PlumeGuestTextureBaseAddress(source);
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const std::array<uint32_t, 6> source_fetch =
      PlumeGuestTextureFetchDwords(source);
  const uint64_t command_fetch_key = FableDirectTextureFetchKey(command_fetch);
  const uint64_t source_fetch_key = FableDirectTextureFetchKey(source_fetch);
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  const DirectTextureFetchInfo source_fetch_info =
      DirectTextureFetchInfoFromDwords(source_fetch);
  const uint32_t source_width = PlumeGuestTextureWidth(source);
  const uint32_t source_height = PlumeGuestTextureHeight(source);
  const uint32_t source_format = PlumeGuestTextureGuestFormat(source);
  const uint32_t target_width = PlumeGuestTextureWidth(target);
  const uint32_t target_height = PlumeGuestTextureHeight(target);
  const uint32_t target_format = PlumeGuestTextureGuestFormat(target);
  const uint64_t command_draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;

  auto fetch_matches = [](const std::array<uint32_t, 6>& fetch_dwords,
                          uint64_t key) {
    return key != 0 && FableDirectTextureFetchKey(fetch_dwords) == key;
  };
  auto action_visible = [&](const FablePrivateTextureCopyAction& action) {
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
    return action.source_slot == kUnknownSlot || action.source_slot == slot;
  };
  auto action_source_matches = [&](const FablePrivateTextureCopyAction& action) {
    if (action.source_texture != 0 &&
        (action.source_texture == guest ||
         action.source_texture == source.guest_address ||
         action.source_texture == source.parent_address)) {
      return true;
    }
    if (action.source_base_address != 0 && source_base != 0 &&
        action.source_base_address == source_base) {
      return true;
    }
    if (fetch_matches(action.source_fetch_dwords, command_fetch_key) ||
        fetch_matches(action.source_fetch_dwords, source_fetch_key)) {
      return true;
    }
    return false;
  };
  auto action_targets_current_resource =
      [&](const FablePrivateTextureCopyAction& action) {
    return action.target_surface == target.guest_address ||
           action.render_target == target.guest_address ||
           action.depth_stencil == target.guest_address ||
           (target.parent_address != 0 &&
            DirectPrivateTextureCopyActionTargetShapeCompatibleWithResource(
                action, target) &&
            (action.target_surface == target.parent_address ||
             action.render_target == target.parent_address ||
             action.depth_stencil == target.parent_address));
  };
  bool has_shape_reject = false;
  uint32_t rejected_expected_source_width = 0;
  uint32_t rejected_expected_source_height = 0;
  uint32_t rejected_action_source_width = 0;
  uint32_t rejected_action_source_height = 0;
  uint32_t rejected_action_source_format = 0;
  uint32_t rejected_action_target_width = 0;
  uint32_t rejected_action_target_height = 0;
  uint32_t rejected_action_target_format = 0;
  uint64_t rejected_action_serial = 0;
  auto action_shape_matches =
      [&](const FablePrivateTextureCopyAction& action) {
    const auto format_matches = [](uint32_t action_format,
                                   uint32_t resource_format) {
      return action_format == 0 || resource_format == 0 ||
             action_format == resource_format;
    };
    uint32_t expected_source_width = source_width;
    uint32_t expected_source_height = source_height;
    uint32_t expected_source_format = source_format;
    if (command_fetch_info.valid &&
        fetch_matches(action.source_fetch_dwords, command_fetch_key)) {
      expected_source_width = command_fetch_info.width;
      expected_source_height = command_fetch_info.height;
      expected_source_format = command_fetch_info.format;
    } else if (source_fetch_info.valid &&
               fetch_matches(action.source_fetch_dwords, source_fetch_key)) {
      expected_source_width = source_fetch_info.width;
      expected_source_height = source_fetch_info.height;
      expected_source_format = source_fetch_info.format;
    }
    // Shape-authority widening (2026-08-02, run 521): registration sites
    // populate action.source_* from TWO different authorities — the tracked
    // resource header (RememberDirectPrivateTextureCopyActionLocked) or the
    // decoded fetch constant (RememberDirectExactPrivateRectSurfaceTexture-
    // EdgesLocked) — and the fetch-derived width routinely disagrees with the
    // header shape. Run 521's 280x180->480x270 rejects printed IDENTICAL
    // shapes on both sides because the log shows the header shape while this
    // compared the fetch shape. Accept when the action matches EITHER
    // authority exactly; both operands still have to agree with one real
    // shape, so a genuinely different-sized surface cannot slip through.
    const bool source_shape_mismatch =
        (action.source_width != expected_source_width ||
         action.source_height != expected_source_height) &&
        (action.source_width != source_width ||
         action.source_height != source_height);
    const bool target_shape_mismatch =
        action.target_width != target_width ||
        action.target_height != target_height;
    const bool title_scale_down_target_shape =
        target_shape_mismatch &&
        (DirectPrivateTextureCopyActionIsTitleScaleDownLocked(command, action) ||
         DirectPrivateTextureCopyActionTargetMatchesTitleHalfTargetLocked(
             command, action, target_width, target_height));
    const bool exact_shape_match =
        !source_shape_mismatch &&
        format_matches(action.source_format, expected_source_format) &&
        (!target_shape_mismatch || title_scale_down_target_shape) &&
        format_matches(action.target_format, target_format);
    if (exact_shape_match) {
      return true;
    }

    const bool mismatch =
        source_shape_mismatch ||
        !format_matches(action.source_format, expected_source_format) ||
        target_shape_mismatch ||
        !format_matches(action.target_format, target_format);
    if (mismatch) {
      const bool action_has_complete_shape =
          action.source_width != 0 && action.source_height != 0 &&
          action.target_width != 0 && action.target_height != 0;
      if (action_has_complete_shape &&
          (source_shape_mismatch || target_shape_mismatch) &&
          !has_shape_reject) {
        has_shape_reject = true;
        rejected_expected_source_width = expected_source_width;
        rejected_expected_source_height = expected_source_height;
        rejected_action_source_width = action.source_width;
        rejected_action_source_height = action.source_height;
        rejected_action_source_format = action.source_format;
        rejected_action_target_width = action.target_width;
        rejected_action_target_height = action.target_height;
        rejected_action_target_format = action.target_format;
        rejected_action_serial = action.serial;
      }
      return false;
    }
    return true;
  };
  auto action_is_exact_gpu_surface =
      [&](const FablePrivateTextureCopyAction& action) {
    if (!action_visible(action)) {
      return false;
    }
    if (!action_source_matches(action)) {
      return false;
    }
    if (!action_targets_current_resource(action)) {
      return false;
    }
    if (!action_shape_matches(action)) {
      return false;
    }
    if (!DirectPrivateTextureCopyActionContextMatches(command, action)) {
      return false;
    }
    if (action.backing_texture != 0 || action.backing_base_address != 0 ||
        FableDirectTextureFetchKey(action.backing_fetch_dwords) != 0) {
      return false;
    }
    if (action.sampled_source.kind !=
        FableRegisteredSampleSourceKind::kGpuSurface) {
      return false;
    }
    if (action.target_surface == 0 ||
        action.target_surface != target.guest_address) {
      return false;
    }
    if (action.sampled_source.resource != 0 &&
        action.sampled_source.resource != action.target_surface) {
      return false;
    }
    return action.target_content_serial != 0 ||
           action.sampled_source.content_serial != 0 ||
           FableTrackedResourceHasGpuProducedContent(target);
  };
  auto exact_keyed_action_is_exact_gpu_surface =
      [&](const FablePrivateTextureCopyAction& action) {
    const auto format_matches = [](uint32_t action_format,
                                   uint32_t resource_format) {
      return action_format == 0 || resource_format == 0 ||
             action_format == resource_format;
    };
    if (!action_visible(action) || !action_source_matches(action) ||
        !action_targets_current_resource(action) ||
        !DirectPrivateTextureCopyActionContextMatches(command, action)) {
      return false;
    }
    const bool title_scale_down_target_shape =
        (action.target_width != target_width ||
         action.target_height != target_height) &&
        (DirectPrivateTextureCopyActionIsTitleScaleDownLocked(command, action) ||
         DirectPrivateTextureCopyActionTargetMatchesTitleHalfTargetLocked(
             command, action, target_width, target_height));
    if (action.source_width != source_width ||
        action.source_height != source_height ||
        !format_matches(action.source_format, source_format) ||
        ((action.target_width != target_width ||
          action.target_height != target_height) &&
         !title_scale_down_target_shape) ||
        !format_matches(action.target_format, target_format)) {
      return false;
    }
    if (action.backing_texture != 0 || action.backing_base_address != 0 ||
        FableDirectTextureFetchKey(action.backing_fetch_dwords) != 0) {
      return false;
    }
    if (action.sampled_source.kind !=
        FableRegisteredSampleSourceKind::kGpuSurface) {
      return false;
    }
    if (action.target_surface == 0 ||
        action.target_surface != target.guest_address) {
      return false;
    }
    if (action.sampled_source.resource != 0 &&
        action.sampled_source.resource != action.target_surface) {
      return false;
    }
    return action.target_content_serial != 0 ||
           action.sampled_source.content_serial != 0 ||
           FableTrackedResourceHasGpuProducedContent(target);
  };

  const FablePrivateTextureCopyAction* matched = nullptr;
  if (target.texture_depth_target &&
      DirectSyntheticPrivateRectDepthActionCanExecuteLocked(command, slot,
                                                            &source)) {
    static uint32_t logged_exact_native_depth_gpu_surface_no_backing = 0;
    if (logged_exact_native_depth_gpu_surface_no_backing < 32u) {
      REXLOG_INFO(
          "Refii plume private action exact native-depth GPU-surface no-backing "
          "#{} frame={} slot={} guest=0x{:08X} source=0x{:08X}/{}x{} "
          "target=0x{:08X}/{}x{} rt=0x{:08X} ds=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_native_depth_gpu_surface_no_backing,
          g_direct_renderer.current_frame_index, slot, guest,
          source.guest_address, source_width, source_height,
          target.guest_address, target_width, target_height,
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return true;
  }
  if (const FablePrivateTextureCopyAction* exact_action =
          FindDirectPrivateRectExactActionLocked(command, slot, source, target);
      exact_action && exact_keyed_action_is_exact_gpu_surface(*exact_action)) {
    matched = exact_action;
  }
  std::unordered_set<uint64_t> seen_actions;
  auto consider_actions =
      [&](const std::vector<FablePrivateTextureCopyAction>* actions) {
    if (!actions || matched) {
      return;
    }
    for (auto it = actions->rbegin(); it != actions->rend(); ++it) {
      const FablePrivateTextureCopyAction& action = *it;
      const uint64_t key = action.serial != 0 ? action.serial
                                              : reinterpret_cast<uintptr_t>(&action);
      if (!seen_actions.insert(key).second) {
        continue;
      }
      if (action_is_exact_gpu_surface(action)) {
        matched = &action;
        return;
      }
    }
  };

  consider_actions(
      FableDirectFindPrivateTextureCopyActionsByTarget(target.guest_address));
  consider_actions(FableDirectFindPrivateTextureCopyActionsBySource(guest));
  consider_actions(
      FableDirectFindPrivateTextureCopyActionsBySource(source.guest_address));
  consider_actions(
      FableDirectFindPrivateTextureCopyActionsBySource(source.parent_address));
  consider_actions(FableDirectFindPrivateTextureCopyActionsByBase(source_base));
  consider_actions(
      FableDirectFindPrivateTextureCopyActionsByFetch(command_fetch));
  consider_actions(FableDirectFindPrivateTextureCopyActionsByFetch(source_fetch));

  if (!matched) {
    if (has_shape_reject) {
      const PlumeTrackedResource* direct_texture =
          guest != 0 ? FindDirectResourceLocked(guest) : nullptr;
      if (DirectSyntheticPrivateRectColorActionCanExecuteLocked(
              command, slot, direct_texture ? direct_texture : &source)) {
        static uint32_t logged_exact_gpu_surface_partial_shape_covers = 0;
        if (logged_exact_gpu_surface_partial_shape_covers < 32u) {
          REXLOG_INFO(
              "Refii plume exact GPU-surface partial-shape covered by scaled "
              "private rect #{} frame={} slot={} guest=0x{:08X} "
              "source=0x{:08X}/{}x{} fmt={} target=0x{:08X}/{}x{} fmt={} "
              "action_source_shape={}x{} fmt={} action_target_shape={}x{} "
              "fmt={} action={} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
              "ps=0x{:08X}",
              ++logged_exact_gpu_surface_partial_shape_covers,
              g_direct_renderer.current_frame_index, slot, guest,
              source.guest_address, source_width, source_height, source_format,
              target.guest_address, target_width, target_height, target_format,
              rejected_action_source_width, rejected_action_source_height,
              rejected_action_source_format, rejected_action_target_width,
              rejected_action_target_height, rejected_action_target_format,
              static_cast<unsigned long long>(rejected_action_serial),
              command.resources.render_targets[0],
              command.resources.depth_stencil, command.resources.vertex_shader,
              command.resources.pixel_shader);
        }
        return true;
      }
      static uint32_t logged_exact_gpu_surface_partial_shape_rejects = 0;
      if (logged_exact_gpu_surface_partial_shape_rejects < 32u) {
        REXLOG_INFO(
            "Refii plume exact GPU-surface partial-shape reject #{} "
            "frame={} slot={} guest=0x{:08X} source=0x{:08X}/{}x{} fmt={} "
            "expected_source={}x{} target=0x{:08X}/{}x{} fmt={} "
            "action_source_shape={}x{} fmt={} "
            "action_target_shape={}x{} fmt={} action={}",
            ++logged_exact_gpu_surface_partial_shape_rejects,
            g_direct_renderer.current_frame_index, slot, guest,
            source.guest_address, source_width, source_height, source_format,
            rejected_expected_source_width, rejected_expected_source_height,
            target.guest_address, target_width, target_height, target_format,
            rejected_action_source_width, rejected_action_source_height,
            rejected_action_source_format, rejected_action_target_width,
            rejected_action_target_height, rejected_action_target_format,
            static_cast<unsigned long long>(rejected_action_serial));
      }
    }
    return false;
  }

  const char* matched_ida_proof =
      DirectIdaPrivateRectWrapperProofName(matched->wrapper_symbol_address);
  const bool matched_has_ida_proof =
      matched_ida_proof &&
      DirectPrivateTextureCopyActionHasExactWrapperIdentity(*matched);
  if (!matched_has_ida_proof) {
    static uint32_t logged_exact_gpu_surface_missing_ida_proofs = 0;
    if (RendererDeepObserverHooksEnabled() &&
        logged_exact_gpu_surface_missing_ida_proofs < 32u) {
      REXLOG_WARN(
          "Refii plume private action exact GPU-surface slot edge missing IDA "
          "proof #{} frame={} slot={} action={} guest=0x{:08X} "
          "source=0x{:08X}/{}x{} source_base=0x{:08X} "
          "target=0x{:08X}/{}x{} content={} kind={} call={} "
          "context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} ds=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_gpu_surface_missing_ida_proofs,
          g_direct_renderer.current_frame_index, slot,
          static_cast<unsigned long long>(matched->serial), guest,
          source.guest_address, source_width, source_height, source_base,
          target.guest_address, target_width, target_height,
          static_cast<unsigned long long>(matched->target_content_serial),
          FablePrivateTextureCopyKindName(matched->kind),
          static_cast<unsigned long long>(matched->wrapper_call_serial),
          static_cast<unsigned long long>(matched->wrapper_context_key),
          matched->wrapper_symbol_address, command.resources.render_targets[0],
          command.resources.depth_stencil, command.resources.vertex_shader,
          command.resources.pixel_shader);
    }
    return false;
  }

  FablePrivateTextureCopyAction published_action = *matched;
  published_action.source_slot = slot;
  if (published_action.source_texture == 0) {
    published_action.source_texture =
        guest != 0 ? guest : source.guest_address;
  }
  if (published_action.source_base_address == 0) {
    published_action.source_base_address = source_base;
  }
  if (published_action.source_width == 0) {
    published_action.source_width = source_width;
  }
  if (published_action.source_height == 0) {
    published_action.source_height = source_height;
  }
  if (published_action.source_format == 0) {
    published_action.source_format = source_format;
  }
  if (!HasTrackedFetchDwords(published_action.source_fetch_dwords)) {
    published_action.source_fetch_dwords =
        HasTrackedFetchDwords(command_fetch) ? command_fetch : source_fetch;
  }
  PopulateDirectPrivateTextureCopyTargetLocked(published_action, target);
  published_action.sampled_source = {};
  published_action.sampled_source.kind = FableRegisteredSampleSourceKind::kGpuSurface;
  published_action.sampled_source.resource = published_action.target_surface;
  published_action.sampled_source.content_serial =
      published_action.target_content_serial;
  published_action.render_target = command.resources.render_targets[0];
  published_action.depth_stencil = command.resources.depth_stencil;
  published_action.vertex_shader = command.resources.vertex_shader;
  published_action.pixel_shader = command.resources.pixel_shader;
  PopulateDirectPrivateTextureCopyShaderRoots(published_action, command);
  published_action.lr = command.draw.state_flush_lr;
  published_action.frame_index = g_direct_renderer.current_frame_index;
  published_action.draw_serial = command_draw_serial;
  RegisterDirectPrivateTextureCopyActionLocked(published_action);
  if (DirectPrivateTextureCopyActionHasExactWrapperIdentity(published_action)) {
    FableSurfaceTextureLink link;
    link.copy_call_serial = published_action.wrapper_call_serial;
    link.copy_context_key = published_action.wrapper_context_key;
    link.copy_symbol_address = published_action.wrapper_symbol_address;
    link.source_slot = slot;
    link.trusted_destination_edge = true;
    link.texture = published_action.source_texture;
    link.texture_base_address = published_action.source_base_address;
    link.texture_width = published_action.source_width;
    link.texture_height = published_action.source_height;
    link.texture_format = published_action.source_format;
    link.texture_fetch_dwords = published_action.source_fetch_dwords;
    link.source_surface = published_action.target_surface;
    link.target_content_serial = published_action.target_content_serial;
    link.render_target = published_action.render_target;
    link.depth_stencil = published_action.depth_stencil;
    link.vertex_shader = published_action.vertex_shader;
    link.pixel_shader = published_action.pixel_shader;
    PopulateDirectSurfaceTextureLinkShaderRoots(link, published_action);
    link.frame_index = published_action.frame_index;
    link.draw_serial = published_action.draw_serial;
    link.wrapper_source_texture = published_action.wrapper_source_texture;
    link.wrapper_source_exact = published_action.wrapper_source_exact;
    link.wrapper_setup_globals = published_action.wrapper_setup_globals;
    link.sampled_source = published_action.sampled_source;
    link.kind = published_action.kind;
    RegisterDirectSurfaceTextureLinkPreparedLocked(
        link, "exact-gpu-surface-no-backing");
  }

  if (g_direct_renderer.diagnostics.logged_private_action_exact_gpu_surface_no_backing < 32u) {
    REXLOG_INFO(
        "Refii plume private action exact GPU-surface slot ownership proof #{} "
        "frame={} slot={} action={} guest=0x{:08X} source=0x{:08X}/{}x{} "
        "source_base=0x{:08X} target=0x{:08X}/{}x{} content={} "
        "proof={} kind={} call={} context=0x{:016X} symbol=0x{:08X} "
        "rt=0x{:08X} ds=0x{:08X} "
        "vs=0x{:08X} ps=0x{:08X}",
        ++g_direct_renderer.diagnostics.logged_private_action_exact_gpu_surface_no_backing,
        g_direct_renderer.current_frame_index, slot,
        static_cast<unsigned long long>(matched->serial), guest,
        source.guest_address, source_width, source_height, source_base,
        target.guest_address,
        target_width, target_height,
        static_cast<unsigned long long>(published_action.target_content_serial),
        matched_ida_proof, FablePrivateTextureCopyKindName(published_action.kind),
        static_cast<unsigned long long>(published_action.wrapper_call_serial),
        static_cast<unsigned long long>(published_action.wrapper_context_key),
        published_action.wrapper_symbol_address,
        command.resources.render_targets[0], command.resources.depth_stencil,
        command.resources.vertex_shader, command.resources.pixel_shader);
  }
  return true;
}

#endif

}  // namespace refii::renderer
