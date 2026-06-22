#include "installer/installer.h"

#include "installer/xiso_extractor.h"
#include "extractor/bnk_extractor.h"
#include "extractor/default_xex_icon_extractor.h"
#include "inimanager.h"
#include "launcherpaths.h"
#include "platform.h"
#include "runtime_settings.h"
#include "tools/native/native_lua_decompiler.h"
#include "shader/shader_sbk_pipeline.h"
#include "versionmanager.h"

#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iomanip>
#include <iostream>
#include <ostream>
#include <sstream>
#include <string_view>
#include <thread>
#include <vector>

namespace {

using InstallStageCallback = std::function<void(const std::string&)>;
using TransientStatusCallback = std::function<void(const std::string&)>;

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

std::string GetRepositoryName() {
    return INI::GetString("Name", "Fable2Recomp/refii", "Github");
}

std::string GetRepositoryBranch() {
    return INI::GetString("Branch", "master", "Github");
}

std::string GetGameDataDirectoryName() {
    return INI::GetString("GameDataDirectory", "game", "Game");
}

std::string GetUpdateDirectoryName() {
    return INI::GetString("UpdateDirectory", "update", "Game");
}

std::string GetGameMarkerRelativePath() {
    return INI::GetString("GameMarkerFile", "game/default.xexp", "Game");
}

std::string Trim(std::string value) {
    const auto isWhitespace = [](unsigned char character) {
        return std::isspace(character) != 0;
    };

    value.erase(
        value.begin(),
        std::find_if(value.begin(), value.end(), [&](unsigned char character) {
            return !isWhitespace(character);
        }));
    value.erase(
        std::find_if(value.rbegin(), value.rend(), [&](unsigned char character) {
            return !isWhitespace(character);
        }).base(),
        value.end());
    return value;
}

std::vector<std::string> SplitList(const std::string& value, char delimiter) {
    std::vector<std::string> entries;
    std::stringstream stream(value);
    std::string entry;
    while (std::getline(stream, entry, delimiter)) {
        entry = Trim(entry);
        if (!entry.empty()) {
            entries.push_back(entry);
        }
    }
    return entries;
}

std::string GetManagedFilesConfigValue() {
    if (!INI::GetBool("EnableDownload", "Github", false)) {
        return "";
    }

    const std::string configuredValue =
        GetPlatformAwareString("ManagedFiles", "", "Github");
    if (!configuredValue.empty()) {
        return configuredValue;
    }

    return GetPlatformAwareString("ExeRelativePath", "", "Github");
}

std::string LowercasePathString(const std::filesystem::path& path) {
    std::string value = path.generic_string();
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char character) {
        return static_cast<char>(std::tolower(character));
    });
    return value;
}

std::string LowercaseAscii(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char character) {
        return static_cast<char>(std::tolower(character));
    });
    return value;
}

std::filesystem::path LowercaseRelativePath(const std::filesystem::path& path) {
    return std::filesystem::path(LowercasePathString(path));
}

bool IsWindowsReservedName(std::string_view name) {
    if (name.empty()) {
        return false;
    }
    if (name == "con" || name == "prn" || name == "aux" || name == "nul" ||
        name == "clock$") {
        return true;
    }
    if ((name.size() == 4 && name[0] == 'c' && name[1] == 'o' && name[2] == 'm' &&
         std::isdigit(static_cast<unsigned char>(name[3]))) ||
        (name.size() == 4 && name[0] == 'l' && name[1] == 'p' && name[2] == 't' &&
         std::isdigit(static_cast<unsigned char>(name[3])))) {
        return true;
    }
    return false;
}

bool IsWindowsCompatibleFilename(std::string_view filename) {
    if (filename.empty()) {
        return false;
    }

    for (unsigned char ch : filename) {
        if (ch <= 0x1F || ch == '<' || ch == '>' || ch == ':' || ch == '"' || ch == '/' ||
            ch == '\\' || ch == '|' || ch == '?' || ch == '*') {
            return false;
        }
    }
    if (filename.back() == ' ' || filename.back() == '.') {
        return false;
    }
    return !IsWindowsReservedName(LowercaseAscii(std::string(filename)));
}

bool IsSafeRelativePath(const std::filesystem::path& path, std::string* error) {
    const std::filesystem::path candidate = path.relative_path().lexically_normal();
    if (candidate.empty()) {
        if (error != nullptr) {
            *error = "invalid output path: empty";
        }
        return false;
    }
    for (const auto& component : candidate) {
        const std::string part = component.generic_string();
        if (part.empty() || part == "." || part == "..") {
            if (error != nullptr) {
                *error = "invalid output path component: " + part;
            }
            return false;
        }
        if (!IsWindowsCompatibleFilename(part)) {
            if (error != nullptr) {
                *error = "output path component not compatible with Windows: " + part;
            }
            return false;
        }
    }
    return true;
}

bool IsSafeDestinationUnderBase(const std::filesystem::path& destinationPath,
                                const std::filesystem::path& basePath,
                                std::string* error) {
    const std::filesystem::path normalizedBase =
        std::filesystem::absolute(basePath).lexically_normal();
    const std::filesystem::path normalizedDestination =
        std::filesystem::absolute(destinationPath).lexically_normal();
    const std::filesystem::path relativeDestination =
        normalizedDestination.lexically_relative(normalizedBase).lexically_normal();

    if (relativeDestination.empty()) {
        if (error != nullptr) {
            *error = "destination path is outside the install root: " +
                     normalizedDestination.string();
        }
        return false;
    }

    for (const auto& component : relativeDestination) {
        const std::string part = component.generic_string();
        if (part == "..") {
            if (error != nullptr) {
                *error = "destination path is outside the install root: " +
                         normalizedDestination.string();
            }
            return false;
        }
    }

    return IsSafeRelativePath(relativeDestination, error);
}

bool HasExtension(const std::filesystem::path& path, const std::string& extension) {
    std::string pathExtension = path.extension().generic_string();
    std::transform(pathExtension.begin(), pathExtension.end(), pathExtension.begin(),
                   [](unsigned char character) {
                       return static_cast<char>(std::tolower(character));
                   });
    return pathExtension == extension;
}

bool IsLuaBankRelativePath(const std::filesystem::path& relativePath) {
    const std::string normalized = LowercasePathString(relativePath.lexically_normal());
    return normalized == "gamescripts.bnk" || normalized == "gamescripts_r.bnk" ||
           normalized == "guiscripts.bnk";
}

std::vector<std::filesystem::path> GetLuaBankRelativePaths() {
    return {
        std::filesystem::path("gamescripts.bnk"),
        std::filesystem::path("gamescripts_r.bnk"),
        std::filesystem::path("guiscripts.bnk"),
    };
}

std::vector<std::filesystem::path> GetLuaLooseDirectoryPaths() {
    return {
        std::filesystem::path("scripts"),
        std::filesystem::path("scripts_r"),
    };
}

bool IsLuaLooseRelativePath(const std::filesystem::path& relativePath) {
    const std::string normalized = LowercasePathString(relativePath.lexically_normal());
    return normalized == "scripts" || normalized == "scripts_r" ||
           normalized.rfind("scripts/", 0) == 0 ||
           normalized.rfind("scripts_r/", 0) == 0;
}

