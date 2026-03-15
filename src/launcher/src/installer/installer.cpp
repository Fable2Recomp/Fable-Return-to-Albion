#include "installer/installer.h"

#include "installer/xiso_extractor.h"
#include "inimanager.h"
#include "launcherpaths.h"
#include "platform.h"
#include "versionmanager.h"

#include <filesystem>
#include <ostream>
#include <thread>

namespace {

std::string GetPlatformAwareString(
    const std::string& key,
    const std::string& defaultValue,
    const std::string& section) {
    const std::string platformValue =
        INI::GetOptionalString(key + Platform::GetConfigSuffix(), section);
    if (!platformValue.empty()) {
        return platformValue;
    }

    const std::string genericValue = INI::GetOptionalString(key, section);
    if (!genericValue.empty()) {
        return genericValue;
    }

    if (defaultValue.empty()) {
        return "";
    }

    return INI::GetString(key, defaultValue, section);
}

std::string GetRepositoryName() {
    return INI::GetString("Name", "Fable2Recomp/refii", "Github");
}

std::string GetRepositoryBranch() {
    return INI::GetString("Branch", "master", "Github");
}

std::string GetGameDataDirectoryName() {
    return INI::GetString("GameDataDirectory", "game", "Game");
}

std::string GetUpdateDirectoryName() {
    return INI::GetString("UpdateDirectory", "update", "Game");
}

std::string GetGameMarkerRelativePath() {
    return INI::GetString("GameMarkerFile", "game/default.xexp", "Game");
}

}  // namespace

