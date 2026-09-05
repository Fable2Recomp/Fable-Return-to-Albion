#include "loose_asset_hooks.h"
#include "gdb/gdb_csv_override.h"
#include "babel/babel_csv_override.h"
#include "runtime/cvars.h"

#include <algorithm>
#include <cstring>
#include <cctype>
#include <filesystem>
#include <chrono>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

#include <rex/runtime.h>


namespace refii::vfs {
namespace {

constexpr std::string_view kUnpackedWorkspaceFolder = "unpacked";

bool StartsWith(std::string_view value, std::string_view prefix) {
  return value.size() >= prefix.size() && value.substr(0, prefix.size()) == prefix;
}

bool EndsWith(std::string_view value, std::string_view suffix) {
  return value.size() >= suffix.size() &&
         value.substr(value.size() - suffix.size()) == suffix;
}

std::string NormalizeRelativePathForResolution(std::string_view path) {
  std::string normalized(path);
  std::replace(normalized.begin(), normalized.end(), '\\', '/');
  return normalized;
}

std::filesystem::path NormalizeLooseAssetPath(std::filesystem::path path) {
  if (path.empty()) {
    return {};
  }
  return path.lexically_normal();
}

void AddUnique(std::vector<std::string>& values, std::string value) {
  while (!value.empty() && (value.front() == '/' || value.front() == '\\')) {
    value.erase(value.begin());
  }
  while (!value.empty() && StartsWith(value, "./")) {
    value.erase(0, 2);
  }
  if (value.empty()) {
    return;
  }
  if (std::find(values.begin(), values.end(), value) == values.end()) {
    values.push_back(std::move(value));
  }
}

void AddGeneratedBankRootCandidates(std::vector<std::string>& candidates,
                                    std::string_view relative) {
  if (relative.empty() || relative.find(':') != std::string_view::npos ||
      StartsWith(relative, "levels/") || StartsWith(relative, "streaming/")) {
    return;
  }

  AddUnique(candidates, "levels/" + std::string(relative));
  AddUnique(candidates, "streaming/" + std::string(relative));
}

void AddGlobalTextureCandidates(std::vector<std::string>& candidates,
                                std::string_view relative) {
  if (!StartsWith(relative, "art/") || !EndsWith(relative, ".tex")) {
    return;
  }

  AddUnique(candidates, "globals/1024mip0_textures/" + std::string(relative));
  AddUnique(candidates, "globals/globals_textures/" + std::string(relative));
  AddUnique(candidates,
            "globals/globals_texture_headers/" + std::string(relative));

  std::string extracted_layout(relative);
  const auto replace_first = [](std::string* value,
                                std::string_view from,
                                std::string_view to) {
    const size_t position = value->find(from);
    if (position == std::string::npos) {
      return false;
    }
    value->replace(position, from.size(), to);
    return true;
  };
  const bool repaired =
      replace_first(&extracted_layout, "pfx_texture_headers/", "pfx_textures/") ||
      replace_first(&extracted_layout, "/texture_headers/", "/textures/") ||
      replace_first(&extracted_layout, "_texture_headers/", "_textures/");
  if (!repaired || extracted_layout == relative) {
    return;
  }

  AddUnique(candidates, extracted_layout);
  if (StartsWith(extracted_layout, "art/fx/")) {
    AddUnique(candidates,
              "globals/globals_texture_headers/" + extracted_layout);
    AddUnique(candidates, "globals/globals_textures/" + extracted_layout);
  } else if (StartsWith(extracted_layout, "art/gui/")) {
    AddUnique(candidates,
              "art/gui/gui_texture_headers/" + extracted_layout);
    AddUnique(candidates, "art/gui/gui_textures/" + extracted_layout);
    AddUnique(candidates, "globals/1024mip0_textures/" + extracted_layout);
  }
}

void AddTitleUpdateDatabaseCandidates(std::vector<std::string>& candidates,
                                      std::string_view relative) {
  constexpr std::string_view kTitleUpdateSuffix = ".gdb.tu";
  if (!EndsWith(relative, kTitleUpdateSuffix)) {
    return;
  }

  std::string base_database(relative);
  base_database.erase(base_database.size() - 3);
  AddUnique(candidates, std::move(base_database));
}

void AddLocalizedAssetCandidates(std::vector<std::string>& candidates,
                                 std::string_view relative) {
  if (!StartsWith(relative, "language/") &&
      !StartsWith(relative, "language_dlc/")) {
    return;
  }

  constexpr std::string_view kVideos = "/videos/";
  constexpr std::string_view kFonts = "/fonts/";
  constexpr std::string_view kText = "/text/";
  constexpr std::string_view kPatchText = "/patch_text/";
  constexpr std::string_view kSpeechDoc = "/speechdoc/";
  const size_t videos = relative.find(kVideos);
  if (videos != std::string_view::npos && EndsWith(relative, ".bik")) {
    const std::string tail =
        std::string(relative.substr(videos + kVideos.size()));
    AddUnique(candidates, "language/en-uk/videos/" + tail);
    return;
  }

  const size_t fonts = relative.find(kFonts);
  if (fonts != std::string_view::npos && EndsWith(relative, ".bft")) {
    const std::string tail =
        std::string(relative.substr(fonts + kFonts.size()));
    AddUnique(candidates, "language/en-uk/fonts/" + tail);
    AddUnique(candidates, "language/fonts/" + tail);
    return;
  }

  const size_t text = relative.find(kText);
  if (text != std::string_view::npos && EndsWith(relative, ".babel")) {
    const std::string tail = std::string(relative.substr(text + kText.size()));
    AddUnique(candidates, "language/en-uk/text/" + tail);
    return;
  }

  const size_t patch_text = relative.find(kPatchText);
  if (patch_text != std::string_view::npos && EndsWith(relative, ".babel")) {
    const std::string tail =
        std::string(relative.substr(patch_text + kPatchText.size()));
    AddUnique(candidates, "language/en-uk/text/" + tail);
    return;
  }

  const size_t speechdoc = relative.find(kSpeechDoc);
  if (speechdoc != std::string_view::npos && EndsWith(relative, ".adb")) {
    const std::string tail =
        std::string(relative.substr(speechdoc + kSpeechDoc.size()));
    AddUnique(candidates, "language/en-uk/speechdoc/" + tail);
  }
}

bool IsNativeLooseAssetExcludedCandidate(std::string_view normalized) {
  return StartsWith(normalized, "luasource/") ||
         StartsWith(normalized, ".baseline/") ||
         StartsWith(normalized, ".cache/") ||
         StartsWith(normalized, "_disasm/") ||
         StartsWith(normalized, "scripts/") ||
         StartsWith(normalized, "scripts_r/") ||
         StartsWith(normalized, "data/scripts/") ||
         StartsWith(normalized, "data/scripts_r/");
}

std::vector<std::string> BuildLooseAssetCandidates(std::string_view normalized) {
  std::vector<std::string> relatives;
  AddUnique(relatives, std::string(normalized));

  if (StartsWith(normalized, "data/")) {
    AddUnique(relatives, std::string(normalized.substr(5)));
  }
  if (StartsWith(normalized, "data/scripts/")) {
    AddUnique(relatives, "scripts/" +
                             std::string(normalized.substr(sizeof("data/scripts/") - 1)));
  }
  if (StartsWith(normalized, "data/scripts_r/")) {
    AddUnique(relatives, "scripts_r/" +
                             std::string(normalized.substr(sizeof("data/scripts_r/") - 1)));
  }
  if (StartsWith(normalized, "unpacked/")) {
    AddUnique(relatives, std::string(normalized.substr(9)));
  }

  std::vector<std::string> candidates;
  for (const std::string& relative : relatives) {
    if (IsNativeLooseAssetExcludedCandidate(relative)) {
      continue;
    }
    AddUnique(candidates, relative);
    AddTitleUpdateDatabaseCandidates(candidates, relative);
    AddGeneratedBankRootCandidates(candidates, relative);
    AddGlobalTextureCandidates(candidates, relative);
    AddLocalizedAssetCandidates(candidates, relative);
  }
  return candidates;
}

std::string NormalizeRelativeLookupKey(std::string_view path) {
  std::string key(path);
  std::replace(key.begin(), key.end(), '\\', '/');
  std::ranges::transform(key, key.begin(), [](unsigned char ch) {
    return static_cast<char>(std::tolower(ch));
  });

  if (StartsWith(key, "game:/")) {
    key.erase(0, 6);
  } else if (StartsWith(key, "game:")) {
    key.erase(0, 5);
  }

  while (!key.empty() && key.front() == '/') {
    key.erase(key.begin());
  }
  while (!key.empty() && StartsWith(key, "./")) {
    key.erase(0, 2);
  }

  return key;
}

struct CachedLooseAsset {
  bool found = false;
  std::filesystem::path host_path;
  std::string relative_path;
};

std::mutex& LooseAssetLookupMutex() {
  static std::mutex mutex;
  return mutex;
}

std::unordered_map<std::string, std::unordered_map<std::string, CachedLooseAsset>>&
LooseAssetLookupCaches() {
  static std::unordered_map<std::string,
                            std::unordered_map<std::string, CachedLooseAsset>>
      caches;
  return caches;
}

std::unordered_map<std::string, std::unordered_map<std::string, std::filesystem::path>>&
LooseAssetDirectoryCaches() {
  static std::unordered_map<std::string,
                            std::unordered_map<std::string, std::filesystem::path>>
      caches;
  return caches;
}

std::filesystem::path ResolveCaseInsensitiveChild(
    const std::filesystem::path& directory,
    std::string_view component) {
  std::string lowered_component(component);
  std::ranges::transform(lowered_component, lowered_component.begin(),
                         [](unsigned char ch) {
                           return static_cast<char>(std::tolower(ch));
                         });
  if (lowered_component.empty()) {
    return {};
  }

  const std::string directory_key = directory.lexically_normal().generic_string();
  {
    std::lock_guard<std::mutex> lock(LooseAssetLookupMutex());
    const auto directory_match = LooseAssetDirectoryCaches().find(directory_key);
    if (directory_match != LooseAssetDirectoryCaches().end()) {
      const auto match = directory_match->second.find(lowered_component);
      return match != directory_match->second.end() ? match->second
                                                    : std::filesystem::path{};
    }
  }

  std::unordered_map<std::string, std::filesystem::path> entries;
  std::error_code error;
  if (std::filesystem::is_directory(directory, error)) {
    for (std::filesystem::directory_iterator iterator(directory, error), end;
         iterator != end; iterator.increment(error)) {
      if (error) {
        error.clear();
        continue;
      }

      std::string filename = iterator->path().filename().string();
      std::ranges::transform(filename, filename.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
      });
      entries.try_emplace(std::move(filename), iterator->path());
    }
  }

