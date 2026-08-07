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

DirectPresentSource ResolveDirectPresentSourceLocked(
    const FableDirectStateSnapshot& frame_snapshot) {
  const auto valid_source = [](const DirectPresentSource& candidate) {
    return candidate.texture && candidate.view && candidate.write_serial != 0;
  };
  // STATED present source (run 199, audit fix). The guest Present
  // (sub_82B6F1D0) fires per guest frame and resolves render_targets[0] into
  // g_FrontBufferTexture, then swaps it — so RT0 IS the finished frame the game
  // wants displayed. Presenting it directly replaces the candidate-ranking +
  // starvation heuristic that showed partial host-pump composes (the
  // fragmentation) and latched a stale serial (the title freeze). Authoritative
  // only when it resolves to a ready host texture; early-startup phases (before
  // the first guest Present) fall through to the heuristic below. Gated so the
  // old heuristic can be A/B'd against the Xenia reference.
  if (RendererGtlStatedPresentSourceEnabled()) {
    const refii::gtl::StatedPresentSource stated =
        refii::gtl::GetStatedPresentSource();
    if (stated.rt0 != 0) {
      DirectPresentSource stated_source =
          PresentSourceFromResourceLocked(stated.rt0);
      if (valid_source(stated_source)) {
        stated_source.reason = "stated_present_rt0";
        static uint32_t logged_stated_present = 0;
        if (++logged_stated_present <= 16u ||
            (logged_stated_present % 256u) == 0u) {
          REXLOG_INFO(
              "Refii plume present STATED source rt0=0x{:08X} {}x{} serial={} "
              "epoch={}",
              stated.rt0, stated_source.width, stated_source.height,
              static_cast<unsigned long long>(stated_source.write_serial),
              static_cast<unsigned long long>(stated.epoch));
        }
        return stated_source;
      }
    }
  }
  const bool startup_handoff_complete =
      FableStartupVideoPumpCompletedForHandoff();
  DirectPresentSource current_frame_color_source;
  DirectPresentSource current_frame_startup_clear_source;
  DirectPresentSource current_frame_non_clear_color_source;
  DirectPresentSource current_frame_title_visible_source;
  DirectPresentSource current_frame_surface_destination_source =
      DirectPresentSourceFromCurrentFrameSurfaceDestinationLocked();
  DirectPresentSource current_frame_title_draw_source =
      DirectPresentSourceFromCurrentFrameTitleDrawsLocked();
  if (startup_handoff_complete &&
      valid_source(current_frame_surface_destination_source)) {
    const std::string_view reason(
        current_frame_surface_destination_source.reason);
    if (reason == "current_frame_surface_destination_texture") {
      current_frame_surface_destination_source.reason =
          "post_startup_current_surface_destination_texture";
    } else if (reason == "current_frame_surface_destination_source") {
      current_frame_surface_destination_source.reason =
          "post_startup_current_surface_destination_source";
    }
  }
  if (startup_handoff_complete && valid_source(current_frame_title_draw_source)) {
    current_frame_title_draw_source.reason = "post_startup_current_title";
  }
  DirectPresentSource current_frame_video_source;
  for (uint32_t target : frame_snapshot.targets.color) {
    DirectPresentSource candidate = PresentSourceFromResourceLocked(target);
    if (!valid_source(candidate) ||
        candidate.frame_index != g_direct_renderer.current_frame_index ||
        !IsFableSceneTargetCandidate(candidate.target, candidate.width,
                                     candidate.height)) {
      continue;
    }
    // Before the native startup-video owner begins drawing, Fable submits a
    // display-sized title target containing only its scene clear. That clear
    // is the complete guest-produced frame for this phase, not starvation.
    // Keep it as an explicit source; a current/recent video draw still wins
    // below as soon as the movie composite exists.
    if (!startup_handoff_complete &&
        !valid_source(current_frame_startup_clear_source)) {
      candidate.reason = "startup_current_scene_clear";
      current_frame_startup_clear_source = candidate;
    }
    if (!valid_source(current_frame_non_clear_color_source) &&
        DirectPresentSourceHasSameFrameTitlePresentProducerLocked(candidate)) {
      candidate.reason = startup_handoff_complete
                             ? "post_startup_current_title"
                             : "current_frame_title_present_target";
      current_frame_non_clear_color_source = candidate;
    }
    if (!valid_source(current_frame_title_visible_source) &&
        DirectPresentSourceHasSameFrameTitleVisibleProducerLocked(candidate)) {
      candidate.reason = startup_handoff_complete
                             ? "post_startup_current_title"
                             : "current_frame_title_visible_scene";
      current_frame_title_visible_source = candidate;
    }
    if (!DirectPresentSourceHasSameFrameFullSceneProducerLocked(candidate)) {
      continue;
    }
    candidate.reason = "current_frame_scene_target";
    current_frame_color_source = candidate;
    break;
  }
  DirectPresentSource fable_scene_source;
  if (IsFableSceneTargetCandidate(g_direct_renderer.fable_scene_target,
                                  g_direct_renderer.fable_scene_width,
                                  g_direct_renderer.fable_scene_height)) {
    fable_scene_source =
        PresentSourceFromResourceLocked(g_direct_renderer.fable_scene_target);
    if (valid_source(fable_scene_source) &&
        IsFableSceneTargetCandidate(fable_scene_source.target,
                                    fable_scene_source.width,
                                    fable_scene_source.height)) {
      fable_scene_source.reason = "fable_scene_target";
    } else {
      fable_scene_source = {};
    }
  }
  DirectPresentSource same_frame_scene_source;
  if (valid_source(current_frame_color_source) &&
      current_frame_color_source.frame_index ==
          g_direct_renderer.current_frame_index &&
      IsFableSceneTargetCandidate(current_frame_color_source.target,
                                  current_frame_color_source.width,
                                  current_frame_color_source.height) &&
      DirectPresentSourceHasSameFrameFullSceneProducerLocked(
          current_frame_color_source)) {
    same_frame_scene_source = current_frame_color_source;
  }
  const auto try_current_scene_target =
      [&](uint32_t target, uint32_t frame_index, const char* reason) {
        if (valid_source(same_frame_scene_source) ||
            frame_index != g_direct_renderer.current_frame_index ||
            target == 0) {
          return;
        }
        DirectPresentSource candidate = PresentSourceFromResourceLocked(target);
        if (!valid_source(candidate) ||
            candidate.frame_index != g_direct_renderer.current_frame_index ||
            !IsFableSceneTargetCandidate(candidate.target, candidate.width,
                                         candidate.height)) {
          return;
        }
        const bool full_scene_producer =
            DirectPresentSourceHasSameFrameFullSceneProducerLocked(candidate);
        const bool title_visible_producer =
            DirectPresentSourceHasSameFrameTitleVisibleProducerLocked(candidate);
        const bool title_present_producer =
            DirectPresentSourceHasSameFrameTitlePresentProducerLocked(candidate);
        if (!full_scene_producer && !title_visible_producer &&
            !title_present_producer) {
          return;
        }
        candidate.reason = reason;
        same_frame_scene_source = candidate;
      };
  try_current_scene_target(g_direct_renderer.last_submitted_scene_target,
                           g_direct_renderer.last_submitted_scene_frame_index,
                           "current_submitted_scene_target");
  try_current_scene_target(g_direct_renderer.last_scene_target,
                           g_direct_renderer.last_scene_frame_index,
                           "current_tracked_scene_target");
  // The guest draws movies at ~25fps while the present pump ticks at display
  // rate, so requiring the video draw to land in this exact pump frame makes
  // the title branches win most ticks and the screen shows the (black) title
  // intermediates instead of the movie composite. Treat a display-sized video
  // target as current while its draws are at most a few pump frames old.
  // ~half a second at the 60Hz pump: movie decode gaps (state waits, loops)
  // regularly exceed a few frames and flapping to the black title composite
  // mid-movie reads as a broken video; a real movie end hands over after
  // this window.
  constexpr uint32_t kVideoPresentRecencyFrames = 30u;
  const bool recent_video_draw =
      g_direct_renderer.last_pix_video_target != 0 &&
      g_direct_renderer.current_frame_index >=
          g_direct_renderer.last_pix_video_frame &&
      g_direct_renderer.current_frame_index -
              g_direct_renderer.last_pix_video_frame <=
          kVideoPresentRecencyFrames;
  if ((g_direct_renderer.current_frame_has_pix_video_draw ||
       recent_video_draw) &&
      g_direct_renderer.last_pix_video_target != 0) {
    DirectPresentSource candidate =
        PresentSourceFromResourceLocked(g_direct_renderer.last_pix_video_target);
    FableVideoPresentCandidateFacts video_present_facts;
    video_present_facts.current_frame_has_video_draw =
        g_direct_renderer.current_frame_has_pix_video_draw ||
        recent_video_draw;
    video_present_facts.source_ready = valid_source(candidate);
    video_present_facts.frame_matches =
        candidate.frame_index == g_direct_renderer.current_frame_index ||
        (candidate.frame_index <= g_direct_renderer.current_frame_index &&
         g_direct_renderer.current_frame_index - candidate.frame_index <=
             kVideoPresentRecencyFrames);
    // Pix/Bink video also renders small helper targets (480x270 previews,
    // 960x540 legal-screen insets); presenting one fullscreen replaces the
    // composite the guest actually shows. Only display-sized video targets
    // may present.
    video_present_facts.present_target_candidate =
        IsFablePresentTargetCandidate(candidate.target, candidate.width,
                                      candidate.height) &&
        candidate.width >= FableGuestDisplayWidth() &&
        candidate.height >= FableGuestDisplayHeight();
    video_present_facts.video_target = g_direct_renderer.last_pix_video_target;
    // Prefer the post-draw snapshot: the live composite is cleared by
    // interleaved title work between movie draws, so sampling it at pump
    // rate shows black on most ticks.
    // Design present model: the video presents at its NATIVE rate while Plume
    // renders at the requested rate — between guest video composites (which the
    // native startup video runs in one blocking pass, so plume frames advance
    // far past the last composite's frame index), Plume REPEATS the last video
    // frame via this hold. So while the startup video pump is ACTIVE, the hold
    // stays valid regardless of the recency window (the recency cap is only to
    // release the hold AFTER the video ends so it never latches over the title).
    const bool hold_matches =
        g_direct_renderer.present.video_hold_texture &&
        g_direct_renderer.present.video_hold_view &&
        g_direct_renderer.present.video_hold_target ==
            g_direct_renderer.last_pix_video_target &&
        g_direct_renderer.present.video_hold_width >=
            FableGuestDisplayWidth() &&
        g_direct_renderer.present.video_hold_height >=
            FableGuestDisplayHeight() &&
        g_direct_renderer.current_frame_index >=
            g_direct_renderer.present.video_hold_frame &&
        (FableStartupVideoPumpActive() ||
         g_direct_renderer.current_frame_index -
                 g_direct_renderer.present.video_hold_frame <=
             kVideoPresentRecencyFrames);
    // Console contract (IDA sub_82A41F10): decode -> plane bind -> ONE
    // compose draw -> advance, all inline — a composite that received THIS
    // frame's video draw is complete and presents as-is (video is an
    // ordinary draw). The hold snapshot only covers pump ticks BETWEEN
    // guest draws (interleaved title clears); letting it outrank a
    // live-drawn composite replayed stale pre-draw states over good frames
    // (the Lionhead white/green flashing, run 084).
    const bool live_drawn_this_frame =
        g_direct_renderer.current_frame_has_pix_video_draw &&
        FableVideoCurrentFramePresentCandidateReady(video_present_facts);
    if (live_drawn_this_frame) {
      candidate.reason = "current_frame_video_target";
      current_frame_video_source = candidate;
    } else if (hold_matches) {
      DirectPresentSource hold_source;
      hold_source.texture = g_direct_renderer.present.video_hold_texture.get();
      hold_source.view = g_direct_renderer.present.video_hold_view.get();
      hold_source.target = g_direct_renderer.present.video_hold_target;
      hold_source.width = g_direct_renderer.present.video_hold_width;
      hold_source.height = g_direct_renderer.present.video_hold_height;
      hold_source.texture_width = g_direct_renderer.present.video_hold_width;
      hold_source.texture_height = g_direct_renderer.present.video_hold_height;
      hold_source.frame_index = g_direct_renderer.present.video_hold_frame;
      hold_source.write_serial = g_direct_renderer.present.video_hold_serial;
      hold_source.format = g_direct_renderer.present.video_hold_format;
      hold_source.reason = "current_frame_video_hold";
      current_frame_video_source = hold_source;
    } else if (FableVideoCurrentFramePresentCandidateReady(video_present_facts)) {
      candidate.reason = "current_frame_video_target";
      current_frame_video_source = candidate;
    } else {
      static uint64_t logged_video_present_rejects = 0;
      const uint64_t reject_count = ++logged_video_present_rejects;
      if (reject_count <= 16u ||
          (reject_count & (reject_count - 1u)) == 0u) {
        REXLOG_INFO(
            "Refii plume direct renderer video present candidate reject #{} "
            "frame={} video_target=0x{:08X} video_frame={} has_draw={} "
            "recent={} source_ready={} frame_matches={} target_candidate={} "
            "candidate_frame={} candidate=0x{:08X}/{}x{} serial={}",
            reject_count, g_direct_renderer.current_frame_index,
            g_direct_renderer.last_pix_video_target,
            g_direct_renderer.last_pix_video_frame,
            g_direct_renderer.current_frame_has_pix_video_draw ? 1 : 0,
            recent_video_draw ? 1 : 0,
            video_present_facts.source_ready ? 1 : 0,
            video_present_facts.frame_matches ? 1 : 0,
            video_present_facts.present_target_candidate ? 1 : 0,
            candidate.frame_index, candidate.target, candidate.width,
            candidate.height,
            static_cast<unsigned long long>(candidate.write_serial));
      }
    }
  }
  // Native-rate video hold: on plume frames with no fresh video candidate (most
  // ticks while the video plays), the candidate loop above leaves current_frame_
  // video_source unset. While the startup video pump is ACTIVE, seed it from the
  // held last video frame so Plume keeps presenting the video (repeated at its
  // rate) instead of falling through to the blank startup scene-clear. Released
  // automatically when the pump completes (the video source is only used under
  // the pump-active guard below).
  if (FableStartupVideoPumpActive() &&
      !valid_source(current_frame_video_source) &&
      g_direct_renderer.present.video_hold_texture &&
      g_direct_renderer.present.video_hold_view &&
      g_direct_renderer.present.video_hold_width >=
          FableGuestDisplayWidth() &&
      g_direct_renderer.present.video_hold_height >=
          FableGuestDisplayHeight()) {
    DirectPresentSource hold_source;
    hold_source.texture = g_direct_renderer.present.video_hold_texture.get();
    hold_source.view = g_direct_renderer.present.video_hold_view.get();
    hold_source.target = g_direct_renderer.present.video_hold_target;
    hold_source.width = g_direct_renderer.present.video_hold_width;
    hold_source.height = g_direct_renderer.present.video_hold_height;
    hold_source.texture_width = g_direct_renderer.present.video_hold_width;
    hold_source.texture_height = g_direct_renderer.present.video_hold_height;
    hold_source.frame_index = g_direct_renderer.present.video_hold_frame;
    hold_source.write_serial = g_direct_renderer.present.video_hold_serial;
    hold_source.format = g_direct_renderer.present.video_hold_format;
    hold_source.reason = "startup_video_hold_active";
    current_frame_video_source = hold_source;
  }
  const bool fable_scene_has_same_frame_full_scene_producer =
      valid_source(fable_scene_source) &&
      fable_scene_source.frame_index == g_direct_renderer.current_frame_index &&
      DirectPresentSourceHasSameFrameFullSceneProducerLocked(
          fable_scene_source);
  const bool same_frame_scene_handoff_ready =
      valid_source(current_frame_surface_destination_source) ||
      valid_source(current_frame_title_draw_source) ||
      valid_source(same_frame_scene_source) ||
      valid_source(current_frame_title_visible_source) ||
      fable_scene_has_same_frame_full_scene_producer ||
      valid_source(current_frame_non_clear_color_source) ||
      valid_source(current_frame_color_source);

  // While the startup video pump is active, present the video source (the held
  // last video frame) even on plume frames with no fresh video draw — Plume
  // renders faster than the video composites, so most ticks have no draw; the
  // hold repeats the last frame (native-rate video, requested-rate present).
  if ((g_direct_renderer.current_frame_has_pix_video_draw ||
       recent_video_draw || FableStartupVideoPumpActive()) &&
      valid_source(current_frame_video_source)) {
    return current_frame_video_source;
  }

  if (!startup_handoff_complete &&
      valid_source(current_frame_startup_clear_source)) {
    return current_frame_startup_clear_source;
  }

  // A frame whose only video draws hit sub-display helper targets (rejected
  // above) must still present through the regular sources; returning empty
  // here freezes the swapchain on the last image (black at boot).
  if ((startup_handoff_complete || same_frame_scene_handoff_ready)) {
    if (valid_source(current_frame_surface_destination_source)) {
      return current_frame_surface_destination_source;
    }
    if (valid_source(current_frame_title_visible_source)) {
      return current_frame_title_visible_source;
    }
    if (valid_source(current_frame_title_draw_source)) {
      return current_frame_title_draw_source;
    }
    if (valid_source(same_frame_scene_source)) {
      return same_frame_scene_source;
    }
    if (fable_scene_has_same_frame_full_scene_producer) {
      return fable_scene_source;
    }
    if (valid_source(current_frame_non_clear_color_source)) {
      return current_frame_non_clear_color_source;
    }
    if (valid_source(current_frame_color_source)) {
      return current_frame_color_source;
    }
  }
  // The guest DisplaySwap object bootstraps presentation before Plume has a
  // completed frame. Once a Plume frame has been presented, a pump tick with
  // no guest draw must scan out that completed frame again. Falling back to
  // the swap object here alternated 1920x1080 scene output with the stale
  // 1280x720 guest object on successive display ticks.
  if (g_direct_renderer.stats.last_present_target == 0 &&
      !g_direct_renderer.present.hold_texture) {
    for (const uint32_t swap_address :
         {g_direct_renderer.guest_swap_frontbuffer_base,
          g_direct_renderer.guest_swap_frontbuffer_object}) {
      if (swap_address == 0) {
        continue;
      }
      DirectPresentSource swap_source =
          PresentSourceFromResourceLocked(swap_address);
      if (valid_source(swap_source)) {
        swap_source.reason = "guest_display_swap_frontbuffer_bootstrap";
        static uint64_t logged_swap_frontbuffer_presents = 0;
        ++logged_swap_frontbuffer_presents;
        if (logged_swap_frontbuffer_presents <= 8u ||
            (logged_swap_frontbuffer_presents % 512u) == 0u) {
          REXLOG_INFO(
              "Refii plume present source guest swap bootstrap #{} frame={} "
              "target=0x{:08X} {}x{}",
              logged_swap_frontbuffer_presents,
              g_direct_renderer.current_frame_index, swap_source.target,
              swap_source.width, swap_source.height);
        }
        return swap_source;
      }
    }
  }
  return {};
}

void CountRefiiFrameActionPacketLocked(const RefiiFrameActionPacket& action) {
  g_direct_renderer.refii_packet_queue.replay.action_queue_depth =
      static_cast<uint32_t>(std::min<size_t>(
          g_direct_renderer.refii_packet_queue.actions.size(), UINT32_MAX));
  switch (action.type) {
    case RefiiFrameActionType::kDraw:
      ++g_direct_renderer.refii_packet_queue.replay.planned_action_packets;
      break;
    case RefiiFrameActionType::kClear:
      ++g_direct_renderer.refii_packet_queue.replay.planned_clear_packets;
      ++g_direct_renderer.refii_packet_queue.replay.planned_action_packets;
      break;
    case RefiiFrameActionType::kResolve:
      ++g_direct_renderer.refii_packet_queue.replay.planned_resolve_packets;
      ++g_direct_renderer.refii_packet_queue.replay.planned_action_packets;
      break;
    case RefiiFrameActionType::kSurfaceDestination:
      ++g_direct_renderer.refii_packet_queue.replay.planned_copy_packets;
      ++g_direct_renderer.refii_packet_queue.replay.planned_action_packets;
      break;
    case RefiiFrameActionType::kPresentBlit:
      ++g_direct_renderer.refii_packet_queue.replay.planned_present_packets;
      ++g_direct_renderer.refii_packet_queue.replay.planned_action_packets;
      break;
    case RefiiFrameActionType::kUnknown:
    default:
      ++g_direct_renderer.refii_packet_queue.replay.unsupported_packets;
      break;
  }
}

void RefreshRefiiPacketQueueReplayReadyLocked() {
  g_direct_renderer.refii_packet_queue.replay.action_queue_depth =
      static_cast<uint32_t>(std::min<size_t>(
          g_direct_renderer.refii_packet_queue.actions.size(), UINT32_MAX));
  g_direct_renderer.refii_packet_queue.replay_ready =
      !g_direct_renderer.refii_packet_queue.actions.empty() &&
      g_direct_renderer.refii_packet_queue.omitted_packets == 0 &&
      g_direct_renderer.refii_packet_queue.omitted_actions == 0 &&
      g_direct_renderer.refii_packet_queue.replay.unsupported_packets == 0;
}

void AppendRefiiFrameActionPacketLocked(RefiiFrameActionPacket action) {
  if (g_direct_renderer.refii_packet_queue.actions.size() >=
      kMaxFrameDatabaseCommandPackets) {
    ++g_direct_renderer.refii_packet_queue.omitted_actions;
    RefreshRefiiPacketQueueReplayReadyLocked();
    return;
  }
  g_direct_renderer.refii_packet_queue.actions.push_back(std::move(action));
  CountRefiiFrameActionPacketLocked(
      g_direct_renderer.refii_packet_queue.actions.back());
  RefreshRefiiPacketQueueReplayReadyLocked();
}

FableDirectFrameDatabaseCommandPacket BuildFrameDatabasePresentBlitCommandPacket(
    const DirectPresentSource& source, uint32_t output_width,
    uint32_t output_height) {
  return BuildFableDirectPresentBlitFrameDatabaseCommandPacket(
      {
          .source_target = source.target,
          .source_width = source.width,
          .source_height = source.height,
          .texture_width = source.texture_width,
          .texture_height = source.texture_height,
          .source_write_serial = source.write_serial,
          .source_frame = source.frame_index,
          .reason = source.reason,
      },
      output_width, output_height);
}

void AppendFrameDatabasePresentBlitCommandPacketLocked(
    FableDirectFrameDatabaseCommandPacket packet) {
  if (packet.present_blit.source_target == 0 ||
      packet.present_blit.output_width == 0 ||
      packet.present_blit.output_height == 0) {
    return;
  }
  if (g_direct_renderer.frame_database_command_packets.size() >=
      kMaxFrameDatabaseCommandPackets) {
    ++g_direct_renderer.frame_database_omitted_command_packets;
    return;
  }

  AppendRefiiFrameActionPacketLocked(
      BuildRefiiPresentBlitActionPacket(
          g_direct_renderer.refii_packet_queue.frame_id, packet));
  g_direct_renderer.frame_database_command_packets.push_back(std::move(packet));
}

void AppendFrameDatabasePresentBlitCommandPacketLocked(
    const DirectPresentSource& source, uint32_t output_width,
    uint32_t output_height) {
  AppendFrameDatabasePresentBlitCommandPacketLocked(
      BuildFrameDatabasePresentBlitCommandPacket(source, output_width,
                                                 output_height));
}

DirectPresentSource ResolveDirectPresentSourceFromRefiiPresentBlitPacketLocked(
    const RefiiPresentBlitPacket& packet) {
  DirectPresentSource resolved_source =
      PresentSourceFromResourceLocked(packet.source_target);
  DirectPresentSource source = resolved_source;
  if (!source.texture || !source.view) {
    return {};
  }
  source.target = packet.source_target;
  source.width = packet.source_width != 0 ? packet.source_width : source.width;
  source.height =
      packet.source_height != 0 ? packet.source_height : source.height;
  source.texture_width =
      packet.texture_width != 0 ? packet.texture_width : source.texture_width;
  source.texture_height =
      packet.texture_height != 0 ? packet.texture_height : source.texture_height;
  source.write_serial = packet.source_write_serial != 0
                            ? packet.source_write_serial
                            : source.write_serial;
  source.frame_index =
      packet.source_frame != 0 ? packet.source_frame : source.frame_index;
  source.reason = !packet.reason.empty()
                      ? packet.reason.c_str()
                      : "refii_present_blit";
  return source;
}

bool DirectTextureCopySizeMatches(const PlumeTrackedResource& source,
                                  const PlumeTrackedResource& destination) {
  return PlumeGuestTextureFormat(source) ==
             PlumeGuestTextureFormat(destination) &&
         PlumeGuestTextureWidth(source) == PlumeGuestTextureWidth(destination) &&
         PlumeGuestTextureHeight(source) ==
             PlumeGuestTextureHeight(destination) &&
         PlumeGuestTextureDepth(source) == PlumeGuestTextureDepth(destination);
}

bool DirectTextureCopyMipCountsMatch(const PlumeTrackedResource& source,
                                     const PlumeTrackedResource& destination) {
  return std::max<uint32_t>(source.texture_mip_levels, 1u) ==
         std::max<uint32_t>(destination.texture_mip_levels, 1u);
}

