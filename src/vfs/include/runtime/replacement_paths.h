#pragma once

#include "loose_asset_hooks.h"

#include <string>
#include <string_view>

namespace refii::vfs {

struct RefiiReplacementPathResolution {
  LooseAssetResolution resolution;
  std::string root_name;
  bool exists = false;
  bool is_directory = false;
};

bool ResolveRefiiReplacementPath(
    std::string_view asset_path,
    RefiiReplacementPathResolution* out_resolution);

}  // namespace refii::vfs
