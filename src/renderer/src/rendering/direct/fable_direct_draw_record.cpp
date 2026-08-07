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

// `command` is the caller's per-replay scratch copy and is normalized IN
// PLACE (carried framebuffer targets, title viewport scaling, ring-inline
// backfills). It used to be taken const and copied into a second 12.6KB
// local per draw — a default-construct plus a full copy (17 inline-buffer
// vectors) on EVERY recorded draw, measured as the bulk of drawphase[pre].
bool RecordDirectDrawCommandLocked(plume::RenderDevice* device,
                                   plume::RenderCommandList* command_list,
                                   plume::RenderFramebuffer* output_framebuffer,
                                   plume::RenderFormat output_depth_format,
                                   FableDirectRenderCommand& command,
                                   bool submitted_command_source) {
  const FableDirectClock::time_point draw_record_t0 =
      FableDirectClock::now();
  if (!device || !command_list || !output_framebuffer) {
    LogFailedDirectDrawLocked("missing_plume_context", command);
    return false;
  }
  // GTL shadow validation: pair this direct draw with an unmatched packet
  // from the same frame (loose key: shader pair) — the per-frame match rate
  // gauges packet-stream completeness ahead of the §3.4 executor cutover.
  // PROOF-ONLY, and it was running on every recorded draw in the default
  // path. try_match scans every packet of both shadow generations per draw and
  // compares render states and whole shader-constant banks dword by dword, so
  // it is O(draws x packets x dwords) — at 168 draws against 168 packets that
  // is the shape of the stall run 129 measured (57 frames >=10ms costing 18%
  // of wall time, record_ms 91% of it, scaling ~6ms per draw).
  //
  // renderer_deep_observer_hooks exists for precisely this ("proof-only
  // observer hooks require renderer_deep_observer_hooks", cvars.cpp) and
  // defaults OFF, so the default runtime path should never have paid for it.
  // gtl_shadow_ms in the command timing line reports what it costs when on.
  const FableDirectClock::time_point gtl_shadow_t0 = FableDirectClock::now();
  if (RendererDeepObserverHooksEnabled()) {
    ++g_direct_renderer.gtl_shadow_direct_draws;
    bool shader_pair_present = false;
    bool matched_generation = false;
    const auto try_match = [&](const std::vector<refii::gtl::DrawPacket>&
                                   generation_packets,
                               std::vector<bool>& generation_matched) {
      for (size_t shadow_index = 0; shadow_index < generation_packets.size();
           ++shadow_index) {
        const refii::gtl::DrawPacket& shadow_packet =
            generation_packets[shadow_index];
        if (shadow_packet.vertex_shader != command.resources.vertex_shader ||
            shadow_packet.pixel_shader != command.resources.pixel_shader) {
          continue;
        }
        // Use the executor's complete identity rule here as well. Title
        // panels commonly share a shader pair and texture slot 0 while
        // advancing slot 13, and repeated meshes advance stream pointers.
        // The old slot-0-only shadow pairing could therefore manufacture a
        // descending packet-id sequence and arm a false order capture even
        // though authority/reordering paired the commands correctly.
        if (!GtlPacketIdentityAgreesWithCommandLocked(command,
                                                      shadow_packet)) {
          continue;
        }
        shader_pair_present = true;
        if (!generation_matched[shadow_index]) {
          generation_matched[shadow_index] = true;
          matched_generation = true;
          // Order flip gate: record the matched packet_id in recording
          // order. Current generation only — prev-generation ids are older
          // by construction and would read as false inversions.
          if (!IsGtlPreDispatchBeginPacket(shadow_packet) &&
              &generation_packets == &g_direct_renderer.gtl_shadow_packets &&
              g_direct_renderer.gtl_order_sequence.size() < 8192u) {
            g_direct_renderer.gtl_order_sequence.push_back(
                shadow_packet.packet_id);
          }
          // State-agreement validation (§3.4 flip gate): the packet's
          // captured render states must reproduce the observer command's
          // pipeline states slot-for-slot — agreement proves executor-built
          // pipelines would match today's observer-built ones.
          uint32_t diverged = 0;
          for (uint32_t rs_slot = 0;
               rs_slot < refii::gtl::kMaxRenderStates &&
               rs_slot < command.pipeline.render_states.size();
               ++rs_slot) {
            if ((shadow_packet.render_state_valid_mask & (1u << rs_slot)) ==
                0u) {
              continue;
            }
            if (shadow_packet.render_states[rs_slot] !=
                command.pipeline.render_states[rs_slot]) {
              ++diverged;
              static uint32_t logged_gtl_state_divergences = 0;
              if (logged_gtl_state_divergences < 24u) {
                REXLOG_INFO(
                    "Refii GTL state divergence #{} slot={} packet=0x{:08X} "
                    "command=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
                    ++logged_gtl_state_divergences, rs_slot,
                    shadow_packet.render_states[rs_slot],
                    command.pipeline.render_states[rs_slot],
                    command.resources.vertex_shader,
                    command.resources.pixel_shader);
              }
            }
          }
          ++g_direct_renderer.gtl_state_compared;
          g_direct_renderer.gtl_state_diverged += diverged != 0 ? 1u : 0u;
          // Constants flip gate: the packet's raw at-draw banks must
          // reproduce the observer command's captured constants. The
          // command's values are sanitized post-snapshot, so compare only
          // dwords where the raw packet value is nonzero (sanitize zeroes
          // specific dwords; a nonzero mismatch is a real divergence).
          if (!shadow_packet.vs_float_constants.empty() ||
              !shadow_packet.ps_float_constants.empty()) {
            uint32_t const_diverged = 0;
            const auto compare_bank =
                [&](const std::vector<uint32_t>& packet_bank,
                    const std::array<uint32_t,
                                     kRendererShaderFloatConstantDwords>&
                        command_bank) {
                  const size_t dwords =
                      std::min(packet_bank.size(), command_bank.size());
                  for (size_t dword_index = 0; dword_index < dwords;
                       ++dword_index) {
                    if (packet_bank[dword_index] != 0 &&
                        command_bank[dword_index] != 0 &&
                        packet_bank[dword_index] !=
                            command_bank[dword_index]) {
                      ++const_diverged;
                    }
                  }
                };
            compare_bank(shadow_packet.vs_float_constants,
                         command.constants.vertex_float);
            compare_bank(shadow_packet.ps_float_constants,
                         command.constants.pixel_float);
            ++g_direct_renderer.gtl_const_compared;
            g_direct_renderer.gtl_const_diverged +=
                const_diverged != 0 ? 1u : 0u;
            static uint32_t logged_gtl_const_divergences = 0;
            if (const_diverged != 0 && logged_gtl_const_divergences < 16u) {
              REXLOG_INFO(
                  "Refii GTL constants divergence #{} dwords={} vs=0x{:08X} "
                  "ps=0x{:08X}",
                  ++logged_gtl_const_divergences, const_diverged,
                  command.resources.vertex_shader,
                  command.resources.pixel_shader);
            }
          }
          // Viewport/scissor flip gate. RUN 094 VERDICT: 100% divergence is
          // EXPECTED and understood — the packet is the raw guest domain
          // (1280x720, reverse-Z 1..0, unclamped guest scissor) while the
          // command is host-effective (scaler-transformed to output size,
          // 0..1 depth, target-derived scissor). vp/sc authority is NOT a
          // straight assign: the executor's materialization must apply the
          // scaler transform (RendererScalerSnapshot source rect -> output),
          // the SetScissorRect twin's viewport clamp, and reverse-Z. The
          // counters stay as a decode-breakage canary (garbage shapes would
          // shift the divergence signature), not a cutover gate.
          if (command.viewport.valid &&
              (shadow_packet.viewport[2] != 0 ||
               shadow_packet.viewport[3] != 0)) {
            const bool viewport_diverged =
                static_cast<float>(shadow_packet.viewport[0]) !=
                    command.viewport.x ||
                static_cast<float>(shadow_packet.viewport[1]) !=
                    command.viewport.y ||
                static_cast<float>(shadow_packet.viewport[2]) !=
                    command.viewport.width ||
                static_cast<float>(shadow_packet.viewport[3]) !=
                    command.viewport.height ||
                std::abs(std::bit_cast<float>(shadow_packet.viewport[4]) -
                         command.viewport.min_depth) > 1.0e-4f ||
                std::abs(std::bit_cast<float>(shadow_packet.viewport[5]) -
                         command.viewport.max_depth) > 1.0e-4f;
            ++g_direct_renderer.gtl_viewport_compared;
            g_direct_renderer.gtl_viewport_diverged +=
                viewport_diverged ? 1u : 0u;
            static uint32_t logged_gtl_viewport_divergences = 0;
            if (viewport_diverged && logged_gtl_viewport_divergences < 16u) {
              REXLOG_INFO(
                  "Refii GTL viewport divergence #{} packet={}x{}+{}+{} "
                  "z={}..{} command={}x{}+{}+{} z={}..{}",
                  ++logged_gtl_viewport_divergences,
                  shadow_packet.viewport[2], shadow_packet.viewport[3],
                  shadow_packet.viewport[0], shadow_packet.viewport[1],
                  std::bit_cast<float>(shadow_packet.viewport[4]),
                  std::bit_cast<float>(shadow_packet.viewport[5]),
                  command.viewport.width, command.viewport.height,
                  command.viewport.x, command.viewport.y,
                  command.viewport.min_depth, command.viewport.max_depth);
            }
          }
          if (command.scissor.valid &&
              (shadow_packet.scissor[2] != 0 ||
               shadow_packet.scissor[3] != 0)) {
            const bool scissor_diverged =
                static_cast<int32_t>(shadow_packet.scissor[0]) !=
                    command.scissor.left ||
                static_cast<int32_t>(shadow_packet.scissor[1]) !=
                    command.scissor.top ||
                static_cast<int32_t>(shadow_packet.scissor[2]) !=
                    command.scissor.right ||
                static_cast<int32_t>(shadow_packet.scissor[3]) !=
                    command.scissor.bottom;
            ++g_direct_renderer.gtl_scissor_compared;
            g_direct_renderer.gtl_scissor_diverged +=
                scissor_diverged ? 1u : 0u;
            static uint32_t logged_gtl_scissor_divergences = 0;
            if (scissor_diverged && logged_gtl_scissor_divergences < 16u) {
              REXLOG_INFO(
                  "Refii GTL scissor divergence #{} packet={},{}..{},{} "
                  "command={},{}..{},{}",
                  ++logged_gtl_scissor_divergences,
                  static_cast<int32_t>(shadow_packet.scissor[0]),
                  static_cast<int32_t>(shadow_packet.scissor[1]),
                  static_cast<int32_t>(shadow_packet.scissor[2]),
                  static_cast<int32_t>(shadow_packet.scissor[3]),
                  command.scissor.left, command.scissor.top,
                  command.scissor.right, command.scissor.bottom);
            }
          }
          return;
        }
      }
    };
    try_match(g_direct_renderer.gtl_shadow_packets,
              g_direct_renderer.gtl_shadow_matched);
    if (!matched_generation) {
      try_match(g_direct_renderer.gtl_shadow_packets_prev,
                g_direct_renderer.gtl_shadow_matched_prev);
    }
    if (!shader_pair_present) {
      ++g_direct_renderer.gtl_shadow_direct_unmatched;
      static uint32_t logged_gtl_missing_pairs = 0;
      if (logged_gtl_missing_pairs < 24u) {
        REXLOG_INFO(
            "Refii GTL shadow missing pair #{} vs=0x{:08X} ps=0x{:08X} "
            "source={} private_rect={}",
            ++logged_gtl_missing_pairs, command.resources.vertex_shader,
            command.resources.pixel_shader,
            static_cast<uint32_t>(command.draw.source),
            command.draw.private_texture_rectangle ? 1 : 0);
      }
    }
  }
  g_direct_renderer.gtl_shadow_ms_accum +=
      FableDirectElapsedMs(gtl_shadow_t0, FableDirectClock::now());
  DirectFramebufferBinding framebuffer = ResolveDirectFramebufferLocked(
      device, output_framebuffer, output_depth_format, command);
  if (!framebuffer.framebuffer) {
    std::unordered_set<uint32_t> resource_filter;
    AddDirectFramebufferResourceInterest(command, resource_filter);
    if (!resource_filter.empty()) {
      SyncDirectResourcesLocked(device, command_list, nullptr,
                                &resource_filter);
      framebuffer = ResolveDirectFramebufferLocked(
          device, output_framebuffer, output_depth_format, command);
    }
  }
  bool carried_framebuffer_state = false;
  if (!framebuffer.framebuffer) {
    const bool command_targets_empty =
        std::all_of(command.resources.render_targets.begin(),
                    command.resources.render_targets.end(),
                    [](uint32_t target) { return target == 0; }) &&
        command.resources.depth_stencil == 0;
    const bool draw_has_real_inputs =
        command.resources.vertex_declaration != 0 &&
        command.resources.vertex_shader != 0 &&
        command.resources.pixel_shader != 0 &&
        (command.resources.streams[0].buffer != 0 ||
         command.inline_vertex_buffers[0].ready());
    if (command_targets_empty && draw_has_real_inputs &&
        command.draw.source != RendererDrawEventSource::kPixVideo) {
      const FableDirectStateSnapshot state_snapshot =
          GetFableDirectStateSnapshot();
      const bool state_has_framebuffer =
          std::any_of(state_snapshot.targets.color.begin(),
                      state_snapshot.targets.color.end(),
                      [](uint32_t target) { return target != 0; }) ||
          state_snapshot.targets.depth != 0;
      if (state_has_framebuffer) {
        // Carry the state's targets directly on the caller's scratch command.
        // The originals are all zero by the command_targets_empty gate above,
        // so the failure path restores them by zeroing.
        command.resources.render_targets = state_snapshot.targets.color;
        command.resources.depth_stencil = state_snapshot.targets.depth;
        std::unordered_set<uint32_t> resource_filter;
        AddDirectFramebufferResourceInterest(command, resource_filter);
        if (!resource_filter.empty()) {
          SyncDirectResourcesLocked(device, command_list, nullptr,
                                    &resource_filter);
        }
        framebuffer = ResolveDirectFramebufferLocked(
            device, output_framebuffer, output_depth_format, command);
        carried_framebuffer_state = framebuffer.framebuffer != nullptr;
        if (!carried_framebuffer_state) {
          command.resources.render_targets = {};
          command.resources.depth_stencil = 0;
        }
        if (carried_framebuffer_state) {
          static uint32_t logged_carried_framebuffer_states = 0;
          if (logged_carried_framebuffer_states < 32u) {
            REXLOG_INFO(
                "Refii plume exact carried framebuffer state #{} frame={} "
                "serial={} draw={} source={} rt0=0x{:08X} ds=0x{:08X} "
                "state_rt0=0x{:08X} state_ds=0x{:08X} "
                "rt_root=0x{:08X}/{} depth_root=0x{:08X}/{} "
                "wrapper=0x{:08X}/{} context=0x{:016X} vs=0x{:08X} "
                "ps=0x{:08X}",
                ++logged_carried_framebuffer_states,
                g_direct_renderer.current_frame_index,
                static_cast<unsigned long long>(command.serial),
                static_cast<unsigned long long>(command.draw_serial),
                static_cast<uint32_t>(command.draw.source),
                // Pre-carry targets are all zero by the command_targets_empty
                // gate; the command now holds the carried values in place.
                0u, 0u,
                command.resources.render_targets[0],
                command.resources.depth_stencil,
                state_snapshot.target_roots.color[0].symbol_address,
                static_cast<unsigned long long>(
                    state_snapshot.target_roots.color[0].call_serial),
                state_snapshot.target_roots.depth.symbol_address,
                static_cast<unsigned long long>(
                    state_snapshot.target_roots.depth.call_serial),
                command.draw.private_texture_wrapper_symbol_address,
                static_cast<unsigned long long>(
                    command.draw.private_texture_wrapper_call_serial),
                static_cast<unsigned long long>(
                    command.draw.private_texture_wrapper_context_key),
                command.resources.vertex_shader, command.resources.pixel_shader);
          }
        }
      }
    }
  }
  if (!framebuffer.framebuffer) {
    LogFailedDirectDrawLocked("missing_framebuffer", command);
    return false;
  }
  ScaleFableTitleDrawStateToFramebuffer(command, framebuffer);
  FableDirectRenderCommand& draw_command = command;
  EnsureDirectKnownShaderResourcesForCommandLocked(draw_command);
  if (!EnsurePixVideoPlanesReadyForDrawLocked(device, command_list,
                                              draw_command)) {
    return true;
  }
  if (!EnsureBinkWrapperPlanesReadyForDrawLocked(device, command_list,
                                                 draw_command)) {
    return true;
  }
  MaybeBeginRenderDocDrawTargetCaptureLocked(draw_command, &framebuffer);
  const FableDirectClock::time_point video_t1 = FableDirectClock::now();
  const uint32_t pixel_texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(draw_command);
  const bool exact_synthetic_private_rect_copy =
      DirectCommandHasExactSyntheticPrivateRectCopyInputsLocked(draw_command);
  const bool exact_title_billboard_atlas_ready =
      DirectTitleBillboardCommandHasAuthoritativeAtlasSampleLocked(draw_command);
  std::unordered_map<uint64_t, uint32_t> private_rect_feedback_aliases_for_draw;
  auto private_rect_feedback_alias_draw_key = [&](uint32_t slot) {
    uint64_t key = 1469598103934665603ull;
    auto mix = [&](uint64_t value) {
      key ^= value;
      key *= 1099511628211ull;
    };

    const uint32_t guest =
        slot < draw_command.resources.textures.size()
            ? draw_command.resources.textures[slot]
            : 0u;
    PlumeTrackedResource* texture = guest != 0 ? FindDirectResourceLocked(guest)
                                               : nullptr;
    mix(guest);
    if (texture) {
      mix(texture->guest_address);
      mix(texture->parent_address);
      mix(PlumeGuestTextureBaseAddress(*texture));
      mix(PlumeGuestTextureWidth(*texture));
      mix(PlumeGuestTextureHeight(*texture));
      mix(static_cast<uint32_t>(PlumeGuestTextureFormat(*texture)));
    }
    mix(FableDirectTextureFetchKey(
        DirectCommandTextureFetchDwords(draw_command, slot)));
    mix(draw_command.resources.render_targets[0]);
    mix(draw_command.resources.depth_stencil);
    mix(draw_command.resources.vertex_shader);
    mix(draw_command.resources.pixel_shader);
    mix(draw_command.draw.private_texture_rectangle ? 1u : 0u);
    mix(draw_command.draw.private_texture_corners_valid ? 1u : 0u);
    mix(draw_command.draw.private_texture_wrapper_source_texture);
    mix(draw_command.draw.private_texture_wrapper_source_exact ? 1u : 0u);
    return key;
  };
  auto remember_existing_private_feedback_alias_for_slot =
      [&](uint32_t slot) -> bool {
    const uint64_t draw_alias_key =
        private_rect_feedback_alias_draw_key(slot);
    const auto alias_it =
        private_rect_feedback_aliases_for_draw.find(draw_alias_key);
    if (alias_it != private_rect_feedback_aliases_for_draw.end()) {
      if (PlumeTrackedResource* alias =
              FindDirectResourceLocked(alias_it->second)) {
        return true;
      }
    }

    if (slot < draw_command.resources.textures.size()) {
      const uint32_t guest = draw_command.resources.textures[slot];
      PlumeTrackedResource* source =
          guest != 0 ? FindDirectResourceLocked(guest) : nullptr;
      PlumeTrackedResource* target =
          FindDirectResourceLocked(draw_command.resources.render_targets[0]);
      if (source && target && IsImageResource(target->kind) &&
          PlumeGuestImageReady(*target) &&
          DirectCommandUsesFablePrivateTexturePath(draw_command, slot, source) &&
          DirectCommandBindsResourceAsTargetLocked(draw_command, *target)) {
        if (PlumeTrackedResource* linked_alias =
                ResolveDirectRegisteredFeedbackSurfaceLinkSampleLocked(
                    draw_command, slot, *source, *target)) {
          private_rect_feedback_aliases_for_draw[draw_alias_key] =
              linked_alias->guest_address;
          return true;
        }
        const uint64_t target_visible_serial =
            FableTrackedResourceVisibleContentSerial(*target);
        auto validate_feedback_alias = [&](uint32_t alias_address)
            -> PlumeTrackedResource* {
          PlumeTrackedResource* alias =
              alias_address != 0 ? FindDirectResourceLocked(alias_address)
                                 : nullptr;
          if (!alias || alias == source || alias->guest_address == guest ||
              alias->guest_address == source->guest_address ||
              alias->guest_address == target->guest_address ||
              !IsImageResource(alias->kind) || !PlumeGuestImageReady(*alias) ||
              !FableTrackedResourceHasSampleableContent(*alias) ||
              FableTrackedResourceIsBlankUploadedSampleProxy(*alias) ||
              DirectCommandBindsResourceAsTargetLocked(draw_command, *alias) ||
              !DirectPrivateFeedbackAliasCanSampleTargetLocked(*alias, *source,
                                                               *target) ||
              target_visible_serial == 0) {
            return nullptr;
          }
          uint64_t alias_target_serial = 0;
          const auto copied_it =
              g_direct_renderer.private_texture_feedback_alias_target_serial
                  .find(alias->guest_address);
          if (copied_it !=
              g_direct_renderer.private_texture_feedback_alias_target_serial
                  .end()) {
            alias_target_serial = copied_it->second;
          }
          return alias_target_serial == target_visible_serial ? alias : nullptr;
        };
        const uint64_t exact_alias_key =
            DirectPrivateFeedbackAliasKey(guest, *source, *target);
        const uint64_t target_identity_alias_key =
            DirectPrivateFeedbackAliasTargetIdentityKey(guest, *source, *target);
        auto exact_feedback_action_allows_cache_reuse =
            [&](const PlumeTrackedResource& alias,
                const char* cache_source) -> bool {
          DirectPrivateTextureCopyActionMatch action_match =
              DirectPrivateTextureCopyActionMatch::kNone;
          const FablePrivateTextureCopyAction* action =
              SelectDirectExactPrivateTextureCopyActionForCommandSlotLocked(
                  draw_command, slot, source, &action_match);
          const bool exact_action =
              action &&
              DirectPrivateTextureCopyActionMatchIsExactRegistered(action_match);
          const bool action_targets_bound_target =
              exact_action &&
              (DirectResourceAddressMatchesLocked(action->target_surface,
                                                  target->guest_address) ||
               DirectResourceAddressMatchesLocked(action->target_surface,
                                                  draw_command.resources
                                                      .render_targets[0]) ||
               DirectResourceAddressMatchesLocked(action->render_target,
                                                  target->guest_address) ||
               DirectResourceAddressMatchesLocked(action->render_target,
                                                  draw_command.resources
                                                      .render_targets[0]));
          const bool action_backing_matches_alias =
              action_targets_bound_target &&
              (action->backing_texture == 0 ||
               DirectResourceAddressMatchesLocked(action->backing_texture,
                                                  alias.guest_address));
          if (action_backing_matches_alias) {
            return true;
          }

          static uint32_t logged_rejected_feedback_cache_reuses = 0;
          if (logged_rejected_feedback_cache_reuses < 32u) {
            REXLOG_INFO(
                "Refii plume rejected private feedback cache reuse #{} "
                "frame={} source={} slot={} guest=0x{:08X} "
                "tracked=0x{:08X}/{}x{} target=0x{:08X}/{}x{} "
                "alias=0x{:08X}/{}x{} exact_action={} match={} "
                "action_target=0x{:08X} action_rt=0x{:08X} "
                "action_backing=0x{:08X} rt=0x{:08X} vs=0x{:08X} "
                "ps=0x{:08X}",
                ++logged_rejected_feedback_cache_reuses,
                g_direct_renderer.current_frame_index, cache_source, slot,
                guest, source->guest_address, PlumeGuestTextureWidth(*source),
                PlumeGuestTextureHeight(*source), target->guest_address,
                PlumeGuestTextureWidth(*target), PlumeGuestTextureHeight(*target),
                alias.guest_address, PlumeGuestTextureWidth(alias),
                PlumeGuestTextureHeight(alias), exact_action ? 1 : 0,
                static_cast<uint32_t>(action_match),
                action ? action->target_surface : 0u,
                action ? action->render_target : 0u,
                action ? action->backing_texture : 0u,
                draw_command.resources.render_targets[0],
                draw_command.resources.vertex_shader,
                draw_command.resources.pixel_shader);
          }
          return false;
        };
        PlumeTrackedResource* alias = nullptr;
        if (const auto exact_alias_it =
                g_direct_renderer.private_texture_feedback_aliases.find(
                    exact_alias_key);
            exact_alias_it !=
            g_direct_renderer.private_texture_feedback_aliases.end()) {
          alias = validate_feedback_alias(exact_alias_it->second);
        }
        if (!alias) {
          if (const auto target_identity_alias_it =
                  g_direct_renderer
                      .private_texture_feedback_aliases_by_target_identity
                      .find(target_identity_alias_key);
              target_identity_alias_it !=
              g_direct_renderer
                  .private_texture_feedback_aliases_by_target_identity.end()) {
            PlumeTrackedResource* identity_alias =
                validate_feedback_alias(target_identity_alias_it->second);
            if (identity_alias &&
                exact_feedback_action_allows_cache_reuse(*identity_alias,
                                                         "target-identity")) {
              alias = identity_alias;
            }
          }
        }
        if (alias) {
          RememberDirectPrivateFeedbackAliasKeyLocked(exact_alias_key,
                                                      alias->guest_address);
          RememberDirectPrivateFeedbackAliasTargetIdentityKeyLocked(
              target_identity_alias_key, alias->guest_address);
          RememberDirectPrivateTextureSiblingAliasAddressLocked(guest, *alias);
          RememberDirectPrivateTextureSiblingAliasLocked(*source, *alias);
          RememberDirectPrivateTextureAliasIndexLocked(
              guest, PlumeGuestTextureBaseAddress(*source),
              HasTrackedFetchDwords(DirectCommandTextureFetchDwords(
                  draw_command, slot))
                  ? DirectCommandTextureFetchDwords(draw_command, slot)
                  : PlumeGuestTextureFetchDwords(*source),
              *alias);
          RememberDirectPrivateFeedbackSurfaceLinkLocked(draw_command, slot,
                                                        *source, *target,
                                                        *alias);
          private_rect_feedback_aliases_for_draw[draw_alias_key] =
              alias->guest_address;
          if (g_direct_renderer
                  .diagnostics.logged_private_rect_feedback_registered_reuses < 32u) {
            REXLOG_INFO(
                "Refii plume private rect feedback registered reuse #{} "
                "frame={} slot={} source=0x{:08X}/{}x{} "
                "target=0x{:08X}/{}x{} alias=0x{:08X}/{}x{} "
                "target_content={} vs=0x{:08X} ps=0x{:08X}",
                ++g_direct_renderer
                      .diagnostics.logged_private_rect_feedback_registered_reuses,
                g_direct_renderer.current_frame_index, slot, guest,
                PlumeGuestTextureWidth(*source),
                PlumeGuestTextureHeight(*source), target->guest_address,
                PlumeGuestTextureWidth(*target),
                PlumeGuestTextureHeight(*target), alias->guest_address,
                PlumeGuestTextureWidth(*alias), PlumeGuestTextureHeight(*alias),
                static_cast<unsigned long long>(target_visible_serial),
                draw_command.resources.vertex_shader,
                draw_command.resources.pixel_shader);
          }
          return true;
        }
      }
    }
    return false;
  };
  auto remember_exact_private_feedback_sample_for_slot =
      [&](uint32_t slot) -> bool {
    PlumeTrackedResource* direct_texture =
        slot < draw_command.resources.textures.size()
            ? FindDirectResourceLocked(draw_command.resources.textures[slot])
            : nullptr;
    if (PlumeTrackedResource* exact_sample =
            ResolveDirectExactProducerSampleForSlotLocked(
                draw_command, slot, direct_texture)) {
      return true;
    }
    return false;
  };
  auto prewarm_private_rect_feedback_alias_for_slot = [&](uint32_t slot) {
    if (draw_command.draw.source == RendererDrawEventSource::kPixVideo ||
        slot >= draw_command.resources.textures.size()) {
      return;
    }
    const bool synthetic_private_rect =
        draw_command.draw.source ==
            RendererDrawEventSource::kPrivateTextureRectangle &&
        draw_command.draw.private_texture_rectangle &&
        IsFableSyntheticPrivateRectCopyShader(
            draw_command.resources.pixel_shader);
    const uint32_t guest = draw_command.resources.textures[slot];
    if (guest == 0) {
      return;
    }
    if (!DirectCommandUsesTextureSlotForSyncLocked(
            draw_command, pixel_texture_slot_mask, slot)) {
      return;
    }
    if (synthetic_private_rect && exact_synthetic_private_rect_copy) {
      if (remember_exact_private_feedback_sample_for_slot(slot)) {
        return;
      }
      return;
    }
    if (synthetic_private_rect) {
      PlumeTrackedResource* direct_texture = FindDirectResourceLocked(guest);
      if (direct_texture &&
          !DirectCommandBindsResourceAsTargetLocked(draw_command,
                                                    *direct_texture)) {
        if (DirectCommandCanBindMaterializedPrivateTextureHeaderLocked(
                draw_command, slot, direct_texture) ||
            DirectCommandCanBootstrapRegisteredPrivateRectMaterializationLocked(
                draw_command, slot, direct_texture)) {
          return;
        }


        if (PlumeTrackedResource* exact_sample =
                ResolveDirectExactProducerSampleForSlotLocked(
                    draw_command, slot, direct_texture)) {
          return;
        }
      }
    }
    if (remember_existing_private_feedback_alias_for_slot(slot)) {
      return;
    }
    if (remember_exact_private_feedback_sample_for_slot(slot)) {
      return;
    }
  };
  // Cross-draw memo key for the ensure/prewarm skip gate: the FNV draw key
  // already mixes every slot-derived input, but not the slot number itself
  // nor which of the two call sites is asking — and their bodies differ (the
  // ensure can CREATE the alias, the prewarm cannot) — so salt both in and
  // never cross-hit. The SAMPLED source anchors the entry's content serial
  // (the bound RT0's serial moves on every draw — see the entry's design
  // comment); the bound target only participates for true self-feedback.
  auto private_rect_feedback_memo_key = [&](uint32_t slot,
                                            uint64_t site_salt) {
    return private_rect_feedback_alias_draw_key(slot) ^
           (0x9E3779B97F4A7C15ull * (site_salt + slot + 1u));
  };
  PlumeTrackedResource* const feedback_memo_target = FindDirectResourceLocked(
      draw_command.resources.render_targets[0]);
  auto resolve_private_rect_feedback_memo_source =
      [&](uint32_t slot) -> std::pair<PlumeTrackedResource*, bool> {
    const uint32_t guest = slot < draw_command.resources.textures.size()
                               ? draw_command.resources.textures[slot]
                               : 0u;
    PlumeTrackedResource* source =
        guest != 0 ? FindDirectResourceLocked(guest) : nullptr;
    const bool bound_as_target =
        source != nullptr &&
        DirectCommandBindsResourceAsTargetLocked(draw_command, *source);
    return {source, bound_as_target};
  };
  uint64_t prewarmed_private_rect_feedback_slots = 0;
  auto prewarm_private_rect_feedback_alias_for_slot_once = [&](uint32_t slot) {
    if (slot >= kMaxFableTextureSlots) {
      return;
    }
    const uint64_t slot_bit = 1ull << slot;
    if ((prewarmed_private_rect_feedback_slots & slot_bit) != 0) {
      return;
    }
    prewarmed_private_rect_feedback_slots |= slot_bit;
    const uint64_t memo_key =
        private_rect_feedback_memo_key(slot, /*site_salt=*/0x100u);
    const auto [memo_source, memo_source_bound] =
        resolve_private_rect_feedback_memo_source(slot);
    if (DirectPrivateFeedbackEnsureMemoHitLocked(memo_key, memo_source,
                                                 feedback_memo_target)) {
      ++g_direct_draw_phases.alias_feedback_memo_hits;
      return;
    }
    ++g_direct_draw_phases.alias_feedback_memo_runs;
    prewarm_private_rect_feedback_alias_for_slot(slot);
    RememberDirectPrivateFeedbackEnsureMemoLocked(
        memo_key, memo_source, memo_source_bound, feedback_memo_target);
  };
  const FableDirectClock::time_point sync_flush_t0 = FableDirectClock::now();
  uint64_t flushed_exact_surface_copy_slots = 0;
  for (uint32_t slot = 0; slot < draw_command.resources.textures.size();
       ++slot) {
    if (FlushExactSurfaceCopyForSlotLocked(device, command_list, draw_command,
                                          pixel_texture_slot_mask, slot) &&
        slot < kMaxFableTextureSlots) {
      flushed_exact_surface_copy_slots |= 1ull << slot;
    }
  }
  const FableDirectClock::time_point sync_flush_t1 = FableDirectClock::now();
  g_direct_draw_phases.sync_flush_ms +=
      FableDirectElapsedMs(sync_flush_t0, sync_flush_t1);
  const bool synthetic_private_rect_copy_command =
      draw_command.draw.private_texture_rectangle &&
      IsFableSyntheticPrivateRectCopyShader(
          draw_command.resources.pixel_shader);
  const bool exact_surface_copy_flushed =
      flushed_exact_surface_copy_slots != 0;
  if ((!exact_surface_copy_flushed && !exact_title_billboard_atlas_ready) ||
      synthetic_private_rect_copy_command) {
    prewarm_private_rect_feedback_alias_for_slot_once(
        kFablePrivateRectCopyTextureSlot);
  }
  if (synthetic_private_rect_copy_command) {
    const uint32_t private_slot =
        draw_command.draw.private_texture_slot & (kMaxFableTextureSlots - 1u);
    prewarm_private_rect_feedback_alias_for_slot_once(private_slot);
    if (!exact_synthetic_private_rect_copy ||
        (pixel_texture_slot_mask & 0x1u) != 0) {
      prewarm_private_rect_feedback_alias_for_slot_once(0u);
    }
  }
  const FableDirectClock::time_point sync_prewarm_t1 = FableDirectClock::now();
  g_direct_draw_phases.sync_prewarm_ms +=
      FableDirectElapsedMs(sync_flush_t1, sync_prewarm_t1);
  EnsureMissingDirectPrivateTextureHeaderResourcesForCommandLocked(
      device, draw_command);
  const FableDirectClock::time_point sync_header_t1 = FableDirectClock::now();
  g_direct_draw_phases.sync_header_ms +=
      FableDirectElapsedMs(sync_prewarm_t1, sync_header_t1);
  if (!exact_synthetic_private_rect_copy &&
      DirectCommandLooksLikeBinkIndexedWrapperCandidateLocked(draw_command)) {
    uint32_t missing_bink_slot = 0;
    uint32_t missing_bink_guest = 0;
    if (!DirectBinkWrapperPlaneSetReadyLocked(
            draw_command, &missing_bink_slot, &missing_bink_guest)) {
      SyncBinkWrapperPlanesForDrawLocked(device, command_list, draw_command,
                                         false);
    }
  }
  const FableDirectClock::time_point title_sync_t1 = FableDirectClock::now();
  g_direct_draw_phases.sync_bink_ms +=
      FableDirectElapsedMs(sync_header_t1, title_sync_t1);
  const double title_sync_ms = FableDirectElapsedMs(video_t1, title_sync_t1);
  g_direct_draw_phases.preamble_ms +=
      FableDirectElapsedMs(draw_record_t0, video_t1);
  g_direct_draw_phases.title_sync_ms += title_sync_ms;
  plume::RenderPipeline* pipeline =
      EnsurePipelineForDirectDrawLocked(device, draw_command, framebuffer);
  const FableDirectClock::time_point pipeline_t1 = FableDirectClock::now();
  g_direct_draw_phases.pipeline_ms +=
      FableDirectElapsedMs(title_sync_t1, pipeline_t1);
  if (!pipeline) {
    const FableDirectClock::time_point pipefix_t0 = FableDirectClock::now();
    std::unordered_set<uint32_t> resource_filter;
    std::unordered_set<uint32_t> texture_upload_filter;
    AddDirectCommandResourceInterest(draw_command, resource_filter,
                                     texture_upload_filter);
    SyncDirectResourcesLocked(device, command_list, &texture_upload_filter,
                              &resource_filter);
    pipeline =
        EnsurePipelineForDirectDrawLocked(device, draw_command, framebuffer);
    g_direct_draw_phases.interior_pipefix_ms +=
        FableDirectElapsedMs(pipefix_t0, FableDirectClock::now());
    if (!pipeline) {
      LogFailedDirectDrawLocked("missing_pipeline", draw_command);
      return false;
    }
  }
  const FableDirectClock::time_point interior_reval_t0 =
      FableDirectClock::now();
  {
    // Empty scratch records bind "successfully" as zero-filled buffers
    // (degenerate quads — invisible press-a/legal-text/water): BeginVertices
    // hands the ring pointer out before the CPU writes the payload, and the
    // bind-failure retry below never fires because the bind does not fail.
    // Re-read rings with empty records from guest memory BEFORE the first
    // bind; by execution time the guest wrote them long ago.
    // Sample-time revalidation: CPU-authored sampled textures (never
    // GPU-drawn) may have been rewritten by plain CPU stores since capture
    // (GUI text rasterized after first sync → white/stale surfaces). Once
    // per frame per record, re-capture from the fetch base; unchanged
    // content is a cheap hash compare inside the capture path.
    for (uint32_t slot = 0; slot < draw_command.resources.textures.size();
         ++slot) {
      const uint32_t sampled_guest = draw_command.resources.textures[slot];
      if (sampled_guest == 0) {
        continue;
      }
      // Memo per (record, consuming layout): the gui-text ping-pong samples
      // the same record under different fetch layouts within one frame, and
      // each layout change requires its own re-capture.
      uint32_t layout_salt = 0;
      const size_t memo_fetch_base = size_t(slot) * 6u;
      if (memo_fetch_base + 6u <= draw_command.constants.fetch.size()) {
        layout_salt =
            draw_command.constants.fetch[memo_fetch_base + 1u] * 31u ^
            draw_command.constants.fetch[memo_fetch_base + 2u];
      }
      if (!g_direct_renderer.revalidated_cpu_textures
               .insert(sampled_guest ^ (layout_salt << 1))
               .second) {
        continue;
      }
      PlumeTrackedResource* sampled = FindDirectResourceLocked(sampled_guest);
      if (!sampled || !IsImageResource(sampled->kind) ||
          sampled->draw_content_serial != 0 ||
          sampled->texture_depth_target) {
        continue;
      }
      // Never re-capture channel-owned resources: their physical buffers are
      // live event-channel targets (Bink decode etc.), and a point-in-time
      // read samples a half-written frame (the yellow/green column-band
      // class — run 080's first revalidation hit plane 0x40085EB0 and
      // capture 8748 shows the band). The owning channel delivers their
      // content at the guest's own sequence points. (The observer-map guard
      // this replaced answered TRUE on mutex contention; runs 093/094
      // proved every divergence was that false positive — an ordinary 1x1
      // texture needlessly protected — and zero real plane misses.)
      if (sampled->content_channel != FableGuestContentChannel::kNone) {
        continue;
      }
      // Re-capture with the CONSUMING draw's fetch layout, not the record's
      // stored one: the gui-text ping-pong reuses the same physical bases
      // with different pitch/format/tiling per pass, so a record captured
      // under another pass's layout decodes this pass's bytes into glyph
      // noise (capture 4241, RT 0x40883970).
      std::array<uint32_t, 6> consumer_fetch = {};
      const std::array<uint32_t, 6>* consumer_fetch_override = nullptr;
      const size_t fetch_dword_base = size_t(slot) * consumer_fetch.size();
      if (fetch_dword_base + consumer_fetch.size() <=
          draw_command.constants.fetch.size()) {
        std::copy_n(draw_command.constants.fetch.begin() + fetch_dword_base,
                    consumer_fetch.size(), consumer_fetch.begin());
        if (std::any_of(consumer_fetch.begin(), consumer_fetch.end(),
                        [](uint32_t dword) { return dword != 0; })) {
          consumer_fetch_override = &consumer_fetch;
        }
      }
      if (RefreshRendererTextureFromFetchBase(sampled_guest, nullptr, false,
                                              consumer_fetch_override)) {
        static uint32_t logged_sample_revalidations = 0;
        if (logged_sample_revalidations < 24u) {
          REXLOG_INFO(
              "Refii plume sample-time revalidation #{} serial={} slot={} "
              "guest=0x{:08X} consumer_fetch={}",
              ++logged_sample_revalidations,
              static_cast<unsigned long long>(draw_command.serial), slot,
              sampled_guest, consumer_fetch_override != nullptr ? 1 : 0);
        }
        std::unordered_set<uint32_t> revalidate_filter{sampled_guest};
        std::unordered_set<uint32_t> revalidate_upload_filter{sampled_guest};
        SyncDirectResourcesLocked(device, command_list,
                                  &revalidate_upload_filter,
                                  &revalidate_filter);
      }
    }
    const FableDirectClock::time_point interior_streams_t0 =
        FableDirectClock::now();
    g_direct_draw_phases.interior_reval_ms +=
        FableDirectElapsedMs(interior_reval_t0, interior_streams_t0);
    std::unordered_set<uint32_t> scratch_refresh_filter;
    // DEAD ADDRESS GATE prebind_entry_probe_ps — DELETED 2026-07-26.
    // A DIAGNOSTIC-ONLY pre-bind probe log line, gated on
    // pixel_shader == kFableTitleSceneTextPixelShader (0x42151550). Run 288
    // census[final] (frame=2952) lists it under tested_but_never_matched: the
    // literal was evaluated on every bound draw and never once said yes, so
    // the probe never emitted a line. Pure logging, no render effect.
    for (size_t slot = 0; slot < draw_command.resources.streams.size();
         ++slot) {
      const RendererStreamBindingSnapshot& stream =
          draw_command.resources.streams[slot];
      if (stream.buffer == 0) {
        continue;
      }
      if (DirectInlineBufferCoversAddress(
              draw_command.inline_vertex_buffers[slot], stream.buffer)) {
        // The inline payload was captured at BeginVertices — before the CPU
        // wrote the ring — so it carries the PREVIOUS ring tenant's bytes
        // (or our emulation's zero fill), and its size reflects the stale
        // capture, not this draw's needs (observed: 80B stale vs 120B
        // needed → degenerate invisible press-a/legal-text/water quads).
        // The ring hands out fresh pointers per Begin and wraps rarely at
        // title scale, so the live guest bytes at execute time are this
        // draw's actual payload. Always refresh ring inlines, sized from
        // the draw itself.
        FableDirectInlineBuffer& inline_buffer =
            draw_command.inline_vertex_buffers[slot];
        const bool inline_all_zero =
            std::all_of(inline_buffer.data.begin(), inline_buffer.data.end(),
                        [](uint8_t inline_byte) { return inline_byte == 0; });
        // Only blank inlines may take the execute-time live read: the ring
        // recycles per guest frame (our emulation zero-fills new tenants), so
        // a live read can hit a LATER tenant's bytes — worse than invisible
        // for a stale-but-plausible inline. Stale inlines are repaired by the
        // guest-side next-Begin finalize re-attach instead.
        if (inline_all_zero &&
            (stream.buffer & 0xFF000000u) == 0xFF000000u &&
            stream.stride != 0) {
          const uint32_t draw_vertex_count = DrawVertexOrIndexCount(
              draw_command.draw.draw.primitive_type,
              draw_command.draw.draw.primitive_count);
          const uint32_t needed_bytes = draw_vertex_count * stream.stride;
          const uint32_t refresh_bytes =
              std::max(inline_buffer.byte_size, needed_bytes);
          std::vector<uint8_t> live_bytes = CopyLiveGuestStreamBytes(
              inline_buffer.address, refresh_bytes);
          const bool live_nonzero =
              !live_bytes.empty() &&
              std::any_of(live_bytes.begin(), live_bytes.end(),
                          [](uint8_t byte) { return byte != 0; });
          static uint32_t logged_inline_ring_backfills = 0;
          if (logged_inline_ring_backfills < 32u) {
            REXLOG_INFO(
                "Refii plume inline ring backfill #{} serial={} slot={} "
                "buffer=0x{:08X} captured={} needed={} live={}",
                ++logged_inline_ring_backfills,
                static_cast<unsigned long long>(draw_command.serial), slot,
                stream.buffer, inline_buffer.byte_size, needed_bytes,
                live_nonzero ? 1 : 0);
          }
          if (live_nonzero) {
            inline_buffer.data = std::move(live_bytes);
            inline_buffer.byte_size = refresh_bytes;
            // Bytes mutated after any stamping path ran: void the stamp so
            // the bind hashes the refreshed payload (censused as a lazy
            // hash). Rare repair path (32-print logged).
            inline_buffer.content_hash = 0;
          }
        }
        continue;
      }
      PlumeTrackedResource* tracked = FindDirectResourceLocked(stream.buffer);
      const bool empty_scratch =
          !tracked || !PlumeGuestBufferReady(*tracked) ||
          PlumeGuestBufferByteSize(*tracked) == 0;
      if (!empty_scratch) {
        continue;
      }
      const bool ring_recovered =
          NotifyFableScratchDrawInputMissing(stream.buffer);
      static uint32_t logged_prebind_scratch_refreshes = 0;
      if (logged_prebind_scratch_refreshes < 32u &&
          (stream.buffer & 0xFF000000u) == 0xFF000000u) {
        REXLOG_INFO(
            "Refii plume pre-bind scratch refresh #{} serial={} slot={} "
            "buffer=0x{:08X} tracked={} recovered={}",
            ++logged_prebind_scratch_refreshes,
            static_cast<unsigned long long>(draw_command.serial), slot,
            stream.buffer, tracked ? 1 : 0, ring_recovered ? 1 : 0);
      }
      if (ring_recovered) {
        scratch_refresh_filter.insert(stream.buffer);
        continue;
      }
      const uint32_t live_stride =
          stream.stride != 0
              ? stream.stride
              : ResolveDirectInputSlotStrideLocked(
                    draw_command, static_cast<uint32_t>(slot));
      if (live_stride == 0) {
        continue;
      }
      const uint32_t live_required =
          DirectMinimumVertexBufferViewBytes(draw_command, live_stride);
      const uint64_t live_needed =
          uint64_t(stream.offset) + uint64_t(live_required);
      constexpr uint64_t kMaxLiveStreamReadBytes = 1u << 20;
      if (live_required == 0 || live_needed > kMaxLiveStreamReadBytes) {
        continue;
      }
      std::vector<uint8_t> live_bytes = CopyLiveGuestStreamBytes(
          stream.buffer, static_cast<uint32_t>(live_needed));
      if (live_bytes.size() < live_needed ||
          !DirectBytesHaveNonZero(live_bytes.data(), live_bytes.size())) {
        continue;
      }
      FableDirectInlineBuffer& live_inline =
          draw_command.inline_vertex_buffers[slot];
      live_inline.address = stream.buffer;
      live_inline.byte_size = static_cast<uint32_t>(live_needed);
      live_inline.stride_or_format = live_stride;
      live_inline.data = std::move(live_bytes);
      live_inline.content_hash = 0;
      static uint32_t logged_live_stream_captures = 0;
      if (logged_live_stream_captures < 32u) {
        REXLOG_INFO(
            "Refii plume live stream capture #{} serial={} slot={} "
            "buffer=0x{:08X} stride={} bytes={}",
            ++logged_live_stream_captures,
            static_cast<unsigned long long>(draw_command.serial),
            static_cast<uint32_t>(slot), stream.buffer, live_stride,
            static_cast<uint32_t>(live_needed));
      }
    }
    if (!scratch_refresh_filter.empty()) {
      std::unordered_set<uint32_t> scratch_upload_filter =
          scratch_refresh_filter;
      SyncDirectResourcesLocked(device, command_list, &scratch_upload_filter,
                                &scratch_refresh_filter);
    }
    g_direct_draw_phases.interior_streams_ms +=
        FableDirectElapsedMs(interior_streams_t0, FableDirectClock::now());
  }
  // (The temporary BIND COST run-total probe that lived here is superseded by
  // the per-frame interiorsplit vbind/ibind counters.)
  //
  // Xenos vfetch clamp (root-caused run 502, IDA + payload hex). The guest
  // legitimately draws more vertices than its bound window holds: GUI quad
  // batches bind a 48-byte-stride window sized for the REAL glyphs and issue
  // a fixed-size vertex count, relying on Xenos returning ZERO for vertex
  // fetches beyond the fetch constant's size field — the tail becomes
  // degenerate zero-area quads. Host APIs give no such guarantee, and our
  // coverage gate refused the whole draw (the missing_vertex_input class,
  // runs 497-502), dropping the real glyphs with the padding. Reproduce the
  // hardware clamp: zero-extend a base-anchored inline payload to the draw's
  // required bytes so the tail reads zeros exactly as on console.
  for (size_t clamp_slot = 0;
       clamp_slot < draw_command.inline_vertex_buffers.size() &&
       clamp_slot < draw_command.resources.streams.size();
       ++clamp_slot) {
    FableDirectInlineBuffer& clamp_vb =
        draw_command.inline_vertex_buffers[clamp_slot];
    const RendererStreamBindingSnapshot& clamp_stream =
        draw_command.resources.streams[clamp_slot];
    if (!clamp_vb.ready() || clamp_vb.address != clamp_stream.buffer ||
        clamp_stream.offset != 0) {
      continue;
    }
    const uint32_t clamp_stride = ResolveDirectInputSlotStrideLocked(
        draw_command, static_cast<uint32_t>(clamp_slot));
    if (clamp_stride == 0) {
      continue;
    }
    const uint32_t clamp_required =
        DirectMinimumVertexBufferViewBytes(draw_command, clamp_stride);
    constexpr uint32_t kMaxVfetchClampBytes = 1u << 20;
    if (clamp_required > clamp_vb.byte_size &&
        clamp_required <= kMaxVfetchClampBytes) {
      clamp_vb.data.resize(clamp_required, 0u);
      clamp_vb.byte_size = clamp_required;
      // Bytes changed: drop the stamped hash so the upload cache rekeys.
      clamp_vb.content_hash = 0;
    }
  }
  const FableDirectClock::time_point interior_vbind_t0 =
      FableDirectClock::now();
  bool vertex_input_bound =
      BindDirectVertexInputLocked(device, command_list, draw_command);
  const FableDirectClock::time_point interior_vbind_t1 =
      FableDirectClock::now();
  g_direct_draw_phases.interior_vbind_ms +=
      FableDirectElapsedMs(interior_vbind_t0, interior_vbind_t1);
  bool index_input_bound =
      vertex_input_bound &&
      BindDirectIndexBufferLocked(device, command_list, draw_command);
  g_direct_draw_phases.interior_ibind_ms +=
      FableDirectElapsedMs(interior_vbind_t1, FableDirectClock::now());
  if (!vertex_input_bound || !index_input_bound) {
    const FableDirectClock::time_point interior_retry_t0 =
        FableDirectClock::now();
    // Scratch-ring inputs (BeginIndexedVertices) are written by the CPU
    // after the pointers were handed out; a draw can queue with an empty
    // scratch record. Re-read the ring from guest memory before syncing so
    // the retry binds the recovered payload instead of dropping the draw.
    if (draw_command.resources.index_buffer != 0) {
      NotifyFableScratchDrawInputMissing(draw_command.resources.index_buffer);
    }
    for (const RendererStreamBindingSnapshot& retry_stream :
         draw_command.resources.streams) {
      if (retry_stream.buffer != 0) {
        NotifyFableScratchDrawInputMissing(retry_stream.buffer);
      }
    }
    std::unordered_set<uint32_t> resource_filter;
    std::unordered_set<uint32_t> texture_upload_filter;
    AddDirectCommandResourceInterest(draw_command, resource_filter,
                                     texture_upload_filter);
    SyncDirectResourcesLocked(device, command_list, &texture_upload_filter,
                              &resource_filter);
    vertex_input_bound =
        BindDirectVertexInputLocked(device, command_list, draw_command);
    index_input_bound =
        vertex_input_bound &&
        BindDirectIndexBufferLocked(device, command_list, draw_command);
    g_direct_draw_phases.interior_retry_ms +=
        FableDirectElapsedMs(interior_retry_t0, FableDirectClock::now());
    if (!vertex_input_bound || !index_input_bound) {
      LogFailedDirectDrawLocked(
          !vertex_input_bound ? "missing_vertex_input" : "missing_index_input",
          draw_command);
      return false;
    }
  }
  const FableDirectClock::time_point input_t1 = FableDirectClock::now();
  g_direct_draw_phases.interior_ms +=
      FableDirectElapsedMs(pipeline_t1, input_t1);

  auto ensure_private_rect_feedback_alias_for_slot = [&](uint32_t slot) {
    if (draw_command.draw.source ==
            RendererDrawEventSource::kPrivateTextureRectangle &&
        draw_command.draw.private_texture_rectangle &&
        IsFableSyntheticPrivateRectCopyShader(
            draw_command.resources.pixel_shader) &&
        slot < draw_command.resources.textures.size()) {
      const uint32_t guest = draw_command.resources.textures[slot];
      PlumeTrackedResource* direct_texture =
          guest != 0 ? FindDirectResourceLocked(guest) : nullptr;
      if (exact_synthetic_private_rect_copy) {
        if (remember_exact_private_feedback_sample_for_slot(slot)) {
          return;
        }
        return;
      }
      if (direct_texture &&
          !DirectCommandBindsResourceAsTargetLocked(draw_command,
                                                    *direct_texture)) {
        if (DirectCommandCanBindMaterializedPrivateTextureHeaderLocked(
                draw_command, slot, direct_texture) ||
            DirectCommandCanBootstrapRegisteredPrivateRectMaterializationLocked(
                draw_command, slot, direct_texture)) {
          return;
        }
        DirectPrivateTextureCopyActionMatch action_match =
            DirectPrivateTextureCopyActionMatch::kNone;
        const FablePrivateTextureCopyAction* action =
            SelectDirectExactPrivateTextureCopyActionForCommandSlotLocked(
                draw_command, slot, direct_texture, &action_match);
        if (action &&
            DirectPrivateTextureCopyActionMatchIsExactRegistered(
                action_match) &&
            !DirectPrivateTextureCopyActionProvidesSampleLocked(draw_command,
                                                               *action)) {
          if (PlumeTrackedResource* exact_sample =
                  ResolveDirectExactProducerSampleForSlotLocked(
                      draw_command, slot, direct_texture)) {
            return;
          }
          if (PlumeTrackedResource* alias =
                  EnsureDirectPrivateRectFeedbackAliasLocked(
                      device, command_list, draw_command, slot)) {
            private_rect_feedback_aliases_for_draw
                [private_rect_feedback_alias_draw_key(slot)] =
                    alias->guest_address;
            return;
          }
        }
      }
      if (remember_exact_private_feedback_sample_for_slot(slot)) {
        return;
      }
    }
    if (remember_existing_private_feedback_alias_for_slot(slot)) {
      return;
    }
    const uint64_t draw_alias_key =
        private_rect_feedback_alias_draw_key(slot);
    const auto alias_it =
        private_rect_feedback_aliases_for_draw.find(draw_alias_key);
    if (alias_it != private_rect_feedback_aliases_for_draw.end()) {
      // (Was: a FindDirectResourceLocked call into an EMPTY if-body. The result
      // was never named or used -- the lookup existed only to be discarded.
      // Removed; the dedup hit already means this slot is handled.)
      return;
    }
    const uint32_t guest =
        slot < draw_command.resources.textures.size()
            ? draw_command.resources.textures[slot]
            : 0u;
    PlumeTrackedResource* direct_texture =
        guest != 0 ? FindDirectResourceLocked(guest) : nullptr;
    const bool scene_object_feedback_draw =
        draw_command.draw.source == RendererDrawEventSource::kSceneObject &&
        (IsFableTitleSceneVertexShaderCommandLocked(draw_command) ||
         IsFableTitleSceneTargetLocked(draw_command.resources.render_targets[0]));
    if (scene_object_feedback_draw) {
      if (DirectCommandTextureSlotExactProducerProvidesSampleLocked(
              draw_command, slot, direct_texture)) {
        return;
      }
    }
    if (DirectCommandCanSnapshotPrivateTextureFeedbackLocked(
            draw_command, slot, direct_texture) &&
        remember_exact_private_feedback_sample_for_slot(slot)) {
      return;
    }
    if (remember_exact_private_feedback_sample_for_slot(slot)) {
      return;
    }
  };
  uint64_t ensured_private_rect_feedback_slots = 0;
  auto ensure_private_rect_feedback_alias_for_slot_once = [&](uint32_t slot) {
    if (slot >= kMaxFableTextureSlots) {
      return;
    }
    const uint64_t slot_bit = 1ull << slot;
    if ((ensured_private_rect_feedback_slots & slot_bit) != 0) {
      return;
    }
    ensured_private_rect_feedback_slots |= slot_bit;
    const uint64_t memo_key =
        private_rect_feedback_memo_key(slot, /*site_salt=*/0x200u);
    const auto [memo_source, memo_source_bound] =
        resolve_private_rect_feedback_memo_source(slot);
    if (DirectPrivateFeedbackEnsureMemoHitLocked(memo_key, memo_source,
                                                 feedback_memo_target)) {
      ++g_direct_draw_phases.alias_feedback_memo_hits;
      return;
    }
    ++g_direct_draw_phases.alias_feedback_memo_runs;
    ensure_private_rect_feedback_alias_for_slot(slot);
    RememberDirectPrivateFeedbackEnsureMemoLocked(
        memo_key, memo_source, memo_source_bound, feedback_memo_target);
  };
  if ((!exact_surface_copy_flushed && !exact_title_billboard_atlas_ready) ||
      synthetic_private_rect_copy_command) {
    ensure_private_rect_feedback_alias_for_slot_once(
        kFablePrivateRectCopyTextureSlot);
  }
  if (synthetic_private_rect_copy_command) {
    const uint32_t private_slot =
        draw_command.draw.private_texture_slot & (kMaxFableTextureSlots - 1u);
    ensure_private_rect_feedback_alias_for_slot_once(private_slot);
    if (!exact_synthetic_private_rect_copy ||
        (pixel_texture_slot_mask & 0x1u) != 0) {
      ensure_private_rect_feedback_alias_for_slot_once(0u);
    }
  }
  if (!exact_title_billboard_atlas_ready &&
      draw_command.draw.source == RendererDrawEventSource::kSceneObject &&
      (IsFableTitleSceneVertexShaderCommandLocked(draw_command) ||
       IsFableTitleSceneTargetLocked(draw_command.resources.render_targets[0]))) {
    const uint32_t texture_slot_mask =
        DirectCommandPixelTextureSlotMaskLocked(draw_command);
    for (uint32_t slot = 0; slot < draw_command.resources.textures.size(); ++slot) {
      if (!DirectCommandUsesTextureSlotForSyncLocked(
              draw_command, texture_slot_mask, slot)) {
        continue;
      }
      ensure_private_rect_feedback_alias_for_slot_once(slot);
    }
  }
  const FableDirectClock::time_point alias_feedback_t1 =
      FableDirectClock::now();
  g_direct_draw_phases.alias_feedback_ms +=
      FableDirectElapsedMs(input_t1, alias_feedback_t1);
  ResolveUnbackedTitlePrivateProxyCopyLocked(draw_command);
  const FableDirectClock::time_point alias_proxy_t1 = FableDirectClock::now();
  g_direct_draw_phases.alias_proxy_ms +=
      FableDirectElapsedMs(alias_feedback_t1, alias_proxy_t1);
  const bool exact_bink_wrapper_video_draw =
      DirectCommandIsExactBinkIndexedWrapperDrawLocked(draw_command);
  if (!exact_bink_wrapper_video_draw && !exact_title_billboard_atlas_ready) {
    RecordDirectSurfaceAliasHazardCopiesForCommandLocked(command_list,
                                                         draw_command);
  } else if (exact_bink_wrapper_video_draw) {
    // These three resolves feed ONLY the log below, and the 32-line cap used to
    // be tested after they ran -- so every Bink-wrapper draw kept paying for
    // three plane resolutions (each running the wrapper-candidate and
    // plane-readiness classifiers) long after the log had gone quiet. Test the
    // cap first; nothing else observes y/cr/cb.
    static uint32_t logged_exact_bink_alias_hazard_rejects = 0;
    const PlumeTrackedResource* y = nullptr;
    const PlumeTrackedResource* cr = nullptr;
    const PlumeTrackedResource* cb = nullptr;
    if (logged_exact_bink_alias_hazard_rejects < 32u) {
      y = ResolveDirectExactBinkWrapperPlaneForSlotLocked(draw_command, 0);
      cr = ResolveDirectExactBinkWrapperPlaneForSlotLocked(draw_command, 1);
      cb = ResolveDirectExactBinkWrapperPlaneForSlotLocked(draw_command, 2);
    }
    if (y && cr && cb && logged_exact_bink_alias_hazard_rejects < 32u) {
      REXLOG_INFO(
          "Refii plume exact Bink wrapper resolved submitted video planes "
          "#{} frame={} serial={} draw={} rt=0x{:08X} ds=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X} y=0x{:08X}/{}x{} "
          "cr=0x{:08X}/{}x{} cb=0x{:08X}/{}x{}",
          ++logged_exact_bink_alias_hazard_rejects,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(draw_command.serial),
          static_cast<unsigned long long>(draw_command.draw_serial),
          draw_command.resources.render_targets[0],
          draw_command.resources.depth_stencil,
          draw_command.resources.vertex_shader,
          draw_command.resources.pixel_shader,
          y->guest_address, PlumeGuestTextureWidth(*y),
          PlumeGuestTextureHeight(*y), cr->guest_address,
          PlumeGuestTextureWidth(*cr), PlumeGuestTextureHeight(*cr),
          cb->guest_address, PlumeGuestTextureWidth(*cb),
          PlumeGuestTextureHeight(*cb));
    }
  }
  const FableDirectClock::time_point alias_hazard_t1 = FableDirectClock::now();
  g_direct_draw_phases.alias_hazard_ms +=
      FableDirectElapsedMs(alias_proxy_t1, alias_hazard_t1);
  if (!exact_bink_wrapper_video_draw &&
      !g_direct_renderer.packet_queue_owns_surface_destination) {
    // This runs PER DRAW, and each call rebuilds a snapshot of the whole
    // relation registry: two registry maps walked, every visible relation
    // copied into a map and again into a vector, then sorted -- after which
    // every relation typically hits the "already recorded this frame" guard and
    // the entire snapshot is discarded.
    //
    // It cannot simply be hoisted to once per frame: relations are registered
    // as draws are recorded, and a copy must be recorded before a later draw
    // samples that destination. So gate on the registry's content GENERATION.
    // The raw mutation serial proved useless here: every per-draw link
    // re-registration bumped it even when the re-stored relation was
    // byte-identical, so during bursts every draw carried a new serial and the
    // gate never skipped (run 479: 3,280 draws, aliassplit dest=3807.7ms,
    // 1.16ms per draw of pure snapshot-rebuild-and-discard). The generation
    // moves only when a store or repair changes stored relation bytes.
    // Reading the generation is O(1); building the snapshot is not.
    //
    // The generation gate decides WHEN a run happens; the plan below decides
    // WHAT it visits (run 486: the 16 gate-passed runs still cost ~92ms each
    // because each walked the whole ~1478-relation population). A targeted
    // run visits only the destination buckets the draw could observe, so the
    // old proof ("after a run everything is recorded") no longer holds
    // globally — the per-bucket attempt marks inside the plan carry it at
    // bucket granularity instead: a draw binding a bucket that no run has
    // visited at the current generation triggers a catch-up run for exactly
    // that bucket, even when the generation itself has not moved.
    const DirectSurfaceDestinationScanPlan scan_plan =
        PlanDirectSurfaceDestinationScanForDrawLocked(draw_command);
    const bool same_frame =
        g_direct_renderer.last_surface_destination_scan_frame ==
        g_direct_renderer.current_frame_index;
    const bool generation_gate_fires =
        !same_frame ||
        scan_plan.generation !=
            g_direct_renderer.last_surface_destination_scan_generation;
    if (generation_gate_fires) {
      g_direct_renderer.last_surface_destination_scan_frame =
          g_direct_renderer.current_frame_index;
      g_direct_renderer.last_surface_destination_scan_generation =
          scan_plan.generation;
    }
    // RUN-487 FREEZE BACKSTOP: generation churn at the storybook menu fired
    // this gate 164×/compose (panel content serials move per redraw), each a
    // ~75ms global run — 12.3s of dest scan and a frozen record thread. Cap
    // per-draw runs per compose; the pre/post compose-level global runs
    // record anything the capped tail would have caught (worst case: an
    // intra-compose copy ordering deferred to the end-of-compose run).
    static uint32_t dest_run_cap_frame = UINT32_MAX;
    static uint32_t dest_runs_this_compose = 0;
    if (dest_run_cap_frame != g_direct_renderer.current_frame_index) {
      dest_run_cap_frame = g_direct_renderer.current_frame_index;
      dest_runs_this_compose = 0;
    }
    constexpr uint32_t kMaxPerDrawDestScanRunsPerCompose = 8;
    const bool wants_run = (generation_gate_fires && scan_plan.global) ||
                           !scan_plan.buckets.empty();
    if (wants_run &&
        dest_runs_this_compose < kMaxPerDrawDestScanRunsPerCompose) {
      ++dest_runs_this_compose;
      ++g_direct_draw_phases.alias_destination_scan_runs;
      RecordDirectSurfaceDestinationCopiesLocked(command_list, false,
                                                 &scan_plan);
    } else {
      ++g_direct_draw_phases.alias_destination_scan_skips;
    }
  }
  const FableDirectClock::time_point alias_t1 = FableDirectClock::now();
  g_direct_draw_phases.alias_destination_ms +=
      FableDirectElapsedMs(alias_hazard_t1, alias_t1);
  g_direct_draw_phases.alias_ms += FableDirectElapsedMs(input_t1, alias_t1);

  TransitionDirectFramebufferForWrite(command_list, framebuffer);
  command_list->setFramebuffer(framebuffer.framebuffer);
  EnsureTitleSceneTargetInitializedForDrawLocked(command_list, draw_command,
                                                framebuffer);
  const FableDirectClock::time_point submit_fb_t1 = FableDirectClock::now();
  TransitionDirectSampledTexturesForDrawLocked(command_list, draw_command,
                                               framebuffer);
  const FableDirectClock::time_point submit_tex_t1 = FableDirectClock::now();
  command_list->setGraphicsPipelineLayout(g_plume_resources.pipeline_layout.get());
  if (!BindDirectDrawConstantsLocked(device, command_list, draw_command,
                                     framebuffer)) {
    LogFailedDirectDrawLocked("missing_constants", draw_command);
    return false;
  }
  const FableDirectClock::time_point submit_const_t1 = FableDirectClock::now();
  for (uint32_t set = 0; set < kDirectTextureDescriptorSets; ++set) {
    command_list->setGraphicsDescriptorSet(
        g_plume_resources.texture_descriptor_sets[set].get(), set);
  }
  command_list->setGraphicsDescriptorSet(
      g_plume_resources.sampler_descriptor_set.get(),
      kDirectSamplerDescriptorSet);
  command_list->setPipeline(pipeline);
  const FableDirectClock::time_point submit_bind_t1 = FableDirectClock::now();
  if (draw_command.draw.source == RendererDrawEventSource::kPixVideo &&
      g_direct_renderer.diagnostics.logged_pix_video_draw_bindings < 12u) {
    LogPixVideoTextureBindingLocked(draw_command, 0);
    LogPixVideoTextureBindingLocked(draw_command, 1);
    LogPixVideoTextureBindingLocked(draw_command, 2);
    ++g_direct_renderer.diagnostics.logged_pix_video_draw_bindings;
  }
  if (draw_command.draw.private_texture_rectangle &&
      (IsFableSyntheticPrivateRectCopyShader(
           draw_command.resources.pixel_shader)
           ? g_direct_renderer.diagnostics.logged_private_rect_copy_draw_bindings < 24u
           : g_direct_renderer.diagnostics.logged_private_rect_video_draw_bindings < 12u)) {
    LogPrivateRectTextureBindingLocked(draw_command);
    if (IsFableSyntheticPrivateRectCopyShader(
            draw_command.resources.pixel_shader)) {
      ++g_direct_renderer.diagnostics.logged_private_rect_copy_draw_bindings;
    } else {
      ++g_direct_renderer.diagnostics.logged_private_rect_video_draw_bindings;
    }
  }
  LogTitleSceneDrawLocked(draw_command, framebuffer);
  LogSmallGuiTextureBindingsLocked(draw_command);
  const FableDirectClock::time_point submit_log_t1 = FableDirectClock::now();

  // THE SECOND SCALER LIVED HERE, AND IT COMPOUNDED (deleted 2026-08-04).
  //
  // This multiplied the viewport and scissor AGAIN, by
  // `framebuffer_extent / guest_display_extent` (1280x720), gated on
  // `viewport_guest_space` — the SAME gate ScaleFableTitleDrawStateToFramebuffer
  // uses. So every guest-space command that the first scaler had already
  // converted into attachment space was scaled a second time here. Its
  // `viewport.width <= native` guard hid that for full-screen viewports but not
  // for sub-rects: a 560x360 guest viewport on a target promoted 1120x720 ->
  // 1920x1080 became 960x540 in the first scaler, then passed this guard
  // (960 <= 1280, 540 <= 720) and was scaled to 1440x810. That is the same
  // double-scale shape as the run-552 scissor, in a different place.
  //
  // There is now ONE scaler, and it runs in guest space against the guest target
  // extent. By the time a command reaches the executor its viewport and scissor
  // are already in attachment space, so the executor's only job is to enforce
  // the invariant below.
  //
  // FINAL INVARIANT — a viewport or scissor may never exceed its attachment.
  // Applied UNCONDITIONALLY, to every draw, whatever produced the numbers, and
  // deliberately last: capture frame3342 still showed `vp=217x1923` on a 960x540
  // depth target (eid=300, a non-guest-space command the upstream clamp does not
  // see) and `vp=1920x1080` on a 280x180 target (eid=263, our own blit path).
  // Rasterizing outside the bound attachments is exactly the Xid 13
  // "3D WIDTH/HEIGHT ZT Violation" that cost several runs, and clamping can only
  // ever remove coverage we had no right to draw.
  const float fb_w = static_cast<float>(framebuffer.width);
  const float fb_h = static_cast<float>(framebuffer.height);
  if (draw_command.viewport.valid) {
    float min_depth = draw_command.viewport.min_depth;
    float max_depth = draw_command.viewport.max_depth;
    if (min_depth > max_depth) {
      std::swap(min_depth, max_depth);
    }
    const float vx = std::clamp(draw_command.viewport.x, 0.0f, fb_w);
    const float vy = std::clamp(draw_command.viewport.y, 0.0f, fb_h);
    const float vw = std::clamp(draw_command.viewport.width, 0.0f, fb_w - vx);
    const float vh = std::clamp(draw_command.viewport.height, 0.0f, fb_h - vy);
    if (vw != draw_command.viewport.width ||
        vh != draw_command.viewport.height) {
      ++g_direct_renderer.diagnostics.viewport_clamped_to_attachment;
      static uint32_t logged_attachment_clamp = 0;
      if (logged_attachment_clamp < 24u) {
        ++logged_attachment_clamp;
        REXLOG_WARN(
            "Refii plume viewport EXCEEDED attachment #{} frame={} fb={}x{} "
            "vp {:.1f},{:.1f} {:.1f}x{:.1f} -> {:.1f},{:.1f} {:.1f}x{:.1f} "
            "guest_space={} rt=0x{:08X} — clamped; a viewport outside its own "
            "attachment is a ZT-violation candidate",
            logged_attachment_clamp, g_direct_renderer.current_frame_index,
            framebuffer.width, framebuffer.height, draw_command.viewport.x,
            draw_command.viewport.y, draw_command.viewport.width,
            draw_command.viewport.height, vx, vy, vw, vh,
            draw_command.viewport_guest_space ? 1 : 0,
            framebuffer.color_address);
      }
    }
    command_list->setViewports(
        plume::RenderViewport(vx, vy, vw, vh, min_depth, max_depth));
  } else {
    command_list->setViewports(plume::RenderViewport(0.0f, 0.0f, fb_w, fb_h));
  }
  const int32_t max_x = static_cast<int32_t>(framebuffer.width);
  const int32_t max_y = static_cast<int32_t>(framebuffer.height);
  if (draw_command.scissor.valid) {
    command_list->setScissors(plume::RenderRect(
        std::clamp(draw_command.scissor.left, 0, max_x),
        std::clamp(draw_command.scissor.top, 0, max_y),
        std::clamp(draw_command.scissor.right, 0, max_x),
        std::clamp(draw_command.scissor.bottom, 0, max_y)));
  } else {
    command_list->setScissors(plume::RenderRect(0, 0, max_x, max_y));
  }

  const uint32_t count = DirectDrawVertexOrIndexCount(draw_command);
  if (count == 0) {
    LogFailedDirectDrawLocked("zero_draw_count", draw_command);
    return false;
  }
  const bool synthetic_quad_list =
      DirectDrawUsesSyntheticQuadListIndices(draw_command);
  if (synthetic_quad_list &&
      !BindDirectQuadListIndexBufferLocked(
          device, command_list, draw_command.draw.draw.primitive_count)) {
    LogFailedDirectDrawLocked("missing_quad_list_index_input", draw_command);
    return false;
  }
  // DIAGONAL DIAG: the title composite/video quads render as a single
  // upper-right triangle (clean corner-to-corner black split) even though
  // indices repair to a valid [0,1,2,0,2,3] and the frame records failed=0.
  // That points at a DEGENERATE quad vertex (a collapsed corner zeroing one of
  // the two triangles), not an index or count fault. Dump the 4 post-byteswap
  // corner positions of the small scratch-wrapper quads so we can see which
  // corner collapsed. Gated on indexed scratch quads with a 4-vertex buffer.
  if (draw_command.draw.source == RendererDrawEventSource::kScratchWrapper &&
      draw_command.draw.draw.indexed &&
      draw_command.inline_vertex_buffers[0].ready()) {
    const FableDirectInlineBuffer& dvb = draw_command.inline_vertex_buffers[0];
    const uint32_t vstride = dvb.stride_or_format != 0 ? dvb.stride_or_format : 1u;
    const uint32_t vcount = dvb.byte_size / vstride;
    if (vcount >= 3u && vcount <= 6u) {
      static uint32_t logged_quad_pos = 0;
      if (logged_quad_pos < 32u) {
        const auto vf = [&](uint32_t v, uint32_t c) {
          return ReadDirectInlineFloat(dvb, v * vstride + c * 4u);
        };
        // Repaired 16-bit indices: inline_index_buffer holds the RAW guest
        // bytes (big-endian); ReadDirectInlineIndex reads host-order, so
        // byte-swap here to show the values the executor actually draws with
        // (the endian-repair uploads the swapped set). This nails the triangle
        // winding: same winding for both tris => cull cannot drop one; opposite
        // winding => a wrong cull mode drops exactly the missing half.
        const FableDirectInlineBuffer& dib = draw_command.inline_index_buffer;
        const auto ix = [&](uint32_t i) -> uint32_t {
          const uint32_t raw = ReadDirectInlineIndex(dib, i);
          return ((raw & 0xFFu) << 8) | ((raw >> 8) & 0xFFu);
        };
        // Depth/classification: a 2D fullscreen video composite must draw with
        // depth OFF. If this quad is not classified as a Bink/PixVideo draw it
        // keeps depth-test on and stale depth in the lower-left kills triangle
        // {1,0,2}. Log the axes that decide it: source, bink-exact predicate,
        // packet depth_control, and the CULLMODE render state.
        const bool bink_exact =
            DirectCommandIsExactBinkIndexedWrapperDrawLocked(draw_command);
        REXLOG_INFO(
            "Refii GTL quad-pos DIAG #{} frame={} serial={} vb=0x{:08X} "
            "stride={} vcount={} count={} start={} idx=[{},{},{},{},{},{}] "
            "src={} bink_exact={} ps=0x{:08X} zenable=0x{:08X} zwrite=0x{:08X} "
            "zfunc=0x{:08X} cull=0x{:08X} pm4_valid={} "
            "v0=[{:.3f},{:.3f},{:.3f},{:.3f}] v1=[{:.3f},{:.3f},{:.3f},{:.3f}] "
            "v2=[{:.3f},{:.3f},{:.3f},{:.3f}] v3=[{:.3f},{:.3f},{:.3f},{:.3f}]",
            ++logged_quad_pos, g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(draw_command.serial), dvb.address,
            vstride, vcount, count, draw_command.draw.draw.start,
            ix(0), ix(1), ix(2), ix(3), ix(4), ix(5),
            static_cast<uint32_t>(draw_command.draw.source), bink_exact ? 1 : 0,
            draw_command.resources.pixel_shader,
            draw_command.pipeline.render_states[kFableRenderStateZEnable],
            draw_command.pipeline.render_states[kFableRenderStateZWriteEnable],
            draw_command.pipeline.render_states[kFableRenderStateZFunc],
            draw_command.pipeline.render_states[kFableRenderStateCullMode],
            draw_command.pipeline.pm4.valid ? 1 : 0,
            vf(0, 0), vf(0, 1), vf(0, 2), vf(0, 3), vf(1, 0), vf(1, 1),
            vf(1, 2), vf(1, 3), vf(2, 0), vf(2, 1), vf(2, 2), vf(2, 3),
            vf(3, 0), vf(3, 1), vf(3, 2), vf(3, 3));
      }
    }
  }
  const FableDirectClock::time_point submit_vp_t1 = FableDirectClock::now();
  ZoneScopedN("RefiiRenderer::SubmitDrawPacket");
  const char* pass_name = ClassifyDirectRenderPassLocked(draw_command);
  const std::string draw_debug_label =
      BuildDirectDrawDebugLabelLocked(draw_command, count, synthetic_quad_list,
                                      pass_name);
  ZoneText(draw_debug_label.c_str(), draw_debug_label.size());
  PlumeDirectDebugLabelScope draw_debug_scope(command_list, draw_debug_label);
  const FableDirectClock::time_point submit_label_t1 = FableDirectClock::now();
  if (draw_command.draw.draw.indexed) {
    command_list->drawIndexedInstanced(count, 1, draw_command.draw.draw.start,
                                       DirectDrawBaseVertex(draw_command), 0);
  } else if (synthetic_quad_list) {
    command_list->drawIndexedInstanced(count, 1, 0,
                                       draw_command.draw.draw.start, 0);
  } else {
    command_list->drawInstanced(count, 1, draw_command.draw.draw.start, 0);
  }
  draw_debug_scope.End();
  const FableDirectClock::time_point submit_draw_t1 = FableDirectClock::now();
  const bool synthetic_private_rect_copy_draw =
      draw_command.draw.source == RendererDrawEventSource::kPrivateTextureRectangle &&
      draw_command.draw.private_texture_rectangle &&
      IsFableSyntheticPrivateRectCopyShader(draw_command.resources.pixel_shader);
  if (synthetic_private_rect_copy_draw) {
    const uint64_t write_serial = ++g_direct_renderer.scene_write_serial;
    for (uint32_t slot = 0; slot < framebuffer.color_count; ++slot) {
      if (framebuffer.color_addresses[slot] == 0 ||
          DirectColorWriteMaskForFramebufferTarget(draw_command, framebuffer,
                                                   slot) == 0) {
        continue;
      }
      MarkDirectResourceGpuProducedContentLocked(
          framebuffer.color_addresses[slot], write_serial, false,
          slot < framebuffer.color_textures.size()
              ? framebuffer.color_textures[slot]
              : nullptr);
      if (PlumeTrackedResource* target =
              FindDirectResourceLocked(framebuffer.color_addresses[slot])) {
        RememberMaterializedPrivateRectTargetSourcesLocked(draw_command,
                                                           *target);
      }
    }
    if (DirectSyntheticPrivateRectCommandUsesDepthTarget(draw_command)) {
      if (PlumeTrackedResource* depth_target =
              FindDirectResourceLocked(draw_command.resources.depth_stencil);
          depth_target && IsImageResource(depth_target->kind) &&
          PlumeGuestImageReady(*depth_target) &&
          DirectCommandBindsResourceAsTargetLocked(draw_command,
                                                  *depth_target)) {
        MarkDirectResourceGpuProducedContentLocked(
            depth_target->guest_address, write_serial);
        RememberMaterializedPrivateRectTargetSourcesLocked(draw_command,
                                                           *depth_target);
      }
    }
  } else if (ShouldRememberDrawnSceneTargetForCommand(draw_command)) {
    RememberDrawnSceneTargetLocked(draw_command, framebuffer,
                                   submitted_command_source);
  }
  if (FableVideoCommandIsDirectVideoDraw(
          draw_command, DirectCommandIsExactBinkIndexedWrapperDrawLocked) &&
      framebuffer.color_address != 0 &&
      // Video also draws sub-display helpers (480x270 previews, 960x540
      // legal insets); letting them clobber the tracked video present target
      // between composite draws starves the video-present branch. Only
      // display-sized targets (live guest dims) participate in video
      // presentation.
      framebuffer.width >= FableGuestDisplayWidth() &&
      framebuffer.height >= FableGuestDisplayHeight()) {
    g_direct_renderer.last_pix_video_target = framebuffer.color_address;
    g_direct_renderer.last_pix_video_frame =
        g_direct_renderer.current_frame_index;
    // Defer the hold snapshot to the end of the batch replay: recording the
    // copy while the draw's render pass is still open captures pre-draw
    // contents on some frames (alternating black snapshots).
    g_direct_renderer.pending_video_hold_texture = framebuffer.color_texture;
    g_direct_renderer.pending_video_hold_target = framebuffer.color_address;
    g_direct_renderer.pending_video_hold_width = framebuffer.width;
    g_direct_renderer.pending_video_hold_height = framebuffer.height;
    g_direct_renderer.pending_video_hold_format = framebuffer.color_format;
    {
      // Video-black diagnosis: sample what the video draw actually binds.
      static uint64_t logged_video_draw_plane_states = 0;
      const uint64_t log_count = ++logged_video_draw_plane_states;
      if (log_count <= 8u || (log_count % 256u) == 0u) {
        char plane_states[256] = {};
        size_t offset = 0;
        for (uint32_t slot = 0; slot < 3u; ++slot) {
          PlumeTrackedResource* plane =
              ResolveDirectExactBinkWrapperPlaneForSlotLocked(draw_command,
                                                              slot);
          uint64_t observer_generation = 0;
          if (plane) {
            const auto observer_it =
                FableDirectGuestResourceRegistry().resources.find(
                    plane->guest_address);
            if (observer_it != FableDirectGuestResourceRegistry().resources.end()) {
              observer_generation = observer_it->second.upload_generation;
            }
          }
          const int written = std::snprintf(
              plane_states + offset, sizeof(plane_states) - offset,
              " s%u=0x%08X/gen%llu/obs%llu%s%s%s", slot,
              plane ? plane->guest_address : 0u,
              plane ? static_cast<unsigned long long>(plane->upload_generation)
                    : 0ull,
              static_cast<unsigned long long>(observer_generation),
              plane && plane->texture_upload_pending ? "/pend" : "",
              plane && (plane->texture_render_target ||
                        plane->texture_depth_target)
                  ? "/target"
                  : "",
              plane && PlumeGuestImageReady(*plane) ? "" : "/notready");
          if (written <= 0) {
            break;
          }
          offset += static_cast<size_t>(written);
        }
        REXLOG_INFO(
            "Refii plume video draw plane state #{} frame={} rt=0x{:08X}{}",
            log_count, g_direct_renderer.current_frame_index,
            framebuffer.color_address, plane_states);
      }
    }
  }
  if (!g_direct_renderer.diagnostics.logged_first_draw) {
    REXLOG_INFO(
        "Refii plume direct renderer submitted first draw indexed={} count={} "
        "vs=0x{:08X} ps=0x{:08X} decl=0x{:08X}",
        draw_command.draw.draw.indexed, count,
        draw_command.resources.vertex_shader, draw_command.resources.pixel_shader,
        draw_command.resources.vertex_declaration);
    g_direct_renderer.diagnostics.logged_first_draw = true;
  }
  {
    const FableDirectClock::time_point submit_end = FableDirectClock::now();
    g_direct_draw_phases.submit_ms +=
        FableDirectElapsedMs(alias_t1, submit_end);
    g_direct_draw_phases.submit_fb_ms +=
        FableDirectElapsedMs(alias_t1, submit_fb_t1);
    g_direct_draw_phases.submit_tex_ms +=
        FableDirectElapsedMs(submit_fb_t1, submit_tex_t1);
    g_direct_draw_phases.submit_const_ms +=
        FableDirectElapsedMs(submit_tex_t1, submit_const_t1);
    g_direct_draw_phases.submit_bind_ms +=
        FableDirectElapsedMs(submit_const_t1, submit_bind_t1);
    g_direct_draw_phases.submit_log_ms +=
        FableDirectElapsedMs(submit_bind_t1, submit_log_t1);
    g_direct_draw_phases.submit_vp_ms +=
        FableDirectElapsedMs(submit_log_t1, submit_vp_t1);
    g_direct_draw_phases.submit_label_ms +=
        FableDirectElapsedMs(submit_vp_t1, submit_label_t1);
    g_direct_draw_phases.submit_draw_ms +=
        FableDirectElapsedMs(submit_label_t1, submit_draw_t1);
    g_direct_draw_phases.submit_post_ms +=
        FableDirectElapsedMs(submit_draw_t1, submit_end);
  }
  if (RendererDeepObserverHooksEnabled()) {
    const double alias_ms = FableDirectElapsedMs(input_t1, alias_t1);
    const FableDirectClock::time_point diagnostic_t0 =
        FableDirectClock::now();
    const double pre_diagnostic_draw_record_ms =
        FableDirectElapsedMs(draw_record_t0, diagnostic_t0);
    LogTitleTextureSlotDiagnosticLocked(draw_command, framebuffer,
                                        pixel_texture_slot_mask,
                                        pre_diagnostic_draw_record_ms,
                                        title_sync_ms, alias_ms);
  }
  return true;
}

