#include "runtime/refii_renderer/plume/fable/title/fable_title_draw_classification.h"

#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_private_rect_symbols.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_classification.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_constants.h"

namespace refii::renderer {

const char* FableTitleRenderPassName(FableTitleRenderPass pass) {
  switch (pass) {
    case FableTitleRenderPass::kUnknown:
      return "unknown";
    case FableTitleRenderPass::kPrivateRectCopy:
      return "private-rect-copy";
    case FableTitleRenderPass::kGuiText:
      return "gui-text";
    case FableTitleRenderPass::kPressALegalText:
      return "press-a/legal-text";
    case FableTitleRenderPass::kBackgroundWater:
      return "background-water";
    case FableTitleRenderPass::kDepthPrepass:
      return "depth-prepass";
    case FableTitleRenderPass::kLogoBillboard:
      return "logo/billboard";
    case FableTitleRenderPass::kPostprocess:
      return "postprocess";
    case FableTitleRenderPass::kTitleTarget:
      return "title-target";
  }
  return "unknown";
}

bool FableTitleRenderPassNeedsInitializedTarget(FableTitleRenderPass pass) {
  switch (pass) {
    case FableTitleRenderPass::kGuiText:
    case FableTitleRenderPass::kPressALegalText:
    case FableTitleRenderPass::kBackgroundWater:
    case FableTitleRenderPass::kDepthPrepass:
    case FableTitleRenderPass::kLogoBillboard:
    case FableTitleRenderPass::kPostprocess:
    case FableTitleRenderPass::kTitleTarget:
      return true;
    case FableTitleRenderPass::kUnknown:
    case FableTitleRenderPass::kPrivateRectCopy:
      return false;
  }
  return false;
}

bool FableTitleTargetIsExactVisibleOrScene(uint32_t address) {
  return address == kFableTitleVisibleRenderTarget ||
         address == kFableTitleSceneRenderTarget;
}

bool FableTitleTextureBindingTouchesPrivateChain(
    const FableTitleTextureBindingFacts& binding) {
  return IsFableTitlePrivateTextureProxyAddress(binding.guest) ||
         IsFableTitlePrivateTextureProxyAddress(binding.tracked) ||
         IsFableTitlePrivateTextureProxyAddress(binding.base) ||
         binding.guest == 0x404D6D10u || binding.tracked == 0x404D6D10u ||
         binding.guest == 0x404D6DD0u || binding.tracked == 0x404D6DD0u ||
         binding.guest == 0x4C132C70u || binding.tracked == 0x4C132C70u ||
         binding.guest == 0x4C132CD8u || binding.tracked == 0x4C132CD8u ||
         binding.base == 0x16C40000u ||
         binding.guest == 0x44127210u || binding.tracked == 0x44127210u ||
         binding.guest == 0x44127278u || binding.tracked == 0x44127278u ||
         binding.guest == 0x441272ACu || binding.tracked == 0x441272ACu ||
         binding.guest == 0x441272E0u || binding.tracked == 0x441272E0u ||
         binding.guest == 0x44127314u || binding.tracked == 0x44127314u ||
         binding.guest == 0x44127480u || binding.tracked == 0x44127480u ||
         binding.guest == 0x441274B4u || binding.tracked == 0x441274B4u ||
         binding.guest == 0x441274E8u || binding.tracked == 0x441274E8u ||
         binding.guest == 0x4412751Cu || binding.tracked == 0x4412751Cu ||
         binding.base == 0x13520000u || binding.base == 0x13530000u ||
         binding.base == 0x13540000u || binding.base == 0x13580000u ||
         binding.base == 0x13590000u ||
         binding.guest == 0x404DDDD0u || binding.tracked == 0x404DDDD0u ||
         binding.base == 0x1A0EA000u;
}

FableTitleRenderPass FableTitleRenderPassForCommand(
    const FableDirectRenderCommand& command,
    const FableTitleRenderPassFacts& facts) {
  if (command.draw.private_texture_rectangle ||
      facts.synthetic_private_rect_copy) {
    return FableTitleRenderPass::kPrivateRectCopy;
  }

  if (facts.gui_text_declaration ||
      facts.vertex_hash == kFableTitleGuiText3DVertexHash ||
      facts.pixel_hash == kFableTitleGuiText3DPixelShaderHash ||
      facts.gui_text_3d_pixel_shader_address) {
    return FableTitleRenderPass::kGuiText;
  }

  if (command.resources.pixel_shader == kFableTitleSceneTextPixelShader ||
      facts.source_slot_gui_text_atlas) {
    return FableTitleRenderPass::kPressALegalText;
  }

  // The title's billboard/repopulate tail deliberately reuses the water
  // z-pass vertex program with a billboard pixel program. Classify the
  // combined draw before the VS-only depth heuristic; SBK names are not a
  // reliable pass identity on their own.
  if (facts.title_indexed_declaration ||
      facts.pixel_hash == kFableTitleTreeBillboardPixelHash) {
    return FableTitleRenderPass::kLogoBillboard;
  }

  if (facts.vertex_hash == kFableTitleWaterTessellatorZPassVertexHash ||
      facts.vertex_hash == kFableTitleWaterTessellatorMistedZPassVertexHash) {
    return FableTitleRenderPass::kDepthPrepass;
  }

  if (facts.vertex_hash == kFableTitleOceanWaterVertexHash ||
      facts.vertex_hash == kFableTitleOceanWaterMistedVertexHash ||
      facts.pixel_hash == kFableTitleTextureMorphPixelHash) {
    return FableTitleRenderPass::kBackgroundWater;
  }

  if (facts.tone_map_draw || facts.first_texture.scene_scaler_texture ||
      facts.source_texture.scene_scaler_texture ||
      facts.first_texture.private_texture_proxy ||
      facts.source_texture.private_texture_proxy) {
    return FableTitleRenderPass::kPostprocess;
  }

  if (facts.title_scene_draw_target) {
    return FableTitleRenderPass::kTitleTarget;
  }

  return FableTitleRenderPass::kUnknown;
}

bool FableTitleCommandWritesVisibleTarget(
    const FableDirectRenderCommand& command,
    bool render_target_is_dynamic_title_scene) {
  const uint32_t render_target = command.resources.render_targets[0];
  return render_target == kFableTitleVisibleRenderTarget ||
         render_target_is_dynamic_title_scene;
}

bool FableTitleCommandIsRepeatedMeshOverlay(
    const FableDirectRenderCommand& command,
    bool writes_visible_target,
    uint64_t vertex_hash,
    uint64_t pixel_hash) {
  if (command.draw.source != RendererDrawEventSource::kSceneObject ||
      !writes_visible_target ||
      command.resources.textures[0] == 0 ||
      command.resources.textures[1] == 0) {
    return false;
  }

  return vertex_hash == kFableRepeatedMeshesAlphaNoStippleVertexHash &&
         pixel_hash == kFableLandscapeMaterialBlt2Glt1Mist1Amb1Clf1PixelHash;
}

bool FableTitleCommandIsParticleSceneDraw(
    const FableDirectRenderCommand& command,
    bool writes_visible_target,
    uint64_t vertex_hash,
    uint64_t pixel_hash) {
  return command.draw.source == RendererDrawEventSource::kSceneObject &&
         writes_visible_target &&
         vertex_hash == kFableRepeatedMeshesAlphaNoStippleVertexHash &&
         pixel_hash == kFableParticleMaterialAnim1DepthPixelHash;
}

bool FableTitleCommandIsGuiStripProofCandidate(
    const FableDirectRenderCommand& command,
    bool gui_quad_declaration,
    uint64_t vertex_hash,
    uint64_t pixel_hash) {
  const bool title_draw_source =
      command.draw.source == RendererDrawEventSource::kSceneObject ||
      command.draw.source == RendererDrawEventSource::kScratchWrapper;
  if (!title_draw_source ||
      command.resources.render_targets[0] != kFableTitleVisibleRenderTarget ||
      command.resources.textures[1] == 0 ||
      command.resources.textures[2] == 0 ||
      command.draw.draw.indexed ||
      command.draw.draw.primitive_type != 8u ||
      command.draw.draw.primitive_count != 3u) {
    return false;
  }

  const uint32_t scene_sample = command.resources.textures[1];
  const bool samples_title_present_source =
      scene_sample == 0x40130CD0u ||
      scene_sample == kFableTitleSceneScalerTexture ||
      scene_sample == 0x40131CD0u || scene_sample == 0x40131D10u ||
      scene_sample == 0x40133CD0u || scene_sample == 0x40133D10u ||
      scene_sample == 0x40136CD0u || scene_sample == 0x40136D10u ||
      scene_sample == 0x40137CD0u ||
      scene_sample == 0x40137D10u;
  if (!samples_title_present_source) {
    return false;
  }

  return gui_quad_declaration &&
         vertex_hash == kFableTitleGuiText3DVertexHash &&
         pixel_hash ==
             kFableStandardMaterialBlt1Glt1Mist0Amb1Nrm1Scr1PixelHash;
}

bool FableTitleCommandIsGuiOverdrawVisualise(
    const FableDirectRenderCommand& command,
    bool is_direct_draw,
    bool gui_text_atlas_command,
    uint64_t vertex_hash,
    uint64_t pixel_hash) {
  return is_direct_draw &&
         command.draw.source == RendererDrawEventSource::kSceneObject &&
         command.resources.render_targets[0] == kFableTitleVisibleRenderTarget &&
         vertex_hash == kFableTitleWaterTessellatorMistedZPassVertexHash &&
         pixel_hash == kFableTitleOverdrawVisualisePixelHash &&
         gui_text_atlas_command;
}

bool FableTitleCommandIsVisibleProofCandidate(
    const FableDirectRenderCommand& command,
    bool is_direct_draw,
    bool writes_visible_target,
    bool gui_overdraw_visualise,
    bool repeated_mesh_overlay,
    uint64_t vertex_hash,
    uint64_t pixel_hash) {
  if (!is_direct_draw ||
      command.draw.source == RendererDrawEventSource::kPixVideo ||
      !writes_visible_target ||
      command.resources.vertex_shader == 0 ||
      command.resources.pixel_shader == 0) {
    return false;
  }

  if (gui_overdraw_visualise || repeated_mesh_overlay) {
    return true;
  }

  if (command.resources.textures[0] == 0) {
    return false;
  }

  if (vertex_hash != kFableRepeatedMeshesAlphaNoStippleVertexHash) {
    return false;
  }

  return pixel_hash == kFableParticleMaterialAnim1DepthPixelHash ||
         pixel_hash == kFableLandscapeMaterialBlt2Glt1Mist1Amb1Clf1PixelHash ||
         pixel_hash == kFableLandscapeMaterialBlt7Glt1Mist0Amb1Clf1PixelHash;
}

bool FableTitleCommandHasHandoffSample(
    const FableDirectRenderCommand& command,
    uint32_t texture_slot_mask,
    FableTitleCommandTextureFetchDwordsGetter texture_fetch_dwords) {
  if (texture_fetch_dwords == nullptr) {
    return false;
  }

  for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
    if (slot >= 32u || (texture_slot_mask & (1u << slot)) == 0 ||
        command.resources.textures[slot] == 0) {
      continue;
    }

    const FableTextureFetchInfo fetch =
        FableTextureFetchInfoFromDwords(texture_fetch_dwords(command, slot));
    if (!fetch.valid) {
      continue;
    }
    if (fetch.width == 1u && fetch.height == 1u) {
      continue;
    }
    return true;
  }
  return false;
}

