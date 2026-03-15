#include "background.h"
#include "changelog.h"
#include "credits.h"
#include "fonts.h"
#include "installer/installer.h"
#include "patches.h"
#include "platform.h"
#include "smartstyles.h"
#include "window.h"
#include "utils/filedownloader.h"
#include "utils/githubapi.h"
#include "utils/inimanager.h"
#include "utils/logsystem.h"
#include "utils/versionmanager.h"

#include <chrono>
#include <cstdio>
#include <filesystem>
#include <iostream>
#include <memory>
#include <string>
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
    std::shared_ptr<DownloadProgress> downloadProgress = nullptr;
    std::shared_ptr<IsoExtractionProgress> isoProgress = nullptr;

    std::string latestCommitSha;
    std::string savedCommitSha;
    bool isCheckingForUpdates = false;
    bool updateAvailable = false;
    bool gameIsRunning = false;
    Platform::ProcessHandle gameProcessHandle;

    window.SetupImGuiIO();

    Patches patchesWindow;
    patchesWindow.Init();

    backgroundManager.Initialize();
    changeLog.Initialize(GetRepositoryName(), GetRepositoryBranch());
    credits.Initialize(GetRepositoryName());

    savedCommitSha = VersionManager::LoadSavedVersion();

    auto startUpdateCheck = [&](const char* logPrefix) {
        if (isCheckingForUpdates || !Installer::CanCheckForUpdates() ||
            !Installer::HasDownloadSource()) {
            return;
        }

        isCheckingForUpdates = true;
        GitHubAPI::FetchLatestCommitShaForFileAsync(
            GetRepositoryName(),
            GetRepositoryBranch(),
            Installer::GetConfiguredExecutableRelativePath(),
            [&](const std::string& sha) {
                latestCommitSha = sha;
                isCheckingForUpdates = false;

                if (!savedCommitSha.empty() && !latestCommitSha.empty() &&
                    savedCommitSha != latestCommitSha) {
                    updateAvailable = true;
                    std::cout << logPrefix << "Executable update available! Current: "
                              << savedCommitSha << " Latest: " << latestCommitSha << std::endl;
                } else if (!savedCommitSha.empty() && savedCommitSha == latestCommitSha) {
                    updateAvailable = false;
                    std::cout << logPrefix << "Executable up to date! Version: "
                              << savedCommitSha << std::endl;
                }
            });
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

        if (currentTime - lastUpdateCheck >= updateCheckInterval && !isCheckingForUpdates) {
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
                        std::cout << "Install path set to: " << gamePath << std::endl;
                    } else if (!error.empty()) {
                        std::cout << error << std::endl;
                    }
                }

                if (ImGui::MenuItem("Uninstall")) {
                    if (Installer::UninstallManagedFiles(std::cout)) {
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
            const bool bundledExecutableExists = std::filesystem::exists(executablePath);

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
                const std::string downloadLabel = GetPlatformAwareString(
                    "DownloadingText",
                    "Downloading " + Installer::GetExecutableName() + "...",
                    "Game");
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

                if (downloadProgress->hasError.load()) {
                    ImGui::TextColored(
                        ImVec4(1.0f, 0.2f, 0.2f, 1.0f),
                        "Error: %s",
                        downloadProgress->errorMessage.c_str());
                    if (ImGui::Button("Retry", ImVec2(-1.0f, 30.0f))) {
                        downloadProgress = std::make_shared<DownloadProgress>();
                        FileDownloader::DownloadFileAsync(
                            Installer::GetDownloadUrl(),
                            executablePath.string(),
                            downloadProgress,
                            [&]() {
                                if (!latestCommitSha.empty()) {
                                    VersionManager::SaveCurrentVersion(latestCommitSha);
                                    savedCommitSha = latestCommitSha;
                                    updateAvailable = false;
                                    std::cout << "Version saved after retry download: "
                                              << latestCommitSha << std::endl;
                                }
                            });
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
                } else if (!bundledExecutableExists && canDownloadBuild) {
                    buttonText = "Download " + Installer::GetExecutableName();
                } else if (!bundledExecutableExists) {
                    buttonText = "Missing " + Installer::GetExecutableName();
                } else if (canDownloadBuild && updateAvailable) {
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
                    } else if (canDownloadBuild && (!bundledExecutableExists || updateAvailable)) {
                        if (updateAvailable && bundledExecutableExists) {
                            try {
                                std::filesystem::remove(executablePath);
                                VersionManager::RemoveVersionFile();
                            } catch (const std::exception& exception) {
                                std::cout << "Error when removing old exe: "
                                          << exception.what() << std::endl;
                            }
                        }

                        downloadProgress = std::make_shared<DownloadProgress>();
                        FileDownloader::DownloadFileAsync(
                            Installer::GetDownloadUrl(),
                            executablePath.string(),
                            downloadProgress,
                            [&]() {
                                if (!latestCommitSha.empty()) {
                                    VersionManager::SaveCurrentVersion(latestCommitSha);
                                    savedCommitSha = latestCommitSha;
                                    updateAvailable = false;
                                }
                            });
                    } else if (!bundledExecutableExists) {
                        std::cout << "Bundled executable is missing: "
                                  << executablePath.string() << std::endl;
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
