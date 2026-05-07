#pragma once

#include "loose_asset_hooks.h"

#include <cstdint>
#include <string>
#include <string_view>

namespace refii::vfs {
inline namespace detail {

bool MaybeUpgradeResourceManagerTextureResolution(
    uint32_t resource_hash,
    std::string_view request_name,
    uint32_t caller_lr,
    LooseAssetResolution* inout_resolution,
    std::string* inout_candidate);
bool ResolveIndexedResourceHashLooseAsset(uint32_t resource_hash,
                                          LooseAssetResolution* out_resolution,
                                          std::string* out_candidate);
bool ResolveTargetedRequestManagerHashLooseAsset(
    uint32_t resource_hash,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate);
bool ResolveResourceHashLooseAsset(uint32_t resource_hash,
                                   LooseAssetResolution* out_resolution,
                                   std::string* out_candidate,
                                   bool prefer_resource_manager_texture_shape,
                                   uint32_t caller_lr);

}  // namespace detail
}  // namespace refii::vfs