std::unique_ptr<plume::RenderBuffer> CreateSyntheticPrivateRectVertexUpload(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    uint32_t slot) {
  if (!device || !command.draw.private_texture_corners_valid) {
    return nullptr;
  }
  const float u0 = command.draw.private_texture_uv_override
                       ? command.draw.private_texture_u0
                       : 0.0f;
  const float v0 = command.draw.private_texture_uv_override
                       ? command.draw.private_texture_v0
                       : 0.0f;
  const float u1 = command.draw.private_texture_uv_override
                       ? command.draw.private_texture_u1
                       : 1.0f;
  const float v1 = command.draw.private_texture_uv_override
                       ? command.draw.private_texture_v1
                       : 1.0f;
  const auto vertices = BuildPrivateTextureRectangleDirectVertices(
      command.draw.private_texture_corners, u0, v0, u1, v1);
  char name[128] = {};
  std::snprintf(name, sizeof(name),
                "Refii Fable regenerated private rect VB%u serial=%llu", slot,
                static_cast<unsigned long long>(command.serial));
  return CreateUploadBufferWithData(
      device, vertices.data(),
      static_cast<uint32_t>(sizeof(FablePrivateRectDirectVertex) *
                            vertices.size()),
      name, plume::RenderBufferFlag::VERTEX);
}

