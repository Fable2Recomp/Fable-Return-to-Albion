#include "versionmanager.h"
#include "inimanager.h"
#include "launcherpaths.h"

#include <filesystem>
#include <fstream>
#include <iostream>
#include <map>

namespace {

std::string TrimTrailingCarriageReturn(std::string value) {
    while (!value.empty() && (value.back() == '\n' || value.back() == '\r')) {
        value.pop_back();
    }
    return value;
}

}  // namespace

void VersionManager::SaveCurrentVersion(const std::string& commitSha) {
    SaveCurrentVersions({ {"", commitSha} });
}

void VersionManager::SaveCurrentVersions(const std::map<std::string, std::string>& versions) {
    try {
        const std::string versionFilePath = GetVersionFilePath();

        std::filesystem::path filePath(versionFilePath);
        std::filesystem::create_directories(filePath.parent_path());

        std::ofstream file(versionFilePath);
        if (!file.is_open()) {
            std::cout << "Failed to save version file: " << versionFilePath << std::endl;
            return;
        }

        for (const auto& [key, value] : versions) {
            if (value.empty()) {
                continue;
            }

            if (key.empty()) {
                file << value << '\n';
            } else {
                file << key << '=' << value << '\n';
            }
        }
    } catch (const std::exception& e) {
        std::cout << "Error saving version: " << e.what() << std::endl;
    }
}

std::string VersionManager::LoadSavedVersion() {
    const std::map<std::string, std::string> versions = LoadSavedVersions();
    const auto legacyIt = versions.find("");
    if (legacyIt != versions.end()) {
        return legacyIt->second;
    }

    if (!versions.empty()) {
        return versions.begin()->second;
    }

    return "";
}

std::map<std::string, std::string> VersionManager::LoadSavedVersions() {
    std::map<std::string, std::string> versions;

    try {
        const std::string versionFilePath = GetVersionFilePath();
        if (!std::filesystem::exists(versionFilePath)) {
            return versions;
        }

        std::ifstream file(versionFilePath);
        if (file.is_open()) {
            std::string line;
            while (std::getline(file, line)) {
                line = TrimTrailingCarriageReturn(line);
                if (line.empty()) {
                    continue;
                }

                const std::size_t separatorPos = line.find('=');
                if (separatorPos == std::string::npos) {
                    versions[""] = line;
                    continue;
                }

                const std::string key = line.substr(0, separatorPos);
                const std::string value = line.substr(separatorPos + 1);
                if (!key.empty() && !value.empty()) {
                    versions[key] = value;
                }
            }
        }
    } catch (const std::exception& e) {
        std::cout << "Error loading version: " << e.what() << std::endl;
    }

    return versions;
}

bool VersionManager::VersionFileExists() {
    try {
        return std::filesystem::exists(GetVersionFilePath());
    }
    catch (const std::exception& e) {
        return false;
    }
}

void VersionManager::RemoveVersionFile() {
    try {
        std::string versionFilePath = GetVersionFilePath();
        if (std::filesystem::exists(versionFilePath)) {
            std::filesystem::remove(versionFilePath);
        }
    }
    catch (const std::exception& e) {
      // log or something eventually
    }
}

const std::string VersionManager::GetVersionFilePath() {
    return LauncherPaths::ResolveFromLauncherDirectory(
        INI::GetString("VersionFile", "version.txt", "Game"))
        .string();
}