  std::filesystem::path result;
  const auto match = entries.find(lowered_component);
  if (match != entries.end()) {
    result = match->second;
  }

  {
    std::lock_guard<std::mutex> lock(LooseAssetLookupMutex());
    auto [iterator, inserted] =
        LooseAssetDirectoryCaches().try_emplace(directory_key, std::move(entries));
    if (!inserted) {
      const auto cached_match = iterator->second.find(lowered_component);
      result = cached_match != iterator->second.end() ? cached_match->second
                                                      : std::filesystem::path{};
    }
  }

  return result;
}

std::filesystem::path ResolveCaseInsensitivePath(
    const std::filesystem::path& root,
    std::string_view relative_path);

bool TryResolveCachedLooseAsset(const std::filesystem::path& root,
                                std::string_view relative_path,
                                CachedLooseAsset* out_asset);

bool HasExtractedBankMirror(const std::filesystem::path& root,
                            std::string_view relative_path) {
  std::string normalized = NormalizeRelativeLookupKey(relative_path);
  if (!EndsWith(normalized, ".bnk")) {
    return false;
  }

  normalized.erase(normalized.size() - 4);
  std::error_code error;
  const std::filesystem::path direct_manifest_path =
      root / std::filesystem::path(normalized) / "_manifest.tsv";
  if (std::filesystem::is_regular_file(direct_manifest_path, error)) {
    return true;
  }
  error.clear();

  const std::filesystem::path metadata_manifest_path =
      root / "refii_vfs" / "banks" / std::filesystem::path(normalized) /
      "_manifest.tsv";
  if (std::filesystem::is_regular_file(metadata_manifest_path, error)) {
    return true;
  }

  CachedLooseAsset manifest;
  return TryResolveCachedLooseAsset(root, normalized + "/_manifest.tsv", &manifest) ||
         TryResolveCachedLooseAsset(
             root, "refii_vfs/banks/" + normalized + "/_manifest.tsv", &manifest);
}

std::filesystem::path ResolveCaseInsensitivePath(const std::filesystem::path& root,
                                                 std::string_view relative_path) {
  const std::string normalized_relative =
      NormalizeRelativePathForResolution(relative_path);
  std::error_code error;
  if (root.empty() || !std::filesystem::exists(root, error) ||
      !std::filesystem::is_directory(root, error)) {
    return {};
  }

  std::filesystem::path current = root;
  size_t position = 0;
  while (position < normalized_relative.size()) {
    const size_t next_separator = normalized_relative.find('/', position);
    const size_t count =
        next_separator == std::string::npos ? normalized_relative.size() - position
                                            : next_separator - position;
    const std::string_view component = std::string_view(normalized_relative).substr(
        position, count);
    position = next_separator == std::string::npos ? normalized_relative.size()
                                                   : next_separator + 1;

    if (component.empty() || component == ".") {
      continue;
    }

    const std::filesystem::path exact = current / std::string(component);
    if (std::filesystem::exists(exact, error)) {
      current = exact;
      continue;
    }

    const std::filesystem::path match =
        ResolveCaseInsensitiveChild(current, component);
    if (match.empty()) {
      return {};
    }

    current = match;
  }

  return std::filesystem::exists(current, error) ? current : std::filesystem::path{};
}

bool TryResolveCachedLooseAsset(const std::filesystem::path& root,
                                std::string_view relative_path,
                                CachedLooseAsset* out_asset) {
  const std::string root_key = root.lexically_normal().generic_string();
  const std::string lookup_key = NormalizeRelativeLookupKey(relative_path);
  if (root_key.empty() || lookup_key.empty()) {
    return false;
  }

  {
    std::lock_guard<std::mutex> lock(LooseAssetLookupMutex());
    const auto root_match = LooseAssetLookupCaches().find(root_key);
    if (root_match != LooseAssetLookupCaches().end()) {
      const auto match = root_match->second.find(lookup_key);
      if (match != root_match->second.end()) {
        if (match->second.found && out_asset != nullptr) {
          *out_asset = match->second;
        }
        return match->second.found;
      }
    }
  }

  CachedLooseAsset resolved;
  std::error_code error;
  const std::filesystem::path exact =
      root / std::filesystem::path(std::string(relative_path));
  if (std::filesystem::is_regular_file(exact, error)) {
    resolved.found = true;
    resolved.host_path = exact.lexically_normal();
    resolved.relative_path = std::string(relative_path);
  } else {
    const std::filesystem::path insensitive =
        ResolveCaseInsensitivePath(root, relative_path);
    if (!insensitive.empty() &&
        std::filesystem::is_regular_file(insensitive, error)) {
      resolved.found = true;
      resolved.host_path = insensitive.lexically_normal();
      resolved.relative_path = insensitive.lexically_relative(root).generic_string();
    }
  }

  {
    std::lock_guard<std::mutex> lock(LooseAssetLookupMutex());
    auto& root_cache = LooseAssetLookupCaches()[root_key];
    constexpr size_t kMaxLooseAssetLookupCacheEntries = 65536;
    if (root_cache.size() > kMaxLooseAssetLookupCacheEntries) {
      root_cache.clear();
    }
    root_cache.try_emplace(lookup_key, resolved);
  }

  if (resolved.found && out_asset != nullptr) {
    *out_asset = resolved;
  }
  return resolved.found;
}

std::filesystem::path GetDefaultGameRoot() {
  const auto* runtime = rex::Runtime::instance();
  if (runtime == nullptr) {
    return {};
  }

  std::filesystem::path root = runtime->game_data_root();
  std::string filename = root.filename().generic_string();
  std::ranges::transform(filename, filename.begin(), [](unsigned char ch) {
    return static_cast<char>(std::tolower(ch));
  });
  if (filename == "data") {
    root = root.parent_path();
  }
  filename = root.filename().generic_string();
  std::ranges::transform(filename, filename.begin(), [](unsigned char ch) {
    return static_cast<char>(std::tolower(ch));
  });
  if (filename == "game") {
    root = root.parent_path();
  }
  return root;
}

std::filesystem::path ResolveConfiguredAssetRoot(std::string_view configured_root,
                                                 const std::filesystem::path& game_root) {
  if (configured_root.empty()) {
    return {};
  }

  std::filesystem::path root{std::string(configured_root)};
  if (root.is_relative()) {
    if (game_root.empty()) {
      return {};
    }
    root = game_root / root;
  }
  return NormalizeLooseAssetPath(std::move(root));
}

void AddUniqueRoot(std::vector<std::filesystem::path>* roots,
                   std::filesystem::path root) {
  if (roots == nullptr || root.empty()) {
    return;
  }
  root = NormalizeLooseAssetPath(std::move(root));
  if (std::find(roots->begin(), roots->end(), root) == roots->end()) {
    roots->push_back(std::move(root));
  }
}

// GODOT OWNS THE MODS. The loose search roots used to begin with every enabled mod's folder,
// fed from `mods/enabled.txt` through `refii::modding`. Godot's mount now overlays mods itself —
// one fold, one resolved index, one report — and the launcher writes `loadorder.toml`, which
// nothing here ever read. What was left resolved a folder no longer consulted.
std::vector<std::filesystem::path> BuildLooseAssetSearchRoots() {
  std::vector<std::filesystem::path> roots;
  AddUniqueRoot(&roots, GetLooseAssetRoot());
  return roots;
}

}  // namespace

