#pragma once

#include "loose_asset_hooks.h"

#include <cstdint>
#include <filesystem>
#include <string>
#include <string_view>
#include <unordered_set>
#include <vector>

namespace refii::vfs {
inline namespace detail {

std::vector<std::string> LookupRefiiVfsDatabaseAliasCandidates(
    std::string_view asset_path,
    const std::filesystem::path& loose_root);
bool IsCachedOptionalSyntheticVfsMiss(std::string_view asset_path);
bool PrimeRefiiVfsRuntimeAliasCandidates(
    const std::filesystem::path& loose_root,
    const std::vector<std::string>& aliases);
void PrimeRefiiVfsDatabaseAliasCandidates(
    const std::filesystem::path& loose_root,
    const std::vector<std::string>& aliases);
}  // namespace detail
}  // namespace refii::vfs
