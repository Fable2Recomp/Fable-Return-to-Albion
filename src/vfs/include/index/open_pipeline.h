#pragma once

#include "index/ownership.h"
#include "index/runtime_tables.h"
#include "loose_asset_hooks.h"
#include "vfs_hook_types.h"

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <optional>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

struct PPCContext;

namespace refii::vfs {
inline namespace detail {

enum class RefiiVfsOpenShape {
  RawStream,
  ResourceWrapper,
  NativeGenerated,
  NullOptionalMiss,
  SyntheticBank,
};

enum class RefiiVfsOpenDisposition {
  Miss,
  Opened,
  NativeGenerated,
  OptionalNull,
  DeferredBank,
  OpenFailed,
};

struct RefiiVfsOpenRequest {
  std::string_view api;
  std::string_view request_name;
  uint32_t resource_hash = 0;
  uint32_t out_guest = 0;
  uint32_t manager_guest = 0;
  uint32_t mode = 1;
  uint32_t resource_flags = 0;
  uint64_t resource_offset = 0;
  uint32_t resource_size = 0;
  uint32_t caller_lr = 0;
  bool prefer_resource_manager_texture_shape = false;
  bool allow_optional_null_miss = false;
};

struct RefiiVfsOpenResult {
  RefiiVfsOpenDisposition disposition = RefiiVfsOpenDisposition::Miss;
  RefiiVfsOwnershipDecision ownership;
  LooseAssetResolution resolution;
  std::string candidate;
  std::string source;
  uint64_t hit_count = 0;
  uint32_t opened_guest = 0;
  bool handled = false;
  bool opened = false;
  bool native_generated = false;
};

struct RefiiResolvedVfsOpenRequest {
  std::string_view api;
  std::string_view request_name;
  std::string_view candidate;
  std::string_view source;
  uint32_t resource_hash = 0;
  uint32_t out_guest = 0;
  uint32_t manager_guest = 0;
  uint32_t mode = 1;
  uint32_t resource_flags = 0;
  uint64_t resource_offset = 0;
  uint32_t resource_size = 0;
  uint32_t caller_lr = 0;
  bool count_vfs_hit = true;
};

struct RequestManagerHashEntry {
  uint32_t hash = 0;
  uint32_t provider_index = 0;
  uint32_t provider_entry_key = 0;
};

struct RefiiBankEntryOpenRequest {
  std::string_view api;
  std::string_view bank_name;
  std::string_view entry_name;
  const BankManifestEntry* manifest_entry = nullptr;
  uint32_t out_guest = 0;
  uint32_t manager_guest = 0;
  uint32_t mode = 0;
  uint32_t caller_lr = 0;
  bool count_bank_lookup = false;
  std::string_view lookup_label;
  std::string_view lua_trace_tag;
};


const char* ToString(RefiiVfsOpenShape shape);
const char* ToString(RefiiVfsOpenDisposition disposition);
bool IsHandledRefiiVfsOpenDisposition(RefiiVfsOpenDisposition disposition);
RefiiVfsOpenDisposition TryOpenResolvedRefiiVfsAsset(
    PPCContext& ctx,
    uint8_t* base,
    const PPCContext& original_ctx,
    RefiiVfsOpenShape shape,
    const LooseAssetResolution& resolution,
    const RefiiResolvedVfsOpenRequest& request,
    RefiiVfsOpenResult* out_result = nullptr);
RefiiVfsOpenDisposition TryOpenRefiiVfsAsset(
    PPCContext& ctx,
    uint8_t* base,
    const PPCContext& original_ctx,
    RefiiVfsOpenShape shape,
    const RefiiVfsOpenRequest& request,
    RefiiVfsOpenResult* out_result = nullptr);
RefiiVfsOpenDisposition TryOpenRefiiBankEntryAsset(
    PPCContext& ctx,
    uint8_t* base,
    const PPCContext& original_ctx,
    const RefiiBankEntryOpenRequest& request,
    RefiiVfsOpenResult* out_result = nullptr);
uint64_t HashFingerprintString(uint64_t value, std::string_view text);
uint64_t FingerprintRequestManagerEntries(
    const std::vector<RequestManagerHashEntry>& entries);
uint64_t FingerprintRuntimeResourceHashNames(
    const std::unordered_map<uint32_t, std::vector<std::string>>& runtime_names);
bool IsCachedRequestManagerHashAugmentNoop(std::string_view cache_key);
void CacheRequestManagerHashAugmentNoop(std::string cache_key);
std::vector<uint32_t> SnapshotKnownRequestManagerGuests();
void AddRefiiRequestManagerHashEntry(
    std::vector<RequestManagerHashEntry>& entries,
    std::unordered_set<uint32_t>& added_hashes,
    uint32_t resource_hash,
    size_t* contributed);
bool IsRefiiHashCandidateOwnedByDatabase(
    const std::filesystem::path& loose_root,
    std::string_view candidate);
bool RefiiDatabaseHashHasOwnedResolution(uint32_t resource_hash,
                                         std::string_view candidate,
                                         std::string* out_resolved_candidate);
void DeferFrontendTextureHashWake();
void RegisterResolvedSyntheticBankOpenAlias(std::string_view bank_alias,
                                            std::string_view resolved_bank,
                                            std::string_view reason);
bool ResolveRefiiOwnedTextureDescriptorHash(std::string_view api,
                                            uint32_t resource_hash,
                                            uint32_t caller_lr,
                                            LooseAssetResolution* out_resolution,
                                            std::string* out_name,
                                            std::string* out_candidate,
                                            std::string* out_source);
void BuildTextureResourceDescriptor(PPCContext& source_ctx,
                                    uint8_t* base,
                                    uint32_t descriptor_guest,
                                    uint32_t mode,
                                    uint32_t hash_pointer_guest,
                                    uint32_t ref_pointer_guest);
void LogRefiiOwnedTextureStreamBuilt(uint8_t* base,
                                     std::string_view api,
                                     uint32_t caller_lr,
                                     uint32_t resource_hash,
                                     std::string_view request_name,
                                     const LooseAssetResolution& resolution,
                                     std::string_view candidate,
                                     std::string_view source,
                                     uint32_t out_guest,
                                     uint32_t stream_guest,
                                     bool wrapper_shape);
bool TryResolveRefiiFrontendTextureDescriptor(
    PPCContext& ctx,
    uint8_t* base,
    uint32_t descriptor_guest,
    uint32_t resource_hash,
    uint32_t caller_lr,
    uint32_t table_index,
    uint32_t table_object_guest,
    const LooseAssetResolution& resolution,
    std::string_view name,
    std::string_view candidate,
    std::string_view source,
    uint32_t* out_resolver_result,
    std::string* out_failure_reason);
bool RefiiDatabaseHashHasExactOwnedCandidate(uint32_t resource_hash,
                                             std::string_view candidate);
bool ConsumeDeferredFrontendTextureHashWake();
size_t RefiiPrivateHandleCount();
size_t TargetedRequestManagerHashContributionCount();
std::optional<TargetedRequestManagerHashContribution>
GetTargetedRequestManagerHashContribution(uint32_t resource_hash);
std::vector<std::pair<uint32_t, TargetedRequestManagerHashContribution>>
SnapshotTargetedRequestManagerHashContributions();
void RecordKnownRequestManagerGuest(uint32_t manager_guest,
                                    std::string_view source);
std::string FormatKnownRequestManagerGuestsForLog(size_t max_count);
inline std::string FormatKnownRequestManagerGuestsForLog() {
  return FormatKnownRequestManagerGuestsForLog(8);
}
std::string FormatTargetedTextureHashTableAudit(uint8_t* base);
bool RegisterTargetedRequestManagerHashContribution(
    uint32_t resource_hash,
    std::string_view name,
    std::string_view reason,
    std::string_view resolved_candidate,
    std::string_view provider_relative,
    std::string_view source);
void RecordRefiiLastVfsAliasOperation(std::string_view api,
                                      std::string_view requested,
                                      std::string_view resolved,
                                      uint32_t requested_hash,
                                      uint32_t resolved_hash,
                                      uint32_t manager_guest,
                                      std::string_view ownership);
std::string FormatRefiiLastVfsOperationForLog();
bool ShouldQueryVfsDatabaseAlias(std::string_view asset_path);
bool ResolveVfsDatabaseExactAliasLooseAsset(std::string_view asset_path,
                                            LooseAssetResolution* out_resolution,
                                            std::string* out_candidate);
bool ResolveRefiiVirtualFileSystemProbe(std::string_view asset_path,
                                        bool* out_is_directory,
                                        LooseAssetResolution* out_resolution,
                                        std::string* out_candidate);
bool IsRefiiPrivateHandle(uint32_t handle);
bool CloseRefiiPrivateHandle(uint32_t handle);
bool TryCreateRefiiPrivateFileHandle(std::string_view path,
                                     uint32_t desired_access,
                                     uint32_t creation_disposition,
                                     uint32_t* out_handle,
                                     LooseAssetResolution* out_resolution,
                                     std::string* out_candidate);
bool TryReadRefiiPrivateFileHandle(PPCContext& ctx,
                                   uint8_t* base,
                                   uint32_t handle,
                                   uint32_t buffer_guest,
                                   uint32_t requested_bytes,
                                   uint32_t bytes_read_guest,
                                   uint32_t overlapped_guest);
bool TryGetRefiiPrivateFileSize(uint32_t handle, uint64_t* out_size);
bool TrySetRefiiPrivateFilePointer(PPCContext& ctx,
                                   uint8_t* base,
                                   uint32_t handle,
                                   int32_t distance_low,
                                   uint32_t distance_high_guest,
                                   uint32_t move_method);
bool TryFindFirstRefiiPrivate(PPCContext& ctx,
                              uint8_t* base,
                              std::string_view path,
                              uint32_t find_data_guest);
bool TryFindNextRefiiPrivate(PPCContext& ctx,
                             uint8_t* base,
                             uint32_t handle,
                             uint32_t find_data_guest);

}  // namespace detail
}  // namespace refii::vfs
