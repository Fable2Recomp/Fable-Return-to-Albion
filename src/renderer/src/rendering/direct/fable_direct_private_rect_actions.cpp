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

PlumeTrackedResource*
ResolveDirectPrivateTextureActionBackingFromOutputActionsLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target) {
  if (slot >= command.resources.textures.size() ||
      command.draw.source == RendererDrawEventSource::kPixVideo) {
    return nullptr;
  }

  const uint64_t draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  const uint32_t current_rt = command.resources.render_targets[0];
  const uint32_t current_ds = command.resources.depth_stencil;

  auto action_targets_resource =
      [&](const FablePrivateTextureCopyAction& action) {
    const bool exact_target_match =
        action.target_surface == target.guest_address ||
        action.render_target == target.guest_address ||
        action.depth_stencil == target.guest_address ||
        (target.parent_address != 0 &&
         (action.target_surface == target.parent_address ||
          action.render_target == target.parent_address ||
          action.depth_stencil == target.parent_address)) ||
        (current_rt != 0 &&
         (action.target_surface == current_rt ||
          action.render_target == current_rt)) ||
        (current_ds != 0 &&
         (action.target_surface == current_ds ||
          action.depth_stencil == current_ds));
    const bool inexact_target_alias_match =
        exact_target_match ||
        DirectResourceAddressMatchesLocked(action.target_surface,
                                           target.guest_address) ||
        DirectResourceAddressMatchesLocked(action.render_target,
                                           target.guest_address) ||
        DirectResourceAddressMatchesLocked(action.depth_stencil,
                                           target.guest_address) ||
        DirectResourceAddressMatchesLocked(action.target_surface,
                                           target.parent_address) ||
        DirectResourceAddressMatchesLocked(action.render_target,
                                           target.parent_address) ||
        DirectResourceAddressMatchesLocked(action.depth_stencil,
                                           target.parent_address) ||
        (current_rt != 0 &&
         (DirectResourceAddressMatchesLocked(action.target_surface,
                                             current_rt) ||
          DirectResourceAddressMatchesLocked(action.render_target, current_rt))) ||
        (current_ds != 0 &&
         (DirectResourceAddressMatchesLocked(action.target_surface,
                                             current_ds) ||
          DirectResourceAddressMatchesLocked(action.depth_stencil, current_ds)));
    const bool target_match = exact_target_match;
    if (!target_match) {
      if (inexact_target_alias_match) {
        static uint32_t logged_rejected_output_action_inexact_target_aliases = 0;
        if (logged_rejected_output_action_inexact_target_aliases < 32u) {
          REXLOG_INFO(
              "Refii plume exact output-action target-alias reject #{} "
              "frame={} serial={} draw={} slot={} "
              "action_draw={} action_call={} action_context=0x{:016X} "
              "action_symbol=0x{:08X} action_target=0x{:08X} action_rt=0x{:08X} "
              "action_ds=0x{:08X} target=0x{:08X} parent=0x{:08X} "
              "current_rt=0x{:08X} current_ds=0x{:08X} source=0x{:08X} "
              "vs=0x{:08X} ps=0x{:08X}",
              ++logged_rejected_output_action_inexact_target_aliases,
              g_direct_renderer.current_frame_index,
              static_cast<unsigned long long>(command.serial),
              static_cast<unsigned long long>(draw_serial), slot,
              static_cast<unsigned long long>(action.draw_serial),
              static_cast<unsigned long long>(action.wrapper_call_serial),
              static_cast<unsigned long long>(action.wrapper_context_key),
              action.wrapper_symbol_address,
              action.target_surface, action.render_target, action.depth_stencil,
              target.guest_address, target.parent_address, current_rt, current_ds,
              source.guest_address, command.resources.vertex_shader,
              command.resources.pixel_shader);
        }
      }
      return false;
    }
    if (target.texture_depth_target &&
        action.kind != FablePrivateTextureCopyKind::kDepth) {
      return false;
    }
    if (target.texture_render_target &&
        action.kind == FablePrivateTextureCopyKind::kDepth) {
      return false;
    }
    return true;
  };

  auto valid_output_backing =
      [&](PlumeTrackedResource* candidate,
          const FablePrivateTextureCopyAction& action) -> PlumeTrackedResource* {
    if (!DirectPrivateTextureBackingCandidateValidLocked(command, source, target,
                                                         candidate)) {
      return nullptr;
    }
    if (DirectPrivateTextureCopyActionBackingContainsTargetContentLocked(
            action, *candidate, target) ||
        DirectPrivateFeedbackAliasContainsTargetContentLocked(*candidate,
                                                              target)) {
      return candidate;
    }
    return nullptr;
  };

  auto resolve_action =
      [&](const FablePrivateTextureCopyAction* action) -> PlumeTrackedResource* {
    if (!action || !action_targets_resource(*action)) {
      return nullptr;
    }

    if (PlumeTrackedResource* backing =
            valid_output_backing(FindDirectResourceLocked(action->backing_texture),
                                 *action)) {
      return backing;
    }

    if (action->sampled_source.kind ==
        FableRegisteredSampleSourceKind::kBackingTexture) {
      if (PlumeTrackedResource* sample = valid_output_backing(
              ResolveDirectRegisteredSampleSourceLocked(command,
                                                        action->sampled_source),
              *action)) {
        return sample;
      }
    }

    return nullptr;
  };

  auto resolve_destination = [&](uint32_t destination) -> PlumeTrackedResource* {
    if (destination == 0) {
      return nullptr;
    }
    if (PlumeTrackedResource* backing = resolve_action(
            FableDirectFindVisiblePrivateTextureCopyOutputActionForDestinationTextureSlot(
                destination, slot, g_direct_renderer.current_frame_index,
                draw_serial))) {
      return backing;
    }
    return resolve_action(
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

  add_candidate(target.guest_address);
  add_candidate(target.parent_address);
  add_candidate(current_rt);
  add_candidate(current_ds);
  if (const FableTextureIdentity* identity =
          FableDirectFindTextureIdentityByObject(target.guest_address)) {
    add_candidate(identity->object_texture);
    add_candidate(identity->parent_texture);
  }
  if (target.parent_address != 0) {
    if (const FableTextureIdentity* identity =
            FableDirectFindTextureIdentityByObject(target.parent_address)) {
      add_candidate(identity->object_texture);
      add_candidate(identity->parent_texture);
    }
  }

  for (size_t i = 0; i < candidate_count; ++i) {
    if (PlumeTrackedResource* backing = resolve_destination(candidates[i])) {
      static uint32_t logged_exact_output_backings = 0;
      if (logged_exact_output_backings < 32u) {
        REXLOG_INFO(
            "Refii plume exact private copy output backing #{} frame={} "
            "slot={} source=0x{:08X}/{}x{} target=0x{:08X}/{}x{} "
            "candidate=0x{:08X} backing=0x{:08X}/{}x{} rt=0x{:08X} "
            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_output_backings,
            g_direct_renderer.current_frame_index, slot, source.guest_address,
            PlumeGuestTextureWidth(source), PlumeGuestTextureHeight(source),
            target.guest_address, PlumeGuestTextureWidth(target),
            PlumeGuestTextureHeight(target), candidates[i],
            backing->guest_address, PlumeGuestTextureWidth(*backing),
            PlumeGuestTextureHeight(*backing), current_rt, current_ds,
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
      return backing;
    }
  }

  return nullptr;
}

uint64_t DirectPrivateTextureBackingMissCacheKeyLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target) {
  uint64_t key = 0x9E3779B97F4A7C15ull;
  const auto mix = [&](uint64_t value) {
    key ^= value + 0xC6A4A7935BD1E995ull + (key << 6u) + (key >> 2u);
  };
  const uint32_t guest =
      slot < command.resources.textures.size() ? command.resources.textures[slot]
                                               : 0u;
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const std::array<uint32_t, 6> source_fetch =
      PlumeGuestTextureFetchDwords(source);
  const std::array<uint32_t, 6> target_fetch =
      PlumeGuestTextureFetchDwords(target);

  mix(static_cast<uint32_t>(command.draw.source));
  mix(slot);
  mix(guest);
  mix(source.guest_address);
  mix(source.parent_address);
  mix(PlumeGuestTextureBaseAddress(source));
  mix(PlumeGuestTextureWidth(source));
  mix(PlumeGuestTextureHeight(source));
  mix(PlumeGuestTextureGuestFormat(source));
  for (uint32_t dword : command_fetch) {
    mix(dword);
  }
  for (uint32_t dword : source_fetch) {
    mix(dword);
  }

  mix(target.guest_address);
  mix(target.parent_address);
  mix(PlumeGuestTextureBaseAddress(target));
  mix(PlumeGuestTextureWidth(target));
  mix(PlumeGuestTextureHeight(target));
  mix(PlumeGuestTextureGuestFormat(target));
  for (uint32_t dword : target_fetch) {
    mix(dword);
  }

  mix(command.resources.render_targets[0]);
  mix(command.resources.depth_stencil);
  mix(command.resources.vertex_shader);
  mix(command.resources.pixel_shader);
  return key;
}

bool FindCachedDirectPrivateTextureBackingMissLocked(
    uint64_t cache_key, const PlumeTrackedResource& source,
    const PlumeTrackedResource& target) {
  const auto it =
      g_direct_renderer.private_texture_backing_miss_cache.find(cache_key);
  if (it == g_direct_renderer.private_texture_backing_miss_cache.end()) {
    return false;
  }

  const DirectPrivateTextureBackingMissCacheEntry& entry = it->second;
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  if (entry.private_copy_serial != DirectPrivateTextureCopySerialLocked() ||
      entry.texture_identity_serial != registry.texture_identity_serial ||
      entry.private_texture_alias_serial !=
          g_direct_renderer.private_texture_alias_serial ||
      entry.source_content_serial != FableTrackedResourceVisibleContentSerial(source) ||
      entry.target_content_serial != FableTrackedResourceVisibleContentSerial(target)) {
    g_direct_renderer.private_texture_backing_miss_cache.erase(it);
    return false;
  }
  return true;
}

void RememberDirectPrivateTextureBackingMissLocked(
    uint64_t cache_key, const PlumeTrackedResource& source,
    const PlumeTrackedResource& target) {
  constexpr size_t kMaxPrivateTextureBackingMissCacheEntries = 4096u;
  if (g_direct_renderer.private_texture_backing_miss_cache.size() >=
      kMaxPrivateTextureBackingMissCacheEntries) {
    g_direct_renderer.private_texture_backing_miss_cache.clear();
  }

  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  DirectPrivateTextureBackingMissCacheEntry entry;
  entry.private_copy_serial = DirectPrivateTextureCopySerialLocked();
  entry.texture_identity_serial = registry.texture_identity_serial;
  entry.private_texture_alias_serial =
      g_direct_renderer.private_texture_alias_serial;
  entry.source_content_serial = FableTrackedResourceVisibleContentSerial(source);
  entry.target_content_serial = FableTrackedResourceVisibleContentSerial(target);
  entry.frame_index = g_direct_renderer.current_frame_index;
  g_direct_renderer.private_texture_backing_miss_cache[cache_key] = entry;
}

// Feedback-ensure memo (see DirectPrivateFeedbackEnsureMemoEntry for the
// generation design). FAIL OPEN: no resolvable sampled source, or any
// generation mismatch, runs the full ensure body. `source` is the slot's
// sampled surface (the feedback source whose content the body's registrations
// describe); `target` is consulted only for the self-feedback case where the
// source is bound as a target of the same draw.
bool DirectPrivateFeedbackEnsureMemoHitLocked(
    uint64_t memo_key, const PlumeTrackedResource* source,
    const PlumeTrackedResource* target) {
  if (!source) {
    return false;
  }
  const auto it = g_direct_renderer.private_feedback_ensure_memo.find(memo_key);
  if (it == g_direct_renderer.private_feedback_ensure_memo.end()) {
    return false;
  }
  const DirectPrivateFeedbackEnsureMemoEntry& entry = it->second;
  const FableGuestResourceRegistry& registry =
      FableDirectGuestResourceRegistry();
  if (entry.frame_index != g_direct_renderer.current_frame_index ||
      entry.relation_generation !=
          FableDirectGetSurfaceDestinationRelationGeneration() ||
      entry.private_copy_serial != DirectPrivateTextureCopySerialLocked() ||
      entry.texture_identity_serial != registry.texture_identity_serial ||
      entry.private_texture_alias_serial !=
          g_direct_renderer.private_texture_alias_serial ||
      entry.source_content_serial !=
          FableTrackedResourceVisibleContentSerial(*source) ||
      (entry.source_is_bound_target &&
       (!target || entry.target_content_serial !=
                       FableTrackedResourceVisibleContentSerial(*target)))) {
    g_direct_renderer.private_feedback_ensure_memo.erase(it);
    return false;
  }
  return true;
}

void RememberDirectPrivateFeedbackEnsureMemoLocked(
    uint64_t memo_key, const PlumeTrackedResource* source,
    bool source_is_bound_target, const PlumeTrackedResource* target) {
  if (!source || (source_is_bound_target && !target)) {
    // Without the sampled source (or, for self-feedback, the bound target)
    // there is no content serial to anchor the entry on — never memoize, so
    // the next draw runs the body again (fail open).
    return;
  }
  constexpr size_t kMaxPrivateFeedbackEnsureMemoEntries = 8192u;
  if (g_direct_renderer.private_feedback_ensure_memo.size() >=
      kMaxPrivateFeedbackEnsureMemoEntries) {
    g_direct_renderer.private_feedback_ensure_memo.clear();
  }
  const FableGuestResourceRegistry& registry =
      FableDirectGuestResourceRegistry();
  DirectPrivateFeedbackEnsureMemoEntry entry;
  entry.frame_index = g_direct_renderer.current_frame_index;
  // Snapshot AFTER the body ran, so the body's own registrations (which bump
  // these generations on real change) do not invalidate the entry it earned.
  entry.relation_generation =
      FableDirectGetSurfaceDestinationRelationGeneration();
  entry.private_copy_serial = DirectPrivateTextureCopySerialLocked();
  entry.texture_identity_serial = registry.texture_identity_serial;
  entry.private_texture_alias_serial =
      g_direct_renderer.private_texture_alias_serial;
  entry.source_content_serial =
      FableTrackedResourceVisibleContentSerial(*source);
  entry.source_is_bound_target = source_is_bound_target;
  entry.target_content_serial =
      target ? FableTrackedResourceVisibleContentSerial(*target) : 0;
  g_direct_renderer.private_feedback_ensure_memo[memo_key] = entry;
}

PlumeTrackedResource* ResolveDirectPrivateTextureActionBackingLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target) {
  if (slot >= command.resources.textures.size()) {
    return nullptr;
  }
  const FableDirectClock::time_point resolver_t0 = FableDirectClock::now();
  double alias_ms = 0.0;
  double output_ms = 0.0;
  double link_ms = 0.0;
  double identity_ms = 0.0;
  double uploaded_ms = 0.0;
  const bool source_needs_backing =
      !FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(source) ||
      FableTrackedResourceIsBlankUploadedSampleProxy(source) ||
      FableTrackedResourceIsUnbackedSampleProxy(source) ||
      DirectCommandBindsExactResourceAsTargetLocked(command, source);
  if (!source_needs_backing) {
    return nullptr;
  }

  auto valid_backing = [&](PlumeTrackedResource* candidate) {
    return DirectPrivateTextureBackingCandidateValidLocked(command, source, target,
                                                          candidate);
  };

  auto log_resolve_timing = [&](const char* outcome,
                                const PlumeTrackedResource* result) {
    const double total_ms =
        FableDirectElapsedMs(resolver_t0, FableDirectClock::now());
    if ((total_ms < 1.0 && alias_ms < 0.5 && output_ms < 0.5 &&
         link_ms < 0.5 && identity_ms < 0.5 && uploaded_ms < 0.5) ||
        g_direct_renderer.diagnostics.logged_private_action_backing_timing_breakdowns >=
            128u) {
      return;
    }
    REXLOG_INFO(
        "Refii plume private action backing timing #{} frame={} "
        "slot={} outcome={} result=0x{:08X} source=0x{:08X}/{}x{} "
        "target=0x{:08X}/{}x{} guest=0x{:08X} total_ms={:.3f} "
        "alias_ms={:.3f} output_ms={:.3f} link_ms={:.3f} identity_ms={:.3f} "
        "uploaded_ms={:.3f} rt=0x{:08X} ds=0x{:08X} "
        "vs=0x{:08X} ps=0x{:08X}",
        ++g_direct_renderer.diagnostics.logged_private_action_backing_timing_breakdowns,
        g_direct_renderer.current_frame_index, slot,
        outcome ? outcome : "unknown", result ? result->guest_address : 0u,
        source.guest_address, PlumeGuestTextureWidth(source),
        PlumeGuestTextureHeight(source), target.guest_address,
        PlumeGuestTextureWidth(target), PlumeGuestTextureHeight(target),
        command.resources.textures[slot], total_ms, alias_ms, output_ms, link_ms,
        identity_ms, uploaded_ms, command.resources.render_targets[0],
        command.resources.depth_stencil, command.resources.vertex_shader,
        command.resources.pixel_shader);
  };

  const FablePrivateTextureCopyAction* exact_action =
      FindDirectPrivateRectExactActionLocked(command, slot, source, target);
  if (exact_action) {
    const uint32_t exact_backing_address =
        exact_action->backing_texture != 0
            ? exact_action->backing_texture
            : (exact_action->sampled_source.kind ==
                       FableRegisteredSampleSourceKind::kBackingTexture
                   ? exact_action->sampled_source.resource
                   : 0u);
    if (PlumeTrackedResource* exact_backing =
            FindDirectResourceLocked(exact_backing_address);
        valid_backing(exact_backing)) {
      log_resolve_timing("exact-action", exact_backing);
      return exact_backing;
    }
  }

  if (DirectPrivateTextureActionHasExactGpuSurfaceNoBackingLocked(
          command, slot, source, target)) {
    log_resolve_timing("exact-gpu-surface", nullptr);
    return nullptr;
  }

  const uint64_t backing_miss_key =
      DirectPrivateTextureBackingMissCacheKeyLocked(command, slot, source,
                                                    target);
  if (FindCachedDirectPrivateTextureBackingMissLocked(backing_miss_key, source,
                                                      target)) {
    if (g_direct_renderer.diagnostics.logged_private_action_backing_miss_cache_hits < 32u) {
      REXLOG_INFO(
          "Refii plume exact private action backing cache reject #{} frame={} "
          "slot={} source=0x{:08X}/{}x{} target=0x{:08X}/{}x{} "
          "guest=0x{:08X} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++g_direct_renderer.diagnostics.logged_private_action_backing_miss_cache_hits,
          g_direct_renderer.current_frame_index, slot, source.guest_address,
          PlumeGuestTextureWidth(source), PlumeGuestTextureHeight(source),
          target.guest_address, PlumeGuestTextureWidth(target),
          PlumeGuestTextureHeight(target), command.resources.textures[slot],
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    log_resolve_timing("cached-miss", nullptr);
    return nullptr;
  }

  const FableDirectClock::time_point output_t0 = FableDirectClock::now();
  PlumeTrackedResource* output_action_backing =
      ResolveDirectPrivateTextureActionBackingFromOutputActionsLocked(
          command, slot, source, target);
  output_ms += FableDirectElapsedMs(output_t0, FableDirectClock::now());
  if (valid_backing(output_action_backing)) {
    RememberDirectPrivateFeedbackActionBackingLocked(
        command, exact_action, source, target, *output_action_backing);
    log_resolve_timing("output-action", output_action_backing);
    return output_action_backing;
  }

  DirectPrivateTextureResolveSource resolve_source =
      DirectPrivateTextureResolveSource::kNone;
  const FableDirectClock::time_point identity_t0 = FableDirectClock::now();
  PlumeTrackedResource* identity =
      ResolveDirectPrivateTextureIdentityLocked(command, slot, &source,
                                                &resolve_source);
  identity_ms += FableDirectElapsedMs(identity_t0, FableDirectClock::now());
  if (valid_backing(identity)) {
    static uint32_t logged_exact_private_action_identity_backing_rejects = 0;
    if (logged_exact_private_action_identity_backing_rejects < 32u) {
      REXLOG_INFO(
          "Refii plume exact private action identity backing reject #{} frame={} "
          "source_kind={} slot={} guest=0x{:08X} source=0x{:08X}/{}x{} "
          "target=0x{:08X}/{}x{} identity=0x{:08X}/{}x{} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_private_action_identity_backing_rejects,
          g_direct_renderer.current_frame_index,
          DirectPrivateTextureResolveSourceName(resolve_source), slot,
          command.resources.textures[slot], source.guest_address,
          PlumeGuestTextureWidth(source), PlumeGuestTextureHeight(source),
          target.guest_address, PlumeGuestTextureWidth(target),
          PlumeGuestTextureHeight(target), identity->guest_address,
          PlumeGuestTextureWidth(*identity), PlumeGuestTextureHeight(*identity),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
  }

  const FableDirectClock::time_point uploaded_t0 = FableDirectClock::now();
  PlumeTrackedResource* uploaded =
      FindDirectPrivateRectExactUploadedBackingLocked(command, slot, &source);
  uploaded_ms += FableDirectElapsedMs(uploaded_t0, FableDirectClock::now());
  if (valid_backing(uploaded)) {
    static uint32_t logged_exact_private_action_uploaded_backing_rejects = 0;
    if (logged_exact_private_action_uploaded_backing_rejects < 32u) {
      REXLOG_INFO(
          "Refii plume exact private action uploaded backing reject #{} frame={} "
          "slot={} guest=0x{:08X} source=0x{:08X}/{}x{} "
          "target=0x{:08X}/{}x{} uploaded=0x{:08X}/{}x{} "
          "base=0x{:08X} uploaded_base=0x{:08X} rt=0x{:08X} "
          "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_private_action_uploaded_backing_rejects,
          g_direct_renderer.current_frame_index, slot,
          command.resources.textures[slot], source.guest_address,
          PlumeGuestTextureWidth(source), PlumeGuestTextureHeight(source),
          target.guest_address, PlumeGuestTextureWidth(target),
          PlumeGuestTextureHeight(target), uploaded->guest_address,
          PlumeGuestTextureWidth(*uploaded), PlumeGuestTextureHeight(*uploaded),
          PlumeGuestTextureBaseAddress(source),
          PlumeGuestTextureBaseAddress(*uploaded),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
  }

  RememberDirectPrivateTextureBackingMissLocked(backing_miss_key, source, target);
  log_resolve_timing("miss", nullptr);
  return nullptr;
}

void RegisterDirectSurfaceTextureLinkForPrivateCopyActionLocked(
    const FablePrivateTextureCopyAction& action, const char* reason) {
  if (action.source_texture == 0 || action.target_surface == 0 ||
      action.source_texture == action.target_surface) {
    return;
  }
  if (!DirectPrivateTextureCopyActionHasExactWrapperIdentity(action)) {
    return;
  }

  FableSurfaceTextureLink link;
  link.copy_call_serial = action.wrapper_call_serial;
  link.copy_context_key = action.wrapper_context_key;
  link.copy_symbol_address = action.wrapper_symbol_address;
  link.source_slot = action.source_slot;
  link.trusted_destination_edge = true;
  link.texture = action.source_texture;
  link.texture_base_address = action.source_base_address;
  link.texture_width = action.source_width;
  link.texture_height = action.source_height;
  link.texture_format = action.source_format;
  link.texture_fetch_dwords = action.source_fetch_dwords;
  link.source_surface = action.target_surface;
  link.backing_texture = action.backing_texture;
  link.backing_base_address = action.backing_base_address;
  link.backing_fetch_dwords = action.backing_fetch_dwords;
  link.backing_content_serial = action.backing_content_serial;
  link.target_content_serial = action.target_content_serial;
  link.render_target = action.render_target;
  link.depth_stencil = action.depth_stencil;
  link.vertex_shader = action.vertex_shader;
  link.pixel_shader = action.pixel_shader;
  PopulateDirectSurfaceTextureLinkShaderRoots(link, action);
  link.frame_index = action.frame_index;
  link.draw_serial = action.draw_serial;
  link.wrapper_source_texture = action.wrapper_source_texture;
  link.wrapper_source_exact = action.wrapper_source_exact;
  link.wrapper_setup_globals = action.wrapper_setup_globals;
  link.sampled_source = action.sampled_source;
  link.kind = action.kind;
  if (link.sampled_source.kind == FableRegisteredSampleSourceKind::kUnknown &&
      (link.backing_texture != 0 || link.backing_base_address != 0 ||
       FableDirectTextureFetchKey(link.backing_fetch_dwords) != 0)) {
    link.sampled_source.kind = FableRegisteredSampleSourceKind::kBackingTexture;
    link.sampled_source.resource = link.backing_texture;
    link.sampled_source.base_address = link.backing_base_address;
    link.sampled_source.fetch_dwords = link.backing_fetch_dwords;
    link.sampled_source.content_serial = link.backing_content_serial;
  }

  if (DirectSurfaceTextureLinkIsPrivateTitleWrapperRoot(link) &&
      !DirectSurfaceTextureLinkHasCommandConsumableSampleIdentity(link)) {
    if (PlumeTrackedResource* target =
            FindDirectResourceLocked(link.source_surface);
        target && IsImageResource(target->kind) &&
        PlumeGuestImageReady(*target) &&
        FableTrackedResourceVisibleContentSerial(*target) != 0) {
      PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(link, *target);
    }
  }

  uint64_t action_link_key = 1469598103934665603ull;
  auto mix_action_link_key = [&](uint64_t value) {
    action_link_key ^= value;
    action_link_key *= 1099511628211ull;
  };
  mix_action_link_key(action.frame_index);
  mix_action_link_key(action.draw_serial);
  mix_action_link_key(action.wrapper_call_serial);
  mix_action_link_key(action.wrapper_context_key);
  mix_action_link_key(action.wrapper_symbol_address);
  mix_action_link_key(action.wrapper_source_texture);
  mix_action_link_key(action.wrapper_source_exact ? 1u : 0u);
  for (uint32_t setup_global : link.wrapper_setup_globals) {
    mix_action_link_key(setup_global);
  }
  mix_action_link_key(link.source_slot);
  mix_action_link_key(link.texture);
  mix_action_link_key(link.texture_base_address);
  mix_action_link_key(FableDirectTextureFetchKey(link.texture_fetch_dwords));
  mix_action_link_key(link.source_surface);
  mix_action_link_key(link.render_target);
  mix_action_link_key(link.depth_stencil);
  mix_action_link_key(link.vertex_shader);
  mix_action_link_key(link.pixel_shader);
  mix_action_link_key(static_cast<uint32_t>(link.kind));
  if (!g_direct_renderer.private_copy_action_surface_link_frame_keys
           .insert(action_link_key)
           .second) {
    return;
  }

  RegisterDirectSurfaceTextureLinkPreparedLocked(link, reason);
  if (DirectSurfaceTextureLinkShouldPublishObjectMirror(link)) {
    FableSurfaceTextureLink object_link = link;
    object_link.serial = 0;
    object_link.source_slot = 0xFFFFFFFFu;
    RegisterDirectSurfaceTextureLinkPreparedLocked(
        object_link, "private-copy-action-exact-object");
  }

  static uint32_t logged_action_exact_surface_links = 0;
  if (RendererVerboseTracingEnabled() &&
      logged_action_exact_surface_links < 48u) {
    REXLOG_INFO(
        "Refii plume exact action surface-texture edge #{} frame={} "
        "reason={} slot={} texture=0x{:08X}/{}x{} base=0x{:08X} "
        "surface=0x{:08X} kind={} call={} context=0x{:016X} "
        "symbol=0x{:08X} setup={:08X}/{:08X}/{:08X}/{:08X} "
        "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
        ++logged_action_exact_surface_links, action.frame_index,
        reason ? reason : "private-copy-action",
        link.source_slot, link.texture, link.texture_width,
        link.texture_height, link.texture_base_address, link.source_surface,
        FablePrivateTextureCopyKindName(link.kind),
        static_cast<unsigned long long>(link.copy_call_serial),
        static_cast<unsigned long long>(link.copy_context_key),
        link.copy_symbol_address, link.wrapper_setup_globals[0],
        link.wrapper_setup_globals[1], link.wrapper_setup_globals[2],
        link.wrapper_setup_globals[3], link.render_target, link.depth_stencil,
        link.vertex_shader, link.pixel_shader);
  }
}

void RegisterDirectScaledPrivateRectSurfaceTextureLinkLocked(
    const FablePrivateTextureCopyAction& action, const char* reason) {
  if (action.source_texture == 0 || action.target_surface == 0 ||
      action.source_texture == action.target_surface ||
      !HasTrackedFetchDwords(action.source_fetch_dwords) ||
      action.source_width == 0 || action.source_height == 0 ||
      action.target_width == 0 || action.target_height == 0 ||
      action.target_content_serial == 0) {
    return;
  }

  FableSurfaceTextureLink link;
  link.copy_call_serial = action.wrapper_call_serial;
  link.copy_context_key = action.wrapper_context_key;
  link.copy_symbol_address = action.wrapper_symbol_address;
  link.source_slot = action.source_slot;
  link.trusted_destination_edge = true;
  link.texture = action.source_texture;
  link.texture_base_address = action.source_base_address;
  link.texture_width = action.source_width;
  link.texture_height = action.source_height;
  link.texture_format = action.source_format;
  link.texture_fetch_dwords = action.source_fetch_dwords;
  link.source_surface = action.target_surface;
  link.target_content_serial = action.target_content_serial;
  link.render_target = action.render_target;
  link.depth_stencil = action.depth_stencil;
  link.vertex_shader = action.vertex_shader;
  link.pixel_shader = action.pixel_shader;
  PopulateDirectSurfaceTextureLinkShaderRoots(link, action);
  link.frame_index = action.frame_index;
  link.draw_serial = action.draw_serial;
  link.wrapper_source_texture = action.wrapper_source_texture;
  link.wrapper_source_exact = action.wrapper_source_exact;
  link.wrapper_setup_globals = action.wrapper_setup_globals;
  link.sampled_source = action.sampled_source;
  link.kind = action.kind;

  uint64_t scaled_link_key = 1469598103934665603ull;
  auto mix_scaled_link_key = [&](uint64_t value) {
    scaled_link_key ^= value;
    scaled_link_key *= 1099511628211ull;
  };
  mix_scaled_link_key(action.frame_index);
  mix_scaled_link_key(action.draw_serial);
  mix_scaled_link_key(link.source_slot);
  mix_scaled_link_key(link.texture);
  mix_scaled_link_key(link.texture_base_address);
  mix_scaled_link_key(FableDirectTextureFetchKey(link.texture_fetch_dwords));
  mix_scaled_link_key(link.texture_width);
  mix_scaled_link_key(link.texture_height);
  mix_scaled_link_key(link.texture_format);
  mix_scaled_link_key(link.source_surface);
  mix_scaled_link_key(action.target_width);
  mix_scaled_link_key(action.target_height);
  mix_scaled_link_key(action.target_format);
  mix_scaled_link_key(link.target_content_serial);
  mix_scaled_link_key(link.render_target);
  mix_scaled_link_key(link.depth_stencil);
  mix_scaled_link_key(link.vertex_shader);
  mix_scaled_link_key(link.pixel_shader);
  mix_scaled_link_key(link.copy_call_serial);
  mix_scaled_link_key(link.copy_context_key);
  mix_scaled_link_key(link.copy_symbol_address);
  mix_scaled_link_key(static_cast<uint32_t>(link.kind));
  if (!g_direct_renderer.private_copy_action_surface_link_frame_keys
           .insert(scaled_link_key)
           .second) {
    return;
  }

  RegisterDirectSurfaceTextureLinkPreparedLocked(
      link, reason ? reason : "scaled-private-rect-action");
  if (DirectSurfaceTextureLinkShouldPublishObjectMirror(link)) {
    FableSurfaceTextureLink object_link = link;
    object_link.serial = 0;
    object_link.source_slot = 0xFFFFFFFFu;
    RegisterDirectSurfaceTextureLinkPreparedLocked(
        object_link, "scaled-private-rect-action-object");
  }

  static uint32_t logged_scaled_private_rect_edges = 0;
  if (logged_scaled_private_rect_edges < 32u) {
    REXLOG_INFO(
        "Refii plume exact scaled private-rect surface-texture identity #{} "
        "frame={} reason={} draw={} slot={} "
        "texture=0x{:08X}/{}x{} base=0x{:08X} fmt={} "
        "surface=0x{:08X}/{}x{} target_fmt={} content={} "
        "kind={} call={} context=0x{:016X} symbol=0x{:08X} "
        "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
        "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
        ++logged_scaled_private_rect_edges, action.frame_index,
        reason ? reason : "scaled-private-rect-action",
        static_cast<unsigned long long>(action.draw_serial), link.source_slot,
        link.texture, link.texture_width, link.texture_height,
        link.texture_base_address, link.texture_format, link.source_surface,
        action.target_width, action.target_height, action.target_format,
        static_cast<unsigned long long>(link.target_content_serial),
        FablePrivateTextureCopyKindName(link.kind),
        static_cast<unsigned long long>(link.copy_call_serial),
        static_cast<unsigned long long>(link.copy_context_key),
        link.copy_symbol_address, link.render_target, link.depth_stencil,
        link.vertex_shader, link.pixel_shader,
        link.texture_fetch_dwords[0], link.texture_fetch_dwords[1],
        link.texture_fetch_dwords[2], link.texture_fetch_dwords[3],
        link.texture_fetch_dwords[4], link.texture_fetch_dwords[5]);
  }
}


void RememberDirectPrivateTextureCopyActionLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    FablePrivateTextureCopyKind kind, bool allow_materialized_surface_link) {
  const FableDirectClock::time_point remember_t0 = FableDirectClock::now();
  double resolve_ms = 0.0;
  double register_ms = 0.0;
  double exact_ms = 0.0;
  double exact_index_ms = 0.0;
  double exact_link_ms = 0.0;
  double exact_alias_ms = 0.0;
  double exact_sampled_link_ms = 0.0;
  double exact_sampled_alias_ms = 0.0;
  double surface_link_ms = 0.0;
  double alias_ms = 0.0;

  if (!DirectCommandUsesFablePrivateTexturePath(command, slot, &source)) {
    return;
  }
  if (FableTrackedResourceHasMeaningfulTextureUpload(source) &&
      !FableTrackedResourceIsBlankUploadedSampleProxy(source) &&
      !DirectPrivateTextureCopyActionAllowsUploadedSource(source, target,
                                                         kind)) {
    return;
  }

  const std::array<uint32_t, 6> command_fetch_dwords =
      DirectCommandTextureFetchDwords(command, slot);
  uint64_t preflight_key = 1469598103934665603ull;
  auto mix_preflight_key = [&](uint64_t value) {
    preflight_key ^= value;
    preflight_key *= 1099511628211ull;
  };
  mix_preflight_key(g_direct_renderer.current_frame_index);
  mix_preflight_key(command.draw_serial != 0 ? command.draw_serial
                                             : command.serial);
  mix_preflight_key(slot);
  mix_preflight_key(command.resources.textures[slot]);
  mix_preflight_key(source.guest_address);
  mix_preflight_key(source.parent_address);
  mix_preflight_key(PlumeGuestTextureBaseAddress(source));
  mix_preflight_key(FableDirectTextureFetchKey(command_fetch_dwords));
  mix_preflight_key(target.guest_address);
  mix_preflight_key(target.parent_address);
  mix_preflight_key(PlumeGuestTextureBaseAddress(target));
  mix_preflight_key(FableTrackedResourceVisibleContentSerial(source));
  mix_preflight_key(FableTrackedResourceVisibleContentSerial(target));
  mix_preflight_key(command.resources.render_targets[0]);
  mix_preflight_key(command.resources.depth_stencil);
  mix_preflight_key(command.resources.vertex_shader);
  mix_preflight_key(command.resources.pixel_shader);
  mix_preflight_key(command.resources.shader_roots.vertex_shader.symbol_address);
  mix_preflight_key(command.resources.shader_roots.vertex_shader.call_serial);
  mix_preflight_key(command.resources.shader_roots.pixel_shader.symbol_address);
  mix_preflight_key(command.resources.shader_roots.pixel_shader.call_serial);
  mix_preflight_key(command.draw.state_flush_lr);
  mix_preflight_key(command.draw.private_texture_wrapper_call_serial);
  mix_preflight_key(command.draw.private_texture_wrapper_context_key);
  mix_preflight_key(command.draw.private_texture_wrapper_symbol_address);
  mix_preflight_key(command.draw.private_texture_wrapper_source_texture);
  mix_preflight_key(command.draw.private_texture_wrapper_source_exact ? 1u : 0u);
  mix_preflight_key(static_cast<uint32_t>(kind));
  if (!g_direct_renderer.preflight_private_copy_action_frame_keys
           .insert(preflight_key)
           .second) {
    return;
  }

  FablePrivateTextureCopyAction action;
  action.source_slot = slot;
  action.source_texture = command.resources.textures[slot] != 0
                              ? command.resources.textures[slot]
                              : source.guest_address;
  action.source_base_address = PlumeGuestTextureBaseAddress(source);
  action.source_width = PlumeGuestTextureWidth(source);
  action.source_height = PlumeGuestTextureHeight(source);
  action.source_format = PlumeGuestTextureGuestFormat(source);
  action.source_fetch_dwords =
      HasTrackedFetchDwords(command_fetch_dwords)
          ? command_fetch_dwords
          : PlumeGuestTextureFetchDwords(source);
  PopulateDirectPrivateTextureCopyTargetLocked(action, target);
  const FableDirectClock::time_point resolve_t0 = FableDirectClock::now();
  const bool uploaded_source_sample =
      DirectPrivateTextureCopyActionCanSampleUploadedSource(command, source, target,
                                                           kind);
  PlumeTrackedResource* backing_source = nullptr;
  const bool current_gpu_surface_source =
      backing_source == nullptr &&
      DirectCurrentPrivateTextureActionCanSampleTargetSurfaceLocked(
          command, slot, source, target);
  if (!backing_source && !current_gpu_surface_source) {
    backing_source = ResolveDirectPrivateTextureActionBackingLocked(
        command, slot, source, target);
  }
  resolve_ms += FableDirectElapsedMs(resolve_t0, FableDirectClock::now());
  if (backing_source) {
    PopulateDirectPrivateTextureCopyBackingLocked(action, *backing_source);
  } else if (uploaded_source_sample) {
    PopulateDirectPrivateTextureCopySourceLocked(action, source);
  }
  if (!backing_source && !uploaded_source_sample &&
      action.target_content_serial == 0) {
    return;
  }
  action.render_target = command.resources.render_targets[0];
  action.depth_stencil = command.resources.depth_stencil;
  action.vertex_shader = command.resources.vertex_shader;
  action.pixel_shader = command.resources.pixel_shader;
  PopulateDirectPrivateTextureCopyShaderRoots(action, command);
  action.lr = command.draw.state_flush_lr;
  action.frame_index = g_direct_renderer.current_frame_index;
  action.draw_serial = command.draw_serial != 0 ? command.draw_serial
                                                : command.serial;
  action.kind = kind;
  action.rectangle_valid =
      command.draw.private_texture_rectangle &&
      command.draw.private_texture_corners_valid;
  if (action.rectangle_valid) {
    const auto& corners = command.draw.private_texture_corners;
    action.rect_width = std::fabs(corners[2] - corners[0]);
    action.rect_height = std::fabs(corners[5] - corners[1]);
  }
  action.source_x = command.draw.private_texture_source_x;
  action.source_y = command.draw.private_texture_source_y;
  action.uv_override = command.draw.private_texture_uv_override;
  action.u0 = command.draw.private_texture_u0;
  action.v0 = command.draw.private_texture_v0;
  action.u1 = command.draw.private_texture_u1;
  action.v1 = command.draw.private_texture_v1;
  action.wrapper_call_serial = command.draw.private_texture_wrapper_call_serial;
  action.wrapper_context_key = command.draw.private_texture_wrapper_context_key;
  action.wrapper_symbol_address =
      command.draw.private_texture_wrapper_symbol_address;
  PopulateDirectPrivateTextureWrapperSourceIdentity(action, command);
  RefreshDirectSetupFlushPrivateCopyActionGpuSurfaceSampleLocked(action,
                                                                 target);
  if (!allow_materialized_surface_link) {
    const uint64_t stable_action_key =
        DirectStablePrivateCopyActionKey(action, source, target, true,
                                         0xD1EC7A11u);
    if (!RememberDirectStablePrivateCopyActionKeyLocked(stable_action_key)) {
      return;
    }
  }
  uint64_t remember_key = 1469598103934665603ull;
  auto mix_remember_key = [&](uint64_t value) {
    remember_key ^= value;
    remember_key *= 1099511628211ull;
  };
  mix_remember_key(g_direct_renderer.current_frame_index);
  mix_remember_key(command.draw_serial != 0 ? command.draw_serial
                                            : command.serial);
  mix_remember_key(DirectPrivateRectExactActionLookupKey(action));
  mix_remember_key(command.draw.state_flush_lr);
  mix_remember_key(FableTrackedResourceVisibleContentSerial(source));
  mix_remember_key(FableTrackedResourceVisibleContentSerial(target));
  mix_remember_key(backing_source ? backing_source->guest_address : 0u);
  mix_remember_key(backing_source
                       ? FableTrackedResourceVisibleContentSerial(*backing_source)
                       : 0u);
  if (!g_direct_renderer.remembered_private_copy_action_frame_keys
           .insert(remember_key)
           .second) {
    return;
  }
  const bool duplicate_title_content_edge_can_skip =
      command.draw.source == RendererDrawEventSource::kSceneObject &&
      !command.draw.private_texture_rectangle &&
      (IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
       IsFableTitleSceneVertexShaderCommandLocked(command) ||
       IsFableTitleSceneTextureSyncCommandLocked(command) ||
       command.draw.private_texture_wrapper_symbol_address ==
           kFableBeginVerticesTitleWrapperSymbol);
  const bool duplicate_native_depth_content_edge_can_skip =
      command.draw.source != RendererDrawEventSource::kPixVideo &&
      action.kind == FablePrivateTextureCopyKind::kDepth &&
      action.target_content_serial != 0 &&
      action.sampled_source.kind == FableRegisteredSampleSourceKind::kGpuSurface;
  if ((duplicate_title_content_edge_can_skip ||
       duplicate_native_depth_content_edge_can_skip) &&
      !RememberDirectStablePrivateCopyActionKeyLocked(
          DirectPrivateCopyActionContentEdgeKey(action, source, target))) {
    static uint32_t logged_duplicate_title_private_copy_edges = 0;
    if (RendererVerboseTracingEnabled() &&
        logged_duplicate_title_private_copy_edges < 48u) {
      REXLOG_INFO(
          "Refii plume exact duplicate title/private copy edge covered #{} "
          "frame={} serial={} draw={} slot={} src=0x{:08X} "
          "target=0x{:08X} source_content={} target_content={} "
          "symbol=0x{:08X} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
          "ps=0x{:08X}",
          ++logged_duplicate_title_private_copy_edges,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.serial),
          static_cast<unsigned long long>(command.draw_serial), slot,
          source.guest_address, target.guest_address,
          static_cast<unsigned long long>(
              FableTrackedResourceVisibleContentSerial(source)),
          static_cast<unsigned long long>(action.target_content_serial),
          action.wrapper_symbol_address, command.resources.render_targets[0],
          command.resources.depth_stencil, command.resources.vertex_shader,
          command.resources.pixel_shader);
    }
    return;
  }

  const FableDirectClock::time_point register_t0 = FableDirectClock::now();
  RegisterDirectPrivateTextureCopyActionLocked(action);
  register_ms += FableDirectElapsedMs(register_t0, FableDirectClock::now());
  const FableDirectClock::time_point exact_t0 = FableDirectClock::now();
  FableDirectClock::time_point exact_step_t0 = FableDirectClock::now();
  RememberDirectPrivateRectExactActionLocked(action);
  exact_index_ms +=
      FableDirectElapsedMs(exact_step_t0, FableDirectClock::now());
  exact_step_t0 = FableDirectClock::now();
  RegisterDirectSurfaceTextureLinkForPrivateCopyActionLocked(
      action, "private-copy-action-exact");
  exact_link_ms +=
      FableDirectElapsedMs(exact_step_t0, FableDirectClock::now());
  exact_step_t0 = FableDirectClock::now();
  TrackDirectExactSurfaceAliasForPrivateCopyActionLocked(
      action, "private-copy-action-exact");
  exact_alias_ms +=
      FableDirectElapsedMs(exact_step_t0, FableDirectClock::now());
  if (source.guest_address != 0 && source.guest_address != action.source_texture) {
    FablePrivateTextureCopyAction sampled_source_action = action;
    sampled_source_action.source_texture = source.guest_address;
    sampled_source_action.source_base_address = PlumeGuestTextureBaseAddress(source);
    sampled_source_action.source_width = PlumeGuestTextureWidth(source);
    sampled_source_action.source_height = PlumeGuestTextureHeight(source);
    sampled_source_action.source_format = PlumeGuestTextureGuestFormat(source);
    sampled_source_action.source_fetch_dwords =
        HasTrackedFetchDwords(command_fetch_dwords)
            ? command_fetch_dwords
            : PlumeGuestTextureFetchDwords(source);
    RefreshDirectSetupFlushPrivateCopyActionGpuSurfaceSampleLocked(
        sampled_source_action, target);
    exact_step_t0 = FableDirectClock::now();
    RegisterDirectSurfaceTextureLinkForPrivateCopyActionLocked(
        sampled_source_action, "private-copy-action-sampled-source");
    exact_sampled_link_ms +=
        FableDirectElapsedMs(exact_step_t0, FableDirectClock::now());
    exact_step_t0 = FableDirectClock::now();
    TrackDirectExactSurfaceAliasForPrivateCopyActionLocked(
        sampled_source_action, "private-copy-action-sampled-source");
    exact_sampled_alias_ms +=
        FableDirectElapsedMs(exact_step_t0, FableDirectClock::now());
  }
  exact_ms += FableDirectElapsedMs(exact_t0, FableDirectClock::now());
  const FableDirectClock::time_point surface_link_t0 = FableDirectClock::now();
  const bool exact_native_depth_private_rect =
      kind == FablePrivateTextureCopyKind::kDepth &&
      command.draw.private_texture_rectangle &&
      IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader) &&
      DirectSyntheticPrivateRectCommandUsesDepthTarget(command);
  if (allow_materialized_surface_link &&
      !exact_native_depth_private_rect &&
      !RememberDirectExactPrivateRectSurfaceTextureEdgesLocked(command, target,
                                                              kind)) {
    RememberDirectMaterializedPrivateSourceSurfaceLinkLocked(
        command, slot, source, target, kind, "private-copy-action",
        backing_source, true);
  }
  surface_link_ms +=
      FableDirectElapsedMs(surface_link_t0, FableDirectClock::now());
  const bool source_has_registered_content =
      FableTrackedResourceHasSampleableContent(source) &&
      !FableTrackedResourceIsBlankUploadedSampleProxy(source) &&
      !FableTrackedResourceIsUnbackedSampleProxy(source);
  const PlumeTrackedResource* action_alias_resource =
      backing_source ? backing_source
                     : (source_has_registered_content ? &source : nullptr);
  if (action_alias_resource) {
    const bool include_equivalent_identity_aliases =
        backing_source == nullptr;
    const FableDirectClock::time_point alias_t0 = FableDirectClock::now();
    auto remember_action_source_alias = [&](uint32_t source_guest,
                                            uint32_t base_address,
                                            const std::array<uint32_t, 6>&
                                                fetch_dwords) {
      if (source_guest == 0 && base_address == 0 &&
          !HasTrackedFetchDwords(fetch_dwords)) {
        return;
      }
      RememberDirectPrivateTextureAliasIndexLocked(
          source_guest, base_address, fetch_dwords, *action_alias_resource,
          include_equivalent_identity_aliases);
    };
    remember_action_source_alias(action.source_texture,
                                 action.source_base_address,
                                 action.source_fetch_dwords);
    remember_action_source_alias(source.guest_address,
                                 action.source_base_address,
                                 action.source_fetch_dwords);
    if (source.parent_address != 0) {
      remember_action_source_alias(source.parent_address,
                                   action.source_base_address,
                                   action.source_fetch_dwords);
    }

    auto remember_action_source_identity = [&](uint32_t source_object) {
      const FableTextureIdentity* identity =
          FableDirectFindTextureIdentityByObject(source_object);
      if (!identity) {
        return;
      }
      remember_action_source_alias(identity->object_texture,
                                   identity->base_address,
                                   identity->fetch_dwords);
      if (identity->parent_texture != 0) {
        remember_action_source_alias(identity->parent_texture,
                                     identity->base_address,
                                     identity->fetch_dwords);
      }
    };
    remember_action_source_identity(action.source_texture);
    remember_action_source_identity(source.guest_address);
    if (source.parent_address != 0) {
      remember_action_source_identity(source.parent_address);
    }
    alias_ms += FableDirectElapsedMs(alias_t0, FableDirectClock::now());
  }

  const double total_ms =
      FableDirectElapsedMs(remember_t0, FableDirectClock::now());
  if ((total_ms >= 1.0 || resolve_ms >= 0.5 || alias_ms >= 0.5 ||
       surface_link_ms >= 0.5) &&
      g_direct_renderer.diagnostics.logged_private_copy_action_timing_breakdowns < 128u) {
    REXLOG_INFO(
        "Refii plume private copy action timing #{} frame={} serial={} "
        "draw={} slot={} src=0x{:08X} target=0x{:08X} "
        "total_ms={:.3f} resolve_ms={:.3f} register_ms={:.3f} "
        "exact_ms={:.3f} exact_index_ms={:.3f} exact_link_ms={:.3f} "
        "exact_alias_ms={:.3f} exact_sampled_link_ms={:.3f} "
        "exact_sampled_alias_ms={:.3f} surface_link_ms={:.3f} alias_ms={:.3f} "
        "backing=0x{:08X} source_content={} target_content={} kind={}",
        ++g_direct_renderer.diagnostics.logged_private_copy_action_timing_breakdowns,
        g_direct_renderer.current_frame_index,
        static_cast<unsigned long long>(command.serial),
        static_cast<unsigned long long>(command.draw_serial), slot,
        source.guest_address, target.guest_address, total_ms, resolve_ms,
        register_ms, exact_ms, exact_index_ms, exact_link_ms, exact_alias_ms,
        exact_sampled_link_ms, exact_sampled_alias_ms, surface_link_ms, alias_ms,
        backing_source ? backing_source->guest_address : 0u,
        static_cast<unsigned long long>(
            FableTrackedResourceVisibleContentSerial(source)),
        static_cast<unsigned long long>(
            FableTrackedResourceVisibleContentSerial(target)),
        static_cast<uint32_t>(kind));
  }
}

void RememberDirectMaterializedPrivateSourceSurfaceLinkLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    FablePrivateTextureCopyKind kind, const char* reason,
    const PlumeTrackedResource* known_backing, bool backing_resolved) {
  if (slot >= command.resources.textures.size() ||
      command.draw.source == RendererDrawEventSource::kPixVideo ||
      source.guest_address == 0 || target.guest_address == 0 ||
      source.guest_address == target.guest_address ||
      !IsImageResource(source.kind) || !IsImageResource(target.kind) ||
      !PlumeGuestImageReady(source) || !PlumeGuestImageReady(target) ||
      !DirectCommandUsesFablePrivateTexturePath(command, slot, &source) ||
      !DirectCommandBindsResourceAsTargetLocked(command, target)) {
    return;
  }

  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const std::array<uint32_t, 6> source_fetch =
      PlumeGuestTextureFetchDwords(source);
  if (!HasTrackedFetchDwords(command_fetch) &&
      !HasTrackedFetchDwords(source_fetch)) {
    return;
  }

  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  const uint32_t source_guest = command.resources.textures[slot] != 0
                                    ? command.resources.textures[slot]
                                    : source.guest_address;

  FableSurfaceTextureLink link;
  link.source_slot = slot;
  link.trusted_destination_edge = true;
  link.texture = source_guest;
  link.texture_base_address = command_fetch_info.valid
                                  ? command_fetch_info.base_address
                                  : PlumeGuestTextureBaseAddress(source);
  link.texture_width = command_fetch_info.valid
                           ? command_fetch_info.width
                           : PlumeGuestTextureWidth(source);
  link.texture_height = command_fetch_info.valid
                            ? command_fetch_info.height
                            : PlumeGuestTextureHeight(source);
  link.texture_format = command_fetch_info.valid
                            ? command_fetch_info.format
                            : PlumeGuestTextureGuestFormat(source);
  link.texture_fetch_dwords =
      HasTrackedFetchDwords(command_fetch) ? command_fetch : source_fetch;
  link.source_surface = target.guest_address;
  link.target_content_serial = FableTrackedResourceVisibleContentSerial(target);
  link.render_target = command.resources.render_targets[0];
  link.depth_stencil = command.resources.depth_stencil;
  link.vertex_shader = command.resources.vertex_shader;
  link.pixel_shader = command.resources.pixel_shader;
  PopulateDirectSurfaceTextureLinkShaderRoots(link, command);
  link.copy_call_serial = command.draw.private_texture_wrapper_call_serial;
  link.copy_context_key = command.draw.private_texture_wrapper_context_key;
  link.copy_symbol_address =
      command.draw.private_texture_wrapper_symbol_address;
  link.frame_index = g_direct_renderer.current_frame_index;
  link.draw_serial = command.draw_serial != 0 ? command.draw_serial
                                              : command.serial;
  link.kind = kind;
  if (known_backing) {
    PopulateDirectSurfaceTextureLinkBackingLocked(link, *known_backing);
  } else if (DirectPrivateTextureCopyActionCanSampleUploadedSource(
                 command, source, target, kind)) {
    PopulateDirectSurfaceTextureLinkSourceLocked(link, source);
  } else if (!backing_resolved &&
             (known_backing = ResolveDirectPrivateTextureActionBackingLocked(
                  command, slot, source, target)) != nullptr) {
    const PlumeTrackedResource* backing = known_backing;
    PopulateDirectSurfaceTextureLinkBackingLocked(link, *backing);
  }

  uint64_t materialized_link_key = 1469598103934665603ull;
  auto mix_materialized_link_key = [&](uint64_t value) {
    materialized_link_key ^= value;
    materialized_link_key *= 1099511628211ull;
  };
  mix_materialized_link_key(g_direct_renderer.current_frame_index);
  mix_materialized_link_key(link.texture);
  mix_materialized_link_key(link.texture_base_address);
  mix_materialized_link_key(FableDirectTextureFetchKey(link.texture_fetch_dwords));
  mix_materialized_link_key(link.texture_width);
  mix_materialized_link_key(link.texture_height);
  mix_materialized_link_key(link.texture_format);
  mix_materialized_link_key(link.source_surface);
  mix_materialized_link_key(link.target_content_serial);
  mix_materialized_link_key(link.render_target);
  mix_materialized_link_key(link.depth_stencil);
  mix_materialized_link_key(link.vertex_shader);
  mix_materialized_link_key(link.pixel_shader);
  mix_materialized_link_key(link.copy_call_serial);
  mix_materialized_link_key(link.copy_context_key);
  mix_materialized_link_key(link.copy_symbol_address);
  mix_materialized_link_key(static_cast<uint32_t>(link.kind));
  mix_materialized_link_key(static_cast<uint32_t>(link.sampled_source.kind));
  mix_materialized_link_key(link.sampled_source.resource);
  mix_materialized_link_key(link.sampled_source.base_address);
  mix_materialized_link_key(
      FableDirectTextureFetchKey(link.sampled_source.fetch_dwords));
  mix_materialized_link_key(link.sampled_source.content_serial);
  if (!g_direct_renderer.materialized_private_surface_link_frame_keys
           .insert(materialized_link_key)
           .second) {
    return;
  }

  RegisterDirectSurfaceTextureLinkPreparedLocked(link, "materialized-private");
  if (DirectSurfaceTextureLinkShouldPublishObjectMirror(link)) {
    FableSurfaceTextureLink object_link = link;
    object_link.serial = 0;
    object_link.source_slot = 0xFFFFFFFFu;
    RegisterDirectSurfaceTextureLinkPreparedLocked(
        object_link, "materialized-private-object");
  }

  static uint32_t logged_exact_materialized_edges = 0;
  if (logged_exact_materialized_edges < 32u) {
    REXLOG_INFO(
        "Refii plume materialized exact surface-texture edge #{} "
        "frame={} reason={} slot={} texture=0x{:08X}/{}x{} "
        "base=0x{:08X} surface=0x{:08X}/{}x{} kind={} "
        "target_serial={} rt=0x{:08X} ds=0x{:08X} "
        "vs=0x{:08X} ps=0x{:08X}",
        ++logged_exact_materialized_edges,
        g_direct_renderer.current_frame_index, reason ? reason : "unknown",
        slot, link.texture, link.texture_width, link.texture_height,
        link.texture_base_address, link.source_surface,
        PlumeGuestTextureWidth(target), PlumeGuestTextureHeight(target),
        FablePrivateTextureCopyKindName(kind),
        static_cast<unsigned long long>(link.target_content_serial),
        link.render_target, link.depth_stencil, link.vertex_shader,
        link.pixel_shader);
  }
}

bool RememberDirectExactPrivateRectSurfaceTextureEdgesLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& target, FablePrivateTextureCopyKind kind) {
  if (command.draw.source == RendererDrawEventSource::kPixVideo ||
      !IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader) ||
      target.guest_address == 0 || !IsImageResource(target.kind) ||
      !PlumeGuestImageReady(target) ||
      !DirectCommandBindsResourceAsTargetLocked(command, target)) {
    return false;
  }
  const bool has_exact_wrapper_identity =
      command.draw.private_texture_wrapper_call_serial != 0 ||
      command.draw.private_texture_wrapper_context_key != 0 ||
      command.draw.private_texture_wrapper_symbol_address != 0;
  if (!has_exact_wrapper_identity) {
    return false;
  }
  uint64_t action_edge_key = 1469598103934665603ull;
  auto mix_action_edge = [&](uint64_t value) {
    action_edge_key ^= value;
    action_edge_key *= 1099511628211ull;
  };
  mix_action_edge(command.serial);
  mix_action_edge(command.draw_serial);
  mix_action_edge(target.guest_address);
  mix_action_edge(command.resources.render_targets[0]);
  mix_action_edge(command.resources.depth_stencil);
  mix_action_edge(command.resources.vertex_shader);
  mix_action_edge(command.resources.pixel_shader);
  mix_action_edge(command.draw.private_texture_wrapper_call_serial);
  mix_action_edge(command.draw.private_texture_wrapper_context_key);
  mix_action_edge(command.draw.private_texture_wrapper_symbol_address);
  mix_action_edge(command.draw.private_texture_wrapper_source_texture);
  mix_action_edge(command.draw.private_texture_wrapper_source_exact ? 1u : 0u);
  mix_action_edge(static_cast<uint32_t>(kind));
  if (!g_direct_renderer.exact_private_rect_edge_frame_keys
           .insert(action_edge_key)
           .second) {
    return true;
  }

  const uint32_t texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(command);
  auto command_uses_source_slot = [&](uint32_t slot) {
    return slot < command.resources.textures.size() &&
           (texture_slot_mask == 0u || slot >= 32u ||
            (texture_slot_mask & (1u << slot)) != 0);
  };

  std::unordered_set<uint64_t> remembered_edges;
  bool remembered_exact_edge = false;
  auto remember_slot = [&](uint32_t slot) {
    if (!command_uses_source_slot(slot)) {
      return;
    }
    if (slot >= command.resources.textures.size()) {
      return;
    }
    const uint32_t texture_guest = command.resources.textures[slot];
    if (texture_guest == 0 || texture_guest == target.guest_address) {
      return;
    }

    const std::array<uint32_t, 6> command_fetch =
        DirectCommandTextureFetchDwords(command, slot);
    if (!HasTrackedFetchDwords(command_fetch)) {
      return;
    }

    PlumeTrackedResource* texture = FindDirectResourceLocked(texture_guest);
    if (texture && DirectCommandBindsResourceAsTargetLocked(command, *texture)) {
      return;
    }

    const DirectTextureFetchInfo fetch_info =
        DirectTextureFetchInfoFromDwords(command_fetch);
    if (!fetch_info.valid) {
      return;
    }

    uint64_t edge_key = 1469598103934665603ull;
    auto mix_edge = [&](uint64_t value) {
      edge_key ^= value;
      edge_key *= 1099511628211ull;
    };
    mix_edge(slot);
    mix_edge(texture_guest);
    mix_edge(FableDirectTextureFetchKey(command_fetch));
    mix_edge(target.guest_address);
    mix_edge(command.resources.render_targets[0]);
    mix_edge(command.resources.depth_stencil);
    mix_edge(command.resources.vertex_shader);
    mix_edge(command.resources.pixel_shader);
    mix_edge(command.draw.private_texture_wrapper_call_serial);
    mix_edge(command.draw.private_texture_wrapper_context_key);
    mix_edge(command.draw.private_texture_wrapper_symbol_address);
    mix_edge(command.draw.private_texture_wrapper_source_texture);
    mix_edge(command.draw.private_texture_wrapper_source_exact ? 1u : 0u);
    mix_edge(static_cast<uint32_t>(kind));
    if (!remembered_edges.insert(edge_key).second) {
      return;
    }

    FableSurfaceTextureLink link;
    link.source_slot = slot;
    link.trusted_destination_edge = true;
    link.texture = texture_guest;
    link.texture_base_address = fetch_info.base_address;
    link.texture_width = fetch_info.width;
    link.texture_height = fetch_info.height;
    link.texture_format = fetch_info.format;
    link.texture_fetch_dwords = command_fetch;
    link.source_surface = target.guest_address;
    link.target_content_serial = FableTrackedResourceVisibleContentSerial(target);
    link.render_target = command.resources.render_targets[0];
    link.depth_stencil = command.resources.depth_stencil;
    link.vertex_shader = command.resources.vertex_shader;
    link.pixel_shader = command.resources.pixel_shader;
    PopulateDirectSurfaceTextureLinkShaderRoots(link, command);
    link.copy_call_serial = command.draw.private_texture_wrapper_call_serial;
    link.copy_context_key = command.draw.private_texture_wrapper_context_key;
    link.copy_symbol_address =
        command.draw.private_texture_wrapper_symbol_address;
    link.wrapper_source_texture =
        command.draw.private_texture_wrapper_source_texture;
	    link.wrapper_source_exact =
	        command.draw.private_texture_wrapper_source_exact;
	    link.frame_index = g_direct_renderer.current_frame_index;
	    link.draw_serial = command.draw_serial != 0 ? command.draw_serial
	                                                : command.serial;
	    link.kind = kind;
	    PlumeTrackedResource* backing = nullptr;
	    bool current_gpu_surface_source = false;
	    if (texture) {
      current_gpu_surface_source =
          backing == nullptr &&
          DirectCurrentPrivateTextureActionCanSampleTargetSurfaceLocked(
              command, slot, *texture, target);
      if (!backing && !current_gpu_surface_source) {
        backing = ResolveDirectPrivateTextureActionBackingLocked(
            command, slot, *texture, target);
      }
    }
    if (backing) {
      PopulateDirectSurfaceTextureLinkBackingLocked(link, *backing);
    } else if (current_gpu_surface_source || link.target_content_serial != 0 ||
               FableTrackedResourceHasGpuProducedContent(target)) {
      PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(link, target);
    }
    RegisterDirectSurfaceTextureLinkPreparedLocked(
        link, "private-rect-exact-current-target");
    if (DirectSurfaceTextureLinkShouldPublishObjectMirror(link)) {
      FableSurfaceTextureLink object_link = link;
      object_link.serial = 0;
      object_link.source_slot = 0xFFFFFFFFu;
      RegisterDirectSurfaceTextureLinkPreparedLocked(
          object_link, "private-rect-exact-current-target-object");
    }

    FablePrivateTextureCopyAction action;
    action.source_slot = slot;
    action.source_texture = texture_guest;
    action.source_base_address = fetch_info.base_address;
    action.source_width = fetch_info.width;
    action.source_height = fetch_info.height;
    action.source_format = fetch_info.format;
    action.source_fetch_dwords = command_fetch;
    PopulateDirectPrivateTextureCopyTargetLocked(action, target);
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
    action.draw_serial = command.draw_serial != 0 ? command.draw_serial
                                                  : command.serial;
    action.kind = kind;
    if (backing) {
      PopulateDirectPrivateTextureCopyBackingLocked(action, *backing);
    }
    action.rectangle_valid =
        command.draw.private_texture_rectangle &&
        command.draw.private_texture_corners_valid;
    if (action.rectangle_valid) {
      const auto& corners = command.draw.private_texture_corners;
      action.rect_width = std::fabs(corners[2] - corners[0]);
      action.rect_height = std::fabs(corners[5] - corners[1]);
    }
    action.source_x = command.draw.private_texture_source_x;
    action.source_y = command.draw.private_texture_source_y;
    action.uv_override = command.draw.private_texture_uv_override;
    action.u0 = command.draw.private_texture_u0;
    action.v0 = command.draw.private_texture_v0;
    action.u1 = command.draw.private_texture_u1;
    action.v1 = command.draw.private_texture_v1;
    RegisterDirectPrivateTextureCopyActionLocked(action);
    RememberDirectPrivateRectExactActionLocked(action);
    RegisterDirectSurfaceTextureLinkForPrivateCopyActionLocked(
        action, "private-rect-exact-current-target-action");
    TrackDirectExactSurfaceAliasForPrivateCopyActionLocked(
        action, "private-rect-exact-current-target");
    RememberDirectPrivateTextureAliasIndexLocked(
        action.source_texture, action.source_base_address,
        action.source_fetch_dwords, target);
    remembered_exact_edge = true;

    static uint32_t logged_exact_current_target_ownership_proofs = 0;
    if (logged_exact_current_target_ownership_proofs < 32u) {
      REXLOG_INFO(
          "Refii plume private rect exact current-target ownership proof #{} "
          "reason=private-rect-exact-current-target "
          "frame={} slot={} texture=0x{:08X}/{}x{} base=0x{:08X} "
          "surface=0x{:08X}/{}x{} kind={} target_serial={} call={} "
          "context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} ds=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_current_target_ownership_proofs,
          g_direct_renderer.current_frame_index,
          slot, link.texture, link.texture_width, link.texture_height,
          link.texture_base_address, link.source_surface,
          PlumeGuestTextureWidth(target), PlumeGuestTextureHeight(target),
          FablePrivateTextureCopyKindName(kind),
          static_cast<unsigned long long>(link.target_content_serial),
          static_cast<unsigned long long>(link.copy_call_serial),
          static_cast<unsigned long long>(link.copy_context_key),
          link.copy_symbol_address, link.render_target, link.depth_stencil,
          link.vertex_shader, link.pixel_shader);
    }
    static uint32_t logged_large_exact_current_target_ownership_proofs = 0;
    if (logged_large_exact_current_target_ownership_proofs < 16u &&
        PlumeGuestTextureWidth(target) >= 900u &&
        PlumeGuestTextureHeight(target) >= 500u) {
      REXLOG_INFO(
          "Refii plume private rect exact large current-target ownership proof "
          "#{} reason=private-rect-exact-current-target-large "
          "frame={} slot={} texture=0x{:08X}/{}x{} base=0x{:08X} "
          "surface=0x{:08X}/{}x{} kind={} target_serial={} call={} "
          "context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} ds=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X}",
          ++logged_large_exact_current_target_ownership_proofs,
          g_direct_renderer.current_frame_index, slot, link.texture,
          link.texture_width, link.texture_height,
          link.texture_base_address, link.source_surface,
          PlumeGuestTextureWidth(target), PlumeGuestTextureHeight(target),
          FablePrivateTextureCopyKindName(kind),
          static_cast<unsigned long long>(link.target_content_serial),
          static_cast<unsigned long long>(link.copy_call_serial),
          static_cast<unsigned long long>(link.copy_context_key),
          link.copy_symbol_address, link.render_target, link.depth_stencil,
          link.vertex_shader, link.pixel_shader);
    }
	  };

  remember_slot(source_slot);
  remember_slot(command.draw.private_texture_slot & (kMaxFableTextureSlots - 1u));
  remember_slot(kFablePrivateRectCopyTextureSlot);
  return remembered_exact_edge;
}

void RememberMaterializedPrivateRectTargetSourcesLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& target) {
  const bool copy_shader =
      IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader);
  if ((!command.draw.private_texture_rectangle && !copy_shader) ||
      command.draw.source == RendererDrawEventSource::kPixVideo ||
      !copy_shader ||
      !IsImageResource(target.kind) || !PlumeGuestImageReady(target) ||
      target.guest_address == 0) {
    return;
  }

  const bool target_is_depth =
      target.texture_depth_target ||
      (command.resources.depth_stencil != 0 &&
       (target.guest_address == command.resources.depth_stencil ||
        target.parent_address == command.resources.depth_stencil ||
        target.guest_texture.object_address == command.resources.depth_stencil ||
        target.guest_texture.parent_address == command.resources.depth_stencil));
  if (DirectSyntheticPrivateRectCommandUsesDepthTarget(command) &&
      !target_is_depth) {
    return;
  }

  const FablePrivateTextureCopyKind kind =
      DirectSyntheticPrivateRectCopyKindForTarget(command, target);
  if (RememberDirectExactPrivateRectSurfaceTextureEdgesLocked(command, target,
                                                             kind)) {
    return;
  }

  uint64_t target_sources_key = 1469598103934665603ull;
  auto mix_target_sources_key = [&](uint64_t value) {
    target_sources_key ^= value;
    target_sources_key *= 1099511628211ull;
  };
  const uint32_t texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(command);
  auto command_uses_source_slot = [&](uint32_t slot) {
    return slot < command.resources.textures.size() &&
           (texture_slot_mask == 0u || slot >= 32u ||
            (texture_slot_mask & (1u << slot)) != 0);
  };
  auto mix_target_source_slot = [&](uint32_t slot) {
    if (!command_uses_source_slot(slot)) {
      return;
    }
    mix_target_sources_key(slot);
    if (slot >= command.resources.textures.size()) {
      mix_target_sources_key(0u);
      mix_target_sources_key(0u);
      return;
    }
    mix_target_sources_key(command.resources.textures[slot]);
    mix_target_sources_key(
        FableDirectTextureFetchKey(DirectCommandTextureFetchDwords(command,
                                                                  slot)));
  };
  mix_target_sources_key(g_direct_renderer.current_frame_index);
  mix_target_sources_key(command.draw_serial != 0 ? command.draw_serial
                                                  : command.serial);
  mix_target_sources_key(target.guest_address);
  mix_target_sources_key(target.parent_address);
  mix_target_sources_key(PlumeGuestTextureBaseAddress(target));
  mix_target_sources_key(FableDirectTextureFetchKey(
      PlumeGuestTextureFetchDwords(target)));
  mix_target_sources_key(PlumeGuestTextureWidth(target));
  mix_target_sources_key(PlumeGuestTextureHeight(target));
  mix_target_sources_key(PlumeGuestTextureGuestFormat(target));
  mix_target_sources_key(FableTrackedResourceVisibleContentSerial(target));
  mix_target_sources_key(command.resources.render_targets[0]);
  mix_target_sources_key(command.resources.depth_stencil);
  mix_target_sources_key(command.resources.vertex_shader);
  mix_target_sources_key(command.resources.pixel_shader);
  mix_target_sources_key(command.draw.state_flush_lr);
  mix_target_sources_key(static_cast<uint32_t>(kind));
  mix_target_source_slot(source_slot);
  mix_target_source_slot(command.draw.private_texture_slot &
                         (kMaxFableTextureSlots - 1u));
  mix_target_source_slot(kFablePrivateRectCopyTextureSlot);
  if (!g_direct_renderer.remembered_private_rect_target_source_frame_keys
           .insert(target_sources_key)
           .second) {
    return;
  }

  std::unordered_set<uint64_t> remembered_source_edges;
  auto remember_slot = [&](uint32_t slot) {
    if (!command_uses_source_slot(slot)) {
      return;
    }
    if (slot >= command.resources.textures.size()) {
      return;
    }
    const uint32_t source_guest = command.resources.textures[slot];
    if (source_guest == 0 || source_guest == target.guest_address) {
      return;
    }
    if (!HasTrackedFetchDwords(DirectCommandTextureFetchDwords(command, slot))) {
      return;
    }

    PlumeTrackedResource* source = FindDirectResourceLocked(source_guest);
    if (!source || source == &target || !IsImageResource(source->kind) ||
        !PlumeGuestImageReady(*source) ||
        !DirectCommandUsesFablePrivateTexturePath(command, slot, source) ||
        DirectCommandBindsResourceAsTargetLocked(command, *source)) {
      return;
    }
    if (FableTrackedResourceHasMeaningfulTextureUpload(*source) &&
        !FableTrackedResourceIsBlankUploadedSampleProxy(*source) &&
        !DirectPrivateTextureCopyActionAllowsUploadedSource(*source, target,
                                                           kind)) {
      return;
    }
    if (kind == FablePrivateTextureCopyKind::kDepth &&
        DirectSyntheticPrivateRectCommandUsesDepthTarget(command)) {
      DirectPrivateTextureCopyActionMatch action_match =
          DirectPrivateTextureCopyActionMatch::kNone;
      const FablePrivateTextureCopyAction* exact_depth_action =
          SelectDirectExactPrivateTextureCopyActionForCommandSlotLocked(
              command, slot, source, &action_match);
      if (exact_depth_action &&
          DirectPrivateTextureCopyActionMatchIsExactRegistered(action_match) &&
          exact_depth_action->kind == FablePrivateTextureCopyKind::kDepth &&
          DirectPrivateTextureCopyActionCommandSlotMatches(
              command, *exact_depth_action) &&
          (DirectResourceAddressMatchesLocked(
               exact_depth_action->target_surface, target.guest_address) ||
           DirectResourceAddressMatchesLocked(exact_depth_action->depth_stencil,
                                              target.guest_address) ||
           DirectResourceAddressMatchesLocked(exact_depth_action->depth_stencil,
                                              command.resources.depth_stencil))) {
        return;
      }
      if (FindDirectPrivateRectExactActionLocked(command, slot, *source,
                                                target)) {
        return;
      }
    }

    uint64_t source_edge_key = 1469598103934665603ull;
    auto mix_source_edge = [&](uint64_t value) {
      source_edge_key ^= value;
      source_edge_key *= 1099511628211ull;
    };
    const std::array<uint32_t, 6> command_fetch =
        DirectCommandTextureFetchDwords(command, slot);
    const std::array<uint32_t, 6> source_fetch =
        PlumeGuestTextureFetchDwords(*source);
    mix_source_edge(slot);
    mix_source_edge(source_guest);
    mix_source_edge(source->guest_address);
    mix_source_edge(source->parent_address);
    mix_source_edge(PlumeGuestTextureBaseAddress(*source));
    mix_source_edge(HasTrackedFetchDwords(command_fetch)
                        ? FableDirectTextureFetchKey(command_fetch)
                        : FableDirectTextureFetchKey(source_fetch));
    mix_source_edge(PlumeGuestTextureWidth(*source));
    mix_source_edge(PlumeGuestTextureHeight(*source));
    mix_source_edge(PlumeGuestTextureGuestFormat(*source));
    mix_source_edge(target.guest_address);
    mix_source_edge(target.parent_address);
    mix_source_edge(PlumeGuestTextureWidth(target));
    mix_source_edge(PlumeGuestTextureHeight(target));
    mix_source_edge(PlumeGuestTextureGuestFormat(target));
    mix_source_edge(command.resources.render_targets[0]);
    mix_source_edge(command.resources.depth_stencil);
    mix_source_edge(command.resources.vertex_shader);
    mix_source_edge(command.resources.pixel_shader);
    mix_source_edge(command.draw.state_flush_lr);
    mix_source_edge(static_cast<uint32_t>(kind));
    if (!remembered_source_edges.insert(source_edge_key).second) {
      return;
    }

    const uint64_t action_key =
        DirectMaterializedPrivateRectActionFrameKey(command, slot, *source,
                                                   target, kind);
    if (!g_direct_renderer.materialized_private_rect_action_frame_keys
             .insert(action_key)
             .second) {
      return;
    }

    RememberDirectPrivateTextureCopyActionLocked(command, slot, *source, target,
                                                kind);
    RememberDirectMaterializedPrivateSourceSurfaceLinkLocked(
        command, slot, *source, target, kind, "materialized-private-rect");
    if (g_direct_renderer.diagnostics.logged_materialized_private_rect_copy_actions < 32u) {
      REXLOG_INFO(
          "Refii plume registered materialized private rect action #{} "
          "frame={} slot={} source=0x{:08X}/{}x{} fmt={} "
          "target=0x{:08X}/{}x{} kind={} rt=0x{:08X} ds=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X}",
          ++g_direct_renderer.diagnostics.logged_materialized_private_rect_copy_actions,
          g_direct_renderer.current_frame_index, slot, source->guest_address,
          PlumeGuestTextureWidth(*source), PlumeGuestTextureHeight(*source),
          static_cast<uint32_t>(PlumeGuestTextureFormat(*source)),
          target.guest_address, PlumeGuestTextureWidth(target),
          PlumeGuestTextureHeight(target), FablePrivateTextureCopyKindName(kind),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
  };

  remember_slot(source_slot);
  remember_slot(command.draw.private_texture_slot & (kMaxFableTextureSlots - 1u));
  remember_slot(kFablePrivateRectCopyTextureSlot);
}
uint64_t DirectPrivateFeedbackAliasKey(uint32_t source_guest,
                                       const PlumeTrackedResource& source,
                                       const PlumeTrackedResource& target) {
  uint64_t key = 1469598103934665603ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };
  mix(source_guest);
  mix(source.guest_address);
  mix(source.parent_address);
  mix(PlumeGuestTextureBaseAddress(source));
  mix(FableDirectTextureFetchKey(PlumeGuestTextureFetchDwords(source)));
  mix(PlumeGuestTextureWidth(source));
  mix(PlumeGuestTextureHeight(source));
  mix(static_cast<uint32_t>(PlumeGuestTextureFormat(source)));
  mix(target.guest_address);
  mix(target.parent_address);
  mix(target.content_serial);
  mix(target.draw_content_serial);
  mix(PlumeGuestTextureWidth(target));
  mix(PlumeGuestTextureHeight(target));
  mix(static_cast<uint32_t>(PlumeGuestTextureFormat(target)));
  return key;
}

uint64_t DirectPrivateFeedbackAliasTargetIdentityKey(
    uint32_t source_guest, const PlumeTrackedResource& source,
    const PlumeTrackedResource& target) {
  uint64_t key = 1469598103934665603ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };

  mix(source_guest);
  mix(source.guest_address);
  mix(source.parent_address);
  mix(PlumeGuestTextureBaseAddress(source));
  mix(FableDirectTextureFetchKey(PlumeGuestTextureFetchDwords(source)));
  mix(target.guest_address);
  mix(target.parent_address);
  mix(PlumeGuestTextureBaseAddress(target));
  mix(FableDirectTextureFetchKey(PlumeGuestTextureFetchDwords(target)));
  return key;
}

