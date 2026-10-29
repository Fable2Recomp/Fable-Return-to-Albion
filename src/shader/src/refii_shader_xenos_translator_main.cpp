#include <cctype>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#if defined(__unix__) || defined(__APPLE__)
#include <dlfcn.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>
#elif defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

#include "shader/refii_shader_xenos_translator.h"

#include "xenos_recomp_api.h"

#ifndef REFII_XENOSRECOMP_SHADER_COMMON_PATH
#error "REFII_XENOSRECOMP_SHADER_COMMON_PATH must be defined"
#endif

#ifndef REFII_XENOS_DXC_TOOL_PATH
#error "REFII_XENOS_DXC_TOOL_PATH must be defined"
#endif

#ifndef REFII_XENOS_DXC_LIB_DIR
#error "REFII_XENOS_DXC_LIB_DIR must be defined"
#endif

namespace {

/// The directory this library was loaded from, or empty when the platform cannot say.
std::filesystem::path LibraryDirectory() {
#if defined(_WIN32)
    HMODULE module = nullptr;
    if (!GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCSTR>(&LibraryDirectory), &module) ||
        module == nullptr) {
        return {};
    }
    char path[MAX_PATH] = {};
    const DWORD length = GetModuleFileNameA(module, path, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) {
        return {};
    }
    return std::filesystem::path(path).parent_path();
#else
    Dl_info info{};
    if (dladdr(reinterpret_cast<void*>(&LibraryDirectory), &info) == 0 ||
        info.dli_fname == nullptr) {
        return {};
    }
    return std::filesystem::path(info.dli_fname).parent_path();
#endif
}

/// Where the compiler, its libraries and shader_common.h are at runtime.
///
/// BESIDE THE LIBRARY FIRST. A shipped app stages them as bin/dxc (see
/// cmake/refii_stage_godot.cmake); the compile-time paths below name the dev tree's vendored
/// copies, which exist on no player's machine. A dev tree has no bin/dxc until it is staged,
/// so it falls through to them.
const std::filesystem::path& BundledToolDirectory() {
    static const std::filesystem::path directory = [] {
        const std::filesystem::path library = LibraryDirectory();
        if (library.empty()) {
            return std::filesystem::path{};
        }
        const std::filesystem::path candidate = library / "dxc";
        const std::filesystem::path tool =
            candidate / std::filesystem::path(REFII_XENOS_DXC_TOOL_PATH).filename();
        std::error_code ignored;
        return std::filesystem::exists(tool, ignored) ? candidate : std::filesystem::path{};
    }();
    return directory;
}

std::string DxcToolPath() {
    const std::filesystem::path& bundled = BundledToolDirectory();
    if (bundled.empty()) {
        return REFII_XENOS_DXC_TOOL_PATH;
    }
    return (bundled / std::filesystem::path(REFII_XENOS_DXC_TOOL_PATH).filename()).string();
}

// The compiler's libraries sit beside this library (bin/), where XenosRecomp, which links
// them, is loaded from; the tool under bin/dxc is launched with that directory on its path.
std::string DxcLibraryDirectory() {
    const std::filesystem::path& bundled = BundledToolDirectory();
    return bundled.empty() ? std::string(REFII_XENOS_DXC_LIB_DIR) : bundled.parent_path().string();
}

std::string ShaderCommonPath() {
    const std::filesystem::path& bundled = BundledToolDirectory();
    if (bundled.empty()) {
        return REFII_XENOSRECOMP_SHADER_COMMON_PATH;
    }
    return (bundled / "shader_common.h").string();
}

}  // namespace

struct Options {
    std::filesystem::path input;
    std::string target;
    std::string stage;
    std::string name;
    std::filesystem::path output;
    // Vertex stage only: second dxc pass over the same HLSL with
    // -D REFII_TESSELLATION_VARIANT=1 (the hardware-tessellation variant).
    std::filesystem::path tess_output;
};

std::string ToLowerAscii(std::string value) {
    for (char& ch : value) {
        ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
    }
    return value;
}

std::string TrimAscii(std::string value) {
    std::size_t begin = 0;
    while (begin < value.size() &&
           std::isspace(static_cast<unsigned char>(value[begin])) != 0) {
        ++begin;
    }

    std::size_t end = value.size();
    while (end > begin &&
           std::isspace(static_cast<unsigned char>(value[end - 1])) != 0) {
        --end;
    }
    return value.substr(begin, end - begin);
}

void PrintUsage() {
    std::cerr
        << "usage: refii-shader-xenos-translator --input <blob> --target <vulkan|d3d12|metal> "
           "--stage <vertex|pixel> --name <entry> --output <file>\n";
}