std::filesystem::path GetLooseAssetRootImpl() {
  std::filesystem::path root;
  const std::string configured_root = ConfiguredLooseAssetRoot();
  if (!configured_root.empty()) {
    root = ResolveConfiguredAssetRoot(configured_root, GetDefaultGameRoot());
  } else {
    const std::filesystem::path game_root = GetDefaultGameRoot();
    if (!game_root.empty()) {
      root = game_root / kUnpackedWorkspaceFolder;
    }
  }

  return NormalizeLooseAssetPath(std::move(root));
}

std::vector<std::filesystem::path> GetLooseAssetSearchRootsImpl() {
  // Rebuilding the root list reads several cvars, stats enabled.txt, and
  // allocates a handful of paths -- and resolve paths call this for every
  // lookup. Cache with a short TTL so launcher-side mod toggles and cvar
  // hot-reloads still land within half a second.
  constexpr auto kRootsTtl = std::chrono::milliseconds(500);
  static std::mutex mutex;
  static std::vector<std::filesystem::path> cached_roots;
  static std::chrono::steady_clock::time_point built_at{};
  static bool built = false;

  const auto now = std::chrono::steady_clock::now();
  std::lock_guard<std::mutex> lock(mutex);
  if (!built || now - built_at >= kRootsTtl) {
    cached_roots = BuildLooseAssetSearchRoots();
    built_at = now;
    built = true;
  }
  return cached_roots;
}