// Content-keyed variant of CreateSyntheticPrivateRectVertexUpload (2026-08-02
// compose peel): the regenerated corner quad is a pure function of the
// command's corners + UV override, and the settled title re-records the SAME
// private-rect draws every compose — each previously paying a fresh device
// createBuffer+map+copy per draw per record. Route the 288-byte payload
// through the content-hash upload cache instead; identical bytes bind an
// identical buffer, so the draw is bit-identical. The name is generic because
// the cached buffer is shared across command serials by construction.
plume::RenderBuffer* GetOrCreateCachedSyntheticPrivateRectVertexBufferLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command) {
  if (!device || !command.draw.private_texture_corners_valid) {
    return nullptr;
  }
  const float u0 = command.draw.private_texture_uv_override
                       ? command.draw.private_texture_u0
                       : 0.0f;
  const float v0 = command.draw.private_texture_uv_override
                       ? command.draw.private_texture_v0
                       : 0.0f;
  const float u1 = command.draw.private_texture_uv_override
                       ? command.draw.private_texture_u1
                       : 1.0f;
  const float v1 = command.draw.private_texture_uv_override
                       ? command.draw.private_texture_v1
                       : 1.0f;
  const auto vertices = BuildPrivateTextureRectangleDirectVertices(
      command.draw.private_texture_corners, u0, v0, u1, v1);
  const uint32_t byte_size = static_cast<uint32_t>(
      sizeof(FablePrivateRectDirectVertex) * vertices.size());
  const DirectUploadBufferKey key{
      DirectHashBytes(vertices.data(), byte_size), byte_size,
      static_cast<uint32_t>(plume::RenderBufferFlag::VERTEX)};
  if (plume::RenderBuffer* found =
          FindCachedUploadBufferForKeyLocked(key, vertices.data(), byte_size)) {
    ++g_direct_draw_phases.rect_vb_cache_hits;
    return found;
  }
  ++g_direct_draw_phases.rect_vb_cache_creates;
  return GetOrCreateCachedUploadBufferForKeyLocked(
      device, key, vertices.data(), byte_size,
      "Refii Fable cached regenerated private rect VB",
      plume::RenderBufferFlag::VERTEX);
}

