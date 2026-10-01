#pragma once

#include "loose_asset_hooks.h"
#include "index/runtime_tables.h"
#include "vfs_hook_types.h"

#include <array>
#include <cstdint>
#include <filesystem>
#include <string>
#include <string_view>
#include <unordered_set>
#include <vector>

namespace refii::vfs {
inline namespace detail {

uint32_t LoadBigEndianU32(const uint8_t* bytes);
bool IsRegularHostFile(const std::filesystem::path& path);
uint64_t HostFileSize(const std::filesystem::path& path);

/// Size and first words of a resolution, whichever side (disk or Godot mount) owns its bytes.
uint64_t HostFileSize(const LooseAssetResolution& resolution);
std::array<uint32_t, 4> ReadHostFileFirstWords(
    const LooseAssetResolution& resolution);

/// The first `count` bytes of a resolution, whichever side owns it; `count` 0 means everything.
bool ReadResolutionBytes(const LooseAssetResolution& resolution, uint32_t count,
                         std::vector<uint8_t>& out);
std::array<uint32_t, 4> ReadHostFileFirstWords(
    const std::filesystem::path& path);
std::string FormatHostFileFirstWords(const std::array<uint32_t, 4>& words);
bool IsTextureAssetCandidate(std::string_view candidate);
bool IsTextureHeaderCandidate(std::string_view candidate);
bool LooksLikeResourceWrapperHeader(const std::array<uint32_t, 4>& words);
bool ResolveBankRootedVfsDatabaseCandidate(
    const std::filesystem::path& loose_root,
    std::string_view candidate,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate);
bool MakeVfsDatabaseCandidateResolution(
    const std::filesystem::path& loose_root,
    std::string_view candidate,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate);
bool ResolvePairedTexturePayloadForHeader(std::string_view header_key,
                                          LooseAssetResolution* out_resolution,
                                          std::string* out_candidate);
}  // namespace detail
}  // namespace refii::vfs