std::optional<std::string> ResolveDxcProfile(std::string_view stage);
bool WriteTextFile(const std::filesystem::path& path,
                  std::string_view text,
                  std::string* error);
bool ReadBinaryFile(const std::filesystem::path& path,
                   std::vector<std::uint8_t>* bytes,
                   std::string* error);
std::string QuoteProcessArgument(const std::string& argument);
void WriteTranslatorLog(const std::filesystem::path& log_path, const std::string& message);
bool RunProcess(const std::vector<std::string>& args,
                const std::vector<std::pair<std::string, std::string>>& env_overrides,
                const std::filesystem::path& log_path,
                int* exit_code,
                std::string* error);
std::vector<std::pair<std::string, std::string>> BuildDxcEnvironment();
bool TryWriteFablePixRenderVideoFramePixelShader(
    const Options& options,
    const std::filesystem::path& input_path,
    const std::filesystem::path& hlsl_output_path,
    std::string* error);
bool TryWriteFableBinkGodotShader(
    const Options& options,
    const std::filesystem::path& input_path,
    const std::filesystem::path& output_path,
    std::string* error);

void WriteTranslatorLog(const std::filesystem::path& log_path, const std::string& message) {
    if (log_path.empty()) {
        return;
    }

    try {
        std::filesystem::create_directories(log_path.parent_path());
    } catch (...) {
        return;
    }

    std::ofstream stream(log_path, std::ios::binary | std::ios::app);
    if (stream) {
        stream << message << '\n';
    }
}

