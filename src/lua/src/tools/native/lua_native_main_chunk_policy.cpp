#include "tools/native/lua_native_main_chunk_policy.h"

#include "tools/decompiler/rendered/rendered_readability.h"
#include "tools/decompiler/repair/lua_decompile_repair_context.h"
#include "tools/decompiler/lua_decompiler_internal.h"

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <filesystem>
#include <string>

#if defined(_WIN32)
#include <process.h>
#else
#include <unistd.h>
#endif

namespace refii::lua::internal {
namespace {

constexpr std::uintmax_t kLargeNativeMainChunkSubprocessThresholdBytes = 192 * 1024;
constexpr std::uintmax_t kLargeNativeMainChunkInProcessFallbackMaxBytes = 320 * 1024;

} // namespace

bool IsLargeNativeMainChunkInProcessCandidate(const std::vector<char>& input_bytes) {
    if (input_bytes.size() < kLargeNativeMainChunkSubprocessThresholdBytes ||
        input_bytes.size() > kLargeNativeMainChunkInProcessFallbackMaxBytes ||
        !IsLuaBytecode(input_bytes)) {
        return false;
    }

    const ParsedChunkCacheEntry& cache_entry = GetParsedChunkCacheEntry(input_bytes);
    if (!cache_entry.success) {
        return false;
    }

    const refii::lua::Lua51ChunkStats& stats = cache_entry.chunk_stats;
    return stats.function_count <= 224 && stats.instruction_count <= 24000;
}

bool IsTriviallyEmptyLuaMainChunk(const refii::lua::Lua51Function& function) {
    if (!function.child_functions.empty()) {
        return false;
    }

    const std::vector<refii::lua::Lua51Instruction> instructions =
        refii::lua::DecodeLua51Instructions(function);
    if (instructions.empty()) {
        return true;
    }

    std::size_t cursor = 0;
    while (cursor < instructions.size() &&
           instructions[cursor].opcode == refii::lua::Lua51Opcode::kClose) {
        ++cursor;
    }
    if (cursor >= instructions.size()) {
        return false;
    }

    if (cursor + 1 != instructions.size()) {
        return false;
    }

    const refii::lua::Lua51Instruction& tail = instructions[cursor];
    return tail.opcode == refii::lua::Lua51Opcode::kReturn && tail.a == 0 &&
           tail.b == 1;
}

bool IsTriviallyEmptyLuaMainChunk(const std::vector<char>& input_bytes) {
    if (!IsLuaBytecode(input_bytes)) {
        return false;
    }

    const ParsedChunkCacheEntry& cache_entry = GetParsedChunkCacheEntry(input_bytes);
    return cache_entry.success && IsTriviallyEmptyLuaMainChunk(cache_entry.chunk.main);
}

bool ShouldForceTimedNativeMainChunkSubprocess(const std::vector<char>& input_bytes) {
    const DecompileRepairContext& context = GetCurrentDecompileRepairContext();
    return context.deadline_active && context.outlier_candidate &&
           !context.previous_failed_file && !context.unfinished_active &&
           IsNativeMainChunkOutlierCandidate(input_bytes);
}

bool IsCanonicalNativeDebugBankInputPath(const std::filesystem::path& input_path) {
    const std::string normalized = NormalizePathString(input_path.generic_string());
    return normalized.find("/gamescripts/scripts/") != std::string::npos ||
           normalized.find("/scripts/") != std::string::npos ||
           StartsWith(normalized, "gamescripts/scripts/") ||
           StartsWith(normalized, "scripts/");
}

bool ShouldPreferInProcessNativeMainChunkForCanonicalHoldout(
    const std::vector<char>& input_bytes,
    const std::filesystem::path& input_path) {
    if (!IsLuaBytecode(input_bytes) || !IsCanonicalNativeDebugBankInputPath(input_path)) {
        return false;
    }

    if (IsNativeMainChunkOutlierCandidate(input_bytes)) {
        return false;
    }

    const ParsedChunkCacheEntry& cache_entry = GetParsedChunkCacheEntry(input_bytes);
    if (!cache_entry.success) {
        return false;
    }

    const refii::lua::Lua51ChunkStats& stats = cache_entry.chunk_stats;
    return stats.function_count <= 224 && stats.instruction_count <= 24000;
}

bool ShouldUseNativeMainChunkSubprocess(const std::vector<char>& input_bytes) {
    return ShouldForceTimedNativeMainChunkSubprocess(input_bytes) ||
           (input_bytes.size() >= kLargeNativeMainChunkSubprocessThresholdBytes &&
            !IsLargeNativeMainChunkInProcessCandidate(input_bytes));
}

bool ShouldAllowInProcessNativeMainChunkFallback(const std::vector<char>& input_bytes) {
    if (ShouldForceTimedNativeMainChunkSubprocess(input_bytes)) {
        return false;
    }
    return IsLargeNativeMainChunkInProcessCandidate(input_bytes);
}

bool ShouldPreferRawNativeMainChunkCandidate(const std::vector<char>& input_bytes) {
    if (!IsLuaBytecode(input_bytes)) {
        return false;
    }

    const ParsedChunkCacheEntry& cache_entry = GetParsedChunkCacheEntry(input_bytes);
    if (!cache_entry.success) {
        return false;
    }

    const refii::lua::Lua51ChunkStats& stats = cache_entry.chunk_stats;
    const refii::lua::Lua51OperationSummary& operations = cache_entry.operation_summary;
    const refii::lua::Lua51ControlFlowSummary& control_flow =
        cache_entry.control_flow_summary;
    const bool table_heavy =
        stats.instruction_count >= 8000 &&
        operations.table_set_count >= 1000 &&
        operations.table_set_count > operations.call_count * 2;
    const bool control_flow_heavy =
        stats.instruction_count >= 6000 &&
        control_flow.conditional_branch_count >= 500 &&
        control_flow.if_else_block_count >= 80 &&
        control_flow.max_branch_span >= 256 &&
        operations.call_count >= 500;
    return table_heavy || control_flow_heavy;
}

bool IsNativeMainChunkOutlierCandidate(const std::vector<char>& input_bytes) {
    if (!IsLuaBytecode(input_bytes)) {
        return false;
    }

    const ParsedChunkCacheEntry& cache_entry = GetParsedChunkCacheEntry(input_bytes);
    if (!cache_entry.success) {
        return input_bytes.size() >= kLargeNativeMainChunkSubprocessThresholdBytes;
    }

    const refii::lua::Lua51ChunkStats& stats = cache_entry.chunk_stats;
    const refii::lua::Lua51OperationSummary& operations = cache_entry.operation_summary;
    const refii::lua::Lua51ControlFlowSummary& control_flow =
        cache_entry.control_flow_summary;
    return stats.instruction_count >= 8000 ||
           (stats.instruction_count >= 6000 &&
            control_flow.conditional_branch_count >= 300 &&
            control_flow.max_branch_span >= 256) ||
           input_bytes.size() >= kLargeNativeMainChunkSubprocessThresholdBytes ||
           (operations.table_set_count >= 1000 &&
            operations.table_set_count > operations.call_count * 2);
}

unsigned int GetNativeMainChunkRenderTimeoutSeconds(const std::vector<char>& input_bytes,
                                                    unsigned int default_timeout_seconds) {
    if (GetCurrentDecompileRepairContext().deadline_active) {
        return default_timeout_seconds;
    }
    if (ShouldUseNativeMainChunkSubprocess(input_bytes)) {
        unsigned int timeout_seconds = std::max(default_timeout_seconds, 45u);
        if (input_bytes.size() > 200000) {
            timeout_seconds = std::max(timeout_seconds, 120u);
        }
        if (input_bytes.size() > 275000) {
            timeout_seconds = std::max(timeout_seconds, 240u);
        }
        return timeout_seconds;
    }
    if (ShouldAllowInProcessNativeMainChunkFallback(input_bytes)) {
        return std::max(default_timeout_seconds, 20u);
    }
    return default_timeout_seconds;
}

bool IsEligibleLargeValidatedFastFinalize(std::string_view candidate_source,
                                          std::string_view note,
                                          std::string_view note_prefix) {
    const bool matched_large_quality =
        note.find(std::string(note_prefix) + "_quality_relaxed_structured_tables=") !=
            std::string_view::npos ||
        note.find(std::string(note_prefix) + "_quality_relaxed_large_body=") !=
            std::string_view::npos;
    if (!matched_large_quality) {
        return false;
    }

    if (candidate_source.find("DECOMPILER ERROR") != std::string_view::npos ||
        candidate_source.find("do return end") != std::string_view::npos ||
        candidate_source.find("-- refii-invalid") != std::string_view::npos ||
        candidate_source.find("Readable disassembly:") != std::string_view::npos ||
        candidate_source.find("Replace this file with valid Lua") != std::string_view::npos) {
        return false;
    }

    const LuaOutputQualitySummary quality = SummarizeLuaOutputQuality(candidate_source);
    return !HasBlockingLuaOutputQualityIssues(quality) &&
           !ContainsObviouslyInvalidNilUsage(candidate_source);
}

std::filesystem::path MakeNativeMainChunkTempOutputPath(const std::filesystem::path& input_path) {
    static std::atomic<std::uint64_t> counter{0};
#if defined(_WIN32)
    const auto process_id = static_cast<long long>(::_getpid());
#else
    const auto process_id = static_cast<long long>(::getpid());
#endif
    std::filesystem::path output =
        std::filesystem::temp_directory_path() /
        ("refii-native-main-" + std::to_string(process_id) + "-" +
         std::to_string(counter.fetch_add(1, std::memory_order_relaxed) + 1) + "-" +
         input_path.filename().generic_string());
    output += ".lua";
    return output;
}

} // namespace refii::lua::internal
