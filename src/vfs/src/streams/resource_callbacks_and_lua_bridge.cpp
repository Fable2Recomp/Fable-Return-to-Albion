#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {
bool StartsWith(std::string_view value, std::string_view prefix) {
  return value.size() >= prefix.size() && value.substr(0, prefix.size()) == prefix;
}

bool EndsWith(std::string_view value, std::string_view suffix) {
  return value.size() >= suffix.size() &&
         value.substr(value.size() - suffix.size()) == suffix;
}

uint64_t MakeBankManifestLookupKey(uint32_t offset, uint32_t size) {
  return (static_cast<uint64_t>(offset) << 32) | static_cast<uint64_t>(size);
}

std::filesystem::path ResolveCaseInsensitiveExistingPath(
    const std::filesystem::path& root,
    const std::filesystem::path& relative_path);

bool IsLuaScriptAssetKey(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  if (!EndsWith(normalized, ".lua")) {
    return false;
  }

  // Lua has its own override path. The native loose bank hook deals in raw
  // streams, so feeding decompiled/text Lua through it can stall consumers that
  // expect the original banked bytecode layout.
  return true;
}

std::string StripTrailingSlashes(std::string value) {
  while (!value.empty() && value.back() == '/') {
    value.pop_back();
  }
  return value;
}

std::vector<std::string> BuildGameFaceRedirectCandidates(
    std::string_view asset_path) {
  const std::string normalized =
      StripTrailingSlashes(NormalizeLooseAssetKey(asset_path));
  std::vector<std::string> candidates;
  if (normalized.empty()) {
    return candidates;
  }

  auto add_candidate = [&](std::string value) {
    value = StripTrailingSlashes(NormalizeLooseAssetKey(value));
    if (value.empty() ||
        std::find(candidates.begin(), candidates.end(), value) !=
            candidates.end()) {
      return;
    }
    candidates.push_back(std::move(value));
  };

  constexpr std::string_view kDataScriptsGameface = "data/scripts/gameface";
  constexpr std::string_view kScriptsGameface = "scripts/gameface";
  constexpr std::string_view kDataArtGameface = "data/art/gui/gameface";
  constexpr std::string_view kArtGameface = "art/gui/gameface";
  constexpr std::string_view kGuiScriptsGameface = "guiscripts/scripts/gameface";
  constexpr std::string_view kGuiArtGameface = "guiscripts/art/gui/gameface";

  auto remap_prefix = [&](std::string_view prefix, std::string_view replacement) {
    if (!StartsWith(normalized, prefix)) {
      return;
    }
    std::string tail = std::string(normalized.substr(prefix.size()));
    while (!tail.empty() && tail.front() == '/') {
      tail.erase(tail.begin());
    }
    add_candidate(std::string(replacement) + (tail.empty() ? "" : "/" + tail));
  };

  auto remap_embedded = [&](std::string_view marker,
                            std::string_view replacement) {
    const size_t marker_pos = normalized.find(marker);
    if (marker_pos == std::string::npos) {
      return;
    }
    std::string tail =
        std::string(normalized.substr(marker_pos + marker.size()));
    while (!tail.empty() && tail.front() == '/') {
      tail.erase(tail.begin());
    }
    add_candidate(std::string(replacement) + (tail.empty() ? "" : "/" + tail));
  };

  remap_prefix(kDataScriptsGameface, kGuiScriptsGameface);
  remap_prefix(kScriptsGameface, kGuiScriptsGameface);
  remap_prefix(kDataArtGameface, kGuiArtGameface);
  remap_prefix(kArtGameface, kGuiArtGameface);
  remap_embedded("sourceassets/scripts/gameface", kGuiScriptsGameface);
  remap_embedded("scripts/gameface", kGuiScriptsGameface);
  remap_embedded("sourceassets/art/gui/gameface", kGuiArtGameface);
  remap_embedded("art/gui/gameface", kGuiArtGameface);
  add_candidate(normalized);
  return candidates;
}
}  // namespace detail
}  // namespace refii::vfs