bool TranslateXenosShaderBlobImpl(const Options& options,
                                 const std::filesystem::path& log_path,
                                 std::string* error,
                                 refii::shader::XenosMemoryExportStats* memexport_stats) {
    const std::filesystem::path input_path = std::filesystem::absolute(options.input);
    const std::filesystem::path output_path = std::filesystem::absolute(options.output);
    const std::filesystem::path hlsl_output_path(output_path.string() + ".hlsl");

    auto set_error = [&](const std::string& message) {
        if (error != nullptr) {
            *error = message;
        }
        WriteTranslatorLog(log_path, message);
    };

    if (!std::filesystem::exists(input_path)) {
        set_error("input blob does not exist: " + input_path.string());
        return false;
    }

    const std::optional<std::string> dxc_profile = ResolveDxcProfile(options.stage);
    if (!dxc_profile.has_value()) {
        set_error("unsupported stage (expected vertex or pixel): " + options.stage);
        return false;
    }

    if (options.target == "godot") {
        // Godot's shading language, straight from the recompiler: no HLSL, no dxc. The output
        // is the artifact itself, a .gdshaderinc the classic renderer's materials include.
        try {
            std::filesystem::create_directories(output_path.parent_path());
        } catch (const std::exception& exception) {
            set_error("unable to create output directory: " + std::string(exception.what()));
            return false;
        }
        // The inline Bink shaders are not Xenos microcode; they have a hand-written form here
        // as they do for HLSL.
        std::string inline_error;
        const bool wrote_inline =
            TryWriteFableBinkGodotShader(options, input_path, output_path, &inline_error);
        if (!inline_error.empty()) {
            set_error(inline_error);
            return false;
        }
        if (wrote_inline) {
            WriteTranslatorLog(log_path, "phase: fable_bink_godot_complete");
            return true;
        }

        const char* xenos_error = nullptr;
        XenosRecompTranslationStats xenos_stats{};
        WriteTranslatorLog(log_path, "phase: xenos_translate_godot_start");
        if (!XenosRecompTranslateEx(input_path.string().c_str(), output_path.string().c_str(), "",
                                    XENOSRECOMP_DIALECT_GODOT, &xenos_stats, &xenos_error)) {
            set_error(std::string("XenosRecomp failed: ") +
                      (xenos_error != nullptr ? xenos_error : "failed to run"));
            return false;
        }
        if (memexport_stats != nullptr) {
            memexport_stats->reported = true;
            memexport_stats->enabled = xenos_stats.memexportEnabled != 0;
            memexport_stats->streams = xenos_stats.memexportStreams;
            memexport_stats->data_register_mask = xenos_stats.memexportDataRegisterMask;
            memexport_stats->unresolved_address_writes = xenos_stats.memexportUnresolvedAddressWrites;
        }
        WriteTranslatorLog(log_path, "phase: xenos_translate_godot_complete");
        if (!std::filesystem::exists(output_path)) {
            set_error("XenosRecomp did not write the Godot shader: " + output_path.string());
            return false;
        }
        return true;
    }

    if (options.target != "vulkan" && options.target != "d3d12" &&
        options.target != "metal") {
        set_error("unsupported target '" + options.target + "'");
        return false;
    }
    if (options.target == "metal") {
        set_error(
            "metal target selected, but XenosRecomp Metal artifact generation "
            "requires the SPIR-V to MSL/metallib conversion step");
        return false;
    }

    try {
        std::filesystem::create_directories(output_path.parent_path());
        std::filesystem::create_directories(hlsl_output_path.parent_path());
    } catch (const std::exception& exception) {
        set_error("unable to create output directories: " + std::string(exception.what()));
        return false;
    }

    // Always emit a sidecar path for deterministic diagnostics, even if
    // recompilation fails before producing real HLSL.
    std::string hlsl_stub_error;
    if (!WriteTextFile(
            hlsl_output_path,
            "// refii-shader-xenos-translator placeholder sidecar\n",
            &hlsl_stub_error)) {
        set_error("unable to initialize HLSL sidecar: " + hlsl_stub_error);
        return false;
    }

    std::string fable_inline_error;
    const bool wrote_fable_inline_shader =
        TryWriteFablePixRenderVideoFramePixelShader(
            options, input_path, hlsl_output_path, &fable_inline_error);
    if (!fable_inline_error.empty()) {
        set_error(fable_inline_error);
        return false;
    }

    if (wrote_fable_inline_shader) {
        WriteTranslatorLog(log_path, "phase: fable_pixrender_video_hlsl_complete");
    } else {
        const char* xenos_error = nullptr;
        XenosRecompTranslationStats xenos_stats{};
        WriteTranslatorLog(log_path, "phase: xenos_translate_start");
        const std::string shader_common_path = ShaderCommonPath();
        if (!XenosRecompTranslateToHlslEx(input_path.string().c_str(),
                                          hlsl_output_path.string().c_str(),
                                          shader_common_path.c_str(),
                                          &xenos_stats,
                                          &xenos_error)) {
            set_error(std::string("XenosRecomp failed: ") +
                      (xenos_error != nullptr ? xenos_error : "failed to run"));
            return false;
        }
        if (memexport_stats != nullptr) {
            memexport_stats->reported = true;
            memexport_stats->enabled = xenos_stats.memexportEnabled != 0;
            memexport_stats->streams = xenos_stats.memexportStreams;
            memexport_stats->data_register_mask = xenos_stats.memexportDataRegisterMask;
            memexport_stats->unresolved_address_writes =
                xenos_stats.memexportUnresolvedAddressWrites;
        }
        // Memexport is rare enough that every occurrence belongs in the log.
        if (xenos_stats.memexportEnabled != 0 ||
            xenos_stats.memexportUnresolvedAddressWrites != 0) {
            WriteTranslatorLog(
                log_path,
                "phase: xenos_memexport streams=" +
                    std::to_string(xenos_stats.memexportStreams) +
                    " eM_mask=" + std::to_string(xenos_stats.memexportDataRegisterMask) +
                    " unresolved_eA=" +
                    std::to_string(xenos_stats.memexportUnresolvedAddressWrites));
        }
        WriteTranslatorLog(log_path, "phase: xenos_translate_complete");
    }
    if (!std::filesystem::exists(hlsl_output_path)) {
        set_error("XenosRecomp did not write HLSL output: " + hlsl_output_path.string());
        return false;
    }

    const auto run_dxc = [&](const std::filesystem::path& artifact_path,
                             bool tess_variant) -> bool {
        std::vector<std::string> dxc_args;
        dxc_args.push_back(DxcToolPath());
        dxc_args.push_back("-T");
        dxc_args.push_back(*dxc_profile);
        dxc_args.push_back("-E");
        dxc_args.push_back("main");
        dxc_args.push_back("-HV");
        dxc_args.push_back("2021");
        dxc_args.push_back("-all-resources-bound");
        dxc_args.push_back("-Wno-conversion");
        if (tess_variant) {
            // Same HLSL, second artifact: the tessellation entry seeding is
            // compiled in (see REFII_TESSELLATION_VARIANT in shader_common.h).
            dxc_args.push_back("-D");
            dxc_args.push_back("REFII_TESSELLATION_VARIANT=1");
        }
        if (options.target == "vulkan") {
            dxc_args.push_back("-spirv");
            dxc_args.push_back("-fvk-use-dx-layout");
            if (options.stage == "vertex") {
                dxc_args.push_back("-fvk-invert-y");
            }
        } else {
            dxc_args.push_back("-Wno-ignored-attributes");
            dxc_args.push_back("-Qstrip_reflect");
        }
        dxc_args.push_back("-Qstrip_debug");
        dxc_args.push_back("-Fo");
        dxc_args.push_back(artifact_path.string());
        dxc_args.push_back(hlsl_output_path.string());

        const char* phase_tag = tess_variant ? "dxc_tess" : "dxc";
        int dxc_exit_code = 0;
        std::string dxc_error;
        WriteTranslatorLog(log_path, std::string("phase: ") + phase_tag + "_start");
        if (!RunProcess(dxc_args, BuildDxcEnvironment(), log_path, &dxc_exit_code,
                        &dxc_error)) {
            set_error("unable to launch DXC: " + dxc_error);
            return false;
        }
        WriteTranslatorLog(log_path, std::string("phase: ") + phase_tag +
                                         "_exit_code " + std::to_string(dxc_exit_code));
        if (dxc_exit_code != 0) {
            set_error("DXC exited with code " + std::to_string(dxc_exit_code) +
                      " for target " + options.target +
                      (tess_variant ? " (tessellation variant)" : ""));
            return false;
        }
        if (!std::filesystem::exists(artifact_path)) {
            set_error("DXC did not write output: " + artifact_path.string());
            return false;
        }
        return true;
    };

    if (!run_dxc(output_path, false)) {
        return false;
    }

    // Hardware-tessellation variant: vertex shaders only (no whitelist — every
    // vertex blob gets one). The inline Fable Bink shaders are pixel-stage and
    // never reach here with a tess_output.
    if (!options.tess_output.empty() && options.stage == "vertex") {
        const std::filesystem::path tess_output_path =
            std::filesystem::absolute(options.tess_output);
        try {
            std::filesystem::create_directories(tess_output_path.parent_path());
        } catch (const std::exception& exception) {
            set_error("unable to create tess output directory: " +
                      std::string(exception.what()));
            return false;
        }
        if (!run_dxc(tess_output_path, true)) {
            return false;
        }
    }

    return true;
}

