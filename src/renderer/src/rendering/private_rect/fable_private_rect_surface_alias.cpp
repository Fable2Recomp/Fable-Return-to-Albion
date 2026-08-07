#include "runtime/refii_renderer/plume/fable/private_rect/fable_private_rect_surface_alias.h"

#include "runtime/refii_renderer/plume/fable/title/fable_title_classification.h"

#include <rex/logging.h>

namespace refii::renderer {
namespace {

bool FablePrivateRectSurfaceAliasCallbacksReady(
    const FablePrivateRectSurfaceAliasCallbacks& callbacks) {
  return callbacks.is_likely_fable_object_address && callbacks.next_serial &&
         callbacks.store_surface_copy_alias &&
         callbacks.track_surface_copy_alias && callbacks.claim_blank_probe &&
         callbacks.reserve_log_index;
}

bool FablePrivateRectAddressIsTitleProxy(uint32_t address) {
  return IsFableTitlePrivateTextureProxyAddress(address) ||
         IsFablePrivateTextureCopyProxyAddress(address) ||
         IsFableHeaderOnlyPrivateTextureProxyAddress(address) ||
         IsFableMutablePrivateTextureHeaderAddress(address);
}

uint32_t FablePrivateRectAliasDestinationTexture(
    const FablePrivateTextureCopyAction& action,
    const FablePrivateRectSurfaceAliasCallbacks& callbacks) {
  if (callbacks.is_likely_fable_object_address(action.source_texture) ||
      FablePrivateRectAddressIsTitleProxy(action.source_texture)) {
    return action.source_texture;
  }
  if (FablePrivateRectAddressIsTitleProxy(action.source_base_address)) {
    return action.source_base_address;
  }
  return 0;
}

}  // namespace

void FablePrivateTextureRectanglePublishExactSurfaceAlias(
    const FablePrivateTextureCopyAction& action,
    const char* reason,
    const FablePrivateRectSurfaceAliasCallbacks& callbacks) {
  if (!FablePrivateRectSurfaceAliasCallbacksReady(callbacks)) {
    return;
  }
  if (action.wrapper_call_serial == 0 && action.wrapper_context_key == 0 &&
      action.wrapper_symbol_address == 0) {
    return;
  }
  const uint32_t destination_texture =
      FablePrivateRectAliasDestinationTexture(action, callbacks);
  if (destination_texture == 0 ||
      !callbacks.is_likely_fable_object_address(action.target_surface) ||
      destination_texture == action.target_surface) {
    return;
  }

  RendererSurfaceCopyAliasSnapshot alias;
  alias.source_surface = action.target_surface;
  alias.destination_surface = action.source_texture;
  if (destination_texture != action.source_texture) {
    alias.destination_surface = destination_texture;
  }
  alias.hook_address = action.wrapper_symbol_address != 0
                           ? action.wrapper_symbol_address
                           : action.lr;
  alias.serial = callbacks.next_serial();
  alias.copy_call_serial = action.wrapper_call_serial;
  alias.copy_context_key = action.wrapper_context_key;
  alias.copy_symbol_address = action.wrapper_symbol_address;
  callbacks.store_surface_copy_alias(alias);
  callbacks.track_surface_copy_alias(alias);
  callbacks.claim_blank_probe(
      action, reason ? reason : "private-rect-surface-alias");

  const uint32_t log_index = callbacks.reserve_log_index();
  if (log_index != 0) {
    REXLOG_INFO(
        "Refii renderer exact private rect surface alias #{} reason={} "
        "source=0x{:08X} destination=0x{:08X} action_source=0x{:08X} "
        "source_base=0x{:08X} call={} context=0x{:016X} symbol=0x{:08X} "
        "kind={}",
        log_index,
        reason ? reason : "unknown",
        alias.source_surface,
        alias.destination_surface,
        action.source_texture,
        action.source_base_address,
        static_cast<unsigned long long>(alias.copy_call_serial),
        static_cast<unsigned long long>(alias.copy_context_key),
        alias.copy_symbol_address,
        FablePrivateTextureCopyKindName(action.kind));
  }
}

}  // namespace refii::renderer