void MaybeBeginRenderDocSurfaceDestinationCaptureLocked(
    const char* copy_kind,
    const FableSurfaceDestinationTextureRelation& relation,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target) {
  static std::string requested_capture_target;
  const std::string capture_target = RenderDocDeepObserverCaptureDrawTarget();
  if (capture_target.empty()) {
    requested_capture_target.clear();
    return;
  }
  if (requested_capture_target == capture_target ||
      !IsRenderDocSurfaceDestinationCaptureTarget(capture_target)) {
    return;
  }
  const uint32_t target_width = PlumeGuestTextureWidth(target);
  const uint32_t target_height = PlumeGuestTextureHeight(target);
  const uint32_t source_width = PlumeGuestTextureWidth(source);
  const uint32_t source_height = PlumeGuestTextureHeight(source);
  if (BeginRenderDocImmediateFrameCapture(
          "Refii Plume SurfaceDestination Copy")) {
    requested_capture_target = capture_target;
    g_direct_renderer.stats.renderdoc_capture_started = true;
    REXLOG_INFO(
        "Refii plume RenderDoc surface-destination capture started "
        "target={} frame={} kind={} relation={} source=0x{:08X}/{}x{} "
        "destination=0x{:08X}/{}x{} texture_base=0x{:08X} "
        "backing=0x{:08X}/0x{:08X} trusted={} exact={}",
        capture_target, g_direct_renderer.current_frame_index,
        copy_kind != nullptr ? copy_kind : "unknown",
        static_cast<unsigned long long>(relation.serial),
        relation.source_surface, source_width, source_height,
        relation.destination_texture, target_width, target_height,
        relation.link.texture_base_address,
        relation.link.backing_texture, relation.link.backing_base_address,
        relation.link.trusted_destination_edge ? 1 : 0,
        relation.link.wrapper_source_exact ? 1 : 0);
  }
}

void MarkDirectTextureLayoutLocked(plume::RenderTexture* texture,
                                   plume::RenderTextureLayout layout) {
  if (!texture || layout == plume::RenderTextureLayout::UNKNOWN) {
    return;
  }
  g_direct_renderer.known_texture_layouts[texture] = layout;
}

void ForgetDirectTextureLayoutLocked(plume::RenderTexture* texture) {
  if (!texture) {
    return;
  }
  g_direct_renderer.known_texture_layouts.erase(texture);
}

bool RecordDirectTextureBarrierLocked(plume::RenderCommandList* command_list,
                                      plume::RenderBarrierStages stages,
                                      plume::RenderTexture* texture,
                                      plume::RenderTextureLayout layout) {
  if (!command_list || !texture ||
      layout == plume::RenderTextureLayout::UNKNOWN) {
    return false;
  }
  const auto known_it = g_direct_renderer.known_texture_layouts.find(texture);
  if (known_it != g_direct_renderer.known_texture_layouts.end() &&
      known_it->second == layout) {
    return false;
  }
  command_list->barriers(stages, plume::RenderTextureBarrier(texture, layout));
  MarkDirectTextureLayoutLocked(texture, layout);
  return true;
}

bool RecordDirectTextureBarrierForcedLocked(
    plume::RenderCommandList* command_list, plume::RenderBarrierStages stages,
    plume::RenderTexture* texture, plume::RenderTextureLayout layout) {
  if (!command_list || !texture ||
      layout == plume::RenderTextureLayout::UNKNOWN) {
    return false;
  }
  command_list->barriers(stages, plume::RenderTextureBarrier(texture, layout));
  MarkDirectTextureLayoutLocked(texture, layout);
  return true;
}

uint64_t DirectSurfaceAliasCopyCacheKey(uint32_t source_surface,
                                        uint32_t destination_surface) {
  return (uint64_t(source_surface) << 32) | uint64_t(destination_surface);
}

uint64_t DirectSurfaceAliasCopyCacheKey(
    const RendererSurfaceCopyAliasSnapshot& alias) {
  if (alias.copy_call_serial == 0 && alias.copy_context_key == 0 &&
      alias.copy_symbol_address == 0) {
    return DirectSurfaceAliasCopyCacheKey(alias.source_surface,
                                          alias.destination_surface);
  }

  uint64_t key = 0xCBF29CE484222325ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 0x100000001B3ull;
  };
  mix(alias.copy_call_serial);
  mix(alias.copy_context_key);
  mix(alias.copy_symbol_address);
  mix(alias.source_surface);
  mix(alias.destination_surface);
  return key;
}

uint64_t DirectSurfaceDestinationCopyFrameKey(
    const FableSurfaceDestinationTextureRelation& relation) {
  uint64_t key = 0xCBF29CE484222325ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 0x100000001B3ull;
  };
  mix(relation.serial);
  mix(relation.source_surface);
  mix(relation.destination_texture);
  mix(relation.source_slot);
  mix(relation.link.copy_call_serial);
  mix(relation.link.copy_context_key);
  mix(relation.link.copy_symbol_address);
  mix(relation.link.source_surface);
  mix(relation.link.texture);
  mix(relation.link.backing_texture);
  mix(relation.link.target_content_serial);
  mix(relation.link.backing_content_serial);
  return key != 0 ? key : 1ull;
}

bool DirectSurfaceDestinationCopyRecordedThisFrameLocked(
    const FableSurfaceDestinationTextureRelation& relation) {
  return g_direct_renderer.surface_destination_copy_frame_keys.find(
             DirectSurfaceDestinationCopyFrameKey(relation)) !=
         g_direct_renderer.surface_destination_copy_frame_keys.end();
}

void RememberDirectSurfaceDestinationCopyThisFrameLocked(
    const FableSurfaceDestinationTextureRelation& relation) {
  g_direct_renderer.surface_destination_copy_frame_keys.insert(
      DirectSurfaceDestinationCopyFrameKey(relation));
}

uint64_t DirectCoveredDuplicatePrivateRectSurfaceLinkKey(
    const FableSurfaceTextureLink& link) {
  uint64_t key = 0xCBF29CE484222325ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 0x100000001B3ull;
  };
  mix(link.source_slot);
  mix(link.texture);
  mix(link.source_surface);
  mix(link.copy_call_serial);
  mix(link.copy_context_key);
  mix(link.copy_symbol_address);
  return key;
}

bool DirectResourceIsPrivateFeedbackAlias(
    const PlumeTrackedResource& resource) {
  return resource.plume_owned &&
         (resource.guest_address & 0xFF000000u) == 0xF1000000u &&
         !resource.texture_render_target && !resource.texture_depth_target;
}

bool DirectPrivateFeedbackAliasContainsTargetContentLocked(
    const PlumeTrackedResource& alias, const PlumeTrackedResource& target) {
  if (alias.guest_address == 0 || target.guest_address == 0) {
    return true;
  }
  const bool strict_alias = DirectResourceIsPrivateFeedbackAlias(alias);
  const uint64_t target_visible_serial =
      FableTrackedResourceVisibleContentSerial(target);
  if (target_visible_serial == 0) {
    return !strict_alias;
  }
  const auto copied_it =
      g_direct_renderer.private_texture_feedback_alias_target_serial.find(
          alias.guest_address);
  if (copied_it ==
      g_direct_renderer.private_texture_feedback_alias_target_serial.end()) {
    return !strict_alias;
  }
  return copied_it->second == target_visible_serial;
}

bool DirectPrivateFeedbackAliasContainsTargetSerialLocked(
    const PlumeTrackedResource& alias, uint64_t target_content_serial) {
  if (!DirectResourceIsPrivateFeedbackAlias(alias) ||
      target_content_serial == 0) {
    return true;
  }
  const auto copied_it =
      g_direct_renderer.private_texture_feedback_alias_target_serial.find(
          alias.guest_address);
  return copied_it !=
             g_direct_renderer.private_texture_feedback_alias_target_serial.end() &&
         copied_it->second == target_content_serial;
}

bool DirectPrivateFeedbackAliasCanSampleTargetLocked(
    const PlumeTrackedResource& alias, const PlumeTrackedResource& source,
    const PlumeTrackedResource& target) {
  if (DirectTextureCopySizeMatches(target, alias)) {
    return true;
  }
  if (!DirectResourceIsPrivateFeedbackAlias(alias) ||
      !DirectPrivateFeedbackAliasContainsTargetContentLocked(alias, target)) {
    return false;
  }

  const uint32_t alias_width = PlumeGuestTextureWidth(alias);
  const uint32_t alias_height = PlumeGuestTextureHeight(alias);
  const uint32_t source_width = PlumeGuestTextureWidth(source);
  const uint32_t source_height = PlumeGuestTextureHeight(source);
  const uint32_t alias_depth = PlumeGuestTextureDepth(alias);
  const uint32_t source_depth = PlumeGuestTextureDepth(source);
  return alias_width != 0 && alias_height != 0 && source_width != 0 &&
         source_height != 0 && alias_width == source_width &&
         alias_height == source_height &&
         (alias_depth == 0 || source_depth == 0 || alias_depth == source_depth);
}

bool DirectPrivateTextureCopyActionBackingContainsTargetContentLocked(
    const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& alias, const PlumeTrackedResource& target) {
  const uint64_t target_visible_serial =
      FableTrackedResourceVisibleContentSerial(target);
  if (target_visible_serial == 0) {
    return true;
  }
  if (action.target_content_serial != target_visible_serial) {
    return false;
  }
  const bool action_targets_resource =
      action.target_surface == target.guest_address ||
      action.target_surface == target.parent_address ||
      action.render_target == target.guest_address ||
      action.render_target == target.parent_address;
  const bool action_uses_backing =
      action.backing_texture == alias.guest_address ||
      action.backing_texture == alias.parent_address ||
      (action.backing_base_address != 0 &&
       action.backing_base_address == PlumeGuestTextureBaseAddress(alias)) ||
      (HasTrackedFetchDwords(action.backing_fetch_dwords) &&
       FableDirectTextureFetchKey(action.backing_fetch_dwords) ==
           FableDirectTextureFetchKey(PlumeGuestTextureFetchDwords(alias)));
  return action_targets_resource && action_uses_backing;
}

void RetirePresentHoldAllocationLocked(
    std::unique_ptr<plume::RenderTexture>& texture,
    std::unique_ptr<plume::RenderTextureView>& view) {
  if (!texture && !view) {
    return;
  }
  ForgetDirectTextureLayoutLocked(texture.get());
  PlumeTrackedResource retired;
  retired.guest_texture.texture = std::move(texture);
  retired.guest_texture.view = std::move(view);
  RetirePlumeResourceLocked(std::move(retired),
                            g_direct_renderer.scene_write_serial);
}

void RecordVideoPresentHoldSnapshotLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    plume::RenderTexture* color_texture, uint32_t color_address,
    uint32_t width, uint32_t height, plume::RenderFormat color_format) {
  if (!device || !command_list || !color_texture || color_address == 0 ||
      width == 0 || height == 0) {
    return;
  }

  if (!g_direct_renderer.present.video_hold_texture ||
      g_direct_renderer.present.video_hold_width != width ||
      g_direct_renderer.present.video_hold_height != height ||
      g_direct_renderer.present.video_hold_format != color_format) {
    plume::RenderTextureDesc desc = plume::RenderTextureDesc::Texture2D(
        width, height, 1, color_format);
    RetirePresentHoldAllocationLocked(
        g_direct_renderer.present.video_hold_texture,
        g_direct_renderer.present.video_hold_view);
    g_direct_renderer.present.video_hold_texture = device->createTexture(desc);
    if (!g_direct_renderer.present.video_hold_texture) {
      return;
    }
    g_direct_renderer.known_texture_layouts.erase(
        g_direct_renderer.present.video_hold_texture.get());
    g_direct_renderer.present.video_hold_texture->setName(
        "Refii Fable video present hold");
    plume::RenderTextureViewDesc view_desc;
    view_desc.format = color_format;
    view_desc.dimension = plume::RenderTextureViewDimension::TEXTURE_2D;
    view_desc.componentMapping = IdentityTextureComponentMapping();
    g_direct_renderer.present.video_hold_view =
        g_direct_renderer.present.video_hold_texture->createTextureView(
            view_desc);
    if (!g_direct_renderer.present.video_hold_view) {
      g_direct_renderer.present.video_hold_texture.reset();
      return;
    }
    g_direct_renderer.present.video_hold_width = width;
    g_direct_renderer.present.video_hold_height = height;
    g_direct_renderer.present.video_hold_format = color_format;
  }

  RecordDirectTextureBarrierLocked(command_list,
                                   plume::RenderBarrierStage::COPY,
                                   color_texture,
                                   plume::RenderTextureLayout::COPY_SOURCE);
  RecordDirectTextureBarrierLocked(
      command_list, plume::RenderBarrierStage::COPY,
      g_direct_renderer.present.video_hold_texture.get(),
      plume::RenderTextureLayout::COPY_DEST);
  command_list->copyTexture(g_direct_renderer.present.video_hold_texture.get(),
                            color_texture);
  RecordDirectTextureBarrierLocked(
      command_list,
      plume::RenderBarrierStage::GRAPHICS | plume::RenderBarrierStage::COPY,
      g_direct_renderer.present.video_hold_texture.get(),
      plume::RenderTextureLayout::SHADER_READ);
  RecordDirectTextureBarrierLocked(
      command_list,
      plume::RenderBarrierStage::GRAPHICS | plume::RenderBarrierStage::COPY,
      color_texture, plume::RenderTextureLayout::COLOR_WRITE);

  g_direct_renderer.present.video_hold_target = color_address;
  {
    static uint64_t recorded_video_hold_copies = 0;
    const uint64_t copy_count = ++recorded_video_hold_copies;
    if (RendererDrawTracingEnabled() &&
        (copy_count <= 8u || (copy_count & (copy_count - 1u)) == 0u)) {
      REXLOG_INFO(
          "Refii plume video hold copy #{} frame={} target=0x{:08X} {}x{}",
          copy_count, g_direct_renderer.current_frame_index, color_address,
          width, height);
    }
  }
  g_direct_renderer.present.video_hold_frame =
      g_direct_renderer.current_frame_index;
  g_direct_renderer.present.video_hold_serial =
      ++g_direct_renderer.scene_write_serial;
}

void RecordGenericPresentHoldSnapshotLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    const DirectPresentSource& source) {
  if (!device || !command_list || !source.texture ||
      source.format == plume::RenderFormat::UNKNOWN || source.width < 960u ||
      source.height < 540u ||
      source.texture == g_direct_renderer.present.hold_texture.get()) {
    return;
  }
  if (!g_direct_renderer.present.hold_texture ||
      g_direct_renderer.present.hold_width != source.texture_width ||
      g_direct_renderer.present.hold_height != source.texture_height ||
      g_direct_renderer.present.hold_format != source.format) {
    plume::RenderTextureDesc desc = plume::RenderTextureDesc::Texture2D(
        source.texture_width, source.texture_height, 1, source.format);
    RetirePresentHoldAllocationLocked(g_direct_renderer.present.hold_texture,
                                      g_direct_renderer.present.hold_view);
    g_direct_renderer.present.hold_texture = device->createTexture(desc);
    if (!g_direct_renderer.present.hold_texture) {
      return;
    }
    g_direct_renderer.known_texture_layouts.erase(
        g_direct_renderer.present.hold_texture.get());
    g_direct_renderer.present.hold_texture->setName(
        "Refii Fable present hold");
    plume::RenderTextureViewDesc view_desc;
    view_desc.format = source.format;
    view_desc.dimension = plume::RenderTextureViewDimension::TEXTURE_2D;
    view_desc.componentMapping = IdentityTextureComponentMapping();
    g_direct_renderer.present.hold_view =
        g_direct_renderer.present.hold_texture->createTextureView(view_desc);
    if (!g_direct_renderer.present.hold_view) {
      g_direct_renderer.present.hold_texture.reset();
      return;
    }
    g_direct_renderer.present.hold_width = source.texture_width;
    g_direct_renderer.present.hold_height = source.texture_height;
    g_direct_renderer.present.hold_format = source.format;
  }
  RecordDirectTextureBarrierLocked(command_list,
                                   plume::RenderBarrierStage::COPY,
                                   source.texture,
                                   plume::RenderTextureLayout::COPY_SOURCE);
  RecordDirectTextureBarrierLocked(
      command_list, plume::RenderBarrierStage::COPY,
      g_direct_renderer.present.hold_texture.get(),
      plume::RenderTextureLayout::COPY_DEST);
  command_list->copyTexture(g_direct_renderer.present.hold_texture.get(),
                            source.texture);
  RecordDirectTextureBarrierLocked(
      command_list,
      plume::RenderBarrierStage::GRAPHICS | plume::RenderBarrierStage::COPY,
      g_direct_renderer.present.hold_texture.get(),
      plume::RenderTextureLayout::SHADER_READ);
  RecordDirectTextureBarrierLocked(
      command_list,
      plume::RenderBarrierStage::GRAPHICS | plume::RenderBarrierStage::COPY,
      source.texture, source.layout);
  g_direct_renderer.present.hold_target = source.target;
  g_direct_renderer.present.hold_frame = g_direct_renderer.current_frame_index;
  g_direct_renderer.present.hold_serial = source.write_serial;
}

bool DirectTextureCopyCanRecordLocked(plume::RenderCommandList* command_list,
                                      const PlumeTrackedResource& source,
                                      const PlumeTrackedResource& destination) {
  return command_list && PlumeGuestImageReady(source) &&
         PlumeGuestImageReady(destination) &&
         source.guest_texture.texture.get() !=
             destination.guest_texture.texture.get() &&
         DirectTextureCopySizeMatches(source, destination);
}

bool RecordDirectTextureCopyLocked(plume::RenderCommandList* command_list,
                                   PlumeTrackedResource& source,
                                   PlumeTrackedResource& destination) {
  if (!DirectTextureCopyCanRecordLocked(command_list, source, destination)) {
    return false;
  }
  RecordDirectTextureBarrierLocked(command_list, plume::RenderBarrierStage::COPY,
                                   source.guest_texture.texture.get(),
                                   plume::RenderTextureLayout::COPY_SOURCE);
  RecordDirectTextureBarrierLocked(command_list, plume::RenderBarrierStage::COPY,
                                   destination.guest_texture.texture.get(),
                                   plume::RenderTextureLayout::COPY_DEST);
  if (DirectTextureCopyMipCountsMatch(source, destination)) {
    command_list->copyTexture(destination.guest_texture.texture.get(),
                              source.guest_texture.texture.get());
  } else {
    const plume::RenderBox source_box(
        0, 0, static_cast<int32_t>(PlumeGuestTextureWidth(source)),
        static_cast<int32_t>(PlumeGuestTextureHeight(source)), 0,
        static_cast<int32_t>(PlumeGuestTextureDepth(source)));
    command_list->copyTextureRegion(
        plume::RenderTextureCopyLocation::Subresource(
            destination.guest_texture.texture.get(), 0),
        plume::RenderTextureCopyLocation::Subresource(
            source.guest_texture.texture.get(), 0),
        0, 0, 0, &source_box);
  }
  RecordDirectTextureBarrierLocked(
      command_list,
      plume::RenderBarrierStage::GRAPHICS | plume::RenderBarrierStage::COPY,
      destination.guest_texture.texture.get(),
      plume::RenderTextureLayout::SHADER_READ);
  RecordDirectTextureBarrierLocked(
      command_list,
      plume::RenderBarrierStage::GRAPHICS | plume::RenderBarrierStage::COPY,
      source.guest_texture.texture.get(),
      source.texture_render_target ? plume::RenderTextureLayout::COLOR_WRITE
                                   : plume::RenderTextureLayout::SHADER_READ);
  MarkDirectResourceGpuProducedContentLocked(
      destination.guest_address, ++g_direct_renderer.scene_write_serial, false,
      destination.guest_texture.texture.get());
  return true;
}

// Why a resolve host copy cannot record. Split by cause: a bare false cannot
// say whether the packet was unusable, the resources were absent, or the copy
// itself was refused, and those point at different fixes.

// LIVE §3b execution counters (kGtlResolve commands, ordered-merge interleave).
// Separate from the per-frame dry-run census stats so the census keeps
// predicting and the live numbers accumulate run-long.
GtlResolveHostCopyStats g_gtl_resolve_live_stats;
// A one-frame no-dst RETRY was tried and disproven here (run 549: 221 queued,
// 5 recovered, 71 exhausted) — the miss family is NOT a transient
// materialization window. The pooled title headers' host resources exist with
// a stale creation-time base, so the base-keyed hybrid lookup can never match
// after a re-point; the fix is refreshing the host resource's base identity
// from the packet's dest_fetch/address_generation, not waiting. That refresh
// lives in FindGtlResolveDestinationHybridLocked's miss tail; this counts it.
uint64_t g_gtl_resolve_repoint_refreshes = 0;
uint64_t g_gtl_resolve_repoint_recreates = 0;

// CUMULATIVE video-merge counters (renderer_gtl_video_draws). The per-frame
// merge counters are logged only on every 240th frame, and a sampled frame
// showing 0 has twice been read as "the path never fires" (run 511: the
// sampled frames all showed video_inserted=0 while the path inserted between
// samples). These accumulate run-long so one log line answers "did it happen".
GtlVideoMergeStats g_gtl_video_merge_stats;

// Clear ownership counters: the packet streams own all clears
// (renderer_gtl_clears_authoritative, default ON since run 559).
GtlClearMergeStats g_gtl_clear_merge_stats;

// Step-3 census, second cut: attribute the registry's ~28 lookups/run to
// their consumer sites before shrinking the fetch-binding-action machinery.
void CountFetchBindingActionSiteHit(const char* site, bool hit) {
  if (!hit) {
    return;
  }
  static std::mutex site_mutex;
  static std::map<std::string, uint64_t> site_hits;
  std::lock_guard<std::mutex> lock(site_mutex);
  const uint64_t count = ++site_hits[site];
  if (count <= 8u || (count % 256u) == 0u) {
    REXLOG_INFO("Refii fetch-binding-action site {} hit #{}", site,
                static_cast<unsigned long long>(count));
  }
}