bool DirectShaderResourceReadyLocked(uint32_t address) {
  PlumeTrackedResource* resource = FindDirectResourceLocked(address);
  return resource && PlumeGuestShaderReady(*resource);
}

bool DirectBufferResourceReadyLocked(uint32_t address) {
  PlumeTrackedResource* resource = FindDirectResourceLocked(address);
  return resource && IsBufferResource(resource->kind) &&
         DirectTrackedResourceReady(resource);
}

bool DirectTextureIdentityImageReadyLocked(const FableTextureIdentity* identity) {
  if (!identity) {
    return false;
  }
  if (DirectTrackedImageResourceReady(
          FindDirectResourceLocked(identity->object_texture)) ||
      DirectTrackedImageResourceReady(
          FindDirectResourceLocked(identity->parent_texture)) ||
      DirectTrackedImageResourceReady(
          FindDirectResourceLocked(identity->base_address))) {
    return true;
  }
  return false;
}

bool DirectImageResourceReadyLocked(uint32_t address) {
  // DEAD ADDRESS GATE billboard_atlas_image_ready — DELETED 2026-07-26.
  // A readiness short-circuit gated on address being one of 0x4C132C70 /
  // 0x4C132CA4 / 0x4C132CD8 / kFableCanonicalTitleBillboardBase. Run 288
  // census[final] (frame=2952) lists it under tested_but_never_matched: this
  // function is called constantly and the literal never once said yes, so the
  // short-circuit never fired and readiness always came from the tracked/
  // identity paths below. Note the previous comment here reasoned about NOT
  // widening it to the structural predicates — that concern is moot now the
  // measurement shows the narrow form was already answering nothing.
  if (DirectTrackedImageResourceReady(FindDirectResourceLocked(address))) {
    return true;
  }
  if (DirectTextureIdentityImageReadyLocked(
          FableDirectFindTextureIdentityByObject(address)) ||
      DirectTextureIdentityImageReadyLocked(
          FableDirectFindTextureIdentityByBase(address))) {
    return true;
  }
  if (const std::vector<uint32_t>* objects =
          FableDirectFindTextureIdentityObjectsByBase(address)) {
    for (uint32_t object : *objects) {
      if (DirectTextureIdentityImageReadyLocked(
              FableDirectFindTextureIdentityByObject(object))) {
        return true;
      }
    }
  }
  return false;
}

void AddDirectTextureIdentityResourceInterestLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FableTextureIdentity* identity) {
  if (!identity) {
    return;
  }
  AddDirectResourceInterest(resource_filter, identity->object_texture);
  AddDirectResourceInterest(texture_upload_filter, identity->object_texture);
  AddDirectResourceInterest(resource_filter, identity->parent_texture);
  AddDirectResourceInterest(texture_upload_filter, identity->parent_texture);
  // Texture bases are memory/upload keys, not necessarily resource objects.
  // Keep them out of resource_filter so missing draws do not wait forever on
  // raw guest image addresses such as Fable title private texture payloads.
  AddDirectResourceInterest(texture_upload_filter, identity->base_address);
}

void AddDirectTextureIdentityObjectsResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const std::vector<uint32_t>* objects) {
  if (!objects) {
    return;
  }
  for (uint32_t object : *objects) {
    AddDirectResourceInterest(resource_filter, object);
    AddDirectResourceInterest(texture_upload_filter, object);
    AddDirectTextureIdentityResourceInterestLocked(
        resource_filter, texture_upload_filter,
        FableDirectFindTextureIdentityByObject(object));
  }
}

void AddDirectTextureBaseIdentityResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    uint32_t base_address) {
  AddDirectTextureIdentityObjectsResourceInterestsLocked(
      resource_filter, texture_upload_filter,
      FableDirectFindTextureIdentityObjectsByBase(base_address));
}

void AddDirectTextureBaseAddressResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    uint32_t base_address) {
  if (base_address == 0) {
    return;
  }
  AddDirectResourceInterest(texture_upload_filter, base_address);
  AddDirectTextureIdentityResourceInterestLocked(
      resource_filter, texture_upload_filter,
      FableDirectFindTextureIdentityByObject(base_address));
  // Singular ByBase deleted 2026-07-22, same subset-by-construction argument
  // as the two title sites: the call below resolves the SAME base key through
  // ObjectsByBase and adds the identity interest for every object in the list,
  // so SelectUniqueTextureIdentity could only ever re-add one of them.
  // Run 156 measured it at c=358 h=358 u=4 — 358 hits, four singletons, and
  // even those four were already covered by the next line.
  // The ByObject lookup above stays: different key, not a subset.
  AddDirectTextureBaseIdentityResourceInterestsLocked(
      resource_filter, texture_upload_filter, base_address);
}

void AddFableTitleBillboardTileUploadAliasResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    uint32_t base_address, uint32_t width, uint32_t height) {
  if (!IsFableTitleBillboardTileUploadBaseAddress(base_address)) {
    return;
  }
  const uint32_t object =
      FableObservedTitleBillboardTileObjectAddressForBaseLocked(
          base_address, width, height);
  const uint32_t header =
      FableTitleBillboardTileHeaderAddressForObject(object);
  AddDirectResourceInterest(resource_filter, base_address);
  AddDirectResourceInterest(texture_upload_filter, base_address);
  AddDirectResourceInterest(resource_filter, object);
  AddDirectResourceInterest(texture_upload_filter, object);
  AddDirectResourceInterest(resource_filter, header);
  AddDirectResourceInterest(texture_upload_filter, header);
  AddDirectTextureIdentityResourceInterestLocked(
      resource_filter, texture_upload_filter,
      FableDirectFindTextureIdentityByObject(object));
  AddDirectTextureIdentityResourceInterestLocked(
      resource_filter, texture_upload_filter,
      FableDirectFindTextureIdentityByObject(header));
  AddDirectTextureIdentityResourceInterestLocked(
      resource_filter, texture_upload_filter,
      FableDirectFindTextureIdentityByBase(base_address));
}

void AddDirectTextureFetchIdentityResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const std::array<uint32_t, 6>& fetch_dwords) {
  AddDirectTextureIdentityObjectsResourceInterestsLocked(
      resource_filter, texture_upload_filter,
      FableDirectFindTextureIdentityObjectsByFetch(fetch_dwords));
}

void AddDirectTextureFetchResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const std::array<uint32_t, 6>& fetch_dwords) {
  AddDirectTextureIdentityResourceInterestLocked(
      resource_filter, texture_upload_filter,
      FableDirectFindTextureIdentityByFetch(fetch_dwords));
  AddDirectTextureFetchIdentityResourceInterestsLocked(
      resource_filter, texture_upload_filter, fetch_dwords);

  const DirectTextureFetchInfo fetch_info =
      DirectTextureFetchInfoFromDwords(fetch_dwords);
  if (!fetch_info.valid) {
    return;
  }

  const uint32_t base_address = fetch_info.base_address;
  const uint32_t aligned_base_address = base_address & 0xFFFFF000u;
  AddDirectTextureBaseAddressResourceInterestsLocked(
      resource_filter, texture_upload_filter, base_address);

  if (aligned_base_address != base_address) {
    AddDirectTextureBaseAddressResourceInterestsLocked(
        resource_filter, texture_upload_filter, aligned_base_address);
  }
}

// ZERO-CALLER FindDirectPrivateTextureCopyActionForInterestLocked — DELETED
// 2026-07-26. A thin wrapper over
// SelectDirectExactPrivateTextureCopyActionForCommandSlotLocked (which is
// still live and called directly elsewhere). Compiler-verified unused
// (-Wunused-function) plus grep-verified: only its forward declaration and
// definition existed, both in this file, both removed. Not an overload set.


