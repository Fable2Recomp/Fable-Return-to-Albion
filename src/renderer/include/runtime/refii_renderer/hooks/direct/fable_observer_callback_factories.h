#pragma once

#include "runtime/refii_renderer/hooks/general/fable_hook_stack.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_private_rect_geometry.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_root_texture_header.h"

#include <array>
#include <cstdint>

namespace refii::renderer {

using FableLikelyGuestDwordAddressFn = bool (*)(uint32_t guest_address);
using FableExactStackRectReturnAddressFn = bool (*)(uint32_t lr);
using FableRenderStateReadFn = uint32_t (*)();
using FableTextureFetchLoadFn = std::array<uint32_t, 6> (*)(
    uint8_t* base,
    uint32_t source_address);

FableHookGuestAddressCallbacks MakeFableHookGuestAddressCallbacks(
    FableLikelyGuestDwordAddressFn is_likely_guest_dword_address);

FablePrivateTextureRectangleObserverGeometryCallbacks
MakeFablePrivateTextureRectangleObserverGeometryCallbacks(
    FableLikelyGuestDwordAddressFn is_likely_guest_dword_address,
    FableExactStackRectReturnAddressFn is_exact_stack_rect_return_address,
    FableRenderStateReadFn current_pixel_center_offset_render_state,
    FableTextureFetchLoadFn load_texture_fetch_dwords);

FableTitleRootTextureHeaderAddressCallbacks
MakeFableTitleRootTextureHeaderAddressCallbacks(
    FableLikelyGuestDwordAddressFn is_likely_guest_dword_address);

}  // namespace refii::renderer
