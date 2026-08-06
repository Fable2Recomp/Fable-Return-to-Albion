#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"

#include <cstdint>
#include <vector>

namespace refii::renderer {

bool FableVideoCommandIsDraw(const FableDirectRenderCommand& command);
bool FableVideoCommandIsPixVideoDraw(const FableDirectRenderCommand& command);

using FableVideoExactBinkDrawPredicate =
    bool (*)(const FableDirectRenderCommand& command);

bool FableVideoCommandIsDirectVideoDraw(
    const FableDirectRenderCommand& command,
    FableVideoExactBinkDrawPredicate is_exact_bink_draw);

bool FableVideoCommandHasBinkIndexedWrapperSymbol(
    const FableDirectRenderCommand& command);
bool FableVideoCommandHasBinkWrapperSource(
    const FableDirectRenderCommand& command);
bool FableVideoCommandHasBinkIndexedWrapperDrawShape(
    const FableDirectRenderCommand& command);
bool FableVideoBinkWrapperPixelShaderHashAllowed(uint64_t pixel_hash);
bool FableVideoCommandUsesKnownVideoPixelShader(uint64_t pixel_hash);

bool FableVideoCommandIsEarlyBinkWrapperDrawTarget(
    const FableDirectRenderCommand& command, uint64_t pixel_hash);
bool FableVideoCommandIsStartupVideoCopyDrawTarget(
    const FableDirectRenderCommand& command, uint64_t pixel_hash,
    const FableTextureFetchInfo& slot0_fetch);

bool FableVideoBinkFetchInfoIsPlaneSlot(
    uint32_t slot,
    const FableTextureFetchInfo& y,
    const FableTextureFetchInfo& cr,
    const FableTextureFetchInfo& cb,
    const FableTextureFetchInfo& slot_info);
bool FableVideoCommandFetchesMatchBinkWrapperPlaneSet(
    const FableDirectRenderCommand& command,
    FableTextureFetchInfo* y_out = nullptr,
    FableTextureFetchInfo* cr_out = nullptr,
    FableTextureFetchInfo* cb_out = nullptr);
bool FableVideoCommandSlotUsesFetchDecodedBinkWrapperPlane(
    const FableDirectRenderCommand& command,
    uint32_t slot);

struct FableVideoBinkIndexedWrapperCandidateFacts {
  bool has_draw_shape = false;
  bool has_wrapper_symbol = false;
  bool uses_known_video_pixel_shader = false;
  bool pixel_shader_allowed = false;
  bool fetches_match_plane_set = false;
  bool has_ready_exact_planes = false;
};

bool FableVideoCommandLooksLikeBinkIndexedWrapperCandidate(
    const FableVideoBinkIndexedWrapperCandidateFacts& facts);

struct FableVideoPresentCandidateFacts {
  bool current_frame_has_video_draw = false;
  bool source_ready = false;
  bool frame_matches = false;
  bool present_target_candidate = false;
  uint32_t video_target = 0;
};

bool FableVideoCurrentFramePresentCandidateReady(
    const FableVideoPresentCandidateFacts& facts);

bool FableVideoClearMatchesExactBinkDrawTarget(
    const FableDirectRenderCommand& draw_command,
    const FableDirectRenderCommand& clear_command,
    bool draw_command_is_exact_bink);

uint32_t FableVideoNormalizeExactBinkClearOrder(
    std::vector<FableDirectRenderCommand>& submitted_commands,
    FableVideoExactBinkDrawPredicate is_exact_bink_draw);

void FableVideoLogBinkCommandOrder(
    const std::vector<FableDirectRenderCommand>& submitted_commands,
    bool verbose_tracing_enabled,
    uint32_t frame_index,
    uint32_t& logged_command_order,
    FableVideoExactBinkDrawPredicate is_exact_bink_draw);

}  // namespace refii::renderer
