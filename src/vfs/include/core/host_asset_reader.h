#pragma once

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

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
bool ReadHostAsset(std::string_view path, std::vector<uint8_t>& out);

/// The asset's length without reading it; false when Godot has no such asset.
bool HostAssetSize(std::string_view path, uint32_t& out_size);

/// Godot's hash index: the guest names most resources by a 32-bit hash of the asset name.
/// `out` receives the path; returns the length written, 0 for no such hash. A truncated
/// answer is a miss, not a short string.
using HostAssetNameForHashFn = uint32_t (*)(uint32_t hash, char* out, uint32_t capacity);

/// Installs Godot's hash index. Passing nullptr unregisters it.
void SetHostAssetNameResolver(HostAssetNameForHashFn resolver);

/// The asset path Godot holds for `hash`; empty when none or no resolver installed.
std::string HostAssetNameForHash(uint32_t hash);

/// The first `count` bytes, for header sniffing.
bool ReadHostAssetPrefix(std::string_view path, uint32_t count, std::vector<uint8_t>& out);

}  // namespace refii::vfs