bool FableTitleCommandSlotIsExactBeginVerticesTitleStrip(
    const FableDirectRenderCommand& command,
    uint32_t slot,
    const FableTextureFetchInfo& fetch_info) {
  return slot < command.resources.textures.size() &&
         command.draw.private_texture_wrapper_symbol_address ==
             kFableBeginVerticesTitleWrapperSymbol &&
         fetch_info.valid && fetch_info.base_address == 0x1FAA0000u &&
         fetch_info.width == 512u && fetch_info.height == 1u &&
         fetch_info.format == 36u;
}

bool FableTitleResourceMatchesExactBeginVerticesTitleStripFetch(
    bool image_ready,
    uint32_t base_address,
    uint32_t width,
    uint32_t height,
    uint32_t format,
    bool fetch_dwords_match,
    const FableTextureFetchInfo& fetch_info) {
  return image_ready && fetch_info.valid &&
         base_address == fetch_info.base_address &&
         width == fetch_info.width && height == fetch_info.height &&
         format == fetch_info.format && fetch_dwords_match;
}

bool FableTitleResourceIsExactBeginVerticesTitleStripSample(
    bool image_ready,
    uint32_t base_address,
    uint32_t width,
    uint32_t height,
    uint32_t format,
    bool has_sampleable_or_produced_content,
    bool command_binds_resource_as_target) {
  return image_ready && base_address == 0x1FAA0000u && width == 512u &&
         height == 1u && format == 36u &&
         has_sampleable_or_produced_content &&
         !command_binds_resource_as_target;
}

}  // namespace refii::renderer