std::string BuildLooseAssetOpenPath(const std::filesystem::path& loose_path) {
  std::error_code error;
  const std::filesystem::path game_root =
      std::filesystem::weakly_canonical(GetDefaultGameRoot(), error);
  const std::filesystem::path canonical_loose_path =
      std::filesystem::weakly_canonical(loose_path, error);

  if (!game_root.empty() && !canonical_loose_path.empty()) {
    const std::filesystem::path relative =
        canonical_loose_path.lexically_relative(game_root);
    if (!relative.empty()) {
      const auto first = relative.begin();
      if (first == relative.end() || *first != "..") {
        std::string virtual_path = "game:\\" + relative.generic_string();
        std::replace(virtual_path.begin(), virtual_path.end(), '/', '\\');
        return virtual_path;
      }
    }
  }

  return loose_path.lexically_normal().generic_string();
}

std::string NormalizeLooseAssetKeyImpl(std::string_view asset_path) {
  std::string key(asset_path);
  std::replace(key.begin(), key.end(), '\\', '/');

  while (!key.empty() && key.back() == '\0') {
    key.pop_back();
  }

  std::ranges::transform(key, key.begin(), [](unsigned char ch) {
    return static_cast<char>(std::tolower(ch));
  });

  if (StartsWith(key, "game:/")) {
    key.erase(0, 6);
  } else if (StartsWith(key, "game:")) {
    key.erase(0, 5);
  }

  while (!key.empty() && key.front() == '/') {
    key.erase(key.begin());
  }

  return key;
}

