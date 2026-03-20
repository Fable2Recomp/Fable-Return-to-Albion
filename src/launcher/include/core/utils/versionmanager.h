#pragma once

#include <map>
#include <string>

class VersionManager {
public:
    static void SaveCurrentVersion(const std::string& commitSha);
    static void SaveCurrentVersions(const std::map<std::string, std::string>& versions);
    
    static std::string LoadSavedVersion();
    static std::map<std::string, std::string> LoadSavedVersions();
    
    static bool VersionFileExists();
    
    static void RemoveVersionFile();

private:
    static const std::string GetVersionFilePath();
};
