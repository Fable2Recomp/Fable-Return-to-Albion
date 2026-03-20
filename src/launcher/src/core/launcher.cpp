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
#include "utils/logsystem.h"
#include "utils/versionmanager.h"

#include <atomic>
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

}  // namespace

int main(int argc, char** argv) {
    (void)argc;
    (void)argv;

    LogStreambuf logBuffer(std::cout.rdbuf());
    std::cout.rdbuf(&logBuffer);

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
            ImGui::SetCursorPosY(ImGui::GetWindowHeight() - 85.0f);
            ImGui::PopFont();
            ImGui::PushFont(BasicTitleFont);

            const std::filesystem::path executablePath = Installer::GetInstalledExecutablePath();
            const std::filesystem::path gameDataPath = Installer::GetGameDataDirectoryPath();
            const std::filesystem::path markerPath = Installer::GetGameMarkerPath();
            const bool installedBuildExists = Installer::HasInstalledBuild();

            if (isoProgress && isoProgress->isExtracting.load()) {
                ImGui::Text("Installing %s...", Installer::GetGameTitle().c_str());
                ImGui::ProgressBar(
                    -1.0f * static_cast<float>(ImGui::GetTime()),
                    ImVec2(-1.0f, 0.0f),
                    "Extracting ISO and copying XEXP...");

                if (isoProgress->hasError.load()) {
                    ImGui::TextColored(
                        ImVec4(1.0f, 0.2f, 0.2f, 1.0f),
                        "Error: %s",
                        isoProgress->errorMessage.c_str());
                    if (ImGui::Button("Retry", ImVec2(-1.0f, 30.0f))) {
                        startInstallFlow();
                    }
                }
            } else if (downloadProgress && !downloadProgress->isComplete.load()) {
                std::string downloadLabel = GetPlatformAwareString(
                    "DownloadingText",
                    "Downloading runtime files...",
                    "Game");
                const std::string currentFileName = downloadProgress->GetCurrentFileName();
                if (!currentFileName.empty()) {
                    downloadLabel += " (" + currentFileName + ")";
                }
                ImGui::Text("%s", downloadLabel.c_str());

                float progress = downloadProgress->GetPercentage() / 100.0f;
                char progressText[64] = {};

                if (downloadProgress->totalBytes.load() > 0) {
                    std::snprintf(
                        progressText,
                        sizeof(progressText),
                        "%.1f%% (%.2f MB / %.2f MB)",
                        downloadProgress->GetPercentage(),
                        downloadProgress->bytesDownloaded.load() / (1024.0f * 1024.0f),
                        downloadProgress->totalBytes.load() / (1024.0f * 1024.0f));
                } else {
                    std::snprintf(progressText, sizeof(progressText), "%s", "Connecting...");
                    progress = 0.0f;
                }

                ImGui::ProgressBar(progress, ImVec2(-1.0f, 0.0f), progressText);
                if (downloadProgress->totalFiles.load() > 1) {
                    ImGui::Text(
                        "File %d of %d",
                        downloadProgress->currentFileIndex.load(),
                        downloadProgress->totalFiles.load());
                }

                if (downloadProgress->hasError.load()) {
                    const std::string errorMessage = downloadProgress->GetErrorMessage();
                    ImGui::TextColored(
                        ImVec4(1.0f, 0.2f, 0.2f, 1.0f),
                        "Error: %s",
                        errorMessage.c_str());
                    if (ImGui::Button("Retry", ImVec2(-1.0f, 30.0f))) {
                        startBuildDownload();
                    }
                }
            } else {
                if (downloadProgress && downloadProgress->isComplete.load() &&
                    !downloadProgress->hasError.load()) {
                    downloadProgress.reset();
                }

                if (isoProgress && isoProgress->isComplete.load() && !isoProgress->hasError.load()) {
                    isoProgress.reset();
                }

                const bool canDownloadBuild = Installer::HasDownloadSource();
                const bool assetsExist = Installer::HasExtractedAssets(gameDataPath, markerPath);

                std::string buttonText = "Play " + Installer::GetGameTitle();
                bool buttonEnabled = true;

                if (gameIsRunning) {
                    buttonText = Installer::GetGameTitle() + " Running";
                    buttonEnabled = false;
                } else if (!assetsExist) {
                    buttonText = "Install " + Installer::GetGameTitle();
                } else if (!installedBuildExists && canDownloadBuild) {
                    buttonText = "Download " + Installer::GetExecutableName();
                } else if (!installedBuildExists) {
                    buttonText = "Missing Runtime Files";
                } else if (canDownloadBuild && updateAvailable.load()) {
                    buttonText = "Update Available";
                }

                if (!buttonEnabled) {
                    ImGui::PushStyleColor(ImGuiCol_Button, ImVec4(0.3f, 0.3f, 0.3f, 0.5f));
                    ImGui::PushStyleColor(ImGuiCol_ButtonHovered, ImVec4(0.3f, 0.3f, 0.3f, 0.5f));
                    ImGui::PushStyleColor(ImGuiCol_ButtonActive, ImVec4(0.3f, 0.3f, 0.3f, 0.5f));
                }

                if (ImGui::Button(buttonText.c_str(), ImVec2(-1.0f, 75.0f)) && buttonEnabled) {
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

                if (!buttonEnabled) {
                    ImGui::PopStyleColor(3);
                }
            }

            ImGui::End();
            ImGui::PopFont();
            ImGui::PushFont(BasicTextFont);
        }
        PopSmartStyle();

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