uint64_t DirectMaterializedPrivateRectActionFrameKey(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    FablePrivateTextureCopyKind kind) {
  uint64_t key = 1469598103934665603ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };
  auto mix_float = [&](float value) {
    uint32_t bits = 0;
    std::memcpy(&bits, &value, sizeof(bits));
    mix(bits);
  };

  mix(g_direct_renderer.current_frame_index);
  mix(command.draw_serial != 0 ? command.draw_serial : command.serial);
  mix(slot);
  mix(slot < command.resources.textures.size() ? command.resources.textures[slot]
                                               : 0u);
  mix(source.guest_address);
  mix(source.parent_address);
  mix(PlumeGuestTextureBaseAddress(source));
  mix(FableDirectTextureFetchKey(DirectCommandTextureFetchDwords(command, slot)));
  mix(FableDirectTextureFetchKey(PlumeGuestTextureFetchDwords(source)));
  mix(PlumeGuestTextureWidth(source));
  mix(PlumeGuestTextureHeight(source));
  mix(PlumeGuestTextureGuestFormat(source));
  mix(target.guest_address);
  mix(target.parent_address);
  mix(PlumeGuestTextureWidth(target));
  mix(PlumeGuestTextureHeight(target));
  mix(PlumeGuestTextureGuestFormat(target));
  mix(command.resources.render_targets[0]);
  mix(command.resources.depth_stencil);
  mix(command.resources.vertex_shader);
  mix(command.resources.pixel_shader);
  mix(command.draw.state_flush_lr);
  mix(static_cast<uint32_t>(kind));
  mix(command.draw.private_texture_rectangle ? 1u : 0u);
  mix(command.draw.private_texture_corners_valid ? 1u : 0u);
  if (command.draw.private_texture_corners_valid) {
    const auto& corners = command.draw.private_texture_corners;
    for (float corner : corners) {
      mix_float(corner);
    }
  }
  mix_float(command.draw.private_texture_source_x);
  mix_float(command.draw.private_texture_source_y);
  mix(command.draw.private_texture_uv_override ? 1u : 0u);
  mix_float(command.draw.private_texture_u0);
  mix_float(command.draw.private_texture_v0);
  mix_float(command.draw.private_texture_u1);
  mix_float(command.draw.private_texture_v1);
  mix(command.draw.private_texture_wrapper_source_texture);
  mix(command.draw.private_texture_wrapper_source_exact ? 1u : 0u);
  return key;
}