bool IsDirectLooseVfsRelativePath(const std::filesystem::path& relativePath) {
    const std::string normalized = LowercasePathString(relativePath.lexically_normal());
    if (normalized.empty() || IsLuaLooseRelativePath(relativePath)) {
        return false;
    }

    if (normalized == "dir.manifest" || normalized == "startup.vfsconfig" ||
        normalized == "fable2_anims.animation_toc" ||
        normalized == "fable2_anims.animation_data" ||
        normalized == "scenarios.list") {
        return true;
    }

    const std::filesystem::path normalizedPath(normalized);
    const std::string extension = normalizedPath.extension().generic_string();
    return extension == ".adb" || extension == ".amp" ||
           extension == ".babel" || extension == ".bft" ||
           extension == ".bgf" || extension == ".bik" ||
           extension == ".bnk" || extension == ".bsg" ||
           extension == ".csv" ||
           extension == ".dds" || extension == ".fac" ||
           extension == ".gdb" ||
           extension == ".ini" || extension == ".list" ||
           extension == ".loco" || extension == ".manifest" ||
           extension == ".sbk" || extension == ".swf" ||
           extension == ".vfsconfig" || extension == ".xml";
}

std::uint32_t ReadBigEndianU32(const unsigned char* bytes) {
    return (static_cast<std::uint32_t>(bytes[0]) << 24) |
           (static_cast<std::uint32_t>(bytes[1]) << 16) |
           (static_cast<std::uint32_t>(bytes[2]) << 8) |
           static_cast<std::uint32_t>(bytes[3]);
}

bool IsExtractableGameBank(const std::filesystem::path& bankPath) {
    std::ifstream stream(bankPath, std::ios::binary);
    if (!stream) {
        return false;
    }

    unsigned char header[8]{};
    stream.read(reinterpret_cast<char*>(header), sizeof(header));
    if (stream.gcount() != static_cast<std::streamsize>(sizeof(header))) {
        return false;
    }

    std::error_code error;
    const std::uintmax_t fileSize = std::filesystem::file_size(bankPath, error);
    if (error) {
        return false;
    }

    const std::uint32_t headerPointer = ReadBigEndianU32(header);
    const std::uint32_t formatWord = ReadBigEndianU32(header + 4);
    return (formatWord == 2 || formatWord == 3) && headerPointer <= fileSize;
}

std::vector<std::filesystem::path> CollectGameBankRelativePaths(
    const std::filesystem::path& gameContentRoot) {
    std::vector<std::filesystem::path> banks;
    std::error_code error;
    constexpr auto options = std::filesystem::directory_options::skip_permission_denied;
    for (std::filesystem::recursive_directory_iterator it(gameContentRoot, options, error), end;
         it != end; it.increment(error)) {
        if (error) {
            error.clear();
            continue;
        }
        if (!it->is_regular_file(error) || !HasExtension(it->path(), ".bnk")) {
            error.clear();
            continue;
        }

        std::filesystem::path relativePath = it->path().lexically_relative(gameContentRoot);
        if (relativePath.empty() || IsLuaBankRelativePath(relativePath) ||
            !IsExtractableGameBank(it->path())) {
            continue;
        }
        banks.push_back(relativePath.lexically_normal());
    }

    std::sort(banks.begin(), banks.end(), [](const auto& lhs, const auto& rhs) {
        return LowercasePathString(lhs) < LowercasePathString(rhs);
    });
    return banks;
}

bool CopyLooseGameContentFiles(const std::filesystem::path& gameContentRoot,
                               const std::filesystem::path& outputRoot,
                               std::size_t* copiedFiles,
                               std::string* error) {
    if (copiedFiles != nullptr) {
        *copiedFiles = 0;
    }

    std::error_code iteratorError;
    constexpr auto options = std::filesystem::directory_options::skip_permission_denied;
    for (std::filesystem::recursive_directory_iterator it(gameContentRoot, options, iteratorError),
         end;
         it != end; it.increment(iteratorError)) {
        if (iteratorError) {
            iteratorError.clear();
            continue;
        }
        if (!it->is_regular_file(iteratorError)) {
            iteratorError.clear();
            continue;
        }

        const std::filesystem::path relativePath =
            it->path().lexically_relative(gameContentRoot).lexically_normal();
        if (relativePath.empty()) {
            continue;
        }
        if (!IsSafeRelativePath(relativePath, error)) {
            return false;
        }
        if (HasExtension(it->path(), ".bnk") &&
            (IsLuaBankRelativePath(relativePath) || IsExtractableGameBank(it->path()))) {
            continue;
        }
        if (!IsDirectLooseVfsRelativePath(relativePath)) {
            continue;
        }

        const std::filesystem::path destination = outputRoot / LowercaseRelativePath(relativePath);
        try {
            std::filesystem::create_directories(destination.parent_path());
            std::filesystem::copy_file(
                it->path(), destination, std::filesystem::copy_options::overwrite_existing);
        } catch (const std::exception& exception) {
            if (error != nullptr) {
                *error = "failed to copy loose game file " + relativePath.generic_string() +
                         ": " + exception.what();
            }
            return false;
        }

        if (copiedFiles != nullptr) {
            ++*copiedFiles;
        }
    }

    return true;
}

bool RemoveStaleUnpackedBankFiles(const std::filesystem::path& outputRoot,
                                  std::size_t* removedFiles,
                                  std::string* error) {
    if (removedFiles != nullptr) {
        *removedFiles = 0;
    }

    std::error_code existsError;
    if (!std::filesystem::exists(outputRoot, existsError)) {
        return true;
    }

    std::error_code iteratorError;
    constexpr auto options = std::filesystem::directory_options::skip_permission_denied;
    for (std::filesystem::recursive_directory_iterator it(outputRoot, options, iteratorError),
         end;
         it != end; it.increment(iteratorError)) {
        if (iteratorError) {
            iteratorError.clear();
            continue;
        }
        if (!it->is_regular_file(iteratorError) || !HasExtension(it->path(), ".bnk")) {
            iteratorError.clear();
            continue;
        }

        std::error_code removeError;
        std::filesystem::remove(it->path(), removeError);
        if (removeError) {
            if (error != nullptr) {
                *error = "failed to remove stale unpacked bank " + it->path().string() +
                         ": " + removeError.message();
            }
            return false;
        }

        if (removedFiles != nullptr) {
            ++*removedFiles;
        }
    }

    return true;
}

bool RemoveStaleScriptBankRootDirectories(const std::filesystem::path& outputRoot,
                                          std::size_t* removedDirectories,
                                          std::string* error) {
    if (removedDirectories != nullptr) {
        *removedDirectories = 0;
    }

    for (const std::filesystem::path& luaBank : GetLuaBankRelativePaths()) {
        const std::filesystem::path staleRoot =
            refii::bnk::GetBankOutputRoot(outputRoot, luaBank);
        std::error_code existsError;
        if (!std::filesystem::exists(staleRoot, existsError)) {
            continue;
        }
        if (existsError) {
            if (error != nullptr) {
                *error = "failed to inspect stale script-bank directory " +
                         staleRoot.string() + ": " + existsError.message();
            }
            return false;
        }

        std::error_code removeError;
        std::filesystem::remove_all(staleRoot, removeError);
        if (removeError) {
            if (error != nullptr) {
                *error = "failed to remove stale script-bank directory " +
                         staleRoot.string() + ": " + removeError.message();
            }
            return false;
        }

        if (removedDirectories != nullptr) {
            ++*removedDirectories;
        }
    }

    return true;
}

