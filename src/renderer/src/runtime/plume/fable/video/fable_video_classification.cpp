#include "runtime/refii_renderer/plume/fable/video/fable_video_classification.h"

#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_private_rect_symbols.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_constants.h"
#include "runtime/refii_renderer/plume/fable/video/fable_video_constants.h"

#include <rex/logging.h>

#include <algorithm>
#include <cstddef>

namespace refii::renderer {
namespace {

constexpr uint32_t kFableVideoClearDepth = 0x10u;
constexpr uint32_t kFableVideoClearStencil = 0x20u;

}  // namespace

bool FableVideoCommandIsDraw(const FableDirectRenderCommand& command) {
  return command.type == FableDirectRenderCommandType::kDrawPrimitive ||
         command.type == FableDirectRenderCommandType::kDrawIndexedPrimitive ||
         command.type == FableDirectRenderCommandType::kDrawPrimitiveUP;
}

bool FableVideoCommandIsPixVideoDraw(
    const FableDirectRenderCommand& command) {
  return FableVideoCommandIsDraw(command) &&
         command.draw.source == RendererDrawEventSource::kPixVideo;
}

bool FableVideoCommandIsDirectVideoDraw(
    const FableDirectRenderCommand& command,
    FableVideoExactBinkDrawPredicate is_exact_bink_draw) {
  return FableVideoCommandIsPixVideoDraw(command) ||
         (is_exact_bink_draw != nullptr && is_exact_bink_draw(command));
}

bool FableVideoCommandHasBinkIndexedWrapperSymbol(
    const FableDirectRenderCommand& command) {
  return command.draw.private_texture_wrapper_symbol_address ==
             kFableD3DBeginIndexedVerticesWrapperSymbol ||
         command.draw.private_texture_wrapper_symbol_address ==
             kFableBinkIndexedVerticesWrapperBuilderSymbol;
}

bool FableVideoCommandHasBinkWrapperSource(
    const FableDirectRenderCommand& command) {
  return command.draw.source == RendererDrawEventSource::kScratchWrapper ||
         command.draw.source == RendererDrawEventSource::kD3DWrapper ||
         command.draw.source == RendererDrawEventSource::kSceneObject;
}

bool FableVideoCommandHasBinkIndexedWrapperDrawShape(
    const FableDirectRenderCommand& command) {
  return FableVideoCommandHasBinkWrapperSource(command) &&
         command.draw.draw.indexed && command.draw.draw.primitive_type == 4u &&
         command.draw.draw.base_vertex == 0 &&
         command.resources.render_targets[0] != 0 &&
         command.resources.vertex_shader != 0 &&
         command.resources.pixel_shader != 0;
}

bool FableVideoBinkWrapperPixelShaderHashAllowed(uint64_t pixel_hash) {
  return pixel_hash == 0 ||
         pixel_hash == kFableBinkIndexedWrapperPixelShaderHash ||
         pixel_hash == kFablePixRenderVideoFramePixelShaderHash;
}

bool FableVideoCommandUsesKnownVideoPixelShader(uint64_t pixel_hash) {
  // SCOPE NOTE (2026-07-18): this deliberately does NOT include
  // kFableStartupVideoCopyPixelShaderHash, and that is not drift from
  // IsFableVideoPixelShaderHash (hook_observer.cpp). The two predicates ask
  // different questions: the observer one is "video FAMILY shader" (publish
  // routing, includes the RGB startup-copy), this one is "YCrCb wrapper
  // COMPOSE shader" and feeds DirectCommandIsExactBinkIndexedWrapperDrawLocked
  // — which forces Bink plane bindings on qualifying draws. Adding the
  // startup-copy hash here made copy draws (which sample the already-converted
  // RGB texture, not Y/Cr/Cb planes) classify as plane composes and broke
  // video display. Keep the scopes separate.
  return pixel_hash == kFableBinkIndexedWrapperPixelShaderHash ||
         pixel_hash == kFablePixRenderVideoFramePixelShaderHash;
}

bool FableVideoCommandIsEarlyBinkWrapperDrawTarget(
    const FableDirectRenderCommand& command, uint64_t pixel_hash) {
  if (!FableVideoCommandIsDraw(command) ||
      command.draw.source != RendererDrawEventSource::kScratchWrapper ||
      command.draw.private_texture_wrapper_symbol_address !=
          kFableD3DBeginIndexedVerticesWrapperSymbol ||
      !command.draw.draw.indexed || command.draw.draw.primitive_type != 4u ||
      command.draw.draw.base_vertex != 0 ||
      command.draw.draw.primitive_count != 6u ||
      command.resources.render_targets[0] != kFableTitleVisibleRenderTarget ||
      command.resources.vertex_shader == 0 ||
      command.resources.pixel_shader == 0 ||
      command.resources.textures[0] == 0 ||
      command.resources.textures[1] == 0 ||
      command.resources.textures[2] == 0) {
    return false;
  }

  return pixel_hash == 0 ||
         pixel_hash == kFableBinkIndexedWrapperPixelShaderHash;
}

bool FableVideoCommandIsStartupVideoCopyDrawTarget(
    const FableDirectRenderCommand& command, uint64_t pixel_hash,
    const FableTextureFetchInfo& slot0_fetch) {
  if (!FableVideoCommandIsDraw(command) ||
      command.resources.render_targets[0] != kFableTitleVisibleRenderTarget ||
      pixel_hash != kFableStartupVideoCopyPixelShaderHash) {
    return false;
  }

  return command.resources.textures[0] == kFableTitleSceneScalerTexture ||
         (slot0_fetch.valid &&
          slot0_fetch.base_address == kFableTitleSceneScalerUploadBase &&
          slot0_fetch.width == 1280u && slot0_fetch.height == 720u);
}

bool FableVideoBinkFetchInfoIsPlaneSlot(
    uint32_t slot,
    const FableTextureFetchInfo& y,
    const FableTextureFetchInfo& cr,
    const FableTextureFetchInfo& cb,
    const FableTextureFetchInfo& slot_info) {
  if (slot >= 3u || !y.valid || !cr.valid || !cb.valid || !slot_info.valid) {
    return false;
  }
  if (y.format != 2u || cr.format != 2u || cb.format != 2u) {
    return false;
  }
  if (y.width < 64u || y.height < 64u || (y.width & 1u) != 0 ||
      (y.height & 1u) != 0) {
    return false;
  }
  if (cr.width != cb.width || cr.height != cb.height ||
      cr.width * 2u != y.width || cr.height * 2u != y.height) {
    return false;
  }
  switch (slot) {
    case 0:
      return slot_info.base_address == y.base_address &&
             slot_info.width == y.width && slot_info.height == y.height;
    case 1:
      return slot_info.base_address == cr.base_address &&
             slot_info.width == cr.width && slot_info.height == cr.height;
    case 2:
      return slot_info.base_address == cb.base_address &&
             slot_info.width == cb.width && slot_info.height == cb.height;
    default:
      return false;
  }
}

bool FableVideoCommandFetchesMatchBinkWrapperPlaneSet(
    const FableDirectRenderCommand& command,
    FableTextureFetchInfo* y_out,
    FableTextureFetchInfo* cr_out,
    FableTextureFetchInfo* cb_out) {
  const FableTextureFetchInfo y = FableTextureFetchInfoFromDwords(
      FableCommandTextureFetchDwords(command, 0));
  const FableTextureFetchInfo cr = FableTextureFetchInfoFromDwords(
      FableCommandTextureFetchDwords(command, 1));
  const FableTextureFetchInfo cb = FableTextureFetchInfoFromDwords(
      FableCommandTextureFetchDwords(command, 2));
  if (y_out) {
    *y_out = y;
  }
  if (cr_out) {
    *cr_out = cr;
  }
  if (cb_out) {
    *cb_out = cb;
  }
  return FableVideoBinkFetchInfoIsPlaneSlot(0, y, cr, cb, y) &&
         FableVideoBinkFetchInfoIsPlaneSlot(1, y, cr, cb, cr) &&
         FableVideoBinkFetchInfoIsPlaneSlot(2, y, cr, cb, cb);
}

bool FableVideoCommandSlotUsesFetchDecodedBinkWrapperPlane(
    const FableDirectRenderCommand& command,
    uint32_t slot) {
  if (slot >= 3u) {
    return false;
  }
  const FableTextureFetchInfo y = FableTextureFetchInfoFromDwords(
      FableCommandTextureFetchDwords(command, 0));
  const FableTextureFetchInfo cr = FableTextureFetchInfoFromDwords(
      FableCommandTextureFetchDwords(command, 1));
  const FableTextureFetchInfo cb = FableTextureFetchInfoFromDwords(
      FableCommandTextureFetchDwords(command, 2));
  const FableTextureFetchInfo slot_info = FableTextureFetchInfoFromDwords(
      FableCommandTextureFetchDwords(command, slot));
  return FableVideoBinkFetchInfoIsPlaneSlot(slot, y, cr, cb, slot_info);
}

bool FableVideoCommandLooksLikeBinkIndexedWrapperCandidate(
    const FableVideoBinkIndexedWrapperCandidateFacts& facts) {
  if (!facts.has_draw_shape) {
    return false;
  }

  if (!facts.has_wrapper_symbol && !facts.uses_known_video_pixel_shader) {
    return false;
  }

  return facts.pixel_shader_allowed &&
         (facts.fetches_match_plane_set || facts.has_ready_exact_planes);
}

bool FableVideoCurrentFramePresentCandidateReady(
    const FableVideoPresentCandidateFacts& facts) {
  return facts.current_frame_has_video_draw && facts.source_ready &&
         facts.frame_matches && facts.present_target_candidate &&
         facts.video_target != 0;
}

bool FableVideoClearMatchesExactBinkDrawTarget(
    const FableDirectRenderCommand& draw_command,
    const FableDirectRenderCommand& clear_command,
    bool draw_command_is_exact_bink) {
  if (!draw_command_is_exact_bink ||
      clear_command.type != FableDirectRenderCommandType::kClear ||
      clear_command.clear.flags == 0) {
    return false;
  }
  if (draw_command.resources.render_targets[0] == 0 ||
      clear_command.resources.render_targets[0] !=
          draw_command.resources.render_targets[0]) {
    return false;
  }
  if ((clear_command.clear.flags &
       (kFableVideoClearDepth | kFableVideoClearStencil)) != 0 &&
      clear_command.resources.depth_stencil != 0 &&
      draw_command.resources.depth_stencil != 0 &&
      clear_command.resources.depth_stencil !=
          draw_command.resources.depth_stencil) {
    return false;
  }
  return true;
}

uint32_t FableVideoNormalizeExactBinkClearOrder(
    std::vector<FableDirectRenderCommand>& submitted_commands,
    FableVideoExactBinkDrawPredicate is_exact_bink_draw) {
  if (!is_exact_bink_draw) {
    return 0;
  }

  uint32_t normalized = 0;
  for (size_t index = 0; index + 1u < submitted_commands.size(); ++index) {
    FableDirectRenderCommand& draw_command = submitted_commands[index];
    FableDirectRenderCommand& clear_command = submitted_commands[index + 1u];
    if (!FableVideoClearMatchesExactBinkDrawTarget(
            draw_command, clear_command, is_exact_bink_draw(draw_command))) {
      continue;
    }
    std::swap(draw_command, clear_command);
    ++normalized;
    ++index;
  }
  return normalized;
}

void FableVideoLogBinkCommandOrder(
    const std::vector<FableDirectRenderCommand>& submitted_commands,
    bool verbose_tracing_enabled,
    uint32_t frame_index,
    uint32_t& logged_command_order,
    FableVideoExactBinkDrawPredicate is_exact_bink_draw) {
  if (!verbose_tracing_enabled || !is_exact_bink_draw ||
      logged_command_order >= 96u) {
    return;
  }
  const bool has_bink_draw =
      std::any_of(submitted_commands.begin(), submitted_commands.end(),
                  is_exact_bink_draw);
  if (!has_bink_draw) {
    return;
  }

  for (uint32_t index = 0;
       index < submitted_commands.size() && logged_command_order < 96u;
       ++index) {
    const FableDirectRenderCommand& command = submitted_commands[index];
    switch (command.type) {
      case FableDirectRenderCommandType::kClear:
        REXLOG_INFO(
            "Refii plume Bink command order #{} frame={} index={} "
            "kind=clear serial={} flags=0x{:08X} rt0=0x{:08X} "
            "rt1=0x{:08X} rt2=0x{:08X} rt3=0x{:08X} ds=0x{:08X} "
            "color={:.4f},{:.4f},{:.4f},{:.4f} depth={:.4f} stencil={} "
            "rect={} {} {} {} {}",
            ++logged_command_order, frame_index, index,
            static_cast<unsigned long long>(command.serial),
            command.clear.flags, command.resources.render_targets[0],
            command.resources.render_targets[1],
            command.resources.render_targets[2],
            command.resources.render_targets[3],
            command.resources.depth_stencil, command.clear.color[0],
            command.clear.color[1], command.clear.color[2],
            command.clear.color[3], command.clear.depth,
            command.clear.stencil, command.clear.rect.valid ? 1 : 0,
            command.clear.rect.left, command.clear.rect.top,
            command.clear.rect.right, command.clear.rect.bottom);
        break;
      case FableDirectRenderCommandType::kDrawPrimitive:
      case FableDirectRenderCommandType::kDrawIndexedPrimitive:
      case FableDirectRenderCommandType::kDrawPrimitiveUP:
        REXLOG_INFO(
            "Refii plume Bink command order #{} frame={} index={} "
            "kind=draw bink={} serial={} draw={} source={} indexed={} "
            "count={} rt0=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
            "tex0=0x{:08X} tex1=0x{:08X} tex2=0x{:08X} tex13=0x{:08X}",
            ++logged_command_order, frame_index, index,
            is_exact_bink_draw(command) ? 1 : 0,
            static_cast<unsigned long long>(command.serial),
            static_cast<unsigned long long>(command.draw_serial),
            static_cast<uint32_t>(command.draw.source),
            command.draw.draw.indexed ? 1 : 0,
            command.draw.draw.primitive_count,
            command.resources.render_targets[0],
            command.resources.depth_stencil, command.resources.vertex_shader,
            command.resources.pixel_shader, command.resources.textures[0],
            command.resources.textures[1], command.resources.textures[2],
            command.resources.textures[13]);
        break;
      case FableDirectRenderCommandType::kResolveCopy:
        REXLOG_INFO(
            "Refii plume Bink command order #{} frame={} index={} "
            "kind=resolve serial={} rb_dest=0x{:08X} rb_info=0x{:08X}",
            ++logged_command_order, frame_index, index,
            static_cast<unsigned long long>(command.serial),
            command.resolve.rb_copy_dest_base,
            command.resolve.rb_copy_dest_info);
        break;
      default:
        break;
    }
  }
}

}  // namespace refii::renderer
