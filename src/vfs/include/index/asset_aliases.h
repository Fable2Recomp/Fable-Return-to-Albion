#pragma once

#include "loose_asset_hooks.h"

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace refii::vfs {
inline namespace detail {
bool IsGameFaceBinaryAssetKey(std::string_view asset_path);
bool IsOptionalGameFaceBinaryMiss(std::string_view asset_path);
bool ResolveExactGameFaceBinaryCarrierLooseAsset(
    std::string_view asset_path,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate);
bool IsInternalLooseAssetKey(std::string_view asset_path);
bool IsLooseLuaWorkspaceRootKey(std::string_view asset_path);
std::string_view GetResourceManagerCachedDirectBypassReason(
    std::string_view asset_path);
std::string_view GetLooseNativeAssetSkipReason(std::string_view asset_path);
bool IsOptionalSyntheticVfsProbe(std::string_view asset_path);
bool IsKnownAbsentBootProbe(std::string_view asset_path);
bool ShouldSkipLooseNativeAsset(std::string_view asset_path,
                                std::string_view context);
}  // namespace detail
}  // namespace refii::vfs