std::filesystem::path ResolveLooseAssetOverridePath(std::string_view asset_path) {
  LooseAssetResolution resolution;
  if (ResolveLooseAssetOverride(asset_path, &resolution)) {
    return resolution.host_path;
  }
  return {};
}

bool ResolveLooseAssetOverrideImpl(std::string_view asset_path,
                               LooseAssetResolution* out_resolution) {
  const std::vector<std::filesystem::path> roots = GetLooseAssetSearchRoots();
  if (roots.empty()) {
    return false;
  }

  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  if (IsNativeLooseAssetExcludedCandidate(normalized)) {
    return false;
  }

  const std::vector<std::string> candidates = BuildLooseAssetCandidates(normalized);

  for (const std::filesystem::path& root : roots) {
    if (root.empty()) {
      continue;
    }
    for (const std::string& candidate : candidates) {
      if (IsNativeLooseAssetExcludedCandidate(candidate)) {
        continue;
      }
      // A CSV-authored game database beats the binary .gdb in the same root;
      // it is compiled on demand into the .cache area and served from there.
      if (GdbCsvOverridesEnabled() && EndsWith(candidate, ".gdb")) {
        CachedLooseAsset csv_asset;
        std::filesystem::path compiled;
        if (TryResolveCachedLooseAsset(root, candidate + ".csv", &csv_asset) &&
            TryCompileGdbCsvOverride(csv_asset.host_path, candidate,
                                     &compiled)) {
          if (out_resolution != nullptr) {
            out_resolution->host_path = compiled;
            out_resolution->normalized_key = normalized;
            out_resolution->provider_relative_path = candidate;
          }
          return true;
        }
      }
      // A CSV-authored localized-text database likewise beats the binary
      // .babel; it is compiled on demand and served from the cache.
      if (BabelCsvOverridesEnabled() && EndsWith(candidate, ".babel")) {
        CachedLooseAsset csv_asset;
        std::filesystem::path compiled;
        if (TryResolveCachedLooseAsset(root, candidate + ".csv", &csv_asset) &&
            TryCompileBabelCsvOverride(csv_asset.host_path, candidate,
                                       &compiled)) {
          if (out_resolution != nullptr) {
            out_resolution->host_path = compiled;
            out_resolution->normalized_key = normalized;
            out_resolution->provider_relative_path = candidate;
          }
          return true;
        }
      }
      CachedLooseAsset asset;
      if (!TryResolveCachedLooseAsset(root, candidate, &asset) ||
          HasExtractedBankMirror(root, asset.relative_path)) {
        continue;
      }

      if (out_resolution != nullptr) {
        out_resolution->host_path = asset.host_path;
        out_resolution->normalized_key = normalized;
        out_resolution->provider_relative_path = asset.relative_path;
      }
      return true;
    }
  }

  return false;
}

