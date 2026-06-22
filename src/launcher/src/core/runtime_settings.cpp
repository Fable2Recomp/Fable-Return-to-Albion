#include "runtime_settings.h"

#include "installer/installer.h"
#include "imgui.h"
#include "launcherpaths.h"
#include "modding/runtime_settings.h"

#include <algorithm>
#include <cctype>
#include <charconv>
#include <cstring>
#include <cstdlib>
#include <exception>
#include <fstream>
#include <string_view>
#include <sstream>
#include <unordered_map>
#include <vector>

namespace {

std::string Trim(std::string value) {
    const auto isWhitespace = [](unsigned char ch) {
        return std::isspace(ch) != 0;
    };

    value.erase(value.begin(), std::find_if(value.begin(), value.end(), [&](unsigned char ch) {
        return !isWhitespace(ch);
    }));
    value.erase(std::find_if(value.rbegin(), value.rend(), [&](unsigned char ch) {
        return !isWhitespace(ch);
    }).base(), value.end());
    return value;
}

std::string StripTomlComment(const std::string& line) {
    bool inString = false;
    bool escaped = false;
    std::string result;
    result.reserve(line.size());
    for (char ch : line) {
        if (escaped) {
            result.push_back(ch);
            escaped = false;
            continue;
        }
        if (ch == '\\' && inString) {
            result.push_back(ch);
            escaped = true;
            continue;
        }
        if (ch == '"') {
            inString = !inString;
            result.push_back(ch);
            continue;
        }
        if (ch == '#' && !inString) {
            break;
        }
        result.push_back(ch);
    }
    return result;
}

std::string UnquoteTomlString(std::string value) {
    value = Trim(std::move(value));
    if (value.size() < 2 || value.front() != '"' || value.back() != '"') {
        return value;
    }

    std::string result;
    result.reserve(value.size() - 2);
    bool escaped = false;
    for (std::size_t i = 1; i + 1 < value.size(); ++i) {
        const char ch = value[i];
        if (escaped) {
            switch (ch) {
                case 'n':
                    result.push_back('\n');
                    break;
                case 'r':
                    result.push_back('\r');
                    break;
                case 't':
                    result.push_back('\t');
                    break;
                default:
                    result.push_back(ch);
                    break;
            }
            escaped = false;
            continue;
        }
        if (ch == '\\') {
            escaped = true;
            continue;
        }
        result.push_back(ch);
    }
    return result;
}

std::string EscapeTomlString(const std::string& value) {
    std::string result = "\"";
    for (char ch : value) {
        switch (ch) {
            case '\\':
                result += "\\\\";
                break;
            case '"':
                result += "\\\"";
                break;
            case '\n':
                result += "\\n";
                break;
            case '\r':
                result += "\\r";
                break;
            case '\t':
                result += "\\t";
                break;
            default:
                result.push_back(ch);
                break;
        }
    }
    result += '"';
    return result;
}

std::string NormalizeBoolValue(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    if (value == "1" || value == "yes" || value == "on") {
        return "true";
    }
    if (value == "0" || value == "no" || value == "off") {
        return "false";
    }
    return value == "true" ? "true" : "false";
}

std::string ToLowerAscii(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return value;
}

std::string NormalizeGameDataRootValue(
    const std::string& value,
    const std::filesystem::path& configPath) {
    if (value.empty()) {
        return "assets";
    }

    std::filesystem::path path(value);
    if (path.filename() == "game") {
        path = path.parent_path();
    }
    path = path.lexically_normal();

    if (!path.is_absolute()) {
        if (std::filesystem::exists(path)) {
            return path.lexically_normal().generic_string();
        }

        std::filesystem::path searchBase = configPath.parent_path();
        for (std::size_t i = 0; i < 8 && !searchBase.empty(); ++i) {
            const std::filesystem::path candidate = searchBase / path;
            if (std::filesystem::exists(candidate)) {
                return candidate.lexically_normal().generic_string();
            }
            const std::filesystem::path parent = searchBase.parent_path();
            if (parent == searchBase) {
                break;
            }
            searchBase = parent;
        }
        return path.lexically_normal().generic_string();
    }

    return path.lexically_normal().generic_string();
}

bool ShouldUsePackagedRuntimeDefaults() {
    return LauncherPaths::UseLinuxUserDataRoot();
}

std::string GetDefaultRuntimeGameDataRoot() {
    return Installer::GetInstallBasePath().lexically_normal().generic_string();
}

std::string GetDefaultRuntimeUserDataRoot() {
    return LauncherPaths::GetRuntimeDataRootDirectory().lexically_normal().generic_string();
}

std::string GetDefaultRuntimeUpdateDataRoot() {
    return (Installer::GetUpdateDirectoryPath()).lexically_normal().generic_string();
}

std::string GetDefaultRuntimeCacheRoot() {
    return (LauncherPaths::GetRuntimeDataRootDirectory() / "cache")
        .lexically_normal()
        .generic_string();
}

std::string GetDefaultRuntimeLogFilePath() {
    return (LauncherPaths::GetRuntimeDataRootDirectory() / "logs" / "refii.log")
        .lexically_normal()
        .generic_string();
}

std::filesystem::path DeriveUpdateDataRootPath(const std::string& gameDataRoot,
                                               const std::string& updateDataRoot) {
    if (!updateDataRoot.empty()) {
        return std::filesystem::path(updateDataRoot).lexically_normal();
    }

    std::filesystem::path base(gameDataRoot.empty() ? "assets" : gameDataRoot);
    if (base.filename() == "game") {
        base = base.parent_path();
    }
    return (base / "update").lexically_normal();
}

bool MatchesLegacyUserDataRootValue(const std::string& value) {
    if (value.empty() || value == "assets/save") {
        return true;
    }

    const std::filesystem::path legacyAbsolute =
        (Installer::GetInstallBasePath() / "save").lexically_normal();
    return std::filesystem::path(value).lexically_normal() == legacyAbsolute;
}

void EnsureDirectoryExists(const std::filesystem::path& path) {
    if (path.empty()) {
        return;
    }

    std::error_code error;
    std::filesystem::create_directories(path, error);
}

void EnsurePackagedRuntimeDirectories(const std::map<std::string, std::string>& values,
                                      const std::filesystem::path& configPath) {
    const auto gameDataIt = values.find("game_data_root");
    const auto userDataIt = values.find("user_data_root");
    const auto updateDataIt = values.find("update_data_root");
    const auto cacheIt = values.find("cache_path");

    const std::filesystem::path gameDataRoot =
        std::filesystem::path(gameDataIt != values.end() ? gameDataIt->second : "assets")
            .lexically_normal();
    const std::filesystem::path userDataRoot =
        std::filesystem::path(userDataIt != values.end() ? userDataIt->second : "")
            .lexically_normal();
    const std::filesystem::path updateDataRoot = DeriveUpdateDataRootPath(
        gameDataIt != values.end() ? gameDataIt->second : "assets",
        updateDataIt != values.end() ? updateDataIt->second : "");
    const std::filesystem::path cacheRoot =
        std::filesystem::path(cacheIt != values.end() ? cacheIt->second : "")
            .lexically_normal();
    const auto logFileIt = values.find("log_file");
    const std::filesystem::path logFilePath =
        std::filesystem::path(logFileIt != values.end() ? logFileIt->second : "")
            .lexically_normal();

    EnsureDirectoryExists(configPath.parent_path());
    EnsureDirectoryExists(gameDataRoot);
    EnsureDirectoryExists(userDataRoot);
    EnsureDirectoryExists(updateDataRoot);
    EnsureDirectoryExists(cacheRoot);
    EnsureDirectoryExists(logFilePath.parent_path());
}

std::string_view TrimView(std::string_view value) {
    const auto isWhitespace = [](unsigned char ch) {
        return std::isspace(ch) != 0;
    };

    auto begin = std::find_if(value.begin(), value.end(), [&](unsigned char ch) {
        return !isWhitespace(ch);
    });
    auto end = std::find_if(value.rbegin(), value.rend(), [&](unsigned char ch) {
        return !isWhitespace(ch);
    }).base();
    if (begin >= end) {
        return std::string_view{};
    }
    return std::string_view(&*begin, static_cast<std::size_t>(end - begin));
}

std::vector<std::string_view> ParseAllowedValues(const char* allowedValues) {
    std::vector<std::string_view> values;
    if (allowedValues == nullptr || allowedValues[0] == '\0') {
        return values;
    }

    std::string_view data(allowedValues);
    std::size_t offset = 0;
    while (offset < data.size()) {
        std::size_t end = data.find(',', offset);
        std::string_view token = end == std::string_view::npos
                                     ? data.substr(offset)
                                     : data.substr(offset, end - offset);
        token = TrimView(token);
        if (!token.empty()) {
            values.push_back(token);
        }
        if (end == std::string_view::npos) {
            break;
        }
        offset = end + 1;
    }
    return values;
}

std::string_view StripPrefix(std::string_view name, std::string_view prefix) {
    if (name.size() > prefix.size() &&
        name.compare(0, prefix.size(), prefix) == 0 &&
        name[prefix.size()] == '_') {
        return name.substr(prefix.size() + 1);
    }
    return name;
}

std::string FormatDisplayName(const RuntimeCVarDefinition& definition) {
    std::string_view category(definition.category);
    std::string_view name(definition.name);

    if (category == "UI/Window") {
        name = StripPrefix(name, "window");
    } else if (category == "UI/Presenter") {
        name = StripPrefix(name, "host");
        name = StripPrefix(name, "present");
    } else if (category == "GPU") {
        name = StripPrefix(name, "video_mode");
    } else if (category == "GPU/Vulkan") {
        name = StripPrefix(name, "vulkan");
    } else if (category == "GPU/D3D12") {
        name = StripPrefix(name, "d3d12");
    } else if (category == "Input/Keybinds") {
        name = StripPrefix(name, "keybind");
    } else if (category == "Input") {
        name = StripPrefix(name, "input");
        name = StripPrefix(name, "mnk");
        name = StripPrefix(name, "hid");
        name = StripPrefix(name, "guide");
    } else if (category == "VFS") {
        if (name == "vfs_loose_asset_backend") {
            return "Backend";
        }
        name = StripPrefix(name, "vfs");
    } else if (category == "Renderer") {
        name = StripPrefix(name, "renderer");
    } else if (category == "Renderer/Vulkan") {
        name = StripPrefix(name, "renderer");
        name = StripPrefix(name, "vulkan");
    } else if (category == "Renderer/D3D12") {
        name = StripPrefix(name, "renderer");
        name = StripPrefix(name, "d3d12");
    } else if (category == "Renderer/RenderDoc") {
        name = StripPrefix(name, "renderer");
        name = StripPrefix(name, "renderdoc");
    } else if (category == "Renderer/ImGui") {
        name = StripPrefix(name, "renderer");
        name = StripPrefix(name, "imgui");
    } else if (category == "Log") {
        name = StripPrefix(name, "log");
    } else if (category == "Lua") {
        name = StripPrefix(name, "lua");
    } else if (category == "UI/Vulkan") {
        name = StripPrefix(name, "vulkan");
    } else if (category == "UI/D3D12") {
        name = StripPrefix(name, "d3d12");
    } else if (category == "Kernel") {
        name = StripPrefix(name, "kernel");
    } else if (category == "Audio") {
        name = StripPrefix(name, "audio");
    } else if (category == "Clock") {
        name = StripPrefix(name, "clock");
    } else if (category == "Perf") {
        name = StripPrefix(name, "perf");
    } else if (category == "crt") {
        name = StripPrefix(name, "rexcrt");
    }

    if (name.empty()) {
        name = definition.name;
    }

    std::string result;
    result.reserve(name.size());
    bool upperNext = true;
    for (char c : name) {
        if (c == '_') {
            result.push_back(' ');
            upperNext = true;
            continue;
        }
        if (std::isspace(static_cast<unsigned char>(c))) {
            result.push_back(' ');
            upperNext = true;
            continue;
        }
        if (upperNext) {
            result.push_back(static_cast<char>(std::toupper(static_cast<unsigned char>(c))));
            upperNext = false;
        } else {
            result.push_back(c);
        }
    }

    if (!result.empty() && result.back() == ' ') {
        result.pop_back();
    }

    return result;
}

const RuntimeCVarDefinition* FindDefinition(const std::string& name) {
    for (const RuntimeCVarDefinition& definition : GetRuntimeCVarDefinitions()) {
        if (definition.name == name) {
            return &definition;
        }
    }
    return nullptr;
}

std::string FormatTomlValue(const RuntimeCVarDefinition& definition,
                            const std::string& rawValue) {
    switch (definition.type) {
        case RuntimeCVarType::Bool:
            return NormalizeBoolValue(rawValue);
        case RuntimeCVarType::String:
            return EscapeTomlString(rawValue);
        case RuntimeCVarType::Int:
        case RuntimeCVarType::Double:
            return rawValue.empty() ? definition.defaultValue : rawValue;
    }
    return rawValue;
}

bool MatchesFilter(const RuntimeCVarDefinition& definition, const std::string& filter) {
    if (filter.empty()) {
        return true;
    }

    std::string haystack = std::string(definition.name) + " " + definition.category + " " +
                           definition.description;
    std::transform(haystack.begin(), haystack.end(), haystack.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return haystack.find(filter) != std::string::npos;
}

std::string GetRuntimeSettingValue(const std::map<std::string, std::string>& values,
                                   std::string_view name,
                                   std::string_view fallback) {
    const auto it = values.find(std::string(name));
    if (it == values.end() || it->second.empty()) {
        return std::string(fallback);
    }
    return it->second;
}

bool IsNativeRendererCVarCategory(std::string_view category) {
    return category == "GPU" ||
           category == "GPU/Shader" ||
           category == "GPU/Vulkan" ||
           category == "GPU/D3D12" ||
           category == "UI/Vulkan" ||
           category == "UI/D3D12";
}

bool IsSharedRendererResolutionCVar(std::string_view name) {
    return name == "resolution" ||
           name == "video_mode_width" ||
           name == "video_mode_height";
}

bool RuntimeSettingsUsePlumeRenderer(
    const std::map<std::string, std::string>& values);

std::string RuntimeCVarDisplayCategory(
    const RuntimeCVarDefinition& definition,
    const std::map<std::string, std::string>& values) {
    if (RuntimeSettingsUsePlumeRenderer(values) &&
        std::string_view(definition.category) == "GPU" &&
        IsSharedRendererResolutionCVar(definition.name)) {
        return "Renderer";
    }
    return definition.category;
}

bool ShouldShowRuntimeCVar(const RuntimeCVarDefinition& definition,
                           const std::map<std::string, std::string>& values) {
    const std::string_view category(definition.category);
    const std::string_view name(definition.name);
    const std::string rendererBackend = ToLowerAscii(
        GetRuntimeSettingValue(values, "renderer_backend", "xenia"));
    const std::string plumeApi = ToLowerAscii(
        GetRuntimeSettingValue(values, "renderer_plume_api", "vulkan"));
    const std::string vfsBackend = ToLowerAscii(
        GetRuntimeSettingValue(values, "vfs_loose_asset_backend", "native"));
    const bool isNativeBackend =
        rendererBackend == "native" || rendererBackend == "xenia";
    const bool isPlumeBackend = rendererBackend == "plume";
    const bool isRefiiVfsBackend = vfsBackend == "refii";

    if (category == "VFS") {
        if (name == "vfs_loose_asset_backend") {
            return true;
        }
        return isRefiiVfsBackend;
    }

    if (category == "Renderer") {
        if (name == "renderer_backend") {
            return true;
        }
        return isPlumeBackend;
    }

    if (category == "Renderer/Vulkan") {
        return isPlumeBackend && plumeApi == "vulkan";
    }

    if (category == "Renderer/D3D12") {
        return isPlumeBackend && plumeApi == "d3d12";
    }

    if (category == "Renderer/RenderDoc") {
        return isPlumeBackend;
    }

    if (category == "Renderer/ImGui") {
        return isPlumeBackend;
    }

    if (isPlumeBackend && category == "GPU" &&
        IsSharedRendererResolutionCVar(name)) {
        return true;
    }

    if (IsNativeRendererCVarCategory(category)) {
        return isNativeBackend;
    }

    return true;
}

bool ShouldWriteRuntimeCVar(const RuntimeCVarDefinition& definition,
                            const std::map<std::string, std::string>& values) {
    return ShouldShowRuntimeCVar(definition, values);
}

int ParseIntValue(const std::string& value, const char* fallback) {
    int parsed = 0;
    const std::string source = value.empty() ? fallback : value;
    const auto begin = source.data();
    const auto end = begin + source.size();
    if (std::from_chars(begin, end, parsed).ec == std::errc()) {
        return parsed;
    }
    return std::atoi(fallback);
}

double ParseDoubleValue(const std::string& value, const char* fallback) {
    try {
        return std::stod(value.empty() ? fallback : value);
    } catch (const std::exception&) {
        return std::atof(fallback);
    }
}

bool TryParsePositiveRuntimeInt(std::string_view value, int& output) {
    if (value.empty()) {
        return false;
    }

    int parsed = 0;
    const auto begin = value.data();
    const auto end = begin + value.size();
    if (std::from_chars(begin, end, parsed).ec != std::errc() ||
        parsed <= 0) {
        return false;
    }
    output = parsed;
    return true;
}

bool TryGetResolutionPresetSize(std::string_view value, int& width, int& height) {
    std::string normalized;
    normalized.reserve(value.size());
    for (char ch : value) {
        const unsigned char unsignedCh = static_cast<unsigned char>(ch);
        if (std::isspace(unsignedCh) || ch == '_' || ch == '-') {
            continue;
        }
        normalized.push_back(static_cast<char>(std::tolower(unsignedCh)));
    }

    const std::size_t xPosition = normalized.find('x');
    if (xPosition != std::string::npos && xPosition > 0 &&
        xPosition + 1 < normalized.size()) {
        int parsedWidth = 0;
        int parsedHeight = 0;
        if (TryParsePositiveRuntimeInt(
                std::string_view(normalized).substr(0, xPosition),
                parsedWidth) &&
            TryParsePositiveRuntimeInt(
                std::string_view(normalized).substr(xPosition + 1),
                parsedHeight)) {
            width = parsedWidth;
            height = parsedHeight;
            return true;
        }
    }

    if (normalized == "480p") {
        width = 640;
        height = 480;
        return true;
    }
    if (normalized == "540p") {
        width = 960;
        height = 540;
        return true;
    }
    if (normalized == "720p") {
        width = 1280;
        height = 720;
        return true;
    }
    if (normalized == "900p") {
        width = 1600;
        height = 900;
        return true;
    }
    if (normalized == "1080p") {
        width = 1920;
        height = 1080;
        return true;
    }
    if (normalized == "1440p") {
        width = 2560;
        height = 1440;
        return true;
    }
    if (normalized == "1800p") {
        width = 3200;
        height = 1800;
        return true;
    }
    if (normalized == "2160p" || normalized == "4k") {
        width = 3840;
        height = 2160;
        return true;
    }
    return false;
}

bool RuntimeSettingsUsePlumeRenderer(
    const std::map<std::string, std::string>& values) {
    return ToLowerAscii(GetRuntimeSettingValue(values, "renderer_backend",
                                               "xenia")) == "plume";
}

int RuntimeSettingInt(const std::map<std::string, std::string>& values,
                      const char* name,
                      const char* fallback) {
    const auto it = values.find(name);
    if (it == values.end()) {
        return ParseIntValue("", fallback);
    }
    return ParseIntValue(it->second, fallback);
}

void SetRuntimeSetting(std::map<std::string, std::string>& values,
                       const char* name,
                       int value) {
    values[name] = std::to_string(std::max(1, value));
}

void LinkPlumeResolutionSettings(
    const RuntimeCVarDefinition& changedDefinition,
    std::map<std::string, std::string>& values) {
    const std::string_view name(changedDefinition.name);

    int presetWidth = 0;
    int presetHeight = 0;
    if (name == "resolution" &&
        TryGetResolutionPresetSize(values["resolution"], presetWidth,
                                   presetHeight)) {
        SetRuntimeSetting(values, "video_mode_width", presetWidth);
        SetRuntimeSetting(values, "video_mode_height", presetHeight);
        SetRuntimeSetting(values, "window_width", presetWidth);
        SetRuntimeSetting(values, "window_height", presetHeight);
        return;
    }

    if (!RuntimeSettingsUsePlumeRenderer(values)) {
        return;
    }

    const bool changedVideoSize =
        name == "video_mode_width" || name == "video_mode_height";
    const bool changedWindowSize =
        name == "window_width" || name == "window_height";
    if (!changedVideoSize && !changedWindowSize) {
        return;
    }

    const char* widthName = "video_mode_width";
    const char* heightName = "video_mode_height";
    if (changedWindowSize) {
        widthName = "window_width";
        heightName = "window_height";
    }

    const int width = RuntimeSettingInt(values, widthName, "1280");
    const int height = RuntimeSettingInt(values, heightName, "720");
    values["resolution"].clear();
    SetRuntimeSetting(values, "video_mode_width", width);
    SetRuntimeSetting(values, "video_mode_height", height);
    SetRuntimeSetting(values, "window_width", width);
    SetRuntimeSetting(values, "window_height", height);
}

void CopyToBuffer(std::array<char, 512>& buffer, const std::string& value) {
    buffer.fill('\0');
    std::strncpy(buffer.data(), value.c_str(), buffer.size() - 1);
}

RuntimeCVarType ToRuntimeCVarType(refii::modding::RuntimeSettingType type) {
    switch (type) {
        case refii::modding::RuntimeSettingType::Bool:
            return RuntimeCVarType::Bool;
        case refii::modding::RuntimeSettingType::Int:
            return RuntimeCVarType::Int;
        case refii::modding::RuntimeSettingType::Double:
            return RuntimeCVarType::Double;
        case refii::modding::RuntimeSettingType::String:
            return RuntimeCVarType::String;
    }
    return RuntimeCVarType::String;
}

void AppendModdingRuntimeCVarDefinitions(
    std::vector<RuntimeCVarDefinition>& definitions) {
    for (const refii::modding::RuntimeSettingDefinition& definition :
         refii::modding::GetRuntimeSettingDefinitions()) {
        const auto existing = std::find_if(
            definitions.begin(),
            definitions.end(),
            [&](const RuntimeCVarDefinition& existingDefinition) {
                return std::string_view(existingDefinition.name) ==
                       definition.name;
            });
        if (existing != definitions.end()) {
            continue;
        }
        definitions.push_back(RuntimeCVarDefinition{
            definition.name,
            definition.category,
            ToRuntimeCVarType(definition.type),
            definition.default_value,
            definition.description,
            definition.allowed_values});
    }
}

bool CategoryMatchesPrefix(std::string_view category,
                           std::string_view prefix) {
    return prefix.empty() ||
           category == prefix ||
           (category.size() > prefix.size() &&
            category.compare(0, prefix.size(), prefix) == 0 &&
            category[prefix.size()] == '/');
}

bool IsPrimaryBackendSelector(const RuntimeCVarDefinition& definition) {
    const std::string_view name(definition.name);
    return name == "renderer_backend" || name == "vfs_loose_asset_backend";
}

void MoveCategoryBefore(std::vector<std::string>& categoryOrder,
                        std::string_view category,
                        std::string_view beforeCategory) {
    auto categoryIt = std::find(categoryOrder.begin(), categoryOrder.end(), category);
    auto beforeIt = std::find(categoryOrder.begin(), categoryOrder.end(), beforeCategory);
    if (categoryIt == categoryOrder.end() || beforeIt == categoryOrder.end() ||
        categoryIt < beforeIt) {
        return;
    }

    std::string value = std::move(*categoryIt);
    categoryOrder.erase(categoryIt);
    beforeIt = std::find(categoryOrder.begin(), categoryOrder.end(), beforeCategory);
    categoryOrder.insert(beforeIt, std::move(value));
}

void ApplyRuntimeSettingsCategoryOrder(std::vector<std::string>& categoryOrder) {
    MoveCategoryBefore(categoryOrder, "VFS", "Renderer");
}

}  // namespace

const std::vector<RuntimeCVarDefinition>& GetRuntimeCVarDefinitions() {
    static const std::vector<RuntimeCVarDefinition> definitions = [] {
    std::vector<RuntimeCVarDefinition> definitions = {
        {"log_level", "Log", RuntimeCVarType::String, "info", "Logging level.", "trace,debug,info,warn,error,critical,off"},
        {"log_file", "Log", RuntimeCVarType::String, "", "Log file path; empty uses automatic logs."},
        {"log_verbose", "Log", RuntimeCVarType::Bool, "false", "Enable verbose trace logging."},
        {"log_noisy", "Log", RuntimeCVarType::Bool, "false", "Enable noisy high-frequency logs."},
        {"log_flush_interval", "Log", RuntimeCVarType::Int, "0", "Periodic log flush interval in seconds."},
        {"log_max_file_size_mb", "Log", RuntimeCVarType::Int, "5", "Maximum log file size before rotation."},
        {"log_max_files", "Log", RuntimeCVarType::Int, "20", "Maximum rotated log files to keep."},

        {"game_data_root", "Runtime", RuntimeCVarType::String, "assets", "Override game data path."},
        {"user_data_root", "Runtime", RuntimeCVarType::String, "assets/save", "Override user data path."},
        {"update_data_root", "Runtime", RuntimeCVarType::String, "", "Override update data path."},
        {"cache_path", "Runtime", RuntimeCVarType::String, "", "Override shader cache path."},

        {"lua_use_native_handlers", "Lua", RuntimeCVarType::Bool, "false", "Enable native Lua handler overrides."},
        {"lua_force_loose_banks", "Lua", RuntimeCVarType::Bool, "false", "Force loose-script mode for Lua workspace iteration."},
        {"lua_trace_script_loads", "Lua", RuntimeCVarType::Bool, "false", "Log script source paths at load time."},
        {"lua_trace_native_handlers", "Lua", RuntimeCVarType::Bool, "false", "Log native Lua dispatch matches and fallbacks."},

        {"vfs_loose_asset_backend", "VFS", RuntimeCVarType::String, "native", "VFS backend: refii for loose asset overrides, native for the original Fable 2 VFS.", "native,refii"},
        {"vfs_log_folder", "VFS", RuntimeCVarType::String, "refii_vfs", "When the Refii VFS backend is selected, route run logs to this automatic log subfolder. Empty disables the VFS-specific log folder."},
        {"vfs_trace_loose_assets", "VFS", RuntimeCVarType::Bool, "false", "Log loose asset override matches and fallbacks."},
        {"vfs_trace_vfs", "VFS", RuntimeCVarType::Bool, "false", "Log Fable 2 VFS/resource manager/bank stream flow."},
        {"vfs_trace_vfs_verbose_io", "VFS", RuntimeCVarType::Bool, "false", "Log high-volume Refii VFS stream read/page operations."},
        {"vfs_skip_startup_videos", "VFS", RuntimeCVarType::Bool, "false", "Skip the non-interactive startup videos while using the Refii VFS."},
        {"vfs_force_startup_video_sequence", "VFS", RuntimeCVarType::Bool, "false", "Require the native startup videos to play in sequence before Refii VFS post-video handoff patches may run."},
        {"vfs_startup_video_minimum_playback_ms", "VFS", RuntimeCVarType::Int, "0", "Optional extra startup-video playback floor before Refii VFS post-video handoff may continue when native finalizers are not observed. 0 uses the built-in observed playback gate."},
        {"vfs_loose_asset_root", "VFS", RuntimeCVarType::String, "", "Optional host loose asset root; empty defaults to <assets_root>/unpacked."},
        {"vfs_auto_augment_request_manager_hashes", "VFS", RuntimeCVarType::Bool, "true", "Add Refii-owned runtime resource hashes to the native request-manager hash table."},

        {"renderer_backend", "Renderer", RuntimeCVarType::String, "xenia", "Renderer backend: plume for the Refii RHI path, xenia for the original Fable 2 renderer.", "xenia,plume,disabled"},
        {"renderer_plume_api", "Renderer", RuntimeCVarType::String, "vulkan", "Plume graphics API: vulkan or d3d12.", "vulkan,d3d12"},
        {"renderer_trace_hooks", "Renderer", RuntimeCVarType::Bool, "false", "Log renderer hook dispatch and fallback decisions."},
        {"renderer_trace_resources", "Renderer", RuntimeCVarType::Bool, "false", "Log renderer resource create/lock/unlock/destroy flow."},
        {"renderer_trace_draws", "Renderer", RuntimeCVarType::Bool, "false", "Log draw, state, and presentation calls."},
        {"renderer_trace_verbose", "Renderer", RuntimeCVarType::Bool, "false", "Log high-volume renderer details while hook tracing is enabled."},

        {"renderer_imgui_enable", "Renderer/ImGui", RuntimeCVarType::Bool, "false", "Enable the ReXGlue ImGui interface on the Plume renderer path."},
        {"renderer_imgui_show_stats", "Renderer/ImGui", RuntimeCVarType::Bool, "false", "Show Refii renderer stats windows when ImGui is enabled."},
        {"renderer_imgui_dim_fable", "Renderer/ImGui", RuntimeCVarType::Bool, "false", "Draw a dimmer between Fable 2 and the Plume ImGui overlay."},
        {"renderer_imgui_dim_alpha", "Renderer/ImGui", RuntimeCVarType::Double, "0.55", "Opacity for the Plume ImGui dimmer over Fable 2."},

        {"renderer_debug_test_triangle", "Renderer/Debug", RuntimeCVarType::Bool, "false", "Draw a Plume-native diagnostic triangle over the game output to validate swapchain and pipeline rendering."},

        {"renderer_vulkan_shader_root", "Renderer/Vulkan", RuntimeCVarType::String, "assets/unpacked/shaders/vulkan", "Directory containing compiled Vulkan shader artifacts."},
        {"renderer_vulkan_validation", "Renderer/Vulkan", RuntimeCVarType::Bool, "false", "Enable Vulkan validation for the Plume renderer backend."},

        {"renderer_d3d12_shader_root", "Renderer/D3D12", RuntimeCVarType::String, "assets/unpacked/shaders/D3D12", "Directory containing compiled DirectX 12 shader artifacts."},
        {"renderer_d3d12_debug_layer", "Renderer/D3D12", RuntimeCVarType::Bool, "false", "Enable the DirectX 12 debug layer for the Plume renderer backend."},

        {"renderer_renderdoc_enable", "Renderer/RenderDoc", RuntimeCVarType::Bool, "false", "Enable RenderDoc in-application capture integration for the Refii renderer."},
        {"renderer_renderdoc_capture_next_frame", "Renderer/RenderDoc", RuntimeCVarType::Bool, "false", "Capture the next Plume-rendered frame with RenderDoc."},
        {"renderer_renderdoc_capture_frame", "Renderer/RenderDoc", RuntimeCVarType::Int, "0", "Capture a specific Plume frame with RenderDoc. 0 disables frame-index capture."},
        {"renderer_renderdoc_capture_draw_target", "Renderer/RenderDoc", RuntimeCVarType::String, "", "Capture the first Plume frame whose queued direct draw matches this target. Supported values: treebillboard, particlematerial, overdrawvisualise, postprocessingdownscale4, postprocessinggaussblur, screenfacingsprite, decal, texturedquadpreservestencil.", "treebillboard"},
        {"renderer_renderdoc_overlay", "Renderer/RenderDoc", RuntimeCVarType::Bool, "true", "Show RenderDoc's capture overlay when the RenderDoc API is available."},
        {"renderer_renderdoc_launch_replay", "Renderer/RenderDoc", RuntimeCVarType::Bool, "false", "Ask RenderDoc to launch or focus its replay UI when a capture is triggered from Refii."},
        {"renderer_renderdoc_runtime_path", "Renderer/RenderDoc", RuntimeCVarType::String, "", "Optional RenderDoc runtime library path. Empty searches common system and local librenderdoc locations."},
        {"renderer_renderdoc_capture_path", "Renderer/RenderDoc", RuntimeCVarType::String, "renderdoc/captures", "Directory for RenderDoc captures. Relative paths resolve beside refii."},

        {"window_width", "UI/Window", RuntimeCVarType::Int, "0", "Initial window width; 0 means default."},
        {"window_height", "UI/Window", RuntimeCVarType::Int, "0", "Initial window height; 0 means default."},
        {"fullscreen", "UI/Window", RuntimeCVarType::Bool, "false", "Start in fullscreen mode."},
        {"monitor", "UI/Window", RuntimeCVarType::Int, "0", "Monitor index for fullscreen."},
        {"host_present_from_non_ui_thread", "UI/Presenter", RuntimeCVarType::Bool, "true", "Allow presentation from non-UI threads."},
        {"present_letterbox", "UI/Presenter", RuntimeCVarType::Bool, "true", "Preserve aspect ratio with letterboxing."},
        {"present_safe_area_x", "UI/Presenter", RuntimeCVarType::Int, "90", "Horizontal safe-area percentage."},
        {"present_safe_area_y", "UI/Presenter", RuntimeCVarType::Int, "90", "Vertical safe-area percentage."},
        {"present_effect", "UI/Presenter", RuntimeCVarType::String, "bilinear", "Guest output scaling effect.",
#if defined(REX_HAS_FIDELITYFX_SDK) && REX_HAS_FIDELITYFX_SDK
         "bilinear,cas,fsr,fsr2,fsr3"},
#else
         "bilinear"},
#endif
#if defined(REX_HAS_FIDELITYFX_SDK) && REX_HAS_FIDELITYFX_SDK
        {"present_cas_additional_sharpness", "UI/Presenter", RuntimeCVarType::Double, "0.0", "Additional CAS sharpening amount."},
        {"present_fsr_max_upsampling_passes", "UI/Presenter", RuntimeCVarType::Int, "4", "Maximum FSR upsampling passes."},
        {"present_fsr_sharpness_reduction", "UI/Presenter", RuntimeCVarType::Double, "0.2", "FSR sharpness reduction amount."},
        {"present_fsr_quality_mode", "UI/Presenter", RuntimeCVarType::String, "auto", "FSR quality mode.", "auto,nativeaa,quality,balanced,performance,ultra_performance"},
#endif
        {"present_dither", "UI/Presenter", RuntimeCVarType::Bool, "false", "Enable present dithering."},
        {"present_allow_overscan_cutoff", "UI/Presenter", RuntimeCVarType::Bool, "false", "Allow overscan cutoff."},
        {"present_render_pass_clear", "UI/Presenter", RuntimeCVarType::Bool, "true", "Clear the present render pass."},

        {"resolution", "GPU", RuntimeCVarType::String, "",
         "Resolution preset override. Use 'custom' to control video_mode_* manually.",
         "custom,480p,540p,720p,900p,1080p,1440p,1800p,2160p,4k"},
        {"video_mode_width", "GPU", RuntimeCVarType::Int, "1280", "Guest video mode width."},
        {"video_mode_height", "GPU", RuntimeCVarType::Int, "720", "Guest video mode height."},
        {"renderer_scaling", "Renderer", RuntimeCVarType::String, "1x", "Fable 2 internal viewport supersampling scale.", "1x,2x,3x,4x"},
        {"renderer_plume_present_fps", "Renderer", RuntimeCVarType::Int, "60", "Target FPS for the Plume renderer present pump."},
        {"video_mode_refresh_rate", "GPU", RuntimeCVarType::Double, "60.0", "Guest video mode refresh rate."},
        {"vsync", "GPU", RuntimeCVarType::Bool, "true", "Enable vertical sync."},
        {"async_shader_compilation", "GPU", RuntimeCVarType::Bool, "true", "Compile shaders asynchronously."},
        {"store_shaders", "GPU", RuntimeCVarType::Bool, "true", "Store compiled shaders in cache."},
        {"swap_post_effect", "GPU", RuntimeCVarType::String, "none", "Swap post effect.", "none,fxaa,fxaa_extreme"},
        {"trace_gpu_prefix", "GPU", RuntimeCVarType::String, "", "GPU trace file prefix."},
        {"trace_gpu_stream", "GPU", RuntimeCVarType::Bool, "false", "Enable GPU trace streaming."},
        {"clear_memory_page_state", "GPU", RuntimeCVarType::Bool, "true", "Clear memory page state on startup."},
        {"occlusion_query_enable", "GPU", RuntimeCVarType::Bool, "true", "Enable host occlusion query handling."},
        {"readback_resolve", "GPU", RuntimeCVarType::String, "none", "Readback resolve mode.", "none,fast,some,full"},
        {"readback_resolve_half_pixel_offset", "GPU", RuntimeCVarType::Bool, "false", "Apply half-pixel offset to readback resolves."},
        {"readback_memexport", "GPU", RuntimeCVarType::Bool, "true", "Enable memexport readback."},
        {"readback_memexport_fast", "GPU", RuntimeCVarType::Bool, "true", "Use fast memexport readback."},
        {"query_occlusion_fake_sample_count", "GPU", RuntimeCVarType::Int, "1000", "Fake sample count for occlusion queries."},
        {"half_pixel_offset", "GPU", RuntimeCVarType::Bool, "true", "Enable half-pixel offset."},
        {"native_2x_msaa", "GPU", RuntimeCVarType::Bool, "true", "Enable native 2x MSAA."},
        {"depth_float24_round", "GPU", RuntimeCVarType::Bool, "false", "Round float24 depth values."},
        {"depth_float24_convert_in_pixel_shader", "GPU", RuntimeCVarType::Bool, "false", "Convert float24 depth in pixel shader."},
        {"depth_transfer_not_equal_test", "GPU", RuntimeCVarType::Bool, "true", "Use not-equal depth transfer test."},
        {"gamma_render_target_as_unorm16", "GPU", RuntimeCVarType::Bool, "true", "Treat gamma render targets as UNORM16."},
        {"dump_shaders", "GPU", RuntimeCVarType::String, "", "Directory to dump shaders to."},
        {"use_fuzzy_alpha_epsilon", "GPU", RuntimeCVarType::Bool, "false", "Use fuzzy alpha epsilon comparisons."},
        {"gpu_debug_markers", "GPU", RuntimeCVarType::Bool, "false", "Enable GPU debug markers."},
        {"gpu_allow_invalid_fetch_constants", "GPU", RuntimeCVarType::Bool, "false", "Allow invalid fetch constants."},
        {"draw_resolution_scaled_texture_offsets", "GPU/Shader", RuntimeCVarType::Bool, "true", "Scale texture offsets for draw resolution."},
        {"dxbc_switch", "GPU/Shader", RuntimeCVarType::Bool, "true", "Use switch statements in DXBC."},
        {"dxbc_source_map", "GPU/Shader", RuntimeCVarType::Bool, "false", "Generate DXBC source maps."},
        {"texture_cache_memory_limit_render_to_texture", "GPU", RuntimeCVarType::Int, "24", "Render-to-texture memory limit."},
        {"texture_cache_memory_limit_soft", "GPU", RuntimeCVarType::Int, "384", "Soft texture cache memory limit."},
        {"texture_cache_memory_limit_hard", "GPU", RuntimeCVarType::Int, "768", "Hard texture cache memory limit."},
        {"texture_cache_memory_limit_soft_lifetime", "GPU", RuntimeCVarType::Int, "30", "Soft texture cache lifetime."},
        {"gpu_3d_to_2d_texture", "GPU", RuntimeCVarType::Bool, "true", "Allow 3D-to-2D texture handling."},
        {"anisotropic_override", "GPU", RuntimeCVarType::Int, "3", "Anisotropic filtering override."},
        {"draw_resolution_scale_x", "GPU", RuntimeCVarType::Int, "1", "Draw resolution scale X."},
        {"draw_resolution_scale_y", "GPU", RuntimeCVarType::Int, "1", "Draw resolution scale Y."},
        {"resolution_scale", "GPU", RuntimeCVarType::Int, "1", "Shared draw resolution scale."},
        {"pre_mask_resolve_l2_block", "GPU", RuntimeCVarType::Bool, "true", "Use L2 block for pre-mask resolves."},
        {"resolve_resolution_scale_fill_half_pixel_offset", "GPU", RuntimeCVarType::Bool, "true", "Fill half-pixel offset during scaled resolves."},
        {"execute_unclipped_draw_vs_on_cpu", "GPU", RuntimeCVarType::Bool, "false", "Execute unclipped draw vertex shader on CPU."},
        {"execute_unclipped_draw_vs_on_cpu_with_scissor", "GPU", RuntimeCVarType::Bool, "false", "Use scissor with CPU unclipped draw VS."},
        {"execute_unclipped_draw_vs_on_cpu_for_psi_render_backend", "GPU", RuntimeCVarType::Bool, "true", "Use CPU unclipped draw VS for PSI render backend."},
        {"force_convert_line_loops_to_strips", "GPU", RuntimeCVarType::Bool, "false", "Convert line loops to strips."},
        {"force_convert_quad_lists_to_triangle_lists", "GPU", RuntimeCVarType::Bool, "false", "Convert quad lists to triangle lists."},
        {"force_convert_triangle_fans_to_lists", "GPU", RuntimeCVarType::Bool, "false", "Convert triangle fans to lists."},
        {"primitive_processor_cache_min_indices", "GPU", RuntimeCVarType::Int, "0", "Minimum indices for primitive processor cache."},
        {"mrt_edram_used_range_clamp_to_min", "GPU", RuntimeCVarType::Bool, "true", "Clamp MRT EDRAM used range to minimum."},
        {"snorm16_render_target_full_range", "GPU", RuntimeCVarType::Bool, "true", "Use full range for SNORM16 render targets."},
        {"direct_host_resolve", "GPU", RuntimeCVarType::Bool, "true", "Enable direct host resolves."},
        {"native_stencil_value_output", "GPU", RuntimeCVarType::Bool, "true", "Enable native stencil value output."},
        {"non_seamless_cube_map", "GPU", RuntimeCVarType::Bool, "false", "Use non-seamless cube map sampling."},

        {"input_backend", "Input", RuntimeCVarType::String, "sdl", "Input backend.", "sdl,xinput"},
        {"guide_button", "Input", RuntimeCVarType::Bool, "false", "Enable guide button pass-through."},
        {"hid_mappings_file", "Input", RuntimeCVarType::String, "gamecontrollerdb.txt", "Controller mapping database."},
        {"mnk_mode", "Input", RuntimeCVarType::Bool, "false", "Enable mouse and keyboard controller emulation."},
        {"mnk_user_index", "Input", RuntimeCVarType::Int, "0", "Controller slot for mouse and keyboard."},
        {"mnk_sensitivity", "Input", RuntimeCVarType::Double, "1.0", "Mouse sensitivity for right stick."},
        {"keybind_a", "Input/Keybinds", RuntimeCVarType::String, "Space", "A button keybind."},
        {"keybind_b", "Input/Keybinds", RuntimeCVarType::String, "Shift", "B button keybind."},
        {"keybind_x", "Input/Keybinds", RuntimeCVarType::String, "R", "X button keybind."},
        {"keybind_y", "Input/Keybinds", RuntimeCVarType::String, "E", "Y button keybind."},
        {"keybind_left_trigger", "Input/Keybinds", RuntimeCVarType::String, "RMB", "Left trigger keybind."},
        {"keybind_right_trigger", "Input/Keybinds", RuntimeCVarType::String, "LMB", "Right trigger keybind."},
        {"keybind_left_shoulder", "Input/Keybinds", RuntimeCVarType::String, "Q", "Left shoulder keybind."},
        {"keybind_right_shoulder", "Input/Keybinds", RuntimeCVarType::String, "F", "Right shoulder keybind."},
        {"keybind_lstick_up", "Input/Keybinds", RuntimeCVarType::String, "W", "Left stick up keybind."},
        {"keybind_lstick_down", "Input/Keybinds", RuntimeCVarType::String, "S", "Left stick down keybind."},
        {"keybind_lstick_left", "Input/Keybinds", RuntimeCVarType::String, "A", "Left stick left keybind."},
        {"keybind_lstick_right", "Input/Keybinds", RuntimeCVarType::String, "D", "Left stick right keybind."},
        {"keybind_lstick_press", "Input/Keybinds", RuntimeCVarType::String, "C", "Left stick press keybind."},
        {"keybind_rstick_press", "Input/Keybinds", RuntimeCVarType::String, "MMB", "Right stick press keybind."},
        {"keybind_dpad_up", "Input/Keybinds", RuntimeCVarType::String, "Up", "D-pad up keybind."},
        {"keybind_dpad_down", "Input/Keybinds", RuntimeCVarType::String, "Down", "D-pad down keybind."},
        {"keybind_dpad_left", "Input/Keybinds", RuntimeCVarType::String, "Left", "D-pad left keybind."},
        {"keybind_dpad_right", "Input/Keybinds", RuntimeCVarType::String, "Right", "D-pad right keybind."},
        {"keybind_back", "Input/Keybinds", RuntimeCVarType::String, "Tab", "Back button keybind."},
        {"keybind_start", "Input/Keybinds", RuntimeCVarType::String, "Escape", "Start button keybind."},
        {"keybind_guide", "Input/Keybinds", RuntimeCVarType::String, "", "Guide button keybind."},

        {"audio_mute", "Audio", RuntimeCVarType::Bool, "false", "Mute audio output."},
        {"audio_maxqframes", "Audio", RuntimeCVarType::Int, "8", "Maximum buffered audio frames."},
        {"ffmpeg_verbose", "Audio", RuntimeCVarType::Bool, "false", "Verbose FFmpeg output."},

        {"cl", "Kernel", RuntimeCVarType::String, "", "Additional guest command line."},
        {"xex_apply_patches", "Kernel", RuntimeCVarType::Bool, "true", "Apply configured XEX patches before launch."},
        {"log_high_frequency_kernel_calls", "Kernel", RuntimeCVarType::Bool, "false", "Log high-frequency kernel calls."},
        {"kernel_debug_monitor", "Kernel", RuntimeCVarType::Bool, "false", "Enable debug monitor."},
        {"kernel_cert_monitor", "Kernel", RuntimeCVarType::Bool, "false", "Enable cert monitor."},
        {"ignore_thread_priorities", "Kernel", RuntimeCVarType::Bool, "true", "Ignore guest thread priorities."},
        {"ignore_thread_affinities", "Kernel", RuntimeCVarType::Bool, "true", "Ignore guest thread affinities."},
        {"license_mask", "Kernel", RuntimeCVarType::Int, "0", "Activated content license mask."},
        {"headless", "Kernel", RuntimeCVarType::Bool, "false", "Run without UI prompts."},
        {"user_language", "Kernel", RuntimeCVarType::Int, "1", "User language ID."},
        {"user_country", "Kernel", RuntimeCVarType::Int, "103", "User country ID."},

        {"protect_zero", "Memory", RuntimeCVarType::Bool, "true", "Protect the zero page."},
        {"protect_on_release", "Memory", RuntimeCVarType::Bool, "false", "Protect memory after release."},
        {"scribble_heap", "Memory", RuntimeCVarType::Bool, "false", "Scribble allocated heap memory."},
        {"writable_executable_memory", "Memory", RuntimeCVarType::Bool, "true", "Allow writable executable memory."},
        {"allow_game_relative_writes", "Filesystem", RuntimeCVarType::Bool, "false", "Allow game-relative file writes."},
        {"clock_no_scaling", "Clock", RuntimeCVarType::Bool, "false", "Disable clock scaling."},
        {"clock_source_raw", "Clock", RuntimeCVarType::Bool, "false", "Use raw clock source."},
        {"perf_log_csv", "Perf", RuntimeCVarType::String, "", "Performance counter CSV path."},
        {"rexcrt_heap_enable", "crt", RuntimeCVarType::Bool, "false", "Enable rexcrt heap."},
        {"rexcrt_heap_size_mb", "crt", RuntimeCVarType::Int, "256", "rexcrt heap size in MB."},

#if defined(_WIN32)
        {"d3d12_debug", "UI/D3D12", RuntimeCVarType::Bool, "false", "Enable D3D12 debug layer."},
        {"d3d12_break_on_error", "UI/D3D12", RuntimeCVarType::Bool, "false", "Break on D3D12 errors."},
        {"d3d12_break_on_warning", "UI/D3D12", RuntimeCVarType::Bool, "false", "Break on D3D12 warnings."},
        {"d3d12_adapter", "UI/D3D12", RuntimeCVarType::Int, "-1", "D3D12 adapter index."},
        {"d3d12_queue_priority", "UI/D3D12", RuntimeCVarType::Int, "1", "D3D12 queue priority."},
        {"d3d12_allow_variable_refresh_rate_and_tearing", "UI/D3D12", RuntimeCVarType::Bool, "true", "Allow variable refresh and tearing."},
        {"d3d12_bindless", "GPU/D3D12", RuntimeCVarType::Bool, "true", "Use bindless resources where available."},
        {"d3d12_readback_memexport", "GPU/D3D12", RuntimeCVarType::Bool, "false", "D3D12 memexport readback."},
        {"d3d12_readback_resolve", "GPU/D3D12", RuntimeCVarType::Bool, "false", "D3D12 resolve readback."},
        {"d3d12_submit_on_primary_buffer_end", "GPU/D3D12", RuntimeCVarType::Bool, "true", "Submit on primary buffer end."},
        {"d3d12_dxbc_disasm", "GPU/D3D12", RuntimeCVarType::Bool, "false", "Dump DXBC disassembly."},
        {"d3d12_dxbc_disasm_dxilconv", "GPU/D3D12", RuntimeCVarType::Bool, "false", "Dump DXIL conversion disassembly."},
        {"d3d12_pipeline_creation_threads", "GPU/D3D12", RuntimeCVarType::Int, "-1", "D3D12 pipeline creation worker count."},
        {"d3d12_tessellation_wireframe", "GPU/D3D12", RuntimeCVarType::Bool, "false", "Render tessellation wireframe."},
        {"native_stencil_value_output_d3d12_intel", "GPU/D3D12", RuntimeCVarType::Bool, "false", "Intel native stencil value output workaround."},
        {"render_target_path_d3d12", "GPU/D3D12", RuntimeCVarType::String, "", "D3D12 render-target dump path."},
        {"d3d12_tiled_shared_memory", "GPU/D3D12", RuntimeCVarType::Bool, "true", "Use D3D12 tiled shared memory."},
#else
        {"vulkan_validation_enabled", "UI/Vulkan", RuntimeCVarType::Bool, "false", "Enable Vulkan validation."},
        {"vulkan_device", "UI/Vulkan", RuntimeCVarType::Int, "-1", "Vulkan device index."},
        {"vulkan_require_fragment_stores_and_atomics", "UI/Vulkan", RuntimeCVarType::Bool, "true", "Require fragment stores and atomics."},
        {"vulkan_require_vertex_pipeline_stores_and_atomics", "UI/Vulkan", RuntimeCVarType::Bool, "true", "Require vertex pipeline stores and atomics."},
        {"vulkan_require_geometry_shader", "UI/Vulkan", RuntimeCVarType::Bool, "true", "Require geometry shader support."},
        {"vulkan_require_fill_mode_non_solid", "UI/Vulkan", RuntimeCVarType::Bool, "true", "Require non-solid fill mode support."},
        {"vulkan_prefer_geometry_shader", "UI/Vulkan", RuntimeCVarType::Bool, "true", "Prefer geometry shader support."},
        {"vulkan_prefer_fragment_stores_and_atomics", "UI/Vulkan", RuntimeCVarType::Bool, "true", "Prefer fragment stores and atomics."},
        {"vulkan_prefer_vertex_pipeline_stores_and_atomics", "UI/Vulkan", RuntimeCVarType::Bool, "true", "Prefer vertex pipeline stores and atomics."},
        {"vulkan_prefer_fill_mode_non_solid", "UI/Vulkan", RuntimeCVarType::Bool, "true", "Prefer non-solid fill mode support."},
        {"vulkan_log_debug_messages", "UI/Vulkan", RuntimeCVarType::Bool, "true", "Log Vulkan debug messages."},
        {"vulkan_allow_present_mode_immediate", "UI/Vulkan", RuntimeCVarType::Bool, "true", "Allow immediate present mode."},
        {"vulkan_allow_present_mode_mailbox", "UI/Vulkan", RuntimeCVarType::Bool, "true", "Allow mailbox present mode."},
        {"vulkan_allow_present_mode_fifo_relaxed", "UI/Vulkan", RuntimeCVarType::Bool, "true", "Allow relaxed FIFO present mode."},
        {"vulkan_readback_resolve", "GPU/Vulkan", RuntimeCVarType::Bool, "false", "Vulkan resolve readback."},
        {"vulkan_readback_memexport", "GPU/Vulkan", RuntimeCVarType::Bool, "false", "Vulkan memexport readback."},
        {"vulkan_async_skip_incomplete_frames", "GPU/Vulkan", RuntimeCVarType::Bool, "true", "Skip incomplete async frames."},
        {"vulkan_submit_on_primary_buffer_end", "GPU/Vulkan", RuntimeCVarType::Bool, "true", "Submit on primary buffer end."},
        {"vulkan_dynamic_rendering", "GPU/Vulkan", RuntimeCVarType::Bool, "true", "Use Vulkan dynamic rendering."},
        {"render_target_path_vulkan", "GPU/Vulkan", RuntimeCVarType::String, "", "Vulkan render-target dump path."},
        {"vulkan_sparse_shared_memory", "GPU/Vulkan", RuntimeCVarType::Bool, "true", "Use sparse shared memory."},
        {"vulkan_tessellation_wireframe", "GPU/Vulkan", RuntimeCVarType::Bool, "false", "Render tessellation wireframe."},
        {"vulkan_pipeline_creation_threads", "GPU/Vulkan", RuntimeCVarType::Int, "-1", "Vulkan async pipeline creation thread count."},
        {"vulkan_force_expand_point_sprites_in_vs", "GPU/Vulkan", RuntimeCVarType::Bool, "false", "Force point sprite expansion in vertex shader."},
        {"vulkan_force_expand_rectangle_lists_in_vs", "GPU/Vulkan", RuntimeCVarType::Bool, "false", "Force rectangle list expansion in vertex shader."},
        {"vulkan_force_convert_quad_lists_to_triangle_lists", "GPU/Vulkan", RuntimeCVarType::Bool, "false", "Force Vulkan quad lists to triangle lists."},
#endif
    };
    AppendModdingRuntimeCVarDefinitions(definitions);
    return definitions;
    }();
    return definitions;
}

