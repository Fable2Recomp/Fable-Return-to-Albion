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

void CollectDirectExactTitleUploadPreflightInterestsLocked(
    const FableDirectRenderCommand& command,
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter) {
  switch (command.type) {
    case FableDirectRenderCommandType::kDrawPrimitive:
    case FableDirectRenderCommandType::kDrawIndexedPrimitive:
    case FableDirectRenderCommandType::kDrawPrimitiveUP:
      break;
    default:
      return;
  }
  if (command.draw.source == RendererDrawEventSource::kPixVideo) {
    return;
  }
  if (command.draw.source == RendererDrawEventSource::kD3DWrapper &&
      !command.draw.private_texture_rectangle &&
      !IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) &&
      !IsFableTitleSceneVertexShaderCommandLocked(command) &&
      !IsFableTitleSceneTextureSyncCommandLocked(command)) {
    return;
  }

  const uint32_t texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(command);
  for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
    if (!DirectCommandUsesTextureSlotForSyncLocked(command, texture_slot_mask,
                                                   slot)) {
      continue;
    }
    if (!DirectCommandSlotUsesExactTitleUploadSourceLocked(command, slot)) {
      continue;
    }
    const std::array<uint32_t, 6> command_fetch =
        DirectCommandTextureFetchDwords(command, slot);
    const DirectTextureFetchInfo command_fetch_info =
        DirectTextureFetchInfoFromDwords(command_fetch);
    if (!command_fetch_info.valid) {
      continue;
    }
    const uint32_t guest = command.resources.textures[slot];
    PlumeTrackedResource* direct_texture =
        guest != 0 ? FindDirectResourceLocked(guest) : nullptr;
    PlumeTrackedResource* existing_exact_title_upload =
        ResolveDirectExactTitleUploadSourceForCommandSlotLocked(
            command, slot, direct_texture);
    if (!existing_exact_title_upload) {
      existing_exact_title_upload =
          ResolveDirectTinyTitleUploadedTextureForCommandSlotLocked(
              command, slot, direct_texture);
    }
    if (!existing_exact_title_upload &&
        DirectTinyTitleCachedTextureCanBindLocked(command, guest,
                                                  direct_texture)) {
      existing_exact_title_upload = direct_texture;
    }
    if (existing_exact_title_upload) {
      g_exact_title_preflight_backoff.erase(command_fetch_info.base_address);
      continue;
    }
    if (g_exact_title_preflight_backoff.size() <
        kMaxExactTitlePreflightBackoffEntries) {
      ExactTitlePreflightBackoff& backoff =
          g_exact_title_preflight_backoff[command_fetch_info.base_address];
      if (g_direct_renderer.current_frame_index < backoff.next_retry_frame) {
        continue;
      }
      backoff.delay_frames = std::min(
          backoff.delay_frames == 0 ? 1u : backoff.delay_frames * 2u,
          kMaxExactTitlePreflightBackoffFrames);
      backoff.next_retry_frame =
          g_direct_renderer.current_frame_index + backoff.delay_frames;
    }
    const auto add_exact_texture_identity_address = [&](uint32_t address) {
      if (address == 0) {
        return;
      }
      AddDirectResourceInterest(resource_filter, address);
      AddDirectResourceInterest(texture_upload_filter, address);
      AddDirectTextureIdentityResourceInterestLocked(
          resource_filter, texture_upload_filter,
          FableDirectFindTextureIdentityByObject(address));
      // The singular ByBase lookup here was deleted 2026-07-22 for the same
      // structural reason as the billboard pair: the very next call resolves
      // the SAME base key through ObjectsByBase and adds the identity interest
      // for every object in the list, so the singular form could only repeat a
      // subset of it. (Run 152: c=747 h=332 u=0.) The ByObject lookup above
      // stays — that is a different key, not a subset of anything here.
      AddDirectTextureBaseIdentityResourceInterestsLocked(
          resource_filter, texture_upload_filter, address);
    };

    add_exact_texture_identity_address(guest);
    add_exact_texture_identity_address(command_fetch_info.base_address);
    add_exact_texture_identity_address(command_fetch_info.base_address &
                                       0xFFFFF000u);
    if (IsFableTitleBillboardTileUploadBase(command_fetch_info.base_address,
                                           command_fetch_info.width,
                                           command_fetch_info.height)) {
      AddFableTitleBillboardTileUploadAliasResourceInterestsLocked(
          resource_filter, texture_upload_filter,
          command_fetch_info.base_address, command_fetch_info.width,
          command_fetch_info.height);
    }
    // DEAD ADDRESS GATE exact_title_upload_1x1_alias (site 1 of 2) — DELETED
    // 2026-07-26. Branch key was fetch base 0x1FC40000 at 1x1, payload was the
    // one-run alias 0x401314D0. Run 288 census[final] (frame=2952) lists the
    // gate under tested_but_never_matched: this site is tested on every
    // preflight slot and the key never once held, so no interest was ever
    // seeded from it.
    const FableTextureHeaderAction* action =
        FableDirectFindTextureHeaderActionForExactFetchIdentity(
            command_fetch_info.base_address, command_fetch_info.width,
            command_fetch_info.height, command_fetch_info.format,
            command_fetch);
    if (!action || action->serial == 0 || action->call_serial == 0 ||
        action->context_key == 0 || action->symbol_address == 0 ||
        action->stack_scratch ||
        !DirectTextureFetchDwordsMatch(action->fetch_dwords, command_fetch) ||
        action->base_address != command_fetch_info.base_address ||
        action->width != command_fetch_info.width ||
        action->height != command_fetch_info.height ||
        action->format != command_fetch_info.format) {
      continue;
    }

    add_exact_texture_identity_address(guest);
    add_exact_texture_identity_address(action->texture_object);
    add_exact_texture_identity_address(action->parent_texture);
    add_exact_texture_identity_address(action->texture_header);
    add_exact_texture_identity_address(action->base_address);
    add_exact_texture_identity_address(action->base_address & 0xFFFFF000u);
    AddDirectTextureBaseAddressResourceInterestsLocked(
        resource_filter, texture_upload_filter, action->base_address);
    if (IsFableTitleBillboardTileUploadBase(action->base_address,
                                           action->width, action->height)) {
      AddFableTitleBillboardTileUploadAliasResourceInterestsLocked(
          resource_filter, texture_upload_filter, action->base_address,
          action->width, action->height);
    }
    AddDirectTextureFetchResourceInterestsLocked(
        resource_filter, texture_upload_filter, action->fetch_dwords);
    if (IsFableTinyTitleUploadedTextureIdentityBase(
            action->base_address, action->width, action->height)) {
      if (IsFableOneDimensionalTitleUploadedTextureIdentityBase(
              action->base_address, action->width, action->height)) {
        // ADDRESS GATE (census: exact_title_upload_1d_alias). Derived branch
        // key (1-D uploaded-identity base), one-run alias payload per case.
        DirectAddressGateTest(DirectAddressGate::kExactTitleUpload1DAlias, true,
                              action->base_address);
        switch (action->base_address) {
          case 0x1FAB5000u:
            add_exact_texture_identity_address(0x401317D0u);
            break;
          case 0x1FAB4000u:
            add_exact_texture_identity_address(0x40131810u);
            break;
          case 0x1FAB3000u:
            add_exact_texture_identity_address(0x40131850u);
            break;
          case 0x1FAB2000u:
            add_exact_texture_identity_address(0x40131890u);
            break;
          case 0x1FAB1000u:
            add_exact_texture_identity_address(0x401318D0u);
            break;
          case 0x1FAB0000u:
            add_exact_texture_identity_address(0x40131910u);
            break;
          default:
            break;
        }
        add_exact_texture_identity_address(action->base_address);
      } else if (action->base_address == 0x1A0D7000u &&
                 action->width == 16u && action->height == 16u) {
        // ADDRESS GATE (census: exact_title_upload_16x16_alias).
        DirectAddressGateTest(DirectAddressGate::kExactTitleUpload16x16Alias,
                              true, action->base_address);
        add_exact_texture_identity_address(0x404DE490u);
        add_exact_texture_identity_address(0x404DE4D0u);
        add_exact_texture_identity_address(0x404DE550u);
        add_exact_texture_identity_address(0x1A0D7000u);
      }
      // DEAD ADDRESS GATE exact_title_upload_1x1_alias (site 2 of 2) — DELETED
      // 2026-07-26. A third `else if (base == 0x1FC40000 && 1x1)` arm seeded
      // aliases 0x401314D0 / 0x1FC40000. It called DirectAddressGateTest with
      // literal_hit hardcoded TRUE, so reaching it at all would have driven the
      // shared census slot's `matched` above zero; run 288 recorded matched=0,
      // which proves the arm never executed. (The sibling arms are NOT dead in
      // the same way: exact_title_upload_1d_alias measured tested=1 matched=1
      // and exact_title_upload_16x16_alias is never_tested — both kept.)
    }

  }
}

void CollectDirectExactTitleUploadPreflightInterestsLocked(
    const std::vector<FableDirectRenderCommand>& commands,
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter) {
  for (const FableDirectRenderCommand& command : commands) {
    CollectDirectExactTitleUploadPreflightInterestsLocked(
        command, resource_filter, texture_upload_filter);
  }
}

void LogFinalFailedDirectCommandLocked(const char* phase,
                                       const FableDirectRenderCommand& command) {
  if (g_direct_renderer.diagnostics.logged_final_failed_direct_commands >=
      kMaxLoggedFinalFailedDirectCommands) {
    return;
  }

  std::array<uint32_t, 4> missing_slots = {};
  std::array<uint32_t, 4> missing_slot_guests = {};
  std::array<std::array<uint32_t, 4>, 4> missing_slot_fetch = {};
  uint32_t missing_slot_count = 0;
  const bool draw_command = IsDirectDrawCommand(command);
  if (draw_command) {
    const uint32_t texture_slot_mask =
        DirectCommandPixelTextureSlotMaskLocked(command);
    for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
      if (slot < 32u && (texture_slot_mask & (1u << slot)) == 0) {
        continue;
      }
      const uint32_t guest = command.resources.textures[slot];
      if (guest == 0) {
        continue;
      }
      PlumeTrackedResource* texture =
          ResolveDirectSampledTextureForCommandLocked(command, slot);
      if (texture && PlumeGuestImageReady(*texture)) {
        continue;
      }
      if (missing_slot_count >= missing_slots.size()) {
        break;
      }
      const std::array<uint32_t, 6> fetch =
          DirectCommandTextureFetchDwords(command, slot);
      missing_slots[missing_slot_count] = slot;
      missing_slot_guests[missing_slot_count] = guest;
      missing_slot_fetch[missing_slot_count] = {
          fetch[0], fetch[1], fetch[2], fetch[3]};
      ++missing_slot_count;
    }
  }
  const char* last_reason =
      g_direct_renderer.last_failed_direct_draw_reason.empty()
          ? "unknown"
          : g_direct_renderer.last_failed_direct_draw_reason.c_str();

  REXLOG_WARN(
      "Refii plume final failed direct command #{} frame={} phase={} "
      "last_reason={} last_serial={} serial={} draw_serial={} "
      "type={} source={} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
      "ps=0x{:08X} vs_root=0x{:08X}/{} ps_root=0x{:08X}/{} "
      "decl=0x{:08X} vb0=0x{:08X}/{} ib=0x{:08X} "
      "tex0=0x{:08X} tex13=0x{:08X} private_rect={} "
      "inline_vb={} inline_ib={} cache_vb={} cache_ib={} "
      "missing_slots={} s0={}/0x{:08X}/{:08X}:{:08X}:{:08X}:{:08X} "
      "s1={}/0x{:08X}/{:08X}:{:08X}:{:08X}:{:08X} "
      "s2={}/0x{:08X}/{:08X}:{:08X}:{:08X}:{:08X} "
      "s3={}/0x{:08X}/{:08X}:{:08X}:{:08X}:{:08X}",
      ++g_direct_renderer.diagnostics.logged_final_failed_direct_commands,
      g_direct_renderer.current_frame_index, phase ? phase : "unknown",
      last_reason,
      static_cast<unsigned long long>(
          g_direct_renderer.last_failed_direct_draw_serial),
      static_cast<unsigned long long>(command.serial),
      static_cast<unsigned long long>(command.draw_serial),
      static_cast<uint32_t>(command.type),
      static_cast<uint32_t>(command.draw.source),
      command.resources.render_targets[0], command.resources.depth_stencil,
      command.resources.vertex_shader, command.resources.pixel_shader,
      command.resources.shader_roots.vertex_shader.symbol_address,
      static_cast<unsigned long long>(
          command.resources.shader_roots.vertex_shader.call_serial),
      command.resources.shader_roots.pixel_shader.symbol_address,
      static_cast<unsigned long long>(
          command.resources.shader_roots.pixel_shader.call_serial),
      command.resources.vertex_declaration,
      command.resources.streams[0].buffer, command.resources.streams[0].stride,
      command.resources.index_buffer, command.resources.textures[0],
      command.resources.textures[13], command.draw.private_texture_rectangle,
      command.inline_vertex_buffers[0].ready() ? 1 : 0,
      command.inline_index_buffer.ready() ? 1 : 0,
      FableDirectDrawScratchCacheEntryCount(command.draw_serial, false),
      FableDirectDrawScratchCacheEntryCount(command.draw_serial, true),
      missing_slot_count, missing_slots[0],
      missing_slot_guests[0], missing_slot_fetch[0][0],
      missing_slot_fetch[0][1], missing_slot_fetch[0][2],
      missing_slot_fetch[0][3], missing_slots[1], missing_slot_guests[1],
      missing_slot_fetch[1][0], missing_slot_fetch[1][1],
      missing_slot_fetch[1][2], missing_slot_fetch[1][3],
      missing_slots[2], missing_slot_guests[2], missing_slot_fetch[2][0],
      missing_slot_fetch[2][1], missing_slot_fetch[2][2],
      missing_slot_fetch[2][3], missing_slots[3], missing_slot_guests[3],
      missing_slot_fetch[3][0], missing_slot_fetch[3][1],
      missing_slot_fetch[3][2], missing_slot_fetch[3][3]);

  if (RenderDocCaptureOnErrorEnabled()) {
    static uint32_t requested_error_captures = 0;
    char capture_reason[160] = {};
    std::snprintf(capture_reason, sizeof(capture_reason),
                  "final_failed_direct_command_%s_%s_serial_%llu",
                  phase ? phase : "unknown", last_reason,
                  static_cast<unsigned long long>(command.serial));
    if (requested_error_captures < 8u &&
        RequestRenderDocFrameCaptureWithReason(capture_reason)) {
      ++requested_error_captures;
      REXLOG_WARN(
          "Refii plume RenderDoc error-trigger capture armed #{} frame={} "
          "phase={} serial={} reason={}",
          requested_error_captures, g_direct_renderer.current_frame_index,
          phase ? phase : "unknown",
          static_cast<unsigned long long>(command.serial), last_reason);
    }
  }
}


// Per-compose census for the command timing line: run 490's sync_ms=621.9
// looked like "many 14ms calls" until the per-call lines were re-read — it
// was ONE oversized missing-resource call. The count settles that question
// at a glance on every timing line. Reset at compose top, printed (never
// silently omitted) on the compose timing line. Registry-lock domain, so a
// plain counter is enough.
uint32_t g_direct_sync_calls_this_compose = 0;
// Companion census (run 491 dead-address filter): calls that returned through
// the cheap-harvest paths because every requested address was compose-dead.
uint32_t g_direct_sync_early_outs_this_compose = 0;

void LogSkippedUnsupportedDirectDrawLocked(
    const FableDirectRenderCommand& command,
    const char* reason) {
  static uint32_t logged_unsupported_draw_skips = 0;
  if (logged_unsupported_draw_skips >= 32u) {
    return;
  }
  REXLOG_INFO(
      "Refii plume unsupported direct draw packet rejected #{} frame={} "
      "reason={} serial={} draw={} source={} rt=0x{:08X} ds=0x{:08X} "
      "vs=0x{:08X} ps=0x{:08X} decl=0x{:08X} vb0=0x{:08X}/{} "
      "ib=0x{:08X} inline_vb0={}/{} inline_ib={}/{} "
      "wrapper=0x{:08X}/{} context=0x{:016X}",
      ++logged_unsupported_draw_skips,
      g_direct_renderer.current_frame_index,
      reason ? reason : "unknown",
      static_cast<unsigned long long>(command.serial),
      static_cast<unsigned long long>(command.draw_serial),
      static_cast<uint32_t>(command.draw.source),
      command.resources.render_targets[0], command.resources.depth_stencil,
      command.resources.vertex_shader, command.resources.pixel_shader,
      command.resources.vertex_declaration,
      command.resources.streams[0].buffer,
      command.resources.streams[0].stride, command.resources.index_buffer,
      command.inline_vertex_buffers[0].ready() ? 1 : 0,
      command.inline_vertex_buffers[0].byte_size,
      command.inline_index_buffer.ready() ? 1 : 0,
      command.inline_index_buffer.byte_size,
      command.draw.private_texture_wrapper_symbol_address,
      static_cast<unsigned long long>(
          command.draw.private_texture_wrapper_call_serial),
      static_cast<unsigned long long>(
          command.draw.private_texture_wrapper_context_key));
}

bool IsRenderDocEarlyBinkWrapperDrawTargetLocked(
    const FableDirectRenderCommand& command) {
  const uint64_t pixel_hash =
      DirectPixelShaderHashLocked(command.resources.pixel_shader);
  return FableVideoCommandIsEarlyBinkWrapperDrawTarget(command, pixel_hash);
}

bool DirectCommandHasRenderDocTitleHandoffSampleLocked(
    const FableDirectRenderCommand& command) {
  return FableTitleCommandHasHandoffSample(
      command, DirectCommandPixelTextureSlotMaskLocked(command),
      DirectCommandTextureFetchDwords);
}

bool IsRenderDocStartupVideoCopyDrawTargetLocked(
    const FableDirectRenderCommand& command) {
  const uint64_t pixel_hash =
      DirectPixelShaderHashLocked(command.resources.pixel_shader);
  const DirectTextureFetchInfo slot0 = DirectTextureFetchInfoFromDwords(
      DirectCommandTextureFetchDwords(command, 0));
  return FableVideoCommandIsStartupVideoCopyDrawTarget(command, pixel_hash,
                                                       slot0);
}

bool DirectCommandIsTreeBillboardDrawTargetLocked(
    const FableDirectRenderCommand& command) {
  // ADDRESS GATE (census: tree_billboard_heap_tex13). A POOL BOUNDARY guess:
  // "the slot-13 texture object lives above 0x4C000000". Pool layout is a
  // per-run property. The derived discriminator is the one the very next lines
  // already use — the translated pixel shader's marker name — so `derived`
  // measures whether the name test alone decides this classifier.
  if ((command.draw.source != RendererDrawEventSource::kSceneObject &&
       command.draw.source != RendererDrawEventSource::kScratchWrapper) ||
      command.resources.render_targets[0] != kFableTitleVisibleRenderTarget ||
      !DirectAddressGateTest(DirectAddressGate::kTreeBillboardHeapTexture13,
                             command.resources.textures[13] >= 0x4C000000u,
                             command.resources.textures[13])) {
    return false;
  }

  const std::string pixel_shader_name =
      DirectResourceMarkerNameLocked(command.resources.pixel_shader);
  return pixel_shader_name.find("0098_pshader_treebillboard") !=
         std::string::npos;
}


DirectTitleVisibleFramePacketProbe DirectTitleVisibleFramePacketProbeLocked(
    const FableDirectRenderCommand& command) {
  DirectTitleVisibleFramePacketProbe probe;
  if (!IsDirectDrawCommand(command)) {
    return probe;
  }

  for (const FableDirectFrameDatabaseDrawPacket& packet :
       g_direct_renderer.frame_database_draw_packets) {
    if (packet.command_serial != command.serial) {
      continue;
    }

    probe.packet_seen = true;
    probe.rt0 = packet.render_targets[0];
    const FableTitleFramePacketTargetFactsList target_facts =
        DirectTitleFramePacketTargetFactsLocked(packet);
    const FableTitleFramePacketShaderFacts shader_facts =
        DirectTitleFramePacketShaderFactsLocked(packet);
    probe.derived = BuildFableTitleFramePacketDerivedFacts(
        packet, target_facts, shader_facts);
    probe.candidate = probe.derived.visible_producer;
    return probe;
  }

  return probe;
}

bool DirectCommandIsTitleVisibleFramePacketCandidateLocked(
    const FableDirectRenderCommand& command) {
  return DirectTitleVisibleFramePacketProbeLocked(command).candidate;
}

bool IsRenderDocDrawTargetMatchLocked(
    const std::string& target,
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding* framebuffer) {
  (void)framebuffer;
  FableRenderDocDrawTargetFactCallbacks callbacks;
  callbacks.is_exact_bink_indexed_wrapper_draw =
      DirectCommandIsExactBinkIndexedWrapperDrawLocked;
  callbacks.is_early_bink_wrapper_draw_target =
      IsRenderDocEarlyBinkWrapperDrawTargetLocked;
  callbacks.is_startup_video_copy_draw_target =
      IsRenderDocStartupVideoCopyDrawTargetLocked;
  callbacks.is_title_visible_proof_candidate =
      DirectCommandIsTitleVisibleProofCandidateLocked;
  callbacks.is_title_visible_frame_packet_candidate =
      DirectCommandIsTitleVisibleFramePacketCandidateLocked;
  callbacks.is_title_gui_strip_proof_candidate =
      DirectCommandIsTitleGuiStripProofCandidateLocked;
  callbacks.is_title_repeated_mesh_overlay =
      DirectCommandIsTitleRepeatedMeshOverlayLocked;
  callbacks.is_tree_billboard_draw_target =
      DirectCommandIsTreeBillboardDrawTargetLocked;
  callbacks.has_title_handoff_sample =
      DirectCommandHasRenderDocTitleHandoffSampleLocked;
  callbacks.startup_video_pump_active = FableStartupVideoPumpActive;
  callbacks.vertex_shader_hash = DirectVertexShaderHashLocked;
  callbacks.pixel_shader_hash = DirectPixelShaderHashLocked;
  const FableRenderDocDrawTargetFacts facts =
      BuildFableRenderDocDrawTargetFacts(target, command, callbacks);
  const bool matched =
      FableRenderDocDrawTargetMatches(target, command, facts);
  static uint32_t logged_titlevisible_target_probes = 0;
  if (FableRenderDocTargetIsTitleVisible(target) &&
      logged_titlevisible_target_probes < 32u) {
    const DirectTitleVisibleFramePacketProbe probe =
        DirectTitleVisibleFramePacketProbeLocked(command);
    if (matched || probe.packet_seen ||
        command.resources.render_targets[0] == kFableTitleVisibleRenderTarget ||
        facts.title_visible_proof_candidate ||
        facts.title_gui_strip_proof_candidate) {
      REXLOG_INFO(
          "Refii plume RenderDoc titlevisible target probe #{} frame={} "
          "matched={} serial={} source={} rt=0x{:08X} packet_rt=0x{:08X} "
          "visible_proof={} frame_packet={} gui_strip={} writes_visible={} "
          "full_composite={} full_private={} present={} full_scene={} "
          "overlay={} vs=0x{:08X} ps=0x{:08X} vhash=0x{:016X} "
          "phash=0x{:016X} tex0=0x{:08X} tex1=0x{:08X} tex2=0x{:08X} "
          "tex13=0x{:08X}",
          ++logged_titlevisible_target_probes,
          g_direct_renderer.current_frame_index, matched,
          static_cast<unsigned long long>(command.serial),
          static_cast<uint32_t>(command.draw.source),
          command.resources.render_targets[0], probe.rt0,
          facts.title_visible_proof_candidate,
          facts.title_visible_frame_packet_candidate,
          facts.title_gui_strip_proof_candidate,
          probe.derived.writes_visible_target,
          probe.derived.full_frame_composite,
          probe.derived.full_frame_private_copy,
          probe.derived.title_present_producer,
          probe.derived.full_scene_producer,
          probe.derived.overlay_only,
          command.resources.vertex_shader, command.resources.pixel_shader,
          static_cast<unsigned long long>(
              DirectVertexShaderHashLocked(command.resources.vertex_shader)),
          static_cast<unsigned long long>(
              DirectPixelShaderHashLocked(command.resources.pixel_shader)),
          command.resources.textures[0], command.resources.textures[1],
          command.resources.textures[2], command.resources.textures[13]);
    }
  }
  static uint32_t logged_treebillboard_target_probes = 0;
  // ADDRESS GATE (census: tree_billboard_probe_heap_tex13). DIAGNOSTIC ONLY —
  // same pool-boundary guess as the classifier above, gating a probe log line.
  const bool treebillboard_probe_candidate = DirectAddressGateTest(
      DirectAddressGate::kTreeBillboardProbeHeapTexture13,
      command.resources.textures[13] >= 0x4C000000u,
      command.resources.textures[13]);
  if (target == "treebillboard" &&
      command.resources.render_targets[0] == kFableTitleVisibleRenderTarget &&
      treebillboard_probe_candidate &&
      logged_treebillboard_target_probes < 16u) {
    const std::string pixel_shader_name =
        DirectResourceMarkerNameLocked(command.resources.pixel_shader);
    REXLOG_INFO(
        "Refii plume RenderDoc treebillboard target probe #{} frame={} "
        "matched={} source={} direct={} rt=0x{:08X} ps=0x{:08X} "
        "pixel_hash=0x{:016X} tex13=0x{:08X} shader_name='{}'",
        ++logged_treebillboard_target_probes,
        g_direct_renderer.current_frame_index, matched,
        static_cast<uint32_t>(command.draw.source), facts.direct_draw,
        command.resources.render_targets[0], command.resources.pixel_shader,
        static_cast<unsigned long long>(facts.pixel_hash),
        command.resources.textures[13], pixel_shader_name);
  }
  return matched;
}