ManagedDownloadFile ParseManagedDownloadFile(const std::string& entry) {
    ManagedDownloadFile file;

    const std::size_t separatorPos = entry.find('|');
    if (separatorPos == std::string::npos) {
        file.repositoryRelativePath = Trim(entry);
        file.localRelativePath =
            std::filesystem::path(file.repositoryRelativePath).filename();
    } else {
        file.repositoryRelativePath = Trim(entry.substr(0, separatorPos));
        file.localRelativePath = Trim(entry.substr(separatorPos + 1));
    }

    file.versionKey = file.localRelativePath.generic_string();
    return file;
}

std::string BuildRawDownloadUrl(const std::string& repositoryRelativePath) {
    return "https://github.com/" + GetRepositoryName() + "/raw/" +
           GetRepositoryBranch() + "/" + repositoryRelativePath;
}

std::vector<std::string> GetShaderCompileTargets() {
    std::vector<std::string> targets;
    targets.push_back("vulkan");
#if defined(_WIN32)
    targets.push_back("d3d12");
#endif
    return targets;
}

bool EnsureDirectoryRemovedIfExists(const std::filesystem::path& path, std::string* error) {
    std::error_code errorCode;
    if (!std::filesystem::exists(path, errorCode)) {
        if (errorCode) {
            if (error != nullptr) {
                *error = "Unable to query shader workspace path " + path.string() + ": " +
                         errorCode.message();
            }
            return false;
        }
        return true;
    }

    std::error_code removeError;
    std::filesystem::remove_all(path, removeError);
    if (removeError) {
        if (error != nullptr) {
            *error = "Failed to remove shader workspace path " + path.string() + ": " +
                     removeError.message();
        }
        return false;
    }
    return true;
}

std::filesystem::path GetRuntimeConfigPathInternal() {
    return LauncherPaths::ResolveFromLauncherDirectory("refii.toml");
}

std::string BuildLuaWorkspaceProgressMessage(
    const refii::lua::LuaWorkspaceDecompileProgress& progress) {
    auto summarize_path = [](const std::filesystem::path& path) {
        if (path.empty()) {
            return std::string();
        }

        std::vector<std::string> components;
        for (const auto& part : path) {
            const std::string component = part.generic_string();
            if (!component.empty() && component != ".") {
                components.push_back(component);
            }
        }

        if (components.size() <= 3) {
            return path.generic_string();
        }

        return components[components.size() - 3] + "/" + components[components.size() - 2] +
               "/" + components[components.size() - 1];
    };

    std::ostringstream stream;
    if (progress.phase == "exporting_workspace") {
        stream << "Exporting Lua sources...";
    } else if (progress.phase == "finalizing_manifest") {
        stream << "Finalizing Lua manifest...";
    } else if (progress.phase == "writing_timing_report") {
        stream << "Writing Lua timing report...";
    } else if (progress.phase == "snapshotting_baseline") {
        stream << "Snapshotting Lua baseline...";
    } else if (progress.phase == "verifying_workspace") {
        stream << "Verifying Lua workspace...";
    } else if (progress.phase == "resetting_workspace") {
        stream << "Resetting Lua workspace...";
    } else if (progress.phase == "extracting_banks") {
        stream << "Unpacking game banks...";
    } else if (progress.phase == "building_vfs_database") {
        stream << "Building VFS database...";
    } else if (progress.phase == "disassembling_fallback") {
        stream << "Disassembling Lua fallback...";
    } else {
        stream << "Decompiling Lua scripts...";
    }

    if (progress.total_files != 0) {
        stream << ' ' << progress.completed_files << '/' << progress.total_files;
    }

    if (!progress.current_input.empty()) {
        stream << " (" << summarize_path(progress.current_input) << ")";
    }

    return stream.str();
}

std::string BuildShaderSbkProgressMessage(const refii::shader::ShaderSbkProgress& progress) {
    auto summarize_path = [](const std::filesystem::path& path) {
        if (path.empty()) {
            return std::string();
        }
        const std::string filename = path.filename().generic_string();
        if (!filename.empty()) {
            return filename;
        }
        return path.generic_string();
    };

    std::ostringstream stream;
    if (progress.phase == "extracting_bank") {
        stream << "Parsing shader bank ";
        stream << progress.bank_name;
        if (progress.total_banks != 0) {
            stream << " (" << progress.bank_index << '/' << progress.total_banks << ')';
        }
    } else if (progress.phase == "bank_ready") {
        stream << "Extracted " << progress.bank_name << " bank blobs";
    } else if (progress.phase == "extracting_blobs") {
        stream << "Extracting shader blobs from " << progress.bank_name;
    } else if (progress.phase == "compiling_shader" || progress.phase == "translating_shader") {
        stream << "Compiling " << summarize_path(progress.current_input) << " ["
               << progress.target << "]";
        if (progress.stage != "unknown") {
            stream << " (" << progress.stage << ')';
        }
        if (progress.total_items != 0) {
            stream << " (" << (progress.completed_items + 1) << '/' << progress.total_items << ')';
        }
    } else if (progress.phase == "compile_complete" || progress.phase == "translation_complete") {
        stream << "Shader compile complete";
    } else {
        stream << "Compiling shaders...";
    }
    return stream.str();
}

bool BuildLuaWorkspace(const std::filesystem::path& gameInstallRoot,
                       std::string* error,
                       const TransientStatusCallback& transientStatusCallback) {
    refii::lua::LuaWorkspaceBuildOptions options;
    options.game_install_root = gameInstallRoot;
    options.workspace_root = refii::lua::GetLuaWorkspacePath(gameInstallRoot);
    options.worker_count = 0;
    options.progress_update_stride = 1;
    if (transientStatusCallback) {
        options.progress_callback =
            [transientStatusCallback](const refii::lua::LuaWorkspaceDecompileProgress& progress) {
                transientStatusCallback(BuildLuaWorkspaceProgressMessage(progress));
            };
    }

    refii::lua::LuaWorkspaceBuildSummary summary;
    if (!refii::lua::BuildLuaWorkspace(options, &summary, error)) {
        return false;
    }

    std::cerr << "Lua workspace built at " << summary.output_root << " ("
              << summary.decompile_summary.decompiled << " decompiled, "
              << summary.decompile_summary.aliased << " aliased, "
              << summary.decompile_summary.nonexact << " nonexact, "
              << summary.decompile_summary.aliased_nonexact << " aliased_nonexact, "
              << summary.decompile_summary.disassembled << " disassembled, "
              << summary.decompile_summary.invalid << " invalid, "
              << summary.decompile_summary.budget_exceeded << " budget_exceeded, "
              << summary.decompile_summary.copied << " copied, "
              << summary.decompile_summary.skipped << " skipped, "
              << summary.baseline_files << " baseline file(s))\n";
    return true;
}

void LogLuaWorkspaceVerifySummary(const refii::lua::LuaWorkspaceVerifySummary& summary) {
    std::cerr << "Lua workspace verify: " << summary.ok << " ok, "
              << summary.modified << " modified, " << summary.missing << " missing, "
              << summary.extra << " extra, " << summary.baseline_missing
              << " baseline_missing, " << summary.baseline_corrupt
              << " baseline_corrupt\n";
    for (const refii::lua::LuaWorkspaceVerifyEntry& entry : summary.entries) {
        if (entry.status == refii::lua::LuaWorkspaceVerifyStatus::kOk) {
            continue;
        }
        std::cerr << "  " << refii::lua::DescribeLuaWorkspaceVerifyStatus(entry.status);
        if (!entry.relative_path.empty()) {
            std::cerr << ": " << entry.relative_path.generic_string();
        }
        std::cerr << "\n";
    }
}