bool ShouldDeferLooseBankContainerImpl(std::string_view asset_path) {
  const std::vector<std::filesystem::path> roots = GetLooseAssetSearchRoots();
  if (roots.empty()) {
    return false;
  }

  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  if (!EndsWith(normalized, ".bnk")) {
    return false;
  }

  const std::vector<std::string> candidates = BuildLooseAssetCandidates(normalized);
  for (const std::filesystem::path& root : roots) {
    if (root.empty()) {
      continue;
    }
    for (const std::string& candidate : candidates) {
      if (HasExtractedBankMirror(root, candidate)) {
        return true;
      }
      CachedLooseAsset asset;
      if (TryResolveCachedLooseAsset(root, candidate, &asset) &&
          HasExtractedBankMirror(root, asset.relative_path)) {
        return true;
      }
    }
  }
  return false;
}


// --- The payload/host C ABI ----------------------------------------------------------
//
// These are the entries the inline wrappers in loose_asset_hooks.h call. The C++
// implementations above keep their shape for host callers; only the boundary is flattened.
// Strings are written into the CALLER's buffer and the needed length returned, so nothing
// allocated here is ever freed on the other side. See core/payload_bridge.h.

namespace {

size_t CopyOut(const std::string& value, char* out, size_t capacity) {
  if (out != nullptr && capacity > 0) {
    size_t take = value.size() < capacity ? value.size() : capacity;
    std::memcpy(out, value.data(), take);
  }
  return value.size();
}

}  // namespace