std::map<std::string, std::string> ReadRuntimeConfigValues(
    const std::filesystem::path& configPath) {
    std::map<std::string, std::string> values;
    std::ifstream stream(configPath, std::ios::binary);
    if (!stream) {
        return values;
    }

    std::string section;
    std::string line;
    while (std::getline(stream, line)) {
        line = Trim(StripTomlComment(line));
        if (line.empty()) {
            continue;
        }

        if (line.front() == '[' && line.back() == ']') {
            section = Trim(line.substr(1, line.size() - 2));
            std::replace(section.begin(), section.end(), '.', '_');
            continue;
        }

        const std::size_t equals = line.find('=');
        if (equals == std::string::npos) {
            continue;
        }

        std::string key = Trim(line.substr(0, equals));
        std::string value = Trim(line.substr(equals + 1));
        if (key.empty()) {
            continue;
        }

        if (!section.empty()) {
            key = section + "_" + key;
        }
        const std::string unquoted = UnquoteTomlString(value);
        if (key == "game_data_root") {
            values[key] = NormalizeGameDataRootValue(unquoted, configPath);
        } else {
            values[key] = unquoted;
        }
    }

    return values;
}

bool WriteRuntimeConfigValues(
    const std::filesystem::path& configPath,
    const std::map<std::string, std::string>& values,
    std::string* error) {
    try {
        std::filesystem::create_directories(configPath.parent_path());
        std::ofstream stream(configPath, std::ios::binary | std::ios::trunc);
        if (!stream) {
            if (error) {
                *error = "failed to open " + configPath.string() + " for writing";
            }
            return false;
        }

        stream << "# Runtime config generated by the launcher.\n"
               << "# Values are written explicitly for the active runtime backend.\n\n";

        std::string currentCategory;
        for (const RuntimeCVarDefinition& definition : GetRuntimeCVarDefinitions()) {
            if (!ShouldWriteRuntimeCVar(definition, values)) {
                continue;
            }

            const std::string name = definition.name;
            if (definition.category != currentCategory) {
                stream << "\n# " << definition.category << "\n";
                currentCategory = definition.category;
            }

            const auto valueIt = values.find(name);
            const std::string rawValue =
                valueIt != values.end() ? valueIt->second : definition.defaultValue;
            if (definition.description && definition.description[0] != '\0') {
                stream << "# " << definition.description << "\n";
            }
            stream << name << " = " << FormatTomlValue(definition, rawValue)
                   << "\n";
        }
        return stream.good();
    } catch (const std::exception& exception) {
        if (error) {
            *error = exception.what();
        }
        return false;
    }
}

