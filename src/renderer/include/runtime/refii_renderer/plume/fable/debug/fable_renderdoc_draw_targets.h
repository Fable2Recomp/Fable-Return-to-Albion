#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"

#include <cstdint>
#include <string_view>

namespace refii::renderer {

struct FableRenderDocDrawTargetFacts {
  bool direct_draw = false;
  bool pix_video_draw = false;
  bool direct_video_draw = false;
  bool early_bink_wrapper_draw = false;
  bool exact_bink_indexed_wrapper_draw = false;
  bool startup_video_copy_draw = false;
  bool startup_video_pump_active = false;
  bool title_repeated_mesh_overlay = false;
  bool title_visible_proof_candidate = false;
  bool title_visible_frame_packet_candidate = false;
  bool title_gui_strip_proof_candidate = false;
  bool title_handoff_has_sample = false;
  bool tree_billboard_draw_target = false;
  uint64_t vertex_hash = 0;
  uint64_t pixel_hash = 0;
};

using FableRenderDocDrawCommandPredicate =
    bool (*)(const FableDirectRenderCommand& command);
using FableRenderDocShaderHashGetter = uint64_t (*)(uint32_t shader);
using FableRenderDocBoolGetter = bool (*)();

struct FableRenderDocDrawTargetFactCallbacks {
  FableRenderDocDrawCommandPredicate is_exact_bink_indexed_wrapper_draw =
      nullptr;
  FableRenderDocDrawCommandPredicate is_early_bink_wrapper_draw_target =
      nullptr;
  FableRenderDocDrawCommandPredicate is_startup_video_copy_draw_target =
      nullptr;
  FableRenderDocDrawCommandPredicate is_title_visible_proof_candidate =
      nullptr;
  FableRenderDocDrawCommandPredicate is_title_visible_frame_packet_candidate =
      nullptr;
  FableRenderDocDrawCommandPredicate is_title_gui_strip_proof_candidate =
      nullptr;
  FableRenderDocDrawCommandPredicate is_title_repeated_mesh_overlay = nullptr;
  FableRenderDocDrawCommandPredicate is_tree_billboard_draw_target = nullptr;
  FableRenderDocDrawCommandPredicate has_title_handoff_sample = nullptr;
  FableRenderDocBoolGetter startup_video_pump_active = nullptr;
  FableRenderDocShaderHashGetter vertex_shader_hash = nullptr;
  FableRenderDocShaderHashGetter pixel_shader_hash = nullptr;
};

FableRenderDocDrawTargetFacts BuildFableRenderDocDrawTargetFacts(
    std::string_view target,
    const FableDirectRenderCommand& command,
    const FableRenderDocDrawTargetFactCallbacks& callbacks);

bool FableRenderDocDrawTargetMatches(
    std::string_view target,
    const FableDirectRenderCommand& command,
    const FableRenderDocDrawTargetFacts& facts);

bool FableRenderDocResolveTargetMatches(
    std::string_view target,
    const FableDirectRenderCommand& command);

bool FableRenderDocTargetIsPixVideo(std::string_view target);
bool FableRenderDocTargetIsBinkVideo(std::string_view target);
bool FableRenderDocTargetIsTitleMenu(std::string_view target);
bool FableRenderDocTargetIsTitleVisible(std::string_view target);
bool FableRenderDocTargetIsTitleGuiStrip(std::string_view target);
bool FableRenderDocTargetUsesShaderHashFacts(std::string_view target);

}  // namespace refii::renderer
