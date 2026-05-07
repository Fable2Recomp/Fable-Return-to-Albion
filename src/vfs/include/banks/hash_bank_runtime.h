#pragma once

#include "loose_asset_hooks.h"
#include "vfs_hook_types.h"

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

struct PPCContext;

namespace refii::vfs {
inline namespace detail {

std::string MakeBackslashResourceName(std::string_view alias);
std::vector<std::string> GetRuntimeLanguageFamilyAliases(
    std::string_view normalized_alias);
bool RuntimeResourceHashHasLooseResolution(uint32_t resource_hash,
                                           std::string* out_name,
                                           std::string* out_candidate);
bool RuntimeResourceHashNamesAreOptionalMisses(uint32_t resource_hash);
bool AugmentRequestManagerHashTable(PPCContext& ctx,
                                    uint8_t* base,
                                    uint32_t manager_guest,
                                    std::string_view api);
void RerunKnownRequestManagerHashAugmentation(PPCContext& ctx,
                                              uint8_t* base,
                                              std::string_view api,
                                              uint32_t resource_hash,
                                              std::string_view name);
void RegisterRefiiOwnedTextureHashContribution(PPCContext& ctx,
                                               uint8_t* base,
                                               uint32_t resource_hash,
                                               std::string_view name,
                                               uint32_t caller_lr);
bool ShouldRegisterBootVfsResourceHashContribution(std::string_view normalized);
void RegisterRefiiOwnedResourceHashContribution(PPCContext& ctx,
                                                uint8_t* base,
                                                uint32_t resource_hash,
                                                std::string_view name,
                                                uint32_t caller_lr);
bool ResolveSyntheticBankEntryLooseAsset(std::string_view asset_path,
                                         LooseAssetResolution* out_resolution,
                                         std::string* out_candidate);

}  // namespace detail
}  // namespace refii::vfs
