#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/video/fable_video_classification.h"

#include <cstdint>
#include <vector>

namespace refii::renderer {

struct FableDirectFrameCommandBatch {
  std::vector<FableDirectRenderCommand> submitted_commands;
  FableDirectStateSnapshot staged_snapshot;
  // Backlog supersession census (run 474): how many complete guest frames'
  // draws were discarded as superseded, and the draw count dropped. Zero in
  // healthy operation (one publish per capture). AGGREGATE (run 485): the
  // capture-time direct-command filter, the GTL packet-stream supersession in
  // the authoritative merge, and the count-keyed record backlog cap all add
  // into these — the compose lock census (frames_superseded=/draws_dropped=)
  // reads the sum; the per-mechanism log lines identify which engaged.
  uint32_t superseded_frames = 0;
  uint32_t superseded_draws_dropped = 0;
  // The generation of the guest frame still being assembled at capture time
  // (guest_frame_seal_ordinal + 1). Commands carrying it are a PARTIAL frame:
  // with composes longer than a guest frame the capture almost always lands
  // mid-frame, so they must neither claim newest (a partial frame would
  // supersede the newest COMPLETE frame's UI) nor be dropped.
  uint32_t in_progress_generation = 0;
};

FableDirectStateSnapshot BuildEffectiveDirectFrameStatsSnapshot(
    const FableDirectFrameCommandBatch& batch);

FableDirectFrameCommandBatch CaptureFableDirectFrameCommandBatch();

void FableDirectLogCommandSourceSummary(
    const std::vector<FableDirectRenderCommand>& submitted_commands,
    uint32_t current_frame_index,
    uint32_t& logged_command_source_summaries);

uint32_t FableDirectNormalizeExactBinkClearOrder(
    std::vector<FableDirectRenderCommand>& submitted_commands,
    FableVideoExactBinkDrawPredicate is_exact_bink_draw);

void FableDirectLogBinkCommandOrder(
    const std::vector<FableDirectRenderCommand>& submitted_commands,
    bool verbose_tracing_enabled,
    uint32_t current_frame_index,
    uint32_t& logged_bink_command_order,
    FableVideoExactBinkDrawPredicate is_exact_bink_draw);

}  // namespace refii::renderer
