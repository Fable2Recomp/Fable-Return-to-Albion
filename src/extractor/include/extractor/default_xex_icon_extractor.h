#pragma once

#include <cstddef>
#include <filesystem>
#include <string>

namespace refii::extractor {

struct DefaultXexIconExtractionSummary {
    std::filesystem::path output_directory;
    std::size_t extracted_icons = 0;
};

bool ExtractDefaultXexIcons(
    const std::filesystem::path& xex_path,
    DefaultXexIconExtractionSummary* summary = nullptr,
    std::string* error = nullptr);

} // namespace refii::extractor
