#pragma once

#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

namespace refii::modding {

constexpr std::string_view kDefaultModsFolderName = "mods";
constexpr std::string_view kEnabledModsFilename = "enabled.txt";
constexpr std::string_view kModFilesFolderName = "files";

struct ModInstall {
  std::string name;
  std::filesystem::path root;
  bool has_files_folder = false;
};

std::filesystem::path GetDefaultModsRoot(
    const std::filesystem::path& assets_root);
bool IsSafeModFolderName(const std::filesystem::path& name);
std::vector<std::string> ReadEnabledModNames(
    const std::filesystem::path& mods_root);
bool WriteEnabledModNames(const std::filesystem::path& mods_root,
                          const std::vector<std::string>& names,
                          std::string* error = nullptr);
std::vector<ModInstall> DiscoverModInstalls(
    const std::filesystem::path& mods_root);

}  // namespace refii::modding
