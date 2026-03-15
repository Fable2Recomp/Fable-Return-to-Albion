#pragma once

#include <atomic>
#include <filesystem>
#include <iosfwd>
#include <memory>
#include <mutex>
#include <string>

struct IsoExtractionProgress {
    std::atomic<bool> isExtracting{false};
    std::atomic<bool> isComplete{false};
    std::atomic<bool> hasError{false};
    std::string errorMessage;
    std::string lastConsoleOutput;
    std::mutex outputMutex;

    void Reset() {
        isExtracting = false;
        isComplete = false;
        hasError = false;
        errorMessage.clear();
        lastConsoleOutput.clear();
    }
};

namespace Installer {

std::string EnsureTrailingSeparator(std::string path);

std::string GetGameTitle();
std::string GetExecutableName();
std::string GetConfiguredExecutableRelativePath();
std::string GetDownloadUrl();

bool HasDownloadSource();
bool CanCheckForUpdates();

std::filesystem::path GetInstallBasePath();
std::filesystem::path GetInstalledExecutablePath();
std::filesystem::path GetGameDataDirectoryPath();
std::filesystem::path GetUpdateDirectoryPath();
std::filesystem::path GetGameMarkerPath();

bool HasExtractedAssets(
    const std::filesystem::path& gameDataDirectory,
    const std::filesystem::path& markerPath);
bool UninstallManagedFiles(std::ostream& log);

std::string OpenIsoFileDialog();
std::string OpenXexpFileDialog();
bool InstallGame(
    const std::filesystem::path& isoPath,
    const std::filesystem::path& xexpPath,
    std::string* error = nullptr);
bool ExtractIso(
    const std::filesystem::path& isoPath,
    const std::filesystem::path& destinationDirectory,
    std::string* error = nullptr);
bool CopyGameExecutable(
    const std::filesystem::path& sourcePath,
    const std::filesystem::path& destinationPath,
    std::string* error = nullptr);
void ExtractIsoAsync(
    const std::string& isoPath,
    std::shared_ptr<IsoExtractionProgress> progress);
void InstallGameAsync(
    const std::string& isoPath,
    const std::string& xexpPath,
    std::shared_ptr<IsoExtractionProgress> progress);

}  // namespace Installer
