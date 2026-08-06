#pragma once

#include <string>

namespace refii::vfs {
inline namespace detail {

std::string ConfiguredLooseAssetRoot();
std::string ConfiguredModAssetRoots();
std::string ConfiguredModsRootSetting();
std::string ConfiguredEnabledModsSetting();

}  // namespace detail
}  // namespace refii::vfs
