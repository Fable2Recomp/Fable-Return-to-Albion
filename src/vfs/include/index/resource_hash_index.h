#pragma once

#include "vfs_hook_types.h"

#include <cstdint>
#include <filesystem>
#include <memory>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace refii::vfs {
inline namespace detail {

bool LoadRefiiVfsDatabase(RefiiResourceHashIndex& index,
                          const std::filesystem::path& loose_root);
void AddRefiiExplicitResourceHashCandidate(RefiiResourceHashIndex& index,
                                           uint32_t resource_hash,
                                           std::string_view candidate);
void AddRefiiDatabaseAliasCandidate(RefiiResourceHashIndex& index,
                                    std::string_view alias,
                                    std::string_view candidate);
void AddRefiiResourceHashCandidate(RefiiResourceHashIndex& index,
                                   std::string_view candidate);
void AddRefiiBasenameCandidate(
    std::unordered_map<std::string, std::vector<std::string>>&
        basename_candidates,
    std::string_view relative_path);
void AddRefiiFileHashCsvMappings(
    RefiiResourceHashIndex& index,
    const std::filesystem::path& loose_root,
    const std::vector<std::filesystem::path>& filehash_csv_paths,
    const std::unordered_map<std::string, std::vector<std::string>>&
        basename_candidates);
void RecordRuntimeResourceHashName(uint32_t resource_hash,
                                   std::string_view name);
std::vector<std::string> GetRuntimeResourceHashNames(uint32_t resource_hash);
std::unordered_map<uint32_t, std::vector<std::string>>
SnapshotRuntimeResourceHashNames();
bool ShouldPassThroughNativeResourceHash(
    uint32_t resource_hash,
    const std::vector<std::string>& names);
void TraceLuaVfsBridgeResolution(std::string_view context,
                                 uint32_t resource_hash,
                                 std::string_view script_key);
void TraceLuaVfsBridgeResolution(std::string_view context,
                                 uint32_t resource_hash,
                                 const std::vector<std::string>& names);
std::shared_ptr<const SyntheticVfsEntryIndex> GetSyntheticVfsEntryIndex();
std::shared_ptr<const RefiiResourceHashIndex> GetRefiiResourceHashIndex();

}  // namespace detail
}  // namespace refii::vfs