void ReportInstallStage(const InstallStageCallback& callback, const std::string& message) {
    std::cout << message << std::endl;
    if (callback) {
        callback(message);
    }
}

bool RebuildLuaWorkspaceInternal(
    std::string* error,
    const InstallStageCallback& stageCallback,
    const TransientStatusCallback& transientStatusCallback) {
    std::string configError;
    if (!Installer::EnsureRuntimeConfigFile(&configError)) {
        std::cerr << "warning: failed to write default runtime config: " << configError << "\n";
    }

    ReportInstallStage(stageCallback, "Preparing Lua workspace...");
    std::string decompileError;
    if (!BuildLuaWorkspace(
            Installer::GetGameDataDirectoryPath(), &decompileError, transientStatusCallback)) {
        const std::string resolved_error =
            decompileError.empty() ? "failed to build clear-text Lua workspace"
                                   : decompileError;
        std::cerr << "warning: failed to build clear-text Lua workspace: " << resolved_error
                  << "\n";
        if (error != nullptr) {
            *error = resolved_error;
        }
        ReportInstallStage(stageCallback, "Lua workspace rebuild failed.");
        return false;
    }

    ReportInstallStage(stageCallback, "Lua workspace ready.");
    return true;
}

bool CopyLuaLooseScriptsForVfs(const std::filesystem::path& gameContentRoot,
                               const std::filesystem::path& outputRoot,
                               std::size_t* copiedFiles,
                               std::string* error) {
    if (copiedFiles != nullptr) {
        *copiedFiles = 0;
    }

    for (const std::filesystem::path& looseDirectory : GetLuaLooseDirectoryPaths()) {
        const std::filesystem::path sourceRoot = gameContentRoot / looseDirectory;
        std::error_code statusError;
        if (!std::filesystem::is_directory(sourceRoot, statusError)) {
            continue;
        }

        std::error_code iteratorError;
        constexpr auto options = std::filesystem::directory_options::skip_permission_denied;
        for (std::filesystem::recursive_directory_iterator it(sourceRoot, options, iteratorError),
             end;
             it != end; it.increment(iteratorError)) {
            if (iteratorError) {
                iteratorError.clear();
                continue;
            }
            if (!it->is_regular_file(iteratorError)) {
                iteratorError.clear();
                continue;
            }

            const std::filesystem::path sourceRelative =
                it->path().lexically_relative(sourceRoot).lexically_normal();
            const std::filesystem::path looseRelative =
                (looseDirectory / sourceRelative).lexically_normal();
            if (!IsSafeRelativePath(looseRelative, error)) {
                return false;
            }

            const std::filesystem::path destination =
                outputRoot / "luasource" / looseRelative;
            try {
                std::filesystem::create_directories(destination.parent_path());
                std::filesystem::copy_file(
                    it->path(), destination, std::filesystem::copy_options::overwrite_existing);
            } catch (const std::exception& exception) {
                if (error != nullptr) {
                    *error = "failed to copy loose Lua source " +
                             looseRelative.generic_string() + ": " + exception.what();
                }
                return false;
            }

            if (copiedFiles != nullptr) {
                ++*copiedFiles;
            }
        }
    }

    return true;
}

bool ExtractLuaSourceBanksForVfs(const std::filesystem::path& gameContentRoot,
                                 const std::filesystem::path& outputRoot,
                                 std::size_t* extractedFiles,
                                 std::string* error,
                                 const TransientStatusCallback& transientStatusCallback) {
    if (extractedFiles != nullptr) {
        *extractedFiles = 0;
    }

    refii::bnk::BankExtractionOptions options;
    options.game_content_root = gameContentRoot;
    options.output_root = outputRoot / "luasource";
    options.bank_relative_paths = GetLuaBankRelativePaths();
    options.clean_output = true;
    options.build_vfs_database = false;
    if (transientStatusCallback) {
        options.progress_callback =
            [transientStatusCallback](const refii::bnk::BankExtractionProgress& progress) {
                refii::lua::LuaWorkspaceDecompileProgress lua_progress;
                lua_progress.phase = progress.phase;
                lua_progress.completed_files = progress.completed_banks;
                lua_progress.total_files = progress.total_banks;
                lua_progress.current_input = progress.current_bank;
                transientStatusCallback(BuildLuaWorkspaceProgressMessage(lua_progress));
            };
    }

    refii::bnk::BankExtractionSummary summary;
    std::string extractionError;
    if (!refii::bnk::ExtractBanks(options, &summary, &extractionError)) {
        if (error != nullptr) {
            *error = extractionError.empty()
                         ? "failed to unpack script banks into lua source"
                         : extractionError;
        }
        return false;
    }

    if (extractedFiles != nullptr) {
        *extractedFiles = summary.extracted_files;
    }
    return true;
}

bool ExtractGeneralGameBanksInternal(
    std::string* error,
    const InstallStageCallback& stageCallback,
    const TransientStatusCallback& transientStatusCallback) {
    ReportInstallStage(stageCallback, "Preparing game bank extraction...");

    const std::filesystem::path gameContentRoot = Installer::GetGameContentDirectoryPath();
    const std::filesystem::path outputRoot = Installer::GetLuaWorkspacePath();

    ReportInstallStage(stageCallback, "Cleaning stale bank archives...");
    std::size_t removedBankFiles = 0;
    if (!RemoveStaleUnpackedBankFiles(outputRoot, &removedBankFiles, error)) {
        ReportInstallStage(stageCallback, "Bank archive cleanup failed.");
        return false;
    }
    std::size_t removedScriptBankDirectories = 0;
    if (!RemoveStaleScriptBankRootDirectories(
            outputRoot, &removedScriptBankDirectories, error)) {
        ReportInstallStage(stageCallback, "Script-bank root cleanup failed.");
        return false;
    }

    ReportInstallStage(stageCallback, "Copying loose game files...");
    std::size_t copiedFiles = 0;
    if (!CopyLooseGameContentFiles(gameContentRoot, outputRoot, &copiedFiles, error)) {
        ReportInstallStage(stageCallback, "Loose game file copy failed.");
        return false;
    }

    ReportInstallStage(stageCallback, "Extracting script-bank lua source...");
    std::size_t extractedLuaSourceFiles = 0;
    if (!ExtractLuaSourceBanksForVfs(gameContentRoot,
                                     outputRoot,
                                     &extractedLuaSourceFiles,
                                     error,
                                     transientStatusCallback)) {
        ReportInstallStage(stageCallback, "Script-bank lua source extraction failed.");
        return false;
    }
    std::size_t copiedLuaSourceFiles = 0;
    if (!CopyLuaLooseScriptsForVfs(
            gameContentRoot, outputRoot, &copiedLuaSourceFiles, error)) {
        ReportInstallStage(stageCallback, "Loose lua source copy failed.");
        return false;
    }

    std::vector<std::filesystem::path> bankRelativePaths =
        CollectGameBankRelativePaths(gameContentRoot);
    if (bankRelativePaths.empty()) {
        if (error != nullptr) {
            *error = "no game banks were found under " + gameContentRoot.string();
        }
        ReportInstallStage(stageCallback, "Game bank extraction failed.");
        return false;
    }

    refii::bnk::BankExtractionOptions options;
    options.game_content_root = gameContentRoot;
    options.output_root = outputRoot;
    options.bank_relative_paths = std::move(bankRelativePaths);
    options.clean_output = true;
    if (transientStatusCallback) {
        options.progress_callback =
            [transientStatusCallback](const refii::bnk::BankExtractionProgress& progress) {
                refii::lua::LuaWorkspaceDecompileProgress lua_progress;
                lua_progress.phase = progress.phase;
                lua_progress.completed_files = progress.completed_banks;
                lua_progress.total_files = progress.total_banks;
                lua_progress.current_input = progress.current_bank;
                transientStatusCallback(BuildLuaWorkspaceProgressMessage(lua_progress));
            };
    }

    refii::bnk::BankExtractionSummary summary;
    std::string extractionError;
    if (!refii::bnk::ExtractBanks(options, &summary, &extractionError)) {
        const std::string resolved_error =
            extractionError.empty() ? "failed to unpack game banks" : extractionError;
        if (error != nullptr) {
            *error = resolved_error;
        }
        ReportInstallStage(stageCallback, "Game bank extraction failed.");
        return false;
    }

    std::cerr << "Game banks unpacked at " << summary.output_root << " ("
              << summary.extracted_banks << " bank(s), " << summary.extracted_files
              << " extracted file(s), " << copiedFiles << " loose file(s) copied, "
              << extractedLuaSourceFiles << " lua source file(s), "
              << copiedLuaSourceFiles << " loose lua source file(s), "
              << removedBankFiles << " stale bank archive(s) removed, "
              << removedScriptBankDirectories
              << " stale script-bank root(s) removed, "
              << summary.vfs_database_entries << " VFS database row(s), "
              << summary.vfs_unindexed_files << " unindexed VFS file candidate(s), "
              << summary.duplicate_conflicts
              << " differing duplicate conflict(s) routed)\n";
    ReportInstallStage(stageCallback, "Game banks unpacked.");
    return true;
}

