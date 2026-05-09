#pragma once

#include "platform.h"

#include <cstdlib>
#include <filesystem>

namespace LauncherPaths {

inline std::filesystem::path GetLauncherDirectory() {
    return Platform::GetExecutableDirectory();
}

inline std::filesystem::path GetBundledLauncherDataDirectory() {
    return GetLauncherDirectory() / "assets" / "launcher";
}

inline bool UseLinuxUserDataRoot() {
#if defined(__linux__)
    const char* appImagePath = std::getenv("APPIMAGE");
    if (appImagePath != nullptr && appImagePath[0] != '\0') {
        return true;
    }

    const char* appDirPath = std::getenv("APPDIR");
    if (appDirPath != nullptr && appDirPath[0] != '\0') {
        return true;
    }

    const char* flatpakId = std::getenv("FLATPAK_ID");
    if (flatpakId != nullptr && flatpakId[0] != '\0') {
        return true;
    }
#endif

    return false;
}

inline std::filesystem::path GetRuntimeDataRootDirectory() {
#if defined(__linux__)
    if (UseLinuxUserDataRoot()) {
        const char* xdgDataHome = std::getenv("XDG_DATA_HOME");
        if (xdgDataHome != nullptr && xdgDataHome[0] != '\0') {
            return (std::filesystem::path(xdgDataHome) / "refii").lexically_normal();
        }

        const char* homeDirectory = std::getenv("HOME");
        if (homeDirectory != nullptr && homeDirectory[0] != '\0') {
            return (std::filesystem::path(homeDirectory) / ".local" / "share" / "refii")
                .lexically_normal();
        }
    }
#endif

    return GetLauncherDirectory();
}

inline std::filesystem::path GetLauncherDataDirectory() {
    return GetRuntimeDataRootDirectory() / "assets" / "launcher";
}

inline std::filesystem::path ResolveFromLauncherDirectory(const std::filesystem::path& path) {
    if (path.empty()) {
        return GetRuntimeDataRootDirectory();
    }

    if (path.is_absolute()) {
        return path;
    }

    return (GetRuntimeDataRootDirectory() / path).lexically_normal();
}

inline std::filesystem::path GetLauncherAssetDirectory() {
    const std::filesystem::path runtimeAssetDirectory =
        (GetLauncherDataDirectory() / "assets").lexically_normal();
    const std::filesystem::path bundledAssetDirectory =
        (GetBundledLauncherDataDirectory() / "assets").lexically_normal();

    if (runtimeAssetDirectory != bundledAssetDirectory &&
        std::filesystem::exists(runtimeAssetDirectory)) {
        return runtimeAssetDirectory;
    }

    return bundledAssetDirectory;
}

inline std::filesystem::path GetLauncherAssetPath(const std::filesystem::path& relativePath) {
    return (GetLauncherAssetDirectory() / relativePath).lexically_normal();
}

inline std::filesystem::path GetConfigPath() {
    return GetLauncherDataDirectory() / "config.cfg";
}

inline std::filesystem::path GetImGuiIniPath() {
    return GetLauncherDataDirectory() / "imgui.cfg";
}

inline std::filesystem::path GetLauncherLogPath() {
    if (UseLinuxUserDataRoot()) {
        return GetRuntimeDataRootDirectory() / "refii-launcher.log";
    }

    return GetLauncherDirectory() / "refii-launcher.log";
}

}  // namespace LauncherPaths