uint64_t DirectStablePrivateCopyActionKey(
    const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    bool include_wrapper_sample_identity, uint64_t domain) {
  uint64_t key = 1469598103934665603ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };
  auto mix_float = [&](float value) {
    uint32_t bits = 0;
    std::memcpy(&bits, &value, sizeof(bits));
    mix(bits);
  };

  mix(domain);
  mix(action.source_slot);
  mix(action.source_texture);
  mix(action.source_base_address);
  mix(FableDirectTextureFetchKey(action.source_fetch_dwords));
  mix(action.source_width);
  mix(action.source_height);
  mix(action.source_format);
  mix(source.guest_address);
  mix(source.parent_address);
  mix(FableTrackedResourceVisibleContentSerial(source));
  mix(target.guest_address);
  mix(target.parent_address);
  mix(action.target_surface);
  mix(action.render_target);
  mix(action.depth_stencil);
  mix(action.target_width);
  mix(action.target_height);
  mix(action.target_format);
  mix(action.target_content_serial);
  mix(action.vertex_shader);
  mix(action.pixel_shader);
  mix(action.vertex_shader_root_symbol_address);
  mix(action.vertex_shader_root_lr);
  mix(action.pixel_shader_root_symbol_address);
  mix(action.pixel_shader_root_lr);
  mix(static_cast<uint32_t>(action.kind));

  if (!include_wrapper_sample_identity) {
    return key;
  }

  mix(action.lr);
  mix(action.wrapper_context_key);
  mix(action.wrapper_symbol_address);
  mix(action.wrapper_source_texture);
  mix(action.wrapper_source_exact ? 1u : 0u);
  for (uint32_t setup_global : action.wrapper_setup_globals) {
    mix(setup_global);
  }
  mix(action.rectangle_valid ? 1u : 0u);
  mix_float(action.rect_width);
  mix_float(action.rect_height);
  mix_float(action.source_x);
  mix_float(action.source_y);
  mix(action.uv_override ? 1u : 0u);
  mix_float(action.u0);
  mix_float(action.v0);
  mix_float(action.u1);
  mix_float(action.v1);
  mix(static_cast<uint32_t>(action.sampled_source.kind));
  mix(action.sampled_source.resource);
  mix(action.sampled_source.base_address);
  mix(FableDirectTextureFetchKey(action.sampled_source.fetch_dwords));
  mix(action.sampled_source.content_serial);
  return key;
}

