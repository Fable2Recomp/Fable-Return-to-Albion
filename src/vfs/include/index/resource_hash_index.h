#pragma once

#include "vfs_hook_types.h"

#include <cstdint>
#include <filesystem>
#include <memory>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace refii::vfs {
inline namespace detail {
std::vector<std::string> GetRuntimeResourceHashNames(uint32_t resource_hash);
}  // namespace detail
}  // namespace refii::vfs
