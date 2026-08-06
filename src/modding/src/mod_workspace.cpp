#include "modding/mod_workspace.h"

#include <algorithm>
#include <cctype>
#include <exception>
#include <fstream>

namespace refii::modding {
namespace {

std::string TrimAsciiWhitespace(std::string_view value) {
  while (!value.empty() &&
         std::isspace(static_cast<unsigned char>(value.front())) != 0) {
    value.remove_prefix(1);
  }
  while (!value.empty() &&
         std::isspace(static_cast<unsigned char>(value.back())) != 0) {
    value.remove_suffix(1);
  }
  return std::string(value);
}

void AddEnabledModName(std::vector<std::string>* names,
                       std::string_view value) {
  if (names == nullptr) {
    return;
  }

  std::string trimmed = TrimAsciiWhitespace(value);
  if (trimmed.empty() || trimmed.front() == '#' || trimmed.front() == ';') {
    return;
  }

  const std::size_t inline_comment = trimmed.find('#');
  if (inline_comment != std::string::npos) {
    trimmed = TrimAsciiWhitespace(
        std::string_view(trimmed).substr(0, inline_comment));
  }
  if (trimmed.empty() ||
      !IsSafeModFolderName(std::filesystem::path(trimmed))) {
    return;
  }

  if (std::find(names->begin(), names->end(), trimmed) == names->end()) {
    names->push_back(std::move(trimmed));
  }
}

}  // namespace

std::filesystem::path GetDefaultModsRoot(
    const std::filesystem::path& assets_root) {
  if (assets_root.empty()) {
    return {};
  }
  return (assets_root / std::string(kDefaultModsFolderName)).lexically_normal();
}

bool IsSafeModFolderName(const std::filesystem::path& name) {
  if (name.empty() || name.is_absolute()) {
    return false;
  }
  for (const std::filesystem::path& component : name) {
    const std::string value = component.generic_string();
    if (value.empty() || value == "." || value == "..") {
      return false;
    }
  }
  return true;
}

std::vector<std::string> ReadEnabledModNames(
    const std::filesystem::path& mods_root) {
  std::vector<std::string> names;
  if (mods_root.empty()) {
    return names;
  }

  std::ifstream stream(mods_root / std::string(kEnabledModsFilename));
  if (!stream) {
    return names;
  }

  std::string line;
  while (std::getline(stream, line)) {
    AddEnabledModName(&names, line);
  }
  return names;
}

bool WriteEnabledModNames(const std::filesystem::path& mods_root,
                          const std::vector<std::string>& names,
                          std::string* error) {
  try {
    std::filesystem::create_directories(mods_root);
    std::ofstream stream(mods_root / std::string(kEnabledModsFilename),
                         std::ios::binary | std::ios::trunc);
    if (!stream) {
      if (error != nullptr) {
        *error = "failed to write enabled mod list";
      }
      return false;
    }

    for (const std::string& name : names) {
      if (IsSafeModFolderName(std::filesystem::path(name))) {
        stream << name << '\n';
      }
    }
    return stream.good();
  } catch (const std::exception& exception) {
    if (error != nullptr) {
      *error = exception.what();
    }
    return false;
  }
}

std::vector<ModInstall> DiscoverModInstalls(
    const std::filesystem::path& mods_root) {
  std::vector<ModInstall> mods;
  if (mods_root.empty()) {
    return mods;
  }

  std::error_code error;
  if (!std::filesystem::is_directory(mods_root, error)) {
    return mods;
  }

  for (const std::filesystem::directory_entry& entry :
       std::filesystem::directory_iterator(mods_root, error)) {
    if (error) {
      break;
    }
    if (!entry.is_directory(error)) {
      continue;
    }

    const std::string name = entry.path().filename().generic_string();
    if (!IsSafeModFolderName(std::filesystem::path(name))) {
      continue;
    }

    const std::filesystem::path files_root =
        entry.path() / std::string(kModFilesFolderName);
    mods.push_back(ModInstall{
        name,
        entry.path().lexically_normal(),
        std::filesystem::is_directory(files_root, error),
        ReadModMetadata(entry.path())});
  }

  std::sort(mods.begin(), mods.end(), [](const ModInstall& left,
                                         const ModInstall& right) {
    return left.name < right.name;
  });
  return mods;
}

std::filesystem::path GetModAssetRoot(const ModInstall& mod) {
  if (mod.has_files_folder) {
    return mod.root / std::string(kModFilesFolderName);
  }
  return mod.root;
}

ModMetadata ReadModMetadata(const std::filesystem::path& mod_root) {
  ModMetadata metadata;
  if (mod_root.empty()) {
    return metadata;
  }

  std::ifstream stream(mod_root / std::string(kModMetadataFilename));
  if (!stream) {
    return metadata;
  }

  // Minimal key = "value" parser: top-level assignments only, sections and
  // comments are skipped. Enough for mod.toml identity fields without a
  // full TOML dependency in this small policy library.
  std::string line;
  while (std::getline(stream, line)) {
    std::string trimmed = TrimAsciiWhitespace(line);
    if (trimmed.empty() || trimmed.front() == '#' || trimmed.front() == '[') {
      continue;
    }
    const std::size_t equals = trimmed.find('=');
    if (equals == std::string::npos) {
      continue;
    }
    std::string key = TrimAsciiWhitespace(
        std::string_view(trimmed).substr(0, equals));
    std::string value = TrimAsciiWhitespace(
        std::string_view(trimmed).substr(equals + 1));
    if (value.size() >= 2 &&
        ((value.front() == '"' && value.back() == '"') ||
         (value.front() == '\'' && value.back() == '\''))) {
      value = value.substr(1, value.size() - 2);
    }
    std::transform(key.begin(), key.end(), key.begin(), [](unsigned char ch) {
      return static_cast<char>(std::tolower(ch));
    });
    if (key == "name" || key == "title") {
      metadata.title = value;
    } else if (key == "version") {
      metadata.version = value;
    } else if (key == "author") {
      metadata.author = value;
    } else if (key == "description") {
      metadata.description = value;
    }
  }
  return metadata;
}

ModAssetSummary SummarizeModAssets(const std::filesystem::path& asset_root,
                                   std::size_t max_files) {
  ModAssetSummary summary;
  if (asset_root.empty()) {
    return summary;
  }

  std::error_code error;
  if (!std::filesystem::is_directory(asset_root, error)) {
    return summary;
  }

  constexpr std::string_view kBankManifestFilename = "_manifest.tsv";
  std::error_code iterator_error;
  for (std::filesystem::recursive_directory_iterator
           it(asset_root,
              std::filesystem::directory_options::skip_permission_denied,
              iterator_error),
       end;
       it != end; it.increment(iterator_error)) {
    if (iterator_error) {
      iterator_error.clear();
      continue;
    }
    std::error_code status_error;
    if (!it->is_regular_file(status_error)) {
      continue;
    }

    const std::filesystem::path relative =
        it->path().lexically_relative(asset_root);
    std::string key = relative.generic_string();
    std::transform(key.begin(), key.end(), key.begin(), [](unsigned char ch) {
      return static_cast<char>(std::tolower(ch));
    });
    if (key == std::string(kModMetadataFilename)) {
      continue;
    }
    if (key.size() >= kBankManifestFilename.size() &&
        key.compare(key.size() - kBankManifestFilename.size(),
                    kBankManifestFilename.size(),
                    kBankManifestFilename) == 0) {
      summary.has_bank_manifests = true;
    }

    ++summary.file_count;
    if (summary.normalized_paths.size() < max_files) {
      summary.normalized_paths.push_back(std::move(key));
    }
  }
  return summary;
}

}  // namespace refii::modding
