#pragma once

#include "platform.h"

#include <filesystem>

namespace LauncherPaths {

inline std::filesystem::path GetLauncherDirectory() {
    return Platform::GetExecutableDirectory();
}

inline std::filesystem::path GetLauncherDataDirectory() {
    return GetLauncherDirectory() / "assets" / "launcher";
}

inline std::filesystem::path ResolveFromLauncherDirectory(const std::filesystem::path& path) {
    if (path.empty()) {
        return GetLauncherDirectory();
    }

    if (path.is_absolute()) {
        return path;
    }

    return (GetLauncherDirectory() / path).lexically_normal();
}

inline std::filesystem::path GetLauncherAssetDirectory() {
    return GetLauncherDataDirectory() / "assets";
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
    return GetLauncherDirectory() / "refii-launcher.log";
}

}  // namespace LauncherPaths
