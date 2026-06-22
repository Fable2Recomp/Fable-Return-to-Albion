#include <cctype>
#include <cstdlib>
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
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>
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

struct Options {
    std::filesystem::path input;
    std::string target;
    std::string stage;
    std::string name;
    std::filesystem::path output;
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
bool RunProcess(const std::vector<std::string>& args,
                const std::vector<std::pair<std::string, std::string>>& env_overrides,
                int* exit_code,
                std::string* error);
std::vector<std::pair<std::string, std::string>> BuildDxcEnvironment();

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
                                 std::string* error) {
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

    const char* xenos_error = nullptr;
    if (!XenosRecompTranslateToHlsl(input_path.string().c_str(),
                                    hlsl_output_path.string().c_str(),
                                    REFII_XENOSRECOMP_SHADER_COMMON_PATH,
                                    &xenos_error)) {
        set_error(std::string("XenosRecomp failed: ") +
                  (xenos_error != nullptr ? xenos_error : "failed to run"));
        return false;
    }
    if (!std::filesystem::exists(hlsl_output_path)) {
        set_error("XenosRecomp did not write HLSL output: " + hlsl_output_path.string());
        return false;
    }

    std::vector<std::string> dxc_args;
    dxc_args.push_back(REFII_XENOS_DXC_TOOL_PATH);
    dxc_args.push_back("-T");
    dxc_args.push_back(*dxc_profile);
    dxc_args.push_back("-E");
    dxc_args.push_back("main");
    dxc_args.push_back("-HV");
    dxc_args.push_back("2021");
    dxc_args.push_back("-all-resources-bound");
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
    dxc_args.push_back(output_path.string());
    dxc_args.push_back(hlsl_output_path.string());

    int dxc_exit_code = 0;
    std::string dxc_error;
    if (!RunProcess(dxc_args, BuildDxcEnvironment(), &dxc_exit_code, &dxc_error)) {
        set_error("unable to launch DXC: " + dxc_error);
        return false;
    }
    if (dxc_exit_code != 0) {
        set_error("DXC exited with code " + std::to_string(dxc_exit_code) + " for target " +
                  options.target);
        return false;
    }
    if (!std::filesystem::exists(output_path)) {
        set_error("DXC did not write output: " + output_path.string());
        return false;
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
                             std::string* error) {
    Options options;
    options.input = input;
    options.target = ToLowerAscii(TrimAscii(target));
    options.stage = ToLowerAscii(TrimAscii(stage));
    options.name = name;
    options.output = output;
    return TranslateXenosShaderBlobImpl(options, log_path, error);
}

}  // namespace refii::shader

bool RunProcess(const std::vector<std::string>& args,
                const std::vector<std::pair<std::string, std::string>>& env_overrides,
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
#else
    for (const auto& [key, value] : env_overrides) {
        _putenv_s(key.c_str(), value.c_str());
    }

    std::string command;
    for (std::size_t index = 0; index < args.size(); ++index) {
        if (index != 0) {
            command.push_back(' ');
        }
        command.push_back('"');
        for (const char character : args[index]) {
            if (character == '\"' || character == '\\') {
                command.push_back('\\');
            }
            command.push_back(character);
        }
        command.push_back('"');
    }

    const int code = std::system(command.c_str());
    if (exit_code != nullptr) {
        *exit_code = code;
    }
    return true;
#endif
}

std::vector<std::pair<std::string, std::string>> BuildDxcEnvironment() {
    std::vector<std::pair<std::string, std::string>> environment;
    const std::string lib_dir = REFII_XENOS_DXC_LIB_DIR;
    if (lib_dir.empty()) {
        return environment;
    }

#if defined(__APPLE__)
    const char* variable_name = "DYLD_LIBRARY_PATH";
    const char separator = ':';
#elif defined(__unix__)
    const char* variable_name = "LD_LIBRARY_PATH";
    const char separator = ':';
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

 
