#pragma once

#include "core/guest_state.h"

#include <cstdint>
#include <string>
#include <string_view>

namespace refii::vfs {
inline namespace detail {
std::string FormatPostVideoFrameTeardownState(uint8_t* base,
                                              uint32_t object_guest);
bool IsParserBoundaryTextureWrapperCaller(uint32_t lr);
}  // namespace detail
}  // namespace refii::vfs