bool PublishDirectBeginVerticesTitleTextureActionForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  const bool begin_vertices_title_wrapper =
      command.draw.private_texture_wrapper_symbol_address ==
      kFableBeginVerticesTitleWrapperSymbol;
  const bool begin_vertices_title_source =
      command.draw.source == RendererDrawEventSource::kSceneObject ||
      command.draw.source == RendererDrawEventSource::kScratchWrapper;
  if (slot >= command.resources.textures.size() ||
      !begin_vertices_title_source ||
      command.draw.draw.indexed || command.draw.draw.primitive_type != 8u ||
      command.draw.draw.primitive_count != 3u ||
      !begin_vertices_title_wrapper ||
      (command.draw.private_texture_wrapper_call_serial == 0 &&
       command.draw.private_texture_wrapper_context_key == 0) ||
      command.draw.source == RendererDrawEventSource::kPixVideo) {
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

  const uint32_t texture_base =
      texture ? PlumeGuestTextureBaseAddress(*texture) : 0u;
  const bool private_title_binding =
      IsFableTitlePrivateTextureProxyAddress(guest) ||
      IsFableMutablePrivateTextureHeaderAddress(guest) ||
      (command_fetch_info.valid &&
       (IsFableTitlePrivateTextureProxyAddress(command_fetch_info.base_address) ||
        IsFableMutablePrivateTextureHeaderAddress(
            command_fetch_info.base_address))) ||
      (texture &&
       (IsFableTitlePrivateTextureProxyAddress(texture->guest_address) ||
        IsFableMutablePrivateTextureHeaderAddress(texture->guest_address) ||
        IsFableTitlePrivateTextureProxyAddress(texture->parent_address) ||
        IsFableMutablePrivateTextureHeaderAddress(texture->parent_address) ||
        IsFableTitlePrivateTextureProxyAddress(texture_base) ||
        IsFableMutablePrivateTextureHeaderAddress(texture_base)));
  const bool title_scene =
      IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
      IsFableTitleSceneVertexShaderCommandLocked(command) ||
      private_title_binding;
  if (!title_scene || !private_title_binding) {
    return false;
  }

  const uint64_t draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  const uint64_t command_fetch_key = FableDirectTextureFetchKey(command_fetch);
  const std::array<uint32_t, 6> texture_fetch =
      texture ? PlumeGuestTextureFetchDwords(*texture)
              : std::array<uint32_t, 6>{};
  const uint64_t texture_fetch_key = FableDirectTextureFetchKey(texture_fetch);
  const FableTextureIdentity* guest_identity =
      FableDirectFindTextureIdentityByObject(guest);
  const FableTextureIdentity* texture_identity =
      texture ? FableDirectFindTextureIdentityByObject(texture->guest_address)
              : nullptr;
  auto begin_vertices_title_action_frame_key = [&]() {
    size_t seed = 0;
    DirectHashCombine(seed, std::hash<uint32_t>{}(
                          g_direct_renderer.current_frame_index));
    DirectHashCombine(seed, std::hash<uint64_t>{}(command.serial));
    DirectHashCombine(seed, std::hash<uint64_t>{}(draw_serial));
    DirectHashCombine(seed, std::hash<uint32_t>{}(slot));
    DirectHashCombine(seed, std::hash<uint32_t>{}(guest));
    DirectHashCombine(seed, std::hash<uint64_t>{}(command_fetch_key));
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
      DirectHashCombine(seed, std::hash<uint32_t>{}(texture_base));
      DirectHashCombine(seed, std::hash<uint64_t>{}(texture_fetch_key));
      DirectHashCombine(seed, std::hash<uint64_t>{}(texture->content_serial));
      DirectHashCombine(seed, std::hash<uint64_t>{}(texture->upload_generation));
      DirectHashCombine(seed, std::hash<uint64_t>{}(texture->draw_content_serial));
    }
    DirectHashCombine(seed, std::hash<uint32_t>{}(command.resources.render_targets[0]));
    DirectHashCombine(seed, std::hash<uint32_t>{}(command.resources.depth_stencil));
    DirectHashCombine(seed, std::hash<uint32_t>{}(command.resources.vertex_shader));
    DirectHashCombine(seed, std::hash<uint32_t>{}(command.resources.pixel_shader));
    DirectHashCombine(seed, std::hash<uint64_t>{}(
                          command.draw.private_texture_wrapper_call_serial));
    DirectHashCombine(seed, std::hash<uint64_t>{}(
                          command.draw.private_texture_wrapper_context_key));
    DirectHashCombine(seed, std::hash<uint32_t>{}(
                          command.draw.private_texture_wrapper_symbol_address));
    DirectHashCombine(seed, std::hash<uint32_t>{}(
                          command.draw.private_texture_wrapper_source_texture));
    DirectHashCombine(seed, std::hash<bool>{}(
                          command.draw.private_texture_wrapper_source_exact));
    MixDirectShaderRoots([&](uint64_t value) {
      DirectHashCombine(seed, std::hash<uint64_t>{}(value));
    }, command);
    return seed != 0 ? static_cast<uint64_t>(seed) : 1ull;
  };
  const uint64_t frame_action_key = begin_vertices_title_action_frame_key();
  if (g_direct_renderer.begin_vertices_title_action_frame_keys.find(
          frame_action_key) !=
      g_direct_renderer.begin_vertices_title_action_frame_keys.end()) {
    const PlumeTrackedResource* title_strip_texture =
        texture ? texture : FindDirectResourceLocked(guest);
    if (DirectResourceMatchesExactBeginVerticesTitleStripFetch(
            title_strip_texture, command_fetch_info, command_fetch) &&
        DirectResourceIsExactBeginVerticesTitleStripSampleLocked(
            command, title_strip_texture)) {
      if (RendererVerboseTracingEnabled() &&
          g_direct_renderer.diagnostics.logged_begin_vertices_title_action_cache_hits <
              32u) {
        REXLOG_INFO(
            "Refii plume exact BeginVertices title action cache hit #{} "
            "frame={} slot={} guest=0x{:08X} sample=0x{:08X}/{}x{} "
            "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++g_direct_renderer.diagnostics.logged_begin_vertices_title_action_cache_hits,
            g_direct_renderer.current_frame_index, slot, guest,
            title_strip_texture->guest_address,
            PlumeGuestTextureWidth(*title_strip_texture),
            PlumeGuestTextureHeight(*title_strip_texture),
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
      return true;
    }
    g_direct_renderer.begin_vertices_title_action_frame_keys.erase(
        frame_action_key);
  }

  auto address_matches_identity =
      [](uint32_t address, const FableTextureIdentity* identity) {
    return address != 0 && identity &&
           (address == identity->object_texture ||
            address == identity->parent_texture ||
            address == identity->base_address);
  };
  auto fetch_matches_identity =
      [](const std::array<uint32_t, 6>& fetch,
         const FableTextureIdentity* identity) {
    return identity && HasTrackedFetchDwords(fetch) &&
           FableDirectTextureFetchKey(fetch) ==
               FableDirectTextureFetchKey(identity->fetch_dwords);
  };
  auto link_texture_matches_command =
      [&](const FableSurfaceTextureLink& link) {
    if (link.texture != 0 &&
        (link.texture == guest ||
         (texture && (link.texture == texture->guest_address ||
                      link.texture == texture->parent_address)) ||
         address_matches_identity(link.texture, guest_identity) ||
         address_matches_identity(link.texture, texture_identity))) {
      return true;
    }
    if (link.texture_base_address != 0 &&
        (link.texture_base_address == guest ||
         (command_fetch_info.valid &&
          link.texture_base_address == command_fetch_info.base_address) ||
         link.texture_base_address == texture_base ||
         address_matches_identity(link.texture_base_address, guest_identity) ||
         address_matches_identity(link.texture_base_address,
                                  texture_identity))) {
      return true;
    }
    const uint64_t link_fetch_key =
        FableDirectTextureFetchKey(link.texture_fetch_dwords);
    return link_fetch_key != 0 &&
           (link_fetch_key == command_fetch_key ||
            link_fetch_key == texture_fetch_key ||
            fetch_matches_identity(link.texture_fetch_dwords, guest_identity) ||
            fetch_matches_identity(link.texture_fetch_dwords,
                                   texture_identity));
  };
  auto link_visible_to_command =
      [&](const FableSurfaceTextureLink* link) {
    if (!link || !link->trusted_destination_edge ||
        link->kind == FablePrivateTextureCopyKind::kDepth ||
        link->source_surface == 0) {
      return false;
    }
    const bool texture_matches = link_texture_matches_command(*link);
    if (!texture_matches) {
      return false;
    }
    if (private_title_binding &&
        DirectSurfaceTextureLinkIsBeginVerticesTitleStripUpload(*link)) {
      return false;
    }
    const bool same_or_unknown_slot =
        link->source_slot == 0xFFFFFFFFu || link->source_slot == slot;
    const bool exact_title_slot_alias =
        private_title_binding &&
        DirectSurfaceTextureLinkHasExactCopyIdentity(*link) &&
        DirectSurfaceTextureLinkPrivateWrapperVisibleToCommandLocked(command,
                                                                    *link) &&
        DirectSurfaceTextureLinkTouchesFablePrivateChain(*link, texture);
    if (!same_or_unknown_slot && !exact_title_slot_alias) {
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
    PlumeTrackedResource* surface = FindDirectResourceLocked(link->source_surface);
    const bool link_has_registered_content =
        link->target_content_serial != 0 || link->backing_content_serial != 0 ||
        link->sampled_source.kind == FableRegisteredSampleSourceKind::kGpuSurface ||
        (surface && FableTrackedResourceHasGpuProducedContent(*surface));
    return surface && IsImageResource(surface->kind) &&
           PlumeGuestImageReady(*surface) && link_has_registered_content &&
           !DirectCommandBindsResourceAsTargetLocked(command, *surface);
  };
  auto best_link =
      [&](const FableSurfaceTextureLink* lhs,
          const FableSurfaceTextureLink* rhs) -> const FableSurfaceTextureLink* {
    if (!link_visible_to_command(lhs)) {
      return rhs;
    }
    if (!link_visible_to_command(rhs)) {
      return lhs;
    }
    const auto slot_rank = [&](const FableSurfaceTextureLink* link) {
      if (link->source_slot == slot) {
        return 0u;
      }
      if (link->source_slot == 0xFFFFFFFFu) {
        return 1u;
      }
      return 2u;
    };
    const uint32_t lhs_slot_rank = slot_rank(lhs);
    const uint32_t rhs_slot_rank = slot_rank(rhs);
    if (lhs_slot_rank != rhs_slot_rank) {
      return lhs_slot_rank < rhs_slot_rank ? lhs : rhs;
    }
    if (lhs->frame_index != rhs->frame_index) {
      return lhs->frame_index > rhs->frame_index ? lhs : rhs;
    }
    if (lhs->draw_serial != rhs->draw_serial) {
      return lhs->draw_serial > rhs->draw_serial ? lhs : rhs;
    }
    return lhs->serial >= rhs->serial ? lhs : rhs;
  };

  const FableSurfaceTextureLink* link = nullptr;
  std::array<FableSurfaceTextureLink, 16> alias_link_snapshots = {};
  FableSurfaceTextureLink begin_vertices_current_target_link = {};
  uint32_t alias_link_snapshot_count = 0;
  auto consider_link = [&](const FableSurfaceTextureLink* candidate) {
    link = best_link(candidate, link);
  };
  auto consider_exact_relation = [&](uint32_t destination_texture) {
    if (destination_texture == 0) {
      return;
    }
    if (const FableSurfaceDestinationTextureRelation* relation =
            FableDirectFindVisibleSurfaceDestinationRelationForDestinationTexture(
                destination_texture, g_direct_renderer.current_frame_index,
                draw_serial, slot)) {
      consider_link(&relation->link);
    }
    if (const FableSurfaceDestinationTextureRelation* relation =
            FableDirectFindSurfaceDestinationRelationForDestinationTextureSlot(
                destination_texture, slot)) {
      consider_link(&relation->link);
    }
    if (const FableSurfaceDestinationTextureRelation* relation =
            FableDirectFindSurfaceDestinationRelationForDestinationTexture(
                destination_texture)) {
      consider_link(&relation->link);
    }
  };
  auto consider_exact_alias = [&](uint32_t destination_texture) {
    RendererSurfaceCopyAliasSnapshot alias;
    if (destination_texture == 0 ||
        !FindDirectSurfaceCopyAliasByDestinationAddressLocked(
            destination_texture, &alias) ||
        !DirectSurfaceCopyAliasHasExactIdentity(alias)) {
      return;
    }
    if (const FableSurfaceTextureLink* exact_link =
            FindDirectSurfaceTextureLinkByAliasLocked(alias)) {
      consider_link(exact_link);
      return;
    }
    if (alias_link_snapshot_count >= alias_link_snapshots.size()) {
      return;
    }
    FableSurfaceTextureLink& alias_link =
        alias_link_snapshots[alias_link_snapshot_count++];
    alias_link = {};
    alias_link.copy_call_serial = alias.copy_call_serial;
    alias_link.copy_context_key = alias.copy_context_key;
    alias_link.copy_symbol_address = alias.copy_symbol_address;
    alias_link.source_slot = 0xFFFFFFFFu;
    alias_link.trusted_destination_edge = true;
    alias_link.texture = alias.destination_surface;
    alias_link.texture_base_address =
        command_fetch_info.valid ? command_fetch_info.base_address
                                 : texture_base;
    alias_link.texture_width =
        command_fetch_info.valid
            ? command_fetch_info.width
            : (texture ? PlumeGuestTextureWidth(*texture) : 0u);
    alias_link.texture_height =
        command_fetch_info.valid
            ? command_fetch_info.height
            : (texture ? PlumeGuestTextureHeight(*texture) : 0u);
    alias_link.texture_format =
        command_fetch_info.valid
            ? command_fetch_info.format
            : (texture ? PlumeGuestTextureGuestFormat(*texture) : 0u);
    alias_link.texture_fetch_dwords =
        HasTrackedFetchDwords(command_fetch)
            ? command_fetch
            : (texture ? PlumeGuestTextureFetchDwords(*texture)
                       : std::array<uint32_t, 6>{});
    alias_link.source_surface = alias.source_surface;
    alias_link.render_target = command.resources.render_targets[0];
    alias_link.depth_stencil = command.resources.depth_stencil;
    alias_link.vertex_shader = command.resources.vertex_shader;
    alias_link.pixel_shader = command.resources.pixel_shader;
    PopulateDirectSurfaceTextureLinkShaderRoots(alias_link, command);
    alias_link.frame_index = g_direct_renderer.current_frame_index;
    alias_link.draw_serial = draw_serial;
    alias_link.kind = FablePrivateTextureCopyKind::kColor;
    if (PlumeTrackedResource* surface =
            FindDirectResourceLocked(alias_link.source_surface)) {
      alias_link.target_content_serial =
          FableTrackedResourceVisibleContentSerial(*surface);
      if (alias_link.target_content_serial != 0) {
        alias_link.sampled_source.kind =
            FableRegisteredSampleSourceKind::kGpuSurface;
        alias_link.sampled_source.resource = alias_link.source_surface;
        alias_link.sampled_source.content_serial =
            alias_link.target_content_serial;
      }
    }
    consider_link(&alias_link);
  };
  auto consider_destination = [&](uint32_t destination_texture) {
    if (destination_texture == 0) {
      return;
    }
    consider_exact_alias(destination_texture);
    consider_exact_relation(destination_texture);
    consider_link(FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
        destination_texture, slot));
    consider_link(
        FableDirectFindSurfaceTextureLinkForDestinationTexture(destination_texture));
    if (const std::vector<FableSurfaceTextureLink>* destination_links =
            FableDirectFindSurfaceTextureLinksForDestinationTexture(
                destination_texture)) {
      for (auto it = destination_links->rbegin();
           it != destination_links->rend(); ++it) {
        consider_link(&*it);
      }
    }
  };
  consider_destination(guest);
  if (texture) {
    consider_destination(texture->guest_address);
    consider_destination(texture->parent_address);
  }
  if (guest_identity) {
    consider_destination(guest_identity->object_texture);
    consider_destination(guest_identity->parent_texture);
  }
  if (texture) {
    const FableTextureIdentity* identity =
        FableDirectFindTextureIdentityByObject(texture->guest_address);
    if (identity) {
      consider_destination(identity->object_texture);
      consider_destination(identity->parent_texture);
    }
  }

  auto remember_exact_title_strip_sample_without_link = [&]() {
    const PlumeTrackedResource* title_strip_texture =
        texture ? texture : FindDirectResourceLocked(guest);
    if (!title_strip_texture || !command_fetch_info.valid ||
        command_fetch_info.base_address != 0x1FAA0000u ||
        command_fetch_info.width != 512u ||
        command_fetch_info.height != 1u ||
        command_fetch_info.format != 36u ||
        !DirectResourceIsExactBeginVerticesTitleStripSampleLocked(
            command, title_strip_texture)) {
      return false;
    }

    g_direct_renderer.begin_vertices_title_action_frame_keys.insert(
        frame_action_key);
    static uint32_t logged_begin_vertices_title_strip_samples = 0;
    if (logged_begin_vertices_title_strip_samples < 32u) {
      REXLOG_INFO(
          "Refii plume exact BeginVertices title strip texture identity #{} "
          "frame={} serial={} draw={} slot={} guest=0x{:08X} "
          "base=0x{:08X} texture=0x{:08X}/{}x{} fmt={} call={} "
          "context=0x{:016X} rt=0x{:08X} ds=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X} "
          "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
          ++logged_begin_vertices_title_strip_samples,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.serial),
          static_cast<unsigned long long>(draw_serial), slot, guest,
          command_fetch_info.base_address, title_strip_texture->guest_address,
          PlumeGuestTextureWidth(*title_strip_texture),
          PlumeGuestTextureHeight(*title_strip_texture),
          PlumeGuestTextureGuestFormat(*title_strip_texture),
          static_cast<unsigned long long>(
              command.draw.private_texture_wrapper_call_serial),
          static_cast<unsigned long long>(
              command.draw.private_texture_wrapper_context_key),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader,
          command_fetch[0], command_fetch[1], command_fetch[2],
          command_fetch[3], command_fetch[4], command_fetch[5]);
    }
    return true;
  };

  if (!link && remember_exact_title_strip_sample_without_link()) {
    return true;
  }

  auto action_endpoint_matches_requested_sample =
      [&](uint32_t action_texture, uint32_t action_base,
          const std::array<uint32_t, 6>& action_fetch) {
    if (action_texture != 0 &&
        (action_texture == guest ||
         (texture && (action_texture == texture->guest_address ||
                      action_texture == texture->parent_address)) ||
         address_matches_identity(action_texture, guest_identity) ||
         address_matches_identity(action_texture, texture_identity))) {
      return true;
    }
    if (action_base != 0 &&
        (action_base == guest || action_base == texture_base ||
         (command_fetch_info.valid &&
          action_base == command_fetch_info.base_address) ||
         address_matches_identity(action_base, guest_identity) ||
         address_matches_identity(action_base, texture_identity))) {
      return true;
    }
    const uint64_t action_fetch_key =
        FableDirectTextureFetchKey(action_fetch);
    return action_fetch_key != 0 &&
           (action_fetch_key == command_fetch_key ||
            action_fetch_key == texture_fetch_key ||
            fetch_matches_identity(action_fetch, guest_identity) ||
            fetch_matches_identity(action_fetch, texture_identity));
  };
  auto consider_exact_sibling_alias_source =
      [&](uint32_t destination_texture) {
    if (link || destination_texture == 0 ||
        alias_link_snapshot_count >= alias_link_snapshots.size()) {
      return;
    }
    RendererSurfaceCopyAliasSnapshot alias = {};
    if (!FindDirectSurfaceCopyAliasByDestinationAddressLocked(destination_texture,
                                                             &alias) ||
        !DirectSurfaceCopyAliasHasExactIdentity(alias) ||
        !action_endpoint_matches_requested_sample(alias.destination_surface, 0,
                                                  std::array<uint32_t, 6>{})) {
      return;
    }
    const bool exact_sibling_alias =
        alias.copy_symbol_address == kFablePrivateRectSiblingWrapperSymbol ||
        alias.copy_symbol_address == kFablePrivateRectSiblingWrapperCallsite;
    if (!exact_sibling_alias) {
      return;
    }
    PlumeTrackedResource* source =
        FindDirectResourceLocked(alias.source_surface);
    if (!source || !IsImageResource(source->kind) ||
        !PlumeGuestImageReady(*source) ||
        DirectCommandBindsResourceAsTargetLocked(command, *source) ||
        (!FableTrackedResourceHasSampleableContent(*source) &&
         !FableTrackedResourceHasGpuProducedContent(*source))) {
      return;
    }
    const uint64_t source_content_serial =
        FableTrackedResourceVisibleContentSerial(*source);
    if (source_content_serial == 0) {
      return;
    }

    FableSurfaceTextureLink& alias_link =
        alias_link_snapshots[alias_link_snapshot_count++];
    alias_link = {};
    alias_link.copy_call_serial = alias.copy_call_serial;
    alias_link.copy_context_key = alias.copy_context_key;
    alias_link.copy_symbol_address = alias.copy_symbol_address;
    alias_link.source_slot = 0xFFFFFFFFu;
    alias_link.trusted_destination_edge = true;
    alias_link.texture = alias.destination_surface;
    alias_link.texture_base_address =
        command_fetch_info.valid ? command_fetch_info.base_address
                                 : texture_base;
    alias_link.texture_width =
        command_fetch_info.valid
            ? command_fetch_info.width
            : (texture ? PlumeGuestTextureWidth(*texture) : 0u);
    alias_link.texture_height =
        command_fetch_info.valid
            ? command_fetch_info.height
            : (texture ? PlumeGuestTextureHeight(*texture) : 0u);
    alias_link.texture_format =
        command_fetch_info.valid
            ? command_fetch_info.format
            : (texture ? PlumeGuestTextureGuestFormat(*texture) : 0u);
    alias_link.texture_fetch_dwords =
        HasTrackedFetchDwords(command_fetch)
            ? command_fetch
            : (texture ? PlumeGuestTextureFetchDwords(*texture)
                       : std::array<uint32_t, 6>{});
    alias_link.source_surface = alias.source_surface;
    alias_link.render_target = command.resources.render_targets[0];
    alias_link.depth_stencil = command.resources.depth_stencil;
    alias_link.vertex_shader = command.resources.vertex_shader;
    alias_link.pixel_shader = command.resources.pixel_shader;
    PopulateDirectSurfaceTextureLinkShaderRoots(alias_link, command);
    alias_link.frame_index = g_direct_renderer.current_frame_index;
    alias_link.draw_serial = draw_serial;
    alias_link.kind = FablePrivateTextureCopyKind::kColor;
    alias_link.target_content_serial = source_content_serial;
    alias_link.sampled_source.kind =
        FableRegisteredSampleSourceKind::kGpuSurface;
    alias_link.sampled_source.resource = source->guest_address;
    alias_link.sampled_source.content_serial = source_content_serial;
    link = &alias_link;

    static uint32_t logged_begin_vertices_sibling_alias_sources = 0;
    if (logged_begin_vertices_sibling_alias_sources < 32u) {
      REXLOG_INFO(
          "Refii plume exact BeginVertices sibling alias source #{} frame={} "
          "slot={} guest=0x{:08X} destination=0x{:08X} "
          "source=0x{:08X}/{}x{} call={} context=0x{:016X} "
          "symbol=0x{:08X} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
          "ps=0x{:08X}",
          ++logged_begin_vertices_sibling_alias_sources,
          g_direct_renderer.current_frame_index, slot, guest,
          alias.destination_surface, source->guest_address,
          PlumeGuestTextureWidth(*source), PlumeGuestTextureHeight(*source),
          static_cast<unsigned long long>(alias.copy_call_serial),
          static_cast<unsigned long long>(alias.copy_context_key),
          alias.copy_symbol_address, command.resources.render_targets[0],
          command.resources.depth_stencil, command.resources.vertex_shader,
          command.resources.pixel_shader);
    }
  };
  consider_exact_sibling_alias_source(guest);
  if (!link && texture) {
    consider_exact_sibling_alias_source(texture->guest_address);
    consider_exact_sibling_alias_source(texture->parent_address);
  }
  if (!link && guest_identity) {
    consider_exact_sibling_alias_source(guest_identity->object_texture);
    consider_exact_sibling_alias_source(guest_identity->parent_texture);
  }
  if (!link && private_title_binding &&
      command.resources.render_targets[0] != 0) {
    PlumeTrackedResource* current_target =
        FindDirectResourceLocked(command.resources.render_targets[0]);
    const uint64_t current_target_content =
        current_target ? FableTrackedResourceVisibleContentSerial(*current_target) : 0ull;
    if (current_target && IsImageResource(current_target->kind) &&
        PlumeGuestImageReady(*current_target) &&
        current_target->texture_render_target &&
        !current_target->texture_depth_target &&
        DirectCommandBindsResourceAsTargetLocked(command, *current_target) &&
        current_target_content != 0) {
      const uint32_t requested_texture =
          guest != 0 ? guest : (texture ? texture->guest_address : 0u);
      const uint32_t requested_width =
          command_fetch_info.valid
              ? command_fetch_info.width
              : (texture ? PlumeGuestTextureWidth(*texture) : 0u);
      const uint32_t requested_height =
          command_fetch_info.valid
              ? command_fetch_info.height
              : (texture ? PlumeGuestTextureHeight(*texture) : 0u);
      const bool requested_texture_is_current_target =
          DirectResourceAddressMatchesLocked(requested_texture,
                                             current_target->guest_address) ||
          DirectResourceAddressMatchesLocked(requested_texture,
                                             current_target->parent_address) ||
          DirectResourceAddressMatchesLocked(
              requested_texture, command.resources.render_targets[0]) ||
          (texture &&
           (DirectResourceAddressMatchesLocked(texture->guest_address,
                                               current_target->guest_address) ||
            DirectResourceAddressMatchesLocked(texture->parent_address,
                                               current_target->guest_address) ||
            DirectResourceAddressMatchesLocked(texture_base,
                                               current_target->guest_address)));
      const uint32_t current_target_base =
          PlumeGuestTextureBaseAddress(*current_target);
      const bool requested_fetch_base_is_current_target =
          command_fetch_info.valid &&
          (DirectResourceAddressMatchesLocked(command_fetch_info.base_address,
                                              current_target->guest_address) ||
           DirectResourceAddressMatchesLocked(command_fetch_info.base_address,
                                              current_target->parent_address) ||
           DirectResourceAddressMatchesLocked(command_fetch_info.base_address,
                                              current_target_base));
      const uint64_t command_fetch_key =
          FableDirectTextureFetchKey(command_fetch);
      const uint64_t current_target_fetch_key =
          FableDirectTextureFetchKey(
              PlumeGuestTextureFetchDwords(*current_target));
      const bool requested_fetch_is_current_target =
          command_fetch_key != 0 && current_target_fetch_key != 0 &&
          command_fetch_key == current_target_fetch_key;
      const bool requested_dimensions_match_current_target =
          requested_width != 0 && requested_height != 0 &&
          requested_width == PlumeGuestTextureWidth(*current_target) &&
          requested_height == PlumeGuestTextureHeight(*current_target);
      const bool current_target_can_source_requested_texture =
          requested_texture_is_current_target ||
          requested_fetch_base_is_current_target ||
          requested_fetch_is_current_target;
      if (!current_target_can_source_requested_texture &&
          requested_dimensions_match_current_target) {
        static uint32_t logged_begin_vertices_current_target_link_rejects = 0;
        if (logged_begin_vertices_current_target_link_rejects < 32u) {
          REXLOG_INFO(
              "Refii plume exact BeginVertices current-target link reject #{} "
              "frame={} slot={} guest=0x{:08X} base=0x{:08X} "
              "requested={}x{} surface=0x{:08X}/{}x{} content={} "
              "call={} context=0x{:016X} wrapper_source=0x{:08X}/{} "
              "fetch_match={} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
              "ps=0x{:08X}",
              ++logged_begin_vertices_current_target_link_rejects,
              g_direct_renderer.current_frame_index, slot, guest,
              command_fetch_info.valid ? command_fetch_info.base_address
                                       : texture_base,
              requested_width, requested_height, current_target->guest_address,
              PlumeGuestTextureWidth(*current_target),
              PlumeGuestTextureHeight(*current_target),
              static_cast<unsigned long long>(current_target_content),
              static_cast<unsigned long long>(
                  command.draw.private_texture_wrapper_call_serial),
              static_cast<unsigned long long>(
                  command.draw.private_texture_wrapper_context_key),
              requested_texture, requested_texture_is_current_target ? 1 : 0,
              requested_fetch_is_current_target ? 1 : 0,
              command.resources.render_targets[0],
              command.resources.depth_stencil, command.resources.vertex_shader,
              command.resources.pixel_shader);
        }
      }
      if (!current_target_can_source_requested_texture) {
        current_target = nullptr;
      }
      if (current_target) {
        begin_vertices_current_target_link = {};
        begin_vertices_current_target_link.copy_call_serial =
            command.draw.private_texture_wrapper_call_serial;
        begin_vertices_current_target_link.copy_context_key =
            command.draw.private_texture_wrapper_context_key;
        begin_vertices_current_target_link.copy_symbol_address =
            command.draw.private_texture_wrapper_symbol_address;
        begin_vertices_current_target_link.wrapper_source_texture =
            requested_texture;
        begin_vertices_current_target_link.wrapper_source_exact =
            begin_vertices_current_target_link.wrapper_source_texture != 0;
        begin_vertices_current_target_link.source_slot = slot;
        begin_vertices_current_target_link.trusted_destination_edge = true;
        begin_vertices_current_target_link.texture = requested_texture;
        begin_vertices_current_target_link.texture_base_address =
            command_fetch_info.valid ? command_fetch_info.base_address
                                     : texture_base;
        begin_vertices_current_target_link.texture_width = requested_width;
        begin_vertices_current_target_link.texture_height = requested_height;
      begin_vertices_current_target_link.texture_format =
          command_fetch_info.valid
              ? command_fetch_info.format
              : (texture ? PlumeGuestTextureGuestFormat(*texture) : 0u);
      begin_vertices_current_target_link.texture_fetch_dwords =
          HasTrackedFetchDwords(command_fetch)
              ? command_fetch
              : (texture ? PlumeGuestTextureFetchDwords(*texture)
                         : std::array<uint32_t, 6>{});
      begin_vertices_current_target_link.source_surface =
          current_target->guest_address;
      begin_vertices_current_target_link.render_target =
          command.resources.render_targets[0];
      begin_vertices_current_target_link.depth_stencil =
          command.resources.depth_stencil;
      begin_vertices_current_target_link.vertex_shader =
          command.resources.vertex_shader;
      begin_vertices_current_target_link.pixel_shader =
          command.resources.pixel_shader;
      PopulateDirectSurfaceTextureLinkShaderRoots(
          begin_vertices_current_target_link, command);
      begin_vertices_current_target_link.frame_index =
          g_direct_renderer.current_frame_index;
      begin_vertices_current_target_link.draw_serial = draw_serial;
      begin_vertices_current_target_link.kind =
          FablePrivateTextureCopyKind::kColor;
      begin_vertices_current_target_link.target_content_serial =
          current_target_content;
      begin_vertices_current_target_link.sampled_source.kind =
          FableRegisteredSampleSourceKind::kGpuSurface;
      begin_vertices_current_target_link.sampled_source.resource =
          current_target->guest_address;
      begin_vertices_current_target_link.sampled_source.content_serial =
          current_target_content;
      link = &begin_vertices_current_target_link;

      static uint32_t logged_begin_vertices_current_target_links = 0;
      if (logged_begin_vertices_current_target_links < 32u) {
        REXLOG_INFO(
            "Refii plume exact BeginVertices current-target link #{} "
            "frame={} slot={} guest=0x{:08X} base=0x{:08X} "
            "surface=0x{:08X}/{}x{} content={} call={} "
            "context=0x{:016X} wrapper_source=0x{:08X}/{} "
            "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_begin_vertices_current_target_links,
            g_direct_renderer.current_frame_index, slot, guest,
            begin_vertices_current_target_link.texture_base_address,
            current_target->guest_address,
            PlumeGuestTextureWidth(*current_target),
            PlumeGuestTextureHeight(*current_target),
            static_cast<unsigned long long>(current_target_content),
            static_cast<unsigned long long>(
                command.draw.private_texture_wrapper_call_serial),
            static_cast<unsigned long long>(
                command.draw.private_texture_wrapper_context_key),
            begin_vertices_current_target_link.wrapper_source_texture,
            begin_vertices_current_target_link.wrapper_source_exact ? 1 : 0,
            command.resources.render_targets[0],
            command.resources.depth_stencil, command.resources.vertex_shader,
            command.resources.pixel_shader);
      }
      }
    }
  }

  if (!link && private_title_binding &&
      IsFableTitleSceneTextureSyncCommandLocked(command)) {
    PlumeTrackedResource* title_sync_sample =
        ResolveDirectSampledTextureFromFetchConstantsLocked(command, slot);
    if (title_sync_sample &&
        DirectTitleSyncFetchTextureMatchesCommandLocked(command, slot,
                                                        *title_sync_sample) &&
        !DirectCommandBindsResourceAsTargetLocked(command, *title_sync_sample) &&
        alias_link_snapshot_count < alias_link_snapshots.size()) {
      FableSurfaceTextureLink& title_sync_link =
          alias_link_snapshots[alias_link_snapshot_count++];
      title_sync_link = {};
      title_sync_link.copy_call_serial =
          command.draw.private_texture_wrapper_call_serial;
      title_sync_link.copy_context_key =
          command.draw.private_texture_wrapper_context_key;
      title_sync_link.copy_symbol_address =
          command.draw.private_texture_wrapper_symbol_address;
      title_sync_link.wrapper_source_texture =
          guest != 0 ? guest : title_sync_sample->guest_address;
      title_sync_link.wrapper_source_exact =
          title_sync_link.wrapper_source_texture != 0;
      title_sync_link.source_slot = slot;
      title_sync_link.trusted_destination_edge = true;
      title_sync_link.texture =
          guest != 0 ? guest : title_sync_sample->guest_address;
      title_sync_link.texture_base_address =
          command_fetch_info.valid
              ? command_fetch_info.base_address
              : PlumeGuestTextureBaseAddress(*title_sync_sample);
      title_sync_link.texture_width =
          command_fetch_info.valid ? command_fetch_info.width
                                   : PlumeGuestTextureWidth(*title_sync_sample);
      title_sync_link.texture_height =
          command_fetch_info.valid ? command_fetch_info.height
                                   : PlumeGuestTextureHeight(*title_sync_sample);
      title_sync_link.texture_format =
          command_fetch_info.valid
              ? command_fetch_info.format
              : PlumeGuestTextureGuestFormat(*title_sync_sample);
      title_sync_link.texture_fetch_dwords =
          HasTrackedFetchDwords(command_fetch)
              ? command_fetch
              : PlumeGuestTextureFetchDwords(*title_sync_sample);
      title_sync_link.source_surface = title_sync_sample->guest_address;
      title_sync_link.render_target = command.resources.render_targets[0];
      title_sync_link.depth_stencil = command.resources.depth_stencil;
      title_sync_link.vertex_shader = command.resources.vertex_shader;
      title_sync_link.pixel_shader = command.resources.pixel_shader;
      PopulateDirectSurfaceTextureLinkShaderRoots(title_sync_link, command);
      title_sync_link.frame_index = g_direct_renderer.current_frame_index;
      title_sync_link.draw_serial = draw_serial;
      title_sync_link.kind = FablePrivateTextureCopyKind::kColor;
      title_sync_link.target_content_serial =
          FableTrackedResourceVisibleContentSerial(*title_sync_sample);
      title_sync_link.sampled_source.kind =
          FableRegisteredSampleSourceKind::kGpuSurface;
      title_sync_link.sampled_source.resource = title_sync_sample->guest_address;
      title_sync_link.sampled_source.content_serial =
          title_sync_link.target_content_serial;
      link = &title_sync_link;
    }
  }
  if (!link) {
    return false;
  }

  const PlumeTrackedResource* snapshot_source = texture;
  if (!snapshot_source && guest != 0) {
    snapshot_source = FindDirectResourceLocked(guest);
  }
  const bool publish_current_snapshot_edge =
      DirectCommandCanSnapshotPrivateTextureFeedbackLocked(command, slot,
                                                           snapshot_source);
  PlumeTrackedResource* snapshot_target =
      publish_current_snapshot_edge
          ? FindDirectResourceLocked(command.resources.render_targets[0])
          : nullptr;
  const bool snapshot_target_valid =
      snapshot_target && IsImageResource(snapshot_target->kind) &&
      PlumeGuestImageReady(*snapshot_target) &&
      DirectCommandBindsResourceAsTargetLocked(command, *snapshot_target) &&
      FableTrackedResourceVisibleContentSerial(*snapshot_target) != 0;
  const bool use_current_snapshot_target =
      publish_current_snapshot_edge && snapshot_target_valid;

  uint64_t action_key = 1469598103934665603ull;
  auto mix_action_key = [&](uint64_t value) {
    action_key ^= value;
    action_key *= 1099511628211ull;
  };
  mix_action_key(g_direct_renderer.current_frame_index);
  mix_action_key(draw_serial);
  mix_action_key(command.draw.private_texture_wrapper_call_serial);
  mix_action_key(command.draw.private_texture_wrapper_context_key);
  mix_action_key(command.draw.private_texture_wrapper_symbol_address);
  mix_action_key(command.draw.private_texture_wrapper_source_texture);
  mix_action_key(command.draw.private_texture_wrapper_source_exact ? 1u : 0u);
  mix_action_key(slot);
  mix_action_key(guest);
  mix_action_key(command_fetch_key);
  mix_action_key(use_current_snapshot_target ? snapshot_target->guest_address
                                             : link->source_surface);
  mix_action_key(link->target_content_serial);
  mix_action_key(command.resources.render_targets[0]);
  mix_action_key(command.resources.depth_stencil);
  mix_action_key(command.resources.vertex_shader);
  mix_action_key(command.resources.pixel_shader);
  MixDirectShaderRoots(mix_action_key, command);
  auto remember_exact_title_strip_sample = [&]() {
    const uint32_t source_base =
        command_fetch_info.valid
            ? command_fetch_info.base_address
            : (link->texture_base_address != 0 ? link->texture_base_address
                                               : texture_base);
    const uint32_t source_width =
        command_fetch_info.valid
            ? command_fetch_info.width
            : (link->texture_width != 0
                   ? link->texture_width
                   : (texture ? PlumeGuestTextureWidth(*texture) : 0u));
    const uint32_t source_height =
        command_fetch_info.valid
            ? command_fetch_info.height
            : (link->texture_height != 0
                   ? link->texture_height
                   : (texture ? PlumeGuestTextureHeight(*texture) : 0u));
    const uint32_t source_format =
        command_fetch_info.valid
            ? command_fetch_info.format
            : (link->texture_format != 0
                   ? link->texture_format
                   : (texture ? PlumeGuestTextureGuestFormat(*texture) : 0u));
    const PlumeTrackedResource* title_strip_texture =
        texture ? texture : FindDirectResourceLocked(guest);
    if (!title_strip_texture || source_base != 0x1FAA0000u ||
        source_width != 512u || source_height != 1u ||
        source_format != 36u ||
        !DirectResourceIsExactBeginVerticesTitleStripSampleLocked(
            command, title_strip_texture)) {
      return false;
    }
    return true;
  };
  static std::unordered_set<uint64_t> published_action_keys;
  if (published_action_keys.size() >= 512u) {
    published_action_keys.clear();
  }
  if (!published_action_keys.insert(action_key).second) {
    remember_exact_title_strip_sample();
    g_direct_renderer.begin_vertices_title_action_frame_keys.insert(
        frame_action_key);
    return true;
  }

  PlumeTrackedResource* surface =
      use_current_snapshot_target ? snapshot_target
                                  : FindDirectResourceLocked(link->source_surface);
  if (!surface) {
    return false;
  }

  FablePrivateTextureCopyAction action;
  action.source_slot = slot;
  action.source_texture = guest != 0 ? guest : link->texture;
  action.source_base_address =
      command_fetch_info.valid
          ? command_fetch_info.base_address
          : (link->texture_base_address != 0 ? link->texture_base_address
                                             : texture_base);
  action.source_width = command_fetch_info.valid
                            ? command_fetch_info.width
                            : (link->texture_width != 0
                                   ? link->texture_width
                                   : (texture ? PlumeGuestTextureWidth(*texture)
                                              : 0u));
  action.source_height =
      command_fetch_info.valid
          ? command_fetch_info.height
          : (link->texture_height != 0
                 ? link->texture_height
                 : (texture ? PlumeGuestTextureHeight(*texture) : 0u));
  action.source_format =
      command_fetch_info.valid
          ? command_fetch_info.format
          : (link->texture_format != 0
                 ? link->texture_format
                 : (texture ? PlumeGuestTextureGuestFormat(*texture) : 0u));
  action.source_fetch_dwords =
      HasTrackedFetchDwords(command_fetch)
          ? command_fetch
          : (HasTrackedFetchDwords(link->texture_fetch_dwords)
                 ? link->texture_fetch_dwords
                 : texture_fetch);
  PopulateDirectPrivateTextureCopyTargetLocked(action, *surface);
  action.target_content_serial =
      link->target_content_serial != 0
          ? link->target_content_serial
          : FableTrackedResourceVisibleContentSerial(*surface);
  action.backing_texture = link->backing_texture;
  action.backing_base_address = link->backing_base_address;
  action.backing_fetch_dwords = link->backing_fetch_dwords;
  action.backing_content_serial = link->backing_content_serial;
  if (use_current_snapshot_target) {
    action.backing_texture = 0;
    action.backing_base_address = 0;
    action.backing_fetch_dwords = {};
    action.backing_content_serial = 0;
  }
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
  PopulateDirectPrivateTextureWrapperSourceIdentity(action, command, link);
  action.frame_index = g_direct_renderer.current_frame_index;
  action.draw_serial = draw_serial;
  action.kind = FablePrivateTextureCopyKind::kColor;
  action.sampled_source = link->sampled_source;
  const PlumeTrackedResource* title_strip_texture =
      texture ? texture : FindDirectResourceLocked(guest);
  const bool exact_title_strip_sample =
      title_strip_texture && action.source_base_address == 0x1FAA0000u &&
      action.source_width == 512u && action.source_height == 1u &&
      action.source_format == 36u &&
      DirectResourceIsExactBeginVerticesTitleStripSampleLocked(
          command, title_strip_texture);
  if (exact_title_strip_sample) {
    action.sampled_source = {};
    action.sampled_source.kind = FableRegisteredSampleSourceKind::kGuestTexture;
    action.sampled_source.resource = title_strip_texture->guest_address;
    action.sampled_source.base_address =
        PlumeGuestTextureBaseAddress(*title_strip_texture);
    action.sampled_source.fetch_dwords =
        PlumeGuestTextureFetchDwords(*title_strip_texture);
    action.sampled_source.content_serial =
        FableTrackedResourceVisibleContentSerial(*title_strip_texture);
  }

  RegisterDirectPrivateTextureCopyActionLocked(action);
  RememberDirectPrivateRectExactActionLocked(action);
  RegisterDirectSurfaceTextureLinkForPrivateCopyActionLocked(
      action, "beginvertices-title-action");
  TrackDirectExactSurfaceAliasForPrivateCopyActionLocked(
      action, "beginvertices-title-action");
  RememberDirectPrivateTextureAliasIndexLocked(
      action.source_texture, action.source_base_address,
      action.source_fetch_dwords, *surface);
  RememberDirectMutablePrivateHeaderExactSampleLocked(
      command, slot, action, *surface, "beginvertices-title-action");
  g_direct_renderer.begin_vertices_title_action_frame_keys.insert(
      frame_action_key);

  static uint32_t logged_begin_vertices_title_actions = 0;
  if (RendererVerboseTracingEnabled() &&
      logged_begin_vertices_title_actions < 48u) {
    REXLOG_INFO(
        "Refii plume exact BeginVertices title action #{} frame={} "
        "slot={} texture=0x{:08X}/{}x{} base=0x{:08X} "
        "surface=0x{:08X}/{}x{} call={} context=0x{:016X} "
	        "symbol=0x{:08X} wrapper_source=0x{:08X}/{} target_serial={} "
	        "sampled={} sample_resource=0x{:08X} sample_serial={} snapshot={} "
	        "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
	        ++logged_begin_vertices_title_actions,
	        g_direct_renderer.current_frame_index, slot, action.source_texture,
        action.source_width, action.source_height, action.source_base_address,
        action.target_surface, action.target_width, action.target_height,
	        static_cast<unsigned long long>(action.wrapper_call_serial),
	        static_cast<unsigned long long>(action.wrapper_context_key),
	        action.wrapper_symbol_address, action.wrapper_source_texture,
	        action.wrapper_source_exact ? 1 : 0,
	        static_cast<unsigned long long>(action.target_content_serial),
	        static_cast<uint32_t>(action.sampled_source.kind),
	        action.sampled_source.resource,
	        static_cast<unsigned long long>(action.sampled_source.content_serial),
	        use_current_snapshot_target ? 1 : 0,
	        action.render_target, action.depth_stencil, action.vertex_shader,
        action.pixel_shader);
  }
  return true;
}

