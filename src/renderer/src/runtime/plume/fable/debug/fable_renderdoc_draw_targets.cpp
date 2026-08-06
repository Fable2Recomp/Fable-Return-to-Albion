#include "runtime/refii_renderer/plume/fable/debug/fable_renderdoc_draw_targets.h"

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_command_classification.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_constants.h"
#include "runtime/refii_renderer/plume/fable/video/fable_video_classification.h"

namespace refii::renderer {
namespace {

bool FableRenderDocPixelShaderDrawTargetMatches(
    const FableDirectRenderCommand& command,
    const FableRenderDocDrawTargetFacts& facts,
    uint64_t pixel_shader_hash) {
  return facts.direct_draw &&
         command.draw.source != RendererDrawEventSource::kPixVideo &&
         facts.pixel_hash == pixel_shader_hash;
}

bool FableRenderDocTreeBillboardDrawTargetMatches(
    const FableDirectRenderCommand& command,
    const FableRenderDocDrawTargetFacts& facts) {
  const bool tree_billboard_draw_source =
      command.draw.source == RendererDrawEventSource::kSceneObject ||
      command.draw.source == RendererDrawEventSource::kScratchWrapper;
  return facts.direct_draw && tree_billboard_draw_source &&
         command.resources.render_targets[0] == kFableTitleVisibleRenderTarget &&
         command.resources.textures[13] >= 0x4C000000u &&
         (facts.pixel_hash == kFableTitleTreeBillboardPixelHash ||
          facts.tree_billboard_draw_target);
}

bool FableRenderDocParticleMaterialDrawTargetMatches(
    const FableDirectRenderCommand& command,
    const FableRenderDocDrawTargetFacts& facts) {
  return facts.direct_draw &&
         command.resources.render_targets[0] == kFableTitleVisibleRenderTarget &&
         ((command.draw.source == RendererDrawEventSource::kD3DWrapper &&
           facts.vertex_hash == kFableRepeatedMeshesAlphaNoStippleVertexHash &&
           facts.pixel_hash == kFableParticleMaterialAnim1DepthPixelHash) ||
          facts.title_repeated_mesh_overlay);
}

bool FableRenderDocLandscapeMaterialDrawTargetMatches(
    const FableDirectRenderCommand& command,
    const FableRenderDocDrawTargetFacts& facts) {
  // Any landscape-material scene draw: they all share the repeated-meshes
  // vertex shader while the pixel shader varies per blend-layer count.
  return facts.direct_draw &&
         command.resources.render_targets[0] == kFableTitleVisibleRenderTarget &&
         facts.vertex_hash == kFableRepeatedMeshesAlphaNoStippleVertexHash;
}

bool FableRenderDocOverdrawVisualiseDrawTargetMatches(
    const FableDirectRenderCommand& command,
    const FableRenderDocDrawTargetFacts& facts) {
  return facts.direct_draw &&
         command.draw.source == RendererDrawEventSource::kSceneObject &&
         command.resources.render_targets[0] == kFableTitleVisibleRenderTarget &&
         facts.vertex_hash ==
             kFableTitleWaterTessellatorMistedZPassVertexHash &&
         facts.pixel_hash == kFableTitleOverdrawVisualisePixelHash;
}

bool FableRenderDocTitleHandoffDrawTargetMatches(
    const FableDirectRenderCommand& command,
    const FableRenderDocDrawTargetFacts& facts) {
  if (!facts.direct_draw ||
      command.draw.source == RendererDrawEventSource::kPixVideo ||
      command.resources.render_targets[0] == 0 ||
      command.resources.vertex_shader == 0 ||
      command.resources.pixel_shader == 0 ||
      !facts.title_handoff_has_sample) {
    return false;
  }

  return facts.title_repeated_mesh_overlay ||
         facts.pixel_hash == kFableTitleScreenFacingAtlasSpritePixelHash ||
         facts.pixel_hash == kFableScreenFacingSpritePixelHash ||
         facts.pixel_hash == kFableDecalPixelHash ||
         facts.pixel_hash == kFableTexturedQuadPreserveStencilPixelHash ||
         facts.pixel_hash ==
             kFableStandardMaterialBlt1Glt1Mist0Amb1Nrm1Scr1PixelHash;
}

bool FableRenderDocTitleMenuDrawTargetMatches(
    const FableDirectRenderCommand& command,
    const FableRenderDocDrawTargetFacts& facts) {
  if (!facts.direct_draw ||
      command.draw.source == RendererDrawEventSource::kPixVideo ||
      facts.direct_video_draw ||
      facts.startup_video_pump_active ||
      command.resources.render_targets[0] == 0 ||
      command.resources.vertex_shader == 0 ||
      command.resources.pixel_shader == 0) {
    return false;
  }

  return facts.title_visible_proof_candidate ||
         facts.title_gui_strip_proof_candidate;
}

}  // namespace

bool FableRenderDocTargetIsPixVideo(std::string_view target) {
  return target == "pixvideo" || target == "pix-video" ||
         target == "pix_video";
}

bool FableRenderDocTargetIsBinkVideo(std::string_view target) {
  return target == "binkvideo" || target == "bink-video" ||
         target == "bink_video";
}

bool FableRenderDocTargetIsTitleVisible(std::string_view target) {
  return target == "titlevisible" || target == "title-visible" ||
         target == "title_visible";
}

bool FableRenderDocTargetIsTitleGuiStrip(std::string_view target) {
  return target == "titleguistrip" || target == "title-gui-strip" ||
         target == "title_gui_strip";
}

bool FableRenderDocTargetUsesShaderHashFacts(std::string_view target) {
  return !FableRenderDocTargetIsPixVideo(target) &&
         !FableRenderDocTargetIsBinkVideo(target) &&
         !FableRenderDocTargetIsTitleMenu(target) &&
         !FableRenderDocTargetIsTitleVisible(target) &&
         !FableRenderDocTargetIsTitleGuiStrip(target);
}

FableRenderDocDrawTargetFacts BuildFableRenderDocDrawTargetFacts(
    std::string_view target,
    const FableDirectRenderCommand& command,
    const FableRenderDocDrawTargetFactCallbacks& callbacks) {
  FableRenderDocDrawTargetFacts facts;
  facts.direct_draw = IsDirectDrawCommand(command);
  facts.pix_video_draw = FableVideoCommandIsPixVideoDraw(command);

  const bool bink_target = FableRenderDocTargetIsBinkVideo(target);
  const bool title_menu_target = FableRenderDocTargetIsTitleMenu(target);
  if (bink_target || title_menu_target) {
    facts.exact_bink_indexed_wrapper_draw =
        callbacks.is_exact_bink_indexed_wrapper_draw != nullptr &&
        callbacks.is_exact_bink_indexed_wrapper_draw(command);
    facts.direct_video_draw =
        facts.pix_video_draw || facts.exact_bink_indexed_wrapper_draw;
  }
  if (bink_target) {
    facts.early_bink_wrapper_draw =
        callbacks.is_early_bink_wrapper_draw_target != nullptr &&
        callbacks.is_early_bink_wrapper_draw_target(command);
    facts.startup_video_copy_draw =
        callbacks.is_startup_video_copy_draw_target != nullptr &&
        callbacks.is_startup_video_copy_draw_target(command);
  }
  if (title_menu_target) {
    facts.startup_video_pump_active =
        callbacks.startup_video_pump_active != nullptr &&
        callbacks.startup_video_pump_active();
    facts.title_visible_proof_candidate =
        callbacks.is_title_visible_proof_candidate != nullptr &&
        callbacks.is_title_visible_proof_candidate(command);
    facts.title_gui_strip_proof_candidate =
        callbacks.is_title_gui_strip_proof_candidate != nullptr &&
        callbacks.is_title_gui_strip_proof_candidate(command);
  } else if (FableRenderDocTargetIsTitleVisible(target)) {
    facts.title_visible_proof_candidate =
        callbacks.is_title_visible_proof_candidate != nullptr &&
        callbacks.is_title_visible_proof_candidate(command);
    facts.title_visible_frame_packet_candidate =
        callbacks.is_title_visible_frame_packet_candidate != nullptr &&
        callbacks.is_title_visible_frame_packet_candidate(command);
    facts.title_gui_strip_proof_candidate =
        callbacks.is_title_gui_strip_proof_candidate != nullptr &&
        callbacks.is_title_gui_strip_proof_candidate(command);
  } else if (FableRenderDocTargetIsTitleGuiStrip(target)) {
    facts.title_gui_strip_proof_candidate =
        callbacks.is_title_gui_strip_proof_candidate != nullptr &&
        callbacks.is_title_gui_strip_proof_candidate(command);
  }
  if (target == "particlematerial" || target == "titlehandoff") {
    facts.title_repeated_mesh_overlay =
        callbacks.is_title_repeated_mesh_overlay != nullptr &&
        callbacks.is_title_repeated_mesh_overlay(command);
  }
  if (target == "titlehandoff") {
    facts.title_handoff_has_sample =
        callbacks.has_title_handoff_sample != nullptr &&
        callbacks.has_title_handoff_sample(command);
  }
  if (target == "treebillboard") {
    facts.tree_billboard_draw_target =
        callbacks.is_tree_billboard_draw_target != nullptr &&
        callbacks.is_tree_billboard_draw_target(command);
  }
  if (FableRenderDocTargetUsesShaderHashFacts(target)) {
    if (callbacks.vertex_shader_hash != nullptr) {
      facts.vertex_hash =
          callbacks.vertex_shader_hash(command.resources.vertex_shader);
    }
    if (callbacks.pixel_shader_hash != nullptr) {
      facts.pixel_hash =
          callbacks.pixel_shader_hash(command.resources.pixel_shader);
    }
  }
  return facts;
}

bool FableRenderDocDrawTargetMatches(
    std::string_view target,
    const FableDirectRenderCommand& command,
    const FableRenderDocDrawTargetFacts& facts) {
  if (target == "treebillboard") {
    return FableRenderDocTreeBillboardDrawTargetMatches(command, facts);
  }
  if (target == "particlematerial") {
    return FableRenderDocParticleMaterialDrawTargetMatches(command, facts);
  }
  if (target == "landscapematerial") {
    return FableRenderDocLandscapeMaterialDrawTargetMatches(command, facts);
  }
  if (target == "overdrawvisualise") {
    return FableRenderDocOverdrawVisualiseDrawTargetMatches(command, facts);
  }
  if (target == "postprocessingdownscale4") {
    return FableRenderDocPixelShaderDrawTargetMatches(
        command, facts, kFablePostprocessingDownscale4PixelHash);
  }
  if (target == "postprocessinggaussblur") {
    return FableRenderDocPixelShaderDrawTargetMatches(
        command, facts, kFablePostprocessingGaussBlurPixelHash);
  }
  if (target == "screenfacingsprite") {
    return FableRenderDocPixelShaderDrawTargetMatches(
        command, facts, kFableScreenFacingSpritePixelHash);
  }
  if (target == "decal") {
    return FableRenderDocPixelShaderDrawTargetMatches(
        command, facts, kFableDecalPixelHash);
  }
  if (target == "texturedquadpreservestencil") {
    return FableRenderDocPixelShaderDrawTargetMatches(
        command, facts, kFableTexturedQuadPreserveStencilPixelHash);
  }
  if (target == "titlehandoff") {
    return FableRenderDocTitleHandoffDrawTargetMatches(command, facts);
  }
  if (FableRenderDocTargetIsTitleMenu(target)) {
    return FableRenderDocTitleMenuDrawTargetMatches(command, facts);
  }
  if (FableRenderDocTargetIsTitleVisible(target)) {
    return facts.title_visible_proof_candidate ||
           facts.title_visible_frame_packet_candidate ||
           facts.title_gui_strip_proof_candidate;
  }
  if (FableRenderDocTargetIsTitleGuiStrip(target)) {
    return facts.title_gui_strip_proof_candidate;
  }
  if (FableRenderDocTargetIsPixVideo(target)) {
    return facts.pix_video_draw;
  }
  if (FableRenderDocTargetIsBinkVideo(target)) {
    return facts.pix_video_draw ||
           facts.early_bink_wrapper_draw ||
           facts.exact_bink_indexed_wrapper_draw ||
           facts.startup_video_copy_draw;
  }
  return false;
}

bool FableRenderDocResolveTargetMatches(
    std::string_view target,
    const FableDirectRenderCommand& command) {
  return (target == "resolvecopy" || target == "pm4resolve" ||
          target == "resolve") &&
         command.type == FableDirectRenderCommandType::kResolveCopy;
}

bool FableRenderDocTargetIsTitleMenu(std::string_view target) {
  return target == "titlemenu" || target == "title-menu" ||
         target == "title_menu";
}

}  // namespace refii::renderer
