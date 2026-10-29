#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>

struct PPCContext;

namespace refii::vfs {
inline namespace detail {
bool RefiiMainMenuTraceIsHashOnly();
bool RefiiMainMenuTraceIsTextureOwnedButNotOpened();
}  // namespace detail
}  // namespace refii::vfs