bool InstallGameInternal(
    const std::filesystem::path& isoPath,
    const std::filesystem::path& xexpPath,
    std::string* error,
    const InstallStageCallback& stageCallback,
    const TransientStatusCallback& transientStatusCallback) {
    (void)transientStatusCallback;

    ReportInstallStage(stageCallback, "Extracting ISO...");
    if (!Installer::ExtractIso(isoPath, Installer::GetGameDataDirectoryPath(), error)) {
        return false;
    }

    ReportInstallStage(stageCallback, "Copying XEXP...");
    if (!Installer::CopyGameExecutable(xexpPath, Installer::GetGameMarkerPath(), error)) {
        return false;
    }

    ReportInstallStage(stageCallback, "Installation complete.");
    return true;
}

bool ReplaceDownloadedFile(
    const std::filesystem::path& temporaryPath,
    const std::filesystem::path& destinationPath,
    std::string* error) {
    std::string path_error;
    if (!IsSafeDestinationUnderBase(
            destinationPath, Platform::GetExecutableDirectory(), &path_error)) {
        if (error != nullptr) {
            *error = path_error;
        }
        return false;
    }
    try {
        std::filesystem::create_directories(destinationPath.parent_path());

        if (std::filesystem::exists(destinationPath)) {
            std::filesystem::remove(destinationPath);
        }

        std::filesystem::rename(temporaryPath, destinationPath);
        return true;
    } catch (const std::exception&) {
        try {
            std::filesystem::copy_file(
                temporaryPath,
                destinationPath,
                std::filesystem::copy_options::overwrite_existing);
            std::filesystem::remove(temporaryPath);
            return true;
        } catch (const std::exception& exception) {
            if (error) {
                *error = exception.what();
            }
            return false;
        }
    }
}

}  // namespace