// Destination identity, the hybrid rule (run 460: 98% deterministic, 2%
// refuse, 0 missing). dword[1] of the captured fetch constant IS the memory
// the resolve writes (IDA: D3DDevice_Resolve masks it into RB_COPY_DEST_BASE):
// nonzero => match host images memory-to-memory, r6 object breaks ties,
// ambiguity refuses. Zero (DS/EndTiling family — phys 0 is a legal 360 write
// target, zero is a placement, not missing data) => the r6 object is the
// stated identity. Both keys come from the wrapper call; nothing is guessed.
PlumeTrackedResource* FindGtlResolveDestinationHybridLocked(
    const refii::gtl::ResolvePacket& packet) {
  const uint32_t dest_base_from_fetch =
      packet.dest_fetch_valid
          ? FableDecodeTextureFetchAddressDword(packet.dest_fetch_dwords[1])
          : 0;
  if (dest_base_from_fetch == 0) {
    return FindDirectResourceByExactObjectLocked(packet.dest_texture);
  }
  PlumeTrackedResource* single = nullptr;
  PlumeTrackedResource* preferred = nullptr;
  uint32_t hits = 0;
  for (auto& [address, resource] : g_direct_renderer.resources) {
    (void)address;
    if (!IsImageResource(resource.kind) ||
        PlumeGuestTextureBaseAddress(resource) != dest_base_from_fetch) {
      continue;
    }
    ++hits;
    single = &resource;
    if (resource.guest_address == packet.dest_texture) {
      preferred = &resource;
    }
  }
  const auto log_miss_path = [&](const char* path, uint32_t extra) {
    static uint32_t logged_miss_paths = 0;
    if (logged_miss_paths < 24u) {
      REXLOG_INFO(
          "Refii GTL resolve dest-miss path #{} frame={} path={} hits={} "
          "extra=0x{:08X} obj=0x{:08X} base=0x{:08X}",
          ++logged_miss_paths, g_direct_renderer.current_frame_index, path,
          hits, extra, packet.dest_texture, dest_base_from_fetch);
    }
  };
  if (preferred != nullptr) {
    return preferred;
  }
  if (hits == 1u) {
    return single;
  }
  if (hits != 0u) {
    // Multiple owners, none the stated object — run-551 census: this is the
    // ONLY miss path (24/24, hits 2-3, pooled bases 0x12AE1000/0x12CFD000).
    // Do not refuse: the r6 object the wrapper stated is the owner by
    // design; fall through to the re-point refresh below, after which the
    // preferred branch resolves every later scan.
    log_miss_path("ambiguous", hits);
  }
  // Re-point refresh (runs 547-551 diagnosis): the r6 header was RE-POINTED
  // (XGOffsetBaseTextureAddress) after its host resource was created — the
  // resource exists under the object key with a stale creation-time base
  // while 2-3 OTHER stale claimants still carry the target base, and no
  // retry horizon fixes that (run 549: 5 of 221 recovered). The packet's
  // dest_fetch_dwords are the header's CURRENT words, read at the resolve
  // call — wrapper-stated identity. When the shape (dims+format) agrees,
  // re-point the host resource's guest addressing in place; a shape change
  // would need a re-create and refuses instead.
  if (!packet.dest_fetch_valid) {
    log_miss_path("no_fetch", 0);
    return nullptr;
  }
  PlumeTrackedResource* object_resource =
      FindDirectResourceByExactObjectLocked(packet.dest_texture);
  if (object_resource == nullptr || !IsImageResource(object_resource->kind)) {
    log_miss_path(object_resource == nullptr ? "no_object" : "not_image",
                  object_resource != nullptr
                      ? static_cast<uint32_t>(object_resource->kind)
                      : 0u);
    return nullptr;
  }
  const FableTextureFetchInfo repoint_info =
      FableTextureFetchInfoFromDwords(packet.dest_fetch_dwords);
  if (!repoint_info.valid ||
      repoint_info.base_address != dest_base_from_fetch) {
    log_miss_path(repoint_info.valid ? "base_disagree" : "decode_invalid",
                  repoint_info.base_address);
    return nullptr;
  }
  if (PlumeGuestTextureWidth(*object_resource) != repoint_info.width ||
      PlumeGuestTextureHeight(*object_resource) != repoint_info.height ||
      PlumeGuestTextureGuestFormat(*object_resource) != repoint_info.format) {
    // The header re-pointed with a SHAPE change (run 553: fmt 6->54/7, even
    // 256x256->280x180 — the run-437 pooled-scratch pattern). A metadata poke
    // cannot fix the host image desc; push a snapshot built from the packet's
    // own fetch words through the sync machinery, whose retire-on-shape-change
    // branch recreates the texture (content comes from this resolve).
    const uint32_t old_width = PlumeGuestTextureWidth(*object_resource);
    const uint32_t old_height = PlumeGuestTextureHeight(*object_resource);
    const uint32_t old_format = PlumeGuestTextureGuestFormat(*object_resource);
    FableGuestResourceSnapshot repoint_snapshot;
    repoint_snapshot.kind = FableGuestResourceKind::kTexture;
    repoint_snapshot.guest_address = packet.dest_texture;
    repoint_snapshot.parent_address = object_resource->parent_address != 0
                                          ? object_resource->parent_address
                                          : packet.dest_texture;
    repoint_snapshot.texture.object_address = packet.dest_texture;
    repoint_snapshot.texture.parent_address = repoint_snapshot.parent_address;
    repoint_snapshot.texture.base_address = repoint_info.base_address;
    repoint_snapshot.texture.byte_size = repoint_info.byte_size;
    repoint_snapshot.texture.raw_fetch_dwords = packet.dest_fetch_dwords;
    repoint_snapshot.serial = std::max<uint64_t>(object_resource->serial, 1u);
    if (!SyncTrackedResourceLocked(g_direct_renderer.device, repoint_snapshot,
                                   nullptr)) {
      static uint32_t logged_repoint_recreate_failures = 0;
      if (logged_repoint_recreate_failures < 8u) {
        REXLOG_INFO(
            "Refii GTL resolve re-point RECREATE FAILED #{} obj=0x{:08X} "
            "want={}x{} fmt={}",
            ++logged_repoint_recreate_failures, packet.dest_texture,
            repoint_info.width, repoint_info.height, repoint_info.format);
      }
      return nullptr;
    }
    object_resource =
        FindDirectResourceByExactObjectLocked(packet.dest_texture);
    if (object_resource == nullptr ||
        !IsImageResource(object_resource->kind)) {
      return nullptr;
    }
    ++g_gtl_resolve_repoint_recreates;
    static uint32_t logged_repoint_recreates = 0;
    if (logged_repoint_recreates < 16u) {
      REXLOG_INFO(
          "Refii GTL resolve re-point RECREATE #{} frame={} obj=0x{:08X} "
          "{}x{} fmt={} -> {}x{} fmt={} base=0x{:08X} gen={}",
          ++logged_repoint_recreates, g_direct_renderer.current_frame_index,
          packet.dest_texture, old_width, old_height, old_format,
          repoint_info.width, repoint_info.height, repoint_info.format,
          repoint_info.base_address,
          static_cast<unsigned long long>(packet.dest_address_generation));
    }
    return object_resource;
  }
  const uint32_t stale_base = PlumeGuestTextureBaseAddress(*object_resource);
  const uint32_t mip_base =
      FableDecodeTextureFetchAddressDword(packet.dest_fetch_dwords[5]);
  // Both metadata domains: accessors prefer the typed guest_texture snapshot
  // and fall back to the legacy texture_guest_* fields.
  object_resource->guest_texture.fetch_dwords = packet.dest_fetch_dwords;
  object_resource->guest_texture.base_address = repoint_info.base_address;
  object_resource->guest_texture.mip_address = mip_base;
  object_resource->texture_fetch_dwords = packet.dest_fetch_dwords;
  object_resource->texture_guest_base = repoint_info.base_address;
  object_resource->texture_guest_mips = mip_base;
  InvalidateDirectResourceMemoKeyLocked(stale_base);
  InvalidateDirectResourceMemosForResourceLocked(*object_resource);
  static uint32_t logged_repoint_refreshes = 0;
  ++g_gtl_resolve_repoint_refreshes;
  if (logged_repoint_refreshes < 16u) {
    REXLOG_INFO(
        "Refii GTL resolve re-point refresh #{} frame={} obj=0x{:08X} "
        "base 0x{:08X}->0x{:08X} gen={} {}x{} fmt={}",
        ++logged_repoint_refreshes, g_direct_renderer.current_frame_index,
        packet.dest_texture, stale_base, repoint_info.base_address,
        static_cast<unsigned long long>(packet.dest_address_generation),
        repoint_info.width, repoint_info.height, repoint_info.format);
  }
  return object_resource;
}

// §3b: execute a captured D3DDevice_Resolve as an explicit host copy —
// EDRAM/RT -> destination texture. This is the packet OWNING the resolve.
//
// Deliberately NOT RecordDirectScaledPrivateRectCopyLocked. That primitive is a
// fullscreen-triangle DRAW (drawInstanced(3,1,0,0)) that samples a source
// texture as a descriptor and renders INTO the bound framebuffer — the
// superseded "private-rect blit" model in code: a source texture, and the
// opposite direction. A resolve reads the EDRAM surface named by Flags&7 and
// WRITES into pDestTexture, so it is an image-to-image region copy.
//
// The corrected arg map (2026-07-17) maps 1:1 onto copyTextureRegion, and
// RenderBox and pSourceRect are BOTH LTRB, so the rect needs no conversion.
// "Placement is a delta" (pDestPoint - pSourceRect.topLeft) is exactly this
// call's semantics: the pixel at source_rect.topLeft lands at dest_point. That
// makes EndTiling's per-tile behaviour (it passes each tile's rect as BOTH
// pSourceRect and pDestPoint) fall out with no special case.
//
// copyTextureRegion, not resolveTextureRegion: a Xenos EDRAM surface can be
// multisampled, but the host textures here are not — ColorTarget/DepthTarget/
// Texture descs are all created without multisampling, so the MSAA is already
// flattened at creation. If host RTs ever become multisampled this must switch
// to resolveTextureRegion, which is why the check below is on the DESC, not on
// the guest's multisample_type.
//
// PURE CLASSIFIER (2026-08-02): runs every check and records NOTHING — it
// answers "could a host copy express this resolve" and feeds the refusal
// census. The LIVE copy-recording tail (barriers + copyTextureRegion +
// content bookkeeping) was DELETED: across runs 497-503 (GTL-authoritative,
// gameplay clean) the census read `HOST_COPY(dry): buildable=0` and the live
// path read `live[copy=0]` — no resolve ever passed the format+extent ladder,
// so the tail never executed. RE-ADD TRIGGER: `HOST_COPY(dry): buildable=`
// going nonzero in the resolve census (format materialization could make
// copies buildable again); restore the recording tail from git history.
// Defined next to DirectDrawPhaseAccumulator (which lives further down this
// file); RecordGtlResolveDrawLocked precedes the accumulator definition.

// Pre-resolved resolve endpoints for the live execute path (2026-08-02
// one-pass peel). `valid` means BOTH lookups were performed by the caller —
// source via FindDirectResourceByExactObjectLocked and, only when the source
// existed (preserving the classifier's historical refusal order, where a
// missing source refuses before the hybrid walk's repoint side effects can
// run), destination via FindGtlResolveDestinationHybridLocked. Either pointer
// may still be null; that is the same answer the callee's own lookup would
// have produced at the same instant under the same registry lock.

bool IsGtlResolveHostCopyBuildableLocked(
    const refii::gtl::ResolvePacket& packet, GtlResolveHostCopyStats* stats,
    const GtlResolveEndpoints* endpoints) {
  const auto refuse = [&](uint32_t GtlResolveHostCopyStats::*counter) {
    if (stats) {
      ++(stats->*counter);
    }
    return false;
  };
  if (g_direct_renderer.device == nullptr) {
    return false;
  }
  // EXACT object lookup, not FindDirectResourceLocked. The fuzzy one falls back
  // to DirectResourceMatchesAddress, which also matches a resource's DATA/BASE
  // address — and that measurably collapsed a resolve's source and destination
  // onto ONE host image (run 454: src_surf=0x404DAA00 resolved to the dest's own
  // resource 0x404DE690), which would make the resolve a silent copy-to-self.
  // Two distinct guest objects are two resources; matching an EDRAM surface by
  // "some resource whose base address happens to equal this" is exactly the
  // inference the design removes. Refuse rather than guess.
  // NOTE: this is still not perfectly exact — it also matches parent_address —
  // so the same_texture counter stays as the guard that proves the collapse is
  // actually gone rather than assumed.
  PlumeTrackedResource* source =
      endpoints != nullptr
          ? endpoints->source
          : FindDirectResourceByExactObjectLocked(packet.source_surface);
  if (source == nullptr) {
    return refuse(&GtlResolveHostCopyStats::no_source_resource);
  }
  // Destination identity: the hybrid rule measured in run 460 (176 packets:
  // 50 base-keyed deterministic, 123 zero-base, 3 ambiguous, 0 missing).
  // dword[1] of the captured fetch constant IS the memory the resolve writes
  // (IDA: D3DDevice_Resolve masks it into RB_COPY_DEST_BASE), so when its base
  // bits are nonzero, key memory-to-memory and let the r6 object break ties.
  // When they are zero (the DS/EndTiling family — phys 0 is a legal 360 write
  // target, so zero is a placement, not missing data), the base names nothing
  // usable and the r6 object is the stated identity. Both keys come from the
  // wrapper call; ambiguity refuses rather than guesses.
  PlumeTrackedResource* destination =
      endpoints != nullptr ? endpoints->destination
                           : FindGtlResolveDestinationHybridLocked(packet);
  if (destination == nullptr) {
    // Run-545 diagnosis: authoritative clears tripled this refusal (31->102);
    // log the identities to see WHICH destination family loses its host
    // resource under the cutover.
    static uint32_t logged_no_dest = 0;
    if (logged_no_dest < 24u) {
      REXLOG_INFO(
          "Refii GTL resolve NO-DST #{} frame={} dest_tex=0x{:08X} "
          "dest_base=0x{:08X} gen={} sel={} flags=0x{:X} src=0x{:08X} "
          "fetch1=0x{:08X}",
          ++logged_no_dest, g_direct_renderer.current_frame_index,
          packet.dest_texture, packet.dest_base_address,
          static_cast<unsigned long long>(packet.dest_address_generation),
          packet.source_select, packet.flags, packet.source_surface,
          packet.dest_fetch_valid ? packet.dest_fetch_dwords[1] : 0u);
    }
    return refuse(&GtlResolveHostCopyStats::no_dest_resource);
  }
  if (!IsImageResource(source->kind) || !IsImageResource(destination->kind)) {
    return refuse(&GtlResolveHostCopyStats::not_image);
  }
  if (!PlumeGuestImageReady(*source) || !PlumeGuestImageReady(*destination)) {
    return refuse(&GtlResolveHostCopyStats::not_ready);
  }
  plume::RenderTexture* source_texture = source->guest_texture.texture.get();
  plume::RenderTexture* dest_texture = destination->guest_texture.texture.get();
  if (source_texture == nullptr || dest_texture == nullptr ||
      source_texture == dest_texture) {
    // source==dest is not a harmless skip: it means two distinct guest objects
    // (an EDRAM surface and a texture header) collapsed onto ONE host image, so
    // the resolve would be a copy-to-self and silently do nothing. On console
    // EDRAM is NOT the destination's memory, so this is a host aliasing
    // artifact — most likely FindDirectResourceLocked's fallback loop, which
    // matches on parent/data/base address (DirectResourceMatchesAddress), not
    // just the exact object. Print the pair rather than reason about it.
    static uint32_t logged_same_texture = 0;
    if (source_texture != nullptr && source_texture == dest_texture &&
        logged_same_texture < 12u) {
      // Print the PARENT links and base addresses too. The likely story is not a
      // lookup bug at all: an RT surface and its texture are parent-related, and
      // on console they are DIFFERENT memories (EDRAM vs main) while host-side
      // they are one image — in which case rendering already wrote the texture,
      // the resolve is genuinely redundant, and refusing is CORRECT. If instead
      // the parents are unrelated and only the addresses collide, it is the
      // namespace bug. These fields separate those two readings.
      REXLOG_INFO(
          "Refii GTL resolve host-copy SAME-TEX #{} src_surf=0x{:08X} "
          "dest_tex=0x{:08X} src_res=0x{:08X}(parent=0x{:08X} base=0x{:08X}) "
          "dst_res=0x{:08X}(parent=0x{:08X} base=0x{:08X}) "
          "pkt_dest_base=0x{:08X} flags=0x{:08X} sel={}{}",
          ++logged_same_texture, packet.source_surface, packet.dest_texture,
          source->guest_address, source->parent_address,
          PlumeGuestTextureBaseAddress(*source), destination->guest_address,
          destination->parent_address,
          PlumeGuestTextureBaseAddress(*destination), packet.dest_base_address,
          packet.flags, packet.source_select,
          packet.source_is_depth ? "(DS)" : "(RT)");
    }
    return refuse(&GtlResolveHostCopyStats::same_texture);
  }
  // A real resolve converts EDRAM's layout/format into the destination's tiled
  // format; a raw image copy cannot. Refuse rather than emit an invalid copy
  // (the design's "refuse rather than guess") and MEASURE how often the formats
  // disagree — a non-zero count is the evidence that this needs a shader-based
  // resolve rather than a copy, and that is a decision to take on data.
  // PlumeGuestTextureFormat, not texture_format: the host image is created with
  // the effective format (guest_texture.format when set), which is what the
  // copy will actually validate against — the same reason the extents below use
  // PlumeGuestTextureWidth/Height.
  // A DEPTH resolve (Flags&7 == 4) is expected to land here: a depth source and
  // a colour destination genuinely do not share a format. That refusal is
  // correct, and its count is the measurement of how much of the resolve family
  // a plain copy can never own.
  const plume::RenderFormat source_format = PlumeGuestTextureFormat(*source);
  const plume::RenderFormat dest_format = PlumeGuestTextureFormat(*destination);
  if (source_format != dest_format) {
    // Print the actual pair. "86% format mismatch" has two very different
    // explanations and they demand opposite fixes: a genuine EDRAM->texture
    // format CONVERSION (build the shader resolve), or a destination we
    // materialized with no format at all (UNKNOWN — a self-inflicted artifact
    // of claiming resolve dests as interest with image_content=false, in which
    // case fix the materialization and the copy may just work). Do not build
    // the shader path until this says which.
    static uint32_t logged_format_mismatch = 0;
    if (logged_format_mismatch < 24u) {
      REXLOG_INFO(
          "Refii GTL resolve host-copy fmt MISMATCH #{} src=0x{:08X} fmt={} "
          "({}x{}) -> dst=0x{:08X} fmt={} ({}x{}) flags=0x{:08X} sel={}{}",
          ++logged_format_mismatch, packet.source_surface,
          static_cast<uint32_t>(source_format),
          PlumeGuestTextureWidth(*source), PlumeGuestTextureHeight(*source),
          packet.dest_texture, static_cast<uint32_t>(dest_format),
          PlumeGuestTextureWidth(*destination),
          PlumeGuestTextureHeight(*destination), packet.flags,
          packet.source_select, packet.source_is_depth ? "(DS)" : "(RT)");
    }
    return refuse(&GtlResolveHostCopyStats::format_mismatch);
  }

  // r5 LTRB; NULL (source_rect_default) means the FULL source surface.
  const int32_t source_width =
      static_cast<int32_t>(PlumeGuestTextureWidth(*source));
  const int32_t source_height =
      static_cast<int32_t>(PlumeGuestTextureHeight(*source));
  int32_t left = 0, top = 0, right = source_width, bottom = source_height;
  if (packet.source_rect_valid) {
    left = static_cast<int32_t>(packet.source_rect[0]);
    top = static_cast<int32_t>(packet.source_rect[1]);
    right = static_cast<int32_t>(packet.source_rect[2]);
    bottom = static_cast<int32_t>(packet.source_rect[3]);
  }
  // The console rounds the source rect's right/bottom UP to a multiple of 8;
  // that rounding can name pixels past the host surface, so clamp to the real
  // extent instead of reproducing an overrun.
  left = std::clamp(left, 0, source_width);
  top = std::clamp(top, 0, source_height);
  right = std::clamp(right, left, source_width);
  bottom = std::clamp(bottom, top, source_height);
  if (right <= left || bottom <= top) {
    return refuse(&GtlResolveHostCopyStats::empty_rect);
  }

  // r7 {x,y}; NULL (dest_point_default) means {0,0}.
  const uint32_t dest_x =
      packet.dest_point_valid ? packet.dest_point[0] : 0u;
  const uint32_t dest_y =
      packet.dest_point_valid ? packet.dest_point[1] : 0u;

  // The box must land fully inside the destination subresource. Formats
  // agreeing is not enough: the source host surface may be output-extent
  // promoted while the destination keeps guest dims, and vkCmdCopyImage does
  // not clip — an oversized copy is out-of-bounds and wedged the device the
  // first time it recorded (run 489, full 480x270 -> 280x180). Refuse to the
  // conversion draw, whose viewport/scissor express the placement safely.
  const int32_t dest_level_width = std::max<int32_t>(
      1, static_cast<int32_t>(PlumeGuestTextureWidth(*destination)) >>
             packet.dest_level);
  const int32_t dest_level_height = std::max<int32_t>(
      1, static_cast<int32_t>(PlumeGuestTextureHeight(*destination)) >>
             packet.dest_level);
  if (static_cast<int32_t>(dest_x) + (right - left) > dest_level_width ||
      static_cast<int32_t>(dest_y) + (bottom - top) > dest_level_height) {
    return refuse(&GtlResolveHostCopyStats::extent_mismatch);
  }

  // Every check has passed: this resolve IS buildable as a host copy.
  // `recorded` counts buildability only — see the classifier note at the top
  // of this function for the deleted recording tail and its re-add trigger.
  if (stats) {
    ++stats->recorded;
  }
  return true;
}

void ResetDirectScaledPrivateRectBlitResourcesLocked() {
  DirectScaledPrivateRectBlitResources& blit =
      g_direct_renderer.scaled_private_rect_blit;
  blit.shader_format = plume::RenderShaderFormat::UNKNOWN;
  blit.pipeline_layout.reset();
  blit.pipelines_by_format.clear();
  blit.texture_sets.clear();
  blit.texture_set_index = 0;
  blit.texture_set_textures = {};
  blit.texture_set_views = {};
  blit.texture_set_layouts = {};
  blit.sampler_set.reset();
  blit.sampler.reset();
}

bool EnsureDirectScaledPrivateRectBlitResourcesLocked(
    plume::RenderDevice* device, plume::RenderShaderFormat shader_format,
    plume::RenderFormat target_format) {
  if (!device || target_format == plume::RenderFormat::UNKNOWN ||
      plume::RenderFormatIsDepth(target_format)) {
    return false;
  }

  DirectScaledPrivateRectBlitResources& blit =
      g_direct_renderer.scaled_private_rect_blit;
  if (blit.shader_format != shader_format) {
    ResetDirectScaledPrivateRectBlitResourcesLocked();
    blit.shader_format = shader_format;
  }

  if (!blit.pipeline_layout || !blit.sampler_set || !blit.sampler ||
      blit.texture_sets.size() != kPresentBlitDescriptorSetRingSize) {
    blit.pipeline_layout.reset();
    blit.pipelines_by_format.clear();
    blit.texture_sets.clear();
    blit.texture_set_index = 0;
    blit.texture_set_textures = {};
    blit.texture_set_views = {};
    blit.texture_set_layouts = {};
    blit.sampler_set.reset();
    blit.sampler.reset();

    plume::RenderDescriptorSetBuilder texture_set_builder;
    texture_set_builder.begin();
    texture_set_builder.addTexture(0, 1);
    texture_set_builder.end();
    plume::RenderDescriptorSetBuilder sampler_set_builder;
    sampler_set_builder.begin();
    sampler_set_builder.addSampler(0, 1);
    sampler_set_builder.end();

    plume::RenderPipelineLayoutBuilder layout_builder;
    layout_builder.begin(false, false);
    layout_builder.addDescriptorSet(texture_set_builder);
    layout_builder.addDescriptorSet(sampler_set_builder);
    if (shader_format == plume::RenderShaderFormat::SPIRV) {
      layout_builder.addPushConstant(
          0, 2, sizeof(PlumePresentBlitPushConstants),
          plume::RenderShaderStageFlag::PIXEL);
    } else {
      layout_builder.addRootDescriptor(
          0, 2, plume::RenderRootDescriptorType::CONSTANT_BUFFER);
    }
    layout_builder.end();
    blit.pipeline_layout = layout_builder.create(device);
    if (!blit.pipeline_layout) {
      ResetDirectScaledPrivateRectBlitResourcesLocked();
      return false;
    }

    for (uint32_t i = 0; i < kPresentBlitDescriptorSetRingSize; ++i) {
      std::unique_ptr<plume::RenderDescriptorSet> descriptor_set =
          texture_set_builder.create(device);
      if (!descriptor_set) {
        ResetDirectScaledPrivateRectBlitResourcesLocked();
        return false;
      }
      blit.texture_sets.emplace_back(std::move(descriptor_set));
    }

    blit.sampler_set = sampler_set_builder.create(device);
    blit.sampler = device->createSampler(plume::RenderSamplerDesc());
    if (!blit.sampler_set || !blit.sampler) {
      ResetDirectScaledPrivateRectBlitResourcesLocked();
      return false;
    }
    blit.sampler_set->setSampler(0, blit.sampler.get());
  }

  const uint32_t format_key = static_cast<uint32_t>(target_format);
  if (blit.pipelines_by_format.find(format_key) !=
      blit.pipelines_by_format.end()) {
    return true;
  }

  std::unique_ptr<plume::RenderShader> vertex_shader;
  std::unique_ptr<plume::RenderShader> pixel_shader;
  if (!CreatePresentBlitShaderForFormat(device, shader_format, true,
                                        vertex_shader) ||
      !CreatePresentBlitShaderForFormat(device, shader_format, false,
                                        pixel_shader)) {
    return false;
  }

  plume::RenderGraphicsPipelineDesc desc;
  desc.pipelineLayout = blit.pipeline_layout.get();
  desc.vertexShader = vertex_shader.get();
  desc.pixelShader = pixel_shader.get();
  desc.renderTargetFormat[0] = target_format;
  desc.renderTargetBlend[0] = plume::RenderBlendDesc::Copy();
  desc.renderTargetCount = 1;
  desc.primitiveTopology = plume::RenderPrimitiveTopology::TRIANGLE_LIST;
  desc.cullMode = plume::RenderCullMode::NONE;
  std::unique_ptr<plume::RenderPipeline> pipeline =
      device->createGraphicsPipeline(desc);
  if (!pipeline) {
    return false;
  }
  pipeline->setName("Refii Fable direct scaled private rect blit");
  blit.pipelines_by_format.emplace(format_key, std::move(pipeline));
  return true;
}