bool DirectCommandTextureSlotHasExactProducerOwnerProbeLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (slot >= command.resources.textures.size()) {
    return false;
  }

  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  const uint32_t guest = command.resources.textures[slot];
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  if (guest == 0 && !command_fetch_info.valid) {
    return false;
  }

  const uint64_t draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  const bool private_or_title_surface_copy_command =
      command.draw.source != RendererDrawEventSource::kPixVideo &&
      (DirectCommandUsesPrivateOrTitleSurfaceCopyPathLocked(command) ||
       DirectCommandUsesFablePrivateTexturePath(command, slot, texture) ||
       command.draw.private_texture_rectangle ||
       IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader) ||
       IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
       IsFableTitleSceneVertexShaderCommandLocked(command) ||
       IsFableTitleSceneTextureSyncCommandLocked(command));
  const bool command_private_header_identity =
      IsFableTitlePrivateTextureProxyAddress(guest) ||
      IsFableMutablePrivateTextureHeaderAddress(guest) ||
      (command_fetch_info.valid &&
       (IsFableTitlePrivateTextureProxyAddress(command_fetch_info.base_address) ||
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

  if (DirectCommandSlotUsesExactTitleUploadSourceLocked(command, slot)) {
    return ResolveDirectExactTitleUploadSourceForCommandSlotLocked(command, slot,
                                                                   texture) !=
           nullptr;
  }
  if (texture && PlumeGuestImageReady(*texture) &&
      PlumeGuestTextureBaseAddress(*texture) == 0x1FAA0000u &&
      PlumeGuestTextureWidth(*texture) == 512u &&
      PlumeGuestTextureHeight(*texture) == 1u &&
      PlumeGuestTextureGuestFormat(*texture) == 36u &&
      (FableTrackedResourceHasSampleableContent(*texture) ||
       FableTrackedResourceHasGpuProducedContent(*texture) ||
       FableTrackedResourceVisibleContentSerial(*texture) != 0) &&
      !DirectCommandBindsResourceAsTargetLocked(command, *texture)) {
    return true;
  }

  auto link_visible_to_command = [&](const FableSurfaceTextureLink* link) {
    if (!link || !link->trusted_destination_edge ||
        !DirectSurfaceTextureLinkHasExactCopyIdentity(*link)) {
      return false;
    }
    const bool same_slot =
        link->source_slot == kUnknownSlot || link->source_slot == slot;
    if (!same_slot &&
        !DirectSurfaceTextureLinkTouchesFablePrivateChain(*link, texture)) {
      return false;
    }
    if (DirectSurfaceTextureLinkIsPrivateTitleWrapperRoot(*link) &&
        !DirectSurfaceTextureLinkPrivateWrapperVisibleToCommandLocked(command,
                                                                      *link)) {
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
    return true;
  };

  auto find_exact_destination_owner = [&](uint32_t destination_texture) {
    if (destination_texture == 0) {
      return false;
    }
    if (link_visible_to_command(
            FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
                destination_texture, g_direct_renderer.current_frame_index,
                draw_serial, slot))) {
      return true;
    }
    if (link_visible_to_command(
            FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
                destination_texture, slot))) {
      return true;
    }
    return link_visible_to_command(
        FableDirectFindSurfaceTextureLinkForDestinationTexture(
            destination_texture));
  };
  auto find_exact_base_owner = [&](uint32_t base_address) {
    return base_address != 0 &&
           link_visible_to_command(FableDirectFindVisibleSurfaceTextureLinkForBase(
               base_address, g_direct_renderer.current_frame_index,
               draw_serial, slot));
  };
  auto find_exact_fetch_owner =
      [&](const std::array<uint32_t, 6>& fetch_dwords) {
    return HasTrackedFetchDwords(fetch_dwords) &&
           link_visible_to_command(
               FableDirectFindVisibleSurfaceTextureLinkForFetch(
                   fetch_dwords, g_direct_renderer.current_frame_index,
                   draw_serial, slot));
  };
  auto find_exact_alias_owner = [&](uint32_t destination_texture) {
    RendererSurfaceCopyAliasSnapshot alias;
    return destination_texture != 0 &&
           FindDirectSurfaceCopyAliasByDestinationAddressLocked(
               destination_texture, &alias) &&
           DirectSurfaceCopyAliasHasExactIdentity(alias) &&
           link_visible_to_command(FindDirectSurfaceTextureLinkByAliasLocked(alias));
  };
  auto identity_has_exact_owner = [&](const FableTextureIdentity* identity) {
    if (!identity) {
      return false;
    }
    return find_exact_alias_owner(identity->object_texture) ||
           find_exact_alias_owner(identity->parent_texture) ||
           find_exact_alias_owner(identity->base_address) ||
           find_exact_destination_owner(identity->object_texture) ||
           find_exact_destination_owner(identity->parent_texture) ||
           find_exact_destination_owner(identity->base_address) ||
           find_exact_base_owner(identity->base_address) ||
           find_exact_fetch_owner(identity->fetch_dwords);
  };

  if (find_exact_alias_owner(guest) || find_exact_destination_owner(guest) ||
      (command_fetch_info.valid &&
       (find_exact_alias_owner(command_fetch_info.base_address) ||
        find_exact_base_owner(command_fetch_info.base_address))) ||
      find_exact_fetch_owner(command_fetch) ||
      identity_has_exact_owner(FableDirectFindTextureIdentityByObject(guest))) {
    return true;
  }

  if (texture &&
      (find_exact_alias_owner(texture->guest_address) ||
       find_exact_alias_owner(texture->parent_address) ||
       find_exact_alias_owner(PlumeGuestTextureBaseAddress(*texture)) ||
       find_exact_destination_owner(texture->guest_address) ||
       find_exact_destination_owner(texture->parent_address) ||
       find_exact_destination_owner(PlumeGuestTextureBaseAddress(*texture)) ||
       find_exact_base_owner(PlumeGuestTextureBaseAddress(*texture)) ||
       find_exact_fetch_owner(PlumeGuestTextureFetchDwords(*texture)) ||
       identity_has_exact_owner(FableDirectFindTextureIdentityByObject(
           texture->guest_address)) ||
       identity_has_exact_owner(FableDirectFindTextureIdentityByObject(
           texture->parent_address)) ||
       identity_has_exact_owner(FableDirectFindTextureIdentityByBase(
           PlumeGuestTextureBaseAddress(*texture))))) {
    return true;
  }

  return false;
}