bool IsRenderDocResolveTargetMatch(const std::string& target,
                                   const FableDirectRenderCommand& command) {
  return FableRenderDocResolveTargetMatches(target, command);
}

void MaybeBeginRenderDocDrawTargetCaptureLocked(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding* framebuffer) {
  static FableRenderDocDrawTargetCaptureState capture_state;
  const std::string target = RenderDocDeepObserverCaptureDrawTarget();
  const bool matched =
      IsRenderDocDrawTargetMatchLocked(target, command, framebuffer);
  if (!FableRenderDocDrawCaptureTargetReady(capture_state, target, matched)) {
    return;
  }

  uint32_t titlemenu_match_slot = 0;
  if (FableRenderDocTakeTitleMenuMatchLogSlot(
          capture_state, target, titlemenu_match_slot)) {
    const uint32_t fb_width = framebuffer ? framebuffer->width : 0u;
    const uint32_t fb_height = framebuffer ? framebuffer->height : 0u;
    const uint32_t fb_color =
        framebuffer ? framebuffer->color_address : 0u;
    REXLOG_INFO(
        "Refii plume RenderDoc titlemenu target matched #{} frame={} "
        "rt=0x{:08X} fb=0x{:08X}/{}x{} source={} vs=0x{:08X} "
        "ps=0x{:08X} decl=0x{:08X} tex0=0x{:08X} tex13=0x{:08X}",
        titlemenu_match_slot, g_direct_renderer.current_frame_index,
        command.resources.render_targets[0], fb_color, fb_width, fb_height,
        static_cast<uint32_t>(command.draw.source),
        command.resources.vertex_shader, command.resources.pixel_shader,
        command.resources.vertex_declaration, command.resources.textures[0],
        command.resources.textures[13]);
  }

  if (BeginRenderDocImmediateFrameCapture("Refii Plume Target Draw")) {
    FableRenderDocMarkDrawCaptureStarted(capture_state, target);
    g_direct_renderer.stats.renderdoc_capture_started = true;
    REXLOG_INFO(
        "Refii plume RenderDoc target-draw capture started "
        "target={} frame={} rt=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
        "tex0=0x{:08X} tex1=0x{:08X} tex2=0x{:08X} tex13=0x{:08X}",
        target, g_direct_renderer.current_frame_index,
        command.resources.render_targets[0], command.resources.vertex_shader,
        command.resources.pixel_shader, command.resources.textures[0],
        command.resources.textures[1], command.resources.textures[2],
        command.resources.textures[13]);
  } else {
    uint32_t failure_slot = 0;
    if (!FableRenderDocTakeDrawCaptureFailureLogSlot(capture_state,
                                                     failure_slot)) {
      return;
    }
    const RenderDocDebuggerStatus status = GetRenderDocDebuggerStatus();
    REXLOG_WARN(
        "Refii plume RenderDoc target-draw capture failed #{} target={} "
        "frame={} rt=0x{:08X} available={} capturing={} pending={} "
        "captures={} template='{}' last_error='{}'",
        failure_slot, target,
        g_direct_renderer.current_frame_index, command.resources.render_targets[0],
        status.available ? 1 : 0, status.capturing ? 1 : 0,
        status.capture_request_pending ? 1 : 0, status.capture_count,
        status.capture_template, status.last_error);
  }
}

void MaybeBeginRenderDocResolveTargetCaptureLocked(
    const FableDirectRenderCommand& command) {
  static FableRenderDocResolveTargetCaptureState capture_state;
  const std::string target = RenderDocDeepObserverCaptureDrawTarget();
  if (!FableRenderDocResolveCaptureTargetReady(capture_state, target,
                                               command)) {
    return;
  }

  if (BeginRenderDocImmediateFrameCapture("Refii Plume Target Resolve")) {
    FableRenderDocMarkResolveCaptureStarted(capture_state, target);
    g_direct_renderer.stats.renderdoc_capture_started = true;
    REXLOG_INFO(
        "Refii plume RenderDoc target-resolve capture started "
        "target={} frame={} serial={} source={} rb_dest=0x{:08X} "
        "rb_pitch=0x{:08X} rb_info=0x{:08X} rb_control=0x{:08X} "
        "rt0=0x{:08X} ds=0x{:08X}",
        target, g_direct_renderer.current_frame_index,
        static_cast<unsigned long long>(command.serial),
        static_cast<uint32_t>(command.draw.source),
        command.resolve.rb_copy_dest_base, command.resolve.rb_copy_dest_pitch,
        command.resolve.rb_copy_dest_info, command.resolve.rb_copy_control,
        command.resources.render_targets[0], command.resources.depth_stencil);
  }
}

// §3.4 executor cutover, identity authority: a classic draw command's
// binding fields are replaced with its GTL packet's — the packet stream
// becomes the identity source of record. Values are expected identical
// (shadow-proven); any real change is counted and logged as a divergence
// alarm. Render state stays command-carried (proven equal, and the PM4
// derivation happens at command build).
bool GtlPacketIdentityAgreesWithCommandLocked(
    const FableDirectRenderCommand& command,
    const refii::gtl::DrawPacket& packet) {
  if (packet.vertex_shader != command.resources.vertex_shader ||
      packet.pixel_shader != command.resources.pixel_shader) {
    return false;
  }
  const auto nonzero_agrees = [](uint32_t command_value,
                                 uint32_t packet_value) {
    return command_value == 0 || packet_value == 0 ||
           command_value == packet_value;
  };
  if (!nonzero_agrees(command.resources.vertex_declaration,
                      packet.vertex_declaration) ||
      !nonzero_agrees(command.resources.index_buffer,
                      packet.index_buffer_pointer) ||
      !nonzero_agrees(command.resources.depth_stencil, packet.depth_target)) {
    return false;
  }
  for (uint32_t slot = 0;
       slot < command.resources.render_targets.size() &&
       slot < packet.render_targets.size(); ++slot) {
    if (!nonzero_agrees(command.resources.render_targets[slot],
                        packet.render_targets[slot])) {
      return false;
    }
  }
  for (uint32_t slot = 0;
       slot < 16u && slot < command.resources.textures.size() &&
       slot < packet.textures.size(); ++slot) {
    uint32_t packet_texture = packet.textures[slot];
    // Authority resolves lazy fetch-only bindings to their tracked texture
    // object before assignment. Identity must compare that same effective
    // value: treating packet.textures[slot] == 0 as "no claim" here and
    // resolving fetch_bases[slot] later allowed a packet to pass the gate,
    // then overwrite a different live command texture (run 215, slot 8:
    // 0x441347A4 -> 0x40137510).
    if (packet_texture == 0 && slot < packet.fetch_bases.size() &&
        packet.fetch_bases[slot] != 0) {
      // Resolve through the SAME base->objects identity map the command builder
      // uses. FindDirectResourceLocked (the old lookup here) answers a different
      // question and returned a device-adjacent fetch-slot pseudo-object: run
      // 210 resolved 0x441428E4 (device was 0x44142480) while the command held
      // the real texture 0x404DF650, so an otherwise IDENTICAL Begin packet —
      // same vs, ps, rt0, ds, decl — was vetoed on this axis alone.
      //
      // One base can name several aliased objects (candidate #512.3 resolved
      // 0x404DF750 against the command's 0x404DF650, one sibling apart), so the
      // question is not "is it the same object id" but "does it name the same
      // console memory". Accept when the command's texture is ANY object
      // registered for this base; fall back to the old lookup only when the
      // identity map has nothing.
      const std::vector<uint32_t>* objects =
          FableDirectFindTextureIdentityObjectsByBase(packet.fetch_bases[slot]);
      if (objects != nullptr && !objects->empty()) {
        const uint32_t command_texture = command.resources.textures[slot];
        if (command_texture != 0 &&
            std::find(objects->begin(), objects->end(), command_texture) !=
                objects->end()) {
          continue;  // same backing memory, aliased object id — agree
        }
        packet_texture = objects->front();
      } else {
        const PlumeTrackedResource* by_base =
            FindDirectResourceLocked(packet.fetch_bases[slot]);
        if (by_base != nullptr && IsImageResource(by_base->kind)) {
          packet_texture = by_base->guest_address;
        }
      }
    }
    if (!nonzero_agrees(command.resources.textures[slot], packet_texture)) {
      return false;
    }
  }
  for (uint32_t slot = 0;
       slot < command.resources.streams.size() &&
       slot < packet.vertex_buffer_pointers.size(); ++slot) {
    if (!nonzero_agrees(command.resources.streams[slot].buffer,
                        packet.vertex_buffer_pointers[slot])) {
      return false;
    }
  }
  return true;
}

// Clear-class counterpart of the draw converter (§3.4/step-5): build an
// executable kClear command straight from a GTL ClearPacket so clears are
// packet-sourced alongside draws (interleaved by packet_id) instead of coming
// from the observer batch. RecordDirectClearCommand only reads the target
// binding (resources.render_targets/depth_stencil) and command.clear.{flags,
// color,depth,stencil,rect}, all of which the packet carries verbatim.
FableDirectRenderCommand BuildDirectCommandFromGtlClearPacketLocked(
    const refii::gtl::ClearPacket& packet) {
  FableDirectRenderCommand command;
  command.serial = packet.packet_id;
  command.draw_serial = packet.packet_id;
  command.type = FableDirectRenderCommandType::kClear;
  command.draw.source = RendererDrawEventSource::kClear;

  command.resources.render_targets = packet.render_targets;
  command.resources.depth_stencil = packet.depth_target;

  command.clear.flags = packet.flags;
  if (packet.color_valid) {
    for (uint32_t i = 0; i < command.clear.color.size() && i < packet.color.size();
         ++i) {
      float channel = 0.0f;
      std::memcpy(&channel, &packet.color[i], sizeof(float));
      command.clear.color[i] = channel;
    }
  }
  command.clear.depth = packet.depth;
  command.clear.stencil = packet.stencil;
  // The packet carries the CALL's own rect (ClearF r5 pointer / ClearRect
  // x-y-w-h) — the device scissor is ambient state and was the wrong proxy.
  if (packet.rect_valid) {
    command.clear.rect.valid = true;
    command.clear.rect.left = packet.rect[0];
    command.clear.rect.top = packet.rect[1];
    command.clear.rect.right = packet.rect[2];
    command.clear.rect.bottom = packet.rect[3];
  }
  command.clear.root_symbol_address = packet.source_symbol;
  return command;
}

// The resolve's own EDRAM clear (D3DDevice_Resolve Flags 0x100/0x200): after
// the copy, the hardware clears the SOURCE surface — color to pClearColor on
// the resolved RT slot, depth/stencil to ClearZ/ClearStencil on the bound DS.
// EndTiling primes each next tile this way, so the clear rect is the resolve's
// own source rect. Everything comes from the wrapper call via the packet.
FableDirectRenderCommand BuildDirectCommandFromGtlResolveClearLocked(
    const refii::gtl::ResolvePacket& packet) {
  FableDirectRenderCommand command;
  command.serial = packet.packet_id;
  command.draw_serial = packet.packet_id;
  command.type = FableDirectRenderCommandType::kClear;
  command.draw.source = RendererDrawEventSource::kClear;

  command.resources.render_targets = packet.dest_targets;
  command.resources.depth_stencil = packet.dest_depth;

  command.clear.flags = 0;
  if ((packet.flags & 0x100u) != 0 && packet.source_select < 4u) {
    command.clear.flags |= 1u << packet.source_select;
    if (packet.clear_color_valid) {
      for (uint32_t i = 0;
           i < command.clear.color.size() && i < packet.clear_color.size();
           ++i) {
        float channel = 0.0f;
        std::memcpy(&channel, &packet.clear_color[i], sizeof(float));
        command.clear.color[i] = channel;
      }
    }
  }
  if ((packet.flags & 0x200u) != 0) {
    command.clear.flags |= 0x30u;
    command.clear.depth = packet.clear_z;
    command.clear.stencil = packet.clear_stencil;
  }
  if (packet.source_rect_valid) {
    command.clear.rect.valid = true;
    command.clear.rect.left = static_cast<int32_t>(packet.source_rect[0]);
    command.clear.rect.top = static_cast<int32_t>(packet.source_rect[1]);
    command.clear.rect.right = static_cast<int32_t>(packet.source_rect[2]);
    command.clear.rect.bottom = static_cast<int32_t>(packet.source_rect[3]);
  }
  command.clear.root_symbol_address = packet.source_symbol;
  return command;
}