bool RecordDirectScaledPrivateRectCopyLocked(
    plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer,
    const FablePrivateTextureCopyAction& action,
    PlumeTrackedResource& source, PlumeTrackedResource& target,
    uint32_t source_slot, const char* reason) {
  if (!command_list || !g_direct_renderer.device ||
      source_slot >= command.resources.textures.size() ||
      !DirectPrivateTextureCopyKindCanBlitToColorTarget(action.kind) ||
      (action.kind == FablePrivateTextureCopyKind::kDepth &&
       target.texture_depth_target) ||
      framebuffer.color_count != 1 || framebuffer.color_address == 0 ||
      (framebuffer.color_address != target.guest_address &&
       framebuffer.color_address != target.parent_address) ||
      framebuffer.framebuffer == nullptr || !PlumeGuestImageReady(source) ||
      !PlumeGuestImageReady(target) ||
      source.guest_texture.texture.get() ==
          target.guest_texture.texture.get() ||
      !source.guest_texture.texture || !source.guest_texture.view ||
      !target.guest_texture.texture || !target.guest_surface.attachment_view) {
    return false;
  }

  const plume::RenderFormat target_format = PlumeGuestTextureFormat(target);
  DirectFramebufferBinding copy_framebuffer;
  if (!ResolveDirectSingleColorFramebufferForResourceLocked(
          g_direct_renderer.device, target, copy_framebuffer) ||
      copy_framebuffer.framebuffer == nullptr) {
    return false;
  }
  if (!EnsureDirectScaledPrivateRectBlitResourcesLocked(
          g_direct_renderer.device, g_direct_renderer.shader_format,
          target_format)) {
    return false;
  }

  DirectScaledPrivateRectBlitResources& blit =
      g_direct_renderer.scaled_private_rect_blit;
  const uint32_t descriptor_index =
      blit.texture_set_index++ % kPresentBlitDescriptorSetRingSize;
  plume::RenderDescriptorSet* texture_set =
      blit.texture_sets[descriptor_index].get();
  constexpr plume::RenderTextureLayout source_layout =
      plume::RenderTextureLayout::SHADER_READ;
  texture_set->setTexture(0, source.guest_texture.texture.get(),
                          source_layout, source.guest_texture.view.get());
  blit.texture_set_textures[descriptor_index] =
      source.guest_texture.texture.get();
  blit.texture_set_views[descriptor_index] = source.guest_texture.view.get();
  blit.texture_set_layouts[descriptor_index] = source_layout;

  RecordDirectTextureBarrierLocked(command_list,
                                   plume::RenderBarrierStage::GRAPHICS,
                                   source.guest_texture.texture.get(),
                                   source_layout);
  RecordDirectTextureBarrierLocked(command_list,
                                   plume::RenderBarrierStage::GRAPHICS,
                                   target.guest_texture.texture.get(),
                                   plume::RenderTextureLayout::COLOR_WRITE);

  command_list->setFramebuffer(copy_framebuffer.framebuffer);
  command_list->setGraphicsPipelineLayout(blit.pipeline_layout.get());
  auto pipeline_it =
      blit.pipelines_by_format.find(static_cast<uint32_t>(target_format));
  if (pipeline_it == blit.pipelines_by_format.end() || !pipeline_it->second) {
    return false;
  }
  command_list->setPipeline(pipeline_it->second.get());
  command_list->setGraphicsDescriptorSet(texture_set, 0);
  command_list->setGraphicsDescriptorSet(blit.sampler_set.get(), 1);

  PlumePresentBlitPushConstants push_constants{};
  if (action.uv_override) {
    push_constants.uv_scale[0] = action.u1 - action.u0;
    push_constants.uv_scale[1] = action.v1 - action.v0;
    push_constants.uv_bias[0] = action.u0;
    push_constants.uv_bias[1] = action.v0;
  } else {
    // GUEST NUMERATOR NEEDS A GUEST DENOMINATOR (2026-08-04).
    //
    // action.source_x / rect_width come from the guest wrapper rect, so the
    // extent they are normalized against must be guest space too. The fallback
    // `PlumeGuestTextureWidth(source)` returns the PROMOTED extent once the
    // source is promoted, which shrinks the sampled window by the promotion
    // factor and magnifies the result — the same defect fixed on the resolve
    // source rect above. Prefer the recorded guest extent.
    //
    // NOTE: `action.source_width` is itself written in BOTH spaces by
    // different producers (guest: the fetch-constant and private-rect copy
    // paths; host: the link/frame-database paths). It is left as the first
    // choice here because the host producers pair it with a host source, but
    // that field genuinely needs to commit to one space.
    const uint32_t source_guest_width =
        source.promoted_from_guest_width != 0
            ? source.promoted_from_guest_width
            : PlumeGuestTextureWidth(source);
    const uint32_t source_guest_height =
        source.promoted_from_guest_height != 0
            ? source.promoted_from_guest_height
            : PlumeGuestTextureHeight(source);
    const uint32_t source_width =
        action.source_width != 0 ? action.source_width : source_guest_width;
    const uint32_t source_height =
        action.source_height != 0 ? action.source_height : source_guest_height;
    if (source_width != 0 && source_height != 0) {
      push_constants.uv_bias[0] = action.source_x / float(source_width);
      push_constants.uv_bias[1] = action.source_y / float(source_height);
      if (action.rectangle_valid && action.rect_width > 0.0f &&
          action.rect_height > 0.0f) {
        push_constants.uv_scale[0] =
            std::min(1.0f, action.rect_width / float(source_width));
        push_constants.uv_scale[1] =
            std::min(1.0f, action.rect_height / float(source_height));
      }
    }
  }

  if (g_direct_renderer.shader_format == plume::RenderShaderFormat::SPIRV) {
    command_list->setGraphicsPushConstants(0, &push_constants, 0,
                                           sizeof(push_constants));
  } else {
    auto constants = CreateUploadBufferWithData(
        g_direct_renderer.device, &push_constants, sizeof(push_constants),
        "Refii Fable direct scaled private rect constants",
        plume::RenderBufferFlag::CONSTANT);
    if (!constants) {
      return false;
    }
    const plume::RenderBuffer* constants_buffer = constants.get();
    command_list->setGraphicsRootDescriptor(constants_buffer->at(0), 0);
    std::vector<std::unique_ptr<plume::RenderBuffer>> uploads;
    uploads.emplace_back(std::move(constants));
    RetainFrameUploadBuffersLocked(std::move(uploads));
  }

  const uint32_t width = copy_framebuffer.width != 0
                             ? copy_framebuffer.width
                             : PlumeGuestTextureWidth(target);
  const uint32_t height = copy_framebuffer.height != 0
                              ? copy_framebuffer.height
                              : PlumeGuestTextureHeight(target);
  // Honor the wrapper's dest_rect (LTRB, r5 of sub_82206888 -- IDA-proven the
  // guest sets its scissor/window to this rect). Discarding it and blitting
  // full-target is the storybook band-SCRAMBLE root cause: page sub-rects all
  // land full-frame and overwrite each other. Place the fullscreen-triangle
  // blit into the dest window instead; the UV push-constants already select
  // the matching source sub-region, giving a correct source-rect->dest-rect
  // scaled blit. Wrappers with no dest_rect (rect_dwords all zero) fall back
  // to full-target -- exactly the already-coherent path, untouched.
  float dest_x = 0.0f;
  float dest_y = 0.0f;
  float dest_w = static_cast<float>(width);
  float dest_h = static_cast<float>(height);
  const uint32_t rect_l = action.wrapper_rect_dwords[0];
  const uint32_t rect_t = action.wrapper_rect_dwords[1];
  const uint32_t rect_r = action.wrapper_rect_dwords[2];
  const uint32_t rect_b = action.wrapper_rect_dwords[3];
  // Guest extent, not the promoted one — see the note on the resolve dest
  // window below. `wrapper_rect_dwords` are GUEST-stated, so dividing by the
  // promoted extent gave a constant 1.0 and would misplace the rect as soon
  // as promotion is live.
  const uint32_t target_guest_width =
      target.promoted_from_guest_width != 0 ? target.promoted_from_guest_width
                                            : PlumeGuestTextureWidth(target);
  const uint32_t target_guest_height =
      target.promoted_from_guest_height != 0 ? target.promoted_from_guest_height
                                             : PlumeGuestTextureHeight(target);
  const bool dest_rect_valid =
      rect_r > rect_l && rect_b > rect_t && target_guest_width != 0 &&
      target_guest_height != 0 && rect_r <= target_guest_width &&
      rect_b <= target_guest_height &&
      // A rect spanning the whole target is the full-target case already.
      (rect_l != 0 || rect_t != 0 || rect_r != target_guest_width ||
       rect_b != target_guest_height);
  if (dest_rect_valid) {
    // Scale guest dest-rect coords into the (possibly scaled) framebuffer.
    const float sx = static_cast<float>(width) /
                     static_cast<float>(target_guest_width);
    const float sy = static_cast<float>(height) /
                     static_cast<float>(target_guest_height);
    dest_x = static_cast<float>(rect_l) * sx;
    dest_y = static_cast<float>(rect_t) * sy;
    dest_w = static_cast<float>(rect_r - rect_l) * sx;
    dest_h = static_cast<float>(rect_b - rect_t) * sy;
  }
  command_list->setViewports(
      plume::RenderViewport(dest_x, dest_y, dest_w, dest_h));
  command_list->setScissors(plume::RenderRect(
      static_cast<int32_t>(dest_x), static_cast<int32_t>(dest_y),
      static_cast<int32_t>(dest_x + dest_w),
      static_cast<int32_t>(dest_y + dest_h)));
  command_list->drawInstanced(3, 1, 0, 0);
  const uint64_t produced_content_serial =
      ++g_direct_renderer.scene_write_serial;
  MarkDirectResourceGpuProducedContentLocked(
      target.guest_address, produced_content_serial, false,
      target.guest_texture.texture.get());

  FablePrivateTextureCopyAction produced_action = action;
  produced_action.source_slot = source_slot;
  produced_action.source_texture =
      command.resources.textures[source_slot] != 0
          ? command.resources.textures[source_slot]
          : source.guest_address;
  produced_action.source_base_address = PlumeGuestTextureBaseAddress(source);
  produced_action.source_width = PlumeGuestTextureWidth(source);
  produced_action.source_height = PlumeGuestTextureHeight(source);
  produced_action.source_format = PlumeGuestTextureGuestFormat(source);
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, source_slot);
  produced_action.source_fetch_dwords =
      HasTrackedFetchDwords(command_fetch) ? command_fetch
                                           : PlumeGuestTextureFetchDwords(source);
  PopulateDirectPrivateTextureCopyTargetLocked(produced_action, target);
  produced_action.backing_texture = 0;
  produced_action.backing_base_address = 0;
  produced_action.backing_width = 0;
  produced_action.backing_height = 0;
  produced_action.backing_format = 0;
  produced_action.backing_fetch_dwords = {};
  produced_action.backing_content_serial = 0;
  PopulateDirectPrivateTextureCopySourceLocked(produced_action, source);
  produced_action.render_target = command.resources.render_targets[0];
  produced_action.depth_stencil = command.resources.depth_stencil;
  produced_action.vertex_shader = command.resources.vertex_shader;
  produced_action.pixel_shader = command.resources.pixel_shader;
  PopulateDirectPrivateTextureCopyShaderRoots(produced_action, command);
  produced_action.lr = command.draw.state_flush_lr;
  produced_action.frame_index = g_direct_renderer.current_frame_index;
  produced_action.draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  RegisterDirectPrivateTextureCopyActionLocked(produced_action);
  RememberDirectPrivateRectExactActionLocked(produced_action);
  RememberDirectPrivateTextureAliasIndexLocked(
      produced_action.source_texture, produced_action.source_base_address,
      produced_action.source_fetch_dwords, target);
  if (!RememberDirectExactPrivateRectSurfaceTextureEdgesLocked(command, target,
                                                              action.kind)) {
    RegisterDirectScaledPrivateRectSurfaceTextureLinkLocked(
        produced_action, "scaled-private-rect-action");
  }
  if (g_direct_renderer.diagnostics.logged_scaled_private_rect_texture_identities < 64u) {
    REXLOG_INFO(
        "Refii plume exact scaled private-rect surface-texture identity #{} "
        "frame={} reason={} draw={} slot={} texture=0x{:08X}/{}x{} "
        "base=0x{:08X} fmt={} surface=0x{:08X}/{}x{} target_fmt={} "
        "content={} kind={} call={} context=0x{:016X} symbol=0x{:08X} "
        "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
        "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X} "
        "uv=({},{})->({},{})",
        ++g_direct_renderer.diagnostics.logged_scaled_private_rect_texture_identities,
        g_direct_renderer.current_frame_index,
        reason != nullptr ? reason : "unknown",
        static_cast<unsigned long long>(command.draw_serial),
        source_slot, produced_action.source_texture,
        produced_action.source_width, produced_action.source_height,
        produced_action.source_base_address, produced_action.source_format,
        target.guest_address, PlumeGuestTextureWidth(target),
        PlumeGuestTextureHeight(target),
        static_cast<uint32_t>(target_format),
        static_cast<unsigned long long>(produced_content_serial),
        FablePrivateTextureCopyKindName(action.kind),
        static_cast<unsigned long long>(produced_action.wrapper_call_serial),
        static_cast<unsigned long long>(produced_action.wrapper_context_key),
        produced_action.wrapper_symbol_address,
        command.resources.render_targets[0], command.resources.depth_stencil,
        command.resources.vertex_shader, command.resources.pixel_shader,
        produced_action.source_fetch_dwords[0],
        produced_action.source_fetch_dwords[1],
        produced_action.source_fetch_dwords[2],
        produced_action.source_fetch_dwords[3],
        produced_action.source_fetch_dwords[4],
        produced_action.source_fetch_dwords[5], push_constants.uv_bias[0],
        push_constants.uv_bias[1],
        push_constants.uv_bias[0] + push_constants.uv_scale[0],
        push_constants.uv_bias[1] + push_constants.uv_scale[1]);
  }
  return true;
}

// §3b: the format-CONVERTING resolve, for the (measured 82%, run 461) majority
// a plain image copy cannot express — R10G10B10A2->R8G8B8A8, FP16->UNORM, and
// eventually D32S8->R32F. Reuses the scaled-private-rect blit machinery (the
// per-target-format pipelines are the conversion) with the ROLES CORRECTED to
// the 2026-07-17 arg map: the render target is the packet's DESTINATION
// texture (r6) — not the bound framebuffer — and the sampled input is the
// EDRAM surface Flags&7 named. Geometry per the map: UVs select pSourceRect
// (LTRB) out of the source; the viewport window lands at pDestPoint with the
// source rect's extent, i.e. the delta placement the hardware applies. This is
// exactly EndTiling-safe: each tile passes its own rect as both, so window ==
// UV region with no special case.
//
// Depth resolves are DEFERRED (refused + counted): sampling a D32S8 source
// into an R32F color target needs a depth-sampling view and its own shader —
// build that on data once the color family is proven live.
//
// `dry_run` records nothing (capture-first; live execution waits on the
// ordering work) but exercises identity + framebuffer + pipeline acquisition,
// so its census predicts the live path's refusals.
bool RecordGtlResolveDrawLocked(plume::RenderCommandList* command_list,
                                const refii::gtl::ResolvePacket& packet,
                                GtlResolveHostCopyStats* stats, bool dry_run,
                                const GtlResolveEndpoints* endpoints) {
  const auto refuse = [&](uint32_t GtlResolveHostCopyStats::*counter) {
    if (stats) {
      ++(stats->*counter);
    }
    return false;
  };
  if ((command_list == nullptr && !dry_run) ||
      g_direct_renderer.device == nullptr) {
    return false;
  }
  if (packet.source_is_depth && !RendererGtlDepthResolvesEnabled()) {
    return refuse(&GtlResolveHostCopyStats::draw_depth_deferred);
  }
  // One-pass endpoints (2026-08-02): the live execute path resolves
  // source+destination ONCE and hands them to both the classifier and this
  // draw — previously each ran its own exact-object find plus the hybrid
  // destination walk (a full registry scan, with repoint side effects) back
  // to back for identical answers. endpoints==nullptr keeps the historical
  // self-lookup for the dry-run census callers.
  PlumeTrackedResource* source =
      endpoints != nullptr
          ? endpoints->source
          : FindDirectResourceByExactObjectLocked(packet.source_surface);
  if (source == nullptr) {
    return refuse(&GtlResolveHostCopyStats::no_source_resource);
  }
  // Depth resolve (D32S8 -> R32F, the pinned pair): plume images all carry
  // SAMPLED usage and views auto-select the depth aspect, so the same draw
  // shape works — the only depth-specific precondition is a live SRV view.
  if (packet.source_is_depth &&
      (source->guest_texture.view == nullptr ||
       source->guest_texture.texture == nullptr)) {
    static uint32_t logged_depth_no_view = 0;
    if (logged_depth_no_view < 8u) {
      REXLOG_INFO(
          "Refii GTL depth resolve NO-VIEW #{} src=0x{:08X} texture={} view={}",
          ++logged_depth_no_view, packet.source_surface,
          source->guest_texture.texture != nullptr ? 1 : 0,
          source->guest_texture.view != nullptr ? 1 : 0);
    }
    return refuse(&GtlResolveHostCopyStats::draw_depth_deferred);
  }
  PlumeTrackedResource* destination =
      endpoints != nullptr ? endpoints->destination
                           : FindGtlResolveDestinationHybridLocked(packet);
  if (destination == nullptr) {
    return refuse(&GtlResolveHostCopyStats::no_dest_resource);
  }
  if (!IsImageResource(source->kind) || !IsImageResource(destination->kind)) {
    return refuse(&GtlResolveHostCopyStats::not_image);
  }
  if (!PlumeGuestImageReady(*source) || !PlumeGuestImageReady(*destination)) {
    return refuse(&GtlResolveHostCopyStats::not_ready);
  }
  if (source->guest_texture.texture.get() ==
      destination->guest_texture.texture.get()) {
    // Parent-linked RT/texture pair backed by one host image (run 460 probes):
    // rendering already wrote the pixels; the resolve is redundant host-side.
    return refuse(&GtlResolveHostCopyStats::same_texture);
  }

  // Source rect (LTRB, NULL => full surface), clamped like the copy path.
  //
  // THE SOURCE RECT IS GUEST SPACE, SO ITS DENOMINATOR MUST BE TOO
  // (2026-08-04). `packet.source_rect` is the rect the GUEST passed to
  // D3DDevice_Resolve — 1280x720 numbers. `PlumeGuestTextureWidth` returns the
  // PROMOTED extent for any promoted surface (1920), so clamping and
  // normalizing against it made a full-surface guest rect come out as
  // uv_scale = 1280/1920 = 0.667: we sampled the top-left 1/1.5 of the source.
  // The destination window below (:dest_promoted_from_width) correctly scales
  // by 1.5, so that 2/3 crop was then stretched over the whole destination —
  // content magnified exactly 1.5x and cropped to the top-left, and an exact
  // no-op at 720p where promotion does not happen. That is the loading screen
  // symptom precisely.
  //
  // Use the guest extent for the rect arithmetic; the UVs are normalized, so a
  // guest rect over a guest extent addresses the promoted image correctly.
  const int32_t source_width =
      static_cast<int32_t>(source->promoted_from_guest_width != 0
                               ? source->promoted_from_guest_width
                               : PlumeGuestTextureWidth(*source));
  const int32_t source_height =
      static_cast<int32_t>(source->promoted_from_guest_height != 0
                               ? source->promoted_from_guest_height
                               : PlumeGuestTextureHeight(*source));
  if (source_width <= 0 || source_height <= 0) {
    return refuse(&GtlResolveHostCopyStats::empty_rect);
  }
  int32_t left = 0, top = 0, right = source_width, bottom = source_height;
  if (packet.source_rect_valid) {
    left = std::clamp(static_cast<int32_t>(packet.source_rect[0]), 0,
                      source_width);
    top = std::clamp(static_cast<int32_t>(packet.source_rect[1]), 0,
                     source_height);
    right = std::clamp(static_cast<int32_t>(packet.source_rect[2]), left,
                       source_width);
    bottom = std::clamp(static_cast<int32_t>(packet.source_rect[3]), top,
                        source_height);
  }
  if (right <= left || bottom <= top) {
    return refuse(&GtlResolveHostCopyStats::empty_rect);
  }

  const FableDirectClock::time_point resolve_fb_t0 = FableDirectClock::now();
  DirectFramebufferBinding dest_framebuffer;
  // Audit R1: attach the packet's destination level (mip-chain filler
  // sub_821FC5D0 resolves DestLevel=1..N-1); a level the host image does not
  // carry refuses here, so the dry-run census predicts that too.
  if (!ResolveDirectSingleColorFramebufferForResourceLocked(
          g_direct_renderer.device, *destination, dest_framebuffer,
          packet.dest_level) ||
      dest_framebuffer.framebuffer == nullptr) {
    AccumulateGtlResolveFbMs(
        FableDirectElapsedMs(resolve_fb_t0, FableDirectClock::now()));
    return refuse(&GtlResolveHostCopyStats::draw_no_framebuffer);
  }
  const plume::RenderFormat target_format =
      PlumeGuestTextureFormat(*destination);
  if (!EnsureDirectScaledPrivateRectBlitResourcesLocked(
          g_direct_renderer.device, g_direct_renderer.shader_format,
          target_format)) {
    AccumulateGtlResolveFbMs(
        FableDirectElapsedMs(resolve_fb_t0, FableDirectClock::now()));
    return refuse(&GtlResolveHostCopyStats::draw_no_pipeline);
  }
  DirectScaledPrivateRectBlitResources& blit =
      g_direct_renderer.scaled_private_rect_blit;
  auto pipeline_it =
      blit.pipelines_by_format.find(static_cast<uint32_t>(target_format));
  if (pipeline_it == blit.pipelines_by_format.end() || !pipeline_it->second) {
    AccumulateGtlResolveFbMs(
        FableDirectElapsedMs(resolve_fb_t0, FableDirectClock::now()));
    return refuse(&GtlResolveHostCopyStats::draw_no_pipeline);
  }
  const FableDirectClock::time_point resolve_draw_t0 = FableDirectClock::now();
  AccumulateGtlResolveFbMs(
      FableDirectElapsedMs(resolve_fb_t0, resolve_draw_t0));

  if (dry_run) {
    if (stats) {
      ++stats->draw_recorded;
    }
    return true;
  }

  const uint32_t descriptor_index =
      blit.texture_set_index++ % kPresentBlitDescriptorSetRingSize;
  plume::RenderDescriptorSet* texture_set =
      blit.texture_sets[descriptor_index].get();
  constexpr plume::RenderTextureLayout source_layout =
      plume::RenderTextureLayout::SHADER_READ;
  texture_set->setTexture(0, source->guest_texture.texture.get(),
                          source_layout, source->guest_texture.view.get());
  blit.texture_set_textures[descriptor_index] =
      source->guest_texture.texture.get();
  blit.texture_set_views[descriptor_index] = source->guest_texture.view.get();
  blit.texture_set_layouts[descriptor_index] = source_layout;

  RecordDirectTextureBarrierLocked(command_list,
                                   plume::RenderBarrierStage::GRAPHICS,
                                   source->guest_texture.texture.get(),
                                   source_layout);
  RecordDirectTextureBarrierLocked(command_list,
                                   plume::RenderBarrierStage::GRAPHICS,
                                   destination->guest_texture.texture.get(),
                                   plume::RenderTextureLayout::COLOR_WRITE);

  command_list->setFramebuffer(dest_framebuffer.framebuffer);
  command_list->setGraphicsPipelineLayout(blit.pipeline_layout.get());
  command_list->setPipeline(pipeline_it->second.get());
  command_list->setGraphicsDescriptorSet(texture_set, 0);
  command_list->setGraphicsDescriptorSet(blit.sampler_set.get(), 1);

  // UVs select pSourceRect out of the source surface.
  PlumePresentBlitPushConstants push_constants{};
  push_constants.uv_bias[0] = static_cast<float>(left) / source_width;
  push_constants.uv_bias[1] = static_cast<float>(top) / source_height;
  push_constants.uv_scale[0] =
      static_cast<float>(right - left) / source_width;
  push_constants.uv_scale[1] =
      static_cast<float>(bottom - top) / source_height;
  if (g_direct_renderer.shader_format == plume::RenderShaderFormat::SPIRV) {
    command_list->setGraphicsPushConstants(0, &push_constants, 0,
                                           sizeof(push_constants));
  } else {
    auto constants = CreateUploadBufferWithData(
        g_direct_renderer.device, &push_constants, sizeof(push_constants),
        "Refii GTL resolve draw constants", plume::RenderBufferFlag::CONSTANT);
    if (!constants) {
      return false;
    }
    const plume::RenderBuffer* constants_buffer = constants.get();
    command_list->setGraphicsRootDescriptor(constants_buffer->at(0), 0);
    std::vector<std::unique_ptr<plume::RenderBuffer>> uploads;
    uploads.emplace_back(std::move(constants));
    RetainFrameUploadBuffersLocked(std::move(uploads));
  }

  // Dest window: pDestPoint top-left, source-rect extent (the delta
  // placement), scaled from guest texel space into the (possibly scaled)
  // framebuffer. Guest texel space is the DESTINATION LEVEL's (audit R1):
  // pDestPoint is in level coordinates and the attached framebuffer is the
  // level view, so both sides of the scale shift by dest_level (min 1),
  // mirroring the copy path's Subresource math.
  // DENOMINATOR MUST BE THE GUEST EXTENT, NOT THE PROMOTED ONE.
  // PlumeGuestTextureWidth() returns `guest_texture.width`, which
  // RefreshPlumeGuestImageObjectsFromTrackedFields sets from
  // tracked.texture_width — and promotion OVERWRITES texture_width with the
  // host extent. So this used to divide the framebuffer extent by itself and
  // get exactly 1.0 every time. Invisible while promotion is dead; the moment
  // it goes live, every guest-stated dest rect would land at guest size in the
  // top-left of a host-sized attachment. promoted_from_guest_* is the extent
  // the guest actually stated (0 when unpromoted, hence the fallback).
  const uint32_t dest_promoted_from_width =
      destination->promoted_from_guest_width != 0
          ? destination->promoted_from_guest_width
          : PlumeGuestTextureWidth(*destination);
  const uint32_t dest_promoted_from_height =
      destination->promoted_from_guest_height != 0
          ? destination->promoted_from_guest_height
          : PlumeGuestTextureHeight(*destination);
  const uint32_t dest_guest_width =
      std::max(1u, dest_promoted_from_width >> packet.dest_level);
  const uint32_t dest_guest_height =
      std::max(1u, dest_promoted_from_height >> packet.dest_level);
  const uint32_t fb_width = dest_framebuffer.width != 0
                                ? dest_framebuffer.width
                                : dest_guest_width;
  const uint32_t fb_height = dest_framebuffer.height != 0
                                 ? dest_framebuffer.height
                                 : dest_guest_height;
  const float sx =
      static_cast<float>(fb_width) / static_cast<float>(dest_guest_width);
  const float sy =
      static_cast<float>(fb_height) / static_cast<float>(dest_guest_height);
  const uint32_t dest_point_x =
      packet.dest_point_valid ? packet.dest_point[0] : 0u;
  const uint32_t dest_point_y =
      packet.dest_point_valid ? packet.dest_point[1] : 0u;
  const float dest_x = static_cast<float>(dest_point_x) * sx;
  const float dest_y = static_cast<float>(dest_point_y) * sy;
  const float dest_w = static_cast<float>(right - left) * sx;
  const float dest_h = static_cast<float>(bottom - top) * sy;
  command_list->setViewports(
      plume::RenderViewport(dest_x, dest_y, dest_w, dest_h));
  command_list->setScissors(plume::RenderRect(
      static_cast<int32_t>(dest_x), static_cast<int32_t>(dest_y),
      static_cast<int32_t>(dest_x + dest_w),
      static_cast<int32_t>(dest_y + dest_h)));
  command_list->drawInstanced(3, 1, 0, 0);

  RecordDirectTextureBarrierLocked(
      command_list, plume::RenderBarrierStage::GRAPHICS,
      destination->guest_texture.texture.get(),
      plume::RenderTextureLayout::SHADER_READ);
  MarkDirectResourceGpuProducedContentLocked(
      destination->guest_address, ++g_direct_renderer.scene_write_serial,
      false, destination->guest_texture.texture.get());
  if (packet.dest_level != 0) {
    // Mip-resolve census (audit R1): capped, then every 1024th.
    static uint32_t logged_mip_draws = 0;
    ++logged_mip_draws;
    if (logged_mip_draws <= 16u || (logged_mip_draws % 1024u) == 0u) {
      REXLOG_INFO(
          "Refii gtl resolve mip draw #{} level={} dest=0x{:08X} {}x{} "
          "window=({},{} {}x{})",
          logged_mip_draws, packet.dest_level, destination->guest_address,
          dest_guest_width, dest_guest_height, dest_point_x, dest_point_y,
          right - left, bottom - top);
    }
  }
  if (stats) {
    ++stats->draw_recorded;
  }
  AccumulateGtlResolveDrawMs(
      FableDirectElapsedMs(resolve_draw_t0, FableDirectClock::now()));
  return true;
}