bool EnsureRuntimeConfigDefaults(
    const std::filesystem::path& configPath,
    std::string* error) {
    std::map<std::string, std::string> values = ReadRuntimeConfigValues(configPath);
    bool changed = !std::filesystem::exists(configPath);

    if (ShouldUsePackagedRuntimeDefaults()) {
        const std::string packagedGameDataRoot = GetDefaultRuntimeGameDataRoot();
        const std::string packagedUserDataRoot = GetDefaultRuntimeUserDataRoot();
        const std::string packagedUpdateDataRoot = GetDefaultRuntimeUpdateDataRoot();
        const std::string packagedCacheRoot = GetDefaultRuntimeCacheRoot();
        const std::string packagedLogFilePath = GetDefaultRuntimeLogFilePath();

        auto updateDefaultPath = [&](const char* key,
                                     const char* legacyDefault,
                                     const std::string& packagedValue) {
            const auto valueIt = values.find(key);
            if (valueIt == values.end() || valueIt->second.empty() ||
                valueIt->second == legacyDefault) {
                if (valueIt == values.end() || valueIt->second != packagedValue) {
                    values[key] = packagedValue;
                    changed = true;
                }
            }
        };

        updateDefaultPath("game_data_root", "assets", packagedGameDataRoot);
        const auto userDataIt = values.find("user_data_root");
        if (userDataIt == values.end() ||
            MatchesLegacyUserDataRootValue(userDataIt->second)) {
            if (userDataIt == values.end() ||
                userDataIt->second != packagedUserDataRoot) {
                values["user_data_root"] = packagedUserDataRoot;
                changed = true;
            }
        }
        updateDefaultPath("update_data_root", "", packagedUpdateDataRoot);
        updateDefaultPath("cache_path", "", packagedCacheRoot);
        updateDefaultPath("log_file", "", packagedLogFilePath);
    }

    for (const RuntimeCVarDefinition& definition : GetRuntimeCVarDefinitions()) {
        if (values.find(definition.name) == values.end()) {
            values[definition.name] = definition.defaultValue;
            changed = true;
        }
    }

    for (auto valueIt = values.begin(); valueIt != values.end();) {
        const RuntimeCVarDefinition* definition = FindDefinition(valueIt->first);
        if (definition && !ShouldWriteRuntimeCVar(*definition, values)) {
            valueIt = values.erase(valueIt);
            changed = true;
        } else {
            ++valueIt;
        }
    }

    if (ShouldUsePackagedRuntimeDefaults()) {
        EnsurePackagedRuntimeDirectories(values, configPath);
    }
    return changed ? WriteRuntimeConfigValues(configPath, values, error) : true;
}