// Design step 5 (migration doc / volume §3.4): build an executable command
// straight from a GTL DrawPacket, so the packet stream can DRIVE submission
// instead of only supplying identity authority to an observer-sourced command.
// The GTL captures every draw at the wrapper root (race-free geometry, complete
// device-state snapshot), so a command built here is not subject to the
// observer publish/replay drop pipeline. Resource materialization still comes
// from the resource bridge (AddGtlPacketResourceInterestsLocked); this only
// provides the DRAW. Fields the GTL does not yet capture stay at their defaults.
FableDirectRenderCommand BuildDirectCommandFromGtlDrawPacketLocked(
    const refii::gtl::DrawPacket& packet) {
  FableDirectRenderCommand command;
  command.serial = packet.packet_id;
  command.draw_serial = packet.packet_id;
  // The packet is this command's source: identity, constant banks and inline
  // geometry are all copied right here. Record-time packet-identity authority
  // (ApplyGtlPacketIdentityAuthorityLocked) would only rediscover this same
  // packet by scanning the whole shadow backlog, then re-apply the same
  // banks — measured as the dominant term of drawphase[copy=] at menu shape
  // (run 486: 1534ms for 2375 commands) — so it is skipped for this command.
  command.gtl_packet_sourced = true;

  // DIAGNOSTIC (2026-07-25, unconditional, top-of-function ground truth):
  // every prior hypothesis about WHY the 0x8220A3F0 slot-13 fill never logs
  // checked out on code inspection, so stop reasoning and just observe every
  // packet.source_symbol/vs/ps/fetch_bases[13] value that actually enters
  // this function, in order, no gating. Remove once resolved.
  {
    static uint32_t logged_entry_diag = 0;
    if (logged_entry_diag < 64u) {
      REXLOG_INFO(
          "Refii GTL builder-entry DIAG #{} packet_id={} src=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X} fetch13=0x{:08X} indexed={} "
          "inline_vb_empty={} vb0=0x{:08X}",
          ++logged_entry_diag,
          static_cast<unsigned long long>(packet.packet_id),
          packet.source_symbol, packet.vertex_shader, packet.pixel_shader,
          packet.fetch_bases.size() > 13 ? packet.fetch_bases[13] : 0u,
          packet.indexed ? 1 : 0, packet.inline_vertex_data.empty() ? 1 : 0,
          packet.vertex_buffers[0]);
    }
  }

  switch (packet.source_symbol) {
    case 0x82265CD8u:  // DrawPrimitiveUP
      command.type = FableDirectRenderCommandType::kDrawPrimitiveUP;
      command.draw.source = RendererDrawEventSource::kDrawVerticesUp;
      command.draw.draw_vertices_up = true;
      break;
    case 0x8220A3F0u:  // DrawIndexedUserPrimitives (Bink/UI compose)
      // The observer never promotes this root to kSceneObject (it captures it
      // through its own dedicated path, not the BeginVertices/DrawPrimitive
      // submit-kind switch) -- always kScratchWrapper, regardless of
      // scene_submit_depth.
      command.type =
          packet.indexed ? FableDirectRenderCommandType::kDrawIndexedPrimitive
                         : FableDirectRenderCommandType::kDrawPrimitive;
      command.draw.source = RendererDrawEventSource::kScratchWrapper;
      break;
    case 0x82205F68u:  // BeginVertices
    case 0x82217DB8u:  // BeginIndexedVertices
      command.type =
          packet.indexed ? FableDirectRenderCommandType::kDrawIndexedPrimitive
                         : FableDirectRenderCommandType::kDrawPrimitive;
      // hook_observer.cpp ObserveRendererHookEnter promotes BOTH of these to
      // kSceneObject whenever g_observer.scene_submit_depth != 0 (real scene
      // mesh submission), else kScratchWrapper. Mirror it: without this, real
      // scene draws issued via the Begin family default to kScratchWrapper
      // forever under GTL authority and every kSceneObject-gated classifier
      // (title-scene text/gui-atlas/tree-billboard texture-slot masks) is
      // unreachable for them too.
      command.draw.source = packet.scene_submit_depth != 0
                                 ? RendererDrawEventSource::kSceneObject
                                 : RendererDrawEventSource::kScratchWrapper;
      break;
    default:  // 0x8221C3E8 DrawPrimitive / 0x8221DFC0 DrawIndexedPrimitive
      command.type =
          packet.indexed ? FableDirectRenderCommandType::kDrawIndexedPrimitive
                         : FableDirectRenderCommandType::kDrawPrimitive;
      // hook_observer.cpp promotes both DrawPrimitive and DrawIndexedPrimitive
      // to kSceneObject when scene_submit_depth != 0 (real scene mesh
      // submission) -- this is the root cause fix for tree-billboard/title-
      // scene texture classification being unreachable under GTL authority
      // (see refii-tree-billboard-stride-gate memory). Simplification: the
      // observer ALSO promotes a non-indexed DrawPrimitive to kSceneObject
      // when it is a "scene batch terminal draw" with a valid batch marker,
      // even at depth==0; the GTL does not track that marker, so a terminal
      // draw issued right as the guest exits scene-submit scope could still
      // classify as kD3DWrapper here. Not yet observed to matter in practice.
      command.draw.source = packet.scene_submit_depth != 0
                                 ? RendererDrawEventSource::kSceneObject
                                 : RendererDrawEventSource::kD3DWrapper;
      break;
  }

  command.resources.vertex_shader = packet.vertex_shader;
  command.resources.pixel_shader = packet.pixel_shader;
  command.resources.vertex_declaration = packet.vertex_declaration;
  command.resources.render_targets = packet.render_targets;
  command.resources.depth_stencil = packet.depth_target;
  command.resources.index_buffer = packet.index_buffer;
  for (uint32_t slot = 0; slot < command.resources.textures.size() &&
                          slot < packet.textures.size();
       ++slot) {
    command.resources.textures[slot] = packet.textures[slot];
  }
  // Stream bindings. The packet's RESOLVED data pointer (object+0x18 decoded at
  // SetStreamSource) is the authority, not the raw stream object: it is the
  // domain the command pipeline consumes, which is why the packet<->command
  // match predicate above and ApplyGtlPacketIdentityAuthorityLocked both
  // compare/assign exactly this pair (pointer + stride).
  //
  // OFFSET-FOLD CONVENTION (same note as the identity-authority path): the
  // command folds the stream offset INTO the resolved pointer and leaves this
  // field zero, while the packet keeps the raw offset in vertex_offsets. They
  // are two representations of one binding — do NOT also copy
  // packet.vertex_offsets here or the vertex data would be advanced twice.
  //
  // UP/inline draws overwrite stream 0 further down (the inline_vertex_data
  // block anchors it on the payload's own guest address), so the stale device
  // stream a UP draw inherits never survives.
  for (uint32_t slot = 0; slot < command.resources.streams.size() &&
                          slot < packet.vertex_buffer_pointers.size();
       ++slot) {
    command.resources.streams[slot].buffer =
        packet.vertex_buffer_pointers[slot];
    command.resources.streams[slot].offset = 0;
    command.resources.streams[slot].stride = packet.vertex_strides[slot];
  }

  // Draw parameters. Without these the command carries primitive_type 0,
  // ConvertPrimitiveTopology answers UNKNOWN and plume asserts on the first
  // pipeline it builds — this is the axis that makes a packet EXECUTABLE.
  //
  // The packet's field names follow each guest ROOT's register map
  // (refii_gtl.cpp BuildDrawPacketLocked), which is not one-to-one with
  // RendererLastDrawSnapshot's:
  //   * primitive_type / start_index / base_vertex map straight across.
  //   * the COUNT arrives in one of two packet fields depending on the root.
  //     The non-indexed roots write primitive_count (D3DDevice_DrawVertices r6,
  //     DrawVerticesUP r5, BeginVertices r5) — a MISNOMER that holds a VERTEX
  //     count, see the deliberate field comment in refii_gtl.h. The indexed
  //     roots write index_count instead: D3DDevice_DrawIndexedVertices
  //     (0x8221DFC0) r7 = IndexCount — IDA-confirmed, r7 is what shifts into
  //     the PM4 draw initiator's num_indices field — and the quad utility
  //     Render_DrawIndexedQuadUP (0x8220A3F0) stores r4*3, its index count.
  //     D3DDevice_BeginIndexedVertices (0x82217DB8) joined them 2026-07-25:
  //     it also states IndexCount in r7 (same num_indices shift), and writes
  //     primitive_count from r6 NumVertices. Before that fix it shared
  //     BeginVertices' map, so BOTH counts read 0 here (r5 is its
  //     BaseVertexIndex) and the count pin below could never fire for it.
  // RendererLastDrawSnapshot has ONE count slot for both, and the observer
  // fills it the same way (hook_observer.cpp: primitive_count = r6 for
  // DrawPrimitive, = r7 for DrawIndexedPrimitive). Every consumer treats it as
  // a vertex/index count verbatim — DrawVertexOrIndexCount() returns it
  // unchanged for every supported topology, and DirectDrawVertexOrIndexCount()
  // returns it directly for the exact Bink indexed wrapper. So prefer
  // index_count whenever the root supplied one; primitive_count otherwise.
  command.draw.draw.indexed = packet.indexed;
  command.draw.draw.primitive_type = packet.primitive_type;
  command.draw.draw.start = packet.start_index;
  command.draw.draw.base_vertex = packet.base_vertex;
  command.draw.draw.primitive_count =
      packet.index_count != 0 ? packet.index_count : packet.primitive_count;

  // Begin-family count pin. These roots take their count as an argument and
  // issue the draw from inside the wrapper body, so pin it explicitly rather
  // than letting it be re-derived. EXCEPT for D3DPT_RECTLIST (primitive 8):
  // the executor expands a 3-corner rect to a 4-corner quad, and that
  // expansion only runs while
  // vertex_or_index_count is 0. Pinning it to the raw VertexCount (3) here
  // short-circuited the expansion and rasterized ONLY the first triangle: the
  // "diagonal" half-screen composite (RenderDoc frame3357 eid=746, present RT
  // 0x4011F370, one triangle TL/BL/TR). So do NOT pin the count for a rectlist —
  // leave it 0 and let the primitive-8 path own the count and the synthesized
  // fourth corner. (Rectlists are non-indexed, so this only affects BeginVertices.)
  if ((packet.source_symbol == 0x82205F68u ||
       packet.source_symbol == 0x82217DB8u) &&
      packet.primitive_type != 8u) {
    if (packet.index_count != 0) {
      command.draw.vertex_or_index_count = packet.index_count;
    } else if (packet.primitive_count != 0) {
      command.draw.vertex_or_index_count = packet.primitive_count;
    }
  }
  // Index WIDTH, from the one root that states one. command.draw.index_format
  // is a byte width (every consumer runs it through NormalizeDirectIndexFormat,
  // which answers 4 only for a literal 4 and 2 otherwise), NOT a D3DFORMAT — so
  // translate rather than copying the packet's raw IndexDataFormat. Gated on
  // the root symbol because 0 is a valid raw format value and could not
  // otherwise be told apart from "this root states no index format".
  if (packet.source_symbol == 0x82217DB8u) {
    command.draw.index_format = (packet.index_format & 4u) != 0 ? 4u : 2u;
  }
  command.draw.base_vertex = static_cast<int32_t>(packet.base_vertex);
  command.draw.base_vertex_valid = true;

  // Baseline BEFORE the valid-mask copy: an unwritten slot must read the D3D
  // device default, not zero. The mask gate below is right (it keeps a stated
  // zero meaningful), but with an all-zero starting array "never dispatched"
  // and "explicitly set to 0" collapse to the same value, which is how ZFUNC
  // came out as NEVER. Defaults are the guest's own, read from its
  // {get,set,default} table at 0x83315E70 (see MakeDefaultRenderStates in
  // refii_gtl.cpp); the packet carries them because the GTL mirror is seeded
  // with the same block.
  command.pipeline.render_states = packet.render_states;
  for (uint32_t slot = 0; slot < command.pipeline.render_states.size() &&
                          slot < packet.render_states.size();
       ++slot) {
    if ((packet.render_state_valid_mask & (1u << slot)) != 0u) {
      command.pipeline.render_states[slot] = packet.render_states[slot];
    }
  }
  // Live RB_DEPTHCONTROL wins outright when the draw read it (see
  // FableDirectPipelineState::live_depth_control): it is the resolved word the
  // ring carries, so it needs no slot reconstruction at all.
  command.pipeline.live_depth_control = packet.live_depth_control;
  command.pipeline.live_depth_control_valid = packet.live_depth_control_valid;
  // Collapse census (temporary, delete once the live word is trusted): how
  // often the live register and the slot reconstruction disagree, and on what.
  // Expect zfunc to be the loud one -- derived reads 0 (NEVER) for every draw
  // whose ZFUNC the guest never dispatched.
  {
    static std::atomic<uint64_t> live_seen{0};
    static std::atomic<uint64_t> derived_disagreed{0};
    static std::atomic<uint32_t> logged{0};
    if (packet.live_depth_control_valid) {
      const uint32_t derived =
          DirectDepthControlFromRenderStates(command.pipeline.render_states);
      const uint64_t seen = ++live_seen;
      if (derived != packet.live_depth_control) {
        const uint64_t disagreed = ++derived_disagreed;
        const uint32_t n = ++logged;
        if (n <= 24u || (seen & 8191u) == 0u) {
          REXLOG_INFO(
              "Refii GTL depth-control LIVE vs derived #{} live=0x{:08X} "
              "(en={} wr={} func={}) derived=0x{:08X} (en={} wr={} func={}) "
              "seen={} disagreed={} src=0x{:08X} rt=0x{:08X} ds=0x{:08X}",
              n, packet.live_depth_control,
              (packet.live_depth_control >> 1u) & 1u,
              (packet.live_depth_control >> 2u) & 1u,
              (packet.live_depth_control >> 4u) & 7u, derived,
              (derived >> 1u) & 1u, (derived >> 2u) & 1u,
              (derived >> 4u) & 7u,
              static_cast<unsigned long long>(seen),
              static_cast<unsigned long long>(disagreed),
              packet.source_symbol, command.resources.render_targets[0],
              command.resources.depth_stencil);
        }
      }
    }
  }

  // LIVE CULL AUTHORITY (2026-07-25). The mirror this loop just copied only
  // advances when the SetRenderState_CullMode root (0x8220B470) fires, and IDA
  // shows the engine reaches that function through the device's D3DRS call
  // table, not a direct call (one direct caller binary-wide: the Scaleform UI).
  // Wherever that indirect dispatch misses, slot 19 keeps our session default
  // of 6 (cull-back / CW-front) and any draw whose real winding disagrees emits
  // nothing. packet's live_mode_control is the dword the GPU itself consumes,
  // read post-flush in the draw root's exit block, so it wins over the mirror on
  // both carriers (the render state AND the PM4 raster word derived from it).
  if (packet.live_mode_control_valid && RendererGtlLiveCullAuthorityEnabled()) {
    const uint32_t live_cull = packet.live_mode_control & 0x7u;
    const uint32_t mirror_cull =
        command.pipeline.render_states[kFableRenderStateCullMode] & 0x7u;
    command.pipeline.render_states[kFableRenderStateCullMode] = live_cull;
    command.pipeline.pm4.pa_su_sc_mode_cntl =
        (command.pipeline.pm4.pa_su_sc_mode_cntl & 0xFFFFFFF8u) | live_cull;
    ++g_direct_renderer.gtl_live_cull_applied;
    if (live_cull != mirror_cull) {
      ++g_direct_renderer.gtl_live_cull_diverged;
    }
  }

  if (!packet.vs_float_constants.empty()) {
    const size_t count = std::min<size_t>(command.constants.vertex_float.size(),
                                          packet.vs_float_constants.size());
    std::copy_n(packet.vs_float_constants.begin(), count,
                command.constants.vertex_float.begin());
  }
  // c0-c3 WorldViewProjection override REMOVED (2026-07-25). It read the
  // live device bank at packet ENTER time -- i.e. BEFORE the guest's state
  // flush -- so it returned the previous draw's stale value (observed: a
  // constant 6.3026886 for every draw in the frame, matching and
  // non-matching shaders alike). It also fired 2588x per run while the GPU
  // buffer still read zero, because ApplyGtlPacketIdentityAuthorityLocked
  // re-applies the whole packet bank at record time and reverted it. The
  // real fix is in refii_gtl.cpp: refresh the PACKET's float banks POST-FLUSH
  // in the 0x8220A3F0 exit block, exactly as the Begin-family roots already
  // do -- which makes the packet bank correct at source and this special
  // case unnecessary.
  if (!packet.ps_float_constants.empty()) {
    const size_t count = std::min<size_t>(command.constants.pixel_float.size(),
                                          packet.ps_float_constants.size());
    std::copy_n(packet.ps_float_constants.begin(), count,
                command.constants.pixel_float.begin());
  }
  // constants.fetch: populate from the packet's FULL 6-dword-per-slot fetch
  // block when the draw self-read it (fetch_dwords_valid). This is the axis that
  // was silently missing on every GTL command: FableCommandTextureFetchDwords
  // reads fetch[slot*6..slot*6+5], and EVERY fetch-based classifier/binder — the
  // Bink plane detection (DirectCommandIsExactBinkIndexedWrapperDrawLocked) and
  // the maze's decoded-plane substitution — reads it. With it zero the GTL video
  // draws never classified as Bink and the title background rendered WHITE
  // (runs 214-216). The OLD hazard was packing per-slot BASES into the 6-dword
  // space (garbage); this copies the ACTUAL captured fetch dwords, so GTL
  // commands are byte-identical to observer commands and the existing machinery
  // engages unchanged. When the draw did not self-read fetch, leave it zero (the
  // prior design-blessed absent-fetch/object-identity route) rather than write
  // partial data.
  if (packet.fetch_dwords_valid) {
    const size_t count = std::min<size_t>(command.constants.fetch.size(),
                                          packet.fetch_dwords.size());
    std::copy_n(packet.fetch_dwords.begin(), count,
                command.constants.fetch.begin());
  }

  // constants.{vertex,pixel}_{bool,int}: state-fidelity audit F1 (2026-08-01).
  // The whole guest→observer capture chain for shader-flow constants works
  // (SetVertexShaderConstantB pack verified bit-for-bit; ledger publish,
  // SET_CONSTANT, and register-mirror deliveries all land in
  // g_fable_direct.constants) — but this builder default-constructed them, so
  // every GTL-authoritative draw shipped g_Booleans == 0 while pixel shaders
  // reference up to b9. Mirror the sampler fallback: copy the live banks.
  FableDirectCopyShaderFlowConstants(
      command.constants.vertex_bool, command.constants.pixel_bool,
      command.constants.vertex_int, command.constants.pixel_int,
      command.constants.vertex_int_valid_mask,
      command.constants.pixel_int_valid_mask);

  // Bink wrapper compose (0x8220A3F0 + YCrCb pixel shader): the wrapper binds
  // its Y/Cr/Cb planes through fetch-constant writes, not the SetTexture root,
  // so packet.textures[0..2] are empty and the converted command drew with NO
  // plane bindings (compose-trace frame 1019: ps=none smp=none => white
  // video). The packet DOES state the plane memory in fetch_bases; resolve
  // each base to its texture object through the correctly-keyed base->objects
  // identity map and bind it. Packet-stated data + registry, no observation.
  if (packet.source_symbol == 0x8220A3F0u) {
    const uint64_t video_ps_hash =
        DirectPixelShaderHashLocked(packet.pixel_shader);
    const bool known_video_ps =
        FableVideoCommandUsesKnownVideoPixelShader(video_ps_hash);
    // Diagnostic (run 214 white title background): plane fill fired 0 times, but
    // the log sits after the per-slot continue, so 0 could be "ps not known" OR
    // "fetch_bases[0..2] all zero". Name which so the fix is targeted.
    static uint32_t logged_video_plane_diag = 0;
    if (logged_video_plane_diag < 24u) {
      REXLOG_INFO(
          "Refii GTL video plane DIAG #{} ps=0x{:08X} ps_hash=0x{:016X} "
          "known={} fetch0=0x{:08X} fetch1=0x{:08X} fetch2=0x{:08X} "
          "tex0=0x{:08X} tex1=0x{:08X} tex2=0x{:08X}",
          ++logged_video_plane_diag, packet.pixel_shader, video_ps_hash,
          known_video_ps ? 1 : 0,
          packet.fetch_bases.size() > 0 ? packet.fetch_bases[0] : 0u,
          packet.fetch_bases.size() > 1 ? packet.fetch_bases[1] : 0u,
          packet.fetch_bases.size() > 2 ? packet.fetch_bases[2] : 0u,
          command.resources.textures[0], command.resources.textures[1],
          command.resources.textures[2]);
    }
    if (known_video_ps) {
      // The packet's root IS the Bink indexed-vertices wrapper builder
      // (kFableBinkIndexedVerticesWrapperBuilderSymbol == 0x8220A3F0). Stamp
      // it so DirectCommandIsExactBinkIndexedWrapperDrawLocked classifies the
      // GTL-built command and the whole existing video path engages: plane
      // slot binding, the video pipeline-state override, present latching.
      // Without this the command failed classification and drew with zero
      // descriptor indices (sampling the default white texture at heap 0).
      command.draw.private_texture_wrapper_symbol_address =
          packet.source_symbol;
      static uint32_t logged_video_plane_fills = 0;
      for (uint32_t slot = 0; slot < 3u; ++slot) {
        if (slot >= packet.fetch_bases.size() ||
            packet.fetch_bases[slot] == 0) {
          continue;
        }
        // OVERRIDE, not fill-if-empty. The plane slots already carry the
        // SetTexture-tracked device.textures value, which for this Bink compose
        // is the previous draw's leftover — NOT the Y/Cr/Cb plane. The old
        // `textures[slot] != 0 -> continue` guard therefore skipped every slot
        // (run 216: fetch0/1/2 populated but 0 fills), leaving the wrong texture
        // bound -> white video. The fetch-constant plane the Bink upload decoded
        // into is authoritative here; resolve the base to its plane object and
        // OVERWRITE the slot.
        const std::vector<uint32_t>* objects =
            FableDirectFindTextureIdentityObjectsByBase(
                packet.fetch_bases[slot]);
        const uint32_t plane_object =
            (objects != nullptr && !objects->empty()) ? objects->front() : 0u;
        if (plane_object != 0) {
          command.resources.textures[slot] = plane_object;
        }
        if (logged_video_plane_fills < 24u) {
          REXLOG_INFO(
              "Refii GTL video plane fill slot={} base=0x{:08X} object=0x{:08X} "
              "prev_tex=0x{:08X}",
              slot, packet.fetch_bases[slot],
              command.resources.textures[slot],
              packet.textures.size() > slot ? packet.textures[slot] : 0u);
          ++logged_video_plane_fills;
        }
      }
    }
  }

  // Begin-family texture binding (0x82205F68 BeginVertices / 0x82217DB8
  // BeginIndexedVertices / 0x8220A3F0 the shared quad-wrapper utility): like
  // the Bink wrapper case above, these lazy-dispatch draws bind their textures
  // through the device fetch-constant block, NOT SetTexture, so packet.textures
  // is empty/stale. The authoritative EXIT construction (refii_gtl.cpp)
  // captured the applied fetch bases into packet.fetch_bases; resolve each to
  // its texture object through the same correctly-keyed base->objects identity
  // map the Bink path uses, for every slot the tracked textures did not already
  // claim. Without this the GTL-owned composite quads would draw textureless.
  // Packet-stated data + registry, no observation.
  //
  // 0x8220A3F0 ADDED (2026-07-25, IDA-traced): this is a Lionhead engine
  // utility, not a distinct D3D9 endpoint -- it wraps D3DDevice_BeginIndexed
  // Vertices internally (confirmed via xrefs_to(0x82217DB8): its only callers
  // are this wrapper, a thin DrawIndexedVerticesUP shim, and one debug-gizmo
  // drawer). It is shared by Bink video, UI panel composites, AND scene
  // content (tree-billboard impostors, ordinary mesh-material submission --
  // confirmed by decompiling several of its ~60 callers, none of which is a
  // "tree renderer": there is no per-content-type endpoint in Fable's D3D9
  // call graph, only this one shared quad-draw primitive). The video-specific
  // block above only resolves slots 0-2 when the pixel shader is a KNOWN Bink
  // shader; every other 0x8220A3F0 draw (tree billboards included) previously
  // got NO fetch-base resolution at all, so command.resources.textures[] sat
  // at all-zero regardless of which RendererDrawEventSource classified the
  // draw. Adding it here is content-agnostic and heuristic-free: it resolves
  // whatever the hardware fetch-constant state actually captured, for any
  // slot, the same way already proven correct for BeginVertices/
  // BeginIndexedVertices -- no shader-hash or draw-source guessing involved.
  // SOURCE-SYMBOL GATE REMOVED 2026-08-03 — this ran for three wrapper roots
  // only (0x82205F68 BeginVertices, 0x82217DB8 BeginIndexedVertices,
  // 0x8220A3F0 the quad wrapper) and therefore never for the plain
  // 0x8221C3E8 DrawPrimitive / 0x8221DFC0 DrawIndexedPrimitive roots — the
  // default arm of the switch above, and the arm that classifies gameplay as
  // kSceneObject (:53433-53449). Three clauses covering menu, title and video
  // with none covering the root the world draws through is the same shape as
  // the render-target mint gate, which had clauses for kScratchWrapper and
  // title but none for kSceneObject.
  //
  // The block's own argument for adding 0x8220A3F0 — "content-agnostic and
  // heuristic-free: it resolves whatever the hardware fetch-constant state
  // actually captured, for any slot" — is root-agnostic, and the hardware
  // model says the fill is REQUIRED, not merely allowed: D3DDevice_SetTexture
  // (0x821B6EE0) writes the bound-pointer array at dev+0x3100+4*Sampler and
  // the fetch-constant shadow at dev+0x480+24*Sampler together, but on a NULL
  // texture 0x821B6F00 `beq -> loc_821B6FE4` skips the whole fetch block and
  // zeroes ONLY the pointer array. The constant stays live and the GPU keeps
  // sampling the previous descriptor. So packet.textures[slot]==0 with a live
  // packet.fetch_bases[slot] is exactly the state real hardware samples, and
  // dropping the binding to zero is our bookkeeping diverging from the console.
  //
  // Fill-if-empty is preserved for every non-quad-wrapper root, so this can
  // only ADD a binding where the command had none; it never overrides a stated
  // one. The packet<->command identity predicate at :53224-53255 already
  // resolves fetch_bases this way for packets of every source symbol, so this
  // also removes a builder/matcher disagreement about what a slot names.
  {
    // OVERRIDE for 0x8220A3F0 (2026-07-25, root-caused via control-flow
    // checkpoints, not guessed): command.resources.textures[] is seeded from
    // packet.textures[], which mirrors g_state.device.textures[] -- fed by
    // the kSetTexture hook. That hook is not permanently dead the way
    // docs/refii_render_path_map.md's menu-phase measurement suggested; in
    // deeper gameplay/character-select content it fires at least once and
    // leaves a STALE claim sitting in a device-array slot forever after
    // (nothing ever resets it). Proof: CKPT-B showed command.resources.
    // textures[13] already holding 0x40130590 (a generic 1x1 default/
    // placeholder texture, confirmed via this run's own "exact texture fetch
    // binding action" logs) for EVERY 0x8220A3F0 packet BEFORE this loop even
    // ran -- so the existing "only fill if empty" rule silently protected a
    // wrong default forever, for tree billboards and any other quad-wrapper
    // content sharing that slot. This is the EXACT same problem the Bink
    // block above already solved for slots 0-2 ("OVERRIDE, not
    // fill-if-empty... the plane slots already carry the SetTexture-tracked
    // device.textures value... NOT the Y/Cr/Cb plane") -- extend that same
    // precedent to the rest of this root's slots instead of the generic
    // fill-if-empty rule BeginVertices/BeginIndexedVertices use (left
    // unchanged there -- no evidence those are affected the same way).
    const bool is_quad_wrapper = packet.source_symbol == 0x8220A3F0u;
    for (uint32_t slot = 0; slot < command.resources.textures.size() &&
                            slot < packet.fetch_bases.size();
         ++slot) {
      if (packet.fetch_bases[slot] == 0) {
        continue;
      }
      if (!is_quad_wrapper && command.resources.textures[slot] != 0) {
        // BEGIN-FAMILY DIVERGENCE CHECK (2026-07-25): fill-if-empty was left
        // unchanged here for BeginVertices/BeginIndexedVertices per the
        // comment above ("no evidence those are affected the same way") --
        // this is that evidence-gathering, not a behavior change. Resolve
        // what the fetch-base WOULD have produced and compare against the
        // already-present (SetTexture-mirror-sourced) value without
        // overwriting it, so a future run shows whether Begin-family slots
        // silently protect a stale claim the same way the quad-wrapper root
        // did before its override fix (see
        // refii-tree-billboard-stale-default-override.md).
        // OPT-IN 2026-07-28. This probe answered its question: it logged ZERO
        // times across the whole of run 369, so `logged_begin_family_divergence`
        // never advanced, the 4000 cap never filled, and the lookup below ran
        // on EVERY non-zero fetch-base slot of EVERY draw for the entire run
        // while producing no output at all.
        //
        // The cost is not the log, it is the lookup:
        // FableDirectFindTextureIdentityObjectsByBase takes a recursive_mutex,
        // does a map find, and then a std::map operator[] site-record keyed on
        // std::source_location -- per call. With 16 texture slots and
        // submitted=189 draws that is up to ~3,000 dead lookups per frame,
        // inside RecordFableDirectFrame, which a core-dump stack caught in the
        // act and which the timing shows is 79% of the direct-command frame
        // (record_ms=60.7 of total_ms=76.4, ~320us per draw).
        //
        // A cap that only advances when the RARE event fires does not bound the
        // cost of looking for it. Set REFII_RENDERER_BEGIN_FAMILY_DIVERGENCE=1
        // to re-arm.
        static const bool begin_family_divergence_probe_enabled = [] {
          const char* value =
              std::getenv("REFII_RENDERER_BEGIN_FAMILY_DIVERGENCE");
          return value != nullptr && value[0] != '\0' && value[0] != '0';
        }();
        static uint32_t logged_begin_family_divergence = 0;
        if (begin_family_divergence_probe_enabled &&
            logged_begin_family_divergence < 4000u) {
          const std::vector<uint32_t>* fetch_objects =
              FableDirectFindTextureIdentityObjectsByBase(
                  packet.fetch_bases[slot]);
          const uint32_t fetch_resolved =
              (fetch_objects != nullptr && !fetch_objects->empty())
                  ? fetch_objects->front()
                  : 0u;
          if (fetch_resolved != 0 &&
              fetch_resolved != command.resources.textures[slot]) {
            ++logged_begin_family_divergence;
            REXLOG_INFO(
                "Refii GTL begin-family texture divergence #{} slot={} "
                "existing=0x{:08X} fetch_base=0x{:08X} fetch_resolved=0x{:08X} "
                "ps=0x{:08X} source_symbol=0x{:08X}",
                logged_begin_family_divergence, slot,
                command.resources.textures[slot], packet.fetch_bases[slot],
                fetch_resolved, packet.pixel_shader, packet.source_symbol);
          }
        }
        continue;
      }
      const std::vector<uint32_t>* objects =
          FableDirectFindTextureIdentityObjectsByBase(packet.fetch_bases[slot]);
      if (objects != nullptr && !objects->empty()) {
        command.resources.textures[slot] = objects->front();
      }
      // RESOLVE-OUTCOME CHECK (2026-07-25): the override fix (above) proved
      // reachable via CKPT-B, but its downstream resolve success/failure was
      // never actually observed -- CKPT-C (which would have shown it) was
      // removed before a build with the override ever ran. RenderDoc still
      // shows ps=none for tree draws with this fix in place, so this is the
      // one remaining unverified link. Fully isolated counter (not shared
      // with anything), gated only on slot==13 && is_quad_wrapper.
      // 0x1FC40000 is the confirmed Bink/generic 1x1 placeholder -- it
      // resolves correctly (14 aliases) and is NOT the case under
      // investigation. Exclude it so the cap is spent on everything ELSE
      // (including, eventually, a tree packet) instead of being exhausted by
      // early-run Bink-phase repeats again.
      if (slot == 13u && is_quad_wrapper &&
          packet.fetch_bases[slot] != 0x1FC40000u) {
        static uint32_t logged_resolve_outcome = 0;
        // Cap raised 500->8000 (2026-07-25): same blind spot as
        // logged_tree_checkpoint above -- exhausted before reaching the
        // RenderDoc capture frame in run250, so the actual resolve outcome
        // at the inspected frame was never observed.
        if (logged_resolve_outcome < 64u) {
          ++logged_resolve_outcome;
          REXLOG_INFO(
              "Refii GTL RESOLVE-OUTCOME #{} packet_id={} fetch13=0x{:08X} "
              "objects_ptr_null={} objects_empty={} objects_count={} "
              "final_tex13=0x{:08X}",
              logged_resolve_outcome,
              static_cast<unsigned long long>(packet.packet_id),
              packet.fetch_bases[slot], objects == nullptr ? 1 : 0,
              (objects != nullptr && objects->empty()) ? 1 : 0,
              objects != nullptr ? static_cast<uint32_t>(objects->size())
                                  : 0u,
              command.resources.textures[slot]);
        }
      }
    }
  }

  // Sampler states (§3.2 self-containment): the packet carries the packed
  // sampler state captured at the SetSamplerState roots — the at-draw truth.
  // The current-state read remains only as the fallback for slots never
  // written this session (valid-mask bit clear). The divergence counter
  // guards the cutover: packet-vs-current mismatches mean a draw would have
  // sampled with stale drain-time state under the old read.
  static uint64_t sampler_packet_slots = 0;
  static uint64_t sampler_divergences = 0;
  for (uint32_t slot = 0; slot < command.sampler_states.size(); ++slot) {
    const uint32_t current = FableDirectGetPackedSamplerState(slot);
    if (slot < packet.sampler_states.size() &&
        (packet.sampler_state_valid_mask & (1u << slot)) != 0) {
      command.sampler_states[slot] = packet.sampler_states[slot];
      ++sampler_packet_slots;
      if (packet.sampler_states[slot] != current) {
        ++sampler_divergences;
        static uint32_t logged_sampler_divergences = 0;
        if (logged_sampler_divergences < 16u ||
            (sampler_divergences % 4096u) == 0u) {
          ++logged_sampler_divergences;
          REXLOG_INFO(
              "Refii GTL sampler divergence #{} slot={} packet=0x{:08X} "
              "current=0x{:08X} src=0x{:08X} ps=0x{:08X} (packet wins) "
              "cum={}/{}",
              logged_sampler_divergences, slot, packet.sampler_states[slot],
              current, packet.source_symbol, packet.pixel_shader,
              static_cast<unsigned long long>(sampler_divergences),
              static_cast<unsigned long long>(sampler_packet_slots));
        }
      }
    } else {
      command.sampler_states[slot] = current;
    }
  }

  if (!packet.inline_vertex_data.empty()) {
    FableDirectInlineBuffer& vb = command.inline_vertex_buffers[0];
    vb.address = packet.up_vertex_data;
    vb.byte_size = static_cast<uint32_t>(packet.inline_vertex_data.size());
    vb.stride_or_format = packet.up_vertex_stride;
    vb.pinned_per_draw = true;
    // The packet carries the guest's raw big-endian bytes (GTL memcpy); the
    // record path's inline contract is host-order dwords (observer payloads
    // arrive that way — its capture reads through LoadGuestU32). Un-swapped
    // vertices read as denormals ~0, collapsing the quad to a point at the
    // half-pixel origin — the white-video postvs proof (all four vertices at
    // NDC -0.99948,-0.99907 = screen 0,0 + half-pixel). Vertex elements are
    // 32-bit (floats/D3DCOLOR), so a per-dword swap is the full conversion.
    vb.data = packet.inline_vertex_data;
    const size_t dword_bytes = vb.data.size() & ~size_t{3};
    for (size_t offset = 0; offset < dword_bytes; offset += 4) {
      uint32_t dword;
      std::memcpy(&dword, vb.data.data() + offset, sizeof(dword));
      dword = __builtin_bswap32(dword);
      std::memcpy(vb.data.data() + offset, &dword, sizeof(dword));
    }
    // Payload write site: stamp the content hash over the SWAPPED bytes the
    // bind will hash-key on (see FableDirectInlineBuffer::content_hash).
    vb.content_hash = DirectHashBytes(vb.data.data(), vb.byte_size);
    // Anchor stream slot 0 on the inline payload's own guest address, exactly
    // as the observer's ApplyDrawEventScratchBuffersLocked does for scratch/UP
    // draws. UP draws issue no SetStreamSource, so whatever stream[0] the
    // device snapshot carries is a STALE binding from an unrelated draw (a
    // tail-inserted video compose inherited a stride-304 scene stream and
    // rasterized a degenerate quad — postvs proof, capture frame 3117). The
    // packet's own payload is the authority for a UP draw: override
    // unconditionally.
    command.resources.streams[0].buffer = packet.up_vertex_data;
    command.resources.streams[0].offset = 0;
    command.resources.streams[0].stride = packet.up_vertex_stride;
  }
  if (!packet.inline_index_data.empty()) {
    FableDirectInlineBuffer& ib = command.inline_index_buffer;
    ib.address = packet.up_index_data;
    ib.byte_size = static_cast<uint32_t>(packet.inline_index_data.size());
    // Index width from the root that stated one. Only BeginIndexedVertices
    // (0x82217DB8) carries an IndexDataFormat, and IDA shows the guest decodes
    // exactly bit 2 of it: set = 32-bit indices (it is what ORs the draw
    // initiator's 0x800 INDEX_SIZE bit). Every other root leaves index_format
    // at 0, which yields 2 — the 16-bit triangle-list UP contract that was
    // hardcoded here before.
    ib.stride_or_format = (packet.index_format & 4u) != 0 ? 4u : 2u;
    ib.pinned_per_draw = true;
    ib.data = packet.inline_index_data;
    ib.content_hash = DirectHashBytes(ib.data.data(), ib.byte_size);
    // Anchor the index binding on the inline payload's guest address, matching
    // the observer's ApplyDrawEventScratchBuffersLocked (event.index_buffer =
    // scratch index address). UP draws issue no SetIndices, so any nonzero
    // index_buffer here is a stale binding from an unrelated draw — the
    // packet's payload is the authority (same stale-stream class as the
    // vertex anchor above).
    command.resources.index_buffer = packet.up_index_data;
  }

  if (packet.viewport[2] != 0u || packet.viewport[3] != 0u) {
    command.viewport.valid = true;
    command.viewport.x = static_cast<float>(packet.viewport[0]);
    command.viewport.y = static_cast<float>(packet.viewport[1]);
    command.viewport.width = static_cast<float>(packet.viewport[2]);
    command.viewport.height = static_cast<float>(packet.viewport[3]);
    float min_depth = 0.0f;
    float max_depth = 1.0f;
    std::memcpy(&min_depth, &packet.viewport[4], sizeof(float));
    std::memcpy(&max_depth, &packet.viewport[5], sizeof(float));
    command.viewport.min_depth = min_depth;
    command.viewport.max_depth = max_depth;
    // Raw guest viewport (native space); the executor scales it to output. The
    // observer path pre-scales in ScaleGuestViewportToCurrentDrawSurface, which
    // the GTL packet does not carry.
    command.viewport_guest_space = true;
  }
  if (packet.scissor[2] != 0u || packet.scissor[3] != 0u) {
    command.scissor.valid = true;
    command.scissor.left = static_cast<int32_t>(packet.scissor[0]);
    command.scissor.top = static_cast<int32_t>(packet.scissor[1]);
    command.scissor.right = static_cast<int32_t>(packet.scissor[2]);
    command.scissor.bottom = static_cast<int32_t>(packet.scissor[3]);
  }

  return command;
}

