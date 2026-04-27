#include "tools/lua_workspace_decompile_pipeline.h"

#include "lua_script_paths.h"
#include "tools/lua_decompiler_internal.h"
#include "tools/native_lua_chunk.h"
#include "tools/straight_line_native_render.h"
#include "tools/decompiler/lua51_analysis.h"
#include "tools/decompiler/lua_script_profile_policy.h"
#include "tools/decompiler/operation_build.h"

#include <chrono>
#include <algorithm>
#include <atomic>
#include <cstdlib>
#include <cstdint>
#include <exception>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <limits>
#include <mutex>
#include <optional>
#include <sstream>
#include <string_view>
#include <thread>
#include <tuple>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace refii::lua::workspace {

namespace {

constexpr std::size_t kManifestFlushStride = 32;
constexpr std::string_view kWorkspacePersistentCacheVersion = "refii_lua_workspace_cache_v5";
constexpr std::string_view kWorkspaceStateVersionFileName = ".workspace_version";
constexpr std::string_view kManifestHeader = "status\tinput\toutput\tsource_name\tnote\n";
constexpr std::string_view kTimingHeader = "wall_time_ms\tstatus\tinput\tsource_name\n";
constexpr std::string_view kLiveManifestFileName = "_manifest.live.tsv";
constexpr std::string_view kLiveTimingFileName = "_timing.live.tsv";

template <typename Fn>
class ScopeExit {
public:
    explicit ScopeExit(Fn fn) : fn_(std::move(fn)) {}
    ScopeExit(const ScopeExit&) = delete;
    ScopeExit& operator=(const ScopeExit&) = delete;

    ~ScopeExit() { fn_(); }

private:
    Fn fn_;
};

template <typename Fn>
ScopeExit<Fn> MakeScopeExit(Fn fn) {
    return ScopeExit<Fn>(std::move(fn));
}

bool ShouldPreserveFailedOutputs() {
    const char* value = std::getenv("REFII_LUA_PRESERVE_FAILED_OUTPUTS");
    if (value == nullptr) {
        return false;
    }
    const std::string_view text(value);
    return !text.empty() && text != "0" && text != "false" && text != "FALSE";
}

std::string MakeCacheKey(const std::filesystem::path& path) {
    return internal::NormalizePathString(
        std::filesystem::absolute(path).lexically_normal().generic_string());
}

struct CacheEntry {
    bool attempted = false;
    enum class Result {
        kFailed,
        kDecompiled,
        kNonexact,
        kDisassembled,
    } result = Result::kFailed;
    std::filesystem::path output_path;
    std::string note;
    bool exact_parity_required = false;
    bool exact_parity_satisfied = false;
};

struct PersistentCacheEntry {
    std::string version;
    std::string bytecode_hash;
    std::string result;
    std::string note;
    std::string preferred_render_mode;
    std::string outlier_classification;
};

using ManifestStatusMap = std::unordered_map<std::string, std::string>;
using ManifestInputSet = std::unordered_set<std::string>;

constexpr std::size_t kMinimumManifestRowsForFrontierInference = 128;

struct ManifestStatusSnapshot {
    ManifestStatusMap statuses;
    std::unordered_map<std::string, std::string> manifest_rows;
    std::size_t row_count = 0;
    bool has_input_index_bound = false;
    std::size_t highest_input_index = 0;
};

struct ManifestRowRecord {
    std::string status;
    std::string input_relative;
    std::string output_relative;
    std::string source_name;
    std::string note;
};

struct PreparedInput {
    std::filesystem::path relative;
    internal::ResolvedInput resolved;
    std::filesystem::path input_path;
    std::filesystem::path output_relative;
    std::filesystem::path output_path;
    std::filesystem::path failed_output_path;
    std::filesystem::path disassembly_output_path;
    std::string cache_key;
    std::string self_cache_key;
};

enum class SourceProcessKind {
    kSkipped,
    kInvalid,
    kBudgetExceeded,
    kCopied,
    kDecompiled,
    kNonexact,
    kDisassembled,
    kFailed,
};

bool IsUsableLuaProcessKind(SourceProcessKind kind) {
    return kind == SourceProcessKind::kCopied ||
           kind == SourceProcessKind::kDecompiled ||
           kind == SourceProcessKind::kNonexact;
}

bool IsTerminalFallbackProcessKind(SourceProcessKind kind) {
    return kind == SourceProcessKind::kFailed ||
           kind == SourceProcessKind::kDisassembled ||
           kind == SourceProcessKind::kInvalid ||
           kind == SourceProcessKind::kBudgetExceeded;
}

bool TryUnwrapRawTopLevelVarargFunction(std::string& source) {
    constexpr std::string_view kPrefix = "function(...)\n";
    if (!internal::StartsWith(source, kPrefix)) {
        return false;
    }
    std::size_t end_pos = source.size();
    while (end_pos > kPrefix.size() &&
           (source[end_pos - 1] == '\n' || source[end_pos - 1] == '\r' ||
            source[end_pos - 1] == ' ' || source[end_pos - 1] == '\t')) {
        --end_pos;
    }
    constexpr std::string_view kSuffix = "end";
    if (end_pos < kPrefix.size() + kSuffix.size() ||
        source.compare(end_pos - kSuffix.size(), kSuffix.size(), kSuffix) != 0) {
        return false;
    }
    source = source.substr(kPrefix.size(), end_pos - kPrefix.size() - kSuffix.size());
    if (source.empty() || source.back() != '\n') {
        source.push_back('\n');
    }
    return true;
}

enum class SourceProcessPhase {
    kLegacy,
    kPhase1LightProbe,
    kPhase2HoldoutRepair,
};

struct SourceProcessResult {
    SourceProcessKind kind = SourceProcessKind::kFailed;
    std::filesystem::path artifact_path;
    std::string source_name;
    std::string note;
    std::uint64_t wall_time_ms = 0;
    bool exact_parity_required = false;
    bool exact_parity_satisfied = false;
};

enum class MaterializedResultKind {
    kCopied,
    kAliased,
    kNonexact,
    kAliasedNonexact,
    kDecompiled,
    kDisassembled,
    kInvalid,
    kBudgetExceeded,
    kSkipped,
};

struct FileProgressSnapshot {
    bool exists = false;
    std::uintmax_t size = 0;
    std::filesystem::file_time_type last_write_time{};
};

std::filesystem::path MapOutputRelativePath(const std::filesystem::path& relative_path) {
    return refii::lua::CanonicalizeRetailLuaRelativePath(relative_path);
}

std::string ComputeStableBytecodeHash(const std::vector<char>& bytes) {
    std::uint64_t hash = 1469598103934665603ull;
    for (const char byte : bytes) {
        hash ^= static_cast<unsigned char>(byte);
        hash *= 1099511628211ull;
    }

    std::ostringstream stream;
    stream << std::hex << std::setw(16) << std::setfill('0') << hash;
    return stream.str();
}

std::string DescribeToolIdentity(const std::filesystem::path& path) {
    if (path.empty()) {
        return "missing";
    }

    std::error_code ec;
    const std::filesystem::path absolute = std::filesystem::absolute(path, ec);
    const std::filesystem::path normalized =
        (ec ? path : absolute).lexically_normal();
    const bool exists = std::filesystem::exists(normalized, ec);
    if (ec || !exists) {
        return internal::NormalizePathString(normalized.generic_string()) + "|missing";
    }

    const auto size = std::filesystem::file_size(normalized, ec);
    const auto write_time = std::filesystem::last_write_time(normalized, ec);
    long long write_ticks = 0;
    if (!ec) {
        write_ticks = static_cast<long long>(write_time.time_since_epoch().count());
    }

    std::ostringstream stream;
    stream << internal::NormalizePathString(normalized.generic_string()) << "|size="
           << (ec ? 0 : static_cast<unsigned long long>(size)) << "|mtime=" << write_ticks;
    return stream.str();
}

std::string BuildWorkspacePersistentCacheVersion(const LuaWorkspaceDecompileOptions& options) {
    std::string version(kWorkspacePersistentCacheVersion);
    version += "|pipeline=";
    version += options.pipeline_mode == LuaWorkspacePipelineMode::kTwoPhaseExactFirst
                   ? "two_phase_exact_first"
                   : "legacy_single_pass";
    version += "|parity=";
    version += options.exact_parity_policy == LuaExactParityPolicy::kAlwaysForBytecode
                   ? "always_for_bytecode"
                   : "canonical_only";
    version += "|strict_exact=";
    version += options.strict_exact_outputs ? "1" : "0";
    return version;
}

std::string ReadWorkspaceStateVersion(const std::filesystem::path& path) {
    std::string text;
    if (!internal::ReadTextFile(path, text)) {
        return {};
    }

    const std::string_view trimmed = internal::TrimWhitespace(text);
    return std::string(trimmed);
}

bool WriteWorkspaceStateVersion(const std::filesystem::path& path, std::string_view version) {
    std::filesystem::create_directories(path.parent_path());
    return internal::WriteTextFile(path, std::string(version) + "\n");
}

std::size_t ResolveLuaWorkspaceWorkerCount(const LuaWorkspaceDecompileOptions& options,
                                           std::size_t unique_job_count) {
    if (unique_job_count == 0) {
        return 1;
    }

    std::size_t worker_count = options.worker_count;
    if (worker_count == 0) {
        const std::size_t reported_core_count = std::thread::hardware_concurrency();
        const std::size_t available_core_count =
            reported_core_count == 0 ? 1 : reported_core_count;
        if (available_core_count <= 2) {
            worker_count = 1;
        } else if (available_core_count <= 4) {
            worker_count = available_core_count - 1;
        } else {
            // Keep auto mode responsive for interactive use by leaving at least
            // two cores free on larger machines and otherwise aiming for about
            // half of the reported cores.
            worker_count = std::min(available_core_count - 2,
                                    (available_core_count + 1) / 2);
        }
    }

    worker_count = std::max<std::size_t>(1, worker_count);
    worker_count = std::min(worker_count, unique_job_count);
    return worker_count;
}

std::filesystem::path MakePersistentCacheMetadataPath(const std::filesystem::path& cache_root,
                                                      const std::filesystem::path& relative_path) {
    std::filesystem::path metadata_path = cache_root / ".meta" / relative_path;
    metadata_path += ".tsv";
    return metadata_path;
}

bool ParsePersistentCacheLine(std::string_view line,
                              std::string_view key,
                              std::string* value_out) {
    if (value_out == nullptr) {
        return false;
    }
    const std::string prefix = std::string(key) + "\t";
    if (!internal::StartsWith(line, prefix)) {
        return false;
    }
    *value_out = std::string(line.substr(prefix.size()));
    return true;
}

bool ReadPersistentCacheEntry(const std::filesystem::path& path,
                              PersistentCacheEntry* entry_out) {
    if (entry_out == nullptr) {
        return false;
    }

    std::string text;
    if (!internal::ReadTextFile(path, text)) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = internal::SplitLines(text, &had_trailing_newline);
    if (lines.size() < 4) {
        return false;
    }

    PersistentCacheEntry entry;
    bool have_version = false;
    bool have_bytecode_hash = false;
    bool have_result = false;
    bool have_note = false;
    for (const std::string& line : lines) {
        have_version =
            ParsePersistentCacheLine(line, "version", &entry.version) || have_version;
        have_bytecode_hash =
            ParsePersistentCacheLine(line, "bytecode_hash", &entry.bytecode_hash) ||
            have_bytecode_hash;
        have_result = ParsePersistentCacheLine(line, "result", &entry.result) || have_result;
        have_note = ParsePersistentCacheLine(line, "note", &entry.note) || have_note;
        ParsePersistentCacheLine(line, "preferred_render_mode", &entry.preferred_render_mode);
        ParsePersistentCacheLine(line, "outlier_classification", &entry.outlier_classification);
    }

    if (!have_version || !have_bytecode_hash || !have_result || !have_note) {
        return false;
    }

    *entry_out = std::move(entry);
    return true;
}

bool WritePersistentCacheEntry(const std::filesystem::path& path,
                               const PersistentCacheEntry& entry) {
    std::filesystem::create_directories(path.parent_path());
    std::string text;
    text.reserve(entry.note.size() + 128);
    text += "version\t";
    text += entry.version;
    text += "\nbytecode_hash\t";
    text += entry.bytecode_hash;
    text += "\nresult\t";
    text += entry.result;
    text += "\nnote\t";
    text += entry.note;
    if (!entry.preferred_render_mode.empty()) {
        text += "\npreferred_render_mode\t";
        text += entry.preferred_render_mode;
    }
    if (!entry.outlier_classification.empty()) {
        text += "\noutlier_classification\t";
        text += entry.outlier_classification;
    }
    text += "\n";
    return internal::WriteTextFile(path, text);
}

ManifestStatusSnapshot LoadManifestStatusSnapshot(const std::filesystem::path& manifest_path) {
    ManifestStatusSnapshot snapshot;

    std::ifstream stream(manifest_path, std::ios::binary);
    if (!stream) {
        return snapshot;
    }

    std::string line;
    bool first_line = true;
    while (std::getline(stream, line)) {
        if (first_line) {
            first_line = false;
            continue;
        }
        if (line.empty()) {
            continue;
        }

        const std::size_t first_tab = line.find('\t');
        if (first_tab == std::string::npos) {
            continue;
        }
        const std::size_t second_tab = line.find('\t', first_tab + 1);
        if (second_tab == std::string::npos || second_tab <= first_tab + 1) {
            continue;
        }

        const std::string status = line.substr(0, first_tab);
        const std::string input_relative =
            internal::NormalizePathString(
                line.substr(first_tab + 1, second_tab - first_tab - 1));
        if (status.empty() || input_relative.empty()) {
            continue;
        }

        snapshot.statuses[input_relative] = status;
        snapshot.manifest_rows[input_relative] = line + "\n";
        ++snapshot.row_count;
    }

    if (snapshot.row_count > 0) {
        snapshot.has_input_index_bound = true;
        snapshot.highest_input_index = snapshot.row_count - 1;
    }

    return snapshot;
}

bool TryParseManifestStatusLine(std::string_view line,
                                std::string* status,
                                std::string* input_relative) {
    if (status == nullptr || input_relative == nullptr || line.empty()) {
        return false;
    }

    const std::size_t first_tab = line.find('\t');
    if (first_tab == std::string::npos) {
        return false;
    }
    const std::size_t second_tab = line.find('\t', first_tab + 1);
    if (second_tab == std::string::npos || second_tab <= first_tab + 1) {
        return false;
    }

    *status = std::string(line.substr(0, first_tab));
    *input_relative = internal::NormalizePathString(
        std::string(line.substr(first_tab + 1, second_tab - first_tab - 1)));
    return !status->empty() && !input_relative->empty();
}

bool TryParseManifestRowText(std::string_view line, ManifestRowRecord* record) {
    if (record == nullptr || line.empty()) {
        return false;
    }

    const std::size_t first_tab = line.find('\t');
    if (first_tab == std::string::npos) {
        return false;
    }
    const std::size_t second_tab = line.find('\t', first_tab + 1);
    if (second_tab == std::string::npos) {
        return false;
    }
    const std::size_t third_tab = line.find('\t', second_tab + 1);
    if (third_tab == std::string::npos) {
        return false;
    }
    const std::size_t fourth_tab = line.find('\t', third_tab + 1);
    if (fourth_tab == std::string::npos) {
        return false;
    }

    record->status = std::string(line.substr(0, first_tab));
    record->input_relative = internal::NormalizePathString(
        std::string(line.substr(first_tab + 1, second_tab - first_tab - 1)));
    record->output_relative = std::string(line.substr(second_tab + 1,
                                                      third_tab - second_tab - 1));
    record->source_name = std::string(line.substr(third_tab + 1,
                                                  fourth_tab - third_tab - 1));
    record->note = std::string(line.substr(fourth_tab + 1));
    return !record->status.empty() && !record->input_relative.empty();
}

ManifestStatusMap LoadExistingManifestStatuses(const std::filesystem::path& manifest_path) {
    return LoadManifestStatusSnapshot(manifest_path).statuses;
}

ManifestStatusSnapshot LoadManifestStatusSnapshotFromRowCache(
    const std::filesystem::path& manifest_row_root) {
    ManifestStatusSnapshot snapshot;
    std::error_code error;
    if (!std::filesystem::exists(manifest_row_root, error)) {
        return snapshot;
    }

    for (std::filesystem::directory_iterator it(manifest_row_root, error), end;
         !error && it != end;
         it.increment(error)) {
        if (error || !it->is_regular_file() || it->path().extension() != ".tsv") {
            continue;
        }

        std::size_t input_index = 0;
        try {
            input_index = static_cast<std::size_t>(std::stoull(it->path().stem().string()));
        } catch (...) {
            continue;
        }

        std::string row_text;
        if (!internal::ReadTextFile(it->path(), row_text)) {
            continue;
        }

        std::size_t line_end = row_text.find('\n');
        if (line_end == std::string::npos) {
            line_end = row_text.size();
        }
        while (line_end > 0 &&
               (row_text[line_end - 1] == '\r' || row_text[line_end - 1] == '\n')) {
            --line_end;
        }

        std::string status;
        std::string input_relative;
        if (!TryParseManifestStatusLine(std::string_view(row_text.data(), line_end),
                                        &status,
                                        &input_relative)) {
            continue;
        }

        snapshot.statuses[input_relative] = status;
        std::string row_line(row_text.data(), line_end);
        if (row_line.empty() || row_line.back() != '\n') {
            row_line.push_back('\n');
        }
        snapshot.manifest_rows[input_relative] = std::move(row_line);
        ++snapshot.row_count;
        if (!snapshot.has_input_index_bound || input_index > snapshot.highest_input_index) {
            snapshot.has_input_index_bound = true;
            snapshot.highest_input_index = input_index;
        }
    }

    return snapshot;
}

bool ShouldPreferLiveManifestSnapshot(const ManifestStatusSnapshot& final_snapshot,
                                      const ManifestStatusSnapshot& live_snapshot,
                                      const std::filesystem::path& final_manifest_path,
                                      const std::filesystem::path& live_manifest_path) {
    if (live_snapshot.row_count == 0) {
        return false;
    }
    if (final_snapshot.row_count == 0) {
        return true;
    }

    std::error_code final_error;
    std::error_code live_error;
    const auto final_time = std::filesystem::last_write_time(final_manifest_path, final_error);
    const auto live_time = std::filesystem::last_write_time(live_manifest_path, live_error);
    if (final_error || live_error || live_time <= final_time) {
        return false;
    }

    return live_snapshot.row_count + 8 >= final_snapshot.row_count;
}

ManifestStatusSnapshot LoadAuthoritativeManifestStatuses(
    const std::filesystem::path& final_manifest_path,
    const std::filesystem::path& live_manifest_path,
    const std::filesystem::path& manifest_row_root) {
    const ManifestStatusSnapshot final_snapshot =
        LoadManifestStatusSnapshot(final_manifest_path);
    const ManifestStatusSnapshot live_snapshot =
        LoadManifestStatusSnapshot(live_manifest_path);
    const ManifestStatusSnapshot row_cache_snapshot =
        LoadManifestStatusSnapshotFromRowCache(manifest_row_root);

    ManifestStatusSnapshot authoritative_snapshot = final_snapshot;
    if (ShouldPreferLiveManifestSnapshot(final_snapshot,
                                         live_snapshot,
                                         final_manifest_path,
                                         live_manifest_path)) {
        authoritative_snapshot = live_snapshot;
    }

    // Row-cache status rows are useful as a supplement when live/final manifests have been
    // truncated by interrupted reruns, but the row-cache index space is sparse across runs, so
    // it is not a reliable "frontier reached" signal.
    for (const auto& [input_relative, status] : row_cache_snapshot.statuses) {
        authoritative_snapshot.statuses.emplace(input_relative, status);
    }
    for (const auto& [input_relative, row_text] : row_cache_snapshot.manifest_rows) {
        authoritative_snapshot.manifest_rows.emplace(input_relative, row_text);
    }

    if (authoritative_snapshot.row_count < kMinimumManifestRowsForFrontierInference) {
        authoritative_snapshot.has_input_index_bound = false;
        authoritative_snapshot.highest_input_index = 0;
    }

    return authoritative_snapshot;
}

FileProgressSnapshot CaptureFileProgressSnapshot(const std::filesystem::path& path) {
    FileProgressSnapshot snapshot;
    std::error_code error;
    snapshot.exists = std::filesystem::exists(path, error);
    if (error || !snapshot.exists) {
        snapshot.exists = false;
        return snapshot;
    }

    snapshot.size = std::filesystem::file_size(path, error);
    if (error) {
        snapshot.size = 0;
        error.clear();
    }
    snapshot.last_write_time = std::filesystem::last_write_time(path, error);
    return snapshot;
}

bool DidFileProgressAdvance(const FileProgressSnapshot& before,
                            const std::filesystem::path& path) {
    const FileProgressSnapshot after = CaptureFileProgressSnapshot(path);
    if (!before.exists) {
        return after.exists;
    }
    if (!after.exists) {
        return false;
    }
    return after.last_write_time != before.last_write_time || after.size != before.size;
}

bool HasActiveFileProgressAdvanced(const FileProgressSnapshot& cache_before,
                                   const std::filesystem::path& cache_path,
                                   const FileProgressSnapshot& output_before,
                                   const std::filesystem::path& output_path) {
    return DidFileProgressAdvance(cache_before, cache_path) ||
           DidFileProgressAdvance(output_before, output_path);
}

bool HasUnresolvedMaterializedOutput(const std::filesystem::path& output_root,
                                     const std::filesystem::path& relative) {
    const std::filesystem::path output_relative = MapOutputRelativePath(relative);
    const std::filesystem::path output_path = output_root / output_relative;
    const std::filesystem::path failed_output_path =
        output_root / "_failed" / output_relative;
    const std::filesystem::path disassembly_output_path =
        output_root / "_disasm" / output_relative;

    return !internal::HasUsableDecompiledLuaOutput(output_path, {}) ||
           std::filesystem::exists(failed_output_path) ||
           std::filesystem::exists(disassembly_output_path);
}

bool WorkspaceHasMaterializedLuaOutputs(const std::filesystem::path& output_root) {
    std::error_code error;
    if (!std::filesystem::exists(output_root, error)) {
        return false;
    }

    for (std::filesystem::recursive_directory_iterator it(output_root, error), end;
         !error && it != end;
         it.increment(error)) {
        if (error || !it->is_regular_file() || it->path().extension() != ".lua") {
            continue;
        }

        const std::string normalized = internal::NormalizePathString(
            std::filesystem::relative(it->path(), output_root).generic_string());
        if (internal::StartsWith(normalized, ".source/") ||
            internal::StartsWith(normalized, ".cache/") ||
            internal::StartsWith(normalized, "_failed/") ||
            internal::StartsWith(normalized, "_disasm/")) {
            continue;
        }
        return true;
    }

    return false;
}

ManifestInputSet FindCanonicalInputsMissingFromManifest(
    const std::vector<std::filesystem::path>& relative_inputs,
    const ManifestStatusSnapshot& previous_snapshot,
    const std::filesystem::path& output_root,
    bool inspect_output_state) {
    ManifestInputSet missing_inputs;
    if (previous_snapshot.statuses.empty() && !inspect_output_state) {
        return missing_inputs;
    }

    for (std::size_t input_index = 0; input_index < relative_inputs.size(); ++input_index) {
        const std::filesystem::path& relative = relative_inputs[input_index];
        const std::string normalized =
            internal::NormalizePathString(relative.generic_string());
        const auto existing = previous_snapshot.statuses.find(normalized);
        const bool missing_from_manifest =
            previous_snapshot.has_input_index_bound &&
            input_index <= previous_snapshot.highest_input_index &&
            existing == previous_snapshot.statuses.end();
        const bool unresolved_materialization =
            inspect_output_state && HasUnresolvedMaterializedOutput(output_root, relative);
        if (missing_from_manifest || unresolved_materialization) {
            missing_inputs.insert(normalized);
        }
    }
    return missing_inputs;
}

bool ShouldUseGuardedExternalDecompiler(bool previous_failed_file,
                                        bool unfinished_active,
                                        bool allow_native_retry) {
    if (allow_native_retry) {
        return false;
    }
    return previous_failed_file || unfinished_active;
}

unsigned int GetGuardedExternalDecompilerTimeoutSeconds(const std::vector<char>& input_bytes,
                                                        unsigned int default_timeout_seconds) {
    unsigned int timeout_seconds = std::max(default_timeout_seconds, 20u);
    if (internal::IsLuaBytecode(input_bytes)) {
        const auto& parsed_chunk = internal::GetParsedChunkCacheEntry(input_bytes);
        if (parsed_chunk.success) {
            if (parsed_chunk.chunk_stats.instruction_count >= 5000 ||
                parsed_chunk.chunk_stats.function_count >= 32) {
                timeout_seconds = std::max(timeout_seconds, 60u);
            }
            if (parsed_chunk.chunk_stats.instruction_count >= 10000 ||
                parsed_chunk.chunk_stats.function_count >= 96) {
                timeout_seconds = std::max(timeout_seconds, 120u);
            }
        }
    }
    if (input_bytes.size() > 120000) {
        timeout_seconds = std::max(timeout_seconds, 60u);
    }
    if (input_bytes.size() > 220000) {
        timeout_seconds = std::max(timeout_seconds, 180u);
    }
    return timeout_seconds;
}

std::uint64_t GetElapsedWallTimeMs(const std::chrono::steady_clock::time_point& started) {
    return static_cast<std::uint64_t>(
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - started)
            .count());
}

