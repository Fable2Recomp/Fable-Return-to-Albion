#pragma once

#include "vfs_hook_types.h"

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

struct PPCContext;

namespace refii::vfs {
inline namespace detail {
bool StartsWith(std::string_view value, std::string_view prefix);
bool EndsWith(std::string_view value, std::string_view suffix);
bool IsLuaScriptAssetKey(std::string_view asset_path);
uint64_t MakeBankManifestLookupKey(uint32_t offset, uint32_t size);
std::string StripTrailingSlashes(std::string value);
std::vector<std::string> BuildGameFaceRedirectCandidates(
    std::string_view asset_path);
}  // namespace detail
}  // namespace refii::vfs
