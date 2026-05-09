#pragma once

#include "loose_asset_hooks.h"

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace refii::vfs {
inline namespace detail {

std::vector<std::string> BuildLuaVfsCarrierCandidates(
    std::string_view asset_path);
bool ResolveGameFaceLuaCarrierAsset(std::string_view asset_path,
                                    LooseAssetResolution* out_resolution,
                                    std::string* out_candidate);
bool ResolveLuaVfsCarrierAsset(std::string_view asset_path,
                               LooseAssetResolution* out_resolution,
                               std::string* out_candidate);
bool ResolveExtractedLuaSourceAsset(std::string_view asset_path,
                                    LooseAssetResolution* out_resolution,
                                    std::string* out_candidate);
bool ResolveOriginalDataLuaAsset(std::string_view asset_path,
                                 LooseAssetResolution* out_resolution,
                                 std::string* out_candidate);
bool ResolveOriginalGameDataAsset(std::string_view asset_path,
                                  LooseAssetResolution* out_resolution,
                                  std::string* out_candidate);
bool IsGameFaceBinaryAssetKey(std::string_view asset_path);
bool IsOptionalGameFaceBinaryMiss(std::string_view asset_path);
bool ResolveExactGameFaceBinaryCarrierLooseAsset(
    std::string_view asset_path,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate);
bool IsNativeBootStreamAssetKey(std::string_view asset_path);
bool IsInternalLooseAssetKey(std::string_view asset_path);
bool IsLooseLuaWorkspaceRootKey(std::string_view asset_path);
std::string_view GetResourceManagerCachedDirectBypassReason(
    std::string_view asset_path);
std::string_view GetLooseNativeAssetSkipReason(std::string_view asset_path);
bool ShouldQueryVfsDatabaseAlias(std::string_view asset_path);
bool IsOptionalSyntheticVfsProbe(std::string_view asset_path);
bool IsKnownAbsentBootProbe(std::string_view asset_path);
std::string BuildProviderRelativeAssetPath(std::string_view provider,
                                           std::string_view name);
std::vector<std::string> BuildProviderOpenAssetPathCandidates(
    std::string_view provider,
    std::string_view name);
bool IsKnownOptionalBankOpenMiss(uint8_t* base,
                                 uint32_t provider_guest,
                                 std::string_view bank_name,
                                 std::string* out_asset_path);
bool ShouldSkipLooseNativeAsset(std::string_view asset_path,
                                std::string_view context);

}  // namespace detail
}  // namespace refii::vfs