bool RememberDirectStablePrivateCopyActionKeyLocked(uint64_t stable_key) {
  if (g_direct_renderer.stable_private_copy_action_keys.size() >= 16384u) {
    g_direct_renderer.stable_private_copy_action_keys.clear();
  }
  return g_direct_renderer.stable_private_copy_action_keys.insert(stable_key)
      .second;
}

uint64_t DirectPrivateCopyActionContentEdgeKey(
    const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target) {
  uint64_t key = 1469598103934665603ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };
  mix(0xC0117E6Du);
  mix(action.source_slot);
  mix(action.source_texture);
  mix(action.source_base_address);
  mix(FableDirectTextureFetchKey(action.source_fetch_dwords));
  mix(action.source_width);
  mix(action.source_height);
  mix(action.source_format);
  mix(source.guest_address);
  mix(source.parent_address);
  mix(PlumeGuestTextureBaseAddress(source));
  mix(FableTrackedResourceVisibleContentSerial(source));
  mix(action.backing_texture);
  mix(action.backing_base_address);
  mix(FableDirectTextureFetchKey(action.backing_fetch_dwords));
  mix(action.backing_content_serial);
  mix(action.target_surface);
  mix(target.guest_address);
  mix(target.parent_address);
  mix(PlumeGuestTextureBaseAddress(target));
  mix(action.target_width);
  mix(action.target_height);
  mix(action.target_format);
  mix(action.target_content_serial);
  mix(action.render_target);
  mix(action.depth_stencil);
  mix(action.vertex_shader);
  mix(action.pixel_shader);
  mix(action.vertex_shader_root_symbol_address);
  mix(action.vertex_shader_root_lr);
  mix(action.pixel_shader_root_symbol_address);
  mix(action.pixel_shader_root_lr);
  mix(action.lr);
  mix(action.wrapper_symbol_address);
  mix(action.wrapper_source_texture);
  mix(action.wrapper_source_exact ? 1u : 0u);
  for (uint32_t setup_global : action.wrapper_setup_globals) {
    mix(setup_global);
  }
  mix(static_cast<uint32_t>(action.sampled_source.kind));
  mix(action.sampled_source.resource);
  mix(action.sampled_source.base_address);
  mix(FableDirectTextureFetchKey(action.sampled_source.fetch_dwords));
  mix(action.sampled_source.content_serial);
  mix(static_cast<uint32_t>(action.kind));
  mix(action.rectangle_valid ? 1u : 0u);
  mix(action.uv_override ? 1u : 0u);
  return key != 0 ? key : 1ull;
}

uint64_t DirectPrivateRectExactActionLookupKey(
    uint32_t slot, uint32_t source_texture, uint32_t source_base_address,
    const std::array<uint32_t, 6>& source_fetch_dwords, uint32_t source_width,
    uint32_t source_height, uint32_t source_format, uint32_t target_surface,
    uint32_t render_target, uint32_t depth_stencil, uint32_t vertex_shader,
    uint32_t pixel_shader, FablePrivateTextureCopyKind kind,
    uint64_t vertex_shader_root_call_serial,
    uint64_t vertex_shader_root_context_key,
    uint32_t vertex_shader_root_symbol_address,
    uint32_t vertex_shader_root_lr,
    uint64_t pixel_shader_root_call_serial,
    uint64_t pixel_shader_root_context_key,
    uint32_t pixel_shader_root_symbol_address,
    uint32_t pixel_shader_root_lr) {
  const uint32_t source_identity =
      source_base_address != 0 ? source_base_address : source_texture;
  if (source_identity == 0 || target_surface == 0) {
    return 0;
  }
  uint64_t key = 1469598103934665603ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };
  mix(slot);
  mix(source_identity);
  mix(source_width);
  mix(source_height);
  mix(source_format);
  mix(FableDirectTextureFetchKey(source_fetch_dwords));
  mix(target_surface);
  mix(render_target);
  mix(depth_stencil);
  mix(vertex_shader);
  mix(pixel_shader);
  MixDirectShaderRoots(mix, vertex_shader_root_call_serial,
                       vertex_shader_root_context_key,
                       vertex_shader_root_symbol_address,
                       vertex_shader_root_lr,
                       pixel_shader_root_call_serial,
                       pixel_shader_root_context_key,
                       pixel_shader_root_symbol_address,
                       pixel_shader_root_lr);
  mix(static_cast<uint32_t>(kind));
  return key;
}

uint64_t DirectPrivateRectExactActionLookupKey(
    const FablePrivateTextureCopyAction& action) {
  return DirectPrivateRectExactActionLookupKey(
      action.source_slot, action.source_texture, action.source_base_address,
      action.source_fetch_dwords, action.source_width, action.source_height,
      action.source_format, action.target_surface, action.render_target,
      action.depth_stencil, action.vertex_shader, action.pixel_shader,
      action.kind, action.vertex_shader_root_call_serial,
      action.vertex_shader_root_context_key,
      action.vertex_shader_root_symbol_address, action.vertex_shader_root_lr,
      action.pixel_shader_root_call_serial, action.pixel_shader_root_context_key,
      action.pixel_shader_root_symbol_address, action.pixel_shader_root_lr);
}

uint64_t DirectPrivateRectExactActionLookupKey(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    FablePrivateTextureCopyKind kind) {
  return DirectPrivateRectExactActionLookupKey(
      slot,
      slot < command.resources.textures.size() ? command.resources.textures[slot]
                                               : source.guest_address,
      PlumeGuestTextureBaseAddress(source),
      HasTrackedFetchDwords(DirectCommandTextureFetchDwords(command, slot))
          ? DirectCommandTextureFetchDwords(command, slot)
          : PlumeGuestTextureFetchDwords(source),
	      PlumeGuestTextureWidth(source), PlumeGuestTextureHeight(source),
	      PlumeGuestTextureGuestFormat(source), target.guest_address,
	      command.resources.render_targets[0], command.resources.depth_stencil,
	      command.resources.vertex_shader, command.resources.pixel_shader, kind,
      command.resources.shader_roots.vertex_shader.call_serial,
      command.resources.shader_roots.vertex_shader.context_key,
      command.resources.shader_roots.vertex_shader.symbol_address,
      command.resources.shader_roots.vertex_shader.lr,
      command.resources.shader_roots.pixel_shader.call_serial,
      command.resources.shader_roots.pixel_shader.context_key,
      command.resources.shader_roots.pixel_shader.symbol_address,
      command.resources.shader_roots.pixel_shader.lr);
}

uint32_t DirectPrivateRectExactTargetShapeKey(uint32_t width, uint32_t height,
                                              uint32_t format) {
  if (width == 0 || height == 0) {
    return 0;
  }
  uint64_t key = 1469598103934665603ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };
  mix(width);
  mix(height);
  mix(format);
  return 0xF0000000u | static_cast<uint32_t>(key & 0x0FFFFFFFu);
}

uint32_t DirectPrivateRectExactTargetShapeKey(
    const PlumeTrackedResource& target) {
  return DirectPrivateRectExactTargetShapeKey(
      PlumeGuestTextureWidth(target), PlumeGuestTextureHeight(target),
      PlumeGuestTextureGuestFormat(target));
}

bool DirectPrivateRectExactActionCacheEntryEquivalent(
    const FablePrivateTextureCopyAction& existing,
    const FablePrivateTextureCopyAction& action) {
  return existing.source_slot == action.source_slot &&
         existing.source_texture == action.source_texture &&
         existing.source_base_address == action.source_base_address &&
         existing.source_fetch_dwords == action.source_fetch_dwords &&
         existing.source_width == action.source_width &&
         existing.source_height == action.source_height &&
         existing.source_format == action.source_format &&
         existing.target_surface == action.target_surface &&
         existing.target_width == action.target_width &&
         existing.target_height == action.target_height &&
         existing.target_format == action.target_format &&
         existing.target_content_serial == action.target_content_serial &&
         existing.backing_texture == action.backing_texture &&
         existing.backing_base_address == action.backing_base_address &&
         existing.backing_fetch_dwords == action.backing_fetch_dwords &&
         existing.backing_content_serial == action.backing_content_serial &&
         existing.render_target == action.render_target &&
         existing.depth_stencil == action.depth_stencil &&
         existing.vertex_shader == action.vertex_shader &&
         existing.pixel_shader == action.pixel_shader &&
         existing.vertex_shader_root_symbol_address ==
             action.vertex_shader_root_symbol_address &&
         existing.vertex_shader_root_lr == action.vertex_shader_root_lr &&
         existing.pixel_shader_root_symbol_address ==
             action.pixel_shader_root_symbol_address &&
         existing.pixel_shader_root_lr == action.pixel_shader_root_lr &&
         existing.wrapper_context_key == action.wrapper_context_key &&
         existing.wrapper_symbol_address == action.wrapper_symbol_address &&
         existing.wrapper_source_texture == action.wrapper_source_texture &&
         existing.wrapper_source_exact == action.wrapper_source_exact &&
         existing.wrapper_setup_globals == action.wrapper_setup_globals &&
         existing.sampled_source.kind == action.sampled_source.kind &&
         existing.sampled_source.resource == action.sampled_source.resource &&
         existing.sampled_source.base_address ==
             action.sampled_source.base_address &&
         existing.sampled_source.fetch_dwords ==
             action.sampled_source.fetch_dwords &&
         existing.sampled_source.content_serial ==
             action.sampled_source.content_serial &&
         existing.kind == action.kind;
}

void StoreDirectPrivateRectExactActionLocked(
    uint64_t key, const FablePrivateTextureCopyAction& action) {
  if (key == 0) {
    return;
  }
  constexpr size_t kMaxExactPrivateRectActions = 8192;
  if (g_direct_renderer.materialized_private_rect_actions_by_exact_key.size() >=
      kMaxExactPrivateRectActions) {
    g_direct_renderer.materialized_private_rect_actions_by_exact_key.clear();
  }
  const auto existing =
      g_direct_renderer.materialized_private_rect_actions_by_exact_key.find(key);
  if (existing !=
          g_direct_renderer.materialized_private_rect_actions_by_exact_key.end() &&
      DirectPrivateRectExactActionCacheEntryEquivalent(existing->second,
                                                       action)) {
    return;
  }
  g_direct_renderer.materialized_private_rect_actions_by_exact_key[key] = action;
}

void RememberDirectPrivateRectExactActionEndpointLocked(
    const FablePrivateTextureCopyAction& action, uint32_t source_texture,
    uint32_t source_base_address,
    const std::array<uint32_t, 6>& source_fetch_dwords,
    uint32_t source_width, uint32_t source_height, uint32_t source_format,
    uint32_t target_surface, bool allow_render_target_wildcard) {
  if (target_surface == 0) {
    return;
  }

  const auto remember_key =
      [&](uint32_t texture, uint32_t base_address) {
    const uint32_t normalized_pixel_shader =
        DirectPrivateRectExactPixelShaderKey(action.pixel_shader, action.kind);
    auto store_for_render_target = [&](uint32_t render_target,
                                       uint32_t pixel_shader) {
      StoreDirectPrivateRectExactActionLocked(
          DirectPrivateRectExactActionLookupKey(
              action.source_slot, texture, base_address, source_fetch_dwords,
              source_width, source_height, source_format, target_surface,
              render_target, action.depth_stencil, action.vertex_shader,
              pixel_shader, action.kind),
          action);
    };
    store_for_render_target(action.render_target, action.pixel_shader);
    if (normalized_pixel_shader != action.pixel_shader) {
      store_for_render_target(action.render_target, normalized_pixel_shader);
    }
    if (allow_render_target_wildcard && action.render_target != 0) {
      store_for_render_target(0, action.pixel_shader);
      if (normalized_pixel_shader != action.pixel_shader) {
        store_for_render_target(0, normalized_pixel_shader);
      }
    }
  };

  remember_key(source_texture, source_base_address);
  remember_key(source_texture, 0);
  remember_key(0, source_base_address);

  auto remember_identity = [&](const FableTextureIdentity* identity) {
    if (!identity) {
      return;
    }
    const uint32_t width = source_width != 0 ? source_width : identity->width;
    const uint32_t height = source_height != 0 ? source_height : identity->height;
    const uint32_t format = source_format != 0 ? source_format : identity->format;
    const std::array<uint32_t, 6>& fetch =
        HasTrackedFetchDwords(source_fetch_dwords) ? source_fetch_dwords
                                                   : identity->fetch_dwords;
    StoreDirectPrivateRectExactActionLocked(
        DirectPrivateRectExactActionLookupKey(
            action.source_slot, identity->object_texture, 0, fetch, width,
            height, format, target_surface, action.render_target,
            action.depth_stencil, action.vertex_shader, action.pixel_shader,
            action.kind),
        action);
    if (allow_render_target_wildcard && action.render_target != 0) {
      StoreDirectPrivateRectExactActionLocked(
          DirectPrivateRectExactActionLookupKey(
              action.source_slot, identity->object_texture, 0, fetch, width,
              height, format, target_surface, 0, action.depth_stencil,
              action.vertex_shader, action.pixel_shader, action.kind),
          action);
    }
    const uint32_t normalized_pixel_shader =
        DirectPrivateRectExactPixelShaderKey(action.pixel_shader, action.kind);
    if (normalized_pixel_shader != action.pixel_shader) {
      StoreDirectPrivateRectExactActionLocked(
          DirectPrivateRectExactActionLookupKey(
              action.source_slot, identity->object_texture, 0, fetch, width,
              height, format, target_surface, action.render_target,
              action.depth_stencil, action.vertex_shader,
              normalized_pixel_shader, action.kind),
          action);
      if (allow_render_target_wildcard && action.render_target != 0) {
        StoreDirectPrivateRectExactActionLocked(
            DirectPrivateRectExactActionLookupKey(
                action.source_slot, identity->object_texture, 0, fetch, width,
                height, format, target_surface, 0, action.depth_stencil,
                action.vertex_shader, normalized_pixel_shader, action.kind),
            action);
      }
    }
    if (identity->parent_texture != 0) {
      StoreDirectPrivateRectExactActionLocked(
          DirectPrivateRectExactActionLookupKey(
              action.source_slot, identity->parent_texture, 0, fetch, width,
              height, format, target_surface, action.render_target,
              action.depth_stencil, action.vertex_shader, action.pixel_shader,
              action.kind),
          action);
      if (allow_render_target_wildcard && action.render_target != 0) {
        StoreDirectPrivateRectExactActionLocked(
            DirectPrivateRectExactActionLookupKey(
                action.source_slot, identity->parent_texture, 0, fetch, width,
                height, format, target_surface, 0, action.depth_stencil,
                action.vertex_shader, action.pixel_shader, action.kind),
            action);
      }
      if (normalized_pixel_shader != action.pixel_shader) {
        StoreDirectPrivateRectExactActionLocked(
            DirectPrivateRectExactActionLookupKey(
                action.source_slot, identity->parent_texture, 0, fetch, width,
                height, format, target_surface, action.render_target,
                action.depth_stencil, action.vertex_shader,
                normalized_pixel_shader, action.kind),
            action);
        if (allow_render_target_wildcard && action.render_target != 0) {
          StoreDirectPrivateRectExactActionLocked(
              DirectPrivateRectExactActionLookupKey(
                  action.source_slot, identity->parent_texture, 0, fetch, width,
                  height, format, target_surface, 0, action.depth_stencil,
                  action.vertex_shader, normalized_pixel_shader, action.kind),
              action);
        }
      }
    }
    if (identity->base_address != 0) {
      StoreDirectPrivateRectExactActionLocked(
          DirectPrivateRectExactActionLookupKey(
              action.source_slot, 0, identity->base_address, fetch, width,
              height, format, target_surface, action.render_target,
              action.depth_stencil, action.vertex_shader, action.pixel_shader,
              action.kind),
          action);
      if (allow_render_target_wildcard && action.render_target != 0) {
        StoreDirectPrivateRectExactActionLocked(
            DirectPrivateRectExactActionLookupKey(
                action.source_slot, 0, identity->base_address, fetch, width,
                height, format, target_surface, 0, action.depth_stencil,
                action.vertex_shader, action.pixel_shader, action.kind),
            action);
      }
      if (normalized_pixel_shader != action.pixel_shader) {
        StoreDirectPrivateRectExactActionLocked(
            DirectPrivateRectExactActionLookupKey(
                action.source_slot, 0, identity->base_address, fetch, width,
                height, format, target_surface, action.render_target,
                action.depth_stencil, action.vertex_shader,
                normalized_pixel_shader, action.kind),
            action);
        if (allow_render_target_wildcard && action.render_target != 0) {
          StoreDirectPrivateRectExactActionLocked(
              DirectPrivateRectExactActionLookupKey(
                  action.source_slot, 0, identity->base_address, fetch, width,
                  height, format, target_surface, 0, action.depth_stencil,
                  action.vertex_shader, normalized_pixel_shader, action.kind),
              action);
        }
      }
    }
  };

  remember_identity(FableDirectFindTextureIdentityByObject(source_texture));
  remember_identity(FableDirectFindTextureIdentityByBase(source_base_address));
  const std::vector<uint32_t>* fetch_objects =
      FableDirectFindTextureIdentityObjectsByFetch(source_fetch_dwords);
  if (fetch_objects) {
    constexpr size_t kMaxExactPrivateRectFetchIdentityKeys = 16;
    size_t remembered = 0;
    for (uint32_t object : *fetch_objects) {
      remember_identity(FableDirectFindTextureIdentityByObject(object));
      if (++remembered >= kMaxExactPrivateRectFetchIdentityKeys) {
        break;
      }
    }
  }
}

void RememberDirectPrivateRectExactActionLocked(
    const FablePrivateTextureCopyAction& action) {
  if (action.target_surface == 0) {
    return;
  }

  std::array<std::pair<uint32_t, bool>, 4> target_surfaces = {};
  uint32_t target_count = 0;
  auto add_target = [&](uint32_t target, bool allow_render_target_wildcard) {
    if (target == 0) {
      return;
    }
    const auto existing = std::find_if(
        target_surfaces.begin(), target_surfaces.begin() + target_count,
        [&](const auto& entry) { return entry.first == target; });
    if (existing != target_surfaces.begin() + target_count) {
      existing->second = existing->second || allow_render_target_wildcard;
      return;
    }
    if (target_count < target_surfaces.size()) {
      target_surfaces[target_count++] =
          std::make_pair(target, allow_render_target_wildcard);
    }
  };
  target_count = 0;
  add_target(action.target_surface, false);
  add_target(action.render_target, false);
  if (const PlumeTrackedResource* target =
          FindDirectResourceLocked(action.target_surface)) {
    add_target(DirectPrivateRectExactTargetShapeKey(*target), true);
  }
  if (const PlumeTrackedResource* target =
          FindDirectResourceLocked(action.render_target)) {
    add_target(DirectPrivateRectExactTargetShapeKey(*target), true);
  }

  for (uint32_t i = 0; i < target_count; ++i) {
    const auto& [target_surface, allow_render_target_wildcard] =
        target_surfaces[i];
    RememberDirectPrivateRectExactActionEndpointLocked(
        action, action.source_texture, action.source_base_address,
        action.source_fetch_dwords, action.source_width, action.source_height,
        action.source_format, target_surface, allow_render_target_wildcard);
    if (action.backing_texture != 0 || action.backing_base_address != 0 ||
        HasTrackedFetchDwords(action.backing_fetch_dwords)) {
      RememberDirectPrivateRectExactActionEndpointLocked(
          action, action.backing_texture, action.backing_base_address,
          action.backing_fetch_dwords,
          action.backing_width != 0 ? action.backing_width : action.source_width,
          action.backing_height != 0 ? action.backing_height
                                     : action.source_height,
          action.backing_format != 0 ? action.backing_format
                                     : action.source_format,
          target_surface, allow_render_target_wildcard);
    }
  }
}

