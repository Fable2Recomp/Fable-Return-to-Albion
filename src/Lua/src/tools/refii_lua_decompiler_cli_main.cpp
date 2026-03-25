#include "tools/native_lua_decompiler.h"

#include <filesystem>
#include <iostream>
#include <string_view>

namespace {

struct Options {
    std::filesystem::path workspace;
    std::filesystem::path output;
    std::filesystem::path single_tool;
};

bool ParseArguments(int argc, char** argv, Options& options) {
    if (argc < 2) {
        return false;
    }

    options.workspace = std::filesystem::path(argv[1]);
    for (int i = 2; i < argc; ++i) {
        const std::string_view argument(argv[i]);
        if (argument == "--out" && i + 1 < argc) {
            options.output = std::filesystem::path(argv[++i]);
        } else if (argument == "--single-tool" && i + 1 < argc) {
            options.single_tool = std::filesystem::path(argv[++i]);
        } else {
            return false;
        }
    }

    return true;
}

std::filesystem::path GetDefaultSingleToolPath(const char* argv0) {
    std::filesystem::path tool_path = std::filesystem::absolute(std::filesystem::path(argv0));
#if defined(_WIN32)
    tool_path.replace_filename("refii-lua-decompiler-single.exe");
#else
    tool_path.replace_filename("refii-lua-decompiler-single");
#endif
    return tool_path;
}

}  // namespace

int main(int argc, char** argv) {
    Options options;
    if (!ParseArguments(argc, argv, options)) {
        std::cerr
            << "usage: refii-lua-decompiler <workspace> [--out <dir>] [--single-tool <path>]\n";
        return 1;
    }

    refii::lua::LuaWorkspaceDecompileOptions decompile_options;
    decompile_options.workspace_root = options.workspace;
    decompile_options.output_root = options.output;
#if defined(_WIN32)
    decompile_options.single_tool_path =
        options.single_tool.empty() ? GetDefaultSingleToolPath(argv[0]) : options.single_tool;
#else
    decompile_options.single_tool_path = options.single_tool;
#endif

    refii::lua::LuaWorkspaceDecompileSummary summary;
    std::string error;
    if (!refii::lua::DecompileLuaWorkspace(decompile_options, &summary, &error)) {
        std::cerr << error << "\n";
        return 1;
    }

    std::cout << "Workspace: " << summary.workspace_root << "\n";
    std::cout << "Output:    " << summary.output_root << "\n";
    std::cout << "Files:     " << summary.total_files << "\n";
    std::cout << "Decompiled:" << summary.decompiled << "\n";
    std::cout << "Aliased:   " << summary.aliased << "\n";
    std::cout << "Disasm:    " << summary.disassembled << "\n";
    std::cout << "Copied:    " << summary.copied << "\n";
    std::cout << "Skipped:   " << summary.skipped << "\n";
    std::cout << "Manifest:  " << summary.manifest_path << "\n";
    return 0;
}