extern "C" size_t refii_bridge_vfs_loose_asset_root(char* out, size_t capacity) {
  return CopyOut(GetLooseAssetRootImpl().string(), out, capacity);
}

extern "C" size_t refii_bridge_vfs_normalize_loose_asset_key(const char* key, size_t length,
                                                             char* out, size_t capacity) {
  return CopyOut(NormalizeLooseAssetKeyImpl(std::string_view(key, length)), out, capacity);
}

extern "C" uint32_t refii_bridge_vfs_loose_asset_search_root_count(void) {
  return static_cast<uint32_t>(GetLooseAssetSearchRootsImpl().size());
}

extern "C" size_t refii_bridge_vfs_loose_asset_search_root(uint32_t index, char* out,
                                                            size_t capacity) {
  std::vector<std::filesystem::path> roots = GetLooseAssetSearchRootsImpl();
  if (index >= roots.size()) {
    return 0;
  }
  return CopyOut(roots[index].string(), out, capacity);
}

extern "C" int32_t refii_bridge_vfs_should_defer_loose_bank_container(const char* path,
                                                                      size_t length) {
  return ShouldDeferLooseBankContainerImpl(std::string_view(path, length)) ? 1 : 0;
}

extern "C" int32_t refii_bridge_vfs_resolve_loose_asset_override(
    const char* path, size_t length,
    char* host_path, size_t host_path_capacity, size_t* out_host_path_length,
    char* normalized_key, size_t normalized_key_capacity, size_t* out_normalized_key_length,
    char* provider_relative, size_t provider_relative_capacity,
    size_t* out_provider_relative_length) {
  LooseAssetResolution resolution;
  if (!ResolveLooseAssetOverrideImpl(std::string_view(path, length), &resolution)) {
    return 0;
  }

  size_t host_length = CopyOut(resolution.host_path.string(), host_path, host_path_capacity);
  size_t key_length =
      CopyOut(resolution.normalized_key, normalized_key, normalized_key_capacity);
  size_t relative_length = CopyOut(resolution.provider_relative_path, provider_relative,
                                   provider_relative_capacity);

  if (out_host_path_length != nullptr) {
    *out_host_path_length = host_length;
  }
  if (out_normalized_key_length != nullptr) {
    *out_normalized_key_length = key_length;
  }
  if (out_provider_relative_length != nullptr) {
    *out_provider_relative_length = relative_length;
  }
  return 1;
}

}  // namespace refii::vfs