bool HasExceededSourceWallTimeBudget(const LuaWorkspaceDecompileOptions& options,
                                     const std::chrono::steady_clock::time_point& started) {
    return options.source_wall_time_budget_ms != 0 &&
           GetElapsedWallTimeMs(started) >= options.source_wall_time_budget_ms;
}

std::uint64_t GetRemainingSourceWallTimeBudgetMs(
    const LuaWorkspaceDecompileOptions& options,
    const std::chrono::steady_clock::time_point& started) {
    if (options.source_wall_time_budget_ms == 0) {
        return std::numeric_limits<std::uint64_t>::max();
    }
    const std::uint64_t elapsed_ms = GetElapsedWallTimeMs(started);
    if (elapsed_ms >= options.source_wall_time_budget_ms) {
        return 0;
    }
    return options.source_wall_time_budget_ms - elapsed_ms;
}

unsigned int ClampTimeoutSecondsToSourceWallTimeBudget(
    unsigned int timeout_seconds,
    const LuaWorkspaceDecompileOptions& options,
    const std::chrono::steady_clock::time_point& started) {
    const std::uint64_t remaining_ms = GetRemainingSourceWallTimeBudgetMs(options, started);
    if (remaining_ms == std::numeric_limits<std::uint64_t>::max()) {
        return timeout_seconds;
    }
    if (remaining_ms == 0) {
        return 0;
    }

    const std::uint64_t remaining_seconds = (remaining_ms + 999ull) / 1000ull;
    const std::uint64_t capped_timeout_seconds =
        timeout_seconds == 0
            ? remaining_seconds
            : std::min<std::uint64_t>(timeout_seconds, remaining_seconds);
    return static_cast<unsigned int>(std::max<std::uint64_t>(1, capped_timeout_seconds));
}

LuaWorkspaceDecompileOptions BuildBudgetedDecompileOptions(
    const LuaWorkspaceDecompileOptions& options,
    const std::chrono::steady_clock::time_point& started) {
    LuaWorkspaceDecompileOptions budgeted_options = options;
    if (options.source_wall_time_budget_ms == 0) {
        return budgeted_options;
    }

    budgeted_options.child_timeout_seconds =
        ClampTimeoutSecondsToSourceWallTimeBudget(options.child_timeout_seconds,
                                                  options,
                                                  started);
    budgeted_options.native_render_timeout_seconds =
        ClampTimeoutSecondsToSourceWallTimeBudget(options.native_render_timeout_seconds,
                                                  options,
                                                  started);
    return budgeted_options;
}

std::string BuildSourceWallTimeBudgetExceededNote(const LuaWorkspaceDecompileOptions& options) {
    return "source_wall_time_budget_exceeded_" +
           std::to_string(options.source_wall_time_budget_ms) + "ms";
}

std::string BuildManifestRowText(const char* status,
                                 const std::filesystem::path& input_relative,
                                 const std::filesystem::path& output_relative,
                                 const std::string& source_name,
                                 const std::string& note) {
    const std::string sanitized_note = internal::JoinNotes(note, {});
    std::ostringstream row;
    row << status << "\t" << input_relative.generic_string() << "\t"
        << output_relative.generic_string() << "\t" << source_name << "\t"
        << sanitized_note << "\n";
    return row.str();
}

const char* DescribeMaterializedResultKind(MaterializedResultKind kind) {
    switch (kind) {
    case MaterializedResultKind::kCopied:
        return "copied";
    case MaterializedResultKind::kAliased:
        return "aliased";
    case MaterializedResultKind::kNonexact:
        return "nonexact";
    case MaterializedResultKind::kAliasedNonexact:
        return "aliased_nonexact";
    case MaterializedResultKind::kDecompiled:
        return "decompiled";
    case MaterializedResultKind::kDisassembled:
        return "disassembled";
    case MaterializedResultKind::kInvalid:
        return "invalid";
    case MaterializedResultKind::kBudgetExceeded:
        return "budget_exceeded";
    case MaterializedResultKind::kSkipped:
    default:
        return "skipped";
    }
}

std::string BuildTimingRowText(std::uint64_t wall_time_ms,
                               MaterializedResultKind kind,
                               const std::filesystem::path& input_relative,
                               const std::string& source_name) {
    std::ostringstream row;
    row << wall_time_ms << "\t" << DescribeMaterializedResultKind(kind) << "\t"
        << input_relative.generic_string() << "\t" << source_name << "\n";
    return row.str();
}

std::optional<MaterializedResultKind> MaterializedResultKindFromManifestStatus(
    std::string_view status) {
    if (status == "copied") {
        return MaterializedResultKind::kCopied;
    }
    if (status == "aliased") {
        return MaterializedResultKind::kAliased;
    }
    if (status == "nonexact") {
        return MaterializedResultKind::kNonexact;
    }
    if (status == "aliased_nonexact") {
        return MaterializedResultKind::kAliasedNonexact;
    }
    if (status == "decompiled") {
        return MaterializedResultKind::kDecompiled;
    }
    if (status == "disassembled") {
        return MaterializedResultKind::kDisassembled;
    }
    if (status == "invalid") {
        return MaterializedResultKind::kInvalid;
    }
    if (status == "budget_exceeded") {
        return MaterializedResultKind::kBudgetExceeded;
    }
    if (status == "skipped") {
        return MaterializedResultKind::kSkipped;
    }
    return std::nullopt;
}

std::uint64_t ExtractSourceWallTimeMs(std::string_view note) {
    constexpr std::string_view kNeedle = "source_wall_time_ms=";
    const std::size_t found = note.find(kNeedle);
    if (found == std::string_view::npos) {
        return 0;
    }

    std::size_t cursor = found + kNeedle.size();
    std::uint64_t value = 0;
    bool saw_digit = false;
    while (cursor < note.size() &&
           std::isdigit(static_cast<unsigned char>(note[cursor]))) {
        saw_digit = true;
        value = value * 10 + static_cast<unsigned char>(note[cursor] - '0');
        ++cursor;
    }
    return saw_digit ? value : 0;
}

bool TryBuildTimingRowFromManifestRowText(std::string_view manifest_row_text,
                                          std::string* timing_row_out,
                                          MaterializedResultKind* kind_out,
                                          std::string* source_name_out,
                                          std::uint64_t* wall_time_ms_out) {
    if (timing_row_out == nullptr) {
        return false;
    }

    ManifestRowRecord record;
    if (!TryParseManifestRowText(manifest_row_text, &record)) {
        return false;
    }

    const std::optional<MaterializedResultKind> kind =
        MaterializedResultKindFromManifestStatus(record.status);
    if (!kind.has_value()) {
        return false;
    }

    const std::uint64_t wall_time_ms = ExtractSourceWallTimeMs(record.note);
    if (kind_out != nullptr) {
        *kind_out = *kind;
    }
    if (source_name_out != nullptr) {
        *source_name_out = record.source_name;
    }
    if (wall_time_ms_out != nullptr) {
        *wall_time_ms_out = wall_time_ms;
    }
    *timing_row_out = BuildTimingRowText(wall_time_ms,
                                         *kind,
                                         std::filesystem::path(record.input_relative),
                                         record.source_name);
    return true;
}

std::string BuildTimedNote(std::string note, std::uint64_t wall_time_ms) {
    return internal::JoinNotes(std::move(note),
                               "source_wall_time_ms=" + std::to_string(wall_time_ms));
}

std::uint64_t ExtractNativeMainChunkTotalTimingMs(std::string_view note) {
    constexpr std::string_view kNeedle = "total:";
    std::uint64_t best = 0;
    std::size_t search_from = 0;
    while (search_from < note.size()) {
        const std::size_t found = note.find(kNeedle, search_from);
        if (found == std::string_view::npos) {
            break;
        }
        std::size_t cursor = found + kNeedle.size();
        std::uint64_t value = 0;
        bool saw_digit = false;
        while (cursor < note.size() &&
               std::isdigit(static_cast<unsigned char>(note[cursor]))) {
            saw_digit = true;
            value = value * 10 + static_cast<unsigned char>(note[cursor] - '0');
            ++cursor;
        }
        if (saw_digit && value > best) {
            best = value;
        }
        search_from = cursor;
    }
    return best;
}

std::string DeterminePreferredRenderModeFromNote(std::string_view note) {
    if (note.find("native_main_chunk_raw_validated") != std::string_view::npos ||
        note.find("native_main_chunk_raw_fast_finalized_large_validated") !=
            std::string_view::npos) {
        return "raw";
    }
    if (note.find("native_main_chunk_validated") != std::string_view::npos ||
        note.find("native_main_chunk_fast_finalized_large_validated") !=
            std::string_view::npos) {
        return "simplified";
    }
    return {};
}

std::string DetermineOutlierClassification(const std::vector<char>& bytes,
                                           std::string_view note) {
    const std::uint64_t prior_total_ms = ExtractNativeMainChunkTotalTimingMs(note);
    if (prior_total_ms >= 60000) {
        return "timed";
    }

    if (!internal::IsLuaBytecode(bytes)) {
        return {};
    }

    refii::lua::Lua51Chunk chunk;
    std::string parse_error;
    if (!refii::lua::ParseLua51Chunk(bytes, &chunk, &parse_error)) {
        return {};
    }
    const refii::lua::Lua51ChunkStats stats = refii::lua::SummarizeLua51Chunk(chunk);
    const refii::lua::Lua51OperationSummary operations =
        refii::lua::SummarizeLua51Operations(chunk.main);
    const refii::lua::Lua51ControlFlowSummary control_flow =
        refii::lua::SummarizeLua51ControlFlow(chunk.main);
    if (stats.instruction_count >= 8000) {
        return "instruction_count";
    }
    if (bytes.size() >= 192 * 1024 && operations.table_set_count >= 1000 &&
        operations.table_set_count > operations.call_count * 2) {
        return "table_heavy";
    }
    if (stats.instruction_count >= 6000 &&
        control_flow.conditional_branch_count >= 500 &&
        control_flow.if_else_block_count >= 80 &&
        control_flow.max_branch_span >= 256 &&
        operations.call_count >= 500) {
        return "control_flow_heavy";
    }
    return {};
}