const FablePrivateTextureCopyAction*
FindDirectPrivateRectExactActionLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target) {
  const std::array<FablePrivateTextureCopyKind, 3> candidate_kinds = {
      FablePrivateTextureCopyKind::kColor,
      FablePrivateTextureCopyKind::kColorSibling,
      FablePrivateTextureCopyKind::kDepth,
  };
  std::array<uint32_t, 12> source_textures = {};
  uint32_t source_texture_count = 0;
  auto add_source_texture = [&](uint32_t texture) {
    if (texture == 0) {
      return;
    }
    if (std::find(source_textures.begin(),
                  source_textures.begin() + source_texture_count,
                  texture) != source_textures.begin() + source_texture_count) {
      return;
    }
    if (source_texture_count < source_textures.size()) {
      source_textures[source_texture_count++] = texture;
    }
  };

  const uint32_t command_guest =
      slot < command.resources.textures.size() ? command.resources.textures[slot]
                                               : 0u;
  add_source_texture(command_guest);
  add_source_texture(source.guest_address);
  add_source_texture(source.parent_address);
  auto add_identity_textures = [&](const FableTextureIdentity* identity) {
    if (!identity) {
      return;
    }
    add_source_texture(identity->object_texture);
    add_source_texture(identity->parent_texture);
  };
  add_identity_textures(FableDirectFindTextureIdentityByObject(command_guest));
  add_identity_textures(FableDirectFindTextureIdentityByObject(source.guest_address));
  add_identity_textures(FableDirectFindTextureIdentityByObject(source.parent_address));
  add_identity_textures(FableDirectFindTextureIdentityByBase(
      PlumeGuestTextureBaseAddress(source)));

  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const std::array<uint32_t, 6> source_fetch =
      PlumeGuestTextureFetchDwords(source);
  auto add_fetch_identity_textures =
      [&](const std::array<uint32_t, 6>& fetch_dwords) {
    const std::vector<uint32_t>* objects =
        FableDirectFindTextureIdentityObjectsByFetch(fetch_dwords);
    if (!objects) {
      return;
    }
    for (uint32_t object : *objects) {
      add_source_texture(object);
      add_identity_textures(FableDirectFindTextureIdentityByObject(object));
      if (source_texture_count >= source_textures.size()) {
        break;
      }
    }
  };
  add_fetch_identity_textures(command_fetch);
  add_fetch_identity_textures(source_fetch);

  std::array<uint32_t, 6> source_bases = {};
  uint32_t source_base_count = 0;
  auto add_source_base = [&](uint32_t base_address) {
    if (base_address == 0) {
      return;
    }
    if (std::find(source_bases.begin(),
                  source_bases.begin() + source_base_count,
                  base_address) != source_bases.begin() + source_base_count) {
      return;
    }
    if (source_base_count < source_bases.size()) {
      source_bases[source_base_count++] = base_address;
    }
  };
  add_source_base(PlumeGuestTextureBaseAddress(source));
  if (const FableTextureIdentity* identity =
          FableDirectFindTextureIdentityByObject(command_guest)) {
    add_source_base(identity->base_address);
  }
  if (const FableTextureIdentity* identity =
          FableDirectFindTextureIdentityByObject(source.guest_address)) {
    add_source_base(identity->base_address);
  }
  if (const FableTextureIdentity* identity =
          FableDirectFindTextureIdentityByObject(source.parent_address)) {
    add_source_base(identity->base_address);
  }

  std::array<std::array<uint32_t, 6>, 2> fetch_variants = {
      command_fetch,
      source_fetch,
  };

  std::array<uint32_t, 2> pixel_shader_variants = {
      command.resources.pixel_shader,
      DirectPrivateRectExactPixelShaderKey(command.resources.pixel_shader,
                                           FablePrivateTextureCopyKind::kColor),
  };

  std::array<uint32_t, 2> render_target_variants = {
      command.resources.render_targets[0],
      0u,
  };

  std::array<uint32_t, 4> target_surfaces = {};
  uint32_t target_surface_count = 0;
  auto add_target_surface = [&](uint32_t target_surface) {
    if (target_surface == 0) {
      return;
    }
    if (std::find(target_surfaces.begin(),
                  target_surfaces.begin() + target_surface_count,
                  target_surface) != target_surfaces.begin() + target_surface_count) {
      return;
    }
    if (target_surface_count < target_surfaces.size()) {
      target_surfaces[target_surface_count++] = target_surface;
    }
  };
  add_target_surface(target.guest_address);
  add_target_surface(target.parent_address);
  add_target_surface(command.resources.render_targets[0]);
  add_target_surface(DirectPrivateRectExactTargetShapeKey(target));

  auto find_key = [&](uint64_t key) -> const FablePrivateTextureCopyAction* {
    if (key == 0) {
      return nullptr;
    }
    const auto it =
        g_direct_renderer.materialized_private_rect_actions_by_exact_key.find(
            key);
    if (it ==
        g_direct_renderer.materialized_private_rect_actions_by_exact_key.end()) {
      return nullptr;
    }
    const FablePrivateTextureCopyAction& action = it->second;
    const uint64_t command_draw_serial =
        command.draw_serial != 0 ? command.draw_serial : command.serial;
    const bool same_registered_draw =
        action.draw_serial != 0 && action.draw_serial == command_draw_serial;
    if (same_registered_draw ||
        DirectPrivateTextureCopyActionContextMatches(command, action)) {
      return &action;
    }
    return nullptr;
  };

  for (FablePrivateTextureCopyKind kind : candidate_kinds) {
    pixel_shader_variants[1] =
        DirectPrivateRectExactPixelShaderKey(command.resources.pixel_shader,
                                             kind);
    auto find_key_for_command_roots =
        [&](uint32_t source_texture, uint32_t source_base,
            const std::array<uint32_t, 6>& fetch_dwords,
            uint32_t target_surface, uint32_t render_target,
            uint32_t pixel_shader) -> const FablePrivateTextureCopyAction* {
      if (const FablePrivateTextureCopyAction* action = find_key(
              DirectPrivateRectExactActionLookupKey(
                  slot, source_texture, source_base, fetch_dwords,
                  PlumeGuestTextureWidth(source), PlumeGuestTextureHeight(source),
                  PlumeGuestTextureGuestFormat(source), target_surface,
                  render_target, command.resources.depth_stencil,
                  command.resources.vertex_shader, pixel_shader, kind,
                  command.resources.shader_roots.vertex_shader.call_serial,
                  command.resources.shader_roots.vertex_shader.context_key,
                  command.resources.shader_roots.vertex_shader.symbol_address,
                  command.resources.shader_roots.vertex_shader.lr,
                  command.resources.shader_roots.pixel_shader.call_serial,
                  command.resources.shader_roots.pixel_shader.context_key,
                  command.resources.shader_roots.pixel_shader.symbol_address,
                  command.resources.shader_roots.pixel_shader.lr))) {
        return action;
      }
      return find_key(DirectPrivateRectExactActionLookupKey(
          slot, source_texture, source_base, fetch_dwords,
          PlumeGuestTextureWidth(source), PlumeGuestTextureHeight(source),
          PlumeGuestTextureGuestFormat(source), target_surface, render_target,
          command.resources.depth_stencil, command.resources.vertex_shader,
          pixel_shader, kind));
    };
    for (uint32_t target_surface :
         std::span<const uint32_t>(target_surfaces.data(), target_surface_count)) {
      for (uint32_t source_texture :
           std::span<const uint32_t>(source_textures.data(), source_texture_count)) {
        for (const auto& fetch_dwords : fetch_variants) {
          for (uint32_t render_target : render_target_variants) {
            for (uint32_t pixel_shader : pixel_shader_variants) {
              if (const FablePrivateTextureCopyAction* action =
                      find_key_for_command_roots(source_texture, 0, fetch_dwords,
                                                 target_surface, render_target,
                                                 pixel_shader)) {
                return action;
              }
              if (pixel_shader == pixel_shader_variants[0]) {
                break;
              }
            }
            if (render_target == 0) {
              break;
            }
          }
        }
      }
      for (uint32_t source_base :
           std::span<const uint32_t>(source_bases.data(), source_base_count)) {
        for (const auto& fetch_dwords : fetch_variants) {
          for (uint32_t render_target : render_target_variants) {
            for (uint32_t pixel_shader : pixel_shader_variants) {
              if (const FablePrivateTextureCopyAction* action =
                      find_key_for_command_roots(0, source_base, fetch_dwords,
                                                 target_surface, render_target,
                                                 pixel_shader)) {
                return action;
              }
              if (pixel_shader == pixel_shader_variants[0]) {
                break;
              }
            }
            if (render_target == 0) {
              break;
            }
          }
        }
      }
    }
  }
  return nullptr;
}

void RememberDirectPrivateFeedbackAliasKeyLocked(uint64_t key,
                                                uint32_t alias_address) {
  if (key == 0 || alias_address == 0) {
    return;
  }
  bool changed = false;
  for (auto it = g_direct_renderer.private_texture_feedback_aliases.begin();
       it != g_direct_renderer.private_texture_feedback_aliases.end();) {
    if (it->second == alias_address && it->first != key) {
      changed = true;
      it = g_direct_renderer.private_texture_feedback_aliases.erase(it);
    } else {
      ++it;
    }
  }
  const auto existing_it =
      g_direct_renderer.private_texture_feedback_aliases.find(key);
  changed =
      changed ||
      existing_it == g_direct_renderer.private_texture_feedback_aliases.end() ||
      existing_it->second != alias_address;
  g_direct_renderer.private_texture_feedback_aliases[key] = alias_address;
  if (changed) {
    MarkDirectPrivateTextureAliasStateChangedLocked();
  }
}

// Equivalence-checked store for the target-identity feedback index. The two
// former direct map assignments bypassed the alias-state generation, leaving
// this index invisible to every private_texture_alias_serial-guarded cache
// (including the feedback-ensure memo) — bump ONLY on real value change,
// mirroring RememberDirectPrivateFeedbackAliasKeyLocked above.
void RememberDirectPrivateFeedbackAliasTargetIdentityKeyLocked(
    uint64_t key, uint32_t alias_address) {
  if (key == 0 || alias_address == 0) {
    return;
  }
  auto& index =
      g_direct_renderer.private_texture_feedback_aliases_by_target_identity;
  const auto existing_it = index.find(key);
  const bool changed =
      existing_it == index.end() || existing_it->second != alias_address;
  index[key] = alias_address;
  if (changed) {
    MarkDirectPrivateTextureAliasStateChangedLocked();
  }
}

PlumeTrackedResource* ResolveDirectRegisteredFeedbackSurfaceLinkSampleLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target) {
  if (slot >= command.resources.textures.size()) {
    return nullptr;
  }

  const uint32_t source_guest = command.resources.textures[slot];
  if (source_guest == 0 || target.guest_address == 0 ||
      FableTrackedResourceVisibleContentSerial(target) == 0) {
    return nullptr;
  }

  const uint64_t draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  const uint32_t frame_index = g_direct_renderer.current_frame_index;
  const FableSurfaceTextureLink* candidates[] = {
      FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
          source_guest, frame_index, draw_serial, slot),
      FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(source_guest,
                                                                slot),
      FableDirectFindSurfaceTextureLinkForDestinationTexture(source_guest),
      FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
          source.guest_address, frame_index, draw_serial, slot),
      FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
          source.guest_address, slot),
      FableDirectFindSurfaceTextureLinkForDestinationTexture(
          source.guest_address),
      FableDirectFindVisibleSurfaceTextureLinkForSourceSurface(
          target.guest_address, frame_index, draw_serial, slot),
      FableDirectFindSurfaceTextureLinkForSourceSurfaceSlot(target.guest_address,
                                                           slot),
      FableDirectFindSurfaceTextureLinkForSourceSurface(target.guest_address),
  };

  const uint64_t target_visible_serial =
      FableTrackedResourceVisibleContentSerial(target);
  auto link_matches_source = [&](const FableSurfaceTextureLink& link) {
    if (link.texture == source_guest || link.texture == source.guest_address ||
        link.texture == source.parent_address) {
      return true;
    }
    const uint32_t source_base = PlumeGuestTextureBaseAddress(source);
    return source_base != 0 && link.texture_base_address == source_base;
  };
  auto link_matches_target = [&](const FableSurfaceTextureLink& link) {
    return DirectResourceAddressMatchesLocked(link.source_surface,
                                             target.guest_address) ||
           DirectResourceAddressMatchesLocked(link.source_surface,
                                             target.parent_address) ||
           DirectResourceAddressMatchesLocked(link.render_target,
                                             target.guest_address) ||
           DirectResourceAddressMatchesLocked(link.render_target,
                                             command.resources.render_targets[0]);
  };

  for (const FableSurfaceTextureLink* link : candidates) {
    if (!link ||
        (link->source_slot != 0xFFFFFFFFu && link->source_slot != slot) ||
        !link_matches_source(*link) || !link_matches_target(*link)) {
      continue;
    }
    if (link->target_content_serial != 0 &&
        link->target_content_serial != target_visible_serial) {
      continue;
    }

    PlumeTrackedResource* sample =
        ResolveDirectSurfaceTextureLinkResourceLocked(command, *link);
    if (!sample && link->backing_texture != 0) {
      sample = FindDirectResourceLocked(link->backing_texture);
    }
    if (!sample && link->backing_base_address != 0) {
      sample = FindDirectResourceLocked(link->backing_base_address);
    }
    if (!sample || !IsImageResource(sample->kind) ||
        !PlumeGuestImageReady(*sample) ||
        !FableTrackedResourceHasSampleableContent(*sample) ||
        FableTrackedResourceIsBlankUploadedSampleProxy(*sample) ||
        DirectCommandBindsResourceAsTargetLocked(command, *sample) ||
        !DirectPrivateFeedbackAliasCanSampleTargetLocked(*sample, source,
                                                         target)) {
      continue;
    }
    if (!DirectPrivateFeedbackAliasContainsTargetSerialLocked(
            *sample, target_visible_serial) &&
        !DirectPrivateFeedbackAliasContainsTargetContentLocked(*sample,
                                                              target)) {
      continue;
    }

    const uint64_t exact_key =
        DirectPrivateFeedbackAliasKey(source_guest, source, target);
    const uint64_t target_identity_key =
        DirectPrivateFeedbackAliasTargetIdentityKey(source_guest, source, target);
    RememberDirectPrivateFeedbackAliasKeyLocked(exact_key,
                                                sample->guest_address);
    RememberDirectPrivateFeedbackAliasTargetIdentityKeyLocked(
        target_identity_key, sample->guest_address);
    RememberDirectPrivateTextureSiblingAliasAddressLocked(source_guest,
                                                         *sample);
    RememberDirectPrivateTextureSiblingAliasLocked(source, *sample);
    RememberDirectPrivateTextureAliasIndexLocked(
        source_guest,
        link->texture_base_address != 0 ? link->texture_base_address
                                        : PlumeGuestTextureBaseAddress(source),
        HasTrackedFetchDwords(link->texture_fetch_dwords)
            ? link->texture_fetch_dwords
            : PlumeGuestTextureFetchDwords(source),
        *sample);
    return sample;
  }

  return nullptr;
}

bool DirectPrivateFeedbackBackingActionAlreadyRegisteredLocked(
    const FablePrivateTextureCopyAction& backed_action) {
  if (backed_action.source_texture == 0 ||
      backed_action.target_surface == 0 ||
      backed_action.backing_texture == 0) {
    return false;
  }

  auto same_registered_backing =
      [&](const FablePrivateTextureCopyAction& existing) {
        auto fetch_keys_match =
            [](const std::array<uint32_t, 6>& lhs,
               const std::array<uint32_t, 6>& rhs) {
              return FableDirectTextureFetchKey(lhs) ==
                     FableDirectTextureFetchKey(rhs);
            };
        if (existing.source_slot != backed_action.source_slot ||
            existing.source_texture != backed_action.source_texture ||
            existing.source_base_address != backed_action.source_base_address ||
            existing.source_width != backed_action.source_width ||
            existing.source_height != backed_action.source_height ||
            existing.source_format != backed_action.source_format ||
            !fetch_keys_match(existing.source_fetch_dwords,
                              backed_action.source_fetch_dwords) ||
            existing.backing_texture != backed_action.backing_texture ||
            existing.backing_base_address !=
                backed_action.backing_base_address ||
            existing.backing_width != backed_action.backing_width ||
            existing.backing_height != backed_action.backing_height ||
            existing.backing_format != backed_action.backing_format ||
            (backed_action.backing_content_serial != 0 &&
             existing.backing_content_serial <
                 backed_action.backing_content_serial) ||
            (backed_action.target_content_serial != 0 &&
             existing.target_content_serial !=
                 backed_action.target_content_serial) ||
            !fetch_keys_match(existing.backing_fetch_dwords,
                              backed_action.backing_fetch_dwords) ||
            existing.target_surface != backed_action.target_surface ||
            existing.render_target != backed_action.render_target ||
            existing.depth_stencil != backed_action.depth_stencil ||
            existing.lr != backed_action.lr ||
            existing.kind != backed_action.kind ||
            existing.rectangle_valid != backed_action.rectangle_valid ||
            existing.uv_override != backed_action.uv_override ||
            existing.rect_width != backed_action.rect_width ||
            existing.rect_height != backed_action.rect_height ||
            existing.source_x != backed_action.source_x ||
            existing.source_y != backed_action.source_y ||
            existing.u0 != backed_action.u0 ||
            existing.v0 != backed_action.v0 ||
            existing.u1 != backed_action.u1 ||
            existing.v1 != backed_action.v1) {
          return false;
        }
        return DirectVertexShaderObjectsEquivalentLocked(
                   existing.vertex_shader, backed_action.vertex_shader) &&
               DirectPixelShaderObjectsEquivalentLocked(
                   existing.pixel_shader, backed_action.pixel_shader);
      };

  auto find_registered =
      [&](const std::vector<FablePrivateTextureCopyAction>* actions) {
        if (!actions) {
          return false;
        }
        return std::any_of(actions->begin(), actions->end(),
                           same_registered_backing);
      };

  if (find_registered(FableDirectFindPrivateTextureCopyActionsBySource(
          backed_action.source_texture))) {
    return true;
  }
  if (find_registered(FableDirectFindPrivateTextureCopyActionsByTarget(
          backed_action.target_surface))) {
    return true;
  }
  if (backed_action.render_target != 0 &&
      find_registered(FableDirectFindPrivateTextureCopyActionsByTarget(
          backed_action.render_target))) {
    return true;
  }
  if (backed_action.source_base_address != 0 &&
      find_registered(FableDirectFindPrivateTextureCopyActionsByBase(
          backed_action.source_base_address))) {
    return true;
  }
  return HasTrackedFetchDwords(backed_action.source_fetch_dwords) &&
         find_registered(FableDirectFindPrivateTextureCopyActionsByFetch(
             backed_action.source_fetch_dwords));
}

void RememberDirectPrivateFeedbackActionBackingLocked(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction* exact_action,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    const PlumeTrackedResource& alias) {
  if (!exact_action || alias.guest_address == 0 ||
      !PlumeGuestImageReady(alias) || !FableTrackedResourceHasSampleableContent(alias) ||
      !DirectResourceIsPersistentPrivateSampleBacking(alias)) {
    return;
  }

  FablePrivateTextureCopyAction backed_action = *exact_action;
  PopulateDirectPrivateTextureCopyBackingLocked(backed_action, alias);
  backed_action.target_content_serial =
      FableTrackedResourceVisibleContentSerial(target);
  backed_action.frame_index = g_direct_renderer.current_frame_index;
  backed_action.draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;

  const bool already_registered =
      DirectPrivateFeedbackBackingActionAlreadyRegisteredLocked(backed_action);
  if (already_registered) {
    return;
  }

  auto remember_alias_index = [&](uint32_t source_guest,
                                  uint32_t source_base_address,
                                  const std::array<uint32_t, 6>& fetch_dwords) {
    if (source_guest == 0 && source_base_address == 0 &&
        !HasTrackedFetchDwords(fetch_dwords)) {
      return;
    }
    RememberDirectPrivateTextureAliasIndexLocked(source_guest,
                                                 source_base_address,
                                                 fetch_dwords, alias);
  };
  const uint32_t action_source =
      backed_action.source_texture != 0 ? backed_action.source_texture
                                        : source.guest_address;
  const uint32_t action_source_base =
      backed_action.source_base_address != 0
          ? backed_action.source_base_address
          : PlumeGuestTextureBaseAddress(source);
  const std::array<uint32_t, 6> action_source_fetch =
      HasTrackedFetchDwords(backed_action.source_fetch_dwords)
          ? backed_action.source_fetch_dwords
          : PlumeGuestTextureFetchDwords(source);
  remember_alias_index(action_source, action_source_base, action_source_fetch);
  remember_alias_index(source.guest_address, action_source_base,
                       action_source_fetch);
  remember_alias_index(source.parent_address, action_source_base,
                       action_source_fetch);

  auto remember_identity_alias = [&](const FableTextureIdentity* identity) {
    if (!identity) {
      return;
    }
    const uint32_t identity_base =
        identity->base_address != 0 ? identity->base_address
                                    : action_source_base;
    const std::array<uint32_t, 6>& identity_fetch =
        HasTrackedFetchDwords(identity->fetch_dwords) ? identity->fetch_dwords
                                                      : action_source_fetch;
    remember_alias_index(identity->object_texture, identity_base,
                         identity_fetch);
    remember_alias_index(identity->parent_texture, identity_base,
                         identity_fetch);
  };
  remember_identity_alias(FableDirectFindTextureIdentityByObject(action_source));
  remember_identity_alias(
      FableDirectFindTextureIdentityByObject(source.guest_address));
  remember_identity_alias(
      FableDirectFindTextureIdentityByObject(source.parent_address));
  remember_identity_alias(
      FableDirectFindTextureIdentityByBase(action_source_base));
  if (const std::vector<uint32_t>* fetch_objects =
          FableDirectFindTextureIdentityObjectsByFetch(action_source_fetch)) {
    constexpr size_t kMaxFeedbackBackingIdentityFanout = 16;
    size_t remembered = 0;
    for (uint32_t object : *fetch_objects) {
      remember_identity_alias(FableDirectFindTextureIdentityByObject(object));
      if (++remembered >= kMaxFeedbackBackingIdentityFanout) {
        break;
      }
    }
  }

  RegisterDirectPrivateTextureCopyActionLocked(backed_action);
  RememberDirectPrivateRectExactActionLocked(backed_action);

  auto fanout_backing_to_matching_command_slots = [&]() {
    if (command.draw.source == RendererDrawEventSource::kPixVideo) {
      return;
    }

    const uint32_t explicit_private_slot =
        command.draw.private_texture_slot & (kMaxFableTextureSlots - 1u);
    const uint64_t action_fetch_key =
        FableDirectTextureFetchKey(action_source_fetch);

    auto slot_is_feedback_candidate = [&](uint32_t slot) {
      return slot == 0u || slot == kFablePrivateRectCopyTextureSlot ||
             slot == explicit_private_slot ||
             slot == backed_action.source_slot;
    };
    auto source_identity_matches = [&](uint32_t slot,
                                       const PlumeTrackedResource& candidate,
                                       const std::array<uint32_t, 6>& fetch,
                                       const DirectTextureFetchInfo& fetch_info) {
      const uint32_t bound_texture = command.resources.textures[slot];
      if (bound_texture == 0) {
        return false;
      }
      if (bound_texture == action_source ||
          candidate.guest_address == action_source ||
          candidate.parent_address == action_source) {
        return true;
      }
      if (action_source_base != 0 &&
          (PlumeGuestTextureBaseAddress(candidate) == action_source_base ||
           (fetch_info.valid && fetch_info.base_address == action_source_base))) {
        return true;
      }
      const uint64_t slot_fetch_key = FableDirectTextureFetchKey(fetch);
      if (action_fetch_key != 0 && slot_fetch_key == action_fetch_key) {
        return true;
      }
      if (const FableTextureIdentity* identity =
              FableDirectFindTextureIdentityByObject(bound_texture)) {
        if (identity->object_texture == action_source ||
            identity->parent_texture == action_source ||
            (action_source_base != 0 &&
             identity->base_address == action_source_base) ||
            (action_fetch_key != 0 &&
             FableDirectTextureFetchKey(identity->fetch_dwords) ==
                 action_fetch_key)) {
          return true;
        }
      }
      return false;
    };

    uint32_t registered_sibling_slots = 0;
    for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
      if (!slot_is_feedback_candidate(slot) ||
          slot == backed_action.source_slot) {
        continue;
      }

      const uint32_t bound_texture = command.resources.textures[slot];
      PlumeTrackedResource* candidate =
          bound_texture != 0 ? FindDirectResourceLocked(bound_texture) : nullptr;
      if (!candidate || !IsImageResource(candidate->kind) ||
          !PlumeGuestImageReady(*candidate) ||
          DirectCommandBindsResourceAsTargetLocked(command, *candidate) ||
          !DirectCommandUsesFablePrivateTexturePath(command, slot, candidate)) {
        continue;
      }

      const std::array<uint32_t, 6> command_fetch =
          DirectCommandTextureFetchDwords(command, slot);
      const std::array<uint32_t, 6> candidate_fetch =
          HasTrackedFetchDwords(command_fetch)
              ? command_fetch
              : PlumeGuestTextureFetchDwords(*candidate);
      const DirectTextureFetchInfo fetch_info =
          DirectTextureFetchInfoFromDwords(candidate_fetch);
      if (!source_identity_matches(slot, *candidate, candidate_fetch,
                                   fetch_info)) {
        continue;
      }

      FablePrivateTextureCopyAction sibling_action = backed_action;
      sibling_action.source_slot = slot;
      sibling_action.source_texture =
          bound_texture != 0 ? bound_texture : candidate->guest_address;
      sibling_action.source_base_address =
          fetch_info.valid ? fetch_info.base_address
                           : PlumeGuestTextureBaseAddress(*candidate);
      sibling_action.source_width =
          fetch_info.valid ? fetch_info.width
                           : PlumeGuestTextureWidth(*candidate);
      sibling_action.source_height =
          fetch_info.valid ? fetch_info.height
                           : PlumeGuestTextureHeight(*candidate);
      sibling_action.source_format =
          fetch_info.valid ? fetch_info.format
                           : PlumeGuestTextureGuestFormat(*candidate);
      sibling_action.source_fetch_dwords = candidate_fetch;
      RegisterDirectPrivateTextureCopyActionLocked(sibling_action);
      RememberDirectPrivateRectExactActionLocked(sibling_action);
      RememberDirectPrivateTextureAliasIndexLocked(
          sibling_action.source_texture, sibling_action.source_base_address,
          sibling_action.source_fetch_dwords, alias);
      RememberDirectPrivateTextureAliasIndexLocked(
          candidate->guest_address, sibling_action.source_base_address,
          sibling_action.source_fetch_dwords, alias);
      if (candidate->parent_address != 0) {
        RememberDirectPrivateTextureAliasIndexLocked(
            candidate->parent_address, sibling_action.source_base_address,
            sibling_action.source_fetch_dwords, alias);
      }
      ++registered_sibling_slots;
    }

    static uint32_t logged_feedback_backing_fanouts = 0;
    if (registered_sibling_slots != 0 &&
        logged_feedback_backing_fanouts < 32u) {
      REXLOG_INFO(
          "Refii plume private rect feedback backing fanout #{} frame={} "
          "source_slot={} sibling_slots={} source=0x{:08X} "
          "target=0x{:08X} alias=0x{:08X} base=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X}",
          ++logged_feedback_backing_fanouts,
          g_direct_renderer.current_frame_index, backed_action.source_slot,
          registered_sibling_slots, backed_action.source_texture,
          target.guest_address, alias.guest_address,
          backed_action.source_base_address, command.resources.vertex_shader,
          command.resources.pixel_shader);
    }
  };
  fanout_backing_to_matching_command_slots();

  if (g_direct_renderer.diagnostics.logged_private_rect_feedback_action_backings < 32u) {
    REXLOG_INFO(
        "Refii plume private rect feedback action backing #{} frame={} "
        "source=0x{:08X}/{}x{} target=0x{:08X}/{}x{} "
        "alias=0x{:08X}/{}x{} action_draw={} base=0x{:08X} "
        "vs=0x{:08X} ps=0x{:08X}",
        ++g_direct_renderer.diagnostics.logged_private_rect_feedback_action_backings,
        g_direct_renderer.current_frame_index, backed_action.source_texture,
        backed_action.source_width, backed_action.source_height,
        target.guest_address, PlumeGuestTextureWidth(target),
        PlumeGuestTextureHeight(target), alias.guest_address,
        PlumeGuestTextureWidth(alias), PlumeGuestTextureHeight(alias),
        static_cast<unsigned long long>(backed_action.draw_serial),
        backed_action.source_base_address, command.resources.vertex_shader,
        command.resources.pixel_shader);
  }
}

