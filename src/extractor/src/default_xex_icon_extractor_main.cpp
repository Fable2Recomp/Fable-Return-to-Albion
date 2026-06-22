#include "extractor/default_xex_icon_extractor.h"

#include <filesystem>
#include <iostream>
#include <string>

namespace {

void PrintUsage(const char* executable_name) {
    std::cerr << "Usage: " << executable_name << " <default.xex>\n";
}

} // namespace

int main(int argc, char** argv) {
    if (argc != 2) {
        PrintUsage(argv[0]);
        return 2;
    }

    refii::extractor::DefaultXexIconExtractionSummary summary;
    std::string error;
    if (!refii::extractor::ExtractDefaultXexIcons(
            std::filesystem::path(argv[1]), &summary, &error)) {
        std::cerr << "default.xex icon extraction failed: " << error << '\n';
        return 1;
    }

    std::cout << "Extracted " << summary.extracted_icons << " icon(s) into "
              << summary.output_directory << '\n';
    return 0;
}