std::optional<long long> ExtractParityMismatchDeltaSumFromNote(
    std::string_view note,
    std::string_view prefix) {
    const std::size_t found = note.find(prefix);
    if (found == std::string_view::npos) {
        return std::nullopt;
    }

    std::string_view mismatch = note.substr(found + prefix.size());
    const std::size_t note_end = mismatch.find(';');
    if (note_end != std::string_view::npos) {
        mismatch = mismatch.substr(0, note_end);
    }

    const auto parse_signed_field = [&](std::string_view needle) -> std::optional<long long> {
        const std::size_t field = mismatch.find(needle);
        if (field == std::string_view::npos) {
            return std::nullopt;
        }
        std::size_t cursor = field + needle.size();
        int sign = 1;
        if (cursor < mismatch.size() && mismatch[cursor] == '+') {
            ++cursor;
        } else if (cursor < mismatch.size() && mismatch[cursor] == '-') {
            sign = -1;
            ++cursor;
        }
        if (cursor >= mismatch.size() ||
            !std::isdigit(static_cast<unsigned char>(mismatch[cursor]))) {
            return std::nullopt;
        }

        long long value = 0;
        while (cursor < mismatch.size() &&
               std::isdigit(static_cast<unsigned char>(mismatch[cursor]))) {
            value = value * 10 +
                    static_cast<long long>(static_cast<unsigned char>(mismatch[cursor] - '0'));
            ++cursor;
        }
        return static_cast<long long>(sign) * value;
    };

    const std::optional<long long> inst = parse_signed_field("inst=");
    const std::optional<long long> constant = parse_signed_field("const=");
    const std::optional<long long> locals = parse_signed_field("locals=");
    if (!inst.has_value() || !constant.has_value() || !locals.has_value()) {
        return std::nullopt;
    }

    return std::llabs(*inst) + std::llabs(*constant) + std::llabs(*locals);
}

std::optional<std::uint64_t> ExtractUnsignedNoteField(std::string_view note,
                                                      std::string_view field_name) {
    const std::size_t found = note.find(field_name);
    if (found == std::string_view::npos) {
        return std::nullopt;
    }

    std::size_t cursor = found + field_name.size();
    if (cursor >= note.size() ||
        !std::isdigit(static_cast<unsigned char>(note[cursor]))) {
        return std::nullopt;
    }

    std::uint64_t value = 0;
    while (cursor < note.size() &&
           std::isdigit(static_cast<unsigned char>(note[cursor]))) {
        value = value * 10 +
                static_cast<std::uint64_t>(static_cast<unsigned char>(note[cursor] - '0'));
        ++cursor;
    }
    return value;
}

bool ShouldSkipNativeExactRescueForDirectNonexact(std::string_view note,
                                                  bool outlier_candidate,
                                                  std::size_t byte_size) {
    static constexpr std::string_view kDirectParityPrefix =
        "workspace_direct_main_chunk_parity_mismatch=";
    const std::optional<long long> delta_sum =
        ExtractParityMismatchDeltaSumFromNote(note, kDirectParityPrefix);
    if (!delta_sum.has_value()) {
        return false;
    }
    const std::optional<std::uint64_t> instruction_count =
        ExtractUnsignedNoteField(note, "chunk_instructions=");

    if (*delta_sum <= 1) {
        if (outlier_candidate &&
            byte_size >= 96 * 1024 &&
            note.find("function_stats_differ") != std::string_view::npos) {
            return true;
        }
        return false;
    }

    const bool no_progress_signal =
        note.find("validated_exact_repair_no_progress") != std::string_view::npos ||
        note.find("candidate_raw_no_progress") != std::string_view::npos ||
        note.find("bytecode_function_refinement_no_progress") != std::string_view::npos ||
        note.find("syntax_repair_no_progress") != std::string_view::npos ||
        note.find("top_level_replacement_no_progress_cutoff@") != std::string_view::npos;
    const bool high_cost_signal =
        note.find("raw_donor_quality_bypassed_raw") != std::string_view::npos ||
        note.find("repaired_from_raw_expansion") != std::string_view::npos;
    if ((no_progress_signal || high_cost_signal) &&
        byte_size >= 96 * 1024 &&
        *delta_sum >= 3) {
        return true;
    }

    if (outlier_candidate && *delta_sum >= 3) {
        return true;
    }

    if (byte_size >= 96 * 1024 && *delta_sum >= 3) {
        return true;
    }

    if (instruction_count.has_value() && *instruction_count >= 1000 && *delta_sum >= 3) {
        return true;
    }

    if (byte_size >= 64 * 1024 &&
        note.find("workspace_direct_main_chunk_validated_parity_improved_cleanup") !=
            std::string_view::npos &&
        *delta_sum >= 3) {
        return true;
    }

    return false;
}

bool HasWorkspaceNoProgressOrHighCostSignal(std::string_view note) {
    return note.find("active_file_no_progress_timeout") != std::string_view::npos ||
           note.find("validated_exact_repair_no_progress") != std::string_view::npos ||
           note.find("candidate_raw_no_progress") != std::string_view::npos ||
           note.find("bytecode_function_refinement_no_progress") != std::string_view::npos ||
           note.find("syntax_repair_no_progress") != std::string_view::npos ||
           note.find("top_level_replacement_no_progress_cutoff@") != std::string_view::npos ||
           note.find("raw_donor_quality_bypassed_raw") != std::string_view::npos ||
           note.find("repaired_from_raw_expansion") != std::string_view::npos;
}

bool ShouldReprobeCachedNonexactNearExact(std::string_view note) {
    if (note.empty() ||
        note.find("function_stats_differ") == std::string_view::npos) {
        return false;
    }
    if (note.find("validated_exact_near_exact_preferred_nonexact") !=
        std::string_view::npos) {
        return true;
    }

    static constexpr std::string_view kDirectParityPrefix =
        "workspace_direct_main_chunk_parity_mismatch=";
    static constexpr std::string_view kNativeParityPrefix =
        "native_main_chunk_parity_mismatch=";
    static constexpr std::string_view kExactRescueParityPrefix =
        "native_first_exact_rescue_parity_mismatch=";

    const std::optional<long long> direct_delta =
        ExtractParityMismatchDeltaSumFromNote(note, kDirectParityPrefix);
    if (direct_delta.has_value() && *direct_delta <= 3) {
        return true;
    }

    const std::optional<long long> native_delta =
        ExtractParityMismatchDeltaSumFromNote(note, kNativeParityPrefix);
    if (native_delta.has_value() && *native_delta <= 2) {
        return true;
    }

    const std::optional<long long> exact_rescue_delta =
        ExtractParityMismatchDeltaSumFromNote(note, kExactRescueParityPrefix);
    if (exact_rescue_delta.has_value() && *exact_rescue_delta <= 2) {
        return true;
    }

    return false;
}

bool ShouldClampRuntimeTimeoutForNoProgressHotspot(
    bool is_runtime_hotspot,
    std::string_view previous_manifest_status,
    std::string_view previous_note,
    std::size_t byte_size) {
    if (!is_runtime_hotspot) {
        return false;
    }
    if (previous_manifest_status.empty()) {
        return false;
    }
    if (previous_manifest_status != "nonexact" &&
        previous_manifest_status != "budget_exceeded" &&
        previous_manifest_status != "invalid" &&
        previous_manifest_status != "disassembled") {
        return false;
    }
    if (!HasWorkspaceNoProgressOrHighCostSignal(previous_note)) {
        return false;
    }
    if (byte_size >= 96 * 1024) {
        return true;
    }
    const std::optional<std::uint64_t> instruction_count =
        ExtractUnsignedNoteField(previous_note, "chunk_instructions=");
    return instruction_count.has_value() && *instruction_count >= 2000;
}

bool ShouldProactivelyClampRuntimeTimeoutForHotspot(
    bool is_runtime_hotspot,
    std::size_t byte_size) {
    // Runtime recovery: apply a conservative first-pass clamp for known long-pole files
    // even before we have prior no-progress notes. This prevents minute-scale stalls
    // on fresh runs while keeping the existing fallback pipeline unchanged.
    return is_runtime_hotspot && byte_size >= 128 * 1024;
}

bool ShouldClampRuntimeTimeoutForBehaviorNoProgress(
    bool is_behavior_path,
    std::string_view previous_manifest_status,
    std::string_view previous_note,
    std::size_t byte_size) {
    if (!is_behavior_path || previous_manifest_status.empty()) {
        return false;
    }
    if (previous_manifest_status != "nonexact" &&
        previous_manifest_status != "budget_exceeded" &&
        previous_manifest_status != "invalid" &&
        previous_manifest_status != "disassembled") {
        return false;
    }
    if (!HasWorkspaceNoProgressOrHighCostSignal(previous_note)) {
        return false;
    }

    const std::optional<std::uint64_t> previous_wall_time_ms =
        ExtractUnsignedNoteField(previous_note, "source_wall_time_ms=");
    if (previous_wall_time_ms.has_value() && *previous_wall_time_ms >= 15000) {
        return true;
    }
    const std::optional<std::uint64_t> previous_instruction_count =
        ExtractUnsignedNoteField(previous_note, "chunk_instructions=");
    if (previous_instruction_count.has_value() && *previous_instruction_count >= 1200) {
        return true;
    }
    return byte_size >= 96 * 1024;
}

bool ShouldRunPhase2HoldoutRepairForResult(const SourceProcessResult& phase1_result) {
    if (!phase1_result.exact_parity_required || phase1_result.exact_parity_satisfied) {
        return false;
    }
    if (!IsUsableLuaProcessKind(phase1_result.kind)) {
        return true;
    }

    static constexpr std::string_view kDirectParityPrefix =
        "workspace_direct_main_chunk_parity_mismatch=";
    static constexpr std::string_view kNativeParityPrefix =
        "native_main_chunk_parity_mismatch=";
    const std::optional<long long> direct_delta =
        ExtractParityMismatchDeltaSumFromNote(phase1_result.note, kDirectParityPrefix);
    const std::optional<long long> native_delta =
        ExtractParityMismatchDeltaSumFromNote(phase1_result.note, kNativeParityPrefix);

    const long long unknown_delta = std::numeric_limits<long long>::max();
    const long long best_delta =
        direct_delta.has_value() ? *direct_delta
                                 : (native_delta.has_value() ? *native_delta : unknown_delta);
    const bool high_cost_or_no_progress =
        HasWorkspaceNoProgressOrHighCostSignal(phase1_result.note);
    const bool already_ran_exact_repair =
        phase1_result.note.find("validated_exact_repair_state@") != std::string::npos ||
        phase1_result.note.find("validated_exact_near_exact") != std::string::npos ||
        phase1_result.note.find("validated_exact_last_chance") != std::string::npos;
    if (best_delta <= 3) {
        // Phase 1 already runs the cheap tiny/near-exact exactifiers. Re-entering
        // phase 2 for the same usable tiny mismatch can pin behaviour files in a
        // no-progress holdout loop, so only escalate if phase 1 did not already
        // try an exact-repair path.
        return !already_ran_exact_repair && !high_cost_or_no_progress &&
               phase1_result.wall_time_ms < 1500;
    }

    const bool wide_function_stats =
        phase1_result.note.find("function_stats_differ") != std::string_view::npos &&
        best_delta != unknown_delta && best_delta >= 8;
    if (high_cost_or_no_progress || wide_function_stats ||
        phase1_result.wall_time_ms >= 15000) {
        return false;
    }

    // Keep phase 2 for terminal/recovery cases and tiny drift only. Wide but usable
    // nonexact outputs are better handled by cheap targeted exactifiers; exhaustive
    // holdout repair can otherwise pin a worker on the same no-progress state.
    return best_delta == unknown_delta;
}

bool IsScopedDirectNonexactNativeExactRescueOverridePath(
    std::string_view relative_path) {
    return BuildLuaScriptPolicy(relative_path)
        .direct_nonexact_native_exact_rescue_override;
}

bool IsScopedGamefaceDisassemblyRecoveryPath(std::string_view relative_path) {
    return IsLuaScriptProfilePath(relative_path, LuaScriptProfileFlag::kGameface);
}

bool IsScopedBlockerDisassemblyRecoveryPath(std::string_view relative_path) {
    return IsLuaScriptProfilePath(relative_path,
                                  LuaScriptProfileFlag::kDisassemblyRecoveryPriority);
}

bool IsScopedDisassemblyRecoveryReprobePath(std::string_view relative_path) {
    return IsScopedGamefaceDisassemblyRecoveryPath(relative_path) ||
           IsScopedBlockerDisassemblyRecoveryPath(relative_path);
}

bool MaterializePreparedResultToDisk(const PreparedInput& prepared,
                                     const SourceProcessResult& result,
                                     const std::filesystem::path& manifest_row_path,
                                     std::string* manifest_row_out,
                                     MaterializedResultKind* kind_out) {
    if (kind_out == nullptr) {
        return false;
    }

    std::filesystem::create_directories(prepared.output_path.parent_path());
    std::error_code remove_error;
    std::string manifest_row;
    const bool was_aliased = prepared.cache_key != prepared.self_cache_key;

    if (result.kind == SourceProcessKind::kCopied) {
        std::filesystem::copy_file(result.artifact_path,
                                   prepared.output_path,
                                   std::filesystem::copy_options::overwrite_existing);
        std::filesystem::remove(prepared.failed_output_path, remove_error);
        std::filesystem::remove(prepared.disassembly_output_path, remove_error);
        *kind_out = MaterializedResultKind::kCopied;
        const std::string note = BuildTimedNote(result.note, result.wall_time_ms);
        manifest_row = BuildManifestRowText("copied",
                                            prepared.relative,
                                            prepared.output_relative,
                                            result.source_name,
                                            note);
    } else if ((result.kind == SourceProcessKind::kDecompiled ||
                result.kind == SourceProcessKind::kNonexact) &&
               internal::HasUsableDecompiledLuaOutput(result.artifact_path, result.note)) {
        std::string note = result.note;
        if (!internal::CopyNormalizedLuaTextFile(result.artifact_path, prepared.output_path, &note)) {
            std::filesystem::remove(prepared.output_path, remove_error);
            std::filesystem::remove(prepared.disassembly_output_path, remove_error);
            *kind_out = MaterializedResultKind::kSkipped;
            note = BuildTimedNote(std::move(note), result.wall_time_ms);
            manifest_row = BuildManifestRowText("skipped",
                                                prepared.relative,
                                                {},
                                                result.source_name,
                                                internal::JoinNotes(note,
                                                                    "copy_decompiled_output_failed"));
        } else {
            std::filesystem::remove(prepared.failed_output_path, remove_error);
            std::filesystem::remove(prepared.disassembly_output_path, remove_error);
            const bool nonexact = result.kind == SourceProcessKind::kNonexact;
            *kind_out = nonexact
                            ? (was_aliased ? MaterializedResultKind::kAliasedNonexact
                                           : MaterializedResultKind::kNonexact)
                            : (was_aliased ? MaterializedResultKind::kAliased
                                           : MaterializedResultKind::kDecompiled);
            note = BuildTimedNote(std::move(note), result.wall_time_ms);
            manifest_row = BuildManifestRowText(
                nonexact ? (was_aliased ? "aliased_nonexact" : "nonexact")
                         : (was_aliased ? "aliased" : "decompiled"),
                prepared.relative,
                prepared.output_relative,
                result.source_name,
                note);
        }
    } else if (result.kind == SourceProcessKind::kInvalid) {
        std::filesystem::remove(prepared.output_path, remove_error);
        std::filesystem::remove(prepared.disassembly_output_path, remove_error);
        *kind_out = MaterializedResultKind::kInvalid;
        const std::string note = BuildTimedNote(
            result.note.empty() ? std::string("native_candidate_invalid") : result.note,
            result.wall_time_ms);
        manifest_row = BuildManifestRowText("invalid",
                                            prepared.relative,
                                            {},
                                            result.source_name,
                                            note);
    } else if (result.kind == SourceProcessKind::kBudgetExceeded) {
        std::filesystem::remove(prepared.output_path, remove_error);
        std::filesystem::remove(prepared.disassembly_output_path, remove_error);
        *kind_out = MaterializedResultKind::kBudgetExceeded;
        const std::string note = BuildTimedNote(
            result.note.empty() ? std::string("source_wall_time_budget_exceeded") : result.note,
            result.wall_time_ms);
        manifest_row = BuildManifestRowText("budget_exceeded",
                                            prepared.relative,
                                            {},
                                            result.source_name,
                                            note);
    } else if (result.kind == SourceProcessKind::kDisassembled &&
               std::filesystem::exists(result.artifact_path) &&
               std::filesystem::file_size(result.artifact_path) > 0) {
        const std::filesystem::path disassembly_output_relative =
            std::filesystem::path("_disasm") / prepared.output_relative;
        std::filesystem::create_directories(prepared.disassembly_output_path.parent_path());
        std::filesystem::copy_file(result.artifact_path,
                                   prepared.disassembly_output_path,
                                   std::filesystem::copy_options::overwrite_existing);
        std::string note = result.note;
        note = internal::JoinNotes(note,
                                   "disassembly_path=" +
                                       disassembly_output_relative.generic_string());
        if (internal::WriteTextFile(
                prepared.output_path,
                internal::BuildDisassemblyPlaceholder(prepared.output_relative,
                                                     disassembly_output_relative,
                                                     result.source_name))) {
            if (ShouldPreserveFailedOutputs() &&
                std::filesystem::exists(prepared.failed_output_path)) {
                note = internal::JoinNotes(
                    note,
                    "failed_output_preserved=" +
                        std::filesystem::path("_failed")
                            .append(prepared.output_relative.generic_string())
                            .generic_string());
            } else {
                std::filesystem::remove(prepared.failed_output_path, remove_error);
            }
            note = internal::JoinNotes(note, "placeholder_written");
        } else {
            std::filesystem::remove(prepared.output_path, remove_error);
            note = internal::JoinNotes(note, "placeholder_write_failed");
        }
        *kind_out = MaterializedResultKind::kDisassembled;
        note = BuildTimedNote(std::move(note), result.wall_time_ms);
        manifest_row = BuildManifestRowText("disassembled",
                                            prepared.relative,
                                            prepared.output_relative,
                                            result.source_name,
                                            note);
    } else {
        std::filesystem::remove(prepared.output_path, remove_error);
        std::filesystem::remove(prepared.disassembly_output_path, remove_error);
        *kind_out = MaterializedResultKind::kSkipped;
        const std::string note = BuildTimedNote(
            result.note.empty() ? std::string("decompiler_failed") : result.note,
            result.wall_time_ms);
        manifest_row = BuildManifestRowText("skipped",
                                                prepared.relative,
                                            {},
                                            result.source_name,
                                            note);
    }

    std::filesystem::create_directories(manifest_row_path.parent_path());
    if (manifest_row_out != nullptr) {
        *manifest_row_out = manifest_row;
    }
    return internal::WriteTextFile(manifest_row_path, manifest_row);
}