namespace refii::shader {

bool TranslateXenosShaderBlob(const std::filesystem::path& input,
                             const std::string& target,
                             const std::string& stage,
                             const std::string& name,
                             const std::filesystem::path& output,
                             const std::filesystem::path& log_path,
                             std::string* error,
                             XenosMemoryExportStats* memexport_stats,
                             const std::filesystem::path& tess_output) {
    Options options;
    options.input = input;
    options.target = ToLowerAscii(TrimAscii(target));
    options.stage = ToLowerAscii(TrimAscii(stage));
    options.name = name;
    options.output = output;
    options.tess_output = tess_output;
    if (memexport_stats != nullptr) {
        *memexport_stats = XenosMemoryExportStats{};
    }
    return TranslateXenosShaderBlobImpl(options, log_path, error, memexport_stats);
}

std::string DescribeXenosMemoryExportStats(const XenosMemoryExportStats& stats) {
    if (!stats.reported) {
        return "none";
    }
    if (!stats.enabled && stats.unresolved_address_writes == 0) {
        return "streams=0";
    }

    std::string description = "streams=" + std::to_string(stats.streams);
    description += ",eM=0x";
    const char* digits = "0123456789ABCDEF";
    description.push_back(digits[(stats.data_register_mask >> 4) & 0xFu]);
    description.push_back(digits[stats.data_register_mask & 0xFu]);
    if (stats.unresolved_address_writes != 0) {
        description += ",unresolved_eA=" + std::to_string(stats.unresolved_address_writes);
    }
    if (!stats.enabled) {
        // eA written but no eM# - nothing reaches memory. Worth shouting about.
        description += ",no_data_registers";
    }
    return description;
}

}  // namespace refii::shader

