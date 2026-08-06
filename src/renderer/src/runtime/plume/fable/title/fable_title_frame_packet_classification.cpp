#include "runtime/refii_renderer/plume/fable/title/fable_title_frame_packet_classification.h"

#include "runtime/refii_renderer/plume/fable/fable_guest_display.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_synthetic_private_rect.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_classification.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_constants.h"

#include <algorithm>
#include <cmath>

namespace refii::renderer {

bool FableTitleSceneTargetCandidate(uint32_t address,
                                    uint32_t width,
                                    uint32_t height) {
  // Scene-framebuffer floor tracks the live guest dims (qword_834A5FE8).
  if (address == 0 || width < FableGuestDisplayWidth() ||
      height < FableGuestDisplayHeight()) {
    return false;
  }

  const uint64_t wide = uint64_t(width) * 9u;
  const uint64_t tall = uint64_t(height) * 16u;
  const uint64_t delta = wide > tall ? wide - tall : tall - wide;
  const uint64_t tolerance =
      std::max<uint64_t>(1u, std::max(wide, tall) / 32u);
  return delta <= tolerance;
}

bool FableTitlePresentTargetCandidate(uint32_t address,
                                      uint32_t width,
                                      uint32_t height) {
  if (FableTitleSceneTargetCandidate(address, width, height)) {
    return true;
  }
  if (address == 0 || width < 480u || height < 270u) {
    return false;
  }

  const uint64_t wide = uint64_t(width) * 9u;
  const uint64_t tall = uint64_t(height) * 16u;
  const uint64_t delta = wide > tall ? wide - tall : tall - wide;
  const uint64_t tolerance =
      std::max<uint64_t>(1u, std::max(wide, tall) / 32u);
  return delta <= tolerance;
}

bool FableTitleSurfaceDestinationRootIsWrapper(uint32_t symbol_address) {
  return symbol_address == 0x821EBF30u ||
         symbol_address == 0x82217DB8u ||
         symbol_address == 0x82205F68u;
}

bool FableTitlePresentSourceCanUseSameFrameProducer(
    const FableTitlePresentSourceFacts& facts) {
  return facts.source_ready && facts.target != 0 && facts.write_serial != 0 &&
         facts.frame_matches;
}

bool FableTitleFramePacketExtentsCoverTarget(
    const FableDirectFrameDatabaseDrawPacket& packet,
    uint32_t target_width,
    uint32_t target_height) {
  if (target_width == 0 || target_height == 0) {
    return false;
  }

  auto extent_covers_target = [](float width, float height,
                                uint32_t target_width,
                                uint32_t target_height) {
    return std::isfinite(width) && std::isfinite(height) &&
           width >= static_cast<float>(target_width) * 0.95f &&
           height >= static_cast<float>(target_height) * 0.95f;
  };
  const bool viewport_covers =
      packet.viewport_valid &&
      extent_covers_target(packet.viewport_width, packet.viewport_height,
                           target_width, target_height);
  const bool scissor_covers =
      packet.scissor_valid && packet.scissor_right > packet.scissor_left &&
      packet.scissor_bottom > packet.scissor_top &&
      extent_covers_target(
          static_cast<float>(packet.scissor_right - packet.scissor_left),
          static_cast<float>(packet.scissor_bottom - packet.scissor_top),
          target_width, target_height);
  return viewport_covers || scissor_covers;
}

uint32_t FableTitleFramePacketInputStride(
    const FableDirectFrameDatabaseDrawPacket& packet,
    uint32_t slot) {
  if (slot >= packet.vertex_strides.size()) {
    return 0;
  }
  if (packet.vertex_strides[slot] != 0) {
    return packet.vertex_strides[slot];
  }
  if (slot < packet.inline_vertex_buffers.size() &&
      packet.inline_vertex_buffers[slot].stride_or_format != 0) {
    return packet.inline_vertex_buffers[slot].stride_or_format;
  }
  return 0;
}

bool FableTitleFramePacketUsesBillboardAtlas(
    const FableDirectFrameDatabaseDrawPacket& packet) {
  const auto is_title_billboard_atlas = [](uint32_t texture) {
    return texture == kFableCanonicalTitleBillboardBase ||
           texture == kFableTitleBillboardCommandTexture ||
           texture == kFableTitleBillboardCommandTextureAlias ||
           texture == kFableTitleBillboardCommandSource ||
           texture == kFableTitleBillboardCommandSourceAlias ||
           IsFableTitleTextureIndirectionChainAddress(texture);
  };

  constexpr uint32_t atlas_slot = kFablePrivateRectCopyTextureSlot;
  for (uint32_t slot : {atlas_slot, atlas_slot + 1u, atlas_slot + 2u}) {
    if (slot < packet.textures.size() &&
        is_title_billboard_atlas(packet.textures[slot])) {
      return true;
    }
  }
  return false;
}

bool FableTitleFramePacketCanProducePresentColor(
    const FableDirectFrameDatabaseDrawPacket& packet,
    bool full_frame_private_copy) {
  if (packet.pass_name == "PrivateRectCopy") {
    return full_frame_private_copy;
  }
  return packet.pass_name != "PrivateRectDepth" &&
         packet.pass_name != "DepthOnly";
}

bool FableTitleFramePacketTargetsContain(
    const FableDirectFrameDatabaseDrawPacket& packet,
    uint32_t target) {
  return std::find(packet.render_targets.begin(), packet.render_targets.end(),
                   target) != packet.render_targets.end();
}

namespace {

bool FableTitleFramePacketIsFullFrameCompositeFromDerivedInputs(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    bool writes_visible_target,
    bool logo_billboard_overlay,
    const FableTitleFramePacketShaderFacts& facts) {
  if (packet.pass_name != "PostProcessOrUI" || !writes_visible_target) {
    return false;
  }

  for (const FableTitleFramePacketTargetFacts& target : targets) {
    if (target.target == 0 || !target.image_ready ||
        !target.present_target_candidate) {
      continue;
    }
    return FableTitleFramePacketIsFullFrameComposite(
        packet, writes_visible_target, target.width, target.height,
        logo_billboard_overlay, facts);
  }
  return false;
}

}  // namespace

bool FableTitleFramePacketWritesTitleVisibleTarget(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets) {
  for (const FableTitleFramePacketTargetFacts& target : targets) {
    if (target.target != 0 &&
        (target.target == kFableTitleVisibleRenderTarget ||
         target.target == kFableTitleSceneRenderTarget ||
         target.title_scene_target)) {
      return true;
    }
  }
  return false;
}

FableTitleFramePacketDerivedFacts BuildFableTitleFramePacketDerivedFacts(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    const FableTitleFramePacketShaderFacts& facts) {
  FableTitleFramePacketDerivedFacts derived;
  derived.writes_visible_target =
      FableTitleFramePacketWritesTitleVisibleTarget(packet, targets);
  derived.full_frame_private_copy =
      FableTitleFramePacketIsFullFramePrivateCopy(packet, targets, facts);
  derived.logo_billboard_overlay = FableTitleFramePacketIsLogoBillboardOverlay(
      packet, derived.writes_visible_target, facts);
  derived.repopulate_tail = FableTitleFramePacketIsRepopulateTail(
      packet, derived.writes_visible_target, facts);
  derived.full_frame_composite =
      FableTitleFramePacketIsFullFrameCompositeFromDerivedInputs(
          packet, targets, derived.writes_visible_target,
          derived.logo_billboard_overlay, facts);
  derived.can_produce_present_color = FableTitleFramePacketCanProducePresentColor(
      packet, derived.full_frame_private_copy);
  derived.title_present_producer = FableTitleFramePacketIsPresentProducer(
      derived.can_produce_present_color, derived.full_frame_composite,
      derived.full_frame_private_copy);
  derived.overlay_only = FableTitleFramePacketIsOverlayOnly(
      packet, derived.full_frame_private_copy, derived.full_frame_composite,
      derived.repopulate_tail, derived.logo_billboard_overlay, facts);
  derived.visible_producer = FableTitleFramePacketIsVisibleProducer(
      packet, derived.can_produce_present_color, derived.writes_visible_target,
      derived.full_frame_composite, derived.full_frame_private_copy,
      derived.repopulate_tail, derived.logo_billboard_overlay, facts);
  derived.full_scene_producer =
      derived.can_produce_present_color && !derived.overlay_only;
  return derived;
}

bool FableTitleFramePacketIsFullFramePrivateCopy(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    const FableTitleFramePacketShaderFacts& facts) {
  const RendererDrawEventSource source =
      static_cast<RendererDrawEventSource>(packet.source);
  if (source != RendererDrawEventSource::kPrivateTextureRectangle ||
      packet.pass_name != "PrivateRectCopy" || !packet.private_texture.enabled ||
      !facts.synthetic_private_rect_copy_shader) {
    return false;
  }

  bool writes_color = false;
  for (uint32_t mask : packet.color_write_masks) {
    writes_color = writes_color || mask != 0;
  }
  if (!writes_color) {
    return false;
  }

  for (const FableTitleFramePacketTargetFacts& target : targets) {
    if (target.target == 0 || !target.title_scene_target ||
        !target.image_ready || !target.scene_target_candidate) {
      continue;
    }
    if (FableTitleFramePacketExtentsCoverTarget(packet, target.width,
                                               target.height)) {
      return true;
    }
  }
  return false;
}

bool FableTitleFramePacketCanProducePresentColor(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    const FableTitleFramePacketShaderFacts& facts) {
  return BuildFableTitleFramePacketDerivedFacts(packet, targets, facts)
      .can_produce_present_color;
}

bool FableTitleFramePacketIsLogoBillboardOverlay(
    const FableDirectFrameDatabaseDrawPacket& packet,
    bool writes_visible_target,
    const FableTitleFramePacketShaderFacts& facts) {
  if (static_cast<RendererDrawEventSource>(packet.source) !=
          RendererDrawEventSource::kSceneObject ||
      !packet.indexed || packet.primitive_type != 4u ||
      packet.primitive_count > 12u ||
      !writes_visible_target ||
      !FableTitleFramePacketUsesBillboardAtlas(packet)) {
    return false;
  }

  const bool title_indexed_declaration =
      facts.title_indexed_declaration || facts.title_billboard_mesh_declaration;
  const uint32_t stride = FableTitleFramePacketInputStride(packet, 0u);
  const bool title_billboard_wrapper_layout =
      packet.primitive_count == 6u && stride == 72u &&
      packet.index_format != 0u;
  if (!title_indexed_declaration && !title_billboard_wrapper_layout) {
    return false;
  }

  const bool title_billboard_vertex =
      facts.title_scene_vertex_shader ||
      (facts.vertex_hash == kFableTitleWaterTessellatorZPassVertexHash &&
       (stride == 0u || stride == 72u));
  const bool title_billboard_pixel =
      facts.pixel_hash == kFableTitleTreeBillboardPixelHash ||
      facts.pixel_hash == kFableTitleScreenFacingAtlasSpritePixelHash;
  return title_billboard_vertex || title_billboard_pixel ||
         title_billboard_wrapper_layout;
}

bool FableTitleFramePacketIsLogoBillboardOverlay(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    const FableTitleFramePacketShaderFacts& facts) {
  return FableTitleFramePacketIsLogoBillboardOverlay(
      packet, FableTitleFramePacketWritesTitleVisibleTarget(packet, targets),
      facts);
}

bool FableTitleFramePacketIsRepopulateTail(
    const FableDirectFrameDatabaseDrawPacket& packet,
    bool writes_visible_target,
    const FableTitleFramePacketShaderFacts& facts) {
  if (static_cast<RendererDrawEventSource>(packet.source) !=
          RendererDrawEventSource::kSceneObject ||
      !packet.indexed ||
      !writes_visible_target) {
    return false;
  }

  const bool tree_billboard_tail =
      facts.vertex_hash == kFableTitleWaterTessellatorZPassVertexHash &&
      facts.pixel_hash == kFableTitleTreeBillboardPixelHash &&
      packet.primitive_count <= 12u;
  const bool overdraw_visualise_tail =
      facts.vertex_hash == kFableTitleWaterTessellatorMistedZPassVertexHash &&
      facts.pixel_hash == kFableTitleOverdrawVisualisePixelHash &&
      packet.primitive_count <= 64u;
  return tree_billboard_tail || overdraw_visualise_tail;
}

bool FableTitleFramePacketIsRepopulateTail(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    const FableTitleFramePacketShaderFacts& facts) {
  return FableTitleFramePacketIsRepopulateTail(
      packet, FableTitleFramePacketWritesTitleVisibleTarget(packet, targets),
      facts);
}

bool FableTitleFramePacketIsFullFrameComposite(
    const FableDirectFrameDatabaseDrawPacket& packet,
    bool writes_visible_target,
    uint32_t target_width,
    uint32_t target_height,
    bool logo_billboard_overlay,
    const FableTitleFramePacketShaderFacts& facts) {
  if (packet.pass_name != "PostProcessOrUI" || !writes_visible_target) {
    return false;
  }
  if (!FableTitleFramePacketExtentsCoverTarget(packet, target_width,
                                              target_height)) {
    return false;
  }
  if (facts.title_gui_quad_declaration || logo_billboard_overlay) {
    return false;
  }
  if (facts.vertex_hash == kFableRepeatedMeshesAlphaNoStippleVertexHash &&
      facts.pixel_hash == kFableParticleMaterialAnim1DepthPixelHash) {
    return FableTitleFramePacketIsTitleMenuParticleComposite(
        packet, writes_visible_target, target_width, target_height, facts);
  }
  if (facts.vertex_hash == kFableTitleGuiText3DVertexHash ||
      facts.pixel_hash == kFableTitleGuiText3DPixelShaderHash ||
      facts.title_gui_text_3d_pixel_shader_address ||
      packet.pixel_shader == kFableTitleSceneTextPixelShader) {
    return false;
  }
  return true;
}

bool FableTitleFramePacketIsFullFrameComposite(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    const FableTitleFramePacketShaderFacts& facts) {
  return BuildFableTitleFramePacketDerivedFacts(packet, targets, facts)
      .full_frame_composite;
}

bool FableTitleFramePacketIsTitleMenuParticleComposite(
    const FableDirectFrameDatabaseDrawPacket& packet,
    bool writes_visible_target,
    uint32_t target_width,
    uint32_t target_height,
    const FableTitleFramePacketShaderFacts& facts) {
  const RendererDrawEventSource source =
      static_cast<RendererDrawEventSource>(packet.source);
  return source == RendererDrawEventSource::kSceneObject &&
         packet.pass_name == "PostProcessOrUI" && writes_visible_target &&
         facts.vertex_hash == kFableRepeatedMeshesAlphaNoStippleVertexHash &&
         facts.pixel_hash == kFableParticleMaterialAnim1DepthPixelHash &&
         !packet.indexed && packet.primitive_type == 13u &&
         packet.primitive_count >= 16u && packet.depth_stencil == 0 &&
         packet.textures[0] != 0 && packet.textures[1] != 0 &&
         FableTitleFramePacketExtentsCoverTarget(packet, target_width,
                                                target_height);
}

bool FableTitleFramePacketIsPresentProducer(
    bool can_produce_present_color,
    bool full_frame_composite,
    bool full_frame_private_copy) {
  return can_produce_present_color &&
         (full_frame_composite || full_frame_private_copy);
}


bool FableTitleFramePacketIsOverlayOnly(
    const FableDirectFrameDatabaseDrawPacket& packet,
    bool full_frame_private_copy,
    bool full_frame_composite,
    bool repopulate_tail,
    bool logo_billboard_overlay,
    const FableTitleFramePacketShaderFacts& facts) {
  const RendererDrawEventSource source =
      static_cast<RendererDrawEventSource>(packet.source);
  if (source == RendererDrawEventSource::kPrivateTextureRectangle) {
    return !full_frame_private_copy;
  }
  if (source == RendererDrawEventSource::kPixVideo) {
    return true;
  }
  if (packet.pass_name == "PrivateRectDepth" ||
      packet.pass_name == "DepthOnly") {
    return true;
  }
  if (packet.pass_name == "PrivateRectCopy") {
    return !full_frame_private_copy;
  }
  if (packet.pass_name == "PostProcessOrUI" && !full_frame_composite) {
    return true;
  }
  if (repopulate_tail) {
    return true;
  }

  if (facts.title_gui_quad_declaration ||
      facts.vertex_hash == kFableTitleGuiText3DVertexHash ||
      facts.pixel_hash == kFableTitleGuiText3DPixelShaderHash ||
      facts.title_gui_text_3d_pixel_shader_address ||
      packet.pixel_shader == kFableTitleSceneTextPixelShader) {
    return true;
  }
  if (logo_billboard_overlay) {
    return true;
  }

  return source == RendererDrawEventSource::kSceneObject && !packet.indexed &&
         packet.primitive_type == 8u && packet.primitive_count <= 3u &&
         packet.depth_stencil == 0;
}

bool FableTitleFramePacketIsOverlayOnly(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    const FableTitleFramePacketShaderFacts& facts) {
  return BuildFableTitleFramePacketDerivedFacts(packet, targets, facts)
      .overlay_only;
}

bool FableTitleFramePacketIsVisibleProducer(
    const FableDirectFrameDatabaseDrawPacket& packet,
    bool can_produce_present_color,
    bool writes_visible_target,
    bool full_frame_composite,
    bool full_frame_private_copy,
    bool repopulate_tail,
    bool logo_billboard_overlay,
    const FableTitleFramePacketShaderFacts& facts) {
  if (!can_produce_present_color || !writes_visible_target) {
    return false;
  }

  if (full_frame_composite || full_frame_private_copy) {
    return true;
  }
  if (repopulate_tail || logo_billboard_overlay) {
    return false;
  }

  return facts.title_gui_quad_declaration ||
         facts.vertex_hash == kFableTitleGuiText3DVertexHash ||
         facts.pixel_hash == kFableTitleGuiText3DPixelShaderHash ||
         facts.title_gui_text_3d_pixel_shader_address ||
         packet.pixel_shader == kFableTitleSceneTextPixelShader;
}

bool FableTitleFramePacketIsVisibleProducer(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    const FableTitleFramePacketShaderFacts& facts) {
  return BuildFableTitleFramePacketDerivedFacts(packet, targets, facts)
      .visible_producer;
}


uint64_t FableTitleFramePacketCurrentFramePresentScore(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    const FableTitleFramePacketShaderFacts& shader_facts,
    const FableTitleFramePacketPresentCandidateFacts& candidate) {
  if (static_cast<RendererDrawEventSource>(packet.source) ==
      RendererDrawEventSource::kPixVideo) {
    return 0;
  }

  bool writes_color = false;
  for (uint32_t mask : packet.color_write_masks) {
    writes_color = writes_color || mask != 0;
  }
  if (!writes_color) {
    return 0;
  }

  const FableTitleFramePacketDerivedFacts derived =
      BuildFableTitleFramePacketDerivedFacts(packet, targets, shader_facts);
  const bool title_visible_packet =
      derived.full_frame_composite || derived.full_frame_private_copy ||
      (static_cast<RendererDrawEventSource>(packet.source) ==
           RendererDrawEventSource::kSceneObject &&
       !derived.overlay_only);
  if (!title_visible_packet || !candidate.source_ready ||
      candidate.write_serial == 0 || !candidate.source_scene_target_candidate) {
    return 0;
  }

  uint64_t score = 1;
  score += derived.full_frame_private_copy ? 1ull << 62 : 0;
  score += derived.visible_producer ? 1ull << 61 : 0;
  score += candidate.title_scene_target ? 1ull << 60 : 0;
  score += candidate.source_scene_target_candidate ? 1ull << 59 : 0;
  score += std::min<uint64_t>(packet.command_serial, 0xFFFFFFFFull) << 16;
  score += std::min<uint64_t>(uint64_t(candidate.width) *
                                  uint64_t(candidate.height),
                              0xFFFFull);
  return score;
}

uint64_t FableTitleSurfaceDestinationPresentScore(
    const FableTitleSurfaceDestinationPresentCandidateFacts& candidate) {
  if (!candidate.source_ready || !candidate.present_target_candidate ||
      candidate.write_serial == 0) {
    return 0;
  }
  // Copies below half the display extent are the title blur/downsample
  // chain (480x270/560x360 thumbnails); presenting one flips the output to
  // a black thumbnail. The 960x540 surface is the real title composite at
  // 1x (all 1080p targets stay black while it carries the menu) and must
  // remain eligible; full-resolution candidates still outrank it through
  // the high score bits.
  if (!candidate.full_resolution_candidate &&
      (candidate.width < 960u || candidate.height < 540u)) {
    return 0;
  }

  uint64_t score = 1;
  score += !candidate.source_surface && candidate.title_wrapper &&
                   candidate.wrapper_source_exact
               ? 1ull << 62
               : 0;
  score += candidate.full_resolution_candidate ? 1ull << 61 : 0;
  score += candidate.source_surface && candidate.full_resolution_candidate
               ? 1ull << 60
               : 0;
  score += candidate.title_wrapper ? 1ull << 59 : 0;
  score += candidate.wrapper_source_exact ? 1ull << 58 : 0;
  score += candidate.color_write_layout ? 1ull << 57 : 0;
  score += std::min<uint64_t>(candidate.write_serial, 0xFFFFFFFFull) << 16;
  score += std::min<uint64_t>(uint64_t(candidate.width) *
                                  uint64_t(candidate.height),
                              0xFFFFull);
  return score;
}

}  // namespace refii::renderer