// BASE -> OBJECT NORMALISATION (2026-07-25).
//
// STALE PREMISE CORRECTED 2026-08-03 — twice. This comment used to say the
// resolver "does exactly one FindDirectResourceLocked(textures[slot]), its
// fetch-base fallback having been deliberately deleted 2026-07-22". That
// fallback was REINSTATED 2026-08-01 and widened 2026-08-03: the resolver now
// tries the exact object, then the fetch base, then the base->objects identity
// map, and no longer requires the fetch constant to be snapshot-decodable.
// This normalisation is therefore no longer the only thing standing between a
// stated base and a bound texture — but it remains worth doing, because
// stating an OBJECT is still stronger than making the resolver infer one.
//
// Fetch-constant-bound draws can still leave a raw BASE here — the fill at
// hook_observer.cpp:~9497 writes `texture_base` verbatim when the guest resource
// registry knows the base but no object owns it. That base is real in the
// GUEST-RESOURCE registry yet has no PLUME tracked resource, so the resolver
// reports `tracked=0 image=0` and the draw binds nothing. Run 271: the menu
// binds slot 0 with `guest=0x12D33000` and gets a textureless bind — the "phase
// we cannot reach yet" the resolver's own comment predicted would trip `null=`.
//
// Fix the PRODUCER, not the resolver: "binding is stated, never inferred" is
// right, so state an object rather than teaching the resolver to infer one.
//
// Runs from record_command, i.e. for EVERY command whatever built it. An
// earlier revision sat in BuildDirectCommandFromGtlDrawPacketLocked and never
// fired (run 271: 0 normalisations).
//
// The reason recorded for that — "these draws are `source=7` (kSceneObject) and
// never pass through the GTL packet builder" — is ALSO STALE. kSceneObject is
// assigned by that very builder, in the default arm of its source_symbol switch
// (0x8221C3E8 DrawPrimitive / 0x8221DFC0 DrawIndexedPrimitive) and in the
// Begin-family arm, whenever packet.scene_submit_depth != 0. Reading
// `command.constants.fetch` rather than packet.fetch_dwords is still the right
// call — every command carries it, whatever built it — but do not re-derive the
// old claim from this comment.
void NormalizeDirectTextureSlotsToObjectsLocked(
    FableDirectRenderCommand& command) {
  for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
    const uint32_t stated = command.resources.textures[slot];
    // Only rewrite values that are NOT already an object. The object-address
    // window is the guest allocator's own layout, not a tuned constant.
    if (stated == 0 || IsLikelyFableObjectAddress(stated)) {
      continue;
    }
    const uint32_t base_index = slot * 6u;
    if (base_index + 6u > command.constants.fetch.size()) {
      continue;
    }
    std::array<uint32_t, 6> slot_fetch = {};
    std::copy_n(command.constants.fetch.begin() + base_index, 6u,
                slot_fetch.begin());
    uint32_t object = 0;
    // Prefer the EXACT 6-dword fetch key over the base: by_base is ~44%
    // ambiguous per the identity census because pooled headers re-point across
    // sizes/formats, while the full fetch constant separates those cases.
    if (const std::vector<uint32_t>* by_fetch =
            FableDirectFindTextureIdentityObjectsByFetch(slot_fetch)) {
      if (by_fetch->size() == 1u) {
        object = by_fetch->front();
      }
    }
    // Base fallback only when the exact key cannot answer, and only when it
    // answers UNAMBIGUOUSLY — a multi-object base is precisely the case that
    // would bind the WRONG texture, and a wrong texture is worse than a missing
    // one. Leave it for the existing `null=` alarm to report rather than guess.
    if (object == 0) {
      if (const std::vector<uint32_t>* by_base =
              FableDirectFindTextureIdentityObjectsByBase(stated)) {
        if (by_base->size() == 1u) {
          object = by_base->front();
        }
      }
    }
    if (object == 0 || object == stated) {
      continue;
    }
    command.resources.textures[slot] = object;
    ++g_direct_renderer.gtl_texture_base_normalised;
    static uint32_t logged_base_normalise = 0;
    if (logged_base_normalise < 24u) {
      REXLOG_INFO(
          "Refii GTL texture base->object #{} total={} slot={} src={} "
          "base=0x{:08X} -> object=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_base_normalise,
          static_cast<unsigned long long>(
              g_direct_renderer.gtl_texture_base_normalised),
          slot, static_cast<uint32_t>(command.draw.source), stated, object,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
  }
}

void ApplyGtlPacketIdentityAuthorityLocked(FableDirectRenderCommand& command) {
  if (command.type != FableDirectRenderCommandType::kDrawPrimitive &&
      command.type != FableDirectRenderCommandType::kDrawIndexedPrimitive &&
      command.type != FableDirectRenderCommandType::kDrawPrimitiveUP) {
    return;
  }
  if (command.draw.source == RendererDrawEventSource::kPixVideo ||
      command.draw.private_texture_rectangle) {
    return;
  }
  const refii::gtl::DrawPacket* packet = nullptr;
  const auto take = [&](const std::vector<refii::gtl::DrawPacket>& packets,
                        std::vector<bool>& consumed) {
    for (size_t index = 0; index < packets.size() && index < consumed.size();
         ++index) {
      if (consumed[index]) {
        continue;
      }
      // Shader identity alone is not a draw identity. Repeated title quads
      // share one shader pair while advancing texture slot 13, and repeated
      // meshes share shaders while advancing stream pointers. Taking the
      // first shader match paired a command with the preceding draw's GTL
      // packet, then applied its texture/geometry/constants as authority.
      // Every overlapping non-zero binding must agree; zero remains the
      // packet's documented "no claim" value.
      if (!GtlPacketIdentityAgreesWithCommandLocked(command,
                                                    packets[index])) {
        continue;
      }
      consumed[index] = true;
      packet = &packets[index];
      return;
    }
  };
  take(g_direct_renderer.gtl_shadow_packets,
       g_direct_renderer.gtl_authority_consumed);
  if (packet == nullptr) {
    take(g_direct_renderer.gtl_shadow_packets_prev,
         g_direct_renderer.gtl_authority_consumed_prev);
  }
  if (packet == nullptr) {
    return;
  }
  // Begin-family packets snapshot device state at Begin ENTER, but that
  // family's guest contract stages shaders/textures/constants AFTER Begin
  // and flushes at End — every packet axis is pre-draw stale for them.
  // They pair for coverage/order; they must carry NO authority (the black
  // screen began exactly when the nesting fix first made them match:
  // shadow 2/0 -> 1/1 at run 118, black reported immediately after).
  if (IsGtlPreDispatchBeginPacket(*packet)) {
    return;
  }
  // IDENTITY-FIELD AUTHORITY — DELETED 2026-07-26 (proven no-op).
  // An `assign()` lambda used to copy the matched packet's decl / index buffer
  // / depth target / render targets / textures (incl. fetch-base resolution) /
  // stream buffers+strides over the command's, counting a divergence whenever
  // the two disagreed. The counter incremented BEFORE the write, so a zero
  // divergence count is proof the write never changed anything. Run 288: 39
  // sampled "authority applied" lines, every one total_divergent_fields=0 —
  // across every class (decl/ib/ds/rt/tex/sbuf/soff/sstr). The observer command
  // and the GTL packet already agree on identity by construction, because
  // GtlPacketIdentityAgreesWithCommandLocked (the take() matcher below^) only
  // pairs packets whose overlapping non-zero bindings are equal. The assign
  // pass was therefore writing each field's existing value back onto itself.
  // The divergence census that consumed it went with it.
  // KEPT: the take() matcher above and the constants authority below. Those
  // are LIVE — the matcher selects the packet, and the constant banks are
  // captured at dispatch and genuinely differ from the observer's.

  // Constants authority for all matched draws (step-5 cutover, generalizing
  // the video-family first pass): packet banks are captured AT DISPATCH —
  // the at-draw truth — while observer-captured constants can be one frame
  // stale (the WITH_ALPHA g_FixedAlpha flash was this, made visible). The
  // flip gate proved agreement at 99.9% (run 092: const_cmp 4/3491) and
  // every historical divergence was the time-register class where the
  // packet value is the more correct one. The banks are complete snapshots,
  // so zeros are real values (a fade TO zero must apply) — copy whole
  // banks, each dword through the same sanitizer the observer delivery
  // path applies (title tonemap +inf -> NaN white-out otherwise).
  if (!packet->vs_float_constants.empty() ||
      !packet->ps_float_constants.empty()) {
    const auto apply_bank = [](const std::vector<uint32_t>& packet_bank,
                               std::array<uint32_t,
                                          kRendererShaderFloatConstantDwords>&
                                   command_bank,
                               bool pixel_bank) {
      const size_t dwords = std::min(packet_bank.size(), command_bank.size());
      for (size_t dword_index = 0; dword_index < dwords; ++dword_index) {
        command_bank[dword_index] = RendererSanitizeShaderFloatConstantDword(
            pixel_bank, static_cast<uint32_t>(dword_index),
            packet_bank[dword_index]);
      }
      return dwords;
    };
    const size_t vs_dwords =
        apply_bank(packet->vs_float_constants, command.constants.vertex_float,
                   false);
    const size_t ps_dwords =
        apply_bank(packet->ps_float_constants, command.constants.pixel_float,
                   true);
    ++g_direct_renderer.gtl_constants_authority_applied;
    static uint32_t logged_constant_authority = 0;
    if (logged_constant_authority < 8u) {
      REXLOG_INFO(
          "Refii GTL constants authority #{} serial={} vs=0x{:08X} "
          "ps=0x{:08X} dwords={}/{}",
          ++logged_constant_authority,
          static_cast<unsigned long long>(command.serial),
          command.resources.vertex_shader, command.resources.pixel_shader,
          static_cast<uint32_t>(vs_dwords), static_cast<uint32_t>(ps_dwords));
    }
  }

  // GTL render-state authority: DO NOT REINTRODUCE.
  // Refuted by the run-104 bisect. Unlike lazily-dispatched constants (where
  // packets genuinely win), the observer reads render states at DRAW-FLUSH —
  // the authoritative D3D moment — so there is no staleness for a packet to
  // correct. Applying packet state actively harms: run 104 slot 19 (CullMode,
  // raw Xenos PA_SU_SC_MODE_CNTL bits per ConvertFableCullMode) diverged
  // packet=6 (cull-back + CW winding) vs command=0 (cull none); applying the
  // packet enabled back-face culling against geometry whose winding did not
  // match and culled every triangle to a BLACK SCREEN. Root reason it can
  // never be made safe as-is: the shader+texture pairing used to match a
  // packet to a command carries no per-draw state ordering, so a packet can
  // legitimately carry a neighbouring draw's state. All risk, no gain — the
  // observer stays authoritative for render states.
  // (Dead implementation removed 2026-07-26; it was `if (false && ...)`.)

  // Geometry authority (submission close, pinned family): packets from the
  // DrawIndexedUserPrimitives root copy the CALLER's source geometry at
  // ENTER — race-free by construction, unlike the ring destination the
  // observer captures after the fact. The packet bytes are served under the
  // command's own binding identity (the stream/index pointers the bind
  // matches); the packet's raw source pointers are guest stack addresses
  // the bind path rightly rejects. This removes the observer capture/
  // attach/rescue dependency for the whole 8220A3F0 family (bink compose +
  // the UI quad callers).
  const bool packet_geometry_nonzero =
      !packet->inline_vertex_data.empty() &&
      std::any_of(packet->inline_vertex_data.begin(),
                  packet->inline_vertex_data.end(),
                  [](uint8_t byte) { return byte != 0; });
  // FILL-GAPS-ONLY redesign 2026-07-14 (bisect rung H convicted the old
  // override): geometry authority now supplies packet bytes ONLY when the
  // observer produced NO ready payload for this draw. The bisect proved
  // that replacing a working observer capture — even a same-or-larger one,
  // because shader+texture pairing carries no size/identity discipline for
  // geometry — corrupted the gui compose binds (title triangle payload
  // pointed at a menu-quad's stream) and blacked the screen. Filling only
  // absent payloads keeps the observer authoritative where it works and
  // covers the ring-race draws it misses, which was the point.
  const bool observer_has_geometry =
      command.inline_vertex_buffers[0].ready();
  if (packet_geometry_nonzero && !observer_has_geometry &&
      packet->up_vertex_stride != 0 &&
      command.draw.draw.indexed == packet->indexed) {
    FableDirectInlineBuffer& vertex_payload = command.inline_vertex_buffers[0];
    vertex_payload.address = command.resources.streams[0].buffer != 0
                                 ? command.resources.streams[0].buffer
                                 : packet->up_vertex_data;
    vertex_payload.byte_size =
        static_cast<uint32_t>(packet->inline_vertex_data.size());
    vertex_payload.stride_or_format = packet->up_vertex_stride;
    vertex_payload.pinned_per_draw = true;
    vertex_payload.data = packet->inline_vertex_data;
    // Payload write site: stamp the content hash the bind will key on.
    vertex_payload.content_hash = DirectHashBytes(
        vertex_payload.data.data(), vertex_payload.byte_size);
    if (!packet->inline_index_data.empty()) {
      FableDirectInlineBuffer& index_payload = command.inline_index_buffer;
      index_payload.address = command.resources.index_buffer != 0
                                  ? command.resources.index_buffer
                                  : packet->up_index_data;
      index_payload.byte_size =
          static_cast<uint32_t>(packet->inline_index_data.size());
      index_payload.stride_or_format = 2u;
      index_payload.pinned_per_draw = true;
      index_payload.data = packet->inline_index_data;
      index_payload.content_hash = DirectHashBytes(
          index_payload.data.data(), index_payload.byte_size);
    }
    ++g_direct_renderer.gtl_geometry_authority_applied;
    static uint32_t logged_geometry_authority = 0;
    if (logged_geometry_authority < 8u ||
        (g_direct_renderer.gtl_geometry_authority_applied % 1024u) == 0u) {
      REXLOG_INFO(
          "Refii GTL geometry authority #{} serial={} vb_bytes={} ib_bytes={} "
          "addr=0x{:08X}",
          ++logged_geometry_authority,
          static_cast<unsigned long long>(command.serial),
          static_cast<uint32_t>(packet->inline_vertex_data.size()),
          static_cast<uint32_t>(packet->inline_index_data.size()),
          vertex_payload.address);
    }
  }

  ++g_direct_renderer.gtl_authority_applied;
  static uint64_t logged_gtl_authority_applies = 0;
  ++logged_gtl_authority_applies;
  if (logged_gtl_authority_applies <= 4u ||
      (logged_gtl_authority_applies % 1024u) == 0u) {
    REXLOG_INFO("Refii GTL authority applied #{} frame={} (constants only)",
                static_cast<unsigned long long>(logged_gtl_authority_applies),
                g_direct_renderer.current_frame_index);
  }
}

// Executor resource lifecycle (§3.4): every address a frame's DrawPackets
// bind becomes sync interest up front — the game bound it, so it must
// materialize. This is the packet-owned replacement for the materialization
// pressure the resolver families exert as a side effect of readiness
// classification; it needs no identity blessing and no title gates.
void AddGtlPacketResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter) {
  if (!RendererGtlExecutorEnabled()) {
    return;
  }
  // Pressure is for MATERIALIZATION only: an address whose record already
  // has a ready host resource needs nothing from this path — its content
  // refresh flows through its own channel (upload generations for CPU
  // writes, the dedicated Bink staging channel for video planes). Forcing
  // ready addresses through the per-frame harvest re-opens the
  // capture-routing race the bink channel exists to close.
  //
  // Address memo (2026-08-01): the packet backlog repeats the same few
  // hundred addresses massively (run 489: ~1900 packets x ~25 address slots
  // = ~47k FindDirectResourceLocked calls, ~37ms). Nothing in this walk
  // mutates tracked state, so the outcome for a given (address, content)
  // pair is identical across packets — resolve each pair once. Keyed on the
  // pair, not the address alone, because the same address can arrive as
  // image content (sampler slot / fetch base) and as non-image (RT/stream),
  // and the untracked branch inserts into different filters for the two.
  std::unordered_set<uint64_t> walked;
  const auto add_missing = [&](uint32_t address, bool image_content) {
    if (address == 0) {
      return;
    }
    if (!walked
             .insert(static_cast<uint64_t>(address) |
                     (image_content ? (1ull << 32) : 0ull))
             .second) {
      return;
    }
    PlumeTrackedResource* tracked = FindDirectResourceLocked(address);
    if (tracked == nullptr) {
      // Never registered: materialize the record and deliver its initial
      // content in one sync.
      resource_filter.insert(address);
      if (image_content) {
        texture_upload_filter.insert(address);
      }
      return;
    }
    // Tracked but unready: request materialization only. Content delivery
    // belongs to the record's own channel (upload generations, the Bink
    // staging path); pulling uploads at draw time races those channels —
    // mid-decode video planes are the proven casualty.
    if (IsImageResource(tracked->kind)) {
      if (!PlumeGuestImageReady(*tracked)) {
        resource_filter.insert(address);
      }
    } else if (IsBufferResource(tracked->kind)) {
      if (!PlumeGuestBufferReady(*tracked)) {
        resource_filter.insert(address);
      }
    }
  };
  for (const refii::gtl::DrawPacket& packet :
       g_direct_renderer.gtl_shadow_packets) {
    // Texture identity bounded to the consumed slot space (0-15); fetch
    // bases carry the lazy-dispatch bindings the wrapper roots never see.
    for (uint32_t slot = 0; slot < 16u && slot < packet.textures.size();
         ++slot) {
      add_missing(packet.textures[slot], true);
    }
    for (const uint32_t fetch_base : packet.fetch_bases) {
      add_missing(fetch_base, true);
    }
    for (const uint32_t render_target : packet.render_targets) {
      add_missing(render_target, false);
    }
    add_missing(packet.depth_target, false);
    add_missing(packet.vertex_shader, false);
    add_missing(packet.pixel_shader, false);
    add_missing(packet.vertex_declaration, false);
    // Streams in the pipeline's pointer domain (resolved at SetStreamSource);
    // the index buffer stays command-sourced until its object→pointer
    // resolution lands.
    for (const uint32_t stream_pointer : packet.vertex_buffer_pointers) {
      add_missing(stream_pointer, false);
    }
  }
  // Resolve DESTINATIONS (§3b). Until now this loop only walked the DrawPacket
  // stream, so a texture that is only ever a resolve destination — never bound
  // to a sampler slot, a fetch base, or an RT — was never materialized by
  // anyone. Measured (run 449): ~26% of resolve destinations had no host
  // resource under EITHER lookup key, which is not a lookup bug (there was
  // nothing to find) but this missing interest. The destination is stated by
  // the packet (r6), so claiming it here needs no inference.
  //
  // image_content=false ON PURPOSE. A resolve destination's content comes from
  // the resolve itself (the doc's "Resolve root = explicit host RT->texture
  // copy; no competing CPU source exists"). Passing true would additionally
  // pull a CPU texture upload of that console memory, which is precisely the
  // competing-writer/upload-stomp class §4 exists to delete. Materialize the
  // record; let the resolve write the pixels.
  //
  // Resolve SOURCES are claimed too (2026-08-01). The old rule ("draw-stream
  // RT/DT interest already covers them — measured no_host_source=0 across runs
  // 448/449") no longer held: run 497 measured no_host_source=30 of 51
  // packets on EVERY gameplay frame.
  //
  // *** THIS CLAIM IS THE FIX, AND IT WORKED — run 497 IS PRE-FIX. *** Read
  // the paragraph below as the ORIGINAL DIAGNOSIS, not as current behaviour:
  // run 498 (2026-08-02) holds `no_host_source=0` on every steady-state
  // gameplay census, LIVE paths went 94/183 -> 210 draw / 69 refused, and 32+
  // on-demand surface mints fired. Only scene-transition BURST frames still
  // spike (frame 4323: packets=4420 no_src=2958) — transient per-frame headers;
  // watch, do not chase, unless it returns in steady gameplay.
  //
  // Also do not read `HOST_COPY(dry): buildable=0` as "resolves are dropped".
  // It is BY DESIGN: a depth source against a colour destination always trips
  // format_mismatch (:7257), which ROUTES to the blit draw path rather than
  // refusing. The deleted host-copy recording tail (:7094) is an unused
  // alternative, not the resolve implementation.
  //
  // IDA named the losing family — per-light
  // shadow depth maps (EndTiling via 0x822A0B08), the scene depth resolve
  // feeding soft particles (0x82A7FDE0 → g_ResolvedDepthTexture), and the
  // bloom/blur/downscale post chain (0x82242108) — none of whose passes have
  // draw packets carrying their RT/DT into the loop above. image_content=false
  // for the same reason as destinations: an EDRAM surface's content comes from
  // rendering, never a CPU upload. Note the interest is a sync REQUEST, not a
  // mint: it only materializes once the observer holds a record for the
  // surface (TrackBoundSurfaceLocked at bind), so it is a no-op for headers
  // the bind-time gate still rejects.
  //
  // KEY-MISMATCH NOTE, DELIBERATELY NOT ACTED ON (2026-08-03). The early-out
  // above uses the FUZZY FindDirectResourceLocked while the resolve consumer
  // looks both endpoints up with the EXACT FindDirectResourceByExactObjectLocked
  // (:6972 destination, :7147 source, :55419 live source), so in principle a
  // base-alias hit can satisfy the claim while the consumer starves on the
  // exact key. Claiming under the exact key was tried and REVERTED, for two
  // reasons:
  //   1. The starvation it would address is already fixed and verified — run
  //      498 holds `no_host_source=0` on every steady-state gameplay census
  //      (run 497's 30-of-51, which the comment above still describes, is
  //      pre-fix). Only scene-transition BURST frames still spike.
  //   2. FindDirectResourceByExactObjectLocked has no negative memo and no fast
  //      cache: every miss walks the whole resource map (:14486-14492). On the
  //      burst frames this would target (frame 4323: packets=4420) that is two
  //      full-map scans per packet, i.e. a large cost on precisely the frames
  //      already under pressure. The fuzzy lookup memoizes its misses.
  // If a steady-state census ever shows `no_host_source` non-zero again, give
  // the exact lookup a negative memo FIRST, then reconsider.
  if (RendererGtlPrivateRectComposeEnabled()) {
    for (const refii::gtl::ResolvePacket& packet :
         g_direct_renderer.gtl_resolve_shadow_packets) {
      add_missing(packet.dest_texture, false);
      add_missing(packet.source_surface, false);
    }
  }
}

