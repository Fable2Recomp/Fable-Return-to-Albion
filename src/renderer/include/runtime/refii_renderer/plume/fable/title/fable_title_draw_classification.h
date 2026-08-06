#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"

#include <array>
#include <cstdint>

namespace refii::renderer {

enum class FableTitleRenderPass : uint8_t {
  kUnknown,
  kPrivateRectCopy,
  kGuiText,
  kPressALegalText,
  kBackgroundWater,
  kDepthPrepass,
  kLogoBillboard,
  kPostprocess,
  kTitleTarget,
};

struct FableTitleTextureBindingFacts {
  uint32_t guest = 0;
  uint32_t tracked = 0;
  uint32_t base = 0;
  bool scene_scaler_texture = false;
  bool private_texture_proxy = false;
};

struct FableTitleRenderPassFacts {
  bool synthetic_private_rect_copy = false;
  bool gui_text_declaration = false;
  bool title_indexed_declaration = false;
  bool gui_text_3d_pixel_shader_address = false;
  bool source_slot_gui_text_atlas = false;
  bool tone_map_draw = false;
  bool title_scene_draw_target = false;
  uint64_t vertex_hash = 0;
  uint64_t pixel_hash = 0;
  FableTitleTextureBindingFacts first_texture;
  FableTitleTextureBindingFacts source_texture;
};

using FableTitleCommandTextureFetchDwordsGetter =
    std::array<uint32_t, 6> (*)(const FableDirectRenderCommand& command,
                                uint32_t slot);

const char* FableTitleRenderPassName(FableTitleRenderPass pass);

bool FableTitleRenderPassNeedsInitializedTarget(FableTitleRenderPass pass);

bool FableTitleTargetIsExactVisibleOrScene(uint32_t address);

bool FableTitleTextureBindingTouchesPrivateChain(
    const FableTitleTextureBindingFacts& binding);

FableTitleRenderPass FableTitleRenderPassForCommand(
    const FableDirectRenderCommand& command,
    const FableTitleRenderPassFacts& facts);

bool FableTitleCommandWritesVisibleTarget(
    const FableDirectRenderCommand& command,
    bool render_target_is_dynamic_title_scene);

bool FableTitleCommandIsRepeatedMeshOverlay(
    const FableDirectRenderCommand& command,
    bool writes_visible_target,
    uint64_t vertex_hash,
    uint64_t pixel_hash);

bool FableTitleCommandIsParticleSceneDraw(
    const FableDirectRenderCommand& command,
    bool writes_visible_target,
    uint64_t vertex_hash,
    uint64_t pixel_hash);

bool FableTitleCommandIsGuiStripProofCandidate(
    const FableDirectRenderCommand& command,
    bool gui_quad_declaration,
    uint64_t vertex_hash,
    uint64_t pixel_hash);

bool FableTitleCommandIsGuiOverdrawVisualise(
    const FableDirectRenderCommand& command,
    bool is_direct_draw,
    bool gui_text_atlas_command,
    uint64_t vertex_hash,
    uint64_t pixel_hash);

bool FableTitleCommandIsVisibleProofCandidate(
    const FableDirectRenderCommand& command,
    bool is_direct_draw,
    bool writes_visible_target,
    bool gui_overdraw_visualise,
    bool repeated_mesh_overlay,
    uint64_t vertex_hash,
    uint64_t pixel_hash);

bool FableTitleCommandHasHandoffSample(
    const FableDirectRenderCommand& command,
    uint32_t texture_slot_mask,
    FableTitleCommandTextureFetchDwordsGetter texture_fetch_dwords);

bool FableTitleCommandSlotIsExactBeginVerticesTitleStrip(
    const FableDirectRenderCommand& command,
    uint32_t slot,
    const FableTextureFetchInfo& fetch_info);

bool FableTitleResourceMatchesExactBeginVerticesTitleStripFetch(
    bool image_ready,
    uint32_t base_address,
    uint32_t width,
    uint32_t height,
    uint32_t format,
    bool fetch_dwords_match,
    const FableTextureFetchInfo& fetch_info);

bool FableTitleResourceIsExactBeginVerticesTitleStripSample(
    bool image_ready,
    uint32_t base_address,
    uint32_t width,
    uint32_t height,
    uint32_t format,
    bool has_sampleable_or_produced_content,
    bool command_binds_resource_as_target);

}  // namespace refii::renderer
