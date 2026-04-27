#include "background.h"
#include "changelog.h"
#include "credits.h"
#include "fonts.h"
#include "installer/installer.h"
#include "patches.h"
#include "platform.h"
#include "smartstyles.h"
#include "window.h"
#include "utils/githubapi.h"
#include "utils/inimanager.h"
#include "utils/launcherpaths.h"
#include "utils/logsystem.h"
#include "utils/versionmanager.h"

#include <atomic>
#include <array>
#include <cctype>
#include <chrono>
#include <cstdio>
#include <filesystem>
#include <iostream>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

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
    std::shared_ptr<ManagedDownloadProgress> downloadProgress = nullptr;
    std::shared_ptr<IsoExtractionProgress> isoProgress = nullptr;
    refii::lua::LuaWorkspaceVerifySummary luaWorkspaceVerifySummary;
    std::string luaWorkspaceVerifyError;
    std::array<char, 256> luaResetFileFilter{};

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
    changeLog.Initialize(GetRepositoryName(), GetRepositoryBranch());
    credits.Initialize(GetRepositoryName());

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
        const std::string isoPath = Installer::OpenIsoFileDialog();
        if (isoPath.empty()) {
            return;
        }

        const std::string xexpPath = Installer::OpenXexpFileDialog();
        if (xexpPath.empty()) {
            return;
        }

        isoProgress = std::make_shared<IsoExtractionProgress>();
        Installer::InstallGameAsync(isoPath, xexpPath, isoProgress);
    };

    auto startLuaWorkspaceRebuild = [&]() {
        isoProgress = std::make_shared<IsoExtractionProgress>();
        Installer::RebuildLuaWorkspaceAsync(isoProgress);
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
            Platform::CloseProcessHandle(gameProcessHandle);
            std::cout << "Game process has ended." << std::endl;
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
        if (ImGui::BeginMainMenuBar()) {
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
        backgroundManager.Render(viewport);
        ImGui::PopStyleColor(2);

        PushSmartStyle(
            INI::GetImColor("Warning_forground", ImColor(1.0f, 0.0f, 0.0f), "Theme"),
            INI::GetImColor("Warning_background", ImColor(1.0f, 0.0f, 0.0f), "Theme"));
        {
            ImGui::Begin("NotPlayableYetWarning");
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
            ImGui::Begin("Launch Button");
            ImGui::PopFont();
            ImGui::PushFont(BasicTitleFont);

            const std::filesystem::path executablePath = Installer::GetInstalledExecutablePath();
            const std::filesystem::path gameDataPath = Installer::GetGameDataDirectoryPath();
            const std::filesystem::path markerPath = Installer::GetGameMarkerPath();
            const bool installedBuildExists = Installer::HasInstalledBuild();
            constexpr float kPrimaryButtonHeight = 88.0f;
            constexpr float kSecondaryButtonHeight = 36.0f;
            constexpr float kButtonSpacing = 8.0f;
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

            const bool canRebuildLuaWorkspace =
                !isoInProgress && !downloadInProgress && assetsExist && !gameIsRunning &&
                (!downloadProgress || downloadProgress->isComplete.load());

            const float primaryButtonY =
                ImGui::GetWindowHeight() - kBottomPadding - kPrimaryButtonHeight;
            const float secondaryButtonY =
                primaryButtonY - kButtonSpacing - kSecondaryButtonHeight;

            if (canRebuildLuaWorkspace) {
                ImGui::SetCursorPosY(secondaryButtonY);
                    if (ImGui::Button(
                        "Rebuild Lua Workspace",
                        ImVec2(-1.0f, kSecondaryButtonHeight))) {
                    startLuaWorkspaceRebuild();
                }
            }

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

        PushSmartStyle(
            INI::GetImColor("ChangeLog_forground", ImColor(0.02f, 0.10f, 0.20f), "Theme"),
            INI::GetImColor("ChangeLog_background", ImColor(0.20f, 0.80f, 0.40f), "Theme"));
        changeLog.Render();
        PopSmartStyle();

        PushSmartStyle(
            INI::GetImColor("Credits_forground", ImColor(0.02f, 0.10f, 0.20f), "Theme"),
            INI::GetImColor("Credits_background", ImColor(0.20f, 0.80f, 0.40f), "Theme"));
        credits.Render();
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

    glfwDestroyWindow(window.getWindow());
    glfwTerminate();

    return 0;
}
