#include "shader/refii_shader_xenos_translator.h"

#include <string>
#include <string_view>
#include <filesystem>
#include <iostream>

namespace {

struct Options {
    std::filesystem::path input;
    std::string target;
    std::string stage;
    std::string name;
    std::filesystem::path output;
    std::filesystem::path log_path;
    std::string title;
};

bool ParseArguments(int argc, char** argv, Options& options) {
    if (argc < 11 || argc > 15 || (argc % 2) == 0) {
        return false;
    }

    for (int index = 1; index < argc; ++index) {
        const std::string_view argument = argv[index];
        if (argument == "--input" && index + 1 < argc) {
            options.input = std::filesystem::path(argv[++index]);
        } else if (argument == "--target" && index + 1 < argc) {
            options.target = argv[++index];
        } else if (argument == "--stage" && index + 1 < argc) {
            options.stage = argv[++index];
        } else if (argument == "--name" && index + 1 < argc) {
            options.name = argv[++index];
        } else if (argument == "--output" && index + 1 < argc) {
            options.output = std::filesystem::path(argv[++index]);
        } else if (argument == "--log" && index + 1 < argc) {
            options.log_path = std::filesystem::path(argv[++index]);
        } else if (argument == "--title" && index + 1 < argc) {
            options.title = argv[++index];
        } else {
            return false;
        }
    }

    return !options.input.empty() && !options.target.empty() && !options.stage.empty() &&
           !options.name.empty() && !options.output.empty();
}

void PrintUsage() {
    std::cerr
        << "usage: refii-shader-xenos-translator --input <blob> --target <vulkan|d3d12|metal> "
           "--stage <vertex|pixel> --name <entry> --output <file> [--log <file>] "
           "[--title <Fable2|Fable3>]\n";
}

}  // namespace

int main(int argc, char** argv) {
    Options options;
    if (!ParseArguments(argc, argv, options)) {
        PrintUsage();
        return 1;
    }

    std::string error;
    if (!refii::shader::TranslateXenosShaderBlob(
            options.input,
            options.target,
            options.stage,
            options.name,
            options.output,
            options.log_path,
            &error,
            nullptr,
            std::filesystem::path{},
            options.title)) {
        std::cerr << "compile failed: " << error << '\n';
        return 1;
    }

    std::cout << "wrote " << options.output.generic_string() << '\n';
    return 0;
}