namespace Installer {

std::string EnsureTrailingSeparator(std::string path) {
    if (!path.empty() && path.back() != '\\' && path.back() != '/') {
        path += "/";
    }
    return path;
}

std::string GetGameTitle() {
    return INI::GetString("GameTitle", "Fable 2", "About");
}

std::string GetExecutableName() {
    return GetPlatformAwareString("ExeName", Platform::GetDefaultExecutableName(), "Game");
}

std::string GetConfiguredExecutableRelativePath() {
    return GetPlatformAwareString("ExeRelativePath", "", "Github");
}

std::string GetDownloadUrl() {
    const std::string configuredUrl = GetPlatformAwareString("DownloadFile", "", "Github");
    if (!configuredUrl.empty()) {
        return configuredUrl;
    }

    const std::string executableRelativePath = GetConfiguredExecutableRelativePath();
    if (executableRelativePath.empty()) {
        return "";
    }

    return BuildRawDownloadUrl(executableRelativePath);
}

std::string GetDownloadUrlForRelativePath(const std::string& repositoryRelativePath) {
    if (repositoryRelativePath.empty()) {
        return "";
    }

    return BuildRawDownloadUrl(repositoryRelativePath);
}

std::vector<ManagedDownloadFile> GetManagedDownloadFiles() {
    const std::string managedFilesValue = GetManagedFilesConfigValue();
    if (managedFilesValue.empty()) {
        return {};
    }

    std::vector<ManagedDownloadFile> files;
    for (const std::string& entry : SplitList(managedFilesValue, ';')) {
        ManagedDownloadFile file = ParseManagedDownloadFile(entry);
        if (!file.repositoryRelativePath.empty() && !file.localRelativePath.empty()) {
            files.push_back(std::move(file));
        }
    }

    return files;
}

bool HasDownloadSource() {
    return !GetManagedDownloadFiles().empty();
}

bool CanCheckForUpdates() {
    return !GetManagedDownloadFiles().empty();
}

std::filesystem::path GetInstallBasePath() {
    return LauncherPaths::ResolveFromLauncherDirectory(
        INI::GetString("GamePath", "assets/", "Game"));
}

std::filesystem::path GetInstalledExecutablePath() {
    return Platform::GetExecutableDirectory() / GetExecutableName();
}

std::filesystem::path GetInstalledManagedFilePath(const ManagedDownloadFile& file) {
    return Platform::GetExecutableDirectory() / file.localRelativePath;
}

std::filesystem::path GetGameDataDirectoryPath() {
    return GetInstallBasePath() / GetGameDataDirectoryName();
}

std::filesystem::path GetGameContentDirectoryPath() {
    return GetGameDataDirectoryPath() / "data";
}

std::filesystem::path GetUpdateDirectoryPath() {
    return GetInstallBasePath() / GetUpdateDirectoryName();
}

std::filesystem::path GetGameMarkerPath() {
    return GetInstallBasePath() / GetGameMarkerRelativePath();
}

std::filesystem::path GetLuaWorkspacePath() {
    return refii::lua::GetLuaWorkspacePath(GetGameDataDirectoryPath());
}

std::filesystem::path GetRuntimeConfigPath() {
    return GetRuntimeConfigPathInternal();
}

bool EnsureRuntimeConfigFile(std::string* error) {
    return EnsureRuntimeConfigDefaults(GetRuntimeConfigPath(), error);
}

bool HasInstalledBuild() {
    const std::vector<ManagedDownloadFile> files = GetManagedDownloadFiles();
    if (files.empty()) {
        return std::filesystem::exists(GetInstalledExecutablePath());
    }

    for (const ManagedDownloadFile& file : files) {
        if (!std::filesystem::exists(GetInstalledManagedFilePath(file))) {
            return false;
        }
    }

    return true;
}

bool HasExtractedAssets(
    const std::filesystem::path& gameDataDirectory,
    const std::filesystem::path& markerPath) {
    return std::filesystem::exists(gameDataDirectory) && std::filesystem::exists(markerPath);
}

bool UninstallManagedFiles(std::ostream& log) {
    const std::filesystem::path gameDataPath = GetGameDataDirectoryPath();
    const std::filesystem::path updateDataPath = GetUpdateDirectoryPath();
    bool hasRemovedFiles = false;

    for (const ManagedDownloadFile& file : GetManagedDownloadFiles()) {
        const std::filesystem::path installedPath = GetInstalledManagedFilePath(file);
        if (!std::filesystem::exists(installedPath)) {
            continue;
        }

        try {
            std::filesystem::remove(installedPath);
            hasRemovedFiles = true;
            log << "Removed managed runtime file: " << installedPath.string() << std::endl;
        } catch (const std::exception& exception) {
            log << "Error removing managed runtime file " << installedPath.string()
                << ": " << exception.what() << std::endl;
        }
    }

    if (VersionManager::VersionFileExists()) {
        try {
            VersionManager::RemoveVersionFile();
            hasRemovedFiles = true;
            log << "Removed version file." << std::endl;
        } catch (const std::exception& exception) {
            log << "Error removing version file: " << exception.what() << std::endl;
        }
    }

    if (std::filesystem::exists(gameDataPath)) {
        try {
            std::filesystem::remove_all(gameDataPath);
            hasRemovedFiles = true;
            log << "Removed game data directory: " << gameDataPath.string() << std::endl;
        } catch (const std::exception& exception) {
            log << "Error removing game data directory " << gameDataPath.string()
                << ": " << exception.what() << std::endl;
        }
    }

    if (std::filesystem::exists(updateDataPath)) {
        try {
            std::filesystem::remove_all(updateDataPath);
            hasRemovedFiles = true;
            log << "Removed update data directory: " << updateDataPath.string() << std::endl;
        } catch (const std::exception& exception) {
            log << "Error removing update data directory " << updateDataPath.string()
                << ": " << exception.what() << std::endl;
        }
    }

    return hasRemovedFiles;
}

std::string OpenIsoFileDialog(std::string* error) {
    std::string result;
    Platform::SelectIsoFile(result, error);
    return result;
}

std::string OpenXexpFileDialog(std::string* error) {
    std::string result;
    Platform::SelectXexpFile(result, error);
    return result;
}

bool ExtractIso(
    const std::filesystem::path& isoPath,
    const std::filesystem::path& destinationDirectory,
    std::string* error) {
    try {
        if (std::filesystem::exists(destinationDirectory)) {
            std::filesystem::remove_all(destinationDirectory);
        }
        std::filesystem::create_directories(destinationDirectory);
    } catch (const std::exception& exception) {
        if (error) {
            *error = exception.what();
        }
        return false;
    }

    if (!XisoExtractor::ExtractImage(isoPath, destinationDirectory, error)) {
        return false;
    }

    const std::filesystem::path defaultXexPath = destinationDirectory / "default.xex";
    if (std::filesystem::exists(defaultXexPath) &&
        !refii::extractor::ExtractDefaultXexIcons(defaultXexPath, nullptr, error)) {
        return false;
    }

    return true;
}

bool CopyGameExecutable(
    const std::filesystem::path& sourcePath,
    const std::filesystem::path& destinationPath,
    std::string* error) {
    std::string path_error;
    if (!IsSafeDestinationUnderBase(destinationPath, GetInstallBasePath(), &path_error)) {
        if (error != nullptr) {
            *error = path_error;
        }
        return false;
    }
    try {
        if (!std::filesystem::exists(sourcePath)) {
            if (error) {
                *error = "The selected XEXP file does not exist.";
            }
            return false;
        }

        std::filesystem::create_directories(destinationPath.parent_path());
        std::filesystem::copy_file(
            sourcePath,
            destinationPath,
            std::filesystem::copy_options::overwrite_existing);
        return true;
    } catch (const std::exception& exception) {
        if (error) {
            *error = exception.what();
        }
        return false;
    }
}

bool InstallGame(
    const std::filesystem::path& isoPath,
    const std::filesystem::path& xexpPath,
    std::string* error) {
    return InstallGameInternal(isoPath, xexpPath, error, {}, {});
}

bool ExtractGameShadersInternal(
    std::string* error,
    const InstallStageCallback& stageCallback,
    const TransientStatusCallback& transientStatusCallback) {
    const std::vector<std::string> targets = GetShaderCompileTargets();
    const std::filesystem::path gameDataPath = GetGameDataDirectoryPath();
    const std::filesystem::path shaderInputRoot = gameDataPath / "data" / "Shaders";
    const std::filesystem::path shaderOutputRoot = GetLuaWorkspacePath() / "shaders";
    const std::filesystem::path manifestPath = shaderOutputRoot / "extract_manifest.tsv";
    const std::filesystem::path reportPath = shaderOutputRoot / "compile_report.tsv";

    if (!std::filesystem::exists(shaderInputRoot)) {
        if (error != nullptr) {
            *error = "Shader input directory not found: " + shaderInputRoot.string();
        }
        ReportInstallStage(stageCallback, "Shader compile failed.");
        return false;
    }

    const bool hasShadersSbk =
        std::filesystem::exists(shaderInputRoot / "Shaders.sbk") ||
        std::filesystem::exists(shaderInputRoot / "ShadersRelease.sbk");
    if (!hasShadersSbk) {
        if (error != nullptr) {
            *error = "No .sbk files found in " + shaderInputRoot.string();
        }
        ReportInstallStage(stageCallback, "Shader compile failed.");
        return false;
    }

    if (!EnsureDirectoryRemovedIfExists(shaderOutputRoot / "blobs", error)) {
        ReportInstallStage(stageCallback, "Shader compile failed.");
        return false;
    }
    if (!EnsureDirectoryRemovedIfExists(shaderOutputRoot / "logs", error)) {
        ReportInstallStage(stageCallback, "Shader compile failed.");
        return false;
    }
    const std::vector<std::string> targetOutputDirectories = {
        "vulkan",
        "Vulkan",
        "D3D12",
        "d3d12",
    };
    for (const std::string& directoryName : targetOutputDirectories) {
        if (!EnsureDirectoryRemovedIfExists(shaderOutputRoot / directoryName, error)) {
            ReportInstallStage(stageCallback, "Shader compile failed.");
            return false;
        }
    }
    std::error_code createError;
    std::filesystem::create_directories(shaderOutputRoot, createError);
    if (createError) {
        if (error != nullptr) {
            *error = "Failed to create shader output directory " + shaderOutputRoot.string() +
                     ": " + createError.message();
        }
        ReportInstallStage(stageCallback, "Shader compile failed.");
        return false;
    }

    std::string requestedTargets;
    for (std::size_t i = 0; i < targets.size(); ++i) {
        if (i > 0) {
            requestedTargets.push_back(',');
        }
        requestedTargets += targets[i];
    }

    ReportInstallStage(stageCallback, "Preparing shader compile...");
    refii::shader::ShaderSbkBuildOptions buildOptions;
    buildOptions.extract.input_dir = shaderInputRoot;
    buildOptions.extract.extract_out_dir = shaderOutputRoot;
    buildOptions.extract.manifest_path = manifestPath;
    buildOptions.translate.extract_manifest_path = manifestPath;
    buildOptions.translate.translate_out_dir = shaderOutputRoot;
    buildOptions.translate.report_path = reportPath;
    buildOptions.translate.targets = requestedTargets;

    refii::shader::ShaderSbkBuildSummary summary;
    const auto progressCallback = [&](const refii::shader::ShaderSbkProgress& progress) {
        if (transientStatusCallback) {
            transientStatusCallback(BuildShaderSbkProgressMessage(progress));
        }
    };

    bool shaderBuildSucceeded = false;
    try {
        shaderBuildSucceeded = refii::shader::BuildShaderSbkWorkspace(
            buildOptions, &summary, error, progressCallback);
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = "Shader compile crashed with exception: " + std::string(exception.what());
        }
        shaderBuildSucceeded = false;
    } catch (...) {
        if (error != nullptr) {
            *error = "Shader compile crashed with an unknown exception.";
        }
        shaderBuildSucceeded = false;
    }