FableDirectFrameDatabaseCommandPacket
BuildFrameDatabaseSurfaceDestinationCommandPacketLocked(
    const FableSurfaceDestinationTextureRelation& relation,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    bool copy_used_scaled_path) {
  return BuildFableDirectSurfaceDestinationFrameDatabaseCommandPacket(
      relation, source, target, copy_used_scaled_path);
}

void AppendFrameDatabaseSurfaceDestinationCommandPacketLocked(
    FableDirectFrameDatabaseCommandPacket packet) {
  if (g_direct_renderer.frame_database_command_packets.size() >=
      kMaxFrameDatabaseCommandPackets) {
    ++g_direct_renderer.frame_database_omitted_command_packets;
    return;
  }
  AppendRefiiFrameActionPacketLocked(
      BuildRefiiSurfaceDestinationActionPacket(
          g_direct_renderer.refii_packet_queue.frame_id, packet));
  g_direct_renderer.frame_database_command_packets.push_back(std::move(packet));
}

void AppendFrameDatabaseSurfaceDestinationCommandPacketLocked(
    const FableSurfaceDestinationTextureRelation& relation,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    bool scaled_copy) {
  AppendFrameDatabaseSurfaceDestinationCommandPacketLocked(
      BuildFrameDatabaseSurfaceDestinationCommandPacketLocked(
          relation, source, target, scaled_copy));
}

void AppendFrameDatabaseSurfaceDestinationResolvePacketLocked(
    const FableSurfaceDestinationTextureRelation& relation,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target) {
  if (g_direct_renderer.frame_database_command_packets.size() >=
      kMaxFrameDatabaseCommandPackets) {
    ++g_direct_renderer.frame_database_omitted_command_packets;
    return;
  }

  const FableSurfaceTextureLink& link = relation.link;
  const uint64_t source_packet_id =
      relation.serial != 0 ? relation.serial : link.copy_call_serial;
  FableDirectFrameDatabaseCommandPacket packet;
  packet.packet_id =
      source_packet_id != 0 ? ((source_packet_id << 1u) | 1u)
                            : uint64_t(g_direct_renderer.current_frame_index);
  packet.command_serial = link.copy_call_serial != 0 ? link.copy_call_serial
                                                     : relation.serial;
  packet.command_type =
      static_cast<uint32_t>(FableDirectRenderCommandType::kResolveCopy);
  packet.kind = "resolve";
  packet.pass_name = "Resolve";
  packet.root.call_serial = link.copy_call_serial;
  packet.root.context_key = link.copy_context_key;
  packet.root.symbol_address = link.copy_symbol_address;
  packet.render_targets[0] = link.render_target;
  packet.depth_stencil = link.depth_stencil;

  const uint32_t source_slot =
      relation.source_slot < packet.render_targets.size()
          ? relation.source_slot
          : 0u;
  packet.render_targets[source_slot] = source.guest_address;
  packet.resolve_source_surface = source.guest_address;
  packet.resolve_source_slot = source_slot;
  packet.resolve_source_depth = false;
  packet.resolve_copy_control = source_slot;
  packet.resolve_dest_base =
      link.texture_base_address != 0 ? link.texture_base_address
                                     : PlumeGuestTextureBaseAddress(target);
  if (packet.resolve_dest_base == 0) {
    packet.resolve_dest_base = target.guest_address;
  }
  packet.resolve_dest_pitch = PlumeGuestTextureWidth(target);
  packet.resolve_dest_info = PlumeGuestTextureGuestFormat(target);
  g_direct_renderer.frame_database_command_packets.push_back(std::move(packet));
}

bool RecordDirectSurfaceDestinationScaledCopyLocked(
    plume::RenderCommandList* command_list,
    const RefiiSurfaceDestinationPacket& packet,
    const FableSurfaceDestinationTextureRelation& relation,
    PlumeTrackedResource& source, PlumeTrackedResource& target) {
  if (!command_list || !g_direct_renderer.device ||
      !DirectPrivateTextureCopyKindCanBlitToColorTarget(relation.link.kind) ||
      !PlumeGuestImageReady(source) || !PlumeGuestImageReady(target) ||
      source.guest_texture.texture.get() == target.guest_texture.texture.get() ||
      !source.guest_texture.texture || target.texture_depth_target ||
      !target.guest_texture.texture) {
    return false;
  }
  if (!RefreshDirectTextureSampleViewLocked(source) ||
      !source.guest_texture.view) {
    return false;
  }
  if (!PromoteDirectExactCopyDestinationToColorTargetLocked(
          g_direct_renderer.device, target, relation.link)) {
    return false;
  }

  const plume::RenderFormat target_format = PlumeGuestTextureFormat(target);
  DirectFramebufferBinding copy_framebuffer;
  if (!ResolveDirectSingleColorFramebufferForResourceLocked(
          g_direct_renderer.device, target, copy_framebuffer) ||
      copy_framebuffer.framebuffer == nullptr ||
      !EnsureDirectScaledPrivateRectBlitResourcesLocked(
          g_direct_renderer.device, g_direct_renderer.shader_format,
          target_format)) {
    return false;
  }

  DirectScaledPrivateRectBlitResources& blit =
      g_direct_renderer.scaled_private_rect_blit;
  auto pipeline_it =
      blit.pipelines_by_format.find(static_cast<uint32_t>(target_format));
  if (pipeline_it == blit.pipelines_by_format.end() || !pipeline_it->second) {
    return false;
  }
  const uint32_t descriptor_index =
      blit.texture_set_index++ % kPresentBlitDescriptorSetRingSize;
  plume::RenderDescriptorSet* texture_set =
      blit.texture_sets[descriptor_index].get();
  constexpr plume::RenderTextureLayout source_layout =
      plume::RenderTextureLayout::SHADER_READ;
  texture_set->setTexture(0, source.guest_texture.texture.get(),
                          source_layout, source.guest_texture.view.get());
  blit.texture_set_textures[descriptor_index] =
      source.guest_texture.texture.get();
  blit.texture_set_views[descriptor_index] = source.guest_texture.view.get();
  blit.texture_set_layouts[descriptor_index] = source_layout;

  MaybeBeginRenderDocSurfaceDestinationCaptureLocked("scaled", relation,
                                                     source, target);
  RecordDirectTextureBarrierLocked(command_list,
                                   plume::RenderBarrierStage::GRAPHICS,
                                   source.guest_texture.texture.get(),
                                   source_layout);
  RecordDirectTextureBarrierLocked(command_list,
                                   plume::RenderBarrierStage::GRAPHICS,
                                   target.guest_texture.texture.get(),
                                   plume::RenderTextureLayout::COLOR_WRITE);

  command_list->setFramebuffer(copy_framebuffer.framebuffer);
  command_list->setGraphicsPipelineLayout(blit.pipeline_layout.get());
  command_list->setPipeline(pipeline_it->second.get());
  command_list->setGraphicsDescriptorSet(texture_set, 0);
  command_list->setGraphicsDescriptorSet(blit.sampler_set.get(), 1);

  PlumePresentBlitPushConstants push_constants{};
  const uint32_t packet_source_width =
      packet.source_width != 0 ? packet.source_width
                               : PlumeGuestTextureWidth(source);
  const uint32_t packet_source_height =
      packet.source_height != 0 ? packet.source_height
                                : PlumeGuestTextureHeight(source);
  const uint32_t texture_width = PlumeGuestTextureWidth(source);
  const uint32_t texture_height = PlumeGuestTextureHeight(source);
  if (packet_source_width != 0 && packet_source_height != 0 &&
      texture_width != 0 && texture_height != 0) {
    push_constants.uv_scale[0] =
        std::min(1.0f, static_cast<float>(packet_source_width) /
                           static_cast<float>(texture_width));
    push_constants.uv_scale[1] =
        std::min(1.0f, static_cast<float>(packet_source_height) /
                           static_cast<float>(texture_height));
  }
  if (g_direct_renderer.shader_format == plume::RenderShaderFormat::SPIRV) {
    command_list->setGraphicsPushConstants(0, &push_constants, 0,
                                           sizeof(push_constants));
  } else {
    auto constants = CreateUploadBufferWithData(
        g_direct_renderer.device, &push_constants, sizeof(push_constants),
        "Refii Fable surface destination constants",
        plume::RenderBufferFlag::CONSTANT);
    if (!constants) {
      return false;
    }
    const plume::RenderBuffer* constants_buffer = constants.get();
    command_list->setGraphicsRootDescriptor(constants_buffer->at(0), 0);
    std::vector<std::unique_ptr<plume::RenderBuffer>> uploads;
    uploads.emplace_back(std::move(constants));
    RetainFrameUploadBuffersLocked(std::move(uploads));
  }

  const uint32_t width = packet.destination_width != 0
                             ? packet.destination_width
                             : (copy_framebuffer.width != 0
                                    ? copy_framebuffer.width
                                    : PlumeGuestTextureWidth(target));
  const uint32_t height = packet.destination_height != 0
                              ? packet.destination_height
                              : (copy_framebuffer.height != 0
                                     ? copy_framebuffer.height
                                     : PlumeGuestTextureHeight(target));
  command_list->setViewports(plume::RenderViewport(
      0.0f, 0.0f, static_cast<float>(width), static_cast<float>(height)));
  command_list->setScissors(
      plume::RenderRect(0, 0, static_cast<int32_t>(width),
                        static_cast<int32_t>(height)));
  command_list->drawInstanced(3, 1, 0, 0);
  RecordDirectTextureBarrierLocked(command_list,
                                   plume::RenderBarrierStage::GRAPHICS,
                                   target.guest_texture.texture.get(),
                                   plume::RenderTextureLayout::SHADER_READ);
  RecordDirectTextureBarrierLocked(
      command_list, plume::RenderBarrierStage::GRAPHICS,
      source.guest_texture.texture.get(),
      source.texture_render_target ? plume::RenderTextureLayout::COLOR_WRITE
                                   : plume::RenderTextureLayout::SHADER_READ);
  MarkDirectResourceGpuProducedContentLocked(
      target.guest_address, ++g_direct_renderer.scene_write_serial, false,
      target.guest_texture.texture.get());
  return true;
}

bool RecordDirectSurfaceDestinationExactCopyLocked(
    plume::RenderCommandList* command_list,
    const RefiiSurfaceDestinationPacket& packet,
    const FableSurfaceDestinationTextureRelation& relation,
    PlumeTrackedResource& source, PlumeTrackedResource& target) {
  if (packet.scaled_copy ||
      packet.source_surface != source.guest_address ||
      packet.destination_texture != target.guest_address) {
    return false;
  }
  const uint32_t source_width = PlumeGuestTextureWidth(source);
  const uint32_t source_height = PlumeGuestTextureHeight(source);
  const uint32_t target_width = PlumeGuestTextureWidth(target);
  const uint32_t target_height = PlumeGuestTextureHeight(target);
  if ((packet.source_width != 0 && packet.source_width != source_width) ||
      (packet.source_height != 0 && packet.source_height != source_height) ||
      (packet.destination_width != 0 &&
       packet.destination_width != target_width) ||
      (packet.destination_height != 0 &&
       packet.destination_height != target_height)) {
    return false;
  }
  MaybeBeginRenderDocSurfaceDestinationCaptureLocked("exact", relation,
                                                     source, target);
  return RecordDirectTextureCopyLocked(command_list, source, target);
}

bool RecordDirectSurfaceDestinationActionPacketLocked(
    plume::RenderCommandList* command_list,
    const RefiiFrameActionPacket& action,
    const FableSurfaceDestinationTextureRelation& relation,
    PlumeTrackedResource& source, PlumeTrackedResource& target,
    bool* scaled_copy) {
  if (action.type != RefiiFrameActionType::kSurfaceDestination) {
    return false;
  }
  const RefiiSurfaceDestinationPacket& packet = action.surface_destination;
  if (scaled_copy) {
    *scaled_copy = packet.scaled_copy;
  }
  if (packet.scaled_copy) {
    return RecordDirectSurfaceDestinationScaledCopyLocked(
        command_list, packet, relation, source, target);
  }
  return RecordDirectSurfaceDestinationExactCopyLocked(
      command_list, packet, relation, source, target);
}


bool RecordDirectFrameActionPacketLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    plume::RenderTexture* output_texture,
    plume::RenderFramebuffer* present_framebuffer,
    plume::RenderShaderFormat shader_format,
    const RefiiFrameActionPacket& action,
    const FableSurfaceDestinationTextureRelation* surface_relation,
    PlumeTrackedResource* surface_source, PlumeTrackedResource* surface_target,
    DirectPresentSource* present_source, bool* surface_scaled_copy) {
  switch (action.type) {
    case RefiiFrameActionType::kSurfaceDestination:
      if (!surface_relation || !surface_source || !surface_target) {
        return false;
      }
      return RecordDirectSurfaceDestinationActionPacketLocked(
          command_list, action, *surface_relation, *surface_source,
          *surface_target, surface_scaled_copy);
    case RefiiFrameActionType::kPresentBlit:
      if (!present_source) {
        return false;
      }
      return RecordDirectPresentBlitActionPacketLocked(
          device, command_list, output_texture, present_framebuffer,
          shader_format, action, *present_source);
    case RefiiFrameActionType::kDraw:
    case RefiiFrameActionType::kClear:
    case RefiiFrameActionType::kResolve:
    case RefiiFrameActionType::kUnknown:
    default:
      return false;
  }
}

bool DirectSurfaceDestinationSourceHasDrawnOrAuthoredPayload(
    const PlumeTrackedResource& source) {
  if (!IsImageResource(source.kind) || !PlumeGuestImageReady(source)) {
    return false;
  }
  if (FableTrackedResourceHasMeaningfulTextureUpload(source) &&
      !FableTrackedResourceIsBlankUploadedSampleProxy(source)) {
    return true;
  }
  return source.draw_content_serial != 0;
}

void ResetDirectSurfaceDestinationStatsLocked(uint32_t relations,
                                              uint32_t omitted) {
  g_direct_renderer.stats.surface_destination_relations = relations;
  g_direct_renderer.stats.surface_destination_omitted_relations = omitted;
  g_direct_renderer.stats.surface_destination_copied_exact = 0;
  g_direct_renderer.stats.surface_destination_copied_scaled = 0;
  g_direct_renderer.stats.surface_destination_skipped_invalid = 0;
  g_direct_renderer.stats.surface_destination_skipped_zero_endpoint = 0;
  g_direct_renderer.stats.surface_destination_skipped_missing_copy_root = 0;
  g_direct_renderer.stats.surface_destination_skipped_wrong_frame = 0;
  g_direct_renderer.stats.surface_destination_skipped_missing_resource = 0;
  g_direct_renderer.stats.surface_destination_skipped_not_image = 0;
  g_direct_renderer.stats.surface_destination_skipped_not_ready = 0;
  g_direct_renderer.stats.surface_destination_skipped_same_texture = 0;
  g_direct_renderer.stats.surface_destination_skipped_inexact_wrapper = 0;
  g_direct_renderer.stats.surface_destination_skipped_stale_content = 0;
  g_direct_renderer.stats.surface_destination_skipped_copy_failed = 0;
}

void NormalizeDirectSurfaceDestinationStatsLocked() {
  // A frame may execute packet-owned SurfaceDestination actions and also run
  // a later registry census.  The census describes the currently retained
  // relation bank, not the number of copy attempts already recorded, so it
  // must not shrink the sidecar's attempt denominator below its outcomes.
  const uint64_t outcomes =
      uint64_t(g_direct_renderer.stats.surface_destination_copied_exact) +
      g_direct_renderer.stats.surface_destination_copied_scaled +
      g_direct_renderer.stats.surface_destination_skipped_invalid +
      g_direct_renderer.stats.surface_destination_skipped_zero_endpoint +
      g_direct_renderer.stats.surface_destination_skipped_missing_copy_root +
      g_direct_renderer.stats.surface_destination_skipped_wrong_frame +
      g_direct_renderer.stats.surface_destination_skipped_missing_resource +
      g_direct_renderer.stats.surface_destination_skipped_not_image +
      g_direct_renderer.stats.surface_destination_skipped_not_ready +
      g_direct_renderer.stats.surface_destination_skipped_same_texture +
      g_direct_renderer.stats.surface_destination_skipped_inexact_wrapper +
      g_direct_renderer.stats.surface_destination_skipped_stale_content +
      g_direct_renderer.stats.surface_destination_skipped_copy_failed;
  g_direct_renderer.stats.surface_destination_relations = std::max(
      g_direct_renderer.stats.surface_destination_relations,
      ClampCounter(outcomes));
}

// Per-frame attribution of surface_destination_ms (the compose-level
// RecordDirectSurfaceDestinationCopiesLocked call, 14-40ms on run 454) plus
// the serial-gated per-draw reruns. The function has four distinct cost
// shapes — a full O(registry) snapshot build, an O(relations x identity
// lookups) interest walk, a non-blocking resource sync, a BLOCKING
// wait-for-observer catch-up sync — and the timing line cannot currently say
// which one owns the swing. Reset each frame in BeginFableDirectFrame.
DirectSurfaceDestinationSplit g_direct_surface_destination_split;

// Plan for one per-draw surface-destination scan. The generation gate at the
// call site is unchanged and still makes runs rare; the plan makes the RUN
// itself O(relevant): instead of snapshotting the whole relation population
// (run 486, storybook: 16 runs x ~1478-relation population = ~92ms each), a
// targeted run visits only the destination buckets the current draw could
// observe, found through the registry's target-address/fetch-key index.

std::unordered_map<uint32_t, uint64_t>&
DirectSurfaceDestinationBucketAttemptsThisFrameLocked() {
  if (g_direct_renderer.surface_destination_bucket_attempt_frame !=
      g_direct_renderer.current_frame_index) {
    g_direct_renderer.surface_destination_bucket_attempt_frame =
        g_direct_renderer.current_frame_index;
    g_direct_renderer.surface_destination_bucket_attempt_generation.clear();
  }
  return g_direct_renderer.surface_destination_bucket_attempt_generation;
}

// Forward declaration: the drawphase accumulator is defined much later in the
// file; the destination-scan census below needs to reach it.

// Collects the destination-relation buckets the current draw could observe:
// every bound texture slot plus the bound render targets / depth target
// (copies into a bound target must land before the draw writes over them),
// expanded through the same identity maps the scan's endpoint resolver uses
// (object / parent / base, objects-by-base, resource base + fetch key) and
// probed against the registry's target index. Over-inclusion is harmless (an
// extra bucket visit); the store-time expansion in
// IndexSurfaceDestinationRelationTargets covers the one direction this
// query-side walk cannot reach (an identity's parent_texture edge, which has
// no reverse map). The residual gap — an identity edge registered AFTER the
// relation's last mutating store whose alias is reachable ONLY via that new
// edge — has no bounded local fix; the small-population global fallback in
// the plan keeps sparse scenes on the provably exact path.
DirectSurfaceDestinationScanPlan
PlanDirectSurfaceDestinationScanForDrawLocked(
    const FableDirectRenderCommand& draw_command) {
  DirectSurfaceDestinationScanPlan plan;
  plan.generation = FableDirectGetSurfaceDestinationRelationGeneration();
  constexpr size_t kTargetedScanMinBuckets = 64;
  if (FableDirectGetSurfaceDestinationRelationBucketCount() <=
      kTargetedScanMinBuckets) {
    plan.global = true;
    return plan;
  }
  std::unordered_set<uint32_t> matched_buckets;
  std::unordered_set<uint32_t> probed_addresses;
  auto probe_address = [&](uint32_t address) {
    if (address == 0 || !probed_addresses.insert(address).second) {
      return;
    }
    if (const std::vector<uint32_t>* buckets =
            FableDirectFindSurfaceDestinationRelationBucketsByTargetAddress(
                address)) {
      matched_buckets.insert(buckets->begin(), buckets->end());
    }
  };
  auto probe_identity = [&](const FableTextureIdentity* identity) {
    if (!identity) {
      return;
    }
    probe_address(identity->object_texture);
    probe_address(identity->parent_texture);
    probe_address(identity->base_address);
  };
  auto expand_address = [&](uint32_t address) {
    if (address == 0) {
      return;
    }
    probe_address(address);
    probe_identity(FableDirectFindTextureIdentityByObject(address));
    probe_identity(FableDirectFindTextureIdentityByBase(address));
    if (const std::vector<uint32_t>* objects =
            FableDirectFindTextureIdentityObjectsByBase(address)) {
      for (uint32_t object : *objects) {
        probe_address(object);
        probe_identity(FableDirectFindTextureIdentityByObject(object));
      }
    }
    if (const PlumeTrackedResource* resource =
            FindDirectResourceLocked(address)) {
      probe_address(PlumeGuestTextureBaseAddress(*resource));
      const uint64_t fetch_key =
          FableDirectTextureFetchKey(PlumeGuestTextureFetchDwords(*resource));
      if (fetch_key != 0) {
        if (const std::vector<uint32_t>* buckets =
                FableDirectFindSurfaceDestinationRelationBucketsByTargetFetchKey(
                    fetch_key)) {
          matched_buckets.insert(buckets->begin(), buckets->end());
        }
      }
    }
  };
  for (uint32_t texture : draw_command.resources.textures) {
    expand_address(texture);
  }
  for (uint32_t render_target : draw_command.resources.render_targets) {
    expand_address(render_target);
  }
  expand_address(draw_command.resources.depth_stencil);
  plan.relevant = static_cast<uint32_t>(
      std::min<size_t>(matched_buckets.size(), UINT32_MAX));
  const std::unordered_map<uint32_t, uint64_t>& attempts =
      DirectSurfaceDestinationBucketAttemptsThisFrameLocked();
  plan.buckets.reserve(matched_buckets.size());
  for (uint32_t bucket : matched_buckets) {
    const auto it = attempts.find(bucket);
    if (it == attempts.end() || it->second != plan.generation) {
      plan.buckets.push_back(bucket);
    }
  }
  return plan;
}

