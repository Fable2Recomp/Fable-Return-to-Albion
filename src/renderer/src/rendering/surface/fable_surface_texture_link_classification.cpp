#include "runtime/refii_renderer/plume/fable/private_rect/fable_surface_texture_link_classification.h"

#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_private_rect_symbols.h"

namespace refii::renderer {

bool DirectSurfaceTextureLinkShouldPublishObjectMirror(
    const FableSurfaceTextureLink& link) {
  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  if (link.source_slot == kUnknownSlot) {
    return false;
  }
  switch (link.copy_symbol_address) {
    case kFableBeginVerticesTitleWrapperSymbol:
    case kFablePrivateRectSetupWrapperSymbol:
    case kFablePrivateRectSetupFlushWrapperSymbol:
    case kFablePrivateRectCompactWrapperSymbol:
    case kFablePrivateRectSiblingWrapperSymbol:
    case kFablePrivateRectSiblingWrapperCallsite:
    case kFablePrivateRectTitleGlobalWrapperSymbol:
    case kFablePrivateRectTitleGlobalWrapperCallsite:
      return false;
    default:
      return true;
  }
}

bool DirectSurfaceTextureLinkIsPrivateTitleWrapperRoot(
    const FableSurfaceTextureLink& link) {
  switch (link.copy_symbol_address) {
    case kFableBeginVerticesTitleWrapperSymbol:
    case kFablePrivateRectSetupWrapperSymbol:
    case kFablePrivateRectSetupFlushWrapperSymbol:
    case kFablePrivateRectSiblingWrapperSymbol:
    case kFablePrivateRectSiblingWrapperCallsite:
    case kFablePrivateRectTitleGlobalWrapperSymbol:
    case kFablePrivateRectTitleGlobalWrapperCallsite:
      return true;
    default:
      return false;
  }
}

bool DirectSurfaceTextureLinkIsBeginVerticesTitleStripUpload(
    const FableSurfaceTextureLink& link) {
  return link.copy_symbol_address == kFableBeginVerticesTitleWrapperSymbol &&
         link.texture_base_address == 0x1FAA0000u &&
         link.texture_width == 512u && link.texture_height == 1u &&
         link.texture_format == 36u;
}

bool DirectSurfaceTextureLinkIsUnresolvedSurfaceViewIdentity(
    const FableSurfaceTextureLink& link) {
  return link.copy_symbol_address == kFableD3DTextureGetSurfaceLevelSymbol &&
         link.target_content_serial == 0 && link.backing_content_serial == 0 &&
         link.sampled_source.content_serial == 0;
}

bool DirectSurfaceTextureLinkRequiresExactWrapperSourceForMutableHeaderResolve(
    const FableSurfaceTextureLink& link) {
  if (DirectSurfaceTextureLinkIsPrivateTitleWrapperRoot(link)) {
    return true;
  }
  switch (link.copy_symbol_address) {
    case kFablePrivateRectCompactWrapperSymbol:
    case kFablePrivateRectTitleLocalWrapperCallsite:
    case kFablePrivateRectTitleGlobalWrapperSymbol:
    case kFablePrivateRectTitleGlobalWrapperCallsite:
      return true;
    default:
      return false;
  }
}

bool DirectSurfaceTextureLinkExactWrapperOwnedByCommand(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link) {
  const bool command_has_call =
      command.draw.private_texture_wrapper_call_serial != 0;
  const bool command_has_context =
      command.draw.private_texture_wrapper_context_key != 0;
  const bool command_has_symbol =
      command.draw.private_texture_wrapper_symbol_address != 0;
  const bool command_has_source =
      command.draw.private_texture_wrapper_source_texture != 0;
  if (!command_has_call && !command_has_context && !command_has_symbol &&
      !command_has_source) {
    return false;
  }

  bool overlapped_identity = false;
  if (link.copy_call_serial != 0 && command_has_call) {
    overlapped_identity = true;
    if (link.copy_call_serial !=
        command.draw.private_texture_wrapper_call_serial) {
      return false;
    }
  }
  if (link.copy_context_key != 0 && command_has_context) {
    overlapped_identity = true;
    if (link.copy_context_key !=
        command.draw.private_texture_wrapper_context_key) {
      return false;
    }
  }
  if (link.copy_symbol_address != 0 && command_has_symbol) {
    overlapped_identity = true;
    if (!FablePrivateRectWrapperSymbolsCompatible(
            link.copy_symbol_address,
            command.draw.private_texture_wrapper_symbol_address)) {
      return false;
    }
  }
  if (link.wrapper_source_texture != 0 && command_has_source) {
    overlapped_identity = true;
    if (link.wrapper_source_texture !=
        command.draw.private_texture_wrapper_source_texture) {
      return false;
    }
  }
  return overlapped_identity;
}

bool DirectSurfaceTextureLinkWrapperOwnedByCommand(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link) {
  if (!DirectSurfaceTextureLinkIsPrivateTitleWrapperRoot(link)) {
    return true;
  }
  return DirectSurfaceTextureLinkExactWrapperOwnedByCommand(command, link);
}

}  // namespace refii::renderer