// Missing vertex-input interest for a replay command. Packet interest covers
// wrapper draws' stream pointers; the pix-video and scratch-ring families are
// not in the packet stream, so their unready vertex/index buffers must be
// requested here or their draws fail-fast with missing_vertex_input.
void AddDirectCommandMissingVertexInputInterestLocked(
    const FableDirectRenderCommand& command,
    std::unordered_set<uint32_t>& resource_filter) {
  if (!IsDirectDrawCommand(command)) {
    return;
  }
  const auto add_if_unready = [&](uint32_t address) {
    if (address == 0) {
      return;
    }
    PlumeTrackedResource* tracked = FindDirectResourceLocked(address);
    if (tracked == nullptr || (IsBufferResource(tracked->kind) &&
                               !PlumeGuestBufferReady(*tracked))) {
      resource_filter.insert(address);
    }
  };
  for (size_t slot = 0; slot < command.resources.streams.size(); ++slot) {
    const RendererStreamBindingSnapshot& stream =
        command.resources.streams[slot];
    if (!DirectInlineBufferCoversAddress(command.inline_vertex_buffers[slot],
                                         stream.buffer)) {
      add_if_unready(stream.buffer);
    }
  }
  if (!DirectInlineBufferCoversAddress(command.inline_index_buffer,
                                       command.resources.index_buffer)) {
    add_if_unready(command.resources.index_buffer);
  }
}

// ZERO-CALLER FUNCTION ReorderDirectDrawCommandsToPacketOrderLocked —
// DELETED 2026-07-26 (~221 lines). It re-sorted matched draws inside each
// contiguous draw run into GTL packet-id order (the run-098 menu-collage
// ordering cutover) and, as a side effect, maintained the
// gtl_submission_eligible/paired coverage metric and the "submission
// unpaired" diagnostic. It is fully superseded: the authoritative packet
// replay in the submission path now emits draws in packet_id order BY
// CONSTRUCTION, so there is nothing left to reorder. Verified zero-caller
// across the WHOLE repo (src/, tools/, both rexglue shadows, headers
// included) — exactly ONE occurrence existed, the definition itself, with no
// forward declaration. Internal linkage (anonymous namespace).

// ---------------------------------------------------------------------------
// T2/T3 (2026-08-02): kGtlTessDraw — hardware-tessellator patch execution.
// ---------------------------------------------------------------------------

// `<stem>.tess` artifact path from the base entry's per-API binary path
// (shader_library.cpp names artifacts `<name>.vulkan.bin` / `<name>.d3d12.bin`
// / `<name>.metallib`; T1 stages the tess variant as `<name>.tess.<api-suffix>`
// beside it). Empty when the base path has no recognised suffix.
std::string DeriveGtlTessVariantArtifactPath(const std::string& base_path) {
  static constexpr const char* kSuffixes[] = {".vulkan.bin", ".d3d12.bin",
                                              ".metallib"};
  for (const char* suffix : kSuffixes) {
    const size_t suffix_len = std::strlen(suffix);
    if (base_path.size() > suffix_len &&
        base_path.compare(base_path.size() - suffix_len, suffix_len, suffix) ==
            0) {
      std::string path = base_path.substr(0, base_path.size() - suffix_len);
      path += ".tess";
      path += suffix;
      return path;
    }
  }
  return {};
}

// Tess-variant VS for the packet's guest vertex shader, cached by bytecode
// hash. A miss is LOUD by contract (risk R4): one-shot error log per hash +
// the vs_tess_miss census counter — never a silent drop, because a missing
// tess artifact means the T1 regeneration is incomplete for this shader.
plume::RenderShader* EnsureGtlTessVertexShaderVariantLocked(
    plume::RenderDevice* device, uint32_t vertex_shader_object) {
  const uint64_t vs_hash = DirectVertexShaderHashLocked(vertex_shader_object);
  const auto loud_miss = [&](const char* reason, const std::string& path) {
    ++g_gtl_tess_draw_stats.vs_tess_miss;
    if (g_direct_renderer.tess_vertex_shader_variant_misses
            .insert(vs_hash != 0 ? vs_hash : vertex_shader_object)
            .second) {
      REXLOG_ERROR(
          "Refii GTL TESS-DRAW tess-variant VS MISS ({}) vs=0x{:08X} "
          "hash=0x{:016X} path={} — terrain/water patch draws for this "
          "shader CANNOT execute until the tess artifact is regenerated",
          reason, vertex_shader_object, vs_hash, path.empty() ? "-" : path);
    }
    return static_cast<plume::RenderShader*>(nullptr);
  };
  if (vs_hash == 0) {
    return loud_miss("no_bytecode_hash", {});
  }
  if (const auto it =
          g_direct_renderer.tess_vertex_shader_variants.find(vs_hash);
      it != g_direct_renderer.tess_vertex_shader_variants.end()) {
    return it->second.get();
  }
  if (g_direct_renderer.tess_vertex_shader_variant_misses.count(vs_hash) !=
      0) {
    ++g_gtl_tess_draw_stats.vs_tess_miss;  // still counted, already logged
    return nullptr;
  }
  RendererShaderLibraryEntry entry;
  if (!FindRendererShaderLibraryEntryByGuestHash(vs_hash, entry) ||
      entry.binary_path.empty()) {
    return loud_miss("no_base_library_entry", {});
  }
  const std::string tess_path =
      DeriveGtlTessVariantArtifactPath(entry.binary_path);
  if (tess_path.empty()) {
    return loud_miss("unrecognised_artifact_suffix", entry.binary_path);
  }
  std::vector<uint8_t> bytes;
  if (!ReadBinaryFile(tess_path, bytes) || bytes.empty()) {
    return loud_miss("tess_artifact_unreadable", tess_path);
  }
  std::unique_ptr<plume::RenderShader> shader =
      device != nullptr
          ? device->createShader(bytes.data(), bytes.size(), "main",
                                 g_direct_renderer.shader_format)
          : nullptr;
  if (!shader) {
    return loud_miss("shader_creation_failed", tess_path);
  }
  shader->setName(entry.name + ".tess");
  plume::RenderShader* shader_ptr = shader.get();
  g_direct_renderer.tess_vertex_shader_variants.emplace(vs_hash,
                                                        std::move(shader));
  static uint32_t logged_tess_vs_links = 0;
  if (logged_tess_vs_links < 16u) {
    REXLOG_INFO(
        "Refii GTL TESS-DRAW tess-variant VS linked #{} vs=0x{:08X} "
        "hash=0x{:016X} entry={} bytes={}",
        ++logged_tess_vs_links, vertex_shader_object, vs_hash, entry.name,
        bytes.size());
  }
  return shader_ptr;
}

// Pipeline for a kGtlTessDraw command: tess-variant VS + the command's base
// pixel shader, NO input slots/elements (the guest binds no vertex streams —
// geometry generates in-shader from SV_VertexID/SV_InstanceID + vertex
// textures), triangle-list topology over the synthetic domain grid.
// force_no_cull: the Xenos tessellator's output winding over a quad patch is
// hardware-defined and our synthetic grid cannot reproduce it, so honouring
// the guest cull word is a coin flip per shape that can blank the terrain;
// patch surfaces are closed heightfields for which culling is a perf
// optimisation, not correctness.
plume::RenderPipeline* EnsureGtlTessPipelineLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer) {
  if (!device || !g_plume_resources.pipeline_layout) {
    return nullptr;
  }
  plume::RenderShader* tess_vertex_shader =
      EnsureGtlTessVertexShaderVariantLocked(device,
                                             command.resources.vertex_shader);
  if (!tess_vertex_shader) {
    return nullptr;  // loud_miss already counted + logged (R4)
  }
  PlumeTrackedResource* pixel_shader =
      FindDirectReadyShaderResourceByIdentityLocked(
          command.resources.pixel_shader,
          FableGuestResourceKind::kPixelShader);
  if (!pixel_shader || !PlumeGuestShaderReady(*pixel_shader)) {
    LogFailedDirectDrawLocked("tess_pixel_shader_not_ready", command);
    return nullptr;
  }
  const std::vector<plume::RenderInputSlot> input_slots;      // none: no streams
  const std::vector<plume::RenderInputElement> input_elements;
  DirectPipelineKey key = BuildDirectPipelineKey(
      command, input_slots, input_elements, framebuffer,
      /*force_no_cull=*/true);
  key.tess_variant = true;
  const auto pipeline_it = g_direct_renderer.pipelines.find(key);
  if (pipeline_it != g_direct_renderer.pipelines.end()) {
    ++g_direct_renderer.stats.pipeline_cache_hits;
    return pipeline_it->second.get();
  }

  plume::RenderGraphicsPipelineDesc desc;
  desc.pipelineLayout = g_plume_resources.pipeline_layout.get();
  desc.vertexShader = tess_vertex_shader;
  desc.pixelShader = pixel_shader->guest_shader.shader.get();
  desc.primitiveTopology = plume::RenderPrimitiveTopology::TRIANGLE_LIST;
  for (uint32_t slot = 0; slot < key.color_count; ++slot) {
    desc.renderTargetFormat[slot] =
        key.color_formats[slot] != plume::RenderFormat::UNKNOWN
            ? key.color_formats[slot]
            : plume::RenderFormat::B8G8R8A8_UNORM;
    desc.renderTargetBlend[slot] = DirectBlendDescFromEffectiveState(
        key.blend_enabled[slot], key.blend_controls[slot],
        key.color_write_masks[slot]);
  }
  desc.renderTargetCount = key.color_count;
  desc.inputSlots = nullptr;
  desc.inputSlotsCount = 0;
  desc.inputElements = nullptr;
  desc.inputElementsCount = 0;
  plume::RenderSpecConstant spec_constant;
  if (key.spec_constants != 0) {
    spec_constant = plume::RenderSpecConstant(0, key.spec_constants);
    desc.specConstants = &spec_constant;
    desc.specConstantsCount = 1;
  }
  desc.cullMode = plume::RenderCullMode::NONE;
  desc.frontFace = ConvertFableFrontFace(key.cull_control);
  if (key.depth_format != plume::RenderFormat::UNKNOWN) {
    const uint32_t depth_control = key.depth_control;
    desc.depthTargetFormat = key.depth_format;
    desc.depthEnabled = (depth_control & 0x2u) != 0;
    desc.depthWriteEnabled = (depth_control & 0x4u) != 0;
    desc.depthFunction = ConvertFableCompare(
        (depth_control >> 4u) & 0x7u,
        plume::RenderComparisonFunction::LESS_EQUAL);
    desc.stencilEnabled = (depth_control & 0x1u) != 0;
    desc.stencilReference = key.stencil_ref_mask & 0xFFu;
    desc.stencilReadMask = (key.stencil_ref_mask >> 8u) & 0xFFu;
    desc.stencilWriteMask = (key.stencil_ref_mask >> 16u) & 0xFFu;
    desc.stencilFrontFace =
        FableStencilFaceFromDepthControl(depth_control, false);
    desc.stencilBackFace =
        FableStencilFaceFromDepthControl(depth_control, true);
  }
  std::unique_ptr<plume::RenderPipeline> pipeline =
      device->createGraphicsPipeline(desc);
  if (!pipeline) {
    LogFailedDirectDrawLocked("tess_pipeline_creation_failed", command);
    return nullptr;
  }
  char name[160] = {};
  std::snprintf(name, sizeof(name),
                "Refii tess pipeline VS_%016llX PS_%016llX RT0_%u DT_%u "
                "SPEC_%08X vsobj_%08X psobj_%08X",
                static_cast<unsigned long long>(key.vertex_shader_hash),
                static_cast<unsigned long long>(key.pixel_shader_hash),
                static_cast<uint32_t>(key.color_formats[0]),
                static_cast<uint32_t>(key.depth_format), key.spec_constants,
                command.resources.vertex_shader,
                command.resources.pixel_shader);
  pipeline->setName(name);
  plume::RenderPipeline* pipeline_ptr = pipeline.get();
  g_direct_renderer.pipelines.emplace(std::move(key), std::move(pipeline));
  ++g_direct_renderer.stats.pipeline_cache_misses;
  return pipeline_ptr;
}

// Cached (N+1)x(N+1) domain-grid index buffer: 6*N*N R32 indices, two
// triangles per cell. Vertex v(row,col) = row*(N+1)+col; the tess-variant VS
// derives domain UV as (vid % (N+1))/N, (vid / (N+1))/N (T1 r0 seed), so this
// grid IS the domain sampling. Winding is uniform; the tess pipeline disables
// culling (see EnsureGtlTessPipelineLocked), so it is not load-bearing.
bool BindGtlTessGridIndexBufferLocked(plume::RenderDevice* device,
                                      plume::RenderCommandList* command_list,
                                      uint32_t n) {
  if (!device || !command_list || n == 0u) {
    return false;
  }
  auto it = g_direct_renderer.tess_grid_index_buffers.find(n);
  if (it == g_direct_renderer.tess_grid_index_buffers.end()) {
    const uint32_t verts_per_row = n + 1u;
    std::vector<uint32_t> indices;
    indices.reserve(static_cast<size_t>(n) * n * 6u);
    for (uint32_t row = 0; row < n; ++row) {
      for (uint32_t col = 0; col < n; ++col) {
        const uint32_t v00 = row * verts_per_row + col;
        const uint32_t v01 = v00 + 1u;
        const uint32_t v10 = v00 + verts_per_row;
        const uint32_t v11 = v10 + 1u;
        indices.push_back(v00);
        indices.push_back(v01);
        indices.push_back(v11);
        indices.push_back(v00);
        indices.push_back(v11);
        indices.push_back(v10);
      }
    }
    char name[96] = {};
    std::snprintf(name, sizeof(name),
                  "Refii Fable tess domain grid N=%u indices=%zu", n,
                  indices.size());
    std::unique_ptr<plume::RenderBuffer> buffer = CreateUploadBufferWithData(
        device, indices.data(),
        static_cast<uint32_t>(indices.size() * sizeof(indices[0])), name,
        plume::RenderBufferFlag::INDEX);
    if (!buffer) {
      return false;
    }
    it = g_direct_renderer.tess_grid_index_buffers
             .emplace(n, std::move(buffer))
             .first;
  }
  plume::RenderIndexBufferView view(
      it->second->at(0),
      static_cast<uint64_t>(n) * n * 6u * sizeof(uint32_t),
      plume::RenderFormat::R32_UINT);
  command_list->setIndexBuffer(&view);
  return true;
}

// T2: TessellatedPatchDrawPacket -> kGtlTessDraw command (the packet is the
// command's source by value, kGtlResolve pattern — commands can be held/
// replayed across frames, so per-frame vector indices would dangle).
FableDirectRenderCommand BuildDirectCommandFromGtlTessPacketLocked(
    const refii::gtl::TessellatedPatchDrawPacket& packet) {
  FableDirectRenderCommand command;
  command.serial = packet.packet_id;
  command.draw_serial = packet.packet_id;
  command.type = FableDirectRenderCommandType::kGtlTessDraw;
  command.gtl_packet_sourced = true;
  command.gtl_tess_valid = true;
  // Terrain/water patch passes are scene submission by construction (they
  // render into the scene EDRAM targets from inside the scene pass table).
  command.draw.source = RendererDrawEventSource::kSceneObject;

  command.resources.vertex_shader = packet.vertex_shader;
  command.resources.pixel_shader = packet.pixel_shader;
  command.resources.render_targets = packet.render_targets;
  command.resources.depth_stencil = packet.depth_target;
  // Byte-for-byte the conversion BuildDirectCommandFromGtlDrawPacketLocked
  // applies to DrawPacket::viewport. Without it the tess command carried no
  // viewport, no scissor and no private-rect corners, so the extent test at the
  // tail of DirectCommandCanMaterializePrivateRectTargetLocked could not pass
  // and a tessellated draw could never materialize its render target.
  if (packet.viewport[2] != 0u || packet.viewport[3] != 0u) {
    command.viewport.valid = true;
    command.viewport.x = static_cast<float>(packet.viewport[0]);
    command.viewport.y = static_cast<float>(packet.viewport[1]);
    command.viewport.width = static_cast<float>(packet.viewport[2]);
    command.viewport.height = static_cast<float>(packet.viewport[3]);
    float min_depth = 0.0f;
    float max_depth = 1.0f;
    std::memcpy(&min_depth, &packet.viewport[4], sizeof(float));
    std::memcpy(&max_depth, &packet.viewport[5], sizeof(float));
    command.viewport.min_depth = min_depth;
    command.viewport.max_depth = max_depth;
    // The draw builder sets this and the tess builder did not, so every
    // TESSELLATED draw was skipped by ScaleFableTitleDrawStateToFramebuffer
    // (which early-returns on !viewport_guest_space) and kept a guest-space
    // viewport on a host-sized attachment. Harmless while the scale factor is
    // 1; it puts all terrain and water in a corner the moment it is not.
    // Same source, same space, same flag as the draw path.
    command.viewport_guest_space = true;
  }
  for (uint32_t slot = 0; slot < command.resources.textures.size() &&
                          slot < packet.textures.size();
       ++slot) {
    command.resources.textures[slot] = packet.textures[slot];
  }
  // Fetch-base override, the proven Bink/Begin-family precedent: these
  // emitters bind textures through the device fetch-constant block, NOT
  // SetTexture, so the tracked slots can carry a stale previous claim.
  // The captured fetch dwords are the hardware truth; resolve each stated
  // base to its object through the correctly-keyed base->objects identity map
  // and OVERRIDE the slot. Vertex textures s16-s19 ride the same 26-slot
  // fetch block, so this is also what makes the load-bearing vertex-stage
  // sources bindable (risk R3).
  if (packet.fetch_dwords_valid) {
    for (uint32_t slot = 0; slot < refii::gtl::kMaxTextureSlots; ++slot) {
      const uint32_t base = packet.fetch_dwords[slot * 6u + 1u] & 0xFFFFF000u;
      if (base == 0u) {
        continue;
      }
      const std::vector<uint32_t>* objects =
          FableDirectFindTextureIdentityObjectsByBase(base);
      if (objects != nullptr && !objects->empty() &&
          slot < command.resources.textures.size()) {
        command.resources.textures[slot] = objects->front();
      }
    }
    const size_t fetch_count = std::min<size_t>(
        command.constants.fetch.size(), packet.fetch_dwords.size());
    std::copy_n(packet.fetch_dwords.begin(), fetch_count,
                command.constants.fetch.begin());
  }

  if (!packet.vs_float_constants.empty()) {
    const size_t count = std::min<size_t>(command.constants.vertex_float.size(),
                                          packet.vs_float_constants.size());
    std::copy_n(packet.vs_float_constants.begin(), count,
                command.constants.vertex_float.begin());
  }
  if (!packet.ps_float_constants.empty()) {
    const size_t count = std::min<size_t>(command.constants.pixel_float.size(),
                                          packet.ps_float_constants.size());
    std::copy_n(packet.ps_float_constants.begin(), count,
                command.constants.pixel_float.begin());
  }
  FableDirectCopyShaderFlowConstants(
      command.constants.vertex_bool, command.constants.pixel_bool,
      command.constants.vertex_int, command.constants.pixel_int,
      command.constants.vertex_int_valid_mask,
      command.constants.pixel_int_valid_mask);

  // Host topology: the synthetic domain grid is an ordinary indexed triangle
  // list; the guest's prim 0x12 (quad patch) never reaches a host pipeline.
  command.draw.draw.indexed = true;
  command.draw.draw.primitive_type = 4u;  // D3DPT_TRIANGLELIST

  // Pipeline state from the captured register shadows. Register indices are
  // the public Xenos/Xenia map (register_table.inc), anchored by two
  // IDA-proven fixpoints in this codebase: dev+0x2940 = block 0x2200 index 3
  // = RB_HIZCONTROL (0x2203), and dev+0x2948 = index 5 = PA_SU_SC_MODE_CNTL
  // (0x2205, the cull word SetRenderState_CullMode merges into).
  //   0x2000 block (rb_surface_regs): [0] RB_SURFACE_INFO [1] RB_COLOR_INFO
  //     [2] RB_DEPTH_INFO [3..5] RB_COLOR1..3_INFO
  //   0x2100 block (pa_vgt_regs): [2] VGT_INDX_OFFSET [4] RB_COLOR_MASK
  //     [12] RB_STENCILREFMASK_BF [13] RB_STENCILREFMASK [14] RB_ALPHA_REF
  //   0x2200 block (sq_regs): [0] RB_DEPTHCONTROL [1] RB_BLENDCONTROL0
  //     [2] RB_COLORCONTROL [5] PA_SU_SC_MODE_CNTL [8] RB_MODECONTROL
  //     [9..11] RB_BLENDCONTROL1..3
  if (packet.device_regs_valid) {
    FableDirectPm4State& pm4 = command.pipeline.pm4;
    pm4.valid = true;
    pm4.rb_surface_info = packet.rb_surface_regs[0];
    pm4.rb_color_info = {packet.rb_surface_regs[1], packet.rb_surface_regs[3],
                         packet.rb_surface_regs[4], packet.rb_surface_regs[5]};
    pm4.rb_depth_info = packet.rb_surface_regs[2];
    pm4.rb_depthcontrol = packet.sq_regs[0];
    pm4.rb_blendcontrol = {packet.sq_regs[1], packet.sq_regs[9],
                           packet.sq_regs[10], packet.sq_regs[11]};
    pm4.rb_colorcontrol = packet.sq_regs[2];
    pm4.rb_modecontrol = packet.sq_regs[8];
    pm4.pa_su_sc_mode_cntl = packet.sq_regs[5];
    pm4.rb_color_mask = packet.pa_vgt_regs[4];
    pm4.rb_stencilrefmask_bf = packet.pa_vgt_regs[12];
    pm4.rb_stencilrefmask = packet.pa_vgt_regs[13];
    pm4.rb_alpha_ref = packet.pa_vgt_regs[14];
    command.pipeline.render_states[kFableRenderStateCullMode] =
        pm4.pa_su_sc_mode_cntl & 0x7u;
  }

  FableDirectGtlTessDrawCommand& tess = command.gtl_tess;
  tess.source_symbol = packet.source_symbol;
  tess.caller_lr = packet.caller_lr;
  tess.indexed = packet.indexed;
  tess.arg_index_offset = packet.arg_index_offset;
  tess.arg_count = packet.arg_count;
  tess.draw_initiator = packet.draw_initiator;
  tess.draw_packet_count = packet.draw_packet_count;
  tess.vgt_tess_regs = packet.vgt_tess_regs;
  tess.vgt_regs_valid = packet.device_regs_valid;
  tess.vgt_indx_offset = packet.pa_vgt_regs[2];
  tess.predication_mask = packet.predication_mask;
  tess.predication_enabled = packet.predication_enabled;
  tess.predication_bin_select = packet.predication_bin_select;
  tess.tiled_variant = packet.tiled_variant;
  return command;
}

