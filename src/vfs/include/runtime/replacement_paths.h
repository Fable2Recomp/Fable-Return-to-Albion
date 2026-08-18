#pragma once

#include "loose_asset_hooks.h"

#include <string>
#include <string_view>

namespace refii::vfs {

struct RefiiReplacementPathResolution {
  LooseAssetResolution resolution;
  std::string root_name;
  bool exists = false;
  bool is_directory = false;
};

/// Inline wrapper over the C entry in core/payload_bridge.h — the payload calls this, and
/// nothing but C may cross to it. Measured first, then filled: four independent strings
/// cannot share one grow-once attempt.
inline bool ResolveRefiiReplacementPath(
    std::string_view asset_path,
    RefiiReplacementPathResolution* out_resolution) {
  size_t host_length = 0;
  size_t key_length = 0;
  size_t relative_length = 0;
  size_t root_length = 0;
  int32_t exists = 0;
  int32_t is_directory = 0;

  if (refii_bridge_vfs_resolve_refii_replacement_path(
          asset_path.data(), asset_path.size(), nullptr, 0, &host_length, nullptr, 0,
          &key_length, nullptr, 0, &relative_length, nullptr, 0, &root_length, &exists,
          &is_directory) == 0) {
    return false;
  }

  if (out_resolution == nullptr) {
    return true;
  }

  std::string host(host_length, '\0');
  std::string key(key_length, '\0');
  std::string relative(relative_length, '\0');
  std::string root(root_length, '\0');

  if (refii_bridge_vfs_resolve_refii_replacement_path(
          asset_path.data(), asset_path.size(), host.data(), host.size(), &host_length,
          key.data(), key.size(), &key_length, relative.data(), relative.size(),
          &relative_length, root.data(), root.size(), &root_length, &exists,
          &is_directory) == 0) {
    return false;
  }

  out_resolution->resolution.host_path = std::filesystem::path(host);
  out_resolution->resolution.normalized_key = std::move(key);
  out_resolution->resolution.provider_relative_path = std::move(relative);
  out_resolution->root_name = std::move(root);
  out_resolution->exists = exists != 0;
  out_resolution->is_directory = is_directory != 0;
  return true;
}

}  // namespace refii::vfs
