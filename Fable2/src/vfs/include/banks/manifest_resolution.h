#pragma once

#include "loose_asset_hooks.h"
#include "vfs_hook_types.h"

#include <cstdint>
#include <filesystem>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

namespace refii::vfs {
inline namespace detail {

void AddUnique(std::vector<std::string>& values, std::string value);
std::string BankNameToLooseRoot(std::string_view bank_name);
std::filesystem::path ResolveCaseInsensitiveExistingPath(
    const std::filesystem::path& root,
    const std::filesystem::path& relative_path);
std::filesystem::path ResolveBankManifestPath(
    const std::filesystem::path& manifest_root);
std::shared_ptr<const BankManifestIndex> LoadCachedBankManifestIndex(
    const std::filesystem::path& manifest_root,
    std::filesystem::path* out_resolved_root);
bool LookupCachedBankManifestEntryByName(
    const std::filesystem::path& manifest_root,
    std::string_view entry_name,
    BankManifestEntry* out_entry);
bool ParseBankManifestEntryLine(const std::filesystem::path& manifest_root,
                                std::string_view line,
                                BankManifestEntry* out_entry);
std::string CanonicalizeMip0TextureHeaderRedirect(std::string_view value);
std::vector<std::filesystem::path> BuildBankManifestRootCandidates(
    const std::string& bank_root);
bool LookupRefiiVfsTypedBankManifest(std::string_view bank_name,
                                     std::filesystem::path* out_manifest_root,
                                     std::filesystem::path* out_manifest_path);
bool LookupResolvedSyntheticBankOpenAlias(std::string_view bank_alias,
                                          std::string* out_resolved_bank,
                                          std::string* out_reason);
bool FindSyntheticBankManifest(std::string_view bank_name,
                               std::filesystem::path* out_manifest_root,
                               std::filesystem::path* out_manifest_path);
bool ResolveSyntheticBankManifestLooseAsset(std::string_view bank_name,
                                            LooseAssetResolution* out_resolution,
                                            std::string* out_candidate);
}  // namespace detail
}  // namespace refii::vfs
