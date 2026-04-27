#pragma once

#include <atomic>
#include <filesystem>
#include <functional>
#include <iosfwd>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "tools/native_lua_decompiler.h"

struct IsoExtractionProgress {
    std::atomic<bool> isExtracting{false};
    std::atomic<bool> isComplete{false};
    std::atomic<bool> hasError{false};
    std::string errorMessage;
    std::string operationLabel = "Installing";
    std::string lastConsoleOutput;
    std::string statusHistory;
    mutable std::mutex outputMutex;

    void SetOperationLabel(const std::string& label) {
        std::lock_guard<std::mutex> lock(outputMutex);
        operationLabel = label;
    }

    std::string GetOperationLabel() const {
        std::lock_guard<std::mutex> lock(outputMutex);
        return operationLabel;
    }

    void SetStatusMessage(const std::string& message, bool appendToHistory = true) {
        std::lock_guard<std::mutex> lock(outputMutex);
        if (appendToHistory && !message.empty() && lastConsoleOutput != message) {
            if (!statusHistory.empty()) {
                statusHistory += '\n';
            }
            statusHistory += message;
        }
        lastConsoleOutput = message;
    }

    std::string GetStatusMessage() const {
        std::lock_guard<std::mutex> lock(outputMutex);
        return lastConsoleOutput;
    }

    std::string GetStatusHistory() const {
        std::lock_guard<std::mutex> lock(outputMutex);
        return statusHistory;
    }

    void Reset() {
        isExtracting = false;
        isComplete = false;
        hasError = false;
        errorMessage.clear();
        operationLabel = "Installing";
        lastConsoleOutput.clear();
        statusHistory.clear();
    }
};

struct ManagedDownloadFile {
    std::string repositoryRelativePath;
    std::filesystem::path localRelativePath;
    std::string versionKey;
};

struct ManagedDownloadProgress {
    std::atomic<size_t> bytesDownloaded{0};
    std::atomic<size_t> totalBytes{0};
    std::atomic<int> currentFileIndex{0};
    std::atomic<int> totalFiles{0};
    std::atomic<bool> isComplete{false};
    std::atomic<bool> hasError{false};
    std::string currentFileName;
    std::string errorMessage;
    mutable std::mutex mutex;

    float GetPercentage() const {
        if (totalBytes.load() == 0) {
            return 0.0f;
        }
        return static_cast<float>(bytesDownloaded.load()) /
               static_cast<float>(totalBytes.load()) * 100.0f;
    }

    void SetCurrentFileName(const std::string& fileName) {
        std::lock_guard<std::mutex> lock(mutex);
        currentFileName = fileName;
    }

    std::string GetCurrentFileName() const {
        std::lock_guard<std::mutex> lock(mutex);
        return currentFileName;
    }

    void SetErrorMessage(const std::string& message) {
        std::lock_guard<std::mutex> lock(mutex);
        errorMessage = message;
    }

    std::string GetErrorMessage() const {
        std::lock_guard<std::mutex> lock(mutex);
        return errorMessage;
    }
};

namespace Installer {

std::string EnsureTrailingSeparator(std::string path);

std::string GetGameTitle();
std::string GetExecutableName();
std::string GetConfiguredExecutableRelativePath();
std::string GetDownloadUrl();
std::string GetDownloadUrlForRelativePath(const std::string& repositoryRelativePath);
std::vector<ManagedDownloadFile> GetManagedDownloadFiles();

bool HasDownloadSource();
bool CanCheckForUpdates();

std::filesystem::path GetInstallBasePath();
std::filesystem::path GetInstalledExecutablePath();
std::filesystem::path GetInstalledManagedFilePath(const ManagedDownloadFile& file);
std::filesystem::path GetGameDataDirectoryPath();
std::filesystem::path GetGameContentDirectoryPath();
std::filesystem::path GetUpdateDirectoryPath();
std::filesystem::path GetGameMarkerPath();
std::filesystem::path GetLuaWorkspacePath();

bool HasInstalledBuild();
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
bool RebuildLuaWorkspace(std::string* error = nullptr);
bool VerifyLuaWorkspace(refii::lua::LuaWorkspaceVerifySummary* summary = nullptr,
                        std::string* error = nullptr);
bool ResetLuaWorkspace(std::string* error = nullptr);
bool ResetLuaWorkspaceFile(const std::filesystem::path& relativePath,
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
void RebuildLuaWorkspaceAsync(std::shared_ptr<IsoExtractionProgress> progress);
void VerifyLuaWorkspaceAsync(std::shared_ptr<IsoExtractionProgress> progress);
void ResetLuaWorkspaceAsync(std::shared_ptr<IsoExtractionProgress> progress);
void ResetLuaWorkspaceFileAsync(const std::filesystem::path& relativePath,
                                std::shared_ptr<IsoExtractionProgress> progress);
void DownloadManagedFilesAsync(
    const std::vector<ManagedDownloadFile>& files,
    std::shared_ptr<ManagedDownloadProgress> progress,
    std::function<void()> onSuccessCallback = nullptr);

}  // namespace Installer
