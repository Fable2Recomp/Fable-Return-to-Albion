#include "runtime/replacement_paths.h"

#include <algorithm>
#include <cstring>
#include <cctype>
#include <filesystem>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include <rex/runtime.h>

namespace refii::vfs {
namespace {

bool StartsWith(std::string_view value, std::string_view prefix) {
  return value.size() >= prefix.size() && value.substr(0, prefix.size()) == prefix;
}

std::filesystem::path NormalizeHostPath(std::filesystem::path path) {
  return path.empty() ? std::filesystem::path{} : path.lexically_normal();
}

std::filesystem::path GetDefaultGameRootForReplacement() {
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

std::filesystem::path RuntimeRootOrDefault(
    const std::filesystem::path& runtime_root,
    std::string_view fallback_child) {
  if (!runtime_root.empty()) {
    return NormalizeHostPath(std::filesystem::absolute(runtime_root));
  }
  const std::filesystem::path game_root = GetDefaultGameRootForReplacement();
  return game_root.empty()
             ? std::filesystem::path{}
             : NormalizeHostPath(game_root / std::string(fallback_child));
}

std::string NormalizeReplacementPathText(std::string_view asset_path) {
  std::string key(asset_path);
  std::replace(key.begin(), key.end(), '\\', '/');
  while (!key.empty() && key.back() == '\0') {
    key.pop_back();
  }
  while (!key.empty() && StartsWith(key, "./")) {
    key.erase(0, 2);
  }
  std::ranges::transform(key, key.begin(), [](unsigned char ch) {
    return static_cast<char>(std::tolower(ch));
  });
  while (StartsWith(key, "//device/")) {
    key.erase(key.begin());
  }
  return key;
}

bool IsSafeReplacementRelativePath(std::string_view relative) {
  if (relative.empty()) {
    return true;
  }
  if (relative.find(':') != std::string_view::npos) {
    return false;
  }
  std::filesystem::path path{std::string(relative)};
  if (path.is_absolute()) {
    return false;
  }
  path = path.lexically_normal();
  for (const std::filesystem::path& component : path) {
    if (component == "..") {
      return false;
    }
  }
  return true;
}

std::filesystem::path NormalizeReplacementRelativePath(
    std::string_view relative) {
  std::string cleaned(relative);
  while (!cleaned.empty() && cleaned.front() == '/') {
    cleaned.erase(cleaned.begin());
  }
  while (!cleaned.empty() && StartsWith(cleaned, "./")) {
    cleaned.erase(0, 2);
  }
  return std::filesystem::path(cleaned).lexically_normal();
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

  std::error_code error;
  if (!std::filesystem::is_directory(directory, error)) {
    return {};
  }
  for (std::filesystem::directory_iterator it(directory, error), end;
       it != end; it.increment(error)) {
    if (error) {
      error.clear();
      continue;
    }
    std::string filename = it->path().filename().generic_string();
    std::ranges::transform(filename, filename.begin(), [](unsigned char ch) {
      return static_cast<char>(std::tolower(ch));
    });
    if (filename == lowered_component) {
      return it->path();
    }
  }
  return {};
}

std::filesystem::path ResolveCaseInsensitiveExistingPath(
    const std::filesystem::path& root,
    std::string_view relative_path) {
  if (root.empty()) {
    return {};
  }

  std::string normalized(relative_path);
  std::replace(normalized.begin(), normalized.end(), '\\', '/');
  std::filesystem::path current = root;
  size_t position = 0;
  while (position < normalized.size()) {
    const size_t next_separator = normalized.find('/', position);
    const size_t count =
        next_separator == std::string::npos ? normalized.size() - position
                                            : next_separator - position;
    const std::string_view component =
        std::string_view(normalized).substr(position, count);
    position = next_separator == std::string::npos ? normalized.size()
                                                   : next_separator + 1;
    if (component.empty() || component == ".") {
      continue;
    }

    std::error_code error;
    const std::filesystem::path exact = current / std::string(component);
    if (std::filesystem::exists(exact, error)) {
      current = exact;
      continue;
    }
    const std::filesystem::path insensitive =
        ResolveCaseInsensitiveChild(current, component);
    if (insensitive.empty()) {
      return {};
    }
    current = insensitive;
  }

  std::error_code error;
  return std::filesystem::exists(current, error) ? current : std::filesystem::path{};
}

bool SelectReplacementRoot(std::string_view asset_path,
                           std::filesystem::path* out_root,
                           std::string* out_relative,
                           std::string* out_root_name) {
  if (out_root == nullptr || out_relative == nullptr ||
      out_root_name == nullptr) {
    return false;
  }

  std::string relative = NormalizeReplacementPathText(asset_path);
  if (relative.empty()) {
    return false;
  }

  std::string root_name;
  constexpr std::string_view kPartition1 =
      "/device/harddisk0/partition1/";
  constexpr std::string_view kPartition1NoSlash =
      "/device/harddisk0/partition1";
  if (StartsWith(relative, kPartition1)) {
    root_name = "game";
    relative.erase(0, kPartition1.size());
  } else if (relative == kPartition1NoSlash) {
    root_name = "game";
    relative.clear();
  } else {
    const size_t colon = relative.find(':');
    if (colon != std::string::npos) {
      root_name = relative.substr(0, colon);
      relative.erase(0, colon + 1);
      while (!relative.empty() && relative.front() == '/') {
        relative.erase(relative.begin());
      }
    } else if (relative.front() == '/') {
      return false;
    } else {
      root_name = "game";
    }
  }

  std::filesystem::path root;
  if (root_name == "game" || root_name == "d") {
    root_name = "game";
    root = GetLooseAssetRoot();
  } else {
    const auto* runtime = rex::Runtime::instance();
    if (root_name == "update") {
      root = RuntimeRootOrDefault(
          runtime != nullptr ? runtime->update_data_root() : std::filesystem::path{},
          "update");
    } else if (root_name == "cache") {
      root = RuntimeRootOrDefault(
          runtime != nullptr ? runtime->cache_root() : std::filesystem::path{},
          "cache");
    } else if (root_name == "user") {
      root = RuntimeRootOrDefault(
          runtime != nullptr ? runtime->user_data_root() : std::filesystem::path{},
          "user");
    } else {
      return false;
    }
  }

  if (root.empty() || !IsSafeReplacementRelativePath(relative)) {
    return false;
  }
  *out_root = NormalizeHostPath(std::move(root));
  *out_relative = std::move(relative);
  *out_root_name = std::move(root_name);
  return true;
}

}  // namespace

bool ResolveRefiiReplacementPathImpl(
    std::string_view asset_path,
    RefiiReplacementPathResolution* out_resolution) {
  if (out_resolution == nullptr) {
    return false;
  }

  *out_resolution = {};

  std::filesystem::path root;
  std::string relative;
  std::string root_name;
  if (!SelectReplacementRoot(asset_path, &root, &relative, &root_name) ||
      root.empty()) {
    return false;
  }

  if (root_name == "game") {
    LooseAssetResolution loose_resolution;
    if (ResolveLooseAssetOverride(asset_path, &loose_resolution)) {
      out_resolution->resolution = std::move(loose_resolution);
      out_resolution->root_name = std::move(root_name);
      out_resolution->exists = true;
      return true;
    }
  }

  std::vector<std::filesystem::path> relative_candidates;
  relative_candidates.push_back(NormalizeReplacementRelativePath(relative));
  if (root_name == "game" && StartsWith(relative, "data/")) {
    relative_candidates.push_back(
        NormalizeReplacementRelativePath(std::string_view(relative).substr(5)));
  }

  std::filesystem::path selected_relative =
      relative_candidates.empty() ? std::filesystem::path{}
                                  : relative_candidates.front();
  std::filesystem::path selected_path = root / selected_relative;
  std::error_code error;
  bool selected_exists = std::filesystem::exists(selected_path, error);
  if (error) {
    error.clear();
    selected_exists = false;
  }

  for (const std::filesystem::path& candidate_relative : relative_candidates) {
    const std::filesystem::path exact = root / candidate_relative;
    const bool exact_exists = std::filesystem::exists(exact, error);
    if (error) {
      error.clear();
    }
    if (exact_exists) {
      selected_relative = candidate_relative;
      selected_path = exact.lexically_normal();
      selected_exists = true;
      break;
    }
    const std::filesystem::path insensitive =
        ResolveCaseInsensitiveExistingPath(root, candidate_relative.generic_string());
    if (!insensitive.empty()) {
      selected_relative = insensitive.lexically_relative(root);
      selected_path = insensitive.lexically_normal();
      selected_exists = true;
      break;
    }
  }
  if (!selected_exists && root_name == "game" && relative_candidates.size() > 1) {
    selected_relative = relative_candidates.back();
    selected_path = root / selected_relative;
  }

  out_resolution->resolution.host_path = selected_path.lexically_normal();
  out_resolution->resolution.normalized_key = NormalizeLooseAssetKey(asset_path);
  out_resolution->resolution.provider_relative_path =
      selected_relative.generic_string();
  out_resolution->root_name = std::move(root_name);
  out_resolution->exists = selected_exists;
  if (selected_exists) {
    out_resolution->is_directory =
        std::filesystem::is_directory(out_resolution->resolution.host_path, error);
    if (error) {
      out_resolution->is_directory = false;
    }
  }
  return true;
}


// --- The payload/host C ABI ----------------------------------------------------------
//
// RefiiReplacementPathResolution flattened into its four strings and two flags. See
// core/payload_bridge.h: strings go into the caller's buffer, nothing crosses owned.

namespace {

size_t ReplacementCopyOut(const std::string& value, char* out, size_t capacity) {
  if (out != nullptr && capacity > 0) {
    size_t take = value.size() < capacity ? value.size() : capacity;
    std::memcpy(out, value.data(), take);
  }
  return value.size();
}

}  // namespace

extern "C" int32_t refii_bridge_vfs_resolve_refii_replacement_path(
    const char* path, size_t length,
    char* host_path, size_t host_path_capacity, size_t* out_host_path_length,
    char* normalized_key, size_t normalized_key_capacity, size_t* out_normalized_key_length,
    char* provider_relative, size_t provider_relative_capacity,
    size_t* out_provider_relative_length,
    char* root_name, size_t root_name_capacity, size_t* out_root_name_length,
    int32_t* out_exists, int32_t* out_is_directory) {
  RefiiReplacementPathResolution resolution;
  if (!ResolveRefiiReplacementPathImpl(std::string_view(path, length), &resolution)) {
    return 0;
  }

  size_t host_length =
      ReplacementCopyOut(resolution.resolution.host_path.string(), host_path,
                         host_path_capacity);
  size_t key_length = ReplacementCopyOut(resolution.resolution.normalized_key,
                                         normalized_key, normalized_key_capacity);
  size_t relative_length =
      ReplacementCopyOut(resolution.resolution.provider_relative_path, provider_relative,
                         provider_relative_capacity);
  size_t root_length =
      ReplacementCopyOut(resolution.root_name, root_name, root_name_capacity);

  if (out_host_path_length != nullptr) { *out_host_path_length = host_length; }
  if (out_normalized_key_length != nullptr) { *out_normalized_key_length = key_length; }
  if (out_provider_relative_length != nullptr) {
    *out_provider_relative_length = relative_length;
  }
  if (out_root_name_length != nullptr) { *out_root_name_length = root_length; }
  if (out_exists != nullptr) { *out_exists = resolution.exists ? 1 : 0; }
  if (out_is_directory != nullptr) { *out_is_directory = resolution.is_directory ? 1 : 0; }
  return 1;
}

}  // namespace refii::vfs
