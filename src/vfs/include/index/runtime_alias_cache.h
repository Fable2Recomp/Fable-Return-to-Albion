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
bool ResolveTypedLuaVfsAsset(std::string_view asset_path,
                             bool carrier,
                             LooseAssetResolution* out_resolution,
                             std::string* out_candidate);
bool PrimeRefiiVfsRuntimeAliasCandidates(
    const std::filesystem::path& loose_root,
    const std::vector<std::string>& aliases);
bool PrimeRefiiVfsRuntimeExactAliasCandidate(
    const std::filesystem::path& loose_root,
    std::string_view alias);
void PrimeRefiiVfsDatabaseAliasCandidates(
    const std::filesystem::path& loose_root,
    const std::vector<std::string>& aliases);
std::vector<std::string> LookupRefiiVfsDatabaseHashCandidates(
    uint32_t resource_hash,
    const std::filesystem::path& loose_root);
bool LoadRefiiVfsRuntimeHashOwnershipTable(
    const std::filesystem::path& loose_root,
    std::unordered_set<uint32_t>* out_owned_hashes);
const std::unordered_set<uint32_t>* GetRefiiVfsRuntimeOwnedHashes(
    const std::filesystem::path& loose_root);

}  // namespace detail
}  // namespace refii::vfs