    if (!shaderBuildSucceeded) {
        ReportInstallStage(stageCallback, "Shader compile failed.");
        return false;
    }

    if (summary.translate.failed > 0) {
        std::cerr << "Warning: " << summary.translate.failed
                  << " shader compile entries failed. See " << reportPath.string() << "\n";
    }

    ReportInstallStage(stageCallback, "Shader compile complete.");
    return true;
}

bool RebuildLuaWorkspace(std::string* error) {
    return RebuildLuaWorkspaceInternal(error, {}, {});
}

bool ExtractGeneralGameBanks(std::string* error) {
    return ExtractGeneralGameBanksInternal(error, {}, {});
}

bool ExtractGameShaders(std::string* error) {
    return ExtractGameShadersInternal(error, {}, {});
}

bool VerifyLuaWorkspace(refii::lua::LuaWorkspaceVerifySummary* summary, std::string* error) {
    refii::lua::LuaWorkspaceVerifyOptions options;
    options.workspace_root = GetLuaWorkspacePath();
    options.include_ok_entries = true;
    const bool success = refii::lua::VerifyLuaWorkspace(options, summary, error);
    if (success && summary != nullptr) {
        LogLuaWorkspaceVerifySummary(*summary);
    }
    return success;
}

bool ResetLuaWorkspace(std::string* error) {
    refii::lua::LuaWorkspaceResetOptions options;
    options.workspace_root = GetLuaWorkspacePath();
    options.reset_all = true;

    refii::lua::LuaWorkspaceResetSummary summary;
    const bool success = refii::lua::ResetLuaWorkspace(options, &summary, error);
    if (success) {
        std::cerr << "Lua workspace reset: " << summary.restored << " restored, "
                  << summary.removed << " removed, " << summary.failed << " failed\n";
    }
    return success;
}

bool ResetLuaWorkspaceFile(const std::filesystem::path& relativePath, std::string* error) {
    refii::lua::LuaWorkspaceResetOptions options;
    options.workspace_root = GetLuaWorkspacePath();
    options.relative_path = relativePath;
    options.reset_all = false;

    refii::lua::LuaWorkspaceResetSummary summary;
    const bool success = refii::lua::ResetLuaWorkspace(options, &summary, error);
    if (success) {
        std::cerr << "Lua workspace file reset: " << relativePath.generic_string() << " ("
                  << summary.restored << " restored, " << summary.removed << " removed)\n";
    }
    return success;
}

void ExtractIsoAsync(
    const std::string& isoPath,
    std::shared_ptr<IsoExtractionProgress> progress) {
    std::thread([isoPath, progress]() {
        progress->isExtracting = true;
        progress->isComplete = false;
        progress->hasError = false;
        progress->errorMessage.clear();
        progress->SetStatusMessage("Extracting ISO...");

        std::string extractionError;
        const bool success = ExtractIso(isoPath, GetGameDataDirectoryPath(), &extractionError);

        progress->isExtracting = false;
        if (success) {
            progress->SetStatusMessage("Extraction complete.");
            progress->isComplete = true;
        } else {
            progress->hasError = true;
            progress->errorMessage = extractionError.empty() ? "ISO extraction failed" : extractionError;
        }
    }).detach();
}

void InstallGameAsync(
    const std::string& isoPath,
    const std::string& xexpPath,
    std::shared_ptr<IsoExtractionProgress> progress) {
    std::thread([isoPath, xexpPath, progress]() {
        progress->isExtracting = true;
        progress->isComplete = false;
        progress->hasError = false;
        progress->errorMessage.clear();
        progress->SetOperationLabel("Installing");
        progress->SetStatusMessage("Starting installation...");

        std::string installError;
        const bool success = InstallGameInternal(
            isoPath,
            xexpPath,
            &installError,
            [progress](const std::string& message) { progress->SetStatusMessage(message); },
            [progress](const std::string& message) { progress->SetStatusMessage(message, false); });

        progress->isExtracting = false;
        if (success) {
            progress->isComplete = true;
        } else {
            progress->hasError = true;
            progress->errorMessage = installError.empty() ? "Game installation failed" : installError;
        }
    }).detach();
}

void RebuildLuaWorkspaceAsync(std::shared_ptr<IsoExtractionProgress> progress) {
    std::thread([progress]() {
        progress->isExtracting = true;
        progress->isComplete = false;
        progress->hasError = false;
        progress->errorMessage.clear();
        progress->SetOperationLabel("Rebuilding Lua Workspace");
        progress->SetStatusMessage("Preparing Lua workspace...");

        std::string rebuildError;
        const bool success = RebuildLuaWorkspaceInternal(
            &rebuildError,
            [progress](const std::string& message) { progress->SetStatusMessage(message); },
            [progress](const std::string& message) { progress->SetStatusMessage(message, false); });

        progress->isExtracting = false;
        if (success) {
            progress->isComplete = true;
        } else {
            progress->hasError = true;
            progress->errorMessage =
                rebuildError.empty() ? "Lua workspace rebuild failed" : rebuildError;
        }
    }).detach();
}

void ExtractGeneralGameBanksAsync(std::shared_ptr<IsoExtractionProgress> progress) {
    std::thread([progress]() {
        progress->isExtracting = true;
        progress->isComplete = false;
        progress->hasError = false;
        progress->errorMessage.clear();
        progress->SetOperationLabel("Unpacking Game Banks");
        progress->SetStatusMessage("Preparing game bank extraction...");

        std::string extractionError;
        const bool success = ExtractGeneralGameBanksInternal(
            &extractionError,
            [progress](const std::string& message) { progress->SetStatusMessage(message); },
            [progress](const std::string& message) { progress->SetStatusMessage(message, false); });

        progress->isExtracting = false;
        if (success) {
            progress->isComplete = true;
        } else {
            progress->hasError = true;
            progress->errorMessage =
                extractionError.empty() ? "Game bank extraction failed" : extractionError;
        }
    }).detach();
}