void RememberDirectPrivateFeedbackSurfaceLinkLocked(
    const FableDirectRenderCommand& command, uint32_t source_slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    const PlumeTrackedResource& alias) {
  if (source_slot >= command.resources.textures.size() ||
      source.guest_address == 0 || target.guest_address == 0 ||
      alias.guest_address == 0 || !IsImageResource(source.kind) ||
      !IsImageResource(target.kind) || !IsImageResource(alias.kind) ||
      !PlumeGuestImageReady(source) || !PlumeGuestImageReady(target) ||
      !PlumeGuestImageReady(alias) ||
      !FableTrackedResourceHasSampleableContent(alias) ||
      !DirectResourceIsPersistentPrivateSampleBacking(alias) ||
      FableTrackedResourceVisibleContentSerial(target) == 0) {
    return;
  }

  // Unleashed/Marathon keep surface-copy relationships on real guest texture
  // objects. Fable's 0xF100 scratch resources are command-local feedback
  // copies, so keep them out of the authoritative surface-texture graph.
  if (DirectResourceIsPrivateFeedbackAlias(alias)) {
    return;
  }

  const uint32_t bound_texture = command.resources.textures[source_slot];
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, source_slot);
  FableSurfaceTextureLink link;
  link.source_slot = source_slot;
  link.trusted_destination_edge = true;
  link.texture = bound_texture != 0 ? bound_texture : source.guest_address;
  link.texture_base_address = PlumeGuestTextureBaseAddress(source);
  link.texture_width = PlumeGuestTextureWidth(source);
  link.texture_height = PlumeGuestTextureHeight(source);
  link.texture_format = PlumeGuestTextureGuestFormat(source);
  link.texture_fetch_dwords =
      HasTrackedFetchDwords(command_fetch) ? command_fetch
                                           : PlumeGuestTextureFetchDwords(source);
  link.source_surface = target.guest_address;
  link.backing_texture = alias.guest_address;
  link.backing_base_address = PlumeGuestTextureBaseAddress(alias);
  link.backing_fetch_dwords = PlumeGuestTextureFetchDwords(alias);
  link.backing_content_serial = FableTrackedResourceVisibleContentSerial(alias);
  link.target_content_serial = FableTrackedResourceVisibleContentSerial(target);
  PopulateDirectSurfaceTextureLinkBackingLocked(link, alias);
  link.render_target = command.resources.render_targets[0];
  link.depth_stencil = command.resources.depth_stencil;
  link.vertex_shader = command.resources.vertex_shader;
  link.pixel_shader = command.resources.pixel_shader;
  PopulateDirectSurfaceTextureLinkShaderRoots(link, command);
  link.frame_index = g_direct_renderer.current_frame_index;
  link.draw_serial = command.draw_serial != 0 ? command.draw_serial
                                              : command.serial;
  link.kind = target.texture_depth_target ? FablePrivateTextureCopyKind::kDepth
                                          : FablePrivateTextureCopyKind::kColor;
  uint64_t frame_key = 1469598103934665603ull;
  auto mix_frame_key = [&](uint64_t value) {
    frame_key ^= value;
    frame_key *= 1099511628211ull;
  };
  mix_frame_key(link.frame_index);
  mix_frame_key(source_slot);
  mix_frame_key(link.texture);
  mix_frame_key(link.texture_base_address);
  mix_frame_key(FableDirectTextureFetchKey(link.texture_fetch_dwords));
  mix_frame_key(link.source_surface);
  mix_frame_key(link.backing_texture);
  mix_frame_key(link.backing_base_address);
  mix_frame_key(link.target_content_serial);
  mix_frame_key(link.backing_content_serial);
  mix_frame_key(link.render_target);
  mix_frame_key(link.depth_stencil);
  mix_frame_key(link.vertex_shader);
  mix_frame_key(link.pixel_shader);
  mix_frame_key(link.draw_serial);
  if (!g_direct_renderer.remembered_private_feedback_surface_link_frame_keys
           .insert(frame_key)
           .second) {
    return;
  }

  RegisterDirectSurfaceTextureLinkPreparedLocked(link, "feedback-surface");
  if (DirectSurfaceTextureLinkShouldPublishObjectMirror(link)) {
    FableSurfaceTextureLink object_link = link;
    object_link.serial = 0;
    object_link.source_slot = 0xFFFFFFFFu;
    RegisterDirectSurfaceTextureLinkPreparedLocked(object_link,
                                                  "feedback-surface-object");
  }

  static uint32_t logged_feedback_surface_links = 0;
  if (logged_feedback_surface_links < 32u) {
    REXLOG_INFO(
        "Refii plume private feedback surface link #{} frame={} slot={} "
        "texture=0x{:08X}/{}x{} source_surface=0x{:08X}/{}x{} "
        "backing=0x{:08X}/{}x{} target_content={} backing_content={} "
        "vs=0x{:08X} ps=0x{:08X}",
        ++logged_feedback_surface_links, g_direct_renderer.current_frame_index,
        source_slot, link.texture, link.texture_width, link.texture_height,
        target.guest_address, PlumeGuestTextureWidth(target),
        PlumeGuestTextureHeight(target), alias.guest_address,
        PlumeGuestTextureWidth(alias), PlumeGuestTextureHeight(alias),
        static_cast<unsigned long long>(link.target_content_serial),
        static_cast<unsigned long long>(link.backing_content_serial),
        command.resources.vertex_shader, command.resources.pixel_shader);
  }
}

bool DirectPrivateRectExactSurfaceLinkCoveredByExactLinkLocked(
    const FableDirectRenderCommand& command, const FableSurfaceTextureLink& link) {
  if (!link.trusted_destination_edge ||
      !DirectSurfaceTextureLinkHasExactCopyIdentity(link) ||
      link.source_surface == 0 || link.texture == 0) {
    return false;
  }

  const uint64_t draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  const uint64_t link_fetch_key =
      FableDirectTextureFetchKey(link.texture_fetch_dwords);
  auto private_copy_kinds_compatible =
      [](FablePrivateTextureCopyKind lhs, FablePrivateTextureCopyKind rhs) {
        if (lhs == rhs) {
          return true;
        }
        return DirectPrivateTextureCopyKindUsesColorBacking(lhs) &&
               DirectPrivateTextureCopyKindUsesColorBacking(rhs);
      };
  auto nonzero_field_matches = [](uint32_t lhs, uint32_t rhs) {
    return lhs == 0 || rhs == 0 || lhs == rhs;
  };
  auto wrapper_matches = [&](const FableSurfaceTextureLink& candidate) {
    if (link.copy_call_serial != 0 && candidate.copy_call_serial != 0 &&
        link.copy_call_serial != candidate.copy_call_serial) {
      return false;
    }
    if (link.copy_context_key != 0 && candidate.copy_context_key != 0 &&
        link.copy_context_key != candidate.copy_context_key) {
      return false;
    }
    if (link.copy_symbol_address != 0 && candidate.copy_symbol_address != 0 &&
        !FablePrivateRectWrapperSymbolsCompatible(
            link.copy_symbol_address, candidate.copy_symbol_address)) {
      return false;
    }
    if (link.wrapper_source_texture != 0 &&
        candidate.wrapper_source_texture != 0 &&
        !DirectResourceAddressMatchesLocked(link.wrapper_source_texture,
                                           candidate.wrapper_source_texture)) {
      return false;
    }
    return true;
  };
  auto candidate_covers_link = [&](const FableSurfaceTextureLink& candidate) {
    const bool candidate_source_matches =
        DirectResourceAddressMatchesLocked(candidate.source_surface,
                                           link.source_surface) ||
        (candidate.sampled_source.kind ==
             FableRegisteredSampleSourceKind::kGpuSurface &&
         DirectResourceAddressMatchesLocked(candidate.sampled_source.resource,
                                           link.source_surface));
    if (&candidate == &link || !candidate.trusted_destination_edge ||
        !DirectSurfaceTextureLinkHasExactCopyIdentity(candidate) ||
        !private_copy_kinds_compatible(candidate.kind, link.kind) ||
        !wrapper_matches(candidate) || !candidate_source_matches) {
      return false;
    }
    if (candidate.frame_index != 0 &&
        candidate.frame_index > g_direct_renderer.current_frame_index) {
      return false;
    }
    if (candidate.frame_index != 0 &&
        candidate.frame_index == g_direct_renderer.current_frame_index &&
        candidate.draw_serial != 0 && draw_serial != 0 &&
        candidate.draw_serial > draw_serial) {
      return false;
    }
    if (DirectSurfaceTextureLinkIsPrivateTitleWrapperRoot(candidate) &&
        !DirectSurfaceTextureLinkPrivateWrapperVisibleToCommandLocked(
            command, candidate)) {
      return false;
    }
    const bool texture_matches =
        DirectResourceAddressMatchesLocked(candidate.texture, link.texture) ||
        DirectResourceAddressMatchesLocked(candidate.backing_texture,
                                           link.texture);
    const bool base_matches =
        link.texture_base_address != 0 &&
        (candidate.texture_base_address == link.texture_base_address ||
         candidate.backing_base_address == link.texture_base_address);
    const bool fetch_matches =
        link_fetch_key != 0 &&
        (DirectTextureFetchDwordsMatch(candidate.texture_fetch_dwords,
                                       link.texture_fetch_dwords) ||
         DirectTextureFetchDwordsMatch(candidate.backing_fetch_dwords,
                                       link.texture_fetch_dwords));
    if (!texture_matches && !base_matches && !fetch_matches) {
      return false;
    }
    return nonzero_field_matches(candidate.render_target, link.render_target) &&
           nonzero_field_matches(candidate.depth_stencil, link.depth_stencil) &&
           nonzero_field_matches(candidate.vertex_shader, link.vertex_shader) &&
           nonzero_field_matches(candidate.pixel_shader, link.pixel_shader);
  };
  auto consider_links = [&](const std::vector<FableSurfaceTextureLink>* links) {
    if (!links) {
      return false;
    }
    for (auto it = links->rbegin(); it != links->rend(); ++it) {
      if (candidate_covers_link(*it)) {
        return true;
      }
    }
    return false;
  };

  return consider_links(
             FableDirectFindSurfaceTextureLinksForDestinationTexture(
                 link.texture)) ||
         consider_links(
             FableDirectFindSurfaceTextureLinksByBase(
                 link.texture_base_address)) ||
         consider_links(
             FableDirectFindSurfaceTextureLinksByFetch(
                 link.texture_fetch_dwords));
}

bool DirectPrivateRectSetupFlushActionCanOwnExactSurfaceLinkLocked(
    const FablePrivateTextureCopyAction& action,
    const FableSurfaceTextureLink& link) {
  if (link.copy_symbol_address != kFablePrivateRectSetupFlushWrapperSymbol ||
      action.wrapper_symbol_address != kFablePrivateRectSetupFlushWrapperSymbol ||
      !DirectPrivateTextureCopyActionHasExactWrapperIdentity(action) ||
      action.wrapper_call_serial == 0 || action.wrapper_context_key == 0 ||
      link.copy_call_serial == 0 || link.copy_context_key == 0 ||
      action.wrapper_call_serial != link.copy_call_serial ||
      action.wrapper_context_key != link.copy_context_key ||
      action.source_texture == 0 || action.target_surface == 0 ||
      link.texture == 0 || link.source_surface == 0) {
    return false;
  }

  const bool same_kind =
      action.kind == link.kind ||
      (DirectPrivateTextureCopyKindUsesColorBacking(action.kind) &&
       DirectPrivateTextureCopyKindUsesColorBacking(link.kind));
  if (!same_kind) {
    return false;
  }
  const bool same_target =
      DirectResourceAddressMatchesLocked(action.target_surface,
                                         link.source_surface) ||
      DirectResourceAddressMatchesLocked(action.render_target,
                                         link.source_surface);
  if (!same_target) {
    return false;
  }
  const bool same_source =
      DirectResourceAddressMatchesLocked(action.source_texture, link.texture) ||
      (action.source_base_address != 0 &&
       action.source_base_address == link.texture_base_address) ||
      DirectTextureFetchDwordsMatch(action.source_fetch_dwords,
                                    link.texture_fetch_dwords);
  if (!same_source) {
    return false;
  }
  auto nonzero_field_matches = [](uint32_t lhs, uint32_t rhs) {
    return lhs == 0 || rhs == 0 || lhs == rhs;
  };
  return nonzero_field_matches(action.render_target, link.render_target) &&
         nonzero_field_matches(action.depth_stencil, link.depth_stencil) &&
         nonzero_field_matches(action.vertex_shader, link.vertex_shader) &&
         nonzero_field_matches(action.pixel_shader, link.pixel_shader);
}

bool DirectPrivateRectSiblingActionCanOwnExactSurfaceLinkLocked(
    const FablePrivateTextureCopyAction& action,
    const FableSurfaceTextureLink& link) {
  if (link.copy_symbol_address != kFablePrivateRectSiblingWrapperCallsite ||
      action.wrapper_symbol_address != kFablePrivateRectSiblingWrapperCallsite ||
      !DirectPrivateTextureCopyActionHasExactWrapperIdentity(action) ||
      action.wrapper_call_serial == 0 || action.wrapper_context_key == 0 ||
      link.copy_call_serial == 0 || link.copy_context_key == 0 ||
      action.wrapper_call_serial != link.copy_call_serial ||
      action.wrapper_context_key != link.copy_context_key ||
      action.source_texture == 0 || action.target_surface == 0 ||
      link.texture == 0 || link.source_surface == 0) {
    return false;
  }

  const bool same_kind =
      action.kind == link.kind ||
      (DirectPrivateTextureCopyKindUsesColorBacking(action.kind) &&
       DirectPrivateTextureCopyKindUsesColorBacking(link.kind));
  if (!same_kind) {
    return false;
  }
  const bool same_target =
      DirectResourceAddressMatchesLocked(action.target_surface,
                                         link.source_surface) ||
      DirectResourceAddressMatchesLocked(action.render_target,
                                         link.source_surface);
  if (!same_target) {
    return false;
  }
  const bool same_source =
      DirectResourceAddressMatchesLocked(action.source_texture, link.texture) ||
      (action.source_base_address != 0 &&
       action.source_base_address == link.texture_base_address) ||
      DirectTextureFetchDwordsMatch(action.source_fetch_dwords,
                                    link.texture_fetch_dwords);
  if (!same_source) {
    return false;
  }
  auto nonzero_field_matches = [](uint32_t lhs, uint32_t rhs) {
    return lhs == 0 || rhs == 0 || lhs == rhs;
  };
  return nonzero_field_matches(action.render_target, link.render_target) &&
         nonzero_field_matches(action.depth_stencil, link.depth_stencil) &&
         nonzero_field_matches(action.vertex_shader, link.vertex_shader) &&
         nonzero_field_matches(action.pixel_shader, link.pixel_shader);
}

bool DirectPrivateRectCompactActionCanOwnExactSurfaceLinkLocked(
    const FablePrivateTextureCopyAction& action,
    const FableSurfaceTextureLink& link) {
  if (link.copy_symbol_address != kFablePrivateRectCompactWrapperSymbol ||
      action.wrapper_symbol_address != kFablePrivateRectCompactWrapperSymbol ||
      !DirectPrivateTextureCopyActionHasExactWrapperIdentity(action) ||
      action.wrapper_call_serial == 0 || action.wrapper_context_key == 0 ||
      link.copy_call_serial == 0 || link.copy_context_key == 0 ||
      action.wrapper_call_serial != link.copy_call_serial ||
      action.wrapper_context_key != link.copy_context_key ||
      action.source_texture == 0 || action.target_surface == 0 ||
      link.texture == 0 || link.source_surface == 0) {
    return false;
  }

  const bool same_kind =
      action.kind == link.kind ||
      (DirectPrivateTextureCopyKindUsesColorBacking(action.kind) &&
       DirectPrivateTextureCopyKindUsesColorBacking(link.kind));
  if (!same_kind) {
    return false;
  }
  const bool same_target =
      DirectResourceAddressMatchesLocked(action.target_surface,
                                         link.source_surface) ||
      DirectResourceAddressMatchesLocked(action.render_target,
                                         link.source_surface);
  if (!same_target) {
    return false;
  }
  const bool same_source =
      DirectResourceAddressMatchesLocked(action.source_texture, link.texture) ||
      (action.source_base_address != 0 &&
       action.source_base_address == link.texture_base_address) ||
      DirectTextureFetchDwordsMatch(action.source_fetch_dwords,
                                    link.texture_fetch_dwords);
  if (!same_source) {
    return false;
  }
  auto nonzero_field_matches = [](uint32_t lhs, uint32_t rhs) {
    return lhs == 0 || rhs == 0 || lhs == rhs;
  };
  return nonzero_field_matches(action.render_target, link.render_target) &&
         nonzero_field_matches(action.depth_stencil, link.depth_stencil) &&
         nonzero_field_matches(action.vertex_shader, link.vertex_shader) &&
         nonzero_field_matches(action.pixel_shader, link.pixel_shader);
}

bool DirectPrivateRectDrawHelperActionCanOwnExactSurfaceLinkLocked(
    const FablePrivateTextureCopyAction& action,
    const FableSurfaceTextureLink& link) {
  if (link.copy_symbol_address != kFablePrivateRectDrawHelperSymbol ||
      action.wrapper_symbol_address != kFablePrivateRectDrawHelperSymbol ||
      !DirectPrivateTextureCopyActionHasExactWrapperIdentity(action) ||
      action.wrapper_call_serial == 0 || action.wrapper_context_key == 0 ||
      link.copy_call_serial == 0 || link.copy_context_key == 0 ||
      action.wrapper_call_serial != link.copy_call_serial ||
      action.wrapper_context_key != link.copy_context_key ||
      action.source_texture == 0 || action.target_surface == 0 ||
      link.texture == 0 || link.source_surface == 0) {
    return false;
  }

  const bool same_kind =
      action.kind == link.kind ||
      (DirectPrivateTextureCopyKindUsesColorBacking(action.kind) &&
       DirectPrivateTextureCopyKindUsesColorBacking(link.kind));
  if (!same_kind) {
    return false;
  }
  const bool same_target =
      DirectResourceAddressMatchesLocked(action.target_surface,
                                         link.source_surface) ||
      DirectResourceAddressMatchesLocked(action.render_target,
                                         link.source_surface);
  if (!same_target) {
    return false;
  }
  const bool same_source =
      DirectResourceAddressMatchesLocked(action.source_texture, link.texture) ||
      (action.source_base_address != 0 &&
       action.source_base_address == link.texture_base_address) ||
      DirectTextureFetchDwordsMatch(action.source_fetch_dwords,
                                    link.texture_fetch_dwords);
  if (!same_source) {
    return false;
  }
  auto nonzero_field_matches = [](uint32_t lhs, uint32_t rhs) {
    return lhs == 0 || rhs == 0 || lhs == rhs;
  };
  return nonzero_field_matches(action.render_target, link.render_target) &&
         nonzero_field_matches(action.depth_stencil, link.depth_stencil) &&
         nonzero_field_matches(action.vertex_shader, link.vertex_shader) &&
         nonzero_field_matches(action.pixel_shader, link.pixel_shader);
}

