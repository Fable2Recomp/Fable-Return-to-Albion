#pragma once

#include <cstdint>
#include <filesystem>
#include <functional>
#include <string>
#include <vector>

struct GLFWwindow;

namespace Platform {

struct ProcessHandle {
    void* nativeHandle = nullptr;
    std::uint64_t processId = 0;
    bool hasExitStatus = false;
    int exitCode = 0;
    int termSignal = 0;
    std::string errorMessage;
};

std::string GetConfigSuffix();
std::string GetDefaultExecutableName();
std::string GetDefaultExecutableRelativePath();

bool OpenUrl(const std::string& url, std::string* error = nullptr);
bool SelectDirectory(const std::string& title, std::string& path, std::string* error = nullptr);
bool SelectIsoFile(std::string& path, std::string* error = nullptr);
bool SelectXexpFile(std::string& path, std::string* error = nullptr);

void EnableWindowEffects(GLFWwindow* window);

std::string HttpGet(const std::string& url, std::string* error = nullptr);
bool DownloadFile(
    const std::string& url,
    const std::filesystem::path& localPath,
    const std::function<void(std::size_t, std::size_t)>& onProgress,
    std::string* error = nullptr);

bool LaunchProcess(
    const std::filesystem::path& workingDirectory,
    const std::filesystem::path& executablePath,
    const std::vector<std::string>& arguments,
    ProcessHandle& outHandle,
    std::string* error = nullptr);
bool IsProcessRunning(ProcessHandle& handle);
void CloseProcessHandle(ProcessHandle& handle);

std::filesystem::path GetExecutablePath();
std::filesystem::path GetExecutableDirectory();

void SetExecutableDirectoryOverride(const std::filesystem::path& directory);

}  // namespace Platform
