// Public contract of the Lua workspace: script-bank export, decompilation
// with byte-exact verification, baseline snapshot, drift verification and
// reset. The launcher's Modding tab and refii-lua-decompiler drive it.
#pragma once

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <string>
#include <vector>

namespace refii::lua {

struct LuaWorkspaceDecompileProgress {
    std::size_t completed_files = 0;
    std::size_t total_files = 0;
    std::filesystem::path current_input;
    std::string phase;
    std::size_t worker_slot = 0;
    std::size_t worker_count = 0;
    std::size_t active_workers = 0;
};

struct LuaWorkspaceDecompileOptions {
    // The workspace root (usually <assets>/unpacked) or its luasource/ cache.
    std::filesystem::path workspace_root;
    // Where the public Lua files go; defaults to the workspace root.
    std::filesystem::path output_root;
    // 0 selects an automatic worker count that keeps some cores free.
    std::size_t worker_count = 0;
    std::size_t progress_update_stride = 1;
    // A chunk that cannot be decompiled gets a placeholder at its public path
    // (the runtime keeps loading the original) and its listing under _disasm/.
    bool write_disassembly_fallback = true;
    std::function<void(const LuaWorkspaceDecompileProgress&)> progress_callback;
};

struct LuaWorkspaceDecompileSummary {
    std::filesystem::path workspace_root;
    std::filesystem::path output_root;
    std::filesystem::path manifest_path;
    std::size_t total_files = 0;      // inputs considered
    std::size_t decompiled = 0;       // outputs that recompile byte-for-byte
    std::size_t aliased = 0;          // retail inputs served by their debug sibling's text
    std::size_t nonexact = 0;         // outputs whose code matches but debug info does not
    std::size_t aliased_nonexact = 0; // aliased inputs the shared text does not reproduce exactly
    std::size_t disassembled = 0;     // inputs that could not be decompiled
    std::size_t invalid = 0;          // inputs that are not readable chunks
    std::size_t mismatched = 0;       // outputs whose code does not recompile to the original
    std::size_t copied = 0;           // plaintext inputs copied as they are
    std::size_t skipped = 0;
};

struct LuaWorkspaceBuildOptions {
    std::filesystem::path game_install_root;
    std::filesystem::path workspace_root;
    // 0 selects an automatic worker count that keeps some cores free.
    std::size_t worker_count = 0;
    std::size_t progress_update_stride = 1;
    bool write_disassembly_fallback = true;
    std::function<void(const LuaWorkspaceDecompileProgress&)> progress_callback;
};

struct LuaWorkspaceBuildSummary {
    std::filesystem::path game_install_root;
    std::filesystem::path workspace_root;
    std::filesystem::path source_root;
    std::filesystem::path output_root;
    std::filesystem::path baseline_root;
    std::filesystem::path baseline_manifest_path;
    std::size_t extracted_banks = 0;
    std::size_t extracted_files = 0;
    std::size_t copied_loose_files = 0;
    std::size_t baseline_files = 0;
    LuaWorkspaceDecompileSummary decompile_summary;
};

enum class LuaWorkspaceVerifyStatus {
    kOk,
    kModified,
    kMissing,
    kExtra,
    kBaselineMissing,
    kBaselineCorrupt,
};

struct LuaWorkspaceVerifyEntry {
    std::filesystem::path relative_path;
    LuaWorkspaceVerifyStatus status = LuaWorkspaceVerifyStatus::kOk;
    std::string expected_hash;
    std::string actual_hash;
};

struct LuaWorkspaceVerifyOptions {
    std::filesystem::path workspace_root;
    bool include_ok_entries = true;
    std::function<void(const LuaWorkspaceDecompileProgress&)> progress_callback;
};

struct LuaWorkspaceVerifySummary {
    std::filesystem::path workspace_root;
    std::filesystem::path baseline_root;
    std::filesystem::path baseline_manifest_path;
    std::size_t tracked_files = 0;
    std::size_t ok = 0;
    std::size_t modified = 0;
    std::size_t missing = 0;
    std::size_t extra = 0;
    std::size_t baseline_missing = 0;
    std::size_t baseline_corrupt = 0;
    std::vector<LuaWorkspaceVerifyEntry> entries;
};

struct LuaWorkspaceResetOptions {
    std::filesystem::path workspace_root;
    std::filesystem::path relative_path;
    bool reset_all = true;
    std::function<void(const LuaWorkspaceDecompileProgress&)> progress_callback;
};

struct LuaWorkspaceResetSummary {
    std::filesystem::path workspace_root;
    std::filesystem::path baseline_root;
    std::size_t restored = 0;
    std::size_t removed = 0;
    std::size_t failed = 0;
};

std::filesystem::path GetDefaultLuaDecompiledOutputRoot(const std::filesystem::path& workspace_root);
std::filesystem::path GetDefaultLuaDecompileInputRoot(const std::filesystem::path& workspace_root);
std::filesystem::path GetLuaWorkspacePath(const std::filesystem::path& game_install_root);
std::filesystem::path GetLuaWorkspaceSourcePath(const std::filesystem::path& game_install_root);
std::filesystem::path GetLuaWorkspaceBaselineRoot(const std::filesystem::path& workspace_root);

const char* DescribeLuaWorkspaceVerifyStatus(LuaWorkspaceVerifyStatus status);

// Decompiles every script under the workspace's luasource/ cache into the
// public workspace, verifying each output against the chunk it came from.
bool DecompileLuaWorkspace(const LuaWorkspaceDecompileOptions& options,
                          LuaWorkspaceDecompileSummary* summary = nullptr,
                          std::string* error = nullptr);

bool BuildLuaWorkspace(const LuaWorkspaceBuildOptions& options,
                       LuaWorkspaceBuildSummary* summary = nullptr,
                       std::string* error = nullptr);

bool VerifyLuaWorkspace(const LuaWorkspaceVerifyOptions& options,
                        LuaWorkspaceVerifySummary* summary = nullptr,
                        std::string* error = nullptr);

bool ResetLuaWorkspace(const LuaWorkspaceResetOptions& options,
                       LuaWorkspaceResetSummary* summary = nullptr,
                       std::string* error = nullptr);

}  // namespace refii::lua