PreparedInput PrepareInput(const LuaWorkspaceDecompileOptions& options,
                           const std::filesystem::path& relative) {
    PreparedInput prepared;
    prepared.relative = relative;
    prepared.resolved = internal::ResolveBestInputPath(options.workspace_root, relative);
    prepared.input_path = prepared.resolved.path;
    prepared.output_relative = MapOutputRelativePath(relative);
    prepared.output_path = options.output_root / prepared.output_relative;
    prepared.failed_output_path = options.output_root / "_failed" / prepared.output_relative;
    prepared.disassembly_output_path = options.disassembly_output_root / prepared.output_relative;
    prepared.cache_key = MakeCacheKey(prepared.input_path);
    prepared.self_cache_key = MakeCacheKey(options.workspace_root / relative);
    return prepared;
}

SourceProcessResult ProcessPreparedInput(const LuaWorkspaceDecompileOptions& options,
                                         const PreparedInput& prepared,
                                         const std::string& persistent_cache_version,
                                         const std::filesystem::path& cache_root,
                                         const std::filesystem::path& failed_output_root,
                                         const ManifestStatusMap* previous_manifest_statuses,
                                         const ManifestInputSet* unfinished_active_inputs,
                                         SourceProcessPhase process_phase) {
    const auto started = std::chrono::steady_clock::now();
    const auto finish_result = [&](SourceProcessResult* result) {
        if (result == nullptr) {
            return;
        }
        result->wall_time_ms = static_cast<std::uint64_t>(
            std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - started)
                .count());
    };
    SourceProcessResult result;

    std::vector<char> bytes;
    const auto release_transient_state = MakeScopeExit([&]() {
        internal::ResetTransientDecompileCaches();
        bytes.clear();
        bytes.shrink_to_fit();
    });

    if (!internal::ReadFile(prepared.input_path, bytes)) {
        result.kind = SourceProcessKind::kSkipped;
        result.note = "read_failed";
        finish_result(&result);
        return result;
    }

    result.source_name = internal::DecodeSourceName(bytes);
    const std::string chunk_note = internal::DescribeBytecodeChunk(bytes);
    if (!internal::IsLuaBytecode(bytes)) {
        result.kind = SourceProcessKind::kCopied;
        result.artifact_path = prepared.input_path;
        result.note = prepared.resolved.note;
        finish_result(&result);
        return result;
    }

    CacheEntry entry;
    entry.attempted = true;
    entry.note = internal::JoinNotes(prepared.resolved.note, chunk_note);
    const std::string bytecode_hash = ComputeStableBytecodeHash(bytes);

    std::error_code relative_error;
    const std::filesystem::path source_relative =
        std::filesystem::relative(prepared.input_path, options.workspace_root, relative_error);
    const std::filesystem::path cache_output =
        cache_root /
        (relative_error ? prepared.output_relative : MapOutputRelativePath(source_relative));
    const std::filesystem::path disassembly_cache_output =
        cache_root / "_disasm" /
        (relative_error ? prepared.output_relative : MapOutputRelativePath(source_relative));
    const std::filesystem::path failed_output_path =
        failed_output_root /
        (relative_error ? prepared.output_relative : MapOutputRelativePath(source_relative));
    const std::filesystem::path cache_metadata_path =
        MakePersistentCacheMetadataPath(
            cache_root,
            relative_error ? prepared.output_relative : MapOutputRelativePath(source_relative));

    const bool exact_parity_required_by_policy =
        internal::ShouldRequireExactParityForInput(prepared.relative,
                                                   &prepared.input_path,
                                                   bytes,
                                                   &options);
    entry.exact_parity_required = exact_parity_required_by_policy;

    std::filesystem::create_directories(cache_output.parent_path());

    PersistentCacheEntry persistent_entry;
    const bool persistent_entry_valid =
        ReadPersistentCacheEntry(cache_metadata_path, &persistent_entry) &&
        persistent_entry.version == persistent_cache_version &&
        persistent_entry.bytecode_hash == bytecode_hash;
    const std::string prior_outlier_classification =
        persistent_entry_valid && !persistent_entry.outlier_classification.empty()
            ? persistent_entry.outlier_classification
            : DetermineOutlierClassification(
                  bytes, persistent_entry_valid ? std::string_view(persistent_entry.note)
                                                : std::string_view());
    const std::string_view previous_note_view =
        persistent_entry_valid ? std::string_view(persistent_entry.note) : std::string_view();
    std::string previous_manifest_status;
    bool unfinished_active = false;
    const std::string normalized_input_relative =
        internal::NormalizePathString(prepared.relative.generic_string());
    if (previous_manifest_statuses != nullptr) {
        const auto it = previous_manifest_statuses->find(normalized_input_relative);
        if (it != previous_manifest_statuses->end()) {
            previous_manifest_status = it->second;
        }
        if (unfinished_active_inputs != nullptr &&
            unfinished_active_inputs->find(normalized_input_relative) !=
                unfinished_active_inputs->end()) {
            unfinished_active = true;
        }
    }
    const bool previous_failed_file = previous_manifest_status == "disassembled" ||
                                      previous_manifest_status == "invalid" ||
                                      previous_manifest_status == "budget_exceeded";
    const bool outlier_candidate = !prior_outlier_classification.empty();
    const bool control_flow_heavy_outlier =
        prior_outlier_classification == "control_flow_heavy";
    const bool defer_outlier_to_single_tool =
        outlier_candidate && !control_flow_heavy_outlier;
    const bool prefer_exhaustive_rescue_default =
        unfinished_active || previous_failed_file || !outlier_candidate ||
        control_flow_heavy_outlier;
    const bool prefer_exhaustive_rescue =
        process_phase == SourceProcessPhase::kPhase1LightProbe
            ? false
            : (process_phase == SourceProcessPhase::kPhase2HoldoutRepair
                   ? true
                   : prefer_exhaustive_rescue_default);
    LuaWorkspaceDecompileOptions effective_options = options;
    const std::string normalized_relative_for_hotspot =
        internal::NormalizePathString(prepared.relative.generic_string());
    const LuaScriptPolicy runtime_policy = BuildLuaScriptPolicy(normalized_relative_for_hotspot);
    const bool is_behavior_path = runtime_policy.runtime_behavior_path;
    if (effective_options.source_wall_time_budget_ms != 0) {
        if (prior_outlier_classification == "timed") {
            effective_options.source_wall_time_budget_ms =
                std::min<std::uint64_t>(effective_options.source_wall_time_budget_ms, 120000ull);
            entry.note = internal::JoinNotes(
                entry.note, "workspace_outlier_timed_budget_clamped=120000ms");
        } else if (control_flow_heavy_outlier) {
            effective_options.source_wall_time_budget_ms =
                std::min<std::uint64_t>(effective_options.source_wall_time_budget_ms, 150000ull);
            entry.note = internal::JoinNotes(
                entry.note, "workspace_outlier_control_flow_budget_clamped=150000ms");
        }
        if (is_behavior_path && bytes.size() >= 96 * 1024) {
            effective_options.source_wall_time_budget_ms =
                std::min<std::uint64_t>(effective_options.source_wall_time_budget_ms, 90000ull);
            entry.note = internal::JoinNotes(
                entry.note, "workspace_behavior_budget_clamped=90000ms");
        }
    }
    constexpr std::size_t kQuestHotspotThresholdBytes = 100000;
    constexpr std::size_t kQuestRuntimeBoundedAttemptThresholdBytes = 48 * 1024;
    constexpr unsigned int kQuestHotspotNativeAttemptTimeoutSeconds = 20;
    constexpr unsigned int kCombatsequencesNativeAttemptTimeoutSeconds = 15;
    constexpr unsigned int kBlockerNativeAttemptTimeoutSeconds = 25;
    constexpr unsigned int kRuntimeHotspotNativeAttemptTimeoutSeconds = 15;
    const bool is_known_quest_hotspot =
        runtime_policy.runtime_fallback_quest_hotspot;
    const bool is_large_known_quest_hotspot =
        bytes.size() >= kQuestHotspotThresholdBytes && is_known_quest_hotspot;
    const bool is_combatsequences_hotspot =
        runtime_policy.runtime_bounded_attempt_ai_combat_hotspot;
    const bool is_blocker_bounded_attempt_target =
        runtime_policy.runtime_disassembly_recovery_target;
    const bool is_quest_path =
        runtime_policy.profile.Has(LuaScriptProfileFlag::kQuest);
    const bool is_runtime_bounded_attempt_hotspot =
        (runtime_policy.runtime_hotspot && bytes.size() >= 96 * 1024) ||
        (is_quest_path && bytes.size() >= kQuestRuntimeBoundedAttemptThresholdBytes);
    const bool bounded_native_attempt_for_large_quest_hotspot =
        is_large_known_quest_hotspot;
    const bool bounded_native_attempt_for_combatsequences =
        is_combatsequences_hotspot;
    const bool bounded_native_attempt_for_blocker =
        is_blocker_bounded_attempt_target;
    const bool bounded_native_attempt_for_runtime_hotspot =
        is_runtime_bounded_attempt_hotspot;
    const bool bounded_native_attempt_for_hotspot =
        bounded_native_attempt_for_large_quest_hotspot ||
        bounded_native_attempt_for_combatsequences ||
        bounded_native_attempt_for_blocker ||
        bounded_native_attempt_for_runtime_hotspot;
    const bool allow_fast_nonexact_for_bounded_hotspot =
        bounded_native_attempt_for_large_quest_hotspot ||
        bounded_native_attempt_for_runtime_hotspot ||
        bounded_native_attempt_for_combatsequences ||
        (bounded_native_attempt_for_blocker && bytes.size() >= 96 * 1024);
    const unsigned int bounded_native_attempt_timeout_seconds =
        bounded_native_attempt_for_large_quest_hotspot
            ? kQuestHotspotNativeAttemptTimeoutSeconds
            : (bounded_native_attempt_for_combatsequences
                   ? kCombatsequencesNativeAttemptTimeoutSeconds
                   : (bounded_native_attempt_for_runtime_hotspot
                          ? kRuntimeHotspotNativeAttemptTimeoutSeconds
                          : (bounded_native_attempt_for_blocker
                                 ? kBlockerNativeAttemptTimeoutSeconds
                                 : 0u)));
    const bool clamp_runtime_timeout_for_no_progress_hotspot =
        ShouldClampRuntimeTimeoutForNoProgressHotspot(
            runtime_policy.runtime_hotspot,
            previous_manifest_status,
            previous_note_view,
            bytes.size());
    const bool clamp_runtime_timeout_for_proactive_hotspot =
        ShouldProactivelyClampRuntimeTimeoutForHotspot(
            runtime_policy.runtime_hotspot,
            bytes.size());
    const bool clamp_runtime_timeout_for_bounded_attempt_hotspot =
        bounded_native_attempt_for_hotspot;
    const bool clamp_runtime_timeout_for_runtime_hotspot =
        is_runtime_bounded_attempt_hotspot;
    const bool clamp_runtime_timeout_for_behavior_no_progress =
        ShouldClampRuntimeTimeoutForBehaviorNoProgress(
            is_behavior_path, previous_manifest_status, previous_note_view, bytes.size());
    const bool clamp_runtime_timeout_for_large_behavior =
        is_behavior_path && bytes.size() >= 96 * 1024;
    const bool phase1_light_probe =
        process_phase == SourceProcessPhase::kPhase1LightProbe;
    const bool clamp_runtime_timeout_for_hotspot =
        clamp_runtime_timeout_for_no_progress_hotspot ||
        clamp_runtime_timeout_for_proactive_hotspot ||
        clamp_runtime_timeout_for_bounded_attempt_hotspot ||
        clamp_runtime_timeout_for_runtime_hotspot ||
        clamp_runtime_timeout_for_behavior_no_progress ||
        clamp_runtime_timeout_for_large_behavior;
    if (clamp_runtime_timeout_for_hotspot) {
        const std::uint64_t clamped_source_wall_budget_ms =
            clamp_runtime_timeout_for_large_behavior
                ? (phase1_light_probe ? 45000ull : 90000ull)
                : (clamp_runtime_timeout_for_behavior_no_progress
                       ? (phase1_light_probe ? 30000ull : 60000ull)
                       : (clamp_runtime_timeout_for_bounded_attempt_hotspot
                              ? (phase1_light_probe ? 30000ull : 60000ull)
                       : (clamp_runtime_timeout_for_runtime_hotspot
                              ? (phase1_light_probe ? 30000ull : 60000ull)
                              : (phase1_light_probe ? 60000ull : 120000ull))));
        const bool auto_applied_source_wall_budget =
            effective_options.source_wall_time_budget_ms == 0;
        effective_options.source_wall_time_budget_ms =
            auto_applied_source_wall_budget
                ? clamped_source_wall_budget_ms
                : std::min<std::uint64_t>(effective_options.source_wall_time_budget_ms,
                                          clamped_source_wall_budget_ms);
        entry.note = internal::JoinNotes(
            entry.note,
            clamp_runtime_timeout_for_no_progress_hotspot
                ? ("workspace_source_wall_time_budget_clamped_no_progress_hotspot=" +
                   std::to_string(clamped_source_wall_budget_ms) + "ms")
                : (clamp_runtime_timeout_for_proactive_hotspot
                       ? ("workspace_source_wall_time_budget_clamped_proactive_hotspot=" +
                          std::to_string(clamped_source_wall_budget_ms) + "ms")
                       : (clamp_runtime_timeout_for_bounded_attempt_hotspot
                              ? ("workspace_source_wall_time_budget_clamped_bounded_hotspot=" +
                                 std::to_string(clamped_source_wall_budget_ms) + "ms")
                       : (clamp_runtime_timeout_for_runtime_hotspot
                              ? ("workspace_source_wall_time_budget_clamped_runtime_hotspot=" +
                                 std::to_string(clamped_source_wall_budget_ms) + "ms")
                              : (clamp_runtime_timeout_for_behavior_no_progress
                                     ? ("workspace_source_wall_time_budget_clamped_behavior_no_progress=" +
                                        std::to_string(clamped_source_wall_budget_ms) + "ms")
                                      : ("workspace_source_wall_time_budget_clamped_large_behavior=" +
                                         std::to_string(clamped_source_wall_budget_ms) + "ms"))))));
        if (auto_applied_source_wall_budget) {
            entry.note = internal::JoinNotes(
                entry.note,
                "workspace_source_wall_time_budget_auto_applied");
        }
    }
    const bool prefer_external_tool =
        ShouldUseGuardedExternalDecompiler(previous_failed_file,
                                          unfinished_active,
                                          control_flow_heavy_outlier) ||
        defer_outlier_to_single_tool;
    const bool defer_inprocess_native_candidate =
        ((!control_flow_heavy_outlier) && (previous_failed_file || unfinished_active)) ||
        defer_outlier_to_single_tool;
    unsigned int guarded_tool_timeout_seconds =
        prefer_external_tool
            ? GetGuardedExternalDecompilerTimeoutSeconds(bytes,
                                                        effective_options.child_timeout_seconds)
            : effective_options.child_timeout_seconds;
    const unsigned int clamped_runtime_timeout_seconds =
        clamp_runtime_timeout_for_large_behavior
            ? (phase1_light_probe ? 12u : 20u)
            : (clamp_runtime_timeout_for_behavior_no_progress
                   ? (phase1_light_probe ? 10u : 15u)
                   : (clamp_runtime_timeout_for_bounded_attempt_hotspot
                          ? (phase1_light_probe ? 12u : 20u)
                   : (clamp_runtime_timeout_for_runtime_hotspot
                          ? (phase1_light_probe ? 12u : 20u)
                          : (phase1_light_probe ? 15u : 25u))));
    if (clamp_runtime_timeout_for_hotspot && guarded_tool_timeout_seconds > 25u) {
        guarded_tool_timeout_seconds = clamped_runtime_timeout_seconds;
        entry.note = internal::JoinNotes(
            entry.note,
            clamp_runtime_timeout_for_no_progress_hotspot
                ? ("workspace_runtime_timeout_clamped_no_progress_hotspot=" +
                   std::to_string(clamped_runtime_timeout_seconds))
                : (clamp_runtime_timeout_for_proactive_hotspot
                       ? ("workspace_runtime_timeout_clamped_proactive_hotspot=" +
                          std::to_string(clamped_runtime_timeout_seconds))
                       : (clamp_runtime_timeout_for_bounded_attempt_hotspot
                              ? ("workspace_runtime_timeout_clamped_bounded_hotspot=" +
                                 std::to_string(clamped_runtime_timeout_seconds))
                       : (clamp_runtime_timeout_for_behavior_no_progress
                              ? ("workspace_runtime_timeout_clamped_behavior_no_progress=" +
                                 std::to_string(clamped_runtime_timeout_seconds))
                              : (clamp_runtime_timeout_for_runtime_hotspot
                                     ? ("workspace_runtime_timeout_clamped_runtime_hotspot=" +
                                        std::to_string(clamped_runtime_timeout_seconds))
                                     : ("workspace_runtime_timeout_clamped_large_behavior=" +
                                        std::to_string(clamped_runtime_timeout_seconds)))))));
    }
    const auto try_prefer_single_tool_nonexact_output = [&](std::string_view note_prefix) {
        if (effective_options.single_tool_path.empty() ||
            !std::filesystem::exists(effective_options.single_tool_path)) {
            return false;
        }
        const std::string normalized_relative =
            internal::NormalizePathString(prepared.relative.generic_string());
        if (!internal::StartsWith(normalized_relative, "gamescripts/scripts/") &&
            !internal::StartsWith(normalized_relative, "scripts/")) {
            return false;
        }
        std::error_code file_error;
        if (!std::filesystem::exists(cache_output, file_error) || file_error ||
            std::filesystem::file_size(cache_output, file_error) == 0) {
            return false;
        }

        LuaWorkspaceDecompileOptions budgeted_options =
            BuildBudgetedDecompileOptions(effective_options, started);
        const unsigned int single_tool_timeout_seconds =
            ClampTimeoutSecondsToSourceWallTimeBudget(effective_options.child_timeout_seconds,
                                                      effective_options,
                                                      started);
        if (budgeted_options.source_wall_time_budget_ms != 0 &&
            single_tool_timeout_seconds == 0) {
            return false;
        }

        const std::filesystem::path single_tool_output = cache_output.string() + ".single_tool";
        std::filesystem::remove(single_tool_output, file_error);
        const auto cleanup_single_tool_output = MakeScopeExit([&]() {
            std::filesystem::remove(single_tool_output, file_error);
        });

        const int exit_code = internal::RunDecompiler(budgeted_options,
                                                      prepared.input_path,
                                                      single_tool_output,
                                                      {},
                                                      true,
                                                      single_tool_timeout_seconds);
        if (!std::filesystem::exists(single_tool_output, file_error) || file_error ||
            std::filesystem::file_size(single_tool_output, file_error) == 0) {
            return false;
        }

        const internal::NativeCandidateFinalizeResult single_tool_result =
            internal::ClassifyExistingNativeCandidateOutput(prepared.relative,
                                                           single_tool_output,
                                                           bytes,
                                                           &prepared.input_path,
                                                           &effective_options,
                                                           &entry.note,
                                                           "single_tool_output");
        entry.exact_parity_required =
            entry.exact_parity_required || single_tool_result.exact_parity_required;
        if (single_tool_result.kind == internal::NativeCandidateFinalizeKind::kExact) {
            entry.exact_parity_satisfied = true;
        }
        if (single_tool_result.kind == internal::NativeCandidateFinalizeKind::kInvalid) {
            return false;
        }

        std::string cached_source;
        std::string single_tool_source;
        if (!internal::ReadTextFile(cache_output, cached_source) ||
            !internal::ReadTextFile(single_tool_output, single_tool_source) ||
            cached_source == single_tool_source) {
            return false;
        }

        const auto count_substring = [](std::string_view text, std::string_view needle) {
            std::size_t count = 0;
            std::size_t offset = 0;
            while (offset < text.size()) {
                const std::size_t found = text.find(needle, offset);
                if (found == std::string_view::npos) {
                    break;
                }
                ++count;
                offset = found + needle.size();
            }
            return count;
        };

        bool prefer_single_tool =
            single_tool_result.kind == internal::NativeCandidateFinalizeKind::kExact;
        if (!prefer_single_tool) {
            const internal::LuaOutputQualitySummary cached_quality =
                internal::SummarizeLuaOutputQuality(cached_source);
            const internal::LuaOutputQualitySummary single_tool_quality =
                internal::SummarizeLuaOutputQuality(single_tool_source);
            prefer_single_tool =
                internal::IsLuaOutputQualityBetter(cached_quality, single_tool_quality);
        }
        if (!prefer_single_tool &&
            count_substring(single_tool_source, ":CreateSubClass(") >
                count_substring(cached_source, ":CreateSubClass(")) {
            prefer_single_tool = true;
        }
        if (!prefer_single_tool &&
            count_substring(single_tool_source, "L0_0.") <
                count_substring(cached_source, "L0_0.")) {
            prefer_single_tool = true;
        }
        if (!prefer_single_tool) {
            return false;
        }

        std::filesystem::copy_file(single_tool_output,
                                   cache_output,
                                   std::filesystem::copy_options::overwrite_existing,
                                   file_error);
        if (file_error) {
            return false;
        }

        entry.note = internal::JoinNotes(
            entry.note,
            std::string(note_prefix) + "_single_tool_nonexact_preferred=" +
                internal::DescribeRunResult("single_tool", exit_code));
        return true;
    };
    const auto record_finalize_outcome =
        [&](const internal::NativeCandidateFinalizeResult& finalize_result) {
            entry.exact_parity_required =
                entry.exact_parity_required || finalize_result.exact_parity_required;
            if (finalize_result.kind == internal::NativeCandidateFinalizeKind::kExact) {
                entry.exact_parity_satisfied = true;
            }
        };
    const std::chrono::steady_clock::time_point repair_deadline =
        effective_options.source_wall_time_budget_ms == 0
            ? std::chrono::steady_clock::time_point{}
            : started + std::chrono::milliseconds(effective_options.source_wall_time_budget_ms);
    internal::SetCurrentDecompileRepairContext(
        prefer_exhaustive_rescue,
        outlier_candidate,
        previous_failed_file,
        unfinished_active,
        repair_deadline);
    const auto reset_repair_context = MakeScopeExit([]() {
        internal::ResetCurrentDecompileRepairContext();
    });
    const auto cleanup_partial_outputs = [&]() {
        std::error_code cleanup_error;
        std::filesystem::remove(cache_output, cleanup_error);
        std::filesystem::remove(disassembly_cache_output, cleanup_error);
        std::filesystem::remove(cache_metadata_path, cleanup_error);
    };
    const auto try_recover_cache_output = [&](const char* note_prefix) {
        std::error_code file_error;
        if (!std::filesystem::exists(cache_output, file_error) || file_error) {
            return false;
        }
        const auto size = std::filesystem::file_size(cache_output, file_error);
        if (file_error || size == 0 ||
            !internal::HasUsableDecompiledLuaOutput(cache_output, entry.note)) {
            return false;
        }

        const internal::NativeCandidateFinalizeResult existing_output_result =
            internal::ClassifyExistingNativeCandidateOutput(prepared.relative,
                                                           cache_output,
                                                           bytes,
                                                           &prepared.input_path,
                                                           &effective_options,
                                                           &entry.note,
                                                           note_prefix);
        record_finalize_outcome(existing_output_result);
        if (existing_output_result.kind == internal::NativeCandidateFinalizeKind::kExact) {
            entry.result = CacheEntry::Result::kDecompiled;
            entry.output_path = cache_output;
            return true;
        }
        if (existing_output_result.kind == internal::NativeCandidateFinalizeKind::kNonexact) {
            entry.result = CacheEntry::Result::kNonexact;
            entry.output_path = cache_output;
            return true;
        }
        return false;
    };
    const auto finish_wall_time_budget_exceeded = [&](std::string note) {
        cleanup_partial_outputs();
        result.kind = SourceProcessKind::kBudgetExceeded;
        result.note =
            internal::JoinNotes(std::move(note),
                                BuildSourceWallTimeBudgetExceededNote(effective_options));
        result.exact_parity_required = entry.exact_parity_required;
        result.exact_parity_satisfied = entry.exact_parity_satisfied;
        finish_result(&result);
        return result;
    };

    if (persistent_entry_valid) {
        if (persistent_entry.result == "decompiled" &&
            std::filesystem::exists(cache_output) &&
            std::filesystem::file_size(cache_output) > 0 &&
            internal::HasUsableDecompiledLuaOutput(cache_output,
                                                   persistent_entry.note)) {
            entry.result = CacheEntry::Result::kDecompiled;
            entry.output_path = cache_output;
            entry.note = internal::JoinNotes(persistent_entry.note, "cache_hit_decompiled");
            entry.exact_parity_required = exact_parity_required_by_policy;
            entry.exact_parity_satisfied = true;
        } else if (persistent_entry.result == "nonexact" &&
                   std::filesystem::exists(cache_output) &&
                   std::filesystem::file_size(cache_output) > 0 &&
                   internal::HasUsableDecompiledLuaOutput(cache_output,
                                                          persistent_entry.note)) {
            if (process_phase != SourceProcessPhase::kPhase1LightProbe &&
                effective_options.strict_exact_outputs && exact_parity_required_by_policy) {
                entry.note = internal::JoinNotes(
                    entry.note,
                    "cache_hit_nonexact_ignored_strict_exact_required");
            } else if (ShouldReprobeCachedNonexactNearExact(persistent_entry.note)) {
                entry.note = internal::JoinNotes(
                    persistent_entry.note,
                    "cache_hit_nonexact_reprobe_near_exact");
            } else {
                entry.result = CacheEntry::Result::kNonexact;
                entry.output_path = cache_output;
                entry.note = internal::JoinNotes(persistent_entry.note, "cache_hit_nonexact");
                entry.exact_parity_required = exact_parity_required_by_policy;
                entry.exact_parity_satisfied = false;
            }
        } else if (previous_failed_file &&
                   persistent_entry.result == "disassembled" &&
                   std::filesystem::exists(disassembly_cache_output) &&
                   std::filesystem::file_size(disassembly_cache_output) > 0) {
            if (bounded_native_attempt_for_hotspot ||
                runtime_policy.runtime_behavior_path ||
                IsScopedDisassemblyRecoveryReprobePath(prepared.relative.generic_string())) {
                entry.note = internal::JoinNotes(
                    persistent_entry.note,
                    bounded_native_attempt_for_hotspot
                        ? "cache_hit_disassembled_reprobe_bounded_hotspot"
                    : (runtime_policy.runtime_behavior_path
                           ? "cache_hit_disassembled_reprobe_behavior"
                           : (IsScopedGamefaceDisassemblyRecoveryPath(
                                  prepared.relative.generic_string())
                                  ? "cache_hit_disassembled_reprobe_gameface"
                                  : "cache_hit_disassembled_reprobe_blocker")));
            } else {
                entry.result = CacheEntry::Result::kDisassembled;
                entry.output_path = disassembly_cache_output;
                entry.note = internal::JoinNotes(persistent_entry.note, "cache_hit_disassembled");
            }
        }
    }

    if (entry.result == CacheEntry::Result::kFailed) {
        if (HasExceededSourceWallTimeBudget(effective_options, started)) {
            return finish_wall_time_budget_exceeded(entry.note);
        }
        internal::ResetTransientDecompileCaches();
        const std::string preferred_render_mode = persistent_entry.preferred_render_mode;

        std::error_code remove_error;
        std::filesystem::remove(cache_output, remove_error);
        std::filesystem::remove(disassembly_cache_output, remove_error);
        std::filesystem::remove(cache_metadata_path, remove_error);
        std::string native_candidate_source;
        Lua51Chunk direct_main_chunk;
        std::string direct_parse_error;
        const bool have_direct_main_chunk =
            ParseLua51Chunk(bytes, &direct_main_chunk, &direct_parse_error);
        const bool skip_native_pipeline_for_bounded_attempt_hotspot =
            bounded_native_attempt_for_runtime_hotspot &&
            is_behavior_path &&
            process_phase == SourceProcessPhase::kPhase1LightProbe;
        const bool skip_native_first_for_bounded_attempt_hotspot =
            bounded_native_attempt_for_hotspot;
        const bool defer_direct_main_probe_for_bounded_attempt_hotspot =
            bounded_native_attempt_for_runtime_hotspot &&
            runtime_policy.profile.Has(LuaScriptProfileFlag::kConstructorDeepTarget) &&
            !bounded_native_attempt_for_combatsequences;
        if (bounded_native_attempt_for_hotspot) {
            entry.note = internal::JoinNotes(
                entry.note,
                bounded_native_attempt_for_large_quest_hotspot
                    ? "workspace_native_pipeline_bounded_attempt_quest_hotspot"
                    : (bounded_native_attempt_for_combatsequences
                           ? "workspace_native_pipeline_bounded_attempt_combatsequences"
                           : (bounded_native_attempt_for_runtime_hotspot
                                   ? "workspace_native_pipeline_bounded_attempt_runtime_hotspot"
                                   : "workspace_native_pipeline_bounded_attempt_blocker")));
            if (skip_native_pipeline_for_bounded_attempt_hotspot) {
                entry.note = internal::JoinNotes(
                    entry.note,
                    "workspace_native_pipeline_bounded_attempt_phase1_deferred");
            }
        }
        const auto try_finalize_direct_main_body = [&](bool raw_render,
                                                       std::string_view note_prefix) {
            if (!have_direct_main_chunk) {
                return false;
            }

            ResetLuaNativeTableConstructorCounters();
            std::string direct_source;
            std::string render_error;
            const bool rendered_ok =
                raw_render
                    ? RenderLua51StraightLineFunctionBodyRaw(direct_main_chunk.main,
                                                             &direct_source,
                                                             &render_error)
                    : RenderLua51StraightLineFunctionBody(direct_main_chunk.main,
                                                          &direct_source,
                                                          &render_error);
            const std::size_t native_table_constructor_commits =
                ConsumeLuaNativeTableConstructorCommitCounter();
            const std::size_t native_setlist_constructor_links =
                ConsumeLuaNativeSetListConstructorLinkCounter();
            const std::size_t native_table_constructor_fallbacks =
                ConsumeLuaNativeTableConstructorFallbackCounter();
            entry.note = internal::JoinNotes(
                entry.note,
                "native_table_constructor_commits=" +
                    std::to_string(native_table_constructor_commits));
            entry.note = internal::JoinNotes(
                entry.note,
                "native_setlist_constructor_links=" +
                    std::to_string(native_setlist_constructor_links));
            entry.note = internal::JoinNotes(
                entry.note,
                "native_table_constructor_fallbacks=" +
                    std::to_string(native_table_constructor_fallbacks));
            if (!rendered_ok) {
                if (!render_error.empty()) {
                    entry.note = internal::JoinNotes(
                        entry.note,
                        std::string(note_prefix) + "_render_failed=" + render_error);
                }
                return false;
            }
            if (raw_render && bounded_native_attempt_for_hotspot &&
                TryUnwrapRawTopLevelVarargFunction(direct_source)) {
                entry.note = internal::JoinNotes(
                    entry.note,
                    std::string(note_prefix) + "_raw_top_level_wrapper_unwrapped");
            }
            if (allow_fast_nonexact_for_bounded_hotspot &&
                process_phase == SourceProcessPhase::kPhase1LightProbe) {
                const std::string validation_error =
                    internal::ValidateLuaSource(direct_source,
                                                prepared.relative.generic_string());
                if (validation_error.empty() &&
                    internal::WriteTextFile(cache_output, direct_source)) {
                    entry.result = CacheEntry::Result::kNonexact;
                    entry.exact_parity_satisfied = false;
                    entry.output_path = cache_output;
                    entry.note = internal::JoinNotes(
                        entry.note,
                        std::string(note_prefix) +
                            "_valid_syntax_fast_nonexact_bounded_hotspot");
                    return true;
                }
                if (!validation_error.empty()) {
                    entry.note = internal::JoinNotes(
                        entry.note,
                        std::string(note_prefix) +
                            "_fast_nonexact_invalid=" + validation_error);
                }
            }

            const std::string finalize_note_prefix(note_prefix);
            const internal::NativeCandidateFinalizeResult finalize_result =
                internal::TryFinalizeNativeCandidateOutput(prepared.relative,
                                                           std::move(direct_source),
                                                           bytes,
                                                           cache_output,
                                                           failed_output_path,
                                                           &effective_options,
                                                           &prepared.input_path,
                                                           &entry.note,
                                                           finalize_note_prefix.c_str());
            record_finalize_outcome(finalize_result);
            if (finalize_result.kind == internal::NativeCandidateFinalizeKind::kExact) {
                entry.result = CacheEntry::Result::kDecompiled;
                entry.exact_parity_satisfied = true;
                entry.output_path = cache_output;
                return true;
            }
            if (finalize_result.kind == internal::NativeCandidateFinalizeKind::kNonexact) {
                std::string finalized_source;
                refii::lua::audit::LuaBytecodeParityReference parity_reference;
                refii::lua::audit::LuaBytecodeParityResult parity_result;
                if (internal::ReadTextFile(cache_output, finalized_source) &&
                    refii::lua::audit::BuildLuaBytecodeParityReference(bytes,
                                                                       &parity_reference) &&
                    refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                        parity_reference,
                        finalized_source,
                        prepared.relative.generic_string(),
                        &parity_result) &&
                    parity_result.exact_bytecode) {
                    entry.result = CacheEntry::Result::kDecompiled;
                    entry.exact_parity_satisfied = true;
                    entry.output_path = cache_output;
                    entry.note = internal::JoinNotes(
                        entry.note,
                        std::string(note_prefix) +
                            "_validated_nonexact_promoted_exact_post_finalize");
                    return true;
                }
                entry.result = CacheEntry::Result::kNonexact;
                entry.exact_parity_satisfied = false;
                entry.output_path = cache_output;
                return true;
            }
            return false;
        };
        const auto try_promote_direct_nonexact_with_native_exact_rescue = [&]() {
            if (entry.result != CacheEntry::Result::kNonexact ||
                process_phase == SourceProcessPhase::kPhase1LightProbe ||
                defer_inprocess_native_candidate) {
                return false;
            }
            if (bounded_native_attempt_for_hotspot) {
                entry.note = internal::JoinNotes(
                    entry.note,
                    "workspace_direct_nonexact_native_exact_rescue_skipped_bounded_attempt_budget");
                return false;
            }

            const bool skip_native_exact_rescue =
                ShouldSkipNativeExactRescueForDirectNonexact(entry.note,
                                                             outlier_candidate,
                                                             bytes.size());
            const bool scoped_skip_override =
                skip_native_exact_rescue &&
                IsScopedDirectNonexactNativeExactRescueOverridePath(
                    prepared.relative.generic_string());
            if (skip_native_exact_rescue && !scoped_skip_override) {
                entry.note = internal::JoinNotes(
                    entry.note,
                    "workspace_direct_nonexact_native_exact_rescue_skipped_large_mismatch");
                return false;
            }
            if (scoped_skip_override) {
                entry.note = internal::JoinNotes(
                    entry.note,
                    "workspace_direct_nonexact_native_exact_rescue_skip_override_allowlisted");
            }

            constexpr std::size_t kNativeExactRescueMaxBytes = 256 * 1024;
            constexpr unsigned int kNativeExactRescueTimeoutSeconds = 10;
            if (bytes.size() > kNativeExactRescueMaxBytes) {
                return false;
            }

            LuaWorkspaceDecompileOptions rescue_options =
                BuildBudgetedDecompileOptions(effective_options, started);
            if (rescue_options.source_wall_time_budget_ms != 0 &&
                rescue_options.native_render_timeout_seconds == 0) {
                return false;
            }

            auto native_probe_deadline = repair_deadline;
            const auto capped_deadline =
                started + std::chrono::seconds(kNativeExactRescueTimeoutSeconds);
            if (native_probe_deadline == std::chrono::steady_clock::time_point{} ||
                capped_deadline < native_probe_deadline) {
                native_probe_deadline = capped_deadline;
            }
            if (rescue_options.native_render_timeout_seconds == 0 ||
                rescue_options.native_render_timeout_seconds >
                    kNativeExactRescueTimeoutSeconds) {
                rescue_options.native_render_timeout_seconds =
                    kNativeExactRescueTimeoutSeconds;
            }

            std::string native_rescue_source;
            internal::SetCurrentDecompileRepairContext(false,
                                                       outlier_candidate,
                                                       false,
                                                       false,
                                                       native_probe_deadline);
            const auto restore_workspace_repair_context = MakeScopeExit([&]() {
                internal::SetCurrentDecompileRepairContext(prefer_exhaustive_rescue,
                                                           outlier_candidate,
                                                           previous_failed_file,
                                                           unfinished_active,
                                                           repair_deadline);
            });
            if (!internal::TryReplaceWithValidatedNativeMainChunk(bytes,
                                                                  native_rescue_source,
                                                                  &entry.note,
                                                                  &rescue_options,
                                                                  &prepared.input_path,
                                                                  preferred_render_mode)) {
                return false;
            }

            if (HasExceededSourceWallTimeBudget(effective_options, started)) {
                return false;
            }

            std::filesystem::path native_probe_output = cache_output;
            native_probe_output += ".native_exact_probe";
            std::filesystem::path native_probe_failed_output = failed_output_path;
            native_probe_failed_output += ".native_exact_probe";

            std::error_code cleanup_error;
            std::filesystem::remove(native_probe_output, cleanup_error);
            std::filesystem::remove(native_probe_failed_output, cleanup_error);

            const internal::NativeCandidateFinalizeResult finalize_result =
                internal::TryFinalizeNativeCandidateOutput(prepared.relative,
                                                           std::move(native_rescue_source),
                                                           bytes,
                                                           native_probe_output,
                                                           native_probe_failed_output,
                                                           &rescue_options,
                                                           &prepared.input_path,
                                                           &entry.note,
                                                           "native_first_exact_rescue");
            record_finalize_outcome(finalize_result);
            if (finalize_result.kind !=
                internal::NativeCandidateFinalizeKind::kExact) {
                std::filesystem::remove(native_probe_output, cleanup_error);
                std::filesystem::remove(native_probe_failed_output, cleanup_error);
                return false;
            }

            cleanup_error.clear();
            std::filesystem::copy_file(native_probe_output,
                                       cache_output,
                                       std::filesystem::copy_options::overwrite_existing,
                                       cleanup_error);
            std::filesystem::remove(native_probe_output, cleanup_error);
            std::filesystem::remove(native_probe_failed_output, cleanup_error);
            if (cleanup_error) {
                return false;
            }

            entry.result = CacheEntry::Result::kDecompiled;
            entry.output_path = cache_output;
            entry.exact_parity_satisfied = true;
            entry.note = internal::JoinNotes(
                entry.note,
                "workspace_direct_nonexact_promoted_by_native_exact_rescue");
            return true;
        };
        const auto try_recover_failed_native_output_as_nonexact =
            [&](std::string_view note_prefix) {
                if (entry.result != CacheEntry::Result::kFailed ||
                    !std::filesystem::exists(failed_output_path) ||
                    std::filesystem::file_size(failed_output_path) == 0) {
                    return false;
                }

                std::string failed_output_source;
                if (!internal::ReadTextFile(failed_output_path, failed_output_source)) {
                    entry.note = internal::JoinNotes(
                        entry.note,
                        std::string(note_prefix) + "_read_failed");
                    return false;
                }

                std::error_code cleanup_error;
                std::filesystem::remove(cache_output, cleanup_error);

                const internal::NativeCandidateFinalizeResult failed_output_result =
                    [&]() {
                        const std::string note_prefix_text =
                            std::string(note_prefix) + "_validated";
                        return internal::TryFinalizeNativeCandidateOutput(
                            prepared.relative,
                            std::move(failed_output_source),
                            bytes,
                            cache_output,
                            failed_output_path,
                            &effective_options,
                            &prepared.input_path,
                            &entry.note,
                            note_prefix_text.c_str());
                    }();
                record_finalize_outcome(failed_output_result);
                if (failed_output_result.kind !=
                        internal::NativeCandidateFinalizeKind::kExact &&
                    failed_output_result.kind !=
                        internal::NativeCandidateFinalizeKind::kNonexact) {
                    std::filesystem::remove(cache_output, cleanup_error);
                    return false;
                }

                if (!std::filesystem::exists(cache_output) ||
                    std::filesystem::file_size(cache_output) == 0) {
                    entry.note = internal::JoinNotes(
                        entry.note,
                        std::string(note_prefix) + "_missing_output");
                    return false;
                }

                if (failed_output_result.kind ==
                    internal::NativeCandidateFinalizeKind::kExact) {
                    entry.result = CacheEntry::Result::kDecompiled;
                    entry.exact_parity_satisfied = true;
                    entry.note = internal::JoinNotes(
                        entry.note,
                        std::string(note_prefix) + "_recovered_exact");
                } else {
                    entry.result = CacheEntry::Result::kNonexact;
                    entry.exact_parity_satisfied = false;
                    entry.note = internal::JoinNotes(
                        entry.note,
                        std::string(note_prefix) + "_recovered_nonexact");
                }
                entry.output_path = cache_output;
                return true;
            };
        if (!have_direct_main_chunk && !direct_parse_error.empty()) {
            entry.note =
                internal::JoinNotes(entry.note,
                                    "workspace_direct_main_chunk_parse_failed=" +
                                        direct_parse_error);
        }
        if (entry.result == CacheEntry::Result::kFailed &&
            !defer_direct_main_probe_for_bounded_attempt_hotspot &&
            try_finalize_direct_main_body(false, "workspace_direct_main_chunk")) {
        } else if (entry.result == CacheEntry::Result::kFailed &&
                   !defer_direct_main_probe_for_bounded_attempt_hotspot &&
                   (!bounded_native_attempt_for_hotspot ||
                    (is_quest_path && bytes.size() <= 384 * 1024)) &&
                   try_finalize_direct_main_body(true, "workspace_direct_main_chunk_raw")) {
        } else if (entry.result == CacheEntry::Result::kFailed &&
                   bounded_native_attempt_for_hotspot) {
            entry.note = internal::JoinNotes(
                entry.note,
                "workspace_direct_main_chunk_raw_skipped_bounded_attempt_budget");
        }
        if (entry.result == CacheEntry::Result::kNonexact &&
            !defer_direct_main_probe_for_bounded_attempt_hotspot) {
            try_promote_direct_nonexact_with_native_exact_rescue();
        }
        if (entry.result == CacheEntry::Result::kFailed &&
            !skip_native_pipeline_for_bounded_attempt_hotspot &&
            !defer_inprocess_native_candidate &&
            !skip_native_first_for_bounded_attempt_hotspot) {
            LuaWorkspaceDecompileOptions budgeted_options =
                BuildBudgetedDecompileOptions(effective_options, started);
            if (budgeted_options.source_wall_time_budget_ms != 0 &&
                budgeted_options.native_render_timeout_seconds == 0) {
                return finish_wall_time_budget_exceeded(entry.note);
            }
            auto native_probe_deadline = repair_deadline;
            if (previous_failed_file || unfinished_active) {
                constexpr unsigned int kNativeFirstProbeTimeoutSeconds = 30;
                const auto capped_deadline =
                    started + std::chrono::seconds(kNativeFirstProbeTimeoutSeconds);
                if (native_probe_deadline == std::chrono::steady_clock::time_point{} ||
                    capped_deadline < native_probe_deadline) {
                    native_probe_deadline = capped_deadline;
                }
                if (budgeted_options.native_render_timeout_seconds == 0 ||
                    budgeted_options.native_render_timeout_seconds >
                        kNativeFirstProbeTimeoutSeconds) {
                    budgeted_options.native_render_timeout_seconds =
                        kNativeFirstProbeTimeoutSeconds;
                }
            }
            if (bounded_native_attempt_for_hotspot) {
                const auto capped_deadline =
                    started + std::chrono::seconds(bounded_native_attempt_timeout_seconds);
                if (native_probe_deadline == std::chrono::steady_clock::time_point{} ||
                    capped_deadline < native_probe_deadline) {
                    native_probe_deadline = capped_deadline;
                }
                if (budgeted_options.native_render_timeout_seconds == 0 ||
                    budgeted_options.native_render_timeout_seconds >
                        bounded_native_attempt_timeout_seconds) {
                    budgeted_options.native_render_timeout_seconds =
                        bounded_native_attempt_timeout_seconds;
                }
                entry.note = internal::JoinNotes(
                    entry.note,
                    "workspace_native_pipeline_bounded_attempt_timeout_seconds=" +
                        std::to_string(bounded_native_attempt_timeout_seconds));
                entry.note = internal::JoinNotes(
                    entry.note,
                    bounded_native_attempt_for_large_quest_hotspot
                        ? "workspace_native_pipeline_bounded_attempt_target=quest_hotspot"
                        : (bounded_native_attempt_for_combatsequences
                               ? "workspace_native_pipeline_bounded_attempt_target=combatsequences"
                               : (bounded_native_attempt_for_runtime_hotspot
                                      ? "workspace_native_pipeline_bounded_attempt_target=runtime_hotspot"
                                      : "workspace_native_pipeline_bounded_attempt_target=blocker")));
            }
            internal::SetCurrentDecompileRepairContext(
                false,
                outlier_candidate,
                false,
                false,
                native_probe_deadline);
            const auto restore_workspace_repair_context = MakeScopeExit([&]() {
                internal::SetCurrentDecompileRepairContext(prefer_exhaustive_rescue,
                                                           outlier_candidate,
                                                           previous_failed_file,
                                                           unfinished_active,
                                                           repair_deadline);
            });
            if (internal::TryReplaceWithValidatedNativeMainChunk(bytes,
                                                                 native_candidate_source,
                                                                 &entry.note,
                                                                 &budgeted_options,
                                                                 &prepared.input_path,
                                                                 preferred_render_mode)) {
                if (HasExceededSourceWallTimeBudget(effective_options, started)) {
                    return finish_wall_time_budget_exceeded(entry.note);
                }
                const internal::NativeCandidateFinalizeResult finalize_result =
                    internal::TryFinalizeNativeCandidateOutput(prepared.relative,
                                                               std::move(native_candidate_source),
                                                               bytes,
                                                               cache_output,
                                                               failed_output_path,
                                                               &budgeted_options,
                                                               &prepared.input_path,
                                                               &entry.note,
                                                               "native_first_main_chunk");
                record_finalize_outcome(finalize_result);
                if (finalize_result.kind ==
                    internal::NativeCandidateFinalizeKind::kExact) {
                    entry.result = CacheEntry::Result::kDecompiled;
                    entry.output_path = cache_output;
                } else if (finalize_result.kind ==
                           internal::NativeCandidateFinalizeKind::kNonexact) {
                    entry.result = CacheEntry::Result::kNonexact;
                    entry.output_path = cache_output;
                } else {
                    entry.note = internal::JoinNotes(entry.note, "native_first_candidate_rejected");
                    std::filesystem::remove(cache_output, remove_error);
                }
            }
        } else if (entry.result == CacheEntry::Result::kFailed) {
            entry.note = internal::JoinNotes(
                entry.note,
                skip_native_first_for_bounded_attempt_hotspot
                    ? "native_first_candidate_skipped_bounded_hotspot"
                    : "native_first_candidate_deferred_to_single_tool");
        }

        if (entry.result == CacheEntry::Result::kFailed &&
            !skip_native_pipeline_for_bounded_attempt_hotspot) {
            if (HasExceededSourceWallTimeBudget(effective_options, started)) {
                return finish_wall_time_budget_exceeded(entry.note);
            }
            LuaWorkspaceDecompileOptions budgeted_options =
                BuildBudgetedDecompileOptions(effective_options, started);
            unsigned int decompiler_timeout_seconds =
                ClampTimeoutSecondsToSourceWallTimeBudget(guarded_tool_timeout_seconds,
                                                          effective_options,
                                                          started);
            if (bounded_native_attempt_timeout_seconds != 0 &&
                (decompiler_timeout_seconds == std::numeric_limits<unsigned int>::max() ||
                 decompiler_timeout_seconds > bounded_native_attempt_timeout_seconds)) {
                decompiler_timeout_seconds = bounded_native_attempt_timeout_seconds;
                entry.note = internal::JoinNotes(
                    entry.note,
                    "workspace_decompiler_timeout_clamped_bounded_hotspot=" +
                        std::to_string(bounded_native_attempt_timeout_seconds));
            }
            if (budgeted_options.source_wall_time_budget_ms != 0 &&
                decompiler_timeout_seconds == 0) {
                return finish_wall_time_budget_exceeded(entry.note);
            }
            const FileProgressSnapshot cache_before =
                CaptureFileProgressSnapshot(cache_output);
            const FileProgressSnapshot output_before =
                CaptureFileProgressSnapshot(prepared.output_path);
            const int exit_code =
                internal::RunDecompiler(budgeted_options,
                                        prepared.input_path,
                                        cache_output,
                                        {},
                                        prefer_external_tool ||
                                            budgeted_options.force_external_single_tool,
                                        decompiler_timeout_seconds);
            const bool active_progress_advanced =
                HasActiveFileProgressAdvanced(
                    cache_before, cache_output, output_before, prepared.output_path);
            if (exit_code == 0 && std::filesystem::exists(cache_output) &&
                std::filesystem::file_size(cache_output) > 0) {
                if (internal::ValidateAndRepairDecompiledOutput(prepared.relative,
                                                               result.source_name,
                                                               bytes,
                                                               &budgeted_options,
                                                               &prepared.input_path,
                                                               cache_output,
                                                               failed_output_path,
                                                               &entry.note)) {
                    entry.result = CacheEntry::Result::kDecompiled;
                    entry.output_path = cache_output;
                    entry.exact_parity_satisfied = true;
                } else {
                    const internal::NativeCandidateFinalizeResult existing_output_result =
                        internal::ClassifyExistingNativeCandidateOutput(prepared.relative,
                                                                       cache_output,
                                                                       bytes,
                                                                       &prepared.input_path,
                                                                       &effective_options,
                                                                       &entry.note,
                                                                       "decompiler_output");
                    record_finalize_outcome(existing_output_result);
                    if (existing_output_result.kind ==
                        internal::NativeCandidateFinalizeKind::kExact) {
                        entry.result = CacheEntry::Result::kDecompiled;
                        entry.output_path = cache_output;
                    } else if (existing_output_result.kind ==
                               internal::NativeCandidateFinalizeKind::kNonexact) {
                        entry.result = CacheEntry::Result::kNonexact;
                        entry.output_path = cache_output;
                    } else {
                        std::filesystem::remove(cache_output, remove_error);
                    }
                }
            } else if (std::filesystem::exists(cache_output) &&
                       std::filesystem::file_size(cache_output) > 0 &&
                       internal::HasUsableDecompiledLuaOutput(cache_output, entry.note)) {
                const internal::NativeCandidateFinalizeResult existing_output_result =
                    internal::ClassifyExistingNativeCandidateOutput(prepared.relative,
                                                                   cache_output,
                                                                   bytes,
                                                                   &prepared.input_path,
                                                                   &effective_options,
                                                                   &entry.note,
                                                                   "decompiler_output");
                record_finalize_outcome(existing_output_result);
                if (existing_output_result.kind ==
                    internal::NativeCandidateFinalizeKind::kExact) {
                    entry.result = CacheEntry::Result::kDecompiled;
                    entry.output_path = cache_output;
                    if (exit_code != 0) {
                        entry.note =
                            internal::JoinNotes(entry.note, "decompiler_nonzero_output_retained");
                    }
                } else if (existing_output_result.kind ==
                           internal::NativeCandidateFinalizeKind::kNonexact) {
                    entry.result = CacheEntry::Result::kNonexact;
                    entry.output_path = cache_output;
                    if (exit_code != 0) {
                        entry.note =
                            internal::JoinNotes(entry.note, "decompiler_nonzero_output_retained");
                    }
                } else {
                    std::filesystem::remove(cache_output, remove_error);
                    const std::string run_result =
                        internal::DescribeRunResult("decompiler", exit_code);
                    entry.note = internal::JoinNotes(entry.note, run_result);
                    if (prefer_external_tool && !active_progress_advanced &&
                        run_result == "decompiler_timeout") {
                        entry.note = internal::JoinNotes(entry.note,
                                                         "active_file_no_progress_timeout");
                    }
                }
            } else {
                std::filesystem::remove(cache_output, remove_error);
                const std::string run_result =
                    internal::DescribeRunResult("decompiler", exit_code);
                entry.note = internal::JoinNotes(entry.note, run_result);
                if (prefer_external_tool && !active_progress_advanced &&
                    run_result == "decompiler_timeout") {
                    entry.note = internal::JoinNotes(entry.note,
                                                     "active_file_no_progress_timeout");
                }
            }
        }

        if (entry.result == CacheEntry::Result::kFailed) {
            try_recover_failed_native_output_as_nonexact(
                "workspace_native_failed_output_recovery");
        }

        if (entry.result == CacheEntry::Result::kFailed) {
            try_recover_cache_output("retained_cache_output");
        }

        if (entry.result == CacheEntry::Result::kNonexact) {
            try_prefer_single_tool_nonexact_output("workspace_cache");
        }

        const bool enforce_strict_exact_here =
            effective_options.strict_exact_outputs &&
            !(effective_options.pipeline_mode ==
                  LuaWorkspacePipelineMode::kTwoPhaseExactFirst &&
              process_phase == SourceProcessPhase::kPhase1LightProbe);
        if (entry.result == CacheEntry::Result::kNonexact &&
            entry.exact_parity_required &&
            !entry.exact_parity_satisfied &&
            enforce_strict_exact_here) {
            entry.note = internal::JoinNotes(entry.note, "exact_required_nonexact_holdout");
            entry.result = CacheEntry::Result::kFailed;
            entry.output_path.clear();
        }

        if (entry.result == CacheEntry::Result::kFailed &&
            effective_options.write_disassembly_fallback) {
            if (bounded_native_attempt_for_hotspot) {
                std::string fallback_reason = "unresolved";
                if (entry.note.find("deadline_exceeded") != std::string::npos) {
                    fallback_reason = "deadline";
                } else if (entry.note.find("_invalid=") != std::string::npos ||
                           entry.note.find("quality_rejected") != std::string::npos) {
                    fallback_reason = "validation";
                }
                entry.note = internal::JoinNotes(
                    entry.note,
                    "workspace_native_pipeline_bounded_attempt_fallback=" +
                        fallback_reason);
            }
            if (HasExceededSourceWallTimeBudget(effective_options, started)) {
                return finish_wall_time_budget_exceeded(entry.note);
            }
            LuaWorkspaceDecompileOptions budgeted_options =
                BuildBudgetedDecompileOptions(effective_options, started);
            unsigned int disassembly_timeout_seconds =
                ClampTimeoutSecondsToSourceWallTimeBudget(guarded_tool_timeout_seconds,
                                                          effective_options,
                                                          started);
            if (bounded_native_attempt_timeout_seconds != 0 &&
                (disassembly_timeout_seconds == std::numeric_limits<unsigned int>::max() ||
                 disassembly_timeout_seconds > bounded_native_attempt_timeout_seconds)) {
                disassembly_timeout_seconds = bounded_native_attempt_timeout_seconds;
                entry.note = internal::JoinNotes(
                    entry.note,
                    "workspace_disassembly_timeout_clamped_bounded_hotspot=" +
                        std::to_string(bounded_native_attempt_timeout_seconds));
            }
            if (budgeted_options.source_wall_time_budget_ms != 0 &&
                disassembly_timeout_seconds == 0) {
                return finish_wall_time_budget_exceeded(entry.note);
            }
            std::filesystem::create_directories(disassembly_cache_output.parent_path());
            const FileProgressSnapshot disassembly_before =
                CaptureFileProgressSnapshot(disassembly_cache_output);
            const FileProgressSnapshot output_before =
                CaptureFileProgressSnapshot(prepared.output_path);
            const int disassembly_exit_code =
                internal::RunDecompiler(budgeted_options,
                                        prepared.input_path,
                                        disassembly_cache_output,
                                        {"-dis"},
                                        prefer_external_tool ||
                                            budgeted_options.force_external_single_tool,
                                        disassembly_timeout_seconds);
            const bool active_progress_advanced =
                HasActiveFileProgressAdvanced(disassembly_before,
                                              disassembly_cache_output,
                                              output_before,
                                              prepared.output_path);
            if (disassembly_exit_code == 0 &&
                std::filesystem::exists(disassembly_cache_output) &&
                std::filesystem::file_size(disassembly_cache_output) > 0) {
                entry.result = CacheEntry::Result::kDisassembled;
                entry.output_path = disassembly_cache_output;
                entry.note = internal::JoinNotes(entry.note, "disassembly_fallback");
            } else {
                std::filesystem::remove(disassembly_cache_output, remove_error);
                const std::string run_result =
                    internal::DescribeRunResult("disassembler", disassembly_exit_code);
                entry.note = internal::JoinNotes(entry.note, run_result);
                if (prefer_external_tool && !active_progress_advanced &&
                    run_result == "disassembler_timeout") {
                    entry.note = internal::JoinNotes(entry.note,
                                                     "active_file_no_progress_timeout");
                }
            }
        }

        const bool cacheable_nonexact =
            entry.result == CacheEntry::Result::kNonexact &&
            !(effective_options.strict_exact_outputs &&
              entry.exact_parity_required &&
              !entry.exact_parity_satisfied);
        if (entry.result == CacheEntry::Result::kDecompiled ||
            cacheable_nonexact ||
            entry.result == CacheEntry::Result::kDisassembled) {
            PersistentCacheEntry updated_entry;
            updated_entry.version = persistent_cache_version;
            updated_entry.bytecode_hash = bytecode_hash;
            updated_entry.result = entry.result == CacheEntry::Result::kDecompiled
                                       ? "decompiled"
                                       : (cacheable_nonexact
                                              ? "nonexact"
                                              : "disassembled");
            updated_entry.note = entry.note;
            updated_entry.preferred_render_mode =
                DeterminePreferredRenderModeFromNote(entry.note);
            updated_entry.outlier_classification =
                DetermineOutlierClassification(bytes, entry.note);
            WritePersistentCacheEntry(cache_metadata_path, updated_entry);
        } else {
            std::error_code remove_error;
            std::filesystem::remove(cache_metadata_path, remove_error);
        }
    }

    result.note = entry.note;
    result.artifact_path = entry.output_path;
    result.exact_parity_required = entry.exact_parity_required;
    result.exact_parity_satisfied = entry.exact_parity_satisfied;
    switch (entry.result) {
    case CacheEntry::Result::kDecompiled:
        result.kind = SourceProcessKind::kDecompiled;
        break;
    case CacheEntry::Result::kNonexact:
        result.kind = SourceProcessKind::kNonexact;
        break;
    case CacheEntry::Result::kDisassembled:
        result.kind = SourceProcessKind::kDisassembled;
        break;
    case CacheEntry::Result::kFailed:
    default:
        result.kind = SourceProcessKind::kInvalid;
        break;
    }
    finish_result(&result);
    return result;
}

}  // namespace

