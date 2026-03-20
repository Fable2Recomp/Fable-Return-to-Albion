#include "installer/installer.h"

#include "installer/xiso_extractor.h"
#include "inimanager.h"
#include "launcherpaths.h"
#include "platform.h"
#include "versionmanager.h"

#include <algorithm>
#include <cctype>
#include <filesystem>
#include <fstream>
#include <ostream>
#include <sstream>
#include <thread>
#include <vector>

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

std::string Trim(std::string value) {
    const auto isWhitespace = [](unsigned char character) {
        return std::isspace(character) != 0;
    };

    value.erase(
        value.begin(),
        std::find_if(value.begin(), value.end(), [&](unsigned char character) {
            return !isWhitespace(character);
        }));
    value.erase(
        std::find_if(value.rbegin(), value.rend(), [&](unsigned char character) {
            return !isWhitespace(character);
        }).base(),
        value.end());
    return value;
}

std::vector<std::string> SplitList(const std::string& value, char delimiter) {
    std::vector<std::string> entries;
    std::stringstream stream(value);
    std::string entry;
    while (std::getline(stream, entry, delimiter)) {
        entry = Trim(entry);
        if (!entry.empty()) {
            entries.push_back(entry);
        }
    }
    return entries;
}

std::string GetManagedFilesConfigValue() {
    const std::string configuredValue =
        GetPlatformAwareString("ManagedFiles", "", "Github");
    if (!configuredValue.empty()) {
        return configuredValue;
    }

    return GetPlatformAwareString("ExeRelativePath", "", "Github");
}

ManagedDownloadFile ParseManagedDownloadFile(const std::string& entry) {
    ManagedDownloadFile file;

    const std::size_t separatorPos = entry.find('|');
    if (separatorPos == std::string::npos) {
        file.repositoryRelativePath = Trim(entry);
        file.localRelativePath =
            std::filesystem::path(file.repositoryRelativePath).filename();
    } else {
        file.repositoryRelativePath = Trim(entry.substr(0, separatorPos));
        file.localRelativePath = Trim(entry.substr(separatorPos + 1));
    }

    file.versionKey = file.localRelativePath.generic_string();
    return file;
}

std::string BuildRawDownloadUrl(const std::string& repositoryRelativePath) {
    return "https://github.com/" + GetRepositoryName() + "/raw/" +
           GetRepositoryBranch() + "/" + repositoryRelativePath;
}

bool ReplaceDownloadedFile(
    const std::filesystem::path& temporaryPath,
    const std::filesystem::path& destinationPath,
    std::string* error) {
    try {
        std::filesystem::create_directories(destinationPath.parent_path());

        if (std::filesystem::exists(destinationPath)) {
            std::filesystem::remove(destinationPath);
        }

        std::filesystem::rename(temporaryPath, destinationPath);
        return true;
    } catch (const std::exception&) {
        try {
            std::filesystem::copy_file(
                temporaryPath,
                destinationPath,
                std::filesystem::copy_options::overwrite_existing);
            std::filesystem::remove(temporaryPath);
            return true;
        } catch (const std::exception& exception) {
            if (error) {
                *error = exception.what();
            }
            return false;
        }
    }
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

    return BuildRawDownloadUrl(executableRelativePath);
}

std::string GetDownloadUrlForRelativePath(const std::string& repositoryRelativePath) {
    if (repositoryRelativePath.empty()) {
        return "";
    }

    return BuildRawDownloadUrl(repositoryRelativePath);
}

std::vector<ManagedDownloadFile> GetManagedDownloadFiles() {
    const std::string managedFilesValue = GetManagedFilesConfigValue();
    if (managedFilesValue.empty()) {
        return {};
    }

    std::vector<ManagedDownloadFile> files;
    for (const std::string& entry : SplitList(managedFilesValue, ';')) {
        ManagedDownloadFile file = ParseManagedDownloadFile(entry);
        if (!file.repositoryRelativePath.empty() && !file.localRelativePath.empty()) {
            files.push_back(std::move(file));
        }
    }

    return files;
}

bool HasDownloadSource() {
    return !GetManagedDownloadFiles().empty();
}

bool CanCheckForUpdates() {
    return !GetManagedDownloadFiles().empty();
}

