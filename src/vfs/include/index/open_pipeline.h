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
}  // namespace detail
}  // namespace refii::vfs
