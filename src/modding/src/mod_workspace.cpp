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
        std::filesystem::is_directory(files_root, error)});
  }

  std::sort(mods.begin(), mods.end(), [](const ModInstall& left,
                                         const ModInstall& right) {
    return left.name < right.name;
  });
  return mods;
}

}  // namespace refii::modding