bool RunProcess(const std::vector<std::string>& args,
                const std::vector<std::pair<std::string, std::string>>& env_overrides,
                const std::filesystem::path& log_path,
                int* exit_code,
                std::string* error) {
    if (args.empty()) {
        if (error != nullptr) {
            *error = "empty process command";
        }
        return false;
    }

#if defined(__unix__) || defined(__APPLE__)
    const pid_t pid = fork();
    if (pid < 0) {
        if (error != nullptr) {
            *error = "fork() failed";
        }
        return false;
    }

    if (pid == 0) {
        for (const auto& [key, value] : env_overrides) {
            setenv(key.c_str(), value.c_str(), 1);
        }

        std::vector<char*> argv;
        argv.reserve(args.size() + 1);
        for (const std::string& arg : args) {
            argv.push_back(const_cast<char*>(arg.c_str()));
        }
        argv.push_back(nullptr);

        execvp(argv[0], argv.data());
        _exit(127);
    }

    int status = 0;
    if (waitpid(pid, &status, 0) < 0) {
        if (error != nullptr) {
            *error = "waitpid() failed";
        }
        return false;
    }

    if (exit_code != nullptr) {
        if (WIFEXITED(status)) {
            *exit_code = WEXITSTATUS(status);
        } else if (WIFSIGNALED(status)) {
            *exit_code = 128 + WTERMSIG(status);
        } else {
            *exit_code = 1;
        }
    }
    return true;
#elif defined(_WIN32)
    // The tool's libraries are in bin/, one directory above it, so the child needs PATH with
    // that directory first. CreateProcess takes a whole environment block, not a delta: the
    // parent's block is copied with the overridden variables replaced.
    std::string environment_block;
    {
        std::vector<std::pair<std::string, std::string>> variables;
        if (LPCH strings = GetEnvironmentStringsA(); strings != nullptr) {
            for (const char* cursor = strings; *cursor != '\0';
                 cursor += std::strlen(cursor) + 1) {
                const std::string_view entry(cursor);
                // "=C:=C:\\dir" entries record per-drive directories; their name starts at
                // the leading '='.
                const std::size_t equals = entry.find('=', entry.front() == '=' ? 1 : 0);
                if (equals == std::string_view::npos) {
                    continue;
                }
                variables.emplace_back(std::string(entry.substr(0, equals)),
                                       std::string(entry.substr(equals + 1)));
            }
            FreeEnvironmentStringsA(strings);
        }
        for (const auto& [key, value] : env_overrides) {
            bool replaced = false;
            for (auto& [existing_key, existing_value] : variables) {
                if (_stricmp(existing_key.c_str(), key.c_str()) == 0) {
                    existing_value = value;
                    replaced = true;
                    break;
                }
            }
            if (!replaced) {
                variables.emplace_back(key, value);
            }
        }
        for (const auto& [key, value] : variables) {
            environment_block += key;
            environment_block.push_back('=');
            environment_block += value;
            environment_block.push_back('\0');
        }
        environment_block.push_back('\0');
    }
    WriteTranslatorLog(log_path, "phase: process_set_environment_complete");

    std::string command;
    for (std::size_t index = 0; index < args.size(); ++index) {
        if (index != 0) {
            command.push_back(' ');
        }
        command += QuoteProcessArgument(args[index]);
    }
    WriteTranslatorLog(log_path, "phase: process_command " + command);

    STARTUPINFOA startup_info = {};
    startup_info.cb = sizeof(startup_info);
    PROCESS_INFORMATION process_info = {};
    std::string mutable_command = command;
    const std::filesystem::path application_path(args[0]);
    const std::string application = application_path.string();
    const std::filesystem::path working_directory = application_path.parent_path();
    const std::string working_directory_string = working_directory.string();
    WriteTranslatorLog(log_path, "phase: create_process_start");
    const BOOL created = CreateProcessA(
        application.c_str(),
        mutable_command.data(),
        nullptr,
        nullptr,
        FALSE,
        0,
        environment_block.data(),
        working_directory_string.empty() ? nullptr : working_directory_string.c_str(),
        &startup_info,
        &process_info);
    WriteTranslatorLog(log_path, "phase: create_process_return");
    if (!created) {
        if (error != nullptr) {
            *error = "CreateProcess failed with error " +
                     std::to_string(GetLastError()) + ": " + command;
        }
        return false;
    }

    WriteTranslatorLog(log_path, "phase: wait_process_start");
    WaitForSingleObject(process_info.hProcess, INFINITE);
    WriteTranslatorLog(log_path, "phase: wait_process_complete");
    DWORD process_exit_code = 1;
    if (!GetExitCodeProcess(process_info.hProcess, &process_exit_code)) {
        if (error != nullptr) {
            *error = "GetExitCodeProcess failed with error " +
                     std::to_string(GetLastError()) + ": " + command;
        }
        CloseHandle(process_info.hThread);
        CloseHandle(process_info.hProcess);
        return false;
    }
    WriteTranslatorLog(log_path, "phase: get_exit_code " + std::to_string(process_exit_code));

    CloseHandle(process_info.hThread);
    CloseHandle(process_info.hProcess);
    if (exit_code != nullptr) {
        *exit_code = static_cast<int>(process_exit_code);
    }
    return true;
#else
    WriteTranslatorLog(log_path, "phase: process_set_environment_start");
    for (const auto& [key, value] : env_overrides) {
        _putenv_s(key.c_str(), value.c_str());
    }
    WriteTranslatorLog(log_path, "phase: process_set_environment_complete");

    std::string command;
    for (std::size_t index = 0; index < args.size(); ++index) {
        if (index != 0) {
            command.push_back(' ');
        }
        command += QuoteProcessArgument(args[index]);
    }
    WriteTranslatorLog(log_path, "phase: process_command " + command);

    const int code = std::system(command.c_str());
    if (exit_code != nullptr) {
        *exit_code = code;
    }
    return true;
#endif
}

