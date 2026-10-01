#pragma once

#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

#include "core/payload_bridge.h"
#include "core/payload_bridge_string.h"

namespace refii::vfs {

// THE CROSSED ONES ARE INLINE WRAPPERS over the C entries in core/payload_bridge.h.
//
// Everything below that the payload calls has to reach librefii_vfs without a C++ symbol:
// the payload is compiled on the player's machine and, on Windows, by a compiler whose C++
// ABI is not ours. Wrapping in the header keeps all ~729 existing call sites unchanged, and
// the wrappers inline away at -O2 — the call site emits the same cross-module call it did
// before. See src/vfs/include/core/payload_bridge.h for the contract.
//
// The rest stay plain declarations: nothing in the payload calls them.
inline bool LooseAssetOverrideTracingEnabled() {
  return refii_bridge_vfs_loose_asset_override_tracing_enabled() != 0;
}
inline bool VfsTracingEnabled() { return refii_bridge_vfs_tracing_enabled() != 0; }
inline bool VfsVerboseIoTracingEnabled() {
  return refii_bridge_vfs_verbose_io_tracing_enabled() != 0;
}
inline bool LooseGameFaceBinaryOverridesEnabled() {
  return refii_bridge_vfs_loose_gameface_binary_overrides_enabled() != 0;
}

struct LooseAssetResolution {
  std::filesystem::path host_path;
  std::string normalized_key;
  std::string provider_relative_path;

  /// True when Godot holds the bytes and there is no host path to open; `normalized_key` is
  /// then the virtual path Godot's reader takes.
  bool host_served = false;
};

inline std::filesystem::path GetLooseAssetRoot() {
  return std::filesystem::path(refii::bridge::TakeString(
      [](char* out, size_t capacity) { return refii_bridge_vfs_loose_asset_root(out, capacity); }));
}

inline std::vector<std::filesystem::path> GetLooseAssetSearchRoots() {
  std::vector<std::filesystem::path> roots;
  uint32_t count = refii_bridge_vfs_loose_asset_search_root_count();
  roots.reserve(count);
  for (uint32_t index = 0; index < count; ++index) {
    roots.emplace_back(refii::bridge::TakeString([index](char* out, size_t capacity) {
      return refii_bridge_vfs_loose_asset_search_root(index, out, capacity);
    }));
  }
  return roots;
}

inline bool ShouldDeferLooseBankContainer(std::string_view asset_path) {
  return refii_bridge_vfs_should_defer_loose_bank_container(asset_path.data(),
                                                            asset_path.size()) != 0;
}
inline std::string NormalizeLooseAssetKey(std::string_view asset_path) {
  return refii::bridge::TakeString([asset_path](char* out, size_t capacity) {
    return refii_bridge_vfs_normalize_loose_asset_key(asset_path.data(), asset_path.size(),
                                                      out, capacity);
  });
}
}  // namespace refii::vfs