void RuntimeSettingsPanel::Initialize() {
    if (initialized) {
        return;
    }
    initialized = true;
    Reload();
}

void RuntimeSettingsPanel::Reload() {
    configPath = std::filesystem::path();
    statusMessage.clear();
    values.clear();

    std::string error;
    configPath = Installer::GetRuntimeConfigPath();
    if (!EnsureRuntimeConfigDefaults(configPath, &error)) {
        statusMessage = "Failed to prepare refii.toml: " + error;
    }
    values = ReadRuntimeConfigValues(configPath);
    for (const RuntimeCVarDefinition& definition : GetRuntimeCVarDefinitions()) {
        if (values.find(definition.name) == values.end()) {
            values[definition.name] = definition.defaultValue;
        }
    }
    SyncTextBuffers();
}

void RuntimeSettingsPanel::Save() {
    std::string error;
    if (!WriteRuntimeConfigValues(configPath, values, &error)) {
        statusMessage = "Failed to save refii.toml: " + error;
        return;
    }
    statusMessage = "Saved " + configPath.filename().string();
}

void RuntimeSettingsPanel::ResetAllToDefaults() {
    values.clear();
    for (const RuntimeCVarDefinition& definition : GetRuntimeCVarDefinitions()) {
        values[definition.name] = definition.defaultValue;
    }
    SyncTextBuffers();
    Save();
}