std::string QuoteProcessArgument(const std::string& argument) {
    if (argument.empty()) {
        return "\"\"";
    }

    if (argument.find_first_of(" \t\"") == std::string::npos) {
        return argument;
    }

    std::string quoted = "\"";
    std::size_t backslash_count = 0;
    for (const char character : argument) {
        if (character == '\\') {
            ++backslash_count;
            continue;
        }

        if (character == '"') {
            quoted.append(backslash_count * 2 + 1, '\\');
            quoted.push_back('"');
        } else {
            quoted.append(backslash_count, '\\');
            quoted.push_back(character);
        }
        backslash_count = 0;
    }

    quoted.append(backslash_count * 2, '\\');
    quoted.push_back('"');
    return quoted;
}

std::vector<std::pair<std::string, std::string>> BuildDxcEnvironment() {
    std::vector<std::pair<std::string, std::string>> environment;
    const std::string lib_dir = DxcLibraryDirectory();
    if (lib_dir.empty()) {
        return environment;
    }

#if defined(__APPLE__)
    const char* variable_name = "DYLD_LIBRARY_PATH";
    const char separator = ':';
#elif defined(__unix__)
    const char* variable_name = "LD_LIBRARY_PATH";
    const char separator = ':';
#elif defined(_WIN32)
    const char* variable_name = "PATH";
    const char separator = ';';
#else
    return environment;
#endif

    std::string value = lib_dir;
    if (const char* existing = std::getenv(variable_name);
        existing != nullptr && *existing != '\0') {
        value.push_back(separator);
        value.append(existing);
    }
    environment.emplace_back(variable_name, std::move(value));
    return environment;
}

std::optional<std::string> ResolveDxcProfile(std::string_view stage) {
    if (stage == "vertex") {
        return "vs_6_0";
    }
    if (stage == "pixel") {
        return "ps_6_0";
    }
    return std::nullopt;
}

bool WriteTextFile(const std::filesystem::path& path,
                  std::string_view text,
                  std::string* error) {
    std::ofstream file(path, std::ios::binary | std::ios::trunc);
    if (!file) {
        if (error != nullptr) {
            *error = "failed to open " + path.string();
        }
        return false;
    }
    file.write(text.data(), static_cast<std::streamsize>(text.size()));
    if (!file) {
        if (error != nullptr) {
            *error = "failed to write " + path.string();
        }
        return false;
    }
    return true;
}

bool ReadBinaryFile(const std::filesystem::path& path,
                   std::vector<std::uint8_t>* bytes,
                   std::string* error) {
    if (bytes == nullptr) {
        if (error != nullptr) {
            *error = "missing byte output";
        }
        return false;
    }

    std::error_code ec;
    const auto size = std::filesystem::file_size(path, ec);
    if (ec) {
        if (error != nullptr) {
            *error = "failed to stat " + path.string() + ": " + ec.message();
        }
        return false;
    }

    bytes->assign(static_cast<std::size_t>(size), 0);
    std::ifstream file(path, std::ios::binary);
    if (!file) {
        if (error != nullptr) {
            *error = "failed to open " + path.string();
        }
        bytes->clear();
        return false;
    }
    file.read(reinterpret_cast<char*>(bytes->data()),
              static_cast<std::streamsize>(bytes->size()));
    if (!file && !file.eof()) {
        if (error != nullptr) {
            *error = "failed to read " + path.string();
        }
        bytes->clear();
        return false;
    }
    return true;
}

// The three inline Bink shaders Fable draws video with. They are not Xenos microcode (the
// PixRenderVideoFrame payload is a 36-byte stub the engine recognises by content), so every
// dialect writes them by hand. Returns false with `error` empty when the blob is not one of them.
enum class FableBinkInline {
    kNone,
    kPixRenderVideo,
    kMovie,
    kMovieWithAlpha,
};

bool DetectFableBinkInlineShader(const Options& options,
                                 const std::filesystem::path& input_path,
                                 FableBinkInline* kind,
                                 std::string* error) {
    constexpr std::uint8_t kExpectedPayload[] = {
        0x00, 0x00, 0x00, 0x00, 0x10, 0x01, 0xc4, 0x00, 0x22, 0x00, 0x00, 0x00,
        0xc8, 0x0f, 0x80, 0x00, 0x02, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };
    *kind = FableBinkInline::kNone;
    if (options.name == "PSHADER_FABLE_PIXRENDER_VIDEO") {
        *kind = FableBinkInline::kPixRenderVideo;
    } else if (options.name == "SHADER_BINK_MOVIE") {
        *kind = FableBinkInline::kMovie;
    } else if (options.name == "SHADER_BINK_MOVIE_WITH_ALPHA") {
        *kind = FableBinkInline::kMovieWithAlpha;
    } else {
        return false;
    }
    if (options.stage != "pixel") {
        if (error != nullptr) {
            *error = "Fable Bink movie shader must be pixel stage";
        }
        return false;
    }

    if (*kind == FableBinkInline::kPixRenderVideo) {
        std::vector<std::uint8_t> bytes;
        std::string read_error;
        if (!ReadBinaryFile(input_path, &bytes, &read_error)) {
            if (error != nullptr) {
                *error = read_error;
            }
            return false;
        }
        if (bytes.size() != sizeof(kExpectedPayload) ||
            !std::equal(bytes.begin(), bytes.end(), std::begin(kExpectedPayload))) {
            if (error != nullptr) {
                *error = "Fable PixRenderVideoFrame inline shader payload mismatch";
            }
            return false;
        }
    }
    return true;
}

