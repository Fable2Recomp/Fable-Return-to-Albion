#pragma once

#include "renderer_runtime.h"

#include <cstdint>

namespace refii::renderer {

struct FableHookGuestAddressCallbacks {
  bool (*is_likely_guest_dword_address)(uint32_t guest_address) = nullptr;
};

uint32_t FableHookStackU32(
    uint8_t* base,
    const RendererHookArgs& args,
    uint32_t offset,
    const FableHookGuestAddressCallbacks& callbacks);

bool FableHookAddressNearStack(const RendererHookArgs& args,
                               uint32_t address);

}  // namespace refii::renderer