uint32_t RecordDirectSurfaceDestinationCopiesLocked(
    plume::RenderCommandList* command_list, bool reset_stats,
    const DirectSurfaceDestinationScanPlan* scan_plan) {
  if (reset_stats) {
    ResetDirectSurfaceDestinationStatsLocked();
  }

  if (!command_list) {
    return 0;
  }

  ++g_direct_surface_destination_split.calls;
  const FableDirectClock::time_point sd_t0 = FableDirectClock::now();
  const bool targeted_scan = scan_plan != nullptr && !scan_plan->global;
  const FableSurfaceDestinationRelationSnapshot snapshot =
      targeted_scan
          ? FableDirectGetSurfaceDestinationRelationSnapshotForBuckets(
                scan_plan->buckets, 512, g_direct_renderer.current_frame_index)
          : FableDirectGetSurfaceDestinationRelationSnapshot(
                512, g_direct_renderer.current_frame_index);
  const FableDirectClock::time_point sd_snapshot_t1 = FableDirectClock::now();
  g_direct_surface_destination_split.snapshot_ms +=
      FableDirectElapsedMs(sd_t0, sd_snapshot_t1);
  // Mark visited buckets attempted at the planned generation so later draws
  // matching the same buckets skip until the registry content moves again.
  // Global runs mark every bucket they visited (the pre-draw compose run
  // thereby pre-marks the whole population, so draw 1's targeted plan is
  // usually empty). Marks are advisory only — a lost mark re-runs a cheap
  // targeted scan, a stale one is refreshed on the next generation change.
  {
    std::unordered_map<uint32_t, uint64_t>& attempts =
        DirectSurfaceDestinationBucketAttemptsThisFrameLocked();
    const uint64_t mark_generation =
        scan_plan != nullptr
            ? scan_plan->generation
            : FableDirectGetSurfaceDestinationRelationGeneration();
    if (targeted_scan) {
      for (uint32_t bucket : scan_plan->buckets) {
        attempts[bucket] = mark_generation;
      }
    } else {
      for (const FableSurfaceDestinationTextureRelation& relation :
           snapshot.relations) {
        attempts[relation.destination_texture] = mark_generation;
      }
    }
  }
  if (scan_plan != nullptr) {
    // g_direct_draw_phases is defined below this function; accumulate through
    // the forward-declared helper.
    AccumulateDestScanCensus(ClampCounter(snapshot.relations.size()),
                             scan_plan->relevant);
  }
  if (!targeted_scan) {
    // Targeted snapshots see only the planned buckets; overwriting the
    // population census with that slice would misreport the relation bank.
    g_direct_renderer.stats.surface_destination_relations =
        ClampCounter(snapshot.relations.size());
    g_direct_renderer.stats.surface_destination_omitted_relations =
        snapshot.omitted_relations;
  }
  const bool surface_destination_watch_active =
      IsRenderDocSurfaceDestinationCaptureTarget(
          RenderDocDeepObserverCaptureDrawTarget());
  if (snapshot.relations.empty()) {
    // An empty TARGETED snapshot is the expected common case (nothing the
    // draw observes changed); only the global walk's emptiness is worth a
    // diagnostic line.
    if (surface_destination_watch_active && !targeted_scan) {
      static uint32_t logged_empty_surface_destination_scans = 0;
      if (logged_empty_surface_destination_scans < 16u ||
          IsPowerOfTwo(g_direct_renderer.current_frame_index)) {
        REXLOG_INFO(
            "Refii plume surface destination scan empty #{} frame={} "
            "relations=0 omitted={}",
            ++logged_empty_surface_destination_scans,
            g_direct_renderer.current_frame_index, snapshot.omitted_relations);
      }
    }
    return 0;
  }

  std::unordered_set<uint32_t> relation_resource_filter;
  std::unordered_set<uint32_t> relation_texture_upload_filter;
  auto add_relation_interest = [](std::unordered_set<uint32_t>& interests,
                                  uint32_t address) {
    if (address != 0) {
      interests.insert(address);
    }
  };
  auto add_relation_identity_interest =
      [&](const FableTextureIdentity* identity) {
        if (!identity) {
          return;
        }
        add_relation_interest(relation_resource_filter,
                              identity->object_texture);
        add_relation_interest(relation_texture_upload_filter,
                              identity->object_texture);
        add_relation_interest(relation_resource_filter,
                              identity->parent_texture);
        add_relation_interest(relation_texture_upload_filter,
                              identity->parent_texture);
        add_relation_interest(relation_texture_upload_filter,
                              identity->base_address);
      };
  auto add_relation_identity_objects_interest =
      [&](const std::vector<uint32_t>* objects) {
        if (!objects) {
          return;
        }
        for (uint32_t object : *objects) {
          add_relation_interest(relation_resource_filter, object);
          add_relation_interest(relation_texture_upload_filter, object);
          add_relation_identity_interest(
              FableDirectFindTextureIdentityByObject(object));
        }
      };
  auto add_relation_endpoint_interest = [&](uint32_t address) {
    add_relation_interest(relation_resource_filter, address);
    add_relation_interest(relation_texture_upload_filter, address);
    add_relation_identity_interest(
        FableDirectFindTextureIdentityByObject(address));
    add_relation_identity_interest(
        FableDirectFindTextureIdentityByBase(address));
    add_relation_identity_objects_interest(
        FableDirectFindTextureIdentityObjectsByBase(address));
  };
  auto add_relation_fetch_interest =
      [&](const std::array<uint32_t, 6>& fetch_dwords) {
        add_relation_identity_interest(
            FableDirectFindTextureIdentityByFetch(fetch_dwords));
        add_relation_identity_objects_interest(
            FableDirectFindTextureIdentityObjectsByFetch(fetch_dwords));
      };
  auto relation_has_replay_identity =
      [](const FableSurfaceDestinationTextureRelation& relation) {
        if (relation.link.copy_call_serial != 0 ||
            relation.link.copy_symbol_address != 0 ||
            relation.link.copy_context_key != 0) {
          return true;
        }
        if (!relation.link.trusted_destination_edge) {
          return false;
        }
        if (relation.link.wrapper_source_exact ||
            DirectSurfaceTextureLinkHasExactCopyPayloadIdentity(relation.link)) {
          return true;
        }
        const bool has_texture_identity =
            relation.link.texture_base_address != 0 ||
            FableDirectTextureFetchKey(relation.link.texture_fetch_dwords) != 0;
        return has_texture_identity &&
               (relation.link.target_content_serial != 0 ||
                relation.link.backing_content_serial != 0);
      };
  for (const FableSurfaceDestinationTextureRelation& relation :
       snapshot.relations) {
    if (DirectSurfaceDestinationCopyRecordedThisFrameLocked(relation)) {
      continue;
    }
    if (relation.source_surface == 0 || relation.destination_texture == 0 ||
        relation.source_surface == relation.destination_texture) {
      continue;
    }
    if (!relation_has_replay_identity(relation)) {
      continue;
    }
    if (relation.link.frame_index != 0 &&
        relation.link.frame_index != g_direct_renderer.current_frame_index) {
      continue;
    }
    add_relation_endpoint_interest(relation.source_surface);
    add_relation_endpoint_interest(relation.destination_texture);
    add_relation_endpoint_interest(relation.link.source_surface);
    add_relation_endpoint_interest(relation.link.texture);
    add_relation_endpoint_interest(relation.link.backing_texture);
    add_relation_endpoint_interest(relation.link.texture_base_address);
    add_relation_endpoint_interest(relation.link.backing_base_address);
    add_relation_endpoint_interest(relation.link.sampled_source.resource);
    add_relation_endpoint_interest(relation.link.sampled_source.base_address);
    add_relation_fetch_interest(relation.link.texture_fetch_dwords);
    add_relation_fetch_interest(relation.link.backing_fetch_dwords);
    add_relation_fetch_interest(relation.link.sampled_source.fetch_dwords);
  }
  const FableDirectClock::time_point sd_interest_t1 = FableDirectClock::now();
  g_direct_surface_destination_split.interest_ms +=
      FableDirectElapsedMs(sd_snapshot_t1, sd_interest_t1);
  // RUN-487 FREEZE FIX: this sync ran with FULL endpoint filters (resource +
  // upload) on EVERY run — 4.66s/compose across 165 generation-churned runs
  // at the storybook menu (sdsplit sync=). Full-filter refresh is the
  // compose-level syncs' job; per-draw runs get it once per compose (first
  // run keeps the old behavior as the per-compose backstop), and subsequent
  // runs restrict the filters to endpoints with NO ready record —
  // materialization pressure only, no per-run full-population upload refresh.
  // Persistent never-materialized endpoint cache, shared by this run's two
  // syncs (declared here, incremented at the catch-up below): an address that
  // failed to materialize twice is dead (recycled panel header) until the
  // relation generation moves. Without it every run re-synced the same dead
  // set — run 488: sync=406ms + catchup=1235ms across 9 capped runs.
  static std::unordered_map<uint32_t, uint32_t> sd_endpoint_failed_attempts;
  static uint64_t sd_endpoint_cache_generation = 0;
  {
    const uint64_t current_generation =
        FableDirectGetSurfaceDestinationRelationGeneration();
    if (sd_endpoint_cache_generation != current_generation ||
        sd_endpoint_failed_attempts.size() > 8192u) {
      sd_endpoint_cache_generation = current_generation;
      sd_endpoint_failed_attempts.clear();
    }
  }
  const auto sd_drop_dead_readonly = [](std::unordered_set<uint32_t>& filter) {
    for (auto it = filter.begin(); it != filter.end();) {
      const auto found = sd_endpoint_failed_attempts.find(*it);
      if (found != sd_endpoint_failed_attempts.end() && found->second >= 2u) {
        it = filter.erase(it);
      } else {
        ++it;
      }
    }
  };
  {
    static uint32_t full_sync_frame = UINT32_MAX;
    const bool first_run_this_compose =
        full_sync_frame != g_direct_renderer.current_frame_index;
    if (first_run_this_compose) {
      full_sync_frame = g_direct_renderer.current_frame_index;
    }
    if (!first_run_this_compose) {
      auto keep_missing_only = [](std::unordered_set<uint32_t>& filter) {
        for (auto it = filter.begin(); it != filter.end();) {
          const PlumeTrackedResource* record = FindDirectResourceLocked(*it);
          if (record != nullptr && PlumeGuestImageReady(*record)) {
            it = filter.erase(it);
          } else {
            ++it;
          }
        }
      };
      keep_missing_only(relation_resource_filter);
      keep_missing_only(relation_texture_upload_filter);
      sd_drop_dead_readonly(relation_resource_filter);
      sd_drop_dead_readonly(relation_texture_upload_filter);
    }
    if (!relation_resource_filter.empty() ||
        !relation_texture_upload_filter.empty()) {
      SyncDirectResourcesLocked(g_direct_renderer.device, command_list,
                                &relation_texture_upload_filter,
                                &relation_resource_filter, false);
    }
  }
  const FableDirectClock::time_point sd_sync_t1 = FableDirectClock::now();
  g_direct_surface_destination_split.sync_ms +=
      FableDirectElapsedMs(sd_interest_t1, sd_sync_t1);
  std::unordered_set<uint32_t> missing_relation_resource_filter;
  std::unordered_set<uint32_t> missing_relation_texture_upload_filter;
  for (const FableSurfaceDestinationTextureRelation& relation :
       snapshot.relations) {
    if (DirectSurfaceDestinationCopyRecordedThisFrameLocked(relation)) {
      continue;
    }
    if (relation.source_surface == 0 || relation.destination_texture == 0 ||
        relation.source_surface == relation.destination_texture) {
      continue;
    }
    if (!relation_has_replay_identity(relation)) {
      continue;
    }
    if (relation.link.frame_index != 0 &&
        relation.link.frame_index != g_direct_renderer.current_frame_index) {
      continue;
    }
    if (!relation.link.trusted_destination_edge) {
      continue;
    }
    const PlumeTrackedResource* source =
        FindDirectResourceLocked(relation.source_surface);
    const PlumeTrackedResource* target =
        FindDirectResourceLocked(relation.destination_texture);
    if (source && target) {
      continue;
    }
    auto add_missing_endpoint_interest = [&](uint32_t address) {
      add_relation_interest(missing_relation_resource_filter, address);
      add_relation_interest(missing_relation_texture_upload_filter, address);
      if (const FableTextureIdentity* identity =
              FableDirectFindTextureIdentityByObject(address)) {
        add_relation_interest(missing_relation_resource_filter,
                              identity->object_texture);
        add_relation_interest(missing_relation_texture_upload_filter,
                              identity->object_texture);
        add_relation_interest(missing_relation_resource_filter,
                              identity->parent_texture);
        add_relation_interest(missing_relation_texture_upload_filter,
                              identity->parent_texture);
        add_relation_interest(missing_relation_texture_upload_filter,
                              identity->base_address);
      }
      if (const FableTextureIdentity* identity =
              FableDirectFindTextureIdentityByBase(address)) {
        add_relation_interest(missing_relation_resource_filter,
                              identity->object_texture);
        add_relation_interest(missing_relation_texture_upload_filter,
                              identity->object_texture);
        add_relation_interest(missing_relation_resource_filter,
                              identity->parent_texture);
        add_relation_interest(missing_relation_texture_upload_filter,
                              identity->parent_texture);
        add_relation_interest(missing_relation_texture_upload_filter,
                              identity->base_address);
      }
    };
    add_missing_endpoint_interest(relation.source_surface);
    add_missing_endpoint_interest(relation.destination_texture);
    add_missing_endpoint_interest(relation.link.source_surface);
    add_missing_endpoint_interest(relation.link.texture);
    add_missing_endpoint_interest(relation.link.backing_texture);
    add_missing_endpoint_interest(relation.link.texture_base_address);
    add_missing_endpoint_interest(relation.link.backing_base_address);
    add_missing_endpoint_interest(relation.link.sampled_source.resource);
    add_missing_endpoint_interest(relation.link.sampled_source.base_address);
  }
  // RUN-487 FREEZE FIX: this BLOCKING catch-up re-requested the same
  // never-materializing endpoints on every run — 7.8s/compose across 165 runs
  // (sdsplit catchup=), ~47ms of observer block each, for addresses that can
  // never materialize (stale relation endpoints on recycled panel headers).
  // Block once per address per compose; repeats are pure wait for a known
  // answer. The next compose retries fresh. (This used to say it mirrored the
  // sync-stage `starved_blocking_retried` memo; that memo was one-shot-forever
  // and became a 4-attempt budget in 2026-08-03 — run 550 showed its single
  // attempt being spent on a transient `busy=1` contention failure and the
  // address then starved permanently. This catch-up is per-compose and is NOT
  // affected.)
  {
    // RUN-488: the per-compose attempt memo reset every compose, so each
    // compose re-paid first-attempt waits for the SAME never-materializing
    // endpoints (recycled panel headers). Uses the shared persistent negative
    // cache declared above the first sync; attempts are counted HERE (the
    // missing pass is the authority on "still missing after a sync").
    auto drop_known_dead = [&](std::unordered_set<uint32_t>& filter) {
      for (auto it = filter.begin(); it != filter.end();) {
        auto& attempts = sd_endpoint_failed_attempts[*it];
        if (attempts >= 2u) {
          it = filter.erase(it);
        } else {
          ++attempts;
          ++it;
        }
      }
    };
    drop_known_dead(missing_relation_resource_filter);
    drop_known_dead(missing_relation_texture_upload_filter);
  }
  if (!missing_relation_resource_filter.empty() ||
      !missing_relation_texture_upload_filter.empty()) {
    // Non-blocking (was wait_for_observer=true): the blocking variant's only
    // value is one-compose-earlier materialization; for live endpoints the
    // next compose's sync picks them up, for dead recycled headers blocking
    // was pure wait (run 487: 7.8s/compose; run 488: still 1.2s).
    SyncDirectResourcesLocked(
        g_direct_renderer.device, command_list,
        &missing_relation_texture_upload_filter,
        &missing_relation_resource_filter, false);
    static uint32_t logged_surface_destination_observer_catchups = 0;
    if ((surface_destination_watch_active || RendererDeepObserverHooksEnabled()) &&
        logged_surface_destination_observer_catchups < 32u) {
      REXLOG_INFO(
          "Refii plume surface destination observer catch-up #{} frame={} "
          "resources={} uploads={} relations={}",
          ++logged_surface_destination_observer_catchups,
          g_direct_renderer.current_frame_index,
          missing_relation_resource_filter.size(),
          missing_relation_texture_upload_filter.size(),
          snapshot.relations.size());
    }
  }

  const FableDirectClock::time_point sd_catchup_t1 = FableDirectClock::now();
  // catchup includes the wait_for_observer=true sync above: it drops the
  // registry lock and BLOCKS on the observer, so it is the only piece of
  // this function whose cost is contention-shaped rather than size-shaped.
  g_direct_surface_destination_split.catchup_ms +=
      FableDirectElapsedMs(sd_sync_t1, sd_catchup_t1);
  ZoneScopedN("RefiiPlume/SurfaceDestinationCopies");
  PlumeDirectDebugLabelScope surface_destination_scope(
      command_list, "RefiiRenderer::SurfaceDestinationCopies");
  uint32_t copied_exact = 0;
  uint32_t copied_scaled = 0;
  uint32_t skipped = 0;
  uint32_t skipped_invalid = 0;
  uint32_t skipped_zero_endpoint = 0;
  uint32_t skipped_missing_copy_root = 0;
  uint32_t skipped_wrong_frame = 0;
  uint32_t skipped_missing_resource = 0;
  uint32_t skipped_not_image = 0;
  uint32_t skipped_not_ready = 0;
  uint32_t skipped_same_texture = 0;
  uint32_t skipped_inexact_wrapper = 0;
  uint32_t skipped_stale_content = 0;
  uint32_t skipped_copy_failed = 0;
  static uint32_t logged_surface_destination_relation_exclusions = 0;
  const bool surface_destination_relation_detail_enabled =
      surface_destination_watch_active || RendererDeepObserverHooksEnabled();
  auto log_surface_destination_relation_exclusion =
      [&](const char* reason,
          const FableSurfaceDestinationTextureRelation& relation,
          const PlumeTrackedResource* source,
          const PlumeTrackedResource* target, uint64_t source_content) {
        if (!surface_destination_relation_detail_enabled) {
          return;
        }
        const uint32_t log_limit =
            surface_destination_watch_active ? 256u : 64u;
        if (logged_surface_destination_relation_exclusions >= log_limit) {
          return;
        }
        const uint32_t source_width =
            source ? PlumeGuestTextureWidth(*source) : 0u;
        const uint32_t source_height =
            source ? PlumeGuestTextureHeight(*source) : 0u;
        const uint32_t target_width =
            target ? PlumeGuestTextureWidth(*target) : 0u;
        const uint32_t target_height =
            target ? PlumeGuestTextureHeight(*target) : 0u;
        REXLOG_INFO(
            "Refii plume surface destination relation exclusion #{} frame={} "
            "reason={} relation={} source=0x{:08X}/{}x{} "
            "target=0x{:08X}/{}x{} slot={} link_texture=0x{:08X} "
            "backing=0x{:08X} ready={}/{} content={}/{} backing_content={} "
            "call={} context=0x{:016X} symbol=0x{:08X} draw={} "
            "trusted={} wrapper_exact={}",
            ++logged_surface_destination_relation_exclusions,
            g_direct_renderer.current_frame_index,
            reason != nullptr ? reason : "unknown",
            static_cast<unsigned long long>(relation.serial),
            relation.source_surface, source_width, source_height,
            relation.destination_texture, target_width, target_height,
            relation.source_slot, relation.link.texture,
            relation.link.backing_texture,
            source && PlumeGuestImageReady(*source) ? 1 : 0,
            target && PlumeGuestImageReady(*target) ? 1 : 0,
            static_cast<unsigned long long>(source_content),
            static_cast<unsigned long long>(
                relation.link.target_content_serial),
            static_cast<unsigned long long>(
                relation.link.backing_content_serial),
            static_cast<unsigned long long>(relation.link.copy_call_serial),
            static_cast<unsigned long long>(relation.link.copy_context_key),
            relation.link.copy_symbol_address,
            static_cast<unsigned long long>(relation.link.draw_serial),
            relation.link.trusted_destination_edge ? 1 : 0,
            relation.link.wrapper_source_exact ? 1 : 0);
      };
  auto surface_texture_link_visible_to_relation =
      [&](const FableSurfaceTextureLink& link,
          const FableSurfaceDestinationTextureRelation& relation) {
        if (!link.trusted_destination_edge) {
          return false;
        }
        if (link.frame_index != 0 &&
            link.frame_index > g_direct_renderer.current_frame_index) {
          return false;
        }
        const uint64_t relation_draw_serial =
            relation.link.draw_serial != 0 ? relation.link.draw_serial
                                           : UINT64_MAX;
        if (link.frame_index != 0 &&
            link.frame_index == g_direct_renderer.current_frame_index &&
            link.draw_serial != 0 && relation_draw_serial != 0 &&
            link.draw_serial > relation_draw_serial) {
          return false;
        }
        constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
        if (link.source_slot != kUnknownSlot &&
            relation.source_slot != kUnknownSlot &&
            link.source_slot != relation.source_slot) {
          return false;
        }
        return true;
      };
  auto surface_texture_link_matches_payload =
      [](const FableSurfaceTextureLink& link,
         const PlumeTrackedResource& resource) {
        const uint32_t base = PlumeGuestTextureBaseAddress(resource);
        const uint64_t resource_fetch_key =
            FableDirectTextureFetchKey(PlumeGuestTextureFetchDwords(resource));
        const uint64_t sampled_fetch_key =
            FableDirectTextureFetchKey(link.sampled_source.fetch_dwords);
        const bool sampled_producer_surface =
            link.sampled_source.kind ==
                FableRegisteredSampleSourceKind::kGpuSurface &&
            link.sampled_source.resource != 0 && link.source_surface != 0 &&
            DirectResourceAddressMatchesLocked(link.sampled_source.resource,
                                               link.source_surface);
        return !sampled_producer_surface &&
               ((link.sampled_source.resource != 0 &&
                resource.guest_address == link.sampled_source.resource) ||
               (link.sampled_source.base_address != 0 &&
                base == link.sampled_source.base_address) ||
               (sampled_fetch_key != 0 &&
                resource_fetch_key == sampled_fetch_key));
      };
  auto source_surface_link_matches_payload =
      [&](const FableSurfaceDestinationTextureRelation& relation,
          const PlumeTrackedResource& resource) {
        const std::vector<FableSurfaceTextureLink>* links =
            FableDirectFindSurfaceTextureLinksBySurface(
                relation.source_surface);
        if (!links) {
          return false;
        }
        for (const FableSurfaceTextureLink& link : *links) {
          if (surface_texture_link_visible_to_relation(link, relation) &&
              surface_texture_link_matches_payload(link, resource)) {
            return true;
          }
        }
        return false;
      };
  auto resolve_surface_destination_endpoint =
      [&](const std::array<uint32_t, 8>& addresses,
          const std::array<std::array<uint32_t, 6>, 2>& fetches,
          const FableSurfaceDestinationTextureRelation& relation,
          bool target) -> PlumeTrackedResource* {
        struct SurfaceDestinationEndpointCandidate {
          PlumeTrackedResource* resource = nullptr;
          uint32_t rank = UINT32_MAX;
          bool direct_address = false;
        };

        std::array<SurfaceDestinationEndpointCandidate, 128> candidates = {};
        uint32_t candidate_count = 0;
        auto add_candidate = [&](PlumeTrackedResource* resource, uint32_t rank,
                                 bool direct_address) {
          if (!resource) {
            return;
          }
          for (uint32_t i = 0; i < candidate_count; ++i) {
            if (candidates[i].resource == resource) {
              candidates[i].rank = std::min(candidates[i].rank, rank);
              candidates[i].direct_address =
                  candidates[i].direct_address || direct_address;
              return;
            }
          }
          if (candidate_count < candidates.size()) {
            candidates[candidate_count++] = {resource, rank, direct_address};
          }
        };
        auto add_identity_candidates = [&](const FableTextureIdentity* identity,
                                           uint32_t rank) {
          if (!identity) {
            return;
          }
          add_candidate(FindDirectResourceLocked(identity->object_texture), rank,
                        false);
          add_candidate(FindDirectResourceLocked(identity->parent_texture), rank,
                        false);
          add_candidate(FindDirectResourceLocked(identity->base_address), rank,
                        false);
        };
        auto add_identity_object_candidates =
            [&](const std::vector<uint32_t>* objects, uint32_t rank) {
              if (!objects) {
                return;
              }
              for (uint32_t object : *objects) {
                add_candidate(FindDirectResourceLocked(object), rank, false);
                add_identity_candidates(
                    FableDirectFindTextureIdentityByObject(object), rank);
              }
            };
        for (uint32_t address_index = 0; address_index < addresses.size();
             ++address_index) {
          const uint32_t address = addresses[address_index];
          if (address == 0) {
            continue;
          }
          add_candidate(FindDirectResourceLocked(address), address_index, true);
          add_identity_candidates(FableDirectFindTextureIdentityByObject(address),
                                  address_index);
          add_identity_candidates(FableDirectFindTextureIdentityByBase(address),
                                  address_index);
          add_identity_object_candidates(
              FableDirectFindTextureIdentityObjectsByBase(address),
              address_index);
        }
        for (uint32_t fetch_index = 0; fetch_index < fetches.size();
             ++fetch_index) {
          const std::array<uint32_t, 6>& fetch = fetches[fetch_index];
          const uint32_t rank =
              static_cast<uint32_t>(addresses.size() + fetch_index);
          add_identity_candidates(FableDirectFindTextureIdentityByFetch(fetch),
                                  rank);
          add_identity_object_candidates(
              FableDirectFindTextureIdentityObjectsByFetch(fetch), rank);
        }
        PlumeTrackedResource* best = nullptr;
        uint64_t best_score = 0;
        for (uint32_t i = 0; i < candidate_count; ++i) {
          PlumeTrackedResource* candidate = candidates[i].resource;
          if (!candidate || !IsImageResource(candidate->kind) ||
              !PlumeGuestImageReady(*candidate) ||
              (target && candidate->texture_depth_target)) {
            continue;
          }
          const uint64_t content = FableTrackedResourceVisibleContentSerial(*candidate);
          const uint64_t area =
              uint64_t(PlumeGuestTextureWidth(*candidate)) *
              uint64_t(PlumeGuestTextureHeight(*candidate));
          const uint32_t base = PlumeGuestTextureBaseAddress(*candidate);
          const bool direct_exact = candidates[i].direct_address;
          const bool primary_exact =
              direct_exact && candidate->guest_address == addresses[0];
          const bool relation_target =
              target && candidate->guest_address == relation.destination_texture;
          const bool relation_source =
              !target && candidate->guest_address == relation.source_surface;
          const bool sampled_resource =
              !target &&
              ((relation.link.sampled_source.resource != 0 &&
                candidate->guest_address ==
                    relation.link.sampled_source.resource) ||
               (relation.link.sampled_source.base_address != 0 &&
                base == relation.link.sampled_source.base_address));
          const bool backing_resource =
              !target &&
              ((relation.link.backing_texture != 0 &&
                candidate->guest_address == relation.link.backing_texture) ||
               (relation.link.backing_base_address != 0 &&
                base == relation.link.backing_base_address));
          const bool surface_linked_payload =
              !target &&
              source_surface_link_matches_payload(relation, *candidate);
          const bool authored_source =
              !target && DirectSurfaceDestinationSourceHasDrawnOrAuthoredPayload(
                             *candidate);
          const bool required_content =
              target || relation.link.target_content_serial == 0 ||
              content >= relation.link.target_content_serial;

          uint64_t score = 1;
          if (target) {
            score += relation_target ? 1ull << 54 : 0;
            score += primary_exact ? 1ull << 53 : 0;
            score += direct_exact ? 1ull << 52 : 0;
            score += candidate->texture_render_target ? 1ull << 51 : 0;
            score += IsFablePresentTargetCandidate(
                         candidate->guest_address,
                         PlumeGuestTextureWidth(*candidate),
                         PlumeGuestTextureHeight(*candidate))
                         ? 1ull << 50
                         : 0;
          } else {
            score += required_content ? 1ull << 54 : 0;
            score += surface_linked_payload ? 1ull << 55 : 0;
            score += authored_source ? 1ull << 53 : 0;
            score += sampled_resource ? 1ull << 52 : 0;
            score += backing_resource ? 1ull << 51 : 0;
            score += relation_source ? 1ull << 50 : 0;
            score += primary_exact ? 1ull << 49 : 0;
            score += candidate->texture_render_target ? 1ull << 48 : 0;
          }
          score += (uint64_t(64u - std::min(candidates[i].rank, 63u)) << 40);
          score += (std::min<uint64_t>(content, 0xFFFFFFFFull) << 8);
          score += std::min<uint64_t>(area, 0xFFull);
          if (!best || score > best_score) {
            best = candidate;
            best_score = score;
          }
        }
        return best;
      };
  for (const FableSurfaceDestinationTextureRelation& relation :
       snapshot.relations) {
    if (DirectSurfaceDestinationCopyRecordedThisFrameLocked(relation)) {
      continue;
    }
    if (relation.source_surface == 0 || relation.destination_texture == 0) {
      ++skipped_invalid;
      ++skipped_zero_endpoint;
      ++skipped;
      log_surface_destination_relation_exclusion("invalid", relation, nullptr,
                                            nullptr, 0);
      continue;
    }
    if (relation.source_surface == relation.destination_texture) {
      ++skipped_same_texture;
      ++skipped;
      log_surface_destination_relation_exclusion("same_texture", relation, nullptr,
                                            nullptr, 0);
      continue;
    }
    if (!relation_has_replay_identity(relation)) {
      ++skipped_invalid;
      ++skipped_missing_copy_root;
      ++skipped;
      log_surface_destination_relation_exclusion("missing_copy_root", relation,
                                            nullptr, nullptr, 0);
      continue;
    }
    const bool trusted_payload_replay =
        relation.link.trusted_destination_edge &&
        relation_has_replay_identity(relation);
    if (!relation.link.wrapper_source_exact && !trusted_payload_replay) {
      ++skipped_inexact_wrapper;
      ++skipped;
      log_surface_destination_relation_exclusion("inexact_wrapper", relation, nullptr,
                                            nullptr, 0);
      continue;
    }
    if (relation.link.frame_index != 0 &&
        relation.link.frame_index != g_direct_renderer.current_frame_index) {
      ++skipped_wrong_frame;
      ++skipped;
      log_surface_destination_relation_exclusion("wrong_frame", relation, nullptr,
                                            nullptr, 0);
      continue;
    }
    const bool exact_color_payload_replay =
        relation.link.trusted_destination_edge &&
        relation.link.wrapper_source_exact &&
        DirectPrivateTextureCopyKindUsesColorBacking(relation.link.kind);
    const bool sampled_producer_surface =
        exact_color_payload_replay &&
        relation.link.sampled_source.kind ==
            FableRegisteredSampleSourceKind::kGpuSurface &&
        relation.link.sampled_source.resource != 0 &&
        relation.link.source_surface != 0 &&
        DirectResourceAddressMatchesLocked(relation.link.sampled_source.resource,
                                           relation.link.source_surface);
    const uint32_t producer_surface_candidate =
        sampled_producer_surface ? relation.link.source_surface : 0u;
    const uint32_t sampled_resource_candidate =
        sampled_producer_surface ? 0u : relation.link.sampled_source.resource;
    const uint32_t sampled_base_candidate =
        sampled_producer_surface ? 0u : relation.link.sampled_source.base_address;
    const std::array<uint32_t, 6> sampled_fetch_candidate =
        sampled_producer_surface ? std::array<uint32_t, 6>{}
                                 : relation.link.sampled_source.fetch_dwords;
    const std::array<uint32_t, 8> source_candidates =
        exact_color_payload_replay
            ? std::array<uint32_t, 8>{
                  producer_surface_candidate,
                  sampled_resource_candidate,
                  sampled_base_candidate,
                  relation.link.backing_texture,
                  relation.link.backing_base_address,
                  relation.link.texture_base_address,
                  0u,
                  0u,
              }
            : std::array<uint32_t, 8>{
                  relation.source_surface,
                  relation.link.source_surface,
                  relation.link.sampled_source.resource,
                  relation.link.sampled_source.base_address,
                  relation.link.render_target,
                  0u,
                  0u,
                  0u,
              };
    const std::array<uint32_t, 8> target_candidates = {
        relation.destination_texture,
        relation.link.texture,
        relation.link.texture_base_address,
        relation.link.backing_texture,
        relation.link.backing_base_address,
        0u,
        0u,
        0u,
    };
    const std::array<std::array<uint32_t, 6>, 2> source_fetches = {
        sampled_fetch_candidate,
        exact_color_payload_replay ? relation.link.backing_fetch_dwords
                                   : std::array<uint32_t, 6>{},
    };
    const std::array<std::array<uint32_t, 6>, 2> target_fetches = {
        relation.link.texture_fetch_dwords,
        relation.link.backing_fetch_dwords,
    };
    PlumeTrackedResource* source =
        resolve_surface_destination_endpoint(source_candidates, source_fetches,
                                             relation, false);
    PlumeTrackedResource* target =
        resolve_surface_destination_endpoint(target_candidates, target_fetches,
                                             relation, true);
    if (!source || !target) {
      ++skipped_missing_resource;
      ++skipped;
      log_surface_destination_relation_exclusion("missing_resource", relation,
                                            source, target, 0);
      continue;
    }
    if (!IsImageResource(source->kind) || !IsImageResource(target->kind)) {
      ++skipped_not_image;
      ++skipped;
      log_surface_destination_relation_exclusion("not_image", relation, source,
                                            target, 0);
      continue;
    }
    if (!PlumeGuestImageReady(*source) || !PlumeGuestImageReady(*target)) {
      ++skipped_not_ready;
      ++skipped;
      log_surface_destination_relation_exclusion("not_ready", relation, source,
                                            target, 0);
      continue;
    }
    if (source->guest_texture.texture.get() ==
        target->guest_texture.texture.get()) {
      ++skipped_same_texture;
      ++skipped;
      log_surface_destination_relation_exclusion("same_texture", relation, source,
                                            target, 0);
      continue;
    }

    const uint64_t source_content = FableTrackedResourceVisibleContentSerial(*source);
    if (relation.link.target_content_serial != 0 &&
        source_content < relation.link.target_content_serial &&
        !source_surface_link_matches_payload(relation, *source)) {
      ++skipped_stale_content;
      ++skipped;
      log_surface_destination_relation_exclusion("stale_content", relation, source,
                                            target, source_content);
      continue;
    }
    bool copied = false;
    FableDirectFrameDatabaseCommandPacket surface_destination_packet =
        BuildFrameDatabaseSurfaceDestinationCommandPacketLocked(
            relation, *source, *target,
            !DirectTextureCopySizeMatches(*source, *target));
    const RefiiFrameActionPacket surface_destination_action =
        BuildRefiiSurfaceDestinationActionPacket(
            g_direct_renderer.refii_packet_queue.frame_id,
            surface_destination_packet);
    bool scaled_copy = false;
    copied = RecordDirectFrameActionPacketLocked(
        g_direct_renderer.device, command_list, nullptr, nullptr,
        g_direct_renderer.shader_format, surface_destination_action, &relation,
        source, target, nullptr, &scaled_copy);
    if (scaled_copy) {
      copied_scaled += copied ? 1u : 0u;
    } else {
      copied_exact += copied ? 1u : 0u;
    }
    if (!copied) {
      ++skipped_copy_failed;
      ++skipped;
      log_surface_destination_relation_exclusion("copy_failed", relation, source,
                                            target, source_content);
    }
    if (copied) {
      RememberDirectSurfaceDestinationCopyThisFrameLocked(relation);
      AppendFrameDatabaseSurfaceDestinationCommandPacketLocked(
          std::move(surface_destination_packet));
      AppendFrameDatabaseSurfaceDestinationResolvePacketLocked(relation, *source,
                                                               *target);
    }
  }
  g_direct_renderer.stats.surface_destination_copied_exact += copied_exact;
  g_direct_renderer.stats.surface_destination_copied_scaled += copied_scaled;
  if (reset_stats) {
    g_direct_renderer.stats.surface_destination_skipped_invalid +=
        skipped_invalid;
    g_direct_renderer.stats.surface_destination_skipped_zero_endpoint +=
        skipped_zero_endpoint;
    g_direct_renderer.stats.surface_destination_skipped_missing_copy_root +=
        skipped_missing_copy_root;
    g_direct_renderer.stats.surface_destination_skipped_wrong_frame +=
        skipped_wrong_frame;
    g_direct_renderer.stats.surface_destination_skipped_missing_resource +=
        skipped_missing_resource;
    g_direct_renderer.stats.surface_destination_skipped_not_image +=
        skipped_not_image;
    g_direct_renderer.stats.surface_destination_skipped_not_ready +=
        skipped_not_ready;
    g_direct_renderer.stats.surface_destination_skipped_same_texture +=
        skipped_same_texture;
    g_direct_renderer.stats.surface_destination_skipped_inexact_wrapper +=
        skipped_inexact_wrapper;
    g_direct_renderer.stats.surface_destination_skipped_stale_content +=
        skipped_stale_content;
    g_direct_renderer.stats.surface_destination_skipped_copy_failed +=
        skipped_copy_failed;
  }

  if (copied_exact != 0 || copied_scaled != 0 || surface_destination_watch_active) {
    static uint32_t logged_surface_destination_copies = 0;
    const uint32_t log_limit =
        (surface_destination_watch_active || RendererDeepObserverHooksEnabled())
            ? 96u
            : 24u;
    if (logged_surface_destination_copies < log_limit) {
      REXLOG_INFO(
          "Refii plume surface destination copy exclusions #{} frame={} "
          "copied_exact={} copied_scaled={} excluded={} invalid={} "
          "zero_endpoint={} missing_copy_root={} wrong_frame={} "
          "missing_resource={} not_image={} not_ready={} same_texture={} "
          "inexact_wrapper={} stale_content={} copy_failed={} relations={} "
          "omitted={}",
          ++logged_surface_destination_copies,
          g_direct_renderer.current_frame_index, copied_exact, copied_scaled,
          skipped, skipped_invalid, skipped_zero_endpoint,
          skipped_missing_copy_root, skipped_wrong_frame,
          skipped_missing_resource, skipped_not_image, skipped_not_ready,
          skipped_same_texture, skipped_inexact_wrapper, skipped_stale_content,
          skipped_copy_failed, snapshot.relations.size(),
          snapshot.omitted_relations);
    }
  }
  g_direct_surface_destination_split.record_ms +=
      FableDirectElapsedMs(sd_catchup_t1, FableDirectClock::now());
  return copied_exact + copied_scaled;
}

