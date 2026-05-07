#pragma once

#include "loose_asset_hooks.h"
#include "index/runtime_tables.h"
#include "vfs_hook_types.h"

#include <array>
#include <cstdint>
#include <filesystem>
#include <string>
#include <string_view>
#include <unordered_set>
#include <vector>

namespace refii::vfs {
inline namespace detail {

uint32_t LoadBigEndianU32(const uint8_t* bytes);
bool IsRegularHostFile(const std::filesystem::path& path);
uint64_t HostFileSize(const std::filesystem::path& path);
std::array<uint32_t, 4> ReadHostFileFirstWords(
    const std::filesystem::path& path);
std::string FormatHostFileFirstWords(const std::array<uint32_t, 4>& words);
bool IsTextureAssetCandidate(std::string_view candidate);
bool IsTextureHeaderCandidate(std::string_view candidate);
bool IsTexturePayloadBankCandidate(std::string_view candidate);
bool IsKnownFullResourceTextureCandidate(std::string_view candidate);
bool LooksLikeResourceWrapperHeader(const std::array<uint32_t, 4>& words);
bool LooksLikeTextureHeaderDescriptor(const std::array<uint32_t, 4>& words);
bool ResolveBankRootedVfsDatabaseCandidate(
    const std::filesystem::path& loose_root,
    std::string_view candidate,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate);
void AddPairedTextureHeaderCandidateIfPresent(
    const std::filesystem::path& loose_root,
    std::string_view payload_candidate,
    std::unordered_set<std::string>* seen_candidates,
    std::vector<ResolvedVfsHashCandidate>* resolved_candidates);
bool SelectResolvedVfsHashCandidate(
    uint32_t resource_hash,
    std::vector<ResolvedVfsHashCandidate>* resolved_candidates,
    bool prefer_resource_manager_texture_shape,
    uint32_t caller_lr,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate);
bool ResolveConcreteLuaLooseAsset(std::string_view asset_path,
                                  LooseAssetResolution* out_resolution,
                                  std::string* out_candidate,
                                  std::string* out_source = nullptr);
bool ResolveRefiiLuaScriptAsset(std::string_view asset_path,
                                LooseAssetResolution* out_resolution,
                                std::string* out_candidate,
                                std::string* out_source = nullptr);
bool MakeVfsDatabaseCandidateResolution(
    const std::filesystem::path& loose_root,
    std::string_view candidate,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate);
bool ResolvePairedTexturePayloadForHeader(std::string_view header_key,
                                          LooseAssetResolution* out_resolution,
                                          std::string* out_candidate);
bool ResolveVfsDatabaseAliasLooseAsset(std::string_view asset_path,
                                       LooseAssetResolution* out_resolution,
                                       std::string* out_candidate);
bool ResolveVfsDatabaseAliasLooseAssetForResourceManager(
    std::string_view asset_path,
    uint32_t caller_lr,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate);
bool ResolveVfsDatabaseExactAliasLooseAsset(std::string_view asset_path,
                                            LooseAssetResolution* out_resolution,
                                            std::string* out_candidate);

}  // namespace detail
}  // namespace refii::vfs
