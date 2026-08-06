#pragma once

#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

namespace refii::modding {

constexpr std::string_view kDefaultModsFolderName = "mods";
constexpr std::string_view kEnabledModsFilename = "enabled.txt";
constexpr std::string_view kModFilesFolderName = "files";
constexpr std::string_view kModMetadataFilename = "mod.toml";

struct ModMetadata {
  std::string title;
  std::string version;
  std::string author;
  std::string description;
};

struct ModAssetSummary {
  std::size_t file_count = 0;
  bool has_bank_manifests = false;
  // Game-relative lowercase forward-slash keys, used for conflict detection.
  std::vector<std::string> normalized_paths;
};

struct ModInstall {
  std::string name;
  std::filesystem::path root;
  bool has_files_folder = false;
  ModMetadata metadata;
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
// The folder the VFS layers into the search roots: files/ when present,
// otherwise the mod root itself.
std::filesystem::path GetModAssetRoot(const ModInstall& mod);
ModMetadata ReadModMetadata(const std::filesystem::path& mod_root);
ModAssetSummary SummarizeModAssets(const std::filesystem::path& asset_root,
                                   std::size_t max_files = 200000);

}  // namespace refii::modding