bool DirectCommandSamplesSurfaceAliasDestinationLocked(
    const FableDirectRenderCommand& command,
    const RendererSurfaceCopyAliasSnapshot& alias) {
  const PlumeTrackedResource* destination =
      FindDirectResourceLocked(alias.destination_surface);
  const uint32_t texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(command);
  for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
    if (!DirectCommandUsesTextureSlotForSyncLocked(command, texture_slot_mask,
                                                   slot)) {
      continue;
    }
    const uint32_t texture = command.resources.textures[slot];
    if (texture != 0 &&
        DirectSurfaceAliasDestinationMatchesAddress(alias, texture,
                                                   destination)) {
      return true;
    }
  }
  return false;
}


const FableSurfaceTextureLink* FindDirectSurfaceTextureLinkByAliasLocked(
    const RendererSurfaceCopyAliasSnapshot& alias) {
  auto link_matches_alias = [&](const FableSurfaceTextureLink* link) {
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
    if (alias.copy_symbol_address != 0 && link->copy_symbol_address != 0 &&
        link->copy_symbol_address != alias.copy_symbol_address) {
      return false;
    }
    if (link->copy_call_serial != 0 && alias.copy_call_serial != 0 &&
        link->copy_call_serial != alias.copy_call_serial) {
      return false;
    }
    if (link->copy_context_key != 0 && alias.copy_context_key != 0 &&
        link->copy_context_key != alias.copy_context_key) {
      return false;
    }
    if (!DirectResourceAddressMatchesLocked(link->texture,
                                            alias.destination_surface)) {
      return false;
    }
    if (DirectResourceAddressMatchesLocked(link->source_surface,
                                           alias.source_surface)) {
      return true;
    }
    return DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
        *link, link->source_surface, alias.source_surface);
  };

  const FableSurfaceTextureLink* link = nullptr;
  if (alias.copy_call_serial != 0) {
    link = FableDirectFindSurfaceTextureLinkByCopyCall(alias.copy_call_serial);
  }
  if (!link && alias.copy_context_key != 0) {
    link =
        FableDirectFindSurfaceTextureLinkByCopyContext(alias.copy_context_key);
  }
  if (link_matches_alias(link)) {
    return link;
  }

  if (const std::vector<FableSurfaceTextureLink>* destination_links =
          FableDirectFindSurfaceTextureLinksForDestinationTexture(
              alias.destination_surface)) {
    for (auto it = destination_links->rbegin(); it != destination_links->rend();
         ++it) {
      if (link_matches_alias(&*it)) {
        return &*it;
      }
    }
  }
  if (const std::vector<FableSurfaceTextureLink>* texture_links =
          FableDirectFindSurfaceTextureLinksByTexture(
              alias.destination_surface)) {
    for (auto it = texture_links->rbegin(); it != texture_links->rend();
         ++it) {
      if (link_matches_alias(&*it)) {
        return &*it;
      }
    }
  }
  return nullptr;
}

bool DirectSurfaceCopyAliasHasExactIdentity(
    const RendererSurfaceCopyAliasSnapshot& alias) {
  return alias.copy_call_serial != 0 || alias.copy_context_key != 0 ||
         alias.copy_symbol_address != 0;
}

bool DirectSurfaceTextureLinkHasExactCopyIdentity(
    const FableSurfaceTextureLink& link) {
  return link.copy_call_serial != 0 || link.copy_context_key != 0 ||
         link.copy_symbol_address != 0;
}

bool DirectCommandUsesPrivateOrTitleSurfaceCopyPathLocked(
    const FableDirectRenderCommand& command) {
  return command.draw.private_texture_rectangle ||
         IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader) ||
         IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
         IsFableTitleSceneVertexShaderCommandLocked(command);
}

bool FindDirectSurfaceCopyAliasByDestinationAddressLocked(
    uint32_t address, RendererSurfaceCopyAliasSnapshot* alias) {
  if (address == 0 || !alias) {
    return false;
  }

  std::array<uint32_t, 8> candidates = {};
  uint32_t candidate_count = 0;
  auto add_candidate = [&](uint32_t candidate) {
    if (candidate == 0) {
      return;
    }
    for (uint32_t i = 0; i < candidate_count; ++i) {
      if (candidates[i] == candidate) {
        return;
      }
    }
    if (candidate_count < candidates.size()) {
      candidates[candidate_count++] = candidate;
    }
  };

  add_candidate(address);
  if (const PlumeTrackedResource* resource = FindDirectResourceLocked(address)) {
    add_candidate(resource->guest_address);
    add_candidate(resource->parent_address);
    add_candidate(resource->guest_texture.object_address);
    add_candidate(resource->guest_texture.parent_address);
    add_candidate(PlumeGuestTextureBaseAddress(*resource));
    add_candidate(resource->guest_surface.object_address);
    add_candidate(resource->guest_surface.parent_address);
  }

  for (uint32_t i = 0; i < candidate_count; ++i) {
    if (FableDirectFindSurfaceCopyAliasByDestination(candidates[i], alias)) {
      return true;
    }
  }
  return false;
}

bool RecordDirectSurfaceAliasHazardCopyForAliasLocked(
    plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command,
    const RendererSurfaceCopyAliasSnapshot& alias) {
  if (!command_list ||
      !DirectCommandWritesSurfaceAliasSourceLocked(command, alias) ||
      !DirectCommandSamplesSurfaceAliasDestinationLocked(command, alias)) {
    return false;
  }

  FableSurfaceTextureLink alias_snapshot_link = {};
  const FableSurfaceTextureLink* exact_link =
      FindDirectSurfaceTextureLinkByAliasLocked(alias);
  if (!exact_link && DirectSurfaceCopyAliasHasExactIdentity(alias)) {
    PlumeTrackedResource* alias_source =
        FindDirectResourceLocked(alias.source_surface);
    PlumeTrackedResource* alias_destination =
        FindDirectResourceLocked(alias.destination_surface);
    if (alias_source && alias_destination &&
        IsImageResource(alias_source->kind) &&
        IsImageResource(alias_destination->kind) &&
        PlumeGuestImageReady(*alias_source) &&
        PlumeGuestImageReady(*alias_destination)) {
      constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
      uint32_t source_slot = kUnknownSlot;
      const uint32_t texture_slot_mask =
          DirectCommandPixelTextureSlotMaskLocked(command);
      for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
        if (!DirectCommandUsesTextureSlotForSyncLocked(command,
                                                       texture_slot_mask,
                                                       slot)) {
          continue;
        }
        const uint32_t texture = command.resources.textures[slot];
        if (texture != 0 &&
            DirectSurfaceAliasDestinationMatchesAddress(
                alias, texture, alias_destination)) {
          source_slot = slot;
          break;
        }
      }

      alias_snapshot_link.copy_call_serial = alias.copy_call_serial;
      alias_snapshot_link.copy_context_key = alias.copy_context_key;
      alias_snapshot_link.copy_symbol_address = alias.copy_symbol_address;
      alias_snapshot_link.source_slot = source_slot;
      alias_snapshot_link.trusted_destination_edge = true;
      alias_snapshot_link.texture = alias_destination->guest_address;
      alias_snapshot_link.texture_base_address =
          PlumeGuestTextureBaseAddress(*alias_destination);
      alias_snapshot_link.texture_width =
          PlumeGuestTextureWidth(*alias_destination);
      alias_snapshot_link.texture_height =
          PlumeGuestTextureHeight(*alias_destination);
      alias_snapshot_link.texture_format =
          PlumeGuestTextureGuestFormat(*alias_destination);
      alias_snapshot_link.texture_fetch_dwords =
          PlumeGuestTextureFetchDwords(*alias_destination);
      alias_snapshot_link.source_surface = alias_source->guest_address;
      alias_snapshot_link.render_target = command.resources.render_targets[0];
      alias_snapshot_link.depth_stencil = command.resources.depth_stencil;
      alias_snapshot_link.vertex_shader = command.resources.vertex_shader;
      alias_snapshot_link.pixel_shader = command.resources.pixel_shader;
      PopulateDirectSurfaceTextureLinkShaderRoots(alias_snapshot_link, command);
      alias_snapshot_link.frame_index = g_direct_renderer.current_frame_index;
      alias_snapshot_link.draw_serial =
          command.draw_serial != 0 ? command.draw_serial : command.serial;
      alias_snapshot_link.kind = alias_source->texture_depth_target
                                     ? FablePrivateTextureCopyKind::kDepth
                                     : FablePrivateTextureCopyKind::kColor;
      alias_snapshot_link.target_content_serial =
          FableTrackedResourceVisibleContentSerial(*alias_source);
      if (alias_snapshot_link.target_content_serial != 0) {
        alias_snapshot_link.sampled_source.kind =
            FableRegisteredSampleSourceKind::kGpuSurface;
        alias_snapshot_link.sampled_source.resource =
            alias_source->guest_address;
        alias_snapshot_link.sampled_source.content_serial =
            alias_snapshot_link.target_content_serial;
      }
      exact_link = &alias_snapshot_link;
    }
  }
  if (!exact_link) {
    static uint32_t logged_exact_surface_alias_missing_copy_link_rejects = 0;
    if (logged_exact_surface_alias_missing_copy_link_rejects < 32u) {
      REXLOG_INFO(
          "Refii plume exact surface alias missing copy-link reject #{} "
          "frame={} source=0x{:08X} destination=0x{:08X} call={} "
          "context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} ds=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_surface_alias_missing_copy_link_rejects,
          g_direct_renderer.current_frame_index, alias.source_surface,
          alias.destination_surface,
          static_cast<unsigned long long>(alias.copy_call_serial),
          static_cast<unsigned long long>(alias.copy_context_key),
          alias.copy_symbol_address, command.resources.render_targets[0],
          command.resources.depth_stencil, command.resources.vertex_shader,
          command.resources.pixel_shader);
    }
    return false;
  }

  PlumeTrackedResource* source =
      FindDirectResourceLocked(exact_link->source_surface);
  if (!source) {
    source = FindDirectResourceLocked(alias.source_surface);
  }
  PlumeTrackedResource* destination =
      FindDirectResourceLocked(exact_link->texture);
  if (!destination) {
    destination = FindDirectResourceLocked(alias.destination_surface);
  }
  if (!source || !destination) {
    return true;
  }

  const uint64_t cache_key = DirectSurfaceAliasCopyCacheKey(alias);
  const uint64_t source_content_serial =
      FableTrackedResourceCopyContentSerial(*source);
  const uint64_t destination_content_serial =
      FableTrackedResourceCopyContentSerial(*destination);
  auto duplicate_alias_copy_proof = [&]() -> const char* {
    if (!DirectSurfaceCopyAliasHasExactIdentity(alias) ||
        !DirectSurfaceTextureLinkHasExactCopyIdentity(*exact_link) ||
        !DirectResourceAddressMatchesLocked(exact_link->texture,
                                            alias.destination_surface)) {
      return nullptr;
    }
    const bool source_matches =
        DirectResourceAddressMatchesLocked(exact_link->source_surface,
                                           alias.source_surface) ||
        DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
            *exact_link, exact_link->source_surface, alias.source_surface);
    if (!source_matches) {
      return nullptr;
    }

    switch (exact_link->copy_symbol_address) {
      case kFableBeginVerticesTitleWrapperSymbol:
        if (DirectCommandUsesPrivateOrTitleSurfaceCopyPathLocked(command) ||
            IsFableTitleSceneVertexShaderCommandLocked(command) ||
            IsFableTitleSceneTargetLocked(command.resources.render_targets[0])) {
          return "ida-beginvertices-title";
        }
        break;
      case kFablePrivateRectNativeDepthWrapperSymbol:
        if (source->texture_depth_target ||
            DirectResourceAddressMatchesLocked(alias.source_surface,
                                               command.resources.depth_stencil)) {
          return "ida-native-depth";
        }
        break;
      case kFablePrivateRectSetupWrapperSymbol:
      case kFablePrivateRectSetupFlushWrapperSymbol:
      case kFablePrivateRectCompactWrapperSymbol:
      case kFablePrivateRectSiblingWrapperSymbol:
      case kFablePrivateRectSiblingWrapperCallsite:
      case kFablePrivateRectTitleLocalWrapperCallsite:
      case kFablePrivateRectSourceObjectWrapperSymbol:
      case kFablePrivateRectBatchedWrapperSymbol:
      case kFablePrivateRectBatchedIntermediateCallsite:
      case kFablePrivateRectBatchedFinalCallsite:
      case kFablePrivateRectTitleGlobalWrapperSymbol:
      case kFablePrivateRectTitleGlobalWrapperCallsite:
        if (DirectCommandUsesPrivateOrTitleSurfaceCopyPathLocked(command)) {
          return "ida-private-rect";
        }
        break;
      default:
        break;
    }
    return nullptr;
  }();
  const auto cache_it =
      g_direct_renderer.surface_alias_copy_cache.find(cache_key);
  if (cache_it != g_direct_renderer.surface_alias_copy_cache.end()) {
    DirectSurfaceAliasCopyCacheEntry& cached = cache_it->second;
    if (cached.alias_serial == alias.serial &&
        cached.source_content_serial == source_content_serial &&
        cached.destination_content_serial == destination_content_serial &&
        duplicate_alias_copy_proof) {
      cached.frame_index = g_direct_renderer.current_frame_index;
      if (g_direct_renderer.diagnostics.logged_surface_alias_hazard_copy_owners < 32u) {
        REXLOG_INFO(
            "Refii plume exact duplicate surface alias hazard copy owned #{} "
            "frame={} source=0x{:08X} destination=0x{:08X} content={} "
            "dest_content={} alias_serial={} proof={} rt=0x{:08X} "
            "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} link_call={} "
            "link_context=0x{:016X} link_symbol=0x{:08X}",
            ++g_direct_renderer.diagnostics.logged_surface_alias_hazard_copy_owners,
            g_direct_renderer.current_frame_index, alias.source_surface,
            alias.destination_surface,
            static_cast<unsigned long long>(source_content_serial),
            static_cast<unsigned long long>(destination_content_serial),
            static_cast<unsigned long long>(alias.serial),
            duplicate_alias_copy_proof,
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader,
            static_cast<unsigned long long>(exact_link->copy_call_serial),
            static_cast<unsigned long long>(exact_link->copy_context_key),
            exact_link->copy_symbol_address);
      }
      return true;
    }
  }

  bool copied = RecordDirectTextureCopyLocked(command_list, *source,
                                             *destination);
  if (!copied && g_direct_renderer.device &&
      DirectPrivateTextureCopyKindCanBlitToColorTarget(exact_link->kind) &&
      !destination->texture_depth_target &&
      source->guest_texture.texture.get() !=
          destination->guest_texture.texture.get()) {
    FablePrivateTextureCopyAction scaled_action;
    constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
    uint32_t source_slot = exact_link->source_slot;
    if (source_slot == kUnknownSlot ||
        source_slot >= command.resources.textures.size()) {
      const uint32_t texture_slot_mask =
          DirectCommandPixelTextureSlotMaskLocked(command);
      for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
        if (!DirectCommandUsesTextureSlotForSyncLocked(command,
                                                       texture_slot_mask,
                                                       slot)) {
          continue;
        }
        const uint32_t texture = command.resources.textures[slot];
        if (texture != 0 &&
            DirectSurfaceAliasDestinationMatchesAddress(alias, texture,
                                                       destination)) {
          source_slot = slot;
          break;
        }
      }
    }
    if (source_slot != kUnknownSlot &&
        source_slot < command.resources.textures.size()) {
      scaled_action.source_slot = source_slot;
      scaled_action.source_texture = command.resources.textures[source_slot];
      scaled_action.source_base_address =
          PlumeGuestTextureBaseAddress(*source);
      scaled_action.source_width = PlumeGuestTextureWidth(*source);
      scaled_action.source_height = PlumeGuestTextureHeight(*source);
      scaled_action.source_format = PlumeGuestTextureGuestFormat(*source);
      const std::array<uint32_t, 6> command_fetch =
          DirectCommandTextureFetchDwords(command, source_slot);
      scaled_action.source_fetch_dwords =
          HasTrackedFetchDwords(command_fetch)
              ? command_fetch
              : PlumeGuestTextureFetchDwords(*source);
      PopulateDirectPrivateTextureCopyTargetLocked(scaled_action,
                                                   *destination);
      scaled_action.render_target = command.resources.render_targets[0];
      scaled_action.depth_stencil = command.resources.depth_stencil;
      scaled_action.vertex_shader = command.resources.vertex_shader;
      scaled_action.pixel_shader = command.resources.pixel_shader;
      PopulateDirectPrivateTextureCopyShaderRoots(scaled_action, command);
      scaled_action.wrapper_call_serial = exact_link->copy_call_serial;
      scaled_action.wrapper_context_key = exact_link->copy_context_key;
      scaled_action.wrapper_symbol_address = exact_link->copy_symbol_address;
      scaled_action.wrapper_source_texture =
          exact_link->wrapper_source_texture != 0
              ? exact_link->wrapper_source_texture
              : scaled_action.source_texture;
      scaled_action.wrapper_source_exact =
          exact_link->wrapper_source_exact ||
          scaled_action.wrapper_source_texture != 0;
      scaled_action.wrapper_setup_globals = exact_link->wrapper_setup_globals;
      scaled_action.frame_index = g_direct_renderer.current_frame_index;
      scaled_action.draw_serial =
          command.draw_serial != 0 ? command.draw_serial : command.serial;
      scaled_action.kind = exact_link->kind;
      scaled_action.uv_override = true;
      scaled_action.u0 = 0.0f;
      scaled_action.v0 = 0.0f;
      scaled_action.u1 = 1.0f;
      scaled_action.v1 = 1.0f;

      if ((!destination->texture_render_target ||
           !destination->guest_surface.attachment_view) &&
          exact_link->trusted_destination_edge) {
        PromoteDirectExactCopyDestinationToColorTargetLocked(
            g_direct_renderer.device, *destination, *exact_link);
      }
      DirectFramebufferBinding destination_framebuffer;
      if (destination->texture_render_target &&
          ResolveDirectSingleColorFramebufferForResourceLocked(
              g_direct_renderer.device, *destination,
              destination_framebuffer)) {
        copied = RecordDirectScaledPrivateRectCopyLocked(
            command_list, command, destination_framebuffer, scaled_action,
            *source, *destination, source_slot,
            "surface-alias-hazard-scaled-copy");
      }
    }
  }

  if (copied) {
    FableSurfaceTextureLink copied_link = *exact_link;
    PopulateDirectSurfaceTextureLinkBackingLocked(copied_link, *destination);
    copied_link.frame_index = g_direct_renderer.current_frame_index;
    copied_link.draw_serial =
        command.draw_serial != 0 ? command.draw_serial : command.serial;
    RegisterDirectSurfaceTextureLinkPreparedLocked(copied_link,
                                                  "surface-alias-hazard-copy");
    g_direct_renderer.surface_alias_copy_cache[cache_key] =
        DirectSurfaceAliasCopyCacheEntry{
            .alias_serial = alias.serial,
            .source_content_serial = source_content_serial,
            .destination_content_serial =
                FableTrackedResourceCopyContentSerial(*destination),
            .frame_index = g_direct_renderer.current_frame_index,
        };
    if (g_direct_renderer.diagnostics.logged_surface_alias_hazard_copies < 32u) {
      REXLOG_INFO(
          "Refii plume direct renderer copied surface alias hazard #{} "
          "source=0x{:08X} destination=0x{:08X} current_rt=0x{:08X} "
          "current_depth=0x{:08X} call={} context=0x{:016X}",
          ++g_direct_renderer.diagnostics.logged_surface_alias_hazard_copies,
          source->guest_address, destination->guest_address,
          command.resources.render_targets[0], command.resources.depth_stencil,
          static_cast<unsigned long long>(alias.copy_call_serial),
          static_cast<unsigned long long>(alias.copy_context_key));
    }
    return true;
  }
  static uint32_t logged_exact_surface_alias_copy_rejects = 0;
  if (logged_exact_surface_alias_copy_rejects < 32u) {
    REXLOG_INFO(
        "Refii plume exact surface alias hazard copy reject #{} frame={} "
        "source=0x{:08X}/{}x{} destination=0x{:08X}/{}x{} "
        "same_texture={} current_rt=0x{:08X} current_depth=0x{:08X} "
        "call={} context=0x{:016X} symbol=0x{:08X}",
        ++logged_exact_surface_alias_copy_rejects,
        g_direct_renderer.current_frame_index, source->guest_address,
        PlumeGuestTextureWidth(*source), PlumeGuestTextureHeight(*source),
        destination->guest_address, PlumeGuestTextureWidth(*destination),
        PlumeGuestTextureHeight(*destination),
        source->guest_texture.texture.get() ==
                destination->guest_texture.texture.get()
            ? 1u
            : 0u,
        command.resources.render_targets[0], command.resources.depth_stencil,
        static_cast<unsigned long long>(alias.copy_call_serial),
        static_cast<unsigned long long>(alias.copy_context_key),
        alias.copy_symbol_address);
  }
  return false;
}