// T3 executor. Returns false only on a genuine failure (framebuffer/pipeline/
// constants); predication skips and zero-patch decodes return true (censused,
// intentionally not drawn).
bool RecordGtlTessDrawLocked(plume::RenderDevice* device,
                             plume::RenderCommandList* command_list,
                             plume::RenderFramebuffer* output_framebuffer,
                             plume::RenderFormat output_depth_format,
                             FableDirectRenderCommand& command) {
  if (!device || !command_list || !output_framebuffer ||
      !command.gtl_tess_valid) {
    return false;
  }
  GtlTessDrawStats& stats = g_gtl_tess_draw_stats;
  // T4-lite predication, ClearPacket policy: a nonzero raw app mask (stored
  // verbatim by both predication writers, 0 on the disable call) marks this
  // as the PREDICATED submission of the terrain's double submit
  // (sub_822059E8 runs a masked pass, then a FULL sub_82A8F1D8(-1,...) pass).
  // Refii renders full-target, non-tiled, so the EDRAM-tile predication has
  // no work to do here; the full pass carries the complete terrain.
  if (command.gtl_tess.predication_mask != 0u ||
      command.gtl_tess.predication_enabled) {
    ++stats.pred_skips;
    return true;
  }
  uint32_t tess_n = 0;
  uint32_t patch_count = 0;
  uint32_t tess_mode = 0;
  DecodeGtlTessDrawShape(command.gtl_tess, tess_n, patch_count, tess_mode);
  if (patch_count == 0u) {
    ++stats.zero_patches;
    return true;
  }

  DirectFramebufferBinding framebuffer = ResolveDirectFramebufferLocked(
      device, output_framebuffer, output_depth_format, command);
  if (!framebuffer.framebuffer) {
    std::unordered_set<uint32_t> framebuffer_filter;
    AddDirectFramebufferResourceInterest(command, framebuffer_filter);
    if (!framebuffer_filter.empty()) {
      SyncDirectResourcesLocked(device, command_list, nullptr,
                                &framebuffer_filter);
      framebuffer = ResolveDirectFramebufferLocked(
          device, output_framebuffer, output_depth_format, command);
    }
  }
  if (!framebuffer.framebuffer) {
    ++stats.no_framebuffer;
    LogFailedDirectDrawLocked("tess_missing_framebuffer", command);
    return false;
  }

  // Sync the command's resources (shaders + ALL stated texture slots — the
  // slot-mask override in DirectCommandPixelTextureSlotMaskLocked makes the
  // interest walk include the vertex-stage slots 16-19).
  {
    std::unordered_set<uint32_t> resource_filter;
    std::unordered_set<uint32_t> texture_upload_filter;
    AddDirectCommandResourceInterest(command, resource_filter,
                                     texture_upload_filter);
    if (!resource_filter.empty() || !texture_upload_filter.empty()) {
      SyncDirectResourcesLocked(device, command_list, &texture_upload_filter,
                                &resource_filter);
    }
  }

  // Materialize the shader identities (base PS must be a ready tracked
  // resource; the base VS identity supplies the bytecode hash the tess
  // variant is keyed by) — same helper the ordinary draw path runs.
  EnsureDirectKnownShaderResourcesForCommandLocked(command);

  TransitionDirectFramebufferForWrite(command_list, framebuffer);
  command_list->setFramebuffer(framebuffer.framebuffer);
  TransitionDirectSampledTexturesForDrawLocked(command_list, command,
                                               framebuffer);
  plume::RenderPipeline* pipeline =
      EnsureGtlTessPipelineLocked(device, command, framebuffer);
  if (!pipeline) {
    ++stats.no_pipeline;
    return false;
  }
  command_list->setGraphicsPipelineLayout(
      g_plume_resources.pipeline_layout.get());
  if (!BindDirectDrawConstantsLocked(device, command_list, command,
                                     framebuffer)) {
    ++stats.no_constants;
    LogFailedDirectDrawLocked("tess_missing_constants", command);
    return false;
  }
  for (uint32_t set = 0; set < kDirectTextureDescriptorSets; ++set) {
    command_list->setGraphicsDescriptorSet(
        g_plume_resources.texture_descriptor_sets[set].get(), set);
  }
  command_list->setGraphicsDescriptorSet(
      g_plume_resources.sampler_descriptor_set.get(),
      kDirectSamplerDescriptorSet);
  command_list->setPipeline(pipeline);
  // The packet carries no viewport (the scene pass programs a full-target
  // viewport at these draws); bind the full framebuffer.
  command_list->setViewports(plume::RenderViewport(
      0.0f, 0.0f, static_cast<float>(framebuffer.width),
      static_cast<float>(framebuffer.height)));
  command_list->setScissors(plume::RenderRect(
      0, 0, static_cast<int32_t>(framebuffer.width),
      static_cast<int32_t>(framebuffer.height)));
  if (!BindGtlTessGridIndexBufferLocked(device, command_list, tess_n)) {
    ++stats.no_index_buffer;
    LogFailedDirectDrawLocked("tess_missing_grid_index_buffer", command);
    return false;
  }
  // One instance per patch; the tess-variant VS reads SV_InstanceID (+ the
  // index offset from SharedConstants +564) as the patch index and
  // SV_VertexID as the domain-grid position (T1 r0 seed contract).
  command_list->drawIndexedInstanced(6u * tess_n * tess_n, patch_count, 0, 0,
                                     0);

  ++stats.execs;
  stats.patches += patch_count;
  if (tess_n == 2u) {
    ++stats.n2;
  } else if (tess_n == 16u) {
    ++stats.n16;
  } else {
    ++stats.n_other;
  }
  if (stats.execs == 1u) {
    REXLOG_INFO(
        "Refii GTL TESS-DRAW first exec src=0x{:08X} lr=0x{:08X} mode={} N={} "
        "patches={} indexed={} initiator=0x{:08X} pm4_draws={} "
        "indx_offset={} arg_offset={} vs=0x{:08X} ps=0x{:08X} rt0=0x{:08X} "
        "ds=0x{:08X} tex16=0x{:08X} tex17=0x{:08X} tex18=0x{:08X} "
        "tex19=0x{:08X}",
        command.gtl_tess.source_symbol, command.gtl_tess.caller_lr, tess_mode,
        tess_n, patch_count, command.gtl_tess.indexed ? 1 : 0,
        command.gtl_tess.draw_initiator, command.gtl_tess.draw_packet_count,
        GtlTessIndexOffset(command.gtl_tess),
        command.gtl_tess.arg_index_offset, command.resources.vertex_shader,
        command.resources.pixel_shader, command.resources.render_targets[0],
        command.resources.depth_stencil, command.resources.textures[16],
        command.resources.textures[17], command.resources.textures[18],
        command.resources.textures[19]);
  }
  if (stats.execs <= 32u || (stats.execs & 63u) == 0u) {
    REXLOG_INFO(
        "Refii GTL tessdraw[execs={} patches={} n2={} n16={} n_other={} "
        "vs_tess_miss={} pred_skips={} zero_patches={} no_fb={} no_pipe={} "
        "no_const={} no_ib={}]",
        static_cast<unsigned long long>(stats.execs),
        static_cast<unsigned long long>(stats.patches),
        static_cast<unsigned long long>(stats.n2),
        static_cast<unsigned long long>(stats.n16),
        static_cast<unsigned long long>(stats.n_other),
        static_cast<unsigned long long>(stats.vs_tess_miss),
        static_cast<unsigned long long>(stats.pred_skips),
        static_cast<unsigned long long>(stats.zero_patches),
        static_cast<unsigned long long>(stats.no_framebuffer),
        static_cast<unsigned long long>(stats.no_pipeline),
        static_cast<unsigned long long>(stats.no_constants),
        static_cast<unsigned long long>(stats.no_index_buffer));
  }
  return true;
}

