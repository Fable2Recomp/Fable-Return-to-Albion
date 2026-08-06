#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_synthetic_private_rect.h"

namespace refii::renderer {

bool IsFableSyntheticPrivateRectCopyShader(uint32_t shader) {
  return shader == kFableSyntheticPrivateRectDepthPixelShader ||
         shader == kFableSyntheticPrivateRectCopyPixelShader;
}

bool DirectSyntheticPrivateRectCommandUsesDepthTarget(
    const FableDirectRenderCommand& command) {
  return command.draw.source ==
             RendererDrawEventSource::kPrivateTextureRectangle &&
         command.draw.private_texture_rectangle &&
         command.resources.pixel_shader ==
             kFableSyntheticPrivateRectDepthPixelShader &&
         command.resources.depth_stencil != 0;
}

FablePrivateTextureCopyKind DirectSyntheticPrivateRectCopyKindForTarget(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& target) {
  const bool target_is_depth =
      target.texture_depth_target ||
      (command.resources.depth_stencil != 0 &&
       (target.guest_address == command.resources.depth_stencil ||
        target.parent_address == command.resources.depth_stencil ||
        target.guest_texture.object_address == command.resources.depth_stencil ||
        target.guest_texture.parent_address == command.resources.depth_stencil));
  if (target_is_depth) {
    return FablePrivateTextureCopyKind::kDepth;
  }

  for (uint32_t render_target : command.resources.render_targets) {
    if (render_target != 0 &&
        (target.guest_address == render_target ||
         target.parent_address == render_target ||
         target.guest_texture.object_address == render_target ||
         target.guest_texture.parent_address == render_target)) {
      return FablePrivateTextureCopyKind::kColor;
    }
  }

  if (command.draw.source ==
          RendererDrawEventSource::kPrivateTextureRectangle &&
      command.draw.private_texture_rectangle &&
      command.resources.pixel_shader ==
          kFableSyntheticPrivateRectCopyPixelShader) {
    return FablePrivateTextureCopyKind::kColor;
  }
  return target.texture_depth_target ? FablePrivateTextureCopyKind::kDepth
                                     : FablePrivateTextureCopyKind::kColor;
}

bool DirectPrivateTextureCopyKindUsesColorBacking(
    FablePrivateTextureCopyKind kind) {
  return kind == FablePrivateTextureCopyKind::kColor ||
         kind == FablePrivateTextureCopyKind::kColorSibling;
}

bool DirectPrivateTextureCopyKindCanBlitToColorTarget(
    FablePrivateTextureCopyKind kind) {
  // kDepth is deliberately excluded: Fable's native-depth private-rect copy
  // shares its guest destination (e.g. 0x40137C90) with the scene COLOR copy
  // within one guest frame, and pump-frame splitting can replay the depth
  // copy in the tick whose consumer expects the color generation — the
  // composite quad then samples depth-as-color (black title scene). Our
  // color blit of a depth source writes normalized depth into RGB, never the
  // raw reinterpreted bits a genuine guest depth-consumer would read, so
  // replaying it into the shared color destination loses information in
  // every case. If a real depth-copy consumer surfaces, give kDepth its own
  // per-destination side texture instead of re-adding it here.
  return DirectPrivateTextureCopyKindUsesColorBacking(kind);
}

uint32_t DirectPrivateRectExactPixelShaderKey(
    uint32_t pixel_shader, FablePrivateTextureCopyKind kind) {
  if (DirectPrivateTextureCopyKindUsesColorBacking(kind) &&
      IsFableSyntheticPrivateRectCopyShader(pixel_shader)) {
    return kFableSyntheticPrivateRectCopyPixelShader;
  }
  return pixel_shader;
}

bool DirectPrivateRectSyntheticPixelShadersCompatible(
    uint32_t command_pixel_shader, uint32_t action_pixel_shader,
    FablePrivateTextureCopyKind action_kind) {
  return DirectPrivateTextureCopyKindUsesColorBacking(action_kind) &&
         IsFableSyntheticPrivateRectCopyShader(command_pixel_shader) &&
         IsFableSyntheticPrivateRectCopyShader(action_pixel_shader);
}

}  // namespace refii::renderer