void RecordDirectSurfaceAliasHazardCopiesForCommandLocked(
    plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command) {
  if (!command_list) {
    return;
  }

  bool recorded_exact_alias = false;
  bool saw_exact_alias_candidate = false;
  uint32_t used_texture_slot_count = 0;
  uint32_t first_used_texture_slot = UINT32_MAX;
  uint32_t first_used_texture = 0;
  std::unordered_set<uint64_t> handled_aliases;
  const uint32_t texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(command);
  for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
    if (!DirectCommandUsesTextureSlotForSyncLocked(command, texture_slot_mask,
                                                   slot)) {
      continue;
    }
    const uint32_t texture = command.resources.textures[slot];
    if (texture != 0) {
      ++used_texture_slot_count;
      if (first_used_texture_slot == UINT32_MAX) {
        first_used_texture_slot = slot;
        first_used_texture = texture;
      }
    }
    RendererSurfaceCopyAliasSnapshot alias;
    if (texture == 0 ||
        !FindDirectSurfaceCopyAliasByDestinationAddressLocked(texture, &alias)) {
      continue;
    }
    saw_exact_alias_candidate = true;

    const uint64_t cache_key = DirectSurfaceAliasCopyCacheKey(alias);
    if (!handled_aliases.insert(cache_key).second) {
      continue;
    }

    recorded_exact_alias |= RecordDirectSurfaceAliasHazardCopyForAliasLocked(
        command_list, command, alias);
  }
  if (recorded_exact_alias) {
    return;
  }

  const bool private_or_title_command =
      command.draw.source != RendererDrawEventSource::kPixVideo &&
      (command.draw.private_texture_rectangle ||
       IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader) ||
       IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
       IsFableTitleSceneVertexShaderCommandLocked(command));
  if (private_or_title_command) {
    if (!saw_exact_alias_candidate) {
      static uint32_t logged_exact_private_title_alias_no_candidate_proofs = 0;
      if (RendererDeepObserverHooksEnabled() &&
          logged_exact_private_title_alias_no_candidate_proofs < 32u) {
        REXLOG_INFO(
            "Refii plume exact private/title alias hazard scan no-candidate "
            "proof #{} reason=no-exact-slot-alias frame={} serial={} draw={} "
            "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
            "slot_mask=0x{:08X} used_slots={} first_slot={} "
            "first_texture=0x{:08X}",
            ++logged_exact_private_title_alias_no_candidate_proofs,
            g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(command.serial),
            static_cast<unsigned long long>(command.draw_serial),
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader,
            texture_slot_mask, used_texture_slot_count,
            first_used_texture_slot == UINT32_MAX ? 0xFFFFFFFFu
                                                  : first_used_texture_slot,
            first_used_texture);
      }
      return;
    }
    auto command_has_exact_registered_title_sync_samples = [&]() {
      if (!IsFableTitleSceneTextureSyncCommandLocked(command)) {
        return false;
      }
      auto slot_has_exact_title_sync_sample = [&](uint32_t slot) {
        PlumeTrackedResource* registered_sample =
            ResolveDirectSampledTextureFromFetchConstantsLocked(command, slot);
        if (registered_sample &&
            DirectTitleSyncFetchTextureMatchesCommandLocked(
                command, slot, *registered_sample)) {
          return true;
        }
        const uint32_t guest = command.resources.textures[slot];
        PlumeTrackedResource* direct_texture =
            guest != 0 ? FindDirectResourceLocked(guest) : nullptr;
        if (direct_texture &&
            DirectTitleSyncFetchTextureMatchesCommandLocked(
                command, slot, *direct_texture)) {
          return true;
        }
        return false;
      };
      bool saw_used_slot = false;
      for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
        if (!DirectCommandUsesTextureSlotForSyncLocked(command,
                                                       texture_slot_mask, slot)) {
          continue;
        }
        saw_used_slot = true;
        if (!slot_has_exact_title_sync_sample(slot)) {
          return false;
        }
      }
      return saw_used_slot;
    };
    if (command_has_exact_registered_title_sync_samples()) {
      return;
    }

    for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
      if (DirectCommandUsesTextureSlotForSyncLocked(command, texture_slot_mask,
                                                   slot) &&
          DirectCommandSlotUsesExactTitleUploadSourceLocked(command, slot)) {
        return;
      }
    }

    auto command_has_exact_direct_private_title_sample = [&]() {
      for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
        if (!DirectCommandUsesTextureSlotForSyncLocked(command,
                                                       texture_slot_mask, slot)) {
          continue;
        }
        const uint32_t guest = command.resources.textures[slot];
        PlumeTrackedResource* direct_texture =
            guest != 0 ? FindDirectResourceLocked(guest) : nullptr;
        PlumeTrackedResource* registered_sample =
            ResolveDirectSampledTextureFromFetchConstantsLocked(command, slot);
        if (DirectCommandTextureSlotDirectResourceProvidesSampleLocked(
                command, slot,
                registered_sample ? registered_sample : direct_texture,
                false)) {
          return true;
        }
      }
      return false;
    };
    if (command_has_exact_direct_private_title_sample()) {
      return;
    }

    auto command_has_exact_surface_texture_link = [&]() {
      const uint64_t draw_serial =
          command.draw_serial != 0 ? command.draw_serial : command.serial;
      auto trusted_link = [&](const FableSurfaceTextureLink* link) {
        return link && link->trusted_destination_edge &&
               link->source_surface != 0 && link->texture != 0 &&
               link->frame_index <= g_direct_renderer.current_frame_index &&
               !(link->frame_index == g_direct_renderer.current_frame_index &&
                 link->draw_serial != 0 && draw_serial != 0 &&
                 link->draw_serial > draw_serial);
      };
      for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
        if (!DirectCommandUsesTextureSlotForSyncLocked(command,
                                                       texture_slot_mask, slot)) {
          continue;
        }
        const uint32_t guest = command.resources.textures[slot];
        if (guest == 0) {
          continue;
        }
        if (trusted_link(
                FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
                    guest, g_direct_renderer.current_frame_index, draw_serial,
                    slot)) ||
            trusted_link(FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
                guest, slot)) ||
            trusted_link(
                FableDirectFindSurfaceTextureLinkForDestinationTexture(guest))) {
          return true;
        }
        const PlumeTrackedResource* texture = FindDirectResourceLocked(guest);
        if (!texture) {
          continue;
        }
        if (trusted_link(
                FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
                    texture->guest_address,
                    g_direct_renderer.current_frame_index, draw_serial, slot)) ||
            trusted_link(
                FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
                    texture->parent_address,
                    g_direct_renderer.current_frame_index, draw_serial, slot)) ||
            trusted_link(FableDirectFindVisibleSurfaceTextureLinkForBase(
                PlumeGuestTextureBaseAddress(*texture),
                g_direct_renderer.current_frame_index, draw_serial, slot)) ||
            trusted_link(FableDirectFindVisibleSurfaceTextureLinkForFetch(
                PlumeGuestTextureFetchDwords(*texture),
                g_direct_renderer.current_frame_index, draw_serial, slot))) {
          return true;
        }
      }
      return false;
    };
    if (command_has_exact_surface_texture_link()) {
      return;
    }

    static uint32_t logged_exact_private_title_global_alias_hazard_rejects = 0;
    if (logged_exact_private_title_global_alias_hazard_rejects < 32u) {
      REXLOG_INFO(
          "Refii plume exact private/title global alias hazard-scan reject #{} "
          "frame={} serial={} draw={} rt=0x{:08X} ds=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X} slot_mask=0x{:08X}",
          ++logged_exact_private_title_global_alias_hazard_rejects,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.serial),
          static_cast<unsigned long long>(command.draw_serial),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader,
          texture_slot_mask);
    }
    return;
  }
}

void RegisterDirectPm4ResolveSurfaceTextureLinkLocked(
    const FableDirectRenderCommand& command, const PlumeTrackedResource& source,
    const PlumeTrackedResource* destination, uint32_t destination_base,
    uint32_t normalized_base, uint32_t source_slot, bool depth_resolve) {
  const uint64_t source_content = FableTrackedResourceVisibleContentSerial(source);
  const uint64_t destination_content =
      destination ? FableTrackedResourceVisibleContentSerial(*destination)
                  : source_content;
  if (source_content == 0 && destination_content == 0) {
    return;
  }

  FableSurfaceTextureLink link;
  link.source_slot = 0xFFFFFFFFu;
  link.trusted_destination_edge = true;
  link.source_surface = source.guest_address;
  link.texture_width =
      destination ? PlumeGuestTextureWidth(*destination)
                  : PlumeGuestTextureWidth(source);
  link.texture_height =
      destination ? PlumeGuestTextureHeight(*destination)
                  : PlumeGuestTextureHeight(source);
  link.texture_format =
      destination ? PlumeGuestTextureGuestFormat(*destination)
                  : PlumeGuestTextureGuestFormat(source);
  link.target_content_serial =
      destination_content != 0 ? destination_content : source_content;
  link.render_target = command.resources.render_targets[0];
  link.depth_stencil = command.resources.depth_stencil;
  link.vertex_shader = command.resources.vertex_shader;
  link.pixel_shader = command.resources.pixel_shader;
  PopulateDirectSurfaceTextureLinkShaderRoots(link, command);
  link.frame_index = g_direct_renderer.current_frame_index;
  link.draw_serial = command.draw_serial != 0 ? command.draw_serial
                                              : command.serial;
  link.kind = depth_resolve ? FablePrivateTextureCopyKind::kDepth
                            : FablePrivateTextureCopyKind::kColor;
  if (destination) {
    PopulateDirectSurfaceTextureLinkBackingLocked(link, *destination);
    link.texture_fetch_dwords = PlumeGuestTextureFetchDwords(*destination);
    if (link.texture_base_address == 0) {
      link.texture_base_address = destination_base != 0 ? destination_base
                                                        : normalized_base;
    }
  } else {
    link.sampled_source.kind = FableRegisteredSampleSourceKind::kGpuSurface;
    link.sampled_source.resource = source.guest_address;
    link.sampled_source.content_serial = source_content;
  }

  auto register_for_key = [&](uint32_t texture_key, uint32_t base_key) {
    if (texture_key == 0 && base_key == 0) {
      return;
    }
    FableSurfaceTextureLink keyed_link = link;
    keyed_link.texture = texture_key != 0 ? texture_key : base_key;
    keyed_link.texture_base_address = base_key;
    RegisterDirectSurfaceTextureLinkPreparedLocked(keyed_link, "pm4-resolve");
  };

  if (destination) {
    const uint32_t destination_resource_base =
        PlumeGuestTextureBaseAddress(*destination);
    register_for_key(destination->guest_address,
                     destination_resource_base != 0 ? destination_resource_base
                                                    : destination_base);
  }
  register_for_key(normalized_base, destination_base);
  if (destination_base != normalized_base) {
    register_for_key(destination_base, normalized_base);
  }

  if (g_direct_renderer.diagnostics.logged_first_resolve_alias) {
    return;
  }
  REXLOG_INFO(
      "Refii plume direct renderer registered PM4 resolve edge "
      "source=0x{:08X} dest=0x{:08X} normalized=0x{:08X} sample=0x{:08X} "
      "slot={} content={} depth={}",
      source.guest_address, destination_base, normalized_base,
      destination ? destination->guest_address : source.guest_address,
      source_slot,
      static_cast<unsigned long long>(link.target_content_serial),
      depth_resolve ? 1 : 0);
}


uint32_t DirectResolveCopyDestinationBase(
    const FableDirectRenderCommand& command) {
  return command.resolve.rb_copy_dest_base != 0
             ? command.resolve.rb_copy_dest_base
             : command.pipeline.pm4.rb_copy_dest_base;
}

uint32_t DirectResolveCopyControl(const FableDirectRenderCommand& command) {
  return command.resolve.rb_copy_control != 0
             ? command.resolve.rb_copy_control
             : command.pipeline.pm4.rb_copy_control;
}

DirectResolveCopySourceSelection SelectDirectResolveCopySourceLocked(
    const FableDirectRenderCommand& command) {
  DirectResolveCopySourceSelection selection;
  if (command.resolve.refii_source_surface != 0) {
    selection.source_slot = command.resolve.refii_source_slot;
    selection.source_address = command.resolve.refii_source_surface;
    selection.depth_resolve = command.resolve.refii_source_depth;
    selection.source = FindDirectResourceLocked(selection.source_address);
    selection.ready =
        selection.source != nullptr && PlumeGuestImageReady(*selection.source);
    return selection;
  }
  const uint32_t copy_control = DirectResolveCopyControl(command);
  selection.source_slot = copy_control & 0x7u;
  selection.depth_resolve =
      selection.source_slot >= command.resources.render_targets.size();
  selection.source_address =
      selection.depth_resolve
          ? command.resources.depth_stencil
          : command.resources.render_targets[selection.source_slot];
  selection.source = FindDirectResourceLocked(selection.source_address);
  selection.ready =
      selection.source != nullptr && PlumeGuestImageReady(*selection.source);
  if (selection.ready) {
    return selection;
  }

  for (uint32_t slot = 0; slot < command.resources.render_targets.size();
       ++slot) {
    const uint32_t candidate_address = command.resources.render_targets[slot];
    PlumeTrackedResource* candidate = FindDirectResourceLocked(candidate_address);
    if (!candidate || !PlumeGuestImageReady(*candidate)) {
      continue;
    }
    selection.source_slot = slot;
    selection.source_address = candidate_address;
    selection.depth_resolve = false;
    selection.source = candidate;
    selection.ready = true;
    return selection;
  }

  if (command.resources.depth_stencil != 0) {
    PlumeTrackedResource* candidate =
        FindDirectResourceLocked(command.resources.depth_stencil);
    if (candidate && PlumeGuestImageReady(*candidate)) {
      selection.source_slot =
          static_cast<uint32_t>(command.resources.render_targets.size());
      selection.source_address = command.resources.depth_stencil;
      selection.depth_resolve = true;
      selection.source = candidate;
      selection.ready = true;
    }
  }
  return selection;
}


PlumeTrackedResource* EnsureDirectResolveDestinationResourceLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    const DirectResolveCopySourceSelection& selection,
    uint32_t destination_base, uint32_t normalized_base,
    bool rematerialize_incompatible) {
  if (!device || !selection.source || destination_base == 0 ||
      !PlumeGuestImageReady(*selection.source)) {
    return nullptr;
  }

  auto copy_compatible = [&](const PlumeTrackedResource& destination) {
    return selection.source != &destination &&
           selection.source->guest_texture.texture.get() !=
               destination.guest_texture.texture.get() &&
           DirectTextureCopySizeMatches(*selection.source, destination);
  };
  auto existing_destination = [&]() -> PlumeTrackedResource* {
    if (PlumeTrackedResource* destination =
            FindDirectResourceLocked(destination_base)) {
      return destination;
    }
    return FindDirectResourceLocked(normalized_base);
  };

  if (PlumeTrackedResource* destination = existing_destination();
      destination &&
      (!rematerialize_incompatible || copy_compatible(*destination) ||
       destination == selection.source ||
       destination->guest_texture.texture.get() ==
           selection.source->guest_texture.texture.get())) {
    return destination;
  }

  const uint32_t destination_address =
      normalized_base != 0 ? normalized_base : destination_base;
  const uint32_t width = PlumeGuestTextureWidth(*selection.source);
  const uint32_t height = PlumeGuestTextureHeight(*selection.source);
  if (destination_address == 0 || width == 0 || height == 0 ||
      width > 0x4000u || height > 0x8000u) {
    return nullptr;
  }

  const uint32_t source_guest_format =
      PlumeGuestTextureGuestFormat(*selection.source) & 0x3Fu;
  FableGuestResourceSnapshot snapshot;
  snapshot.kind = selection.depth_resolve ? FableGuestResourceKind::kDepthSurface
                                          : FableGuestResourceKind::kSurface;
  snapshot.guest_address = destination_address;
  snapshot.parent_address = destination_address;
  snapshot.surface.object_address = destination_address;
  snapshot.surface.parent_address = destination_address;
  snapshot.surface.packed_extent = PackFableSurfaceExtent(width, height);
  snapshot.surface.d3d_format = source_guest_format;
  snapshot.surface.pitch_msaa = width;
  snapshot.surface.byte_size = width * height * 4u;
  if (selection.depth_resolve) {
    snapshot.surface.depth_stencil_info = source_guest_format;
  } else {
    snapshot.surface.render_target_info = source_guest_format;
  }
  snapshot.serial = command.serial != 0
                        ? command.serial
                        : (command.draw_serial != 0
                               ? command.draw_serial
                               : uint64_t(g_direct_renderer.current_frame_index));

  if (!SyncTrackedResourceLocked(device, snapshot, nullptr)) {
    ++g_direct_renderer.failed_resource_syncs;
    LogDirectResourceSyncFailureLocked(snapshot, "resolve_destination");
    return nullptr;
  }
  g_direct_renderer.known_uploads[destination_address] =
      UploadCacheEntryForSnapshot(snapshot);

  PlumeTrackedResource* destination =
      FindDirectResourceLocked(destination_address);
  if (!destination || !IsImageResource(destination->kind) ||
      !PlumeGuestImageReady(*destination)) {
    return nullptr;
  }
  RegisterDirectTextureIdentityFromResourceLocked(
      *destination, PlumeGuestTextureFetchDwords(*destination));
  return destination;
}

#endif

}  // namespace refii::renderer