// The Godot form: the same YUV to RGB combine over the three planes (four with alpha), bound as
// the unnamed pixel samplers s0..s3 the way the Godot dialect names them.
bool TryWriteFableBinkGodotShader(
    const Options& options,
    const std::filesystem::path& input_path,
    const std::filesystem::path& output_path,
    std::string* error) {
    FableBinkInline kind = FableBinkInline::kNone;
    if (!DetectFableBinkInlineShader(options, input_path, &kind, error)) {
        return false;
    }
    const bool with_alpha = kind == FableBinkInline::kMovieWithAlpha;

    std::string shader =
        "// Fable's inline Bink video pixel shader, written by hand for Godot: the blob is not Xenos\n"
        "// microcode. Planes: s0 = Y, s1 = Cr, s2 = Cb" +
        std::string(with_alpha ? ", s3 = alpha" : "") + ".\n"
        "#include \"res://src/Engine/material/xenos_common.gdshaderinc\"\n"
        "#ifndef XR_V_TEXCOORD0\n#define XR_V_TEXCOORD0\nvarying vec4 v_texcoord0;\n#endif\n"
        "uniform sampler2D ps_s0;\n"
        "uniform sampler2D ps_s1;\n"
        "uniform sampler2D ps_s2;\n";
    if (with_alpha) {
        shader += "uniform sampler2D ps_s3;\n";
    }
    shader +=
        "\nvoid fragment()\n{\n"
        "\tvec2 uv = v_texcoord0.xy;\n"
        "\tfloat cb = texture(ps_s2, uv).r;\n"
        "\tfloat y = texture(ps_s0, uv).r;\n"
        "\tfloat cr = texture(ps_s1, uv).r;\n"
        "\tvec4 color;\n"
        "\tcolor.r = y * 1.1641235 + cr * 1.5957947 - 0.8706551;\n"
        "\tfloat green_base = y * 1.1641235 + cr * -0.81347656;\n"
        "\tcolor.g = green_base + cb * -0.3914490 + 0.5297050;\n"
        "\tcolor.b = y * 1.1641235 + cb * 2.0178223 - 1.081669;\n";
    shader += with_alpha ? "\tcolor.a = texture(ps_s3, uv).r;\n" : "\tcolor.a = 1.0;\n";
    shader +=
        "\tALBEDO = color.rgb;\n"
        "\tALPHA = color.a;\n"
        "}\n";

    return WriteTextFile(output_path, shader, error);
}

