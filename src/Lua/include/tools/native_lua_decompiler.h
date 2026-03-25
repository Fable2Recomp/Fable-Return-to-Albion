#pragma once

#include <cstddef>
#include <filesystem>
#include <functional>
#include <string>

namespace refii::lua {

struct LuaWorkspaceDecompileProgress {
    std::size_t completed_files = 0;
    std::size_t total_files = 0;
    std::filesystem::path current_input;
    std::string phase;
};

struct LuaWorkspaceDecompileOptions {
    std::filesystem::path workspace_root;
    std::filesystem::path output_root;
    std::filesystem::path single_tool_path;
    std::filesystem::path disassembly_output_root;
    std::size_t progress_update_stride = 16;
    unsigned int child_timeout_seconds = 2;
    unsigned int native_render_timeout_seconds = 8;
    bool write_disassembly_fallback = true;
    std::function<void(const LuaWorkspaceDecompileProgress&)> progress_callback;
};

struct LuaWorkspaceDecompileSummary {
    std::filesystem::path workspace_root;
    std::filesystem::path output_root;
    std::filesystem::path manifest_path;
    std::size_t total_files = 0;
    std::size_t decompiled = 0;
    std::size_t aliased = 0;
    std::size_t disassembled = 0;
    std::size_t copied = 0;
    std::size_t skipped = 0;
};

std::filesystem::path GetDefaultLuaDecompiledOutputRoot(const std::filesystem::path& workspace_root);
std::filesystem::path GetDefaultLuaDecompileInputRoot(const std::filesystem::path& workspace_root);

bool DecompileLuaWorkspace(const LuaWorkspaceDecompileOptions& options,
                          LuaWorkspaceDecompileSummary* summary = nullptr,
                          std::string* error = nullptr);

}  // namespace refii::lua
