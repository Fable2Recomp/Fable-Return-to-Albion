#include "runtime/refii_renderer/plume/fable/direct/fable_direct_command_batch.h"

#include "runtime/cvars.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_command_classification.h"

#include <rex/logging.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <numeric>

namespace refii::renderer {
namespace {

void AccumulateDirectCommandSourceCounts(
    const std::vector<FableDirectRenderCommand>& commands,
    std::array<uint32_t, 10>& source_counts,
    uint32_t& clears,
    uint32_t& resolves,
    uint32_t& state_commands) {
  for (const FableDirectRenderCommand& command : commands) {
    if (IsDirectDrawCommand(command)) {
      const size_t source = static_cast<size_t>(command.draw.source);
      if (source < source_counts.size()) {
        ++source_counts[source];
      }
      continue;
    }
    if (command.type == FableDirectRenderCommandType::kClear) {
      ++clears;
    } else if (command.type == FableDirectRenderCommandType::kResolveCopy) {
      ++resolves;
    } else {
      ++state_commands;
    }
  }
}

}  // namespace

FableDirectStateSnapshot BuildEffectiveDirectFrameStatsSnapshot(
    const FableDirectFrameCommandBatch& batch) {
  FableDirectStateSnapshot snapshot = batch.staged_snapshot;
  snapshot.render_command_count = 0;
  snapshot.draw_event_count = 0;
  snapshot.direct_event_count = 0;
  snapshot.draw_count = 0;
  snapshot.indexed_draw_count = 0;
  snapshot.staged_state_command_count = 0;
  snapshot.staged_clear_command_count = 0;
  snapshot.staged_resolve_command_count = 0;
  snapshot.staged_draw_source_counts = {};
  snapshot.submitted_render_command_count = 0;

  for (const FableDirectRenderCommand& command : batch.submitted_commands) {
    // Supersession / backlog-cap marks are dropped commands still awaiting
    // the record strip's sweep — they must not count as this frame's work.
    if (command.record_dropped) {
      continue;
    }
    if (IsDirectDrawCommand(command)) {
      ++snapshot.render_command_count;
      ++snapshot.draw_event_count;
      ++snapshot.direct_event_count;
      ++snapshot.draw_count;
      if (command.draw.draw.indexed) {
        ++snapshot.indexed_draw_count;
      }
      ++snapshot.submitted_render_command_count;
      continue;
    }
    if (command.type == FableDirectRenderCommandType::kClear ||
        command.type == FableDirectRenderCommandType::kResolveCopy) {
      ++snapshot.render_command_count;
      ++snapshot.submitted_render_command_count;
    }
  }
  return snapshot;
}

FableDirectFrameCommandBatch CaptureFableDirectFrameCommandBatch() {
  FableDirectFrameCommandBatch batch;
  // Non-video commands are published only when the guest RenderFrame owner
  // exits. A host swapchain tick may consume a completed batch, but must not
  // promote the frame that the guest is still assembling.
  batch.submitted_commands = ConsumeFableDirectSubmittedRenderCommands();
  // Read AFTER the consume: if the ordinal advances in the gap, a frame that
  // just completed is (correctly) promoted from partial to complete; the
  // ordinal is monotonic so the reverse cannot happen.
  batch.in_progress_generation = FableDirectCurrentStagingPublishGeneration();

  // GUEST BACKLOG SUPERSESSION (run 474 record-loop runaway): a compose hold
  // longer than one guest frame lets several sealed guest frames pile up here
  // — replaying every backlogged frame's draws in one registry hold
  // multiplied the hold, which grew the next backlog (hold_ms 2382 -> 8182 ->
  // 53353). That replay-all was OUR invention, not guest semantics: the
  // guest's own ring interpreter (sub_822DFE08, IDA-proven) runs one
  // published list per 30Hz tick and discards backlog a newer complete list
  // supersedes (~150ms staleness). Mirror it at frame granularity: non-video
  // DRAWS from SEALED publish generations (nonzero — see publish_generation)
  // older than the newest sealed generation carrying non-video draws are
  // dropped. Their non-draw commands (clears / resolves / state) still
  // replay, in order — state is cumulative, and a superseded frame's resolve
  // may fill a surface the newest frame samples, so a full frame skip is NOT
  // safe; the per-draw record cost is what spirals, and that is all this
  // removes. Exempt from dropping AND from claiming newest: pix-video draws,
  // and every generation-0 command — immediate producer segments (PIX/Bink
  // compose, synthetic recovery), whose Bink compose quads are NOT kPixVideo
  // (their sources are kScratchWrapper/kD3DWrapper/kSceneObject, see
  // FableVideoCommandHasBinkWrapperSource) and which minting generations in
  // run 475 superseded the video-phase frames the guest's fence progression
  // depended on. Draws of the still-assembling frame never appear here (they
  // live in the staged queue), so nothing current is ever dropped. GATED by
  // renderer_backlog_supersession (default ON since run 479).
  //
  // SCOPE (run 485): this filter bounds the OBSERVER direct-command stream
  // only. Under GTL-authoritative submission the record volume rides the GTL
  // shadow-packet stream instead — the merge REPLACES this batch's commands
  // with packet-built ones whose publish_generation is 0 (they never pass
  // PushRenderCommandLocked), so this filter cannot see or bound them. The
  // packet-stream twin lives in the authoritative replay ("GTL-STREAM
  // BACKLOG SUPERSESSION", fable_direct_renderer.cpp) and folds its drops
  // into the same census fields.
  if (RendererBacklogSupersessionEnabled() &&
      !batch.submitted_commands.empty()) {
    uint32_t newest_sealed_generation = 0;
    for (const FableDirectRenderCommand& command : batch.submitted_commands) {
      if (IsDirectDrawCommand(command) &&
          command.draw.source != RendererDrawEventSource::kPixVideo &&
          // The still-assembling frame (in_progress_generation) must not
          // claim newest: with composes longer than a guest frame the batch
          // almost always ends mid-frame, and letting the partial frame
          // supersede the newest COMPLETE frame drops that frame's UI
          // overlay draws (run 483: missing title logo/text). Its commands
          // are also protected from dropping by the >= keep below.
          command.publish_generation != batch.in_progress_generation &&
          command.publish_generation > newest_sealed_generation) {
        newest_sealed_generation = command.publish_generation;
      }
    }
    if (newest_sealed_generation != 0) {
      std::vector<uint32_t> superseded_generations;
      // MARK superseded draws (record_dropped) instead of erase/remove_if:
      // compacting a vector of ~12.6KB FableDirectRenderCommand elements is
      // an O(N) memmove per drop event (run-321 storm class). The record
      // path's single strip compaction sweeps the marks; the stats snapshot
      // below and every census in between skip them.
      uint32_t superseded_draws = 0;
      for (FableDirectRenderCommand& command : batch.submitted_commands) {
        if (!IsDirectDrawCommand(command) ||
            command.draw.source == RendererDrawEventSource::kPixVideo ||
            command.publish_generation == 0 ||
            command.publish_generation >= newest_sealed_generation) {
          continue;
        }
        command.record_dropped = true;
        ++superseded_draws;
        if (std::find(superseded_generations.begin(),
                      superseded_generations.end(),
                      command.publish_generation) ==
            superseded_generations.end()) {
          superseded_generations.push_back(command.publish_generation);
        }
      }
      batch.superseded_draws_dropped = superseded_draws;
      batch.superseded_frames =
          static_cast<uint32_t>(superseded_generations.size());
      if (batch.superseded_draws_dropped != 0) {
        static uint32_t logged_superseded_backlog_drops = 0;
        if (logged_superseded_backlog_drops < 64u ||
            (logged_superseded_backlog_drops % 64u) == 0u) {
          REXLOG_INFO(
              "Refii plume superseded guest-frame backlog #{} "
              "frames_superseded={} draws_dropped={} kept_commands={}",
              logged_superseded_backlog_drops, batch.superseded_frames,
              batch.superseded_draws_dropped,
              static_cast<uint32_t>(batch.submitted_commands.size() -
                                    superseded_draws));
        }
        ++logged_superseded_backlog_drops;
      }
    }
  }
  batch.staged_snapshot = GetFableDirectStateSnapshot();

  if (batch.staged_snapshot.render_command_count != 0) {
    const auto command_carries_direct_event =
        [](const FableDirectRenderCommand& command) {
          switch (command.type) {
            case FableDirectRenderCommandType::kClear:
            case FableDirectRenderCommandType::kResolveCopy:
            case FableDirectRenderCommandType::kDrawPrimitive:
            case FableDirectRenderCommandType::kDrawIndexedPrimitive:
            case FableDirectRenderCommandType::kDrawPrimitiveUP:
              return true;
            default:
              return false;
          }
        };
    const bool has_submitted_direct_batch =
        std::any_of(batch.submitted_commands.begin(),
                    batch.submitted_commands.end(),
                    command_carries_direct_event);
    const bool staged_contains_draw_events =
        batch.staged_snapshot.draw_event_count != 0 ||
        batch.staged_snapshot.last_command_boundary_staged_draws != 0;
    if (has_submitted_direct_batch || !staged_contains_draw_events) {
      const uint32_t consumed_exact_framebuffer_state =
          FableDirectConsumeExactFramebufferStateCommands();
      if (consumed_exact_framebuffer_state != 0) {
        batch.staged_snapshot = GetFableDirectStateSnapshot();
      }
      const uint32_t consumed_exact_rooted_state =
          FableDirectConsumeExactRootedStateCommands();
      if (consumed_exact_rooted_state != 0) {
        batch.staged_snapshot = GetFableDirectStateSnapshot();
      }
      const uint32_t consumed_state_flush =
          FableDirectConsumeStateFlushCommands();
      if (consumed_state_flush != 0) {
        batch.staged_snapshot = GetFableDirectStateSnapshot();
      }
      const uint32_t consumed_post_flush_exact_rooted_state =
          FableDirectConsumeExactRootedStateCommands();
      if (consumed_post_flush_exact_rooted_state != 0) {
        batch.staged_snapshot = GetFableDirectStateSnapshot();
      }
      const uint32_t consumed_state_only =
          FableDirectConsumeStateOnlyCommands();
      if (consumed_state_only != 0) {
        batch.staged_snapshot = GetFableDirectStateSnapshot();
      }
    }
    if (batch.staged_snapshot.render_command_count == 0) {
      return batch;
    }
    batch.staged_snapshot = GetFableDirectStateSnapshot();
  }
  return batch;
}

void FableDirectLogCommandSourceSummary(
    const std::vector<FableDirectRenderCommand>& submitted_commands,
    uint32_t current_frame_index,
    uint32_t& logged_command_source_summaries) {
  if (submitted_commands.empty()) {
    return;
  }

  std::array<uint32_t, 10> submitted_sources = {};
  uint32_t submitted_clears = 0;
  uint32_t submitted_resolves = 0;
  uint32_t submitted_state_commands = 0;
  AccumulateDirectCommandSourceCounts(submitted_commands, submitted_sources,
                                      submitted_clears, submitted_resolves,
                                      submitted_state_commands);
  const uint32_t submitted_draws =
      std::accumulate(submitted_sources.begin(), submitted_sources.end(), 0u);
  if (submitted_draws == 0 && submitted_clears == 0 &&
      submitted_resolves == 0) {
    return;
  }

  uint64_t signature = 0x9E3779B97F4A7C15ull;
  auto mix_signature = [&](uint64_t value) {
    signature ^= value + 0x9E3779B97F4A7C15ull + (signature << 6u) +
                 (signature >> 2u);
  };
  mix_signature(submitted_commands.size());
  for (uint32_t count : submitted_sources) {
    mix_signature(count);
  }
  mix_signature(submitted_clears);
  mix_signature(submitted_resolves);
  mix_signature(submitted_state_commands);

  static uint64_t last_logged_signature = 0;
  const bool source_mix_changed = signature != last_logged_signature;
  uint64_t category_signature = 0;
  auto mark_category = [&](uint32_t bit, bool active) {
    if (active) {
      category_signature |= 1ull << bit;
    }
  };
  for (uint32_t i = 0; i < submitted_sources.size(); ++i) {
    mark_category(i, submitted_sources[i] != 0);
  }
  mark_category(16u, submitted_clears != 0);
  mark_category(17u, submitted_resolves != 0);
  mark_category(18u, submitted_state_commands != 0);
  static uint64_t last_logged_category_signature = 0;
  const bool category_mix_changed =
      category_signature != last_logged_category_signature;
  const bool has_scene_or_pm4_draws =
      submitted_sources[static_cast<uint32_t>(
          RendererDrawEventSource::kSceneObject)] != 0 ||
      submitted_sources[static_cast<uint32_t>(
          RendererDrawEventSource::kPm4Draw)] != 0 ||
      submitted_sources[static_cast<uint32_t>(
          RendererDrawEventSource::kPrivateTextureRectangle)] != 0;
  const uint32_t logged = logged_command_source_summaries;
  if (has_scene_or_pm4_draws) {
    if (logged >= 128u) {
      return;
    }
  } else if (logged >= 32u) {
    if (!category_mix_changed || logged >= 64u) {
      return;
    }
  } else if (!source_mix_changed) {
    return;
  }
  last_logged_signature = signature;
  last_logged_category_signature = category_signature;

  REXLOG_INFO(
      "Refii plume direct command source summary #{} frame={} submitted={} "
      "submitted_sources={} {} {} {} {} {} {} {} {} {} clears={} "
      "resolves={} state={}",
      ++logged_command_source_summaries, current_frame_index,
      submitted_commands.size(), submitted_sources[0], submitted_sources[1],
      submitted_sources[2], submitted_sources[3], submitted_sources[4],
      submitted_sources[5], submitted_sources[6], submitted_sources[7],
      submitted_sources[8], submitted_sources[9], submitted_clears,
      submitted_resolves, submitted_state_commands);
}

uint32_t FableDirectNormalizeExactBinkClearOrder(
    std::vector<FableDirectRenderCommand>& submitted_commands,
    FableVideoExactBinkDrawPredicate is_exact_bink_draw) {
  return FableVideoNormalizeExactBinkClearOrder(submitted_commands,
                                                is_exact_bink_draw);
}

void FableDirectLogBinkCommandOrder(
    const std::vector<FableDirectRenderCommand>& submitted_commands,
    bool verbose_tracing_enabled,
    uint32_t current_frame_index,
    uint32_t& logged_bink_command_order,
    FableVideoExactBinkDrawPredicate is_exact_bink_draw) {
  FableVideoLogBinkCommandOrder(
      submitted_commands, verbose_tracing_enabled, current_frame_index,
      logged_bink_command_order, is_exact_bink_draw);
}

}  // namespace refii::renderer
