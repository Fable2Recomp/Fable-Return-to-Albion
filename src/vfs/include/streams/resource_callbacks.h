#pragma once

#include "vfs_hook_types.h"

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

struct PPCContext;

namespace refii::vfs {
inline namespace detail {

std::string DescribeRefiiHostOrCompositeStream(uint8_t* base,
                                               uint32_t stream_guest);
void LogTextureStreamBuildResult(uint8_t* base,
                                 const PPCContext& original_ctx,
                                 uint32_t out_guest,
                                 uint32_t texture_set_guest,
                                 uint32_t texture_index);
std::string DescribeResourceCallbackState(uint8_t* base,
                                          uint32_t callback_table_guest);
std::string DescribeInitModelHeaderSummary(uint8_t* base);
void LogLooseBankHitDetail(std::string_view api,
                           uint8_t* base,
                           const PPCContext& caller_ctx,
                           uint32_t out_guest,
                           uint32_t bank_guest,
                           uint32_t manager_guest,
                           uint32_t mode,
                           uint64_t hit_count,
                           std::string_view bank_name,
                           std::string_view entry_name,
                           std::string_view candidate,
                           const LooseAssetResolution& resolution);
uint32_t GetGlobalDataProvider(uint8_t* base, const PPCContext& source_ctx);
bool StartsWith(std::string_view value, std::string_view prefix);
bool EndsWith(std::string_view value, std::string_view suffix);
bool IsLuaScriptAssetKey(std::string_view asset_path);
bool LuaVfsBridgeEnabled();
bool LooseLuaOverridesEnabled();
bool ResolveLuaVfsOverride(std::string_view asset_path,
                           LooseAssetResolution* out_resolution,
                           std::string* out_candidate);
uint64_t MakeBankManifestLookupKey(uint32_t offset, uint32_t size);
std::string ManifestPathToGenericPath(std::string path);
std::string StripTrailingSlashes(std::string value);
void AddLuaVfsCarrierCandidate(std::vector<std::string>& candidates,
                               std::string value);
std::vector<std::string> BuildGameFaceRedirectCandidates(
    std::string_view asset_path);

}  // namespace detail
}  // namespace refii::vfs
