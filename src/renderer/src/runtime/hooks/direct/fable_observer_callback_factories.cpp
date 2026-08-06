#include "runtime/refii_renderer/hooks/direct/fable_observer_callback_factories.h"

namespace refii::renderer {

FableHookGuestAddressCallbacks MakeFableHookGuestAddressCallbacks(
    FableLikelyGuestDwordAddressFn is_likely_guest_dword_address) {
  return {
      .is_likely_guest_dword_address = is_likely_guest_dword_address,
  };
}

FablePrivateTextureRectangleObserverGeometryCallbacks
MakeFablePrivateTextureRectangleObserverGeometryCallbacks(
    FableLikelyGuestDwordAddressFn is_likely_guest_dword_address,
    FableExactStackRectReturnAddressFn is_exact_stack_rect_return_address,
    FableRenderStateReadFn current_pixel_center_offset_render_state,
    FableTextureFetchLoadFn load_texture_fetch_dwords) {
  return {
      .is_likely_guest_dword_address = is_likely_guest_dword_address,
      .is_exact_stack_rect_return_address =
          is_exact_stack_rect_return_address,
      .current_pixel_center_offset_render_state =
          current_pixel_center_offset_render_state,
      .load_texture_fetch_dwords = load_texture_fetch_dwords,
  };
}

FableTitleRootTextureHeaderAddressCallbacks
MakeFableTitleRootTextureHeaderAddressCallbacks(
    FableLikelyGuestDwordAddressFn is_likely_guest_dword_address) {
  return {
      .is_likely_guest_dword_address = is_likely_guest_dword_address,
  };
}

}  // namespace refii::renderer