bool DirectCommandTextureSlotHasExactRegisteredProducerLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture, bool log_misses) {
  if (slot >= command.resources.textures.size()) {
    return false;
  }

  const uint32_t texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(command);
  if (!DirectCommandUsesTextureSlotForSyncLocked(command, texture_slot_mask,
                                                 slot)) {
    return false;
  }

  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  const auto link_matches = [&](const FableSurfaceTextureLink* link) {
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
  const auto find_link_by_destination =
      [&](uint32_t destination_texture) -> const FableSurfaceTextureLink* {
    if (destination_texture == 0) {
      return nullptr;
    }
    const FableSurfaceTextureLink* slot_link =
        FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
            destination_texture, slot);
    if (link_matches(slot_link)) {
      return slot_link;
    }
    const FableSurfaceTextureLink* link =
        FableDirectFindSurfaceTextureLinkForDestinationTexture(
            destination_texture);
    return link_matches(link) ? link : nullptr;
  };
  const uint64_t draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  const auto find_visible_link_by_base =
      [&](uint32_t base_address) -> const FableSurfaceTextureLink* {
    if (base_address == 0) {
      return nullptr;
    }
    const FableSurfaceTextureLink* link =
        FableDirectFindVisibleSurfaceTextureLinkForBase(
            base_address, g_direct_renderer.current_frame_index, draw_serial,
            slot);
    return link_matches(link) ? link : nullptr;
  };
  const auto find_visible_link_by_fetch =
      [&](const std::array<uint32_t, 6>& fetch_dwords)
          -> const FableSurfaceTextureLink* {
    if (!HasTrackedFetchDwords(fetch_dwords)) {
      return nullptr;
    }
    const FableSurfaceTextureLink* link =
        FableDirectFindVisibleSurfaceTextureLinkForFetch(
            fetch_dwords, g_direct_renderer.current_frame_index, draw_serial,
            slot);
    return link_matches(link) ? link : nullptr;
  };
  const auto exact_private_title_link_visible =
      [&](const FableSurfaceTextureLink* link) -> bool {
    if (!link || !DirectSurfaceTextureLinkHasExactCopyIdentity(*link) ||
        !link_matches(link)) {
      return false;
    }
    if (!DirectSurfaceTextureLinkIsPrivateTitleWrapperRoot(*link)) {
      return true;
    }
    return DirectSurfaceTextureLinkPrivateWrapperVisibleToCommandLocked(command,
                                                                        *link);
  };
  const auto find_exact_link_by_destination =
      [&](uint32_t destination_texture) -> const FableSurfaceTextureLink* {
    if (destination_texture == 0) {
      return nullptr;
    }
    const FableSurfaceTextureLink* slot_link =
        FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
            destination_texture, slot);
    if (exact_private_title_link_visible(slot_link)) {
      return slot_link;
    }
    const FableSurfaceTextureLink* link =
        FableDirectFindSurfaceTextureLinkForDestinationTexture(
            destination_texture);
    return exact_private_title_link_visible(link) ? link : nullptr;
  };
  const auto find_exact_visible_link_by_base =
      [&](uint32_t base_address) -> const FableSurfaceTextureLink* {
    if (base_address == 0) {
      return nullptr;
    }
    const FableSurfaceTextureLink* link =
        FableDirectFindVisibleSurfaceTextureLinkForBase(
            base_address, g_direct_renderer.current_frame_index, draw_serial,
            slot);
    return exact_private_title_link_visible(link) ? link : nullptr;
  };
  const auto find_exact_visible_link_by_fetch =
      [&](const std::array<uint32_t, 6>& fetch_dwords)
          -> const FableSurfaceTextureLink* {
    if (!HasTrackedFetchDwords(fetch_dwords)) {
      return nullptr;
    }
    const FableSurfaceTextureLink* link =
        FableDirectFindVisibleSurfaceTextureLinkForFetch(
            fetch_dwords, g_direct_renderer.current_frame_index, draw_serial,
            slot);
    return exact_private_title_link_visible(link) ? link : nullptr;
  };

  const uint32_t guest = command.resources.textures[slot];
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  if (guest == 0 && !command_fetch_info.valid) {
    return false;
  }
  const bool exact_d3d_indexed_title_strip =
      command.draw.source == RendererDrawEventSource::kSceneObject &&
      command.draw.private_texture_wrapper_symbol_address ==
          kFableD3DBeginIndexedVerticesWrapperSymbol &&
      (command.draw.private_texture_wrapper_call_serial != 0 ||
       command.draw.private_texture_wrapper_context_key != 0) &&
      command_fetch_info.valid &&
      command_fetch_info.base_address == 0x1FAA0000u &&
      command_fetch_info.width == 512u && command_fetch_info.height == 1u &&
      command_fetch_info.format == 36u;
  if (exact_d3d_indexed_title_strip) {
    const PlumeTrackedResource* title_strip_texture =
        texture ? texture : FindDirectResourceLocked(guest);
    if (DirectResourceMatchesExactBeginVerticesTitleStripFetch(
            title_strip_texture, command_fetch_info, command_fetch) &&
        DirectResourceIsExactBeginVerticesTitleStripSampleLocked(
            command, title_strip_texture)) {
      static uint32_t logged_exact_d3d_indexed_title_strip_producers = 0;
      if (logged_exact_d3d_indexed_title_strip_producers < 32u) {
        REXLOG_INFO(
            "Refii plume exact D3D indexed title strip registered producer #{} "
            "frame={} slot={} guest=0x{:08X} sample=0x{:08X}/{}x{} "
            "wrapper=0x{:08X}/{} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
            "ps=0x{:08X}",
            ++logged_exact_d3d_indexed_title_strip_producers,
            g_direct_renderer.current_frame_index, slot, guest,
            title_strip_texture->guest_address,
            PlumeGuestTextureWidth(*title_strip_texture),
            PlumeGuestTextureHeight(*title_strip_texture),
            command.draw.private_texture_wrapper_symbol_address,
            static_cast<unsigned long long>(
                command.draw.private_texture_wrapper_call_serial),
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
      return true;
    }
  }
  if (PublishDirectBeginVerticesTitleTextureActionForSlotLocked(command, slot,
                                                                texture)) {
    return true;
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
  const bool command_private_header_identity =
      IsFableTitlePrivateTextureProxyAddress(guest) ||
      IsFableMutablePrivateTextureHeaderAddress(guest) ||
      (command_fetch_info.valid &&
       (IsFableTitlePrivateTextureProxyAddress(command_fetch_info.base_address) ||
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
	  auto exact_private_title_link_matches_destination =
	      [&](const FableSurfaceTextureLink& link,
	          uint32_t destination_texture) -> bool {
    if (!private_or_title_surface_copy_command ||
        !command_private_header_identity || destination_texture == 0 ||
        !link.trusted_destination_edge ||
        !DirectSurfaceTextureLinkHasExactCopyIdentity(link) ||
        link.kind == FablePrivateTextureCopyKind::kDepth ||
        link.source_surface == 0) {
      return false;
    }
    const bool destination_matches =
        link.texture == destination_texture ||
        link.backing_texture == destination_texture ||
        link.texture_base_address == destination_texture ||
        link.backing_base_address == destination_texture ||
        (command_fetch_info.valid &&
         (link.texture_base_address == command_fetch_info.base_address ||
          link.backing_base_address == command_fetch_info.base_address)) ||
        (texture &&
         (link.texture == texture->guest_address ||
          link.texture == texture->parent_address ||
          link.backing_texture == texture->guest_address ||
          link.backing_texture == texture->parent_address ||
          link.texture_base_address == PlumeGuestTextureBaseAddress(*texture) ||
          link.backing_base_address == PlumeGuestTextureBaseAddress(*texture)));
    if (!destination_matches) {
      return false;
    }
    if (DirectSurfaceTextureLinkIsPrivateTitleWrapperRoot(link) &&
        !DirectSurfaceTextureLinkPrivateWrapperVisibleToCommandLocked(command,
                                                                      link)) {
      return false;
    }
    if (link.frame_index != 0 &&
        link.frame_index > g_direct_renderer.current_frame_index) {
      return false;
    }
    if (link.frame_index != 0 &&
        link.frame_index == g_direct_renderer.current_frame_index &&
        link.draw_serial != 0 && draw_serial != 0 &&
        link.draw_serial > draw_serial) {
      return false;
    }
    const PlumeTrackedResource* source =
        FindDirectResourceLocked(link.source_surface);
    const bool link_has_registered_content =
        link.target_content_serial != 0 || link.backing_content_serial != 0 ||
        link.sampled_source.kind == FableRegisteredSampleSourceKind::kGpuSurface ||
        (source && FableTrackedResourceHasGpuProducedContent(*source));
    return source && IsImageResource(source->kind) &&
           PlumeGuestImageReady(*source) && link_has_registered_content &&
           !DirectCommandBindsResourceAsTargetLocked(command, *source);
  };
  auto find_exact_private_title_link_by_destination =
      [&](uint32_t destination_texture) -> const FableSurfaceTextureLink* {
    if (destination_texture == 0) {
      return nullptr;
    }
    const std::vector<FableSurfaceTextureLink>* links =
        FableDirectFindSurfaceTextureLinksForDestinationTexture(
            destination_texture);
    if (!links) {
      return nullptr;
    }
    for (auto it = links->rbegin(); it != links->rend(); ++it) {
      if (exact_private_title_link_matches_destination(*it,
                                                       destination_texture)) {
        return &*it;
      }
    }
    return nullptr;
  };
  auto exact_native_depth_family_registered_producer =
      [&](uint32_t destination_texture) -> bool {
    if (!private_or_title_surface_copy_command ||
        !IsFableTitleSceneTextureSyncCommandLocked(command) ||
        destination_texture == 0 ||
        FablePrivateTextureProxyRoleForAddress(destination_texture) !=
            FablePrivateTextureProxyRole::kDepth) {
      return false;
    }

    RendererSurfaceCopyAliasSnapshot alias;
    const bool alias_valid =
        FindDirectSurfaceCopyAliasByDestinationAddressLocked(
            destination_texture, &alias) &&
        DirectSurfaceCopyAliasHasExactIdentity(alias);

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
          !DirectSurfaceTextureLinkHasExactCopyIdentity(*candidate) ||
          candidate->kind != FablePrivateTextureCopyKind::kDepth ||
          FablePrivateTextureProxyRoleForAddress(candidate->texture) !=
              FablePrivateTextureProxyRole::kDepth ||
          !DirectResourceAddressMatchesLocked(candidate->texture,
                                              destination_texture) ||
          !link_matches(candidate)) {
        return;
      }
      const bool same_slot =
          candidate->source_slot == kUnknownSlot ||
          candidate->source_slot == slot;
      if (!same_slot &&
          !DirectSurfaceTextureLinkTouchesFablePrivateChain(*candidate,
                                                            texture)) {
        return;
      }
      const bool same_depth_family =
          DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
              *candidate, candidate->source_surface,
              command.resources.depth_stencil) ||
          DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
              *candidate, candidate->depth_stencil,
              command.resources.depth_stencil) ||
          (alias_valid &&
           (DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
                *candidate, candidate->source_surface, alias.source_surface) ||
            DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
                *candidate, candidate->depth_stencil, alias.source_surface)));
      if (!same_depth_family) {
        return;
      }
      const PlumeTrackedResource* source =
          FindDirectResourceLocked(candidate->source_surface);
      if (!source || !IsImageResource(source->kind) ||
          !PlumeGuestImageReady(*source) ||
          FableTrackedResourceIsBlankUploadedSampleProxy(*source) ||
          DirectCommandBindsResourceAsTargetLocked(command, *source)) {
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
          break;
        }
      }
    }
    if (!best_link) {
      if (const std::vector<FableSurfaceTextureLink>* texture_links =
              FableDirectFindSurfaceTextureLinksByTexture(destination_texture)) {
        for (auto it = texture_links->rbegin(); it != texture_links->rend();
             ++it) {
          consider_link(&*it);
          if (best_link && link_rank(best_link) == 0u) {
            break;
          }
        }
      }
    }
    if (!best_link) {
      return false;
    }

    static uint32_t logged_exact_native_depth_family_registered_producers = 0;
    if (logged_exact_native_depth_family_registered_producers < 32u) {
      REXLOG_INFO(
          "Refii plume exact native depth family registered producer #{} "
          "frame={} slot={} guest=0x{:08X} texture=0x{:08X} "
          "surface=0x{:08X} call={} context=0x{:016X} symbol=0x{:08X} "
          "alias_source=0x{:08X} alias_call={} alias_context=0x{:016X} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_native_depth_family_registered_producers,
          g_direct_renderer.current_frame_index, slot, destination_texture,
          best_link->texture, best_link->source_surface,
          static_cast<unsigned long long>(best_link->copy_call_serial),
          static_cast<unsigned long long>(best_link->copy_context_key),
          best_link->copy_symbol_address, alias_valid ? alias.source_surface : 0u,
          static_cast<unsigned long long>(
              alias_valid ? alias.copy_call_serial : 0u),
          static_cast<unsigned long long>(
              alias_valid ? alias.copy_context_key : 0u),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return true;
  };
  auto exact_alias_link_matches_destination =
      [&](uint32_t destination_texture) -> bool {
    RendererSurfaceCopyAliasSnapshot alias;
    if (!FindDirectSurfaceCopyAliasByDestinationAddressLocked(
            destination_texture, &alias) ||
        !DirectSurfaceCopyAliasHasExactIdentity(alias)) {
      return false;
    }
    return link_matches(FindDirectSurfaceTextureLinkByAliasLocked(alias));
  };

  if (exact_alias_link_matches_destination(guest)) {
    return true;
  }
  if (exact_native_depth_family_registered_producer(guest)) {
    return true;
  }
  if (ResolveDirectAuthoritativeTitleSyncSampleForSlotLocked(command, slot,
                                                             texture)) {
    return true;
  }
  if (ResolveDirectExactTitleUploadSourceForCommandSlotLocked(command, slot,
                                                              texture)) {
    return true;
  }
  if (ResolveDirectTinyTitleUploadedTextureForCommandSlotLocked(command, slot,
                                                                texture)) {
    return true;
  }
  const bool exact_tiny_title_direct_sample =
      command.draw.source != RendererDrawEventSource::kPixVideo &&
      (command.draw.private_texture_wrapper_symbol_address ==
           kFableD3DBeginIndexedVerticesWrapperSymbol ||
       IsFableTitleSceneVertexShaderCommandLocked(command)) &&
      DirectCommandTextureSlotDirectResourceProvidesSampleLocked(
          command, slot, texture, true) &&
      (IsFableTinyTitleUploadedTextureAddress(guest) ||
       (command_fetch_info.valid &&
        IsFableTinyTitleUploadedTextureFilterAddress(
            command_fetch_info.base_address)) ||
       (texture &&
        (IsFableTinyTitleUploadedTextureAddress(texture->guest_address) ||
         IsFableTinyTitleUploadedTextureAddress(texture->parent_address) ||
         IsFableTinyTitleUploadedTextureFilterAddress(
             PlumeGuestTextureBaseAddress(*texture)))));
  if (exact_tiny_title_direct_sample) {
    static uint32_t logged_exact_tiny_title_direct_registered_producers = 0;
    if (logged_exact_tiny_title_direct_registered_producers < 32u) {
      REXLOG_INFO(
          "Refii plume exact tiny-title direct registered producer #{} "
          "frame={} slot={} guest=0x{:08X} source={} wrapper=0x{:08X}/{} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_tiny_title_direct_registered_producers,
          g_direct_renderer.current_frame_index, slot, guest,
          static_cast<uint32_t>(command.draw.source),
          command.draw.private_texture_wrapper_symbol_address,
          static_cast<unsigned long long>(
              command.draw.private_texture_wrapper_call_serial),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return true;
  }
  if (IsFableTitleSceneTextureSyncCommandLocked(command)) {
    if (PlumeTrackedResource* registered_fetch_sample =
            ResolveDirectSampledTextureFromFetchConstantsLocked(command,
                                                                slot)) {
      if (DirectTitleSyncFetchTextureMatchesCommandLocked(
              command, slot, *registered_fetch_sample)) {
        static uint32_t logged_exact_title_sync_registered_producers = 0;
        if (logged_exact_title_sync_registered_producers < 32u) {
          REXLOG_INFO(
              "Refii plume exact title-sync registered producer #{} "
              "frame={} slot={} guest=0x{:08X} tracked=0x{:08X} "
              "base=0x{:08X} {}x{} fmt={} serial={} rt=0x{:08X} "
              "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
              ++logged_exact_title_sync_registered_producers,
              g_direct_renderer.current_frame_index, slot, guest,
              registered_fetch_sample->guest_address,
              PlumeGuestTextureBaseAddress(*registered_fetch_sample),
              PlumeGuestTextureWidth(*registered_fetch_sample),
              PlumeGuestTextureHeight(*registered_fetch_sample),
              PlumeGuestTextureGuestFormat(*registered_fetch_sample),
              static_cast<unsigned long long>(
                  FableTrackedResourceVisibleContentSerial(*registered_fetch_sample)),
              command.resources.render_targets[0],
              command.resources.depth_stencil, command.resources.vertex_shader,
              command.resources.pixel_shader);
        }
        return true;
      }
    }
  }
  if (DirectCommandSlotUsesExactTitleUploadSourceLocked(command, slot)) {
    static uint32_t logged_exact_title_upload_producers = 0;
    if (logged_exact_title_upload_producers < 32u) {
      REXLOG_INFO(
          "Refii plume exact title upload producer awaiting upload #{} frame={} "
          "slot={} guest=0x{:08X} source={} rt=0x{:08X} ds=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_title_upload_producers,
          g_direct_renderer.current_frame_index, slot, guest,
          static_cast<uint32_t>(command.draw.source),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return false;
  }
  if (private_or_title_surface_copy_command) {
    if (texture) {
      if (exact_alias_link_matches_destination(texture->guest_address) ||
          exact_alias_link_matches_destination(texture->parent_address) ||
          exact_alias_link_matches_destination(
              PlumeGuestTextureBaseAddress(*texture))) {
        return true;
      }
    }
    if (const FableTextureIdentity* identity =
            FableDirectFindTextureIdentityByObject(guest)) {
      if (exact_alias_link_matches_destination(identity->object_texture) ||
          exact_alias_link_matches_destination(identity->parent_texture) ||
          exact_alias_link_matches_destination(identity->base_address)) {
        return true;
      }
    }
    if (find_exact_link_by_destination(guest)) {
      return true;
    }
    if (find_exact_private_title_link_by_destination(guest)) {
      static uint32_t logged_exact_private_title_cross_slot_links = 0;
      if (logged_exact_private_title_cross_slot_links < 32u) {
        REXLOG_INFO(
            "Refii plume exact private/title cross-slot link producer #{} "
            "frame={} slot={} guest=0x{:08X} source={} rt=0x{:08X} "
            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_private_title_cross_slot_links,
            g_direct_renderer.current_frame_index, slot, guest,
            static_cast<uint32_t>(command.draw.source),
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
      return true;
    }
    if (exact_native_depth_family_registered_producer(guest)) {
      return true;
    }
    if ((command_fetch_info.valid &&
         find_exact_visible_link_by_base(command_fetch_info.base_address)) ||
        find_exact_visible_link_by_fetch(command_fetch)) {
      return true;
    }
    if (texture) {
      if (find_exact_link_by_destination(texture->guest_address) ||
          find_exact_link_by_destination(texture->parent_address) ||
          find_exact_link_by_destination(PlumeGuestTextureBaseAddress(*texture)) ||
          find_exact_private_title_link_by_destination(texture->guest_address) ||
          find_exact_private_title_link_by_destination(texture->parent_address) ||
          find_exact_private_title_link_by_destination(
              PlumeGuestTextureBaseAddress(*texture)) ||
          find_exact_visible_link_by_base(PlumeGuestTextureBaseAddress(*texture)) ||
          find_exact_visible_link_by_fetch(PlumeGuestTextureFetchDwords(*texture))) {
        return true;
      }
    }
    if (const FableTextureIdentity* identity =
            FableDirectFindTextureIdentityByObject(guest)) {
      if (find_exact_link_by_destination(identity->object_texture) ||
          find_exact_link_by_destination(identity->parent_texture) ||
          find_exact_link_by_destination(identity->base_address) ||
          find_exact_private_title_link_by_destination(identity->object_texture) ||
          find_exact_private_title_link_by_destination(identity->parent_texture) ||
          find_exact_private_title_link_by_destination(identity->base_address) ||
          find_exact_visible_link_by_base(identity->base_address) ||
          find_exact_visible_link_by_fetch(identity->fetch_dwords)) {
        return true;
      }
    }
    if (ResolveDirectMutablePrivateHeaderSampleByIdentityLocked(
            command, slot, texture, true)) {
      static uint32_t logged_exact_mutable_header_producers = 0;
      if (logged_exact_mutable_header_producers < 32u) {
        REXLOG_INFO(
            "Refii plume exact mutable private header producer #{} "
            "frame={} slot={} guest=0x{:08X} source={} rt=0x{:08X} "
            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_mutable_header_producers,
            g_direct_renderer.current_frame_index, slot, guest,
            static_cast<uint32_t>(command.draw.source),
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
      static uint32_t logged_exact_snapshot_registered_producers = 0;
      if (logged_exact_snapshot_registered_producers < 32u) {
        REXLOG_INFO(
            "Refii plume exact snapshot registered producer #{} frame={} "
            "slot={} guest=0x{:08X} source=0x{:08X}/{}x{} target=0x{:08X} "
            "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_snapshot_registered_producers,
            g_direct_renderer.current_frame_index, slot, guest,
            snapshot_source ? snapshot_source->guest_address : 0u,
            snapshot_source ? PlumeGuestTextureWidth(*snapshot_source) : 0u,
            snapshot_source ? PlumeGuestTextureHeight(*snapshot_source) : 0u,
            command.resources.render_targets[0],
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
      return true;
    }
    if (ResolveDirectExactWrapperSourceSampleForSlotLocked(command, slot,
                                                           texture)) {
      return true;
    }
    if (!DirectCommandTextureSlotHasPrivateTitleIdentityLocked(command, slot,
                                                              texture) &&
        DirectCommandTextureSlotDirectResourceProvidesSampleLocked(
            command, slot, texture, true)) {
      return true;
    }
    if (IsFableTitleSceneTextureSyncCommandLocked(command) &&
        command_fetch_info.valid) {
      return false;
    }
    {
      if (PlumeTrackedResource* mutable_header_sample =
              ResolveDirectMutablePrivateHeaderSampleByIdentityLocked(
                  command, slot, texture, false)) {
        static uint32_t logged_exact_mutable_header_registered_producers = 0;
        if (logged_exact_mutable_header_registered_producers < 32u) {
          REXLOG_INFO(
              "Refii plume exact mutable private header registered producer "
              "#{} frame={} slot={} guest=0x{:08X} sample=0x{:08X}/{}x{} "
              "source={} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
              ++logged_exact_mutable_header_registered_producers,
              g_direct_renderer.current_frame_index, slot, guest,
              mutable_header_sample->guest_address,
              PlumeGuestTextureWidth(*mutable_header_sample),
              PlumeGuestTextureHeight(*mutable_header_sample),
              static_cast<uint32_t>(command.draw.source),
              command.resources.render_targets[0],
              command.resources.depth_stencil, command.resources.vertex_shader,
              command.resources.pixel_shader);
        }
        return true;
      }
    }
    if (log_misses && RendererDeepObserverHooksEnabled()) {
      static uint32_t logged_private_title_exact_producer_misses = 0;
      if (logged_private_title_exact_producer_misses < 64u) {
        REXLOG_INFO(
            "Refii plume exact private/title registered producer reject #{} "
            "frame={} slot={} guest=0x{:08X} source={} "
	            "private_rect={} title_sync={} wrapper=0x{:08X}/{} "
	            "context=0x{:016X} wrapper_source=0x{:08X}/{} "
	            "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
	            ++logged_private_title_exact_producer_misses,
	            g_direct_renderer.current_frame_index, slot, guest,
	            static_cast<uint32_t>(command.draw.source),
	            command.draw.private_texture_rectangle ? 1 : 0,
	            IsFableTitleSceneTextureSyncCommandLocked(command) ? 1 : 0,
	            command.draw.private_texture_wrapper_symbol_address,
	            static_cast<unsigned long long>(
	                command.draw.private_texture_wrapper_call_serial),
	            static_cast<unsigned long long>(
	                command.draw.private_texture_wrapper_context_key),
	            command.draw.private_texture_wrapper_source_texture,
	            command.draw.private_texture_wrapper_source_exact ? 1 : 0,
	            command.resources.render_targets[0], command.resources.depth_stencil,
	            command.resources.vertex_shader, command.resources.pixel_shader);
      }
    }
    return false;
  }
  if (find_link_by_destination(guest)) {
    return true;
  }
  if ((command_fetch_info.valid &&
       find_visible_link_by_base(command_fetch_info.base_address)) ||
      find_visible_link_by_fetch(command_fetch)) {
    return true;
  }
  if (texture) {
    if (find_link_by_destination(texture->guest_address) ||
        find_link_by_destination(texture->parent_address) ||
        find_link_by_destination(PlumeGuestTextureBaseAddress(*texture)) ||
        find_visible_link_by_base(PlumeGuestTextureBaseAddress(*texture)) ||
        find_visible_link_by_fetch(PlumeGuestTextureFetchDwords(*texture))) {
      return true;
    }
  }
  if (const FableTextureIdentity* identity =
          FableDirectFindTextureIdentityByObject(guest)) {
    if (find_link_by_destination(identity->object_texture) ||
        find_link_by_destination(identity->parent_texture) ||
        find_link_by_destination(identity->base_address) ||
        find_visible_link_by_base(identity->base_address) ||
        find_visible_link_by_fetch(identity->fetch_dwords)) {
      return true;
    }
  }
  return false;
}

#endif

}  // namespace refii::renderer