void RememberDirectPrivateRectActionExactSurfaceLinkLocked(
    const FableDirectRenderCommand& command, uint32_t source_slot,
    const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    const char* reason) {
  if (source_slot >= command.resources.textures.size() ||
      command.draw.source == RendererDrawEventSource::kPixVideo ||
      source.guest_address == 0 || target.guest_address == 0 ||
      !IsImageResource(source.kind) || !IsImageResource(target.kind) ||
      !PlumeGuestImageReady(source) || !PlumeGuestImageReady(target) ||
      !DirectCommandUsesFablePrivateTexturePath(command, source_slot, &source) ||
      !DirectCommandBindsResourceAsTargetLocked(command, target)) {
    return;
  }

  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, source_slot);
  const std::array<uint32_t, 6> source_fetch =
      PlumeGuestTextureFetchDwords(source);
  if (!HasTrackedFetchDwords(command_fetch) &&
      !HasTrackedFetchDwords(source_fetch) &&
      !HasTrackedFetchDwords(action.source_fetch_dwords)) {
    return;
  }

  FableSurfaceTextureLink link;
  link.source_slot = source_slot;
  link.trusted_destination_edge = true;
  link.texture = action.source_texture != 0
                     ? action.source_texture
                     : (command.resources.textures[source_slot] != 0
                            ? command.resources.textures[source_slot]
                            : source.guest_address);
  link.texture_base_address =
      action.source_base_address != 0 ? action.source_base_address
                                      : PlumeGuestTextureBaseAddress(source);
  link.texture_width =
      action.source_width != 0 ? action.source_width : PlumeGuestTextureWidth(source);
  link.texture_height = action.source_height != 0
                            ? action.source_height
                            : PlumeGuestTextureHeight(source);
  link.texture_format = action.source_format != 0
                            ? action.source_format
                            : PlumeGuestTextureGuestFormat(source);
  link.texture_fetch_dwords =
      HasTrackedFetchDwords(action.source_fetch_dwords)
          ? action.source_fetch_dwords
          : (HasTrackedFetchDwords(command_fetch) ? command_fetch : source_fetch);
  link.source_surface = target.guest_address;
  link.target_content_serial =
      FableTrackedResourceVisibleContentSerial(target) != 0
          ? FableTrackedResourceVisibleContentSerial(target)
          : action.target_content_serial;
  link.render_target = command.resources.render_targets[0] != 0
                           ? command.resources.render_targets[0]
                           : action.render_target;
  link.depth_stencil = command.resources.depth_stencil != 0
                           ? command.resources.depth_stencil
                           : action.depth_stencil;
  link.vertex_shader = command.resources.vertex_shader;
  link.pixel_shader = command.resources.pixel_shader;
  PopulateDirectSurfaceTextureLinkShaderRoots(link, command);
  link.copy_call_serial = action.wrapper_call_serial;
  link.copy_context_key = action.wrapper_context_key;
  link.copy_symbol_address = action.wrapper_symbol_address;
  link.wrapper_source_texture = action.wrapper_source_texture;
  link.wrapper_source_exact = action.wrapper_source_exact;
  link.wrapper_setup_globals = action.wrapper_setup_globals;
  link.frame_index = g_direct_renderer.current_frame_index;
  link.draw_serial = command.draw_serial != 0 ? command.draw_serial
                                              : command.serial;
  link.kind = action.kind;
  link.sampled_source = action.sampled_source;

  if (link.texture == 0 || link.texture == link.source_surface) {
    return;
  }
  if (link.copy_symbol_address == kFablePrivateRectDrawHelperSymbol &&
      action.target_surface == 0) {
    static uint32_t logged_draw_helper_targetless_link_rejects = 0;
    if (logged_draw_helper_targetless_link_rejects < 32u) {
      REXLOG_INFO(
          "Refii plume exact private rect draw-helper targetless surface-link reject "
          "#{} frame={} reason={} slot={} texture=0x{:08X}/{}x{} "
          "base=0x{:08X} surface=0x{:08X}/{}x{} call={} "
          "context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} "
          "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_draw_helper_targetless_link_rejects,
          g_direct_renderer.current_frame_index, reason ? reason : "unknown",
          source_slot, link.texture, link.texture_width, link.texture_height,
          link.texture_base_address, link.source_surface,
          PlumeGuestTextureWidth(target), PlumeGuestTextureHeight(target),
          static_cast<unsigned long long>(link.copy_call_serial),
          static_cast<unsigned long long>(link.copy_context_key),
          link.copy_symbol_address, link.render_target, link.depth_stencil,
          link.vertex_shader, link.pixel_shader);
    }
    return;
  }
  auto register_current_target_exact_texture_identity =
      [&](const char* role, const char* register_reason) {
        RegisterDirectSurfaceTextureLinkPreparedLocked(link, register_reason);
        static uint32_t logged_current_target_exact_texture_identities = 0;
        if (logged_current_target_exact_texture_identities < 128u) {
          REXLOG_INFO(
              "Refii plume exact current-target private-rect "
              "surface-texture identity #{} frame={} role={} reason={} slot={} "
              "texture=0x{:08X}/{}x{} base=0x{:08X} surface=0x{:08X}/{}x{} "
              "kind={} call={} context=0x{:016X} symbol=0x{:08X} "
              "setup={:08X}/{:08X}/{:08X}/{:08X} content={} rt=0x{:08X} "
              "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
              ++logged_current_target_exact_texture_identities,
              g_direct_renderer.current_frame_index, role ? role : "unknown",
              reason ? reason : "unknown", source_slot, link.texture,
              link.texture_width, link.texture_height,
              link.texture_base_address, link.source_surface,
              PlumeGuestTextureWidth(target), PlumeGuestTextureHeight(target),
              FablePrivateTextureCopyKindName(link.kind),
              static_cast<unsigned long long>(link.copy_call_serial),
              static_cast<unsigned long long>(link.copy_context_key),
              link.copy_symbol_address, link.wrapper_setup_globals[0],
              link.wrapper_setup_globals[1], link.wrapper_setup_globals[2],
              link.wrapper_setup_globals[3],
              static_cast<unsigned long long>(link.target_content_serial),
              link.render_target, link.depth_stencil, link.vertex_shader,
              link.pixel_shader);
        }
      };

  if (DirectPrivateRectSetupFlushActionCanOwnExactSurfaceLinkLocked(action,
                                                                    link)) {
    if (!DirectSurfaceTextureLinkHasCommandConsumableSampleIdentity(link)) {
      static uint32_t logged_exact_setup_flush_unconsumable_link_rejects = 0;
      if (logged_exact_setup_flush_unconsumable_link_rejects < 48u) {
        REXLOG_INFO(
            "Refii plume exact setup-flush unconsumable private rect "
            "surface-link reject #{} frame={} reason={} slot={} "
            "texture=0x{:08X}/{}x{} base=0x{:08X} surface=0x{:08X}/{}x{} "
            "kind={} call={} context=0x{:016X} symbol=0x{:08X} "
            "setup={:08X}/{:08X}/{:08X}/{:08X} content={} rt=0x{:08X} "
            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_exact_setup_flush_unconsumable_link_rejects,
            g_direct_renderer.current_frame_index, reason ? reason : "unknown",
            source_slot, link.texture, link.texture_width, link.texture_height,
            link.texture_base_address, link.source_surface,
            PlumeGuestTextureWidth(target), PlumeGuestTextureHeight(target),
            FablePrivateTextureCopyKindName(link.kind),
            static_cast<unsigned long long>(link.copy_call_serial),
            static_cast<unsigned long long>(link.copy_context_key),
            link.copy_symbol_address, link.wrapper_setup_globals[0],
            link.wrapper_setup_globals[1], link.wrapper_setup_globals[2],
            link.wrapper_setup_globals[3],
            static_cast<unsigned long long>(link.target_content_serial),
            link.render_target, link.depth_stencil, link.vertex_shader,
            link.pixel_shader);
      }
      return;
    }
    register_current_target_exact_texture_identity(
        "setup-flush", "setup-flush-current-target-exact-action");
    return;
  }
  if (DirectPrivateRectSiblingActionCanOwnExactSurfaceLinkLocked(action,
                                                                 link)) {
    register_current_target_exact_texture_identity(
        "private-rect-c4", "c4-current-target-exact-action");
    return;
  }
  if (DirectPrivateRectCompactActionCanOwnExactSurfaceLinkLocked(action,
                                                                  link)) {
    register_current_target_exact_texture_identity(
        "compact", "compact-current-target-exact-action");
    return;
  }
  if (DirectPrivateRectDrawHelperActionCanOwnExactSurfaceLinkLocked(action,
                                                                    link)) {
    register_current_target_exact_texture_identity(
        "draw-helper", "draw-helper-current-target-exact-action");
    return;
  }
  if (DirectPrivateRectExactSurfaceLinkCoveredByExactLinkLocked(command,
                                                               link)) {
    if (g_direct_renderer.duplicate_exact_private_rect_surface_link_keys
            .size() > 4096u) {
      g_direct_renderer.duplicate_exact_private_rect_surface_link_keys.clear();
    }
    g_direct_renderer.duplicate_exact_private_rect_surface_link_keys.insert(
        DirectCoveredDuplicatePrivateRectSurfaceLinkKey(link));
    return;
  }

  static uint32_t logged_rejected_action_exact_surface_links = 0;
  if (logged_rejected_action_exact_surface_links < 48u) {
    REXLOG_INFO(
        "Refii plume exact private rect action surface-link reject #{} "
        "frame={} reason={} slot={} texture=0x{:08X}/{}x{} base=0x{:08X} "
        "surface=0x{:08X}/{}x{} kind={} call={} context=0x{:016X} "
        "symbol=0x{:08X} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
        ++logged_rejected_action_exact_surface_links,
        g_direct_renderer.current_frame_index, reason ? reason : "unknown",
        source_slot, link.texture, link.texture_width, link.texture_height,
        link.texture_base_address, link.source_surface,
        PlumeGuestTextureWidth(target), PlumeGuestTextureHeight(target),
        FablePrivateTextureCopyKindName(link.kind),
        static_cast<unsigned long long>(link.copy_call_serial),
        static_cast<unsigned long long>(link.copy_context_key),
        link.copy_symbol_address, link.render_target, link.depth_stencil,
        link.vertex_shader, link.pixel_shader);
  }
  return;
}

