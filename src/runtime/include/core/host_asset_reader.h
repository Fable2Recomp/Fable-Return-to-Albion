#pragma once

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

#include "core/payload_bridge.h"
#include "core/payload_bridge_string.h"

namespace refii::vfs {

/// Godot's asset loader (`Engine_GameAssets.ReadAllBytes`) as a registered function pointer.
/// Call with a null buffer to learn the size, then again with one big enough, so no
/// allocation crosses the boundary.
using HostAssetReaderFn = int32_t (*)(const char* path, uint8_t* out, uint32_t capacity,
                                      uint32_t* out_size);

/// Installs Godot's reader. Passing nullptr unregisters it (Godot leaving the tree).
void SetHostAssetReader(HostAssetReaderFn reader);

/// Whether Godot is answering asset reads at all.
bool HostAssetReaderReady();

/// Reads one asset through Godot. False when there is no reader or no such asset. `path` is
/// game-relative with either separator.
inline bool ReadHostAsset(std::string_view path, std::vector<uint8_t>& out) {
  out.clear();

  uint32_t size = 0;
  if (refii_bridge_read_host_asset(path.data(), path.size(), nullptr, 0u, &size) == 0) {
    return false;
  }
  if (size == 0u) {
    return true;
  }

  out.resize(size);
  uint32_t written = 0;
  if (refii_bridge_read_host_asset(path.data(), path.size(), out.data(), size, &written) == 0) {
    out.clear();
    return false;
  }

  out.resize(written < size ? written : size);
  return true;
}

/// The asset's length without reading it; false when Godot has no such asset.
inline bool HostAssetSize(std::string_view path, uint32_t& out_size) {
  out_size = 0;
  return refii_bridge_host_asset_size(path.data(), path.size(), &out_size) != 0;
}

/// Callback from Godot answering whether a given asset path is an explicit mod overlay or
/// virtual asset rather than base game data.
using HostAssetIsOverrideFn = int32_t (*)(const char* path, size_t length);

/// Installs Godot's asset override query callback. Passing nullptr unregisters it.
void SetHostAssetOverrideQuery(HostAssetIsOverrideFn query);

/// Whether Godot serves an in-memory or modded override for this asset path.
inline bool HostAssetIsOverride(std::string_view path) {
  return refii_bridge_host_asset_is_override(path.data(), path.size()) != 0;
}

/// What Godot's query states about the path: 0 not an override, 1 an override (the game's own
/// loose or updated file among them), 2 a file a mod contributed or the host composed from mods
/// (HostBridge.SetAssetOverrideQuery). A title that must tell a mod's file from the game's own
/// loose file — Fable 3's disk files — reads the kind.
inline int32_t HostAssetOverrideKind(std::string_view path) {
  return refii_bridge_host_asset_is_override(path.data(), path.size());
}

/// Godot's hash index: the guest names most resources by a 32-bit hash of the asset name.
/// `out` receives the path; returns the length written, 0 for no such hash. A truncated
/// answer is a miss, not a short string.
using HostAssetNameForHashFn = uint32_t (*)(uint32_t hash, char* out, uint32_t capacity);

/// Installs Godot's hash index. Passing nullptr unregisters it.
void SetHostAssetNameResolver(HostAssetNameForHashFn resolver);

/// The asset path Godot holds for `hash`; empty when none or no resolver installed.
inline std::string HostAssetNameForHash(uint32_t hash) {
  return refii::bridge::TakeString([hash](char* out, size_t capacity) {
    return refii_bridge_host_asset_name_for_hash(hash, out, capacity);
  });
}

/// The first `count` bytes, for header sniffing.
inline bool ReadHostAssetPrefix(std::string_view path, uint32_t count,
                                std::vector<uint8_t>& out) {
  out.clear();

  uint32_t available = 0;
  if (refii_bridge_read_host_asset_prefix(path.data(), path.size(), count, nullptr, 0u,
                                          &available) == 0) {
    return false;
  }
  if (available == 0u) {
    return true;
  }

  out.resize(available);
  uint32_t written = 0;
  if (refii_bridge_read_host_asset_prefix(path.data(), path.size(), count, out.data(),
                                          available, &written) == 0) {
    out.clear();
    return false;
  }

  out.resize(written < available ? written : available);
  return true;
}

}  // namespace refii::vfs
