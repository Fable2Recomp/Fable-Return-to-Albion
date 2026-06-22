#pragma once

#include <array>
#include <filesystem>
#include <map>
#include <string>
#include <string_view>
#include <vector>

enum class RuntimeCVarType {
    Bool,
    Int,
    Double,
    String,
};

struct RuntimeCVarDefinition {
    const char* name;
    const char* category;
    RuntimeCVarType type;
    const char* defaultValue;
    const char* description;
    const char* allowedValues = nullptr;
};

class RuntimeSettingsPanel {
public:
    void Initialize();
    void Render(std::string_view categoryPrefix = {});

private:
    void Reload();
    void Save();
    void ResetAllToDefaults();
    void SyncTextBuffers();

    std::filesystem::path configPath;
    std::map<std::string, std::string> values;
    std::map<std::string, std::array<char, 512>> textBuffers;
    std::array<char, 128> filter{};
    std::string statusMessage;
    bool initialized = false;
};

const std::vector<RuntimeCVarDefinition>& GetRuntimeCVarDefinitions();
std::map<std::string, std::string> ReadRuntimeConfigValues(
    const std::filesystem::path& configPath);
bool WriteRuntimeConfigValues(
    const std::filesystem::path& configPath,
    const std::map<std::string, std::string>& values,
    std::string* error = nullptr);
bool EnsureRuntimeConfigDefaults(
    const std::filesystem::path& configPath,
    std::string* error = nullptr);