PlumeTrackedResource* EnsureDirectPrivateRectFeedbackAliasLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command,
    uint32_t source_slot) {
  const bool private_rect_feedback =
      command.draw.private_texture_rectangle &&
      IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader);
  const bool title_scene_feedback =
      command.draw.source == RendererDrawEventSource::kSceneObject &&
      (IsFableTitleSceneVertexShaderCommandLocked(command) ||
       IsFableTitleSceneTargetLocked(command.resources.render_targets[0]));
  if (!device || !command_list ||
      (!private_rect_feedback && !title_scene_feedback) ||
      source_slot >= command.resources.textures.size()) {
    return nullptr;
  }

  const uint32_t source_guest =
      command.resources.textures[source_slot];
  const bool depth_private_rect_feedback =
      private_rect_feedback &&
      DirectSyntheticPrivateRectCommandUsesDepthTarget(command);
  const uint32_t target_guest =
      depth_private_rect_feedback ? command.resources.depth_stencil
                                  : command.resources.render_targets[0];
  if (source_guest == 0 || target_guest == 0) {
    return nullptr;
  }
  const FableDirectClock::time_point feedback_alias_t0 =
      FableDirectClock::now();

  PlumeTrackedResource* source = FindDirectResourceLocked(source_guest);
  PlumeTrackedResource* target = FindDirectResourceLocked(target_guest);
  const bool target_is_scene_output =
      target && title_scene_feedback &&
      DirectResourceIsTrackedSceneOutputLocked(*target);
  const bool target_is_valid_private_feedback_target =
      target &&
      ((depth_private_rect_feedback && target->texture_depth_target) ||
       (!depth_private_rect_feedback && target->texture_render_target &&
        !target->texture_depth_target));
  if (!source || !target || !PlumeGuestImageReady(*source) ||
      !PlumeGuestImageReady(*target) ||
      !DirectCommandUsesFablePrivateTexturePath(
          command, source_slot, source) ||
      !DirectCommandBindsResourceAsTargetLocked(command, *target) ||
      !target_is_valid_private_feedback_target ||
      (!IsFablePrivateTextureChainResource(*target) &&
       !target_is_scene_output) ||
      FableTrackedResourceVisibleContentSerial(*target) == 0) {
    return nullptr;
  }
  auto log_feedback_alias_stage = [&](const char* stage, double elapsed_ms,
                                      const PlumeTrackedResource* alias) {
    if (elapsed_ms < 2.0 ||
        g_direct_renderer.diagnostics.logged_private_rect_feedback_timing_breakdowns >=
            32u) {
      return;
    }
    REXLOG_INFO(
        "Refii plume private rect feedback timing #{} frame={} stage={} "
        "slot={} source=0x{:08X}/{}x{} base=0x{:08X} "
        "target=0x{:08X}/{}x{} alias=0x{:08X}/{}x{} "
        "elapsed_ms={:.3f} vs=0x{:08X} ps=0x{:08X}",
        ++g_direct_renderer.diagnostics.logged_private_rect_feedback_timing_breakdowns,
        g_direct_renderer.current_frame_index, stage, source_slot,
        source_guest, PlumeGuestTextureWidth(*source),
        PlumeGuestTextureHeight(*source),
        PlumeGuestTextureBaseAddress(*source), target->guest_address,
        PlumeGuestTextureWidth(*target), PlumeGuestTextureHeight(*target),
        alias ? alias->guest_address : 0u,
        alias ? PlumeGuestTextureWidth(*alias) : 0u,
        alias ? PlumeGuestTextureHeight(*alias) : 0u, elapsed_ms,
        command.resources.vertex_shader, command.resources.pixel_shader);
  };

  if (PlumeTrackedResource* uploaded_source =
          FindDirectPrivateRectExactUploadedBackingLocked(
              command, source_slot, source)) {
    if (g_direct_renderer.diagnostics.logged_private_rect_feedback_events < 32u) {
      REXLOG_INFO(
          "Refii plume exact private rect feedback direct-upload source reject #{} "
          "frame={} slot={} source=0x{:08X}/{}x{} "
          "uploaded=0x{:08X}/{}x{} target=0x{:08X}/{}x{} "
          "base=0x{:08X} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++g_direct_renderer.diagnostics.logged_private_rect_feedback_events,
          g_direct_renderer.current_frame_index,
          source_slot, source_guest,
          PlumeGuestTextureWidth(*source), PlumeGuestTextureHeight(*source),
          uploaded_source->guest_address,
          PlumeGuestTextureWidth(*uploaded_source),
          PlumeGuestTextureHeight(*uploaded_source), target->guest_address,
          PlumeGuestTextureWidth(*target), PlumeGuestTextureHeight(*target),
          PlumeGuestTextureBaseAddress(*source),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return nullptr;
  }

  const bool source_needs_snapshot =
      FableTrackedResourceIsUnbackedSampleProxy(*source) ||
      FableTrackedResourceIsBlankUploadedSampleProxy(*source) ||
      DirectPrivateTextureHeaderNeedsAlias(command, source_slot, source);
  const bool source_has_direct_upload =
      FableTrackedResourceHasMeaningfulTextureUpload(*source) &&
      !FableTrackedResourceIsBlankUploadedSampleProxy(*source);
  if (source_has_direct_upload &&
      !DirectPrivateTextureHeaderNeedsAlias(command, source_slot, source)) {
    return nullptr;
  }

  FablePrivateTextureCopyAction exact_feedback_action_storage;
  const FablePrivateTextureCopyAction* exact_feedback_action = nullptr;
  auto exact_action_reads_current_target =
      [&](const FablePrivateTextureCopyAction* action) {
    return action && action->kind != FablePrivateTextureCopyKind::kDepth &&
           (DirectResourceAddressMatchesLocked(action->target_surface,
                                               target->guest_address) ||
            DirectResourceAddressMatchesLocked(action->render_target,
                                               target->guest_address) ||
            DirectResourceAddressMatchesLocked(
                action->target_surface, command.resources.render_targets[0]) ||
            DirectResourceAddressMatchesLocked(
                action->render_target, command.resources.render_targets[0])) &&
           DirectCommandBindsResourceAsTargetLocked(command, *target);
  };
  exact_feedback_action = FindDirectPrivateRectMaterializationActionLocked(
      command, source_slot, source, *target);
  if (exact_feedback_action) {
    exact_feedback_action_storage = *exact_feedback_action;
    exact_feedback_action = &exact_feedback_action_storage;
  }
  if (!exact_feedback_action) {
    DirectPrivateTextureCopyActionMatch selected_match =
        DirectPrivateTextureCopyActionMatch::kNone;
    if (const FablePrivateTextureCopyAction* selected_action =
            SelectDirectExactPrivateTextureCopyActionForCommandSlotLocked(
                command, source_slot, source, &selected_match);
        selected_action &&
        DirectPrivateTextureCopyActionMatchIsExactRegistered(selected_match)) {
      exact_feedback_action_storage = *selected_action;
      exact_feedback_action = &exact_feedback_action_storage;
    }
  }
  if (exact_feedback_action) {
    auto remember_exact_uploaded_source =
        [&](PlumeTrackedResource* uploaded_source) -> bool {
      if (!uploaded_source) {
        return false;
      }
      RememberDirectPrivateTextureSiblingAliasAddressLocked(source_guest,
                                                           *uploaded_source);
      RememberDirectPrivateTextureSiblingAliasLocked(*source, *uploaded_source);
      RememberDirectPrivateTextureAliasIndexLocked(
          source_guest,
          exact_feedback_action->source_base_address != 0
              ? exact_feedback_action->source_base_address
              : PlumeGuestTextureBaseAddress(*source),
          HasTrackedFetchDwords(exact_feedback_action->source_fetch_dwords)
              ? exact_feedback_action->source_fetch_dwords
              : DirectCommandTextureFetchDwords(
                    command, source_slot),
          *uploaded_source);
      if (g_direct_renderer.diagnostics.logged_private_rect_feedback_events < 32u) {
        REXLOG_INFO(
            "Refii plume private rect feedback exact action source #{} "
            "frame={} slot={} source=0x{:08X}/{}x{} "
            "uploaded=0x{:08X}/{}x{} target=0x{:08X}/{}x{} "
            "base=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++g_direct_renderer.diagnostics.logged_private_rect_feedback_events,
            g_direct_renderer.current_frame_index,
            source_slot, source_guest,
            PlumeGuestTextureWidth(*source), PlumeGuestTextureHeight(*source),
            uploaded_source->guest_address,
            PlumeGuestTextureWidth(*uploaded_source),
            PlumeGuestTextureHeight(*uploaded_source), target->guest_address,
            PlumeGuestTextureWidth(*target), PlumeGuestTextureHeight(*target),
            exact_feedback_action->source_base_address,
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
      return true;
    };
    auto exact_backing_is_sampleable = [&]() -> PlumeTrackedResource* {
      auto validate_backing =
          [&](PlumeTrackedResource* backing) -> PlumeTrackedResource* {
        if (!backing || !IsImageResource(backing->kind) ||
            !PlumeGuestImageReady(*backing) ||
            !FableTrackedResourceHasSampleableContent(*backing) ||
            FableTrackedResourceIsBlankUploadedSampleProxy(*backing) ||
            DirectCommandBindsResourceAsTargetLocked(command, *backing)) {
          return nullptr;
        }
        if (exact_feedback_action->backing_width != 0 &&
            PlumeGuestTextureWidth(*backing) !=
                exact_feedback_action->backing_width) {
          return nullptr;
        }
        if (exact_feedback_action->backing_height != 0 &&
            PlumeGuestTextureHeight(*backing) !=
                exact_feedback_action->backing_height) {
          return nullptr;
        }
        if (exact_feedback_action->backing_format != 0) {
          const uint32_t guest_format =
              PlumeGuestTextureGuestFormat(*backing);
          const uint32_t render_format =
              static_cast<uint32_t>(PlumeGuestTextureFormat(*backing));
          if (guest_format != exact_feedback_action->backing_format &&
              render_format != exact_feedback_action->backing_format) {
            return nullptr;
          }
        }
        if (exact_feedback_action->backing_base_address != 0 &&
            PlumeGuestTextureBaseAddress(*backing) !=
                exact_feedback_action->backing_base_address) {
          return nullptr;
        }
        if (HasTrackedFetchDwords(
                exact_feedback_action->backing_fetch_dwords) &&
            !DirectTextureFetchDwordsMatch(
                exact_feedback_action->backing_fetch_dwords,
                PlumeGuestTextureFetchDwords(*backing))) {
          return nullptr;
        }
        if (!DirectPrivateTextureCopyActionBackingContainsTargetContentLocked(
                *exact_feedback_action, *backing, *target) &&
            !DirectPrivateFeedbackAliasContainsTargetContentLocked(*backing,
                                                                  *target)) {
          return nullptr;
        }
        return backing;
      };
      auto try_backing_address = [&](uint32_t address) -> PlumeTrackedResource* {
        return address != 0 ? validate_backing(FindDirectResourceLocked(address))
                            : nullptr;
      };
      auto try_backing_identity =
          [&](const FableTextureIdentity* identity) -> PlumeTrackedResource* {
        if (!identity) {
          return nullptr;
        }
        if (PlumeTrackedResource* backing =
                try_backing_address(identity->object_texture)) {
          return backing;
        }
        if (PlumeTrackedResource* backing =
                try_backing_address(identity->parent_texture)) {
          return backing;
        }
        return try_backing_address(identity->base_address);
      };
      auto try_backing_identities =
          [&](const std::vector<uint32_t>* objects) -> PlumeTrackedResource* {
        if (!objects) {
          return nullptr;
        }
        for (uint32_t object : *objects) {
          if (PlumeTrackedResource* backing = try_backing_identity(
                  FableDirectFindTextureIdentityByObject(object))) {
            return backing;
          }
        }
        return nullptr;
      };

      if (PlumeTrackedResource* backing =
              try_backing_address(exact_feedback_action->backing_texture)) {
        return backing;
      }
      if (PlumeTrackedResource* backing =
              try_backing_address(exact_feedback_action->backing_base_address)) {
        return backing;
      }
      if (PlumeTrackedResource* backing = try_backing_identity(
              FableDirectFindTextureIdentityByObject(
                  exact_feedback_action->backing_texture))) {
        return backing;
      }
      if (PlumeTrackedResource* backing = try_backing_identity(
              FableDirectFindTextureIdentityByBase(
                  exact_feedback_action->backing_base_address))) {
        return backing;
      }
      if (PlumeTrackedResource* backing = try_backing_identities(
              FableDirectFindTextureIdentityObjectsByBase(
                  exact_feedback_action->backing_base_address))) {
        return backing;
      }
      if (HasTrackedFetchDwords(exact_feedback_action->backing_fetch_dwords)) {
        if (PlumeTrackedResource* backing = try_backing_identity(
                FableDirectFindTextureIdentityByFetch(
                    exact_feedback_action->backing_fetch_dwords))) {
          return backing;
        }
        if (PlumeTrackedResource* backing = try_backing_identities(
                FableDirectFindTextureIdentityObjectsByFetch(
                    exact_feedback_action->backing_fetch_dwords))) {
          return backing;
        }
      }
      return nullptr;
    };
    if (PlumeTrackedResource* exact_backing = exact_backing_is_sampleable();
        remember_exact_uploaded_source(exact_backing)) {
      return exact_backing;
    }
    if (!exact_action_reads_current_target(exact_feedback_action)) {
      if (PlumeTrackedResource* uploaded_source =
              FindDirectPrivateRectUploadedActionSourceLocked(
                  command, source_slot, *exact_feedback_action, source);
          remember_exact_uploaded_source(uploaded_source)) {
        return uploaded_source;
      }
    }

    const bool title_private_action =
        IsFableTitlePrivateTextureProxyAddress(source_guest) ||
        IsFableTitlePrivateTextureProxyAddress(
            PlumeGuestTextureBaseAddress(*source)) ||
        IsFablePrivateTextureChainResource(*source) ||
        IsFableTitlePrivateTextureProxyAddress(target->guest_address) ||
        IsFableTitlePrivateTextureProxyAddress(
            PlumeGuestTextureBaseAddress(*target)) ||
        IsFablePrivateTextureChainResource(*target);
    if (title_private_action) {
      if (exact_action_reads_current_target(exact_feedback_action)) {
        RememberDirectPrivateRectActionExactSurfaceLinkLocked(
            command, source_slot, *exact_feedback_action, *source, *target,
            "feedback-exact-action");
      }
      if (g_direct_renderer.diagnostics.logged_private_rect_feedback_exact_source_holds <
          32u) {
        REXLOG_INFO(
            "Refii plume snapshot private rect feedback exact source #{} "
            "frame={} slot={} source=0x{:08X}/{}x{} base=0x{:08X} "
            "target=0x{:08X}/{}x{} action_draw={} action_target=0x{:08X} "
            "action_base=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++g_direct_renderer
                  .diagnostics.logged_private_rect_feedback_exact_source_holds,
            g_direct_renderer.current_frame_index,
            source_slot, source_guest,
            PlumeGuestTextureWidth(*source), PlumeGuestTextureHeight(*source),
            PlumeGuestTextureBaseAddress(*source), target->guest_address,
            PlumeGuestTextureWidth(*target), PlumeGuestTextureHeight(*target),
            static_cast<unsigned long long>(
                exact_feedback_action->draw_serial),
            exact_feedback_action->target_surface,
            exact_feedback_action->source_base_address,
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
    }
  }
  log_feedback_alias_stage(
      "initial-action-lookup",
      FableDirectElapsedMs(feedback_alias_t0, FableDirectClock::now()),
      nullptr);

  const uint32_t target_width = PlumeGuestTextureWidth(*target);
  const uint32_t target_height = PlumeGuestTextureHeight(*target);
  const plume::RenderFormat format = PlumeGuestTextureFormat(*target);
  if (target_width == 0 || target_height == 0 ||
      format == plume::RenderFormat::UNKNOWN) {
    return nullptr;
  }
  const uint32_t width = target_width;
  const uint32_t height = target_height;
  auto remember_direct_action_sample =
      [&](PlumeTrackedResource* uploaded_source) -> bool {
    if (!uploaded_source || !exact_feedback_action) {
      return false;
    }
    if ((uploaded_source->guest_address & 0xFF000000u) == 0xF1000000u) {
      if (!DirectPrivateFeedbackAliasContainsTargetContentLocked(
              *uploaded_source, *target)) {
        return false;
      }
    }
    RememberDirectPrivateTextureSiblingAliasAddressLocked(source_guest,
                                                         *uploaded_source);
    RememberDirectPrivateTextureSiblingAliasLocked(*source, *uploaded_source);
    RememberDirectPrivateTextureAliasIndexLocked(
        source_guest,
        exact_feedback_action->source_base_address != 0
            ? exact_feedback_action->source_base_address
            : PlumeGuestTextureBaseAddress(*source),
        HasTrackedFetchDwords(exact_feedback_action->source_fetch_dwords)
            ? exact_feedback_action->source_fetch_dwords
            : DirectCommandTextureFetchDwords(command, source_slot),
        *uploaded_source);
    if (g_direct_renderer.diagnostics.logged_private_rect_feedback_events < 32u) {
      REXLOG_INFO(
          "Refii plume private rect feedback registered action source #{} "
          "frame={} slot={} source=0x{:08X}/{}x{} "
          "sample=0x{:08X}/{}x{} target=0x{:08X}/{}x{} "
          "action_draw={} base=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++g_direct_renderer.diagnostics.logged_private_rect_feedback_events,
          g_direct_renderer.current_frame_index, source_slot, source_guest,
          PlumeGuestTextureWidth(*source), PlumeGuestTextureHeight(*source),
          uploaded_source->guest_address,
          PlumeGuestTextureWidth(*uploaded_source),
          PlumeGuestTextureHeight(*uploaded_source), target->guest_address,
          PlumeGuestTextureWidth(*target), PlumeGuestTextureHeight(*target),
          static_cast<unsigned long long>(exact_feedback_action->draw_serial),
          exact_feedback_action->source_base_address,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return true;
  };
  if (private_rect_feedback && exact_feedback_action &&
      !exact_action_reads_current_target(exact_feedback_action)) {
    if (PlumeTrackedResource* uploaded_source =
            FindDirectPrivateRectUploadedActionSourceLocked(
                command, source_slot, *exact_feedback_action, source);
        remember_direct_action_sample(uploaded_source)) {
      return uploaded_source;
    }
  }
  if (!exact_feedback_action && source_needs_snapshot &&
      g_direct_renderer.diagnostics.logged_private_rect_feedback_unresolved < 16u) {
    auto action_count =
        [](const std::vector<FablePrivateTextureCopyAction>* actions) {
          return actions ? actions->size() : size_t{0};
        };
    float rect_width = 0.0f;
    float rect_height = 0.0f;
    if (command.draw.private_texture_corners_valid) {
      const auto& corners = command.draw.private_texture_corners;
      rect_width = std::fabs(corners[2] - corners[0]);
      rect_height = std::fabs(corners[5] - corners[1]);
    }
    const std::array<uint32_t, 6> command_fetch =
        DirectCommandTextureFetchDwords(command,
                                        source_slot);
    REXLOG_INFO(
        "Refii plume private rect feedback unresolved #{} frame={} "
        "slot={} source=0x{:08X}/{}x{} target=0x{:08X}/{}x{} "
        "rt=0x{:08X} rect={:.2f}x{:.2f} "
        "actions source={} resource={} parent={} target={} rt={} fetch={} "
        "base=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
        ++g_direct_renderer.diagnostics.logged_private_rect_feedback_unresolved,
        g_direct_renderer.current_frame_index, source_slot,
        source_guest, PlumeGuestTextureWidth(*source),
        PlumeGuestTextureHeight(*source), target->guest_address,
        PlumeGuestTextureWidth(*target), PlumeGuestTextureHeight(*target),
        command.resources.render_targets[0], rect_width, rect_height,
        action_count(
            FableDirectFindPrivateTextureCopyActionsBySource(source_guest)),
        action_count(FableDirectFindPrivateTextureCopyActionsBySource(
            source->guest_address)),
        action_count(FableDirectFindPrivateTextureCopyActionsBySource(
            source->parent_address)),
        action_count(FableDirectFindPrivateTextureCopyActionsByTarget(
            target->guest_address)),
        action_count(FableDirectFindPrivateTextureCopyActionsByTarget(
            command.resources.render_targets[0])),
        action_count(FableDirectFindPrivateTextureCopyActionsByFetch(
            command_fetch)),
        PlumeGuestTextureBaseAddress(*source), command.resources.vertex_shader,
        command.resources.pixel_shader);
  }
  if (!source_needs_snapshot && !exact_feedback_action) {
    return nullptr;
  }
  if (!IsFableTitlePrivateTextureProxyAddress(source_guest) &&
      !IsFablePrivateTextureChainResource(*source)) {
    return nullptr;
  }
  if (g_direct_renderer.diagnostics.logged_private_rect_feedback_unresolved < 32u) {
    REXLOG_INFO(
        "Refii plume private rect feedback unresolved without scratch alias #{} "
        "frame={} slot={} source=0x{:08X}/{}x{} target=0x{:08X}/{}x{} "
        "exact_action={} action_target=0x{:08X} action_base=0x{:08X} "
        "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
        ++g_direct_renderer.diagnostics.logged_private_rect_feedback_unresolved,
        g_direct_renderer.current_frame_index, source_slot, source_guest,
        PlumeGuestTextureWidth(*source), PlumeGuestTextureHeight(*source),
        target->guest_address, width, height, exact_feedback_action ? 1 : 0,
        exact_feedback_action ? exact_feedback_action->target_surface : 0u,
        exact_feedback_action ? exact_feedback_action->source_base_address : 0u,
        command.resources.render_targets[0], command.resources.depth_stencil,
        command.resources.vertex_shader, command.resources.pixel_shader);
  }
  return nullptr;
}


bool FindCachedDirectPrivateTextureCopyActionSampleLocked(
    uint64_t cache_key, const FablePrivateTextureCopyAction& action,
    PlumeTrackedResource** sample) {
  if (sample) {
    *sample = nullptr;
  }
  auto cached_it =
      g_direct_renderer.private_texture_copy_action_sample_cache.find(
          cache_key);
  if (cached_it ==
      g_direct_renderer.private_texture_copy_action_sample_cache.end()) {
    return false;
  }

  const uint64_t private_copy_serial =
      DirectPrivateTextureCopySerialLocked();
  const DirectPrivateTextureCopyActionSampleCacheEntry& entry =
      cached_it->second;
  if (entry.action_serial != action.serial ||
      entry.private_copy_serial != private_copy_serial) {
    g_direct_renderer.private_texture_copy_action_sample_cache.erase(cached_it);
    return false;
  }
  if (!entry.resolved || entry.sample_address == 0) {
    return true;
  }

  PlumeTrackedResource* cached = FindDirectResourceLocked(entry.sample_address);
  if (!cached || !IsImageResource(cached->kind) ||
      !PlumeGuestImageReady(*cached) ||
      FableTrackedResourceIsBlankUploadedSampleProxy(*cached) ||
      (!FableTrackedResourceHasSampleableContent(*cached) &&
       !FableTrackedResourceHasGpuProducedContent(*cached))) {
    g_direct_renderer.private_texture_copy_action_sample_cache.erase(cached_it);
    return false;
  }
  if (sample) {
    *sample = cached;
  }
  return true;
}

void RememberDirectPrivateTextureCopyActionSampleLocked(
    uint64_t cache_key, const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource* sample) {
  constexpr size_t kMaxPrivateTextureCopyActionSampleCacheEntries = 4096u;
  if (g_direct_renderer.private_texture_copy_action_sample_cache.size() >=
      kMaxPrivateTextureCopyActionSampleCacheEntries) {
    g_direct_renderer.private_texture_copy_action_sample_cache.clear();
    g_direct_renderer.private_texture_exact_interest_cache.clear();
    g_direct_renderer.private_texture_backing_miss_cache.clear();
    g_direct_renderer.registered_private_rect_execute_decision_cache.clear();
  }
  g_direct_renderer.private_texture_copy_action_sample_cache[cache_key] = {
      sample ? sample->guest_address : 0u, sample != nullptr, action.serial,
      DirectPrivateTextureCopySerialLocked()};
}

PlumeTrackedResource *ResolveDirectPrivateTextureCopyActionLocked(
    const FableDirectRenderCommand &command, uint32_t slot,
    PlumeTrackedResource *texture) {
  if (!DirectCommandUsesFablePrivateTexturePath(command, slot, texture)) {
    return nullptr;
  }
  const bool private_header_needs_alias =
      DirectPrivateTextureHeaderNeedsAlias(command, slot, texture);
  const bool private_title_identity =
      DirectCommandTextureSlotHasPrivateTitleIdentityLocked(command, slot,
                                                            texture);
  if (texture &&
      (FableTrackedResourceHasSampleableContent(*texture) ||
       FableTrackedResourceHasGpuProducedContent(*texture)) &&
      !FableTrackedResourceIsBlankUploadedSampleProxy(*texture) &&
      !private_header_needs_alias && !private_title_identity) {
    return nullptr;
  }

  const uint32_t guest = command.resources.textures[slot];
  if (texture && texture->guest_address != guest &&
      PlumeGuestImageReady(*texture)) {
    return nullptr;
  }
  DirectPrivateTextureCopyActionMatch action_match =
      DirectPrivateTextureCopyActionMatch::kNone;
  const FablePrivateTextureCopyAction* action =
      SelectDirectExactPrivateTextureCopyActionForCommandSlotLocked(
          command, slot, texture, &action_match);
  if (!action) {
    return nullptr;
  }
  if (!DirectPrivateTextureCopyActionMatchIsExactRegistered(action_match)) {
    static uint32_t logged_rejected_inexact_private_copy_actions = 0;
    if (logged_rejected_inexact_private_copy_actions < 32u) {
      REXLOG_INFO(
          "Refii plume rejected inexact private copy action #{} frame={} "
          "slot={} match={} guest=0x{:08X} tracked=0x{:08X} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_rejected_inexact_private_copy_actions,
          g_direct_renderer.current_frame_index, slot,
          DirectPrivateTextureCopyActionMatchName(action_match), guest,
          texture ? texture->guest_address : 0u,
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return nullptr;
  }
  const uint64_t action_sample_cache_key =
      DirectPrivateTextureCopyActionSampleCacheKeyLocked(command, slot, texture,
                                                         *action);
  PlumeTrackedResource* cached_action_sample = nullptr;
  if (FindCachedDirectPrivateTextureCopyActionSampleLocked(
          action_sample_cache_key, *action, &cached_action_sample)) {
    return cached_action_sample;
  }
  CountDirectPrivateTextureCopyActionMatchLocked(action_match);
  const bool synthetic_private_rect_copy =
      command.draw.private_texture_rectangle &&
      IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader);
  if (action->kind != FablePrivateTextureCopyKind::kDepth) {
    if (PlumeTrackedResource* registered_sample =
            ResolveDirectRegisteredSampleSourceLocked(command,
                                                      action->sampled_source)) {
      if (g_direct_renderer.diagnostics.logged_private_rect_backing_aliases < 32u) {
        REXLOG_INFO(
            "Refii plume exact private-copy action texture identity #{} "
            "frame={} slot={} match={} guest=0x{:08X} source=0x{:08X} "
            "base=0x{:08X} {}x{} fmt={} sample_kind={} "
            "sample=0x{:08X}/{}x{} sample_fmt={} sample_content={} "
            "required_content={} serial={} action_draw={} "
            "action_call={} action_context=0x{:016X} action_symbol=0x{:08X} "
            "action_rt=0x{:08X} action_ds=0x{:08X} "
            "action_vs=0x{:08X} action_ps=0x{:08X} "
            "action_vs_root=0x{:08X}/{} action_ps_root=0x{:08X}/{} "
            "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
            "cmd_vs_root=0x{:08X}/{} cmd_ps_root=0x{:08X}/{}",
            ++g_direct_renderer.diagnostics.logged_private_rect_backing_aliases,
            g_direct_renderer.current_frame_index, slot,
            DirectPrivateTextureCopyActionMatchName(action_match), guest,
            action->source_texture, action->source_base_address,
            action->source_width, action->source_height,
            action->source_format,
            FableRegisteredSampleSourceKindName(action->sampled_source.kind),
            registered_sample->guest_address,
            PlumeGuestTextureWidth(*registered_sample),
            PlumeGuestTextureHeight(*registered_sample),
            static_cast<uint32_t>(PlumeGuestTextureFormat(*registered_sample)),
            static_cast<unsigned long long>(
                FableTrackedResourceVisibleContentSerial(*registered_sample)),
            static_cast<unsigned long long>(
                action->sampled_source.content_serial),
            static_cast<unsigned long long>(action->serial),
            static_cast<unsigned long long>(action->draw_serial),
            static_cast<unsigned long long>(action->wrapper_call_serial),
            static_cast<unsigned long long>(action->wrapper_context_key),
            action->wrapper_symbol_address,
            action->render_target, action->depth_stencil,
            action->vertex_shader, action->pixel_shader,
            action->vertex_shader_root_symbol_address,
            static_cast<unsigned long long>(
                action->vertex_shader_root_call_serial),
            action->pixel_shader_root_symbol_address,
            static_cast<unsigned long long>(
                action->pixel_shader_root_call_serial),
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader,
            command.resources.shader_roots.vertex_shader.symbol_address,
            static_cast<unsigned long long>(
                command.resources.shader_roots.vertex_shader.call_serial),
            command.resources.shader_roots.pixel_shader.symbol_address,
            static_cast<unsigned long long>(
                command.resources.shader_roots.pixel_shader.call_serial));
      }
      RememberDirectMutablePrivateHeaderExactSampleLocked(
          command, slot, *action, *registered_sample,
          "private-copy-registered-sample");
      RememberDirectPrivateTextureCopyActionSampleLocked(
          action_sample_cache_key, *action, registered_sample);
      return registered_sample;
    }
  }
  PlumeTrackedResource* backing =
      FindDirectResourceLocked(action->backing_texture);
  PlumeTrackedResource* backing_action_target =
      FindDirectResourceLocked(action->target_surface);
  if (!backing_action_target && action->render_target != 0) {
    backing_action_target = FindDirectResourceLocked(action->render_target);
  }
  if (backing && (!texture || backing != texture) &&
      IsImageResource(backing->kind) && PlumeGuestImageReady(*backing) &&
      FableTrackedResourceHasSampleableContent(*backing) &&
      !FableTrackedResourceIsBlankUploadedSampleProxy(*backing) &&
      !DirectCommandBindsResourceAsTargetLocked(command, *backing) &&
      (!backing_action_target ||
       action->kind == FablePrivateTextureCopyKind::kDepth ||
       DirectPrivateTextureCopyActionBackingContainsTargetContentLocked(
           *action, *backing, *backing_action_target) ||
       DirectPrivateFeedbackAliasContainsTargetContentLocked(
           *backing, *backing_action_target))) {
    if (g_direct_renderer.diagnostics.logged_private_rect_backing_aliases < 32u) {
      REXLOG_INFO(
          "Refii plume private copy action backing alias #{} frame={} "
          "slot={} match={} guest=0x{:08X} source=0x{:08X} base=0x{:08X} "
          "{}x{} fmt={} backing=0x{:08X}/{}x{} backing_fmt={} kind={} "
          "sampled={} sample=0x{:08X} serial={} action_target=0x{:08X} "
          "action_rt=0x{:08X} "
          "action_ds=0x{:08X} action_vs=0x{:08X} action_ps=0x{:08X} "
          "action_frame={} action_draw={} rt=0x{:08X} ds=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X}",
          ++g_direct_renderer.diagnostics.logged_private_rect_backing_aliases,
          g_direct_renderer.current_frame_index, slot,
          DirectPrivateTextureCopyActionMatchName(action_match), guest,
          action->source_texture, action->source_base_address,
          action->source_width, action->source_height, action->source_format,
          backing->guest_address, PlumeGuestTextureWidth(*backing),
          PlumeGuestTextureHeight(*backing),
          static_cast<uint32_t>(PlumeGuestTextureFormat(*backing)),
          FablePrivateTextureCopyKindName(action->kind),
          FableRegisteredSampleSourceKindName(action->sampled_source.kind),
          action->sampled_source.resource,
          static_cast<unsigned long long>(action->serial),
          action->target_surface, action->render_target, action->depth_stencil,
          action->vertex_shader, action->pixel_shader, action->frame_index,
          static_cast<unsigned long long>(action->draw_serial),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    RememberDirectMutablePrivateHeaderExactSampleLocked(
        command, slot, *action, *backing, "private-copy-backing");
    RememberDirectPrivateTextureCopyActionSampleLocked(action_sample_cache_key,
                                                       *action, backing);
    return backing;
  }

  if (synthetic_private_rect_copy &&
      action->kind != FablePrivateTextureCopyKind::kDepth &&
      action->backing_texture == 0) {
    RememberDirectPrivateTextureCopyActionSampleLocked(action_sample_cache_key,
                                                       *action, nullptr);
    return nullptr;
  }

  const bool mutable_title_private_color_action =
      action->kind != FablePrivateTextureCopyKind::kDepth &&
      action->source_base_address != 0 &&
      IsFablePrivateTextureChainExtent(action->source_width,
                                       action->source_height) &&
      (IsFableTitlePrivateTextureProxyAddress(action->source_texture) ||
       IsFableTitlePrivateTextureProxyAddress(action->source_base_address) ||
       IsFableTitlePrivateTextureProxyAddress(guest) ||
       (texture && IsFablePrivateTextureChainResource(*texture)));
  if (mutable_title_private_color_action) {
    if (PlumeTrackedResource* uploaded_source =
            FindDirectPrivateRectUploadedActionSourceLocked(command, slot,
                                                            *action, texture)) {
      RememberDirectPrivateTextureSiblingAliasAddressLocked(guest,
                                                           *uploaded_source);
      if (texture) {
        RememberDirectPrivateTextureSiblingAliasLocked(*texture,
                                                      *uploaded_source);
      }
      RememberDirectPrivateTextureAliasIndexLocked(
          guest,
          action->source_base_address != 0
              ? action->source_base_address
              : (texture ? PlumeGuestTextureBaseAddress(*texture) : 0u),
          HasTrackedFetchDwords(action->source_fetch_dwords)
              ? action->source_fetch_dwords
              : DirectCommandTextureFetchDwords(command, slot),
          *uploaded_source);
      RememberDirectMutablePrivateHeaderExactSampleLocked(
          command, slot, *action, *uploaded_source,
          "private-copy-uploaded-source");
      RememberDirectPrivateTextureCopyActionSampleLocked(
          action_sample_cache_key, *action, uploaded_source);
      return uploaded_source;
    }
  }

  PlumeTrackedResource* target = FindDirectResourceLocked(action->target_surface);
  if (!target || !PlumeGuestImageReady(*target)) {
    RememberDirectPrivateTextureCopyActionSampleLocked(action_sample_cache_key,
                                                       *action, nullptr);
    return nullptr;
  }

  const bool writes_same_target =
      DirectCommandBindsResourceAsTargetLocked(command, *target);
  const bool depth_action_reads_bound_depth =
      action->kind == FablePrivateTextureCopyKind::kDepth &&
      target->texture_depth_target &&
      DirectResourceAddressMatchesLocked(target->guest_address,
                                         command.resources.depth_stencil);
  if (!FableTrackedResourceHasGpuProducedContent(*target)) {
    if (depth_action_reads_bound_depth &&
        DirectSyntheticPrivateRectDepthActionCanExecuteLocked(command, slot,
                                                              texture)) {
      static uint32_t logged_private_rect_first_use_depth_aliases = 0;
      if (logged_private_rect_first_use_depth_aliases < 32u) {
        REXLOG_INFO(
            "Refii plume private copy first-use depth alias #{} frame={} "
            "slot={} guest=0x{:08X} source=0x{:08X} target=0x{:08X}/{}x{} "
            "kind={} serial={} action_frame={} action_draw={} rt=0x{:08X} "
            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_private_rect_first_use_depth_aliases,
            g_direct_renderer.current_frame_index, slot, guest,
            action->source_texture, target->guest_address,
            PlumeGuestTextureWidth(*target), PlumeGuestTextureHeight(*target),
            FablePrivateTextureCopyKindName(action->kind),
            static_cast<unsigned long long>(action->serial),
            action->frame_index,
            static_cast<unsigned long long>(action->draw_serial),
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
      RememberDirectMutablePrivateHeaderExactSampleLocked(
          command, slot, *action, *target, "private-copy-first-use-depth");
      RememberDirectPrivateTextureCopyActionSampleLocked(action_sample_cache_key,
                                                         *action, target);
      return target;
    }
    RememberDirectPrivateTextureCopyActionSampleLocked(action_sample_cache_key,
                                                       *action, nullptr);
    return nullptr;
  }
  if (writes_same_target && !depth_action_reads_bound_depth) {
    if (g_direct_renderer.diagnostics.logged_private_rect_current_target_owners <
        32u) {
      REXLOG_INFO(
        "Refii plume exact private/title current-target owner #{} "
          "frame={} match={} slot={} guest=0x{:08X} source=0x{:08X} "
          "target=0x{:08X} kind={} content={} action_rt=0x{:08X} "
          "action_ds=0x{:08X} action_vs=0x{:08X} action_ps=0x{:08X} "
          "action_vs_root=0x{:08X}/{} action_ps_root=0x{:08X}/{} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
          "cmd_vs_root=0x{:08X}/{} cmd_ps_root=0x{:08X}/{}",
          ++g_direct_renderer.diagnostics.logged_private_rect_current_target_owners,
          g_direct_renderer.current_frame_index,
          DirectPrivateTextureCopyActionMatchName(action_match), slot, guest,
          action->source_texture, action->target_surface,
          FablePrivateTextureCopyKindName(action->kind),
          static_cast<unsigned long long>(
              FableTrackedResourceVisibleContentSerial(*target)),
          action->render_target, action->depth_stencil, action->vertex_shader,
          action->pixel_shader, action->vertex_shader_root_symbol_address,
          static_cast<unsigned long long>(
              action->vertex_shader_root_call_serial),
          action->pixel_shader_root_symbol_address,
          static_cast<unsigned long long>(action->pixel_shader_root_call_serial),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader,
          command.resources.shader_roots.vertex_shader.symbol_address,
          static_cast<unsigned long long>(
              command.resources.shader_roots.vertex_shader.call_serial),
          command.resources.shader_roots.pixel_shader.symbol_address,
          static_cast<unsigned long long>(
              command.resources.shader_roots.pixel_shader.call_serial));
    }
    RememberDirectPrivateTextureCopyActionSampleLocked(action_sample_cache_key,
                                                       *action, nullptr);
    return nullptr;
  }

  if (g_direct_renderer.diagnostics.logged_private_rect_produced_aliases < 32u) {
    REXLOG_INFO(
        "Refii plume private copy action alias #{} frame={} slot={} "
        "match={} guest=0x{:08X} source=0x{:08X} base=0x{:08X} {}x{} fmt={} "
        "target=0x{:08X}/{}x{} target_fmt={} kind={} sampled={} "
        "sample=0x{:08X} serial={} "
        "action_rt=0x{:08X} action_ds=0x{:08X} "
        "action_vs=0x{:08X} action_ps=0x{:08X} action_frame={} "
        "action_draw={} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
        ++g_direct_renderer.diagnostics.logged_private_rect_produced_aliases,
        g_direct_renderer.current_frame_index, slot,
        DirectPrivateTextureCopyActionMatchName(action_match), guest,
        action->source_texture, action->source_base_address,
        action->source_width, action->source_height, action->source_format,
        target->guest_address, PlumeGuestTextureWidth(*target),
        PlumeGuestTextureHeight(*target),
        static_cast<uint32_t>(PlumeGuestTextureFormat(*target)),
        FablePrivateTextureCopyKindName(action->kind),
        FableRegisteredSampleSourceKindName(action->sampled_source.kind),
        action->sampled_source.resource,
        static_cast<unsigned long long>(action->serial),
        action->render_target, action->depth_stencil, action->vertex_shader,
        action->pixel_shader, action->frame_index,
        static_cast<unsigned long long>(action->draw_serial),
        command.resources.render_targets[0], command.resources.depth_stencil,
        command.resources.vertex_shader, command.resources.pixel_shader);
  }
  RememberDirectMutablePrivateHeaderExactSampleLocked(
      command, slot, *action, *target, "private-copy-target");
  RememberDirectPrivateTextureCopyActionSampleLocked(action_sample_cache_key,
                                                     *action, target);
  return target;
}

// ZERO-CALLER FUNCTION ResolveDirectNativeDepthFirstUseSampleLocked —
// DELETED 2026-07-26. It resolved a native-depth first-use sample by walking
// FableSurfaceTextureLinks for a trusted kDepth copy edge off the bound depth
// target, then logged the hit. Verified zero-caller across the WHOLE repo
// (src/, tools/, both rexglue shadows, headers included) before deletion: the
// only two occurrences were its forward declaration and its definition, both
// in this file, and both are removed. It has internal linkage (anonymous
// namespace) so there is no cross-TU call path either.
bool IsFableFinalTitlePrivateIndirectionCommandLocked(
    const FableDirectRenderCommand& command) {
  const uint64_t pixel_hash =
      DirectPixelShaderHashLocked(command.resources.pixel_shader);
  const uint64_t vertex_hash =
      DirectVertexShaderHashLocked(command.resources.vertex_shader);
  // DERIVED (census: final_title_indirection_ps / _vs). The hardcoded guest
  // object-pointer literals (ps=0x421615F0/0x42161780/0x42178730,
  // vs=0x4C2BA090/A310/A410/0x4C2BB320) were DELETED 2026-07-26: run 288
  // measured tested=525171 matched=5049 derived=5049 lit_only=0 on BOTH gates,
  // i.e. the shader-bytecode hash alternative caught 100% of matches and the
  // address literals never decided anything on their own. The hashes are
  // content-derived and survive a different heap layout, which the pointers
  // never could. Census calls are KEPT so any regression shows up as a drop
  // from 5049 on the next run.
  const bool final_title_pixel_shader_hash =
      pixel_hash == kFableParticleMaterialAnim1DepthPixelHash ||
      pixel_hash == kFableLandscapeMaterialBlt2Glt1Mist1Amb1Clf1PixelHash ||
      pixel_hash == kFableLandscapeMaterialBlt7Glt1Mist0Amb1Clf1PixelHash;
  const bool final_title_pixel_shader = DirectAddressGateTest(
      DirectAddressGate::kFinalTitleIndirectionPixelShader,
      final_title_pixel_shader_hash, command.resources.pixel_shader, pixel_hash,
      final_title_pixel_shader_hash);
  const bool final_title_vertex_shader_hash =
      vertex_hash == kFableRepeatedMeshesAlphaNoStippleVertexHash;
  const bool final_title_vertex_shader = DirectAddressGateTest(
      DirectAddressGate::kFinalTitleIndirectionVertexShader,
      final_title_vertex_shader_hash, command.resources.vertex_shader,
      vertex_hash, final_title_vertex_shader_hash);
  // 0x4011F370 is kFableTitleVisibleRenderTarget — substituted for the named
  // constant (identical value, no behaviour change).
  return command.draw.source == RendererDrawEventSource::kD3DWrapper &&
         DirectAddressGateTest(
             DirectAddressGate::kFinalTitleIndirectionRenderTarget,
             command.resources.render_targets[0] ==
                 kFableTitleVisibleRenderTarget,
             command.resources.render_targets[0]) &&
         DirectVertexDeclarationMatchesHashLocked(
             command.resources.vertex_declaration,
             kFableRepeatedMeshDeclarationHash) &&
         final_title_pixel_shader && final_title_vertex_shader;
}

#endif

}  // namespace refii::renderer