namespace Installer {

std::string EnsureTrailingSeparator(std::string path) {
    if (!path.empty() && path.back() != '\\' && path.back() != '/') {
        path += "/";
    }
    return path;
}

std::string GetGameTitle() {
    return INI::GetString("GameTitle", "Fable 2", "About");
}

std::string GetExecutableName() {
    return GetPlatformAwareString("ExeName", Platform::GetDefaultExecutableName(), "Game");
}

std::string GetConfiguredExecutableRelativePath() {
    return GetPlatformAwareString("ExeRelativePath", "", "Github");
}

std::string GetDownloadUrl() {
    const std::string configuredUrl = GetPlatformAwareString("DownloadFile", "", "Github");
    if (!configuredUrl.empty()) {
        return configuredUrl;
    }

    const std::string executableRelativePath = GetConfiguredExecutableRelativePath();
    if (executableRelativePath.empty()) {
        return "";
    }

    return "https://github.com/" + GetRepositoryName() + "/raw/" +
           GetRepositoryBranch() + "/" + executableRelativePath;
}

bool HasDownloadSource() {
    return !GetDownloadUrl().empty();
}

bool CanCheckForUpdates() {
    return !GetConfiguredExecutableRelativePath().empty();
}

std::filesystem::path GetInstallBasePath() {
    return LauncherPaths::ResolveFromLauncherDirectory(
        INI::GetString("GamePath", "assets/", "Game"));
}

std::filesystem::path GetInstalledExecutablePath() {
    return Platform::GetExecutableDirectory() / GetExecutableName();
}

std::filesystem::path GetGameDataDirectoryPath() {
    return GetInstallBasePath() / GetGameDataDirectoryName();
}

std::filesystem::path GetUpdateDirectoryPath() {
    return GetInstallBasePath() / GetUpdateDirectoryName();
}

std::filesystem::path GetGameMarkerPath() {
    return GetInstallBasePath() / GetGameMarkerRelativePath();
}

bool HasExtractedAssets(
    const std::filesystem::path& gameDataDirectory,
    const std::filesystem::path& markerPath) {
    return std::filesystem::exists(gameDataDirectory) && std::filesystem::exists(markerPath);
}

bool UninstallManagedFiles(std::ostream& log) {
    const std::filesystem::path gameDataPath = GetGameDataDirectoryPath();
    const std::filesystem::path updateDataPath = GetUpdateDirectoryPath();
    bool hasRemovedFiles = false;

    if (std::filesystem::exists(gameDataPath)) {
        try {
            std::filesystem::remove_all(gameDataPath);
            hasRemovedFiles = true;
            log << "Removed game data directory: " << gameDataPath.string() << std::endl;
        } catch (const std::exception& exception) {
            log << "Error removing game data directory " << gameDataPath.string()
                << ": " << exception.what() << std::endl;
        }
    }

    if (std::filesystem::exists(updateDataPath)) {
        try {
            std::filesystem::remove_all(updateDataPath);
            hasRemovedFiles = true;
            log << "Removed update data directory: " << updateDataPath.string() << std::endl;
        } catch (const std::exception& exception) {
            log << "Error removing update data directory " << updateDataPath.string()
                << ": " << exception.what() << std::endl;
        }
    }

    return hasRemovedFiles;
}

std::string OpenIsoFileDialog() {
    std::string result;
    std::string ignoredError;
    Platform::SelectIsoFile(result, &ignoredError);
    return result;
}

std::string OpenXexpFileDialog() {
    std::string result;
    std::string ignoredError;
    Platform::SelectXexpFile(result, &ignoredError);
    return result;
}

bool ExtractIso(
    const std::filesystem::path& isoPath,
    const std::filesystem::path& destinationDirectory,
    std::string* error) {
    try {
        if (std::filesystem::exists(destinationDirectory)) {
            std::filesystem::remove_all(destinationDirectory);
        }
        std::filesystem::create_directories(destinationDirectory);
    } catch (const std::exception& exception) {
        if (error) {
            *error = exception.what();
        }
        return false;
    }

    return XisoExtractor::ExtractImage(isoPath, destinationDirectory, error);
}

bool CopyGameExecutable(
    const std::filesystem::path& sourcePath,
    const std::filesystem::path& destinationPath,
    std::string* error) {
    try {
        if (!std::filesystem::exists(sourcePath)) {
            if (error) {
                *error = "The selected XEXP file does not exist.";
            }
            return false;
        }

        std::filesystem::create_directories(destinationPath.parent_path());
        std::filesystem::copy_file(
            sourcePath,
            destinationPath,
            std::filesystem::copy_options::overwrite_existing);
        return true;
    } catch (const std::exception& exception) {
        if (error) {
            *error = exception.what();
        }
        return false;
    }
}

bool InstallGame(
    const std::filesystem::path& isoPath,
    const std::filesystem::path& xexpPath,
    std::string* error) {
    if (!ExtractIso(isoPath, GetGameDataDirectoryPath(), error)) {
        return false;
    }

    return CopyGameExecutable(xexpPath, GetGameMarkerPath(), error);
}

void ExtractIsoAsync(
    const std::string& isoPath,
    std::shared_ptr<IsoExtractionProgress> progress) {
    std::thread([isoPath, progress]() {
        progress->isExtracting = true;
        progress->isComplete = false;
        progress->hasError = false;
        progress->errorMessage.clear();

        std::string extractionError;
        const bool success = ExtractIso(isoPath, GetGameDataDirectoryPath(), &extractionError);

        progress->isExtracting = false;
        if (success) {
            progress->isComplete = true;
        } else {
            progress->hasError = true;
            progress->errorMessage = extractionError.empty() ? "ISO extraction failed" : extractionError;
        }
    }).detach();
}

void InstallGameAsync(
    const std::string& isoPath,
    const std::string& xexpPath,
    std::shared_ptr<IsoExtractionProgress> progress) {
    std::thread([isoPath, xexpPath, progress]() {
        progress->isExtracting = true;
        progress->isComplete = false;
        progress->hasError = false;
        progress->errorMessage.clear();

        std::string installError;
        const bool success = InstallGame(isoPath, xexpPath, &installError);

        progress->isExtracting = false;
        if (success) {
            progress->isComplete = true;
        } else {
            progress->hasError = true;
            progress->errorMessage = installError.empty() ? "Game installation failed" : installError;
        }
    }).detach();
}

}  // namespace Installer