void RecordDirectRenderCommandsLocked(plume::RenderDevice* device,
                                      plume::RenderCommandList* command_list,
                                      plume::RenderFramebuffer* output_framebuffer,
                                      plume::RenderTexture* output_texture,
                                      plume::RenderFramebuffer* present_framebuffer,
                                      plume::RenderFormat output_depth_format,
                                      FableDirectFrameCommandBatch command_batch) {
  if (!device || !command_list || !output_framebuffer) {
    return;
  }
  const FableDirectClock::time_point commands_t0 = FableDirectClock::now();
  g_direct_sync_calls_this_compose = 0;
  g_direct_sync_early_outs_this_compose = 0;
  uint32_t recorded_draws = 0;
  uint32_t failed_draws = 0;
  uint32_t attempted_draws = 0;
  uint32_t recorded_clears = 0;
  uint32_t recorded_resolves = 0;
  const auto record_command = [&](FableDirectRenderCommand& command,
                                  bool submitted_command_source) {
    const FableDirectClock::time_point copy_t0 = FableDirectClock::now();
    // IN-PLACE record (2026-08-01). This used to deep-copy every replayed
    // command into a persistent static scratch (~12.6KB inline plus up to 17
    // heap inline-buffer vectors per command) so the record's normalization
    // could not alias the batch's stored command. The batch is MOVED into
    // this function and destroyed at its end, record_commands runs exactly
    // once per compose, and nothing reads a command after its record — so the
    // record path owns the stored command outright and normalizes it in
    // place. Run 486 (storybook menu) measured the old prep at
    // drawphase[copy=1534.2ms] for 2375 commands (~0.65ms each): the copy
    // itself, a guest-contended g_fable_direct.mutex acquisition per command
    // (scratch attach, previously run even for clears/resolves), and the
    // packet-identity authority scan re-run for packet-sourced commands.
    if (IsDirectDrawCommand(command)) {
      FableDirectAttachCachedScratchBuffers(command);
      if (RendererGtlExecutorEnabled() && !command.gtl_packet_sourced) {
        // Observer-command repair only: packet-sourced commands already carry
        // the packet's banks (see BuildDirectCommandFromGtlDrawPacketLocked);
        // re-matching them against the shadow backlog was an O(backlog)
        // identity scan per draw for a guaranteed no-op re-apply.
        ApplyGtlPacketIdentityAuthorityLocked(command);
      }
    }
    const FableDirectClock::time_point copy_t1 = FableDirectClock::now();
    g_direct_draw_phases.copy_ms += FableDirectElapsedMs(copy_t0, copy_t1);
    // BASE->OBJECT NORMALISATION IS DISABLED AT COMPILE TIME (2026-07-25).
    // `if (false && ...)` deliberately, matching the render-state authority
    // block below: a cvar gate was NOT enough. The cvar
    // (renderer_gtl_texture_base_normalisation) is not registered in the
    // launcher's GetRuntimeCVarDefinitions, and an unregistered key cannot be
    // relied on to read false — the plume UNKNOWN-topology abort came back with
    // the gate supposedly off, which is exactly what a gate that is not holding
    // looks like. Keep the call compiled (so the function does not rot) but
    // unreachable until it has a real per-draw scope AND proof that the object
    // it selects is the right one. See refii-resolve-draw-path-already-exists.
    if (false) {
      NormalizeDirectTextureSlotsToObjectsLocked(command);
    }
    switch (command.type) {
      case FableDirectRenderCommandType::kClear: {
        // (The clear-time PRE-draw video snapshot is retired: it latched
        // whatever the composite held at clear time — white/partial states
        // during movie starts — and re-presented them for up to 30 frames
        // (the Lionhead flashing). The latch now updates POST-draw at each
        // completed video compose, below in the draw case.)
        const FableDirectClock::time_point record_clear_t0 =
            FableDirectClock::now();
        RecordDirectClearCommand(device, command_list, output_framebuffer,
                                 output_depth_format, command);
        ++recorded_clears;
        g_direct_draw_phases.record_clear_ms +=
            FableDirectElapsedMs(record_clear_t0, FableDirectClock::now());
        break;
      }
      case FableDirectRenderCommandType::kDrawPrimitive:
      case FableDirectRenderCommandType::kDrawIndexedPrimitive:
      case FableDirectRenderCommandType::kDrawPrimitiveUP: {
        if (DirectDrawHasUnrecoverableMissingPipelineLocked(command)) {
          LogSkippedUnsupportedDirectDrawLocked(command, "missing_pipeline");
          // Book the skip-path cost (unrecoverable check + skip log) into
          // gate= — this break used to leave it outside every bucket.
          g_direct_draw_phases.record_gate_ms +=
              FableDirectElapsedMs(copy_t1, FableDirectClock::now());
          break;
        }
        // (No shader ensure here: RecordDirectDrawCommandLocked ensures this
        // exact command itself right after framebuffer resolution, so a call
        // at this site ran the same lookups twice per recorded draw.)
        MaybeBeginRenderDocDrawTargetCaptureLocked(command);
        ++attempted_draws;
        // Snapshot the latch target BEFORE recording: the record normalizes
        // the scratch command in place (a carried framebuffer can rewrite
        // render_targets[0]), and the latch has always keyed on the command's
        // pre-normalization target.
        const uint32_t pre_record_render_target0 =
            command.resources.render_targets[0];
        const FableDirectClock::time_point record_gate_t1 =
            FableDirectClock::now();
        g_direct_draw_phases.record_gate_ms +=
            FableDirectElapsedMs(copy_t1, record_gate_t1);
        const bool direct_draw_recorded = RecordDirectDrawCommandLocked(
            device, command_list, output_framebuffer, output_depth_format,
            command, submitted_command_source);
        const FableDirectClock::time_point record_draw_t1 =
            FableDirectClock::now();
        // The FULL call interval, success and failure alike. The drawphase
        // buckets tile only the path that reaches every accumulation point;
        // draw= minus their sum is the early-exit/failure leakage that run
        // 493 measured as a ~78ms hole.
        g_direct_draw_phases.record_draw_ms +=
            FableDirectElapsedMs(record_gate_t1, record_draw_t1);
        if (direct_draw_recorded) {
          ++recorded_draws;
          // Video pacing latch (contained per-video-frame timer): each
          // COMPLETED video compose replaces the latched presentation frame
          // post-draw. The pump re-presents the latch at any display rate
          // (60/240Hz alike) and switches exactly on video-frame
          // completion — the console's own present model (scanout repeats
          // the frontbuffer between guest presents).
          if ((command.draw.source == RendererDrawEventSource::kPixVideo ||
               DirectCommandIsExactBinkIndexedWrapperDrawLocked(command) ||
               // Startup video copy (scaled movie -> title-visible target): a
               // video draw the present selector must latch onto, symmetric to
               // the publish-classifier wiring. Without this the copy draw now
               // publishes+executes but last_pix_video_target stays 0, so the
               // present stays on the empty startup scene-clear (black video).
               // Reuses the semantic classifier (scaler texture + startup-copy
               // shader), not a guest-address whitelist.
               IsRenderDocStartupVideoCopyDrawTargetLocked(command)) &&
              // At most ONE latch copy per pump frame: pix frames issue
              // several video-classified draws (composite + helper passes),
              // and a 1080p GPU copy per draw stalled the pump — the pix
              // slow-motion regression. The last video draw of the frame
              // re-latches on the next frame's first draw, so content
              // freshness is unaffected at video cadence.
              g_direct_renderer.video_latch_frame_index !=
                  g_direct_renderer.current_frame_index) {
            const uint32_t video_target = pre_record_render_target0;
            PlumeTrackedResource* video_target_resource =
                video_target != 0 ? FindDirectResourceLocked(video_target)
                                  : nullptr;
            if (video_target_resource &&
                video_target_resource->guest_texture.texture &&
                PlumeGuestTextureWidth(*video_target_resource) >=
                    FableGuestDisplayWidth() &&
                PlumeGuestTextureHeight(*video_target_resource) >=
                    FableGuestDisplayHeight()) {
              RecordVideoPresentHoldSnapshotLocked(
                  device, command_list,
                  video_target_resource->guest_texture.texture.get(),
                  video_target,
                  PlumeGuestTextureWidth(*video_target_resource),
                  PlumeGuestTextureHeight(*video_target_resource),
                  video_target_resource->texture_format);
              g_direct_renderer.last_pix_video_target = video_target;
              g_direct_renderer.last_pix_video_frame =
                  g_direct_renderer.current_frame_index;
              g_direct_renderer.video_latch_frame_index =
                  g_direct_renderer.current_frame_index;
            }
          }
        } else {
          LogFinalFailedDirectCommandLocked("draw", command);
          ++failed_draws;
        }
        g_direct_draw_phases.record_latch_ms +=
            FableDirectElapsedMs(record_draw_t1, FableDirectClock::now());
        break;
      }
      // kResolveCopy live executor case deleted 2026-07-18: the last
      // producer (export-invalidate family) measured zero-fire across
      // runs 559/560, so live streams never carry this type. The
      // frame-database replay path records resolve copies directly.
      case FableDirectRenderCommandType::kGtlResolve: {
        // §3b live execution: the packet owns the resolve at its interleaved
        // packet_id position. Host copy when formats agree; on the copy's
        // format refusal the format-converting resolve draw is the owner.
        // Other refusals (depth deferred, same-texture, ambiguous identity)
        // are counted, not failed — they are the census's refuse classes.
        if (!command.gtl_resolve_valid) {
          break;
        }
        const FableDirectClock::time_point record_resolve_t0 =
            FableDirectClock::now();
        GtlResolveHostCopyStats& live_stats = g_gtl_resolve_live_stats;
        // Hang forensics (run 486: frame thread went silent ~21s after live
        // execution began): log entry/exit around each primitive so a core's
        // last line names the phase. First 16, then every 64th.
        static uint32_t live_exec_serial = 0;
        const uint32_t exec_id = ++live_exec_serial;
        const bool trace_exec = exec_id <= 16u || (exec_id & 63u) == 0u;
        if (trace_exec) {
          REXLOG_INFO(
              "Refii GTL resolve LIVE #{} enter pkt={} dest=0x{:08X} "
              "src=0x{:08X} flags=0x{:08X}",
              exec_id, command.gtl_resolve.packet_id,
              command.gtl_resolve.dest_texture,
              command.gtl_resolve.source_surface, command.gtl_resolve.flags);
        }
        // HOST-COPY LIVE RECORDING DELETED 2026-08-02 (runs 497-503 measured
        // buildable=0 — all menu/video traffic). RE-ADD TRIGGER FIRED the same
        // day: run 520's in-game frames classify a large fraction of resolves
        // buildable (`resolve LIVE exit path=copy_buildable_dropped`), and
        // dropping them left every well-formed EDRAM->texture resolve
        // unmaterialized — the world/loading black screen. Rather than
        // resurrect a second (raw-copy) recorder, buildable resolves ride the
        // SAME format-pipeline resolve draw as the mismatched ones: the blit
        // is format-agnostic (samples source into the destination framebuffer
        // with the destination-format pipeline), so same-format is just its
        // trivial case. A vkCmdCopyImage fast path can come back later as a
        // measured optimization, not as a correctness fork.
        const uint32_t fm_before = live_stats.format_mismatch;
        const uint32_t ext_before = live_stats.extent_mismatch;
        bool live_recorded = false;
        const char* live_path = "refused";
        // One-pass endpoint resolution (2026-08-02): source exact-object find
        // + hybrid destination walk run ONCE here and feed both the
        // classifier and the resolve draw — previously each performed both
        // lookups itself (the hybrid walk is a full registry scan), doubling
        // the dominant cost of resolvesplit find=. Refusal order preserved:
        // the destination walk (and its repoint side effects) only runs when
        // the source exists, exactly as the classifier's own ladder did.
        ++g_direct_draw_phases.resolve_execs;
        GtlResolveEndpoints live_endpoints;
        live_endpoints.source = FindDirectResourceByExactObjectLocked(
            command.gtl_resolve.source_surface);
        if (live_endpoints.source != nullptr) {
          live_endpoints.destination =
              FindGtlResolveDestinationHybridLocked(command.gtl_resolve);
        }
        const FableDirectClock::time_point resolve_classify_t0 =
            FableDirectClock::now();
        g_direct_draw_phases.resolve_find_ms +=
            FableDirectElapsedMs(record_resolve_t0, resolve_classify_t0);
        const bool live_copy_buildable = IsGtlResolveHostCopyBuildableLocked(
            command.gtl_resolve, &live_stats, &live_endpoints);
        g_direct_draw_phases.resolve_classify_ms += FableDirectElapsedMs(
            resolve_classify_t0, FableDirectClock::now());
        if (live_copy_buildable) {
          if (trace_exec) {
            REXLOG_INFO("Refii GTL resolve LIVE #{} copy-via-draw enter",
                        exec_id);
          }
          if (RecordGtlResolveDrawLocked(command_list, command.gtl_resolve,
                                         &live_stats, /*dry_run=*/false,
                                         &live_endpoints)) {
            live_recorded = true;
            live_path = "copy_via_draw";
          } else {
            live_path = "copy_draw_refused";
          }
        } else if (live_stats.format_mismatch != fm_before ||
                   live_stats.extent_mismatch != ext_before) {
          if (trace_exec) {
            REXLOG_INFO("Refii GTL resolve LIVE #{} draw-path enter", exec_id);
          }
          if (RecordGtlResolveDrawLocked(command_list, command.gtl_resolve,
                                         &live_stats, /*dry_run=*/false,
                                         &live_endpoints)) {
            live_recorded = true;
            live_path = "draw";
          }
        }
        if (live_recorded) {
          ++recorded_resolves;
        }
        if (trace_exec) {
          REXLOG_INFO("Refii GTL resolve LIVE #{} exit path={}", exec_id,
                      live_path);
        }
        g_direct_draw_phases.record_resolve_ms +=
            FableDirectElapsedMs(record_resolve_t0, FableDirectClock::now());
        break;
      }
      case FableDirectRenderCommandType::kGtlTessDraw: {
        // T3: hardware-tessellator patch draw at its interleaved packet_id
        // position. Skips (predication, zero patches) return true and are
        // censused inside; only genuine failures count as failed draws.
        if (!command.gtl_tess_valid) {
          break;
        }
        const FableDirectClock::time_point record_tess_t0 =
            FableDirectClock::now();
        if (RecordGtlTessDrawLocked(device, command_list, output_framebuffer,
                                    output_depth_format, command)) {
          ++recorded_draws;
        } else {
          LogFinalFailedDirectCommandLocked("tessdraw", command);
          ++failed_draws;
        }
        g_direct_draw_phases.record_draw_ms +=
            FableDirectElapsedMs(record_tess_t0, FableDirectClock::now());
        break;
      }
      default:
        break;
    }
  };
  const auto record_commands =
      [&](std::vector<FableDirectRenderCommand>& commands,
          bool submitted_command_source) {
    // Label work is for capture tooling. When labels are off (RenderDoc not
    // loaded and no explicit REFII_RENDERER_DEBUG_LABELS=1 — see
    // DirectDebugLabelsEnabled) skip the whole pass/step scope machinery:
    // the per-command snprintf pairs, the beginDebugLabel/endDebugLabel
    // calls (previously still issued with EMPTY labels through
    // begin_pass_scope even when the label build was disabled), and the
    // label-only ClassifyDirectRenderPassLocked call here. The submit-site
    // Classify call keeps running — it owns the topology noting.
    const bool labels_on = DirectDebugLabelsEnabled();
    bool pass_scope_active = false;
    std::string active_pass_name;
    const auto end_pass_scope = [&]() {
      if (!pass_scope_active) {
        return;
      }
      command_list->endDebugLabel();
      pass_scope_active = false;
      active_pass_name.clear();
    };
    const auto begin_pass_scope = [&](const FableDirectRenderCommand& command) {
      if (!labels_on) {
        return;
      }
      const char* pass_name = ClassifyDirectRenderPassLocked(command);
      if (pass_scope_active && active_pass_name == pass_name) {
        return;
      }
      end_pass_scope();
      active_pass_name = pass_name;
      command_list->beginDebugLabel(
          BuildDirectPassDebugLabelLocked(active_pass_name));
      pass_scope_active = true;
    };
    for (FableDirectRenderCommand& command : commands) {
      // recordsplit label=: the pass-scope maintenance plus the per-command
      // step-label build/begin/end below. record_command's own time is
      // excluded (it is the drawphase).
      const FableDirectClock::time_point pass_label_t0 =
          FableDirectClock::now();
      if (IsDirectDrawCommand(command)) {
        begin_pass_scope(command);
      } else {
        end_pass_scope();
      }
      g_direct_draw_phases.record_label_ms +=
          FableDirectElapsedMs(pass_label_t0, FableDirectClock::now());
      // A clear of the video composite arriving later in the same batch
      // erases the movie frame before the batch-end hold snapshot runs (the
      // run-to-run "black intro" variance). Snapshot the composite before
      // the wipe executes.
      if (command.type == FableDirectRenderCommandType::kClear &&
          g_direct_renderer.pending_video_hold_texture &&
          std::find(command.resources.render_targets.begin(),
                    command.resources.render_targets.end(),
                    g_direct_renderer.pending_video_hold_target) !=
              command.resources.render_targets.end()) {
        RecordVideoPresentHoldSnapshotLocked(
            device, command_list, g_direct_renderer.pending_video_hold_texture,
            g_direct_renderer.pending_video_hold_target,
            g_direct_renderer.pending_video_hold_width,
            g_direct_renderer.pending_video_hold_height,
            g_direct_renderer.pending_video_hold_format);
        g_direct_renderer.pending_video_hold_texture = nullptr;
        g_direct_renderer.pending_video_hold_target = 0;
      }
      if (command.type == FableDirectRenderCommandType::kClear) {
        ZoneScopedN("RenderGraph::Scheduler/Step::ClearCommands");
        const FableDirectClock::time_point step_label_t0 =
            FableDirectClock::now();
        char clear_step_label[160] = {};
        if (labels_on) {
          std::snprintf(
              clear_step_label, sizeof(clear_step_label),
              "RefiiRenderGraph::Scheduler::Step::ClearCommands Frame %u "
              "Serial %llu",
              g_direct_renderer.current_frame_index,
              static_cast<unsigned long long>(command.serial));
        }
        PlumeDirectDebugLabelScope clear_step_scope(command_list,
                                                    clear_step_label);
        g_direct_draw_phases.record_label_ms +=
            FableDirectElapsedMs(step_label_t0, FableDirectClock::now());
        record_command(command, submitted_command_source);
        const FableDirectClock::time_point step_label_end_t0 =
            FableDirectClock::now();
        clear_step_scope.End();
        g_direct_draw_phases.record_label_ms +=
            FableDirectElapsedMs(step_label_end_t0, FableDirectClock::now());
      } else if (IsDirectDrawCommand(command)) {
        ZoneScopedN("RenderGraph::Scheduler/Step::DrawCommands");
        const FableDirectClock::time_point step_label_t0 =
            FableDirectClock::now();
        char draw_step_label[192] = {};
        if (labels_on) {
          std::snprintf(
              draw_step_label, sizeof(draw_step_label),
              "RefiiRenderGraph::Scheduler::Step::DrawCommands Frame %u "
              "Draw %llu Serial %llu",
              g_direct_renderer.current_frame_index,
              static_cast<unsigned long long>(command.draw_serial),
              static_cast<unsigned long long>(command.serial));
        }
        PlumeDirectDebugLabelScope draw_step_scope(command_list,
                                                   draw_step_label);
        g_direct_draw_phases.record_label_ms +=
            FableDirectElapsedMs(step_label_t0, FableDirectClock::now());
        record_command(command, submitted_command_source);
        const FableDirectClock::time_point step_label_end_t0 =
            FableDirectClock::now();
        draw_step_scope.End();
        g_direct_draw_phases.record_label_ms +=
            FableDirectElapsedMs(step_label_end_t0, FableDirectClock::now());
      } else if (command.type == FableDirectRenderCommandType::kResolveCopy) {
        ZoneScopedN("RenderGraph::Scheduler/Step::ResolveCommands");
        const FableDirectClock::time_point step_label_t0 =
            FableDirectClock::now();
        char resolve_step_label[192] = {};
        if (labels_on) {
          std::snprintf(
              resolve_step_label, sizeof(resolve_step_label),
              "RefiiRenderGraph::Scheduler::Step::ResolveCommands Frame %u "
              "Serial %llu",
              g_direct_renderer.current_frame_index,
              static_cast<unsigned long long>(command.serial));
        }
        PlumeDirectDebugLabelScope resolve_step_scope(command_list,
                                                      resolve_step_label);
        g_direct_draw_phases.record_label_ms +=
            FableDirectElapsedMs(step_label_t0, FableDirectClock::now());
        record_command(command, submitted_command_source);
        const FableDirectClock::time_point step_label_end_t0 =
            FableDirectClock::now();
        resolve_step_scope.End();
        g_direct_draw_phases.record_label_ms +=
            FableDirectElapsedMs(step_label_end_t0, FableDirectClock::now());
      } else if (command.type == FableDirectRenderCommandType::kGtlResolve) {
        // resolve= (accumulated inside record_command) owns this branch;
        // keeping it out of state= keeps that census pure kSet*/other.
        record_command(command, submitted_command_source);
      } else {
        // State/other commands (kSet* family): 511 submitted vs ~375
        // draw/clear/resolve at the run-493 menu leaves 100+ commands on
        // this branch — state= says whether they cost anything.
        const FableDirectClock::time_point state_t0 = FableDirectClock::now();
        record_command(command, submitted_command_source);
        g_direct_draw_phases.record_state_ms +=
            FableDirectElapsedMs(state_t0, FableDirectClock::now());
      }
      // Whole loop-body span for this command; pass_label_t0 is the
      // iteration's first timestamp. iter= minus the per-command buckets is
      // the loop's own dispatch/check overhead.
      g_direct_draw_phases.record_iter_ms +=
          FableDirectElapsedMs(pass_label_t0, FableDirectClock::now());
    }
    const FableDirectClock::time_point tail_label_t0 = FableDirectClock::now();
    end_pass_scope();
    g_direct_draw_phases.record_label_ms +=
        FableDirectElapsedMs(tail_label_t0, FableDirectClock::now());
  };

  std::vector<FableDirectRenderCommand> submitted_commands =
      std::move(command_batch.submitted_commands);
  const FableDirectStateSnapshot staged_snapshot =
      command_batch.staged_snapshot;
  NormalizeDirectInheritedScratchWrapperShaders(
      submitted_commands, staged_snapshot,
      g_direct_renderer.last_scratch_wrapper_shader_binding,
      g_direct_renderer.current_frame_index);
  // THE single batch compaction. Also sweeps commands the supersession /
  // record-backlog-cap passes MARKED (record_dropped) instead of erasing —
  // each of those passes used to run its own remove_if over ~12.6KB elements
  // (an O(N) memmove per drop event, the run-321 storm class); marking defers
  // them all to this one pass that was compacting anyway. Elements moved here
  // feed the copies=/copy_kb= census.
  auto strip_state_only_commands =
      [](std::vector<FableDirectRenderCommand>& commands) {
        const auto stripped = [](const FableDirectRenderCommand& command) {
          return !IsDirectActionCommand(command) || command.record_dropped;
        };
        const auto first_removed =
            std::find_if(commands.begin(), commands.end(), stripped);
        if (first_removed == commands.end()) {
          return;
        }
        const size_t first_removed_index =
            static_cast<size_t>(first_removed - commands.begin());
        commands.erase(std::remove_if(first_removed, commands.end(), stripped),
                       commands.end());
        // Survivors relocated by the compaction: everything now stored at or
        // after the first removed slot was move-assigned once.
        const size_t moved = commands.size() > first_removed_index
                                 ? commands.size() - first_removed_index
                                 : 0u;
        g_direct_draw_phases.copy_count += moved;
        g_direct_draw_phases.copy_bytes +=
            moved * sizeof(FableDirectRenderCommand);
  };
  // PRE-RECORD CLEAR COALESCING (2026-08-01, run 490: 776 clears/compose at
  // the storybook menu, recordsplit clear=1603ms). Supersession keeps the
  // non-draw commands of dropped frames BY DESIGN, so the clears of 2-3 stale
  // frames stack in one batch with their draws already marked record_dropped.
  // A clear obliterates the prior content of exactly the (targets, rect,
  // flags) it touches, so when two clears share an identical semantic key and
  // no still-recording action command sits between them, only the LAST one
  // can have any effect — mark the earlier record_dropped (swept by the
  // single compaction below, counted as clears coalesced). Guest-semantics-
  // faithful: this drops no clear the console would have observed.
  // Interposition is PER-TARGET (2026-08-01, run 492: the first cut wiped the
  // whole pending map on ANY non-clear action, and superseded frames' KEPT
  // resolves sat between every stale frame's clears — clears_coalesced=0 at
  // 3,304 clears/compose). Each pending clear remembers its touched targets
  // (its 4 bound RTs + depth_stencil, nonzero only); a live non-clear action
  // evicts only the pending entries whose target set intersects the action's
  // touched set:
  //  - draws: 4 RTs + depth_stencil + all 16 texture slots (a draw SAMPLING a
  //    cleared target must see that clear; vertex/index buffers are never
  //    clear targets and are skipped);
  //  - kResolveCopy: rb_copy_dest_base + refii_source_surface, plus its
  //    bound RTs/depth (refii_source_surface can be 0 on a tracking gap);
  //  - kGtlResolve: the by-value ResolvePacket's source_surface + dest
  //    identities (dest_texture, dest_registry_object, dest_base_address,
  //    dest_mip_address); a packet-less kGtlResolve (gtl_resolve_valid ==
  //    false — should not exist) keeps the conservative full wipe.
  // The key hashes every input the clear-path memo hashes (targets, flags, LR
  // class, private-rect classification, extent inputs) PLUS the clear rect,
  // so the surviving clear is a full semantic substitute for each dropped
  // one — including the private-rect setup-clear side effects, which the
  // survivor re-derives from the identical inputs. Colors/depth values are
  // deliberately NOT keyed: the later clear wins regardless of what values
  // the dropped one carried.
  {
    struct PendingClear {
      FableDirectRenderCommand* command = nullptr;
      std::array<uint32_t, 5> targets = {};  // 4 RTs + depth_stencil
    };
    std::unordered_map<uint64_t, PendingClear> pending_clears;
    for (FableDirectRenderCommand& command : submitted_commands) {
      if (command.record_dropped || !IsDirectActionCommand(command)) {
        // Never records — semantically absent from the batch (superseded
        // draws land here, which is what lets stale frames' clears stack).
        continue;
      }
      if (command.type != FableDirectRenderCommandType::kClear) {
        std::array<uint32_t, 21> touched = {};
        size_t touched_count = 0;
        const auto add_touched = [&](uint32_t address) {
          if (address != 0 && touched_count < touched.size()) {
            touched[touched_count++] = address;
          }
        };
        bool full_wipe = false;
        if (IsDirectDrawCommand(command)) {
          for (uint32_t render_target : command.resources.render_targets) {
            add_touched(render_target);
          }
          add_touched(command.resources.depth_stencil);
          for (uint32_t texture : command.resources.textures) {
            add_touched(texture);
          }
        } else if (command.type ==
                   FableDirectRenderCommandType::kResolveCopy) {
          add_touched(command.resolve.rb_copy_dest_base);
          add_touched(command.resolve.refii_source_surface);
          // refii_source_surface can be 0 on a device-state tracking gap; the
          // bound targets name the EDRAM source surfaces at resolve time.
          for (uint32_t render_target : command.resources.render_targets) {
            add_touched(render_target);
          }
          add_touched(command.resources.depth_stencil);
        } else if (command.type == FableDirectRenderCommandType::kGtlResolve &&
                   command.gtl_resolve_valid) {
          add_touched(command.gtl_resolve.source_surface);
          add_touched(command.gtl_resolve.dest_texture);
          add_touched(command.gtl_resolve.dest_registry_object);
          add_touched(command.gtl_resolve.dest_base_address);
          add_touched(command.gtl_resolve.dest_mip_address);
        } else {
          full_wipe = true;
        }
        if (full_wipe) {
          if (!pending_clears.empty()) {
            pending_clears.clear();
            ++g_direct_draw_phases.record_coalesce_wipes;
          }
        } else if (touched_count != 0 && !pending_clears.empty()) {
          for (auto pending = pending_clears.begin();
               pending != pending_clears.end();) {
            bool intersects = false;
            for (uint32_t target : pending->second.targets) {
              if (target == 0) {
                continue;
              }
              for (size_t i = 0; i < touched_count; ++i) {
                if (touched[i] == target) {
                  intersects = true;
                  break;
                }
              }
              if (intersects) {
                break;
              }
            }
            if (intersects) {
              pending = pending_clears.erase(pending);
              ++g_direct_draw_phases.record_coalesce_evictions;
            } else {
              ++pending;
            }
          }
        }
        continue;
      }
      uint64_t key = 1469598103934665603ull;
      const auto mix = [&](uint64_t value) {
        key ^= value;
        key *= 1099511628211ull;
      };
      for (uint32_t render_target : command.resources.render_targets) {
        mix(render_target);
      }
      mix(command.resources.depth_stencil);
      mix(command.clear.flags);
      mix(command.clear.rect.valid ? 1u : 0u);
      mix(static_cast<uint64_t>(
          static_cast<uint32_t>(command.clear.rect.left)));
      mix(static_cast<uint64_t>(static_cast<uint32_t>(command.clear.rect.top)));
      mix(static_cast<uint64_t>(
          static_cast<uint32_t>(command.clear.rect.right)));
      mix(static_cast<uint64_t>(
          static_cast<uint32_t>(command.clear.rect.bottom)));
      mix(command.draw.state_flush_lr);
      mix(command.draw.private_texture_rectangle ? 1u : 0u);
      mix(command.resources.pixel_shader);
      mix(command.viewport.valid ? 1u : 0u);
      mix(static_cast<uint64_t>(
          std::bit_cast<uint32_t>(command.viewport.width)));
      mix(static_cast<uint64_t>(
          std::bit_cast<uint32_t>(command.viewport.height)));
      mix(command.scissor.valid ? 1u : 0u);
      mix(static_cast<uint64_t>(static_cast<uint32_t>(command.scissor.left)));
      mix(static_cast<uint64_t>(static_cast<uint32_t>(command.scissor.top)));
      mix(static_cast<uint64_t>(static_cast<uint32_t>(command.scissor.right)));
      mix(static_cast<uint64_t>(
          static_cast<uint32_t>(command.scissor.bottom)));
      mix(command.draw.private_texture_corners_valid ? 1u : 0u);
      if (command.draw.private_texture_corners_valid) {
        for (float corner : command.draw.private_texture_corners) {
          mix(static_cast<uint64_t>(std::bit_cast<uint32_t>(corner)));
        }
      }
      PendingClear entry;
      entry.command = &command;
      entry.targets = {command.resources.render_targets[0],
                       command.resources.render_targets[1],
                       command.resources.render_targets[2],
                       command.resources.render_targets[3],
                       command.resources.depth_stencil};
      const auto [pending_it, inserted] =
          pending_clears.try_emplace(key, entry);
      if (!inserted) {
        pending_it->second.command->record_dropped = true;
        ++g_direct_draw_phases.record_clears_coalesced;
        pending_it->second = entry;
      }
    }
  }
  strip_state_only_commands(submitted_commands);
  const uint32_t normalized_bink_clear_order =
      FableDirectNormalizeExactBinkClearOrder(
          submitted_commands, DirectCommandIsExactBinkIndexedWrapperDrawLocked);
  if (RendererVerboseTracingEnabled() && normalized_bink_clear_order != 0 &&
      g_direct_renderer.diagnostics.logged_bink_command_order < 96u) {
    REXLOG_INFO(
        "Refii plume exact Bink clear order normalized frame={} count={}",
        g_direct_renderer.current_frame_index, normalized_bink_clear_order);
  }
  const FableDirectClock::time_point consume_t1 = FableDirectClock::now();
  FableDirectLogCommandSourceSummary(
      submitted_commands, g_direct_renderer.current_frame_index,
      g_direct_renderer.diagnostics.logged_command_source_summaries);
  FableDirectLogBinkCommandOrder(
      submitted_commands, RendererVerboseTracingEnabled(),
      g_direct_renderer.current_frame_index,
      g_direct_renderer.diagnostics.logged_bink_command_order,
      DirectCommandIsExactBinkIndexedWrapperDrawLocked);
  // Compose-scoped SUCCESS cache for the shader/declaration ensure (2026-08-01).
  // This compose used to run EnsureDirectKnownShaderResourcesForCommandLocked
  // up to 3x per draw (this summary loop, add_command_interests, and the
  // record path) while hundreds of commands share a handful of shader
  // addresses — each ready address paying a FindDirectResourceLocked per call.
  // Cache only SUCCESS: a ready resource stays ready for the rest of the
  // compose (nothing here retires resources), so skipping the re-ensure is
  // exact. Failures are deliberately NOT cached — an address that cannot be
  // ensured pre-sync can become ensurable after SyncDirectResourcesLocked's
  // shader-identity prewarm, and the record path retries it.
  std::unordered_set<uint32_t> ensured_shader_ok;
  std::unordered_set<uint32_t> ensured_declaration_ok;
  const auto ensure_command_shader_resources_once =
      [&](const FableDirectRenderCommand& command) {
        const auto ensure_shader = [&](uint32_t address) {
          if (address == 0 || ensured_shader_ok.count(address) != 0) {
            return;
          }
          if (EnsureDirectKnownShaderResourceLocked(address)) {
            ensured_shader_ok.insert(address);
          }
        };
        ensure_shader(command.resources.vertex_shader);
        ensure_shader(command.resources.pixel_shader);
        const uint32_t declaration = command.resources.vertex_declaration;
        if (declaration != 0 &&
            ensured_declaration_ok.count(declaration) == 0 &&
            EnsureDirectKnownVertexDeclarationResourceLocked(declaration)) {
          ensured_declaration_ok.insert(declaration);
        }
      };
  for (const FableDirectRenderCommand& command : submitted_commands) {
    if (IsDirectDrawCommand(command)) {
      ensure_command_shader_resources_once(command);
    }
  }
  CaptureFrameDatabaseDrawPacketsLocked(submitted_commands);
  // (The §3b resolve ORDER census that lived here — measurement only — is
  // DELETED 2026-08-01. Its question is answered: observer_resolves=0 always
  // (runs 466/469/470), kResolveCopy never rides this stream, and the resolve
  // interleave now inserts GTL resolves by packet_id in the authoritative
  // merge. It cost an O(resolves x packets) scan per compose in summary_ms.)
  const bool packet_queue_action_replay_active =
      RendererPlumePacketQueueReplayEnabled() &&
      g_direct_renderer.refii_packet_queue.replay_ready &&
      RefiiPacketQueueHasOutOfBandBackendReplayWork(
          g_direct_renderer.refii_packet_queue);
  // Non-const: the record loop normalizes commands IN PLACE (see
  // record_command). Every other consumer below reads only.
  std::vector<FableDirectRenderCommand>& replay_commands = submitted_commands;
  g_direct_renderer.stats.packet_queue_depth =
      g_direct_renderer.refii_packet_queue.replay.queue_depth;
  g_direct_renderer.stats.packet_queue_replay_planned =
      g_direct_renderer.refii_packet_queue.replay.planned_draw_packets;
  g_direct_renderer.stats.packet_queue_replay_unsupported =
      g_direct_renderer.refii_packet_queue.replay.unsupported_packets;
  g_direct_renderer.stats.packet_queue_replay_enabled =
      packet_queue_action_replay_active;
  const FableDirectClock::time_point summary_t1 = FableDirectClock::now();
  g_direct_renderer.current_frame_has_pix_video_draw =
      std::any_of(
          replay_commands.begin(), replay_commands.end(),
          [](const FableDirectRenderCommand& command) {
            return FableVideoCommandIsDirectVideoDraw(
                command, DirectCommandIsExactBinkIndexedWrapperDrawLocked);
          });
  if (!g_direct_renderer.current_frame_has_pix_video_draw) {
    RememberDirectPrivateTextureSetupClearIntentsLocked(replay_commands);
  }
  const FableDirectClock::time_point preflight_t1 = FableDirectClock::now();
  std::unordered_set<uint32_t> resource_filter;
  std::unordered_set<uint32_t> texture_upload_filter;
  std::unordered_set<uint32_t> changed_resource_filter;
  std::unordered_set<uint32_t> changed_texture_upload_filter;
  std::unordered_set<uint32_t> exact_title_upload_resource_filter;
  std::unordered_set<uint32_t> exact_title_upload_texture_filter;
  // Packet-owned materialization pressure runs FIRST: everything this
  // frame's draws bind is interest before any legacy collector runs, so the
  // novelty census below measures exactly what the collectors still add.
  AddGtlPacketResourceInterestsLocked(resource_filter, texture_upload_filter);
  const FableDirectClock::time_point exact_title_preflight_t0 =
      FableDirectClock::now();
  if (!g_direct_renderer.current_frame_has_pix_video_draw) {
    // Alias/link-expanded addresses (upload carriers) the packet stream does
    // not carry — proven 30/38 novel vs packet interest; stays until the
    // content-authority phase owns those aliases.
    CollectDirectExactTitleUploadPreflightInterestsLocked(
        replay_commands, exact_title_upload_resource_filter,
        exact_title_upload_texture_filter);
  }
  const FableDirectClock::time_point exact_title_preflight_collect_t1 =
      FableDirectClock::now();
  if (!g_direct_renderer.current_frame_has_pix_video_draw) {
    if (!exact_title_upload_resource_filter.empty() ||
        !exact_title_upload_texture_filter.empty()) {
      PlumeDirectDebugLabelScope sync_scope(
          command_list, "RefiiRenderer::SyncExactTitleUploads");
      SyncDirectResourcesLocked(device, command_list,
                                &exact_title_upload_texture_filter,
                                &exact_title_upload_resource_filter, false);
    }
  }
  const FableDirectClock::time_point exact_title_preflight_sync_t1 =
      FableDirectClock::now();
  const auto add_command_interests =
      [&](const std::vector<FableDirectRenderCommand>& commands) {
    for (const FableDirectRenderCommand& command : commands) {
      if (IsDirectDrawCommand(command)) {
        ensure_command_shader_resources_once(command);
        if (!FableVideoCommandIsDirectVideoDraw(
                command, DirectCommandIsExactBinkIndexedWrapperDrawLocked)) {
          PrepareDirectPrivateRectRenderTargetsForCommandLocked(device,
                                                                command);
          PrepareDirectExactCopyDestinationsForCommandLocked(device,
                                                             command);
        }
      }
      if (FableVideoCommandIsDirectVideoDraw(
              command, DirectCommandIsExactBinkIndexedWrapperDrawLocked)) {
        CollectDirectVideoCommandNonPlaneResourceInterestLocked(
            command, resource_filter, texture_upload_filter);
        continue;
      }

      const bool input_ready_cache_eligible =
          DirectCommandInputReadyCacheEligibleLocked(command);
      const uint64_t input_ready_cache_key =
          input_ready_cache_eligible
              ? DirectCommandInputReadyCacheKeyLocked(command)
              : 0ull;
      if (input_ready_cache_eligible &&
          FindCachedDirectCommandInputReadyLocked(input_ready_cache_key)) {
        static uint32_t logged_direct_input_interest_cache_hits = 0;
        if (logged_direct_input_interest_cache_hits < 64u) {
          REXLOG_INFO(
              "Refii plume direct input-ready interest cache hit #{} "
              "frame={} serial={} source={} rt=0x{:08X} ds=0x{:08X} "
              "vs=0x{:08X} ps=0x{:08X}",
              ++logged_direct_input_interest_cache_hits,
              g_direct_renderer.current_frame_index,
              static_cast<unsigned long long>(command.serial),
              static_cast<uint32_t>(command.draw.source),
              command.resources.render_targets[0],
              command.resources.depth_stencil,
              command.resources.vertex_shader,
              command.resources.pixel_shader);
        }
        continue;
      }
      if (input_ready_cache_eligible &&
          DirectCommandNonTextureInputsReadyLocked(command)) {
        static uint32_t logged_direct_title_nontexture_ready_edges = 0;
        if (logged_direct_title_nontexture_ready_edges < 64u) {
          REXLOG_INFO(
              "Refii plume direct title non-texture inputs ready #{} "
              "frame={} serial={} source={} rt=0x{:08X} ds=0x{:08X} "
              "vs=0x{:08X} ps=0x{:08X}",
              ++logged_direct_title_nontexture_ready_edges,
              g_direct_renderer.current_frame_index,
              static_cast<unsigned long long>(command.serial),
              static_cast<uint32_t>(command.draw.source),
              command.resources.render_targets[0],
              command.resources.depth_stencil,
              command.resources.vertex_shader,
              command.resources.pixel_shader);
        }
        // Missing-texture pull for this fast path (the run-664 black-paint
        // class) is covered by the packet-owned interest added above; the
        // legacy per-command collector proved novelty-free against it.
        continue;
      }
      if (input_ready_cache_eligible &&
          DirectCommandInputsReadyWithoutMissingInterestLocked(command)) {
        static uint32_t logged_direct_input_preinterest_ready_hits = 0;
        if (logged_direct_input_preinterest_ready_hits < 64u) {
          REXLOG_INFO(
              "Refii plume direct input-ready preinterest hit #{} "
              "frame={} serial={} source={} rt=0x{:08X} ds=0x{:08X} "
              "vs=0x{:08X} ps=0x{:08X}",
              ++logged_direct_input_preinterest_ready_hits,
              g_direct_renderer.current_frame_index,
              static_cast<unsigned long long>(command.serial),
              static_cast<uint32_t>(command.draw.source),
              command.resources.render_targets[0],
              command.resources.depth_stencil,
              command.resources.vertex_shader,
              command.resources.pixel_shader);
        }
        continue;
      }

      AddDirectCommandResourceInterest(command, changed_resource_filter,
                                       changed_texture_upload_filter, false);
      // Vertex-input materialization for NON-PACKET draws (pix-video and
      // scratch-ring families): packets carry stream pointers only for
      // wrapper draws, so a not-ready vertex/index buffer here must enter
      // the missing-resource sync or the draw fails missing_vertex_input on
      // its only attempt. The white startup videos were this: the compose
      // draws failed, landed on the retry path, and recorded after the
      // clear-time hold snapshot — which then latched the pre-draw white.
      AddDirectCommandMissingVertexInputInterestLocked(command,
                                                       resource_filter);
    }
  };
  add_command_interests(replay_commands);
  if (packet_queue_action_replay_active) {
    for (const RefiiFrameActionPacket& action :
         g_direct_renderer.refii_packet_queue.actions) {
      AddRefiiFrameActionResourceInterestsLocked(
          action, resource_filter, texture_upload_filter);
    }
  }
  // Deliver objects whose execute-time resolve failed last frame despite a
  // registered meaningful CPU upload (queued at the resolver null tail).
  if (!g_direct_renderer.deferred_resolve_interest.empty()) {
    for (const uint32_t address :
         g_direct_renderer.deferred_resolve_interest) {
      resource_filter.insert(address);
      texture_upload_filter.insert(address);
    }
    g_direct_renderer.deferred_resolve_interest.clear();
  }
  const FableDirectClock::time_point interest_t1 = FableDirectClock::now();
  const bool has_direct_action_commands =
      !replay_commands.empty();
  const uint32_t packet_surface_destination_actions =
      packet_queue_action_replay_active
          ? ClampCounter(static_cast<uint64_t>(std::count_if(
                g_direct_renderer.refii_packet_queue.actions.begin(),
                g_direct_renderer.refii_packet_queue.actions.end(),
                [](const RefiiFrameActionPacket& action) {
                  return action.type ==
                         RefiiFrameActionType::kSurfaceDestination;
                })))
          : 0u;
  if (packet_surface_destination_actions != 0) {
    // Packet-action replay is the owner for this primitive family. Reset the
    // per-frame census to packet attempts before replay; do not also execute
    // the legacy registry scan and duplicate the copies/Frame DB commands.
    ResetDirectSurfaceDestinationStatsLocked(
        packet_surface_destination_actions);
  }
  if (has_direct_action_commands && !changed_resource_filter.empty()) {
    PlumeDirectDebugLabelScope sync_scope(
        command_list, "RefiiRenderer::SyncChangedResources");
    // Plane content arrives through the dedicated Bink staging channel
    // (DrainBinkPlaneSnapshotsForPlume), so the harvest no longer needs to
    // block against the publish path's long observer holds.
    SyncDirectResourcesLocked(device, command_list, &changed_texture_upload_filter,
                              &changed_resource_filter, false, true);
  }
  if (!resource_filter.empty()) {
    PlumeDirectDebugLabelScope sync_scope(
        command_list, "RefiiRenderer::SyncMissingResources");
    SyncDirectResourcesLocked(device, command_list, &texture_upload_filter,
                              &resource_filter);
  }
  const FableDirectClock::time_point sync_t1 = FableDirectClock::now();
  if (!g_direct_renderer.current_frame_has_pix_video_draw) {
    PreflightDirectPrivateRectCopyActionsLocked(device, replay_commands);
  }
  const FableDirectClock::time_point post_sync_preflight_t1 =
      FableDirectClock::now();
  uint32_t pre_draw_surface_destination_commands = 0;
  if (!g_direct_renderer.current_frame_has_pix_video_draw &&
      packet_surface_destination_actions == 0 &&
      g_direct_renderer.packet_queue_replayed_surface_destination_actions == 0) {
    pre_draw_surface_destination_commands =
        RecordDirectSurfaceDestinationCopiesLocked(command_list, true);
    recorded_resolves += pre_draw_surface_destination_commands;
  }
  const FableDirectClock::time_point pre_draw_surface_destination_t1 =
      FableDirectClock::now();
  {
    char commands_label[160] = {};
    if (DirectDebugLabelsEnabled()) {
      std::snprintf(commands_label, sizeof(commands_label),
                    "RefiiRenderer::ReplaySubmittedCommands Frame %u Count %zu",
                    g_direct_renderer.current_frame_index,
                    replay_commands.size());
    }
    PlumeDirectDebugLabelScope commands_scope(command_list, commands_label);
    record_commands(replay_commands, true);
  }
  // recordsplit post=: everything between the replay loop's return and
  // record_t1 (hold snapshots, packet-queue action replay, graph counters).
  const FableDirectClock::time_point record_post_t0 = FableDirectClock::now();
  // Per-batch fallback snapshot: only while no clear-triggered (compose-
  // complete) snapshots are firing. Full-overwrite content (movies) never
  // clears the target, so it keeps the per-batch path; progressive composes
  // (title pages) get clear-gated holds and never present intermediates.
  const bool clear_gated_hold_active =
      g_direct_renderer.video_hold_clear_snapshot_frame != 0 &&
      g_direct_renderer.current_frame_index >=
          g_direct_renderer.video_hold_clear_snapshot_frame &&
      g_direct_renderer.current_frame_index -
              g_direct_renderer.video_hold_clear_snapshot_frame <=
          4u;
  if (g_direct_renderer.pending_video_hold_texture &&
      !clear_gated_hold_active) {
    RecordVideoPresentHoldSnapshotLocked(
        device, command_list, g_direct_renderer.pending_video_hold_texture,
        g_direct_renderer.pending_video_hold_target,
        g_direct_renderer.pending_video_hold_width,
        g_direct_renderer.pending_video_hold_height,
        g_direct_renderer.pending_video_hold_format);
  }
  g_direct_renderer.pending_video_hold_texture = nullptr;
  g_direct_renderer.pending_video_hold_target = 0;
  const uint32_t packet_queue_action_records =
      packet_queue_action_replay_active
          ? RecordDirectPacketQueueFrameActionsLocked(
                g_direct_renderer.device, command_list, output_framebuffer,
                output_depth_format, output_texture, present_framebuffer,
                g_direct_renderer.shader_format,
                g_direct_renderer.refii_packet_queue)
          : 0u;
  g_direct_renderer.render_graph_graph_owned_clear_commands = recorded_clears;
  g_direct_renderer.render_graph_graph_owned_clear_steps =
      recorded_clears != 0 ? 1u : 0u;
  g_direct_renderer.render_graph_graph_owned_draw_commands = recorded_draws;
  g_direct_renderer.render_graph_graph_owned_draw_steps =
      recorded_draws != 0 ? 1u : 0u;
  g_direct_renderer.render_graph_graph_owned_resolve_commands =
      recorded_resolves +
      g_direct_renderer.packet_queue_replayed_surface_destination_actions;
  g_direct_renderer.render_graph_graph_owned_resolve_steps =
      g_direct_renderer.render_graph_graph_owned_resolve_commands != 0 ? 1u
                                                                       : 0u;
  g_direct_renderer.render_graph_graph_owned_render_steps =
      g_direct_renderer.render_graph_graph_owned_clear_steps +
      g_direct_renderer.render_graph_graph_owned_draw_steps +
      g_direct_renderer.render_graph_graph_owned_resolve_steps;
  g_direct_renderer.render_graph_bridge_steps =
      (!replay_commands.empty() || packet_queue_action_records != 0) ? 1u
                                                                     : 0u;
  const size_t replay_bridge_commands =
      replay_commands.size() >
              recorded_clears + recorded_draws + recorded_resolves
          ? replay_commands.size() - recorded_clears - recorded_draws -
                recorded_resolves
          : 0u;
  g_direct_renderer.render_graph_bridge_commands = static_cast<uint32_t>(
      std::min<size_t>(
          replay_bridge_commands +
              g_direct_renderer.packet_queue_replayed_present_blit_actions,
          UINT32_MAX));
  const FableDirectClock::time_point record_t1 = FableDirectClock::now();
  g_direct_draw_phases.record_post_ms +=
      FableDirectElapsedMs(record_post_t0, record_t1);
  if ((attempted_draws != 0 || failed_draws != 0) &&
      g_direct_renderer.diagnostics.logged_command_record_summaries < 12u) {
    REXLOG_INFO(
        "Refii plume direct command record summary #{} frame={} attempted={} "
        "recorded={} failed={} clears={} resolves={} submitted={} "
        "staged_remaining={} private_fetch_identity={} private_copy_exact={}",
        ++g_direct_renderer.diagnostics.logged_command_record_summaries,
        g_direct_renderer.current_frame_index, attempted_draws, recorded_draws,
        failed_draws, recorded_clears, recorded_resolves,
        replay_commands.size(), staged_snapshot.render_command_count,
        g_direct_renderer.frame_private_fetch_identity_aliases,
        g_direct_renderer.frame_private_copy_exact_matches);
  }
  if (recorded_draws != 0) {
    g_direct_renderer.stats.draw_count = recorded_draws;
  }
  // Draw funnel ledger: cumulative counts at every pipeline stage — where
  // the game's draws vanish is read directly off one line (events = draw
  // events recorded observer-side, pushed = draw commands into staging,
  // published = draw commands into the submitted stream, attempted/recorded
  // = replay execution, dropped = staging retire/trim).
  {
    static uint64_t funnel_attempted_total = 0;
    static uint64_t funnel_recorded_total = 0;
    static uint64_t funnel_failed_total = 0;
    funnel_attempted_total += attempted_draws;
    funnel_recorded_total += recorded_draws;
    funnel_failed_total += failed_draws;
    if (g_direct_renderer.current_frame_index % 120u == 0) {
      REXLOG_INFO(
          "Refii direct draw funnel frame={} observer_draws={} events={} "
          "pushed={} published={} "
          "attempted_total={} recorded_total={} failed_total={} dropped={} "
          "consumed_state={}",
          g_direct_renderer.current_frame_index,
          g_direct_renderer.observer_draws_at_frame_top,
          staged_snapshot.draw_count,
          staged_snapshot.funnel_draw_commands_pushed,
          staged_snapshot.funnel_draw_commands_published,
          funnel_attempted_total, funnel_recorded_total, funnel_failed_total,
          staged_snapshot.dropped_render_commands,
          staged_snapshot.consumed_exact_state_commands);
      // Migration step 6 falsifier, on the same 120-frame cadence.
      FableDirectLogGtlIdentityShadow();
    }
  }
  g_direct_renderer.stats.attempted_draw_commands = attempted_draws;
  g_direct_renderer.stats.recorded_draw_commands = recorded_draws;
  g_direct_renderer.stats.failed_draw_commands = failed_draws;
  g_direct_renderer.stats.recorded_clear_commands = recorded_clears;
  g_direct_renderer.stats.recorded_resolve_commands = recorded_resolves;
  // §3 cutover-readiness metric. Cheap, so it rides the existing 120-frame
  // cadence rather than a slow-frame trigger.
  if (g_direct_renderer.current_frame_index % 120u == 0 &&
      g_direct_renderer.gtl_binding_shadow_slots != 0) {
    const uint64_t total = g_direct_renderer.gtl_binding_shadow_slots;
    // bink_override stays inside `decided` so the percentage remains
    // comparable with the runs that counted it as a divergence.
    const uint64_t decided =
        g_direct_renderer.gtl_binding_shadow_agree +
        g_direct_renderer.gtl_binding_shadow_bink_override +
        g_direct_renderer.gtl_binding_shadow_differ;
    REXLOG_INFO(
        "Refii GTL binding shadow frame={} slots={} agree={} bink_override={} "
        "differ={} "
        "maze_only={} stated_only={} neither={} agree_pct_of_decided={:.1f}",
        g_direct_renderer.current_frame_index, total,
        g_direct_renderer.gtl_binding_shadow_agree,
        g_direct_renderer.gtl_binding_shadow_bink_override,
        g_direct_renderer.gtl_binding_shadow_differ,
        g_direct_renderer.gtl_binding_shadow_maze_only,
        g_direct_renderer.gtl_binding_shadow_stated_only,
        g_direct_renderer.gtl_binding_shadow_neither,
        decided != 0
            ? 100.0 * static_cast<double>(
                          g_direct_renderer.gtl_binding_shadow_agree) /
                  static_cast<double>(decided)
            : 0.0);
  }
  const double command_total_ms = FableDirectElapsedMs(commands_t0, record_t1);
  // Captured then cleared here so the breakdown below reports THIS frame's
  // shadow cost and the next frame starts from zero.
  const double gtl_shadow_frame_ms = g_direct_renderer.gtl_shadow_ms_accum;
  g_direct_renderer.gtl_shadow_ms_accum = 0.0;
  if (command_total_ms >= 10.0 ||
      g_direct_renderer.current_frame_index % 120u == 0) {
    // Rate-limited by TIME, not a lifetime print cap: the old 512-print cap
    // went dark 90s into a menu sit (run 455), exactly when the late-run
    // degradation it exists to measure was happening.
    static std::chrono::steady_clock::time_point last_timing_log{};
    const auto timing_now = std::chrono::steady_clock::now();
    if (timing_now - last_timing_log >= std::chrono::milliseconds(1500)) {
      last_timing_log = timing_now;
      static uint32_t logged_direct_command_timing_breakdowns = 0;
      REXLOG_INFO(
          "Refii plume direct command timing #{} frame={} total_ms={:.3f} "
          "consume_ms={:.3f} summary_ms={:.3f} preflight_ms={:.3f} "
          "exact_title_preflight_ms={:.3f} "
          "exact_title_preflight_sync_ms={:.3f} "
          "command_interest_ms={:.3f} interest_collect_ms={:.3f} "
          "packet_interest_ms={:.3f} "
          "interest_ms={:.3f} sync_ms={:.3f} sync_calls={} "
          "post_sync_preflight_ms={:.3f} pre_record_gap_ms={:.3f} "
          "post_sync_ms={:.3f} surface_destination_ms={:.3f} "
          "record_ms={:.3f} gtl_shadow_ms={:.3f} submitted={} "
          "staged_remaining={} "
          "resource_interest={} texture_interest={} attempted={} recorded={} "
          "failed={} clears={} resolves={} uploads={} dropped={} "
          "consumed_exact_state={} "
          "pipeline_hits={} pipeline_misses={} private_fetch_identity={} "
          "private_copy_exact={} "
          "arena[allocs={} kb={} chunks={}] upload_cache={} "
          "drawphase[copy={:.1f} copies={} copy_kb={} pre={:.1f} sync={:.1f} "
          "pipe={:.1f} interior={:.1f} alias={:.1f} submit={:.1f}] "
          "aliassplit[feedback={:.1f} proxy={:.1f} hazard={:.1f} dest={:.1f} "
          "destscan={}/{} scanned={} relevant={} fbmemo={}/{}] "
          "syncsplit[flush={:.1f} prewarm={:.1f} header={:.1f} bink={:.1f}] "
          "interiorsplit[reval={:.1f} streams={:.1f} vbind={:.1f} "
          "ibind={:.1f} retry={:.1f} pipefix={:.1f}] "
          "vbindsplit[layout={:.1f} ready={:.1f} upload={:.1f} "
          "setvb={:.1f} upload_hits={} upload_misses={}] "
          "submitsplit[fb={:.1f} tex={:.1f} const={:.1f} bind={:.1f} "
          "log={:.1f} vp={:.1f} label={:.1f} draw={:.1f} post={:.1f} "
          "const_reuse={} const_uploads={}] "
          "recordsplit[gate={:.1f} latch={:.1f} clear={:.1f} "
          "resolve={:.1f} label={:.1f} post={:.1f} draw={:.1f} "
          "state={:.1f} iter={:.1f} clearmemo={}/{} "
          "clears_coalesced={} coalesce_wipes={} coalesce_evict={}] "
          "clearsplit[ensure={:.1f} resolve={:.1f} sync={:.1f} vk={:.1f} "
          "retry={}/{}/{}] "
          "sdsplit[calls={} snap={:.1f} interest={:.1f} sync={:.1f} "
          "catchup={:.1f} record={:.1f}] "
          "reslookup[calls={} fasthit={} maphit={} memohit={} scans={} "
          "scanhit={} neghit={} "
          "entries={} ambig={}/{}] "
          "resolvesplit[find={:.1f} classify={:.1f} fb={:.1f} draw={:.1f} "
          "execs={}] pipememo[hit={} miss={} bypass={}] rectvb[hit={} "
          "create={}]",
          ++logged_direct_command_timing_breakdowns,
          g_direct_renderer.current_frame_index, command_total_ms,
          FableDirectElapsedMs(commands_t0, consume_t1),
          FableDirectElapsedMs(consume_t1, summary_t1),
          FableDirectElapsedMs(summary_t1, preflight_t1),
          FableDirectElapsedMs(exact_title_preflight_t0,
                               exact_title_preflight_collect_t1),
          FableDirectElapsedMs(exact_title_preflight_collect_t1,
                               exact_title_preflight_sync_t1),
          FableDirectElapsedMs(exact_title_preflight_sync_t1, interest_t1),
          FableDirectElapsedMs(preflight_t1, interest_t1),
          FableDirectElapsedMs(preflight_t1, exact_title_preflight_t0),
          FableDirectElapsedMs(summary_t1, interest_t1),
          FableDirectElapsedMs(interest_t1, sync_t1),
          g_direct_sync_calls_this_compose,
          FableDirectElapsedMs(sync_t1, post_sync_preflight_t1),
          0.0,
          FableDirectElapsedMs(sync_t1, post_sync_preflight_t1),
          FableDirectElapsedMs(post_sync_preflight_t1,
                               pre_draw_surface_destination_t1),
          FableDirectElapsedMs(pre_draw_surface_destination_t1, record_t1),
          gtl_shadow_frame_ms,
          replay_commands.size(), staged_snapshot.render_command_count,
          resource_filter.size(), texture_upload_filter.size(),
          attempted_draws, recorded_draws, failed_draws, recorded_clears,
          recorded_resolves, g_direct_renderer.resource_uploads,
          staged_snapshot.dropped_render_commands,
          staged_snapshot.consumed_exact_state_commands,
          g_direct_renderer.stats.pipeline_cache_hits,
          g_direct_renderer.stats.pipeline_cache_misses,
          g_direct_renderer.frame_private_fetch_identity_aliases,
          g_direct_renderer.frame_private_copy_exact_matches,
          g_direct_renderer.constant_arena_allocs_this_frame,
          g_direct_renderer.constant_arena_bytes_this_frame / 1024u,
          g_direct_renderer.constant_arena_chunks_created,
          g_direct_renderer.cached_upload_buffer_count,
          g_direct_draw_phases.copy_ms,
          static_cast<unsigned long long>(g_direct_draw_phases.copy_count),
          static_cast<unsigned long long>(g_direct_draw_phases.copy_bytes /
                                          1024u),
          g_direct_draw_phases.preamble_ms, g_direct_draw_phases.title_sync_ms,
          g_direct_draw_phases.pipeline_ms, g_direct_draw_phases.interior_ms,
          g_direct_draw_phases.alias_ms, g_direct_draw_phases.submit_ms,
          g_direct_draw_phases.alias_feedback_ms,
          g_direct_draw_phases.alias_proxy_ms,
          g_direct_draw_phases.alias_hazard_ms,
          g_direct_draw_phases.alias_destination_ms,
          g_direct_draw_phases.alias_destination_scan_runs,
          g_direct_draw_phases.alias_destination_scan_skips,
          g_direct_draw_phases.alias_destination_scan_scanned,
          g_direct_draw_phases.alias_destination_scan_relevant,
          g_direct_draw_phases.alias_feedback_memo_hits,
          g_direct_draw_phases.alias_feedback_memo_runs,
          g_direct_draw_phases.sync_flush_ms,
          g_direct_draw_phases.sync_prewarm_ms,
          g_direct_draw_phases.sync_header_ms,
          g_direct_draw_phases.sync_bink_ms,
          g_direct_draw_phases.interior_reval_ms,
          g_direct_draw_phases.interior_streams_ms,
          g_direct_draw_phases.interior_vbind_ms,
          g_direct_draw_phases.interior_ibind_ms,
          g_direct_draw_phases.interior_retry_ms,
          g_direct_draw_phases.interior_pipefix_ms,
          g_direct_draw_phases.vbind_layout_ms,
          g_direct_draw_phases.vbind_ready_ms,
          g_direct_draw_phases.vbind_upload_ms,
          g_direct_draw_phases.vbind_setvb_ms,
          g_direct_draw_phases.vbind_upload_stored_hash,
          g_direct_draw_phases.vbind_upload_lazy_hash,
          g_direct_draw_phases.submit_fb_ms,
          g_direct_draw_phases.submit_tex_ms,
          g_direct_draw_phases.submit_const_ms,
          g_direct_draw_phases.submit_bind_ms,
          g_direct_draw_phases.submit_log_ms,
          g_direct_draw_phases.submit_vp_ms,
          g_direct_draw_phases.submit_label_ms,
          g_direct_draw_phases.submit_draw_ms,
          g_direct_draw_phases.submit_post_ms,
          g_direct_draw_phases.submit_const_bank_reuses,
          g_direct_draw_phases.submit_const_bank_uploads,
          g_direct_draw_phases.record_gate_ms,
          g_direct_draw_phases.record_latch_ms,
          g_direct_draw_phases.record_clear_ms,
          g_direct_draw_phases.record_resolve_ms,
          g_direct_draw_phases.record_label_ms,
          g_direct_draw_phases.record_post_ms,
          g_direct_draw_phases.record_draw_ms,
          g_direct_draw_phases.record_state_ms,
          g_direct_draw_phases.record_iter_ms,
          g_direct_draw_phases.record_clear_memo_hits,
          g_direct_draw_phases.record_clear_memo_runs,
          g_direct_draw_phases.record_clears_coalesced,
          g_direct_draw_phases.record_coalesce_wipes,
          g_direct_draw_phases.record_coalesce_evictions,
          g_direct_draw_phases.clear_ensure_ms,
          g_direct_draw_phases.clear_resolve_ms,
          g_direct_draw_phases.clear_sync_ms,
          g_direct_draw_phases.clear_vk_ms,
          g_direct_draw_phases.clear_retry_runs,
          g_direct_draw_phases.clear_retry_skips,
          g_direct_draw_phases.clear_retry_invalidated,
          g_direct_surface_destination_split.calls,
          g_direct_surface_destination_split.snapshot_ms,
          g_direct_surface_destination_split.interest_ms,
          g_direct_surface_destination_split.sync_ms,
          g_direct_surface_destination_split.catchup_ms,
          g_direct_surface_destination_split.record_ms,
          g_direct_resource_lookup_census.calls,
          g_direct_resource_lookup_census.fast_hits,
          g_direct_resource_lookup_census.map_hits,
          g_direct_resource_lookup_census.memo_hits,
          g_direct_resource_lookup_census.scans,
          g_direct_resource_lookup_census.scan_hits,
          g_direct_resource_lookup_census.negative_hits,
          g_direct_resource_lookup_census.entries_scanned,
          g_direct_resource_lookup_census.ambiguous_scans,
          g_direct_resource_lookup_census.ambiguity_samples,
          g_direct_draw_phases.resolve_find_ms,
          g_direct_draw_phases.resolve_classify_ms,
          g_direct_draw_phases.resolve_fb_ms,
          g_direct_draw_phases.resolve_draw_ms,
          g_direct_draw_phases.resolve_execs,
          g_direct_draw_phases.pipeline_memo_hits,
          g_direct_draw_phases.pipeline_memo_misses,
          g_direct_draw_phases.pipeline_memo_bypass,
          g_direct_draw_phases.rect_vb_cache_hits,
          g_direct_draw_phases.rect_vb_cache_creates);
    }
  }
}

// Stale-resource TTL, in compose frames. Shared by the eviction predicate and
// the skip census below — they used to carry independent copies of the same
// rules, which is how the depth-stencil leak stayed invisible: the census
// reported `skip[depth=]` from its own duplicated test, so it agreed with the
// broken predicate instead of grading it.

// A depth surface currently serving as a framebuffer attachment. The ONLY
// depth exemption the sweep gets — see the full note in the eviction predicate
// for why the previous blanket `texture_depth_target` test had to go.
bool IsLiveBoundDepthTargetLocked(uint32_t guest_address,
                                  const PlumeTrackedResource& entry,
                                  uint64_t sweep_frame) {
  return entry.kind == FableGuestResourceKind::kDepthSurface &&
         guest_address == g_direct_renderer.last_bound_depth_target &&
         sweep_frame >= g_direct_renderer.last_bound_depth_frame &&
         sweep_frame - g_direct_renderer.last_bound_depth_frame <=
             kStaleResourceFrames;
}

#endif

}  // namespace refii::renderer