bool TryWriteFablePixRenderVideoFramePixelShader(
    const Options& options,
    const std::filesystem::path& input_path,
    const std::filesystem::path& hlsl_output_path,
    std::string* error) {
    FableBinkInline kind = FableBinkInline::kNone;
    if (!DetectFableBinkInlineShader(options, input_path, &kind, error)) {
        return false;
    }
    const bool is_bink_movie_with_alpha = kind == FableBinkInline::kMovieWithAlpha;

    constexpr std::string_view kHlsl = R"(#define REFII_SHADER_STAGE_PIXEL 1
#define REFII_BINK_ALPHA 0

#ifdef __spirv__
struct PushConstants {
  uint64_t VertexShaderConstants;
  uint64_t PixelShaderConstants;
  uint64_t SharedConstants;
};
[[vk::push_constant]] ConstantBuffer<PushConstants> g_PushConstants;
#define s0_Texture2DDescriptorIndex vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 0)
#define s1_Texture2DDescriptorIndex vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 4)
#define s2_Texture2DDescriptorIndex vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 8)
#define s3_Texture2DDescriptorIndex vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 12)
#define s0_SamplerDescriptorIndex vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 384)
#define s1_SamplerDescriptorIndex vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 388)
#define s2_SamplerDescriptorIndex vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 392)
#define s3_SamplerDescriptorIndex vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 396)
#else
cbuffer SharedConstants : register(b2, space4) {
  uint4 g_Texture2DDescriptorIndexRows[8] : packoffset(c0);
  uint4 g_Texture3DDescriptorIndexRows[8] : packoffset(c8);
  uint4 g_TextureCubeDescriptorIndexRows[8] : packoffset(c16);
  uint4 g_SamplerDescriptorIndexRows[8] : packoffset(c24);
};
#define s0_Texture2DDescriptorIndex g_Texture2DDescriptorIndexRows[0].x
#define s1_Texture2DDescriptorIndex g_Texture2DDescriptorIndexRows[0].y
#define s2_Texture2DDescriptorIndex g_Texture2DDescriptorIndexRows[0].z
#define s3_Texture2DDescriptorIndex g_Texture2DDescriptorIndexRows[0].w
#define s0_SamplerDescriptorIndex g_SamplerDescriptorIndexRows[0].x
#define s1_SamplerDescriptorIndex g_SamplerDescriptorIndexRows[0].y
#define s2_SamplerDescriptorIndex g_SamplerDescriptorIndexRows[0].z
#define s3_SamplerDescriptorIndex g_SamplerDescriptorIndexRows[0].w
#endif

Texture2D<float4> g_Texture2DDescriptorHeap[] : register(t0, space0);
Texture3D<float4> g_Texture3DDescriptorHeap[] : register(t0, space1);
TextureCube<float4> g_TextureCubeDescriptorHeap[] : register(t0, space2);
SamplerState g_SamplerDescriptorHeap[] : register(s0, space3);

float SamplePlane(uint texture_index, uint sampler_index, float2 uv) {
  Texture2D<float4> texture = g_Texture2DDescriptorHeap[texture_index];
#if REFII_SHADER_STAGE_PIXEL
  return texture.Sample(g_SamplerDescriptorHeap[sampler_index], uv).r;
#else
  return texture.SampleLevel(g_SamplerDescriptorHeap[sampler_index], uv, 0.0f).r;
#endif
}

#ifndef __spirv__
[shader("pixel")]
#endif
void main(in float4 iPos : SV_Position,
          in float4 iTexCoord0 : TEXCOORD0,
          in float4 iTexCoord1 : TEXCOORD1,
          in float4 iTexCoord2 : TEXCOORD2,
          in float4 iTexCoord3 : TEXCOORD3,
          in float4 iTexCoord4 : TEXCOORD4,
          in float4 iTexCoord5 : TEXCOORD5,
          in float4 iTexCoord6 : TEXCOORD6,
          in float4 iTexCoord7 : TEXCOORD7,
          in float4 iTexCoord8 : TEXCOORD8,
          in float4 iTexCoord9 : TEXCOORD9,
          in float4 iTexCoord10 : TEXCOORD10,
          in float4 iTexCoord11 : TEXCOORD11,
          in float4 iTexCoord12 : TEXCOORD12,
          in float4 iTexCoord13 : TEXCOORD13,
          in float4 iTexCoord14 : TEXCOORD14,
          in float4 iTexCoord15 : TEXCOORD15,
          in float4 iColor0 : COLOR0,
          in float4 iColor1 : COLOR1,
#ifdef __spirv__
          in bool iFace : SV_IsFrontFace,
#else
          in uint iFace : SV_IsFrontFace,
#endif
          out float4 oC0 : SV_Target0) {
  const float2 uv = iTexCoord0.xy;
  const float cb = SamplePlane(s2_Texture2DDescriptorIndex,
                               s2_SamplerDescriptorIndex, uv);
  const float y = SamplePlane(s0_Texture2DDescriptorIndex,
                              s0_SamplerDescriptorIndex, uv);
  const float cr = SamplePlane(s1_Texture2DDescriptorIndex,
                               s1_SamplerDescriptorIndex, uv);

  float4 color;
  color.r = y * 1.1641235f + cr * 1.5957947f - 0.8706551f;
  const float green_base = y * 1.1641235f + cr * -0.81347656f;
  color.g = green_base + cb * -0.3914490f + 0.5297050f;
  color.b = y * 1.1641235f + cb * 2.0178223f - 1.081669f;
  color.a = 1.0f;
#if REFII_BINK_ALPHA
  color.a = SamplePlane(s3_Texture2DDescriptorIndex,
                        s3_SamplerDescriptorIndex, uv);
#endif
  oC0 = color;
}
)";
    std::string hlsl(kHlsl);
    if (is_bink_movie_with_alpha) {
        constexpr std::string_view kAlphaOff = "#define REFII_BINK_ALPHA 0";
        constexpr std::string_view kAlphaOn = "#define REFII_BINK_ALPHA 1";
        const std::size_t alpha_define = hlsl.find(kAlphaOff);
        if (alpha_define != std::string::npos) {
            hlsl.replace(alpha_define, kAlphaOff.size(), kAlphaOn);
        }
    }
    if (!WriteTextFile(hlsl_output_path, hlsl, error)) {
        return false;
    }
    return true;
}

 
