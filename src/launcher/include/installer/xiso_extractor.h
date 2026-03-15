#pragma once

#include <filesystem>
#include <string>

namespace Installer::XisoExtractor {

bool ExtractImage(
    const std::filesystem::path& isoPath,
    const std::filesystem::path& destinationDirectory,
    std::string* error = nullptr);

}  // namespace Installer::XisoExtractor