void RuntimeSettingsPanel::SyncTextBuffers() {
    for (const RuntimeCVarDefinition& definition : GetRuntimeCVarDefinitions()) {
        const auto valueIt = values.find(definition.name);
        CopyToBuffer(textBuffers[definition.name],
                     valueIt != values.end() ? valueIt->second : definition.defaultValue);
    }
}

void RuntimeSettingsPanel::Render(std::string_view categoryPrefix) {
    Initialize();

    ImGui::TextWrapped("Editing: %s", configPath.string().c_str());
    ImGui::TextWrapped("Changes are written to refii.toml immediately. Restart-only CVars still require relaunching the game.");
    ImGui::InputText("Filter", filter.data(), filter.size());
    ImGui::SameLine();
    if (ImGui::Button("Reload")) {
        Reload();
    }
    ImGui::SameLine();
    if (ImGui::Button("Reset all to defaults")) {
        ResetAllToDefaults();
    }
    if (!statusMessage.empty()) {
        ImGui::TextWrapped("%s", statusMessage.c_str());
    }

    std::string filterText(filter.data());
    std::transform(filterText.begin(), filterText.end(), filterText.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });

    ImGui::BeginChild("RuntimeSettingsChild", ImVec2(0, 0), true);
    std::vector<std::string> categoryOrder;
    std::unordered_map<std::string, std::vector<const RuntimeCVarDefinition*>>
        definitionsByCategory;
    for (const RuntimeCVarDefinition& definition : GetRuntimeCVarDefinitions()) {
        if (!ShouldShowRuntimeCVar(definition, values) ||
            !MatchesFilter(definition, filterText)) {
            continue;
        }

        const std::string displayCategory =
            RuntimeCVarDisplayCategory(definition, values);
        if (!CategoryMatchesPrefix(displayCategory, categoryPrefix)) {
            continue;
        }
        if (definitionsByCategory.find(displayCategory) ==
            definitionsByCategory.end()) {
            categoryOrder.push_back(displayCategory);
        }
        std::vector<const RuntimeCVarDefinition*>& categoryDefinitions =
            definitionsByCategory[displayCategory];
        if (IsPrimaryBackendSelector(definition)) {
            categoryDefinitions.insert(categoryDefinitions.begin(), &definition);
        } else {
            categoryDefinitions.push_back(&definition);
        }
    }
    ApplyRuntimeSettingsCategoryOrder(categoryOrder);

    for (const std::string& currentCategory : categoryOrder) {
        const bool categoryOpen = ImGui::CollapsingHeader(
            currentCategory.c_str(),
            ImGuiTreeNodeFlags_DefaultOpen);

        if (!categoryOpen) {
            continue;
        }

        ImGui::TreePush(currentCategory.c_str());
        ImGui::BeginTable(
            (std::string("RuntimeSettingsTable_") + currentCategory).c_str(),
            3,
            ImGuiTableFlags_RowBg | ImGuiTableFlags_BordersInnerV |
                ImGuiTableFlags_NoHostExtendX);
        ImGui::TableSetupColumn("Setting", ImGuiTableColumnFlags_WidthFixed,
                                220.0f);
        ImGui::TableSetupColumn("Value", ImGuiTableColumnFlags_WidthStretch);
        ImGui::TableSetupColumn("Default", ImGuiTableColumnFlags_WidthFixed,
                                80.0f);

        for (const RuntimeCVarDefinition* definitionPtr :
             definitionsByCategory[currentCategory]) {
            const RuntimeCVarDefinition& definition = *definitionPtr;
            std::string& value = values[definition.name];
            if (value.empty() && definition.type != RuntimeCVarType::String) {
                value = definition.defaultValue;
            }

            ImGui::PushID(definition.name);
            bool changed = false;
            bool isVideoModeSetting = false;
            const std::string_view definitionName(definition.name);
            const bool isResolutionCustom = [&]() {
                const auto resolutionIt = values.find("resolution");
                if (resolutionIt == values.end()) {
                    return false;
                }
                const std::string& resolutionValue = resolutionIt->second;
                return resolutionValue.empty() || resolutionValue == "custom";
            }();
            if (definitionName.rfind("video_mode_", 0) == 0 &&
                definitionName != "video_mode_refresh_rate") {
                isVideoModeSetting = true;
                if (!isResolutionCustom) {
                    ImGui::BeginDisabled();
                }
            }

            ImGui::TableNextRow();
            ImGui::TableSetColumnIndex(0);
            const std::string displayName = FormatDisplayName(definition);
            ImGui::TextUnformatted(displayName.c_str());
            if (definition.description && definition.description[0] != '\0' &&
                ImGui::IsItemHovered()) {
                ImGui::SetTooltip("%s", definition.description);
            }

            ImGui::TableSetColumnIndex(1);
            const float valueWidth = ImGui::GetContentRegionAvail().x;
            ImGui::SetNextItemWidth(valueWidth);

            if (definition.type == RuntimeCVarType::Bool) {
                bool boolValue = NormalizeBoolValue(value) == "true";
                if (ImGui::Checkbox("##value", &boolValue)) {
                    value = boolValue ? "true" : "false";
                    changed = true;
                }
            } else if (definition.type == RuntimeCVarType::Int) {
                int intValue = ParseIntValue(value, definition.defaultValue);
                if (ImGui::InputInt("##value", &intValue)) {
                    value = std::to_string(intValue);
                    changed = true;
                }
            } else if (definition.type == RuntimeCVarType::Double) {
                double doubleValue = ParseDoubleValue(value, definition.defaultValue);
                if (ImGui::InputDouble("##value", &doubleValue, 0.1, 1.0, "%.3f")) {
                    std::ostringstream stream;
                    stream << doubleValue;
                    value = stream.str();
                    changed = true;
                }
            } else if (definition.type == RuntimeCVarType::String &&
                       definition.allowedValues != nullptr &&
                       definition.allowedValues[0] != '\0') {
                const auto options = ParseAllowedValues(definition.allowedValues);
                int selectedIndex = -1;
                std::string displayValue = value.empty() ? definition.defaultValue : value;
                if (definitionName == "renderer_backend" && displayValue == "native") {
                    displayValue = "xenia";
                }
                if (definitionName == "resolution" && displayValue.empty()) {
                    displayValue = "custom";
                }
                for (std::size_t i = 0; i < options.size(); ++i) {
                    if (options[i] == displayValue) {
                        selectedIndex = static_cast<int>(i);
                        break;
                    }
                }
                if (definitionName == "resolution" && selectedIndex == -1) {
                    const auto customIt = std::find(
                        options.begin(), options.end(), std::string_view{"custom"});
                    if (customIt != options.end()) {
                        selectedIndex = 0;
                        displayValue = "custom";
                    }
                }

                if (ImGui::BeginCombo("##value", displayValue.c_str())) {
                    for (std::size_t i = 0; i < options.size(); ++i) {
                        const std::string option(options[i]);
                        const bool isSelected =
                            static_cast<int>(i) == selectedIndex;
                        if (ImGui::Selectable(option.c_str(), isSelected)) {
                            std::string selectedValue = option;
                            if (definitionName == "resolution" &&
                                option == "custom") {
                                selectedValue.clear();
                            }

                            if (value != selectedValue) {
                                value = std::move(selectedValue);
                                selectedIndex = static_cast<int>(i);
                                displayValue = option;
                                changed = true;
                            }
                        }
                        if (isSelected) {
                            ImGui::SetItemDefaultFocus();
                        }
                    }
                    ImGui::EndCombo();
                }
            } else {
                auto& buffer = textBuffers[definition.name];
                if (ImGui::InputText("##value", buffer.data(), buffer.size(),
                                     ImGuiInputTextFlags_EnterReturnsTrue)) {
                    value = buffer.data();
                    changed = true;
                }
                if (ImGui::IsItemDeactivatedAfterEdit()) {
                    value = buffer.data();
                    changed = true;
                }
            }


            if (isVideoModeSetting && !isResolutionCustom) {
                ImGui::EndDisabled();
            }

            ImGui::TableSetColumnIndex(2);
            if (ImGui::SmallButton("Default")) {
                value = definition.defaultValue;
                if (definition.type == RuntimeCVarType::String) {
                    CopyToBuffer(textBuffers[definition.name], value);
                }
                changed = true;
            }

            if (changed) {
                LinkPlumeResolutionSettings(definition, values);
                Save();
            }
            ImGui::PopID();
        }

        ImGui::EndTable();
        ImGui::TreePop();
    }
    ImGui::EndChild();
}
