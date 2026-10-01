#pragma once

#include "loose_asset_hooks.h"

#include <cstdint>
#include <string>
#include <string_view>

struct PPCContext;

namespace refii::vfs {
inline namespace detail {

enum class RefiiVfsOwnershipKind {
  RefiiOwned,
  NativeOnly,
  OptionalMiss,
  Unresolved,
};

struct RefiiVfsOwnershipDecision {
  RefiiVfsOwnershipKind kind = RefiiVfsOwnershipKind::Unresolved;
  std::string normalized;
  std::string candidate;
  std::string source;
  std::string reason;
  LooseAssetResolution resolution;
  bool has_resolution = false;
  bool native_shaped = false;
};


const char* ToString(RefiiVfsOwnershipKind kind);
bool IsRefiiNativeShapeSensitiveAsset(std::string_view asset_path);
bool HasRuntimeTextureName(uint32_t resource_hash);
bool IsRefiiTextureResolution(std::string_view request_name,
                              uint32_t resource_hash,
                              const LooseAssetResolution& resolution,
                              std::string_view candidate);
void LogRefiiOwnedTextureResolution(std::string_view api,
                                    uint32_t caller_lr,
                                    uint32_t resource_hash,
                                    std::string_view request_name,
                                    const LooseAssetResolution& resolution,
                                    std::string_view candidate,
                                    std::string_view source);
RefiiVfsOwnershipDecision DecideRefiiVfsOwnershipForName(
    std::string_view asset_path);
RefiiVfsOwnershipDecision DecideRefiiVfsOwnershipForHash(uint32_t resource_hash);
void LogRefiiVfsOwnershipDecision(std::string_view api,
                                  uint32_t caller_lr,
                                  std::string_view request_name,
                                  const RefiiVfsOwnershipDecision& decision);
bool TryRepairNativeShapedGetCachedOutput(
    PPCContext& ctx,
    uint8_t* base,
    const PPCContext& original_ctx,
    uint32_t out_guest,
    std::string_view name,
    std::string_view reason,
    const RefiiVfsOwnershipDecision& ownership);
}  // namespace detail
}  // namespace refii::vfs