void ExtractGameShadersAsync(std::shared_ptr<IsoExtractionProgress> progress) {
    std::thread([progress]() {
        progress->isExtracting = true;
        progress->isComplete = false;
        progress->hasError = false;
        progress->errorMessage.clear();
        progress->SetOperationLabel("Compiling Game Shaders");
        progress->SetStatusMessage("Preparing shader compile...");

        std::string extractionError;
        const bool success = ExtractGameShadersInternal(
            &extractionError,
            [progress](const std::string& message) { progress->SetStatusMessage(message); },
            [progress](const std::string& message) { progress->SetStatusMessage(message, false); });

        progress->isExtracting = false;
        if (success) {
            progress->SetStatusMessage("Shader compile complete.");
            progress->isComplete = true;
        } else {
            progress->hasError = true;
            progress->errorMessage =
                extractionError.empty() ? "Shader compile failed" : extractionError;
        }
    }).detach();
}

void VerifyLuaWorkspaceAsync(std::shared_ptr<IsoExtractionProgress> progress) {
    std::thread([progress]() {
        progress->isExtracting = true;
        progress->isComplete = false;
        progress->hasError = false;
        progress->errorMessage.clear();
        progress->SetOperationLabel("Verifying Lua Workspace");
        progress->SetStatusMessage("Verifying Lua workspace...");

        refii::lua::LuaWorkspaceVerifySummary summary;
        refii::lua::LuaWorkspaceVerifyOptions verifyOptions;
        verifyOptions.workspace_root = GetLuaWorkspacePath();
        verifyOptions.include_ok_entries = true;
        verifyOptions.progress_callback =
            [progress](const refii::lua::LuaWorkspaceDecompileProgress& update) {
                progress->SetStatusMessage(BuildLuaWorkspaceProgressMessage(update), false);
            };
        std::string verifyError;
        const bool success =
            refii::lua::VerifyLuaWorkspace(verifyOptions, &summary, &verifyError);

        progress->isExtracting = false;
        if (success) {
            LogLuaWorkspaceVerifySummary(summary);
            progress->SetStatusMessage("Lua workspace verify complete.");
            progress->isComplete = true;
        } else {
            progress->hasError = true;
            progress->errorMessage =
                verifyError.empty() ? "Lua workspace verify failed" : verifyError;
        }
    }).detach();
}

void ResetLuaWorkspaceAsync(std::shared_ptr<IsoExtractionProgress> progress) {
    std::thread([progress]() {
        progress->isExtracting = true;
        progress->isComplete = false;
        progress->hasError = false;
        progress->errorMessage.clear();
        progress->SetOperationLabel("Resetting Lua Workspace");
        progress->SetStatusMessage("Resetting Lua workspace...");

        refii::lua::LuaWorkspaceResetSummary summary;
        refii::lua::LuaWorkspaceResetOptions resetOptions;
        resetOptions.workspace_root = GetLuaWorkspacePath();
        resetOptions.reset_all = true;
        resetOptions.progress_callback =
            [progress](const refii::lua::LuaWorkspaceDecompileProgress& update) {
                progress->SetStatusMessage(BuildLuaWorkspaceProgressMessage(update), false);
            };
        std::string resetError;
        const bool success =
            refii::lua::ResetLuaWorkspace(resetOptions, &summary, &resetError);

        progress->isExtracting = false;
        if (success) {
            std::cerr << "Lua workspace reset: " << summary.restored << " restored, "
                      << summary.removed << " removed, " << summary.failed << " failed\n";
            progress->SetStatusMessage("Lua workspace reset complete.");
            progress->isComplete = true;
        } else {
            progress->hasError = true;
            progress->errorMessage =
                resetError.empty() ? "Lua workspace reset failed" : resetError;
        }
    }).detach();
}

void ResetLuaWorkspaceFileAsync(const std::filesystem::path& relativePath,
                                std::shared_ptr<IsoExtractionProgress> progress) {
    std::thread([relativePath, progress]() {
        progress->isExtracting = true;
        progress->isComplete = false;
        progress->hasError = false;
        progress->errorMessage.clear();
        progress->SetOperationLabel("Resetting Lua File");
        progress->SetStatusMessage("Resetting Lua file...");

        refii::lua::LuaWorkspaceResetSummary summary;
        refii::lua::LuaWorkspaceResetOptions resetOptions;
        resetOptions.workspace_root = GetLuaWorkspacePath();
        resetOptions.relative_path = relativePath;
        resetOptions.reset_all = false;
        resetOptions.progress_callback =
            [progress](const refii::lua::LuaWorkspaceDecompileProgress& update) {
                progress->SetStatusMessage(BuildLuaWorkspaceProgressMessage(update), false);
            };
        std::string resetError;
        const bool success =
            refii::lua::ResetLuaWorkspace(resetOptions, &summary, &resetError);

        progress->isExtracting = false;
        if (success) {
            std::cerr << "Lua workspace file reset: " << relativePath.generic_string() << " ("
                      << summary.restored << " restored, " << summary.removed << " removed)\n";
            progress->SetStatusMessage("Lua file reset complete.");
            progress->isComplete = true;
        } else {
            progress->hasError = true;
            progress->errorMessage =
                resetError.empty() ? "Lua file reset failed" : resetError;
        }
    }).detach();
}

void DownloadManagedFilesAsync(
    const std::vector<ManagedDownloadFile>& files,
    std::shared_ptr<ManagedDownloadProgress> progress,
    std::function<void()> onSuccessCallback) {
    std::thread([files, progress, onSuccessCallback]() {
        progress->isComplete = false;
        progress->hasError = false;
        progress->bytesDownloaded = 0;
        progress->totalBytes = 0;
        progress->currentFileIndex = 0;
        progress->totalFiles = static_cast<int>(files.size());
        progress->SetCurrentFileName("");
        {
            std::lock_guard<std::mutex> lock(progress->mutex);
            progress->errorMessage.clear();
        }

        for (std::size_t index = 0; index < files.size(); ++index) {
            const ManagedDownloadFile& file = files[index];
            progress->currentFileIndex = static_cast<int>(index + 1);
            progress->bytesDownloaded = 0;
            progress->totalBytes = 0;
            progress->SetCurrentFileName(file.localRelativePath.filename().string());

            const std::filesystem::path destinationPath = GetInstalledManagedFilePath(file);
            const std::filesystem::path temporaryPath =
                destinationPath.string() + ".download";

            std::string error;
            const bool downloadSucceeded = Platform::DownloadFile(
                GetDownloadUrlForRelativePath(file.repositoryRelativePath),
                temporaryPath,
                [progress](std::size_t downloaded, std::size_t total) {
                    progress->bytesDownloaded = downloaded;
                    progress->totalBytes = total;
                },
                &error);
            if (!downloadSucceeded) {
                std::error_code ignoredError;
                std::filesystem::remove(temporaryPath, ignoredError);
                progress->hasError = true;
                {
                    std::lock_guard<std::mutex> lock(progress->mutex);
                    progress->errorMessage =
                        error.empty() ? "Download failed" : error;
                }
                progress->isComplete = true;
                return;
            }

            if (!ReplaceDownloadedFile(temporaryPath, destinationPath, &error)) {
                std::error_code ignoredError;
                std::filesystem::remove(temporaryPath, ignoredError);
                progress->hasError = true;
                {
                    std::lock_guard<std::mutex> lock(progress->mutex);
                    progress->errorMessage =
                        error.empty() ? "Failed to replace file" : error;
                }
                progress->isComplete = true;
                return;
            }
        }

        progress->isComplete = true;
        if (onSuccessCallback) {
            onSuccessCallback();
        }
    }).detach();
}

}  // namespace Installer