std::filesystem::path GetInstallBasePath() {
    return LauncherPaths::ResolveFromLauncherDirectory(
        INI::GetString("GamePath", "assets/", "Game"));
}

std::filesystem::path GetInstalledExecutablePath() {
    return Platform::GetExecutableDirectory() / GetExecutableName();
}

std::filesystem::path GetInstalledManagedFilePath(const ManagedDownloadFile& file) {
    return Platform::GetExecutableDirectory() / file.localRelativePath;
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

bool HasInstalledBuild() {
    const std::vector<ManagedDownloadFile> files = GetManagedDownloadFiles();
    if (files.empty()) {
        return std::filesystem::exists(GetInstalledExecutablePath());
    }

    for (const ManagedDownloadFile& file : files) {
        if (!std::filesystem::exists(GetInstalledManagedFilePath(file))) {
            return false;
        }
    }

    return true;
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

    for (const ManagedDownloadFile& file : GetManagedDownloadFiles()) {
        const std::filesystem::path installedPath = GetInstalledManagedFilePath(file);
        if (!std::filesystem::exists(installedPath)) {
            continue;
        }

        try {
            std::filesystem::remove(installedPath);
            hasRemovedFiles = true;
            log << "Removed managed runtime file: " << installedPath.string() << std::endl;
        } catch (const std::exception& exception) {
            log << "Error removing managed runtime file " << installedPath.string()
                << ": " << exception.what() << std::endl;
        }
    }

    if (VersionManager::VersionFileExists()) {
        try {
            VersionManager::RemoveVersionFile();
            hasRemovedFiles = true;
            log << "Removed version file." << std::endl;
        } catch (const std::exception& exception) {
            log << "Error removing version file: " << exception.what() << std::endl;
        }
    }

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

void DownloadManagedFilesAsync(
    const std::vector<ManagedDownloadFile>& files,
    std::shared_ptr<ManagedDownloadProgress> progress,
    std::function<void()> onSuccessCallback) {
    std::thread([files, progress, onSuccessCallback]() {
        progress->isComplete = false;
        progress->hasError = false;
        progress->bytesDownloaded = 0;
        progress->totalBytes = 0;
        progress->currentFileIndex = 0;
        progress->totalFiles = static_cast<int>(files.size());
        progress->SetCurrentFileName("");
        {
            std::lock_guard<std::mutex> lock(progress->mutex);
            progress->errorMessage.clear();
        }

        for (std::size_t index = 0; index < files.size(); ++index) {
            const ManagedDownloadFile& file = files[index];
            progress->currentFileIndex = static_cast<int>(index + 1);
            progress->bytesDownloaded = 0;
            progress->totalBytes = 0;
            progress->SetCurrentFileName(file.localRelativePath.filename().string());

            const std::filesystem::path destinationPath = GetInstalledManagedFilePath(file);
            const std::filesystem::path temporaryPath =
                destinationPath.string() + ".download";

            std::string error;
            const bool downloadSucceeded = Platform::DownloadFile(
                GetDownloadUrlForRelativePath(file.repositoryRelativePath),
                temporaryPath,
                [progress](std::size_t downloaded, std::size_t total) {
                    progress->bytesDownloaded = downloaded;
                    progress->totalBytes = total;
                },
                &error);
            if (!downloadSucceeded) {
                std::error_code ignoredError;
                std::filesystem::remove(temporaryPath, ignoredError);
                progress->hasError = true;
                {
                    std::lock_guard<std::mutex> lock(progress->mutex);
                    progress->errorMessage =
                        error.empty() ? "Download failed" : error;
                }
                progress->isComplete = true;
                return;
            }

            if (!ReplaceDownloadedFile(temporaryPath, destinationPath, &error)) {
                std::error_code ignoredError;
                std::filesystem::remove(temporaryPath, ignoredError);
                progress->hasError = true;
                {
                    std::lock_guard<std::mutex> lock(progress->mutex);
                    progress->errorMessage =
                        error.empty() ? "Failed to replace file" : error;
                }
                progress->isComplete = true;
                return;
            }
        }

        progress->isComplete = true;
        if (onSuccessCallback) {
            onSuccessCallback();
        }
    }).detach();
}

}  // namespace Installer
