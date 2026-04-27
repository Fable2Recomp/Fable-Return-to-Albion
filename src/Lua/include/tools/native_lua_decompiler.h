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
};

enum class LuaWorkspacePipelineMode {
    kLegacySinglePass,
    kTwoPhaseExactFirst,
};

enum class LuaExactParityPolicy {
    kCanonicalOnly,
    kAlwaysForBytecode,
};

struct LuaWorkspaceDecompileOptions {
    std::filesystem::path workspace_root;
    std::filesystem::path output_root;
    std::filesystem::path workspace_tool_path;
    std::filesystem::path single_tool_path;
    std::filesystem::path disassembly_output_root;
    // 0 selects an automatic worker count that keeps some cores free.
    std::size_t worker_count = 0;
    std::size_t progress_update_stride = 1;
    unsigned int child_timeout_seconds = 2;
    unsigned int native_render_timeout_seconds = 8;
    std::uint64_t source_wall_time_budget_ms = 600000;
    bool force_external_single_tool = false;
    bool write_disassembly_fallback = true;
    LuaWorkspacePipelineMode pipeline_mode = LuaWorkspacePipelineMode::kLegacySinglePass;
    LuaExactParityPolicy exact_parity_policy = LuaExactParityPolicy::kCanonicalOnly;
    bool strict_exact_outputs = false;
    std::function<void(const LuaWorkspaceDecompileProgress&)> progress_callback;
};

struct LuaWorkspaceDecompileSummary {
    std::filesystem::path workspace_root;
    std::filesystem::path output_root;
    std::filesystem::path manifest_path;
    std::size_t total_files = 0;
    std::size_t decompiled = 0;
    std::size_t aliased = 0;
    std::size_t nonexact = 0;
    std::size_t aliased_nonexact = 0;
    std::size_t disassembled = 0;
    std::size_t invalid = 0;
    std::size_t budget_exceeded = 0;
    std::size_t copied = 0;
    std::size_t skipped = 0;
};

struct LuaWorkspaceBuildOptions {
    std::filesystem::path game_install_root;
    std::filesystem::path workspace_root;
    // 0 selects an automatic worker count that keeps some cores free.
    std::size_t worker_count = 0;
    std::size_t progress_update_stride = 1;
    unsigned int child_timeout_seconds = 2;
    unsigned int native_render_timeout_seconds = 8;
    std::uint64_t source_wall_time_budget_ms = 600000;
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

struct LuaBytecodeParityAuditOptions {
    std::filesystem::path workspace_root;
    std::filesystem::path generated_root;
    std::filesystem::path report_root;
    std::filesystem::path single_tool_path;
    std::filesystem::path oracle_unluac_path;
    std::size_t progress_update_stride = 16;
    unsigned int child_timeout_seconds = 2;
    std::function<void(const LuaWorkspaceDecompileProgress&)> progress_callback;
};

struct LuaBytecodeParityAuditSummary {
    std::filesystem::path workspace_root;
    std::filesystem::path generated_root;
    std::filesystem::path report_root;
    std::filesystem::path parity_report_path;
    std::filesystem::path summary_report_path;
    std::size_t total_files = 0;
    std::size_t exact_matches = 0;
    std::size_t current_best = 0;
    std::size_t unluac_best = 0;
    std::size_t no_candidate_exact = 0;
    std::size_t compile_or_parse_failures = 0;
    std::size_t non_bytecode_sources = 0;
};

std::filesystem::path GetDefaultLuaDecompiledOutputRoot(const std::filesystem::path& workspace_root);
std::filesystem::path GetDefaultLuaDecompileInputRoot(const std::filesystem::path& workspace_root);
std::filesystem::path GetLuaWorkspacePath(const std::filesystem::path& game_install_root);
std::filesystem::path GetLuaWorkspaceSourcePath(const std::filesystem::path& game_install_root);
std::filesystem::path GetLuaWorkspaceBaselineRoot(const std::filesystem::path& workspace_root);

const char* DescribeLuaWorkspaceVerifyStatus(LuaWorkspaceVerifyStatus status);

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

bool AuditLuaWorkspaceBytecodeParity(const LuaBytecodeParityAuditOptions& options,
                                     LuaBytecodeParityAuditSummary* summary = nullptr,
                                     std::string* error = nullptr);

}  // namespace refii::lua
