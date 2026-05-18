#pragma once

#include <string>

namespace refii::vfs {
inline namespace detail {

std::string ConfiguredLooseAssetRoot();
std::string ConfiguredModAssetRoots();

}  // namespace detail
}  // namespace refii::vfs
