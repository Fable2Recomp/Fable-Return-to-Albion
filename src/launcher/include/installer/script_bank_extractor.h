#pragma once

#include <cstddef>
#include <filesystem>
#include <iosfwd>
#include <string>

namespace Installer::ScriptBankExtractor {

struct ExtractionSummary {
    std::size_t bankCount = 0;
    std::size_t extractedFileCount = 0;
    std::size_t copiedLooseFileCount = 0;
};

std::filesystem::path GetGameContentDirectoryPath(const std::filesystem::path& gameInstallRoot);
std::filesystem::path GetLuaWorkspacePath(const std::filesystem::path& gameInstallRoot);
std::filesystem::path GetLuaWorkspaceSourcePath(const std::filesystem::path& gameInstallRoot);

bool ExportLuaWorkspace(
    const std::filesystem::path& gameInstallRoot,
    ExtractionSummary* summary = nullptr,
    std::ostream* log = nullptr,
    std::string* error = nullptr);

}  // namespace Installer::ScriptBankExtractor
