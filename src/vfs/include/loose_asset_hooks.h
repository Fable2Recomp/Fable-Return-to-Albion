#pragma once

#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

namespace refii::vfs {

enum class LooseAssetBackend {
  kRefii,
  kNative,
};

bool LooseAssetOverridesEnabled();
bool LooseAssetOverrideTracingEnabled();
bool VfsTracingEnabled();
bool VfsVerboseIoTracingEnabled();
bool LooseGameFaceBinaryOverridesEnabled();
bool LooseBankEntryOverridesEnabled();
bool SkipStartupVideosEnabled();
int StartupVideoMinimumPlaybackMs();
LooseAssetBackend GetLooseAssetBackend();
bool RefiiLooseAssetBackendEnabled();

struct LooseAssetResolution {
  std::filesystem::path host_path;
  std::string normalized_key;
  std::string provider_relative_path;
};

std::filesystem::path GetLooseAssetRoot();
std::vector<std::filesystem::path> GetLooseAssetSearchRoots();
bool ResolveLooseAssetOverride(std::string_view asset_path,
                               LooseAssetResolution* out_resolution);
bool ShouldDeferLooseBankContainer(std::string_view asset_path);
std::filesystem::path ResolveLooseAssetOverridePath(std::string_view asset_path);
std::string BuildLooseAssetOpenPath(const std::filesystem::path& loose_path);
std::string NormalizeLooseAssetKey(std::string_view asset_path);

void TouchLooseAssetHooks();

}  // namespace refii::vfs