bool RunLuaWorkspaceDecompilePipeline(const LuaWorkspaceDecompileOptions& raw_options,
                                      LuaWorkspaceDecompileSummary* summary,
                                      std::string* error) {
    try {
        LuaWorkspaceDecompileOptions options = raw_options;
        options.workspace_root =
            std::filesystem::absolute(GetDefaultLuaDecompileInputRoot(raw_options.workspace_root));
        if (options.output_root.empty()) {
            options.output_root = GetDefaultLuaDecompiledOutputRoot(options.workspace_root);
        } else {
            options.output_root = std::filesystem::absolute(options.output_root);
        }
        if (options.disassembly_output_root.empty()) {
            options.disassembly_output_root = options.output_root / "_disasm";
        } else {
            options.disassembly_output_root =
                std::filesystem::absolute(options.disassembly_output_root);
        }
        if (!options.single_tool_path.empty()) {
            options.single_tool_path = std::filesystem::absolute(options.single_tool_path);
        }
        if (options.progress_update_stride == 0) {
            options.progress_update_stride = 1;
        }
        if (options.worker_count > 0) {
            options.worker_count = std::max<std::size_t>(1, options.worker_count);
        }

        if (!std::filesystem::is_directory(options.workspace_root)) {
            if (error) {
                *error = "workspace not found: " + options.workspace_root.string();
            }
            return false;
        }

        const std::string ignored_output_prefix =
            internal::BuildIgnoredOutputPrefix(options.workspace_root, options.output_root);
        const std::string persistent_cache_version =
            BuildWorkspacePersistentCacheVersion(options);
        const std::filesystem::path final_manifest_path = options.output_root / "_manifest.tsv";
        const std::filesystem::path final_timing_report_path =
            options.output_root / "_timing.tsv";
        const std::filesystem::path live_manifest_path =
            options.output_root / std::string(kLiveManifestFileName);
        const std::filesystem::path live_timing_report_path =
            options.output_root / std::string(kLiveTimingFileName);
        const std::filesystem::path cache_root = options.output_root / ".cache";
        const std::filesystem::path manifest_row_root = cache_root / ".manifest_rows";
        const std::filesystem::path workspace_state_version_path =
            cache_root / std::string(kWorkspaceStateVersionFileName);
        const std::string previous_workspace_state_version =
            ReadWorkspaceStateVersion(workspace_state_version_path);
        const bool can_resume_previous_state =
            previous_workspace_state_version == persistent_cache_version;
        const ManifestStatusSnapshot previous_manifest_snapshot =
            can_resume_previous_state
                ? LoadAuthoritativeManifestStatuses(final_manifest_path,
                                                    live_manifest_path,
                                                    manifest_row_root)
                : ManifestStatusSnapshot{};

        std::vector<std::filesystem::path> relative_inputs;
        std::unordered_set<std::string> seen_relative_inputs;
        for (std::filesystem::recursive_directory_iterator it(options.workspace_root), end; it != end;
             ++it) {
            if (!it->is_regular_file() || it->path().extension() != ".lua") {
                continue;
            }

            const std::filesystem::path relative =
                std::filesystem::relative(it->path(), options.workspace_root);
            const std::filesystem::path canonical_relative =
                refii::lua::CanonicalizeRetailLuaRelativePath(relative);
            const std::string normalized =
                internal::NormalizePathString(canonical_relative.generic_string());
            if (internal::StartsWith(normalized, "decompiled/")) {
                continue;
            }
            if (internal::StartsWith(normalized, "_disasm/")) {
                continue;
            }
            if (internal::StartsWith(normalized, ".cache/")) {
                continue;
            }
            if (!ignored_output_prefix.empty() &&
                internal::StartsWith(normalized, ignored_output_prefix)) {
                continue;
            }
            if (!seen_relative_inputs.insert(normalized).second) {
                continue;
            }
            relative_inputs.push_back(canonical_relative);
        }

        std::sort(relative_inputs.begin(), relative_inputs.end(),
                  [](const std::filesystem::path& lhs, const std::filesystem::path& rhs) {
                      const int lhs_priority = internal::GetInputPriority(lhs);
                      const int rhs_priority = internal::GetInputPriority(rhs);
                      if (lhs_priority != rhs_priority) {
                          return lhs_priority < rhs_priority;
                      }
                      return internal::NormalizePathString(lhs.generic_string()) <
                             internal::NormalizePathString(rhs.generic_string());
                  });
        const bool inspect_output_state =
            !previous_manifest_snapshot.statuses.empty() ||
            WorkspaceHasMaterializedLuaOutputs(options.output_root);
        const ManifestInputSet unfinished_active_inputs = FindCanonicalInputsMissingFromManifest(
            relative_inputs,
            previous_manifest_snapshot,
            options.output_root,
            inspect_output_state);
        if (!unfinished_active_inputs.empty()) {
            std::stable_sort(
                relative_inputs.begin(),
                relative_inputs.end(),
                [&](const std::filesystem::path& lhs, const std::filesystem::path& rhs) {
                    const bool lhs_unfinished =
                        unfinished_active_inputs.find(
                            internal::NormalizePathString(lhs.generic_string())) !=
                        unfinished_active_inputs.end();
                    const bool rhs_unfinished =
                        unfinished_active_inputs.find(
                            internal::NormalizePathString(rhs.generic_string())) !=
                        unfinished_active_inputs.end();
                    if (lhs_unfinished != rhs_unfinished) {
                        return lhs_unfinished > rhs_unfinished;
                    }

                    const bool lhs_failed =
                        previous_manifest_snapshot.statuses.find(
                            internal::NormalizePathString(lhs.generic_string())) !=
                            previous_manifest_snapshot.statuses.end() &&
                        (previous_manifest_snapshot.statuses.at(
                             internal::NormalizePathString(lhs.generic_string())) ==
                             "disassembled" ||
                         previous_manifest_snapshot.statuses.at(
                             internal::NormalizePathString(lhs.generic_string())) == "invalid" ||
                         previous_manifest_snapshot.statuses.at(
                             internal::NormalizePathString(lhs.generic_string())) ==
                             "budget_exceeded");
                    const bool rhs_failed =
                        previous_manifest_snapshot.statuses.find(
                            internal::NormalizePathString(rhs.generic_string())) !=
                            previous_manifest_snapshot.statuses.end() &&
                        (previous_manifest_snapshot.statuses.at(
                             internal::NormalizePathString(rhs.generic_string())) ==
                             "disassembled" ||
                         previous_manifest_snapshot.statuses.at(
                             internal::NormalizePathString(rhs.generic_string())) == "invalid" ||
                         previous_manifest_snapshot.statuses.at(
                             internal::NormalizePathString(rhs.generic_string())) ==
                             "budget_exceeded");
                    if (lhs_failed != rhs_failed) {
                        return lhs_failed > rhs_failed;
                    }
                    return false;
                });
        }

        std::filesystem::create_directories(options.output_root);
        std::filesystem::create_directories(cache_root);
        if (!WriteWorkspaceStateVersion(workspace_state_version_path, persistent_cache_version)) {
            if (error != nullptr) {
                *error = "failed to write workspace state version: " +
                         workspace_state_version_path.string();
            }
            return false;
        }

        const std::filesystem::path failed_output_root = options.output_root / "_failed";

        LuaWorkspaceDecompileSummary local_summary;
        local_summary.workspace_root = options.workspace_root;
        local_summary.output_root = options.output_root;
        local_summary.manifest_path = final_manifest_path;
        local_summary.total_files = relative_inputs.size();

        auto emit_progress = [&](std::size_t completed,
                                 const std::filesystem::path& current_input,
                                 const char* phase,
                                 bool force) {
            if (!options.progress_callback) {
                return;
            }
            if (!force && options.progress_update_stride > 1 &&
                (completed % options.progress_update_stride) != 0) {
                return;
            }

            LuaWorkspaceDecompileProgress progress;
            progress.completed_files = completed;
            progress.total_files = local_summary.total_files;
            progress.current_input = current_input;
            progress.phase = phase;
            options.progress_callback(progress);
        };

        struct UniquePreparedJob {
            PreparedInput prepared;
            std::vector<std::size_t> input_indices;
        };

        std::vector<PreparedInput> prepared_inputs;
        prepared_inputs.reserve(relative_inputs.size());
        std::vector<UniquePreparedJob> unique_jobs;
        std::unordered_map<std::string, std::size_t> unique_job_by_cache_key;
        for (std::size_t i = 0; i < relative_inputs.size(); ++i) {
            PreparedInput prepared = PrepareInput(options, relative_inputs[i]);
            const std::size_t prepared_index = prepared_inputs.size();
            auto [it, inserted] =
                unique_job_by_cache_key.emplace(prepared.cache_key, unique_jobs.size());
            if (inserted) {
                UniquePreparedJob job;
                job.prepared = prepared;
                job.input_indices.push_back(prepared_index);
                unique_jobs.push_back(std::move(job));
            } else {
                unique_jobs[it->second].input_indices.push_back(prepared_index);
            }
            prepared_inputs.push_back(std::move(prepared));
        }

        std::vector<MaterializedResultKind> indexed_result_kinds(
            prepared_inputs.size(),
            MaterializedResultKind::kSkipped);
        std::vector<std::uint64_t> indexed_wall_time_ms(prepared_inputs.size(), 0);
        std::vector<std::string> indexed_source_names(prepared_inputs.size());
        std::vector<std::string> indexed_manifest_rows(prepared_inputs.size());
        std::vector<std::string> indexed_timing_rows(prepared_inputs.size());
        std::vector<bool> indexed_live_seeded(prepared_inputs.size(), false);
        std::mutex results_mutex;
        std::mutex progress_mutex;
        std::mutex live_report_mutex;
        std::atomic<std::size_t> completed_files{0};

        for (std::size_t index = 0; index < prepared_inputs.size(); ++index) {
            const std::string normalized =
                internal::NormalizePathString(prepared_inputs[index].relative.generic_string());
            const auto row_it = previous_manifest_snapshot.manifest_rows.find(normalized);
            if (row_it == previous_manifest_snapshot.manifest_rows.end()) {
                continue;
            }

            indexed_manifest_rows[index] = row_it->second;
            indexed_live_seeded[index] = true;

            MaterializedResultKind kind = MaterializedResultKind::kSkipped;
            std::string timing_row_text;
            std::string source_name;
            std::uint64_t wall_time_ms = 0;
            if (TryBuildTimingRowFromManifestRowText(row_it->second,
                                                     &timing_row_text,
                                                     &kind,
                                                     &source_name,
                                                     &wall_time_ms)) {
                indexed_result_kinds[index] = kind;
                indexed_wall_time_ms[index] = wall_time_ms;
                indexed_source_names[index] = std::move(source_name);
                indexed_timing_rows[index] = std::move(timing_row_text);
            }
        }

        const std::size_t seeded_completed_count =
            std::count(indexed_live_seeded.begin(), indexed_live_seeded.end(), true);
        completed_files.store(seeded_completed_count, std::memory_order_relaxed);

        auto rewrite_live_reports = [&]() {
            std::vector<std::string> manifest_rows_snapshot;
            std::vector<std::string> timing_rows_snapshot;
            {
                std::lock_guard<std::mutex> lock(results_mutex);
                manifest_rows_snapshot = indexed_manifest_rows;
                timing_rows_snapshot = indexed_timing_rows;
            }

            std::ofstream manifest(live_manifest_path, std::ios::binary | std::ios::trunc);
            manifest << kManifestHeader;
            for (const std::string& row_text : manifest_rows_snapshot) {
                if (!row_text.empty()) {
                    manifest << row_text;
                }
            }
            manifest.flush();

            std::ofstream timing_report_live(live_timing_report_path,
                                             std::ios::binary | std::ios::trunc);
            timing_report_live << kTimingHeader;
            for (const std::string& row_text : timing_rows_snapshot) {
                if (!row_text.empty()) {
                    timing_report_live << row_text;
                }
            }
            timing_report_live.flush();
        };

        {
            std::lock_guard<std::mutex> report_lock(live_report_mutex);
            rewrite_live_reports();
        }

        emit_progress(seeded_completed_count,
                      {},
                      seeded_completed_count > 0 ? "resuming" : "starting",
                      true);

        auto store_job_result = [&](const UniquePreparedJob& job,
                                    const SourceProcessResult& result,
                                    bool count_completion,
                                    const char* progress_phase) {
            std::vector<std::tuple<std::size_t, MaterializedResultKind, bool>> completed_inputs;
            completed_inputs.reserve(job.input_indices.size());
            for (const std::size_t input_index : job.input_indices) {
                const std::filesystem::path manifest_row_path =
                    manifest_row_root / (std::to_string(input_index) + ".tsv");
                MaterializedResultKind kind = MaterializedResultKind::kSkipped;
                std::string manifest_row_text;
                if (!MaterializePreparedResultToDisk(prepared_inputs[input_index],
                                                    result,
                                                    manifest_row_path,
                                                    &manifest_row_text,
                                                    &kind)) {
                    kind = MaterializedResultKind::kSkipped;
                    manifest_row_text = BuildManifestRowText(
                        "skipped",
                        prepared_inputs[input_index].relative,
                        {},
                        result.source_name,
                        internal::JoinNotes(result.note, "write_manifest_row_failed"));
                    std::filesystem::create_directories(manifest_row_path.parent_path());
                    internal::WriteTextFile(manifest_row_path, manifest_row_text);
                }

                {
                    std::lock_guard<std::mutex> lock(results_mutex);
                    const bool was_seeded = indexed_live_seeded[input_index];
                    indexed_result_kinds[input_index] = kind;
                    indexed_wall_time_ms[input_index] = result.wall_time_ms;
                    indexed_source_names[input_index] = result.source_name;
                    indexed_manifest_rows[input_index] = std::move(manifest_row_text);
                    if (input_index == job.input_indices.front()) {
                        indexed_timing_rows[input_index] =
                            BuildTimingRowText(result.wall_time_ms,
                                               kind,
                                               prepared_inputs[input_index].relative,
                                               result.source_name);
                    } else {
                        indexed_timing_rows[input_index].clear();
                    }
                    indexed_live_seeded[input_index] = false;
                    completed_inputs.emplace_back(input_index, kind, was_seeded);
                }

                {
                    std::lock_guard<std::mutex> report_lock(live_report_mutex);
                    rewrite_live_reports();
                }
            }

            for (const auto& [input_index, kind, was_seeded] : completed_inputs) {
                std::size_t completed = completed_files.load(std::memory_order_relaxed);
                if (count_completion && !was_seeded) {
                    completed = completed_files.fetch_add(1) + 1;
                }
                std::lock_guard<std::mutex> progress_lock(progress_mutex);
                emit_progress(completed,
                              prepared_inputs[input_index].relative,
                              progress_phase,
                              false);
            }
        };

        auto run_job_batch = [&](const std::vector<std::size_t>& job_indices,
                                 SourceProcessPhase process_phase,
                                 const char* progress_phase,
                                 bool count_completion,
                                 std::vector<SourceProcessResult>* unique_results) -> bool {
            if (job_indices.empty()) {
                return true;
            }

            {
                std::lock_guard<std::mutex> progress_lock(progress_mutex);
                emit_progress(completed_files.load(std::memory_order_relaxed),
                              {},
                              progress_phase,
                              true);
            }

            std::mutex worker_error_mutex;
            std::mutex active_inputs_mutex;
            std::exception_ptr worker_error;
            std::atomic<bool> worker_failed{false};
            std::atomic<bool> cancel_workers{false};
            std::atomic<std::size_t> next_batch_index{0};
            std::atomic<bool> stop_heartbeat{false};

            const std::size_t worker_count =
                ResolveLuaWorkspaceWorkerCount(options, job_indices.size());
            std::vector<std::filesystem::path> active_inputs(worker_count);

            auto worker = [&](std::size_t worker_slot) {
                try {
                    while (!cancel_workers.load(std::memory_order_relaxed)) {
                        const std::size_t batch_index =
                            next_batch_index.fetch_add(1, std::memory_order_relaxed);
                        if (batch_index >= job_indices.size()) {
                            break;
                        }
                        const std::size_t job_index = job_indices[batch_index];
                        const UniquePreparedJob& job = unique_jobs[job_index];
                        {
                            std::lock_guard<std::mutex> lock(active_inputs_mutex);
                            active_inputs[worker_slot] = job.prepared.relative;
                        }
                        {
                            std::lock_guard<std::mutex> lock(progress_mutex);
                            emit_progress(completed_files.load(std::memory_order_relaxed),
                                          job.prepared.relative,
                                          progress_phase,
                                          true);
                        }
                        SourceProcessResult result =
                            ProcessPreparedInput(options,
                                                 job.prepared,
                                                 persistent_cache_version,
                                                 cache_root,
                                                 failed_output_root,
                                                 &previous_manifest_snapshot.statuses,
                                                 &unfinished_active_inputs,
                                                 process_phase);
                        if (process_phase == SourceProcessPhase::kPhase2HoldoutRepair &&
                            unique_results != nullptr &&
                            job_index < unique_results->size()) {
                            const SourceProcessResult& phase1_result =
                                (*unique_results)[job_index];
                            if (IsUsableLuaProcessKind(phase1_result.kind) &&
                                IsTerminalFallbackProcessKind(result.kind)) {
                                SourceProcessResult preserved = phase1_result;
                                preserved.note = internal::JoinNotes(
                                    preserved.note,
                                    internal::JoinNotes(
                                        "phase2_holdout_preserved_phase1_lua",
                                        "phase2_holdout_repair_fallback=" + result.note));
                                result = std::move(preserved);
                            }
                        }
                        if (unique_results != nullptr) {
                            (*unique_results)[job_index] = result;
                        }
                        store_job_result(job, result, count_completion, progress_phase);
                        {
                            std::lock_guard<std::mutex> lock(active_inputs_mutex);
                            active_inputs[worker_slot].clear();
                        }
                    }
                } catch (...) {
                    {
                        std::lock_guard<std::mutex> lock(worker_error_mutex);
                        if (worker_error == nullptr) {
                            worker_error = std::current_exception();
                        }
                    }
                    worker_failed.store(true, std::memory_order_relaxed);
                    cancel_workers.store(true, std::memory_order_relaxed);
                }
            };

            std::vector<std::thread> workers;
            workers.reserve(worker_count);
            for (std::size_t index = 0; index < worker_count; ++index) {
                workers.emplace_back(worker, index);
            }

            std::thread heartbeat_thread([&]() {
                while (!stop_heartbeat.load(std::memory_order_relaxed) &&
                       !cancel_workers.load(std::memory_order_relaxed)) {
                    std::this_thread::sleep_for(std::chrono::seconds(5));
                    if (stop_heartbeat.load(std::memory_order_relaxed) ||
                        cancel_workers.load(std::memory_order_relaxed)) {
                        break;
                    }

                    std::filesystem::path active_input;
                    {
                        std::lock_guard<std::mutex> lock(active_inputs_mutex);
                        for (const std::filesystem::path& candidate : active_inputs) {
                            if (!candidate.empty()) {
                                active_input = candidate;
                                break;
                            }
                        }
                    }

                    if (!active_input.empty()) {
                        std::lock_guard<std::mutex> lock(progress_mutex);
                        emit_progress(completed_files.load(std::memory_order_relaxed),
                                      active_input,
                                      progress_phase,
                                      true);
                    }
                }
            });

            for (std::thread& worker_thread : workers) {
                if (worker_thread.joinable()) {
                    worker_thread.join();
                }
            }
            stop_heartbeat.store(true, std::memory_order_relaxed);
            if (heartbeat_thread.joinable()) {
                heartbeat_thread.join();
            }

            if (worker_failed.load(std::memory_order_relaxed)) {
                std::lock_guard<std::mutex> lock(worker_error_mutex);
                if (worker_error != nullptr) {
                    std::rethrow_exception(worker_error);
                }
                return false;
            }

            return true;
        };

        std::vector<SourceProcessResult> unique_job_results(unique_jobs.size());
        std::vector<std::size_t> all_job_indices;
        all_job_indices.reserve(unique_jobs.size());
        for (std::size_t index = 0; index < unique_jobs.size(); ++index) {
            all_job_indices.push_back(index);
        }

        const bool two_phase_exact_first =
            options.pipeline_mode == LuaWorkspacePipelineMode::kTwoPhaseExactFirst;
        if (!run_job_batch(all_job_indices,
                           two_phase_exact_first ? SourceProcessPhase::kPhase1LightProbe
                                                 : SourceProcessPhase::kLegacy,
                           two_phase_exact_first ? "phase1_processing" : "processing",
                           true,
                           &unique_job_results)) {
            if (error != nullptr) {
                *error = "phase1 workspace decompile failed";
            }
            return false;
        }

        if (two_phase_exact_first) {
            std::vector<std::size_t> holdout_job_indices;
            holdout_job_indices.reserve(unique_job_results.size());
            for (std::size_t job_index = 0; job_index < unique_job_results.size(); ++job_index) {
                const SourceProcessResult& phase1_result = unique_job_results[job_index];
                if (ShouldRunPhase2HoldoutRepairForResult(phase1_result)) {
                    holdout_job_indices.push_back(job_index);
                }
            }

            if (!holdout_job_indices.empty() &&
                !run_job_batch(holdout_job_indices,
                               SourceProcessPhase::kPhase2HoldoutRepair,
                               "phase2_holdout_repair",
                               false,
                               &unique_job_results)) {
                if (error != nullptr) {
                    *error = "phase2 holdout repair failed";
                }
                return false;
            }
        }

        local_summary.decompiled = 0;
        local_summary.aliased = 0;
        local_summary.nonexact = 0;
        local_summary.aliased_nonexact = 0;
        local_summary.disassembled = 0;
        local_summary.invalid = 0;
        local_summary.budget_exceeded = 0;
        local_summary.copied = 0;
        local_summary.skipped = 0;

        emit_progress(completed_files.load(std::memory_order_relaxed),
                      {},
                      "finalizing_manifest",
                      true);
        {
            std::ofstream final_manifest(final_manifest_path, std::ios::binary | std::ios::trunc);
            if (!final_manifest.is_open()) {
                if (error != nullptr) {
                    *error = "failed to open final manifest for writing: " +
                             final_manifest_path.string();
                }
                return false;
            }
            final_manifest << kManifestHeader;
            for (std::size_t index = 0; index < prepared_inputs.size(); ++index) {
                std::string manifest_row_text;
                {
                    std::lock_guard<std::mutex> lock(results_mutex);
                    manifest_row_text = indexed_manifest_rows[index];
                }
                if (manifest_row_text.empty()) {
                    const std::filesystem::path manifest_row_path =
                        manifest_row_root / (std::to_string(index) + ".tsv");
                    if (!internal::ReadTextFile(manifest_row_path, manifest_row_text)) {
                        manifest_row_text = BuildManifestRowText("skipped",
                                                                 prepared_inputs[index].relative,
                                                                 {},
                                                                 {},
                                                                 "missing_materialized_manifest_row");
                        indexed_result_kinds[index] = MaterializedResultKind::kSkipped;
                    }
                }
                final_manifest << manifest_row_text;
                if (((index + 1) % kManifestFlushStride) == 0) {
                    final_manifest.flush();
                }

                switch (indexed_result_kinds[index]) {
                case MaterializedResultKind::kCopied:
                    ++local_summary.copied;
                    break;
                case MaterializedResultKind::kAliased:
                    ++local_summary.aliased;
                    break;
                case MaterializedResultKind::kNonexact:
                    ++local_summary.nonexact;
                    break;
                case MaterializedResultKind::kAliasedNonexact:
                    ++local_summary.aliased_nonexact;
                    break;
                case MaterializedResultKind::kDecompiled:
                    ++local_summary.decompiled;
                    break;
                case MaterializedResultKind::kDisassembled:
                    ++local_summary.disassembled;
                    break;
                case MaterializedResultKind::kInvalid:
                    ++local_summary.invalid;
                    break;
                case MaterializedResultKind::kBudgetExceeded:
                    ++local_summary.budget_exceeded;
                    break;
                case MaterializedResultKind::kSkipped:
                default:
                    ++local_summary.skipped;
                    break;
                }

                const std::filesystem::path manifest_row_path =
                    manifest_row_root / (std::to_string(index) + ".tsv");
                std::error_code remove_error;
                std::filesystem::remove(manifest_row_path, remove_error);
            }
            final_manifest.flush();
            if (!final_manifest.good()) {
                if (error != nullptr) {
                    *error = "failed to write final manifest: " + final_manifest_path.string();
                }
                return false;
            }
        }

        emit_progress(completed_files.load(std::memory_order_relaxed),
                      {},
                      "writing_timing_report",
                      true);
        {
            std::ofstream timing_report(final_timing_report_path,
                                        std::ios::binary | std::ios::trunc);
            if (!timing_report.is_open()) {
                if (error != nullptr) {
                    *error = "failed to open timing report for writing: " +
                             final_timing_report_path.string();
                }
                return false;
            }
            timing_report << kTimingHeader;

            std::vector<std::size_t> timing_indices;
            timing_indices.reserve(prepared_inputs.size());
            std::unordered_map<std::string, std::size_t> seen_cache_keys;
            for (std::size_t index = 0; index < prepared_inputs.size(); ++index) {
                if (!seen_cache_keys.emplace(prepared_inputs[index].cache_key, index).second) {
                    continue;
                }
                timing_indices.push_back(index);
            }

            std::sort(timing_indices.begin(),
                      timing_indices.end(),
                      [&](std::size_t lhs, std::size_t rhs) {
                          if (indexed_wall_time_ms[lhs] != indexed_wall_time_ms[rhs]) {
                              return indexed_wall_time_ms[lhs] > indexed_wall_time_ms[rhs];
                          }
                          return internal::NormalizePathString(
                                     prepared_inputs[lhs].relative.generic_string()) <
                                 internal::NormalizePathString(
                                     prepared_inputs[rhs].relative.generic_string());
                      });

            for (const std::size_t index : timing_indices) {
                timing_report << indexed_wall_time_ms[index] << "\t"
                              << DescribeMaterializedResultKind(indexed_result_kinds[index]) << "\t"
                              << prepared_inputs[index].relative.generic_string() << "\t"
                              << indexed_source_names[index] << "\n";
            }
            timing_report.flush();
            if (!timing_report.good()) {
                if (error != nullptr) {
                    *error = "failed to write timing report: " +
                             final_timing_report_path.string();
                }
                return false;
            }
        }

        emit_progress(completed_files.load(std::memory_order_relaxed), {}, "done", true);

        if (summary != nullptr) {
            *summary = std::move(local_summary);
        }
        return true;
    } catch (const std::exception& exception) {
        if (error) {
            *error = exception.what();
        }
        return false;
    }
}

}  // namespace refii::lua::workspace

namespace refii::lua {

bool DecompileLuaWorkspace(const LuaWorkspaceDecompileOptions& options,
                           LuaWorkspaceDecompileSummary* summary,
                           std::string* error) {
    return workspace::RunLuaWorkspaceDecompilePipeline(options, summary, error);
}

}  // namespace refii::lua
