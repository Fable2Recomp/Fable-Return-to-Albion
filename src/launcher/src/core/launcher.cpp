#include "background.h"
#include "changelog.h"
#include "credits.h"
#include "fonts.h"
#include "installer/installer.h"
#include "modding/mod_workspace.h"
#include "patches.h"
#include "platform.h"
#include "runtime_settings.h"
#include "smartstyles.h"
#include "window.h"
#include "utils/githubapi.h"
#include "utils/inimanager.h"
#include "utils/launcherpaths.h"
#include "utils/logsystem.h"
#include "utils/versionmanager.h"

#include <algorithm>
#include <atomic>
#include <array>
#include <cctype>
#include <chrono>
#include <cstdio>
#include <csignal>
#include <cstdint>
#include <cstdlib>
#include <exception>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

namespace {

std::string GetRepositoryName() {
    return INI::GetString("Name", "Fable2Recomp/refii", "Github");
}

std::string GetRepositoryBranch() {
    return INI::GetString("Branch", "master", "Github");
}

std::string GetProjectTitle() {
    return INI::GetString("ProjectTitle", "Fable 2 Recompiled", "About");
}

std::string GetProjectPage() {
    return INI::GetString("ProjectPage", "https://github.com/Fable2Recomp/refii", "About");
}

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

void TryOpenUrl(const std::string& url) {
    std::string error;
    if (!Platform::OpenUrl(url, &error) && !error.empty()) {
        std::cout << error << std::endl;
    }
}

#if defined(_WIN32)
bool IsFatalLauncherExceptionCode(DWORD exceptionCode) {
    switch (exceptionCode) {
    case EXCEPTION_ACCESS_VIOLATION:
    case EXCEPTION_ARRAY_BOUNDS_EXCEEDED:
    case EXCEPTION_DATATYPE_MISALIGNMENT:
    case EXCEPTION_FLT_DENORMAL_OPERAND:
    case EXCEPTION_FLT_DIVIDE_BY_ZERO:
    case EXCEPTION_FLT_INEXACT_RESULT:
    case EXCEPTION_FLT_INVALID_OPERATION:
    case EXCEPTION_FLT_OVERFLOW:
    case EXCEPTION_FLT_STACK_CHECK:
    case EXCEPTION_FLT_UNDERFLOW:
    case EXCEPTION_ILLEGAL_INSTRUCTION:
    case EXCEPTION_IN_PAGE_ERROR:
    case EXCEPTION_INT_DIVIDE_BY_ZERO:
    case EXCEPTION_INT_OVERFLOW:
    case EXCEPTION_INVALID_DISPOSITION:
    case EXCEPTION_NONCONTINUABLE_EXCEPTION:
    case EXCEPTION_PRIV_INSTRUCTION:
    case EXCEPTION_STACK_OVERFLOW:
        return true;
    default:
        return false;
    }
}

void WriteLauncherCrashLog(const char* source,
                           EXCEPTION_POINTERS* exceptionPointers,
                           int signalNumber = 0) {
    const std::filesystem::path crashLogPath =
        LauncherPaths::GetRuntimeDataRootDirectory() / "logs" / "refii-launcher-crash.log";
    const std::filesystem::path shaderBreadcrumbPath =
        LauncherPaths::GetRuntimeDataRootDirectory() / "assets" / "unpacked" /
        "shaders" / "compile_crash_breadcrumb.txt";

    std::error_code createError;
    std::filesystem::create_directories(crashLogPath.parent_path(), createError);

    std::ofstream crashLog(crashLogPath, std::ios::binary | std::ios::app);
    if (crashLog) {
        crashLog << "=== unhandled launcher exception ===\n";
        crashLog << "source\t" << (source != nullptr ? source : "unknown") << '\n';
        if (signalNumber != 0) {
            crashLog << "signal\t" << signalNumber << '\n';
        }
        if (exceptionPointers != nullptr && exceptionPointers->ExceptionRecord != nullptr) {
            crashLog << "code\t0x" << std::hex
                     << exceptionPointers->ExceptionRecord->ExceptionCode << std::dec << '\n';
            crashLog << "address\t"
                     << exceptionPointers->ExceptionRecord->ExceptionAddress << '\n';
        } else {
            crashLog << "code\tunknown\n";
            crashLog << "address\tunknown\n";
        }

        if (std::filesystem::exists(shaderBreadcrumbPath)) {
            crashLog << "shader_breadcrumb_path\t"
                     << shaderBreadcrumbPath.generic_string() << '\n';
            crashLog << "--- shader breadcrumb ---\n";
            std::ifstream breadcrumb(shaderBreadcrumbPath, std::ios::binary);
            crashLog << breadcrumb.rdbuf();
            crashLog << "\n--- end shader breadcrumb ---\n";
        }
        crashLog << '\n';
    }
}

LONG WINAPI LauncherVectoredExceptionHandler(EXCEPTION_POINTERS* exceptionPointers) {
    if (exceptionPointers != nullptr && exceptionPointers->ExceptionRecord != nullptr &&
        IsFatalLauncherExceptionCode(exceptionPointers->ExceptionRecord->ExceptionCode)) {
        WriteLauncherCrashLog("vectored_exception", exceptionPointers);
    }

    return EXCEPTION_CONTINUE_SEARCH;
}

LONG WINAPI LauncherUnhandledExceptionFilter(EXCEPTION_POINTERS* exceptionPointers) {
    WriteLauncherCrashLog("unhandled_exception_filter", exceptionPointers);

    return EXCEPTION_EXECUTE_HANDLER;
}

void LauncherTerminateHandler() {
    WriteLauncherCrashLog("std_terminate", nullptr);
    std::abort();
}

void LauncherSignalHandler(int signalNumber) {
    WriteLauncherCrashLog("signal", nullptr, signalNumber);
    std::_Exit(128 + signalNumber);
}

void LauncherInvalidParameterHandler(const wchar_t* expression,
                                     const wchar_t* function,
                                     const wchar_t* file,
                                     unsigned int line,
                                     uintptr_t reserved) {
    (void)expression;
    (void)function;
    (void)file;
    (void)line;
    (void)reserved;
    WriteLauncherCrashLog("invalid_parameter", nullptr);
}
#endif

std::string NormalizeButtonStatus(std::string value, std::size_t maxLength = 64) {
    for (char& character : value) {
        if (character == '\n' || character == '\r' || character == '\t') {
            character = ' ';
        }
    }

    const std::size_t detailPos = value.find(" (");
    if (detailPos != std::string::npos) {
        value.erase(detailPos);
    }

    std::string compact;
    compact.reserve(value.size());
    bool previousWasSpace = false;
    for (const char character : value) {
        if (character == ' ') {
            if (!previousWasSpace) {
                compact.push_back(character);
            }
            previousWasSpace = true;
            continue;
        }

        compact.push_back(character);
        previousWasSpace = false;
    }

    while (!compact.empty() && compact.front() == ' ') {
        compact.erase(compact.begin());
    }
    while (!compact.empty() && compact.back() == ' ') {
        compact.pop_back();
    }

    if (compact.size() > maxLength && maxLength > 3) {
        compact.resize(maxLength - 3);
        compact += "...";
    }

    return compact;
}

void CopyStringToBuffer(const std::string& value, std::array<char, 4096>& buffer) {
    std::snprintf(buffer.data(), buffer.size(), "%s", value.c_str());
}

std::string BuildIsoProgressButtonLabel(const IsoExtractionProgress& progress) {
    std::string operationLabel = NormalizeButtonStatus(progress.GetOperationLabel(), 40);
    if (operationLabel.empty()) {
        operationLabel = "Working";
    }

    std::string statusMessage = NormalizeButtonStatus(progress.GetStatusMessage());
    if (statusMessage.empty()) {
        return operationLabel + "...";
    }

    return operationLabel + "...\n" + statusMessage;
}

std::string BuildDownloadProgressButtonLabel(const ManagedDownloadProgress& progress) {
    char progressSummary[96] = {};
    if (progress.totalBytes.load() > 0) {
        std::snprintf(
            progressSummary,
            sizeof(progressSummary),
            "%.1f%% downloaded",
            progress.GetPercentage());
    } else if (progress.totalFiles.load() > 1) {
        std::snprintf(
            progressSummary,
            sizeof(progressSummary),
            "File %d of %d",
            progress.currentFileIndex.load(),
            progress.totalFiles.load());
    } else {
        std::snprintf(progressSummary, sizeof(progressSummary), "%s", "Connecting...");
    }

    return std::string("Downloading Runtime Files...\n") + progressSummary;
}

std::string ToLowerAscii(std::string value) {
    for (char& character : value) {
        character = static_cast<char>(std::tolower(static_cast<unsigned char>(character)));
    }
    return value;
}

std::filesystem::path ResolveLauncherRelativePath(const std::string& value) {
    if (value.empty()) {
        return {};
    }

    std::filesystem::path path(value);
    if (path.is_absolute()) {
        return path.lexically_normal();
    }
    return LauncherPaths::ResolveFromLauncherDirectory(path);
}

std::filesystem::path NormalizeAssetsRoot(std::filesystem::path path) {
    path = path.lexically_normal();
    std::string filename = path.filename().generic_string();
    std::transform(filename.begin(), filename.end(), filename.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    if (filename == "data" || filename == "game") {
        path = path.parent_path();
    }
    return path.lexically_normal();
}

std::filesystem::path ResolveModdingAssetsRoot(
    const std::map<std::string, std::string>& runtimeValues) {
    const auto gameDataIt = runtimeValues.find("game_data_root");
    if (gameDataIt != runtimeValues.end() && !gameDataIt->second.empty()) {
        return NormalizeAssetsRoot(ResolveLauncherRelativePath(gameDataIt->second));
    }
    return Installer::GetInstallBasePath().lexically_normal();
}

std::filesystem::path ResolveConfiguredModsRoot(
    const std::map<std::string, std::string>& runtimeValues) {
    const std::filesystem::path assetsRoot = ResolveModdingAssetsRoot(runtimeValues);
    const auto modsRootIt = runtimeValues.find("vfs_mods_root");
    if (modsRootIt != runtimeValues.end() && !modsRootIt->second.empty()) {
        std::filesystem::path root(modsRootIt->second);
        if (root.is_relative()) {
            root = assetsRoot / root;
        }
        return root.lexically_normal();
    }
    return refii::modding::GetDefaultModsRoot(assetsRoot);
}

void RenderModdingTab(std::string& moddingStatusMessage) {
    const std::filesystem::path runtimeConfigPath = Installer::GetRuntimeConfigPath();
    std::map<std::string, std::string> runtimeValues =
        ReadRuntimeConfigValues(runtimeConfigPath);
    const std::filesystem::path modsRoot =
        ResolveConfiguredModsRoot(runtimeValues);

    ImGui::TextWrapped("Mod folder: %s", modsRoot.generic_string().c_str());
    ImGui::TextWrapped("Install loose-file mods as folders here. A mod can place files directly in its folder, or under a files folder.");

    if (ImGui::Button("Create Folder")) {
        std::error_code error;
        std::filesystem::create_directories(modsRoot, error);
        moddingStatusMessage = error ? ("Failed to create mod folder: " + error.message())
                                     : "Mod folder is ready.";
    }
    ImGui::SameLine();
    if (ImGui::Button("Refresh")) {
        moddingStatusMessage.clear();
    }

    if (!moddingStatusMessage.empty()) {
        ImGui::TextWrapped("%s", moddingStatusMessage.c_str());
    }

    std::vector<refii::modding::ModInstall> mods =
        refii::modding::DiscoverModInstalls(modsRoot);
    std::vector<std::string> enabledMods =
        refii::modding::ReadEnabledModNames(modsRoot);

    ImGui::Separator();
    if (mods.empty()) {
        ImGui::TextWrapped("No mods found.");
        return;
    }

    ImGui::BeginTable("ModdingModsTable", 3,
                      ImGuiTableFlags_RowBg | ImGuiTableFlags_BordersInnerV |
                          ImGuiTableFlags_NoHostExtendX);
    ImGui::TableSetupColumn("Enabled", ImGuiTableColumnFlags_WidthFixed, 80.0f);
    ImGui::TableSetupColumn("Mod", ImGuiTableColumnFlags_WidthFixed, 220.0f);
    ImGui::TableSetupColumn("Path", ImGuiTableColumnFlags_WidthStretch);

    for (const refii::modding::ModInstall& mod : mods) {
        const bool wasEnabled =
            std::find(enabledMods.begin(), enabledMods.end(), mod.name) !=
            enabledMods.end();
        bool enabled = wasEnabled;

        ImGui::PushID(mod.name.c_str());
        ImGui::TableNextRow();
        ImGui::TableSetColumnIndex(0);
        if (ImGui::Checkbox("##enabled", &enabled) && enabled != wasEnabled) {
            if (enabled) {
                enabledMods.push_back(mod.name);
            } else {
                enabledMods.erase(
                    std::remove(enabledMods.begin(), enabledMods.end(), mod.name),
                    enabledMods.end());
            }

            std::string error;
            if (refii::modding::WriteEnabledModNames(modsRoot, enabledMods,
                                                     &error)) {
                moddingStatusMessage = "Updated enabled mods.";
            } else {
                moddingStatusMessage = "Failed to update enabled mods: " + error;
            }
        }

        ImGui::TableSetColumnIndex(1);
        ImGui::TextUnformatted(mod.name.c_str());
        if (mod.has_files_folder && ImGui::IsItemHovered()) {
            ImGui::SetTooltip("Uses the files folder as its asset root.");
        }

        ImGui::TableSetColumnIndex(2);
        const std::string path = mod.root.generic_string();
        ImGui::TextUnformatted(path.c_str());
        ImGui::PopID();
    }
    ImGui::EndTable();
}

float ClampFloat(float value, float minimum, float maximum) {
    return std::max(minimum, std::min(value, maximum));
}

struct LauncherLayout {
    ImVec2 infoPos;
    ImVec2 infoSize;
    ImVec2 warningPos;
    ImVec2 warningSize;
    ImVec2 actionPos;
    ImVec2 actionSize;
};

LauncherLayout BuildLauncherLayout(const ImGuiViewport& viewport, float menuBarHeight) {
    const float contentTop = viewport.Pos.y + std::max(menuBarHeight, 0.0f);
    const float contentHeight = std::max(1.0f, viewport.Size.y - menuBarHeight);

    float infoWidth = ClampFloat(viewport.Size.x * 0.265f, 440.0f, 560.0f);
    if (viewport.Size.x - infoWidth < 640.0f) {
        infoWidth = std::max(320.0f, viewport.Size.x - 640.0f);
    }
    if (viewport.Size.x < 900.0f) {
        infoWidth = ClampFloat(viewport.Size.x * 0.42f, 300.0f, viewport.Size.x * 0.5f);
    }

    const float leftWidth = std::max(1.0f, viewport.Size.x - infoWidth);
    const float warningHeight = viewport.Size.y < 720.0f ? 36.0f : 44.0f;
    const float actionHeight =
        ClampFloat(viewport.Size.y * 0.11f, viewport.Size.y < 720.0f ? 88.0f : 96.0f, 120.0f);
    const float bottomPanelHeight = warningHeight + actionHeight;
    const float bottomTop = contentTop + std::max(0.0f, contentHeight - bottomPanelHeight);

    return {
        ImVec2(viewport.Pos.x + leftWidth, contentTop),
        ImVec2(infoWidth, contentHeight),
        ImVec2(viewport.Pos.x, bottomTop),
        ImVec2(leftWidth, warningHeight),
        ImVec2(viewport.Pos.x, bottomTop + warningHeight),
        ImVec2(leftWidth, actionHeight),
    };
}

}  // namespace

int main(int argc, char** argv) {
    (void)argc;
    (void)argv;

    std::string launcherLogError;
    if (!LogSystem::GetInstance().InitializePersistentLog(LauncherPaths::GetLauncherLogPath(),
                                                          &launcherLogError) &&
        !launcherLogError.empty()) {
        std::fprintf(stderr, "%s\n", launcherLogError.c_str());
    }
#if defined(_WIN32)
    AddVectoredExceptionHandler(1, LauncherVectoredExceptionHandler);
    SetUnhandledExceptionFilter(LauncherUnhandledExceptionFilter);
    _set_invalid_parameter_handler(LauncherInvalidParameterHandler);
    std::set_terminate(LauncherTerminateHandler);
    std::signal(SIGABRT, LauncherSignalHandler);
    std::signal(SIGFPE, LauncherSignalHandler);
    std::signal(SIGILL, LauncherSignalHandler);
    std::signal(SIGSEGV, LauncherSignalHandler);
#endif

    LogStreambuf stdoutLogBuffer(std::cout.rdbuf());
    LogStreambuf stderrLogBuffer(std::cerr.rdbuf());
    std::cout.rdbuf(&stdoutLogBuffer);
    std::cerr.rdbuf(&stderrLogBuffer);
    if (!LogSystem::GetInstance().GetPersistentLogPath().empty()) {
        std::cout << "Launcher log file: "
                  << LogSystem::GetInstance().GetPersistentLogPath().string() << std::endl;
    }

    INI::EnsureINIFileExists();

    VinceWindow window(
        1920,
        1080,
        INI::GetString("Title", "Fable 2 Launcher", "Window").c_str());

    BackgroundManager backgroundManager;
    ChangeLog changeLog;
    Credits credits;
    RuntimeSettingsPanel runtimeSettings;
    std::string moddingStatusMessage;
    std::shared_ptr<ManagedDownloadProgress> downloadProgress = nullptr;
    std::shared_ptr<IsoExtractionProgress> isoProgress = nullptr;
    refii::lua::LuaWorkspaceVerifySummary luaWorkspaceVerifySummary;
    std::string luaWorkspaceVerifyError;
    std::array<char, 256> luaResetFileFilter{};
    std::array<char, 4096> installIsoPathBuffer{};
    std::array<char, 4096> installXexpPathBuffer{};
    std::string installDialogError;
    bool requestInstallPopup = false;

    std::map<std::string, std::string> savedVersions =
        VersionManager::LoadSavedVersions();
    std::map<std::string, std::string> latestVersions;
    std::vector<ManagedDownloadFile> pendingUpdateFiles;
    std::mutex updateStateMutex;
    std::atomic<bool> isCheckingForUpdates{false};
    std::atomic<bool> updateAvailable{false};
    bool gameIsRunning = false;
    Platform::ProcessHandle gameProcessHandle;

    window.SetupImGuiIO();

    Patches patchesWindow;
    patchesWindow.Init();

    backgroundManager.Initialize();
    if (INI::GetBool("EnableOnlineMetadata", "Github", false)) {
        changeLog.Initialize(GetRepositoryName(), GetRepositoryBranch());
        credits.Initialize(GetRepositoryName());
    }
    runtimeSettings.Initialize();

    auto startUpdateCheck = [&](const char* logPrefix) {
        if (isCheckingForUpdates.load() || !Installer::CanCheckForUpdates() ||
            !Installer::HasDownloadSource()) {
            return;
        }

        isCheckingForUpdates = true;

        const std::string repositoryName = GetRepositoryName();
        const std::string repositoryBranch = GetRepositoryBranch();
        const std::string logPrefixString = logPrefix;
        const std::vector<ManagedDownloadFile> managedFiles =
            Installer::GetManagedDownloadFiles();

        std::thread([&,
                     repositoryName,
                     repositoryBranch,
                     logPrefixString,
                     managedFiles]() {
            std::map<std::string, std::string> fetchedVersions;
            std::vector<ManagedDownloadFile> updatedFiles;
            std::map<std::string, std::string> savedVersionsSnapshot;

            for (const ManagedDownloadFile& file : managedFiles) {
                const std::string sha = GitHubAPI::FetchLatestCommitShaForFile(
                    repositoryName,
                    repositoryBranch,
                    file.repositoryRelativePath);
                if (!sha.empty()) {
                    fetchedVersions[file.versionKey] = sha;
                }
            }

            {
                std::lock_guard<std::mutex> lock(updateStateMutex);
                latestVersions = fetchedVersions;
                savedVersionsSnapshot = savedVersions;
            }

            for (const ManagedDownloadFile& file : managedFiles) {
                const bool localFileExists =
                    std::filesystem::exists(Installer::GetInstalledManagedFilePath(file));
                const auto fetchedVersionIt = fetchedVersions.find(file.versionKey);
                const std::string fetchedVersion =
                    fetchedVersionIt != fetchedVersions.end() ? fetchedVersionIt->second : "";
                const auto savedVersionIt = savedVersionsSnapshot.find(file.versionKey);
                const std::string savedVersion =
                    savedVersionIt != savedVersionsSnapshot.end() ? savedVersionIt->second : "";

                if (!localFileExists ||
                    (!fetchedVersion.empty() && fetchedVersion != savedVersion)) {
                    updatedFiles.push_back(file);
                }
            }

            {
                std::lock_guard<std::mutex> lock(updateStateMutex);
                pendingUpdateFiles = updatedFiles;
            }

            updateAvailable = !updatedFiles.empty();
            isCheckingForUpdates = false;

            if (!updatedFiles.empty()) {
                std::cout << logPrefixString << "Runtime update available for "
                          << updatedFiles.size() << " file(s)." << std::endl;
            } else if (!fetchedVersions.empty()) {
                std::cout << logPrefixString << "Runtime files are up to date."
                          << std::endl;
            }
        }).detach();
    };

    auto startInstallFlow = [&]() {
        installDialogError.clear();
        installIsoPathBuffer[0] = '\0';
        installXexpPathBuffer[0] = '\0';
        requestInstallPopup = true;
    };

    auto startLuaWorkspaceRebuild = [&]() {
        isoProgress = std::make_shared<IsoExtractionProgress>();
        Installer::RebuildLuaWorkspaceAsync(isoProgress);
    };

    auto startGeneralBankExtraction = [&]() {
        isoProgress = std::make_shared<IsoExtractionProgress>();
        Installer::ExtractGeneralGameBanksAsync(isoProgress);
    };

    auto startShaderExtraction = [&]() {
        isoProgress = std::make_shared<IsoExtractionProgress>();
        Installer::ExtractGameShadersAsync(isoProgress);
    };

    auto startLuaWorkspaceVerify = [&]() {
        isoProgress = std::make_shared<IsoExtractionProgress>();
        Installer::VerifyLuaWorkspaceAsync(isoProgress);
    };

    auto startLuaWorkspaceResetAll = [&]() {
        isoProgress = std::make_shared<IsoExtractionProgress>();
        Installer::ResetLuaWorkspaceAsync(isoProgress);
    };

    auto startLuaWorkspaceResetFile = [&](const std::filesystem::path& relativePath) {
        isoProgress = std::make_shared<IsoExtractionProgress>();
        Installer::ResetLuaWorkspaceFileAsync(relativePath, isoProgress);
    };

    auto startBuildDownload = [&]() {
        std::vector<ManagedDownloadFile> filesToDownload;
        std::map<std::string, std::string> latestVersionsSnapshot;

        {
            std::lock_guard<std::mutex> lock(updateStateMutex);
            filesToDownload = pendingUpdateFiles;
            latestVersionsSnapshot = latestVersions;
        }

        if (filesToDownload.empty()) {
            filesToDownload = Installer::GetManagedDownloadFiles();
        }

        if (filesToDownload.empty()) {
            return;
        }

        downloadProgress = std::make_shared<ManagedDownloadProgress>();
        Installer::DownloadManagedFilesAsync(
            filesToDownload,
            downloadProgress,
            [&, filesToDownload, latestVersionsSnapshot]() {
                bool savedAnyVersion = false;
                {
                    std::lock_guard<std::mutex> lock(updateStateMutex);
                    for (const ManagedDownloadFile& file : filesToDownload) {
                        const auto versionIt = latestVersionsSnapshot.find(file.versionKey);
                        if (versionIt != latestVersionsSnapshot.end() &&
                            !versionIt->second.empty()) {
                            savedVersions[file.versionKey] = versionIt->second;
                            savedAnyVersion = true;
                        }
                    }

                    if (savedAnyVersion) {
                        VersionManager::SaveCurrentVersions(savedVersions);
                    }

                    pendingUpdateFiles.clear();
                }

                updateAvailable = false;

                if (!savedAnyVersion) {
                    startUpdateCheck("Post-download check: ");
                }
            });
    };

    startUpdateCheck("");

    PushSmartStyle(
        INI::GetImColor("BaseColor_forground", ImColor(0.02f, 0.10f, 0.20f), "Theme"),
        INI::GetImColor("BaseColor_background", ImColor(0.20f, 0.80f, 0.40f), "Theme"));

    glfwSwapInterval(1);
    using clock = std::chrono::high_resolution_clock;
    auto lastTime = clock::now();
    auto lastUpdateCheck = clock::now();
    const auto updateCheckInterval = std::chrono::minutes(5);

    while (!glfwWindowShouldClose(window.getWindow())) {
        const auto currentTime = clock::now();
        const std::chrono::duration<float> elapsed = currentTime - lastTime;
        const float deltaTime = elapsed.count();
        lastTime = currentTime;

        backgroundManager.Update(deltaTime);

        if (gameIsRunning && !Platform::IsProcessRunning(gameProcessHandle)) {
            gameIsRunning = false;
            std::string processEndedMessage = "Game process has ended.";
            if (!gameProcessHandle.errorMessage.empty()) {
                processEndedMessage += " Error: " + gameProcessHandle.errorMessage;
            } else if (gameProcessHandle.hasExitStatus) {
                if (gameProcessHandle.termSignal != 0) {
                    processEndedMessage +=
                        " Signal: " + std::to_string(gameProcessHandle.termSignal) + ".";
                } else {
                    processEndedMessage +=
                        " Exit code: " + std::to_string(gameProcessHandle.exitCode) + ".";
                }
            }
            Platform::CloseProcessHandle(gameProcessHandle);
            std::cout << processEndedMessage << std::endl;
        }

        if (currentTime - lastUpdateCheck >= updateCheckInterval &&
            !isCheckingForUpdates.load()) {
            lastUpdateCheck = currentTime;
            startUpdateCheck("Update check: ");
        }

        glfwPollEvents();
        if (glfwGetWindowAttrib(window.getWindow(), GLFW_ICONIFIED) != 0) {
            continue;
        }

        window.NewFrame();

        ImGui::PushFont(BasicTextFont);

        const bool menuIsoInProgress = isoProgress && isoProgress->isExtracting.load();
        const bool menuDownloadInProgress =
            downloadProgress && !downloadProgress->isComplete.load();
        const bool menuAssetsExist =
            Installer::HasExtractedAssets(Installer::GetGameDataDirectoryPath(),
                                          Installer::GetGameMarkerPath());
        const bool canRunLuaWorkspaceAction =
            !menuIsoInProgress && !menuDownloadInProgress && menuAssetsExist && !gameIsRunning;

        PushSmartStyle(
            INI::GetImColor("MenuBar_forground", ImColor(0.02f, 0.10f, 0.20f), "Theme"),
            INI::GetImColor("MenuBar_background", ImColor(0.20f, 0.80f, 0.40f), "Theme"));
        float mainMenuBarHeight = ImGui::GetFrameHeight();
        if (ImGui::BeginMainMenuBar()) {
            mainMenuBarHeight = ImGui::GetWindowHeight();
            if (ImGui::BeginMenu("File")) {
                if (ImGui::MenuItem("Set Install Folder")) {
                    std::string gamePath;
                    std::string error;
                    if (Platform::SelectDirectory("Select Install Folder", gamePath, &error)) {
                        gamePath = Installer::EnsureTrailingSeparator(gamePath);
                        INI::SetString("GamePath", gamePath, "Game");
                        {
                            std::lock_guard<std::mutex> lock(updateStateMutex);
                            savedVersions = VersionManager::LoadSavedVersions();
                            latestVersions.clear();
                            pendingUpdateFiles.clear();
                        }
                        updateAvailable = false;
                        std::cout << "Install path set to: " << gamePath << std::endl;
                        startUpdateCheck("Install path check: ");
                    } else if (!error.empty()) {
                        std::cout << error << std::endl;
                    }
                }

                if (ImGui::MenuItem("Uninstall")) {
                    if (Installer::UninstallManagedFiles(std::cout)) {
                        {
                            std::lock_guard<std::mutex> lock(updateStateMutex);
                            savedVersions.clear();
                            latestVersions.clear();
                            pendingUpdateFiles.clear();
                        }
                        updateAvailable = false;
                        std::cout << Installer::GetGameTitle()
                                  << " install removed successfully." << std::endl;
                    } else {
                        std::cout << "No " << Installer::GetGameTitle()
                                  << " launcher-managed files to uninstall." << std::endl;
                    }
                }

                ImGui::Separator();

                if (ImGui::MenuItem("Verify Lua Workspace", nullptr, false, canRunLuaWorkspaceAction)) {
                    startLuaWorkspaceVerify();
                }

                if (ImGui::MenuItem("Reset All Lua Files", nullptr, false, canRunLuaWorkspaceAction)) {
                    startLuaWorkspaceResetAll();
                }

                if (ImGui::MenuItem("Reset Lua File...", nullptr, false, canRunLuaWorkspaceAction)) {
                    luaWorkspaceVerifySummary = {};
                    luaWorkspaceVerifyError.clear();
                    if (Installer::VerifyLuaWorkspace(&luaWorkspaceVerifySummary,
                                                      &luaWorkspaceVerifyError)) {
                        luaResetFileFilter.fill('\0');
                        if (luaWorkspaceVerifySummary.baseline_missing != 0 ||
                            luaWorkspaceVerifySummary.baseline_corrupt != 0) {
                            std::cout << "Lua baseline is not ready for per-file reset."
                                      << std::endl;
                        } else {
                            ImGui::OpenPopup("Reset Lua File");
                        }
                    } else if (!luaWorkspaceVerifyError.empty()) {
                        std::cout << luaWorkspaceVerifyError << std::endl;
                    }
                }

                ImGui::Separator();

                if (ImGui::MenuItem("Close")) {
                    glfwSetWindowShouldClose(window.getWindow(), GLFW_TRUE);
                }

                ImGui::EndMenu();
            }

            if (ImGui::BeginMenu("About")) {
                if (ImGui::MenuItem(GetProjectTitle().c_str())) {
                    TryOpenUrl(GetProjectPage());
                }

                if (ImGui::MenuItem("RexGlue")) {
                    TryOpenUrl("https://github.com/rexglue/rexglue-sdk");
                }

                ImGui::EndMenu();
            }

            ImGui::EndMainMenuBar();
        }
        PopSmartStyle();

        ImGui::PushStyleColor(ImGuiCol_WindowBg, ImVec4(0, 0, 0, 0));
        ImGui::PushStyleColor(ImGuiCol_ChildBg, ImVec4(0, 0, 0, 0));
        ImGuiViewport* viewport = ImGui::GetMainViewport();
        const LauncherLayout layout = BuildLauncherLayout(*viewport, mainMenuBarHeight);
        backgroundManager.Render(viewport);
        ImGui::PopStyleColor(2);

        const ImGuiWindowFlags pinnedWindowFlags =
            ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoResize |
            ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoDocking |
            ImGuiWindowFlags_NoSavedSettings;

        PushSmartStyle(
            INI::GetImColor("Warning_forground", ImColor(1.0f, 0.0f, 0.0f), "Theme"),
            INI::GetImColor("Warning_background", ImColor(1.0f, 0.0f, 0.0f), "Theme"));
        {
            ImGui::SetNextWindowPos(layout.warningPos, ImGuiCond_Always);
            ImGui::SetNextWindowSize(layout.warningSize, ImGuiCond_Always);
            ImGui::Begin("NotPlayableYetWarning",
                         nullptr,
                         pinnedWindowFlags | ImGuiWindowFlags_NoTitleBar);
            ImGui::TextWrapped(
                "%s",
                INI::GetString(
                    "WarningText",
                    "Fable 2 Recompiled is not intended to be fully playable yet.",
                    "Window")
                    .c_str());
            ImGui::End();
        }
        PopSmartStyle();

        PushSmartStyle(
            INI::GetImColor("LaunchButton_forground", ImColor(0.02f, 0.10f, 0.20f), "Theme"),
            INI::GetImColor("LaunchButton_background", ImColor(0.20f, 0.80f, 0.40f), "Theme"));
        {
            ImGui::SetNextWindowPos(layout.actionPos, ImGuiCond_Always);
            ImGui::SetNextWindowSize(layout.actionSize, ImGuiCond_Always);
            ImGui::Begin("Launch Button",
                         nullptr,
                         pinnedWindowFlags | ImGuiWindowFlags_NoTitleBar);
            ImGui::PopFont();
            ImGui::PushFont(BasicTitleFont);

            const std::filesystem::path executablePath = Installer::GetInstalledExecutablePath();
            const std::filesystem::path gameDataPath = Installer::GetGameDataDirectoryPath();
            const std::filesystem::path markerPath = Installer::GetGameMarkerPath();
            const bool installedBuildExists = Installer::HasInstalledBuild();
            constexpr float kPrimaryButtonHeight = 88.0f;
            constexpr float kBottomPadding = 10.0f;

            if (downloadProgress && downloadProgress->isComplete.load() &&
                !downloadProgress->hasError.load()) {
                downloadProgress.reset();
            }

            if (isoProgress && isoProgress->isComplete.load() && !isoProgress->hasError.load()) {
                isoProgress.reset();
            }

            const bool isoInProgress = isoProgress && isoProgress->isExtracting.load();
            const bool downloadInProgress =
                downloadProgress && !downloadProgress->isComplete.load();
            const bool canDownloadBuild = Installer::HasDownloadSource();
            const bool assetsExist = Installer::HasExtractedAssets(gameDataPath, markerPath);

            std::string primaryButtonText = "Play " + Installer::GetGameTitle();
            bool primaryButtonEnabled = true;
            bool useCompactProgressFont = false;

            if (isoInProgress) {
                primaryButtonText = BuildIsoProgressButtonLabel(*isoProgress);
                primaryButtonEnabled = false;
                useCompactProgressFont = true;
            } else if (downloadInProgress) {
                primaryButtonText = BuildDownloadProgressButtonLabel(*downloadProgress);
                primaryButtonEnabled = false;
                useCompactProgressFont = true;
            } else if (gameIsRunning) {
                primaryButtonText = Installer::GetGameTitle() + " Running";
                primaryButtonEnabled = false;
            } else if (!assetsExist) {
                primaryButtonText = "Install " + Installer::GetGameTitle();
            } else if (!installedBuildExists && canDownloadBuild) {
                primaryButtonText = "Download " + Installer::GetExecutableName();
            } else if (!installedBuildExists) {
                primaryButtonText = "Missing Runtime Files";
            } else if (canDownloadBuild && updateAvailable.load()) {
                primaryButtonText = "Update Available";
            }

            const float primaryButtonY =
                ImGui::GetWindowHeight() - kBottomPadding - kPrimaryButtonHeight;

            ImGui::SetCursorPosY(primaryButtonY);
            if (!primaryButtonEnabled) {
                ImGui::PushStyleColor(ImGuiCol_Button, ImVec4(0.3f, 0.3f, 0.3f, 0.5f));
                ImGui::PushStyleColor(ImGuiCol_ButtonHovered, ImVec4(0.3f, 0.3f, 0.3f, 0.5f));
                ImGui::PushStyleColor(ImGuiCol_ButtonActive, ImVec4(0.3f, 0.3f, 0.3f, 0.5f));
            }

            if (useCompactProgressFont) {
                ImGui::PopFont();
                ImGui::PushFont(BasicTextFont);
            }

            if (ImGui::Button(primaryButtonText.c_str(), ImVec2(-1.0f, kPrimaryButtonHeight)) &&
                primaryButtonEnabled) {
                if (!assetsExist) {
                    startInstallFlow();
                } else if (canDownloadBuild &&
                           (!installedBuildExists || updateAvailable.load())) {
                    startBuildDownload();
                } else if (!installedBuildExists) {
                    std::cout << "Runtime files are missing from: "
                              << Platform::GetExecutableDirectory().string()
                              << std::endl;
                } else {
                    std::string error;
                    if (!Installer::EnsureRuntimeConfigFile(&error)) {
                        std::cout << "Failed to prepare runtime config. " << error << std::endl;
                    } else {
                        if (Platform::LaunchProcess(
                                Installer::GetInstallBasePath(),
                                executablePath,
                                patchesWindow.GetPatchLaunchArguments(),
                                gameProcessHandle,
                                &error)) {
                            gameIsRunning = true;
                            std::cout << "Game launched successfully." << std::endl;
                        } else {
                            std::cout << "Failed to launch game. " << error << std::endl;
                        }
                    }
                }
            }

            if (useCompactProgressFont) {
                ImGui::PopFont();
                ImGui::PushFont(BasicTitleFont);
            }

            if (!primaryButtonEnabled) {
                ImGui::PopStyleColor(3);
            }

            ImGui::End();
            ImGui::PopFont();
            ImGui::PushFont(BasicTextFont);
        }
        PopSmartStyle();

        if (ImGui::BeginPopupModal("Reset Lua File", nullptr, ImGuiWindowFlags_AlwaysAutoResize)) {
            ImGui::TextWrapped("%zu tracked Lua files", luaWorkspaceVerifySummary.entries.size());
            ImGui::InputText("Filter", luaResetFileFilter.data(), luaResetFileFilter.size());

            const std::string filter = ToLowerAscii(std::string(luaResetFileFilter.data()));
            ImGui::BeginChild("LuaResetFileList", ImVec2(700.0f, 380.0f), true);
            for (const refii::lua::LuaWorkspaceVerifyEntry& entry : luaWorkspaceVerifySummary.entries) {
                if (entry.status == refii::lua::LuaWorkspaceVerifyStatus::kBaselineMissing ||
                    entry.status == refii::lua::LuaWorkspaceVerifyStatus::kBaselineCorrupt ||
                    entry.relative_path.empty()) {
                    continue;
                }

                const std::string relativeText = entry.relative_path.generic_string();
                const std::string statusText =
                    refii::lua::DescribeLuaWorkspaceVerifyStatus(entry.status);
                const std::string haystack =
                    ToLowerAscii(relativeText + " " + statusText);
                if (!filter.empty() && haystack.find(filter) == std::string::npos) {
                    continue;
                }

                ImGui::PushID(relativeText.c_str());
                if (ImGui::Button("Reset")) {
                    ImGui::CloseCurrentPopup();
                    startLuaWorkspaceResetFile(entry.relative_path);
                    ImGui::PopID();
                    break;
                }
                ImGui::SameLine();
                ImGui::TextWrapped("[%s] %s", statusText.c_str(), relativeText.c_str());
                ImGui::PopID();
            }
            ImGui::EndChild();

            if (ImGui::Button("Close")) {
                ImGui::CloseCurrentPopup();
            }
            ImGui::EndPopup();
        }

        if (requestInstallPopup) {
            ImGui::OpenPopup("Install Fable 2");
            requestInstallPopup = false;
        }

        ImGui::SetNextWindowSize(ImVec2(700.0f, 0.0f), ImGuiCond_Appearing);
        if (ImGui::BeginPopupModal("Install Fable 2",
                                   nullptr,
                                   ImGuiWindowFlags_AlwaysAutoResize)) {
            ImGui::TextWrapped(
                "Select the Fable 2 ISO and the extracted game executable marker (.xexp).");
            ImGui::Spacing();

            ImGui::TextUnformatted("Game ISO");
            ImGui::SetNextItemWidth(-120.0f);
            ImGui::InputText(
                "##InstallIsoPath",
                installIsoPathBuffer.data(),
                installIsoPathBuffer.size());
            ImGui::SameLine();
            if (ImGui::Button("Browse##Iso")) {
                std::string pickerError;
                const std::string isoPath = Installer::OpenIsoFileDialog(&pickerError);
                if (!isoPath.empty()) {
                    CopyStringToBuffer(isoPath, installIsoPathBuffer);
                    installDialogError.clear();
                } else if (!pickerError.empty()) {
                    installDialogError = pickerError;
                }
            }

            ImGui::TextUnformatted("Game XEXP");
            ImGui::SetNextItemWidth(-120.0f);
            ImGui::InputText(
                "##InstallXexpPath",
                installXexpPathBuffer.data(),
                installXexpPathBuffer.size());
            ImGui::SameLine();
            if (ImGui::Button("Browse##Xexp")) {
                std::string pickerError;
                const std::string xexpPath = Installer::OpenXexpFileDialog(&pickerError);
                if (!xexpPath.empty()) {
                    CopyStringToBuffer(xexpPath, installXexpPathBuffer);
                    installDialogError.clear();
                } else if (!pickerError.empty()) {
                    installDialogError = pickerError;
                }
            }

            if (!installDialogError.empty()) {
                ImGui::Spacing();
                ImGui::TextWrapped("Picker error: %s", installDialogError.c_str());
                ImGui::TextWrapped("You can still paste the paths manually and continue.");
            }

            const std::string isoPath = installIsoPathBuffer.data();
            const std::string xexpPath = installXexpPathBuffer.data();
            const bool canStartInstall = !isoPath.empty() && !xexpPath.empty();

            ImGui::Spacing();
            if (!canStartInstall) {
                ImGui::BeginDisabled();
            }
            if (ImGui::Button("Start Install", ImVec2(160.0f, 0.0f)) && canStartInstall) {
                installDialogError.clear();

                if (!std::filesystem::exists(std::filesystem::path(isoPath))) {
                    installDialogError = "The selected ISO path does not exist.";
                } else if (!std::filesystem::exists(std::filesystem::path(xexpPath))) {
                    installDialogError = "The selected XEXP path does not exist.";
                } else {
                    isoProgress = std::make_shared<IsoExtractionProgress>();
                    Installer::InstallGameAsync(isoPath, xexpPath, isoProgress);
                    ImGui::CloseCurrentPopup();
                }
            }
            if (!canStartInstall) {
                ImGui::EndDisabled();
            }

            ImGui::SameLine();
            if (ImGui::Button("Cancel", ImVec2(120.0f, 0.0f))) {
                installDialogError.clear();
                ImGui::CloseCurrentPopup();
            }

            ImGui::EndPopup();
        }

        PushSmartStyle(
            INI::GetImColor("ChangeLog_forground", ImColor(0.02f, 0.10f, 0.20f), "Theme"),
            INI::GetImColor("ChangeLog_background", ImColor(0.20f, 0.80f, 0.40f), "Theme"));
        {
            ImGui::SetNextWindowPos(layout.infoPos, ImGuiCond_Always);
            ImGui::SetNextWindowSize(layout.infoSize, ImGuiCond_Always);
            ImGui::Begin("Launcher Info", nullptr, pinnedWindowFlags);
            if (ImGui::BeginTabBar("LauncherInfoTabs")) {
                if (ImGui::BeginTabItem("Change Log")) {
                    changeLog.RenderContents();
                    ImGui::EndTabItem();
                }
                if (ImGui::BeginTabItem("Credits")) {
                    credits.RenderContents();
                    ImGui::EndTabItem();
                }
                if (ImGui::BeginTabItem("Reinstall")) {
                    const std::filesystem::path workspacePath = Installer::GetLuaWorkspacePath();
                    ImGui::TextWrapped("Target: %s", workspacePath.string().c_str());

                    const bool moddingActionEnabled = canRunLuaWorkspaceAction;
                    const bool reinstallActionEnabled =
                        !menuIsoInProgress && !menuDownloadInProgress && !gameIsRunning;
                    const float buttonWidth = std::max(150.0f, ImGui::GetContentRegionAvail().x);

                    if (!reinstallActionEnabled) {
                        ImGui::PushStyleColor(ImGuiCol_Button, ImVec4(0.3f, 0.3f, 0.3f, 0.5f));
                        ImGui::PushStyleColor(ImGuiCol_ButtonHovered,
                                              ImVec4(0.3f, 0.3f, 0.3f, 0.5f));
                        ImGui::PushStyleColor(ImGuiCol_ButtonActive,
                                              ImVec4(0.3f, 0.3f, 0.3f, 0.5f));
                    }
                    if (ImGui::Button("Reinstall Fable 2", ImVec2(buttonWidth, 36.0f)) &&
                        reinstallActionEnabled) {
                        startInstallFlow();
                    }
                    if (!reinstallActionEnabled) {
                        ImGui::PopStyleColor(3);
                    }
                    ImGui::Spacing();
                    if (!moddingActionEnabled) {
                        ImGui::PushStyleColor(ImGuiCol_Button, ImVec4(0.3f, 0.3f, 0.3f, 0.5f));
                        ImGui::PushStyleColor(ImGuiCol_ButtonHovered,
                                              ImVec4(0.3f, 0.3f, 0.3f, 0.5f));
                        ImGui::PushStyleColor(ImGuiCol_ButtonActive,
                                              ImVec4(0.3f, 0.3f, 0.3f, 0.5f));
                    }
                    if (ImGui::Button("Unpack Game Banks", ImVec2(buttonWidth, 36.0f)) &&
                        moddingActionEnabled) {
                        startGeneralBankExtraction();
                    }
                    if (!moddingActionEnabled) {
                        ImGui::PopStyleColor(3);
                    }
                    ImGui::Spacing();
                    if (!moddingActionEnabled) {
                        ImGui::PushStyleColor(ImGuiCol_Button, ImVec4(0.3f, 0.3f, 0.3f, 0.5f));
                        ImGui::PushStyleColor(ImGuiCol_ButtonHovered,
                                              ImVec4(0.3f, 0.3f, 0.3f, 0.5f));
                        ImGui::PushStyleColor(ImGuiCol_ButtonActive,
                                              ImVec4(0.3f, 0.3f, 0.3f, 0.5f));
                    }
                    if (ImGui::Button("Unpack and Decompile Lua", ImVec2(buttonWidth, 36.0f)) &&
                        moddingActionEnabled) {
                        startLuaWorkspaceRebuild();
                    }
                    if (!moddingActionEnabled) {
                        ImGui::PopStyleColor(3);
                    }
                    ImGui::Spacing();
                    if (!moddingActionEnabled) {
                        ImGui::PushStyleColor(ImGuiCol_Button, ImVec4(0.3f, 0.3f, 0.3f, 0.5f));
                        ImGui::PushStyleColor(ImGuiCol_ButtonHovered,
                                              ImVec4(0.3f, 0.3f, 0.3f, 0.5f));
                        ImGui::PushStyleColor(ImGuiCol_ButtonActive,
                                              ImVec4(0.3f, 0.3f, 0.3f, 0.5f));
                    }
                    if (ImGui::Button("Compile Shaders", ImVec2(buttonWidth, 36.0f)) &&
                        moddingActionEnabled) {
                        startShaderExtraction();
                    }

                    if (!moddingActionEnabled) {
                        ImGui::PopStyleColor(3);
                    }

                    if (isoProgress != nullptr) {
                        const std::string operationLabel = isoProgress->GetOperationLabel();
                        const std::string statusMessage = isoProgress->GetStatusMessage();
                        if (!operationLabel.empty() || !statusMessage.empty()) {
                            ImGui::Separator();
                            ImGui::TextWrapped("%s", operationLabel.c_str());
                            if (!statusMessage.empty()) {
                                ImGui::TextWrapped("%s", statusMessage.c_str());
                            }
                        }
                        if (isoProgress->hasError.load() && !isoProgress->errorMessage.empty()) {
                            ImGui::TextWrapped("Error: %s", isoProgress->errorMessage.c_str());
                        }
                    }

                    ImGui::EndTabItem();
                }
                if (ImGui::BeginTabItem("Modding")) {
                    RenderModdingTab(moddingStatusMessage);
                    ImGui::EndTabItem();
                }
                if (ImGui::BeginTabItem("Settings")) {
                    runtimeSettings.Render();
                    ImGui::EndTabItem();
                }
                ImGui::EndTabBar();
            }
            ImGui::End();
        }
        PopSmartStyle();

        PushSmartStyle(
            INI::GetImColor("Patches_forground", ImColor(0.02f, 0.10f, 0.20f), "Theme"),
            INI::GetImColor("Patches_background", ImColor(0.20f, 0.80f, 0.40f), "Theme"));
        if (patchesWindow.HasPatches()) {
            ImGui::Begin("Patches and Mods");
            patchesWindow.Render();
            ImGui::End();
        }
        PopSmartStyle();

        ImGui::PopFont();

        ImGui::Render();
        window.EndFrame();
    }

    PopSmartStyle();
    Platform::CloseProcessHandle(gameProcessHandle);

    ImGui_ImplOpenGL3_Shutdown();
    ImGui_ImplGlfw_Shutdown();
    ImGui::DestroyContext();

    window.Destroy();
    glfwTerminate();

    return 0;
}
